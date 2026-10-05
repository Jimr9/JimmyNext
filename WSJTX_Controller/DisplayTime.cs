using System;
using System.Collections.Generic;
using System.Linq;

namespace WSJTX_Controller
{
    // How dates and times are SHOWN (2026-10-05): UTC unless Options > General "Display dates and
    // times in my selected time zone" is on, then the zone picked on the Station & Operator page
    // (default: the computer's own). A real Windows time zone, so daylight saving is applied for
    // each instant on its own -- a contest that spans a change shows each end in its own offset.
    // Display only: stored QSO times, FT8/FT4 timing, contest windows, ADIF, Cabrillo and uploads
    // stay UTC and are never converted here.
    internal static class DisplayTime
    {
        private static Func<string> _zoneId = () => "";
        private static Func<bool> _useZone = () => false;

        // Set by the Controller: read live, so a profile switch or an Options change shows at once.
        internal static void Configure(Func<string> zoneId, Func<bool> useZone)
        {
            _zoneId = zoneId ?? (() => "");
            _useZone = useZone ?? (() => false);
        }

        internal const string ComputerZoneLabel = "Use computer's time zone";

        // The picked zone: blank (or one Windows no longer has) is the computer's own.
        internal static TimeZoneInfo SelectedZone(string zoneId)
        {
            if (string.IsNullOrWhiteSpace(zoneId)) return TimeZoneInfo.Local;
            try { return TimeZoneInfo.FindSystemTimeZoneById(zoneId.Trim()); }
            catch (Exception) { return TimeZoneInfo.Local; }
        }

        // The zone displays use now: UTC, or the selected zone when the preference is on.
        internal static TimeZoneInfo Zone
        {
            get
            {
                bool use; string id;
                try { use = _useZone(); id = _zoneId(); } catch { use = false; id = ""; }
                return use ? SelectedZone(id) : TimeZoneInfo.Utc;
            }
        }

        // A UTC instant as "Sat 2026-06-06 13:00", in `zone` (default: Zone), without the zone name.
        internal static string DateTimeText(DateTime utc, TimeZoneInfo zone = null)
        {
            zone = zone ?? Zone;
            var t = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
            return t.ToString("ddd yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }

        // The zone's short name AT that instant: "UTC", "CDT" in summer and "CST" in winter (the
        // initials of Windows' own standard / daylight names; the full name when they are unclear).
        internal static string Abbreviation(DateTime utc, TimeZoneInfo zone = null)
        {
            zone = zone ?? Zone;
            if (zone.Id == TimeZoneInfo.Utc.Id) return "UTC";
            string name = FullName(utc, zone);
            string initials = new string(name.Split(new[] { ' ', '.', '-' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => char.IsLetter(w[0])).Select(w => char.ToUpperInvariant(w[0])).ToArray());
            return initials.Length >= 2 && initials.Length <= 5 ? initials : name;
        }

        // Windows' name for the zone at that instant: "Central Daylight Time" / "Central Standard Time".
        internal static string FullName(DateTime utc, TimeZoneInfo zone = null)
        {
            zone = zone ?? Zone;
            if (zone.Id == TimeZoneInfo.Utc.Id) return "UTC";
            var instant = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
            return zone.IsDaylightSavingTime(instant) ? zone.DaylightName : zone.StandardName;
        }

        // "Times are UTC." / "Times are Central Daylight Time (UTC-05:00)." -- once per screen.
        internal static string ZoneSentence(DateTime utcNow, TimeZoneInfo zone = null)
        {
            zone = zone ?? Zone;
            if (zone.Id == TimeZoneInfo.Utc.Id) return "Times are UTC.";
            var off = zone.GetUtcOffset(new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)));
            string sign = off < TimeSpan.Zero ? "-" : "+";
            return $"Times are {FullName(utcNow, zone)} (UTC{sign}{off.Duration():hh\\:mm}).";
        }

        // A UTC range, each end in its own offset: "Sat 2026-06-06 13:00 to Sun 2026-06-07 18:59 CDT";
        // when daylight saving changes inside it, each end carries its own zone name.
        internal static string RangeText(DateTime startUtc, DateTime endUtc, TimeZoneInfo zone = null)
        {
            zone = zone ?? Zone;
            string a1 = Abbreviation(startUtc, zone), a2 = Abbreviation(endUtc, zone);
            return a1 == a2
                ? $"{DateTimeText(startUtc, zone)} to {DateTimeText(endUtc, zone)} {a2}"
                : $"{DateTimeText(startUtc, zone)} {a1} to {DateTimeText(endUtc, zone)} {a2}";
        }

        // The time-zone picker's entries: the computer's own first, then every Windows zone.
        internal static List<(string Id, string Name)> PickerEntries() =>
            new[] { ("", ComputerZoneLabel) }
                .Concat(TimeZoneInfo.GetSystemTimeZones().Select(z => (z.Id, z.DisplayName)))
                .ToList();
    }
}
