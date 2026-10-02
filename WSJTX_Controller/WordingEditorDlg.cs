using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // The wording editor (operator, 2026-10-02): every entry of Wording.txt in one list, by
    // section. Not public -- reached only when Jimmy Next was started with --wording (Options >
    // Notifications then shows a "Wording..." button); never in help, website or release notes.
    //   * Section box: which part of the file the list shows.
    //   * Entries: checked = your own words; Space on a checked entry returns it to the built-in
    //     words. Each reads "what it is: the words now".
    //   * Words: the words for the selected entry -- typing makes them your own.
    //   * Fields: the {fields} the entry can use; check/uncheck to use one, Move earlier/later to
    //     reorder them in the words.
    // Changes apply at once (new messages use them) and are saved to Wording.txt; a few words read
    // only at start (list titles) change at the next start.
    internal sealed class WordingEditorDlg : Form
    {
        private readonly ComboBox _sectionCb;
        private readonly CheckedListBox _entriesClb;
        private readonly TextBox _wordsTb;
        private readonly TextBox _defaultTb;
        private readonly CheckedListBox _fieldsClb;
        private readonly Button _earlierBtn, _laterBtn, _resetBtn, _closeBtn;
        private List<string> _keys = new List<string>();
        private bool _updating;
        private static readonly Regex FieldRx = new Regex(@"\{[A-Za-z]+\}");

        public WordingEditorDlg()
        {
            Text = "Wording";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            const int lx = 12, w = 560;
            int y = 12, tab = 0;

            Controls.Add(new Label { Text = "Section:", Location = new Point(lx, y + 3), AutoSize = true });
            _sectionCb = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(lx + 70, y), Size = new Size(w - 70, 21),
                AccessibleName = "Section", TabIndex = tab++,
            };
            _sectionCb.Items.AddRange(Wording.Sections.Select(s => (object)Title(s)).ToArray());
            _sectionCb.SelectedIndexChanged += (s, e) => FillEntries(0);
            Controls.Add(_sectionCb);
            y += 32;

            Controls.Add(new Label { Text = "Entries (checked = your own words):", Location = new Point(lx, y), AutoSize = true });
            y += 18;
            _entriesClb = new CheckedListBox
            {
                Location = new Point(lx, y), Size = new Size(w, 200), CheckOnClick = false, IntegralHeight = false,
                AccessibleName = "Wording entries", AccessibleDescription = "Checked entries use your own words", TabIndex = tab++,
            };
            _entriesClb.SelectedIndexChanged += (s, e) => ShowEntry();
            _entriesClb.ItemCheck += EntriesItemCheck;
            Controls.Add(_entriesClb);
            y += 208;

            Controls.Add(new Label { Text = "Words:", Location = new Point(lx, y), AutoSize = true });
            y += 18;
            _wordsTb = new TextBox { Location = new Point(lx, y), Size = new Size(w, 22), AccessibleName = "Words", TabIndex = tab++ };
            _wordsTb.TextChanged += (s, e) => { if (!_updating) WordsChanged(); };
            _wordsTb.Leave += (s, e) =>
            {
                int i = _entriesClb.SelectedIndex;
                if (i >= 0 && i < _keys.Count)
                {
                    _updating = true;
                    _entriesClb.Items[i] = ItemText(_keys[i]);
                    _entriesClb.SetItemChecked(i, Wording.IsChanged(_keys[i]));
                    _updating = false;
                }
                SaveFile();
            };
            Controls.Add(_wordsTb);
            y += 30;

            Controls.Add(new Label { Text = "Built-in words:", Location = new Point(lx, y), AutoSize = true });
            y += 18;
            _defaultTb = new TextBox
            {
                Location = new Point(lx, y), Size = new Size(w, 22), ReadOnly = true, TabStop = true,
                BorderStyle = BorderStyle.None, BackColor = SystemColors.Control, AccessibleName = "Built-in words", TabIndex = tab++,
            };
            Controls.Add(_defaultTb);
            y += 30;

            Controls.Add(new Label { Text = "Fields (checked = used in the words):", Location = new Point(lx, y), AutoSize = true });
            y += 18;
            _fieldsClb = new CheckedListBox
            {
                Location = new Point(lx, y), Size = new Size(260, 90), CheckOnClick = false, IntegralHeight = false,
                AccessibleName = "Fields", AccessibleDescription = "Checked fields are used in the words", TabIndex = tab++,
            };
            _fieldsClb.ItemCheck += FieldsItemCheck;
            Controls.Add(_fieldsClb);
            _earlierBtn = new Button { Text = "Move earlier", Location = new Point(lx + 272, y), Size = new Size(110, 26), TabIndex = tab++,
                AccessibleName = "Move the selected field earlier" };
            _earlierBtn.Click += (s, e) => MoveField(-1);
            _laterBtn = new Button { Text = "Move later", Location = new Point(lx + 272, y + 32), Size = new Size(110, 26), TabIndex = tab++,
                AccessibleName = "Move the selected field later" };
            _laterBtn.Click += (s, e) => MoveField(+1);
            Controls.Add(_earlierBtn);
            Controls.Add(_laterBtn);
            y += 100;

            _resetBtn = new Button { Text = "Reset to built-in", Location = new Point(lx, y), Size = new Size(140, 27), TabIndex = tab++,
                AccessibleName = "Reset this entry to the built-in words" };
            _resetBtn.Click += (s, e) => { if (CurrentKey != null) { _wordsTb.Text = Wording.DefaultOf(CurrentKey); SaveFile(); } };
            _closeBtn = new Button { Text = "Close", Location = new Point(lx + w - 90, y), Size = new Size(90, 27), TabIndex = tab++,
                DialogResult = DialogResult.OK };
            Controls.Add(_resetBtn);
            Controls.Add(_closeBtn);
            CancelButton = _closeBtn;
            ClientSize = new Size(w + 24, y + 40);
            FormClosing += (s, e) => SaveFile();

            _sectionCb.SelectedIndex = 0;
        }

        private static string Title(string section) =>
            System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(section.ToLowerInvariant());

        private string CurrentKey =>
            _entriesClb.SelectedIndex >= 0 && _entriesClb.SelectedIndex < _keys.Count ? _keys[_entriesClb.SelectedIndex] : null;

        private static string ItemText(string key) => $"{Wording.NoteOf(key)}: {Wording.Get(key)}";

        private void FillEntries(int select)
        {
            string section = Wording.Sections[Math.Max(0, _sectionCb.SelectedIndex)];
            _keys = Wording.InFileOrder().Where(k => Wording.SectionOf(k.Key) == section).Select(k => k.Key).ToList();
            _updating = true;
            _entriesClb.Items.Clear();
            foreach (var k in _keys) _entriesClb.Items.Add(ItemText(k), Wording.IsChanged(k));
            _updating = false;
            if (_keys.Count > 0) _entriesClb.SelectedIndex = Math.Min(select, _keys.Count - 1);
        }

        private void ShowEntry()
        {
            string key = CurrentKey;
            _updating = true;
            _wordsTb.Text = key == null ? "" : Wording.Get(key);
            _defaultTb.Text = key == null ? "" : Wording.DefaultOf(key);
            _updating = false;
            FillFields();
        }

        // The entry's fields: those its built-in words use, plus any in the words now.
        private void FillFields()
        {
            string key = CurrentKey;
            var all = key == null ? new List<string>()
                : FieldRx.Matches(Wording.DefaultOf(key) + " " + _wordsTb.Text).Cast<Match>().Select(m => m.Value).Distinct().ToList();
            int keep = _fieldsClb.SelectedIndex;
            _updating = true;
            _fieldsClb.Items.Clear();
            foreach (var f in all) _fieldsClb.Items.Add(f, _wordsTb.Text.Contains(f));
            _updating = false;
            if (_fieldsClb.Items.Count > 0) _fieldsClb.SelectedIndex = Math.Max(0, Math.Min(keep, _fieldsClb.Items.Count - 1));
            _fieldsClb.Enabled = _earlierBtn.Enabled = _laterBtn.Enabled = _fieldsClb.Items.Count > 0;
        }

        private void WordsChanged()
        {
            string key = CurrentKey;
            if (key == null) return;
            Wording.Set(key, _wordsTb.Text);
            // Only the check here -- rewriting the item's text while typing would move the caret;
            // the text is refreshed when the Words box is left.
            int i = _entriesClb.SelectedIndex;
            _updating = true;
            _entriesClb.SetItemChecked(i, Wording.IsChanged(key));
            _updating = false;
            FillFields();
        }

        // Space on an entry: unchecking returns it to the built-in words; checking alone changes
        // nothing (type in Words to make it your own).
        private void EntriesItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (_updating || e.Index < 0 || e.Index >= _keys.Count) return;
            string key = _keys[e.Index];
            if (e.NewValue == CheckState.Unchecked)
            {
                Wording.Set(key, null);
                BeginInvoke((Action)(() => { RefreshItem(e.Index); SaveFile(); }));
            }
            else if (!Wording.IsChanged(key))
                e.NewValue = CheckState.Unchecked;
        }

        private void RefreshItem(int i)
        {
            if (i < 0 || i >= _keys.Count) return;
            _updating = true;
            _entriesClb.Items[i] = ItemText(_keys[i]);
            _entriesClb.SetItemChecked(i, Wording.IsChanged(_keys[i]));
            _updating = false;
            if (i == _entriesClb.SelectedIndex) ShowEntry();
        }

        // Checking a field adds it to the end of the words; unchecking removes it.
        private void FieldsItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (_updating || e.Index < 0 || e.Index >= _fieldsClb.Items.Count) return;
            string f = (string)_fieldsClb.Items[e.Index];
            string text = _wordsTb.Text;
            string next = e.NewValue == CheckState.Checked
                ? (text.Contains(f) ? text : text + f)
                : Regex.Replace(text.Replace(f, ""), @"\s{2,}", " ").Trim();
            BeginInvoke((Action)(() => { _wordsTb.Text = next; SaveFile(); }));
        }

        // Swaps the selected field with the field before/after it in the words.
        private void MoveField(int step)
        {
            if (_fieldsClb.SelectedIndex < 0) return;
            string f = (string)_fieldsClb.Items[_fieldsClb.SelectedIndex];
            var ms = FieldRx.Matches(_wordsTb.Text).Cast<Match>().ToList();
            int at = ms.FindIndex(m => m.Value == f);
            int other = at + step;
            if (at < 0 || other < 0 || other >= ms.Count) return;
            var a = ms[Math.Min(at, other)]; var b = ms[Math.Max(at, other)];
            string t = _wordsTb.Text;
            string swapped = t.Substring(0, a.Index) + b.Value + t.Substring(a.Index + a.Length, b.Index - a.Index - a.Length)
                + a.Value + t.Substring(b.Index + b.Length);
            _wordsTb.Text = swapped;
            SaveFile();
        }

        private void SaveFile()
        {
            string err = Wording.Save();
            if (err != null) Text = "Wording (not saved: " + err + ")";
        }
    }
}
