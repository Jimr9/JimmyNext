# Jimmy vs. WSJT-X 3.1 Accessible Build — Phase 2: Functional Feature Comparison

Status: research only — no code changed. This document supersedes Phase 1's protocol-only framing; per instruction, this phase compares **functionality**, not wire format.

**UPDATE — see `WSJTX_Improved_Migration_Research_Phase3.md`.** Phase 3 obtained and forensically examined the actual `wsjtx-3.1.0-win64_for_visually_impaired_operators.exe` file. It confirmed the build is real (window title is compiled as "...v3.1.0 for visually impaired operators" — genuine branding, not a renamed generic installer) but found no accessibility-specific *functional* code beyond that title string, and surfaced a significant correction to Phase 1 (Andy WM8Q's fork appears to genuinely incorporate WSJT-X Improved's own feature set, not be independent of it) plus a meaningful upgrade to this document's characterization of WSJT-X Improved's native call-selection/filtering/alerting (§3.1/3.2/§7/§9.1 below — read Phase 3 §8 for the exact deltas). The accessibility conclusion itself (§5 below) is unchanged and now more strongly evidenced, not weaker.

---

## Correction from Phase 1, and an honest gap

Phase 1 wrongly treated "the visually impaired WSJT-X build used by QLog" as probably nonexistent. That was wrong to assert. It is being treated here as a real, distinct artifact — `wsjtx-3.1.0-win64_for_visually_impaired_operators.exe`, distributed via the **QLOGforblindhams** Groups.io group's Files section — until proven otherwise.

**What a second research pass could and couldn't establish**, so the comparison below is graded honestly rather than guessed:

- `groups.io/g/QLOGforblindhams` requires a logged-in membership to browse Files; every automated fetch route available in this session (direct fetch, Wayback Machine) was blocked. **The file itself was not directly inspected. Its exact contents, version string, and any bundled readme remain unconfirmed.**
- Independent corroborating evidence **was** found, from non-groups.io sources (a 2017 wsjt-devel mailing-list thread, Sam W2JDB's own posts on mail-archive.com, and community descriptions of the QLOGforblindhams ecosystem):
  - In 2017, a blind-ham advocate (Rich Zwirko, K1HTV) asked the WSJT-X core developers to add accessibility features directly into WSJT-X. **WSJT-X developer David Tiller's reply explicitly steered away from modifying WSJT-X itself and recommended a separate companion program using the UDP interface instead** — the same architecture Jimmy uses.
  - The accessibility layer in that community is consistently attributed to **QLog** (a separate program by Sam W2JDB, himself a blind operator) plus his companion utilities `AllText.exe` and `ChgWSet` — not to a modified WSJT-X GUI. Multiple independent descriptions state QLog provides CQ-response filtering and audio feedback "without relying on any screen reader," while WSJT-X runs underneath as the modem/decode engine.
  - Version "3.1.0 build 260226" matches a confirmed, real, official **WSJT-X Improved** release (DG2YCB) — whose own changelog (read directly) lists no accessibility-related changes.
- **Working hypothesis for this document, not a confirmed fact:** the `_for_visually_impaired_operators` build is most likely a stock or WSJT-X-Improved binary, repackaged/renamed for that community's convenience, with the actual accessibility work living in QLog rather than in WSJT-X's own code. This is inference from strong circumstantial and historical evidence, not a direct inspection.
- **I'm not able to close this gap further without either (a) direct groups.io member access, or (b) you sharing what you already know** — the actual file, its readme, its About-dialog version string, or your own first-hand experience running it. **That single fact would resolve the most consequential open question in this entire comparison** (Category 5 below), so I'm flagging it clearly rather than quietly assuming an answer either way. Everything below proceeds using WSJT-X Improved 3.1's confirmed feature set as the working WSJT-X baseline, with Category 5 marked accordingly wherever the accessible build's actual behavior would change the verdict.

---

## How to read each entry

Each feature line answers all nine of your requested dimensions in one compact block:

- **Presence:** Jimmy only / WSJT-X only / Both
- **Better:** which implementation, if both have it
- **Disposition:** Keep in Jimmy / Remove (WSJT-X covers it) / Become thin controller over WSJT-X / Needs additional protocol support / Redesign / Cannot yet be determined
- **Why:** mandatory whenever Jimmy keeps something instead of deferring to WSJT-X

"WSJT-X" below means the union of stock WSJT-X + WSJT-X Improved 3.1's confirmed feature set, unless a line specifically discusses the QLOGforblindhams-distributed build.

---

## 1. Operating Workflow

**1.1 CQ calling (non-directed / DX-only / directed-CQ with target text)**
Presence: Both. WSJT-X has a native "Call CQ" checkbox/mode and directed-CQ macros (e.g. `CQ POTA`, `CQ DX`) via its Tx-message editing. Jimmy adds a dedicated dialog (`CallCqDlg`) remembering the last-used CQ type/slot across sessions.
Better: Roughly tied at the raw-capability level; Jimmy's version is keyboard/screen-reader operable, WSJT-X's is mouse/GUI-first.
Disposition: **Keep in Jimmy.** Why: the underlying WSJT-X capability already exists and doesn't need duplicating logically — what Jimmy adds is exclusively the *accessible front door* to it (a non-modal dialog reachable and operable without ever touching WSJT-X's own GUI). That accessible front door has no WSJT-X equivalent in any variant researched.

**1.2 Reply-to-a-decode workflow (select a station, generate a QSO)**
Presence: Both. WSJT-X's native mechanism is: double-click a decode in Band Activity → auto-composes the exchange. This maps directly to the standard `Reply` UDP message.
Better: WSJT-X's underlying reply-composition logic (which of the six Tx messages to send next, given QSO state) is authoritative — Jimmy doesn't reimplement this; it *invokes* it.
Disposition: **Become a thin controller here — already true today.** Why N/A (this is already the architecture: Jimmy sends the equivalent of a "reply to this decode" command and lets WSJT-X's own state machine drive message content).

**1.3 Manual call entry (type a callsign directly, bypass the queue)**
Presence: Both. WSJT-X: type into the DX Call box directly. Jimmy: `ManualCallDlg`, pre-filled with the last call, optional QRZ grid-square sanity-check before committing, sent via the *standard* `Configure` message.
Better: Jimmy's version for accessibility (keyboard reachable, pre-filled, confirmed); WSJT-X's for raw simplicity if you can see the screen.
Disposition: **Keep in Jimmy.** Why: same reasoning as 1.1 — this is already using a standard WSJT-X message under the hood; the value-add is 100% the accessible dialog wrapper, not duplicated logic.

**1.4 QSO state tracking / sequencing (what message to send next in an exchange)**
Presence: WSJT-X only, authoritative. Jimmy does not reimplement FT8/FT4 QSO sequencing — it relies entirely on WSJT-X's own `qsoState`.
Disposition: **Remove/never build — already fully WSJT-X's job**, correctly so today. No change recommended.

**1.5 Fox and Hound / DXpedition mode**
Presence: WSJT-X only (native feature; also touched by Andy's fork's unused `AnnotationInfo` extension for Hound-queue sort-order). Jimmy has no DXpedition-specific mode.
Disposition: **Cannot yet be determined / low priority.** Why: no evidence Jimmy's operator base needs Fox/Hound support; flag as a possible future accessible-front-door candidate only if requested, not a gap today.

---

## 2. Automation

**2.1 Wait-and-Reply cooperation** (`HandleUnsolicitedTxResume`, reacting when WSJT-X's own "Wait and Reply" feature resumes a stalled QSO on its own initiative)
Presence: Both, but asymmetric — "Wait and Reply" itself is a **native WSJT-X feature**; Jimmy's piece is purely *reactive bookkeeping* so Jimmy's internal state and status announcements don't drift out of sync with what WSJT-X just did on its own.
Disposition: **Keep in Jimmy.** Why: WSJT-X has no way to tell a screen-reader user "I just resumed this on my own" — without Jimmy's cooperation logic, a blind operator would have no way to know their radio started transmitting again automatically. This is pure accessibility glue, not duplicated automation, and it depends on the non-standard `txEnableClk`/`txHaltClk` Status fields (see Phase 1 §2.2) — flagged there as needing a protocol-capture spike against the actual WSJT-X build in use.

**2.2 Per-call/per-period Tx timeout and retry tracking** (`txTimeout`, `xmitCycleCount`, timed-out-call bookkeeping)
Presence: Jimmy only. WSJT-X has no concept of "give up on this station after N cycles and move to the next" — that decision belongs entirely to the operator watching the screen, or to Fox/Hound's own DXpedition-specific logic.
Disposition: **Keep in Jimmy.** Why: this is precisely the automation a screen-reader user needs in place of "watching the screen and clicking the next station" — it has no WSJT-X equivalent for normal (non-Fox/Hound) operation and is core to Jimmy's reason for existing.

**2.3 Per-period call-queue admission caps** (`maxAutoGenEnqueue`, `maxQueuedCallsBase`)
Presence: Jimmy only.
Disposition: **Keep in Jimmy.** Why: exists to keep an accessible queue from becoming unmanageable to navigate by keyboard/speech on a busy band — a sighted operator scanning a screen doesn't need this, but a screen-reader user paging through a list does.

**2.4 Auto Tx-offset/slot analysis** (`CalcBestOffset`, congestion avoidance)
Presence: Jimmy only, as far as could be confirmed — nothing in WSJT-X Improved's release notes resembles automatic offset-congestion analysis (it does have manual waterfall click-to-set-offset, which is screen-dependent).
Disposition: **Keep in Jimmy.** Why: this replaces a purely visual task (look at the waterfall, click an empty spot) with a computed one — there is no non-visual equivalent in WSJT-X at all.

**2.5 Band-hopping**
Presence: WSJT-X Improved only (added as "customizable band-hopping" per its feature list — exact mechanics not independently verified this session). Jimmy has manual band-up/band-down only.
Disposition: **Cannot yet be determined.** Why: needs direct inspection of what WSJT-X Improved's band-hopping actually does (automatic unattended band cycling? scheduled? rule-based?) before deciding whether Jimmy should adopt/front-end it or leave it alone — worth a spike if unattended multi-band operation is a real use case for you.

---

## 3. Queue Management & Ranking

**3.1 Ranked call-priority queue** (multi-tier category weighting, configurable sort methods, beam-heading quadrants, LoTW-boost tiebreak)
Presence: Jimmy only, decisively. WSJT-X Improved's "call auto-selection/filtering" is basic decode-list filtering (e.g., hide already-worked, hide weak signals) — not a weighted, multi-criteria ranked queue integrated with award/DXCC state.
Disposition: **Keep in Jimmy — do not weaken this.** Why: this is Jimmy's single largest differentiator and the reason "priority order" exists as a concept at all in this ecosystem; nothing else researched, including the accessible-build hypothesis, comes close.

**3.2 Admission filtering** (CQ-only/CQ+grid/any-message, DX/local origin, azimuth window, already-worked exceptions, blocklist, unwanted-directed-CQ tracking)
Presence: Jimmy only (WSJT-X Improved's "filtering" is a much shallower version — visibility toggles on the decode list, not queue-admission logic feeding a ranked structure).
Disposition: **Keep in Jimmy.** Why: same rationale as 3.1 — this is the input-shaping layer for the ranking system; removing it breaks the thing that makes Jimmy's queue useful.

**3.3 Wanted-calls list / Always-wanted category**
Presence: Jimmy only.
Disposition: **Keep in Jimmy.** Why: no equivalent "notify me specifically when call X shows up, and rank them above everything else" concept found in WSJT-X.

**3.4 "Opposite-period" and "wanted-anywhere" alerting** (calls of interest heard outside the normal admission path)
Presence: Jimmy only.
Disposition: **Keep in Jimmy.** Why: purely an accessibility/attention-management feature — no visual equivalent needed for a sighted operator glancing at a waterfall, but essential for a non-visual one.

---

## 4. Transmit Behavior

**4.1 Enable/Disable/Halt Tx**
Presence: Both — likely mappable to standard `Reply`/`HaltTx` messages (per Phase 1 §2.2, unconfirmed pending a capture).
Disposition: **Become a thin controller — pending protocol confirmation.** Why N/A; this is exactly the kind of action that should NOT be independently reimplemented, only remotely triggered.

**4.2 Skip-grid / Use-RR73 toggles, Tx message-slot selection**
Presence: Both — WSJT-X has these as GUI checkboxes/radio buttons; Jimmy remotely toggles them via the non-standard `SetupTx` "Opt Req" sub-command.
Disposition: **Needs additional protocol support**, per Phase 1 — no standard-message path confirmed yet for remote toggling. Why keep remote control at all: a screen-reader user cannot reach a WSJT-X checkbox without full WSJT-X GUI accessibility, which doesn't exist in any variant — so this remains a hard requirement for Jimmy regardless of backend, only the transport mechanism is in question.

**4.3 Band/frequency switching**
Presence: Both, but WSJT-X owns the actual CAT link in every case (see Phase 1 §2.3 — no passthrough exists anywhere).
Disposition: **Needs additional protocol support or a rigctld side-channel** (open question, unchanged from Phase 1). Why keep in Jimmy: a screen-reader user needs an accessible way to trigger this; WSJT-X's own band-select UI has no accessibility layer in any variant found.

**4.4 Power/SWR readout, audio-level adjustment, tuning toggle**
Presence: Both — WSJT-X exposes these as GUI elements/meters; Jimmy remote-controls them via non-standard `SetupTx` sub-commands with no standard equivalent found.
Disposition: **Cannot yet be determined — genuine open question for you (Phase 1 §6.4).** Why keep in Jimmy if the answer is yes: these are visual meters/sliders in WSJT-X with no screen-reader-accessible readout in any variant found; if the answer is "acceptable to drop," Jimmy could shed this rather than build/maintain a bridge for it.

**4.5 Forced operating-mode switch (FT8 ⇄ FT4), PSKReporter-enable toggle, LoTW-upload-trigger, broadcast-log (ADIF) command**
Presence: Both — again GUI-native in WSJT-X, non-standard-command-driven in Jimmy.
Disposition: **Requires additional WSJT-X support**, unless a stock equivalent is found during the Phase 1 spike. Why keep remote-control in Jimmy: same accessibility argument as 4.2–4.4 — these are rare actions, but "rare" isn't "unimportant" for a keyboard-only/screen-reader operator who otherwise has zero access to that WSJT-X setting at all.

---

## 5. Accessibility (the crux category)

**5.1 Whole-application screen-reader operability**
Presence claim per your correction: **possibly WSJT-X (the specific accessible build) — unconfirmed.** Confirmed-elsewhere fact: WSJT-X Improved 3.1.0's own published changelog contains no accessibility items, and independent history (the 2017 K1HTV/David-Tiller exchange) shows the WSJT-X core team's own stated design preference is to keep accessibility **out** of WSJT-X and push it into a companion app — the same architecture Jimmy already uses.
Disposition: **Cannot yet be determined without direct inspection of the actual build** — this is the one item in the whole report where I will not guess. If it turns out the QLOGforblindhams build genuinely does add screen-reader hooks to WSJT-X's own GUI, that would be a first (nothing else researched across two research passes found this anywhere in the WSJT-X ecosystem), and it would materially change several dispositions above (especially 4.2–4.5, and potentially large parts of Section 3). If it turns out to be a repackaged stock/Improved binary with no source changes (the better-supported hypothesis right now), nothing above changes. **This is the one fact most worth you confirming directly** — from the file itself, its readme, or your own experience running it — before any further architecture decision is finalized.

**5.2 `AccessibleName` + `SendKeys` re-announce mechanism**
Presence: Jimmy only, confirmed.
Disposition: **Keep in Jimmy.** Why: this is a Windows-accessibility-API-level technique with no WSJT-X equivalent found in any variant.

**5.3 Terse, single-sentence, non-redundant status announcements**
Presence: Jimmy only.
Disposition: **Keep in Jimmy.** Why: explicitly, deliberately designed against verbosity per your own stated project rules — nothing found in any WSJT-X variant does this (WSJT-X's own GUI has no concept of "announcement" at all, screen-reader or otherwise).

**5.4 Change-detection before list re-render (avoid re-announcing unchanged lists)**
Presence: Jimmy only.
Disposition: **Keep in Jimmy.** Why: same rationale as 5.3.

**5.5 Accessible-navigation hotkeys** (`NavStatus`, `NavCallList`, `NavLoggedList`, etc.)
Presence: Jimmy only.
Disposition: **Keep in Jimmy.** Why: no WSJT-X variant has a keyboard-navigable structure analogous to this at all.

---

## 6. Logging

**6.1 Live QSO capture from WSJT-X**
Presence: Both — this is inherently WSJT-X's own action (it logs the QSO and broadcasts `QSOLogged`/`LoggedADIF`, both **standard** messages); Jimmy just listens.
Disposition: **Thin controller / listener — already correct today.** No change needed.

**6.2 Local logbook database** (SQLite, versioned schema, dedup by call+band+mode+date+minute, extensive metadata columns)
Presence: Jimmy only. WSJT-X's own logging is a flat ADIF file append with no dedup, no query engine, no state/DXCC/CQ-zone enrichment stored per-row.
Disposition: **Keep in Jimmy.** Why: this is the data backbone the entire Awards engine and ranking's "new call/new country" logic depend on — there is no equivalent database-with-query-engine in WSJT-X to defer to.

**6.3 ADIF import/export**
Presence: Both, roughly — WSJT-X exports its own flat log; Jimmy has a general-purpose importer/exporter with source filtering (`WSJTX/QRZ/LOTW/CLUBLOG/MANUAL`).
Disposition: **Keep in Jimmy.** Why: needed as the ingestion path for the sync engine (Section 7) and for populating the local DB from external sources — WSJT-X's own export has no filtering/source-tagging concept.

**6.4 Manual QSO entry into the logbook** (not just calling a station — logging a QSO that happened outside WSJT-X, e.g. phone/CW)
Presence: Jimmy only (not explicitly detailed in Phase 1's inventory beyond the DX-calling `ManualCallDlg`, but Jimmy's Logbook window has an `EditQsoDlg` for direct record editing/entry).
Disposition: **Keep in Jimmy.** Why: WSJT-X has no concept of logging a QSO it didn't itself decode.

---

## 7. Synchronization

**7.1 QRZ Logbook upload/download**
Presence: Jimmy only. WSJT-X has no QRZ Logbook API integration at all (only ADIF export a user could feed elsewhere manually).
Disposition: **Keep in Jimmy.** Why: no equivalent exists anywhere in the researched WSJT-X ecosystem.

**7.2 Club Log real-time + batch upload, country-data download**
Presence: Jimmy only.
Disposition: **Keep in Jimmy.** Why: same as 7.1; also feeds the Awards engine's DXCC/CQ-zone data (Phase 1 §2.2's EnqueueDecode-replacement plan depends on this already existing).

**7.3 LoTW upload/download**
Presence: mixed — WSJT-X's non-standard `SetupTx` sub-command 16 ("start upload to LoTW") suggests Andy's fork can *trigger* a LoTW upload, but the actual sync/dedup/scheduling logic is Jimmy's.
Disposition: **Keep in Jimmy** (the sync engine); the trigger mechanism itself falls under Section 4's protocol-support questions. Why: LoTW's confirmed-QSO tracking directly feeds Awards confirmation logic (`RuleConfirmation.Lotw`), which has no WSJT-X equivalent.

**7.4 Auto-sync scheduling, per-service refresh-days, "due" checking**
Presence: Jimmy only.
Disposition: **Keep in Jimmy.**

**7.5 Upload circuit breakers, redaction, dedup-safe upsert, already-downloaded-marks-as-uploaded logic**
Presence: Jimmy only — and notably, these were built in direct response to real incidents (Club Log IP-blocking risk, QRZ duplicate-retry starvation) that a naive integration would repeat.
Disposition: **Keep in Jimmy.** Why: this is hard-won correctness logic with no WSJT-X equivalent to defer to; removing it would reintroduce already-fixed bugs.

---

## 8. Lookups

**8.1 QRZ XML lookup (name/country/state/grid/continent/CQ/ITU zone/QSL manager/email)**
Presence: Jimmy only.
Disposition: **Keep in Jimmy.** Why: WSJT-X's own country/CQ-zone data (used for its basic worked-before highlighting, where present) comes from a static `cty.dat`-style file, not a live per-callsign lookup service — no equivalent richness.

**8.2 Club Log country-data (DXCC/CQ-zone/continent per prefix)**
Presence: mixed — WSJT-X ships its own static country-data file for basic decode coloring; Jimmy's Club Log integration is used for the Awards rule engine's universe resolution, a different and more demanding use case (arbitrary rule definitions, not just decode-list coloring).
Disposition: **Keep in Jimmy.** Why: feeds Awards (Section 9), which has no WSJT-X equivalent at all.

**8.3 FCC ULS state lookup** (weekly full-database download, dedup by highest license ID)
Presence: Jimmy only. (WSJT-X Improved's "WAS via FCC database" item suggests it also consumes FCC data for its one built-in award — see 9.1 — but this is a narrower, single-purpose consumer, not a general lookup provider Jimmy-style.)
Disposition: **Keep in Jimmy.** Why: feeds every US-state-dependent feature (WAS award, state display in Stations Available/Raw Decodes/Spot Watch rows) — general-purpose, not single-award-scoped like WSJT-X Improved's apparent usage.

**8.4 LoTW user-activity data (for ranking's LoTW-boost tiebreak)**
Presence: Jimmy only.
Disposition: **Keep in Jimmy.**

**8.5 Grid-to-US-state fallback** (`UsGridStateMap`, used when QRZ/FCC data is unavailable)
Presence: Jimmy only.
Disposition: **Keep in Jimmy.** Why: low-risk fallback logic, no reason to remove regardless of backend.

---

## 9. Awards

**9.1 General-purpose award rule engine** (INI-defined rules, arbitrary `GroupBy` kinds, SQL-driven evaluation, Count/Levels/All target types, endorsements, date-scoping)
Presence: Jimmy only, decisively. WSJT-X Improved's single built-in award ("Worked All States via FCC database") is one hardcoded case of what Jimmy's engine already does generally.
Disposition: **Keep in Jimmy — do not weaken this.** Why: this is Jimmy's second-largest differentiator alongside ranking; nothing in the WSJT-X ecosystem offers a general award-definition system, only this one specific built-in check.

**9.2 Live "still-needed" tagging feeding the call queue in real time**
Presence: Jimmy only.
Disposition: **Keep in Jimmy.** Why: this is what actually connects Awards to the ranking system (Section 3) — no WSJT-X equivalent exists for real-time award-relevance highlighting of incoming decodes at all, let alone feeding a priority queue.

**9.3 Award-category alert sounds and row colors**
Presence: Jimmy only.
Disposition: **Keep in Jimmy.**

---

## 10. DX Spotting

**10.1 PSKReporter live-spot MQTT watch list** (push-based, per-callsign subscription, customizable/sortable rows)
Presence: Jimmy only, as far as confirmed — WSJT-X Improved's release notes mention unspecified "PSK Reporter enhancements" with no further detail found.
Disposition: **Cannot yet be determined precisely, lean toward keep.** Why: even if WSJT-X Improved's PSK Reporter enhancement turns out to add spot-display to its own decode list, that's a different use case (watching *your own* decodes) from Jimmy's feature (subscribing to *other spotters'* reports of a specific watched callsign anywhere in the world, independent of what your own station currently hears) — worth a direct check once installed, but not assumed redundant.

**10.2 PSKReporter spot *reporting* (telling PSKReporter about what you decode)**
Presence: Both — this is WSJT-X's own native feature (a checkbox); Jimmy's non-standard `SetupTx` sub-command 17 just remote-toggles it.
Disposition: **Needs additional protocol support** if remote toggle capability must be kept (Section 4 territory) — the feature itself is entirely WSJT-X's, correctly so.

---

## 11. POTA / SOTA

**11.1 POTA same-day repeat-QSO suppression** (text-file-tracked, per band/mode)
Presence: Jimmy only. Detection itself parses WSJT-X's decode text for `CQ POTA` patterns — no external POTA API used by either side.
Disposition: **Keep in Jimmy.** Why: no WSJT-X equivalent found; small, low-risk, purpose-built.

**11.2 SOTA category** (present as a `CallCategory` in ranking, per Phase 1)
Presence: Jimmy only.
Disposition: **Keep in Jimmy.**

---

## 12. Settings & Configuration

**12.1 Options dialog** (13 tabs covering every Jimmy subsystem)
Presence: Jimmy only, obviously scoped to Jimmy's own features. (WSJT-X has its own, separate Settings dialog for its own concerns — General/Radio/Audio/Tx Macros/Reporting/Frequencies/Colors/Advanced.)
Disposition: **Keep in Jimmy** for everything that configures Jimmy-only features (ranking weights, awards, sounds, hotkeys, logbook sync, lookups). No overlap/duplication found with WSJT-X's own settings scope.

**12.2 Credential protection** (DPAPI encryption at rest for stored passwords/API keys)
Presence: Jimmy only.
Disposition: **Keep in Jimmy.** Why: security-sensitive, no WSJT-X equivalent (WSJT-X doesn't store third-party service credentials at all).

**12.3 INI-based settings persistence**
Presence: Both use INI files independently (Jimmy's own `.ini`, WSJT-X's own `WSJT-X.ini`) — Jimmy also *reads* WSJT-X's ini for UDP auto-detection (Phase 1 §16 equivalent).
Disposition: **Keep in Jimmy** as-is; this cross-reading is low-risk and convenient, no reason to change.

---

## 13. Notifications & Sounds

**13.1 Per-category sound alerts with cooldowns and callsign-specific overrides**
Presence: mixed — WSJT-X Improved added generic "audio alerts"/"multilingual alert voices" (basic match-tone concept); Jimmy's system is far more granular (11+ distinct event types, per-category enable/file, cooldown windows, three-tier file-resolution priority).
Disposition: **Keep in Jimmy.** Why: WSJT-X Improved's alerting is a coarse callsign-match tone, not tied to award/category/ranking state the way Jimmy's is — genuinely different depth, not a duplicate.

---

## 14. Networking & Connection Management

**14.1 UDP endpoint setup, auto-detection from WSJT-X's own ini, multicast support**
Presence: Jimmy-side necessarily (it's the client half of a connection WSJT-X is the server half of).
Disposition: **Keep in Jimmy**, unchanged regardless of backend — this is inherent to being a UDP client of any WSJT-X variant.

**14.2 WSJT-X version gating / build-compatibility checking**
Presence: Jimmy only, and this is precisely the mechanism that would need updating for any migration decided from this report (`acceptableWsjtxVersions`).
Disposition: **Keep in Jimmy**, update its contents per whatever Phase 1's spikes/this report's conclusions land on.

**14.3 Negotiation state machine, schema-version handling, running-detection (lock-file check)**
Presence: Jimmy-side implementation of a protocol WSJT-X defines.
Disposition: **Keep in Jimmy**, unchanged.

---

## 15. Keyboard Operation

**15.1 Full hotkey system** (~45 actions, user-remappable, conflict/reserved-key validation, dual-format speech-friendly rendering)
Presence: Jimmy only. WSJT-X has its own limited set of built-in keyboard shortcuts (not user-remappable, not screen-reader-formatted) but nothing resembling Jimmy's breadth or accessibility-first design.
Disposition: **Keep in Jimmy.** Why: core to the entire accessible-operation premise; no WSJT-X variant offers comparable remappable, conflict-checked, speech-friendly keyboard coverage.

---

## 16. Presentation / UI Customization

**16.1 Row display order/field customization** (three tabs: Stations Available, Raw Decodes, Spot Watch)
Presence: Jimmy only.
Disposition: **Keep in Jimmy.** Why: this is an accessibility feature at heart (controlling what gets read aloud and in what order) — WSJT-X's own decode-list columns are fixed and visual-only.

**16.2 Appearance (colors, font size, per-award-category alert row colors)**
Presence: Both have color/appearance settings, but WSJT-X's are for a sighted user's visual comfort (Dark Style, waterfall palettes); Jimmy's are lower-priority for a screen-reader-primary user but still used by low-vision users.
Disposition: **Keep in Jimmy**, low-priority/no-conflict — orthogonal concerns, not duplicated logic.

**16.3 Advanced Call Layout (TX1/TX2 split display), Raw Decodes panel**
Presence: Jimmy only, in this accessible-panel form.
Disposition: **Keep in Jimmy.**

---

## 17. Diagnostics & Support

**17.1 Support report builder** (ZIP with redacted ini + logs + diagnostic snapshot)
Presence: Jimmy only.
Disposition: **Keep in Jimmy.** Why: entirely orthogonal to WSJT-X; needed regardless of backend decision.

**17.2 Debug/diagnostic logging** (`[BAND-AUDIT]` and similar gated diagnostic output)
Presence: Jimmy only.
Disposition: **Keep in Jimmy.**

---

## Summary of what actually changes vs. Phase 1

Phase 1 (protocol-only) risked implying that shrinking Jimmy's non-standard UDP surface was the main event. This functional pass shows the opposite emphasis is correct: **the overwhelming majority of Jimmy's ~70 inventoried features have no WSJT-X equivalent in any variant researched, accessible-build hypothesis included.** The features that *are* shared with WSJT-X (Sections 1.1–1.3, 4.1, 6.1, 7.3's trigger, 10.2, 12.3, 14) are, without exception, cases where Jimmy is **already** acting as a thin accessible controller over a WSJT-X-owned capability — not areas carrying duplicated logic that could be deleted. The disposition "remove — WSJT-X already does it well" was reached for exactly one item in this entire pass: QSO-sequencing state (1.4), which Jimmy correctly never reimplemented in the first place.

**The one fact that could still change this conclusion** is Section 5.1 — if the QLOGforblindhams-distributed build genuinely does add screen-reader/accessibility hooks to WSJT-X's own GUI (unconfirmed either way), it would be the first such thing found anywhere in this two-pass research effort, and would be worth a much deeper look specifically at what it covers before concluding Jimmy's accessibility layer is untouchable. Until that's confirmed, the evidence (WSJT-X's own developers' 2017 stated preference to keep accessibility out of core WSJT-X, and the QLOGforblindhams community's own apparent reliance on a separate companion program, QLog, for exactly this purpose) points toward "no."

---

## What I need from you to close the loop

Since I hit a real access wall (Groups.io requires membership login; the Wayback Machine is blocked at the tool level in this environment) — could you check one of the following, whichever is easiest:

1. Do you already have `wsjtx-3.1.0-win64_for_visually_impaired_operators.exe` installed or downloaded? If so, its Help → About text (version/build string) and whether its menus/dialogs are actually screen-reader-navigable in your own hands-on experience would settle Section 5.1 definitively.
2. Is there a readme/description posted alongside the file in the QLOGforblindhams Files section that you could paste to me?
3. Failing both — do you use QLog (W2JDB's) yourself, and if so, does WSJT-X's own GUI ever need to be operated directly, or does QLog fully substitute for it in normal operation?

Any of those would resolve the last open question in this comparison.
