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
    }
}

fn main() {
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
         rig=model{} conn={} port={} baud={} ptt={} rigctldPort={} (Phase 5: driven by Nexus's \
         own real run_radio loop -- Engine handles decode/TX/QSO/radio-control, this process \
         just configures and starts it)",
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
    );

    if args.mycall == "NOCALL" || args.mygrid == "AA00" {
        eprintln!("FATAL: --mycall and --mygrid are required (got mycall={:?} mygrid={:?})", args.mycall, args.mygrid);
        std::process::exit(1);
    }

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
    match run_radio(engine, cfg) {
        Ok(()) => log!("jimmy-engine-host: run_radio exited normally"),
        Err(e) => {
            eprintln!("FATAL: run_radio failed: {e}");
            std::process::exit(1);
        }
    }
}
