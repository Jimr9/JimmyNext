using System;
using System.Collections.Generic;
using System.Linq;

namespace WSJTX_Controller
{
    // The park the OPERATOR chose for a station they are hunting (2026-10-04): set from the Spots
    // window, one per callsign -- so several Smart Mode stations each keep their own (Nexus's own
    // hunt target holds one for the whole engine). Optional: no choice simply logs SIG=POTA with
    // the park blank. Used once, by that station's next logged contact; a choice older than
    // MaxAge is stale and ignored. A spot is evidence of what the operator chose, never a choice
    // made for them.
    internal static class ParkChoices
    {
        internal static readonly TimeSpan MaxAge = TimeSpan.FromHours(2);

        internal sealed class Choice
        {
            public string Reference;   // as chosen, e.g. "US-1234"
            public string State;       // the park's state when it lies in exactly one; else null
            public DateTime ChosenUtc;
        }

        private static readonly Dictionary<string, Choice> _byCall = new Dictionary<string, Choice>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new object();

        // The station itself without a portable marker: K5BTM/P and K5BTM are one station.
        internal static string CallKey(string call)
        {
            if (string.IsNullOrWhiteSpace(call)) return "";
            return call.Trim().ToUpperInvariant().Split('/').OrderByDescending(p => p.Length).First();
        }

        // A park's location as the POTA directory gives it ("US-ID", or "US-ID,US-WY" for a park in
        // two): the state only when it is exactly one US state.
        internal static string SingleUsState(string location)
        {
            var places = (location ?? "").Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            return places.Count == 1 && places[0].StartsWith("US-", StringComparison.OrdinalIgnoreCase)
                ? places[0].Substring(3).ToUpperInvariant() : null;
        }

        internal static void Choose(string call, string reference, string parkLocation, DateTime? nowUtc = null)
        {
            string key = CallKey(call);
            if (key.Length == 0 || string.IsNullOrWhiteSpace(reference)) return;
            lock (_lock)
                _byCall[key] = new Choice
                {
                    Reference = reference.Trim().ToUpperInvariant(),
                    State = SingleUsState(parkLocation),
                    ChosenUtc = nowUtc ?? DateTime.UtcNow,
                };
        }

        // The current choice for this station, without using it up.
        internal static Choice Peek(string call, DateTime? nowUtc = null)
        {
            lock (_lock)
                return _byCall.TryGetValue(CallKey(call), out var c) && (nowUtc ?? DateTime.UtcNow) - c.ChosenUtc <= MaxAge ? c : null;
        }

        // The choice for this station's contact being logged, used up; null when none or stale.
        internal static Choice Take(string call, DateTime? nowUtc = null)
        {
            string key = CallKey(call);
            lock (_lock)
            {
                if (!_byCall.TryGetValue(key, out var c)) return null;
                _byCall.Remove(key);
                return (nowUtc ?? DateTime.UtcNow) - c.ChosenUtc <= MaxAge ? c : null;
            }
        }

        internal static void Clear(string call) { lock (_lock) _byCall.Remove(CallKey(call)); }
        internal static void ClearAll() { lock (_lock) _byCall.Clear(); }
    }
}
