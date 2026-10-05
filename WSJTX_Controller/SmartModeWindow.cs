using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // The stations Smart Mode is managing (operator, 2026-10-04): each one's status, when it was
    // last heard and what it sent, its calls against the repeat limit and its time against the
    // time limit -- with Cancel for one station. Refreshed every second without moving the
    // selection or focus. Closing it (Close, Escape, Alt+F4) leaves Smart Mode running.
    // One line per station in a plain ListBox, not a multi-column ListView: NVDA reads only a
    // ListView's first column (see OtaSpotsWindow.MakeListBox's comment), so the whole row is
    // the item's own text, read the same way by JAWS and NVDA.
    internal sealed class SmartModeWindow : Form
    {
        private readonly Controller _ctrl;
        private readonly ListBox _list;
        private readonly Label _countLbl;
        private readonly Button _cancelBtn;
        private readonly Button _orderBtn;
        private readonly Button _closeBtn;
        private List<string> _fields;

        // The parts of a row, and their default order (all shown).
        internal static readonly string[] DefaultFields = { "station", "status", "notHeard", "lastHeard", "calls", "onList" };
        internal static readonly Dictionary<string, string> FieldLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "station", "Station" }, { "status", "Status" }, { "notHeard", "Not heard" },
            { "lastHeard", "Last heard" }, { "calls", "Calls" }, { "onList", "On the list" },
        };
        private readonly Timer _timer;
        private readonly List<string> _calls = new List<string>();   // the call behind each row, in row order

        public SmartModeWindow(Controller ctrl)
        {
            _ctrl = ctrl;
            Text = Wording.Get("List.SmartModeTitle");
            Font = ctrl.Font;
            Size = new Size(760, 300);
            MinimumSize = new Size(420, 200);
            // Its own window, like Logbook Center: on the taskbar and in Alt+Tab, Jimmy Next usable
            // beside it (operator, 2026-10-04).
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            MinimizeBox = true;
            MaximizeBox = true;

            _countLbl = new Label { Dock = DockStyle.Top, Height = 22, Padding = new Padding(6, 4, 6, 0), AccessibleName = "Station count" };

            _list = new ListBox
            {
                Dock = DockStyle.Fill,
                HorizontalScrollbar = true,
                IntegralHeight = false,
                AccessibleName = Wording.Get("List.SmartModeTitle"),
                TabIndex = 0,
            };
            ApplyListLook(ctrl.Settings);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4), TabIndex = 1 };
            _closeBtn = new Button { Text = "Close", AccessibleName = "Close", AutoSize = true, TabIndex = 1 };
            _closeBtn.Click += (s, e) => Close();
            _cancelBtn = new Button { Text = "&Cancel station", AccessibleName = "Cancel station", AutoSize = true, TabIndex = 0 };
            _cancelBtn.Click += (s, e) => CancelSelected();
            _orderBtn = new Button { Text = "Row Order...", AccessibleName = "Row Order", AutoSize = true, TabIndex = 1 };
            _orderBtn.Click += (s, e) => ChooseRowOrder();
            _closeBtn.TabIndex = 2;
            buttons.Controls.Add(_closeBtn);
            buttons.Controls.Add(_orderBtn);
            buttons.Controls.Add(_cancelBtn);
            _fields = ctrl.SmartWindowRowOrder;

            Controls.Add(_list);
            _countLbl.TabIndex = 2;   // not a tab stop; after the buttons so it never sits between them
            Controls.Add(_countLbl);
            Controls.Add(buttons);
            CancelButton = _closeBtn;   // Escape closes the window, never Smart Mode

            _timer = new Timer { Interval = 1000 };
            _timer.Tick += (s, e) => RefreshRows();
            Load += (s, e) => { RefreshRows(); _timer.Start(); _list.Focus(); };
            FormClosed += (s, e) => { _timer.Stop(); _timer.Dispose(); };
        }

        // The title from the wording file (List.SmartModeTitle), again after the file changes.
        internal void ApplyWording()
        {
            if (IsDisposed) return;
            string title = Wording.Get("List.SmartModeTitle");
            if (Text != title) Text = title;
            if (_list.AccessibleName != title) _list.AccessibleName = title;
        }

        // The look of Jimmy Next's own station lists (Options > Appearance): bold Consolas at the
        // chosen size, the list colours and the alternating row shade. Drawing only -- each row's
        // text, which the screen reader reads, is unchanged.
        private void ApplyListLook(JimmySettings st)
        {
            var font = new Font("Consolas", st.ListFontSize, FontStyle.Bold);
            _list.Font = font;
            _list.BackColor = st.ListBackColor;
            _list.ForeColor = st.ListForeColor;
            _list.DrawMode = DrawMode.OwnerDrawFixed;
            _list.ItemHeight = TextRenderer.MeasureText("Ag", font).Height + 2;
            Color alt = st.ListAltRowColor;
            _list.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                bool selected = (e.State & DrawItemState.Selected) != 0;
                Color back = selected ? SystemColors.Highlight : e.Index % 2 == 1 ? alt : _list.BackColor;
                Color fore = selected ? SystemColors.HighlightText : _list.ForeColor;
                using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);
                TextRenderer.DrawText(e.Graphics, _list.Items[e.Index].ToString(), _list.Font, e.Bounds, fore,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                e.DrawFocusRectangle();
            };
        }

        private void CancelSelected()
        {
            // Cancel is never disabled (operator, 2026-10-04: Tab did not always work). It used to
            // switch off whenever nothing was selected -- including for a moment each time the
            // list was rebuilt -- and a focused button that switches off drops the keyboard focus.
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _calls.Count)
            {
                MessageBox.Show(this, "No station selected.", "Smart Mode", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _ctrl.CancelSmartStation(_calls[i], this);
            RefreshRows();
        }

        private void ChooseRowOrder()
        {
            using (var dlg = new EditLogRowOrderDlg(_fields, DefaultFields, FieldLabels, "Smart Mode Row Order",
                "Choose what each station's row says, and in what order:", "Row parts"))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _fields = dlg.SelectedFields;
                _ctrl.SmartWindowRowOrder = _fields;
            }
            RefreshRows(force: true);   // rebuilt in the new layout, selection kept by station
        }

        // The chosen parts in order; a part with nothing to say (not heard, when it has been heard) is left out.
        internal static string RowText(WsjtxClient.SmartStationRow r, IList<string> fields = null)
        {
            var parts = new List<string>();
            foreach (var f in fields ?? DefaultFields)
            {
                switch (f)
                {
                    case "station": parts.Add(r.Shown); break;
                    case "status": parts.Add(r.Status); break;
                    case "notHeard": if (!string.IsNullOrEmpty(r.NotHeard)) parts.Add(r.NotHeard); break;
                    case "lastHeard": parts.Add("last heard " + r.LastHeard); break;
                    case "calls": parts.Add("calls " + r.Calls); break;
                    case "onList": parts.Add("on the list " + r.OnList); break;
                }
            }
            return string.Join(", ", parts.Where(p => !string.IsNullOrEmpty(p)));
        }

        // Rows are updated in place: an unchanged row is left alone (no re-read), and the
        // selected station stays selected wherever it moves.
        private void RefreshRows(bool force = false)
        {
            List<WsjtxClient.SmartStationRow> rows;
            try { rows = _ctrl.SmartStationRows(); }
            catch { return; }
            int prevIndex = _list.SelectedIndex;
            string selected = prevIndex >= 0 && prevIndex < _calls.Count ? _calls[prevIndex] : null;
            var texts = rows.Select(r => RowText(r, _fields)).ToList();
            bool sameCalls = !force && rows.Select(r => r.Call).SequenceEqual(_calls);

            _list.BeginUpdate();
            try
            {
                if (sameCalls)
                {
                    for (int i = 0; i < texts.Count; i++)
                        if ((string)_list.Items[i] != texts[i]) _list.Items[i] = texts[i];
                }
                else
                {
                    _list.Items.Clear();
                    _calls.Clear();
                    foreach (var r in rows) _calls.Add(r.Call);
                    foreach (var t in texts) _list.Items.Add(t);
                }
            }
            finally { _list.EndUpdate(); }
            // Owner-drawn rows don't size the horizontal scroll themselves: as wide as the widest row.
            int widest = 0;
            foreach (var t in texts) widest = Math.Max(widest, TextRenderer.MeasureText(t, _list.Font).Width + 8);
            if (_list.HorizontalExtent != widest) _list.HorizontalExtent = widest;

            int keep = selected != null ? _calls.IndexOf(selected) : -1;
            // The selected station left the list: stay at the same place in it.
            if (keep < 0 && _calls.Count > 0) keep = Math.Min(_calls.Count - 1, Math.Max(0, prevIndex));
            if (keep >= 0 && _list.SelectedIndex != keep) _list.SelectedIndex = keep;
            string count = rows.Count == 0 ? "No stations" : rows.Count == 1 ? "1 station" : $"{rows.Count} stations";
            if (_countLbl.Text != count) _countLbl.Text = count;
        }
    }
}
