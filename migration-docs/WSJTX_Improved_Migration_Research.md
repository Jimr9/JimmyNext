# Jimmy → WSJT-X Improved: Architecture Research Report

Status: **research only — no code changed.** Per instructions, this is the deliverable to review and approve before any implementation planning begins.

---

## 0. Executive Summary

Jimmy's core accessibility value is **not replaceable by anything in the current WSJT-X ecosystem** — no variant of WSJT-X researched (stock, WSJT-X Improved, or Andy WM8Q's modified build) has JAWS/NVDA support, screen-reader-friendly dialogs, or non-verbose spoken status. Jimmy's reason for existing is fully intact regardless of which WSJT-X backend it targets.

The real architectural question is narrower than "should Jimmy exist" — it's **"how much non-standard protocol surface does Jimmy actually need, and can that shrink?"** Today Jimmy hard-requires a specific, separately-maintained WSJT-X fork (Andy WM8Q's, pinned to old base versions 2.7.0/3.0.0-rc1) because of ~2 dozen non-standard UDP additions. Investigation shows a meaningful fraction of that dependency is **removable** — Jimmy already owns the logbook/lookup infrastructure needed to compute several of those "enriched" fields itself — and the rest needs a small, targeted validation spike (not guesswork) to size accurately.

**Two premise corrections surfaced during research** — read Section 1 before anything else, since they affect how to interpret the rest of this report.

---

## 1. Critical Disambiguations (read first)

### 1.1 "WSJT-X Improved" and "Andy's modified WSJT-X" are two different, apparently unrelated projects

- **WSJT-X Improved** is maintained by **Uwe Risse, DG2YCB** (SourceForge: `wsjt-x-improved`, since 2020). Version **3.1.0** (build "260226", beta) is real and current. It adds: an open FT2 mode, callsign overlay on the waterfall, "Worked All States via FCC database," decoder work, Hamlib 4/5 support, call auto-selection/filtering, audio alerts, call highlighting, multiple layouts, Dark style, Full Duplex, AutoSeq, Fox/Superfox enhancements, band-hopping, PSK Reporter enhancements, high-DPI scaling. GPLv3, "experimental."
- **Andy's modified WSJT-X** — the build Jimmy's README requires — is maintained by GitHub user **avantol**, callsign **WM8Q** (same person credited for Jimmy's predecessor app **Tilly**, and for **Otto**, the successor to the old WSJTX-Controller). His modified-WSJT-X repos (`avantol/WSJT-X_3.0.0`, `_2.7.0-GA`, `_2.7.0-RC3`, etc.) are versioned as a **pair** with Tilly/Otto releases, each forked from a specific official K1JT source tag.
- **I could not confirm a code-lineage relationship between the two.** GitHub metadata shows avantol's repo as `"fork": false` (not a registered fork of anything), and its README credits only "modified for use with Otto," with no attribution to DG2YCB's project. Some feature overlap exists (both added call auto-selection/filtering, audio alerts, call-highlighting) but this looks like **convergent, independent reimplementation**, not a shared codebase. Treat "Andy's fork is based on WSJT-X Improved" as **unconfirmed** — it is not.
- **Practical implication:** migrating Jimmy to "WSJT-X Improved 3.1" is not a matter of following an existing upgrade path Andy already blazed — it means **Jimmy would be adopting a third, independent WSJT-X lineage** it has never targeted before, and re-deriving whatever protocol extensions it still needs against that codebase from scratch (or dropping them).

### 1.2 The "visually impaired build used by QLog" is very likely not a WSJT-X build at all

Two unrelated programs are both called "QLog":

1. The well-known general logger by **Ladislav Foldyna (OK1MLG)** (github.com/foldynl/QLog) — sighted-user-oriented, no blind/visually-impaired-specific features found.
2. **Sam, W2JDB's QLog** (distributed via `groups.io/g/ProgramsByW2JDB`) — a **separate Windows companion app**, in the same category as Jimmy/Tilly/Otto, that monitors WSJT-X's UDP traffic and announces events audibly "without relying on any screen reader" (own built-in TTS/audio engine, not a modified WSJT-X GUI).

**Neither is "the visually impaired build of WSJT-X."** There does not appear to be an accessible fork of WSJT-X's own GUI at all — every accessibility solution found in this research (Jimmy, Tilly, Otto, W2JDB's QLog) takes the identical architectural shape: **an external companion app that listens to WSJT-X over UDP and provides its own accessible interface**, because WSJT-X's native Qt GUI remains screen-reader-hostile in every variant checked (stock, Improved, Andy's fork). This is corroborating evidence, not a contradiction, for Jimmy's core design — but it means the original framing ("the visually impaired build used by QLog") should be understood as **a peer/competitor to Jimmy itself**, not a WSJT-X feature Jimmy could defer to. Whether W2JDB's QLog requires stock or a modified WSJT-X could not be confirmed (groups.io blocked automated fetch) — worth a manual look if a feature comparison against it specifically is ever wanted, but it doesn't change today's architecture conclusions.

---

## 2. The UDP Protocol: Standard vs. What Jimmy Actually Depends On

### 2.1 Standard/stock protocol (confirmed from official source, `NetworkMessage.hpp`)

16 message types, values 0–15, big-endian `QDataStream`, framed with a magic number + schema version (1/2/3; schema 3 = `Qt_5_4`, current). Negotiated once at Heartbeat time; a client/server may only ever *lower* the agreed schema, never raise it later. New fields are contractually appended only at the end of existing messages — this is the exact mechanism Andy's fork uses to extend Status non-breakingly.

| # | Name | Purpose |
|---|---|---|
| 0 | Heartbeat | version/schema negotiation |
| 1 | Status | live operating state (broadcast) |
| 2 | Decode | one decoded message (broadcast) |
| 3 | Clear | decode-list clear (broadcast) |
| 4 | **Reply** | **control** — tell WSJT-X to compose/arm a reply to a given decode |
| 5 | QSOLogged | a QSO was logged (broadcast) |
| 6 | Close | WSJT-X is closing (broadcast) |
| 7 | Replay | re-request decode history |
| 8 | **HaltTx** | **control** — stop transmitting |
| 9 | **FreeText** | **control** — set/send free-text Tx content |
| 10 | WSPRDecode | WSPR-specific decode (broadcast) |
| 11 | Location | control — set Maidenhead grid |
| 12 | LoggedADIF | full ADIF record of a logged QSO (broadcast) |
| 13 | HighlightCallsign | control — UI highlight only |
| 14 | **SwitchConfiguration** | **control** — switch active named Configuration |
| 15 | **Configure** | **control** — set mode/submode, Rx DF, T/R period, DX call/grid, generate-messages |

Notably: **no frequency/band, PTT, or split field exists anywhere in the standard protocol.** WSJT-X never exposes rig control over UDP to third parties in any variant.

### 2.2 What Jimmy actually depends on beyond this (Andy's fork, verified from source)

Three added message types, all tagged `//avt`:

- **16 — AnnotationInfo** (client→WSJT-X): attach a sort-order to a DX call, for Fox/Hound queue ordering during DXpedition-style operation. *Not currently used by Jimmy* per the codebase inventory.
- **17 — SetupTx** (client→WSJT-X): this is the wrapper Jimmy calls internally via `EnableTxMessage`/`NewTxMsgIdx`. It is effectively a **generic RPC channel** — a single message type whose sub-command field selects one of ~18 distinct actions. Jimmy's codebase sends sub-commands for: close/de-init, clear, ack/heartbeat-reply, disable Tx, **enable Tx**, skip-grid/RR73/Tx-offset ("Opt Req"), enable monitoring, **halt Tx**, listen mode, **set band / Tx-first (drives the rig via WSJT-X's own CAT link)**, start LoTW upload, set PSKReporter-enable, get power/SWR, toggle tuning, set audio level up/down, set operating mode (FT8/FT4), and a 255 "broadcast log QSO" (ADIF) command.
- **18 — EnqueueDecode** (WSJT-X→client): an enriched duplicate of the standard Decode message. WSJT-X itself computes and attaches `isDx`, `isNewCallOnBand`, `isNewCall`, `isNewCountryOnBand`, `isNewCountry`, `country`, `continent`, azimuth, and distance — i.e., **this fork moved the "have I worked this before / is this a new country" lookup into WSJT-X itself**, and Jimmy's entire call-queue-ranking/award-tagging pipeline is built directly on receiving that already-computed verdict.

Additionally (separately from new message types): Andy's fork appends several **new trailing fields onto the standard Status message** — `lastTxMsg`, `qsoProgress`, `txFirst`, `cQonly`, `genMsg`, `txHaltClk`, `txEnableState`, `txEnableClk`, `myContinent`, `metricUnits`. This is protocol-legal (fields only ever appended), but Jimmy's Wait-and-Reply cooperation logic (Section on feature #18 below) specifically depends on `txEnableClk`/`txHaltClk` — flags that only this fork sends.

### 2.3 Hamlib / CAT: no passthrough exists, and none is likely to appear

Checked directly against source: the standard **Configure** message (the most control-capable stock message) has no frequency/PTT/split field. Andy's fork's extensions don't add one either — `SetupTx`'s "set band" sub-command tells WSJT-X to change *its own* configured band, which then drives whatever CAT backend (Hamlib, rigctld, Commander, Flrig, HRD, OmniRig) WSJT-X itself is separately configured with. **There is no mechanism, in any variant, for a UDP client to drive PTT/split/frequency "through" WSJT-X.** The only real alternative topology is: both WSJT-X and Jimmy independently connect to a shared external `rigctld` daemon as peers — not a UDP passthrough, and a real architecture change (Jimmy currently has **zero** direct CAT/serial code — confirmed via full-codebase grep, zero hits for Hamlib/rigctl/SerialPort/COM).

---

## 3. Feature-by-Feature Comparison

Eighteen feature areas from the current codebase (full detail was gathered separately; this section gives the disposition and rationale for each). Categories per the requested taxonomy.

**1. Call-queue ranking & calling priorities** (`CallQueueRanker`, category tiers, beam-heading/SNR/distance sort, LoTW-boost tiebreak)
→ **Should remain uniquely in Jimmy.** WSJT-X Improved's "call auto-selection/filtering" is basic decode-list filtering; nothing in any researched variant does award-aware, multi-tier, user-weighted ranked prioritization. This is Jimmy's central differentiator. *Caveat:* the raw inputs it ranks on (new-call/new-country/azimuth/distance) currently come from Andy's fork's enrichment — see item 2.

**2. WSJT-X UDP protocol handling (the non-standard extensions themselves)**
→ **Split disposition, this is the crux of the whole report:**
 - *EnqueueDecode's enrichment (new-call/new-country/country/continent/azimuth/distance)* → **should remain a Jimmy-side computation**, not a WSJT-X dependency at all. Jimmy already owns a local logbook (`LogbookDb`) and lookup providers (Club Log, QRZ, FCC ULS) that independently determine worked-before/country/DXCC status for award purposes — the machinery to compute this from a **standard** Decode message plus Jimmy's own data almost certainly already exists in the Awards/Lookup subsystems. This is the single biggest opportunity to shed the fork dependency. Needs a validation spike (Section 5) to confirm parity, not a guess.
 - *SetupTx's Tx-control sub-commands (enable/disable/halt Tx, skip-grid/RR73, message-slot selection)* → **Cannot yet be determined without a protocol capture.** The standard Reply/HaltTx/FreeText/Configure messages look like plausible replacements for some of these, but no source-level confirmation was done of exact behavioral equivalence.
 - *SetupTx's non-Tx sub-commands with no standard equivalent* (band/frequency switch, power/SWR read, audio-level, tuning toggle, forced FT8/FT4 mode switch, PSKReporter-enable toggle, LoTW-upload-trigger, broadcast-ADIF-log) → **Requires additional WSJT-X support**, i.e. these have no stock protocol path at all. Each needs individual triage: is it essential to keep remote-controllable, or can the operator use WSJT-X Improved's own richer GUI/hotkeys for that specific rare action instead?
 - *Extended Status fields (`txEnableClk`/`txHaltClk`, used by Wait-and-Reply cooperation)* → **Cannot yet be determined** whether WSJT-X Improved's own Status message (as an independently-evolved fork) happens to carry equivalent fields — needs a capture, not an assumption.

**3. CAT / rig control**
→ **Not applicable / already correct as-is.** Jimmy has zero direct rig code today and should stay that way; WSJT-X (any variant) remains the sole owner of actual CAT control. No migration action needed here except resolving the "set band" command gap in item 2.

**4. Band/mode switching & transmit-slot/period tracking** (`CalcBestOffset`, congestion-avoiding Tx-offset auto-selection)
→ **Should remain uniquely in Jimmy** for the offset-analysis logic itself (nothing found in WSJT-X Improved's release notes resembles this). The band/mode-*switch command* itself is the same non-standard gap as item 2 — **requires additional WSJT-X support or a rigctld-based side channel** (open architecture question, Section 6).

**5. Hotkey system**
→ **Should remain uniquely in Jimmy.** Entirely local; not a WSJT-X-protocol concern at all.

**6. Accessibility / screen-reader integration** (`AccessibleName` + `SendKeys` re-announce trick, terse single-sentence status, change-detection before list re-render)
→ **Should remain uniquely in Jimmy — the single most important finding in this report.** Confirmed directly from WSJT-X Improved 3.1.0's actual release notes: no JAWS/NVDA support, no screen-reader-friendly dialogs, no non-verbose speech design. Its only audio addition ("multilingual alert voices") is a match-tone announcement, not a navigable accessible UI. Nothing in the researched ecosystem threatens this feature's reason for existing.

**7. Sound/notification system** (per-category enable/file, cooldown-managed alerts, callsign-specific override)
→ **Should remain uniquely in Jimmy**, with a narrow overlap noted: WSJT-X Improved's new "alert voices" cover the single narrowest case (a spoken/audible cue on a callsign match) but nothing like Jimmy's category taxonomy, cooldowns, or award integration.

**8. Options dialog**
→ Disposition follows its constituent features (mostly "remain unique," since it's the host UI for accessibility, ranking, awards, sounds, logbook sync, and lookup configuration — none of which move to WSJT-X).

**9. Awards engine** (INI-defined rule engine, SQL-driven evaluation, live "still-needed" tagging, GroupBy/Confirmation/Target-type combinatorics)
→ **Should remain uniquely in Jimmy.** WSJT-X Improved added one specific built-in award ("Worked All States via FCC database") — a single hardcoded case of what Jimmy's general rule engine already does as one configuration among many. Not a reason to shrink Jimmy's engine; if anything it validates the FCC-ULS-based approach Jimmy already independently built.

**10. Logbook subsystem** (local SQLite, dedup, multi-service upload/download sync, circuit breakers)
→ **Split**: live QSO *capture* (`QsoLoggedMessage`/`LoggedAdifMessage`) is standard-protocol, "already provided by WSJT-X" in the sense that any variant sends it — no change needed. The **sync engine** (QRZ/Club Log/LoTW dedup, auto-sync scheduling, upload circuit breakers, redaction) → **should remain uniquely in Jimmy**; nothing resembling this exists in any researched WSJT-X variant.

**11. Lookup subsystem** (QRZ/Club Log/FCC ULS/LoTW merge-priority provider chain)
→ **Should remain uniquely in Jimmy.** Serves Jimmy's own ranking/award pipeline; no WSJT-X variant exposes third-party lookup data to a UDP client.

**12. DX Spot Watch (PSKReporter MQTT)**
→ **Cannot yet be determined precisely, lean toward remain unique.** WSJT-X Improved's release notes mention unspecified "PSK Reporter enhancements" — details weren't found. Jimmy's implementation (accessible, customizable-row, sortable, dedicated watch-list panel independent of the main queue) is unlikely to be subsumed by whatever WSJT-X Improved added to its own decode-list display, but this should be spot-checked once WSJT-X Improved 3.1 is actually installed and exercised.

**13. POTA tracking** (same-day repeat-QSO suppression from decode-text pattern matching)
→ **Should remain uniquely in Jimmy.** No POTA-specific handling found in any researched variant.

**14. Manual QSO / call entry**
→ **Split, and good news:** the underlying WSJT-X action (`ConfigureMessage`, type 15) is **already a standard message** — this piece of Jimmy is already protocol-compatible with stock/Improved with no bridge needed. The accessible workflow around it (pre-fill, QRZ confirmation dialog, focus-reclaim after WSJT-X steals foreground) → **should remain uniquely in Jimmy.**

**15. Row display customization**
→ **Should remain uniquely in Jimmy.** Pure local accessible-presentation logic.

**16. Setup/Connection dialog** (UDP endpoint config, auto-detection from WSJT-X's own ini)
→ **Should remain uniquely in Jimmy**, low-risk, no change needed — this is the same regardless of which WSJT-X variant is on the other end.

**17. Support report builder**
→ **Should remain uniquely in Jimmy.** Unrelated to WSJT-X features entirely.

**18. Wait-and-Reply / cooperation logic** (`HandleUnsolicitedTxResume`, reacting to WSJT-X's own automatic Tx-state changes)
→ **Cannot yet be determined.** Depends entirely on Andy's fork's extended Status fields (`txEnableClk`/`txHaltClk`). Whether WSJT-X Improved's own Status message (an independently-evolved fork) happens to carry an equivalent signal is unknown without a live capture — flagged as a required spike, not assumed either way.

---

## 4. Proposed Target Architecture

**Principle applied:** simplest architecture that (a) keeps 100% of Jimmy's accessibility/ranking/award/logbook value — none of it is replaceable by anything found in the ecosystem — while (b) minimizing custom WSJT-X protocol surface, so Jimmy can ride an actively-maintained upstream (WSJT-X Improved) instead of a narrowly-pinned, independently-maintained fork.

Recommended direction, in order of preference:

- **Option A — stock-protocol-only Jimmy.** Drop all three non-standard message types. Recompute new-call/new-country/azimuth/distance/continent Jimmy-side from a standard Decode message plus Jimmy's own logbook/lookup data (already exists). Replace Tx-control sub-commands with standard Reply/HaltTx/FreeText/Configure where a spike confirms equivalence. For the genuinely un-replaceable sub-commands (band-switch, power/SWR, audio-level, tuning-toggle, PSKReporter-toggle, LoTW-upload-trigger), either accept the operator occasionally reaching into WSJT-X Improved's own GUI/hotkeys for that one rare action, or grow a narrow rigctld-based side channel for the rig-adjacent ones only (band/frequency). This is the architecture that lets Jimmy track WSJT-X Improved's upstream (decoder improvements, FT2, Hamlib 5, etc.) with zero fork maintenance burden.
- **Option B — minimal self-maintained compatibility patch.** If the Option-A spike shows some capability truly can't be dropped or replaced (most likely candidate: the Wait-and-Reply Status-field signal, item 18), maintain a **much smaller** patch than today's — just the 1–3 fields/messages actually needed — rebased periodically against WSJT-X Improved's own repo (an actively maintained upstream) rather than against old pinned K1JT tags the way Andy's fork does today. Substantially less maintenance surface than the status quo, while still capturing WSJT-X Improved's ongoing improvements.
- **Not recommended:** staying on Andy's fork as the long-term target. It's independently maintained, pinned to old WSJT-X base versions (2.7.0/3.0.0-rc1), and — per Section 1.1 — has no demonstrated relationship to WSJT-X Improved's ongoing development, so Jimmy inherits none of WSJT-X Improved's decoder/protocol/Hamlib improvements by staying there.

The end state in either option is **the same Jimmy** from the operator's perspective — same ranking, same awards, same accessible UI, same logbook sync — just talking to a different, better-maintained WSJT-X backend through a smaller, more standard protocol surface.

---

## 5. Required Validation Spikes (before implementation planning)

These are research/measurement tasks, not implementation — appropriate to do before this report is acted on:

1. **Install WSJT-X Improved 3.1.0 and capture its actual UDP traffic** (Heartbeat, Status, Decode) against Jimmy's current parser to get a byte-level, not release-notes-level, confirmation of exactly which non-standard fields are genuinely absent — including whether it has picked up any similar Status-field extensions independently.
2. **Prototype (throwaway harness, outside Jimmy) computing new-call/new-country/azimuth/distance/continent from a stock Decode message + Jimmy's existing Logbook/Lookup data**, and diff the results against what Andy's fork's EnqueueDecode currently supplies for a real decode sample, to confirm parity before committing to Option A.
3. **Enumerate which SetupTx sub-commands have true standard equivalents** (Reply/HaltTx/FreeText/Configure) by testing each against WSJT-X Improved 3.1 directly, producing the residual gap list Section 3/Option A-B decisions depend on.
4. **Confirm whether W2JDB's QLog requires stock or a modified WSJT-X** (direct check of `groups.io/g/ProgramsByW2JDB` — blocked to automated fetch this session) in case its approach offers a shortcut for anything in spike 1–3.
5. **Decide the band/frequency-switch philosophy** — accept the residual WSJT-X dependency, or have Jimmy join a shared `rigctld` daemon as its own CAT client for that one action.

---

## 6. Open Questions for the User

1. Given Section 1.2, do you want a feature comparison against W2JDB's QLog specifically (a peer accessible companion app), separate from this WSJT-X-backend research?
2. Target Option A (stock-protocol-only, maximal decoupling) vs. Option B (small self-maintained patch rebased on WSJT-X Improved) vs. staying on Andy's fork — or defer this decision until the Section 5 spikes narrow the actual gap size?
3. During any transition, should Jimmy support **both** Andy's fork and WSJT-X Improved simultaneously (version-detected), or cut over in one release? Existing users are currently all on Andy's fork.
4. Is it acceptable for a small number of rare, WSJT-X-side actions (power/SWR check, tuning toggle, forced mode switch, PSKReporter enable) to require the operator to use WSJT-X Improved's own GUI directly if no standard/small-patch equivalent is found — or must every current Jimmy hotkey action remain fully remote-controllable from Jimmy no matter the cost?
5. Should Jimmy grow a direct/rigctld-based CAT capability for band/frequency switching specifically, or keep depending on WSJT-X to do it (residual gap either way)?

---

## Sources

- WSJT-X Improved: https://sourceforge.net/projects/wsjt-x-improved/ , https://wsjt-x-improved.sourceforge.io/Release_Notes.txt , mailing list announcement https://sourceforge.net/p/wsjt-x-improved/mailman/message/59301070/
- Andy WM8Q's modified WSJT-X: https://github.com/avantol/WSJT-X_3.0.0 (`Network/NetworkMessage.hpp`, `Network/MessageClient.cpp`), https://github.com/avantol/Otto , https://github.com/avantol/Tilly
- Stock protocol reference: https://github.com/saitohirga/WSJT-X/blob/master/Network/NetworkMessage.hpp
- QLog (general logger, OK1MLG): https://github.com/foldynl/QLog
- QLog (W2JDB, accessible companion): https://groups.io/g/ProgramsByW2JDB
- Jimmy's own `README.md` (WSJT-X build requirements) and `WSJTX_Controller/` source (full inventory retained in this session's working notes; available on request).
