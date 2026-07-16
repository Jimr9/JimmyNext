using System;

namespace WSJTX_Controller
{
    // Migration Stage A2 (Jimmy_Master_Migration_Roadmap.md): standard Maidenhead-grid
    // -> lat/lon -> great-circle bearing/distance math -- the one piece of
    // EnqueueDecodeMessage's wire-supplied fields (Azimuth, Distance) that is genuinely
    // new code for Jimmy, per the Phase 4 dependency audit (zero existing great-circle/
    // bearing math anywhere in Jimmy before this).
    //
    // Distance is returned in kilometers and Azimuth in degrees [0, 360), matching the
    // units of the existing wire-supplied EnqueueDecodeMessage.Distance/.Azimuth fields
    // (CallQueueRanker.cs's EarthDiameter=40000 constant is Earth's circumference in km,
    // confirming the wire convention this must match).
    //
    // Found via live A6 field testing 2026-07-16: the small but consistent Distance/
    // Azimuth drift against WSJT-X's own wire-supplied values (e.g. ~2% off) traced to
    // this class originally using a spherical Haversine approximation (mean Earth
    // radius 6371 km), while WSJT-X itself computes distance/azimuth using Thomas's
    // 1970 spheroidal geodesic formula on the Clarke 1866 reference ellipsoid --
    // confirmed by reading WSJT-X's own source (lib/geodist.f90/lib/azdist.f90, via
    // Andy WM8Q's fork, github.com/avantol/WSJT-X_3.0.0 -- an unmodified copy of
    // stock WSJT-X's own geodesy code). Ported here line-for-line, independently
    // cross-validated against a separate Python transcription of the same Fortran
    // before being trusted (see the Stage-A6-geodesic-match commit message for the
    // validation used) -- not a from-scratch reimplementation.
    public static class GeoMath
    {
        // Clarke 1866 ellipsoid (meters) -- the specific reference ellipsoid WSJT-X's
        // own geodist.f90 uses (Thomas, P.D., 1970, "Spheroidal geodesics, reference
        // systems, & local geometry", U.S. Naval Oceanographic Office SP-138).
        private const double EquatorialRadiusM = 6378206.4;
        private const double PolarRadiusM = 6356583.8;

        // Maidenhead locator -> (lat, lon) in degrees. Supports 2, 4, or 6-character
        // locators (the trailing pair(s) are optional and each add finer resolution);
        // a locator shorter than 2 characters or otherwise malformed returns null
        // rather than throwing, so a bad/missing grid degrades to "can't compute"
        // instead of crashing a decode-classification path. Longitude is standard East-
        // positive (e.g. a North American station is negative) -- WSJT-X's own grid2deg.f90
        // returns the opposite (West-positive) convention; DistanceAndAzimuth/DistanceKm/
        // AzimuthDegrees below negate internally before calling the ported geodesic math,
        // so this method's own contract/sign convention is unchanged by that fix.
        //
        // For a 4-character locator (no sub-square letters -- by far the most common
        // length actually exchanged over FT8/FT4), WSJT-X's own grid2deg.f90 does NOT
        // resolve to the geometric center of the 2x1 degree square: it defaults the
        // missing sub-square pair to "mm" and then computes that sub-square's own
        // center, which lands about 1-2 arcminutes off the true box center. Replicated
        // here (lonSub/latSub default to 12, i.e. 'm') so a plain 4-character grid
        // produces the identical lat/lon -- and therefore identical Distance/Azimuth --
        // that WSJT-X itself displays for the same grid, rather than a slightly
        // different but individually-reasonable value. Confirmed via cross-validation
        // against a Python port of WSJT-X's own geodist.f90/grid2deg.f90 during the
        // Stage-A6-geodesic-match work (2026-07-16): this was the entire source of a
        // small (~0.03%) Distance/Azimuth drift that remained after switching from
        // spherical Haversine to this ellipsoidal formula.
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

            if (grid.Length < 4)
            {
                // 2-character-only locator (field only, no square digits) -- WSJT-X's
                // own routine doesn't really support this shorter form in practice
                // (decode messages always carry at least a 4-character grid); keep
                // Jimmy's own reasonable fallback of the geometric field center.
                lon += 20.0 / 2.0;
                lat += 10.0 / 2.0;
                return (lat, lon);
            }

            if (!char.IsDigit(grid[2]) || !char.IsDigit(grid[3])) return null;
            int lonSquare = grid[2] - '0';
            int latSquare = grid[3] - '0';
            lon += lonSquare * 2.0;
            lat += latSquare * 1.0;

            int lonSub = 12;
            int latSub = 12;

            if (grid.Length == 6)
            {
                char lonSubCh = char.ToLowerInvariant(grid[4]);
                char latSubCh = char.ToLowerInvariant(grid[5]);
                if (lonSubCh < 'a' || lonSubCh > 'x' || latSubCh < 'a' || latSubCh > 'x') return null;
                lonSub = lonSubCh - 'a';
                latSub = latSubCh - 'a';
            }

            lon += (lonSub + 0.5) * (2.0 / 24.0);
            lat += (latSub + 0.5) * (1.0 / 24.0);

            return (lat, lon);
        }

        private static double DegToRad(double deg) => deg * Math.PI / 180.0;

        // Great-circle (strictly: geodesic) distance in kilometers between two lat/lon
        // points, standard East-positive longitude. See EllipsoidalGeodesic for the
        // actual math.
        public static double DistanceKm(double lat1, double lon1, double lat2, double lon2) =>
            EllipsoidalGeodesic(lat1, lon1, lat2, lon2).distKm;

        // Initial geodesic bearing in degrees [0, 360) from point 1 to point 2, standard
        // East-positive longitude. 0 = due north, 90 = due east, 180 = due south,
        // 270 = due west. See EllipsoidalGeodesic for the actual math.
        public static double AzimuthDegrees(double lat1, double lon1, double lat2, double lon2) =>
            EllipsoidalGeodesic(lat1, lon1, lat2, lon2).azDeg;

        // Convenience overload: both stations given as Maidenhead grid squares. Returns
        // null if either grid can't be parsed (matches GridToLatLon's degrade-gracefully
        // contract -- a bad/missing grid must not throw out of a decode-classification
        // path). Computes distance and azimuth together in one pass (EllipsoidalGeodesic
        // derives both from the same intermediate terms), rather than the two separate
        // calls DistanceKm/AzimuthDegrees above would need.
        public static (double distanceKm, double azimuthDeg)? DistanceAndAzimuth(string fromGrid, string toGrid)
        {
            var from = GridToLatLon(fromGrid);
            var to = GridToLatLon(toGrid);
            if (from == null || to == null) return null;

            var result = EllipsoidalGeodesic(from.Value.lat, from.Value.lon, to.Value.lat, to.Value.lon);
            return (result.distKm, result.azDeg);
        }

        // Thomas, P.D., 1970, "Spheroidal geodesics, reference systems, & local
        // geometry", U.S. Naval Oceanographic Office SP-138 -- the exact formula WSJT-X
        // itself uses (lib/geodist.f90), ported line-for-line onto the Clarke 1866
        // ellipsoid. lat1/lon1 = "my" position, lat2/lon2 = "the other station's"
        // position, both standard East-positive longitude (this method negates to
        // WSJT-X's own West-positive internal convention before running the ported
        // math, so the two implementations operate on identical values).
        //
        // azDeg is the forward bearing from point 1 toward point 2; bazDeg is the
        // reverse bearing (point 2 back toward point 1) -- WSJT-X computes and exposes
        // both (Az/BAz in geodist.f90) even though Jimmy currently only consumes azDeg;
        // bazDeg is kept in the return tuple in case a future caller needs it (e.g. a
        // "beam heading back to me" display) rather than silently discarding data
        // WSJT-X itself treats as a first-class output of the same calculation.
        private static (double azDeg, double bazDeg, double distKm) EllipsoidalGeodesic(
            double lat1, double lon1, double lat2, double lon2)
        {
            // WSJT-X's own near-identical-point short-circuit (geodist.f90): avoids a
            // divide-by-zero further down when the two points are (numerically)
            // coincident, at the cost of a little precision right at zero distance --
            // matching the original's behavior/threshold exactly rather than
            // "improving" it.
            if (Math.Abs(lat1 - lat2) < 0.02 && Math.Abs(lon1 - lon2) < 0.02)
                return (0.0, 180.0, 0.0);

            // Convert this method's standard East-positive longitude to WSJT-X's own
            // West-positive convention (grid2deg.f90's own doc comment: "degrees of
            // West longitude") before running the formula exactly as WSJT-X does.
            double eplon = -lon1;
            double stlon = -lon2;

            double boa = PolarRadiusM / EquatorialRadiusM;
            double f = 1.0 - boa;
            double p1r = DegToRad(lat1);
            double p2r = DegToRad(lat2);
            double l1r = DegToRad(eplon);
            double l2r = DegToRad(stlon);
            double dlr = l2r - l1r;
            double t1r = Math.Atan(boa * Math.Tan(p1r));
            double t2r = Math.Atan(boa * Math.Tan(p2r));
            double tm = (t1r + t2r) / 2.0;
            double dtm = (t2r - t1r) / 2.0;
            double stm = Math.Sin(tm);
            double ctm = Math.Cos(tm);
            double sdtm = Math.Sin(dtm);
            double cdtm = Math.Cos(dtm);
            double kl = stm * cdtm;
            double kk = sdtm * ctm;
            double sdlmr = Math.Sin(dlr / 2.0);
            double l = sdtm * sdtm + sdlmr * sdlmr * (cdtm * cdtm - stm * stm);
            double cd = 1.0 - 2.0 * l;
            double dl = Math.Acos(cd);
            double sd = Math.Sin(dl);
            double t = dl / sd;
            double u = 2.0 * kl * kl / (1.0 - l);
            double v = 2.0 * kk * kk / l;
            double d = 4.0 * t * t;
            double x = u + v;
            double e = -2.0 * cd;
            double y = u - v;
            double a = -d * e;
            double ff64 = f * f / 64.0;

            double distKm = EquatorialRadiusM * sd * (t - (f / 4.0) * (t * x - y) + ff64 * (x * (a + (t - (a + e) / 2.0) * x) + y * (-2.0 * d + e * y) + d * x * y)) / 1000.0;

            double tdlpm = Math.Tan((dlr + (-((e * (4.0 - x) + 2.0 * y) * ((f / 2.0) * t + ff64 * (32.0 * t + (a - 20.0 * t) * x - 2.0 * (d + 2.0) * y)) / 4.0) * Math.Tan(dlr))) / 2.0);
            double hapbr = Math.Atan2(sdtm, ctm * tdlpm);
            double hambr = Math.Atan2(cdtm, stm * tdlpm);
            double a1m2 = 2.0 * Math.PI + hambr - hapbr;
            double a2m1 = 2.0 * Math.PI - hambr - hapbr;

            // WSJT-X normalizes A1M2/A2M1 into [0, 2*pi) with an explicit add/subtract
            // loop; a floating-point-safe modulo achieves the identical normalized
            // result.
            a1m2 = ((a1m2 % (2.0 * Math.PI)) + 2.0 * Math.PI) % (2.0 * Math.PI);
            a2m1 = ((a2m1 % (2.0 * Math.PI)) + 2.0 * Math.PI) % (2.0 * Math.PI);

            double azDeg = a1m2 * 180.0 / Math.PI;
            double bazDeg = a2m1 * 180.0 / Math.PI;

            // WSJT-X's own final "fix the mirrored coords" step (geodist.f90's own
            // comment), needed because of the West-positive longitude convention used
            // internally above.
            azDeg = 360.0 - azDeg;
            bazDeg = 360.0 - bazDeg;
            azDeg = ((azDeg % 360.0) + 360.0) % 360.0;
            bazDeg = ((bazDeg % 360.0) + 360.0) % 360.0;

            return (azDeg, bazDeg, distKm);
        }
    }
}
