using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace WSJTX_Controller
{
    // Downloads confirmed QSO records from LoTW using the lotwreport.adi endpoint.
    // Credentials are separate from the LoTW user-activity CSV provider.
    public class LoTWQsoClient
    {
        private static readonly HttpClient _http =
            new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        private const string ReportUrl = "https://lotw.arrl.org/lotwuser/lotwreport.adi";

        public string LastError { get; private set; }

        // Downloads LoTW QSOs as ADIF text.
        // If since is not null, only QSLs confirmed on or after that date are returned.
        // Set confirmedOnly = true to include only LoTW-confirmed QSOs (recommended).
        public async Task<string> FetchReportAsync(
            string username, string password,
            DateTime? since = null,
            bool confirmedOnly = true)
        {
            LastError = null;
            if (TestModeGuard.IsTestMode)
            {
                LastError = "Blocked: JIMMY_TEST_DB_PATH is set (test mode) -- no real LoTW traffic allowed.";
                return null;
            }
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                LastError = "LoTW username or password is not configured.";
                return null;
            }

            var ub = new UriBuilder(ReportUrl);
            string query =
                $"login={Uri.EscapeDataString(username.Trim())}" +
                $"&password={Uri.EscapeDataString(password.Trim())}" +
                "&qso_query=1" +
                "&qso_qsldetail=yes" +
                "&qso_mydetail=yes" +
                "&qso_withown=yes";
            // qso_qsldetail=yes (added 2026-08-20, root-caused live): a DIFFERENT parameter from
            // qso_detail below -- without it, LoTW's own export omits DXCC/CQZ/GRIDSQUARE/COUNTY
            // for every returned QSO, confirmed/not, even though qso_mydetail=yes was already
            // being sent (that one only supplies MY_* fields about the operator's own station,
            // not the confirmed contact's). Every QSO imported before this fix landed with
            // dxcc=0/cq_zone=0/grid='' regardless of LoTW confirmation status -- the QSL_RCVD
            // flag itself was never the problem, only the missing entity/zone/grid detail feeding
            // WAS/DXCC/WAZ progress and the logbook's own qso.dxcc column.
            //
            // qso_detail=yes intentionally omitted: it restricts results to QSOs that have
            // a station location with detail set up in LoTW, filtering out most QSOs.

            // LoTW applies a "system supplied default" date when none is given,
            // which snaps to the last QSL/upload time and returns only the most recent record.
            // Always supply an explicit date: the stored since-date for incremental runs,
            // or 1900-01-01 for a full history download.
            if (confirmedOnly)
            {
                string sinceDate = since.HasValue ? since.Value.ToString("yyyy-MM-dd") : "1900-01-01";
                query += "&qso_qsl=yes&qso_qslsince=" + Uri.EscapeDataString(sinceDate);
            }
            else
            {
                string sinceDate = since.HasValue ? since.Value.ToString("yyyy-MM-dd") : "1900-01-01";
                query += "&qso_qsl=no&qso_qsorxsince=" + Uri.EscapeDataString(sinceDate);
            }

            ub.Query = query;

            string response;
            try
            {
                response = await _http.GetStringAsync(ub.Uri).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LastError = "Network error: " + ex.Message;
                return null;
            }

            // LoTW returns an error page or a "Your query returned no records" page on failure
            if (response.IndexOf("invalid password", StringComparison.OrdinalIgnoreCase) >= 0 ||
                response.IndexOf("Login failed",     StringComparison.OrdinalIgnoreCase) >= 0)
            {
                LastError = "LoTW login failed. Check username and password.";
                return null;
            }

            if (response.IndexOf("lotw-user-activity", StringComparison.OrdinalIgnoreCase) >= 0 &&
                response.IndexOf("<EOR>", StringComparison.OrdinalIgnoreCase) < 0)
            {
                LastError = "LoTW returned unexpected response. Check credentials.";
                return null;
            }

            return response;
        }
    }
}
