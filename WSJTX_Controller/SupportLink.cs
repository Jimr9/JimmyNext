using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;

namespace WSJTX_Controller
{
    // Setup links (2026-10-07): a helper copies a private link -- the recipient's base callsign
    // and download code -- and sends it; opening it starts Jimmy Next (or reaches the one already
    // running), which asks before remembering the code and fetching the settings. Nothing is
    // installed until the recipient chooses Install. The link is registered by the installer
    // (JimmyNext.wxs); a Debug build registers itself for this Windows user.
    //   jimmynext-support://profile?callsign=KD4DC&code=0123ABCD-...
    // Only that exact form is accepted; anything else is ignored. The link is never logged.
    internal static class SupportLink
    {
        internal const string Scheme = "jimmynext-support";

        internal static string Make(string callsign, string code)
        {
            string call = SupportService.BaseCallsign(callsign), c = SupportService.NormalizeCode(code);
            return call == null || c == null ? null : $"{Scheme}://profile?callsign={call}&code={c}";
        }

        // (callsign, code), or null when it is not a valid setup link.
        internal static (string Callsign, string Code)? Parse(string link)
        {
            if (string.IsNullOrEmpty(link) || link.Length > 200) return null;
            if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri)) return null;
            if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)) return null;
            if (!string.Equals(uri.Host, "profile", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.Trim('/').Length > 0) return null;
            string call = null, code = null;
            foreach (string part in uri.Query.TrimStart('?').Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) return null;
                string k = part.Substring(0, eq), v = Uri.UnescapeDataString(part.Substring(eq + 1));
                if (k.Equals("callsign", StringComparison.OrdinalIgnoreCase) && call == null) call = v;
                else if (k.Equals("code", StringComparison.OrdinalIgnoreCase) && code == null) code = v;
                else return null;
            }
            string baseCall = SupportService.BaseCallsign(call), c = SupportService.NormalizeCode(code);
            if (baseCall == null || c == null || !string.Equals(baseCall, (call ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return null;
            return (baseCall, c);
        }

        // The setup link among the program's arguments, if any.
        internal static string FromArgs(string[] args) =>
            (args ?? new string[0]).Skip(1).FirstOrDefault(a => a.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase));

        // ── Reaching the running Jimmy Next ─────────────────────────────────────────────────

        private static string PipeName =>
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Name.Replace(' ', '_') + "_SupportLink_" + Environment.UserName;

        // Started once by the running program; each valid link received is handed to onLink (on
        // a background thread -- the receiver moves it to the window's own thread).
        internal static void Listen(Action<string> onLink)
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly))
                        {
                            server.WaitForConnection();
                            var buffer = new byte[512];
                            int n = 0, r;
                            while (n < buffer.Length && (r = server.Read(buffer, n, buffer.Length - n)) > 0) n += r;
                            string link = Encoding.UTF8.GetString(buffer, 0, n);
                            if (Parse(link) != null) onLink(link);
                        }
                    }
                    catch { Thread.Sleep(1000); }
                }
            }) { IsBackground = true, Name = "SupportLinkListener" };
            t.Start();
        }

        // Called by a second start of the program that was opened with a link: hands the link to
        // the running one. False when it could not be reached.
        internal static bool SendToRunning(string link)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly))
                {
                    client.Connect(3000);
                    byte[] b = Encoding.UTF8.GetBytes(link);
                    client.Write(b, 0, b.Length);
                    client.Flush();
                    return true;
                }
            }
            catch { return false; }
        }

        // A Debug build registers the link for this Windows user, pointing at itself, so links can
        // be tried before an installer exists; the installed program is registered by the MSI.
        internal static void RegisterForDebugBuild()
        {
#if DEBUG
            try
            {
                string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Scheme))
                {
                    key.SetValue("", "URL:Jimmy Next support setup link");
                    key.SetValue("URL Protocol", "");
                    using (var cmd = key.CreateSubKey(@"shell\open\command"))
                        cmd.SetValue("", $"\"{exe}\" \"%1\"");
                }
            }
            catch { }
#endif
        }
    }
}
