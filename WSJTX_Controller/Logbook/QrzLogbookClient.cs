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
    // Downloads QSO data from the QRZ Logbook API.
    // The API key is the logbook API key from qrz.com (same key used by logging programs
    // that sync to the QRZ online logbook).
    public class QrzLogbookClient
    {
        private static readonly HttpClient _http =
            new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        private const string ApiUrl = "https://logbook.qrz.com/api";

        public string LastError { get; private set; }
        // The last FETCH reply exactly as QRZ sent it (header + HTML-escaped ADIF; no API key).
        public string LastRawResponse { get; private set; }

        // Fetches logbook as ADIF text.
        // If since is not null, only records created after that date are returned.
        public async Task<string> FetchAdifAsync(string apiKey, DateTime? since = null)
        {
            LastError = null;
            if (TestModeGuard.IsTestMode)
            {
                LastError = "Blocked: JIMMY_TEST_DB_PATH is set (test mode) -- no real QRZ traffic allowed.";
                return null;
            }
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                LastError = "QRZ Logbook API key is not configured.";
                return null;
            }

            // QRZ OPTION uses comma-separated, colon-delimited pairs with no spaces.
            // The date filter key is MODSINCE (not SINCE) and takes a date-only value
            // (YYYY-MM-DD, no time-of-day). Without a date filter, the API returns the
            // full logbook.
            string option = since.HasValue
                ? "TYPE:ADIF,MODSINCE:" + since.Value.ToUniversalTime().ToString("yyyy-MM-dd")
                : "TYPE:ADIF";

            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("KEY",    apiKey.Trim()),
                new KeyValuePair<string, string>("ACTION", "FETCH"),
                new KeyValuePair<string, string>("OPTION", option),
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
                LogFailure("Timeout", LastError, ex.ToString());
                return null;
            }
            catch (HttpRequestException ex)
            {
                string category = ex.InnerException is SocketException ? "Network/DNS failure" : "HTTP request failure";
                LastError = $"{category} contacting QRZ Logbook API ({ApiUrl}): {ex.Message}";
                LogFailure(category, LastError, ex.ToString());
                return null;
            }
            catch (Exception ex)
            {
                LastError = $"Network error contacting QRZ Logbook API ({ApiUrl}): {ex.Message}";
                LogFailure("Network error", LastError, ex.ToString());
                return null;
            }

            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} from QRZ Logbook API ({ApiUrl}).";
                LogFailure("HTTP error", LastError, response);
                return null;
            }

            // The whole reply as received (no API key in it), for sync diagnosis while Nexus keeps
            // the log -- see NexusSyncDiagnostics.
            LastRawResponse = response;
            NexusSyncDiagnostics.Retain("qrz-fetch", response);

            // QRZ signals failure via RESULT=FAIL (general failure) or RESULT=AUTH
            // (bad/expired API key) -- any RESULT other than OK is a failure.
            // REASON is form-url-encoded (e.g. "Invalid+API+Key" or "Session%20Timeout"),
            // so decode it before display.
            int resultIdx = response.IndexOf("RESULT=", StringComparison.OrdinalIgnoreCase);
            string result = resultIdx >= 0 ? response.Substring(resultIdx + 7).Split('&')[0] : null;

            if (result != null && !result.Equals("OK", StringComparison.OrdinalIgnoreCase))
            {
                int ri = response.IndexOf("REASON=", StringComparison.OrdinalIgnoreCase);
                string reason = ri >= 0 ? WebUtility.UrlDecode(response.Substring(ri + 7).Split('&')[0]) : null;

                // QRZ quirk: a MODSINCE query that matches zero records comes back as
                // RESULT=FAIL&COUNT=0 with no REASON, instead of RESULT=OK&COUNT=0.
                // That shape means "nothing new," not an actual failure.
                int ci = response.IndexOf("COUNT=", StringComparison.OrdinalIgnoreCase);
                string count = ci >= 0 ? response.Substring(ci + 6).Split('&')[0] : null;
                if (string.IsNullOrWhiteSpace(reason) && count == "0")
                    return "";

                LastError = !string.IsNullOrWhiteSpace(reason)
                    ? $"QRZ API error ({result}): {reason}"
                    : $"QRZ API reported failure (RESULT={result}) but did not include a REASON.";
                LogFailure("QRZ API error", LastError, response);
                return null;
            }

            // QRZ returns the ADIF inside an ADIF= field, HTML-entity-encoded.
            // Example: COUNT=2003&RESULT=OK&ADIF=&lt;call:6&gt;KB0UZT&lt;EOR&gt;...
            int adifIdx = response.IndexOf("ADIF=", StringComparison.OrdinalIgnoreCase);
            if (adifIdx >= 0)
            {
                string encoded = response.Substring(adifIdx + 5);
                if (string.IsNullOrWhiteSpace(encoded))
                    return "";
                return WebUtility.HtmlDecode(encoded);
            }

            // If no ADIF= field but RESULT=OK, logbook is empty
            if (result != null && result.Equals("OK", StringComparison.OrdinalIgnoreCase))
                return "";

            // Fallback: whole response might be raw ADIF
            return response;
        }

        // Inserts one QSO into the QRZ Logbook (upload). Deliberately never sends
        // OPTION=REPLACE -- QRZ's own docs warn that flag can overwrite an already
        // -confirmed QSO with the newly submitted (unconfirmed) data, so a plain
        // INSERT is used; if QRZ already has this QSO, INSERT creates a second
        // (duplicate) record rather than risking a bad overwrite. Never calls
        // DELETE -- QRZ's own docs describe it as unrecoverable.
        public async Task<bool> InsertAsync(string apiKey, string adifRecord)
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
                new KeyValuePair<string, string>("ACTION", "INSERT"),
                new KeyValuePair<string, string>("ADIF",   adifRecord),
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
                LogFailure("Upload timeout", LastError, ex.ToString());
                return false;
            }
            catch (HttpRequestException ex)
            {
                string category = ex.InnerException is SocketException ? "Network/DNS failure" : "HTTP request failure";
                LastError = $"{category} contacting QRZ Logbook API ({ApiUrl}): {ex.Message}";
                LogFailure("Upload " + category, LastError, ex.ToString());
                return false;
            }
            catch (Exception ex)
            {
                LastError = $"Network error contacting QRZ Logbook API ({ApiUrl}): {ex.Message}";
                LogFailure("Upload network error", LastError, ex.ToString());
                return false;
            }

            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} from QRZ Logbook API ({ApiUrl}).";
                LogFailure("Upload HTTP error", LastError, response);
                return false;
            }

            int resultIdx = response.IndexOf("RESULT=", StringComparison.OrdinalIgnoreCase);
            string result = resultIdx >= 0 ? response.Substring(resultIdx + 7).Split('&')[0] : null;

            // Treat REPLACE as success defensively even though Jimmy never requests
            // it via OPTION=REPLACE -- it is not a failure state if QRZ ever returns it.
            if (result != null && (result.Equals("OK", StringComparison.OrdinalIgnoreCase) ||
                                    result.Equals("REPLACE", StringComparison.OrdinalIgnoreCase)))
                return true;

            int ri = response.IndexOf("REASON=", StringComparison.OrdinalIgnoreCase);
            string reason = ri >= 0 ? WebUtility.UrlDecode(response.Substring(ri + 7).Split('&')[0]) : null;

            // QRZ reports "already have this QSO" as RESULT=FAIL with a REASON
            // mentioning "duplicate", rather than a distinct result code (unlike
            // Club Log's clean "200 QSO Duplicate"). Treat it as handled, not
            // failed -- QRZ already has the QSO, nothing more for Jimmy to do.
            // Without this, a duplicate retries forever on every Alt+U catch-up,
            // burning minutes and blocking Club Log's catch-up from ever running
            // behind it (they share one sequential background task).
            if (IsDuplicateReason(reason))
                return true;

            LastError = !string.IsNullOrWhiteSpace(reason)
                ? $"QRZ API error ({result}): {reason}"
                : $"QRZ API reported failure (RESULT={result ?? "none"}).";
            LogFailure("Upload error", LastError, response);
            return false;
        }

        // Validates a Logbook API key with no side effects -- ACTION=STATUS just reports
        // on the logbook the key belongs to, unlike FETCH/INSERT it never returns or
        // submits any QSO data. Used by the Options dialog's "Test Login" button so a bad
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

        // Extracted for testability (JimmyTests has no InternalsVisibleTo, so
        // only public static members of a plain class are reachable from tests).
        public static bool IsDuplicateReason(string reason) =>
            !string.IsNullOrWhiteSpace(reason) &&
            reason.IndexOf("duplicate", StringComparison.OrdinalIgnoreCase) >= 0;

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
