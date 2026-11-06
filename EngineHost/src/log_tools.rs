//! LOG_TOOL (2026-10-31, Nexus 1.21): the Logbook's clean-up tools, its history and its import
//! preview, as Nexus itself makes them -- `tempo_app::logtools` (bulk edit, the duplicate finder
//! and its merge, the check and its fixes), `tempo_app::loghistory` (each contact's history, undo,
//! Recently deleted, restore) and `tempo_core::logbook::import` (the preview, ADX and Cabrillo,
//! the no-loss report). Nothing here decides anything of its own: each op reads its arguments,
//! calls Nexus's function and words the answer as JSON for Jimmy Next.
//!
//! One command, `LOG_TOOL {"op": ..., ...}`. A control line is capped at 8 KB, so a list of
//! contacts or merges rides in a file (`idsPath`, one Nexus id per line; `asksPath` /
//! `fixesPath`, JSON), as LOTW_BATCH_PREPARE's ids do.
//!
//! Every change is one run of Nexus's history: its answer names the `run`, and `undoRun` on it
//! takes the whole change back. Writes go through the logbook's write gate (refused once
//! SHUTDOWN has begun) and answer only once the change is on disk, as every LOG_* write does.

use std::collections::HashMap;
use std::sync::Mutex;

use serde_json::{json, Value};
use tempo_app::engine::Engine;
use tempo_app::logstore::Durability;
use tempo_app::logtools::{self, DupeTier, Fix, MergeAsk, MovesAck, ToolRun};
use tempo_app::loghistory::{self, Undone};
use tempo_core::logbook::cleanup::{Change, Field};
use tempo_core::logbook::history::{content_key, CannotUndo, Entry};
use tempo_core::logbook::{import, QsoRecord, RecordId};

use crate::logbook_host::{after_durable_json, gated, LogHost};

#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase")]
struct Args {
    op: String,
    #[serde(default)]
    ids_path: Option<String>,
    #[serde(default)]
    asks_path: Option<String>,
    #[serde(default)]
    fixes_path: Option<String>,
    #[serde(default)]
    path: Option<String>,
    #[serde(default)]
    changes: Vec<Change>,
    #[serde(default)]
    expect: usize,
    #[serde(default)]
    moves: usize,
    #[serde(default)]
    move_to: Option<String>,
    #[serde(default)]
    possible: bool,
    #[serde(default)]
    id: Option<String>,
    #[serde(default)]
    seq: Option<i64>,
    #[serde(default)]
    run: Option<String>,
    #[serde(default)]
    anyway: bool,
    #[serde(default)]
    new_only: bool,
    #[serde(default)]
    limit: Option<usize>,
    #[serde(default)]
    answers_path: Option<String>,
    #[serde(default)]
    out_path: Option<String>,
}

/// LOG_TOOL: the answer, one line of JSON. `state`: "ok" for a read; "saved" / "unconfirmed"
/// for a change (as every LOG_* write); "error" (`why`) when nothing was done.
pub fn run(host: &LogHost, engine: &Mutex<Engine>, json_args: &str) -> String {
    let args: Args = match serde_json::from_str(json_args) {
        Ok(a) => a,
        Err(e) => return error(format!("bad LOG_TOOL args: {e}")),
    };
    let answer = match args.op.as_str() {
        "bulkPreview" => bulk_preview(engine, &args),
        "bulkEdit" => write(host, || bulk_edit(engine, &args)),
        "dupes" => dupes(engine, &args),
        "merge" => write(host, || merge(engine, &args)),
        "check" => check(engine),
        "fix" => write(host, || fix(engine, &args)),
        "history" => history(engine, &args),
        "deleted" => deleted(engine, &args),
        "runs" => runs(engine, &args),
        "undoEntry" => write(host, || match args.seq {
            Some(seq) => undone(loghistory::undo_entry(engine, seq)),
            None => (Err("no change named".into()), Durability::default()),
        }),
        "undoRun" => write(host, || match args.run.as_deref() {
            Some(run) => undone(loghistory::undo_run(engine, run)),
            None => (Err("no change named".into()), Durability::default()),
        }),
        "restore" => write(host, || match parse_id(args.id.as_deref()) {
            Ok(id) => undone(if args.anyway {
                loghistory::restore_deleted_anyway(engine, id)
            } else {
                loghistory::restore_deleted(engine, id)
            }),
            Err(e) => (Err(e), Durability::default()),
        }),
        "callbookPreview" => callbook_preview(engine, &args),
        "callbookFill" => write(host, || callbook_fill(engine, &args)),
        "importPreview" => import_preview(engine, &args),
        "toAdif" => to_adif(&args),
        "import" => write(host, || import_file(engine, &args)),
        other => Err(format!("unknown LOG_TOOL op {other:?}")),
    };
    match answer {
        Ok(v) => v.to_string(),
        Err(why) => error(why),
    }
}

fn error(why: String) -> String {
    json!({ "state": "error", "why": why }).to_string()
}

/// A change: made through the write gate, then answered once it is on disk.
fn write(
    host: &LogHost,
    make: impl FnOnce() -> (Result<Value, String>, Durability),
) -> Result<Value, String> {
    let (made, durability) = gated(host, make).map_err(|_| "the logbook is shutting down".to_string())?;
    Ok(after_durable_json(durability, made?))
}

fn parse_id(id: Option<&str>) -> Result<RecordId, String> {
    let id = id.unwrap_or("").trim();
    id.parse::<RecordId>().map_err(|_| format!("not a contact id: {id}"))
}

fn read_text(path: Option<&str>) -> Result<String, String> {
    let path = path.ok_or("no file named")?;
    std::fs::read_to_string(path).map_err(|e| format!("could not read {path}: {e}"))
}

fn ids_of(args: &Args) -> Result<Vec<RecordId>, String> {
    read_text(args.ids_path.as_deref())?
        .lines()
        .map(str::trim)
        .filter(|l| !l.is_empty())
        .map(|l| parse_id(Some(l)))
        .collect()
}

fn field_name(f: Field) -> String {
    serde_json::to_value(f).ok().and_then(|v| v.as_str().map(str::to_string)).unwrap_or_default()
}

fn tool_run(made: (Result<ToolRun, String>, Durability)) -> (Result<Value, String>, Durability) {
    let (made, durability) = made;
    (
        made.map(|r| {
            json!({
                "changed": r.changed,
                "run": r.run,
                "refused": r.refused.iter().map(|(id, why)| json!([id.to_string(), why])).collect::<Vec<_>>(),
            })
        }),
        durability,
    )
}

fn cannot(why: &CannotUndo) -> &'static str {
    match why {
        CannotUndo::Gone => "gone",
        CannotUndo::AlreadyThere => "alreadyThere",
        CannotUndo::ChangedSince => "changedSince",
        CannotUndo::Duplicate => "duplicate",
        CannotUndo::UnknownKind => "unknownKind",
    }
}

fn undone(made: (Result<Undone, String>, Durability)) -> (Result<Value, String>, Durability) {
    let (made, durability) = made;
    (
        made.map(|u| {
            json!({
                "changed": u.changed,
                "run": u.run,
                "kept": u.kept.iter().map(|(id, f)| json!([id.to_string(), f])).collect::<Vec<_>>(),
                "held": u.held.iter().map(|(id, f)| json!([id.to_string(), f])).collect::<Vec<_>>(),
                "heldUnder": u.held_under,
                "refused": u.refused.iter().map(|(id, why)| json!([id.to_string(), cannot(why)])).collect::<Vec<_>>(),
                "calls": u.calls.iter().map(|(id, c)| (id.to_string(), json!(c))).collect::<serde_json::Map<_, _>>(),
            })
        }),
        durability,
    )
}

// ── bulk edit ────────────────────────────────────────────────────────────────────────────────

fn bulk_preview(engine: &Mutex<Engine>, args: &Args) -> Result<Value, String> {
    let ids = ids_of(args)?;
    let p = logtools::bulk_preview(engine, &ids, &args.changes)?;
    Ok(json!({
        "state": "ok",
        "selected": p.selected, "change": p.change, "unchanged": p.unchanged, "missing": p.missing,
        "recall": p.recall, "resend": p.resend, "unconfirm": p.unconfirm,
        "moves": p.moves, "moveTo": p.move_to, "moveFrom": p.move_from,
        "activationChanges": p.activation_changes, "activationUploaded": p.activation_uploaded,
        "activationExported": p.activation_exported, "activationFiles": p.activation_files,
    }))
}

fn bulk_edit(engine: &Mutex<Engine>, args: &Args) -> (Result<Value, String>, Durability) {
    let ids = match ids_of(args) {
        Ok(ids) => ids,
        Err(e) => return (Err(e), Durability::default()),
    };
    let ack = MovesAck { moves: args.moves, move_to: args.move_to.clone() };
    tool_run(logtools::bulk_edit(engine, &ids, args.expect, &ack, &args.changes))
}

// ── duplicates ───────────────────────────────────────────────────────────────────────────────

fn copy_json(r: &QsoRecord) -> Value {
    let u = &r.upload;
    let sent = [&u.lotw, &u.eqsl, &u.qrz, &u.clublog]
        .into_iter()
        .any(|s| s.as_ref().is_some_and(|s| s.outcome.is_sent()));
    json!({
        "id": r.id.map(|i| i.to_string()),
        "revision": content_key(r),
        "call": r.call, "band": r.band, "mode": r.mode, "whenUnix": r.when_unix,
        "grid": r.grid, "state": r.state, "stationCallsign": r.station_callsign,
        "confirmed": r.qsl_rcvd.lotw || r.qsl_rcvd.card || r.qsl_rcvd.eqsl || r.qsl_rcvd.qrz,
        "uploaded": sent,
    })
}

fn dupes(engine: &Mutex<Engine>, args: &Args) -> Result<Value, String> {
    let tier = if args.possible { DupeTier::Possible } else { DupeTier::Rule };
    let scan = logtools::find_duplicates(engine, tier)?;
    let groups: Vec<Value> = scan
        .groups
        .iter()
        .map(|g| {
            json!({
                "keep": g.copies.get(g.keep).and_then(|r| r.id).map(|i| i.to_string()),
                "copies": g.copies.iter().map(copy_json).collect::<Vec<_>>(),
                "conflicts": g.conflicts.iter().map(|c| json!({
                    "tag": c.tag,
                    "guarded": c.guarded,
                    "chosen": c.chosen.to_string(),
                    "values": c.values.iter().map(|(id, v)| json!([id.to_string(), v])).collect::<Vec<_>>(),
                })).collect::<Vec<_>>(),
            })
        })
        .collect();
    Ok(json!({ "state": "ok", "scanned": scan.scanned, "groups": groups }))
}

#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase")]
struct AskArg {
    keep: String,
    /// [id, revision] for every copy, as the finder showed it.
    copies: Vec<(String, String)>,
    #[serde(default)]
    choices: HashMap<String, String>,
}

fn merge(engine: &Mutex<Engine>, args: &Args) -> (Result<Value, String>, Durability) {
    let asks = (|| -> Result<Vec<MergeAsk>, String> {
        let list: Vec<AskArg> = serde_json::from_str(&read_text(args.asks_path.as_deref())?).map_err(|e| e.to_string())?;
        list.into_iter()
            .map(|a| {
                Ok(MergeAsk {
                    keep: parse_id(Some(&a.keep))?,
                    copies: a.copies.into_iter().map(|(id, rev)| Ok((parse_id(Some(&id))?, rev))).collect::<Result<_, String>>()?,
                    choices: a.choices.into_iter().map(|(tag, id)| Ok((tag, parse_id(Some(&id))?))).collect::<Result<_, String>>()?,
                })
            })
            .collect()
    })();
    match asks {
        Ok(asks) => tool_run(logtools::merge_duplicates(engine, &asks)),
        Err(e) => (Err(e), Durability::default()),
    }
}

// ── the check ────────────────────────────────────────────────────────────────────────────────

/// Nexus's own rule (src-tauri log_tools.rs `dxcc_mismatch`): whether the country file places
/// `call` in another current entity than `logged`, and the number to propose only where the
/// call's prefix decides it.
fn dxcc_mismatch(call: &str, logged: u32) -> Option<Option<u32>> {
    let entity = propagation::dxcc::entity_by_dxcc(logged)?;
    if entity.deleted {
        return None;
    }
    let by_call = propagation::dxcc::resolve(call)?.dxcc?;
    (by_call != logged).then(|| propagation::dxcc::prefix_decides(call, logged).then_some(by_call))
}

fn check(engine: &Mutex<Engine>) -> Result<Value, String> {
    let now = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map_or(0, |d| d.as_secs());
    let (checked, problems) = logtools::check(engine, &dxcc_mismatch, now)?;
    let list: Vec<Value> = problems
        .iter()
        .map(|p| {
            json!({
                "id": p.id.to_string(),
                "call": p.call,
                "whenUnix": p.when_unix,
                "kind": format!("{:?}", p.kind),
                "field": p.field.map(field_name),
                "current": p.current,
                "hasFix": p.fix.is_some(),
                "fixValue": p.fix.as_ref().and_then(|c| c.value.clone()),
            })
        })
        .collect();
    Ok(json!({ "state": "ok", "checked": checked, "problems": list }))
}

#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase")]
struct FixArg {
    id: String,
    field: Field,
    #[serde(default)]
    expect: Option<String>,
    #[serde(default)]
    value: Option<String>,
}

fn fix(engine: &Mutex<Engine>, args: &Args) -> (Result<Value, String>, Durability) {
    let fixes = (|| -> Result<Vec<Fix>, String> {
        let list: Vec<FixArg> = serde_json::from_str(&read_text(args.fixes_path.as_deref())?).map_err(|e| e.to_string())?;
        list.into_iter()
            .map(|f| Ok(Fix { id: parse_id(Some(&f.id))?, field: f.field, expect: f.expect, value: f.value }))
            .collect()
    })();
    match fixes {
        Ok(fixes) => tool_run(logtools::apply_fixes(engine, &fixes)),
        Err(e) => (Err(e), Durability::default()),
    }
}

// ── history ──────────────────────────────────────────────────────────────────────────────────

fn entry_json(e: &Entry) -> Value {
    let changes = e.changes();
    let of = |tag: &str| changes.iter().find(|c| c.tag == tag).and_then(|c| c.old.clone().or(c.new.clone()));
    json!({
        "seq": e.seq,
        "id": e.qso_id.to_string(),
        "kind": e.kind.word(),
        "run": e.run,
        "atMs": e.at_ms,
        "source": e.source,
        "undoes": e.undoes,
        // A deleted contact's own call, band, mode and time, from the copy the delete kept.
        "call": of("CALL"), "band": of("BAND"), "mode": of("MODE"), "date": of("QSO_DATE"), "time": of("TIME_ON"),
        "changes": changes.iter().map(|c| json!({ "tag": c.tag, "old": c.old, "new": c.new })).collect::<Vec<_>>(),
    })
}

fn history(engine: &Mutex<Engine>, args: &Args) -> Result<Value, String> {
    let id = parse_id(args.id.as_deref())?;
    let entries = loghistory::history_of(engine, id)?;
    Ok(json!({ "state": "ok", "entries": entries.iter().map(entry_json).collect::<Vec<_>>() }))
}

fn deleted(engine: &Mutex<Engine>, args: &Args) -> Result<Value, String> {
    let entries = loghistory::recently_deleted(engine, args.limit.unwrap_or(200))?;
    Ok(json!({ "state": "ok", "entries": entries.iter().map(entry_json).collect::<Vec<_>>() }))
}

fn runs(engine: &Mutex<Engine>, args: &Args) -> Result<Value, String> {
    let runs = loghistory::undoable_runs(engine, args.limit.unwrap_or(50))?;
    Ok(json!({
        "state": "ok",
        "runs": runs.iter().map(|r| json!({
            "run": r.run, "source": r.source, "atMs": r.at_ms, "contacts": r.contacts, "removes": r.removes,
        })).collect::<Vec<_>>(),
    }))
}

// ── update from callbook ─────────────────────────────────────────────────────────────────────
// Jimmy Next looks the calls up itself (its own QRZ/HamQTH login, cache and pace); Nexus decides
// what goes on each contact (`callbook_fill`: empty fields only, where the station is only on a
// contact from the last 12 months, never the call, a stamp or a confirmation), as one run.

/// One call's answer as Jimmy Next sends it (`tempo_core::logbook::callbook::CallbookAnswer`).
#[derive(serde::Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
struct AnswerArg {
    call: String,
    name: Option<String>,
    qth: Option<String>,
    grid: Option<String>,
    state: Option<String>,
    county: Option<String>,
    iota: Option<String>,
    cq_zone: Option<u32>,
    itu_zone: Option<u32>,
    email: Option<String>,
    qsl_via: Option<String>,
    lotw: Option<bool>,
    eqsl: Option<bool>,
}

fn answers_of(args: &Args) -> Result<HashMap<String, tempo_core::logbook::callbook::CallbookAnswer>, String> {
    let Some(path) = args.answers_path.as_deref() else {
        return Ok(HashMap::new());
    };
    let list: HashMap<String, AnswerArg> = serde_json::from_str(&read_text(Some(path))?).map_err(|e| e.to_string())?;
    Ok(list
        .into_iter()
        .map(|(key, a)| {
            (
                key,
                tempo_core::logbook::callbook::CallbookAnswer {
                    call: a.call,
                    name: a.name,
                    qth: a.qth,
                    grid: a.grid,
                    state: a.state,
                    county: a.county,
                    iota: a.iota,
                    cq_zone: a.cq_zone,
                    itu_zone: a.itu_zone,
                    email: a.email,
                    qsl_via: a.qsl_via,
                    lotw: a.lotw,
                    eqsl: a.eqsl,
                    ..Default::default()
                },
            )
        })
        .collect())
}

fn now_unix() -> u64 {
    std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map_or(0, |d| d.as_secs())
}

/// The calls to look up for these contacts (each once, by Nexus's own key), and -- given the
/// answers found so far -- how many contacts and fields the fill would give. Changes nothing.
fn callbook_preview(engine: &Mutex<Engine>, args: &Args) -> Result<Value, String> {
    let ids = ids_of(args)?;
    let answers = answers_of(args)?;
    let p = logtools::callbook_preview(engine, &ids, now_unix(), &mut |key| answers.get(key).cloned())?;
    Ok(json!({
        "state": "ok", "selected": p.selected, "missing": p.missing, "calls": p.calls, "kept": p.kept,
        "couldChange": p.could_change, "fields": p.fields, "locationTooOld": p.location_too_old,
    }))
}

fn callbook_fill(engine: &Mutex<Engine>, args: &Args) -> (Result<Value, String>, Durability) {
    let (ids, answers) = match (ids_of(args), answers_of(args)) {
        (Ok(i), Ok(a)) => (i, a),
        (Err(e), _) | (_, Err(e)) => return (Err(e), Durability::default()),
    };
    // A large fill waits for the radio to stop transmitting (Nexus refuses one under TX): up to
    // ten minutes, then it stops, keeping what it made.
    let mut wait_tx = || {
        for _ in 0..600 {
            if !tempo_app::engine::engine_lock(engine).transmit_needs_the_lock() {
                return true;
            }
            std::thread::sleep(std::time::Duration::from_secs(1));
        }
        false
    };
    let (made, why, durability) = logtools::callbook_fill(engine, &ids, args.expect, &answers, now_unix(), &mut wait_tx);
    let mut v = json!({
        "changed": made.changed,
        "run": made.run,
        "refused": made.refused.iter().map(|(id, w)| json!([id.to_string(), w])).collect::<Vec<_>>(),
    });
    if let Some(why) = why {
        if made.changed == 0 {
            return (Err(why), durability);
        }
        v["stopped"] = json!(why);
    }
    (Ok(v), durability)
}

// ── import ───────────────────────────────────────────────────────────────────────────────────

/// What a file holds, in any of Nexus's three formats (ADIF, ADX, Cabrillo), and the log's own
/// duplicate rule's counts from the plan the import would make -- nothing written. Nexus's own
/// `preview_import` command, unchanged.
fn import_preview(engine: &Mutex<Engine>, args: &Args) -> Result<Value, String> {
    let text = read_text(args.path.as_deref())?;
    let source = import::read_file(&text)?;
    let mut preview = import::preview(&source);
    let adif = source.into_adif();
    let (new, duplicates, updates) = tempo_app::logwrite::preview_import(engine, adif.as_deref().unwrap_or(&text))?;
    preview.new = new;
    preview.duplicates = duplicates;
    preview.updates = updates;
    let mut v = serde_json::to_value(&preview).map_err(|e| e.to_string())?;
    v["state"] = json!("ok");
    Ok(v)
}

/// An ADX or Cabrillo file as the ADIF Nexus's import reads from it (an ADIF file as it is),
/// written to `outPath`: so Jimmy Next marks its contacts as it marks every import.
fn to_adif(args: &Args) -> Result<Value, String> {
    let text = read_text(args.path.as_deref())?;
    let out = args.out_path.as_deref().ok_or("no file to write named")?;
    let source = import::read_file(&text)?;
    let adif = source.into_adif().unwrap_or(text);
    std::fs::write(out, adif).map_err(|e| format!("could not write {out}: {e}"))?;
    Ok(json!({ "state": "ok" }))
}

/// The import itself, as one run (its Undo undoes it): "Import new only" leaves every contact the
/// log already holds as it is. With the no-loss report. Nexus's own `import_log`, unchanged.
fn import_file(engine: &Mutex<Engine>, args: &Args) -> (Result<Value, String>, Durability) {
    let text = match read_text(args.path.as_deref()) {
        Ok(t) => t,
        Err(e) => return (Err(e), Durability::default()),
    };
    let source = match import::read_file(&text) {
        Ok(s) => s,
        Err(e) => return (Err(e), Durability::default()),
    };
    let adif = source.adif(&text);
    let run = loghistory::new_run();
    let (made, durability) = tempo_app::logwrite::until_written(|| {
        if args.new_only {
            tempo_app::logwrite::import_adif_new_only_in_run(engine, adif, &run)
        } else {
            tempo_app::logwrite::import_adif_in_run(engine, adif, &run)
        }
    });
    let made = made.map(|(added, skipped, updated, total)| {
        json!({
            "added": added, "skipped": skipped, "updated": updated, "total": total, "run": run,
            "report": import::report(&source, (added, skipped, updated), args.new_only),
        })
    });
    (made, durability)
}
