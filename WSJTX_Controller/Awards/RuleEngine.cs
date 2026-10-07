using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;

namespace WSJTX_Controller
{
    public class RuleEndorsementResult
    {
        public string Kind;    // "Band" or "Mode"
        public string Value;   // e.g. "20m"
        public int    Worked;
        public int    Confirmed;
        public bool   Completed;
        public string Tier;
    }

    public class RuleResult
    {
        public RuleDefinition Definition;
        public int    Worked;
        public int    Confirmed;
        public int    UniverseSize = -1;   // -1 = not applicable / not resolvable
        public bool   Completed;
        public string CurrentTier;         // Target=Levels only
        public int    EffectiveThreshold;  // Target=Count only; def.Threshold, or the resolved
                                            // value when def.ThresholdFrom is set (e.g. Honor
                                            // Roll's "current active DXCC entities minus 9")
        public List<string> StillNeeded;   // Target=All only; null if not resolvable

        // Worked but not (yet) confirmed -- workedSet minus confirmedSet. Unlike StillNeeded,
        // this is computed for EVERY GroupBy'd rule regardless of Target type (All or Count),
        // since "I worked this one but need a QSL for it" is a real, useful fact for a
        // Count-target award (e.g. DXCC) just as much as an All-target one (e.g. WAS) --
        // there's no checklist-completion concept here, just "which of the ones I've worked
        // still need a confirmation". Null only when GroupBy'd evaluation didn't run at all
        // (GroupBy=None, or a universe/limitTo resolution failure). See AwardTagger/
        // AwardMatcher for how this backs the live "{Award} Unconf" decode tag.
        public List<string> WorkedUnconfirmed;
        public string EvaluationError;     // e.g. universe unavailable
        public List<RuleEndorsementResult> Endorsements = new List<RuleEndorsementResult>();

        // Per-item breakdown, for UI checklist rendering. Null when not applicable
        // (GroupBy=None has no items; UniverseItems only when Target=All resolved).
        public List<string> UniverseItems;   // Target=All only; the full checklist
        public List<string> WorkedItems;     // GroupBy != None only
        public List<string> ConfirmedItems;  // GroupBy != None only

        // Which specific service(s) confirmed each item, independent of the rule's own
        // Confirmation setting -- always LoTW/QRZ, purely informational (e.g. so the Awards
        // tab can show "LoTW + QRZ" / "LoTW" / "QRZ" instead of just "Confirmed"). GroupBy !=
        // None only. An item can appear in ConfirmedItems without appearing in either of these
        // if it's confirmed via a service not yet tracked here (see Next Build TODO item 1).
        public List<string> LotwConfirmedItems;
        public List<string> QrzConfirmedItems;
        // 2026-10-06: the other channels, each separate -- an eQSL confirmation, one from an AG
        // sender, and a paper card. Informational like the two above; the accepted ones are in
        // ConfirmedItems.
        public List<string> EqslConfirmedItems;
        public List<string> EqslAgConfirmedItems;
        public List<string> CardConfirmedItems;

        // A confirmed-basis checklist (Target=All, Basis=Confirmed): items worked but not yet
        // confirmed by an accepted source -- still needed for completion, but not to WORK again
        // (StillNeeded, which hunting and live tags use, stays worked-based). Null otherwise.
        public List<string> AwaitingConfirmation;

        // Item -> distinct bands it was worked on (band-ordered, low to high), for UI display
        // ("which band(s) did I work this station/entity/etc. on"). GroupBy != None only.
        public Dictionary<string, List<string>> WorkedBands;
    }

    // Evaluates Rule Definitions against the logbook database.
    //
    // This opens its own read-only SQLite connection directly against
    // LogbookDb.DbPath rather than going through LogbookDb's purpose-built query
    // methods. LogbookDb already runs in WAL mode, which allows concurrent
    // readers even while Jimmy holds a writable connection open, so this is safe.
    // The payoff: adding a new GroupBy kind is a change to this file alone --
    // LogbookDb never needs a new one-off query method per award family.
    public static class RuleEngine
    {
        // GroupBy kinds a live FT8 decode can be matched against in-memory, in constant time,
        // using fields WsjtxClient already has on hand (grid->state, or a Club Log entity
        // lookup). Used by Controller.RefreshStillNeedCache() to decide whether the currently
        // selected Still Need Rule Definition can drive live decode tagging, and by the Still
        // Need tab to show the user whether their selection is doing so. GroupBy kinds not
        // listed here (County, Grid, Iota, SigInfo, DarcDok, Prefix, None, ...) have no such
        // field available cheaply at decode time -- those rules still work fine in the Still
        // Need tab's static checklist, just not for live tagging.
        public static readonly HashSet<RuleGroupBy> LiveTagSupportedGroupBys = new HashSet<RuleGroupBy>
        {
            RuleGroupBy.Callsign, RuleGroupBy.State, RuleGroupBy.CqZone,
            RuleGroupBy.Continent, RuleGroupBy.Dxcc,
        };

        // Whether a decode can be live-tagged against this definition's still-needed items at
        // all, independent of whether it currently has any (that's the caller's StillNeeded
        // null check). GroupBy=State is a special case: the decode-time match key comes from
        // GridToUsState (grid -> US state only), so a State-grouped award over a non-US universe
        // -- e.g. Canadaward's Canadian provinces -- would never match live and must be excluded
        // rather than silently doing nothing. Used by both Controller.RefreshStillNeedCache()
        // and the Still Need tab's "Live decode tagging" status line, so the two never disagree.
        public static bool SupportsLiveTag(RuleDefinition def)
        {
            if (def == null || !LiveTagSupportedGroupBys.Contains(def.GroupBy)) return false;
            if (def.GroupBy == RuleGroupBy.State)
                return "US_50_STATES".Equals(def.Universe, StringComparison.OrdinalIgnoreCase);
            return true;
        }

        // clubLog defaults to RuleLibrary.ClubLog (the live provider wired up at
        // startup) when not supplied -- callers only need to pass one explicitly
        // in tests or other contexts where the global isn't set up.
        public static RuleResult Evaluate(RuleDefinition def, string dbPath = null, ClubLogProvider clubLog = null)
        {
            dbPath = dbPath ?? LogbookDb.DbPath;
            using (var conn = new SQLiteConnection($"Data Source={dbPath};Read Only=True;"))
            {
                conn.Open();
                return Evaluate(def, conn, clubLog ?? RuleLibrary.ClubLog);
            }
        }

        // Evaluates a definition restricted to a single band (or all bands, if
        // band is null/blank) -- used by the Still Need tab's band filter.
        // Endorsements are not computed here since that view doesn't show them.
        public static RuleResult EvaluateBand(RuleDefinition def, string band, string dbPath = null, ClubLogProvider clubLog = null)
        {
            dbPath = dbPath ?? LogbookDb.DbPath;
            using (var conn = new SQLiteConnection($"Data Source={dbPath};Read Only=True;"))
            {
                conn.Open();
                return EvaluateCore(def, conn, string.IsNullOrWhiteSpace(band) ? null : band, null, clubLog ?? RuleLibrary.ClubLog);
            }
        }

        // Resolves which band(s) one evaluation actually filters by. bandOverride (from the
        // Still Need tab's band selector, or EvaluateBand's caller in general) narrows an
        // unrestricted award to one band -- that's a legitimate "how am I doing on just this
        // band" browse. But an award that already restricts itself to specific band(s)
        // (defBands non-empty) must never be evaluated "as" some other band instead -- e.g.
        // picking "20m" for a 6m-only award must not silently substitute 20m data under that
        // award's name. So an override is only honored if it's actually one of the award's own
        // bands; otherwise it's ignored and the award's own Bands list is used untouched.
        public static List<string> ResolveBandsForEvaluation(List<string> defBands, string bandOverride)
        {
            if (bandOverride == null) return defBands;
            if (defBands.Count == 0) return new List<string> { bandOverride };
            return defBands.Any(b => b.Equals(bandOverride, StringComparison.OrdinalIgnoreCase))
                ? new List<string> { bandOverride }
                : defBands;
        }

        // Which bands the Still Need tab's Band dropdown should offer for a given award.
        // An unrestricted award (defBands empty) offers "(All Bands)" plus every real band --
        // useful for browsing "how am I doing on just 20m" on an all-band award like WAS/DXCC.
        // A band-restricted award (e.g. a per-band WAS variant, or a single-band special
        // event) offers "(All Bands)" (meaning: evaluate using the award's own Bands list, no
        // override) plus only its own band(s) -- any other band can never be a meaningful
        // choice for that award. allBandsSentinelAndList's first entry is the "(All Bands)"
        // sentinel; the rest are every real band the app knows about.
        public static string[] BandChoicesFor(List<string> defBands, string[] allBandsSentinelAndList)
        {
            if (defBands == null || defBands.Count == 0) return allBandsSentinelAndList;
            var choices = new List<string> { allBandsSentinelAndList[0] };
            choices.AddRange(defBands);
            return choices.ToArray();
        }

        public static List<RuleResult> EvaluateAll(IEnumerable<RuleDefinition> defs, string dbPath = null, ClubLogProvider clubLog = null)
        {
            dbPath = dbPath ?? LogbookDb.DbPath;
            clubLog = clubLog ?? RuleLibrary.ClubLog;
            var results = new List<RuleResult>();
            using (var conn = new SQLiteConnection($"Data Source={dbPath};Read Only=True;"))
            {
                conn.Open();
                foreach (var def in defs.Where(d => d.Enabled))
                    results.Add(Evaluate(def, conn, clubLog));
            }
            return results;
        }

        public static RuleResult Evaluate(RuleDefinition def, SQLiteConnection conn, ClubLogProvider clubLog = null)
        {
            var main = EvaluateCore(def, conn, null, null, clubLog);

            if (def.Endorsements != null)
            {
                foreach (var band in def.Endorsements.Bands)
                {
                    var sub = EvaluateCore(def, conn, band, null, clubLog);
                    main.Endorsements.Add(new RuleEndorsementResult
                    {
                        Kind = "Band", Value = band,
                        Worked = sub.Worked, Confirmed = sub.Confirmed,
                        Completed = sub.Completed, Tier = sub.CurrentTier,
                    });
                }
                foreach (var mode in def.Endorsements.Modes)
                {
                    var sub = EvaluateCore(def, conn, null, mode, clubLog);
                    main.Endorsements.Add(new RuleEndorsementResult
                    {
                        Kind = "Mode", Value = mode,
                        Worked = sub.Worked, Confirmed = sub.Confirmed,
                        Completed = sub.Completed, Tier = sub.CurrentTier,
                    });
                }
            }

            return main;
        }

        private static RuleResult EvaluateCore(
            RuleDefinition def, SQLiteConnection conn, string bandOverride, string modeOverride, ClubLogProvider clubLog)
        {
            var result = new RuleResult { Definition = def };

            HashSet<string> universe = null;
            if (def.Target == RuleTargetType.All)
            {
                string uniError;
                universe = RuleUniverse.Resolve(def.Universe, RuleLoader.ListsFolder, clubLog, out uniError);
                if (universe == null)
                {
                    result.EvaluationError = uniError ?? "Universe could not be resolved.";
                    return result;
                }
                result.UniverseSize = universe.Count;
                result.UniverseItems = universe.OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToList();
            }

            HashSet<string> limitTo = null;
            if (!string.IsNullOrWhiteSpace(def.LimitTo))
            {
                string limitError;
                limitTo = RuleUniverse.Resolve(def.LimitTo, RuleLoader.ListsFolder, clubLog, out limitError);
                if (limitTo == null)
                {
                    result.EvaluationError = "Match.LimitTo: " + (limitError ?? "could not be resolved.");
                    return result;
                }
            }

            string confirmExpr = ConfirmationExpression(def);

            var whereParts = new List<string>();
            var parms = new List<SQLiteParameter>();
            AddGroupByFilter(def, whereParts);

            var bands = ResolveBandsForEvaluation(def.Bands, bandOverride);
            if (bands.Count > 0)
            {
                var names = new List<string>();
                for (int i = 0; i < bands.Count; i++)
                {
                    string p = $"@band{i}";
                    names.Add(p);
                    parms.Add(new SQLiteParameter(p, bands[i].ToLowerInvariant()));
                }
                whereParts.Add($"band IN ({string.Join(",", names)})");
            }
            for (int i = 0; i < def.ExcludeBands.Count; i++)
            {
                whereParts.Add($"LOWER(band) != @xband{i}");
                parms.Add(new SQLiteParameter($"@xband{i}", def.ExcludeBands[i].ToLowerInvariant()));
            }
            var dxccIn = def.DxccIn.Select(d => int.TryParse(d, out int n) ? n : -1).Where(n => n > 0).ToList();
            if (dxccIn.Count > 0)
                whereParts.Add($"{DxccExpression(def)} IN ({string.Join(",", dxccIn)})");
            for (int i = 0; i < def.ExcludeCallsigns.Count; i++)
            {
                whereParts.Add($"UPPER(callsign) NOT LIKE @xcall{i} ESCAPE '\\'");
                parms.Add(new SQLiteParameter($"@xcall{i}", WildcardToLike(def.ExcludeCallsigns[i])));
            }

            var modes = modeOverride != null ? new List<string> { modeOverride } : def.Modes;
            if (modes.Count > 0)
            {
                var names = new List<string>();
                for (int i = 0; i < modes.Count; i++)
                {
                    string p = $"@mode{i}";
                    names.Add(p);
                    parms.Add(new SQLiteParameter(p, modes[i].ToUpperInvariant()));
                }
                whereParts.Add($"UPPER(mode) IN ({string.Join(",", names)})");
            }

            if (!string.IsNullOrWhiteSpace(def.CallsignPattern))
            {
                whereParts.Add("UPPER(callsign) LIKE @callPattern ESCAPE '\\'");
                parms.Add(new SQLiteParameter("@callPattern", WildcardToLike(def.CallsignPattern)));
            }

            if (!string.IsNullOrWhiteSpace(def.Sig))
            {
                whereParts.Add("UPPER(sig) = @sig");
                parms.Add(new SQLiteParameter("@sig", def.Sig.ToUpperInvariant()));
            }

            // Dates compare as UTC yyyyMMddHHmm (a date alone is the whole day): an event window
            // like "2026-07-01 1300" to "2026-07-08 0400" counts to the minute (2026-10-06).
            const string when = "(qso_date || SUBSTR(IFNULL(time_on,'') || '0000', 1, 4))";
            if (!string.IsNullOrWhiteSpace(def.DateFrom))
            {
                whereParts.Add($"{when} >= @dateFrom");
                parms.Add(new SQLiteParameter("@dateFrom", CompactDateTime(def.DateFrom, false)));
            }
            if (!string.IsNullOrWhiteSpace(def.DateTo))
            {
                whereParts.Add($"{when} <= @dateTo");
                parms.Add(new SQLiteParameter("@dateTo", CompactDateTime(def.DateTo, true)));
            }

            string where = whereParts.Count > 0 ? "WHERE " + string.Join(" AND ", whereParts) : "";

            if (def.GroupBy == RuleGroupBy.None)
                EvaluatePlainCount(def, conn, where, parms, confirmExpr, result);
            else if (def.GroupBy == RuleGroupBy.Prefix)
                EvaluatePrefixGroup(def, conn, where, parms, confirmExpr, universe, limitTo, result);
            else
                EvaluateGrouped(def, conn, GroupByExpression(def), where, parms, confirmExpr, universe, limitTo, result);

            // Dynamic Target=Count threshold (def.ThresholdFrom set, e.g. Honor Roll) --
            // resolved independently of the grouped query above, right before it's consumed.
            // A resolution failure (e.g. Club Log data not downloaded yet) doesn't discard the
            // Worked/Confirmed counts already computed above; it just marks the result unusable
            // for anything that needs a real Completed/EffectiveThreshold (same EvaluationError
            // contract Target=All's own universe-resolution failure uses).
            int effectiveThreshold = def.Threshold;
            if (def.Target == RuleTargetType.Count && !string.IsNullOrWhiteSpace(def.ThresholdFrom))
            {
                string thresholdError;
                var thresholdUniverse = RuleUniverse.Resolve(def.ThresholdFrom, RuleLoader.ListsFolder, clubLog, out thresholdError);
                if (thresholdUniverse == null)
                {
                    result.EvaluationError = "Target.ThresholdFrom: " + (thresholdError ?? "could not be resolved.");
                }
                else
                {
                    effectiveThreshold = Math.Max(0, thresholdUniverse.Count - def.ThresholdOffset);
                }
            }

            ApplyTarget(def, result, universe, effectiveThreshold);
            return result;
        }

        private static void EvaluatePlainCount(
            RuleDefinition def, SQLiteConnection conn, string where,
            List<SQLiteParameter> parms, string confirmExpr, RuleResult result)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT COUNT(*), SUM(CASE WHEN {confirmExpr} THEN 1 ELSE 0 END) FROM qso {where};";
                foreach (var p in parms) cmd.Parameters.Add(p);
                using (var r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        result.Worked    = r.IsDBNull(0) ? 0 : Convert.ToInt32(r.GetValue(0));
                        result.Confirmed = r.IsDBNull(1) ? 0 : Convert.ToInt32(r.GetValue(1));
                    }
                }
            }
            if (def.Confirmation == RuleConfirmation.None) result.Confirmed = result.Worked;
        }

        private static void EvaluateGrouped(
            RuleDefinition def, SQLiteConnection conn, string groupExpr, string where,
            List<SQLiteParameter> parms, string confirmExpr,
            HashSet<string> universe, HashSet<string> limitTo, RuleResult result)
        {
            var workedSet    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var confirmedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sources      = new Evidence();
            var bandsByItem   = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    $"SELECT {groupExpr} AS g, MAX(CASE WHEN {confirmExpr} THEN 1 ELSE 0 END) AS conf, " +
                    $"GROUP_CONCAT(DISTINCT band) AS bands, {Evidence.Columns} " +
                    $"FROM qso {where} GROUP BY g;";
                foreach (var p in parms) cmd.Parameters.Add(p);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        if (r.IsDBNull(0)) continue;
                        string g = r.GetValue(0).ToString();
                        if (g.Length == 0) continue;
                        workedSet.Add(g);
                        if (!r.IsDBNull(1) && Convert.ToInt32(r.GetValue(1)) != 0) confirmedSet.Add(g);
                        if (!r.IsDBNull(2)) bandsByItem[g] = OrderBands(r.GetString(2).Split(','));
                        sources.Read(r, 3, g);
                    }
                }
            }

            FinishGrouped(def, workedSet, confirmedSet, sources, bandsByItem, universe, limitTo, result);
        }

        // GroupBy=Prefix is computed in C# (see WpxPrefixOf), so it can't use the
        // GROUP BY-in-SQL path the other GroupBy kinds use.
        private static void EvaluatePrefixGroup(
            RuleDefinition def, SQLiteConnection conn, string where,
            List<SQLiteParameter> parms, string confirmExpr,
            HashSet<string> universe, HashSet<string> limitTo, RuleResult result)
        {
            var workedSet    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var confirmedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sources      = new Evidence();
            var bandsByItem  = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT callsign, wpx_prefix, ({confirmExpr}), band, {Evidence.Columns} FROM qso {where};";
                foreach (var p in parms) cmd.Parameters.Add(p);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        string call = r.IsDBNull(0) ? "" : r.GetString(0);
                        if (call.Length == 0) continue;
                        string storedPfx = r.IsDBNull(1) ? "" : r.GetString(1);
                        string pfx = storedPfx.Length > 0 ? storedPfx : WpxPrefixOf(call);
                        if (pfx.Length == 0) continue;

                        workedSet.Add(pfx);
                        if (!r.IsDBNull(2) && Convert.ToInt64(r.GetValue(2)) != 0) confirmedSet.Add(pfx);
                        if (!r.IsDBNull(3))
                        {
                            string band = r.GetString(3);
                            if (band.Length > 0)
                            {
                                if (!bandsByItem.TryGetValue(pfx, out var set))
                                    bandsByItem[pfx] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                set.Add(band);
                            }
                        }
                        sources.Read(r, 4, pfx);
                    }
                }
            }

            var orderedBandsByItem = bandsByItem.ToDictionary(kv => kv.Key, kv => OrderBands(kv.Value), StringComparer.OrdinalIgnoreCase);
            FinishGrouped(def, workedSet, confirmedSet, sources, orderedBandsByItem, universe, limitTo, result);
        }

        // Canonical low-to-high band order (matches AdifImporter's frequency-ascending band
        // table) so a UI showing "which bands did I work this on" reads naturally instead of
        // alphabetically (which would put "10m" before "160m").
        private static readonly string[] BandOrder =
        {
            "2200m", "630m", "160m", "80m", "60m", "40m", "30m", "20m", "17m", "15m",
            "12m", "10m", "6m", "4m", "2m", "1.25m", "70cm", "33cm", "23cm",
        };

        private static List<string> OrderBands(IEnumerable<string> bands) =>
            bands.Where(b => !string.IsNullOrEmpty(b))
                 .Distinct(StringComparer.OrdinalIgnoreCase)
                 .OrderBy(b => { int i = Array.IndexOf(BandOrder, b.ToLowerInvariant()); return i < 0 ? int.MaxValue : i; })
                 .ThenBy(b => b, StringComparer.OrdinalIgnoreCase)
                 .ToList();

        private static void FinishGrouped(
            RuleDefinition def, HashSet<string> workedSet, HashSet<string> confirmedSet, Evidence sources,
            Dictionary<string, List<string>> bandsByItem,
            HashSet<string> universe, HashSet<string> limitTo, RuleResult result)
        {
            if (def.Confirmation == RuleConfirmation.None) confirmedSet = workedSet;

            if (limitTo != null)
            {
                workedSet    = new HashSet<string>(workedSet,    StringComparer.OrdinalIgnoreCase);
                confirmedSet = new HashSet<string>(confirmedSet, StringComparer.OrdinalIgnoreCase);
                workedSet.IntersectWith(limitTo);
                confirmedSet.IntersectWith(limitTo);
                sources.IntersectWith(limitTo);
            }

            // For a checklist-style (ALL) award, "worked"/"confirmed" should reflect
            // progress against the checklist itself -- not every distinct value ever
            // seen, which could include stray/foreign data that happens to collide
            // with the grouped column's format (e.g. a garbage 2-letter "state").
            if (def.Target == RuleTargetType.All && universe != null)
            {
                workedSet    = new HashSet<string>(workedSet,    StringComparer.OrdinalIgnoreCase);
                confirmedSet = new HashSet<string>(confirmedSet, StringComparer.OrdinalIgnoreCase);
                workedSet.IntersectWith(universe);
                confirmedSet.IntersectWith(universe);
                sources.IntersectWith(universe);
            }

            result.Worked    = workedSet.Count;
            result.Confirmed = confirmedSet.Count;
            result.WorkedItems    = workedSet.OrderBy(w => w, StringComparer.OrdinalIgnoreCase).ToList();
            result.ConfirmedItems = confirmedSet.OrderBy(w => w, StringComparer.OrdinalIgnoreCase).ToList();
            result.LotwConfirmedItems   = Sorted(sources.Lotw);
            result.QrzConfirmedItems    = Sorted(sources.Qrz);
            result.EqslConfirmedItems   = Sorted(sources.Eqsl);
            result.EqslAgConfirmedItems = Sorted(sources.EqslAg);
            result.CardConfirmedItems   = Sorted(sources.Card);
            result.WorkedBands    = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in workedSet)
                if (bandsByItem.TryGetValue(item, out var bands))
                    result.WorkedBands[item] = bands;

            // StillNeeded is always worked-based -- a station drops off the needed list as soon as
            // it's logged, so hunting and live tags never chase a station already worked. A
            // confirmed-basis checklist's completion is ApplyTarget's; what it still waits for
            // (worked, no accepted confirmation yet) is AwaitingConfirmation.
            if (def.Target == RuleTargetType.All && universe != null)
            {
                result.StillNeeded = universe.Where(u => !workedSet.Contains(u)).OrderBy(u => u).ToList();
                if (def.Basis == RuleBasis.Confirmed)
                    result.AwaitingConfirmation = universe.Where(u => workedSet.Contains(u) && !confirmedSet.Contains(u))
                        .OrderBy(u => u).ToList();
            }

            // Independent of Target/StillNeeded above -- see the field's own comment on
            // RuleResult. Computed from the same workedSet/confirmedSet already adjusted for
            // limitTo/universe restriction above, so a Target=All award's Unconfirmed set is
            // checklist-scoped the same way StillNeeded is, and a Target=Count award (e.g.
            // DXCC, no universe) simply uses every distinct worked value.
            result.WorkedUnconfirmed = workedSet.Where(w => !confirmedSet.Contains(w))
                .OrderBy(w => w, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // basis is Worked by default for every Target type -- Target=All is ALWAYS Worked,
        // never overridable (see FinishGrouped's own comment); Count/Levels use def.Basis,
        // which defaults to Worked too, so every award that doesn't explicitly opt in via
        // Basis=CONFIRMED is completely unaffected. effectiveThreshold is def.Threshold
        // unless def.ThresholdFrom resolved a dynamic value (see EvaluateCore).
        private static void ApplyTarget(RuleDefinition def, RuleResult result, HashSet<string> universe, int effectiveThreshold)
        {
            result.EffectiveThreshold = effectiveThreshold;
            int basis = def.Basis == RuleBasis.Confirmed ? result.Confirmed : result.Worked;

            switch (def.Target)
            {
                case RuleTargetType.All:
                    // Worked basis: every item worked. Confirmed basis: every item confirmed by an
                    // accepted source -- working everything is not completion (2026-10-06).
                    if (universe != null)
                        result.Completed = result.StillNeeded != null && result.StillNeeded.Count == 0
                            && (def.Basis != RuleBasis.Confirmed || (result.AwaitingConfirmation?.Count ?? 0) == 0);
                    break;

                case RuleTargetType.Count:
                    result.Completed = basis >= effectiveThreshold;
                    break;

                case RuleTargetType.Levels:
                    string tier = null;
                    foreach (var lvl in def.Levels)
                        if (basis >= lvl.Threshold) tier = lvl.Name;
                    result.CurrentTier = tier;
                    result.Completed   = tier != null;
                    break;
            }
        }

        // ── SQL fragment helpers ────────────────────────────────────────────────

        // The location an award counts for a contact (2026-10-04): the location LoTW CONFIRMED when
        // LoTW confirmed the contact with one (kept in lotw_*, apart from the logged location),
        // otherwise the logged location. The logged fields are never changed by a confirmation.
        internal static string AwardText(string col, string lotwCol) => AwardText(col, lotwCol, true);
        internal static string AwardNumber(string col, string lotwCol) => AwardNumber(col, lotwCol, true);

        // useLotw = false: the logged location only -- an award that does not accept LoTW never
        // counts a contact where LoTW says it was (RuleDefinition.UsesLotwLocation).
        internal static string AwardText(string col, string lotwCol, bool useLotw) => useLotw
            ? $"(CASE WHEN lotw_qsl_rcvd='Y' AND TRIM({lotwCol})!='' THEN {lotwCol} ELSE {col} END)" : col;
        internal static string AwardNumber(string col, string lotwCol, bool useLotw) => useLotw
            ? $"(CASE WHEN lotw_qsl_rcvd='Y' AND {lotwCol}>0 THEN {lotwCol} ELSE {col} END)" : col;

        private static string DxccExpression(RuleDefinition def) => AwardNumber("dxcc", "lotw_dxcc", def.UsesLotwLocation);

        // The state an award counts; "DC" as "MD" where the sponsor says so (ARRL WAS, QRZ US).
        private static string StateExpression(RuleDefinition def)
        {
            string st = $"UPPER(TRIM({AwardText("state", "lotw_state", def.UsesLotwLocation)}))";
            return def.DcCountsAsMaryland ? $"(CASE WHEN {st}='DC' THEN 'MD' ELSE {st} END)" : st;
        }

        // A county is its state AND its name (2026-10-06): "Washington" in Oregon and in Utah are two
        // counties. ADIF CNTY is "ST,Name"; a bare name takes the contact's state. Spaces trimmed.
        private static string CountyExpression(RuleDefinition def)
        {
            string c = $"UPPER(TRIM({AwardText("county", "lotw_cnty", def.UsesLotwLocation)}))";
            string st = $"UPPER(TRIM({AwardText("state", "lotw_state", def.UsesLotwLocation)}))";
            return $"(CASE WHEN {c} GLOB '[A-Z][A-Z],*' THEN REPLACE({c}, ', ', ',') WHEN {st} != '' THEN {st} || ',' || {c} ELSE '' END)";
        }

        // A grid counts only if it is a real Maidenhead locator: field letters A-R, square digits.
        private static string GridText(RuleDefinition def) => $"UPPER(TRIM({AwardText("grid", "lotw_grid", def.UsesLotwLocation)}))";
        private static string ValidGrid4(string g) => $"SUBSTR({g},1,4) GLOB '[A-R][A-R][0-9][0-9]'";

        private static string GroupByExpression(RuleDefinition def)
        {
            bool l = def.UsesLotwLocation;
            switch (def.GroupBy)
            {
                case RuleGroupBy.Dxcc:      return DxccExpression(def);
                case RuleGroupBy.Country:   return "country";
                case RuleGroupBy.State:     return StateExpression(def);
                case RuleGroupBy.CqZone:    return AwardNumber("cq_zone", "lotw_cqz", l);
                case RuleGroupBy.ItuZone:   return AwardNumber("itu_zone", "lotw_ituz", l);
                case RuleGroupBy.Continent: return "UPPER(TRIM(continent))";
                case RuleGroupBy.County:    return CountyExpression(def);
                case RuleGroupBy.Grid:      return GridText(def);
                case RuleGroupBy.Grid4:     return $"SUBSTR({GridText(def)},1,4)";
                case RuleGroupBy.Iota:      return "UPPER(TRIM(iota))";
                case RuleGroupBy.SigInfo:   return "UPPER(TRIM(sig_info))";
                case RuleGroupBy.DarcDok:   return "UPPER(TRIM(darc_dok))";
                case RuleGroupBy.Callsign:  return "callsign";
                default: throw new InvalidOperationException("GroupByExpression not applicable for " + def.GroupBy);
            }
        }

        private static void AddGroupByFilter(RuleDefinition def, List<string> whereParts)
        {
            bool l = def.UsesLotwLocation;
            switch (def.GroupBy)
            {
                case RuleGroupBy.Dxcc:      whereParts.Add($"{DxccExpression(def)} > 0"); break;
                case RuleGroupBy.Country:   whereParts.Add("country != ''"); break;
                case RuleGroupBy.State:     whereParts.Add($"{StateExpression(def)} != ''"); break;
                case RuleGroupBy.CqZone:    whereParts.Add($"{AwardNumber("cq_zone", "lotw_cqz", l)} > 0"); break;
                case RuleGroupBy.ItuZone:   whereParts.Add($"{AwardNumber("itu_zone", "lotw_ituz", l)} > 0"); break;
                case RuleGroupBy.Continent: whereParts.Add("TRIM(continent) != ''"); break;
                case RuleGroupBy.County:    whereParts.Add($"{CountyExpression(def)} GLOB '[A-Z][A-Z],?*'"); break;
                case RuleGroupBy.Grid:      whereParts.Add($"{ValidGrid4(GridText(def))}"); break;
                case RuleGroupBy.Grid4:     whereParts.Add($"{ValidGrid4(GridText(def))}"); break;
                case RuleGroupBy.Iota:      whereParts.Add("TRIM(iota) != ''"); break;
                case RuleGroupBy.SigInfo:   whereParts.Add("TRIM(sig_info) != ''"); break;
                case RuleGroupBy.DarcDok:   whereParts.Add("TRIM(darc_dok) != ''"); break;
                case RuleGroupBy.Callsign:  whereParts.Add("callsign != ''"); break;
                case RuleGroupBy.Prefix:    whereParts.Add("callsign != ''"); break;
                    // None: no filter.
            }
        }

        // ── Confirmation evidence, one channel at a time ─────────────────────────
        // A paper card: Nexus's card flag, carried in the read copy as a QSL_RCVD=Y extra. An AG
        // eQSL: an eQSL confirmation AND the sender's AG mark, which Jimmy keeps on the contact as
        // APP_JIMMY_EQSL_AG (from eQSL's EQSL_AG / APP_EQSL_AG -- NexusLogbookService.KeepEqslAg).
        internal const string EqslAgTag = "APP_JIMMY_EQSL_AG";
        private const string CardExpr = "EXISTS(SELECT 1 FROM qso_extra_field e WHERE e.qso_id=qso.id AND e.tag_name='QSL_RCVD' AND e.tag_value='Y')";
        private static readonly string EqslAgExpr =
            $"(eqsl_qsl_rcvd='Y' AND EXISTS(SELECT 1 FROM qso_extra_field e WHERE e.qso_id=qso.id AND e.tag_name='{EqslAgTag}' AND e.tag_value='Y'))";

        private static string SourceExpression(string source)
        {
            switch (source)
            {
                case RuleConfirmationSources.Lotw: return "lotw_qsl_rcvd='Y'";
                case RuleConfirmationSources.Qrz: return "qrz_qsl_rcvd='Y'";
                case RuleConfirmationSources.Eqsl: return "eqsl_qsl_rcvd='Y'";
                case RuleConfirmationSources.EqslAg: return EqslAgExpr;
                case RuleConfirmationSources.Card: return CardExpr;
                default: return "0";
            }
        }

        // Which channels confirmed each item, kept apart.
        private sealed class Evidence
        {
            public static readonly string Columns = string.Join(", ",
                new[] { RuleConfirmationSources.Lotw, RuleConfirmationSources.Qrz, RuleConfirmationSources.Eqsl,
                        RuleConfirmationSources.EqslAg, RuleConfirmationSources.Card }
                .Select(src => $"MAX(CASE WHEN {SourceExpression(src)} THEN 1 ELSE 0 END)"));
            public HashSet<string> Lotw = New(), Qrz = New(), Eqsl = New(), EqslAg = New(), Card = New();
            private static HashSet<string> New() => new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public void Read(SQLiteDataReader r, int first, string item)
            {
                var sets = new[] { Lotw, Qrz, Eqsl, EqslAg, Card };
                for (int i = 0; i < sets.Length; i++)
                    if (!r.IsDBNull(first + i) && Convert.ToInt64(r.GetValue(first + i)) != 0) sets[i].Add(item);
            }

            public void IntersectWith(HashSet<string> keep)
            {
                foreach (var set in new[] { Lotw, Qrz, Eqsl, EqslAg, Card }) set.IntersectWith(keep);
            }
        }

        private static List<string> Sorted(HashSet<string> set) => set.OrderBy(w => w, StringComparer.OrdinalIgnoreCase).ToList();

        private static string ConfirmationExpression(RuleDefinition def)
        {
            switch (def.Confirmation)
            {
                case RuleConfirmation.Sources:
                {
                    // Any one of the named channels confirms. CARD: a paper card, which only a
                    // Nexus-owned logbook records (its read projection keeps it as a QSL_RCVD=Y
                    // extra -- NexusMigration.Rebuild). Jimmy's own database never stores QSL_RCVD
                    // (its importers consume it), so on it CARD matches nothing -- it can never
                    // inflate a total.
                    var parts = def.ConfirmationSources.Select(SourceExpression).Where(x => x != "0").ToList();
                    return parts.Count == 0 ? "0" : "(" + string.Join(" OR ", parts) + ")";
                }
                case RuleConfirmation.Lotw: return "lotw_qsl_rcvd='Y'";
                case RuleConfirmation.Qrz:  return "qrz_qsl_rcvd='Y'";
                case RuleConfirmation.Both: return "(lotw_qsl_rcvd='Y' AND qrz_qsl_rcvd='Y')";
                case RuleConfirmation.None: return "1=1";
                default:                    return "(lotw_qsl_rcvd='Y' OR qrz_qsl_rcvd='Y')"; // Any
            }
        }

        private static string WildcardToLike(string pattern) =>
            pattern.Trim().ToUpperInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")
                   .Replace("*", "%").Replace("?", "_");

        // "yyyy-MM-dd" or "yyyy-MM-dd HHmm" (UTC; "HH:mm" too) -> yyyyMMddHHmm, the form EvaluateCore
        // compares; a date alone is 0000 as a start, 2359 as an end. Null when it is neither.
        internal static string CompactDateTime(string text, bool isEnd)
        {
            string digits = new string((text ?? "").Where(char.IsDigit).ToArray());
            if (digits.Length != 8 && digits.Length != 12) return null;
            if (!DateTime.TryParseExact(digits.Substring(0, 8), "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out _)) return null;
            if (digits.Length == 8) return digits + (isEnd ? "2359" : "0000");
            int hh = int.Parse(digits.Substring(8, 2)), mm = int.Parse(digits.Substring(10, 2));
            return hh < 24 && mm < 60 ? digits : null;
        }

        // ── WPX-style prefix derivation ─────────────────────────────────────────

        // Standard CQ WPX prefix: the callsign up to and including its last digit,
        // with "/" portable designators handled per WPX contest rules -- a numeric
        // suffix (e.g. "/4") replaces the numeral, a longer alphanumeric prefix
        // before the slash (e.g. "PJ4/K1ABC") is used as-is, and non-numeric
        // suffixes with no digit of their own (e.g. "/P", "/QRP") are ignored.
        // Only used as a fallback when the ADIF PFX field wasn't supplied by the
        // source data (stored in qso.wpx_prefix, which always takes precedence).
        public static string WpxPrefixOf(string callsign)
        {
            if (string.IsNullOrWhiteSpace(callsign)) return "";
            callsign = callsign.Trim().ToUpperInvariant();
            var parts = callsign.Split('/');

            string basisCall = parts[0];
            string overrideSuffix = null;

            if (parts.Length >= 2)
            {
                string second = parts[1];
                if (second.Length > 0 && second.Length <= 2 && second.All(char.IsDigit))
                    overrideSuffix = second;
                // else if it's a longer alphanumeric prefix (e.g. "PJ4/K1ABC"),
                // basisCall already defaults to parts[0] ("PJ4"), which is correct.
                // else (e.g. "/P", "/QRP", "/MM") -- no digit, ignore, keep parts[0].
            }

            string prefix = RawPrefix(basisCall);

            if (overrideSuffix != null)
            {
                string letters = new string(prefix.TakeWhile(c => !char.IsDigit(c)).ToArray());
                if (letters.Length == 0) letters = prefix;
                prefix = letters + overrideSuffix;
            }

            return prefix;
        }

        private static string RawPrefix(string call)
        {
            int lastDigit = -1;
            for (int i = 0; i < call.Length; i++)
                if (char.IsDigit(call[i])) lastDigit = i;
            return lastDigit < 0 ? call : call.Substring(0, lastDigit + 1);
        }
    }
}
