//! Automated regression test (runs under `cargo test`), mirroring Nexus's own
//! `tests/roundtrip.c` pattern (headless encode -> decode round-trip via the native decoder,
//! PASS iff the recovered text matches what was sent) -- the FT8 equivalent of that FT1 test,
//! reusing the real `ft8` crate rather than a hand-rolled decoder. First formalized in Phase 4c
//! as a manual proof-of-concept (main.rs); this is that same check made a permanent, scriptable
//! regression fixture per the self-sufficiency plan's testing notes ("prove the engine host's
//! audio->decode path independently with a small roundtrip-style fixture").

#[test]
fn ft8_encode_decode_roundtrip_recovers_exact_text() {
    let msg = "CQ K1ABC FN20"; // standard-format callsign; see main.rs's own note on why
    let f0 = 1500.0_f32;

    let tones = ft8::encode(msg);
    assert_eq!(tones.len(), ft8::NN, "FT8 must encode to {} tones", ft8::NN);

    let wave = ft8::gen_wave(&tones, ft8::SAMPLE_RATE, f0);
    assert!(!wave.is_empty(), "gen_wave produced no samples");

    let mut iwave = vec![0i16; ft8::NMAX];
    let noff = 6_000usize; // 0.5s FT8 TX start, matching WSJT-X's own convention
    for (i, &s) in wave.iter().enumerate() {
        if noff + i < iwave.len() {
            iwave[noff + i] = (s * 1000.0).clamp(-32768.0, 32767.0) as i16;
        }
    }

    let decodes = ft8::decode_frame(&iwave, 200, 2900, 3, "", "", 0, 0, true, false);
    assert!(
        decodes.iter().any(|d| d.message == msg),
        "native decoder did not recover '{msg}' from its own encoded audio; got: {:?}",
        decodes.iter().map(|d| &d.message).collect::<Vec<_>>()
    );
}

#[test]
fn ft8_decode_frame_finds_nothing_in_pure_silence() {
    // Complements the positive roundtrip above: confirms the decoder doesn't hallucinate
    // decodes from an empty buffer (a false-positive here would be a much worse bug than a
    // missed decode -- see libtempo's own false-alarm test executables, verified in Phase 4b).
    let silence = vec![0i16; ft8::NMAX];
    let decodes = ft8::decode_frame(&silence, 200, 2900, 3, "", "", 0, 0, true, false);
    assert!(decodes.is_empty(), "decoder found {} decode(s) in pure silence", decodes.len());
}
