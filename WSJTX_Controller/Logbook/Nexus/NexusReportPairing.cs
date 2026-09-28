using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace WSJTX_Controller
{
    // Makes a confirmation download (LoTW report, QRZ FETCH) pair with the RIGHT contacts in Nexus,
    // without changing Nexus.
    //
    // Nexus's merge pairs a report row with a logged contact by (call, band, mode class, UTC day),
    // falling back to the day before and then the day after, and inside that group takes the
    // contacts in LOG order -- the order they were added -- not the one nearest in time
    // (tempo-core reconcile::take_match). So with several contacts with one station on one band in
    // a day, a row can land on the wrong one: a download listing them in time order against a log
    // that holds them in another order, a row missing for an earlier contact, or a confirmation of
    // a later contact while an earlier one is unconfirmed. Upgrades are never taken back, so a
    // wrong pairing leaves a false confirmation.
    //
    // This hands Nexus only the rows its pairing is certain to put on their own contact (the
    // contact at the same minute), in the order that makes it do so; every other row is held back
    // -- it changes nothing -- and reported, rather than guessed:
    //   - a row with exactly one contact at its minute is sent when the rows sent for that group
    //     are for the group's FIRST contacts in log order (Nexus hands them out in that order, so
    //     row i then takes contact i); otherwise the whole group's rows are held;
    //   - a row with no contact at its minute is sent only when the log has no contact with that
    //     call and band on that day or the day either side (QRZ: a contact the log lacks, which
    //     Nexus adds; LoTW: an unmatched confirmation Nexus reports); otherwise it is held;
    //   - two contacts at one minute, or two rows for one contact, hold the group.
    // The header and each row sent are passed on exactly as received.
    public static class NexusReportPairing
    {
        public class Result
        {
            public string Text;                              // what to hand to Nexus
            public int Sent, Held;
            public List<string> HeldDetails = new List<string>();
        }

        private sealed class Contact { public int Pos; public string Bucket, Minute; }
        private sealed class Row { public string Raw, Bucket, Minute, Label; public long Day; public string CallBand, ModeClass; }

        private static readonly Regex Field = new Regex(@"<([A-Za-z0-9_]+):(\d+)(?::[A-Za-z])?>", RegexOptions.Compiled);
        private static readonly Regex Eoh = new Regex(@"<eoh>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Eor = new Regex(@"<eor>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // tempo-core reconcile::mode_class, reproduced exactly.
        public static string ModeClass(string mode)
        {
            switch ((mode ?? "").Trim().ToUpperInvariant())
            {
                case "CW": return "CW";
                case "SSB": case "USB": case "LSB": case "AM": case "FM": case "PHONE": case "PH": case "DV": case "C4FM":
                case "DIGITALVOICE": case "DSTAR": case "FUSION": case "M17": case "FREEDV": return "Phone";
                case "": return "Other";
                default: return "Digital";
            }
        }

        private static string Bucket(string callBand, string modeClass, long day) => $"{callBand}|{modeClass}|{day}";

        public static Result Prepare(string text, IReadOnlyList<NexusQso> logInOrder)
        {
            text = text ?? "";
            var m = Eoh.Match(text);
            string header = m.Success ? text.Substring(0, m.Index + m.Length) : "";
            string body = m.Success ? text.Substring(m.Index + m.Length) : text;

            // The log, grouped the way Nexus groups it.
            var contacts = new Dictionary<string, List<Contact>>();
            var byMinute = new Dictionary<string, List<Contact>>();
            var callBandDays = new HashSet<string>();
            for (int i = 0; i < logInOrder.Count; i++)
            {
                var q = logInOrder[i];
                var when = DateTimeOffset.FromUnixTimeSeconds((long)q.WhenUnix).UtcDateTime;
                string cb = $"{(q.Call ?? "").Trim().ToUpperInvariant()}|{(q.Band ?? "").Trim().ToLowerInvariant()}";
                long day = (long)q.WhenUnix / 86400;
                var c = new Contact { Pos = i, Bucket = Bucket(cb, ModeClass(q.Mode), day), Minute = $"{cb}|{when:yyyyMMddHHmm}" };
                if (!contacts.TryGetValue(c.Bucket, out var list)) contacts[c.Bucket] = list = new List<Contact>();
                list.Add(c);
                if (!byMinute.TryGetValue(c.Minute + "|" + ModeClass(q.Mode), out var ml)) byMinute[c.Minute + "|" + ModeClass(q.Mode)] = ml = new List<Contact>();
                ml.Add(c);
                callBandDays.Add($"{cb}|{ModeClass(q.Mode)}|{day}");
            }

            // The download's rows, as received.
            var rows = new List<Row>();
            string trailer = "";
            var parts = Eor.Split(body);
            for (int i = 0; i < parts.Length; i++)
            {
                string raw = parts[i];
                var f = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match fm in Field.Matches(raw))
                {
                    int len = int.Parse(fm.Groups[2].Value, CultureInfo.InvariantCulture);
                    int at = fm.Index + fm.Length;
                    if (at + len <= raw.Length) f[fm.Groups[1].Value] = raw.Substring(at, len);
                }
                if (!f.TryGetValue("CALL", out var call) || string.IsNullOrWhiteSpace(call))
                {
                    if (i == parts.Length - 1) trailer = raw; // e.g. LoTW's <APP_LoTW_EOF>
                    continue;
                }
                f.TryGetValue("BAND", out var band); f.TryGetValue("MODE", out var mode);
                f.TryGetValue("QSO_DATE", out var date); f.TryGetValue("TIME_ON", out var time);
                string cb = $"{call.Trim().ToUpperInvariant()}|{(band ?? "").Trim().ToLowerInvariant()}";
                string hhmm = ((time ?? "").Trim() + "0000").Substring(0, 4);
                long dayNo = DateTime.TryParseExact((date ?? "").Trim(), "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
                    ? (long)(d - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalDays : long.MinValue;
                string mc = ModeClass(mode);
                rows.Add(new Row
                {
                    Raw = raw, CallBand = cb, ModeClass = mc, Day = dayNo,
                    Bucket = Bucket(cb, mc, dayNo), Minute = $"{cb}|{(date ?? "").Trim()}{hhmm}|{mc}",
                    Label = $"{call.Trim().ToUpperInvariant()} {(band ?? "").Trim().ToLowerInvariant()} {(mode ?? "").Trim()} {(date ?? "").Trim()} {hhmm}"
                });
            }

            var result = new Result();
            var send = new List<(int Order, Row Row)>();
            void Hold(Row r, string why) { result.Held++; result.HeldDetails.Add($"{r.Label}: {why}"); }

            // Rows at a contact's minute, grouped by the contact's group.
            var matched = new Dictionary<string, List<(Row Row, Contact Contact)>>();
            foreach (var r in rows)
            {
                if (r.Day == long.MinValue) { Hold(r, "no valid date"); continue; }
                byMinute.TryGetValue(r.Minute, out var at);
                if (at != null && at.Count > 1) { Hold(r, "more than one logged contact at this minute"); continue; }
                if (at != null && at.Count == 1)
                {
                    var c = at[0];
                    if (!matched.TryGetValue(c.Bucket, out var g)) matched[c.Bucket] = g = new List<(Row, Contact)>();
                    g.Add((r, c));
                    continue;
                }
                bool near = callBandDays.Contains($"{r.CallBand}|{r.ModeClass}|{r.Day}") ||
                            callBandDays.Contains($"{r.CallBand}|{r.ModeClass}|{r.Day - 1}") ||
                            callBandDays.Contains($"{r.CallBand}|{r.ModeClass}|{r.Day + 1}");
                if (near) { Hold(r, "no logged contact at this minute, but others with this station and band that day or the day either side"); continue; }
                send.Add((int.MaxValue, r)); // a contact the log does not hold at all
            }

            foreach (var g in matched)
            {
                var inLog = contacts[g.Key];
                var taken = g.Value.Select(x => x.Contact).ToList();
                bool dup = taken.Distinct().Count() != taken.Count;
                bool prefix = !dup && new HashSet<Contact>(taken).SetEquals(inLog.Take(taken.Count));
                if (!prefix)
                {
                    string why = dup ? "two rows for one logged contact"
                        : $"{inLog.Count} logged contacts with this station and band that day, and the rows do not cover them in the order Nexus pairs them";
                    foreach (var x in g.Value) Hold(x.Row, why);
                    continue;
                }
                foreach (var x in g.Value) send.Add((x.Contact.Pos, x.Row));
            }

            var sb = new StringBuilder(header);
            foreach (var s in send.OrderBy(s => s.Order)) sb.Append(s.Row.Raw).Append("<eor>");
            sb.Append(trailer);
            result.Text = sb.ToString();
            result.Sent = send.Count;
            return result;
        }
    }
}
