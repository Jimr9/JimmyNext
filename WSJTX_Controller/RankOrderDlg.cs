using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    public partial class RankOrderDlg : Form
    {

        // ── Public results ─────────────────────────────────────────────────────
        public List<WsjtxClient.RankMethods>              SelectedOrder              { get; private set; }
        public WsjtxClient.RankMethods?                   SelectedBeam               { get; private set; }
        public Dictionary<WsjtxClient.CallCategory, int>  SelectedCategoryWeights    { get; private set; }
        public List<WsjtxClient.CallCategory>              SelectedCallingPriorities  { get; private set; }

        // ── Static data ────────────────────────────────────────────────────────
        private static readonly SortEntry[] DefaultSortEntries = new SortEntry[]
        {
            new SortEntry(WsjtxClient.RankMethods.CALL_ORDER,  "Order received, oldest first"),
            new SortEntry(WsjtxClient.RankMethods.MOST_RECENT, "Most recent first"),
            new SortEntry(WsjtxClient.RankMethods.DIST_INCR,   "Nearest callers first"),
            new SortEntry(WsjtxClient.RankMethods.DIST_DECR,   "Farthest callers first"),
            new SortEntry(WsjtxClient.RankMethods.SNR_INCR,    "Weakest signal first"),
            new SortEntry(WsjtxClient.RankMethods.SNR_DECR,    "Strongest signal first"),
        };

        private static readonly BeamEntry[] BeamEntries = new BeamEntry[]
        {
            new BeamEntry(null,                               "None"),
            new BeamEntry(WsjtxClient.RankMethods.AZ_NQUAD,  "N"),
            new BeamEntry(WsjtxClient.RankMethods.AZ_NEQUAD, "NE"),
            new BeamEntry(WsjtxClient.RankMethods.AZ_EQUAD,  "E"),
            new BeamEntry(WsjtxClient.RankMethods.AZ_SEQUAD, "SE"),
            new BeamEntry(WsjtxClient.RankMethods.AZ_SQUAD,  "S"),
            new BeamEntry(WsjtxClient.RankMethods.AZ_SWQUAD, "SW"),
            new BeamEntry(WsjtxClient.RankMethods.AZ_WQUAD,  "W"),
            new BeamEntry(WsjtxClient.RankMethods.AZ_NWQUAD, "NW"),
        };

        private static readonly Dictionary<WsjtxClient.CallCategory, string> CategoryLabels =
            new Dictionary<WsjtxClient.CallCategory, string>
        {
            { WsjtxClient.CallCategory.NEW_COUNTRY,         "New DXCC" },
            { WsjtxClient.CallCategory.NEW_COUNTRY_ON_BAND, "New DXCC on band" },
            { WsjtxClient.CallCategory.ALWAYS_WANTED,       "Always Wanted Calls" },
            { WsjtxClient.CallCategory.TO_MYCALL,           "Calling me" },
            { WsjtxClient.CallCategory.MANUAL_SEL,          "Manual selection" },
            { WsjtxClient.CallCategory.WANTED_CQ,           "Directed CQ" },
            { WsjtxClient.CallCategory.DEFAULT,             "Ordinary CQ" },
            { WsjtxClient.CallCategory.WAS_NEEDED,          "WAS Needed" },
            { WsjtxClient.CallCategory.WAS_UNCONFIRMED,     "WAS Worked, Unconfirmed" },
            { WsjtxClient.CallCategory.DXCC_UNCONFIRMED,    "DXCC Worked, Unconfirmed" },
            { WsjtxClient.CallCategory.ZONE_NEEDED,         "Zones Needed" },
            { WsjtxClient.CallCategory.STILL_NEEDED,        "Still Need (selected award)" },
        };

        // POTA, SOTA, and MANUAL_SEL are hidden from user-facing lists.
        // POTA/SOTA are managed via the directed CQ targets field.
        // MANUAL_SEL is an internal WSJT-X integration category (user click in WSJT-X decode panel).
        private static readonly HashSet<WsjtxClient.CallCategory> HiddenCategories =
            new HashSet<WsjtxClient.CallCategory>
        {
            WsjtxClient.CallCategory.POTA,
            WsjtxClient.CallCategory.SOTA,
            WsjtxClient.CallCategory.MANUAL_SEL,
        };

        private static readonly List<WsjtxClient.CallCategory> DefaultCallingPriorities =
            new List<WsjtxClient.CallCategory>
        {
            WsjtxClient.CallCategory.NEW_COUNTRY,
            WsjtxClient.CallCategory.NEW_COUNTRY_ON_BAND,
            WsjtxClient.CallCategory.ALWAYS_WANTED,
            WsjtxClient.CallCategory.TO_MYCALL,
            WsjtxClient.CallCategory.WANTED_CQ,
            WsjtxClient.CallCategory.WAS_NEEDED,
            WsjtxClient.CallCategory.WAS_UNCONFIRMED,
            WsjtxClient.CallCategory.DXCC_UNCONFIRMED,
            WsjtxClient.CallCategory.ZONE_NEEDED,
            WsjtxClient.CallCategory.STILL_NEEDED,
        };

        // ── Constructor ────────────────────────────────────────────────────────
        public RankOrderDlg(
            List<WsjtxClient.RankMethods> currentOrder,
            WsjtxClient.RankMethods? currentBeam,
            List<WsjtxClient.CallCategory> currentCallingPriorities)
        {
            InitializeComponent();
            PopulateCallingList(currentCallingPriorities);
            PopulateSortList(currentOrder);
            PopulateBeamCombo(currentBeam);
        }

        // ── InitializeComponent ────────────────────────────────────────────────

        // ── Tab 1: Priorities & Filters logic ───────────────────────────────────

        // All categories that can appear in the list, in fallback order.
        private static readonly WsjtxClient.CallCategory[] AllFilterCategories =
        {
            WsjtxClient.CallCategory.NEW_COUNTRY,
            WsjtxClient.CallCategory.NEW_COUNTRY_ON_BAND,
            WsjtxClient.CallCategory.ALWAYS_WANTED,
            WsjtxClient.CallCategory.TO_MYCALL,
            WsjtxClient.CallCategory.WANTED_CQ,
            WsjtxClient.CallCategory.WAS_NEEDED,
            WsjtxClient.CallCategory.WAS_UNCONFIRMED,
            WsjtxClient.CallCategory.DXCC_UNCONFIRMED,
            WsjtxClient.CallCategory.ZONE_NEEDED,
            WsjtxClient.CallCategory.STILL_NEEDED,
            WsjtxClient.CallCategory.DEFAULT,
        };

        private void PopulateCallingList(List<WsjtxClient.CallCategory> currentCallingPriorities)
        {
            var calling = (currentCallingPriorities != null && currentCallingPriorities.Count > 0)
                ? currentCallingPriorities
                : DefaultCallingPriorities;

            callingCheckedListBox.Items.Clear();

            // 1. Checked (enabled) items in their saved order — this order drives Alt+N.
            foreach (var cat in calling)
            {
                if (HiddenCategories.Contains(cat)) continue;
                string label = CategoryLabels.ContainsKey(cat) ? CategoryLabels[cat] : cat.ToString();
                callingCheckedListBox.Items.Add(new CategoryEntry(cat, label), true);
            }

            // 2. Unchecked items: categories not in the enabled list, in fallback order.
            foreach (var cat in AllFilterCategories)
            {
                if (HiddenCategories.Contains(cat)) continue;
                if (calling.Contains(cat)) continue;
                string label = CategoryLabels.ContainsKey(cat) ? CategoryLabels[cat] : cat.ToString();
                callingCheckedListBox.Items.Add(new CategoryEntry(cat, label), false);
            }

            if (callingCheckedListBox.Items.Count > 0)
                callingCheckedListBox.SelectedIndex = 0;

            UpdateCallingMoveButtons();
        }

        private void CallingMoveUpButton_Click(object sender, EventArgs e)
        {
            MoveCallingItem(-1);
            BeginInvoke((Action)(() =>
            {
                if (callingMoveUpButton.Enabled) callingMoveUpButton.Focus();
                else callingCheckedListBox.Focus();
            }));
        }

        private void CallingMoveDownButton_Click(object sender, EventArgs e)
        {
            MoveCallingItem(1);
            BeginInvoke((Action)(() =>
            {
                if (callingMoveDownButton.Enabled) callingMoveDownButton.Focus();
                else callingCheckedListBox.Focus();
            }));
        }

        private void MoveCallingItem(int direction)
        {
            int index = callingCheckedListBox.SelectedIndex;
            if (index < 0) return;

            int target = index + direction;
            if (target < 0 || target >= callingCheckedListBox.Items.Count) return;

            bool   currentChk  = callingCheckedListBox.GetItemChecked(index);
            bool   targetChk   = callingCheckedListBox.GetItemChecked(target);
            object currentItem = callingCheckedListBox.Items[index];
            object targetItem  = callingCheckedListBox.Items[target];

            callingCheckedListBox.Items[target] = currentItem;
            callingCheckedListBox.Items[index]  = targetItem;
            callingCheckedListBox.SetItemChecked(target, currentChk);
            callingCheckedListBox.SetItemChecked(index,  targetChk);
            callingCheckedListBox.SelectedIndex = target;
            UpdateCallingMoveButtons();
        }

        private void CallingCheckedListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateCallingMoveButtons();
        }

        private void UpdateCallingMoveButtons()
        {
            int index = callingCheckedListBox.SelectedIndex;
            int count = callingCheckedListBox.Items.Count;
            if (index < 0 || count == 0)
            {
                callingMoveUpButton.Enabled = callingMoveDownButton.Enabled = false;
                return;
            }
            callingMoveUpButton.Enabled   = index > 0;
            callingMoveDownButton.Enabled = index < count - 1;
        }

        private void RestoreCallingDefaultsButton_Click(object sender, EventArgs e)
        {
            PopulateCallingList(DefaultCallingPriorities);
        }

        // ── Tab 3: Normal Sort Order logic ─────────────────────────────────────

        private void PopulateSortList(List<WsjtxClient.RankMethods> currentOrder)
        {
            var activeSet      = new HashSet<WsjtxClient.RankMethods>(
                currentOrder ?? Enumerable.Empty<WsjtxClient.RankMethods>());
            var orderedEntries = new List<SortEntry>();

            if (currentOrder != null)
            {
                foreach (var m in currentOrder)
                {
                    var entry = Array.Find(DefaultSortEntries, e => e.Method == m);
                    if (entry != null) orderedEntries.Add(entry);
                }
            }
            foreach (var entry in DefaultSortEntries)
            {
                if (!orderedEntries.Exists(e => e.Method == entry.Method))
                    orderedEntries.Add(entry);
            }

            sortCheckedListBox.Items.Clear();
            foreach (var entry in orderedEntries)
                sortCheckedListBox.Items.Add(entry, activeSet.Contains(entry.Method));

            if (sortCheckedListBox.Items.Count > 0)
                sortCheckedListBox.SelectedIndex = 0;

            UpdateSortMoveButtons();
        }

        private void PopulateBeamCombo(WsjtxClient.RankMethods? currentBeam)
        {
            beamComboBox.Items.Clear();
            int selectedIdx = 0;
            for (int i = 0; i < BeamEntries.Length; i++)
            {
                beamComboBox.Items.Add(BeamEntries[i]);
                if (BeamEntries[i].Method == currentBeam)
                    selectedIdx = i;
            }
            beamComboBox.SelectedIndex = selectedIdx;
        }

        private void SortMoveUpButton_Click(object sender, EventArgs e)
        {
            MoveSortItem(-1);
            BeginInvoke((Action)(() =>
            {
                if (sortMoveUpButton.Enabled) sortMoveUpButton.Focus();
                else sortCheckedListBox.Focus();
            }));
        }

        private void SortMoveDownButton_Click(object sender, EventArgs e)
        {
            MoveSortItem(1);
            BeginInvoke((Action)(() =>
            {
                if (sortMoveDownButton.Enabled) sortMoveDownButton.Focus();
                else sortCheckedListBox.Focus();
            }));
        }

        private void MoveSortItem(int direction)
        {
            int index = sortCheckedListBox.SelectedIndex;
            if (index < 0) return;

            int target = index + direction;
            if (target < 0 || target >= sortCheckedListBox.Items.Count) return;

            bool   currentChk  = sortCheckedListBox.GetItemChecked(index);
            bool   targetChk   = sortCheckedListBox.GetItemChecked(target);
            object currentItem = sortCheckedListBox.Items[index];
            object targetItem  = sortCheckedListBox.Items[target];

            sortCheckedListBox.Items[target] = currentItem;
            sortCheckedListBox.Items[index]  = targetItem;
            sortCheckedListBox.SetItemChecked(target, currentChk);
            sortCheckedListBox.SetItemChecked(index,  targetChk);
            sortCheckedListBox.SelectedIndex = target;
            UpdateSortMoveButtons();
        }

        private void SortCheckedListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateSortMoveButtons();
        }

        private void UpdateSortMoveButtons()
        {
            int index = sortCheckedListBox.SelectedIndex;
            sortMoveUpButton.Enabled   = (index > 0);
            sortMoveDownButton.Enabled = (index >= 0 && index < sortCheckedListBox.Items.Count - 1);
        }

        private void RestoreSortDefaultsButton_Click(object sender, EventArgs e)
        {
            PopulateSortList(new List<WsjtxClient.RankMethods> { WsjtxClient.RankMethods.MOST_RECENT });
            beamComboBox.SelectedIndex = 0; // None
        }

        // ── OK ─────────────────────────────────────────────────────────────────

        private void OkButton_Click(object sender, EventArgs e)
        {
            // Validate: at least one sort method must be checked
            var selected = new List<WsjtxClient.RankMethods>();
            for (int i = 0; i < sortCheckedListBox.Items.Count; i++)
            {
                if (sortCheckedListBox.GetItemChecked(i))
                    selected.Add(((SortEntry)sortCheckedListBox.Items[i]).Method);
            }

            if (selected.Count == 0)
            {
                tabControl.SelectedTab = sortTabPage;
                MessageBox.Show(this,
                    "At least one sort option must be checked.",
                    "Stations Available Sort Order",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                sortCheckedListBox.Focus();
                return;
            }

            SelectedOrder = selected;
            SelectedBeam  = ((BeamEntry)beamComboBox.SelectedItem)?.Method;

            // Single list drives both outputs, so they can never disagree the way the old
            // separate List Priorities / Call Filters tabs could: checked items are elevated
            // above normal calls (order = priority tier, same as the old List Priorities tab),
            // AND checked items in this same order become the calling-priority/Alt+N list
            // (same as the old Call Filters tab). Unchecked items and DEFAULT get weight 0.
            int checkedCount = 0;
            for (int i = 0; i < callingCheckedListBox.Items.Count; i++)
                if (callingCheckedListBox.GetItemChecked(i))
                    checkedCount++;

            var weights     = new Dictionary<WsjtxClient.CallCategory, int>();
            var callingList = new List<WsjtxClient.CallCategory>();
            int rank        = checkedCount;
            for (int i = 0; i < callingCheckedListBox.Items.Count; i++)
            {
                var entry = (CategoryEntry)callingCheckedListBox.Items[i];
                if (callingCheckedListBox.GetItemChecked(i))
                {
                    weights[entry.Category] = rank--;
                    callingList.Add(entry.Category);
                }
                else
                {
                    weights[entry.Category] = 0;
                }
            }
            weights[WsjtxClient.CallCategory.DEFAULT] = 0;
            SelectedCategoryWeights   = weights;
            SelectedCallingPriorities = callingList;

            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        // ── Inner record types ─────────────────────────────────────────────────

        private class SortEntry
        {
            public WsjtxClient.RankMethods Method { get; }
            private readonly string label;
            public SortEntry(WsjtxClient.RankMethods method, string label) { Method = method; this.label = label; }
            public override string ToString() => label;
        }

        private class BeamEntry
        {
            public WsjtxClient.RankMethods? Method { get; }
            private readonly string label;
            public BeamEntry(WsjtxClient.RankMethods? method, string label) { Method = method; this.label = label; }
            public override string ToString() => label;
        }

        private class CategoryEntry
        {
            public WsjtxClient.CallCategory Category { get; }
            private readonly string label;
            public CategoryEntry(WsjtxClient.CallCategory cat, string label) { Category = cat; this.label = label; }
            public override string ToString() => label;
        }
    }
}
