using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace WSJTX_Controller
{
    // Launches/stops Jimmy's own native FT8 engine host (self-sufficiency plan, Phase 4g) -- a
    // long-running child process that captures real audio, decodes it through the real native
    // FT8 decoder (reusing Nexus's own libtempo/ft8/tempo-core crates rather than reimplementing
    // any of it), and reports decodes to Jimmy over the same stock WSJT-X UDP protocol Jimmy
    // already speaks -- WsjtxProtocolAdapter needs no changes to receive it.
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

        // For ProcessAudioSessionVolume.cs -- finding the engine's own OS-level audio session
        // (Options > Decode Engine tab's per-device volume controls, added 2026-08-09) needs its
        // process ID. 0 when not running, matching Running's own null-safety.
        public int ProcessId => Running ? _process.Id : 0;

        // Local-loopback-only port the engine host's own control server (EngineHost/src/main.rs's
        // run_control_server) listens on for the lifetime of a session. Fixed rather than derived
        // from jimmyPort: only one engine session ever runs at a time (Controller's own
        // nativeEngineClient field + Stop()-before-Launch() discipline), so there's nothing to
        // collide with, and a fixed value lets the static ListDevices() below reach it without
        // needing the live session's own settings threaded through. Loopback-only binds don't
        // trigger a Windows Firewall prompt. Internal (not private): WsjtxClient.Direct.cs's
        // DirectEngineClient uses this exact same port for its SNAPSHOT/REPLY/HALT_TX/
        // SET_TX_ENABLED commands -- one control server, one port, shared rather than duplicated.
        internal const int ControlPort = 58239;

        // Locates jimmy-engine-host.exe: first the bundled release location (Resources\EngineHost\,
        // staged at release/publish time -- see Jimmy.csproj), then the dev-build location next
        // to this checkout's own EngineHost\ folder, so this also works against a local
        // `cargo build --release` with no packaging step while developing.
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
                // EngineHost/.cargo/config.toml pins the build target to x86_64-pc-windows-gnu
                // explicitly (native libtempo linking needs the MinGW toolchain, not MSVC's) --
                // cargo therefore always builds under target\<triple>\release\, never plain
                // target\release\, even though that triple matches every dev machine's host.
                string devBuild = Path.Combine(repoRoot, "EngineHost", "target", "x86_64-pc-windows-gnu", "release", "jimmy-engine-host.exe");
                if (File.Exists(devBuild)) return devBuild;
            }
            catch
            {
                // Best-effort only -- fall through to null.
            }
            return null;
        }

        // Standard Maidenhead locator shape check -- 4 or 6 chars, field letters A-R, square
        // digits 0-9, optional subsquare letters A-X. Same bounds as propagation::geo::
        // maidenhead_to_latlon / tempo-app's own maidenhead_center (EngineHost's vendored Nexus
        // source), kept in sync deliberately -- see Launch()'s own comment on why this exists.
        // Case-insensitive (Launch's caller already normalizes to uppercase, but this doesn't
        // assume that).
        internal static bool IsValidGridFormat(string grid)
        {
            if (grid == null || (grid.Length != 4 && grid.Length != 6)) return false;
            if (!IsFieldLetter(grid[0]) || !IsFieldLetter(grid[1])) return false;
            if (!char.IsDigit(grid[2]) || !char.IsDigit(grid[3])) return false;
            if (grid.Length == 6 && (!IsSubsquareLetter(grid[4]) || !IsSubsquareLetter(grid[5]))) return false;
            return true;

            bool IsFieldLetter(char c) { char u = char.ToUpperInvariant(c); return u >= 'A' && u <= 'R'; }
            bool IsSubsquareLetter(char c) { char u = char.ToUpperInvariant(c); return u >= 'A' && u <= 'X'; }
        }

        // 2026-08-19 fresh-install usability fix (release blocker): single source of truth for
        // "is My Call/My Grid usable yet" -- used by BOTH Launch()'s own safety-net guard and
        // Controller.ApplyEngineMode's pre-check (called there BEFORE attempting Launch() at
        // all), so the two can never drift on what counts as "not configured". Returns a plain,
        // user-facing sentence with no "native engine"/implementation jargon (the operator should
        // never need to know this concept exists) -- null means configuration is valid. This is
        // deliberately a normal, expected, first-run condition, not an error: a fresh install
        // (or My Call/My Grid cleared) is not a malfunction.
        internal static string DescribeConfigProblem(string mycall, string mygrid)
        {
            // Wording, 2026-08-19 (release-blocker follow-up, project owner feedback): names the
            // specific Options category ("Decode Engine" -- OptionsDlg.Designer.cs's own
            // _categoryListBox label, where the My Call/My Grid fields actually live) rather than
            // a generic "in Options", so a screen-reader user knows exactly where to go without
            // guessing which of the 17 categories to open. Deliberately NOT a hotkey instruction
            // (e.g. "press Alt+O") -- names the destination, not a specific input method, since
            // Options is also reachable by mouse/other means.
            if (string.IsNullOrWhiteSpace(mycall) || string.IsNullOrWhiteSpace(mygrid))
                return "Set your callsign and grid on the Decode Engine tab in Options to begin operating.";
            // 2026-08-18 fresh-install audit: OptionsDlg's own FormatGridSquare is explicitly
            // cosmetic-only ("not this dialog's job to validate grid syntax" -- normalizes case,
            // returns malformed input unchanged), and the engine side degrades a malformed grid
            // silently rather than refusing to start (propagation::geo::maidenhead_to_latlon /
            // tempo-app's own maidenhead_center both return None for it, never panic -- confirmed
            // by reading both). So a typo'd grid ("ABC", "N", six random characters) previously
            // launched successfully with no indication anything was wrong, silently losing
            // distance/bearing-to-DX and grid-based propagation estimates for the whole session.
            // Real Maidenhead locators are 4 or 6 chars: 2 field letters (A-R), 2 square digits,
            // optionally 2 subsquare letters (A-X) -- same bounds propagation::geo enforces, kept
            // in sync here so Jimmy refuses exactly what the engine would have silently accepted-
            // but-ignored, not a stricter or looser rule.
            if (!IsValidGridFormat(mygrid.Trim()))
                return $"'{mygrid}' isn't a valid grid square (e.g. FN42 or FN42ab) -- fix it on the Decode Engine tab in Options to begin operating.";
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

        // Repeat limit / TX watchdog authority split, 2026-08-24 (independent audit finding):
        // Jimmy's own "Repeat limit" (Controller.cs's timeoutNumUpDown) is now the sole
        // attempt-count-based stop (WsjtxClient.cs's DiscardCall actively sends SET_TX_ENABLED 0
        // once reached -- see its own comment); EngineHost's own directed_max_calls is disabled
        // outright (main.rs's own Settings construction). This is what sizes the ONE remaining
        // backstop -- the wall-clock-only TX watchdog (Settings.tx_watchdog_min) -- so it can
        // never silently override a legitimate Repeat Limit by firing first, while still staying
        // a genuine runaway-TX safety net if Jimmy's own stop command somehow never lands.
        //
        // Both stations transmit on alternating slots -- one real attempt costs TWO periods, not
        // one (confirmed two ways: a live SNAPSHOT capture showing periodSecs:15.0 for FT8, and
        // Nexus's own maintainer notes, ".nexus-src/scripts/create-issues.sh": "8 overs is 16
        // slots ~= 4 minutes on FT8, not 2 -- we transmit every other slot"). FT8's 30s/attempt is
        // used UNCONDITIONALLY, even under FT4 (15s/attempt) -- computing per-mode would need the
        // watchdog re-sent on every FT8<->FT4 switch, which (like a live Repeat-Limit change) has
        // no clean live-update path into an already-running Engine (see Launch's own
        // sessionToken-adjacent comments on tx_watchdog_min being startup-CLI-arg-only). Using the
        // slower basis is always safe in both directions: extra, harmless margin under FT4, exact
        // under FT8, and Jimmy's own live SET_TX_ENABLED 0 -- not this backstop -- is what
        // actually stops things on time either way.
        //
        // marginAttempts=2: one full cycle for detection lag (DiscardCall's own caller only
        // re-checks on the NEXT period boundary after the count is reached -- confirmed live, up
        // to one full attempt-cycle late in the worst case) plus one more for command round-trip
        // and ordinary timing jitter (observed directly: real period-boundary spacing in a live
        // diagnostic log drifted several seconds either side of the nominal 30s).
        //
        // ceil (not round): the field is whole minutes only (Settings.tx_watchdog_min: u32) --
        // rounding down could under-shoot Jimmy's own limit, which a BACKSTOP must never do.
        //
        // Bounds: floor of 2 minutes -- Nexus's own code treats 0 as "watchdog disabled entirely"
        // (`if limit_secs > 0`), which Automatic must never compute even at Repeat Limit's own
        // floor of 1; a hard 0-2 min backstop also isn't a genuine safety margin, just a
        // hair-trigger. Ceiling of 30 minutes is defensive headroom, not a real-world constraint
        // today -- Repeat Limit's own enforced UI range (Controller.cs's minSkipCount..
        // maxSkipCount, 1..20) only ever needs up to 11 minutes at the current ceiling; 30 leaves
        // generous room if that ceiling is ever raised without silently producing something absurd.
        internal const int TxWatchdogSecondsPerAttempt = 30;
        internal const int TxWatchdogMarginAttempts = 2;
        internal const int TxWatchdogMinMinutes = 2;
        internal const int TxWatchdogMaxMinutes = 30;

        internal static int ComputeAutomaticTxWatchdogMinutes(int repeatLimit)
        {
            int seconds = (repeatLimit + TxWatchdogMarginAttempts) * TxWatchdogSecondsPerAttempt;
            int minutes = (seconds + 59) / 60; // ceiling division -- see this method's own comment on why ceil, not round
            return Math.Max(TxWatchdogMinMinutes, Math.Min(TxWatchdogMaxMinutes, minutes));
        }

        // Frequency-override authority split, 2026-08-24: `args` (below) is handed to
        // ProcessStartInfo.Arguments as ONE raw command-line string, parsed by the OS the same
        // way any CommandLineToArgvW consumer does. Every other value quoted in this method
        // (device names, a DX-cluster address) is wrapped naively (`\"..\"`, no escaping of
        // embedded quotes) because none of them realistically contain a literal '"'. The
        // --working-frequencies JSON payload is different in kind, not just in size -- it always
        // contains many literal '"' characters, so that naive wrapping would truncate the
        // argument at the first one. This implements the real Win32 rule instead: each '"' needs
        // an odd number of '\' immediately before it (so it's read as an escaped quote, not an
        // argument terminator), and a run of '\' immediately before the closing quote must be
        // doubled so it isn't misread as escaping that quote.
        internal static string EscapeCommandLineArg(string arg)
        {
            var sb = new StringBuilder();
            sb.Append('"');
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    backslashes = 0;
                    sb.Append('"');
                    continue;
                }
                if (backslashes > 0) { sb.Append('\\', backslashes); backslashes = 0; }
                sb.Append(c);
            }
            if (backslashes > 0) sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        // `onUnexpectedExit`: called (NOT marshalled to any particular thread -- Process.Exited
        // fires on a threadpool thread, so the caller must marshal to the UI thread itself if it
        // touches Windows Forms controls) if the engine host process exits on its own, outside
        // of a Stop() this client itself initiated. Before this existed, a real crash was
        // completely silent -- Jimmy kept showing whatever state it last had (decode, radio
        // status) with no indication the engine backing all of it was simply gone. Optional:
        // null is fine and skips the whole EnableRaisingEvents/Exited wiring.
        // sessionToken: EngineHost ownership / session identity, 2026-08-23 -- the per-launch
        // nonce this exact child process echoes back on every SNAPSHOT (main.rs's own
        // --session-token arg / sessionToken JSON field), so ConnectDirectEngine's caller can
        // refuse to trust a stale/orphan process answering on the fixed control port instead of
        // the one launched here. Optional/null keeps existing callers (and any future one that
        // doesn't care about authentication) working exactly as before -- no arg is passed and
        // the child simply reports an empty token, which ConnectDirectEngine(expectedSessionToken:
        // null) never checks.
        // repeatLimit: Controller.cs's own timeoutNumUpDown.Value at launch time -- feeds
        // ComputeAutomaticTxWatchdogMinutes above. Optional/null (rather than a required param)
        // for the same backward-compatibility reason sessionToken is: any existing/future caller
        // that doesn't pass one gets Nexus's own stock tx_watchdog_min (6) via
        // Args::tx_watchdog_min's own default, not a crash or a nonsensical computed value.
        public bool Launch(string mycall, string mygrid, string audioDevice, int jimmyPort,
                            string outputDevice = null, RadioSettings radio = null,
                            Action<string> debugOutput = null, Action onUnexpectedExit = null,
                            DecodeSettings decode = null, bool pskreporter = false,
                            string dxClusterAddress = null, string sessionToken = null,
                            int? repeatLimit = null,
                            List<WorkingFreqArg> workingFrequencies = null)
        {
            LastError = null;
            try
            {
                string exePath = LocateExe();
                if (exePath == null)
                {
                    // "or switch Decode Engine back to WSJT-X External" used to be offered here as
                    // an escape hatch -- removed (found stale while auditing UDP-transport code,
                    // 2026-08-17): Jimmy Next's Form_Load always calls ApplyEngineMode(), which
                    // always launches the native engine host unconditionally (Controller.cs's own
                    // "Phase 4g: always launches the native engine host" comment) -- there is no
                    // remaining way to run against a separate, real external WSJT-X, so telling an
                    // operator to switch to it would send them looking for a setting that no
                    // longer exists.
                    LastError = "jimmy-engine-host.exe not found. Build EngineHost first (cargo build --release).";
                    return false;
                }
                // 2026-08-19 fresh-install usability fix: mycall/mygrid validity used to be
                // checked here only, inline -- Controller.ApplyEngineMode() now checks the exact
                // same condition (DescribeConfigProblem) BEFORE ever getting this far, so a
                // genuinely unconfigured install never reaches Launch() at all (no wasted
                // background Task/Process lookup, and a calmer, non-"Error"-severity status
                // message). This check stays here too -- as a real safety net, not the primary
                // gate -- since Launch() is public and must never spawn the real engine process
                // against invalid config no matter what calls it or in what order.
                string configProblem = DescribeConfigProblem(mycall, mygrid);
                if (configProblem != null)
                {
                    LastError = configProblem;
                    return false;
                }

                // --jimmy-addr: kept deliberately during the 2026-08-18 UDP-transport cleanup, even
                // though Jimmy itself no longer listens on it -- ConnectDirectEngine's Direct control
                // port replaced it as Jimmy's own transport back on 2026-08-12. This flag still makes
                // Nexus's run_radio broadcast standard WSJT-X UDP (decodes/status) to jimmyPort, which
                // is the same wire format third-party tools (JTAlert, GridTracker) expect if an
                // operator runs one of those alongside Jimmy for their own logging/mapping -- a real,
                // separate use case from Jimmy's own engine transport, confirmed via operator decision
                // (2026-08-18) not to remove. Do not treat this as leftover dead code in a future pass
                // without re-checking that decision first.
                var args = $"--mycall {mycall} --mygrid {mygrid} --jimmy-addr 127.0.0.1:{jimmyPort} --control-port {ControlPort}";
                if (!string.IsNullOrWhiteSpace(sessionToken))
                    args += $" --session-token {sessionToken}";
                if (repeatLimit.HasValue)
                    args += $" --tx-watchdog-min {ComputeAutomaticTxWatchdogMinutes(repeatLimit.Value)}";
                // Frequency-override authority split, 2026-08-24 -- see
                // WsjtxClient.BuildWorkingFrequencyEntries' own comment. Omitted entirely when
                // empty (no band customized), matching every other startup arg's "absent =
                // stock behavior" convention -- and unlike every other arg here, this one is
                // JSON containing many literal '"' characters, so it needs real command-line
                // escaping (EscapeCommandLineArg below), not the naive \"..\" wrapping the rest
                // of this method uses for plain device/address strings.
                if (workingFrequencies != null && workingFrequencies.Count > 0)
                {
                    string wfJson = JsonSerializer.Serialize(workingFrequencies, WsjtxClient.DirectJsonOptions);
                    args += $" --working-frequencies {EscapeCommandLineArg(wfJson)}";
                }
                if (!string.IsNullOrWhiteSpace(audioDevice))
                    args += $" --device \"{audioDevice}\"";
                if (!string.IsNullOrWhiteSpace(outputDevice))
                    args += $" --output-device \"{outputDevice}\"";

                // Options > Decode tab -- independent of Radio.Mode (decoding works the same
                // whether or not CAT is configured), so this is unconditional, unlike the
                // rig-specific block below. Only decode.DecodeDepth also has a live control-port
                // path (SET_DECODE_DEPTH) for mid-session changes; the other four are startup-
                // CLI-arg-only (see DecodeSettings.cs's own comment on why).
                if (decode != null)
                {
                    args += $" --decode-depth {decode.DecodeDepth}";
                    args += $" --decode-flow-hz {decode.DecodeFLowHz}";
                    args += $" --decode-fhigh-hz {decode.DecodeFHighHz}";
                    args += $" --ap-decode {(decode.ApDecode ? "1" : "0")}";
                    args += $" --ap-cq-only {(decode.ApCqOnly ? "1" : "0")}";
                    args += $" --single-decode {(decode.SingleDecode ? "1" : "0")}";
                }

                // Independent of Radio.Mode -- spotting works whether or not CAT is configured,
                // same reasoning as the decode block above. Root-caused live, 2026-08-11: this
                // was never wired at all before, in either transport (see TogglePskReporter's
                // own comment, WsjtxClient.Protocol.cs) -- the engine's own native PSK Reporter
                // spotting ran unconditionally regardless of what this checkbox said.
                if (pskreporter) args += " --pskreporter";

                // DX Spots (Alt+G window): an OPTIONAL additional human DX-cluster telnet node
                // (SSB/phone + human-typed spots). RBN's digital skimmer feed is always on and
                // needs no CLI arg at all (EngineHost/src/live_feeds.rs wires it unconditionally,
                // same "wired automatically" default official Nexus's own desktop app uses) --
                // this arg only adds a second, operator-picked source on top of it, since there
                // is no single universal default for a human cluster node (an independently-run
                // federation of nodes) the way there is for RBN. Startup-CLI-arg-only, same
                // reasoning as the decode block above -- changing it in Options requires the
                // usual engine restart.
                if (!string.IsNullOrWhiteSpace(dxClusterAddress))
                    args += $" --dx-cluster \"{dxClusterAddress.Trim()}\"";

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
                    // rigctld host can't be shared with the engine this way -- the engine's own
                    // CAT/PTT would be receive-only in that specific configuration (2026-08-20:
                    // Jimmy no longer runs its own live RigctldClient session that could reach a
                    // remote host as a fallback; OptionsDlg's "Test connection" button still can,
                    // but only as a one-shot diagnostic, not a runtime path). Not a concern for a
                    // bundled/local rigctld, which is the common case this was tested against.
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
                    if (radio.TxMode == RadioTxMode.Usb) args += " --plain-ssb-data-modes";
                    else if (radio.TxMode == RadioTxMode.None) args += " --dont-set-mode";
                    if (radio.PttDataSource) args += " --ptt-data-source";
                    if (!string.IsNullOrWhiteSpace(radio.PttSerialPort))
                        args += $" --ptt-serial-port {radio.PttSerialPort}";
                    if (radio.SplitMode != RadioSplitMode.None)
                        args += $" --split-mode {radio.SplitMode.ToString().ToLowerInvariant()}";
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
                // Diagnostic-only: a real crash so far has never printed a Rust panic message
                // to stderr before dying (confirmed live, 2026-08-08 -- debug log shows nothing
                // between the last normal decode line and "stopped unexpectedly"), which usually
                // means either a genuine access violation/SEH crash (no Rust panic machinery
                // involved at all) or a panic whose message was lost in the pipe on abrupt exit.
                // RUST_BACKTRACE doesn't fix that by itself, but costs nothing and gives a real
                // backtrace for free the next time a panic *does* get to print.
                _process.StartInfo.EnvironmentVariables["RUST_BACKTRACE"] = "full";
                if (debugOutput != null)
                {
                    _process.OutputDataReceived += (s, e) => { if (e.Data != null) debugOutput($"[NativeEngine] {e.Data}"); };
                    _process.ErrorDataReceived += (s, e) => { if (e.Data != null) debugOutput($"[NativeEngine] {e.Data}"); };
                }
                _stopping = false;
                if (onUnexpectedExit != null)
                {
                    _process.EnableRaisingEvents = true;
                    // thisProcess: captured by value at the moment THIS process was launched,
                    // not read from the _process field inside the handler. Found live,
                    // 2026-08-10, auditing against production: if Stop() then Launch() ever ran
                    // back to back on this same client faster than this handler fired for the
                    // OLD process, reading the _process FIELD inside the handler would report
                    // the NEW process's exit code for the OLD process's exit. Checking identity
                    // against thisProcess (on top of the existing _stopping check) also covers
                    // the same race from the other side: a stale Exited firing after _stopping
                    // has already been reset to false for the new launch no longer gets
                    // misreported as an unexpected crash, since it no longer matches the
                    // now-current _process.
                    Process thisProcess = _process;
                    _process.Exited += (s, e) =>
                    {
                        if (_stopping || !ReferenceEquals(_process, thisProcess)) return;
                        // Diagnostic-only: exit code narrows down "genuine crash" (Rust panic
                        // aborts with 101; a Windows-level SEH exception like access violation
                        // shows as a large/negative code, e.g. 0xC0000005) vs a clean early exit.
                        int exitCode = -1;
                        try { exitCode = thisProcess.ExitCode; } catch { /* best-effort */ }
                        debugOutput?.Invoke($"[NativeEngine] process exited unexpectedly, exit code: {exitCode} (0x{(uint)exitCode:X8})");
                        onUnexpectedExit();
                    };
                }
                _process.Start();
                // Always drain both pipes asynchronously, whether or not debugOutput is given --
                // without this, jimmy-engine-host.exe's own stdout/stderr pipe buffers fill once
                // enough lines accumulate (heartbeats every 10s, decodes, TX_SCHEDULE/TX_CONTROL
                // lines), and its synchronous, explicitly-flushed println! calls (see its own
                // `log!` macro) BLOCK on a full pipe -- silently hanging the entire engine,
                // including the timed PTT_OFF release the TX scheduler depends on.
                //
                // (Corrected 2026-08-17: this comment used to cite "NativeTxPttListener's own
                // watchdog" as the backstop for that scenario; that class was already retired by
                // the time this comment was written -- Self-sufficiency Phase 5 moved PTT
                // in-process into jimmy-engine-host.exe itself, superseding it, and the reference
                // was never updated. The real backstops today, verified by reading the actual
                // code rather than trusting the old comment: (1) Nexus's own TX watchdog/abort
                // inside the radio loop (tempo-audio/src/service.rs) is independent of this
                // process's stdout pipe; (2) if jimmy-engine-host.exe itself dies -- crash, or a
                // hard Kill() from NativeEngineClient.Stop() -- rigctld.exe (its own spawned
                // child) cannot be left orphaned holding the radio: RigctldProc binds it to a
                // Windows Job Object with KILL_ON_JOB_CLOSE (tempo-audio/src/rigctld_proc.rs),
                // an OS-level guarantee that survives even a forceful parent termination; (3) on
                // Jimmy's own side, OnNativeEngineUnexpectedExit (Controller.cs) is what actually
                // notices a hang/crash via Process.Exited and drives the bounded auto-restart
                // (5 attempts per 5-minute window) -- a hung, silently-dead engine host does not
                // go unnoticed because THAT mechanism catches it, not a per-process watchdog.
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

        // Enumerates real audio input device names for Options > Radio's device picker. Returns
        // an empty list, never throws, if the exe is missing or enumeration fails -- the picker
        // degrades to "type a device name" rather than blocking the whole tab from opening.
        // `sessionActive`: pass true whenever a real engine-host session might currently be
        // running (e.g. `ctrl.nativeEngineClient?.Running == true`) -- see ListDevices' own
        // comment on why this must come from known state, not be guessed.
        public static List<string> ListAudioDevices(bool sessionActive) => ListDevices("--list-devices", sessionActive);

        // Stage 4: same idea, output side, for Options > Radio's TX audio-device picker. Launch's
        // own `outputDevice` argument expects one of these names verbatim (or empty = system
        // default).
        public static List<string> ListOutputAudioDevices(bool sessionActive) => ListDevices("--list-output-devices", sessionActive);

        private static List<string> ListDevices(string listArg, bool sessionActive)
        {
            string controlCommand = listArg == "--list-devices" ? "LIST_DEVICES" : "LIST_OUTPUT_DEVICES";

            // A live session means a jimmy-engine-host process already has the sound card open.
            // Spawning a SECOND one to answer a device-list query -- even as a "fallback" after a
            // failed control-port attempt -- recreates the exact crash this control port exists to
            // prevent (Nexus's own AUDIO_HOST_LOCK, tempo-audio/src/device.rs, only serializes
            // concurrent cpal/WASAPI callers WITHIN one process; it has no power over two SEPARATE
            // processes). Confirmed live, 2026-08-08: inferring "no session running" from a control-
            // port connect timeout was wrong during a restart storm -- the control server just
            // hadn't finished (re)starting yet, so the old code fell through to the spawn path
            // anyway and reproduced the identical crash. `sessionActive` is real state from the
            // caller, not a guess, so this can refuse to ever spawn instead of merely trying not
            // to. A few short retries ride out that same "control server not up yet" window
            // WITHOUT falling back to a second process -- worst case, the picker comes back empty
            // for this one open of Options, which is safe.
            if (sessionActive)
            {
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    var viaControlPort = TryListDevicesViaControlPort(controlCommand);
                    if (viaControlPort != null) return viaControlPort;
                    System.Threading.Thread.Sleep(250);
                }
                return new List<string>();
            }

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
                    // ReadToEnd() alone has no timeout -- if the engine host hangs (or a crash-
                    // loop leaves antivirus scanning every fresh launch of it) this blocked
                    // forever with no way out, freezing Options on the UI thread despite this
                    // method's own doc comment promising it never blocks the tab from opening
                    // (found live, 2026-08-08, chasing an Options freeze during a real engine
                    // crash storm). Bound the wait and kill the process if it overruns so the
                    // device pickers always degrade to empty instead of hanging.
                    var outputTask = p.StandardOutput.ReadToEndAsync();
                    if (!outputTask.Wait(5000))
                    {
                        try { p.Kill(); } catch { }
                    }
                    p.WaitForExit(1000);
                    string output = outputTask.Status == TaskStatus.RanToCompletion ? outputTask.Result : string.Empty;
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

        // Connect timeout is short and deliberate: on loopback, "nothing listening" refuses the
        // connection almost instantly, so this only ever waits meaningfully long when a session
        // really is up and about to answer. Read timeout is generous (a fresh device enumeration
        // can briefly queue behind whatever run_radio itself is doing with AUDIO_HOST_LOCK).
        // Returns null (never throws, never returns empty-for-"nothing listening" -- empty is
        // reserved for "connected and it really has no devices") so ListDevices can tell "ask the
        // spawn-a-process fallback instead" apart from "control server answered with zero devices".
        private static List<string> TryListDevicesViaControlPort(string controlCommand)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var connectTask = client.ConnectAsync(IPAddress.Loopback, ControlPort);
                    if (!connectTask.Wait(300) || !client.Connected) return null;

                    using (var stream = client.GetStream())
                    {
                        stream.WriteTimeout = 1000;
                        stream.ReadTimeout = 5000;
                        byte[] cmd = System.Text.Encoding.ASCII.GetBytes(controlCommand + "\n");
                        stream.Write(cmd, 0, cmd.Length);

                        var result = new List<string>();
                        using (var reader = new StreamReader(stream, System.Text.Encoding.ASCII))
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                                result.Add(line);
                        }
                        return result;
                    }
                }
            }
            catch
            {
                // Nothing listening (no session running), or it timed out/dropped mid-response --
                // either way, fall back to the spawn-a-process path rather than failing outright.
                return null;
            }
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
                    // Found live, 2026-08-10, auditing against production: WaitForExit's own
                    // return value (did it actually exit within StopWaitMs, or time out) was
                    // never checked -- silently defeating this method's own documented purpose
                    // above (waiting for the real OS-level exit so a following Launch() doesn't
                    // race the old process for the same serial/rigctld port). If it times out,
                    // that race is still possible and nothing would ever have known.
                    bool exited = _process.WaitForExit(StopWaitMs);
                    if (!exited)
                        LastError = $"jimmy-engine-host did not exit within {StopWaitMs}ms after being killed -- a following Launch() may race it for the same serial port.";
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
