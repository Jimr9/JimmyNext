using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // The support report form. Since 2026-10-12 it does not block the rest of Jimmy Next (ticket
    // KB0UZT-20261008): it is a window of its own (Controller.OpenSupportReport keeps the one
    // instance), so the operator can leave it open, look at the main window, Options, Spots or
    // the Logbook, and come back to everything as it was typed. Nothing is built or sent until
    // OK is pressed and the question after it is answered Yes; then this window makes the
    // report itself and closes when that worked. Closing it with something typed asks first.
    // A profile switch closes every window: what was typed is then kept (KeptDraft) and comes
    // back the next time Create Support Report is opened in this run of the program.
    internal sealed class SupportReportDlg : Form
    {
        internal sealed class Draft
        {
            public string Callsign, Name, Email, Description, Steps;
            public int ProblemType;
            public bool IncludeLogbook, Upload;
        }
        internal static Draft KeptDraft;
        private static string _lastSupportFolder;   // where the last save-only report went, this session

        private readonly Controller _ctrl;
        private Button _okButton;
        private bool _submitting, _done, _closingForOwner;

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

        public SupportReportDlg(Controller ctrl, string prefillCallsign)
        {
            _ctrl           = ctrl;
            Text            = "Create Support Report";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            // Its own window: on the taskbar and in Alt+Tab, and it can be minimized. No Owner --
            // an owned window stays in front of the main window (Controller.OpenLogbookWindow).
            MinimizeBox     = true;
            ShowInTaskbar   = true;
            StartPosition   = FormStartPosition.CenterScreen;

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
                Text      = "Current settings and recent logs are included; passwords and logins are never included.",
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
            _okButton = okButton;

            var cancelButton = new Button
            {
                Text         = "Cancel",
                Size         = new Size(80, 26),
                Location     = new Point(lx + 90, y),
                TabIndex     = tab++,
            };
            cancelButton.Click += (s, e) => Close();   // Escape too; asks first when something is typed

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

            // What was being typed when a profile switch closed the window.
            var kept = KeptDraft;
            KeptDraft = null;
            if (kept != null)
            {
                _callsignTextBox.Text = kept.Callsign;
                _nameTextBox.Text = kept.Name;
                _emailTextBox.Text = kept.Email;
                if (kept.ProblemType >= 0 && kept.ProblemType < _problemTypeCombo.Items.Count) _problemTypeCombo.SelectedIndex = kept.ProblemType;
                _descTextBox.Text = kept.Description;
                _stepsTextBox.Text = kept.Steps;
                _includeLogbookCheckBox.Checked = kept.IncludeLogbook;
                (kept.Upload ? _uploadRadio : saveRadio).Checked = true;
            }
            FormClosing += OnFormClosing;
            AppLook.Attach(this);   // the shared look: theme, text size, list colours
        }

        // Something the operator would lose: a description or steps typed and not yet sent.
        internal bool HasDraft => !_done && (_descTextBox.Text.Trim().Length > 0 || _stepsTextBox.Text.Trim().Length > 0);
        internal bool Submitting => _submitting;

        internal Draft Snapshot() => new Draft
        {
            Callsign = _callsignTextBox.Text, Name = _nameTextBox.Text, Email = _emailTextBox.Text,
            ProblemType = _problemTypeCombo.SelectedIndex, Description = _descTextBox.Text, Steps = _stepsTextBox.Text,
            IncludeLogbook = _includeLogbookCheckBox.Checked, Upload = _uploadRadio.Checked,
        };

        // Tests: type into the form.
        internal void TestType(string description, string steps) { _descTextBox.Text = description; _stepsTextBox.Text = steps; }

        // The main window is closing. keepDraft (a profile switch: the program goes on) keeps
        // what was typed for the next time the form is opened; otherwise it was already asked
        // about (Controller_FormClosing) and goes with the program.
        internal void CloseForOwner(bool keepDraft)
        {
            if (keepDraft && HasDraft) KeptDraft = Snapshot();
            _closingForOwner = true;
            Close();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_closingForOwner || _done || e.CloseReason != CloseReason.UserClosing) return;
            if (_submitting)
            {
                MessageBox.Show(this, "The report is being made. Please wait until it has finished.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                e.Cancel = true;
                return;
            }
            if (!HasDraft) return;
            if (MessageBox.Show(this, "Close this support report and discard what you typed?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                e.Cancel = true;
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
            _ = SubmitAsync();
        }

        // OK, after validation: the question, the place to save (save only), the report, and
        // sending it. The fields are read ONCE, here, so what is sent is what was on screen when
        // OK was pressed. OK is off while this runs, so a second press cannot send it twice; the
        // rest of Jimmy Next stays usable. Anything that stops short of a finished report leaves
        // the window open with everything typed.
        private async Task SubmitAsync()
        {
            if (_submitting || _done) return;
            var d = Snapshot();
            string callsign = d.Callsign.Trim(), name = d.Name.Trim(), email = d.Email.Trim();
            string type = ProblemType, description = d.Description.Trim(), steps = d.Steps.Trim();

            string confirmText =
                (d.Upload
                    ? "Jimmy Next will create a support report and send it to KB0UZT with your callsign, name and email. A copy is kept on this computer.\n\n"
                    : "Jimmy Next will create a support report ZIP. Next you choose where to save it.\n\n") +
                "It contains your description, your current settings with passwords removed, the last few days of logs" +
                (d.IncludeLogbook ? ", a copy of your logbook" : "") + " and Windows information.\n\n" +
                (d.Upload ? "Send the report?" : "Create the report?");
            if (MessageBox.Show(this, confirmText, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            // The operator chooses where a save-only report goes (2.0.81); a sent one keeps its
            // copy in the usual place, and the message afterwards says where.
            string offered = SupportReportBuilder.DefaultZipPath(callsign, type, name, email);
            string zipPath = offered;
            if (!d.Upload)
                using (var save = new SaveFileDialog
                {
                    Title            = "Save support report",
                    Filter           = "ZIP file (*.zip)|*.zip",
                    FileName         = Path.GetFileName(offered),
                    InitialDirectory = _lastSupportFolder ?? Path.GetDirectoryName(offered),
                    OverwritePrompt  = true,
                })
                {
                    if (save.ShowDialog(this) != DialogResult.OK) return;
                    zipPath = save.FileName;
                    _lastSupportFolder = Path.GetDirectoryName(zipPath);
                }

            _submitting = true;
            _okButton.Enabled = false;
            bool finished = false;
            try
            {
                var result = SupportReportBuilder.Build(_ctrl, callsign, name, email, type, description, steps, d.IncludeLogbook, zipPath);
                if (!result.Success)
                {
                    MessageBox.Show(this, $"Could not create support report:\n{result.Error}", "Support Report Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                finished = await SupportReportDelivery.Deliver(this, d.Upload, result.ZipPath, callsign, name, email, type, null);
            }
            finally
            {
                _submitting = false;
                if (!IsDisposed)
                {
                    _okButton.Enabled = true;
                    if (finished) { _done = true; Close(); }
                }
            }
        }
    }
}
