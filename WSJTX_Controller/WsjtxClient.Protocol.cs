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
    public partial class WsjtxClient
    {
        //override auto IP addr, port, and/or mode with new values
        public void UpdateAddrPortMulti(IPAddress reqIpAddress, int reqPort, bool reqMulticast, bool reqOverrideUdpDetect)
        {
            ipAddress = reqIpAddress;
            port = reqPort;
            multicast = reqMulticast;
            overrideUdpDetect = reqOverrideUdpDetect;
            ResetNego();
            CloseAllUdp();
        }

        // Stage A3: body moved to WsjtxProtocolAdapter.ReceiveCallback (Protocol/
        // WsjtxProtocolAdapter.cs) -- it had zero true WsjtxClient-instance dependency
        // beyond the socket-receive state that now lives there. Kept here as a thin
        // wrapper since asyncCallback = new AsyncCallback(ReceiveCallback) (below, in
        // CheckWsjtxRunning) needs a method matching AsyncCallback's signature.
        public void ReceiveCallback(IAsyncResult ar) => _protocolAdapter.ReceiveCallback(ar);

        public void UdpLoop()
        {
            if (WsjtxMessage.NegoState == WsjtxMessage.NegoStates.WAIT)
            {
                if (!suspendComm) CheckWsjtxRunning();            //re-init if so
                return;
            }
            else
            {
                bool notRunning = !WsjtxProtocolAdapter.IsWsjtxRunning();
                if (notRunning || wsjtxClosing)
                {
                    DebugOutput($"{nl}{Time()} WSJT-X notRunning:{notRunning} wsjtxClosing:{wsjtxClosing}");
                    ResetNego();
                    CloseAllUdp();
                    wsjtxClosing = false;
                    StatusView.ShowMessage("WSJT-X closed", true);
                }
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

        private void CheckWsjtxRunning()
        {
            if (WsjtxProtocolAdapter.IsWsjtxRunning())
            {
                DebugOutput($"{nl}{Time()} WSJT-X running");
                StatusView.ShowMessage("WSJT-X detected", false);
                Thread.Sleep(3000);     //wait for WSJT-X to open UDP

                bool retry = true;
                while (retry)
                {
                    if (!overrideUdpDetect)
                    {
                        // Stage A3: ipAddress/port/multicast are now delegating properties
                        // (WsjtxProtocolAdapter-backed), so they can't be passed as out
                        // params directly -- DetectUdpSettings always sets its out params
                        // (detected values on success, IPv4 loopback/2237/unicast defaults
                        // on failure), so this still assigns all three unconditionally,
                        // matching the original behavior exactly.
                        bool detected = WsjtxProtocolAdapter.DetectUdpSettings(out IPAddress detectedIp, out int detectedPort, out bool detectedMulticast);
                        ipAddress = detectedIp;
                        port = detectedPort;
                        multicast = detectedMulticast;
                        if (!detected)
                        {
                            DebugOutput($"{spacer}using default IP address from WSJT-X");
                            heartbeatRecdTimer.Stop();
                            suspendComm = true;
                            ctrl.BringToFront();
                            MessageBox.Show($"{pgmName} is using the default UDP IP address and port.", pgmName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            suspendComm = false;
                        }
                    }


                    DebugOutput($"{spacer}ipAddress:{ipAddress} port:{port} multicast:{multicast}");
                    string modeStr = multicast ? "multicast" : "unicast";
                    // Stage A3: socket-construction mechanics moved to
                    // WsjtxProtocolAdapter.TryOpenReceiveSocket -- this retry loop, the
                    // debug logging, and the error-dialog handling stay here (Controller/
                    // UI-level orchestration decisions, not socket mechanics).
                    if (_protocolAdapter.TryOpenReceiveSocket(out Exception openErr))
                    {
                        DebugOutput($"{spacer}opened udpClient:{udpClient}");
                        retry = false;
                    }
                    else
                    {
                        DebugOutput($"{spacer}unable to open udpClient:{udpClient}{nl}{openErr}");
                        heartbeatRecdTimer.Stop();
                        suspendComm = true;
                        ctrl.BringToFront();
                        if (MessageBox.Show($"Unable to open WSJT-X's specified UDP port,{nl}address: {ipAddress}{nl}port: {port}{nl}mode: {modeStr}.{nl}{nl}In WSJT-X, select File | Settings | Reporting.{nl}At 'UDP Server':{nl}- Enter '239.255.0.0' for 'UDP Server{nl}- Enter '2237' for 'UDP Server port number'{nl}- Select all checkboxes at 'Outgoing interfaces'{nl}- Click 'Retry' below to try opening the UDP port again.{nl}{nl}Alternatively:{nl}- Click 'Cancel' below for {ctrl.friendlyName}'s 'Config'{nl}- Enter the UDP address and port as shown in WSJT-X, or{nl}- Select 'Override' to use auto-detected UDP settings.", pgmName, MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) == DialogResult.Cancel)
                        {
                            ctrl.OpenUdpConfig();
                            return;                 //suspendComm set to false at Options dialog close
                        }
                    }
                }
                suspendComm = false;

                // Stage A3: udpSt is now a struct-returning delegating property, so its
                // fields can't be mutated in place (the getter returns a copy) -- build
                // the whole value and assign it in one step instead, same end state.
                udpSt = new UdpState { e = endPoint, u = udpClient };
                asyncCallback = new AsyncCallback(ReceiveCallback);

                WsjtxMessage.NegoState = WsjtxMessage.NegoStates.INITIAL;
                DebugOutput($"{spacer}NegoState:{WsjtxMessage.NegoState}");

                if (!suspendComm)
                {
                    ctrl.initialConnFaultTimer.Interval = 3 * heartbeatInterval * 1000;           //pop up dialog showing UDP corrective info at tick
                    ctrl.initialConnFaultTimer.Start();
                }
            }
        }

        public bool TogglePskReporter()
        {
            usePskReporter = !usePskReporter;

            // Stage A4: build+send moved to WsjtxCompatibilityExtension.SetPskReporterEnable.
            string genMsg = $"(mod by KB0UZT, w/{pgmName} v{pgmVer} [FT8 for blind hams], qrz.com/db/KB0UZT)";
            _compatExtension.SetPskReporterEnable(usePskReporter, genMsg, msg => DebugOutput($"{Time()} {msg}"));

            newPskReporter = true;
            ShowStatus();
            return true;
        }

        public bool SetOperatingMode(string newMode)
        {
            if (transmitting || txEnabled) HaltTx();
            if (transmitting) Thread.Sleep(250);        //radio must return to original rx freq first

            // Stage A4: build+send moved to WsjtxCompatibilityExtension.SetOperatingMode.
            _compatExtension.SetOperatingMode(newMode, msg => DebugOutput($"{Time()} {msg}"));

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
            if (msg.GetType().Name == "HeartbeatMessage" && (WsjtxMessage.NegoState == WsjtxMessage.NegoStates.INITIAL || WsjtxMessage.NegoState == WsjtxMessage.NegoStates.FAIL))
            {
                ctrl.initialConnFaultTimer.Stop();             //stop connection fault dialog
                HeartbeatMessage imsg = (HeartbeatMessage)msg;
                DebugOutput($"{Time()}{nl}{imsg}");

                string[] sa = imsg.Revision.Split(' '); //may contain other info, including URL

                string rev = sa[0];
                int.TryParse(rev, out wsjtxRevision);

                string testVer = sa.Length >= 2 ? sa[1] : "42";
                int.TryParse(testVer, out wsjtxTestVer);

                curVerBld = $"{imsg.Version}/{rev}";

                // Stage A5: the hard acceptableWsjtxVersions allowlist + blocking dialog
                // is gone (Blueprint §18). Any build that speaks the standard Heartbeat
                // protocol is accepted here; whether the non-standard Compatibility Layer
                // is also present gets probed separately via the existing cmd:7 exchange
                // (see CapabilityNegotiator, and the SENT->RECD transition below) rather
                // than refusing to connect up front.
                _capabilityNegotiator.BeginNegotiating();

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
                DebugOutput($"{Time()} >>>>>Sent'Heartbeat' msg:{nl}{tmsg}");
                ShowStatus();
                StatusView.ShowMessage("WSJT-X responding", false);

                if (wsjtxRevision == 102 && wsjtxTestVer < 72) DeleteLotwCsv();        //fixed, reason for WSJT-X crashing at startup because of NVDA determined

                UpdateDebug();
                return;
            }

            //rec'd negotiation HeartbeatMessage
            //send another request for a StatusMessage
            //go from SENT to RECD state
            if (WsjtxMessage.NegoState == WsjtxMessage.NegoStates.SENT && msg.GetType().Name == "HeartbeatMessage")
            {
                HeartbeatMessage hmsg = (HeartbeatMessage)msg;
                DebugOutput($"{Time()}{nl}{hmsg}");
                WsjtxMessage.NegotiatedSchemaVersion = hmsg.SchemaVersion;
                WsjtxMessage.NegoState = WsjtxMessage.NegoStates.RECD;
                UpdateDebug();
                DebugOutput($"{spacer}NegoState:{WsjtxMessage.NegoState}");
                DebugOutput($"{spacer}negotiated schema version:{WsjtxMessage.NegotiatedSchemaVersion}");
                UpdateDebug();

                //send ACK request to WSJT-X, to get 
                //a StatusMessage reply to start normal operation
                Thread.Sleep(250);
                emsg.NewTxMsgIdx = 7;
                emsg.GenMsg = $"";          //no effect
                emsg.ReplyReqd = true;
                emsg.EnableTimeout = !debug;
                emsg.CmdCheck = cmdCheck;
                ba = emsg.GetBytes();
                udpClient2.Send(ba, ba.Length);
                DebugOutput($"{Time()} >>>>>Sent 'Ack Req' cmd:7 cmdCheck:{cmdCheck}{nl}{emsg}");
                // Stage A5: cmd:7 doubles as the CapabilityNegotiator's probe for the
                // Compatibility Layer -- tracking only, the send itself is unchanged.
                _capabilityNegotiator.BeginCapabilityProbe();

                // Stage A4: build+send moved to WsjtxCompatibilityExtension.SetPskReporterEnable.
                _compatExtension.SetPskReporterEnable(usePskReporter,
                    $"(mod by KB0UZT, w/{pgmName} v{pgmVer} [FT8 for blind hams], qrz.com/db/KB0UZT)",
                    msg => DebugOutput($"{Time()} {msg}"));

                HaltTx();       //sync up WSJT-X button state

                if (bandIdx == null)
                {
                    SetOperatingMode("FT8");            //after halt
                    Thread.Sleep(250);
                    mode = "FT8";
                    bandIdx = FreqToBandIdx(dialFrequency / 1e6);       //can be null if unknown
                    if (bandIdx == null) bandIdx = 5;
                    SetBandTxFirst((uint)(bandToFreq(bandIdx) * 1000), txFirst, "InitialConnect");
                    Thread.Sleep(250);
                }

                cmdCheckTimer.Interval = 10000;           //set up cmd check timeout
                cmdCheckTimer.Start();
                DebugOutput($"{spacer}check cmd timer started");
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

                    //if seconds units, need msec
                    if (smsg.TRPeriod != null)
                    {
                        if ((int)smsg.TRPeriod < 1000)
                        {
                            trPeriod = 1000 * (int)smsg.TRPeriod;
                        }
                        else
                        {
                            trPeriod = (int)smsg.TRPeriod;
                        }
                    }

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
                emsg.NewTxMsgIdx = 7;
                emsg.GenMsg = $"";          //no effect
                emsg.ReplyReqd = (opMode != OpModes.ACTIVE);
                emsg.EnableTimeout = !debug;
                if (emsg.ReplyReqd) cmdCheck = RandomCheckString();
                emsg.CmdCheck = cmdCheck;
                ba = emsg.GetBytes();
                udpClient2.Send(ba, ba.Length);
                if (opMode != OpModes.ACTIVE) DebugOutput($"{Time()} >>>>>Sent 'Ack Req' cmd:7 cmdCheck:{cmdCheck}{nl}{emsg}");

                heartbeatRecdTimer.Stop();
                if (!debug)
                {
                    heartbeatRecdTimer.Start();
                    if (opMode != OpModes.ACTIVE) DebugOutput($"{spacer}heartbeatRecdTimer restarted");
                }

                // Stage A4: build+send moved to WsjtxCompatibilityExtension.ResetTxWatchdog.
                _compatExtension.ResetTxWatchdog(msg =>
                {
                    if (opMode != OpModes.ACTIVE) DebugOutput($"{Time()} {msg}");
                });

            }

            if (WsjtxMessage.NegoState == WsjtxMessage.NegoStates.RECD)
            {
                if (modeSupported)
                {
                    //********************
                    //EnqueueDecodeMessage
                    //********************
                    //only resulting action is to add call to callQueue, optionally restart queue
                    if (msg.GetType().Name == "EnqueueDecodeMessage" && myCall != null)
                    {
                        EnqueueDecodeMessage dmsg = (EnqueueDecodeMessage)msg;
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


                    //need msec unit
                    if (smsg.TRPeriod != null)
                    {
                        if ((int)smsg.TRPeriod < 1000)
                        {
                            trPeriod = 1000 * (int)smsg.TRPeriod;
                        }
                        else
                        {
                            trPeriod = (int)smsg.TRPeriod;
                        }
                    }

                    if (cmdCheckTimer.Enabled && smsg.Check == cmdCheck)             //found the random cmd check string, cmd receive ack'd
                    {
                        cmdCheckTimer.Stop();
                        commConfirmed = true;
                        _capabilityNegotiator.ConfirmCompatibilityLayer();     //Stage A5
                        DebugOutput($"{nl}{Time()} WSJT-X event, Check cmd rec'd, match");
                    }

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

                            if (!modeChanged) StatusView.ShowMessage("Band changed", false);
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
                    if (commConfirmed && supportedModes.Contains(mode) && specOp == 0 && opMode == OpModes.IDLE)
                    {
                        EnableMonitoring();              //must do only after DisableTx and HaltTx

                        opMode = OpModes.START;
                        DebugOutput($"{Time()} opMode:{opMode}");
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
                        }
                    }

                    //***********************************
                    //check for changed WSJT-X Tx enabled
                    //***********************************
                    if (txEnabledConf != lastTxEnabled)
                    {
                        DebugOutput($"{nl}{Time()} WSJT-X event, Tx enable change confirmed, txEnabled:{txEnabled} (was {lastTxEnabled}) cqPaused:{cqPaused} txMode:{txMode}");
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
                        //send cmd:10 when offset is known, or when freqCheckBox is off (offset=0 is safe in that case)
                        if (activeOffset > 0 || !ctrl.freqCheckBox.Checked)
                        {
                        DebugOutput($"{Time()} [BAND-AUDIT] CheckActive→cmd:10 sent: bandIdx:{bandIdx} offset:{activeOffset}");
                        // Stage A4: build+send moved to WsjtxCompatibilityExtension.OptReq.
                        _compatExtension.OptReq(ctrl.skipGridCheckBox.Checked, ctrl.useRR73CheckBox.Checked, activeOffset,
                            msg => DebugOutput($"{Time()} {msg}"));
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
                // Stage A4: build+send moved to WsjtxCompatibilityExtension.OptReq.
                _compatExtension.OptReq(ctrl.skipGridCheckBox.Checked, ctrl.useRR73CheckBox.Checked, 0,
                    msg => DebugOutput($"{Time()} {msg}"));

                ctrl.WsjtxSettingConfirmed();
                settingChanged = false;
            }
        }

        private void ResetNego()
        {
            WsjtxMessage.Reinit();                      //NegoState = WAIT;
            heartbeatRecdTimer.Stop();
            cmdCheckTimer.Stop();
            DebugOutput($"{nl}{Time()} ResetNego, NegoState:{WsjtxMessage.NegoState}");
            ResetOpMode();
            DebugOutput($"{Time()} Waiting for WSJT-X to run...");
            cmdCheck = RandomCheckString();
            commConfirmed = false;
            _capabilityNegotiator.Reset();     //Stage A5: never cached across a connection boundary
            UpdateRR73();
            ShowStatus();
            UpdateDebug();
        }


        public void ConnectionDialog()
        {
            ctrl.initialConnFaultTimer.Stop();
            heartbeatRecdTimer.Stop();
            if (WsjtxMessage.NegoState == WsjtxMessage.NegoStates.INITIAL)
            {
                heartbeatRecdTimer.Stop();
                suspendComm = true;         //in case udpClient msgs start 
                string s = multicast ? $"{nl}{nl}In WSJT-X:{nl}- Select File | Settings then the 'Reporting' tab.{nl}{nl}'- Try different 'Outgoing interface' selection(s), including selecting all of them." : "";
                ctrl.BringToFront();
                MessageBox.Show($"No response from WSJT-X.{s}{nl}{nl}{pgmName} will continue waiting for WSJT-X to respond when you close this dialog.{nl}{nl}Alternatively, select 'Config' and override the auto-detected UDP settings.", pgmName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                suspendComm = false;
                ctrl.initialConnFaultTimer.Start();
            }
        }

        public void CmdCheckDialog()
        {
            cmdCheckTimer.Stop();
            if (commConfirmed) return;

            heartbeatRecdTimer.Stop();
            suspendComm = true;
            ctrl.BringToFront();
            MessageBox.Show($"Unable to make a two-way connection with WSJT-X.{nl}{nl}{pgmName} will try again when you close this dialog.", pgmName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ResetOpMode();
            ShowStatus();

            if (udpClient2 != null)
            {
                emsg.NewTxMsgIdx = 7;
                //emsg.SchemaVersion = (uint)WsjtxMessage.NegotiatedSchemaVersion;
                emsg.GenMsg = $"";          //no effect
                emsg.ReplyReqd = true;
                emsg.EnableTimeout = !debug;
                cmdCheck = RandomCheckString();
                emsg.CmdCheck = cmdCheck;
                ba = emsg.GetBytes();
                udpClient2.Send(ba, ba.Length);
                DebugOutput($"{Time()} >>>>>Sent 'Ack Req' cmd:7 cmdCheck:{cmdCheck}{nl}{emsg}");

                cmdCheckTimer.Interval = 10000;           //set up cmd check timeout
                cmdCheckTimer.Start();
                DebugOutput($"{Time()} Check cmd timer restarted");
            }

            suspendComm = false;
        }

        private string RandomCheckString()
        {
            string s = rnd.Next().ToString();
            if (s.Length > 8) s = s.Substring(0, 8);
            return s;
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

        private void SetBandTxFirst(uint freq, bool state, string caller = "")       //requires bestWsjtxVersions
        {
            if (udpClient2 == null)
            {
                DebugOutput($"{Time()} SetBandTxFirst skipped, udpClient2:{udpClient2}");
                return;
            }

            string bandLabel = freq > 0 ? (FreqToBandStr(freq / 1000.0 / 1e6) ?? $"{freq / 1000}kHz") : "none";
            DebugOutput($"{Time()} [BAND-AUDIT] SetBandTxFirst: caller:{caller} freq:{freq} band:{bandLabel} txFirst:{state} bandIdx:{bandIdx}");

            // Stage A4: build+send moved to WsjtxCompatibilityExtension.SetBandTxFirst.
            _compatExtension.SetBandTxFirst(freq, state, msg => DebugOutput($"{Time()} {msg}"));
        }

        private void GetPowerSwr()       //requires bestWsjtxVersions
        {
            if (udpClient2 == null)
            {
                DebugOutput($"{Time()} GetPowerSwr skipped, udpClient2:{udpClient2}");
                return;
            }

            // Stage A4: build+send moved to WsjtxCompatibilityExtension.GetPowerSwr.
            _compatExtension.GetPowerSwr(msg => DebugOutput($"{Time()} {msg}"));
        }

        private void AdjAudioLevel(bool up)       //requires bestWsjtxVersions
        {
            if (udpClient2 == null)
            {
                DebugOutput($"{Time()} SetAudioLevel skipped, udpClient2:{udpClient2}");
                return;
            }

            // Stage A4: build+send moved to WsjtxCompatibilityExtension.AdjAudioLevel.
            _compatExtension.AdjAudioLevel(up, msg => DebugOutput($"{Time()} {msg}"));
        }

        private void ToggleTuning()       //requires bestWsjtxVersions
        {
            if (udpClient2 == null)
            {
                DebugOutput($"{Time()} ToggleTuning skipped, udpClient2:{udpClient2}");
                return;
            }

            if (txEnabled) HaltTx();

            // Stage A4: build+send moved to WsjtxCompatibilityExtension.ToggleTuning.
            _compatExtension.ToggleTuning(cmdPrompts, msg => DebugOutput($"{Time()} {msg}"));
        }

        private void StartUploadLotw()       //requires bestWsjtxVersions
        {
            if (udpClient2 == null)
            {
                DebugOutput($"{Time()} StartUploadLotw skipped, udpClient2:{udpClient2}");
                return;
            }

            // Stage A4: build+send moved to WsjtxCompatibilityExtension.StartUploadLotw.
            _compatExtension.StartUploadLotw(msg => DebugOutput($"{Time()} {msg}"));
        }

        private void HeartbeatNotRecd(object sender, EventArgs e)
        {
            //no heartbeat from WSJT-X, re-init communication
            heartbeatRecdTimer.Stop();
            DebugOutput($"{Time()} heartbeatRecdTimer timed out");
            if (WsjtxMessage.NegoState == WsjtxMessage.NegoStates.RECD)
            {
                StatusView.ShowMessage("WSJT-X disconnected", false);
                Sounds.PlaySoundEvent(ctrl.soundEnabled_Disconnected, ctrl.soundFile_Disconnected);
            }
            else
            {
                StatusView.ShowMessage("WSJT-X not responding", true);
            }
            ResetNego();
            CloseAllUdp();          //usually not needed
        }

        private void cmdCheckTimer_Tick(object sender, EventArgs e)
        {
            CmdCheckDialog();
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
