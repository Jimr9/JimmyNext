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
        // A contact keeps its country and state as logged or imported, blanks included -- an
        // import, a merge-added contact, an upload stamp or an edit never fills them (operator,
        // 2026-10-04: no automatic enrichment). Live contacts keep Nexus's country fill. The
        // worked-entity index is keyed by the resolver above, not by the stored country.
        eng.set_keep_logged_location(true);
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

/// Nexus 1.16.0 (two windows on one log): the store turns a change back when another writer
/// changed a row after this change read it, and every command that waits for its change makes it
/// through `logwrite::until_written`, which makes it again on the rows as they now stand (up to
/// Nexus's own PLANS). Every write below does the same (2026-10-04) -- made once, a turned-back
/// change came back to Jimmy as "not saved". A retried edit still names its edit key, so a row
/// someone else really changed is refused as stale, never overwritten.
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
    let (made, durability) = tempo_app::logwrite::until_written(|| tempo_app::logwrite::update_row(
        engine,
        id,
        &args.edit_key,
        |old| {
            let mut r = rec.clone();
            fold_contest_tags(&mut r, old);
            r
        },
        |_, made| made.1.is_some(),
    ));
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
    let (made, durability) = tempo_app::logwrite::until_written(|| tempo_app::logwrite::change_ops(
        engine,
        id,
        Some(&args.edit_key),
        &[tempo_core::logbook::LogOp::Delete(id)],
        "delete_qso",
    ));
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
    let (made, durability) = tempo_app::logwrite::until_written(|| tempo_app::logwrite::import_adif(engine, &text));
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
        "lotw" => match tempo_app::logwrite::until_written(|| tempo_app::logwrite::merge_lotw_report(engine, &text)) {
            (Ok(s), d) => Ok((summary_json(&s), d)),
            (Err(e), _) => Err(e),
        },
        "eqsl" => match tempo_app::logwrite::until_written(|| tempo_app::logwrite::merge_eqsl_report(engine, &text)) {
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
            match tempo_app::logwrite::until_written(|| tempo_app::logwrite::merge_lotw_own_echo(engine, &text, now)) {
                (Ok(n), d) => Ok((serde_json::json!({ "promoted": n }), d)),
                (Err(e), _) => Err(e),
            }
        }
        "qrz" => match tempo_app::logwrite::until_written(|| tempo_app::logwrite::merge_qrz_report(engine, &text)) {
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

/// LOG_SET_EXTRA's argument: Jimmy's own APP_JIMMY_ fields to set on one contact.
#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LogSetExtraArgs {
    pub id: String,
    pub set: Vec<(String, String)>,
}

/// LOG_SET_EXTRA (2026-10-04): Jimmy's own `APP_JIMMY_*` fields on a contact -- e.g. the location
/// LoTW confirmed, kept apart from the logged location. Only that namespace: a logged location,
/// confirmation or upload field can never be written here. Made like every waited write
/// (`until_written`), on the row as it stands, stale-safe by its edit key.
pub fn log_set_extra(host: &LogHost, engine: &Mutex<Engine>, args: LogSetExtraArgs) -> WriteReply {
    if args.set.iter().any(|(k, _)| !k.to_ascii_uppercase().starts_with("APP_JIMMY_")) {
        return WriteReply::refused("error", Some("only APP_JIMMY_ fields may be set".into()));
    }
    let id = match parse_id(&args.id) {
        Ok(id) => id,
        Err(r) => return r,
    };
    let gate = host.writes.lock().unwrap_or_else(|e| e.into_inner());
    if gate.closed {
        return WriteReply::refused("closed", None);
    }
    let (made, durability) = tempo_app::logwrite::until_written(|| {
        let plan = lock(engine).log_plan();
        let key = match plan.row(id) {
            Ok(Some(row)) => tempo_core::logbook::QsoEdit::project(&row).key(),
            _ => String::new(),
        };
        tempo_app::logwrite::update_row(
            engine,
            id,
            &key,
            |r| {
                let mut rec = r.clone();
                for (k, v) in &args.set {
                    rec.extra.retain(|(t, _)| !t.eq_ignore_ascii_case(k));
                    if !v.is_empty() {
                        rec.extra.push((k.to_ascii_uppercase(), v.clone()));
                    }
                }
                rec
            },
            |_, made| made.1.is_some(),
        )
    });
    drop(gate);
    match made {
        Ok(Ok(Some(true))) => after_durable(durability, serde_json::json!({ "id": args.id })),
        Ok(Ok(_)) => WriteReply::refused("gone", None),
        Ok(Err(r)) => refusal(r),
        Err(e) => WriteReply::refused("error", Some(e)),
    }
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
    let (stamped, durability) = tempo_app::logwrite::until_written(|| tempo_app::logwrite::stamp_push(engine, &row, service, status.clone()));
    drop(gate);
    if !stamped {
        return WriteReply::refused("gone", None);
    }
    after_durable(durability, serde_json::json!({ "id": args.id }))
}

/// TEST ONLY: a local fake server LOG_UPLOAD posts to instead of the services, set only in a
/// logbook-only start (--no-radio) with JIMMY_TEST_UPLOAD_BASE (an http://127.0.0.1:PORT). Nexus's
/// transports accept HTTPS only (by design), so the fake is reached by `test_post` below; every
/// other step -- record, request body, answer classification, stamp -- is the production one. A
/// real Jimmy never sets it and always uses Nexus's own transports and service addresses.
static UPLOAD_TEST_BASE: std::sync::OnceLock<String> = std::sync::OnceLock::new();

pub fn set_upload_test_base(base: &str) {
    let _ = UPLOAD_TEST_BASE.set(base.trim_end_matches('/').to_string());
}

/// TEST ONLY: one HTTP/1.1 POST to the fake server ("http://127.0.0.1:PORT" + path). Returns
/// (status, body); a closed connection with no answer is an Err, like a transport failure.
fn test_post(base: &str, path: &str, body: String) -> Result<(u16, String), String> {
    use std::io::{Read, Write};
    let host = base.trim_start_matches("http://");
    let mut s = std::net::TcpStream::connect(host).map_err(|e| format!("test: connect failed: {e}"))?;
    s.set_read_timeout(Some(Duration::from_secs(20))).ok();
    let req = format!(
        "POST {path} HTTP/1.1\r\nHost: {host}\r\nContent-Type: application/x-www-form-urlencoded\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
        body.len()
    );
    s.write_all(req.as_bytes()).map_err(|e| format!("test: send failed: {e}"))?;
    let mut raw = Vec::new();
    s.read_to_end(&mut raw).map_err(|e| format!("test: request failed: {e}"))?;
    let text = String::from_utf8_lossy(&raw).to_string();
    let status = text.split(' ').nth(1).and_then(|c| c.parse::<u16>().ok()).ok_or("test: request failed")?;
    let body = text.split_once("\r\n\r\n").map(|(_, b)| b.to_string()).unwrap_or_default();
    Ok((status, body))
}

fn send_qrz(body: String) -> Result<String, String> {
    match UPLOAD_TEST_BASE.get() {
        Some(base) => test_post(base, "/qrz", body).map(|(_, b)| b),
        None => propagation::live::qrz::post_form(tempo_core::qrz::QRZ_LOGBOOK_URL, body),
    }
}

fn send_clublog(body: String) -> Result<(u16, String), String> {
    match UPLOAD_TEST_BASE.get() {
        Some(base) => test_post(base, "/clublog", body),
        None => propagation::live::clublog::push_realtime(tempo_core::clublog::CLUBLOG_REALTIME_URL, body),
    }
}

fn send_hrdlog(body: String) -> Result<String, String> {
    match UPLOAD_TEST_BASE.get() {
        Some(base) => test_post(base, "/hrdlog", body).map(|(_, b)| b),
        None => propagation::live::hrdlog::post_form(tempo_core::hrdlog::HRDLOG_NEWENTRY_URL, env!("CARGO_PKG_VERSION"), body),
    }
}

/// The tag that records an HRDLog.net upload on a contact: Nexus's own upload state has no HRDLog
/// service, so the time is kept in the contact's own record (its extra ADIF fields -- the same tag
/// Jimmy's logbook carried over at the move), stored and saved by Nexus like any other field.
pub const HRDLOG_UPLOADED_TAG: &str = "APP_JIMMY_HRDLOG_UL";

/// UTC seconds as ISO 8601 ("2026-09-28T15:04:05Z"), the form Jimmy reads the tag in.
fn iso_utc(secs: i64) -> String {
    let days = secs.div_euclid(86_400);
    let rem = secs.rem_euclid(86_400);
    // Civil date from days since 1970-01-01 (Howard Hinnant's algorithm).
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1_460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let y = yoe + era * 400 + if m <= 2 { 1 } else { 0 };
    format!("{y:04}-{m:02}-{d:02}T{:02}:{:02}:{:02}Z", rem / 3600, rem / 60 % 60, rem % 60)
}

/// Record an HRDLog upload on contact `id`: its HRDLOG_UPLOADED_TAG set to `when`, through Nexus's
/// own edit (`logwrite::update_row` -- the row as it stands, stale-safe by its edit key; an
/// ordinary edit keeps every confirmation and upload state). A few tries if a sync moved the row
/// on meanwhile. Returns whether it was recorded.
fn record_hrdlog_upload(host: &LogHost, engine: &Mutex<Engine>, id: tempo_core::logbook::RecordId, when: i64) -> bool {
    for _ in 0..3 {
        let plan = lock(engine).log_plan();
        let Ok(Some(row)) = plan.row(id) else { return false };
        let key = tempo_core::logbook::QsoEdit::project(&row).key();
        let stamp = iso_utc(when);
        let gate = host.writes.lock().unwrap_or_else(|e| e.into_inner());
        if gate.closed {
            return false;
        }
        let (made, durability) = tempo_app::logwrite::until_written(|| tempo_app::logwrite::update_row(
            engine,
            id,
            &key,
            |r| {
                let mut rec = r.clone();
                rec.extra.retain(|(k, _)| !k.eq_ignore_ascii_case(HRDLOG_UPLOADED_TAG));
                rec.extra.push((HRDLOG_UPLOADED_TAG.to_string(), stamp.clone()));
                rec
            },
            |_, made| made.1.is_some(),
        ));
        drop(gate);
        match made {
            Ok(Ok(Some(true))) => return durability.wait(DURABLE_WAIT).is_ok(),
            Ok(Err(_)) => continue, // stale edit key: the row moved on; read it again
            _ => return false,
        }
    }
    false
}

fn send_eqsl(body: String) -> Result<String, String> {
    match UPLOAD_TEST_BASE.get() {
        Some(base) => test_post(base, "/eqsl", body).map(|(_, b)| b),
        None => propagation::live::eqsl::post_form(tempo_core::eqsl::EQSL_IMPORT_URL, body),
    }
}

/// LOG_UPLOAD's argument: one contact (Nexus id), one service, and that service's credentials,
/// which arrive with the request and live only as long as it (never stored or logged here).
#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LogUploadArgs {
    pub id: String,
    /// "qrz" | "clublog" | "eqsl"
    pub service: String,
    #[serde(default)]
    pub qrz_key: String,
    #[serde(default)]
    pub clublog_email: String,
    #[serde(default)]
    pub clublog_password: String,
    #[serde(default)]
    pub clublog_callsign: String,
    #[serde(default)]
    pub clublog_app_key: String,
    #[serde(default)]
    pub eqsl_username: String,
    #[serde(default)]
    pub eqsl_password: String,
    #[serde(default)]
    pub hrdlog_callsign: String,
    #[serde(default)]
    pub hrdlog_code: String,
}

/// LOG_UPLOAD: one contact to one service, the way the Nexus desktop's connector push does it --
/// the record as Nexus writes it for a service (`logbook::adif_record`), Nexus's request builder
/// and transport, Nexus's classification of the answer, and Nexus's per-QSO stamp
/// (`logwrite::stamp_push`) of accepted / duplicate / rejected / authfail. A transport failure or an
/// answer Nexus does not classify (Club Log busy) stamps nothing, so the contact stays owed.
/// Reply: {"state":"stamped","outcome":...,"detail":...,"message":...} or {"state":"unsent","why":...}.
pub fn log_upload(host: &LogHost, engine: &Mutex<Engine>, args: LogUploadArgs) -> serde_json::Value {
    use tempo_core::logbook::{UploadService, UploadStatus};
    let id = match parse_id(&args.id) {
        Ok(id) => id,
        Err(_) => return serde_json::json!({ "state": "unsent", "why": format!("not a contact id: {}", args.id) }),
    };
    // The plan is taken under the Engine lock and read with it released (a store read).
    let plan = lock(engine).log_plan();
    let row = match plan.row(id) {
        Ok(Some(row)) => row,
        Ok(None) => return serde_json::json!({ "state": "unsent", "why": "no such contact" }),
        Err(e) => return serde_json::json!({ "state": "unsent", "why": e }),
    };
    let adif = tempo_core::logbook::adif_record(&row);
    let (service, outcome, detail, message) = match args.service.as_str() {
        "qrz" => {
            if args.qrz_key.trim().is_empty() {
                return serde_json::json!({ "state": "unsent", "why": "no QRZ Logbook API key" });
            }
            let body = tempo_core::qrz::build_insert_body(args.qrz_key.trim(), &adif, false);
            let resp = match send_qrz(body) {
                Ok(r) => r,
                Err(e) => return serde_json::json!({ "state": "unsent", "why": e }),
            };
            let push = tempo_core::qrz::parse_push_response(&resp);
            (UploadService::Qrz, Some(push.result.to_upload_outcome()), push.result.to_upload_detail(), push.reason)
        }
        "clublog" => {
            let query = tempo_core::clublog::ClubLogQuery {
                email: args.clublog_email.trim().to_string(),
                password: args.clublog_password.clone(),
                callsign: args.clublog_callsign.trim().to_string(),
                api_key: args.clublog_app_key.trim().to_string(),
                adif,
            };
            let body = tempo_core::clublog::build_realtime_body(&query);
            let (status, resp) = match send_clublog(body) {
                Ok(r) => r,
                Err(e) => return serde_json::json!({ "state": "unsent", "why": e }),
            };
            let push = tempo_core::clublog::classify_response(status, &resp);
            (UploadService::Clublog, push.result.to_upload_outcome(), push.result.to_upload_detail(), push.message)
        }
        "eqsl" => {
            let body = tempo_core::eqsl::build_upload_body(&args.eqsl_username, &args.eqsl_password, &adif, None);
            let html = match send_eqsl(body) {
                Ok(h) => h,
                Err(e) => return serde_json::json!({ "state": "unsent", "why": e }),
            };
            (UploadService::Eqsl, tempo_core::eqsl::classify_upload(&html), None, None)
        }
        "hrdlog" => {
            let code = args.hrdlog_code.trim().to_string();
            if code.is_empty() || args.hrdlog_callsign.trim().is_empty() {
                return serde_json::json!({ "state": "unsent", "why": "no HRDLog.net callsign or upload code" });
            }
            let query = tempo_core::hrdlog::HrdLogQuery {
                callsign: args.hrdlog_callsign.trim().to_string(),
                code: code.clone(),
                app: "Jimmy".to_string(), // what Jimmy has always sent HRDLog as its App
                adif,
            };
            let body = tempo_core::hrdlog::build_upload_body(&query);
            let resp = match send_hrdlog(body) {
                Ok(r) => r,
                Err(e) => return serde_json::json!({ "state": "unsent", "why": e }),
            };
            let push = tempo_core::hrdlog::classify_response(&resp);
            let message = push.message.map(|m| tempo_core::hrdlog::scrub_code(&m, &code));
            use tempo_core::hrdlog::HrdLogResult as H;
            let code_str = match push.result {
                H::Ok => "accepted",
                H::Duplicate => "duplicate",
                H::AuthFail => "authfail",
                H::Rejected => "rejected",
                H::Unknown => return serde_json::json!({ "state": "unsent", "why": message.unwrap_or_else(|| "HRDLog did not answer as expected (busy?)".into()) }),
            };
            // Nexus has no HRDLog upload state: an upload HRDLog holds is recorded on the contact's
            // own record; a refusal records nothing (the contact stays owed), as Jimmy always did.
            let held = matches!(push.result, H::Ok | H::Duplicate);
            let now = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_secs() as i64)
                .unwrap_or(0);
            let recorded = held && record_hrdlog_upload(host, engine, id, now);
            return serde_json::json!({
                "state": if recorded { "stamped" } else if held { "sent-not-stamped" } else { "refused" },
                "outcome": code_str,
                "message": message,
            });
        }
        other => return serde_json::json!({ "state": "unsent", "why": format!("Nexus does not upload to {other} here") }),
    };
    let Some(outcome) = outcome else {
        return serde_json::json!({ "state": "unsent", "why": message.unwrap_or_else(|| "the service's answer was not recognised (busy?)".into()) });
    };
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0);
    let gate = host.writes.lock().unwrap_or_else(|e| e.into_inner());
    if gate.closed {
        return serde_json::json!({ "state": "sent-not-stamped", "outcome": outcome.code(), "why": "logbook closing" });
    }
    let status = UploadStatus { outcome, when_unix: now, detail };
    let (stamped, durability) = tempo_app::logwrite::until_written(|| tempo_app::logwrite::stamp_push(engine, &row, service, status.clone()));
    drop(gate);
    let durable = durability.wait(DURABLE_WAIT).is_ok();
    serde_json::json!({
        "state": if stamped { "stamped" } else { "sent-not-stamped" },
        "outcome": outcome.code(),
        "detail": detail.map(|d| d.code()),
        "message": message,
        "durable": durable,
    })
}

/// The contest block's own ADIF tags (Nexus's `contest_fields`). Nexus's desktop contact shape
/// (`LoggedQso`) carries no contest block, so a contact imported with a contest exchange --
/// WSJT-X writes Field Day's received exchange as `SRX_STRING` -- read back with it blank in
/// Jimmy, though Nexus kept it (W0CAS's VO1VON, 2026-10-01). LOG_ROWS adds them to `extra`;
/// LOG_EDIT folds them back, so an edit neither stores them twice nor loses a changed value.
const CONTEST_TAGS: [&str; 5] = ["CONTEST_ID", "STX", "STX_STRING", "SRX", "SRX_STRING"];

/// The contest block's tags with a value -- except one the record's own `extra` already
/// carries (a contact Jimmy logged keeps its exchange there).
fn contest_tags(r: &QsoRecord) -> Vec<(String, String)> {
    let Some(c) = r.contest.as_deref() else { return Vec::new() };
    let values = [
        c.contest_id.clone(),
        c.stx.map(|n| n.to_string()).unwrap_or_default(),
        c.stx_string.clone().unwrap_or_default(),
        c.srx.map(|n| n.to_string()).unwrap_or_default(),
        c.srx_string.clone().unwrap_or_default(),
    ];
    CONTEST_TAGS
        .iter()
        .zip(values)
        .filter(|(tag, v)| !v.is_empty() && !r.extra.iter().any(|(k, _)| k.eq_ignore_ascii_case(tag)))
        .map(|(tag, v)| (tag.to_string(), v))
        .collect()
}

/// LOG_EDIT: the tags [`contest_tags`] added come back in the edited contact's `extra`. Each one
/// the stored contact keeps in its contest block goes back there, with the edited value (none =
/// cleared); a tag the stored contact keeps in `extra` stays in `extra`. Nexus's `update_record`
/// keeps the rest of the block (session, both exchange vectors, QID) because it is cloned here.
fn fold_contest_tags(rec: &mut QsoRecord, old: &QsoRecord) {
    let Some(stored) = old.contest.as_deref() else { return };
    let in_block = |tag: &str| !old.extra.iter().any(|(k, _)| k.eq_ignore_ascii_case(tag));
    let mut c = stored.clone();
    let mut take = |tag: &str| -> Option<String> {
        let at = rec.extra.iter().position(|(k, _)| k.eq_ignore_ascii_case(tag))?;
        Some(rec.extra.remove(at).1.trim().to_string()).filter(|v| !v.is_empty())
    };
    if in_block("CONTEST_ID") {
        c.contest_id = take("CONTEST_ID").unwrap_or_default();
    }
    if in_block("STX_STRING") {
        c.stx_string = take("STX_STRING");
    }
    if in_block("SRX_STRING") {
        c.srx_string = take("SRX_STRING");
    }
    // A serial that is not a number stays in `extra` as typed rather than being dropped.
    let mut not_numbers = Vec::new();
    for (tag, slot) in [("STX", &mut c.stx), ("SRX", &mut c.srx)] {
        if !in_block(tag) {
            continue;
        }
        *slot = None;
        if let Some(v) = take(tag) {
            match v.parse() {
                Ok(n) => *slot = Some(n),
                Err(_) => not_numbers.push((tag.to_string(), v)),
            }
        }
    }
    rec.extra.extend(not_numbers);
    rec.contest = Some(Box::new(c));
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
                    let contest = contest_tags(&r);
                    let mut v = serde_json::to_value(LoggedQso::from(r)).unwrap_or_default();
                    v["editKey"] = serde_json::Value::String(key);
                    if let Some(extra) = v["extra"].as_array_mut() {
                        extra.extend(contest.into_iter().map(|(k, val)| serde_json::json!([k, val])));
                    }
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
    fn an_imported_contest_exchange_reads_back_and_survives_or_changes_with_an_edit() {
        let d = TempDir::new("contest");
        let (eng, host) = launch(&d.0);
        // WSJT-X's Field Day contact: the received exchange, no CONTEST_ID (W0CAS's VO1VON).
        let adi = d.0.join("fd.adi");
        std::fs::write(&adi, "<EOH>\n<CALL:6>VO1VON <BAND:3>20m <MODE:3>FT8 <QSO_DATE:8>20230624 \
            <TIME_ON:6>180000 <SRX_STRING:5>2A NL <EOR>\n").unwrap();
        let im = log_import(&host, &eng, LogFileArgs { path: adi.display().to_string(), kind: String::new() });
        assert_eq!(im.state, "saved", "{im:?}");
        let row = first_row(&eng);
        let extra = row["extra"].to_string();
        assert!(extra.contains("SRX_STRING") && extra.contains("2A NL"), "read back: {row}");
        assert!(!extra.contains("CONTEST_ID"), "an empty tag is not invented: {row}");

        // An ordinary edit sends the row back as read: stored once, in the contest block.
        let id = row["id"].as_str().unwrap().to_string();
        let mut edited: LoggedQso = serde_json::from_value(row.clone()).unwrap();
        edited.name = Some("Joel".into());
        let key = row["editKey"].as_str().unwrap().to_string();
        assert_eq!(log_edit(&host, &eng, LogEditArgs { id: id.clone(), edit_key: key, qso: edited }).state, "saved");
        let reads = lock(&eng).log_store_reads();
        let stored = reads.rows(logstore::READ_WAIT).expect("rows").0[0].clone();
        assert!(!stored.extra.iter().any(|(k, _)| k == "SRX_STRING"), "not stored twice: {:?}", stored.extra);
        assert_eq!(stored.contest.as_deref().and_then(|c| c.srx_string.clone()).as_deref(), Some("2A NL"));

        // Changing the exchange changes it.
        let row = first_row(&eng);
        let mut fix: LoggedQso = serde_json::from_value(row.clone()).unwrap();
        for kv in fix.extra.iter_mut() {
            if kv.0 == "SRX_STRING" {
                kv.1 = "3A NL".into();
            }
        }
        let key = row["editKey"].as_str().unwrap().to_string();
        assert_eq!(log_edit(&host, &eng, LogEditArgs { id, edit_key: key, qso: fix }).state, "saved");
        assert!(first_row(&eng)["extra"].to_string().contains("3A NL"));
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
