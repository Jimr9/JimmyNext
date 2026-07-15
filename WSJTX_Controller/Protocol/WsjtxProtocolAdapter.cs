using System;
using System.IO;
using System.Net;

namespace WSJTX_Controller
{
    // Migration Stage A3 (Jimmy_Master_Migration_Roadmap.md / Architecture Blueprint):
    // first slice of WsjtxClient.Protocol.cs's parsing/building logic moved behind a
    // clean internal boundary, zero wire-format/behavior change.
    //
    // Scope note: the Architecture Blueprint's end-state WsjtxProtocolAdapter owns the
    // UDP socket itself and all standard message parsing/building. In this codebase,
    // WsjtxClient.Protocol.cs's socket state (udpClient, udpClient2, endPoint, etc.) is
    // referenced from 50+ call sites across WsjtxClient.cs and its other partial-class
    // files, and its message dispatch (Update()) fuses standard-message parsing with
    // Jimmy's own business logic (call queue, band/mode change handling) in the same
    // per-message-type blocks rather than cleanly separating them. Moving that safely
    // -- without risking a subtle behavior change in Jimmy's most protocol-critical
    // code -- is a much larger, separate piece of work than one sitting safely covers.
    //
    // This first slice extracts only the two methods that were already fully
    // self-contained (no shared mutable state, no other call sites anywhere in the
    // app) -- a genuinely zero-risk move, not a cosmetic one. The socket-lifecycle
    // methods (UdpLoop, ReceiveCallback, CheckWsjtxRunning, CloseAllUdp,
    // UpdateAddrPortMulti) and the Update() dispatch method remain in
    // WsjtxClient.Protocol.cs for a follow-up stage.
    public static class WsjtxProtocolAdapter
    {
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
