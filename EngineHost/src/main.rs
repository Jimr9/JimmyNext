//! jimmy-engine-host: Jimmy's own native FT8/FT4 engine for DecodeEngineMode.JimmyNative.
//!
//! Self-sufficiency plan Phase 5: this process no longer hand-rolls its own decode/TX-
//! scheduling/PTT/radio-control loop. That loop -- wall-clock-UTC-steered slot timing, an async
//! decode-worker thread, real TX scheduling, real PTT/CAT via a `Rig` it builds and owns
//! internally, and the full standard WSJT-X UDP protocol (both directions) -- already exists,
//! tested, as Nexus's own real production radio loop:
//! `tempo_audio::service::run_radio`, driven against a real `tempo_app::engine::Engine`. It is
//! the exact function Nexus's own desktop app (`src-tauri`) spawns on a thread to run a real
//! station. This file's entire job is to build an `Engine` + `RadioConfig` from Jimmy's own CLI
//! args and call `run_radio` -- nothing else.
//!
//! This replaces an earlier, hand-rolled implementation (see git history: "TX wiring Stage
//! 1-4", `audio.rs`/`tx_control.rs`/`tx_schedule.rs`, now deleted) that was re-deriving, by
//! hand, most of what `run_radio` already does -- discovered during a Phase 5 planning session
//! after the operator asked whether jimmy-engine-host was reusing Nexus the right way. It
//! wasn't: `run_radio`'s `RadioConfig` already has every field that hand-rolled version needed
//! (audio devices, rig model/COM port/baud/PTT method, `wsjtx_udp`/`wsjtx_addr` for the exact
//! protocol bridge Jimmy already speaks), `Engine::call_station_ctx` is the exact WSJT-X
//! double-click-to-reply entry point (using `tempo_core::qso::Station` directly -- the same type
//! the old hand-rolled scheduler used), and `Engine::halt_tx()`/`set_tx_enabled(false)` already
//! implement WSJT-X's own Halt-Tx `auto_only` distinction. Retuning (Band Up/Down) and S-meter/
//! SWR/power telemetry are NOT handled here at all -- Jimmy's own `Radio/RigctldClient.cs` talks
//! directly to the same `rigctld` daemon this process launches (a multi-client TCP daemon), so
//! no private protocol is needed for those either.
//!
//! Launched by Jimmy's `NativeEngineClient.cs` (spawned on demand, killed on Jimmy shutdown) when
//! `EngineModeCutover.Mode == DecodeEngineMode.JimmyNative`.

// Native-crash reporter (Windows) -- ported verbatim from Nexus's own src-tauri/src/main.rs
// (its own header comment there explains the full history: the JT65 Call-CQ crash in 0.19.16
// took a multi-day hunt an ordinary stack trace would have ended immediately, because an
// ACCESS VIOLATION from the vendored Fortran DSP layer isn't a Rust panic -- catch_unwind can't
// contain it, and Windows Error Reporting only records a fault offset into a stripped binary,
// naming no module and no call path). jimmy-engine-host has been hitting exactly that: a
// 100%-reproducible ACCESS_VIOLATION at a fixed ntdll.dll offset, several fixes in, still
// unexplained by comparing source against Nexus's own usage -- this module exists in Nexus's
// own src-tauri binary, NOT in any shared crate jimmy-engine-host already links against, so it
// was never installed here. Copied wholesale rather than re-derived: self-contained by design
// (no allocation, inline Win32 declarations, no new dependencies -- see its own comment below),
// so it ports as-is. Writes `nexus-crash.txt` next to this exe (falling back to %TEMP%) naming
// the faulting module+offset and a full stack trace, the first time this process ever needed
// that and didn't have it.
#[cfg(windows)]
mod crashlog {
    use std::ffi::c_void;
    use std::ptr;
    use std::sync::atomic::{AtomicBool, Ordering};

    const EXCEPTION_ACCESS_VIOLATION: u32 = 0xC000_0005;
    // STATUS_HEAP_CORRUPTION: Windows' own heap manager detected corrupted metadata and is
    // fail-fasting. Added 2026-08-08 after finding this process also crashes with this code,
    // same ntdll offset as the very first crash reports collected. NOTE: heap-corruption
    // fail-fast typically routes through __fastfail, which is explicitly designed to bypass
    // SEH/VEH (the process state is no longer trusted) -- this filter is included on the
    // chance the code path taken here isn't the pure-bypass one, not a guarantee it fires.
    const STATUS_HEAP_CORRUPTION: u32 = 0xC000_0374;
    const CONTINUE_SEARCH: i32 = 0;
    const FROM_ADDRESS_UNCHANGED: u32 = 0x0000_0004 | 0x0000_0002;
    const GENERIC_WRITE: u32 = 0x4000_0000;
    const CREATE_ALWAYS: u32 = 2;
    const MAX_FRAMES: usize = 48;
    const OUT_CAP: usize = 8192;

    #[repr(C)]
    struct ExceptionRecord {
        code: u32,
        flags: u32,
        next: *mut ExceptionRecord,
        address: *mut c_void,
        n_params: u32,
        params: [usize; 15],
    }

    #[repr(C)]
    struct ExceptionPointers {
        record: *mut ExceptionRecord,
        context: *mut c_void,
    }

    extern "system" {
        fn AddVectoredExceptionHandler(
            first: u32,
            handler: unsafe extern "system" fn(*mut ExceptionPointers) -> i32,
        ) -> *mut c_void;
        fn RtlCaptureStackBackTrace(
            skip: u32,
            capture: u32,
            frames: *mut *mut c_void,
            hash: *mut u32,
        ) -> u16;
        fn GetModuleHandleExW(flags: u32, addr: *const c_void, module: *mut *mut c_void) -> i32;
        fn GetModuleFileNameW(module: *mut c_void, buf: *mut u16, size: u32) -> u32;
        fn CreateFileW(
            name: *const u16,
            access: u32,
            share: u32,
            sa: *mut c_void,
            disposition: u32,
            flags: u32,
            template: *mut c_void,
        ) -> *mut c_void;
        fn WriteFile(
            file: *mut c_void,
            buf: *const u8,
            len: u32,
            written: *mut u32,
            overlapped: *mut c_void,
        ) -> i32;
        fn CloseHandle(handle: *mut c_void) -> i32;
        fn GetCurrentThreadId() -> u32;
        fn GetTempPathW(len: u32, buf: *mut u16) -> u32;
    }

    /// The report, built in place. Static so the handler needs almost no stack.
    static mut OUT: [u8; OUT_CAP] = [0; OUT_CAP];
    static mut OUT_LEN: usize = 0;
    static mut FRAMES: [*mut c_void; MAX_FRAMES] = [ptr::null_mut(); MAX_FRAMES];
    static FIRED: AtomicBool = AtomicBool::new(false);

    /// Append raw bytes to the report, truncating rather than overflowing.
    unsafe fn put(bytes: &[u8]) {
        let out = ptr::addr_of_mut!(OUT) as *mut u8;
        let len = ptr::addr_of_mut!(OUT_LEN);
        for &b in bytes {
            if *len >= OUT_CAP {
                return;
            }
            *out.add(*len) = b;
            *len += 1;
        }
    }

    /// Append `value` as fixed-width `0x…` hex -- no allocation, no core::fmt.
    unsafe fn put_hex(value: usize, digits: usize) {
        const HEX: &[u8; 16] = b"0123456789abcdef";
        put(b"0x");
        let mut i = digits;
        while i > 0 {
            i -= 1;
            put(&[HEX[(value >> (i * 4)) & 0xF]]);
        }
    }

    /// Append a decimal number.
    unsafe fn put_dec(mut value: u32) {
        let mut digits = [0u8; 10];
        let mut n = 0;
        loop {
            digits[n] = b'0' + (value % 10) as u8;
            value /= 10;
            n += 1;
            if value == 0 {
                break;
            }
        }
        while n > 0 {
            n -= 1;
            put(&[digits[n]]);
        }
    }

    /// Resolve `addr` to its owning module and append `name+0xoffset`.
    ///
    /// The module is the point of the whole exercise: it says whether the fault
    /// is in jimmy-engine-host itself, in the vendored Fortran's runtime (libgfortran), in
    /// cpal/WASAPI, or in Windows.
    unsafe fn put_module_relative(addr: *mut c_void) {
        let mut module: *mut c_void = ptr::null_mut();
        if GetModuleHandleExW(FROM_ADDRESS_UNCHANGED, addr, &mut module) == 0 || module.is_null() {
            put(b"<unknown module> ");
            put_hex(addr as usize, 16);
            return;
        }
        let mut path = [0u16; 260];
        let n = GetModuleFileNameW(module, path.as_mut_ptr(), 260) as usize;
        // Basename only: everything after the last backslash.
        let mut start = 0;
        for i in 0..n {
            if path[i] == b'\\' as u16 {
                start = i + 1;
            }
        }
        for &wide in path.iter().take(n).skip(start) {
            // Module file names are ASCII in practice; the low byte is the char.
            put(&[(wide & 0xFF) as u8]);
        }
        put(b"+");
        put_hex(addr as usize - module as usize, 8);
    }

    /// Append `nexus-crash.txt\0` at `cut` and try to write the report there.
    /// Returns false if the file could not be created.
    unsafe fn try_write(path: &mut [u16; 260], cut: usize) -> bool {
        for (i, ch) in b"nexus-crash.txt\0".iter().enumerate() {
            if cut + i >= 260 {
                return false;
            }
            path[cut + i] = *ch as u16;
        }
        let file = CreateFileW(
            path.as_ptr(),
            GENERIC_WRITE,
            0,
            ptr::null_mut(),
            CREATE_ALWAYS,
            0,
            ptr::null_mut(),
        );
        if file as isize == -1 {
            return false;
        }
        let mut written = 0u32;
        WriteFile(
            file,
            ptr::addr_of!(OUT) as *const u8,
            OUT_LEN as u32,
            &mut written,
            ptr::null_mut(),
        );
        CloseHandle(file);
        true
    }

    /// Write the report beside the executable, falling back to `%TEMP%`.
    unsafe fn flush() {
        let mut path = [0u16; 260];
        let n = GetModuleFileNameW(ptr::null_mut(), path.as_mut_ptr(), 260) as usize;
        if n > 0 {
            let mut cut = 0;
            for i in 0..n {
                if path[i] == b'\\' as u16 {
                    cut = i + 1;
                }
            }
            if try_write(&mut path, cut) {
                return;
            }
        }
        // GetTempPathW includes the trailing backslash, so its length IS the cut.
        let mut temp = [0u16; 260];
        let n = GetTempPathW(260, temp.as_mut_ptr()) as usize;
        if n > 0 && n < 240 {
            try_write(&mut temp, n);
        }
    }

    unsafe extern "system" fn handler(info: *mut ExceptionPointers) -> i32 {
        if info.is_null() {
            return CONTINUE_SEARCH;
        }
        let record = (*info).record;
        if record.is_null()
            || ((*record).code != EXCEPTION_ACCESS_VIOLATION
                && (*record).code != STATUS_HEAP_CORRUPTION)
        {
            return CONTINUE_SEARCH;
        }
        // Report the FIRST fault only. Later ones are usually the unwinder (or the heap
        // manager itself, for the corruption case) tripping over the same broken state.
        if FIRED.swap(true, Ordering::SeqCst) {
            return CONTINUE_SEARCH;
        }

        put(b"jimmy-engine-host native-crash report (ported from Nexus's own crashlog)\r\n");
        put(b"exception : ");
        if (*record).code == STATUS_HEAP_CORRUPTION {
            put(b"HEAP_CORRUPTION (0xc0000374)\r\n");
        } else {
            put(b"ACCESS_VIOLATION (0xc0000005)\r\n");
        }
        put(b"faulting  : ");
        put_module_relative((*record).address);
        put(b"\r\nthread    : ");
        put_dec(GetCurrentThreadId());

        // ExceptionInformation distinguishes a null deref from a wild pointer
        // from a guard-page hit (stack overflow) -- different bugs entirely.
        if (*record).n_params >= 2 {
            put(b"\r\noperation : ");
            put(match (*record).params[0] {
                0 => b"READ from  ".as_slice(),
                1 => b"WRITE to   ".as_slice(),
                8 => b"EXECUTE at ".as_slice(),
                _ => b"?? at      ".as_slice(),
            });
            put_hex((*record).params[1], 16);
        }

        put(b"\r\n\r\nstack (innermost first):\r\n");
        let frames = ptr::addr_of_mut!(FRAMES) as *mut *mut c_void;
        let n = RtlCaptureStackBackTrace(0, MAX_FRAMES as u32, frames, ptr::null_mut()) as usize;
        for i in 0..n {
            let frame = *frames.add(i);
            if frame.is_null() {
                continue;
            }
            put(b"  ");
            put_module_relative(frame);
            put(b"\r\n");
        }
        if n == 0 {
            put(b"  <no frames captured>\r\n");
        }

        flush();
        CONTINUE_SEARCH
    }

    /// Install the handler. Call once, as early as possible.
    pub fn install() {
        unsafe {
            AddVectoredExceptionHandler(1, handler);
        }
    }
}

use std::sync::{Arc, Mutex};

mod contest_bridge;
mod decode_semantics;
mod external_data;
mod live_feeds;
mod logbook_host;

use tempo_app::engine::Engine;
use tempo_app::settings::Settings;
use tempo_audio::service::{run_radio, RadioConfig};

/// `println!`, but flushed immediately -- Jimmy's `NativeEngineClient` reads this process's
/// stdout line-by-line via `OutputDataReceived`, so a buffered line can sit unseen arbitrarily
/// long when stdout isn't a real console (a launched-as-child-process Jimmy, or a bench-test
/// harness capturing output).
macro_rules! log {
    ($($arg:tt)*) => {{
        println!($($arg)*);
        let _ = std::io::Write::flush(&mut std::io::stdout());
    }};
}

struct Args {
    mycall: String,
    mygrid: String,
    device: Option<String>,
    output_device: Option<String>,
    jimmy_addr: String,
    dial_freq: u64,
    /// Hamlib rig model number for rigctld `-m`. `None`/0 = no CAT, PTT method forced to vox.
    rig_model: u32,
    /// "serial" (default) or "network" -- matches `RadioConfig.rig_conn` exactly, passed through
    /// verbatim rather than translated, so there is only one vocabulary to keep in sync.
    rig_conn: String,
    /// COM port for a serial rig (e.g. "COM4"). Ignored for `rig_conn == "network"`.
    rig_port: String,
    /// `host:port` for a network rig (e.g. a Flex's SmartSDR). Ignored for serial.
    rig_addr: String,
    rig_baud: u32,
    /// "cat" | "vox" | "rts" | "dtr" -- matches `RadioConfig.ptt_method`'s own expected strings
    /// verbatim.
    ptt_method: String,
    /// Local TCP port this process runs its own bundled rigctld on (and connects to). Jimmy's
    /// own `RigctldClient` connects here too, read-only, for S-meter/SWR/power/frequency --
    /// rigctld is a multi-client daemon, so this is a second independent connection, not a
    /// conflict (same reasoning `NativeTxPttListener`'s own header comment gave for Stage 3,
    /// now generalized: Jimmy no longer needs a private channel for ANY of this).
    rigctld_port: u16,
    /// When true, sets `Settings.data_modes_plain_ssb` -- Digital operating mode normally
    /// unconditionally commands the rig's DATA submode (PKTUSB/PKTLSB) over CAT for every FT8/
    /// FT4 transmission; this maps that down to plain USB/LSB instead. Nexus itself calls this
    /// "wiring-dependent, and wrong for most rigs" (it's meant for a mic-jack-wired interface,
    /// where plain SSB is what actually routes TX audio correctly) -- exposed here anyway as an
    /// operator-facing experiment, not a recommendation. NOTE: a real TS-590SG transmitting mic
    /// audio instead of the FT8 tone *despite* CAT read-back correctly reporting PKTUSB
    /// (confirmed live, 2026-08-07) turned out to be a SEPARATE issue from this one -- CAT mode
    /// was already right; the rig's PTT command itself didn't say which audio input to key from.
    /// See `ptt_data_source` below for that. Jimmy's own Options > Radio tab is the accessible
    /// equivalent of WSJT-X's Radio tab "Mode" dropdown this maps to.
    plain_ssb_data_modes: bool,
    /// When true, sets `Settings.ptt_data_source` -- WSJT-X-equivalent "Transmit Audio Source:
    /// Data" (see that Settings field's own doc comment, and `rig::ptt_line` in Nexus). Off by
    /// default, matching WSJT-X's own default; only relevant for a rig whose Hamlib backend
    /// distinguishes mic/data PTT AND whose interface is wired to the rig's rear DATA/ACC port.
    /// Jimmy's own Options > Radio tab is the accessible equivalent of WSJT-X's Radio tab
    /// "Transmit Audio Source" Mic/Data radio buttons.
    ptt_data_source: bool,
    /// Upload heard stations to PSK Reporter -- sets both Settings.pskreporter (live, every
    /// tick) and RadioConfig.pskreporter (startup) so the two never briefly disagree the way
    /// they did before this was wired through (RadioConfig defaulted off, Settings defaulted
    /// on, so native spotting always silently flipped on within the first tick regardless of
    /// what the operator asked for). Jimmy's own Options checkbox is the accessible equivalent
    /// of WSJT-X's own "Enable PSK Reporter Spotting" checkbox (Reports tab).
    pskreporter: bool,
    /// WSJT-X Radio tab "Mode: None" -- sets Settings.dont_set_mode. Off by default, matching
    /// WSJT-X's own default of Data/Pkt. When on, the radio loop never sends a CAT mode
    /// command at all, for any operating mode -- the operator's own manual rig setting stands.
    dont_set_mode: bool,
    /// SO2R: separate serial port for RTS/DTR PTT when it differs from the CAT port -- sets
    /// Settings.ptt_serial_port. Empty (default) = key on the same port as CAT.
    ptt_serial_port: String,
    /// WSJT-X Radio tab "Split Operation" -- sets Settings.split_mode. "none" (default) |
    /// "rig" (true hardware split via CAT) | "fakeit" (software-emulated: retune before TX,
    /// restore after -- no real rig split needed).
    split_mode: String,
    /// Local-loopback-only TCP port this process's control server listens on for the lifetime of
    /// the session (see `run_control_server` below). NativeEngineClient.cs asks THIS already-
    /// running process for its device list instead of spawning a second, competing
    /// jimmy-engine-host process -- root-caused live, 2026-08-08: Nexus's own `AUDIO_HOST_LOCK`
    /// (tempo-audio/src/device.rs) only serializes concurrent cpal/WASAPI callers WITHIN one
    /// process; two SEPARATE jimmy-engine-host processes (a live session plus a throwaway
    /// `--list-devices` invocation) touching the sound card at once get none of that protection
    /// and fault natively. Keeping every device query inside the one already-running process
    /// makes that race structurally impossible instead of papering over it.
    control_port: u16,
    /// WSJT-X "Fast/Normal/Deep" decoder depth (1/2/3) -- Settings.decode_depth. Startup value
    /// only; SET_DECODE_DEPTH (control port, below) is the live path used after that, since
    /// Engine::set_decode_depth is safe to call mid-session (the decoder reads it fresh on
    /// every slot). Defaults to Nexus's own Settings::default() (3 = Deep) when not passed.
    decode_depth: Option<u8>,
    /// WSJT-X "F Low" -- decoder passband low edge in Hz. Settings.decode_flow_hz. Startup-only:
    /// Engine has no live setter for this (its `settings` field is private to the tempo-app
    /// crate), so unlike decode_depth this can only be set once, at Engine::with_settings(...)
    /// construction -- an Options change here needs the usual engine restart to take effect,
    /// same as rig-model/data-modes-plain-ssb/etc. already do.
    decode_flow_hz: Option<u32>,
    /// WSJT-X "F High" -- decoder passband high edge in Hz. Settings.decode_fhigh_hz.
    /// Startup-only, same reasoning as decode_flow_hz above.
    decode_fhigh_hz: Option<u32>,
    /// WSJT-X "Enable AP" (Decode menu) -- a-priori decoding, FT8 only. Settings.ap_decode.
    /// Startup-only, same reasoning as decode_flow_hz above.
    ap_decode: Option<bool>,
    /// WSJT-X-adjacent expert toggle: restrict AP to the CQ hypothesis only.
    /// Settings.ap_cq_only. Startup-only, same reasoning as decode_flow_hz above.
    ap_cq_only: Option<bool>,
    /// "Single decode": narrows the FT8/FT4 search to the RX offset +/-25 Hz.
    /// Settings.single_decode -- a genuine Nexus improvement over stock WSJT-X, whose own
    /// "Single decode" checkbox is inert for FT8/FT4 (verified 3.0.2 -- only JT65/Q65/FST4 read
    /// it). Startup-only, same reasoning as decode_flow_hz above.
    single_decode: Option<bool>,
    /// `host:port` of an operator-chosen DX-cluster/RBN telnet node (tempo_net::cluster). `None`
    /// (default) disables the DX Spots feed entirely -- unlike PSK Reporter's one public broker,
    /// DX clusters are an independently-run federation with no single correct default, so this
    /// must be the operator's own choice. Startup-only, same reasoning as decode_flow_hz above --
    /// changing it in Options requires the usual engine restart.
    dx_cluster_addr: Option<String>,
    /// Independent audit finding, 2026-08-23 (EngineHost ownership / session identity, HIGH
    /// PRIORITY): a per-launch random value NativeEngineClient.cs generates fresh before every
    /// Launch() and never reuses. Echoed back verbatim as the `sessionToken` field on every
    /// SNAPSHOT response (see `handle_control_connection`'s own SNAPSHOT branch) so Jimmy can
    /// prove the control-port connection actually reaches the specific child process it just
    /// launched, not a stale/orphan jimmy-engine-host.exe left over from a prior session that
    /// still happens to hold port 58239 (the fail-closed bind fix closes a NEW second process
    /// silently coexisting with an old one, but does nothing about an old one Jimmy itself never
    /// launched THIS session). Empty when not passed (e.g. a manual/debug launch with no
    /// --session-token flag) -- Jimmy's own expected token is always a real generated value, so
    /// an empty/missing echoed token can never accidentally satisfy its match check.
    session_token: String,
    /// Repeat limit / TX watchdog authority split, 2026-08-24: Jimmy computes this from its own
    /// "Repeat limit" setting (Controller.cs's timeoutNumUpDown) plus real FT8/FT4 timing and a
    /// safety margin -- see NativeEngineClient.ComputeAutomaticTxWatchdogMinutes's own comment
    /// for the exact formula. Passed straight through to Settings.tx_watchdog_min (main() below)
    /// as the wall-clock-only runaway-TX safety backstop, now fully independent of
    /// directed_max_calls (which main() disables outright -- see that assignment's own comment).
    /// Startup-only, same reasoning as decode_flow_hz above: Engine::apply_settings exists but is
    /// a whole-settings-form-save mechanism with extensive live-state-preservation logic, not a
    /// safe vehicle for a single-field live update, so a Repeat-limit change takes effect on the
    /// next engine restart (OptionsDlg.cs restarts automatically when it actually changed).
    /// Defaults to Nexus's own Settings::default() (6) when not passed, matching every other
    /// startup-only field's own "absent = stock behavior" convention.
    tx_watchdog_min: u32,
    /// Alt+T tune carrier auto-release, seconds (Jimmy's ini-only tuneTimeoutSeconds) ->
    /// Settings.tune_timeout_secs. Nexus clamps it to its own 60 s ceiling at the point of use.
    /// Absent = Nexus's stock 12 s.
    tune_timeout_secs: Option<u32>,
    /// Nexus's internet time check (Settings.clock_check), from Jimmy's Options > General
    /// "Check clock with internet time servers". Absent = Nexus's stock value (on).
    clock_check: Option<bool>,
    /// Frequency-override authority split, 2026-08-24 (independent audit finding): mirrors
    /// Jimmy's own Options>Frequencies per-band/per-mode overrides (FrequencySettings.cs) into
    /// Nexus's own documented working-frequency override mechanism (Engine::band_plan's own doc
    /// comment: "WSJT-X Settings > Frequencies... an override replaces the dial of the matching
    /// (band, mode) row"). Without this, Engine::set_tier's own internal auto-QSY (engine.rs,
    /// ~line 8122 -- "switching the mode moves the rig to the NEW mode's dial for the CURRENT
    /// band") always retunes to Nexus's stock band-plan dial on every tier switch, independent
    /// of whatever dial Jimmy itself just restored/commanded. Harmless on a band the operator
    /// has never customized -- Jimmy's own built-in defaults (WsjtxClient.cs's freqsDict)
    /// already match Nexus's stock table exactly on every band checked -- but an unnecessary
    /// extra retune on any band the operator HAS customized. Passed as a JSON array
    /// (`[{"band":"30m","mode":"FT4","mhz":10.14}, ...]`, matching WorkingFreq's own
    /// #[serde(rename_all = "camelCase")]) at startup; SET_WORKING_FREQUENCIES (control port,
    /// below) is the live-update counterpart for an Options>Frequencies edit made mid-session --
    /// unlike tx_watchdog_min, this is a normal operator-facing settings field (Nexus's own
    /// SettingsPanel edits it the same way), so Engine::apply_settings is its intended vehicle,
    /// not an inappropriate one. Empty (default) = Nexus's own stock table, matching every other
    /// startup field's "absent = stock behavior" convention.
    working_frequencies: Vec<tempo_app::settings::WorkingFreq>,
    /// Logbook migration Phase 1 (`logbook_host.rs`): the folder of a Nexus-owned logbook this
    /// process opens and answers LOG_* for. `None` (not passed) = no Nexus logbook at all --
    /// Jimmy's own LogbookDb stays the only logbook, exactly as before. Phase 1 is exercised
    /// only with temporary test folders.
    log_dir: Option<std::path::PathBuf>,
    /// `--no-radio`: a logbook-only start -- the control server and the logbook, and NO radio
    /// loop (no audio device, no CAT, no PTT). For the migration/comparison tools and for Jimmy
    /// with no radio configured (plan section 6.1). Only meaningful with --log-dir.
    no_radio: bool,
}

/// Set by SHUTDOWN before it stops the radio loop. `run_radio` returns once it has unkeyed, and
/// main() would then return and end the process while SHUTDOWN is still saving the logbook; with
/// this set, main() waits instead, and SHUTDOWN ends the process itself when the save is done.
static SHUTTING_DOWN: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

/// Whether this process runs a radio loop at all (false under --no-radio): SHUTDOWN waits for a
/// loop to unkey only when there is one.
static RADIO_RUNNING: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

/// SHUTDOWN's own last resort: main() waiting for a shutdown that never finishes (a stuck save)
/// still ends the process after this, rather than hanging forever.
const SHUTDOWN_BACKSTOP: std::time::Duration = std::time::Duration::from_secs(150);

/// Nexus contesting foundation, phase 3: where the contest bridge's own small sidecar files
/// (session-instance identity, restored across a restart) live. Self-contained default -- no
/// coordinated Jimmy-side CLI-arg change needed for this phase -- under LOCALAPPDATA, the same
/// root Jimmy's own C# side already uses for its per-install data (LookupManager.DataRoot),
/// just EngineHost's own subtree so the two processes never write the same file.
fn contest_data_dir() -> std::path::PathBuf {
    let base = std::env::var("LOCALAPPDATA")
        .map(std::path::PathBuf::from)
        .unwrap_or_else(|_| std::env::temp_dir());
    let dir = base.join("jimmy-engine-host").join("contest");
    let _ = std::fs::create_dir_all(&dir);
    dir
}

fn parse_args() -> Args {
    let mut mycall = "NOCALL".to_string();
    let mut mygrid = "AA00".to_string();
    let mut device = None;
    let mut output_device = None;
    let mut jimmy_addr = "127.0.0.1:2237".to_string();
    let mut dial_freq: u64 = 14_074_000;
    let mut rig_model: u32 = 0;
    let mut rig_conn = "serial".to_string();
    let mut rig_port = String::new();
    let mut rig_addr = String::new();
    let mut rig_baud: u32 = 38_400;
    let mut ptt_method = "vox".to_string();
    let mut rigctld_port: u16 = 4532;
    let mut plain_ssb_data_modes = false;
    let mut ptt_data_source = false;
    let mut pskreporter = false;
    let mut dont_set_mode = false;
    let mut ptt_serial_port = String::new();
    let mut split_mode = "none".to_string();
    let mut control_port: u16 = 58239;
    let mut decode_depth: Option<u8> = None;
    let mut decode_flow_hz: Option<u32> = None;
    let mut decode_fhigh_hz: Option<u32> = None;
    let mut ap_decode: Option<bool> = None;
    let mut ap_cq_only: Option<bool> = None;
    let mut single_decode: Option<bool> = None;
    let mut dx_cluster_addr: Option<String> = None;
    let mut session_token = String::new();
    let mut log_dir: Option<std::path::PathBuf> = None;
    let mut no_radio = false;
    let mut tune_timeout_secs: Option<u32> = None;
    let mut clock_check: Option<bool> = None;
    let mut tx_watchdog_min: u32 = 6; // Nexus's own Settings::default() -- see Args::tx_watchdog_min's own comment
    let mut working_frequencies: Vec<tempo_app::settings::WorkingFreq> = Vec::new(); // empty = Nexus's own stock table

    let mut it = std::env::args().skip(1);
    while let Some(flag) = it.next() {
        match flag.as_str() {
            "--mycall" => mycall = it.next().unwrap_or(mycall),
            "--mygrid" => mygrid = it.next().unwrap_or(mygrid),
            "--device" => device = it.next(),
            "--output-device" => output_device = it.next(),
            "--jimmy-addr" => jimmy_addr = it.next().unwrap_or(jimmy_addr),
            "--dial-freq" => {
                if let Some(v) = it.next() {
                    dial_freq = v.parse().unwrap_or(dial_freq);
                }
            }
            "--rig-model" => {
                if let Some(v) = it.next() {
                    rig_model = v.parse().unwrap_or(rig_model);
                }
            }
            "--rig-conn" => rig_conn = it.next().unwrap_or(rig_conn),
            "--rig-port" => rig_port = it.next().unwrap_or(rig_port),
            "--rig-addr" => rig_addr = it.next().unwrap_or(rig_addr),
            "--rig-baud" => {
                if let Some(v) = it.next() {
                    rig_baud = v.parse().unwrap_or(rig_baud);
                }
            }
            "--ptt-method" => ptt_method = it.next().unwrap_or(ptt_method),
            "--rigctld-port" => {
                if let Some(v) = it.next() {
                    rigctld_port = v.parse().unwrap_or(rigctld_port);
                }
            }
            "--plain-ssb-data-modes" => plain_ssb_data_modes = true,
            "--ptt-data-source" => ptt_data_source = true,
            "--pskreporter" => pskreporter = true,
            "--dont-set-mode" => dont_set_mode = true,
            "--ptt-serial-port" => ptt_serial_port = it.next().unwrap_or(ptt_serial_port),
            "--split-mode" => split_mode = it.next().unwrap_or(split_mode),
            "--control-port" => {
                if let Some(v) = it.next() {
                    control_port = v.parse().unwrap_or(control_port);
                }
            }
            "--decode-depth" => {
                if let Some(v) = it.next() {
                    decode_depth = v.parse::<u8>().ok().map(|d| d.clamp(1, 3));
                }
            }
            "--decode-flow-hz" => {
                if let Some(v) = it.next() {
                    decode_flow_hz = v.parse().ok();
                }
            }
            "--decode-fhigh-hz" => {
                if let Some(v) = it.next() {
                    decode_fhigh_hz = v.parse().ok();
                }
            }
            "--ap-decode" => {
                if let Some(v) = it.next() {
                    ap_decode = Some(v.trim() == "1");
                }
            }
            "--ap-cq-only" => {
                if let Some(v) = it.next() {
                    ap_cq_only = Some(v.trim() == "1");
                }
            }
            "--single-decode" => {
                if let Some(v) = it.next() {
                    single_decode = Some(v.trim() == "1");
                }
            }
            "--dx-cluster" => {
                dx_cluster_addr = it.next().filter(|v| !v.trim().is_empty());
            }
            "--session-token" => session_token = it.next().unwrap_or(session_token),
            "--no-radio" => no_radio = true,
            "--log-dir" => {
                log_dir = it.next().filter(|v| !v.trim().is_empty()).map(std::path::PathBuf::from);
            }
            "--tx-watchdog-min" => {
                if let Some(v) = it.next() {
                    tx_watchdog_min = v.parse().unwrap_or(tx_watchdog_min);
                }
            }
            "--clock-check" => {
                clock_check = it.next().map(|v| v.eq_ignore_ascii_case("on"));
            }
            "--tune-timeout-secs" => {
                tune_timeout_secs = it.next().and_then(|v| v.parse().ok()).filter(|v: &u32| *v >= 1);
            }
            "--working-frequencies" => {
                if let Some(v) = it.next() {
                    match serde_json::from_str::<Vec<tempo_app::settings::WorkingFreq>>(&v) {
                        Ok(wf) => working_frequencies = wf,
                        Err(e) => eprintln!(
                            "jimmy-engine-host: ignoring unparseable --working-frequencies value: {e}"
                        ),
                    }
                }
            }
            other => eprintln!("jimmy-engine-host: ignoring unrecognized argument '{other}'"),
        }
    }
    Args {
        mycall,
        mygrid,
        device,
        output_device,
        jimmy_addr,
        dial_freq,
        rig_model,
        rig_conn,
        rig_port,
        rig_addr,
        rig_baud,
        ptt_method,
        rigctld_port,
        plain_ssb_data_modes,
        ptt_data_source,
        pskreporter,
        dont_set_mode,
        ptt_serial_port,
        split_mode,
        control_port,
        decode_depth,
        decode_flow_hz,
        decode_fhigh_hz,
        ap_decode,
        ap_cq_only,
        single_decode,
        dx_cluster_addr,
        session_token,
        tx_watchdog_min,
        tune_timeout_secs,
        clock_check,
        working_frequencies,
        log_dir,
        no_radio,
    }
}

/// Serves this already-running process's device list AND (Self-sufficiency plan Phase 6) a
/// direct, UDP-free control channel to NativeEngineClient.cs / DirectEngineClient.cs over a
/// local-loopback-only TCP control port, for the lifetime of the session -- see
/// `Args::control_port`'s own doc comment for why the device-listing half exists (making the
/// two-processes-touching-the-sound-card-at-once crash structurally impossible instead of just
/// less likely). Runs on its own thread, never on the one `run_radio` blocks;
/// `available_devices()` still goes through Nexus's own `AUDIO_HOST_LOCK`, so a query landing
/// mid-session safely queues behind whatever `run_radio` itself is doing with the audio host
/// rather than racing it.
///
/// One-line-command text protocol, deliberately minimal, one connection per request (matching
/// the existing LIST_DEVICES shape rather than inventing a second style). The accept loop is
/// single-threaded and serial by design (commands run in the order their connections arrive,
/// HALT_TX included) -- see `read_one_control_line`'s own doc comment for the bounded read
/// timeout/length that keeps one stalled or incomplete connection from blocking every command
/// behind it:
///   LIST_DEVICES / LIST_OUTPUT_DEVICES  -- unchanged: one device name per line, then EOF.
///   SNAPSHOT                            -- one line: engine.snapshot() as JSON (AppSnapshot,
///                                          already Serialize for Nexus's own Tauri IPC -- same
///                                          data Nexus's own UI renders from, reused verbatim),
///                                          then EOF. THE reason this whole channel exists: it
///                                          replaces the WSJT-X UDP heartbeat/negotiation
///                                          handshake for DirectEngineClient.cs entirely --
///                                          connect and ask, no timing-sensitive dance to race
///                                          against a slow CAT/audio open on a slower machine
///                                          (root-caused live, 2026-08-08: that handshake, not
///                                          raw UDP delivery, was the actual fragility).
///   REPLY <json>                        -- {"dxcall":"...", "dxgrid":"...", "replyMsg":"...",
///                                          "replySnr":-10, "dxFreqHz":1500.0} (grid/msg/snr/freq
///                                          all optional/nullable) -- calls Engine's own
///                                          call_station_ctx, the exact WSJT-X double-click-to-
///                                          reply entry point. Responds "OK" or "ERR <message>".
///   HALT_TX                             -- calls Engine::halt_tx(). Responds "OK".
///   SET_TX_ENABLED <0|1>                -- calls Engine::set_tx_enabled(bool). Responds "OK".
///   SET_PSKREPORTER <0|1>                -- calls Engine::set_pskreporter(bool). Responds "OK".
///   SET_MIC_GAIN <0.0-1.0>               -- calls Engine::set_mic_gain(f32). Responds "OK" or
///                                          "ERR <message>". Radio-side (CAT) mic gain, applied
///                                          only for manual Phone/PTT operation (tempo-audio/
///                                          src/service.rs: "Only the Phone section drives
///                                          these (the FT8 TX path is idle there)") -- NOT used
///                                          by Jimmy's F11/F12 (see SET_TX_LEVEL below, added
///                                          2026-08-10 after finding this command has no effect
///                                          on FT8/FT4 transmit audio at all, despite the
///                                          original 2026-08-09 comment's belief that it did).
///   SET_TX_LEVEL <0.0-1.0>               -- calls Engine::set_tx_level(f32). Responds "OK" or
///                                          "ERR <message>". Jimmy's own F11/F12 hotkeys (audio
///                                          level up/down) -- the real modern equivalent of Andy
///                                          WM8Q's fork's original proprietary "Set Audio Level"
///                                          UDP sub-command (NewTxMsgIdx=20), which adjusted
///                                          WSJT-X's own generated Tx tone level (the "Pwr"
///                                          slider) -- sound-card/software side of the signal
///                                          chain, not the radio's own physical MIC/DATA gain.
///                                          set_tx_level already existed in Engine and was
///                                          already wired to the radio loop every slot
///                                          (tempo-app/src/engine.rs's own doc comment: "takes
///                                          effect live"); this just exposes it, matching every
///                                          other command above -- no Nexus source touched.
///   SET_TIER FT8|FT4                    -- calls Engine::set_tier(Tier). Responds
///                                          "OK <TIER> <PERIOD_SECS>" (e.g. "OK FT4 7.5") or
///                                          "ERR <message>". Jimmy's own Alt+M (Toggle Mode)
///                                          hotkey -- under classic WSJT-X/UDP mode this always
///                                          just halted Tx and left the operator to switch modes
///                                          in WSJT-X's own UI (no outbound mode-change command
///                                          exists in WSJT-X's UDP API at all); direct-engine mode
///                                          has no separate UI to fall back to, so it needs this
///                                          instead. set_tier already existed in Engine; this just
///                                          exposes it.
///                                          Response shape fixed 2026-09-15 (tier-confirmation
///                                          redesign): set_tier() is synchronous and infallible --
///                                          by the time it returns, engine.tier()/
///                                          active_slot_secs() are already the authoritative
///                                          resulting values, still under the SAME lock this
///                                          handler already holds. Nexus's own Tauri `set_tier`
///                                          command (src-tauri/src/lib.rs) does exactly this --
///                                          `eng.set_tier(tier); Ok(eng.snapshot())` -- returning
///                                          the fresh state instead of a bare acknowledgement.
///                                          This mirrors that, so Jimmy can trust the response
///                                          immediately instead of polling SNAPSHOT afterward to
///                                          find out what it was already told.
///   SET_DECODE_DEPTH 1|2|3               -- calls Engine::set_decode_depth(u8) (WSJT-X's
///                                          "Fast/Normal/Deep"). Responds "OK" or "ERR <message>".
///                                          Live, mid-session -- unlike the rest of Jimmy's Decode
///                                          tab (F Low/F High/Enable AP/AP-CQ-only/Single decode),
///                                          which have no live setter on Engine and are configured
///                                          via CLI args at startup only (--decode-flow-hz etc.,
///                                          see Args' own doc comments).
///   SET_SPECIAL_OP none|hound            -- Jimmy's Hound toggle (traditional FT8 DXpedition
///                                          Hound) through Nexus's own settings save -- see
///                                          set_special_op. Responds "OK <resulting state>" or
///                                          "ERR <message>" (Hound is refused outside FT8, and
///                                          SET_TIER to a non-FT8 tier turns it off). SNAPSHOT
///                                          carries the engine's actual state as `specialOp`.
///   SET_TUNING <0|1>                     -- calls Engine::set_tune(bool). Responds "OK". Jimmy's
///                                          own Tune hotkey -- Andy WM8Q's fork's "ToggleTuning"
///                                          UDP sub-command (NewTxMsgIdx=19) had no native-engine
///                                          equivalent exposed before this; set_tune already
///                                          existed in Engine (used by Nexus's own Tauri UI) and
///                                          already plays a continuous test carrier in small
///                                          chunks (tempo-audio/src/service.rs, TUNE_CHUNK_MS =
///                                          40ms) rather than one pre-rendered slot buffer -- this
///                                          just exposes it, matching every other command above.
///                                          Added 2026-08-10 specifically so F11/F12 (SET_TX_LEVEL)
///                                          can apply live during Tune for ALC calibration, since
///                                          a normal FT8/FT4 slot's audio is rendered in one shot
///                                          at slot start and can't be live-adjusted mid-over (see
///                                          SET_TX_LEVEL's own comment) -- Tune's chunked
///                                          generation has no such limitation.
///   SET_TX_OFFSET <hz>                   -- calls Engine::set_tx_offset(f32). Responds "OK" or
///                                          "ERR <message>". Jimmy's "Use best Tx frequency"
///                                          restoration (2026-08-18): Jimmy analyzes recent decodes
///                                          to find the widest quiet gap in the passband, then sends
///                                          that Hz value here right before calling CQ or replying,
///                                          so its own transmission lands in the gap instead of on
///                                          top of another station. set_tx_offset already existed in
///                                          Engine (clamped 200-4000 Hz, "read by the next poll_tx")
///                                          and is unrelated to REPLY's dx_freq_hz field just above
///                                          -- that one moves RX (and TX, unless Hold Tx Freq is on)
///                                          onto a SPECIFIC DX station's own decoded frequency
///                                          (WSJT-X's classic double-click-to-work behavior);
///                                          this one sets Jimmy's own outbound audio tone
///                                          independent of any particular station. No Nexus source
///                                          touched -- this just exposes an existing Engine method,
///                                          matching every other command above.
///   SET_RX_OFFSET <hz>                   -- calls Engine::set_rx_offset(f32). Responds "OK" or
///                                          "ERR <message>". Companion to SET_TX_OFFSET for Jimmy
///                                          Next's accessible Rx/Tx frequency controls: Jimmy is
///                                          the sole authority on both audio offsets (REPLY's
///                                          dx_freq_hz is always null), so "follow the station on
///                                          receive" -- every Hold/Best-mode reply and every
///                                          caller-answers-our-CQ -- is one SET_RX_OFFSET with the
///                                          worked decode's audio Hz. set_rx_offset already exists
///                                          in Engine (clamped 200-4000 Hz, read by the next
///                                          poll); no Nexus source touched.
/// Formats SET_TIER's wire response from the ALREADY-RESULTING tier/period (read by the caller,
/// under the same engine lock, immediately after `Engine::set_tier` returns) -- pulled out as a
/// small pure function for direct test coverage, same reasoning as `reply_wire_response` below.
/// "OK <TIER> <PERIOD_SECS>", e.g. "OK FT4 7.5". `tier` is serialized through `Tier`'s own
/// `Serialize` impl (the SAME wire name `AppSnapshot.link.tier` already uses, e.g. "FT8"/"FT4")
/// rather than a hand-maintained duplicate mapping, so this can never drift from what a SNAPSHOT
/// response calls the same tier. Tier-confirmation redesign, 2026-09-15: no bare "OK" anymore --
/// see this command's own doc comment above for why the caller can trust this immediately instead
/// of polling SNAPSHOT afterward.
/// Jimmy's Hound toggle (traditional FT8 DXpedition Hound), 2026-09-26. Goes through Nexus's
/// OWN supported lifecycle -- the same settings save its Operate-header Hound button makes
/// (`apply_settings` with only `special_op` changed) -- so every Hound rule stays Nexus's:
/// the >1000 Hz first call, moving TX onto the Fox, the quiet finish on the Fox's RR73, and the
/// in-QSO Fox-frame reattach. Nexus drops Hound at every launch (`with_settings`), so a
/// restarted engine comes back with it off. `superhound` is not offered: Nexus has no SuperFox
/// decoder and treats it as plain Hound (settings.rs `SpecialOp::SuperHound`).
///
/// FT8 ONLY. WSJT-X's DXpedition mode is FT8-only and Nexus declares `fox_hound` on FT8 alone,
/// but its quiet-finish / Hound-offset rules read the setting whatever the tier -- so the tier
/// check lives here, and [`set_tier_reconciling_special_op`] turns Hound off on leaving FT8.
fn set_special_op(eng: &mut Engine, arg: &str) -> Result<&'static str, String> {
    use tempo_app::settings::SpecialOp;
    let want = match arg.trim() {
        "none" => SpecialOp::None,
        "hound" => SpecialOp::Hound,
        other => return Err(format!("bad SET_SPECIAL_OP value: {other} (expected none|hound)")),
    };
    if want == SpecialOp::Hound && eng.tier() != tempo_app::dto::Tier::Ft8 {
        return Err("Hound is FT8 only".to_string());
    }
    if eng.settings().special_op != want {
        let mut s = eng.settings().clone();
        s.special_op = want;
        eng.apply_settings(s);
    }
    Ok(special_op_wire(eng))
}

/// The engine's ACTUAL special-operation state as Jimmy sees it: `"hound"` (incl. the retired
/// `superhound` alias, which Nexus runs as Hound) or `"none"`.
fn special_op_wire(eng: &Engine) -> &'static str {
    match eng.settings().special_op {
        tempo_app::settings::SpecialOp::None => "none",
        _ => "hound",
    }
}

/// `Engine::set_tier`, then Hound off if the new tier is not FT8 (see [`set_special_op`]).
fn set_tier_reconciling_special_op(eng: &mut Engine, tier: tempo_app::dto::Tier) {
    eng.set_tier(tier);
    if eng.tier() != tempo_app::dto::Tier::Ft8 && special_op_wire(eng) != "none" {
        let _ = set_special_op(eng, "none");
    }
}

fn set_tier_wire_response(tier: tempo_app::dto::Tier, period_secs: f64) -> String {
    let tier_name = match serde_json::to_value(tier) {
        Ok(serde_json::Value::String(s)) => s,
        // Tier's Serialize impl always produces a bare string for every variant -- this arm is
        // unreachable in practice, but a malformed response is far better than a panic here.
        _ => format!("{tier:?}"),
    };
    format!("OK {tier_name} {period_secs}")
}

/// Formats the REPLY command's wire response from `Engine::call_station_ctx`'s own `Result` --
/// pulled out as a small pure function so this has direct test coverage without needing a live
/// Engine/TCP round trip. `Ok(())` -> "OK"; `Err(e)` -> "ERR {e}", matching every other fallible
/// command in `run_control_server`'s dispatch.
fn reply_wire_response(result: Result<(), String>) -> String {
    match result {
        Ok(()) => "OK".to_string(),
        Err(e) => format!("ERR {e}"),
    }
}

/// Parses the single Hz argument shared by `SET_TX_OFFSET` / `SET_RX_OFFSET`. `Ok(hz)` is
/// passed straight to `Engine::set_tx_offset` / `set_rx_offset` (which do the 200-4000 Hz
/// passband clamp themselves); `Err(s)` is the ready-to-write `ERR ...` wire line. Pulled out
/// as a pure function for the same reason as `reply_wire_response` above -- direct test
/// coverage of the parse/error contract without a live Engine or TCP round trip. `name` is the
/// command keyword, echoed into the error so a malformed line is self-identifying in the log.
fn parse_offset_arg(name: &str, v: &str) -> Result<f32, String> {
    match v.trim().parse::<f32>() {
        Ok(hz) if hz.is_finite() => Ok(hz),
        Ok(_) => Err(format!("ERR bad {name} value: not finite")),
        Err(e) => Err(format!("ERR bad {name} value: {e}")),
    }
}

/// Recognizes a CALL_CQ control-port line (the accept loop's own `line.trim()` has already run
/// by the time this sees it) and extracts its optional directed-CQ token. `None` means `line`
/// isn't a CALL_CQ command at all (falls through to "ERR unknown command"); `Some(None)` is a
/// plain CQ; `Some(Some(token))` is a directed one.
///
/// Release-audit finding (Codex Audit 02, 2026-08-21) -- confirmed real, release blocker: the
/// handler this feeds used to be `line.strip_prefix("CALL_CQ ")` directly (a literal trailing
/// space required to match at all). Plain CQ's own C# sender (WsjtxClient.Direct.cs's
/// DirectSendCq) sends exactly "CALL_CQ " for an empty/no directed token -- but the accept
/// loop's own `line.trim()`, applied before ANY command match, strips that trailing space
/// first, turning "CALL_CQ " into bare "CALL_CQ" before the old handler ever saw it.
/// strip_prefix("CALL_CQ ") could then never match a plain CQ at all -- it silently fell
/// through to "ERR unknown command" every time, while Jimmy's own C# side had already updated
/// its local "calling CQ" state and enabled TX. Only a DIRECTED token (e.g. "CALL_CQ DX",
/// which still has a real non-whitespace character after the space and so survives
/// line.trim() intact) could ever match -- exactly the "configuration-dependent and deceptive"
/// shape the audit described. Extracted into its own pure, synchronously-testable function
/// (matching `validate_set_frequency`'s own reason for existing separately, just below) so
/// this parsing has real unit coverage for the plain-CQ case specifically, not just the
/// directed one the original code happened to already exercise correctly.
fn parse_call_cq_line(line: &str) -> Option<Option<&str>> {
    if line != "CALL_CQ" && !line.starts_with("CALL_CQ ") {
        return None;
    }
    let dir = line.strip_prefix("CALL_CQ").unwrap_or("").trim();
    Some(if dir.is_empty() { None } else { Some(dir) })
}

/// Release-audit finding, 2026-08-20: the SET_FREQUENCY handler used to only check that
/// hz/band/mode were each individually well-formed (finite/positive/non-empty), never that
/// they actually agreed WITH EACH OTHER -- a malformed internal caller could request e.g.
/// `{hz: 7100000, band: "20m"}` and this would happily answer OK and command the radio there,
/// even though Jimmy's own display/persistence (RetuneBand's caller, RadioSettings.
/// LastBandIdx/LastDialFrequencyHz) believed it was on 20m. Extracted into its own pure,
/// synchronously-testable function (matching `reply_wire_response`'s own reason for existing
/// as a separate function above) so this validation has real unit coverage without needing to
/// spin up `run_control_server`'s actual TCP listener. Cross-checks the claimed band against
/// `tempo_app::bandplan::band_for_dial` -- the SAME canonical band-plan table Nexus's own
/// `tune_dial` already canonicalizes every dial through (`bandplan::canonical_band`), so this
/// reuses Nexus's existing source of truth rather than adding a second, possibly-drifting band
/// table in EngineHost.
fn validate_set_frequency(a: &SetFrequencyArgs) -> Result<(), String> {
    if !a.hz.is_finite() || a.hz <= 0.0 {
        return Err(format!("hz must be finite and positive, got {}", a.hz));
    }
    if a.band.trim().is_empty() {
        return Err("band must not be empty".to_string());
    }
    if a.mode.trim().is_empty() {
        return Err("mode must not be empty".to_string());
    }
    match tempo_app::bandplan::band_for_dial(a.hz / 1_000_000.0) {
        Some(actual_band) if actual_band.eq_ignore_ascii_case(a.band.trim()) => Ok(()),
        Some(actual_band) => Err(format!(
            "{} Hz is on {actual_band}, not the requested band {}",
            a.hz, a.band
        )),
        None => Err(format!(
            "{} Hz is not on any recognized amateur band",
            a.hz
        )),
    }
}

/// Bounds how long `read_one_control_line` will wait for an accepted connection's command line
/// to arrive, and how many bytes it will buffer while waiting -- see that function's own doc
/// comment for why. Generous relative to Jimmy's own client-side round-trip budget
/// (RigctldClient.NetworkTimeoutMs = 3000ms; WsjtxClient.Direct.cs's own DirectSendCommand totals
/// ~4000ms of connect+read budget): a well-behaved local client's command line arrives within
/// milliseconds of connecting, so this only ever matters for a stalled, incomplete, or oversized
/// connection.
const CONTROL_READ_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(5);
const MAX_CONTROL_LINE_BYTES: u64 = 8192;

/// Reads exactly one command line from an already-accepted control-port connection, bounded by
/// both a read timeout and a maximum line length. Found live (Codex release audit, 2026-08-19),
/// back when `run_control_server` handled every connection serially, inline, on one thread
/// (fixed since -- Codex Audit 03 finding 5, see `handle_control_connection`'s own doc comment):
/// receiving that FIRST line had no timeout and no length bound at all (`TcpStream::read_line`
/// can block indefinitely), so a client that connected and never sent a newline -- or never sent
/// a complete line -- stalled whichever thread was handling it forever. This bound stays load-
/// bearing regardless of the threading model: a run-away connection today only ever pins its own
/// spawned thread, not every other command, but an unbounded read is still an unbounded read.
/// Returns None on any failure: couldn't set the
/// timeout, couldn't clone the stream, the read itself timed out (a real Err from read_line --
/// distinct from a clean EOF), or a line that never reached a terminating newline within
/// max_bytes. That last case needs an explicit check: `BufRead::read_line` treats hitting EOF
/// without a newline as a *successful* partial read (`Ok(n)`), not an error -- so a
/// `Read::take(max_bytes)`-wrapped stream that runs out of budget mid-line would otherwise hand
/// back a silently truncated fragment instead of signaling "this didn't fit", which is exactly
/// the "buffer without limit" failure mode this exists to close off. Callers already treat
/// "nothing usable was read" as a single case (drop this connection, move on to the next), so
/// none of this changes that handling, only bounds how long/how much it costs to reach it. Takes
/// timeout/max_bytes as parameters (rather than reading the consts directly) so this is directly
/// testable at a much shorter timeout without touching the real server's own budget.
fn read_one_control_line(
    stream: &std::net::TcpStream,
    timeout: std::time::Duration,
    max_bytes: u64,
) -> Option<String> {
    use std::io::{BufRead, BufReader, Read};
    stream.set_read_timeout(Some(timeout)).ok()?;
    let cloned = stream.try_clone().ok()?;
    let mut reader = BufReader::new(cloned.take(max_bytes));
    let mut line = String::new();
    reader.read_line(&mut line).ok()?;
    if line.ends_with('\n') { Some(line) } else { None }
}

/// Codex Audit 03 finding 5, 2026-08-21: `run_control_server` used to process one accepted
/// connection fully, inline, before ever calling `listener.incoming()` again -- a client that
/// connected but stalled sending its first line held `read_one_control_line`'s full
/// `CONTROL_READ_TIMEOUT` (5s) and blocked EVERY other command behind it, HALT_TX included, on
/// the one accept-loop thread. This file already had the right pattern for exactly this shape of
/// problem: EQSL_UPLOAD/EQSL_DOWNLOAD/HAMQTH_LOOKUP/HAMQTH_TEST below each spawn their own
/// thread so the KNOWN-slow ones can't block the accept loop -- this extends that same pattern
/// one level up, to every connection, since the actual risk (a stalled/slow client) can happen
/// before the command is even known, at the read_one_control_line stage itself. Engine
/// mutations stay correctly serialized at Arc<Mutex<Engine>> exactly as they already were (each
/// handler locks briefly, calls one method, unlocks -- never holds the lock for a whole
/// connection), so concurrent connections interleave safely; this only removes the artificial
/// socket-accept-order serialization Codex's finding is about. No connection cap/thread pool
/// added: this is a loopback-only, single-operator, one-shot-per-command local control channel
/// (Jimmy's own client already opens a fresh connection per command, matching the eQSL/HamQTH
/// spawns' own established shape), not a public-facing service that needs DoS hardening.
/// ATU_START / ATU_STOP / ATU_STATUS -- Jimmy's "Start antenna tuner" (Alt+Shift+T), 2026-09-29.
/// Nexus's own ATU command (Engine::atu_tune) goes through Hamlib's `set_func TUNER`, which on the
/// Kenwood backend can only switch the tuner IN, never start a tune-up -- so Nexus refuses it there
/// (rigmodels::hamlib_atu_start_tune_reaches). The TS-590-family CAT command that does start one is
/// `AC111;` (third digit: 1 start / 0 stop; `AC;` reads it back), sent as raw CAT through the SAME
/// rigctld Nexus already runs, with Nexus's own client (Rig::rigctld, send_raw_set / send_raw) --
/// never a second connection to the radio. That client never keys, so dropping it never unkeys.
/// Kenwood backend only for now. The transmit gates are Nexus's (Engine::atu_tune_gate) minus two:
/// the "can't start over CAT" refusal this replaces, and the Enable-TX latch -- Jimmy disarms TX
/// before a tune-up exactly as it does for Tune (Alt+T), and FT8/FT4 are never receive-only tiers.
/// Replies "OK", "OK <1 tuning | 0 not> <radio's AC answer>" for ATU_STATUS, or "ERR <reason>".
fn kenwood_atu(engine: &Arc<Mutex<Engine>>, cmd: &str) -> String {
    const KENWOOD_BACKEND: u32 = 2;
    let port = {
        let e = engine.lock().unwrap_or_else(|e| e.into_inner());
        if e.settings().rig_model / 1000 != KENWOOD_BACKEND {
            return "ERR Starting this radio's tuner from Jimmy works only with Kenwood radios so far".to_string();
        }
        if cmd == "ATU_START" {
            if e.tuning() || e.on_air() {
                return "ERR Still transmitting, stop first".to_string();
            }
            if !e.tx_allowed() {
                return "ERR TX locked, this frequency is outside your license privileges".to_string();
            }
            if let Some(owner) = e.tx_owner() {
                return format!("ERR {}", owner.busy_reason());
            }
        }
        e.settings().rigctld_port
    }; // engine lock released before any network I/O
    let mut rig = tempo_audio::rig::Rig::rigctld(&format!("127.0.0.1:{port}"));
    let Some(reply) = rig.send_raw("AC;") else {
        return "ERR No answer from the radio".to_string();
    };
    let Some(digits) = kenwood_ac_digits(&reply) else {
        return format!("ERR Unexpected tuner answer from the radio: {reply}");
    };
    if cmd == "ATU_STATUS" {
        // "OK <tuning 1|0> <raw answer>": the third digit is 1 while a tune-up runs.
        return format!("OK {} {reply}", digits[2]);
    }
    // The first digit (receive through the tuner) is the operator's own setting: keep it, and
    // change only TX-AT in (second digit) and start/stop (third).
    let rx_at = digits[0];
    let start = cmd == "ATU_START";
    if !rig.send_raw_set(&format!("AC{rx_at}1{};", if start { '1' } else { '0' })) {
        return "ERR The radio control link is not reachable".to_string();
    }
    if start {
        // Same stand-down Nexus does when a tune-up reaches the radio (#322).
        engine.lock().unwrap_or_else(|e| e.into_inner()).note_atu_tune_started();
    }
    "OK".to_string()
}

/// The three digits of a Kenwood `AC` answer ("AC110;" -> ['1','1','0']), else None.
fn kenwood_ac_digits(reply: &str) -> Option<[char; 3]> {
    let d: Vec<char> = reply.trim().strip_prefix("AC")?.chars().take(3).collect();
    (d.len() == 3 && d.iter().all(|c| c.is_ascii_digit())).then(|| [d[0], d[1], d[2]])
}

fn handle_control_connection(
    mut stream: std::net::TcpStream,
    engine: Arc<Mutex<Engine>>,
    external_cache: Arc<external_data::SharedCache>,
    live_feeds_cache: Arc<live_feeds::LiveFeedsCache>,
    session_token: Arc<str>,
    contest_bridge_state: Arc<Mutex<contest_bridge::ContestBridge>>,
    log_host: Option<Arc<logbook_host::LogHost>>,
) {
    use std::io::Write;

    let line = match read_one_control_line(&stream, CONTROL_READ_TIMEOUT, MAX_CONTROL_LINE_BYTES) {
        Some(l) => l,
        None => return,
    };
    let line = line.trim();

        if line == "LIST_DEVICES" || line == "LIST_OUTPUT_DEVICES" {
            let (inputs, outputs) = tempo_audio::device::available_devices();
            let devices = if line == "LIST_DEVICES" { inputs } else { outputs };
            for dev in devices {
                let _ = writeln!(stream, "{}", dev.name);
            }
        } else if line == "SNAPSHOT" {
            // Poisoned-lock tolerant, same reasoning as Nexus's own src-tauri: a panic
            // elsewhere while holding this lock must not also take the control channel's
            // ability to report state down with it.
            // split_provenance is read under the SAME lock as the snapshot, so its flags line up
            // with this snapshot's recent_decodes rows (built 1:1, in order, from last_decodes).
            let (snap, special_op, split_provenance) = {
                let mut eng = engine.lock().unwrap_or_else(|e| e.into_inner());
                // Nexus-owned logbook only (logbook_host.rs): Nexus's own resend of a change the
                // disk refused for a reason that can pass -- where the Nexus desktop makes the same
                // call, on the snapshot poll. No I/O.
                if log_host.is_some() {
                    eng.log_resend_due();
                }
                (eng.snapshot(), special_op_wire(&eng), eng.last_decodes_multiplexed().to_vec())
            };
            // Independent audit finding, 2026-08-23 (EngineHost ownership / session identity):
            // sessionToken/pid are injected at the JSON level, AFTER AppSnapshot (a pinned
            // Nexus/tempo-app type) has already been serialized normally -- this never touches
            // the pinned struct itself, only wraps its own output with two extra top-level
            // fields via serde_json::Value. Jimmy's DirectSnapshot (WsjtxClient.Direct.cs)
            // compares sessionToken against the value it generated before launching this exact
            // process and never marks Direct connected/sends TX-capable commands without a
            // match -- see that file's own comment. pid is informational only (support-report/
            // diagnostic cross-check), not itself required for the match.
            //
            // Nexus modernization Stage 4 (2026-09-08): `decodeSemantics` is injected the same
            // way -- one entry per `recent_decodes` entry, SAME order -- carrying the typed
            // FT8/FT4 facts (`to` call, directed-CQ target, numeric report, R-report vs
            // report, RRR/RR73/73 subtype, call form, active-QSO relationship) that
            // `DecodeRow` does not expose and Jimmy re-derives with its own parser. Built from
            // `tempo_core::message::Msg::parse` -- Nexus's OWN public parser, not a reparse --
            // and never touching the pinned `AppSnapshot` struct. Jimmy shadow-compares it
            // against `WsjtxMessage` (Stage 5); nothing acts on it until proven equal. See
            // EngineHost/src/decode_semantics.rs and C:\chat gpt\nexus plan.txt Section 7.
            let partner = snap.qso.as_ref().and_then(|q| q.dxcall.as_deref());
            let mut decode_semantics: Vec<decode_semantics::DecodeSemantics> = snap
                .recent_decodes
                .iter()
                .map(|row| {
                    decode_semantics::DecodeSemantics::from_decode(&row.message, &snap.mycall, partner)
                })
                .collect();
            // Multi-answer halves the engine already split: from the engine's own record of the
            // split (Jimmy compat patch), never reconstructed from matching measurements.
            decode_semantics::DecodeSemantics::apply_split_provenance(&split_provenance, &mut decode_semantics);
            // Stage 10: the same envelope for the QSO's own "now sending" text, so Jimmy's
            // completion / TX-tracking path reads a typed kind instead of re-parsing
            // qso.txNow with WsjtxMessage. Same Msg::parse -- no Nexus patch. `None` when
            // listening / no active QSO.
            let qso_tx_semantics: Option<decode_semantics::DecodeSemantics> = snap
                .qso
                .as_ref()
                .and_then(|q| q.tx_now.as_deref())
                .filter(|t| !t.is_empty())
                .map(|t| decode_semantics::DecodeSemantics::from_decode(t, &snap.mycall, partner));
            match serde_json::to_value(&snap) {
                Ok(serde_json::Value::Object(mut obj)) => {
                    obj.insert("sessionToken".to_string(), serde_json::Value::String(session_token.to_string()));
                    obj.insert("pid".to_string(), serde_json::Value::Number(std::process::id().into()));
                    obj.insert(
                        "decodeSemantics".to_string(),
                        serde_json::to_value(&decode_semantics)
                            .unwrap_or(serde_json::Value::Array(Vec::new())),
                    );
                    obj.insert(
                        "qsoTxSemantics".to_string(),
                        serde_json::to_value(&qso_tx_semantics).unwrap_or(serde_json::Value::Null),
                    );
                    // The engine's ACTUAL Hound state ("none" | "hound") -- Jimmy shows this, not
                    // its own request, so an engine restart (Nexus drops Hound at launch) or a
                    // tier switch away from FT8 is visible immediately. See set_special_op.
                    obj.insert("specialOp".to_string(), serde_json::Value::String(special_op.to_string()));
                    // Nexus's OWN base call of the engine's QSO partner (`message::base_call`:
                    // "VA3LG/W2" -> "VA3LG", "KH8/W1AW" -> "W1AW"), so Jimmy can tell that the
                    // station it started the contact with is the engine's partner under a
                    // portable spelling, without re-implementing the rule (live 2026-09-26: a
                    // directed "CQ POTA VA3LG" answered as "VA3LG/W2"; the engine completed it,
                    // Jimmy never logged it).
                    if let Some(dx) = partner {
                        obj.insert(
                            "qsoDxcallBase".to_string(),
                            serde_json::Value::String(tempo_core::message::base_call(dx)),
                        );
                    }
                    // Nexus contesting foundation, phase 3: same injection pattern as
                    // sessionToken/pid above -- never touches the pinned AppSnapshot struct.
                    // Lets Jimmy confirm a contest session is active (and which durable instance
                    // id to expect completions under) without waiting for the first completion.
                    let contest_session_instance_id = contest_bridge_state
                        .lock()
                        .unwrap_or_else(|e| e.into_inner())
                        .active_session_instance_id()
                        .map(|s| s.to_string());
                    obj.insert(
                        "contestSessionInstanceId".to_string(),
                        match contest_session_instance_id {
                            Some(id) => serde_json::Value::String(id),
                            None => serde_json::Value::Null,
                        },
                    );
                    let _ = writeln!(stream, "{}", serde_json::Value::Object(obj));
                }
                Ok(_) => {
                    let _ = writeln!(stream, "ERR snapshot serialize failed: not a JSON object");
                }
                Err(e) => {
                    let _ = writeln!(stream, "ERR snapshot serialize failed: {e}");
                }
            }
        } else if let Some(json) = line.strip_prefix("REPLY ") {
            match serde_json::from_str::<ReplyArgs>(json) {
                Ok(a) => {
                    // call_station_ctx has exactly one Err path (Engine::call_station_ctx,
                    // tempo-app/src/engine.rs): "No recent decode from <call>" when reply_msg
                    // is Some but no matching/fallback decode slot exists. Its own comment is
                    // explicit that this is a real refusal, not a warning -- "REFUSE BEFORE ANY
                    // STATE CHANGES ... bail must mean nothing happened": no QSO starts, TX is
                    // not armed for this station. Previously this Result was discarded and OK
                    // was written unconditionally, so a refused reply was indistinguishable from
                    // a real one on Jimmy Next's side. Propagate the real outcome instead --
                    // same shape every other fallible command in this match already uses.
                    let result = engine.lock().unwrap_or_else(|e| e.into_inner()).call_station_ctx(
                        &a.dxcall,
                        a.dxgrid.as_deref(),
                        a.reply_msg.as_deref(),
                        a.reply_snr,
                        a.dx_freq_hz,
                    );
                    let _ = writeln!(stream, "{}", reply_wire_response(result));
                }
                Err(e) => {
                    let _ = writeln!(stream, "ERR bad REPLY args: {e}");
                }
            }
        } else if let Some(dir) = parse_call_cq_line(line) {
            // Jimmy's own Call-CQ start/resume command -- Direct's control protocol had
            // REPLY/HALT_TX/SET_TX_ENABLED/SET_TIER/SET_FREQUENCY/SET_TUNING and setters, but
            // nothing meant "start calling CQ" at all (release-audit finding, 2026-08-20; see
            // WsjtxClient.Uploads.cs's own long-standing comment on this exact gap). SetupCq
            // (WsjtxClient.cs) already computes curCmd/qsoState locally on every Call-CQ start
            // AND on every post-QSO auto-resume, but under Direct mode neither one ever reached
            // the engine -- Jimmy could believe it was calling CQ while the radio transmitted
            // nothing. Deliberately Engine::call_cq, NOT Engine::start_cq: start_cq calls
            // set_mode("qso-run"), which hands the ENTIRE pileup -- who to answer next -- to
            // Nexus's own auto-answer sequencer (see its own doc comment: "including the
            // return-to-CQ after each pileup contact"). Jimmy's CallQueueRanker already owns
            // that decision (award-priority ranking, not "first/strongest caller"); using
            // start_cq would put two different callers-choosers in the same seat -- exactly the
            // kind of duplicate ownership this whole audit pass was watching for. call_cq queues
            // one structured CQ frame and arms TX without touching self.mode at all, matching
            // call_station_ctx's existing REPLY role: Jimmy decides what happens next, the
            // engine only executes the one transmission it was just told to make. dir is the
            // directed-CQ token Jimmy's own NextDirCq() already resolved (e.g. "DX"), or None
            // for a plain CQ -- see parse_call_cq_line's own comment for why this can no longer
            // silently reject the plain-CQ case.
            let result = engine.lock().unwrap_or_else(|e| e.into_inner()).call_cq(dir);
            let _ = writeln!(stream, "{}", reply_wire_response(result));
        } else if line == "HALT_TX" {
            engine.lock().unwrap_or_else(|e| e.into_inner()).halt_tx();
            let _ = writeln!(stream, "OK");
        } else if let Some(v) = line.strip_prefix("SET_TX_ENABLED ") {
            engine.lock().unwrap_or_else(|e| e.into_inner()).set_tx_enabled(v.trim() == "1");
            let _ = writeln!(stream, "OK");
        } else if let Some(v) = line.strip_prefix("SET_PSKREPORTER ") {
            engine.lock().unwrap_or_else(|e| e.into_inner()).set_pskreporter(v.trim() == "1");
            let _ = writeln!(stream, "OK");
        } else if let Some(v) = line.strip_prefix("SET_MIC_GAIN ") {
            match v.trim().parse::<f32>() {
                Ok(frac) => {
                    engine.lock().unwrap_or_else(|e| e.into_inner()).set_mic_gain(frac);
                    let _ = writeln!(stream, "OK");
                }
                Err(e) => {
                    let _ = writeln!(stream, "ERR bad SET_MIC_GAIN value: {e}");
                }
            }
        } else if let Some(v) = line.strip_prefix("SET_TX_LEVEL ") {
            // Added 2026-08-10: Jimmy's own F11/F12 (audio level up/down) hotkeys were wired to
            // SET_MIC_GAIN above instead -- confirmed wrong, live, by tracing every consumer of
            // Engine::mic_gain() in tempo-audio/src/service.rs: it's applied in exactly one
            // place, explicitly commented "Only the Phone section drives these (the FT8 TX path
            // is idle there)" -- it has no effect on FT8/FT4 transmit audio at all. Engine::
            // set_tx_level is the real equivalent of WSJT-X's own "Pwr" slider / the original
            // Andy WM8Q fork's proprietary "Set Audio Level" feature this hotkey was always
            // meant to replace (see set_tx_level's own doc comment, tempo-app/src/engine.rs:
            // "the radio loop reads settings.tx_level each slot and applies it to the audio
            // backend, so this takes effect live"). Sound-card/software side of the signal
            // chain (scales the generated tone before the sound card), not the radio's own
            // physical MIC/DATA gain -- the correct place to trim drive level to avoid
            // over-driving the radio's audio input in the first place.
            match v.trim().parse::<f32>() {
                Ok(frac) => {
                    engine.lock().unwrap_or_else(|e| e.into_inner()).set_tx_level(frac);
                    let _ = writeln!(stream, "OK");
                }
                Err(e) => {
                    let _ = writeln!(stream, "ERR bad SET_TX_LEVEL value: {e}");
                }
            }
        } else if let Some(v) = line.strip_prefix("SET_TIER ") {
            // Jimmy's own Alt+M (Toggle Mode FT8/FT4) hotkey -- root-caused live, 2026-08-09:
            // under classic WSJT-X/UDP mode, mode switching was ALWAYS just "halt Tx, then the
            // operator changes it in WSJT-X's own UI" (WSJT-X's UDP API has no outbound
            // mode-change command at all -- Jimmy only ever OBSERVED whichever mode WSJT-X's own
            // Status messages reported). Direct-engine mode has no separate WSJT-X UI to fall
            // back to, so it needs a real command instead. Engine::set_tier already exists; this
            // just exposes it, matching every other command above.
            // Correction, 2026-09-15: the ORIGINAL version of this comment claimed set_tier
            // "already safely halts any in-flight over across a tier switch" for every tier --
            // traced directly against tempo-app/src/engine.rs and that is only true for the
            // long-over tier family (WSPR/FST4/FST4W/Q65/...). For FT8/FT4 specifically (and
            // FT2/FT1/DX1/TempoFast/TempoDeep), set_tier deliberately does NOT halt an in-flight
            // over -- it is left to finish naturally on its own pre-computed timer, explicitly
            // documented in that source as "shipped gold-standard behaviour and not mine to
            // alter." Jimmy's own SetOperatingMode (WsjtxClient.Protocol.cs) halts Tx itself
            // before calling this command, independent of and more conservative than this
            // engine's own FT8/FT4 default -- a deliberate, separate Jimmy-side choice, not
            // something this command relies on or should be described as providing.
            match v.trim() {
                "FT8" | "FT4" => {
                    let requested = if v.trim() == "FT8" { tempo_app::dto::Tier::Ft8 } else { tempo_app::dto::Tier::Ft4 };
                    // Tier-confirmation redesign, 2026-09-15: one lock acquisition covers both
                    // the mutation and reading the resulting state back -- set_tier() is
                    // synchronous and infallible (confirmed directly from tempo-app/src/
                    // engine.rs), so by the time it returns, tier()/active_slot_secs() are
                    // already authoritative. Mirrors Nexus's own Tauri `set_tier` command
                    // (src-tauri/src/lib.rs: `eng.set_tier(tier); Ok(eng.snapshot())`).
                    let mut eng = engine.lock().unwrap_or_else(|e| e.into_inner());
                    set_tier_reconciling_special_op(&mut eng, requested);
                    let resulting_tier = eng.tier();
                    let period_secs = eng.active_slot_secs();
                    drop(eng);
                    let _ = writeln!(stream, "{}", set_tier_wire_response(resulting_tier, period_secs));
                }
                other => {
                    let _ = writeln!(stream, "ERR bad SET_TIER value: {other}");
                }
            }
        } else if let Some(v) = line.strip_prefix("SET_SPECIAL_OP ") {
            // Jimmy's Hound toggle -- see set_special_op. Responds "OK none" / "OK hound" (the
            // engine's resulting state) or "ERR <message>" (bad value, or Hound outside FT8).
            let mut eng = engine.lock().unwrap_or_else(|e| e.into_inner());
            match set_special_op(&mut eng, v) {
                Ok(state) => { let _ = writeln!(stream, "OK {state}"); }
                Err(e) => { let _ = writeln!(stream, "ERR {e}"); }
            }
        } else if let Some(v) = line.strip_prefix("SET_TUNING ") {
            engine.lock().unwrap_or_else(|e| e.into_inner()).set_tune(v.trim() == "1");
            let _ = writeln!(stream, "OK");
        } else if line == "ATU_START" || line == "ATU_STOP" || line == "ATU_STATUS" {
            let _ = writeln!(stream, "{}", kenwood_atu(&engine, line));
        } else if let Some(v) = line.strip_prefix("SET_TX_OFFSET ") {
            match parse_offset_arg("SET_TX_OFFSET", v) {
                Ok(hz) => {
                    engine.lock().unwrap_or_else(|e| e.into_inner()).set_tx_offset(hz);
                    let _ = writeln!(stream, "OK");
                }
                Err(e) => {
                    let _ = writeln!(stream, "{e}");
                }
            }
        } else if let Some(v) = line.strip_prefix("SET_RX_OFFSET ") {
            // Mirror of SET_TX_OFFSET (just above) for the receive marker. Jimmy Next's
            // accessible Rx/Tx frequency controls (Options > Transmit "Transmit frequency"
            // modes, plus the Rx<->Tx and announce hotkeys) make Jimmy the single authority
            // on BOTH audio offsets: it always passes REPLY's dxFreqHz as null and drives
            // every RX/TX move explicitly through these two commands, so "follow the station
            // on receive" (Hold / Best modes, and every caller-answers-our-CQ case) is one
            // SET_RX_OFFSET with the worked decode's audio Hz -- no reliance on the engine's
            // own hold_tx_freq default. Engine::set_rx_offset already clamps to the passband
            // (200-4000 Hz) and is read by the next poll, same as set_tx_offset. Best-effort
            // like SET_DECODE_DEPTH/SET_TX_OFFSET: a dropped one just means the marker stays
            // where it was for one more cycle.
            match parse_offset_arg("SET_RX_OFFSET", v) {
                Ok(hz) => {
                    engine.lock().unwrap_or_else(|e| e.into_inner()).set_rx_offset(hz);
                    let _ = writeln!(stream, "OK");
                }
                Err(e) => {
                    let _ = writeln!(stream, "{e}");
                }
            }
        } else if let Some(json) = line.strip_prefix("SET_FREQUENCY ") {
            // Jimmy's own Band Up/Down and Options>Frequencies hotkeys used to retune the radio
            // by writing straight to the rigctld daemon the engine's own radio loop already owns
            // (RigctldClient.SetFrequency, a second concurrent client on the same CAT session) --
            // a second, uncoordinated writer to state the engine believes only IT changes (split,
            // TX halt, per-(band,mode) dial memory, retry/reconciliation all live in Engine::
            // set_frequency/tune_dial already). This routes the same request through the engine
            // instead, so Nexus is the only thing that ever writes a frequency to the radio.
            match serde_json::from_str::<SetFrequencyArgs>(json) {
                Ok(a) => match validate_set_frequency(&a) {
                    Ok(()) => {
                        engine.lock().unwrap_or_else(|e| e.into_inner()).set_frequency(a.hz / 1_000_000.0, &a.band, &a.mode);
                        let _ = writeln!(stream, "OK");
                    }
                    Err(e) => {
                        let _ = writeln!(stream, "ERR bad SET_FREQUENCY args: {e}");
                    }
                },
                Err(e) => {
                    let _ = writeln!(stream, "ERR bad SET_FREQUENCY args: {e}");
                }
            }
        } else if let Some(json) = line.strip_prefix("APPLY_SETTINGS ") {
            // Phase 4 lifecycle: radio/audio settings applied live (see ApplyRadioArgs).
            match serde_json::from_str::<ApplyRadioArgs>(json) {
                Ok(a) => {
                    let mut eng = engine.lock().unwrap_or_else(|e| e.into_inner());
                    let next = with_radio_settings(eng.settings(), &a);
                    eng.apply_settings(next);
                    let _ = writeln!(stream, "OK");
                }
                Err(e) => {
                    let _ = writeln!(stream, "ERR bad APPLY_SETTINGS args: {e}");
                }
            }
        } else if let Some(json) = line.strip_prefix("SET_WORKING_FREQUENCIES ") {
            // Live counterpart of --working-frequencies (Args's own doc comment) -- Jimmy sends
            // this when the operator saves an edited Options>Frequencies entry mid-session, so
            // Nexus's own auto-QSY (Engine::set_tier -> Engine::band_plan) picks up the new dial
            // immediately, without an EngineHost restart. working_frequencies is a normal
            // operator-facing settings field (Nexus's own Settings panel edits it the same way),
            // so Engine::apply_settings -- clone the current settings, change this one field,
            // apply -- is its intended live-update vehicle, same pattern Nexus's own test suite
            // uses throughout (engine.rs's apply_settings_* tests).
            match serde_json::from_str::<Vec<tempo_app::settings::WorkingFreq>>(json) {
                Ok(wf) => {
                    let mut eng = engine.lock().unwrap_or_else(|e| e.into_inner());
                    let mut s = eng.settings().clone();
                    s.working_frequencies = wf;
                    eng.apply_settings(s);
                    let _ = writeln!(stream, "OK");
                }
                Err(e) => {
                    let _ = writeln!(stream, "ERR bad SET_WORKING_FREQUENCIES args: {e}");
                }
            }
        } else if let Some(v) = line.strip_prefix("SET_DECODE_DEPTH ") {
            // WSJT-X "Fast/Normal/Deep" (1/2/3), Jimmy's Decode tab -- the one decode-tab
            // setting with a live setter (Engine::set_decode_depth), so this can change
            // mid-session without an engine restart, unlike SET_DECODE_FLOW_HZ/FHIGH_HZ/
            // AP_DECODE/AP_CQ_ONLY/SINGLE_DECODE, which don't exist because Engine has no live
            // setter for those four (Engine.settings is private to the tempo-app crate) -- they
            // stay startup-CLI-arg-only, same as rig-model/data-modes-plain-ssb/etc.
            match v.trim().parse::<u8>() {
                Ok(depth) if (1..=3).contains(&depth) => {
                    engine.lock().unwrap_or_else(|e| e.into_inner()).set_decode_depth(depth);
                    let _ = writeln!(stream, "OK");
                }
                _ => {
                    let _ = writeln!(stream, "ERR bad SET_DECODE_DEPTH value: {}", v.trim());
                }
            }
        } else if line == "CONTEST_LIST_EVENTS" {
            // Pure in-memory read of the bundled seed -- always fast, inline like SNAPSHOT.
            let _ = writeln!(stream, "{}", contest_bridge::list_events_json());
        } else if let Some(event_id) = line.strip_prefix("CONTEST_GET_RULESET ") {
            let _ = writeln!(stream, "{}", contest_bridge::get_ruleset_json(event_id.trim()));
        } else if let Some(json) = line.strip_prefix("CONTEST_ENTER ") {
            match serde_json::from_str::<contest_bridge::EnterArgs>(json) {
                Ok(args) => {
                    let mut eng = engine.lock().unwrap_or_else(|e| e.into_inner());
                    let mut bridge = contest_bridge_state.lock().unwrap_or_else(|e| e.into_inner());
                    match bridge.enter(&mut eng, &args) {
                        Ok(j) => { let _ = writeln!(stream, "OK {j}"); }
                        Err(e) => { let _ = writeln!(stream, "ERR {e}"); }
                    }
                }
                Err(e) => { let _ = writeln!(stream, "ERR bad CONTEST_ENTER args: {e}"); }
            }
        } else if let Some(json) = line.strip_prefix("CONTEST_LOG_MANUAL ") {
            #[derive(serde::Deserialize)]
            #[serde(rename_all = "camelCase")]
            struct LogManualArgs {
                call: String,
                fields: Vec<(String, String)>,
                mode: String,
                #[serde(default)]
                submode: String,
            }
            match serde_json::from_str::<LogManualArgs>(json) {
                Ok(args) => {
                    let mut eng = engine.lock().unwrap_or_else(|e| e.into_inner());
                    let bridge = contest_bridge_state.lock().unwrap_or_else(|e| e.into_inner());
                    match bridge.log_manual(&mut eng, &args.call, &args.fields, &args.mode, &args.submode) {
                        Ok(logged) => { let _ = writeln!(stream, "OK {{\"logged\":{logged}}}"); }
                        Err(e) => { let _ = writeln!(stream, "ERR {e}"); }
                    }
                }
                Err(e) => { let _ = writeln!(stream, "ERR bad CONTEST_LOG_MANUAL args: {e}"); }
            }
        } else if line == "CONTEST_EXIT" {
            let mut eng = engine.lock().unwrap_or_else(|e| e.into_inner());
            let mut bridge = contest_bridge_state.lock().unwrap_or_else(|e| e.into_inner());
            match bridge.exit(&mut eng) {
                Ok(()) => { let _ = writeln!(stream, "OK"); }
                Err(e) => { let _ = writeln!(stream, "ERR {e}"); }
            }
        } else if let Some(v) = line.strip_prefix("CONTEST_QSOS_SINCE ") {
            match v.trim().parse::<u64>() {
                Ok(after_seq) => {
                    let mut eng = engine.lock().unwrap_or_else(|e| e.into_inner());
                    let bridge = contest_bridge_state.lock().unwrap_or_else(|e| e.into_inner());
                    let _ = writeln!(stream, "{}", bridge.qsos_since(&mut eng, after_seq));
                }
                Err(_) => { let _ = writeln!(stream, "ERR bad CONTEST_QSOS_SINCE seq: {}", v.trim()); }
            }
        } else if let Some(v) = line.strip_prefix("CONTEST_QSO_ACK ") {
            match v.trim().parse::<u64>() {
                Ok(seq) => {
                    let mut bridge = contest_bridge_state.lock().unwrap_or_else(|e| e.into_inner());
                    bridge.ack(seq);
                    let _ = writeln!(stream, "OK");
                }
                Err(_) => { let _ = writeln!(stream, "ERR bad CONTEST_QSO_ACK seq: {}", v.trim()); }
            }
        } else if line == "CONTEST_REBUILD_BEGIN" {
            let mut eng = engine.lock().unwrap_or_else(|e| e.into_inner());
            let mut bridge = contest_bridge_state.lock().unwrap_or_else(|e| e.into_inner());
            match bridge.rebuild_begin(&mut eng) {
                Ok(j) => { let _ = writeln!(stream, "OK {j}"); }
                Err(e) => { let _ = writeln!(stream, "ERR {e}"); }
            }
        } else if let Some(json) = line.strip_prefix("CONTEST_REBUILD_APPEND ") {
            #[derive(serde::Deserialize)]
            #[serde(rename_all = "camelCase")]
            struct AppendArgs {
                rebuild_token: String,
                contacts: Vec<contest_bridge::RebuildContact>,
            }
            match serde_json::from_str::<AppendArgs>(json) {
                Ok(args) => {
                    let mut bridge = contest_bridge_state.lock().unwrap_or_else(|e| e.into_inner());
                    match bridge.rebuild_append(&args.rebuild_token, &args.contacts) {
                        Ok(()) => { let _ = writeln!(stream, "OK"); }
                        Err(e) => { let _ = writeln!(stream, "ERR {e}"); }
                    }
                }
                Err(e) => { let _ = writeln!(stream, "ERR bad CONTEST_REBUILD_APPEND args: {e}"); }
            }
        } else if let Some(token) = line.strip_prefix("CONTEST_REBUILD_COMMIT ") {
            let mut eng = engine.lock().unwrap_or_else(|e| e.into_inner());
            let mut bridge = contest_bridge_state.lock().unwrap_or_else(|e| e.into_inner());
            match bridge.rebuild_commit(&mut eng, token.trim()) {
                Ok(j) => { let _ = writeln!(stream, "OK {j}"); }
                Err(e) => { let _ = writeln!(stream, "ERR {e}"); }
            }
        } else if let Some(json) = line.strip_prefix("CONTEST_EXPORT ") {
            #[derive(serde::Deserialize)]
            #[serde(rename_all = "camelCase")]
            struct ExportArgs {
                format: String,
                #[serde(default)]
                operator_name: String,
                #[serde(default)]
                contest_email: String,
            }
            match serde_json::from_str::<ExportArgs>(json) {
                Ok(args) => {
                    let bridge = contest_bridge_state.lock().unwrap_or_else(|e| e.into_inner());
                    match bridge.export(&args.format, &args.operator_name, &args.contest_email) {
                        Ok(text) => match serde_json::to_string(&text) {
                            Ok(j) => { let _ = writeln!(stream, "OK {j}"); }
                            Err(e) => { let _ = writeln!(stream, "ERR could not encode export: {e}"); }
                        },
                        Err(e) => { let _ = writeln!(stream, "ERR {e}"); }
                    }
                }
                Err(e) => { let _ = writeln!(stream, "ERR bad CONTEST_EXPORT args: {e}"); }
            }
        } else if line == "OTA_SPOTS" {
            // Cache-only, always fast -- safe to handle inline on this accept loop like SNAPSHOT.
            let _ = writeln!(stream, "{}", external_cache.spots_json());
        } else if line == "SPACE_WX" {
            let _ = writeln!(stream, "{}", external_cache.space_wx_json());
        } else if line == "BAND_CONDITIONS" {
            // Cache-only read (the rolling PSK Reporter window) + one PropAdvisor pass over up to
            // 13 bands -- cheap enough to run inline on this accept loop, same as SNAPSHOT/
            // OTA_SPOTS/SPACE_WX, not a network call.
            let wx = external_cache.current_space_wx();
            let _ = writeln!(stream, "{}", live_feeds_cache.band_conditions_json(wx.as_ref()));
        } else if line == "DX_SPOTS" {
            // Cache-only read of the DX-cluster/RBN telnet buffer -- always fast.
            let _ = writeln!(stream, "{}", live_feeds_cache.dx_spots_json());
        } else if line == "LOG_STATUS" {
            // Logbook migration Phase 1 (logbook_host.rs) -- the LOG_* family answers only when
            // this process was launched with --log-dir; otherwise nothing about it exists.
            match &log_host {
                Some(host) => { let _ = writeln!(stream, "{}", logbook_host::status_json(host, &engine)); }
                None => { let _ = writeln!(stream, "{{\"enabled\":false}}"); }
            }
        } else if let Some(json) = line.strip_prefix("LOG_QSO ") {
            // Durable: answers only once the contact is on disk, or says it is not (up to
            // logbook_host::DURABLE_WAIT). This connection's own thread waits; the engine lock is
            // never held while it does.
            match (&log_host, serde_json::from_str::<logbook_host::LogQsoArgs>(json)) {
                (None, _) => { let _ = writeln!(stream, "ERR logbook not enabled"); }
                (Some(_), Err(e)) => { let _ = writeln!(stream, "ERR bad LOG_QSO args: {e}"); }
                (Some(host), Ok(a)) => {
                    let reply = logbook_host::log_qso(host, &engine, a);
                    let _ = writeln!(stream, "{}", serde_json::to_string(&reply).unwrap_or_default());
                }
            }
        } else if line == "LOG_ROWS" || line.starts_with("LOG_ROWS ") {
            let arg = line.strip_prefix("LOG_ROWS").unwrap_or("").trim();
            let parsed = if arg.is_empty() {
                Ok(logbook_host::LogRowsArgs::default())
            } else {
                serde_json::from_str::<logbook_host::LogRowsArgs>(arg)
            };
            match (&log_host, parsed) {
                (None, _) => { let _ = writeln!(stream, "ERR logbook not enabled"); }
                (Some(_), Err(e)) => { let _ = writeln!(stream, "ERR bad LOG_ROWS args: {e}"); }
                (Some(_), Ok(a)) => { let _ = writeln!(stream, "{}", logbook_host::rows_json(&engine, &a)); }
            }
        } else if let Some((name, json)) = ["LOG_EDIT ", "LOG_DELETE ", "LOG_IMPORT ", "LOG_MERGE ", "LOG_STAMP_UPLOAD "]
            .iter()
            .find_map(|p| line.strip_prefix(p).map(|rest| (p.trim(), rest)))
        {
            // The rest of the logbook's durable writes (logbook_host.rs): each answers only once
            // its change is on disk, or says it is not.
            let reply: Result<logbook_host::WriteReply, String> = match &log_host {
                None => Err("logbook not enabled".into()),
                Some(host) => match name {
                    "LOG_EDIT" => serde_json::from_str(json).map(|a| logbook_host::log_edit(host, &engine, a)).map_err(|e| e.to_string()),
                    "LOG_DELETE" => serde_json::from_str(json).map(|a| logbook_host::log_delete(host, &engine, a)).map_err(|e| e.to_string()),
                    "LOG_IMPORT" => serde_json::from_str(json).map(|a| logbook_host::log_import(host, &engine, a)).map_err(|e| e.to_string()),
                    "LOG_MERGE" => serde_json::from_str(json).map(|a| logbook_host::log_merge(host, &engine, a)).map_err(|e| e.to_string()),
                    _ => serde_json::from_str(json).map(|a| logbook_host::log_stamp(host, &engine, a)).map_err(|e| e.to_string()),
                },
            };
            match reply {
                Ok(r) => { let _ = writeln!(stream, "{}", serde_json::to_string(&r).unwrap_or_default()); }
                Err(e) => { let _ = writeln!(stream, "ERR {name}: {e}"); }
            }
        } else if let Some(json) = line.strip_prefix("LOG_UPLOAD ") {
            // Real network I/O (a service can take up to a minute): its own thread, like
            // EQSL_UPLOAD, so the accept loop and Jimmy's SNAPSHOT poll never wait on it.
            match (&log_host, serde_json::from_str::<logbook_host::LogUploadArgs>(json)) {
                (None, _) => { let _ = writeln!(stream, "ERR logbook not enabled"); }
                (Some(_), Err(e)) => { let _ = writeln!(stream, "ERR bad LOG_UPLOAD args"); let _ = e; }
                (Some(host), Ok(a)) => {
                    let host = host.clone();
                    let engine = engine.clone();
                    std::thread::spawn(move || {
                        let mut stream = stream;
                        let reply = logbook_host::log_upload(&host, &engine, a);
                        let _ = writeln!(stream, "{reply}");
                        let _ = stream.shutdown(std::net::Shutdown::Write);
                    });
                    return;
                }
            }
        } else if let Some(json) = line.strip_prefix("LOG_EXPORT ") {
            match (&log_host, serde_json::from_str::<logbook_host::LogFileArgs>(json)) {
                (None, _) => { let _ = writeln!(stream, "ERR logbook not enabled"); }
                (Some(_), Err(e)) => { let _ = writeln!(stream, "ERR bad LOG_EXPORT args: {e}"); }
                (Some(_), Ok(a)) => { let _ = writeln!(stream, "{}", logbook_host::log_export(&engine, a)); }
            }
        } else if line == "LOG_FLUSH" {
            match &log_host {
                None => { let _ = writeln!(stream, "ERR logbook not enabled"); }
                Some(_) => {
                    let reply = logbook_host::flush(&engine, logbook_host::DURABLE_WAIT);
                    let _ = writeln!(stream, "{}", serde_json::to_string(&reply).unwrap_or_default());
                }
            }
        } else if line == "SHUTDOWN" || line.starts_with("SHUTDOWN ") {
            // Graceful exit (logbook migration plan 6.1; Jimmy starts using it in Phase 4), in the
            // order Nexus's own quit uses (src-tauri quit.rs, `prepare_quit` + `stop_the_radio`):
            // the transmitter comes off the air FIRST and a save never delays that; then no new
            // log write is taken and every logbook change is put on disk; the answer says where
            // they stand; THEN the process exits. Only this session's owner may ask -- the token
            // Jimmy launched us with -- so a stray local client cannot end the session.
            let token = line.strip_prefix("SHUTDOWN").unwrap_or("").trim();
            if token != &*session_token {
                let _ = writeln!(stream, "ERR session token mismatch");
            } else {
                use std::sync::atomic::Ordering;
                SHUTTING_DOWN.store(true, Ordering::SeqCst);
                engine.lock().unwrap_or_else(|e| e.into_inner()).halt_tx();
                // Nexus's `stop_the_radio`: ask the radio loop to stop, and wait (bounded, 3 s --
                // a step can be blocked in a CAT read for ~2.5 s) until it says it has UNKEYED,
                // rather than a fixed sleep; then end any daemon a wedged loop never dropped.
                let radio = RADIO_RUNNING.load(Ordering::SeqCst);
                let unkeyed = if radio {
                    tempo_audio::service::SHUTDOWN.store(true, Ordering::Relaxed);
                    let deadline = std::time::Instant::now() + std::time::Duration::from_millis(3_000);
                    while !tempo_audio::service::SHUTDOWN_DONE.load(Ordering::Relaxed)
                        && std::time::Instant::now() < deadline
                    {
                        std::thread::sleep(std::time::Duration::from_millis(20));
                    }
                    let done = tempo_audio::service::SHUTDOWN_DONE.load(Ordering::Relaxed);
                    tempo_audio::rigctld_proc::kill_leftover_daemons();
                    done
                } else {
                    true // no radio loop: nothing was ever keyed
                };
                let reply = match &log_host {
                    Some(host) => {
                        let mut v = serde_json::to_value(logbook_host::close(host, &engine, logbook_host::DURABLE_WAIT))
                            .unwrap_or_default();
                        v["unkeyed"] = serde_json::Value::Bool(unkeyed);
                        v.to_string()
                    }
                    None => serde_json::json!({ "saved": true, "logbook": false, "unkeyed": unkeyed }).to_string(),
                };
                let _ = writeln!(stream, "{reply}");
                let _ = stream.flush();
                let _ = stream.shutdown(std::net::Shutdown::Write);
                std::process::exit(0);
            }
        } else if let Some(json) = line.strip_prefix("EQSL_UPLOAD ") {
            // Credential-bearing, real network I/O (eQSL can take up to ~60s -- it builds the
            // file server-side). MUST NOT run inline: this accept loop is single-threaded, and
            // Jimmy Next polls SNAPSHOT every ~1s -- blocking it here for a minute would read as
            // a false-positive engine disconnect. Spawn a dedicated thread for just this
            // connection's request/response and let the accept loop move on immediately.
            match serde_json::from_str::<external_data::EqslUploadArgs>(json) {
                Ok(args) => {
                    std::thread::spawn(move || {
                        let mut stream = stream;
                        match external_data::eqsl_upload(&args) {
                            Ok(outcome) => {
                                let _ = writeln!(stream, "OK {outcome}");
                            }
                            Err(e) => {
                                let _ = writeln!(stream, "ERR {e}");
                            }
                        }
                        let _ = stream.shutdown(std::net::Shutdown::Write);
                    });
                }
                Err(e) => {
                    let _ = writeln!(stream, "ERR bad EQSL_UPLOAD args: {e}");
                    let _ = stream.shutdown(std::net::Shutdown::Write);
                }
            }
            return;
        } else if let Some(json) = line.strip_prefix("EQSL_DOWNLOAD ") {
            match serde_json::from_str::<external_data::EqslDownloadArgs>(json) {
                Ok(args) => {
                    std::thread::spawn(move || {
                        let mut stream = stream;
                        match external_data::eqsl_download(&args) {
                            // The ADIF body can itself contain newlines -- encode as one JSON
                            // string line so the wire framing (one response per line) holds.
                            Ok(adif) => match serde_json::to_string(&adif) {
                                Ok(json_str) => {
                                    let _ = writeln!(stream, "OK {json_str}");
                                }
                                Err(e) => {
                                    let _ = writeln!(stream, "ERR could not encode InBox body: {e}");
                                }
                            },
                            Err(e) => {
                                let _ = writeln!(stream, "ERR {e}");
                            }
                        }
                        let _ = stream.shutdown(std::net::Shutdown::Write);
                    });
                }
                Err(e) => {
                    let _ = writeln!(stream, "ERR bad EQSL_DOWNLOAD args: {e}");
                    let _ = stream.shutdown(std::net::Shutdown::Write);
                }
            }
            return;
        } else if let Some(json) = line.strip_prefix("HAMQTH_LOOKUP ") {
            match serde_json::from_str::<external_data::HamQthLookupArgs>(json) {
                Ok(args) => {
                    std::thread::spawn(move || {
                        let mut stream = stream;
                        match external_data::hamqth_lookup(&args) {
                            Ok(result) => match serde_json::to_string(&result) {
                                Ok(json_str) => {
                                    let _ = writeln!(stream, "OK {json_str}");
                                }
                                Err(e) => {
                                    let _ = writeln!(stream, "ERR could not encode lookup result: {e}");
                                }
                            },
                            Err(e) => {
                                let _ = writeln!(stream, "ERR {e}");
                            }
                        }
                        let _ = stream.shutdown(std::net::Shutdown::Write);
                    });
                }
                Err(e) => {
                    let _ = writeln!(stream, "ERR bad HAMQTH_LOOKUP args: {e}");
                    let _ = stream.shutdown(std::net::Shutdown::Write);
                }
            }
            return;
        } else if let Some(json) = line.strip_prefix("HAMQTH_TEST ") {
            match serde_json::from_str::<external_data::HamQthTestArgs>(json) {
                Ok(args) => {
                    std::thread::spawn(move || {
                        let mut stream = stream;
                        match external_data::hamqth_test(&args) {
                            Ok(()) => {
                                let _ = writeln!(stream, "OK");
                            }
                            Err(e) => {
                                let _ = writeln!(stream, "ERR {e}");
                            }
                        }
                        let _ = stream.shutdown(std::net::Shutdown::Write);
                    });
                }
                Err(e) => {
                    let _ = writeln!(stream, "ERR bad HAMQTH_TEST args: {e}");
                    let _ = stream.shutdown(std::net::Shutdown::Write);
                }
            }
            return;
        } else {
            let _ = writeln!(stream, "ERR unknown command");
        }
        let _ = stream.shutdown(std::net::Shutdown::Write);
}

/// Accepts connections and hands each one to its own thread (handle_control_connection) --
/// see that function's own doc comment (Codex Audit 03 finding 5) for why this changed from
/// fully serial, inline handling.
///
/// Independent audit finding 1, 2026-08-23 (confirmed bug, HIGH PRIORITY): this used to bind
/// its own TcpListener internally, INSIDE the spawned thread -- a bind failure (port already
/// held by another Jimmy/engine-host instance, or a stale process that never exited) only
/// printed to stderr and returned from that one thread, while main() below carried on into
/// run_radio() regardless, still opening real audio/CAT/PTT hardware with no usable control
/// channel. Worse, NativeEngineClient never handshakes the control-port TCP connection against
/// this specific process (see NativeEngineClient.cs's own Launch() -- confirmed no PID/session
/// check exists), so a second Jimmy instance launched while a first is still running could
/// silently end up controlling hardware nothing can reach, while ITS OWN commands land on
/// whichever older process still owns the port. Fixed by moving the bind to main(), BEFORE
/// run_radio starts and before this thread is even spawned -- see main()'s own comment at the
/// call site. A bind failure is now fatal to the entire process, matching every other startup
/// precondition failure in this file (--mycall/--mygrid, RadioConfig).
fn run_control_server(
    listener: std::net::TcpListener,
    engine: Arc<Mutex<Engine>>,
    external_cache: Arc<external_data::SharedCache>,
    live_feeds_cache: Arc<live_feeds::LiveFeedsCache>,
    session_token: Arc<str>,
    contest_bridge_state: Arc<Mutex<contest_bridge::ContestBridge>>,
    log_host: Option<Arc<logbook_host::LogHost>>,
) {
    for incoming in listener.incoming() {
        let stream = match incoming {
            Ok(s) => s,
            Err(_) => continue,
        };
        let engine = Arc::clone(&engine);
        let external_cache = Arc::clone(&external_cache);
        let live_feeds_cache = Arc::clone(&live_feeds_cache);
        let session_token = Arc::clone(&session_token);
        let contest_bridge_state = Arc::clone(&contest_bridge_state);
        let log_host = log_host.clone();
        std::thread::spawn(move || {
            handle_control_connection(stream, engine, external_cache, live_feeds_cache, session_token, contest_bridge_state, log_host);
        });
    }
}

/// Wire shape for the REPLY command's JSON argument -- field names match what
/// DirectEngineClient.cs sends (camelCase, mirroring AppSnapshot's own `rename_all`).
#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase")]
struct ReplyArgs {
    dxcall: String,
    dxgrid: Option<String>,
    reply_msg: Option<String>,
    reply_snr: Option<i32>,
    dx_freq_hz: Option<f32>,
}

/// Wire shape for APPLY_SETTINGS (logbook migration plan Phase 4 -- the Nexus desktop's lifecycle):
/// the radio and audio settings Jimmy passes on the command line at launch, applied LIVE instead of
/// relaunching this process. Same meanings as the CLI flags (see parse_args / the Settings literal in
/// main()). Nexus's radio loop rebuilds CAT (Transport::from_settings, rig_differs) and reopens audio
/// on its next tick by itself -- exactly how the Nexus desktop applies a settings save.
#[derive(serde::Deserialize, Debug, Clone)]
#[serde(rename_all = "camelCase")]
struct ApplyRadioArgs {
    audio_in: String,
    audio_out: String,
    rig_model: u32,
    rig_conn: String,
    rig_port: String,
    rig_addr: String,
    rig_baud: u32,
    ptt_method: String,
    rigctld_port: u16,
    plain_ssb_data_modes: bool,
    dont_set_mode: bool,
    ptt_data_source: bool,
    ptt_serial_port: String,
    split_mode: String,
}

/// The engine's CURRENT settings with only the radio/audio fields replaced. Deliberately not a
/// rebuild from launch arguments: `Engine::apply_settings` replaces the whole Settings, and
/// everything changed live since launch (dial and band, decode depth, PSK Reporter, special
/// operation, working frequencies) must survive a radio-settings save.
fn with_radio_settings(current: &Settings, a: &ApplyRadioArgs) -> Settings {
    let mut s = current.clone();
    s.audio_in = a.audio_in.clone();
    s.audio_out = a.audio_out.clone();
    s.rig_model = a.rig_model;
    s.rig_conn = a.rig_conn.clone();
    s.serial_port = a.rig_port.clone();
    s.rig_addr = a.rig_addr.clone();
    s.baud = a.rig_baud;
    s.ptt_method = a.ptt_method.clone();
    s.rigctld_port = a.rigctld_port;
    s.data_modes_plain_ssb = a.plain_ssb_data_modes;
    s.dont_set_mode = a.dont_set_mode;
    s.ptt_data_source = a.ptt_data_source;
    s.ptt_serial_port = a.ptt_serial_port.clone();
    s.split_mode = match a.split_mode.as_str() {
        "rig" => tempo_app::settings::SplitMode::Rig,
        "fakeit" => tempo_app::settings::SplitMode::FakeIt,
        _ => tempo_app::settings::SplitMode::None,
    };
    // JIMMY COMPAT: RFPOWER never-touch, unconditionally -- a settings save can never turn it off.
    s.disable_rfpower_probe = true;
    s
}

/// Wire shape for the SET_FREQUENCY command's JSON argument -- field names match what
/// WsjtxClient.Direct.cs sends (camelCase, mirroring ReplyArgs's own convention). `mode` is the
/// logical sideband/class label Engine::set_frequency expects ("USB"/"LSB"/"FM"/"CW"), not a raw
/// CAT mode word -- the engine's own rig_mode()/rig_mode_effective() derives the actual CAT
/// mode (e.g. PKTUSB) from it every radio-loop tick.
#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase")]
struct SetFrequencyArgs {
    hz: f64,
    band: String,
    mode: String,
}

fn main() {
    // As early as possible, matching crashlog's own "call once, as early as possible" contract
    // (its doc comment above) -- before arg parsing, before anything else, so nothing that
    // could itself fault runs unprotected.
    #[cfg(windows)]
    crashlog::install();

    // Not part of the continuous-service contract -- a quick, side-effect-free query mode so
    // Jimmy's Options dialog can populate its audio-device pickers without needing its own
    // cpal/WASAPI bindings. Prints one device's addressing name per line and exits. `.name` is
    // the identity string CpalBackend::open (and this exe's own --device/--output-device
    // arguments) expect verbatim; `.label` is display-only.
    if std::env::args().any(|a| a == "--list-devices") {
        let (inputs, _outputs) = tempo_audio::device::available_devices();
        for dev in inputs {
            println!("{}", dev.name);
        }
        return;
    }
    if std::env::args().any(|a| a == "--list-output-devices") {
        let (_inputs, outputs) = tempo_audio::device::available_devices();
        for dev in outputs {
            println!("{}", dev.name);
        }
        return;
    }

    let args = parse_args();
    log!(
        "jimmy-engine-host starting: mycall={} mygrid={} device={} outputDevice={} -> {} \
         rig=model{} conn={} port={} baud={} ptt={} rigctldPort={} controlPort={} (Phase 5: \
         driven by Nexus's own real run_radio loop -- Engine handles decode/TX/QSO/radio-\
         control, this process just configures and starts it)",
        args.mycall,
        args.mygrid,
        args.device.as_deref().unwrap_or("<system default>"),
        args.output_device.as_deref().unwrap_or("<system default>"),
        args.jimmy_addr,
        args.rig_model,
        args.rig_conn,
        if args.rig_conn == "network" { &args.rig_addr } else { &args.rig_port },
        args.rig_baud,
        args.ptt_method,
        args.rigctld_port,
        args.control_port,
    );

    if args.mycall == "NOCALL" || args.mygrid == "AA00" {
        eprintln!("FATAL: --mycall and --mygrid are required (got mycall={:?} mygrid={:?})", args.mycall, args.mygrid);
        std::process::exit(1);
    }

    // Read before args.* fields below get moved into settings/cfg -- control_port (u16) is
    // Copy, but grabbing it now keeps this independent of exactly which fields move later.
    let control_port = args.control_port;
    // Same reasoning -- session_token (String) is not Copy, so it must be taken (not merely
    // read) before settings/cfg construction below could otherwise move args.session_token
    // itself. Arc<str> so every per-connection handler thread gets a cheap clone rather than
    // needing its own owned String.
    let session_token: Arc<str> = Arc::from(args.session_token.as_str());

    // Tx parity 0: an initial default only -- Engine::call_station_ctx (the real WSJT-X
    // double-click-to-reply entry point) recomputes the correct parity per-QSO from decode
    // history every time, so this never matters once real traffic starts.
    let mut settings = Settings {
        mycall: args.mycall.clone(),
        mygrid: args.mygrid.clone(),
        dial_mhz: args.dial_freq as f64 / 1_000_000.0,
        ptt_method: args.ptt_method.clone(),
        rig_model: args.rig_model,
        serial_port: args.rig_port.clone(),
        baud: args.rig_baud,
        rig_conn: args.rig_conn.clone(),
        rig_addr: args.rig_addr.clone(),
        // Settings carries its OWN copy of rigctld_port (independent of RadioConfig's) --
        // RadioLoop's live-settings-reconciliation compares against THIS one every tick and
        // rebuilds the rig the instant they disagree, so leaving this at Settings::default()'s
        // 4532 silently discarded RadioConfig.rigctld_port below within the first tick,
        // confirmed live 2026-08-06 (rigctld spawned correctly on the requested port, then was
        // immediately torn down and respawned on 4532 before anything could connect to it).
        rigctld_port: args.rigctld_port,
        audio_in: args.device.clone().unwrap_or_default(),
        audio_out: args.output_device.clone().unwrap_or_default(),
        auto_log: false, // Jimmy owns logbook writes; the native engine must never double-log.
        data_modes_plain_ssb: args.plain_ssb_data_modes,
        ptt_data_source: args.ptt_data_source,
        pskreporter: args.pskreporter,
        dont_set_mode: args.dont_set_mode,
        ptt_serial_port: args.ptt_serial_port.clone(),
        split_mode: match args.split_mode.as_str() {
            "rig" => tempo_app::settings::SplitMode::Rig,
            "fakeit" => tempo_app::settings::SplitMode::FakeIt,
            _ => tempo_app::settings::SplitMode::None,
        },
        // Nexus's CAT broker (Settings::default()'s cat_broker: true) is a share endpoint for
        // OTHER programs (VarAC, N1MM, a logger) to reach the radio through Nexus -- Jimmy never
        // talks to it, has its own private control port instead. Left at the inherited default
        // it silently binds cat_broker_port (also 4532 by default) -- the exact same port Jimmy
        // explicitly asks its own per-radio rigctld for (--rigctld-port), and the broker winning
        // that race is what left rigctld never actually listening: confirmed live, 2026-08-11,
        // "Radio CAT link lost: ... target machine actively refused it" on every fresh connect
        // after this crate picked up the broker feature. Off here since nothing uses it.
        cat_broker: false,
        // Repeat limit authority split, 2026-08-24 (independent audit finding): Nexus's own
        // directed_max_calls (default Some(8)) governs every in-QSO directed step (AwaitReport/
        // Roger/RR73/etc, tempo-core's QsoStation::tx_capped) completely independently of
        // Jimmy's own "Repeat limit" -- confirmed live, a Jimmy limit of 3 kept transmitting
        // past it (5 real overs observed before manual intervention) because the two counters
        // never agreed and NEITHER of Nexus's own mechanisms actually disables tx_enabled when
        // the count is reached (only the separate, wall-clock-only watchdog below does that --
        // see tempo-app/src/engine.rs's own "THE CAPPED STATION IS STILL AN ARMED TRANSMITTER"
        // comment). None here fully disables that cap -- the well-supported, intrinsic "uncapped"
        // state (tx_capped() is bool::is_some_and, unconditionally false for None; every
        // Default/test constructor in tempo-core's own qso.rs already starts here) -- so Jimmy's
        // own DiscardCall (WsjtxClient.cs), which now actively sends SET_TX_ENABLED 0 the moment
        // ITS count is reached, is the only attempt-count-based stop. Disabling this also avoids
        // inheriting a documented upstream defect (.nexus-src/scripts/create-issues.sh, "No
        // latch": a capped station stays armed and can spontaneously re-key later with no
        // operator action) -- moot once nothing is ever capped this way. cq_max_calls
        // (unrelated -- only the CallingCq/"nobody answering at all" phase, not an in-QSO step)
        // is untouched: it already defaults to None/unbounded, matching stock WSJT-X.
        directed_max_calls: None,
        // tx_watchdog_min: the ONE remaining safety backstop after the above -- see
        // Args::tx_watchdog_min's own comment for where this value comes from (Jimmy's own
        // Automatic calculation, or Nexus's stock 6 if not passed at all).
        tx_watchdog_min: args.tx_watchdog_min,
        // Jimmy's ini-only tuneTimeoutSeconds when passed, else Nexus's stock value.
        tune_timeout_secs: args.tune_timeout_secs.unwrap_or_else(|| Settings::default().tune_timeout_secs),
        clock_check: args.clock_check.unwrap_or_else(|| Settings::default().clock_check),
        // Frequency-override authority split, 2026-08-24 -- see Args::working_frequencies' own
        // comment. Engine::band_plan (read by Engine::set_tier's own internal auto-QSY on every
        // tier switch) applies these the same way Nexus's own Settings panel would, so Nexus
        // never needs correcting by a follow-up Jimmy retune after a tier switch.
        working_frequencies: args.working_frequencies.clone(),
        ..Settings::default()
    };
    settings.wsjtx_udp = true;
    settings.wsjtx_udp_addr = args.jimmy_addr.clone();
    // JIMMY COMPAT (nexus-compat patch tempo-audio-*): RFPOWER never-touch, always on,
    // unconditionally -- not operator-configurable, no CLI flag. Set on BOTH Settings (so the
    // per-tick Transport::from_settings rebuild keeps re-stamping the Rig-level chokepoint after
    // any CAT reopen) and RadioConfig below (the startup seed + the RadioLoop lifetime mirror
    // that gates the loop's own three RFPOWER call sites). Reading RFPOWER on a freshly-spawned
    // rigctld can trip a destructive calibration-sweep bug in Hamlib's Kenwood backend
    // (Hamlib/Hamlib#1595) on first touch; more broadly, Jimmy's policy is that a read must
    // never be able to change anything on the radio, and the engine never adjusts the
    // operator's transmit drive. Safe telemetry meters (watts/SWR/ALC/COMP) are unaffected.
    settings.disable_rfpower_probe = true;

    // Decode tab settings -- `if let Some` rather than folding these into the struct literal
    // above so an operator who hasn't touched Options at all gets Nexus's own Settings::default()
    // for each (Deep depth, 200/2900 Hz passband, AP on, AP-CQ-only off, single-decode off)
    // rather than this file silently re-deciding what "default" means and drifting from Nexus's
    // own choice over time. decode_depth also gets applied here as the STARTING value; unlike
    // the other four, it additionally has a live control-port path (SET_DECODE_DEPTH below) since
    // Engine::set_decode_depth is safe to call mid-session -- these other four have no live
    // setter (Engine.settings is private to the tempo-app crate), so they're startup-only, same
    // as rig-model/data-modes-plain-ssb/etc. above.
    if let Some(v) = args.decode_depth { settings.decode_depth = v; }
    if let Some(v) = args.decode_flow_hz { settings.decode_flow_hz = v; }
    if let Some(v) = args.decode_fhigh_hz { settings.decode_fhigh_hz = v; }
    if let Some(v) = args.ap_decode { settings.ap_decode = v; }
    if let Some(v) = args.ap_cq_only { settings.ap_cq_only = v; }
    if let Some(v) = args.single_decode { settings.single_decode = v; }

    // Logbook migration Phase 1: a Nexus-owned logbook ONLY when --log-dir was passed (see
    // logbook_host.rs). Opened here, before the engine exists and so before its lock can be
    // taken -- Nexus's own launch order (a first open converts log.adi, which takes seconds on a
    // lifetime log). A store that cannot open leaves the session on log.adi, as Nexus does; a log
    // that cannot be kept at all stops the launch.
    let mut engine_value = Engine::with_settings(settings);
    if args.log_dir.is_some() {
        // Nexus's `launch_engine`: prove a logbook can be kept at all, then refuse every log
        // change until the operator's store is attached (a change sent to the placeholder store
        // would be lost with it; a debug build panics naming it).
        if let Err(e) = tempo_app::station::can_keep_a_log() {
            eprintln!("FATAL: jimmy-engine-host cannot keep a logbook: {e}");
            std::process::exit(1);
        }
        engine_value.refuse_log_changes_until_attached();
    }
    let log_host: Option<Arc<logbook_host::LogHost>> = match args.log_dir.as_deref() {
        None => None,
        Some(dir) => {
            let opened = logbook_host::open_store(dir);
            match logbook_host::LogHost::adopt(&mut engine_value, dir, opened) {
                Ok(host) => Some(Arc::new(host)),
                Err(e) => {
                    eprintln!("FATAL: jimmy-engine-host could not keep the logbook in {}: {e}", dir.display());
                    std::process::exit(1);
                }
            }
        }
    };
    let engine = Arc::new(Mutex::new(engine_value));
    // TEST ONLY (logbook migration Phase 3): a crash point for the recovery tests, honoured only
    // in a logbook-only start (--no-radio) and armed BEFORE the control server can take a
    // command -- see logbook_host.rs's "Phase 3 crash testing".
    if args.no_radio {
        // TEST ONLY, same gate: LOG_UPLOAD's service addresses pointed at a local fake server.
        if let Ok(base) = std::env::var("JIMMY_TEST_UPLOAD_BASE") {
            if !base.trim().is_empty() {
                logbook_host::set_upload_test_base(base.trim());
            }
        }
        if let Ok(spec) = std::env::var("JIMMY_TEST_CRASH_AT") {
            if !spec.trim().is_empty() {
                logbook_host::arm_test_crash(&spec);
            }
        }
    }

    // `Tier` (the FT8/FT4/TempoFast/... waveform selector) is separate from Settings'
    // "Digital" operating-mode category and is ONLY ever set by a live operator command in
    // Nexus's own app (src-tauri's set_tier, wired to a UI click) -- there is no default or
    // startup path that puts it anywhere but its #[default], Tier::TempoFast (Nexus's own
    // private 4-second protocol, not FT8). jimmy-engine-host has no UI to click, so without
    // this call every session ran TempoFast against a WSJT-X-protocol-speaking Jimmy the
    // whole time -- a far less-exercised code path under these conditions, and the real
    // cause of this process's repeated crashes (root-caused live, 2026-08-08, after two
    // narrower fixes each failed to stop a crash that reproduced with a session just left
    // sitting, no Options interaction). FT8 only for now -- Jimmy doesn't offer a mode
    // picker yet; add a --tier arg here when FT4/other modes are wired up.
    engine.lock().unwrap().set_tier(tempo_app::dto::Tier::Ft8);

    // Nexus contesting foundation, phase 3: startup wiring for the contest bridge. Cheap,
    // no-op-when-idle -- installing the call resolver costs nothing when no contest is ever
    // entered, so it happens unconditionally rather than lazily on first use (avoiding an
    // install-after-read ordering hazard for zero benefit). No --data-dir CLI arg yet (a
    // coordinated Jimmy-side change, deferred) -- defaults to a self-contained location under
    // LOCALAPPDATA so this works out of the box; see contest_data_dir's own comment.
    contest_bridge::install_call_resolver();
    // ⚠️ Must be set BEFORE any CONTEST_ENTER/restore -- Nexus's own set_mode (called by both)
    // reads this field to decide whether to merge a durable FD journal back into the fresh
    // session (engine.rs's own FieldDay mode-entry arm). Left unset, persist_fd_log() silently
    // no-ops (its own early return) and merge_adif() never runs -- a confirmed, found-live gap:
    // a contact logged before an EngineHost crash was NOT present after a real restart until
    // this line was added (session-instance identity restored correctly via the sidecar below,
    // but Nexus's own contest rows did not, because nothing had ever told Nexus where to
    // journal them). Cheap and safe to set unconditionally -- the file is only touched when a
    // contest is actually entered.
    engine.lock().unwrap().set_fd_log_path(contest_data_dir().join("field_day_journal.adi"));
    let contest_bridge_state = Arc::new(Mutex::new(contest_bridge::ContestBridge::new(contest_data_dir())));
    contest_bridge_state
        .lock()
        .unwrap()
        .restore_if_present(&mut engine.lock().unwrap());

    // POTA/SOTA spots + space weather: background-refreshed, credential-free, cached in memory
    // (see external_data.rs's own header comment). Independent of the engine/radio loop
    // entirely -- a POTA/SWPC outage can never affect decode/TX.
    let external_cache = external_data::SharedCache::new(&args.mygrid);
    external_cache.spawn_refresh_thread();

    // Band conditions (always -- mycall/mygrid are already known) + DX spots (only if the
    // operator configured a cluster server): see live_feeds.rs's own header comment. Same
    // "independent of the engine/radio loop" isolation as external_cache above.
    let live_feeds_cache = live_feeds::LiveFeedsCache::new(
        &args.mycall,
        &args.mygrid,
        args.dx_cluster_addr.as_deref(),
    );
    live_feeds_cache.spawn_feed_threads(args.dx_cluster_addr.as_deref());

    // Independent audit finding 1, 2026-08-23: bind the control-port listener HERE, on main's
    // own thread, synchronously, and treat a failed bind as fatal to the whole process --
    // BEFORE run_radio (below) ever starts opening real audio/CAT/PTT hardware. Previously the
    // bind happened inside run_control_server's own spawned thread, so a failure there only
    // logged to stderr while this thread carried on regardless (see run_control_server's own
    // updated doc comment for the full failure mode this closes). Only the bind itself moves
    // up-front; accepting/handling connections still happens on its own thread below exactly as
    // before, sharing the same Arc<Mutex<Engine>>.
    let control_listener = match std::net::TcpListener::bind(("127.0.0.1", control_port)) {
        Ok(l) => l,
        Err(e) => {
            eprintln!(
                "FATAL: jimmy-engine-host control server failed to bind 127.0.0.1:{control_port}: {e} \
                 -- another Jimmy/engine-host instance may already be running. Refusing to start \
                 (would otherwise control radio/audio hardware with no usable control channel)."
            );
            std::process::exit(1);
        }
    };
    {
        let control_engine = engine.clone();
        let control_cache = external_cache.clone();
        let control_live_feeds = live_feeds_cache.clone();
        let control_session_token = Arc::clone(&session_token);
        let control_contest_bridge = Arc::clone(&contest_bridge_state);
        let control_log_host = log_host.clone();
        std::thread::spawn(move || {
            run_control_server(control_listener, control_engine, control_cache, control_live_feeds, control_session_token, control_contest_bridge, control_log_host)
        });
    }

    let cfg = RadioConfig {
        ptt_method: args.ptt_method,
        rig_model: args.rig_model,
        serial_port: args.rig_port,
        baud: args.rig_baud,
        rig_conn: args.rig_conn,
        rig_addr: args.rig_addr,
        rigctld_port: args.rigctld_port,
        dial_hz: args.dial_freq,
        mode: "USB".to_string(),
        wsjtx_udp: true,
        wsjtx_addr: args.jimmy_addr,
        audio_in: args.device.unwrap_or_default(),
        audio_out: args.output_device.unwrap_or_default(),
        ptt_data_source: args.ptt_data_source,
        pskreporter: args.pskreporter,
        // JIMMY COMPAT (nexus-compat patch tempo-audio-telemetry.patch): never let the radio
        // loop's own routine telemetry poll issue an RFPOWER read at all. Always on,
        // unconditionally -- not operator-configurable, no CLI flag -- because reading RFPOWER
        // on a freshly-spawned rigctld process can trip a destructive calibration-sweep bug in
        // Hamlib's Kenwood backend (Hamlib/Hamlib#1595) on first touch, dropping the operator's
        // actual transmit power (confirmed live, twice, 2026-08-20). Jimmy's own policy is that
        // a read must never be able to change anything on the radio -- see nexus-compat/
        // README.md for the full story and what to do if a future Nexus revision obsoletes this.
        disable_rfpower_probe: true,
        // RadioConfig has no ptt_serial_port field -- Transport::from_cfg (tempo-audio/service.rs)
        // deliberately seeds it empty regardless (it's a GLOBAL keying-line setting the live
        // per-tick Transport::from_settings rebuild supplies instead); Settings.ptt_serial_port
        // above is what actually matters and is already native to Nexus.
        // RadioConfig::default()'s broker_self_port is Some(4532) -- Nexus's own CAT-broker
        // default port, matching Settings::default()'s cat_broker/cat_broker_port. Jimmy
        // disables the broker outright (see the Settings literal above, cat_broker: false), but
        // this INITIAL open -- Transport::from_cfg, run_radio's very first open_rig call, before
        // the live-settings reconciliation loop's first tick -- reads RadioConfig directly, not
        // Settings, so it never saw that override. Confirmed live, 2026-08-11: the first CAT
        // open died instantly on "CAT broker and rigctld are both on :4532" (open_cat's own
        // early-return), every session, before the second (correct) attempt from the settings
        // loop ever got a chance to leave the port in a clean state for it.
        broker_self_port: None,
        ..RadioConfig::default()
    };

    log!("jimmy-engine-host: starting run_radio (real audio, real decode, real TX/PTT)...");
    // run_radio's own doc comment (tempo-audio/src/service.rs): "Blocks -- call on a dedicated
    // thread." Nexus's own real app (src-tauri/src/lib.rs) always spawns it via
    // std::thread::spawn wrapped in catch_unwind; this process used to call it directly on its
    // own main thread instead, out of contract. Real candidate for this process's repeated heap-
    // corruption/access-violation crashes -- confirmed live, 2026-08-08, reproduced with a
    // session simply left running, zero Options/device-query interaction involved at all, which
    // ruled out the separate two-process device-listing race as the (sole) cause. Matching
    // Nexus's own calling convention exactly, including catch_unwind, rather than guessing
    // further at what else might differ.
    if args.no_radio {
        // Logbook-only start: no radio loop at all. The control server answers until SHUTDOWN
        // ends the process.
        log!("jimmy-engine-host: --no-radio: logbook and control server only");
        drop(cfg);
        wait_for_shutdown(None);
    }
    RADIO_RUNNING.store(true, std::sync::atomic::Ordering::SeqCst);
    let radio_handle = std::thread::spawn(move || {
        std::panic::catch_unwind(std::panic::AssertUnwindSafe(move || run_radio(engine, cfg)))
    });
    let joined = radio_handle.join();
    if SHUTTING_DOWN.load(std::sync::atomic::Ordering::SeqCst) {
        // The radio loop stopped because SHUTDOWN asked it to; SHUTDOWN is saving the logbook and
        // ends the process itself.
        wait_for_shutdown(Some(SHUTDOWN_BACKSTOP));
    }
    match joined {
        Ok(Ok(Ok(()))) => log!("jimmy-engine-host: run_radio exited normally"),
        Ok(Ok(Err(e))) => {
            eprintln!("FATAL: run_radio failed: {e}");
            std::process::exit(1);
        }
        Ok(Err(_)) | Err(_) => {
            eprintln!("FATAL: run_radio panicked on its dedicated thread");
            std::process::exit(1);
        }
    }
}

/// Park main() while the control thread finishes a SHUTDOWN (which ends the process itself).
/// `None` = until then (the logbook-only start); `Some(d)` = at most `d` after SHUTDOWN began,
/// then exit anyway rather than hang.
fn wait_for_shutdown(backstop: Option<std::time::Duration>) -> ! {
    let started = std::time::Instant::now();
    loop {
        std::thread::sleep(std::time::Duration::from_millis(200));
        if let Some(d) = backstop {
            if started.elapsed() >= d {
                eprintln!("jimmy-engine-host: SHUTDOWN did not finish in {} s; exiting", d.as_secs());
                std::process::exit(2);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn radio_args() -> ApplyRadioArgs {
        ApplyRadioArgs {
            audio_in: "USB Audio CODEC".into(),
            audio_out: "USB Audio CODEC out".into(),
            rig_model: 3073,
            rig_conn: "serial".into(),
            rig_port: "COM7".into(),
            rig_addr: String::new(),
            rig_baud: 38_400,
            ptt_method: "cat".into(),
            rigctld_port: 4540,
            plain_ssb_data_modes: false,
            dont_set_mode: true,
            ptt_data_source: true,
            ptt_serial_port: String::new(),
            split_mode: "fakeit".into(),
        }
    }

    // APPLY_SETTINGS replaces only the radio/audio fields: everything changed live since launch
    // (here the dial, decode depth and PSK Reporter) survives, and RFPOWER stays suppressed even
    // if the current settings somehow had it off.
    #[test]
    fn apply_settings_changes_only_the_radio_fields() {
        let mut current = Settings::default();
        current.dial_mhz = 7.074;
        current.decode_depth = 1;
        current.pskreporter = true;
        current.disable_rfpower_probe = false;
        let next = with_radio_settings(&current, &radio_args());
        assert_eq!(next.rig_model, 3073);
        assert_eq!(next.serial_port, "COM7");
        assert_eq!(next.ptt_method, "cat");
        assert_eq!(next.audio_in, "USB Audio CODEC");
        assert!(next.dont_set_mode && next.ptt_data_source);
        assert!(matches!(next.split_mode, tempo_app::settings::SplitMode::FakeIt));
        assert!(next.disable_rfpower_probe, "a settings save can never turn RFPOWER protection off");
        assert_eq!(next.dial_mhz, 7.074, "the dial the operator is on is kept");
        assert_eq!(next.decode_depth, 1);
        assert!(next.pskreporter);
    }

    // Applied to a real Engine, the change is visible in its settings and nothing else moved.
    #[test]
    fn apply_settings_reaches_the_engine() {
        let mut eng = Engine::new("KB0UZT", "EN34", 0);
        let before_dial = eng.settings().dial_mhz;
        let next = with_radio_settings(eng.settings(), &radio_args());
        eng.apply_settings(next);
        assert_eq!(eng.settings().rig_model, 3073);
        assert_eq!(eng.settings().rigctld_port, 4540);
        assert!(eng.settings().disable_rfpower_probe);
        assert_eq!(eng.settings().dial_mhz, before_dial);
        assert_eq!(eng.settings().mycall, "KB0UZT", "identity is not part of APPLY_SETTINGS");
    }

    // Regression coverage for the REPLY handler fix: previously call_station_ctx's Result was
    // discarded and "OK" was written unconditionally, so a refusal (Engine's own "No recent
    // decode from <call>" -- fired when reply_msg is Some but no decode context can be found,
    // and guaranteed to leave engine state untouched) was indistinguishable from a real success
    // on Jimmy Next's side. These two cases are exactly what reply_wire_response now decides.

    #[test]
    fn reply_wire_response_ok_reports_ok() {
        assert_eq!(reply_wire_response(Ok(())), "OK");
    }

    // Hound (2026-09-26): FT8 only, through Nexus's own settings save, and turned off when the
    // tier leaves FT8 -- so traditional Hound rules can never apply to an FT4 contact.
    #[test]
    fn hound_is_ft8_only_and_is_dropped_on_leaving_ft8() {
        use tempo_app::dto::Tier;
        let mut e = Engine::new("W9XYZ", "EN37", 0);
        e.set_tier(Tier::Ft4);
        assert!(set_special_op(&mut e, "hound").is_err(), "refused in FT4");
        assert_eq!(special_op_wire(&e), "none");
        set_tier_reconciling_special_op(&mut e, Tier::Ft8);
        assert_eq!(set_special_op(&mut e, "hound"), Ok("hound"));
        assert_eq!(special_op_wire(&e), "hound");
        set_tier_reconciling_special_op(&mut e, Tier::Ft4);
        assert_eq!(special_op_wire(&e), "none", "leaving FT8 turns Hound off");
        assert!(set_special_op(&mut e, "superhound").is_err(), "SuperHound is not offered");
    }

    #[test]
    fn reply_wire_response_err_reports_err_with_the_real_message() {
        let msg = "No recent decode from W1AW -- wait for their next transmission, then click again.";
        assert_eq!(reply_wire_response(Err(msg.to_string())), format!("ERR {msg}"));
    }

    // Tier-confirmation redesign, 2026-09-15: SET_TIER's response now carries the resulting tier
    // and period instead of a bare "OK" -- these lock in the exact wire shape Jimmy's own
    // DirectSetTier parses, and that the tier name matches AppSnapshot.link.tier's own wire
    // convention (Tier's Serialize impl), not a hand-maintained duplicate.
    #[test]
    fn set_tier_wire_response_ft8() {
        assert_eq!(
            set_tier_wire_response(tempo_app::dto::Tier::Ft8, 15.0),
            "OK FT8 15"
        );
    }

    #[test]
    fn set_tier_wire_response_ft4() {
        assert_eq!(
            set_tier_wire_response(tempo_app::dto::Tier::Ft4, 7.5),
            "OK FT4 7.5"
        );
    }

    // Release-audit finding (Codex Audit 02, 2026-08-21) -- release blocker: plain CQ
    // (Jimmy's own DirectSendCq sends exactly "CALL_CQ ", which the accept loop's line.trim()
    // reduces to bare "CALL_CQ" before any handler sees it) used to be UNREACHABLE --
    // strip_prefix("CALL_CQ ") required a literal trailing space no plain-CQ line could ever
    // have by the time it got here. This is the positive control: prove the plain case is
    // recognized at all, not just the directed case the original code happened to already
    // exercise correctly.
    #[test]
    fn parse_call_cq_line_recognizes_plain_cq_as_the_bare_command_with_no_directed_token() {
        assert_eq!(parse_call_cq_line("CALL_CQ"), Some(None));
    }

    #[test]
    fn parse_offset_arg_accepts_a_plain_hz_value_and_leaves_clamping_to_the_engine() {
        // 8000 is outside the 200-4000 passband, but that clamp is Engine::set_rx_offset's
        // job -- this parser only rejects things that aren't a finite number at all.
        assert_eq!(parse_offset_arg("SET_RX_OFFSET", " 8000 "), Ok(8000.0));
        assert_eq!(parse_offset_arg("SET_TX_OFFSET", "1500"), Ok(1500.0));
    }

    #[test]
    fn parse_offset_arg_rejects_non_numeric_and_non_finite_with_a_self_identifying_err_line() {
        assert_eq!(
            parse_offset_arg("SET_RX_OFFSET", "abc"),
            Err("ERR bad SET_RX_OFFSET value: invalid float literal".to_string())
        );
        assert!(parse_offset_arg("SET_TX_OFFSET", "NaN")
            .unwrap_err()
            .starts_with("ERR bad SET_TX_OFFSET value:"));
        assert_eq!(
            parse_offset_arg("SET_RX_OFFSET", "inf"),
            Err("ERR bad SET_RX_OFFSET value: not finite".to_string())
        );
    }

    #[test]
    fn parse_call_cq_line_recognizes_a_directed_token() {
        assert_eq!(parse_call_cq_line("CALL_CQ DX"), Some(Some("DX")));
    }

    #[test]
    fn parse_call_cq_line_trims_the_directed_token() {
        // Defensive: the accept loop's own line.trim() only trims the WHOLE line's outer
        // whitespace, not whatever a caller puts between "CALL_CQ" and a token.
        assert_eq!(parse_call_cq_line("CALL_CQ  DX  "), Some(Some("DX")));
    }

    #[test]
    fn parse_call_cq_line_treats_a_whitespace_only_token_as_plain_cq() {
        assert_eq!(parse_call_cq_line("CALL_CQ   "), Some(None));
    }

    #[test]
    fn parse_call_cq_line_rejects_an_unrelated_command() {
        assert_eq!(parse_call_cq_line("SNAPSHOT"), None);
    }

    #[test]
    fn parse_call_cq_line_rejects_a_command_that_merely_starts_with_the_same_letters() {
        // "CALL_CQX" must not be mistaken for a CALL_CQ variant just because it shares a prefix.
        assert_eq!(parse_call_cq_line("CALL_CQX"), None);
    }

    // Release-audit finding, 2026-08-20 (real validation/coverage for the SET_FREQUENCY
    // Direct contract): a positive control first -- proves this test module can actually see a
    // rejection at all, not just that every case happens to hit the Ok(()) branch.
    #[test]
    fn validate_set_frequency_accepts_a_self_consistent_20m_request() {
        let a = SetFrequencyArgs { hz: 14_074_000.0, band: "20m".to_string(), mode: "USB".to_string() };
        assert_eq!(validate_set_frequency(&a), Ok(()));
    }

    #[test]
    fn validate_set_frequency_accepts_band_case_insensitively() {
        let a = SetFrequencyArgs { hz: 7_074_000.0, band: "40M".to_string(), mode: "USB".to_string() };
        assert_eq!(validate_set_frequency(&a), Ok(()));
    }

    #[test]
    fn validate_set_frequency_rejects_a_frequency_band_mismatch() {
        // 14.074 MHz is real 20m -- claiming it's 40m must be refused, not silently accepted
        // and commanded to the radio anyway.
        let a = SetFrequencyArgs { hz: 14_074_000.0, band: "40m".to_string(), mode: "USB".to_string() };
        let result = validate_set_frequency(&a);
        assert!(result.is_err());
        assert!(result.unwrap_err().contains("20m"));
    }

    #[test]
    fn validate_set_frequency_rejects_a_frequency_off_any_ham_band() {
        let a = SetFrequencyArgs { hz: 11_000_000.0, band: "20m".to_string(), mode: "USB".to_string() };
        let result = validate_set_frequency(&a);
        assert!(result.is_err());
        assert!(result.unwrap_err().contains("not on any recognized amateur band"));
    }

    #[test]
    fn validate_set_frequency_rejects_non_positive_hz() {
        let a = SetFrequencyArgs { hz: 0.0, band: "20m".to_string(), mode: "USB".to_string() };
        assert!(validate_set_frequency(&a).is_err());
    }

    #[test]
    fn validate_set_frequency_rejects_non_finite_hz() {
        let a = SetFrequencyArgs { hz: f64::NAN, band: "20m".to_string(), mode: "USB".to_string() };
        assert!(validate_set_frequency(&a).is_err());
    }

    #[test]
    fn validate_set_frequency_rejects_empty_band() {
        let a = SetFrequencyArgs { hz: 14_074_000.0, band: "".to_string(), mode: "USB".to_string() };
        assert!(validate_set_frequency(&a).is_err());
    }

    #[test]
    fn validate_set_frequency_rejects_empty_mode() {
        let a = SetFrequencyArgs { hz: 14_074_000.0, band: "20m".to_string(), mode: "".to_string() };
        assert!(validate_set_frequency(&a).is_err());
    }

    // Regression coverage for the control-port blocking fix (Codex release audit, 2026-08-19):
    // read_one_control_line is the exact piece run_control_server's accept loop now uses instead
    // of a raw, unbounded read_line -- these prove the two properties that actually matter
    // (bounded wait on a silent connection; unaffected behavior on a normal one) without needing
    // a live Engine/audio stack, same reasoning as reply_wire_response's own extraction above.

    #[test]
    fn read_one_control_line_times_out_instead_of_blocking_forever() {
        use std::net::{TcpListener, TcpStream};
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = listener.local_addr().unwrap();
        let _client = TcpStream::connect(addr).unwrap(); // connects, sends nothing, no newline ever
        let (server_side, _) = listener.accept().unwrap();

        let start = std::time::Instant::now();
        let result = read_one_control_line(&server_side, std::time::Duration::from_millis(200), 8192);
        let elapsed = start.elapsed();

        assert!(result.is_none(), "no newline was ever sent -- must time out, not fabricate a line");
        assert!(
            elapsed < std::time::Duration::from_secs(2),
            "must bound the wait to roughly the requested timeout, not block indefinitely: {elapsed:?}"
        );
    }

    #[test]
    fn read_one_control_line_returns_the_real_line_when_sent_promptly() {
        use std::io::Write as _;
        use std::net::{TcpListener, TcpStream};
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = listener.local_addr().unwrap();
        let mut client = TcpStream::connect(addr).unwrap();
        let (server_side, _) = listener.accept().unwrap();

        client.write_all(b"SNAPSHOT\n").unwrap();
        let result = read_one_control_line(&server_side, std::time::Duration::from_secs(2), 8192);

        assert_eq!(result.as_deref(), Some("SNAPSHOT\n"), "a normal, promptly-sent command line must be read unchanged");
    }

    #[test]
    fn read_one_control_line_gives_up_on_an_oversized_line_instead_of_buffering_forever() {
        use std::io::Write as _;
        use std::net::{TcpListener, TcpStream};
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = listener.local_addr().unwrap();
        let mut client = TcpStream::connect(addr).unwrap();
        let (server_side, _) = listener.accept().unwrap();

        // Well over the bound, and deliberately never newline-terminated -- a real oversized/
        // malformed client wouldn't stop to be polite either.
        let junk = vec![b'x'; 200];
        client.write_all(&junk).unwrap();

        // Bounded to 64 bytes here (not the real 8192) so this test doesn't need to push tens of
        // KB over a loopback socket to prove the same property.
        let result = read_one_control_line(&server_side, std::time::Duration::from_millis(200), 64);

        // Hitting the byte bound without a newline is treated the same as any other "nothing
        // usable" case (None) -- the caller's existing `continue` already handles that safely
        // (see run_control_server: an unrecognized/absent line just drops that connection).
        assert!(result.is_none(), "an oversized line with no newline must give up, not buffer without limit");
    }
}
