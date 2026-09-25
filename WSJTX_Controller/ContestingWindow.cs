using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Nexus contesting foundation: the accessible Contesting workspace. Non-modal singleton
    // (Show(), not ShowDialog()), same pattern as OtaSpotsWindow/LogbookWindow -- an operator
    // running a contest wants this visible alongside normal operation. Opened/focused by both
    // Options -> Station & Operator's "Open Contesting" button and the configurable Contesting
    // hotkey (see Controller.OpenContestingWindow) -- the SAME window either way, never a second
    // instance.
    //
    // Live JAWS testing went through two designs before this one:
    //   1. A tab inside Logbook Center -- rejected: Logbook Center's own category list followed
    //      by three MORE nested tabs inside that one entry read poorly.
    //   2. A single flattened top-to-bottom sequence of six GroupBoxes in the standalone window
    //      -- also rejected (2026-09-25): with every group simultaneously parented in one
    //      AutoScroll panel, JAWS's own speech buffer (which reads the whole present, focusable
    //      tree, not just whatever currently has focus) kept re-exposing the same groups and
    //      controls regardless of where focus actually was.
    // This is the corrected design: the SAME accessible category-list-and-page arrangement
    // Options and Logbook Center already use (CategoryListNav.Wire) -- a plain ListBox
    // (_categoryListBox) driving exactly ONE visible page at a time in a host panel
    // (_categoryDetailHost). Only the selected category's page is ever actually parented in the
    // tree, so it is the only one visible, enabled, exposed to accessibility, or reachable by Tab
    // -- there is no hidden sibling left for JAWS's speech buffer to re-expose. No TabControl and
    // no nested tabs anywhere in this file. This list behaves EXACTLY like Options' and Logbook
    // Center's own category lists, not merely similarly: choosing a category only swaps which
    // page is shown, the same way CategoryListNav.Wire already does everywhere else it's used --
    // it never also moves focus into the page (an earlier version of this window did that, found
    // live, 2026-09-25, to make this list behave differently from the other two for no real
    // benefit); a real Tab press is what enters the page, same as Options/Logbook Center.
    //
    // Categories:
    //   Select & Configure -- Nexus contest list, support level, refresh, selected contest,
    //     required/generated contest fields, operating style, loading saved contest configuration.
    //   Active Contest -- enter/exit, current contest and session status, score, warnings/
    //     advisories, score recalculation.
    //   Manual Contact -- the CONTACTED station's callsign/band/mode, a known-Nexus-contest
    //     selection or an unvalidated/unscored free-text contest name, and Log Contact.
    //   Export -- Cabrillo and ADIF export.
    //
    // Every list here is a single-column ListBox with pre-formatted row text (never a
    // multi-column ListView) -- OtaSpotsWindow's own comment documents why: a live-tested NVDA
    // gap with multi-column ListView that a plain ListBox does not have. Every status/warning
    // surface below is exactly ONE label per concern, updated in place -- never duplicated -- so
    // nothing is announced twice. No MessageBox anywhere in this file: a warning from Nexus (e.g.
    // Winter Field Day's mode advisory) is shown as status text the operator can read on their
    // own schedule, never a focus-stealing dialog that interrupts typing an exchange mid-contact.
    // Controls that do not currently apply (Exit/Recalculate/Export before a session is active)
    // are disabled, not merely rejected at click time, so JAWS announces them as unavailable
    // rather than the operator discovering that only after activating one.
    //
    // Jimmy's OWN station identity (station callsign, operator callsign, grid, ARRL/RAC section)
    // is never re-typed here -- it is read live from Options -> Station & Operator (the _myCall/
    // _myGrid/_operatorCall/_station Funcs below, same live-read pattern LiveQsoUploadOrchestrator
    // already uses), matching that page's own role as the single authoritative place for it. If
    // Station Callsign or Grid Locator is blank, entering a contest or logging a manual contact is
    // refused with an accessible explanation naming the missing field and pointing at Station &
    // Operator (RequireStationInfo below) -- never a silent guess and never a proceed-anyway.
    public class ContestingWindow : Form
    {
        private readonly ContestClient _contestClient = new ContestClient();
        private readonly Func<string> _dbPath;
        private readonly Func<string> _myCall;
        private readonly Func<string> _myGrid;
        private readonly Func<string> _operatorCall;
        private readonly Func<ContestWorkflow> _workflow;
        private readonly Func<StationSettings> _station;

        private readonly System.Windows.Forms.Timer _statusTimer;

        // ── Category list / page host ────────────────────────────────────────────
        private ListBox _categoryListBox;
        private Panel   _categoryDetailHost;

        private const int PAGE_SELECT = 0;
        private const int PAGE_ACTIVE = 1;
        private const int PAGE_MANUAL = 2;
        private const int PAGE_EXPORT = 3;

        // ── Select & Configure ────────────────────────────────────────────────────
        private Panel _selectConfigPanel;
        private ListBox _eventList;
        private Label _eventStatusLabel;
        private List<ContestEventListEntry> _events = new List<ContestEventListEntry>();
        private TextBox _eventIdBox;
        private ComboBox _runModeCombo;
        private Panel _entryFieldsPanel;
        private Label _configStatusLabel;
        private ContestRuleset _selectedRuleset;
        private string _selectedEventId;
        private readonly List<(ContestField Field, Control Control)> _entryControls = new List<(ContestField, Control)>();

        // ── Active Contest ────────────────────────────────────────────────────────
        private Panel _activeContestPanel;
        private Button _enterButton, _exitButton;
        private Label _sessionStatusLabel;
        private Button _rebuildButton;
        private Label _scoreLabel;
        private Label _warningLabel;

        // ── Manual Contact ────────────────────────────────────────────────────────
        private Panel _manualContactPanel;
        private TextBox _manualCallBox, _manualBandBox, _manualContestFreeTextBox;
        private ComboBox _manualModeCombo, _manualContestCombo;
        private Panel _manualFieldsPanel;
        private readonly List<(ContestField Field, Control Control)> _manualControls = new List<(ContestField, Control)>();
        private Label _manualStatusLabel;

        // ── Export ────────────────────────────────────────────────────────────────
        private Panel _exportPanel;
        private Button _exportCabrilloButton, _exportAdifButton;
        private Label _exportStatusLabel;

        public ContestingWindow(
            Func<string> dbPath, Func<string> myCall, Func<string> myGrid, Func<string> operatorCall,
            Func<ContestWorkflow> workflow, Func<StationSettings> station)
        {
            _dbPath = dbPath;
            _myCall = myCall;
            _myGrid = myGrid;
            _operatorCall = operatorCall;
            _workflow = workflow;
            _station = station;

            Text = "Contesting";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            MinimumSize = new Size(760, 460);
            Size = new Size(880, 560);
            Font = new Font("Microsoft Sans Serif", 9F);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            BuildUi();

            _statusTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _statusTimer.Tick += (s, e) => RefreshSessionStatus();

            Load += (s, e) =>
            {
                RefreshEventList();
                PopulateManualContestCombo();
                // A session started before this window instance existed (it's destroyed and
                // recreated on Close/reopen, but the Nexus session itself -- and ContestWorkflow's
                // own reconciliation -- keep running independent of the window, same "keep
                // running regardless of the window" precedent as OtaSpotsWindow's live feeds) must
                // not leave Exit/Recalculate/Export looking unavailable when they actually are.
                bool active = _workflow()?.IsSessionActive == true;
                _enterButton.Enabled = !active;
                _exitButton.Enabled = active;
                _rebuildButton.Enabled = active;
                _exportCabrilloButton.Enabled = active;
                _exportAdifButton.Enabled = active;
                RefreshSessionStatus();
            };
            // Initial focus goes to the category list itself (JAWS announces "Contesting
            // categories, Select and Configure, 1 of 4" the instant the window opens), matching
            // the same Shown-based fix OptionsDlg/LogbookWindow already use -- Load fires before
            // the window is actually visible/activated, so a Focus() call made there is
            // unreliable. Choosing a different category afterward keeps focus on the list too
            // (see BuildUi's own SelectedIndexChanged handler) -- a real Tab press is what enters
            // the page, exactly like Options/Logbook Center's own identical category lists.
            Shown += (s, e) => _categoryListBox.Focus();
            FormClosed += (s, e) => { _statusTimer.Stop(); };
        }

        // ── UI construction ──────────────────────────────────────────────────────

        private void BuildUi()
        {
            var font = Font;

            _categoryListBox = new ListBox
            {
                Dock           = DockStyle.Left,
                Width          = 160,
                Font           = font,
                IntegralHeight = false,
                AccessibleName = "Contesting categories",
                TabIndex       = 0,
            };

            // Pure layout wrapper -- the one category page actually parented inside it carries
            // its own real AccessibleName (set below); left unnamed so WinForms/JAWS's own "infer
            // a name for this container" fallback never attaches to the host itself. Safe for
            // each page to carry a real name (rather than the ""/None a single always-present
            // container would need) specifically because CategoryListNav.Wire Adds/Removes only
            // the ONE currently-selected page -- see this class's own header comment.
            _categoryDetailHost = new Panel
            {
                Dock           = DockStyle.Fill,
                AccessibleName = "",
                AccessibleRole = AccessibleRole.None,
                TabIndex       = 1,
            };

            _selectConfigPanel   = BuildSelectConfigPage(font);
            _activeContestPanel  = BuildActiveContestPage(font);
            _manualContactPanel  = BuildManualContactPage(font);
            _exportPanel         = BuildExportPage(font);

            string[] pageNames = { "Select and Configure", "Active Contest", "Manual Contact", "Export" };
            var pagePanels = new List<Panel> { _selectConfigPanel, _activeContestPanel, _manualContactPanel, _exportPanel };
            for (int i = 0; i < pageNames.Length; i++)
            {
                pagePanels[i].Dock = DockStyle.Fill;
                pagePanels[i].AccessibleName = pageNames[i];
                pagePanels[i].AccessibleRole = AccessibleRole.Grouping;
                _categoryListBox.Items.Add(pageNames[i]);
            }
            CategoryListNav.Wire(_categoryListBox, _categoryDetailHost, pagePanels.Cast<Control>().ToList());

            // Corrected 2026-09-25 (live report): an earlier version of this handler also moved
            // focus DIRECTLY into the newly selected page's first control. Live testing found that
            // made this list behave differently from the identical-looking category lists in
            // Options and Logbook Center (which only swap the visible page and leave focus on the
            // list itself -- a real Tab press is what enters the page), which was confusing rather
            // than helpful. This list now matches that same, single established convention
            // exactly: CategoryListNav.Wire already handles showing the right page; the only thing
            // still needed here is refreshing Active Contest's own live status on every visit
            // (content, not focus -- same reasoning as RefreshSessionStatus's own timer tick).
            _categoryListBox.SelectedIndexChanged += (s, e) =>
            {
                if (_categoryListBox.SelectedIndex == PAGE_ACTIVE) RefreshSessionStatus();
            };

            Controls.Add(_categoryDetailHost);
            Controls.Add(_categoryListBox);
        }

        // Required Jimmy-side station identity: Station Callsign and Grid Locator. Returns null
        // when both are present; otherwise an accessible, specific explanation naming exactly
        // which one is missing and where to fix it -- never a silent guess, never a generic
        // "incomplete" message that leaves the operator to hunt for what's actually wrong.
        // Operator Callsign is deliberately NOT required here: StationSettings.OperatorCallsign's
        // own comment documents that it already falls back to Station Callsign wherever it's
        // consumed (WsjtxClient.RequestLog's same pattern) -- ResolvedOperatorCall below reuses
        // that existing convention rather than treating a blank value as an error.
        private string RequireStationInfo()
        {
            if (string.IsNullOrWhiteSpace(_myCall()))
                return "Station Callsign is not set. Go to Options, Station & Operator, to set it.";
            if (string.IsNullOrWhiteSpace(_myGrid()))
                return "Grid Locator is not set. Go to Options, Station & Operator, to set it.";
            return null;
        }

        private string ResolvedOperatorCall() =>
            string.IsNullOrWhiteSpace(_operatorCall()) ? _myCall() : _operatorCall();

        // ── Select & Configure ────────────────────────────────────────────────────

        private Panel BuildSelectConfigPage(Font font)
        {
            var page = MakePage();
            int y = 8;
            const int left = 8;

            var instr = new Label
            {
                Text = "Contests Nexus can validate, score, and export. Support level is Jimmy's own assessment of what it can do for each one -- not a claim about Nexus.",
                AutoSize = false,
                Location = new Point(left, y),
                Size = new Size(660, 36),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = font,
                TabStop = false,
            };
            page.Controls.Add(instr);
            y += 40;

            _eventList = new ListBox
            {
                Location = new Point(left, y),
                Size = new Size(660, 90),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = font,
                AccessibleName = "Contests",
            };
            page.Controls.Add(_eventList);
            y += 96;

            var refreshBtn = new Button { Text = "&Refresh List", Location = new Point(left, y), Size = new Size(120, 24), Font = font, AccessibleName = "Refresh contest list" };
            refreshBtn.Click += (s, e) => RefreshEventList();
            page.Controls.Add(refreshBtn);

            _eventStatusLabel = new Label
            {
                Location = new Point(left + 132, y + 3),
                Size = new Size(520, 20),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = font,
                AutoSize = false,
                TabStop = false,
                AccessibleName = "Contest list status",
            };
            page.Controls.Add(_eventStatusLabel);
            y += 32;

            var eventLabel = new Label { Text = "Selected event id:", AutoSize = true, Location = new Point(left, y + 3), Font = font, TabStop = false };
            page.Controls.Add(eventLabel);
            _eventIdBox = new TextBox { Location = new Point(left + 140, y), Size = new Size(160, 21), Font = font, AccessibleName = "Selected event id" };
            _eventIdBox.TextChanged += (s, e) => { _selectedEventId = _eventIdBox.Text.Trim(); };
            page.Controls.Add(_eventIdBox);

            var loadBtn = new Button { Text = "&Load Fields", Location = new Point(left + 310, y - 1), Size = new Size(100, 23), Font = font, AccessibleName = "Load contest fields" };
            loadBtn.Click += (s, e) => LoadSelectedRuleset(_eventIdBox.Text.Trim());
            page.Controls.Add(loadBtn);
            y += 32;

            var runModeLabel = new Label { Text = "Operating style:", AutoSize = true, Location = new Point(left, y + 3), Font = font, TabStop = false };
            page.Controls.Add(runModeLabel);
            _runModeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(left + 140, y), Size = new Size(160, 21), Font = font, AccessibleName = "Operating style" };
            _runModeCombo.Items.Add("Run (auto-CQ)");
            _runModeCombo.Items.Add("Search & Pounce");
            _runModeCombo.SelectedIndex = 0;
            page.Controls.Add(_runModeCombo);
            y += 32;

            _entryFieldsPanel = new Panel
            {
                Location = new Point(left, y),
                Size = new Size(660, 140),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                AutoScroll = true,
                AccessibleName = "",
                AccessibleRole = AccessibleRole.None,
            };
            page.Controls.Add(_entryFieldsPanel);
            y += 146;

            _configStatusLabel = new Label
            {
                Location = new Point(left, y),
                Size = new Size(660, 20),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = font,
                AutoSize = false,
                TabStop = false,
                AccessibleName = "Contest configuration status",
            };
            page.Controls.Add(_configStatusLabel);

            return page;
        }

        private void RefreshEventList()
        {
            var events = _contestClient.ListEvents(out string error);
            if (events == null)
            {
                _eventStatusLabel.Text = "Could not reach the engine: " + error;
                return;
            }
            _events = events;
            _eventList.Items.Clear();
            foreach (var ev in events.OrderBy(e => e.EventId))
            {
                var level = ContestSupportLevels.Get(ev.EventId, eventIdKnownToNexus: true);
                _eventList.Items.Add($"{ev.EventId} ({ev.ContestId}) - {ContestSupportLevels.Label(level)}");
            }
            _eventStatusLabel.Text = $"{events.Count} contest(s) from Nexus's current rules table.";
        }

        private void LoadSelectedRuleset(string eventId)
        {
            _selectedEventId = eventId;
            _selectedRuleset = _contestClient.GetRuleset(eventId, out string error);
            _entryFieldsPanel.Controls.Clear();
            _entryControls.Clear();
            if (_selectedRuleset == null)
            {
                _configStatusLabel.Text = "Could not load rules for " + eventId + ": " + error;
                return;
            }
            int y = 4;
            foreach (var field in _selectedRuleset.Fields)
            {
                var label = new Label { Text = (field.Label ?? field.Key) + ":", AutoSize = true, Location = new Point(4, y + 3), Font = Font, TabStop = false };
                _entryFieldsPanel.Controls.Add(label);
                var control = ContestFieldControlFactory.Create(field, Font);
                control.Location = new Point(160, y);
                control.Size = control is Label ? control.Size : new Size(140, 21);
                _entryFieldsPanel.Controls.Add(control);
                _entryControls.Add((field, control));
                y += 28;
            }

            // Prefill CLASS/SECTION from this profile's own saved defaults for this contest
            // (ContestConfigStore -- the operator's usual/last-used values), never overwriting a
            // field this contest doesn't actually have. SECTION additionally falls back to the
            // operator's own ARRL/RAC Section from Station & Operator when there is no saved
            // value yet -- "automatically obtain... other applicable identity/location values"
            // (the one exchange value CONTEST_ENTER actually accepts that also has a direct
            // Station & Operator equivalent; CLASS has none, and category operator/power/
            // assisted/station are contest-specific operating choices, not station identity).
            var defaults = ContestConfigStore.Load(eventId);
            string sectionFallback = _station()?.ArrlSection ?? "";
            foreach (var (field, control) in _entryControls)
            {
                if (string.Equals(field.Key, "CLASS", StringComparison.OrdinalIgnoreCase))
                    ContestFieldControlFactory.WriteValue(control, defaults.Class);
                else if (string.Equals(field.Key, "SECTION", StringComparison.OrdinalIgnoreCase))
                    ContestFieldControlFactory.WriteValue(control,
                        !string.IsNullOrWhiteSpace(defaults.Section) ? defaults.Section : sectionFallback);
            }
            _runModeCombo.SelectedIndex = string.Equals(defaults.RunMode, "sp", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

            _configStatusLabel.Text = $"Loaded {_selectedRuleset.Fields.Count} exchange field(s) for {eventId}. Not yet entered.";
        }

        // ── Active Contest ────────────────────────────────────────────────────────

        private Panel BuildActiveContestPage(Font font)
        {
            var page = MakePage();
            int y = 8;
            const int left = 8;

            _enterButton = new Button { Text = "E&nter Contest", Location = new Point(left, y), Size = new Size(120, 26), Font = font, AccessibleName = "Enter contest" };
            _enterButton.Click += (s, e) => DoEnter();
            page.Controls.Add(_enterButton);

            _exitButton = new Button { Text = "E&xit Contest", Location = new Point(left + 130, y), Size = new Size(120, 26), Font = font, AccessibleName = "Exit contest", Enabled = false };
            _exitButton.Click += (s, e) => DoExit();
            page.Controls.Add(_exitButton);
            y += 34;

            _sessionStatusLabel = new Label
            {
                Location = new Point(left, y),
                Size = new Size(660, 40),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = font,
                AutoSize = false,
                TabStop = false,
                AccessibleName = "Current contest and session status",
            };
            page.Controls.Add(_sessionStatusLabel);
            y += 44;

            _rebuildButton = new Button { Text = "&Recalculate Score", Location = new Point(left, y), Size = new Size(150, 26), Font = font, AccessibleName = "Recalculate score", Enabled = false };
            _rebuildButton.Click += (s, e) => DoRebuild();
            page.Controls.Add(_rebuildButton);
            y += 34;

            _scoreLabel = new Label
            {
                Location = new Point(left, y),
                Size = new Size(660, 20),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = font,
                AutoSize = false,
                TabStop = false,
                AccessibleName = "Score",
            };
            page.Controls.Add(_scoreLabel);
            y += 24;

            // The mode-eligibility advisory surface (Winter Field Day and similar) -- Nexus's own
            // warning text, shown accessibly, never blocking. Empty when there is nothing to say
            // -- see RefreshSessionStatus.
            _warningLabel = new Label
            {
                Location = new Point(left, y),
                Size = new Size(660, 40),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = font,
                AutoSize = false,
                TabStop = false,
                ForeColor = Color.DarkOrange,
                AccessibleName = "Contest advisory",
            };
            page.Controls.Add(_warningLabel);

            return page;
        }

        private void DoEnter()
        {
            string missing = RequireStationInfo();
            if (missing != null)
            {
                _sessionStatusLabel.Text = missing;
                return;
            }
            if (_selectedRuleset == null || string.IsNullOrEmpty(_selectedEventId))
            {
                _sessionStatusLabel.Text = "Load a contest's fields first, on Select and Configure.";
                return;
            }
            string runMode = _runModeCombo.SelectedIndex == 1 ? "sp" : "run";
            string GetField(string key) => _entryControls.Where(c => c.Field.Key == key).Select(c => ContestFieldControlFactory.ReadValue(c.Control)).FirstOrDefault() ?? "";

            string classValue = GetField("CLASS");
            string sectionValue = GetField("SECTION");

            var result = _contestClient.Enter(
                _selectedEventId, runMode, _myCall(), _myGrid(), ResolvedOperatorCall(),
                classValue, sectionValue,
                categoryOperator: "", categoryPower: "", categoryAssisted: "", categoryStation: "",
                out string error);
            if (result == null)
            {
                _sessionStatusLabel.Text = "Could not enter contest: " + error;
                return;
            }
            _workflow()?.OnSessionEntered(result.SessionInstanceId, _selectedEventId, _selectedRuleset.ContestId);

            // Saved only now -- a successful CONTEST_ENTER -- never during an unrelated Options
            // save (see ContestConfigStore's own header comment).
            ContestConfigStore.Save(_selectedEventId, new ContestEntryDefaults
            {
                Class = classValue,
                Section = sectionValue,
                RunMode = runMode,
            });

            _enterButton.Enabled = false;
            _exitButton.Enabled = true;
            _rebuildButton.Enabled = true;
            _exportCabrilloButton.Enabled = true;
            _exportAdifButton.Enabled = true;
            _sessionStatusLabel.Text = $"Entered {_selectedEventId}, session instance {result.SessionInstanceId}.";
        }

        private void DoExit()
        {
            _contestClient.Exit(out string error);
            _workflow()?.OnSessionExited();
            _enterButton.Enabled = true;
            _exitButton.Enabled = false;
            _rebuildButton.Enabled = false;
            _exportCabrilloButton.Enabled = false;
            _exportAdifButton.Enabled = false;
            _sessionStatusLabel.Text = error == null ? "Contest session exited." : "Exit reported: " + error;
        }

        private void RefreshSessionStatus()
        {
            var wf = _workflow();
            if (wf == null || !wf.IsSessionActive)
                return;

            // Reconciliation itself runs on Controller's own persistent timer, independent of
            // whether this window is open -- same "keep running regardless of the window"
            // precedent as OtaSpotsWindow's live feeds (see ARCHITECTURE.md). This tick (and
            // every visit to the Active Contest category) only refreshes what's displayed here.

            // Winter Field Day / advisory-mode warn-and-continue: if the selected ruleset bans
            // the mode Jimmy currently operates (FT8/FT4), show Nexus's own advisory here --
            // never block entry or logging (banned_modes is advisory-only in Nexus's own design,
            // never an enforcement input; see ARCHITECTURE.md's Winter Field Day note).
            if (_selectedRuleset != null && _selectedRuleset.BannedModes != null &&
                (_selectedRuleset.BannedModes.Contains("FT8") || _selectedRuleset.BannedModes.Contains("FT4")))
            {
                _warningLabel.Text = $"Note: {_selectedEventId} lists FT8/FT4 among modes not credited by the sponsor's rules for this event. Contacts are still logged normally; Nexus does not block them.";
            }
            else
            {
                _warningLabel.Text = "";
            }
        }

        private void DoRebuild()
        {
            var wf = _workflow();
            if (wf == null || !wf.IsSessionActive)
            {
                _scoreLabel.Text = "No active session to recalculate.";
                return;
            }
            var result = wf.RebuildScoreAndExport(out string error);
            _scoreLabel.Text = result != null
                ? $"Recalculated: {result.QsoCount} QSOs, {result.Points} points."
                : "Recalculate failed: " + error;
        }

        // ── Manual Contact ────────────────────────────────────────────────────────

        private Panel BuildManualContactPage(Font font)
        {
            var page = MakePage();
            int y = 8;
            const int left = 8;

            var instr = new Label
            {
                Text = "Log a contact made by any means (voice, CW, another rig, or a contest Jimmy doesn't automate). For a contest Nexus knows, its own validation/duplicate/scoring apply. For any other contest, this is a plain, unvalidated, unscored record.",
                AutoSize = false,
                Location = new Point(left, y),
                Size = new Size(660, 48),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = font,
                TabStop = false,
            };
            page.Controls.Add(instr);
            y += 52;

            // "Callsign" alone was found live to read ambiguously -- easily misheard/misread as
            // asking for the OPERATOR's own callsign (which Jimmy already obtains automatically
            // from Station & Operator and never asks for here). This is always the OTHER
            // station's callsign.
            var callLabel = new Label { Text = "Contacted station callsign:", AutoSize = true, Location = new Point(left, y + 3), Font = font, TabStop = false };
            page.Controls.Add(callLabel);
            _manualCallBox = new TextBox { Location = new Point(left + 190, y), Size = new Size(120, 21), Font = font, AccessibleName = "Contacted station callsign" };
            page.Controls.Add(_manualCallBox);
            y += 30;

            var bandLabel = new Label { Text = "Band:", AutoSize = true, Location = new Point(left, y + 3), Font = font, TabStop = false };
            page.Controls.Add(bandLabel);
            _manualBandBox = new TextBox { Location = new Point(left + 190, y), Size = new Size(70, 21), Font = font, AccessibleName = "Band" };
            page.Controls.Add(_manualBandBox);

            var modeLabel = new Label { Text = "Mode:", AutoSize = true, Location = new Point(left + 280, y + 3), Font = font, TabStop = false };
            page.Controls.Add(modeLabel);
            _manualModeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Location = new Point(left + 330, y), Size = new Size(90, 21), Font = font, AccessibleName = "Mode" };
            _manualModeCombo.Items.AddRange(new object[] { "FT8", "FT4", "CW", "SSB", "RTTY" });
            page.Controls.Add(_manualModeCombo);
            y += 30;

            var contestLabel = new Label { Text = "Contest:", AutoSize = true, Location = new Point(left, y + 3), Font = font, TabStop = false };
            page.Controls.Add(contestLabel);
            _manualContestCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(left + 190, y), Size = new Size(200, 21), Font = font, AccessibleName = "Contest, from Nexus's known list" };
            _manualContestCombo.SelectedIndexChanged += (s, e) => LoadManualFields();
            page.Controls.Add(_manualContestCombo);

            var freeLabel = new Label { Text = "Or, contest not listed:", AutoSize = true, Location = new Point(left + 400, y + 3), Font = font, TabStop = false };
            page.Controls.Add(freeLabel);
            _manualContestFreeTextBox = new TextBox { Location = new Point(left + 540, y), Size = new Size(120, 21), Anchor = AnchorStyles.Top | AnchorStyles.Left, Font = font, AccessibleName = "Contest name, not in Nexus's list -- unvalidated and unscored" };
            page.Controls.Add(_manualContestFreeTextBox);
            y += 32;

            _manualFieldsPanel = new Panel
            {
                Location = new Point(left, y),
                Size = new Size(660, 90),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                AutoScroll = true,
                AccessibleName = "",
                AccessibleRole = AccessibleRole.None,
            };
            page.Controls.Add(_manualFieldsPanel);
            y += 96;

            var logBtn = new Button { Text = "&Log Contact", Location = new Point(left, y), Size = new Size(120, 26), Font = font, AccessibleName = "Log contact" };
            logBtn.Click += (s, e) => DoManualLog();
            page.Controls.Add(logBtn);
            y += 32;

            _manualStatusLabel = new Label
            {
                Location = new Point(left, y),
                Size = new Size(660, 20),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = font,
                AutoSize = false,
                TabStop = false,
                AccessibleName = "Manual entry status",
            };
            page.Controls.Add(_manualStatusLabel);

            return page;
        }

        private void PopulateManualContestCombo()
        {
            if (_manualContestCombo.Items.Count > 0) return;
            var events = _contestClient.ListEvents(out _);
            if (events != null)
                foreach (var ev in events.OrderBy(e => e.EventId))
                    _manualContestCombo.Items.Add(ev.EventId);
        }

        private void LoadManualFields()
        {
            _manualFieldsPanel.Controls.Clear();
            _manualControls.Clear();
            if (_manualContestCombo.SelectedItem == null) return;
            string eventId = _manualContestCombo.SelectedItem.ToString();
            var ruleset = _contestClient.GetRuleset(eventId, out string error);
            if (ruleset == null) { _manualStatusLabel.Text = "Could not load fields: " + error; return; }
            int y = 4;
            foreach (var field in ruleset.Fields)
            {
                var label = new Label { Text = (field.Label ?? field.Key) + ":", AutoSize = true, Location = new Point(4, y + 3), Font = Font, TabStop = false };
                _manualFieldsPanel.Controls.Add(label);
                var control = ContestFieldControlFactory.Create(field, Font);
                control.Location = new Point(160, y);
                control.Size = control is Label ? control.Size : new Size(140, 21);
                _manualFieldsPanel.Controls.Add(control);
                _manualControls.Add((field, control));
                y += 28;
            }
        }

        private void DoManualLog()
        {
            string missing = RequireStationInfo();
            if (missing != null)
            {
                _manualStatusLabel.Text = missing;
                return;
            }

            string call = _manualCallBox.Text.Trim().ToUpperInvariant();
            string band = _manualBandBox.Text.Trim();
            string mode = _manualModeCombo.Text.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(call))
            {
                _manualStatusLabel.Text = "Enter the contacted station's callsign first.";
                return;
            }

            string knownEventId = _manualContestCombo.SelectedItem?.ToString();
            bool nexusKnown = !string.IsNullOrEmpty(knownEventId) && _manualControls.Count > 0;

            if (nexusKnown && _workflow() != null && _workflow().IsSessionActive)
            {
                var fields = _manualControls.Select(c => new List<string> { c.Field.Key, ContestFieldControlFactory.ReadValue(c.Control) }).ToList();
                bool logged = _contestClient.LogManual(call, fields, mode, "", out string error);
                if (error != null) { _manualStatusLabel.Text = "Could not log: " + error; return; }
                _manualStatusLabel.Text = logged
                    ? $"Logged {call} via Nexus's own validation for {knownEventId}."
                    : $"{call} was refused as a duplicate by Nexus's own dupe rule for {knownEventId}.";
                return;
            }

            // Not Nexus-known (or no active session for it): a plain, local, unvalidated,
            // unscored record -- still a real contact, clearly labeled as such.
            string contestTag = !string.IsNullOrWhiteSpace(_manualContestFreeTextBox.Text)
                ? _manualContestFreeTextBox.Text.Trim()
                : (knownEventId ?? "");
            using (ILogbookService db = new LogbookDb(_dbPath()))
            {
                var now = DateTime.UtcNow;
                string qsoDate = now.ToString("yyyyMMdd");
                string timeOn = now.ToString("HHmmss");
                string dedupKey = AdifImporter.BuildDedupKey(call, band, mode, qsoDate, timeOn);
                db.Upsert(call, band, mode, qsoDate, timeOn, timeOn,
                    freqHz: 0, rstSent: "", rstRcvd: "",
                    state: "", country: "", dxcc: 0, cqZone: 0,
                    grid: "", name: "", comment: "Manual contest entry, unvalidated: " + contestTag, txPwr: "",
                    operatorCall: ResolvedOperatorCall(), stationCall: _myCall(), myGrid: _myGrid(),
                    lotwQslSent: "", lotwQslRcvd: "", qrzQslSent: "", qrzQslRcvd: "",
                    source: "MANUAL", sourceQsoId: "", dedupKey: dedupKey,
                    continent: "", ituZone: 0, county: "", iota: "",
                    sig: "", sigInfo: "", mySig: "", mySigInfo: "",
                    darcDok: "", wpxPrefix: "", exchangeSent: "", exchangeRcvd: "");
                var qsoId = db.GetIdByDedupKey(dedupKey);
                if (qsoId.HasValue && !string.IsNullOrEmpty(contestTag))
                    db.SetContestAssociation(qsoId.Value, contestTag, "");
            }
            _manualStatusLabel.Text = $"Logged {call} as a plain, unvalidated, unscored contact" +
                (string.IsNullOrEmpty(contestTag) ? "." : $" (contest: {contestTag}, not supported by Nexus -- not validated or scored).");
        }

        // ── Export ────────────────────────────────────────────────────────────────

        private Panel BuildExportPage(Font font)
        {
            var page = MakePage();
            int y = 8;
            const int left = 8;

            _exportCabrilloButton = new Button { Text = "Export &Cabrillo...", Location = new Point(left, y), Size = new Size(140, 26), Font = font, AccessibleName = "Export Cabrillo", Enabled = false };
            _exportCabrilloButton.Click += (s, e) => DoExport("cabrillo");
            page.Controls.Add(_exportCabrilloButton);

            _exportAdifButton = new Button { Text = "Export &ADIF...", Location = new Point(left + 150, y), Size = new Size(140, 26), Font = font, AccessibleName = "Export ADIF", Enabled = false };
            _exportAdifButton.Click += (s, e) => DoExport("adif");
            page.Controls.Add(_exportAdifButton);
            y += 34;

            _exportStatusLabel = new Label
            {
                Location = new Point(left, y),
                Size = new Size(660, 40),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = font,
                AutoSize = false,
                TabStop = false,
                AccessibleName = "Export status",
            };
            page.Controls.Add(_exportStatusLabel);

            return page;
        }

        private void DoExport(string format)
        {
            var wf = _workflow();
            if (wf == null || !wf.IsSessionActive)
            {
                _exportStatusLabel.Text = "No active session to export.";
                return;
            }
            var rebuild = wf.RebuildScoreAndExport(out string rebuildError);
            if (rebuild == null)
            {
                _exportStatusLabel.Text = "Export needs a successful recalculation first: " + rebuildError;
                return;
            }
            _scoreLabel.Text = $"Recalculated: {rebuild.QsoCount} QSOs, {rebuild.Points} points.";
            var station = _station();
            string text = _contestClient.Export(format, station?.OperatorName ?? "", station?.ContestEmail ?? "", out string error);
            if (text == null)
            {
                _exportStatusLabel.Text = "Export failed: " + error;
                return;
            }
            using (var dlg = new SaveFileDialog
            {
                Filter = format == "cabrillo" ? "Cabrillo files (*.log)|*.log|All files (*.*)|*.*" : "ADIF files (*.adi)|*.adi|All files (*.*)|*.*",
                FileName = (_selectedEventId ?? "contest") + (format == "cabrillo" ? ".log" : ".adi"),
            })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    System.IO.File.WriteAllText(dlg.FileName, text);
                    _exportStatusLabel.Text = "Exported to " + dlg.FileName;
                }
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        // Same pure-layout-container reasoning as LogbookWindow.MakePage() -- AccessibleRole.None
        // + empty AccessibleName here, overwritten with a real name/Grouping role per page in
        // BuildUi once CategoryListNav.Wire makes that safe (see this class's own header comment).
        private static Panel MakePage()
        {
            return new Panel
            {
                Dock           = DockStyle.Fill,
                AutoScroll     = true,
                AccessibleName = "",
                AccessibleRole = AccessibleRole.None,
            };
        }
    }
}
