using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WSJTX_Controller
{
    // Where a worked station IS, for the STATE a contact is logged with (2026-09-29: AF0EC, a POTA
    // activator in Idaho, grid DN43, was logged as MO -- his licence's mailing address. Nexus
    // fills a MISSING state from a LoTW/QRZ report but never overwrites one, so a wrong state
    // stays wrong). Two sources, both from the Nexus engine:
    //   - A POTA activation now on the air (the engine's spot cache): its park's location in the
    //     POTA directory ("US-ID") says exactly where the station is, and names the park.
    //   - Nexus's grid -> US state table: the grid heard on the air checks the mailing address.
    //     When they disagree the state is left BLANK -- a confirmation fills a blank correctly
    //     later; a wrong state is never corrected.
    internal static class StationLocation
    {
        private static volatile Dictionary<string, string> _gridStates;   // grid4 -> state; null until the engine answers
        private static volatile OtaSpot[] _spots = Array.Empty<OtaSpot>();
        private static DateTime _lastRefreshUtc = DateTime.MinValue;
        private static int _refreshing;
        private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(60);

        // Called on every engine poll: at most once a minute, off the UI thread, reads the engine's
        // POTA/SOTA spot cache (and Nexus's grid table, until it has it). Never in test mode.
        internal static void RefreshIfDue()
        {
            if (TestModeGuard.IsTestMode) return;
            if (DateTime.UtcNow - _lastRefreshUtc < RefreshEvery) return;
            if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
            _lastRefreshUtc = DateTime.UtcNow;
            Task.Run(() =>
            {
                try
                {
                    var client = new ExternalDataClient();
                    if (_gridStates == null)
                    {
                        var table = client.GetGridStates(out _);
                        if (table != null && table.Count > 0)
                            _gridStates = new Dictionary<string, string>(table, StringComparer.OrdinalIgnoreCase);
                    }
                    var spots = client.GetOtaSpots(out _);
                    if (spots?.Spots != null) _spots = spots.Spots;
                }
                catch { /* stays as it was; tried again next minute */ }
                finally { Interlocked.Exchange(ref _refreshing, 0); }
            }).ObserveFault();
        }

        // gridDat: a test's stand-in for WSJT-X's grid.dat (null with a grid table = no grid.dat).
        internal static void SetForTest(Dictionary<string, string> gridStates, OtaSpot[] spots, Dictionary<string, string> gridDat = null)
        {
            _gridStates = gridStates == null ? null : new Dictionary<string, string>(gridStates, StringComparer.OrdinalIgnoreCase);
            _spots = spots ?? Array.Empty<OtaSpot>();
            _testTables = gridStates != null || gridDat != null;   // a test's own tables, not this PC's grid.dat
            _testGridDat = gridDat == null ? null : new Dictionary<string, string>(gridDat, StringComparer.OrdinalIgnoreCase);
        }
        private static volatile bool _testTables;
        private static volatile Dictionary<string, string> _testGridDat;

        // Nexus's state for a heard grid (its dominant state for the 4-char square); null for a
        // grid outside the US, or before the engine has sent the table.
        internal static string GridState(string grid)
        {
            var table = _gridStates;
            if (table == null || string.IsNullOrWhiteSpace(grid) || grid.Trim().Length < 4) return null;
            return table.TryGetValue(grid.Trim().Substring(0, 4), out var st) ? st : null;
        }

        // The state to log (fix 2): the mailing-address state (homeState, the callsign lookup)
        // checked against the grid heard on the air. No grid heard: the mailing address, as before.
        // A grid outside the US, or one naming another state: blank. A grid heard before the engine
        // has sent the table (the first moments of a session, or a startup backfill running early):
        // blank too -- nothing to check it against, and a blank is filled again later.
        internal static string ResolveState(string homeState, string grid)
        {
            if (string.IsNullOrWhiteSpace(grid)) return Blank(homeState);
            if (_gridStates == null) return null;
            string heard = GridState(grid);
            if (heard == null) return null;
            if (string.IsNullOrWhiteSpace(homeState)) return heard;
            return string.Equals(homeState.Trim(), heard, StringComparison.OrdinalIgnoreCase) ? heard : null;
        }

        // The newest POTA spot of this station on this band, spotted within SuggestWithin of `atUtc`
        // (2026-10-04). A SUGGESTION only -- supporting evidence, never the logged park: a spot with
        // no time is not current, and an activator who moved parks is never joined into a two-fer.
        internal static readonly TimeSpan SuggestWithin = TimeSpan.FromMinutes(30);

        // Each park this station was spotted at on this band within SuggestWithin of `atUtc`, its
        // newest spot (2026-10-05). One park: it is logged, marked as from a spot and unconfirmed.
        // Several: none is logged -- an activator spotted at two parks is not proof of a two-fer.
        internal static List<OtaSpot> SpotParks(string call, string band, DateTime atUtc)
        {
            string key = CallKey(call);
            if (key.Length == 0 || string.IsNullOrEmpty(band)) return new List<OtaSpot>();
            long at = new DateTimeOffset(atUtc, TimeSpan.Zero).ToUnixTimeSeconds();
            return _spots.Where(s => s != null
                    && string.Equals(s.Program, "POTA", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(s.Reference)
                    && CallKey(s.Activator) == key
                    && string.Equals(SpotBand(s), band, StringComparison.OrdinalIgnoreCase)
                    && s.SpotTimeUnix.HasValue
                    && s.SpotTimeUnix.Value <= at + 60
                    && at - s.SpotTimeUnix.Value <= (long)SuggestWithin.TotalSeconds)
                .GroupBy(s => s.Reference.Trim().ToUpperInvariant())
                .Select(g => g.OrderByDescending(s => s.SpotTimeUnix.Value).First())
                .ToList();
        }

        // The US states a received grid lies in (2026-10-05) -- a 4-char square can straddle a
        // state line, so its main state is not proof. WSJT-X's grid.dat names the states of a
        // border square ("MN-WI") when it is installed: that answer is used as the grid's states.
        // Without it, Nexus's table (one state per square) with the states of the eight squares
        // around it -- only an ESTIMATE (estimate = true): it neither proves the grid lies in one
        // state nor rules another out. Null: not a US grid, or nothing known yet.
        internal static HashSet<string> GridStateSet(string grid) => GridStateSet(grid, out _);

        internal static HashSet<string> GridStateSet(string grid, out bool estimate)
        {
            estimate = false;
            if (string.IsNullOrWhiteSpace(grid) || grid.Trim().Length < 4) return null;
            string g4 = grid.Trim().Substring(0, 4).ToUpperInvariant();
            string dat = null;
            bool known = _testTables ? (_testGridDat?.TryGetValue(g4, out dat) ?? false) : UsGridStateMap.TryGetState(g4, out dat);
            if (known && !string.IsNullOrWhiteSpace(dat))
                return new HashSet<string>(dat.Split('-').Select(p => p.Trim().ToUpperInvariant()).Where(p => p.Length == 2), StringComparer.OrdinalIgnoreCase);
            estimate = true;
            var table = _gridStates;
            if (table == null || !table.TryGetValue(g4, out string own)) return null;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { own.ToUpperInvariant() };
            int lon = (g4[0] - 'A') * 10 + (g4[2] - '0'), lat = (g4[1] - 'A') * 10 + (g4[3] - '0');
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    int x = (lon + dx + 180) % 180, y = lat + dy;
                    if (y < 0 || y >= 180) continue;
                    string n = $"{(char)('A' + x / 10)}{(char)('A' + y / 10)}{x % 10}{y % 10}";
                    if (table.TryGetValue(n, out string st)) set.Add(st.ToUpperInvariant());
                }
            return set;
        }

        // A chosen park's state fits the received grid unless grid.dat says the grid lies only in
        // other states: no grid, a grid not known as US, or only an estimate never makes a
        // conflict (a park on a state line is valid).
        internal static bool ParkStateFits(string parkState, string grid)
        {
            if (string.IsNullOrWhiteSpace(grid)) return true;
            var set = GridStateSet(grid, out bool estimate);
            return set == null || estimate || set.Contains((parkState ?? "").Trim());
        }

        internal sealed class StateDecision { public string State, From, NotSet; }

        // The other station's US state on a contact Jimmy Next logs (2026-10-05), as conventional
        // loggers fill it, but only where it can hold, and always saying where it came from:
        //   - Alaska / Hawaii: the entity names its state.
        //   - the callbook state (FCC / QRZ, a mailing address), when no grid was received or the
        //     received grid may lie in that state (grid.dat), or only an estimate is available --
        //     not for a portable call (K1ABC/P, VE3/K1ABC) or a POTA contact, away from home;
        //   - else the received grid's state, when it lies in one state (grid.dat), or Nexus's
        //     table estimates one -- labelled an estimate;
        //   - else blank, with what the lookups said (NotSet). Never for a non-US entity.
        internal static StateDecision TheirState(string call, int dxcc, string grid, bool pota, string callbookState)
        {
            var d = new StateDecision();
            if (dxcc == 6) { d.State = "AK"; d.From = "DXCC entity Alaska"; return d; }
            if (dxcc == 110) { d.State = "HI"; d.From = "DXCC entity Hawaii"; return d; }
            if (dxcc != 291) return d;
            grid = (grid ?? "").Trim().ToUpperInvariant();
            string cb = (callbookState ?? "").Trim().ToUpperInvariant();
            if (cb.Length != 2 || !cb.All(char.IsLetter)) cb = null;
            bool portable = (call ?? "").Contains("/");
            bool estimate = false;
            var set = grid.Length >= 4 ? GridStateSet(grid, out estimate) : null;
            string Est() => $"grid {grid} estimated in {string.Join(" or ", set.OrderBy(x => x))} from Nexus's grid table, borders not known";
            if (cb != null && !portable && !pota)
            {
                if (grid.Length < 4) { d.State = cb; d.From = "callbook, no grid received"; return d; }
                if (set != null && set.Contains(cb)) { d.State = cb; d.From = estimate ? $"callbook; {Est()}" : $"callbook, grid {grid} agrees"; return d; }
                if (set != null && estimate) { d.State = cb; d.From = $"callbook; {Est()}, so not ruled out"; return d; }
            }
            if (set != null && set.Count == 1)
            {
                d.State = set.First();
                d.From = estimate ? $"received grid {grid}, estimated from Nexus's grid table (borders not known)" : $"received grid {grid}, which lies in one state";
                return d;
            }
            if (cb == null && grid.Length < 4) return d;   // nothing known: plain blank
            string why = cb == null ? "no callbook state"
                : $"callbook {cb}" + (portable ? " not used (portable call)" : pota ? " not used (POTA)" : "");
            string gridSays = grid.Length < 4 ? "no grid received"
                : set == null ? $"grid {grid} not in a known US state"
                : estimate ? Est()
                : $"grid {grid} lies in {string.Join(" or ", set.OrderBy(x => x))}";
            d.NotSet = $"STATE not set: {why}; {gridSays}";
            return d;
        }

        internal static OtaSpot NewestSpotPark(string call, string band, DateTime atUtc)
        {
            string key = CallKey(call);
            if (key.Length == 0 || string.IsNullOrEmpty(band)) return null;
            long at = new DateTimeOffset(atUtc, TimeSpan.Zero).ToUnixTimeSeconds();
            return _spots.Where(s => s != null
                    && string.Equals(s.Program, "POTA", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(s.Reference)
                    && CallKey(s.Activator) == key
                    && string.Equals(SpotBand(s), band, StringComparison.OrdinalIgnoreCase)
                    && s.SpotTimeUnix.HasValue
                    && s.SpotTimeUnix.Value <= at + 60
                    && at - s.SpotTimeUnix.Value <= (long)SuggestWithin.TotalSeconds)
                .OrderByDescending(s => s.SpotTimeUnix.Value)
                .FirstOrDefault();
        }

        // The station itself, without a portable marker: AF0EC/P and AF0EC match; KH8/N0CALL keeps
        // its longer side, the call.
        private static string CallKey(string call)
        {
            if (string.IsNullOrWhiteSpace(call)) return "";
            var parts = call.Trim().ToUpperInvariant().Split('/');
            return parts.OrderByDescending(p => p.Length).First();
        }

        private static string SpotBand(OtaSpot s) =>
            s.FreqKhz > 0 ? AdifImporter.NormalizeBand("", (s.FreqKhz / 1000.0).ToString("0.000###", CultureInfo.InvariantCulture)) : "";

        private static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
