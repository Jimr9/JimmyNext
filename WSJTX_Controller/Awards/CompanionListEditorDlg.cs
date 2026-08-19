using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // View/create/edit/rename/duplicate/delete/import/export companion list
    // files (RuleLoader.ListsFolder\*.txt), and see which Rule Definitions
    // reference each one before renaming or deleting it.
    public class CompanionListEditorDlg : Form
    {
        private ListBox  _filesLb;
        private TextBox  _entriesTb;
        private TextBox  _findTb;
        private Label    _usedByLbl;
        private Label    _statusLbl;
        private Button   _saveBtn, _renameBtn, _duplicateBtn, _deleteBtn, _exportBtn;
        private string   _loadedFileName;   // file name backing _entriesTb's current content
        private bool     _dirty;

        public CompanionListEditorDlg()
        {
            Text            = "Companion List Editor";
            StartPosition   = FormStartPosition.CenterParent;
            MinimumSize     = new Size(720, 460);
            Size            = new Size(820, 540);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox     = true;
            MinimizeBox     = false;
            ShowInTaskbar   = false;
            KeyPreview      = true;

            BuildUi();
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            Load    += (s, e) => LoadFileList();
        }

        private void BuildUi()
        {
            var filesLbl = new Label { Text = "Companion Lists:", Location = new Point(10, 10), AutoSize = true };
            Controls.Add(filesLbl);

            _filesLb = new ListBox
            {
                Location = new Point(10, 28), Size = new Size(220, 340),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left,
                TabIndex = 1, AccessibleName = "Companion list files",
            };
            _filesLb.SelectedIndexChanged += (s, e) => LoadSelectedFile();
            Controls.Add(_filesLb);

            const int bh = 26, gap = 6, bx = 10, bw = 220;
            Button MakeFileBtn(string text, string accName, EventHandler onClick, int yOverride)
            {
                var b = new Button { Text = text, Location = new Point(bx, yOverride), Size = new Size(bw, bh),
                    Anchor = AnchorStyles.Top | AnchorStyles.Left, AccessibleName = accName };
                b.Click += onClick;
                Controls.Add(b);
                return b;
            }
            int fy = 374;
            MakeFileBtn("&New List...", "Create a new companion list", (s, e) => NewList(), fy); fy += bh + gap;
            _renameBtn    = MakeFileBtn("Rena&me...", "Rename the selected companion list", (s, e) => RenameSelected(), fy); fy += bh + gap;
            _duplicateBtn = MakeFileBtn("&Duplicate...", "Duplicate the selected companion list", (s, e) => DuplicateSelected(), fy); fy += bh + gap;
            _deleteBtn    = MakeFileBtn("De&lete...", "Delete the selected companion list", (s, e) => DeleteSelected(), fy);

            // ── Right side: entries editor ──────────────────────────────────
            var entriesLbl = new Label { Text = "Entries (one per line; ; or # starts a comment):",
                Location = new Point(244, 10), AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left };
            Controls.Add(entriesLbl);

            _entriesTb = new TextBox
            {
                Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = false,
                Location = new Point(244, 28), Size = new Size(540, 300),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                TabIndex = 2, AccessibleName = "Companion list entries",
                Font = new Font(FontFamily.GenericMonospace, 9f),
            };
            _entriesTb.TextChanged += (s, e) => { _dirty = true; UpdateSaveState(); };
            Controls.Add(_entriesTb);

            var findLbl = new Label { Text = "Find:", Location = new Point(244, 334), AutoSize = true,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            Controls.Add(findLbl);
            _findTb = new TextBox { Location = new Point(284, 331), Size = new Size(160, 20),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left, AccessibleName = "Find text in entries" };
            Controls.Add(_findTb);
            var findNextBtn = new Button { Text = "Find Next", Location = new Point(450, 330), Size = new Size(90, 24),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left, AccessibleName = "Find next match" };
            findNextBtn.Click += (s, e) => FindNext();
            Controls.Add(findNextBtn);
            var sortBtn = new Button { Text = "Sort Entries", Location = new Point(548, 330), Size = new Size(100, 24),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left, AccessibleName = "Sort entries alphabetically" };
            sortBtn.Click += (s, e) => SortEntries();
            Controls.Add(sortBtn);
            var dedupBtn = new Button { Text = "Remove Duplicates", Location = new Point(654, 330), Size = new Size(130, 24),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left, AccessibleName = "Remove duplicate entries" };
            dedupBtn.Click += (s, e) => RemoveDuplicates();
            Controls.Add(dedupBtn);

            _usedByLbl = new Label
            {
                Location = new Point(244, 360), Size = new Size(540, 40),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AccessibleName = "Rule Definitions using this companion list",
            };
            Controls.Add(_usedByLbl);

            _saveBtn = new Button { Text = "&Save Entries", Location = new Point(244, 404), Size = new Size(110, 26),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left, AccessibleName = "Save companion list entries", Enabled = false };
            _saveBtn.Click += (s, e) => SaveEntries();
            Controls.Add(_saveBtn);

            var importBtn = new Button { Text = "&Import...", Location = new Point(360, 404), Size = new Size(90, 26),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left, AccessibleName = "Import a companion list file" };
            importBtn.Click += (s, e) => ImportList();
            Controls.Add(importBtn);

            _exportBtn = new Button { Text = "&Export...", Location = new Point(456, 404), Size = new Size(90, 26),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left, AccessibleName = "Export the selected companion list" };
            _exportBtn.Click += (s, e) => ExportSelected();
            Controls.Add(_exportBtn);

            _statusLbl = new Label
            {
                Location = new Point(10, 440), Size = new Size(500, 18),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                AccessibleName = "Companion list status",
            };
            Controls.Add(_statusLbl);

            var closeBtn = new Button { Text = "Close", Location = new Point(694, 404), Size = new Size(90, 26),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right, DialogResult = DialogResult.Cancel,
                AccessibleName = "Close" };
            Controls.Add(closeBtn);
            CancelButton = closeBtn;
        }

        // ── File list ────────────────────────────────────────────────────────

        private void LoadFileList()
        {
            Directory.CreateDirectory(RuleLoader.ListsFolder);
            string keep = _loadedFileName;

            _filesLb.Items.Clear();
            var files = Directory.GetFiles(RuleLoader.ListsFolder, "*.txt")
                .Select(Path.GetFileName).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var f in files) _filesLb.Items.Add(f);

            _statusLbl.Text = $"{files.Count} companion list file(s) in {RuleLoader.ListsFolder}";

            if (keep != null && files.Contains(keep, StringComparer.OrdinalIgnoreCase))
                _filesLb.SelectedIndex = files.FindIndex(f => string.Equals(f, keep, StringComparison.OrdinalIgnoreCase));
            else
                ClearEditor();
        }

        private void ClearEditor()
        {
            _loadedFileName = null;
            _entriesTb.Text = "";
            _dirty = false;
            _usedByLbl.Text = "";
            UpdateSaveState();
            _renameBtn.Enabled = _duplicateBtn.Enabled = _deleteBtn.Enabled = _exportBtn.Enabled = false;
        }

        private void LoadSelectedFile()
        {
            if (_filesLb.SelectedItem == null) { ClearEditor(); return; }
            string fileName = (string)_filesLb.SelectedItem;
            string path = Path.Combine(RuleLoader.ListsFolder, fileName);

            if (_dirty && !string.IsNullOrEmpty(_loadedFileName))
            {
                var r = MessageBox.Show(this, $"Save changes to '{_loadedFileName}' before switching?",
                    "Unsaved Changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) return;
                if (r == DialogResult.Yes) SaveEntriesTo(_loadedFileName);
            }

            try { _entriesTb.Text = File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch (Exception ex) { MessageBox.Show(this, "Could not read file: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }

            _loadedFileName = fileName;
            _dirty = false;
            UpdateSaveState();
            UpdateUsedBy(fileName);
            _renameBtn.Enabled = _duplicateBtn.Enabled = _deleteBtn.Enabled = _exportBtn.Enabled = true;
        }

        private void UpdateSaveState() => _saveBtn.Enabled = _dirty && !string.IsNullOrEmpty(_loadedFileName);

        private List<string> FindUsage(string fileName)
        {
            string token = "File:" + fileName;
            return RuleLibrary.Definitions
                .Where(d => string.Equals(d.Universe, token, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(d.LimitTo, token, StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Name)
                .ToList();
        }

        private void UpdateUsedBy(string fileName)
        {
            var usage = FindUsage(fileName);
            _usedByLbl.Text = usage.Count == 0
                ? "Not referenced by any loaded Rule Definition."
                : "Used by: " + string.Join(", ", usage);
        }

        // ── Actions ──────────────────────────────────────────────────────────

        private void NewList()
        {
            using (var prompt = new TextPromptDlg("New Companion List", "File name (e.g. my_list.txt):", "", "New companion list file name"))
            {
                if (prompt.ShowDialog(this) != DialogResult.OK) return;
                string name = NormalizeFileName(prompt.Value);
                if (name == null) return;

                string path = Path.Combine(RuleLoader.ListsFolder, name);
                if (File.Exists(path))
                {
                    MessageBox.Show(this, $"'{name}' already exists.", "Duplicate Name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                try
                {
                    File.WriteAllText(path, "; One entry per line. ';' or '#' starts a comment.\r\n");
                    LoadFileList();
                    _filesLb.SelectedIndex = _filesLb.Items.IndexOf(name);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not create file: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void RenameSelected()
        {
            if (_loadedFileName == null) return;
            var usage = FindUsage(_loadedFileName);
            if (usage.Count > 0)
            {
                var r = MessageBox.Show(this,
                    $"'{_loadedFileName}' is referenced by: {string.Join(", ", usage)}.\r\n\r\n" +
                    "Renaming it will NOT update those Rule Definitions -- they will start failing to resolve their Universe/LimitTo until you " +
                    "edit them to use the new file name. Rename anyway?",
                    "Companion List In Use", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
            }

            using (var prompt = new TextPromptDlg("Rename Companion List", "New file name:", _loadedFileName, "New file name"))
            {
                if (prompt.ShowDialog(this) != DialogResult.OK) return;
                string newName = NormalizeFileName(prompt.Value);
                if (newName == null) return;

                string oldPath = Path.Combine(RuleLoader.ListsFolder, _loadedFileName);
                string newPath = Path.Combine(RuleLoader.ListsFolder, newName);
                if (File.Exists(newPath))
                {
                    MessageBox.Show(this, $"'{newName}' already exists.", "Duplicate Name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                try
                {
                    File.Move(oldPath, newPath);
                    LoadFileList();
                    _filesLb.SelectedIndex = _filesLb.Items.IndexOf(newName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not rename: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void DuplicateSelected()
        {
            if (_loadedFileName == null) return;
            using (var prompt = new TextPromptDlg("Duplicate Companion List", "New file name:",
                Path.GetFileNameWithoutExtension(_loadedFileName) + "_copy.txt", "New file name"))
            {
                if (prompt.ShowDialog(this) != DialogResult.OK) return;
                string newName = NormalizeFileName(prompt.Value);
                if (newName == null) return;

                string srcPath = Path.Combine(RuleLoader.ListsFolder, _loadedFileName);
                string newPath = Path.Combine(RuleLoader.ListsFolder, newName);
                if (File.Exists(newPath))
                {
                    MessageBox.Show(this, $"'{newName}' already exists.", "Duplicate Name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                try
                {
                    File.Copy(srcPath, newPath);
                    LoadFileList();
                    _filesLb.SelectedIndex = _filesLb.Items.IndexOf(newName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not duplicate: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void DeleteSelected()
        {
            if (_loadedFileName == null) return;
            var usage = FindUsage(_loadedFileName);
            string msg = usage.Count > 0
                ? $"'{_loadedFileName}' is referenced by: {string.Join(", ", usage)}.\r\n\r\n" +
                  "Deleting it will break those Rule Definitions until they're updated. Delete anyway?"
                : $"Delete '{_loadedFileName}'? This cannot be undone from here.";
            var confirm = MessageBox.Show(this, msg, "Confirm Delete", MessageBoxButtons.YesNo,
                usage.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            try
            {
                File.Delete(Path.Combine(RuleLoader.ListsFolder, _loadedFileName));
                LoadFileList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not delete: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ImportList()
        {
            using (var ofd = new OpenFileDialog { Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*", Title = "Import Companion List" })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                string name = NormalizeFileName(Path.GetFileName(ofd.FileName));
                if (name == null) return;

                string destPath = Path.Combine(RuleLoader.ListsFolder, name);
                if (File.Exists(destPath))
                {
                    var r = MessageBox.Show(this, $"'{name}' already exists. Overwrite?", "Confirm Overwrite",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (r != DialogResult.Yes) return;
                }
                try
                {
                    File.Copy(ofd.FileName, destPath, overwrite: true);
                    LoadFileList();
                    _filesLb.SelectedIndex = _filesLb.Items.IndexOf(name);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not import: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ExportSelected()
        {
            if (_loadedFileName == null) return;
            using (var sfd = new SaveFileDialog { Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                FileName = _loadedFileName, Title = "Export Companion List" })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.Copy(Path.Combine(RuleLoader.ListsFolder, _loadedFileName), sfd.FileName, overwrite: true);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not export: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void SaveEntries()
        {
            if (string.IsNullOrEmpty(_loadedFileName)) return;
            SaveEntriesTo(_loadedFileName);
        }

        private void SaveEntriesTo(string fileName)
        {
            try
            {
                File.WriteAllText(Path.Combine(RuleLoader.ListsFolder, fileName), _entriesTb.Text);
                _dirty = false;
                UpdateSaveState();
                _statusLbl.Text = $"Saved {fileName}.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SortEntries()
        {
            var lines = _entriesTb.Lines.OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToArray();
            _entriesTb.Lines = lines;
        }

        private void RemoveDuplicates()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var line in _entriesTb.Lines)
            {
                string trimmed = line.Trim();
                // Comments and blank lines are always kept, never deduped.
                if (trimmed.Length == 0 || trimmed.StartsWith(";") || trimmed.StartsWith("#") || seen.Add(trimmed))
                    result.Add(line);
            }
            _entriesTb.Lines = result.ToArray();
        }

        private void FindNext()
        {
            string needle = _findTb.Text;
            if (string.IsNullOrEmpty(needle)) return;
            int start = _entriesTb.SelectionStart + _entriesTb.SelectionLength;
            int idx = _entriesTb.Text.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) idx = _entriesTb.Text.IndexOf(needle, 0, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                _statusLbl.Text = $"'{needle}' not found.";
                return;
            }
            _entriesTb.Focus();
            _entriesTb.Select(idx, needle.Length);
            _entriesTb.ScrollToCaret();
        }

        // Rejects path separators/invalid chars and ensures a .txt extension.
        private string NormalizeFileName(string raw)
        {
            string name = (raw ?? "").Trim();
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageBox.Show(this, "File name contains invalid characters.", "Invalid Name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            if (!name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) name += ".txt";
            return name;
        }
    }
}
