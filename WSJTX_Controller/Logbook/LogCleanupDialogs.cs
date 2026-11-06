using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // The Logbook's Clean Up windows (2026-10-31, Nexus 1.21): Find duplicates, Check the log, a
    // contact's History, Recently deleted and Undo a change. Each shows what Nexus answered
    // (NexusLogTools) and passes the operator's choice back; every change is one run of Nexus's
    // history, which Undo a change takes back whole. Keyboard first: the list, then its buttons,
    // then Close; a result is put in Status and spoken, and focus is never moved by an answer.
    internal class CleanupDlgBase : Form
    {
        protected readonly TextBox Intro;
        protected readonly ListView List;
        protected readonly FlowLayoutPanel Buttons;
        protected readonly TextBox Details;
        protected readonly TextBox Status;
        protected bool Changed;   // anything changed: the Logbook refreshes its list on close

        protected CleanupDlgBase(string title, string intro, string listName, bool checkBoxes)
        {
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            Size = new Size(760, 560);
            MinimumSize = new Size(560, 400);
            KeyPreview = true;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);

            Intro = new TextBox
            {
                Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control,
                Dock = DockStyle.Fill, Height = 52, Text = intro, AccessibleName = "About " + title, TabIndex = 0,
            };
            List = new ListView
            {
                View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, GridLines = true,
                CheckBoxes = checkBoxes, Dock = DockStyle.Fill, AccessibleName = listName, TabIndex = 1,
            };
            List.SelectedIndexChanged += (s, e) => OnSelected();
            Buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, TabIndex = 2 };
            Details = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
                AccessibleName = "Details", TabIndex = 3,
            };
            Status = new TextBox { ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control, Dock = DockStyle.Fill, AccessibleName = "Status", TabIndex = 4 };
            layout.Controls.Add(Intro, 0, 0);
            layout.Controls.Add(List, 0, 1);
            layout.Controls.Add(Buttons, 0, 2);
            layout.Controls.Add(Details, 0, 3);
            layout.Controls.Add(Status, 0, 4);

            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
            CancelButton = close;
            Shown += (s, e) => List.Focus();
            FormClosed += (s, e) => { if (Changed) DialogResult = DialogResult.OK; };
            Load += (s, e) => Buttons.Controls.Add(close);   // last, after each window's own buttons
            AppLook.Attach(this);
        }

        protected Button AddButton(string text, string name, Action click)
        {
            var b = new Button { Text = text, AutoSize = true, AccessibleName = name };
            b.Click += (s, e) => click();
            Buttons.Controls.Add(b);
            return b;
        }

        protected void Columns(params (string Name, int Width)[] cols)
        {
            foreach (var (n, w) in cols) List.Columns.Add(n, w);
        }

        protected virtual void OnSelected() { }

        // Says it, in Status and to the screen reader, without moving focus.
        protected void Say(string text)
        {
            Status.Text = text ?? "";
            try
            {
                (ActiveControl ?? Status).AccessibilityObject.RaiseAutomationNotification(
                    System.Windows.Forms.Automation.AutomationNotificationKind.Other,
                    System.Windows.Forms.Automation.AutomationNotificationProcessing.ImportantMostRecent, Status.Text);
            }
            catch { }
        }

        // `work` off the window's thread (a scan or a change can take a while), `done` back on it.
        protected void Run(string busy, Func<NexusLogTools.Answer> work, Action<NexusLogTools.Answer> done)
        {
            UseWaitCursor = true;
            Buttons.Enabled = false;
            Status.Text = busy;
            Task.Run(work).ContinueWith(t =>
            {
                if (IsDisposed) { t.Result?.Dispose(); return; }
                BeginInvoke((Action)(() =>
                {
                    UseWaitCursor = false;
                    Buttons.Enabled = true;
                    using (var a = t.IsFaulted ? new NexusLogTools.Answer(JsonSerializer.Serialize(new { state = "error", why = t.Exception?.GetBaseException().Message })) : t.Result)
                        done(a);
                }));
            });
        }

        protected bool Confirm(string question) =>
            MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

        protected static string Source(string code)
        {
            switch (code)
            {
                case "operator": return "you";
                case "import": return "an import";
                case "fill": return "the automatic fill";
                case "lotw": return "a LoTW download";
                case "eqsl": return "an eQSL download";
                case "qrz": return "a QRZ download";
                case "pota": return "a POTA export";
                case "contest": return "the contest log";
                case "remote": return "a remote computer";
                case "file": return "another program";
                case "undo": return "an undo";
                default: return code ?? "";
            }
        }

        protected static string WhenMs(long ms) =>
            ms <= 0 ? "" : DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    }

    // ── Find duplicates ──────────────────────────────────────────────────────────────────────

    internal sealed class DuplicatesDlg : CleanupDlgBase
    {
        private readonly CheckBox _possible;
        private readonly Button _merge;

        private sealed class Group
        {
            public string Keep;
            public List<(string Id, string Revision, string Line)> Copies = new List<(string, string, string)>();
            public List<(string Tag, bool Guarded, string Chosen, List<(string Id, string Value)> Values)> Conflicts =
                new List<(string, bool, string, List<(string, string)>)>();
        }

        public DuplicatesDlg() : base("Find Duplicates",
            "Contacts logged more than once: the same call, band and mode within 5 minutes, or within an hour with " +
            "Include possible duplicates. Check the ones to merge. Merging keeps the fullest copy, gives it everything the " +
            "others held, and puts the others in Recently deleted. Nothing is sent to any service. Undo a change takes a merge back.",
            "Duplicates", true)
        {
            Columns(("Call", 100), ("Band", 60), ("Mode", 60), ("First logged, UTC", 140), ("Copies", 60), ("Choices needed", 110));
            _possible = new CheckBox { Text = "Include possible duplicates (within an hour)", AutoSize = true, AccessibleName = "Include possible duplicates" };
            AddButton("Find", "Find duplicates", Find);
            Buttons.Controls.Add(_possible);
            _merge = AddButton("Merge Checked...", "Merge the checked duplicates", Merge);
            Shown += (s, e) => Find();
        }

        private void Find()
        {
            bool possible = _possible.Checked;
            Run("Looking for duplicates...", () => NexusLogTools.FindDuplicates(possible), a =>
            {
                List.Items.Clear();
                Details.Text = "";
                if (!a.Ok) { Say("Could not look: " + a.Why); return; }
                foreach (var g in NexusLogTools.List(a.Root, "groups"))
                {
                    var group = new Group { Keep = NexusLogTools.Str(g, "keep") };
                    var copies = NexusLogTools.List(g, "copies").ToList();
                    foreach (var c in copies)
                    {
                        string line = $"{NexusLogTools.When(NexusLogTools.Num(c, "whenUnix"))} UTC" +
                            Part("grid", NexusLogTools.Str(c, "grid")) + Part("state", NexusLogTools.Str(c, "state")) +
                            Part("as", NexusLogTools.Str(c, "stationCallsign")) +
                            (NexusLogTools.Flag(c, "confirmed") ? ", confirmed" : "") + (NexusLogTools.Flag(c, "uploaded") ? ", uploaded" : "");
                        group.Copies.Add((NexusLogTools.Str(c, "id"), NexusLogTools.Str(c, "revision"), line));
                    }
                    foreach (var c in NexusLogTools.List(g, "conflicts"))
                        group.Conflicts.Add((NexusLogTools.Str(c, "tag"), NexusLogTools.Flag(c, "guarded"), NexusLogTools.Str(c, "chosen"),
                            NexusLogTools.List(c, "values").Select(v => { var p = v.EnumerateArray().ToList(); return (p[0].GetString(), p[1].GetString()); }).ToList()));
                    var first = copies.FirstOrDefault();
                    var item = new ListViewItem(new[]
                    {
                        NexusLogTools.Str(first, "call"), NexusLogTools.Str(first, "band"), NexusLogTools.Str(first, "mode"),
                        NexusLogTools.When(NexusLogTools.Num(first, "whenUnix")), group.Copies.Count.ToString(),
                        group.Conflicts.Count(c => c.Guarded).ToString(),
                    }) { Tag = group };
                    List.Items.Add(item);
                }
                long scanned = NexusLogTools.Num(a.Root, "scanned");
                Say(List.Items.Count == 0 ? $"No duplicates among {scanned:N0} contacts."
                    : $"{List.Items.Count} duplicate{(List.Items.Count == 1 ? "" : "s")} among {scanned:N0} contacts.");
                if (List.Items.Count > 0) { List.Items[0].Selected = true; List.Items[0].Focused = true; }
            });
        }

        private static string Part(string label, string v) => string.IsNullOrEmpty(v) ? "" : $", {label} {v}";

        protected override void OnSelected()
        {
            if (List.SelectedItems.Count != 1) { Details.Text = ""; return; }
            var g = (Group)List.SelectedItems[0].Tag;
            var lines = g.Copies.Select((c, i) => $"Copy {i + 1}{(c.Id == g.Keep ? ", kept" : "")}: {c.Line}").ToList();
            foreach (var c in g.Conflicts)
                lines.Add($"{c.Tag} differs: " + string.Join(" or ", c.Values.Select(v => v.Value)) +
                          (c.Guarded ? " (yours to choose)" : $" (keeps {c.Values.FirstOrDefault(v => v.Id == c.Chosen).Value})"));
            Details.Text = string.Join(Environment.NewLine, lines);
        }

        private void Merge()
        {
            var groups = List.CheckedItems.Cast<ListViewItem>().Select(i => (Group)i.Tag).ToList();
            if (groups.Count == 0) { Say("Check the duplicates to merge first."); return; }
            var asks = new List<NexusLogTools.MergeAsk>();
            foreach (var g in groups)
            {
                var ask = new NexusLogTools.MergeAsk { Keep = g.Keep, Copies = g.Copies.Select(c => (c.Id, c.Revision)).ToList() };
                foreach (var c in g.Conflicts.Where(c => c.Guarded))
                {
                    string chosen = ChooseValue(c.Tag, c.Values);
                    if (chosen == null) { Say("Merge cancelled; nothing changed."); return; }
                    ask.Choices[c.Tag] = chosen;
                }
                asks.Add(ask);
            }
            int others = groups.Sum(g => g.Copies.Count - 1);
            if (!Confirm($"Merge {groups.Count} duplicate{(groups.Count == 1 ? "" : "s")}? {others} extra cop{(others == 1 ? "y goes" : "ies go")} to Recently deleted. " +
                         "Nothing is sent to any service. Undo a change takes the whole merge back.")) { Say("Merge cancelled; nothing changed."); return; }
            Run("Merging...", () => NexusLogTools.Merge(asks), a =>
            {
                Say(NexusLogTools.Said(a, "Merged, changing"));
                if (a.Ok) { Changed = true; Find(); }
            });
        }

        // A guarded field's value (a confirmed copy's, a comment, notes, a contest exchange):
        // never defaulted, so the operator picks it. The copy's id, or null when cancelled.
        private string ChooseValue(string tag, List<(string Id, string Value)> values)
        {
            using (var f = new Form { Text = "Choose " + tag, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink })
            {
                var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(8) };
                flow.Controls.Add(new Label { Text = $"The copies hold different {tag}. Which should the merged contact keep?", AutoSize = true, MaximumSize = new Size(420, 0) });
                var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 400, AccessibleName = tag + " to keep" };
                foreach (var v in values) combo.Items.Add(v.Value);
                combo.SelectedIndex = 0;
                flow.Controls.Add(combo);
                var row = new FlowLayoutPanel { AutoSize = true };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
                row.Controls.Add(ok); row.Controls.Add(cancel);
                flow.Controls.Add(row);
                f.Controls.Add(flow);
                f.AcceptButton = ok; f.CancelButton = cancel;
                AppLook.Attach(f);
                return f.ShowDialog(this) == DialogResult.OK ? values[combo.SelectedIndex].Id : null;
            }
        }
    }

    // ── Check the log ────────────────────────────────────────────────────────────────────────

    internal sealed class CheckLogDlg : CleanupDlgBase
    {
        private sealed class Problem { public string Id, Field, Current, FixValue; public bool HasFix; }

        public CheckLogDlg() : base("Check the Log",
            "Problems Nexus finds in the log: grids that are not locators, a DXCC the call does not match, a band its frequency is not in, " +
            "an end before the start, a date in the future, and park or island references not in their format. Check the fixes to make; " +
            "problems with no fix are yours to look at. Only the checked fixes are made, as one change Undo a change takes back.",
            "Problems found", true)
        {
            Columns(("Call", 100), ("Date, UTC", 130), ("Problem", 200), ("Now", 120), ("Fix", 120));
            AddButton("Check Now", "Check the log now", Check);
            AddButton("Check All Fixes", "Check every problem that has a fix", () =>
            {
                foreach (ListViewItem i in List.Items) if (((Problem)i.Tag).HasFix) i.Checked = true;
                Say($"{List.CheckedItems.Count} fixes checked.");
            });
            AddButton("Make Checked Fixes...", "Make the checked fixes", MakeFixes);
            // A problem with no fix cannot be checked.
            List.ItemCheck += (s, e) => { if (e.NewValue == CheckState.Checked && !((Problem)List.Items[e.Index].Tag).HasFix) e.NewValue = CheckState.Unchecked; };
            Shown += (s, e) => Check();
        }

        private static string Words(string kind)
        {
            switch (kind)
            {
                case "Grid": return "Grid is not a locator";
                case "MyGrid": return "My grid is not a locator";
                case "Dxcc": return "DXCC is not the call's";
                case "NoBand": return "No band";
                case "BandFreq": return "Band does not match the frequency";
                case "Future": return "Date in the future";
                case "EndBeforeStart": return "Ends before it starts";
                case "Iota": return "IOTA reference not valid";
                case "Pota": return "POTA reference not valid";
                case "Sota": return "SOTA reference not valid";
                default: return kind;
            }
        }

        private static string ShowValue(string field, string value)
        {
            if (value == null) return "(clear)";
            if (field == "TIME_OFF" && long.TryParse(value, out long unix)) return NexusLogTools.When(unix);
            return value;
        }

        private void Check()
        {
            Run("Checking the log...", NexusLogTools.Check, a =>
            {
                List.Items.Clear();
                if (!a.Ok) { Say("Could not check: " + a.Why); return; }
                foreach (var p in NexusLogTools.List(a.Root, "problems"))
                {
                    var pr = new Problem
                    {
                        Id = NexusLogTools.Str(p, "id"), Field = NexusLogTools.Str(p, "field"), Current = NexusLogTools.Str(p, "current"),
                        HasFix = NexusLogTools.Flag(p, "hasFix"), FixValue = NexusLogTools.Str(p, "fixValue"),
                    };
                    List.Items.Add(new ListViewItem(new[]
                    {
                        NexusLogTools.Str(p, "call"), NexusLogTools.When(NexusLogTools.Num(p, "whenUnix")), Words(NexusLogTools.Str(p, "kind")),
                        ShowValue(pr.Field, pr.Current) == "(clear)" ? "" : ShowValue(pr.Field, pr.Current),
                        pr.HasFix ? ShowValue(pr.Field, pr.FixValue) : "no fix, yours to look at",
                    }) { Tag = pr });
                }
                long checkedCount = NexusLogTools.Num(a.Root, "checked");
                int fixable = List.Items.Cast<ListViewItem>().Count(i => ((Problem)i.Tag).HasFix);
                Say(List.Items.Count == 0 ? $"No problems in {checkedCount:N0} contacts."
                    : $"{List.Items.Count} problem{(List.Items.Count == 1 ? "" : "s")} in {checkedCount:N0} contacts, {fixable} with a fix.");
                if (List.Items.Count > 0) { List.Items[0].Selected = true; List.Items[0].Focused = true; }
            });
        }

        private void MakeFixes()
        {
            var fixes = List.CheckedItems.Cast<ListViewItem>().Select(i => (Problem)i.Tag).Where(p => p.HasFix)
                .Select(p => new NexusLogTools.Fix { Id = p.Id, Field = p.Field, Expect = p.Current, Value = p.FixValue }).ToList();
            if (fixes.Count == 0) { Say("Check the fixes to make first."); return; }
            if (!Confirm($"Make {fixes.Count} fix{(fixes.Count == 1 ? "" : "es")}? Undo a change takes them back.")) { Say("Nothing changed."); return; }
            Run("Fixing...", () => NexusLogTools.ApplyFixes(fixes), a =>
            {
                Say(NexusLogTools.Said(a, "Fixed"));
                if (a.Ok) { Changed = true; Check(); }
            });
        }
    }

    // ── One contact's history ────────────────────────────────────────────────────────────────

    internal sealed class HistoryDlg : CleanupDlgBase
    {
        private readonly string _nexusId;
        private readonly string _contact;

        private sealed class Entry { public long Seq; public string Kind; public string What; }

        public HistoryDlg(string nexusId, string contact) : base("History of " + contact,
            "Every change to this contact, oldest first: when, who made it and what it changed. Undo puts back the fields a change " +
            "changed that have not changed since. An undo is itself a change, so it can be undone too. Nothing is sent to any service.",
            "Changes", false)
        {
            _nexusId = nexusId;
            _contact = contact;
            Columns(("When", 130), ("By", 130), ("Change", 90), ("What changed", 380));
            AddButton("Undo This Change...", "Undo the selected change", Undo);
            Shown += (s, e) => Load_();
        }

        private static string KindWords(string kind)
        {
            switch (kind)
            {
                case "create": return "logged";
                case "edit": return "edited";
                case "delete": return "deleted";
                case "merge": return "merged";
                case "fill": return "filled";
                default: return kind;
            }
        }

        private void Load_()
        {
            Run("Reading the history...", () => NexusLogTools.History(_nexusId), a =>
            {
                List.Items.Clear();
                if (!a.Ok) { Say("Could not read the history: " + a.Why); return; }
                foreach (var e in NexusLogTools.List(a.Root, "entries"))
                {
                    var changes = NexusLogTools.List(e, "changes")
                        .Select(c => $"{NexusLogTools.Str(c, "tag")} {NexusLogTools.Str(c, "old") ?? "(none)"} to {NexusLogTools.Str(c, "new") ?? "(none)"}").ToList();
                    string kind = NexusLogTools.Str(e, "kind");
                    string what = kind == "create" ? "" : kind == "delete" ? "the whole contact" : string.Join("; ", changes);
                    List.Items.Add(new ListViewItem(new[] { WhenMs(NexusLogTools.Num(e, "atMs")), Source(NexusLogTools.Str(e, "source")), KindWords(kind), what })
                    { Tag = new Entry { Seq = NexusLogTools.Num(e, "seq"), Kind = kind, What = what } });
                }
                Say($"{List.Items.Count} change{(List.Items.Count == 1 ? "" : "s")}.");
                if (List.Items.Count > 0) { var last = List.Items[List.Items.Count - 1]; last.Selected = true; last.Focused = true; last.EnsureVisible(); }
            });
        }

        protected override void OnSelected()
        {
            Details.Text = List.SelectedItems.Count == 1 ? ((Entry)List.SelectedItems[0].Tag).What.Replace("; ", Environment.NewLine) : "";
        }

        private void Undo()
        {
            if (List.SelectedItems.Count != 1) { Say("Choose a change first."); return; }
            var e = (Entry)List.SelectedItems[0].Tag;
            string q = e.Kind == "create" ? $"Undo logging {_contact}? The contact is taken out of the log, to Recently deleted."
                     : e.Kind == "delete" ? $"Put {_contact} back as it was deleted?"
                     : "Undo this change? Fields changed again since stay as they are.";
            if (!Confirm(q)) { Say("Nothing changed."); return; }
            Run("Undoing...", () => NexusLogTools.UndoEntry(e.Seq, $"Undo of a change to {_contact}"), a =>
            {
                Say(UndoWords(a));
                if (a.Ok) { Changed = true; Load_(); }
            });
        }

        internal static string UndoWords(NexusLogTools.Answer a)
        {
            if (!a.Ok) return "Nothing was undone: " + (a.Why ?? a.State);
            long n = NexusLogTools.Num(a.Root, "changed");
            var refused = NexusLogTools.List(a.Root, "refused").Select(r => r.EnumerateArray().Skip(1).First().GetString()).ToList();
            int kept = NexusLogTools.List(a.Root, "kept").Count();
            string text = n == 0 ? "Nothing needed undoing." : $"Undone on {n} contact{(n == 1 ? "" : "s")}.";
            if (kept > 0) text += $" {kept} field{(kept == 1 ? " was" : "s were")} changed again since and stay as they are.";
            if (refused.Count > 0) text += " Not undone: " + string.Join(", ", refused.GroupBy(w => w).Select(g => $"{g.Count()} {NexusLogTools.Why(g.Key)}")) + ".";
            return text;
        }
    }

    // ── Recently deleted ─────────────────────────────────────────────────────────────────────

    internal sealed class DeletedDlg : CleanupDlgBase
    {
        private sealed class Gone { public string Id, Name; }

        public DeletedDlg() : base("Recently Deleted",
            "Contacts taken out of the log, newest first, kept for 90 days. Restore brings one back exactly as it was. " +
            "Nothing is sent to any service.",
            "Deleted contacts", false)
        {
            Columns(("Call", 100), ("Band", 60), ("Mode", 60), ("Contact, UTC", 140), ("Deleted", 130), ("By", 130));
            AddButton("Restore...", "Restore the selected contact", () => Restore(false));
            Shown += (s, e) => Load_();
        }

        private static string Date(string d, string t) =>
            d != null && d.Length >= 8 ? $"{d.Substring(0, 4)}-{d.Substring(4, 2)}-{d.Substring(6, 2)}" + (t != null && t.Length >= 4 ? $" {t.Substring(0, 2)}:{t.Substring(2, 2)}" : "") : "";

        private void Load_()
        {
            Run("Reading...", () => NexusLogTools.RecentlyDeleted(), a =>
            {
                List.Items.Clear();
                if (!a.Ok) { Say("Could not read: " + a.Why); return; }
                foreach (var e in NexusLogTools.List(a.Root, "entries"))
                {
                    string call = NexusLogTools.Str(e, "call"), band = NexusLogTools.Str(e, "band"), mode = NexusLogTools.Str(e, "mode");
                    string when = Date(NexusLogTools.Str(e, "date"), NexusLogTools.Str(e, "time"));
                    List.Items.Add(new ListViewItem(new[] { call, band, mode, when, WhenMs(NexusLogTools.Num(e, "atMs")), Source(NexusLogTools.Str(e, "source")) })
                    { Tag = new Gone { Id = NexusLogTools.Str(e, "id"), Name = $"{call} {band} {mode} {when}".Trim() } });
                }
                Say(List.Items.Count == 0 ? "No deleted contacts." : $"{List.Items.Count} deleted contact{(List.Items.Count == 1 ? "" : "s")}.");
                if (List.Items.Count > 0) { List.Items[0].Selected = true; List.Items[0].Focused = true; }
            });
        }

        private void Restore(bool anyway)
        {
            if (List.SelectedItems.Count != 1) { Say("Choose a contact first."); return; }
            var g = (Gone)List.SelectedItems[0].Tag;
            if (!anyway && !Confirm($"Restore {g.Name}?")) { Say("Nothing changed."); return; }
            Run("Restoring...", () => NexusLogTools.Restore(g.Id, anyway, $"Restore of {g.Name}"), a =>
            {
                bool dupe = a.Ok && NexusLogTools.List(a.Root, "refused").Any(r => r.EnumerateArray().Skip(1).First().GetString() == "duplicate");
                if (dupe && Confirm($"{g.Name} was logged again since it was deleted. Restore it anyway, as a second contact?")) { Restore(true); return; }
                Say(HistoryDlg.UndoWords(a));
                if (a.Ok && NexusLogTools.Num(a.Root, "changed") > 0) { Changed = true; Load_(); }
            });
        }
    }

    // ── Update from callbook ─────────────────────────────────────────────────────────────────

    internal sealed class CallbookUpdateDlg : CleanupDlgBase
    {
        // Jimmy Next's own lookups (set by the Controller): its QRZ or HamQTH login, its cache.
        internal static LookupManager Lookups;
        private const int MaxLookups = 250;    // a run's limit, as Nexus's own update keeps
        private const int PaceMs = 2000;       // one lookup every 2 seconds, as Nexus's

        private readonly List<string> _ids;
        private List<string> _calls = new List<string>();
        private readonly Dictionary<string, object> _answers = new Dictionary<string, object>();
        private readonly Button _lookUp, _stop, _fill;
        private volatile bool _stopAsked;

        public CallbookUpdateDlg(List<string> nexusIds) : base("Update from Callbook",
            "Fills the empty fields of these contacts from your callbook lookups: name, e-mail, QSL route and LoTW use on any " +
            "contact, and where the station is (grid, state, county, IOTA, zones) only on contacts from the last 12 months, since a " +
            "callbook says where a station is now. A field a contact already holds is never changed, and nothing is uploaded. " +
            "Look up first; the fill is one change Undo a change takes back.",
            "Fields to fill", false)
        {
            _ids = nexusIds;
            Columns(("Field", 200), ("Contacts", 100));
            _lookUp = AddButton("Look Up", "Look up the calls", LookUp);
            _stop = AddButton("Stop", "Stop looking up", () => _stopAsked = true);
            _stop.Enabled = false;
            _fill = AddButton("Fill...", "Fill the contacts", Fill);
            _fill.Enabled = false;
            Shown += (s, e) => Preview(first: true);
            FormClosing += (s, e) => _stopAsked = true;   // a lookup run in progress ends with the window
        }

        private void Preview(bool first)
        {
            var answers = new Dictionary<string, object>(_answers);
            Run("Looking at the contacts...", () => NexusLogTools.CallbookPreview(_ids, answers), a =>
            {
                List.Items.Clear();
                if (!a.Ok) { Say("Could not look at the contacts: " + a.Why); return; }
                _calls = NexusLogTools.List(a.Root, "calls").Select(c => c.GetString()).ToList();
                if (a.Root.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object)
                    foreach (var f in fields.EnumerateObject())
                        List.Items.Add(new ListViewItem(new[] { f.Name, f.Value.GetInt64().ToString() }));
                long could = NexusLogTools.Num(a.Root, "couldChange"), old = NexusLogTools.Num(a.Root, "locationTooOld");
                if (first)
                {
                    int toAsk = Lookups == null ? 0 : _calls.Count(c => Lookups.PrimaryNeedsLookup(c));
                    int asked = Math.Min(toAsk, MaxLookups);
                    Details.Text = $"{_ids.Count} contacts, {_calls.Count} calls. {_calls.Count - toAsk} answered from the lookup cache; " +
                                   $"{toAsk} to look up" + (toAsk > MaxLookups ? $", {MaxLookups} in this run" : "") +
                                   (asked > 0 ? $", about {Math.Max(1, asked * PaceMs / 60000)} minute{(asked * PaceMs / 60000 > 1 ? "s" : "")}" : "") + ".";
                    Say(Details.Text);
                }
                else
                {
                    Details.Text = $"{_answers.Count} of {_calls.Count} calls answered. {could} contact{(could == 1 ? "" : "s")} can be filled." +
                                   (old > 0 ? $" {old} older than 12 months keep their location blank." : "");
                    _fill.Enabled = could > 0;
                    Say(Details.Text);
                }
                if (List.Items.Count > 0) { List.Items[0].Selected = true; List.Items[0].Focused = true; }
            });
        }

        private async void LookUp()
        {
            _lookUp.Enabled = false; _stop.Enabled = true; _fill.Enabled = false;
            _stopAsked = false;
            int asked = 0, misses = 0, done = 0;
            string stoppedWhy = null;
            foreach (string call in _calls)
            {
                if (IsDisposed) return;
                if (_stopAsked) { stoppedWhy = "Stopped."; break; }
                LookupRecord rec = null;
                if (Lookups != null && Lookups.PrimaryNeedsLookup(call))
                {
                    if (asked >= MaxLookups) { stoppedWhy = $"{MaxLookups} lookups made; run it again for the rest."; break; }
                    asked++;
                    LookupRecord live = null;
                    try { live = await Lookups.LookupPrimaryAsync(call); } catch { }
                    // Three in a row with no record is how QRZ refuses lookups: stop, as Nexus does.
                    misses = live == null ? misses + 1 : 0;
                    if (misses >= 3) { stoppedWhy = "Three lookups in a row found nothing; the callbook may be refusing lookups. Stopped."; break; }
                    await Task.Delay(PaceMs);
                }
                try { rec = Lookups?.Build(call); } catch { }
                object ans = NexusLogTools.AnswerOf(call, rec);
                if (ans != null) _answers[call] = ans;
                done++;
                if (done % 10 == 0) Status.Text = $"Looked at {done} of {_calls.Count} calls...";
            }
            if (IsDisposed) return;
            _stop.Enabled = false; _lookUp.Enabled = true;
            if (stoppedWhy != null) Say(stoppedWhy);
            Preview(first: false);
        }

        private void Fill()
        {
            if (_answers.Count == 0) { Say("Nothing to fill: look up first."); return; }
            if (!Confirm($"Fill the empty fields of these {_ids.Count} contacts from {_answers.Count} callbook answers? Undo a change takes it back.")) { Say("Nothing changed."); return; }
            var answers = new Dictionary<string, object>(_answers);
            Run("Filling...", () => NexusLogTools.CallbookFill(_ids, answers, _ids.Count), a =>
            {
                string said = NexusLogTools.Said(a, "Filled");
                string stopped = NexusLogTools.Str(a.Root, "stopped");
                Say(stopped != null ? said + " " + stopped : said);
                if (a.Ok && NexusLogTools.Num(a.Root, "changed") > 0) { Changed = true; _fill.Enabled = false; }
            });
        }
    }

    // ── Undo a change ────────────────────────────────────────────────────────────────────────

    internal sealed class UndoRunsDlg : CleanupDlgBase
    {
        private sealed class RunRow { public string Run, What; public long Contacts; }

        public UndoRunsDlg() : base("Undo a Change",
            "Whole changes to the log that can be undone in one step: bulk edits, merges, fixes and imports, newest first. " +
            "Undo puts back what the change changed, except fields changed again since. Nothing is sent to any service.",
            "Changes to undo", false)
        {
            Columns(("When", 130), ("Change", 380), ("Contacts", 80));
            AddButton("Undo...", "Undo the selected change", Undo);
            Shown += (s, e) => Load_();
        }

        private void Load_()
        {
            Run("Reading...", () => NexusLogTools.UndoableRuns(), a =>
            {
                List.Items.Clear();
                var rows = NexusLogTools.RecordedRuns()
                    .Select(r => (At: new DateTimeOffset(DateTime.SpecifyKind(r.AtUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds(), new RunRow { Run = r.Run, What = r.What, Contacts = r.Contacts }))
                    .ToList();
                if (a.Ok)
                    foreach (var r in NexusLogTools.List(a.Root, "runs"))
                    {
                        string run = NexusLogTools.Str(r, "run");
                        if (rows.Any(x => x.Item2.Run == run)) continue;
                        string src = NexusLogTools.Str(r, "source");
                        rows.Add((NexusLogTools.Num(r, "atMs"), new RunRow
                        {
                            Run = run, Contacts = NexusLogTools.Num(r, "contacts"),
                            What = (src == "import" ? "An import" : "A change by " + Source(src)) + (NexusLogTools.Flag(r, "removes") ? ", its undo takes contacts out of the log" : ""),
                        }));
                    }
                foreach (var (at, row) in rows.OrderByDescending(x => x.At))
                    List.Items.Add(new ListViewItem(new[] { WhenMs(at), row.What, row.Contacts.ToString() }) { Tag = row });
                Say(List.Items.Count == 0 ? "No changes to undo." : $"{List.Items.Count} change{(List.Items.Count == 1 ? "" : "s")}.");
                if (List.Items.Count > 0) { List.Items[0].Selected = true; List.Items[0].Focused = true; }
            });
        }

        private void Undo()
        {
            if (List.SelectedItems.Count != 1) { Say("Choose a change first."); return; }
            var r = (RunRow)List.SelectedItems[0].Tag;
            if (!Confirm($"Undo: {r.What}, on {r.Contacts} contact{(r.Contacts == 1 ? "" : "s")}? Fields changed again since stay as they are.")) { Say("Nothing changed."); return; }
            Run("Undoing...", () => NexusLogTools.UndoRun(r.Run, "Undo of: " + r.What), a =>
            {
                Say(HistoryDlg.UndoWords(a));
                if (a.Ok) { Changed = true; Load_(); }
            });
        }
    }
}
