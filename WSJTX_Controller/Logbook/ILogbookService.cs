using System;
using System.Collections.Generic;

namespace WSJTX_Controller
{
    // The logbook boundary. Nexus keeps the log (logbook migration, Phase 7 -- 2026-09-29):
    // NexusLogbookService is the one implementation of ILogbookService, and every write goes
    // through Nexus. Reads come from the READ COPY -- a Jimmy-format database rebuilt from
    // Nexus's rows (NexusMigration.Rebuild) -- through LogbookDb, which implements only
    // ILogbookReader. Code that only reads (classification, the OTA spot annotator, awards) takes
    // an ILogbookReader, so it works the same against the read copy or Nexus.
    //
    // The method shapes are deliberately ADIF-field-shaped: ADIF (plus AdifExtraFields' lossless
    // extra-tag bag) is this architecture's agreed common representation.
    //
    // RuleEngine.cs/AwardTagger.cs remain a deliberate, lower-level exception: they query the
    // read copy through a raw SQLiteConnection for performance (their own established pattern).
    public interface ILogbookReader : IDisposable
    {
        List<(string Tag, string Value)> GetExtraFields(long qsoId);
        long? GetIdByDedupKey(string dedupKey);

        // Every authoritative row for a contest session, in call order -- what
        // ContestWorkflow.RebuildScoreAndExport replays into Nexus via CONTEST_REBUILD_APPEND.
        List<ContestSessionRow> GetContestSessionRows(string contestSessionId);

        // Classification's per-decode "have I worked this before" hot-path queries
        // (ClassificationEngine, OtaSpotAnnotator).
        bool HasWorkedBefore(string callsign, string band = null);
        bool HasWorkedDxcc(int dxcc, string band = null);
        bool HasWorkedGrid(string grid, string band = null);

        string GetMeta(string key);

        // Per-service outbound upload state (QRZ/Club Log/HRDLog/LoTW-via-TQSL catch-up).
        List<LogbookDb.PendingUploadQso> GetPendingUploads(string service, int limit = 1000);

        // Logbook Center's stats/search surface.
        int TotalQsos(string source = null);
        int ConfirmedQsos(string source = null);
        int LotwConfirmedQsos();
        int QrzConfirmedQsos();
        int EqslConfirmedQsos();
        (int worked, int confirmed) WasProgress(string band = null);
        (int worked, int confirmed) DxccProgress(string band = null);
        (int worked, int confirmed) WazProgress(string band = null);
        List<QsoRecord> GetRecentQsos(int limit = 10);
        QsoRecord GetQso(int id);
        List<QsoRecord> SearchByCallsign(string pattern, int limit = 200);
        List<QsoRecord> SearchQsos(string callsignPattern, string source, string dateFrom, string dateTo, int limit = 500,
            string searchField = null, string searchText = null, int uploadFilter = -1);
        LogbookDb.UploadSyncStatus GetUploadSyncStatus(string service);
        Dictionary<int, string> GetDxccCountryNames();
        List<Dictionary<string, string>> GetAdifFieldDicts(IEnumerable<int> ids, IEnumerable<string> sources = null);
    }

    // Everything that changes the log -- Nexus only (NexusLogbookService).
    public interface ILogbookService : ILogbookReader
    {
        (bool isNew, bool newlyConfirmed, bool corrected) Upsert(
            string callsign, string band, string mode,
            string qsoDate, string timeOn, string timeOff,
            long freqHz, string rstSent, string rstRcvd,
            string state, string country, int dxcc, int cqZone,
            string grid, string name, string comment, string txPwr,
            string operatorCall, string stationCall, string myGrid,
            string lotwQslSent, string lotwQslRcvd,
            string qrzQslSent, string qrzQslRcvd,
            string source, string sourceQsoId, string dedupKey,
            string continent, int ituZone, string county, string iota,
            string sig, string sigInfo, string mySig, string mySigInfo,
            string darcDok, string wpxPrefix,
            string exchangeSent, string exchangeRcvd);

        bool UpdateQso(int id, string callsign, string band, string mode,
            string qsoDate, string timeOn, string timeOff, string state, string country,
            string grid, string name, string rstSent, string rstRcvd, string comment);

        int DeleteQsos(IEnumerable<int> ids);

        void SaveExtraFields(long qsoId, List<(string Tag, string Value)> extras);

        // contest_id/contest_session_id association -- ContestWorkflow's completed-QSO delivery.
        void SetContestAssociation(long qsoId, string contestId, string contestSessionId);

        // Startup state-backfill repair (Controller) and the key/value meta store it records
        // completion in -- also ContestWorkflow's per-session reconciliation watermark.
        int BackfillMissingStates(Func<string, string> resolveState);
        void SetMeta(string key, string value);

        void MarkUploaded(string dedupKey, string service, DateTime whenUtc);
        void MarkUploaded(IEnumerable<string> dedupKeys, string service, DateTime whenUtc);   // one read-copy rebuild

        // Import-log bookkeeping (LogbookAutoSync's sync-status reporting; Logbook Center's
        // Import History display).
        int LogImportStart(string source);
        void LogImportFinish(int logId, int total, int newCount, int newlyConfirmed, int corrected,
            int skipped, string errorText);
        List<ImportLogEntry> GetImportHistory(int limit = 25);
    }

    // WhenUnix is derived from the row's own qso_date/time_on (UTC) at query time.
    public class ContestSessionRow
    {
        public long Id { get; set; }
        public string Callsign { get; set; }
        public string Mode { get; set; }
        public long WhenUnix { get; set; }
    }
}
