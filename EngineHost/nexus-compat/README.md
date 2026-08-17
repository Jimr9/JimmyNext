# Jimmy Test-owned Nexus compatibility patches

Nexus (https://github.com/kd9taw/Nexus) is a third-party, GPL-3.0 project. Jimmy is a
**consumer only**: we do not own it, do not modify the official checkout, and never submit
changes back to it (see "Rules" below).

Jimmy Next's EngineHost builds against a clean checkout of Nexus at the exact revision pinned
in `pin.txt`, with a small number of source patches applied on top by `scripts/prepare-nexus.ps1`
(run automatically as part of the normal build -- see that script). Those patches are the
**only** difference between what Jimmy actually builds and official upstream Nexus, and they
exist for exactly one reason: to carry functionality Jimmy Test genuinely requires that official
Nexus, at the pinned revision, does not yet provide (or, for one patch, to work around a Windows
build-toolchain interaction bug in Nexus's own build script).

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

## Current patches (against Nexus `v1.6.0`, commit `de01be9d`)

### `patches/tempo-app-engine.patch` -- 3 behaviors

| Behavior | Why Jimmy needs it | What's missing upstream |
|---|---|---|
| Early-pass decode accumulation | Without this, a QSO reply caught only on a slot's early decode pass is used by Nexus's own sequencer to drive the next transmission, but never reaches Jimmy's decode feed -- silently breaking **auto-logging** for a QSO that otherwise completed normally. | `Engine::observe`'s decode-recording path overwrites `last_wire_decodes`/`last_decodes` on every call instead of accumulating within the same slot. |
| `Engine::set_pskreporter(bool)` | Jimmy's Options UI has a live PSK Reporter on/off toggle that must not require a restart. | No live setter exists; only a start-of-session `Settings.pskreporter` field. |
| `Engine::last_own_tx_text()` | Feeds the WSJT-X-protocol `Status.tx_message` field (see the service.rs patch) so external tools that parse Jimmy's UDP status (GridTracker, JTAlert, etc.) can track which over is in progress. | No accessor for the most recent transmitted text exists. |

**Obsoleted when:** official Nexus accumulates same-slot decodes across an early + boundary pass,
and/or adds a live PSK-Reporter setter and a "last transmitted text" accessor to `Engine`.
**How to check:** `grep -n "last_decode_slot ==" crates/tempo-app/src/engine.rs` in a clean
checkout of the new revision -- if the overwrite (`self.last_wire_decodes = wire_copy...`) is
now conditioned on the slot rather than unconditional, the decode-accumulation part is fixed.
`grep -n "fn set_pskreporter\|fn last_own_tx_text"` for the other two.

### `patches/tempo-app-settings.patch` -- 2 behaviors

Adds `Settings.ptt_data_source: bool` and `Settings.dont_set_mode: bool`, both `#[serde(default)]`
(off), mirroring WSJT-X's Radio tab "Transmit Audio Source: Data" and "Mode: None".

**Why Jimmy needs it:** Jimmy's Options > Radio tab exposes both as operator settings (parity
with WSJT-X); EngineHost's CLI (`--ptt-data-source`, `--dont-set-mode`) sets them at launch.
**Obsoleted when:** official Nexus's `Settings` struct gains fields with equivalent meaning.
**How to check:** `grep -n "ptt_data_source\|dont_set_mode" crates/tempo-app/src/settings.rs`.

### `patches/tempo-audio-rig.patch` -- part of `ptt_data_source`

Changes `rig::ptt_line(on: bool)` to `ptt_line(on: bool, data_source: bool)`, sending Hamlib's
`RIG_PTT_ON_DATA` (`T 3`) instead of plain `RIG_PTT_ON` (`T 1`) when the operator has selected
the rig's DATA/ACC port for transmit audio. Adds `Rig::set_ptt_data_source`/a `ptt_data_source`
field to carry the setting through to the actual PTT command.

**Why Jimmy needs it:** without this, a station wired to the rig's rear DATA port (rather than
the front mic jack) cannot select that at the CAT level -- this is a real-station wiring case,
not a cosmetic setting.
**Obsoleted when:** `rig::ptt_line`'s signature (or equivalent) gains a mic/data distinction.
**How to check:** `grep -n "fn ptt_line" crates/tempo-audio/src/rig.rs` -- if it already takes
more than a plain `on: bool`, compare its behavior against WSJT-X's `TXAudioSource`.

### `patches/tempo-audio-service.patch` -- wiring for `ptt_data_source`/`dont_set_mode` + `tx_message`

Threads `ptt_data_source`/`dont_set_mode` from `RadioConfig` through the private `Transport`
struct to the `Rig` built in `open_cat` (both spawn paths), and changes the outbound WSJT-X UDP
`Status` message's `tx_message` field from a hardcoded `""` to `eng.last_own_tx_text()`.

**Obsoleted when:** the corresponding `tempo-app-engine.patch`/`tempo-app-settings.patch`/
`tempo-audio-rig.patch` items are obsoleted -- this patch only wires those through, it carries
no independent behavior of its own. Re-derive it against whichever of those three still apply.

### `patches/tempo-fast-sys-build.patch` -- Windows `\\?\`-path / gfortran build fix

Strips the `\\?\` extended-length-path prefix that `Path::canonicalize()` produces on Windows
before handing the path to CMake, whose generated Ninja/Makefile rules get mis-split by
MSYS2/MinGW gfortran's own path handling. Without this, a full rebuild from a fresh build
directory fails every Fortran compile step in `tempo-fast-sys`'s native `libtempo` build.

This is **not** one of the 5 behaviors reported to the operator -- it's a build-environment
compatibility fix, unrelated to Jimmy's runtime behavior. It is carried here (not as a separate
vendored crate copy) because `tempo-app`/`tempo-audio`/`tempo-core` all transitively depend on
`tempo-fast-sys`, and once those three are sourced from the same prepared checkout as this patch
set (see `prepare-nexus.ps1`), a separately-patched standalone copy of `tempo-fast-sys` would
either conflict or silently stop being used -- keeping every patch in one place, applied to one
consistent checkout, avoids that class of bug entirely.

**Obsoleted when:** official Nexus's own `tempo-fast-sys/build.rs` strips or avoids the
`\\?\` prefix itself (or the underlying Rust/CMake/MinGW interaction is fixed upstream in one of
those tools, making the workaround moot).
**How to check:** `grep -n "canonicalize" crates/tempo-fast-sys/build.rs` in a clean checkout of
the new revision, and try a build from a **fresh** build directory (`cargo clean` first) --
the bug does not reproduce on an incremental build, only a fresh one.

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
   change), not force it through.
4. Re-run the EngineHost test suite (`cargo test`, from `EngineHost/`) before committing the
   revision bump.

## Retired

`vendor/tempo-fast-sys-patched/` (a full local copy of the `tempo-fast-sys` crate with only the
`\\?\` fix applied, wired in via a Cargo `[patch]` section) served this same purpose before this
directory existed, and has been removed. It is superseded by `patches/tempo-fast-sys-build.patch`
above -- keeping it would have meant two different `\\?\` fixes, one of which (the Cargo
`[patch]` redirect) would have silently stopped applying once `tempo-app`/`tempo-audio` moved to
building from `prepare-nexus.ps1`'s prepared checkout instead of the developer's raw Nexus
clone, since the patched source URL would no longer match.
