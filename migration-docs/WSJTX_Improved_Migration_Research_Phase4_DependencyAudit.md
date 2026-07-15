# Jimmy — Phase 4: Static Dependency Audit on Andy WM8Q's Modified WSJT-X

Status: research only — no code changed. Pure static source read, cross-referenced with Phase 1–3's protocol/forensic findings. Every finding below is sourced to an exact file and, where practical, a line number, from a fresh read of the current working tree.

---

## 0. How to read this

Each dependency entry answers, in order: **file/function**, **the Andy-specific message/field/behavior**, **why Jimmy uses it**, **what it provides**, **standard-protocol equivalent (if any)**, **can Jimmy compute it itself**, **does WSJT-X Improved 3.1 already provide it**, then a **classification 1–6**:

1. Already satisfied by standard WSJT-X 3.1
2. Can be replaced by Jimmy-side computation
3. Can be eliminated because Jimmy no longer needs it
4. Requires a very small WSJT-X compatibility extension
5. Requires significant redesign
6. Cannot yet be determined

---

## 1. The wire-format facts, confirmed from source (foundation for everything below)

- **`Messages/Out/MessageType.cs`**: confirms the exact enum. `ENABLE_TX_MESSAGE_TYPE_2=16`, `ENABLE_TX_MESSAGE_TYPE_3=17`, `ENQUEUE_DECODE_MESSAGE_TYPE_2=17`, `ENQUEUE_DECODE_MESSAGE_TYPE_3=18`. Type 17 means two *different* things depending on which build generation is connected — Jimmy resolves this per-message via `WsjtxClient.IsWsjtx270Rc()` (`WsjtxClient.cs:312-315`), which just compares the negotiated Heartbeat revision number against a hardcoded constant (`lastWsjtx270RcRevision = 185`, `WsjtxClient.cs:186`).
- **`Messages/In/EnableTxMessage.cs`**: this is the generic RPC wrapper ("SetupTx" in Phase 1's terms). Wire fields: `Id, NewTxMsgIdx(uint32), GenMsg(string), SkipGrid(bool), UseRR73(bool), CmdCheck(string), Offset(uint32)`. Its own header comment says outright: *"This message class requires the use of a slightly modified WSJT-X program."*
- **`Messages/Out/DecodeMessage.cs:299-466`** (`EnqueueDecodeMessage`): **not** a legal append-only extension of stock Decode — it's a different wire layout even in the shared prefix. Stock Decode's `New/LowConfidence/OffAir` bools are replaced by `AutoGen`; the extension fields are `IsDx, Modifier, IsNewCallOnBand, IsNewCallAnyBand, IsNewCountryOnBand, IsNewCountry, Country, Continent, Azimuth, Distance`. This matters for the migration: it's not something a future WSJT-X could send *in addition to* stock Decode without also breaking a naive stock-protocol parser — Jimmy currently requires this exact non-standard layout or gets nothing.
- **`Messages/Out/StatusMessage.cs:74-136`**: **is** a legal append-only extension — confirmed by the defensive `if (cur < message.Length)` guards wrapping every extension field, meaning a stock/shorter Status message parses fine and just leaves the extension fields at their defaults. Extension fields, in wire order: `LastTxMsg, QsoProgress, TxFirst, DblClk, Check, TxHaltClk, TxEnableButton, TxEnableClk, MyContinent, MetricUnits`.
- **`AnnotationInfo` (message type 16, Fox/Hound external scoring)**: confirmed **zero usage anywhere in Jimmy's source** — grepped the whole tree, no hits. Not a current dependency at all.
- **Fox/Hound generally**: confirmed Jimmy has **no real Fox/Hound mode support** — `WsjtxMessage.IsFoxHound()` (`Messages/Out/WsjtxMessage.cs:357`) only pattern-matches decode *text* to split a possible multi-target Fox/Hound-style message into two ordinary decodes for queueing purposes; its own comment says "full f/h mode not supported" and the match is explicitly non-authoritative. Not a dependency on Andy's fork specifically — this is a self-imposed scope limit, unrelated to which WSJT-X build is connected.
- **Auto Sequence**: no Jimmy dependency found — Jimmy never reimplements WSJT-X's own QSO-sequencing; it only reacts to state WSJT-X reports (`qsoState`/`QsoProgress`).

---

## 2. Full `NewTxMsgIdx` sub-command inventory (the SetupTx RPC channel)

All 18 distinct sub-commands Jimmy sends through `EnableTxMessage`/`emsg`, with every call site (some values are sent from multiple places):

| Idx | Purpose | Call sites | Standard equivalent | Classification |
|---|---|---|---|---|
| 0 | De-init/close | `WsjtxClient.cs:2078` | none (Close, type 6, is outbound-from-WSJT-X only in stock) | 6 — needs live-test to see if simply not sending anything on exit is fine |
| 5 | Enable Debug | `WsjtxClient.Protocol.cs:1487` | none | 3 — diagnostic-only, Jimmy doesn't need WSJT-X's own debug logging to function |
| 6 | Clear | `WsjtxClient.cs:1460,3254` | **`Clear` is a standard message (type 3) — but it's documented Out-only (WSJT-X→client) in stock.** Sending a Clear *to* WSJT-X to trigger a decode-list clear is itself the non-standard part. | 6 — needs a live test against Improved to see if it accepts an inbound Clear at all |
| 7 | "Ack Req" — heartbeat keepalive / cmd-check round-trip | `WsjtxClient.Protocol.cs:436,625,1311` | none directly; closest is just relying on stock's own passive Status broadcasts | 5 — this is Jimmy's own confirmation mechanism for "did WSJT-X receive my command," foundational to the whole negotiation state machine, not a cosmetic feature |
| 8 | Disable Tx | `WsjtxClient.cs:2764` | **`HaltTx` (type 8 in stock!) is the standard message for stopping Tx** — note the numeric collision is coincidental (different message *types*, not sub-command values); semantically this Andy sub-command and stock HaltTx likely do the same thing | 4 — plausible near-1:1 replacement, needs live confirmation |
| 9 | Enable Tx | `WsjtxClient.cs:2731` | No standalone "arm Tx" standard message exists; closest is `Reply` (type 4), which arms Tx *in response to a specific decode* | 5 — semantic mismatch (Jimmy sometimes wants to arm Tx generically, e.g., resuming a paused CQ cycle, not just replying to one specific decode) — needs design work, not just a protocol swap |
| 10 | "Opt Req" — skip-grid/RR73/Tx-offset | `WsjtxClient.cs:1442,2971,3239,3341`; `Protocol.cs:1188,1250` | No standard remote toggle for these WSJT-X GUI checkboxes/spinner | 5 — no standard path found in Phase 1/3 research; likely the single hardest piece to replace since it's used across CQ-mode, offset-analysis, and RR73-reply logic |
| 11 | Enable Monitoring | `WsjtxClient.cs:2786` | none | 6 |
| 12 | Halt Tx | `WsjtxClient.cs:2822` | **Standard `HaltTx` message, type 8, exists and is documented control-capable** | 1 — very likely already satisfied; same live-test caveat as #8 |
| 13 | Reset Tx watchdog | `WsjtxClient.Protocol.cs:642`, comment: *"important! reset watchdog timer"* | none found | 5 — sent on every heartbeat cycle; if there's no standard equivalent, WSJT-X's own Tx watchdog could fire unexpectedly during long Jimmy-managed sessions |
| 14 | Set listen mode | `WsjtxClient.cs:2802` | none found | 6 |
| 15 | **Set Band / Tx First** | `WsjtxClient.Protocol.cs:1398` (`SetBandTxFirst`) | **No standard message carries frequency/band.** Confirmed in Phase 1 (`Configure`, the most control-capable standard message, has no frequency field) and re-confirmed here — this is real, not a gap in prior research. | 5 — this is the mechanism behind every Band Up/Down/Select Band action in Jimmy; also the mechanism for the initial-connect band sync (`Protocol.cs:457-466`) |
| 16 | Start LoTW upload | `WsjtxClient.Protocol.cs:1472` | none — WSJT-X's own LoTW upload trigger has no standard remote path | 3 — Jimmy already has its own independent LoTW upload client (`Logbook/LoTWQsoClient.cs`); this WSJT-X-side trigger looks redundant with Jimmy's own capability, not something that needs preserving |
| 17 | Set PSKReporter enable | `WsjtxClient.Protocol.cs:171,445` | WSJT-X's own PSKReporter-spotting checkbox has no standard remote toggle | 5 — real gap, though low operator-facing urgency (rarely changed after initial setup) |
| 18 | Get Power/SWR | `WsjtxClient.Protocol.cs:1416` | none | 5 — real gap; power/SWR is a rig-adjacent readout Jimmy currently gets by asking WSJT-X (which asks the rig via CAT) |
| 19 | Toggle Tuning | `WsjtxClient.Protocol.cs:1454` | none | 5 |
| 20 | Set Audio Level up/down | `WsjtxClient.Protocol.cs:1434` | none | 5 |
| 21 | Set Operating Mode (FT8/FT4) | `WsjtxClient.Protocol.cs:191` | none — no standard message switches FT8⇄FT4 | 5 — used in the startup sequence itself (`Protocol.cs:459`), not just an optional feature |
| 255 | Broadcast/log QSO (full ADIF) | `WsjtxClient.cs:2239` | none — this is Jimmy's own fallback self-logging path | 3 — Jimmy already has a complete independent logging pipeline (`Logbook/`); this exists as a *belt-and-suspenders* fallback for when WSJT-X's own `QsoLoggedMessage`/`LoggedADIF` doesn't arrive, not a primary dependency |

**A subtlety worth flagging explicitly**: sub-command 17 ("Set PSKReporter enable") also carries a literal identification string in its `GenMsg` field — `"(mod by KB0UZT, w/{pgmName} v{pgmVer} [FT8 for blind hams], qrz.com/db/KB0UZT)"` (`WsjtxClient.Protocol.cs:449`) — sent every time Jimmy connects. This is a cosmetic/identification use of a non-standard field, not functional, but it is one more place hard-coded to this RPC channel.

---

## 3. Handshake / startup synchronization — the most foundational dependency

`WsjtxClient.Protocol.cs:354-469`, the connection handshake, in exact order:

1. Wait for `HeartbeatMessage` (standard, type 0).
2. **Version-gate** the reported `Version/Revision` against `acceptableWsjtxVersions = {"2.7.0/204", "3.0.0-rc1/102", "3.0.0-rc1/103"}` (`WsjtxClient.cs:54`) — if not an exact match, a **blocking dialog** refuses to proceed at all (`Protocol.cs:374-385`).
3. Send Jimmy's own Heartbeat reply (standard).
4. Wait for the negotiation Heartbeat, confirm schema (standard).
5. Send **"Ack Req" (sub-command 7)** with a random `cmdCheck` string, which WSJT-X is expected to echo back in `StatusMessage.Check` — this is Jimmy's *own* confirmation that WSJT-X actually received and processed a command, since the standard protocol has no built-in command-acknowledgment mechanism (`Protocol.cs:436-443, 763-768`).
6. Send **"Set PSKReporter" (sub-command 17)** with the identification string.
7. Send **`HaltTx()` (sub-command 12)** "to sync up WSJT-X button state."
8. If band is unknown: `SetOperatingMode("FT8")` (**sub-command 21**), then `SetBandTxFirst(...)` (**sub-command 15**) to establish a known band/frequency.
9. Start a 10-second `cmdCheckTimer` waiting for the `Check` echo to confirm the whole handshake succeeded.

**This entire sequence — steps 5 through 9 — depends on non-standard sub-commands and the non-standard `Check`/`TxEnableButton` extended Status fields.** This is the single most consequential finding of this audit: it's not just advanced features that need Andy's fork — Jimmy's basic "are we connected and does WSJT-X actually understand me" logic is built entirely on the non-standard RPC channel. Classification: **5 (significant redesign)** — a stock-protocol version of this handshake would need a different confirmation mechanism entirely (e.g., trusting the first subsequent `Status` message as implicit acknowledgment, since there's no standard command-ack primitive to substitute).

---

## 4. Extended Status fields — every consumption site

| Field | Consumption site(s) | What it drives | Standard equivalent | Jimmy-computable? | Classification |
|---|---|---|---|---|---|
| `TxFirst` | `Protocol.cs:487,717` → `UpdateCallListAccessibleName()` | Whether Jimmy's advanced-layout panes are labeled "TX1/RX1" or "RX1/TX1" — purely a display/accessibility-label concern | none | **Yes** — Jimmy already tracks/sends `TxFirst` state itself via sub-command 15 when *setting* it; it could simply trust its own last-commanded value instead of reading it back | 2 |
| `MyContinent` | `Protocol.cs:580-585,812-817` → sets `ctrl.replyLocalCheckBox.Text` | Label text for the "reply to local" filter checkbox (shows the actual continent name instead of generic "loc") | none | **Yes** — Jimmy's own `LookupManager`/`ClubLogProvider` already resolves continent from a callsign; Jimmy's *own* callsign's continent is a one-time lookup, not a per-decode cost | 2 |
| `TxEnableButton` | `Protocol.cs:557,725` → `UpdateDblClkTip()` | Keeps a local mirror of WSJT-X's own Enable-Tx button state, used for a tooltip hint | none found | 6 — needs a live check of what standard signal (if any) reflects Tx-armed state |
| `TxHaltClk` | `Protocol.cs:1028-1036` | Detects WSJT-X halting Tx **on its own initiative** (not via a Jimmy command) and syncs Jimmy's belief to match | none found | 6 — this is exactly the piece Phase 3 flagged as needing a live capture to resolve |
| `TxEnableClk` | `Protocol.cs:1040-1058` → `HandleUnsolicitedTxResume()` (`WsjtxClient.cs:729-744`) | Detects WSJT-X's own "Wait and Reply" feature auto-resuming a stalled QSO, so Jimmy can sync its belief and announce it distinctly to the operator (critical for a screen-reader user — otherwise they'd have no way to know their radio started transmitting again on its own) | none found | 6 — same as above; this is the accessibility-critical one, not just a nice-to-have |
| `LastTxMsg` | `Protocol.cs:716` → `curTxMsg` tracking, drives status announcements when a transmission is interrupted mid-cycle by a different call | none found (no standard field carries "what was just transmitted") | 6 |
| `Check` | `Protocol.cs:592,763` | The handshake confirmation mechanism itself (§3) | none | 5 (tied to §3) |
| `QsoProgress` | `StatusMessage.cs:171-174` (`CurQsoState()`), consumed throughout `WsjtxClient.Protocol.cs` (`qsoStateConf`, `lastQsoState` tracking) | Jimmy's belief about which stage of a QSO exchange WSJT-X is currently in | none found — no standard field exposes QSO-state progress | 6 — this feeds `UpdateCallInProg()` and general state-machine tracking; likely a real gap, not easily inferred from anything else Jimmy has |
| `MetricUnits` | `Protocol.cs:727`; `WsjtxClient.Display.cs:246,365` | Whether to display distance in km or miles | none | **Yes, trivially** — this is a pure user preference; Jimmy could just have its own miles/km setting instead of mirroring WSJT-X's | 3 — arguably shouldn't have been read from WSJT-X in the first place |
| `DblClk` | referenced alongside `TxEnableButton`/`UpdateDblClkTip()` | Whether double-click-to-enable-Tx is on in WSJT-X, for a tooltip | none | 3 — cosmetic tooltip accuracy, not core functionality |

---

## 5. EnqueueDecode fields — every consumption site (the deepest, most pervasive dependency)

This is the single biggest surface area in the whole audit — it runs through admission (`WsjtxClient.CallQueue.cs`), ranking (`CallQueueRanker.cs`), category derivation (`Awards/AwardTagger.cs`), and display (`WsjtxClient.Display.cs`).

| Field | Representative consumption sites | What it drives |
|---|---|---|
| `IsNewCallOnBand` / `IsNewCallAnyBand` | `WsjtxClient.CallQueue.cs:43,50,177,237-238,318,486`; `WsjtxClient.Display.cs:399,416,424` | Core admission gate for whether a decode enters the call queue at all, "still needed" award exceptions, per-period queue caps, accessible alert triggering |
| `IsNewCountry` / `IsNewCountryOnBand` | `WsjtxClient.cs:1004-1005,1032`; `WsjtxClient.CallQueue.cs:238`; `SupportReportBuilder.cs:477,505` | Sets `Priority = NEW_COUNTRY`/`NEW_COUNTRY_ON_BAND`, which `AwardTagger.DeriveCategory` (`Awards/AwardTagger.cs:26-55`) then turns into the ranking `CallCategory` — this is a **direct input to Jimmy's core ranking tiers** |
| `IsDx` | `WsjtxClient.CallQueue.cs:40,42,49`; `WsjtxClient.cs:1014,1117`; `WsjtxClient.Display.cs:410,419-420` | DX/local origin filtering, directed-CQ interpretation, "reply to DX only" checkbox logic |
| `Country` | `WsjtxClient.Display.cs:220,232,349-350`; `Awards/AwardTagger.cs:172` (via resolved state, not directly) | Row display text, US-state display gating, feeds `AwardMatcher.Match`'s `Country`-based `GroupBy` rule matching |
| `Continent` | `Awards/AwardTagger.cs:172` (comment confirms: *"d.Continent comes straight from WSJT-X's own decode message — always available"*) | Feeds `Continent`-based award rule matching directly |
| `Azimuth` / `Distance` | `CallQueueRanker.cs:147-148,186`; `WsjtxClient.Display.cs:246-248,363-367` | **Beam-heading quadrant ranking** (one of Jimmy's configurable sort methods) and distance-sort ranking both consume these fields *directly from the wire* — this is a primary ranking input, not just a display nicety |

**Critical finding confirmed by a targeted search: Jimmy has zero existing great-circle bearing/distance code anywhere in its source** (searched for Haversine/GreatCircle/Bearing/Maidenhead-to-lat-lon patterns — no hits). This means:

- **New-call/new-country flags**: **classification 2 (Jimmy-side computable)** — the infrastructure substantially already exists. `LogbookDb` already has the worked-before data (used today for the `AwardMatcher.ShouldRejectAlreadyWorked` exception path), and `LookupManager`/`ClubLogProvider` already resolves DXCC/Country/Continent independently of the wire field (used today for Awards rule matching). This is wiring/integration work on top of existing subsystems, not a new subsystem.
- **Country/Continent**: **classification 2** — same reasoning; `LookupManager.Build(call)` already returns this from a different code path today. Notably, Jimmy is *already* computing Country/Continent for its own award-matching purposes in parallel with reading it off the wire for display purposes — an existing near-duplication that a migration would actually resolve, not create.
- **Azimuth/Distance**: **classification 2, but higher implementation cost than the above** — computable in principle (well-known, bounded math: Maidenhead grid → lat/lon → great-circle bearing/distance, standard formulas, needs Jimmy's own grid plus the DE station's grid, which Jimmy already has via `LookupManager`/QRZ in many cases, falling back to grid.dat-style data it already reads elsewhere) but genuinely **net-new code**, not existing-logic reuse. This is the one piece of the EnqueueDecode replacement that isn't "wire up what's already there."

---

## 6. Band/mode change, manual call, and other command paths

| Feature | File/function | Mechanism | Classification |
|---|---|---|---|
| Band Up/Down/Select Band | `WsjtxClient.BandAudio.cs:59-117` → `SetBandTxFirst` | Sub-command 15 (§2) | 5 |
| Mode toggle (FT8⇄FT4) | `Protocol.cs:191` (sub-command 21) | Sub-command 21 (§2) | 5 |
| Manual call entry (`ManualEnqueueCall`) | `WsjtxClient.CallQueue.cs:399-446` | **Standard `ConfigureMessage` (type 15)** — already confirmed working against the WSJT-X 3.1 test build in Phase 3's live capture (test callsign `W9TEST` appeared correctly in the resulting Status) | **1 — already satisfied**, no change needed |
| Enable Tx / Halt Tx (operator hotkeys Alt+E / Halt Tx button) | `WsjtxClient.cs:2731 (Enable), 2822 (Halt)` | Sub-commands 9 / 12 | Halt: 4 (plausible standard `HaltTx` replacement); Enable: 5 (no clean standard equivalent, semantic gap vs. `Reply`) |
| Skip-grid / RR73 toggle | Sub-command 10 throughout | No standard remote toggle found | 5 |
| Wait and Reply cooperation | `WsjtxClient.cs:729-744`, driven by `TxEnableClk`/`TxHaltClk` (§4) | 6 — genuinely unresolved pending live test |
| Live QSO capture | `QsoLoggedMessage`/`LoggedAdifMessage` handling | **Standard messages (types 5, 12)** | **1 — already satisfied** |
| Logging fallback (broadcast ADIF) | Sub-command 255 | Jimmy's own independent logging pipeline already covers this | 3 |

---

## 7. Dependency ranking — easiest to hardest to eliminate

**Tier 1 — trivial, arguably shouldn't have depended on WSJT-X at all:**
1. `MetricUnits` (§4) — replace with Jimmy's own setting.
2. `DblClk` tooltip accuracy (§4) — cosmetic only.
3. Sub-command 16 (LoTW upload trigger) — Jimmy already has its own LoTW client.
4. Sub-command 255 (broadcast-log fallback) — Jimmy already has its own primary logging path.
5. Sub-command 5 (Enable Debug) — diagnostic-only, unnecessary.

**Tier 2 — real work, but the hard part (data/infrastructure) already exists in Jimmy:**
6. New-call/new-country flags (§5) — wire `LogbookDb` + `LookupManager` into decode-time classification instead of trusting the wire fields.
7. Country/Continent (§5) — same `LookupManager` source, different call site.
8. `MyContinent` (§4) — one-time self-lookup instead of per-Status-message wire read.
9. `TxFirst` readback (§4) — trust Jimmy's own last-commanded value instead of reading it back.
10. Manual call entry — **already done**, just needs the version-gate relaxed to admit stock/Improved builds.
11. Live QSO capture — **already done**, same version-gate note.

**Tier 3 — genuinely new implementation work, but bounded and well-understood:**
12. Azimuth/Distance computation (§5) — standard great-circle math, zero existing code, needs building from scratch.
13. Halt Tx via standard `HaltTx` message — plausible but unverified; low implementation risk once confirmed live.

**Tier 4 — real protocol/behavioral gaps with no clean standard substitute found in three research passes:**
14. Enable Tx (generic arm, not decode-reply-triggered) — semantic gap vs. standard `Reply`.
15. Skip-grid/RR73/Tx-offset remote toggle (sub-command 10) — no standard path found.
16. Band/frequency change (sub-command 15) — confirmed in Phase 1 and re-confirmed here: no standard message carries frequency at all.
17. Operating-mode switch FT8⇄FT4 (sub-command 21) — no standard equivalent, and it's load-bearing in the startup sequence itself.
18. Power/SWR, audio level, tuning toggle, PSKReporter toggle (sub-commands 18/20/19/17) — real gaps, lower operator-facing urgency.

**Tier 5 — foundational, would need a redesign rather than a swap:**
19. The handshake/command-acknowledgment mechanism itself (§3) — Jimmy's whole "did WSJT-X hear me" confirmation loop (`cmdCheck`/`Check` echo) has no standard substitute; a stock-protocol version would need a different design (e.g., trusting the next Status broadcast as implicit ack), not a like-for-like message swap.
20. Tx watchdog reset (sub-command 13) — sent every heartbeat cycle specifically to prevent WSJT-X's own watchdog from firing; unclear what happens if this stops, needs investigation before removal.

**Cannot yet be determined (needs the live Tx-path testing Phase 3 didn't complete):**
- `TxHaltClk`/`TxEnableClk` (Wait-and-Reply cooperation) — the accessibility-critical one. This is the single most important item to resolve with live testing before finalizing a migration plan, since losing it silently would mean a blind operator has no way to know WSJT-X resumed or halted transmission on its own.
- `QsoProgress` — feeds general QSO-state tracking; no standard field found, not yet confirmed unresolvable.
- `TxEnableButton` mirror — minor (tooltip), but same "needs live test" status.
- Whether `HaltTx`(sub-cmd 12)/`Clear`(sub-cmd 6) really do map cleanly to the standard `HaltTx`/`Clear` messages, or whether Jimmy's non-standard versions do something subtly different.

---

## 8. What this means for the smallest possible compatibility layer

If every Tier 1–3 item above is resolved as classified (11 of the ~30 distinct dependency points), what would remain as genuinely non-standard is narrow and specific:

- The handshake/command-ack mechanism (§3) — needed for *any* remote command to be trustworthy, not optional.
- Enable Tx (generic arm) and skip-grid/RR73/offset toggle (sub-command 10) — Tier 4, no standard substitute found.
- Band/frequency change (sub-command 15) and mode switch (sub-command 21) — Tier 4, confirmed gaps.
- The Wait-and-Reply cooperation fields (`TxEnableClk`/`TxHaltClk`) — accessibility-critical, still unresolved pending live testing.
- Possibly `QsoProgress`, if nothing else can substitute for tracking exchange state.

That's a substantially smaller surface than today's full `EnableTxMessage`/`EnqueueDecodeMessage` dependency — consistent with Phase 1's original hypothesis (Option B: a small, targeted compatibility extension rebased on an actively-maintained upstream) being the realistic target, now with a concrete, source-verified list of exactly what that extension would need to cover rather than an estimate.
