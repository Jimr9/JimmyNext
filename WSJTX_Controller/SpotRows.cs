using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WSJTX_Controller
{
    // What Jimmy knows about a spot beyond what the feed says (2026-10-12), in one place for the
    // POTA/SOTA, DX Spots and Who Hears Me lists:
    //
    //   - WHERE AN ACTIVATION IS. The state or province of the park or summit, from the feed
    //     (the engine sends it as "US-ID", or several for a park on a state line) -- never the
    //     activator's home address. One place is said as that place; several as "Idaho or
    //     Wyoming"; none as nothing at all, because unknown stays unknown.
    //   - WHAT IT COUNTS FOR. Every checked award the station is still needed for, or worked but
    //     not confirmed for, ON THE SPOT'S OWN BAND, in the award's own short name and the same
    //     Needed / Unconf / Wanted words the station lists use (AwardTagger.AwardLabel). For a
    //     state award the activation's place decides: one place gives a plain label, several
    //     give "possibly", none gives no label. A home state is used only for a station that is
    //     not activating anything.
    //   - THE ROW. Each list's row is built from named fields in an order the operator can
    //     change (Row Order), as the station lists already allow. Every field is in the row's
    //     text, so a screen reader reads the whole row.
    internal static class SpotRows
    {
        // ── Bands ────────────────────────────────────────────────────────────────────────────

        private static readonly (double Lo, double Hi, string Band)[] Bands =
        {
            (1800, 2000, "160m"), (3500, 4000, "80m"), (5250, 5450, "60m"), (7000, 7300, "40m"), (10100, 10150, "30m"),
            (14000, 14350, "20m"), (18068, 18168, "17m"), (21000, 21450, "15m"), (24890, 24990, "12m"), (28000, 29700, "10m"),
            (50000, 54000, "6m"), (70000, 71000, "4m"), (144000, 148000, "2m"), (222000, 225000, "1.25m"), (420000, 450000, "70cm"),
        };

        // The ham band a frequency in kHz is on, or null when it is on none.
        internal static string BandOfKhz(double khz)
        {
            foreach (var (lo, hi, band) in Bands) if (khz >= lo && khz <= hi) return band;
            return null;
        }

        // ── Places ───────────────────────────────────────────────────────────────────────────

        private static readonly Dictionary<string, string> PlaceNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["US-AL"] = "Alabama", ["US-AK"] = "Alaska", ["US-AZ"] = "Arizona", ["US-AR"] = "Arkansas", ["US-CA"] = "California", ["US-CO"] = "Colorado",
            ["US-CT"] = "Connecticut", ["US-DE"] = "Delaware", ["US-DC"] = "District of Columbia", ["US-FL"] = "Florida", ["US-GA"] = "Georgia",
            ["US-HI"] = "Hawaii", ["US-ID"] = "Idaho", ["US-IL"] = "Illinois", ["US-IN"] = "Indiana", ["US-IA"] = "Iowa", ["US-KS"] = "Kansas",
            ["US-KY"] = "Kentucky", ["US-LA"] = "Louisiana", ["US-ME"] = "Maine", ["US-MD"] = "Maryland", ["US-MA"] = "Massachusetts",
            ["US-MI"] = "Michigan", ["US-MN"] = "Minnesota", ["US-MS"] = "Mississippi", ["US-MO"] = "Missouri", ["US-MT"] = "Montana",
            ["US-NE"] = "Nebraska", ["US-NV"] = "Nevada", ["US-NH"] = "New Hampshire", ["US-NJ"] = "New Jersey", ["US-NM"] = "New Mexico",
            ["US-NY"] = "New York", ["US-NC"] = "North Carolina", ["US-ND"] = "North Dakota", ["US-OH"] = "Ohio", ["US-OK"] = "Oklahoma",
            ["US-OR"] = "Oregon", ["US-PA"] = "Pennsylvania", ["US-RI"] = "Rhode Island", ["US-SC"] = "South Carolina", ["US-SD"] = "South Dakota",
            ["US-TN"] = "Tennessee", ["US-TX"] = "Texas", ["US-UT"] = "Utah", ["US-VT"] = "Vermont", ["US-VA"] = "Virginia", ["US-WA"] = "Washington",
            ["US-WV"] = "West Virginia", ["US-WI"] = "Wisconsin", ["US-WY"] = "Wyoming",
            ["CA-AB"] = "Alberta", ["CA-BC"] = "British Columbia", ["CA-MB"] = "Manitoba", ["CA-NB"] = "New Brunswick",
            ["CA-NL"] = "Newfoundland and Labrador", ["CA-NS"] = "Nova Scotia", ["CA-NT"] = "Northwest Territories", ["CA-NU"] = "Nunavut",
            ["CA-ON"] = "Ontario", ["CA-PE"] = "Prince Edward Island", ["CA-QC"] = "Quebec", ["CA-SK"] = "Saskatchewan", ["CA-YT"] = "Yukon",
        };

        // One place as the engine names it ("US-ID", "CA-ON"): a US state, DC, or a Canadian
        // province. Anything else -- another country's region, a code that is no state -- is no
        // place here, so "AU-WA" is never read as Washington.
        internal static string CanonicalPlace(string code)
        {
            string c = (code ?? "").Trim().ToUpperInvariant();
            return PlaceNames.ContainsKey(c) ? c : null;
        }

        // The places an activation may be in. The engine's own list when it sends one (Nexus's
        // park and summit data); else the park directory's location text, read by the same rule.
        internal static List<string> Places(OtaSpot spot)
        {
            IEnumerable<string> raw = spot?.States != null && spot.States.Count > 0
                ? spot.States
                : (spot?.Location ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            return raw.Select(CanonicalPlace).Where(p => p != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static string PlaceName(string place) => PlaceNames.TryGetValue(place ?? "", out string n) ? n : place;
        internal static bool IsUsState(string place) => (place ?? "").StartsWith("US-", StringComparison.OrdinalIgnoreCase);
        // "US-ID" -> "ID": the code an award's own list of states or provinces holds.
        internal static string PlaceCode(string place) => place != null && place.Length > 3 ? place.Substring(3) : "";

        // "Idaho"; "Idaho or Wyoming"; "Idaho, chosen" when the operator picked it for a park in
        // several; "" when nothing is known.
        internal static string LocationText(IList<string> places, string chosenUsState)
        {
            if (!string.IsNullOrEmpty(chosenUsState) && places != null && places.Any(p => string.Equals(p, "US-" + chosenUsState, StringComparison.OrdinalIgnoreCase)))
                return PlaceName("US-" + chosenUsState.ToUpperInvariant()) + (places.Count > 1 ? ", chosen" : "");
            if (places == null || places.Count == 0) return "";
            return string.Join(" or ", places.Select(PlaceName));
        }

        // ── Award labels ─────────────────────────────────────────────────────────────────────

        internal sealed class Facts
        {
            public string Call;
            public string Band;                      // the SPOT's band, or null when unknown
            public bool Activation;                  // a park or summit: only its own place counts for a state award
            public IList<string> Places;             // "US-ID", ... (activation), or null
            public string ChosenUsState;             // the operator's own choice for a park in several states
            public string HomeState, Continent;      // from the callsign lookup; HomeState is ignored for an activation
            public int CqZone, Dxcc;
            public bool Wanted;                      // on the operator's wanted-calls list
        }

        // Every label that applies, in the order the awards are checked, each once. tags: the
        // checked awards as they stand for the spot's band (Controller.AwardTagsForBand).
        internal static List<string> AwardLabels(Facts f, IEnumerable<WsjtxClient.ActiveAwardTag> tags)
        {
            var labels = new List<string>();
            void Add(string label) { if (!string.IsNullOrWhiteSpace(label) && !labels.Contains(label)) labels.Add(label); }
            if (f == null || string.IsNullOrEmpty(f.Call)) return labels;
            if (f.Wanted) Add(Wording.Get("Tag.Wanted"));
            if (tags == null) return labels;

            // The states that can be judged: an activation's own place(s), never its home state.
            List<string> states;
            if (f.Activation)
                states = !string.IsNullOrEmpty(f.ChosenUsState) ? new List<string> { f.ChosenUsState.ToUpperInvariant() }
                       : (f.Places ?? new List<string>()).Select(PlaceCode).Where(c => c.Length > 0).ToList();
            else
                states = string.IsNullOrEmpty(f.HomeState) ? new List<string>() : new List<string> { f.HomeState };

            foreach (var tag in tags)
            {
                if (tag == null) continue;
                bool Has(HashSet<string> set, out bool possibly)
                {
                    possibly = false;
                    if (set == null || set.Count == 0) return false;
                    switch (tag.GroupBy)
                    {
                        case RuleGroupBy.Callsign: return set.Contains(f.Call);
                        case RuleGroupBy.State:
                        {
                            // A border value ("MN-WI") matches when either state is in the set
                            // (UsGridStateMap.StateSetContains), exactly as for a live decode.
                            var hits = states.Where(s => UsGridStateMap.StateSetContains(s, set)).ToList();
                            if (hits.Count == 0) return false;
                            possibly = hits.Count < states.Count;   // one of several possible places counts, another does not
                            return true;
                        }
                        case RuleGroupBy.Continent: return !string.IsNullOrEmpty(f.Continent) && set.Contains(f.Continent);
                        case RuleGroupBy.CqZone: return f.CqZone > 0 && set.Contains(f.CqZone.ToString(CultureInfo.InvariantCulture));
                        case RuleGroupBy.Dxcc: return f.Dxcc > 0 && set.Contains(f.Dxcc.ToString(CultureInfo.InvariantCulture));
                        default: return false;
                    }
                }
                // Needed first; a station still needed is never reported as merely unconfirmed.
                if (Has(tag.Set, out bool maybeNeeded))
                    Add((maybeNeeded ? "possibly " : "") + AwardTagger.AwardLabel(tag.RuleId, tag.ShortName, tag.RuleName, needed: true));
                else if (Has(tag.UnconfirmedSet, out bool maybeUnconf))
                    Add((maybeUnconf ? "possibly " : "") + AwardTagger.AwardLabel(tag.RuleId, tag.ShortName, tag.RuleName, needed: false));
            }
            return labels;
        }

        // ── Rows ─────────────────────────────────────────────────────────────────────────────

        internal sealed class FieldSet
        {
            public string[] Defaults;                            // the fields a new install shows, in order
            public string[] All;                                 // every field this list has
            public Dictionary<string, string> Labels;            // what the Row Order window calls each
        }

        internal static readonly FieldSet Pota = new FieldSet
        {
            Defaults = new[] { "program", "reference", "callsign", "location", "freqmode", "age", "status", "awards" },
            All = new[] { "program", "reference", "callsign", "location", "freqmode", "age", "status", "awards", "band", "name" },
            Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["program"] = "Program (POTA or SOTA)", ["reference"] = "Park or summit reference", ["callsign"] = "Activator callsign",
                ["location"] = "State or province", ["freqmode"] = "Frequency and mode", ["age"] = "Age of the spot",
                ["status"] = "Worked before or not", ["awards"] = "Award and wanted labels", ["band"] = "Band", ["name"] = "Park or summit name",
            },
        };

        internal static readonly FieldSet Dx = new FieldSet
        {
            Defaults = new[] { "callsign", "freq", "mode", "spotter", "age", "comment", "status", "awards" },
            All = new[] { "callsign", "freq", "mode", "spotter", "age", "comment", "status", "awards", "band" },
            Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["callsign"] = "DX callsign", ["freq"] = "Frequency", ["mode"] = "Reported mode", ["spotter"] = "Spotter", ["age"] = "Age of the spot",
                ["comment"] = "Comment", ["status"] = "Worked before or not", ["awards"] = "Award and wanted labels", ["band"] = "Band",
            },
        };

        internal static readonly FieldSet Heard = new FieldSet
        {
            Defaults = new[] { "callsign", "grid", "band", "snr", "distance", "direction", "age" },
            All = new[] { "callsign", "grid", "band", "snr", "distance", "direction", "age" },
            Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["callsign"] = "Receiving station", ["grid"] = "Grid", ["band"] = "Band", ["snr"] = "Signal report",
                ["distance"] = "Distance", ["direction"] = "Direction", ["age"] = "Age of the report",
            },
        };

        // Fields in order, each with nothing to say left out. A field joins the one before it
        // with ", " -- except a pair that reads as one thing (a grid after its callsign, a
        // direction after its distance), which joins with a space, as those rows always read.
        private static string Join(IEnumerable<string> fields, Func<string, string> text, params (string First, string Then)[] pairs)
        {
            var sb = new System.Text.StringBuilder();
            string previous = null;
            foreach (string field in fields)
            {
                string t = text(field);
                if (string.IsNullOrEmpty(t)) continue;
                if (sb.Length > 0) sb.Append(pairs.Any(p => p.First == previous && p.Then == field) ? " " : ", ");
                sb.Append(t);
                previous = field;
            }
            return sb.ToString();
        }

        internal sealed class SpotNotes
        {
            public bool LogReady = true, WorkedBefore;
            public string Location = "";                 // LocationText
            public List<string> Labels = new List<string>();
        }

        internal static string StatusText(SpotNotes n) =>
            n == null ? "" : !n.LogReady ? "log loading" : n.WorkedBefore ? "worked" : "not worked";

        internal static string PotaRow(OtaSpot spot, SpotNotes n, IList<string> fields, string age)
        {
            return Join(fields ?? Pota.Defaults, field =>
            {
                switch (field)
                {
                    case "program": return spot.Program;
                    case "reference": return "Reference: " + spot.Reference;
                    case "callsign": return "Activator: " + spot.Activator;
                    case "location": return string.IsNullOrEmpty(n?.Location) ? "" : "Location: " + n.Location;
                    case "freqmode": return $"Freq/Mode: {spot.FreqKhz / 1000.0:0.000} {spot.Mode}";
                    case "band": return BandOfKhz(spot.FreqKhz) ?? "";
                    case "age": return "Age: " + age;
                    case "status": return n == null ? "" : "Status: " + StatusText(n);
                    case "awards": return n == null || n.Labels.Count == 0 ? "" : string.Join(", ", n.Labels);
                    case "name": return string.IsNullOrWhiteSpace(spot.Name) ? "" : spot.Name.Trim();
                    default: return "";
                }
            });
        }

        internal static string DxRow(DxSpot s, SpotNotes n, IList<string> fields, string age)
        {
            return Join(fields ?? Dx.Defaults, field =>
            {
                switch (field)
                {
                    case "callsign": return "DX Call: " + s.DxCall;
                    case "freq": return $"Frequency: {s.FreqKhz / 1000.0:0.000} MHz";
                    case "mode": return "Mode: " + (s.Rbn ? (s.SkimmerMode ?? "RBN") : "");
                    case "band": return BandOfKhz(s.FreqKhz) ?? "";
                    case "spotter": return "Spotter: " + s.Spotter;
                    case "age": return "Age: " + age;
                    case "comment": return "Comment: " + s.Comment;
                    case "status": return n == null ? "" : "Status: " + StatusText(n);
                    case "awards": return n == null || n.Labels.Count == 0 ? "" : string.Join(", ", n.Labels);
                    default: return "";
                }
            });
        }

        internal static string HeardRow(HeardMe h, IList<string> fields, string age)
        {
            bool hasGrid = !string.IsNullOrEmpty(h.Grid);
            return Join(fields ?? Heard.Defaults, field =>
            {
                switch (field)
                {
                    case "callsign": return h.Call;
                    case "grid": return hasGrid ? h.Grid : "";
                    case "band": return h.Band;
                    case "snr": return h.Snr.HasValue ? $"{h.Snr.Value:+0;-0;0} dB" : "SNR not reported";
                    case "distance": return hasGrid ? $"{h.Km:N0} km" : "distance unknown";
                    case "direction": return hasGrid ? h.Octant : "";
                    case "age": return age;
                    default: return "";
                }
            }, ("callsign", "grid"), ("distance", "direction"));
        }
    }
}
