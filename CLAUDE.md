# Jimmy — WSJT-X Controller: Project Rules

## Workflow

- No backups unless explicitly requested. When a backup is requested: ZIP source files only, no binaries, no backup folders.
- Complete one task before starting another.
- Keep changes as small as possible. Avoid unrelated cleanup or refactoring.
- Investigate and read relevant code before implementing, unless instructed otherwise.
- Report larger or unexpected problems before changing code; wait for approval.
- Never run git operations (commit, push, branch, reset) unless explicitly asked.

## Builds

- Build Debug configuration unless Release is explicitly requested.
- Report all build errors and warnings before proceeding.
- Run parser tests after any parser changes.
- Run replay tests after any replay or protocol changes.

## Replay Framework

- Add or update a replay test before fixing a protocol bug, whenever practical.
- Keep all passing replay tests permanently as regression tests.
- Do not remove a passing replay test without explicit approval.

## Accessibility

- All UI must be usable with JAWS and NVDA screen readers.
- Do not steal keyboard focus.
- Keep speech output concise — no verbose or redundant announcements.
- Use short, clear AccessibleName values on controls.
- Preserve full keyboard accessibility; every action must be reachable without a mouse.
- Accessibility is a first-class requirement, not an afterthought.

## Release Versioning

For every public Jimmy release, even a small beta or installer-only fix:
- Increment AssemblyFileVersion.
- Increment AssemblyInformationalVersion.
- Increment WiX ProductVersion.
- Rebuild Release with /t:Rebuild.
- Rebuild the MSI.
- Verify the MSI ProductVersion.
- Verify the Jimmy.exe File table version inside the MSI.
- Verify that MajorUpgrade will upgrade the previous public release.
- Copy the fresh MSI to C:\claude\Jimmy\Jimmy.msi.
- Verify SHA-256 before upload.
- Zip the MSI (Jimmy.msi.zip) and upload it as an additional GitHub release asset
  alongside the raw MSI — the update page (blindsea.com/jimmy) prefers the zip since
  browsers/SmartScreen flag a bare .msi download more aggressively.

AssemblyVersion may remain frozen unless project policy changes.

## Coding Philosophy

- Prefer reusing existing logic over duplicating it.
- Do not special-case individual callsigns or stations in code.
- Reliability is more important than new features.
- Never knowingly lose a valid FT8/FT4 station or QSO opportunity due to a code change.
- Root-cause fixes only — do not paper over bugs with workarounds that hide the underlying problem.
