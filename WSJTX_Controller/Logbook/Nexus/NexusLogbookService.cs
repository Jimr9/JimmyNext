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
            X("SIG", sig); X("SIG_INFO", sigInfo); X("MY_SIG", mySig); X("MY_SIG_INFO", mySigInfo);
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
                Ota = new NexusOta { Iota = Blank(iota) },
                Extra = extra,
            };
        }

        private static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        private static string Pad6(string t) { t = (t ?? "").Trim(); return t.Length == 4 ? t + "00" : t; }

        // A request id per contact: re-sending the same contact (a retry, a lost reply) names the
        // same Nexus record, so it can never be logged twice.
        public static string RequestIdFor(string source, string dedupKey) => $"{source}:{dedupKey}";

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
                G("TX_PWR"), G("OPERATOR"), G("STATION_CALLSIGN"), G("MY_GRIDSQUARE"), "WSJTX", "",
                G("CONT"), ituz, G("CNTY"), G("IOTA"), G("SIG"), G("SIG_INFO"), G("MY_SIG"), G("MY_SIG_INFO"), "", G("PFX"), "", "");
            string reqId = RequestIdFor("WSJTX", dedupKey);
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
                foreach (var kv in fields)
                    if (!string.IsNullOrEmpty(kv.Value))
                        sb.Append('<').Append(kv.Key).Append(':').Append(Encoding.UTF8.GetByteCount(kv.Value)).Append('>').Append(kv.Value).Append(' ');
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
            result.Held = prep.Held;
            result.HeldDetails.AddRange(prep.HeldDetails);
            NexusSyncDiagnostics.WriteList("held-" + source.ToLowerInvariant(),
                $"{source} rows held back -- not merged, nothing changed -- because Nexus could not be sure to pair them with the right contact", prep.HeldDetails);
            return result;
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

        public LogbookDb.EqslReconcileOutcome TryMarkEqslConfirmed(string callsign, string band, string qsoDateAdif, string mode) =>
            throw new NotSupportedException("the eQSL inbox is merged as a whole through Nexus (EqslReconciler)");

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
            }, null) ?? NexusLogbook.RecordIdForRequest(RequestIdFor("WSJTX", dedupKey));

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

        // State fill for contacts that have none: a Nexus edit per contact, only filling a blank.
        public int BackfillMissingStates(Func<string, string> resolveState)
        {
            if (resolveState == null) return 0;
            var rows = Client.Rows();
            if (rows.Error != null) return 0;
            int n = 0;
            foreach (var q in rows.Rows.Where(q => string.IsNullOrEmpty(q.State)))
            {
                string state = resolveState(q.Call);
                if (string.IsNullOrEmpty(state) && !string.IsNullOrEmpty(q.Grid)) state = WsjtxClient.GridToUsState(q.Grid);
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
        public void RunBatch<T>(IEnumerable<T> items, Action<T> perItemAction) { foreach (var i in items) perItemAction(i); }
        public bool HasWorkedBefore(string callsign, string band = null) => R(db => db.HasWorkedBefore(callsign, band), false);
        public bool HasWorkedDxcc(int dxcc, string band = null) => R(db => db.HasWorkedDxcc(dxcc, band), false);
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
        public List<QsoRecord> SearchQsos(string callsignPattern, string source, string dateFrom, string dateTo, int limit = 500) =>
            R(db => db.SearchQsos(callsignPattern, source, dateFrom, dateTo, limit), new List<QsoRecord>());
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

        // The step a LoTW sync runs after merging confirmations while Nexus keeps the log.
        // Best-effort, as in the Nexus desktop: it never fails the sync. Returns a short note for
        // the status line, or null when there is nothing to say.
        public async System.Threading.Tasks.Task<string> LotwReceivedStepAsync(string user, string pass)
        {
            var from = OldestPendingLotwUpload();
            if (from == null) return null;
            var lotw = new LoTWQsoClient();
            string text = await lotw.FetchReportAsync(user, pass, null, confirmedOnly: false, ownFromQsoDate: from).ConfigureAwait(false);
            if (text == null) return "LoTW received check skipped: " + lotw.LastError;
            int n = PromoteLotwReceived(text, out var why);
            return n < 0 ? "LoTW received status not updated: " + why : n > 0 ? $"LoTW has received {n:N0} upload(s)." : null;
        }

        // Export while Nexus keeps the log: Nexus's own exporter writes the whole log (its full
        // records, the file other programs read), and the records Jimmy's selection names --
        // the same rows and sources, in the same order, as Jimmy's own export picks them from the
        // read copy -- are kept exactly as Nexus wrote them. Returns (records written, a note for
        // the status line, or null).
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
                string header = m.Success ? text.Substring(0, m.Index + m.Length) : "";
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
