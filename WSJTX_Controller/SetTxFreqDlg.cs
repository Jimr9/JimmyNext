using System;
using System.Drawing;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Alt+W ("Set Transmit Frequency..."): type an exact transmit audio offset in Hz. The
    // accessible equivalent of dragging the red waterfall marker to a specific spot. Mirrors
    // ManualCallDlg's hand-coded small-Form pattern (no Designer file) -- one labelled
    // NumericUpDown, OK / Cancel, Enter accepts. Range/step match Engine::set_tx_offset's own
    // 200-4000 Hz passband clamp and the Options "Frequency step (Hz)" value.
    internal class SetTxFreqDlg : Form
    {
        private readonly NumericUpDown _hzUpDown;

        public int Hz { get; private set; }

        public SetTxFreqDlg(int currentHz, int minHz, int maxHz, int step)
        {
            Text = "Set Transmit Frequency";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(270, 92);

            var lbl = new Label
            {
                Text = "Transmit Hz:",
                Location = new Point(12, 18),
                AutoSize = true,
            };

            _hzUpDown = new NumericUpDown
            {
                Location = new Point(96, 14),
                Size = new Size(90, 20),
                Minimum = minHz,
                Maximum = maxHz,
                Increment = step,
                AccessibleName = "Transmit frequency in hertz",
                Value = Math.Max(minHz, Math.Min(maxHz, currentHz <= 0 ? 1500 : currentHz)),
            };
            _hzUpDown.Select(0, _hzUpDown.Text.Length);

            var okButton = new Button
            {
                Text = "OK",
                Location = new Point(82, 54),
                Size = new Size(80, 26),
            };
            okButton.Click += (s, e) =>
            {
                Hz = (int)_hzUpDown.Value;
                DialogResult = DialogResult.OK;
                Close();
            };

            var cancelButton = new Button
            {
                Text = "Cancel",
                Location = new Point(170, 54),
                Size = new Size(80, 26),
                DialogResult = DialogResult.Cancel,
            };

            Controls.AddRange(new Control[] { lbl, _hzUpDown, okButton, cancelButton });
            AcceptButton = okButton;
            CancelButton = cancelButton;
        }
    }
}
