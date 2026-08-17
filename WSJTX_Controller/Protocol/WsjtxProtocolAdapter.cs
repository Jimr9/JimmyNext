using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using WsjtxUdpLib.Messages;
using WsjtxUdpLib.Messages.Out;

namespace WSJTX_Controller
{
    // Pairs a UdpClient with the endpoint an in-flight BeginReceive/EndReceive call
    // belongs to. Moved here from WsjtxClient.cs (Stage A3, slice 2) -- purely a
    // parameter bag for ReceiveCallback, no business-logic dependency.
    public struct UdpState
    {
        public UdpClient u;
        public IPEndPoint e;
    }

    // Test/replay-only in current production (see WsjtxClient.Protocol.cs's own top-of-file
    // banner comment): the sockets this owns are opened only via ConnectNativeEngine, which
    // Controller.ApplyEngineMode() calls only under TestModeGuard.IsTestMode. Real production
    // always uses Direct (WsjtxClient.Direct.cs) and never opens a UDP socket at all. Kept
    // because run_replay_tests.bat's JimmyReplay.py genuinely depends on this socket mechanics
    // working end to end against a real running Jimmy Test.exe.
    //
    // Migration Stage A3 (Jimmy_Master_Migration_Roadmap.md / Architecture Blueprint):
    // Protocol Adapter boundary for WsjtxClient.Protocol.cs's socket ownership, zero
    // wire-format/behavior change.
    //
    // Scope note (see slice 1's comment history for the full reasoning): this owns the
    // UDP socket state and the purely-mechanical socket operations (opening/closing the
    // sockets, completing an async receive). It deliberately does NOT own connection
    // retry/error-dialog orchestration (CheckWsjtxRunning), the receive-loop driver
    // (UdpLoop), or any part of Update()'s message dispatch -- those mix in WsjtxClient/
    // Controller-level decisions (which dialog to show, when to call Update(), timer
    // state) that the Architecture Blueprint's own module boundary keeps out of the
    // Protocol Adapter ("Never: touches ranking, awards, logbook, or any Jimmy business
    // logic"). WsjtxClient keeps thin wrapper methods with the original names/signatures
    // so every existing call site (50+, across WsjtxClient's other partial-class files)
    // keeps working unchanged.
    public class WsjtxProtocolAdapter
    {
        // -- Socket configuration (moved from WsjtxClient's public fields; still public
        // here since external classes like Controller/OptionsDlg read the WsjtxClient-side
        // delegating properties, never this class directly). --
        public IPAddress IpAddress { get; set; }
        public int Port { get; set; }
        public bool Multicast { get; set; }

        // -- Socket state (moved from WsjtxClient's private fields). ReceiveSocket was
        // "udpClient" (the BeginReceive/EndReceive socket); SendSocket was "udpClient2"
        // (the one every outgoing message is sent through). messageRecd/datagram/fromEp/
        // recvStarted were "private static" on WsjtxClient -- a historical artifact with
        // no observable effect in this single-instance app (only one WsjtxClient, hence
        // only one WsjtxProtocolAdapter, is ever constructed) -- now plain instance state
        // here, behavior-identical. --
        public UdpClient ReceiveSocket { get; set; }
        public UdpClient SendSocket { get; set; }
        public IPEndPoint EndPoint { get; set; }
        public AsyncCallback AsyncCallback { get; set; }
        public UdpState UdpSt { get; set; }
        public bool MessageRecd { get; set; }
        public byte[] Datagram { get; set; }
        public IPEndPoint FromEp { get; set; } = new IPEndPoint(IPAddress.Any, 0);
        public bool RecvStarted { get; set; }

        // Stage A3 slice 3: the one genuinely-separable piece of Update()'s message
        // dispatch -- the literal WsjtxMessage.Parse(datagram) call, with zero business
        // logic attached. Everything downstream of a successful parse (per-message-type
        // reactions, negotiation-state transitions, call-queue/award/logbook side
        // effects) stays in WsjtxClient.Update(), correctly outside the Protocol
        // Adapter's boundary per the Architecture Blueprint ("Never: touches ranking,
        // awards, logbook, or any Jimmy business logic").
        public WsjtxMessage TryParse(byte[] datagram, out ParseFailureException parseError)
        {
            parseError = null;
            try
            {
                return WsjtxMessage.Parse(datagram);
            }
            catch (ParseFailureException ex)
            {
                parseError = ex;
                return null;
            }
        }

        // Completes an in-flight BeginReceive. Moved verbatim from WsjtxClient.Protocol.cs
        // -- every touch here was already one of this class's own properties (via
        // WsjtxMessage.NegoState, a static property on the shared protocol library, plus
        // the receive state above), so this had zero true WsjtxClient-instance dependency
        // to begin with.
        public void ReceiveCallback(IAsyncResult ar)
        {
            Datagram = null;
            MessageRecd = true;

            try
            {
                if (WsjtxMessage.NegoState == WsjtxMessage.NegoStates.WAIT) return;
                UdpClient u = ((UdpState)(ar.AsyncState)).u;
                if (WsjtxMessage.NegoState == WsjtxMessage.NegoStates.WAIT) return;
                var fromEp = ((UdpState)(ar.AsyncState)).e;
                if (WsjtxMessage.NegoState == WsjtxMessage.NegoStates.WAIT) return;
                Datagram = u.EndReceive(ar, ref fromEp);
                FromEp = fromEp;
            }
            catch (Exception err)
            {
#if DEBUG
                // This class's sockets are only ever opened by ConnectNativeEngine
                // (WsjtxClient.Protocol.cs), itself only reachable via
                // TestModeGuard.IsTestMode -- and run_replay_tests.bat always runs the DEBUG
                // build (see that script's own JIMMY_EXE path), so this branch is exactly the
                // one place this callback is ever genuinely exercised. Logged here for a
                // developer diagnosing a replay-test failure, not swallowed.
                Console.WriteLine($"Exception: ReceiveCallback() {err}");
#else
                // Release/production never reaches this callback at all (see above) -- no
                // Notify/status-reporting reference exists on this class to route it to even if
                // it did (deliberately: "zero business-logic dependency", this class's own
                // header comment). Nothing meaningful to do with err here; discard explicitly
                // rather than leave an unused-variable warning unexplained.
                _ = err;
#endif
                return;
            }
        }

        // Opens ReceiveSocket per the current IpAddress/Port/Multicast configuration.
        // Extracted from CheckWsjtxRunning's inner try block verbatim -- the retry loop,
        // debug logging, and error-dialog handling around this stay in WsjtxClient
        // (Controller/UI-level orchestration decisions, not socket mechanics).
        public bool TryOpenReceiveSocket(out Exception error)
        {
            error = null;
            try
            {
                if (Multicast)
                {
                    ReceiveSocket = new UdpClient();
                    ReceiveSocket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    ReceiveSocket.Client.Bind(EndPoint = new IPEndPoint(IPAddress.Any, Port));
                    ReceiveSocket.JoinMulticastGroup(IpAddress);
                }
                else
                {
                    ReceiveSocket = new UdpClient(EndPoint = new IPEndPoint(IpAddress, Port));
                }
                return true;
            }
            catch (Exception e)
            {
                error = e;
                return false;
            }
        }

        // Closes and nulls out both sockets, matching CloseAllUdp's original try/catch
        // shape exactly. debugOutput is optional so callers can preserve their own
        // existing log line wording/timestamp prefix -- this doesn't add or remove any
        // diagnostic log content, just relocates where the strings are built.
        public void Close(Action<string> debugOutput = null)
        {
            try
            {
                if (ReceiveSocket != null)
                {
                    ReceiveSocket.Close();
                    ReceiveSocket = null;
                    debugOutput?.Invoke("closed udpClient");
                }
                if (SendSocket != null)
                {
                    SendSocket.Close();
                    SendSocket = null;
                    debugOutput?.Invoke("closed udpClient2");
                }
            }
            catch (Exception e)
            {
                debugOutput?.Invoke($"error:{e}");
            }
        }

    }
}
