using System;
using System.Drawing;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Small reusable single-line text prompt (e.g. "Rename", "New Rule Id").
    internal class TextPromptDlg : Form
    {
        private readonly TextBox _textBox;

        public string Value => _textBox.Text.Trim();

        public TextPromptDlg(string title, string prompt, string initialValue, string accessibleName)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(360, 110);

            var lbl = new Label
            {
                Text = prompt,
                Location = new Point(12, 14),
                AutoSize = true,
            };

            _textBox = new TextBox
            {
                Location = new Point(12, 36),
                Size = new Size(336, 20),
                Text = initialValue ?? "",
                AccessibleName = accessibleName ?? prompt,
            };

            var okButton = new Button
            {
                Text = "OK",
                Location = new Point(184, 72),
                Size = new Size(80, 26),
            };
            okButton.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(_textBox.Text))
                {
                    MessageBox.Show(this, "A value is required.", "Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                DialogResult = DialogResult.OK;
            };

            var cancelButton = new Button
            {
                Text = "Cancel",
                Location = new Point(268, 72),
                Size = new Size(80, 26),
                DialogResult = DialogResult.Cancel,
            };

            Controls.AddRange(new Control[] { lbl, _textBox, okButton, cancelButton });
            AcceptButton = okButton;
            CancelButton = cancelButton;
            Shown += (s, e) => { _textBox.Focus(); _textBox.SelectAll(); };
            AppLook.Attach(this);   // the shared look: theme, text size, list colours
        }
    }
}
