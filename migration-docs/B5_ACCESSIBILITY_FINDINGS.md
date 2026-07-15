# Stage B5 accessibility findings (live JAWS testing, net10.0-windows build)

Status: in progress. B5 requires your own JAWS/NVDA verification per the
roadmap ("You are the required verifier here") -- this log tracks findings
across sessions so nothing gets re-discovered or re-tested by accident.

Build under test: `C:\claude\jimmy_wsjtx31\WSJTX_Controller\bin\Debug\net10.0-windows\Jimmy.exe`
(rebuild after any fix below before retesting -- check "Date Modified", not
"Date Created": .NET builds overwrite the exe in place, so Explorer's Date
Created stays fixed at whenever that file path was first built.)

Testing against WSJT-X 3.0.0-rc1 (not 3.1) since A5/capability negotiation
isn't implemented yet -- the net10.0 build still enforces the legacy version
gate and refuses 3.1. Correct, expected, matches the roadmap.

## Finding 1 — statusText arrow-key navigation broken — FIXED, confirmed

- Symptom: JAWS could not arrow-key-navigate/review text inside the main
  screen's status box (production could).
- Root cause: `statusText.AccessibleRole` was explicitly `AccessibleRole.None`
  (present since the control was first added, no explanatory history).
  `Switch.UseLegacyAccessibilityFeatures{,.2,.3}` was tried first and
  confirmed to do nothing -- per Microsoft's own docs, .NET (unlike .NET
  Framework) does not support opting out of the new accessibility behavior
  at all; that avenue is a dead end for any future finding here too.
- Fix: changed to `AccessibleRole.Text` (`Controller.Designer.cs`), matching
  what a plain unmodified TextBox already reports and what every other
  working edit box in the app has. Commit `3ed5adc`.
- Verified: confirmed fixed by live JAWS retest, 2026-07-15.
- Swept for the same pattern elsewhere: every other `ReadOnly=true` TextBox
  in the app (`OptionsDlg`'s several label-style boxes) explicitly sets
  `AccessibleRole.StaticText`, a real distinct role, not `None`. `HelpDlg`'s
  `helpLabel` sets no override at all. Nothing else matches this pattern --
  this was an isolated, one-off issue, not systemic.

## Finding 2 — list box JAWS-cursor tracking broken — OPEN, unresolved

- Symptom: in production, JAWS cursor follows/tracks selection inside the
  app's list boxes (callListBox, logListBox, and the Advanced Call Layout
  lists advTx1ListBox/advTx2ListBox/advRawListBox -- all `DrawMode.
  OwnerDrawFixed` with a shared `AdvListBox_DrawItem` handler). On
  net10.0-windows, JAWS cursor does not follow in these list boxes.
  Confirmed as a genuine regression (not present in production) by direct
  comparison, 2026-07-15.
- Ruled out: `AdvListBox_DrawItem` already uses `TextRenderer.DrawText` (the
  GDI-based, screen-reader-friendly renderer), not `Graphics.DrawString`;
  this code is untouched by the migration. The
  `UseLegacyAccessibilityFeatures` switches are confirmed inapplicable on
  .NET (see Finding 1) so not worth retrying here either.
- Working theory, unconfirmed: ListBox's UI Automation support (per-item
  bounding rectangles, ScrollItemPattern, focus-change events) was a
  separate, later addition to WinForms (.NET Framework 4.7.1+) from
  TextBox's, reimplemented independently for .NET Core WinForms. JAWS-cursor
  tracking depends on the framework correctly reporting each item's on-
  screen bounding rectangle as focus/selection moves through an owner-drawn
  list -- plausible this is a genuine, specific .NET Core WinForms port gap
  for owner-drawn ListBox items, not anything in Jimmy's own code. No
  matching public dotnet/winforms issue found by search, so this may be
  under-reported rather than a known, already-fixed issue.
- Not yet attempted: a custom accessible-object override to manually report
  correct per-item bounding rectangles. Bigger, less certain change than
  Finding 1's fix; deferred pending a fuller test pass (see checklist below)
  so we're not spending a retest cycle on it before knowing what else needs
  fixing first.
- **Open question for you**: does normal Tab-into-the-list-then-arrow-up/down
  (not JAWS-cursor mode) correctly announce each entry? That's the primary
  way the app is actually used day to day -- if that works, this finding is
  lower priority than if it doesn't.
