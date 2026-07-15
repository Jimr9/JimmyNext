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

## Confirmed working (no issues found)

- Startup focus / `ForceForeground` interaction: close Jimmy fully, relaunch
  fresh -- focus lands and announces correctly without needing to click or
  Alt-Tab first. This was the specific thing the roadmap asked to re-verify
  on the new runtime (previously-fixed `AllocConsole` foreground-stealing
  bug, per project history) -- confirmed still fixed on net10.0-windows.
  2026-07-15.
- Main-screen "CQ intent" mode buttons (`modeGroupBox`: Listen / CQ only /
  CQ DX only / CQ and CQ DX) -- tabbed through and read correctly. Not
  actually invoked (Alt+C starts a real CQ transmission -- correctly left
  untested per the no-transmit-testing rule).
- Options dialog: Logbook Sync tab (QRZ/LoTW/Club Log sub-panels, all
  fields/checkboxes/spin-boxes) fully tabbed through, reads correctly aside
  from Finding 3 above.

## Remaining checklist (from Controller.Designer.cs's actual AccessibleName
list, not assumption -- update this if the UI has moved on again)

- `callListBox` ("stations calling:") / `logListBox` ("auto-logged calls:")
  -- simple-mode lists, if not already covered by the "control 1 2 3 4"
  pass from earlier in testing.
- `advTx1ListBox`/`advTx2ListBox`/`advRawListBox`/`spotWatchListBox` --
  only visible with Advanced Call Layout enabled.
- `timeoutNumUpDown` ("Repeat limit"), `showUsStateCheckBox` ("Show U.S.
  state instead of USA").
- The scattered "Help for X setting" labels (Directed CQ, Alert callsign,
  Log Early, Exclude filter, Include filter, Ignore non-DX, RR73 reply,
  Transmit period, Transmit limit, Except callsigns, Auto frequency).
- Remaining Options tabs beyond Logbook Sync (there are 13+ tab pages).

## Update 2026-07-15: full pass through Options (Basic, General, Receive/Auto
Reply, Transmit, Hotkeys, Advanced UI, Wanted Calls, Spot Watch, Sounds)

Everything reads correctly except one new finding below. This retires most
of the "remaining checklist" items above -- only Sounds tab (partially
covered), and Awards/Still Need/Lookup/Edit Log/Sync tabs in the Logbook
window (separate from Options) remain untested.

## Finding 4 — CheckBox announces the full label one extra time, on the first toggle only — OPEN, low severity, confirmed

- Direct apples-to-apples comparison, same checkbox ("Always on top",
  General tab), same steps (tab onto it, press Space 6 times), both
  captured as JAWS speech buffers 2026-07-15:
  - **Production**: arrival = full label + state ("not checked"). All 6
    subsequent Space toggles = bare state word only ("checked", "not
    checked", ...), no repeats of the full label, ever.
  - **Migration (net10.0-windows)**: arrival = full label + state ("not
    checked"). **Toggle 1** = full label + state again ("checked") --
    the one extra announcement. Toggles 2-6 = bare state word only,
    identical to production from that point on.
- So the earlier "every 3rd time" read (from the first big Options pass,
  four different checkboxes) was consistent with this same root cause, just
  described imprecisely from noisier data. The actual shape is: one extra
  full-label announcement on the first state change after focus arrives,
  never again after that.
- Confirmed NOT a Jimmy-code-level issue: zero `CheckBox` controls anywhere
  in the app have any `AccessibleRole` (or other accessibility property)
  override -- plain, unmodified WinForms `CheckBox` instances. No specific
  Jimmy-set property to change, unlike Finding 1.
- Working theory, unconfirmed: some .NET 10 WinForms `CheckBox`/
  `AccessibleObject` internal detail fires an extra accessibility
  change-notification specifically on the first `CheckedChanged` after the
  control's accessible object is realized (e.g. lazy-initialization on
  first real access), but not on subsequent toggles. Speculative -- not
  verified against WinForms source.
- Severity assessment: low. One extra spoken phrase, occurs once per
  checkbox per dialog-open (not persistently annoying), and arguably
  harmless since it repeats context rather than omitting it. No known
  Jimmy-side fix available (no property to correct). Recommend logging as
  a known minor cosmetic difference rather than pursuing a WinForms-
  internals-level fix with uncertain success -- open to reconsidering if
  you feel it's worth chasing further.

## Other observation (not yet a confirmed finding)

"Jimmy Options" (the dialog title) is announced very frequently throughout
tab/section navigation -- flagged by you as "the extra Jimmy Options."
Likely JAWS re-establishing context on its own heuristics as focus moves
between TabPages/groups, not something Jimmy's code explicitly triggers
(no repeated `this.Text = "Jimmy Options"` or similar in the dialog code).
Unclear whether this is fixable from Jimmy's side at all, or whether it
differs from production. Needs clarification: does production announce the
dialog title this often too, or is this also new on net10.0-windows?
