//! Live background feeds beyond the on-demand/cached-fact plumbing in external_data.rs:
//!
//!   - **Band conditions**: subscribes to Nexus's own PSK Reporter MQTT firehose (the operator's
//!     own reciprocal "who hears me / who I hear" reports, `tempo_net::mqtt` +
//!     `propagation::pskr_mqtt`), accumulates a rolling window, and runs Nexus's own
//!     `propagation::PropAdvisor` over it to produce a plain-language "what's open now" nowcast
//!     -- no VOACAP expertise required, counts people not physics (advisor.rs's own framing).
//!     Always on: the operator's callsign/grid are already known at EngineHost startup, so this
//!     needs no extra configuration.
//!
//!   - **DX spots**: TWO telnet sources feed one merged, deduped spot list
//!     (`tempo_net::cluster`), mirroring how official Nexus's own desktop app aggregates them
//!     (kd9taw/Nexus src-tauri/src/lib.rs's `start_cluster_feeds`):
//!       - RBN digital skimmer (`telnet.reversebeacon.net:7001` -- FT8/FT4/RTTY/PSK, the port
//!         relevant to an FT8/FT4-only app; the CW-only port 7000 is deliberately not wired,
//!         it would just add spots Jimmy Test's operator can't work). ALWAYS on, no operator
//!         configuration needed -- official Nexus wires this unconditionally too ("the RBN CW +
//!         digital skimmer feeds are wired automatically", Settings::default's own comment),
//!         it needs only the operator's own callsign (already known at EngineHost startup),
//!         same as PSK Reporter above.
//!       - An OPTIONAL human DX-cluster node (e.g. a VE7CC/CC-Cluster or DXSpider node) for
//!         SSB/phone and human-typed spots RBN's automated skimmers don't cover -- there is no
//!         single universal default for this one (DX clusters are an independently-run
//!         federation of nodes), so it stays operator-configured (Options > Decode Engine,
//!         `--dx-cluster host:port`), same convention as the Decode tab's other
//!         non-live-settable options (NativeEngineClient.cs's own comment) -- changing it
//!         requires restarting the native engine, which Jimmy Test already does for those.
//!     Every spot is tagged `rbn: true`/`false` at the push site (DxSpot.Rbn) so the UI can
//!     tell an automated skimmer spot from a human-typed one, same convention Nexus's own
//!     `start_cluster_feed` uses.
//!
//! Both transports are pure std TCP (no external MQTT/telnet crate) -- see Cargo.toml's own
//! comment on why `tempo-net` was a low-risk addition. Same graceful-degradation discipline as
//! external_data.rs: a feed that can't connect (or hasn't produced enough data yet) degrades to
//! "no data yet" / stale-but-real cached data, never takes down engine/decode/TX.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, RwLock};
use std::time::Instant;

use propagation::model::SpaceWx;
use propagation::{LiveSpots, PropAdvisor};
use tempo_net::cluster::SpotBuffer;

const PSKR_MQTT_ADDR: &str = "mqtt.pskreporter.info:1883";
/// RBN's digital skimmer port (FT8/FT4/RTTY/PSK) -- matches official Nexus's own
/// `RBN_DIGITAL_HOST` constant (kd9taw/Nexus src-tauri/src/lib.rs), wired unconditionally
/// there too. The CW-only port (7000, Nexus's `RBN_CW_HOST`) is deliberately not wired here --
/// Jimmy Test is FT8/FT4-only, and CW skimmer spots aren't something its operator can work.
const RBN_DIGITAL_ADDR: &str = "telnet.reversebeacon.net:7001";
/// Matches PropAdvisor's own default observation window (advisor.rs::AdvisorConfig::default) --
/// the accumulation buffer must cover at least this much, so advise() never reads past what's
/// actually retained.
const ADVISORY_WINDOW_SECS: i64 = 900;
const LIVE_SPOTS_CAP: usize = 20_000; // matches propagation::LiveSpots::default()'s own cap
const CLUSTER_SPOT_CAP: usize = 500;
/// A cluster/RBN feed is push-only (no discrete refresh cycle) -- "stale" here means "no spot
/// arrived recently enough to trust the connection is actually alive", not "cache aged out".
const DX_SPOTS_STALE_AFTER_SECS: u64 = 20 * 60;

pub struct LiveFeedsCache {
    mycall: String,
    mygrid: String,

    live_spots: RwLock<LiveSpots>,
    pskr_connected: AtomicBool,
    pskr_last_event: RwLock<Option<Instant>>,

    // Shared by BOTH the always-on RBN digital feed and the optional human DX-cluster node --
    // one merged, de-duped-by-callsign DX Spots list, same aggregation shape official Nexus's
    // own desktop app uses. `connected`/`last_event` are tracked PER SOURCE (not one shared
    // flag) so one feed's reconnect cycle can't misreport the other's health -- dx_spots_json
    // combines them (connected = either; last_event = the freshest of the two) when it builds
    // the wire payload.
    cluster_spots: Mutex<SpotBuffer>,
    rbn_connected: AtomicBool,
    rbn_last_event: RwLock<Option<Instant>>,
    human_cluster_connected: AtomicBool,
    human_cluster_last_event: RwLock<Option<Instant>>,
    /// True only when the operator has ALSO configured an optional human DX-cluster node
    /// (Options > Decode Engine). Purely informational now that RBN digital is auto-wired
    /// below -- it no longer gates whether the DX Spots tab has anything to show.
    human_cluster_configured: bool,
}

impl LiveFeedsCache {
    pub fn new(mycall: &str, mygrid: &str, dx_cluster_addr: Option<&str>) -> Arc<Self> {
        Arc::new(Self {
            mycall: mycall.trim().to_uppercase(),
            mygrid: mygrid.trim().to_uppercase(),
            live_spots: RwLock::new(LiveSpots::new(LIVE_SPOTS_CAP)),
            pskr_connected: AtomicBool::new(false),
            pskr_last_event: RwLock::new(None),
            cluster_spots: Mutex::new(SpotBuffer::new(CLUSTER_SPOT_CAP)),
            rbn_connected: AtomicBool::new(false),
            rbn_last_event: RwLock::new(None),
            human_cluster_connected: AtomicBool::new(false),
            human_cluster_last_event: RwLock::new(None),
            human_cluster_configured: dx_cluster_addr.map(|a| !a.trim().is_empty()).unwrap_or(false),
        })
    }

    /// Starts the PSK Reporter MQTT feed and the RBN digital skimmer feed (both always, gated
    /// only on having a real callsign) plus the optional human DX-cluster telnet feed (only if
    /// an address was configured at startup). Each runs on its own dedicated thread with its
    /// own internal reconnect/backoff (tempo_net's own `subscribe`/`run`) -- this call returns
    /// immediately.
    pub fn spawn_feed_threads(self: &Arc<Self>, dx_cluster_addr: Option<&str>) {
        if !self.mycall.is_empty() && self.mycall != "NOCALL" {
            let cache = self.clone();
            std::thread::spawn(move || {
                let topics = propagation::pskr_mqtt_topics(&cache.mycall);
                let topic_refs: Vec<&str> = topics.iter().map(|s| s.as_str()).collect();
                static PSKR_STOP: AtomicBool = AtomicBool::new(false);
                tempo_net::mqtt::subscribe(
                    PSKR_MQTT_ADDR,
                    &format!("jimmy-{}", cache.mycall),
                    &topic_refs,
                    |topic, payload| {
                        if let Some(spot) =
                            propagation::parse_pskr_mqtt_payload(topic, payload, now_unix())
                        {
                            if let Ok(mut b) = cache.live_spots.write() {
                                b.push(spot);
                            }
                            if let Ok(mut t) = cache.pskr_last_event.write() {
                                *t = Some(Instant::now());
                            }
                        }
                    },
                    &PSKR_STOP,
                    &cache.pskr_connected,
                );
            });
        }

        // RBN digital skimmer -- always on, same gate as PSK Reporter above (a real callsign
        // is all it needs; see RBN_DIGITAL_ADDR's own doc comment for why this mirrors official
        // Nexus's own "wired automatically" default).
        if !self.mycall.is_empty() && self.mycall != "NOCALL" {
            let call = self.mycall.clone();
            let cache = self.clone();
            std::thread::spawn(move || {
                static RBN_STOP: AtomicBool = AtomicBool::new(false);
                // RBN is receive-only -- never post a spot to a skimmer, same as official
                // Nexus's own RBN wiring (its RBN_DEAD_OUTBOX comment says exactly this).
                static RBN_OUTBOX: Mutex<std::collections::VecDeque<String>> =
                    Mutex::new(std::collections::VecDeque::new());
                tempo_net::cluster::run(
                    RBN_DIGITAL_ADDR,
                    &call,
                    |spot| {
                        // Mark the skimmer origin so a leading mode token (e.g. "FT8") can be
                        // trusted for DxSpot.SkimmerMode -- same convention as official Nexus's
                        // own start_cluster_feed (src-tauri/src/lib.rs), which tags rbn=true at
                        // exactly this push site rather than inside the parser (parse_dx_spot
                        // always sets rbn:false; "the pushing feed sets this true").
                        let mut s = spot.clone();
                        s.rbn = true;
                        if let Ok(mut b) = cache.cluster_spots.lock() {
                            b.push(s);
                        }
                        if let Ok(mut t) = cache.rbn_last_event.write() {
                            *t = Some(Instant::now());
                        }
                    },
                    &RBN_STOP,
                    &cache.rbn_connected,
                    &RBN_OUTBOX,
                );
            });
        }

        // Optional human DX-cluster node (SSB/phone + human-typed spots RBN's automated
        // skimmers don't cover) -- shares the SAME cluster_spots buffer as RBN above (one
        // merged DX Spots list), but its own connected/last-event tracking, so a hiccup on
        // either feed can't misreport the other's health.
        if let Some(addr) = dx_cluster_addr.filter(|a| !a.trim().is_empty()) {
            let addr = addr.trim().to_string();
            let call = self.mycall.clone();
            let cache = self.clone();
            std::thread::spawn(move || {
                static CLUSTER_STOP: AtomicBool = AtomicBool::new(false);
                static CLUSTER_OUTBOX: Mutex<std::collections::VecDeque<String>> =
                    Mutex::new(std::collections::VecDeque::new());
                tempo_net::cluster::run(
                    &addr,
                    &call,
                    |spot| {
                        if let Ok(mut b) = cache.cluster_spots.lock() {
                            b.push(spot.clone());
                        }
                        if let Ok(mut t) = cache.human_cluster_last_event.write() {
                            *t = Some(Instant::now());
                        }
                    },
                    &CLUSTER_STOP,
                    &cache.human_cluster_connected,
                    &CLUSTER_OUTBOX,
                );
            });
        }
    }

    /// Runs PropAdvisor over the current rolling PSK Reporter window + the latest cached space
    /// weather. `wx` is read from EngineHost's own SharedCache (external_data.rs) by the
    /// caller -- this module doesn't fetch space weather itself, avoiding a second fetch loop
    /// for data external_data.rs already keeps fresh.
    pub fn band_conditions_json(&self, wx: Option<&SpaceWx>) -> String {
        let now = now_unix();
        let spots = self
            .live_spots
            .read()
            .map(|g| g.recent(now, ADVISORY_WINDOW_SECS))
            .unwrap_or_default();
        let connected = self.pskr_connected.load(Ordering::Relaxed);
        let last_event_age = self
            .pskr_last_event
            .read()
            .ok()
            .and_then(|g| *g)
            .map(|t| t.elapsed().as_secs());

        let payload = match wx {
            None => BandConditionsPayload {
                headline: None,
                bands: Vec::new(),
                banners: Vec::new(),
                spot_count: spots.len(),
                connected,
                last_event_age_secs: last_event_age,
                error: Some("Space weather not yet available".to_string()),
            },
            // PropAdvisor::advise() already handles an empty spots window gracefully: it falls
            // back to the physics-only prior (MUF/absorption/aurora/greyline) and reports a
            // soft Quiet/Closed gradient per band, never an empty ladder -- see its own test
            // suite, e.g. silent_eligible_band_is_a_gradient_not_binary_closed and
            // modeled_open_unheard_band_is_quiet_not_closed. The previous "spots.is_empty() ->
            // blank the whole tab" branch here duplicated that judgment call incorrectly: it
            // discarded Nexus's own physics-based nowcast whenever the operator hadn't yet
            // accumulated PSK Reporter reception reports (the common case early in a session,
            // or a no-antenna/indoor test run) -- exactly the "Band Conditions shows 0 items"
            // bug. Let advise() decide; spot_count/connected below still tell the UI whether
            // it's looking at modeled-only or observed data.
            Some(wx) => {
                let advisor = PropAdvisor::new(&self.mycall, &self.mygrid);
                let advisory = advisor.advise(now, &spots, wx);
                BandConditionsPayload {
                    headline: Some(advisory.headline),
                    bands: advisory
                        .bands
                        .into_iter()
                        .map(|b| BandReportPayload {
                            band: b.band,
                            tier: b.tier.label(),
                            score: b.score,
                            n_hear_me: b.n_hear_me,
                            n_i_hear: b.n_i_hear,
                            confidence: b.confidence.label(),
                            reason: b.reason,
                            modeled: b.modeled,
                            modeled_reason: b.modeled_reason,
                            best_region: b.best_region.map(|r| RegionReportPayload {
                                region: r.region,
                                octant: r.octant,
                                bearing_deg: r.bearing_deg,
                                stations: r.stations,
                                bidirectional: r.bidirectional,
                            }),
                        })
                        .collect(),
                    banners: advisory.banners,
                    spot_count: spots.len(),
                    connected,
                    last_event_age_secs: last_event_age,
                    error: None,
                }
            }
        };
        serde_json::to_string(&payload).unwrap_or_else(|e| format!("{{\"error\":\"{e}\"}}"))
    }

    pub fn dx_spots_json(&self) -> String {
        let spots = self
            .cluster_spots
            .lock()
            .map(|b| b.recent())
            .unwrap_or_default();
        // Combine the two independently-tracked sources: connected if EITHER RBN or the
        // optional human node currently has a session up; last event = whichever is freshest.
        // A shared single flag would let one feed's reconnect cycle stomp the other's true
        // state (see the struct's own field comment).
        let rbn_connected = self.rbn_connected.load(Ordering::Relaxed);
        let human_connected = self.human_cluster_connected.load(Ordering::Relaxed);
        let connected = rbn_connected || human_connected;
        let rbn_last = self.rbn_last_event.read().ok().and_then(|g| *g);
        let human_last = self.human_cluster_last_event.read().ok().and_then(|g| *g);
        let last_event = match (rbn_last, human_last) {
            (Some(a), Some(b)) => Some(a.max(b)),
            (Some(a), None) | (None, Some(a)) => Some(a),
            (None, None) => None,
        };
        let last_event_age = last_event.map(|t| t.elapsed().as_secs());
        let stale = is_stale(last_event_age);

        let payload = DxSpotsPayload {
            configured: self.human_cluster_configured,
            connected,
            stale,
            last_event_age_secs: last_event_age,
            spots: spots
                .iter()
                .map(|s| DxSpotPayload {
                    spotter: s.spotter.clone(),
                    dx_call: s.dx_call.clone(),
                    freq_khz: s.freq_khz,
                    comment: s.comment.clone(),
                    time_utc: s.time_utc.clone(),
                    age_secs: if s.received_unix > 0 {
                        Some((now_unix() as u64).saturating_sub(s.received_unix))
                    } else {
                        None
                    },
                    rbn: s.rbn,
                    skimmer_mode: s.skimmer_mode().map(|m| m.to_string()),
                })
                .collect(),
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

/// A cluster/RBN feed is push-only, so "stale" is the only signal the UI has that the
/// connection might actually be dead despite `connected` still reading true (e.g. a quiet
/// telnet session with a dropped TCP half-close). `None` (no spot ever received this session)
/// is NOT stale -- that's "no data yet", a different, already-distinguished case (see
/// `configured`/`connected` in DxSpotsPayload).
fn is_stale(last_event_age_secs: Option<u64>) -> bool {
    last_event_age_secs.map(|a| a > DX_SPOTS_STALE_AFTER_SECS).unwrap_or(false)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn no_event_yet_is_not_stale() {
        assert!(!is_stale(None));
    }

    #[test]
    fn recent_event_is_not_stale() {
        assert!(!is_stale(Some(60)));
        assert!(!is_stale(Some(DX_SPOTS_STALE_AFTER_SECS)));
    }

    #[test]
    fn old_event_is_stale() {
        assert!(is_stale(Some(DX_SPOTS_STALE_AFTER_SECS + 1)));
        assert!(is_stale(Some(DX_SPOTS_STALE_AFTER_SECS * 10)));
    }

    // Regression coverage for the "Band Conditions shows 0 items" bug: a fresh
    // LiveFeedsCache has an empty live_spots window (no feed threads spawned in a unit
    // test), matching the real-world case of a session with no PSK Reporter reception
    // reports yet. band_conditions_json must still return a real modeled band ladder from
    // PropAdvisor's physics-only prior whenever space weather IS available -- see
    // PropAdvisor's own advisor.rs test suite (silent_eligible_band_is_a_gradient_not_binary_closed,
    // modeled_open_unheard_band_is_quiet_not_closed) for why zero spots must never mean zero
    // bands.
    #[test]
    fn band_conditions_with_wx_and_no_spots_still_returns_bands() {
        let cache = LiveFeedsCache::new("KD9TAW", "EN52", None);
        let wx = SpaceWx {
            sfi: 120.0,
            ssn: None,
            kp: 2.0,
            a_index: 8.0,
            xray_long: 1e-7,
        };
        let json = cache.band_conditions_json(Some(&wx));
        let v: serde_json::Value = serde_json::from_str(&json).unwrap();
        assert!(v["error"].is_null(), "no spots yet must not blank the whole tab: {json}");
        let bands = v["bands"].as_array().expect("bands array present");
        assert!(
            !bands.is_empty(),
            "PropAdvisor must still return a modeled band ladder with zero spots: {json}"
        );
        assert_eq!(v["spotCount"], 0);
    }

    // The ONE case that legitimately stays empty: space weather itself hasn't been fetched
    // yet (SharedCache's first NOAA SWPC poll hasn't completed / is offline). PropAdvisor
    // genuinely needs a SpaceWx to run at all.
    #[test]
    fn band_conditions_without_space_weather_reports_the_real_blocker() {
        let cache = LiveFeedsCache::new("KD9TAW", "EN52", None);
        let json = cache.band_conditions_json(None);
        let v: serde_json::Value = serde_json::from_str(&json).unwrap();
        assert!(v["error"].as_str().unwrap().contains("Space weather"));
        assert!(v["bands"].as_array().unwrap().is_empty());
    }
}

#[derive(serde::Serialize)]
struct RegionReportPayload {
    region: String,
    octant: String,
    #[serde(rename = "bearingDeg")]
    bearing_deg: f32,
    stations: u32,
    bidirectional: bool,
}

#[derive(serde::Serialize)]
struct BandReportPayload {
    band: String,
    tier: &'static str,
    score: f32,
    #[serde(rename = "nHearMe")]
    n_hear_me: u32,
    #[serde(rename = "nIHear")]
    n_i_hear: u32,
    confidence: &'static str,
    reason: String,
    modeled: String,
    #[serde(rename = "modeledReason")]
    modeled_reason: String,
    #[serde(rename = "bestRegion")]
    best_region: Option<RegionReportPayload>,
}

#[derive(serde::Serialize)]
struct BandConditionsPayload {
    headline: Option<String>,
    bands: Vec<BandReportPayload>,
    banners: Vec<String>,
    #[serde(rename = "spotCount")]
    spot_count: usize,
    connected: bool,
    #[serde(rename = "lastEventAgeSecs")]
    last_event_age_secs: Option<u64>,
    error: Option<String>,
}

#[derive(serde::Serialize)]
struct DxSpotPayload {
    spotter: String,
    #[serde(rename = "dxCall")]
    dx_call: String,
    #[serde(rename = "freqKhz")]
    freq_khz: f64,
    comment: String,
    #[serde(rename = "timeUtc")]
    time_utc: Option<String>,
    #[serde(rename = "ageSecs")]
    age_secs: Option<u64>,
    rbn: bool,
    #[serde(rename = "skimmerMode")]
    skimmer_mode: Option<String>,
}

/// `configured` means "an OPTIONAL human DX-cluster node is ALSO set up" -- it no longer gates
/// whether `spots`/`connected` have anything real in them, since RBN digital is always wired
/// (see live_feeds.rs's module doc comment). `false` is a normal, complete state: RBN-only,
/// nothing missing that the operator needs to go configure.
#[derive(serde::Serialize)]
struct DxSpotsPayload {
    configured: bool,
    connected: bool,
    stale: bool,
    #[serde(rename = "lastEventAgeSecs")]
    last_event_age_secs: Option<u64>,
    spots: Vec<DxSpotPayload>,
}
