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
    // Scope note (final, phase 2 completion pass): this covers every method the app's non-UI
    // consumers actually call -- classification's worked-before checks, the upload-catch-up
    // paths (QRZ/Club Log/HRDLog/LoTW-via-TQSL), auto-sync's import-log bookkeeping, and the
    // startup state-backfill repair -- so WsjtxClient.cs, OtaSpotsWindow.cs (via
    // OtaSpotAnnotator), WsjtxClient.Uploads.cs (+ TqslUploadClient), LogbookAutoSync.cs,
    // LiveQsoUploadOrchestrator.cs, and Controller.cs's BackfillMissingStates all hold this
    // interface type now, not LogbookDb concretely.
    //
    // ONE deliberate, identified exception remains: LogbookWindow.cs (the Logbook UI) stays on
    // LogbookDb concretely. It calls 21 distinct methods -- DxccProgress/WasProgress/WazProgress,
    // ConfirmedQsos/LotwConfirmedQsos/QrzConfirmedQsos/EqslConfirmedQsos, SearchQsos/
    // SearchByCallsign, GetRecentQsos, GetUploadSyncStatus, GetDxccCountryNames, and more --
    // Jimmy's own rich, multi-join, SQLite-optimized query/stats layer for the Logbook window
    // specifically. Forcing that whole surface into this interface now would mean designing a
    // second, much larger query contract under time pressure, not a real abstraction. This does
    // NOT obstruct a future Nexus-backed service: the accepted architecture already scopes
    // exactly this kind of rich local query/stats need to its own future surface (a rebuildable
    // local cache/query layer, the same shape already planned for the Awards engine's own
    // eventual Nexus-backed consumption) rather than this CRUD-focused contract -- LogbookWindow
    // would move to THAT surface when it exists, not to this one. RuleEngine.cs/AwardTagger.cs
    // are a second, pre-existing, even-lower-level example of the same kind of deliberately
    // out-of-scope dependency: they already bypass LogbookDb entirely and query a raw
    // SQLiteConnection directly for performance (their own established pattern, not something
    // this phase touches).
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

        // Import-log bookkeeping (LogbookAutoSync's own sync-status reporting).
        int LogImportStart(string source);
        void LogImportFinish(int logId, int total, int newCount, int newlyConfirmed, int corrected,
            int skipped, string errorText);
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
