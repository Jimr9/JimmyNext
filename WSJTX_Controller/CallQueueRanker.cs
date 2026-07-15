using System;
using System.Collections.Generic;
using System.Linq;
using WsjtxUdpLib.Messages.Out;

namespace WSJTX_Controller
{
    // Pure call-queue ranking/ordering logic, extracted from WsjtxClient so it can be
    // unit-tested directly (JimmyTests references Jimmy.exe as a compiled binary with no
    // InternalsVisibleTo, so only public members of plain classes are ever reachable from
    // tests -- this is the only way to get automated coverage on ranking/tie-break logic).
    //
    // Deliberately scoped to ranking/ordering only. Category *derivation* (matching a decode
    // against HRC caches, Club Log lookups, active award tags, POTA/SOTA state) stays in
    // WsjtxClient -- it depends on live app state (Controller.activeAwardRuleIds, lookupManager
    // entity data, activeAwardTags) that doesn't belong in a decoupled ranking module. This
    // class assumes Category/Priority/Distance/Azimuth/Snr/SequenceNumber are already set on
    // the EnqueueDecodeMessage before Rank(...)/Compare(...) are called.
    //
    // CallCategory/RankMethods remain nested inside WsjtxClient (not moved here) because ~160
    // call sites across Controller.cs, OptionsDlg.cs, RankOrderDlg.cs, and
    // Messages/Out/DecodeMessage.cs already reference them as WsjtxClient.CallCategory /
    // WsjtxClient.RankMethods -- moving the enums would force a large, purely mechanical rename
    // with no decoupling benefit. Only the algorithms/weight tables move.
    public class CallQueueRanker
    {
        public const int NonDefaultTierBase = 100_000_000;
        public const int CategoryTierRange = 1_000_000;
        public const int OffBeamRank = -91;
        public static int BeamWidth = 90;
        private const int EarthDiameter = 40000;

        // Category tier levels (higher = ranked earlier in queue).
        // DEFAULT is always tier 0 (uses sort scores, not this table).
        public Dictionary<WsjtxClient.CallCategory, int> categoryWeight = new Dictionary<WsjtxClient.CallCategory, int>
        {
            { WsjtxClient.CallCategory.DEFAULT,             0 },
            { WsjtxClient.CallCategory.NEW_COUNTRY,         3 },
            { WsjtxClient.CallCategory.NEW_COUNTRY_ON_BAND, 4 },
            { WsjtxClient.CallCategory.TO_MYCALL,           5 },
            { WsjtxClient.CallCategory.MANUAL_SEL,          4 },
            { WsjtxClient.CallCategory.WANTED_CQ,           2 },
            { WsjtxClient.CallCategory.POTA,                0 },
            { WsjtxClient.CallCategory.SOTA,                0 },
            { WsjtxClient.CallCategory.ALWAYS_WANTED,       1 },
            { WsjtxClient.CallCategory.WAS_NEEDED,          0 },
            { WsjtxClient.CallCategory.DXCC_UNCONFIRMED,    0 },
            { WsjtxClient.CallCategory.ZONE_NEEDED,         0 },
            { WsjtxClient.CallCategory.STILL_NEEDED,        0 },
        };

        // Categories that Alt+N is allowed to call. DEFAULT is never callable by Alt+N.
        public List<WsjtxClient.CallCategory> callingEnabled = new List<WsjtxClient.CallCategory>
        {
            WsjtxClient.CallCategory.TO_MYCALL,
            WsjtxClient.CallCategory.NEW_COUNTRY_ON_BAND,
            WsjtxClient.CallCategory.NEW_COUNTRY,
            WsjtxClient.CallCategory.WANTED_CQ,
            WsjtxClient.CallCategory.ALWAYS_WANTED,
            WsjtxClient.CallCategory.DEFAULT,
        };

        public List<WsjtxClient.RankMethods> rankOrderList = new List<WsjtxClient.RankMethods> { WsjtxClient.RankMethods.MOST_RECENT };
        public WsjtxClient.RankMethods? rankBeamMethod = null;
        public WsjtxClient.RankMethods rankMethod = WsjtxClient.RankMethods.DIST_DECR;
        public int rankMethodIdx = 0;

        // Replace the entire categoryWeight table (loaded from INI or set by Phase 4 UI).
        // Validates that all entries are present and DEFAULT is 0. Returns false (no change
        // applied) if the table is null or DEFAULT isn't 0, matching prior behavior.
        public bool ApplyCategoryWeights(Dictionary<WsjtxClient.CallCategory, int> weights)
        {
            if (weights == null) return false;
            // Merge defaults for any keys absent in the loaded table (handles old INI with extra
            // entries and new UI that hides POTA/SOTA). Missing hidden categories default to 0.
            foreach (WsjtxClient.CallCategory cat in Enum.GetValues(typeof(WsjtxClient.CallCategory)))
            {
                if (!weights.ContainsKey(cat))
                {
                    int defaultTier;
                    categoryWeight.TryGetValue(cat, out defaultTier);
                    weights[cat] = defaultTier;
                }
            }
            if (weights[WsjtxClient.CallCategory.DEFAULT] != 0) return false;  // DEFAULT must always be 0
            categoryWeight = weights;
            return true;
        }

        // Apply calling priorities (loaded from INI or set by dialog).
        public void ApplyCallingPriorities(List<WsjtxClient.CallCategory> enabled)
        {
            callingEnabled = enabled ?? new List<WsjtxClient.CallCategory>
            {
                WsjtxClient.CallCategory.TO_MYCALL, WsjtxClient.CallCategory.NEW_COUNTRY_ON_BAND,
                WsjtxClient.CallCategory.NEW_COUNTRY, WsjtxClient.CallCategory.WANTED_CQ,
                WsjtxClient.CallCategory.ALWAYS_WANTED, WsjtxClient.CallCategory.DEFAULT,
            };
        }

        // POTA, SOTA, and MANUAL_SEL are hidden from the Call Filters UI; they follow the Directed CQ entry.
        public bool IsCallingEnabled(WsjtxClient.CallCategory cat)
        {
            if (cat == WsjtxClient.CallCategory.POTA || cat == WsjtxClient.CallCategory.SOTA || cat == WsjtxClient.CallCategory.MANUAL_SEL)
                cat = WsjtxClient.CallCategory.WANTED_CQ;
            return callingEnabled.Contains(cat);
        }

        public void RankMethodIdxChanged(int idx)
        {
            WsjtxClient.RankMethods method = (WsjtxClient.RankMethods)idx;
            if (idx >= (int)WsjtxClient.RankMethods.AZ_NQUAD)
                ApplySortOrder(new List<WsjtxClient.RankMethods>(rankOrderList), method);
            else
                ApplySortOrder(new List<WsjtxClient.RankMethods> { method }, null);
        }

        public void ApplySortOrder(List<WsjtxClient.RankMethods> orderList, WsjtxClient.RankMethods? beamMethod)
        {
            rankOrderList = (orderList != null && orderList.Count > 0)
                ? new List<WsjtxClient.RankMethods>(orderList)
                : new List<WsjtxClient.RankMethods> { WsjtxClient.RankMethods.MOST_RECENT };
            rankBeamMethod = beamMethod;

            // Keep legacy rankMethod in sync for backward compatibility
            if (beamMethod.HasValue)
                rankMethod = beamMethod.Value;
            else if (rankOrderList.Count > 0)
                rankMethod = rankOrderList[0];
            else
                rankMethod = WsjtxClient.RankMethods.MOST_RECENT;

            rankMethodIdx = (int)rankMethod;
        }

        public bool IsPrimarySort(WsjtxClient.RankMethods method)
        {
            return rankOrderList.Count > 0 && rankOrderList[0] == method;
        }

        public int RegularSortScore(WsjtxClient.RankMethods method, EnqueueDecodeMessage d)
        {
            switch (method)
            {
                case WsjtxClient.RankMethods.CALL_ORDER:  return -1 * d.SequenceNumber;
                case WsjtxClient.RankMethods.MOST_RECENT: return d.SequenceNumber;
                // Stage A6: Distance now comes from d.EffectiveClassification() (GeoMath,
                // via ClassificationEngine) instead of directly off the wire.
                case WsjtxClient.RankMethods.DIST_DECR:   return d.EffectiveClassification().Distance;
                case WsjtxClient.RankMethods.DIST_INCR:   { int dist = d.EffectiveClassification().Distance; return dist < 0 ? dist : EarthDiameter - dist; }
                case WsjtxClient.RankMethods.SNR_DECR:    return d.Snr;
                case WsjtxClient.RankMethods.SNR_INCR:    return d.Snr * -1;
                default:                                  return 0;
            }
        }

        // Category and Priority assumed already set (DeriveCategory called before this, in WsjtxClient).
        //
        // For non-DEFAULT categories: place the call in a tier band far above any DEFAULT
        // sort score. NonDefaultTierBase (100M) is well above the max practical DEFAULT sort
        // score (~8M for MOST_RECENT). CategoryTierRange (1M) separates adjacent tiers. Within
        // a tier, user sort methods (then CALL_ORDER) break ties via Compare/CompareRank --
        // SequenceNumber is NOT embedded here.
        //
        // For DEFAULT calls: use the existing sort-score logic unchanged.
        //
        // debugLog, if supplied, receives the same trace text WsjtxClient.DebugOutput used to
        // log directly -- kept as an optional callback so this class has no dependency on
        // WsjtxClient's debug/DebugOutput plumbing.
        public void SetRank(EnqueueDecodeMessage d, Action<string> debugLog = null)
        {
            if (d.Category != WsjtxClient.CallCategory.DEFAULT)
            {
                // POTA, SOTA, and MANUAL_SEL are hidden from List Priorities UI; they rank with Directed CQ.
                WsjtxClient.CallCategory tierKey = (d.Category == WsjtxClient.CallCategory.POTA || d.Category == WsjtxClient.CallCategory.SOTA
                                        || d.Category == WsjtxClient.CallCategory.MANUAL_SEL)
                    ? WsjtxClient.CallCategory.WANTED_CQ : d.Category;
                int tier;
                if (!categoryWeight.TryGetValue(tierKey, out tier)) tier = 0;
                d.Rank = NonDefaultTierBase + (CategoryTierRange * tier);
                debugLog?.Invoke($"SetRank: '{d.DeCall()}' cat:{d.Category} tierKey:{tierKey} tier:{tier} rank:{d.Rank}");
                return;
            }

            if (rankBeamMethod.HasValue)
            {
                // CalcAzRank uses rankMethod field for heading; rankMethod is kept in sync with rankBeamMethod
                // Stage A6: Azimuth now comes from d.EffectiveClassification() instead of directly off the wire.
                d.Rank = CalcAzRank(d.EffectiveClassification().Azimuth);
                debugLog?.Invoke($"SetRank: '{d.DeCall()}' cat:DEFAULT beam rank:{d.Rank}");
                return;
            }

            d.Rank = RegularSortScore(rankOrderList.Count > 0 ? rankOrderList[0] : WsjtxClient.RankMethods.MOST_RECENT, d);
            debugLog?.Invoke($"SetRank: '{d.DeCall()}' cat:DEFAULT sort rank:{d.Rank}");
        }

        public int CalcAzRank(int az)
        {
            if (az < 0) return OffBeamRank;

            int heading = ((int)rankMethod - (int)WsjtxClient.RankMethods.AZ_NQUAD) * 45;
            int minAz = heading - (BeamWidth / 2);
            int maxAz = heading + (BeamWidth / 2);

            if (minAz < 0)
            {
                minAz = (minAz + BeamWidth) % 360;
                maxAz += BeamWidth;
                heading = (heading + BeamWidth) % 360;
                az = (az + BeamWidth) % 360;
            }

            if (az < minAz || az > maxAz) return OffBeamRank;

            int res = -1 * Math.Abs(az - heading);
            return res;
        }

        // Returns positive if 'existing' should remain before 'incoming' (higher priority).
        // Used by AddCall to find the correct insertion point, and mirrors Compare's order.
        // isLoTWUser/lotwBoostEnabled are passed in rather than referencing LookupManager
        // directly, keeping this class free of any dependency beyond plain data.
        public int CompareRank(EnqueueDecodeMessage existing, EnqueueDecodeMessage incoming, Func<string, bool> isLoTWUser, bool lotwBoostEnabled)
        {
            int cmp = existing.Rank.CompareTo(incoming.Rank);
            if (cmp != 0) return cmp;

            // Same rank. For beam or non-DEFAULT same-tier, primary sort is NOT embedded in
            // .Rank, so apply all rankOrderList methods. For DEFAULT same primary score,
            // skip the first (already in .Rank) and apply secondary/tertiary methods.
            IEnumerable<WsjtxClient.RankMethods> tiebreakers;
            if (rankBeamMethod.HasValue || existing.Rank >= NonDefaultTierBase)
                tiebreakers = rankOrderList;
            else
                tiebreakers = rankOrderList.Skip(1);

            foreach (var method in tiebreakers)
            {
                cmp = RegularSortScore(method, existing).CompareTo(RegularSortScore(method, incoming));
                if (cmp != 0) return cmp;
            }
            // LoTW boost tiebreaker: prefer LoTW users when all other criteria are equal.
            // Only fires for DEFAULT-category calls (non-DEFAULT ranks differ by >= CategoryTierRange).
            if (lotwBoostEnabled && isLoTWUser != null && existing.Rank < NonDefaultTierBase)
            {
                bool exLoTW = isLoTWUser(existing.DeCall());
                bool inLoTW = isLoTWUser(incoming.DeCall());
                if (exLoTW != inLoTW) return exLoTW ? 1 : -1;
            }
            // Final tiebreaker: CALL_ORDER (oldest first = lower SequenceNumber first).
            return incoming.SequenceNumber.CompareTo(existing.SequenceNumber);
        }

        // Used by WsjtxClient.SortCalls() for the full-list descending sort (q before p when
        // Compare(p, q, ...) > 0, matching List<T>.Sort's comparator contract). Mirrors
        // CompareRank exactly but with the opposite (descending) argument convention that
        // list.Sort expects -- kept as a separate method rather than a re-derivation of
        // CompareRank to avoid changing either's behavior in this pass.
        public int Compare(EnqueueDecodeMessage p, EnqueueDecodeMessage q, Func<string, bool> isLoTWUser, bool lotwBoostEnabled)
        {
            int cmp = q.Rank.CompareTo(p.Rank);
            if (cmp != 0) return cmp;

            IEnumerable<WsjtxClient.RankMethods> tiebreakers;
            if (rankBeamMethod.HasValue || q.Rank >= NonDefaultTierBase)
                tiebreakers = rankOrderList;
            else
                tiebreakers = rankOrderList.Skip(1);

            foreach (var method in tiebreakers)
            {
                cmp = RegularSortScore(method, q).CompareTo(RegularSortScore(method, p));
                if (cmp != 0) return cmp;
            }
            if (lotwBoostEnabled && isLoTWUser != null && q.Rank < NonDefaultTierBase)
            {
                bool qLoTW = isLoTWUser(q.DeCall());
                bool pLoTW = isLoTWUser(p.DeCall());
                if (qLoTW != pLoTW) return qLoTW ? 1 : -1;
            }
            return p.SequenceNumber.CompareTo(q.SequenceNumber);
        }
    }
}
