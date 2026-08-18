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
    // TEST/REPLAY-ONLY IN CURRENT PRODUCTION. Jimmy Next's sole production transport is
    // Jimmy Test -> Direct (control port) -> EngineHost -> Nexus (WsjtxClient.Direct.cs).
    // Controller.Form_Load's ApplyEngineMode() call always launches the native engine host
    // and always connects to it via ConnectDirectEngine() outside TestModeGuard.IsTestMode
    // -- there is no remaining operator choice or code path that reaches this file's
    // classic WSJT-X UDP protocol handling in a real session. It is exercised ONLY when
    // TestModeGuard.IsTestMode is true, by run_replay_tests.bat's JimmyReplay.py driving a
    // real Jimmy Test.exe process over real UDP packets, simulating a standard WSJT-X peer
    // -- genuinely load-bearing test infrastructure, not dead code, kept exactly because
    // that harness (and the message-parsing library it exercises, WsjtxUdpLib) still needs
    // it to work. See ConnectNativeEngine's own comment below for the entry point.
    // ═══════════════════════════════════════════════════════════════════════════════════
    public partial class WsjtxClient
    {
        // Stage A3: body moved to WsjtxProtocolAdapter.ReceiveCallback (Protocol/
        // WsjtxProtocolAdapter.cs) -- it had zero true WsjtxClient-instance dependency
        // beyond the socket-receive state that now lives there. Kept here as a thin
        // wrapper since asyncCallback = new AsyncCallback(ReceiveCallback) below needs a
        // method matching AsyncCallback's signature.
        public void ReceiveCallback(IAsyncResult ar) => _protocolAdapter.ReceiveCallback(ar);

        // Retries ConnectNativeEngine on a cooldown (not every 10ms tick) while stuck in WAIT
        // -- only reachable if the UDP listener genuinely failed to open (e.g. a port
        // conflict); a normal connect never leaves NegoState at WAIT in the first place.
        private DateTime _lastNativeConnectRetry = DateTime.MinValue;
        private static readonly TimeSpan NativeConnectRetryCooldown = TimeSpan.FromSeconds(5);

        public void UdpLoop()
        {
            // Called once per mainLoopTimer tick unconditionally (Controller.cs) regardless of
            // transport -- the _directConnected guard immediately below is what makes it a no-op
            // in real production (see this file's own top-of-file banner comment). Kept as an
            // unconditional call site, not itself gated by TestModeGuard.IsTestMode, so replay
            // tests don't need Controller.cs to know which mode is active.
            //
            // Structural mutual-exclusivity fix, 2026-08-10: this whole method must be a no-op
            // whenever Direct mode (WsjtxClient.Direct.cs) is the active transport to the engine.
            // Before this check existed, the two pipelines could both end up live at once: this
            // method's own guard below only ever checked WsjtxMessage.NegoState, a single SHARED
            // flag both pipelines read AND write -- Direct mode's own first successful poll sets
            // NegoState to RECD (needed for unrelated things elsewhere that gate on it), which
            // ALSO satisfied this method's "not still waiting" condition, letting it fall through
            // to udpClient.BeginReceive(...) if udpClient happened to be non-null for any reason
            // (e.g. left over from an earlier UDP-mode session before Direct was selected).
            // Root-caused live, 2026-08-10, from a real QSO where Tx got disabled mid-contact by
            // safety logic (WsjtxClient.cs's ProcessTxEnd/consecTxCount) that only exists in the
            // UDP-only status-message handler below -- it could only have fired if this loop was
            // actually receiving and processing real messages while nominally in Direct mode.
            // _directConnected is Direct mode's own authoritative flag (unlike NegoState, never
            // written by the UDP side), so gating on it here, first, makes the two transports
            // structurally exclusive regardless of NegoState's shared value or udpClient's state.
            if (_directConnected) return;

            if (WsjtxMessage.NegoState == WsjtxMessage.NegoStates.WAIT)
            {
                if (_nativeEngineAddr != null && DateTime.UtcNow - _lastNativeConnectRetry >= NativeConnectRetryCooldown)
                {
                    _lastNativeConnectRetry = DateTime.UtcNow;
                    ConnectNativeEngine(_nativeEngineAddr, _nativeEnginePort);
                }
                return;
            }

            if (wsjtxClosing)
            {
                DebugOutput($"{nl}{Time()} native engine connection closing");
                ResetNego();
                CloseAllUdp();
                wsjtxClosing = false;
                // Wave 1 of the notification architecture (WSJTX_Controller/Notify/):
                // default template "WSJT-X closed", Important priority -- byte-identical
                // to the direct ShowMessage call this replaces.
                Notify.Publish(new ConnectionClosedEvent());
            }

            //timer expires at 11-12 msec minimum (due to OS limitations)
            if (messageRecd)
            {
                if (datagram != null) Update();
                messageRecd = false;
                recvStarted = false;
            }
            // Receive a UDP datagram
            if (!recvStarted)
            {
                if (udpClient == null || WsjtxMessage.NegoState == WsjtxMessage.NegoStates.WAIT) return;
                udpClient.BeginReceive(asyncCallback, udpSt);
                recvStarted = true;
            }
        }

        // ONLY called from Controller.ApplyEngineMode()'s TestModeGuard.IsTestMode branch --
        // real production always calls ConnectDirectEngine() (WsjtxClient.Direct.cs) instead.
        // Kept and still genuinely exercised: it's what a replay-test run's real Jimmy Test.exe
        // process uses to open the real UDP socket JimmyReplay.py sends packets to (see this
        // file's own top-of-file banner comment) -- not a dead alternate production path.
        //
        // Jimmy Native's own connection path -- deliberately bypasses CheckWsjtxRunning()
        // entirely rather than reusing it. That method exists to detect and reconnect to a
        // SEPARATE, already-running real WSJT-X.exe: it reads WSJT-X's own ini file for its
        // configured UDP server address (irrelevant here, and can be flat wrong -- e.g.
        // multicast -- if a real WSJT-X install coexists on the same PC), unconditionally
        // blocks the UI thread for a flat Thread.Sleep(3000), and on failure pops a modal
        // MessageBox.Show with WSJT-X-specific instructions that make no sense in Native
        // mode. Confirmed live, 2026-08-07/08: this whole path -- not anything in
        // NativeEngineClient.Launch itself -- was the actual cause of Jimmy's window going
        // fully unresponsive (busy cursor, no keyboard/mouse response) the instant Native
        // mode was enabled, since ApplyEngineMode() creates the same WSJT-X.lock file
        // CheckWsjtxRunning() uses as its "a WSJT-X is running" signal, guaranteeing this
        // path fires via mainLoopTimer's very next tick. Native mode already knows its own
        // UDP endpoint exactly -- NativeEngineClient.Launch was told to send to
        // 127.0.0.1:<jimmyPort> via --jimmy-addr -- so there is nothing to detect: just open
        // that exact socket directly and move straight to normal heartbeat negotiation.
        private IPAddress _nativeEngineAddr;
        private int _nativeEnginePort;

        public void ConnectNativeEngine(IPAddress addr, int nativeEnginePort)
        {
            _nativeEngineAddr = addr;
            _nativeEnginePort = nativeEnginePort;

            ResetNego();
            CloseAllUdp();
            ipAddress = addr;
            port = nativeEnginePort;
            multicast = false;

            if (!_protocolAdapter.TryOpenReceiveSocket(out Exception openErr))
            {
                DebugOutput($"{spacer}unable to open native engine udpClient:{openErr}");
                Notify.Publish(new ErrorWarningEvent(ErrorSeverity.Error, "Native engine",
                    $"couldn't open the UDP listener on {addr}:{nativeEnginePort}: {openErr.Message}"));
                return;         //stays in NegoState.WAIT; UdpLoop's WAIT branch retries this
                                 //same call on a cooldown until it succeeds
            }

            DebugOutput($"{spacer}opened udpClient:{udpClient} (native engine, {addr}:{nativeEnginePort})");
            udpSt = new UdpState { e = endPoint, u = udpClient };
            asyncCallback = new AsyncCallback(ReceiveCallback);
            WsjtxMessage.NegoState = WsjtxMessage.NegoStates.INITIAL;
            DebugOutput($"{spacer}NegoState:{WsjtxMessage.NegoState}");

            suspendComm = false;
            ctrl.initialConnFaultTimer.Interval = 3 * heartbeatInterval * 1000;
            ctrl.initialConnFaultTimer.Start();
        }

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
            if (_directConnected && newModeValue != mode)
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
                if (!DirectSetTier(tier))
                {
                    // Routed through NotificationCenter (2026-08-19, notification-system-
                    // consistency pass) instead of a raw StatusView.ShowMessage -- same
                    // "headline: reason" ErrorWarningEvent shape as the band-change-failure
                    // conversion (WsjtxClient.BandAudio.cs). Error severity forces Important.
                    Notify?.Publish(new ErrorWarningEvent(ErrorSeverity.Error, $"Mode change to {tier} failed", "engine did not confirm"));
                    return true;
                }
                mode = tier;
                newMode = true;
                // A tier switch changes the T/R period (FT8 15s / FT4 7.5s) -- everything queued
                // under the old period's timing is stale, same treatment DirectApplyStatus's own
                // band-change handling already gives a confirmed band change.
                trPeriod = null;
                // Found in the Direct-engine-path review, 2026-08-12: DT samples measured under
                // one mode's decode correlator aren't directly comparable to the other mode's --
                // averaging an FT8 sample together with a fresh FT4 one at the next boundary
                // would measure something incoherent. _clockWasAcceptable deliberately NOT
                // reset here (unlike ConnectDirectEngine's own reset): the operator's actual
                // clock didn't change just because they switched modes, so the transition gate
                // should keep its real, current answer rather than being forced to re-announce
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
            }
            return true;
        }

        public bool TogglePrompts()
        {
            cmdPrompts = !cmdPrompts;
            promptsChanged = true;
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


        private void Update()
        {
            if (suspendComm) return;

            // Stage A3: the parse call itself moved to WsjtxProtocolAdapter.TryParse --
            // this is now just the error-reporting/logging around it (Time()/
            // DatagramString() are WsjtxClient-instance methods, so that part stays).
            msg = _protocolAdapter.TryParse(datagram, out ParseFailureException parseEx);
            if (parseEx != null)
            {
                //File.WriteAllBytes($"{ex.MessageType}.couldnotparse.bin", ex.Datagram);
                DebugOutput($"{Time()} ERROR: Parse failure {parseEx.InnerException.Message}");
                DebugOutput($"datagram[{datagram.Length}]: {DatagramString(datagram)}");
                return;
            }

            if (msg == null)
            {
                DebugOutput($"{Time()} ERROR: null message, datagram[{datagram.Length}]: {DatagramString(datagram)}");
                return;
            }

            //rec'd first HeartbeatMessage
            //check version, send requested schema version
            //request a StatusMessage
            //go from INIT to SENT state
            //
            // Real native-engine sessions never reply to the engine's Heartbeat at all -- same
            // class of bug as the cmd:7 removal below, found the same way (root-caused live,
            // 2026-08-08). tempo-audio/src/service.rs sends its one-shot opening Heartbeat for
            // passive loggers like GridTracker that never answer; replying to it -- opening a
            // second UdpClient and sending a real Heartbeat back -- reliably crashes the engine
            // (heap corruption / access violation, confirmed via a standalone repro with zero
            // Jimmy C# code involved: the SAME crash reproduces from a bare Python script doing
            // nothing but this one reply). Skipping this block leaves NegoState at INITIAL, which
            // is exactly the "missed heartbeat" case the StatusMessage branch below (the
            // 2026-08-06/07 slow-CAT fix) already handles -- that fallback does everything this
            // block would have (stops the connection-fault timer, opens udpClient2, captures the
            // negotiated schema version, promotes to RECD) purely from the engine's normal ~15s
            // Status broadcasts, with nothing ever sent back to it.
            //
            // Gated on TestModeGuard.IsTestMode (send the reply ONLY under test), not on
            // _nativeEngineAddr: ConnectNativeEngine is the one connection path used both for the
            // real spawned engine AND for JimmyReplay.py's simulated WSJT-X during replay tests
            // (Controller.cs calls it unconditionally; only the actual engine-process spawn is
            // skipped under TestModeGuard.IsTestMode) -- _nativeEngineAddr is set in both cases
            // alike, so it can't tell them apart. The replay harness genuinely expects a standard
            // Heartbeat reply (that's what real WSJT-X does), so IsTestMode is what actually
            // distinguishes "a real jimmy-engine-host.exe that crashes on this" from "a test
            // simulating standard protocol behavior" -- confirmed live, 2026-08-08, when gating on
            // _nativeEngineAddr instead broke JimmyReplay.py's handshake outright.
            if (TestModeGuard.IsTestMode && msg.GetType().Name == "HeartbeatMessage" && (WsjtxMessage.NegoState == WsjtxMessage.NegoStates.INITIAL || WsjtxMessage.NegoState == WsjtxMessage.NegoStates.FAIL))
            {
                ctrl.initialConnFaultTimer.Stop();             //stop connection fault dialog
                HeartbeatMessage imsg = (HeartbeatMessage)msg;
                DebugOutput($"{Time()} Heartbeat received{nl}{imsg}");

                string[] sa = imsg.Revision.Split(' '); //may contain other info, including URL

                string rev = sa[0];
                int.TryParse(rev, out wsjtxRevision);

                string testVer = sa.Length >= 2 ? sa[1] : "42";
                int.TryParse(testVer, out wsjtxTestVer);

                curVerBld = $"{imsg.Version}/{rev}";
                // Stage A7: previously only captured on a 2nd incoming Heartbeat (that
                // wait-for-a-2nd-Heartbeat step is now removed -- see the SENT->RECD
                // comment below); captured here instead so it's never silently skipped.
                WsjtxMessage.NegotiatedSchemaVersion = imsg.SchemaVersion;

                if (udpClient2 != null)
                {
                    udpClient2.Close();
                    udpClient2 = null;
                    DebugOutput($"{spacer}closed udpClient2:{udpClient2}");
                }

                var tmsg = new HeartbeatMessage();
                tmsg.SchemaVersion = WsjtxMessage.PgmSchemaVersion;
                tmsg.MaxSchemaNumber = (uint)WsjtxMessage.PgmSchemaVersion;
                tmsg.Id = WsjtxMessage.UniqueId;
                tmsg.Version = WsjtxMessage.PgmVersion;
                tmsg.Revision = WsjtxMessage.PgmRevision;

                ba = tmsg.GetBytes();
                udpClient2 = new UdpClient();
                udpClient2.Connect(fromEp);
                udpClient2.Send(ba, ba.Length);
                WsjtxMessage.NegoState = WsjtxMessage.NegoStates.SENT;
                UpdateDebug();
                DebugOutput($"{spacer}NegoState:{WsjtxMessage.NegoState}");
                DebugOutput($"{Time()} Heartbeat reply sent{nl}{tmsg}");
                ShowStatus();
                StatusView.ShowMessage("WSJT-X responding", false);

                if (wsjtxRevision == 102 && wsjtxTestVer < 72) DeleteLotwCsv();        //fixed, reason for WSJT-X crashing at startup because of NVDA determined

                // Native-only: no capability probe of any kind. The old cmd:7 "Ack Req" exchange
                // existed purely to detect whether a separately-running real WSJT-X-family process
                // also spoke Andy WM8Q's non-standard Compatibility Layer -- meaningless once the
                // only peer is jimmy-engine-host.exe, which Jimmy launched itself and already knows
                // the exact capabilities of. Sending it was also confirmed live, 2026-08-08 (tester
                // log log_8-7-2026.txt), to reliably crash the engine host within ~1 second: cmd:7
                // is a REAL, standard EnableTxMessage (not a value packed inside an Andy-fork-only
                // sub-command byte Nexus's dispatch silently ignores), and the engine tried to act
                // on it as a genuine transmit-enable request.
                UpdateDebug();
                return;
            }

            //while in INIT or SENT state:
            //get minimal info from StatusMessage needed for faster startup
            //and for special case of ack msg returned by WSJT-X after req for StatusMessage
            //check for no call sign or grid, exit if so;
            //calculate best offset frequency;
            //also get decode offset frequencies for best offest calculation
            if (WsjtxMessage.NegoState != WsjtxMessage.NegoStates.RECD)
            {
                if (msg.GetType().Name == "StatusMessage")
                {
                    StatusMessage smsg = (StatusMessage)msg;
                    DebugOutput($"{nl}{Time()}{nl}{smsg}{nl}{spacer}NegoState:{WsjtxMessage.NegoState} opMode:{opMode} smsg.TRPeriod:'{smsg.TRPeriod}'");

                    txFirst = smsg.TxFirst;
                    UpdateCallListAccessibleName();     // update RX1/TX1 labels as soon as txFirst is known

                    UpdateTrPeriod(smsg);

                    if (trPeriod != null)
                    {
                        decoding = smsg.Decoding;
                        DebugOutput($"{spacer}decoding:{decoding} lastDecoding:{lastDecoding} decodeCycle:{decodeCycle} trPeriod:{trPeriod}");
                        if (decoding != lastDecoding)
                        {
                            if (decoding)
                            {
                                if (decodeCycle == 0)
                                {
                                    SetPeriodState();
                                }
                                if (ctrl.advancedCallLayout)
                                {
                                    _rawDecodeHistory.Clear();
                                    if (ctrl.advShowRaw) ShowRawDecodes();
                                }
                            }
                            else
                            {
                                postDecodeTimer.Stop();
                                postDecodeTimer.Start();                    //restart timer at every decode, will time out after last decode
                                DebugOutput($"{spacer}postDecodeTimer start, decodeNum:{decodeNum} decodeCycle:{decodeCycle}");

                                if (lastDecoding != null)           //need to start with decoding = true
                                {
                                    if (decodeCycle == 0)
                                    {
                                        //first calcluation of best offset
                                        if (!skipFirstDecodeSeries)
                                        {
                                            DebugOutput($"{spacer}audioOffsets.Count:{audioOffsets.Count}");
                                            CalcBestOffset(audioOffsets, period, false);
                                            CalcAvgTimeOffset(false);
                                        }
                                    }
                                    decodeCycle++;
                                    DebugOutput($"{spacer}next decodeCycle:{decodeCycle}");
                                }
                            }
                        }
                        lastDecoding = decoding;
                    }

                    txEnabledConf = smsg.TxEnabled;
                    if (txEnabledConf != lastTxEnabled)         //lastTxEnabled can be null
                    {
                        if (txEnabledConf)
                        {
                            StatusView.ShowMessage("Not ready yet... please wait", true);
                        }
                    }
                    lastTxEnabled = txEnabledConf;

                    wsjtxTxEnableButton = smsg.TxEnableButton;          //keep WSJT-X "Enable Tx" button state current
                    UpdateDblClkTip();

                    //marker2
                    string mode = smsg.Mode;
                    if (mode != lastMode)
                    {
                        DebugOutput($"{spacer}mode changed, decodeCycle:{CurrentDecodeCycleString()} lastDecoding:{lastDecoding}");
                        ClearAudioOffsets();
                        decodeCycle = 0;
                        consecNoDecodes = 0;
                    }
                    lastMode = mode;

                    dialFrequency = smsg.DialFrequency;
                    if (lastDialFrequency == null) lastDialFrequency = dialFrequency;
                    if (lastDialFrequency != null && (Math.Abs((float)lastDialFrequency - (float)dialFrequency) > freqChangeThreshold))
                    {
                        DebugOutput($"{spacer}frequency changed, decodeCycle:{CurrentDecodeCycleString()} lastDecoding:{lastDecoding}");
                        ClearAudioOffsets();
                    }
                    lastDialFrequency = dialFrequency;

                    if (myContinent != smsg.MyContinent)
                    {
                        myContinent = smsg.MyContinent;
                        ctrl.replyLocalCheckBox.Text = (myContinent == null ? "loc" : myContinent);
                        DebugOutput($"{spacer}myContinent changed:{myContinent}");
                    }

                    UpdateRR73();
                    specOp = (int)smsg.SpecialOperationMode;

                    configuration = smsg.ConfigurationName;
                    if (!CheckMyCall(smsg)) return;
                    DebugOutput($"{spacer}myCall:'{myCall}' myGrid:'{myGrid}' mode:'{mode}' specOp:'{specOp}' configuration:{configuration} check:{smsg.Check}");
                    UpdateDebug();

                    // Stage A7: normal operation now depends only on receiving the first
                    // standard StatusMessage broadcast while SENT, never on any
                    // non-standard acknowledgment (Blueprint §6/§19 -- replaces the old
                    // "wait for a 2nd Heartbeat, then send cmd:7 and go straight to RECD"
                    // sequence). CheckMyCall's return above already guards this to the
                    // first *valid* StatusMessage (real callsign/grid configured in
                    // WSJT-X), not literally the first one of any kind.
                    if (WsjtxMessage.NegoState == WsjtxMessage.NegoStates.SENT ||
                        WsjtxMessage.NegoState == WsjtxMessage.NegoStates.INITIAL)
                    {
                        // Self-sufficiency plan Phase 5: the native engine host (Nexus's
                        // run_radio) sends its opening Heartbeat exactly ONCE, at startup --
                        // unlike real WSJT-X, it never repeats it (tempo-audio/src/service.rs's
                        // own comment: "the opening Heartbeat... harmless if unheard", written
                        // for passive loggers like GridTracker that don't need a strict
                        // handshake, not for a stateful negotiation like this one). If the
                        // engine's real CAT/rig-open sequence takes long enough that Jimmy's own
                        // UDP socket isn't listening yet when that one-shot Heartbeat goes out,
                        // it is gone forever and the Heartbeat-triggered INITIAL->SENT transition
                        // above never fires -- confirmed live, 2026-08-06/07: Jimmy stuck at
                        // "WSJT-X detected" indefinitely while real Status messages kept arriving
                        // normally every ~15s the whole time (a real TS-590SG's CAT-open over a
                        // real serial port is far slower than the dummy-rig bench tests this was
                        // validated against). A valid Status message proves a live,
                        // WSJT-X-protocol-speaking peer just as well as a Heartbeat would, so
                        // promote straight to RECD from INITIAL too -- but first do the setup
                        // steps the (skipped) Heartbeat branch above would have done: open
                        // udpClient2 (every outbound Reply/HaltTx/SetBandTxFirst/etc. send is a
                        // silent no-op without it -- see SetBandTxFirst's own udpClient2==null
                        // guard) and stop the connection-fault dialog's timer so it doesn't still
                        // pop up "No response from WSJT-X" after we just connected via Status.
                        if (WsjtxMessage.NegoState == WsjtxMessage.NegoStates.INITIAL)
                        {
                            ctrl.initialConnFaultTimer.Stop();
                            if (udpClient2 == null)
                            {
                                udpClient2 = new UdpClient();
                                udpClient2.Connect(fromEp);
                            }
                            WsjtxMessage.NegotiatedSchemaVersion = smsg.SchemaVersion;
                            DebugOutput($"{Time()} First valid Status received while still INITIAL (no Heartbeat seen) -- promoting straight to RECD");
                        }
                        else
                        {
                            DebugOutput($"{Time()} First valid Status received");
                        }
                        WsjtxMessage.NegoState = WsjtxMessage.NegoStates.RECD;
                        DebugOutput($"{spacer}NegoState -> RECD");

                        HaltTx();       //sync up WSJT-X button state

                        if (bandIdx == null)
                        {
                            SetOperatingMode("FT8");            //after halt
                            Thread.Sleep(250);
                            // Stage A7 field-testing fix, 2026-07-17: this block used to
                            // live in a scope with no local "mode" variable, so "mode =
                            // ..." unambiguously meant the class field. Moved here, it's
                            // nested inside the StatusMessage handler's own
                            // "string mode = smsg.Mode;" local, which shadows the field --
                            // an unqualified "mode = "FT8"" silently updated only the
                            // local, leaving the class field at its previous value
                            // (empty string on a fresh connect). bandToFreq() then failed
                            // its own freqsDict.Keys.Contains(mode) check against that
                            // stale field value and returned null, and the unchecked
                            // (uint) cast on the caller's side threw
                            // InvalidOperationException ("Nullable object must have a
                            // value") -- crashed on every first-connect handshake, not
                            // just against WSJT-X Improved 3.1. this.mode makes the
                            // target explicit regardless of the local shadow.
                            this.mode = "FT8";
                            bandIdx = FreqToBandIdx(dialFrequency / 1e6);       //can be null if unknown
                            if (bandIdx == null) bandIdx = 5;
                            SetBandTxFirst((uint)(bandToFreq(bandIdx) * 1000), txFirst, "InitialConnect");
                            Thread.Sleep(250);
                        }
                    }
                }

                if (msg.GetType().Name == "EnqueueDecodeMessage")
                {
                    EnqueueDecodeMessage qmsg = (EnqueueDecodeMessage)msg;
                    if (qmsg.DeltaFrequency > offsetLoLimit && qmsg.DeltaFrequency < offsetHiLimit) audioOffsets.Add(qmsg.DeltaFrequency);
                    timeOffsets.Add(qmsg.DeltaTime);

                    if (!qmsg.AutoGen)
                        StatusView.ShowMessage("Not ready yet... please wait", true);
                }
            }

            //************
            //CloseMessage
            //************
            if (msg.GetType().Name == "CloseMessage")
            {
                DebugOutput($"{nl}{Time()} CloseMessage rec'd{nl}{Time()}{nl}{msg}");
                if (WsjtxMessage.NegoState != WsjtxMessage.NegoStates.WAIT) wsjtxClosing = true;
                DebugOutput($"{spacer}NegoState:{WsjtxMessage.NegoState} wsjtxClosing:{wsjtxClosing}");
                return;
            }

            //****************
            //HeartbeatMessage
            //****************
            //in case 'Monitor' disabled, get StatusMessages
            if (msg.GetType().Name == "HeartbeatMessage")
            {
                if (opMode != OpModes.ACTIVE) DebugOutput($"{nl}{Time()} WSJT-X event, heartbeat rec'd:{nl}{msg}");
                // Native-only: no cmd:7 "Ack Req" reply on any heartbeat, initial or repeat --
                // see the matching comment at the NegoState==WAIT branch above. Confirmed live,
                // 2026-08-08 (tester log log_8-7-2026.txt): this site used to fire on EVERY
                // heartbeat the peer sends, not just the first -- Nexus's real run_radio sends
                // its own periodic Heartbeat roughly every 15-20s, and replying to that SECOND
                // one with the same crash-inducing probe every time is why the engine reliably
                // died ~19-21s after connecting, regardless of radio config, audio device, or
                // queue content.

                heartbeatRecdTimer.Stop();
                if (!debug)
                {
                    heartbeatRecdTimer.Start();
                    if (opMode != OpModes.ACTIVE) DebugOutput($"{spacer}heartbeatRecdTimer restarted");
                }
            }

            // A StatusMessage is just as valid a "the peer is alive" signal as a Heartbeat --
            // arguably more so, since it carries real state (mode/decoding/TR period) a dead
            // peer couldn't produce. Needed because the native engine sends its Heartbeat
            // exactly ONCE at startup and never repeats it (see the comment above): without
            // this, heartbeatRecdTimer's own 60s timeout (4 * heartbeatInterval) fires on every
            // single native session right on schedule, tears down a perfectly healthy
            // connection, and plays the "disconnected" alarm for nothing -- root-caused live,
            // 2026-08-08 after a real session ran stably for minutes and still heard that alarm
            // once, ~60s in. Real WSJT-X keeps sending its own periodic Heartbeat too, so this
            // is a harmless, redundant reset in that case -- not native-mode-only.
            if (msg.GetType().Name == "StatusMessage")
            {
                heartbeatRecdTimer.Stop();
                if (!debug) heartbeatRecdTimer.Start();
            }

            if (WsjtxMessage.NegoState == WsjtxMessage.NegoStates.RECD)
            {
                if (modeSupported)
                {
                    //********************
                    //EnqueueDecodeMessage / standard DecodeMessage
                    //********************
                    //only resulting action is to add call to callQueue, optionally restart queue
                    //
                    // Found via live A7 field testing 2026-07-17: stock WSJT-X and WSJT-X
                    // Improved send the standard "DecodeMessage" (msg type 2), never the
                    // non-standard "EnqueueDecodeMessage" (msg type 18, Andy WM8Q's fork
                    // only) this branch used to require exclusively -- meaning Jimmy never
                    // processed a single decode from any non-Andy-fork build. Adapting a
                    // standard DecodeMessage into an EnqueueDecodeMessage shell (via
                    // FromStandardDecode) lets it flow through the exact same pipeline below
                    // unchanged -- see that method's own comment for the field-by-field
                    // justification of why this is safe.
                    if ((msg.GetType().Name == "EnqueueDecodeMessage" || msg.GetType().Name == "DecodeMessage") && myCall != null)
                    {
                        EnqueueDecodeMessage dmsg = msg as EnqueueDecodeMessage
                            ?? EnqueueDecodeMessage.FromStandardDecode((DecodeMessage)msg);
                        if (dmsg.AutoGen && ctrl.advancedCallLayout)
                        {
                            while (_rawDecodeHistory.Count >= ctrl.rawMaxRows)
                                _rawDecodeHistory.RemoveAt(0);
                            _rawDecodeHistory.Add(dmsg);
                            if (ctrl.advShowRaw) ShowRawDecodes();
                        }
                        if (!dmsg.Message.Contains(";"))
                        {
                            //normal (not "special operating activity") message
                            ProcessDecodeMsg(dmsg, false);
                        }
                        else
                        {
                            //fox/hound-style (multi-target) message: process as two separate decodes (note: full f/h mode not supported)
                            // 0    1     2    3   4
                            //W1AW RR73; WM8Q T2C -02
                            string msg = dmsg.Message;
                            DebugOutput($"{nl}{Time()} F/H msg detected: {msg}");
                            string[] words = msg.Replace(";", "").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (words.Length != 5) return;

                            EnqueueDecodeMessage dmsg2 = dmsg.DeepCopy();       //prevent aliasing

                            dmsg.Message = $"{words[0]} {words[3]} {words[1]}";
                            DebugOutput($"{spacer}processing first msg: {dmsg.Message}");
                            ProcessDecodeMsg(dmsg, true);

                            dmsg2.Message = $"{words[2]} {words[3]} {words[4]}";
                            DebugOutput($"{spacer}processing second msg: {dmsg2.Message}");
                            ProcessDecodeMsg(dmsg2, true);
                        }
                        return;
                    }
                }


                //*************
                //StatusMessage
                //*************
                if (msg.GetType().Name == "StatusMessage")
                {
                    StatusMessage smsg = (StatusMessage)msg;
                    DateTime dtNow = DateTime.UtcNow;
                    bool modeChanged = false;
                    if (opMode < OpModes.ACTIVE) DebugOutput($"{Time()}{nl}{msg}{nl}{spacer}opMode:{opMode} cqPaused:{cqPaused} myCall:'{myCall}'");
                    qsoStateConf = smsg.CurQsoState();
                    txEnabledConf = smsg.TxEnabled;
                    dxCall = smsg.DxCall;                               //unreliable info, can be edited manually
                    if (dxCall == "") dxCall = null;
                    mode = smsg.Mode;
                    specOp = (int)smsg.SpecialOperationMode;
                    txMsg = WsjtxMessage.RemoveAngleBrackets(smsg.LastTxMsg);        //msg from last Tx
                    txFirst = smsg.TxFirst;
                    UpdateCallListAccessibleName();     // update RX1/TX1 labels as soon as txFirst is known
                    decoding = smsg.Decoding;
                    transmitting = smsg.Transmitting;
                    int? prevBandIdx = bandIdx;
                    dialFrequency = smsg.DialFrequency;
                    bandIdx = FreqToBandIdx(dialFrequency / 1e6);       //can be null if unknown
                    _pendingBandIdx = null;       // real confirmation arrived -- drop any optimistic guess
                    txOffset = smsg.TxDF;
                    wsjtxTxEnableButton = smsg.TxEnableButton;
                    UpdateDblClkTip();
                    metricUnits = smsg.MetricUnits;
                    wsjtxResultCode = smsg.ResultCode != null ? (int)smsg.ResultCode : 0;
                    statusDetail = smsg.Detail;     //can be null

                    if (lastXmitting == null) lastXmitting = transmitting;     //initialize
                    if (lastQsoState == WsjtxMessage.QsoStates.INVALID) lastQsoState = qsoStateConf;    //initialize WSJT-X user QSO state change detection
                    if (lastDecoding == null) lastDecoding = decoding;     //initialize
                    if (lastTxWatchdog == null) lastTxWatchdog = smsg.TxWatchdog;   //initialize
                    if (lastTxFirst == null) lastTxFirst = txFirst;                     //initialize

                    if (txMsg != lastStatusTxMsg)
                    {
                        if (transmitting)
                        {
                            curTxMsg = txMsg;       //tx interrupted with a different call
                            curTxPayload = null;
                            DebugOutput($"{nl}{Time()} WSJT-X event, txMsg changed, curTxMsg:{curTxMsg} curTxPayload:'{curTxPayload}'");
                            if (!tuning) ShowStatus();
                        }
                        lastStatusTxMsg = txMsg;
                    }


                    UpdateTrPeriod(smsg);

                    //*********************************
                    //detect WSJT-X xmit start/end ASAP
                    //*********************************
                    if (trPeriod != null && transmitting != lastXmitting)
                    {
                        if (transmitting)
                        {
                            StartProcessDecodeTimer();
                            ProcessTxStart();
                            if (firstDecodeTime == DateTime.MinValue) firstDecodeTime = DateTime.UtcNow;       //start counting until WSJT-X watchdog timer set
                        }
                        else                //end of transmit
                        {
                            ProcessTxEnd();
                        }
                        lastXmitting = transmitting;
                    }

                    //***********************
                    //check myCall and myGrid
                    //***********************
                    if (myCall == null || myGrid == null)
                    {
                        CheckMyCall(smsg);
                    }
                    else
                    {
                        if (myCall != smsg.DeCall || myGrid != smsg.DeGrid)
                        {
                            DebugOutput($"{nl}{Time()} WSJT-X event, Call or grid changed, myCall:{smsg.DeCall} (was {myCall}) myGrid:{smsg.DeGrid} (was {myGrid})");
                            myCall = smsg.DeCall;
                            myGrid = smsg.DeGrid;

                            ResetOpMode();
                            Pause(true, true);
                            SetCallInProg(null);    //not calling anyone
                        }
                    }

                    //*****************
                    //check myContinent
                    //*****************
                    if (myContinent != smsg.MyContinent)
                    {
                        myContinent = smsg.MyContinent;
                        ctrl.replyLocalCheckBox.Text = (myContinent == null ? "loc" : myContinent);
                        DebugOutput($"{nl}{Time()} WSJT-X event, myContinent changed:{myContinent}");
                    }

                    //*******************************
                    //check for WSJT-X dxCall changed
                    //*******************************
                    if (dxCall != lastDxCall)       //occurs after dbl-click reported
                    {
                        DebugOutput($"{nl}{Time()} WSJT-X event, dxCall changed, dxCall:{dxCall} (was {lastDxCall})");
                        lastDxCall = dxCall;
                    }

                    //****************************
                    //detect WSJT-X Tx mode change
                    //****************************
                    if (mode != lastMode)
                    {
                        DebugOutput($"{nl}{Time()} WSJT-X event, mode changed, mode:'{mode}' (was '{lastMode}')");
                        UpdateRR73();

                        if (opMode > OpModes.IDLE)
                        {
                            decodeCycle = 0;
                            consecNoDecodes = 0;
                            ClearAudioOffsets();
                        }

                        if (opMode >= OpModes.START)
                        {
                            ctrl.holdCheckBox.Checked = false;
                            DisableAutoFreqPause();
                            ResetOpMode();
                            SetCallInProg(null);      //not calling anyone
                            StatusView.ShowMessage("Mode changed", false);
                            modeChanged = true;
                            newMode = true;
                        }
                        CheckModeSupported();
                    }
                    lastMode = mode;

                    //**********************************
                    //check for WSJT-X frequency changed
                    //**********************************
                    if (lastDialFrequency != null && (Math.Abs((float)lastDialFrequency - (float)dialFrequency) > freqChangeThreshold))
                    {
                        DebugOutput($"{nl}{Time()} [BAND-AUDIT] StatusMsg FreqChanged: newFreq:{dialFrequency / 1e6:F6} oldFreq:{lastDialFrequency / 1e6:F6} oldBandIdx:{prevBandIdx} newBandIdx:{FreqToBandIdx(dialFrequency / 1e6)} opMode:{opMode}");
                        bandIdx = FreqToBandIdx(dialFrequency / 1e6);       //can be null if unknown

                        if (FreqToBandIdx(dialFrequency / 1e6) == FreqToBandIdx(lastDialFrequency / 1e6))      //same band
                        {
                            DisableAutoFreqPause();

                            if (opMode == OpModes.ACTIVE)
                            {
                                ClearAudioOffsets();
                                if (ctrl.freqCheckBox.Checked) AutoFreqChanged(true, false);
                                Pause(true, false);
                                //if transmitting, let tx end trigger show status
                                if (!transmitting) ShowStatus();
                                if (!modeChanged) StatusView.ShowMessage("Frequency changed", false);
                                decodeCount = 0;
                                consecNoDecodes = 0;
                            }
                        }
                        else        //new band
                        {
                            DisableAutoFreqPause();
                            ClearAudioOffsets();
                            // See WsjtxClient.BandAudio.cs BandUp() -- not arming
                            // _requireOffsetForActive on band change (2026-07-12).
                            newBand = true;

                            // Options > Radio "Remember F11/F12 audio level per band" -- this
                            // classic WSJT-X/UDP path had NO restore call at all before this fix
                            // (found live, 2026-08-17: an operator reported the level never
                            // coming back on returning to a band). See RestoreTxLevelForBand's
                            // own comment (WsjtxClient.BandAudio.cs) for the full story.
                            RestoreTxLevelForBand();

                            decodeCount = 0;
                            consecNoDecodes = 0;
                            AutoFreqChanged(false, true);
                            DebugOutput($"{spacer}band changed:'{FreqToBandStr(dialFrequency / 1e6)}' (was:'{FreqToBandStr(lastDialFrequency / 1e6)}')");

                            _rawDecodeHistory.Clear();
                            if (ctrl.advShowRaw) ShowRawDecodes();

                            // Always clear calls and log on any confirmed band change, regardless of
                            // opMode. BandUp/Down set opMode=START via AutoFreqChanged before the
                            // command is sent, so opMode is never ACTIVE when this confirmation
                            // arrives — gating ClearCalls on ACTIVE caused the old list to persist.
                            DebugOutput($"{spacer}[BAND-AUDIT] StatusMsg FreqChanged: new band confirmed → ClearCalls+logList.Clear");
                            ClearCalls(true);
                            logList.Clear();        //can re-log on new mode/band or in new session
                            ShowLogged();
                            ctrl.LoadHrcCache();    //refresh HRC sets (band-independent; picks up any new imports)
                            ctrl.RefreshStillNeedCache();    //reload Still Need live-tag cache for the new band

                            if (opMode == OpModes.ACTIVE)
                            {
                                CancelQso();            //band change abandons any active contact
                                //won't get notification of Halt and Enable Tx buttons changing
                                if (txEnabled) Pause(true, false);
                            }

                            //if transmitting, let tx end trigger show status
                            if (!transmitting) ShowStatus();

                            // Say the actual band, not just "Band changed" -- confirmed live,
                            // 2026-08-07: cycling Band Up/Down quickly (each retune racing the
                            // previous one's confirmation) produces several of these in a row, and
                            // a bare "Band changed" gives no way to tell which retune you landed
                            // on without separately checking the mode/band edit box.
                            if (!modeChanged) StatusView.ShowMessage($"Band changed to {FreqToBandStr(dialFrequency / 1e6)}", false);
                            DebugOutput($"{spacer}cleared queued calls:DialFrequency, txTimeout:{txTimeout} callInProg:'{CallPriorityString(callInProg)}'");
                        }
                    }
                    lastDialFrequency = smsg.DialFrequency;

                    //*******************************************
                    //detect WSJT-X special operating mode change
                    //*******************************************
                    if (specOp != lastSpecOp)
                    {
                        DebugOutput($"{nl}{Time()} WSJT-X event, Special operating mode changed, specOp:{specOp} (was {lastSpecOp})");

                        if (opMode > OpModes.IDLE) ClearAudioOffsets();

                        if (opMode >= OpModes.START)
                        {
                            ctrl.holdCheckBox.Checked = false;
                            DisableAutoFreqPause();
                            ResetOpMode();
                            ShowStatus();
                            SetCallInProg(null);      //not calling anyone
                            modeChanged = true;
                            newMode = true;
                        }
                        CheckModeSupported();
                    }
                    lastSpecOp = specOp;

                    //***************************************
                    //check for transition from IDLE to START
                    //***************************************
                    // Stage A7: commConfirmed (the non-standard cmd:7 echo) is no longer
                    // required here -- NegoState==RECD (reaching this whole block at all)
                    // is by itself now standard-protocol-only proof Jimmy is talking to a
                    // real WSJT-X.
                    if (supportedModes.Contains(mode) && specOp == 0 && opMode == OpModes.IDLE)
                    {
                        opMode = OpModes.START;
                        DebugOutput($"{Time()} opMode IDLE -> START");
                        if (ctrl.freqCheckBox.Checked) ShowStatus();
                        UpdateModeVisible();
                    }

                    //*************************
                    //detect decoding start/end
                    //*************************
                    if (decoding != lastDecoding)
                    {
                        if (smsg.Decoding)
                        {
                            string newLn = (decodeCycle == 0 ? nl : "");
                            DebugOutput($"{newLn}{Time()} WSJT-X event, Decode start, trPeriod:'{trPeriod}' decodeCycle:{decodeCycle}, processDecodeTimer.Enabled:{processDecodeTimer.Enabled}");
                            if (decodeCycle == 0 && trPeriod != null)
                            {
                                SetPeriodState();
                                decodesProcessed = false;
                                if (!processDecodeTimer.Enabled)           //was not started at end of last xmit, use first decode instead
                                {
                                    int msec = (dtNow.Second * 1000) + dtNow.Millisecond;
                                    int diffMsec = msec % (int)trPeriod;
                                    int cycleTimerAdj = CalcTimerAdj();
                                    int interval = Math.Max(((int)trPeriod) - diffMsec - cycleTimerAdj, 1);
                                    DebugOutput($"{spacer}msec:{msec} diffMsec:{diffMsec} interval:{interval} cycleTimerAdj:{cycleTimerAdj}");
                                    if (interval > 0)
                                    {
                                        processDecodeTimer.Interval = interval;
                                        processDecodeTimer.Start();
                                        DebugOutput($"{spacer}processDecodeTimer start");
                                    }
                                }
                            }
                        }
                        else  //not decoding
                        {
                            postDecodeTimer.Stop();
                            postDecodeTimer.Start();                    //restart timer at every decode, will time out after last decode
                            DebugOutput($"{Time()} WSJT-X event, Decode end, postDecodeTimer start, decodeNum:{decodeNum} decodeCycle:{decodeCycle}");
                            if (decodeCycle == 0)
                            {
                                //first calculation of best offset
                                if (!skipFirstDecodeSeries)
                                {
                                    if (CalcBestOffset(audioOffsets, period, false))       //calc for period when decodes started
                                    {
                                        ctrl.freqCheckBox.Text = "Use best Tx frequency";
                                        ctrl.freqCheckBox.ForeColor = Color.Black;
                                    }
                                    CalcAvgTimeOffset(false);
                                }
                            }
                            decodeCycle++;
                            DebugOutput($"{spacer}next decodeCycle:{decodeCycle}");
                        }
                        lastDecoding = smsg.Decoding;
                    }

                    //*************************************
                    //check for changed QSO state in WSJT-X
                    //*************************************
                    if (lastQsoState != qsoStateConf)
                    {
                        qsoState = qsoStateConf;            //qsoState confirmed
                        DebugOutput($"{nl}{Time()} WSJT-X event, qsoState changed, qsoState:{qsoState} (was {lastQsoState})");
                        lastQsoState = qsoState;
                        UpdateCallInProg();
                        DebugOutputStatus();
                    }

                    //**********************
                    //WSJT-X Tx halt clicked
                    //**********************
                    if (smsg.TxHaltClk)
                    {
                        if (opMode >= OpModes.START)
                        {
                            DebugOutput($"{nl}{Time()} WSJT-X event, TxHaltClk, cqPaused:{cqPaused} txMode:{txMode} processDecodeTimer.Enabled:{processDecodeTimer.Enabled}");
                            txEnabled = false;        //sync belief -- WSJT-X halted Tx on its own, not via Jimmy's own EnableTx()/DisableTx()
                            Pause(false, true);       //WSJT-X already halted Tx
                            // Stage A8: mark this transition as already handled so the
                            // standard-protocol fallback below doesn't also fire for it --
                            // see that block's own comment.
                            lastTxEnabled = txEnabledConf;
                        }
                    }
                    //***********************************************
                    //check for WSJT-X Tx enable button state changed
                    //***********************************************
                    if (smsg.TxEnableClk)           //WSJT-X "Tx Enable" button clicked, and button state updated by WSJT-X
                    {
                        if (opMode >= OpModes.START)
                        {
                            DebugOutput($"{nl}{Time()} WSJT-X event, wsjtxTxEnableButton:{wsjtxTxEnableButton}, txEnabled:{txEnabled} cqPaused:{cqPaused} txMode:{txMode} processDecodeTimer.Enabled:{processDecodeTimer.Enabled}");
                            if (!txEnabled)    //Jimmy didn't ask for this -- WSJT-X changed its own Enable Tx button
                            {
                                if (wsjtxTxEnableButton)    //button just became enabled on WSJT-X's own initiative (e.g. Wait and Reply)
                                {
                                    HandleUnsolicitedTxResume();
                                }
                                else                        //button just became disabled
                                {
                                    //HaltTx();
                                    Console.Beep();
                                }
                            }
                            // Stage A8: same reasoning as the TxHaltClk block above.
                            lastTxEnabled = txEnabledConf;
                        }
                    }

                    //***********************************
                    //check for changed WSJT-X Tx enabled
                    //***********************************
                    // Stage A8: standard-protocol-only substitute for the non-standard
                    // TxHaltClk/TxEnableClk detection above -- Wait-and-Reply cooperation
                    // ("tell the blind operator their radio started/stopped transmitting
                    // on its own"), previously only available against Andy WM8Q's fork.
                    // Confirmed via reading WSJT-X's own real source (mainwindow.cpp,
                    // github.com/avantol/WSJT-X_3.0.0 -- an unmodified copy of stock
                    // WSJT-X's own code in this area, confirmed by MessageClient.cpp's
                    // trace log literally carrying Andy's own initials/dates only on the
                    // genuinely-added fields): the standard "Tx Enabled" field
                    // (statusUpdate()'s tx_enabled parameter) is populated from the exact
                    // same m_auto variable that both a direct operator click in WSJT-X's
                    // own GUI AND Wait-and-Reply's own timeout (auto_tx_mode ->
                    // on_autoButton_clicked -> process_autoButton, the identical code path
                    // for both) update -- and process_autoButton sends a fresh Status
                    // broadcast unconditionally on every change, regardless of source or
                    // build. This is documented standard-protocol behavior
                    // ("'Enable Tx' button status changes" is one of NetworkMessage.hpp's
                    // own listed Status-broadcast triggers), not an Andy-fork-specific
                    // behavior -- so Jimmy can infer "WSJT-X changed this on its own"
                    // purely by comparing the standard field against its own
                    // last-commanded belief (txEnabled), exactly as the non-standard path
                    // above already does with wsjtxTxEnableButton/txEnabled, without
                    // needing WSJT-X to explicitly flag the event at all.
                    //
                    // Only ever fires for a transition the blocks above did NOT already
                    // handle: each syncs lastTxEnabled itself when it fires, so on an
                    // Andy-fork connection (where both mechanisms observe the same
                    // change) this never double-triggers. On a standard-only connection
                    // (TxHaltClk/TxEnableClk always false -- those fields don't exist on
                    // that build's wire), this is the only path that ever runs.
                    if (txEnabledConf != lastTxEnabled)
                    {
                        DebugOutput($"{nl}{Time()} WSJT-X event, Tx enable change confirmed, txEnabled:{txEnabled} (was {lastTxEnabled}) cqPaused:{cqPaused} txMode:{txMode}");
                        if (opMode >= OpModes.START)
                        {
                            if (txEnabledConf && !txEnabled)    //became enabled, and Jimmy didn't ask for this -- WSJT-X's own initiative (e.g. Wait and Reply auto-resume, or a direct operator click in WSJT-X's own GUI)
                            {
                                DebugOutput($"{nl}{Time()} WSJT-X event, Tx enable became enabled unsolicited (standard-protocol path)");
                                HandleUnsolicitedTxResume();
                            }
                            else if (!txEnabledConf && txEnabled)    //became disabled, and Jimmy still believes it's enabled -- WSJT-X's own initiative, not Jimmy's own HaltTx()
                            {
                                DebugOutput($"{nl}{Time()} WSJT-X event, Tx enable became disabled unsolicited (standard-protocol path)");
                                txEnabled = false;       //sync belief -- WSJT-X halted Tx on its own
                                Pause(false, true);      //WSJT-X already halted Tx
                            }
                        }
                        lastTxEnabled = txEnabledConf;
                    }

                    //**********************************************
                    //check for WSJT-X watchdog timer status changed
                    //**********************************************
                    if (smsg.TxWatchdog != lastTxWatchdog)
                    {
                        DebugOutput($"{nl}{Time()} WSJT-X event, smsg.TxWatchdog:{smsg.TxWatchdog} (was {lastTxWatchdog})");
                        /*if (opMode == OpModes.ACTIVE)
                        {
                            ctrl.holdCheckBox.Checked = false;
                        }

                        if (smsg.TxWatchdog && opMode == OpModes.ACTIVE)        //only need this event if in valid mode
                        {
                            if (firstDecodeTime != DateTime.MinValue)
                            {
                                string txt;
                                if ((DateTime.UtcNow - firstDecodeTime).TotalMinutes < 15)
                                {
                                    txt = $"Set the 'Tx watchdog' in WSJT-X to 15 minutes or longer.{nl}{nl}This will be the timeout in case {ctrl.friendlyName} sends the same message repeatedly (for example, calling CQ when the band is closed).{nl}{nl}The WSJT-X 'Tx watchdog' setting is under File | Settings, in the 'General' tab.";
                                }
                                else
                                {
                                    txt = $"The 'Tx watchdog' in WSJT-X has timed out.{nl}{nl}(The WSJT-X 'Tx watchdog' setting is under File | Settings, in the 'General' tab).{nl}{nl}Select an 'Operatng Mode' to continue.";
                                }

                                firstDecodeTime = DateTime.MinValue;        //allow timing to restart
                            }
                        }*/

                        lastTxWatchdog = smsg.TxWatchdog;
                    }

                    //*****************************
                    //detect WSJT-X Tx First change
                    //*****************************
                    if (txFirst != lastTxFirst)
                    {
                        DebugOutput($"{nl}{Time()} WSJT-X event, Tx first changed, txFirst:{txFirst} txMode:{txMode}");
                        settingChanged = true;
                        DisableAutoFreqPause();
                        if (opMode > OpModes.IDLE) ClearAudioOffsets();

                        if (opMode == OpModes.ACTIVE)
                        {
                            newTxFirst = true;
                            if (!ctrl.advancedCallLayout)
                            {
                                // Normal mode: a txFirst change means the user manually
                                // switched TX period — clear the queue and pause so the
                                // next decode cycle fills the list for the new period.
                                SetCallInProg(null);
                                ClearCalls(true);
                                Pause(true, true);
                                ctrl.holdCheckBox.Checked = false;
                            }
                            else
                            {
                                // Advanced mode: both TX periods coexist in the queue.
                                // A txFirst change here is either an Alt+F manual toggle
                                // or a cross-period ReplyMessage side-effect — keep the
                                // queue and show the confirmed status promptly.
                                StartStatusTimer();
                            }
                        }
                        lastTxFirst = txFirst;
                        UpdateCallListAccessibleName();
                    }

                    //**********************************
                    //detect WSJT-X log upload log state
                    //**********************************
                    if (wsjtxResultCode != lastWsjtxResultCode)
                    {
                        if (wsjtxResultCode == (int)WsjtxResultCodes.LOTW_UPL)
                        {
                            DebugOutput($"{nl}{Time()} WSJT-X event, upload to LOTW, wsjtxResultCode:{wsjtxResultCode} statusDetail:'{statusDetail}' isNull:{statusDetail == null}");
                            uploadResult = (statusDetail != null && statusDetail != "" ) ? statusDetail : "QSO upload status unknown";
                            ShowStatus();
                        }

                        if (wsjtxResultCode == (int)WsjtxResultCodes.PWR_SWR_SINGLE_RPT)        //no reason to lose decode syncing 
                        {
                            DisableAutoFreqPause();
                            DebugOutput($"{nl}{Time()} WSJT-X event, power/swr single result, wsjtxResultCode:{wsjtxResultCode} statusDetail:'{statusDetail}' isNull:{statusDetail == null}");
                            tuneResult = (statusDetail != null && statusDetail != "") ? statusDetail : "Power/SWR unknown";
                            ShowStatus();
                        }

                        if (wsjtxResultCode == (int)WsjtxResultCodes.PWR_SWR_RPT)
                        {
                            consecNoDecodes = 0;
                            StopDecodeTimers();
                            DisableAutoFreqPause();
                            DebugOutput($"{nl}{Time()} WSJT-X event, power/swr result, wsjtxResultCode:{wsjtxResultCode} statusDetail:'{statusDetail}' isNull:{statusDetail == null}");
                            tuneResult = (statusDetail != null && statusDetail != "") ? statusDetail : "Power/SWR unknown";
                            ShowStatus();
                        }

                        if (wsjtxResultCode == (int)WsjtxResultCodes.PWR_SWR_END)
                        {
                            decodeCycle = 0;        //restart decode syncing
                            DebugOutput($"{nl}{Time()} WSJT-X event, power/swr result, wsjtxResultCode:{wsjtxResultCode}");
                            tuneResult = "Tune stopped";
                            tuning = false;             //normal status msgs
                            statusTimer.Interval = 750;     //will be receiving mode soon
                            statusTimer.Start();
                        }
                        lastWsjtxResultCode = wsjtxResultCode;
                    }



                    if (CheckActive())
                    {
                        _requireOffsetForActive = false;
                        UInt32 activeOffset = AudioOffsetFromTxPeriod();
                        if (activeOffset > 0 || !ctrl.freqCheckBox.Checked)
                        {
                        DebugOutput($"{Time()} [BAND-AUDIT] CheckActive offset calc: bandIdx:{bandIdx} offset:{activeOffset}");
                        }
                        if (settingChanged)
                        {
                            ctrl.WsjtxSettingConfirmed();
                            settingChanged = false;
                        }

                        newBand = true;
                        newMode = true;
                        decodeCount = 0;
                        consecNoDecodes = 0;
                        ShowStatus();
                    }

                    //*****end of status *****
                    UpdateDebug();
                    return;
                }

                //*****************
                //QsoLoggedMessage
                //*****************
                if (msg.GetType().Name == "QsoLoggedMessage")
                {
                    var qMsg = (QsoLoggedMessage)msg;
                    DebugOutput($"{nl}{Time()} QsoLoggedMessage rec'd: DxCall:'{qMsg.DxCall}'");
                    HandleLiveQsoLogged(qMsg);
                }

                //*****************
                //LoggedAdifMessage -- WSJT-X sends this alongside QsoLoggedMessage for every
                //logged QSO. Jimmy normally acts on QsoLoggedMessage; this is a fallback so
                //one dropped UDP packet doesn't silently keep a QSO out of the log/awards.
                //(Note: this message's own "Id" field, like QsoLoggedMessage's, is WSJT-X's
                //fixed per-instance identifier, not a per-QSO key -- ClaimLiveLoggedQso()
                //dedupes on callsign/band/mode/date/time instead, so the normal case where
                //both messages arrive for the same QSO only processes it once.)
                //*****************
                else if (msg.GetType().Name == "LoggedAdifMessage")
                {
                    var aMsg = (LoggedAdifMessage)msg;
                    DebugOutput($"{nl}{Time()} LoggedAdifMessage rec'd, Id:'{aMsg.Id}'");
                    HandleLiveAdifLogged(aMsg);
                }
            }
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
            heartbeatRecdTimer.Stop();
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
        public void ConnectionDialog()
        {
            ctrl.initialConnFaultTimer.Stop();
            heartbeatRecdTimer.Stop();
            if (WsjtxMessage.NegoState == WsjtxMessage.NegoStates.INITIAL)
            {
                Notify.Publish(new ErrorWarningEvent(ErrorSeverity.Warning, "Native engine",
                    "no response yet -- still waiting."));
                ctrl.initialConnFaultTimer.Start();
            }
        }

        //detect supported mode
        //opMode = IDLE, NegoState can be in SENT or RECD
        private void CheckModeSupported()
        {
            string s = "";
            modeSupported = supportedModes.Contains(mode) && specOp == 0;
            DebugOutput($"{Time()} CheckModeSupported, mode:'{mode}' curVerBld:{curVerBld} modeSupported:{modeSupported}");

            if (!modeSupported)
            {
                ShowStatus();
                if (specOp != 0) s = "Special ";
                DebugOutput($"{spacer}{s}mode:'{mode}' specOp:'{specOp}'");
                failReason = $"{s}{mode} mode not supported";
                if (txMode == TxModes.LISTEN)
                {
                    if (opMode == OpModes.ACTIVE) ctrl.cqModeButton_Click(null, null);       //re-enable WSJT-X "Tx even/1st" control
                }
            }

            if (mode == "MSK144" && modeSupported)
            {
                ctrl.freqCheckBox.Enabled = false;
                ctrl.freqCheckBox.Checked = false;
                ctrl.optimizeCheckBox.Enabled = false;
                ctrl.optimizeCheckBox.Checked = false;
                ctrl.holdCheckBox.Checked = false;
            }
            else
            {
                ctrl.freqCheckBox.Enabled = true;
                ctrl.optimizeCheckBox.Enabled = !ctrl.holdCheckBox.Checked;
            }
        }

        // Retunes over rigctld when a real frequency is given (band changes); a bare txFirst
        // toggle (freq==0, from ToggleTxFirst) is pure Jimmy-side TX-sequencing state with
        // nothing to send anywhere.
        private void SetBandTxFirst(uint freq, bool state, string caller = "")
        {
            string bandLabel = freq > 0 ? (FreqToBandStr(freq / 1000.0 / 1e6) ?? $"{freq / 1000}kHz") : "none";
            DebugOutput($"{Time()} [BAND-AUDIT] SetBandTxFirst: caller:{caller} freq:{freq} band:{bandLabel} txFirst:{state} bandIdx:{bandIdx}");

            if (freq > 0 && ctrl.Radio.Mode == RadioControlMode.HamlibRigctld && ctrl.rigctldClient != null)
                ctrl.rigctldClient.SetFrequency(freq);
        }

        private void HeartbeatNotRecd(object sender, EventArgs e)
        {
            //no heartbeat from WSJT-X, re-init communication
            heartbeatRecdTimer.Stop();
            DebugOutput($"{Time()} heartbeatRecdTimer timed out");
            if (WsjtxMessage.NegoState == WsjtxMessage.NegoStates.RECD)
            {
                // Wave 1 of the notification architecture (WSJTX_Controller/Notify/): default
                // template "WSJT-X disconnected", Normal priority -- byte-identical to the
                // direct ShowMessage call this replaces. The sound cue below stays untouched
                // and independent, exactly as before.
                Notify.Publish(new ConnectionLostEvent());
                Sounds.PlaySoundEvent(ctrl.soundEnabled_Disconnected, ctrl.soundFile_Disconnected);
            }
            else
            {
                StatusView.ShowMessage("WSJT-X not responding", true);
            }
            ResetNego();
            CloseAllUdp();          //usually not needed
        }

        //must call only when in WAIT state
        //to avoid async cakkback using disposed udpClient
        private void CloseAllUdp()
        {
            DebugOutput($"{Time()} CloseAllUdp");
            // Stage A3: socket-closing mechanics moved to WsjtxProtocolAdapter.Close --
            // same null-check/Close()/set-null/exception-handling shape as before, exact
            // same log line text via this delegate.
            _protocolAdapter.Close(msg => DebugOutput($"{spacer}{msg}"));
        }

    }
}
