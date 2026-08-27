using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Three-tab row field editor: "Stations Available" (the main call queue / TX1 / TX2
    // rows), "Raw Decodes", and "Spot Watch". Each tab is independent -- its own field
    // universe, its own checked/ordered list, its own Restore Default -- sharing only
    // the dialog's OK/Cancel. Replaces the old single-purpose CallWaitingRowOrderDlg.
    public partial class RowDisplayOrderDlg : Form
    {
        // ── Stations Available (call queue / TX1 / TX2) row fields ──────────────
        public static readonly string[] CallWaitingDefaultFields =
            { "callp", "pri", "tag", "grid", "snr", "freq", "country", "distAz", "oe", "descr", "rankStr" };

        public static readonly Dictionary<string, string> CallWaitingFieldLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "callp", "Call Sign" }, { "pri", "Reply Status" }, { "tag", "Alert" }, { "grid", "Grid" },
            { "snr", "SNR" }, { "freq", "Audio Frequency" }, { "country", "Country" }, { "distAz", "Distance and Direction" },
            { "oe", "Age" }, { "descr", "Reason" }, { "rankStr", "Rank" }
        };

        // ── Raw Decodes row fields ────────────────────────────────────────────────
        // Callsign first by default -- lets first-letter type-ahead jump in the Raw
        // Decodes list land on a callsign instead of always hitting the TX1:/TX2: side
        // label every row otherwise starts with.
        public static readonly string[] RawDecodeDefaultFields =
            { "callsign", "side", "tag", "message", "snr", "freq", "grid", "country", "distAz" };

        public static readonly Dictionary<string, string> RawDecodeFieldLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "callsign", "Call Sign" }, { "side", "TX1/TX2" }, { "tag", "Alert" },
            { "message", "Raw Message" }, { "snr", "SNR" }, { "freq", "Audio Frequency" }, { "grid", "Grid" },
            { "country", "Country/State" }, { "distAz", "Distance and Direction" }
        };

        // ── Spot Watch row fields ─────────────────────────────────────────────────
        // Callsign first, matching the same first-letter-navigation rationale as
        // Raw Decodes.
        public static readonly string[] SpotWatchDefaultFields =
            { "callsign", "age", "band", "frequency", "mode", "evenOdd", "snr", "senderGrid", "country",
              "spottercall", "spottercountry", "spottergrid" };

        public static readonly Dictionary<string, string> SpotWatchFieldLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "callsign", "Call Sign" }, { "age", "Last Spotted" }, { "band", "Band" }, { "frequency", "Frequency" },
            { "mode", "Mode" }, { "evenOdd", "Even/Odd" }, { "snr", "SNR" }, { "senderGrid", "Grid" },
            { "country", "Country" }, { "spottercall", "Spotted By" },
            { "spottercountry", "Spotter Country/State" }, { "spottergrid", "Spotter Grid" }
        };

        public List<string> SelectedCallWaitingFields { get; private set; }
        public List<string> SelectedRawDecodeFields { get; private set; }
        public List<string> SelectedSpotWatchFields { get; private set; }

        private readonly bool _debug;

        public RowDisplayOrderDlg(List<string> currentCallWaitingOrder, List<string> currentRawDecodeOrder,
            List<string> currentSpotWatchOrder, bool debug)
        {
            _debug = debug;
            InitializeComponent();
            PopulateList(callWaitingListBox, CallWaitingDefaultFields, CallWaitingFieldLabels, currentCallWaitingOrder,
                callWaitingMoveUpButton, callWaitingMoveDownButton);
            PopulateList(rawDecodeListBox, RawDecodeDefaultFields, RawDecodeFieldLabels, currentRawDecodeOrder,
                rawDecodeMoveUpButton, rawDecodeMoveDownButton);
            PopulateList(spotWatchListBox, SpotWatchDefaultFields, SpotWatchFieldLabels, currentSpotWatchOrder,
                spotWatchMoveUpButton, spotWatchMoveDownButton);
        }

        private static void UpdateMoveButtons(CheckedListBox listBox, Button moveUpButton, Button moveDownButton)
        {
            int index = listBox.SelectedIndex;
            moveUpButton.Enabled = index > 0;
            moveDownButton.Enabled = index >= 0 && index < listBox.Items.Count - 1;
        }

        private void PopulateList(CheckedListBox listBox, string[] defaultFields,
            Dictionary<string, string> fieldLabels, List<string> currentOrder,
            Button moveUpButton, Button moveDownButton)
        {
            var selectedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var orderedFields = new List<string>();

            if (currentOrder != null)
            {
                foreach (var field in currentOrder)
                {
                    if (string.IsNullOrWhiteSpace(field)) continue;
                    if (!defaultFields.Contains(field, StringComparer.OrdinalIgnoreCase)) continue;
                    if (orderedFields.Exists(s => string.Equals(s, field, StringComparison.OrdinalIgnoreCase))) continue;
                    orderedFields.Add(field);
                    selectedSet.Add(field);
                }
            }

            foreach (var field in defaultFields)
            {
                if (!orderedFields.Exists(s => string.Equals(s, field, StringComparison.OrdinalIgnoreCase)))
                    orderedFields.Add(field);
            }

            listBox.Items.Clear();
            foreach (var field in orderedFields)
            {
                if (!_debug && (string.Equals(field, "descr", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(field, "oe", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(field, "rankStr", StringComparison.OrdinalIgnoreCase))) continue;
                listBox.Items.Add(new FieldItem(field, fieldLabels), selectedSet.Contains(field));
            }

            if (listBox.Items.Count > 0) listBox.SelectedIndex = 0;
            UpdateMoveButtons(listBox, moveUpButton, moveDownButton);
        }

        private static void MoveSelectedItem(CheckedListBox listBox, int direction)
        {
            int index = listBox.SelectedIndex;
            if (index < 0) return;

            int targetIndex = index + direction;
            if (targetIndex < 0 || targetIndex >= listBox.Items.Count) return;

            object currentItem = listBox.Items[index];
            bool currentChecked = listBox.GetItemChecked(index);
            object targetItem = listBox.Items[targetIndex];
            bool targetChecked = listBox.GetItemChecked(targetIndex);

            listBox.Items[targetIndex] = currentItem;
            listBox.Items[index] = targetItem;
            listBox.SetItemChecked(targetIndex, currentChecked);
            listBox.SetItemChecked(index, targetChecked);
            listBox.SelectedIndex = targetIndex;
        }

        private static List<string> CheckedFields(CheckedListBox listBox)
        {
            var result = new List<string>();
            for (int i = 0; i < listBox.Items.Count; i++)
                if (listBox.GetItemChecked(i))
                    result.Add(((FieldItem)listBox.Items[i]).Id);
            return result;
        }

        // ── Stations Available tab handlers ──────────────────────────────────────
        private void CallWaitingMoveUpButton_Click(object sender, EventArgs e)
        {
            MoveSelectedItem(callWaitingListBox, -1);
            BeginInvoke((Action)(() => (callWaitingMoveUpButton.Enabled ? (Control)callWaitingMoveUpButton : callWaitingListBox).Focus()));
        }

        private void CallWaitingMoveDownButton_Click(object sender, EventArgs e)
        {
            MoveSelectedItem(callWaitingListBox, 1);
            BeginInvoke((Action)(() => (callWaitingMoveDownButton.Enabled ? (Control)callWaitingMoveDownButton : callWaitingListBox).Focus()));
        }

        private void CallWaitingRestoreDefaultButton_Click(object sender, EventArgs e)
        {
            PopulateList(callWaitingListBox, CallWaitingDefaultFields, CallWaitingFieldLabels, new List<string>(CallWaitingDefaultFields),
                callWaitingMoveUpButton, callWaitingMoveDownButton);
        }

        // ── Raw Decodes tab handlers ──────────────────────────────────────────────
        private void RawDecodeMoveUpButton_Click(object sender, EventArgs e)
        {
            MoveSelectedItem(rawDecodeListBox, -1);
            BeginInvoke((Action)(() => (rawDecodeMoveUpButton.Enabled ? (Control)rawDecodeMoveUpButton : rawDecodeListBox).Focus()));
        }

        private void RawDecodeMoveDownButton_Click(object sender, EventArgs e)
        {
            MoveSelectedItem(rawDecodeListBox, 1);
            BeginInvoke((Action)(() => (rawDecodeMoveDownButton.Enabled ? (Control)rawDecodeMoveDownButton : rawDecodeListBox).Focus()));
        }

        private void RawDecodeRestoreDefaultButton_Click(object sender, EventArgs e)
        {
            PopulateList(rawDecodeListBox, RawDecodeDefaultFields, RawDecodeFieldLabels, new List<string>(RawDecodeDefaultFields),
                rawDecodeMoveUpButton, rawDecodeMoveDownButton);
        }

        // ── Spot Watch tab handlers ────────────────────────────────────────────────
        private void SpotWatchMoveUpButton_Click(object sender, EventArgs e)
        {
            MoveSelectedItem(spotWatchListBox, -1);
            BeginInvoke((Action)(() => (spotWatchMoveUpButton.Enabled ? (Control)spotWatchMoveUpButton : spotWatchListBox).Focus()));
        }

        private void SpotWatchMoveDownButton_Click(object sender, EventArgs e)
        {
            MoveSelectedItem(spotWatchListBox, 1);
            BeginInvoke((Action)(() => (spotWatchMoveDownButton.Enabled ? (Control)spotWatchMoveDownButton : spotWatchListBox).Focus()));
        }

        private void SpotWatchRestoreDefaultButton_Click(object sender, EventArgs e)
        {
            PopulateList(spotWatchListBox, SpotWatchDefaultFields, SpotWatchFieldLabels, new List<string>(SpotWatchDefaultFields),
                spotWatchMoveUpButton, spotWatchMoveDownButton);
        }

        private void OkButton_Click(object sender, EventArgs e)
        {
            var callWaitingFields = CheckedFields(callWaitingListBox);
            var rawDecodeFields = CheckedFields(rawDecodeListBox);
            var spotWatchFields = CheckedFields(spotWatchListBox);

            if (callWaitingFields.Count == 0 || rawDecodeFields.Count == 0 || spotWatchFields.Count == 0)
            {
                MessageBox.Show(this, "Select at least one field on each tab.", "Selection Required", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SelectedCallWaitingFields = callWaitingFields;
            SelectedRawDecodeFields = rawDecodeFields;
            SelectedSpotWatchFields = spotWatchFields;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private void CallWaitingListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateMoveButtons(callWaitingListBox, callWaitingMoveUpButton, callWaitingMoveDownButton);
        }

        private void RawDecodeListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateMoveButtons(rawDecodeListBox, rawDecodeMoveUpButton, rawDecodeMoveDownButton);
        }

        private void SpotWatchListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateMoveButtons(spotWatchListBox, spotWatchMoveUpButton, spotWatchMoveDownButton);
        }

        private class FieldItem
        {
            public string Id { get; }
            public string Label { get; }

            public FieldItem(string id, Dictionary<string, string> labels)
            {
                Id = id;
                Label = labels.TryGetValue(id, out var label) ? label : id;
            }

            public override string ToString() => Label;
        }
    }
}
