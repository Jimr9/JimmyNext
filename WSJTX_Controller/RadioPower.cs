using System;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WSJTX_Controller
{
    // The power the radio is SET to, in watts, for the TX_PWR a contact is logged with -- as QLog
    // and other loggers record it (operator request 2026-09-29).
    //
    // READ ONLY, and read by Jimmy itself: the engine keeps its RFPOWER never-touch patch (no
    // power WRITE can reach the radio, and Nexus's own RFPOWER read stays off). Asked of the
    // rigctld the engine runs -- a separate client, which rigctld serves in turn. Once when the
    // radio is first seen, then once a minute to follow a knob change.
    //
    //  - Kenwood-family radios (Hamlib models 2xxx): the radio's own `PC;` query, sent raw
    //    (`w PC;`, the same route as the tuner start's `AC;`); the answer is the setting in watts
    //    ("PC100;"). NEVER Hamlib's `l RFPOWER` there: on a fresh rigctld its Kenwood backend runs
    //    a power calibration sweep that leaves the radio at 5 W (Hamlib/Hamlib#1595; confirmed
    //    on a TS-590SG with Hamlib 4.7.1 on 2026-08-20 and again 2026-09-29; Nexus issue #381
    //    is the same symptom on a TS-590S).
    //  - Other radios: Hamlib's `l RFPOWER` (the setting, 0.0-1.0) and `\power2mW` for the model
    //    (a 10 W X6100 at 50% is 5 W).
    //
    // A reading belongs to the radio and profile it was read from (release blocker 2026-09-29): a
    // profile switch (Reset) or a different radio clears it, a reply that arrives after the
    // switch is dropped, and the log gets no power until the current radio has answered.
    internal static class RadioPower
    {
        private sealed class Reading { public int Watts; public DateTime AtUtc; public string Key; }
        private static volatile Reading _last;
        private static volatile string _radioKey = "";
        private static long _generation;
        private static int _busy;
        private static DateTime _lastAskUtc = DateTime.MinValue;
        private static readonly TimeSpan AskEvery = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan GoodFor = TimeSpan.FromMinutes(3);

        // Called with every engine status.
        internal static void Poll(double dialMhz, RadioSettings radio, Action<string> debug)
        {
            if (TestModeGuard.IsTestMode || dialMhz <= 0) return;
            if (radio == null || radio.Mode != RadioControlMode.HamlibRigctld) return;
            bool kenwood = IsKenwoodFamily(radio.RigModel);
            string host = radio.UseExternalRigctld && !string.IsNullOrWhiteSpace(radio.RigctldHost) ? radio.RigctldHost.Trim() : "127.0.0.1";
            int port = radio.RigctldPort;
            string key = RadioKey(radio.RigModel, host, port, SafeProfile());
            if (key != _radioKey) { Reset(); _radioKey = key; }
            if (DateTime.UtcNow - _lastAskUtc < AskEvery) return;
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            _lastAskUtc = DateTime.UtcNow;
            long gen = Interlocked.Read(ref _generation);
            long hz = (long)Math.Round(dialMhz * 1e6);
            Task.Run(() =>
            {
                try
                {
                    int? watts = kenwood ? AskKenwoodWatts(host, port, out string detail) : AskWatts(host, port, hz, out detail);
                    if (!Accept(gen, key, watts)) { debug?.Invoke($"[POWER] reply from the previous radio dropped ({detail})"); return; }
                    debug?.Invoke($"[POWER] {(watts.HasValue ? watts + " W" : "unknown")} ({detail})");
                }
                catch (Exception ex) { debug?.Invoke("[POWER] " + ex.Message); }
                finally { Interlocked.Exchange(ref _busy, 0); }
            }).ObserveFault();
        }

        // The current radio's set power in watts, or "" when it has not been read recently.
        internal static string WattsText()
        {
            var last = _last;
            if (last == null || last.Key != _radioKey || DateTime.UtcNow - last.AtUtc > GoodFor) return "";
            return last.Watts.ToString(CultureInfo.InvariantCulture);
        }

        // Profile switch (the window closing) or a different radio: forget the reading, drop any
        // reply still on its way, and ask the new radio at once.
        internal static void Reset()
        {
            Interlocked.Increment(ref _generation);
            _last = null;
            _radioKey = "";
            _lastAskUtc = DateTime.MinValue;
        }

        internal static string RadioKey(string rigModel, string host, int port, string profile) =>
            $"{(rigModel ?? "").Trim()}|{host}:{port}|{profile}";

        private static string SafeProfile()
        {
            try { return Controller.ActiveIniFilePath(); } catch { return ""; }
        }

        // Keeps a reply only if no switch happened since it was asked.
        internal static bool Accept(long askedGeneration, string askedKey, int? watts)
        {
            if (askedGeneration != Interlocked.Read(ref _generation) || askedKey != _radioKey) return false;
            if (watts.HasValue) _last = new Reading { Watts = watts.Value, AtUtc = DateTime.UtcNow, Key = askedKey };
            return true;
        }

        internal static long GenerationForTest => Interlocked.Read(ref _generation);
        internal static void SetForTest(int watts, DateTime atUtc, string key = "test")
        {
            _radioKey = key;
            _last = new Reading { Watts = watts, AtUtc = atUtc, Key = key };
        }
        internal static void SetCurrentRadioForTest(string key) => _radioKey = key;
        internal static void ResetForTest() => Reset();

        // Hamlib's Kenwood backend: model numbers 2000-2999.
        internal static bool IsKenwoodFamily(string rigModel) =>
            int.TryParse((rigModel ?? "").Trim(), out int m) && m / 1000 == 2;

        // "PC100;" -> 100 (watts); null for anything else.
        internal static int? ParseKenwoodPc(string reply)
        {
            string r = (reply ?? "").Trim().TrimEnd('\0', ';');
            if (!r.StartsWith("PC", StringComparison.Ordinal)) return null;
            return int.TryParse(r.Substring(2), NumberStyles.None, CultureInfo.InvariantCulture, out int w) && w > 0 && w <= 2000 ? w : (int?)null;
        }

        // The Kenwood radio's own power query, raw through rigctld: `w PC;` -> "PC100;".
        private static int? AskKenwoodWatts(string host, int port, out string detail)
        {
            detail = "";
            using (var client = new TcpClient())
            {
                var connect = client.ConnectAsync(host, port).ObserveFault();
                if (!connect.Wait(1000) || !client.Connected) { detail = "no rigctld connection"; return null; }
                using (var stream = client.GetStream())
                {
                    stream.ReadTimeout = 2000;
                    stream.WriteTimeout = 1000;
                    byte[] b = Encoding.ASCII.GetBytes("w PC;\n");
                    stream.Write(b, 0, b.Length);
                    string reply = ReadRawReply(stream).Trim();
                    detail = $"Kenwood PC; answered {reply}";
                    return ParseKenwoodPc(reply);
                }
            }
        }

        // rigctld's `w` (raw CAT) answer is the radio's own string ended by a NUL and NO newline
        // ("PC100;" + NUL, as Nexus's Rig::command_inner documents) -- a ReadLine waits the whole
        // timeout for a newline that never comes (2026-09-30: every power read failed so). Read to
        // either terminator, or the radio's own ';'.
        internal static string ReadRawReply(Stream stream)
        {
            var sb = new StringBuilder();
            var buf = new byte[64];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 2000)
            {
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                string t = sb.ToString();
                if (t.IndexOf('\0') >= 0 || t.IndexOf('\n') >= 0 || t.TrimEnd().EndsWith(";")) break;
            }
            return sb.ToString().Replace("\0", "");
        }

        // `l RFPOWER` (the setting as a fraction), then `\power2mW <fraction> <Hz> USB` (milliwatts).
        private static int? AskWatts(string host, int port, long hz, out string detail)
        {
            detail = "";
            using (var client = new TcpClient())
            {
                var connect = client.ConnectAsync(host, port).ObserveFault();
                if (!connect.Wait(1000) || !client.Connected) { detail = "no rigctld connection"; return null; }
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.ASCII))
                {
                    stream.ReadTimeout = 2000;
                    stream.WriteTimeout = 1000;
                    string Ask(string cmd)
                    {
                        byte[] b = Encoding.ASCII.GetBytes(cmd + "\n");
                        stream.Write(b, 0, b.Length);
                        return (reader.ReadLine() ?? "").Trim();
                    }
                    string level = Ask("l RFPOWER");
                    if (!double.TryParse(level, NumberStyles.Float, CultureInfo.InvariantCulture, out double fraction) || fraction <= 0 || fraction > 1)
                    { detail = $"setting: {level}"; return null; }
                    string mw = Ask($"\\power2mW {fraction.ToString("0.###", CultureInfo.InvariantCulture)} {hz} USB");
                    detail = $"setting {fraction:0.###}, power2mW {mw}";
                    if (!double.TryParse(mw, NumberStyles.Float, CultureInfo.InvariantCulture, out double milliwatts) || milliwatts <= 0) return null;
                    return (int)Math.Round(milliwatts / 1000.0);
                }
            }
        }
    }
}
