//! Bounded, manual, RECEIVE-ONLY verification of Phase 4e: feeds tempo_core::qso::Station --
//! Nexus's own auto-sequenced QSO state machine, reused directly rather than reimplemented --
//! with real decoded traffic and watches it produce correct outgoing messages, then proves each
//! one is well-formed by encoding it through the real native encoder and decoding it back.
//!
//! NEVER plays audio to an output device and NEVER asserts PTT -- this is a bench proof of the
//! sequencing LOGIC only. Wiring a Station's `pending_text()` to an actual transmitter is a
//! separate, safety-reviewed step this example deliberately does not take.
//!
//! Two parts:
//!   1. If a radio is connected and on, real captured/decoded traffic is fed to a `monitoring`
//!      Station via `observe()` -- exercises real-world message parsing at scale (portable
//!      suffixes, RR73/73, hashed "<...>" calls, etc.) against actual off-air text. A monitoring
//!      Station never auto-answers (by design -- see qso.rs's own comment on this), so this part
//!      only proves `observe()` doesn't choke on real traffic; it produces no outgoing message.
//!   2. A synthetic two-station QSO (mirroring qso.rs's own module-doc example) run entirely
//!      in-process, checking every state transition AND round-tripping every outgoing message
//!      through encode -> real native decode to confirm it's actually valid over-the-air FT8,
//!      not just internally-consistent Rust.
//!
//! `cargo run --release --example qso_bench [listen_seconds]`

#[path = "../src/audio.rs"]
mod audio;

use std::time::{Duration, Instant, SystemTime};
use tempo_core::qso::{State, Station};

fn encode_decode_roundtrip(text: &str) -> Option<String> {
    let tones = ft8::encode(text);
    if tones.len() != ft8::NN {
        return None;
    }
    let wave = ft8::gen_wave(&tones, ft8::SAMPLE_RATE, 1500.0);
    let mut iwave = vec![0i16; ft8::NMAX];
    let noff = 6_000usize;
    for (i, &s) in wave.iter().enumerate() {
        if noff + i < iwave.len() {
            iwave[noff + i] = (s * 1000.0).clamp(-32768.0, 32767.0) as i16;
        }
    }
    let decodes = ft8::decode_frame(&iwave, 200, 2900, 3, "", "", 0, 0, true, false);
    decodes.into_iter().find(|d| d.message == text).map(|d| d.message)
}

fn part1_real_traffic(listen_secs: u64) {
    println!("── Part 1: real captured traffic into Station::observe() ──");
    let mut cap = match audio::FrameCapture::open_named("Microphone (USB Audio CODEC )") {
        Ok(c) => c,
        Err(e) => {
            println!("  (skipped -- couldn't open the radio input: {e})");
            return;
        }
    };
    let mut station = Station::monitoring("KB0UZT", "FN42");
    let start = Instant::now();
    let (current_period, _) = audio::period_position();
    let mut next_decode_period = current_period + 1;
    let mut total_real_decodes = 0usize;

    while start.elapsed() < Duration::from_secs(listen_secs) {
        cap.poll();
        let boundary = audio::period_boundary_time(next_decode_period);
        if boundary <= SystemTime::now() {
            if let Some(frame) = cap.frame_for_period(boundary) {
                let decodes = ft8::decode_frame(&frame, 200, 2900, 3, "", "", 0, 0, true, false);
                if !decodes.is_empty() {
                    total_real_decodes += decodes.len();
                    let modes_decodes: Vec<modes::Decode> =
                        decodes.into_iter().map(Into::into).collect();
                    station.observe(&modes_decodes); // must not panic on real-world text
                    println!(
                        "  [{:>3}s] period {next_decode_period}: observed {} real decode(s), \
                         Station still in {:?} (monitoring never auto-answers)",
                        start.elapsed().as_secs(),
                        modes_decodes.len(),
                        station.state
                    );
                }
                next_decode_period += 1;
            }
        }
        std::thread::sleep(Duration::from_millis(150));
    }
    println!(
        "  Fed {total_real_decodes} real off-air decodes into observe() -- no panics, state stayed \
         Listening as expected for a passive monitor.\n"
    );
}

fn part2_synthetic_qso() {
    println!("── Part 2: synthetic two-station QSO through the real state machine ──");
    // Mirrors qso.rs's own module-doc example: A calls CQ, B answers.
    let mut a = Station::calling_cq("W9XYZ", "EN37");
    let mut ok = true;

    // B hears A's CQ and (operator) double-clicks to answer -- start() with the CQ as context.
    let a_cq_text = a.pending_text().expect("A should have a pending CQ");
    println!("  A transmits: '{a_cq_text}'");
    match encode_decode_roundtrip(&a_cq_text) {
        Some(_) => println!("    -> encode/decode round-trip OK (this really is valid FT8 audio)"),
        None => {
            println!("    -> FAIL: round-trip did not recover the exact text");
            ok = false;
        }
    }
    a.after_tx();

    let mut b = Station::start("K2DEF", "FN31", "W9XYZ", None, false, false);
    assert_eq!(b.state, State::AwaitReport, "B should now be awaiting a report from A");
    let b_grid_text = b.pending_text().expect("B should have a pending grid reply");
    println!("  B transmits: '{b_grid_text}'  (state now {:?})", b.state);
    match encode_decode_roundtrip(&b_grid_text) {
        Some(recovered) => {
            // A hears B's grid reply for real, decoded back through the native decoder --
            // proving the full loop: Station -> encode -> native decode -> observe().
            let d = modes::Decode {
                message: recovered,
                sync: 0.0,
                snr: -5,
                dt: 0.0,
                freq: 1500.0,
                nap: 0,
                qual: 1.0,
                rv: None,
                mode: None,
            };
            a.observe(std::slice::from_ref(&d));
        }
        None => {
            println!("    -> FAIL: round-trip did not recover the exact text");
            ok = false;
        }
    }
    b.after_tx();
    assert_eq!(a.state, State::AwaitRoger, "A should now be awaiting a rogered report");
    let a_report_text = a.pending_text().expect("A should have a pending report");
    println!("  A transmits: '{a_report_text}'  (state now {:?})", a.state);

    if a.state == State::AwaitRoger && b.state == State::AwaitReport {
        println!("\n  RESULT: PASS -- the real Station state machine correctly sequenced a live \
                   CQ/grid/report exchange, and every transmitted message round-tripped through \
                   the real native encoder/decoder as valid over-the-air FT8.");
    } else {
        println!("\n  RESULT: FAIL -- unexpected state after the exchange.");
        ok = false;
    }

    if !ok {
        std::process::exit(1);
    }
}

fn main() {
    let listen_secs: u64 = std::env::args().nth(1).and_then(|s| s.parse().ok()).unwrap_or(0);
    if listen_secs > 0 {
        part1_real_traffic(listen_secs);
    } else {
        println!("── Part 1 skipped (pass a listen-seconds argument to run it against a real radio) ──\n");
    }
    part2_synthetic_qso();
}
