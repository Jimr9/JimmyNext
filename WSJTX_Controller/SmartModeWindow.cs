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
    internal sealed class SmartModeWindow : Form
    {
        private readonly Controller _ctrl;
        private readonly ListView _list;
        private readonly Label _countLbl;
        private readonly Button _cancelBtn;
        private readonly Button _closeBtn;
        private readonly Timer _timer;

        public SmartModeWindow(Controller ctrl)
        {
            _ctrl = ctrl;
            Text = "Smart Mode stations";
            Font = ctrl.Font;
            Size = new Size(760, 300);
            MinimumSize = new Size(420, 200);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = true;

            _countLbl = new Label { Dock = DockStyle.Top, Height = 22, Padding = new Padding(6, 4, 6, 0), AccessibleName = "Station count" };

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                AccessibleName = "Smart Mode stations",
                TabIndex = 0,
            };
            _list.Columns.Add("Station", 100);
            _list.Columns.Add("Status", 230);
            _list.Columns.Add("Last heard", 230);
            _list.Columns.Add("Calls", 70);
            _list.Columns.Add("On the list", 100);
            _list.SelectedIndexChanged += (s, e) => _cancelBtn.Enabled = _list.SelectedItems.Count > 0;

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4), TabIndex = 1 };
            _closeBtn = new Button { Text = "Close", AccessibleName = "Close", AutoSize = true, TabIndex = 1 };
            _closeBtn.Click += (s, e) => Close();
            _cancelBtn = new Button { Text = "&Cancel station", AccessibleName = "Cancel station", AutoSize = true, TabIndex = 0, Enabled = false };
            _cancelBtn.Click += (s, e) => CancelSelected();
            buttons.Controls.Add(_closeBtn);
            buttons.Controls.Add(_cancelBtn);

            Controls.Add(_list);
            Controls.Add(_countLbl);
            Controls.Add(buttons);
            CancelButton = _closeBtn;   // Escape closes the window, never Smart Mode

            _timer = new Timer { Interval = 1000 };
            _timer.Tick += (s, e) => RefreshRows();
            Load += (s, e) => { RefreshRows(); _timer.Start(); _list.Focus(); };
            FormClosed += (s, e) => { _timer.Stop(); _timer.Dispose(); };
        }

        private void CancelSelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            _ctrl.CancelSmartStation(_list.SelectedItems[0].Name, this);
            RefreshRows();
        }

        // Updates the rows in place: an unchanged cell is left alone, and the selected station
        // stays selected (and focused) wherever it moves.
        private void RefreshRows()
        {
            List<WsjtxClient.SmartStationRow> rows;
            try { rows = _ctrl.SmartStationRows(); }
            catch { return; }
            string selected = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Name : null;

            _list.BeginUpdate();
            try
            {
                for (int i = _list.Items.Count - 1; i >= 0; i--)
                    if (!rows.Any(r => r.Call == _list.Items[i].Name)) _list.Items.RemoveAt(i);
                for (int i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    string[] cells = { r.Shown, r.Status, r.LastHeard, r.Calls, r.OnList };
                    int at = _list.Items.IndexOfKey(r.Call);
                    ListViewItem item;
                    if (at < 0)
                    {
                        item = new ListViewItem(cells) { Name = r.Call };
                        _list.Items.Insert(i, item);
                        continue;
                    }
                    item = _list.Items[at];
                    if (at != i)
                    {
                        _list.Items.RemoveAt(at);
                        _list.Items.Insert(i, item);
                    }
                    for (int c = 0; c < cells.Length; c++)
                        if (item.SubItems[c].Text != cells[c]) item.SubItems[c].Text = cells[c];
                }
            }
            finally { _list.EndUpdate(); }

            int keep = selected != null ? _list.Items.IndexOfKey(selected) : -1;
            if (keep < 0 && _list.Items.Count > 0 && _list.SelectedItems.Count == 0) keep = 0;
            if (keep >= 0 && !_list.Items[keep].Selected)
            {
                _list.Items[keep].Selected = true;
                _list.Items[keep].Focused = true;
            }
            _cancelBtn.Enabled = _list.SelectedItems.Count > 0;
            string count = rows.Count == 0 ? "No stations" : rows.Count == 1 ? "1 station" : $"{rows.Count} stations";
            if (_countLbl.Text != count) _countLbl.Text = count;
        }
    }
}
