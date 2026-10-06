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
    // With nearbyUnique (eQSL, whose rows carry the OTHER station's time, often minutes off ours --
    // Jimmy's own eQSL matcher never used the time): a row with no contact at its minute is also
    // sent when exactly ONE logged contact has its call, band and mode class on that day or the day
    // either side and no other row claims it -- the only contact Nexus can pair it with. More than
    // one such contact holds it, as Jimmy's matcher skipped it as ambiguous.
    // The header and each row sent are passed on exactly as received -- except that a row paired
    // with a logged contact has its STATE and COUNTRY taken out (2026-10-04): Nexus's merge fills a
    // BLANK logged state or country from the row, and the contact keeps what was logged, blanks
    // included. The row's location is returned in Located, for the caller to keep apart.
    public static class NexusReportPairing
    {
        public class Result
        {
            public string Text;                              // what to hand to Nexus
            public int Sent, Held;
            public int Dropped;                              // settled / repeated rows left out (own-records report)
            public List<string> HeldDetails = new List<string>();
            // Each row sent that pairs with a logged contact: the contact, and the row's location fields.
            public List<(NexusQso Logged, Dictionary<string, string> Location, string Label)> Located =
                new List<(NexusQso, Dictionary<string, string>, string)>();
        }

        internal static readonly string[] LocationTags = { "STATE", "CNTY", "COUNTRY", "DXCC", "CQZ", "ITUZ", "GRIDSQUARE" };
        private static readonly HashSet<string> MergeFills = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "STATE", "COUNTRY" };

        // Club Log's own view of a confirmation (operator, 2026-10-06): a contact Club Log adds is
        // added unconfirmed -- LoTW, QRZ and eQSL downloads say what is confirmed, from the source.
        private static readonly HashSet<string> ConfirmationTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "QSL_RCVD", "QSL_RCVD_VIA", "QSLRDATE", "LOTW_QSL_RCVD", "LOTW_QSLRDATE", "EQSL_QSL_RCVD", "EQSL_QSLRDATE",
            "QRZCOM_QSO_DOWNLOAD_STATUS", "QRZCOM_QSO_DOWNLOAD_DATE", "CREDIT_GRANTED", "CREDIT_SUBMITTED",
        };

        // The row with the fields Nexus's merge would fill from it removed.
        private static string WithoutMergeFills(string raw) => WithoutTags(raw, MergeFills);

        private static string WithoutTags(string raw, HashSet<string> tags)
        {
            var sb = new StringBuilder();
            int pos = 0;
            foreach (Match fm in Field.Matches(raw))
            {
                if (fm.Index < pos) continue;
                int len = int.Parse(fm.Groups[2].Value, CultureInfo.InvariantCulture);
                int end = Math.Min(raw.Length, fm.Index + fm.Length + len);
                if (!tags.Contains(fm.Groups[1].Value)) continue;
                sb.Append(raw, pos, fm.Index - pos);
                pos = end;
            }
            sb.Append(raw, pos, raw.Length - pos);
            return sb.ToString();
        }

        private sealed class Contact { public int Pos; public string Bucket, Minute; }
        private sealed class Row { public string Raw, Bucket, Minute, Label; public long Day; public string CallBand, ModeClass; public Dictionary<string, string> Fields; }

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

        // LoTW's own-records report only (PromoteLotwReceived), 2026-10-06:
        //   settled: contacts outside logInOrder (already award-confirmed). A row at one of their
        //     minutes, and at no contact in logInOrder, is that contact's own record -- nothing to
        //     do, so it is dropped, not held.
        //   collapseSameMinute: several rows at one contact's minute are that contact uploaded more
        //     than once (LoTW keeps a re-upload whose details differ) -- one is sent, the rest dropped.
        // absentOnly (Club Log's download, 2026-10-06): Club Log holds only what was uploaded to it,
        //   so only a contact the log does not hold at all is sent -- added unconfirmed (its
        //   confirmation fields taken out). A row at a logged contact is left out (Dropped).
        public static Result Prepare(string text, IReadOnlyList<NexusQso> logInOrder, bool nearbyUnique = false,
                                     IReadOnlyList<NexusQso> settled = null, bool collapseSameMinute = false,
                                     bool absentOnly = false)
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
            var settledMinutes = new HashSet<string>();
            foreach (var q in settled ?? new List<NexusQso>())
            {
                var when = DateTimeOffset.FromUnixTimeSeconds((long)q.WhenUnix).UtcDateTime;
                settledMinutes.Add($"{(q.Call ?? "").Trim().ToUpperInvariant()}|{(q.Band ?? "").Trim().ToLowerInvariant()}|{when:yyyyMMddHHmm}|{ModeClass(q.Mode)}");
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
                    Raw = raw, CallBand = cb, ModeClass = mc, Day = dayNo, Fields = f,
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
                if (absentOnly && at != null && at.Count > 0) { result.Dropped++; continue; }   // already logged
                if (at != null && at.Count > 1) { Hold(r, "more than one logged contact at this minute"); continue; }
                if (at != null && at.Count == 1)
                {
                    var c = at[0];
                    if (!matched.TryGetValue(c.Bucket, out var g)) matched[c.Bucket] = g = new List<(Row, Contact)>();
                    g.Add((r, c));
                    continue;
                }
                if (settledMinutes.Contains(r.Minute)) { result.Dropped++; continue; }
                if (nearbyUnique)
                {
                    var cands = new List<Contact>();
                    for (long dd = r.Day - 1; dd <= r.Day + 1; dd++)
                        if (contacts.TryGetValue(Bucket(r.CallBand, r.ModeClass, dd), out var l)) cands.AddRange(l);
                    if (cands.Count == 1)
                    {
                        var c = cands[0];
                        if (!matched.TryGetValue(c.Bucket, out var g)) matched[c.Bucket] = g = new List<(Row, Contact)>();
                        g.Add((r, c));
                        continue;
                    }
                    if (cands.Count > 1) { Hold(r, $"{cands.Count} logged contacts with this station and band that day or the day either side, none at this minute"); continue; }
                }
                bool near = callBandDays.Contains($"{r.CallBand}|{r.ModeClass}|{r.Day}") ||
                            callBandDays.Contains($"{r.CallBand}|{r.ModeClass}|{r.Day - 1}") ||
                            callBandDays.Contains($"{r.CallBand}|{r.ModeClass}|{r.Day + 1}");
                if (near) { Hold(r, "no logged contact at this minute, but others with this station and band that day or the day either side"); continue; }
                if (absentOnly) r.Raw = WithoutTags(r.Raw, ConfirmationTags);
                send.Add((int.MaxValue, r)); // a contact the log does not hold at all
            }

            foreach (var g in matched)
            {
                var inLog = contacts[g.Key];
                if (collapseSameMinute)
                {
                    var one = g.Value.GroupBy(x => x.Contact).Select(x => x.First()).ToList();
                    result.Dropped += g.Value.Count - one.Count;
                    g.Value.Clear();
                    g.Value.AddRange(one);
                }
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
                foreach (var x in g.Value)
                {
                    var loc = LocationTags.Where(t => x.Row.Fields.ContainsKey(t))
                        .ToDictionary(t => t, t => x.Row.Fields[t].Trim(), StringComparer.OrdinalIgnoreCase);
                    result.Located.Add((logInOrder[x.Contact.Pos], loc, x.Row.Label));
                    x.Row.Raw = WithoutMergeFills(x.Row.Raw);
                    send.Add((x.Contact.Pos, x.Row));
                }
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
