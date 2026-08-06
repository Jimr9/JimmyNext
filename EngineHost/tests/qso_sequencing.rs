//! Automated regression test (runs under `cargo test`) for Phase 4e's QSO sequencing: a
//! synthetic two-station CQ/grid/report exchange run through the real tempo_core::qso::Station
//! state machine, with every outgoing message round-tripped through the real native
//! encoder/decoder to confirm it's genuinely valid over-the-air FT8, not just
//! internally-consistent Rust. First formalized as a manual proof-of-concept
//! (examples/qso_bench.rs); this is that same check made a permanent, scriptable regression
//! fixture. Station never asserts PTT and this test never touches an audio device -- pure logic.

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

#[test]
fn cq_grid_report_exchange_sequences_correctly_and_every_message_is_valid_ft8() {
    // Mirrors qso.rs's own module-doc example: A calls CQ, B answers.
    let mut a = Station::calling_cq("W9XYZ", "EN37");

    let a_cq_text = a.pending_text().expect("A should have a pending CQ");
    assert_eq!(a_cq_text, "CQ W9XYZ EN37");
    assert_eq!(
        encode_decode_roundtrip(&a_cq_text).as_deref(),
        Some(a_cq_text.as_str()),
        "A's CQ did not round-trip through the real native encoder/decoder"
    );
    a.after_tx();

    // B hears A's CQ and (operator) double-clicks to answer -- start() with the CQ as context.
    let mut b = Station::start("K2DEF", "FN31", "W9XYZ", None, false, false);
    assert_eq!(b.state, State::AwaitReport, "B should now be awaiting a report from A");

    let b_grid_text = b.pending_text().expect("B should have a pending grid reply");
    assert_eq!(b_grid_text, "W9XYZ K2DEF FN31");
    let recovered = encode_decode_roundtrip(&b_grid_text)
        .expect("B's grid reply did not round-trip through the real native encoder/decoder");

    // A hears B's grid reply for real, decoded back through the native decoder -- proving the
    // full loop: Station -> encode -> native decode -> observe().
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
    b.after_tx();

    assert_eq!(a.state, State::AwaitRoger, "A should now be awaiting a rogered report");
    let a_report_text = a.pending_text().expect("A should have a pending report");
    assert!(
        a_report_text.starts_with("K2DEF W9XYZ"),
        "A's report should address K2DEF: '{a_report_text}'"
    );
    assert!(
        encode_decode_roundtrip(&a_report_text).is_some(),
        "A's report did not round-trip through the real native encoder/decoder"
    );

    // B's own state is unaffected by A's not-yet-observed report -- still AwaitReport.
    assert_eq!(b.state, State::AwaitReport);
}
