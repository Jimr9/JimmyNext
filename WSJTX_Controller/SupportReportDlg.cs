using System;
using System.Drawing;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    internal sealed class SupportReportDlg : Form
    {
        private readonly TextBox  _callsignTextBox;
        private readonly TextBox  _nameTextBox;
        private readonly TextBox  _emailTextBox;
        private readonly ComboBox _problemTypeCombo;
        private readonly TextBox  _descTextBox;
        private readonly TextBox  _stepsTextBox;

        public string Callsign    => _callsignTextBox.Text.Trim();
        public string PersonName  => _nameTextBox.Text.Trim();
        public string Email       => _emailTextBox.Text.Trim();
        public string ProblemType => _problemTypeCombo.Text;
        public string Description => _descTextBox.Text.Trim();
        public string Steps       => _stepsTextBox.Text.Trim();
        public bool IncludeLogbook => _includeLogbookCheckBox.Checked;
        private CheckBox _includeLogbookCheckBox;

        public SupportReportDlg(string prefillCallsign)
        {
            Text            = "Create Support Report";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            ShowInTaskbar   = false;
            StartPosition   = FormStartPosition.CenterParent;

            const int lx = 12;
            const int fw = 450;
            int tab = 0;
            int y = 12;

            // ---- Callsign ----
            var lblCallsign = new Label { Text = "Callsign (optional)", Location = new Point(lx, y), AutoSize = true };
            y += 18;
            _callsignTextBox = new TextBox
            {
                Location        = new Point(lx, y),
                Size            = new Size(fw, 22),
                AccessibleName  = "Callsign",
                CharacterCasing = CharacterCasing.Upper,
                TabIndex        = tab++,
            };
            _callsignTextBox.Text = prefillCallsign ?? "";
            y += 32;

            // ---- Name ----
            var lblName = new Label { Text = "Name (optional)", Location = new Point(lx, y), AutoSize = true };
            y += 18;
            _nameTextBox = new TextBox
            {
                Location       = new Point(lx, y),
                Size           = new Size(fw, 22),
                AccessibleName = "Name",
                TabIndex       = tab++,
            };
            y += 32;

            // ---- Email ----
            var lblEmail = new Label { Text = "Email address (optional)", Location = new Point(lx, y), AutoSize = true };
            y += 18;
            _emailTextBox = new TextBox
            {
                Location       = new Point(lx, y),
                Size           = new Size(fw, 22),
                AccessibleName = "Email address",
                TabIndex       = tab++,
            };
            y += 32;

            // ---- Problem type ----
            var lblType = new Label { Text = "Problem type", Location = new Point(lx, y), AutoSize = true };
            y += 18;
            _problemTypeCombo = new ComboBox
            {
                Location       = new Point(lx, y),
                Size           = new Size(fw, 22),
                DropDownStyle  = ComboBoxStyle.DropDownList,
                AccessibleName = "Problem type",
                TabIndex       = tab++,
            };
            _problemTypeCombo.Items.AddRange(new object[]
                { "Bug / Problem", "Feature Request", "Accessibility", "Question / Other" });
            _problemTypeCombo.SelectedIndex = 0;
            y += 32;

            // ---- Problem description (required) ----
            var lblDesc = new Label { Text = "Problem description (required)", Location = new Point(lx, y), AutoSize = true };
            y += 18;
            var hintDesc = new Label
            {
                Text      = "Describe what you were doing, what you expected to happen, and what actually happened.",
                Location  = new Point(lx, y),
                Size      = new Size(fw, 16),
                Font      = new Font(SystemFonts.DefaultFont.FontFamily, 7.5f),
                ForeColor = SystemColors.GrayText,
                AutoSize  = false,
            };
            y += 16;
            _descTextBox = new TextBox
            {
                Location       = new Point(lx, y),
                Size           = new Size(fw, 80),
                Multiline      = true,
                // Multiline alone doesn't make Enter insert a newline -- AcceptsReturn defaults
                // to false regardless, so without this, Enter fell through to the form's
                // AcceptButton (OK) instead, making it impossible to write more than one line.
                AcceptsReturn  = true,
                ScrollBars     = ScrollBars.Vertical,
                AccessibleName = "Problem description",
                TabIndex       = tab++,
            };
            y += 90;

            // ---- Steps to reproduce (optional) ----
            var lblSteps = new Label { Text = "Steps to reproduce (optional)", Location = new Point(lx, y), AutoSize = true };
            y += 18;
            var hintSteps = new Label
            {
                Text      = "If someone else wanted to reproduce this problem, what steps should they follow?",
                Location  = new Point(lx, y),
                Size      = new Size(fw, 16),
                Font      = new Font(SystemFonts.DefaultFont.FontFamily, 7.5f),
                ForeColor = SystemColors.GrayText,
                AutoSize  = false,
            };
            y += 16;
            _stepsTextBox = new TextBox
            {
                Location       = new Point(lx, y),
                Size           = new Size(fw, 80),
                Multiline      = true,
                AcceptsReturn  = true,
                ScrollBars     = ScrollBars.Vertical,
                AccessibleName = "Steps to reproduce",
                TabIndex       = tab++,
            };
            y += 90;

            // 2.0.80 (W0CAS): the report carries the whole Jimmy Next folder (passwords blanked,
            // downloadable lookup data left out). The logbook is the operator's own choice.
            _includeLogbookCheckBox = new CheckBox
            {
                Text           = "Include my logbook (my contacts; helps with logbook problems)",
                AccessibleName = "Include my logbook",
                Location       = new Point(lx, y),
                AutoSize       = true,
                Checked        = true,
                TabIndex       = tab++,
            };
            y += 26;
            var hintPrivacy = new Label
            {
                Text      = "Settings, logs and Jimmy Next's folder are included; passwords and logins are never included.",
                Location  = new Point(lx, y),
                Size      = new Size(fw, 16),
                Font      = new Font(SystemFonts.DefaultFont.FontFamily, 7.5f),
                ForeColor = SystemColors.GrayText,
                AutoSize  = false,
            };
            y += 18;

            // ---- Buttons ----
            y += 4;
            var okButton = new Button
            {
                Text     = "OK",
                Size     = new Size(80, 26),
                Location = new Point(lx, y),
                TabIndex = tab++,
            };
            okButton.Click += OkButton_Click;

            var cancelButton = new Button
            {
                Text         = "Cancel",
                Size         = new Size(80, 26),
                Location     = new Point(lx + 90, y),
                TabIndex     = tab++,
                DialogResult = DialogResult.Cancel,
            };

            Controls.AddRange(new Control[]
            {
                lblCallsign, _callsignTextBox,
                lblName,     _nameTextBox,
                lblEmail,    _emailTextBox,
                lblType,     _problemTypeCombo,
                lblDesc,     hintDesc,  _descTextBox,
                lblSteps,    hintSteps, _stepsTextBox,
                _includeLogbookCheckBox, hintPrivacy,
                okButton,    cancelButton,
            });

            AcceptButton = okButton;
            CancelButton = cancelButton;
            ClientSize   = new Size(fw + 26, y + 26 + 12);
        }

        private void OkButton_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_descTextBox.Text))
            {
                MessageBox.Show(
                    "Please enter a problem description before continuing.",
                    "Create Support Report",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                _descTextBox.Focus();
                return;
            }
            DialogResult = DialogResult.OK;
        }
    }
}
