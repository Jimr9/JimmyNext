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
    /// operator-facing experiment, not a recommendation, because the automatic PKTUSB path was
    /// confirmed live, 2026-08-07, to leave a real TS-590SG transmitting mic audio instead of
    /// the FT8 tone despite CAT read-back reporting PKTUSB correctly (Kenwood's own "Data mode"
    /// CAT command may not be sufficient by itself to switch its physical audio source the way
    /// Nexus assumes for every rig). Jimmy's own Options > Radio tab is the accessible
    /// equivalent of WSJT-X's Radio tab "Mode" dropdown this maps to.
    plain_ssb_data_modes: bool,
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
    let mut control_port: u16 = 58239;

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
            "--control-port" => {
                if let Some(v) = it.next() {
                    control_port = v.parse().unwrap_or(control_port);
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
        control_port,
    }
}

/// Serves this already-running process's device list to NativeEngineClient.cs over a local-
/// loopback-only TCP control port, for the lifetime of the session -- see `Args::control_port`'s
/// own doc comment for why this exists (making the two-processes-touching-the-sound-card-at-once
/// crash structurally impossible instead of just less likely). One-line text protocol, plain and
/// deliberately minimal: client sends "LIST_DEVICES" or "LIST_OUTPUT_DEVICES" followed by a
/// newline, server writes one device name per line and closes its write side (EOF signals "list
/// complete") -- mirrors the exact line-per-device format `--list-devices`/`--list-output-devices`
/// already print to stdout, so NativeEngineClient.cs's parsing stays identical either way. Runs on
/// its own thread, never on the one `run_radio` blocks; `available_devices()` still goes through
/// Nexus's own `AUDIO_HOST_LOCK`, so a query landing mid-session safely queues behind whatever
/// `run_radio` itself is doing with the audio host rather than racing it.
fn run_control_server(port: u16) {
    use std::io::{BufRead, BufReader, Write};
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
        let mut reader = match stream.try_clone() {
            Ok(s) => BufReader::new(s),
            Err(_) => continue,
        };
        let mut line = String::new();
        if reader.read_line(&mut line).is_err() {
            continue;
        }
        let cmd = line.trim();
        if cmd != "LIST_DEVICES" && cmd != "LIST_OUTPUT_DEVICES" {
            continue;
        }
        let (inputs, outputs) = tempo_audio::device::available_devices();
        let devices = if cmd == "LIST_DEVICES" { inputs } else { outputs };
        for dev in devices {
            let _ = writeln!(stream, "{}", dev.name);
        }
        let _ = stream.shutdown(std::net::Shutdown::Write);
    }
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
    // Own thread, not the one run_radio blocks on: see run_control_server's own doc comment.
    let control_port = args.control_port;
    std::thread::spawn(move || run_control_server(control_port));

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
        ..Settings::default()
    };
    settings.wsjtx_udp = true;
    settings.wsjtx_udp_addr = args.jimmy_addr.clone();

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
