using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace WSJTX_Controller
{
    // Readable names for support reports and uploaded support settings (2026-10-12): who, what
    // kind, and when -- "KB0UZT - Bug - Jim Ruby - kb0uzt@example.com - 2026-10-09 04-15 CDT" and
    // "KB0UZT - Updated 2026-10-09 04-15 CDT" -- in place of the program name, version and the
    // server's long ticket string. The time is the event's own UTC instant shown the way this
    // computer shows every other time (DisplayTime: UTC, or the operator's selected zone, with
    // daylight saving for that instant), and it says which. The server keeps its own ticket for
    // finding and deleting a report; that is never part of a name the operator reads.
    internal static class SupportNames
    {
        // The form's four choices as the short word used in names; anything else (an older
        // client's report with no type) is "Report".
        internal static string TypeLabel(string problemType)
        {
            string t = (problemType ?? "").Trim().ToLowerInvariant();
            if (t.StartsWith("bug")) return "Bug";
            if (t.StartsWith("feature")) return "Feature Request";
            if (t.StartsWith("accessib")) return "Accessibility";
            if (t.StartsWith("question")) return "Question";
            return "Report";
        }

        // "2026-10-09 04-15 CDT": no colon (not allowed in a file name), the zone it is in.
        internal static string TimeText(DateTime utc, TimeZoneInfo zone = null)
        {
            zone = zone ?? DisplayTime.Zone;
            utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
            string abbr = DisplayTime.Abbreviation(utc, zone);
            if (abbr.Length > 5 || abbr.Any(c => !char.IsLetter(c)))
            {
                var off = zone.GetUtcOffset(new DateTimeOffset(utc));
                abbr = "UTC" + (off < TimeSpan.Zero ? "-" : "+") + off.Duration().ToString("hhmm", CultureInfo.InvariantCulture);
            }
            return local.ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture) + " " + abbr;
        }

        // One part of a name: nothing a file name cannot hold, no run of spaces, no leading or
        // trailing dot or space, and no longer than `max`. The report itself keeps the exact text.
        internal static string Part(string text, int max)
        {
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            var chars = (text ?? "").Select(c => invalid.Contains(c) || char.IsControl(c) ? ' ' : c).ToArray();
            string s = string.Join(" ", new string(chars).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '.');
            if (s.Length > max) s = s.Substring(0, max).Trim(' ', '.');
            return s;
        }

        // A support report's name, without the extension. Parts that are empty are left out
        // (a report saved to the computer only needs no callsign, name or email).
        internal static string Report(string callsign, string problemType, string name, string email, DateTime utc, TimeZoneInfo zone = null)
        {
            var parts = new List<string>
            {
                Part((callsign ?? "").ToUpperInvariant(), 16), TypeLabel(problemType), Part(name, 40), Part(email, 64), TimeText(utc, zone),
            };
            string joined = string.Join(" - ", parts.Where(p => p.Length > 0));
            return parts[0].Length == 0 && parts[2].Length == 0 && parts[3].Length == 0 ? "Jimmy Next support - " + joined : joined;
        }

        // An uploaded support settings file: who it is for and when it was last uploaded.
        internal static string Profile(string callsign, DateTime updatedUtc, TimeZoneInfo zone = null) =>
            $"{Part((callsign ?? "").ToUpperInvariant(), 16)} - Updated {TimeText(updatedUtc, zone)}";

        // A path that does not exist yet: "name.zip", then "name (2).zip", "name (3).zip" -- two
        // reports from the same person in the same minute never replace each other.
        internal static string Unique(string folder, string name, string extension)
        {
            string path = Path.Combine(folder, name + extension);
            for (int n = 2; File.Exists(path) && n < 1000; n++)
                path = Path.Combine(folder, $"{name} ({n}){extension}");
            return path;
        }

        // The server's time ("2026-10-09T09:15:20+00:00"), or null when it is not a time.
        internal static DateTime? ParseUtc(string serverTime) =>
            DateTime.TryParse(serverTime, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : (DateTime?)null;
    }
}
