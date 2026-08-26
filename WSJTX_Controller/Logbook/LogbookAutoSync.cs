using System;
using System.Threading.Tasks;

namespace WSJTX_Controller
{
    // Automatic, scheduled logbook download for QRZ/LoTW/Club Log -- runs once per
    // session, a fixed delay after Jimmy reaches ACTIVE, so the user doesn't have to
    // remember to click the manual "Download from X" buttons on the Logbook window's
    // Sync tab. Deliberately reuses the exact same fetch/parse/import classes those
    // manual buttons already call (QrzLogbookClient/LoTWQsoClient/ClubLogUploadClient,
    // AdifParser/AdifImporter) so this automatically inherits their existing safety
    // behavior (TestModeGuard, Club Log's real-time circuit breaker doesn't apply here
    // since that's upload-only, but the same client classes are used either way).
    //
    // The manual buttons are entirely unaffected by any of this -- they always run
    // immediately regardless of the days setting, same as before.
    public class LogbookAutoSync
    {
        private readonly IniFile _ini;
        private readonly Func<string> _qrzApiKey;
        private readonly Func<string> _lotwUser;
        private readonly Func<string> _lotwPass;
        private readonly Func<string> _clubLogEmail;
        private readonly Func<string> _clubLogPassword;
        private readonly Func<string> _clubLogCallsign;
        private readonly Func<string, string> _resolveUsState;

        // Main status bar -- called at most twice per run: once before starting (only
        // if at least one service is actually due), once after every due service has
        // finished. Never receives per-service detail.
        private readonly Action<string> _mainStatus;
        // Logbook window's own status bar, if open -- gets the full per-service detail
        // (start + result for each service that runs), same wording the manual
        // buttons already produce. No-ops silently if the window isn't open.
        private readonly Action<string> _logbookWindowStatus;

        public bool QrzAutoSyncEnabled;
        public int  QrzRefreshDays = 7;
        public bool LotwAutoSyncEnabled;
        public int  LotwRefreshDays = 7;
        public bool ClubLogAutoSyncEnabled;
        public int  ClubLogRefreshDays = 7;

        public LogbookAutoSync(IniFile ini,
            Func<string> qrzApiKey, Func<string> lotwUser, Func<string> lotwPass,
            Func<string> clubLogEmail, Func<string> clubLogPassword, Func<string> clubLogCallsign,
            Func<string, string> resolveUsState,
            Action<string> mainStatus, Action<string> logbookWindowStatus)
        {
            _ini              = ini;
            _qrzApiKey        = qrzApiKey;
            _lotwUser         = lotwUser;
            _lotwPass         = lotwPass;
            _clubLogEmail     = clubLogEmail;
            _clubLogPassword  = clubLogPassword;
            _clubLogCallsign  = clubLogCallsign;
            _resolveUsState   = resolveUsState;
            _mainStatus       = mainStatus ?? (s => { });
            _logbookWindowStatus = logbookWindowStatus ?? (s => { });
        }

        private bool IsDue(string lastRefreshIniKey, int refreshDays)
        {
            string raw = _ini?.Read(lastRefreshIniKey);
            DateTime last;
            if (string.IsNullOrWhiteSpace(raw) || !DateTime.TryParse(raw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out last))
                return true; // never synced before -- due immediately
            return (DateTime.UtcNow - last).TotalDays >= refreshDays;
        }

        public async Task RunDueSyncsAsync()
        {
            bool qrzDue = QrzAutoSyncEnabled && !string.IsNullOrWhiteSpace(_qrzApiKey())
                && IsDue("LogbookLastQrzRefresh", QrzRefreshDays);
            bool lotwDue = LotwAutoSyncEnabled && !string.IsNullOrWhiteSpace(_lotwUser())
                && !string.IsNullOrWhiteSpace(_lotwPass()) && IsDue("LogbookLastLoTWRefresh", LotwRefreshDays);
            bool clubLogDue = ClubLogAutoSyncEnabled && !string.IsNullOrWhiteSpace(_clubLogEmail())
                && !string.IsNullOrWhiteSpace(_clubLogPassword()) && !string.IsNullOrWhiteSpace(_clubLogCallsign())
                && IsDue("LogbookLastClubLogRefresh", ClubLogRefreshDays);

            if (!qrzDue && !lotwDue && !clubLogDue) return;

            _mainStatus("Syncing logbooks in the background…");
            bool anyError = false;

            LogbookDb db = null;
            try
            {
                db = new LogbookDb();

                if (qrzDue) anyError |= !await SyncQrzAsync(db);
                if (lotwDue) anyError |= !await SyncLotwAsync(db);
                if (clubLogDue) anyError |= !await SyncClubLogAsync(db);
            }
            catch (Exception ex)
            {
                anyError = true;
                _logbookWindowStatus("Automatic logbook sync error: " + ex.Message);
            }
            finally
            {
                db?.Dispose();
            }

            _mainStatus(anyError
                ? "Logbook sync complete (1 or more errors — see Logbook window for details)."
                : "Logbook sync complete.");
        }

        private async Task<bool> SyncQrzAsync(LogbookDb db)
        {
            _logbookWindowStatus("Auto-sync: fetching QRZ Logbook…");
            var client = new QrzLogbookClient();
            string adif = await client.FetchAdifAsync(_qrzApiKey(), since: null).ConfigureAwait(true);
            if (adif == null)
            {
                _logbookWindowStatus("Auto-sync: QRZ error: " + (client.LastError ?? "Unknown error"));
                return false;
            }
            return ImportAndReport(db, adif, "QRZ", "LogbookLastQrzRefresh");
        }

        private async Task<bool> SyncLotwAsync(LogbookDb db)
        {
            _logbookWindowStatus("Auto-sync: fetching LoTW Logbook…");
            var client = new LoTWQsoClient();
            string adif1 = await client.FetchReportAsync(_lotwUser(), _lotwPass(), since: null, confirmedOnly: true).ConfigureAwait(true);
            if (adif1 == null)
            {
                _logbookWindowStatus("Auto-sync: LoTW error: " + (client.LastError ?? "Unknown error"));
                return false;
            }
            string adif2 = await client.FetchReportAsync(_lotwUser(), _lotwPass(), since: null, confirmedOnly: false).ConfigureAwait(true);
            // Independent audit finding 2, 2026-08-23 (CONFIRMED bug): matches
            // LogbookWindow.LoTWRefreshBtn_Click's own fix -- a failed unconfirmed-QSO fetch
            // must not be silently treated as a complete two-part success. Aborts before import
            // (same as the adif1==null case above) so the checkpoint is not advanced and the
            // next scheduled run retries the whole sync.
            if (adif2 == null)
            {
                _logbookWindowStatus("Auto-sync: LoTW error (unconfirmed QSOs): " + (client.LastError ?? "Unknown error"));
                return false;
            }
            return ImportAndReport(db, adif1 + "\r\n" + adif2, "LOTW", "LogbookLastLoTWRefresh");
        }

        private async Task<bool> SyncClubLogAsync(LogbookDb db)
        {
            _logbookWindowStatus("Auto-sync: fetching Club Log…");
            var client = new ClubLogUploadClient();
            string adif = await client.FetchAdifAsync(_clubLogEmail(), _clubLogPassword(), _clubLogCallsign(), sinceYear: null).ConfigureAwait(true);
            if (adif == null)
            {
                _logbookWindowStatus("Auto-sync: Club Log error: " + (client.LastError ?? "Unknown error"));
                return false;
            }
            return ImportAndReport(db, adif, "CLUBLOG", "LogbookLastClubLogRefresh");
        }

        private bool ImportAndReport(LogbookDb db, string adifText, string source, string lastRefreshIniKey)
        {
            int logId = db.LogImportStart(source);
            var result = AdifImporter.Import(db, AdifParser.Parse(adifText), source, null, _resolveUsState);
            db.LogImportFinish(logId, result.Processed, result.NewQsos, result.NewlyConfirmed, result.Corrected, result.Skipped, result.Errors);
            // Independent audit finding 3, 2026-08-23 (CONFIRMED bug): matches
            // LogbookWindow.RunImportFromText's own fix -- only write the checkpoint on a
            // genuinely clean import, so a source with any errors gets retried in full on the
            // next scheduled run instead of silently being marked "done" for another
            // RefreshDays-day period.
            bool clean = string.IsNullOrWhiteSpace(result.Errors);
            if (clean) _ini?.Write(lastRefreshIniKey, DateTime.UtcNow.ToString("o"));
            _logbookWindowStatus($"Auto-sync: {source} import complete: {result.NewQsos:N0} new, " +
                $"{result.NewlyConfirmed:N0} newly confirmed, {result.Corrected:N0} corrected, {result.Skipped:N0} unchanged." +
                (clean ? "" : " (errors -- will retry this source in full next time)"));
            return clean;
        }
    }
}
