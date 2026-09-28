//! Nexus-owned logbook, Phase 1 of the logbook migration (`c:\chat gpt\nexus log review.txt`).
//!
//! OFF unless EngineHost is launched with `--log-dir <folder>`. With the flag, this process opens
//! a Nexus logbook store in that folder (`log.adi` + its database beside it) and answers the LOG_*
//! control commands. Without it, nothing here runs and every LOG_* command answers
//! `ERR logbook not enabled` -- Jimmy's own LogbookDb stays the only logbook, exactly as before.
//! Phase 1 is exercised only against temporary test folders; Jimmy does not send these commands.
//!
//! Everything here is Nexus's own desktop wiring (src-tauri/src/lib.rs), reused rather than
//! re-derived: `open_logbook_store` / `adopt_logbook` / `store_resolve` / `country_resolver` for
//! the launch, `durable_command` for "answer only once the change is on disk", and the quit's
//! `flush_logbook` for SHUTDOWN. Nexus's rules decide duplicates, ids and storage.
//!
//! Two things Jimmy adds on top, both because a KILLED EngineHost loses whatever Nexus had not yet
//! committed (its retry queue lives in memory only -- `logstore::Unsaved`):
//!   - Every LOG_QSO carries Jimmy's own request id, and the contact's RecordId is DERIVED from
//!     it ([`record_id_for_request`]). A re-send of a request whose reply was lost therefore finds
//!     its contact by id and answers "already" instead of logging it twice -- across restarts too,
//!     since the id is stored with the contact. Jimmy keeps the request until it hears "saved".
//!   - A reply says "saved" only when Nexus's durability wait succeeded. Anything less is
//!     "unconfirmed", with the writer's reason, and Jimmy must not announce it as logged.

use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use tempo_app::dto::LoggedQso;
use tempo_app::engine::{Engine, LogWriteOutcome};
use tempo_app::logstore::{self, Freshness, OpenError, Opened, StoreResolve};
use tempo_core::logbook::{QsoRecord, RecordId};

/// The extra ADIF field carrying Jimmy's request id on the contact it created (traceability; the
/// id itself is what makes a re-send idempotent).
pub const REQ_ID_FIELD: &str = "APP_JIMMY_REQ_ID";

/// Position id stamped on every RecordId Jimmy derives: "JMNX". Nexus's own ids carry the
/// station's Field Day position id (0 when none), and Nexus's minter never draws a nonce an id in
/// the log already carries (`Minter::clear_of`), so a derived id cannot be re-minted by Nexus.
const JIMMY_POSID: u32 = 0x4A4D_4E58;

/// How long a durable command waits for its change to reach the disk -- Nexus's own figure.
pub const DURABLE_WAIT: Duration = logstore::DURABLE_WAIT;

/// What the store is, as opened at launch.
#[derive(Debug, Clone)]
pub enum StoreKind {
    /// The database owns the log (the ordinary Nexus launch). `detail` is how it opened.
    Database { detail: String },
    /// The database could not be used; the session keeps the log in log.adi (Nexus's 1.13 path).
    FileOnly { why: String },
}

/// The logbook this process owns, when `--log-dir` was given.
pub struct LogHost {
    dir: PathBuf,
    kind: StoreKind,
    /// LOG_QSO / SHUTDOWN serialize here, so the "is this request already logged?" check and the
    /// log that follows it cannot interleave with a second copy of the same request.
    writes: Mutex<WriteGate>,
}

struct WriteGate {
    /// Set by SHUTDOWN: no new write is taken once the final flush has begun.
    closed: bool,
}

/// The log file inside the folder: Nexus names the database beside it.
pub fn log_path(dir: &Path) -> PathBuf {
    dir.join("log.adi")
}

/// cty.dat's entity name for a call -- Nexus's `country_of`.
fn country_of(call: &str) -> Option<String> {
    propagation::dxcc::resolve(call).map(|i| i.entity.to_string())
}

/// Nexus's `country_resolver`: ONE shared resolver for the station and for the hot index the
/// store builds while it opens, so the attach installs that index instead of rebuilding it
/// under the engine lock.
fn country_resolver() -> Arc<tempo_app::station::DxccResolve> {
    static SHARED: std::sync::LazyLock<Arc<tempo_app::station::DxccResolve>> =
        std::sync::LazyLock::new(|| Arc::new(country_of));
    Arc::clone(&SHARED)
}

/// Nexus's `store_resolve`: the entity and CQ zone the store writes beside each contact.
fn store_resolve() -> StoreResolve {
    Arc::new(|r| {
        propagation::dxcc::resolve(&r.call).map_or_else(Default::default, |i| {
            tempo_core::logbook::sqlite::Resolved {
                entity: Some(i.entity),
                cq_zone: Some(i.cq_zone),
            }
        })
    })
}

/// Nexus's `open_logbook_store`: every bit of the launch's logbook I/O, BEFORE the engine lock is
/// taken. Converts an existing log.adi the first time (keeping `log.adi.pre-sqlite`).
pub fn open_store(dir: &Path) -> Result<Opened, OpenError> {
    let _ = std::fs::create_dir_all(dir);
    let hot = logstore::HotBuild::keyed_by(Some(country_resolver()));
    // `network: None` -- the folder is Jimmy Next's own, under LOCALAPPDATA or a test temp dir.
    logstore::open_reporting(&log_path(dir), store_resolve(), None, Some(hot), &mut |_| {})
}

impl LogHost {
    /// Nexus's `adopt_logbook`: make the opened store the owner of the engine's log, or -- when it
    /// could not be opened -- keep the log in log.adi for this session and say why. `Err` only when
    /// not even that works (the launch must stop).
    pub fn adopt(
        eng: &mut Engine,
        dir: &Path,
        opened: Result<Opened, OpenError>,
    ) -> Result<LogHost, String> {
        // Before the log is attached, so the worked-entity index is keyed the way the store built it.
        eng.set_dxcc_resolver_shared(country_resolver());
        let kind = match opened {
            Ok(opened) => {
                let detail = match opened.outcome {
                    tempo_core::logbook::migrate::Outcome::Empty => "new logbook".to_string(),
                    tempo_core::logbook::migrate::Outcome::AlreadyDone => "opened".to_string(),
                    tempo_core::logbook::migrate::Outcome::Converted { total, resumed_at, .. } => {
                        if resumed_at > 0 {
                            format!("{total} contacts converted from log.adi, resuming after {resumed_at}")
                        } else {
                            format!("{total} contacts converted from log.adi")
                        }
                    }
                };
                let _ = eng.attach_log_store(opened);
                StoreKind::Database { detail }
            }
            Err(e) => {
                let why = e.to_string();
                eng.set_log_path_resolved(log_path(dir), store_resolve())?;
                eng.note_log_store_problem(&e);
                StoreKind::FileOnly { why }
            }
        };
        Ok(LogHost {
            dir: dir.to_path_buf(),
            kind,
            writes: Mutex::new(WriteGate { closed: false }),
        })
    }
}

/// The RecordId Jimmy's request `req_id` creates. A function of the request alone, so every
/// re-send of one request names the same contact. FNV-1a-64, the hash Nexus uses for its own
/// provisional ids -- stable across builds and platforms, unlike std's hasher.
pub fn record_id_for_request(req_id: &str) -> RecordId {
    let nonce = req_id
        .bytes()
        .fold(0xcbf2_9ce4_8422_2325_u64, |h, b| (h ^ u64::from(b)).wrapping_mul(0x0000_0100_0000_01b3));
    RecordId::Minted { posid: JIMMY_POSID, nonce, seq: 1 }
}

/// LOG_QSO's argument.
#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LogQsoArgs {
    /// Jimmy's id for this request; the same on every re-send of it.
    pub req_id: String,
    /// The contact, in Nexus's own wire shape.
    pub qso: LoggedQso,
}

/// What LOG_QSO answers. `state`:
///   "saved"       -- in the log AND confirmed on disk.
///   "already"     -- this request was logged before (a re-send); on disk as far as a read can tell.
///   "unconfirmed" -- in the log in memory, NOT confirmed on disk (`why` says why). Jimmy keeps the
///                    request and must not announce "logged".
///   "duplicate"   -- Nexus refused it: the same station, band and mode within 5 minutes.
///   "closed"      -- the process is shutting down; nothing was changed. Re-send after restart.
#[derive(serde::Serialize, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct LogQsoReply {
    pub state: &'static str,
    pub id: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub why: Option<String>,
    /// On "duplicate": the contact already in the log that Nexus's rule matched, when a read can
    /// name it (same call spelling; a portable-call variant matches the rule but not this lookup).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub existing_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub existing_when_unix: Option<u64>,
}

impl LogQsoReply {
    fn new(state: &'static str, id: String, why: Option<String>) -> Self {
        LogQsoReply { state, id, why, existing_id: None, existing_when_unix: None }
    }
}

/// LOG_QSO: log one contact, idempotently, and answer only once it is on disk (or say it is not).
pub fn log_qso(host: &LogHost, engine: &Mutex<Engine>, args: LogQsoArgs) -> LogQsoReply {
    let id = record_id_for_request(&args.req_id);
    let id_text = id.to_string();
    let gate = host.writes.lock().unwrap_or_else(|e| e.into_inner());
    if gate.closed {
        return LogQsoReply::new("closed", id_text, None);
    }

    // A re-send? The row is looked up by id with the lock released (a plan reads the store, and
    // lays this process's own changes still on their way over it).
    let plan = lock(engine).log_plan();
    match plan.row(id) {
        Ok(Some(_)) => {
            let reads = lock(engine).log_store_reads();
            let why = match reads.read(DURABLE_WAIT, |_| Ok(())) {
                Ok(((), Freshness::Current)) => None,
                Ok(((), Freshness::Stale(why))) => Some(why),
                Err(e) => Some(e.to_string()),
            };
            let state = if why.is_none() { "already" } else { "unconfirmed" };
            return LogQsoReply::new(state, id_text, why);
        }
        Ok(None) => {}
        Err(e) => {
            return LogQsoReply::new(
                "unconfirmed",
                id_text,
                Some(format!("the logbook could not be read to check for a re-send: {e}")),
            )
        }
    }

    let mut rec: QsoRecord = args.qso.into();
    rec.id = Some(id);
    rec.extra.retain(|(k, _)| k != REQ_ID_FIELD);
    rec.extra.push((REQ_ID_FIELD.to_string(), args.req_id.clone()));
    rec.extra.sort();

    test_crash_point("before_log");
    let probe = rec.clone();
    let (outcome, durability) = lock(engine).with_log_tickets(|e| e.log_qso_for_sync(rec));
    // The receipts inside PendingSync are Nexus's per-contact durability for the Remote path; the
    // tickets collected above cover the same change, and are what is waited on below.
    if matches!(outcome, LogWriteOutcome::Duplicate) {
        // Nexus's own live rule refused it (plan decision D2: adopted as is). Name the contact
        // it matched, with Nexus's own predicate, read off the lock -- so Jimmy can say why.
        let mut reply = LogQsoReply::new("duplicate", id_text, None);
        let plan = lock(engine).log_plan();
        const DUPE_COLUMNS: tempo_core::logbook::sqlite::Narrow = tempo_core::logbook::sqlite::Narrow {
            columns: &["call", "band", "mode", "when_unix"],
            uploads: false,
        };
        if let Ok(Some(existing)) = plan.newest(&probe.call, DUPE_COLUMNS, |r| {
            tempo_core::logbook::dedup::is_recent_duplicate(r, &probe)
        }) {
            reply.existing_id = existing.id.map(|i| i.to_string());
            reply.existing_when_unix = Some(existing.when_unix);
        }
        return reply;
    }
    drop(gate);
    // Handed to Nexus, not yet confirmed on disk: whether it lands is a race with the writer.
    test_crash_point("after_log");
    // Nexus's `durable_command`: wait with every lock released.
    match durability.wait(DURABLE_WAIT) {
        Ok(()) => {
            // On disk, and the reply not yet sent.
            test_crash_point("after_save");
            LogQsoReply::new("saved", id_text, None)
        }
        Err(why) => LogQsoReply::new("unconfirmed", id_text, Some(why)),
    }
}

// ── Phase 3 crash testing ─────────────────────────────────────────────────────────────────
//
// TEST ONLY. main() arms this only for a logbook-only start (--no-radio) AND only when the
// environment variable JIMMY_TEST_CRASH_AT names a point -- so it can never fire in a radio
// session or in normal use. The spec is "<point>" or "<point>:<n>" (crash at the n-th time that
// point is reached, 1 = the first). The crash is a hard exit: no reply, no flush, no destructors
// -- nothing this process had not already put on disk survives it, exactly as with a real crash.

static TEST_CRASH: std::sync::OnceLock<(String, u32)> = std::sync::OnceLock::new();
static TEST_CRASH_SEEN: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);

/// Arm a crash point (see above). Called by main() only under --no-radio.
pub fn arm_test_crash(spec: &str) {
    let (point, n) = match spec.split_once(':') {
        Some((p, n)) => (p.trim().to_string(), n.trim().parse().unwrap_or(1)),
        None => (spec.trim().to_string(), 1),
    };
    eprintln!("jimmy-engine-host: TEST crash armed at {point} #{n}");
    let _ = TEST_CRASH.set((point, n));
}

fn test_crash_point(name: &str) {
    if let Some((point, n)) = TEST_CRASH.get() {
        if point == name {
            let seen = TEST_CRASH_SEEN.fetch_add(1, std::sync::atomic::Ordering::SeqCst) + 1;
            if seen == *n {
                eprintln!("jimmy-engine-host: TEST crash at {name} #{seen}");
                std::process::exit(86);
            }
        }
    }
}

/// What every other durable write answers. `state`:
///   "saved"       -- made AND confirmed on disk (`detail` says what it made).
///   "unconfirmed" -- made in the log, NOT confirmed on disk (`why`).
///   "changed"     -- refused: the row is no longer the version the edit key names; re-read it.
///   "gone"        -- refused: no such row.
///   "busy" / "error" -- nothing was changed (`why`).
///   "closed"      -- shutting down; nothing was changed.
#[derive(serde::Serialize, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct WriteReply {
    pub state: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub why: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub detail: Option<serde_json::Value>,
}

impl WriteReply {
    fn refused(state: &'static str, why: Option<String>) -> Self {
        WriteReply { state, why, detail: None }
    }
}

/// Nexus's `durable_command` tail: wait with every lock released, then say what happened.
fn after_durable(durability: logstore::Durability, detail: serde_json::Value) -> WriteReply {
    match durability.wait(DURABLE_WAIT) {
        Ok(()) => WriteReply { state: "saved", why: None, detail: Some(detail) },
        Err(why) => WriteReply { state: "unconfirmed", why: Some(why), detail: Some(detail) },
    }
}

fn refusal(r: tempo_app::station::RowRefusal) -> WriteReply {
    use tempo_app::station::RowRefusal;
    match r {
        RowRefusal::Gone => WriteReply::refused("gone", None),
        RowRefusal::Busy => WriteReply::refused("busy", Some(tempo_app::station::LOG_BUSY.to_string())),
        RowRefusal::Changed(_) => WriteReply::refused("changed", None),
        #[allow(unreachable_patterns)]
        _ => WriteReply::refused("error", Some("refused".into())),
    }
}

fn parse_id(id: &str) -> Result<RecordId, WriteReply> {
    id.parse::<RecordId>().map_err(|_| WriteReply::refused("gone", Some(format!("not a contact id: {id}"))))
}

/// LOG_EDIT's argument: the row, the edit key LOG_ROWS gave for it, and the contact as edited.
#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LogEditArgs {
    pub id: String,
    pub edit_key: String,
    pub qso: LoggedQso,
}

/// LOG_EDIT: Nexus's `logwrite::update_row` -- a correction, only while the row is still the
/// version `edit_key` names. Nexus keeps what the services said (confirmations, upload stamps)
/// through an edit (`LogOp::Edit`).
pub fn log_edit(host: &LogHost, engine: &Mutex<Engine>, args: LogEditArgs) -> WriteReply {
    let gate = host.writes.lock().unwrap_or_else(|e| e.into_inner());
    if gate.closed {
        return WriteReply::refused("closed", None);
    }
    let id = match parse_id(&args.id) {
        Ok(id) => id,
        Err(r) => return r,
    };
    let mut rec: QsoRecord = args.qso.into();
    rec.id = Some(id);
    let (made, durability) = tempo_app::logwrite::update_row(
        engine,
        id,
        &args.edit_key,
        |_| rec.clone(),
        |_, made| made.1.is_some(),
    );
    drop(gate);
    match made {
        Ok(Ok(Some(true))) => after_durable(durability, serde_json::json!({ "id": args.id })),
        Ok(Ok(_)) => WriteReply::refused("gone", None),
        Ok(Err(r)) => refusal(r),
        Err(e) => WriteReply::refused("error", Some(e)),
    }
}

/// LOG_DELETE's argument.
#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LogDeleteArgs {
    pub id: String,
    pub edit_key: String,
}

/// LOG_DELETE: `LogOp::Delete` through Nexus's `logwrite::change_ops`, only while the row is still
/// the version `edit_key` names. Local only -- never touches an online logbook.
pub fn log_delete(host: &LogHost, engine: &Mutex<Engine>, args: LogDeleteArgs) -> WriteReply {
    let gate = host.writes.lock().unwrap_or_else(|e| e.into_inner());
    if gate.closed {
        return WriteReply::refused("closed", None);
    }
    let id = match parse_id(&args.id) {
        Ok(id) => id,
        Err(r) => return r,
    };
    let (made, durability) = tempo_app::logwrite::change_ops(
        engine,
        id,
        Some(&args.edit_key),
        &[tempo_core::logbook::LogOp::Delete(id)],
        "delete_qso",
    );
    drop(gate);
    match made {
        Ok(Ok(_)) => after_durable(durability, serde_json::json!({ "id": args.id })),
        Ok(Err(r)) => refusal(r),
        Err(e) => WriteReply::refused("error", Some(e)),
    }
}

/// LOG_IMPORT / LOG_MERGE's argument: a file to read (a control line is capped at 8 KB, so the
/// ADIF text itself never rides the line). `kind` for LOG_MERGE: "lotw" | "qrz" | "eqsl".
#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LogFileArgs {
    pub path: String,
    #[serde(default)]
    pub kind: String,
}

fn read_file(path: &str) -> Result<String, WriteReply> {
    std::fs::read(path)
        .map(|b| String::from_utf8_lossy(&b).into_owned())
        .map_err(|e| WriteReply::refused("error", Some(format!("could not read {path}: {e}"))))
}

/// LOG_IMPORT: an ADIF file through Nexus's own import (`logwrite::import_adif`: its duplicate
/// rule, its merge of what a contact already has).
pub fn log_import(host: &LogHost, engine: &Mutex<Engine>, args: LogFileArgs) -> WriteReply {
    let text = match read_file(&args.path) {
        Ok(t) => t,
        Err(r) => return r,
    };
    let gate = host.writes.lock().unwrap_or_else(|e| e.into_inner());
    if gate.closed {
        return WriteReply::refused("closed", None);
    }
    let (made, durability) = tempo_app::logwrite::import_adif(engine, &text);
    drop(gate);
    match made {
        Ok((added, skipped, merged, total)) => after_durable(
            durability,
            serde_json::json!({ "added": added, "skipped": skipped, "merged": merged, "total": total }),
        ),
        Err(e) => WriteReply::refused("error", Some(e)),
    }
}

fn summary_json(s: &tempo_core::reconcile::ReconcileSummary) -> serde_json::Value {
    serde_json::json!({
        "matched": s.matched,
        "newlyConfirmed": s.newly_confirmed,
        "newlyConfirmedAny": s.newly_confirmed_any,
        "newlyCredited": s.newly_credited,
        "newlySubmitted": s.newly_submitted,
        "orphans": s.orphans.len(),
        // Nexus's own list of the confirmations that matched no logged contact (its reason for
        // each), so Jimmy can show and diagnose them -- read-only, as Nexus reported them.
        "unmatched": s.orphans.iter().map(|o| serde_json::json!({
            "call": o.call,
            "band": o.band,
            "mode": o.mode,
            "whenUnix": o.when_unix,
            "reason": o.reason,
        })).collect::<Vec<_>>(),
    })
}

/// LOG_MERGE: a confirmation download merged the way Nexus merges it -- `merge_lotw_report`,
/// `merge_eqsl_report`, or `merge_qrz_report` (which also adds contacts the log lacks, as a QRZ
/// download does in Jimmy today). Monotonic: never takes a confirmation away.
pub fn log_merge(host: &LogHost, engine: &Mutex<Engine>, args: LogFileArgs) -> WriteReply {
    let text = match read_file(&args.path) {
        Ok(t) => t,
        Err(r) => return r,
    };
    let gate = host.writes.lock().unwrap_or_else(|e| e.into_inner());
    if gate.closed {
        return WriteReply::refused("closed", None);
    }
    let reply = match args.kind.as_str() {
        "lotw" => match tempo_app::logwrite::merge_lotw_report(engine, &text) {
            (Ok(s), d) => Ok((summary_json(&s), d)),
            (Err(e), _) => Err(e),
        },
        "eqsl" => match tempo_app::logwrite::merge_eqsl_report(engine, &text) {
            (Ok(s), d) => Ok((summary_json(&s), d)),
            (Err(e), _) => Err(e),
        },
        // LoTW's own-QSO report (qso_qsl=no): uploads LoTW holds promoted to "accepted" --
        // Nexus's merge_lotw_own_echo, as the Nexus desktop's LoTW sync runs it.
        "lotw-own" => {
            let now = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_secs() as i64)
                .unwrap_or(0);
            match tempo_app::logwrite::merge_lotw_own_echo(engine, &text, now) {
                (Ok(n), d) => Ok((serde_json::json!({ "promoted": n }), d)),
                (Err(e), _) => Err(e),
            }
        }
        "qrz" => match tempo_app::logwrite::merge_qrz_report(engine, &text) {
            (Ok((added, s)), d) => {
                let mut v = summary_json(&s);
                v["added"] = serde_json::json!(added);
                Ok((v, d))
            }
            (Err(e), _) => Err(e),
        },
        other => Err(format!("unknown merge kind: {other}")),
    };
    drop(gate);
    match reply {
        Ok((detail, durability)) => after_durable(durability, detail),
        Err(e) => WriteReply::refused("error", Some(e)),
    }
}

/// LOG_STAMP_UPLOAD's argument: what an upload of one contact came to, in Nexus's own terms.
#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LogStampArgs {
    pub id: String,
    /// "lotw" | "eqsl" | "qrz" | "clublog" -- the services Nexus tracks.
    pub service: String,
    /// "pending" | "accepted" | "duplicate" | "rejected" | "authfail".
    pub outcome: String,
    pub when_unix: i64,
    #[serde(default)]
    pub detail: Option<String>,
}

/// LOG_STAMP_UPLOAD: Nexus's `logwrite::stamp_push` on the contact `id`.
pub fn log_stamp(host: &LogHost, engine: &Mutex<Engine>, args: LogStampArgs) -> WriteReply {
    use tempo_core::logbook::{UploadDetail, UploadOutcome, UploadService, UploadStatus};
    let service = match args.service.as_str() {
        "lotw" => UploadService::Lotw,
        "eqsl" => UploadService::Eqsl,
        "qrz" => UploadService::Qrz,
        "clublog" => UploadService::Clublog,
        other => return WriteReply::refused("error", Some(format!("Nexus does not track uploads to {other}"))),
    };
    let Some(outcome) = UploadOutcome::from_code(&args.outcome) else {
        return WriteReply::refused("error", Some(format!("not an upload outcome: {}", args.outcome)));
    };
    let detail = match args.detail.as_deref().filter(|d| !d.is_empty()) {
        None => None,
        Some(d) => match UploadDetail::from_code(d) {
            Some(d) => Some(d),
            None => return WriteReply::refused("error", Some(format!("not an upload detail: {d}"))),
        },
    };
    let id = match parse_id(&args.id) {
        Ok(id) => id,
        Err(r) => return r,
    };
    let gate = host.writes.lock().unwrap_or_else(|e| e.into_inner());
    if gate.closed {
        return WriteReply::refused("closed", None);
    }
    let plan = lock(engine).log_plan();
    let row = match plan.row(id) {
        Ok(Some(row)) => row,
        Ok(None) => return WriteReply::refused("gone", None),
        Err(e) => return WriteReply::refused("error", Some(e)),
    };
    let status = UploadStatus { outcome, when_unix: args.when_unix, detail };
    let (stamped, durability) = tempo_app::logwrite::stamp_push(engine, &row, service, status);
    drop(gate);
    if !stamped {
        return WriteReply::refused("gone", None);
    }
    after_durable(durability, serde_json::json!({ "id": args.id }))
}

/// LOG_ROWS's argument.
#[derive(serde::Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
pub struct LogRowsArgs {
    pub offset: usize,
    /// 0 = every row from `offset`.
    pub limit: usize,
}

/// LOG_ROWS: the log's contacts in log order, as Nexus reads them -- after waiting for every
/// change this process made before the question to be committed, and saying whether it was.
pub fn rows_json(engine: &Mutex<Engine>, args: &LogRowsArgs) -> String {
    let (reads, revision) = {
        let e = lock(engine);
        (e.log_store_reads(), e.log_revision())
    };
    match reads.rows(logstore::READ_WAIT) {
        Ok((rows, fresh)) => {
            let total = rows.len();
            let take = if args.limit == 0 { usize::MAX } else { args.limit };
            // Each row carries its edit key (Nexus's `QsoEdit::project(row).key()`): an edit or
            // delete sends it back, and Nexus refuses the change if the row moved on since.
            let page: Vec<serde_json::Value> = rows
                .into_iter()
                .skip(args.offset)
                .take(take)
                .map(|r| {
                    let key = tempo_core::logbook::QsoEdit::project(&r).key();
                    let mut v = serde_json::to_value(LoggedQso::from(r)).unwrap_or_default();
                    v["editKey"] = serde_json::Value::String(key);
                    v
                })
                .collect();
            let (freshness, why) = match fresh {
                Freshness::Current => ("current", None),
                Freshness::Stale(why) => ("stale", Some(why)),
            };
            serde_json::json!({
                "revision": revision,
                "freshness": freshness,
                "why": why,
                "total": total,
                "offset": args.offset,
                "rows": page,
            })
            .to_string()
        }
        Err(e) => serde_json::json!({ "error": e.to_string() }).to_string(),
    }
}

/// LOG_STATUS: what the store is and where the changes stand. Never waits. Also the poll that
/// drives Nexus's resend of refused changes in a logbook-only start (no SNAPSHOT poll there).
pub fn status_json(host: &LogHost, engine: &Mutex<Engine>) -> String {
    let mut e = lock(engine);
    // Nexus's own retry for a change the disk refused for a reason that can pass (the desktop
    // makes this call on its UI snapshot poll). No I/O.
    e.log_resend_due();
    let standing = e.log_unsaved().standing();
    let (store, detail) = match &host.kind {
        StoreKind::Database { detail } => ("database", detail.clone()),
        StoreKind::FileOnly { why } => ("file", why.clone()),
    };
    serde_json::json!({
        "enabled": true,
        "logDir": host.dir.display().to_string(),
        "store": store,
        "detail": detail,
        "revision": e.log_revision(),
        "pending": standing.pending,
        "retryable": standing.retryable,
        "refused": standing.refused,
        "reason": standing.reason.or(standing.retry_reason),
    })
    .to_string()
}

/// What a flush found: saved, or how much is still not on disk and why.
#[derive(serde::Serialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct FlushReply {
    pub saved: bool,
    pub pending: usize,
    pub retryable: usize,
    pub refused: usize,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub why: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub mirror: Option<String>,
}

/// LOG_FLUSH (while running) and the logbook half of SHUTDOWN: send again whatever the writer
/// gave up on for a reason that can pass, then wait -- with every lock released -- for the
/// writer and the log.adi mirror. Nexus's quit does the same (`flush_logbook`).
/// LOG_EXPORT: the whole logbook as ADIF, written by Nexus's own exporter
/// (`logexport::export_logbook`, the Nexus Logbook's Export: it waits briefly for this process's
/// changes, then writes what the store holds) to `path` (a temp file, then renamed into place).
/// The reply says how many recent changes the file lacks (`saving`: still on their way; `held`:
/// refused by the store) so Jimmy can say so.
pub fn log_export(engine: &Mutex<Engine>, args: LogFileArgs) -> serde_json::Value {
    let source = {
        let e = lock(engine);
        tempo_app::logexport::Source::of(&e)
    };
    match tempo_app::logexport::export_logbook(&source, "adif", None, None) {
        Ok(x) => {
            let tmp = format!("{}.part", args.path);
            let written = std::fs::write(&tmp, x.text.as_bytes()).and_then(|_| std::fs::rename(&tmp, &args.path));
            match written {
                Ok(()) => serde_json::json!({ "state": "saved", "saving": x.saving, "held": x.held, "bytes": x.text.len() }),
                Err(e) => {
                    let _ = std::fs::remove_file(&tmp);
                    serde_json::json!({ "state": "error", "why": format!("could not write {}: {e}", args.path) })
                }
            }
        }
        Err(e) => serde_json::json!({ "state": "error", "why": e }),
    }
}

pub fn flush(engine: &Mutex<Engine>, cap: Duration) -> FlushReply {
    let unsaved = {
        let mut e = lock(engine);
        e.log_resend_all();
        e.log_unsaved()
    };
    let standing = unsaved.wait(cap);
    let mirror = unsaved.write_mirror(cap);
    FlushReply {
        saved: standing.saved(),
        pending: standing.pending,
        retryable: standing.retryable,
        refused: standing.refused,
        why: standing.reason.or(standing.retry_reason),
        mirror,
    }
}

/// SHUTDOWN's logbook half: take no new write, then flush.
pub fn close(host: &LogHost, engine: &Mutex<Engine>, cap: Duration) -> FlushReply {
    host.writes.lock().unwrap_or_else(|e| e.into_inner()).closed = true;
    flush(engine, cap)
}

fn lock(engine: &Mutex<Engine>) -> std::sync::MutexGuard<'_, Engine> {
    engine.lock().unwrap_or_else(|e| e.into_inner())
}

#[cfg(test)]
mod tests {
    use super::*;

    struct TempDir(PathBuf);
    impl TempDir {
        fn new(tag: &str) -> Self {
            let p = std::env::temp_dir().join(format!(
                "jimmy-loghost-{tag}-{}-{}",
                std::process::id(),
                std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
            ));
            std::fs::create_dir_all(&p).unwrap();
            TempDir(p)
        }
    }
    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    fn launch(dir: &Path) -> (Arc<Mutex<Engine>>, LogHost) {
        let opened = open_store(dir);
        let mut eng = Engine::new("KB0UZT", "EN34", 0);
        let host = LogHost::adopt(&mut eng, dir, opened).expect("adopted");
        assert!(matches!(host.kind, StoreKind::Database { .. }), "the database owns the log");
        (Arc::new(Mutex::new(eng)), host)
    }

    fn qso(call: &str, when: u64, band: &str) -> LoggedQso {
        serde_json::from_value(serde_json::json!({
            "call": call, "grid": "FN31", "band": band, "freqMhz": 14.075, "mode": "FT8",
            "rstSent": "-10", "rstRcvd": "-12", "whenUnix": when, "timeKnown": true,
            "confirmed": false,
        }))
        .expect("a LoggedQso")
    }

    fn args(req: &str, q: LoggedQso) -> LogQsoArgs {
        LogQsoArgs { req_id: req.into(), qso: q }
    }

    fn rows(engine: &Mutex<Engine>) -> serde_json::Value {
        serde_json::from_str(&rows_json(engine, &LogRowsArgs::default())).unwrap()
    }

    const T0: u64 = 1_790_000_000;

    #[test]
    fn a_logged_contact_is_saved_readable_and_a_resend_is_not_logged_twice() {
        let d = TempDir::new("resend");
        let (eng, host) = launch(&d.0);
        let first = log_qso(&host, &eng, args("req-1", qso("W1AW", T0, "20m")));
        assert_eq!(first.state, "saved", "{first:?}");
        assert_eq!(first.id, record_id_for_request("req-1").to_string());

        // The reply was lost; Jimmy sends the same request again.
        let again = log_qso(&host, &eng, args("req-1", qso("W1AW", T0, "20m")));
        assert_eq!(again.state, "already", "{again:?}");
        assert_eq!(again.id, first.id);

        let v = rows(&eng);
        assert_eq!(v["freshness"], "current");
        assert_eq!(v["total"], 1, "one contact, not two");
        assert_eq!(v["rows"][0]["id"], first.id);
        assert!(v["rows"][0]["extra"].to_string().contains("req-1"));
    }

    fn first_row(engine: &Mutex<Engine>) -> serde_json::Value {
        rows(engine)["rows"][0].clone()
    }

    #[test]
    fn edit_delete_merge_and_stamp_are_durable_and_refuse_a_stale_edit() {
        let d = TempDir::new("writes");
        let (eng, host) = launch(&d.0);
        assert_eq!(log_qso(&host, &eng, args("e1", qso("W1AW", T0, "20m"))).state, "saved");
        let row = first_row(&eng);
        let id = row["id"].as_str().unwrap().to_string();
        let key = row["editKey"].as_str().unwrap().to_string();

        // Edit: the name changes, and the edit key moves on with it.
        let mut edited: LoggedQso = serde_json::from_value(row.clone()).unwrap();
        edited.name = Some("Hiram".into());
        let r = log_edit(&host, &eng, LogEditArgs { id: id.clone(), edit_key: key.clone(), qso: edited.clone() });
        assert_eq!(r.state, "saved", "{r:?}");
        assert_eq!(first_row(&eng)["name"], "Hiram");
        // The same edit sent with the OLD key is refused, not applied over the newer row.
        let stale = log_edit(&host, &eng, LogEditArgs { id: id.clone(), edit_key: key.clone(), qso: edited });
        assert_eq!(stale.state, "changed", "{stale:?}");

        // Upload stamp in Nexus's own terms, then read back.
        let s = log_stamp(&host, &eng, LogStampArgs {
            id: id.clone(), service: "qrz".into(), outcome: "accepted".into(), when_unix: 1_790_000_100, detail: None,
        });
        assert_eq!(s.state, "saved", "{s:?}");
        assert_eq!(first_row(&eng)["upload"]["qrz"]["outcome"], "accepted");
        let bad = log_stamp(&host, &eng, LogStampArgs {
            id: id.clone(), service: "hrdlog".into(), outcome: "accepted".into(), when_unix: 0, detail: None,
        });
        assert_eq!(bad.state, "error", "Nexus tracks no HRDLog upload state");

        // A LoTW confirmation merged from a report file: LoTW's channel, never a paper card.
        let report = d.0.join("lotw.adi");
        // LoTW's own report shape: Nexus reads QSL_RCVD as LoTW's only under LoTW's header
        // (`lotw_channel_fixup`) -- Jimmy must hand over the download exactly as received.
        std::fs::write(&report, "ARRL Logbook of the World Status Report\n<PROGRAMID:4>LoTW\n\
            <APP_LoTW_NUMREC:1>1\n<eoh>\n<CALL:4>W1AW <BAND:3>20m <MODE:3>FT8 <QSO_DATE:8>20260921 \
            <TIME_ON:6>134000 <QSL_RCVD:1>Y <eor>\n").unwrap();
        let m = log_merge(&host, &eng, LogFileArgs { path: report.display().to_string(), kind: "lotw".into() });
        assert_eq!(m.state, "saved", "{m:?}");
        let after = first_row(&eng);
        assert_eq!(after["qslRcvd"]["lotw"], true, "{after}");
        assert_eq!(after["qslRcvd"]["card"], false);

        // Delete with the current key.
        let key_now = after["editKey"].as_str().unwrap().to_string();
        let del = log_delete(&host, &eng, LogDeleteArgs { id, edit_key: key_now });
        assert_eq!(del.state, "saved", "{del:?}");
        assert_eq!(rows(&eng)["total"], 0);
    }

    #[test]
    fn nexus_refuses_a_real_duplicate_but_keeps_a_different_band() {
        let d = TempDir::new("dupe");
        let (eng, host) = launch(&d.0);
        assert_eq!(log_qso(&host, &eng, args("a", qso("W1AW", T0, "20m"))).state, "saved");
        // A different request for the same station, band and mode two minutes later.
        let dup = log_qso(&host, &eng, args("b", qso("W1AW", T0 + 120, "20m")));
        assert_eq!(dup.state, "duplicate");
        assert_eq!(dup.existing_id, Some(record_id_for_request("a").to_string()), "names the contact it matched");
        assert_eq!(dup.existing_when_unix, Some(T0));
        // The same station on another band is a new contact.
        assert_eq!(log_qso(&host, &eng, args("c", qso("W1AW", T0 + 120, "40m"))).state, "saved");
        assert_eq!(rows(&eng)["total"], 2);
    }

    #[test]
    fn after_a_graceful_close_a_restart_finds_the_contact_and_the_resend_is_recognised() {
        let d = TempDir::new("restart");
        let id = {
            let (eng, host) = launch(&d.0);
            let r = log_qso(&host, &eng, args("req-9", qso("K1ABC", T0, "15m")));
            assert_eq!(r.state, "saved");
            let closed = close(&host, &eng, Duration::from_secs(10));
            assert!(closed.saved, "{closed:?}");
            assert_eq!(log_qso(&host, &eng, args("req-10", qso("N0CALL", T0, "15m"))).state, "closed");
            r.id
        };
        assert!(log_path(&d.0).exists(), "the log.adi mirror is written");
        let (eng, host) = launch(&d.0);
        let v = rows(&eng);
        assert_eq!(v["total"], 1, "the closed request was never logged");
        assert_eq!(v["rows"][0]["id"], id);
        assert_eq!(log_qso(&host, &eng, args("req-9", qso("K1ABC", T0, "15m"))).state, "already");
    }
}
