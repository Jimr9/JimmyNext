using System;
using System.Collections.Generic;
using System.Globalization;

namespace WSJTX_Controller
{
    // Space-weather announcements (2026-11-09). The facts are Nexus's own (pathnow::space_now via
    // EngineHost's SPACE_WX): a flare at M1 or above, an S1+ proton event, a NOAA geomagnetic
    // watch. This only decides WHEN each is said -- once when it begins, again only when it
    // grows (a stronger flare, a higher S level, a new watch) -- and puts it into words.
    internal sealed class SpaceWeatherAlerts
    {
        private long? _watchIssued;
        private int _flareR;
        private int _protonS;

        // What to say for this reading. A quiet reading (null) ends any flare or proton event, so
        // the next one is said again; a watch is said once per issue.
        internal List<SpaceWeatherEvent> Check(SpaceNow now, long nowUnix)
        {
            var said = new List<SpaceWeatherEvent>();
            var flare = now?.Flare;
            if (flare == null) _flareR = 0;
            else if (flare.R > _flareR)
            {
                _flareR = flare.R;
                said.Add(new SpaceWeatherEvent(NotificationEventType.SpaceWeatherFlare, "flare:" + flare.XrayAt, new Dictionary<string, string>
                {
                    ["Class"] = FlareClass(flare.XrayLong),
                    ["Effect"] = FlareEffect(flare),
                    ["Until"] = Until(flare),
                }));
            }

            var protons = now?.Protons;
            if (protons == null) _protonS = 0;
            else if (protons.S > _protonS)
            {
                _protonS = protons.S;
                said.Add(new SpaceWeatherEvent(NotificationEventType.SpaceWeatherProtons, "protons:" + protons.S, new Dictionary<string, string>
                {
                    ["Level"] = "S" + protons.S,
                }));
            }

            var watch = now?.Watch;
            if (watch != null && watch.Issued != _watchIssued)
            {
                _watchIssued = watch.Issued;
                said.Add(new SpaceWeatherEvent(NotificationEventType.SpaceWeatherStormWatch, "watch:" + watch.Issued, new Dictionary<string, string>
                {
                    ["Level"] = "G" + watch.G,
                    ["When"] = WatchWhen(watch, nowUnix),
                }));
            }
            return said;
        }

        // The Space Weather tab's rows: the default wording, filled the same way.
        internal static string FlareSentence(NowFlare f) =>
            $"{FlareClass(f.XrayLong)} solar flare, {FlareEffect(f)}, until about {Until(f)}.";
        internal static string ProtonsSentence(NowProtons p) => $"Solar proton event, S{p.S}, polar paths absorbed.";
        internal static string WatchSentence(NowWatch w, long nowUnix) => $"Geomagnetic storm watch, G{w.G}, {WatchWhen(w, nowUnix)}.";

        // NOAA's flare class from the long-band flux: "M5", "X1.2" (one decimal only when it says something).
        internal static string FlareClass(float flux)
        {
            string letter; double scale;
            if (flux >= 1e-4f) { letter = "X"; scale = 1e-4; }
            else if (flux >= 1e-5f) { letter = "M"; scale = 1e-5; }
            else if (flux >= 1e-6f) { letter = "C"; scale = 1e-6; }
            else { letter = "B"; scale = 1e-7; }
            return letter + (flux / scale).ToString("0.#", CultureInfo.InvariantCulture);
        }

        // "20m" -> "20 meters".
        internal static string BandWords(string band)
        {
            band = (band ?? "").Trim();
            return band.EndsWith("m", StringComparison.OrdinalIgnoreCase) ? band.Substring(0, band.Length - 1) + " meters" : band;
        }

        // Out = the highest band out under the sun, Weak = the highest weakened (Nexus's own).
        internal static string FlareEffect(NowFlare f)
        {
            bool weakHigher = f.Weak != null && f.Weak != f.Out;
            if (f.Out != null)
                return $"{BandWords(f.Out)} and lower out on daylight paths" + (weakHigher ? $", up to {BandWords(f.Weak)} weakened" : "");
            if (f.Weak != null)
                return $"{BandWords(f.Weak)} and lower weakened on daylight paths";
            return "little effect on daylight paths";
        }

        internal static string Until(NowFlare f) => f.Recovery != null ? TimeText(f.Recovery.At) : "it fades";

        // "expected tomorrow", "expected Saturday", "in force through today". NOAA's watch days are
        // UT days, so they are named by the UTC date.
        internal static string WatchWhen(NowWatch w, long nowUnix)
        {
            if (w.From > nowUnix) return "expected " + DayWord(w.From, nowUnix);
            return "in force through " + DayWord(Math.Max(w.From, w.To - 1), nowUnix);
        }

        private static string DayWord(long unix, long nowUnix)
        {
            var day = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.Date;
            var today = DateTimeOffset.FromUnixTimeSeconds(nowUnix).UtcDateTime.Date;
            int diff = (int)(day - today).TotalDays;
            if (diff == 0) return "today";
            if (diff == 1) return "tomorrow";
            return day.ToString("dddd", CultureInfo.InvariantCulture);
        }

        // "14:50 UTC", or the operator's chosen zone.
        internal static string TimeText(long unix)
        {
            var utc = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            var zone = DisplayTime.Zone;
            return TimeZoneInfo.ConvertTimeFromUtc(utc, zone).ToString("HH:mm", CultureInfo.InvariantCulture) + " " + DisplayTime.Abbreviation(utc, zone);
        }
    }
}
