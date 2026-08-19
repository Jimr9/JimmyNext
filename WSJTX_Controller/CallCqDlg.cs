using System;
using System.Drawing;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Main-screen "Call CQ" button target -- configures what kind of CQ Jimmy calls and which
    // transmit slot it uses. Opening/closing this dialog never starts or stops calling CQ itself
    // (Alt+C keeps doing that, unchanged) -- OK here only saves settings for next time Alt+C runs.
    //
    // Uses its own fresh checkboxes/text box rather than reparenting Controller's shared
    // callNonDirCqCheckBox/callCqDxCheckBox/callDirCqCheckBox/directedTextBox: those controls
    // stopped responding to clicks once moved into a separate top-level Form here (Checked
    // never flipped despite Click firing normally -- root cause not pinned down, but reparenting
    // them across forms is clearly not safe). Local controls read current state on open and
    // write the real, shared fields back on OK, so production code (NextDirCq() etc.) is
    // unaffected either way.
    internal class CallCqDlg : Form
    {
        private readonly Controller _ctrl;
        private readonly WsjtxClient _wsjtxClient;

        private CheckBox _nonDirCqCb;
        private CheckBox _cqDxCb;
        private CheckBox _dirCqCb;
        private TextBox _directedTb;
        private ComboBox _directedComboBox;
        private ComboBox _slotComboBox;
        private Button _findSlotButton;
        private Label _slotStatusLabel;
        private Button _okButton;
        private Button _cancelButton;

        private readonly System.Windows.Forms.Timer _slotStatusTimer;

        public CallCqDlg(Controller ctrl, WsjtxClient wsjtxClient)
        {
            _ctrl = ctrl;
            _wsjtxClient = wsjtxClient;

            Text = "Call CQ";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            // Shown non-modally with its own taskbar entry (like Logbook/Options/Help) so it
            // can be left open while operating and reached again via Alt+Tab or the taskbar --
            // CenterScreen rather than CenterParent since there's deliberately no Owner set
            // (see Controller.OpenCallCqDialog for why).
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(420, 330);

            var callGroupBox = new GroupBox
            {
                Text           = "Call:",
                AccessibleName = "CQ type",
                Location       = new Point(12, 12),
                Size           = new Size(396, 92),
                TabStop        = false,
                TabIndex       = 0,
            };

            _nonDirCqCb = new CheckBox
            {
                Text     = "Call 'CQ' (non-directed)",
                Location = new Point(10, 20),
                AutoSize = true,
                Checked  = ctrl.callNonDirCqCheckBox.Checked,
                TabIndex = 0,
            };
            _cqDxCb = new CheckBox
            {
                Text     = "Call 'CQ DX'",
                Location = new Point(10, 42),
                AutoSize = true,
                Checked  = ctrl.callCqDxCheckBox.Checked,
                TabIndex = 1,
            };
            _dirCqCb = new CheckBox
            {
                Text     = "Call CQ directed to:",
                Location = new Point(10, 64),
                AutoSize = true,
                Checked  = ctrl.callDirCqCheckBox.Checked,
                TabIndex = 2,
            };
            _dirCqCb.CheckedChanged += (s, e) => { _directedTb.Enabled = _dirCqCb.Checked; RefreshDirectedCombo(selectLocked: false); };

            string currentDirText = ctrl.directedTextBox.Text;
            _directedTb = new TextBox
            {
                Location       = new Point(140, 62),
                Size           = new Size(110, 20),
                AccessibleName = "Directed CQ codes",
                Text           = currentDirText == IniPlaceholder ? "" : currentDirText,
                Enabled        = ctrl.callDirCqCheckBox.Checked,
                TabIndex       = 3,
            };
            _directedTb.TextChanged += (s, e) => RefreshDirectedCombo(selectLocked: false);

            var separateHint = new Label
            {
                Text     = "(separate by spaces)",
                Location = new Point(258, 65),
                AutoSize = true,
                ForeColor = System.Drawing.SystemColors.GrayText,
                TabStop  = false,
            };

            callGroupBox.Controls.AddRange(new Control[] { _nonDirCqCb, _cqDxCb, _dirCqCb, _directedTb, separateHint });

            var directedLbl = new Label
            {
                Text     = "Directed CQ code to use:",
                Location = new Point(12, 116),
                AutoSize = true,
                TabStop  = false,
            };
            _directedComboBox = new ComboBox
            {
                Location       = new Point(200, 113),
                Size           = new Size(200, 21),
                DropDownStyle  = ComboBoxStyle.DropDownList,
                AccessibleName = "Directed CQ code to use",
                TabIndex       = 4,
            };

            var slotLbl = new Label
            {
                Text     = "Transmit slot:",
                Location = new Point(12, 148),
                AutoSize = true,
                TabStop  = false,
            };
            _slotComboBox = new ComboBox
            {
                Location       = new Point(200, 145),
                Size           = new Size(200, 21),
                DropDownStyle  = ComboBoxStyle.DropDownList,
                AccessibleName = "Transmit slot",
                TabIndex       = 5,
            };
            _slotComboBox.Items.Add("TX1 (Even)");
            _slotComboBox.Items.Add("TX2 (Odd)");
            _slotComboBox.SelectedIndex = _wsjtxClient.txFirst ? 0 : 1;

            string slotKeyText = ctrl.hotkeyConfig != null && ctrl.hotkeyConfig[HotkeyAction.AnalyzeSlot] != Keys.None
                ? HotkeyConfig.FormatKeys(ctrl.hotkeyConfig[HotkeyAction.AnalyzeSlot])
                : "no hotkey assigned";
            _findSlotButton = new Button
            {
                Text           = $"Find open slot ({slotKeyText})",
                Location       = new Point(12, 182),
                Size           = new Size(240, 26),
                AccessibleName = "Find open transmit slot",
                TabIndex       = 6,
            };
            _findSlotButton.Click += FindSlotButton_Click;

            _slotStatusLabel = new Label
            {
                Location = new Point(12, 214),
                Size     = new Size(396, 32),
                AutoSize = false,
                TabStop  = false,
            };

            _okButton = new Button
            {
                Text     = "OK",
                Location = new Point(148, 288),
                Size     = new Size(80, 26),
                TabIndex = 7,
            };
            _okButton.Click += OkButton_Click;

            _cancelButton = new Button
            {
                Text         = "Cancel",
                Location     = new Point(236, 288),
                Size         = new Size(80, 26),
                DialogResult = DialogResult.Cancel,
                TabIndex     = 8,
            };
            // A button's DialogResult only auto-closes its form when shown via ShowDialog();
            // this dialog uses Show() (see Controller.OpenCallCqDialog), so Close() is explicit.
            _cancelButton.Click += (s, e) => Close();

            Controls.Add(callGroupBox);
            Controls.AddRange(new Control[]
            {
                directedLbl, _directedComboBox, slotLbl, _slotComboBox,
                _findSlotButton, _slotStatusLabel, _okButton, _cancelButton,
            });
            AcceptButton = _okButton;
            CancelButton = _cancelButton;

            RefreshDirectedCombo(selectLocked: true);
            RefreshSlotStatusLabel();

            // Slot analysis can finish well after the button is clicked (it needs a full
            // decode cycle) -- poll rather than requiring a live event hook into the
            // decode-processing path just for this dialog's own status line.
            _slotStatusTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _slotStatusTimer.Tick += (s, e) => RefreshSlotStatusLabel();
            _slotStatusTimer.Start();

            FormClosed += (s, e) => { _slotStatusTimer.Stop(); _slotStatusTimer.Dispose(); };
        }

        // Matches Controller's private "separateBySpaces" placeholder constant -- duplicated
        // here (rather than exposed publicly) since this is the only outside consumer of it.
        private const string IniPlaceholder = "(separate by spaces)";

        private string[] LocalDirectedEntries() =>
            _directedTb.Text.Trim().ToUpper().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        // selectLocked: true only on initial load -- preselect whatever was locked in last
        // time (ctrl.directedCqLockedEntry), falling back to Random if it's blank or no longer
        // one of the current entries. Later refreshes (the text box being edited live) instead
        // try to keep whatever the operator has currently selected in this dialog, so retyping
        // one character doesn't silently bounce the combo back to Random on every keystroke.
        private void RefreshDirectedCombo(bool selectLocked)
        {
            string keepSelected = !selectLocked ? _directedComboBox.SelectedItem as string : null;
            string[] entries = _dirCqCb.Checked ? LocalDirectedEntries() : new string[0];

            _directedComboBox.BeginUpdate();
            _directedComboBox.Items.Clear();
            _directedComboBox.Items.Add("Random");
            foreach (string entry in entries) _directedComboBox.Items.Add(entry);
            _directedComboBox.EndUpdate();

            string wantSelected = selectLocked ? _ctrl.directedCqLockedEntry : keepSelected;
            int idx = !string.IsNullOrEmpty(wantSelected)
                ? _directedComboBox.Items.IndexOf(wantSelected)
                : -1;
            _directedComboBox.SelectedIndex = idx >= 0 ? idx : 0;      // 0 == "Random"
        }

        private void FindSlotButton_Click(object sender, EventArgs e)
        {
            _wsjtxClient.StartSlotAnalysis(false);
            RefreshSlotStatusLabel();
        }

        private void RefreshSlotStatusLabel()
        {
            if (!_ctrl.freqCheckBox.Checked)
                _slotStatusLabel.Text = "Slot finding is off (enable 'Use best Tx frequency' in Options to use it).";
            else if (_wsjtxClient.AnalysisNeeded)
                _slotStatusLabel.Text = "Not yet checked this session.";
            else
                _slotStatusLabel.Text = "Already checked this session.";
        }

        private void OkButton_Click(object sender, EventArgs e)
        {
            bool wantNonDir = _nonDirCqCb.Checked;
            bool wantCqDx   = _cqDxCb.Checked;
            bool wantDirCq  = _dirCqCb.Checked;
            // At least one must stay selected -- matches the existing app-wide invariant
            // (callNonDirCqCheckBox_CheckedChanged enforces the same rule today).
            if (!wantNonDir && !wantCqDx && !wantDirCq) wantNonDir = true;

            _ctrl.callNonDirCqCheckBox.Checked = wantNonDir;
            _ctrl.callCqDxCheckBox.Checked = wantCqDx;
            _ctrl.callDirCqCheckBox.Checked = wantDirCq;
            _ctrl.directedTextBox.Text = _directedTb.Text.Trim();

            string selected = _directedComboBox.SelectedItem as string;
            _ctrl.directedCqLockedEntry = (string.IsNullOrEmpty(selected) || selected == "Random") ? "" : selected;

            bool desiredTxFirst = _slotComboBox.SelectedIndex == 0;
            if (desiredTxFirst != _wsjtxClient.txFirst) _wsjtxClient.ToggleTxFirst();

            // DialogResult alone only auto-closes a form shown with ShowDialog(); this dialog
            // is shown with Show() (see Controller.OpenCallCqDialog), so Close() is explicit.
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
