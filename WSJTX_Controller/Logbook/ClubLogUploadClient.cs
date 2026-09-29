using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace WSJTX_Controller
{
    // Uploads to and downloads from a user's Club Log logbook. Requires the
    // user's own Club Log Application Password (Settings > App Passwords on
    // clublog.org) plus their email and callsign -- this is a different
    // credential than Jimmy's app-wide Club Log API key (ClubLogAppKey.cs),
    // which is only used for the read-only country-data download and is
    // unrelated to a specific user's logbook.
    public class ClubLogUploadClient
    {
        private static readonly HttpClient _http =
            new HttpClient { Timeout = TimeSpan.FromMinutes(2) };

        private const string PutlogsUrl  = "https://clublog.org/putlogs.php";
        private const string GetAdifUrl  = "https://clublog.org/getadif.php";

        public string LastError { get; private set; }

        // Downloads the user's own log back from Club Log as ADIF text. Uses the
        // same email/Application Password/callsign as upload -- no separate
        // credential and no app-wide API key needed for this endpoint. sinceYear,
        // if given, is Club Log's coarsest available filter (whole-year
        // granularity only; there is no day-level "since" filter like QRZ/LoTW).
        public async Task<string> FetchAdifAsync(string email, string password, string callsign, int? sinceYear = null)
        {
            LastError = null;
            if (TestModeGuard.IsTestMode)
            {
                LastError = "Blocked: JIMMY_TEST_DB_PATH is set (test mode) -- no real Club Log traffic allowed.";
                return null;
            }
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(callsign))
            {
                LastError = "Club Log upload email, Application Password, or callsign is not configured.";
                return null;
            }

            var fields = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("email", email),
                new KeyValuePair<string, string>("password", password),
                new KeyValuePair<string, string>("call", callsign),
            };
            if (sinceYear.HasValue)
                fields.Add(new KeyValuePair<string, string>("startyear", sinceYear.Value.ToString()));

            HttpResponseMessage resp;
            string response;
            try
            {
                resp = await _http.PostAsync(GetAdifUrl, new FormUrlEncodedContent(fields)).ConfigureAwait(false);
                response = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (TaskCanceledException ex)
            {
                LastError = $"Timeout waiting for Club Log ({GetAdifUrl}).";
                LogFailure("Download timeout", LastError, ex.ToString());
                return null;
            }
            catch (HttpRequestException ex)
            {
                string category = ex.InnerException is SocketException ? "Network/DNS failure" : "HTTP request failure";
                LastError = $"{category} contacting Club Log ({GetAdifUrl}): {ex.Message}";
                LogFailure("Download " + category, LastError, ex.ToString());
                return null;
            }
            catch (Exception ex)
            {
                LastError = $"Network error contacting Club Log ({GetAdifUrl}): {ex.Message}";
                LogFailure("Download network error", LastError, ex.ToString());
                return null;
            }

            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} from Club Log ({GetAdifUrl}).";
                LogFailure("Download HTTP error", LastError, response);
                return null;
            }

            return response ?? "";
        }

        // Uploads every pending QSO in one file, for the Alt+U batch/catch-up path
        // (and for anyone who leaves the real-time checkbox off; real-time sends go
        // through Nexus). Club Log's own guidance is that realtime.php must not be
        // used to serially upload a backlog -- putlogs.php (this endpoint) is the
        // one built for that.
        // apiKey is Jimmy's own app-wide Club Log key (ClubLogAppKey.cs); confirmed
        // by a live test that the same key already used for country-data download
        // is also accepted here.
        public async Task<bool> BatchUploadAsync(string email, string password, string callsign, string apiKey, string adifFileText)
        {
            LastError = null;
            if (TestModeGuard.IsTestMode)
            {
                LastError = "Blocked: JIMMY_TEST_DB_PATH is set (test mode) -- no real Club Log traffic allowed.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(callsign))
            {
                LastError = "Club Log upload email, Application Password, or callsign is not configured.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                LastError = "Club Log application key is not available in this build.";
                return false;
            }

            using (var content = new MultipartFormDataContent())
            {
                content.Add(new StringContent(apiKey), "api");
                content.Add(new StringContent(email), "email");
                content.Add(new StringContent(password), "password");
                content.Add(new StringContent(callsign), "callsign");
                var fileBytes = Encoding.UTF8.GetBytes(adifFileText);
                content.Add(new ByteArrayContent(fileBytes), "file", "jimmy_upload.adi");

                return await PostAndCheck(PutlogsUrl, content, "Batch upload").ConfigureAwait(false);
            }
        }

        private async Task<bool> PostAndCheck(string url, HttpContent content, string label)
        {
            HttpResponseMessage resp;
            string response;
            try
            {
                resp = await _http.PostAsync(url, content).ConfigureAwait(false);
                response = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (TaskCanceledException ex)
            {
                LastError = $"Timeout waiting for Club Log ({url}).";
                LogFailure(label + " timeout", LastError, ex.ToString());
                return false;
            }
            catch (HttpRequestException ex)
            {
                string category = ex.InnerException is SocketException ? "Network/DNS failure" : "HTTP request failure";
                LastError = $"{category} contacting Club Log ({url}): {ex.Message}";
                LogFailure(label + " " + category, LastError, ex.ToString());
                return false;
            }
            catch (Exception ex)
            {
                LastError = $"Network error contacting Club Log ({url}): {ex.Message}";
                LogFailure(label + " network error", LastError, ex.ToString());
                return false;
            }

            if (!resp.IsSuccessStatusCode)
            {
                // Club Log returns a bare 403 (no detail) for either bad credentials
                // or a bad/wrongly-scoped key -- surface the response body verbatim
                // since there is nothing more specific to parse out of it.
                LastError = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} from Club Log ({url}).";
                LogFailure(label + " HTTP error", LastError, response);
                return false;
            }

            return true;
        }

        private static void LogFailure(string category, string summary, string detail)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Assembly.GetExecutingAssembly().GetName().Name);
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "log_clublog_upload_errors.txt");

                string entry =
                    Environment.NewLine +
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Club Log upload failed [{category}]" + Environment.NewLine +
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
