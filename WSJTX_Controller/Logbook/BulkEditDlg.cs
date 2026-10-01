using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Logbook Center's bulk edit (operator, 2026-10-01): the same value into one field across all
    // the selected contacts. Only fields that are about MY station or my own remarks -- the ones
    // that really are the same for a run of contacts (power, rig, grid, my park, comment, notes).
    // Never the contact itself (call, time, band, mode, reports), the station worked, protected
    // fields or confirmations: those differ contact to contact, and a wrong bulk value there
    // would quietly change awards or uploads. A checked field left blank is cleared.
    internal class BulkEditDlg : Form
    {
        internal static readonly (string Field, string Label)[] Fields =
        {
            ("power",     "Power watts"),
            ("myRig",     "My rig"),
            ("myGrid",    "My grid"),
            ("myProgram", "My park program"),
            ("myRef",     "My park reference"),
            ("comment",   "Comment, shared"),
            ("notes",     "Notes, private"),
        };

        // field -> new value (null = clear); only the fields the operator checked.
        internal Dictionary<string, string> Changes { get; private set; }

        private readonly List<(string Field, CheckBox Box, Control Value)> _rows = new List<(string, CheckBox, Control)>();
        private readonly TextBox _status;

        public BulkEditDlg(int count)
        {
            Text = $"Edit {count} contacts";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(8) };
            Controls.Add(flow);
            int tab = 0;

            var intro = new TextBox
            {
                Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control,
                Width = 460, Height = 66, TabIndex = tab++, AccessibleName = "About bulk edit",
                Text = $"Check each field to change on all {count} selected contacts and type its new value. " +
                       "A checked field left blank is cleared. The logbook is backed up first. " +
                       "Changes are not sent again to QRZ, Club Log or LoTW.",
            };
            flow.Controls.Add(intro);

            var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, TabIndex = tab++ };
            flow.Controls.Add(grid);
            foreach (var (field, label) in Fields)
            {
                var cb = new CheckBox { Text = "Change " + label.ToLowerInvariant(), AccessibleName = "Change " + label.ToLowerInvariant(), AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
                Control value;
                if (field == "myProgram")
                {
                    var combo = new ComboBox { Width = 200, AccessibleName = label, DropDownStyle = ComboBoxStyle.DropDown };
                    combo.Items.AddRange(new object[] { "POTA", "SOTA", "WWFF" });
                    combo.TextChanged += (s, e) => { if (combo.Text.Length > 0) cb.Checked = true; };
                    value = combo;
                }
                else
                {
                    var tb = new TextBox { Width = field == "comment" || field == "notes" ? 260 : 200, AccessibleName = label };
                    tb.TextChanged += (s, e) => { if (tb.Text.Length > 0) cb.Checked = true; };   // typing a value means change it
                    value = tb;
                }
                cb.TabIndex = grid.Controls.Count;
                grid.Controls.Add(cb);
                value.TabIndex = grid.Controls.Count;
                grid.Controls.Add(value);
                _rows.Add((field, cb, value));
            }

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, TabIndex = tab++ };
            var apply = new Button { Text = "Apply...", AutoSize = true, AccessibleName = "Apply to the selected contacts" };
            apply.Click += Apply_Click;
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(apply);
            buttons.Controls.Add(cancel);
            flow.Controls.Add(buttons);
            CancelButton = cancel;

            _status = new TextBox
            {
                Width = 460, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control,
                TabIndex = tab++, AccessibleName = "Status",
            };
            flow.Controls.Add(_status);
            Shown += (s, e) => _rows[0].Box.Focus();
        }

        private void Apply_Click(object sender, EventArgs e)
        {
            var changes = new Dictionary<string, string>();
            foreach (var (field, box, value) in _rows)
            {
                if (!box.Checked) continue;
                string v = value.Text.Trim();
                changes[field] = v.Length == 0 ? null : v;
            }
            if (changes.Count == 0) { _status.Text = "Check at least one field to change."; return; }
            string error = Validate(changes);
            if (error != null) { _status.Text = error; return; }
            Changes = changes;
            DialogResult = DialogResult.OK;
            Close();
        }

        internal static string Validate(Dictionary<string, string> changes)
        {
            if (changes.TryGetValue("power", out string p) && p != null
                && !(double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double w) && w >= 0))
                return "Power is not a number of watts.";
            return null;
        }

        // "power watts and my rig"
        internal static string Describe(IEnumerable<string> fields)
        {
            var names = fields.Select(f => Fields.First(x => x.Field == f).Label.ToLowerInvariant()).ToList();
            return names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];
        }

        // Puts the changes into one contact; true when anything actually changed. Touches only the
        // fields above -- everything else the contact holds is left exactly as it was.
        internal static bool Apply(NexusQso q, Dictionary<string, string> changes)
        {
            bool changed = false;
            void Set(string current, string value, Action<string> assign)
            {
                if (string.Equals(current ?? "", value ?? "", StringComparison.Ordinal)) return;
                assign(value);
                changed = true;
            }
            q.Ota = q.Ota ?? new NexusOta();
            foreach (var kv in changes)
            {
                string v = kv.Value;
                switch (kv.Key)
                {
                    case "power":
                        double? watts = v == null ? (double?)null : double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture);
                        if (q.TxPower != watts) { q.TxPower = watts; changed = true; }
                        break;
                    case "myRig":     Set(q.MyRig, v, x => q.MyRig = x); break;
                    case "myGrid":    Set(q.MyGrid, v?.ToUpperInvariant(), x => q.MyGrid = x); break;
                    case "myProgram": Set(q.Ota.MyProgram, v?.ToUpperInvariant(), x => q.Ota.MyProgram = x); break;
                    case "myRef":     Set(q.Ota.MyRef, v?.ToUpperInvariant(), x => q.Ota.MyRef = x); break;
                    case "comment":   Set(q.Comment, v, x => q.Comment = x); break;
                    case "notes":     Set(q.Notes, v, x => q.Notes = x); break;
                }
            }
            return changed;
        }
    }
}
