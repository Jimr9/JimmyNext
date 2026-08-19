using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Lets the user reorder (and hide) the Edit Log tab's list columns. Same
    // CheckedListBox + Move Up/Down + Restore Default shape as RowDisplayOrderDlg's
    // per-tab pattern, but a small standalone dialog scoped to LogbookWindow --
    // RowDisplayOrderDlg's three tabs are all Controller-owned lists (Stations
    // Available/Raw Decodes/Spot Watch) with their own tightly-coupled constructor
    // signature, so a 4th tab there would mean routing this through Controller for
    // no real benefit. This stays self-contained in the Logbook window instead.
    internal class EditLogRowOrderDlg : Form
    {
        public static readonly string[] DefaultFields =
            { "date", "time", "callsign", "band", "mode", "state", "country", "confirmed", "source" };

        public static readonly Dictionary<string, string> FieldLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "date",      "Date" },
            { "time",      "UTC Time" },
            { "callsign",  "Callsign" },
            { "band",      "Band" },
            { "mode",      "Mode" },
            { "state",     "State" },
            { "country",   "Country" },
            { "confirmed", "Confirmed" },
            { "source",    "Source" },
        };

        private readonly CheckedListBox _listBox;
        private readonly Button _moveUpButton, _moveDownButton, _restoreDefaultButton, _okButton, _cancelButton;

        public List<string> SelectedFields { get; private set; }

        public EditLogRowOrderDlg(List<string> currentOrder)
        {
            Text            = "Edit Log Row Order";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            ShowInTaskbar   = false;
            StartPosition   = FormStartPosition.CenterParent;
            ClientSize      = new Size(320, 300);

            var lbl = new Label
            {
                Text     = "Choose which columns to show, and their order:",
                Location = new Point(12, 10),
                Size     = new Size(296, 16),
            };
            Controls.Add(lbl);

            _listBox = new CheckedListBox
            {
                Location       = new Point(12, 30),
                Size           = new Size(200, 210),
                TabIndex       = 1,
                AccessibleName = "Edit Log columns",
                CheckOnClick   = true,
            };
            _listBox.SelectedIndexChanged += (s, e) => UpdateMoveButtons();
            Controls.Add(_listBox);

            _moveUpButton = new Button
            {
                Text           = "Move Up",
                AccessibleName = "Move selected column up",
                Location       = new Point(222, 30),
                Size           = new Size(86, 26),
                TabIndex       = 2,
            };
            _moveUpButton.Click += (s, e) => { MoveSelected(-1); FocusMoveButton(_moveUpButton); };
            Controls.Add(_moveUpButton);

            _moveDownButton = new Button
            {
                Text           = "Move Down",
                AccessibleName = "Move selected column down",
                Location       = new Point(222, 60),
                Size           = new Size(86, 26),
                TabIndex       = 3,
            };
            _moveDownButton.Click += (s, e) => { MoveSelected(1); FocusMoveButton(_moveDownButton); };
            Controls.Add(_moveDownButton);

            _restoreDefaultButton = new Button
            {
                Text           = "Restore Default",
                AccessibleName = "Restore default column order",
                Location       = new Point(222, 96),
                Size           = new Size(86, 40),
                TabIndex       = 4,
            };
            _restoreDefaultButton.Click += (s, e) => Populate(new List<string>(DefaultFields));
            Controls.Add(_restoreDefaultButton);

            _okButton = new Button
            {
                Text     = "OK",
                Location = new Point(78, 250),
                Size     = new Size(80, 26),
                TabIndex = 5,
            };
            _okButton.Click += OkButton_Click;
            Controls.Add(_okButton);

            _cancelButton = new Button
            {
                Text         = "Cancel",
                Location     = new Point(164, 250),
                Size         = new Size(80, 26),
                TabIndex     = 6,
                DialogResult = DialogResult.Cancel,
            };
            Controls.Add(_cancelButton);

            AcceptButton = _okButton;
            CancelButton = _cancelButton;

            Populate(currentOrder);
        }

        private void Populate(List<string> currentOrder)
        {
            var selectedSet   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var orderedFields = new List<string>();

            if (currentOrder != null)
            {
                foreach (var field in currentOrder)
                {
                    if (string.IsNullOrWhiteSpace(field)) continue;
                    if (!DefaultFields.Contains(field, StringComparer.OrdinalIgnoreCase)) continue;
                    if (orderedFields.Any(f => string.Equals(f, field, StringComparison.OrdinalIgnoreCase))) continue;
                    orderedFields.Add(field);
                    selectedSet.Add(field);
                }
            }
            foreach (var field in DefaultFields)
                if (!orderedFields.Any(f => string.Equals(f, field, StringComparison.OrdinalIgnoreCase)))
                    orderedFields.Add(field);

            _listBox.Items.Clear();
            foreach (var field in orderedFields)
                _listBox.Items.Add(new FieldItem(field), selectedSet.Contains(field));

            if (_listBox.Items.Count > 0) _listBox.SelectedIndex = 0;
            UpdateMoveButtons();
        }

        private void UpdateMoveButtons()
        {
            int index = _listBox.SelectedIndex;
            _moveUpButton.Enabled   = index > 0;
            _moveDownButton.Enabled = index >= 0 && index < _listBox.Items.Count - 1;
        }

        private void FocusMoveButton(Button b) =>
            BeginInvoke((Action)(() => (b.Enabled ? (Control)b : _listBox).Focus()));

        private void MoveSelected(int direction)
        {
            int index = _listBox.SelectedIndex;
            if (index < 0) return;
            int target = index + direction;
            if (target < 0 || target >= _listBox.Items.Count) return;

            object curItem = _listBox.Items[index];
            bool   curChecked = _listBox.GetItemChecked(index);
            object tgtItem = _listBox.Items[target];
            bool   tgtChecked = _listBox.GetItemChecked(target);

            _listBox.Items[target] = curItem;
            _listBox.Items[index]  = tgtItem;
            _listBox.SetItemChecked(target, curChecked);
            _listBox.SetItemChecked(index, tgtChecked);
            _listBox.SelectedIndex = target;
        }

        private void OkButton_Click(object sender, EventArgs e)
        {
            var fields = new List<string>();
            for (int i = 0; i < _listBox.Items.Count; i++)
                if (_listBox.GetItemChecked(i))
                    fields.Add(((FieldItem)_listBox.Items[i]).Id);

            if (fields.Count == 0)
            {
                MessageBox.Show(this, "Select at least one column.", "Selection Required",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SelectedFields = fields;
            DialogResult = DialogResult.OK;
        }

        private class FieldItem
        {
            public string Id { get; }
            public FieldItem(string id) => Id = id;
            public override string ToString() =>
                FieldLabels.TryGetValue(Id, out var label) ? label : Id;
        }
    }
}
