using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

namespace WSJTX_Controller
{
    // One poll's worth of radio state. Fields are nullable -- a rig/backend not supporting a
    // given query (e.g. SWR on many rigs) is expected, not an error; leave that field null
    // rather than fail the whole poll.
    public class RadioStatus
    {
        public bool Ok;
        public string LastError;
        public ulong? FrequencyHz;
        public string Mode;
        public bool? Ptt;
        public int? SMeterDb;
        public double? PowerRaw;   // Hamlib's 0.0-1.0 relative scale, NOT calibrated watts
        public double? Swr;
    }

    // Plain TCP text-protocol client for Hamlib's rigctld (self-sufficiency plan, Phase 1),
    // plus process management for Jimmy's own bundled copy -- mirrors Nexus's approach of
    // bundling and auto-launching rigctld rather than making the operator find and run it
    // separately (see scripts/fetch-hamlib.ps1). No P/Invoke, no native dependency beyond the
    // bundled binary itself.
    //
    // Not thread-safe by design: meant to be owned and polled by one dedicated timer, matching
    // WsjtxClient's existing timer pattern (e.g. StartStatusTimer2), not called concurrently
    // from a background Task. LastError property with no exceptions for expected failures
    // follows the same convention as QrzLogbookClient/ClubLogUploadClient/LoTWQsoClient.
    public class RigctldClient : IDisposable
    {
        public string LastError { get; private set; }

        private readonly string _host;
        private readonly int _port;
        private TcpClient _tcp;
        private NetworkStream _stream;
        private StreamReader _reader;
        private StreamWriter _writer;
        private Process _bundledProcess;

        public RigctldClient(string host, int port)
        {
            _host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host;
            _port = port > 0 ? port : 4532;   // Hamlib's documented rigctld default
        }

        public bool IsConnected => _tcp != null && _tcp.Connected;
        public bool BundledProcessRunning => _bundledProcess != null && !_bundledProcess.HasExited;

        // Locates the bundled rigctld.exe next to Jimmy.exe (Resources\hamlib\rigctld.exe,
        // staged by scripts/fetch-hamlib.ps1 and packaged by the WiX installer). Returns null,
        // not an exception, if it isn't there -- callers degrade to "use external rigctld
        // instead" guidance rather than crash.
        public static string LocateBundledExe()
        {
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(exeDir)) return null;
            string path = Path.Combine(exeDir, "Resources", "hamlib", "rigctld.exe");
            return File.Exists(path) ? path : null;
        }

        // Launches Jimmy's own bundled rigctld against the configured rig model/COM port,
        // listening on this client's configured port. Returns false (LastError set) rather
        // than throwing if the bundled copy is missing or the rig model isn't configured.
        public bool LaunchBundled(string rigModel, string comPort)
        {
            LastError = null;
            try
            {
                string rigctldPath = LocateBundledExe();
                if (rigctldPath == null)
                {
                    LastError = "Bundled rigctld.exe not found. Reinstall Jimmy, or configure an external rigctld instead.";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(rigModel))
                {
                    LastError = "No rig model configured -- choose one in Options > Radio.";
                    return false;
                }

                var args = $"-m {rigModel}";
                if (!string.IsNullOrWhiteSpace(comPort)) args += $" -r {comPort}";
                args += $" -t {_port}";

                _bundledProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = rigctldPath,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    }
                };
                _bundledProcess.Start();

                // Fixed short wait for rigctld to bind its listening socket before the first
                // connect attempt, rather than a retry loop -- matches the app's existing
                // "give the window extra time to come up" style (run_replay_tests.bat).
                Thread.Sleep(500);
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"Failed to launch bundled rigctld: {ex.Message}";
                return false;
            }
        }

        // Terminates the bundled rigctld process this client started. Never touches an
        // externally-run rigctld instance -- only ever the child process this client itself
        // launched, same "only clean up what I started" discipline as run_replay_tests.bat's
        // Jimmy.exe teardown.
        public void StopBundled()
        {
            try
            {
                if (_bundledProcess != null && !_bundledProcess.HasExited)
                    _bundledProcess.Kill();
            }
            catch
            {
                // Best-effort shutdown -- matches WsjtxProtocolAdapter.Close's tolerant style.
            }
            finally
            {
                _bundledProcess?.Dispose();
                _bundledProcess = null;
            }
        }

        private bool EnsureConnected()
        {
            if (IsConnected) return true;
            try
            {
                _tcp = new TcpClient();
                _tcp.Connect(_host, _port);
                _stream = _tcp.GetStream();
                _reader = new StreamReader(_stream, Encoding.ASCII);
                _writer = new StreamWriter(_stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\n" };
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"Could not connect to rigctld at {_host}:{_port}: {ex.Message}";
                Close();
                return false;
            }
        }

        // rigctld replies to an unsupported/failed command with "RPRT <nonzero>". A successful
        // simple query instead replies with the value itself, so absence of that prefix (and a
        // non-null line) is success.
        private static bool LooksLikeError(string reply) => reply == null || reply.StartsWith("RPRT -");

        private string SendCommand(string cmd)
        {
            if (!EnsureConnected()) return null;
            try
            {
                _writer.WriteLine(cmd);
                return _reader.ReadLine();
            }
            catch (Exception ex)
            {
                LastError = $"rigctld command '{cmd}' failed: {ex.Message}";
                Close();
                return null;
            }
        }

        // One round-trip per field this session, not batched -- rigctld has no command
        // batching. Keep PollIntervalMs conservative (RadioSettings default: 1000ms) so this
        // never competes for latency with the FT8 15-second decode cycle.
        public RadioStatus PollOnce()
        {
            LastError = null;
            var status = new RadioStatus();

            string freqReply = SendCommand("f");
            if (!LooksLikeError(freqReply) && ulong.TryParse(freqReply.Trim(), out ulong hz))
                status.FrequencyHz = hz;

            // get_mode ("m") replies on two lines (mode, then passband) -- read both so the
            // passband line is never misread as the reply to whichever command comes next.
            if (EnsureConnected())
            {
                try
                {
                    _writer.WriteLine("m");
                    string modeReply = _reader.ReadLine();
                    _reader.ReadLine();   // passband -- not currently surfaced, but must be consumed
                    if (!LooksLikeError(modeReply)) status.Mode = modeReply.Trim();
                }
                catch (Exception ex)
                {
                    LastError = $"rigctld command 'm' failed: {ex.Message}";
                    Close();
                }
            }

            string pttReply = SendCommand("t");
            if (!LooksLikeError(pttReply) && int.TryParse(pttReply.Trim(), out int pttVal))
                status.Ptt = pttVal != 0;

            // S-meter, power, SWR: none of these have a standard-WSJT-X-protocol equivalent
            // today (Power/SWR only ever came from the WM8Q-only sub-command 18; there has
            // never been an S-meter anywhere in Jimmy). Degrade silently to null on a rig/
            // backend that doesn't support a given level -- expected, not a bug.
            string sMeterReply = SendCommand("l STRENGTH");
            if (!LooksLikeError(sMeterReply) &&
                double.TryParse(sMeterReply.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double sMeter))
                status.SMeterDb = (int)Math.Round(sMeter);

            string powerReply = SendCommand("l RFPOWER");
            if (!LooksLikeError(powerReply) &&
                double.TryParse(powerReply.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double power))
                status.PowerRaw = power;

            string swrReply = SendCommand("l SWR");
            if (!LooksLikeError(swrReply) &&
                double.TryParse(swrReply.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double swr))
                status.Swr = swr;

            status.Ok = LastError == null;
            status.LastError = LastError;
            return status;
        }

        // Opt-in only (RadioSettings.PttEnabled, default off) -- a bigger behavioral change
        // than read-only telemetry, so it's never used unless explicitly turned on.
        public bool SetPtt(bool on)
        {
            string reply = SendCommand(on ? "T 1" : "T 0");
            return !LooksLikeError(reply);
        }

        // Hamlib analogue of WSJT-X's software RX-gain slider that F11/F12 drives in WsjtxCat
        // mode: adjusts the radio's own AF (audio) gain level up/down by a fixed step. Different
        // mechanism (hardware AF gain via CAT vs. a software multiplier applied before decode)
        // but the same practical effect on received audio level.
        private const double AudioStep = 0.05;

        public bool AdjustAudioLevel(bool up)
        {
            string reply = SendCommand("l AF");
            if (LooksLikeError(reply) ||
                !double.TryParse(reply.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double current))
            {
                LastError = "Could not read current AF level from rigctld.";
                return false;
            }
            double next = Math.Max(0.0, Math.Min(1.0, current + (up ? AudioStep : -AudioStep)));
            string setReply = SendCommand("L AF " + next.ToString(CultureInfo.InvariantCulture));
            return !LooksLikeError(setReply);
        }

        public void Close()
        {
            try { _reader?.Dispose(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _stream?.Dispose(); } catch { }
            try { _tcp?.Close(); } catch { }
            _reader = null;
            _writer = null;
            _stream = null;
            _tcp = null;
        }

        public void Dispose()
        {
            Close();
            StopBundled();
        }
    }
}
