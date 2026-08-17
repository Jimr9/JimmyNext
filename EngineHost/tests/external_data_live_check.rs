//! Manual, real-network verification for external_data.rs's POTA/SOTA/space-weather
//! integration -- NOT part of the default test suite (network calls to third-party public APIs
//! would make CI flaky on transient outages/rate limits, and this repo's CI convention is to
//! keep the default suite offline). Run manually with:
//!   cargo test --release --test external_data_live_check -- --ignored --nocapture
//!
//! No credentials, no audio, no radio -- just confirms the actual HTTP fetch + parse pipeline
//! reaches real POTA/SOTA/NOAA endpoints and returns sane data, once, by hand.

#[test]
#[ignore]
fn pota_spots_fetch_reaches_the_real_api() {
    let spots = propagation::live::pota::fetch_pota_spots().expect("POTA fetch failed");
    println!("POTA spots: {}", spots.len());
    for s in spots.iter().take(3) {
        println!("  {} {} {} {:.3} MHz {}", s.program, s.reference, s.activator, s.freq_khz / 1000.0, s.mode);
    }
}

#[test]
#[ignore]
fn sota_spots_fetch_reaches_the_real_api() {
    let spots = propagation::live::pota::fetch_sota_spots(20).expect("SOTA fetch failed");
    println!("SOTA spots: {}", spots.len());
    for s in spots.iter().take(3) {
        println!("  {} {} {} {:.3} MHz {}", s.program, s.reference, s.activator, s.freq_khz / 1000.0, s.mode);
    }
}

#[test]
#[ignore]
fn space_wx_fetch_reaches_the_real_api() {
    let wx = propagation::live::swpc::fetch_space_wx().expect("space weather fetch failed");
    println!("SFI={} Kp={} A={} Xray={:.2e}", wx.sfi, wx.kp, wx.a_index, wx.xray_long);
    assert!(wx.sfi > 0.0, "SFI should be a real positive value, got {}", wx.sfi);
}
