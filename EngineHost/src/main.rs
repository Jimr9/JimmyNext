//! jimmy-engine-host: a continuous, RECEIVE-ONLY native FT8 engine for Jimmy's
//! DecodeEngineMode.JimmyNative. Captures real audio (tempo_audio::device::CpalBackend),
//! decodes each FT8 period through the real native decoder (the `ft8` crate, wrapping
//! libtempo), and reports every decode to Jimmy over the stock WSJT-X UDP protocol
//! (tempo_net::wsjtx) -- the exact protocol Jimmy's EXISTING, UNMODIFIED
//! CapabilityNegotiator/WsjtxProtocolAdapter code already speaks, proven correct against a real
//! radio in Phase 4c/4d/4e (see those commits and examples/live_decode.rs, examples/qso_bench.rs).
//!
//! RECEIVE ONLY, deliberately: this process never asserts PTT and never transmits. It does not
//! yet listen for or act on Jimmy's outbound Reply/HaltTx/EnableTx messages -- an operator using
//! JimmyNative today gets real native decodes of off-air traffic into Jimmy's own accessible
//! queue/classification UI, but "Reply" doesn't yet do anything, because there is no live
//! transmit backend wired to respond to it. Wiring transmit is a separate, safety-reviewed step
//! (see the self-sufficiency plan's Phase 4 notes on why PTT/TX wiring is deferred).
//!
//! Launched by Jimmy's Controller.cs (same lifecycle pattern as Phase 1's
//! RigctldClient.LaunchBundled/StopBundled: spawned on demand, killed on Jimmy shutdown) when
//! EngineModeCutover.Mode == DecodeEngineMode.JimmyNative. Runs standalone/by hand otherwise
//! (e.g. `cargo run --release -- --mycall KB0UZT --mygrid FN42`) for bench testing.

mod audio;

use std::net::UdpSocket;
use std::time::{Duration, SystemTime};

use tempo_net::wsjtx::{encode_decode, encode_heartbeat, encode_status, Decode, Status};

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
    println!(
        "jimmy-engine-host starting: mycall={} mygrid={} device={} -> {} (RECEIVE ONLY -- no PTT, no transmit)",
        args.mycall,
        args.mygrid,
        args.device.as_deref().unwrap_or("<system default>"),
        args.jimmy_addr
    );

    let sock = UdpSocket::bind("127.0.0.1:0").expect("bind ephemeral UDP socket");

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
                }
                if !decodes.is_empty() {
                    println!(
                        "period {next_decode_period}: {} real decode(s) sent to Jimmy",
                        decodes.len()
                    );
                }
                next_decode_period += 1;
            }
        }

        std::thread::sleep(POLL_INTERVAL);
    }
}
