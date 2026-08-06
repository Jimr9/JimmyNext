//! jimmy-engine-host: a continuous, RECEIVE-ONLY native FT8 engine for Jimmy's
//! DecodeEngineMode.JimmyNative. Captures real audio (tempo_audio::device::CpalBackend),
//! decodes each FT8 period through the real native decoder (the `ft8` crate, wrapping
//! libtempo), and reports every decode to Jimmy over the stock WSJT-X UDP protocol
//! (tempo_net::wsjtx) -- the exact protocol Jimmy's EXISTING, UNMODIFIED
//! CapabilityNegotiator/WsjtxProtocolAdapter code already speaks, proven correct against a real
//! radio in Phase 4c/4d/4e (see those commits and examples/live_decode.rs, examples/qso_bench.rs).
//!
//! Transmit-control wiring is being built in verifiable stages (each bench-tested before the
//! next lands), per an explicit, deliberate safety review with the operator -- this process
//! still does not assert PTT or play TX audio as of the current stage (Stage 1: receive Jimmy's
//! outbound Reply/HaltTx, run the real QSO sequencer, report what WOULD be sent -- see
//! tx_control.rs). No stage before the last plays real audio to an output device or asserts
//! PTT, and the final step -- an actual over-the-air test -- only happens with the operator
//! physically present and watching, at reduced power into a dummy load first.
//!
//! Launched by Jimmy's Controller.cs (same lifecycle pattern as Phase 1's
//! RigctldClient.LaunchBundled/StopBundled: spawned on demand, killed on Jimmy shutdown) when
//! EngineModeCutover.Mode == DecodeEngineMode.JimmyNative. Runs standalone/by hand otherwise
//! (e.g. `cargo run --release -- --mycall KB0UZT --mygrid FN42`) for bench testing.

mod audio;
mod tx_control;
mod tx_schedule;

use std::io::Write;
use std::net::UdpSocket;
use std::sync::mpsc;
use std::time::{Duration, SystemTime};

use tempo_core::qso::Station;
use tempo_net::wsjtx::{encode_decode, encode_heartbeat, encode_status, Decode, Status};

/// `println!`, but flushed immediately. Rust's stdout is only line-buffered when attached to a
/// real terminal; redirected to a file or pipe (a launched-as-child-process Jimmy, or any test
/// harness capturing output) it's fully block-buffered by default, so a caller polling the log
/// for a specific line can wait arbitrarily long for it to actually appear. Every status line
/// this process prints matters for exactly that kind of polling (bench tests, Jimmy watching for
/// trouble), so this is used everywhere instead of a bare `println!`.
macro_rules! log {
    ($($arg:tt)*) => {{
        println!($($arg)*);
        let _ = std::io::stdout().flush();
    }};
}

const ENGINE_ID: &str = "JimmyEngine";
const HEARTBEAT_INTERVAL: Duration = Duration::from_secs(10);
const POLL_INTERVAL: Duration = Duration::from_millis(150);

struct Args {
    mycall: String,
    mygrid: String,
    device: Option<String>,
    jimmy_addr: String,
    dial_freq: u64,
}

fn parse_args() -> Args {
    let mut mycall = "NOCALL".to_string();
    let mut mygrid = "AA00".to_string();
    let mut device = None;
    let mut jimmy_addr = "127.0.0.1:2237".to_string();
    let mut dial_freq: u64 = 14_074_000;

    let mut it = std::env::args().skip(1);
    while let Some(flag) = it.next() {
        match flag.as_str() {
            "--mycall" => mycall = it.next().unwrap_or(mycall),
            "--mygrid" => mygrid = it.next().unwrap_or(mygrid),
            "--device" => device = it.next(),
            "--jimmy-addr" => jimmy_addr = it.next().unwrap_or(jimmy_addr),
            "--dial-freq" => {
                if let Some(v) = it.next() {
                    dial_freq = v.parse().unwrap_or(dial_freq);
                }
            }
            other => eprintln!("jimmy-engine-host: ignoring unrecognized argument '{other}'"),
        }
    }
    Args { mycall, mygrid, device, jimmy_addr, dial_freq }
}

fn main() {
    // Not part of the continuous-service contract -- a quick, side-effect-free query mode so
    // Jimmy's Options dialog can populate an audio-device picker (NativeEngineClient.cs) without
    // needing its own cpal/WASAPI bindings. Prints one device name per line and exits.
    if std::env::args().any(|a| a == "--list-devices") {
        let (inputs, _outputs) = tempo_audio::device::available_devices();
        for name in inputs {
            println!("{name}");
        }
        return;
    }

    let args = parse_args();
    log!(
        "jimmy-engine-host starting: mycall={} mygrid={} device={} -> {} (RECEIVE ONLY -- no PTT, no transmit)",
        args.mycall,
        args.mygrid,
        args.device.as_deref().unwrap_or("<system default>"),
        args.jimmy_addr
    );

    let sock = UdpSocket::bind("127.0.0.1:0").expect("bind ephemeral UDP socket");
    log!(
        "jimmy-engine-host listening on {} (this is where Jimmy's own Reply/HaltTx datagrams \
         land -- Jimmy replies to whatever address a Heartbeat arrived from, so this ephemeral \
         port IS the engine's control address; a bench tool targets it directly)",
        sock.local_addr().expect("local_addr of a just-bound socket")
    );

    let (tx_cmd_tx, tx_cmd_rx) = mpsc::channel::<tx_control::Command>();
    tx_control::spawn_receiver(&sock, tx_cmd_tx)
        .expect("spawn transmit-control receive thread (cloning the UDP socket)");
    let mut current_qso: Option<tx_schedule::ActiveQso> = None;
    // (message text, period decoded in) for recently-sent Decodes -- lets a later Reply look up
    // which period the replied-to signal was actually heard in, which TX parity is computed
    // from (see tx_schedule.rs). Pruned to a handful of periods' worth.
    let mut recent_decodes: Vec<(String, u64)> = Vec::new();

    let mut cap = match &args.device {
        Some(name) => audio::FrameCapture::open_named(name),
        None => audio::FrameCapture::open_default(),
    }
    .unwrap_or_else(|e| {
        eprintln!("FATAL: could not open audio input device: {e}");
        std::process::exit(1);
    });

    let mut last_heartbeat = SystemTime::UNIX_EPOCH;
    let (current_period, _) = audio::period_position();
    let mut next_decode_period = current_period + 1;

    loop {
        // Drain every pending transmit-control command before anything else this tick --
        // HaltTx in particular must never wait behind audio-capture work. Stage 1: no real
        // transmit yet, so "acting on" a command means updating current_qso and reporting what
        // WOULD happen -- see tx_control.rs's own header comment for the staged build-out.
        while let Ok(cmd) = tx_cmd_rx.try_recv() {
            match cmd {
                tx_control::Command::Reply { dxcall, msg, snr, raw_text } => {
                    let station = Station::start(
                        &args.mycall,
                        &args.mygrid,
                        &dxcall,
                        Some((&msg, snr)),
                        false,
                        false,
                    );
                    // Which period was the replied-to signal actually decoded in? Needed for TX
                    // parity (tx_schedule::tx_parity_for_decoded_period). Matched by exact
                    // message text against recently-sent Decodes; a Reply to something decoded
                    // before this process started (or long enough ago to be pruned) has no
                    // match -- fall back to the opposite of the CURRENT period rather than
                    // guessing wrong silently.
                    let (current_period, _) = audio::period_position();
                    let decoded_period = recent_decodes
                        .iter()
                        .rev()
                        .find(|(text, _)| text == &raw_text)
                        .map(|(_, p)| *p)
                        .unwrap_or_else(|| {
                            log!(
                                "[TX_CONTROL] Reply message not found in recent-decode history \
                                 -- falling back to current period for parity"
                            );
                            current_period
                        });
                    let qso = tx_schedule::ActiveQso::new(station, decoded_period);
                    log!(
                        "[TX_CONTROL] Reply -> working {dxcall}: state={:?} pending='{}' tx_parity={} \
                         (decoded in period {decoded_period}) (Stage 2: NOT actually transmitted)",
                        qso.station.state,
                        qso.station.pending_text().unwrap_or_default(),
                        qso.tx_parity
                    );
                    current_qso = Some(qso);
                }
                tx_control::Command::HaltTx { auto_only } => {
                    log!("[TX_CONTROL] HaltTx (auto_only={auto_only}) -- clearing current QSO");
                    current_qso = None;
                }
            }
        }

        cap.poll();

        let now = SystemTime::now();
        if now.duration_since(last_heartbeat).unwrap_or(Duration::MAX) >= HEARTBEAT_INTERVAL {
            let hb = encode_heartbeat(ENGINE_ID, 3, env!("CARGO_PKG_VERSION"), "jimmy-native");
            let _ = sock.send_to(&hb, &args.jimmy_addr);

            let status = Status {
                dial_freq: args.dial_freq,
                mode: "FT8",
                tr_period: 15,
                decoding: true,
                de_call: &args.mycall,
                de_grid: &args.mygrid,
                ..Default::default()
            };
            let st = encode_status(ENGINE_ID, &status);
            let _ = sock.send_to(&st, &args.jimmy_addr);
            last_heartbeat = now;
        }

        let boundary = audio::period_boundary_time(next_decode_period);
        if boundary <= now {
            if let Some(frame) = cap.frame_for_period(boundary) {
                let decodes = ft8::decode_frame(&frame, 200, 2900, 3, &args.mycall, "", 0, 0, true, false);
                for d in &decodes {
                    let wire = Decode {
                        new: true,
                        time_ms: 0,
                        snr: d.snr,
                        delta_time: d.dt as f64,
                        delta_freq: d.freq as u32,
                        mode: "~",
                        message: &d.message,
                        low_confidence: d.qual < 0.5,
                        off_air: false,
                    };
                    let bytes = encode_decode(ENGINE_ID, &wire);
                    let _ = sock.send_to(&bytes, &args.jimmy_addr);
                    recent_decodes.push((d.message.clone(), next_decode_period));
                }
                if !decodes.is_empty() {
                    log!(
                        "period {next_decode_period}: {} real decode(s) sent to Jimmy",
                        decodes.len()
                    );
                }
                // Prune decode history older than a handful of periods -- a Reply arrives
                // within seconds of the operator seeing the decode, never minutes later.
                recent_decodes.retain(|(_, p)| next_decode_period.saturating_sub(*p) <= 4);
                next_decode_period += 1;
            }
        }

        // Stage 2 dry run: if the active QSO owes a transmission for the CURRENT period, report
        // exactly what would be sent and when -- generates the real audio via the real native
        // encoder (proving the content is valid) but never plays it to a device or asserts PTT.
        if let Some(qso) = &mut current_qso {
            let (current_period, pos_in_period) = audio::period_position();
            if qso.owes_tx_for(current_period) {
                if let Some(text) = qso.station.pending_text() {
                    let tones = ft8::encode(&text);
                    let target_start = audio::period_boundary_time(current_period);
                    log!(
                        "[TX_SCHEDULE] period {current_period} (parity {}): WOULD transmit '{text}' \
                         ({} tones, {:.2}s duration) starting at period boundary {target_start:?} \
                         -- {:.2}s into this period right now -- NOT actually played, no PTT asserted",
                        qso.tx_parity,
                        tones.len(),
                        tones.len() as f64 * (ft8::NZ as f64 / ft8::NN as f64) / ft8::SAMPLE_RATE as f64,
                        pos_in_period
                    );
                    qso.last_tx_period = Some(current_period);
                    qso.station.after_tx();
                }
            }
        }

        std::thread::sleep(POLL_INTERVAL);
    }
}
