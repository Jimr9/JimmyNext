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
use propagation::live::{contests, eqsl, hamqth, lotw, pota, qrz, solar_wind, swpc, swpc_scales};
use propagation::model::{r_scale, SpaceWx};
use propagation::pota::OtaSpot;
use propagation::{representative_muf, DailySolarIndices, NoaaScalesView, SolarWind};

/// How often to refresh POTA/SOTA spots. Both feeds are meant for "who's on right now" --
/// frequent enough to be useful for chasing, not so frequent it hammers a free public API.
const SPOT_REFRESH: Duration = Duration::from_secs(90);
/// Space weather changes on the order of hours, not minutes -- SFI/Kp are reported hourly by
/// NOAA SWPC. No value in polling faster than this.
const SPACE_WX_REFRESH: Duration = Duration::from_secs(600);
/// NOAA's daily solar indices change once a day; every three hours is plenty (2026-10-04).
const SOLAR_HISTORY_REFRESH: Duration = Duration::from_secs(3 * 3600);
/// DSCOVR solar wind updates by the minute; every five minutes keeps Bz current without
/// hammering SWPC (2026-10-04).
const SOLAR_WIND_REFRESH: Duration = Duration::from_secs(300);

/// The contest calendar as last read: the last good list is kept through a failed refresh, with
/// when it was read and why the newest try failed, so Jimmy can say the list is old.
#[derive(Default)]
struct CachedContests {
    events: Vec<contests::ContestEvent>,
    fetched_unix: Option<i64>,
    tried_unix: Option<i64>,
    error: Option<String>,
}
/// NOAA's R/S/G scales update on roughly the same cadence as the raw SFI/Kp/X-ray feed above --
/// no value polling faster.
const NOAA_SCALES_REFRESH: Duration = Duration::from_secs(600);
/// SOTAwatch's `/spots/<n>/all` returns the last N by count, not by recency (see OtaSpot's own
/// doc comment) -- ask for enough that a quiet day doesn't starve the feed, matching Nexus's
/// own default expectation.
const SOTA_SPOT_COUNT: u32 = 50;
/// The WA7BNM contest calendar, read by Nexus's own adapter (propagation::live::contests) from
/// the same feed its Contests pane uses, refreshed as lazily as that pane does (every 15 min).
const CONTEST_CALENDAR_URL: &str = "https://www.contestcalendar.com/calendar.rss";
const CONTEST_CALENDAR_REFRESH: Duration = Duration::from_secs(15 * 60);

pub struct SharedCache {
    spots: RwLock<CachedSpots>,
    // POTA park -> its POTA directory location ("US-ID", or "US-ID,US-WY" for a park in two),
    // looked up once per park with Nexus's own propagation::live::pota::fetch_park -- a park does
    // not move. "" = the directory knows no such park (not asked again). Jimmy logs an
    // activator's STATE from it (laptop 2026-09-29: AF0EC, activating in Idaho, logged as MO,
    // his mailing address).
    park_locations: RwLock<std::collections::HashMap<String, String>>,
    space_wx: RwLock<CachedSpaceWx>,
    scales: RwLock<CachedScales>,
    // Daily solar history and solar wind (operator, 2026-10-04) -- Nexus's own fetchers and
    // parsers (propagation::live::swpc::fetch_daily_solar_indices, live::solar_wind). Kept on
    // error like every cache here: a failed fetch leaves the last good copy, with its own dates.
    solar_history: RwLock<Cached<DailySolarIndices>>,
    solar_wind: RwLock<Cached<SolarWind>>,
    contests: RwLock<CachedContests>,
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

struct Cached<T> {
    value: Option<T>,
    last_ok: Option<Instant>,
    last_error: Option<String>,
}

impl<T> Default for Cached<T> {
    fn default() -> Self {
        Self { value: None, last_ok: None, last_error: None }
    }
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
            park_locations: RwLock::new(std::collections::HashMap::new()),
            space_wx: RwLock::new(CachedSpaceWx::default()),
            scales: RwLock::new(CachedScales::default()),
            solar_history: RwLock::new(Cached::default()),
            solar_wind: RwLock::new(Cached::default()),
            contests: RwLock::new(CachedContests::default()),
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
        let cache = self.clone();
        std::thread::spawn(move || loop {
            let r = swpc::fetch_daily_solar_indices();
            store(&cache.solar_history, r);
            std::thread::sleep(SOLAR_HISTORY_REFRESH);
        });
        let cache = self.clone();
        std::thread::spawn(move || loop {
            let r = solar_wind::fetch_solar_wind();
            store(&cache.solar_wind, r);
            std::thread::sleep(SOLAR_WIND_REFRESH);
        });
        let cache = self.clone();
        std::thread::spawn(move || loop {
            cache.refresh_contests();
            std::thread::sleep(CONTEST_CALENDAR_REFRESH);
        });
    }

    /// Reads the calendar now (also the Refresh Calendar button). A feed that parses to nothing
    /// is a failure, not an empty calendar: the last good list stays.
    pub fn refresh_contests(&self) {
        let r = contests::fetch(CONTEST_CALENDAR_URL).and_then(|xml| {
            let events = contests::parse_contest_rss(&xml);
            if events.is_empty() {
                Err("the calendar feed had no contests that could be read".to_string())
            } else {
                Ok(events)
            }
        });
        let now = now_unix();
        let mut g = self.contests.write().unwrap_or_else(|e| e.into_inner());
        g.tried_unix = Some(now);
        match r {
            Ok(events) => {
                g.events = events;
                g.fetched_unix = Some(now);
                g.error = None;
            }
            Err(e) => g.error = Some(e),
        }
    }

    pub fn contests_json(&self) -> String {
        let g = self.contests.read().unwrap_or_else(|e| e.into_inner());
        serde_json::json!({
            "events": g.events,
            "fetchedUnix": g.fetched_unix,
            "triedUnix": g.tried_unix,
            "error": g.error,
        })
        .to_string()
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
        let parks: Vec<String> = merged
            .iter()
            .filter(|sp| sp.program == "POTA" && !sp.reference.is_empty())
            .map(|sp| sp.reference.clone())
            .collect();
        if any_succeeded {
            guard.spots = merged;
            guard.last_ok = Some(Instant::now());
        }
        guard.last_error = if errors.is_empty() { None } else { Some(errors.join("; ")) };
        drop(guard);
        self.look_up_park_locations(parks);
    }

    /// The location of parks not yet known, a few per refresh so a first start does not ask
    /// the POTA directory about every active park at once. A failed lookup (network) is asked
    /// again next time; "no such park" is remembered as "".
    fn look_up_park_locations(&self, parks: Vec<String>) {
        const PER_REFRESH: usize = 15;
        let wanted: Vec<String> = {
            let known = self.park_locations.read().unwrap_or_else(|e| e.into_inner());
            let mut w: Vec<String> = parks.into_iter().filter(|r| !known.contains_key(r)).collect();
            w.sort();
            w.dedup();
            w.truncate(PER_REFRESH);
            w
        };
        for reference in wanted {
            let location = match pota::fetch_park(&reference) {
                Ok(park) => park.location,
                Err(e) if e.starts_with("no park found") => String::new(),
                Err(_) => continue,
            };
            self.park_locations
                .write()
                .unwrap_or_else(|e| e.into_inner())
                .insert(reference, location);
        }
    }

    /// SOLAR_HISTORY: NOAA's last ~30 days of 10.7 cm flux and sunspot number, oldest first.
    /// A day's value NOAA did not give is null -- never 0.
    pub fn solar_history_json(&self) -> String {
        let g = self.solar_history.read().unwrap_or_else(|e| e.into_inner());
        let payload = SolarHistoryPayload {
            days: g
                .value
                .as_ref()
                .map(|v| v.days.iter().map(|d| SolarDayPayload { day_unix: d.day_unix, sfi: d.sfi, ssn: d.ssn }).collect())
                .unwrap_or_default(),
            fetched_age_secs: g.last_ok.map(|t| t.elapsed().as_secs()),
            error: g.last_error.clone(),
        };
        serde_json::to_string(&payload).unwrap_or_else(|e| format!("{{\"error\":\"{e}\"}}"))
    }

    /// SOLAR_WIND: Bz, and total field / speed / density when known (null = not known, never
    /// 0). `measuredAgeSecs` is the reading's own age -- a recent successful FETCH can still
    /// carry an old MEASUREMENT -- and `stale` is Nexus's own rule (SolarWind::is_stale).
    pub fn solar_wind_json(&self) -> String {
        let g = self.solar_wind.read().unwrap_or_else(|e| e.into_inner());
        let now = now_unix();
        let w = g.value.as_ref();
        let payload = SolarWindPayload {
            bz_nt: w.map(|w| w.bz_nt),
            bt_nt: w.and_then(|w| w.bt_nt),
            speed_kms: w.and_then(|w| w.speed_kms),
            density: w.and_then(|w| w.density),
            measured_age_secs: w.map(|w| w.age_secs(now)),
            stale: w.map(|w| w.is_stale(now)).unwrap_or(true),
            fetched_age_secs: g.last_ok.map(|t| t.elapsed().as_secs()),
            error: g.last_error.clone(),
        };
        serde_json::to_string(&payload).unwrap_or_else(|e| format!("{{\"error\":\"{e}\"}}"))
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
        let parks = self.park_locations.read().unwrap_or_else(|e| e.into_inner());
        let spots: Vec<SpotWire> = guard
            .spots
            .iter()
            .map(|spot| SpotWire {
                spot,
                location: parks.get(&spot.reference).filter(|l| !l.is_empty()).map(String::as_str),
            })
            .collect();
        let payload = SpotsPayload {
            spots: &spots,
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

/// A fetch result into its cache: a success replaces the value; an error keeps the last good one.
fn store<T>(slot: &RwLock<Cached<T>>, r: Result<T, String>) {
    let mut g = slot.write().unwrap_or_else(|e| e.into_inner());
    match r {
        Ok(v) => {
            g.value = Some(v);
            g.last_ok = Some(Instant::now());
            g.last_error = None;
        }
        Err(e) => g.last_error = Some(e),
    }
}

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct SolarDayPayload {
    day_unix: i64,
    sfi: Option<f32>,
    ssn: Option<f32>,
}

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct SolarHistoryPayload {
    days: Vec<SolarDayPayload>,
    fetched_age_secs: Option<u64>,
    error: Option<String>,
}

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct SolarWindPayload {
    bz_nt: Option<f32>,
    bt_nt: Option<f32>,
    speed_kms: Option<f32>,
    density: Option<f32>,
    measured_age_secs: Option<i64>,
    stale: bool,
    fetched_age_secs: Option<u64>,
    error: Option<String>,
}

fn now_unix() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}


/// One spot as Jimmy receives it: Nexus's OtaSpot plus the park's location, when known.
#[derive(serde::Serialize)]
struct SpotWire<'a> {
    #[serde(flatten)]
    spot: &'a OtaSpot,
    #[serde(skip_serializing_if = "Option::is_none")]
    location: Option<&'a str>,
}

#[derive(serde::Serialize)]
struct SpotsPayload<'a> {
    spots: &'a [SpotWire<'a>],
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
    // qth_nickname: `None` -- Jimmy Next has no UI for eQSL's multi-profile "QTH Nickname"
    // (new upstream field, Nexus 93b9f012) and single-profile accounts don't need it; wiring
    // it up end-to-end (wire args + Options UI) is a separate feature, not part of this
    // compatibility upgrade.
    let body = tempo_core::eqsl::build_upload_body(&args.username, &args.password, &args.record_adif, None);
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
        // See eqsl_upload's own comment -- Jimmy Next has no multi-profile QTH Nickname UI yet.
        qth_nickname: None,
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

/// LOTW_DOWNLOAD wire args (2026-10-02): LoTW's report fetched by Nexus's own code -- the
/// request the Nexus desktop builds (tempo_core::lotw), its fetch (propagation::live::lotw) and
/// its checks -- instead of Jimmy Next's own copy. Jimmy keeps the high-water and its pairing
/// guard, and merges through LOG_MERGE as before.
#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LotwDownloadArgs {
    pub username: String,
    pub password: String,
    /// Confirmations matched since this LoTW high-water (`qso_qslsince`). Jimmy always sends a
    /// date ("1900-01-01" for everything): with none, LoTW uses a "system supplied default" --
    /// the account's last download -- and sends only what came after it.
    #[serde(default)]
    pub since: Option<String>,
    /// The own-records report (`qso_qsl=no`) from this QSO date (`YYYY-MM-DD`), instead of
    /// confirmations -- what tells which uploads LoTW holds.
    #[serde(default)]
    pub own_from: Option<String>,
}

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LotwDownloadReply {
    pub adif: String,
    /// LoTW's `APP_LoTW_LASTQSL` from a complete confirmations report: what Jimmy may store as the
    /// next `since` once the merge is made. None when absent (an empty incremental answer), or
    /// for the own-records report -- the caller then keeps the high-water it has.
    pub high_water: Option<String>,
}

pub fn lotw_download(args: &LotwDownloadArgs) -> Result<LotwDownloadReply, String> {
    let query = tempo_core::lotw::LotwQuery {
        username: args.username.clone(),
        password: args.password.clone(),
        // No qso_owncall: every callsign in the account, as Jimmy has always downloaded.
        owncall: None,
        qsl_since: args.since.clone().filter(|s| !s.trim().is_empty()),
    };
    let own_from = args.own_from.as_deref().filter(|s| !s.trim().is_empty());
    let body = {
        let url = match own_from {
            Some(from) => tempo_core::lotw::build_own_report_url(&query, Some(from)),
            None => tempo_core::lotw::build_report_url(&query),
        };
        lotw::fetch_report(&url)?
    }; // the URL carries the password -- dropped here, never logged
    checked_lotw_report(body, own_from.is_some())
}

/// What LOTW_DOWNLOAD hands on, or why not: a real LoTW report, complete to its end marker; the
/// high-water only from a confirmations report. Separate from the fetch so it is tested offline.
fn checked_lotw_report(body: String, own_records: bool) -> Result<LotwDownloadReply, String> {
    if !tempo_core::lotw::is_lotw_adif(&body) {
        // The Nexus desktop's wording: the login worked (a wrong password fails earlier).
        return Err("LoTW answered, but the download was not the report file it should have been. Your \
                    username and password are fine -- this is not a login problem. LoTW may be returning \
                    an error page, or may be down for maintenance. Try again shortly."
            .to_string());
    }
    // Complete only with LoTW's end marker (the Nexus desktop's is_complete_lotw_body): every
    // confirmation in a cut-off tail is dated at or before LASTQSL, so handing it on and storing
    // the high-water would skip them on every later download. Nothing is handed on instead.
    if !body.to_ascii_lowercase().contains("<app_lotw_eof>") {
        return Err("LoTW: the download was cut off -- nothing was merged; try again.".to_string());
    }
    let high_water = if own_records { None } else { tempo_core::lotw::extract_last_qsl(&body) };
    Ok(LotwDownloadReply { adif: body, high_water })
}

#[cfg(test)]
mod lotw_download_tests {
    use super::checked_lotw_report;

    const HEAD: &str = "ARRL Logbook of the World Status Report
<PROGBUGS:4>xxxx
<APP_LoTW_LASTQSL:19>2026-10-02 18:40:11
<eoh>
";
    const ROW: &str = "<CALL:5>C91RU<BAND:3>17M<MODE:3>FT8<QSO_DATE:8>20261002<TIME_ON:6>183000<QSL_RCVD:1>Y<eor>
";

    #[test]
    fn complete_report_gives_its_high_water() {
        let r = checked_lotw_report(format!("{HEAD}{ROW}<APP_LoTW_EOF>
"), false).expect("complete report");
        assert_eq!(r.high_water.as_deref(), Some("2026-10-02 18:40:11"));
        assert!(r.adif.contains("C91RU"));
    }

    #[test]
    fn cut_off_report_is_refused() {
        assert!(checked_lotw_report(format!("{HEAD}{ROW}"), false).is_err());
    }

    #[test]
    fn error_page_is_refused() {
        assert!(checked_lotw_report("<html><body>LoTW is down for maintenance</body></html>".into(), false).is_err());
    }

    #[test]
    fn own_records_report_never_moves_the_high_water() {
        let r = checked_lotw_report(format!("{HEAD}{ROW}<APP_LoTW_EOF>
"), true).expect("complete report");
        assert_eq!(r.high_water, None);
    }
}

/// QRZ_DOWNLOAD wire args (2026-10-02): the whole QRZ Logbook, fetched by Nexus's own code
/// (tempo_core::qrz FETCH + parse, propagation::live::qrz). Always the whole book: QRZ's
/// MODSINCE follows a record's own edit date, not its confirmation, and lost confirmations that
/// way (2026-07-09).
#[derive(serde::Deserialize)]
pub struct QrzDownloadArgs {
    pub key: String,
}

pub fn qrz_download(args: &QrzDownloadArgs) -> Result<String, String> {
    let resp = qrz::post_form(tempo_core::qrz::QRZ_LOGBOOK_URL, tempo_core::qrz::build_fetch_body(&args.key))?;
    let fetched = tempo_core::qrz::parse_fetch(&resp);
    if !fetched.ok {
        // An empty logbook answers FAIL with COUNT=0 and no reason -- not an error.
        if fetched.count == 0 && fetched.reason.is_none() {
            return Ok(String::new());
        }
        return Err(format!("QRZ refused the download: {}", fetched.reason.unwrap_or_else(|| "no reason given".into())));
    }
    Ok(fetched.adif)
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

