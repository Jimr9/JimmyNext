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
    }
}
