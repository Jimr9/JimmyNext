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

`pin.txt` points at the **newest official stable release tag, `v1.10.3`**, by its exact commit:

```
NEXUS_TAG=v1.10.3
NEXUS_COMMIT=7618390658f8f92431dec0ac65979b84f2c0fb76
```

`NEXUS_COMMIT` is the real lock; `NEXUS_TAG` is the honest human reference. `v1.10.3` is a
lightweight tag pointing straight at `7618390` (2026-09-04). `prepare-nexus.ps1` clones
`--branch v1.10.3 --single-branch` (a lightweight tag checks out in detached HEAD, which is
fine) and still verifies the resolved `HEAD` equals `NEXUS_COMMIT`. The `NEXUS_TAG=main`
branch-and-detach path in that script is retained for any future exact-commit-on-`main` pin
but is not used here.

## Audited upgrade, 93b9f012 -> v1.10.3 (7618390)

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

### Superseded: audited upgrade 44bca866 -> 93b9f012

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

## Current patches (against Nexus `v1.10.3`, commit `7618390`)

Eight patches, **one source file each** (`prepare-nexus.ps1` and the `patches/` directory are
the source of truth; the eight are itemised in the `###` sections below). Jimmy's downstream
behavior these preserve is the **Jimmy Next 2.0.55 operator experience** -- that is the
compatibility baseline.

### `patches/tempo-app-engine.patch` -- 5 behaviors, `crates/tempo-app/src/engine.rs`

| Behavior | Why Jimmy needs it | What's missing / wrong upstream |
|---|---|---|
| Same-slot decode accumulation | A QSO reply caught only on a slot's EARLY decode pass drives Nexus's own sequencer's next over, but a plain overwrite of `last_wire_decodes`/`last_decodes` on the boundary pass drops it from `AppSnapshot.recent_decodes` -- silently breaking Jimmy's **auto-logging** for a QSO that otherwise completed. The fix accumulates when `last_decode_slot == Some(slot)` (early-pass dupes are already removed by `drop_early_dupes`), and still replaces on a genuinely newer slot. | `process_decodes` ends with an unconditional `self.last_wire_decodes = ...; self.last_decodes = ...`. |
| `Engine::set_pskreporter(bool)` | Jimmy's Options UI has a live PSK Reporter on/off toggle that must not require a restart. | No live setter; only a start-of-session `Settings.pskreporter` field. |
| `Engine::last_own_tx_text()` | The **only** consumer is the WSJT-X-protocol `Status.tx_message` field (see the service.rs patch) -- the one signal a UDP logger (GridTracker/JTAlert) needs to track which callsign / exchange step is on the air. Jimmy's own Direct snapshot path derives own-TX from the snapshot's own rows and does **not** use this accessor. | No accessor for the most recent transmitted text. |
| `dont_set_mode` guard in `rig_mode_effective()` | WSJT-X Radio tab "Mode: None" -- when set, the radio loop must never command the rig's mode. `rig_mode_effective()` returns `String::new()` first thing, which every mode-set call site already treats as "nothing to do". | No native "leave the mode alone" gate. |
| `Engine::set_fake_it_restore_status` + `RadioStatus.fake_it_restore_warning` / `fake_it_restore_warning_id` snapshot emit | Codex correction G (+ Audit #10): an UNRESOLVED Fake-It dial restore must reach the operator. The radio loop sets a concise string + a monotonic per-episode id while unresolved and clears both (`None`) the moment it reconciles; Jimmy dedups per `(id + session token)` so the same episode announces once even across a Direct reconnect. `None` in normal operation. | No engine-side path to surface Fake-It restore state. |

**Obsoleted when:** upstream accumulates same-slot decodes across the early + boundary pass;
adds a live PSK-Reporter setter; adds a `dont_set_mode`-equivalent `Settings` gate; and its own
Fake-It restore verifies + exposes an unresolved state.
**How to check:** `grep -n "last_decode_slot ==" crates/tempo-app/src/engine.rs` (is the
overwrite now slot-conditioned?); `grep -n "fn set_pskreporter\|fn last_own_tx_text"`;
`grep -n "dont_set_mode" crates/tempo-app/src/settings.rs`.

### `patches/tempo-app-settings.patch` -- 3 fields, `crates/tempo-app/src/settings.rs`

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

### `patches/tempo-audio-rig.patch` -- DATA/ACC PTT + RFPOWER chokepoint, `crates/tempo-audio/src/rig.rs`

Two concerns, both "how this `Rig` talks to the radio":

1. **DATA/ACC PTT.** `rig::ptt_line(on: bool)` -> `ptt_line(on: bool, data_source: bool)`,
   emitting Hamlib `RIG_PTT_ON_DATA` (`T 3`) instead of plain `RIG_PTT_ON` (`T 1`) when the
   operator has selected the rig's rear DATA/ACC port for transmit audio. `unkey` is `T 0`
   either way. Plus a `Rig::set_ptt_data_source` / `ptt_data_source` field.
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
**How to check:** `grep -n "fn ptt_line\|fn read_level\|fn set_power\|rfpower_suppressed" crates/tempo-audio/src/rig.rs`.

### `patches/tempo-audio-service.patch` -- radio-loop wiring, `crates/tempo-audio/src/service.rs`

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
  #126 says "not mid-over", Jimmy says "not at all when suppressed".
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
On v1.10.3 there are three `disable_rfpower_probe` drive gates in the loop body: the heavy-poll
`l RFPOWER` read (`if !self.disable_rfpower_probe` before `rig.read_level("RFPOWER")`), the
per-mode ceiling (`if !self.disable_rfpower_probe && should_command_rf_power(...)`), and the
Tune-power write (`tune_power.filter(|_| !self.disable_rfpower_probe)`), plus the two stamp
sites (`finish_cat_open`, `open_monitor`).

### `patches/tempo-audio-slot.patch` -- Fake-It physical capture + Rig-split do-not-set-mode

`crates/tempo-audio/src/slot.rs`, `apply_tx_dial_shift`:

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
**How to check:** `grep -n "read_freq\|FakeItShift\|dont_set_mode" crates/tempo-audio/src/slot.rs`.

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
