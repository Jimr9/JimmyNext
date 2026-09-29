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
    // and other loggers record it (operator request 2026-09-29). The engine reports the setting as
    // a fraction of full power; Hamlib's own power2mW turns that into watts for this radio model
    // (a 10 W X6100 at 50% is 5 W, a 100 W TS-590 at 40% is 40 W). Asked of the rigctld the engine
    // already runs -- a separate client, which rigctld serves in turn; power2mW is Hamlib's own
    // arithmetic from the radio's published range, not a command to the radio. Asked again only
    // when the setting changes. Unknown (radio does not report it, no rigctld): logged blank.
    internal static class RadioPower
    {
        private sealed class Reading { public double Fraction; public int Watts; }
        private static volatile Reading _last;
        private static int _busy;
        private static DateTime _lastAskUtc = DateTime.MinValue;

        // Called with every engine status: the current setting (0.0-1.0) and dial.
        internal static void Observe(double? rfPower, double dialMhz, RadioSettings radio, Action<string> debug)
        {
            if (TestModeGuard.IsTestMode || rfPower == null || rfPower <= 0 || dialMhz <= 0) return;
            if (radio == null || radio.Mode != RadioControlMode.HamlibRigctld) return;
            double fraction = Math.Round(rfPower.Value, 3);
            if (_last != null && _last.Fraction == fraction) return;
            if (DateTime.UtcNow - _lastAskUtc < TimeSpan.FromSeconds(5)) return;   // while a knob is turning
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            _lastAskUtc = DateTime.UtcNow;
            string host = radio.UseExternalRigctld && !string.IsNullOrWhiteSpace(radio.RigctldHost) ? radio.RigctldHost.Trim() : "127.0.0.1";
            int port = radio.RigctldPort;
            long hz = (long)Math.Round(dialMhz * 1e6);
            Task.Run(() =>
            {
                try
                {
                    int? watts = AskWatts(host, port, fraction, hz, out string reply);
                    debug?.Invoke($"[POWER] setting {fraction:0.###} -> {(watts.HasValue ? watts + " W" : "unknown")} (rigctld: {reply})");
                    if (watts.HasValue) _last = new Reading { Fraction = fraction, Watts = watts.Value };
                }
                catch (Exception ex) { debug?.Invoke("[POWER] " + ex.Message); }
                finally { Interlocked.Exchange(ref _busy, 0); }
            }).ObserveFault();
        }

        // The watts for this setting, or "" when not known for exactly this setting.
        internal static string WattsText(double? rfPower)
        {
            var last = _last;
            if (rfPower == null || last == null || last.Fraction != Math.Round(rfPower.Value, 3)) return "";
            return last.Watts.ToString(CultureInfo.InvariantCulture);
        }

        internal static void SetForTest(double fraction, int watts) => _last = new Reading { Fraction = fraction, Watts = watts };
        internal static void ResetForTest() => _last = null;

        // rigctld's "\power2mW <fraction> <frequency Hz> <mode>": milliwatts on the first line.
        private static int? AskWatts(string host, int port, double fraction, long hz, out string reply)
        {
            reply = "";
            using (var client = new TcpClient())
            {
                var connect = client.ConnectAsync(host, port).ObserveFault();
                if (!connect.Wait(1000) || !client.Connected) { reply = "no connection"; return null; }
                using (var stream = client.GetStream())
                {
                    stream.ReadTimeout = 2000;
                    stream.WriteTimeout = 1000;
                    byte[] cmd = Encoding.ASCII.GetBytes($"\\power2mW {fraction.ToString("0.###", CultureInfo.InvariantCulture)} {hz} USB\n");
                    stream.Write(cmd, 0, cmd.Length);
                    using (var reader = new StreamReader(stream, Encoding.ASCII))
                        reply = (reader.ReadLine() ?? "").Trim();
                }
            }
            if (!double.TryParse(reply, NumberStyles.Float, CultureInfo.InvariantCulture, out double mw) || mw <= 0) return null;
            return (int)Math.Round(mw / 1000.0);
        }
    }
}
