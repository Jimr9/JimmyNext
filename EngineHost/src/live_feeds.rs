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
//!   - **DX spots**: connects to a DX-cluster/RBN telnet node (`tempo_net::cluster`) ONLY when
//!     the operator configures a server address -- there is no single universal default (DX
//!     clusters are an independently-run federation of nodes), unlike PSK Reporter's one public
//!     broker. Startup-CLI-arg-only (`--dx-cluster host:port`), same convention as the Decode
//!     tab's four non-live-settable options (NativeEngineClient.cs's own comment) -- changing it
//!     requires restarting the native engine, which Jimmy Test already does for those.
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

    cluster_spots: Mutex<SpotBuffer>,
    cluster_connected: AtomicBool,
    cluster_last_event: RwLock<Option<Instant>>,
    cluster_configured: bool,
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
            cluster_connected: AtomicBool::new(false),
            cluster_last_event: RwLock::new(None),
            cluster_configured: dx_cluster_addr.map(|a| !a.trim().is_empty()).unwrap_or(false),
        })
    }

    /// Starts the PSK Reporter MQTT feed (always) and the DX-cluster telnet feed (only if an
    /// address was configured at startup). Both run on their own dedicated thread with their
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
                        if let Ok(mut t) = cache.cluster_last_event.write() {
                            *t = Some(Instant::now());
                        }
                    },
                    &CLUSTER_STOP,
                    &cache.cluster_connected,
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
            Some(_) if spots.is_empty() => BandConditionsPayload {
                headline: None,
                bands: Vec::new(),
                banners: Vec::new(),
                spot_count: 0,
                connected,
                last_event_age_secs: last_event_age,
                error: Some(if connected {
                    "Connected -- waiting for reception reports".to_string()
                } else {
                    "Not yet connected to PSK Reporter".to_string()
                }),
            },
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
        let connected = self.cluster_connected.load(Ordering::Relaxed);
        let last_event_age = self
            .cluster_last_event
            .read()
            .ok()
            .and_then(|g| *g)
            .map(|t| t.elapsed().as_secs());
        let stale = is_stale(last_event_age);

        let payload = DxSpotsPayload {
            configured: self.cluster_configured,
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

#[derive(serde::Serialize)]
struct DxSpotsPayload {
    configured: bool,
    connected: bool,
    stale: bool,
    #[serde(rename = "lastEventAgeSecs")]
    last_event_age_secs: Option<u64>,
    spots: Vec<DxSpotPayload>,
}
