# Jimmy vs. WSJT-X 3.1 Accessible Build — Phase 3: Forensic and Behavioral Investigation

Status: research only — no code changed. This phase supersedes parts of Phase 1 and Phase 2 based on direct forensic evidence from the actual executable at `C:\claude\research\wsjtx-3.1.0-win64_for_visually_impaired_operators.exe`. Where a conclusion changed, it's called out explicitly below rather than silently revised.

**Safety note on method:** all testing was done against an isolated install (`C:\claude\research\wsjtx_vi_install\`) using WSJT-X's own `--rig-name`/`--test-mode` multi-instance flags, which write to entirely separate config paths (`%LocalAppData%\WSJT-X - ClaudeResearchTest*\`, distinct from the real `%LocalAppData%\WSJT-X\`). Your real WSJT-X (`C:\WSJT\wsjtx`) and Jimmy install were confirmed untouched by hash/timestamp before and after (`wsjtx.exe` SHA256 `3FC53131...` unchanged, real `WSJT-X.ini` timestamp unchanged at `6:21:13 PM`, predating any test activity). One process-management mistake happened mid-session (a stray instance launched without the isolation flag) and was killed within about a minute, before it could write anything — verified via timestamp, not just assumed.

---

## 1. Forensic checklist — exactly what this executable is

| Item | Finding |
|---|---|
| Product name (installer) | `wsjtx` |
| Product name (installed binary) | `WSJT-X-improved` |
| Product version | `v3.1.0` (installer) / `3.1.0` (binary) |
| File version | `3.1.0.0` raw |
| Build date | Installer `LastWriteTime`: **June 26, 2026, 7:38:50 PM** (see §3 — this postdates every publicly known official build) |
| Publisher | **Uwe Risse, DG2YCB** (CompanyName field, both installer and installed `wsjtx.exe`) |
| Copyright string | "Copyright (C) 2001-2026 by Joe Taylor, K1JT and 2020-2026 by Uwe Risse, DG2YCB" |
| Digital signature | **Not signed** (Authenticode status: NotSigned) |
| Installer technology | **NSIS** (Nullsoft Scriptable Install System) — confirmed via embedded strings |
| Architecture | Installer stub: x86 (32-bit, typical NSIS wrapper behavior). Installed payload: win64 per filename/directory contents (Qt5 x64 DLLs, standard 64-bit WSJT-X toolset) |
| Installer file hash | SHA256 `1AD3EE565520F745D4C0554CDAFDD8C1C66178550F95AFD6862F070A2D0CC705`; MD5 `7A3853911A951DA899BA685D7146E101`; size 71,122,174 bytes |
| Installed `wsjtx.exe` hash | SHA256 `67487D53164631584FDF22AB0F55F2EF45DF4865F4DAA72BE54F47AFA179C47A` |
| Embedded build-path string | `C:\JTSDK64-Tools\tmp\wsjtx\main.cpp` — built with **JTSDK64-Tools**, the standard community Windows build toolchain for WSJT-X-family projects (not evidence of anything unusual — this is the normal way these projects are built) |
| Bundled files | Full standard WSJT-X directory tree: `bin\` (wsjtx.exe, jt9.exe, wsprd.exe, map65.exe, rigctl-wsjtx.exe/rigctld-wsjtx.exe/rigctlcom-wsjtx.exe, various `*code.exe`/`*sim.exe` test tools, Qt5/boost/hamlib/openssl DLLs), `plugins\` (Qt platform/imageformat/sql/style plugins), `share\wsjtx\` (cty.dat, grid.dat, sat.dat, JPLEPH), `share\doc\wsjtx\` (AUTHORS, BUGS, COPYING, NEWS, README, THANKS, HTML user guide, example log configs) |
| README / documentation | Present, 106,340 bytes — **generic, standard WSJT-X Improved changelog/documentation**, not written for or about visually-impaired users specifically (see §4) |
| License | GPLv3, full text in `COPYING` (3,xxx bytes, standard FSF text) |
| Source-code references / GPL offer | README explicitly points to `https://sourceforge.net/projects/wsjt/` as the project source location and a mailing list; this satisfies GPL's "make source available" intent for the general project, though see §3 for a nuance on whether *this specific build's* one-line customization was ever itself pushed to that public source |
| Configuration files | Standard WSJT-X `.ini` schema (`[MultiSettings]`, `[General]`, `[Configuration]` sections); ships `example_log_configurations\` templates |
| Accessibility-specific files/resources | **None found.** No NVDA/JAWS scripts, no TTS engine, no accessibility-specific DLLs or config. The one directly relevant asset is `bin\sounds\` — 11 language-variant folders of category-named alert WAV files (see §4) — but this is a **standard WSJT-X Improved feature** (multilingual alert voices), present in the mainline product, not something unique to this build |
| QLog-specific files/integration | **None found.** No QLog executable, DLL, config, or protocol hook bundled or referenced anywhere in the installed tree or documentation |

---

## 2. The one truly distinctive, confirmed finding: the branding is compiled into the binary

Running the installed application (isolated instance, station `ClaudeResearchTest`), its window title reads, verbatim:

> **WSJT-X - ClaudeResearchTest - test   v3.1.0  for visually impaired operators**

This was captured directly from the live running process (screenshot), not read from a filename or metadata field. The double-space before "for" suggests the whole phrase `v3.1.0  for visually impaired operators` is one compiled string constant (the same place WSJT-X Improved normally puts just `v3.1.0`) — i.e., **someone with access to the actual source and build toolchain changed this string and recompiled**, rather than a filename rename or a resource-hex-edit of a stock binary. This is real, direct evidence the build is a deliberate, purpose-built variant, not merely a relabeled generic installer — which is exactly the possibility you told me not to assume away, and the forensic evidence bears that out.

**What I could not find anywhere — in the README, NEWS, AUTHORS, GUI, Settings dialog (General/Radio/Audio/Tx Macros/Reporting/Frequencies/Colors/Advanced/Alerts/Filters tabs, all inspected directly via screenshot), or File menu — is any *functional* difference beyond that title string.** No accessibility-specific menu, no screen-reader hook, no different keyboard scheme, no TTS, no QLog integration point. The running GUI is visually and structurally identical to a standard WSJT-X Improved installation.

**Best-supported conclusion (not 100% certain — see caveat below):** this is very likely a straightforward recompilation of WSJT-X Improved's public GPL source, with the sole customization being the window-title/branding string, done as a dedication/goodwill gesture for the QLOGforblindhams community — not a functionally distinct accessibility fork. I want to be precise about confidence here: I verified this from the artifact's internal consistency (matching changelog text, correct file structure, working application, plausible build toolchain) and from the absence of any accessibility-specific code surface anywhere I could inspect — but I was **not able to get a byte-level diff against an official reference build** (see §3), so I cannot rule out other, more substantive private modifications with full certainty. If you want that closed completely, the reliable next step is downloading the official `wsjtx-3.1.0-win64_improved_PLUS_260522.exe` from a normal browser (not blocked by anti-bot protection the way this sandbox's scripted downloads were) and diffing it directly, or asking in QLOGforblindhams what changed.

---

## 3. Origin and provenance — what a second research pass could and couldn't establish

A background research agent attempted to download official reference builds (WSJT-X Improved 3.1.0, stock WSJT-X) for a byte-level hash comparison. **I need to flag something about how that went, honestly:** SourceForge's download infrastructure is behind Cloudflare bot-protection that returns HTTP 403 to scripted clients; the agent's transcript shows it tried multiple mirror URLs and workaround approaches to get past that, which the harness itself flagged as a security concern (external-code-download risk without specific authorization for that workaround pattern). I did not ask for that and I'm not going to repeat it — flagging it to you now rather than treating it as routine, per how the harness surfaced it. No installer bytes were actually obtained by that route; only page-metadata (via a page-summarizing fetch, not raw download) was available.

**What was established (from page metadata, not hashing):**
- Official WSJT-X Improved 3.1.0 build lineage, confirmed from the project's own mailing-list archive and a Debian package repository: **260226** (Feb 26, 2026, beta — now pulled, 404s today) → **260228** (Feb 28, Debian repack) → **260522** (May 22, 2026, the current public release). All official win64 installers list as ~71.1 MB, matching your file's size class.
- Your file's timestamp — **June 26, 2026** — is about a month **after** the newest official public build (260522) with **no public record anywhere** (SourceForge file listing, release-notes text, Debian package archive, mailing list) of any build between May 22 and today. This is consistent with either (a) a private/unlisted build made specifically for this community after the public 260522 release, or (b) a third party's own rebuild from the public 260522 GPL source, done a month later, with the title string added.
- Your exact SHA256/MD5 and the exact filename: **zero hits anywhere on the public web** searched (including a VirusTotal lookup attempt, which didn't return usable data without an API key — so I have no independent malware-scan verdict on this file, positive or negative, beyond my own manual inspection).
- DG2YCB's own SourceForge profile lists his known real projects (WSJT-X Improved, JTDX Improved, ExtIO_RTLTCP_improved, HAMLIB SDK, FlexUSBEmulator) — **no accessibility-branded release listed there.** That doesn't rule out an off-list/private build; it just means there's no public-catalog confirmation.
- A **different, well-documented, genuinely distinct** accessibility-oriented WSJT-X build does exist in this space: avantol/WM8Q's build paired with "Tilly," named `wsjtx-3.0.0-win64_MOD.exe` — version 3.0.0 (not 3.1.0) and a completely different naming convention. This rules out your file being a mislabeled copy of *that* project; it is not.
- Stock (non-Improved) WSJT-X's current release is 53.0 MB — meaningfully smaller than your 71.1 MB file, which rules out "this is just stock WSJT-X relabeled." The size class is unambiguously in the "Improved" family.

**Net assessment:** I have strong, consistent, multi-source evidence this is a genuine WSJT-X-Improved-lineage build with one deliberate, compiled-in branding change — and no positive evidence of tampering or malicious content from my own manual file walk (no unexpected executables, no obfuscated payloads, no credential-harvesting artifacts, no QLog-impersonating files). But I do not have an independent AV scan verdict or a byte-level diff against the official 260522 build, so "provenance fully confirmed" would overstate what I actually verified. Given this ran on your daily-use machine, I'd suggest a routine AV scan of the file as ordinary hygiene — not because anything I found suggests malice, but because I couldn't get the one check (hash reputation lookup) that would close the loop cleanly.

---

## 4. A major upgrade to Phase 2's characterization of WSJT-X Improved's own native features

Reading the bundled README/NEWS in full (something Phase 1/2 did only via external web summaries, not the primary document) surfaced real, concrete built-in automation that Phase 2 underestimated. **This is a genuine correction, not a reversal of the accessibility conclusion** — it changes how "basic" I should have called WSJT-X Improved's own call-selection logic.

- **Wait features**: three distinct modes — Wait and Reply, Wait and Call, Wait and Pounce — with real selection criteria: **CQ: First, CQ: Max Dist, CQ: Max dB, CQ: Min dB**. This is genuine automatic candidate selection from incoming CQs, not just list filtering.
- **"Call Best" button** (FT4, confirmed in NEWS): selects the best reply to a CQ "based on neediness" — a built-in single-click DXCC-need-aware assist.
- **Quick Filters / Ignore List**: three-way classification (**Blacklist, Whitelist, Always Pass**), hide/ignore/highlight by continent, DXCC, worked-before-on-band, worked-today-or-yesterday.
- **Alert categories**: DXCC, DXCC-on-band, CQ Zone, CQ Zone-on-band, ITU Zone, ITU Zone-on-band, Grid, Grid-on-band, Continent, Continent-on-band, US State (new in 3.1), US State-on-band, MyCall, Wanted (callsigns/grids/prefixes/directional calls), Message — each with its own multilingual spoken-word alert WAV, selectable per-language in Settings → Alerts. **This is confirmed to be the same taxonomy your real, live WM8Q-built WSJT-X's `.ini` already has configured** (`alert_DXCC`, `alert_CQZ`, `alert_Grid`, etc.) — meaning your production WSJT-X already has this system active today, independent of anything in this research.
- **SuperFox/Hound scoring API** (confirmed from README): *"Hound callsigns can be highlighted via UDP API, and assigned a score for sorting via UDP API... UDP Status Update messages now include information on how many callsigns have highlighting applied, and how many callsigns have a score assigned."* This is a **standard, public WSJT-X Improved UDP capability** for externally-supplied callsign scoring — conceptually the same purpose as Andy's fork's non-standard `AnnotationInfo` message (Phase 1 §2.2), but native to WSJT-X Improved itself, at least in Fox/Hound (DXpedition) mode. This wasn't testable live this session (see §6) and is worth a dedicated follow-up.

**Revised standing relative to Jimmy:** WSJT-X Improved's own selection/filtering is real and meaningfully more capable than "basic decode-list filtering," but it remains **single-active-criterion** selection (pick one of Max Dist/Max dB/Min dB/First at a time) plus **one hardcoded award** (WAS via a bundled static `callsign_states.tsv` snapshot — not a live-updating, deduped source the way Jimmy's `FccUlsProvider` is), versus Jimmy's multi-tier weighted category ranking integrated with an arbitrary user-defined award rule engine, beam-heading quadrants, and LoTW-boost tiebreak. Jimmy's ranking/award system remains categorically more powerful — the gap is smaller than Phase 2 stated, not closed.

One more concrete correction: Hamlib. This build's `.ini` reports `HamlibBackedUp="Hamlib 4.7.1..."` — it ships and uses **Hamlib 4.7.1**, not 5. The "Hamlib v4/v5 support" claim from earlier web research appears to mean the *option* to use either exists in the codebase, not that this build defaults to or ships Hamlib 5.

---

## 5. Provenance connection to Andy's (WM8Q's) fork — a real revision to Phase 1

This is the most consequential correction in this phase. I compared the actual currently-installed, currently-in-use `wsjtx.exe` (`C:\WSJT\wsjtx\bin\wsjtx.exe`, the real binary Jimmy connects to in production) against this test build, both read-only:

| | Real live WSJT-X (Jimmy's actual backend) | Test build |
|---|---|---|
| ProductName | `WSJT-X` | `WSJT-X-improved` |
| ProductVersion | `3.0.0-rc1 5829e7-dirty` | `3.1.0` |
| CompanyName | **WM8Q** | **Uwe Risse, DG2YCB** |
| SHA256 | `3FC53131B4...` | `67487D5316...` |

**Confirmed different binaries, different branding, different lineage identifiers — Phase 1's finding that these are separate builds stands.** But the real WSJT-X's own `.ini` file already contains distinctive, specific WSJT-X-Improved configuration keys and features not found in stock WSJT-X: `Blacklist1-12`/`Whitelist1-12`/`AlwaysPass`, `SuperFox`, `CloudLog*`, `WaitFeaturesEnabled`, `FiltersForWaitAndPounceOnly`, and the exact same `alert_DXCC`/`alert_CQZ`/`alert_Grid`/etc. taxonomy this test build's sound files implement. These are too specific and numerous to be coincidental independent naming by two unrelated codebases.

**Revised conclusion:** Andy WM8Q's fork very likely **incorporates or is built on top of WSJT-X Improved's source additions**, even though it's a separately branded, separately versioned, separately hashed binary with no formal GitHub fork relationship recorded. Phase 1's "no confirmed code lineage... treat as unconfirmed" should be updated to "real code-level relationship, strong circumstantial evidence, exact mechanism (merge? shared upstream commits? WM8Q pulling specific patches?) not determined." This is good news for a migration: **it means less of WSJT-X Improved's feature set would need to be "gained" by moving off Andy's fork — much of it, per your live config, is already present.** The dependency that would remain is specifically Andy's own protocol-level additions layered on top (message types 16-18, extended Status fields) — not a whole separate feature set.

---

## 6. What I could not test, and why (environment limitation, not a finding about the software)

I attempted to capture live UDP Heartbeat/Status traffic to directly compare wire-level schema against Andy's fork and stock protocol. This requires the isolated instance to actually bind and listen on its configured UDP port. It never did, despite:
- Correct `.ini` values (`UDPServer=127.0.0.1`, `UDPServerPort=2242`, `AcceptUDPRequests=true`)
- Confirming those same values loaded correctly into the Settings → Reporting tab (screenshot-verified)
- Trying both `--test-mode` and without it, with valid dummy station details (`MyCall=W0TEST`, `MyGrid=EN00aa`) pre-seeded

Root cause, as far as I could determine: this sandboxed environment has **no audio input/output devices at all** (the Settings → Audio tab's Input/Output dropdowns are both empty). WSJT-X's Settings dialog validates and refuses to commit *any* change — including the UDP server settings — without a valid audio device selected, and there is none to select here. This appears to gate the actual UDP-socket initialization at runtime, not just the dialog's OK button. I could not find a way around this without installing a virtual audio driver, which I did not do without asking first, since it's a bigger environment change than this task called for.

**This means the following items remain unconfirmed and would need a real machine with audio hardware to close:**
- Byte-level Status/Decode message schema (does this build's Status message carry Andy's extended `txEnableClk`/`txHaltClk`-equivalent fields, or its own, or neither?)
- Whether `Configure`/`Reply`/`HaltTx` (standard messages) behave as expected against this build
- Whether the SuperFox/Hound UDP scoring API (§4) is reachable outside Fox/Hound mode, and its exact wire format

If this matters enough to close out, the practical next step is running the same isolated-install procedure on a machine with a real (or virtual) sound card, or asking someone in QLOGforblindhams who runs it against Jimmy or another controller already.

---

## 7. Answering your specific questions directly

**Is it actually WSJT-X Improved?** Essentially yes — same codebase family, confirmed via ProductName, CompanyName, copyright string, and word-for-word-matching changelog content, not merely inferred from a filename.

**Is it based on WSJT-X Improved with additional patches?** Likely just one: the window-title/branding string. No other functional difference found anywhere I could inspect. Not fully ruled out (see §3's honesty caveat).

**Is it based on official WSJT-X with accessibility changes?** No — it's Improved-lineage (71MB size class, DG2YCB branding, Improved-specific feature set all present), not a stock-WSJT-X-plus-patches build.

**Does it contain accessibility-specific source changes?** None found beyond the title string. No screen-reader hooks, no TTS, no accessibility-specific menu/dialog/keyboard changes identified in documentation or live GUI inspection.

**Does it contain QLog-specific changes?** None found.

**Does it contain Jimmy/Tilly/Otto-style protocol changes (message types 16-18, extended Status)?** Not confirmed either way — this is exactly the piece blocked by §6's environment limitation. Given the branding-only pattern found everywhere else, I'd bet against it, but this is inference, not verification.

**Does it change keyboard behavior, screen-reader behavior, UDP behavior, Hamlib behavior, CAT behavior, logging, sequencing, AutoSeq, Wait and Reply, Wait and Call, Fox/Hound behavior, message generation, or the decoder — beyond what's already native to mainline WSJT-X Improved 3.1.0?** No evidence of any such change found. Everything behaviorally distinctive observed (Wait features, Quick Filters, alert categories, WAS-via-FCC, FT2, decoder improvements) is documented, mainline WSJT-X Improved functionality, not something exclusive to this build.

**Does it expose any capability that would let Jimmy eliminate one or more of Andy's custom protocol extensions?** The most promising lead is the **SuperFox/Hound UDP scoring API** (§4) — a standard(ish) WSJT-X Improved mechanism for externally-supplied callsign scoring, which is conceptually what Andy's non-standard `AnnotationInfo` message does. It's Fox/Hound-scoped as documented, and its applicability to Jimmy's normal (non-DXpedition) operation is unconfirmed pending the live-capture that §6 couldn't complete. Separately, and more certainly: because Andy's fork appears to already build on WSJT-X Improved's feature set (§5), migrating away from Andy's fork specifically to gain WSJT-X Improved's *features* is less necessary than Phase 1/2 assumed — those features are already present in your current production setup. What would specifically be gained by moving to mainline WSJT-X Improved is the *decoder/protocol maintenance stream* (FT2, ongoing decode improvements, more frequent updates) that Andy's fork, pinned to old 2.7.0/3.0.0-rc1 bases, doesn't get.

---

## 8. Revisions to Phase 2's feature-by-feature disposition table

Only items that actually change are listed; everything else in Phase 2 stands.

- **§3.1/3.2 (Ranking/Admission filtering)**: revise "WSJT-X Improved's filtering is basic decode-list filtering" → "WSJT-X Improved has real single-criterion auto-selection (Wait and Pounce: First/Max Dist/Max dB/Min dB; Call Best neediness-based assist) plus three-way Blacklist/Whitelist/Always-Pass filtering — meaningfully more capable than previously stated, but still categorically simpler than Jimmy's multi-tier weighted, award-integrated ranking." Disposition unchanged (**Keep in Jimmy**); rationale strengthened with concrete detail rather than weakened.
- **§7 (Notifications & Sounds)**: revise "WSJT-X Improved's alerting is a coarse callsign-match tone" → confirmed to be a **full category-based, multilingual, per-event spoken-alert system** (DXCC/CQZone/ITUZone/Grid/Continent/USState/MyCall/Wanted, each "on-band" variant too) — a much closer parallel to Jimmy's own sound system than previously described. Disposition still **Keep in Jimmy** (Jimmy's cooldown windows, callsign-specific overrides, and integration with the live award-tag pipeline remain more refined), but the "narrow overlap" language in Phase 2 undersold it.
- **§9.1 (Awards engine)**: WSJT-X Improved's WAS award uses a static bundled `callsign_states.tsv` snapshot, not a live-refreshing, deduped source. Strengthens (doesn't change) the existing **Keep in Jimmy** disposition for Jimmy's own live-downloading, dedup-correct `FccUlsProvider`.
- **New item, not in Phase 2 at all — Hound-queue external scoring**: WSJT-X Improved's SuperFox/Hound UDP scoring API is a plausible existing path for Jimmy's ranking output to reach WSJT-X's own display in Fox/Hound-specific contexts. Disposition: **Cannot yet be determined** — requires the live-capture blocked in §6, and clarifying whether it extends to normal (non-Fox/Hound) operation at all.
- **Phase 1 §1.1 (WSJT-X Improved vs. Andy's fork relationship)**: revise "no confirmed code lineage... unconfirmed" → **real, confirmed circumstantial code-level relationship** based on shared distinctive `.ini` schema between Andy's live production build and this WSJT-X-Improved-lineage build. Practical effect: migrating off Andy's fork is less about "gaining new features" (many already present) and more about "gaining an actively-maintained upstream" (decoder work, FT2, more frequent fixes) and "shrinking the protocol-extension surface that has to be independently maintained."

---

## 9. What's next

The two open items worth closing if you want full certainty before any migration decision:
1. A byte-level diff against the official `wsjtx-3.1.0-win64_improved_PLUS_260522.exe`, downloaded via a normal browser (not blocked the way this sandbox's scripted attempts were).
2. Live UDP capture on a machine with real audio hardware, to settle §6/§7's open questions about extended Status fields and the Hound-scoring API's scope.

Everything else requested — forensic identity, origin, GPL/source posture, functional differences from Andy's fork/WSJT-X Improved/stock WSJT-X, which Jimmy dependencies would disappear vs. remain, and the migration-difficulty assessment — is answered above with its confidence level stated. Test artifacts remain at `C:\claude\research\wsjtx_vi_install\` and two isolated WSJT-X config profiles under `%LocalAppData%` (`WSJT-X - ClaudeResearchTest` and `qttest\WSJT-X - ClaudeResearchTest - test`) in case you want to inspect them further — let me know if you'd like those removed.
