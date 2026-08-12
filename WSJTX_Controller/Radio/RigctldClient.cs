using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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

        // One row of the bundled rigctl.exe's own --list output -- the live-supported rig
        // catalog for whichever Hamlib version Jimmy actually ships, not a separately
        // maintained/hardcoded list that could silently drift out of sync with it.
        public class RigModelInfo
        {
            public int Id;
            public string Mfg;
            public string Model;
            public string Display => $"{Mfg} {Model} ({Id})";
        }

        // Runs the bundled rigctl.exe --list once and parses its fixed-column table (verified
        // directly against a real run: " Rig #  Mfg                    Model  ..." with Rig#
        // in columns 1-8, Mfg in 9-31, Model in 32-55 -- some manufacturer names contain spaces
        // themselves, e.g. "N2ADR James Ahlstrom" and "Vertex Standard", so this cannot be a
        // plain whitespace split). Returns an empty list (never throws, never null) if the
        // bundled exe is missing or the process fails for any reason -- callers must degrade to
        // "type the number directly" rather than crash Options.
        public static System.Collections.Generic.List<RigModelInfo> ListRigModels()
        {
            var result = new System.Collections.Generic.List<RigModelInfo>();
            try
            {
                string dir = Path.GetDirectoryName(LocateBundledExe() ?? "");
                if (string.IsNullOrEmpty(dir)) return result;
                string rigctlPath = Path.Combine(dir, "rigctl.exe");
                if (!File.Exists(rigctlPath)) return result;

                var psi = new ProcessStartInfo
                {
                    FileName = rigctlPath,
                    Arguments = "--list",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var proc = Process.Start(psi))
                {
                    string stdout = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(10_000);

                    foreach (string rawLine in stdout.Split('\n'))
                    {
                        string line = rawLine.TrimEnd('\r');
                        if (line.Length < 32) continue;
                        string idStr = line.Substring(0, 8).Trim();
                        if (!int.TryParse(idStr, out int id)) continue;   // skips the header row too
                        string mfg = line.Substring(8, 23).Trim();
                        string model = line.Substring(31, Math.Min(24, line.Length - 31)).Trim();
                        if (mfg.Length == 0 && model.Length == 0) continue;
                        result.Add(new RigModelInfo { Id = id, Mfg = mfg, Model = model });
                    }
                }
            }
            catch
            {
                // Degrade to an empty list -- Options falls back to free-text entry.
            }
            return result;
        }

        // Launches Jimmy's own bundled rigctld against the configured rig model/COM port,
        // listening on this client's configured port. Returns false (LastError set) rather
        // than throwing if the bundled copy is missing or the rig model isn't configured.
        // `baudRate`: rigctld's own -s/--serial-speed -- empty means let Hamlib use its built-in
        // default for `rigModel`. A mismatch against the rig's own CAT baud rate menu setting is
        // a silent, total CAT communication failure with no error anywhere -- this parameter
        // exists because that gap was live-diagnosed 2026-08-06 (PTT never engaged against a
        // real Kenwood TS-590SG; rigctld itself came up and answered fine on its own TCP port,
        // but every actual CAT command to the radio failed).
        public bool LaunchBundled(string rigModel, string comPort, string baudRate = null)
        {
            LastError = null;
            // Defensive: found live, 2026-08-10, auditing against production -- without this,
            // calling LaunchBundled() a second time before StopBundled()/Dispose() on this same
            // instance silently overwrote _bundledProcess below, orphaning the old rigctld.exe
            // with nothing left able to stop it. Currently only ever avoided in practice by the
            // one known caller (ApplyRadioSettings) disposing the whole client first every time
            // -- guarding here makes it safe regardless of caller discipline, matching
            // StopBundled's own "only ever kills what this client started" contract.
            if (_bundledProcess != null && !_bundledProcess.HasExited)
                StopBundled();
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
                if (!string.IsNullOrWhiteSpace(baudRate)) args += $" -s {baudRate}";
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
                // Drain both pipes asynchronously -- an un-drained pipe fills and blocks
                // rigctld's own output eventually, same class of bug fixed in
                // NativeEngineClient.Launch (see that method's own comment for the full
                // explanation). No debugOutput sink wired up here (yet) -- draining alone is
                // what prevents the hang; nobody needs to read rigctld's own console chatter today.
                _bundledProcess.OutputDataReceived += (s, e) => { };
                _bundledProcess.ErrorDataReceived += (s, e) => { };
                _bundledProcess.BeginOutputReadLine();
                _bundledProcess.BeginErrorReadLine();

                // Fixed short wait for rigctld to bind its listening socket before the first
                // connect attempt, rather than a retry loop -- matches the app's existing
                // "give the window extra time to come up" style (run_replay_tests.bat).
                Thread.Sleep(500);

                // Check the process actually survived the bind attempt -- rigctld exits
                // immediately (with a clear stderr message, already drained above) if the
                // requested port is already taken, e.g. by the native engine host's OWN rigctld
                // on the same configured port. Returning true unconditionally here used to hide
                // exactly that failure: EnsureConnected() would then either fail to connect at
                // all or, worse, connect to whatever else happened to already own the port,
                // neither of which is what the operator asked this call to do.
                if (_bundledProcess.HasExited)
                {
                    LastError = $"rigctld exited immediately after launch (exit code {_bundledProcess.ExitCode}) " +
                                 $"-- port {_port} is likely already in use by another rigctld (e.g. the native " +
                                 "engine's own, if Decode Engine is set to Jimmy Native).";
                    _bundledProcess.Dispose();
                    _bundledProcess = null;
                    return false;
                }
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

        // Bounds EVERY blocking network call this class makes (connect, and -- via
        // TcpClient.ReceiveTimeout/SendTimeout below -- every synchronous stream read/write
        // SendCommand does too). Before this existed, NOTHING in this class had a timeout
        // anywhere: TcpClient.Connect() and StreamReader.ReadLine() can both block
        // indefinitely, and every caller (RadioTestButton_Click, radioPollTimer_Tick) runs
        // synchronously on the UI thread -- an unresponsive/conflicted rigctld (e.g. two
        // instances contending for the same port, confirmed live 2026-08-06: the native
        // engine's own rigctld and the Options > Radio "Test connection" button's separate
        // throwaway rigctld both trying to bind the same configured port) froze the ENTIRE
        // application, with no way to recover except Windows force-closing it.
        private const int NetworkTimeoutMs = 3000;

        private bool EnsureConnected()
        {
            if (IsConnected) return true;
            try
            {
                _tcp = new TcpClient();
                var connectTask = _tcp.ConnectAsync(_host, _port);
                if (!connectTask.Wait(NetworkTimeoutMs))
                {
                    LastError = $"Timed out connecting to rigctld at {_host}:{_port} after {NetworkTimeoutMs}ms " +
                                 "-- is another rigctld already bound to that port?";
                    // Found live, 2026-08-10, auditing against production: giving up on
                    // connectTask.Wait() above does not cancel the underlying ConnectAsync --
                    // it keeps running in the background, and Close() below disposes the same
                    // TcpClient out from under it. If it later completes or faults (e.g. an
                    // ObjectDisposedException from that disposal), nothing ever observes the
                    // result, which is exactly the shape of an unobserved-task-exception leak
                    // under repeated connection failures (e.g. a persistently misconfigured
                    // port). Marking it observed here, regardless of how/when it actually
                    // finishes, closes that gap without needing a full cancellation-token rework.
                    connectTask.ContinueWith(
                        t => { var _ = t.Exception; },
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                    Close();
                    return false;
                }
                _tcp.ReceiveTimeout = NetworkTimeoutMs;
                _tcp.SendTimeout = NetworkTimeoutMs;
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

        // Self-sufficiency plan Phase 5: Band Up/Down retuning under JimmyNative +
        // HamlibRigctld. rigctld is a multi-client daemon, so this rides the SAME connection
        // (and, when JimmyNative, the SAME physical rigctld the native engine host itself
        // launched) used for S-meter/SWR/power polling above -- no separate protocol needed.
        // The engine's own Engine::observe_rig_freq reconciles an externally-changed frequency
        // on its own next poll tick, so commanding it here does not desync the engine's belief
        // about the dial.
        public bool SetFrequency(ulong hz)
        {
            string reply = SendCommand($"F {hz}");
            return !LooksLikeError(reply);
        }

        // Matches WSJT-X's own Radio tab "Split Operation: Rig" choice -- enables/disables the
        // radio's own hardware split (TX on VFO B, RX on VFO A) via Hamlib's set_split_vfo.
        // "None" (the default) calls this with enabled=false, which is a harmless no-op on a
        // rig that was never in split. Requested by the operator, 2026-08-07, for parity with
        // WSJT-X's Radio tab -- WSJT-X's third choice, "Fake It" (software-emulated split with
        // no true rig split at all), has no equivalent here; see RadioSettings.SplitMode's own
        // comment for why.
        public bool SetSplit(bool enabled)
        {
            string reply = SendCommand(enabled ? "S 1 VFOB" : "S 0 VFOA");
            return !LooksLikeError(reply);
        }

        // get_mode ("m") replies on two lines (mode, then passband) -- same shape PollOnce
        // already reads; duplicated here (not shared) because PollOnce populates a whole
        // RadioStatus and this needs just the two raw values for the connect-kick below.
        public bool GetMode(out string mode, out int passband)
        {
            mode = null;
            passband = 0;
            if (!EnsureConnected()) return false;
            try
            {
                _writer.WriteLine("m");
                string modeReply = _reader.ReadLine();
                string passbandReply = _reader.ReadLine();
                if (LooksLikeError(modeReply)) return false;
                mode = modeReply.Trim();
                int.TryParse(passbandReply?.Trim(), out passband);
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"rigctld command 'm' failed: {ex.Message}";
                Close();
                return false;
            }
        }

        // set_mode ("M <mode> <passband>"). Nexus's own retune loop only re-sends a mode
        // command when its OWN belief of the current mode differs from the target -- if that
        // belief was ever seeded as "already correct" (e.g. from a stale prior session) without
        // a real CAT command reaching the physical radio, the rig can be stuck on whatever mode
        // it was last ACTUALLY in, forever, while every read-back keeps agreeing with the belief
        // instead of hardware truth. Used by the connect-kick to force a genuine mode round-trip
        // the same way SetFrequency's own nudge does for frequency.
        public bool SetMode(string mode, int passband)
        {
            string reply = SendCommand($"M {mode} {passband}");
            return !LooksLikeError(reply);
        }

        // Hamlib analogue of WSJT-X's software RX-gain slider that F11/F12 drives in WsjtxCat
        // mode: adjusts the radio's own AF (audio) gain level up/down by a configurable step.
        // Different mechanism (hardware AF gain via CAT vs. a software multiplier applied before
        // decode) but the same practical effect on received audio level. `step` (a 0.0-1.0
        // fraction) comes from the caller -- RadioSettings.AudioStepPercent, the same
        // operator-facing setting the engine mic-gain path (WsjtxClient.BandAudio.cs) uses, so
        // both F11/F12 paths always move by the same amount regardless of which one is active.
        public bool AdjustAudioLevel(bool up, double step, out double newLevel)
        {
            newLevel = 0.0;
            string reply = SendCommand("l AF");
            if (LooksLikeError(reply) ||
                !double.TryParse(reply.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double current))
            {
                LastError = "Could not read current AF level from rigctld.";
                return false;
            }
            double next = Math.Max(0.0, Math.Min(1.0, current + (up ? step : -step)));
            string setReply = SendCommand("L AF " + next.ToString(CultureInfo.InvariantCulture));
            if (LooksLikeError(setReply)) return false;
            newLevel = next;
            return true;
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
