using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Step 1 of the New Rule Wizard: pick a starting point, then hand off to
    // RuleDefinitionEditDlg (in template mode) for the actual fields and save.
    // Deliberately does not duplicate the Edit dialog's form -- every preset
    // here is just a pre-filled RuleDefinition passed to that same dialog, per
    // the "one shared Edit form" design (see project history).
    public class NewRuleWizardDlg : Form
    {
        private class Preset
        {
            public string Label;
            public string Description;
            public Func<RuleDefinition> Build;   // null for "Copy Existing Rule" (handled specially)
        }

        private ListBox _presetLb;
        private Label _descLbl;
        private ComboBox _copyFromCb;
        private Label _copyFromLbl;
        private Button _nextBtn;
        private List<RuleDefinition> _existingDefs;
        private List<Preset> _presets;

        public string SavedId { get; private set; }

        public NewRuleWizardDlg()
        {
            Text            = "New Rule Wizard — Choose a Starting Point";
            StartPosition   = FormStartPosition.CenterParent;
            MinimumSize     = new Size(520, 420);
            Size            = new Size(560, 460);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox     = true;
            MinimizeBox     = false;
            ShowInTaskbar   = false;
            KeyPreview      = true;

            _existingDefs = RuleLibrary.Definitions.OrderBy(d => d.Name).ToList();
            BuildPresets();
            BuildUi();

            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        }

        private void BuildPresets()
        {
            _presets = new List<Preset>
            {
                new Preset { Label = "Blank Rule", Description = "Start with nothing filled in -- choose every field yourself.", Build = () => null },
                new Preset { Label = "Copy Existing Rule", Description = "Start from a full copy of an existing Rule Definition, then change what you need.", Build = null },
                new Preset { Label = "Callsign Award", Description = "Count distinct callsigns worked (e.g. a hunter/activator-style award).",
                    Build = () => new RuleDefinition { GroupBy = RuleGroupBy.Callsign, Target = RuleTargetType.Count, Threshold = 25,
                        Description = "Count distinct callsigns worked." } },
                new Preset { Label = "US State Award (WAS-style)", Description = "Work/confirm all 50 US states.",
                    Build = () => new RuleDefinition { GroupBy = RuleGroupBy.State, Universe = "US_50_STATES", Target = RuleTargetType.All,
                        Description = "Work and confirm all 50 US states." } },
                new Preset { Label = "Canadian Province Award", Description = "Work/confirm all 10 provinces + 3 territories.",
                    Build = () => new RuleDefinition { GroupBy = RuleGroupBy.State, Universe = "CA_PROVINCES", Target = RuleTargetType.All,
                        Description = "Work and confirm all Canadian provinces and territories." } },
                new Preset { Label = "DXCC Award (Current Entities)", Description = "Work/confirm all currently-active DXCC entities (uses Club Log country data).",
                    Build = () => new RuleDefinition { GroupBy = RuleGroupBy.Dxcc, Universe = "DXCC_CURRENT", Target = RuleTargetType.All,
                        Description = "Work and confirm all current DXCC entities." } },
                new Preset { Label = "DXCC Award (Simple Count)", Description = "Count distinct DXCC entities worked, no fixed checklist (e.g. \"Work 100 countries\").",
                    Build = () => new RuleDefinition { GroupBy = RuleGroupBy.Dxcc, Target = RuleTargetType.Count, Threshold = 100,
                        Description = "Work and confirm a number of distinct DXCC entities." } },
                new Preset { Label = "Continental DXCC Award", Description = "DXCC entities limited to one continent (e.g. Worked All North America). Universe defaults to DXCC_NORTH_AMERICA -- change it on the Match tab to any DXCC_SOUTH_AMERICA/EUROPE/AFRICA/ASIA/OCEANIA.",
                    Build = () => new RuleDefinition { GroupBy = RuleGroupBy.Dxcc, Universe = "DXCC_NORTH_AMERICA", Target = RuleTargetType.All,
                        Description = "Work and confirm all DXCC entities in one continent." } },
                new Preset { Label = "CQ Zone Award (WAZ-style)", Description = "Work/confirm all 40 CQ zones.",
                    Build = () => new RuleDefinition { GroupBy = RuleGroupBy.CqZone, Universe = "CQ_ZONES", Target = RuleTargetType.All,
                        Description = "Work and confirm all 40 CQ zones." } },
                new Preset { Label = "ITU Zone Award", Description = "Work/confirm all 90 ITU zones.",
                    Build = () => new RuleDefinition { GroupBy = RuleGroupBy.ItuZone, Universe = "ITU_ZONES", Target = RuleTargetType.All,
                        Description = "Work and confirm all 90 ITU zones." } },
                new Preset { Label = "Continent Award (WAC-style)", Description = "Work/confirm all 6 inhabited continents.",
                    Build = () => new RuleDefinition { GroupBy = RuleGroupBy.Continent, Universe = "CONTINENTS", Target = RuleTargetType.All,
                        Description = "Work and confirm all continents." } },
                new Preset { Label = "Grid Square Award", Description = "Count distinct 4-character grid squares worked.",
                    Build = () => new RuleDefinition { GroupBy = RuleGroupBy.Grid4, Target = RuleTargetType.Count, Threshold = 100,
                        Description = "Count distinct 4-character grid squares worked." } },
                new Preset { Label = "County Award", Description = "Count distinct counties worked (a companion list can be used with LimitTo/Universe for a fixed checklist).",
                    Build = () => new RuleDefinition { GroupBy = RuleGroupBy.County, Target = RuleTargetType.Count, Threshold = 100,
                        Description = "Count distinct counties worked." } },
                new Preset { Label = "Prefix Award (WPX-style)", Description = "Count distinct callsign prefixes worked.",
                    Build = () => new RuleDefinition { GroupBy = RuleGroupBy.Prefix, Target = RuleTargetType.Count, Threshold = 100,
                        Description = "Count distinct callsign prefixes worked." } },
                new Preset { Label = "IOTA Award", Description = "Count distinct IOTA island references worked.",
                    Build = () => new RuleDefinition { GroupBy = RuleGroupBy.Iota, Target = RuleTargetType.Count, Threshold = 100,
                        Description = "Count distinct IOTA island references worked." } },
                new Preset { Label = "DOK Award", Description = "Count distinct DARC DOK county codes worked.",
                    Build = () => new RuleDefinition { GroupBy = RuleGroupBy.DarcDok, Target = RuleTargetType.Count, Threshold = 100,
                        Description = "Count distinct DOK codes worked." } },
            };
        }

        private void BuildUi()
        {
            var lbl = new Label { Text = "&Start from:", Location = new Point(10, 10), AutoSize = true };
            Controls.Add(lbl);

            _presetLb = new ListBox
            {
                Location = new Point(10, 30), Size = new Size(300, 300),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left,
                TabIndex = 1, AccessibleName = "Start from",
            };
            foreach (var p in _presets) _presetLb.Items.Add(p.Label);
            _presetLb.SelectedIndexChanged += (s, e) => UpdateSelection();
            Controls.Add(_presetLb);

            _copyFromLbl = new Label { Text = "Copy from:", Location = new Point(320, 30), AutoSize = true, Visible = false };
            Controls.Add(_copyFromLbl);
            _copyFromCb = new ComboBox
            {
                Location = new Point(320, 50), Size = new Size(210, 21), DropDownStyle = ComboBoxStyle.DropDownList,
                TabIndex = 2, AccessibleName = "Copy from", Visible = false,
            };
            foreach (var d in _existingDefs) _copyFromCb.Items.Add(d.Name);
            if (_copyFromCb.Items.Count > 0) _copyFromCb.SelectedIndex = 0;
            Controls.Add(_copyFromCb);

            _descLbl = new Label
            {
                Location = new Point(320, 84), Size = new Size(220, 246),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left,
                AccessibleName = "Description of the selected starting point",
            };
            Controls.Add(_descLbl);

            _nextBtn = new Button
            {
                Text = "&Next >", Location = new Point(360, 390), Size = new Size(90, 26),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left, TabIndex = 3,
                AccessibleName = "Next",
            };
            _nextBtn.Click += (s, e) => NextClicked();
            Controls.Add(_nextBtn);
            AcceptButton = _nextBtn;

            var cancelBtn = new Button
            {
                Text = "Cancel", Location = new Point(456, 390), Size = new Size(90, 26),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left, TabIndex = 4,
                DialogResult = DialogResult.Cancel, AccessibleName = "Cancel",
            };
            Controls.Add(cancelBtn);
            CancelButton = cancelBtn;

            _presetLb.SelectedIndex = 0;
        }

        private void UpdateSelection()
        {
            int idx = _presetLb.SelectedIndex;
            if (idx < 0) { _nextBtn.Enabled = false; return; }
            var preset = _presets[idx];
            _descLbl.Text = preset.Description;

            bool isCopy = preset.Build == null;
            _copyFromLbl.Visible = isCopy;
            _copyFromCb.Visible  = isCopy;
            _nextBtn.Enabled = !isCopy || _copyFromCb.Items.Count > 0;
        }

        private void NextClicked()
        {
            int idx = _presetLb.SelectedIndex;
            if (idx < 0) return;
            var preset = _presets[idx];

            RuleDefinition template;
            if (preset.Build != null)
            {
                template = preset.Build();
            }
            else
            {
                // Copy Existing Rule -- clone in full; only the Id is cleared
                // (Save() forces the user to choose one), so Name/Sponsor/etc.
                // carry over as a real starting point, not just a shape.
                var source = _existingDefs[_copyFromCb.SelectedIndex];
                template = RuleDefinitionManagerDlg.CloneDefinition(source);
                template.Name = source.Name + " (Copy)";
            }

            using (var edit = new RuleDefinitionEditDlg(template, isTemplate: true))
            {
                if (edit.ShowDialog(this) == DialogResult.OK)
                {
                    SavedId = edit.SavedId;
                    DialogResult = DialogResult.OK;
                    Close();
                }
                // Canceled from the Edit dialog: stay on this screen so the
                // user can pick a different starting point instead of losing
                // the wizard entirely.
            }
        }
    }
}
