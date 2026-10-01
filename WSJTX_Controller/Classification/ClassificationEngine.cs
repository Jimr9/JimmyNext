using System;
using WsjtxUdpLib.Messages.Out;

namespace WSJTX_Controller
{
    // Independently-derived counterpart to EnqueueDecodeMessage's wire-supplied
    // classification fields (IsNewCallOnBand, IsNewCallAnyBand, IsNewCountry,
    // IsNewCountryOnBand, Country, Continent, IsDx, Azimuth, Distance). Field
    // names/semantics intentionally mirror EnqueueDecodeMessage exactly so
    // ClassifiedCall and the wire-supplied values can be diffed field-by-field in
    // tests.
    public class ClassifiedCall
    {
        public bool IsNewCallOnBand { get; set; }
        public bool IsNewCallAnyBand { get; set; }
        public bool IsNewCountry { get; set; }
        public bool IsNewCountryOnBand { get; set; }
        // New grid sounds (2026-09-29): the grid the station SENT (never a lookup's home grid)
        // not in the log -- false whenever that cannot be known. Sounds only; ranking and
        // automatic calling do not read these.
        public bool IsNewGrid { get; set; }
        public bool IsNewGridOnBand { get; set; }
        public string Country { get; set; } = "";
        public string Continent { get; set; } = "";
        public bool IsDx { get; set; }
        // -1 = unknown/not computed (no resolvable grid for this station), matching
        // the "d.Distance >= 0 && d.Azimuth >= 0" known-data gate already used at
        // WsjtxClient.Display.cs's BuildCallWaitingRow/ShowRawDecodes.
        public int Azimuth { get; set; } = -1;
        public int Distance { get; set; } = -1;
    }

    // Migration Stage A1 (Jimmy_Master_Migration_Roadmap.md): computes what
    // EnqueueDecodeMessage's wire-supplied classification fields currently give
    // Jimmy, from Jimmy's own LogbookDb (worked-before) and LookupManager
    // (country/continent/DXCC entity) instead -- both of which already do this
    // exact work today for Awards (AwardTagger.IsHrcDxccUnconfirmed etc.), per
    // Phase 4 dependency-audit report Section 5.
    //
    // Stage A6 extends this with IsDx/Azimuth/Distance (using GeoMath, Stage A2)
    // and wires ClassifiedCall into the queue/ranking/display/award consumers that
    // previously read EnqueueDecodeMessage's wire-supplied fields directly.
    public class ClassificationEngine
    {
        // Reads only: HasWorkedBefore/HasWorkedDxcc.
        private readonly ILogbookReader _logbookDb;
        private readonly LookupManager _lookupManager;

        public ClassificationEngine(ILogbookReader logbookDb, LookupManager lookupManager)
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
        //
        // decodedMessage/myGrid/myContinent are optional (default null) so existing
        // callers that only need the Stage A1 fields don't need to change -- IsDx/
        // Azimuth/Distance simply stay at their conservative defaults (false/-1/-1)
        // when omitted.
        //
        // Phase G-prep (2026-09-14): `canonicalGrid`, when given, is the caller's own
        // already-canonical Nexus-derived grid (SemanticDecode.Grid) and takes precedence over
        // re-parsing `decodedMessage` -- this DOES feed real production decisions (Distance/
        // Azimuth drive CallQueueRanker's DIST_DECR/DIST_INCR sort and beam ranking, not just
        // display), confirmed by tracing every consumer of ClassifiedCall.Distance/Azimuth.
        // The one production caller (ProcessDecodeMsg) now passes it; `decodedMessage` stays
        // the fallback so the 17 existing JimmyTests call sites -- which exercise this via raw
        // text and are tracked for their own Phase E migration -- need no change here.
        public ClassifiedCall Classify(string call, string currentBand, string decodedMessage = null, string myGrid = null, string myContinent = null, string canonicalGrid = null)
        {
            var result = new ClassifiedCall();
            if (string.IsNullOrEmpty(call)) return result;

            // /H-suffixed calls (Fox/Hound "Hound" designation -- WsjtxMessage.IsFoxHound,
            // the same heuristic Jimmy's own "Possible F/H" tagging already uses) have an
            // unreliable true operating location, so the wire-supplied classification
            // deliberately never resolves one for them (Continent/Country come back
            // empty, Azimuth/Distance stay unresolved, IsDx defaults true -- "might be
            // DX, don't filter it out"). Found via live A6 field testing 2026-07-16:
            // LookupManager.Build(call) doesn't share that caution -- QRZ/Club Log match
            // on the base callsign, ignoring the /H suffix, and confidently (but
            // possibly wrongly) resolve the base call's home location. Skipping the
            // lookup entirely for these calls mirrors the wire's own conservative
            // behavior instead of trusting a location that may not reflect where the
            // station is actually operating from.
            bool isPossibleFoxHound = !string.IsNullOrEmpty(decodedMessage) && WsjtxMessage.IsFoxHound(decodedMessage);

            // Country/Continent/Dxcc must resolve from Jimmy's own offline Club Log/Big
            // CTY data regardless of the "Use Lookup Data" master switch -- that switch
            // only gates the optional, account-backed providers (QRZ/LoTW/FccUls/HamQth).
            // When Enabled (useLookupData on, some provider actually configured), Build()
            // keeps its full existing merge order/behavior unchanged; otherwise fall back
            // to BuildOffline() so ClubLog still contributes instead of nothing at all.
            LookupRecord rec = null;
            if (!isPossibleFoxHound && _lookupManager != null)
                rec = _lookupManager.Enabled ? _lookupManager.Build(call) : _lookupManager.BuildOffline(call);
            // Found via live A6 field testing 2026-07-16: lookup providers return their
            // own raw country strings (QRZ: "United States", Club Log: "UNITED STATES OF
            // AMERICA"), not WSJT-X's normalized set -- the wire-supplied Country setter
            // always ran incoming values through this exact normalization
            // (EnqueueDecodeMessage.WsjtxCountry), so several consumers compare against
            // the normalized "USA" literal (US-state display substitution in
            // WsjtxClient.Display.cs, the auto-lookup trigger in CallQueueStore.cs).
            // Applying the same normalization here keeps those comparisons working
            // regardless of which provider's raw string resolved the country.
            result.Country = EnqueueDecodeMessage.WsjtxCountry(rec?.Country);
            result.Continent = rec?.Continent ?? "";

            // Logbook migration: while the Nexus logbook is still loading (no complete read copy
            // yet), worked-before is UNKNOWN -- never "new". An empty placeholder must not make
            // every station a new call / new country and drive ranking or automatic calling.
            // Only these four log-derived flags are affected; location facts below are not.
            bool logReady = NexusLogbook.LogReady;
            bool bandKnown = !string.IsNullOrEmpty(currentBand);
            int dxcc = rec?.Dxcc ?? 0;
            if (!logReady)
            {
                result.IsNewCallAnyBand = false;
                result.IsNewCallOnBand = false;
            }
            else
            {
                bool workedAnyBand = _logbookDb != null && _logbookDb.HasWorkedBefore(call, null);
                result.IsNewCallAnyBand = !workedAnyBand;

                bool workedThisBand = bandKnown && _logbookDb != null && _logbookDb.HasWorkedBefore(call, currentBand);
                result.IsNewCallOnBand = !bandKnown || !workedThisBand;
            }

            if (logReady && dxcc > 0 && _logbookDb != null)
            {
                bool countryWorkedAnyBand = _logbookDb.HasWorkedDxcc(dxcc, null);
                result.IsNewCountry = !countryWorkedAnyBand;

                bool countryWorkedThisBand = bandKnown && _logbookDb.HasWorkedDxcc(dxcc, currentBand);
                result.IsNewCountryOnBand = !bandKnown || !countryWorkedThisBand;
            }
            else
            {
                // DXCC entity unresolved (no lookup data yet), or the logbook still loading:
                // cannot classify as new/not-new, so both default to false rather than a guess.
                result.IsNewCountry = false;
                result.IsNewCountryOnBand = false;
            }

            // IsDx: "true = different continent from this QTH" (EnqueueDecodeMessage.
            // IsDx's own doc comment). Possible-F/H calls get the wire's own convention
            // (true -- "might be DX, don't filter it out") rather than the general
            // conservative-false default below, matching real wire behavior confirmed
            // via live A6 field testing 2026-07-16 (W5C/H: wire IsDx=true). Otherwise,
            // conservative default (false) when either continent is unresolved, matching
            // IsNewCountry's "cannot classify, don't guess" convention above -- not
            // computed anywhere else in Jimmy before this (confirmed: no prior
            // MyContinent-vs-Continent comparison existed in the codebase).
            result.IsDx = isPossibleFoxHound
                || (!string.IsNullOrEmpty(myContinent) && !string.IsNullOrEmpty(result.Continent)
                    && !string.Equals(myContinent, result.Continent, StringComparison.OrdinalIgnoreCase));

            // Azimuth/Distance: the decoded message's own grid (freshest, e.g. a CQ's
            // trailing grid) is tried first, falling back to LookupManager's cached
            // grid (QRZ only -- ClubLog/LoTW/FccUls never populate LookupRecord.Grid)
            // when the message itself doesn't carry one (73/RR73/report messages
            // never do). Same fallback order already established for US-state
            // resolution elsewhere (Awards/AwardTagger.cs, WsjtxClient.Display.cs).
            // Phase G-prep (2026-09-14): canonicalGrid (the caller's own Nexus-derived
            // SemanticDecode.Grid) wins when given -- production always provides it now. Only
            // a caller that omits it (today, every JimmyTests call site) still falls back to
            // parsing decodedMessage's raw text here.
            string theirGrid = !string.IsNullOrEmpty(canonicalGrid) ? canonicalGrid
                : !string.IsNullOrEmpty(decodedMessage) ? WsjtxMessage.Grid(decodedMessage) : null;
            if (logReady && _logbookDb != null && LogbookDb.Grid4(theirGrid) != null)
            {
                result.IsNewGrid = !_logbookDb.HasWorkedGrid(theirGrid, null);
                result.IsNewGridOnBand = bandKnown && !_logbookDb.HasWorkedGrid(theirGrid, currentBand);
            }
            if (string.IsNullOrEmpty(theirGrid)) theirGrid = rec?.Grid;
            if (!string.IsNullOrEmpty(myGrid) && !string.IsNullOrEmpty(theirGrid))
            {
                var da = GeoMath.DistanceAndAzimuth(myGrid, theirGrid);
                if (da.HasValue)
                {
                    result.Distance = (int)Math.Round(da.Value.distanceKm);
                    int az = (int)Math.Round(da.Value.azimuthDeg) % 360;
                    if (az < 0) az += 360;
                    result.Azimuth = az;
                }
            }

            return result;
        }
    }
}
