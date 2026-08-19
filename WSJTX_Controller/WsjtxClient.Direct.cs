using System;
using System.Collections.Generic;
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
        private readonly HashSet<string> _directSeenDecodeSignatures = new HashSet<string>();

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
        public void ConnectDirectEngine(string myCallIn, string myGridIn)
        {
            myCall = string.IsNullOrWhiteSpace(myCallIn) ? null : myCallIn.Trim().ToUpperInvariant();
            myGrid = myGridIn;
            _directSeenDecodeSignatures.Clear();
            _directLastSlotSeen = 0;
            _directFirstStatusShown = false;
            _directStartupBandResolved = false;
            _directConsecutivePollFailures = 0;
            _directLossAnnounced = false;
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
        }

        private void DirectPollTick()
        {
            if (!_directConnected) return;
            DirectSnapshot snap;
            try
            {
                string json = DirectSendCommand("SNAPSHOT");
                if (json == null || json.Length == 0 || json.StartsWith("ERR"))
                {
                    DirectHandlePollFailure();
                    return;
                }
                snap = JsonSerializer.Deserialize<DirectSnapshot>(json, DirectJsonOptions);
            }
            catch (Exception ex)
            {
                // Best-effort, matches the UDP path's own tolerance for a transient miss --
                // the engine host restarting (auto-restart on crash) just means the next
                // poll's connection attempt fails until it's back up, not a fatal error here.
                DebugOutput($"{Time()} [DIRECT] SNAPSHOT poll failed: {ex.Message}");
                DirectHandlePollFailure();
                return;
            }
            if (snap == null)
            {
                DirectHandlePollFailure();
                return;
            }

            // A real snapshot came back -- whatever failure streak was building is over.
            _directConsecutivePollFailures = 0;
            _directLossAnnounced = false;

            // First successful poll = "connected" as far as every OTHER piece of Jimmy's own
            // status/UI code is concerned -- most of it gates on NegoState, not on anything
            // specific to this class (found live, 2026-08-08, testing this for the first time:
            // status kept announcing "Waiting for WSJT-X" on a one-second loop even while real
            // decodes were actively populating the call queue, because nothing here had ever
            // told NegoState we were done). Set once we've actually proven the engine host is
            // reachable and answering -- not unconditionally in ConnectDirectEngine -- so a
            // brief window before its control port comes up still shows as "not yet connected"
            // rather than claiming success before it's true.
            if (WsjtxMessage.NegoState != WsjtxMessage.NegoStates.RECD)
            {
                WsjtxMessage.NegoState = WsjtxMessage.NegoStates.RECD;
                ctrl.initialConnFaultTimer?.Stop();
                DebugOutput($"{Time()} [DIRECT] first snapshot received -- NegoState -> RECD");
            }

            DirectApplyStatus(snap);
            DirectApplyDecodes(snap);
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
        }

        private void DirectApplyStatus(DirectSnapshot snap)
        {
            var radio = snap.Radio;
            if (radio == null) return;

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
                ClearCalls(true);
                logList.Clear();
                ShowLogged();
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
                if (bandIdx == null)
                {
                    int fallbackIdx = (ctrl.Radio.LastBandIdx >= 0 && ctrl.Radio.LastBandIdx < bands.Count)
                        ? ctrl.Radio.LastBandIdx
                        : 5; // 20m -- matches the UDP path's own InitialConnect default
                    if (RetuneBand(fallbackIdx, "DirectInitialConnect")) ShowBandChangePending(fallbackIdx);
                }
            }

            // Persist whenever a real band is confirmed, so the NEXT session can restore it --
            // cheap in-memory field write on every tick a real band is known; actual disk
            // persistence only happens once, on clean shutdown, via Controller_FormClosing's
            // existing Radio.SaveToIni call.
            if (bandIdx != null) ctrl.Radio.LastBandIdx = (int)bandIdx;

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

            // UDP-to-Direct parity pass, 2026-08-12: port the Tx-hold safety net (the UDP path's
            // ProcessTxEnd, WsjtxClient.cs -- "too many consecutive transmits without being
            // heard, in Hold mode" -- consecTxCount/maxConsecTxCount) so it protects Direct-mode
            // operators too, not just UDP ones. Direct mode had NO equivalent at all before this
            // -- confirmed by a full UDP-vs-Direct parity audit.
            //
            // Deliberately triggered on the same physical transmitting-just-ended edge the UDP
            // path's ProcessTxEnd reacts to, NOT on the QSO-completion (Is73orRR73) point a few
            // lines below in this method -- consecTxCount exists specifically to catch a station
            // that's NEVER replying, so by definition it must count every completed Tx cycle,
            // not just ones that reach a 73/RR73. Content-independent (doesn't consult txMsg at
            // all), so it doesn't hit the Qso.TxNow-goes-null-early staleness problem documented
            // on curTxMsg's own assignment above -- only the fact that a transmission just ended
            // matters here, not what it said.
            if (wasTransmitting && !transmitting)
            {
                if (ctrl.freqCheckBox.Checked && autoFreqPauseMode == autoFreqPauseModes.DISABLED && ctrl.holdCheckBox.Checked)
                {
                    consecTxCount++;
                    if (consecTxCount >= maxConsecTxCount)
                    {
                        DisableTx(true);
                        autoFreqPauseMode = autoFreqPauseModes.ENABLED;
                        UpdateCallInProg();
                        DebugOutput($"{Time()} [DIRECT] auto freq update started (consec Tx), autoFreqPauseMode:{autoFreqPauseMode}");
                    }
                }
                else
                {
                    consecTxCount = 0;
                }
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

            // Queue-age expiry (TrimCallQueue) and the retry-limit/discard-give-up counter used
            // to live here, gated on "transmitting just started" -- moved to DirectApplyDecodes'
            // own new-slot detection instead. Root-caused live, 2026-08-11: that gate can never
            // fire in Listen mode (which by definition never transmits), so queue-age expiry
            // silently never ran at all while listening -- confirmed via a full session's debug
            // log showing txMode:LISTEN throughout with callQueue.Count only ever growing. The
            // classic UDP path never had this bug: its own trigger (WsjtxClient.Protocol.cs,
            // "WSJT-X event, Decode start") is a genuine per-period decode-cycle boundary, true
            // whether or not that period transmits -- Direct mode's port used the wrong event.

            // Mirrors the transmitting assignment above -- same root cause, same fix: without
            // this, the class-level `tuning` field (AudioLevel()'s own guard, Alt+T's status
            // text) never learned a real Tune (SET_TUNING) was underway in Direct mode.
            tuning = radio.Tuning;

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
            string newTxMsg = snap.Qso?.TxNow;
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
                    SetCallInProg(null);
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

                // Queue-age expiry + the retry-limit/discard-give-up counter -- moved here
                // 2026-08-11 from DirectApplyStatus's own "transmitting just started" gate,
                // which could never fire in Listen mode. A new slot is a genuine per-period
                // boundary regardless of transmit state, matching the classic UDP path's own
                // "Decode start" trigger (WsjtxClient.Protocol.cs).
                UpdateMaxTxRepeat();
                // No "+2" grace buffer -- see WsjtxClient.cs's ProcessDecodes' matching comment
                // (removed 2026-08-11, user request): the reset-on-any-activity logic already
                // protects a genuinely progressing QSO, so padding only made the configured
                // "repeat limit" number inaccurate.
                int maxDiscardCount = maxTxRepeat;
                if (_callQueueStore.TrimCallQueue())
                    DebugOutput($"{spacer}[DIRECT] TrimCallQueue: expired calls removed{nl}{_callQueueStore.CallQueueString()}");
                if (discardCall != null && discardCall == callInProg && ++discardCallCycleCount >= maxDiscardCount)
                    DiscardCall();

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
                string sig = $"{row.From}|{row.Message}|{row.Snr}|{row.DtSec:F1}";
                if (!_directSeenDecodeSignatures.Add(sig)) continue; // already processed this slot

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
                    Message = row.Message,
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
        public void DirectSendReply(string dxcall, string dxgrid, string replyMsg, int? replySnr, double? dxFreqHz)
        {
            var args = new DirectReplyArgs
            {
                Dxcall = dxcall,
                Dxgrid = dxgrid,
                ReplyMsg = replyMsg,
                ReplySnr = replySnr,
                DxFreqHz = dxFreqHz.HasValue ? (float?)dxFreqHz.Value : null,
            };
            string json = JsonSerializer.Serialize(args, DirectJsonOptions);
            string resp = DirectSendCommand("REPLY " + json);
            if (resp == null || resp.Length == 0 || resp.StartsWith("ERR"))
                DebugOutput($"{Time()} [DIRECT] REPLY to '{dxcall}' did not return OK (response: {(resp ?? "<no response>")})");
        }

        // Fire-and-forget by design (see DirectSendCommand's own comment on the bounded
        // connect/read pair) -- NOT changed to retry or block here, since altering a TX-safety
        // command's timing/retry behavior needs real-radio verification this pass doesn't have.
        // What IS safe and added here: a failed response no longer disappears silently. Every
        // call site already treats "the command may not have reached the engine" as an expected,
        // recoverable case (that's what DirectPollTick's own next SNAPSHOT poll is for -- it will
        // surface a real disconnect via DirectHandlePollFailure within a few seconds either way),
        // so this only adds visibility for diagnosing a suspected TX-command failure after the
        // fact, never a behavior change.
        public void DirectSendHaltTx()
        {
            string resp = DirectSendCommand("HALT_TX");
            if (resp == null || resp.Length == 0 || resp.StartsWith("ERR"))
                DebugOutput($"{Time()} [DIRECT] HALT_TX did not return OK (response: {(resp ?? "<no response>")})");
        }

        public void DirectSetTxEnabled(bool enabled)
        {
            string resp = DirectSendCommand("SET_TX_ENABLED " + (enabled ? "1" : "0"));
            if (resp == null || resp.Length == 0 || resp.StartsWith("ERR"))
                DebugOutput($"{Time()} [DIRECT] SET_TX_ENABLED {(enabled ? 1 : 0)} did not return OK (response: {(resp ?? "<no response>")})");
        }

        // PSK Reporter checkbox (Options), live path for direct-engine mode -- see
        // TogglePskReporter's own comment (WsjtxClient.Protocol.cs) for why this needed adding:
        // usePskReporter never actually sent anything anywhere before this, in either transport.
        public void DirectSetPskReporter(bool on)
        {
            DirectSendCommand("SET_PSKREPORTER " + (on ? "1" : "0"));
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
        public bool DirectSetTuning(bool on)
        {
            string resp = DirectSendCommand("SET_TUNING " + (on ? "1" : "0"));
            return resp != null && resp.StartsWith("OK");
        }

        // Options > Decode tab's "Decode depth" (Fast/Normal/Deep) -- the one Decode-tab setting
        // with a live setter on the engine (Engine::set_decode_depth), so OptionsDlg's
        // SaveDecodeTab calls this instead of restarting the engine when depth is the only thing
        // that changed. depth must be 1, 2, or 3. Like DirectSendCommand itself, this reaches the
        // engine's control port regardless of whether Jimmy's own transport is UDP or Direct --
        // both talk to the same already-running engine process over this same port.
        public void DirectSetDecodeDepth(int depth)
        {
            DirectSendCommand("SET_DECODE_DEPTH " + depth);
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
        public bool DirectSetTier(string newTier)
        {
            string resp = DirectSendCommand("SET_TIER " + newTier);
            return resp != null && resp.StartsWith("OK");
        }

        // One command, one short-lived TCP connection, matching the control server's own
        // one-connection-per-request shape (EngineHost/src/main.rs's run_control_server) --
        // deliberately not a persistent stream, so a hung/slow engine host can never leave this
        // blocking on a socket that will never send anything, only ever on this bounded
        // connect/read pair.
        private static string DirectSendCommand(string command)
        {
            using (var client = new TcpClient())
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
                            // Read timeout or connection reset -- best-effort, return whatever
                            // arrived before that (usually nothing useful, but never throws).
                        }
                        return Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\r', '\n');
                    }
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
}
