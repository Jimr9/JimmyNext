using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace WSJTX_Controller
{
    // Logbook migration (c:\chat gpt\nexus log review.txt), Phase 2: the three tools that move a
    // Jimmy logbook into a Nexus-owned one and back, and prove nothing changed on the way.
    //
    //   WriteAdif   Jimmy logbook.db  ->  log.adi, by mapping version 1 (plan section 7.2).
    //               Nexus's own first-open conversion (logstore::open) then turns that file into
    //               its database -- the migration re-uses Nexus's converter, it does not write
    //               Nexus's database itself.
    //   Compare     Jimmy rows vs the rows Nexus then holds (read through LOG_ROWS), field by
    //               field; the report lists every difference and every piece of NEW information
    //               Nexus read from fields Jimmy only kept as extras.
    //   Rebuild     Nexus's CURRENT rows  ->  a fresh Jimmy-format logbook.db (the rollback,
    //               plan 7.3): edits, deletions, confirmations and upload stamps made in Nexus
    //               are all carried back, because it is built from what Nexus holds now.
    //
    // The source database is only ever opened read-only. Nothing here touches a real data path:
    // callers pass explicit paths (tests and the dry-run use copies in temp folders).
    public static class NexusMigration
    {
        public const string MappingVersion = "1";

        // Jimmy-only values carried as namespaced extras (Nexus keeps unknown fields verbatim).
        public const string RowIdTag = "APP_JIMMY_ROW_ID";
        public const string SourceTag = "APP_JIMMY_SOURCE";
        public const string SourceQsoIdTag = "APP_JIMMY_SOURCE_QSO_ID";
        public const string ImportedAtTag = "APP_JIMMY_IMPORTED_AT";
        public const string ModifiedAtTag = "APP_JIMMY_MODIFIED_AT";
        public const string QrzQslSentTag = "APP_JIMMY_QRZ_QSL_SENT";
        public const string LotwQslRcvdRawTag = "APP_JIMMY_LOTW_QSL_RCVD";
        public const string LotwQslSentRawTag = "APP_JIMMY_LOTW_QSL_SENT";
        public const string HrdlogUploadedTag = "APP_JIMMY_HRDLOG_UL";
        // A value Nexus would drop or reduce, kept verbatim for the rollback. Restored only while
        // Nexus's own current value does not supersede it (an edit in Nexus always wins).
        public const string IotaRawTag = "APP_JIMMY_IOTA";
        // Contest contacts logged while Nexus owns the log: the operating instance, and the
        // structured received exchange (one field per ruleset key), as namespaced extras.
        public const string ContestSessionTag = "APP_JIMMY_CONTEST_SESSION";
        public const string ContestRxPrefix = "APP_JIMMY_RX_";
        public const string RawExtraPrefix = "APP_JIMMY_X_";
        // Jimmy extras Nexus reads into a yes/no: a "N" (or any non-Y) would otherwise vanish.
        private static readonly HashSet<string> ReducedExtraTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "EQSL_QSL_RCVD",
        };
        private static readonly System.Text.RegularExpressions.Regex IotaRef =
            new System.Text.RegularExpressions.Regex(@"^(AF|AN|AS|EU|NA|OC|SA)-\d{3}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Everything Jimmy writes from its own columns. An extra field with one of these names would
        // collide with the column's value, so the column wins and the collision is reported.
        private static readonly HashSet<string> ColumnTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CALL","BAND","MODE","QSO_DATE","TIME_ON","QSO_DATE_OFF","TIME_OFF","FREQ","RST_SENT","RST_RCVD",
            "STATE","COUNTRY","DXCC","CQZ","ITUZ","CONT","CNTY","IOTA","SIG","SIG_INFO","MY_SIG","MY_SIG_INFO",
            "DARC_DOK","PFX","GRIDSQUARE","NAME","COMMENT","TX_PWR","OPERATOR","STATION_CALLSIGN","MY_GRIDSQUARE",
            "LOTW_QSL_SENT","LOTW_QSL_RCVD","EQSL_QSL_RCVD","APP_QRZLOG_STATUS","STX_STRING","SRX_STRING","CONTEST_ID",
            "APP_TEMPO_UL_QRZ","APP_TEMPO_UL_CLUBLOG","APP_TEMPO_UL_LOTW","APP_TEMPO_UL_EQSL",
            RowIdTag, SourceTag, SourceQsoIdTag, ImportedAtTag, ModifiedAtTag, QrzQslSentTag,
            LotwQslRcvdRawTag, LotwQslSentRawTag, HrdlogUploadedTag, IotaRawTag, ContestSessionTag,
        };

        // One Jimmy row as the migration reads it.
        public class JimmyRow
        {
            public long Id;
            public Dictionary<string, string> Col = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<(string Tag, string Value)> Extras = new List<(string, string)>();
            public string C(string col) => Col.TryGetValue(col, out var v) ? v ?? "" : "";
            public long N(string col) => long.TryParse(C(col), out var n) ? n : 0;
        }

        public class WriteResult
        {
            public int Rows;
            public List<string> Notes = new List<string>();
        }

        public static List<JimmyRow> ReadJimmyRows(string jimmyDbPath)
        {
            var rows = new List<JimmyRow>();
            using (var conn = new SQLiteConnection($"Data Source={jimmyDbPath};Read Only=True;"))
            {
                conn.Open();
                var byId = new Dictionary<long, JimmyRow>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM qso ORDER BY id;";
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            var row = new JimmyRow { Id = r.GetInt64(r.GetOrdinal("id")) };
                            for (int i = 0; i < r.FieldCount; i++)
                                row.Col[r.GetName(i)] = r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);
                            rows.Add(row);
                            byId[row.Id] = row;
                        }
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT qso_id, tag_name, tag_value FROM qso_extra_field ORDER BY qso_id, ordinal;";
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            if (byId.TryGetValue(r.GetInt64(0), out var row))
                                row.Extras.Add((r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2)));
                }
            }
            return rows;
        }

        // ── Jimmy -> ADIF (mapping version 1) ─────────────────────────────────────────────

        public static WriteResult WriteAdif(string jimmyDbPath, string outAdiPath)
        {
            var rows = ReadJimmyRows(jimmyDbPath);
            var res = new WriteResult { Rows = rows.Count };
            var sb = new StringBuilder();
            sb.Append("Jimmy Next logbook migration\n");
            sb.Append(Field("ADIF_VER", "3.1.4")).Append(Field("PROGRAMID", "Jimmy Next"))
              .Append(Field("APP_JIMMY_MIGRATION_VERSION", MappingVersion)).Append("<EOH>\n");
            foreach (var row in rows)
            {
                foreach (var (tag, value) in FieldsFor(row, res.Notes))
                    sb.Append(Field(tag, value));
                sb.Append("<EOR>\n");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(outAdiPath));
            File.WriteAllText(outAdiPath, sb.ToString(), new UTF8Encoding(false));
            return res;
        }

        // The record's fields, in order. Public for tests.
        public static List<(string Tag, string Value)> FieldsFor(JimmyRow row, List<string> notes = null)
        {
            var f = new List<(string, string)>();
            void Add(string tag, string v) { if (!string.IsNullOrEmpty(v)) f.Add((tag, v)); }
            void AddNum(string tag, long v) { if (v > 0) f.Add((tag, v.ToString(CultureInfo.InvariantCulture))); }

            string date = row.C("qso_date");
            string timeOn = Seconds(row.C("time_on"));
            Add("CALL", row.C("callsign"));
            Add("BAND", row.C("band"));
            Add("MODE", row.C("mode"));
            Add("QSO_DATE", date);
            Add("TIME_ON", timeOn);
            string timeOff = Seconds(row.C("time_off"));
            if (timeOff.Length > 0)
            {
                // Jimmy stores only a time off; a time before the time on means the contact ran
                // past midnight UTC.
                string dateOff = date;
                if (string.CompareOrdinal(timeOff, timeOn) < 0 &&
                    DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    dateOff = d.AddDays(1).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                Add("QSO_DATE_OFF", dateOff);
                Add("TIME_OFF", timeOff);
            }
            long hz = row.N("freq_hz");
            if (hz > 0) Add("FREQ", (hz / 1_000_000.0).ToString("0.000000", CultureInfo.InvariantCulture));
            Add("RST_SENT", row.C("rst_sent"));
            Add("RST_RCVD", row.C("rst_rcvd"));
            Add("STATE", row.C("state"));
            Add("COUNTRY", row.C("country"));
            AddNum("DXCC", row.N("dxcc"));
            AddNum("CQZ", row.N("cq_zone"));
            AddNum("ITUZ", row.N("itu_zone"));
            Add("CONT", row.C("continent"));
            Add("CNTY", row.C("county"));
            Add("IOTA", row.C("iota"));
            if (row.C("iota").Length > 0 && !IotaRef.IsMatch(row.C("iota"))) Add(IotaRawTag, row.C("iota"));
            Add("SIG", row.C("sig"));
            Add("SIG_INFO", row.C("sig_info"));
            Add("MY_SIG", row.C("my_sig"));
            Add("MY_SIG_INFO", row.C("my_sig_info"));
            Add("DARC_DOK", row.C("darc_dok"));
            Add("PFX", row.C("wpx_prefix"));
            Add("GRIDSQUARE", row.C("grid"));
            Add("NAME", row.C("name"));
            Add("COMMENT", row.C("comment"));
            Add("TX_PWR", row.C("tx_pwr"));
            Add("OPERATOR", row.C("operator_call"));
            Add("STATION_CALLSIGN", row.C("station_call"));
            Add("MY_GRIDSQUARE", row.C("my_grid"));

            // Confirmations, per channel. QRZ is NEVER written as QSL_RCVD -- Nexus reads that as a
            // paper card (award-grade); QRZ's own confirmation is APP_QRZLOG_STATUS:C.
            Add("LOTW_QSL_SENT", row.C("lotw_qsl_sent"));
            Add("LOTW_QSL_RCVD", row.C("lotw_qsl_rcvd"));
            if (row.C("eqsl_qsl_rcvd") == "Y") Add("EQSL_QSL_RCVD", "Y");
            if (row.C("qrz_qsl_rcvd") == "Y") Add("APP_QRZLOG_STATUS", "C");
            // Raw values Nexus reduces to a yes/no, kept so a rollback restores them exactly.
            Add(LotwQslRcvdRawTag, row.C("lotw_qsl_rcvd"));
            Add(LotwQslSentRawTag, row.C("lotw_qsl_sent"));
            Add(QrzQslSentTag, row.C("qrz_qsl_sent"));

            Add("STX_STRING", row.C("exchange_sent"));
            Add("SRX_STRING", row.C("exchange_rcvd"));
            Add("CONTEST_ID", row.C("contest_id"));

            // Upload state, in Nexus's own encoding: "outcome|unix|detail".
            Add("APP_TEMPO_UL_QRZ", UploadField("accepted", row.C("qrz_uploaded_at")));
            Add("APP_TEMPO_UL_CLUBLOG", UploadField("accepted", row.C("clublog_uploaded_at")));
            Add("APP_TEMPO_UL_EQSL", UploadField("accepted", row.C("eqsl_uploaded_at")));
            Add("APP_TEMPO_UL_LOTW", UploadField(LotwProvenByLotw(row) ? "accepted" : "pending", row.C("lotw_uploaded_at")));
            Add(HrdlogUploadedTag, row.C("hrdlog_uploaded_at"));

            Add(RowIdTag, row.Id.ToString(CultureInfo.InvariantCulture));
            Add(SourceTag, row.C("source"));
            Add(SourceQsoIdTag, row.C("source_qso_id"));
            Add(ImportedAtTag, row.C("imported_at"));
            Add(ModifiedAtTag, row.C("modified_at"));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var written = new HashSet<string>(f.Select(x => x.Item1), StringComparer.OrdinalIgnoreCase);
            foreach (var (tag, value) in row.Extras)
            {
                // A logbook that once came from Nexus carries Nexus's old record id; the new log
                // gives every contact its own.
                if (tag.Equals("APP_NEXUS_ID", StringComparison.OrdinalIgnoreCase)) continue;
                // Only a tag this record already carries from a column collides. (An extra
                // EQSL_QSL_RCVD=N beside an empty eQSL column is data, not a collision.)
                if (written.Contains(tag))
                {
                    notes?.Add($"row {row.Id} {row.C("callsign")}: extra {tag}={value} not written (the column wins)");
                    continue;
                }
                if (!seen.Add(tag))
                {
                    // ADIF has one value per tag per record; Nexus keeps one.
                    notes?.Add($"row {row.Id} {row.C("callsign")}: repeated extra {tag}={value} not written (ADIF keeps one per record)");
                    continue;
                }
                Add(tag, value);
                if (ReducedExtraTags.Contains(tag) && !string.Equals(value, "Y", StringComparison.OrdinalIgnoreCase))
                    Add(RawExtraPrefix + tag.ToUpperInvariant(), value);
            }
            return f;
        }

        // LoTW itself reported the contact: it came from a LoTW download, carries LoTW's own
        // download fields, or LoTW confirmed it.
        public static bool LotwProvenByLotw(JimmyRow row) =>
            row.C("source") == "LOTW" || row.C("lotw_qsl_rcvd") == "Y" ||
            row.Extras.Any(e => e.Tag.StartsWith("APP_LOTW_", StringComparison.OrdinalIgnoreCase));

        private static string UploadField(string outcome, string uploadedAt)
        {
            if (string.IsNullOrEmpty(uploadedAt)) return "";
            if (!DateTime.TryParse(uploadedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)) return "";
            long unix = new DateTimeOffset(when.ToUniversalTime()).ToUnixTimeSeconds();
            return $"{outcome}|{unix}|";
        }

        private static string Seconds(string t)
        {
            t = (t ?? "").Trim();
            return t.Length == 4 ? t + "00" : t;
        }

        // Byte length, as Nexus's own writer counts it.
        private static string Field(string tag, string value) =>
            $"<{tag}:{Encoding.UTF8.GetByteCount(value)}>{value} ";

        // ── Compare ───────────────────────────────────────────────────────────────────────

        public class CompareResult
        {
            public int JimmyRows, NexusRows, Matched;
            public List<string> Differences = new List<string>();
            public List<string> NewInNexus = new List<string>();
            public List<string> Normalized = new List<string>();
            public Dictionary<string, int> DiffCountByField = new Dictionary<string, int>();
            public bool Clean => Differences.Count == 0 && Matched == JimmyRows && NexusRows == JimmyRows;
        }

        public static CompareResult Compare(List<JimmyRow> jimmy, List<NexusQso> nexus)
        {
            var res = new CompareResult { JimmyRows = jimmy.Count, NexusRows = nexus.Count };
            var byRow = new Dictionary<string, NexusQso>();
            foreach (var q in nexus)
            {
                string id = q.ExtraValue(RowIdTag);
                if (id != null) byRow[id] = q;
            }
            void Diff(JimmyRow r, string field, string want, string got)
            {
                if (string.Equals(want ?? "", got ?? "", StringComparison.Ordinal)) return;
                res.Differences.Add($"row {r.Id} {r.C("callsign")}: {field} Jimmy='{want}' Nexus='{got}'");
                res.DiffCountByField[field] = res.DiffCountByField.TryGetValue(field, out var n) ? n + 1 : 1;
            }
            foreach (var r in jimmy)
            {
                if (!byRow.TryGetValue(r.Id.ToString(CultureInfo.InvariantCulture), out var q))
                {
                    res.Differences.Add($"row {r.Id} {r.C("callsign")}: MISSING in Nexus");
                    res.DiffCountByField["missing"] = res.DiffCountByField.TryGetValue("missing", out var m) ? m + 1 : 1;
                    continue;
                }
                res.Matched++;
                Diff(r, "call", r.C("callsign"), q.Call);
                Diff(r, "band", r.C("band").ToLowerInvariant(), (q.Band ?? "").ToLowerInvariant());
                Diff(r, "mode", r.C("mode"), q.Mode);
                Diff(r, "time on", UnixOf(r.C("qso_date"), Seconds(r.C("time_on"))).ToString(), q.WhenUnix.ToString());
                Diff(r, "freq", r.N("freq_hz") > 0 ? (r.N("freq_hz") / 1e6).ToString("0.000000", CultureInfo.InvariantCulture) : "",
                     q.FreqMhz > 0 ? q.FreqMhz.ToString("0.000000", CultureInfo.InvariantCulture) : "");
                Diff(r, "rst sent", r.C("rst_sent"), q.RstSent);
                Diff(r, "rst rcvd", r.C("rst_rcvd"), q.RstRcvd);
                if (!string.Equals(r.C("state"), q.State ?? "", StringComparison.Ordinal) &&
                    string.Equals(r.C("state"), q.State ?? "", StringComparison.OrdinalIgnoreCase))
                    res.Normalized.Add($"row {r.Id} {r.C("callsign")}: state '{r.C("state")}' stored as '{q.State}' (Nexus uppercases STATE)");
                else
                    Diff(r, "state", r.C("state"), q.State);
                Diff(r, "country", r.C("country"), q.Country);
                Diff(r, "dxcc", r.N("dxcc") > 0 ? r.C("dxcc") : "", q.Dxcc?.ToString());
                Diff(r, "grid", r.C("grid"), q.Grid);
                Diff(r, "name", r.C("name"), q.Name);
                Diff(r, "comment", r.C("comment"), q.Comment);
                Diff(r, "power", PowerText(r.C("tx_pwr")), q.TxPower.HasValue ? PowerText(q.TxPower.Value.ToString(CultureInfo.InvariantCulture)) : "");
                Diff(r, "operator", r.C("operator_call"), q.Operator);
                Diff(r, "station call", r.C("station_call"), q.StationCallsign);
                Diff(r, "my grid", r.C("my_grid").ToUpperInvariant(), (q.MyGrid ?? "").ToUpperInvariant());   // a locator's case carries no meaning
                Diff(r, "LoTW confirmed", (r.C("lotw_qsl_rcvd") == "Y").ToString(), q.QslRcvd.Lotw.ToString());
                Diff(r, "QRZ confirmed", (r.C("qrz_qsl_rcvd") == "Y").ToString(), q.QslRcvd.Qrz.ToString());
                Diff(r, "paper card", "False", q.QslRcvd.Card.ToString());
                Diff(r, "time off", r.C("time_off").Length > 0 ? "set" : "", q.TimeOffUnix.HasValue ? "set" : "");
                Diff(r, "QRZ upload", Uploaded(r.C("qrz_uploaded_at")), Sent(q.Upload?.Qrz));
                Diff(r, "Club Log upload", Uploaded(r.C("clublog_uploaded_at")), Sent(q.Upload?.Clublog));
                Diff(r, "LoTW upload", Uploaded(r.C("lotw_uploaded_at")), Sent(q.Upload?.Lotw));
                Diff(r, "eQSL upload", Uploaded(r.C("eqsl_uploaded_at")), Sent(q.Upload?.Eqsl));
                // eQSL: Jimmy's column vs Nexus's channel. Nexus may ALSO have read an eQSL
                // confirmation Jimmy only kept as an extra -- new information, reported apart.
                bool jimmyEqsl = r.C("eqsl_qsl_rcvd") == "Y";
                if (!jimmyEqsl && q.QslRcvd.Eqsl && r.Extras.Any(e => e.Tag.Equals("EQSL_QSL_RCVD", StringComparison.OrdinalIgnoreCase)))
                    res.NewInNexus.Add($"row {r.Id} {r.C("callsign")}: eQSL confirmation read from a field Jimmy kept as an extra");
                else
                    Diff(r, "eQSL confirmed", jimmyEqsl.ToString(), q.QslRcvd.Eqsl.ToString());

                // Every extra Jimmy kept must still be there -- as an extra, or read into a field.
                foreach (var (tag, value) in r.Extras.GroupBy(e => e.Tag, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
                {
                    if (ColumnTags.Contains(tag) || tag.Equals("APP_NEXUS_ID", StringComparison.OrdinalIgnoreCase)) continue;
                    string kept = q.ExtraValue(tag);
                    if (kept == value) continue;
                    string modeled = ModeledValue(q, tag);
                    if (modeled != null)
                        res.NewInNexus.Add($"row {r.Id} {r.C("callsign")}: extra {tag}={value} is now read by Nexus as a field ('{modeled}')");
                    else
                        Diff(r, "extra " + tag, value, kept);
                }
            }
            foreach (var q in nexus)
                if (q.ExtraValue(RowIdTag) == null)
                    res.Differences.Add($"Nexus contact {q.Id} {q.Call}: not from the Jimmy log");
            return res;
        }

        // Where Nexus puts an ADIF field it models, for the extras Jimmy only kept verbatim.
        private static string ModeledValue(NexusQso q, string tag)
        {
            switch (tag.ToUpperInvariant())
            {
                case "QTH": return q.Qth ?? "";
                case "NOTES": return q.Notes ?? "";
                case "FREQ_RX": return q.FreqRxMhz?.ToString(CultureInfo.InvariantCulture) ?? "";
                case "EQSL_QSL_RCVD": return q.QslRcvd.Eqsl ? "Y" : "N";
                case "QSL_SENT": case "QSL_SENT_VIA": case "QSLSDATE": return q.QslSent.Sent ? "sent" : "not sent";
                case "CREDIT_GRANTED": return string.Join(",", q.CreditGranted);
                case "CREDIT_SUBMITTED": return string.Join(",", q.CreditSubmitted);
                case "PROP_MODE": return q.PropMode ?? "";
                case "SAT_NAME": return q.SatName ?? "";
                case "MY_RIG": return q.MyRig ?? "";
                case "SOTA_REF": case "MY_SOTA_REF": case "POTA_REF": case "MY_POTA_REF":
                    return $"{q.Ota.TheirRef}/{q.Ota.MyRef}";
                default: return null;
            }
        }

        private static string Uploaded(string at) => string.IsNullOrEmpty(at) ? "owed" : "sent";
        private static string Sent(NexusUploadStatus s) => s != null && s.IsSent ? "sent" : "owed";
        private static string PowerText(string p) =>
            double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w.ToString("0.###", CultureInfo.InvariantCulture) : (p ?? "");

        public static long UnixOf(string yyyymmdd, string hhmmss)
        {
            if (!DateTime.TryParseExact(yyyymmdd + (hhmmss ?? "").PadRight(6, '0').Substring(0, 6), "yyyyMMddHHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                return 0;
            return new DateTimeOffset(dt, TimeSpan.Zero).ToUnixTimeSeconds();
        }

        public static string Report(CompareResult c, WriteResult w = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Jimmy rows: {c.JimmyRows}   Nexus rows: {c.NexusRows}   matched: {c.Matched}");
            sb.AppendLine(c.Clean ? "RESULT: no differences." : $"RESULT: {c.Differences.Count} difference(s).");
            foreach (var kv in c.DiffCountByField.OrderByDescending(k => k.Value))
                sb.AppendLine($"  {kv.Key}: {kv.Value}");
            if (c.Differences.Count > 0) { sb.AppendLine("Differences:"); foreach (var d in c.Differences) sb.AppendLine("  " + d); }
            if (c.Normalized.Count > 0) { sb.AppendLine($"Normalised by Nexus, same meaning ({c.Normalized.Count}):"); foreach (var d in c.Normalized) sb.AppendLine("  " + d); }
            if (c.NewInNexus.Count > 0) { sb.AppendLine($"New information Nexus reads ({c.NewInNexus.Count}):"); foreach (var d in c.NewInNexus) sb.AppendLine("  " + d); }
            if (w != null && w.Notes.Count > 0) { sb.AppendLine($"Writer notes ({w.Notes.Count}):"); foreach (var d in w.Notes) sb.AppendLine("  " + d); }
            return sb.ToString();
        }

        // ── Nexus -> Jimmy (the rollback) ─────────────────────────────────────────────────

        public class RebuildResult
        {
            public int Rows;
            public List<string> Notes = new List<string>();
        }

        // Builds a NEW Jimmy-format logbook.db at outDbPath (which must not exist) from Nexus's
        // current rows. Original row ids are kept where the contact came from Jimmy.
        // idFor: a STABLE Jimmy row id for a contact that has none from Jimmy (logged after the
        // migration) -- the read projection passes NexusLogbook's persistent map, so a row keeps its
        // id across rebuilds and an edit or delete can never land on a different contact. Without
        // it (the rollback), such contacts are numbered after the highest Jimmy id.
        public static RebuildResult Rebuild(List<NexusQso> nexus, string outDbPath, Func<NexusQso, long> idFor = null)
        {
            if (File.Exists(outDbPath)) throw new IOException("Rebuild target already exists: " + outDbPath);
            using (new LogbookDb(outDbPath)) { } // Jimmy's own schema, current version
            var res = new RebuildResult();
            var usedKeys = new HashSet<string>(StringComparer.Ordinal);
            long nextId = nexus.Select(q => long.TryParse(q.ExtraValue(RowIdTag), out var id) ? id : 0).DefaultIfEmpty(0).Max() + 1;
            using (var conn = new SQLiteConnection($"Data Source={outDbPath};"))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    foreach (var q in nexus)
                    {
                        long id = long.TryParse(q.ExtraValue(RowIdTag), out var rid) ? rid : idFor != null ? idFor(q) : nextId++;
                        var when = DateTimeOffset.FromUnixTimeSeconds((long)q.WhenUnix).UtcDateTime;
                        string date = when.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                        string timeOn = when.ToString("HHmm", CultureInfo.InvariantCulture);
                        string timeOff = q.TimeOffUnix.HasValue
                            ? DateTimeOffset.FromUnixTimeSeconds((long)q.TimeOffUnix.Value).UtcDateTime.ToString("HHmmss", CultureInfo.InvariantCulture) : "";
                        string source = q.ExtraValue(SourceTag) ?? "WSJTX";
                        string key = AdifImporter.BuildDedupKey(q.Call, (q.Band ?? "").ToLowerInvariant(), q.Mode, date, timeOn);
                        if (!usedKeys.Add(key))
                        {
                            // Jimmy's key is per minute; Nexus may hold two contacts it counts as one.
                            // Keep both: the contact matters more than the key's shape.
                            res.Notes.Add($"{q.Call} {date} {timeOn}: shares Jimmy's per-minute key with another contact; kept with a distinct key");
                            key = key + "|" + q.Id;
                            usedKeys.Add(key);
                        }
                        var col = new Dictionary<string, object>
                        {
                            ["id"] = id, ["callsign"] = q.Call ?? "", ["band"] = (q.Band ?? "").ToLowerInvariant(), ["mode"] = q.Mode ?? "",
                            ["qso_date"] = date, ["time_on"] = timeOn, ["time_off"] = timeOff,
                            ["freq_hz"] = (long)Math.Round(q.FreqMhz * 1_000_000),
                            ["rst_sent"] = q.RstSent ?? "", ["rst_rcvd"] = q.RstRcvd ?? "",
                            ["state"] = q.State ?? "", ["country"] = q.Country ?? "", ["dxcc"] = (long)(q.Dxcc ?? 0),
                            ["cq_zone"] = ExtraLong(q, "CQZ"), ["itu_zone"] = ExtraLong(q, "ITUZ"),
                            ["continent"] = q.ExtraValue("CONT") ?? "", ["county"] = q.ExtraValue("CNTY") ?? "",
                            ["iota"] = !string.IsNullOrEmpty(q.Ota?.Iota) ? q.Ota.Iota : (q.ExtraValue(IotaRawTag) ?? q.ExtraValue("IOTA") ?? ""),
                            ["sig"] = q.ExtraValue("SIG") ?? SigOf(q.Ota?.TheirProgram), ["sig_info"] = q.ExtraValue("SIG_INFO") ?? (q.Ota?.TheirRef ?? ""),
                            ["my_sig"] = q.ExtraValue("MY_SIG") ?? SigOf(q.Ota?.MyProgram), ["my_sig_info"] = q.ExtraValue("MY_SIG_INFO") ?? (q.Ota?.MyRef ?? ""),
                            ["darc_dok"] = q.ExtraValue("DARC_DOK") ?? "", ["wpx_prefix"] = q.ExtraValue("PFX") ?? "",
                            ["grid"] = q.Grid ?? "", ["name"] = q.Name ?? "", ["comment"] = q.Comment ?? "",
                            ["tx_pwr"] = q.TxPower.HasValue ? PowerText(q.TxPower.Value.ToString(CultureInfo.InvariantCulture)) : "",
                            ["operator_call"] = q.Operator ?? "", ["station_call"] = q.StationCallsign ?? "", ["my_grid"] = q.MyGrid ?? "",
                            ["lotw_qsl_sent"] = q.ExtraValue(LotwQslSentRawTag) ?? "",
                            ["lotw_qsl_rcvd"] = q.QslRcvd.Lotw ? "Y" : (q.ExtraValue(LotwQslRcvdRawTag) is string raw && raw != "Y" ? raw : ""),
                            ["qrz_qsl_sent"] = q.ExtraValue(QrzQslSentTag) ?? "",
                            ["qrz_qsl_rcvd"] = q.QslRcvd.Qrz ? "Y" : "",
                            ["eqsl_qsl_rcvd"] = q.QslRcvd.Eqsl ? "Y" : "",
                            ["source"] = source, ["source_qso_id"] = q.ExtraValue(SourceQsoIdTag) ?? "",
                            ["imported_at"] = q.ExtraValue(ImportedAtTag) ?? DateTime.UtcNow.ToString("o"),
                            ["modified_at"] = q.ExtraValue(ModifiedAtTag) ?? "",
                            ["dedup_key"] = key,
                            ["qrz_uploaded_at"] = UploadedAt(q.Upload?.Qrz), ["clublog_uploaded_at"] = UploadedAt(q.Upload?.Clublog),
                            ["lotw_uploaded_at"] = UploadedAt(q.Upload?.Lotw), ["eqsl_uploaded_at"] = UploadedAt(q.Upload?.Eqsl),
                            ["hrdlog_uploaded_at"] = q.ExtraValue(HrdlogUploadedTag) ?? "",
                            ["exchange_sent"] = q.ExtraValue("STX_STRING") ?? "", ["exchange_rcvd"] = q.ExtraValue("SRX_STRING") ?? "",
                            ["contest_id"] = q.ExtraValue("CONTEST_ID") ?? "", ["contest_session_id"] = q.ExtraValue(ContestSessionTag) ?? "",
                        };
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText = $"INSERT INTO qso ({string.Join(",", col.Keys)}) VALUES ({string.Join(",", col.Keys.Select(k => "@" + k))});";
                            foreach (var kv in col) cmd.Parameters.AddWithValue("@" + kv.Key, kv.Value);
                            cmd.ExecuteNonQuery();
                        }
                        int ord = 0;
                        foreach (var (tag, value) in ExtrasBack(q))
                        {
                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.Transaction = tx;
                                cmd.CommandText = "INSERT INTO qso_extra_field (qso_id, tag_name, tag_value, ordinal) VALUES (@i,@t,@v,@o);";
                                cmd.Parameters.AddWithValue("@i", id);
                                cmd.Parameters.AddWithValue("@t", tag);
                                cmd.Parameters.AddWithValue("@v", value);
                                cmd.Parameters.AddWithValue("@o", ord++);
                                cmd.ExecuteNonQuery();
                            }
                        }
                        res.Rows++;
                    }
                    tx.Commit();
                }
            }
            return res;
        }

        // The extras a rebuilt row keeps: everything Nexus holds that Jimmy has no column for,
        // including Nexus-only fields, so nothing Nexus learned is lost by going back.
        private static IEnumerable<(string, string)> ExtrasBack(NexusQso q)
        {
            foreach (var kv in q.Extra)
            {
                if (kv.Count != 2 || ColumnTags.Contains(kv[0]) || kv[0] == LogbookHostReqIdTag) continue;
                if (kv[0].StartsWith(RawExtraPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    // A reduced extra: back under its own name unless Nexus now says yes.
                    string orig = kv[0].Substring(RawExtraPrefix.Length);
                    if (orig.Equals("EQSL_QSL_RCVD", StringComparison.OrdinalIgnoreCase) && q.QslRcvd.Eqsl) continue;
                    yield return (orig, kv[1]);
                    continue;
                }
                yield return (kv[0], kv[1]);
            }
            if (!string.IsNullOrEmpty(q.Qth)) yield return ("QTH", q.Qth);
            if (!string.IsNullOrEmpty(q.Notes)) yield return ("NOTES", q.Notes);
            if (q.FreqRxMhz.HasValue) yield return ("FREQ_RX", q.FreqRxMhz.Value.ToString("0.######", CultureInfo.InvariantCulture));
            if (q.CreditGranted.Count > 0) yield return ("CREDIT_GRANTED", string.Join(",", q.CreditGranted));
            if (q.CreditSubmitted.Count > 0) yield return ("CREDIT_SUBMITTED", string.Join(",", q.CreditSubmitted));
            if (q.QslRcvd.Card) yield return ("QSL_RCVD", "Y");
            foreach (var (svc, st) in new[] { ("QRZ", q.Upload?.Qrz), ("CLUBLOG", q.Upload?.Clublog), ("LOTW", q.Upload?.Lotw), ("EQSL", q.Upload?.Eqsl) })
                if (st != null && !st.IsSent) yield return ("APP_TEMPO_UL_" + svc, $"{st.Outcome}|{st.WhenUnix}|{st.Detail}");
            if (!string.IsNullOrEmpty(q.Id)) yield return ("APP_NEXUS_ID", q.Id);
        }

        // Jimmy's request id on a contact Jimmy logged through Nexus -- Nexus-side bookkeeping only.
        private const string LogbookHostReqIdTag = "APP_JIMMY_REQ_ID";

        private static string SigOf(string program) => string.IsNullOrEmpty(program) ? "" : program;
        private static long ExtraLong(NexusQso q, string tag) => long.TryParse(q.ExtraValue(tag), out var n) ? n : 0;
        private static string UploadedAt(NexusUploadStatus s) =>
            s != null && s.IsSent ? DateTimeOffset.FromUnixTimeSeconds(s.WhenUnix).UtcDateTime.ToString("o") : "";
    }
}
