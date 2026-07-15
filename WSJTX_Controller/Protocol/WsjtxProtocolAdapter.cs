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
        // here since external classes like Controller/OptionsDlg/SetupDlg read the
        // WsjtxClient-side delegating properties, never this class directly). --
        public IPAddress IpAddress { get; set; }
        public int Port { get; set; }
        public bool Multicast { get; set; }
        public bool OverrideUdpDetect { get; set; }

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
                Console.WriteLine($"Exception: ReceiveCallback() {err}");
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

        // Reads WSJT-X's own ini file for its configured UDP endpoint, so Jimmy can
        // auto-detect the address/port/multicast settings instead of requiring manual
        // configuration. Returns false (with IPv4 loopback / port 2237 / unicast
        // defaults) if WSJT-X's settings folder or ini file isn't present or readable.
        public static bool DetectUdpSettings(out IPAddress ipa, out int prt, out bool mul)
        {
            //use WSJT-X.ini file for settings
            string pgmNameWsjtx = "WSJT-X";
            string pathWsjtx = $"{Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}\\{pgmNameWsjtx}";
            string pathFileNameExtWsjtx = pathWsjtx + "\\" + pgmNameWsjtx + ".ini";

            //set defaults
            ipa = IPAddress.Parse("127.0.0.1");
            prt = 2237;
            mul = false;

            //temp
            IPAddress ipaAddr;
            int prtInt;
            string ipaString;

            if (!Directory.Exists(pathWsjtx)) return false;

            try
            {
                IniFile iniFile = new IniFile(pathFileNameExtWsjtx);
                ipaString = iniFile.Read("UDPServer", "Configuration");
                ipaAddr = IPAddress.Parse(ipaString);
                prtInt = Convert.ToInt32(iniFile.Read("UDPServerPort", "Configuration"));
            }
            catch
            {
                return false;
            }

            if (ipaString == "" || prtInt == 0)
            {
                return false;
            }

            prt = prtInt;
            ipa = ipaAddr;
            mul = ipaString.Substring(0, 4) != "127.";
            return true;
        }

        // WSJT-X creates this lock file while running (and only while running) --
        // Jimmy's cheapest, most reliable "is WSJT-X up right now" check, requiring no
        // socket/process-list access.
        public static bool IsWsjtxRunning()
        {
            string file = "WSJT-X.lock";
            string pathFileNameExt = $"{Path.GetTempPath()}{file}";
            return File.Exists(pathFileNameExt);
        }
    }
}
