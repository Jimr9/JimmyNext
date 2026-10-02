using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;

namespace WSJTX_Controller
{
    // Checks a QRZ Logbook API key (Options' Test Login). The logbook download itself is
    // Nexus's own (NexusLogbookService.DownloadQrzLogbook, 2026-10-02). The API key is the
    // logbook API key from qrz.com (same key used by logging programs that sync to QRZ).
    public class QrzLogbookClient
    {
        private static readonly HttpClient _http =
            new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        private const string ApiUrl = "https://logbook.qrz.com/api";

        public string LastError { get; private set; }

        // Validates a Logbook API key with no side effects -- ACTION=STATUS just reports
        // on the logbook the key belongs to, unlike FETCH it never returns any QSO
        // data. Used by the Options dialog's "Test Login" button so a bad
        // key is caught immediately instead of only surfacing as an upload/download error.
        public async Task<bool> TestApiKeyAsync(string apiKey)
        {
            LastError = null;
            if (TestModeGuard.IsTestMode)
            {
                LastError = "Blocked: JIMMY_TEST_DB_PATH is set (test mode) -- no real QRZ traffic allowed.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                LastError = "QRZ Logbook API key is not configured.";
                return false;
            }

            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("KEY",    apiKey.Trim()),
                new KeyValuePair<string, string>("ACTION", "STATUS"),
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
                LastError = $"Timeout waiting for QRZ Logbook API ({ApiUrl}).";
                LogFailure("Key test timeout", LastError, ex.ToString());
                return false;
            }
            catch (HttpRequestException ex)
            {
                string category = ex.InnerException is SocketException ? "Network/DNS failure" : "HTTP request failure";
                LastError = $"{category} contacting QRZ Logbook API ({ApiUrl}): {ex.Message}";
                LogFailure("Key test " + category, LastError, ex.ToString());
                return false;
            }
            catch (Exception ex)
            {
                LastError = $"Network error contacting QRZ Logbook API ({ApiUrl}): {ex.Message}";
                LogFailure("Key test network error", LastError, ex.ToString());
                return false;
            }

            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} from QRZ Logbook API ({ApiUrl}).";
                LogFailure("Key test HTTP error", LastError, response);
                return false;
            }

            int resultIdx = response.IndexOf("RESULT=", StringComparison.OrdinalIgnoreCase);
            string result = resultIdx >= 0 ? response.Substring(resultIdx + 7).Split('&')[0] : null;
            if (result != null && result.Equals("OK", StringComparison.OrdinalIgnoreCase))
                return true;

            int ri = response.IndexOf("REASON=", StringComparison.OrdinalIgnoreCase);
            string reason = ri >= 0 ? WebUtility.UrlDecode(response.Substring(ri + 7).Split('&')[0]) : null;
            LastError = !string.IsNullOrWhiteSpace(reason)
                ? $"QRZ API error ({result ?? "none"}): {reason}"
                : $"QRZ API reported an invalid Logbook API key (RESULT={result ?? "none"}).";
            LogFailure("Key test error", LastError, response);
            return false;
        }

        // Appends complete failure details (never the API key or session key) to a
        // dedicated log file, named to match the "log_*.txt" pattern so it is picked
        // up automatically by the support report ZIP. A separate file (rather than
        // the WSJT-X diagnostic log) is used deliberately: when "Diagnostic Log" is
        // enabled, WsjtxClient holds that file open for the whole session with
        // FileShare.Read, so a second writer -- even from the same process -- would
        // fail with a sharing violation.
        private static void LogFailure(string category, string summary, string detail)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Assembly.GetExecutingAssembly().GetName().Name);
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "log_qrz_errors.txt");

                string entry =
                    Environment.NewLine +
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} QRZ Logbook fetch failed [{category}]" + Environment.NewLine +
                    $"  {summary}" + Environment.NewLine +
                    "  Full response/detail:" + Environment.NewLine +
                    "  " + (detail ?? "").Replace("\n", "\n  ") + Environment.NewLine;

                File.AppendAllText(file, entry);
            }
            catch
            {
                // Logging must never break the download path.
            }
        }
    }
}
