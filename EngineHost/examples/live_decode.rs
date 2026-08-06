//! Bounded, manual, RECEIVE-ONLY verification: opens the real default input device, captures
//! real audio for a fixed run (default ~65s = a bit over 4 FT8 periods), and decodes each
//! period through the real native decoder. Never asserts PTT or transmits -- this only proves
//! the real-hardware capture path (Phase 4d), same as the earlier synthesized-signal
//! proof-of-concept proved the FFI/protocol path (Phase 4c). Not part of the shipped binary.
//!
//! `cargo run --release --example live_decode [seconds]`

#[path = "../src/audio.rs"]
mod audio;

use std::time::{Duration, Instant, SystemTime};

fn main() {
    let run_secs: u64 = std::env::args()
        .nth(1)
        .and_then(|s| s.parse().ok())
        .unwrap_or(65);
    let device_name = std::env::args().nth(2);

    match &device_name {
        Some(n) => println!("Opening input device '{n}' (RECEIVE ONLY -- no PTT, no transmit)..."),
        None => println!("Opening DEFAULT audio input device (RECEIVE ONLY -- no PTT, no transmit)..."),
    }
    let result = match &device_name {
        Some(n) => audio::FrameCapture::open_named(n),
        None => audio::FrameCapture::open_default(),
    };
    let mut cap = match result {
        Ok(c) => c,
        Err(e) => {
            eprintln!("FAIL: could not open audio input device: {e}");
            std::process::exit(1);
        }
    };
    println!("Device open. Listening for {run_secs}s ({} FT8 periods)...\n", run_secs / 15);

    let start = Instant::now();
    // The next period we owe a decode attempt for. Starting at current+1 skips decoding a
    // period we joined mid-way through (its window would need samples from before this
    // process started capturing).
    let (current_period, _) = audio::period_position();
    let mut next_decode_period = current_period + 1;
    let mut last_level_report = Instant::now();
    let mut total_decodes = 0usize;
    let mut saw_nonzero_audio = false;

    while start.elapsed() < Duration::from_secs(run_secs) {
        cap.poll();

        let level = cap.rx_level();
        let raw_peak = cap.raw_peak_abs();
        if level > 0.0001 || raw_peak > 0.0001 {
            saw_nonzero_audio = true;
        }
        if last_level_report.elapsed() >= Duration::from_secs(5) {
            println!(
                "  [{:>3}s] rx_level(smoothed)={:.4}  raw_peak(last poll)={:.4}",
                start.elapsed().as_secs(),
                level,
                raw_peak
            );
            last_level_report = Instant::now();
        }

        // Try the oldest not-yet-decoded period every poll; frame_for_period returns None
        // until that period's window is fully captured, so this naturally fires once, right
        // when the data becomes available -- not on a fixed "1s after boundary" guess.
        let boundary = audio::period_boundary_time(next_decode_period);
        if boundary <= SystemTime::now() {
            if let Some(frame) = cap.frame_for_period(boundary) {
                print!(
                    "  [{:>3}s] period {next_decode_period}: decoding captured audio... ",
                    start.elapsed().as_secs()
                );
                let decodes = ft8::decode_frame(&frame, 200, 2900, 3, "", "", 0, 0, true, false);
                if decodes.is_empty() {
                    println!("no decodes");
                } else {
                    println!("{} decode(s):", decodes.len());
                    for d in &decodes {
                        println!(
                            "      snr={:>4} dt={:>5.2}s freq={:>5.0}Hz  '{}'",
                            d.snr, d.dt, d.freq, d.message
                        );
                    }
                    total_decodes += decodes.len();
                }
                next_decode_period += 1;
            }
        }

        std::thread::sleep(Duration::from_millis(150));
    }

    println!("\n──── Summary ────");
    println!("Real audio observed (rx_level > 0 at any point): {saw_nonzero_audio}");
    println!("Total real decodes: {total_decodes}");
    if !saw_nonzero_audio {
        println!(
            "\nNOTE: rx_level never left ~0.0 -- the input device opened but no audio energy was \
             seen. That's expected if nothing is connected/playing into it; it does NOT confirm \
             or deny the capture path itself, which is what this run is really checking."
        );
    }
}
