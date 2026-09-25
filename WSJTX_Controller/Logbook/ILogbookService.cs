using System;
using System.Collections.Generic;

namespace WSJTX_Controller
{
    // Nexus contesting foundation, phase 2: the storage-neutral logbook boundary. LogbookDb IS
    // the (only, on this branch) implementation of this interface -- see its own class comment.
    // Jimmy's SQLite database is the only backend on this branch; nothing here changes that. The
    // point is that new code (the contest bridge, phase 3+) and AdifImporter.Import -- the
    // shared core every existing import path (live FT8/FT4 logging, QRZ/LoTW/Club Log/HRDLog
    // sync, manual Logbook import) already funnels through -- depend on THIS interface, never on
    // LogbookDb concretely, so a future Nexus-backed implementation of the same contract is
    // possible without redesigning any of them.
    //
    // The method shapes are deliberately ADIF-field-shaped, not redesigned into new DTOs --
    // ADIF (plus AdifExtraFields' lossless extra-tag bag) IS this architecture's agreed
    // storage-neutral common representation (see the logbook-ownership design), so this is the
    // natural contract shape, not leftover SQLite leakage.
    //
    // Scope note (final, boundary-completion pass): this now covers the FULL surface every
    // consumer in the app actually calls, including LogbookWindow.cs (the Logbook Center UI) --
    // its own stats/search/progress methods (DxccProgress/WasProgress/WazProgress,
    // ConfirmedQsos/LotwConfirmedQsos/QrzConfirmedQsos/EqslConfirmedQsos, SearchQsos/
    // SearchByCallsign, GetRecentQsos/GetQso, GetUploadSyncStatus, GetDxccCountryNames,
    // GetAdifFieldDicts, GetImportHistory, TotalQsos) are declared below alongside everything
    // else. LogbookDb remains the sole implementation (SQLite details -- SQL text, connections,
    // locking -- stay entirely inside it); every consumer, LogbookWindow.cs included, now holds
    // this interface type, never LogbookDb concretely. A future Nexus-backed implementation
    // satisfies the same contract without any consumer changing.
    //
    // RuleEngine.cs/AwardTagger.cs remain a deliberate, pre-existing, lower-level exception:
    // they already bypass LogbookDb entirely and query a raw SQLiteConnection directly for
    // performance (their own established pattern, predating this boundary, not touched here).
    public interface ILogbookService : IDisposable
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

        // Nexus contesting foundation, phase 2: lossless unknown-ADIF-field storage (schema v10).
        void SaveExtraFields(long qsoId, List<(string Tag, string Value)> extras);
        List<(string Tag, string Value)> GetExtraFields(long qsoId);
        long? GetIdByDedupKey(string dedupKey);

        // Nexus contesting foundation, phase 4: contest_id/contest_session_id association
        // (schema v10) -- used by ContestWorkflow's completed-QSO delivery.
        void SetContestAssociation(long qsoId, string contestId, string contestSessionId);

        // Nexus contesting foundation, phase 5: every one of Jimmy's own authoritative rows for
        // a contest session, in call order -- what ContestWorkflow.RebuildScoreAndExport replays
        // into Nexus via CONTEST_REBUILD_APPEND. A narrowly-scoped query (unlike LogbookWindow's
        // much wider stats/search surface, deliberately kept off this interface) because it is
        // exactly what the storage-neutral rebuild path needs, nothing more.
        List<ContestSessionRow> GetContestSessionRows(string contestSessionId);

        // Storage-neutral bulk-import primitive: runs perItemAction once per item, batching the
        // implementation's own underlying commits for performance. Deliberately does NOT expose
        // a transaction object, a commit/rollback method, or any other SQLite-specific
        // primitive -- a future Nexus-backed implementation batches however EngineHost's own
        // contract wants to (or doesn't batch at all) without this interface caring. Replaces the
        // earlier public BeginTransaction()/SQLiteTransaction pair AdifImporter.Import used to
        // manage directly.
        void RunBatch<T>(IEnumerable<T> items, Action<T> perItemAction);

        // Classification's per-decode "have I worked this before" hot-path queries
        // (ClassificationEngine, OtaSpotAnnotator). Read-only, simple, genuinely storage-neutral.
        bool HasWorkedBefore(string callsign, string band = null);
        bool HasWorkedDxcc(int dxcc, string band = null);

        // Startup state-backfill repair (Controller.BackfillMissingStates) and the generic
        // key/value meta store it records completion in. Also used by ContestWorkflow for its
        // own per-session reconciliation watermark (phase 4).
        int BackfillMissingStates(Func<string, string> resolveState);
        void SetMeta(string key, string value);
        string GetMeta(string key);

        // Per-service outbound upload tracking (QRZ/Club Log/HRDLog/LoTW-via-TQSL catch-up and
        // real-time upload paths). PendingUploadQso stays nested on LogbookDb rather than moved
        // to a free-standing type -- a plain data record, no SQLite-specific behavior, not a
        // functional coupling -- to keep this phase's diff to real behavior, not cosmetic type
        // relocation.
        List<LogbookDb.PendingUploadQso> GetPendingUploads(string service, int limit = 1000);
        void MarkUploaded(string dedupKey, string service, DateTime whenUtc);

        // Import-log bookkeeping (LogbookAutoSync's own sync-status reporting; also
        // LogbookWindow's own Import History display).
        int LogImportStart(string source);
        void LogImportFinish(int logId, int total, int newCount, int newlyConfirmed, int corrected,
            int skipped, string errorText);
        List<ImportLogEntry> GetImportHistory(int limit = 25);

        // Boundary-completion pass: LogbookWindow.cs's (Logbook Center UI) own stats/search
        // surface -- SQLite details (SQL text, joins, locking) stay entirely inside LogbookDb;
        // this interface only names the operations.
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
        List<QsoRecord> SearchQsos(string callsignPattern, string source, string dateFrom, string dateTo, int limit = 500);
        LogbookDb.UploadSyncStatus GetUploadSyncStatus(string service);
        Dictionary<int, string> GetDxccCountryNames();
        List<Dictionary<string, string>> GetAdifFieldDicts(IEnumerable<int> ids, IEnumerable<string> sources = null);

        // eQSL InBox reconciliation (EqslReconciler.Reconcile, called from LogbookWindow).
        LogbookDb.EqslReconcileOutcome TryMarkEqslConfirmed(string callsign, string band, string qsoDateAdif, string mode);
    }

    // Nexus contesting foundation, phase 5. WhenUnix is derived from the row's own
    // qso_date/time_on (UTC) at query time.
    public class ContestSessionRow
    {
        public long Id { get; set; }
        public string Callsign { get; set; }
        public string Mode { get; set; }
        public long WhenUnix { get; set; }
    }
}
