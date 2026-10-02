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

            ILogbookService db = null;
            try
            {
                db = LogbookFactory.Open();

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

        private async Task<bool> SyncQrzAsync(ILogbookService db)
        {
            _logbookWindowStatus("Auto-sync: fetching QRZ Logbook…");
            var (adif, error) = await Task.Run(() => NexusLogbookService.DownloadQrzLogbook(_qrzApiKey())).ConfigureAwait(true);
            if (adif == null)
            {
                _logbookWindowStatus("Auto-sync: QRZ error: " + (error ?? "Unknown error"));
                return false;
            }
            return ImportAndReport(db, adif, "QRZ", "LogbookLastQrzRefresh");
        }

        private async Task<bool> SyncLotwAsync(ILogbookService db)
        {
            _logbookWindowStatus("Auto-sync: fetching LoTW Logbook…");
            string user = _lotwUser();
            var (adif1, highWater, error) = await Task.Run(() =>
                NexusLogbookService.DownloadLotwConfirmations(user, _lotwPass(), full: false)).ConfigureAwait(true);
            if (adif1 == null)
            {
                _logbookWindowStatus("Auto-sync: LoTW error: " + (error ?? "Unknown error"));
                return false;
            }
            // Only the confirmations download is merged (as Nexus's own sync does): the own-records
            // download restates every confirmed contact a second time, and Nexus's merge put those
            // second copies on other contacts of the same day.
            bool ok = ImportAndReport(db, adif1, "LOTW", "LogbookLastLoTWRefresh");
            if (ok) NexusLogbookService.SaveLotwHighWater(user, highWater);
            string received = await ((NexusLogbookService)db).LotwReceivedStepAsync(_lotwUser(), _lotwPass()).ConfigureAwait(true);
            if (received != null) _logbookWindowStatus("Auto-sync: " + received);
            return ok;
        }

        private async Task<bool> SyncClubLogAsync(ILogbookService db)
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

        private bool ImportAndReport(ILogbookService db, string adifText, string source, string lastRefreshIniKey)
        {
            int logId = db.LogImportStart(source);
            var result = AdifImporter.Import(db, AdifParser.ParseWithOrder(adifText), source, null, _resolveUsState);
            db.LogImportFinish(logId, result.Processed, result.NewQsos, result.NewlyConfirmed, result.Corrected, result.Skipped, result.Errors);
            // Independent audit finding 3, 2026-08-23 (CONFIRMED bug): matches
            // LogbookWindow.RunImportFromText's own fix -- only write the checkpoint on a
            // genuinely clean import, so a source with any errors gets retried in full on the
            // next scheduled run instead of silently being marked "done" for another
            // RefreshDays-day period.
            bool clean = string.IsNullOrWhiteSpace(result.Errors);
            if (clean) _ini?.Write(lastRefreshIniKey, DateTime.UtcNow.ToString("o"));
            _logbookWindowStatus($"Auto-sync: {source} import complete: {result.NewQsos:N0} new, " +
                $"{result.NewlyConfirmed:N0} newly confirmed, {result.Corrected:N0} corrected, {result.Skipped:N0} unchanged{result.UnmatchedText}." +
                (clean ? "" : " (errors -- will retry this source in full next time)"));
            return clean;
        }
    }
}
