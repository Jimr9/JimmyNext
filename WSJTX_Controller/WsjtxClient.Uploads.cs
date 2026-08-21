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
        // UDP-transport cleanup, 2026-08-18: HandleLiveQsoLogged/HandleLiveAdifLogged (WSJT-X's
        // own QsoLoggedMessage/LoggedAdifMessage wire messages -- "trust me, I just logged this
        // QSO") and their shared OnQsoLogged UI-feedback helper were removed here. Direct mode has
        // no equivalent inbound command at all -- and never needed one: DirectApplyStatus's own
        // curTxMsg/callInProg/Is73orRR73 detection (WsjtxClient.Direct.cs) calls LogQso() ->
        // RequestLog() (below) directly, which ALREADY independently implements every real piece
        // of what OnQsoLogged used to do for the UDP path -- ClaimLiveLoggedQso dedup,
        // EnrichWithClubLogGeoData, ImportLiveLoggedQso (all three still shared, still called from
        // RequestLog, unchanged below), logList.Add/ShowLogged, the completion sound, and
        // RefreshStillNeedCache -- verified by reading RequestLog's own body, not assumed.
        //
        // Release-audit finding, 2026-08-20: OnQsoLogged's one remaining piece of logic --
        // its CQ-mode auto-resume call (_callQueueStore.RemoveCall + CancelQso + SetupCq(true) to
        // re-arm calling CQ after a completed QSO) -- was a genuine gap for a while: it used to
        // work only because the classic UDP dispatcher's own decode-cycle machinery translated
        // SetupCq's local curCmd/qsoState bookkeeping into an actual outbound "resume CQing"
        // command, and Direct's control protocol had nothing that meant "start calling CQ" at
        // all. Fixed by adding a real CALL_CQ Direct command (Engine::call_cq, EngineHost/src/
        // main.rs) and DirectSendCq (WsjtxClient.Direct.cs) -- SetupCq now sends it on every
        // Call-CQ start, and DirectApplyStatus's own Is73orRR73 branch now performs the same
        // RemoveCall+CancelQso+SetupCq(true) re-arm this comment used to say was impossible to
        // restore.

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
        // Codex Audit 02 release blocker, 2026-08-21: now returns whether the local logbook write
        // actually succeeded -- see LiveQsoUploadOrchestrator.ImportLiveLoggedQso's own comment,
        // and this method's own caller in WsjtxClient.cs's RequestLog for the operator-visible half.
        private bool ImportLiveLoggedQso(string dxCall, Dictionary<string, string> fields, string adifRecord, string dedupKey)
        {
            return LiveQsoUploader.ImportLiveLoggedQso(dxCall, fields, adifRecord, dedupKey);
        }

        // Alt+U. Runs Jimmy's own TQSL invocation to upload everything pending to LoTW (see
        // RunTqslUpload below), and also triggers the QRZ/Club Log upload catch-up, so pressing
        // this one key sends everything pending to every configured service. Each part is
        // independently gated -- an unconfigured/disabled service is silently skipped, never
        // attempted. LoTW's gate (ctrl.lotwUploadEnabled, default true) exists for operators who
        // don't use LoTW at all -- TQSL reports an error on this command when it has no LoTW
        // certificate/Station Location setup of its own.
        public bool UploadLotw()
        {
            HaltTuning();
            if (ctrl.lotwUploadEnabled) RunTqslUpload();
            RunUploadCatchUp();
            return true;
        }

        // Self-sufficiency plan, Phase 3: Jimmy's own TQSL invocation, an alternative to
        // StartUploadLotw's WM8Q sub-command 16 path. Runs in the background like
        // RunUploadCatchUp -- TQSL itself can take several seconds to sign and upload.
        private void RunTqslUpload()
        {
            // See LiveQsoUploadOrchestrator.ImportLiveLoggedQso's own comment (2026-08-10) for
            // why this is captured here, synchronously, rather than letting the background task
            // below resolve LogbookDb.DbPath lazily whenever it happens to actually run.
            string dbPath = LogbookDb.DbPath;
            Task.Run(async () =>
            {
                try
                {
                    using (var db = new LogbookDb(dbPath))
                    {
                        var client = new TqslUploadClient();
                        bool ok = await client.UploadPendingAsync(ctrl.tqslStationLocation, db).ConfigureAwait(false);
                        if (!ok)
                        {
                            DebugOutput($"{Time()} TQSL upload failed: {client.LastError}");
                            ctrl.BeginInvoke(new Action(() =>
                                ctrl.ShowUploadStatus($"LoTW (TQSL) upload failed: {client.LastError}", true)));
                        }
                        else if (client.LastError != null)
                        {
                            // Release-audit finding, 2026-08-20: UploadPendingAsync returns true
                            // (not a hard failure) for TQSL's ambiguous "some already
                            // uploaded/out of date range" status codes now, but still sets
                            // LastError to explain that nothing was marked uploaded this run --
                            // must actually reach the operator, not just log_tqsl_errors.txt,
                            // or this fix's whole point (an honest status instead of a silent
                            // false "complete") is lost at this call site.
                            DebugOutput($"{Time()} TQSL upload: {client.LastError}");
                            ctrl.BeginInvoke(new Action(() =>
                                ctrl.ShowUploadStatus($"LoTW (TQSL) upload: {client.LastError}", false)));
                        }
                        else
                        {
                            ctrl.BeginInvoke(new Action(() =>
                            {
                                ctrl.ShowUploadStatus("LoTW (TQSL) upload complete.", false);
                                ctrl.RefreshLogbookWindowIfOpen();
                            }));
                        }
                    }
                }
                catch (Exception ex)
                {
                    DebugOutput($"{Time()} RunTqslUpload error: {ex.Message}");
                }
            });
        }

        // Sends every QSO not yet uploaded to QRZ/Club Log. This is the batch
        // safety net: it runs regardless of whether real-time upload is on for a
        // service (normally finding nothing to do in that case), and it is the
        // only path at all when real-time is off. Runs in the background --
        // Alt+U returns immediately, matching the existing LoTW behavior where
        // WSJT-X performs its upload asynchronously on its own too.
        private void RunUploadCatchUp()
        {
            // See LiveQsoUploadOrchestrator.ImportLiveLoggedQso's own comment (2026-08-10) for
            // why this is captured here, synchronously, rather than letting the background task
            // below resolve LogbookDb.DbPath lazily whenever it happens to actually run.
            string dbPath = LogbookDb.DbPath;
            Task.Run(async () =>
            {
                try
                {
                    using (var db = new LogbookDb(dbPath))
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

        // DeleteLotwCsv() (a workaround tied to a specific real-WSJT-X revision/testVer reported
        // in its own Heartbeat) was removed 2026-08-18 along with the dead UDP dispatcher that was
        // its only caller -- jimmy-engine-host never reports a WSJT-X revision/testVer at all
        // (those fields were only ever populated by parsing a real WSJT-X Heartbeat's version
        // string), so the gate this was behind could never have been true under Direct mode
        // regardless.
    }
}
