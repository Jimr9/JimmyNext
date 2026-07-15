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
- **Answered, 2026-07-15**: normal Tab-into-the-list-then-arrow-up/down reads
  every entry fine. Severity downgraded -- the primary real-world interaction
  path is unaffected; only JAWS-cursor screen-review mode is broken.

## Finding 3 — stray control announced across a TabControl page boundary — OPEN, low severity

- Symptom: tabbing into Options' "Logbook Sync" tab, JAWS reads
  `udpPortStdLabel` ("(Standard: 2237)") -- a `Label` that belongs to the UDP
  port field on the separate "UDP / Connection" tab -- immediately before
  correctly landing on the Logbook Sync tab's own first control (the
  `serviceList` ListBox, `AccessibleName = "Logbook service list"`).
  Reproduced twice in the same session (pasted JAWS speech buffer,
  2026-07-15).
- Confirmed adjacency: `udpTabPage` is `tabControl1` page index 9,
  `logbookSyncTabPage` is index 10 -- immediately next to each other
  (`OptionsDlg.Designer.cs`). Strongly suggests JAWS's tree-walk is briefly
  touching the *previous* tab page's trailing control right as focus lands
  on the next page, i.e. the UI Automation tree isn't correctly scoping
  itself to only the active TabPage's children. Not investigated further
  yet (not attempted: any fix).
- Severity: low/cosmetic -- one extra spoken phrase, not a functional
  blocker, doesn't affect any control's own correctness once reached.
- Not yet known: whether this happens at other tab-page boundaries in
  Options (there are 13+ tabs) or is specific to this pair; whether it
  happens in both Tab and Shift+Tab directions. Not blocking further
  testing -- logged for later, revisit if it turns out to be widespread
  rather than a one-off.
