using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WsjtxUdpLib.Messages.Out;

namespace WSJTX_Controller
{
    // Self-sufficiency plan Phase 6: talks to the native engine host directly over its local TCP
    // control port (NativeEngineClient.ControlPort) instead of the standard WSJT-X UDP protocol.
    // Root-caused live, 2026-08-08: every native-engine crash/freeze chased that whole day traced
    // back to the UDP path's stateful heartbeat/negotiation handshake specifically -- not to raw
    // UDP packet delivery, which is effectively lossless on loopback anyway. That handshake is
    // also the exact thing that's timing-sensitive on a slower machine (a slow CAT/audio open
    // racing the engine's one-shot opening Heartbeat -- see WsjtxClient.Protocol.cs's own comment
    // on the 2026-08-06/07 "stuck at WSJT-X detected" bug this caused). This class removes the
    // handshake from the picture entirely: connect, ask for a snapshot, get one back. No
    // Heartbeat, no NegoState machine, nothing to race.
    //
    // UDP-to-Direct parity pass, 2026-08-12: this is now the sole production transport --
    // Controller.ApplyEngineMode() uses it unconditionally, in production AND in test mode
    // alike (as of the 2026-08-18 UDP-to-Direct test-harness migration -- see that method's
    // own comment). WsjtxClient's classic UDP path (ConnectNativeEngine / WsjtxClient.
    // Protocol.cs) was removed entirely in that same pass once JimmyDirectReplay.py replaced
    // JimmyReplay.py as the replay-test harness and nothing called it anymore, in production
    // or in test mode -- this class is now the ONLY way Jimmy ever talks to the engine host
    // (real or, under TestModeGuard.IsTestMode, JimmyDirectReplay.py's fake control-port
    // server standing in for it).
    //
    // Maximum reuse by design: every decode/status arriving here gets turned into the exact same
    // DecodeMessage/StatusMessage objects the UDP path already builds from wire bytes, then fed
    // through the exact same downstream methods (ProcessDecodeMsg, etc.) -- classification,
    // awards, call-queue ranking, and notifications all keep working unchanged, because they
    // never see a difference between "arrived over UDP" and "arrived over this".
    public partial class WsjtxClient
    {
        private System.Windows.Forms.Timer _directPollTimer;
        private bool _directConnected;
        private ulong _directLastSlotSeen;

        // Engine's last CONFIRMED TX drive level (0.0-1.0), or null when unknown -- no snapshot
        // yet this session, or the engine is currently treated as disconnected. Refreshed from
        // radio.TxLevel on every poll (DirectApplyStatus) and set the instant a SET_TX_LEVEL is
        // confirmed (DirectSetEngineTxLevel). F11/F12 (WsjtxClient.BandAudio.cs's AudioLevel())
        // and the Options > Radio "FT8/FT4 transmit tone level" spinner both read this; neither
        // ever displays a guessed number while it is null.
        private double? _engineTxLevel;
        public double? EngineTxLevel => _engineTxLevel;

        // The one shared guard that serializes every SET_TX_LEVEL originator -- F11/F12 and the
        // (modeless) Options spinner can be driven at the same time, so this keeps exactly one
        // level change outstanding at a time and guarantees each relative F11/F12 step is computed
        // from _engineTxLevel as the engine last CONFIRMED it, never from a value a still-unconfirmed
        // change has already superseded. See DirectSetEngineTxLevel.
        private bool _txLevelChangeInFlight;
        private readonly HashSet<string> _directSeenDecodeSignatures = new HashSet<string>();

        // Codex Audit 02 release blockers, 2026-08-21 ("create ordered/serialized Direct command
        // dispatch" + "make Direct transport exception-safe"): every DirectSendXxx/DirectSetXxx
        // helper below used to open its OWN independent Task.Run, racing every other one for a
        // TCP connection to the same EngineHost control port with no ordering guarantee at all --
        // SetupCq's own "SET_TX_OFFSET, then CALL_CQ, then SET_TX_ENABLED" sequence (and ReplyTo's
        // "SET_TX_OFFSET, then REPLY") could arrive at the engine in a different order than issued,
        // and an unrelated later action's command could race an earlier one still in flight. Worse,
        // none of those independent Task.Run bodies were exception-guarded -- DirectSendCommand's
        // own ConnectAsync(...).Wait(1000) rethrows as AggregateException on a fast connection
        // refusal (Task.Wait() rethrows a faulted task's exception, it does not just return false),
        // and the Write/Shutdown calls around it are unguarded too -- so a refused/reset connection
        // could throw INSIDE a Task.Run lambda nothing ever awaits, silently killing that
        // continuation before any cleanup (clearing an in-flight flag, telling the operator it
        // failed) ran. That is exactly the failure shape that could leave _pendingBandIdx/
        // _tuningRequestInFlight/_tierChangeRequestInFlight/OTA in-flight guards stuck forever.
        //
        // Fixed with one ordered worker: every Direct WRITE command (not the separate SNAPSHOT
        // poll timer, which already has its own correct epoch-guarded loop -- DirectPollTick's own
        // comment) is enqueued here and sent strictly one at a time, in enqueue order, by a single
        // dedicated background worker -- the same forward-progress and exception-safety guarantee
        // a lock held across the whole network round-trip would give, without actually blocking
        // any caller while holding one. Two queues, not a true priority queue: _directPriorityQueue
        // (HALT_TX only) is always fully drained before _directNormalQueue, so a HALT_TX enqueued
        // while other commands are still WAITING (not yet sent) jumps ahead of them -- it cannot
        // interrupt a command already in flight (DirectSendCommand's own bounded ~4s worst case
        // still applies to whatever is currently sending), but that bound was already the accepted
        // worst case for any single Direct command, and jumping the queue is strictly better than
        // no priority at all for a safety action.
        private class DirectCommandRequest
        {
            public string Command;
            public Action<string> OnComplete;
            public bool IsTxArm;
            // Codex Audit 04 finding 1, 2026-08-21: true for every ordinary caller (marshal the
            // completion onto the UI thread via ctrl.BeginInvoke, as always) except
            // HaltTxAndWaitForShutdown's own blocking shutdown-only HALT_TX -- see that method's
            // own comment for why marshaling would deadlock there.
            public bool MarshalToUiThread = true;
        }
        private readonly object _directQueueLock = new object();
        private readonly Queue<DirectCommandRequest> _directPriorityQueue = new Queue<DirectCommandRequest>();
        private readonly Queue<DirectCommandRequest> _directNormalQueue = new Queue<DirectCommandRequest>();
        private readonly System.Threading.SemaphoreSlim _directQueueSignal = new System.Threading.SemaphoreSlim(0, int.MaxValue);
        private bool _directCommandWorkerStarted;

        // Codex Audit 03 finding 3, 2026-08-21: the write queues carried no connection awareness
        // at all -- neither a reconnect (ConnectDirectEngine, e.g. after the engine host crashes
        // and restarts) nor application shutdown ever cleared them, and enqueue stayed possible
        // throughout. A command queued for an old/crashed session could sit there and execute
        // against a brand-new EngineHost process once it came back up, and a command enqueued
        // during the brief window between the operator closing Jimmy and CloseComm() actually
        // killing the engine process would still reach it. Set true only from
        // ShutdownDirectCommandQueue below (called once, from Closing()) -- a reconnect is NOT
        // shutdown (ConnectDirectEngine below purges the queues for the same reason but must
        // keep accepting new commands for the session it is starting), so this flag is
        // deliberately narrower than "just purge on every connection-state change."
        private bool _directQueueShutdown;

        // Drains both queues unconditionally, delivering a null response to every pending
        // caller the same way a purge/failure already does (see PurgePendingTxArmCommands_NoLock
        // and DirectSendCommandSafe's own comments) -- used on reconnect (nothing queued for the
        // old session belongs to the new one) and on shutdown (nothing queued should reach an
        // engine process that is about to be killed).
        private void PurgeAllDirectQueues_NoLock()
        {
            void Drain(Queue<DirectCommandRequest> q)
            {
                while (q.Count > 0) DeliverDirectCompletion(q.Dequeue(), null);
            }
            Drain(_directPriorityQueue);
            Drain(_directNormalQueue);
        }

        // Shared completion-delivery point for every path that can finish a DirectCommandRequest
        // (the worker's own normal completion, a halt purge, a shutdown purge/reject) -- see
        // DirectCommandRequest.MarshalToUiThread's own comment for why this branches.
        private void DeliverDirectCompletion(DirectCommandRequest req, string response)
        {
            if (req.OnComplete == null) return;
            if (req.MarshalToUiThread)
            {
                try { ctrl.BeginInvoke(new Action(() => req.OnComplete(response))); }
                catch { /* ctrl disposed/closing -- best-effort */ }
            }
            else
            {
                try { req.OnComplete(response); }
                catch { /* best-effort, matches every other completion path's own tolerance */ }
            }
        }

        // Called once, from WsjtxClient.cs's Closing() (via CloseComm() -> wsjtxClient.Closing()),
        // right after that method's own HaltTx() -- see finding 3's own comment above. Stops the
        // SNAPSHOT poll timer (the main source of new enqueues, e.g. SetupCq's post-QSO CALL_CQ
        // auto-resume) and permanently closes the write queue: every EnqueueDirectCommand call
        // after this point is a no-op that still calls onComplete(null), so no caller can hang
        // waiting on a completion that will never come.
        internal void ShutdownDirectCommandQueue()
        {
            _directPollTimer?.Stop();
            lock (_directQueueLock)
            {
                _directQueueShutdown = true;
                PurgeAllDirectQueues_NoLock();
            }
        }

        // Codex Audit 03 release blocker #1, 2026-08-21: HALT_TX jumping the queue via priority
        // only reorders it relative to normal-queue commands that have not been DEQUEUED yet --
        // it never removed them. A CALL_CQ/REPLY/SET_TX_ENABLED "1"/SET_TUNING "1" already sitting
        // in _directNormalQueue when the operator hits Halt was still sent right after HALT_TX
        // finished, re-arming TX moments after an emergency stop. Confirmed real: nothing in
        // EnqueueDirectCommand or the worker ever purged or invalidated a normal-queue entry.
        // Fixed here, not with a generation counter Codex's own writeup suggested: every
        // enqueue (both normal and priority) runs under the SAME _directQueueLock end to end, so
        // there is no window between "purge what's queued right now" and "HALT_TX itself becomes
        // the front of the line" for a stale TX-arm command to slip through -- nothing else can
        // be mid-enqueue while this lock is held. isTxArm marks the handful of commands that can
        // actually key the transmitter (CALL_CQ, REPLY, SET_TX_ENABLED "1", SET_TUNING "1" --
        // never SET_TX_ENABLED "0"/SET_TUNING "0", which are themselves disable/stop commands and
        // must never be purged). This does NOT reach a command already dequeued and mid-flight in
        // DirectSendCommandSafe when Halt is pressed -- that remains bounded by
        // DirectSendCommand's own connect/read timeouts (~4s worst case), the same accepted limit
        // already documented on the dispatcher's own class comment, and the separate concern
        // Codex's finding 5 raises about EngineHost's own serial control-socket accept loop.
        private void PurgePendingTxArmCommands_NoLock()
        {
            if (_directNormalQueue.Count == 0) return;
            int kept = _directNormalQueue.Count;
            var survivors = new Queue<DirectCommandRequest>(kept);
            while (_directNormalQueue.Count > 0)
            {
                var item = _directNormalQueue.Dequeue();
                if (item.IsTxArm)
                {
                    // Null response matches the existing "did not reach the engine" contract
                    // every current caller's onComplete already tolerates (see
                    // DirectSendCommandSafe's own comment).
                    DeliverDirectCompletion(item, null);
                }
                else
                {
                    survivors.Enqueue(item);
                }
            }
            while (survivors.Count > 0) _directNormalQueue.Enqueue(survivors.Dequeue());
        }

        // Enqueues one Direct WRITE command for the ordered worker to send, strictly after every
        // earlier-enqueued command has already been sent and answered (or failed) -- see the class
        // comment above. onComplete is invoked EXACTLY ONCE, always marshaled onto the UI thread
        // via ctrl.BeginInvoke, with the raw response string on success or null on ANY failure
        // (refused connection, write error, read timeout, or any other exception -- see
        // DirectSendCommandSafe's own comment) or on being purged by a halt (see
        // PurgePendingTxArmCommands_NoLock above). isPriority is for HALT_TX only. isTxArm marks a
        // command that can key the transmitter -- see PurgePendingTxArmCommands_NoLock's own
        // comment for exactly which ones and why it matters.
        private void EnqueueDirectCommand(string command, Action<string> onComplete, bool isPriority = false, bool isTxArm = false, bool marshalToUiThread = true)
        {
            var req = new DirectCommandRequest { Command = command, OnComplete = onComplete, IsTxArm = isTxArm, MarshalToUiThread = marshalToUiThread };
            lock (_directQueueLock)
            {
                // Codex Audit 03 finding 3: once ShutdownDirectCommandQueue has run, nothing new
                // gets queued at all -- the engine process is about to be (or already is) killed,
                // so there is no connection left for this command to reach. Still delivers a null
                // completion so a caller that awaits one is never left hanging.
                if (_directQueueShutdown)
                {
                    DeliverDirectCompletion(req, null);
                    return;
                }
                if (isPriority)
                {
                    // Codex Audit 03 release blocker #1: purge stale TX-arm commands under the
                    // same lock, before HALT_TX itself is enqueued -- see this class's own comment.
                    PurgePendingTxArmCommands_NoLock();
                    _directPriorityQueue.Enqueue(req);
                    // T7 fix, 2026-08-23: purging the QUEUE above only reaches commands still
                    // waiting -- a command already dequeued and blocked inside DirectSendCommand
                    // is untouched by it. Abort that in-flight command now too (see
                    // AbortInFlightDirectCommand's own comment) so the worker is free to dequeue
                    // and send THIS priority command immediately instead of waiting behind the
                    // in-flight one's own ~4s worst-case budget. isPriority is HALT_TX-only, so
                    // this only ever fires for an emergency stop, never an ordinary command.
                    AbortInFlightDirectCommand();
                }
                else
                {
                    _directNormalQueue.Enqueue(req);
                }
                if (!_directCommandWorkerStarted)
                {
                    _directCommandWorkerStarted = true;
                    System.Threading.Tasks.Task.Run(RunDirectCommandWorkerAsync);
                }
            }
            _directQueueSignal.Release();
        }

        // Found running the automated test suite, 2026-08-21: WaitAsync(), not the blocking
        // Wait() a first pass of this used -- a synchronous SemaphoreSlim.Wait() inside an
        // infinite loop permanently PINS one real thread-pool thread for as long as this
        // WsjtxClient instance lives, even while idle waiting for the next command. Harmless in
        // production (exactly one WsjtxClient instance exists for the app's whole lifetime, so
        // at most one extra parked thread), but the test suite constructs a great many
        // WsjtxClient instances across ~1000 tests, and every one that ever sends a single Direct
        // command leaves its own worker permanently blocked here -- confirmed live: enough
        // accumulate late in a full suite run to transiently starve the thread pool, delaying an
        // unrelated LATER test's own first EnqueueDirectCommand past its 3s PumpUntil timeout
        // (intermittent, not every run -- exactly the symptom of thread-pool growth lagging
        // behind a burst of demand, not a logic bug). await WaitAsync() releases the thread-pool
        // thread back to the pool while genuinely idle and resumes on whatever thread is free
        // once signaled -- strictly better with no behavior change to the ordering/exception-
        // safety guarantees this worker exists for.
        private async System.Threading.Tasks.Task RunDirectCommandWorkerAsync()
        {
            while (true)
            {
                await _directQueueSignal.WaitAsync().ConfigureAwait(false);
                DirectCommandRequest req = null;
                lock (_directQueueLock)
                {
                    if (_directPriorityQueue.Count > 0) req = _directPriorityQueue.Dequeue();
                    else if (_directNormalQueue.Count > 0) req = _directNormalQueue.Dequeue();
                }
                if (req == null) continue; // defensive only -- the semaphore count always matches enqueued items exactly
                string response = DirectSendCommandSafe(req.Command);
                DeliverDirectCompletion(req, response);
            }
        }

        // Codex Audit 02 release blocker, 2026-08-21: wraps DirectSendCommand so a thrown
        // exception (see the class comment above -- AggregateException from ConnectAsync(...).
        // Wait(1000) on a fast refusal, or any exception from the unguarded Write/Shutdown calls)
        // can never escape as an unobserved fault on the worker's background Task, which would
        // otherwise silently kill the worker loop itself and wedge EVERY future Direct command,
        // not just the one that failed. A thrown exception becomes an ordinary null response here
        // -- exactly like every other already-expected failure mode (refused connection, read
        // timeout) already was, so no caller needs to special-case it.
        private string DirectSendCommandSafe(string command)
        {
            try
            {
                return DirectSendCommand(command);
            }
            catch (Exception ex)
            {
                DebugOutput($"{Time()} [DIRECT] '{command}' threw: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        // Release-blocker follow-up, 2026-08-19: guards against two overlapping SNAPSHOT polls
        // running at once now that DirectPollTick's network I/O moved off the UI thread (see its
        // own comment) -- the old fully-synchronous version got this for free (a Timer.Tick
        // handler can't be re-entered while still running), so making it async needs to
        // re-establish that same "never more than one poll in flight" invariant explicitly.
        private bool _directPollInFlight;

        // Incremented on every ConnectDirectEngine() call. A background poll captures this value
        // when it starts; if a NEW connection (reconnect, or the engine auto-restarting) happens
        // while that poll's network I/O is still in flight, its eventual continuation compares
        // its captured value against the current one and discards a stale result instead of
        // acting on behalf of a connection that no longer exists -- see DirectPollTick's own
        // comment for the full reasoning.
        private int _directConnectionEpoch;

        // EngineHost ownership / session identity, 2026-08-23: the per-launch token Jimmy
        // expects THIS connection's engine-host child to echo back on every SNAPSHOT (see
        // main.rs's own sessionToken JSON field). Set by ConnectDirectEngine; null/empty means
        // no authentication was requested for this connection (test-mode default -- see that
        // method's own comment) and DirectPollTick's check is skipped entirely, matching prior
        // (pre-authentication) behavior exactly for those callers. _directAuthenticated is the
        // gate TX-arming commands (DirectSendCq/DirectSendReply/DirectSetTxEnabled(true)/
        // DirectSetTuning(true)) check before sending -- see each method's own comment.
        private string _directExpectedSessionToken;
        private bool _directAuthenticated;
        private bool _directSessionAuthFailureAnnounced;

        // Has DirectApplyStatus ever actually rendered a status once this connection came up?
        // Forces the very first poll's status through regardless of the transmitting/newBand
        // gate below (see that gate's own comment).
        private bool _directFirstStatusShown;

        // Has DirectApplyStatus resolved a real band at least once this connection? Gates the
        // one-time "pick a known band and go there" startup fallback below -- must fire at most
        // once per connect, never repeatedly if the band transiently reads as unknown again
        // later (e.g. a momentary CAT hiccup).
        private bool _directStartupBandResolved;

        // How often to ask the engine for a fresh snapshot. Cheap (one short-lived TCP
        // connection, one JSON round-trip) and independent of the FT8 slot period -- polling
        // faster than new data can arrive just re-reads the same slot's decodes, which
        // _directSeenDecodeSignatures below already dedupes for free.
        private const int DirectPollIntervalMs = 1000;

        // UDP-to-Direct parity pass, 2026-08-12: the UDP path announces "WSJT-X disconnected"
        // (Notify.Publish(ConnectionLostEvent) + a sound cue) via HeartbeatNotRecd once its own
        // heartbeat watchdog times out. DirectPollTick's failure branch below used to only log
        // to DebugOutput -- a hung-but-still-running control port produced no user-facing signal
        // at all under Direct mode. Threshold of 3 consecutive failed polls (~3s at the default
        // 1s poll interval) rather than announcing on the very first miss, so one transient
        // hiccup doesn't false-positive; _directLossAnnounced makes sure this fires once per
        // loss episode, not on every failed poll after the threshold.
        private const int DirectPollFailureThreshold = 3;
        private int _directConsecutivePollFailures;
        private bool _directLossAnnounced;

        // "Halt Tx when SWR > threshold" (Options > Radio, matching WSJT-X's own Radio-tab
        // safety feature), sourced from the engine's own SNAPSHOT (radio.txSwr) now that Jimmy
        // no longer runs a second, concurrent RigctldClient poll loop for this -- see
        // DirectApplyStatus's own check below. Edge-triggered (tracks the PREVIOUS poll's state)
        // so this fires HaltTx()/announces once per episode, not every tick while SWR stays high.
        private bool _swrOverThreshold;

        // Direct-mode runaway-Tx backstop, 2026-08-28 (CONFIRMED live -- HB9TIH then NE5L, same
        // session): count of completed transmit overs observed while Jimmy has NO call in
        // progress and is in LISTEN mode (not calling CQ). The engine's own QSO sequencer can
        // keep re-sending an RR73 every slot after Jimmy has cleared callInProg -- if the worked
        // station never cleanly hears it, or after an engine restart / a SET_TX_ENABLED 0 that
        // never landed on a flaky control link. The FIRST such over is tolerated (it can be the
        // legitimate final RR73 finishing in the poll after callInProg was cleared); the SECOND
        // means the engine re-keyed entirely on its own -> halt it the same way Escape does.
        // Reset whenever a real contact is in progress again (or the mode is no longer LISTEN).
        private int _directOrphanTxOvers;

        // KF4CCG race, 2026-08-29 (CONFIRMED live): bounds how many extra polls the Is73orRR73
        // completion branch (DirectApplyStatus) will HOLD callInProg while LogQso() keeps failing
        // because the DX's roger-report decode has not been ingested into allCallDict yet -- the
        // engine's Qso.TxNow flips to the final RR73 in the same ~1s poll the roger first decodes,
        // and status is applied before decodes. The roger normally lands later in that same tick,
        // so the very next poll succeeds; the cap only matters for a genuinely one-sided exchange
        // where the DX never actually reports. Reset on any callInProg transition (SetCallInProg)
        // and on a successful teardown.
        private int _directRr73LogRetries;
        private const int MaxDirectRr73LogRetries = 4;

        // Audit finding, 2026-08-30: the parallel Is73orRR73 hold for a FAILED local logbook
        // write (Finding 2, 2.0.45) had no bound at all. A locked / full / unavailable SQLite
        // file made LogQso() fail every ~1s poll, holding callInProg indefinitely -- which (a)
        // neutralizes BOTH fast Tx backstops (the _directOrphanTxOvers runaway catcher resets
        // while callInProg != null; DiscardCall() can't fire because RequestLog already
        // CancelDiscardCall()'d), leaving only the 2-11 min engine wall-clock watchdog to stop
        // a final RR73 the DX keeps triggering, and (b) re-published the "NOT saved" error
        // every poll (that policy has no throttle). Bounded here: ~10 polls (~10s) covers the
        // common transient lock (a VSS backup snapshot, an on-access AV scan) -- the 2.0.45
        // benefit -- then gives up, tears the QSO down (re-arming the orphan backstop and
        // ending the retry) with one final "still not saved" notice. Chosen short on purpose:
        // during the hold, if the DX keeps triggering the final RR73, that's unsolicited TX
        // with the fast backstops disabled, so the worst-case exposure is ~1 over rather than
        // the minutes the engine watchdog would otherwise allow. CheckLateLog still retries the
        // write if the DX later re-sends a 73/RR73; a longer outage or a permanent failure
        // (disk full) loses the contact regardless of how long we hold. Reset like
        // _directRr73LogRetries.
        private int _directWriteFailRetries;
        private const int MaxDirectWriteFailRetries = 10;

        // T17 fix, 2026-08-23: what Jimmy last actually commanded via SET_FREQUENCY's own
        // sideband argument (WsjtxClient.BandAudio.cs's RetuneBand, on CONFIRMED success only --
        // see that method's own DirectSetFrequency completion callback) -- compared every poll
        // against the rig's own CAT readback (radio.RigMode) below. Null until the first
        // confirmed retune this session; a null readback or null commanded value never fires a
        // mismatch (nothing to compare yet).
        internal string _lastCommandedSideband;
        private bool _sidebandMismatchAnnounced;
        // CAT mode command/readback correlation, 2026-08-23 (independent audit finding): WHEN
        // _lastCommandedSideband was last actually set, paired with it (RetuneBand's own
        // completion callback below sets both together, never one without the other). The
        // mismatch check gives the physical rig this long to catch up (relay switching/CAT
        // mode-set propagation) before comparing at all -- a freshly confirmed retune's very
        // next poll, arriving well inside normal transition latency, must not be compared
        // against a readback that simply hasn't caught up yet. Not an "epoch"/generation counter
        // -- RetuneBand's existing `_pendingBandIdx != targetIdx` supersession guard already
        // ensures an older, since-superseded retune's delayed completion callback returns before
        // ever reaching the `_lastCommandedSideband = sideband` assignment, so this field is
        // already always the newest confirmed request; this timestamp only rate-limits how soon
        // readback comparison starts trusting a genuine mismatch as real.
        private DateTime? _lastCommandedSidebandChangedUtc;
        private static readonly TimeSpan SidebandMismatchGraceWindow = TimeSpan.FromSeconds(3);
        // How many CONSECUTIVE polls (after the grace window above has already elapsed) must
        // keep agreeing on a real mismatch before DirectApplyStatus reconciles Jimmy's own
        // _lastCommandedSideband to match the rig's actual reported mode -- see that check's own
        // comment for why this exists (stop warning forever) and why it requires sustained
        // agreement, not a single reading (a stale/transient readback must not overwrite the
        // newest requested state on its own).
        private const int SidebandReconcileAfterConsecutiveMismatches = 5;
        private int _sidebandMismatchStreak;

        // Normalizes both sides to a simple USB/LSB substring check rather than an exact string
        // match -- a real rig's own CAT-reported mode word varies by backend/model (e.g.
        // "PKTUSB"/"PKTLSB", "USB-D", "DATA-U"), so this tolerates that vocabulary instead of
        // false-positiving on every rig that doesn't echo back the bare literal "USB"/"LSB".
        // Returns true (a real mismatch) when the readback contains NEITHER substring too --
        // that means the rig reports being in some other mode entirely (FM/CW/etc.), not a
        // sideband naming variant, which is just as wrong for FT8/FT4 as the opposite sideband.
        internal static bool RigModeMismatchesCommandedSideband(string commandedSideband, string rigMode)
        {
            if (string.IsNullOrEmpty(commandedSideband) || string.IsNullOrEmpty(rigMode)) return false;
            bool rigReportsLsb = rigMode.IndexOf("LSB", StringComparison.OrdinalIgnoreCase) >= 0;
            bool rigReportsUsb = rigMode.IndexOf("USB", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!rigReportsLsb && !rigReportsUsb) return true;
            bool commandedLsb = commandedSideband.Equals("LSB", StringComparison.OrdinalIgnoreCase);
            return commandedLsb ? !rigReportsLsb : !rigReportsUsb;
        }

        // "Radio CAT link lost"/recovered -- was RigctldClient.PollOnce's own connection-health
        // check (Controller.cs's old ApplyRadioPollResult, retired 2026-08-20); now sourced from
        // the engine's own SNAPSHOT (radio.catOk/catDetail -- tempo-app/src/dto.rs's RadioStatus.
        // cat_ok), which reports the SAME underlying CAT health more directly than a second,
        // concurrent rigctldClient ever could. null = engine start value / not applicable
        // (VOX, no CAT configured) -- distinguished from a real Some(true)/Some(false) reading
        // so this doesn't fire on startup or misreport a VOX-only station as a lost CAT link.
        private bool? _lastCatOk;

        // Starts polling the engine host's control port directly. Call once the engine host
        // process is known to be starting -- there is no socket to "open" here at all, every
        // request is its own short-lived TCP connection, matching the control server's own
        // one-connection-per-command shape.
        //
        // UDP transport cleanup, 2026-08-18: this used to open with a defensive CloseAllUdp()
        // call ("tear down any UDP socket left over from a PRIOR UDP-mode session before Direct
        // mode starts"), back when ConnectNativeEngine/UdpLoop (WsjtxClient.Protocol.cs) could
        // still open a real UDP socket under TestModeGuard.IsTestMode. Both are deleted now (the
        // Direct-based replay harness, JimmyDirectReplay.py, replaced the UDP one), along with
        // WsjtxProtocolAdapter and CloseAllUdp itself -- there is no longer any UDP socket this
        // method could ever need to tear down, in production or in test mode.
        // expectedSessionToken: the token this SAME session passed to NativeEngineClient.Launch()
        // as --session-token, so DirectPollTick can refuse to trust a SNAPSHOT that didn't come
        // from the child THIS call actually launched (stale/orphan process left bound to the
        // fixed control port -- see DirectPollTick's own comment for the full reasoning).
        // Defaults to null: the many existing JimmyTests.cs call sites that construct a
        // WsjtxClient and drive it directly (StartStubEngineHostWithResponses etc.) never
        // launched a real child and have no token to expect, so they get the pre-authentication
        // behavior unchanged -- only Controller.cs's real ApplyEngineMode call site supplies one.
        public void ConnectDirectEngine(string myCallIn, string myGridIn, string expectedSessionToken = null)
        {
            myCall = string.IsNullOrWhiteSpace(myCallIn) ? null : myCallIn.Trim().ToUpperInvariant();
            myGrid = myGridIn;
            _directConnectionEpoch++;
            _directExpectedSessionToken = expectedSessionToken;
            _directAuthenticated = string.IsNullOrEmpty(expectedSessionToken);
            _directSessionAuthFailureAnnounced = false;
            // Codex Audit 03 finding 3, 2026-08-21: a write queued for the PRIOR connection (the
            // engine host that just crashed/restarted, or the session being torn down for a
            // deliberate reconnect) does not belong to this new one -- purge it rather than
            // letting the worker send it to the freshly (re)started engine process. Does not set
            // _directQueueShutdown: this is a reconnect, not a close, so new commands must keep
            // being accepted for the session about to start below.
            lock (_directQueueLock) { PurgeAllDirectQueues_NoLock(); }
            _directPollInFlight = false;
            _directSeenDecodeSignatures.Clear();
            _directLastSlotSeen = 0;
            _directFirstStatusShown = false;
            _directStartupBandResolved = false;
            _directConsecutivePollFailures = 0;
            _directLossAnnounced = false;
            _swrOverThreshold = false;
            _directOrphanTxOvers = 0;
            _directRr73LogRetries = 0;
            _directWriteFailRetries = 0;
            _lastCatOk = null;
            _lastCommandedSideband = null;
            _lastCommandedSidebandChangedUtc = null;
            _sidebandMismatchAnnounced = false;
            _sidebandMismatchStreak = 0;
            _directConnected = true;
            // UDP-to-Direct parity pass, 2026-08-12: a stale bandIdx/lastDialFrequency surviving
            // from a PRIOR connection could make _directStartupBandResolved's own "band still
            // unknown, pick a fallback and retune" check above see a non-null bandIdx and skip
            // itself, even though nothing in this new connection has actually confirmed a real
            // band yet. Same "fresh connection = clean slate" reasoning as timeOffsets below.
            bandIdx = null;
            lastDialFrequency = null;
            // Found in the Direct-engine-path review, 2026-08-12: a reconnect (operator
            // relaunch, or the engine auto-restarting after a crash -- see DirectPollTick's own
            // comment) left stale pre-disconnect DT samples sitting in timeOffsets, which the
            // next period boundary would then average together with fresh post-reconnect
            // decodes as if they were one continuous measurement. A fresh connection is a clean
            // slate for clock-offset tracking, same as _directLastSlotSeen/
            // _directSeenDecodeSignatures just above already are for decode tracking.
            // _clockWasAcceptable resets to null (not false or true): the clock's condition
            // under THIS connection genuinely hasn't been measured yet, so the very next real
            // evaluation is free to announce either way without being compared against a
            // possibly-stale assumption carried over from before the reconnect.
            timeOffsets.Clear();
            timeOffset = 0;
            _clockWasAcceptable = null;
            opMode = OpModes.ACTIVE;
            // jimmy-engine-host itself always starts a fresh session hardcoded to Tier::Ft8
            // (main.rs's own startup set_tier call) -- match that here so this tracked value
            // (there is no server read-back; see DirectApplyStatus's own comment on why this is
            // tracked optimistically) starts in sync. SetOperatingMode/DirectSetTier update it
            // from here on whenever the operator actually switches modes.
            this.mode = "FT8";

            if (_directPollTimer == null)
            {
                _directPollTimer = new System.Windows.Forms.Timer { Interval = DirectPollIntervalMs };
                _directPollTimer.Tick += (s, e) => DirectPollTick();
            }
            _directPollTimer.Start();
            DebugOutput($"{Time()} [DIRECT] connected -- polling control port {NativeEngineClient.ControlPort} every {DirectPollIntervalMs}ms");
        }

        public void DisconnectDirectEngine()
        {
            _directPollTimer?.Stop();
            _directConnected = false;
            // Codex Audit 03 finding 3, 2026-08-21: same reasoning as ConnectDirectEngine's own
            // purge (this is always called immediately before it, at the one real call site,
            // Controller.cs's reconnect sequence) -- belt and braces in case that ever changes.
            lock (_directQueueLock) { PurgeAllDirectQueues_NoLock(); }
        }

        // Release-blocker follow-up, 2026-08-19: root-caused live (temporary instrumentation,
        // since removed) a genuine, sustained keyboard/focus lockup reported on a real fresh
        // install -- NOT a hotkey-recognition bug (ProcessCmdKey correctly saw every posted key,
        // no exception anywhere in the key-handling path) and NOT a foreground/focus problem
        // (GetForegroundWindow()/GetGUIThreadInfo both showed real, stable foreground and focus
        // throughout the stuck window, measured independently of Jimmy's own state). The actual
        // cause: DirectSendCommand's `connectTask.Wait(1000)` (below) is a genuinely BLOCKING
        // wait, and this method used to call it synchronously, directly on the UI thread, from
        // a System.Windows.Forms.Timer.Tick handler (Timer.Tick always fires on the UI thread).
        // DirectSendCommand's own comment assumed "on loopback, 'nothing listening' refuses the
        // connection almost instantly" -- measured directly (a standalone TcpClient.ConnectAsync
        // against this exact closed port, same code path): that assumption is WRONG on at least
        // some real Windows machines -- the actual connection-refused resolution took ~2 seconds,
        // well past the 1000ms wait budget, so `Wait(1000)` never once saw the real result within
        // budget and always burned the FULL second. Since DirectPollIntervalMs is also 1000ms,
        // the timer fired again almost immediately after each blocking wait ended, leaving the UI
        // thread blocked nearly continuously -- for as long as the engine was never actually
        // running and listening on the control port, which is exactly the "not configured yet"
        // first-run state (and any other state where the engine isn't up). A UI thread that's
        // blocked ~100% of the time still paints its last frame and still answers an occasional
        // WM_NULL ping between blocks (so Process.Responding can still read true), but has almost
        // no time left to pump real keyboard/mouse/WM_CLOSE messages -- exactly the reported
        // symptom (arrow keys not advancing, Tab not moving focus, Alt+F4 not closing) with no
        // exception, no crash, and no foreground/focus anomaly to find, because there wasn't one.
        //
        // Fix: do the actual network I/O (DirectSendCommand + JSON parse) on a background Task,
        // never on the UI thread, however long it genuinely takes -- then marshal only the
        // (already-computed) result back via BeginInvoke to do the real state updates/UI work,
        // matching the exact pattern Controller.ApplyEngineMode already uses for Launch(). This
        // is now the ONLY place SNAPSHOT is polled automatically/unconditionally (every other
        // DirectSendCommand caller is a one-off, operator-triggered command, e.g. a button press
        // or hotkey, where a bounded synchronous wait is an accepted, already-documented
        // tradeoff, not a repeating every-second UI-thread stall) -- so this is the one call site
        // that actually needed to change.
        //
        // _directPollInFlight guards against a second poll starting while a slow one is still
        // out (the old synchronous version got this for free; Timer.Tick can't re-enter itself).
        // _directConnectionEpoch guards against a stale poll's result being applied after a
        // reconnect happened while it was still in flight.
        private void DirectPollTick()
        {
            if (!_directConnected || _directPollInFlight) return;
            _directPollInFlight = true;
            int epoch = _directConnectionEpoch;

            System.Threading.Tasks.Task.Run(() =>
            {
                DirectSnapshot snap = null;
                string failMessage = null;
                try
                {
                    string json = DirectSendCommand("SNAPSHOT");
                    if (json == null || json.Length == 0 || json.StartsWith("ERR"))
                        failMessage = "empty or ERR response";
                    else
                    {
                        snap = JsonSerializer.Deserialize<DirectSnapshot>(json, DirectJsonOptions);
                        if (snap == null) failMessage = "null snapshot";
                    }
                }
                catch (Exception ex)
                {
                    // Best-effort, matches the UDP path's own tolerance for a transient miss --
                    // the engine host restarting (auto-restart on crash) just means the next
                    // poll's connection attempt fails until it's back up, not a fatal error here.
                    failMessage = ex.Message;
                }

                ctrl.BeginInvoke(new Action(() =>
                {
                    _directPollInFlight = false;
                    // Superseded by a disconnect or a fresh reconnect while this poll was still
                    // running -- this result belongs to a connection that's no longer current;
                    // the new/absent connection's own state is authoritative now, not this.
                    if (!_directConnected || epoch != _directConnectionEpoch) return;

                    if (failMessage != null)
                    {
                        DebugOutput($"{Time()} [DIRECT] SNAPSHOT poll failed: {failMessage}");
                        DirectHandlePollFailure();
                        return;
                    }

                    // A real snapshot came back -- whatever failure streak was building is over.
                    _directConsecutivePollFailures = 0;
                    _directLossAnnounced = false;

                    // Independent audit finding, 2026-08-23 (EngineHost ownership / session
                    // identity, HIGH PRIORITY): a real, well-formed SNAPSHOT response alone used
                    // to be trusted unconditionally -- but a TCP connection to port 58239
                    // reaching a real process proves nothing about WHICH process. A stale/orphan
                    // jimmy-engine-host.exe left running from a prior session (crash without
                    // clean exit, or -- before the fail-closed bind fix -- a second instance that
                    // silently kept running hardware while a NEW child's own bind failed) can
                    // answer exactly like the real one. See ConnectDirectEngine's own comment for
                    // where _directExpectedSessionToken comes from. Treated exactly like a failed
                    // poll (DirectHandlePollFailure, no DirectApplyStatus/Decodes) when it
                    // doesn't match -- this snapshot's data is never trusted or acted on, and
                    // NegoState is never promoted to RECD, so nothing downstream can consider
                    // Direct connected on the strength of an unauthenticated response. Empty
                    // _directExpectedSessionToken (test mode / a caller that didn't request
                    // authentication -- see ConnectDirectEngine) skips this check entirely,
                    // matching prior behavior for those callers exactly.
                    if (!string.IsNullOrEmpty(_directExpectedSessionToken) &&
                        !string.Equals(snap.SessionToken, _directExpectedSessionToken, StringComparison.Ordinal))
                    {
                        if (!_directSessionAuthFailureAnnounced)
                        {
                            _directSessionAuthFailureAnnounced = true;
                            // Real-launch-failure root cause, 2026-08-24: a completely empty/
                            // absent sessionToken (and no pid, still its int default 0) is NOT
                            // the same evidence as a real, different, non-empty token -- the
                            // former is exactly what an EngineHost binary built BEFORE this
                            // feature existed reports (main.rs's own sessionToken/pid fields are
                            // injected into the SNAPSHOT JSON only by a build that has them at
                            // all; an older jimmy-engine-host.exe simply omits both, which
                            // deserializes to SessionToken=null/Pid=0, not to some OTHER real
                            // value). Confirmed live: a freshly-launched child (nothing else was
                            // running beforehand) hit this exact branch because the deployed
                            // jimmy-engine-host.exe on disk predated --session-token support --
                            // the ORIGINAL single message here ("close any stale jimmy-engine-
                            // host.exe process") is actively WRONG guidance in that situation
                            // (there is no stale process to close; the running one IS the one
                            // this session launched, it just doesn't speak this protocol yet).
                            // Only the genuinely-ambiguous case (a real, non-empty, merely
                            // DIFFERENT token -- meaning some other real process, with its own
                            // real launch/token, is answering) still gets that specific advice.
                            bool likelyOutdatedBinary = string.IsNullOrEmpty(snap.SessionToken) && snap.Pid == 0;
                            string detail = likelyOutdatedBinary
                                ? "the native engine (jimmy-engine-host.exe) did not report a session identity at all -- it is likely an outdated build from before this Jimmy version. Rebuild/redeploy jimmy-engine-host.exe."
                                : "the control port answered from an unexpected process, not the one this session launched -- not treated as connected. Close any stale jimmy-engine-host.exe process (Task Manager) and restart Jimmy.";
                            DebugOutput($"{Time()} [DIRECT] SNAPSHOT session token mismatch (expected a token from this session's own launch, got '{snap.SessionToken}', reported pid={snap.Pid}, likelyOutdatedBinary={likelyOutdatedBinary}) -- not treated as connected");
                            Notify?.Publish(new ErrorWarningEvent(ErrorSeverity.Error, "Native engine", detail));
                        }
                        _directAuthenticated = false;
                        DirectHandlePollFailure();
                        return;
                    }
                    _directSessionAuthFailureAnnounced = false;
                    if (!_directAuthenticated)
                    {
                        _directAuthenticated = true;
                        DebugOutput($"{Time()} [DIRECT] session authenticated (sessionToken matched, reported pid={snap.Pid})");
                    }

                    // First successful poll = "connected" as far as every OTHER piece of Jimmy's
                    // own status/UI code is concerned -- most of it gates on NegoState, not on
                    // anything specific to this class (found live, 2026-08-08, testing this for
                    // the first time: status kept announcing "Waiting for WSJT-X" on a one-second
                    // loop even while real decodes were actively populating the call queue,
                    // because nothing here had ever told NegoState we were done). Set once we've
                    // actually proven the engine host is reachable and answering -- not
                    // unconditionally in ConnectDirectEngine -- so a brief window before its
                    // control port comes up still shows as "not yet connected" rather than
                    // claiming success before it's true.
                    if (WsjtxMessage.NegoState != WsjtxMessage.NegoStates.RECD)
                    {
                        WsjtxMessage.NegoState = WsjtxMessage.NegoStates.RECD;
                        ctrl.initialConnFaultTimer?.Stop();
                        DebugOutput($"{Time()} [DIRECT] first snapshot received -- NegoState -> RECD");
                    }

                    DirectApplyStatus(snap);
                    DirectApplyDecodes(snap);
                }));
            });
        }

        // Companion to the failure-tracking fields declared above -- see their own comment.
        // Only actually announces once the failure streak crosses the threshold, and only once
        // per streak (guarded by _directLossAnnounced), matching the UDP path's own
        // HeartbeatNotRecd -- ResetNego()+CloseAllUdp() aren't called here since Direct mode has
        // no persistent socket of its own to reset; NegoState is set back to WAIT so
        // DirectPollTick's own "first snapshot received" promotion logic naturally re-fires
        // (and re-announces via existing status machinery) once polling recovers.
        private void DirectHandlePollFailure()
        {
            if (!_directConnected) return;
            _directConsecutivePollFailures++;
            if (_directLossAnnounced || _directConsecutivePollFailures < DirectPollFailureThreshold) return;

            _directLossAnnounced = true;
            DebugOutput($"{Time()} [DIRECT] {_directConsecutivePollFailures} consecutive SNAPSHOT poll failures -- treating as disconnected");
            if (WsjtxMessage.NegoState == WsjtxMessage.NegoStates.RECD)
            {
                Notify.Publish(new ConnectionLostEvent());
                Sounds.PlaySoundEvent(ctrl.soundEnabled_Disconnected, ctrl.soundFile_Disconnected);
            }
            WsjtxMessage.NegoState = WsjtxMessage.NegoStates.WAIT;
            // Engine is no longer trusted to be answering -- drop the cached TX level so F11/F12
            // and the Options spinner report "not available" rather than a stale number. The next
            // successful poll repopulates it from radio.TxLevel.
            _engineTxLevel = null;
        }

        // T16 fix, 2026-08-23 (CONFIRMED bug, CRITICAL -- W5PF, 2026-08-21): tracks which
        // call(s) CompleteQso (below) fully finished THIS poll tick, so ProcessDecodeMsg's own
        // "deCall is not call in progress" RR73 courtesy-reply branch (WsjtxClient.cs) does not
        // immediately re-admit the very call that just completed if that same station's RR73
        // decode is processed later in this identical tick (DirectApplyStatus always runs before
        // DirectApplyDecodes -- see DirectPollTick's own call order). Cleared at the top of every
        // DirectApplyStatus call -- deliberately scoped to ONE tick, not permanent: a station
        // that legitimately calls again in a LATER tick must still be admitted normally.
        private readonly HashSet<string> _completedThisPollTick = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void DirectApplyStatus(DirectSnapshot snap)
        {
            _completedThisPollTick.Clear();
            var radio = snap.Radio;
            if (radio == null) return;

            // Mirror the engine's live TX drive level so F11/F12 and the Options spinner always
            // step from / show what the engine actually has. Held while a SET_TX_LEVEL round-trip
            // is still open (_txLevelChangeInFlight) so an in-flight poll that predates the change
            // can't momentarily stomp the just-sent value back; a later poll reconciles it within
            // ~1s regardless.
            if (!_txLevelChangeInFlight) _engineTxLevel = radio.TxLevel;

            // "Radio CAT link lost"/recovered -- see _lastCatOk's own comment. null (not
            // applicable / not yet reported) never fires either branch, so a VOX-only station,
            // or the very first snapshot of a session, announces nothing.
            if (radio.CatOk.HasValue && _lastCatOk != radio.CatOk)
            {
                if (radio.CatOk.Value)
                {
                    // Only announce a RECOVERY, not the very first successful reading of a
                    // session (that's just normal startup, not news).
                    if (_lastCatOk == false)
                        Notify?.Publish(new RadioCatRecoveredEvent());
                }
                else
                {
                    Notify?.Publish(new ErrorWarningEvent(ErrorSeverity.Error, "Radio CAT link lost", radio.CatDetail ?? "no response"));
                }
                _lastCatOk = radio.CatOk;
            }

            // "Halt Tx when SWR > threshold" -- matches WSJT-X's own Radio-tab safety feature.
            // Used to run off RigctldClient's own periodic "l SWR" poll (Controller.cs's
            // ApplyRadioPollResult); moved here now that Jimmy no longer runs that second,
            // concurrent CAT poll loop -- radio.TxSwr is null on a rig/backend that doesn't
            // report SWR, treated the same way as "no reading, nothing to act on". Runs on every
            // Direct poll tick regardless of RadioControlMode (the engine's own Rig -- and
            // therefore its SWR reading -- exists independently of it; Jimmy no longer runs any
            // separate CAT session), so this now also protects operators under
            // RadioControlMode.WsjtxCat, which the old RigctldClient-based polling never could.
            bool swrOver = ctrl.Radio.HaltTxOnHighSwr && radio.TxSwr.HasValue
                           && radio.TxSwr.Value > ctrl.Radio.SwrHaltThreshold;
            if (swrOver && !_swrOverThreshold)
            {
                HaltTx();
                Notify?.Publish(new ErrorWarningEvent(ErrorSeverity.Error, "Tx halted (high SWR)",
                    $"SWR {radio.TxSwr.Value:F1} exceeds threshold {ctrl.Radio.SwrHaltThreshold:F1}"));
            }
            _swrOverThreshold = swrOver;

            // T17 fix, 2026-08-23: reconcile the rig's own CAT-reported mode readback against
            // what Jimmy last commanded -- see radio.RigMode's own comment for the full defect
            // this closes (previously never read/compared at all). Edge-triggered like CatOk/
            // swrOver above: warns once per mismatch episode, not every poll tick while stuck,
            // and clears silently the instant a later readback agrees again (no separate
            // "recovered" announcement -- unlike a lost CAT link, a corrected sideband isn't
            // itself news worth a second interruption).
            //
            // CAT mode command/readback correlation fix, 2026-08-23 (independent audit finding):
            // two refinements on top of the check above, both bounded and neither retrying/
            // re-sending any CAT command:
            //  1. SidebandMismatchGraceWindow -- a freshly confirmed retune's very next poll(s),
            //     arriving well inside normal relay-switching/CAT-mode-set propagation latency,
            //     must not be compared against a readback that simply hasn't caught up to this
            //     SAME retune yet (see _lastCommandedSidebandChangedUtc's own comment). Skipped
            //     entirely during the grace window -- not warned, not counted toward the
            //     reconcile streak below.
            //  2. Reconciliation -- a mismatch that survives SidebandReconcileAfterConsecutive
            //     Mismatches consecutive polls AFTER the grace window has already elapsed is no
            //     longer "still settling"; it is Jimmy's own belief that's stale (an operator
            //     manually changed the rig's mode, a rig that silently declined the commanded
            //     mode, etc). Reconciling ADOPTS the rig's own reported mode as the new
            //     "commanded" baseline -- stops warning forever, per the required behavior --
            //     but ONLY when the rig unambiguously reports USB or LSB; a rig reporting neither
            //     (some other mode entirely) is left as an open, still-announced mismatch rather
            //     than guessed at, per "do not guess about rig-specific USB/Data/PKTUSB
            //     behavior." Requiring several CONSECUTIVE agreeing polls (not one reading) is
            //     what keeps a single stale/transient readback from overwriting the newest
            //     requested state on its own.
            bool sidebandMismatch = RigModeMismatchesCommandedSideband(_lastCommandedSideband, radio.RigMode);
            bool withinTransitionGrace = _lastCommandedSidebandChangedUtc.HasValue &&
                (DateTime.UtcNow - _lastCommandedSidebandChangedUtc.Value) < SidebandMismatchGraceWindow;
            if (sidebandMismatch && withinTransitionGrace)
            {
                // Still within normal transition latency -- neither announce nor count yet.
            }
            else if (sidebandMismatch)
            {
                if (!_sidebandMismatchAnnounced)
                {
                    Notify?.Publish(new ErrorWarningEvent(ErrorSeverity.Warning, "Radio CAT mode mismatch",
                        $"commanded {_lastCommandedSideband}, radio reports '{radio.RigMode}' -- check Options > Frequencies and the rig's own mode/menu settings"));
                }
                _sidebandMismatchAnnounced = true;
                _sidebandMismatchStreak++;
                bool rigReportsLsb = radio.RigMode.IndexOf("LSB", StringComparison.OrdinalIgnoreCase) >= 0;
                bool rigReportsUsb = !rigReportsLsb && radio.RigMode.IndexOf("USB", StringComparison.OrdinalIgnoreCase) >= 0;
                if (_sidebandMismatchStreak >= SidebandReconcileAfterConsecutiveMismatches && (rigReportsLsb || rigReportsUsb))
                {
                    string reconciledSideband = rigReportsLsb ? "LSB" : "USB";
                    DebugOutput($"{Time()} [DIRECT] CAT mode mismatch persisted {_sidebandMismatchStreak} polls past the transition grace window -- reconciling commanded sideband from '{_lastCommandedSideband}' to the rig's own reported '{reconciledSideband}'");
                    Notify?.Publish(new ErrorWarningEvent(ErrorSeverity.Warning, "Radio CAT mode reconciled",
                        $"the rig has been reporting {reconciledSideband} instead of the commanded {_lastCommandedSideband} -- Jimmy now treats {reconciledSideband} as current; use Options > Frequencies if this isn't what you intended"));
                    _lastCommandedSideband = reconciledSideband;
                    _lastCommandedSidebandChangedUtc = DateTime.UtcNow;
                    _sidebandMismatchStreak = 0;
                    _sidebandMismatchAnnounced = false;
                }
            }
            else
            {
                _sidebandMismatchAnnounced = false;
                _sidebandMismatchStreak = 0;
            }

            // FreqToBandStr (which CurrentBandStr and this method's own band-change detection
            // below both depend on) refuses to resolve any band at all unless the class-level
            // "mode" field -- not StatusMessage.Mode below, a separate field -- is a real key
            // in freqsDict (WsjtxClient.BandAudio.cs's own FreqToBandStr: "!freqsDict.Keys.Contains(mode)
            // ... return null"). The UDP path sets it during negotiation and on every status
            // update; direct mode never did, so it stayed at its "" default forever, and
            // FreqToBandStr kept returning null even after fixing dialFrequency below --
            // CurrentBandStr was still "unknown band" the whole time.
            //
            // No longer hardcoded to "FT8" here (2026-08-09): the engine has no server-side
            // read-back of which tier is actually selected (AppSnapshot exposes no top-level
            // current-tier field -- LinkState.tier is the unrelated Tempo chat-link tier), so
            // this.mode is tracked optimistically, the same way rit_hz/xit_hz/active_vfo etc.
            // are on the Rust side: set once to "FT8" the first time this runs (matching the
            // engine's own startup default; ConnectDirectEngine also sets it eagerly for the
            // normal live-poll path, but TestApplyDirectSnapshot deliberately bypasses that, so
            // this lazy fallback covers both) and only ever changed after that by
            // SetOperatingMode/DirectSetTier when the operator actually commands a switch.
            // Stomping it back to "FT8" every single poll tick here would silently undo that the
            // instant Alt+M picked FT4.
            if (string.IsNullOrEmpty(this.mode)) this.mode = "FT8";
            var smsg = new StatusMessage
            {
                DialFrequency = (ulong)Math.Round(radio.DialMhz * 1_000_000.0),
                Mode = this.mode,
                TransmitMode = this.mode,
                Transmitting = radio.Transmitting,
                Decoding = false, // no direct equivalent in AppSnapshot; UpdateTrPeriod/ShowStatus don't depend on this being exact
                TRPeriod = null,  // null -> UpdateTrPeriod's own mode-based fallback resolves this from smsg.Mode (FT8 15000ms / FT4 7500ms)
                TxFirst = (radio.Slot % 2) == 0, // approximation -- see this method's own doc comment
                DxCall = "",
                DeGrid = "",
            };

            // Mirrors the classification/awards-relevant slice of the UDP path's own ongoing
            // StatusMessage handler (WsjtxClient.Protocol.cs, NegoState==RECD branch): dial
            // frequency tracking and what a confirmed band change resets. Found live,
            // 2026-08-09: without assigning dialFrequency here, it stayed 0 for the entire
            // session, so CurrentBandStr (which reads dialFrequency, not smsg.DialFrequency)
            // was permanently "unknown band" -- and Classify()'s own documented "can't
            // classify, don't guess" convention makes an unknown band always count as new, so
            // every decode, USA included, showed up tagged New DXCC / New DXCC on band
            // regardless of real log history.
            //
            // Deliberately not a full port of that handler: the rest of it (decode-cycle
            // timing via processDecodeTimer/postDecodeTimer, TX start/end state machine,
            // dxCall/txMsg tracking editable only from a real WSJT-X UI) is either meaningless
            // here -- direct mode gets decodes straight from snap.RecentDecodes every poll, it
            // never needs to infer decode timing from a toggling flag -- or TX/QSO-completion
            // tracking, which this method handles its own way below (see the curTxMsg/txMsg
            // block's own comment).
            ulong newDialFrequency = smsg.DialFrequency;
            if (lastDialFrequency != null &&
                Math.Abs((float)lastDialFrequency - (float)newDialFrequency) > freqChangeThreshold &&
                FreqToBandIdx(newDialFrequency / 1e6) != FreqToBandIdx(lastDialFrequency / 1e6))
            {
                DebugOutput($"{nl}{Time()} [DIRECT] band changed:'{FreqToBandStr(newDialFrequency / 1e6)}' (was:'{FreqToBandStr(lastDialFrequency / 1e6)}')");
                newBand = true;
                _rawDecodeHistory.Clear();
                if (ctrl.advShowRaw) ShowRawDecodes();
                // The canonical trio -- a confirmed band change makes the whole band context new:
                //   ClearCalls()      -- the call queue + decode-cycle scratch
                //   EndContact(ContextReset) -- callInProg AND last-command / last-Tx-text fields
                //     (T16/T19: old-band "73" text must not leak onto the new band); its
                //     _contactEpoch bump also invalidates a REPLY/CALL_CQ still in flight for the
                //     band just left, even if callInProg was still null when the change landed.
                //   ResetBandSession() -- decode history, sent/report lists, logList, grid cache
                ClearCalls();
                EndContact(ContactEndReason.ContextReset);
                ResetBandSession();
                // dialFrequency must be updated to the new band BEFORE LoadHrcCache()/
                // RefreshStillNeedCache() run: both rebuild their per-band live-tag caches off
                // wsjtxClient.CurrentBandStr, which is derived from dialFrequency. Assigning it
                // only after this block (as the code below still does, harmlessly) left both
                // caches rebuilt against the band just LEFT -- so after e.g. 160m -> 40m, a
                // WAS_160M-type per-band award stayed live and every 40m decode from a
                // still-needed state got tagged "Worked All States - 160m Needed" until the
                // next band change (which then repeated the mistake one band later).
                dialFrequency = newDialFrequency;
                lastDialFrequency = dialFrequency;
                ctrl.LoadHrcCache();
                ctrl.RefreshStillNeedCache();
                StatusView.ShowMessage($"Band changed to {FreqToBandStr(newDialFrequency / 1e6)}", false);
            }
            dialFrequency = newDialFrequency;
            lastDialFrequency = dialFrequency;

            // Added 2026-08-10: mirrors the UDP path's own bandIdx assignment (WsjtxClient.
            // Protocol.cs) -- without this, bandIdx (what ShowStatus()'s OpModes.START case
            // actually reads for the "X band selected" startup announcement, NOT dialFrequency/
            // FreqToBandStr) stayed permanently null in Direct mode regardless of the real dial
            // frequency, so startup always announced "Unknown band selected" no matter what.
            bandIdx = FreqToBandIdx(newDialFrequency / 1e6);
            // Found 2026-08-17 investigating a "radio clicks twice on a band-change hotkey"
            // report: the UDP path clears _pendingBandIdx the moment a real confirmed bandIdx
            // arrives (WsjtxClient.Protocol.cs, "real confirmation arrived -- drop any
            // optimistic guess"), but that mirroring was never carried over here when bandIdx's
            // own assignment was added above -- Direct mode (the ONLY production transport) has
            // been leaving _pendingBandIdx set forever after the first band change. BandUp/
            // BandDown prefer _pendingBandIdx over the real bandIdx (see its own field comment
            // in WsjtxClient.cs -- intentional, so repeated presses before a CAT round-trip
            // lands keep advancing), so a stale pending value that has drifted from the real
            // confirmed band computes the WRONG target on the next press. Does not by itself
            // explain two commands from one press (that would need a second call site actually
            // sending a second SetFrequency -- see RigctldClient's own new [RIG-CMD] logging for
            // that), but it is a real correctness bug on its own and worth fixing regardless.
            _pendingBandIdx = null;

            // Options > Radio "Remember F11/F12 audio level per band" -- only on a genuine
            // confirmed band change (newBand, set just above), not every poll tick. See
            // RestoreTxLevelForBand's own comment (WsjtxClient.BandAudio.cs) -- shared with the
            // classic WSJT-X/UDP path.
            if (newBand) RestoreTxLevelForBand();

            // One-time-per-connection startup fallback, requested live 2026-08-10: if the very
            // first band resolution attempt this session still comes back unknown (no CAT data
            // has arrived yet, or WsjtxCat mode has no CAT to read at all), pick a known band and
            // actively move the radio there instead of leaving the operator stuck on "Unknown
            // band" -- mirrors the UDP path's own InitialConnect fallback (WsjtxClient.
            // Protocol.cs: defaults to 20m/index 5), plus the new "restore last band used" ask,
            // which the UDP path never had either. Gated on _directStartupBandResolved so this
            // never fires again later in the same connection if the band transiently reads as
            // unknown again (e.g. a momentary CAT hiccup) -- only the first attempt counts.
            if (!_directStartupBandResolved)
            {
                _directStartupBandResolved = true;

                // Release-audit finding, 2026-08-20 (startup dial/tier restore): restore the
                // last CONFIRMED tier before touching frequency at all -- jimmy-engine-host
                // always starts a fresh session hardcoded to Tier::Ft8 (main.rs's own startup
                // set_tier call; see this.mode's own comment above), so a prior FT4 session
                // silently reverted to FT8 on every restart with no correction. Only fires once
                // (guarded by _directStartupBandResolved same as the frequency fallback below)
                // and only when a real prior tier was actually confirmed and it differs from
                // the engine's own current default -- an unconfirmed LastTier (fresh install)
                // leaves the engine on its already-sane FT8 default rather than guessing.
                //
                // Release-audit finding, 2026-08-21: DirectSetTier used to be called
                // synchronously here, on the UI thread. Backgrounded now, same as every other
                // Direct command in this pass -- but unlike RetuneBand/ToggleTuningProcess/
                // SetOperatingMode, this one call site genuinely needs its own completion
                // SEQUENCED before the frequency-fallback decision below: RetuneBand's own
                // two-arg overload resolves a fallback band's frequency via bandToFreq(idx),
                // which reads freqsDict[mode] -- if the frequency fallback ran before the tier
                // restore's own network round-trip actually landed, it could compute the WRONG
                // tier's calling frequency (FT8's when FT4 was just requested, or vice versa).
                // ApplyStartupBandFallback is therefore called explicitly, once, either
                // immediately (no tier restore needed) or from the tier restore's own
                // BeginInvoke continuation (once it's actually landed) -- never both, never
                // neither. epoch mirrors DirectPollTick's own reconnect guard: if the engine
                // host crashes/restarts (ConnectDirectEngine bumps _directConnectionEpoch and
                // resets _directStartupBandResolved) while this tier restore is still in
                // flight, its now-stale completion must not touch `mode` or retune anything on
                // behalf of a connection that no longer exists.
                int epoch = _directConnectionEpoch;

                // Codex Audit 02 release blocker, 2026-08-21 (FT4/exact-frequency startup restore
                // race): this same DirectApplyStatus invocation's own "persist confirmed snapshot"
                // block further down (see its own comment) unconditionally overwrites
                // ctrl.Radio.LastDialFrequencyHz/LastBandIdx with THIS poll's live values -- the
                // actual current radio dial (wherever it happened to power up, not yet retuned) --
                // and it runs SYNCHRONOUSLY, later in this exact same method call, regardless of
                // whether the tier restore below is still in flight. Root-caused live:
                // ApplyStartupBandFallback used to read ctrl.Radio.LastDialFrequencyHz/LastBandIdx
                // directly, but by the time its continuation actually ran (after the persist block
                // below had already run, in this same synchronous call), those fields no longer
                // held the prior-session values they needed -- they held whatever this poll just
                // wrote. Capturing both here, before the persist block runs, is the fix:
                // ApplyStartupBandFallback below reads these captured locals instead of the live
                // (by-then-overwritten) settings fields.
                double capturedLastDialHz = ctrl.Radio.LastDialFrequencyHz;
                int capturedLastBandIdx = ctrl.Radio.LastBandIdx;

                void ApplyStartupBandFallback()
                {
                    // Release-audit finding, 2026-08-21 (operator directive, unconditional --
                    // "always force it, no setting"): ALWAYS restore the operator's own last
                    // CONFIRMED exact FT8/FT4 dial on startup once one is known, even when the
                    // radio is already on a recognized ham band -- e.g. sitting on a CW/phone
                    // frequency for another purpose, or simply not exactly where the prior
                    // session left it. This is a deliberate broadening of the original,
                    // narrower 2026-08-20 fallback (which only ever corrected a totally
                    // UNRECOGNIZED band and otherwise left a recognized-but-different frequency
                    // alone) -- the operator explicitly chose exact-frequency session
                    // continuity over leaving the radio wherever it happened to power up.
                    // RetuneBand is idempotent (Nexus's own Engine::set_frequency re-confirming
                    // an already-correct dial is a harmless no-op), so this fires unconditionally
                    // whenever real prior-session data exists, without checking bandIdx first.
                    if (capturedLastDialHz > 0)
                    {
                        int? lastBandForDial = FreqToBandIdx(capturedLastDialHz / 1e6);
                        if (lastBandForDial != null)
                        {
                            // T18 fix, 2026-08-23: bandToSideband(...), not this overload's own
                            // "USB" default -- without this, startup's exact-dial restore alone
                            // (of every RetuneBand call site) ignored a configured non-default
                            // sideband for the restored band/mode.
                            RetuneBand(lastBandForDial.Value, (uint)capturedLastDialHz, "DirectInitialConnect", bandToSideband(lastBandForDial.Value));
                            return;
                        }
                        // A persisted dial that no longer resolves to any recognized band (e.g. a
                        // stale value left over from a since-changed band-plan/frequency-table
                        // configuration) falls through to the "no usable prior data" path below,
                        // same as if LastDialFrequencyHz had never been confirmed at all.
                    }

                    // No usable prior-session dial -- fresh install, or a stale/unresolvable one.
                    // Falls back to the original, narrower behavior: only correct a totally
                    // unrecognized band (leaves an already-recognized band/frequency alone, since
                    // there's no real prior-session data to prefer over it).
                    if (bandIdx == null)
                    {
                        int fallbackIdx = (capturedLastBandIdx >= 0 && capturedLastBandIdx < bands.Count)
                            ? capturedLastBandIdx
                            : 5; // 20m -- matches the UDP path's own InitialConnect default
                        // RetuneBand is itself void/backgrounded (2026-08-21) -- it owns showing
                        // its own pending-status text or failure notification once the retune
                        // actually completes; see its own comment.
                        RetuneBand(fallbackIdx, "DirectInitialConnect");
                    }
                }

                if (!string.IsNullOrEmpty(ctrl.Radio.LastTier) && ctrl.Radio.LastTier != this.mode)
                {
                    string targetTier = ctrl.Radio.LastTier;
                    // Codex Audit 02 follow-up, 2026-08-21: DirectSetTier now routes through the
                    // ordered dispatcher (WsjtxClient.Direct.cs's own class comment, near the top of
                    // this file) instead of this call site opening its own independent Task.Run --
                    // the dispatcher already marshals onComplete onto the UI thread, same as
                    // ctrl.BeginInvoke did here before.
                    DirectSetTier(targetTier, ok =>
                    {
                        if (epoch != _directConnectionEpoch) return; // superseded by a reconnect

                        if (ok)
                        {
                            this.mode = targetTier;
                            // Startup/restart mode-sync fix, 2026-08-24 (independent audit
                            // finding, CONFIRMED live): the engine and Jimmy's own tracked `mode`
                            // both correct to the restored tier here, genuinely fast (confirmed
                            // live: ~5ms after the first snapshot) -- but nothing here previously
                            // told the UI/announced status about it. The FIRST status render this
                            // session had already used `mode`'s earlier optimistic "FT8" default
                            // (DirectApplyStatus's own "if (string.IsNullOrEmpty(this.mode))
                            // this.mode = 'FT8'" lazy fallback, which always runs before this
                            // restore lands) and rendered plain text with no mode name in it at
                            // all -- so the operator was shown/told nothing about EITHER mode,
                            // and only found out the real (already-correct) mode later, whenever
                            // some unrelated status render happened to include it (this method's
                            // own header comment's "5ms" gap is misleadingly small: the STATUS
                            // TEXT itself doesn't refresh again until the next unrelated render,
                            // which can be much later). newMode=true is the exact same flag
                            // SetOperatingMode's own callback (Alt+M, an operator-driven switch)
                            // already sets, and WsjtxClient.Display.cs's own ShowStatus reads it
                            // to prefix the very next render with "{mode} mode, ..." -- making a
                            // startup RESTORE announce itself exactly the same way a live SWITCH
                            // already does, on the next poll tick rather than silently. UpdateRR73
                            // is the other piece of UI that reads `mode` directly (the "Use RR73"
                            // checkbox, FT4-only) -- ResetNego calls it once at connection start,
                            // before this restore has landed, so it also needs a fresh call here
                            // for the same reason.
                            newMode = true;
                            UpdateRR73();
                            // Independent audit finding, 2026-08-24 (the REAL root cause behind
                            // the visible symptom above -- confirmed by direct log/timing math,
                            // not just the display gap): trPeriod (WsjtxClient.cs) is computed
                            // exactly ONCE per connection and then left alone, guarded on
                            // "trPeriod == null" (UpdateTrPeriod, WsjtxClient.Protocol.cs) --
                            // DirectApplyStatus's own FIRST poll this session already computed it
                            // from the SAME stale optimistic "FT8" default (same lazy-fallback
                            // race as `mode` above, see that comment), and with trPeriod no
                            // longer null, nothing ever re-derives it again for the rest of the
                            // session. trPeriod is NOT cosmetic -- it directly drives even/odd
                            // period-parity math (WsjtxClient.cs's own IsEvenPeriod, "(secPastHour
                            // / (trPeriod / 1000)) % 2 == 0") and call-queue age expiry
                            // (CallQueueStore.cs), so a stale 15000ms (FT8) value under a real
                            // FT4 session would silently mis-time period-boundary/parity decisions
                            // and queue expiry for the ENTIRE REST OF THE SESSION -- a genuine
                            // functional bug, not just a wrong label. Reset to null here so
                            // UpdateTrPeriod's own guard fires again and re-derives it correctly
                            // from the NOW-corrected mode -- the exact same reset
                            // SetOperatingMode's own live-switch callback already does (see its
                            // own comment, WsjtxClient.Protocol.cs) for an operator-driven switch;
                            // this is that same fix's missing startup-restore counterpart.
                            trPeriod = null;
                            DebugOutput($"{Time()} [DIRECT] DirectInitialConnect: restored tier '{this.mode}'");
                            // Retest finding, 2026-08-24: newMode=true alone only primes the
                            // NEXT unrelated ShowStatus() render to include the corrected mode --
                            // it doesn't force one. Live retest showed that render can be many
                            // seconds away (or never come before the operator acts), so the
                            // operator heard/saw the stale pre-restore text, believed the restore
                            // hadn't happened, and pressed Alt+M themselves -- which actually
                            // TOGGLED the already-correct restored tier back to the wrong one.
                            // SetOperatingMode's own live-switch callback (WsjtxClient.Protocol.cs)
                            // already calls ShowStatus() directly instead of waiting; do the same
                            // here so the restore is announced immediately, not passively.
                            ShowStatus();
                        }
                        ApplyStartupBandFallback();
                    });
                }
                else
                {
                    ApplyStartupBandFallback();
                }
            }

            // Persist whenever a real band is confirmed, so the NEXT session can restore it --
            // cheap in-memory field write on every tick a real band is known; actual disk
            // persistence only happens once, on clean shutdown, via Controller_FormClosing's
            // existing Radio.SaveToIni call.
            if (bandIdx != null)
            {
                ctrl.Radio.LastBandIdx = (int)bandIdx;
                // Release-audit finding, 2026-08-20 (startup dial/tier restore): LastBandIdx
                // alone only remembers WHICH band, not the exact confirmed dial or which
                // FT8/FT4 tier was active -- see RadioSettings.LastDialFrequencyHz/LastTier's
                // own comment and this method's startup-restore block below for where these get
                // applied on the NEXT connection.
                ctrl.Radio.LastDialFrequencyHz = newDialFrequency;
                if (!string.IsNullOrEmpty(this.mode)) ctrl.Radio.LastTier = this.mode;
            }

            // Root-caused live, 2026-08-09: this method built smsg.Transmitting from the radio
            // but never actually copied it into the class-level `transmitting` field ShowStatus()
            // reads (that field was only ever set by the UDP path's own status handler in
            // WsjtxClient.Protocol.cs) -- so direct-engine mode's status display never learned a
            // transmission was underway and always announced "Receiving", no matter what the
            // radio was actually doing.
            bool wasTransmitting = transmitting;
            transmitting = radio.Transmitting;
            bool transmittingChanged = wasTransmitting != transmitting;
            // Direct mode's own real transmitting-flag transition -- edge-triggered (only on an
            // actual change), matching the UDP path's ProcessTxStart/ProcessTxEnd exactly. See
            // NotificationCenter.OnTransmittingChanged's own comment.
            if (transmittingChanged) Notify?.OnTransmittingChanged(transmitting);

            // Rx/Tx frequency control, 2026-08-27: the old mid-QSO "too many consecutive
            // transmits without being heard -> disable Tx and re-pick the best free frequency"
            // behavior is removed. It moved the operator's transmit slot out from under an
            // in-progress contact, which the frequency-control design explicitly rules out
            // ("Keep Tx stable during an active QSO"). Automatic best-frequency selection now
            // happens ONLY at the start of a CQ or a reply, never mid-QSO. consecTxCount is
            // still maintained (debug/status display) but drives no automatic action.
            if (wasTransmitting && !transmitting)
            {
                if (ctrl.freqCheckBox.Checked)
                    consecTxCount++;
                else
                    consecTxCount = 0;
            }

            // Found live (release blocker follow-up, 2026-08-19): Jimmy's own `txEnabled` field
            // was NEVER reconciled from the engine's real state in Direct mode -- only ever
            // written locally by EnableTx()/DisableTx() at the moment JIMMY commands a change.
            // The engine can disable its own tx_enabled independently (its own QSO-sequencer/
            // retry logic, confirmed live: no HALT_TX was ever sent, yet a live SNAPSHOT query
            // showed the engine's real txEnabled already false while Jimmy's own field still
            // read true). That silent drift is what made DiscardCall()'s "give up" check log
            // "not in effect" and leave callInProg stuck forever -- see DirectRadioStatus.
            // TxEnabled's own comment for the full chain. Reconciled here, same level-triggered
            // pattern as transmitting/tuning above (DirectApplyDecodes' own discard check runs
            // immediately after this method in the same poll tick, so this is always fresh by
            // the time that check reads it).
            txEnabled = radio.TxEnabled;

            // Queue-age expiry (TrimCallQueue) used to live here too, alongside the retry-limit/
            // discard-give-up counter below -- both moved to DirectApplyDecodes' own new-slot
            // detection together, 2026-08-11. Root-caused live: "transmitting just started" can
            // never fire while just monitoring with nothing armed (by definition nothing is
            // transmitting then), so queue-age expiry silently never ran at all while listening
            // -- confirmed via a full session's debug log showing txMode:LISTEN throughout with
            // callQueue.Count only ever growing. TrimCallQueue itself is unrelated to any one
            // call's own attempt count, so it's still correctly driven by DirectApplyDecodes'
            // own new-slot trigger (a genuine per-period decode-cycle boundary, true whether or
            // not that period transmits -- see that method's own comment).
            //
            // Repeat-limit timing fix, 2026-08-24 (independent audit finding, CONFIRMED live --
            // KF4TST, Repeat Limit 3: attempts 1-3 transmitted normally, but on what would be
            // attempt 4 the radio ACTUALLY KEYED UP for about a second before Jimmy's own halt
            // landed). The discard-give-up counter got swept into that SAME 2026-08-11 move
            // alongside TrimCallQueue, but it needed the opposite fix: DirectApplyDecodes' own
            // new-slot trigger fires from a fresh decode becoming visible, which happens at
            // essentially the SAME moment the engine has already autonomously re-armed and
            // started transmitting the NEXT period (the engine's own QSO-continuation logic runs
            // on its own real-time clock, independent of Jimmy's poll) -- so by the time that
            // check ran, the disallowed attempt had already started keying the radio. Reactive,
            // not proactive. Triggering on "transmitting just ENDED" instead (the SAME
            // wasTransmitting/transmitting edge consecTxCount above already uses) checks the
            // count during the FOLLOWING receive period -- roughly one full period (7.5s FT4 /
            // 15s FT8) of lead time before the next period's TX would begin -- so
            // DirectSetTxEnabled(false) (inside DiscardCall(), via wasTxEnabled below) reaches
            // the engine well before it would otherwise re-key, not after. Placed after the
            // txEnabled reconciliation just above (not up with consecTxCount near the top of this
            // method) so DiscardCall()'s own "wasTxEnabled" capture reads this poll's fresh,
            // reconciled value, not a stale pre-poll one.
            if (wasTransmitting && !transmitting && discardCall != null && discardCall == callInProg
                && ++discardCallCycleCount >= maxTxRepeat)
                DiscardCall();

            // Mirrors the transmitting assignment above -- same root cause, same fix: without
            // this, the class-level `tuning` field (AudioLevel()'s own guard, Alt+T's status
            // text) never learned a real Tune (SET_TUNING) was underway in Direct mode.
            tuning = radio.Tuning;

            // Direct-mode runaway-Tx backstop, 2026-08-28 (CONFIRMED live -- HB9TIH then NE5L in
            // one session). After Jimmy logs a contact and clears callInProg on seeing the
            // engine's own RR73 (DirectApplyDecodes' Is73orRR73 branch), the engine's QSO
            // sequencer can keep re-sending that RR73 every transmit slot -- if the worked
            // station never cleanly hears it and keeps repeating its R-report, or after an
            // engine restart mid-QSO. Jimmy showed "Transmitting, sending R R 7 3" with no call
            // in progress and only stopped after the operator mashed Escape ~8 times (~2 minutes
            // of unsolicited overs on the air). Nothing here caught it: the mid-QSO
            // consecTxCount/best-frequency safety net that used to live at this spot was removed
            // in the 2.0.42 frequency work, and it wouldn't have applied to this "no callInProg
            // at all" case anyway. Count completed orphaned overs on the transmitting-ended
            // edge: the FIRST is tolerated (it can be a legitimate final RR73 finishing in the
            // poll or two after callInProg was cleared), the SECOND means the engine re-keyed on
            // its own -> halt it exactly as Escape does (HALT_TX + SET_TX_ENABLED 0) and tell
            // the operator. Unconditional -- not gated on ctrl.freqCheckBox or any mode flag
            // beyond "LISTEN, no contact, not tuning": there is simply no reason to be
            // transmitting in that state.
            if (callInProg != null || txMode != TxModes.LISTEN || tuning)
            {
                _directOrphanTxOvers = 0;
            }
            else if (wasTransmitting && !transmitting)
            {
                if (++_directOrphanTxOvers >= 2)
                {
                    DebugOutput($"{Time()} [DIRECT] runaway-Tx backstop: {_directOrphanTxOvers} orphaned overs with no callInProg in LISTEN mode -- halting");
                    HaltAndDisableTx();
                    Notify?.Publish(new ErrorWarningEvent(ErrorSeverity.Error, "Transmit stopped",
                        "the radio was transmitting with no contact in progress"));
                    _directOrphanTxOvers = 0;
                }
            }

            // Rx/Tx audio offsets: read back the engine's live values every poll so the
            // accessible frequency controls (Announce Rx/Tx, Tx<->Rx exchange, Set Tx
            // frequency) always report and act on what the engine is actually doing, not on a
            // stale value Jimmy last commanded. Jimmy remains the only thing that MOVES them
            // (DirectSetTxOffset / DirectSetRxOffset). rxOffset/txOffset are 0 until the first
            // snapshot arrives -- callers treat 0 as "unknown", same as dialFrequency.
            if (radio.RxOffsetHz > 0) rxOffset = (uint)Math.Round(radio.RxOffsetHz);
            if (radio.TxOffsetHz > 0) txOffset = (uint)Math.Round(radio.TxOffsetHz);
            engineHoldTxFreq = radio.HoldTxFreq;

            UpdateTrPeriod(smsg);

            // Added 2026-08-10: Direct mode's own equivalent of the UDP path's ProcessTxStart/
            // ProcessTxEnd (WsjtxClient.Protocol.cs) -- neither of those ever runs here, so
            // without this, curTxMsg/txMsg (drives the "sending X" status text) and QSO
            // completion/logging (LogQso, gated on txMsg being 73/RR73) were both permanently
            // dead in Direct mode -- root-caused live, 2026-08-10, from a real QSO that never
            // logged. Uses the engine's own qso.txNow (the real, authoritative "what am I
            // actually sending") rather than trying to reconstruct it from anything Jimmy itself
            // commanded: Jimmy only ever tells the engine WHO to reply to (DirectSendReply), the
            // engine's own QSO sequencer decides the exact on-air text. Deliberately
            // level-triggered (checked every poll, not edge-triggered off transmittingChanged
            // above): tx_now can already have advanced to the next step's text by the time a
            // transmitting-just-ended transition is observed on a 1s poll, so re-checking every
            // tick this method runs is what reliably catches it.
            //
            // The LogQso() call below still needs its OWN explicit "already logged" guard even
            // though ClaimLiveLoggedQso already dedupes the actual database write -- confirmed
            // live, 2026-08-10: with no guard, a real QSO with K7F triggered "Logged QSO with
            // K7F" three times, one per poll tick, because tx_now keeps reporting the same sent
            // 73 text for several seconds (as long as the engine is still on that QSO step), not
            // just for the one tick the transmission ended on. RequestLog's sound/notify/
            // logList.Add side effects are unconditional -- only the ADIF/DB write itself is
            // gated by ClaimLiveLoggedQso -- so without this guard the database stayed correct
            // (one entry) but the operator heard three duplicate "Logged" dings for one QSO.
            // Root-caused live, 2026-08-11: the engine reports Qso.TxNow == null as soon as ITS
            // OWN sequencer considers the QSO done -- which happens before the final 73 has
            // actually finished transmitting over the air. Clearing curTxMsg to null right then
            // (the old unconditional assignment below) wiped out the "sending 73" text before
            // the ShowStatus() render for the real, physical transmitting=true tick ever got a
            // chance to show it -- the operator heard "Transmitting..." with no payload at all
            // for the QSO's actual final 73. Only ever overwrite curTxMsg with a REAL message;
            // once the engine goes quiet, just keep showing whatever it last said until a new
            // callInProg gives it something new to report.
            // Normalize the engine's TxNow the SAME way incoming decodes are normalized
            // (WsjtxMessage.NormalizeDecodedMessage) so transmitted and received Direct text
            // are consistent. In practice TxNow only ever needs the hashed-call unwrap here:
            // "<W1AW/2> KB0UZT 73" -> "W1AW/2 KB0UZT 73". Left bracketed, every WsjtxMessage
            // parser used just below -- ToCall, Is73orRR73, IsReport, IsRogerReport, IsRogers --
            // and Payload (Display) bail on IsInvalid() (true for any '<'/'>'), so ToCall(curTxMsg)
            // never equals callInProg (stored bracket-free) and the whole completion block below
            // is skipped: the compound-call QSO is never logged, callInProg wedges until a band
            // change / Halt / restart, and the operator hears "sending ." with no payload.
            // CONFIRMED live 2026-08-30 (W1AW/2 stalled ~3 min, unlogged).
            string newTxMsg = WsjtxMessage.NormalizeDecodedMessage(snap.Qso?.TxNow);
            if (!string.IsNullOrEmpty(newTxMsg) && newTxMsg != curTxMsg)
            {
                curTxMsg = newTxMsg;
                txMsg = newTxMsg;
                curTxPayload = null;
            }
            if (!string.IsNullOrEmpty(curTxMsg) && callInProg != null && WsjtxMessage.ToCall(curTxMsg) == callInProg)
            {
                if ((WsjtxMessage.IsReport(curTxMsg) || WsjtxMessage.IsRogerReport(curTxMsg)) && !sentReportList.Contains(callInProg))
                    sentReportList.Add(callInProg);
                if (WsjtxMessage.Is73orRR73(curTxMsg))
                {
                    // Mirrors the UDP path's ProcessTxEnd (WsjtxClient.cs, Is73orRR73(txMsg)
                    // branch): once the final 73/RR73 to callInProg is on its way, the QSO is
                    // over and callInProg must be cleared. Without this, Direct mode never
                    // cleared it at all -- root-caused live, 2026-08-11, from a real session
                    // where ShowStatus() kept re-announcing one already-logged QSO's stale info
                    // ("previous RR73") for 19+ minutes afterward instead of returning to normal
                    // "N available stations" status, because callsWaiting is only computed when
                    // callInProg == null (WsjtxClient.Display.cs).
                    if (!logList.Contains(callInProg))
                        LogQso(callInProg);

                    // Finding 2, 2026-08-28 (read-only audit): only tear the QSO down if the
                    // local logbook write actually landed. RequestLog adds to logList only on a
                    // successful (or already-committed) write; a failed write leaves logList
                    // clean, releases the dedup claim, and sets _liveLogWriteFailedCall. Keep
                    // callInProg set in that case so this branch re-enters on the next poll
                    // (curTxMsg is still the 73/RR73) and the write is retried, instead of
                    // clearing callInProg and leaving the contact permanently unlogged with no
                    // path back. "Nothing to log at all" (no report on record -- a partial
                    // exchange) is NOT a failed write: fall through and tear down as before so a
                    // broken exchange can't wedge callInProg forever.
                    bool onRecord = logList.Contains(callInProg);
                    // Write-fail retry, now BOUNDED (audit finding 2026-08-30 -- see
                    // _directWriteFailRetries' own comment). ++ only reached once the earlier
                    // !onRecord / same-failed-call guards pass, so it doesn't tick on a healthy
                    // completion or for some other call.
                    bool writeFailedForThisCall = !onRecord && _liveLogWriteFailedCall == callInProg;
                    bool writeFailedRetry = writeFailedForThisCall
                        && ++_directWriteFailRetries <= MaxDirectWriteFailRetries;
                    // KF4CCG race, 2026-08-29 (CONFIRMED live -- see _directRr73LogRetries's own
                    // comment): the engine's Qso.TxNow flips to the final RR73 in the SAME ~1s poll
                    // that first decodes the DX's roger-report, and DirectApplyStatus runs BEFORE
                    // DirectApplyDecodes ingests that decode -- so LogQso() just above found no
                    // report from the DX on record yet and silently no-op'd. Before this the QSO
                    // was still torn down here: it then went unlogged until CheckLateLog rescued it
                    // ~60s later on the DX's literal 73, the engine kept re-sending an orphaned
                    // RR73 in between (the runaway backstop had to halt it), and the roger-report
                    // was never announced (no callInProg to attach it to). Hold callInProg one more
                    // poll whenever WE have sent a report (a real, completable QSO) and it still is
                    // not on record: the roger lands later in this same tick, so the next poll's
                    // LogQso() succeeds and the "logged" cue fires at the right moment. Bounded by
                    // MaxDirectRr73LogRetries so a genuinely one-sided exchange still tears down;
                    // CheckLateLog stays the final net for a real trailing 73/RR73.
                    // Gated on "no write failure recorded for this call" (not on !writeFailedRetry,
                    // which flips back to false once the write-fail budget is spent) so the two
                    // holds stay mutually exclusive by construction -- this one is only ever the
                    // decode-ingest race, never a disk problem.
                    bool awaitingRogerDecode = !onRecord && _liveLogWriteFailedCall != callInProg
                        && sentReportList.Contains(callInProg)
                        && ++_directRr73LogRetries <= MaxDirectRr73LogRetries;

                    if (writeFailedRetry || awaitingRogerDecode)
                    {
                        DebugOutput($"{Time()} [DIRECT] '{callInProg}' not on record yet ({(writeFailedRetry ? $"local write failed, poll {_directWriteFailRetries}/{MaxDirectWriteFailRetries}" : $"awaiting roger decode, poll {_directRr73LogRetries}/{MaxDirectRr73LogRetries}")}) -- keeping callInProg set, will retry next poll");
                    }
                    else
                    {
                    // Reached the else with an unrecovered failed write means the bounded retry
                    // above is spent -- tell the operator once it's being abandoned, then tear
                    // down normally (re-arms the orphan-Tx backstop, ends the per-poll retry).
                    if (writeFailedForThisCall)
                        Notify?.Publish(new ErrorWarningEvent(ErrorSeverity.Error, $"QSO with {callInProg} still not saved",
                            "the local logbook write kept failing -- this contact was not recorded"));
                    string justWorkedCall = callInProg;
                    // EndContact(Completed) = SetCallInProg(null) (which also zeroes the RR73/
                    // write-fail retry counters on the call transition) + the _contactEpoch bump.
                    // It deliberately leaves curTxMsg alone (the final 73 still announces as it
                    // finishes) and leaves curCmd/replyCmd/replyDecode to the conditional clear
                    // just below, which is completion-specific ("was this the reply that finished").
                    EndContact(ContactEndReason.Completed);

                    // T16 fix, 2026-08-23 (CONFIRMED bug, CRITICAL -- W5PF, 2026-08-21): dequeue
                    // the just-completed call regardless of txMode. Previously RemoveCall only
                    // ran "if (txMode == TxModes.CALL_CQ)" below -- Listen/Reply-mode completions
                    // (the W5PF reproduction: replyRR73CheckBox reply flow, not Call-CQ) had NO
                    // completion dequeue at all, leaving the finished call in CallQueueStore where
                    // ShowStatus's "first"/"to you" wording (WsjtxClient.Display.cs) kept treating
                    // it as still waiting to be worked. RemoveCall is still defensive here (the
                    // worked call is normally already dequeued by ReplyTo()'s own now-deferred-to-
                    // success removal -- see that method's own comment -- but a QSO started some
                    // other way, e.g. a typed/manual call, may not have been). _completedThisPollTick
                    // additionally blocks ProcessDecodeMsg's RR73 courtesy-reply branch from
                    // immediately re-admitting this exact call if its own RR73 decode is processed
                    // later in this SAME tick (see that field's own comment) -- confirmed exactly
                    // this shape in the W5PF log evidence ("1 to you, W5PF first" right after
                    // logging).
                    _callQueueStore.RemoveCall(justWorkedCall);
                    _completedThisPollTick.Add(justWorkedCall);
                    // curCmd/replyCmd/replyDecode only ever describe the reply that just finished
                    // -- clear them so nothing downstream can attribute stale finished-QSO text to
                    // whatever gets selected/replied-to next. (curTxMsg is deliberately left alone
                    // -- see the "only ever overwrite curTxMsg with a REAL message" comment above;
                    // it is flushed on a confirmed band change instead, T19, ClearTransientBandState.)
                    if (justWorkedCall.Equals(WsjtxMessage.DeCall(curCmd ?? ""), StringComparison.OrdinalIgnoreCase))
                    {
                        curCmd = null;
                        replyCmd = null;
                        replyDecode = null;
                    }

                    // Release-audit finding, 2026-08-20 (Call-CQ auto-resume): the classic UDP
                    // path's own OnQsoLogged used to re-arm calling CQ here (CancelQso +
                    // SetupCq(true)) once a QSO finished -- removed along with the rest of the UDP
                    // dispatcher and never ported to Direct mode, leaving Call CQ mode idle (TX
                    // enabled, but no further CQ ever transmitted) after the very first completed
                    // contact. Only in Call-CQ mode: Listen mode must keep waiting on the queue,
                    // not start transmitting on its own.
                    if (txMode == TxModes.CALL_CQ)
                    {
                        // CALL_CQ completion also zeroes the per-attempt cycle fields before
                        // re-arming CQ (LISTEN completion deliberately does not -- preserved from
                        // the pre-EndContact behavior, where only this branch called CancelQso()).
                        xmitCycleCount = 0;
                        txTimeout = false;
                        timedOutCall = null;
                        SetupCq(true);
                    }
                    }   // end Finding-2 "write landed" else -- teardown only when the QSO is on record

                    // Note (runaway-RR73 investigation, 2026-08-28): in LISTEN mode the engine's
                    // own QSO sequencer can keep re-sending this RR73 every slot after callInProg
                    // is cleared here, if the worked station never cleanly hears it. An explicit
                    // DirectSetTxEnabled(false) here would stop that at the source, but it also
                    // drops the engine's tx_enabled between every contact -- which silently
                    // disables ProcessDecodeMsg's own txEnabled-gated "unsolicited 73 -> drop the
                    // stale queue entry" pruning for the whole gap until the operator's next
                    // reply. The _directOrphanTxOvers backstop in DirectApplyStatus catches the
                    // runaway instead (within ~2 overs) without touching the between-contacts
                    // state -- see its own comment.
                }
                else if (WsjtxMessage.IsRogers(curTxMsg) && IsLogEarly(callInProg)
                         && (RecdReport(callInProg) || RecdRogerReport(callInProg)) && sentReportList.Contains(callInProg))
                {
                    // Finding 1, 2026-08-28 (read-only audit): mirrors the removed UDP
                    // ProcessTxEnd() early-log path. When WE send the roger as a bare RRR
                    // (engine Settings.prefer_rrr -- NOT wired through from Jimmy today, so this
                    // stays dormant until it is) with signal reports already exchanged both
                    // ways, "Log early, after RRR" (IsLogEarly: operator opted in AND not a new
                    // DXCC / higher priority) logs the QSO now rather than risk losing it if the
                    // trailing 73 never goes out. Does NOT clear callInProg -- the QSO continues;
                    // the Is73orRR73 branch above tears it down when the 73 is sent. logList /
                    // _liveLoggedQsoKeys dedupe so that later 73 cannot double-log. Also retries
                    // a failed early-RRR write on the next poll, same as Finding 2 above.
                    if (!logList.Contains(callInProg))
                        LogQso(callInProg);
                }
            }

            // Compounding the above: this poll runs every DirectPollIntervalMs (1s) regardless
            // of whether anything actually changed, and ShowStatus()'s own "defer while nothing
            // special is happening" batching is deliberately skipped whenever callInProg is set
            // (an active exchange's real-time info must never be delayed -- see that check's own
            // comment). The UDP path never has this problem because IT only calls ShowStatus()
            // when something in its own status handler actually changed (see e.g. its "if
            // (!transmitting) ShowStatus();" on a frequency change), not from an unconditional
            // fixed-interval timer. Match that discipline here: a poll where nothing in this
            // method's own domain changed already gets its ShowStatus() call for free from
            // whatever real event is happening (a new decode arriving drives ShowStatus() itself,
            // same shared pipeline as the UDP path) -- forcing another one here on every single
            // tick just re-announces identical, already-spoken status once a second, which is
            // exactly what read as "just says Receiving over and over" during an active QSO
            // (callInProg set -> deferral bypassed -> immediate re-announce every tick).
            if (!_directFirstStatusShown || newBand || transmittingChanged)
            {
                _directFirstStatusShown = true;
                ShowStatus();
            }
        }

        private void DirectApplyDecodes(DirectSnapshot snap)
        {
            if (snap.RecentDecodes == null || myCall == null) return;

            // AppSnapshot.recentDecodes is "signals decoded in the most recent RX slot" -- a
            // full-slot list that gets REPLACED each slot, not appended to. When the slot
            // number moves on, everything in it is by definition new; clearing the seen-set
            // then keeps memory bounded and lets an identical message text in a genuinely new
            // slot count as a new decode again, exactly like the UDP path's own 'New' field.
            if (snap.Radio != null && snap.Radio.Slot != _directLastSlotSeen)
            {
                _directLastSlotSeen = snap.Radio.Slot;
                _directSeenDecodeSignatures.Clear();

                // Direct mode's own real "a receive period just completed" transition -- see
                // NotificationCenter.OnPeriodBoundary's own comment. Same real per-period
                // boundary this block's own comment below already establishes for queue-age
                // expiry, reused here rather than inventing a second one.
                Notify?.OnPeriodBoundary();

                // Root-caused live, 2026-08-12: timeOffsets (WsjtxClient.cs) was already being
                // populated in Direct mode (ProcessDecodeMsg -- the exact same shared method the
                // UDP path uses -- adds dmsg.DeltaTime from every decode regardless of
                // transport), but CalcAvgTimeOffset(true) itself, the ONLY thing that turns that
                // raw list into the actual timeOffset average ShowStatus()'s "check clock time"
                // text and the new clock-sync notification below both read, was only ever called
                // from the UDP-only DecodesCompleted()/postDecodeTimer machinery
                // (WsjtxClient.cs). Direct mode collected real clock-offset data all session and
                // never once acted on it. This is the direct-mode equivalent of that same
                // finalization, at the same real per-period boundary as OnPeriodBoundary() just
                // above -- finalizes the period that just ended (using whatever accumulated in
                // timeOffsets before this snapshot's own fresh decodes are processed below) and
                // clears ready for the next one.
                CalcAvgTimeOffset(true);

                // "Use best Tx frequency" restoration, 2026-08-18 investigation + fix: CalcBestOffset
                // (WsjtxClient.BandAudio.cs) had zero live callers since the classic dispatcher died --
                // its only trigger was DecodesCompleted(), itself only ever reachable from the
                // UDP-only "WSJT-X event, Decode start" handler (WsjtxClient.Protocol.cs), which also
                // called SetPeriodState() to set the `period` field CalcBestOffset gates on. Both were
                // removed with the rest of the UDP dispatcher; nothing under Direct mode ever replaced
                // either. Restored here at the same real per-period boundary as CalcAvgTimeOffset(true)
                // just above, not by resurrecting postDecodeTimer/the dispatcher.
                //
                // lastDecodeEvenPeriod already tracks which period the slot that just ended belongs to
                // (set in ProcessDecodeMsg from that decode's own SinceMidnight, the same real event
                // that populates audioOffsets below it) -- same "trust the decode's own period over the
                // current wall clock" reasoning WsjtxClient.Display.cs's ShowAdvancedQueue already uses
                // for the identical problem, reused here rather than re-deriving from DateTime.UtcNow.
                // It also stands in for SetPeriodState()'s old job of keeping the `period` gate itself
                // non-UNK once periods are known.
                Periods justEndedPeriod = lastDecodeEvenPeriod == true ? Periods.EVEN
                    : lastDecodeEvenPeriod == false ? Periods.ODD
                    : Periods.UNK;
                if (justEndedPeriod != Periods.UNK) period = justEndedPeriod;

                // Mirrors DecodesCompleted's own skipFirstDecodeSeries branch exactly: the first
                // cycle after ClearAudioOffsets() (band change, StartSlotAnalysis restart, etc.) has
                // an incomplete/mixed audioOffsets list, so it falls back to any previously-cached
                // offsets instead of computing a bogus fresh result from partial data.
                if (skipFirstDecodeSeries)
                {
                    skipFirstDecodeSeries = false;
                    oddOffset = 0;
                    evenOffset = 0;
                    audioOffsets.Clear();
                    if (cachedOddOffset > 0) oddOffset = cachedOddOffset;
                    if (cachedEvenOffset > 0) evenOffset = cachedEvenOffset;
                }
                else if (justEndedPeriod != Periods.UNK)
                {
                    if (CalcBestOffset(audioOffsets, justEndedPeriod, true))
                    {
                        ctrl.freqCheckBox.Text = "Use best Tx frequency";
                        ctrl.freqCheckBox.ForeColor = Color.Black;
                    }
                }

                // 2026-08-18 investigation + fix: TrimAllCallDict()'s only caller was
                // DecodesCompleted(), itself only ever reachable from the UDP-only "WSJT-X event,
                // Decode start" handler (WsjtxClient.Protocol.cs) -- removed with the rest of the
                // UDP dispatcher, with nothing under Direct mode ever replacing it. Restored here
                // at the same real per-period boundary as CalcAvgTimeOffset(true) just above, not
                // by resurrecting postDecodeTimer/the dispatcher. See CallQueueStore.
                // TrimAllCallDict's own comment; this is deliberately separate from TrimCallQueue
                // just below (different data structure, different concern, different -- and never
                // shared -- age setting: see maxDecodeAgeMinutes's own comment).
                if (_callQueueStore.TrimAllCallDict())
                    DebugOutput(_callQueueStore.AllCallDictString());

                // Queue-age expiry -- moved here 2026-08-11 from DirectApplyStatus's own
                // "transmitting just started" gate, which could never fire while just monitoring
                // with nothing armed. A new slot is a genuine per-period boundary regardless of
                // transmit state, matching the classic UDP path's own "Decode start" trigger
                // (WsjtxClient.Protocol.cs). The retry-limit/discard-give-up counter that used to
                // also live here moved OUT again, 2026-08-24, to DirectApplyStatus's own
                // "transmitting just ended" edge -- see that check's own comment for why (this
                // new-slot trigger fires too late for that specific job, at/after the engine has
                // already autonomously re-armed and started the next period's TX, not before it).
                UpdateMaxTxRepeat();
                if (_callQueueStore.TrimCallQueue())
                    DebugOutput($"{spacer}[DIRECT] TrimCallQueue: expired calls removed{nl}{_callQueueStore.CallQueueString()}");

                // UDP-to-Direct parity pass, 2026-08-12: the recovery half of the Tx-hold safety
                // net ported into DirectApplyStatus above -- without this, autoFreqPauseMode
                // could be set to ENABLED (by that block, once consecTxCount trips) but would
                // then get stuck there forever under Direct mode, since the ENABLED->ACTIVE-
                // >DISABLED progression is normally driven by the UDP path's own CheckNextXmit()
                // (WsjtxClient.cs), which nothing in Direct mode ever calls. That would leave Tx
                // silently, permanently disabled with no automatic recovery -- worse than not
                // having the safety net at all. Mirrors CheckNextXmit's own two-state
                // progression exactly (same shared autoFreqPauseMode/consecTxCount/
                // consecCqCount/consecTimeoutCount fields, same DisableAutoFreqPause()/EnableTx()
                // shared methods -- EnableTx() is transport-aware as of this same pass), driven
                // from this method's own already-correct per-period boundary rather than
                // UDP-only decode-cycle timing.
                if (autoFreqPauseMode == autoFreqPauseModes.ENABLED)
                {
                    autoFreqPauseMode = autoFreqPauseModes.ACTIVE;
                    UpdateCallInProg();
                    DebugOutput($"{Time()} [DIRECT] auto freq update continue");
                }
                else if (autoFreqPauseMode == autoFreqPauseModes.ACTIVE)
                {
                    DisableAutoFreqPause();
                    if (txMode == TxModes.CALL_CQ || callInProg != null) EnableTx();
                    DebugOutput($"{Time()} [DIRECT] auto freq update end");
                }
            }

            foreach (var row in snap.RecentDecodes)
            {
                if (string.IsNullOrEmpty(row.Message)) continue;
                // Per-slot "already processed this decode" guard -- deliberately keyed on the
                // RAW engine text (before normalization), so two engine decodes that only
                // differ by AP suffix / hash form are still treated as distinct arrivals here,
                // exactly as before.
                string sig = $"{row.From}|{row.Message}|{row.Snr}|{row.DtSec:F1}";
                if (!_directSeenDecodeSignatures.Add(sig)) continue;

                // Apply the same cleaning the UDP DecodeMessage/EnqueueDecodeMessage byte
                // parsers always did before shared QSO processing (WsjtxMessage.
                // NormalizeDecodedMessage): strip AP " ? aN"/" aN" markers and unwrap a hashed
                // "<W1AW/2>" compound/portable/special-event call to "W1AW/2". An unresolved
                // "<...>" normalizes to "..." and stays rejected downstream (IsInvalid's own
                // Contains("...") check). Without this, every bracketed decode from the engine
                // (~135 in one live session: incoming reports/RRR/RR73/73 from compound
                // stations) was dropped at ProcessDecodeMsg's deCall/toCall == null gate.
                string normMsg = WsjtxMessage.NormalizeDecodedMessage(row.Message);

                var dmsg = new DecodeMessage
                {
                    Id = WsjtxMessage.UniqueId,
                    New = true,
                    SinceMidnight = DateTime.UtcNow.TimeOfDay,
                    RxDate = DateTime.UtcNow.Date,
                    Snr = row.Snr,
                    DeltaTime = row.DtSec,
                    DeltaFrequency = (int)Math.Round(row.FreqHz),
                    Mode = "~",
                    Message = normMsg,
                    UseStdReply = false,
                    OffAir = false,
                };

                EnqueueDecodeMessage enq = EnqueueDecodeMessage.FromStandardDecode(dmsg);

                // Mirrors the UDP path's own raw-decode-history population (WsjtxClient.Protocol.cs)
                // -- direct mode never did this, so the Raw Decodes panel (Advanced UI tab) has been
                // silently empty this whole session regardless of "Show raw decodes" being checked.
                if (enq.AutoGen && ctrl.advancedCallLayout)
                {
                    while (_rawDecodeHistory.Count >= ctrl.rawMaxRows)
                        _rawDecodeHistory.RemoveAt(0);
                    _rawDecodeHistory.Add(enq);
                    if (ctrl.advShowRaw) ShowRawDecodes();
                }

                if (!enq.Message.Contains(";"))
                {
                    ProcessDecodeMsg(enq, false);
                }
                else
                {
                    // Fox/hound-style multi-target message -- same split ProcessDecodeMsg's own
                    // UDP-path caller uses (WsjtxClient.Protocol.cs), kept identical rather than
                    // reinvented here.
                    string[] words = enq.Message.Replace(";", "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length != 5) continue;
                    EnqueueDecodeMessage enq2 = enq.DeepCopy();
                    enq.Message = $"{words[0]} {words[3]} {words[1]}";
                    ProcessDecodeMsg(enq, true);
                    enq2.Message = $"{words[2]} {words[3]} {words[4]}";
                    ProcessDecodeMsg(enq2, true);
                }
            }
        }

        // Double-click-to-reply equivalent -- calls Engine::call_station_ctx directly via the
        // REPLY control command. Fire-and-forget from the UI's perspective, matching the UDP
        // path's own ReplyMessage send (no synchronous confirmation there either) -- NOT changed
        // to retry or block here, same reasoning as DirectSendHaltTx/DirectSetTxEnabled below.
        //
        // What IS checked: call_station_ctx has exactly one real failure case (Engine's own
        // comment: "No recent decode from <call> -- wait for their next transmission, then click
        // again") -- it fires when Jimmy asks the engine to reply to a specific decoded line that
        // has since aged out of the engine's own decode history with no fallback slot either.
        // Nexus's own design guarantees a refusal here changes NOTHING engine-side (no QSO
        // starts, TX is not armed for this station) -- but before this fix, EngineHost's REPLY
        // handler discarded that Result and always answered "OK" regardless (found 2026-08-17
        // while investigating a real Rust compiler warning, not assumed) -- and even with that
        // fixed engine-side, this call still silently dropped the reply, matching-only on
        // "no response at all" cases the way the two DebugOutput-only helpers below do.
        //
        // Release-audit finding, 2026-08-20: this (and every other fire-and-forget DirectSendXxx/
        // DirectSetXxx below whose return value nothing consumes) used to call DirectSendCommand
        // directly, ON the calling thread -- almost always the UI thread, since these are all
        // hotkey/button handlers. DirectSendCommand's own bounded connect/read pair can genuinely
        // take on the order of a few seconds when the engine host is starting, hung, or restarting
        // (see that method's own comment on the 2026-08-19 root-caused keyboard-lockup bug fixed
        // for the SNAPSHOT poll the same way) -- so every one of these could freeze keyboard/
        // screen-reader/repaint responsiveness for that same window, on EVERY press, including
        // HALT_TX, a safety action. None of these callers ever used the return value for anything
        // beyond this method's own DebugOutput logging, so moving the network I/O onto a
        // background Task changes nothing observable except no longer blocking the UI thread --
        // matching DirectPollTick's own already-established fix for the identical root cause.
        // Codex Audit 04 finding 3, 2026-08-21: HALT_TX's own queue purge
        // (PurgePendingTxArmCommands_NoLock) only reaches a TX-start command still WAITING in the
        // queue -- it cannot cancel one already dequeued and waiting on the engine's response.
        // Every HALT_TX send (DirectSendHaltTx/HaltTxAndWaitForShutdown below) bumps the shared
        // _contactEpoch (WsjtxClient.cs); DirectSendCq/DirectSendReply/DirectSetTxEnabled(true)/
        // DirectSetTuning(true) capture it before enqueueing and compare it at completion -- if a
        // Halt (or any other contact-ending event) landed while their own command was still in
        // flight, its response (even a real "OK") must not be allowed to re-arm TX or commit local
        // QSO state. The engine itself stays safe regardless (the single ordered worker always
        // sends HALT_TX immediately after whatever was already in flight, and purges anything
        // still queued -- see the dispatcher's own class comment), so this is specifically about
        // JIMMY'S OWN bookkeeping not falsely committing "started"/"enabled" after the fact.

        // EngineHost ownership / session identity, 2026-08-23: shared guard for every TX-arming
        // Direct command (DirectSendCq, DirectSendReply, DirectSetTxEnabled(true),
        // DirectSetTuning(true)) -- refuses to arm TX until DirectPollTick has actually
        // confirmed (a real SNAPSHOT whose sessionToken matched _directExpectedSessionToken)
        // that this connection reaches the exact child process this session launched, not
        // merely that SOME process is listening on the fixed control port. False both before
        // the first snapshot has arrived yet and for as long as a mismatch persists; true
        // unconditionally when no token was requested at all for this connection (test-mode/
        // legacy callers -- see ConnectDirectEngine's own comment). Disabling TX/Tune is never
        // gated -- see each call site's own isTxArm comment for why turning OFF must always go
        // through.
        private bool DirectAuthorizedToArmTx(string what)
        {
            // string.IsNullOrEmpty(_directExpectedSessionToken) covers a WsjtxClient that never
            // called ConnectDirectEngine at all -- _directAuthenticated defaults to false (a
            // plain bool field), which on its own would wrongly block a caller that never
            // requested authentication in the first place, not just one still waiting to confirm
            // it. ConnectDirectEngine keeps both in sync going forward (it sets
            // _directAuthenticated = true immediately whenever expectedSessionToken is empty --
            // see its own comment), so this OR is redundant there; it only matters for this
            // no-Connect-yet edge case.
            if (string.IsNullOrEmpty(_directExpectedSessionToken) || _directAuthenticated) return true;
            DebugOutput($"{Time()} [DIRECT] refusing to send '{what}' -- session not yet authenticated");
            return false;
        }

        public void DirectSendReply(string dxcall, string dxgrid, string replyMsg, int? replySnr, double? dxFreqHz, Action<bool> onComplete = null)
        {
            if (!DirectAuthorizedToArmTx("REPLY")) { onComplete?.Invoke(false); return; }
            var args = new DirectReplyArgs
            {
                Dxcall = dxcall,
                Dxgrid = dxgrid,
                ReplyMsg = replyMsg,
                ReplySnr = replySnr,
                DxFreqHz = dxFreqHz.HasValue ? (float?)dxFreqHz.Value : null,
            };
            string json = JsonSerializer.Serialize(args, DirectJsonOptions);
            long capturedContactEpoch = System.Threading.Interlocked.Read(ref _contactEpoch);
            // isTxArm: true -- REPLY arms TX to answer a specific station. See
            // PurgePendingTxArmCommands_NoLock's own comment (Codex Audit 03 release blocker #1).
            EnqueueDirectCommand("REPLY " + json, resp =>
            {
                bool ok = resp != null && resp.Length > 0 && !resp.StartsWith("ERR");
                if (!ok)
                {
                    DebugOutput($"{Time()} [DIRECT] REPLY to '{dxcall}' did not return OK (response: {(resp ?? "<no response>")})");
                    Notify?.Publish(new ErrorWarningEvent(ErrorSeverity.Error, $"Reply to {dxcall} failed",
                        "the engine host did not confirm the reply -- not treated as started"));
                }
                else if (capturedContactEpoch != System.Threading.Interlocked.Read(ref _contactEpoch))
                {
                    // Codex Audit 04 finding 3: superseded by a Halt sent while this REPLY was in
                    // flight -- see _contactEpoch's own comment.
                    DebugOutput($"{Time()} [DIRECT] REPLY to '{dxcall}' returned OK but was superseded by a Halt -- not re-arming");
                    ok = false;
                }
                onComplete?.Invoke(ok);
            }, isTxArm: true);
        }

        // Codex Audit 04 finding 1, 2026-08-21: also called (without a completion callback, as a
        // plain fire-and-forget) for the ordinary interactive Halt hotkey via HaltTx()
        // (WsjtxClient.cs) -- see HaltTxAndWaitForShutdown below for the separate, blocking
        // shutdown-only path CloseComm() now uses instead, while the engine host is still alive.
        public void DirectSendHaltTx()
        {
            System.Threading.Interlocked.Increment(ref _contactEpoch);
            // isPriority: true -- see the dispatcher's own class comment above. HALT_TX must jump
            // ahead of any other not-yet-sent queued command (a CALL_CQ or REPLY that hasn't gone
            // out yet, say), never wait behind it.
            EnqueueDirectCommand("HALT_TX", resp =>
            {
                if (resp == null || resp.Length == 0 || resp.StartsWith("ERR"))
                    DebugOutput($"{Time()} [DIRECT] HALT_TX did not return OK (response: {(resp ?? "<no response>")})");
            }, isPriority: true);
        }

        // Codex Audit 04 finding 1, 2026-08-21: shutdown-only variant of DirectSendHaltTx --
        // called from WsjtxClient.Closing(), which Controller.cs's CloseComm() now runs BEFORE
        // disposing nativeEngineClient (previously reversed: HALT_TX had nothing left to reach by
        // the time it was sent). Blocks the calling thread (the UI thread, from
        // Controller_FormClosing) for up to `timeout`, waiting for a real response -- deliberately
        // does NOT marshal the completion through ctrl.BeginInvoke the way every other Direct
        // command does (marshalToUiThread: false): blocking here means the UI message loop isn't
        // pumping, so a BeginInvoke-marshaled callback would never actually run, deadlocking
        // shutdown forever. Returns false (last-resort backstop, per the required outcome) on
        // timeout or any failure -- CloseComm() proceeds to terminate the engine process either
        // way; this only gives a graceful halt a real chance to land first.
        internal bool HaltTxAndWaitForShutdown(TimeSpan timeout)
        {
            System.Threading.Interlocked.Increment(ref _contactEpoch);
            using (var done = new System.Threading.ManualResetEventSlim(false))
            {
                bool ok = false;
                EnqueueDirectCommand("HALT_TX", resp =>
                {
                    ok = resp != null && resp.Length > 0 && resp.StartsWith("OK");
                    done.Set();
                }, isPriority: true, marshalToUiThread: false);
                done.Wait(timeout);
                return ok;
            }
        }

        // T7 fix, 2026-08-23: DirectSendCommand's own real worst case is 1000ms connect +
        // 3000ms read (see that method's own comment) -- the previous 2s bound Closing() used
        // could expire before even a single HALT_TX round trip finished, let alone one that had
        // to wait behind an already-in-flight ordinary command first (now mitigated separately
        // by AbortInFlightDirectCommand, called automatically for every priority enqueue). Sized
        // to the real worst case plus margin, not an arbitrary round number.
        internal const int DirectHaltConfirmTimeoutMs = 4500;

        // T6/T7 shared halt-and-confirm sequence, 2026-08-23: extracted from Closing() so
        // Controller.ApplyEngineMode() (Options-triggered engine restart) can use the exact same
        // protection a normal application exit already had -- previously ApplyEngineMode
        // disconnected/disposed the OLD engine session immediately with no halt at all (Codex
        // finding 1/12, T6: confirmed bug -- a keyed/tuning radio was left to raw process
        // teardown). Blocking, bounded wait for a real response while the old engine session is
        // still alive to answer; times out and proceeds regardless if it doesn't -- process
        // termination (NativeEngineClient.Dispose(), called right after either caller) remains
        // the last-resort backstop either way, unchanged.
        // Independent audit finding, 2026-08-23 (HALT/restart stopped-state confirmation, HIGH
        // PRIORITY): a small, bounded follow-up budget for SnapshotConfirmsTxAndTuneStopped
        // below, ON TOP OF DirectHaltConfirmTimeoutMs's own wait for HALT_TX's "OK" -- both of
        // HaltAndConfirmTxStopped's callers (Controller.ApplyEngineMode, WsjtxClient.Closing)
        // already block their calling thread for up to DirectHaltConfirmTimeoutMs today, so this
        // adds a second, much smaller wait on an already-bounded, already-blocking sequence, not
        // a new open-ended one.
        internal const int DirectHaltStateConfirmBudgetMs = 1200;
        internal const int DirectHaltStateConfirmPollIntervalMs = 200;

        internal bool HaltAndConfirmTxStopped()
        {
            if (opMode <= OpModes.IDLE) return true;    // never connected this session -- nothing to halt
            StopDecodeTimers();
            tuning = false;
            if (!_directConnected) return true;         // already disconnected -- no engine left to reach
            bool halted = HaltTxAndWaitForShutdown(TimeSpan.FromMilliseconds(DirectHaltConfirmTimeoutMs));
            DebugOutput($"{Time()} [DIRECT] HALT_TX {(halted ? "acknowledged (OK received)" : $"did not confirm within {DirectHaltConfirmTimeoutMs}ms")}");
            txEnabled = false;
            wsjtxTxEnableButton = false;
            if (!halted) return false;   // no OK at all -- nothing left to confirm, straight to forced fallback

            // Independent audit finding, 2026-08-23: HALT_TX's own "OK" only proves main.rs's
            // control-port handler received the command and called Engine::halt_tx() (pinned
            // Nexus code) -- NOT that the engine's own transmit/tune state has actually stopped.
            // This does NOT prove physical PTT release either (that remains a real-radio
            // acceptance test, per the same finding) -- it is the strongest confirmation Jimmy
            // can obtain over this control protocol: the engine's own next reported SNAPSHOT
            // state agrees TX/Tune are off.
            bool stateConfirmed = SnapshotConfirmsTxAndTuneStopped(TimeSpan.FromMilliseconds(DirectHaltStateConfirmBudgetMs));
            DebugOutput($"{Time()} [DIRECT] post-halt state confirmation {(stateConfirmed ? "SNAPSHOT confirmed transmitting/tuning both false" : $"did NOT confirm within {DirectHaltStateConfirmBudgetMs}ms -- proceeding to forced fallback")}");
            return stateConfirmed;
        }

        // Polls a real SNAPSHOT (same request DirectPollTick's own automatic poll sends, called
        // directly here since HaltAndConfirmTxStopped is already fully synchronous/blocking on
        // its own caller's thread -- see that method's own comment) until radio.transmitting and
        // radio.tuning both read back false, or `budget` elapses, whichever comes first. Only
        // ever called after HALT_TX's own "OK" already confirmed above -- if the engine were
        // unreachable entirely, HaltAndConfirmTxStopped already returned before reaching here.
        private bool SnapshotConfirmsTxAndTuneStopped(TimeSpan budget)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < budget)
            {
                string json = DirectSendCommandSafe("SNAPSHOT");
                if (!string.IsNullOrEmpty(json) && !json.StartsWith("ERR"))
                {
                    try
                    {
                        var snap = JsonSerializer.Deserialize<DirectSnapshot>(json, DirectJsonOptions);
                        if (snap?.Radio != null && !snap.Radio.Transmitting && !snap.Radio.Tuning)
                            return true;
                    }
                    catch (JsonException)
                    {
                        // Malformed/partial response -- treat like any other miss and retry
                        // within budget, same tolerance DirectPollTick's own poll already has.
                    }
                }
                System.Threading.Thread.Sleep(DirectHaltStateConfirmPollIntervalMs);
            }
            return false;
        }

        // "Start/resume calling CQ" -- release-audit finding, 2026-08-20: SetupCq (WsjtxClient.cs)
        // has always computed curCmd/qsoState locally for both a fresh Call-CQ start AND the
        // post-QSO auto-resume, but Direct's control protocol had no outbound command that meant
        // "transmit CQ now" at all -- WsjtxClient.Uploads.cs's own comment documented this exact
        // gap since the UDP-transport cleanup. Calls EngineHost's CALL_CQ, which reaches
        // Engine::call_cq -- deliberately NOT the mode-switching Engine::start_cq (see main.rs's
        // own comment on this command): call_cq only queues one CQ frame and arms TX, it never
        // hands pileup-answering decisions to Nexus's own auto-sequencer, so Jimmy's
        // CallQueueRanker stays the only thing that ever decides who gets replied to.
        // dir is the exact directed-CQ token SetupCq's own NextDirCq() already resolved (e.g.
        // "DX"), or null/empty for a plain CQ. Codex Audit 04 finding 2, 2026-08-21: now takes an
        // onComplete callback -- SetupCq (WsjtxClient.cs) gates its own local qsoState/curCmd
        // commit and EnableTx() on this reporting success, instead of committing unconditionally
        // before the engine has accepted the command.
        public void DirectSendCq(string dir, Action<bool> onComplete = null)
        {
            if (!DirectAuthorizedToArmTx("CALL_CQ")) { onComplete?.Invoke(false); return; }
            long capturedContactEpoch = System.Threading.Interlocked.Read(ref _contactEpoch);
            // isTxArm: true -- CALL_CQ arms/starts a transmission. See
            // PurgePendingTxArmCommands_NoLock's own comment (Codex Audit 03 release blocker #1):
            // a HALT_TX enqueued while this is still sitting unsent in the normal queue purges it
            // instead of letting it re-arm TX right after the halt.
            EnqueueDirectCommand("CALL_CQ " + (dir ?? "").Trim(), resp =>
            {
                bool ok = resp != null && resp.Length > 0 && !resp.StartsWith("ERR");
                if (!ok)
                {
                    DebugOutput($"{Time()} [DIRECT] CALL_CQ '{dir}' did not return OK (response: {(resp ?? "<no response>")})");
                    Notify?.Publish(new ErrorWarningEvent(ErrorSeverity.Error, "Call CQ failed",
                        "the engine host did not confirm the CQ -- not treated as started"));
                }
                else if (capturedContactEpoch != System.Threading.Interlocked.Read(ref _contactEpoch))
                {
                    // Codex Audit 04 finding 3: superseded by a Halt sent while this CALL_CQ was
                    // in flight -- see _contactEpoch's own comment.
                    DebugOutput($"{Time()} [DIRECT] CALL_CQ '{dir}' returned OK but was superseded by a Halt -- not re-arming");
                    ok = false;
                }
                onComplete?.Invoke(ok);
            }, isTxArm: true);
        }

        public void DirectSetTxEnabled(bool enabled)
        {
            if (enabled && !DirectAuthorizedToArmTx("SET_TX_ENABLED 1")) return;
            // isTxArm: only when enabling -- disabling TX must never be purged by a halt (see
            // PurgePendingTxArmCommands_NoLock's own comment, Codex Audit 03 release blocker #1).
            EnqueueDirectCommand("SET_TX_ENABLED " + (enabled ? "1" : "0"), resp =>
            {
                if (resp == null || resp.Length == 0 || resp.StartsWith("ERR"))
                    DebugOutput($"{Time()} [DIRECT] SET_TX_ENABLED {(enabled ? 1 : 0)} did not return OK (response: {(resp ?? "<no response>")})");
            }, isTxArm: enabled);
        }

        // PSK Reporter checkbox (Options), live path for direct-engine mode -- see
        // TogglePskReporter's own comment (WsjtxClient.Protocol.cs) for why this needed adding:
        // usePskReporter never actually sent anything anywhere before this, in either transport.
        public void DirectSetPskReporter(bool on)
        {
            EnqueueDirectCommand("SET_PSKREPORTER " + (on ? "1" : "0"), null);
        }

        // Alt+T (Toggle Tune Mode) for direct-engine mode -- see WsjtxClient.BandAudio.cs's
        // ToggleTuningProcess for why this exists (F11/F12 apply live during Tune, unlike a
        // normal FT8/FT4 slot). Engine::set_tune already existed (Nexus's own Tauri UI uses it);
        // this just exposes it, matching every other DirectSetXxx helper above.
        //
        // Returns success (unlike the fire-and-forget DirectSetXxx helpers above) -- found live,
        // 2026-08-10: ToggleTuningProcess used to flip its own `tuning` field unconditionally
        // right after calling this, which under classic WSJT-X/UDP mode (no engine host running
        // at all, DirectSendCommand can't even connect) claimed "Tune started" and let F11/F12
        // proceed as if a real tune carrier existed when nothing had actually happened.
        //
        // Release-audit finding, 2026-08-20 (partial fix, tracked as follow-up work, not done
        // here): this, DirectSetTier, and DirectSetFrequency below are the three DirectSetXxx
        // commands whose bool result a caller still consumes SYNCHRONOUSLY on the UI thread
        // (ToggleTuningProcess/SetOperatingMode/RetuneBand each branch on it immediately to
        // decide what to tell the operator) -- unlike every fire-and-forget DirectSendXxx/
        // DirectSetXxx above, these three still block the UI thread for DirectSendCommand's full
        // bounded connect/read wait. Converting them the same way needs each of those three
        // callers restructured to a background-dispatch + UI-thread-marshaled-callback shape
        // (matching DirectPollTick's own pattern) rather than a same-signature drop-in change --
        // real, still-open work, deliberately not attempted in the same pass that fixed the
        // fire-and-forget commands, to avoid rushing a change to core Tune/mode/frequency control
        // flow without live-radio verification.
        // Codex Audit 02 follow-up, 2026-08-21: now routes through the ordered dispatcher (see its
        // class comment above) instead of running synchronously or opening its own independent
        // Task.Run -- ToggleTuningProcess (WsjtxClient.BandAudio.cs) passes onComplete instead of
        // wrapping this call in its own Task.Run+BeginInvoke; the dispatcher already marshals
        // onComplete onto the UI thread.
        public void DirectSetTuning(bool on, Action<bool> onComplete)
        {
            if (on && !DirectAuthorizedToArmTx("SET_TUNING 1")) { onComplete?.Invoke(false); return; }
            long capturedContactEpoch = System.Threading.Interlocked.Read(ref _contactEpoch);
            // isTxArm: only when starting Tune -- a continuous test carrier is exactly the kind
            // of transmission an emergency halt must prevent restarting; turning tuning OFF must
            // never be purged (Codex Audit 03 release blocker #1, see
            // PurgePendingTxArmCommands_NoLock's own comment).
            EnqueueDirectCommand("SET_TUNING " + (on ? "1" : "0"),
                resp =>
                {
                    bool ok = resp != null && resp.StartsWith("OK");
                    // HALT/restart stopped-state confirmation, 2026-08-23 (independent audit
                    // finding: "an already-received/in-flight command cannot later re-arm TX
                    // after Halt/restart"): mirrors DirectSendReply/DirectSendCq's own identical
                    // check -- a SET_TUNING 1 that was still in flight when HALT_TX ran and only
                    // reports OK afterward must not let ToggleTuningProcess's own onComplete
                    // (WsjtxClient.BandAudio.cs) flip the local `tuning` flag back to true right
                    // after a halt just confirmed it stopped. See _contactEpoch's own comment.
                    if (ok && on && capturedContactEpoch != System.Threading.Interlocked.Read(ref _contactEpoch))
                    {
                        DebugOutput($"{Time()} [DIRECT] SET_TUNING 1 returned OK but was superseded by a Halt -- not re-arming");
                        ok = false;
                    }
                    onComplete(ok);
                }, isTxArm: on);
        }

        // Options > Decode tab's "Decode depth" (Fast/Normal/Deep) -- the one Decode-tab setting
        // with a live setter on the engine (Engine::set_decode_depth), so OptionsDlg's
        // SaveDecodeTab calls this instead of restarting the engine when depth is the only thing
        // that changed. depth must be 1, 2, or 3. Like DirectSendCommand itself, this reaches the
        // engine's control port regardless of whether Jimmy's own transport is UDP or Direct --
        // both talk to the same already-running engine process over this same port.
        public void DirectSetDecodeDepth(int depth)
        {
            EnqueueDirectCommand("SET_DECODE_DEPTH " + depth, null);
        }

        // "Use best Tx frequency" apply step, restored 2026-08-18 -- see CalcBestOffset's own
        // comment (WsjtxClient.BandAudio.cs) for the full investigation/restoration history.
        // Called from SetupCq (before calling CQ) and ReplyTo (before replying), same two real
        // moments the old Andy-WM8Q-fork OptReq command used to fire from, before that whole
        // compatibility layer was removed (8b79743) for crashing the native engine. Reaches
        // Engine::set_tx_offset via the new SET_TX_OFFSET control command -- deliberately NOT
        // REPLY's dxFreqHz field, which has different semantics (follow a specific DX station's
        // own frequency, not pick a quiet gap for Jimmy's own transmission). Fire-and-forget, same
        // as DirectSetDecodeDepth/DirectSetPskReporter above -- a dropped SET_TX_OFFSET just means
        // this particular Tx uses whatever offset the engine already had, not a safety concern.
        //
        // onComplete (audit finding 1, 2026-08-27): the manual frequency hotkeys pass a callback
        // so they can announce the change only after the engine actually confirms it -- rather
        // than speaking the requested value while EngineHost is restarting/unreachable and the
        // marker never moved. true = the engine returned OK; false = ERR/timeout/no response.
        // The automatic pre-reply placement still passes null (best-effort, doesn't announce a
        // number). The dispatcher marshals onComplete onto the UI thread.
        public void DirectSetTxOffset(double hz, Action<bool> onComplete = null)
        {
            if (hz <= 0) { onComplete?.Invoke(false); return; }
            string arg = hz.ToString(System.Globalization.CultureInfo.InvariantCulture);
            EnqueueDirectCommand("SET_TX_OFFSET " + arg,
                onComplete == null ? (Action<string>)null
                    : resp => onComplete(resp != null && resp.Length > 0 && !resp.StartsWith("ERR")));
        }

        // Companion to DirectSetTxOffset for the receive marker (Engine::set_rx_offset via the
        // SET_RX_OFFSET control command, EngineHost/src/main.rs). Jimmy Next's accessible Rx/Tx
        // frequency controls make Jimmy the single authority on BOTH audio offsets -- ReplyTo
        // always passes REPLY's dxFreqHz as null and instead calls this to move RX onto the
        // worked station's decoded audio frequency ("follow the station on receive", every
        // Hold/Best-mode reply and every caller-answers-our-CQ), and DirectSetTxOffset for the
        // TX side. Same fire-and-forget contract: a dropped one just leaves the marker where it
        // was for one more cycle. Engine clamps 200-4000 Hz.
        public void DirectSetRxOffset(double hz, Action<bool> onComplete = null)
        {
            if (hz <= 0) { onComplete?.Invoke(false); return; }
            string arg = hz.ToString(System.Globalization.CultureInfo.InvariantCulture);
            EnqueueDirectCommand("SET_RX_OFFSET " + arg,
                onComplete == null ? (Action<string>)null
                    : resp => onComplete(resp != null && resp.Length > 0 && !resp.StartsWith("ERR")));
        }

        // The single origin point for SET_TX_LEVEL. Both F11/F12 (WsjtxClient.BandAudio.cs's
        // AudioLevel(), a relative +/- step) and the Options > Radio "FT8/FT4 transmit tone level"
        // spinner (an absolute set) come through here. Options is modeless, so the two can be
        // driven at once; _txLevelChangeInFlight serializes them to one outstanding engine command.
        //
        // onDone(true, applied) fires only AFTER the engine returns OK, with `applied` = the
        // clamped value now in effect; the cached level (_engineTxLevel) and -- when "Remember
        // F11/F12 audio level per band" is on -- the per-band remembered value are updated at that
        // same moment and never before. onDone(false, 0) means the change did NOT take: the engine
        // was unreachable / returned ERR / timed out, or another level change was already in flight
        // -- callers must not display, cache, or remember a value in that case.
        public void DirectSetEngineTxLevel(double level01, Action<bool, double> onDone = null)
        {
            if (_txLevelChangeInFlight) { onDone?.Invoke(false, 0); return; }
            double next = Math.Max(0.0, Math.Min(1.0, level01));
            _txLevelChangeInFlight = true;
            EnqueueDirectCommand("SET_TX_LEVEL " + next.ToString(System.Globalization.CultureInfo.InvariantCulture), resp =>
            {
                bool ok = resp != null && resp.Length > 0 && !resp.StartsWith("ERR");
                if (ok)
                {
                    // Set the cache before clearing the in-flight flag, so a poll that lands in
                    // this instant (its refresh is gated on !_txLevelChangeInFlight) can't briefly
                    // overwrite the just-confirmed value with a pre-change snapshot.
                    _engineTxLevel = next;
                    if (ShouldRememberTxLevelForBand(ctrl.Radio.RememberTxLevelPerBand, bandIdx, out int bandKey))
                        ctrl.Radio.TxLevelByBand[bandKey] = next;
                }
                _txLevelChangeInFlight = false;
                onDone?.Invoke(ok, next);
            });
        }

        // Alt+M (Toggle Mode) equivalent for direct-engine mode -- see SetOperatingMode's own
        // comment for why classic UDP/CAT mode never needed this (WSJT-X's UDP API has no
        // outbound mode-change command; Jimmy only ever observed WSJT-X's own self-reported
        // mode). newTier must be "FT8" or "FT4".
        //
        // Returns success (unlike a fire-and-forget DirectSetXxx helper) -- found live (Codex
        // release audit, 2026-08-19): this used to be void, discarding whether the engine ever
        // confirmed the tier switch, while SetOperatingMode changed Jimmy's own `mode` field
        // unconditionally right after calling it. An unreachable engine, a dropped connection, a
        // timed-out read, or an explicit ERR reply all left Jimmy believing it was on the new
        // mode while the engine -- and the actual FT8/FT4 decode/TX cycle on the air -- silently
        // stayed on the old one. Same bug class DirectSetTuning was already fixed for (2026-08-10,
        // see its own comment); this is that fix applied to Alt+M.
        // Codex Audit 02 follow-up, 2026-08-21: now routes through the ordered dispatcher (see its
        // class comment above) instead of running synchronously or opening its own independent
        // Task.Run -- SetOperatingMode (WsjtxClient.Protocol.cs) and the startup tier restore
        // below pass onComplete instead of wrapping this call in their own Task.Run+BeginInvoke;
        // the dispatcher already marshals onComplete onto the UI thread.
        public void DirectSetTier(string newTier, Action<bool> onComplete)
        {
            EnqueueDirectCommand("SET_TIER " + newTier,
                resp => onComplete(resp != null && resp.StartsWith("OK")));
        }

        // Band Up/Down and Options>Frequencies hotkeys' retune path, native/Direct-engine mode.
        // Replaces the old RigctldClient.SetFrequency write (a second, uncoordinated client on
        // the same rigctld session the engine host itself owns) with the engine's own
        // Engine::set_frequency via this command -- Nexus is then the only thing that ever
        // writes a frequency to the radio, and it owns halt/split-clear/dial-memory/retry itself.
        // mode is the logical sideband label Engine::set_frequency expects, not a raw CAT mode
        // word; callers should pass "USB" for FT8/FT4 (Nexus forces the DATA submode USB-side
        // unconditionally for Digital operation, regardless of band -- see settings.rs's own
        // rig_mode_on_sideband comment), matching every FT8/FT4 test call site in the engine.
        // Returns success, same as DirectSetTuning/DirectSetTier above -- callers need to know
        // whether the retune actually reached the engine before claiming a band change happened.
        // Codex Audit 02 follow-up, 2026-08-21: now routes through the ordered dispatcher (see its
        // class comment above) instead of running synchronously or opening its own independent
        // Task.Run -- RetuneBand (WsjtxClient.BandAudio.cs) passes onComplete instead of wrapping
        // this call in its own Task.Run+BeginInvoke; the dispatcher already marshals onComplete
        // onto the UI thread.
        public void DirectSetFrequency(double hz, string band, string mode, Action<bool> onComplete)
        {
            var args = new DirectSetFrequencyArgs { Hz = hz, Band = band, Mode = mode };
            string json = JsonSerializer.Serialize(args, DirectJsonOptions);
            EnqueueDirectCommand("SET_FREQUENCY " + json, resp =>
            {
                bool ok = resp != null && resp.Length > 0 && !resp.StartsWith("ERR");
                if (!ok)
                    DebugOutput($"{Time()} [DIRECT] SET_FREQUENCY {band}/{hz}Hz/{mode} did not return OK (response: {(resp ?? "<no response>")})");
                onComplete?.Invoke(ok);
            });
        }

        // Frequency-override authority split, 2026-08-24 (independent audit finding): Nexus's
        // own Engine::set_tier has an internal auto-QSY that retunes to ITS OWN stock band-plan
        // dial on every tier switch (engine.rs, "switching the mode moves the rig to the NEW
        // mode's dial for the CURRENT band"), independent of whatever dial Jimmy just restored
        // or commanded -- harmless on a band the operator has never customized (Jimmy's own
        // built-in defaults, freqsDict above, already match Nexus's stock table exactly on every
        // band checked), but an unnecessary extra retune on any band the operator HAS
        // customized. Engine::band_plan already has a documented, intended extension point for
        // exactly this ("WSJT-X Settings > Frequencies" overrides, Settings.working_frequencies)
        // -- this builds Jimmy's side of that hand-off. Only the PRIMARY entry per band+mode
        // (FrequencySettings.cs's own "the FIRST entry matching the current mode is that band's
        // primary/canonical frequency" contract) is sent, not every direct-jump hotkey extra --
        // Engine::band_plan has exactly one dial per (band,mode) row, so only the canonical one
        // is meaningful to it. A band the operator has never customized is deliberately left out
        // entirely (not backfilled from Jimmy's own defaults) so Nexus's own stock table --
        // already correct there -- is used unmodified, matching Engine::band_plan's own "Empty
        // overrides = stock" semantics exactly. Shared by NativeEngineClient.Launch (the startup
        // --working-frequencies CLI arg) and OptionsDlg's Frequencies-tab save (the live
        // SET_WORKING_FREQUENCIES command below) so the two never compute this differently.
        internal static List<WorkingFreqArg> BuildWorkingFrequencyEntries(FrequencySettings freq)
        {
            // Index-aligned with this class's own private `bands` field (160/80/60/40/30/20/17/
            // 15/12/10/6) -- FrequencySettings.cs's own class comment documents the same
            // contract; there is no shared constant to import instead (bands is a private
            // instance field, not static).
            int[] bandNumbers = { 160, 80, 60, 40, 30, 20, 17, 15, 12, 10, 6 };
            var result = new List<WorkingFreqArg>();
            for (int i = 0; i < freq.Bands.Length && i < bandNumbers.Length; i++)
            {
                foreach (string mode in new[] { "FT8", "FT4" })
                {
                    var entry = freq.Bands[i].Find(e => e.Mode == mode);
                    if (entry == null) continue;
                    result.Add(new WorkingFreqArg { Band = $"{bandNumbers[i]}m", Mode = mode, Mhz = entry.FreqKHz / 1000.0 });
                }
            }
            return result;
        }

        // Live counterpart of --working-frequencies (BuildWorkingFrequencyEntries's own
        // comment) -- OptionsDlg calls this when the operator saves an edited
        // Options>Frequencies entry mid-session, so Nexus's own auto-QSY picks up the new dial
        // immediately without an EngineHost restart.
        public void DirectSetWorkingFrequencies(List<WorkingFreqArg> entries, Action<bool> onComplete)
        {
            string json = JsonSerializer.Serialize(entries, DirectJsonOptions);
            EnqueueDirectCommand("SET_WORKING_FREQUENCIES " + json, resp =>
            {
                bool ok = resp != null && resp.Length > 0 && !resp.StartsWith("ERR");
                if (!ok)
                    DebugOutput($"{Time()} [DIRECT] SET_WORKING_FREQUENCIES did not return OK (response: {(resp ?? "<no response>")})");
                onComplete?.Invoke(ok);
            });
        }

        // T7 (release-critical, 2026-08-23): the dispatcher's own class comment above already
        // documented that a priority HALT_TX cannot preempt a command already dequeued and
        // blocked inside DirectSendCommand -- it can only jump ahead of commands still WAITING.
        // Confirmed real: DirectSendCommand's own worst case is ~4s (1000ms connect + 3000ms
        // read, immediately below), so an already-in-flight ordinary command could make an
        // emergency HALT_TX wait behind that FULL budget before the single ordered worker is
        // even free to send it. Tracks whichever DirectCommandRequest is currently blocked
        // inside DirectSendCommand (there is at most one -- the worker is strictly serial) so a
        // priority enqueue can force it to fail fast instead of running out its full timeout.
        // volatile: read from whatever thread calls EnqueueDirectCommand (usually the UI
        // thread), written only by the single worker thread.
        private volatile TcpClient _directInFlightClient;

        // Closes whatever Direct command is currently blocked in DirectSendCommand, if any --
        // makes its blocked ConnectAsync/Read throw immediately (caught by
        // DirectSendCommandSafe, same as any other transport failure) instead of running out
        // its own ~4s worst-case budget, so the single ordered worker becomes free to dequeue
        // and send a just-enqueued priority HALT_TX right away. Safe to call whether or not
        // anything is actually in flight, and safe against the field changing/clearing
        // concurrently (Close() on an already-disposed TcpClient just throws
        // ObjectDisposedException, swallowed here -- best-effort, matching every other
        // best-effort teardown in this class).
        private void AbortInFlightDirectCommand()
        {
            try { _directInFlightClient?.Close(); } catch { /* best-effort */ }
        }

        // One command, one short-lived TCP connection, matching the control server's own
        // one-connection-per-request shape (EngineHost/src/main.rs's run_control_server) --
        // deliberately not a persistent stream, so a hung/slow engine host can never leave this
        // blocking on a socket that will never send anything, only ever on this bounded
        // connect/read pair. Instance method (not static, as originally written) specifically so
        // it can publish the in-flight client to _directInFlightClient above -- T7's abort path
        // needs a handle to whatever is currently blocked here.
        private string DirectSendCommand(string command)
        {
            using (var client = new TcpClient())
            {
                _directInFlightClient = client;
                try
                {
                    var connectTask = client.ConnectAsync(System.Net.IPAddress.Loopback, NativeEngineClient.ControlPort);
                    if (!connectTask.Wait(1000) || !client.Connected) return null;

                    using (var stream = client.GetStream())
                    {
                        stream.WriteTimeout = 1000;
                        stream.ReadTimeout = 3000;
                        byte[] cmd = Encoding.UTF8.GetBytes(command + "\n");
                        stream.Write(cmd, 0, cmd.Length);
                        client.Client.Shutdown(SocketShutdown.Send);

                        using (var ms = new System.IO.MemoryStream())
                        {
                            byte[] buf = new byte[8192];
                            int n;
                            try
                            {
                                while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                                    ms.Write(buf, 0, n);
                            }
                            catch (System.IO.IOException)
                            {
                                // Read timeout, connection reset, or AbortInFlightDirectCommand's
                                // own Close() above -- best-effort, return whatever arrived
                                // before that (usually nothing useful, but never throws).
                            }
                            return Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\r', '\n');
                        }
                    }
                }
                finally
                {
                    // Plain assignment, not compare-and-swap: RunDirectCommandWorkerAsync is the
                    // ONLY caller of DirectSendCommandSafe/DirectSendCommand (single ordered
                    // worker, awaited strictly one request at a time -- see this file's own
                    // class comment), so there is never a second call whose "clear" could race
                    // this one's.
                    _directInFlightClient = null;
                }
            }
        }

        // CamelCase naming policy matters for BOTH directions: deserializing SNAPSHOT's JSON
        // (AppSnapshot's own #[serde(rename_all = "camelCase")], tempo-app/src/dto.rs) and
        // serializing REPLY's own JSON (EngineHost/src/main.rs's ReplyArgs, same rename_all) --
        // PropertyNameCaseInsensitive alone only helps the deserialize direction; without the
        // naming policy too, REPLY would send PascalCase field names ("Dxcall") that Rust's
        // serde -- case-SENSITIVE by default -- would silently fail to match.
        //
        // internal (not private): JimmyTests' direct-vs-UDP plumbing parity test (added
        // 2026-08-09 after finding dialFrequency/mode field gaps this exact way, live) needs
        // to build a DirectSnapshot from a hand-written JSON string using these same options,
        // so the test also proves the JSON shape itself still matches, not just the C# side.
        internal static readonly JsonSerializerOptions DirectJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        // Codex Audit 03 release blocker #1 regression test hook, 2026-08-21: a direct,
        // deterministic check of the purge fix (PurgePendingTxArmCommands_NoLock) that needs no
        // real network I/O or stub engine host -- avoids the timing/port-sharing flakiness a
        // real-connection test would inherit from running inside a ~1000-test single process
        // (SNAPSHOT poll timers and leftover dispatcher workers from other tests all sharing the
        // same control port).
        internal bool TestDirectNormalQueueHasTxArmCommand()
        {
            lock (_directQueueLock)
            {
                foreach (var item in _directNormalQueue)
                    if (item.IsTxArm) return true;
            }
            return false;
        }

        // Marks the ordered command worker as "already started" so EnqueueDirectCommand never
        // spawns it. Lets the purge-fix regression test inspect exactly what a HALT_TX enqueue
        // does to the queues, with no background worker racing the assertion by dequeuing the
        // command first (a real Task.Run scheduling race that varies with overall suite load --
        // see HaltPurgesQueuedTxArmCommandTests). Call before the first EnqueueDirectCommand.
        internal void TestSuppressDirectCommandWorker()
        {
            lock (_directQueueLock) _directCommandWorkerStarted = true;
        }

        // ── Test-only hooks (JimmyTests, see InternalsVisibleTo in AssemblyInfo.Testing.cs) ──
        // Exercise the exact same DirectApplyStatus/DirectApplyDecodes pipeline the live poll
        // loop calls, without starting _directPollTimer or touching the network/control port --
        // a synthetic DirectSnapshot goes in, the same downstream classification/call-queue/
        // raw-decode-history code runs, nothing here talks to a real or fake engine process.
        internal void TestApplyDirectSnapshot(string myCallIn, string myGridIn, DirectSnapshot snap)
        {
            myCall = string.IsNullOrWhiteSpace(myCallIn) ? null : myCallIn.Trim().ToUpperInvariant();
            myGrid = myGridIn;
            // ProcessDecodeMsg's own first real guard is "if (opMode != OpModes.ACTIVE) return;"
            // -- ConnectDirectEngine always sets this before the poll timer's first tick reaches
            // it; matched here since this bypasses ConnectDirectEngine entirely.
            opMode = OpModes.ACTIVE;
            DirectApplyStatus(snap);
            DirectApplyDecodes(snap);
        }

        internal string TestCallQueueString => _callQueueStore.CallQueueString();
        internal List<EnqueueDecodeMessage> TestRawDecodeHistory => _rawDecodeHistory;
        // Test-only (item 1, 2026-08-24): ShowRawDecodes is private, and its own real trigger
        // (a new decode arriving via TestApplyDirectSnapshot) always stamps SinceMidnight from
        // the real wall clock -- not controllable from a test, and the even/odd period boundary
        // it needs to land on to prove BOTH alternating sides render correctly would make a test
        // that waits for real time to cross it slow and flaky. This lets a test instead populate
        // TestRawDecodeHistory directly with entries carrying whatever exact SinceMidnight it
        // needs, then render deterministically.
        internal void TestShowRawDecodes() => ShowRawDecodes();
        // Test-only (item 5, 2026-08-24): a status render can be batched (ScheduleStatusAnnounce,
        // WsjtxClient.cs) rather than delivered immediately -- its own real interval runs up to a
        // full trPeriod plus statusBatchDelayMs (as long as ~15.5s for FT8), which would make a
        // test that waits for the real Timer slow and, worse, timing-dependent/flaky depending on
        // exactly where in the real wall-clock period the test happens to run. Reading the
        // pending text directly is deterministic and instant either way.
        internal string TestPendingStatusText => _pendingStatusText;

        // Test-only: runaway-Tx backstop counter (private). DirectRunawayRr73HaltsEngineTests
        // asserts the first orphaned over is tolerated and the second one trips the halt.
        internal int TestOrphanTxOvers => _directOrphanTxOvers;

        // Test-only: `mode` is private and DirectApplyStatus only ever sets it to "FT8" (its
        // own lazy first-run fallback -- see that method's own comment on why Direct mode has
        // no server-side tier read-back to set it any other way). Real FT4 operation only ever
        // changes it via SetOperatingMode/DirectSetTier, both of which need a live engine
        // process this test harness deliberately never starts. This lets the clock-sync
        // notification's own FT4 test exercise a real "FT4" Mode token without one.
        internal void TestSetMode(string m) => mode = m;

        // Test-only: mirrors the timeOffsets/timeOffset/_rawDecodeHistory clearing
        // SetOperatingMode's own successful tier-switch branch performs, for the same reason
        // TestSetMode exists (no live engine host in this test harness, so DirectSetTier can
        // never actually confirm a switch -- see the failure-handling fix in
        // WsjtxClient.Protocol.cs's SetOperatingMode, 2026-08-19). Lets a mode-switch clock-sync
        // test drive the post-switch STATE directly (TestSetMode + this) without needing
        // SetOperatingMode's own wire round-trip to succeed.
        internal void TestClearTimeOffsetState()
        {
            timeOffsets.Clear();
            timeOffset = 0;
            _rawDecodeHistory.Clear();
        }

        // Test-only hooks for the UDP-to-Direct Tx-hold safety-net/connection-loss parity pass,
        // 2026-08-12 -- same InternalsVisibleTo pattern as the hooks above. autoFreqPauseMode/
        // consecTxCount/_directConnected/_directConsecutivePollFailures are all private; these
        // let JimmyTests observe/drive them without exposing them as production API surface.
        internal bool TestAutoFreqPauseDisabled => autoFreqPauseMode == autoFreqPauseModes.DISABLED;
        internal int TestConsecTxCount => consecTxCount;
        internal void TestSetDirectConnected(bool connected) => _directConnected = connected;
        internal void TestDirectHandlePollFailure() => DirectHandlePollFailure();
        internal int TestDirectConsecutivePollFailures => _directConsecutivePollFailures;
        // Finding 2, 2026-08-28: the call whose local logbook write failed and is awaiting a
        // retry (null = none). DirectLogRetryAndEarlyRrrTests asserts it is set after a failed
        // write, cleared after the retry succeeds, and that callInProg is held meanwhile.
        internal string TestLiveLogWriteFailedCall => _liveLogWriteFailedCall;
        // Audit finding, 2026-08-30: DirectFailedWriteRetryIsBoundedTests asserts the failed-
        // write callInProg hold gives up after this many polls instead of holding forever.
        internal int TestMaxDirectWriteFailRetries => MaxDirectWriteFailRetries;
        internal int TestDirectWriteFailRetries => _directWriteFailRetries;

        // "Remember F11/F12 audio level per band" regression coverage: bandIdx is private,
        // updated only inside DirectApplyStatus (the real production poll pipeline, exercised
        // here via TestApplyDirectSnapshot -- same hook every test above already uses).
        // RestoreTxLevelForBand's own DirectSendCommand call is a real, un-mockable TCP attempt
        // (fails fast, bounded 1s, in a test environment with no engine listening -- same
        // tolerance AudioTuningHotkeyTests already documents for DirectSetTuning/SNAPSHOT), so
        // it isn't directly observable here; this accessor instead lets a test prove the
        // DETECTION half that gates it -- does switching bands and returning correctly identify
        // the same band index again -- end to end through the real pipeline, not a hand-rolled
        // stand-in for it. (newBand itself is deliberately not exposed: DirectApplyStatus
        // consumes and resets it via ShowStatus() within the same call TestApplyDirectSnapshot
        // makes, so reading it afterward would race that reset rather than test anything real.)
        internal int? TestBandIdx => bandIdx;
        // internal (not private): JimmyTests proves the 2026-08-17 _pendingBandIdx fix directly
        // -- a real confirmed snapshot for a DIFFERENT band than the one just optimistically
        // requested must clear this back to null, not leave it stuck on the stale guess.
        internal int? TestPendingBandIdx => _pendingBandIdx;
        // internal (not private): JimmyTests proves the 2026-08-19 txEnabled-reconciliation fix
        // directly -- a real snapshot reporting the engine's own txEnabled must update Jimmy's
        // local belief, not leave it stuck on whatever EnableTx()/DisableTx() last wrote.
        internal bool TestTxEnabled => txEnabled;
        // internal (not private): T14 regression coverage sets txEnabled directly, isolating
        // DiscardCall()'s own two-clock-divergence logic without needing a full synthetic
        // snapshot round trip through DirectApplyStatus.
        internal void TestSetTxEnabled(bool v) => txEnabled = v;
        // internal (not private): T16 regression coverage (CompletedQsoRemovesStaleQueueStateTests)
        // proves curCmd/replyCmd/replyDecode are actually cleared once CompleteQso-equivalent
        // cleanup runs for the just-finished call, not merely that callInProg was cleared.
        internal string TestCurCmd => curCmd;
        // internal (not private): T19 regression coverage proves curTxMsg is actually flushed on
        // a confirmed band change, not merely left to be silently overwritten by the next status.
        internal string TestCurTxMsg => curTxMsg;
        internal string TestReplyCmd => replyCmd;
        internal EnqueueDecodeMessage TestReplyDecode => replyDecode;
        // internal (not private): T19 regression coverage (ConfirmedBandChangeFlushesStale
        // TxStateTests) needs to seed old-band curCmd/replyCmd/replyDecode directly to prove the
        // confirmed-band-change flush clears them, without needing a full real-Reply round trip.
        internal void TestSetCurCmd(string v) => curCmd = v;
        // internal (not private): Rx/Tx frequency-control tests seed the analyzed best-free
        // offsets directly (a real slot analysis needs a live decode stream) so ReplyTo's
        // BestFree branch has a non-zero offset to send.
        internal void TestSetBestOffsets(int odd, int even)
        {
            oddOffset = odd; evenOffset = even;
            cachedOddOffset = odd; cachedEvenOffset = even;
        }
        internal uint TestRxOffset => rxOffset;
        internal uint TestTxOffset => txOffset;
        internal bool TestManualFreqThisQso => _manualFreqThisQso;
        // internal (not private): audit finding 3, 2026-08-27 -- nudge-accumulation regression
        // coverage checks the last requested (not yet confirmed) offset a burst chained from, and
        // that it clears back to null once every request in the burst has settled.
        internal int? TestPendingTxOffsetHz => _pendingTxOffsetHz;
        internal int? TestPendingRxOffsetHz => _pendingRxOffsetHz;
        internal int TestTxOffsetRequestsInFlight => _txOffsetRequestsInFlight;
        internal int TestRxOffsetRequestsInFlight => _rxOffsetRequestsInFlight;
        internal void TestSetManualFreqThisQso(bool v) => _manualFreqThisQso = v;
        internal void TestSetReplyCmd(string v) => replyCmd = v;
        internal void TestSetReplyDecode(EnqueueDecodeMessage v) => replyDecode = v;
        // internal (not private): T14 regression coverage (DiscardCallTwoClockDivergenceTests)
        // proves the discard tracker stays ARMED (not disarmed) while txEnabled is still true.
        internal string TestDiscardCall => discardCall;
        internal int TestDiscardCallCycleCount => discardCallCycleCount;
        internal void TestStartDiscardCall(string call) => StartDiscardCall(call);
        internal void TestTriggerDiscardCall() => DiscardCall();
        // internal (not private): "Optimize throughput" scope regression coverage
        // (OptimizeReducesOnlyUntilReportExchangedTests) reads the computed retry budget
        // after UpdateMaxTxRepeat() to prove the queue-depth trim stops applying once the
        // DX has answered with a report.
        internal int TestMaxTxRepeat => maxTxRepeat;
        internal void TestSetManualCallInProg(bool v) => _manualCallInProg = v;
        // internal (not private): lets a test call ConnectDirectEngine (for its myCall/myGrid/
        // opMode/_directConnected side effects) without the 1s SNAPSHOT poll timer it also
        // starts racing a short, timing-sensitive test's own stub-engine-host expectations.
        internal void TestStopPollTimer() => _directPollTimer?.Stop();
        // internal (not private): EngineHost ownership / session identity regression coverage
        // (SessionTokenAuthenticationTests) drives a real SNAPSHOT round trip against a stub
        // engine host -- TestStopPollTimer above is used to stop the automatic 1s timer racing
        // the test's own explicit calls, and this fires exactly one tick on demand instead, the
        // same production DirectPollTick() the real timer calls, so the test proves the actual
        // authentication/NegoState-promotion logic rather than a hand-rolled stand-in for it.
        internal void TestTriggerDirectPollTick() => DirectPollTick();
        internal bool TestDirectAuthenticated => _directAuthenticated;
        // internal (not private): CAT mode command/readback correlation regression coverage
        // (RigModeMismatchGraceWindowAndReconciliationTests) seeds/backdates these directly so it
        // can exercise DirectApplyStatus's own real grace-window/reconcile logic against a chosen
        // elapsed time without an actual multi-second sleep -- _lastCommandedSideband itself is
        // already internal and needs no accessor.
        internal void TestSetLastCommandedSidebandChangedUtc(DateTime utc) => _lastCommandedSidebandChangedUtc = utc;
        internal int TestSidebandMismatchStreak => _sidebandMismatchStreak;
    }

    // JSON shapes matching AppSnapshot/RadioStatus/DecodeRow's own camelCase serde output
    // (tempo-app/src/dto.rs) -- only the fields WsjtxClient.Direct.cs actually reads, not a full
    // mirror of AppSnapshot (which carries a great deal Jimmy doesn't use: QSO/Field Day/chat-CQ/
    // upload-toast state and more, all Nexus-UI-specific).
    internal class DirectSnapshot
    {
        public string Mycall { get; set; }
        public string Mygrid { get; set; }
        public DirectRadioStatus Radio { get; set; }
        public List<DirectDecodeRow> RecentDecodes { get; set; }
        // Added 2026-08-10: without this, Direct mode had no way to know what it was actually
        // transmitting (the "sending -05"/"sending RR73" status text) or to detect a QSO
        // completing at all -- both are UDP-path-only today (WsjtxClient.Protocol.cs's
        // ProcessTxStart/ProcessTxEnd, driven by real WSJT-X status messages Direct mode never
        // receives). Null while the engine reports no active QSO (listening, or between QSOs).
        public DirectQsoStatus Qso { get; set; }
        // EngineHost ownership / session identity, 2026-08-23: injected at the JSON level by
        // main.rs's own SNAPSHOT handler AFTER AppSnapshot is serialized (never part of the
        // pinned Nexus struct itself -- see that handler's own comment). SessionToken is the
        // per-launch nonce DirectPollTick compares against _directExpectedSessionToken before
        // trusting this snapshot at all; Pid is informational/diagnostic only (logged on a
        // mismatch), never itself part of the match.
        public string SessionToken { get; set; }
        public int Pid { get; set; }
    }

    // Mirrors the QSO-relevant slice of tempo-app::dto::QsoStatus (Rust) -- only the fields
    // Direct-mode Tx tracking (DirectApplyStatus's own transmitting-transition check) needs.
    internal class DirectQsoStatus
    {
        // Sequencer state, e.g. "callingCq", "awaitReport", "done" -- informational only today;
        // Tx tracking below uses TxNow's own message content (matching the UDP path's own
        // txMsg-content-based logic) rather than this, so it stays in sync with exactly the same
        // WsjtxMessage.IsReport/IsRogerReport/Is73orRR73 parsing the UDP path already uses.
        public string State { get; set; }
        // On-air text of the message queued for/just sent in the current TX slot ("Now sending"),
        // null when listening or the QSO is complete. Same standard WSJT-X wire-format text
        // (e.g. "KB0UZT W1XI -05") the UDP path's own txMsg (StatusMessage.LastTxMsg) carries,
        // so it can be parsed with the exact same WsjtxMessage helpers.
        public string TxNow { get; set; }
    }

    internal class DirectRadioStatus
    {
        public double DialMhz { get; set; }
        public bool Transmitting { get; set; }
        public ulong Slot { get; set; }
        // Receive / transmit audio offsets in Hz -- the green / red waterfall markers
        // (tempo-app/src/dto.rs: RadioStatus.rx_offset_hz / tx_offset_hz, both default 1500).
        // Already present in every SNAPSHOT; Jimmy Next reads them for the accessible Rx/Tx
        // frequency controls: the "Announce Rx/Tx frequencies" hotkey, the Rx<->Tx exchange
        // hotkeys, and to notice if the engine's offset has drifted from what Jimmy last
        // commanded. Jimmy itself is the sole authority that MOVES them, via DirectSetTxOffset /
        // DirectSetRxOffset; these fields are the read-back.
        public double RxOffsetHz { get; set; }
        public double TxOffsetHz { get; set; }
        // WSJT-X "Hold Tx Freq" (tempo-app/src/dto.rs: RadioStatus.hold_tx_freq). Jimmy drives
        // every RX/TX move explicitly and always passes REPLY's dxFreqHz as null, so the
        // engine's own value never changes Jimmy's behavior -- carried here only so the
        // announce/diagnostic surfaces can report the engine's actual state.
        public bool HoldTxFreq { get; set; }
        // Null when the engine has no CAT control of the radio at all (Radio.Mode == WsjtxCat) --
        // AppSnapshot's own radio.micGain is nullable for exactly that reason (tempo-app/src/dto.rs).
        // Radio-side (CAT) mic gain -- NOT what F11/F12 uses (see TxLevel below); kept here only
        // because it's still a real, separate engine field (Phone/manual-PTT mic gain).
        public double? MicGain { get; set; }
        // TX audio drive level (0.0-1.0) -- the sound-card/software side of the signal chain
        // (WSJT-X's own "Pwr" slider equivalent), always present with a default (tempo-app/
        // src/dto.rs: "#[serde(default = "default_txlevel")] pub tx_level: f32" -- not an
        // Option like MicGain, since it needs no CAT/radio connection at all to mean something).
        // Added 2026-08-10: F11/F12 (WsjtxClient.BandAudio.cs's AudioLevel()) now reads/writes
        // this instead of MicGain -- confirmed live that MicGain has no effect on FT8/FT4
        // transmit audio (Nexus applies it only for manual Phone/PTT operation).
        public double TxLevel { get; set; }
        // Whether the operator is holding a steady Tune carrier (tempo-app/src/dto.rs:
        // RadioStatus.tuning) -- Andy WM8Q's fork's F11/F12 worked "during tune or transmit"
        // (see AudioLevel's own comment); this lets Jimmy Native match that once ToggleTuningProcess
        // actually starts a tune (SET_TUNING, added 2026-08-10).
        public bool Tuning { get; set; }
        // RX input audio level (0.0-1.0 RMS, tempo-app/src/dto.rs: RadioStatus.rx_level) -- the
        // real modern equivalent of Andy WM8Q's fork's "Audio in: X dB" (WSJT-X's own m_px),
        // which Alt+Q reported while receiving. Not an S-meter/CAT reading -- purely the
        // soundcard's own incoming signal level, same as the original.
        public float RxLevel { get; set; }
        // CAT S-meter (dB rel S9), tempo-app/src/dto.rs: RadioStatus.smeter_db -- kept here for
        // completeness (a real, separate engine field) but NOT what Alt+Q's receive-time report
        // uses; see RxLevel above for that.
        public int? SmeterDb { get; set; }
        // Hamlib's 0.0-1.0 relative RF-power reading (tempo-app/src/dto.rs: RadioStatus.
        // rf_power) -- the SNAPSHOT-sourced replacement for RigctldClient.PollOnce's own
        // "l RFPOWER" query. Null on a rig/backend that doesn't report it, same "no reading,
        // nothing to act on" contract RigctldClient.RadioStatus.PowerRaw always had.
        public double? RfPower { get; set; }
        // CAT SWR reading (tempo-app/src/dto.rs: RadioStatus.tx_swr) -- drives both Alt+Q's
        // quick report and the "Halt Tx when SWR > threshold" safety check below in
        // DirectApplyStatus, replacing RigctldClient.PollOnce's own "l SWR" query.
        public double? TxSwr { get; set; }
        // CAT ALC reading (tempo-app/src/dto.rs: RadioStatus.tx_alc) -- RigctldClient never
        // polled this (Hamlib's ALC level has no WSJT-X-parity feature depending on it today);
        // carried here only because the engine already reports it in the same SNAPSHOT.
        public double? TxAlc { get; set; }
        // Calibrated output watts, where the rig/backend can report them (tempo-app/src/dto.rs:
        // RadioStatus.tx_po_w) -- distinct from RfPower's raw 0.0-1.0 Hamlib fraction. Not polled
        // by RigctldClient at all before this (Hamlib's plain rigctld protocol has no calibrated-
        // watts query); available here only because Nexus's own radio loop computes it.
        public double? TxPoW { get; set; }
        // Rig/CAT connection health (tempo-app/src/dto.rs: RadioStatus.cat_ok) -- null = not
        // applicable (VOX, no CAT configured), true = CAT connected, false = CAT configured but
        // failing. Drives DirectApplyStatus's "Radio CAT link lost"/recovered notification.
        public bool? CatOk { get; set; }
        // Human-readable detail paired with CatOk above (tempo-app/src/dto.rs: RadioStatus.
        // cat_detail), e.g. "rigctld not reachable..." -- used as the failure reason text.
        public string CatDetail { get; set; }
        // T17 fix, 2026-08-23 (PARTIAL/CONFIRMED -- radio ended up in LSB on 30m FT4, reported
        // 2026-08-21): the rig's own CAT-reported mode readback (tempo-app/src/dto.rs:
        // RadioStatus.rig_mode -- "the rig's actual mode read back over CAT... Display-only --
        // the cockpit flags a mismatch with the commanded mode"). Confirmed by tracing every
        // C#->EngineHost->Nexus request that Jimmy's own SET_FREQUENCY always requested USB
        // (never LSB) for every band, including 30m FT4/FT8 -- the reproduced LSB outcome is
        // therefore downstream of what Jimmy commands (CAT-mode application/readback, rig
        // memory, or Hamlib behavior), not a Jimmy-side logic bug in frequency/sideband
        // selection. Jimmy previously never read this field at all, so a radio that silently
        // stayed in a different mode than requested was never detected or surfaced -- see
        // DirectApplyStatus's own new reconciliation/warning for the fix this enables. Null
        // until the rig has reported a real CAT mode at least once (VOX-only/no-CAT stations,
        // or the first snapshot before a readback has arrived, correctly report nothing here).
        public string RigMode { get; set; }
        // Added 2026-08-19 (release-blocker follow-up): the engine's own authoritative belief
        // about whether it will currently transmit -- tempo-app/src/dto.rs's RadioStatus.
        // tx_enabled. Confirmed live to be the real cause of a stuck-forever callInProg: the
        // engine can disable its own tx_enabled independently (its own retry/QSO-sequencer
        // logic, unrelated to anything Jimmy explicitly commanded -- no HALT_TX was ever sent),
        // but before this field existed, Jimmy's own `txEnabled` (WsjtxClient.cs) was ONLY ever
        // written locally by EnableTx()/DisableTx() at the moment JIMMY commands a change --
        // Direct mode had no way to learn the engine changed its mind on its own. That silent
        // drift (Jimmy still believing txEnabled=true while the engine's real state was already
        // false) is what made DiscardCall()'s own "give up" check -- gated on
        // `(txMode==LISTEN && !txEnabled) || txMode==CALL_CQ` -- log "not in effect" and leave
        // callInProg stuck, and separately made EnableMode() (Alt+E)'s own `!txEnabled` guard
        // refuse to do anything, since Jimmy's stale belief said Tx was already enabled. See
        // DirectApplyStatus's own reconciliation of this field for the fix.
        public bool TxEnabled { get; set; }
    }

    internal class DirectDecodeRow
    {
        public string From { get; set; }
        public int Snr { get; set; }
        public double DtSec { get; set; }
        public double FreqHz { get; set; }
        public string Message { get; set; }
    }

    // Wire shape for the REPLY control command -- field names (once camelCase'd by
    // JsonSerializer's default naming, matched case-insensitively on the Rust side) must line
    // up with EngineHost/src/main.rs's own ReplyArgs struct.
    internal class DirectReplyArgs
    {
        public string Dxcall { get; set; }
        public string Dxgrid { get; set; }
        public string ReplyMsg { get; set; }
        public int? ReplySnr { get; set; }
        public float? DxFreqHz { get; set; }
    }

    // Field names (camelCase via DirectJsonOptions) must line up with EngineHost/src/main.rs's
    // own SetFrequencyArgs struct.
    internal class DirectSetFrequencyArgs
    {
        public double Hz { get; set; }
        public string Band { get; set; }
        public string Mode { get; set; }
    }

    // Field names (camelCase via DirectJsonOptions) must line up with pinned Nexus's own
    // WorkingFreq struct (tempo-app/src/settings.rs) -- see BuildWorkingFrequencyEntries' own
    // comment. Public (unlike DirectSetFrequencyArgs above): appears in DirectSetWorkingFrequencies
    // and NativeEngineClient.Launch's own public signatures, so it can't be more restrictive
    // than internal without an accessibility mismatch on those methods.
    public class WorkingFreqArg
    {
        public string Band { get; set; }
        public string Mode { get; set; }
        public double Mhz { get; set; }
    }
}
