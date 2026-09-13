using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Options > Notifications > "Notification order..." (2026-09-11 speech-batching fix). Lets
    // the operator control the order in which joinable notification categories (Smart Start /
    // Station Watch narration, plus AwardsNeeded and the routine status line -- see
    // NotificationCenter.WatchEventTypes / NotificationDefaults.DefaultJoinOrder) are composed
    // into one utterance when more than one lands in the same reconciled batch or lifecycle-
    // boundary flush (SpeechCoordinator.ComposeMerged). Order only -- there is no enable/disable
    // here, that is already controlled per-type on the main Notifications policy grid.
    //
    // Built directly on RowDisplayOrderDlg.cs / RankOrderDlg.cs's existing Move Up/Move Down/
    // Restore Defaults pattern (PopulateList/MoveSelectedItem/UpdateMoveButtons shape, focus-
    // follows-move via BeginInvoke) -- same keyboard/accessibility conventions, not a new design.
    // A plain ListBox, not a CheckedListBox: RowDisplayOrderDlg/RankOrderDlg both rely on native
    // ListBox "{label}, item N of M" announcement on selection change with NO extra
    // RaiseAccessibleAlert call, and this dialog deliberately matches that -- see the design
    // writeup's own reasoning for why stacking an explicit UIA notification on top of the native
    // announcement risks double-speaking every move, a real risk, not a hypothetical one.
    public partial class NotificationJoinOrderDlg : Form
    {
        public List<NotificationEventType> SelectedOrder { get; private set; }

        private ListBox _listBox;
        private Button _moveUpButton;
        private Button _moveDownButton;
        private Button _restoreDefaultsButton;
        private Button _okButton;
        private Button _cancelButton;

        private sealed class TypeItem
        {
            public NotificationEventType Type { get; }
            private readonly string _label;
            public TypeItem(NotificationEventType type)
            {
                Type = type;
                _label = NotificationDefaults.DisplayNames.TryGetValue(type, out var label) ? label : type.ToString();
            }
            public override string ToString() => _label;
        }

        public NotificationJoinOrderDlg(List<NotificationEventType> currentOrder)
        {
            InitializeComponent();
            PopulateList(currentOrder);
        }

        private void InitializeComponent()
        {
            this.Text = "Notification Order";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MinimizeBox = false;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.ClientSize = new Size(380, 300);
            this.ShowInTaskbar = false;

            var explainLabel = new Label
            {
                Text = "When multiple notifications are generated together, they are spoken in this order.",
                Location = new Point(12, 10),
                Size = new Size(356, 32),
                AccessibleName = "When multiple notifications are generated together, they are spoken in this order.",
            };
            this.Controls.Add(explainLabel);

            _listBox = new ListBox
            {
                Location = new Point(12, 46),
                Size = new Size(260, 208),
                AccessibleName = "Notification order",
                TabIndex = 0,
            };
            _listBox.SelectedIndexChanged += (s, e) => UpdateMoveButtons();
            this.Controls.Add(_listBox);

            _moveUpButton = new Button
            {
                Text = "Move &Up",
                AccessibleName = "Move up",
                Location = new Point(282, 46),
                Size = new Size(86, 26),
                TabIndex = 1,
            };
            _moveUpButton.Click += (s, e) => MoveSelectedItem(-1);
            this.Controls.Add(_moveUpButton);

            _moveDownButton = new Button
            {
                Text = "Move &Down",
                AccessibleName = "Move down",
                Location = new Point(282, 76),
                Size = new Size(86, 26),
                TabIndex = 2,
            };
            _moveDownButton.Click += (s, e) => MoveSelectedItem(1);
            this.Controls.Add(_moveDownButton);

            _restoreDefaultsButton = new Button
            {
                Text = "&Restore Defaults",
                AccessibleName = "Restore Defaults",
                Location = new Point(282, 112),
                Size = new Size(86, 40),
                TabIndex = 3,
            };
            _restoreDefaultsButton.Click += (s, e) => PopulateList(new List<NotificationEventType>(NotificationDefaults.DefaultJoinOrder));
            this.Controls.Add(_restoreDefaultsButton);

            _okButton = new Button
            {
                Text = "OK",
                AccessibleName = "OK",
                Location = new Point(198, 262),
                Size = new Size(80, 26),
                TabIndex = 4,
                DialogResult = DialogResult.None,
            };
            _okButton.Click += OkButton_Click;
            this.Controls.Add(_okButton);

            _cancelButton = new Button
            {
                Text = "Cancel",
                AccessibleName = "Cancel",
                Location = new Point(288, 262),
                Size = new Size(80, 26),
                TabIndex = 5,
                DialogResult = DialogResult.Cancel,
            };
            this.Controls.Add(_cancelButton);

            this.AcceptButton = _okButton;
            this.CancelButton = _cancelButton;
        }

        private void PopulateList(List<NotificationEventType> currentOrder)
        {
            var valid = new HashSet<NotificationEventType>(NotificationCenter.WatchEventTypes)
            {
                NotificationEventType.AwardsNeeded,
                NotificationEventType.RoutineStatusLine,
            };

            var ordered = new List<NotificationEventType>();
            if (currentOrder != null)
                foreach (var t in currentOrder)
                    if (valid.Contains(t) && !ordered.Contains(t)) ordered.Add(t);
            foreach (var t in NotificationDefaults.DefaultJoinOrder)
                if (!ordered.Contains(t)) ordered.Add(t);

            _listBox.Items.Clear();
            foreach (var t in ordered) _listBox.Items.Add(new TypeItem(t));

            if (_listBox.Items.Count > 0) _listBox.SelectedIndex = 0;
            UpdateMoveButtons();
        }

        private void UpdateMoveButtons()
        {
            int index = _listBox.SelectedIndex;
            int count = _listBox.Items.Count;
            if (index < 0 || count == 0)
            {
                _moveUpButton.Enabled = _moveDownButton.Enabled = false;
                return;
            }
            _moveUpButton.Enabled = index > 0;
            _moveDownButton.Enabled = index < count - 1;
        }

        private void MoveSelectedItem(int direction)
        {
            int index = _listBox.SelectedIndex;
            if (index < 0) return;
            int target = index + direction;
            if (target < 0 || target >= _listBox.Items.Count) return;

            object item = _listBox.Items[index];
            _listBox.Items[index] = _listBox.Items[target];
            _listBox.Items[target] = item;
            _listBox.SelectedIndex = target;
            UpdateMoveButtons();

            BeginInvoke((Action)(() =>
            {
                if (direction < 0 && _moveUpButton.Enabled) _moveUpButton.Focus();
                else if (direction > 0 && _moveDownButton.Enabled) _moveDownButton.Focus();
                else _listBox.Focus();
            }));
        }

        private void OkButton_Click(object sender, EventArgs e)
        {
            var order = new List<NotificationEventType>();
            foreach (TypeItem item in _listBox.Items) order.Add(item.Type);
            SelectedOrder = order;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }
}
