using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace WSJTX_Controller
{
    // Output to other programs (2026-10-12): the WSJT-X-format messages the engine sends --
    // heartbeat, status, each decode, and each contact as it is logged (QSOLogged, then
    // LoggedADIF) -- so a logging or mapping program can run beside Jimmy Next without a
    // second WSJT-X. It was always on, to 127.0.0.1 on Jimmy's port (2237), with no way to see
    // or change it; it is now a setting, with that same destination as its default.
    //
    // Receive-only. The engine host passes these messages through its own relay
    // (EngineHost wsjtx_out.rs), and whatever another program sends back -- a Reply, free
    // text, Halt Tx -- is dropped there. Another program can watch and log; it can never
    // transmit, call a station, or move the radio.
    //
    // This class holds the rules for the destination list; it sends nothing itself.
    internal static class OtherPrograms
    {
        internal const string EnabledKey = "otherProgramsOutput", TargetsKey = "otherProgramsTargets";

        internal static string DefaultTarget(int jimmyPort) => "127.0.0.1:" + (jimmyPort > 0 ? jimmyPort : 2237);

        internal sealed class Check
        {
            public List<string> Targets = new List<string>();   // normalized "address:port"
            public string Error;                                 // not null: nothing is saved
            public string Warning;                               // not null: ask before saving
            public string Normalized => string.Join(",", Targets);
        }

        // text: "address:port", one or several separated by commas, semicolons or spaces.
        // reserved: ports Jimmy Next itself listens on at 127.0.0.1 (its engine's control port,
        // the radio's rigctld port), which must never be a destination.
        internal static Check Validate(string text, IEnumerable<int> reserved)
        {
            var check = new Check();
            var busy = new HashSet<int>(reserved ?? new int[0]);
            var entries = (text ?? "").Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (entries.Length == 0) { check.Error = "Enter where to send, as address and port, for example 127.0.0.1:2237."; return check; }
            var elsewhere = new List<string>();
            foreach (string raw in entries)
            {
                string entry = raw.Trim();
                if (!IPEndPoint.TryParse(entry, out var ep) || entry.IndexOf(':') < 0)
                { check.Error = $"'{entry}' is not an address and port, such as 127.0.0.1:2237. A computer name cannot be used; use its number."; return check; }
                if (ep.Port < 1 || ep.Port > 65535) { check.Error = $"'{entry}': the port must be between 1 and 65535."; return check; }
                if (ep.Address.Equals(IPAddress.Any) || ep.Address.Equals(IPAddress.IPv6Any))
                { check.Error = $"'{entry}' is not an address a program listens on."; return check; }
                if (IPAddress.IsLoopback(ep.Address) && busy.Contains(ep.Port))
                { check.Error = $"'{entry}': port {ep.Port} on this computer is used by Jimmy Next itself. Choose the port the other program listens on."; return check; }
                string normalized = ep.ToString();
                if (check.Targets.Contains(normalized)) continue;   // the same one twice is one
                check.Targets.Add(normalized);
                if (!IPAddress.IsLoopback(ep.Address)) elsewhere.Add(normalized);
            }
            if (check.Targets.Count > 8) { check.Error = "No more than 8 destinations."; return check; }
            if (elsewhere.Count > 0)
                check.Warning = $"{string.Join(" and ", elsewhere)} {(elsewhere.Count == 1 ? "is" : "are")} not on this computer. " +
                                "Your decodes and logged contacts will be sent over the network to " + (elsewhere.Count == 1 ? "it" : "them") + ", unencrypted.";
            return check;
        }

        // What the engine is started with: "off", or the destinations.
        internal static string EngineArg(bool enabled, string targets, int jimmyPort)
        {
            if (!enabled) return "off";
            var check = Validate(string.IsNullOrWhiteSpace(targets) ? DefaultTarget(jimmyPort) : targets, null);
            return check.Error == null ? check.Normalized : DefaultTarget(jimmyPort);
        }

        // The engine's answer to WSJTX_OUT_STATUS as plain sentences. Sent is not received:
        // UDP gives no receipt, so this never says a program "is connected".
        internal static string StatusText(string json)
        {
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(json ?? ""))
                {
                    var root = doc.RootElement;
                    string state = root.TryGetProperty("state", out var s) ? s.GetString() : null;
                    if (state != "on") return "Output to other programs is off in the running engine.";
                    long sent = root.TryGetProperty("sent", out var n) ? n.GetInt64() : 0;
                    long ignored = root.TryGetProperty("ignoredInbound", out var ig) ? ig.GetInt64() : 0;
                    long errors = root.TryGetProperty("sendErrors", out var er) ? er.GetInt64() : 0;
                    var lines = new List<string>();
                    foreach (var t in root.GetProperty("targets").EnumerateArray())
                    {
                        string addr = t.GetProperty("addr").GetString();
                        bool heard = t.TryGetProperty("heardSecsAgo", out var h) && h.ValueKind == System.Text.Json.JsonValueKind.Number;
                        lines.Add(heard
                            ? $"{addr}: sending. Something answered from that address {Ago(h.GetInt64())} ago, so a program is there."
                            : $"{addr}: sending. Nothing has come back from it. That is normal for a program that only listens, and also what a wrong port looks like.");
                    }
                    lines.Add($"{sent:N0} message{(sent == 1 ? "" : "s")} sent since the engine started.");
                    if (errors > 0) lines.Add($"{errors:N0} could not be sent" + (root.TryGetProperty("lastSendError", out var le) && le.ValueKind == System.Text.Json.JsonValueKind.String ? $" (last: {le.GetString()})." : "."));
                    if (ignored > 0) lines.Add($"{ignored:N0} message{(ignored == 1 ? "" : "s")} from other programs ignored. Other programs cannot transmit or control the radio through Jimmy Next.");
                    return string.Join(Environment.NewLine, lines);
                }
            }
            catch { return "The engine did not answer. It reports this only while it is running with a radio."; }
        }

        private static string Ago(long secs) => secs < 90 ? $"{secs} seconds" : secs < 5400 ? $"{secs / 60} minutes" : $"{secs / 3600} hours";
    }
}
