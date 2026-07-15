# Jimmy Master Migration Roadmap

Status: planning document, no code changed. This is the single authoritative implementation plan, synthesizing:
`WSJTX_Improved_Migration_Research.md` (Phase 1, protocol), `WSJTX_Improved_Migration_Research_Phase2.md` (functional comparison), `WSJTX_Improved_Migration_Research_Phase3.md` (forensic + live protocol evidence), `WSJTX_Improved_Migration_Research_Phase4_DependencyAudit.md` (static dependency audit), `Jimmy_Architecture_Blueprint_WSJTX_Improved.md` (target architecture), `Jimmy_DotNet_Modernization_Report.md` (runtime migration). Where this document and a source report disagree on a detail, this document is authoritative going forward; the source reports remain as the evidence trail behind each decision.

---

## 0. Guiding principles carried forward from all six reports

1. **Nothing about Jimmy's unique value is at risk.** Ranking, awards, logbook sync, lookups, and accessibility have no equivalent anywhere in the WSJT-X ecosystem, in any variant, confirmed twice independently (Phase 2's functional pass, Phase 3's forensic pass). This migration changes *how* Jimmy talks to WSJT-X and *what runtime it runs on* — it does not change what Jimmy does for the operator.
2. **Existing unit and replay tests are the default validation method.** Live testing against real WSJT-X/real radio hardware is reserved for the specific, narrow set of behaviors that cannot be validated any other way (Tx-arming semantics, real FT8/FT4 timing, CAT/audio/radio state) — not a default fallback for convenience.
3. **Two independent tracks, not one.** The WSJT-X protocol/architecture migration (Track A) and the .NET runtime migration (Track B) touch different files, carry different risk profiles, and have no technical dependency on each other. Per your explicit instruction, this roadmap does not merge them for scheduling convenience — see §2 for the explicit evaluation of whether a "strong reason" to combine them exists (it doesn't).
4. **Safest work first, always.** Every stage in both tracks is ordered so that structural/refactor work with zero behavior change comes before anything that touches live transmit behavior, and the riskiest items (handshake redesign, Enable Tx semantics, Wait-and-Reply cooperation, final backend cutover) are gated behind your explicit approval, not bundled into earlier "safe" stages.
5. **Every risky action gets a defined rollback**, not just a forward plan — consistent with how the actual Phase 3 live-testing session was run (isolated install, verified untouched production environment, explicit go/no-go checkpoints).

---

## 1. Track overview

**Track A — WSJT-X protocol/architecture migration** (source: Phases 1–4, Architecture Blueprint)
Moves Jimmy from a hard dependency on Andy WM8Q's fork toward the layered architecture in the Blueprint — Protocol Adapter, optional Compatibility Layer, Classification Engine — with capability negotiation replacing the hard version gate, ending with WSJT-X Improved 3.1 as a fully supported backend.

**Track B — .NET runtime migration** (source: .NET Modernization Report)
Moves Jimmy from .NET Framework 4.7.2 to .NET 10 LTS, keeping WinForms unchanged, self-contained deployment, WiX rebuilt around it.

**Are they related enough to combine? No — evaluated explicitly, per your instruction.** Track A is pure C# application-logic restructuring; Track B is a project-file-format and runtime change. Neither requires the other. The only interaction worth naming: doing Track A's riskiest work (handshake redesign, Enable Tx semantics) while Track B's runtime port is *also* mid-flight would mean any bug found during live Tx testing has two possible causes (the protocol refactor or the runtime port) instead of one, which is a real, avoidable risk with no offsetting benefit. **Recommendation: let Track B reach a stable, released state before Track A's highest-risk stages (A7–A9) begin**, so the riskiest work is validated against exactly one moving part, not two. Everything else in both tracks can proceed independently and in parallel.

---

## Track A — WSJT-X protocol/architecture migration

### Stage A1 — Classification Engine (new-call/new-country/Country/Continent)

- **Goal**: Compute what `EnqueueDecodeMessage`'s wire fields currently give Jimmy (`IsNewCallOnBand`, `IsNewCallAnyBand`, `IsNewCountry`, `IsNewCountryOnBand`, `Country`, `Continent`) from Jimmy's own `LogbookEngine`/`LookupProviders`, running in parallel with the existing wire path — validate only, no cutover yet.
- **Exact scope**: New `ClassificationEngine` module. Reads `LogbookDb` (worked-before) and `LookupManager`/`ClubLogProvider` (country/continent) — both already do this work today for Awards, per Phase 4 §5. Every classification is logged alongside the wire-supplied value for diffing; nothing downstream changes yet.
- **Dependencies**: None — can start immediately, on either runtime.
- **Files/subsystems affected**: New file(s) only. Read-only access to `Logbook/LogbookDb.cs`, `Lookup/LookupManager.cs`. Zero changes to `WsjtxClient.CallQueue.cs`, `CallQueueRanker.cs`, `WsjtxClient.Display.cs`, `Awards/AwardTagger.cs` in this stage.
- **Must not change**: Any operator-visible behavior. This stage produces no output anyone sees.
- **Main risks**: Low. Worst case is a classification mismatch discovered during validation — that's the stage's entire purpose, not a failure.
- **Required tests**: Unit tests comparing `ClassificationEngine` output against known values from historical `JimmyReplay.py` captures (real decode sequences already on hand). No live testing needed.
- **Completion criteria**: Classification matches wire-supplied values across a representative set of real replay captures, with any discrepancies explained (e.g., a multi-prefix-country edge case already known from the ClubLog data-completeness gap noted in project memory) rather than silently accepted.
- **Ships independently**: Yes — this stage is invisible to the operator and can land at any time.
- **Versioning milestone**: Internal only, no version bump needed (no behavior change).
- **Rollback plan**: Delete the new module. Nothing else references it yet.

### Stage A2 — Geo-math module (Azimuth/Distance)

- **Goal**: Compute Azimuth/Distance from Maidenhead grids, the one piece of `EnqueueDecodeMessage` replacement that is genuinely new code (confirmed via Phase 4 §5 — zero existing great-circle/bearing math anywhere in Jimmy today).
- **Exact scope**: Standard, well-documented Maidenhead-grid → lat/lon → great-circle bearing/distance formulas. Consumes Jimmy's own grid (already known) and the DE station's grid (already available via `LookupManager`/QRZ in most cases, `grid.dat`-derived data as fallback, per existing `UsGridStateMap.cs`-adjacent patterns).
- **Dependencies**: None (independent of A1, can run in parallel with it).
- **Files/subsystems affected**: New file(s) only, same parallel-validation pattern as A1.
- **Must not change**: `CallQueueRanker.cs`'s beam-heading/distance-sort ranking math itself — only its *future* input source changes, not in this stage.
- **Main risks**: Low-medium — this is the one item in Track A's early stages that's genuinely new arithmetic rather than rewiring existing data, so it needs more careful validation than A1.
- **Required tests**: Unit tests against known grid-pair distance/azimuth values (verifiable against public great-circle calculators), plus the same replay-capture diffing pattern as A1.
- **Completion criteria**: Computed Azimuth/Distance matches wire-supplied values within a small, defined tolerance across real replay captures.
- **Ships independently**: Yes.
- **Versioning milestone**: Internal only.
- **Rollback plan**: Delete the new module.

### Stage A3 — Protocol Adapter extraction (internal refactor, zero wire-format change)

- **Goal**: Restructure `WsjtxClient.Protocol.cs`'s parsing/building logic behind a clean internal boundary (`WsjtxProtocolAdapter`, per the Blueprint) that speaks standard messages only (Heartbeat, Status, Decode, Clear, Reply, QSOLogged, Close, HaltTx, FreeText, Configure, LoggedADIF, SwitchConfiguration) — a pure code-organization change, not a behavior change. Non-standard traffic still flows, just not yet behind the new boundary.
- **Exact scope**: Move parsing/building code into the new module; existing call sites redirect to it. No message content, timing, or protocol behavior changes.
- **Dependencies**: None functionally, but easiest to do once A1/A2 exist so the eventual cutover (A6) has somewhere to plug in.
- **Files/subsystems affected**: `WsjtxClient.Protocol.cs` (large edit), `WsjtxClient.cs` (call-site updates), `Messages/Out/*.cs` (used as-is, not modified).
- **Must not change**: Wire format, message content, `CallQueueRanker`, `AwardEngine`, `LogbookEngine`, `LookupProviders`, `AccessibilityLayer` — none of these should need to change at all in this stage.
- **Main risks**: Medium — large mechanical surface area (this is the biggest file in the app), so regression risk is about *breaking something in the move*, not about the new design being wrong.
- **Required tests**: Full existing unit suite + full `JimmyReplay.py` scenario suite must pass unchanged, byte-for-byte, before and after. No live testing needed — this is exactly what the replay harness exists for.
- **Completion criteria**: Clean `/t:Rebuild`, 100% of existing tests pass, no behavior change detectable in replay scenarios.
- **Ships independently**: Yes — invisible to the operator.
- **Versioning milestone**: Internal only, though worth its own changelog entry given the size of the change (for future debugging reference, not user-facing).
- **Rollback plan**: This is a large refactor — recommend doing it on a dedicated branch/worktree with the ability to revert wholesale if regression tests fail, rather than incremental commits to the working tree.

### Stage A4 — Compatibility Layer extraction

- **Goal**: Move every `NewTxMsgIdx` sub-command (all 18, all ~29 call sites, per Phase 4 §2) behind an isolated, optional `WsjtxCompatibilityExtension` module, each with the fallback behavior defined in Phase 4's disposition table and the Blueprint §21 placement table.
- **Exact scope**: Per Phase 4/Blueprint's disposition table — the Tier 1 items (sub-commands 5, 16, 255, `MetricUnits`, `DblClk`) are **removed entirely** in this stage, not just relocated, since they're already redundant with things Jimmy owns independently. Everything else moves into the new module with an explicit "unavailable" fallback path.
- **Dependencies**: A3 (needs the Adapter boundary to extract *from*).
- **Files/subsystems affected**: `WsjtxClient.cs`, `WsjtxClient.Protocol.cs`, `WsjtxClient.BandAudio.cs`, `Messages/In/EnableTxMessage.cs` usage sites, `Messages/Out/StatusMessage.cs` extended-field consumers.
- **Must not change**: Anything downstream of these commands that already has a defined behavior — e.g., `HandleUnsolicitedTxResume()`'s *logic* doesn't change, only where its trigger signal comes from.
- **Main risks**: Medium-high — this is where the Tier 1 removals happen, which is the first *actual behavior change* in Track A (removing the LoTW-upload-trigger sub-command since Jimmy's own `LoTWQsoClient` already covers it, etc.). Each removal needs its own confirmation that the replacement path is genuinely equivalent, not just theoretically so.
- **Required tests**: Unit tests for each fallback path (both "Compatibility Layer present" and "absent" cases — a new testing discipline per the Blueprint §20, since today's codebase has no "what if the fork isn't there" tests at all). Replay suite extended with an "Compatibility Layer absent" simulated scenario set.
- **Completion criteria**: Every one of the 18 sub-commands has a defined home (Jimmy-side removal, Compatibility Layer, or confirmed-redundant deletion) and a passing test for its fallback behavior.
- **Ships independently**: Mostly — the Tier 1 removals are a real behavior change and should get their own changelog entry even though they're low-risk.
- **Versioning milestone**: A minor version bump is reasonable here (first user-visible-in-changelog step, even if invisible in practice).
- **Rollback plan**: Each Tier 1 removal should be its own isolated commit/change so any single one can be reverted independently if a redundancy assumption turns out wrong in practice.

### Stage A5 — Capability negotiation (replaces the hard version gate)

- **Goal**: Replace `acceptableWsjtxVersions`'s hardcoded string allowlist and blocking dialog with `CapabilityNegotiator` — a runtime probe for whether the Compatibility Layer is present, with graceful degradation instead of refusal to run.
- **Exact scope**: New negotiation step after standard Heartbeat completes (per Blueprint §19). Replaces `WsjtxClient.Protocol.cs:374-385`'s blocking `MessageBox.Show` + `acceptableWsjtxVersions.Contains()` check.
- **Dependencies**: A3, A4.
- **Files/subsystems affected**: `WsjtxClient.cs` (`acceptableWsjtxVersions` removed), `WsjtxClient.Protocol.cs` (handshake logic, though *not* yet the `cmdCheck` mechanism itself — that's A7).
- **Must not change**: Schema negotiation (already standard-protocol, keep as-is per Blueprint §18) — capability negotiation and schema negotiation stay two separate concerns.
- **Main risks**: Medium — this is the first point where Jimmy can connect to a WSJT-X build it's never been tested against (stock, or Improved without the Compatibility Layer). Behavior in that state needs to be genuinely graceful, not just "doesn't crash."
- **Required tests**: Unit tests for the negotiation state machine (Disconnected → Negotiating → CapabilityProbing → Connected/ConnectedFull). One live test recommended here: connect against the WSJT-X 3.1 test build from Phase 3 (already installed at `C:\claude\research\wsjtx_vi_install\`, known-safe isolated setup) and confirm Jimmy reaches `Connected` (degraded) rather than refusing to start.
- **Completion criteria**: Jimmy connects successfully (degraded mode) to a WSJT-X Improved 3.1 build with no Compatibility Layer, and the Accessibility Layer announces the degraded state clearly.
- **Ships independently**: Yes.
- **Versioning milestone**: Minor version bump — this is a real, user-facing capability change (Jimmy can now at least attempt connecting to builds it couldn't before).
- **Rollback plan**: Keep the old version-gate code path available behind a feature flag for one release cycle in case capability negotiation has an unforeseen edge case in the field.

### Stage A6 — Cutover Queue/Display to Classification Engine output

- **Goal**: Switch `CallQueueRanker.cs` and the admission-gate logic in `WsjtxClient.CallQueue.cs`/`WsjtxClient.Display.cs` from reading `EnqueueDecodeMessage` fields directly to reading `ClassificationEngine`'s `ClassifiedCall` output — now safe, since A1/A2 have been validating parity in the background.
- **Exact scope**: Mechanical type-swap at each of the call sites catalogued in Phase 4 §5 (`CallQueueRanker.cs:147-148,186`; `WsjtxClient.CallQueue.cs:40-486` throughout; `WsjtxClient.Display.cs:220-424`; `Awards/AwardTagger.cs:172`).
- **Dependencies**: A1, A2 (must have weeks/sessions of validated parity data, not just a clean first test run), A3.
- **Files/subsystems affected**: `CallQueueRanker.cs`, `WsjtxClient.CallQueue.cs`, `WsjtxClient.Display.cs`, `Awards/AwardTagger.cs` — but **not** `Awards/RuleEngine.cs`/`RuleDefinition.cs`/`AwardMatcher.cs` (these stay untouched, per Blueprint §12 and the explicit "never touch" list).
- **Must not change**: The ranking algorithm itself (tiers, weights, sort methods, LoTW-boost tiebreak) — only its input source changes. This is the step where it would be easiest to accidentally "improve" the ranking logic while touching it; resist that.
- **Main risks**: Medium-high — this is Jimmy's single most operator-critical subsystem (per the project's own standing rule, "never knowingly lose a valid FT8/FT4 station or QSO opportunity"). A subtle classification mismatch here has real operational consequences, not just a display glitch.
- **Required tests**: Full replay suite must pass with **identical** ranking output before/after (not just "no crash" — actual queue-order equivalence). This is the stage where A1/A2's parity-validation data pays off — if it's clean, this cutover should be low-drama.
- **Completion criteria**: Every replay scenario produces byte-identical ranked-queue output whether sourced from wire fields or `ClassificationEngine` — proven, not assumed.
- **Ships independently**: Yes, but only after the parity bar above is met — this is not a "ship and see" stage.
- **Versioning milestone**: Patch/minor version — internally significant, operator-invisible if done correctly.
- **Rollback plan**: Keep the old wire-field path compiled but dormant (feature-flagged) for at least one release, in case a real-world edge case surfaces that replay captures didn't cover.

### Stage A7 — Handshake/acknowledgment redesign — HIGH RISK, gated

- **Goal**: Replace the `cmdCheck`/`Check`-echo confirmation loop (Phase 4 §3 — the single most foundational Andy-fork dependency found) with the Blueprint's "first natural Status broadcast = connected" design, removing Jimmy's dependency on non-standard command acknowledgment entirely.
- **Exact scope**: Redesign `WsjtxClient.Protocol.cs:354-469`'s handshake sequence. Remove reliance on sub-command 7 ("Ack Req") as a confirmation mechanism; the startup band/mode-sync steps (currently sub-commands 21 and 15) move to the Compatibility Layer as optional, not load-bearing, steps.
- **Dependencies**: A3, A4, A5, A6 all stable and shipped/validated first — this is explicitly sequenced last among Track A's structural stages because Phase 4 identified it as the highest-blast-radius change (touches literally every command Jimmy sends).
- **Files/subsystems affected**: `WsjtxClient.Protocol.cs` (handshake), `RuntimeStateModel` (provenance tracking, per Blueprint §9).
- **Must not change**: Nothing about ranking/awards/logbook/accessibility should be touched in this stage at all — if a change here seems to require touching those, stop and reconsider the design rather than expanding scope.
- **Main risks**: **Highest in Track A's structural work.** If the new "implicit ack via next Status" design doesn't actually behave equivalently in practice, every downstream feature is affected simultaneously, not just one subsystem.
- **Required tests**: Full unit + replay suite, **plus** a narrowly-scoped live test against real WSJT-X Improved 3.1 (isolated install, per the safety protocol already proven in the Phase 3 session) confirming the new handshake reaches a working `Connected` state reliably across multiple connect/disconnect cycles — this specific behavior cannot be fully validated by replay alone since it's inherently about real-time message ordering.
- **Completion criteria**: Ten consecutive clean connect cycles against real WSJT-X Improved 3.1 with no handshake failure, timeout, or degraded-state misclassification.
- **Ships independently**: No — recommend this ships together with whatever Track A stage follows it, since a handshake-only release with nothing else changed downstream offers little value and maximizes exposure for minimal benefit.
- **Versioning milestone**: Minor version bump, called out explicitly in release notes as a structural change, with a documented rollback path clearly stated for users.
- **Rollback plan**: This is the one stage in Track A where a same-day rollback capability (previous release still installable, no data migration lock-in) should be explicitly verified before release, not assumed.
- **Decision gate**: **Requires your explicit approval before starting**, per your own stated risk philosophy throughout this whole engagement.

### Stage A8 — Live Tx-path resolution — HIGHEST RISK, gated, narrowly scoped

- **Goal**: Resolve every remaining Tier 4/5/"cannot yet be determined" item from Phase 4 that genuinely requires live WSJT-X + real (or controlled) radio/audio state to validate — the exact set Phase 3's session began but could not finish.
- **Exact scope, item by item, each independently testable and independently gate-able:**
  - **Halt Tx** (sub-cmd 12 vs. standard `HaltTx`): **lowest risk in this stage** — halting can only stop transmission, never start it. Live test: confirm standard `HaltTx` produces the same effect as the current sub-command.
  - **Tx watchdog reset** (sub-cmd 13): moderate risk, no Tx involved — live test is simply "stay connected without sending resets, observe whether WSJT-X's own watchdog fires unexpectedly."
  - **RR73/skip-grid/Tx-offset toggle** (sub-cmd 10): moderate risk — can be validated via Settings-dialog visual confirmation (as already proven possible in the Phase 3 session) without ever arming Tx.
  - **Band/frequency change** (sub-cmd 15): requires the open design decision from Blueprint §22 (Compatibility Layer vs. rigctld) to be made first; live test needed either way, no Tx involved.
  - **Mode switch FT8⇄FT4** (sub-cmd 21): similar risk profile to band change, no Tx involved.
  - **Enable Tx, generic arm** (sub-cmd 9): **highest risk item in the entire roadmap.** Requires the exact safety protocol already established and proven in the Phase 3 live-testing session: isolated WSJT-X profile, `Radio=None` (no real CAT/PTT), explicit confirmation of no-VOX risk before every session, real audio devices only with your direct, in-the-moment authorization, and — per the Phase 3 session's own outcome — the safety system's judgment about what's authorized in the moment takes precedence over this document's plan if there's any conflict.
  - **Wait-and-Reply cooperation** (`TxEnableClk`/`TxHaltClk`): the accessibility-critical item — must resolve whether a standard/Improved-native substitute exists before concluding this needs the Compatibility Layer permanently. This is the one item where "we couldn't test it" is not an acceptable final state, since it's what tells a blind operator their radio started transmitting on its own.
  - **QsoProgress**: likely resolvable via Jimmy's own state tracking for anything Jimmy itself initiated (no live test needed) — live test only needed for the "WSJT-X changed this on its own" detection case, same session as Wait-and-Reply.
- **Dependencies**: A7 complete and stable.
- **Files/subsystems affected**: `WsjtxCompatibilityExtension`, `WsjtxClient.cs` (Enable Tx/Halt Tx call sites), `WsjtxClient.BandAudio.cs`.
- **Must not change**: Nothing else in the app. This stage is scoped as narrowly as possible by design.
- **Main risks**: Real-world radio transmission risk (Enable Tx item specifically), and the accessibility regression risk of silently losing Wait-and-Reply cooperation without realizing it.
- **Required tests**: Each sub-item above gets its own session, its own go/no-go checkpoint, and its own explicit sign-off — this stage should never be treated as one atomic block of work.
- **Completion criteria**: Every sub-item above has a confirmed disposition (works via standard protocol / needs Compatibility Layer / genuinely unavailable, with an accepted operator-facing tradeoff) — not "attempted," confirmed.
- **Ships independently**: No — bundle with A9.
- **Versioning milestone**: Feeds directly into A9's release.
- **Rollback plan**: Per sub-item — since each is independently gated, each can be independently deferred to a later release without blocking the others.
- **Decision gate**: **Requires your explicit approval before each individual sub-item begins**, not just once for the whole stage — this matches how the Phase 3 session actually had to work in practice (you were asked and confirmed "no VOX" before Enable-Tx testing specifically, separately from the general go-ahead).

### Stage A9 — Final backend cutover

- **Goal**: WSJT-X Improved 3.1 (and its ongoing releases) becomes a fully supported Jimmy backend, documented and released as such.
- **Exact scope**: Update `README.md`'s "Accepted WSJT-X builds" section, update `CapabilityNegotiator`'s expectations based on A8's findings, finalize whether Andy's fork remains supported in parallel (open question — see §8).
- **Dependencies**: A7, A8 complete.
- **Files/subsystems affected**: Documentation, version-gating configuration only — no application logic changes in this stage.
- **Must not change**: N/A — this is a documentation/release stage.
- **Main risks**: Low — by this point the technical work is done; the risk is purely in communicating the change clearly to existing users.
- **Required tests**: Full end-to-end operator scenario walkthrough against real WSJT-X Improved 3.1, matching your project's existing "run parser/replay tests after protocol changes" rule one final time.
- **Completion criteria**: README/install docs accurately describe the new capability-based compatibility story; a real operating session (your own, when ready) confirms normal FT8/FT4 operation feels identical to today.
- **Ships independently**: This is the release.
- **Versioning milestone**: Major or minor version bump, per your existing release-versioning process (`RELEASE_STEPS.txt`) — this is a genuine milestone release, worth treating as such in the changelog.
- **Rollback plan**: Previous release (still on Andy's fork) remains installable; this is the standard MSI major-upgrade rollback story, unchanged by this migration.

---

## Track B — .NET runtime migration

*(Full detail already in `Jimmy_DotNet_Modernization_Report.md` §13; restated here in the same per-stage format for consistency with Track A, so this document is genuinely self-contained.)*

### Stage B1 — SQLite/package validation spike

- **Goal**: Resolve the one open empirical question from the .NET report — does `System.Data.SQLite.Core`'s NetStandard stub bundle the native x64 interop DLL the same zero-install way the NetFramework stub does.
- **Exact scope**: Throwaway `net10.0-windows` WinForms project, add `System.Data.SQLite.Core` + `MQTTnet` 4.3.7.1207, confirm `SQLite.Interop.dll` lands in output, confirm a basic read/write round-trips.
- **Dependencies**: None.
- **Files/subsystems affected**: None in Jimmy itself — throwaway project only.
- **Must not change**: Nothing in the real codebase yet.
- **Main risks**: Low.
- **Required tests**: The spike's own success/failure is the test.
- **Completion criteria**: Native interop DLL confirmed present and functional.
- **Ships independently**: N/A (not shipped).
- **Versioning milestone**: None.
- **Rollback plan**: N/A — discard the spike project either way.

### Stage B2 — Convert `JimmyTests.csproj`

- **Goal**: Prove the SDK-style + PackageReference conversion pattern on the smaller, lower-stakes project first.
- **Exact scope**: SDK-style conversion, `net10.0-windows`, PackageReference for SQLite. Its reference to `Jimmy.exe`'s compiled output can stay a raw HintPath-style reference or become a `ProjectReference` (latter is more idiomatic, not required).
- **Dependencies**: B1.
- **Files/subsystems affected**: `JimmyTests\JimmyTests.csproj` only.
- **Must not change**: Test logic/assertions themselves.
- **Main risks**: Low — small project, easily revertible.
- **Required tests**: The existing 300+ test suite must still compile and pass against a `net472`-built `Jimmy.exe` at this stage (Jimmy itself hasn't converted yet).
- **Completion criteria**: `JimmyTests` builds and runs clean under the new format.
- **Ships independently**: N/A (internal tooling).
- **Versioning milestone**: None.
- **Rollback plan**: Revert the one project file.

### Stage B3 — Convert `Jimmy.csproj`

- **Goal**: The actual runtime migration — SDK-style, `net10.0-windows`, `UseWindowsForms=true`, PackageReference format.
- **Exact scope**: Per the .NET report §2/§5: add `Microsoft.Win32.Registry`, `System.Security.Cryptography.ProtectedData`, `System.Configuration.ConfigurationManager` packages; remove `App.config`'s `<startup>` element; verify the Club Log key-embedding `RoslynCodeTaskFactory` inline task still works under SDK-style MSBuild.
- **Dependencies**: B1, B2.
- **Files/subsystems affected**: `Jimmy.csproj` (full rewrite), `App.config` (trimmed), `packages.config` (removed, replaced by PackageReference entries in the csproj itself).
- **Must not change**: Any `.cs` file's logic — this stage is project-file-format and package-reference changes only. If a `.cs` file needs an actual code change to compile, that's a signal worth pausing on, not pushing through silently.
- **Main risks**: Medium — largest mechanical stage in Track B, compile errors are expected and are the *intended* way to discover every place needing a new package reference (Registry, DPAPI, Configuration), per the report's own recommendation.
- **Required tests**: Clean `/t:Rebuild` with zero warnings, matching your project's existing build-quality bar.
- **Completion criteria**: Jimmy compiles and launches under `net10.0-windows`.
- **Ships independently**: No — needs B4/B5 first.
- **Versioning milestone**: Feeds into the eventual Track B release.
- **Rollback plan**: Keep the .NET Framework 4.7.2 version of `Jimmy.csproj` on a separate branch until B4/B5 are both clean — do not delete the old project format until the new one is fully validated.

### Stage B4 — Full regression pass on .NET 10

- **Goal**: Confirm nothing structural broke.
- **Exact scope**: Run the full existing unit test suite (300+ tests) and `JimmyReplay.py` scenario suite against the .NET 10 build.
- **Dependencies**: B3.
- **Files/subsystems affected**: None directly — this is a verification stage; any fixes it surfaces go back into B3's scope.
- **Must not change**: N/A.
- **Main risks**: Low if B3 was done cleanly (project-file-only changes); any actual logic differences surfacing here are exactly what this stage exists to catch.
- **Required tests**: The suites themselves.
- **Completion criteria**: 100% pass, matching pre-migration results exactly.
- **Ships independently**: N/A.
- **Versioning milestone**: None.
- **Rollback plan**: N/A — this is a gate, not a shippable unit.

### Stage B5 — Accessibility verification (JAWS/NVDA)

- **Goal**: Resolve the one real flagged risk in the .NET report — confirm `SendKeys.Send("{UP}")`'s re-announce behavior still works reliably on modern .NET, given documented upstream regressions in this exact area (dotnet/winforms #6666, #7945, #2660, #14145).
- **Exact scope**: A real JAWS session and a real NVDA session, exercising every one of the 10+ `SendKeys.Send("{UP}")` call sites (`Controller.cs`, `WsjtxClient.cs`) — status announcements, list updates, hotkey feedback. Also re-verify the `AllocConsole`/`SetForegroundWindow` focus-stealing interaction in Debug builds (previously a real, fixed bug per project memory — worth re-confirming the fix holds on the new runtime, not assuming it).
- **Dependencies**: B3, B4.
- **Files/subsystems affected**: None expected — this is verification, not development. If a real regression is found, it becomes new scope back in B3.
- **Must not change**: N/A.
- **Main risks**: Medium — this is the one item in Track B with documented upstream evidence of a real behavioral difference, not just a theoretical migration risk.
- **Required tests**: Manual, deliberate, with both screen readers, not just one — the .NET report explicitly found no JAWS-specific data either direction, only NVDA/Narrator-focused GitHub issues.
- **Completion criteria**: Every re-announce point confirmed working with both JAWS and NVDA, with timing that feels equivalent to the current .NET Framework build (your own judgment call, since this is fundamentally a subjective "does this still work well" question only you can answer).
- **Ships independently**: No — this is a release gate for all of Track B.
- **Versioning milestone**: None on its own; gates the Track B release.
- **Rollback plan**: If a genuine regression is found, Track B's release is held until it's resolved — this is not a "ship and monitor" item given the accessibility-critical nature of the app.
- **Decision gate**: **You are the required verifier here** — this can't be delegated or assumed.

### Stage B6 — Self-contained publish trial + size measurement

- **Goal**: Replace the ~150MB community-reported estimate with a real, measured number for Jimmy specifically.
- **Exact scope**: `dotnet publish` self-contained win-x64, measure actual output folder size.
- **Dependencies**: B3.
- **Files/subsystems affected**: None (publish configuration only).
- **Must not change**: N/A.
- **Main risks**: Low.
- **Required tests**: The measurement itself.
- **Completion criteria**: Real installed-size number known and recorded, replacing the estimate in the .NET report.
- **Ships independently**: N/A.
- **Versioning milestone**: None.
- **Rollback plan**: N/A.

### Stage B7 — WiX/MSI rebuild

- **Goal**: Rebuild `Setup_WiX\Jimmy.wxs` around WiX v5 directory harvesting and self-contained packaging, per the .NET report §4.
- **Exact scope**: Move off hand-listed files (impractical for hundreds of runtime DLLs) to `<Files Include="glob">` harvesting or `dotnet publish` + `.wixproj` auto-harvest integration. Remove the `NETFX472RELEASE` launch-condition check (no longer needed — self-contained has no prerequisite). Verify no duplicate directory entries in the built MSI's file table (a known WiX v5 gotcha, wixtoolset/issues#8608).
- **Dependencies**: B3, B6.
- **Files/subsystems affected**: `Setup_WiX\Jimmy.wxs` (largest single restructuring in Track B), `Setup_WiX\*.wixproj` if introduced.
- **Must not change**: `UpgradeCode` (`{D5415907-DD93-4188-85A8-F15A73F949C2}`) — must stay exactly as-is for major-upgrade continuity, per the .NET report §9.
- **Main risks**: Medium-high — largest surface-area change in Track B, and MSI packaging bugs are the kind that only surface on a real install, not in a build log.
- **Required tests**: Build the MSI, inspect its file table directly (per your existing release checklist habit), install on a clean VM/machine if available.
- **Completion criteria**: MSI installs cleanly, Jimmy launches and operates normally post-install, no missing-file errors.
- **Ships independently**: No — feeds into B8.
- **Versioning milestone**: Feeds the Track B release version.
- **Rollback plan**: Keep the old `.wxs` file available; if the new harvesting approach has problems, the explicit-file-list approach still works for a framework-dependent fallback build if ever needed.

### Stage B8 — End-to-end MSI upgrade test

- **Goal**: Confirm the full upgrade story from a real current-production Jimmy install.
- **Exact scope**: Install the new self-contained, .NET 10, MSI over a real existing Jimmy installation (or a faithful copy of one). Specifically verify the two continuity questions the .NET report flags as needing explicit confirmation, not assumption: DPAPI-encrypted credentials (QRZ/Club Log/LoTW passwords) still decrypt correctly, and `Properties.Settings.Default` values (window position, IP/port, checkboxes) survive rather than silently resetting.
- **Dependencies**: B7.
- **Files/subsystems affected**: None — this is a test, not development.
- **Must not change**: N/A.
- **Main risks**: Medium — credential loss on upgrade would be a genuinely bad user experience, silent and easy to miss if not explicitly tested.
- **Required tests**: The upgrade itself, plus your existing release checklist's `MajorUpgrade` verification step.
- **Completion criteria**: Upgrade completes cleanly; credentials, settings, logbook data, and Rule Definitions all present and correct post-upgrade.
- **Ships independently**: This is the release.
- **Versioning milestone**: Follow your existing `RELEASE_STEPS.txt` process (AssemblyFileVersion, AssemblyInformationalVersion, WiX ProductVersion all incremented, Release rebuild, MSI verification, SHA-256 check).
- **Rollback plan**: Standard MSI major-upgrade rollback (previous version's installer remains available) — no data-migration lock-in introduced by this change.

---

## 2. Combined testing strategy, mapped to stage

| Test type | Stages it covers | Notes |
|---|---|---|
| Unit tests | A1, A2, A4, A5, B4 | Existing 300+ suite is the backbone; extended in A4 with "Compatibility Layer absent" cases |
| Replay tests (`JimmyReplay.py`) | A1, A2, A3, A6, A7, B4 | The primary tool for validating Track A's structural work with zero live-radio involvement |
| Protocol-level tests | A5, A7 | Live connection-cycle testing against the Phase 3 isolated WSJT-X 3.1 build, no Tx involved |
| Integration tests | A6, A8 | Full pipeline (decode → classify → rank → display) exercised together |
| Installer tests | B7, B8 | MSI file-table inspection, clean-install verification |
| Upgrade tests | A9, B8 | Real-world upgrade-from-production scenarios |
| On-air/live-Tx tests | A8 only | Narrowly scoped, individually gated, per sub-item — never a default validation method |
| Accessibility (JAWS/NVDA) tests | B5, and a final pass in A9 | The one test category that fundamentally requires you personally, not automatable |

---

## 3. Documentation and release changes

- **Track A's A9**: `README.md` "Accepted WSJT-X builds" section rewritten around capability negotiation instead of a fixed version allowlist; a migration note for existing users about what changes (and what doesn't) if they move to WSJT-X Improved 3.1.
- **Track B's B8**: `RELEASE_STEPS.txt` followed as-is (it's runtime-agnostic already); installer-size change called out explicitly in release notes given the framework-dependent → self-contained tradeoff (§8 of the .NET report).
- **Both tracks**: neither should be a silent release. Given Jimmy's user base is blind/screen-reader-dependent operators who may not casually read visual release notes, consider whether release announcements need an accessible-format channel (this is a judgment call for you, not something this roadmap should decide).

---

## 4. Recommended implementation order

1. **B1 → B2 → B3 → B4** (Track B's safe structural work) — start here; addresses a real forcing function (.NET 8/9 EOL Nov 2026) with low risk.
2. **A1 and A2 in parallel** (Track A's safe structural work) — can start immediately, independent of Track B, on whichever runtime is current at the time.
3. **B5** (accessibility verification) — gates Track B's release; do this before B6/B7/B8 so a regression here doesn't waste packaging work.
4. **B6 → B7 → B8** — Track B ships.
5. **A3 → A4 → A5 → A6** — Track A's remaining structural work, on whichever runtime is current in production by this point (recommend .NET 10, i.e., after Track B has shipped, so this work isn't duplicated).
6. **Decision gate, then A7.**
7. **Decision gate per sub-item, then A8.**
8. **A9** — Track A ships.

## 5. First concrete task to begin

**Stage B1** (SQLite/package validation spike) — smallest, lowest-risk, fastest to complete, and resolves a real open question before any other work depends on the answer. Alternatively, **A1** (Classification Engine) can start the same day in parallel, since the two have zero dependency on each other.

## 6. Major decision gates requiring your approval

- Before **A7** begins (handshake redesign).
- Before **each individual sub-item** of **A8** begins (Enable Tx especially — per-session authorization, not a one-time blanket approval, matching how the Phase 3 session actually had to work).
- The **Blueprint §22 open decisions** (band/frequency-change routing: Compatibility Layer vs. rigctld; whether rare settings like power/SWR/tuning/PSKReporter-toggle stay remote-controllable or become "use WSJT-X's own GUI") — needed before A8 can fully scope itself.
- Whether Jimmy continues supporting Andy's fork in parallel indefinitely, or Track A eventually deprecates it — needed before **A9**.
- **B5's accessibility sign-off** — inherently yours alone to give.

## 7. What can run in parallel

- B1/B2/B3/B4 (Track B structural work) and A1/A2 (Track A's earliest, fully independent stages) — genuinely parallel, no shared files.
- Within Track A, A1 and A2 are independent of each other.
- A3 depends on nothing external but is easiest sequenced after A1/A2 exist (not required, just convenient).
- Nothing in A7/A8/A9 should run in parallel with anything else — by design, these are sequential, gated, and singular in focus.

## 8. Estimated relative effort

| Stage | Effort |
|---|---|
| A1 — Classification Engine | Medium |
| A2 — Geo-math module | Small–Medium |
| A3 — Protocol Adapter extraction | Large |
| A4 — Compatibility Layer extraction | Large |
| A5 — Capability negotiation | Medium |
| A6 — Queue/Display cutover | Medium |
| A7 — Handshake redesign | Large |
| A8 — Live Tx-path resolution | Very Large |
| A9 — Final cutover | Small–Medium |
| B1 — SQLite spike | Small |
| B2 — JimmyTests conversion | Small |
| B3 — Jimmy.csproj conversion | Medium–Large |
| B4 — Regression pass | Small (Medium if fixes needed) |
| B5 — Accessibility verification | Medium |
| B6 — Self-contained publish trial | Small |
| B7 — WiX/MSI rebuild | Large |
| B8 — Upgrade test | Small–Medium |

## 9. What the finished migration delivers to users

An operator installs Jimmy and gets: the exact same ranking, awards, logbook sync, lookups, and accessible keyboard/screen-reader operation they have today — unchanged, not reimagined — now running on a modern, long-supported .NET runtime (no more racing an EOL deadline), self-contained (no separate .NET Framework install step), and able to connect to WSJT-X Improved 3.1 and its ongoing releases directly, with a small, clearly-documented, independently-versioned compatibility layer covering whatever genuinely can't be done through the standard protocol — instead of being pinned to one specific, narrowly-versioned third-party WSJT-X fork as a hard requirement.
