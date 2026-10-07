using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSJTX_Controller
{
    // Resolves a RuleDefinition's Universe string into a concrete checklist to
    // compare worked/confirmed values against. Built-in universes are small,
    // stable lists that rarely change; Club Log universes are derived from
    // Jimmy's automatically-downloaded country data (see ClubLogProvider); and
    // "File:name.txt" universes are companion files dropped in the
    // RuleDefinitions\Lists folder.
    public static class RuleUniverse
    {
        public static readonly string[] Us50States =
        {
            "AK","AL","AR","AZ","CA","CO","CT","DE","FL","GA","HI","IA","ID","IL","IN",
            "KS","KY","LA","MA","MD","ME","MI","MN","MO","MS","MT","NC","ND","NE","NH","NJ",
            "NM","NV","NY","OH","OK","OR","PA","RI","SC","SD","TN","TX","UT","VA","VT","WA",
            "WI","WV","WY"
        };

        // 10 provinces + 3 territories, as used in the ADIF STATE field for
        // Canadian contacts (replaces the old rac_provinces.txt companion file).
        public static readonly string[] CaProvinces =
        {
            "AB","BC","MB","NB","NL","NS","NT","NU","ON","PE","QC","SK","YT"
        };

        // Matches the ADIF CONT field's continent codes. AN (Antarctica) is a
        // legitimate ADIF continent value -- a handful of DXCC entities
        // (e.g. some sub-Antarctic islands) are classified there.
        public static readonly string[] Continents = { "NA", "SA", "EU", "AF", "AS", "OC", "AN" };

        // The six inhabited continents WAC and QRZ World Continents ask for (2026-10-06): Antarctica
        // is not one of them and can never stand in for another.
        public static readonly string[] SixContinents = { "NA", "SA", "EU", "AF", "AS", "OC" };

        public static string[] CqZones =>
            Enumerable.Range(1, 40).Select(z => z.ToString()).ToArray();

        // ITU zones are numbered 1-90.
        public static string[] ItuZones =>
            Enumerable.Range(1, 90).Select(z => z.ToString()).ToArray();

        // Continent-filtered DXCC universe tokens -> the ADIF continent code
        // Club Log tags each entity with. A fixed, closed set matching the
        // real-world continental DXCC award families (e.g. WAC-style awards) --
        // not meant to grow into a general "DXCC filtered by X" syntax.
        private static readonly Dictionary<string, string> DxccContinentTokens =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DXCC_NORTH_AMERICA"] = "NA",
                ["DXCC_SOUTH_AMERICA"] = "SA",
                ["DXCC_EUROPE"]        = "EU",
                ["DXCC_AFRICA"]        = "AF",
                ["DXCC_ASIA"]          = "AS",
                ["DXCC_OCEANIA"]       = "OC",
            };

        // Returns null (with a human-readable reason in error) if the universe can't
        // be resolved: unknown name, missing companion file, or a Club Log-backed
        // universe with no data downloaded yet. A null return means "ALL"-type
        // completion/still-needed can't be computed for this award right now, but
        // COUNT/LEVELS-type awards never call this at all.
        //
        // clubLog may be null (e.g. a caller that hasn't wired one up, or a unit
        // test exercising only built-in/file universes) -- Club Log-backed tokens
        // simply report "not available" in that case, same as no data downloaded.
        public static HashSet<string> Resolve(string universe, string listsFolder, ClubLogProvider clubLog, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(universe))
            {
                error = "No Universe specified.";
                return null;
            }

            var builtIn = ResolveBuiltIn(universe);
            if (builtIn != null) return builtIn;

            if (universe.Equals("DXCC_CURRENT", StringComparison.OrdinalIgnoreCase) ||
                universe.Equals("DXCC_DELETED", StringComparison.OrdinalIgnoreCase) ||
                DxccContinentTokens.ContainsKey(universe))
                return ResolveClubLog(universe, clubLog, out error);

            if (universe.StartsWith("File:", StringComparison.OrdinalIgnoreCase))
                return ResolveFile(universe, listsFolder, out error);

            error = $"Unknown Universe: {universe}";
            return null;
        }

        private static HashSet<string> ResolveBuiltIn(string universe)
        {
            var cmp = StringComparer.OrdinalIgnoreCase;

            if (universe.Equals("US_50_STATES", StringComparison.OrdinalIgnoreCase))
                return new HashSet<string>(Us50States, cmp);
            if (universe.Equals("CA_PROVINCES", StringComparison.OrdinalIgnoreCase))
                return new HashSet<string>(CaProvinces, cmp);
            if (universe.Equals("CONTINENTS", StringComparison.OrdinalIgnoreCase))
                return new HashSet<string>(Continents, cmp);
            if (universe.Equals("SIX_CONTINENTS", StringComparison.OrdinalIgnoreCase))
                return new HashSet<string>(SixContinents, cmp);
            if (universe.Equals("CQ_ZONES", StringComparison.OrdinalIgnoreCase))
                return new HashSet<string>(CqZones, cmp);
            if (universe.Equals("ITU_ZONES", StringComparison.OrdinalIgnoreCase))
                return new HashSet<string>(ItuZones, cmp);

            return null;
        }

        private static HashSet<string> ResolveClubLog(string universe, ClubLogProvider clubLog, out string error)
        {
            error = null;
            if (clubLog == null || clubLog.EntityCount == 0)
            {
                error = "Club Log country data is not available yet (nothing downloaded so far). " +
                        "This universe will resolve automatically once the background download completes.";
                return null;
            }

            var cmp = StringComparer.OrdinalIgnoreCase;
            IEnumerable<ClubLogEntity> entities = clubLog.AllEntities;

            if (universe.Equals("DXCC_CURRENT", StringComparison.OrdinalIgnoreCase))
                entities = entities.Where(e => !e.Deleted);
            else if (universe.Equals("DXCC_DELETED", StringComparison.OrdinalIgnoreCase))
                entities = entities.Where(e => e.Deleted);
            else
            {
                string cont = DxccContinentTokens[universe];
                entities = entities.Where(e => !e.Deleted &&
                    string.Equals(e.Continent, cont, StringComparison.OrdinalIgnoreCase));
            }

            var set = new HashSet<string>(cmp);
            foreach (var e in entities)
                if (e.Adif > 0) set.Add(e.Adif.ToString());
            return set;
        }

        private static HashSet<string> ResolveFile(string universe, string listsFolder, out string error)
        {
            error = null;
            string fileName = universe.Substring("File:".Length).Trim();
            string path = Path.Combine(listsFolder, fileName);
            if (!File.Exists(path))
            {
                error = $"Universe file not found: {path}";
                return null;
            }

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;

                // Strip a trailing inline comment (e.g. "1  ; Canada").
                int c1 = line.IndexOf(';');
                int c2 = line.IndexOf('#');
                int cut = c1 >= 0 && c2 >= 0 ? Math.Min(c1, c2) : Math.Max(c1, c2);
                if (cut >= 0) line = line.Substring(0, cut).Trim();

                if (line.Length > 0) set.Add(line);
            }
            return set;
        }
    }
}
