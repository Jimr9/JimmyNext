//! Nexus contesting foundation (phase 3+): the EngineHost <-> Jimmy contest bridge.
//!
//! Jimmy owns station/operator profile values, contest support-level policy, the accessible
//! Contesting UI, and the permanent logbook. Nexus owns contest definitions, validation,
//! duplicate rules, scoring, and export -- this module is the thin, EngineHost-owned adapter
//! between them, never a reimplementation of anything Nexus already does.
//!
//! ## Ownership boundary this module holds to
//! - Contest RULES (exchange shape, domains, scoring, banned modes) always come from
//!   `tempo_core::fd_rules`/`tempo_core::contest` -- never hardcoded here.
//! - Station/operator values are Jimmy's; they are set on the live `Engine`'s `Settings`
//!   transiently (via `apply_settings`, in memory only) at `enter()` time and never persisted --
//!   EngineHost never calls any settings-save/disk-persistence function. See `enter()`'s own
//!   comment for why `apply_settings` (not a narrow setter) is the right call here specifically.
//! - The durable, unique operating-instance identity is EngineHost's own, not Nexus's
//!   `ContestSession.id` (which is a `"<contest_id>:<location>"` key, deliberately reused across
//!   different years/instances from the same place -- verified directly against
//!   `contest/session.rs`, not assumed).

use std::io::Write as _;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use tempo_app::engine::Engine;

// ── Startup wiring ──────────────────────────────────────────────────────────────────────────

/// Call once, at EngineHost startup, before any contest command can run. Installs the
/// cty.dat-backed call resolver contests use for CQ-zone/country-multiplier placement (CQ WW,
/// CQ WPX -- not needed by ARRL/Winter Field Day, which have no multipliers, but installed
/// unconditionally so every future contest this bridge exposes works without a second startup
/// change). Mirrors Nexus's own `contest_call_resolver_install()` (src-tauri/src/lib.rs) exactly
/// -- same adapter function, same one-resolve-answers-both-halves reasoning.
///
/// Deliberately does NOT call `tempo_core::fd_rules::install_from`: that call exists only to
/// activate a DOWNLOADED rules file ahead of the bundled seed, and Jimmy has no rules-file
/// download feature (out of scope -- "don't implement additional automated protocols"). Every
/// `fd_rules` read already falls back to the bundled seed lazily on first use with no explicit
/// install needed (`fd_rules.rs`'s own doc: "the seed activates lazily as before").
pub fn install_call_resolver() {
    match tempo_core::contest::install_call_resolver(contest_place_call) {
        Ok(()) => eprintln!("contest_bridge: cty.dat wired to the contest scorer"),
        Err(e) => eprintln!("contest_bridge: install_call_resolver: {e}"),
    }
}

// ── Contest rules updates (2026-10-05) ───────────────────────────────────────────────────────
// Nexus's own mechanism (src-tauri `fd_rules_download_if_newer` / `fd_rules_load_from_disk`): the
// rules file Nexus publishes is downloaded, checked with `fd_rules::validate` and kept beside the
// contest session; `fd_rules::install_from` makes it THE table, at engine start only (the table
// is set once per process -- there is no live swap). So a check never changes the rules a running
// contest is scored by. And a session that continues after a restart keeps the rules it was
// entered with: rules_in_use records them, and a newer download waits until no session is active.

const RULES_URL: &str = "https://hamradiotools.io/nexus/fd-rules.json";

fn rules_file(dir: &std::path::Path) -> PathBuf { dir.join("fd-rules.json") }
fn rules_meta_file(dir: &std::path::Path) -> PathBuf { dir.join("fd-rules.meta.json") }
fn rules_in_use_file(dir: &std::path::Path) -> PathBuf { dir.join("fd-rules.in-use.txt") }

#[derive(serde::Serialize, serde::Deserialize, Default, Clone)]
struct RulesMeta {
    generated: String,
    rules_year: u16,
    checked_unix: i64,
}

fn rules_meta(dir: &std::path::Path) -> RulesMeta {
    std::fs::read_to_string(rules_meta_file(dir))
        .ok()
        .and_then(|s| serde_json::from_str(&s).ok())
        .unwrap_or_default()
}

/// At engine start, before anything reads the rules table. With no contest session active, a
/// downloaded file newer than the bundled rules is installed. With one active, only the rules it
/// was entered with: the downloaded file when that is what it used, else the bundled rules.
pub fn install_rules_at_startup(dir: &std::path::Path) {
    let text = match std::fs::read_to_string(rules_file(dir)) {
        Ok(t) => t,
        Err(_) => return, // nothing downloaded: the bundled rules
    };
    let session_active = dir.join("contest_session_instance.json").exists();
    if session_active {
        let in_use = std::fs::read_to_string(rules_in_use_file(dir)).unwrap_or_default();
        let downloaded = tempo_core::fd_rules::validate(&text).map(|s| s.generated).unwrap_or_default();
        if in_use.trim().is_empty() || in_use.trim() != downloaded {
            eprintln!("contest_bridge: a contest session is active -- keeping the rules it was entered with");
            return;
        }
    }
    match tempo_core::fd_rules::install_from(&text) {
        Ok(s) => eprintln!("contest_bridge: downloaded contest rules active (rules year {}, generated {})", s.rules_year, s.generated),
        Err(e) => eprintln!("contest_bridge: downloaded contest rules not used ({e}) -- bundled rules active"),
    }
}

/// Recorded when a session is entered: the rules it is scored by.
fn record_rules_in_use(dir: &std::path::Path) {
    let _ = std::fs::write(rules_in_use_file(dir), tempo_core::fd_rules::active_generated());
}

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct RulesStatusWire {
    rules_year: u16,
    active_generated: String,
    bundled_generated: String,
    downloaded_generated: String,
    downloaded_rules_year: u16,
    checked_unix: i64,
    /// A downloaded file newer than the rules in force: it applies when Jimmy Next next starts
    /// with no contest session active.
    waiting_for_restart: bool,
    session_active: bool,
}

pub fn rules_status_json(dir: &std::path::Path) -> String {
    let m = rules_meta(dir);
    let active = tempo_core::fd_rules::active_generated().to_string();
    let wire = RulesStatusWire {
        rules_year: tempo_core::fd_rules::active_rules_year(),
        bundled_generated: tempo_core::fd_rules::seed_generated().to_string(),
        waiting_for_restart: !m.generated.is_empty() && m.generated.as_str() > active.as_str(),
        active_generated: active,
        downloaded_generated: m.generated,
        downloaded_rules_year: m.rules_year,
        checked_unix: m.checked_unix,
        session_active: dir.join("contest_session_instance.json").exists(),
    };
    serde_json::to_string(&wire).unwrap_or_else(|e| format!("{{\"error\":\"{e}\"}}"))
}

/// The Check for Rules Updates button: download, validate, keep if newer. Never changes the
/// rules in force (see above). "OK <status json>" or "ERR <why>".
pub fn rules_check(dir: &std::path::Path) -> String {
    let text = match propagation::live::contests::fetch(RULES_URL) {
        Ok(t) => t,
        Err(e) => return format!("ERR could not download the contest rules: {e}"),
    };
    let stats = match tempo_core::fd_rules::validate(&text) {
        Ok(s) => s,
        Err(e) => return format!("ERR the downloaded contest rules failed their check: {e}"),
    };
    let now = std::time::SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_secs() as i64).unwrap_or(0);
    let old = rules_meta(dir);
    let keep_file = stats.generated.as_str() >= tempo_core::fd_rules::seed_generated()
        && (stats.generated != old.generated || !rules_file(dir).exists());
    if keep_file {
        let final_path = rules_file(dir);
        let tmp = final_path.with_extension(format!("json.{}.tmp", std::process::id()));
        if let Err(e) = std::fs::write(&tmp, &text).and_then(|_| std::fs::rename(&tmp, &final_path)) {
            return format!("ERR could not save the contest rules: {e}");
        }
    }
    let meta = if keep_file || stats.generated == old.generated {
        RulesMeta { generated: stats.generated.clone(), rules_year: stats.rules_year, checked_unix: now }
    } else {
        RulesMeta { checked_unix: now, ..old }   // older than the bundled rules: nothing kept
    };
    let _ = std::fs::write(rules_meta_file(dir), serde_json::to_string(&meta).unwrap_or_default());
    format!("OK {}", rules_status_json(dir))
}

/// The adapter between `propagation::dxcc` (AD1C's cty.dat) and `tempo_core::contest` -- verbatim
/// port of Nexus's own `contest_place_call` (src-tauri/src/lib.rs), since `tempo_core` cannot
/// depend on `propagation` itself (dependency direction) and EngineHost, like Nexus's own
/// src-tauri, is a composition root that depends on both.
fn contest_place_call(call: &str) -> Option<tempo_core::contest::CallLocation> {
    let info = propagation::dxcc::resolve(call)?;
    if info.cont.is_empty() {
        return None;
    }
    Some(tempo_core::contest::CallLocation {
        entity: info.entity,
        continent: info.cont,
        cq_zone: (info.cq_zone != 0).then_some(info.cq_zone),
    })
}

// ── CONTEST_LIST_EVENTS ─────────────────────────────────────────────────────────────────────

// The exact bundled seed `tempo_core::fd_rules` itself reads (`include_str!` in that crate) --
// referenced here by the same relative path into the pinned `.nexus-src` checkout, so this is
// always byte-identical to what `ruleset_by_id` resolves against (this module never calls
// `install_from`, so the active table IS the bundled seed, always). Reading it a second time
// here is solely to recover the LIST of event ids -- `tempo_core::fd_rules` has no public
// function to enumerate every ruleset (verified: only `ruleset(FdEvent, year)`, two enum arms,
// and `ruleset_by_id(event_id, year)`, which needs an id you already have). This reads Nexus's
// own data file for identifiers only; no rule content (exchange shape, scoring, validation) is
// parsed or duplicated here -- every actual rule lookup still goes through `ruleset_by_id`.
const SEED_JSON: &str = include_str!("../.nexus-src/crates/tempo-core/src/fd_rules.seed.json");

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct EventListEntry {
    event_id: String,
    contest_id: String,
    rules_year: u16,
}

/// Returns `OK <json array>` or `ERR <message>` (never partial/fabricated data) -- the bundled
/// seed is validated on every call: not a one-time startup assumption, so a corrupted or
/// incompatible `.nexus-src` checkout is a clear diagnostic, not a silent empty list.
pub fn list_events_json() -> String {
    match list_events() {
        Ok(events) => match serde_json::to_string(&events) {
            Ok(j) => format!("OK {j}"),
            Err(e) => format!("ERR could not encode event list: {e}"),
        },
        Err(e) => format!("ERR {e}"),
    }
}

fn list_events() -> Result<Vec<EventListEntry>, String> {
    let v: serde_json::Value = serde_json::from_str(SEED_JSON).map_err(|e| {
        format!("bundled fd_rules.seed.json is not valid JSON -- seed unavailable: {e}")
    })?;

    // Cross-check against the ACTIVE table's own generated stamp -- tempo_core::fd_rules's
    // real API, not just this module's own parse of the same bytes. Catches a real future
    // problem (this include_str! path silently pointing at a stale/different copy after a
    // pin update) loudly instead of serving a list that quietly disagrees with what
    // CONTEST_GET_RULESET actually resolves.
    let seed_generated = v
        .get("generated")
        .and_then(|g| g.as_str())
        .ok_or_else(|| "bundled fd_rules.seed.json has no \"generated\" stamp -- seed format incompatible".to_string())?;
    let active_generated = tempo_core::fd_rules::active_generated();
    if seed_generated != active_generated {
        return Err(format!(
            "fd_rules.seed.json mismatch: this module reads generated={seed_generated:?} but \
             the active rules table is generated={active_generated:?} -- .nexus-src checkout is \
             inconsistent with itself"
        ));
    }

    let rulesets = v
        .get("rulesets")
        .and_then(|r| r.as_array())
        .ok_or_else(|| "bundled fd_rules.seed.json has no \"rulesets\" array -- seed format incompatible".to_string())?;

    let mut out = Vec::with_capacity(rulesets.len());
    for (i, r) in rulesets.iter().enumerate() {
        let event = r
            .get("event")
            .and_then(|x| x.as_str())
            .ok_or_else(|| format!("rulesets[{i}] missing \"event\" -- seed format incompatible"))?;
        let contest_id = r
            .get("contest_id")
            .and_then(|x| x.as_str())
            .ok_or_else(|| format!("rulesets[{i}] missing \"contest_id\" -- seed format incompatible"))?;
        let rules_year = r
            .get("rules_year")
            .and_then(|x| x.as_u64())
            .ok_or_else(|| format!("rulesets[{i}] missing \"rules_year\" -- seed format incompatible"))?;
        out.push(EventListEntry {
            event_id: event.to_string(),
            contest_id: contest_id.to_string(),
            rules_year: rules_year as u16,
        });
    }
    if out.is_empty() {
        return Err("bundled fd_rules.seed.json has zero rulesets -- seed unavailable".to_string());
    }
    Ok(out)
}

// ── CONTEST_GET_RULESET ─────────────────────────────────────────────────────────────────────

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct DomainValueWire {
    code: String,
    name: String,
}

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct DomainWire {
    id: String,
    values: Vec<DomainValueWire>,
}

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
#[serde(tag = "type")]
enum FieldKindWire {
    Rst { digits: u8 },
    Serial,
    Enum { domain: DomainWire },
    Pattern { re: String },
    Number { min: u32, max: u32 },
    Grid { chars: u8 },
    Text { max_len: u8 },
    Call,
    // A OneOf's arms are flattened into their own wire shapes -- the Contesting page's control
    // factory picks a control per arm and lets the operator's entered value decide which one
    // matched (the same "copied" verdict Nexus's own ExchangeSpec::copied makes; this module
    // never pre-decides it).
    OneOf { arms: Vec<FieldKindWire> },
}

impl From<&tempo_core::contest::FieldKind> for FieldKindWire {
    fn from(k: &tempo_core::contest::FieldKind) -> Self {
        use tempo_core::contest::FieldKind as K;
        match k {
            K::Rst { digits } => FieldKindWire::Rst { digits: *digits },
            K::Serial { .. } => FieldKindWire::Serial,
            K::Enum { domain } => FieldKindWire::Enum {
                domain: DomainWire {
                    id: domain.id.to_string(),
                    values: domain
                        .values
                        .iter()
                        .map(|(code, name)| DomainValueWire {
                            code: (*code).to_string(),
                            name: (*name).to_string(),
                        })
                        .collect(),
                },
            },
            K::Pattern { re } => FieldKindWire::Pattern { re: (*re).to_string() },
            K::Number { min, max } => FieldKindWire::Number { min: *min, max: *max },
            K::Grid { chars } => FieldKindWire::Grid { chars: *chars },
            K::Text { max_len } => FieldKindWire::Text { max_len: *max_len },
            K::Call => FieldKindWire::Call,
            K::OneOf(arms) => FieldKindWire::OneOf {
                arms: arms.iter().map(FieldKindWire::from).collect(),
            },
        }
    }
}

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct FieldWire {
    key: String,
    label: Option<String>,
    required: bool,
    kind: FieldKindWire,
}

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct ModePointsWire {
    ph: u32,
    cw: u32,
    dig: u32,
}

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct RulesetWire {
    event_id: String,
    contest_id: String,
    rules_year: u16,
    exchange_name: String,
    fields: Vec<FieldWire>,
    // Advisory only -- see `enforcement` below and the Winter Field Day design note in
    // ARCHITECTURE.md: Nexus's own `banned_modes` doc says scoring/dupes never consult this, a
    // UI warn chip only. The Contesting page must show it, never block on it.
    banned_modes: Vec<String>,
    // Present only when this ruleset's scoring is the simple per-mode-class table (ARRL/Winter
    // Field Day, Sweepstakes) -- absent for relation/band-priced contests (CQ WW, CQ WPX, ARRL
    // VHF), which use a different scoring model this DTO does not attempt to flatten. A `null`
    // here is the honest "consult Nexus directly," never a fabricated zero table.
    #[serde(skip_serializing_if = "Option::is_none")]
    points_by_mode_class: Option<ModePointsWire>,
    enforcement: String,
}

/// Returns `OK <json>` or `ERR <message>`.
pub fn get_ruleset_json(event_id: &str) -> String {
    match tempo_core::fd_rules::ruleset_by_id(event_id, tempo_core::fd_rules::CURRENT_RULES_YEAR) {
        Some(rs) => {
            let points_by_mode_class = match rs.scoring.qso_points {
                tempo_core::contest::PointsRule::ByModeClass(mp) => Some(ModePointsWire {
                    ph: mp.ph,
                    cw: mp.cw,
                    dig: mp.dig,
                }),
                _ => None,
            };
            let wire = RulesetWire {
                event_id: rs.event.to_string(),
                contest_id: rs.contest_id.to_string(),
                rules_year: rs.rules_year,
                exchange_name: rs.exchange.name.to_string(),
                fields: rs
                    .exchange
                    .fields
                    .iter()
                    .map(|f| FieldWire {
                        key: f.key.to_string(),
                        label: f.label.map(|l| l.to_string()),
                        required: f.required,
                        kind: FieldKindWire::from(&f.kind), // enum-level rename_all covers max_len -> maxLen on the wire
                    })
                    .collect(),
                banned_modes: rs.banned_modes.iter().map(|m| m.to_string()).collect(),
                points_by_mode_class,
                enforcement: rs.enforcement.to_string(),
            };
            match serde_json::to_string(&wire) {
                Ok(j) => format!("OK {j}"),
                Err(e) => format!("ERR could not encode ruleset: {e}"),
            }
        }
        None => format!("ERR unknown event_id: {event_id}"),
    }
}

// ── Session-instance identity ───────────────────────────────────────────────────────────────

/// Mints a durable, globally-unique operating-instance id -- NOT `ContestSession.id`, which
/// (verified directly against `contest/session.rs`) is `"<contest_id>:<section-or-location>"`,
/// the same string for the same contest run from the same place in ANY year. Two Field Days a
/// year apart from section MO get the identical `ContestSession.id`; this id must not repeat.
/// Shape: `<unix-millis>-<random-hex>`, monotonic-ish and trivially unique within one process
/// (the random half also guards two EngineHost instances started in the same millisecond).
fn mint_session_instance_id() -> String {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let millis = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis())
        .unwrap_or(0);
    let n = COUNTER.fetch_add(1, Ordering::Relaxed);
    let mut seed = millis as u64 ^ std::process::id() as u64;
    seed = seed.wrapping_mul(0x2545_F491_4F6C_DD1D).wrapping_add(n);
    format!("{millis:013x}-{seed:016x}")
}

// ── Session bridge state ────────────────────────────────────────────────────────────────────

/// Everything about the currently-active contest session that Jimmy needs and Nexus's own
/// `ContestSession`/`Engine` don't durably expose the way this bridge needs. Held in its own
/// `Arc<Mutex<ContestBridge>>` in main.rs, separate from `Arc<Mutex<Engine>>` -- methods that
/// also need the engine take `&Engine`/`&mut Engine` explicitly, called while main.rs's command
/// dispatch already holds that lock (same convention every other command handler uses).
pub struct ContestBridge {
    active: Option<ActiveSession>,
    sidecar_dir: PathBuf,
    pending_rebuild: Option<PendingRebuild>,
    /// The most recent successfully-committed rebuild -- the sole source for score presentation
    /// and export once at least one rebuild has succeeded. See `rebuild_commit`'s own comment
    /// for the atomicity/failure-safety argument.
    last_rebuilt: Option<tempo_core::fieldday::FieldDayLog>,
}

/// On-disk shape of the sidecar file `persist_sidecar`/`restore_if_present` use. Versioned by
/// name (V1), not a schema-version field, matching this bridge's own small-file, EngineHost-
/// owned-only scope -- never read by Jimmy or Nexus, so a breaking shape change just needs a
/// new struct name and a corrupt-old-file fallback (already the `Err` arm of `restore_if_present`).
#[derive(serde::Serialize, serde::Deserialize)]
struct SidecarV1 {
    session_instance_id: String,
    event_id: String,
    run_mode: String,
    mycall: String,
    mygrid: String,
    class: String,
    section: String,
    category_operator: String,
    category_power: String,
    category_assisted: String,
    category_station: String,
}

struct ActiveSession {
    session_instance_id: String,
    event_id: String,
    /// High-water seq Jimmy has ACKED (its SQLite commit succeeded) -- EngineHost's own
    /// resend-suppression bookkeeping, not Nexus's concept. Reconciliation
    /// (`qsos_since`) never depends on this; it is always driven by the caller's own
    /// `after_seq`, so a lost/never-sent ack only costs one harmless redundant redelivery.
    acked_seq: u64,
    // Station/entry basis captured at ENTER time, so CONTEST_REBUILD_BEGIN can construct a
    // fresh, standalone session equivalent to the live one without reading Engine's private
    // Mode state (see rebuild's own doc comment for why it's standalone rather than reaching
    // into the live session at all), AND so restore_if_present can actively re-enter the same
    // mode after a restart (see that function's own comment -- a passive check is not enough).
    run_mode: String,
    mycall: String,
    mygrid: String,
    class: String,
    section: String,
    category_operator: String,
    category_power: String,
    category_assisted: String,
    category_station: String,
}

/// What CONTEST_ENTER needs from Jimmy, station values included -- passed explicitly per call,
/// never persisted into Nexus's own Settings (see this module's own header comment).
#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EnterArgs {
    pub event_id: String,
    /// "run" (auto-CQ) or "sp" (search & pounce) -- maps to Nexus's own "fieldday-run"/
    /// "fieldday-sp" mode specs. Only these two are wired today (ARRL/Winter Field Day, the
    /// only contests with a first-class `Mode::FieldDay` code path) -- see this module's header.
    pub run_mode: String,
    pub station_callsign: String,
    pub grid: String,
    #[serde(default)]
    pub operator_callsign: String,
    #[serde(default)]
    pub class: String,
    #[serde(default)]
    pub section: String,
    #[serde(default)]
    pub category_operator: String,
    #[serde(default)]
    pub category_power: String,
    #[serde(default)]
    pub category_assisted: String,
    #[serde(default)]
    pub category_station: String,
}

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct EnterOkWire {
    session_instance_id: String,
}

impl ContestBridge {
    pub fn new(sidecar_dir: PathBuf) -> Self {
        ContestBridge {
            active: None,
            sidecar_dir,
            pending_rebuild: None,
            last_rebuilt: None,
        }
    }

    fn sidecar_path(&self) -> PathBuf {
        self.sidecar_dir.join("contest_session_instance.json")
    }

    /// Restores a session-instance id (and the station/entry basis rebuild needs) left by a
    /// prior EngineHost process -- ACTIVELY (calls the same apply_settings + set_mode as a real
    /// CONTEST_ENTER), not passively.
    ///
    /// ⚠️ A confirmed bug this replaced: the first version of this function only CHECKED
    /// `engine.snapshot().field_day.is_some()` before restoring -- but a freshly-constructed
    /// `Engine` at startup is always `Mode::Chat` (nothing has called `set_mode` yet), so that
    /// check could never once pass, and restore silently did nothing on every real restart.
    /// Found by actually killing and relaunching the real compiled binary mid-session and
    /// observing the restored process report no active session at all -- not assumed from
    /// reading the code alone. Mirrors Nexus's OWN restore mechanism
    /// (`Engine::restore_field_day_if_enabled`, called from persisted-settings-file loading in
    /// its own app) applied to this bridge's own sidecar instead, since EngineHost never loads
    /// Nexus's settings.json at all.
    ///
    /// If re-entering the mode fails (e.g. a section retired between runs -- unlikely but
    /// possible), the sidecar is removed and no session is restored, rather than leaving a
    /// half-restored identity with no live engine mode behind it. acked_seq always restarts at
    /// 0 -- safe by design (a resend-suppression optimization only, see its own field comment);
    /// the worst case is one harmless redundant reconciliation delivery, never a lost or
    /// duplicated contact (the Logbook Service's own idempotent write is what actually prevents
    /// that).
    pub fn restore_if_present(&mut self, engine: &mut Engine) {
        if self.active.is_some() {
            return;
        }
        let text = match std::fs::read_to_string(self.sidecar_path()) {
            Ok(t) => t,
            Err(_) => return,
        };
        let saved: SidecarV1 = match serde_json::from_str(&text) {
            Ok(s) => s,
            Err(e) => {
                eprintln!("contest_bridge: sidecar file is corrupt, not restoring a session: {e}");
                return;
            }
        };
        let a = ActiveSession {
            session_instance_id: saved.session_instance_id,
            event_id: saved.event_id,
            acked_seq: 0,
            run_mode: saved.run_mode,
            mycall: saved.mycall,
            mygrid: saved.mygrid,
            class: saved.class,
            section: saved.section,
            category_operator: saved.category_operator,
            category_power: saved.category_power,
            category_assisted: saved.category_assisted,
            category_station: saved.category_station,
        };
        match Self::apply_and_enter_mode(engine, &a, "") {
            Ok(()) => {
                eprintln!(
                    "contest_bridge: restored session-instance id {} for event {}",
                    a.session_instance_id, a.event_id
                );
                self.active = Some(a);
            }
            Err(e) => {
                eprintln!("contest_bridge: could not restore session {}: {e} -- sidecar removed", a.session_instance_id);
                let _ = std::fs::remove_file(self.sidecar_path());
            }
        }
    }

    fn persist_sidecar(&self) {
        if let Some(a) = &self.active {
            let saved = SidecarV1 {
                session_instance_id: a.session_instance_id.clone(),
                event_id: a.event_id.clone(),
                run_mode: a.run_mode.clone(),
                mycall: a.mycall.clone(),
                mygrid: a.mygrid.clone(),
                class: a.class.clone(),
                section: a.section.clone(),
                category_operator: a.category_operator.clone(),
                category_power: a.category_power.clone(),
                category_assisted: a.category_assisted.clone(),
                category_station: a.category_station.clone(),
            };
            let tmp = self.sidecar_path().with_extension("json.tmp");
            let write = serde_json::to_string(&saved)
                .map_err(|e| e.to_string())
                .and_then(|j| {
                    std::fs::File::create(&tmp)
                        .and_then(|mut f| f.write_all(j.as_bytes()))
                        .map_err(|e| e.to_string())
                });
            if write.is_ok() {
                let _ = std::fs::rename(&tmp, self.sidecar_path());
            }
        } else {
            let _ = std::fs::remove_file(self.sidecar_path());
        }
    }

    /// CONTEST_ENTER. Sets station/contest fields on the engine's Settings via `apply_settings`
    /// (transient, never persisted to disk -- see header) and calls `Engine::set_mode`, Nexus's
    /// own real, already-shipped, already-TX-safety-gated Field Day mode entry (the exact
    /// function its own Tauri UI calls) -- this bridge drives an existing capability, it does
    /// not implement new TX/exchange-composition logic of its own.
    ///
    /// `apply_settings` (not a narrow per-field setter) is used deliberately: its own doc
    /// comment warns it is heavyweight ("the TX gate generation bumps... never the way to
    /// persist ONE field") and names narrow setters for anything hot-path. Entering a contest is
    /// a rare, deliberate, operator-initiated transition -- exactly Nexus's own UI's usage
    /// pattern (a Settings save, then a mode-entry click) -- not a hot path, so the heavyweight
    /// cost is correct here, not a shortcut.
    pub fn enter(&mut self, engine: &mut Engine, args: &EnterArgs) -> Result<String, String> {
        if self.active.is_some() {
            return Err("a contest session is already active -- call CONTEST_EXIT first".to_string());
        }
        let a = ActiveSession {
            session_instance_id: mint_session_instance_id(),
            event_id: args.event_id.clone(),
            acked_seq: 0,
            run_mode: args.run_mode.clone(),
            mycall: args.station_callsign.trim().to_ascii_uppercase(),
            mygrid: args.grid.trim().to_ascii_uppercase(),
            class: args.class.clone(),
            section: args.section.clone(),
            category_operator: args.category_operator.clone(),
            category_power: args.category_power.clone(),
            category_assisted: args.category_assisted.clone(),
            category_station: args.category_station.clone(),
        };
        Self::apply_and_enter_mode(engine, &a, args.operator_callsign.trim())?;

        let session_instance_id = a.session_instance_id.clone();
        self.active = Some(a);
        self.persist_sidecar();
        record_rules_in_use(&self.sidecar_dir);

        let wire = EnterOkWire { session_instance_id };
        serde_json::to_string(&wire).map_err(|e| format!("could not encode CONTEST_ENTER result: {e}"))
    }

    /// The engine-mutating half of CONTEST_ENTER, factored out so `restore_if_present` can call
    /// the exact same logic (apply_settings + set_mode) rather than a second, drifting copy of
    /// it. `apply_settings` (not a narrow per-field setter) is used deliberately: its own doc
    /// comment warns it is heavyweight ("the TX gate generation bumps... never the way to
    /// persist ONE field") and names narrow setters for anything hot-path. Entering (or
    /// restoring) a contest is a rare, deliberate transition -- exactly Nexus's own UI's usage
    /// pattern (a Settings save, then a mode-entry click) -- not a hot path, so the heavyweight
    /// cost is correct here, not a shortcut.
    fn apply_and_enter_mode(engine: &mut Engine, a: &ActiveSession, operator_callsign: &str) -> Result<(), String> {
        if a.run_mode != "run" && a.run_mode != "sp" {
            return Err(format!("unknown run_mode {:?} -- must be \"run\" or \"sp\"", a.run_mode));
        }
        // 2026-10-05: both run modes arm Nexus's Field Day FT8 sequencer, which knows one
        // exchange -- "CQ FD" and class + section -- whatever event is picked (engine.rs's
        // Mode::FieldDay drives it with no event check; its RTTY sequencer refuses other events
        // the same way). Any other contest would put the Field Day exchange on the air, so it is
        // refused here, for CONTEST_ENTER and for a restored session alike.
        if !matches!(a.event_id.trim(), "arrlfd" | "wfd") {
            return Err(format!(
                "Jimmy Next cannot operate {} on the air yet: Nexus's FT8/FT4 contest sequencer sends only the Field Day exchange.",
                a.event_id
            ));
        }
        let mut s = engine.settings().clone();
        s.mycall = a.mycall.clone();
        s.mygrid = a.mygrid.clone();
        if !operator_callsign.is_empty() {
            s.fd_operator = operator_callsign.trim().to_ascii_uppercase();
        }
        s.fd_event = a.event_id.clone();
        s.fd_class = a.class.clone();
        s.fd_section = a.section.clone();
        // ⚠️ The master switch (spec §1.3, verified directly against engine.rs's own snapshot
        // construction): Engine::snapshot() defensively blanks `field_day`/reverts `mode` to
        // Chat in its OUTPUT whenever `fd_active` is false, EVEN THOUGH the real internal
        // `self.mode` is genuinely `Mode::FieldDay` -- a real, confirmed miss this comment
        // exists because of, found by actually running CONTEST_ENTER against the real compiled
        // binary end-to-end (not assumed from reading the code alone) and observing
        // SNAPSHOT.fieldDay come back null despite a successful entry. Without this line,
        // CONTEST_ENTER "succeeds" (set_mode itself does not check fd_active) but every
        // downstream consumer of SNAPSHOT.fieldDay -- the accessible status display -- sees
        // nothing.
        s.fd_active = true;
        if !a.category_operator.is_empty() {
            s.contest_category_operator = a.category_operator.clone();
        }
        if !a.category_power.is_empty() {
            s.contest_category_power = a.category_power.clone();
        }
        if !a.category_assisted.is_empty() {
            s.contest_category_assisted = a.category_assisted.clone();
        }
        if !a.category_station.is_empty() {
            s.contest_category_station = a.category_station.clone();
        }
        engine.apply_settings(s);

        let spec = if a.run_mode == "run" { "fieldday-run" } else { "fieldday-sp" };
        engine.set_mode(spec)
    }

    /// CONTEST_EXIT. Returns the engine to Chat (the same idle mode `set_mode` documents every
    /// other mode returning to) -- the session's rows already survive in the journal
    /// (`persist_fd_log`, called automatically inside `set_mode` on every transition), so no
    /// data is at risk; only the auto-sequencer stops.
    pub fn exit(&mut self, engine: &mut Engine) -> Result<(), String> {
        if self.active.is_none() {
            return Err("no contest session is active".to_string());
        }
        engine.set_mode("chat")?;
        // Mirrors the master-switch set in enter() -- an exited session must not leave
        // fd_active stranded true (harmless today since mode is already Chat, but a future
        // restore_field_day_if_enabled-style path could otherwise resurrect FD chrome the
        // operator explicitly turned off).
        let mut s = engine.settings().clone();
        s.fd_active = false;
        engine.apply_settings(s);
        self.active = None;
        self.persist_sidecar();
        Ok(())
    }

    /// CONTEST_LOG_MANUAL (phase 8): the general manual contest-QSO workflow's entry point into
    /// Nexus's own validation/duplicate-checking, for a contact the operator made by some means
    /// this bridge doesn't control (voice, CW, another rig, or FT8/FT4 typed by hand). Calls
    /// Engine::contest_log_manual -- Nexus's own real manual-entry function (the same one a
    /// contest entry line in its own UI uses), never a Jimmy-side reimplementation of dupe
    /// logic. Requires an active session (Mode::FieldDay) -- manual logging for a Nexus-known
    /// contest still runs through Nexus's live session context, per the accepted design.
    /// Returns Ok(true) logged, Ok(false) refused as a duplicate (Nexus's own DupeRule, not a
    /// Jimmy-side guess) -- both are legitimate outcomes the caller must distinguish, not errors.
    pub fn log_manual(
        &self,
        engine: &mut Engine,
        call: &str,
        fields: &[(String, String)],
        mode: &str,
        submode: &str,
    ) -> Result<bool, String> {
        if self.active.is_none() {
            return Err("no contest session is active -- CONTEST_ENTER first".to_string());
        }
        engine.contest_log_manual(call, fields, mode, if submode.is_empty() { None } else { Some(submode) })
    }

    pub fn active_session_instance_id(&self) -> Option<&str> {
        self.active.as_ref().map(|a| a.session_instance_id.as_str())
    }

    /// CONTEST_QSOS_SINCE. Reuses `Engine::fd_sync_outbox`, Nexus's own real, already-tested
    /// "every contact after this seq" query (built for club-sync, but its filter/shape is
    /// exactly this bridge's reconciliation need) -- no new Nexus-side capability required.
    /// `fd_sync_outbox` needs a non-empty `fd_position_id`; `fd_ensure_position_id` is a pure,
    /// no-I/O local id mint (verified against its own body) and is NOT club-sync networking.
    pub fn qsos_since(&self, engine: &mut Engine, after_seq: u64) -> String {
        let Some(active) = &self.active else {
            return "OK []".to_string();
        };
        engine.fd_ensure_position_id();
        let rows = engine.fd_sync_outbox(after_seq);
        let wire: Vec<CompletionWire> = rows
            .iter()
            .map(|r| CompletionWire::from_wire_qso(&active.session_instance_id, r))
            .collect();
        match serde_json::to_string(&wire) {
            Ok(j) => format!("OK {j}"),
            Err(e) => format!("ERR could not encode completions: {e}"),
        }
    }

    /// CONTEST_QSO_ACK. Advances EngineHost's own resend-suppression watermark. Never required
    /// for correctness (qsos_since is always re-derivable from the live log), purely an
    /// optimization so a healthy connection doesn't redeliver what Jimmy already committed.
    pub fn ack(&mut self, seq: u64) {
        if let Some(a) = &mut self.active {
            if seq > a.acked_seq {
                a.acked_seq = seq;
            }
        }
    }

    fn build_session(active: &ActiveSession) -> Result<tempo_core::contest::ContestSession, String> {
        let station = tempo_core::contest::StationData {
            fd_class: active.class.clone(),
            fd_section: active.section.clone(),
            contest_qth_county: String::new(),
            contest_qth_state: String::new(),
            contest_check: String::new(),
            contest_cq_zone: String::new(),
            contest_itu_zone: String::new(),
            contest_power: String::new(),
            mycall: active.mycall.clone(),
            contest_category_operator: active.category_operator.clone(),
            contest_category_power: active.category_power.clone(),
            contest_category_assisted: active.category_assisted.clone(),
            contest_category_station: active.category_station.clone(),
            mygrid: active.mygrid.clone(),
            dxcc: false,
        };
        match tempo_core::fd_rules::ruleset_by_id(&active.event_id, tempo_core::fd_rules::CURRENT_RULES_YEAR) {
            Some(rs) => tempo_core::contest::ContestSession::for_ruleset(rs, &station),
            None => Ok(tempo_core::contest::ContestSession::field_day(
                tempo_core::fieldday::FdEvent::from_code(&active.event_id),
                &active.class,
                &active.section,
            )),
        }
    }

    /// CONTEST_REBUILD_BEGIN. Builds a fresh, STANDALONE `FieldDayLog` -- deliberately never the
    /// live engine's own internal session (which is private to `tempo_app::Mode` and stays
    /// running, on its own accumulating rows, for real-time on-air sequencing/dupe-checking,
    /// Nexus's own concern). Jimmy's authoritative rows are what actually get scored/exported;
    /// the live session is left completely untouched by every rebuild call, which is also why a
    /// failed or abandoned rebuild can never corrupt or lose live session state -- there is
    /// nothing shared to corrupt.
    ///
    /// Returns the live high-water seq AT THIS INSTANT, from the real engine, not inferred from
    /// anything Jimmy sends -- COMMIT uses exactly this value, never the max seq in Jimmy's own
    /// batch (which may have deleted its own highest-numbered contact).
    pub fn rebuild_begin(&mut self, engine: &mut Engine) -> Result<String, String> {
        let active = self.active.as_ref().ok_or("no contest session is active")?;
        let session = Self::build_session(active)?;
        let log = tempo_core::fieldday::FieldDayLog::new(&active.mycall, session, "");

        engine.fd_ensure_position_id();
        let high_water_seq = engine
            .fd_sync_outbox(0)
            .iter()
            .map(|r| r.seq)
            .max()
            .unwrap_or(0);

        let token = mint_session_instance_id();
        self.pending_rebuild = Some(PendingRebuild {
            token: token.clone(),
            high_water_seq,
            log,
        });

        #[derive(serde::Serialize)]
        #[serde(rename_all = "camelCase")]
        struct BeginWire {
            rebuild_token: String,
            live_high_water_seq: u64,
        }
        serde_json::to_string(&BeginWire { rebuild_token: token, live_high_water_seq: high_water_seq })
            .map_err(|e| format!("could not encode CONTEST_REBUILD_BEGIN result: {e}"))
    }

    /// CONTEST_REBUILD_APPEND. Replays one bounded batch of Jimmy's authoritative contacts into
    /// the staging log. Validates the token so a stale/mismatched batch (a second BEGIN started
    /// without the first being committed or abandoned) is refused loudly rather than silently
    /// merged into the wrong staging log.
    pub fn rebuild_append(&mut self, token: &str, contacts: &[RebuildContact]) -> Result<(), String> {
        let pending = self
            .pending_rebuild
            .as_mut()
            .ok_or("no rebuild is in progress -- call CONTEST_REBUILD_BEGIN first")?;
        if pending.token != token {
            return Err(format!(
                "rebuild token mismatch: batch carries {token:?}, active rebuild is {:?} -- \
                 a stale or concurrent rebuild attempt was refused",
                pending.token
            ));
        }
        for c in contacts {
            pending.log.log_fields_at(&c.call, &c.fields, &c.mode, &c.submode, c.when_unix, c.when_unix);
        }
        Ok(())
    }

    /// CONTEST_REBUILD_COMMIT. Validates the token, carries forward only live completions with
    /// `seq` strictly greater than the high-water mark `BEGIN` returned (never inferred from
    /// Jimmy's own batch), and -- only once every step above has succeeded -- atomically
    /// replaces `last_rebuilt`, a single field assignment under this bridge's own mutex, so no
    /// partial/half-built state is ever visible to a concurrent score-read or export call. On
    /// ANY failure (bad token, encode error) `last_rebuilt` is untouched -- score presentation
    /// and export both fall back to whatever the last successful rebuild produced (or the live
    /// engine snapshot if none has ever succeeded), never a torn state.
    pub fn rebuild_commit(&mut self, engine: &mut Engine, token: &str) -> Result<String, String> {
        let pending = self
            .pending_rebuild
            .take()
            .ok_or("no rebuild is in progress -- call CONTEST_REBUILD_BEGIN first")?;
        if pending.token != token {
            // Put it back -- a wrong-token commit must not consume/abandon a real rebuild that
            // might still be legitimately committed with the right token.
            let high_water_seq = pending.high_water_seq;
            let log = pending.log;
            self.pending_rebuild = Some(PendingRebuild { token: pending.token, high_water_seq, log });
            return Err("rebuild token mismatch at commit -- rebuild left pending, not abandoned".to_string());
        }
        let mut log = pending.log;

        // Carry forward live completions after the returned cutover -- contacts made WHILE
        // Jimmy was uploading its batch, which by definition Jimmy's own snapshot could not
        // have included.
        engine.fd_ensure_position_id();
        for r in engine.fd_sync_outbox(pending.high_water_seq) {
            let fields: Vec<(String, String)> = r.ex.iter().map(|f| (f.k.clone(), f.r.clone())).collect();
            log.log_fields_at(&r.call, &fields, &r.mode, &r.sub, r.when, r.when);
        }

        let qso_count = log.qsos().len();
        let points = log.qso_points();
        self.last_rebuilt = Some(log);

        #[derive(serde::Serialize)]
        #[serde(rename_all = "camelCase")]
        struct CommitWire {
            qso_count: usize,
            points: u32,
        }
        serde_json::to_string(&CommitWire { qso_count, points })
            .map_err(|e| format!("could not encode CONTEST_REBUILD_COMMIT result: {e}"))
    }

    /// CONTEST_EXPORT. Requires a successful rebuild first (per the accepted design: "force a
    /// successful rebuild before... every export" -- there is deliberately no path from this
    /// function to the live engine's own possibly-stale session).
    pub fn export(&self, format: &str, operator_name: &str, contest_email: &str) -> Result<String, String> {
        let log = self
            .last_rebuilt
            .as_ref()
            .ok_or("no successful CONTEST_REBUILD_COMMIT yet -- export requires a rebuilt log")?;
        if format.eq_ignore_ascii_case("adif") {
            Ok(log.adif())
        } else {
            let entrant = tempo_core::contest::CabrilloEntrant {
                name: operator_name.to_string(),
                email: contest_email.to_string(),
            };
            // freq_khz: 0 -- Cabrillo's own FREQ column is band-derived per row for Field Day
            // (transmitter_column is false for both FD rulesets), so a session-level dial
            // frequency is not load-bearing here the way it would be for a single-band contest.
            log.cabrillo_with(0, &entrant)
        }
    }
}

struct PendingRebuild {
    token: String,
    high_water_seq: u64,
    log: tempo_core::fieldday::FieldDayLog,
}

/// One contact in a CONTEST_REBUILD_APPEND batch -- generic key/value exchange fields, never a
/// specific contest's hardcoded field names (mirrors CompletionWire's own shape).
#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RebuildContact {
    pub call: String,
    pub fields: Vec<(String, String)>,
    pub mode: String,
    #[serde(default)]
    pub submode: String,
    pub when_unix: u64,
}

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CompletionWire {
    pub session_instance_id: String,
    pub seq: u64,
    pub call: String,
    pub band: String,
    pub mode: String,
    pub submode: String,
    pub when_unix: u64,
    /// Sent/received exchange fields, generic key/value pairs (never hardcoded to a specific
    /// contest's field names) -- Jimmy stores these as its own exchange_sent/exchange_rcvd.
    pub sent_fields: Vec<(String, String)>,
    pub rcvd_fields: Vec<(String, String)>,
}

impl CompletionWire {
    fn from_wire_qso(session_instance_id: &str, r: &tempo_net::fdsync::WireQso) -> Self {
        CompletionWire {
            session_instance_id: session_instance_id.to_string(),
            seq: r.seq,
            call: r.call.clone(),
            band: r.band.clone(),
            mode: r.mode.clone(),
            submode: r.sub.clone(),
            when_unix: r.when,
            sent_fields: r.mex.iter().map(|f| (f.k.clone(), f.r.clone())).collect(),
            rcvd_fields: r.ex.iter().map(|f| (f.k.clone(), f.r.clone())).collect(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn session_instance_ids_are_unique_even_in_the_same_millisecond() {
        let a = mint_session_instance_id();
        let b = mint_session_instance_id();
        assert_ne!(a, b, "two mints must never collide, even back-to-back");
    }

    #[test]
    fn list_events_reads_the_real_bundled_seed_and_is_internally_consistent() {
        let events = list_events().expect("bundled seed must parse");
        assert!(!events.is_empty());
        assert!(
            events.iter().any(|e| e.event_id == "arrlfd"),
            "arrlfd must be present in the real bundled seed"
        );
        // No duplicate event ids.
        let mut ids: Vec<&str> = events.iter().map(|e| e.event_id.as_str()).collect();
        ids.sort_unstable();
        ids.dedup();
        assert_eq!(ids.len(), events.len(), "list_events produced a duplicate event_id");
    }

    #[test]
    fn get_ruleset_json_arrlfd_has_class_and_section_fields() {
        let json = get_ruleset_json("arrlfd");
        assert!(json.starts_with("OK "), "expected OK, got: {json}");
        let body = &json[3..];
        let v: serde_json::Value = serde_json::from_str(body).expect("valid JSON");
        let fields = v["fields"].as_array().expect("fields array");
        let keys: Vec<&str> = fields.iter().map(|f| f["key"].as_str().unwrap()).collect();
        assert!(keys.contains(&"CLASS"), "arrlfd must expose a CLASS field, got {keys:?}");
        assert!(keys.contains(&"SECTION"), "arrlfd must expose a SECTION field, got {keys:?}");
        // ARRL FD scores via the simple per-mode-class table -- pointsByModeClass must be present.
        assert!(v.get("pointsByModeClass").is_some(), "arrlfd must report pointsByModeClass");
    }

    #[test]
    fn get_ruleset_json_unknown_event_is_a_clear_error_not_a_panic() {
        let json = get_ruleset_json("not-a-real-contest");
        assert!(json.starts_with("ERR "), "expected ERR, got: {json}");
    }

    fn test_active_session() -> ActiveSession {
        ActiveSession {
            session_instance_id: "test-instance-1".to_string(),
            event_id: "arrlfd".to_string(),
            acked_seq: 0,
            run_mode: "sp".to_string(),
            mycall: "K5KPE".to_string(),
            mygrid: "EM48".to_string(),
            class: "2A".to_string(),
            section: "MO".to_string(),
            category_operator: String::new(),
            category_power: String::new(),
            category_assisted: String::new(),
            category_station: String::new(),
        }
    }

    #[test]
    fn build_session_for_arrlfd_produces_a_valid_contest_session() {
        let active = test_active_session();
        let session = ContestBridge::build_session(&active).expect("valid class/section must build");
        assert_eq!(session.event_id, "arrlfd");
    }

    #[test]
    fn build_session_refuses_blank_section() {
        let mut active = test_active_session();
        active.section = "".to_string();
        let result = ContestBridge::build_session(&active);
        assert!(result.is_err(), "a blank section must be refused, not silently accepted");
    }

    #[test]
    fn build_session_refuses_retired_section() {
        // MAR (the pre-split Maritimes code) is retired -- ContestSession::for_ruleset's own
        // domain check must catch it (this is the exact regression the architecture review
        // flagged: ContestSession::field_day() does NOT validate, for_ruleset() does).
        let mut active = test_active_session();
        active.section = "MAR".to_string();
        let result = ContestBridge::build_session(&active);
        assert!(result.is_err(), "a retired section must be refused");
    }

    #[test]
    fn rebuild_append_rejects_a_mismatched_token() {
        let mut bridge = ContestBridge::new(std::env::temp_dir());
        let active = test_active_session();
        let session = ContestBridge::build_session(&active).unwrap();
        let log = tempo_core::fieldday::FieldDayLog::new(&active.mycall, session, "20m");
        bridge.pending_rebuild = Some(PendingRebuild {
            token: "real-token".to_string(),
            high_water_seq: 0,
            log,
        });
        let contacts = vec![RebuildContact {
            call: "W1AW".to_string(),
            fields: vec![("CLASS".to_string(), "1B".to_string()), ("SECTION".to_string(), "CT".to_string())],
            mode: "FT8".to_string(),
            submode: String::new(),
            when_unix: 1_700_000_000,
        }];
        let result = bridge.rebuild_append("wrong-token", &contacts);
        assert!(result.is_err(), "a mismatched token must be refused, not silently applied");
        // The pending rebuild must be untouched by the refused call.
        assert_eq!(bridge.pending_rebuild.as_ref().unwrap().log.qsos().len(), 0);
    }

    #[test]
    fn session_instance_id_does_not_repeat_across_identical_reentries_unlike_nexus_own_session_id() {
        // Directly documents the distinction the architecture requires: Nexus's own
        // ContestSession.id for two IDENTICAL entries (same event, same section -- exactly
        // "this year" vs "next year" from the same location) is the SAME string, because it is
        // deliberately a "<contest_id>:<location>" key, not an instance id. This bridge's own
        // session_instance_id must never repeat for that same case.
        let active = test_active_session(); // event "arrlfd", section "MO"
        let nexus_session_1 = ContestBridge::build_session(&active).unwrap();
        let nexus_session_2 = ContestBridge::build_session(&active).unwrap();
        assert_eq!(
            nexus_session_1.id, nexus_session_2.id,
            "confirms the real behavior this architecture works around: Nexus's own \
             ContestSession.id collides for two identical entries"
        );

        let jimmy_id_1 = mint_session_instance_id();
        let jimmy_id_2 = mint_session_instance_id();
        assert_ne!(
            jimmy_id_1, jimmy_id_2,
            "this bridge's own session_instance_id must never repeat, even for the identical \
             event+section Nexus's own id collides on"
        );
    }

    #[test]
    fn rebuild_append_and_commit_produce_a_real_cabrillo_export() {
        // Exercises the actual data path rebuild_begin/commit use (FieldDayLog::log_fields_at +
        // qso_points + cabrillo_with), end to end, without needing a live Engine -- proves the
        // rebuild mechanism's core logic independent of the Engine/set_mode wiring.
        let mut bridge = ContestBridge::new(std::env::temp_dir());
        let active = test_active_session();
        let session = ContestBridge::build_session(&active).unwrap();
        let log = tempo_core::fieldday::FieldDayLog::new(&active.mycall, session, "20m");
        bridge.pending_rebuild = Some(PendingRebuild {
            token: "tok-1".to_string(),
            high_water_seq: 0,
            log,
        });

        let contacts = vec![
            RebuildContact {
                call: "W1AW".to_string(),
                fields: vec![("CLASS".to_string(), "1B".to_string()), ("SECTION".to_string(), "CT".to_string())],
                mode: "FT8".to_string(),
                submode: String::new(),
                when_unix: 1_700_000_000,
            },
            RebuildContact {
                call: "K1ABC".to_string(),
                fields: vec![("CLASS".to_string(), "3A".to_string()), ("SECTION".to_string(), "EMA".to_string())],
                mode: "FT8".to_string(),
                submode: String::new(),
                when_unix: 1_700_000_060,
            },
        ];
        bridge.rebuild_append("tok-1", &contacts).expect("matching token must be accepted");
        assert_eq!(bridge.pending_rebuild.as_ref().unwrap().log.qsos().len(), 2);

        // Commit needs an Engine for the "carry forward live completions" step -- exercised
        // here via direct field manipulation (bypassing rebuild_commit's Engine dependency) to
        // prove the export path specifically; rebuild_commit's own Engine-dependent half is
        // covered by construction (fd_sync_outbox is Nexus's own tested function).
        let pending = bridge.pending_rebuild.take().unwrap();
        bridge.last_rebuilt = Some(pending.log);

        let cabrillo = bridge.export("cabrillo", "Test Operator", "test@example.com").expect("export must succeed after a rebuild");
        assert!(cabrillo.contains("W1AW"), "Cabrillo export must contain the first logged call");
        assert!(cabrillo.contains("K1ABC"), "Cabrillo export must contain the second logged call");

        let adif = bridge.export("adif", "Test Operator", "test@example.com").expect("ADIF export must succeed after a rebuild");
        assert!(adif.contains("W1AW"), "ADIF export must contain the first logged call");
    }

    #[test]
    fn export_before_any_rebuild_is_a_clear_error_not_stale_or_fabricated_data() {
        let bridge = ContestBridge::new(std::env::temp_dir());
        let result = bridge.export("cabrillo", "Test Operator", "test@example.com");
        assert!(result.is_err(), "export before any successful rebuild must refuse, not fabricate data");
    }

    #[test]
    fn sidecar_round_trips_the_full_station_basis() {
        let dir = std::env::temp_dir().join(format!("jimmy_contest_bridge_test_{}", mint_session_instance_id()));
        std::fs::create_dir_all(&dir).unwrap();
        let mut bridge = ContestBridge::new(dir.clone());
        bridge.active = Some(test_active_session());
        bridge.persist_sidecar();

        let text = std::fs::read_to_string(bridge.sidecar_path()).expect("sidecar file must be written");
        let saved: SidecarV1 = serde_json::from_str(&text).expect("sidecar must be valid JSON");
        assert_eq!(saved.session_instance_id, "test-instance-1");
        assert_eq!(saved.mycall, "K5KPE");
        assert_eq!(saved.class, "2A");
        assert_eq!(saved.section, "MO");

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn section_domain_carries_real_resolved_values_not_an_empty_placeholder() {
        // Regression guard for the documented Nexus-side gap this bridge must NOT reproduce:
        // FdFieldDto omits domain values; this wire DTO must not make the same omission.
        let json = get_ruleset_json("arrlfd");
        let body = &json[3..];
        let v: serde_json::Value = serde_json::from_str(body).unwrap();
        let section_field = v["fields"]
            .as_array()
            .unwrap()
            .iter()
            .find(|f| f["key"] == "SECTION")
            .expect("SECTION field present");
        let values = section_field["kind"]["domain"]["values"]
            .as_array()
            .expect("SECTION field must carry a resolved domain value list");
        assert!(values.len() > 50, "ARRL/RAC section list should have 70+ entries, got {}", values.len());
    }
}
