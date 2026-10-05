using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace WSJTX_Controller
{
    // Logbook migration Phase 5: ILogbookService with Nexus as the owner (see NexusLogbook).
    //   Reads  -> the read-only projection (NexusLogbook.Read), so every screen, stat, award
    //             query and worked-before lookup behaves exactly as on Jimmy's own database.
    //   Writes -> Nexus: a new contact through the durable outbox (LOG_QSO, idempotent by request
    //             id); imports and downloads as a file through Nexus's own import / merge;
    //             edits and deletes by Nexus id with the row's edit key; upload results as Nexus
    //             upload stamps. After a write the projection is refreshed.
    // Nothing is ever written to the projection itself.
    public sealed class NexusLogbookService : ILogbookService
    {
        public void Dispose() { }

        // What the last Upsert through this service came to: "saved", "already", "duplicate", or
        // "queued" (safe in the outbox, not yet confirmed by Nexus). Lets a screen say exactly that.
        public string LastLogState { get; private set; }

        private static NexusLogClient Client => NexusLogbook.Client;
        private static T R<T>(Func<LogbookDb, T> read, T fallback) => NexusLogbook.Read(read, fallback);

        // ── New contacts ───────────────────────────────────────────────────────────────

        // Builds the contact Nexus logs from Upsert's fields (the same fields Jimmy stores).
        private static NexusQso ToQso(string callsign, string band, string mode, string qsoDate, string timeOn,
            string timeOff, long freqHz, string rstSent, string rstRcvd, string state, string country, int dxcc,
            int cqZone, string grid, string name, string comment, string txPwr, string operatorCall,
            string stationCall, string myGrid, string source, string sourceQsoId,
            string continent, int ituZone, string county, string iota, string sig, string sigInfo,
            string mySig, string mySigInfo, string darcDok, string wpxPrefix, string exchangeSent, string exchangeRcvd)
        {
            ulong when = (ulong)Math.Max(0, NexusMigration.UnixOf(qsoDate, Pad6(timeOn)));
            ulong? off = null;
            if (!string.IsNullOrWhiteSpace(timeOff))
            {
                long o = NexusMigration.UnixOf(qsoDate, Pad6(timeOff));
                if (o > 0 && (ulong)o < when) o += 86_400;
                if (o > 0) off = (ulong)o;
            }
            var extra = new List<List<string>>();
            void X(string t, string v) { if (!string.IsNullOrEmpty(v)) extra.Add(new List<string> { t, v }); }
            if (cqZone > 0) X("CQZ", cqZone.ToString(CultureInfo.InvariantCulture));
            if (ituZone > 0) X("ITUZ", ituZone.ToString(CultureInfo.InvariantCulture));
            X("CONT", continent); X("CNTY", county); X("DARC_DOK", darcDok); X("PFX", wpxPrefix);
            // A POTA park goes in Nexus's own park fields (2026-10-04): Nexus writes SIG/SIG_INFO and
            // POTA_REF from them, a list of parks included. POTA without a park ("CQ POTA", no park
            // chosen) has no park to put there, so SIG alone stays an ADIF field, as before.
            bool theirPark = IsPota(sig) && !string.IsNullOrWhiteSpace(sigInfo);
            bool myPark = IsPota(mySig) && !string.IsNullOrWhiteSpace(mySigInfo);
            if (!theirPark) { X("SIG", sig); X("SIG_INFO", sigInfo); }
            if (!myPark) { X("MY_SIG", mySig); X("MY_SIG_INFO", mySigInfo); }
            X("STX_STRING", exchangeSent); X("SRX_STRING", exchangeRcvd);
            X(NexusMigration.SourceTag, source); X(NexusMigration.SourceQsoIdTag, sourceQsoId);
            X(NexusMigration.ImportedAtTag, DateTime.UtcNow.ToString("o"));
            double? power = double.TryParse(txPwr, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : (double?)null;
            return new NexusQso
            {
                Call = (callsign ?? "").Trim().ToUpperInvariant(), Band = band ?? "", Mode = mode ?? "",
                WhenUnix = when, TimeOffUnix = off, TimeKnown = true,
                FreqMhz = freqHz > 0 ? freqHz / 1_000_000.0 : 0,
                RstSent = Blank(rstSent), RstRcvd = Blank(rstRcvd), State = Blank(state), Country = Blank(country),
                Dxcc = dxcc > 0 ? (uint)dxcc : (uint?)null, Grid = Blank(grid), Name = Blank(name), Comment = Blank(comment),
                TxPower = power, Operator = Blank(operatorCall), StationCallsign = Blank(stationCall), MyGrid = Blank(myGrid),
                Ota = new NexusOta
                {
                    Iota = Blank(iota),
                    TheirProgram = theirPark ? "POTA" : null, TheirRef = theirPark ? sigInfo.Trim().ToUpperInvariant() : null,
                    MyProgram = myPark ? "POTA" : null, MyRef = myPark ? mySigInfo.Trim().ToUpperInvariant() : null,
                },
                Extra = extra,
            };
        }

        private static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        private static bool IsPota(string sig) => string.Equals((sig ?? "").Trim(), "POTA", StringComparison.OrdinalIgnoreCase);

        // ── Our operating location, recorded on every contact Jimmy Next creates (2026-10-04) ──
        // Set by the owner: the ADIF fields of where WE are operating now (MY_STATE, MY_CNTY,
        // MY_DXCC, MY_COUNTRY, the TQSL station location in force and its fingerprint, a review
        // note when the profile and that location disagree) and our own park when activating.
        // Recorded at logging, so a later profile change or restart cannot move the contact.
        public static Func<List<(string Tag, string Value)>> OurLocationFields;
        public static Func<string> OurPark;

        internal static void StampOurLocation(NexusQso q)
        {
            if (q == null) return;
            q.Extra = q.Extra ?? new List<List<string>>();
            List<(string Tag, string Value)> fields = null;
            try { fields = OurLocationFields?.Invoke(); } catch { }
            foreach (var (tag, value) in fields ?? new List<(string, string)>())
            {
                if (string.IsNullOrEmpty(value)) continue;
                var have = q.Extra.FirstOrDefault(e => e.Count > 1 && string.Equals(e[0], tag, StringComparison.OrdinalIgnoreCase));
                if (have == null) q.Extra.Add(new List<string> { tag, value });
                else if (string.Equals(tag, "APP_JIMMY_REVIEW", StringComparison.OrdinalIgnoreCase) && !have[1].Contains(value))
                    have[1] = have[1] + "; " + value;
            }
            string park = null;
            try { park = OurPark?.Invoke(); } catch { }
            if (!string.IsNullOrWhiteSpace(park) && q.Ota != null && string.IsNullOrEmpty(q.Ota.MyRef))
            {
                q.Ota.MyProgram = "POTA";
                q.Ota.MyRef = park.Trim().ToUpperInvariant();
            }
        }
        private static string Pad6(string t) { t = (t ?? "").Trim(); return t.Length == 4 ? t + "00" : t; }

        // A request id per contact: re-sending the same contact (a retry, a lost reply) names the
        // same Nexus record, so it can never be logged twice.
        public static string RequestIdFor(string source, string dedupKey) => $"{source}:{dedupKey}";

        // The request id namespace of Jimmy Next's own live contacts. Deliberately still "WSJTX"
        // though their Source is now "Jimmy Next": the request id is the contact's identity in
        // Nexus (RecordIdForRequest), so a contact queued before the change and retried after it
        // is still the same contact -- never logged twice.
        public const string LiveRequestPrefix = "WSJTX";

        // Queue durably, then send now. The contact is safe once queued (the outbox file is flushed
        // to disk); "saved" is only claimed from Nexus's own answer.
        private static NexusLogQsoReply Log(string reqId, NexusQso q, bool sendNow)
        {
            var outbox = NexusLogbook.Outbox;
            outbox.Add(reqId, q);
            if (!sendNow) return new NexusLogQsoReply { State = "queued" };
            var entry = outbox.Snapshot().FirstOrDefault(e => e.ReqId == reqId);
            var reply = entry != null ? outbox.Send(Client, entry) : new NexusLogQsoReply { State = "already" };
            if (reply.State == "saved" || reply.State == "already") NexusLogbook.Refresh();
            return reply;
        }

        public (bool isNew, bool newlyConfirmed, bool corrected) Upsert(
            string callsign, string band, string mode, string qsoDate, string timeOn, string timeOff,
            long freqHz, string rstSent, string rstRcvd, string state, string country, int dxcc, int cqZone,
            string grid, string name, string comment, string txPwr, string operatorCall, string stationCall, string myGrid,
            string lotwQslSent, string lotwQslRcvd, string qrzQslSent, string qrzQslRcvd,
            string source, string sourceQsoId, string dedupKey, string continent, int ituZone, string county, string iota,
            string sig, string sigInfo, string mySig, string mySigInfo, string darcDok, string wpxPrefix,
            string exchangeSent, string exchangeRcvd)
        {
            var q = ToQso(callsign, band, mode, qsoDate, timeOn, timeOff, freqHz, rstSent, rstRcvd, state, country, dxcc,
                cqZone, grid, name, comment, txPwr, operatorCall, stationCall, myGrid, source, sourceQsoId,
                continent, ituZone, county, iota, sig, sigInfo, mySig, mySigInfo, darcDok, wpxPrefix, exchangeSent, exchangeRcvd);
            StampOurLocation(q);
            var reply = Log(RequestIdFor(source ?? "MANUAL", dedupKey), q, sendNow: true);
            LastLogState = reply.State == "saved" || reply.State == "already" || reply.State == "duplicate" ? reply.State : "queued";
            if (reply.State == "duplicate") return (false, false, false);
            if (reply.State == "saved" || reply.State == "already" || reply.State == "unconfirmed" || reply.State == "unknown" || reply.State == "closed")
                return (reply.State == "saved", false, false); // queued: the outbox sends it later
            throw new InvalidOperationException("logbook: " + reply.State + " " + reply.Why);
        }

        // Jimmy's own live-logged contact (RequestLog -> LiveQsoUploadOrchestrator): queued durably
        // on the calling (UI) thread, sent on a background thread -- a disk that is slow to confirm
        // must not stall the operating screen. Returns the request id.
        public static string QueueLiveContact(Dictionary<string, string> f, string dedupKey)
        {
            string G(string k) => f.TryGetValue(k, out var v) ? v : "";
            long.TryParse(((double.TryParse(G("FREQ"), NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz) ? mhz : 0) * 1_000_000).ToString("0", CultureInfo.InvariantCulture), out long hz);
            int.TryParse(G("DXCC"), out int dxcc);
            int.TryParse(G("CQZ"), out int cqz);
            int.TryParse(G("ITUZ"), out int ituz);
            var q = ToQso(G("CALL"), G("BAND").ToLowerInvariant(), G("MODE"), G("QSO_DATE"), G("TIME_ON"), G("TIME_OFF"), hz,
                G("RST_SENT"), G("RST_RCVD"), G("STATE"), G("COUNTRY"), dxcc, cqz, G("GRIDSQUARE"), G("NAME"), G("COMMENT"),
                G("TX_PWR"), G("OPERATOR"), G("STATION_CALLSIGN"), G("MY_GRIDSQUARE"), QsoRecord.JimmyNextSource, "",
                G("CONT"), ituz, G("CNTY"), G("IOTA"), G("SIG"), G("SIG_INFO"), G("MY_SIG"), G("MY_SIG_INFO"), "", G("PFX"), "", "");
            foreach (var kv in f)
                if (kv.Key.StartsWith("APP_JIMMY_", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(kv.Value))
                    q.Extra.Add(new List<string> { kv.Key.ToUpperInvariant(), kv.Value });
            StampOurLocation(q);
            string reqId = RequestIdFor(LiveRequestPrefix, dedupKey);
            Log(reqId, q, sendNow: false);
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var outbox = NexusLogbook.Outbox;
                    var entry = outbox.Snapshot().FirstOrDefault(e => e.ReqId == reqId);
                    if (entry == null) return;
                    var r = outbox.Send(Client, entry);
                    if (r.State == "saved" || r.State == "already") NexusLogbook.Refresh();
                }
                catch { /* stays queued; the upkeep worker sends it */ }
            });
            return reqId;
        }

        // A contest completion as ONE durable write (ContestWorkflow): the contact with its contest
        // association and structured received exchange. Throws unless Nexus has it on disk, so the
        // caller never acknowledges a completion that is not saved.
        public void LogContestCompletion(string reqId, NexusQso q, string contestId, string sessionInstanceId,
            List<(string Tag, string Value)> rcvdPairs)
        {
            StampOurLocation(q);
            q.Extra.Add(new List<string> { "CONTEST_ID", contestId ?? "" });
            q.Extra.Add(new List<string> { NexusMigration.ContestSessionTag, sessionInstanceId ?? "" });
            foreach (var (tag, value) in rcvdPairs ?? new List<(string, string)>())
                q.Extra.Add(new List<string> { NexusMigration.ContestRxPrefix + tag.ToUpperInvariant(), value ?? "" });
            var reply = Log(reqId, q, sendNow: true);
            if (reply.State != "saved" && reply.State != "already")
                throw new InvalidOperationException($"contest contact not saved: {reply.State} {reply.Why}");
        }

        // ── Imports and downloads (AdifImporter.Import, EqslReconciler) ─────────────────────

        // The records back into ADIF text, in their own order. LoTW data gets LoTW's own report
        // header: Nexus reads QSL_RCVD as LoTW's confirmation only under it, never as a card.
        public static string ToAdifText(IEnumerable<AdifRawRecord> records, string source)
        {
            var sb = new StringBuilder();
            if (source == "LOTW")
                sb.Append("ARRL Logbook of the World Status Report\n<PROGRAMID:4>LoTW\n<eoh>\n");
            else
                sb.Append("Jimmy Next\n<eoh>\n");
            foreach (var r in records)
            {
                IEnumerable<KeyValuePair<string, string>> fields = r.Ordered != null && r.Ordered.Count > 0
                    ? r.Ordered.Select(o => new KeyValuePair<string, string>(o.Tag, o.Value))
                    : r.Fields;
                bool hasSource = false;
                foreach (var kv in fields)
                    if (!string.IsNullOrEmpty(kv.Value))
                    {
                        if (string.Equals(kv.Key, NexusMigration.SourceTag, StringComparison.OrdinalIgnoreCase)) hasSource = true;
                        sb.Append('<').Append(kv.Key).Append(':').Append(Encoding.UTF8.GetByteCount(kv.Value)).Append('>').Append(kv.Value).Append(' ');
                    }
                // Where the contact came from (2026-10-04: imports since the move to Nexus kept no
                // source, so the read copy showed them all as "WSJTX"). A record that already says
                // keeps its own. Only a contact the import ADDS takes it: a merge that matches a
                // contact already logged changes only its confirmations and upload stamps
                // (Nexus reconcile::apply_match), never its extra fields.
                if (!hasSource && !string.IsNullOrEmpty(source))
                    sb.Append('<').Append(NexusMigration.SourceTag).Append(':').Append(Encoding.UTF8.GetByteCount(source)).Append('>').Append(source).Append(' ');
                sb.Append("<eor>\n");
            }
            return sb.ToString();
        }

        // Nexus's own import for the source: LoTW / QRZ / eQSL downloads merge (monotonic; QRZ also
        // adds what the log lacks, as Jimmy's QRZ download always did); everything else imports.
        // A LoTW or QRZ download merged so that every confirmation lands on its own contact:
        // NexusReportPairing hands Nexus only the rows its pairing is certain to put on the contact
        // at the same minute; the rest are held (nothing changes for them), counted, and saved for
        // review under NexusLog\diagnostics. Contacts still queued for Nexus are sent first, so the
        // pairing sees the whole log; if any stay queued, nothing is merged.
        public ImportResult MergeDownload(string adifText, string source)
        {
            if (NexusLogbook.Outbox.Count > 0) NexusLogbook.Outbox.Replay(Client);
            if (NexusLogbook.Outbox.Count > 0)
                return new ImportResult { Errors = "Nexus logbook: contacts are still waiting to be saved; nothing was merged. Try again shortly." };
            var rows = Client.Rows();
            if (rows.Error != null)
                return new ImportResult { Errors = "Nexus logbook: could not read the log to pair the download: " + rows.Error };
            var prep = NexusReportPairing.Prepare(adifText, rows.Rows, nearbyUnique: source == "EQSL");
            var result = ImportFile(prep.Text, source);
            // "Newly confirmed" = contacts that gained a confirmation (LoTW, QRZ, eQSL or card) in
            // this merge, read from the log itself: Nexus's own count covers LoTW and card only, and
            // its "any source" count misses a contact already confirmed another way.
            var afterRows = string.IsNullOrEmpty(result.Errors) ? Client.Rows() : null;
            if (afterRows?.Error == null && afterRows != null)
            {
                var was = rows.Rows.Where(q => q.Id != null).ToDictionary(q => q.Id, q => q.QslRcvd);
                int gained = afterRows.Rows.Count(q => q.Id != null && was.TryGetValue(q.Id, out var b) &&
                    ((q.QslRcvd.Lotw && !b.Lotw) || (q.QslRcvd.Qrz && !b.Qrz) || (q.QslRcvd.Eqsl && !b.Eqsl) || (q.QslRcvd.Card && !b.Card)));
                result.Skipped = Math.Max(0, result.Skipped + result.NewlyConfirmed - gained);
                result.NewlyConfirmed = gained;
            }
            if (string.IsNullOrEmpty(result.Errors)) KeepDownloadedLocations(prep, source, afterRows);
            result.Held = prep.Held;
            result.HeldDetails.AddRange(prep.HeldDetails);
            NexusSyncDiagnostics.WriteList("held-" + source.ToLowerInvariant(),
                $"{source} rows held back -- not merged, nothing changed -- because Nexus could not be sure to pair them with the right contact", prep.HeldDetails);
            return result;
        }

        // After a merge (2026-10-04): the logged location stays as logged. LoTW's confirmed
        // location of each contact it confirmed is kept beside it, in APP_JIMMY_LOTW_* fields
        // (STATE, CNTY, GRID, DXCC, CQZ, ITUZ) -- the evidence awards count for that contact. Every
        // download row whose location differs from, or would have filled, the logged one is listed
        // under NexusLog\diagnostics, so nothing it said is lost from view.
        private static readonly (string Row, string Extra)[] LotwLocationFields =
        {
            ("STATE", "APP_JIMMY_LOTW_STATE"), ("CNTY", "APP_JIMMY_LOTW_CNTY"), ("GRIDSQUARE", "APP_JIMMY_LOTW_GRID"),
            ("DXCC", "APP_JIMMY_LOTW_DXCC"), ("CQZ", "APP_JIMMY_LOTW_CQZ"), ("ITUZ", "APP_JIMMY_LOTW_ITUZ"),
        };

        private void KeepDownloadedLocations(NexusReportPairing.Result prep, string source, NexusLogRows afterRows)
        {
            var after = afterRows?.Error == null && afterRows != null
                ? afterRows.Rows.Where(q => q.Id != null).GroupBy(q => q.Id).ToDictionary(g => g.Key, g => g.First())
                : new Dictionary<string, NexusQso>();
            var differs = new List<string>();
            bool changed = false;
            foreach (var (logged, loc, label) in prep.Located)
            {
                string V(string t) => loc.TryGetValue(t, out var v) ? v : "";
                var notes = new List<string>();
                void Cmp(string name, string mine, string theirs)
                {
                    if (theirs.Length == 0) return;
                    if (string.IsNullOrWhiteSpace(mine)) notes.Add($"{name} blank in the log, {source} says {theirs}");
                    else if (!string.Equals(mine.Trim(), theirs, StringComparison.OrdinalIgnoreCase)) notes.Add($"{name} {mine.Trim()} in the log, {source} says {theirs}");
                }
                Cmp("state", logged.State, V("STATE"));
                Cmp("country", logged.Country, V("COUNTRY"));
                if (logged.Dxcc.HasValue && logged.Dxcc.Value > 0) Cmp("DXCC", logged.Dxcc.Value.ToString(), V("DXCC"));
                if (notes.Count > 0) differs.Add($"{label}: {string.Join("; ", notes)}");

                // Kept for each service that now confirms the contact (2026-10-05: QRZ and eQSL too,
                // as APP_JIMMY_QRZ_* / APP_JIMMY_EQSL_*). Awards use LoTW's; the others are kept and
                // exported, for the record.
                if (logged.Id == null || !after.TryGetValue(logged.Id, out var now)) continue;
                bool confirmedHere = source == "LOTW" ? now.QslRcvd?.Lotw == true
                    : source == "QRZ" ? now.QslRcvd?.Qrz == true
                    : source == "EQSL" && now.QslRcvd?.Eqsl == true;
                if (!confirmedHere) continue;
                var set = new List<string[]>();
                foreach (var (rowTag, lotwExtra) in LotwLocationFields)
                {
                    string extra = lotwExtra.Replace("APP_JIMMY_LOTW_", "APP_JIMMY_" + source + "_");
                    string want = V(rowTag);
                    if (want.Length > 0 && !string.Equals(now.ExtraValue(extra) ?? "", want, StringComparison.Ordinal))
                        set.Add(new[] { extra, want });
                }
                if (set.Count == 0) continue;
                var reply = Client.SetExtra(logged.Id, set);
                if (reply.State == "saved") changed = true;
                else differs.Add($"{label}: {source}'s confirmed location not kept -- {reply.State} {reply.Why}".Trim());
            }
            NexusSyncDiagnostics.WriteList("location-" + source.ToLowerInvariant(),
                $"{source} rows whose location differs from the logged contact -- the logged location was kept", differs);
            if (changed) NexusLogbook.Refresh();
        }

        public ImportResult ImportFile(string adifText, string source)
        {
            string kind = source == "LOTW" ? "lotw" : source == "QRZ" ? "qrz" : source == "EQSL" ? "eqsl" : null;
            string tmp = Path.Combine(Path.GetTempPath(), $"jimmy-nexus-import-{Guid.NewGuid():N}.adi");
            File.WriteAllText(tmp, adifText, new UTF8Encoding(false));
            try
            {
                var reply = kind != null ? Client.Merge(kind, tmp) : Client.Import(tmp);
                var result = new ImportResult();
                if (reply.State != "saved")
                {
                    result.Errors = $"Nexus logbook: {reply.State} {reply.Why}".Trim();
                    return result;
                }
                if (reply.Detail is System.Text.Json.JsonElement d)
                {
                    int N(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt32() : 0;
                    result.NewQsos = kind == null ? N("added") : N("added");
                    result.NewlyConfirmed = N("newlyConfirmed");
                    result.Skipped = kind == null ? N("skipped") : Math.Max(0, N("matched") - N("newlyConfirmed"));
                    result.Processed = kind == null ? N("added") + N("skipped") : N("matched") + N("added") + N("orphans");
                    result.Unmatched = kind == null ? 0 : N("orphans");
                    if (d.TryGetProperty("unmatched", out var list) && list.ValueKind == System.Text.Json.JsonValueKind.Array)
                        foreach (var u in list.EnumerateArray())
                        {
                            string S(string k) => u.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : "";
                            long when = u.TryGetProperty("whenUnix", out var w) && w.ValueKind == System.Text.Json.JsonValueKind.Number ? w.GetInt64() : 0;
                            result.UnmatchedDetails.Add($"{S("call")} {S("band")} {S("mode")} {DateTimeOffset.FromUnixTimeSeconds(when).UtcDateTime:yyyy-MM-dd HH:mm}Z: {S("reason")}");
                        }
                    NexusSyncDiagnostics.WriteUnmatched(source, result.UnmatchedDetails);
                }
                NexusLogbook.Refresh();
                return result;
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        // ── Edits and deletes (by the projection row's Nexus id) ─────────────────────────────

        private static (NexusQso row, string error) NexusRowFor(long jimmyRowId)
        {
            string nexusId = R(db => db.GetExtraFields(jimmyRowId).FirstOrDefault(e => e.Tag == "APP_NEXUS_ID").Value, null);
            if (string.IsNullOrEmpty(nexusId)) return (null, "That contact is not in the logbook view -- refresh and try again.");
            var rows = Client.Rows();
            if (rows.Error != null) return (null, "The logbook could not be read: " + rows.Error);
            var row = rows.Rows.FirstOrDefault(q => q.Id == nexusId);
            return row == null ? (null, "That contact changed or was removed -- refresh and try again.") : (row, null);
        }

        public bool UpdateQso(int id, string callsign, string band, string mode, string qsoDate, string timeOn,
            string timeOff, string state, string country, string grid, string name, string rstSent, string rstRcvd, string comment)
        {
            var (row, error) = NexusRowFor(id);
            if (row == null) throw new InvalidOperationException(error);
            row.Call = (callsign ?? "").Trim().ToUpperInvariant();
            row.Band = (band ?? "").Trim().ToLowerInvariant();
            row.Mode = (mode ?? "").Trim().ToUpperInvariant();
            long when = NexusMigration.UnixOf((qsoDate ?? "").Trim(), Pad6(timeOn));
            if (when > 0) row.WhenUnix = (ulong)when;
            if (!string.IsNullOrWhiteSpace(timeOff))
            {
                long off = NexusMigration.UnixOf((qsoDate ?? "").Trim(), Pad6(timeOff));
                if (off > 0 && off < when) off += 86_400;
                if (off > 0) row.TimeOffUnix = (ulong)off;
            }
            row.State = Blank((state ?? "").ToUpperInvariant());
            row.Country = Blank(country);
            row.Grid = Blank((grid ?? "").ToUpperInvariant());
            row.Name = Blank(name);
            row.RstSent = Blank(rstSent);
            row.RstRcvd = Blank(rstRcvd);
            row.Comment = Blank(comment);
            var reply = Client.Edit(row.Id, row.EditKey, row);
            if (reply.State != "saved") throw new InvalidOperationException(EditFailure(reply));
            NexusLogbook.Refresh();
            return true;
        }

        // Logbook Center's full contact editor (ContactEditDlg): the whole stored contact, and its
        // save. The contact goes back in ONE edit -- Nexus keeps what the services said
        // (confirmations, upload stamps) through it -- then each upload status the operator
        // changed is stamped: "sent" as the service's own upload would stamp it, "not sent" as
        // rejected, so the next catch-up sends it again.
        public NexusQso GetRecord(int id)
        {
            var (row, error) = NexusRowFor(id);
            if (row == null) throw new InvalidOperationException(error);
            return row;
        }

        public void SaveRecord(NexusQso edited, IEnumerable<(string Service, bool Sent)> uploadChanges)
        {
            var reply = Client.Edit(edited.Id, edited.EditKey, edited);
            if (reply.State != "saved") throw new InvalidOperationException(EditFailure(reply));
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var (service, sent) in uploadChanges ?? Enumerable.Empty<(string, bool)>())
            {
                string outcome = !sent ? "rejected" : service == "lotw" ? "pending" : "accepted";
                var stamp = Client.StampUpload(edited.Id, service, outcome, now);
                if (stamp.State != "saved") throw new InvalidOperationException(EditFailure(stamp));
            }
            NexusLogbook.Refresh();
        }

        // Bulk edit: the logbook is read from Nexus ONCE, each changed contact goes back in its own
        // edit, and the read copy is rebuilt ONCE at the end. Per contact, GetRecord + SaveRecord
        // fetch every contact twice and rebuild the whole read copy -- about 2 s a contact on a
        // 2,500-contact log, so 500 contacts took over 15 minutes (2026-10-01).
        public (int Changed, int Same, int Failed, string FirstError) BulkEdit(IEnumerable<int> ids, Func<NexusQso, bool> apply)
        {
            int changed = 0, same = 0, failed = 0;
            string firstError = null;
            var rows = Client.Rows();
            if (rows.Error != null || rows.Rows == null)
                throw new InvalidOperationException("The logbook could not be read: " + rows.Error);
            var byNexusId = rows.Rows.Where(q => !string.IsNullOrEmpty(q.Id)).GroupBy(q => q.Id).ToDictionary(g => g.Key, g => g.First());
            try
            {
                foreach (int id in ids)
                {
                    try
                    {
                        string nexusId = R(db => db.GetExtraFields(id).FirstOrDefault(e => e.Tag == "APP_NEXUS_ID").Value, null);
                        if (string.IsNullOrEmpty(nexusId) || !byNexusId.TryGetValue(nexusId, out var q))
                            throw new InvalidOperationException("That contact changed or was removed -- refresh and try again.");
                        if (!apply(q)) { same++; continue; }
                        var reply = Client.Edit(q.Id, q.EditKey, q);
                        if (reply.State != "saved") throw new InvalidOperationException(EditFailure(reply));
                        changed++;
                    }
                    catch (Exception ex) { failed++; firstError = firstError ?? ex.Message; }
                }
            }
            finally
            {
                if (changed > 0) NexusLogbook.Refresh();
            }
            return (changed, same, failed, firstError);
        }

        // A full copy of the logbook (Nexus's own ADIF export, every field) -- taken before a
        // bulk edit, so the contacts as they were can always be imported back.
        public void BackupTo(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var reply = Client.Export(path);
            if (reply.State != "saved" || !File.Exists(path))
                throw new InvalidOperationException($"The logbook backup failed ({reply.State}{(string.IsNullOrEmpty(reply.Why) ? "" : ": " + reply.Why)}).");
        }

        private static string EditFailure(NexusWriteReply r) =>
            r.State == "changed" ? "That contact changed since it was shown -- refresh and try again."
            : r.State == "gone" ? "That contact was removed -- refresh the list."
            : $"The logbook did not take the change ({r.State}{(string.IsNullOrEmpty(r.Why) ? "" : ": " + r.Why)}).";

        public int DeleteQsos(IEnumerable<int> ids)
        {
            int n = 0;
            foreach (int id in ids.Distinct())
            {
                var (row, error) = NexusRowFor(id);
                if (row == null) continue;
                var reply = Client.Delete(row.Id, row.EditKey);
                if (reply.State == "saved") n++;
            }
            NexusLogbook.Refresh();
            return n;
        }

        // ── Uploads (Nexus's own upload state) ─────────────────────────────────────────

        // HRDLog included: its upload time is kept on each contact (APP_JIMMY_HRDLOG_UL, the read
        // copy's hrdlog_uploaded_at), recorded by Nexus's LOG_UPLOAD.
        public List<LogbookDb.PendingUploadQso> GetPendingUploads(string service, int limit = 1000) =>
            R(db => db.GetPendingUploads(service, limit), new List<LogbookDb.PendingUploadQso>());

        // The Nexus id of the contact Jimmy knows by its dedup key: from the read copy, or -- for a
        // live contact not in it yet -- the id its logging request deterministically carries.
        private static string NexusIdForDedupKey(string dedupKey) =>
            R(db =>
            {
                var id = db.GetIdByDedupKey(dedupKey);
                return id.HasValue ? db.GetExtraFields(id.Value).FirstOrDefault(e => e.Tag == "APP_NEXUS_ID").Value : null;
            }, null) ?? NexusLogbook.RecordIdForRequest(RequestIdFor(LiveRequestPrefix, dedupKey));

        // One contact to one service (QRZ, CLUBLOG, EQSL) through Nexus: EngineHost writes the
        // record from Nexus's full data, sends it with Nexus's transport, and Nexus classifies and
        // records the answer (accepted, duplicate, rejected, authfail -- a rejection is now kept,
        // visible, not just "not uploaded"). Returns true when the service holds the contact;
        // otherwise false with the reason. Jimmy still decides WHEN (real-time, catch-up, the
        // Club Log breaker) exactly as before.
        public bool UploadThroughNexus(string dedupKey, string service, LiveUploadCredentials creds, out string error)
        {
            error = null;
            string svc = (service ?? "").ToUpperInvariant();
            string nexusService = svc == "QRZ" ? "qrz" : svc == "CLUBLOG" ? "clublog" : svc == "EQSL" ? "eqsl" : svc == "HRDLOG" ? "hrdlog" : null;
            if (nexusService == null) throw new ArgumentException("Nexus does not upload to " + service + " here");
            string nexusId = NexusIdForDedupKey(dedupKey);
            if (string.IsNullOrEmpty(nexusId)) { error = "contact not found in the logbook"; return false; }
            var reply = Client.Upload(new
            {
                id = nexusId,
                service = nexusService,
                qrzKey = nexusService == "qrz" ? creds?.QrzLogbookApiKey ?? "" : "",
                clublogEmail = nexusService == "clublog" ? creds?.ClubLogUploadEmail ?? "" : "",
                clublogPassword = nexusService == "clublog" ? creds?.ClubLogUploadPassword ?? "" : "",
                clublogCallsign = nexusService == "clublog" ? creds?.ClubLogUploadCallsign ?? "" : "",
                clublogAppKey = nexusService == "clublog" ? ClubLogAppKey.Resolve() ?? "" : "",
                eqslUsername = nexusService == "eqsl" ? creds?.EqslUsername ?? "" : "",
                eqslPassword = nexusService == "eqsl" ? creds?.EqslPassword ?? "" : "",
                hrdlogCallsign = nexusService == "hrdlog" ? creds?.HrdLogUploadCallsign ?? "" : "",
                hrdlogCode = nexusService == "hrdlog" ? creds?.HrdLogUploadCode ?? "" : "",
            });
            if (reply.State == "stamped" || reply.State == "sent-not-stamped") NexusLogbook.Refresh();
            bool held = reply.Outcome == "accepted" || reply.Outcome == "duplicate" || reply.Outcome == "pending";
            // "sent-not-stamped" (rare: the service holds it but the record could not be written) leaves
            // the contact owed; the next catch-up sends it again and the service answers "duplicate".
            if (held && reply.State != "unsent" && reply.State != "unknown") return true;
            error = reply.Why ?? (reply.Outcome != null ? $"{reply.Outcome}{(reply.Message != null ? ": " + reply.Message : "")}" : reply.State);
            return false;
        }

        public void MarkUploaded(string dedupKey, string service, DateTime whenUtc)
        {
            string svc = (service ?? "").ToUpperInvariant();
            // HRDLog has no Nexus upload state; while Nexus keeps the log its upload time is recorded
            // on the contact by LOG_UPLOAD itself (UploadThroughNexus), so nothing to stamp here.
            if (svc == "HRDLOG") return;
            string nexusService = svc == "LOTW" ? "lotw" : svc == "QRZ" ? "qrz" : svc == "CLUBLOG" ? "clublog" : svc == "EQSL" ? "eqsl" : null;
            if (nexusService == null) throw new ArgumentException("Unknown upload service: " + service);
            // TQSL gives no per-contact answer: LoTW is "pending" until a LoTW download echoes it.
            string outcome = nexusService == "lotw" ? "pending" : "accepted";
            string nexusId = NexusIdForDedupKey(dedupKey);
            var reply = Client.StampUpload(nexusId, nexusService, outcome, new DateTimeOffset(whenUtc.ToUniversalTime()).ToUnixTimeSeconds());
            if (reply.State == "saved") NexusLogbook.Refresh();
        }

        public LogbookDb.UploadSyncStatus GetUploadSyncStatus(string service) =>
            R(db => db.GetUploadSyncStatus(service), new LogbookDb.UploadSyncStatus());

        // ── Jimmy's own bookkeeping ─────────────────────────────────────────────────────

        public void SetMeta(string key, string value) => NexusLogbook.SetMeta(key, value);
        public string GetMeta(string key) => NexusLogbook.GetMeta(key);

        public int LogImportStart(string source)
        {
            var history = ImportHistory();
            int id = history.Count == 0 ? 1 : history.Max(h => h.Id) + 1;
            history.Insert(0, new ImportLogEntry { Id = id, Source = source, StartedAt = DateTime.UtcNow });
            SaveImportHistory(history);
            return id;
        }

        public void LogImportFinish(int logId, int total, int newCount, int newlyConfirmed, int corrected, int skipped, string errorText)
        {
            var history = ImportHistory();
            var e = history.FirstOrDefault(h => h.Id == logId);
            if (e == null) return;
            e.TotalQso = total; e.NewQso = newCount; e.NewlyConfirmed = newlyConfirmed; e.Corrected = corrected;
            e.SkippedQso = skipped; e.ErrorText = errorText ?? "";
            SaveImportHistory(history);
        }

        public List<ImportLogEntry> GetImportHistory(int limit = 25) => ImportHistory().Take(limit).ToList();

        private static List<ImportLogEntry> ImportHistory()
        {
            string s = NexusLogbook.GetMeta("import_history");
            if (string.IsNullOrEmpty(s)) return new List<ImportLogEntry>();
            try { return NexusLogClient.FromJson<List<ImportLogEntry>>(s) ?? new List<ImportLogEntry>(); }
            catch { return new List<ImportLogEntry>(); }
        }

        private static void SaveImportHistory(List<ImportLogEntry> h) =>
            NexusLogbook.SetMeta("import_history", NexusLogClient.ToJson(h.Take(100).ToList()));

        // DXCC fill for contacts that have none (the same rule as Jimmy's own import, Club Log's
        // offline data): the DXCC entity, and the country / continent where blank, by a Nexus edit per
        // contact -- only filling blanks. Repairs contacts logged while Nexus kept the log before the
        // live path filled them (2026-09-28), and any that ever arrive without one.
        public int BackfillMissingEntities()
        {
            var rows = Client.Rows();
            if (rows.Error != null) return 0;
            int n = 0;
            foreach (var q in rows.Rows.Where(q => (q.Dxcc ?? 0) == 0))
            {
                int dxcc = 0;
                string country = q.Country ?? "";
                string continent = q.ExtraValue("CONT") ?? "";
                AdifImporter.FillEntityGaps(q.Call, ref dxcc, ref country, ref continent);
                if (dxcc <= 0) continue;
                q.Dxcc = (uint)dxcc;
                if (string.IsNullOrEmpty(q.Country)) q.Country = country;
                if (string.IsNullOrEmpty(q.ExtraValue("CONT")) && !string.IsNullOrEmpty(continent))
                    q.Extra.Add(new List<string> { "CONT", continent.ToUpperInvariant() });
                if (Client.Edit(q.Id, q.EditKey, q).State == "saved") n++;
            }
            if (n > 0) NexusLogbook.Refresh();
            return n;
        }

        // State fill for contacts that have none: a Nexus edit per contact, only filling a blank.
        public int BackfillMissingStates(Func<string, string> resolveState)
        {
            if (resolveState == null) return 0;
            var rows = Client.Rows();
            if (rows.Error != null) return 0;
            int n = 0;
            foreach (var q in rows.Rows.Where(q => string.IsNullOrEmpty(q.State)))
            {
                string state = AdifImporter.ResolveMissingState(q.Call, q.Grid, resolveState);
                if (string.IsNullOrEmpty(state) || state.Length > 2) continue;
                q.State = state.ToUpperInvariant();
                if (Client.Edit(q.Id, q.EditKey, q).State == "saved") n++;
            }
            if (n > 0) NexusLogbook.Refresh();
            return n;
        }

        // ── Reads (the projection) ─────────────────────────────────────────────────────

        public List<(string Tag, string Value)> GetExtraFields(long qsoId) =>
            // Contest rows' structured received exchange (the only caller: ContestWorkflow's rebuild).
            R(db => db.GetExtraFields(qsoId)
                .Where(e => e.Tag.StartsWith(NexusMigration.ContestRxPrefix, StringComparison.OrdinalIgnoreCase))
                .Select(e => (e.Tag.Substring(NexusMigration.ContestRxPrefix.Length), e.Value)).ToList(),
              new List<(string, string)>());
        public void SaveExtraFields(long qsoId, List<(string Tag, string Value)> extras) { /* Nexus keeps them with the import */ }
        public long? GetIdByDedupKey(string dedupKey) => R(db => db.GetIdByDedupKey(dedupKey), null);
        public void SetContestAssociation(long qsoId, string contestId, string contestSessionId) { /* written with the contact */ }
        public List<ContestSessionRow> GetContestSessionRows(string contestSessionId) =>
            R(db => db.GetContestSessionRows(contestSessionId), new List<ContestSessionRow>());
        public bool HasWorkedBefore(string callsign, string band = null) => R(db => db.HasWorkedBefore(callsign, band), false);
        public bool HasWorkedDxcc(int dxcc, string band = null) => R(db => db.HasWorkedDxcc(dxcc, band), false);
        public bool HasWorkedGrid(string grid, string band = null) => R(db => db.HasWorkedGrid(grid, band), true);
        public int TotalQsos(string source = null) => R(db => db.TotalQsos(source), 0);
        public int ConfirmedQsos(string source = null) => R(db => db.ConfirmedQsos(source), 0);
        public int LotwConfirmedQsos() => R(db => db.LotwConfirmedQsos(), 0);
        public int QrzConfirmedQsos() => R(db => db.QrzConfirmedQsos(), 0);
        public int EqslConfirmedQsos() => R(db => db.EqslConfirmedQsos(), 0);
        public (int worked, int confirmed) WasProgress(string band = null) => R(db => db.WasProgress(band), (0, 0));
        public (int worked, int confirmed) DxccProgress(string band = null) => R(db => db.DxccProgress(band), (0, 0));
        public (int worked, int confirmed) WazProgress(string band = null) => R(db => db.WazProgress(band), (0, 0));
        public List<QsoRecord> GetRecentQsos(int limit = 10) => R(db => db.GetRecentQsos(limit), new List<QsoRecord>());
        public QsoRecord GetQso(int id) => R(db => db.GetQso(id), null);
        public List<QsoRecord> SearchByCallsign(string pattern, int limit = 200) => R(db => db.SearchByCallsign(pattern, limit), new List<QsoRecord>());
        public List<QsoRecord> SearchQsos(string callsignPattern, string source, string dateFrom, string dateTo, int limit = 500,
            string searchField = null, string searchText = null) =>
            R(db => db.SearchQsos(callsignPattern, source, dateFrom, dateTo, limit, searchField, searchText), new List<QsoRecord>());
        public Dictionary<int, string> GetDxccCountryNames() => R(db => db.GetDxccCountryNames(), new Dictionary<int, string>());
        public List<Dictionary<string, string>> GetAdifFieldDicts(IEnumerable<int> ids, IEnumerable<string> sources = null) =>
            R(db => db.GetAdifFieldDicts(ids, sources), new List<Dictionary<string, string>>());

        // ── LoTW "received": uploads LoTW holds promoted from pending to accepted ─────────────

        // The QSO date of the oldest contact whose LoTW upload is still "pending", or null when
        // none is -- the lower bound of the own-records request, as the Nexus desktop uses it.
        public DateTime? OldestPendingLotwUpload()
        {
            var rows = Client.Rows();
            if (rows.Error != null) return null;
            var pending = rows.Rows.Where(q => q.Upload?.Lotw?.Outcome == "pending").Select(q => q.WhenUnix).ToList();
            return pending.Count == 0 ? (DateTime?)null : DateTimeOffset.FromUnixTimeSeconds((long)pending.Min()).UtcDateTime.Date;
        }

        // LoTW's own-records download merged by Nexus's merge_lotw_own_echo, which pairs each row
        // with a contact not yet award-confirmed (day, then log order) and marks its LoTW upload
        // accepted. Through the pairing guard over exactly those contacts, so a row can never mark
        // another contact of the same day -- one never uploaded would then never be sent. Returns
        // the number promoted, or -1 with why.
        public int PromoteLotwReceived(string ownReport, out string why)
        {
            why = null;
            if (NexusLogbook.Outbox.Count > 0) NexusLogbook.Outbox.Replay(Client);
            if (NexusLogbook.Outbox.Count > 0) { why = "contacts are still waiting to be saved"; return -1; }
            var rows = Client.Rows();
            if (rows.Error != null) { why = rows.Error; return -1; }
            var prep = NexusReportPairing.Prepare(ownReport, rows.Rows.Where(q => !q.AwardConfirmed).ToList());
            NexusSyncDiagnostics.WriteList("held-lotw-own",
                "LoTW own-records rows not used to mark uploads received, because Nexus could not be sure to pair them with the right contact", prep.HeldDetails);
            if (prep.Sent == 0) return 0;
            string tmp = Path.Combine(Path.GetTempPath(), $"jimmy-nexus-lotw-own-{Guid.NewGuid():N}.adi");
            File.WriteAllText(tmp, prep.Text, new UTF8Encoding(false));
            try
            {
                var reply = Client.Merge("lotw-own", tmp);
                if (reply.State != "saved") { why = $"{reply.State} {reply.Why}".Trim(); return -1; }
                NexusLogbook.Refresh();
                return reply.Detail is System.Text.Json.JsonElement d && d.TryGetProperty("promoted", out var p) ? p.GetInt32() : 0;
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        // ── LoTW and QRZ downloads, fetched by Nexus's own code (2026-10-02) ───────────────────
        // LoTW: only the confirmations matched since the high-water LoTW gave last time
        // (APP_LoTW_LASTQSL), as the Nexus desktop asks -- seconds instead of the whole history.
        // The rules that keep it from missing one are Nexus's: LoTW's own time, not this PC's; only
        // a complete report (the engine refuses a cut-off one); the high-water moves only after a
        // clean merge (the caller then calls SaveLotwHighWater), and is kept per username, so a
        // different account starts with everything. full: everything again.
        private static string LotwHighWaterKey(string user) => "lotwLastQsl|" + (user ?? "").Trim().ToUpperInvariant();

        // A full download asks "since 1900-01-01" in so many words: given no date, LoTW uses a
        // "system supplied default" -- the account's last download -- and sends only what came
        // after it (2026-10-02: the first run, with no high-water yet, got 1 confirmation, not the
        // history; the old Jimmy client always sent the explicit date for this reason).
        private const string LotwEverything = "1900-01-01";

        public static (string adif, string highWater, string error) DownloadLotwConfirmations(string user, string pass, bool full)
        {
            string since = full ? null : NexusLogbook.GetMeta(LotwHighWaterKey(user));
            if (string.IsNullOrWhiteSpace(since)) since = LotwEverything;
            var r = new ExternalDataClient().DownloadLotw(user, pass, since, null, out string error);
            return r == null ? (null, null, error) : (r.Adif ?? "", r.HighWater, null);
        }

        // After a clean merge only. No high-water (an empty answer) keeps the one there is.
        public static void SaveLotwHighWater(string user, string highWater)
        {
            if (!string.IsNullOrWhiteSpace(highWater)) NexusLogbook.SetMeta(LotwHighWaterKey(user), highWater.Trim());
        }

        // QRZ: always the whole logbook -- QRZ's "modified since" follows a record's own edit date,
        // not its confirmation, and lost confirmations that way (2026-07-09).
        public static (string adif, string error) DownloadQrzLogbook(string apiKey)
        {
            string adif = new ExternalDataClient().DownloadQrzLogbook(apiKey, out string error);
            return (adif, error);
        }

        // The step a LoTW sync runs after merging confirmations while Nexus keeps the log.
        // Best-effort, as in the Nexus desktop: it never fails the sync. Returns a short note for
        // the status line, or null when there is nothing to say.
        public async System.Threading.Tasks.Task<string> LotwReceivedStepAsync(string user, string pass)
        {
            var from = OldestPendingLotwUpload();
            if (from == null) return null;
            string error = null;
            var report = await System.Threading.Tasks.Task.Run(() =>
                new ExternalDataClient().DownloadLotw(user, pass, null, from, out error)).ConfigureAwait(false);
            if (report == null) return "LoTW received check skipped: " + error;
            string text = report.Adif ?? "";
            int n = PromoteLotwReceived(text, out var why);
            return n < 0 ? "LoTW received status not updated: " + why : n > 0 ? $"LoTW has received {n:N0} upload(s)." : null;
        }

        // Export while Nexus keeps the log: Nexus's own exporter writes the whole log (its full
        // records, the file other programs read), and the records Jimmy's selection names --
        // the same rows and sources, in the same order, as Jimmy's own export picks them from the
        // read copy -- are kept exactly as Nexus wrote them. Returns (records written, a note for
        // the status line, or null).
        // The export's ADIF header, as other loggers write one: the program, its version, and when.
        internal static string AdifExportHeader()
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            string program = asm.GetName().Name ?? "Jimmy Next";
            string version = (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm)?.InformationalVersion ?? "").Trim();
            string F(string tag, string value) => $"<{tag}:{Encoding.UTF8.GetByteCount(value)}>{value}";
            var sb = new StringBuilder($"{program} ADIF export\n");
            sb.Append(F("ADIF_VER", "3.1.4")).Append('\n');
            sb.Append(F("PROGRAMID", program)).Append('\n');
            if (version.Length > 0) sb.Append(F("PROGRAMVERSION", version)).Append('\n');
            sb.Append(F("CREATED_TIMESTAMP", DateTime.UtcNow.ToString("yyyyMMdd HHmmss", CultureInfo.InvariantCulture))).Append('\n');
            return sb.Append("<EOH>").ToString();
        }

        public (int Written, string Note) ExportAdif(IList<int> ids, IList<string> sources, string outPath)
        {
            var wanted = R(db => db.GetExtraFieldForExport("APP_NEXUS_ID", ids, sources), new List<string>());
            string tmp = Path.Combine(Path.GetTempPath(), $"jimmy-nexus-export-{Guid.NewGuid():N}.adi");
            try
            {
                var reply = Client.Export(tmp);
                if (reply.State != "saved") throw new InvalidOperationException($"Nexus export: {reply.State} {reply.Why}".Trim());
                string text = File.ReadAllText(tmp, Encoding.UTF8);
                var m = System.Text.RegularExpressions.Regex.Match(text, "<eoh>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                string header = AdifExportHeader();   // Nexus's file says "Nexus"; the export is Jimmy Next's
                var byId = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var rec in System.Text.RegularExpressions.Regex.Split(m.Success ? text.Substring(m.Index + m.Length) : text, "<eor>",
                                                                               System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    var id = System.Text.RegularExpressions.Regex.Match(rec, @"<APP_NEXUS_ID:(\d+)>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (id.Success) byId[rec.Substring(id.Index + id.Length, int.Parse(id.Groups[1].Value))] = rec.Trim();
                }
                var sb = new StringBuilder(header).Append('\n');
                int written = 0, missing = 0;
                foreach (var nid in wanted)
                {
                    if (nid != null && byId.TryGetValue(nid, out var rec)) { sb.Append(rec).Append(" <eor>\n"); written++; }
                    else missing++;
                }
                File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
                var notes = new List<string>();
                if (missing > 0) notes.Add($"{missing} selected contact(s) not in Nexus's file");
                if (reply.Saving > 0) notes.Add($"{reply.Saving} recent change(s) still being saved are not in the file");
                if (reply.Held > 0) notes.Add($"{reply.Held} change(s) the logbook refused are not in the file");
                return (written, notes.Count > 0 ? string.Join("; ", notes) + "." : null);
            }
            finally { try { File.Delete(tmp); } catch { } }
        }
    }
}
