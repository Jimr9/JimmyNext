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

**Both installed Hamlib copies (`Resources\hamlib\` and `Resources\EngineHost\hamlib\`) are
genuinely required -- verified by tracing the actual call site, not re-asserted from memory.**
`Resources\EngineHost\hamlib\` is for Nexus's own unmodified `resolve_rigctld()`, relative to
`jimmy-engine-host.exe`'s own directory (see the Hamlib-packaging note above). `Resources\
hamlib\` looked like it might be redundant now that the live session's `RigctldClient` never
launches its own copy -- but `grep`-ing every call site of `RigctldClient.LaunchBundled()` found
exactly one: `OptionsDlg.cs`'s `RadioTestButton_Click` (Options > Radio > Test Connection),
which launches Jimmy's own bundled copy to verify a configured rig model/COM port/baud rate
*independent of whether the native engine is even running*. Two separate processes
(`Jimmy Test.exe` and `jimmy-engine-host.exe`), each needing the binary at a path relative to
its own executable, for two separate still-used purposes -- not architectural duplication for
appearance's sake. Nothing removed.

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

**2. eQSL and HamQTH -- implemented, scoped deliberately (revised after a closer operator review):**
An initial pass of this comparison deferred both as unrelated new features. On review, both are
squarely in-scope: they're logbook/logging services in the same family as Jimmy's existing QRZ/
Club Log/LoTW/HRDLog integrations, not a separate feature area. Re-inspected Nexus's actual
transports (`propagation/src/live/eqsl.rs`, `hamqth.rs`, `tempo_core::eqsl`, `tempo_core::hamqth`)
for what each genuinely implements, then followed the same ownership split already used
everywhere else in this comparison: **Nexus/EngineHost owns the external service's own API
plumbing** where Nexus already does it well; **Jimmy owns the local logbook, operator settings,
enable/disable policy, and workflow.**
  - **eQSL upload -- implemented.** Nexus's `propagation::live::eqsl::post_form` (HTTPS-only, no
    redirect-following, credential-redacted errors -- verified via Nexus's own unit test proving
    no password leakage) plus `tempo_core::eqsl::build_upload_body`/`classify_upload` do the real
    work; EngineHost exposes it as `EQSL_UPLOAD` (`external_data.rs::eqsl_upload`), and Jimmy's
    `ExternalDataClient.UploadEqsl` calls it. Wired into `LiveQsoUploadOrchestrator` alongside
    QRZ/Club Log/HRDLog -- same enable/real-time-toggle shape, same "never blocks local logging"
    guarantee, own `eqsl_uploaded_at` tracking column (`LogbookDb` schema v7). No app-level
    credential: the operator supplies their own eQSL.cc username/password (eQSL has no API-key
    model the way QRZ/Club Log do), configured in Options > Logbook Sync > eQSL.cc Upload.
  - **eQSL download/reconciliation -- plumbing exists, reconciliation NOT implemented.**
    `ExternalDataClient.DownloadEqsl` / EngineHost's `EQSL_DOWNLOAD` (`external_data.rs::
    eqsl_download`, wrapping Nexus's `propagation::live::eqsl::fetch_inbox`) can already fetch
    the raw ADIF InBox. What's missing is the Jimmy-side reconciliation step -- matching returned
    records against `LogbookDb` and marking confirmations, the same job `LogbookAutoSync.cs`
    already does for QRZ/Club Log downloads via `AdifImporter.Import(..., source: "QRZ"/
    "CLUBLOG", ...)`. Not done this pass: needs `"EQSL"` added to `LogbookDb.KnownSources`, a
    `LogbookAutoSync`-style entry point, and (per the operator's explicit instruction) must not
    be described as supported until it actually reconciles -- claiming download/sync from upload
    support alone would be exactly the overclaim this comparison was asked to avoid. **Deferred,
    with a clear seam**, not attempted half-built.
  - **HamQTH lookup -- implemented, deliberately narrow scope.** Nexus's `propagation::live::
    hamqth::fetch` + `tempo_core::hamqth` (combined login+lookup per call, matching QRZ's own
    lookup DTO shape) are exposed as `HAMQTH_LOOKUP` / `ExternalDataClient.LookupHamQth`. Rather
    than adding HamQTH into `LookupManager`'s always-on automatic provider chain (FCC ULS > Club
    Log > QRZ > LoTW) -- which would change lookup precedence and background-request behavior for
    every operator, not just those who configure HamQTH, and needs its own deliberate design pass
    -- it's wired as an **on-demand supplement inside `LookupInfoDlg`** (the existing "Lookup
    Selected Station" dialog): after QRZ/offline data populates what it can, a HamQTH lookup fills
    only the fields still blank, never overriding an already-known answer. Configured in
    Options > Lookup Data > HamQTH Callsign Lookup, using the operator's own HamQTH.com login (no
    app-level credential, matching eQSL).
  - **Neither required a second network client.** Both reuse Nexus's already-mature, already-
    tested transport rather than duplicating eQSL/HamQTH HTTP logic in C# -- the explicit
    preference stated by the operator.

**3. Diagnostic/application logging vs. QSO logbook.** Kept explicitly separate per the operator's request. Jimmy's `SupportReportBuilder.cs` (766 lines, already redacts credential-shaped keywords before building a support report) is diagnostic-only and has no overlap with QSO data. Nexus's own diagnostics (`crates/tempo-core/src/diagnostics.rs`) were not compared in depth -- no evidence surfaced during this pass that Jimmy's diagnostic logging has a gap worth closing, and application/crash diagnostics carry none of the QSO-data risk that would make this urgent for a release-candidate pass.

**Decision: Jimmy's logbook/upload stack stays fully authoritative; eQSL and HamQTH are additive integrations on top of it, not a replacement or migration.** eQSL upload and HamQTH lookup are genuinely usable now, credentials-through-Options included. eQSL download/reconciliation remains open, documented above rather than silently dropped.

## Nexus-backed facts beyond FT8/FT4: POTA, SOTA, space weather

Per operator request: take advantage of useful Nexus information beyond the basic FT8/FT4 engine
-- POTA, SOTA, and propagation/space-weather facts -- without copying Nexus's own visual UI.
Ownership split follows the same pattern as everything else in this document: **Nexus supplies
the facts, Jimmy applies its own operator intelligence and presents them accessibly.**

- **Nexus's `propagation` crate promoted from dev-only to a real runtime dependency** (with its
  `live` feature) -- previously only used by Jimmy's own shadow-comparison test tooling. Its
  `pota`/`live::pota` modules parse and fetch real POTA/SOTA activator spots; `model::SpaceWx` /
  `live::swpc` fetch real space-weather data (SFI/SSN/Kp/A-index/X-ray flux) from NOAA SWPC.
  Verified against the real public APIs, not mocked (31 POTA spots, 5 SOTA spots, real SFI=122/
  Kp=1.33 returned in a manual `#[ignore]`'d live-check test -- `EngineHost/tests/
  external_data_live_check.rs`, deliberately excluded from the default CI suite since it depends
  on external network availability, not code correctness).
- **EngineHost's `external_data.rs`**: a background-refreshed cache (`SharedCache`, POTA/SOTA
  every 90s, space weather every 10 min), exposed to Jimmy over the same control-port protocol
  every other Direct command uses (`OTA_SPOTS`, `SPACE_WX` -- fast, cache-only reads). Graceful
  degradation: if one feed (POTA or SOTA) fails, the cache keeps serving the other plus the last
  good data rather than going blank.
- **`ExternalDataClient.cs`**: standalone C# client (no WinForms/Controller reference, same
  standalone-class discipline as `LiveQsoUploadOrchestrator`) for all of this session's new
  EngineHost commands, including the eQSL/HamQTH ones above.
- **`OtaSpotAnnotator.cs`**: applies Jimmy's own existing worked-before (`LogbookDb
  .HasWorkedBefore`) and needed-for-award (mirrors `AwardMatcher.Match`'s per-`GroupBy` switch,
  counting every match instead of just the first) logic to a spotted callsign. Deliberately does
  **not** touch `AwardMatcher`/`RuleEngine`/`Awards` -- POTA/SOTA is not folded into the awards
  engine's own design, exactly as instructed; this is a new, additive, read-only consumer of data
  those systems already expose publicly.
- **`OtaSpotsWindow.cs`**: the accessible presentation. A plain `ListView` (View=Details) --
  natively keyboard-navigable, no custom accessibility plumbing needed -- with columns Program /
  Reference / Activator / Freq-Mode / Age / Status ("worked before, needed for N awards"),
  matching the requested presentation style ("K1ABC -- POTA K-1234 -- 20m FT8 -- spotted 2 min
  ago -- needed for 2 awards") via each row's tooltip/accessible text. Non-modal singleton window,
  following `LogbookWindow`'s exact established pattern (own `LogbookDb` instance, `Show()` not
  `ShowDialog()`, no `Owner`, cleanup via `FormClosed` + `Controller_FormClosing`). Opened via a
  new button next to Logbook and a new `Alt+G` hotkey (`HotkeyAction.OpenOtaSpots`), both routed
  through the existing `HotkeyConfig`/`OptionsDlg` hotkey editor like every other shortcut.
  Deliberately plain -- no custom dashboard rendering -- so JAWS/NVDA read it the same as any
  other list already in the app.

**Deferred, documented, not attempted this pass: POTA/SOTA notifications.** The operator asked
for optional, non-chatty notifications when a worth-chasing spot appears. `OtaSpotsWindow` is
pull-only (operator opens it, sees current facts) and satisfies the core accessibility
requirement without this. A push notification needs: a background poll independent of the window
being open (today, nothing fetches OTA data unless the window is open), and per-spot dedup state
so the same activator/reference doesn't re-announce every refresh cycle. Jimmy already has a
complete, well-tested notification framework for exactly this shape of problem
(`Notify/NotificationEventType.cs`, `NotificationCenter`, `NotificationPolicy` with
Timing/DeferWhileTransmitting/SuppressUnchanged) -- the clean path is a new
`NotificationEventType.OtaSpotWorthChasing` following that framework's existing "one new type,
zero INI migration needed" design, published from a small Controller-owned poll loop, defaulting
to a conservative timing (e.g. deferred, not Immediate) so it can never compete with FT8/FT4
Tx-period timing. Establishing this now, without a working background poll and dedup story built
and tested to the same standard as the rest of this pass, would be exactly the kind of rushed
half-feature the operator's own instructions warn against -- recorded here as the next concrete
step instead.

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
rollover) that had zero direct test coverage before. Full suite: 890/890 passing as of this pass
(includes `OtaSpotAnnotatorTests` added alongside the POTA/SOTA work above).

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
- eQSL download/reconciliation (upload and HamQTH lookup are implemented -- see "Logbook /
  logging comparison" above for exactly what's done vs. open).
- HamQTH as a full `LookupManager` provider-chain member (currently on-demand only in the Lookup
  Selected Station dialog -- see above).
- POTA/SOTA "worth chasing" notifications (see "Nexus-backed facts beyond FT8/FT4" above for the
  concrete design seam left for this).

Each of these is a substantial, independently-scoped piece of work; attempting them without the
same proof-before-cutover rigor used elsewhere in this pass (isolated change, full test suite,
real build verification before commit) would risk exactly the kind of regression the operator's
plan explicitly warns against.
