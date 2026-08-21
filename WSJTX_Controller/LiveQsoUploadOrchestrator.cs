using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WSJTX_Controller
{
    // Snapshot of the settings ImportLiveLoggedQso reads. Read at task-execution time (via the
    // Func<LiveUploadCredentials> passed to the orchestrator), not captured at call time --
    // preserves the original code's behavior of using whatever the user has configured by the
    // time the background task actually runs, not whatever was configured when the QSO was logged.
    public class LiveUploadCredentials
    {
        public bool QrzUploadEnabled;
        public bool QrzUploadRealtime;
        public string QrzLogbookApiKey;
        public bool ClubLogUploadEnabled;
        public bool ClubLogUploadRealtime;
        public string ClubLogUploadEmail;
        public string ClubLogUploadPassword;
        public string ClubLogUploadCallsign;
        // HRDLog.net (self-sufficiency plan, Phase 2). Same shape as the Club Log fields above --
        // callsign is its own configured setting, not silently derived from wsjtxClient.myCall,
        // for consistency with how ClubLogUploadCallsign already works.
        public bool HrdLogUploadEnabled;
        public bool HrdLogUploadRealtime;
        public string HrdLogUploadCode;
        public string HrdLogUploadCallsign;
        // eQSL.cc, uploaded via EngineHost/Nexus's own transport (propagation::live::eqsl) --
        // see ExternalDataClient.UploadEqsl. Same shape as the others; no app-level credential,
        // the operator supplies their own eQSL.cc username/password (eQSL has no API-key model
        // the way QRZ/Club Log/HRDLog do).
        public bool EqslUploadEnabled;
        public bool EqslUploadRealtime;
        public string EqslUsername;
        public string EqslPassword;
    }

    // Extracted from WsjtxClient.ImportLiveLoggedQso (Phase 2.6 of the modernization plan) --
    // the one genuinely async/cross-thread code path in the app (a background Task.Run that
    // imports a freshly-logged QSO, then optionally uploads it to QRZ/Club Log in real time).
    // No ctrl/Controller/WinForms reference: settings are read live via `credentials`, and the
    // UI-thread refresh (RefreshStillNeedCache/RefreshLogbookWindowIfOpen) is done by the
    // `notifyImported` callback WsjtxClient supplies -- it already owns the ctrl.BeginInvoke
    // marshaling, which stays exactly where it was, just handed in rather than hardcoded here.
    // Automated tests cannot exercise the QRZ/Club Log network calls themselves (no test
    // credentials); this extraction was verified by careful manual review against the original
    // method body, preserving control flow, error handling, and dedup-marking exactly.
    public class LiveQsoUploadOrchestrator
    {
        private readonly Func<LiveUploadCredentials> _credentials;
        private readonly Action _notifyImported;
        private readonly Action<string> _debugLog;
        private readonly Action<string, bool> _showStatus;
        private readonly Func<string, string> _resolveUsState;

        // Circuit breaker for Club Log real-time upload only (matches Club Log's
        // own documented integration requirement: "if you don't receive a '200
        // OK' then you must show the user the error... and stop sending more
        // requests"). Set on the first failure; automatic real-time upload is
        // skipped on every subsequent QSO until ResetClubLogRealtimeBreaker() is
        // called (wired to Options being saved -- the natural "I fixed
        // something, try again" signal). QRZ has no equivalent breaker since it
        // isn't documented to auto-block on repeated failures the way Club Log
        // explicitly warns it does.
        private volatile bool _clubLogRealtimeBroken;

        // Codex Audit 02 finding, 2026-08-21 ("improve shutdown handling for outstanding optional
        // remote-upload work"): the real-time QRZ/Club Log/HRDLog/eQSL upload below is a genuine
        // fire-and-forget Task.Run -- nothing has ever awaited or tracked it, so closing Jimmy
        // (or a forced/Windows shutdown) moments after a QSO completed could tear the process down
        // mid-upload, silently dropping a real-time upload attempt that was seconds from finishing,
        // with no chance to notice or retry. Tracked here so Controller_FormClosing can give
        // outstanding uploads a short, bounded grace period to actually finish (see
        // WaitForPendingUploads below) instead of exiting instantly regardless -- bounded, not a
        // block on the local logbook write (already synchronous/durable, see ImportLiveLoggedQso's
        // own comment) and never indefinite, so a genuinely hung upload still can't delay shutdown
        // forever.
        private readonly object _pendingUploadsLock = new object();
        private readonly List<Task> _pendingUploads = new List<Task>();

        public LiveQsoUploadOrchestrator(Func<LiveUploadCredentials> credentials, Action notifyImported,
            Action<string> debugLog, Action<string, bool> showStatus, Func<string, string> resolveUsState)
        {
            _credentials = credentials;
            _notifyImported = notifyImported;
            _debugLog = debugLog;
            _showStatus = showStatus;
            _resolveUsState = resolveUsState;
        }

        // Called when the user saves Options -- gives automatic real-time
        // upload another chance after they've adjusted credentials/settings,
        // without requiring a full Jimmy restart.
        public void ResetClubLogRealtimeBreaker() => _clubLogRealtimeBroken = false;

        // Codex Audit 02 finding, 2026-08-21: called from Controller_FormClosing. Best-effort and
        // strictly bounded -- returns promptly (true) when nothing is outstanding, otherwise waits
        // up to `timeout` for whatever real-time uploads are still in flight. Returns false if the
        // timeout elapsed with uploads still pending (Controller_FormClosing proceeds with closing
        // either way; this only gives already-close-to-finishing uploads a real chance, it never
        // blocks shutdown indefinitely for a hung network call).
        public bool WaitForPendingUploads(TimeSpan timeout)
        {
            Task[] snapshot;
            lock (_pendingUploadsLock) { snapshot = _pendingUploads.ToArray(); }
            if (snapshot.Length == 0) return true;
            return Task.WaitAll(snapshot, timeout);
        }

        // Codex Audit 02 release blocker, 2026-08-21: returns whether the local DB write below
        // actually succeeded -- the caller (WsjtxClient.cs's RequestLog) must not claim "QSO
        // logged" success (loggedCall/logList/the logged sound/ShowStatus's "call logged" text)
        // on a failed local write, and must surface the failure to the operator instead of only
        // logging it to DebugOutput. See the false-branch below and the caller's own comment.
        public bool ImportLiveLoggedQso(string dxCall, Dictionary<string, string> fields, string adifRecord, string dedupKey)
        {
            // Added 2026-08-10, real incident: capture the target database path HERE, on the
            // calling thread, synchronously -- NOT inside the background task below. LogbookDb's
            // parameterless constructor resolves its path lazily, at the moment it actually runs
            // (Environment.GetEnvironmentVariable("JIMMY_TEST_DB_PATH") ?? the real user data
            // path -- see LogbookDb.DbPath). Task.Run below is fire-and-forget: nothing awaits
            // it, so it can execute an arbitrary, unbounded time after this method returns.
            // Confirmed live: a unit test that sets JIMMY_TEST_DB_PATH, calls this method, then
            // restores the environment variable in its own `finally` block raced this background
            // task and lost -- four synthetic test QSOs landed in the real production logbook.db
            // instead of the test's own throwaway database, because the task didn't actually run
            // until AFTER the test had already restored the real path. Resolving the path here
            // and threading it through explicitly (LogbookDb's own dbPath-argument constructor,
            // "used by automated tests" per its existing doc comment) makes correctness
            // independent of how long the task takes to actually start -- the path is locked in
            // at the moment the QSO was actually logged, which is also the semantically correct
            // behavior for production use, not just a test workaround.
            string dbPath = LogbookDb.DbPath;

            // Release-audit finding, 2026-08-20 (release blocker): the durable local database
            // write used to happen as the FIRST statement inside the fire-and-forget Task.Run
            // below, coupled to the same task as the slow, optional network uploads. Nothing
            // awaits that task and nothing drains it on shutdown (Controller_FormClosing holds
            // no handle to it) -- closing Jimmy, a crash, or a forced/Windows shutdown immediately
            // after a QSO completed could leave it visible in the session UI (logList/ShowLogged
            // already ran synchronously in RequestLog before this method was even called) but
            // permanently absent from Jimmy's own logbook, Still Need, and every pending-upload
            // queue, with no warning and no retry -- the record may simply never have reached the
            // database. Fixed by moving the durable write here, synchronously, on the calling
            // thread (already the UI thread -- see DirectApplyStatus/RequestLog), BEFORE this
            // method returns. A local SQLite insert is a few milliseconds; CLAUDE.md's own
            // "reliability over new features"/"never knowingly lose a valid QSO" rules make that
            // an easy trade against upload calls that can legitimately take seconds. Only the
            // genuinely slow, optional, best-effort network uploads remain deferred below.
            // Guarded the same way the old all-in-one-task version was ("all exceptions are
            // caught and logged, never allowed to propagate") -- this now runs synchronously,
            // on the caller's own thread/stack (the UI thread, via DirectApplyStatus/RequestLog),
            // so an unguarded throw here would propagate out as an unhandled exception on that
            // call stack instead of being swallowed inside a background Task the way it used to
            // be. A write failure (locked file, disk full) still means the QSO didn't durably
            // log -- same real outcome as before -- it just must not also crash Jimmy.
            try
            {
                using (var db = new LogbookDb(dbPath))
                {
                    // resolveUsState is the same lookupManager-backed callback every other US
                    // state lookup in the app already uses (queue display, raw decodes row, HRC
                    // award check, the Rule Definitions engine, and the Logbook window's own
                    // ADIF import) -- without it, a live-logged QSO's state only ever came from
                    // the grid square, which is blank when no grid was heard and an unusable
                    // compound string like "OR-ID" when the grid straddles a state border, so the
                    // QSO silently never counted toward a State-grouped award.
                    AdifImporter.Import(db, new[] { fields }, "WSJTX", null, _resolveUsState);
                }
            }
            catch (Exception ex)
            {
                _debugLog($"ImportLiveLoggedQso local-DB write error for {dxCall}: {ex.Message}");
                // Codex Audit 02 release blocker, 2026-08-21: a failed local write must not also
                // attempt remote uploads -- dedupKey/MarkUploaded below would target a row that
                // was never actually inserted, so a "successful" remote upload could leave the
                // record present at QRZ/Club Log/etc. but permanently absent from Jimmy's own
                // logbook, Still Need, and every pending-upload queue, with nothing to reconcile
                // it later. The caller surfaces this failure to the operator (see its own comment).
                return false;
            }

            // Deliberately its own try/catch, separate from the durable write above (found while
            // fixing the release blocker above, 2026-08-21): notifyImported does its own
            // ctrl.BeginInvoke marshaling, which throws if the control's handle doesn't exist yet
            // (e.g. a console test harness with no real message loop) -- that is a best-effort
            // UI-refresh concern, completely unrelated to whether the write above actually
            // succeeded, and must never be reported to the caller as a local-DB write failure.
            try
            {
                // Keep award tracking current for this new QSO without requiring a restart:
                // refresh the live-tag "still needed" cache used during operation, and the
                // Logbook window's Awards/Still Need page if open. Safe to call synchronously/
                // from the UI thread -- this callback already does its own ctrl.BeginInvoke
                // marshaling (see its construction in WsjtxClient.cs), the same pattern used
                // everywhere else in this codebase when a UI-thread caller still wants to defer
                // to the next message-loop turn rather than because it might be on some other
                // thread.
                _notifyImported();
            }
            catch (Exception ex)
            {
                _debugLog($"ImportLiveLoggedQso notifyImported callback error for {dxCall}: {ex.Message}");
            }

            Task uploadTask = null;
            uploadTask = Task.Run(async () =>
            {
                try
                {
                    using (var db = new LogbookDb(dbPath))
                    {
                        var creds = _credentials();
                        bool needQrz = creds.QrzUploadEnabled && creds.QrzUploadRealtime &&
                                       !string.IsNullOrWhiteSpace(creds.QrzLogbookApiKey);
                        bool needClubLog = creds.ClubLogUploadEnabled && creds.ClubLogUploadRealtime &&
                                       !string.IsNullOrWhiteSpace(creds.ClubLogUploadEmail) &&
                                       !string.IsNullOrWhiteSpace(creds.ClubLogUploadPassword) &&
                                       !string.IsNullOrWhiteSpace(creds.ClubLogUploadCallsign) &&
                                       !_clubLogRealtimeBroken;
                        // No circuit breaker, same as QRZ -- HRDLog's own API isn't documented to
                        // require one the way Club Log's does.
                        bool needHrdLog = creds.HrdLogUploadEnabled && creds.HrdLogUploadRealtime &&
                                       !string.IsNullOrWhiteSpace(creds.HrdLogUploadCode) &&
                                       !string.IsNullOrWhiteSpace(creds.HrdLogUploadCallsign);
                        // No circuit breaker: eQSL's transport goes through EngineHost, which
                        // already applies its own bounded timeout (ExternalDataClient.SlowTimeoutMs)
                        // per call -- a single slow/failed upload can't cascade the way an
                        // unbounded HTTP retry storm could.
                        bool needEqsl = creds.EqslUploadEnabled && creds.EqslUploadRealtime &&
                                       !string.IsNullOrWhiteSpace(creds.EqslUsername) &&
                                       !string.IsNullOrWhiteSpace(creds.EqslPassword);

                        if (!needQrz && !needClubLog && !needHrdLog && !needEqsl) return;

                        if (needQrz)
                        {
                            var qrzClient = new QrzLogbookClient();
                            bool ok = await qrzClient.InsertAsync(creds.QrzLogbookApiKey, adifRecord).ConfigureAwait(false);
                            if (ok) db.MarkUploaded(dedupKey, "QRZ", DateTime.UtcNow);
                            else _debugLog($"QRZ real-time upload failed for {dxCall}: {qrzClient.LastError}");
                        }

                        if (needClubLog)
                        {
                            var clClient = new ClubLogUploadClient();
                            bool ok = await clClient.RealtimeUploadAsync(
                                creds.ClubLogUploadEmail, creds.ClubLogUploadPassword, creds.ClubLogUploadCallsign,
                                ClubLogAppKey.Resolve(), adifRecord).ConfigureAwait(false);
                            if (ok)
                            {
                                db.MarkUploaded(dedupKey, "CLUBLOG", DateTime.UtcNow);
                            }
                            else
                            {
                                _debugLog($"Club Log real-time upload failed for {dxCall}: {clClient.LastError}");
                                // Per Club Log's own integration rules: on any real-time failure,
                                // stop sending further automatic requests and tell the user --
                                // don't silently keep retrying on every subsequent QSO.
                                _clubLogRealtimeBroken = true;
                                _showStatus($"Club Log real-time upload error, automatic upload paused: {clClient.LastError}", true);
                            }
                        }

                        if (needHrdLog)
                        {
                            var hrdClient = new HrdLogUploadClient();
                            bool ok = await hrdClient.InsertAsync(
                                creds.HrdLogUploadCallsign, creds.HrdLogUploadCode, adifRecord).ConfigureAwait(false);
                            if (ok) db.MarkUploaded(dedupKey, "HRDLOG", DateTime.UtcNow);
                            else _debugLog($"HRDLog.net real-time upload failed for {dxCall}: {hrdClient.LastError}");
                        }

                        if (needEqsl)
                        {
                            // ExternalDataClient's calls are synchronous (a bounded blocking TCP
                            // round-trip to EngineHost, not an async HTTP call) -- already off the
                            // UI thread here, so calling it directly is fine, same reasoning as
                            // OtaSpotsWindow's own RefreshSpots().
                            var eqslClient = new ExternalDataClient();
                            string outcome = eqslClient.UploadEqsl(creds.EqslUsername, creds.EqslPassword, adifRecord, out string eqslError);
                            if (eqslError != null)
                            {
                                _debugLog($"eQSL real-time upload failed for {dxCall}: {eqslError}");
                            }
                            else if (outcome == "rejected" || outcome == "authfail")
                            {
                                _debugLog($"eQSL real-time upload rejected for {dxCall}: {outcome}");
                            }
                            else
                            {
                                // "accepted"/"pending"/"duplicate" all mean eQSL has the record.
                                db.MarkUploaded(dedupKey, "EQSL", DateTime.UtcNow);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _debugLog($"ImportLiveLoggedQso error: {ex.Message}");
                }
            });
            lock (_pendingUploadsLock) { _pendingUploads.Add(uploadTask); }
            // Removed on the task's OWN continuation once it finishes -- WaitForPendingUploads
            // above only cares about uploads that are genuinely still running right now, so the
            // tracked list must not grow unbounded across a long session.
            uploadTask.ContinueWith(_ =>
            {
                lock (_pendingUploadsLock) { _pendingUploads.Remove(uploadTask); }
            }, TaskScheduler.Default);
            return true;
        }
    }
}
