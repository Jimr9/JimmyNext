using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSJTX_Controller
{
    // The support service on blindsea.com (2026-10-07): support settings for a recipient
    // callsign, the helpers' list of them, and support requests. Every request is a POST over
    // HTTPS with the shared app key in a header; helper requests add the helper's own username
    // and password in headers. Nothing secret ever goes in a URL, and nothing here is logged.
    // Redirects are not followed, so the headers can never be sent anywhere else.
    internal sealed class SupportService
    {
        internal const string Url = "https://blindsea.com/jimmy_next/service.php";
        // The server's own limit (config.php max_bytes, raised to 256 MB on 2026-10-08 for large
        // support reports; the host allows 5 minutes for an upload to arrive).
        internal const long MaxBytes = 256L * 1024 * 1024;
        internal const string MaxText = "256 MB";

        // The shared app key, built in from the private SupportService.key file (see Jimmy.csproj).
        // A casual-access barrier only -- not proof that the caller is Jimmy Next.
        private static readonly Lazy<string> _key = new Lazy<string>(() =>
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("SupportService.key"))
                {
                    if (s == null) return null;
                    string k = new StreamReader(s).ReadToEnd().Trim();
                    return k.Length >= 24 ? k : null;
                }
            }
            catch { return null; }
        });
        internal static string TestKey;   // tests only
        private static string Key => TestKey ?? _key.Value;
        internal static bool Available => Key != null;
        internal const string NotAvailable = "Support settings are not available in this copy of Jimmy Next.";

        // Tests hand in a fake server here.
        internal static Func<HttpMessageHandler> TestHandler;
        private static HttpClient _shared;
        private static HttpClient Client()
        {
            if (TestHandler != null) return new HttpClient(TestHandler(), true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            return _shared ?? (_shared = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan });
        }

        private static string UserAgent()
        {
            string v = "unknown";
            try { v = System.Diagnostics.FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).FileVersion; } catch { }
            return $"JimmyNext/{v} (Windows; +https://blindsea.com/jimmy20)";
        }

        // The helper's login, when a helper action is made.
        internal string HelperUser, HelperPassword;

        internal sealed class Result
        {
            public bool Ok;
            public int Status;
            public string Code;          // the server's own code: profile_exists, download_code_required, ...
            public string Error;         // a plain sentence, ready to show
            public JsonElement Json;
            public byte[] Bytes;         // a downloaded file
            public string FileName;
            public bool Cancelled;
            public string Get(string name) =>
                Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        // ── The actions ────────────────────────────────────────────────────────────────────

        internal Task<Result> ListProfiles(CancellationToken ct) => Post("list", null, null, true, false, ct);

        // A recipient's own download needs their code; a helper's needs only the login.
        internal Task<Result> DownloadProfile(string callsign, string code, bool asHelper, CancellationToken ct) =>
            Post("profile", Fields(("callsign", callsign), ("download_code", asHelper ? null : code)), null, asHelper, true, ct);

        // replaceRevision: null the first time; the revision from the server's 409 after the
        // helper says Yes to replacing it.
        internal Task<Result> UploadProfile(string callsign, byte[] zip, string replaceRevision, CancellationToken ct) =>
            Post("upload_profile", Fields(("callsign", callsign),
                    ("overwrite", replaceRevision != null ? "yes" : null), ("expected_revision", replaceRevision)),
                ("profile", callsign + ".zip", zip), true, false, ct);

        internal Task<Result> ProfileCode(string callsign, CancellationToken ct) =>
            Post("profile_code", Fields(("callsign", callsign)), null, true, false, ct);

        internal Task<Result> ResetProfileCode(string callsign, CancellationToken ct) =>
            Post("reset_profile_code", Fields(("callsign", callsign), ("confirm", "yes")), null, true, false, ct);

        internal Task<Result> DeleteProfile(string callsign, string revision, CancellationToken ct) =>
            Post("delete_profile", Fields(("callsign", callsign), ("confirm", "yes"), ("expected_revision", revision)), null, true, false, ct);

        internal Task<Result> ListSupport(CancellationToken ct) => Post("list_support", null, null, true, false, ct);

        internal Task<Result> DownloadSupport(string ticket, CancellationToken ct) =>
            Post("download_support", Fields(("ticket", ticket)), null, true, true, ct);

        internal Task<Result> DeleteSupport(string ticket, string revision, CancellationToken ct) =>
            Post("delete_support", Fields(("ticket", ticket), ("confirm", "yes"), ("expected_revision", revision)), null, true, false, ct);

        internal Task<Result> SubmitSupport(string callsign, string name, string email, string fileName, byte[] report, CancellationToken ct) =>
            Post("support", Fields(("callsign", callsign), ("name", name), ("email", email)), ("report", fileName, report), false, false, ct);

        private static List<(string, string)> Fields(params (string Name, string Value)[] f) =>
            f.Where(x => x.Value != null).Select(x => (x.Name, x.Value)).ToList();

        // ── One request ────────────────────────────────────────────────────────────────────

        private async Task<Result> Post(string action, List<(string Name, string Value)> fields,
            (string Field, string FileName, byte[] Bytes)? file, bool helper, bool expectFile, CancellationToken ct)
        {
            if (!Available) return new Result { Error = NotAvailable };
            if (helper && (string.IsNullOrWhiteSpace(HelperUser) || string.IsNullOrEmpty(HelperPassword)))
                return new Result { Error = "Enter your helper username and password first.", Code = "helper_login" };
            if (file != null && file.Value.Bytes.LongLength > MaxBytes)
                return new Result { Error = $"The file is {file.Value.Bytes.LongLength / 1048576.0:0.0} MB; the limit is {MaxText}." };

            var all = new List<(string Name, string Value)> { ("action", action) };
            if (fields != null) all.AddRange(fields);
            HttpContent content;
            if (file == null)
                content = new FormUrlEncodedContent(all.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)));
            else
                content = Multipart(all, file.Value.Field, file.Value.FileName, file.Value.Bytes);

            var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = content };
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent());
            request.Headers.Add("X-Jimmy-Key", Key);
            if (helper)
            {
                request.Headers.Add("X-Jimmy-Helper-User", HelperUser.Trim().ToUpperInvariant());
                request.Headers.Add("X-Jimmy-Helper", HelperPassword);
            }

            var client = Client();
            // Two minutes for a question to the server; ten for a file going up or coming down.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromMinutes(file != null || expectFile ? 10 : 2));
            try
            {
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false))
                {
                    var result = new Result { Status = (int)response.StatusCode };
                    string type = response.Content.Headers.ContentType?.MediaType ?? "";
                    byte[] bytes = await ReadBounded(response, limit.Token).ConfigureAwait(false);
                    if (bytes == null) return new Result { Status = result.Status, Error = "The reply was larger than " + MaxText + "." };

                    if (expectFile && response.StatusCode == HttpStatusCode.OK && !type.Contains("json"))
                    {
                        result.Ok = true;
                        result.Bytes = bytes;
                        result.FileName = SafeName(response.Content.Headers.ContentDisposition?.FileNameStar
                                                   ?? response.Content.Headers.ContentDisposition?.FileName);
                        return result;
                    }
                    try { result.Json = JsonDocument.Parse(bytes).RootElement.Clone(); }
                    catch
                    {
                        // A host page (firewall, server error), never shown or spoken as it is.
                        string title = HostPageTitle(bytes);
                        result.Error = $"The support server sent an unexpected reply (HTTP {result.Status}{(title == null ? "" : ", " + title)}). Try again later.";
                        return result;
                    }
                    result.Ok = result.Json.ValueKind == JsonValueKind.Object && result.Json.TryGetProperty("ok", out var ok)
                                && ok.ValueKind == JsonValueKind.True && result.Status == 200;
                    result.Code = result.Get("code");
                    if (!result.Ok)
                    {
                        // A 403 is either this copy's built-in key ("Access denied.") or the helper
                        // login ("...helper username and password required.") -- said apart.
                        string said = Sentence(result.Get("error") ?? result.Get("message"));
                        if (result.Status == 403 && result.Code == null && (said ?? "").IndexOf("helper", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            result.Code = "helper_login";
                            result.Error = "The helper username or password was not accepted.";
                        }
                        else if (result.Status == 403 && result.Code == null)
                            result.Error = "The support server did not accept this copy of Jimmy Next (" + (said ?? "access denied") +
                                           "). Its built-in support key is probably wrong.";
                        else
                            result.Error = said ?? $"The support server refused the request (HTTP {result.Status}).";
                    }
                    return result;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new Result { Cancelled = true, Error = "Cancelled." };
            }
            catch (OperationCanceledException)   // the time limit above (or HttpClient's own)
            {
                return new Result { Error = file != null
                    ? "The upload did not finish in time. A slow connection may need a smaller file, or try again later."
                    : "The support server did not answer in time. Check the internet connection and try again." };
            }
            catch (HttpRequestException ex)
            {
                return new Result { Error = "Could not reach the support server" + (ex.StatusCode == null ? ". Check the internet connection." : $" (HTTP {(int)ex.StatusCode})." ) };
            }
            finally
            {
                if (TestHandler != null) client.Dispose();
            }
        }

        // A file upload laid out exactly as a web browser sends one (2026-10-07: the host's
        // firewall refused .NET's own MultipartFormDataContent with HTTP 400 -- it leaves field
        // names unquoted and adds a filename* entry). Names and the file name are plain letters,
        // digits, '_' and '.', so nothing needs escaping.
        private static HttpContent Multipart(List<(string Name, string Value)> fields, string fileField, string fileName, byte[] bytes)
        {
            string boundary = "----JimmyNext" + Guid.NewGuid().ToString("N");
            var ms = new MemoryStream();
            void Text(string s) { var b = System.Text.Encoding.UTF8.GetBytes(s); ms.Write(b, 0, b.Length); }
            foreach (var f in fields)
                Text($"--{boundary}\r\nContent-Disposition: form-data; name=\"{f.Name}\"\r\n\r\n{f.Value}\r\n");
            Text($"--{boundary}\r\nContent-Disposition: form-data; name=\"{fileField}\"; filename=\"{fileName}\"\r\n" +
                 "Content-Type: application/octet-stream\r\n\r\n");
            ms.Write(bytes, 0, bytes.Length);
            Text($"\r\n--{boundary}--\r\n");
            var content = new ByteArrayContent(ms.ToArray());
            content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=" + boundary);
            return content;
        }

        // A host page's own title ("Not Acceptable"), as a few plain words -- never the page itself.
        private static string HostPageTitle(byte[] bytes)
        {
            try
            {
                string text = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
                var m = System.Text.RegularExpressions.Regex.Match(text, @"<title>\s*([^<]{1,80}?)\s*</title>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!m.Success) return null;
                string t = new string(m.Groups[1].Value.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '.').ToArray()).Trim();
                return t.Length == 0 ? null : t;
            }
            catch { return null; }
        }

        private static async Task<byte[]> ReadBounded(HttpResponseMessage response, CancellationToken ct)
        {
            if (response.Content.Headers.ContentLength > MaxBytes) return null;
            using (var s = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var ms = new MemoryStream())
            {
                var buffer = new byte[81920];
                int n;
                while ((n = await s.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    if (ms.Length + n > MaxBytes) return null;
                    ms.Write(buffer, 0, n);
                }
                return ms.ToArray();
            }
        }

        private static string Sentence(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return null;
            if (s.Length > 300) s = s.Substring(0, 300);
            return s;
        }

        // A file name from the server, used only as a suggestion: no folders, no odd characters.
        private static string SafeName(string name)
        {
            name = Path.GetFileName((name ?? "").Trim('"', ' '));
            return string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? null : name;
        }

        // ── Server lists ────────────────────────────────────────────────────────────────────

        internal sealed class ServerProfile { public string Callsign, Revision, ModifiedUtc; public long Bytes; }
        internal sealed class SupportRequest { public string Ticket, Callsign, Name, Email, SubmittedUtc, FileName, Revision; public long Bytes; }

        internal static List<ServerProfile> Profiles(Result r) =>
            Items(r, "profiles").Select(e => new ServerProfile
            {
                Callsign = Str(e, "callsign"), Revision = Str(e, "revision"), ModifiedUtc = Str(e, "modified_utc"), Bytes = Num(e, "bytes"),
            }).Where(p => !string.IsNullOrEmpty(p.Callsign)).ToList();

        internal static List<SupportRequest> Requests(Result r) =>
            Items(r, "requests").Select(e => new SupportRequest
            {
                Ticket = Str(e, "ticket"), Callsign = Str(e, "callsign"), Name = Str(e, "name"), Email = Str(e, "email"),
                SubmittedUtc = Str(e, "submitted_utc"), FileName = Str(e, "filename"), Revision = Str(e, "revision"), Bytes = Num(e, "bytes"),
            }).Where(q => !string.IsNullOrEmpty(q.Ticket)).ToList();

        private static IEnumerable<JsonElement> Items(Result r, string name) =>
            r != null && r.Ok && r.Json.ValueKind == JsonValueKind.Object && r.Json.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList()
                : new List<JsonElement>();

        private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
        private static long Num(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long x) ? x : 0;

        // ── Callsigns and codes ───────────────────────────────────────────────────────────────

        // A base callsign as the service wants it: letters and digits, at least one of each, no
        // slash part. "kd4dc/p" and "VE3/KD4DC" give KD4DC (the longest part with a digit).
        internal static string BaseCallsign(string call)
        {
            var parts = (call ?? "").Trim().ToUpperInvariant().Split('/')
                .Where(p => p.Length >= 2 && p.Length <= 16 && p.All(char.IsLetterOrDigit) && p.Any(char.IsDigit) && p.Any(char.IsLetter))
                .OrderByDescending(p => p.Length).ToList();
            return parts.Count > 0 && parts[0].All(c => c < 128) ? parts[0] : null;
        }

        // A download code as the server makes it: four groups of eight hex digits.
        internal static string NormalizeCode(string code)
        {
            string c = (code ?? "").Trim().ToUpperInvariant();
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\A[0-9A-F]{8}(-[0-9A-F]{8}){3}\z") ? c : null;
        }
    }
}
