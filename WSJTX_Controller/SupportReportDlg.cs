using System;
using System.Drawing;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    internal sealed class SupportReportDlg : Form
    {
        private static bool IsEmail(string s)
        {
            if (string.IsNullOrWhiteSpace(s) || s.Length > 254 || s.Contains(" ")) return false;
            try { return new System.Net.Mail.MailAddress(s).Address == s && s.IndexOf('.', s.IndexOf('@')) > 0; }
            catch { return false; }
        }

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
        public bool Upload => _uploadRadio.Checked;
        private RadioButton _uploadRadio;

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
            var lblCallsign = new Label { Text = "Callsign (needed to send)", Location = new Point(lx, y), AutoSize = true };
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
            var lblName = new Label { Text = "Name (needed to send)", Location = new Point(lx, y), AutoSize = true };
            y += 18;
            _nameTextBox = new TextBox
            {
                Location       = new Point(lx, y),
                Size           = new Size(fw, 22),
                AccessibleName = "Name",
                TabIndex       = tab++,
                Text           = SupportLocal.SenderName,   // the last request's, if any
            };
            y += 32;

            // ---- Email ----
            var lblEmail = new Label { Text = "Email address (needed to send)", Location = new Point(lx, y), AutoSize = true };
            y += 18;
            _emailTextBox = new TextBox
            {
                Location       = new Point(lx, y),
                Size           = new Size(fw, 22),
                AccessibleName = "Email address",
                TabIndex       = tab++,
                Text           = SupportLocal.SenderEmail,
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

            // 2.0.81: how the report reaches KB0UZT. Arrow keys move between the two choices.
            var sendGroup = new GroupBox
            {
                Text     = "How to send it",
                Location = new Point(lx, y),
                Size     = new Size(fw, 66),
                TabIndex = tab++,
            };
            _uploadRadio = new RadioButton
            {
                Text     = "Send to KB0UZT",
                Location = new Point(10, 18),
                AutoSize = true,
                Checked  = true,
                TabIndex = 0,
            };
            var saveRadio = new RadioButton
            {
                Text     = "Save to my computer only",
                Location = new Point(10, 40),
                AutoSize = true,
                TabIndex = 1,
            };
            sendGroup.Controls.Add(_uploadRadio);
            sendGroup.Controls.Add(saveRadio);
            y += 72;

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
                _includeLogbookCheckBox, hintPrivacy, sendGroup,
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
            // Sending (2026-10-07): the support service needs a base callsign, a name and an email
            // address to answer; saving only needs none of them.
            if (Upload && !SupportService.Available)
            {
                MessageBox.Show("Sending is not available in this copy of Jimmy Next. Choose Save to my computer only.",
                    "Create Support Report", MessageBoxButtons.OK, MessageBoxIcon.Information);
                _uploadRadio.Focus();
                return;
            }
            if (Upload)
            {
                string problem = SupportService.BaseCallsign(Callsign) == null ? "Enter your callsign to send the report."
                    : PersonName.Length == 0 || PersonName.Length > 120 ? "Enter your name to send the report."
                    : !IsEmail(Email) ? "Enter a valid email address to send the report, so KB0UZT can answer."
                    : null;
                if (problem != null)
                {
                    MessageBox.Show(problem, "Create Support Report", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    (SupportService.BaseCallsign(Callsign) == null ? _callsignTextBox : PersonName.Length == 0 || PersonName.Length > 120 ? _nameTextBox : _emailTextBox).Focus();
                    return;
                }
            }
            DialogResult = DialogResult.OK;
        }
    }
}
