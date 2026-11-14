using System.Globalization;

namespace WSJTX_Controller
{
    // Options > General "Distance units" (2026-11-12, GitHub #3): Automatic, Miles or Kilometers.
    // Same three choices and the same automatic rule as Nexus's own Settings.units: miles only
    // where Windows is set to the US, Liberia or Myanmar, kilometers everywhere else. Distances
    // are always worked out and ranked in kilometers; this changes only what is shown and spoken.
    // Kept in Jimmy Next's own profile ini (key distanceUnits), loaded by Controller.
    internal static class DistanceUnits
    {
        internal const string Auto = "auto";
        internal const string Miles = "miles";
        internal const string Kilometers = "km";

        private static string _setting = Auto;

        internal static string Setting
        {
            get => _setting;
            set => _setting = Normalize(value);
        }

        // Anything unknown or missing (a profile from before this setting) is Automatic.
        internal static string Normalize(string value)
        {
            string v = (value ?? "").Trim().ToLowerInvariant();
            return v == Miles || v == Kilometers ? v : Auto;
        }

        internal static bool UseMiles => UseMilesFor(_setting, RegionCode());

        internal static bool UseMilesFor(string setting, string region)
        {
            switch (Normalize(setting))
            {
                case Miles: return true;
                case Kilometers: return false;
                default:
                    string r = (region ?? "").Trim().ToUpperInvariant();
                    return r == "US" || r == "LR" || r == "MM";
            }
        }

        private static string RegionCode()
        {
            try { return RegionInfo.CurrentRegion.TwoLetterISORegionName; }
            catch { return ""; }
        }

        // Kilometers to the chosen unit, rounded. 0.6213 is the factor the lists always used.
        internal static int FromKm(double km, bool miles) => miles ? (int)(0.6213 * km + 0.5) : (int)(km + 0.5);
        internal static int FromKm(double km) => FromKm(km, UseMiles);

        internal static string ShortName => UseMiles ? "mi" : "km";
        internal static string LongName => UseMiles ? "miles" : "kilometers";
    }
}
