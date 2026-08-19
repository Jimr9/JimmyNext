using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Picker shown by RuleDefinitionManagerDlg.RestoreDefaults() only when at least one
    // award in the live Rule Definitions folder doesn't match a shipped award's Id (i.e.
    // it's custom, tester-made, or otherwise not part of the starter library). Every
    // custom award is checked (kept) by default -- restoring defaults must never remove
    // something the user made unless they explicitly uncheck it here.
    internal class RestoreDefaultAwardsDlg : Form
    {
        private readonly CheckedListBox _listBox;
        private readonly List<RuleDefinition> _customDefs;
        private readonly Button _restoreButton, _cancelButton;

        public List<RuleDefinition> KeptCustomDefs { get; private set; }

        public RestoreDefaultAwardsDlg(int shippedCount, List<RuleDefinition> customDefs)
        {
            _customDefs = customDefs;

            Text            = "Restore Default Awards";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            ShowInTaskbar   = false;
            StartPosition   = FormStartPosition.CenterParent;
            ClientSize      = new Size(420, 330);

            var lbl = new Label
            {
                Text     = $"This resets {shippedCount} built-in award(s) to their original shipped " +
                           "definitions. The custom award(s) below were also found in your Rule " +
                           "Definitions folder -- uncheck any you'd like removed (sent to the Recycle " +
                           "Bin) as part of this reset. Checked awards are kept as-is.",
                Location = new Point(12, 10),
                Size     = new Size(396, 70),
            };
            Controls.Add(lbl);

            _listBox = new CheckedListBox
            {
                Location       = new Point(12, 84),
                Size           = new Size(396, 190),
                TabIndex       = 1,
                AccessibleName = "Custom awards to keep",
                CheckOnClick   = true,
            };
            foreach (var d in customDefs)
                _listBox.Items.Add($"{d.Name} ({d.Id})", true);
            Controls.Add(_listBox);

            _restoreButton = new Button
            {
                Text     = "Restore Defaults",
                Location = new Point(148, 286),
                Size     = new Size(120, 26),
                TabIndex = 2,
            };
            _restoreButton.Click += RestoreButton_Click;
            Controls.Add(_restoreButton);

            _cancelButton = new Button
            {
                Text         = "Cancel",
                Location     = new Point(276, 286),
                Size         = new Size(80, 26),
                TabIndex     = 3,
                DialogResult = DialogResult.Cancel,
            };
            Controls.Add(_cancelButton);

            AcceptButton = _restoreButton;
            CancelButton = _cancelButton;
        }

        private void RestoreButton_Click(object sender, EventArgs e)
        {
            KeptCustomDefs = Enumerable.Range(0, _listBox.Items.Count)
                .Where(i => _listBox.GetItemChecked(i))
                .Select(i => _customDefs[i])
                .ToList();
            DialogResult = DialogResult.OK;
        }
    }
}
