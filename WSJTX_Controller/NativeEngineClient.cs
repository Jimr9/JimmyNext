using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace WSJTX_Controller
{
    // Launches/stops Jimmy's own native FT8 engine host (self-sufficiency plan, Phase 4g) -- a
    // long-running child process that captures real audio, decodes it through the real native
    // FT8 decoder (reusing Nexus's own libtempo/ft8/tempo-core crates rather than reimplementing
    // any of it), and reports decodes to Jimmy over the same stock WSJT-X UDP protocol Jimmy
    // already speaks -- CapabilityNegotiator/WsjtxProtocolAdapter need no changes to receive it.
    // Mirrors RigctldClient's bundled-process lifecycle exactly (LocateBundledExe / LaunchBundled
    // / StopBundled): only ever kills the process THIS client started, never an externally-run
    // instance.
    //
    // RECEIVE ONLY as of Phase 4g: the engine host never asserts PTT and never transmits, and
    // does not yet act on Jimmy's outbound Reply/HaltTx/EnableTx messages -- an operator using
    // JimmyNative today gets real native decodes into Jimmy's own queue, but "Reply" doesn't yet
    // do anything. Wiring transmit is a separate, safety-reviewed step (see
    // EngineHost/src/main.rs's own header comment).
    public class NativeEngineClient : IDisposable
    {
        public string LastError { get; private set; }
        private Process _process;

        public bool Running => _process != null && !_process.HasExited;

        // Locates jimmy-engine-host.exe: first the bundled release location (Resources\EngineHost\,
        // staged at release time the same way Resources\hamlib\ is by fetch-hamlib.ps1 -- no such
        // staging step exists for the engine host yet, since DecodeEngineMode.JimmyNative is still
        // INI-only/unreleased), then the dev-build location next to this checkout's own EngineHost/
        // folder, so this works against a local `cargo build --release` with no packaging step
        // while the feature is still field-testing.
        public static string LocateExe()
        {
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(exeDir)) return null;

            string bundled = Path.Combine(exeDir, "Resources", "EngineHost", "jimmy-engine-host.exe");
            if (File.Exists(bundled)) return bundled;

            try
            {
                // exeDir is WSJTX_Controller\bin\<Config>\<TFM> -- the repo root is 4 levels up.
                string repoRoot = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", ".."));
                string devBuild = Path.Combine(repoRoot, "EngineHost", "target", "release", "jimmy-engine-host.exe");
                if (File.Exists(devBuild)) return devBuild;
            }
            catch
            {
                // Best-effort only -- fall through to null.
            }
            return null;
        }

        // Launches the engine host against the given callsign/grid/audio device (empty device =
        // system default input) reporting to Jimmy's own UDP listener on 127.0.0.1:<jimmyPort>.
        // Returns false (LastError set) rather than throwing if the exe is missing or mycall is
        // unconfigured.
        public bool Launch(string mycall, string mygrid, string audioDevice, int jimmyPort)
        {
            LastError = null;
            try
            {
                string exePath = LocateExe();
                if (exePath == null)
                {
                    LastError = "jimmy-engine-host.exe not found. Build EngineHost first (cargo build --release), " +
                                 "or switch Decode Engine back to WSJT-X External.";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(mycall) || string.IsNullOrWhiteSpace(mygrid))
                {
                    LastError = "My Call / My Grid not configured -- set them before using the native engine.";
                    return false;
                }

                var args = $"--mycall {mycall} --mygrid {mygrid} --jimmy-addr 127.0.0.1:{jimmyPort}";
                if (!string.IsNullOrWhiteSpace(audioDevice))
                    args += $" --device \"{audioDevice}\"";

                _process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    }
                };
                _process.Start();
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"Failed to launch jimmy-engine-host: {ex.Message}";
                return false;
            }
        }

        // Terminates the engine host process this client started. Never touches any other
        // process -- same "only clean up what I started" discipline as RigctldClient.StopBundled.
        public void Stop()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                    _process.Kill();
            }
            catch
            {
                // Best-effort shutdown -- matches RigctldClient.StopBundled's tolerant style.
            }
            finally
            {
                _process?.Dispose();
                _process = null;
            }
        }

        public void Dispose() => Stop();
    }
}
