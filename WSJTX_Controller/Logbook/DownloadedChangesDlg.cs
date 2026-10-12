using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Logbook, Sync, Review Downloaded Changes (2026-10-12): the differences a LoTW, QRZ or eQSL
    // download found between what the service holds and a logged contact's own fields
    // (NexusDownloadedChanges). Each line names the service, the contact, the field, what is
    // logged and what the service says, and is read whole by a screen reader.
    //
    //   - Nothing is checked when the window opens, and nothing is applied by closing it.
    //   - Apply Checked changes only the checked lines, after one question that says how many.
    //   - Decline Checked keeps those from being offered again (unless the service later says
    //     something else); Show declined brings them back into the list.
    //   - A line whose contact was edited since it was proposed is not applied; the result says so.
    internal sealed class DownloadedChangesDlg : Form
    {
        // What the last Apply did, for the Logbook window's status line.
        internal static int LastApplied;
        internal static string LastSummary = "";

        private readonly NexusDownloadedChanges _list;
        private readonly Func<IEnumerable<NexusDownloadedChanges.Proposal>, NexusDownloadedChanges.ApplyResult> _apply;
        private readonly CheckedListBox _lines;
        private readonly CheckBox _showDeclined;
        private readonly TextBox _status;
        private List<NexusDownloadedChanges.Proposal> _shown = new List<NexusDownloadedChanges.Proposal>();

        internal DownloadedChangesDlg(NexusDownloadedChanges list, Func<IEnumerable<NexusDownloadedChanges.Proposal>, NexusDownloadedChanges.ApplyResult> apply)
        {
            _list = list;
            _apply = apply;
            LastApplied = 0;
            LastSummary = "";
            Text = "Review Downloaded Changes";
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 420);
            MinimumSize = new Size(560, 300);
            Font = new Font("Microsoft Sans Serif", 8.25F);

            var about = new TextBox
            {
                ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control, TabStop = false,
                Text = "A download found these differences from your logged contacts. Your log is unchanged. " +
                       "Check the ones you accept, then Apply Checked. Confirmations were already merged and are not affected.",
                Location = new Point(10, 8), Size = new Size(740, 30), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            _lines = new CheckedListBox
            {
                Location = new Point(10, 42), Size = new Size(740, 290), CheckOnClick = false, HorizontalScrollbar = true, IntegralHeight = false,
                AccessibleName = "Proposed changes", TabIndex = 0,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };
            _showDeclined = new CheckBox
            {
                Text = "Show declined", AccessibleName = "Show declined changes", AutoSize = true, Location = new Point(10, 340), TabIndex = 1,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };
            _showDeclined.CheckedChanged += (s, e) => Fill(keepStatus: false);

            int x = 10, tab = 2;
            Button B(string text, string name, int width, EventHandler click)
            {
                var b = new Button
                {
                    Text = text, AccessibleName = name, Location = new Point(x, 366), Size = new Size(width, 26), TabIndex = tab++,
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                };
                b.Click += click;
                x += width + 6;
                return b;
            }
            var applyBtn = B("&Apply Checked", "Apply checked changes", 110, (s, e) => ApplyChecked());
            var all = B("Check A&ll", "Check all", 80, (s, e) => SetAll(true));
            var none = B("Check &None", "Check none", 90, (s, e) => SetAll(false));
            var decline = B("&Decline Checked", "Decline checked changes", 120, (s, e) => DeclineChecked());
            var close = B("Close", "Close", 80, (s, e) => Close());
            _status = new TextBox
            {
                ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control, TabStop = true, TabIndex = tab++,
                AccessibleName = "Status", Location = new Point(10, 398), Size = new Size(740, 16),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };
            Controls.AddRange(new Control[] { about, _lines, _showDeclined, applyBtn, all, none, decline, close, _status });
            CancelButton = close;   // Escape closes, applying nothing
            Fill(keepStatus: false);
            ActiveControl = _lines;
        }

        private void Fill(bool keepStatus)
        {
            _shown = _list.Items.Where(p => _showDeclined.Checked || !p.Declined)
                .OrderBy(p => p.Declined).ThenByDescending(p => p.WhenUnix).ThenBy(p => p.Call, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Field).ToList();
            _lines.BeginUpdate();
            _lines.Items.Clear();
            foreach (var p in _shown) _lines.Items.Add((p.Declined ? "Declined. " : "") + p.Sentence(), false);   // never checked for the operator
            _lines.EndUpdate();
            if (_lines.Items.Count > 0) _lines.SelectedIndex = 0;
            if (!keepStatus)
                _status.Text = _shown.Count == 0
                    ? (_list.Items.Count == 0 ? "No proposed changes." : "No proposed changes waiting. Check Show declined to see the declined ones.")
                    : $"{_shown.Count} proposed change{(_shown.Count == 1 ? "" : "s")}. None is checked.";
        }

        private List<NexusDownloadedChanges.Proposal> Checked() =>
            _lines.CheckedIndices.Cast<int>().Where(i => i >= 0 && i < _shown.Count).Select(i => _shown[i]).ToList();

        private void SetAll(bool on)
        {
            for (int i = 0; i < _lines.Items.Count; i++) _lines.SetItemChecked(i, on);
            _status.Text = on ? $"All {_lines.Items.Count} checked. Apply Checked would change every one of them." : "None is checked.";
        }

        private void ApplyChecked()
        {
            var chosen = Checked();
            if (chosen.Count == 0) { _status.Text = "Nothing is checked, so nothing was applied."; return; }
            int contacts = chosen.Select(p => p.ContactId).Distinct().Count();
            if (MessageBox.Show(this,
                    $"Apply {chosen.Count} change{(chosen.Count == 1 ? "" : "s")} to {contacts} contact{(contacts == 1 ? "" : "s")} in your log?\n\n" +
                    string.Join("\n", chosen.Take(8).Select(p => p.Sentence())) + (chosen.Count > 8 ? $"\n... and {chosen.Count - 8} more" : ""),
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                _status.Text = "Nothing was applied.";
                return;
            }
            NexusDownloadedChanges.ApplyResult r;
            try { r = _apply(chosen); }
            catch (Exception ex) { _status.Text = "Nothing was applied: " + ex.Message; return; }
            LastApplied += r.Applied;
            LastSummary = "Downloaded changes: " + r.Sentence();
            Fill(keepStatus: true);
            _status.Text = r.Sentence() + (r.Notes.Count > 0 ? " " + string.Join(" ", r.Notes.Take(3)) : "");
        }

        private void DeclineChecked()
        {
            var chosen = Checked();
            if (chosen.Count == 0) { _status.Text = "Nothing is checked."; return; }
            foreach (var p in chosen) p.Declined = true;
            try { _list.Save(); }
            catch (Exception ex) { _status.Text = "Could not save: " + ex.Message; return; }
            Fill(keepStatus: true);
            _status.Text = $"{chosen.Count} declined. Your log is unchanged; they are not offered again unless the service says something else.";
        }
    }
}
