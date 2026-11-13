using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // The Edit Log page's Clean Up menu (2026-10-31, Nexus 1.21): Nexus's own log tools
    // (LogCleanupDialogs.cs, NexusLogTools), and the part of bulk edit Nexus makes.
    public partial class LogbookWindow
    {
        private Button _editCleanupBtn;
        private ToolStripMenuItem _historyItem;

        // After the Edit Log footer's other buttons: Clean Up..., a menu. Enter, Space or the
        // Applications key opens it; the arrow keys move through it, as every menu.
        private void AddCleanupButton(Panel footer, Font font, int x, int tabIndex)
        {
            _editCleanupBtn = new Button
            {
                Text = "Clean Up...", AccessibleName = "Clean up the log", Font = font,
                Location = new Point(x, 4), Size = new Size(90, 23), TabIndex = tabIndex,
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Find &Duplicates...", null, (s, e) => ShowCleanup(new DuplicatesDlg())));
            menu.Items.Add(new ToolStripMenuItem("&Check the Log...", null, (s, e) => ShowCleanup(new CheckLogDlg())));
            _historyItem = new ToolStripMenuItem("&History of Selected Contact...", null, (s, e) => ShowHistory());
            menu.Items.Add(_historyItem);
            menu.Items.Add(new ToolStripMenuItem("Update from Call&book...", null, (s, e) => ShowCallbookUpdate()));
            menu.Items.Add(new ToolStripMenuItem("&Recently Deleted...", null, (s, e) => ShowCleanup(new DeletedDlg())));
            menu.Items.Add(new ToolStripMenuItem("&Undo a Change...", null, (s, e) => ShowCleanup(new UndoRunsDlg())));
            menu.Opening += (s, e) => _historyItem.Enabled = _editLv.SelectedItems.Count == 1;
            _editCleanupBtn.Click += (s, e) => menu.Show(_editCleanupBtn, new Point(0, _editCleanupBtn.Height));
            footer.Controls.Add(_editCleanupBtn);
        }

        // QSL (2026-11-09): the selected contacts to LoTW now, and Nexus's paper-card queue.
        internal Action<List<int>> UploadToLotw;
        private Button _editQslBtn;

        private void AddQslButton(Panel footer, Font font, int x, int tabIndex)
        {
            _editQslBtn = new Button
            {
                Text = "QSL...", AccessibleName = "QSL for selected contacts", Font = font,
                Location = new Point(x, 4), Size = new Size(70, 23), TabIndex = tabIndex,
            };
            var menu = new ContextMenuStrip();
            var upload = new ToolStripMenuItem("Upload Selected to &LoTW", null, (s, e) => UploadSelectedToLotw());
            var queue = new ToolStripMenuItem("&Queue Paper Card", null, (s, e) => PaperCard("queue", null));
            var unqueue = new ToolStripMenuItem("&Remove from Card Queue", null, (s, e) => PaperCard("unqueue", null));
            var bureau = new ToolStripMenuItem("Mark Card Sent by &Bureau", null, (s, e) => PaperCard("sent", "bureau"));
            var direct = new ToolStripMenuItem("Mark Card Sent &Direct", null, (s, e) => PaperCard("sent", "direct"));
            var list = new ToolStripMenuItem("Show &Cards to Send", null, (s, e) => ShowCardsToSend());
            menu.Items.AddRange(new ToolStripItem[] { upload, new ToolStripSeparator(), queue, unqueue, bureau, direct, new ToolStripSeparator(), list });
            menu.Opening += (s, e) =>
            {
                bool any = _editLv.SelectedItems.Count > 0;
                foreach (var item in new[] { upload, queue, unqueue, bureau, direct }) item.Enabled = any;
            };
            _editQslBtn.Click += (s, e) => menu.Show(_editQslBtn, new Point(0, _editQslBtn.Height));
            footer.Controls.Add(_editQslBtn);
        }

        private void UploadSelectedToLotw()
        {
            var ids = SelectedEditIds();
            if (ids.Count == 0) return;
            if (UploadToLotw == null) { SetStatusSpoken(_editQslBtn, "LoTW upload is not available here."); return; }
            UploadToLotw(ids);
        }

        private void PaperCard(string action, string via)
        {
            var ids = SelectedEditIds();
            if (ids.Count == 0) return;
            if (!(_db is NexusLogbookService nexus)) { SetStatusSpoken(_editQslBtn, "Paper cards need the logbook, which is not open."); return; }
            int n = nexus.SetPaperCard(ids, action, via);
            string what = action == "queue" ? "queued for a paper card" : action == "unqueue" ? "taken out of the card queue" : "marked card sent";
            SetStatusSpoken(_editQslBtn, n == ids.Count
                ? $"{(n == 1 ? "1 contact" : n + " contacts")} {what}."
                : $"{n} of {ids.Count} contacts {what}; the rest changed elsewhere, search again.");
            DoEditSearch();
        }

        // The cards-to-send list: the search's own "Paper card: to send" upload status.
        private void ShowCardsToSend()
        {
            int idx = Array.FindIndex(LogbookDb.UploadFilters, f => f.State == "cardqueued");
            if (idx < 0) return;
            _editUploadCb.SelectedIndex = idx + 1;   // 0 = Any
            DoEditSearch();
            _editLv.Focus();
        }

        private void ShowCleanup(Form dlg)
        {
            if (!NexusLogbook.Active) { SetStatusSpoken(_editCleanupBtn, "Clean Up needs the logbook, which is not open."); dlg.Dispose(); return; }
            using (dlg)
            {
                dlg.Owner = this;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    DoEditSearch();
                    _onImportComplete?.Invoke();   // the award and still-need caches read the log
                }
            }
        }

        // The selected contacts, or with none selected every contact the search found.
        private void ShowCallbookUpdate()
        {
            var rows = _editLv.SelectedItems.Count > 0 ? SelectedEditIds() : _editFoundIds.ToList();
            if (rows.Count == 0) { SetStatusSpoken(_editCleanupBtn, "Search for the contacts to update first, or select some."); return; }
            var ids = NexusLogTools.NexusIdsOf(rows).Distinct().ToList();
            if (ids.Count == 0) { SetStatusSpoken(_editCleanupBtn, "Those contacts are not in the logbook view; search again."); return; }
            ShowCleanup(new CallbookUpdateDlg(ids));
        }

        private void ShowHistory()
        {
            if (_editLv.SelectedItems.Count != 1) return;
            var item = _editLv.SelectedItems[0];
            string nexusId = NexusLogTools.NexusIdsOf(new[] { (int)item.Tag }).FirstOrDefault();
            if (nexusId == null) { SetStatusSpoken(_editCleanupBtn, "That contact is not in the logbook view; search again and try again."); return; }
            string contact = string.Join(" ", item.SubItems.Cast<ListViewItem.ListViewSubItem>().Take(4).Select(si => si.Text)).Trim();
            ShowCleanup(new HistoryDlg(nexusId, contact));
        }

        // ── Import, looked at first (Nexus 1.21) ─────────────────────────────────────────────
        // An ADIF, ADX or Cabrillo file is read by Nexus and shown before anything is written:
        // its records and dates, how many are new and how many the log holds already, and the
        // problems found. Import, Import new only (contacts already in the log are left exactly as
        // they are) or Cancel. The import is one change Undo a change takes back, and Nexus's
        // no-loss report is saved beside the backups. A file from LoTW, QRZ, eQSL or Club Log is
        // their confirmations, merged as before (RunImport).
        private async System.Threading.Tasks.Task RunPreviewedImport(string filePath)
        {
            string text;
            try { text = await System.Threading.Tasks.Task.Run(() => System.IO.File.ReadAllText(filePath)); }
            catch (Exception ex) { SetStatus("Import error: " + ex.Message); return; }
            SetBusy(true);
            try
            {
                SetStatus($"Reading {System.IO.Path.GetFileName(filePath)}…");
                using (var p = await System.Threading.Tasks.Task.Run(() => NexusLogTools.ImportPreview(filePath)))
                {
                    if (!p.Ok) { SetStatusSpoken(null, "The file could not be read: " + p.Why); return; }
                    var r = p.Root;
                    string format = NexusLogTools.Str(r, "format") ?? "adif";
                    bool adif = format == "adif";
                    string source = adif ? AdifImporter.DetectSource(text, AdifParser.Parse(text)) : "MANUAL";
                    if (source == "LOTW" || source == "QRZ" || source == "EQSL" || source == "CLUBLOG") { SetBusy(false); await RunImport(filePath); return; }
                    long records = NexusLogTools.Num(r, "records"), contacts = NexusLogTools.Num(r, "contacts");
                    long fresh = NexusLogTools.Num(r, "new"), dupes = NexusLogTools.Num(r, "duplicates"), updates = NexusLogTools.Num(r, "updates");
                    string range = NexusLogTools.Num(r, "firstUnix") > 0
                        ? $" from {NexusLogTools.When(NexusLogTools.Num(r, "firstUnix"))} to {NexusLogTools.When(NexusLogTools.Num(r, "lastUnix"))} UTC" : "";
                    var notes = NexusLogTools.List(r, "notes").Select(n => $"{NexusLogTools.Num(n, "count")} {Spaced(NexusLogTools.Str(n, "kind"))}").ToList();
                    string body = $"{records:N0} records, {contacts:N0} of them contacts{range}. " +
                                  $"{fresh:N0} are new; {dupes:N0} are in your log already" + (updates > 0 ? $", and {updates:N0} of those would gain details or confirmations" : "") + "." +
                                  (notes.Count > 0 ? " Problems found: " + string.Join("; ", notes) + "." : "") +
                                  " The import is one change: Undo a change, under Clean Up, takes it back.";
                    var import = new TaskDialogButton("Import");
                    var newOnly = new TaskDialogButton("Import new only");
                    var page = new TaskDialogPage
                    {
                        Caption = "Import",
                        Heading = $"Import {System.IO.Path.GetFileName(filePath)} ({format.ToUpperInvariant()})?",
                        Text = body,
                        Buttons = { import, newOnly, TaskDialogButton.Cancel },
                        DefaultButton = import,
                    };
                    var chosen = TaskDialog.ShowDialog(this, page);
                    if (chosen != import && chosen != newOnly) { SetStatus("Import cancelled; nothing changed."); return; }
                    bool onlyNew = chosen == newOnly;

                    int logId = _db.LogImportStart(source);
                    SetStatus("Importing…");
                    string reportPath = null;
                    using (var a = await System.Threading.Tasks.Task.Run(() =>
                    {
                        // Every file goes as Jimmy Next writes every import (its source kept on each
                        // contact): ADX and Cabrillo first made ADIF by Nexus's own reading of them.
                        string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"jimmy-import-{Guid.NewGuid():N}.adi");
                        try
                        {
                            string adifText = text;
                            if (!adif)
                            {
                                using (var c = NexusLogTools.ToAdif(filePath, tmp))
                                    if (!c.Ok) return new NexusLogTools.Answer(System.Text.Json.JsonSerializer.Serialize(new { state = "error", why = c.Why }));
                                adifText = System.IO.File.ReadAllText(tmp);
                            }
                            string tagged = NexusLogbookService.ToAdifText(AdifParser.ParseWithOrder(adifText), source);
                            System.IO.File.WriteAllText(tmp, tagged, new System.Text.UTF8Encoding(false));
                            return NexusLogTools.Import(tmp, onlyNew, $"Import of {System.IO.Path.GetFileName(filePath)}");
                        }
                        finally { try { System.IO.File.Delete(tmp); } catch { } }
                    }))
                    {
                        if (!a.Ok)
                        {
                            _db.LogImportFinish(logId, 0, 0, 0, 0, 0, a.Why);
                            SetStatusSpoken(null, "Nothing was imported: " + a.Why);
                            return;
                        }
                        long added = NexusLogTools.Num(a.Root, "added"), skipped = NexusLogTools.Num(a.Root, "skipped"), updated = NexusLogTools.Num(a.Root, "updated");
                        _db.LogImportFinish(logId, (int)(added + skipped), (int)added, 0, (int)updated, (int)skipped, null);
                        try { reportPath = SaveImportReport(filePath, a.Root); } catch { }
                        SetStatusSpoken(null, $"Imported: {added:N0} new, {skipped:N0} already in the log" + (updated > 0 ? $", {updated:N0} of them updated" : "") +
                            ". Undo a change, under Clean Up, takes it back." + (reportPath != null ? $" Report: {System.IO.Path.GetFileName(reportPath)} in the Import reports folder." : ""));
                    }
                    if (_activePage == _myLogPanel) PopulateMyLog();
                    else if (_activePage == _syncPanel) PopulateSync();
                    _onImportComplete?.Invoke();
                }
            }
            finally { SetBusy(false); }
        }

        // "noCall" -> "no call".
        private static string Spaced(string word) =>
            System.Text.RegularExpressions.Regex.Replace(word ?? "", "([a-z])([A-Z])", "$1 $2").ToLowerInvariant();

        // Nexus's no-loss report as plain text: every field of the file and what became of it.
        private static string SaveImportReport(string filePath, System.Text.Json.JsonElement answer)
        {
            if (!answer.TryGetProperty("report", out var rep)) return null;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Import of {filePath}, {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"Records {NexusLogTools.Num(rep, "records")}, contacts {NexusLogTools.Num(rep, "contacts")}: {NexusLogTools.Num(rep, "added")} added, " +
                          $"{NexusLogTools.Num(rep, "duplicates")} already in the log, {NexusLogTools.Num(rep, "updated")} updated" + (NexusLogTools.Flag(rep, "newOnly") ? " (new only)" : "") + ".");
            sb.AppendLine(NexusLogTools.Flag(rep, "accounted") ? "Every field of every record is accounted for." : "NOT every field is accounted for -- see below.");
            sb.AppendLine();
            foreach (var f in NexusLogTools.List(rep, "fields"))
            {
                sb.Append($"{NexusLogTools.Str(f, "field")}: {NexusLogTools.Num(f, "occurrences")} in the file; {NexusLogTools.Num(f, "stored")} stored, " +
                          $"{NexusLogTools.Num(f, "normalized")} written in the log's spelling, {NexusLogTools.Num(f, "passthrough")} kept as another program's field, " +
                          $"{NexusLogTools.Num(f, "dropped")} not stored");
                var reasons = NexusLogTools.List(f, "reasons").Select(x => x.ToString()).ToList();
                if (reasons.Count > 0) sb.Append(" (" + string.Join("; ", reasons) + ")");
                var examples = NexusLogTools.List(f, "examples").Select(x => x.GetString()).ToList();
                if (examples.Count > 0) sb.Append(". For example: " + string.Join("; ", examples));
                sb.AppendLine(".");
            }
            foreach (var u in NexusLogTools.List(rep, "unmapped"))
                sb.AppendLine($"Not stored: {NexusLogTools.Str(u, "what")} ({NexusLogTools.Num(u, "count")}), {u.GetProperty("reason")}.");
            string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(LookupManager.DataRoot), "Import reports");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, $"import-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            System.IO.File.WriteAllText(path, sb.ToString());
            return path;
        }

        // The part of a bulk edit Nexus's own bulk edit makes (BulkEditDlg.TakeNexusChanges):
        // previewed, then made on exactly the contacts the preview counted, as ONE change in the
        // history. Run off the window's thread. The status line's sentence.
        private static string NexusBulkEdit(List<int> rowIds, List<(string Field, string Value)> changes, string label)
        {
            var nexusIds = NexusLogTools.NexusIdsOf(rowIds).Distinct().ToList();
            if (nexusIds.Count == 0) return "Nothing changed: those contacts are not in the logbook view; search again.";
            using (var p = NexusLogTools.BulkPreview(nexusIds, changes))
            {
                if (!p.Ok) return $"{label}: nothing changed: {p.Why}";
                long change = NexusLogTools.Num(p.Root, "change");
                if (change == 0) return $"{label}: every contact already had that.";
                string files = string.Join(", ", NexusLogTools.List(p.Root, "activationFiles").Select(f => f.GetString()));
                using (var a = NexusLogTools.BulkEdit(nexusIds, changes, nexusIds.Count, (int)NexusLogTools.Num(p.Root, "moves"),
                    NexusLogTools.Str(p.Root, "moveTo"), $"Bulk edit: {label}"))
                {
                    string said = NexusLogTools.Said(a, $"{char.ToUpperInvariant(label[0])}{label.Substring(1)}: changed");
                    if (a.Ok) said += " Undo a change, under Clean Up, takes it back.";
                    if (a.Ok && files.Length > 0) said += $" These were in activation files you already exported: {files}. Export them again.";
                    return said;
                }
            }
        }
    }
}
