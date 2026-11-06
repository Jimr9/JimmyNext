//! LoTW uploads through Nexus (2026-10-21): the TQSL batch file and what TQSL's result means for
//! the contacts it was handed are Nexus's, not Jimmy's.
//!
//! Jimmy still decides WHICH contacts go and with which TQSL station location (its profile, its
//! logbook rules, the location each contact was logged at), and Jimmy still runs TQSL itself --
//! a run can take minutes, longer than a control command may wait, and Jimmy's runner already
//! bounds a TQSL stuck on a dialog. Two commands carry the rest:
//!
//!   LOTW_BATCH_PREPARE {"idsPath","adifPath"}
//!       The contacts named in `idsPath` (one Nexus id per line), written to `adifPath` as Nexus's
//!       own desktop writes a TQSL batch (`station::lotw_batch_adif`, named-location mode). Each
//!       contact is remembered as it was signed (`station::LotwSigned`: its id and fingerprint).
//!       Reply: {"state":"ready","batch":"<n>","signed":N,"missing":M} | {"state":"error","why"}.
//!   LOTW_BATCH_RESULT {"batch","code","statusCode","stderrPath"}
//!       TQSL's exit code, the code of its own "Final Status" line, and its stderr (in a file:
//!       the control line is capped at 8 KB). Judged by [`judge`]; a verdict to stamp is recorded
//!       by Nexus's `logwrite::stamp_lotw_batch`, only on contacts still as they were signed --
//!       one edited or deleted while TQSL ran is left as it is and offered again next time.
//!       Reply: {"state":"stamped"|"left", "outcome","detail","why","stamped","changed","gone",
//!       "unrecorded","durable"}.

use std::collections::VecDeque;
use std::io::Write as _;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Mutex;

use tempo_app::engine::Engine;
use tempo_app::station::LotwSigned;
use tempo_core::logbook::{RecordId, UploadDetail, UploadOutcome, UploadStatus};

use crate::logbook_host::{self, LogHost};

/// Batches prepared and not yet answered. A batch whose result never comes (TQSL timed out, Jimmy
/// closed) is simply forgotten: nothing was stamped, so its contacts stay owed.
static BATCHES: Mutex<VecDeque<(u64, Vec<LotwSigned>)>> = Mutex::new(VecDeque::new());
const KEPT_BATCHES: usize = 16;
static NEXT_BATCH: AtomicU64 = AtomicU64::new(1);

/// What a TQSL run means for the contacts it was handed.
#[derive(Debug, PartialEq)]
pub enum Verdict {
    /// Record this outcome on every contact still as it was signed.
    Stamp(UploadOutcome, Option<UploadDetail>),
    /// Record nothing: every contact stays owed and goes with the next upload.
    Leave(&'static str),
}

/// Nexus's reading of TQSL's result (`lotw_upload::classify_tqsl_exit` / `tqsl_detail`), with two
/// rules of Jimmy's own on top, both so that a contact is never marked sent that may not have
/// reached LoTW:
///   - Code 8 ("no QSOs were processed") marks nothing. TQSL gives it both for "all already
///     uploaded" and for "outside the certificate's date range", and its date-range messages
///     ("QSO date is not within the date range specified on your Callsign Certificate") carry none
///     of the words Nexus looks for to tell the two apart -- read as Duplicate, such a contact
///     would never be offered again (Jimmy's 2026-08-20 release-audit fix).
///   - A sent outcome needs TQSL's own "Final Status" line to agree with its exit code; anything
///     unclear marks nothing.
///
/// Every other code is Nexus's: 0 = Pending (sent, LoTW confirms later); 9 (some records dropped,
/// TQSL does not say which), 5 and the rest = Rejected or AuthFail, which are not "sent" -- the
/// contacts stay owed, with the reason recorded; 11 (network) = record nothing.
pub fn judge(code: Option<i32>, status_code: Option<i32>, stderr: &str) -> Verdict {
    let Some(code) = code else { return Verdict::Leave("unclear") };
    if status_code.is_some_and(|s| s != code) {
        return Verdict::Leave("unclear");
    }
    if code == 8 {
        return Verdict::Leave("nothing-new");
    }
    match tempo_core::lotw_upload::classify_tqsl_exit(code, stderr) {
        None => Verdict::Leave("retry"),
        Some(o) if o.is_sent() && status_code != Some(code) => Verdict::Leave("unclear"),
        Some(o) => Verdict::Stamp(o, tqsl_detail(code, stderr)),
    }
}

fn tqsl_detail(code: i32, stderr: &str) -> Option<UploadDetail> {
    tempo_core::lotw_upload::tqsl_detail(code, stderr)
}

#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PrepareArgs {
    pub ids_path: String,
    pub adif_path: String,
}

/// LOTW_BATCH_PREPARE.
pub fn prepare(engine: &Mutex<Engine>, args: PrepareArgs) -> serde_json::Value {
    let err = |why: String| serde_json::json!({ "state": "error", "why": why });
    let text = match std::fs::read_to_string(&args.ids_path) {
        Ok(t) => t,
        Err(e) => return err(format!("could not read the contact list: {e}")),
    };
    let mut ids: Vec<RecordId> = Vec::new();
    let mut missing = 0usize;
    for line in text.lines().map(str::trim).filter(|l| !l.is_empty()) {
        match line.parse::<RecordId>() {
            Ok(id) => ids.push(id),
            Err(_) => missing += 1,
        }
    }
    // Read off the store with the Engine lock released, as Nexus's desktop reads its batch.
    let rows = engine.lock().unwrap_or_else(|e| e.into_inner()).log_rows();
    let batch = match tempo_app::station::rows_named(&rows, &ids) {
        Ok(b) => b,
        Err(e) => return err(format!("could not read the logbook: {e}")),
    };
    missing += ids.len() - batch.len();
    let signed: Vec<LotwSigned> = batch.iter().filter_map(LotwSigned::of).collect();
    if signed.is_empty() {
        return serde_json::json!({ "state": "ready", "batch": "", "signed": 0, "missing": missing });
    }
    // Named-location mode (TQSL's -l): the call and grid are used only in ADIF-location mode.
    let adif = tempo_app::station::lotw_batch_adif(&batch, false, "", "");
    let written = std::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&args.adif_path)
        .and_then(|mut f| f.write_all(adif.as_bytes()));
    if let Err(e) = written {
        return err(format!("could not write the upload file: {e}"));
    }
    let n = signed.len();
    let id = NEXT_BATCH.fetch_add(1, Ordering::Relaxed);
    let mut kept = BATCHES.lock().unwrap_or_else(|e| e.into_inner());
    kept.push_back((id, signed));
    while kept.len() > KEPT_BATCHES {
        kept.pop_front();
    }
    serde_json::json!({ "state": "ready", "batch": id.to_string(), "signed": n, "missing": missing })
}

#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResultArgs {
    pub batch: String,
    #[serde(default)]
    pub code: Option<i32>,
    #[serde(default)]
    pub status_code: Option<i32>,
    #[serde(default)]
    pub stderr_path: String,
}

/// LOTW_BATCH_RESULT.
pub fn result(host: &LogHost, engine: &Mutex<Engine>, args: ResultArgs) -> serde_json::Value {
    let signed = {
        let mut kept = BATCHES.lock().unwrap_or_else(|e| e.into_inner());
        let at = args.batch.parse::<u64>().ok().and_then(|b| kept.iter().position(|(id, _)| *id == b));
        at.and_then(|i| kept.remove(i)).map(|(_, s)| s)
    };
    let Some(signed) = signed else {
        return serde_json::json!({ "state": "left", "why": "unknown-batch" });
    };
    let stderr = if args.stderr_path.is_empty() {
        String::new()
    } else {
        std::fs::read(&args.stderr_path).map(|b| String::from_utf8_lossy(&b).into_owned()).unwrap_or_default()
    };
    let (outcome, detail) = match judge(args.code, args.status_code, &stderr) {
        Verdict::Leave(why) => return serde_json::json!({ "state": "left", "why": why }),
        Verdict::Stamp(o, d) => (o, d),
    };
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0);
    let status = UploadStatus { outcome, when_unix: now, detail };
    let made = logbook_host::while_open(host, || {
        tempo_app::logwrite::until_written(|| tempo_app::logwrite::stamp_lotw_batch(engine, &signed, &status))
    });
    let Some((done, durability)) = made else {
        return serde_json::json!({ "state": "left", "why": "logbook closing" });
    };
    let durable = durability.wait(logbook_host::DURABLE_WAIT).is_ok();
    serde_json::json!({
        "state": "stamped",
        "outcome": outcome.code(),
        "detail": detail.map(|d| d.code()),
        "stamped": done.stamped,
        "changed": done.changed,
        "gone": done.gone,
        "unrecorded": done.unrecorded,
        "durable": durable,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    const FINAL_0: &str = "12:00:00 PM Final Status: Success (0)";

    #[test]
    fn success_is_pending_only_when_tqsl_says_so_twice() {
        assert_eq!(judge(Some(0), Some(0), FINAL_0), Verdict::Stamp(UploadOutcome::Pending, None));
        assert_eq!(judge(Some(0), None, ""), Verdict::Leave("unclear"), "no Final Status line");
        assert_eq!(judge(Some(0), Some(8), FINAL_0), Verdict::Leave("unclear"), "exit and status disagree");
        assert_eq!(judge(None, Some(0), FINAL_0), Verdict::Leave("unclear"), "TQSL did not exit");
    }

    #[test]
    fn code_8_marks_nothing_even_when_it_reads_as_duplicates() {
        // Nexus alone reads this as Duplicate (a sent outcome): the date-range wording has none of
        // its rejection markers.
        let date_range = "QSO date is not within the date range specified on your Callsign Certificate";
        assert_eq!(tempo_core::lotw_upload::classify_tqsl_exit(8, date_range), Some(UploadOutcome::Duplicate));
        assert_eq!(judge(Some(8), Some(8), date_range), Verdict::Leave("nothing-new"));
        assert_eq!(judge(Some(8), Some(8), "Duplicate QSO detected"), Verdict::Leave("nothing-new"));
    }

    #[test]
    fn partial_rejected_and_network_keep_the_contacts_owed() {
        assert_eq!(
            judge(Some(9), Some(9), ""),
            Verdict::Stamp(UploadOutcome::Rejected, Some(UploadDetail::BatchPartlySigned))
        );
        assert!(matches!(judge(Some(5), Some(5), "no certificate for this callsign"), Verdict::Stamp(UploadOutcome::AuthFail, _)));
        assert!(matches!(judge(Some(2), None, ""), Verdict::Stamp(UploadOutcome::Rejected, _)), "a failure needs no status line");
        assert_eq!(judge(Some(11), Some(11), ""), Verdict::Leave("retry"));
        for o in [UploadOutcome::Rejected, UploadOutcome::AuthFail] {
            assert!(!o.is_sent(), "{o:?} keeps the contact owed");
        }
    }
}
