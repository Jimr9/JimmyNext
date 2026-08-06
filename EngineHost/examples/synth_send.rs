//! Phase 4h: sends Jimmy a Decode datagram sourced from a REAL encode -> real native decode
//! round-trip of a known message, rather than a hand-crafted WSJT-X UDP datagram (which is what
//! JimmyReplay.py's existing 19 groups use, by necessity, since the audio->decode path itself
//! didn't exist before Phase 4). Deterministic (a known synthesized message always decodes back
//! to itself -- confirmed by tests/roundtrip.rs) and needs no live radio, unlike
//! examples/live_decode.rs, so this is what a replay-test-style fixture can build on for
//! asserting Jimmy's reaction to a GENUINELY native-decoded message.
//!
//! `cargo run --release --example synth_send -- "CQ G3HRC IO91" [jimmy_addr]`

use std::net::UdpSocket;
use std::time::Duration;
use tempo_net::wsjtx::{encode_decode, encode_heartbeat, encode_status, Decode, Status};

const ENGINE_ID: &str = "JimmyEngine";

fn main() {
    let msg = std::env::args().nth(1).unwrap_or_else(|| "CQ G3HRC IO91".to_string());
    let jimmy_addr = std::env::args().nth(2).unwrap_or_else(|| "127.0.0.1:2237".to_string());
    let f0 = 1500.0_f32;

    let tones = ft8::encode(&msg);
    if tones.len() != ft8::NN {
        eprintln!("FAIL: '{msg}' did not encode to {} tones (got {})", ft8::NN, tones.len());
        std::process::exit(1);
    }
    let wave = ft8::gen_wave(&tones, ft8::SAMPLE_RATE, f0);
    let mut iwave = vec![0i16; ft8::NMAX];
    let noff = 6_000usize;
    for (i, &s) in wave.iter().enumerate() {
        if noff + i < iwave.len() {
            iwave[noff + i] = (s * 1000.0).clamp(-32768.0, 32767.0) as i16;
        }
    }

    let decodes = ft8::decode_frame(&iwave, 200, 2900, 3, "", "", 0, 0, true, false);
    let Some(d) = decodes.into_iter().find(|d| d.message == msg) else {
        eprintln!("FAIL: native decoder did not recover '{msg}' from its own encoded audio");
        std::process::exit(1);
    };
    println!("Native decoder confirmed '{msg}' (snr={} dt={:.2} freq={:.0}Hz) -- sending to Jimmy at {jimmy_addr}", d.snr, d.dt, d.freq);

    let sock = UdpSocket::bind("127.0.0.1:0").expect("bind ephemeral UDP socket");
    let hb = encode_heartbeat(ENGINE_ID, 3, env!("CARGO_PKG_VERSION"), "jimmy-native-synth");
    sock.send_to(&hb, &jimmy_addr).expect("send heartbeat");

    let status = Status {
        dial_freq: 14_074_000,
        mode: "FT8",
        tr_period: 15,
        decoding: true,
        de_call: "KB0UZT",
        de_grid: "FN42",
        ..Default::default()
    };
    sock.send_to(&encode_status(ENGINE_ID, &status), &jimmy_addr).expect("send status");
    std::thread::sleep(Duration::from_millis(300));

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
    sock.send_to(&bytes, &jimmy_addr).expect("send decode");
    println!("Sent genuinely native-decoded Decode datagram ({} bytes) to Jimmy.", bytes.len());
}
