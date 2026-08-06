//! Bounded, manual verification of Phase 4f: POTA/SOTA activator spots and PSK Reporter
//! self-spotting, reused from Nexus's propagation and tempo-net crates rather than
//! reimplemented.
//!
//! POTA/SOTA fetch is READ-ONLY (a GET against a public spot feed -- the same as browsing the
//! POTA/SOTA website) and is run for real here. PSK Reporter's `send_spots()` publishes to a
//! real, public, third-party database, which is a real-world action on shared infrastructure --
//! this only calls `build_datagram()` (pure, no network) to prove the encoding is correct,
//! and deliberately stops short of actually sending it. Wiring a real send is a separate step
//! that needs the operator's explicit go-ahead, same reasoning as PTT above.
//!
//! `cargo run --release --example spots_bench`

use propagation::live::pota::{fetch_pota_spots, fetch_sota_spots};
use tempo_net::pskreporter::{PskReporter, Spot};

fn main() {
    println!("── POTA activator spots (live, read-only) ──");
    match fetch_pota_spots() {
        Ok(spots) => {
            println!("  {} POTA spot(s) currently active:", spots.len());
            for s in spots.iter().take(8) {
                println!(
                    "    {:<10} {:<10} {:>9.1}kHz {:<6} {}",
                    s.activator, s.reference, s.freq_khz, s.mode, s.name
                );
            }
            if spots.len() > 8 {
                println!("    ... and {} more", spots.len() - 8);
            }
        }
        Err(e) => println!("  FAIL: {e}"),
    }

    println!("\n── SOTA spots (live, read-only) ──");
    match fetch_sota_spots(10) {
        Ok(spots) => {
            println!("  {} SOTA spot(s):", spots.len());
            for s in spots.iter().take(8) {
                println!(
                    "    {:<10} {:<14} {:>9.1}kHz {:<6}",
                    s.activator, s.reference, s.freq_khz, s.mode
                );
            }
        }
        Err(e) => println!("  FAIL: {e}"),
    }

    println!("\n── PSK Reporter self-spot datagram (build only -- NOT sent) ──");
    let reporter = PskReporter::new();
    let spots = vec![
        Spot { call: "K1ABC".into(), freq_hz: 14_074_000 + 1500, snr: -12, mode: "FT8".into(), time_secs: 1_700_000_000 },
        Spot { call: "W9XYZ".into(), freq_hz: 14_074_000 + 850, snr: 3, mode: "FT8".into(), time_secs: 1_700_000_000 },
    ];
    match reporter.build_datagram("KB0UZT", "FN42", "JimmyEngine 0.1.0", &spots, 1_700_000_000) {
        Some(bytes) => println!(
            "  Built a well-formed {}-byte IPFIX datagram for {} spot(s), target={} (not sent).",
            bytes.len(),
            spots.len(),
            reporter.target()
        ),
        None => println!("  FAIL: build_datagram returned None for a non-empty spot list"),
    }
}
