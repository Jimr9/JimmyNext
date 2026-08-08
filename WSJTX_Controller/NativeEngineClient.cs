using System;
using System.Collections.Generic;
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
    // Self-sufficiency plan Phase 5: the engine host is now a thin wrapper around Nexus's own
    // production tempo_audio::service::run_radio, driven against a real tempo_app::engine::Engine
    // -- real decode, real QSO sequencing, real TX scheduling, AND real CAT/PTT (it builds and
    // owns its own Rig directly, from the --rig-*/--ptt-method args below). There is no private
    // protocol of any kind anymore: run_radio's own built-in WSJT-X UDP server handles standard
    // Reply/HaltTx inbound and Decode/Status/Heartbeat outbound, which is why Launch no longer
    // takes a pttControlPort -- NativeTxPttListener.cs (Stage 3/4's own PTT relay) is retired
    // entirely, superseded by the engine owning PTT in-process. See EngineHost/src/main.rs's own
    // header comment for the full reasoning.
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
        // `outputDevice`: where TX audio actually plays (empty = system default output). Never
        // leave this unconsidered: before this parameter existed, TX audio ALWAYS went to the
        // system default regardless of what the operator expected, since there was nowhere to
        // configure it -- confirmed live during bench testing, 2026-08-06. `radio`: the whole
        // radio-control settings object, passed through as a unit rather than unpacked into a
        // long parameter list -- only consulted (rig model/COM port/baud/PTT method/external
        // rigctld host+port) when `radio.Mode == RadioControlMode.HamlibRigctld`; under
        // WsjtxCat the engine host gets NO rig args at all and runs receive-only for radio
        // (decode/QSO still fully work -- CAT/PTT is a separate concern from decoding, matching
        // Rig's own control/PTT separation). `radio.PttEnabled == false` forces PTT method to
        // Vox regardless of `radio.PttMethod`, so CAT/frequency tracking can stay live even with
        // PTT itself turned off. Returns false (LastError set) rather than throwing if the exe
        // is missing or mycall is unconfigured. `debugOutput`: every line the engine host prints
        // (heartbeats, decodes, run_radio's own diagnostics) is handed to this callback if
        // given. NOT optional in effect even when the caller passes null: the pipes are drained
        // either way (see below) -- only whether anyone's told about the lines is optional.
        // Set right before an intentional Stop()/Dispose() kills _process, and checked by the
        // Exited handler below -- distinguishes "we killed it on purpose" (mode switch, Options
        // save, app shutdown) from a genuine unexpected crash/exit, which onUnexpectedExit
        // should only ever fire for.
        private bool _stopping;

        // `onUnexpectedExit`: called (NOT marshalled to any particular thread -- Process.Exited
        // fires on a threadpool thread, so the caller must marshal to the UI thread itself if it
        // touches Windows Forms controls) if the engine host process exits on its own, outside
        // of a Stop() this client itself initiated. Before this existed, a real crash was
        // completely silent -- Jimmy kept showing whatever state it last had (decode, radio
        // status) with no indication the engine backing all of it was simply gone. Optional:
        // null is fine and skips the whole EnableRaisingEvents/Exited wiring.
        public bool Launch(string mycall, string mygrid, string audioDevice, int jimmyPort,
                            string outputDevice = null, RadioSettings radio = null,
                            Action<string> debugOutput = null, Action onUnexpectedExit = null)
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
                if (!string.IsNullOrWhiteSpace(outputDevice))
                    args += $" --output-device \"{outputDevice}\"";
                if (radio != null && radio.Mode == RadioControlMode.HamlibRigctld)
                {
                    // "network" here means "the RADIO is a network SDR" (Flex/SmartSDR via
                    // rigctld's own -r host:port) -- an axis Jimmy has no setting for today
                    // (only a serial COM port), NOT "rigctld itself runs elsewhere". That second
                    // idea -- radio.UseExternalRigctld -- doesn't need its own flag here: the
                    // engine's own open_cat() ALREADY auto-detects and shares any rigctld
                    // already listening on --rigctld-port (127.0.0.1 only) instead of spawning a
                    // second one, so pointing it at the SAME port Jimmy's own bundled/external
                    // rigctld already uses is sufficient either way. Known gap: this auto-share
                    // only checks loopback, so a genuinely remote (non-127.0.0.1) external
                    // rigctld host can't be shared with the engine this way -- Jimmy's own
                    // RigctldClient (S-meter/SWR/power/retune) can still reach a remote host
                    // directly; only the engine's own CAT/PTT would be receive-only in that
                    // specific configuration. Not a concern for a bundled/local rigctld, which is
                    // the common case this was tested against.
                    args += " --rig-conn serial";
                    if (!string.IsNullOrWhiteSpace(radio.RigModel))
                        args += $" --rig-model {radio.RigModel}";
                    if (!radio.UseExternalRigctld && !string.IsNullOrWhiteSpace(radio.ComPort))
                        args += $" --rig-port {radio.ComPort}";
                    if (!radio.UseExternalRigctld && !string.IsNullOrWhiteSpace(radio.BaudRate))
                        args += $" --rig-baud {radio.BaudRate}";
                    PttMethod effectiveMethod = radio.PttEnabled ? radio.PttMethod : PttMethod.Vox;
                    args += $" --ptt-method {effectiveMethod.ToCliString()}";
                    args += $" --rigctld-port {radio.RigctldPort}";
                    if (radio.DataModesPlainSsb) args += " --plain-ssb-data-modes";
                }

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
                if (debugOutput != null)
                {
                    _process.OutputDataReceived += (s, e) => { if (e.Data != null) debugOutput($"[NativeEngine] {e.Data}"); };
                    _process.ErrorDataReceived += (s, e) => { if (e.Data != null) debugOutput($"[NativeEngine] {e.Data}"); };
                }
                _stopping = false;
                if (onUnexpectedExit != null)
                {
                    _process.EnableRaisingEvents = true;
                    _process.Exited += (s, e) => { if (!_stopping) onUnexpectedExit(); };
                }
                _process.Start();
                // Always drain both pipes asynchronously, whether or not debugOutput is given --
                // without this, jimmy-engine-host.exe's own stdout/stderr pipe buffers fill once
                // enough lines accumulate (heartbeats every 10s, decodes, TX_SCHEDULE/TX_CONTROL
                // lines), and its synchronous, explicitly-flushed println! calls (see its own
                // `log!` macro) BLOCK on a full pipe -- silently hanging the entire engine,
                // including the timed PTT_OFF release Stage 4's transmit scheduling depends on.
                // NativeTxPttListener's own watchdog is still the real backstop if that ever
                // happens, but a hung, silently-dead engine host must never go unnoticed.
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"Failed to launch jimmy-engine-host: {ex.Message}";
                return false;
            }
        }

        // Enumerates real audio input device names via `jimmy-engine-host.exe --list-devices`
        // (tempo_audio::device::available_devices(), the same cpal enumeration Launch's --device
        // argument expects verbatim) for Options > Radio's device picker. Returns an empty list,
        // never throws, if the exe is missing or enumeration fails -- the picker degrades to "type
        // a device name" rather than blocking the whole tab from opening.
        public static List<string> ListAudioDevices() => ListDevices("--list-devices");

        // Stage 4: same idea, output side -- `jimmy-engine-host.exe --list-output-devices`, for
        // Options > Radio's TX audio-device picker. Launch's own `outputDevice` argument expects
        // one of these names verbatim (or empty = system default).
        public static List<string> ListOutputAudioDevices() => ListDevices("--list-output-devices");

        private static List<string> ListDevices(string listArg)
        {
            var result = new List<string>();
            string exePath = LocateExe();
            if (exePath == null) return result;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = listArg,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        result.Add(line);
                }
            }
            catch
            {
                // Best-effort only -- empty list on any failure.
            }
            return result;
        }

        // How long to wait for the killed process (and, via its own Job Object, its child
        // rigctld) to actually release its COM port and rigctld TCP port before Stop() returns.
        private const int StopWaitMs = 3000;

        // Terminates the engine host process this client started. Never touches any other
        // process -- same "only clean up what I started" discipline as RigctldClient.StopBundled.
        // Waits for real OS-level exit (bounded) rather than firing Kill() and returning
        // immediately: ApplyEngineMode() disposes the old client and Launches a new one back to
        // back on the SAME COM port and rigctld port whenever radio settings are re-saved while
        // staying in JimmyNative mode. Kill() only requests termination -- without waiting, the
        // old process (and the child rigctld it owns) can still be holding the exclusive serial
        // port when the new one tries to open it, so the new engine's CAT link silently fails
        // and it falls back to no real control, with nothing surfacing the failure. Found live,
        // 2026-08-06: TX audio stayed on the radio's front mic even after CAT-mode tests passed
        // and settings looked correct, which a lost race here fully explains.
        public void Stop()
        {
            _stopping = true;
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit(StopWaitMs);
                }
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
