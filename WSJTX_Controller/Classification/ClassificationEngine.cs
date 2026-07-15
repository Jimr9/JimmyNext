using System;

namespace WSJTX_Controller
{
    // Independently-derived counterpart to EnqueueDecodeMessage's wire-supplied
    // classification fields (IsNewCallOnBand, IsNewCallAnyBand, IsNewCountry,
    // IsNewCountryOnBand, Country, Continent). Field names/semantics intentionally
    // mirror EnqueueDecodeMessage exactly so ClassifiedCall and the wire-supplied
    // values can be diffed field-by-field in tests.
    public class ClassifiedCall
    {
        public bool IsNewCallOnBand { get; set; }
        public bool IsNewCallAnyBand { get; set; }
        public bool IsNewCountry { get; set; }
        public bool IsNewCountryOnBand { get; set; }
        public string Country { get; set; } = "";
        public string Continent { get; set; } = "";
    }

    // Migration Stage A1 (Jimmy_Master_Migration_Roadmap.md): computes what
    // EnqueueDecodeMessage's wire-supplied classification fields currently give
    // Jimmy, from Jimmy's own LogbookDb (worked-before) and LookupManager
    // (country/continent/DXCC entity) instead -- both of which already do this
    // exact work today for Awards (AwardTagger.IsHrcDxccUnconfirmed etc.), per
    // Phase 4 dependency-audit report Section 5.
    //
    // Parallel-validation only: nothing in WsjtxClient.CallQueue.cs,
    // WsjtxClient.Display.cs, CallQueueRanker.cs, or Awards/AwardTagger.cs reads
    // this class yet, and this stage does not change that. It exists to be
    // diffed against the wire-supplied fields in tests (see JimmyTests.cs's
    // ClassificationEngine tests) until Stage A6 proves parity and cuts the
    // Queue/Display path over.
    public class ClassificationEngine
    {
        private readonly LogbookDb _logbookDb;
        private readonly LookupManager _lookupManager;

        public ClassificationEngine(LogbookDb logbookDb, LookupManager lookupManager)
        {
            _logbookDb = logbookDb;
            _lookupManager = lookupManager;
        }

        // currentBand: Jimmy's own current-band string (WsjtxClient.CurrentBandStr,
        // e.g. "20m"), the same convention already used by the qso table's band
        // column (see AdifRecordBuilder.Build / ADIF import). Pass null/empty if
        // the current band isn't known -- IsNewCallOnBand/IsNewCountryOnBand then
        // default to true (unknown-band decodes are never treated as "not new"),
        // matching the conservative direction of the current wire-supplied fields.
        public ClassifiedCall Classify(string call, string currentBand)
        {
            var result = new ClassifiedCall();
            if (string.IsNullOrEmpty(call)) return result;

            LookupRecord rec = (_lookupManager != null && _lookupManager.Enabled)
                ? _lookupManager.Build(call)
                : null;
            result.Country = rec?.Country ?? "";
            result.Continent = rec?.Continent ?? "";

            bool workedAnyBand = _logbookDb != null && _logbookDb.HasWorkedBefore(call, null);
            result.IsNewCallAnyBand = !workedAnyBand;

            bool bandKnown = !string.IsNullOrEmpty(currentBand);
            bool workedThisBand = bandKnown && _logbookDb != null && _logbookDb.HasWorkedBefore(call, currentBand);
            result.IsNewCallOnBand = !bandKnown || !workedThisBand;

            int dxcc = rec?.Dxcc ?? 0;
            if (dxcc > 0 && _logbookDb != null)
            {
                bool countryWorkedAnyBand = _logbookDb.HasWorkedDxcc(dxcc, null);
                result.IsNewCountry = !countryWorkedAnyBand;

                bool countryWorkedThisBand = bandKnown && _logbookDb.HasWorkedDxcc(dxcc, currentBand);
                result.IsNewCountryOnBand = !bandKnown || !countryWorkedThisBand;
            }
            else
            {
                // DXCC entity unresolved (no lookup data yet): cannot classify as
                // new/not-new, so both default to false rather than a guess.
                result.IsNewCountry = false;
                result.IsNewCountryOnBand = false;
            }

            return result;
        }
    }
}
