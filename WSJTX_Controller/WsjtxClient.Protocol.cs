using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using WsjtxUdpLib.Messages;
using WsjtxUdpLib.Messages.Out;

namespace WSJTX_Controller
{
    // ═══════════════════════════════════════════════════════════════════════════════════
    // UDP TRANSPORT REMOVED, 2026-08-18 (full cleanup pass, following the 2026-08-18
    // UDP-to-Direct test-harness migration that first made it provably unreachable). Jimmy
    // Next's sole transport is Jimmy Test -> Direct (control port) -> EngineHost -> Nexus
    // (WsjtxClient.Direct.cs); Controller.ApplyEngineMode() calls ConnectDirectEngine()
    // unconditionally, in production and in test mode alike. The classic WSJT-X UDP
    // dispatcher that used to live in this file (Update(), the Heartbeat/StatusMessage/
    // Decode/QsoLogged/LoggedAdif handling it drove, CheckMyCall, CheckModeSupported,
    // ProcessTxStart/ProcessTxEnd, the decode-cycle-timing machinery driven by WSJT-X's own
    // "Decoding" status flag, and HeartbeatNotRecd/heartbeatRecdTimer) is deleted outright --
    // every one of those was reachable only via ConnectNativeEngine/UdpLoop, which were
    // themselves deleted the moment JimmyDirectReplay.py replaced JimmyReplay.py as the
    // replay-test harness. `WsjtxProtocolAdapter` (Protocol/WsjtxProtocolAdapter.cs) and the
    // udpClient/udpClient2/ipAddress/multicast socket plumbing it backed are deleted too --
    // see WsjtxClient.cs's own comment at CloseAllUdp's call site and the constructor for the
    // full accounting. `WsjtxUdpLib`'s message DTOs are NOT touched: DecodeMessage/
    // StatusMessage/EnqueueDecodeMessage remain genuinely shared (WsjtxClient.Direct.cs
    // builds and consumes the exact same types from the Direct control port), and
    // WsjtxMessage's static parsing/classification helpers (ToCall/DeCall/IsCQ/Is73orRR73/
    // etc.) are used by every decode ProcessDecodeMsg handles, from either transport.
    // ═══════════════════════════════════════════════════════════════════════════════════
    public partial class WsjtxClient
    {
        // Root-caused live, 2026-08-11: usePskReporter never actually sent anything anywhere
        // before this, in either transport -- it only ever flipped a local bool for the status
        // announcement and the ini. Classic UDP mode has no outbound PSK Reporter command in
        // WSJT-X's own protocol at all, so there's nothing to add there; direct-engine mode DOES
        // have a real engine-side toggle (Engine::set_pskreporter, native spotting independent
        // of WSJT-X's own), which was simply never wired to this checkbox at all until now.
        public bool TogglePskReporter()
        {
            usePskReporter = !usePskReporter;
            newPskReporter = true;
            if (_directConnected) DirectSetPskReporter(usePskReporter);
            ShowStatus();
            return true;
        }

        // Release-audit finding, 2026-08-21 (completes the 2026-08-20 partial fix): guards
        // against a rapid double-press of Alt+M computing `newModeValue != mode` from stale
        // state while an earlier tier-change request is still out (mode only updates once that
        // request's own continuation lands) -- same shape as ToggleTuningProcess's own
        // _tuningRequestInFlight, and the same reasoning: a mode switch is a deliberate,
        // infrequent action, so a second press while one is already in flight is simply ignored
        // (handled, no-op) rather than queued or raced.
        private bool _tierChangeRequestInFlight;

        public bool SetOperatingMode(string newModeValue)
        {
            if (transmitting || txEnabled) HaltTx();
            if (transmitting) Thread.Sleep(250);        //radio must return to original rx freq first

            // Direct-engine mode only: classic UDP/CAT mode has no outbound "change mode"
            // command in WSJT-X's own UDP API at all -- mode has always been observed FROM
            // WSJT-X's own self-reported Status messages (see "mode = smsg.Mode;" above), never
            // commanded BY Jimmy. Halting Tx here has always been the entire point of this
            // method under UDP mode: clear the way for the operator to switch modes themselves
            // in WSJT-X's own UI, which Jimmy then picks up on the next Status message. Direct
            // mode has no separate UI to fall back to, so it needs a real command -- root-caused
            // live, 2026-08-09, from a report that Alt+M did nothing under direct-engine mode.
            if (_directConnected && newModeValue != mode && !_tierChangeRequestInFlight)
            {
                string tier = newModeValue == "FT4" ? "FT4" : "FT8";
                // Found live (Codex release audit, 2026-08-19): `mode` used to be set right after
                // calling DirectSetTier regardless of whether the engine ever confirmed the
                // switch. Only commit Jimmy's own local FT8/FT4 state once the engine has
                // actually accepted it -- otherwise a failed Alt+M (engine unreachable, connection
                // dropped, timed out, or an explicit ERR reply) would leave Jimmy believing it's
                // on the new mode while the engine -- and the real decode/TX cycle on the air --
                // silently stayed on the old one. See DirectSetTier's own comment for the full
                // reasoning; same bug class already fixed once for Tune (DirectSetTuning,
                // 2026-08-10).
                //
                // Release-audit finding, 2026-08-21: DirectSetTier used to be called
                // synchronously here, on the UI thread -- Alt+M could block keyboard/screen-
                // reader/repaint responsiveness for DirectSendCommand's full bounded connect/
                // read wait whenever the engine host is slow, starting, or hung. Now runs on a
                // background Task; only the (already-computed) state update/notification is
                // marshaled back via BeginInvoke, same pattern as RetuneBand/ToggleTuningProcess.
                _tierChangeRequestInFlight = true;
                // Codex Audit 02 follow-up, 2026-08-21: DirectSetTier now routes through the
                // ordered dispatcher (WsjtxClient.Direct.cs's own class comment) instead of this
                // method opening its own independent Task.Run -- the dispatcher already marshals
                // onComplete onto the UI thread, same as ctrl.BeginInvoke did here before.
                DirectSetTier(tier, ok =>
                {
                    _tierChangeRequestInFlight = false;
                    if (!ok)
                    {
                        // Routed through NotificationCenter (2026-08-19, notification-system-
                        // consistency pass) instead of a raw StatusView.ShowMessage -- same
                        // "headline: reason" ErrorWarningEvent shape as the band-change-failure
                        // conversion (WsjtxClient.BandAudio.cs). Error severity forces Important.
                        Notify?.Publish(new ErrorWarningEvent(ErrorSeverity.Error, $"Mode change to {tier} failed", "engine did not confirm"));
                        return;
                    }
                    mode = tier;
                    newMode = true;
                    // A tier switch changes the T/R period (FT8 15s / FT4 7.5s) -- everything
                    // queued under the old period's timing is stale, same treatment
                    // DirectApplyStatus's own band-change handling already gives a confirmed
                    // band change.
                    trPeriod = null;
                    // Found in the Direct-engine-path review, 2026-08-12: DT samples measured
                    // under one mode's decode correlator aren't directly comparable to the
                    // other mode's -- averaging an FT8 sample together with a fresh FT4 one
                    // at the next boundary would measure something incoherent.
                    // _clockWasAcceptable deliberately NOT reset here (unlike
                    // ConnectDirectEngine's own reset): the operator's actual clock didn't
                    // change just because they switched modes, so the transition gate should
                    // keep its real, current answer rather than being forced to re-announce
                    // "still fine"/"still bad" on every mode toggle.
                    timeOffsets.Clear();
                    timeOffset = 0;
                    _rawDecodeHistory.Clear();
                    if (ctrl.advShowRaw) ShowRawDecodes();
                    ClearCalls(true);
                    logList.Clear();
                    ShowLogged();
                    SetCallInProg(null);
                    ShowStatus();
                });
            }
            return true;
        }

        // Live-testing finding, 2026-08-21: cmdPrompts' own canned prompt text (ShowStatus,
        // WsjtxClient.Display.cs -- ", Control W for list or Alt N for next", etc.) hardcodes
        // BEGINNER-mode-only concepts (the single callListBox and its own Ctrl+W, not Advanced
        // Call Layout's separate TX1/TX2/Raw lists and their own Ctrl+1..4 navigation) -- letting
        // this toggle (and its now-tied-together hotkey-name announcements, see
        // Controller.RefreshHotkeyAccessibleNames) have any effect in advanced mode would be
        // actively misleading, not just unnecessary. Gated the same way NavCallList/NavPending
        // Count's own beginner-only restrictions are (Controller.cs).
        public bool TogglePrompts()
        {
            if (ctrl.advancedCallLayout)
            {
                StatusView.ShowMessage("Command prompts only apply outside Advanced Call Layout.", false);
                return true;
            }
            cmdPrompts = !cmdPrompts;
            promptsChanged = true;
            ctrl.RefreshHotkeyAccessibleNames();
            ShowStatus();
            return true;
        }

        public bool HoldCheckBoxChanged()
        {
            if (callInProg == null) return false;

            DebugOutput($"{Time()} HoldCheckBoxChanged holdCheckBox.Checked:{ctrl.holdCheckBox.Checked} holdCheckBox.Enabled:{ctrl.holdCheckBox.Enabled}");
            if (ctrl.holdCheckBox.Checked /*|| (mode == "MSK144" && modeSupported)*/)
            {
                ctrl.limitLabel.Enabled = false;
                ctrl.repeatLabel.Enabled = false;
                ctrl.timeoutNumUpDown.Enabled = false;
                ctrl.optimizeCheckBox.Enabled = false;
            }
            else
            {
                ctrl.limitLabel.Enabled = true;
                ctrl.repeatLabel.Enabled = true;
                ctrl.timeoutNumUpDown.Enabled = true;
                ctrl.optimizeCheckBox.Enabled = true;
            }
            DebugOutput($"{nl}{Time()} HoldCheckBoxChanged");
            ShowStatus();
            return true;
        }

        //log file mode requested to be (possibly) changed
        public void LogModeChanged(bool enable)
        {
            if (enable == diagLog) return;       //no change requested

            diagLog = SetLogFileState(enable);
        }

        public void TxModeChanged(TxModes tMode)          //tx mode selected
        {
            HaltTuning();
            pendingCqAfterAnalysis = false;
            TxModes prevTxMode = txMode;
            txMode = tMode;
            DebugOutput($"{nl}{Time()} TxModeChanged, txMode:{txMode} cqPaused:{cqPaused} txEnabled:{txEnabled}");
            UpdateModeSelection();
            UpdateListenModeTxPeriod();

            cqPaused = txMode == TxModes.CALL_CQ;

            if (!cqPaused)
            {
                if (txMode == TxModes.CALL_CQ && prevTxMode == TxModes.LISTEN)        //WSJT-X "Enable Tx" button is checked
                {
                    EnableTx();       //set WSJT-X tx to enabled and set "Enable Tx" button state to checked
                    DebugOutput($"{spacer}value:{ctrl.timeoutNumUpDown.Value} callQueue.Count:{callQueue.Count}");
                    if (ctrl.timeoutNumUpDown.Value <= maxCheckTxRepeat && callQueue.Count > 0)
                    {
                        DebugOutput($"{_callQueueStore.CallQueueString()}");
                        EnqueueDecodeMessage dmsg;
                        _callQueueStore.PeekCall(0, out dmsg);
                        bool evenCall = IsEvenCall(dmsg);
                        DebugOutput($"{spacer}evenCall:{evenCall}");
                        if (!ctrl.advancedCallLayout)
                            CheckCallQueuePeriod(!evenCall);        //remove queued calls from wrong time period
                    }
                }

                if (txMode == TxModes.LISTEN && prevTxMode == TxModes.CALL_CQ)        //WSJT-X "Enable Tx" button is checked
                {

                    HaltTx();           //stop CQing immediately
                    DisableTx(true);    //set WSJT-X tx to disable
                    txEnableChanged = true;
                    modePrompt = true;
                }

                CheckNextXmit();
            }

            if (txMode == TxModes.CALL_CQ && opMode == OpModes.ACTIVE && callInProg == null)
            {
                newDirCq = true;
                cqPaused = false;
                SetupCq(true);
            }

            StartStatusTimer();
            UpdateDebug();
        }

        public void TxRepeatChanged()
        {
            UpdateMaxTxRepeat();

            bool evenCall;
            DebugOutput($"{Time()} TxRepeatChanged optimize:{ctrl.optimizeCheckBox.Checked} selected:{(int)ctrl.timeoutNumUpDown.Value} maxTxRepeat:{maxTxRepeat} maxPrevTo:{maxPrevTo} maxAutoGenEnqueue:{maxAutoGenEnqueue}");
            if (ctrl.timeoutNumUpDown.Value <= maxCheckTxRepeat)
            {
                if (callQueue.Count > 0)
                {
                    DebugOutput($"{spacer}check next call");
                    DebugOutput($"{_callQueueStore.CallQueueString()}");
                    EnqueueDecodeMessage dmsg;
                    _callQueueStore.PeekCall(0, out dmsg);
                    evenCall = IsEvenCall(dmsg);
                    DebugOutput($"{spacer}evenCall:{evenCall}");
                    if (!ctrl.advancedCallLayout)
                        CheckCallQueuePeriod(!evenCall);        //remove queued calls from wrong time period
                }
                else
                {
                    DebugOutput($"{spacer}check replyDecode");
                    if (callInProg != null && replyDecode != null)
                    {
                        evenCall = IsEvenCall(replyDecode);
                        DebugOutput($"{spacer}evenCall:{evenCall}");
                        if (!ctrl.advancedCallLayout)
                            CheckCallQueuePeriod(!evenCall);        //remove queued calls from wrong time period
                    }
                }
            }
            UpdateDebug();
        }

        private void UpdateWsjtxOptions()
        {
            if (settingChanged)
            {
                ctrl.WsjtxSettingConfirmed();
                settingChanged = false;
            }
        }

        // Found via live field testing 2026-07-17: WSJT-X Improved 3.1's StatusMessage
        // never reports a real TRPeriod at all (confirmed: every single StatusMessage
        // from this build carried the N/A sentinel for the entire session). Without a
        // fallback, trPeriod (nullable) stayed permanently null for the whole
        // connection -- which silently breaks IsEvenPeriod's even/odd period-parity
        // math (WsjtxClient.cs:1976-1985): with trPeriod null, its final comparison
        // collapses to "null == 0" under C#'s lifted nullable-comparison semantics,
        // which is always false, so IsEvenCall() always returned false too. Observed
        // symptom: raw decodes only ever displayed in TX2, never TX1, no matter how
        // much real signal was present on both. FT8's and FT4's T/R periods are fixed
        // protocol constants, not something that varies station to station -- safe to
        // assume from mode alone the one time WSJT-X itself never tells us, without
        // overwriting a previously-learned real value (WSJT-X's own doc comment says
        // it only sends TRPeriod "when the T/R period is changed" -- omitting it on
        // later messages is expected, not itself a sign anything is wrong).
        private void UpdateTrPeriod(StatusMessage smsg)
        {
            if (smsg.TRPeriod != null)
            {
                //if seconds units, need msec
                trPeriod = (int)smsg.TRPeriod < 1000 ? 1000 * (int)smsg.TRPeriod : (int)smsg.TRPeriod;
            }
            else if (trPeriod == null)
            {
                trPeriod = DefaultTrPeriodMs(smsg.Mode);
                DebugOutput($"{Time()} [PERIOD-FALLBACK] WSJT-X never reported TRPeriod; defaulting trPeriod:{trPeriod} from mode:'{smsg.Mode}'");
            }
        }

        // FT8's and FT4's T/R periods are fixed protocol constants, not something
        // that varies station to station -- pulled out as its own pure function so
        // it's directly unit-testable (UpdateTrPeriod itself needs a live
        // StatusMessage/WsjtxClient instance state to exercise). Public: JimmyTests
        // references Jimmy.exe as a compiled binary with no InternalsVisibleTo, so
        // only public members are reachable from there (see CallQueueRanker.cs's/
        // RowFormatter.cs's own comments on this same constraint).
        public static int DefaultTrPeriodMs(string mode) => mode == "FT4" ? 7500 : 15000;

        private void ResetNego()
        {
            WsjtxMessage.Reinit();                      //NegoState = WAIT;
            DebugOutput($"{nl}{Time()} ResetNego, NegoState:{WsjtxMessage.NegoState}");
            ResetOpMode();
            DebugOutput($"{Time()} Waiting for WSJT-X to run...");
            UpdateRR73();
            ShowStatus();
            UpdateDebug();
        }


        // No response at all from the native engine within the fault window (real WSJT-X-
        // specific wording and a blocking modal here used to be one root cause of Jimmy's
        // whole window freezing, confirmed live 2026-08-07/08 in a DIFFERENT but related spot
        // -- CheckWsjtxRunning's own modal, since removed -- so this stays a non-blocking
        // notification rather than a MessageBox.Show, and just keeps waiting/retrying instead
        // of requiring a dialog to be dismissed first.
        // Found live, since the Direct-only cutover: NegoState==INITIAL is now unreachable in
        // any real session -- Direct mode's own ConnectDirectEngine/DirectPollTick
        // (WsjtxClient.Direct.cs) only ever set NegoState to RECD or WAIT, never INITIAL/SENT/
        // FAIL (those three were exclusively the classic UDP handshake's own states). This
        // method's own warning-and-retry body has therefore been a no-op in production since
        // the 2026-08-12 Direct-only parity pass, predating this cleanup pass entirely -- left
        // as-is rather than "fixed" here: giving Direct mode a genuine "still waiting to
        // connect" warning is a real, separate feature decision (what should it say, when
        // should it fire), not a UDP-transport-removal cleanup. Still called from a live,
        // always-wired site (Controller.cs's initialConnFaultTimer, started from
        // OptionsDlgClosed/HelpClosed), so the method itself is not dead code -- only this
        // specific INITIAL check inside it is unreachable today.
        public void ConnectionDialog()
        {
            ctrl.initialConnFaultTimer.Stop();
            if (WsjtxMessage.NegoState == WsjtxMessage.NegoStates.INITIAL)
            {
                Notify.Publish(new ErrorWarningEvent(ErrorSeverity.Warning, "Native engine",
                    "no response yet -- still waiting."));
                ctrl.initialConnFaultTimer.Start();
            }
        }

        // Retunes through the engine's own SET_FREQUENCY command when a real frequency is given
        // (band changes) -- see DirectSetFrequency's own comment (WsjtxClient.Direct.cs); a bare
        // txFirst toggle (freq==0, from ToggleTxFirst) is pure Jimmy-side TX-sequencing state
        // with nothing to send anywhere.
        private void SetBandTxFirst(uint freq, bool state, string caller = "")
        {
            string bandLabel = freq > 0 ? (FreqToBandStr(freq / 1000.0 / 1e6) ?? $"{freq / 1000}kHz") : "none";
            DebugOutput($"{Time()} [BAND-AUDIT] SetBandTxFirst: caller:{caller} freq:{freq} band:{bandLabel} txFirst:{state} bandIdx:{bandIdx}");

            if (freq > 0 && ctrl.Radio.Mode == RadioControlMode.HamlibRigctld)
                DirectSetFrequency(freq, bandLabel, "USB", null);
        }

        // HeartbeatNotRecd (heartbeatRecdTimer's own Tick handler) and CloseAllUdp (its own
        // UDP-socket teardown) were removed 2026-08-18: heartbeatRecdTimer.Start() was only ever
        // called from inside the now-deleted classic UDP dispatcher, so the timer could never
        // actually fire, and CloseAllUdp's only job was closing sockets WsjtxProtocolAdapter
        // owned -- that class is deleted too (nothing left ever opens a UDP socket at all, so
        // there is never anything left to tear down). See WsjtxClient.Direct.cs's
        // ConnectDirectEngine for the one call site this had -- removed there in the same pass.
    }
}
