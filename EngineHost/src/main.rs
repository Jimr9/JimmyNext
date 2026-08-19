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

mod external_data;
mod live_feeds;

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
    /// Command RFPOWER to this fraction (0.0-1.0) exactly once, right after startup -- Jimmy's
    /// own workaround for the Hamlib Kenwood-backend bug (see RadioSettings.StartupPowerEnabled's
    /// doc comment): the engine's own routine PWR/SWR telemetry polling is what trips a fresh
    /// rigctld.exe's first RFPOWER interaction into a destructive calibration sweep, so this
    /// deliberately fires first, before that polling loop's first pass. `None` (the default, when
    /// the operator has not opted in) means do nothing, matching today's behavior exactly.
    startup_power_frac: Option<f32>,
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
    let mut startup_power_frac: Option<f32> = None;
    let mut decode_depth: Option<u8> = None;
    let mut decode_flow_hz: Option<u32> = None;
    let mut decode_fhigh_hz: Option<u32> = None;
    let mut ap_decode: Option<bool> = None;
    let mut ap_cq_only: Option<bool> = None;
    let mut single_decode: Option<bool> = None;
    let mut dx_cluster_addr: Option<String> = None;

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
            "--startup-power-frac" => {
                if let Some(v) = it.next() {
                    startup_power_frac = v.parse().ok();
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
        startup_power_frac,
        decode_depth,
        decode_flow_hz,
        decode_fhigh_hz,
        ap_decode,
        ap_cq_only,
        single_decode,
        dx_cluster_addr,
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
///   SET_TIER FT8|FT4                    -- calls Engine::set_tier(Tier). Responds "OK" or
///                                          "ERR <message>". Jimmy's own Alt+M (Toggle Mode)
///                                          hotkey -- under classic WSJT-X/UDP mode this always
///                                          just halted Tx and left the operator to switch modes
///                                          in WSJT-X's own UI (no outbound mode-change command
///                                          exists in WSJT-X's UDP API at all); direct-engine mode
///                                          has no separate UI to fall back to, so it needs this
///                                          instead. set_tier already existed in Engine; this just
///                                          exposes it.
///   SET_DECODE_DEPTH 1|2|3               -- calls Engine::set_decode_depth(u8) (WSJT-X's
///                                          "Fast/Normal/Deep"). Responds "OK" or "ERR <message>".
///                                          Live, mid-session -- unlike the rest of Jimmy's Decode
///                                          tab (F Low/F High/Enable AP/AP-CQ-only/Single decode),
///                                          which have no live setter on Engine and are configured
///                                          via CLI args at startup only (--decode-flow-hz etc.,
///                                          see Args' own doc comments).
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
/// both a read timeout and a maximum line length. Found live (Codex release audit, 2026-08-19):
/// `run_control_server` below is single-threaded and fully serial by design -- every command
/// waits for the one ahead of it to finish, HALT_TX included. Before this, receiving that FIRST
/// line had no timeout and no length bound at all (`TcpStream::read_line` can block
/// indefinitely), so a client that connected and never sent a newline -- or never sent a complete
/// line -- stalled this thread forever, blocking every later command behind it with no way to
/// recover short of restarting the process. Returns None on any failure: couldn't set the
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

fn run_control_server(
    port: u16,
    engine: Arc<Mutex<Engine>>,
    external_cache: Arc<external_data::SharedCache>,
    live_feeds_cache: Arc<live_feeds::LiveFeedsCache>,
) {
    use std::io::Write;
    use std::net::TcpListener;

    let listener = match TcpListener::bind(("127.0.0.1", port)) {
        Ok(l) => l,
        Err(e) => {
            eprintln!("jimmy-engine-host: control server failed to bind 127.0.0.1:{port}: {e}");
            return;
        }
    };
    for incoming in listener.incoming() {
        let mut stream = match incoming {
            Ok(s) => s,
            Err(_) => continue,
        };
        let line = match read_one_control_line(&stream, CONTROL_READ_TIMEOUT, MAX_CONTROL_LINE_BYTES) {
            Some(l) => l,
            None => continue,
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
            let snap = engine.lock().unwrap_or_else(|e| e.into_inner()).snapshot();
            match serde_json::to_string(&snap) {
                Ok(json) => {
                    let _ = writeln!(stream, "{json}");
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
                    // a real one on Jimmy Test's side. Propagate the real outcome instead --
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
            // back to, so it needs a real command instead. Engine::set_tier already exists and
            // already safely halts any in-flight over across a tier switch (see its own doc
            // comment) -- this just exposes it, matching every other command above.
            match v.trim() {
                "FT8" => {
                    engine.lock().unwrap_or_else(|e| e.into_inner()).set_tier(tempo_app::dto::Tier::Ft8);
                    let _ = writeln!(stream, "OK");
                }
                "FT4" => {
                    engine.lock().unwrap_or_else(|e| e.into_inner()).set_tier(tempo_app::dto::Tier::Ft4);
                    let _ = writeln!(stream, "OK");
                }
                other => {
                    let _ = writeln!(stream, "ERR bad SET_TIER value: {other}");
                }
            }
        } else if let Some(v) = line.strip_prefix("SET_TUNING ") {
            engine.lock().unwrap_or_else(|e| e.into_inner()).set_tune(v.trim() == "1");
            let _ = writeln!(stream, "OK");
        } else if let Some(v) = line.strip_prefix("SET_TX_OFFSET ") {
            match v.trim().parse::<f32>() {
                Ok(hz) => {
                    engine.lock().unwrap_or_else(|e| e.into_inner()).set_tx_offset(hz);
                    let _ = writeln!(stream, "OK");
                }
                Err(e) => {
                    let _ = writeln!(stream, "ERR bad SET_TX_OFFSET value: {e}");
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
        } else if let Some(json) = line.strip_prefix("EQSL_UPLOAD ") {
            // Credential-bearing, real network I/O (eQSL can take up to ~60s -- it builds the
            // file server-side). MUST NOT run inline: this accept loop is single-threaded, and
            // Jimmy Test polls SNAPSHOT every ~1s -- blocking it here for a minute would read as
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
            continue;
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
            continue;
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
            continue;
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
            continue;
        } else {
            let _ = writeln!(stream, "ERR unknown command");
        }
        let _ = stream.shutdown(std::net::Shutdown::Write);
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
        ..Settings::default()
    };
    settings.wsjtx_udp = true;
    settings.wsjtx_udp_addr = args.jimmy_addr.clone();

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

    let engine = Arc::new(Mutex::new(Engine::with_settings(settings)));

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

    // Startup-only power workaround (RadioSettings.StartupPowerEnabled's doc comment has the
    // full Hamlib #1595 story). Deliberately called here, before run_radio's first tick, so
    // Engine already has a desired rf_power queued up the moment the radio loop starts polling
    // -- set_rf_power only records the desired value (clamped to the mode's power ceiling); the
    // radio loop is what actually sends it to the rig, once, on its first pass. No further calls
    // after this: the operator's own manual rig-knob changes afterward are left alone.
    if let Some(frac) = args.startup_power_frac {
        engine.lock().unwrap().set_rf_power(frac);
    }

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

    // Own thread, not the one run_radio blocks on: see run_control_server's own doc comment.
    // Spawned here (after engine exists), not earlier alongside control_port's own read above,
    // specifically so it can share this same Arc<Mutex<Engine>> -- the whole point of the
    // SNAPSHOT/REPLY/HALT_TX/SET_TX_ENABLED commands added to it.
    {
        let control_engine = engine.clone();
        let control_cache = external_cache.clone();
        let control_live_feeds = live_feeds_cache.clone();
        std::thread::spawn(move || {
            run_control_server(control_port, control_engine, control_cache, control_live_feeds)
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
    let radio_handle = std::thread::spawn(move || {
        std::panic::catch_unwind(std::panic::AssertUnwindSafe(move || run_radio(engine, cfg)))
    });
    match radio_handle.join() {
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

#[cfg(test)]
mod tests {
    use super::*;

    // Regression coverage for the REPLY handler fix: previously call_station_ctx's Result was
    // discarded and "OK" was written unconditionally, so a refusal (Engine's own "No recent
    // decode from <call>" -- fired when reply_msg is Some but no decode context can be found,
    // and guaranteed to leave engine state untouched) was indistinguishable from a real success
    // on Jimmy Test's side. These two cases are exactly what reply_wire_response now decides.

    #[test]
    fn reply_wire_response_ok_reports_ok() {
        assert_eq!(reply_wire_response(Ok(())), "OK");
    }

    #[test]
    fn reply_wire_response_err_reports_err_with_the_real_message() {
        let msg = "No recent decode from W1AW -- wait for their next transmission, then click again.";
        assert_eq!(reply_wire_response(Err(msg.to_string())), format!("ERR {msg}"));
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
