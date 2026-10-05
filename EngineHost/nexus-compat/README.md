# Jimmy Next-owned Nexus compatibility patches

Nexus (https://github.com/kd9taw/Nexus) is a third-party, GPL-3.0 project. Jimmy is a
**consumer only**: we do not own it, do not modify the official checkout, and never submit
changes back to it (see "Rules" below).

Jimmy Next's EngineHost builds against a clean checkout of Nexus at the exact revision pinned
in `pin.txt`, with a small number of source patches applied on top by `scripts/prepare-nexus.ps1`
(run automatically as part of the normal build -- see that script). Those patches are the
**only** difference between what Jimmy actually builds and official upstream Nexus, and they
exist for exactly one reason: to carry functionality Jimmy Next genuinely requires that official
Nexus, at the pinned revision, does not yet provide (or, for two patches, to work around Windows
build/test-toolchain interactions in Nexus's own build script and test suite).

Every patch here is small, isolated to one file, and removed the moment official Nexus provides
equivalent functionality -- see "Checking a patch against a newer Nexus" below.

## Currency check (do this at every significant release)

Run `scripts/nexus-status.ps1` (read-only -- it never changes the pin). It prints the Jimmy
base vs the current upstream stable tag and `main`, the commits-behind counts, and the ordered
patch manifest with SHA-256s. `pin.txt` carries a `# DISPOSITION:` line stating the current
decision (upgrade to stable / stay pinned with reason+expiry / take a named intermediate fix);
update its date + text at each release. `scripts/prepare-nexus.ps1` records the full provenance
(repo, ref, resolved commit + date, upstream stable/main heads at prep time, per-patch SHA-256,
tool versions) into `EngineHost/.nexus-src-info.json`. This process exists because the
pre-v1.10.3 integration drifted ~175 commits behind `main` silently -- exact pinning gives
reproducibility, this gives visibility.

## Next upgrade readiness -- reviewed 2026-09-29 (pin stays v1.15.0)

Reviewed upstream `main` at 191 commits past `v1.15.0` (no newer stable tag yet). Decision:
stay pinned; do NOT import `main`. What the next stable upgrade needs:

- **Patches to re-anchor:** upstream changed `tempo-app/src/engine.rs` (+367), `settings.rs`
  (+147), `dto.rs` and `lib.rs` -- `tempo-app-engine.patch`, `tempo-app-settings.patch` and
  `tempo-app-snapshot.patch` will need the usual re-anchor. `tempo-audio/src/rig.rs`,
  `service.rs`, `slot.rs`, `tempo-core/src/message.rs` and `inbox.rs` are unchanged upstream.
- **RFPOWER never-touch stays.** `rig.rs`/`service.rs` are unchanged upstream: Nexus still reads
  and writes RFPOWER with no guard, and Hamlib 4.7.1's Kenwood RFPOWER read still runs the
  power calibration sweep that leaves a TS-590 at 5 W (Hamlib/Hamlib#1595; Nexus issue #381,
  open, TS-590S "power forced to 5W"). Remove the patch only when upstream fixes it and the fix
  is verified on the air.
- **Logbook conflict fix (038fdb27, `logstore.rs`/`logwrite.rs`):** a window no longer takes
  another window's delete in as a stamp. Jimmy Next runs one engine writer with its own read
  copy (NexusMigration.Rebuild), so the multi-window case does not arise today; after the
  upgrade, re-run the logbook sync tests (edit, delete, re-import) to confirm the read copy
  still follows every change kind.
- **Park attribution (b575051a, 454c40a1):** the HUNT-ed park fills in for every activator
  clicked (matched by base call) and never rides onto another station's contact. Jimmy Next
  does not use Nexus's HUNT; it attributes parks itself (StationLocation.TryFindActivation, by
  spot + band). Keep Jimmy's safeguards ("blank rather than wrong" state) -- nothing upstream
  replaces them.
- **Settings writer (75e90a93..f7c542f2):** settings.json is now written off the Engine lock and
  a save sends only changed fields. The engine host builds Settings from its arguments and
  changes them live with `apply_settings` (SET_CLOCK_CHECK, SET_WORKING_FREQUENCIES,
  APPLY_SETTINGS); re-check those three after the re-anchor.
- **Jimmy's own engine commands added 2026-09-29** (not patches, `EngineHost/src/main.rs`):
  SET_CLOCK_CHECK uses Nexus's public `apply_settings` + `clear_clock_offset(true)`; ATU_STATUS
  reads PTT with Nexus's `Rig::read_ptt`. Both are public API -- check they still exist.

## Nine-patch re-assessment -- 2026-10-04 (v1.15.0 -> v1.16.0 upgrade)

Pin moved to stable **v1.16.0** (`21ac4c13`, 652 commits past v1.15.0), operator approved.

- **`tempo-audio-rig.patch` retired.** Both of its concerns are upstream now:
  - **DATA/ACC PTT** -- Nexus #381's own "Transmit audio source (CAT PTT): Rear/Data"
    (`Settings.tx_audio_source` = `"rear"`, keyed `T 3` through `PttMode::CatData`). EngineHost
    maps Jimmy's unchanged "Transmit Audio Source: Data" option onto it (`tx_audio_source()` in
    main.rs). Behaviour difference: upstream keys `T 3` only for a radio whose Hamlib driver has
    mic/data PTT (`rigmodels::PTT_MIC_DATA_RIGS`, TS-590S/SG included); any other radio keys `T 1`.
  - **RFPOWER** -- the operator chose the upstream approach. Nexus never sends the RFPOWER
    READ to the 14 models whose Hamlib read writes the radio's power (`hamlib_never_send` /
    `hamlib_rfpower_read_writes_power`, the Kenwood family -- Hamlib#1595). Power WRITES are
    left to Nexus, which makes them only from settings Jimmy never sets: an operator power level
    (`Engine::rf_power`, `None` until set), a per-mode cap below 100% (none for digital by
    default) and Tune power (`tune_power_pct: None` = never touch). Jimmy's never-touch Layer 1
    (rig.rs) and Layer 2 (service.rs gates) are gone. **Needs the on-air check** (connect,
    reconnect, mode change, Tune, ATU) -- no radio was available for this upgrade.
- **`tempo-app-settings.patch`** now adds only `dont_set_mode` (upstream's `set_rig_mode` is
  deprecated and ignored, so "Mode: None" is still Jimmy's).
- **`tempo-audio-service.patch`** lost its 14 data-PTT / RFPOWER hunks and the four tests that
  covered them; the Fake-It restore, `Status.tx_message` and "Mode: None" test remain.
- The other seven patches carry the same code at new offsets (payload checked unchanged).
- **EngineHost:** every logbook write a command waits for goes through
  `logwrite::until_written` (new with 1.16.0's two-windows-on-one-log store): made once, a change
  the store turned back reached Jimmy as "not saved" -- caught by the logbook integration test.
- Upstream bugs #399 (LoTW first sync omits `qso_qslsince`) and #400 (`take_match` pairs by list
  position) are still open and still in 1.16.0; Jimmy's 1900-01-01 cursor and NexusReportPairing
  stay.
- Note: tempo-audio's service.rs tests need `--features device`; without it a filter matches none.

## Ten-patch re-assessment -- 2026-09-28 (v1.14.0 -> v1.15.0 upgrade)

Re-checked against the new pin `v1.15.0` (`f47d43cc`, an ANNOTATED tag -- tag object
`458297d7`), 595 commits ahead of `v1.14.0` (`12efe3d2`). Each patch's "How to check" was run
against the actual v1.15.0 files. **Result: all ten KEEP; one patch SHRANK.**

- `tempo-core-message.patch`: the upstream #303 backport (`is_call_field`, both callsign fields
  must be callsigns) is now IN the base, so that part was removed -- with its test and its
  `is_signoff` doc/test changes. Only the no-grid part remains (`is_plain_std_call` + the
  two-word Type 1 branch, placed before upstream's own #303-gated three-token arm).
  Upstream still asserts `Msg::parse("CQ W1AW")` is `Msg::Other`, so the no-grid part (and its
  companion `tempo-core-inbox.patch`) is still needed.
- `tempo-app-snapshot.patch`, `tempo-app-engine.patch`, `tempo-audio-service.patch`:
  re-anchored only. Upstream added `cat_share_error` (#165) beside `audio_error` in
  `RadioStatus`, the `Engine` struct/init and the snapshot emit, and new TX-meter capability
  fields beside `level_misses` in `RadioLoop`. Every `+`/`-` payload line is byte-identical to
  the v1.14.0 patches (checked) except one test line in `tempo-app-engine.patch`: upstream
  renamed the test helper `Engine::get_log()` to `stored_log()` (`test_util.rs`), so the Jimmy
  test `an_ordinary_qso_completes_on_its_own_partners_multi_answer_roger` now calls
  `stored_log()`. Found only by compiling the lib tests -- a clean patch apply and a clean
  `cargo build` both missed it.
- The other six applied with offsets only.
- `scripts/nexus-status.ps1` now reads peeled (`^{}`) tag refs, so an annotated stable tag no
  longer shows a false "pin differs from stable" result.

## Eight-patch re-assessment -- 2026-09-24 (v1.10.3 -> v1.14.0 upgrade)

Re-checked against the new pin `v1.14.0` (`12efe3d2`), ~1369 commits ahead of the prior pin
`v1.10.3` (`7618390`, spanning the `v1.11.0`/`v1.11.1` betas and the `v1.12.0`/`v1.13.0` stable
tags). Each of the 8 patches' individual behaviors was checked with its own "How to check" grep
against the actual v1.14.0 file contents (not commit messages) -- see "Audited upgrade,
v1.10.3 -> v1.14.0" below for the full per-behavior evidence. **Result: all eight KEEP, zero
deletions.** None of the 8 behaviors gained an upstream equivalent. Every patch needed only a
mechanical re-anchor: upstream inserted unrelated new fields/lines near several anchors (an
FT-710 RF-scope DTO block, a Remote-control revocation system, a new JS8 ingest hook, a
`sstv_hold_data_submode` field, a fourth RFPOWER-adjacent read site), but never touched the
patched lines themselves. Next re-assessment at the next Nexus stable tag. Long-term goal: this
count decreases as upstream matures; nothing here is superseded at v1.14.0.

## Rules

- The real `C:\claude\nexus` checkout (or whatever machine-local Nexus clone is configured) is
  **never** modified. `prepare-nexus.ps1` always builds from a **separate** checkout it creates
  and owns (see that script), never the developer's reference clone.
- We do not open PRs, issues, or any other contact with the Nexus project about these patches.
  They are Jimmy-internal only.
- A patch here is the **minimum** change needed to unblock Jimmy -- not a general improvement,
  not a refactor, not an opportunity to fix unrelated things in the same file.
- When official Nexus adds equivalent functionality, the corresponding patch is deleted and
  Jimmy moves to the official implementation. These patches are meant to shrink toward zero
  over time, not accumulate.

## Current pin

`pin.txt` points at the **newest official stable release tag, `v1.15.0`**, by its exact commit:

```
NEXUS_TAG=v1.15.0
NEXUS_COMMIT=f47d43cc1053b90b882785dbc95a49c32395a890
```

`NEXUS_COMMIT` is the real lock; `NEXUS_TAG` is the honest human reference. `v1.15.0` is an
ANNOTATED tag (tag object `458297d7`) whose peeled commit is `f47d43cc` (2026-09-26).
`prepare-nexus.ps1` clones `--branch v1.15.0 --single-branch` (the tag checks out in detached
HEAD, which is fine) and still verifies the resolved `HEAD` equals `NEXUS_COMMIT`. The `NEXUS_TAG=main`
branch-and-detach path in that script is retained for any future exact-commit-on-`main` pin
but is not used here.

## Audited upgrade, v1.10.3 -> v1.14.0 (7618390 -> 12efe3d2)

Agent-audited upgrade (2026-09-24) from the prior pin `v1.10.3` (`7618390`) to the newest tagged
stable `v1.14.0` (`12efe3d2`) -- **~1369 commits**, spanning the `v1.11.0-beta.1/.2`,
`v1.11.1-beta.1`, `v1.12.0`, and `v1.13.0` tags along the way. Chosen as simply the newest
official stable tag at the time of this upgrade; no intervening stable tag was skipped.

Each of the 8 patches' individual behaviors was independently re-verified against the actual
v1.14.0 file contents (not commit messages or docstrings), grepping/reading the pinned files
directly -- see each patch's own `###` section below for its current "How to check" command.
**Result: all eight KEEP, zero deletions, zero new patches.** Every anchor needed only a
mechanical rebase: upstream inserted unrelated new code near several of them, but never touched
the patched lines themselves. New code found nearby, for context and for the "possible future
opportunities" note below:

- An FT-710 RF-scope feature (`Settings.yaesu_rf_scope`, `RadioStatus.scope_error` /
  `scope_span_refused` / `scope_mode_code` / `scope_fix_start_mhz`) -- inserted immediately
  after `audio_error` in both `dto.rs`'s `RadioStatus` and `engine.rs`'s `Engine` struct/snapshot,
  exactly where `tempo-app-snapshot.patch` and half of `tempo-app-engine.patch` anchor.
  Visual-only; not relevant to a screen-reader-first product.
- A new Remote-control revocation system (`Engine.remote_actuation: remote_control::Revocation`,
  a new `crates/tempo-app/src/remote_control.rs`), wired via `self.remote_actuation.revoke()`
  into ~40 operator-facing `Engine` setters -- including `set_decode_depth`, which
  `tempo-app-engine.patch`'s new `set_pskreporter` sits next to. Not adopted into Jimmy's new
  setter (out of scope for this upgrade; see "possible future opportunities" below).
- A new `sstv_hold_data_submode` field on `Settings`/`RadioProfile` (widened to HF, #191),
  inserted immediately after `data_modes_plain_ssb` -- exactly where `tempo-app-settings.patch`
  anchors its three fields.
- A new JS8 decode-ingest hook (`self.js8_ingest(&decodes, slot)`) inserted in `process_decodes`
  immediately before the same-slot-accumulation overwrite `tempo-app-engine.patch` patches; it
  only reads `decodes` by reference before the patch's move, so there is no interaction.
- A fourth RFPOWER-adjacent drive-READ call site in `service.rs` (`be942eb7`, #234 -- restores
  the rig's own power level after a Tune: "adopt the operator's level, don't overwrite it"),
  calling `rig.read_level("RFPOWER")` from a site `tempo-audio-service.patch`'s old Layer-2
  enumeration ("three call sites") predates. It needs no new gate: Jimmy's Layer-1 chokepoint in
  `rig.rs` (`rfpower_suppressed`) blocks `read_level("RFPOWER")` by exact token match for
  **every** caller, present or future, so this site is already fully covered -- the
  `tempo-audio-service.patch` section below now says "four" call sites, not "three".
- A new keyed-write-safety pattern (`operator_keyed()`, `99e59211`) that withholds (not drops)
  XIT/VFO writes from a keyed rig -- conceptually adjacent to Jimmy's own `tuning_keyed`/
  `transmitting` RFPOWER gates but not evaluated for adoption in this upgrade.
- A new rig-model-driven ATU tune-capability gate (`hamlib_atu_start_tune_reaches`, #322) that
  distrusts a Hamlib backend's own `RPRT 0` ack for starting a tune on rigs known to lie about
  it -- a similar "don't trust the rig's ack" pattern to Jimmy's own RFPOWER distrust, not
  evaluated for adoption here.
- `tempo-core` gained `rusqlite` (bundled SQLite, via `cc`) for the logbook, and `tempo-audio`
  gained `opus` (via `opusic-sys`, via CMake) for Nexus Remote's receive-audio encoder -- both
  unconditional new dependencies (not feature-gated) pulled in by crates EngineHost already
  depends on. Built clean here with the MSYS2 UCRT64 toolchain (`gcc`/`cmake`/`dlltool`) this
  integration already requires for `tempo-fast-sys`'s native Fortran build -- no new
  build-environment requirement, just more volume through the same mechanism.

**Possible future opportunities surveyed, not adopted (out of scope for this upgrade):** the
Remote-control revocation system, the keyed-write-withholding pattern, and the ATU
capability-gating pattern above. None replace a current Jimmy-owned compatibility patch outright
(checked one-for-one against each patch's own behavior); each is a general pattern that *might*
simplify a future Jimmy feature. Not implemented here -- reported for a separate, deliberate
decision rather than folded into this upgrade.

**Patch impact:** all 8 patches re-anchored with context/offset changes only -- confirmed by
diffing the regenerated patch files against their pre-upgrade versions: every `+`/`-` payload
line is byte-identical (only surrounding `@@` line numbers and context lines moved), except two
`tempo-audio-service.patch` hunks whose immediate context itself gained new upstream lines (the
`RadioLoop`-construction hunk, now immediately after a new `clock_jump` field; and the
fast/freq-poll knob-QSY gate, now after a new `remote_read`/`remote_observe_*` block) -- both
re-derived by hand at the new anchor, same conceptual change, verified by the same
byte-identical `+`/`-` payload check. `tempo-app-snapshot.patch`'s two fixture files were
regenerated from scratch against the patched v1.14.0 tree (`cargo test -p tempo-app --test
station_identity regenerate_station_fixture -- --ignored` and the `watch_identity` sibling,
`regenerate_golden_fixtures`) rather than hand-edited, so they also carry upstream's own new
`scopeError`/`scopeSpanRefused`/`scopeModeCode`/`scopeFixStartMhz` fields alongside Jimmy's
`fakeItRestoreWarning`/`fakeItRestoreWarningId` pair. No patch was deleted; no ninth patch was
created.

**Two genuinely NEW hunks were needed, found only by building** (a clean `patch --dry-run`
apply is a syntax check, not a semantic one -- see "Checking a patch against a newer Nexus"
below): upstream added a `crates/tempo-audio/src/rig/remote/release.rs` (a new Remote-radio
PTT-release path, `remote_unkey_idle`) that calls `rig::ptt_line(false)` with the OLD one-`bool`
signature `tempo-audio-rig.patch` changes to two -- fixed by passing `self.ptt_data_source`,
now a second `diff --git` section inside `tempo-audio-rig.patch`. And `slot_tx_phase` gained a
third `SlotAction { .. }` construction site (a new early-return, "preserve split cleanup even
when permission disappears during CAT preparation") that the original patch's two hunks did not
cover because it did not exist at `v1.10.3` -- it still used the pre-patch field name
`fake_it_restore: split.fake_it_restore`, which no longer compiles against
`tempo-audio-slot.patch`'s renamed `fake_it_shift: Option<FakeItShift>`; fixed the same way as
the other two `SlotAction` sites, now a new hunk in `tempo-audio-slot.patch`. Both are pure
mechanical field-rename/signature-argument fixes -- no new behavior, no change to what either
patch does -- confirmed by `cargo build` (clean) and the full `cargo test` run recorded below.

### Superseded: audited upgrade 93b9f012 -> v1.10.3 (7618390)

Codex-audited upgrade (2026-09-07 audit, executed 2026-09-08) from the prior pin `93b9f012`
(an untagged `v1.10.2`-era commit on `main`) to the tagged stable `v1.10.3` -- **2 commits**:
`ed9e6130` (the substantive "1.10.3 batch") and `76183906` (the version-bump commit, which
touches no crate source). Chosen over the moving `main` (~175 commits ahead: JS8, Winlink,
contest widening, FT-710 scope UI, a tempo-audio process refactor, and a "main was red"
Windows repair) because `ed9e6130` carries the only two material pre-stable deltas and they
are both **ordinary-FT8 transmit-safety fixes**:

- **Stuck-TX on a keyed rig across a channel rebuild** (`ed9e6130`, `service.rs`): a suspect/
  transport rebuild (not just `daemon_died`) now carries the pre-teardown `rig.keyed` belief
  onto the fresh rig and issues an **unconditional** `rig.ptt(false)` through the reopened
  channel. Adopted by the base; no patch. Verified by hand in the staged tree (`grep -n
  "let was_keyed = rig.keyed" service.rs` -> present, not touched by any Jimmy hunk).
- **Ordinary QSO sends 73 off a bystander Fox multiplex** (`ed9e6130`, `engine.rs`, #236): the
  Fox-multiplex `reattach` closure is now gated on `hound_active = matches!(special_op,
  Hound | SuperHound)`. Jimmy Next never sets `special_op` to Hound, so this fabrication path
  is permanently inert for Jimmy. Adopted by the base; no patch.

Also in the base by pin alone (no patch): the #126 FTDX-101D mid-over RFPOWER foldback fix,
which refactored the per-mode power-ceiling `L RFPOWER` write site into a
`should_command_rf_power(tuning_keyed, transmitting, changed_or_forced, giveup_blocked)`
helper; and `logged_tick` / an opt-in beta update channel (neither consumed by EngineHost's
Direct host -- both additive `#[serde(default)]` snapshot/settings fields, verified to
round-trip).

**Patch impact:** 7 of 8 patches re-anchored with offsets only. `tempo-audio-service.patch`
needed a **one-hunk re-anchor** (hunk #17, the RFPOWER Layer-2 gate): its insertion moved
from `if !self.tuning_keyed && (force || ...) && self.rf_power_giveup != Some(p)` to
`if !self.disable_rfpower_probe && should_command_rf_power(...)` -- same conceptual change
(the loop never forms the command when the probe is disabled), now in front of the #126
helper call. The patch file's context/line numbers were regenerated wholesale from the
fully-patched `service.rs`; the added/removed *code* is byte-identical to the prior version
except that one hunk (verified by md5 of the `+`/`-` lines). No patch was deleted; no ninth
patch was created.

#### Superseded: audited upgrade 44bca866 -> 93b9f012

Codex-audited upgrade (2026-09-05) from the prior pin `44bca866` to `93b9f012` (the 40 commits
in between: World Radio League logbook/eQSL integration, a WSJT-X-forward multi-target fix, the
manual's EPUB/PDF pipeline, and CI/funding chores -- **plus** the two behaviors below this
integration deliberately adopted). No patch changed its *behavior* in this pass, only its
context (upstream inserted unrelated code near several patch anchors; see each patch's own
history for the mechanical re-anchoring). Two upstream improvements are adopted by moving the
pin alone -- neither needed a patch:

- **CAT reopen/recovery backoff** (`b31be910`, `636ad7e3`): a failed reopen with the serial port
  present now retries on a doubling backoff (`CAT_REOPEN_RETRY_MS` -> `CAT_REOPEN_MAX_MS`,
  reset by the port re-appearing) instead of the old single attempt that could leave a radio
  switched off overnight never reconnecting on its own.
- **Bounded Tune-meter polling** (`ea37eb1a`): SWR/ALC/RFPOWER_METER_WATTS/COMP_METER are read
  during a Tune carrier again, gated by `TUNE_METER_MIN_LEAD_MS` (skip the read if the carrier's
  remaining lead is too thin) and bounded by `TUNE_METER_DEADLINE_MS` (a slow rig costs a
  reading, never a gap in the carrier) -- replacing the old full stand-down
  (`meters_now = keyed_now && !self.tuning_keyed`) that went in as a starvation fix and, per
  upstream's own note, wrongly assumed nobody watches SWR during a tune-up.

Deliberately **not** adopted: the Nexus CAT broker (`cat_broker`/`broker_self_port`, still
forced off/`None` in EngineHost's `main.rs` -- see the comments there) and upstream's new
voice-memory transmission (`\send_voice_mem`/`\stop_voice_mem`, `civ/broker.rs`) -- both are
CAT-broker-surface features Jimmy has no use for and does not enable.

RFPOWER write protection (the two-layer chokepoint in `tempo-audio-rig.patch` +
`tempo-audio-service.patch`) was re-verified by hand against the new Tune-meter and CAT-reopen
code: both remaining `L RFPOWER` write call sites (per-mode power ceiling, Tune-power) and the
heavy-poll `l RFPOWER` read are still gated by `disable_rfpower_probe`, and every live/test `Rig`
construction still funnels through the single `finish_cat_open` stamp site -- the new CAT-reopen
backoff only changes *when* that funnel runs, not whether it runs. See
`jimmy_compat_rfpower_write_protection_survives_reopen_and_new_rigs_while_meters_flow` in
`service.rs`'s test module for the regression proof.

## Current patches (against Nexus `v1.16.0`, commit `21ac4c13`)

Ten patches, **one source file each** (`prepare-nexus.ps1` and the `patches/` directory are
the source of truth; the ten are itemised in the `###` sections below). Jimmy's downstream
behavior these preserve is the **Jimmy Next 2.0.55 operator experience** -- that is the
compatibility baseline.

### `patches/tempo-app-engine.patch` -- 8 behaviors, `crates/tempo-app/src/engine.rs`

| Behavior | Why Jimmy needs it | What's missing / wrong upstream |
|---|---|---|
| Same-slot decode accumulation | A QSO reply caught only on a slot's EARLY decode pass drives Nexus's own sequencer's next over, but a plain overwrite of `last_wire_decodes`/`last_decodes` on the boundary pass drops it from `AppSnapshot.recent_decodes` -- silently breaking Jimmy's **auto-logging** for a QSO that otherwise completed. The fix accumulates when `last_decode_slot == Some(slot)` (early-pass dupes are already removed by `drop_early_dupes`), and still replaces on a genuinely newer slot. | `process_decodes` ends with an unconditional `self.last_wire_decodes = ...; self.last_decodes = ...`. |
| `Engine::set_pskreporter(bool)` | Jimmy's Options UI has a live PSK Reporter on/off toggle that must not require a restart. | No live setter; only a start-of-session `Settings.pskreporter` field. |
| `Engine::last_own_tx_text()` | The **only** consumer is the WSJT-X-protocol `Status.tx_message` field (see the service.rs patch) -- the one signal a UDP logger (GridTracker/JTAlert) needs to track which callsign / exchange step is on the air. Jimmy's own Direct snapshot path derives own-TX from the snapshot's own rows and does **not** use this accessor. | No accessor for the most recent transmitted text. |
| `dont_set_mode` guard in `rig_mode_effective()` | WSJT-X Radio tab "Mode: None" -- when set, the radio loop must never command the rig's mode. `rig_mode_effective()` returns `String::new()` first thing, which every mode-set call site already treats as "nothing to do". | No native "leave the mode alone" gate. |
| Ordinary-QSO multi-answer roger (`hound_split`, added 2026-09-26) | A station answering several callers at once (MSHV multi-answer "Special MSG", WSJT-X Fox) sends our roger as the sender-less first half of a 0.1 frame, `<us> RR73; <other> <DX> -08`. Outside a Hound QSO Nexus never reattached a sender, so an ordinary QSO with such a station could NEVER complete (live 2026-09-25, K5MGY). The patch reattaches in an ordinary QSO **only** when the frame's own hashed sender (its second half) is our partner -- the frame names its sender, so this is evidence, not assumption; a bystander Fox's frame names another station and #236 stays closed. Matches WSJT-X normal mode's "dual Fox style message, possibly from MSHV" completion. Test: `an_ordinary_qso_completes_on_its_own_partners_multi_answer_roger` beside Nexus's own #236 guard. | Reattach is gated on `hound_qso` alone. |
| Multi-answer split provenance (`hound_split` + `Engine::last_decodes_multiplexed()`, added 2026-09-26) | During a QSO Nexus splits every 0.1 frame into two ordinary rows, so the fact that they came from ONE multi-answer transmission was lost -- Jimmy needs it (a station answering several callers is not "busy working someone else"). The patch records it AT THE SPLIT, from the original combined text (Nexus's own `fox_multiplex` shape gate), as one flag per `last_decodes` row kept in lockstep at every write site; EngineHost reads it under the snapshot's lock and sets the envelope's `multiAnswer`. Never reconstructed from snr/dt/frequency; the accessor returns empty if ever out of step (under-marks, never mis-marks). Test: extended `an_ordinary_qso_completes_on_its_own_partners_multi_answer_roger` (a look-alike row with identical measurements and split free text stay unmarked). | `hound_split` returns only the rewritten decodes; no provenance survives. |
| `Engine::set_keep_logged_location(bool)` (added 2026-10-04) | The switch for `tempo-app-station.patch`; EngineHost turns it on when it adopts the log. | -- |
| `Engine::set_fake_it_restore_status` + `RadioStatus.fake_it_restore_warning` / `fake_it_restore_warning_id` snapshot emit | Codex correction G (+ Audit #10): an UNRESOLVED Fake-It dial restore must reach the operator. The radio loop sets a concise string + a monotonic per-episode id while unresolved and clears both (`None`) the moment it reconciles; Jimmy dedups per `(id + session token)` so the same episode announces once even across a Direct reconnect. `None` in normal operation. | No engine-side path to surface Fake-It restore state. |

**Obsoleted when:** upstream accumulates same-slot decodes across the early + boundary pass;
adds a live PSK-Reporter setter; adds a `dont_set_mode`-equivalent `Settings` gate; and its own
Fake-It restore verifies + exposes an unresolved state.
**How to check:** `grep -n "last_decode_slot ==" crates/tempo-app/src/engine.rs` (is the
overwrite now slot-conditioned?); `grep -n "fn set_pskreporter\|fn last_own_tx_text"`;
`grep -n "dont_set_mode" crates/tempo-app/src/settings.rs`; `grep -n "frame_from_partner\|fn hound_split" -A30
crates/tempo-app/src/engine.rs` (does upstream reattach an ordinary QSO's roger from the frame's
own sender?); `grep -n "fn last_decodes_multiplexed\|fn hound_split"` (does upstream keep split
provenance?).

### `patches/tempo-app-settings.patch` -- 1 field (`dont_set_mode`; the other two retired 2026-10-04), `crates/tempo-app/src/settings.rs`

Adds `Settings.ptt_data_source`, `Settings.dont_set_mode`, and `Settings.disable_rfpower_probe`,
all `#[serde(default)]` (off). `ptt_data_source`/`dont_set_mode` mirror WSJT-X's Radio tab
"Transmit Audio Source: Data" and "Mode: None". `disable_rfpower_probe` lives on `Settings` (not
only the startup `RadioConfig`) so the per-tick `Transport::from_settings` rebuild keeps it and
`finish_cat_open` keeps re-stamping the Rig-level RFPOWER chokepoint after any CAT reopen.

**Why Jimmy needs it:** Jimmy's Options > Radio tab exposes the first two; EngineHost sets all
three at launch (`--ptt-data-source`, `--dont-set-mode`, and `settings.disable_rfpower_probe = true`
unconditionally).
**Obsoleted when:** upstream `Settings` gains fields with equivalent meaning.
**How to check:** `grep -n "ptt_data_source\|dont_set_mode\|disable_rfpower_probe" crates/tempo-app/src/settings.rs`.

### `patches/tempo-app-snapshot.patch` -- Fake-It restore visible to Jimmy, `crates/tempo-app/src/dto.rs` + `src/lib.rs` + `tests/fixtures/*_snapshot.json`

Codex correction G (+ Audit #10). Adds `RadioStatus.fake_it_restore_warning: Option<String>`
**and `fake_it_restore_warning_id: Option<u64>`** (both `#[serde(default)]`), their `None` init
in the `AppSnapshot` literal (`lib.rs`), and re-baselines the two golden snapshot fixtures
(`station_identity` / `watch_identity`), which now carry `"fakeItRestoreWarning": null` +
`"fakeItRestoreWarningId": null`. The engine sets both via `Engine::set_fake_it_restore_status`
(see `tempo-app-engine.patch`). The `_id` is a monotonic per-EPISODE counter (bumped once on
each fresh transition into `Unresolved` in the radio loop) -- Jimmy's `DirectApplyStatus`
dedups the warning on `(episode id + snapshot session token)` so the SAME still-unresolved
episode is announced only once even across a Direct transport reconnect, while a genuinely new
episode (identical wording, or a new EngineHost's new token) announces again.

**Obsoleted when:** the engine-side accessor is obsoleted, or upstream exposes an equivalent
Fake-It restore state on `RadioStatus`.
**How to check:** `grep -n "fake_it_restore_warning" crates/tempo-app/src/dto.rs`. The fixture
re-baseline is regenerated with `cargo test -p tempo-app --test station_identity
regenerate_station_fixture -- --ignored` and the `watch_identity` sibling -- and is *only* the
one new `null` field; anything else in the diff is a real change to find.

### `patches/tempo-app-station.patch` -- a contact keeps its logged location, `crates/tempo-app/src/station.rs` (added 2026-10-04)

Adds `StationCore.keep_logged_location` (default `false`, upstream behaviour). Jimmy turns it
on, and then: a bulk append (`commit_bulk_as` -- an ADIF import, a contact a confirmation merge
adds, rows taken in from log.adi) is not filled by `fill_with(country, state)`; a row update
(`edit_op` -- upload stamps, `LOG_SET_EXTRA`) does not fill a blank country; and the form edit
(`edit_ops`) keeps the stored country unless the edit corrects the callsign, when it is resolved
again as upstream does. So imported STATE and COUNTRY stay exactly as the file had them, blanks
included -- a resolver's guess (a mailing address, a grid's main state) is not where the station
was operating. Found by `--nexus-upload-tests`: without the `edit_op` gate, the first upload
stamp on an imported contact filled its blank country. The live insert (`log_qso`) keeps its
fill. The load-time fill (`fill_loaded`) runs only when the database cannot be opened and the
log falls back to log.adi; it is not gated. Worked/confirmed DXCC is unaffected: the hot index keys an
entity by `dxcc_resolve(call)`, not the stored COUNTRY. A matched merge (`reconcile::apply_match`)
also fills a blank state/country; Jimmy avoids that without a patch by removing STATE/COUNTRY
from paired download rows (`NexusReportPairing`).

**Obsoleted when:** upstream stops filling bulk appends, or offers an equivalent switch.
**How to check:** `grep -n "keep_logged_location\|fill_with(&mut row\|fn edit_op" crates/tempo-app/src/station.rs`.

### RETIRED 2026-10-04: `patches/tempo-audio-rig.patch` -- DATA/ACC PTT + RFPOWER chokepoint (see the v1.16.0 re-assessment above; kept here as history)

Two concerns, both "how this `Rig` talks to the radio":

1. **DATA/ACC PTT.** `rig::ptt_line(on: bool)` -> `ptt_line(on: bool, data_source: bool)`,
   emitting Hamlib `RIG_PTT_ON_DATA` (`T 3`) instead of plain `RIG_PTT_ON` (`T 1`) when the
   operator has selected the rig's rear DATA/ACC port for transmit audio. `unkey` is `T 0`
   either way. Plus a `Rig::set_ptt_data_source` / `ptt_data_source` field. Changing this
   function's signature means EVERY caller needs the new argument, not just the ones existing
   when a hunk was last written -- since v1.11 that includes a second file,
   `crates/tempo-audio/src/rig/remote/release.rs`'s `remote_unkey_idle` (the Remote-radio PTT
   release path, new since v1.10.3), patched alongside `rig.rs` in the same patch file as a
   second `diff --git` section. `cargo build` is the only reliable way to catch a new caller
   like this -- a clean `patch --dry-run` only proves the patch's OWN hunks still apply, not
   that every consumer of the changed signature was found.
2. **RFPOWER never-touch chokepoint (Layer 1).** A `Rig::rfpower_suppressed` field +
   `Rig::set_rfpower_suppressed`. When set: `read_level("RFPOWER")` returns `Err` with **no
   bytes on the wire** (EXACT token match -- `RFPOWER_METER_WATTS` and every `read_meter_f32`
   telemetry read are a different Hamlib level and stay allowed), and `set_power(..)` returns
   `Ok` having sent **nothing**. This covers every present and future caller that reaches these
   two chokepoints. Layer 2 is the call-site gating in the service.rs patch.

**Why Jimmy needs it:** DATA-port wiring is a real-station case, not cosmetic. RFPOWER
suppression: an `l RFPOWER` on a freshly-spawned `rigctld` can trip a destructive
calibration-sweep bug in Hamlib's Kenwood backend (Hamlib/Hamlib#1595) on first touch, dropping
the operator's actual transmit power; and Jimmy's own policy is that a read must never be able
to change anything on the radio, and the engine never adjusts the operator's drive. **No
operator override.**
**Obsoleted when:** `rig::ptt_line` gains a mic/data distinction; AND upstream gives a real way
to forbid RFPOWER drive read/write per rig (or Hamlib #1595 is fixed in the bundled backend).
**How to check:** `grep -n "fn ptt_line\|fn read_level\|fn set_power\|rfpower_suppressed" crates/tempo-audio/src/rig.rs`
-- and separately `grep -rn "ptt_line(" crates/tempo-audio/src` to confirm every caller (not just
`rig.rs`'s own PTT command site) passes both arguments; a caller upstream added since the last
check is a real compile error, not a false alarm.

### `patches/tempo-audio-service.patch` -- radio-loop wiring, `crates/tempo-audio/src/service.rs`

Since 2026-10-04 (v1.16.0) only the Fake-It restore, `Status.tx_message` and the "Mode: None" test remain; the `ptt_data_source` / `disable_rfpower_probe` threading and RFPOWER gates described below were retired with `tempo-audio-rig.patch`.

One file, one concern (the radio loop / CAT service). Carries:

- **`ptt_data_source` + `disable_rfpower_probe` threading.** New fields on `RadioConfig` and
  the private `Transport` struct; carried through `Transport::from_cfg` / `from_settings` /
  `from_profile`; **stamped onto the `Rig` in `finish_cat_open`** -- the single shared tail of
  every CAT open/reopen/recovery (`open_monitor` stamps the RFPOWER flag on its own read-only
  rig too). `from_profile` (monitor radios) forces both safe (`ptt_data_source: false`,
  `disable_rfpower_probe: true`).
- **RFPOWER never-touch (Layer 2).** A `RadioLoop.disable_rfpower_probe` lifetime mirror gates
  the loop's own three RFPOWER **drive** call sites so the loop never even forms the command:
  the heavy-poll `l RFPOWER` read, the per-mode power-ceiling `set_power`, and the Tune-power
  `set_power` (the last two are `L RFPOWER` **writes** that did not exist in Jimmy's old v1.6.0
  Nexus baseline). Safe meters (`RFPOWER_METER_WATTS`/`SWR`/`ALC`/`COMP_METER`) are untouched.
  On v1.10.3 the per-mode power-ceiling site is upstream's #126 `should_command_rf_power(...)`
  helper (FTDX-101D mid-over foldback fix); Jimmy's Layer-2 gate sits in **front** of it as
  `if !self.disable_rfpower_probe && should_command_rf_power(...)`. The two are orthogonal --
  #126 says "not mid-over", Jimmy says "not at all when suppressed". Since v1.11 upstream added
  a **fourth** RFPOWER-adjacent site, a drive-READ in the Tune-power restore path (`be942eb7`,
  #234, "adopt the rig's own power level after a tune instead of overwriting it") -- this one
  has NO Layer-2 gate of its own, but needs none: it calls the same `rig.read_level("RFPOWER")`
  Layer 1 blocks by exact token match for every caller, so it is already fully suppressed when
  Jimmy sets `disable_rfpower_probe`. Only three sites are Layer-2-gated writes/reads that
  needed their own `if !self.disable_rfpower_probe`; the fourth relies on Layer 1 alone -- see
  "How to check" below, which now lists all four.
- **`Status.tx_message`** changes from a hardcoded `""` to `eng.last_own_tx_text()`.
- **Corrected Fake-It dial restore** (rewritten in the Codex correction pass; the state model
  now lives in the `FakeItRestore` enum -- `None` / `Armed` / `Unresolved`):
  - **Physical capture (A).** `slot::apply_tx_dial_shift`'s `FakeIt` arm does a FRESH
    `rig.read_freq()` immediately before the shift and uses *that* as the restore target --
    never the cached engine dial. If a trustworthy read cannot be had it **FAILS CLOSED**: no
    shift, no `FakeItShift`, the over transmits un-shifted.
  - **Non-binary shift outcome (B).** The shift's `set_freq` result is carried as
    `shift_confirmed`; an `Err` (transport lost) does **not** mean "no restore needed" -- the
    `Armed` state is still armed and reconciled on the physical readback.
  - **Unkey first (C).** The teardown runs only under `tx_until_ms.is_none() &&
    !manual_ptt_applied && !tuning_keyed` -- unchanged.
  - **Exactly one immediate attempt (D).** The `Armed` teardown makes ONE attempt (read →
    reconcile → maybe one `set_freq` → verify) and transitions to `None` or `Unresolved`. It
    never stays `Armed`, so there is no idle-tick retry loop. `Unresolved` is **not touched**
    on idle ticks -- only when the heavy poll has a trustworthy physical reading in hand
    (`reconcile_fake_it_on_reading`), and the ONE corrective `set_freq` there is unlocked only
    on the first reading after a CAT-health recovery.
  - **Reconciliation (E).** `classify_fake_it(cur, original, shifted_to)` -- EXACT integer-Hz:
    `cur == original` → already home, clear; `cur == shifted_to` → still shifted, restore;
    anything **else** → a newer operator/external dial, **CANCEL** (never overwritten). Plus
    the knob-QSY readers are gated while a restore is owed, and a commanded retune / reopen /
    a fresh Fake-It cycle each clear or replace it.
  - **Verification tolerance (F).** EXACT `==` -- rigctld frequencies are whole Hz and Nexus
    carries no per-rig tuning-resolution to justify a tolerance. An accepted set that will not
    read back is "sent, NOT confirmed" -- **never** "verified".
  - **Visible to Jimmy (G + Audit #10).** `Unresolved` sets `RadioStatus.fake_it_restore_warning`
    + `fake_it_restore_warning_id` (a per-episode counter, bumped on the entry edge into
    `Unresolved`; see `tempo-app-snapshot.patch`). Jimmy announces once per `(episode id +
    session token)` -- surviving a Direct reconnect. A healthy immediate restore is silent.

**Not carried forward (deliberately -- these were experimental, never in 2.0.55):** the
`5 x 1000 ms` Fake-It retry loop, and the `600 ms` synchronous Tune-meter poll. Upstream's
`meters_now = keyed_now && !self.tuning_keyed` Tune-meter suppression is **kept as-is** (not
patched); live meters are intentionally unavailable during a chunk-fed Tune carrier.

**Obsoleted when:** the engine/settings/rig patch items are obsoleted (the threading follows
them); AND upstream's Fake-It teardown itself verifies the restore and does not consume state
on failure.
**How to check:** `grep -n "disable_rfpower_probe\|fake_it_restore\|last_own_tx_text\|ptt_data_source\|should_command_rf_power" crates/tempo-audio/src/service.rs`
-- and re-read the Fake-It teardown block and every `read_level("RFPOWER")` / `set_power` /
`should_command_rf_power` call site by hand (grep -- do not assume the counts are unchanged).
On v1.14.0 there are three `disable_rfpower_probe` drive gates in the loop body: the heavy-poll
`l RFPOWER` read (`if !self.disable_rfpower_probe` before `rig.read_level("RFPOWER")`), the
per-mode ceiling (`if !self.disable_rfpower_probe && should_command_rf_power(...)`), and the
Tune-power write (`tune_power.filter(|_| !self.disable_rfpower_probe)`), plus the two stamp
sites (`finish_cat_open`, `open_monitor`). Since v1.11 (`be942eb7`, #234) there is also a
**fourth**, ungated `rig.read_level("RFPOWER")` in the Tune-power-restore path -- it needs no
Layer-2 gate of its own because Layer 1 (`rig.rs`'s `rfpower_suppressed`) already blocks that
exact call for every caller; confirm this fourth site is still present and still uncounted by
`disable_rfpower_probe` (if upstream ever adds its own gate there, that is fine -- redundant,
not a regression) by grepping `read_level("RFPOWER")` across the whole file and hand-checking
each hit's own gating.

### `patches/tempo-audio-slot.patch` -- Fake-It physical capture + Rig-split do-not-set-mode

`crates/tempo-audio/src/slot.rs`, `apply_tx_dial_shift` (+ every `SlotAction` builder that
consumes its result -- as of v1.14.0 there are three inside `slot_tx_phase`: the successful-key
branch, the receive branch, and a permission-lost-during-CAT-prep early return added since
v1.10.3; all three must construct `fake_it_shift`, not the old `fake_it_restore`, or the crate
does not compile -- `cargo build` catches a fourth one appearing, a plain patch dry-run does
not):

- **`SplitMode::FakeIt`** now returns a `FakeItShift { original_hz, shifted_to_hz,
  shift_confirmed }` (was a bare `Option<u64>`). `original_hz` is a FRESH `rig.read_freq()`
  taken immediately before the shift -- fail closed (no shift, `None`) if it errors.
  `shift_confirmed` records whether the shift's own `set_freq` returned `Ok` (ambiguous ≠ "no
  restore needed"). This is the pre-shift-authority half of the corrected Fake-It design (see
  the service.rs patch).
- **`SplitMode::Rig`** now gates the TX-VFO mode set (`rig.set_split_mode`) on
  `!eng.settings().dont_set_mode` -- "Do not set mode" must stop the Rig-split mode command
  too, not just the RX-VFO `M`. Only the mode set is gated; `set_split` + `set_split_freq`
  (the frequency split) still run. (`rig_mode_effective()` already returns `""` under
  `dont_set_mode` and `set_split_mode` no-ops on `""`, but the explicit gate stops the path
  regressing on its own.)

**Obsoleted when:** upstream captures a fresh physical dial for Fake-It and honours a
do-not-set-mode option on the Rig-split path.
**How to check:** `grep -n "read_freq\|FakeItShift\|dont_set_mode" crates/tempo-audio/src/slot.rs`
-- and separately `grep -n "fake_it_restore\|fake_it_shift" crates/tempo-audio/src/slot.rs` to
confirm every `SlotAction`/`SplitApply` literal uses `fake_it_shift`; any surviving
`fake_it_restore` is a caller the patch's own hunks don't yet cover (a compile error, not a
cosmetic issue).

### `patches/tempo-audio-rigctld-test-portability.patch` -- test-only Windows fixes

`crates/tempo-audio/src/rigctld_proc.rs`, **`#[cfg(test)]` module only -- no production code**:

- `run_bounded_returns_the_output_of_a_command_that_finishes` spawned bare `echo`, a cmd.exe
  builtin -> on Windows, `cmd /C echo hello`.
- `rigctl_is_found_beside_rigctld` hard-coded `/usr/bin/rigctl` on the EXPECTED side, making it
  a test of Windows' path-separator rules -> build both input and expected with the same
  `Path::join`.

**Obsoleted when:** upstream makes these two tests platform-neutral.
**How to check:** `grep -n "\"echo\"\|/usr/bin/rigctl" crates/tempo-audio/src/rigctld_proc.rs`.

### `patches/tempo-fast-sys-build.patch` -- Windows `\\?\`-path / gfortran build fix

`crates/tempo-fast-sys/build.rs`: strips the `\\?\` extended-length-path prefix that
`Path::canonicalize()` produces on Windows before handing the path to CMake, whose generated
Ninja/Makefile rules get mis-split by MSYS2/MinGW gfortran's own path handling. Without this, a
full rebuild from a **fresh** build directory fails every Fortran compile step in
`tempo-fast-sys`'s native `libtempo` build. Byte-identical to the v1.6.0 version of this patch
(`build.rs` is unchanged upstream). Build-environment only -- no runtime behavior.

**Obsoleted when:** upstream's own `build.rs` strips/avoids the `\\?\` prefix.
**How to check:** `grep -n "canonicalize\|strip_verbatim" crates/tempo-fast-sys/build.rs`, and
try a build after `cargo clean` (the bug does not reproduce on an incremental build).

### `patches/tempo-core-message.patch` -- grid-less standard CQ/reply (protocol fix)

`crates/tempo-core/src/message.rs` (added 2026-09-26): `Msg::parse` treated `CQ W1AW` and
`W9XYZ K2DEF` -- two plain standard calls with no third word -- as free text, on the stated
assumption that "a standard sender would have carried its grid". That is wrong for FT4/FT8: the
QEX paper's Table 1 defines the standard message's g15 field as "4-character grid, Report, RRR,
RR73, 73, or blank", and WSJT-X's `pack77_1` packs any two-word message whose words pass
`chkcall` as Type 1 with a blank g15 (`if(nwords.eq.2) ... igrid4=MAXGRID4+1`) before free text is
ever tried; `unpack77` renders it as just the two calls. Live effect before the fix: `CQ N4NF`
(32 times in one session) could not be worked, and a station answering us with `<mycall> <call>`
was rejected. The patch adds one parse branch (both words must pass a `chkcall` mirror AND
`is_std_call`; slashed calls stay with the existing i3=4 branches) returning `Msg::Cq`/`Msg::Grid`
with an EMPTY grid -- the sequencer's existing empty-grid handling (built for i3=4) then does the
right thing and keeps the grid unknown. Updates the four upstream assertions that encoded the old
rule and adds free-text negatives from the live log plus one sequencer test. EngineHost's
`decode_semantics.rs` needs no change (it calls `Msg::parse`).

**Upstream #303 is now in the base (v1.15.0).** From 2026-09-26 to 2026-09-28 this patch also
carried a backport of kd9taw/Nexus `0c68f705` + `df2c9d75` (both callsign fields must be
callsigns, `is_call_field`). v1.15.0 contains it, so that part was removed at the upgrade; the
no-grid branch now sits directly before upstream's own `is_call_field`-gated three-token arm.

**Obsoleted when:** upstream `Msg::parse("CQ W1AW")` returns a `Msg::Cq` (and `"W9XYZ K2DEF"` a
`Msg::Grid`).
**How to check:** `grep -n 'parse("CQ W1AW")' crates/tempo-core/src/message.rs` -- upstream
asserting `Msg::Other` there means the bug is still present.

**Companion:** the no-grid rule also matches a Nexus Tempo chat chunk whose header happens to read
as a callsign (`A13DE W9XYZ` = header `A13` + payload `DE W9XYZ`) -- see `tempo-core-inbox.patch`
below, which keeps Tempo chat reassembly working (operator-approved 2026-09-26).

### `patches/tempo-core-inbox.patch` -- Tempo chat chunk vs grid-less call, `crates/tempo-core/src/inbox.rs`

Added 2026-09-26 as the companion to `tempo-core-message.patch`. The inbox asks "standard message?"
before "chat chunk?", and a chunk whose 3-character header happens to spell a call (`A13DE W9XYZ`)
now parses as a grid-less two-call message, so the chunk was dropped and the chat message never
reassembled (`inbox::tests::chunked_broadcast_reassembles_and_routes` failed; it passes on pristine
v1.14.0). Text alone cannot separate the two -- real Letter-Digit-Digit DX calls (T88xx, H44xx,
P29xx) have the same shape; the decoder's i3 could, but Nexus discards it. So, for CHAT ONLY, a
grid-less two-word frame that also carries a valid chunk header (`text::parse_chunk`) is treated as
content. `Msg::parse` and every FT8 consumer (sequencer, decode rows, EngineHost semantics, Jimmy)
are unchanged. Test: that inbox test, extended with a control that a real grid-less call (no chunk
header) stays a standard frame.

**Obsoleted when:** `tempo-core-message.patch` is (upstream parses grid-less standard pairs itself
and resolves the chunk ambiguity, e.g. by carrying i3).
**How to check:** `grep -n "gridless_pair_chunk" crates/tempo-core/src/inbox.rs`.

## Checking a patch against a newer Nexus

Whenever Jimmy deliberately updates to a newer official Nexus revision:

1. Update `pin.txt` to the new tag/commit.
2. For each patch file, check whether the "How to check" step above shows the behavior now
   exists upstream. If it does, delete that patch file (and update this README) instead of
   trying to reapply it.
3. For patches still needed, run `scripts/prepare-nexus.ps1` -- it applies each remaining patch
   with `patch --dry-run` first and fails loudly, naming the patch, if upstream has drifted
   enough that a patch no longer applies cleanly. That failure means: re-derive the patch by
   hand against the new revision (locate the same anchor/function, reapply the same conceptual
   change), not force it through. **A clean apply is not proof the patch still does its job** --
   for the RFPOWER and Fake-It patches especially, re-read the real call sites by hand.
4. Re-run the EngineHost test suite (`cargo test`, from `EngineHost/`) before committing the
   revision bump -- including the `jimmy_compat_*` tests in `tempo-audio`'s `service.rs` /
   `rig.rs` test modules, which are the deterministic RFPOWER / Fake-It / DATA-PTT /
   do-not-set-mode proofs.

## Retired

- `patches/tempo-audio-telemetry.patch` (v1.6.0-era): its one behavior -- suppressing the
  heavy-poll `l RFPOWER` read -- is folded into `tempo-audio-rig.patch` (Layer 1 chokepoint) +
  `tempo-audio-service.patch` (Layer 2 call-site gates), which additionally cover the two
  `L RFPOWER` **write** paths that are new since v1.6.0.
- `vendor/tempo-fast-sys-patched/` (a full local copy of the crate wired in via Cargo
  `[patch]`) -- superseded by `patches/tempo-fast-sys-build.patch`.
