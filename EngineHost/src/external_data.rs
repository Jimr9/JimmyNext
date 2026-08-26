//! Nexus-backed facts beyond the FT8/FT4 engine itself (release-candidate pass: "use Nexus for
//! what it already does well"). Two shapes, deliberately kept separate:
//!
//!   - **Background-cached, no credentials**: POTA/SOTA activator spots and space weather.
//!     Fetched periodically on a dedicated thread from Nexus's own public-feed transports
//!     (`propagation::live::{pota,swpc}` -- reused as-is, never reimplemented), cached, served
//!     instantly from memory on request. Jimmy Next polls like it polls SNAPSHOT.
//!
//!   - **On-demand, credential-bearing**: eQSL upload/download and HamQTH lookup. Jimmy Next
//!     owns credential storage (DPAPI-encrypted, same as every other logbook service) and sends
//!     them with each request; EngineHost never stores or logs them, and hands them straight to
//!     Nexus's own transports (`propagation::live::{eqsl,hamqth}` / `tempo_core::{eqsl,hamqth}`)
//!     which already carry the redacted-error/HTTPS-only/no-redirect discipline these two
//!     password-bearing services need. These can take up to ~60s (eQSL builds the file
//!     server-side) -- see run_control_server's own comment on why they must never run on its
//!     single-threaded accept loop.
//!
//! Ownership stays clean: this module is plumbing only. Jimmy Next decides what a spot means
//! (needed for which award, worth a notification), owns local logbook reconciliation for eQSL
//! downloads, and owns enable/disable policy for all of it.

use std::sync::{Arc, RwLock};
use std::time::{Duration, Instant};

use propagation::geo::maidenhead_to_latlon;
use propagation::live::{eqsl, hamqth, pota, swpc, swpc_scales};
use propagation::model::{r_scale, SpaceWx};
use propagation::pota::OtaSpot;
use propagation::{representative_muf, NoaaScalesView};

/// How often to refresh POTA/SOTA spots. Both feeds are meant for "who's on right now" --
/// frequent enough to be useful for chasing, not so frequent it hammers a free public API.
const SPOT_REFRESH: Duration = Duration::from_secs(90);
/// Space weather changes on the order of hours, not minutes -- SFI/Kp are reported hourly by
/// NOAA SWPC. No value in polling faster than this.
const SPACE_WX_REFRESH: Duration = Duration::from_secs(600);
/// NOAA's R/S/G scales update on roughly the same cadence as the raw SFI/Kp/X-ray feed above --
/// no value polling faster.
const NOAA_SCALES_REFRESH: Duration = Duration::from_secs(600);
/// SOTAwatch's `/spots/<n>/all` returns the last N by count, not by recency (see OtaSpot's own
/// doc comment) -- ask for enough that a quiet day doesn't starve the feed, matching Nexus's
/// own default expectation.
const SOTA_SPOT_COUNT: u32 = 50;

pub struct SharedCache {
    spots: RwLock<CachedSpots>,
    space_wx: RwLock<CachedSpaceWx>,
    scales: RwLock<CachedScales>,
    // Resolved once at construction (mirrors LiveFeedsCache's own me_latlon derivation in
    // live_feeds.rs) for the representative-MUF calculation below -- None when the grid doesn't
    // parse, in which case mufNow is simply omitted rather than guessed.
    me_latlon: Option<(f64, f64)>,
}

#[derive(Default)]
struct CachedSpots {
    spots: Vec<OtaSpot>,
    last_ok: Option<Instant>,
    last_error: Option<String>,
}

#[derive(Default)]
struct CachedSpaceWx {
    value: Option<SpaceWx>,
    last_ok: Option<Instant>,
    last_error: Option<String>,
}

#[derive(Default)]
struct CachedScales {
    value: Option<NoaaScalesView>,
    last_ok: Option<Instant>,
    last_error: Option<String>,
}

impl SharedCache {
    pub fn new(mygrid: &str) -> Arc<Self> {
        Arc::new(Self {
            spots: RwLock::new(CachedSpots::default()),
            space_wx: RwLock::new(CachedSpaceWx::default()),
            scales: RwLock::new(CachedScales::default()),
            me_latlon: maidenhead_to_latlon(mygrid.trim()),
        })
    }

    /// Starts the background refresh loop on a dedicated thread. Never panics the process on a
    /// fetch failure -- a dead/unreachable POTA or SWPC endpoint degrades to "stale or empty
    /// cache", never takes down engine/decode/TX, matching the graceful-degradation requirement
    /// (a non-safety-critical subsystem failing must not affect the rest of the engine).
    pub fn spawn_refresh_thread(self: &Arc<Self>) {
        let cache = self.clone();
        std::thread::spawn(move || loop {
            cache.refresh_spots();
            std::thread::sleep(SPOT_REFRESH);
        });
        let cache = self.clone();
        std::thread::spawn(move || loop {
            cache.refresh_space_wx();
            std::thread::sleep(SPACE_WX_REFRESH);
        });
        let cache = self.clone();
        std::thread::spawn(move || loop {
            cache.refresh_scales();
            std::thread::sleep(NOAA_SCALES_REFRESH);
        });
    }

    fn refresh_spots(&self) {
        // Two independent public feeds -- one failing (e.g. SOTAwatch down) must not blank out
        // the other's already-working data. Each fetch keeps the OLD cached value on error
        // rather than clearing it, so a transient outage degrades to "a bit stale", not "empty".
        let pota_result = pota::fetch_pota_spots();
        let sota_result = pota::fetch_sota_spots(SOTA_SPOT_COUNT);

        let mut merged = Vec::new();
        let mut errors = Vec::new();
        let mut any_succeeded = false;
        match pota_result {
            Ok(mut v) => {
                any_succeeded = true;
                merged.append(&mut v);
            }
            Err(e) => errors.push(format!("POTA: {e}")),
        }
        match sota_result {
            Ok(mut v) => {
                any_succeeded = true;
                merged.append(&mut v);
            }
            Err(e) => errors.push(format!("SOTA: {e}")),
        }

        let mut guard = self.spots.write().unwrap_or_else(|e| e.into_inner());
        // Only replace the cache when at least one feed produced real data (even an
        // empty-but-successful fetch counts -- a genuinely quiet moment is still real data).
        // If BOTH feeds failed this cycle, keep whatever was cached before -- a transient outage
        // degrades to "a bit stale", never to an empty list that reads as "nothing spotted".
        if any_succeeded {
            guard.spots = merged;
            guard.last_ok = Some(Instant::now());
        }
        guard.last_error = if errors.is_empty() { None } else { Some(errors.join("; ")) };
    }

    fn refresh_space_wx(&self) {
        match swpc::fetch_space_wx() {
            Ok(wx) => {
                let mut guard = self.space_wx.write().unwrap_or_else(|e| e.into_inner());
                guard.value = Some(wx);
                guard.last_ok = Some(Instant::now());
                guard.last_error = None;
            }
            Err(e) => {
                // Keep the stale value -- see refresh_spots's own comment on the same choice.
                let mut guard = self.space_wx.write().unwrap_or_else(|e| e.into_inner());
                guard.last_error = Some(e);
            }
        }
    }

    /// NOAA's own R/S/G scales (radio blackout / solar radiation storm / geomagnetic storm,
    /// each 0-5) -- a SEPARATE SWPC product from fetch_space_wx's raw SFI/Kp/X-ray, fetched
    /// independently so one product's outage never blanks the other (same discipline as
    /// refresh_spots's two-independent-feeds comment).
    fn refresh_scales(&self) {
        match swpc_scales::fetch_noaa_scales() {
            Ok(scales) => {
                let mut guard = self.scales.write().unwrap_or_else(|e| e.into_inner());
                guard.value = Some(scales);
                guard.last_ok = Some(Instant::now());
                guard.last_error = None;
            }
            Err(e) => {
                let mut guard = self.scales.write().unwrap_or_else(|e| e.into_inner());
                guard.last_error = Some(e);
            }
        }
    }

    /// Current cached spots as JSON, plus staleness info the UI can show ("as of 3 min ago").
    pub fn spots_json(&self) -> String {
        let guard = self.spots.read().unwrap_or_else(|e| e.into_inner());
        let age_secs = guard.last_ok.map(|t| t.elapsed().as_secs());
        let payload = SpotsPayload {
            spots: &guard.spots,
            age_secs,
            last_error: guard.last_error.as_deref(),
        };
        serde_json::to_string(&payload).unwrap_or_else(|e| format!("{{\"error\":\"{e}\"}}"))
    }

    /// The current cached space-weather value (if any fetch has ever succeeded), for
    /// live_feeds.rs's band-conditions advisory -- reuses this cache's own refresh loop rather
    /// than fetching a second time. SpaceWx is Copy, so this is a cheap by-value read.
    pub fn current_space_wx(&self) -> Option<SpaceWx> {
        self.space_wx.read().unwrap_or_else(|e| e.into_inner()).value
    }

    pub fn space_wx_json(&self) -> String {
        let guard = self.space_wx.read().unwrap_or_else(|e| e.into_inner());
        let age_secs = guard.last_ok.map(|t| t.elapsed().as_secs());
        let wire = guard.value.as_ref().map(SpaceWxWire::from);

        // Representative MUF: Nexus's own predict::representative_muf -- the ring-max
        // controlling MUF over 8 evenly-spaced long-haul (~9000 km) directions from the
        // operator's own grid, using the SAME SpaceWx this response already carries. NOT a
        // specific DX path; it answers "what's the highest classical F2 MUF reachable in SOME
        // typical long-haul direction from here right now" (see SpaceWxPayload's own doc
        // comment for the full explanation). Needs both a resolvable grid and a real SpaceWx
        // reading -- omitted (None) rather than guessed when either is missing.
        let muf_now = match (self.me_latlon, guard.value.as_ref()) {
            (Some(me), Some(wx)) => Some(representative_muf(me, now_unix(), wx)),
            _ => None,
        };

        let scales_guard = self.scales.read().unwrap_or_else(|e| e.into_inner());
        let scales_age_secs = scales_guard.last_ok.map(|t| t.elapsed().as_secs());
        let scales = scales_guard.value.as_ref().map(|s| NoaaScalesWire {
            g_scale: s.g,
            g_scale_tomorrow: s.g_tomorrow,
            s_scale: s.s,
        });

        let payload = SpaceWxPayload {
            value: wire.as_ref(),
            age_secs,
            last_error: guard.last_error.as_deref(),
            muf_now,
            scales,
            scales_age_secs,
            scales_last_error: scales_guard.last_error.as_deref(),
        };
        serde_json::to_string(&payload).unwrap_or_else(|e| format!("{{\"error\":\"{e}\"}}"))
    }
}

fn now_unix() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}


#[derive(serde::Serialize)]
struct SpotsPayload<'a> {
    spots: &'a [OtaSpot],
    #[serde(rename = "ageSecs")]
    age_secs: Option<u64>,
    #[serde(rename = "lastError")]
    last_error: Option<&'a str>,
}

// propagation::model::SpaceWx (#[derive(Serialize)]) has no #[serde(rename_all)], so it
// serializes its Rust field names verbatim: "a_index", "xray_long". Jimmy Next's C# side
// (ExternalDataClient.cs) deserializes with JsonNamingPolicy.CamelCase, which expects
// "aIndex"/"xrayLong" -- System.Text.Json does not throw on an unmatched property, so those
// two silently kept C#'s default float value (0.0) instead of the real reading, while sfi/kp/
// ssn (no underscore, already camelCase-equivalent) happened to match and came through fine.
// Confirmed live in a JAWS pass: SFI/Kp read correctly, A-index/X-ray always read "0.0"/
// "0.0e+0". Fixed here, on Jimmy Next's own EngineHost side, rather than touching the vendored
// Nexus SpaceWx type (never hand-edited -- see scripts/prepare-nexus.ps1) -- this wire DTO is
// exactly the pattern live_feeds.rs's BandReportPayload/RegionReportPayload already use for
// the same reason.
//
// Also surfaces two of Nexus's OWN existing classifications for xray_long (never a Jimmy Next
// interpretation): SpaceWx::xray_class() (the standard NOAA flare-class letter) and
// propagation::model::r_scale() (the standard NOAA R-scale radio-blackout risk, 0-5) -- both
// already computed by Nexus from the same raw value, just not previously surfaced.
#[derive(serde::Serialize)]
struct SpaceWxWire {
    sfi: f32,
    ssn: Option<f32>,
    kp: f32,
    #[serde(rename = "aIndex")]
    a_index: f32,
    #[serde(rename = "xrayLong")]
    xray_long: f32,
    #[serde(rename = "xrayClass")]
    xray_class: String,
    #[serde(rename = "rScale")]
    r_scale: u8,
}

impl From<&SpaceWx> for SpaceWxWire {
    fn from(wx: &SpaceWx) -> Self {
        Self {
            sfi: wx.sfi,
            ssn: wx.ssn,
            kp: wx.kp,
            a_index: wx.a_index,
            xray_long: wx.xray_long,
            xray_class: wx.xray_class().to_string(),
            r_scale: r_scale(wx.xray_long),
        }
    }
}

// NOAA's own G (geomagnetic storm) and S (solar radiation storm) scales, each 0-5, from
// propagation::live::swpc_scales::fetch_noaa_scales() -- a SEPARATE SWPC product
// (products/noaa-scales.json) from the raw SFI/Kp/X-ray feed above, fetched independently.
// R (radio blackout) is deliberately NOT re-surfaced here: NOAA defines R purely as a function
// of GOES long X-ray flux (R1=M1, R2=M5, R3=X1, R4=X10, R5=X20) -- exactly what
// propagation::model::r_scale(xray_long) (already in SpaceWxWire.rScale) computes from the same
// raw reading this response already carries, so re-fetching NOAA's own copy of the same number
// would be a second source for the same fact, not new information. G and S are different
// physical quantities (Kp-derived and >=10 MeV proton-flux-derived respectively) Jimmy Next has
// no other source for, which is the actual point of this second fetch.
#[derive(serde::Serialize)]
struct NoaaScalesWire {
    #[serde(rename = "gScale")]
    g_scale: u8,
    #[serde(rename = "gScaleTomorrow")]
    g_scale_tomorrow: u8,
    #[serde(rename = "sScale")]
    s_scale: u8,
}

/// mufNow: see space_wx_json's own comment for exactly what it represents (ring-max, NOT a
/// specific path). scales/scalesAgeSecs/scalesLastError mirror value/ageSecs/lastError's own
/// shape but for the separate NOAA R/S/G product, since it can succeed or fail independently of
/// the raw SFI/Kp/X-ray fetch (see refresh_scales's own comment).
#[derive(serde::Serialize)]
struct SpaceWxPayload<'a> {
    value: Option<&'a SpaceWxWire>,
    #[serde(rename = "ageSecs")]
    age_secs: Option<u64>,
    #[serde(rename = "lastError")]
    last_error: Option<&'a str>,
    #[serde(rename = "mufNow")]
    muf_now: Option<f32>,
    scales: Option<NoaaScalesWire>,
    #[serde(rename = "scalesAgeSecs")]
    scales_age_secs: Option<u64>,
    #[serde(rename = "scalesLastError")]
    scales_last_error: Option<&'a str>,
}

#[cfg(test)]
mod space_wx_wire_tests {
    use super::*;

    #[test]
    fn wire_field_names_are_camel_case_matching_jimmy_tests_expectations() {
        let wx = SpaceWx {
            sfi: 130.5,
            ssn: Some(45.0),
            kp: 2.0,
            a_index: 8.0,
            xray_long: 1e-6,
        };
        let wire = SpaceWxWire::from(&wx);
        let json = serde_json::to_string(&wire).unwrap();
        // The exact bug: "a_index"/"xray_long" (Rust default) vs "aIndex"/"xrayLong" (what
        // Jimmy Next's JsonNamingPolicy.CamelCase actually looks for).
        assert!(json.contains("\"aIndex\":8.0"), "got: {json}");
        assert!(json.contains("\"xrayLong\":"), "got: {json}");
        assert!(!json.contains("a_index"), "must not regress to the snake_case name: {json}");
        assert!(!json.contains("xray_long"), "must not regress to the snake_case name: {json}");
        assert!(json.contains("\"xrayClass\":\"C\""), "1e-6 is C-class: {json}");
        assert!(json.contains("\"rScale\":0"), "1e-6 is below the R1 threshold (1e-5): {json}");
    }

    #[test]
    fn space_wx_json_includes_representative_muf_when_grid_and_wx_are_both_known() {
        let cache = SharedCache::new("EN52");
        {
            let mut guard = cache.space_wx.write().unwrap();
            guard.value = Some(SpaceWx {
                sfi: 150.0,
                ssn: None,
                kp: 2.0,
                a_index: 8.0,
                xray_long: 1e-7,
            });
            guard.last_ok = Some(Instant::now());
        }
        let json = cache.space_wx_json();
        let v: serde_json::Value = serde_json::from_str(&json).unwrap();
        assert!(
            v["mufNow"].as_f64().unwrap_or(0.0) > 0.0,
            "a resolvable grid + real SpaceWx must produce a positive representative MUF: {json}"
        );
    }

    #[test]
    fn space_wx_json_omits_muf_when_grid_does_not_parse() {
        // No fabricated MUF when the operator's grid isn't set/valid -- None, not a 0 that
        // could misread as "MUF is zero" (the same "missing vs misleading zero" bug class this
        // whole tab was already fixed for once).
        let cache = SharedCache::new("not a grid");
        {
            let mut guard = cache.space_wx.write().unwrap();
            guard.value = Some(SpaceWx::default());
            guard.last_ok = Some(Instant::now());
        }
        let json = cache.space_wx_json();
        let v: serde_json::Value = serde_json::from_str(&json).unwrap();
        assert!(v["mufNow"].is_null(), "got: {json}");
    }

    #[test]
    fn space_wx_json_includes_noaa_scales_but_not_a_redundant_r() {
        let cache = SharedCache::new("EN52");
        {
            let mut guard = cache.scales.write().unwrap();
            guard.value = Some(NoaaScalesView {
                r: 1,
                s: 0,
                g: 1,
                g_tomorrow: 2,
                as_of: None,
            });
            guard.last_ok = Some(Instant::now());
        }
        let json = cache.space_wx_json();
        let v: serde_json::Value = serde_json::from_str(&json).unwrap();
        assert_eq!(v["scales"]["gScale"], 1, "got: {json}");
        assert_eq!(v["scales"]["gScaleTomorrow"], 2, "got: {json}");
        assert_eq!(v["scales"]["sScale"], 0, "got: {json}");
        // R deliberately not re-surfaced here -- see NoaaScalesWire's own comment on why it
        // would just be a second source for the same number SpaceWxWire.rScale already carries.
        assert!(
            v["scales"].get("rScale").is_none() && v["scales"].get("r").is_none(),
            "R must not be duplicated from the NOAA scales fetch: {json}"
        );
    }
}

// ---------------------------------------------------------------------------------------------
// On-demand, credential-bearing: eQSL upload/download, HamQTH lookup.
// Never cached, never logged, never written to disk here -- credentials arrive with the request
// and live only as long as this one function call.
// ---------------------------------------------------------------------------------------------

/// EQSL_UPLOAD wire args (Jimmy Next sends the whole ADIF record it already built for its own
/// local logbook -- EngineHost does not construct ADIF, that stays Jimmy Next's job).
#[derive(serde::Deserialize)]
pub struct EqslUploadArgs {
    pub username: String,
    pub password: String,
    pub record_adif: String,
}

/// Uploads one QSO to eQSL. Returns a wire-tagged outcome ("pending"/"accepted"/"duplicate"/
/// "rejected"/"authfail") on a successful round trip, or Err with a redacted (never
/// credential-bearing) message on transport failure.
pub fn eqsl_upload(args: &EqslUploadArgs) -> Result<&'static str, String> {
    let body = tempo_core::eqsl::build_upload_body(&args.username, &args.password, &args.record_adif);
    let html = eqsl::post_form(tempo_core::eqsl::EQSL_IMPORT_URL, body)?;
    match tempo_core::eqsl::classify_upload(&html) {
        Some(outcome) => Ok(outcome.code()),
        None => Err("eQSL: could not classify the upload response".to_string()),
    }
}

/// EQSL_DOWNLOAD wire args. `since_unix` is the last-reconciled watermark (Jimmy Next's own
/// local logbook already tracks a per-service "last synced" time the same way it does for
/// LoTW/Club Log) -- eQSL's own `format_rcvd_since` turns it into the InBox query's date filter,
/// so this never re-downloads a operator's entire confirmation history on every sync.
#[derive(serde::Deserialize)]
pub struct EqslDownloadArgs {
    pub username: String,
    pub password: String,
    pub since_unix: Option<i64>,
}

/// Downloads eQSL InBox confirmations as raw ADIF text. Jimmy Next parses/reconciles this
/// against its own logbook (dedup by its own dedup_key, same as every other import path) --
/// EngineHost does not touch Jimmy Next's database.
pub fn eqsl_download(args: &EqslDownloadArgs) -> Result<String, String> {
    let query = tempo_core::eqsl::EqslQuery {
        username: args.username.clone(),
        password: args.password.clone(),
        rcvd_since: args.since_unix.map(tempo_core::eqsl::format_rcvd_since),
    };
    let url = tempo_core::eqsl::build_inbox_url(&query);
    let body = eqsl::fetch_inbox(&url)?;
    if !tempo_core::eqsl::is_eqsl_adif(&body) {
        return Err("eQSL: response was not a recognizable ADIF InBox".to_string());
    }
    // A truncated-but-HTTP-200 body (partial final record) must never reach Jimmy Next as if
    // it were a complete download -- Jimmy's own reconciliation would otherwise treat a missing
    // trailing record as "not confirmed" rather than "not yet received", and (if it also
    // advances a since_unix watermark from this response) could permanently skip the record on
    // every later sync too. See tempo_core::eqsl::is_complete_eqsl_body's own doc comment.
    if !tempo_core::eqsl::is_complete_eqsl_body(&body) {
        return Err("eQSL: download appears truncated -- try again".to_string());
    }
    Ok(body)
}

/// HAMQTH_LOOKUP wire args. Combined login+lookup per call (no session-id caching across
/// requests) -- simpler and more robust than threading HamQTH's ~1h session lifetime through
/// this process's own state for what is, in Jimmy Next's usage, an occasional operator-driven
/// lookup rather than a per-decode hot path.
#[derive(serde::Deserialize)]
pub struct HamQthLookupArgs {
    pub username: String,
    pub password: String,
    pub callsign: String,
}

// Full shape of tempo_core::hamqth::HamQthLookup -- previously this DTO dropped dxcc/cq_zone/
// itu_zone (obtained the data, then discarded it before it ever reached Jimmy Next). dxcc is
// the numeric ADIF/DXCC entity ID from HamQTH's own <adif> element (the operator's self-reported
// profile address, resolved by HamQTH -- NOT a prefix-algorithm resolution the way
// ClubLogProvider is). image/lat/lon are left out: nothing in Jimmy Next's accessible lookup
// dialog currently has a slot for a photo or a map, and adding one is out of scope here -- see
// ARCHITECTURE.md if a future pass wants them.
#[derive(serde::Serialize)]
pub struct HamQthLookupResult {
    pub call: String,
    pub name: Option<String>,
    pub qth: Option<String>,
    pub grid: Option<String>,
    pub state: Option<String>,
    pub country: Option<String>,
    pub dxcc: Option<u32>,
    pub cq_zone: Option<u32>,
    pub itu_zone: Option<u32>,
}

pub fn hamqth_lookup(args: &HamQthLookupArgs) -> Result<HamQthLookupResult, String> {
    let session_id = hamqth_login(&args.username, &args.password)?;

    let lookup_url = tempo_core::hamqth::build_lookup_url(&session_id, &args.callsign, tempo_core::hamqth::HAMQTH_PRG);
    let lookup_xml = hamqth::fetch(&lookup_url)?;
    let parsed = tempo_core::hamqth::parse_callsign(&lookup_xml)
        .ok_or_else(|| format!("HamQTH: no record found for {}", args.callsign))?;
    Ok(HamQthLookupResult {
        call: parsed.call,
        name: parsed.name,
        qth: parsed.qth,
        grid: parsed.grid,
        state: parsed.state,
        country: parsed.country,
        dxcc: parsed.dxcc,
        cq_zone: parsed.cq_zone,
        itu_zone: parsed.itu_zone,
    })
}

/// HAMQTH_TEST wire args -- login only, no lookup. Mirrors QrzProvider's own TestAsync (Jimmy
/// Test side): proves the username/password are accepted without spending a lookup on an
/// arbitrary callsign.
#[derive(serde::Deserialize)]
pub struct HamQthTestArgs {
    pub username: String,
    pub password: String,
}

pub fn hamqth_test(args: &HamQthTestArgs) -> Result<(), String> {
    hamqth_login(&args.username, &args.password).map(|_| ())
}

fn hamqth_login(username: &str, password: &str) -> Result<String, String> {
    let login = tempo_core::hamqth::HamQthLogin {
        username: username.to_string(),
        password: password.to_string(),
    };
    let login_url = tempo_core::hamqth::build_login_url(&login);
    let login_xml = hamqth::fetch(&login_url)?;
    let session = tempo_core::hamqth::parse_session(&login_xml);
    session
        .session_id
        .ok_or_else(|| "HamQTH: login failed -- check username/password".to_string())
}

