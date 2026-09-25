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
    // Scope note: this is the operations the contest bridge and AdifImporter.Import need, not a
    // 1:1 mirror of every LogbookDb method -- LogbookWindow/LogbookAutoSync's own direct
    // award/query/stat calls, WsjtxClient.Uploads, OtaSpotsWindow, and Controller.cs still
    // reference LogbookDb's own broader surface directly today. Widening this interface (or
    // migrating those call sites' declared types) is real, deferred follow-up work, flagged
    // here rather than silently left undone.
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
    }
}
