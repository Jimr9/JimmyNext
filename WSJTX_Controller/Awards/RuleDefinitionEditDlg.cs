using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Create or edit a single Rule Definition.
    //   existing = null              -> blank new rule
    //   existing != null, isTemplate = false -> edit that rule in place (Id locked)
    //   existing != null, isTemplate = true  -> new rule pre-filled from a
    //     template (e.g. a Wizard preset, or a clone of another rule) -- Id is
    //     still editable and nothing is overwritten on disk until Save.
    // Saving always fully rewrites the .ini file via RuleWriter.
    public class RuleDefinitionEditDlg : Form
    {
        private readonly RuleDefinition _prefill;    // values to populate fields with; null = blank
        private readonly RuleDefinition _original;   // existing file being edited; null when creating
        private readonly bool _isNew;

        public string SavedId { get; private set; }

        // ── General tab ──────────────────────────────────────────────────────
        private TextBox _idTb, _nameTb, _sponsorTb, _categoryTb, _websiteTb;
        private TextBox _descriptionTb;
        private CheckBox _enabledCb;

        // ── Match tab ────────────────────────────────────────────────────────
        private ComboBox _groupByCb;
        private TextBox _universeTb, _limitToTb, _bandsTb, _modesTb, _callsignPatternTb, _sigTb, _dateFromTb, _dateToTb;

        // ── Confirmation & Target tab ────────────────────────────────────────
        private ComboBox _confirmationCb, _targetTypeCb;
        private NumericUpDown _thresholdNum;
        private TextBox _levelsTb;
        private Label _thresholdLbl, _levelsLbl, _levelsHintLbl;

        // ── Endorsements tab ─────────────────────────────────────────────────
        private TextBox _endBandsTb, _endModesTb;

        public RuleDefinitionEditDlg(RuleDefinition existing, bool isTemplate = false)
        {
            _prefill  = existing;
            _original = isTemplate ? null : existing;
            _isNew    = isTemplate || existing == null;

            Text = _isNew
                ? (existing != null ? $"New Rule Definition (from {existing.Name})" : "New Rule Definition")
                : $"Edit Rule Definition — {existing.Name}";
            StartPosition   = FormStartPosition.CenterParent;
            MinimumSize     = new Size(560, 480);
            Size            = new Size(600, 540);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox     = true;
            MinimizeBox     = false;
            ShowInTaskbar   = false;
            KeyPreview      = true;

            BuildUi();
            PopulateFromExisting();

            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        }

        private static Label MakeLabel(string text, int x, int y) =>
            new Label { Text = text, Location = new Point(x, y), AutoSize = true };

        private void BuildUi()
        {
            var tabs = new TabControl
            {
                Location = new Point(10, 10),
                Size     = new Size(560, 440),
                Anchor   = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };
            Controls.Add(tabs);

            tabs.TabPages.Add(BuildGeneralTab());
            tabs.TabPages.Add(BuildMatchTab());
            tabs.TabPages.Add(BuildConfirmationTargetTab());
            tabs.TabPages.Add(BuildEndorsementsTab());

            var saveBtn = new Button
            {
                Text = "&Save", Location = new Point(400, 460), Size = new Size(80, 26),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                AccessibleName = "Save Rule Definition",
            };
            saveBtn.Click += (s, e) => Save();
            Controls.Add(saveBtn);
            AcceptButton = saveBtn;

            var cancelBtn = new Button
            {
                Text = "Cancel", Location = new Point(486, 460), Size = new Size(80, 26),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                DialogResult = DialogResult.Cancel,
                AccessibleName = "Cancel",
            };
            Controls.Add(cancelBtn);
            CancelButton = cancelBtn;
        }

        private TabPage BuildGeneralTab()
        {
            var page = new TabPage("General");
            int y = 14, tab = 1;

            page.Controls.Add(MakeLabel("Rule Id:", 10, y));
            _idTb = new TextBox
            {
                Location = new Point(140, y - 3), Size = new Size(200, 20), TabIndex = tab++,
                AccessibleName = "Rule Id",
                Enabled = _isNew,   // Id is stable once created; other saved state (e.g. Still Need selection) keys off it
            };
            page.Controls.Add(_idTb);
            y += 28;

            page.Controls.Add(MakeLabel("Name:", 10, y));
            _nameTb = new TextBox { Location = new Point(140, y - 3), Size = new Size(380, 20), TabIndex = tab++, AccessibleName = "Rule Name" };
            page.Controls.Add(_nameTb);
            y += 28;

            page.Controls.Add(MakeLabel("Sponsor:", 10, y));
            _sponsorTb = new TextBox { Location = new Point(140, y - 3), Size = new Size(200, 20), TabIndex = tab++, AccessibleName = "Sponsor" };
            page.Controls.Add(_sponsorTb);
            y += 28;

            page.Controls.Add(MakeLabel("Category:", 10, y));
            _categoryTb = new TextBox { Location = new Point(140, y - 3), Size = new Size(200, 20), TabIndex = tab++, AccessibleName = "Category" };
            page.Controls.Add(_categoryTb);
            y += 28;

            page.Controls.Add(MakeLabel("Website:", 10, y));
            _websiteTb = new TextBox { Location = new Point(140, y - 3), Size = new Size(380, 20), TabIndex = tab++, AccessibleName = "Website" };
            page.Controls.Add(_websiteTb);
            y += 28;

            page.Controls.Add(MakeLabel("Description:", 10, y));
            y += 18;
            _descriptionTb = new TextBox
            {
                Location = new Point(10, y), Size = new Size(510, 60), Multiline = true,
                ScrollBars = ScrollBars.Vertical, TabIndex = tab++, AccessibleName = "Description",
            };
            page.Controls.Add(_descriptionTb);
            y += 68;

            _enabledCb = new CheckBox
            {
                Text = "Enabled", Location = new Point(10, y), AutoSize = true, TabIndex = tab++,
                AccessibleName = "Enabled", Checked = true,
            };
            page.Controls.Add(_enabledCb);

            return page;
        }

        private TabPage BuildMatchTab()
        {
            var page = new TabPage("Match");
            int y = 14, tab = 1;

            page.Controls.Add(MakeLabel("Group By:", 10, y));
            _groupByCb = new ComboBox
            {
                Location = new Point(140, y - 3), Size = new Size(160, 20), TabIndex = tab++,
                DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Group By",
            };
            _groupByCb.Items.AddRange(Enum.GetNames(typeof(RuleGroupBy)));
            page.Controls.Add(_groupByCb);
            y += 28;

            page.Controls.Add(MakeLabel("Universe:", 10, y));
            _universeTb = new TextBox
            {
                Location = new Point(140, y - 3), Size = new Size(380, 20), TabIndex = tab++,
                AccessibleName = "Universe: built-in name (e.g. US_50_STATES, DXCC_CURRENT) or File:name.txt",
            };
            page.Controls.Add(_universeTb);
            y += 18;
            page.Controls.Add(new Label
            {
                Text = "e.g. US_50_STATES, CA_PROVINCES, CONTINENTS, DXCC_CURRENT, DXCC_NORTH_AMERICA, or File:name.txt",
                Location = new Point(140, y), AutoSize = true, ForeColor = SystemColors.GrayText,
            });
            y += 24;

            page.Controls.Add(MakeLabel("Limit To:", 10, y));
            _limitToTb = new TextBox
            {
                Location = new Point(140, y - 3), Size = new Size(380, 20), TabIndex = tab++,
                AccessibleName = "Limit To: restrict counted values to this universe",
            };
            page.Controls.Add(_limitToTb);
            y += 28;

            page.Controls.Add(MakeLabel("Bands:", 10, y));
            _bandsTb = new TextBox { Location = new Point(140, y - 3), Size = new Size(380, 20), TabIndex = tab++, AccessibleName = "Bands, comma-separated" };
            page.Controls.Add(_bandsTb);
            y += 28;

            page.Controls.Add(MakeLabel("Modes:", 10, y));
            _modesTb = new TextBox { Location = new Point(140, y - 3), Size = new Size(380, 20), TabIndex = tab++, AccessibleName = "Modes, comma-separated" };
            page.Controls.Add(_modesTb);
            y += 28;

            page.Controls.Add(MakeLabel("Callsign Pattern:", 10, y));
            _callsignPatternTb = new TextBox { Location = new Point(140, y - 3), Size = new Size(200, 20), TabIndex = tab++, AccessibleName = "Callsign Pattern" };
            page.Controls.Add(_callsignPatternTb);
            y += 28;

            page.Controls.Add(MakeLabel("SIG:", 10, y));
            _sigTb = new TextBox { Location = new Point(140, y - 3), Size = new Size(200, 20), TabIndex = tab++, AccessibleName = "SIG filter" };
            page.Controls.Add(_sigTb);
            y += 28;

            page.Controls.Add(MakeLabel("Date From (yyyy-MM-dd):", 10, y));
            _dateFromTb = new TextBox { Location = new Point(180, y - 3), Size = new Size(100, 20), TabIndex = tab++, AccessibleName = "Date From" };
            page.Controls.Add(_dateFromTb);
            y += 28;

            page.Controls.Add(MakeLabel("Date To (yyyy-MM-dd):", 10, y));
            _dateToTb = new TextBox { Location = new Point(180, y - 3), Size = new Size(100, 20), TabIndex = tab++, AccessibleName = "Date To" };
            page.Controls.Add(_dateToTb);

            return page;
        }

        private TabPage BuildConfirmationTargetTab()
        {
            var page = new TabPage("Confirmation && Target");
            int y = 14, tab = 1;

            page.Controls.Add(MakeLabel("Confirmation Requires:", 10, y));
            _confirmationCb = new ComboBox
            {
                Location = new Point(180, y - 3), Size = new Size(120, 20), TabIndex = tab++,
                DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Confirmation Requires",
            };
            _confirmationCb.Items.AddRange(Enum.GetNames(typeof(RuleConfirmation)));
            page.Controls.Add(_confirmationCb);
            y += 32;

            page.Controls.Add(MakeLabel("Target Type:", 10, y));
            _targetTypeCb = new ComboBox
            {
                Location = new Point(180, y - 3), Size = new Size(120, 20), TabIndex = tab++,
                DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Target Type",
            };
            _targetTypeCb.Items.AddRange(Enum.GetNames(typeof(RuleTargetType)));
            _targetTypeCb.SelectedIndexChanged += (s, e) => UpdateTargetVisibility();
            page.Controls.Add(_targetTypeCb);
            y += 32;

            _thresholdLbl = MakeLabel("Threshold (Count):", 10, y);
            page.Controls.Add(_thresholdLbl);
            _thresholdNum = new NumericUpDown
            {
                Location = new Point(180, y - 3), Size = new Size(80, 20), Minimum = 1, Maximum = 100000,
                TabIndex = tab++, AccessibleName = "Threshold for Count target",
            };
            page.Controls.Add(_thresholdNum);
            y += 32;

            _levelsLbl = MakeLabel("Levels (Levels target), one Name=Threshold per line:", 10, y);
            page.Controls.Add(_levelsLbl);
            y += 18;
            _levelsTb = new TextBox
            {
                Location = new Point(10, y), Size = new Size(510, 100), Multiline = true,
                ScrollBars = ScrollBars.Vertical, TabIndex = tab++,
                AccessibleName = "Levels, one Name=Threshold per line, ascending",
            };
            page.Controls.Add(_levelsTb);
            y += 108;
            _levelsHintLbl = new Label
            {
                Text = "Example:\r\nBronze=30\r\nSilver=40\r\nGold=50",
                Location = new Point(10, y), AutoSize = true, ForeColor = SystemColors.GrayText,
            };
            page.Controls.Add(_levelsHintLbl);

            return page;
        }

        private TabPage BuildEndorsementsTab()
        {
            var page = new TabPage("Endorsements");
            int y = 14, tab = 1;

            page.Controls.Add(MakeLabel("Band Endorsements:", 10, y));
            _endBandsTb = new TextBox { Location = new Point(160, y - 3), Size = new Size(360, 20), TabIndex = tab++, AccessibleName = "Band Endorsements, comma-separated" };
            page.Controls.Add(_endBandsTb);
            y += 28;

            page.Controls.Add(MakeLabel("Mode Endorsements:", 10, y));
            _endModesTb = new TextBox { Location = new Point(160, y - 3), Size = new Size(360, 20), TabIndex = tab++, AccessibleName = "Mode Endorsements, comma-separated" };
            page.Controls.Add(_endModesTb);
            y += 28;

            page.Controls.Add(new Label
            {
                Text = "Leave both blank if this award has no band/mode endorsements.",
                Location = new Point(10, y), AutoSize = true, ForeColor = SystemColors.GrayText,
            });

            return page;
        }

        private void UpdateTargetVisibility()
        {
            bool isCount  = _targetTypeCb.SelectedItem as string == RuleTargetType.Count.ToString();
            bool isLevels = _targetTypeCb.SelectedItem as string == RuleTargetType.Levels.ToString();
            _thresholdLbl.Visible = _thresholdNum.Visible = isCount;
            _levelsLbl.Visible = _levelsTb.Visible = _levelsHintLbl.Visible = isLevels;
        }

        private void PopulateFromExisting()
        {
            var d = _prefill;
            // A template's Id/Name are never carried over -- they're what the
            // user is about to choose for a brand-new rule.
            _idTb.Text          = _isNew ? "" : (d?.Id ?? "");
            _nameTb.Text        = d?.Name ?? "";
            _sponsorTb.Text     = d?.Sponsor ?? "";
            _categoryTb.Text    = d?.Category ?? "";
            _websiteTb.Text     = d?.Website ?? "";
            _descriptionTb.Text = d?.Description ?? "";
            _enabledCb.Checked  = d?.Enabled ?? true;

            _groupByCb.SelectedItem = (d?.GroupBy ?? RuleGroupBy.None).ToString();
            _universeTb.Text        = d?.Universe ?? "";
            _limitToTb.Text         = d?.LimitTo ?? "";
            _bandsTb.Text           = string.Join(",", d?.Bands ?? new List<string>());
            _modesTb.Text           = string.Join(",", d?.Modes ?? new List<string>());
            _callsignPatternTb.Text = d?.CallsignPattern ?? "";
            _sigTb.Text             = d?.Sig ?? "";
            _dateFromTb.Text        = d?.DateFrom ?? "";
            _dateToTb.Text          = d?.DateTo ?? "";

            _confirmationCb.SelectedItem = (d?.Confirmation ?? RuleConfirmation.Any).ToString();
            _targetTypeCb.SelectedItem   = (d?.Target ?? RuleTargetType.Count).ToString();
            _thresholdNum.Value          = Math.Max(1, d?.Threshold ?? 1);
            _levelsTb.Text               = string.Join("\r\n", (d?.Levels ?? new List<RuleLevel>()).Select(l => $"{l.Name}={l.Threshold}"));

            _endBandsTb.Text = string.Join(",", d?.Endorsements?.Bands ?? new List<string>());
            _endModesTb.Text = string.Join(",", d?.Endorsements?.Modes ?? new List<string>());

            UpdateTargetVisibility();
        }

        private static List<string> SplitList(string s) =>
            string.IsNullOrWhiteSpace(s)
                ? new List<string>()
                : s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        private void Save()
        {
            string id = _idTb.Text.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(id) || !System.Text.RegularExpressions.Regex.IsMatch(id, @"^[A-Za-z0-9_-]+$"))
            {
                MessageBox.Show(this, "Rule Id is required and may only contain letters, digits, '_' and '-'.",
                    "Invalid Id", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_isNew && RuleLibrary.Definitions.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this, $"A Rule Definition with Id '{id}' already exists.",
                    "Duplicate Id", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(_nameTb.Text))
            {
                MessageBox.Show(this, "Name is required.", "Missing Name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var targetType = (RuleTargetType)Enum.Parse(typeof(RuleTargetType), (string)_targetTypeCb.SelectedItem);
            var levels = new List<RuleLevel>();
            if (targetType == RuleTargetType.Levels)
            {
                foreach (var rawLine in _levelsTb.Lines)
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0) continue;
                    int eq = line.IndexOf('=');
                    int threshold;
                    if (eq < 1 || !int.TryParse(line.Substring(eq + 1).Trim(), out threshold) || threshold <= 0)
                    {
                        MessageBox.Show(this, $"Invalid Levels line: '{line}'. Expected Name=Threshold with a positive Threshold.",
                            "Invalid Levels", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    levels.Add(new RuleLevel { Name = line.Substring(0, eq).Trim(), Threshold = threshold });
                }
                if (levels.Count == 0)
                {
                    MessageBox.Show(this, "At least one Name=Threshold line is required when Target Type is Levels.",
                        "Missing Levels", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                levels = levels.OrderBy(l => l.Threshold).ToList();
            }
            else if (targetType == RuleTargetType.All)
            {
                if (string.IsNullOrWhiteSpace(_universeTb.Text))
                {
                    MessageBox.Show(this, "Universe is required when Target Type is All.",
                        "Missing Universe", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            var endBands = SplitList(_endBandsTb.Text);
            var endModes = SplitList(_endModesTb.Text);

            var def = new RuleDefinition
            {
                Id              = id,
                Name            = _nameTb.Text.Trim(),
                Sponsor         = _sponsorTb.Text.Trim(),
                Category        = _categoryTb.Text.Trim(),
                FormatVersion   = RuleLoader.SupportedFormatVersion,
                Enabled         = _enabledCb.Checked,
                Description     = _descriptionTb.Text.Trim(),
                Website         = _websiteTb.Text.Trim(),
                GroupBy         = (RuleGroupBy)Enum.Parse(typeof(RuleGroupBy), (string)_groupByCb.SelectedItem),
                Universe        = _universeTb.Text.Trim(),
                LimitTo         = _limitToTb.Text.Trim(),
                Bands           = SplitList(_bandsTb.Text),
                Modes           = SplitList(_modesTb.Text),
                CallsignPattern = _callsignPatternTb.Text.Trim(),
                Sig             = _sigTb.Text.Trim(),
                DateFrom        = _dateFromTb.Text.Trim(),
                DateTo          = _dateToTb.Text.Trim(),
                Confirmation    = (RuleConfirmation)Enum.Parse(typeof(RuleConfirmation), (string)_confirmationCb.SelectedItem),
                Target          = targetType,
                Threshold       = (int)_thresholdNum.Value,
                Levels          = levels,
                Endorsements    = (endBands.Count > 0 || endModes.Count > 0)
                                    ? new RuleEndorsements { Bands = endBands, Modes = endModes }
                                    : null,
            };

            string path = _original?.SourceFile ?? Path.Combine(RuleLoader.RulesFolder, id + ".ini");
            try
            {
                RuleWriter.Save(def, path);
                SavedId = id;
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save Rule Definition: " + ex.Message,
                    "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
