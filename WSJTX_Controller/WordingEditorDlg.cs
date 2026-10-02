using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // The wording editor (operator, 2026-10-02): every entry of Wording.txt, by section. Not
    // public -- reached only when Jimmy Next was started with --wording (Options > Notifications
    // then shows a "Wording..." button); never in help, website or release notes.
    // Kept to plain lists that read cleanly (operator: "the list just reads the list"):
    //   Section box -> Entries (each line "short name: words", "(yours)" when changed, "silent")
    //   -> Words (the entry's description is its accessible description, said after a pause)
    //   -> Silent -> Fields (each line "Band, used" / "Mode, not used"; Space switches it)
    //   -> Move earlier / Move later -> Reset -> Close.
    // Changes apply at once and are saved to Wording.txt; list titles follow at once too.
    internal sealed class WordingEditorDlg : Form
    {
        private readonly ComboBox _sectionCb;
        private readonly ListBox _entriesLb;
        private readonly TextBox _wordsTb;
        private readonly CheckBox _silentCb;
        private readonly ListBox _fieldsLb;
        private readonly Button _earlierBtn, _laterBtn, _resetBtn, _closeBtn;
        private List<string> _keys = new List<string>();
        private List<string> _fields = new List<string>();   // "{Band}" ... in list order
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

            Controls.Add(new Label { Text = "Entries:", Location = new Point(lx, y), AutoSize = true });
            y += 18;
            _entriesLb = new ListBox
            {
                Location = new Point(lx, y), Size = new Size(w, 220), IntegralHeight = false,
                AccessibleName = "Entries", TabIndex = tab++,
            };
            _entriesLb.SelectedIndexChanged += (s, e) => { if (!_updating) ShowEntry(); };
            Controls.Add(_entriesLb);
            y += 228;

            Controls.Add(new Label { Text = "Words:", Location = new Point(lx, y), AutoSize = true });
            y += 18;
            _wordsTb = new TextBox { Location = new Point(lx, y), Size = new Size(w, 22), AccessibleName = "Words", TabIndex = tab++ };
            _wordsTb.TextChanged += (s, e) => { if (!_updating) WordsChanged(); };
            _wordsTb.Leave += (s, e) => { RefreshItem(_entriesLb.SelectedIndex); SaveFile(); };
            Controls.Add(_wordsTb);
            y += 30;

            // Silent: this entry is neither said nor shown (stored as key = "").
            _silentCb = new CheckBox
            {
                Text = "Silent (not said or shown)", Location = new Point(lx, y), AutoSize = true,
                AccessibleName = "Silent", TabIndex = tab++,
            };
            _silentCb.CheckedChanged += (s, e) =>
            {
                if (_updating || CurrentKey == null) return;
                Wording.SetSilent(CurrentKey, _silentCb.Checked);
                RefreshItem(_entriesLb.SelectedIndex);
                ShowEntry();
                SaveFile();
            };
            Controls.Add(_silentCb);
            y += 30;

            Controls.Add(new Label { Text = "Fields (Space: use or not):", Location = new Point(lx, y), AutoSize = true });
            y += 18;
            _fieldsLb = new ListBox
            {
                Location = new Point(lx, y), Size = new Size(260, 90), IntegralHeight = false,
                AccessibleName = "Fields", TabIndex = tab++,
            };
            _fieldsLb.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Space) return;
                e.SuppressKeyPress = true;
                ToggleField(_fieldsLb.SelectedIndex);
            };
            _fieldsLb.DoubleClick += (s, e) => ToggleField(_fieldsLb.SelectedIndex);
            Controls.Add(_fieldsLb);
            _earlierBtn = new Button { Text = "Move earlier", Location = new Point(lx + 272, y), Size = new Size(110, 26), TabIndex = tab++,
                AccessibleName = "Move the field earlier" };
            _earlierBtn.Click += (s, e) => MoveField(-1);
            _laterBtn = new Button { Text = "Move later", Location = new Point(lx + 272, y + 32), Size = new Size(110, 26), TabIndex = tab++,
                AccessibleName = "Move the field later" };
            _laterBtn.Click += (s, e) => MoveField(+1);
            Controls.Add(_earlierBtn);
            Controls.Add(_laterBtn);
            y += 100;

            _resetBtn = new Button { Text = "Reset to built-in", Location = new Point(lx, y), Size = new Size(140, 27), TabIndex = tab++,
                AccessibleName = "Reset to the built-in words" };
            _resetBtn.Click += (s, e) =>
            {
                if (CurrentKey == null) return;
                Wording.Set(CurrentKey, null);   // also ends Silent
                RefreshItem(_entriesLb.SelectedIndex);
                ShowEntry();
                SaveFile();
            };
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
            _entriesLb.SelectedIndex >= 0 && _entriesLb.SelectedIndex < _keys.Count ? _keys[_entriesLb.SelectedIndex] : null;

        // "Band changed: Band changed to {Band}", "Band selected (yours): ...", "Status box name: silent".
        private static string ItemText(string key)
        {
            string name = Wording.ShortName(key);
            if (Wording.IsSilent(key)) return name + ": silent";
            return name + (Wording.IsChanged(key) ? " (yours)" : "") + ": " + Wording.Get(key);
        }

        private void FillEntries(int select)
        {
            string section = Wording.Sections[Math.Max(0, _sectionCb.SelectedIndex)];
            _keys = Wording.InFileOrder().Where(k => Wording.SectionOf(k.Key) == section).Select(k => k.Key).ToList();
            _updating = true;
            _entriesLb.Items.Clear();
            foreach (var k in _keys) _entriesLb.Items.Add(ItemText(k));
            _updating = false;
            if (_keys.Count > 0) _entriesLb.SelectedIndex = Math.Min(select, _keys.Count - 1);
        }

        private void ShowEntry()
        {
            string key = CurrentKey;
            _updating = true;
            bool silent = key != null && Wording.IsSilent(key);
            _wordsTb.Text = key == null ? "" : silent ? Wording.DefaultOf(key) : Wording.Get(key);
            _wordsTb.Enabled = !silent;
            // What the entry is: said by the screen reader after a pause on the Words box.
            _wordsTb.AccessibleDescription = key == null ? "" : Wording.NoteOf(key);
            _silentCb.Checked = silent;
            _updating = false;
            FillFields();
        }

        // The entry's fields: those its built-in words use, plus any in the words now.
        private void FillFields()
        {
            string key = CurrentKey;
            _fields = key == null ? new List<string>()
                : FieldRx.Matches(Wording.DefaultOf(key) + " " + _wordsTb.Text).Cast<Match>().Select(m => m.Value).Distinct().ToList();
            int keep = _fieldsLb.SelectedIndex;
            _updating = true;
            _fieldsLb.Items.Clear();
            foreach (var f in _fields)
                _fieldsLb.Items.Add(f.Trim('{', '}') + (_wordsTb.Text.Contains(f) ? ", used" : ", not used"));
            _updating = false;
            if (_fieldsLb.Items.Count > 0) _fieldsLb.SelectedIndex = Math.Max(0, Math.Min(keep, _fieldsLb.Items.Count - 1));
            bool any = _fields.Count > 0 && _wordsTb.Enabled;
            _fieldsLb.Enabled = _earlierBtn.Enabled = _laterBtn.Enabled = any;
        }

        private void WordsChanged()
        {
            string key = CurrentKey;
            if (key == null) return;
            Wording.Set(key, _wordsTb.Text);
            FillFields();   // the entry line itself is refreshed when the Words box is left
        }

        private void RefreshItem(int i)
        {
            if (i < 0 || i >= _keys.Count) return;
            _updating = true;
            _entriesLb.Items[i] = ItemText(_keys[i]);
            _updating = false;
        }

        // Space / double-click on a field: use it (added at the end of the words) or stop using it.
        private void ToggleField(int i)
        {
            if (i < 0 || i >= _fields.Count || !_wordsTb.Enabled) return;
            string f = _fields[i];
            string text = _wordsTb.Text;
            _wordsTb.Text = text.Contains(f)
                ? Regex.Replace(text.Replace(f, ""), @"\s{2,}", " ").Trim()
                : text + f;
            RefreshItem(_entriesLb.SelectedIndex);
            SaveFile();
            if (i < _fieldsLb.Items.Count) _fieldsLb.SelectedIndex = i;
        }

        // Swaps the selected field with the field before/after it in the words.
        private void MoveField(int step)
        {
            int i = _fieldsLb.SelectedIndex;
            if (i < 0 || i >= _fields.Count) return;
            string f = _fields[i];
            var ms = FieldRx.Matches(_wordsTb.Text).Cast<Match>().ToList();
            int at = ms.FindIndex(m => m.Value == f);
            int other = at + step;
            if (at < 0 || other < 0 || other >= ms.Count) return;
            var a = ms[Math.Min(at, other)]; var b = ms[Math.Max(at, other)];
            string t = _wordsTb.Text;
            _wordsTb.Text = t.Substring(0, a.Index) + b.Value + t.Substring(a.Index + a.Length, b.Index - a.Index - a.Length)
                + a.Value + t.Substring(b.Index + b.Length);
            RefreshItem(_entriesLb.SelectedIndex);
            SaveFile();
        }

        private void SaveFile()
        {
            string err = Wording.Save();
            if (err != null) Text = "Wording (not saved: " + err + ")";
        }
    }
}
