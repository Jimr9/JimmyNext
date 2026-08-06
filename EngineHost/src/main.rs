//! Proof-of-concept engine host for Jimmy's self-sufficiency plan, Phase 4.
//!
//! Proves the single riskiest architectural bet in the plan: that a Rust-built native FT8
//! engine, reusing the open-source Nexus project's own libtempo native decoder (via its real
//! `ft8` crate -- originally worked around via a local ft8_ffi.rs port when tempo-fast-sys's
//! build.rs was still broken in this environment; that's fixed now, see
//! vendor/tempo-fast-sys-patched/, so this uses the real crate directly) and tempo-net crate,
//! can emit real WSJT-X UDP protocol datagrams that Jimmy's EXISTING, UNMODIFIED
//! protocol-handling code correctly receives and displays -- with zero Jimmy-side changes needed
//! for this half of the architecture. Synthesizes a test FT8 signal, decodes it back through the
//! real native decoder (not a canned string), and sends the recovered decode to Jimmy exactly
//! the way a real WSJT-X-family process would.
//!
//! Real audio capture (src/audio.rs, tempo-audio) and QSO sequencing (tempo-core's Station,
//! examples/qso_bench.rs) are proven separately, live -- see those files' own header comments.

use std::net::UdpSocket;
use std::time::Duration;
use tempo_net::wsjtx::{encode_decode, encode_heartbeat, encode_status, Decode, Status};

const JIMMY_ADDR: &str = "127.0.0.1:2237";
const ENGINE_ID: &str = "JimmyEngine";

fn main() {
    let sock = UdpSocket::bind("127.0.0.1:0").expect("bind ephemeral UDP socket");
    sock.set_read_timeout(Some(Duration::from_millis(100))).ok();

    println!("jimmy-engine-host proof of concept -- sending to {JIMMY_ADDR} as '{ENGINE_ID}'");

    // 1. Heartbeat -- lets Jimmy's CapabilityNegotiator begin negotiating.
    let hb = encode_heartbeat(ENGINE_ID, 3, "0.1.0", "poc");
    sock.send_to(&hb, JIMMY_ADDR).expect("send heartbeat");
    println!("sent Heartbeat");

    // 2. Status -- de_call/de_grid populate Jimmy's myCall/myGrid (WsjtxClient.cs:1358-1361),
    // reaching ACTIVE state the same way the replay-test harness's first StatusMessage does.
    let status = Status {
        dial_freq: 14_074_000,
        mode: "FT8",
        tr_period: 15,
        decoding: true,
        de_call: "KB0UZT",
        de_grid: "FN42",
        ..Default::default()
    };
    let st = encode_status(ENGINE_ID, &status);
    sock.send_to(&st, JIMMY_ADDR).expect("send status");
    println!("sent Status (myCall=KB0UZT myGrid=FN42, 20m FT8)");

    std::thread::sleep(Duration::from_millis(500));

    // 3. Synthesize a real FT8 signal from a fake test station -- same pattern as ft8's own
    // encode/decode roundtrip unit test, not a canned string. This proves the actual native
    // decode path (encode -> waveform -> place in a 15s frame -> decode_frame) end to end.
    // K1ABC fits FT8's standard compact callsign packing (letter/digit prefix, digit, up to
    // 3-letter suffix); W1TEST doesn't (4-letter suffix), which is why an earlier run of this
    // exact test correctly decoded down to 'CQ W1TEST' but silently dropped the grid -- that's
    // FT8's real non-standard-callsign message type (no grid slot), not a decode failure.
    let msg = "CQ K1ABC FN20";
    let f0 = 1500.0_f32;
    let tones = ft8::encode(msg);
    assert_eq!(tones.len(), ft8::NN, "FT8 must encode to 79 tones");
    let wave = ft8::gen_wave(&tones, ft8::SAMPLE_RATE, f0);

    let mut iwave = vec![0i16; ft8::NMAX];
    let noff = 6_000usize; // 0.5s FT8 TX start, matching WSJT-X's own convention
    for (i, &s) in wave.iter().enumerate() {
        if noff + i < iwave.len() {
            iwave[noff + i] = (s * 1000.0).clamp(-32768.0, 32767.0) as i16;
        }
    }

    println!("synthesized '{msg}' at {f0} Hz, decoding through the real native decoder...");
    let decodes = ft8::decode_frame(&iwave, 200, 2900, 3, "", "", 0, 0, true, false);
    println!("ft8::decode_frame returned {} decode(s)", decodes.len());

    for d in &decodes {
        println!(
            "  decode: snr={} freq={:.0}Hz dt={:.2}s msg='{}'",
            d.snr, d.freq, d.dt, d.message
        );
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
        sock.send_to(&bytes, JIMMY_ADDR).expect("send decode");
        println!("  -> sent Decode datagram to Jimmy ({} bytes)", bytes.len());
    }

    if decodes.iter().any(|d| d.message == msg) {
        println!("\nRESULT: PASS -- native decoder recovered '{msg}' and it was sent to Jimmy.");
    } else {
        println!("\nRESULT: FAIL -- native decoder did not recover the synthesized message.");
        std::process::exit(1);
    }
}
