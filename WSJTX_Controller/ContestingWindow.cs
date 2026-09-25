using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Nexus contesting foundation, phases 6-8: the accessible Contesting workspace. Non-modal
    // singleton (Show(), not ShowDialog()), same pattern as OtaSpotsWindow/LogbookWindow -- an
    // operator running a contest wants this visible alongside normal operation.
    //
    // Every list here is a single-column ListBox with pre-formatted row text (never a
    // multi-column ListView) -- OtaSpotsWindow's own comment documents why: a live-tested NVDA
    // gap with multi-column ListView that a plain ListBox does not have. Every status/warning
    // surface is exactly ONE label, updated in place -- never duplicated across the window, so
    // nothing is announced twice (the same lesson OtaSpotsWindow's own RefreshActiveTab fix
    // encodes). No MessageBox anywhere in this file: a warning from Nexus (e.g. Winter Field
    // Day's mode advisory) is shown as status text the operator can read on their own schedule,
    // never a focus-stealing dialog that interrupts typing an exchange mid-contact.
    public class ContestingWindow : Form
    {
        private readonly ContestClient _contestClient = new ContestClient();
        private readonly Func<string> _dbPath;
        private readonly Func<string> _myCall;
        private readonly Func<string> _myGrid;
        private readonly Func<string> _operatorCall;
        private readonly Func<ContestWorkflow> _workflow;
        private readonly Func<StationSettings> _station;

        private readonly TabControl _tabs;
        private readonly System.Windows.Forms.Timer _statusTimer;

        // ── Select Contest tab ──────────────────────────────────────────────────
        private ListBox _eventList;
        private Label _eventStatusLabel;
        private List<ContestEventListEntry> _events = new List<ContestEventListEntry>();

        // ── Active Session tab ──────────────────────────────────────────────────
        private Label _sessionStatusLabel;
        private Label _sessionWarningLabel;
        private ComboBox _runModeCombo;
        private Panel _entryFieldsPanel;
        private Button _enterButton, _exitButton, _rebuildButton, _exportCabrilloButton, _exportAdifButton;
        private readonly List<(ContestField Field, Control Control)> _entryControls = new List<(ContestField, Control)>();
        private ContestRuleset _selectedRuleset;
        private string _selectedEventId;

        // ── Manual Entry tab ─────────────────────────────────────────────────────
        private TextBox _manualCallBox, _manualBandBox, _manualContestFreeTextBox;
        private ComboBox _manualModeCombo, _manualContestCombo;
        private Panel _manualFieldsPanel;
        private readonly List<(ContestField Field, Control Control)> _manualControls = new List<(ContestField, Control)>();
        private Label _manualStatusLabel;

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
            Size = new Size(760, 560);
            Font = new Font("Microsoft Sans Serif", 9F);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            _tabs = new TabControl { Dock = DockStyle.Fill };
            _tabs.TabPages.Add(BuildSelectContestTab());
            _tabs.TabPages.Add(BuildActiveSessionTab());
            _tabs.TabPages.Add(BuildManualEntryTab());
            Controls.Add(_tabs);

            _statusTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _statusTimer.Tick += (s, e) => RefreshSessionStatus();

            Load += (s, e) => { RefreshEventList(); RefreshSessionStatus(); _statusTimer.Start(); };
            FormClosed += (s, e) => { _statusTimer.Stop(); };
        }

        // ── Select Contest ──────────────────────────────────────────────────────

        private TabPage BuildSelectContestTab()
        {
            var page = new TabPage("Select Contest");
            var font = Font;

            var instr = new Label
            {
                Text = "Contests Nexus can validate, score, and export. Support level is Jimmy's own assessment of what it can do for each one -- not a claim about Nexus.",
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 40,
                Font = font,
                TabStop = false,
            };
            page.Controls.Add(instr);

            _eventList = new ListBox { Dock = DockStyle.Fill, Font = font, AccessibleName = "Contests" };
            page.Controls.Add(_eventList);
            _eventList.BringToFront();

            var refreshBtn = new Button { Text = "&Refresh List", Dock = DockStyle.Bottom, Font = font, AccessibleName = "Refresh contest list" };
            refreshBtn.Click += (s, e) => RefreshEventList();
            page.Controls.Add(refreshBtn);

            _eventStatusLabel = new Label { Dock = DockStyle.Bottom, Height = 20, Font = font, AutoSize = false, TabStop = false };
            page.Controls.Add(_eventStatusLabel);

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

        // ── Active Session ──────────────────────────────────────────────────────

        private TabPage BuildActiveSessionTab()
        {
            var page = new TabPage("Active Session");
            var font = Font;
            int y = 8;
            const int left = 8;

            var eventLabel = new Label { Text = "Selected event id:", AutoSize = true, Location = new Point(left, y + 3), Font = font, TabStop = false };
            page.Controls.Add(eventLabel);
            var eventBox = new TextBox { Location = new Point(left + 140, y), Size = new Size(160, 21), Font = font, AccessibleName = "Selected event id" };
            eventBox.TextChanged += (s, e) => { _selectedEventId = eventBox.Text.Trim(); };
            page.Controls.Add(eventBox);

            var loadBtn = new Button { Text = "&Load Fields", Location = new Point(left + 310, y - 1), Size = new Size(100, 23), Font = font, AccessibleName = "Load contest fields" };
            loadBtn.Click += (s, e) => LoadSelectedRuleset(eventBox.Text.Trim());
            page.Controls.Add(loadBtn);
            y += 32;

            var runModeLabel = new Label { Text = "Operating style:", AutoSize = true, Location = new Point(left, y + 3), Font = font, TabStop = false };
            page.Controls.Add(runModeLabel);
            _runModeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(left + 140, y), Size = new Size(160, 21), Font = font, AccessibleName = "Operating style" };
            _runModeCombo.Items.Add("Run (auto-CQ)");
            _runModeCombo.Items.Add("Search & Pounce");
            _runModeCombo.SelectedIndex = 0;
            page.Controls.Add(_runModeCombo);
            y += 36;

            _entryFieldsPanel = new Panel { Location = new Point(left, y), Size = new Size(700, 140), AutoScroll = true };
            page.Controls.Add(_entryFieldsPanel);
            y += 148;

            _enterButton = new Button { Text = "E&nter Contest", Location = new Point(left, y), Size = new Size(120, 26), Font = font, AccessibleName = "Enter contest" };
            _enterButton.Click += (s, e) => DoEnter();
            page.Controls.Add(_enterButton);

            _exitButton = new Button { Text = "E&xit Contest", Location = new Point(left + 130, y), Size = new Size(120, 26), Font = font, AccessibleName = "Exit contest", Enabled = false };
            _exitButton.Click += (s, e) => DoExit();
            page.Controls.Add(_exitButton);

            _rebuildButton = new Button { Text = "&Recalculate Score", Location = new Point(left + 260, y), Size = new Size(140, 26), Font = font, AccessibleName = "Recalculate score" };
            _rebuildButton.Click += (s, e) => DoRebuild();
            page.Controls.Add(_rebuildButton);
            y += 34;

            _exportCabrilloButton = new Button { Text = "Export &Cabrillo...", Location = new Point(left, y), Size = new Size(140, 26), Font = font, AccessibleName = "Export Cabrillo" };
            _exportCabrilloButton.Click += (s, e) => DoExport("cabrillo");
            page.Controls.Add(_exportCabrilloButton);

            _exportAdifButton = new Button { Text = "Export &ADIF...", Location = new Point(left + 150, y), Size = new Size(140, 26), Font = font, AccessibleName = "Export ADIF" };
            _exportAdifButton.Click += (s, e) => DoExport("adif");
            page.Controls.Add(_exportAdifButton);
            y += 34;

            // ONE status surface, updated in place -- never a second control repeating the same
            // text (see this class's own header comment).
            _sessionStatusLabel = new Label { Location = new Point(left, y), Size = new Size(700, 40), Font = font, AutoSize = false, TabStop = false, AccessibleName = "Contest status" };
            page.Controls.Add(_sessionStatusLabel);
            y += 44;

            // The mode-eligibility advisory surface (Winter Field Day and similar) -- Nexus's
            // own warning text, shown accessibly, never blocking. Empty/hidden when there is
            // nothing to say -- see RefreshSessionStatus.
            _sessionWarningLabel = new Label { Location = new Point(left, y), Size = new Size(700, 40), Font = font, AutoSize = false, TabStop = false, ForeColor = Color.DarkOrange, AccessibleName = "Contest advisory" };
            page.Controls.Add(_sessionWarningLabel);

            return page;
        }

        private void LoadSelectedRuleset(string eventId)
        {
            _selectedEventId = eventId;
            _selectedRuleset = _contestClient.GetRuleset(eventId, out string error);
            _entryFieldsPanel.Controls.Clear();
            _entryControls.Clear();
            if (_selectedRuleset == null)
            {
                _sessionStatusLabel.Text = "Could not load rules for " + eventId + ": " + error;
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
            _sessionStatusLabel.Text = $"Loaded {_selectedRuleset.Fields.Count} exchange field(s) for {eventId}. Not yet entered.";
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

            var result = _contestClient.Enter(
                _selectedEventId, runMode, _myCall(), _myGrid(), _operatorCall(),
                GetField("CLASS"), GetField("SECTION"),
                categoryOperator: "", categoryPower: "", categoryAssisted: "", categoryStation: "",
                out string error);
            if (result == null)
            {
                _sessionStatusLabel.Text = "Could not enter contest: " + error;
                return;
            }
            _workflow()?.OnSessionEntered(result.SessionInstanceId, _selectedEventId, _selectedRuleset.ContestId);
            _enterButton.Enabled = false;
            _exitButton.Enabled = true;
            _sessionStatusLabel.Text = $"Entered {_selectedEventId}, session instance {result.SessionInstanceId}.";
        }

        private void DoExit()
        {
            _contestClient.Exit(out string error);
            _workflow()?.OnSessionExited();
            _enterButton.Enabled = true;
            _exitButton.Enabled = false;
            _sessionStatusLabel.Text = error == null ? "Contest session exited." : "Exit reported: " + error;
        }

        private void DoRebuild()
        {
            var wf = _workflow();
            if (wf == null || !wf.IsSessionActive)
            {
                _sessionStatusLabel.Text = "No active session to recalculate.";
                return;
            }
            var result = wf.RebuildScoreAndExport(out string error);
            _sessionStatusLabel.Text = result != null
                ? $"Recalculated: {result.QsoCount} QSOs, {result.Points} points."
                : "Recalculate failed: " + error;
        }

        private void DoExport(string format)
        {
            var wf = _workflow();
            if (wf == null || !wf.IsSessionActive)
            {
                _sessionStatusLabel.Text = "No active session to export.";
                return;
            }
            var rebuild = wf.RebuildScoreAndExport(out string rebuildError);
            if (rebuild == null)
            {
                _sessionStatusLabel.Text = "Export needs a successful recalculation first: " + rebuildError;
                return;
            }
            var station = _station();
            string text = _contestClient.Export(format, station?.OperatorName ?? "", station?.ContestEmail ?? "", out string error);
            if (text == null)
            {
                _sessionStatusLabel.Text = "Export failed: " + error;
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
                    _sessionStatusLabel.Text = "Exported to " + dlg.FileName;
                }
            }
        }

        private void RefreshSessionStatus()
        {
            var wf = _workflow();
            if (wf == null || !wf.IsSessionActive)
            {
                return;
            }
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
                _sessionWarningLabel.Text = $"Note: {_selectedEventId} lists FT8/FT4 among modes not credited by the sponsor's rules for this event. Contacts are still logged normally; Nexus does not block them.";
            }
            else
            {
                _sessionWarningLabel.Text = "";
            }
        }

        // ── Manual Entry ─────────────────────────────────────────────────────────

        private TabPage BuildManualEntryTab()
        {
            var page = new TabPage("Manual Entry");
            var font = Font;
            int y = 8;
            const int left = 8;

            var instr = new Label
            {
                Text = "Log a contact made by any means (voice, CW, another rig, or a contest Jimmy doesn't automate). For a contest Nexus knows, its own validation/duplicate/scoring apply. For any other contest, this is a plain, unvalidated, unscored record.",
                AutoSize = false, Dock = DockStyle.Top, Height = 48, Font = font, TabStop = false,
            };
            page.Controls.Add(instr);

            var callLabel = new Label { Text = "Callsign:", AutoSize = true, Location = new Point(left, y + 56), Font = font, TabStop = false };
            page.Controls.Add(callLabel);
            _manualCallBox = new TextBox { Location = new Point(left + 100, y + 53), Size = new Size(120, 21), Font = font, AccessibleName = "Callsign" };
            page.Controls.Add(_manualCallBox);

            var bandLabel = new Label { Text = "Band:", AutoSize = true, Location = new Point(left + 240, y + 56), Font = font, TabStop = false };
            page.Controls.Add(bandLabel);
            _manualBandBox = new TextBox { Location = new Point(left + 290, y + 53), Size = new Size(70, 21), Font = font, AccessibleName = "Band" };
            page.Controls.Add(_manualBandBox);

            var modeLabel = new Label { Text = "Mode:", AutoSize = true, Location = new Point(left + 380, y + 56), Font = font, TabStop = false };
            page.Controls.Add(modeLabel);
            _manualModeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Location = new Point(left + 430, y + 53), Size = new Size(90, 21), Font = font, AccessibleName = "Mode" };
            _manualModeCombo.Items.AddRange(new object[] { "FT8", "FT4", "CW", "SSB", "RTTY" });
            page.Controls.Add(_manualModeCombo);
            y += 84;

            var contestLabel = new Label { Text = "Contest:", AutoSize = true, Location = new Point(left, y + 3), Font = font, TabStop = false };
            page.Controls.Add(contestLabel);
            _manualContestCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(left + 100, y), Size = new Size(200, 21), Font = font, AccessibleName = "Contest, from Nexus's known list" };
            _manualContestCombo.SelectedIndexChanged += (s, e) => LoadManualFields();
            page.Controls.Add(_manualContestCombo);

            var freeLabel = new Label { Text = "Or, contest not listed:", AutoSize = true, Location = new Point(left + 320, y + 3), Font = font, TabStop = false };
            page.Controls.Add(freeLabel);
            _manualContestFreeTextBox = new TextBox { Location = new Point(left + 470, y), Size = new Size(160, 21), Font = font, AccessibleName = "Contest name, not in Nexus's list -- unvalidated and unscored" };
            page.Controls.Add(_manualContestFreeTextBox);
            y += 32;

            _manualFieldsPanel = new Panel { Location = new Point(left, y), Size = new Size(700, 120), AutoScroll = true };
            page.Controls.Add(_manualFieldsPanel);
            y += 128;

            var logBtn = new Button { Text = "&Log Contact", Location = new Point(left, y), Size = new Size(120, 26), Font = font, AccessibleName = "Log contact" };
            logBtn.Click += (s, e) => DoManualLog();
            page.Controls.Add(logBtn);
            y += 34;

            _manualStatusLabel = new Label { Location = new Point(left, y), Size = new Size(700, 40), Font = font, AutoSize = false, TabStop = false, AccessibleName = "Manual entry status" };
            page.Controls.Add(_manualStatusLabel);

            page.Enter += (s, e) =>
            {
                if (_manualContestCombo.Items.Count == 0)
                {
                    var events = _contestClient.ListEvents(out _);
                    if (events != null)
                        foreach (var ev in events.OrderBy(e2 => e2.EventId))
                            _manualContestCombo.Items.Add(ev.EventId);
                }
            };

            return page;
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
    }
}
