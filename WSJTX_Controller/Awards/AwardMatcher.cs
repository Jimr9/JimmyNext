using System;
using System.Collections.Generic;

namespace WSJTX_Controller
{
    // Pure "does this decode match any actively-tracked award's still-needed set" logic,
    // extracted from WsjtxClient so it can be unit-tested directly -- JimmyTests references
    // Jimmy.exe as a compiled binary with no InternalsVisibleTo, so only public members of
    // plain classes are ever reachable from tests (same reasoning as CallQueueRanker.cs).
    //
    // CqZone/Dxcc resolution is injected as zero-arg delegates rather than calling a live
    // LookupManager directly, so tests can exercise every GroupBy kind without needing real
    // Club Log data -- and so the live caller only pays for a lookup when a currently-active
    // award's GroupBy actually needs it (this runs on the once-per-decode hot path).
    // State/Continent are passed in already-resolved (grid->state, decode->continent are both
    // cheap/already-available at the call site) rather than also being delegated.
    public static class AwardMatcher
    {
        // Returns the matched rule's Id, or null if none matched.
        public static string Match(
            Dictionary<string, WsjtxClient.ActiveAwardTag> activeAwardTags,
            string call, string state, string continent,
            Func<int> cqZoneLookup, Func<int> dxccLookup)
        {
            if (activeAwardTags == null || activeAwardTags.Count == 0) return null;
            if (string.IsNullOrEmpty(call)) return null;

            foreach (var tag in activeAwardTags.Values)
            {
                if (tag.Set.Count == 0) continue;
                bool match;
                switch (tag.GroupBy)
                {
                    case RuleGroupBy.Callsign:
                        match = tag.Set.Contains(call);
                        break;

                    case RuleGroupBy.State:
                        // UsGridStateMap.StateSetContains, not a plain Contains(state) -- state
                        // can be a compound border-straddling grid.dat value like "MN-WI", which
                        // must match if EITHER component state is in this award's still-needed
                        // set (see that method's own comment). Release-audit finding, 2026-08-20.
                        match = UsGridStateMap.StateSetContains(state, tag.Set);
                        break;

                    case RuleGroupBy.CqZone:
                    {
                        int zone = cqZoneLookup != null ? cqZoneLookup() : 0;
                        match = zone > 0 && tag.Set.Contains(zone.ToString());
                        break;
                    }

                    case RuleGroupBy.Continent:
                        match = !string.IsNullOrEmpty(continent) && tag.Set.Contains(continent);
                        break;

                    case RuleGroupBy.Dxcc:
                    {
                        int dxcc = dxccLookup != null ? dxccLookup() : 0;
                        match = dxcc > 0 && tag.Set.Contains(dxcc.ToString());
                        break;
                    }

                    default:
                        match = false;
                        break;
                }
                if (match) return tag.RuleId;
            }
            return null;
        }

        // The already-worked-per-band admission gate (WsjtxClient.AddSelectedCall) rejects a
        // decode unless one of these is true. Extracted as a pure boolean combination so the
        // exact exception logic has direct, exhaustive test coverage -- this is the crux of
        // "does an active award correctly override the already-worked block."
        public static bool ShouldRejectAlreadyWorked(
            bool isNewCallOnBand, bool isPota, bool isNewDxccCategory, bool isStillNeededByActiveAward)
            => !isNewCallOnBand && !isPota && !isNewDxccCategory && !isStillNeededByActiveAward;
    }
}
