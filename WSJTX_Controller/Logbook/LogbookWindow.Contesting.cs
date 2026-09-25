using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Nexus contesting foundation: the Contesting tab, ported from the former standalone
    // ContestingWindow (removed -- see git history) into Logbook Center so an operator running a
    // contest has it inside the same accessible workspace as their log, reachable from the
    // existing Contesting hotkey, with no separate top-level window and no main-window button.
    //
    // Every list here is a single-column ListBox with pre-formatted row text (never a
    // multi-column ListView) -- OtaSpotsWindow's own comment documents why: a live-tested NVDA
    // gap with multi-column ListView that a plain ListBox does not have. Every status/warning
    // surface is exactly ONE label, updated in place -- never duplicated across the tab, so
    // nothing is announced twice. No MessageBox anywhere in this file: a warning from Nexus
    // (e.g. Winter Field Day's mode advisory) is shown as status text the operator can read on
    // their own schedule, never a focus-stealing dialog that interrupts typing an exchange
    // mid-contact.
    public partial class LogbookWindow
    {
        private readonly ContestClient _contestClient = new ContestClient();
        private System.Windows.Forms.Timer _contestStatusTimer;

        private Panel _contestingPanel;
        private TabControl _contestSubTabs;

        // ── Select Contest sub-tab ───────────────────────────────────────────────
        private ListBox _contestEventList;
        private Label _contestEventStatusLabel;
        private List<ContestEventListEntry> _contestEvents = new List<ContestEventListEntry>();

        // ── Active Session sub-tab ───────────────────────────────────────────────
        private Label _contestSessionStatusLabel;
        private Label _contestSessionWarningLabel;
        private ComboBox _contestRunModeCombo;
        private Panel _contestEntryFieldsPanel;
        private Button _contestEnterBtn, _contestExitBtn, _contestRebuildBtn, _contestExportCabrilloBtn, _contestExportAdifBtn;
        private readonly List<(ContestField Field, Control Control)> _contestEntryControls = new List<(ContestField, Control)>();
        private ContestRuleset _contestSelectedRuleset;
        private string _contestSelectedEventId;

        // ── Manual Entry sub-tab ─────────────────────────────────────────────────
        private TextBox _contestManualCallBox, _contestManualBandBox, _contestManualContestFreeTextBox;
        private ComboBox _contestManualModeCombo, _contestManualContestCombo;
        private Panel _contestManualFieldsPanel;
        private readonly List<(ContestField Field, Control Control)> _contestManualControls = new List<(ContestField, Control)>();
        private Label _contestManualStatusLabel;

        // ── Tab construction ─────────────────────────────────────────────────────

        private void BuildContestingPage(Font font, Font hfont)
        {
            _contestingPanel = MakePage();

            _contestSubTabs = new TabControl { Dock = DockStyle.Fill, Font = font };
            _contestSubTabs.TabPages.Add(BuildContestSelectTab(font));
            _contestSubTabs.TabPages.Add(BuildContestActiveTab(font));
            _contestSubTabs.TabPages.Add(BuildContestManualTab(font));
            _contestingPanel.Controls.Add(_contestSubTabs);

            // The 2-second display refresh only matters while this window is open (it just
            // redraws _contestSessionWarningLabel from state that already exists elsewhere).
            // Reconciliation/delivery itself runs on Controller's own persistent contestPollTimer,
            // independent of this window -- same "keep running regardless of the window"
            // precedent as OtaSpotsWindow's live feeds (see ARCHITECTURE.md).
            _contestStatusTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _contestStatusTimer.Tick += (s, e) => RefreshContestSessionStatus();
            this.Load += (s, e) => _contestStatusTimer.Start();
            this.FormClosed += (s, e) => _contestStatusTimer.Stop();
        }

        private void PopulateContesting()
        {
            if (_contestEventList.Items.Count == 0)
                RefreshContestEventList();
            RefreshContestSessionStatus();
        }

        // ── Select Contest ───────────────────────────────────────────────────────

        private TabPage BuildContestSelectTab(Font font)
        {
            var page = new TabPage("Select Contest");

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

            _contestEventList = new ListBox { Dock = DockStyle.Fill, Font = font, AccessibleName = "Contests" };
            page.Controls.Add(_contestEventList);
            _contestEventList.BringToFront();

            var refreshBtn = new Button { Text = "&Refresh List", Dock = DockStyle.Bottom, Font = font, AccessibleName = "Refresh contest list" };
            refreshBtn.Click += (s, e) => RefreshContestEventList();
            page.Controls.Add(refreshBtn);

            _contestEventStatusLabel = new Label { Dock = DockStyle.Bottom, Height = 20, Font = font, AutoSize = false, TabStop = false };
            page.Controls.Add(_contestEventStatusLabel);

            return page;
        }

        private void RefreshContestEventList()
        {
            var events = _contestClient.ListEvents(out string error);
            if (events == null)
            {
                _contestEventStatusLabel.Text = "Could not reach the engine: " + error;
                return;
            }
            _contestEvents = events;
            _contestEventList.Items.Clear();
            foreach (var ev in events.OrderBy(e => e.EventId))
            {
                var level = ContestSupportLevels.Get(ev.EventId, eventIdKnownToNexus: true);
                _contestEventList.Items.Add($"{ev.EventId} ({ev.ContestId}) - {ContestSupportLevels.Label(level)}");
            }
            _contestEventStatusLabel.Text = $"{events.Count} contest(s) from Nexus's current rules table.";
        }

        // ── Active Session ───────────────────────────────────────────────────────

        private TabPage BuildContestActiveTab(Font font)
        {
            var page = new TabPage("Active Session");
            int y = 8;
            const int left = 8;

            var eventLabel = new Label { Text = "Selected event id:", AutoSize = true, Location = new Point(left, y + 3), Font = font, TabStop = false };
            page.Controls.Add(eventLabel);
            var eventBox = new TextBox { Location = new Point(left + 140, y), Size = new Size(160, 21), Font = font, AccessibleName = "Selected event id" };
            eventBox.TextChanged += (s, e) => { _contestSelectedEventId = eventBox.Text.Trim(); };
            page.Controls.Add(eventBox);

            var loadBtn = new Button { Text = "&Load Fields", Location = new Point(left + 310, y - 1), Size = new Size(100, 23), Font = font, AccessibleName = "Load contest fields" };
            loadBtn.Click += (s, e) => LoadSelectedContestRuleset(eventBox.Text.Trim());
            page.Controls.Add(loadBtn);
            y += 32;

            var runModeLabel = new Label { Text = "Operating style:", AutoSize = true, Location = new Point(left, y + 3), Font = font, TabStop = false };
            page.Controls.Add(runModeLabel);
            _contestRunModeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(left + 140, y), Size = new Size(160, 21), Font = font, AccessibleName = "Operating style" };
            _contestRunModeCombo.Items.Add("Run (auto-CQ)");
            _contestRunModeCombo.Items.Add("Search & Pounce");
            _contestRunModeCombo.SelectedIndex = 0;
            page.Controls.Add(_contestRunModeCombo);
            y += 36;

            _contestEntryFieldsPanel = new Panel { Location = new Point(left, y), Size = new Size(700, 140), AutoScroll = true };
            page.Controls.Add(_contestEntryFieldsPanel);
            y += 148;

            _contestEnterBtn = new Button { Text = "E&nter Contest", Location = new Point(left, y), Size = new Size(120, 26), Font = font, AccessibleName = "Enter contest" };
            _contestEnterBtn.Click += (s, e) => DoContestEnter();
            page.Controls.Add(_contestEnterBtn);

            _contestExitBtn = new Button { Text = "E&xit Contest", Location = new Point(left + 130, y), Size = new Size(120, 26), Font = font, AccessibleName = "Exit contest", Enabled = false };
            _contestExitBtn.Click += (s, e) => DoContestExit();
            page.Controls.Add(_contestExitBtn);

            _contestRebuildBtn = new Button { Text = "&Recalculate Score", Location = new Point(left + 260, y), Size = new Size(140, 26), Font = font, AccessibleName = "Recalculate score" };
            _contestRebuildBtn.Click += (s, e) => DoContestRebuild();
            page.Controls.Add(_contestRebuildBtn);
            y += 34;

            _contestExportCabrilloBtn = new Button { Text = "Export &Cabrillo...", Location = new Point(left, y), Size = new Size(140, 26), Font = font, AccessibleName = "Export Cabrillo" };
            _contestExportCabrilloBtn.Click += (s, e) => DoContestExport("cabrillo");
            page.Controls.Add(_contestExportCabrilloBtn);

            _contestExportAdifBtn = new Button { Text = "Export &ADIF...", Location = new Point(left + 150, y), Size = new Size(140, 26), Font = font, AccessibleName = "Export ADIF" };
            _contestExportAdifBtn.Click += (s, e) => DoContestExport("adif");
            page.Controls.Add(_contestExportAdifBtn);
            y += 34;

            // ONE status surface, updated in place -- never a second control repeating the same
            // text (see this class's own header comment).
            _contestSessionStatusLabel = new Label { Location = new Point(left, y), Size = new Size(700, 40), Font = font, AutoSize = false, TabStop = false, AccessibleName = "Contest status" };
            page.Controls.Add(_contestSessionStatusLabel);
            y += 44;

            // The mode-eligibility advisory surface (Winter Field Day and similar) -- Nexus's
            // own warning text, shown accessibly, never blocking. Empty/hidden when there is
            // nothing to say -- see RefreshContestSessionStatus.
            _contestSessionWarningLabel = new Label { Location = new Point(left, y), Size = new Size(700, 40), Font = font, AutoSize = false, TabStop = false, ForeColor = Color.DarkOrange, AccessibleName = "Contest advisory" };
            page.Controls.Add(_contestSessionWarningLabel);

            return page;
        }

        private void LoadSelectedContestRuleset(string eventId)
        {
            _contestSelectedEventId = eventId;
            _contestSelectedRuleset = _contestClient.GetRuleset(eventId, out string error);
            _contestEntryFieldsPanel.Controls.Clear();
            _contestEntryControls.Clear();
            if (_contestSelectedRuleset == null)
            {
                _contestSessionStatusLabel.Text = "Could not load rules for " + eventId + ": " + error;
                return;
            }
            int y = 4;
            foreach (var field in _contestSelectedRuleset.Fields)
            {
                var label = new Label { Text = (field.Label ?? field.Key) + ":", AutoSize = true, Location = new Point(4, y + 3), Font = Font, TabStop = false };
                _contestEntryFieldsPanel.Controls.Add(label);
                var control = ContestFieldControlFactory.Create(field, Font);
                control.Location = new Point(160, y);
                control.Size = control is Label ? control.Size : new Size(140, 21);
                _contestEntryFieldsPanel.Controls.Add(control);
                _contestEntryControls.Add((field, control));
                y += 28;
            }

            // Prefill from this profile's own saved defaults for this contest (ContestConfigStore
            // -- the operator's usual/last-used CLASS/SECTION/run style), never overwriting a
            // field this contest doesn't actually have.
            var defaults = ContestConfigStore.Load(eventId);
            foreach (var (field, control) in _contestEntryControls)
            {
                if (string.Equals(field.Key, "CLASS", StringComparison.OrdinalIgnoreCase))
                    ContestFieldControlFactory.WriteValue(control, defaults.Class);
                else if (string.Equals(field.Key, "SECTION", StringComparison.OrdinalIgnoreCase))
                    ContestFieldControlFactory.WriteValue(control, defaults.Section);
            }
            _contestRunModeCombo.SelectedIndex = string.Equals(defaults.RunMode, "sp", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

            _contestSessionStatusLabel.Text = $"Loaded {_contestSelectedRuleset.Fields.Count} exchange field(s) for {eventId}. Not yet entered.";
        }

        private void DoContestEnter()
        {
            if (_contestSelectedRuleset == null || string.IsNullOrEmpty(_contestSelectedEventId))
            {
                _contestSessionStatusLabel.Text = "Load a contest's fields first.";
                return;
            }
            string runMode = _contestRunModeCombo.SelectedIndex == 1 ? "sp" : "run";
            string GetField(string key) => _contestEntryControls.Where(c => c.Field.Key == key).Select(c => ContestFieldControlFactory.ReadValue(c.Control)).FirstOrDefault() ?? "";

            string classValue = GetField("CLASS");
            string sectionValue = GetField("SECTION");

            var result = _contestClient.Enter(
                _contestSelectedEventId, runMode, _contestMyCall(), _contestMyGrid(), _contestOperatorCall(),
                classValue, sectionValue,
                categoryOperator: "", categoryPower: "", categoryAssisted: "", categoryStation: "",
                out string error);
            if (result == null)
            {
                _contestSessionStatusLabel.Text = "Could not enter contest: " + error;
                return;
            }
            _contestWorkflowFn()?.OnSessionEntered(result.SessionInstanceId, _contestSelectedEventId, _contestSelectedRuleset.ContestId);

            // Saved only now -- a successful CONTEST_ENTER -- never during an unrelated Options
            // save (see ContestConfigStore's own header comment).
            ContestConfigStore.Save(_contestSelectedEventId, new ContestEntryDefaults
            {
                Class = classValue,
                Section = sectionValue,
                RunMode = runMode,
            });

            _contestEnterBtn.Enabled = false;
            _contestExitBtn.Enabled = true;
            _contestSessionStatusLabel.Text = $"Entered {_contestSelectedEventId}, session instance {result.SessionInstanceId}.";
        }

        private void DoContestExit()
        {
            _contestClient.Exit(out string error);
            _contestWorkflowFn()?.OnSessionExited();
            _contestEnterBtn.Enabled = true;
            _contestExitBtn.Enabled = false;
            _contestSessionStatusLabel.Text = error == null ? "Contest session exited." : "Exit reported: " + error;
        }

        private void DoContestRebuild()
        {
            var wf = _contestWorkflowFn();
            if (wf == null || !wf.IsSessionActive)
            {
                _contestSessionStatusLabel.Text = "No active session to recalculate.";
                return;
            }
            var result = wf.RebuildScoreAndExport(out string error);
            _contestSessionStatusLabel.Text = result != null
                ? $"Recalculated: {result.QsoCount} QSOs, {result.Points} points."
                : "Recalculate failed: " + error;
        }

        private void DoContestExport(string format)
        {
            var wf = _contestWorkflowFn();
            if (wf == null || !wf.IsSessionActive)
            {
                _contestSessionStatusLabel.Text = "No active session to export.";
                return;
            }
            var rebuild = wf.RebuildScoreAndExport(out string rebuildError);
            if (rebuild == null)
            {
                _contestSessionStatusLabel.Text = "Export needs a successful recalculation first: " + rebuildError;
                return;
            }
            var station = _contestStationFn();
            string text = _contestClient.Export(format, station?.OperatorName ?? "", station?.ContestEmail ?? "", out string error);
            if (text == null)
            {
                _contestSessionStatusLabel.Text = "Export failed: " + error;
                return;
            }
            using (var dlg = new SaveFileDialog
            {
                Filter = format == "cabrillo" ? "Cabrillo files (*.log)|*.log|All files (*.*)|*.*" : "ADIF files (*.adi)|*.adi|All files (*.*)|*.*",
                FileName = (_contestSelectedEventId ?? "contest") + (format == "cabrillo" ? ".log" : ".adi"),
            })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    System.IO.File.WriteAllText(dlg.FileName, text);
                    _contestSessionStatusLabel.Text = "Exported to " + dlg.FileName;
                }
            }
        }

        private void RefreshContestSessionStatus()
        {
            var wf = _contestWorkflowFn();
            if (wf == null || !wf.IsSessionActive)
                return;

            // Reconciliation itself runs on Controller's own persistent timer, independent of
            // whether this window/tab is open -- same "keep running regardless of the window"
            // precedent as OtaSpotsWindow's live feeds (see ARCHITECTURE.md). This tick only
            // refreshes what's displayed here.

            // Winter Field Day / advisory-mode warn-and-continue: if the selected ruleset bans
            // the mode Jimmy currently operates (FT8/FT4), show Nexus's own advisory here --
            // never block entry or logging (banned_modes is advisory-only in Nexus's own design,
            // never an enforcement input; see ARCHITECTURE.md's Winter Field Day note).
            if (_contestSelectedRuleset != null && _contestSelectedRuleset.BannedModes != null &&
                (_contestSelectedRuleset.BannedModes.Contains("FT8") || _contestSelectedRuleset.BannedModes.Contains("FT4")))
            {
                _contestSessionWarningLabel.Text = $"Note: {_contestSelectedEventId} lists FT8/FT4 among modes not credited by the sponsor's rules for this event. Contacts are still logged normally; Nexus does not block them.";
            }
            else
            {
                _contestSessionWarningLabel.Text = "";
            }
        }

        // ── Manual Entry ─────────────────────────────────────────────────────────

        private TabPage BuildContestManualTab(Font font)
        {
            var page = new TabPage("Manual Entry");
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
            _contestManualCallBox = new TextBox { Location = new Point(left + 100, y + 53), Size = new Size(120, 21), Font = font, AccessibleName = "Callsign" };
            page.Controls.Add(_contestManualCallBox);

            var bandLabel = new Label { Text = "Band:", AutoSize = true, Location = new Point(left + 240, y + 56), Font = font, TabStop = false };
            page.Controls.Add(bandLabel);
            _contestManualBandBox = new TextBox { Location = new Point(left + 290, y + 53), Size = new Size(70, 21), Font = font, AccessibleName = "Band" };
            page.Controls.Add(_contestManualBandBox);

            var modeLabel = new Label { Text = "Mode:", AutoSize = true, Location = new Point(left + 380, y + 56), Font = font, TabStop = false };
            page.Controls.Add(modeLabel);
            _contestManualModeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Location = new Point(left + 430, y + 53), Size = new Size(90, 21), Font = font, AccessibleName = "Mode" };
            _contestManualModeCombo.Items.AddRange(new object[] { "FT8", "FT4", "CW", "SSB", "RTTY" });
            page.Controls.Add(_contestManualModeCombo);
            y += 84;

            var contestLabel = new Label { Text = "Contest:", AutoSize = true, Location = new Point(left, y + 3), Font = font, TabStop = false };
            page.Controls.Add(contestLabel);
            _contestManualContestCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(left + 100, y), Size = new Size(200, 21), Font = font, AccessibleName = "Contest, from Nexus's known list" };
            _contestManualContestCombo.SelectedIndexChanged += (s, e) => LoadContestManualFields();
            page.Controls.Add(_contestManualContestCombo);

            var freeLabel = new Label { Text = "Or, contest not listed:", AutoSize = true, Location = new Point(left + 320, y + 3), Font = font, TabStop = false };
            page.Controls.Add(freeLabel);
            _contestManualContestFreeTextBox = new TextBox { Location = new Point(left + 470, y), Size = new Size(160, 21), Font = font, AccessibleName = "Contest name, not in Nexus's list -- unvalidated and unscored" };
            page.Controls.Add(_contestManualContestFreeTextBox);
            y += 32;

            _contestManualFieldsPanel = new Panel { Location = new Point(left, y), Size = new Size(700, 120), AutoScroll = true };
            page.Controls.Add(_contestManualFieldsPanel);
            y += 128;

            var logBtn = new Button { Text = "&Log Contact", Location = new Point(left, y), Size = new Size(120, 26), Font = font, AccessibleName = "Log contact" };
            logBtn.Click += (s, e) => DoContestManualLog();
            page.Controls.Add(logBtn);
            y += 34;

            _contestManualStatusLabel = new Label { Location = new Point(left, y), Size = new Size(700, 40), Font = font, AutoSize = false, TabStop = false, AccessibleName = "Manual entry status" };
            page.Controls.Add(_contestManualStatusLabel);

            page.Enter += (s, e) =>
            {
                if (_contestManualContestCombo.Items.Count == 0)
                {
                    var events = _contestClient.ListEvents(out _);
                    if (events != null)
                        foreach (var ev in events.OrderBy(e2 => e2.EventId))
                            _contestManualContestCombo.Items.Add(ev.EventId);
                }
            };

            return page;
        }

        private void LoadContestManualFields()
        {
            _contestManualFieldsPanel.Controls.Clear();
            _contestManualControls.Clear();
            if (_contestManualContestCombo.SelectedItem == null) return;
            string eventId = _contestManualContestCombo.SelectedItem.ToString();
            var ruleset = _contestClient.GetRuleset(eventId, out string error);
            if (ruleset == null) { _contestManualStatusLabel.Text = "Could not load fields: " + error; return; }
            int y = 4;
            foreach (var field in ruleset.Fields)
            {
                var label = new Label { Text = (field.Label ?? field.Key) + ":", AutoSize = true, Location = new Point(4, y + 3), Font = Font, TabStop = false };
                _contestManualFieldsPanel.Controls.Add(label);
                var control = ContestFieldControlFactory.Create(field, Font);
                control.Location = new Point(160, y);
                control.Size = control is Label ? control.Size : new Size(140, 21);
                _contestManualFieldsPanel.Controls.Add(control);
                _contestManualControls.Add((field, control));
                y += 28;
            }
        }

        private void DoContestManualLog()
        {
            if (_db == null)
            {
                _contestManualStatusLabel.Text = "Database not available.";
                return;
            }

            string call = _contestManualCallBox.Text.Trim().ToUpperInvariant();
            string band = _contestManualBandBox.Text.Trim();
            string mode = _contestManualModeCombo.Text.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(call))
            {
                _contestManualStatusLabel.Text = "Enter a callsign first.";
                return;
            }

            string knownEventId = _contestManualContestCombo.SelectedItem?.ToString();
            bool nexusKnown = !string.IsNullOrEmpty(knownEventId) && _contestManualControls.Count > 0;

            if (nexusKnown && _contestWorkflowFn() != null && _contestWorkflowFn().IsSessionActive)
            {
                var fields = _contestManualControls.Select(c => new List<string> { c.Field.Key, ContestFieldControlFactory.ReadValue(c.Control) }).ToList();
                bool logged = _contestClient.LogManual(call, fields, mode, "", out string error);
                if (error != null) { _contestManualStatusLabel.Text = "Could not log: " + error; return; }
                _contestManualStatusLabel.Text = logged
                    ? $"Logged {call} via Nexus's own validation for {knownEventId}."
                    : $"{call} was refused as a duplicate by Nexus's own dupe rule for {knownEventId}.";
                return;
            }

            // Not Nexus-known (or no active session for it): a plain, local, unvalidated,
            // unscored record -- still a real contact, clearly labeled as such.
            string contestTag = !string.IsNullOrWhiteSpace(_contestManualContestFreeTextBox.Text)
                ? _contestManualContestFreeTextBox.Text.Trim()
                : (knownEventId ?? "");
            var now = DateTime.UtcNow;
            string qsoDate = now.ToString("yyyyMMdd");
            string timeOn = now.ToString("HHmmss");
            string dedupKey = AdifImporter.BuildDedupKey(call, band, mode, qsoDate, timeOn);
            _db.Upsert(call, band, mode, qsoDate, timeOn, timeOn,
                freqHz: 0, rstSent: "", rstRcvd: "",
                state: "", country: "", dxcc: 0, cqZone: 0,
                grid: "", name: "", comment: "Manual contest entry, unvalidated: " + contestTag, txPwr: "",
                operatorCall: _contestOperatorCall(), stationCall: _contestMyCall(), myGrid: _contestMyGrid(),
                lotwQslSent: "", lotwQslRcvd: "", qrzQslSent: "", qrzQslRcvd: "",
                source: "MANUAL", sourceQsoId: "", dedupKey: dedupKey,
                continent: "", ituZone: 0, county: "", iota: "",
                sig: "", sigInfo: "", mySig: "", mySigInfo: "",
                darcDok: "", wpxPrefix: "", exchangeSent: "", exchangeRcvd: "");
            var qsoId = _db.GetIdByDedupKey(dedupKey);
            if (qsoId.HasValue && !string.IsNullOrEmpty(contestTag))
                _db.SetContestAssociation(qsoId.Value, contestTag, "");

            _contestManualStatusLabel.Text = $"Logged {call} as a plain, unvalidated, unscored contact" +
                (string.IsNullOrEmpty(contestTag) ? "." : $" (contest: {contestTag}, not supported by Nexus -- not validated or scored).");
        }
    }
}
