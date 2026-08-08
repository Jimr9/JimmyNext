using System;
using System.Net.Sockets;
using WsjtxUdpLib.Messages;
using WsjtxUdpLib.Messages.Out;

namespace WSJTX_Controller
{
    // Migration Stage A4 (Jimmy_Master_Migration_Roadmap.md / Architecture Blueprint):
    // isolates every non-standard NewTxMsgIdx sub-command Jimmy sends through
    // EnableTxMessage/emsg -- the "SetupTx RPC channel" Andy WM8Q's fork adds on top
    // of the standard WSJT-X UDP protocol (see Phase 4 dependency audit, section 2).
    // Zero behavior change: each method here is a verbatim move of the existing
    // build-message-and-send logic, with WsjtxClient's original methods becoming thin
    // wrappers so every call site keeps working unchanged. State/business-logic
    // bookkeeping around each send (txEnabled tracking, UpdateDblClkTip, exception
    // handling, connection-null guards, etc.) stays in WsjtxClient -- this class
    // never touches ranking, awards, logbook, or any Jimmy business logic, matching
    // the Architecture Blueprint's Compatibility Layer boundary.
    //
    // Explicitly NOT moved here: sub-command 7 ("Ack Req", the handshake/cmdCheck
    // confirmation loop) -- reserved for Stage A7 (handshake redesign) and explicitly
    // off-limits for this stage (do not alter handshake/version-negotiation behavior).
    //
    // Two Phase 4 "Tier 1, disappears entirely" claims were investigated during this
    // stage and found incorrect against current code, so those sub-commands are
    // relocated here unchanged rather than removed (see the Stage A4 slice 1 commit
    // for the full reasoning):
    //   - Sub-command 16 (LoTW upload trigger): load-bearing for the Alt+U hotkey,
    //     no Jimmy-side replacement exists (LoTW uploads require WSJT-X's own TQSL
    //     certificate signing).
    //   - Sub-command 255 (broadcast/log QSO): also feeds WSJT-X's own internal
    //     "logged-before" tracking, which the still-wire-sourced IsNewCallOnBand/
    //     IsNewCallAnyBand/IsNewCountry fields may depend on (the A6 ClassificationEngine
    //     cutover hasn't happened yet).
    public class WsjtxCompatibilityExtension
    {
        private readonly WsjtxProtocolAdapter _protocolAdapter;

        // Own EnableTxMessage instance, not WsjtxClient's -- confirmed safe: emsg's
        // only uses anywhere in WsjtxClient are immediate build-then-send, plus a
        // ToString() in the debug log line immediately after (grepped for any other
        // read pattern, found none). SchemaVersion/Id initialization matches
        // WsjtxClient's own emsg construction exactly (WsjtxClient.cs's constructor:
        // "emsg = new EnableTxMessage(); emsg.Id = WsjtxMessage.UniqueId;" --
        // SchemaVersion is never set there either, stays at its default).
        private readonly EnableTxMessage _emsg;

        public WsjtxCompatibilityExtension(WsjtxProtocolAdapter protocolAdapter)
        {
            _protocolAdapter = protocolAdapter;
            _emsg = new EnableTxMessage();
            _emsg.Id = WsjtxMessage.UniqueId;
        }

        private UdpClient SendSocket => _protocolAdapter.SendSocket;

        private void Send(string label, int cmdIdx, Action<string> debugOutput)
        {
            if (EngineModeCutover.Mode == DecodeEngineMode.JimmyNative)
            {
                // Everything sent through this class is Andy WM8Q's fork-only "SetupTx RPC
                // channel" (see the class comment) -- Jimmy's native engine (Nexus/run_radio)
                // speaks only the standard WSJT-X UDP protocol and has no idea what these
                // sub-commands are; they were confirmed to land in Nexus's own inbound
                // dispatch's unmatched `_ => {}` catch-all and do nothing. Skip the send
                // entirely rather than emit dead traffic against a real WSJT-X-protocol peer.
                debugOutput?.Invoke($"[COMPAT-SKIP] '{label}' cmd:{cmdIdx} not sent -- Andy-fork-only, no effect on Jimmy Native engine");
                return;
            }
            byte[] ba = _emsg.GetBytes();
            SendSocket.Send(ba, ba.Length);
            debugOutput?.Invoke($">>>>>Sent '{label}' cmd:{cmdIdx}{Environment.NewLine}{_emsg}");
        }

        // -- sub-command 11: Enable Monitoring (WsjtxClient.EnableMonitoring) --
        public void EnableMonitoring(Action<string> debugOutput)
        {
            _emsg.NewTxMsgIdx = 11;
            _emsg.GenMsg = "";
            _emsg.CmdCheck = "";
            Send("Enable Monitoring", 11, debugOutput);
        }

        // -- sub-command 14: Set listen mode (WsjtxClient.SetListenMode) --
        public void SetListenMode(bool listenMode, Action<string> debugOutput)
        {
            _emsg.NewTxMsgIdx = 14;
            _emsg.Param0 = listenMode;
            _emsg.GenMsg = "";
            _emsg.CmdCheck = "";
            Send("Set listen mode", 14, debugOutput);
        }

        // -- sub-command 9: Enable Tx (WsjtxClient.EnableTx) --
        public void EnableTx(Action<string> debugOutput)
        {
            _emsg.NewTxMsgIdx = 9;
            _emsg.Param0 = true;       //WSJT-X Enable Tx button state
            _emsg.GenMsg = "";
            _emsg.CmdCheck = "";
            Send("Enable Tx", 9, debugOutput);
        }

        // -- sub-command 8: Disable Tx (WsjtxClient.DisableTx) --
        public void DisableTx(bool buttonState, Action<string> debugOutput)
        {
            _emsg.NewTxMsgIdx = 8;
            _emsg.Param0 = buttonState;    //set WSJT-X Enable Tx button state
            _emsg.GenMsg = "";
            _emsg.CmdCheck = "";
            Send("Disable Tx", 8, debugOutput);
        }

        // -- sub-command 12: Halt Tx (WsjtxClient.HaltTx) --
        public void HaltTx(Action<string> debugOutput)
        {
            _emsg.NewTxMsgIdx = 12;
            _emsg.GenMsg = "";
            _emsg.CmdCheck = "";
            Send("HaltTx", 12, debugOutput);
        }

        // -- sub-command 17: Set PSKReporter enable (WsjtxClient.TogglePskReporter) --
        // genMsg carries a literal identification string
        // ("(mod by KB0UZT, w/{pgmName} v{pgmVer} [FT8 for blind hams], qrz.com/db/KB0UZT)"),
        // built by the caller (WsjtxClient knows pgmName/pgmVer, this class shouldn't).
        public void SetPskReporterEnable(bool enable, string genMsg, Action<string> debugOutput)
        {
            _emsg.NewTxMsgIdx = 17;
            _emsg.Param0 = enable;
            _emsg.Param1 = false;        //ignored
            _emsg.Offset = 0;            //ignored
            _emsg.GenMsg = genMsg;
            _emsg.CmdCheck = "";         //ignored
            Send("Set PSKReporter", 17, debugOutput);
        }

        // -- sub-command 21: Set Operating Mode FT8/FT4 (WsjtxClient.SetOperatingMode) --
        public void SetOperatingMode(string newMode, Action<string> debugOutput)
        {
            _emsg.NewTxMsgIdx = 21;
            _emsg.GenMsg = newMode;
            _emsg.Param0 = false;
            _emsg.Param1 = false;
            _emsg.Param2 = 0;         //ignored
            Send("Operating Mode", 21, debugOutput);
        }

        // -- sub-command 15: Set Band / Tx First (WsjtxClient.SetBandTxFirst) --
        public void SetBandTxFirst(uint freq, bool state, Action<string> debugOutput)
        {
            _emsg.NewTxMsgIdx = 15;
            _emsg.Param0 = state;
            _emsg.Offset = freq;
            _emsg.GenMsg = "";          //ignored
            _emsg.CmdCheck = "";         //ignored
            Send("Set band / Tx first", 15, debugOutput);
        }

        // -- sub-command 18: Get Power/SWR (WsjtxClient.GetPowerSwr) --
        public void GetPowerSwr(Action<string> debugOutput)
        {
            _emsg.NewTxMsgIdx = 18;
            _emsg.Param0 = false;        //ignored
            _emsg.Offset = 0;            //ignored
            _emsg.GenMsg = "";          //ignored
            _emsg.CmdCheck = "";         //ignored
            Send("Get Power/SWR", 18, debugOutput);
        }

        // -- sub-command 20: Set Audio Level up/down (WsjtxClient.AdjAudioLevel) --
        public void AdjAudioLevel(bool up, Action<string> debugOutput)
        {
            _emsg.NewTxMsgIdx = 20;
            _emsg.Param0 = up;
            _emsg.Offset = 0;            //ignored
            _emsg.GenMsg = "";          //ignored
            _emsg.CmdCheck = "";         //ignored
            Send("Set Audio Level", 20, debugOutput);
        }

        // -- sub-command 19: Toggle Tuning (WsjtxClient.ToggleTuning) --
        public void ToggleTuning(bool cmdPrompts, Action<string> debugOutput)
        {
            _emsg.NewTxMsgIdx = 19;
            _emsg.Param0 = cmdPrompts;  //detail level
            _emsg.Offset = 0;            //ignored
            _emsg.GenMsg = "";          //ignored
            _emsg.CmdCheck = "";         //ignored
            Send("ToggleTuning", 19, debugOutput);
        }

        // -- sub-command 16: Start LoTW upload (WsjtxClient.StartUploadLotw) --
        // Kept (not removed) -- see this class's header comment for why the Phase 4
        // audit's "Tier 1, disappears entirely" classification for this one is wrong.
        public void StartUploadLotw(Action<string> debugOutput)
        {
            _emsg.NewTxMsgIdx = 16;
            _emsg.Param0 = false;         //ignored
            _emsg.Param1 = false;        //ignored
            _emsg.Offset = 0;            //ignored
            _emsg.GenMsg = "";          //ignored
            _emsg.CmdCheck = "";         //ignored
            Send("Start upload to LOTW", 16, debugOutput);
        }

        // -- sub-command 13: Reset Tx watchdog timer (embedded in the HeartbeatMessage
        // handler in WsjtxClient.Protocol.cs, immediately after the handshake's cmd:7
        // Ack Req -- distinct sub-command, not part of the cmd:7 handshake logic itself).
        // Does not use the shared Send() helper's emsg dump -- matching the original,
        // only a plain confirmation is logged for this one. --
        public void ResetTxWatchdog(Action<string> debugOutput)
        {
            _emsg.NewTxMsgIdx = 13;      //important! reset watchdog timer
            _emsg.GenMsg = "";          //no effect
            _emsg.ReplyReqd = false;     //no effect
            _emsg.EnableTimeout = true;  //no effect
            _emsg.CmdCheck = "";         //no effect
            if (EngineModeCutover.Mode == DecodeEngineMode.JimmyNative)
            {
                debugOutput?.Invoke("[COMPAT-SKIP] 'Reset Tx watchdog' cmd:13 not sent -- Andy-fork-only, no effect on Jimmy Native engine");
                return;
            }
            byte[] ba = _emsg.GetBytes();
            SendSocket.Send(ba, ba.Length);
            debugOutput?.Invoke(">>>>>Sent 'Reset Tx watchdog' cmd:13");
        }

        // -- sub-command 10: Opt Req -- sets SkipGrid/UseRR73/frequency offset.
        // 6 call sites in WsjtxClient.cs/WsjtxClient.Protocol.cs, all identical shape. --
        public void OptReq(bool skipGrid, bool useRR73, uint offset, Action<string> debugOutput)
        {
            _emsg.NewTxMsgIdx = 10;
            _emsg.GenMsg = "";          //no effect
            _emsg.SkipGrid = skipGrid;
            _emsg.UseRR73 = useRR73;
            _emsg.CmdCheck = "";         //ignored
            _emsg.Offset = offset;
            Send("Opt Req", 10, debugOutput);
        }

        // -- sub-command 6: Setup CQ (WsjtxClient.SetupCq) -- 2 call sites, identical shape. --
        public void SetupCq(string cqMsg, bool skipGrid, bool useRR73, Action<string> debugOutput)
        {
            _emsg.NewTxMsgIdx = 6;
            _emsg.GenMsg = cqMsg;
            _emsg.SkipGrid = skipGrid;
            _emsg.UseRR73 = useRR73;
            _emsg.CmdCheck = "";         //ignored
            Send("Setup CQ", 6, debugOutput);
        }

        // -- sub-command 0: De-init WSJT-X (WsjtxClient.Closing) --
        public void DeInit(bool skipGrid, bool useRR73, Action<string> debugOutput)
        {
            _emsg.NewTxMsgIdx = 0;           //de-init WSJT-X
            _emsg.GenMsg = "";         //ignored
            _emsg.SkipGrid = skipGrid;
            _emsg.UseRR73 = useRR73;
            _emsg.CmdCheck = "";         //ignored
            Send("De-init Req", 0, debugOutput);
        }

        // -- sub-command 255: Broadcast a Jimmy-initiated logged QSO for re-broadcast to
        // logging programs (WsjtxClient's RequestLog-style self-log path). Does not use
        // the shared Send() helper's emsg dump -- adifRecord travels in CmdCheck and can
        // be large, so (matching the original) only a plain confirmation is logged. --
        public void BroadcastLoggedQso(string call, string grid, string band, string mode, string adifRecord, Action<string> debugOutput)
        {
            _emsg.NewTxMsgIdx = 255;     //function code
            _emsg.GenMsg = $"{call}${grid}${band}${mode}";
            _emsg.Param0 = false;      //no effect
            _emsg.Param1 = false;      //no effect
            _emsg.CmdCheck = adifRecord;
            if (EngineModeCutover.Mode == DecodeEngineMode.JimmyNative)
            {
                debugOutput?.Invoke("[COMPAT-SKIP] 'Broadcast' cmd:255 not sent -- Andy-fork-only, no effect on Jimmy Native engine");
                _emsg.CmdCheck = "";
                return;
            }
            byte[] ba = _emsg.GetBytes();
            SendSocket.Send(ba, ba.Length);
            debugOutput?.Invoke(">>>>>Sent 'Broadcast' cmd:255");
            _emsg.CmdCheck = "";
        }
    }
}
