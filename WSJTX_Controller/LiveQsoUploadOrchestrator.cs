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

        public void ImportLiveLoggedQso(string dxCall, Dictionary<string, string> fields, string adifRecord, string dedupKey)
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
            Task.Run(async () =>
            {
                try
                {
                    using (var db = new LogbookDb(dbPath))
                    {
                        // resolveUsState is the same lookupManager-backed callback every other US
                        // state lookup in the app already uses (queue display, raw decodes row,
                        // HRC award check, the Rule Definitions engine, and the Logbook window's
                        // own ADIF import) -- without it, a live-logged QSO's state only ever came
                        // from the grid square, which is blank when no grid was heard and an
                        // unusable compound string like "OR-ID" when the grid straddles a state
                        // border, so the QSO silently never counted toward a State-grouped award.
                        AdifImporter.Import(db, new[] { fields }, "WSJTX", null, _resolveUsState);

                        // Keep award tracking current for this new QSO without requiring a
                        // restart: refresh the live-tag "still needed" cache used during
                        // operation, and the Logbook window's Awards/Still Need page if open.
                        _notifyImported();

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

                        if (!needQrz && !needClubLog && !needHrdLog) return;

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
                    }
                }
                catch (Exception ex)
                {
                    _debugLog($"ImportLiveLoggedQso error: {ex.Message}");
                }
            });
        }
    }
}
