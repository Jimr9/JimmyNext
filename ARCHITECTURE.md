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
| `Logbook/` | 15 | The QSO database and ADIF/sync logic. Data-sensitive -- evaluated, not touched, per the operator's explicit instruction to be conservative here. |
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

**2. Available in Nexus and should be used by Jimmy now:**
- **DXCC entity / continent / state resolution.** Jimmy's `LookupManager` currently resolves a callsign's country/continent by calling out to QRZ/Club Log/FCC ULS/LoTW -- network-dependent, needs credentials/API keys for the richer providers, and subject to each provider's own rate limits and occasional downtime. Nexus's `propagation` crate (`crates/propagation/src/dxcc.rs`, and a substantial new `crates/propagation/src/province.rs` for US state/VE province resolution) is a local, offline, instant DXCC-entity/state resolver -- exactly the "factual DXCC/entity/state/grid/geography information" the operator's plan calls out. This looks like a genuine win: a fast local fact source Jimmy could use as a fallback (or primary source, with QRZ/Club Log kept for the richer enrichment they alone provide -- confirmed-QSL status, bio, etc.) when Jimmy's own network lookup is disabled, rate-limited, or offline. **Not implemented in this pass** -- swapping or augmenting the fact source `ClassificationEngine` and `AwardTagger` both depend on needs a proven-parity comparison against Jimmy's current QRZ/Club Log-derived country/continent strings before it can be trusted for award-triggering decisions (a wrong DXCC entity is a wrong award claim). Recommended as the top candidate for the next development pass, with a shadow-comparison test (compute both, log any mismatch, don't act on Nexus's answer yet) as the proving step before cutover -- the same pattern already used successfully for the WSJT-X-wire-vs-ClassificationEngine migration (`ClassificationCutover.cs`).

**3. Available in Nexus but needs more Jimmy work before it is useful:**
- `bandplan.rs` / `privileges.rs` (amateur-radio band edges + license-class privilege checking). Jimmy has no equivalent today (no TX-legality warnings) and no operator-facing feature that would consume it yet -- would need a UI/workflow decision first, not just a wire-up.

**4. Useful for a later Jimmy feature, not now:**
- FT2, Q65, WSPR, MSK144, JT65, FST4/FST4W, SSTV, DeepCW -- all present and apparently mature in Nexus (the pinned revision alone added full FT2 support, 172 commits of work across the other modes). FT8/FT4 remain the shipping focus per the operator's direction; no mode work attempted or recommended in this pass.
- Nexus's Direct-adjacent `connHealth`/settings-registry work in its own Tauri UI (`ui/src/settings/connHealth.ts`, `ui/src/settings/registry.ts`) is conceptually close to what Jimmy's own Direct-contract "central capability model" goal describes -- worth a look if that work starts, not evaluated in depth here.

**5. Not appropriate for Jimmy:**
- Nexus's own Tauri/React UI (`ui/`) -- Jimmy's accessible WinForms UI is the whole point; not a candidate for reuse in any form.
- Nexus's own logbook (`crates/tempo-core/src/logbook.rs`) -- Jimmy's logbook is data-sensitive, already correctly evaluated as staying Jimmy-owned per the operator's explicit conservatism instruction on logbook/user-data.

## Not yet done (scope for a future pass, not attempted here)

- Systematic duplication audit beyond radio ownership + the DXCC finding above (the plan asks
  for a broad review across QSO sequencing, callsign normalization, and more -- what's above is
  what surfaced from a focused, not exhaustive, pass).
- Modularization of the ~28,800-line top-level `WSJTX_Controller/*.cs` into smaller focused
  areas.
- Direct-contract hardening (capability negotiation, structured errors/correlation IDs).
- TX-safety/recovery audit beyond confirming Nexus already owns the physical PTT path.
- CI wiring for `scripts/prepare-nexus.ps1` + EngineHost tests.
- Installer version bump / MSI release-candidate production.

Each of these is a substantial, independently-scoped piece of work; attempting them without the
same proof-before-cutover rigor used for the Nexus dependency fix in this pass (isolated patch,
full test suite, real build verification before commit) would risk exactly the kind of
regression the operator's plan explicitly warns against.
