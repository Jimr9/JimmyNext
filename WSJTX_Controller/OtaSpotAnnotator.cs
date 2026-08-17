using System.Collections.Generic;

namespace WSJTX_Controller
{
    public class OtaSpotAnnotation
    {
        public bool WorkedBefore;
        public int NeededForAwardCount;
    }

    // Applies Jimmy's own existing operator intelligence (worked-before, active-award matching)
    // to a POTA/SOTA activator spot -- Nexus supplies the fact (who's on the air where), Jimmy
    // decides what it means to this operator. Deliberately does NOT touch Awards/RuleEngine or
    // AwardMatcher.Match: those stay exactly as they are for the live-decode path. This is a new,
    // additive, read-only consumer of the same already-public data (LogbookDb, LookupManager,
    // WsjtxClient.activeAwardTags) they already use -- not a change to the award engine's design,
    // and not a special case for POTA/SOTA baked into it.
    //
    // Mirrors AwardMatcher.Match's own per-GroupBy switch (Awards/AwardMatcher.cs) but counts
    // every matching active award instead of returning only the first -- "needed for N awards"
    // needs a count, and duplicating this small, pure switch here is far lower-risk than
    // changing Match's own return shape, which the live once-per-decode hot path depends on.
    public static class OtaSpotAnnotator
    {
        public static OtaSpotAnnotation Annotate(
            string call, string band,
            LogbookDb logbookDb, LookupManager lookupManager,
            Dictionary<string, WsjtxClient.ActiveAwardTag> activeAwardTags)
        {
            var result = new OtaSpotAnnotation();
            if (string.IsNullOrEmpty(call)) return result;

            result.WorkedBefore = logbookDb != null && logbookDb.HasWorkedBefore(call, band);

            LookupRecord rec = (lookupManager != null && lookupManager.Enabled) ? lookupManager.Build(call) : null;
            string state = rec?.State;
            string continent = rec?.Continent;

            int count = 0;
            if (activeAwardTags != null)
            {
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
                            match = !string.IsNullOrEmpty(state) && tag.Set.Contains(state);
                            break;
                        case RuleGroupBy.Continent:
                            match = !string.IsNullOrEmpty(continent) && tag.Set.Contains(continent);
                            break;
                        case RuleGroupBy.CqZone:
                            match = rec != null && rec.CqZone > 0 && tag.Set.Contains(rec.CqZone.ToString());
                            break;
                        case RuleGroupBy.Dxcc:
                            match = rec != null && rec.Dxcc > 0 && tag.Set.Contains(rec.Dxcc.ToString());
                            break;
                        default:
                            match = false;
                            break;
                    }
                    if (match) count++;
                }
            }
            result.NeededForAwardCount = count;
            return result;
        }
    }
}
