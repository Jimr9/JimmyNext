using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WSJTX_Controller
{
    // The wording file (operator request, 2026-09-30): the words of the spoken notification pieces
    // -- the receive side names, shared with the list titles, and the station tags in the lists --
    // live in Wording.txt in the
    // settings folder, read once at startup, so the operator changes a word without a code change.
    // A missing file, missing entry or unreadable line means the built-in wording. First start
    // writes the file with every entry commented out ("# key = words"); remove the '#' to change
    // one. {Name} placeholders are filled in by Jimmy. Never read in test mode. Screen labels and
    // buttons are a later release.
    internal static class Wording
    {
        internal const string FileName = "Wording.txt";

        // key, built-in words, what it is
        internal static readonly (string Key, string Default, string Note)[] Known =
        {
            ("Summary.Stations.One",  "available station",         "receive summary: after the count, one station"),
            ("Summary.Stations.Many", "available stations",        "receive summary: after the count, several stations"),
            ("Summary.None",          "no",                        "receive summary: the count when there are none (simple layout)"),
            ("Summary.ToYou",         "{Count} to you",            "stations calling you"),
            ("Summary.FirstInLine",   "{Call} first",              "the station that will be worked first"),
            ("Summary.NewDxcc",       "{Count} new DXCC",          "stations from a country never worked"),
            ("Summary.NewDxccOnBand", "{Count} new DXCC on band",  "stations from a country not yet worked on this band"),
            ("Summary.Wanted",        "{Count} wanted",            "wanted stations"),
            ("Summary.Award",         "{Count} {Award}",           "stations an award still needs; {Award} is the award's own name"),
            ("Side.TX1",              "TX1",                       "advanced layout: the first-period list when you transmit first"),
            ("Side.RX1",              "RX1",                       "advanced layout: the first-period list when you receive first"),
            ("Side.TX2",              "TX2",                       "advanced layout: the second-period list when you transmit second"),
            ("Side.RX2",              "RX2",                       "advanced layout: the second-period list when you receive second"),
            ("List.Title",            "{Side} available stations", "advanced layout: the label shown above each list"),
            ("List.TitleSpoken",      "{Side} available stations, {Count} calls", "advanced layout: what the screen reader says for each list; remove {Count} not to hear the count"),
            ("Tag.NewDxcc",           "New DXCC",                  "station tag: a country never worked"),
            ("Tag.NewDxccOnBand",     "New DXCC on band",          "station tag: a country not yet worked on this band"),
            ("Tag.Wanted",            "Wanted",                    "station tag: a call on your wanted list"),
            ("Tag.Pota",              "POTA",                      "station tag: a POTA activator"),
            ("Tag.Sota",              "SOTA",                      "station tag: a SOTA activator"),
            ("Tag.WasNeeded",         "WAS Needed",                "station tag: a state Worked All States still needs"),
            ("Tag.WasUnconf",         "WAS Unconf",                "station tag: a state worked but not confirmed"),
            ("Tag.DxccUnconf",        "DXCC Unconf",               "station tag: a country worked but not confirmed"),
            ("Tag.ZoneNeeded",        "Zone Needed",               "station tag: a CQ zone still needed"),
            ("Tag.AwardNeeded",       "{Award} Needed",            "station tag: any other award still needed; {Award} is its name"),
            ("Tag.AwardUnconf",       "{Award} Unconf",            "station tag: any other award worked but not confirmed"),
            ("Tag.CallingMe",         "Calling me",                "raw decodes tag: a station calling you"),
            ("Tag.Manual",            "Manual",                    "raw decodes tag: a station you picked"),
            ("Tag.DirCq",             "Dir CQ",                    "raw decodes tag: a directed CQ you want"),
            ("Tag.FoxHound",          "Possible F/H",              "raw decodes tag: possibly a Fox/Hound station"),
        };

        private static Dictionary<string, string> _overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static string Get(string key)
        {
            if (_overrides.TryGetValue(key, out string v)) return v;
            foreach (var k in Known) if (k.Key == key) return k.Default;
            return key;
        }

        internal static string Fill(string key, params (string Name, string Value)[] values)
        {
            string s = Get(key);
            foreach (var (name, value) in values) s = s.Replace("{" + name + "}", value ?? "");
            return s;
        }

        // Startup: read the file, or write it (all commented out) when there is none. Returns a
        // line for the debug log, or null.
        internal static string Load(string folder)
        {
            if (TestModeGuard.IsTestMode) return null;
            string path = Path.Combine(folder, FileName);
            try
            {
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, Template(), new UTF8Encoding(false));
                    _overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    return null;
                }
                string[] lines = File.ReadAllLines(path);
                _overrides = Parse(lines);
                // Entries added in a later version: appended, commented out, so the file always
                // lists everything that can be reworded. The operator's own lines are untouched.
                var listed = new HashSet<string>(lines.Select(l => l.TrimStart('#', ' ', '\t'))
                    .Where(l => l.Contains("=")).Select(l => l.Substring(0, l.IndexOf('=')).Trim()), StringComparer.OrdinalIgnoreCase);
                var missing = Known.Where(k => !listed.Contains(k.Key)).ToList();
                if (missing.Count > 0)
                {
                    var sb = new StringBuilder();
                    foreach (var (key, def, note) in missing)
                    {
                        sb.AppendLine();
                        sb.AppendLine("# " + note);
                        sb.AppendLine("# " + key + " = " + def);
                    }
                    File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
                }
                return _overrides.Count > 0 ? $"wording: {_overrides.Count} entr{(_overrides.Count == 1 ? "y" : "ies")} from {FileName}" : null;
            }
            catch (Exception ex)
            {
                _overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return $"wording: {FileName} not read ({ex.Message}); built-in wording used";
            }
        }

        // "key = words" lines; '#' starts a comment line; unknown keys and blank words are ignored.
        internal static Dictionary<string, string> Parse(IEnumerable<string> lines)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in lines ?? Enumerable.Empty<string>())
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string words = line.Substring(eq + 1).Trim();
                if (words.Length > 0 && Known.Any(k => k.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) d[key] = words;
            }
            return d;
        }

        internal static void SetForTest(Dictionary<string, string> overrides) =>
            _overrides = overrides ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static string Template()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Jimmy Next wording. Remove the '#' in front of a line and change the words after '='.");
            sb.AppendLine("# Read when Jimmy Next starts. A line left with '#' keeps the built-in wording.");
            sb.AppendLine("# Words in {braces} are filled in by Jimmy Next; keep them.");
            foreach (var (key, def, note) in Known)
            {
                sb.AppendLine();
                sb.AppendLine("# " + note);
                sb.AppendLine("# " + key + " = " + def);
            }
            return sb.ToString();
        }
    }
}
