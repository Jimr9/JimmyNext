# Jimmy Next — architecture map

Quick orientation for a future development session: what owns what, so a focused change
(e.g. "fix notifications") doesn't require reading the whole codebase. Depth varies below --
sections marked **(verified)** were read and traced end-to-end during this pass; sections
marked **(from structure)** are inferred from folder/file layout and naming and should be
confirmed by reading the code before relying on the description.

## Layers (verified)

```
Nexus (third-party, GPL-3.0, https://github.com/kd9taw/Nexus)
    engine/backend/domain: FT8/FT4 DSP, decode, TX audio generation, slot timing,
    QSO sequencing, CAT/PTT, WSJT-X-protocol UDP, PSK Reporter upload
        |
EngineHost (Jimmy-owned, Rust, EngineHost/)
    thin adapter: builds a Nexus Engine + RadioConfig from Jimmy's CLI args, calls
    Nexus's own tempo_audio::service::run_radio on a thread, exposes a small TCP
    control protocol (SNAPSHOT/REPLY/HALT_TX/SET_TX_ENABLED/SET_PSKREPORTER/...)
        |
Jimmy Core (C#, WSJTX_Controller/)
    operator intelligence: queue/ranking, awards, notifications, logbook, workflow
        |
WinForms (C#, WSJTX_Controller/*.Designer.cs, Controller.cs's UI half)
    accessible operator interface (JAWS/NVDA)
```

**EngineHost is genuinely thin.** `EngineHost/Cargo.toml`'s own header comment documents that
it used to hand-roll its own decode/TX-scheduling/PTT loop and was migrated ("Self-sufficiency
plan Phase 5") to call Nexus's real production `run_radio` instead -- there is no duplicate
engine here to remove.

**Radio/CAT/PTT ownership is already correct.** `RigctldClient.cs`'s call site in
`Controller.cs` (`ApplyRadioSettings`) documents explicitly: Nexus/EngineHost always owns and
launches rigctld; Jimmy's own `RigctldClient` only ever connects as a second, read-mostly
client of that same multi-client daemon, for telemetry Nexus doesn't expose over the Direct
contract (S-meter, SWR, power) plus a couple of operator actions (Band Up/Down retune, AF-gain
step). This is "multiple intentional clients of one service," not a competing owner -- confirmed
by reading the code, not just inferred.

## Nexus dependency (verified)

EngineHost builds against a clean, pinned copy of official Nexus, never a developer's own
checkout. See `EngineHost/nexus-compat/README.md` for the full mechanism: `pin.txt` names the
exact revision, `scripts/prepare-nexus.ps1` clones it fresh into the git-ignored
`EngineHost/.nexus-src/`, and a small number of documented, isolated patch files (currently 5,
covering behaviors official Nexus doesn't yet have -- see that README) are applied on top. Run
that script before building EngineHost if `.nexus-src` doesn't exist.

## Jimmy Core subsystems (from structure, folder sizes noted for scale)

| Folder | Files | What it owns (from naming/spot-checks) |
|---|---|---|
| `Awards/` | 14 | Configurable award/rule system: `RuleEngine`, `RuleDefinition`, `AwardMatcher`/`AwardTagger`. Operator-configured, not Nexus's concern (Nexus may supply facts like DXCC entity; Jimmy decides what those facts mean for an award). |
| `Classification/` | 3 | **(verified)** `ClassificationEngine` computes "is this new/DX/what country" from Jimmy's own `LogbookDb` + `LookupManager`, replacing WSJT-X's wire-supplied classification fields (`ClassificationCutover` is a rollback valve from that migration, not Nexus-related). Correctly Jimmy-owned: this is exactly the operator-intelligence layer over raw facts the target architecture calls for. |
| `Logbook/` | 15 | The QSO database and ADIF/sync logic. Data-sensitive -- evaluated (see "Logbook/logging comparison" below), not migrated. |
| `Lookup/` | 10 | `LookupManager` + provider abstraction (`QrzProvider`, `ClubLogProvider`, `LoTWProvider`, `FccUlsProvider`) for callsign -> country/continent/bio enrichment. **See "Nexus capability review" below** -- this is the one area where a real, well-scoped Nexus-adoption opportunity was found. |
| `Notify/` | 10 | Notification policy, templates, timing, dedup. Operator-facing decisions; stays Jimmy-owned per the target architecture. |
| `Radio/` | 1 | `RigctldClient` -- see "Radio/CAT/PTT ownership" above. |
| `RuleDefinitions/` | 29 | Award rule data files (not code) -- companion lists, per-award JSON/config. |
| `Messages/` | 4 | WSJT-X protocol message types. |
| `Geo/` | 1 | `GeoMath` -- distance/azimuth calculation used by `ClassificationEngine`. |

`WSJTX_Controller/*.cs` (top level, ~28,800 lines) holds `Controller.cs` (the main
WinForms/operator-workflow surface), `WsjtxClient.*.cs` (the split-by-concern WSJT-X-protocol
and Direct/native-engine client), `OptionsDlg.cs`, and cross-cutting settings/support classes.
This is the natural next modularization target if a future pass wants smaller, more focused
areas -- not attempted in this pass; recorded here as a scope note, not a finding.

## Nexus capability review

Classified per the operator's request. "Available in Nexus" reflects the pinned `v1.6.0`
revision (`EngineHost/nexus-compat/pin.txt`).

**1. Already being used correctly by Jimmy:**
- FT8/FT4 decode, TX audio generation, slot timing, QSO sequencing -- via `tempo_app::engine::Engine` + `tempo_audio::service::run_radio` (see EngineHost above).
- CAT/PTT -- Nexus is the sole physical-radio owner (see above).
- PSK Reporter upload -- Nexus's own native uploader, toggled live via `Engine::set_pskreporter()` (one of the 5 compatibility patches). Jimmy has no separate PSK Reporter uploader; `DxSpotWatcher.cs` is unrelated (it *consumes* PSK Reporter's public live-spot MQTT feed for DX-spot alerting, a different feature).

**2. Available in Nexus and should be used by Jimmy now:** none found for DXCC specifically --
see the shadow-comparison result below. Revisit after a future Nexus update if
`propagation::dxcc::DxccInfo` gains a numeric entity ID.

**DXCC/country/continent shadow comparison -- cutover NOT made, current path stays authoritative.**
Correcting an earlier draft of this document: Jimmy's `ClubLogProvider`
(`WSJTX_Controller/Lookup/ClubLogProvider.cs`) is *not* a live-network-per-lookup path --
it's already a mature, offline, prefix-based resolver (AD1C's Big CTY country file, cached
locally, refreshed periodically, zero network calls on the per-decode hot path) with real
field-tested edge-case handling: the KG4-format Guantanamo Bay rule, K4-prefix current-vs-
expired precedence, per-callarea CQ-zone overrides. Nexus's `propagation::dxcc::resolve()` draws
from the same AD1C `cty.dat` family, so a real comparison was worth doing rather than assuming
Nexus wins on "local and fast" (it wasn't a legitimate advantage here -- Jimmy's path already is).

Ran both resolvers (`EngineHost/tests/dxcc_shadow_dump.rs` for Nexus,
`JimmyTests.exe --dxcc-shadow-dump` for Jimmy, same 33-callsign list covering every edge case
above plus international spread, portable suffixes, and the `3Y0J` Bouvet exact-call-override
case) and diffed the output by hand:

- **32 of 33 calls agreed exactly** on entity, continent, and CQ zone -- including every hard
  edge case (KG4AB -> Guantanamo Bay, KG4JOK -> USA, K4YT -> current USA not expired Puerto
  Rico, VE3 zone 4 vs VE7 zone 3, 3Y0J -> Bouvet). Real, meaningful equivalence on the common
  path.
- **One real disagreement:** `K1ABC/H` (Fox/Hound-style `/H` suffix) -- Nexus's resolver
  returns no match; Jimmy's resolves it to USA via ordinary prefix stripping.
- **One structural gap that blocks cutover regardless of the above:** `propagation::DxccInfo`
  exposes only an entity *name* string, no numeric ADIF/DXCC-entity ID. Jimmy's award system
  (`AwardTagger.cs`) matches entities by exact numeric ID
  (`hrcUnconfirmedDxcc.Contains(rec.Dxcc)`), and the two sides' entity name strings differ in
  formatting ("South Africa" vs Jimmy's "REPUBLIC OF SOUTH AFRICA") in ways that would make
  name-based matching fragile. Using Nexus's resolver for award-triggering decisions would mean
  Jimmy building and maintaining its own name -> ADIF mapping -- recreating the exact kind of
  duplicate logic this review exists to avoid.

**Decision: kept `ClubLogProvider` as the authoritative source. No cutover.** The comparison
tooling (both dump scripts) is left in place, not deleted -- worth re-running after a future
deliberate Nexus version update in case `DxccInfo` gains a numeric entity ID, which would remove
the structural blocker above.

**3. Available in Nexus but needs more Jimmy work before it is useful:**
- `bandplan.rs` / `privileges.rs` (amateur-radio band edges + license-class privilege checking). Jimmy has no equivalent today (no TX-legality warnings) and no operator-facing feature that would consume it yet -- would need a UI/workflow decision first, not just a wire-up.

**4. Useful for a later Jimmy feature, not now:**
- FT2, Q65, WSPR, MSK144, JT65, FST4/FST4W, SSTV, DeepCW -- all present and apparently mature in Nexus (the pinned revision alone added full FT2 support, 172 commits of work across the other modes). FT8/FT4 remain the shipping focus per the operator's direction; no mode work attempted or recommended in this pass.
- Nexus's Direct-adjacent `connHealth`/settings-registry work in its own Tauri UI (`ui/src/settings/connHealth.ts`, `ui/src/settings/registry.ts`) is conceptually close to what Jimmy's own Direct-contract "central capability model" goal describes -- worth a look if that work starts, not evaluated in depth here.

**5. Not appropriate for Jimmy:**
- Nexus's own Tauri/React UI (`ui/`) -- Jimmy's accessible WinForms UI is the whole point; not a candidate for reuse in any form.

## Logbook / logging comparison

Requested specifically: don't assume Jimmy's logbook is untouchable just because it's data-sensitive, but don't move data for architectural purity either. Compared actual capabilities on both sides.

**What's there.** Jimmy: `WSJTX_Controller/Logbook/` (SQLite via `LogbookDb.cs`, ~5,900 lines across ADIF import/export/parse/record-building, plus dedicated upload clients for QRZ, Club Log, LoTW via TQSL, and HRDLog.net) plus `LiveQsoUploadOrchestrator.cs` (real-time upload) and `LogbookAutoSync.cs` (batch sync). Nexus: `crates/tempo-core/src/logbook.rs` (4,008 lines) plus dedicated modules for `clublog.rs`, `lotw.rs`/`lotw_upload.rs`, `hrdlog.rs`, `eqsl.rs`, `hamqth.rs` -- also a mature, multi-service system, built for Nexus's own desktop app.

**1. Already correctly owned by the right side, verified by reading the code:**
- **Local QSO storage.** Jimmy's `LogbookDb` is a real SQLite database with a `dedup_key` UNIQUE constraint and `ON CONFLICT(dedup_key) DO UPDATE` -- proper dedup/correction semantics, not a flat ADIF file. It's also the data source `ClassificationEngine.HasWorkedBefore()` and the whole award system query directly. Nexus's logbook is ADIF-file-based, for a separate app with its own separate awards/needs model (`propagation::dxped::LogNeeds`). These are two independently-evolved systems serving two different applications' operator workflows, not a duplicate-vs-original relationship -- migrating would mean rebuilding Jimmy's award/classification query layer against a different data model for no functional gain. **Jimmy stays authoritative.**
- **Who writes the log.** Already correctly divided, confirmed in EngineHost's own `RadioConfig` construction: `auto_log: false` with the comment *"Jimmy owns logbook writes; the native engine must never double-log."* Nexus supplies the QSO facts (via the snapshot); Jimmy alone decides when and what to write. No change needed -- this is the target architecture already in place.
- **Local logging never blocks on an external service.** `LiveQsoUploadOrchestrator` runs uploads on a background `Task`, independent of the synchronous local SQLite write, with a circuit breaker for Club Log specifically (matching Club Log's own documented API requirement: stop retrying after a failure until the operator fixes something). A QRZ/Club Log/HRDLog outage cannot prevent or delay safe local logging. This class is also a good, already-proven precedent for the modularization work below -- it was already extracted from `WsjtxClient` with no WinForms/Controller reference at all.
- **Credentials.** Jimmy encrypts stored credentials at rest via Windows DPAPI (`CredentialProtector.cs`), scoped to the current Windows user. Nexus's `lotw.rs` authenticates via the LoTW *website* password over an HTTPS query string; Jimmy's LoTW integration instead uses TQSL (`TqslUploadClient.cs`), ARRL's own official certificate-based signing tool -- arguably the more standard and secure of the two approaches, not a gap to close.

**2. Genuine capability Jimmy doesn't have, real but not adopted this pass:**
- **eQSL and HamQTH.** Nexus has working integrations (`eqsl.rs`, `hamqth.rs`); Jimmy has neither. This is an actual feature gap, not an architecture question -- adding it means a new credentialed external service end to end (settings UI, credential storage, upload client, retry/circuit-breaking, tests), the same shape of work Jimmy's existing four services each already received individually. Not safe to add for the first time, this late in an already large release pass, without the same care. Recorded as a real candidate for a future, dedicated pass -- not because it's hard, but because it's new, untested surface area, and rushing exactly this kind of thing is what the operator's conservatism instruction on data-adjacent work exists to prevent.

**3. Diagnostic/application logging vs. QSO logbook.** Kept explicitly separate per the operator's request. Jimmy's `SupportReportBuilder.cs` (766 lines, already redacts credential-shaped keywords before building a support report) is diagnostic-only and has no overlap with QSO data. Nexus's own diagnostics (`crates/tempo-core/src/diagnostics.rs`) were not compared in depth -- no evidence surfaced during this pass that Jimmy's diagnostic logging has a gap worth closing, and application/crash diagnostics carry none of the QSO-data risk that would make this urgent for a release-candidate pass.

**Decision: Jimmy's logbook/upload stack stays fully authoritative. No migration, no cutover.** This isn't a default-to-caution non-answer -- the comparison found Jimmy's implementation is the correct owner for its own data model and operator workflow, not merely "too risky to check." The one real gap (eQSL/HamQTH) is additive, not a replacement, and deferred as new scope rather than rushed.

## TX-safety / recovery audit

Read the actual crash/recovery/shutdown code rather than assuming it needed building from
scratch -- it turned out to already be largely solid, verified layer by layer:

- **Crash detection**: `NativeEngineClient`'s `Process.Exited` handler distinguishes an
  intentional `Stop()` from a genuine unexpected exit (`_stopping` flag), and reports the exit
  code so a real crash is distinguishable from a clean shutdown in diagnostics.
- **Bounded auto-restart**: `Controller.OnNativeEngineUnexpectedExit` -- 5 attempts per rolling
  5-minute window, then gives up with a clear operator-facing message rather than looping. This
  logic is now extracted into `EngineRestartPolicy.cs` (see "Modularization" below) with direct
  test coverage proving the rolling-window behavior, which it had none of before this pass.
- **Reconnect obtains a fresh authoritative snapshot**: `ConnectDirectEngine` resets every piece
  of session state (decode dedup, clock-offset samples, band tracking) on each (re)connect, and
  `DirectPollTick` only promotes to "connected" after a real snapshot round-trip succeeds --
  already matches "snapshot = what is true now" from the target contract design.
- **Orphan-process safety**: verified, not assumed. `NativeEngineClient.Stop()` does a bounded
  `Kill()` + `WaitForExit()`. If that kill happens while `jimmy-engine-host.exe` had its own
  spawned `rigctld.exe` child, Nexus's own `RigctldProc` (tempo-audio/src/rigctld_proc.rs) binds
  that child to a Windows Job Object with `KILL_ON_JOB_CLOSE` -- an OS-level guarantee the child
  cannot survive the parent's death, even a forceful one. No orphan risk found.
- **Shutdown**: `Controller_FormClosing` disposes both `rigctldClient` and `nativeEngineClient`
  unconditionally; the latter's own comment confirms this force-releases PTT if held.
- **One real defect found and fixed**: a comment in `NativeEngineClient.Launch` claimed
  "NativeTxPttListener's own watchdog is still the real backstop" for a hung engine host --
  that class was already retired (PTT moved in-process in an earlier phase) by the time the
  comment was written, and was never updated. Corrected to describe the three mechanisms that
  actually provide this protection today (listed above), verified by reading the code rather
  than trusting the stale comment.
- **Not verifiable without a physical radio**: real CAT/PTT timing under an actual TX-safety
  fault (e.g. a hung serial port mid-transmission) -- on the live-test list.

## Modularization

One slice completed this pass, chosen because it served TX-safety testability directly, not
picked arbitrarily: `EngineRestartPolicy.cs`, extracted from `Controller.cs` following the same
shape `LiveQsoUploadOrchestrator` had already proven (a standalone class with no WinForms/
Controller reference). The bounded-restart decision was previously pure counting/clock logic
inlined in a WinForms code-behind file, untestable without constructing a `Form`; it now has 15
dedicated automated tests proving the rolling-window behavior (budget exhaustion, window
rollover) that had zero direct test coverage before. Full suite: 883/883 passing.

Not a large-scale restructuring of the ~28,800-line top-level `WSJTX_Controller/*.cs` -- one
coherent, low-risk, high-value slice, matching the instruction to work in slices rather than
attempt a rewrite. Further candidates (Notify/, Awards/ boundary tightening) remain open for a
future pass.

## Direct-contract hardening

- **Added**: `HALT_TX`/`SET_TX_ENABLED` (previously fully fire-and-forget, no visibility at all
  on failure) now log a diagnostic line when the engine's response indicates failure --
  additive only, no change to timing, retries, or control flow, since altering a TX-safety
  command's reliability behavior needs real-radio verification this pass doesn't have.
- **Deliberately deferred, not forgotten**: explicit protocol/version negotiation and a central
  capability model. Investigated first, not assumed unnecessary: `jimmy-engine-host.exe` and
  `Jimmy Test.exe` are always built, versioned, and shipped together from the same commit in the
  same MSI -- there is no current real-world scenario where they'd be mismatched, so a
  negotiation system would police a case that cannot happen yet. The control protocol is a
  simple line-based command dispatch (`EngineHost/src/main.rs`'s `line.strip_prefix(...)` match
  arms) that a future `VERSION`/capability command could be added to non-breakingly whenever
  Jimmy-to-Jimmy networking or remote EngineHost (both explicitly future work, not this pass)
  make mismatch a real possibility -- deferring this now creates no architectural debt to pay
  down later.
- **Already correct, verified rather than assumed**: snapshot-is-authoritative, reconnect
  rebuilds state, and bounded recovery -- see the TX-safety section above, which covers the same
  ground.

## Not yet done (scope for a future pass, not attempted here)

- Systematic duplication audit beyond radio ownership + the DXCC finding above (the plan asks
  for a broad review across QSO sequencing, callsign normalization, and more -- what's above is
  what surfaced from a focused, not exhaustive, pass).
- Further modularization slices beyond `EngineRestartPolicy` (Notify/, Awards/ boundary
  tightening).
- Explicit Direct-contract version/capability negotiation (see above for why this is a
  deliberate, low-risk deferral, not an oversight).
- eQSL / HamQTH integration (see "Logbook / logging comparison" above).

Each of these is a substantial, independently-scoped piece of work; attempting them without the
same proof-before-cutover rigor used elsewhere in this pass (isolated change, full test suite,
real build verification before commit) would risk exactly the kind of regression the operator's
plan explicitly warns against.
