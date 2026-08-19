using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Lets the user choose which logging services' QSOs to include before an ADIF
    // export runs. The checkbox list is built from QsoRecord.KnownSources, so a new
    // logging service shows up here automatically once it's added to that one array --
    // no further UI changes needed.
    internal class ExportSourceFilterDlg : Form
    {
        private readonly CheckedListBox _listBox;
        private readonly Button _okButton, _cancelButton;

        public List<string> SelectedSources { get; private set; }

        public ExportSourceFilterDlg()
        {
            Text            = "Export ADIF — Sources";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            ShowInTaskbar   = false;
            StartPosition   = FormStartPosition.CenterParent;
            ClientSize      = new Size(240, 230);

            var lbl = new Label
            {
                Text     = "Include QSOs logged from:",
                Location = new Point(12, 10),
                Size     = new Size(216, 16),
            };
            Controls.Add(lbl);

            _listBox = new CheckedListBox
            {
                Location       = new Point(12, 30),
                Size           = new Size(216, 140),
                TabIndex       = 1,
                AccessibleName = "Logging services to include",
                CheckOnClick   = true,
            };
            foreach (var source in QsoRecord.KnownSources)
                _listBox.Items.Add(source, true);
            Controls.Add(_listBox);

            _okButton = new Button
            {
                Text     = "OK",
                Location = new Point(28, 182),
                Size     = new Size(80, 26),
                TabIndex = 2,
            };
            _okButton.Click += OkButton_Click;
            Controls.Add(_okButton);

            _cancelButton = new Button
            {
                Text         = "Cancel",
                Location     = new Point(116, 182),
                Size         = new Size(80, 26),
                TabIndex     = 3,
                DialogResult = DialogResult.Cancel,
            };
            Controls.Add(_cancelButton);

            AcceptButton = _okButton;
            CancelButton = _cancelButton;
        }

        private void OkButton_Click(object sender, EventArgs e)
        {
            var sources = new List<string>();
            for (int i = 0; i < _listBox.Items.Count; i++)
                if (_listBox.GetItemChecked(i))
                    sources.Add((string)_listBox.Items[i]);

            if (sources.Count == 0)
            {
                MessageBox.Show(this, "Select at least one logging service.", "Selection Required",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SelectedSources = sources;
            DialogResult = DialogResult.OK;
        }
    }
}
