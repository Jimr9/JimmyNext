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
    internal static class RadioPower
    {
        private sealed class Reading { public int Watts; public DateTime AtUtc; }
        private static volatile Reading _last;
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
            if (DateTime.UtcNow - _lastAskUtc < AskEvery) return;
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            _lastAskUtc = DateTime.UtcNow;
            string host = radio.UseExternalRigctld && !string.IsNullOrWhiteSpace(radio.RigctldHost) ? radio.RigctldHost.Trim() : "127.0.0.1";
            int port = radio.RigctldPort;
            long hz = (long)Math.Round(dialMhz * 1e6);
            Task.Run(() =>
            {
                try
                {
                    int? watts = kenwood ? AskKenwoodWatts(host, port, out string detail) : AskWatts(host, port, hz, out detail);
                    debug?.Invoke($"[POWER] {(watts.HasValue ? watts + " W" : "unknown")} ({detail})");
                    if (watts.HasValue) _last = new Reading { Watts = watts.Value, AtUtc = DateTime.UtcNow };
                }
                catch (Exception ex) { debug?.Invoke("[POWER] " + ex.Message); }
                finally { Interlocked.Exchange(ref _busy, 0); }
            }).ObserveFault();
        }

        // The radio's set power in watts, or "" when not read recently.
        internal static string WattsText()
        {
            var last = _last;
            if (last == null || DateTime.UtcNow - last.AtUtc > GoodFor) return "";
            return last.Watts.ToString(CultureInfo.InvariantCulture);
        }

        internal static void SetForTest(int watts, DateTime atUtc) => _last = new Reading { Watts = watts, AtUtc = atUtc };
        internal static void ResetForTest() => _last = null;

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
                using (var reader = new StreamReader(stream, Encoding.ASCII))
                {
                    stream.ReadTimeout = 2000;
                    stream.WriteTimeout = 1000;
                    byte[] b = Encoding.ASCII.GetBytes("w PC;\n");
                    stream.Write(b, 0, b.Length);
                    string reply = (reader.ReadLine() ?? "").Trim();
                    detail = $"Kenwood PC; answered {reply}";
                    return ParseKenwoodPc(reply);
                }
            }
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
