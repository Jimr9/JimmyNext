using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WsjtxUdpLib.Messages.Out;

namespace WSJTX_Controller
{
    public partial class WsjtxClient
    {
        // Shared "a QSO was just logged" UI feedback and CQ-resume, called from whichever of
        // QsoLoggedMessage/LoggedAdifMessage claims the Id first (see _liveLoggedQsoIds).
        private void OnQsoLogged(string dxCall)
        {
            if (dxCall != null && !logList.Contains(dxCall))
            {
                logList.Add(dxCall);
                loggedCall = dxCall;
                lCall = dxCall;
                ShowLogged();
                Sounds.PlaySoundEvent(ctrl.loggedCheckBox.Checked, ctrl.soundFile_Logged);
                StatusView.ShowMessage($"Logged QSO with {dxCall}", false);
                DebugOutput($"{spacer}OnQsoLogged: added '{dxCall}' to logList");
                // Same reasoning as RequestLog(): without this, an award's "still needed" tag
                // stays stale for the rest of the session on this band, which can also let the
                // already-worked exception keep re-admitting this same call into the queue.
                ctrl.RefreshStillNeedCache();
            }
            if (txMode == TxModes.CALL_CQ &&
                dxCall != null &&
                string.Equals(dxCall, callInProg, StringComparison.OrdinalIgnoreCase))
            {
                DebugOutput($"{spacer}OnQsoLogged: CQ mode QSO complete, resuming CQ");
                _callQueueStore.RemoveCall(dxCall);
                CancelQso();
                cqPaused = false;
                SetupCq(true);
            }
        }

        // Builds the ADIF-style field dictionary and record text from a QsoLoggedMessage,
        // claims the QSO by its dedup key (see ClaimLiveLoggedQso), and if not already
        // handled via the LoggedAdifMessage fallback, fires the UI feedback and hands off
        // to the shared import/upload tail (see ImportLiveLoggedQso).
        private void HandleLiveQsoLogged(QsoLoggedMessage qMsg)
        {
            double freqMhz = qMsg.TxFrequency / 1_000_000.0;
            string freqMhzStr = freqMhz.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
            string band = AdifImporter.NormalizeBand("", freqMhzStr);
            string qsoDate = qMsg.DateTimeOn.ToString("yyyyMMdd");
            string timeOn  = qMsg.DateTimeOn.ToString("HHmmss");
            string timeOff = qMsg.DateTimeOff.ToString("HHmmss");

            string dedupKey = AdifImporter.BuildDedupKey(qMsg.DxCall ?? "", band, qMsg.Mode ?? "", qsoDate, timeOn);
            if (!ClaimLiveLoggedQso(dedupKey)) return;

            OnQsoLogged(qMsg.DxCall);

            var fields = new Dictionary<string, string>
            {
                ["CALL"]             = qMsg.DxCall ?? "",
                ["BAND"]             = band,
                ["FREQ"]             = freqMhzStr,
                ["MODE"]             = qMsg.Mode ?? "",
                ["QSO_DATE"]         = qsoDate,
                ["TIME_ON"]          = timeOn,
                ["TIME_OFF"]         = timeOff,
                ["RST_SENT"]         = qMsg.ReportSent ?? "",
                ["RST_RCVD"]         = qMsg.ReportReceived ?? "",
                ["GRIDSQUARE"]       = qMsg.DxGrid ?? "",
                ["NAME"]             = qMsg.Name ?? "",
                ["COMMENT"]          = qMsg.Comments ?? "",
                ["TX_PWR"]           = qMsg.TxPower ?? "",
                ["OPERATOR"]         = qMsg.OperatorCall ?? "",
                ["STATION_CALLSIGN"] = qMsg.MyCall ?? "",
                ["MY_GRIDSQUARE"]    = qMsg.MyGrid ?? "",
                ["STX_STRING"]       = qMsg.ExchangeSent ?? "",
                ["SRX_STRING"]       = qMsg.ExchangeReceived ?? "",
            };
            EnrichWithClubLogGeoData(fields, qMsg.DxCall);

            string adifRecord = AdifRecordBuilder.Build(
                qMsg.DxCall ?? "", band, (long)qMsg.TxFrequency, qMsg.Mode ?? "",
                qsoDate, timeOn, timeOff, qMsg.ReportSent ?? "", qMsg.ReportReceived ?? "",
                qMsg.DxGrid ?? "", qMsg.Name ?? "", qMsg.Comments ?? "", qMsg.TxPower ?? "",
                qMsg.OperatorCall ?? "", qMsg.MyCall ?? "", qMsg.MyGrid ?? "",
                qMsg.ExchangeSent ?? "", qMsg.ExchangeReceived ?? "");

            ImportLiveLoggedQso(qMsg.DxCall, fields, adifRecord, dedupKey);
        }

        // Fallback trigger for the same event QsoLoggedMessage normally handles -- WSJT-X
        // sends both messages for every logged QSO, so if one is ever dropped in transit
        // the other still gets the QSO into Jimmy's log/awards (see _liveLoggedQsoKeys).
        private void HandleLiveAdifLogged(LoggedAdifMessage aMsg)
        {
            Dictionary<string, string> fields;
            try
            {
                fields = AdifParser.Parse(aMsg.AdifText).FirstOrDefault();
            }
            catch (Exception ex)
            {
                DebugOutput($"{Time()} HandleLiveAdifLogged: could not parse ADIF text: {ex.Message}");
                return;
            }
            if (fields == null) return;

            string dxCall  = fields.TryGetValue("CALL", out var callVal) ? callVal : null;
            string band    = fields.TryGetValue("BAND", out var bandVal) ? bandVal : "";
            string mode    = fields.TryGetValue("MODE", out var modeVal) ? modeVal : "";
            string qsoDate = fields.TryGetValue("QSO_DATE", out var dateVal) ? dateVal : "";
            string timeOn  = fields.TryGetValue("TIME_ON", out var timeVal) ? timeVal : "";
            EnrichWithClubLogGeoData(fields, dxCall);

            string dedupKey = AdifImporter.BuildDedupKey(dxCall ?? "", band, mode, qsoDate, timeOn);
            if (!ClaimLiveLoggedQso(dedupKey)) return;

            OnQsoLogged(dxCall);
            ImportLiveLoggedQso(dxCall, fields, aMsg.AdifText, dedupKey);
        }

        // Fills COUNTRY/DXCC/CONT/CQZ from the callsign's DXCC prefix (Club Log's country
        // database, downloaded automatically at startup and available offline) when the
        // source message didn't supply them -- WSJT-X's own QsoLoggedMessage/LoggedAdifMessage
        // protocol never includes these, so without this a live-logged QSO's award status
        // (DXCC/WAZ/Continents awards) wouldn't reflect it until a later QRZ/LoTW/Club Log
        // sync backfilled the row. Only fills gaps; never overwrites a value already present.
        private void EnrichWithClubLogGeoData(Dictionary<string, string> fields, string call)
        {
            if (string.IsNullOrEmpty(call)) return;
            var entity = ctrl.lookupManager?.ClubLog?.FindByCallsign(call);
            if (entity == null) return;

            if (!fields.TryGetValue("COUNTRY", out var country) || string.IsNullOrEmpty(country))
                fields["COUNTRY"] = entity.Name ?? "";
            if ((!fields.TryGetValue("DXCC", out var dxcc) || string.IsNullOrEmpty(dxcc)) && entity.Adif > 0)
                fields["DXCC"] = entity.Adif.ToString();
            if (!fields.TryGetValue("CONT", out var cont) || string.IsNullOrEmpty(cont))
                fields["CONT"] = entity.Continent ?? "";
            if ((!fields.TryGetValue("CQZ", out var cqz) || string.IsNullOrEmpty(cqz)) && entity.CqZone > 0)
                fields["CQZ"] = entity.CqZone.ToString();
        }

        // Feeds a just-logged QSO into Jimmy's local logbook database (via the same
        // dedup-safe AdifImporter pipeline already used for QRZ/LoTW/manual imports,
        // so My Log/Awards/Still Need reflect it immediately) and, if the user has
        // opted into real-time upload for QRZ and/or Club Log, sends it there too.
        // Runs on a background task so a slow/failed network call never blocks
        // WSJT-X message processing; all exceptions are caught and logged, never
        // allowed to propagate. Shared by both the QsoLoggedMessage and LoggedAdifMessage
        // code paths -- adifRecord is either built from QsoLoggedMessage's typed fields, or
        // (for the LoggedAdifMessage path) is the exact ADIF text WSJT-X itself logged.
        private void ImportLiveLoggedQso(string dxCall, Dictionary<string, string> fields, string adifRecord, string dedupKey)
        {
            LiveQsoUploader.ImportLiveLoggedQso(dxCall, fields, adifRecord, dedupKey);
        }

        // Alt+U. Tells WSJT-X to upload everything pending to LoTW, and also
        // triggers the QRZ/Club Log upload catch-up, so pressing this one key
        // sends everything pending to every configured service. Each part is
        // independently gated -- an unconfigured/disabled service is silently
        // skipped, never attempted. LoTW's gate (ctrl.lotwUploadEnabled, default
        // true) exists for operators who don't use LoTW at all -- WSJT-X reports
        // an error on this command when it has no LoTW/TQSL setup of its own.
        public bool UploadLotw()
        {
            HaltTuning();
            if (ctrl.lotwUploadEnabled)
            {
                StartUploadLotw();
                lastLotwUploadTrigger = DateTime.Now;
            }
            RunUploadCatchUp();
            return true;
        }

        // Sends every QSO not yet uploaded to QRZ/Club Log. This is the batch
        // safety net: it runs regardless of whether real-time upload is on for a
        // service (normally finding nothing to do in that case), and it is the
        // only path at all when real-time is off. Runs in the background --
        // Alt+U returns immediately, matching the existing LoTW behavior where
        // WSJT-X performs its upload asynchronously on its own too.
        private void RunUploadCatchUp()
        {
            Task.Run(async () =>
            {
                try
                {
                    using (var db = new LogbookDb())
                    {
                        if (ctrl.qrzUploadEnabled && !string.IsNullOrWhiteSpace(ctrl.qrzLogbookApiKey))
                            await CatchUpQrz(db).ConfigureAwait(false);

                        if (ctrl.clubLogUploadEnabled &&
                            !string.IsNullOrWhiteSpace(ctrl.clubLogUploadEmail) &&
                            !string.IsNullOrWhiteSpace(ctrl.clubLogUploadPassword) &&
                            !string.IsNullOrWhiteSpace(ctrl.clubLogUploadCallsign))
                            await CatchUpClubLog(db).ConfigureAwait(false);

                        if (ctrl.hrdLogUploadEnabled &&
                            !string.IsNullOrWhiteSpace(ctrl.hrdLogUploadCode) &&
                            !string.IsNullOrWhiteSpace(ctrl.hrdLogUploadCallsign))
                            await CatchUpHrdLog(db).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    DebugOutput($"{Time()} RunUploadCatchUp error: {ex.Message}");
                }
            });
        }

        // QRZ's INSERT is single-QSO-per-call (no batch parameter), so a backlog is
        // sent as a loop with a small courtesy delay between calls -- QRZ documents
        // no hard rate limit, but other logging software follows this same pattern.
        private async Task CatchUpQrz(LogbookDb db)
        {
            var pending = db.GetPendingUploads("QRZ");
            if (pending.Count == 0) return;
            DebugOutput($"{Time()} QRZ upload catch-up: {pending.Count} pending QSO(s).");

            // This loop can run for several minutes on a large backlog (one HTTP
            // call + 300ms delay per QSO) with no other feedback otherwise -- show
            // periodic progress so it's clear Jimmy is still working, not hung.
            // Throttled to roughly every 5 seconds so JAWS/NVDA doesn't get a
            // fresh announcement on every single QSO.
            ctrl.BeginInvoke(new Action(() =>
                ctrl.ShowUploadStatus($"QRZ upload: starting, {pending.Count} pending QSO(s)...", false)));

            var client = new QrzLogbookClient();
            int done = 0, succeeded = 0, failedCount = 0;
            DateTime lastStatusUpdate = DateTime.UtcNow;
            foreach (var q in pending)
            {
                string adifRecord = AdifRecordBuilder.Build(
                    q.Callsign, q.Band, q.FreqHz, q.Mode, q.QsoDate, q.TimeOn, q.TimeOff,
                    q.RstSent, q.RstRcvd, q.Grid, q.Name, q.Comment, q.TxPwr,
                    q.OperatorCall, q.StationCall, q.MyGrid, q.ExchangeSent, q.ExchangeRcvd);
                bool ok = await client.InsertAsync(ctrl.qrzLogbookApiKey, adifRecord).ConfigureAwait(false);
                done++;
                if (ok)
                {
                    db.MarkUploaded(q.DedupKey, "QRZ", DateTime.UtcNow);
                    succeeded++;
                }
                else
                {
                    failedCount++;
                    DebugOutput($"{Time()} QRZ upload catch-up failed for {q.Callsign}: {client.LastError}");
                }

                bool isLast = done == pending.Count;
                if (isLast || (DateTime.UtcNow - lastStatusUpdate).TotalSeconds >= 5)
                {
                    lastStatusUpdate = DateTime.UtcNow;
                    int doneSnap = done, totalSnap = pending.Count, okSnap = succeeded, failSnap = failedCount;
                    string msg = isLast
                        ? $"QRZ upload: {totalSnap} QSO(s) processed ({okSnap} uploaded, {failSnap} failed)."
                        : $"QRZ upload: {doneSnap}/{totalSnap} processed ({okSnap} uploaded, {failSnap} failed)...";
                    // Refresh the Ham Radio Center's Sync Status numbers (pending/uploaded
                    // counts, last-upload time) too when the batch finishes -- otherwise
                    // they only update the next time the user navigates away and back,
                    // showing stale figures right after an upload that just happened.
                    if (isLast)
                        ctrl.BeginInvoke(new Action(() => { ctrl.ShowUploadStatus(msg, false); ctrl.RefreshLogbookWindowIfOpen(); }));
                    else
                        ctrl.BeginInvoke(new Action(() => ctrl.ShowUploadStatus(msg, false)));
                }

                await Task.Delay(300).ConfigureAwait(false);
            }
        }

        // Club Log's own guidance is that a backlog must go through putlogs.php
        // (one file, one request) rather than looping realtime.php -- so the whole
        // pending set is sent as a single batch upload here.
        private async Task CatchUpClubLog(LogbookDb db)
        {
            var pending = db.GetPendingUploads("CLUBLOG");
            if (pending.Count == 0) return;
            DebugOutput($"{Time()} Club Log upload catch-up: {pending.Count} pending QSO(s).");

            var sb = new StringBuilder();
            foreach (var q in pending)
            {
                sb.Append(AdifRecordBuilder.Build(
                    q.Callsign, q.Band, q.FreqHz, q.Mode, q.QsoDate, q.TimeOn, q.TimeOff,
                    q.RstSent, q.RstRcvd, q.Grid, q.Name, q.Comment, q.TxPwr,
                    q.OperatorCall, q.StationCall, q.MyGrid, q.ExchangeSent, q.ExchangeRcvd));
            }

            var client = new ClubLogUploadClient();
            bool ok = await client.BatchUploadAsync(
                ctrl.clubLogUploadEmail, ctrl.clubLogUploadPassword, ctrl.clubLogUploadCallsign,
                ClubLogAppKey.Resolve(), sb.ToString()).ConfigureAwait(false);

            if (ok)
            {
                foreach (var q in pending) db.MarkUploaded(q.DedupKey, "CLUBLOG", DateTime.UtcNow);
                // Refresh the Sync Status numbers too -- see the same comment in CatchUpQrz.
                ctrl.BeginInvoke(new Action(() =>
                {
                    ctrl.ShowUploadStatus($"Club Log upload: {pending.Count} QSO(s) uploaded successfully.", false);
                    ctrl.RefreshLogbookWindowIfOpen();
                }));
            }
            else
            {
                DebugOutput($"{Time()} Club Log upload catch-up failed ({pending.Count} QSOs): {client.LastError}");
                // Per Club Log's own integration rules: show the user the error and
                // do not keep sending more requests -- Alt+U already only fires this
                // once per explicit press, so no separate breaker latch is needed
                // here the way real-time upload needs one.
                ctrl.BeginInvoke(new Action(() =>
                    ctrl.ShowUploadStatus($"Club Log upload failed: {client.LastError}", true)));
            }
        }

        // HRDLog.net's NewEntry.aspx is single-QSO-per-call like QRZ (no batch endpoint), so
        // this follows CatchUpQrz's exact per-QSO-loop-with-delay shape, not CatchUpClubLog's
        // single-batch shape.
        private async Task CatchUpHrdLog(LogbookDb db)
        {
            var pending = db.GetPendingUploads("HRDLOG");
            if (pending.Count == 0) return;
            DebugOutput($"{Time()} HRDLog.net upload catch-up: {pending.Count} pending QSO(s).");

            ctrl.BeginInvoke(new Action(() =>
                ctrl.ShowUploadStatus($"HRDLog.net upload: starting, {pending.Count} pending QSO(s)...", false)));

            var client = new HrdLogUploadClient();
            int done = 0, succeeded = 0, failedCount = 0;
            DateTime lastStatusUpdate = DateTime.UtcNow;
            foreach (var q in pending)
            {
                string adifRecord = AdifRecordBuilder.Build(
                    q.Callsign, q.Band, q.FreqHz, q.Mode, q.QsoDate, q.TimeOn, q.TimeOff,
                    q.RstSent, q.RstRcvd, q.Grid, q.Name, q.Comment, q.TxPwr,
                    q.OperatorCall, q.StationCall, q.MyGrid, q.ExchangeSent, q.ExchangeRcvd);
                bool ok = await client.InsertAsync(ctrl.hrdLogUploadCallsign, ctrl.hrdLogUploadCode, adifRecord).ConfigureAwait(false);
                done++;
                if (ok)
                {
                    db.MarkUploaded(q.DedupKey, "HRDLOG", DateTime.UtcNow);
                    succeeded++;
                }
                else
                {
                    failedCount++;
                    DebugOutput($"{Time()} HRDLog.net upload catch-up failed for {q.Callsign}: {client.LastError}");
                }

                bool isLast = done == pending.Count;
                if (isLast || (DateTime.UtcNow - lastStatusUpdate).TotalSeconds >= 5)
                {
                    lastStatusUpdate = DateTime.UtcNow;
                    int doneSnap = done, totalSnap = pending.Count, okSnap = succeeded, failSnap = failedCount;
                    string msg = isLast
                        ? $"HRDLog.net upload: {totalSnap} QSO(s) processed ({okSnap} uploaded, {failSnap} failed)."
                        : $"HRDLog.net upload: {doneSnap}/{totalSnap} processed ({okSnap} uploaded, {failSnap} failed)...";
                    if (isLast)
                        ctrl.BeginInvoke(new Action(() => { ctrl.ShowUploadStatus(msg, false); ctrl.RefreshLogbookWindowIfOpen(); }));
                    else
                        ctrl.BeginInvoke(new Action(() => ctrl.ShowUploadStatus(msg, false)));
                }

                await Task.Delay(300).ConfigureAwait(false);
            }
        }

        private void DeleteLotwCsv()
        {
            string pgmNameWsjtx = "WSJT-X";
            string pathWsjtx = $"{Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}\\{pgmNameWsjtx}";
            string pathFileNameExt = pathWsjtx + "\\" + "lotw-user-activity.csv";

            try
            {
                if (File.Exists(pathFileNameExt))
                {
                    File.Delete(pathFileNameExt);
                    DebugOutput($"{Time()} DeleteLotwCsv, deleted {pathFileNameExt}");
                }
            }
            catch (Exception)
            {
                DebugOutput($"{Time()} DeleteLotwCsv, unable to delete {pathFileNameExt}");
            }
        }
    }
}
