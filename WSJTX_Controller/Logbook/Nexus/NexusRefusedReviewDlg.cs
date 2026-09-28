using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Logbook migration, D2: review of the contacts Nexus's own duplicate rule refused, which
    // Jimmy's outbox keeps with every detail until they are dealt with. Opened only by the
    // operator (Enter on Logbook Center's Status field while something is held). A list, one line
    // per contact with its full details, then Dismiss and Close; Escape closes.
    public class NexusRefusedReviewDlg : Form
    {
        private readonly NexusLogOutbox _outbox;
        private readonly ListBox _list;
        private readonly Button _dismissBtn;

        public NexusRefusedReviewDlg(NexusLogOutbox outbox)
        {
            _outbox = outbox;
            var font = new Font("Microsoft Sans Serif", 8.25F);
            Text = "Duplicate Contacts Not Logged";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(720, 300);
            KeyPreview = true;

            _list = new ListBox
            {
                Location = new Point(10, 10), Size = new Size(700, 245), Font = font,
                IntegralHeight = false, HorizontalScrollbar = true, TabIndex = 0,
                AccessibleName = "Duplicate contacts held",
            };
            _dismissBtn = new Button
            {
                Text = "&Dismiss", Location = new Point(550, 265), Size = new Size(75, 25), TabIndex = 1,
                AccessibleName = "Dismiss", Font = font,
            };
            _dismissBtn.Click += (s, e) => DismissSelected();
            var closeBtn = new Button
            {
                Text = "Close", Location = new Point(635, 265), Size = new Size(75, 25), TabIndex = 2,
                AccessibleName = "Close", DialogResult = DialogResult.Cancel, Font = font,
            };
            CancelButton = closeBtn;
            Controls.AddRange(new Control[] { _list, _dismissBtn, closeBtn });
            Fill(0);
        }

        // One line per held contact: everything kept about it.
        internal static string Describe(NexusLogOutbox.RefusedEntry r)
        {
            var q = r.Qso;
            var inv = CultureInfo.InvariantCulture;
            string when = DateTimeOffset.FromUnixTimeSeconds((long)q.WhenUnix).UtcDateTime.ToString("yyyy-MM-dd HH:mm", inv);
            var parts = new System.Collections.Generic.List<string> { q.Call, q.Band, q.Mode, when + " UTC" };
            if (q.FreqMhz > 0) parts.Add(q.FreqMhz.ToString("0.000000", inv) + " MHz");
            if (!string.IsNullOrEmpty(q.RstSent)) parts.Add("sent " + q.RstSent);
            if (!string.IsNullOrEmpty(q.RstRcvd)) parts.Add("received " + q.RstRcvd);
            if (!string.IsNullOrEmpty(q.Grid)) parts.Add("grid " + q.Grid);
            if (!string.IsNullOrEmpty(q.Name)) parts.Add("name " + q.Name);
            if (!string.IsNullOrEmpty(q.Comment)) parts.Add("comment " + q.Comment);
            if (r.ExistingWhenUnix.HasValue)
                parts.Add("matches the contact logged " +
                          DateTimeOffset.FromUnixTimeSeconds((long)r.ExistingWhenUnix.Value).UtcDateTime.ToString("yyyy-MM-dd HH:mm", inv) + " UTC");
            return string.Join(", ", parts);
        }

        private void Fill(int select)
        {
            var held = _outbox.Refused.OrderBy(r => r.RefusedUtc).ToList();
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var r in held) _list.Items.Add(new Item(r));
            _list.EndUpdate();
            if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(select, _list.Items.Count - 1);
            _dismissBtn.Enabled = _list.Items.Count > 0;
        }

        private void DismissSelected()
        {
            if (!(_list.SelectedItem is Item item)) return;
            var answer = MessageBox.Show(this,
                $"Discard the held details of {item.Entry.Qso.Call}? The contact is not in the log.",
                "Dismiss Duplicate", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
            int at = _list.SelectedIndex;
            _outbox.Dismiss(item.Entry.ReqId);
            Fill(at);
            (_list.Items.Count > 0 ? (Control)_list : this.CancelButton as Control)?.Focus();
        }

        private sealed class Item
        {
            public readonly NexusLogOutbox.RefusedEntry Entry;
            public Item(NexusLogOutbox.RefusedEntry e) { Entry = e; }
            public override string ToString() => Describe(Entry);
        }
    }
}
