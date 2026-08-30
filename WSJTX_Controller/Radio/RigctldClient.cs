using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WSJTX_Controller
{
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
                    // Independent audit finding, 2026-08-30: ReadToEnd() blocks until stdout hits
                    // EOF, so the WaitForExit(10_000) that used to follow it bounded NOTHING -- a
                    // rigctl.exe that hangs (or whose unread stderr pipe fills and back-pressures
                    // its stdout writes) froze this call, and with it the Options Radio tab and
                    // the screen reader, until Jimmy was killed. Bound the read and kill the
                    // process on overrun so the rig-model list always degrades to free-text
                    // entry instead. Same shape as NativeEngineClient.ListDevices' own fix.
                    string stdout = ReadStdoutBounded(proc, 10_000);

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

        // Reads a short-lived child process's stdout with a real upper bound. ReadToEndAsync()
        // still has no timeout of its own, so it is raced against timeoutMs; on overrun the
        // process is killed (which lets the read complete) and whatever was captured so far --
        // usually nothing -- is returned. stderr is left redirected but undrained, matching
        // NativeEngineClient.ListDevices: rigctl --list writes its table to stdout and is
        // effectively silent on stderr, and the kill-on-overrun path clears any pipe-fill
        // deadlock. internal (not private): RigctldClientBoundedReadTests drives it directly
        // with a deliberately-hanging stand-in process, which the real rigctl.exe can't be.
        internal static string ReadStdoutBounded(Process proc, int timeoutMs)
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            if (!stdoutTask.Wait(timeoutMs))
            {
                try { proc.Kill(); } catch { }
            }
            proc.WaitForExit(1_000);
            return stdoutTask.Status == TaskStatus.RanToCompletion ? stdoutTask.Result : "";
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
            // with nothing left able to stop it. Guarding here makes it safe regardless of
            // caller discipline, matching StopBundled's own "only ever kills what this client
            // started" contract.
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
                // Firewall-rule audit, 2026-08-21: bind LOOPBACK ONLY, explicitly -- Hamlib's own
                // rigctld defaults to the WILDCARD listen address (all interfaces) when -T isn't
                // given, exposing raw rig control to the LAN. This is the exact bug Nexus's own
                // vendored rigctld_proc.rs (EngineHost/.nexus-src/crates/tempo-audio/src/
                // rigctld_proc.rs, "#53") already fixed for the rigctld EngineHost itself spawns --
                // this call site (Jimmy's own fallback launch, used by the Radio Test button when
                // nothing is already answering) was the one remaining place that still relied on
                // that unsafe default. Found while confirming, for the firewall-exception review,
                // that every rigctld Jimmy can ever spawn is genuinely loopback-only.
                args += $" -T 127.0.0.1 -t {_port}";

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
        // indefinitely, and its caller (OptionsDlg.cs's RadioTestButton_Click) runs
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

        // Polls with a plain frequency query ("f", get_freq) every 100ms until rigctld answers
        // or timeoutMs elapses. Deliberately never sends "l RFPOWER" (or any other level query)
        // here -- this exists specifically to confirm a rigctld is up and accepting commands
        // BEFORE anything else touches it, and RFPOWER itself is the one query that can trip
        // Hamlib bug #1595's destructive calibration sweep on a Kenwood rig's first touch of a
        // freshly-spawned process (see OptionsDlg.cs's RadioTestButton_Click, the one remaining
        // caller -- Jimmy's own separate mitigation for the engine host's own RFPOWER telemetry
        // poll lives in Nexus itself now, nexus-compat's tempo-audio-telemetry.patch). SendCommand/
        // EnsureConnected already self-heal a failed connect or a broken stream between attempts,
        // so no explicit Close() is needed in this loop.
        public bool WaitUntilReady(int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                string reply = SendCommand("f");
                if (!LooksLikeError(reply) && ulong.TryParse((reply ?? "").Trim(), out _))
                    return true;
                Thread.Sleep(100);
            } while (DateTime.UtcNow < deadline);
            return false;
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
