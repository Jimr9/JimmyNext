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

**Direct is the sole production transport.** `Jimmy Test -> Direct (TCP control port) ->
EngineHost -> Nexus` is not "the preferred path" or "the default" -- it is the only one real
production code can reach. `Controller.Form_Load` always calls `ApplyEngineMode()`
unconditionally (its own comment: *"Phase 4g: always launches the native engine host"*), which
outside `TestModeGuard.IsTestMode` always calls `ConnectDirectEngine()`
(`WsjtxClient.Direct.cs`) -- never the classic WSJT-X UDP protocol handling in
`WsjtxClient.Protocol.cs`. There is no remaining Options toggle, settings value, or startup
branch that can select anything else; the older "talk over classic WSJT-X UDP instead of
Direct" choice (`UseDirectEngine`) was fully removed in an earlier pass
(`NativeEngineSettings.cs`'s own comment), and a handful of user-facing error messages and
comments that still referenced a "switch Decode Engine back to WSJT-X External" escape hatch
were found stale and corrected during a focused audit (2026-08-17) -- that escape hatch no
longer exists; jimmy-engine-host.exe not being reachable is now a hard error, not a fallback
trigger.

The classic UDP protocol code (`WsjtxClient.Protocol.cs`, `Protocol/WsjtxProtocolAdapter.cs`,
`WsjtxUdpLib`'s message classes) was **not deleted** -- it is genuinely load-bearing test
infrastructure, not vestigial production code. `run_replay_tests.bat` sets
`TestModeGuard.IsTestMode`, which routes `ApplyEngineMode()` to `ConnectNativeEngine()`
(`WsjtxClient.Protocol.cs`) instead, opening a real UDP socket that `JimmyReplay.py` sends real
packets to against a real running `Jimmy Test.exe` process -- an end-to-end replay harness, not
an in-process mock. Deleting this would break that harness for no production benefit. Instead,
every entry point (`WsjtxClient.Protocol.cs`'s own top-of-file banner comment,
`ConnectNativeEngine`, `UdpLoop`, `WsjtxProtocolAdapter`'s class comment) now says explicitly
"test/replay-only in current production" rather than leaving that to be inferred or -- worse --
misread as a live alternate transport.

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

**2. eQSL and HamQTH -- full capability inventory, then implemented against what Nexus actually
provides.** These are logbook/logging services in the same family as Jimmy's existing QRZ/Club
Log/LoTW/HRDLog integrations. Rather than assuming what Nexus's `eqsl.rs`/`hamqth.rs` support,
read the pinned checkout's actual code (`tempo_core::eqsl`, `propagation::live::eqsl`,
`tempo_core::hamqth`, `propagation::live::hamqth`) end to end, cross-checked against Nexus's own
`docs/manual/Logbook-and-Awards.md`, to separate four things that are easy to conflate: what the
external service supports, what Nexus implements, what Nexus implements but didn't yet expose
through EngineHost, and what neither side has. Ownership split follows the same rule as
everywhere else in this document: **Nexus/EngineHost owns the external service's own API
plumbing** where Nexus already does it well; **Jimmy owns the local logbook, operator settings,
policy, and reconciliation with its own richer local records.**

**eQSL capability map:**

| Capability | eQSL.cc supports it | Nexus implements it | Jimmy Test uses it |
|---|---|---|---|
| Authentication (account username/password) | yes | yes (`EqslQuery`, per-request, no session) | yes |
| Individual QSO upload (ADIF) | yes | yes (`build_upload_body`, one record per call) | yes -- real-time, `LiveQsoUploadOrchestrator` |
| InBox / incoming-confirmation download | yes | yes (`fetch_inbox`, 2-step, incremental `RcvdSince` cursor) | yes -- `EqslReconciler` (see below) |
| OutBox / operator's own sent-log download | yes (eQSL.cc website) | **no** | no -- nothing to expose |
| Confirmation/status info | yes | yes (`EQSL_QSL_RCVD` per record; `classify_upload`'s 5-way outcome) | yes -- `eqsl_qsl_rcvd` column, informational |
| Retry / transient-error handling | -- | yes ("system is down" -> no stamp, clean retry) | yes -- surfaced as an error, never auto-marks confirmed |
| Duplicate handling | yes | yes ("duplicate" marker -> `Duplicate`, benign) | yes -- treated as already-sent, not a failure |
| Update / delete a QSL | yes (eQSL.cc website) | **no** | no -- nothing to expose |

**eQSL, what's actually wired up:** Upload uses `propagation::live::eqsl::post_form` +
`tempo_core::eqsl::build_upload_body`/`classify_upload` via `EQSL_UPLOAD`
(`external_data.rs::eqsl_upload`) -> `ExternalDataClient.UploadEqsl`, alongside QRZ/Club
Log/HRDLog in `LiveQsoUploadOrchestrator` -- own `eqsl_uploaded_at` tracking column (schema v7).
Download+reconciliation uses `fetch_inbox` via `EQSL_DOWNLOAD` (`external_data.rs::eqsl_download`
-- this pass also fixed a real gap: it validated `is_eqsl_adif` but not `is_complete_eqsl_body`,
so a truncated-but-HTTP-200 response could have been treated as complete) -> new
`EqslReconciler`/`LogbookDb.TryMarkEqslConfirmed` (schema v8, `eqsl_qsl_rcvd` column). This is
deliberately **not** the same upsert-or-create path `AdifImporter.Import` uses for QRZ/LoTW/Club
Log downloads (which treats the download as the operator's own full logbook and can create a
local row for anything missing) -- an eQSL InBox record is someone else's confirmation report
about a QSO, not a request to add one, so the reconciler only ever MATCHES existing rows
(callsign + band + date within +/-1 day, mode as a tie-breaker) and leaves anything ambiguous or
unmatched alone. `eqsl_qsl_rcvd` is informational only, kept out of `RuleConfirmation`'s
award-counting SQL entirely -- matches Nexus's own documented `confirmed=true` but
`award_confirmed=false` treatment of eQSL (eQSL is not an ARRL-recognized DXCC/WAS confirmation
source). No app-level credential: the operator supplies their own eQSL.cc login. Both directions
fail closed -- an eQSL error never blocks the QSO being saved locally or disrupts FT8/FT4
operation. eQSL OutBox download and update/delete are not built, because Nexus doesn't expose
them; writing a second eQSL client for those would be exactly the duplicate-implementation risk
this comparison exists to avoid.

**HamQTH capability map:**

| Capability | HamQTH.com supports it | Nexus implements it | Jimmy Test uses it |
|---|---|---|---|
| Authentication / session (~1h session id) | yes | yes (`HamQthLogin`/`HamQthSession`) | yes -- re-logs in per call, no session caching (see below) |
| Callsign lookup | yes | yes (`parse_callsign`) | yes -- real alternative primary provider |
| Returned fields | call/name/qth/grid/us_state/country/adif/cq/itu/picture/lat/lon/us_county | all of the above **except `us_county`** (parsed struct omits it) | call/name/qth/grid/state/country/**dxcc/cq_zone/itu_zone** (this pass added the numeric fields -- previously fetched by Nexus, then discarded before reaching Jimmy Test) |
| DXCC/ADIF numeric entity ID | yes (`<adif>`) | yes | yes -- see the DXCC-opportunity finding below |
| QSO upload / log download / sync | HamQTH.com has its own web logbook | **no** | no |
| Confirmation info | n/a for HamQTH the lookup service | **no** | no |
| Recent activity / spots | HamQTH.com has a DX cluster feature | **no** | no |

**HamQTH, what's actually wired up:** promoted from a QRZ-only "supplement whatever's blank"
add-on to a **real alternative primary online lookup provider**, comparable to QRZ. New
`HamQthProvider` mirrors `QrzProvider`'s shape exactly (own file cache, `Configure`/
`NeedsLookup`/`LookupAsync`/`GetCachedAt`/`TestAsync`) behind a new `IOnlineLookupProvider`
interface both now implement, so `LookupManager`'s existing automatic-lookup/policy/queue
machinery (built once for QRZ) routes through whichever one is selected
(`LookupManager.PrimaryProvider`) instead of hardcoding QRZ. Options > Lookup Data > "Callsign
Lookup Provider" picks QRZ or HamQTH; only the selected one makes a live network lookup per
callsign (the operator's explicit instruction: don't spend two live lookups to combine data) --
QRZ's own behavior is completely unchanged when it stays selected, the default for every existing
install. Both providers can still be independently enabled to passively contribute already-cached
data into `Build()`'s merge regardless of which is primary (no network cost -- same read-only
reuse every other provider already does). `HAMQTH_TEST` (login only, no lookup spent) backs a new
Options "Test Login" button, mirroring QRZ's own.

**QRZ vs HamQTH, what Jimmy Test actually gets from each:**

| | QRZ (via Jimmy's `QrzProvider`) | HamQTH (via `HamQthProvider`) |
|---|---|---|
| Name, Grid, State, Country | yes | yes |
| CQ Zone, ITU Zone | yes | yes |
| Numeric DXCC/ADIF entity | **not captured** (`QrzCacheEntry` has no field for it, even though QRZ's XML returns one) | yes |
| License class | yes | not returned by HamQTH |
| QSL Manager, Email | yes | not returned by HamQTH |
| County | yes (`<county>`) | not exposed (Nexus's parser drops `us_county`) |
| Lat/Lon, photo URL | not captured (QRZ's XML has `<geoloc>`, `QrzCacheEntry` doesn't store it) | parsed by Nexus, not surfaced in Jimmy's UI (no display slot -- documented below, not built) |
| Full data without a paid subscription | **no** -- free QRZ accounts return fewer fields | **yes** -- HamQTH's full data needs only a free account |
| Auth/session cost per lookup | 1 round trip after the first (23h cached session key) | 2 round trips every time (no session caching -- see below) |
| Rate courtesy | 150ms delay + single-slot semaphore | same (added for parity, no documented HamQTH limit found) |

Neither is strictly better: QRZ's paid tier can return more (license class, QSL manager, email);
HamQTH gives fuller data for free but costs an extra round trip per lookup (Jimmy's
`HamQthProvider` deliberately doesn't cache HamQTH's session id -- simpler and more robust for an
occasional operator-driven/queue-supplement lookup than threading a ~1h session lifetime through
a second process, matching the reasoning already recorded for the combined `HAMQTH_LOOKUP`
command). **Not surfaced by either side, a genuine future opportunity, not built this pass**: a
map/photo display for lat/lon and profile pictures -- neither `QrzCacheEntry` nor
`HamQthLookup`'s consumer currently has a UI slot for them, and adding one is a real (if small)
UI-design decision outside this pass's scope.

**Does HamQTH-through-Nexus change the ClubLogProvider/DXCC decision? No -- proven structurally,
not merely assumed.** The earlier DXCC shadow comparison rejected Nexus's own `propagation::
dxcc::resolve()` because it returns only an entity name, not the numeric ADIF ID `AwardTagger`
needs. HamQTH's `dxcc` field IS numeric -- but it fails the *complete* requirement for the live
per-decode tagging hot path for two independent, structural reasons, not accuracy: (1) it
requires a live network round trip per callsign, incompatible with a path that must handle every
decode, many times per FT8/FT4 cycle, entirely offline; (2) it is the *contacted station's own
self-reported HamQTH profile address*, not a prefix-to-entity algorithm -- coverage is limited to
callsigns that happen to have a HamQTH account with an address configured, versus
`ClubLogProvider`'s near-universal offline prefix resolution. Both reasons hold regardless of
per-lookup accuracy, so no live comparison against the earlier difficult test cases was needed to
reach a confident answer. **`ClubLogProvider` stays authoritative for live award-tagging DXCC
resolution, unchanged.** HamQTH's numeric DXCC is still genuinely useful for the on-demand
per-station Lookup dialog, where an operator-initiated network round trip is expected -- already
wired via `HamQthProvider.Contribute()`'s blank-fill into `Build()`, positioned after
`ClubLog`/`Qrz` in `LookupManager`'s provider order so it only fills what they left blank.

**3. Diagnostic/application logging vs. QSO logbook.** Kept explicitly separate per the operator's request. Jimmy's `SupportReportBuilder.cs` (766 lines, already redacts credential-shaped keywords before building a support report) is diagnostic-only and has no overlap with QSO data. Nexus's own diagnostics (`crates/tempo-core/src/diagnostics.rs`) were not compared in depth -- no evidence surfaced during this pass that Jimmy's diagnostic logging has a gap worth closing, and application/crash diagnostics carry none of the QSO-data risk that would make this urgent for a release-candidate pass.

**Decision: Jimmy's logbook/upload stack stays fully authoritative; eQSL and HamQTH are additive integrations on top of it, not a replacement or migration.** Both are now genuinely usable end to end (eQSL upload+download+reconciliation, HamQTH as a real alternative primary lookup provider), built strictly against what Nexus actually implements -- nothing was duplicated in C# that Nexus already does, and nothing was claimed that Nexus doesn't yet support (eQSL OutBox/update/delete, HamQTH upload/download/sync/spots all remain unbuilt because Nexus has no such capability to expose, not because Jimmy Test chose to skip them).

## Nexus-backed facts beyond FT8/FT4: POTA, SOTA, DX spots, band conditions, space weather

Per operator request: take advantage of useful Nexus information beyond the basic FT8/FT4 engine
-- POTA, SOTA, DX/current spots, propagation/band-condition text, and space-weather facts --
without copying Nexus's own visual UI. Ownership split follows the same pattern as everything
else in this document: **Nexus supplies the facts, Jimmy applies its own operator intelligence
and presents them accessibly.** Delivered in two passes: POTA/SOTA + space weather first, then
(after the operator asked specifically where the rest of the promised categories actually were)
DX cluster/RBN spots and a real plain-language band-conditions nowcast, all as tabs of the same
`Alt+G` window rather than separate windows.

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
- **`OtaSpotsWindow.cs`**: the accessible presentation, now a `TabControl` with four independently
  refreshable tabs (own Refresh button per tab, plus a shared 15s background timer and F5). Every
  list is a plain `ListView` (View=Details) -- natively keyboard-navigable, no custom
  accessibility plumbing needed. Non-modal singleton window, following `LogbookWindow`'s exact
  established pattern (own `LogbookDb` instance, `Show()` not `ShowDialog()`, no `Owner`, cleanup
  via `FormClosed` + `Controller_FormClosing`). Opened via a button next to Logbook and the
  `Alt+G` hotkey (`HotkeyAction.OpenOtaSpots`, fully remappable through the same
  `HotkeyConfig`/`OptionsDlg` editor as every other shortcut). Deliberately plain -- no custom
  dashboard rendering -- so JAWS/NVDA read every tab the same as any other list already in the
  app.
  - **POTA / SOTA** tab: Program / Reference / Activator / Freq-Mode / Age / Status ("worked
    before, needed for N awards"), matching the requested presentation style ("K1ABC -- POTA
    K-1234 -- 20m FT8 -- spotted 2 min ago -- needed for 2 awards") via each row's tooltip.
  - **Band Conditions** tab: Nexus's own `propagation::PropAdvisor` (advisor.rs) -- "data-driven,
    plain-language what's-open-now, no VOACAP expertise required" (its own doc comment) -- run
    over a rolling window of the operator's own PSK Reporter reciprocal spots ("who hears me / who
    I hear", fetched live via `tempo_net::mqtt`'s hand-rolled MQTT client + `propagation::
    pskr_mqtt`, mirroring Nexus's own `start_pskr_feed` exactly). Shows a one-sentence headline,
    a per-band Tier/Confidence/stations-heard/best-region/reason table, and any alert banners.
    Always on -- mycall/mygrid are already known to EngineHost at startup, no operator
    configuration needed. Deliberately does NOT replicate Nexus's own region/worldwide-activity
    matrix, openings tracker, or map-spot building (`get_propagation` in Nexus's Tauri app) -- that
    whole surrounding machinery supports Nexus's own visual dashboard and is out of scope for
    "useful textual conditions rather than Nexus's visual maps."
  - **DX Spots** tab: a real DX-cluster/RBN telnet feed (`tempo_net::cluster`, the same
    session/parse core Nexus's own app uses, "fully unit-tested (no socket)" per its own doc
    comment) -- DX call, frequency, mode (RBN skimmer spots carry a trusted machine-generated mode
    token; human cluster spots never do, by design), spotter, age, comment. Opt-in only: unlike
    PSK Reporter's one public broker, a DX cluster is an independently-run federation with no
    universal default, so this requires the operator to configure a server address (Options >
    Decode Engine tab > "DX Cluster server", `host:port`) -- empty (default) leaves the tab
    reporting "not configured" rather than attempting a connection. Startup-CLI-arg-only
    (`--dx-cluster`), same convention as the Decode tab's other non-live-settable options --
    changing it restarts the native engine, exactly like changing My Call/My Grid/audio device
    already does.
  - **Space Weather** tab: SFI, SSN, Kp, A-index, and X-ray flux as plain read-only fields (the
    same `SPACE_WX`-cached data the Band Conditions tab's advisor also reads) -- presented as
    facts, not interpreted; the Band Conditions tab is where interpretation belongs.
- **New EngineHost module, `live_feeds.rs`**: owns both live feeds above (`LiveFeedsCache`), same
  background-cache-with-graceful-degradation discipline as `external_data.rs`'s `SharedCache` --
  a feed that can't connect, or hasn't produced enough data yet, degrades to "no data yet" /
  stale-but-real cached data, never affects engine/decode/TX. New control-port commands
  `BAND_CONDITIONS` and `DX_SPOTS`, both cache-only reads (fast enough to run inline on the
  control server's accept loop, same as `SNAPSHOT`/`OTA_SPOTS`/`SPACE_WX`). Added `tempo-net` as
  an EngineHost dependency for this -- confirmed low-risk before adding it: both `tempo_net::mqtt`
  and `tempo_net::cluster` are hand-rolled, pure-`std::net::TcpStream` clients (no external MQTT/
  telnet crate, no new native/transitive dependencies), matching the risk bar every other Nexus
  reuse in this project was held to.

**Alt+G accessibility/functionality pass (2026-08-17), after a live JAWS test found real bugs.**
Every bullet above describing the DX Spots tab as opt-in-only / requiring a configured server is
now the OLD design; what actually shipped:

- **RBN digital skimmer (`telnet.reversebeacon.net:7001`, FT8/FT4/RTTY/PSK) is now always on**,
  no operator configuration needed -- mirroring official Nexus's own desktop app default exactly
  (`kd9taw/Nexus src-tauri/src/lib.rs`'s `start_cluster_feeds`/`RBN_DIGITAL_HOST`, "the RBN CW +
  digital skimmer feeds are wired automatically" per its own `Settings::default` comment). The
  CW-only port (7000) is deliberately not wired -- Jimmy Test is FT8/FT4-only. The Options >
  Decode Engine "DX Cluster server" field is now purely an ADDITIVE, optional human-cluster node
  (SSB/phone + human-typed spots RBN's automated skimmers don't cover) -- leaving it blank still
  gives a working DX Spots tab. Both sources push into one shared, de-duped `SpotBuffer`
  (`live_feeds.rs`), each spot tagged `rbn: true`/`false` at the push site, same convention
  official Nexus uses.
- **Band Conditions no longer blanks to 0 items whenever the operator has no PSK Reporter
  reception reports yet.** The previous `spots.is_empty()` branch in `band_conditions_json`
  discarded `PropAdvisor::advise()`'s own physics-only fallback (MUF/absorption/aurora/greyline
  prior producing a soft Quiet/Closed gradient per band -- see `advisor.rs`'s own test suite),
  even though `advise()` already handles an empty spots window correctly. Removed; `advise()` now
  runs whenever space weather is available, spots or not, and the UI status line says
  "modeled only, no reception reports yet" when spot count is 0 so the distinction stays honest.
- **Space Weather's A-index/X-ray always read "0.0"/"0.0e+0"** -- looking like real
  measurements, not missing data. Root cause: `SPACE_WX`'s response serialized Nexus's own
  `SpaceWx` type verbatim, whose Rust field names (`a_index`, `xray_long`) don't match what
  Jimmy Test's `JsonNamingPolicy.CamelCase` deserialization looks for (`aIndex`, `xrayLong`);
  `System.Text.Json` silently leaves an unmatched property at its default rather than throwing.
  Fixed with a proper wire DTO on EngineHost's own side (`external_data.rs`'s `SpaceWxWire`,
  never touching the vendored Nexus type), which also now surfaces Nexus's own existing
  `xray_class()`/`r_scale()` classifications (standard NOAA flare-class letter and radio-blackout
  scale) next to the raw reading. Genuinely-missing fields (SSN -- no R12 feed currently wired)
  now read "Unavailable" instead of an ambiguous "--".
- **POTA/SOTA row status text collapsed from two clauses to one** -- "not worked before, not
  currently needed" on nearly every row (the common case) is now just "not worked"; "needed for N
  awards" replaces rather than appends to the worked/not-worked clause, so it stands out instead
  of being buried in a compound sentence.
- **Accessibility repetition removed**: the window title, TabControl, every TabPage, and every
  ListView/status-label AccessibleName were each independently restating "POTA and SOTA"/"DX
  cluster and RBN"/etc., so JAWS said the same words three or four times moving from window to
  tab to list. Brought in line with `LogbookWindow.cs`'s own convention (no custom
  AccessibleName on TabControl/TabPage at all -- standard WinForms tab announcement is already
  correct; short, non-repeating list/status names like "Spots list"/"Status"). Window title
  shortened from "POTA / SOTA / DX Spots" (which repeated two of the four tab captions verbatim)
  to "Spots & Conditions".

**Alt+G follow-up pass (2026-08-17b): a second live JAWS finding, plus a Nexus-data audit.**

- **Tab bleed-over bug, root-caused and fixed**: JAWS was heard announcing DX Spots' status text
  ("500 spots -- last spot 0s ago -- add a DX cluster server...") while the operator sat on the
  Space Weather tab. Verified NOT a parenting bug (every control is `Controls.Add`ed to its own
  `TabPage` only, confirmed by direct read; no shared/reused control instances; WinForms
  `TabControl` does set `Visible = false` on every non-selected page). Root cause: the periodic
  refresh called `RefreshAll()`, which updated **every** tab's `Label.Text` on every tick
  regardless of which page was actually selected -- and a `Label.Text` assignment fires that
  control's own accessibility name-change notification whether or not its page is currently
  visible, which JAWS can surface anyway. Fixed the normal WinForms way: a new
  `RefreshActiveTab()` dispatches only to the currently selected tab's own refresh method,
  wired to the timer tick, `F5`, initial `Load`, and `TabControl.SelectedIndexChanged` (so
  switching tabs gets an immediate fresh read). EngineHost's own background feed threads (PSK
  Reporter MQTT, RBN) are unaffected -- they keep running and keep their cache warm regardless of
  which tab the UI is currently polling.
- **Nexus propagation data audit** (per an explicit request not to duplicate or invent anything):
  read through `predict.rs`, `likelihood.rs`, `swpc_scales.rs`, and `pca.rs` for genuinely useful,
  already-computed values Jimmy Test wasn't surfacing.
  - **MUF exists**: `propagation::representative_muf` (used internally by
    `predict::modeled_now`, which `advisor.rs` already calls for Band Conditions' physics prior,
    but never itself surfaced in any payload). It is the **ring-max controlling MUF** -- the
    classical foF2 x obliquity model (`likelihood.rs`'s `PathModel::muf`), evaluated over 8
    evenly-spaced ~9000 km directions from the operator's own grid, taking the maximum. **Not** a
    specific DX path's MUF, and not an observed reading -- a physics-only "best case somewhere
    long-haul, right now" ceiling. Added to `SPACE_WX`'s response as `mufNow` (`EngineHost/src/
    external_data.rs`'s `SharedCache`, now grid-aware via a `mygrid` constructor arg), shown on
    the Space Weather tab as "Representative MUF (best long-haul): NN.N MHz". Deliberately NOT
    touched: Band Conditions' own evidence/scoring model (`advisor.rs`, `live_feeds.rs`) --
    exposing the raw MUF number on the Space Weather tab doesn't touch how bands get
    scored/tiered/ranked there.
  - **NOAA's own G (geomagnetic storm) and S (solar radiation storm) scales exist and were
    completely unused**: `propagation::live::swpc_scales::fetch_noaa_scales()`, a real,
    fully-implemented fetcher (`products/noaa-scales.json`) EngineHost had simply never called.
    Added as a second, independently-refreshed cache in `SharedCache` (own age/error, since it's
    a separate SWPC product from the SFI/Kp/X-ray feed), folded into the same `SPACE_WX`
    response as a `scales` object. R (radio blackout) is deliberately **not** re-fetched from
    this product: NOAA defines R purely as a function of GOES X-ray flux, which the existing
    `rScale`/`xrayClass` fields already carry from the exact same raw reading -- fetching NOAA's
    own copy of the identical number would be a second source for the same fact, not new
    information.
  - **Investigated but NOT added**: a standalone "absorption" number (D-layer absorption is a
    private intermediate inside `PathModel::score`, never exposed on its own -- exposing one
    would mean writing new extraction code, not surfacing something Nexus already hands out);
    polar-cap absorption (`pca.rs`, a real Sauer & Wilkinson 2008 model, but proton-event-
    triggered, path/latitude-specific, and would need a new proton-flux fetch plus per-path
    geometry -- disproportionate to a global Space Weather tab); SWPC alert bulletins
    (`AlertView` -- real, human-readable NOAA watches/warnings, but free-text and
    variable-length, a poor fit for the existing fixed label/value row layout without more
    design work); a "most usable band right now" recommendation from `predict::band_outlook_ring`
    (a real, purely-physics-based band ranking that is architecturally more principled than
    Band Conditions' current hardcoded-40m headline fallback -- but touching that headline is
    exactly the "evidence/scoring model" this pass was told to leave alone).

**Deferred, documented, not attempted this pass: POTA/SOTA/DX-spot "worth chasing" notifications.**
The operator asked for optional, non-chatty notifications when a worth-chasing spot appears.
`OtaSpotsWindow` is pull-only (operator opens it, sees current facts) and satisfies the core
accessibility requirement without this. A push notification needs: a background poll independent
of the window being open (today, nothing fetches this data unless the window is open -- except
the two live feeds behind Band Conditions/DX Spots, which now DO run continuously regardless of
whether the window is open, since PropAdvisor needs a populated rolling window to be useful the
moment the operator looks; POTA/SOTA's own refresh is still window-gated), and per-spot dedup
state so the same activator/reference/DX call doesn't re-announce every refresh cycle. Jimmy
already has a complete, well-tested notification framework for exactly this shape of problem
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
rollover) that had zero direct test coverage before. Full suite: 908/908 passing as of this pass
(includes `OtaSpotAnnotatorTests`, `EqslReconcileTests`, and `LookupManagerPrimaryProviderTests`
added across this and the prior POTA/SOTA/eQSL/HamQTH pass).

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
- eQSL OutBox (operator's own sent-log) download, and update/delete of a QSL -- Nexus doesn't
  implement either, so nothing to expose (see the eQSL capability map above). eQSL upload,
  download, and reconciliation ARE implemented.
- HamQTH QSO upload/log download/sync/spots -- Nexus doesn't implement any of these for HamQTH
  (it's lookup-only in Nexus, matching HamQTH's own "free fallback for QRZ" role there). HamQTH
  callsign lookup IS implemented, as a full alternative primary `LookupManager` provider (not
  merely on-demand supplement, as an earlier pass had it).
- A map/photo display for lat/lon and profile-picture fields QRZ/HamQTH both can return -- neither
  side's lookup DTO currently has anywhere in Jimmy's UI to put them; a real but small UI-design
  decision, not attempted here (see the QRZ vs HamQTH comparison above).
- POTA/SOTA/DX-spot "worth chasing" notifications (see "Nexus-backed facts beyond FT8/FT4" above
  for the concrete design seam left for this).
- Band Conditions' region/worldwide-activity matrix, openings tracker, and map-spot building
  (Nexus's own `get_propagation`) -- deliberately not replicated; that machinery supports Nexus's
  own visual dashboard, out of scope for "useful textual conditions rather than visual maps."

Each of these is a substantial, independently-scoped piece of work; attempting them without the
same proof-before-cutover rigor used elsewhere in this pass (isolated change, full test suite,
real build verification before commit) would risk exactly the kind of regression the operator's
plan explicitly warns against.
