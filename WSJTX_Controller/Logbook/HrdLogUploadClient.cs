using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;

namespace WSJTX_Controller
{
    // Uploads one QSO at a time to HRDLog.net's NewEntry.aspx robot.
    //
    // IMPORTANT: HRDLog.net is the online logging/awards site at hrdlog.net -- NOT the Ham
    // Radio Deluxe *Logbook* desktop app. It is a live-logging/awards site, not an ARRL
    // confirmation source: an upload here never earns DXCC/WAS credit (see
    // docs guidance mirrored from the open-source Nexus project's own hrdlog.rs, whose field
    // names/response format this class was verified against). Sits alongside QRZ/Club Log/LoTW
    // as one more real-time upload target, following QrzLogbookClient's exact shape --
    // no circuit breaker (that's a Club Log-specific requirement per its own documented
    // integration rules; HRDLog follows QRZ's no-breaker precedent), no bulk endpoint (single
    // record per call, so batch catch-up uses the same per-QSO-loop-with-delay shape as QRZ).
    public class HrdLogUploadClient
    {
        private static readonly HttpClient _http =
            new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        private const string ApiUrl = "https://robot.hrdlog.net/NewEntry.aspx";

        public string LastError { get; private set; }

        // callsign: station callsign the QSO is logged under. uploadCode: the HRDLog account's
        // upload code (the credential -- not the account password). adifRecord: exactly one
        // ADIF record ending in <eor>.
        public async Task<bool> InsertAsync(string callsign, string uploadCode, string adifRecord)
        {
            LastError = null;
            if (TestModeGuard.IsTestMode)
            {
                LastError = "Blocked: JIMMY_TEST_DB_PATH is set (test mode) -- no real HRDLog traffic allowed.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(callsign) || string.IsNullOrWhiteSpace(uploadCode))
            {
                LastError = "HRDLog.net callsign/upload code is not configured.";
                return false;
            }

            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("Callsign", callsign.Trim()),
                new KeyValuePair<string, string>("Code",     uploadCode.Trim()),
                new KeyValuePair<string, string>("App",      "Jimmy"),
                new KeyValuePair<string, string>("ADIFData", adifRecord ?? ""),
            });

            HttpResponseMessage resp;
            string response;
            try
            {
                resp = await _http.PostAsync(ApiUrl, form).ConfigureAwait(false);
                response = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (TaskCanceledException ex)
            {
                LastError = $"Timeout waiting for HRDLog.net ({ApiUrl}).";
                LogFailure("Upload timeout", LastError, ex.ToString());
                return false;
            }
            catch (HttpRequestException ex)
            {
                string category = ex.InnerException is SocketException ? "Network/DNS failure" : "HTTP request failure";
                LastError = $"{category} contacting HRDLog.net ({ApiUrl}): {ex.Message}";
                LogFailure("Upload " + category, LastError, ex.ToString());
                return false;
            }
            catch (Exception ex)
            {
                LastError = $"Network error contacting HRDLog.net ({ApiUrl}): {ex.Message}";
                LogFailure("Upload network error", LastError, ex.ToString());
                return false;
            }

            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} from HRDLog.net ({ApiUrl}).";
                LogFailure("Upload HTTP error", LastError, response);
                return false;
            }

            var (result, message) = ClassifyResponse(response);
            switch (result)
            {
                case HrdLogResult.Ok:
                    return true;
                // <insert>0 -- already on file. Benign, same "nothing more to do" handling
                // QrzLogbookClient gives its own duplicate case.
                case HrdLogResult.Duplicate:
                    return true;
                case HrdLogResult.AuthFail:
                    LastError = $"HRDLog.net authentication failed: {message ?? "unknown callsign or invalid upload code"}.";
                    LogFailure("Upload auth error", LastError, response);
                    return false;
                case HrdLogResult.Rejected:
                    LastError = $"HRDLog.net rejected the upload: {message ?? "no reason given"}.";
                    LogFailure("Upload rejected", LastError, response);
                    return false;
                default: // Unknown -- unrecognized body (HTML error page / truncated / server down)
                    LastError = "HRDLog.net returned an unrecognized response (possibly a transient server issue).";
                    LogFailure("Upload unknown response", LastError, response);
                    return false;
            }
        }

        // Public (not internal) for the same reason IsDuplicateReason on QrzLogbookClient is
        // public: JimmyTests has no InternalsVisibleTo, so only public static members of a
        // plain class are reachable from tests.
        public enum HrdLogResult { Ok, Duplicate, AuthFail, Rejected, Unknown }

        // Ported from the open-source Nexus project's crates/tempo-core/src/hrdlog.rs
        // classify_response()/between(), verified against HRDLog's real reply shape there
        // (including its unit tests' fixture bodies) -- kept here as a direct port rather than
        // re-derived from scratch, since HRDLog's actual XML response format isn't otherwise
        // documented anywhere in Jimmy's own codebase.
        public static (HrdLogResult Result, string Message) ClassifyResponse(string body)
        {
            string inserted = Between(body, "insert");
            if (inserted != null)
            {
                int.TryParse(inserted.Trim(), out int added);   // non-numeric -> 0 -> Duplicate, matches Nexus's unwrap_or(0)
                return (added > 0 ? HrdLogResult.Ok : HrdLogResult.Duplicate, null);
            }

            string err = Between(body, "error");
            if (err != null)
            {
                string lower = err.ToLowerInvariant();
                bool authFail = lower.Contains("unknown user") || lower.Contains("invalid token");
                return (authFail ? HrdLogResult.AuthFail : HrdLogResult.Rejected, err);
            }

            return (HrdLogResult.Unknown, null);
        }

        // Case-insensitive <tag>...</tag> substring extraction -- HRDLog's reply is a tiny
        // fixed XML shape, so this avoids pulling a full XML parser in just for this.
        private static string Between(string body, string tag)
        {
            if (string.IsNullOrEmpty(body)) return null;
            string lower = body.ToLowerInvariant();
            string open = "<" + tag + ">";
            string close = "</" + tag + ">";
            int start = lower.IndexOf(open, StringComparison.Ordinal);
            if (start < 0) return null;
            start += open.Length;
            int endRel = lower.IndexOf(close, start, StringComparison.Ordinal);
            if (endRel < 0) return null;
            return body.Substring(start, endRel - start).Trim();
        }

        // Same "log_<service>_errors.txt" convention as QrzLogbookClient/ClubLogUploadClient --
        // never the upload code itself, matching HrdLogQuery's own Debug-redaction discipline
        // in the Nexus source this was ported from.
        private static void LogFailure(string category, string summary, string detail)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Assembly.GetExecutingAssembly().GetName().Name);
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "log_hrdlog_errors.txt");

                string entry =
                    Environment.NewLine +
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} HRDLog.net upload failed [{category}]" + Environment.NewLine +
                    $"  {summary}" + Environment.NewLine +
                    "  Full response/detail:" + Environment.NewLine +
                    "  " + (detail ?? "").Replace("\n", "\n  ") + Environment.NewLine;

                File.AppendAllText(file, entry);
            }
            catch
            {
                // Logging must never break the upload path.
            }
        }
    }
}
