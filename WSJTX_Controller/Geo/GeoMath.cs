using System;

namespace WSJTX_Controller
{
    // Migration Stage A2 (Jimmy_Master_Migration_Roadmap.md): standard Maidenhead-grid
    // -> lat/lon -> great-circle bearing/distance math -- the one piece of
    // EnqueueDecodeMessage's wire-supplied fields (Azimuth, Distance) that is genuinely
    // new code for Jimmy, per the Phase 4 dependency audit (zero existing great-circle/
    // bearing math anywhere in Jimmy before this). Parallel-validation only: nothing
    // downstream (CallQueueRanker.cs's beam-heading/distance-sort ranking math) reads
    // this yet, and this stage does not change that.
    //
    // Distance is returned in kilometers and Azimuth in degrees [0, 360), matching the
    // units of the existing wire-supplied EnqueueDecodeMessage.Distance/.Azimuth fields
    // (CallQueueRanker.cs's EarthDiameter=40000 constant is Earth's circumference in km,
    // confirming the wire convention this must match).
    public static class GeoMath
    {
        private const double EarthRadiusKm = 6371.0;

        // Maidenhead locator -> center-of-square (lat, lon) in degrees. Supports 2, 4, or
        // 6-character locators (the trailing pair(s) are optional and each add finer
        // resolution); a locator shorter than 2 characters or otherwise malformed returns
        // null rather than throwing, so a bad/missing grid degrades to "can't compute"
        // instead of crashing a decode-classification path.
        public static (double lat, double lon)? GridToLatLon(string grid)
        {
            if (string.IsNullOrEmpty(grid)) return null;
            grid = grid.Trim();
            if (grid.Length < 2 || grid.Length == 3 || grid.Length == 5 || grid.Length > 6) return null;

            char lonField = char.ToUpperInvariant(grid[0]);
            char latField = char.ToUpperInvariant(grid[1]);
            if (lonField < 'A' || lonField > 'R' || latField < 'A' || latField > 'R') return null;

            double lon = (lonField - 'A') * 20.0 - 180.0;
            double lat = (latField - 'A') * 10.0 - 90.0;
            // Center-of-field default (used when the locator has no digit pair at all,
            // i.e. a 2-character locator): half of a 20x10 degree field.
            double lonSpan = 20.0;
            double latSpan = 10.0;

            if (grid.Length >= 4)
            {
                if (!char.IsDigit(grid[2]) || !char.IsDigit(grid[3])) return null;
                int lonSquare = grid[2] - '0';
                int latSquare = grid[3] - '0';
                lon += lonSquare * 2.0;
                lat += latSquare * 1.0;
                lonSpan = 2.0;
                latSpan = 1.0;

                if (grid.Length == 6)
                {
                    char lonSub = char.ToLowerInvariant(grid[4]);
                    char latSub = char.ToLowerInvariant(grid[5]);
                    if (lonSub < 'a' || lonSub > 'x' || latSub < 'a' || latSub > 'x') return null;
                    lon += (lonSub - 'a') * (2.0 / 24.0);
                    lat += (latSub - 'a') * (1.0 / 24.0);
                    lonSpan = 2.0 / 24.0;
                    latSpan = 1.0 / 24.0;
                }
            }

            // Center of whatever the smallest resolved square is.
            lon += lonSpan / 2.0;
            lat += latSpan / 2.0;

            return (lat, lon);
        }

        private static double DegToRad(double deg) => deg * Math.PI / 180.0;
        private static double RadToDeg(double rad) => rad * 180.0 / Math.PI;

        // Great-circle distance in kilometers between two lat/lon points (haversine).
        public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
        {
            double dLat = DegToRad(lat2 - lat1);
            double dLon = DegToRad(lon2 - lon1);
            double rlat1 = DegToRad(lat1);
            double rlat2 = DegToRad(lat2);

            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(rlat1) * Math.Cos(rlat2) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return EarthRadiusKm * c;
        }

        // Initial great-circle bearing in degrees [0, 360) from point 1 to point 2.
        // 0 = due north, 90 = due east, 180 = due south, 270 = due west.
        public static double AzimuthDegrees(double lat1, double lon1, double lat2, double lon2)
        {
            double rlat1 = DegToRad(lat1);
            double rlat2 = DegToRad(lat2);
            double dLon = DegToRad(lon2 - lon1);

            double y = Math.Sin(dLon) * Math.Cos(rlat2);
            double x = Math.Cos(rlat1) * Math.Sin(rlat2) -
                       Math.Sin(rlat1) * Math.Cos(rlat2) * Math.Cos(dLon);
            double bearing = RadToDeg(Math.Atan2(y, x));
            return (bearing + 360.0) % 360.0;
        }

        // Convenience overload: both stations given as Maidenhead grid squares. Returns
        // null if either grid can't be parsed (matches GridToLatLon's degrade-gracefully
        // contract -- a bad/missing grid must not throw out of a decode-classification
        // path).
        public static (double distanceKm, double azimuthDeg)? DistanceAndAzimuth(string fromGrid, string toGrid)
        {
            var from = GridToLatLon(fromGrid);
            var to = GridToLatLon(toGrid);
            if (from == null || to == null) return null;

            double dist = DistanceKm(from.Value.lat, from.Value.lon, to.Value.lat, to.Value.lon);
            double az = AzimuthDegrees(from.Value.lat, from.Value.lon, to.Value.lat, to.Value.lon);
            return (dist, az);
        }
    }
}
