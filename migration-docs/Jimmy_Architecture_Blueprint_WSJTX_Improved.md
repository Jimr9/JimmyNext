# Jimmy Next-Generation Architecture Blueprint — Built Around WSJT-X Improved 3.1

Status: design document, no code written. This is the long-term engineering blueprint referenced going forward; it supersedes the "preserve today's structure" assumption entirely. It builds directly on the findings in Phase 1–4 (protocol comparison, functional comparison, forensic build investigation, static dependency audit) — every disposition decision below traces back to a specific Phase 4 finding, not a generic pattern.

---

## 0. Design philosophy

Four phases of research converged on one fact: **Jimmy's entire unique value — ranking, awards, logbook sync, lookups, accessibility — has zero overlap with anything WSJT-X provides, in any variant.** Nothing about this migration is "can WSJT-X replace Jimmy." The actual problem, precisely stated by Phase 4, is narrower and more mechanical: **today's code lets WSJT-X's wire format leak directly into Jimmy's core logic** (ranking reads `EnqueueDecodeMessage.Azimuth` directly; a UI label reads `StatusMessage.MyContinent` directly), so a change to the WSJT-X backend is a change to Jimmy's core, not just its plumbing.

The single architectural principle this design applies everywhere:

> **Jimmy's core logic must never see a WSJT-X wire type. It only ever sees Jimmy's own domain model, populated by an adapter.**

Everything else in this document is that principle applied to each subsystem.

---

## 1. Overall system architecture

Eight layers, strictly one-directional dependency (each layer only calls the one below it; nothing reaches back up except through published events):

```
1. WSJT-X Improved 3.1        (external process — decoder, CAT/Hamlib owner, GUI)
        |  standard UDP protocol only
2. Protocol Adapter            (the ONLY code that parses/builds WSJT-X datagrams)
        |  + optional
3. Compatibility Layer         (isolated, pluggable, degrades gracefully if absent)
        |  Jimmy's own domain events (DecodedCall, RadioState, QsoLogged...)
4. Jimmy Core                  (event bus, RuntimeStateModel, orchestration)
        |
5. Classification Engine       (new-call/new-country/country/continent/azimuth/distance)
        |
6. Feature Engines             (Queue/Ranking, Award, Lookup, Logbook, Notifications)
        |
7. Accessibility Layer         (the only layer allowed to touch screen-reader/focus APIs)
        |
8. UI Layer                    (WinForms shell — thin, presentational only)
```

Layers 5–7 are largely **already shaped this way** in today's codebase (Awards/, Logbook/, Lookup/ are already clean, protocol-agnostic modules). The redesign work is almost entirely in layers 2–4 and in disciplining layers 6/8 to stop reaching past layer 4/5 into wire types directly.

---

## 2. Major modules and responsibilities

**WsjtxProtocolAdapter** (new, replaces most of `WsjtxClient.Protocol.cs`'s parsing half)
Owns: the UDP socket, standard message parsing/building only (Heartbeat, Status, Decode, Clear, Reply, QSOLogged, Close, HaltTx, FreeText, Configure, LoggedADIF, SwitchConfiguration), standard schema negotiation, connection-alive detection.
Never: touches ranking, awards, logbook, or any Jimmy business logic. Never parses a non-standard field. If asked to do something the standard protocol can't do, it returns "not supported" — it does not know the Compatibility Layer exists.

**WsjtxCompatibilityExtension** (new, isolated; today's `EnableTxMessage`/`EnqueueDecodeMessage`/extended-`StatusMessage`-field logic lives only here after migration)
Owns: everything Phase 4 classified Tier 4/5 — generic Tx-arm, band/frequency change, RR73/skip-grid toggle, mode switch, the Wait-and-Reply signal fields, if still needed after live testing.
Never: is required for Jimmy to start or operate at a baseline level. Every capability it offers has a defined, tested fallback for when it's absent.

**CapabilityNegotiator** (new, replaces `acceptableWsjtxVersions`)
Owns: probing what the connected WSJT-X build actually supports, once, per connection. Publishes a `CapabilitySet` (e.g., `HasCompatibilityExtension: bool`, `NegotiatedSchema: int`) that the Compatibility Layer and Core both read. Replaces a hardcoded version-string allowlist with runtime detection.

**ClassificationEngine** (new, formalizes Phase 4's "Jimmy-computable" findings into an actual module)
Owns: turning a raw decode into an enriched `ClassifiedCall` — `IsNewCallOnBand`, `IsNewCallAnyBand`, `IsNewCountry`, `IsNewCountryOnBand`, `Country`, `Continent`, `Azimuth`, `Distance` — computed from `LogbookEngine` (worked-before) and `LookupProviders` (country/continent), plus a new geo-math submodule for azimuth/distance (Maidenhead grid → lat/lon → great-circle bearing/distance — standard, bounded, well-documented math with no existing Jimmy code to build from, per Phase 4 §5).
Never: talks to the network directly for a live per-decode lookup — it's built entirely from data `LookupProviders`/`LogbookEngine` already have cached, matching today's hot-path performance characteristics.

**QueueEngine** (today's `CallQueueRanker.cs` + the admission-gate half of `WsjtxClient.CallQueue.cs`, unchanged internally)
Owns: admission filtering, per-period caps, blocklist, category-tier ranking, sort methods, LoTW-boost tiebreak. Consumes `ClassifiedCall` only — the ranking math itself does not change at all, only what feeds it.

**AwardEngine** (today's `Awards/`, essentially unchanged)
Owns: Rule Definition loading/evaluation, live "still-needed" tagging, award alerts. Already consumes resolved Country/Continent/State rather than wire types in most places — the only change is that resolution now always comes from `ClassificationEngine` instead of sometimes from the wire.

**LookupProviders** (today's `Lookup/`, unchanged)
Owns: QRZ, Club Log, FCC ULS, LoTW-activity, grid-to-state fallback, merge-priority. No change — already protocol-agnostic.

**LogbookEngine** (today's `Logbook/`, unchanged)
Owns: local DB, dedup, live capture (from standard `QSOLogged`/`LoggedADIF` via the Protocol Adapter), QRZ/Club Log/LoTW sync, circuit breakers. No change — already protocol-agnostic, and its capture path already uses standard messages only.

**NotificationEngine** (today's `NotificationSounds.cs` + category logic, unchanged)
Owns: sound playback, cooldowns, category mapping. Consumes Core events, not wire types.

**AccessibilityLayer** (today's `AccessibleName`/`SendKeys` pattern + `IJimmyView`, formalized as a hard boundary)
Owns: every touch of a WinForms control's accessibility properties, terse status-sentence construction, change-detection before re-render, hotkey dispatch. The *only* layer allowed to call `SendKeys`/`AccessibleName` directly.
Never: reaches into `WsjtxProtocolAdapter` or `ClassificationEngine` directly — it subscribes to Core-published state/events only.

**UILayer** (WinForms shell, thin)
Owns: control layout, painting, mouse interaction for sighted/low-vision users. Reads the same Core state the Accessibility Layer reads; does not duplicate business logic.

---

## 3. Module boundaries — what must never happen

- `QueueEngine`, `AwardEngine`, `LookupProviders`, `LogbookEngine`, `AccessibilityLayer`, `UILayer` must **never** `using WsjtxUdpLib.Messages...` or reference `EnqueueDecodeMessage`/`StatusMessage`/`EnableTxMessage` by name. If a compile-time check is worth adding later, this is the one worth enforcing.
- `WsjtxProtocolAdapter` must **never** reference `CallCategory`, `RuleDefinition`, `LogbookDb`, or any Award/Queue/Logbook type. It has no idea Jimmy has a ranking system.
- `WsjtxCompatibilityExtension` must **never** be a hard dependency of anything outside itself and `CapabilityNegotiator`. Every caller must have a fallback path defined *at the call site*, not as an afterthought.

---

## 4. Data flow

```
WSJT-X (standard Decode datagram)
  -> WsjtxProtocolAdapter.ParseDecode()          [raw DecodedCall: callsign, snr, offsets, message text]
  -> ClassificationEngine.Classify(DecodedCall)   [+ IsNewCall*, Country, Continent, Azimuth, Distance]
  -> AwardEngine.DeriveCategory(ClassifiedCall)   [+ CallCategory, MatchedAwardRuleId]
  -> QueueEngine.Admit(ClassifiedCall)            [admission gate: filters, caps, blocklist]
  -> QueueEngine.Rank(admitted queue)             [ranking: tiers, sort method, LoTW-boost]
  -> Core publishes QueueChanged event
  -> AccessibilityLayer + UILayer render
```

Every arrow is a plain Jimmy-domain object, never a wire type, from `ClassificationEngine` onward.

---

## 5. Event flow

Core runs a single internal event bus (formalizing what `IJimmyStatusView`/`IJimmyQueueView`/`IJimmyLogView` already gesture at today). Published events, all consumed only downstream (layers 5–8), never by the Adapter or Compatibility Layer:

- `ConnectionStateChanged` (Disconnected / Negotiating / Connected / ConnectedDegraded)
- `RadioStateChanged` (band, mode, frequency, decoding, transmitting — from standard Status)
- `DecodeReceived` (raw, pre-classification)
- `CallClassified` (post-Classification-Engine)
- `QueueChanged`
- `QsoLogged`
- `AwardAlertRaised`
- `WsjtxAutonomousTxChange` (**only** published if the Compatibility Layer is present and detects it — Wait-and-Reply cooperation's actual event, decoupled from the field name that produces it)

---

## 6. Startup sequence

1. UI launches; **AccessibilityLayer initializes immediately** — hotkeys, AccessibleName wiring — Jimmy is keyboard-operable before any WSJT-X connection exists. (This is already true today and must not regress.)
2. `LookupProviders` load local caches (no network calls yet).
3. `LogbookEngine` opens the local DB, runs pending schema migrations.
4. `AwardEngine` loads Rule Definitions.
5. `WsjtxProtocolAdapter` reads WSJT-X's own ini for the UDP endpoint (unchanged from today) and opens its socket.
6. Standard Heartbeat exchange completes; standard schema negotiated.
7. `CapabilityNegotiator` probes for the Compatibility Layer (bounded timeout, non-blocking — Jimmy does not wait indefinitely or refuse to proceed if it doesn't respond).
8. Core waits for the **first naturally-occurring Status broadcast** (WSJT-X sends one at startup anyway — no non-standard ack loop needed) and transitions to `Connected` (or `ConnectedDegraded` if the Compatibility Layer didn't respond).
9. `AccessibilityLayer` announces connection state once, tersely.
10. Auto-sync (QRZ/Club Log/LoTW) scheduled on its existing delayed-start timer.

---

## 7. Shutdown sequence

1. Operator-initiated exit.
2. Core signals shutdown; `QueueEngine` stops admitting new decodes.
3. `WsjtxProtocolAdapter` simply stops listening and closes its socket — the standard protocol's `Close` message is WSJT-X→client only, so Jimmy sends nothing special on exit.
4. `LogbookEngine` flushes and closes the DB cleanly.
5. In-flight sync operations get a bounded grace period, then are abandoned safely — every sync operation is already idempotent/resumable (dedup keys, upload-status columns), so an interrupted sync is never lost, just deferred to next launch.
6. Settings persisted.
7. UI closes.

---

## 8. Connection lifecycle (state machine)

```
Disconnected
   -> Negotiating        (Heartbeat exchange in progress)
   -> CapabilityProbing   (non-blocking, bounded timeout)
   -> Connected            (standard protocol confirmed; compat layer absent)
   -> ConnectedFull        (standard protocol + compat layer both confirmed)
   -> Disconnected          (on heartbeat timeout or WSJT-X close/lock-file gone)
```

**The core philosophical change from today:** the current design has a *hard, blocking* version-gate — if the connected build's `Version/Revision` string isn't in a hardcoded allowlist, Jimmy refuses to operate at all and shows a modal dialog. The new design has **no such gate**. Any build that speaks the standard protocol reaches `Connected`. Only the Compatibility-Layer-dependent features are unavailable in that state, each with its own defined fallback (see §14).

---

## 9. Runtime state model

Core holds one authoritative `RadioState`, but — this is the key structural change — every field is tagged with its **provenance**, not just its value:

- **Jimmy-commanded**: Jimmy knows the value because it asked WSJT-X to set it (e.g., band, after `SetBandChange`).
- **WSJT-X-reported**: Jimmy learned it from a standard Status broadcast (e.g., current frequency, decoding state).
- **WSJT-X-autonomous-change**: Jimmy learned that WSJT-X changed something *on its own initiative*, not in response to a Jimmy command — this category **only exists if the Compatibility Layer is present** (it's what today's `TxEnableClk`/`TxHaltClk` handling actually is).

Today's codebase already does this informally and inconsistently, one field at a time (`lastTxEnabled`/`txEnabledConf` pairs, `lastQsoState`/`qsoStateConf` pairs, scattered across `WsjtxClient.Protocol.cs`). Making provenance an explicit, first-class property of the state model — rather than a naming convention repeated per-field — is what makes the Compatibility Layer's boundary enforceable rather than aspirational.

---

## 10. Logging flow

Unchanged in shape from today, just re-anchored to standard messages and Core events instead of direct field access:

```
WSJT-X QSOLogged / LoggedADIF (standard messages)
  -> WsjtxProtocolAdapter
  -> Core publishes QsoLogged event
  -> LogbookEngine.Capture()          [dedup by existing key, unchanged logic]
  -> LiveQsoUploadOrchestrator         [QRZ/Club Log/LoTW real-time push, unchanged]
```

Manual QSO entry (`EditQsoDlg`) publishes the same `QsoLogged` event synthetically — no protocol involvement at all, exactly as today.

---

## 11. Lookup flow

`ClassificationEngine` is the **only** caller of `LookupProviders` in the hot (per-decode) path. Today, `WsjtxClient.Display.cs` and others sometimes call `lookupManager` directly in a few places, duplicating a lookup `ClassificationEngine` will already have done — the new design removes that duplication by making `ClassifiedCall` carry the resolved Country/Continent/State so nothing downstream re-queries. Background, user-initiated lookups (the Lookup dialog, QRZ auto-queue for unidentified stations) remain a separate, async path, unchanged.

---

## 12. Award flow

```
ClassifiedCall (Country/Continent/State/worked-before, all resolved)
  -> AwardEngine.DeriveCategory()      [same switch/priority logic as today's AwardTagger]
  -> AwardEngine.CheckAwardAlert()      [same cooldown/sound logic as today]
  -> feeds CallCategory into QueueEngine ranking tiers
```

No behavior change — `RuleEngine`, `RuleDefinition`, `AwardMatcher` are already pure, tested, protocol-agnostic and stay exactly as they are. Only their *input* changes source (from `ClassificationEngine` instead of partially from the wire).

---

## 13. Queue flow

```
ClassifiedCall + CallCategory
  -> QueueEngine.Admit()     [CQ-only/grid filters, DX/local origin, azimuth window,
                               already-worked exceptions, blocklist, per-period caps —
                               all unchanged logic, now reading ClassifiedCall fields]
  -> QueueEngine.Rank()       [category tiers, sort method, beam-heading quadrants,
                               LoTW-boost tiebreak — unchanged math]
  -> QueueChanged event
```

`CallQueueRanker.cs` and the admission-gate logic in `WsjtxClient.CallQueue.cs` do not need new algorithms — they need their input type changed from `EnqueueDecodeMessage` to `ClassifiedCall`, which is a mechanical, low-risk refactor once `ClassificationEngine` is validated (see roadmap §17).

---

## 14. Accessibility flow

Unchanged in design, formalized as a hard boundary:

```
Core state/event change
  -> AccessibilityLayer.BuildAnnouncement()   [terse, single-sentence, de-duplicated
                                                 — same discipline as today's ShowStatus()]
  -> AccessibilityLayer.Announce()             [AccessibleName update + SendKeys "{UP}"
                                                 re-announce trick, focus-guarded exactly
                                                 as today]
```

Every degraded-capability case (Compatibility Layer absent) must have an explicit, accessible announcement of *why* a feature is unavailable, rather than a silent no-op — e.g., if band-change has no working backend, the hotkey should announce "Band change not available with this WSJT-X build" rather than doing nothing. This is a new requirement this architecture introduces that today's codebase doesn't need (today, the feature is either there or Jimmy refuses to start at all).

---

## 15. Synchronization flow

No change at all. `LogbookAutoSync`, `QrzLogbookClient`, `ClubLogUploadClient`, `LoTWQsoClient`, circuit breakers, redaction — none of this touches WSJT-X's protocol today and none of it should. This entire subsystem is already correctly isolated.

---

## 16. Error handling

- **Protocol Adapter**: malformed datagrams are logged and dropped, never crash the process — matches today's `ParseFailureException` handling exactly.
- **Compatibility Layer**: its *absence* is not an error condition anywhere in the system — every consumer has a required, tested fallback. Its *failure* (e.g., a malformed response) degrades that one feature, not the connection.
- **LookupProviders / LogbookEngine**: failures isolated per-provider, exactly as today (already has circuit breakers, `TestModeGuard`, redaction).
- **Core**: bulkhead discipline — one subsystem's exception must never break the decode pipeline for the others. Mostly true today; worth making an explicit design rule rather than an emergent property.

---

## 17. Recovery after WSJT-X restarts

1. `WsjtxProtocolAdapter` detects loss (heartbeat timeout, same lock-file check as today) → Core transitions to `Disconnected`.
2. `QueueEngine` **preserves its current ranked queue** across the disconnect — a WSJT-X blip must not silently wipe the operator's in-progress call list.
3. `AccessibilityLayer` announces disconnection once (not repeatedly).
4. On reconnect, `CapabilityNegotiator` **re-probes from scratch** — WSJT-X may have restarted into a different build than before; capability state must never be cached across a connection boundary.
5. **Design choice, stated explicitly rather than left implicit**: on reconnect, Jimmy resyncs its *belief* to WSJT-X's fresh state (band, mode, frequency) rather than automatically re-commanding its last-known values. Silently re-issuing a band change the moment WSJT-X comes back would surprise an operator who deliberately changed something on the WSJT-X side while Jimmy was disconnected. If auto-reassert is ever wanted, it should be an explicit opt-in setting, not the default.

---

## 18. Version negotiation

Stays exactly the standard protocol mechanism it already partially is — Heartbeat's `MaxSchemaNumber` field, schema 1/2/3, "never negotiate up after negotiating down." The only change: **remove** the non-standard `curVerBld` string-allowlist and its blocking dialog entirely. Schema negotiation and capability negotiation (§19) are kept as two separate concerns — schema is about wire format, capability is about which optional features exist.

---

## 19. Capability negotiation (new)

Replaces `acceptableWsjtxVersions` outright. On connect, after standard Heartbeat completes, `CapabilityNegotiator` sends one bounded-timeout probe toward the Compatibility Layer's extension surface. Two outcomes:

- **Responds correctly** → `ConnectedFull`, all features available.
- **No response / times out** → `Connected` (degraded) — standard-protocol features work fully; Compatibility-Layer-dependent features (§20) individually report unavailable through the Accessibility Layer's explicit fallback messaging.

This is a strictly better failure mode than today's: instead of "wrong WSJT-X build → Jimmy refuses to run at all," it becomes "wrong/newer WSJT-X build → Jimmy runs at full standard-protocol capability, with a short, honest list of what's currently unavailable."

---

## 20. Test strategy

- **Protocol Adapter**: pure unit tests against captured/synthetic byte streams — extends today's `JimmyReplay.py` pattern, but with two capture sets: one from a stock/Improved-only connection, one with the Compatibility Layer present.
- **Compatibility Layer**: tested with the extension both present and absent — every fallback path gets its own test, not just the happy path. This is a genuinely new testing discipline; today's codebase has no "what happens when Andy's fork isn't there" tests because that scenario was never supposed to happen.
- **ClassificationEngine**: unit tests diffing Jimmy-computed classification against historical `EnqueueDecodeMessage`-supplied values from real replay captures, to prove parity *before* the Queue/Display cutover — this is Phase 1's original "spike 2" made concrete.
- **QueueEngine / AwardEngine**: unchanged — already well-covered by the existing 300+ test JimmyTests suite, since these are already pure and protocol-agnostic.
- **End-to-end**: extend `JimmyReplay.py` scenarios to run twice per scenario — once against a simulated Compatibility-Layer-present WSJT-X, once against a simulated standard-only WSJT-X — proving Jimmy functions (at whatever capability level is correct) in both.

---

## 21. Every Phase 4 dependency, placed

| Phase 4 item | Belongs in |
|---|---|
| Standard Decode/Status/Heartbeat/QSOLogged/LoggedADIF/Configure/HaltTx | **Standard WSJT-X** (Protocol Adapter consumes as-is) |
| New-call/new-country/on-band flags | **Jimmy** (ClassificationEngine, using LogbookEngine — already has the data) |
| Country/Continent | **Jimmy** (ClassificationEngine, using LookupProviders — already has the data) |
| Azimuth/Distance | **Jimmy** (ClassificationEngine, new geo-math submodule — genuinely new code) |
| `MyContinent` | **Jimmy** (one-time self-lookup at startup, not a per-Status wire read) |
| `MetricUnits` | **Jimmy** (becomes a plain Jimmy setting; disappears as a WSJT-X dependency) |
| `TxFirst` readback | **Jimmy** (trust own last-commanded value via RuntimeStateModel provenance, §9) |
| `DblClk` tooltip | **Disappears entirely** (cosmetic only) |
| Sub-cmd 16 (LoTW upload trigger) | **Disappears entirely** (Jimmy's own `LoTWQsoClient` already covers this) |
| Sub-cmd 255 (broadcast-log fallback) | **Disappears entirely** (redundant with Jimmy's primary logging path) |
| Sub-cmd 5 (Enable Debug) | **Disappears entirely** (diagnostic-only, unnecessary) |
| Manual call entry (`Configure`, type 15) | **Standard WSJT-X** — already using it correctly, zero change needed |
| Live QSO capture | **Standard WSJT-X** — already using standard messages, zero change needed |
| Halt Tx (sub-cmd 12) | **Protocol Adapter** — plausible standard `HaltTx` replacement, pending one live confirmation test |
| Enable Tx, generic arm (sub-cmd 9) | **Compatibility Layer** (no standard substitute found; ideally this becomes a WSJT-X Improved feature request upstream over time) |
| Skip-grid/RR73/Tx-offset (sub-cmd 10) | **Compatibility Layer** (no standard substitute found) |
| Band/frequency change (sub-cmd 15) | **Compatibility Layer**, OR a separate rigctld-based side channel bypassing WSJT-X entirely — open design choice, see §22 |
| Mode switch FT8⇄FT4 (sub-cmd 21) | **Compatibility Layer** (no standard substitute; also currently load-bearing in the startup sequence, which the new design removes it from — see §6, startup no longer forces a mode switch) |
| Power/SWR, audio level, tuning toggle, PSKReporter toggle (sub-cmds 18/20/19/17) | **Compatibility Layer**, low priority — strong candidates to **disappear entirely** if the operator is willing to use WSJT-X Improved's own GUI for these rare actions (this was an open question in Phase 1/2, still open — a real decision point for you, not something this document should decide unilaterally) |
| The handshake/`cmdCheck` ack mechanism | **Disappears entirely as a concept**, replaced by CapabilityNegotiator + "first natural Status broadcast = connected" (§6, §19) — not ported, redesigned |
| Tx watchdog reset (sub-cmd 13) | **Cannot yet be determined** — needs a live test to see whether WSJT-X Improved's own watchdog configuration makes this external reset unnecessary |
| `TxEnableClk`/`TxHaltClk` (Wait-and-Reply cooperation) | **Compatibility Layer**, accessibility-critical, **cannot yet be determined** whether a standard/Improved-native substitute exists — this is the single most important open item, per Phase 4 |
| `QsoProgress` | **Compatibility Layer** for the "WSJT-X changed this on its own" detection use case specifically; **Jimmy** (own state tracking) for anything Jimmy itself initiated |
| `AnnotationInfo` / Fox-Hound scoring | **Disappears entirely** — confirmed zero current usage, out of scope |
| Fox/Hound mode generally | **Disappears entirely** — confirmed no real support today, not a dependency to preserve |

---

## 22. Open design decisions this document deliberately does not resolve

Two points from the table above are genuine decisions for you, not engineering calls this blueprint should make unilaterally:

1. **Band/frequency change**: route through a small Compatibility Layer extension (keeps everything WSJT-X-mediated, simplest mental model) vs. have Jimmy join a shared `rigctld` daemon as its own CAT client (decouples entirely from any WSJT-X extension, but gives Jimmy its first-ever direct hardware dependency). Recommendation if asked: start with the Compatibility Layer option, since it's smaller and keeps Jimmy's "zero direct CAT code" property intact; revisit rigctld only if the Compatibility Layer proves unmaintainable long-term.
2. **Power/SWR, audio level, tuning toggle, PSKReporter toggle**: keep remote-controllable via Compatibility Layer, or accept the operator uses WSJT-X Improved's own GUI directly for these rare actions. This trades a small amount of Compatibility Layer surface against a small amount of operator friction for infrequent actions.

---

## 23. Migration roadmap

### Order of work

1. **Build `ClassificationEngine` in parallel with the existing wire path — validate, don't cut over.** Compute new-call/new-country/Country/Continent from `LogbookEngine`+`LookupProviders`, diff continuously against the live `EnqueueDecodeMessage` values Jimmy already receives, log any mismatch. Zero behavior change, pure validation. Lowest risk item in the whole roadmap.
2. **Add the geo-math submodule (Azimuth/Distance) the same way** — parallel validation against wire-supplied values before ever being trusted.
3. **Introduce the Protocol Adapter boundary internally**, as a refactor of `WsjtxClient.Protocol.cs`'s parsing half, with zero wire-format change yet. Pure internal restructuring — moves code, doesn't change behavior. Safe, mechanically testable.
4. **Extract the Compatibility Layer** as an isolated, optional module — move every `NewTxMsgIdx` sub-command behind it with an explicit fallback defined per capability (per §21's table).
5. **Replace the hard version gate with `CapabilityNegotiator`** — Jimmy can now at least *attempt* a connection to a stock/Improved build instead of refusing outright.
6. **Cut over `QueueEngine`/Display to consume `ClassificationEngine`'s output** instead of wire `EnqueueDecodeMessage` fields — safe now that steps 1–2 have proven parity over real operating sessions.
7. **Redesign the handshake/ack mechanism** to standard-protocol-only — do this only after 3–6 are stable and tested, since it's the highest-blast-radius change (§24).
8. **Live-test end-to-end against WSJT-X Improved 3.1 with the Compatibility Layer intentionally absent** — confirm every degraded-mode fallback announces correctly and doesn't silently misbehave. Get your explicit sign-off on §22's open decisions at this point, once the real gaps are visible in practice rather than on paper.
9. **Resolve Wait-and-Reply cooperation** — do the live Tx-path test that Phase 3 deferred, now in a properly scoped, low-risk way (a real audio device, a real but muted/attenuated test setup, or simply doing it yourself with me observing UDP traffic passively), to determine definitively whether `TxEnableClk`/`TxHaltClk` need a Compatibility Layer signal or have a standard substitute.
10. **Final cutover** — update README/install docs to describe the new capability-based compatibility story instead of a fixed build allowlist, ship.

### Which pieces can be converted independently (no ordering dependency between them)

Steps 1, 2, 3, and 5 have no dependency on each other — they could genuinely be done in parallel, in any order, by separate work sessions. Step 4 depends on step 3 existing (needs the Adapter boundary to extract *from*). Step 6 depends on 1–2 having proven parity. Steps 7–9 depend on everything before them being stable.

### Highest risk

- **Step 7 (handshake/ack redesign)** — touches literally every command Jimmy sends; if the "first natural Status broadcast = connected" replacement doesn't actually behave the same way in practice, every downstream feature is affected simultaneously. Do this last, not first, despite it being conceptually foundational.
- **Step 6 (Enable-Tx arming semantics, if it changes at all)** — this is the single most safety/correctness-critical piece of Jimmy per the project's own standing rule ("never knowingly lose a valid FT8/FT4 station or QSO opportunity due to a code change"). Any change here needs the most conservative validation of anything in this roadmap.

### Which pieces should never be touched until later (or possibly at all)

- `CallQueueRanker`'s actual ranking math (tiers, weights, sort methods) — Phase 4 confirms this needs zero changes, only its input type changes. Touching the ranking logic itself during this migration is scope creep.
- `Awards/RuleEngine` internals — same reasoning, already protocol-agnostic.
- `Logbook/` sync logic (QRZ/Club Log/LoTW clients, circuit breakers) — already correctly isolated, zero WSJT-X coupling today.
- Anything in the Accessibility Layer's existing `SendKeys`/`AccessibleName` mechanics — this is proven, working, hard-won code; the migration should route new events into it, not rewrite it.

### What success looks like when the migration is complete

- Jimmy runs against WSJT-X Improved 3.1 (or later releases) with **no required non-standard patch** for the large majority of functionality — ranking, awards, logbook, lookups, accessibility all work identically to today, unconditionally.
- The Compatibility Layer, if still needed at all, is small, independently versioned, and clearly optional — Jimmy starts and operates without it, at a defined and honestly-announced reduced capability level, rather than refusing to run.
- The version-gate blocking dialog is gone, replaced by capability negotiation that degrades gracefully instead of failing hard.
- Every ranking/award/logbook/accessibility behavior is unchanged from today's operator-facing experience — this migration is invisible to the operator except in what it newly *tolerates* (a wider range of connectable WSJT-X builds), never in what it changes about how Jimmy behaves day to day.
- The test suite proves both "Compatibility Layer present" and "standard-protocol-only" paths work, for the first time ever — today's "what if the fork isn't there" scenario is simply untested because it was never supposed to occur.
