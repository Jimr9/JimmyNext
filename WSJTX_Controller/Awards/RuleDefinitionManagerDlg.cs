using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // View/create/edit/duplicate/rename/delete/enable/disable Rule Definitions.
    // Operates directly on RuleLibrary.Definitions/RuleLoader.RulesFolder -- the
    // same data every other award-aware view (Awards tab, Still Need tab, live
    // tagging) already reads, so a Reload() here is exactly what those views
    // need too (the caller is expected to refresh them after this dialog closes
    // if RulesChanged is true).
    public class RuleDefinitionManagerDlg : Form
    {
        private TextBox   _searchTb;
        private ListView  _listView;
        private TextBox   _detailTb;
        private Label     _statusLbl;
        private Button    _viewErrorsBtn;
        private Button    _editBtn, _duplicateBtn, _renameBtn, _deleteBtn, _enableBtn, _disableBtn, _exportBtn;

        private List<RuleDefinition> _allDefs = new List<RuleDefinition>();
        private List<RuleDefinition> _shownDefs = new List<RuleDefinition>();

        // Set true whenever an action here could have changed what's on disk or
        // in RuleLibrary -- the caller should refresh Awards/Still Need/live
        // tagging views after ShowDialog() returns if this is true.
        public bool RulesChanged { get; private set; }

        public RuleDefinitionManagerDlg()
        {
            Text            = "Rule Definition Manager";
            StartPosition   = FormStartPosition.CenterParent;
            MinimumSize     = new Size(720, 544);
            Size            = new Size(860, 624);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox     = true;
            MinimizeBox     = false;
            ShowInTaskbar   = false;
            KeyPreview      = true;

            BuildUi();

            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            Load    += (s, e) => LoadFromLibrary();
        }

        private void BuildUi()
        {
            var font = Font;
            int tab = 1;

            var searchLbl = new Label
            {
                Text = "&Search:",
                Location = new Point(10, 12),
                AutoSize = true,
            };
            Controls.Add(searchLbl);

            _searchTb = new TextBox
            {
                Location       = new Point(70, 9),
                Size           = new Size(260, 20),
                Anchor         = AnchorStyles.Top | AnchorStyles.Left,
                TabIndex       = tab++,
                AccessibleName = "Search Rule Definitions",
            };
            _searchTb.TextChanged += (s, e) => ApplyFilter();
            Controls.Add(_searchTb);

            _listView = new ListView
            {
                View            = View.Details,
                FullRowSelect   = true,
                MultiSelect     = false,
                HideSelection   = false,
                Location        = new Point(10, 38),
                Size            = new Size(500, 330),
                Anchor          = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                TabIndex        = tab++,
                AccessibleName  = "Rule Definitions",
            };
            _listView.Columns.Add("Name", 190);
            _listView.Columns.Add("Sponsor", 90);
            _listView.Columns.Add("Category", 110);
            _listView.Columns.Add("Enabled", 60);
            _listView.Columns.Add("Status", 90);
            _listView.SelectedIndexChanged += (s, e) => { UpdateDetail(); UpdateButtonStates(); };
            _listView.DoubleClick += (s, e) => EditSelected();
            Controls.Add(_listView);

            _detailTb = new TextBox
            {
                Multiline       = true,
                ReadOnly        = true,
                ScrollBars      = ScrollBars.Vertical,
                Location        = new Point(10, 374),
                Size            = new Size(500, 90),
                Anchor          = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                TabIndex        = tab++,
                AccessibleName  = "Selected Rule Definition details",
            };
            Controls.Add(_detailTb);

            _statusLbl = new Label
            {
                Location       = new Point(10, 470),
                Size           = new Size(400, 18),
                Anchor         = AnchorStyles.Bottom | AnchorStyles.Left,
                AccessibleName = "Rule Definitions load status",
            };
            Controls.Add(_statusLbl);

            _viewErrorsBtn = new Button
            {
                Text           = "View Load Errors...",
                Location       = new Point(10, 490),
                Size           = new Size(140, 24),
                Anchor         = AnchorStyles.Bottom | AnchorStyles.Left,
                TabIndex       = tab++,
                AccessibleName = "View Rule Definition load errors",
                Visible        = false,
            };
            _viewErrorsBtn.Click += (s, e) => ShowLoadErrors();
            Controls.Add(_viewErrorsBtn);

            // ── Button column ──────────────────────────────────────────────
            int bx = 522, by = 38, bw = 200, bh = 26, gap = 6;

            Button MakeBtn(string text, string accName, EventHandler onClick)
            {
                var b = new Button
                {
                    Text           = text,
                    Location       = new Point(bx, by),
                    Size           = new Size(bw, bh),
                    Anchor         = AnchorStyles.Top | AnchorStyles.Right,
                    TabIndex       = tab++,
                    AccessibleName = accName,
                };
                b.Click += onClick;
                Controls.Add(b);
                by += bh + gap;
                return b;
            }

            MakeBtn("&New Rule...", "Create a new Rule Definition", (s, e) => NewRule());
            _editBtn      = MakeBtn("&Edit...", "Edit the selected Rule Definition", (s, e) => EditSelected());
            _duplicateBtn = MakeBtn("&Duplicate...", "Duplicate the selected Rule Definition", (s, e) => DuplicateSelected());
            _renameBtn    = MakeBtn("Rena&me...", "Rename the selected Rule Definition", (s, e) => RenameSelected());
            _deleteBtn    = MakeBtn("De&lete...", "Delete the selected Rule Definition", (s, e) => DeleteSelected());
            MakeBtn("&Import...", "Import a Rule Definition from a file", (s, e) => ImportRule());
            _exportBtn    = MakeBtn("E&xport...", "Export the selected Rule Definition", (s, e) => ExportRule());
            by += gap;
            _enableBtn    = MakeBtn("En&able", "Enable the selected Rule Definition", (s, e) => SetEnabled(true));
            _disableBtn   = MakeBtn("&Disable", "Disable the selected Rule Definition", (s, e) => SetEnabled(false));
            by += gap;
            MakeBtn("&Reload from Disk", "Reload Rule Definitions from disk", (s, e) => { LoadFromLibrary(); RulesChanged = true; });
            MakeBtn("Open Rule Definitions Folder", "Open the Rule Definitions folder", (s, e) => OpenFolder(RuleLoader.RulesFolder));
            MakeBtn("Open Lists Folder", "Open the companion Lists folder", (s, e) => OpenFolder(RuleLoader.ListsFolder));
            by += gap;
            MakeBtn("Manage Companion &Lists...", "Manage companion list files", (s, e) => OpenCompanionListEditor());
            by += gap;
            MakeBtn("Restore &Defaults...", "Restore built-in awards to their original shipped definitions", (s, e) => RestoreDefaults());

            var closeBtn = new Button
            {
                Text           = "Close",
                Location       = new Point(bx, 554),
                Size           = new Size(bw, bh),
                Anchor         = AnchorStyles.Bottom | AnchorStyles.Right,
                TabIndex       = tab++,
                DialogResult   = DialogResult.Cancel,
                AccessibleName = "Close",
            };
            Controls.Add(closeBtn);
            CancelButton = closeBtn;
        }

        private void LoadFromLibrary()
        {
            try { RuleLibrary.Load(); } catch { }
            _allDefs = RuleLibrary.Definitions
                .OrderBy(d => d.Category ?? "").ThenBy(d => d.Name).ToList();
            ApplyFilter();
            UpdateStatus();
        }

        private void ApplyFilter()
        {
            string q = (_searchTb.Text ?? "").Trim();
            _shownDefs = q.Length == 0
                ? _allDefs
                : _allDefs.Where(d =>
                    (d.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (d.Id ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (d.Sponsor ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (d.Category ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                  ).ToList();

            string prevId = SelectedDef?.Id;
            _listView.BeginUpdate();
            _listView.Items.Clear();
            foreach (var d in _shownDefs)
            {
                var item = new ListViewItem(d.Name) { Tag = d };
                item.SubItems.Add(d.Sponsor ?? "");
                item.SubItems.Add(d.Category ?? "");
                item.SubItems.Add(d.Enabled ? "Yes" : "No");
                item.SubItems.Add(File.Exists(d.SourceFile) ? "OK" : "Missing file");
                _listView.Items.Add(item);
            }
            _listView.EndUpdate();

            if (prevId != null)
            {
                var again = _listView.Items.Cast<ListViewItem>()
                    .FirstOrDefault(i => ((RuleDefinition)i.Tag).Id == prevId);
                if (again != null) again.Selected = true;
            }
            UpdateDetail();
            UpdateButtonStates();
        }

        private RuleDefinition SelectedDef =>
            _listView.SelectedItems.Count > 0 ? (RuleDefinition)_listView.SelectedItems[0].Tag : null;

        private void UpdateDetail()
        {
            var d = SelectedDef;
            _detailTb.Text = d == null ? "" :
                $"Id: {d.Id}\r\n" +
                $"Description: {d.Description}\r\n" +
                $"GroupBy: {d.GroupBy}   Target: {d.Target}   Confirmation: {d.Confirmation}\r\n" +
                $"Source file: {d.SourceFile}";
        }

        private void UpdateButtonStates()
        {
            bool has = SelectedDef != null;
            _editBtn.Enabled = has;
            _duplicateBtn.Enabled = has;
            _renameBtn.Enabled = has;
            _deleteBtn.Enabled = has;
            _enableBtn.Enabled = has && !SelectedDef.Enabled;
            _disableBtn.Enabled = has && SelectedDef.Enabled;
            _exportBtn.Enabled = has;
        }

        private void UpdateStatus()
        {
            int errCount = RuleLibrary.LoadErrors.Count;
            _statusLbl.Text = $"{_allDefs.Count} Rule Definitions loaded" +
                (errCount > 0 ? $", {errCount} load error(s)" : "");
            _viewErrorsBtn.Visible = errCount > 0;
        }

        private void ShowLoadErrors()
        {
            MessageBox.Show(this,
                string.Join("\r\n\r\n", RuleLibrary.LoadErrors),
                "Rule Definition Load Errors",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not open folder: " + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenCompanionListEditor()
        {
            using (var dlg = new CompanionListEditorDlg())
            {
                dlg.ShowDialog(this);
                // Companion list *content* can change evaluation results even
                // though no Rule Definition file changed -- treat this the same
                // as RulesChanged so the caller refreshes Awards/Still Need/live
                // tagging just in case.
                RulesChanged = true;
            }
        }

        private void NewRule()
        {
            using (var dlg = new NewRuleWizardDlg())
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    RulesChanged = true;
                    LoadFromLibrary();
                    SelectById(dlg.SavedId);
                }
            }
        }

        private void EditSelected()
        {
            var d = SelectedDef;
            if (d == null) return;
            using (var dlg = new RuleDefinitionEditDlg(d))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    RulesChanged = true;
                    LoadFromLibrary();
                    SelectById(dlg.SavedId);
                }
            }
        }

        private void DuplicateSelected()
        {
            var d = SelectedDef;
            if (d == null) return;

            using (var prompt = new TextPromptDlg(
                "Duplicate Rule Definition", "New Rule Id (letters, digits, _ and - only):",
                d.Id + "_COPY", "New Rule Id"))
            {
                if (prompt.ShowDialog(this) != DialogResult.OK) return;
                string newId = prompt.Value.ToUpperInvariant();

                if (!System.Text.RegularExpressions.Regex.IsMatch(newId, @"^[A-Za-z0-9_-]+$"))
                {
                    MessageBox.Show(this, "Rule Id may only contain letters, digits, '_' and '-'.",
                        "Invalid Id", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (RuleLibrary.Definitions.Any(x => string.Equals(x.Id, newId, StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show(this, $"A Rule Definition with Id '{newId}' already exists.",
                        "Duplicate Id", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var copy = CloneDefinition(d);
                copy.Id = newId;
                copy.Name = d.Name + " (Copy)";
                string path = Path.Combine(RuleLoader.RulesFolder, newId + ".ini");
                try
                {
                    RuleWriter.Save(copy, path);
                    RulesChanged = true;
                    LoadFromLibrary();
                    SelectById(newId);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not save the duplicated Rule Definition: " + ex.Message,
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void RenameSelected()
        {
            var d = SelectedDef;
            if (d == null) return;

            // Renames the display Name only, never the Id -- the Id is a stable
            // identifier referenced elsewhere (e.g. the saved Still Need live-tag
            // selection), so changing it here would silently orphan that setting.
            using (var prompt = new TextPromptDlg("Rename Rule Definition", "Name:", d.Name, "Rule Definition name"))
            {
                if (prompt.ShowDialog(this) != DialogResult.OK) return;
                d.Name = prompt.Value;
                try
                {
                    RuleWriter.Save(d, d.SourceFile);
                    RulesChanged = true;
                    LoadFromLibrary();
                    SelectById(d.Id);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not save the rename: " + ex.Message,
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void DeleteSelected()
        {
            var d = SelectedDef;
            if (d == null) return;

            var confirm = MessageBox.Show(this,
                $"Delete '{d.Name}' ({d.Id})? This cannot be undone from here.",
                "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            try
            {
                if (File.Exists(d.SourceFile)) File.Delete(d.SourceFile);
                RulesChanged = true;
                LoadFromLibrary();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not delete: " + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Exports the selected Rule Definition as a plain .ini file, or -- if it
        // depends on a companion list file (Universe/LimitTo = "File:xxx.txt") --
        // as a .zip bundle containing both, so the recipient has everything the
        // award needs to actually work.
        private void ExportRule()
        {
            var d = SelectedDef;
            if (d == null) return;

            string companionFile = GetCompanionFileName(d);
            try
            {
                if (companionFile == null)
                {
                    using (var sfd = new SaveFileDialog
                    {
                        Filter   = "Rule Definition (*.ini)|*.ini",
                        FileName = d.Id + ".ini",
                        Title    = "Export Rule Definition",
                    })
                    {
                        if (sfd.ShowDialog(this) != DialogResult.OK) return;
                        File.Copy(d.SourceFile, sfd.FileName, overwrite: true);
                    }
                }
                else
                {
                    using (var sfd = new SaveFileDialog
                    {
                        Filter   = "Rule Definition Bundle (*.zip)|*.zip",
                        FileName = d.Id + ".zip",
                        Title    = "Export Rule Definition Bundle",
                    })
                    {
                        if (sfd.ShowDialog(this) != DialogResult.OK) return;

                        string companionPath = Path.Combine(RuleLoader.ListsFolder, companionFile);
                        using (var fs  = new FileStream(sfd.FileName, FileMode.Create))
                        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                        {
                            AddFileEntry(zip, d.Id + ".ini", d.SourceFile);
                            if (File.Exists(companionPath))
                                AddFileEntry(zip, companionFile, companionPath);
                        }
                    }
                }
                MessageBox.Show(this, "Export complete.", "Export Rule Definition",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not export: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Imports a Rule Definition from a plain .ini file or a .zip bundle
        // (one .ini plus any companion list files, all flat -- as produced by
        // ExportRule above). Handles an Id collision by offering to overwrite or
        // pick a new Id, reusing the same prompt/validation DuplicateSelected()
        // uses. Never silently leaves a broken file behind: if the imported
        // definition fails to load, the failure is shown immediately and the
        // file can be removed on the spot.
        private void ImportRule()
        {
            using (var ofd = new OpenFileDialog
            {
                Filter = "Rule Definition (*.ini;*.zip)|*.ini;*.zip|Rule Definition file (*.ini)|*.ini|Rule Definition bundle (*.zip)|*.zip",
                Title  = "Import Rule Definition",
            })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;

                string tempDir = null;
                try
                {
                    string iniPath;
                    var companionFiles = new List<string>();

                    if (string.Equals(Path.GetExtension(ofd.FileName), ".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        tempDir = Path.Combine(Path.GetTempPath(), "JimmyRuleImport_" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(tempDir);

                        string extractedIni = null;
                        using (var fs  = File.OpenRead(ofd.FileName))
                        using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                        {
                            foreach (var entry in zip.Entries)
                            {
                                // entry.Name is the bare filename (no path component,
                                // unlike FullName) -- this is what keeps a malformed
                                // zip entry from writing outside tempDir.
                                if (string.IsNullOrEmpty(entry.Name)) continue;
                                string destPath = Path.Combine(tempDir, entry.Name);
                                using (var es = entry.Open())
                                using (var ds = File.Create(destPath))
                                    es.CopyTo(ds);

                                if (string.Equals(Path.GetExtension(entry.Name), ".ini", StringComparison.OrdinalIgnoreCase))
                                    extractedIni = destPath;
                                else
                                    companionFiles.Add(destPath);
                            }
                        }
                        if (extractedIni == null)
                        {
                            MessageBox.Show(this, "The selected zip file does not contain a Rule Definition (.ini) file.",
                                "Import Rule Definition", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        iniPath = extractedIni;
                    }
                    else
                    {
                        iniPath = ofd.FileName;
                    }

                    RuleFile incoming;
                    try { incoming = RuleFile.Load(iniPath); }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "Could not read the file: " + ex.Message, "Import Rule Definition",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    string incomingId = incoming.Get("Award", "Id");
                    if (string.IsNullOrWhiteSpace(incomingId))
                    {
                        MessageBox.Show(this, "The selected file does not look like a valid Rule Definition (no [Award] Id found).",
                            "Import Rule Definition", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    string targetId = incomingId;
                    bool exists = RuleLibrary.Definitions.Any(x => string.Equals(x.Id, targetId, StringComparison.OrdinalIgnoreCase));
                    if (exists)
                    {
                        var choice = MessageBox.Show(this,
                            $"A Rule Definition with Id '{targetId}' already exists. Overwrite it?\n\n" +
                            "Choose No to import under a different Id instead, or Cancel to stop.",
                            "Rule Definition Already Exists", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                        if (choice == DialogResult.Cancel) return;
                        if (choice == DialogResult.No)
                        {
                            targetId = PromptForNewId(targetId + "_IMPORT");
                            if (targetId == null) return;
                        }
                    }

                    // Rewrite the [Award] Id line if a new one was chosen, then copy
                    // into place -- same File.Copy/File.WriteAllText primitive every
                    // other button in this dialog already uses.
                    string iniText = File.ReadAllText(iniPath);
                    if (!string.Equals(targetId, incomingId, StringComparison.Ordinal))
                        iniText = System.Text.RegularExpressions.Regex.Replace(
                            iniText, @"(?im)^\s*Id\s*=.*$", "Id=" + targetId);

                    string destIniPath = Path.Combine(RuleLoader.RulesFolder, targetId + ".ini");

                    // Overwriting an existing award: keep its prior content in memory so a
                    // bad import can be rolled back instead of leaving neither version behind.
                    string previousIniText = File.Exists(destIniPath) ? File.ReadAllText(destIniPath) : null;

                    File.WriteAllText(destIniPath, iniText);

                    Directory.CreateDirectory(RuleLoader.ListsFolder);
                    foreach (var companionPath in companionFiles)
                        File.Copy(companionPath, Path.Combine(RuleLoader.ListsFolder, Path.GetFileName(companionPath)), overwrite: true);

                    RulesChanged = true;
                    LoadFromLibrary();

                    var newError = RuleLibrary.LoadErrors.FirstOrDefault(
                        e => e.StartsWith(targetId + ".ini", StringComparison.OrdinalIgnoreCase));
                    if (newError != null)
                    {
                        string prompt = "The imported Rule Definition could not be loaded:\n\n" + newError +
                            (previousIniText != null
                                ? "\n\nRestore the award that was overwritten by this import?"
                                : "\n\nRemove the file that was just imported?");
                        var cleanup = MessageBox.Show(this, prompt,
                            "Import Problem", MessageBoxButtons.YesNo, MessageBoxIcon.Error);
                        if (cleanup == DialogResult.Yes)
                        {
                            try
                            {
                                if (previousIniText != null) File.WriteAllText(destIniPath, previousIniText);
                                else File.Delete(destIniPath);
                            }
                            catch { }
                            LoadFromLibrary();
                        }
                    }
                    else
                    {
                        SelectById(targetId);
                        MessageBox.Show(this, $"Imported '{targetId}'.", "Import Rule Definition",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not import: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    if (tempDir != null) { try { Directory.Delete(tempDir, true); } catch { } }
                }
            }
        }

        // Same "ask for a new Id, validate the character set, check for a
        // collision" loop DuplicateSelected() uses -- shared here so Import's
        // "pick a different Id" path behaves identically. Returns null if the
        // user cancels.
        private string PromptForNewId(string initialValue)
        {
            while (true)
            {
                using (var prompt = new TextPromptDlg(
                    "Import Rule Definition", "New Rule Id (letters, digits, _ and - only):",
                    initialValue, "New Rule Id"))
                {
                    if (prompt.ShowDialog(this) != DialogResult.OK) return null;
                    string newId = prompt.Value.ToUpperInvariant();

                    if (!System.Text.RegularExpressions.Regex.IsMatch(newId, @"^[A-Za-z0-9_-]+$"))
                    {
                        MessageBox.Show(this, "Rule Id may only contain letters, digits, '_' and '-'.",
                            "Invalid Id", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        continue;
                    }
                    if (RuleLibrary.Definitions.Any(x => string.Equals(x.Id, newId, StringComparison.OrdinalIgnoreCase)))
                    {
                        MessageBox.Show(this, $"A Rule Definition with Id '{newId}' already exists.",
                            "Duplicate Id", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        continue;
                    }
                    return newId;
                }
            }
        }

        private static void AddFileEntry(ZipArchive zip, string entryName, string sourcePath)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using (var entryStream = entry.Open())
            using (var sourceStream = File.OpenRead(sourcePath))
                sourceStream.CopyTo(entryStream);
        }

        // Returns the companion list filename (e.g. "myroster.txt") referenced by
        // this definition's Universe or LimitTo (the "File:xxx.txt" syntax
        // RuleUniverse.cs resolves), or null if it doesn't depend on one.
        private static string GetCompanionFileName(RuleDefinition d) =>
            FileRefName(d.Universe) ?? FileRefName(d.LimitTo);

        private static string FileRefName(string universeOrLimitTo)
        {
            const string prefix = "File:";
            if (string.IsNullOrWhiteSpace(universeOrLimitTo) ||
                !universeOrLimitTo.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return null;
            return universeOrLimitTo.Substring(prefix.Length).Trim();
        }

        // Resets every built-in award back to its shipped definition, and lets the user
        // choose whether to keep or discard anything in the live folder that isn't part
        // of the shipped set (custom/tester-made awards, or old ones left over from
        // earlier Jimmy versions). Deleted files go to the Recycle Bin -- Windows' own
        // "Empty Recycle Bin" is the actual point of no return, not this action.
        private void RestoreDefaults()
        {
            string shippedFolder = RuleLoader.ShippedRulesFolder;
            if (!Directory.Exists(shippedFolder))
            {
                MessageBox.Show(this, "The shipped Rule Definitions folder could not be found.",
                    "Restore Default Awards", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var shippedDefs = new List<RuleDefinition>();
            foreach (var path in Directory.GetFiles(shippedFolder, "*.ini"))
            {
                string error;
                var def = RuleLoader.ParseAndValidate(path, out error);
                if (def != null) shippedDefs.Add(def);
            }
            if (shippedDefs.Count == 0)
            {
                MessageBox.Show(this, "No valid shipped award definitions were found.",
                    "Restore Default Awards", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            var shippedIds = new HashSet<string>(shippedDefs.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);

            // Classify every physical file in the live folder independently of
            // RuleLibrary's dedup (which only keeps the first file per duplicate Id) --
            // a stray duplicate of a shipped Id must still be caught and reset, not
            // silently left behind.
            var liveDefaultFiles = new List<string>();
            var customDefs = new List<RuleDefinition>();
            if (Directory.Exists(RuleLoader.RulesFolder))
            {
                foreach (var path in Directory.GetFiles(RuleLoader.RulesFolder, "*.ini"))
                {
                    string error;
                    var def = RuleLoader.ParseAndValidate(path, out error);
                    if (def == null) continue;   // leave unparseable files alone entirely
                    if (shippedIds.Contains(def.Id)) liveDefaultFiles.Add(path);
                    else customDefs.Add(def);
                }
            }

            List<RuleDefinition> keptCustomDefs;
            if (customDefs.Count == 0)
            {
                var confirm = MessageBox.Show(this,
                    $"This resets {shippedDefs.Count} built-in award(s) to their original shipped definitions. " +
                    "No custom awards were found in your Rule Definitions folder. Continue?",
                    "Restore Default Awards", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (confirm != DialogResult.Yes) return;
                keptCustomDefs = new List<RuleDefinition>();
            }
            else
            {
                using (var dlg = new RestoreDefaultAwardsDlg(shippedDefs.Count, customDefs) { Owner = this })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    keptCustomDefs = dlg.KeptCustomDefs;
                }
            }

            var keptIds = new HashSet<string>(keptCustomDefs.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
            var removedCustomDefs = customDefs.Where(d => !keptIds.Contains(d.Id)).ToList();
            int recycledLists = 0;

            try
            {
                foreach (var path in liveDefaultFiles) RecycleFile(path);
                foreach (var d in removedCustomDefs) RecycleFile(d.SourceFile);

                Directory.CreateDirectory(RuleLoader.RulesFolder);
                foreach (var path in Directory.GetFiles(shippedFolder, "*.ini"))
                    File.Copy(path, Path.Combine(RuleLoader.RulesFolder, Path.GetFileName(path)), overwrite: true);

                if (Directory.Exists(RuleLoader.ShippedListsFolder))
                {
                    Directory.CreateDirectory(RuleLoader.ListsFolder);
                    foreach (var path in Directory.GetFiles(RuleLoader.ShippedListsFolder))
                        File.Copy(path, Path.Combine(RuleLoader.ListsFolder, Path.GetFileName(path)), overwrite: true);
                }

                // Orphan cleanup: any live list file that isn't part of the shipped Lists
                // set and isn't referenced by anything actually surviving (shipped + kept
                // custom awards) is leftover junk from a removed/renamed custom award --
                // clean it up too, same as the award files themselves.
                RuleLibrary.Load();
                var referencedListFiles = new HashSet<string>(
                    RuleLibrary.Definitions.Select(GetCompanionFileName).Where(f => f != null),
                    StringComparer.OrdinalIgnoreCase);
                var shippedListFileNames = Directory.Exists(RuleLoader.ShippedListsFolder)
                    ? new HashSet<string>(Directory.GetFiles(RuleLoader.ShippedListsFolder).Select(Path.GetFileName),
                        StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (Directory.Exists(RuleLoader.ListsFolder))
                {
                    foreach (var path in Directory.GetFiles(RuleLoader.ListsFolder))
                    {
                        string name = Path.GetFileName(path);
                        if (shippedListFileNames.Contains(name)) continue;
                        if (referencedListFiles.Contains(name)) continue;
                        RecycleFile(path);
                        recycledLists++;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Restore Defaults did not complete: " + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            RulesChanged = true;
            LoadFromLibrary();

            MessageBox.Show(this,
                $"Restored {shippedDefs.Count} default award(s).\n" +
                $"Removed {removedCustomDefs.Count} custom award(s) and {recycledLists} orphaned list file(s) (sent to Recycle Bin).\n" +
                $"Kept {keptCustomDefs.Count} custom award(s).",
                "Restore Default Awards", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void RecycleFile(string path)
        {
            if (!File.Exists(path)) return;
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }

        private void SetEnabled(bool enabled)
        {
            var d = SelectedDef;
            if (d == null || d.Enabled == enabled) return;
            d.Enabled = enabled;
            try
            {
                RuleWriter.Save(d, d.SourceFile);
                RulesChanged = true;
                LoadFromLibrary();
                SelectById(d.Id);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save: " + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SelectById(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            var item = _listView.Items.Cast<ListViewItem>()
                .FirstOrDefault(i => ((RuleDefinition)i.Tag).Id == id);
            if (item != null)
            {
                item.Selected = true;
                item.EnsureVisible();
            }
        }

        internal static RuleDefinition CloneDefinition(RuleDefinition d) => new RuleDefinition
        {
            Id = d.Id,
            Name = d.Name,
            Sponsor = d.Sponsor,
            Category = d.Category,
            FormatVersion = d.FormatVersion,
            Enabled = d.Enabled,
            Description = d.Description,
            Website = d.Website,
            GroupBy = d.GroupBy,
            Universe = d.Universe,
            LimitTo = d.LimitTo,
            Bands = new List<string>(d.Bands),
            Modes = new List<string>(d.Modes),
            CallsignPattern = d.CallsignPattern,
            Sig = d.Sig,
            DateFrom = d.DateFrom,
            DateTo = d.DateTo,
            Confirmation = d.Confirmation,
            Target = d.Target,
            Threshold = d.Threshold,
            Levels = d.Levels.Select(l => new RuleLevel { Name = l.Name, Threshold = l.Threshold }).ToList(),
            Endorsements = d.Endorsements == null ? null : new RuleEndorsements
            {
                Bands = new List<string>(d.Endorsements.Bands),
                Modes = new List<string>(d.Endorsements.Modes),
            },
        };
    }
}
