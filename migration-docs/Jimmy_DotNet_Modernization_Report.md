# Jimmy: .NET Framework 4.7.2 → Modern .NET (WinForms retained) — Engineering Report

Status: research only, no code changed. Investigation only, per your request — recommendation included, implementation not started.

---

## 1. Target version: **.NET 10**, not 8 or 9

As of today (2026-07-14), **.NET 8 and .NET 9 both reach end-of-support on the same day: November 10, 2026** — about four months from now. This is a real, Microsoft-confirmed fact (devblogs.microsoft.com, June 2026 announcement), a side effect of Microsoft extending STS support windows from 18 to 24 months, which pulled .NET 9's EOL forward to line up with .NET 8's LTS end date. Neither is a sane migration target today.

**.NET 10** (LTS, released Nov 11, 2025) is the only version that makes sense: supported into late 2028, giving ~2.3 years of runway before 8/9 retire. This isn't a close call — it's the only current LTS release, and there's no reason to deliberately target something with a 4-month runway.

---

## 2. Every project/dependency that needs changes

- **`WSJTX_Controller\Jimmy.csproj`** — the main app. Legacy (non-SDK) project format, `packages.config`-based. Must convert to SDK-style (`<Project Sdk="Microsoft.NET.Sdk">`), `TargetFramework=net10.0-windows`, `UseWindowsForms=true`. **`packages.config` → `PackageReference` is mandatory, not optional** — SDK-style projects don't support `packages.config` at all (confirmed via Microsoft docs/NuGet docs), so this happens as part of the same conversion, not as a separate cleanup step later.
- **`JimmyTests\JimmyTests.csproj`** — same legacy format, same conversion needed. It references `..\WSJTX_Controller\bin\Debug\Jimmy.exe` directly as a library (`<Reference Include="Jimmy">` with `HintPath`) — this pattern still works in SDK-style projects but should probably become a `ProjectReference` while you're in there (lower-risk, more idiomatic, not strictly required).
- **`App.config`** — its `<startup>` element (targeting `.NETFramework,Version=v4.7.2`) has no meaning on modern .NET and gets removed; that's automatic as part of retargeting, not extra work. Its `<userSettings>` section (backing `Properties.Settings.Default`, actively used — ~28 call sites in `Controller.cs`, confirmed by grep, not vestigial) needs the **`System.Configuration.ConfigurationManager`** NuGet package (Microsoft-published) to keep working on modern .NET, since that API isn't in the base class library anymore.
- **`Jimmy.csproj`'s custom `BeforeBuild`/`AfterBuild` targets** (the Club Log key-embedding `RoslynCodeTaskFactory` inline task) — should still work under SDK-style MSBuild (these are ordinary MSBuild extension points, not tied to the legacy project format), but needs a build-and-verify pass since inline task compilation behavior has had rough edges across MSBuild versions historically.
- **`Setup_WiX\Jimmy.wxs`** — see §4, significant restructuring needed regardless of self-contained-vs-framework-dependent choice.
- **`build.bat`/`test.bat`/`run_replay_tests.bat`/`run_parser_tests.bat`** — currently locate `MSBuild.exe` via `vswhere` and invoke it directly on the `.csproj`. This still works for SDK-style projects, but `dotnet build`/`dotnet publish`/`dotnet test` (the CLI) is the more idiomatic and more reliable path for modern .NET — worth switching to avoid depending on a full Visual Studio install being present just to build.

---

## 3. `System.Data.SQLite` and native interop

The exact package Jimmy currently uses, `Stub.System.Data.SQLite.Core.NetFramework`, is .NET-Framework-only by name and design. **Do not reference it directly on modern .NET.** Instead, reference the top-level `System.Data.SQLite.Core` (or `System.Data.SQLite`) package — NuGet's target-framework resolution automatically routes a modern-.NET project to `Stub.System.Data.SQLite.Core.NetStandard` (current version 1.0.119, explicitly supports netstandard2.0/2.1, consumable by net5.0 through net10.0). This is the path of least change: no ADO.NET provider swap, no query or type-mapping rewrites — `System.Data.SQLite`'s dynamic-typing behavior (which `LogbookDb.cs` and the Awards rule engine's SQL both depend on) is preserved exactly. **Do not switch to `Microsoft.Data.Sqlite`** — it's intentionally not feature-complete (built for EF Core, no synthetic type coercion), and switching would mean re-validating every query's type-handling assumptions for no lifecycle-driven reason.

**One thing that needs empirical verification, not assumption**: whether the NetStandard stub bundles the native x64 `SQLite.Interop.dll` the same zero-extra-step way the NetFramework stub does. Documentation doesn't say explicitly either way. First step of implementation should be a throwaway test project: reference `System.Data.SQLite.Core` under `net10.0-windows`, publish, and confirm `SQLite.Interop.dll` actually lands in the output folder before touching Jimmy itself.

---

## 4. WiX/MSI changes

Two things change simultaneously, independent of each other:

1. **Heat.exe (WiX's harvesting tool) is being phased out** — WiX's own docs call it obsolete. The current pattern is native `<Files Include="glob">` harvesting (WiX v5+) or `dotnet publish` + auto-harvest via a `.wixproj` `ProjectReference`. Your `Jimmy.wxs` currently lists every file by hand (explicitly, because "WiX 4.0.6 core schema has no wildcard file harvesting," per its own comments) — a self-contained publish makes explicit listing impractical (see below), so this is a forced move to WiX v5 and directory harvesting, not a nice-to-have.
2. **Self-contained deployment changes the file count by orders of magnitude.** Today's MSI ships a handful of files (`Jimmy.exe`, `Jimmy.exe.config`, `System.Data.SQLite.dll`, two MQTTnet DLLs, per-arch `SQLite.Interop.dll`) plus resources — all hand-listed in the `.wxs`, deliberately, with comments warning that a missed file means a broken install. A self-contained .NET 10 publish produces **hundreds of runtime DLLs**. Hand-listing those is not viable; this is the forcing function for directory harvesting.

A known WiX v5 gotcha to watch for: duplicate directory entries from auto-harvest under some folder layouts (wixtoolset/issues#8608) — worth deliberately checking the built MSI's file table, which your own release checklist already requires as a step.

The **.NET Framework 4.7.2 launch-condition check** (`NETFX472RELEASE` registry search + `Launch Condition`) becomes unnecessary if self-contained (no prerequisite to check for) — see §7.

---

## 5. Windows API / accessibility / behavior survey

Confirmed via direct source grep, then checked against modern-.NET behavior:

| Area | Current usage | Modern .NET status |
|---|---|---|
| `AccessibleName`/MSAA | Core to every control (Controller.cs, all dialogs) | **Unchanged, mandatory** (can't be disabled the way .NET Framework allowed via compat switches — the improved accessibility behavior is always-on on modern .NET). This is a non-issue, arguably an improvement. |
| `SendKeys.Send("{UP}")` | **10+ call sites** — this is Jimmy's core screen-reader re-announce mechanism (`Controller.cs`, `WsjtxClient.cs`) | **Real, documented regression risk.** Multiple open dotnet/winforms GitHub issues (#6666, #7945, #2660, #14145) describe `SendKeys` behaving differently on modern .NET vs Framework — wrong characters on non-US keyboard layouts, exceptions on certain key-combo syntax, and a documented change where the modern implementation can fall through to a `SendInput`-based path that **no longer reliably blocks/waits** the way the old journaling-hook implementation did. Since `Send("{UP}")` is the single most accessibility-load-bearing line of code in the entire app, **this needs dedicated, explicit testing before any migration is considered done** — not just "it compiles." |
| `Microsoft.Win32.RegistryKey` | `Controller.cs`, `SupportReportBuilder.cs`, `UsGridStateMap.cs` (reads WSJT-X's state grid registry data) | Works on modern .NET, but requires the **`Microsoft.Win32.Registry`** NuGet package (no longer in the base class library, for cross-platform purity reasons) — small, mechanical add. |
| DPAPI (`ProtectedData.Protect`/`Unprotect`) | `CredentialProtector.cs` — encrypts stored QRZ/Club Log/LoTW passwords at rest | Works on modern .NET via the **`System.Security.Cryptography.ProtectedData`** NuGet package. Same DPAPI semantics (`CurrentUser` scope), so **already-encrypted credentials from the current install should decrypt fine post-migration** (DPAPI keys are tied to the Windows user profile, not the .NET runtime) — worth a explicit verification test given this gates whether upgrading users lose their saved QRZ/Club Log/LoTW passwords. |
| `winmm.dll` `PlaySound` (P/Invoke) | `NotificationSounds.cs` | Unaffected — raw P/Invoke to a Windows system DLL works identically on modern .NET. |
| `kernel32.dll` `WritePrivateProfileString`/`GetPrivateProfileString` (P/Invoke) | `IniFile.cs` | Unaffected, same reasoning. |
| `AllocConsole`/`GetConsoleWindow`/`ShowWindow`/`SetForegroundWindow` (P/Invoke) | `Controller.cs` — Debug-build console + focus-stealing workaround (the one Jimmy's own memory flags as previously causing a real startup-focus bug) | Unaffected mechanically, but **this exact interaction (AllocConsole stealing foreground grant) was already a fragile, previously-broken area** (see prior incident) — deserves explicit re-verification on modern .NET rather than assuming the same fix still holds, since window-activation timing can shift subtly across runtime versions. |
| Networking (`UdpClient`) | `WsjtxClient.Protocol.cs` | No known behavior differences; core BCL networking types are stable across Framework/modern .NET. |
| Threading (`Timer`, `BeginInvoke`, `Task`) | throughout | No known behavior differences. |
| Configuration (`Properties.Settings.Default`) | `Controller.cs`, ~28 active call sites | Needs `System.Configuration.ConfigurationManager` package (see §2); the generated `Settings.Designer.cs` pattern still code-gens the same way under SDK-style projects, but the **on-disk persistence path may resolve slightly differently** — worth an explicit check that upgrading users' existing saved values are found, not silently reset to defaults. |
| Serialization | grepped for `BinaryFormatter` — **zero usage found**, confirmed clean | No blocker. |
| File paths | `%LocalAppData%\Jimmy\...`, `%AppData%` | No known differences — `Environment.GetFolderPath`/`SpecialFolder` behavior is unchanged. |

**No COM interop, no UI Automation code written directly by Jimmy** (WinForms' own accessibility layer handles UIA under the hood — Jimmy never touches `System.Windows.Automation` directly), so that's a non-issue beyond what's already covered by the `AccessibleName`/`SendKeys` rows above.

---

## 6. Will AccessibleName, focus handling, and the JAWS/NVDA re-announce trick keep working?

**Mostly yes, with one real risk flagged, not glossed over.** `AccessibleName`/MSAA continuity is well-documented and solid — if anything, .NET 10's WinForms changelog explicitly lists "improved NVDA screen reader support" as a named improvement, which is a positive signal, not just an absence of regression. Focus-handling P/Invoke calls are unaffected mechanically.

The one place worth real caution is `SendKeys.Send("{UP}")` itself (§5) — not because it's expected to break, but because it's *documented as having changed* in ways that matter specifically for a re-announce-timing trick like Jimmy's, and I found no source confirming JAWS-specific (as opposed to NVDA/Narrator) behavior either way. This is the single item in this whole report I'd treat as "verify before considering the migration done," not "assume fine and move on." Concretely: after a build on .NET 10, the correct test is a real JAWS session and a real NVDA session, exercising every path that currently calls `SendKeys.Send("{UP}")`, confirming re-announcement still fires reliably and with the same timing characteristics as today.

---

## 7. Self-contained deployment

**Yes, self-contained is viable and is the recommended default** — it removes the ".NET Framework 4.7.2 must already be installed" launch-condition entirely, which is a real usability win for a blind-operator-facing installer (no separate download-and-install-prerequisite step to navigate non-visually).

**Trimming is not available for WinForms** — confirmed via Microsoft's own docs: WinForms relies on COM marshalling in a way that makes trimming unsupported and disabled in the SDK today. Don't plan around `PublishTrimmed` shrinking the footprint; it isn't an option for this app type.

**Single-file vs. self-contained folder in the MSI: recommend the folder, not single-file**, for one concrete reason: a documented, currently-open Visual Studio GUI single-file-publish bug on .NET 8.0.300+ (dotnet/winforms#11473) — the CLI (`dotnet publish -p:PublishSingleFile=true`) works around it, but that's an extra fragility point for zero real benefit here (Jimmy isn't distributed as a bare .exe download today, it's already wrapped in an MSI, so single-file's main selling point — "one file to hand someone" — doesn't apply). A plain self-contained publish folder, harvested into the MSI by WiX (§4), is simpler and has no known open bugs.

---

## 8. Expected installer size

Rough planning number, **not a guarantee** — community reports put a self-contained win-x64 WinForms publish around **~150 MB** (hundreds of runtime DLLs plus the app itself); Jimmy's actual number will be somewhat higher once MQTTnet, SQLite's native interop, and Jimmy's own resources (WAV files, Rule Definitions) are added, but not dramatically so. **First concrete step of implementation should include an actual trial publish + measured MSI size** before this number is treated as final — don't plan a release-notes announcement around an unverified estimate.

Compare against today's framework-dependent MSI (small, a handful of MB) — this is a real, user-visible tradeoff: bigger download/install, in exchange for zero prerequisite friction. Worth stating plainly rather than burying it.

---

## 9. Upgrade behavior from existing installations

The existing `MajorUpgrade`/`UpgradeCode` (`{D5415907-DD93-4188-85A8-F15A73F949C2}`) mechanism in `Jimmy.wxs` is WiX/MSI-level, not tied to .NET Framework vs. modern .NET at all — **it continues to work unchanged**, since it operates on product/upgrade GUIDs, not on runtime version. An existing user running today's Framework-based Jimmy will get a clean major-upgrade install of the modern-.NET build the same way any version bump works today, per your existing release checklist ("Verify that MajorUpgrade will upgrade the previous public release").

Two things specifically worth a dedicated upgrade-path test, beyond the routine release checklist:
- **DPAPI credential continuity** (§5) — confirm saved QRZ/Club Log/LoTW passwords survive the upgrade and still decrypt.
- **`Properties.Settings.Default` continuity** (§5/§2) — confirm the handful of legacy settings (window position, IP/port, checkboxes) aren't silently reset to defaults after upgrade, since the persistence path resolution needs empirical confirmation.

The bulk of Jimmy's settings live in its own hand-rolled `.ini` file (`IniFile.cs`), which is completely unaffected by any of this — only the small legacy `Properties.Settings.Default` surface is a genuine open question.

---

## 10. Build-system and Visual Studio changes

- **MSBuild path**: your project's own build notes explicitly warn "Do NOT use framework MSBuild (v4.0.30319) — use VS 18 Community MSBuild" for C# interpolation support today. Modern .NET SDK-style projects are built by the **.NET SDK's own MSBuild** (via `dotnet build`) or any sufficiently recent Visual Studio's MSBuild — the existing MSBuild-path sensitivity in your build notes becomes obsolete once on SDK-style projects (the SDK brings its own consistent toolchain), which is a simplification, not a new risk.
- **Visual Studio version**: SDK-style WinForms projects targeting `net10.0-windows` need a VS version that ships the .NET 10 SDK (or the standalone SDK installed alongside an older VS) — worth confirming your current VS install has (or can get) .NET 10 SDK support before starting.
- **`build.bat`/test scripts**: recommend switching from `vswhere` + direct `MSBuild.exe` invocation to `dotnet build`/`dotnet publish`/`dotnet test` — more portable, doesn't require a full VS install on a build machine, and is the idiomatic modern-.NET path.

---

## 11. Unit-test and replay-test changes

- **`JimmyTests.csproj`**: same SDK-style conversion as the main project. Its pattern of referencing `Jimmy.exe`'s **compiled output** directly (rather than the source) to test parser/ranking types should still work as a `ProjectReference` or even a raw assembly reference — low risk, mechanical change.
- **`JimmyReplay.py`**: this is an external Python harness driving Jimmy over UDP — **completely unaffected by the .NET runtime migration**, since it only interacts with Jimmy's UDP protocol surface, not its internals. No changes expected here at all.
- **New test coverage this migration should add, not just preserve**: a dedicated `SendKeys` reliability check (§5/§6) has no equivalent in today's suite (JimmyReplay doesn't drive real screen-reader interaction), and a DPAPI-credential-round-trip test covering "encrypted on Framework, decrypted on modern .NET" would close the upgrade-continuity question in §9 with an automated test rather than manual verification alone.

---

## 12. Third-party packages: what blocks, what doesn't

- **`System.Data.SQLite`** — no blocker, see §3 (reference the top-level package, let NuGet resolve the right stub).
- **`MQTTnet`/`MQTTnet.Extensions.ManagedClient` 4.3.7.1207** — **no blocker, no forced upgrade needed.** This exact version doesn't ship an explicit `net8.0`/`net10.0` asset, but NuGet's forward-compatibility rules let a `net10.0-windows` app consume its `net7.0` asset directly without any code change. Worth knowing for later: MQTTnet 5.x drops .NET Framework support entirely, so if you ever want to move to MQTTnet 5.x for other reasons, that becomes a one-way door — staying on 4.3.7.1207 for now is actually convenient since it keeps you buildable for both `net472` and `net10.0-windows` during a staged migration (§13).
- **Nothing else in the dependency list is a blocker** — no other third-party packages found in `packages.config`/`Jimmy.csproj` beyond SQLite and MQTTnet.

---

## 13. Safest migration order

1. **Throwaway spike, outside Jimmy entirely**: new `net10.0-windows` WinForms console/test project, add `System.Data.SQLite.Core` + `MQTTnet` 4.3.7.1207, confirm `SQLite.Interop.dll` lands in the output and a basic SQLite read/write round-trips. Resolves §3's one open empirical question before touching real code.
2. **Convert `JimmyTests.csproj` first, not `Jimmy.csproj`.** Smaller, lower-stakes project; proves the SDK-style + PackageReference conversion pattern works before applying it to the app that actually matters. (It currently depends on `Jimmy.exe`'s Debug build existing, so this needs `Jimmy.csproj` buildable in whatever state it's in — sequence carefully, don't strictly gate on full app conversion first if that turns out to block it.)
3. **Convert `Jimmy.csproj`** to SDK-style/`net10.0-windows`/PackageReference. Get it compiling — expect the DPAPI, Registry, and Configuration NuGet package additions (§5) to surface here as compile errors, not runtime surprises, which is the easy way to find them.
4. **Full regression pass**: existing unit test suite (300+ tests) + `JimmyReplay.py` scenarios — these exercise pure logic and protocol handling, unaffected by the runtime change in principle, so a clean pass here is a strong signal nothing structural broke.
5. **Dedicated manual verification pass** for the items this report explicitly flags as "needs testing, not assumption": `SendKeys` re-announce timing with real JAWS and real NVDA sessions (§6), the `AllocConsole`/focus-stealing interaction in Debug builds (§5), DPAPI credential round-trip across an upgrade (§9), `Properties.Settings.Default` persistence continuity across an upgrade (§9).
6. **Self-contained publish trial + measured size** (§8), then rebuild `Setup_WiX\Jimmy.wxs` around WiX v5 directory harvesting (§4) — this is the largest single restructuring in the whole migration and should happen only after 1–5 are solid, since it's packaging, not application logic, and shouldn't block or be blocked by the code-level work.
7. **End-to-end MSI upgrade test** from a real current-production Jimmy install, per your existing release checklist, with the two upgrade-continuity checks from §9 included explicitly this time.

### Which pieces are highest risk
- **`SendKeys` behavior** (§5/§6) — the one item in this entire report with documented upstream regressions specifically in the mechanism Jimmy's accessibility depends on most. Everything else is either "known to work" or "mechanical, low-risk."
- **WiX restructuring** (§4/§13 step 6) — largest surface-area change (hundreds of harvested files vs. today's dozen hand-listed ones), and MSI packaging bugs are the kind that only show up on a real install, not in a build log.

### Which pieces should not be touched until later (or possibly not part of this migration at all)
- Do not use this migration as an opportunity to also migrate `Properties.Settings.Default` usage over to `IniFile.cs`-style storage, tempting as that consolidation might be — that's an unrelated cleanup that adds risk to a runtime migration for no lifecycle-driven reason. Same logic applies to any other "while we're in here" refactor impulse — this migration should change the runtime and packaging, not the application's internal settings architecture.
- Do not attempt `PublishTrimmed` — confirmed unsupported for WinForms today (§7), not a "maybe later" item, an actual current limitation.

### What success looks like
Jimmy builds and runs identically from an operator's perspective on `net10.0-windows`, self-contained, with a WiX-packaged MSI that installs with no .NET prerequisite step; the existing test suite and replay scenarios pass unchanged; a real JAWS and NVDA session confirms every `SendKeys.Send("{UP}")` re-announce point still fires correctly; an upgrade from a real current production install preserves saved credentials and settings; and the installer size increase (§8) is known and accepted, not a surprise discovered after release.
