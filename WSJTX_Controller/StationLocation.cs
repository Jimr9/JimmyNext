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
        private static readonly TimeSpan SpotFreshFor = TimeSpan.FromMinutes(60);

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

        internal static void SetForTest(Dictionary<string, string> gridStates, OtaSpot[] spots)
        {
            _gridStates = gridStates == null ? null : new Dictionary<string, string>(gridStates, StringComparer.OrdinalIgnoreCase);
            _spots = spots ?? Array.Empty<OtaSpot>();
        }

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

        // A POTA activation of this station on this band, spotted within the last hour (fix 1): its
        // park reference(s), and the state when every park is in one and the same US state.
        internal static bool TryFindActivation(string call, string band, out string parkRefs, out string state)
        {
            parkRefs = null; state = null;
            string key = CallKey(call);
            if (key.Length == 0 || string.IsNullOrEmpty(band)) return false;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var activations = _spots.Where(s => s != null
                && string.Equals(s.Program, "POTA", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(s.Reference)
                && CallKey(s.Activator) == key
                && string.Equals(SpotBand(s), band, StringComparison.OrdinalIgnoreCase)
                && (!s.SpotTimeUnix.HasValue || now - s.SpotTimeUnix.Value <= (long)SpotFreshFor.TotalSeconds))
                .ToList();
            if (activations.Count == 0) return false;
            parkRefs = string.Join(",", activations.Select(s => s.Reference.Trim().ToUpperInvariant()).Distinct());
            var states = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in activations)
            {
                var places = (s.Location ?? "").Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                if (places.Count != 1 || !places[0].StartsWith("US-", StringComparison.OrdinalIgnoreCase)) { states.Clear(); states.Add("?"); break; }
                states.Add(places[0].Substring(3).ToUpperInvariant());
            }
            if (states.Count == 1 && !states.Contains("?")) state = states.First();
            return true;
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
