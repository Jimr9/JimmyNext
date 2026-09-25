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
    // Live JAWS testing (2026-09-25) found the earlier "Contesting tab inside Logbook Center"
    // design confusing: Logbook Center's own category list, followed by three MORE nested tabs
    // inside the Contesting entry, read poorly. This is back to being its own standalone window,
    // and its three former nested tabs (Select Contest / Active Session / Manual Entry) are now
    // flattened into ONE straightforward top-to-bottom keyboard sequence -- six GroupBoxes, each
    // a real accessible grouping (native GroupBox Text IS its accessible name/Grouping role, no
    // TabControl re-announcement noise), stacked in a single AutoScroll content panel so nothing
    // is nested and Tab order is exactly visual order:
    //   1. Contest selection and support level
    //   2. Contest configuration and generated exchange fields
    //   3. Start/stop and active-session status
    //   4. Score, warnings, and advisories
    //   5. Manual contact entry
    //   6. Cabrillo and ADIF export
    // Every list here is a single-column ListBox with pre-formatted row text (never a
    // multi-column ListView) -- OtaSpotsWindow's own comment documents why: a live-tested NVDA
    // gap with multi-column ListView that a plain ListBox does not have. Every status/warning
    // surface below is exactly ONE label per concern (one per group), updated in place -- never
    // duplicated -- so nothing is announced twice. No MessageBox anywhere in this file: a warning
    // from Nexus (e.g. Winter Field Day's mode advisory) is shown as status text the operator can
    // read on their own schedule, never a focus-stealing dialog that interrupts typing an
    // exchange mid-contact. Controls that do not currently apply (Exit/Recalculate/Export before
    // a session is active) are disabled, not merely rejected at click time, so JAWS announces
    // them as unavailable rather than the operator discovering that only after activating one.
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

        // ── Group 1: Contest selection and support level ────────────────────────
        private ListBox _eventList;
        private Label _eventStatusLabel;
        private List<ContestEventListEntry> _events = new List<ContestEventListEntry>();

        // ── Group 2: Contest configuration and generated exchange fields ────────
        private TextBox _eventIdBox;
        private ComboBox _runModeCombo;
        private Panel _entryFieldsPanel;
        private Label _configStatusLabel;
        private ContestRuleset _selectedRuleset;
        private string _selectedEventId;
        private readonly List<(ContestField Field, Control Control)> _entryControls = new List<(ContestField, Control)>();

        // ── Group 3: Start/stop and active-session status ───────────────────────
        private Button _enterButton, _exitButton;
        private Label _sessionStatusLabel;

        // ── Group 4: Score, warnings, and advisories ─────────────────────────────
        private Button _rebuildButton;
        private Label _scoreLabel;
        private Label _warningLabel;

        // ── Group 5: Manual contact entry ────────────────────────────────────────
        private TextBox _manualCallBox, _manualBandBox, _manualContestFreeTextBox;
        private ComboBox _manualModeCombo, _manualContestCombo;
        private Panel _manualFieldsPanel;
        private readonly List<(ContestField Field, Control Control)> _manualControls = new List<(ContestField, Control)>();
        private Label _manualStatusLabel;

        // ── Group 6: Cabrillo and ADIF export ────────────────────────────────────
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
            MinimumSize = new Size(640, 420);
            Size = new Size(720, 660);
            Font = new Font("Microsoft Sans Serif", 9F);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true, AccessibleName = "", AccessibleRole = AccessibleRole.None };
            // Each group gets an explicit Location (Y accumulated top-to-bottom below), never
            // Dock=Top stacking -- LogbookWindow.MakePage()'s own comment documents a real,
            // live-confirmed WinForms pitfall where Dock=Top/Dock=Fill add-order and visual/Tab
            // order can disagree. Explicit positioning sidesteps that class of bug entirely: with
            // no TabIndex set on any group (all default to 0), WinForms breaks the tie by
            // Controls-collection add-order, so adding groups 1 -> 6 in this same visual order
            // makes Tab order match visual order exactly, with no ProcessTabKey override needed.
            int y = 8;
            var group1 = BuildSelectGroup(y);    y += group1.Height + 8;
            var group2 = BuildConfigureGroup(y); y += group2.Height + 8;
            var group3 = BuildStartStopGroup(y); y += group3.Height + 8;
            var group4 = BuildScoreGroup(y);     y += group4.Height + 8;
            var group5 = BuildManualEntryGroup(y); y += group5.Height + 8;
            var group6 = BuildExportGroup(y);
            content.Controls.Add(group1);
            content.Controls.Add(group2);
            content.Controls.Add(group3);
            content.Controls.Add(group4);
            content.Controls.Add(group5);
            content.Controls.Add(group6);
            Controls.Add(content);

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
            Shown += (s, e) => _eventList.Focus();
            FormClosed += (s, e) => { _statusTimer.Stop(); };
        }

        // ── Group 1: Contest selection and support level ────────────────────────

        private const int GroupWidth = 680;

        private GroupBox BuildSelectGroup(int y0)
        {
            var box = new GroupBox
            {
                Text = "Contest selection and support level",
                AccessibleName = "Contest selection and support level",
                Location = new Point(8, y0),
                Size = new Size(GroupWidth, 190),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Font,
            };
            int y = 20;
            var instr = new Label
            {
                Text = "Contests Nexus can validate, score, and export. Support level is Jimmy's own assessment of what it can do for each one -- not a claim about Nexus.",
                AutoSize = false,
                Location = new Point(8, y),
                Size = new Size(box.Width - 16, 36),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Font,
                TabStop = false,
            };
            box.Controls.Add(instr);
            y += 40;

            _eventList = new ListBox
            {
                Location = new Point(8, y),
                Size = new Size(box.Width - 16, 90),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Font,
                AccessibleName = "Contests",
            };
            box.Controls.Add(_eventList);
            y += 96;

            var refreshBtn = new Button
            {
                Text = "&Refresh List",
                Location = new Point(8, y),
                Size = new Size(120, 24),
                Font = Font,
                AccessibleName = "Refresh contest list",
            };
            refreshBtn.Click += (s, e) => RefreshEventList();
            box.Controls.Add(refreshBtn);

            _eventStatusLabel = new Label
            {
                Location = new Point(140, y + 3),
                Size = new Size(box.Width - 148, 20),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Font,
                AutoSize = false,
                TabStop = false,
                AccessibleName = "Contest list status",
            };
            box.Controls.Add(_eventStatusLabel);

            return box;
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

        // ── Group 2: Contest configuration and generated exchange fields ────────

        private GroupBox BuildConfigureGroup(int y0)
        {
            var box = new GroupBox
            {
                Text = "Contest configuration and generated exchange fields",
                AccessibleName = "Contest configuration and generated exchange fields",
                Location = new Point(8, y0),
                Size = new Size(GroupWidth, 250),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Font,
            };
            int y = 20;

            var eventLabel = new Label { Text = "Selected event id:", AutoSize = true, Location = new Point(8, y + 3), Font = Font, TabStop = false };
            box.Controls.Add(eventLabel);
            _eventIdBox = new TextBox { Location = new Point(148, y), Size = new Size(160, 21), Font = Font, AccessibleName = "Selected event id" };
            _eventIdBox.TextChanged += (s, e) => { _selectedEventId = _eventIdBox.Text.Trim(); };
            box.Controls.Add(_eventIdBox);

            var loadBtn = new Button { Text = "&Load Fields", Location = new Point(318, y - 1), Size = new Size(100, 23), Font = Font, AccessibleName = "Load contest fields" };
            loadBtn.Click += (s, e) => LoadSelectedRuleset(_eventIdBox.Text.Trim());
            box.Controls.Add(loadBtn);
            y += 32;

            var runModeLabel = new Label { Text = "Operating style:", AutoSize = true, Location = new Point(8, y + 3), Font = Font, TabStop = false };
            box.Controls.Add(runModeLabel);
            _runModeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(148, y), Size = new Size(160, 21), Font = Font, AccessibleName = "Operating style" };
            _runModeCombo.Items.Add("Run (auto-CQ)");
            _runModeCombo.Items.Add("Search & Pounce");
            _runModeCombo.SelectedIndex = 0;
            box.Controls.Add(_runModeCombo);
            y += 32;

            _entryFieldsPanel = new Panel
            {
                Location = new Point(8, y),
                Size = new Size(box.Width - 16, 140),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                AutoScroll = true,
                AccessibleName = "",
                AccessibleRole = AccessibleRole.None,
            };
            box.Controls.Add(_entryFieldsPanel);
            y += 146;

            _configStatusLabel = new Label
            {
                Location = new Point(8, y),
                Size = new Size(box.Width - 16, 20),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Font,
                AutoSize = false,
                TabStop = false,
                AccessibleName = "Contest configuration status",
            };
            box.Controls.Add(_configStatusLabel);

            return box;
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

            // Prefill from this profile's own saved defaults for this contest (ContestConfigStore
            // -- the operator's usual/last-used CLASS/SECTION/run style), never overwriting a
            // field this contest doesn't actually have.
            var defaults = ContestConfigStore.Load(eventId);
            foreach (var (field, control) in _entryControls)
            {
                if (string.Equals(field.Key, "CLASS", StringComparison.OrdinalIgnoreCase))
                    ContestFieldControlFactory.WriteValue(control, defaults.Class);
                else if (string.Equals(field.Key, "SECTION", StringComparison.OrdinalIgnoreCase))
                    ContestFieldControlFactory.WriteValue(control, defaults.Section);
            }
            _runModeCombo.SelectedIndex = string.Equals(defaults.RunMode, "sp", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

            _configStatusLabel.Text = $"Loaded {_selectedRuleset.Fields.Count} exchange field(s) for {eventId}. Not yet entered.";
        }

        // ── Group 3: Start/stop and active-session status ────────────────────────

        private GroupBox BuildStartStopGroup(int y0)
        {
            var box = new GroupBox
            {
                Text = "Start/stop and active-session status",
                AccessibleName = "Start/stop and active-session status",
                Location = new Point(8, y0),
                Size = new Size(GroupWidth, 100),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Font,
            };
            int y = 20;

            _enterButton = new Button { Text = "E&nter Contest", Location = new Point(8, y), Size = new Size(120, 26), Font = Font, AccessibleName = "Enter contest" };
            _enterButton.Click += (s, e) => DoEnter();
            box.Controls.Add(_enterButton);

            _exitButton = new Button { Text = "E&xit Contest", Location = new Point(138, y), Size = new Size(120, 26), Font = Font, AccessibleName = "Exit contest", Enabled = false };
            _exitButton.Click += (s, e) => DoExit();
            box.Controls.Add(_exitButton);
            y += 34;

            _sessionStatusLabel = new Label
            {
                Location = new Point(8, y),
                Size = new Size(box.Width - 16, 40),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Font,
                AutoSize = false,
                TabStop = false,
                AccessibleName = "Active session status",
            };
            box.Controls.Add(_sessionStatusLabel);

            return box;
        }

        private void DoEnter()
        {
            if (_selectedRuleset == null || string.IsNullOrEmpty(_selectedEventId))
            {
                _sessionStatusLabel.Text = "Load a contest's fields first.";
                return;
            }
            string runMode = _runModeCombo.SelectedIndex == 1 ? "sp" : "run";
            string GetField(string key) => _entryControls.Where(c => c.Field.Key == key).Select(c => ContestFieldControlFactory.ReadValue(c.Control)).FirstOrDefault() ?? "";

            string classValue = GetField("CLASS");
            string sectionValue = GetField("SECTION");

            var result = _contestClient.Enter(
                _selectedEventId, runMode, _myCall(), _myGrid(), _operatorCall(),
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
            // precedent as OtaSpotsWindow's live feeds (see ARCHITECTURE.md). This tick only
            // refreshes what's displayed here.

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

        // ── Group 4: Score, warnings, and advisories ─────────────────────────────

        private GroupBox BuildScoreGroup(int y0)
        {
            var box = new GroupBox
            {
                Text = "Score, warnings, and advisories",
                AccessibleName = "Score, warnings, and advisories",
                Location = new Point(8, y0),
                Size = new Size(GroupWidth, 130),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Font,
            };
            int y = 20;

            _rebuildButton = new Button { Text = "&Recalculate Score", Location = new Point(8, y), Size = new Size(150, 26), Font = Font, AccessibleName = "Recalculate score", Enabled = false };
            _rebuildButton.Click += (s, e) => DoRebuild();
            box.Controls.Add(_rebuildButton);
            y += 34;

            _scoreLabel = new Label
            {
                Location = new Point(8, y),
                Size = new Size(box.Width - 16, 20),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Font,
                AutoSize = false,
                TabStop = false,
                AccessibleName = "Score",
            };
            box.Controls.Add(_scoreLabel);
            y += 24;

            // The mode-eligibility advisory surface (Winter Field Day and similar) -- Nexus's own
            // warning text, shown accessibly, never blocking. Empty when there is nothing to say
            // -- see RefreshSessionStatus.
            _warningLabel = new Label
            {
                Location = new Point(8, y),
                Size = new Size(box.Width - 16, 40),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Font,
                AutoSize = false,
                TabStop = false,
                ForeColor = Color.DarkOrange,
                AccessibleName = "Contest advisory",
            };
            box.Controls.Add(_warningLabel);

            return box;
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

        // ── Group 5: Manual contact entry ────────────────────────────────────────

        private GroupBox BuildManualEntryGroup(int y0)
        {
            var box = new GroupBox
            {
                Text = "Manual contact entry",
                AccessibleName = "Manual contact entry",
                Location = new Point(8, y0),
                Size = new Size(GroupWidth, 300),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Font,
            };
            int y = 20;

            var instr = new Label
            {
                Text = "Log a contact made by any means (voice, CW, another rig, or a contest Jimmy doesn't automate). For a contest Nexus knows, its own validation/duplicate/scoring apply. For any other contest, this is a plain, unvalidated, unscored record.",
                AutoSize = false,
                Location = new Point(8, y),
                Size = new Size(box.Width - 16, 48),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Font,
                TabStop = false,
            };
            box.Controls.Add(instr);
            y += 52;

            var callLabel = new Label { Text = "Callsign:", AutoSize = true, Location = new Point(8, y + 3), Font = Font, TabStop = false };
            box.Controls.Add(callLabel);
            _manualCallBox = new TextBox { Location = new Point(100, y), Size = new Size(120, 21), Font = Font, AccessibleName = "Callsign" };
            box.Controls.Add(_manualCallBox);

            var bandLabel = new Label { Text = "Band:", AutoSize = true, Location = new Point(240, y + 3), Font = Font, TabStop = false };
            box.Controls.Add(bandLabel);
            _manualBandBox = new TextBox { Location = new Point(290, y), Size = new Size(70, 21), Font = Font, AccessibleName = "Band" };
            box.Controls.Add(_manualBandBox);

            var modeLabel = new Label { Text = "Mode:", AutoSize = true, Location = new Point(380, y + 3), Font = Font, TabStop = false };
            box.Controls.Add(modeLabel);
            _manualModeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Location = new Point(430, y), Size = new Size(90, 21), Font = Font, AccessibleName = "Mode" };
            _manualModeCombo.Items.AddRange(new object[] { "FT8", "FT4", "CW", "SSB", "RTTY" });
            box.Controls.Add(_manualModeCombo);
            y += 30;

            var contestLabel = new Label { Text = "Contest:", AutoSize = true, Location = new Point(8, y + 3), Font = Font, TabStop = false };
            box.Controls.Add(contestLabel);
            _manualContestCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(100, y), Size = new Size(200, 21), Font = Font, AccessibleName = "Contest, from Nexus's known list" };
            _manualContestCombo.SelectedIndexChanged += (s, e) => LoadManualFields();
            box.Controls.Add(_manualContestCombo);

            var freeLabel = new Label { Text = "Or, contest not listed:", AutoSize = true, Location = new Point(320, y + 3), Font = Font, TabStop = false };
            box.Controls.Add(freeLabel);
            _manualContestFreeTextBox = new TextBox { Location = new Point(470, y), Size = new Size(180, 21), Font = Font, AccessibleName = "Contest name, not in Nexus's list -- unvalidated and unscored" };
            box.Controls.Add(_manualContestFreeTextBox);
            y += 32;

            _manualFieldsPanel = new Panel
            {
                Location = new Point(8, y),
                Size = new Size(box.Width - 16, 90),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                AutoScroll = true,
                AccessibleName = "",
                AccessibleRole = AccessibleRole.None,
            };
            box.Controls.Add(_manualFieldsPanel);
            y += 96;

            var logBtn = new Button { Text = "&Log Contact", Location = new Point(8, y), Size = new Size(120, 26), Font = Font, AccessibleName = "Log contact" };
            logBtn.Click += (s, e) => DoManualLog();
            box.Controls.Add(logBtn);
            y += 32;

            _manualStatusLabel = new Label
            {
                Location = new Point(8, y),
                Size = new Size(box.Width - 16, 20),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Font,
                AutoSize = false,
                TabStop = false,
                AccessibleName = "Manual entry status",
            };
            box.Controls.Add(_manualStatusLabel);

            return box;
        }

        // Populates the Manual Entry group's own Contest picker -- called once from Load,
        // alongside RefreshEventList. Unlike the former tabbed design (where TabPage.Enter was
        // the natural "operator just reached this section" trigger), every group here is visible
        // at once -- there is no longer a meaningful "not yet visible" moment to lazily hook, so
        // this simply populates eagerly like every other group's own initial content.
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
            string call = _manualCallBox.Text.Trim().ToUpperInvariant();
            string band = _manualBandBox.Text.Trim();
            string mode = _manualModeCombo.Text.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(call))
            {
                _manualStatusLabel.Text = "Enter a callsign first.";
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
                    operatorCall: _operatorCall(), stationCall: _myCall(), myGrid: _myGrid(),
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

        // ── Group 6: Cabrillo and ADIF export ─────────────────────────────────────

        private GroupBox BuildExportGroup(int y0)
        {
            var box = new GroupBox
            {
                Text = "Cabrillo and ADIF export",
                AccessibleName = "Cabrillo and ADIF export",
                Location = new Point(8, y0),
                Size = new Size(GroupWidth, 100),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Font,
            };
            int y = 20;

            _exportCabrilloButton = new Button { Text = "Export &Cabrillo...", Location = new Point(8, y), Size = new Size(140, 26), Font = Font, AccessibleName = "Export Cabrillo", Enabled = false };
            _exportCabrilloButton.Click += (s, e) => DoExport("cabrillo");
            box.Controls.Add(_exportCabrilloButton);

            _exportAdifButton = new Button { Text = "Export &ADIF...", Location = new Point(158, y), Size = new Size(140, 26), Font = Font, AccessibleName = "Export ADIF", Enabled = false };
            _exportAdifButton.Click += (s, e) => DoExport("adif");
            box.Controls.Add(_exportAdifButton);
            y += 34;

            _exportStatusLabel = new Label
            {
                Location = new Point(8, y),
                Size = new Size(box.Width - 16, 40),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Font,
                AutoSize = false,
                TabStop = false,
                AccessibleName = "Export status",
            };
            box.Controls.Add(_exportStatusLabel);

            return box;
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
    }
}
