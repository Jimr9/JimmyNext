using System;
using System.Drawing;
using System.Windows.Forms;
using WsjtxUdpLib.Messages.Out;

namespace WSJTX_Controller
{
    internal class ManualCallDlg : Form
    {
        private TextBox _callTextBox;
        private Button _okButton;
        private Button _cancelButton;
        private readonly LookupManager _lookupManager;

        public string Callsign { get; private set; }

        // lastCallsign pre-fills the box (selected, so typing overwrites it right away)
        // instead of always starting blank -- lets Enter/OK alone repeat the same call.
        // lookupManager is optional; when enabled, OK triggers a live grid-square lookup
        // and a Yes/No confirmation before actually accepting the call (a sanity check
        // that the callsign is real/known) -- a missing grid never blocks calling, since
        // plenty of valid callsigns won't have anything on file.
        public ManualCallDlg(string lastCallsign = "", LookupManager lookupManager = null)
        {
            _lookupManager = lookupManager;

            Text = "Call Callsign";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(270, 92);

            var lbl = new Label
            {
                Text = "Callsign:",
                Location = new Point(12, 16),
                AutoSize = true,
            };

            _callTextBox = new TextBox
            {
                Location = new Point(82, 13),
                Size = new Size(168, 20),
                AccessibleName = "Callsign",
                CharacterCasing = CharacterCasing.Upper,
                Text = lastCallsign ?? "",
            };
            _callTextBox.SelectAll();

            _okButton = new Button
            {
                Text = "OK",
                Location = new Point(82, 54),
                Size = new Size(80, 26),
            };
            _okButton.Click += OkButton_Click;

            _cancelButton = new Button
            {
                Text = "Cancel",
                Location = new Point(170, 54),
                Size = new Size(80, 26),
                DialogResult = DialogResult.Cancel,
            };

            Controls.AddRange(new Control[] { lbl, _callTextBox, _okButton, _cancelButton });
            AcceptButton = _okButton;
            CancelButton = _cancelButton;
        }

        private async void OkButton_Click(object sender, EventArgs e)
        {
            string callsign = _callTextBox.Text.Trim().ToUpper();

            if (callsign.Length == 0)
            {
                MessageBox.Show("Please enter a callsign.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _callTextBox.Focus();
                return;
            }

            if (WsjtxMessage.IsInvalidCall(callsign))
            {
                MessageBox.Show($"'{callsign}' does not appear to be a valid callsign.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _callTextBox.SelectAll();
                _callTextBox.Focus();
                return;
            }

            if (_lookupManager != null && _lookupManager.Enabled)
            {
                _okButton.Enabled = false;
                _cancelButton.Enabled = false;
                LookupRecord rec = null;
                try { rec = await _lookupManager.LookupPrimaryAsync(callsign); }
                catch { /* best-effort -- treat like "not found" below */ }
                _okButton.Enabled = true;
                _cancelButton.Enabled = true;

                string gridText = !string.IsNullOrEmpty(rec?.Grid) ? rec.Grid : "not found";
                var result = MessageBox.Show(this,
                    $"Grid square: {gridText}{Environment.NewLine}{Environment.NewLine}Call {callsign}?",
                    "Confirm Call", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (result != DialogResult.Yes)
                {
                    _callTextBox.SelectAll();
                    _callTextBox.Focus();
                    return;
                }
            }

            Callsign = callsign;
            DialogResult = DialogResult.OK;
        }
    }
}
