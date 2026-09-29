using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Logbook Center's editor for one logged contact: every field Nexus keeps for it.
    //
    //   - Editable: the contact, the station worked, my station, comment and notes.
    //   - Protected (DXCC, CQ/ITU zones, station callsign, operator, upload status): shown read
    //     only until "Allow editing protected fields" is checked -- off every time this opens,
    //     because a wrong value there quietly changes awards or uploads.
    //   - Confirmations (LoTW, QRZ, eQSL, card) and credit: ALWAYS read only. The services own
    //     them, and Nexus keeps them through any edit (a sync would put back what they know).
    //   - Any other field the contact carries: listed, read only.
    //
    // The dialog only builds the edited contact; onSave writes it (NexusLogbookService.SaveRecord)
    // and returns null, or an error to show -- a failed save leaves every field as typed.
    internal class ContactEditDlg : Form
    {
        private readonly NexusQso _orig;
        private readonly Func<NexusQso, List<(string Service, bool Sent)>, string> _onSave;
        private readonly FlowLayoutPanel _flow;
        private int _groupTab;

        private TextBox _call, _band, _freq, _date, _timeOn, _timeOff, _rstSent, _rstRcvd;
        private ComboBox _mode, _theirProgram, _myProgram;
        private TextBox _name, _qth, _state, _county, _country, _grid, _iota, _theirRef;
        private TextBox _power, _myRig, _myGrid, _myRef, _comment, _notes;
        private CheckBox _allowProtected;
        private TextBox _dxcc, _cqz, _ituz, _stationCall, _operator;
        private readonly Dictionary<string, CheckBox> _uploads = new Dictionary<string, CheckBox>();
        private TextBox _status;

        private static readonly string[] CommonModes =
        {
            "FT8", "FT4", "SSB", "CW", "RTTY", "PSK31", "FM", "AM", "WSPR", "JT65", "JT9", "MSK144", "FST4",
        };
        private static readonly string[] Programs = { "POTA", "SOTA", "WWFF" };

        // Upload services: Nexus's name, and what the operator hears.
        private static readonly (string Service, string Label)[] UploadServices =
        {
            ("qrz", "Uploaded to QRZ"), ("clublog", "Uploaded to Club Log"), ("lotw", "Uploaded to LoTW"),
            ("eqsl", "Uploaded to eQSL"), ("hrdlog", "Uploaded to HRDLog"),
        };

        // Fields shown in their own boxes (or kept by Jimmy for itself), so not listed again.
        private static readonly HashSet<string> ShownTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CNTY", "CQZ", "ITUZ", NexusMigration.HrdlogUploadedTag,
        };

        public ContactEditDlg(NexusQso q, Func<NexusQso, List<(string Service, bool Sent)>, string> onSave)
        {
            _orig = q;
            _onSave = onSave;
            Text = "Edit Contact " + q.Call;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScroll = true;
            KeyPreview = true;

            _flow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Location = new Point(6, 6),
            };
            Controls.Add(_flow);

            var when = DateTimeOffset.FromUnixTimeSeconds((long)q.WhenUnix).UtcDateTime;
            string timeOff = q.TimeOffUnix.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds((long)q.TimeOffUnix.Value).UtcDateTime.ToString("HHmmss", CultureInfo.InvariantCulture) : "";

            var t = Group("Contact");
            _call = Box(t, "Callsign", q.Call, upper: true);
            _band = Box(t, "Band", q.Band);
            _mode = Combo(t, "Mode", q.Mode, CommonModes);
            _freq = Box(t, "Frequency MHz", q.FreqMhz > 0 ? q.FreqMhz.ToString("0.000000", CultureInfo.InvariantCulture) : "");
            _date = Box(t, "Date YYYYMMDD", when.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            _timeOn = Box(t, "Time on HHMMSS", when.ToString("HHmmss", CultureInfo.InvariantCulture));
            _timeOff = Box(t, "Time off HHMMSS", timeOff);
            _rstSent = Box(t, "RST sent", q.RstSent);
            _rstRcvd = Box(t, "RST rcvd", q.RstRcvd);

            t = Group("Station worked");
            _name = Box(t, "Name", q.Name);
            _qth = Box(t, "QTH", q.Qth);
            _state = Box(t, "State", q.State, upper: true);
            _county = Box(t, "County", q.ExtraValue("CNTY"));
            _country = Box(t, "Country", q.Country);
            _grid = Box(t, "Grid", q.Grid, upper: true);
            _iota = Box(t, "IOTA", q.Ota?.Iota, upper: true);
            _theirProgram = Combo(t, "Their program", q.Ota?.TheirProgram, Programs);
            _theirRef = Box(t, "Their reference", q.Ota?.TheirRef, upper: true);

            t = Group("My station");
            _power = Box(t, "Power watts", q.TxPower.HasValue ? q.TxPower.Value.ToString("0.###", CultureInfo.InvariantCulture) : "");
            _myRig = Box(t, "My rig", q.MyRig);
            _myGrid = Box(t, "My grid", q.MyGrid, upper: true);
            _myProgram = Combo(t, "My program", q.Ota?.MyProgram, Programs);
            _myRef = Box(t, "My reference", q.Ota?.MyRef, upper: true);

            t = Group("Remarks");
            _comment = Box(t, "Comment", q.Comment, width: 420, span: 3);
            _notes = Box(t, "Notes", q.Notes, width: 420, span: 3, lines: 3);

            t = Group("Protected");
            _allowProtected = new CheckBox { Text = "Allow editing protected fields", AutoSize = true, Checked = false };
            _allowProtected.CheckedChanged += (s, e) => SetProtectedEditable(_allowProtected.Checked);
            Add(t, _allowProtected, 4);
            _dxcc = Box(t, "DXCC", q.Dxcc?.ToString(CultureInfo.InvariantCulture));
            _cqz = Box(t, "CQ zone", q.ExtraValue("CQZ"));
            _ituz = Box(t, "ITU zone", q.ExtraValue("ITUZ"));
            _stationCall = Box(t, "Station callsign", q.StationCallsign, upper: true);
            _operator = Box(t, "Operator", q.Operator, upper: true);
            foreach (var (service, label) in UploadServices)
            {
                var cb = new CheckBox { Text = label, AutoSize = true, Checked = UploadSent(q, service), AccessibleName = label };
                _uploads[service] = cb;
                Add(t, cb, 2);
            }

            t = Group("Read only");
            Box(t, "Confirmations", Confirmations(q), width: 420, span: 3, readOnly: true);
            string other = OtherFields(q);
            Box(t, "Other fields", other.Length == 0 ? "none" : other, width: 420, span: 3, readOnly: true,
                lines: Math.Min(8, Math.Max(1, other.Count(c => c == '\n') + 1)));

            // Buttons and status line.
            var bottom = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, TabIndex = _groupTab++ };
            var save = new Button { Text = "Save", AutoSize = true, TabIndex = 0 };
            save.Click += Save_Click;
            var close = new Button { Text = "Close", AutoSize = true, TabIndex = 1 };
            close.Click += (s, e) => Close();
            bottom.Controls.Add(save);
            bottom.Controls.Add(close);
            _flow.Controls.Add(bottom);
            AcceptButton = save;
            CancelButton = close;

            _status = new TextBox
            {
                Width = 560, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control,
                TabStop = true, TabIndex = _groupTab++, AccessibleName = "Status",
            };
            _flow.Controls.Add(_status);

            SetProtectedEditable(false);
            ClientSize = new Size(Math.Min(_flow.PreferredSize.Width + 30, 760),
                                  Math.Min(_flow.PreferredSize.Height + 12, Screen.FromControl(this).WorkingArea.Height - 80));
        }

        // ── Layout ───────────────────────────────────────────────────────────────────────────

        // A titled group (screen readers name it on entering) holding label/box pairs, two per row.
        private TableLayoutPanel Group(string title)
        {
            var gb = new GroupBox
            {
                Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                TabIndex = _groupTab++, Padding = new Padding(6), MinimumSize = new Size(560, 0),
            };
            var t = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Dock = DockStyle.Fill };
            gb.Controls.Add(t);
            _flow.Controls.Add(gb);
            return t;
        }

        private static void Add(TableLayoutPanel t, Control c, int span = 1)
        {
            c.TabIndex = t.Controls.Count;
            t.Controls.Add(c);
            if (span > 1) t.SetColumnSpan(c, span);
        }

        private static TextBox Box(TableLayoutPanel t, string label, string value, bool upper = false,
            int width = 150, int span = 1, bool readOnly = false, int lines = 1)
        {
            t.Controls.Add(new Label { Text = label + ":", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) });
            var tb = new TextBox
            {
                Text = value ?? "", Width = width, AccessibleName = label, ReadOnly = readOnly,
                CharacterCasing = upper ? CharacterCasing.Upper : CharacterCasing.Normal,
            };
            if (lines > 1)
            {
                tb.Multiline = true;
                tb.ScrollBars = ScrollBars.Vertical;
                tb.Height = tb.Font.Height * lines + 8;
            }
            Add(t, tb, span);
            return tb;
        }

        // Editable list: pick a common value or type another.
        private static ComboBox Combo(TableLayoutPanel t, string label, string value, string[] items)
        {
            t.Controls.Add(new Label { Text = label + ":", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) });
            var cb = new ComboBox { Width = 150, AccessibleName = label, DropDownStyle = ComboBoxStyle.DropDown };
            cb.Items.AddRange(items);
            cb.Text = value ?? "";
            Add(t, cb);
            return cb;
        }

        // Read only = still reachable with Tab and read by the screen reader, just not changeable.
        private void SetProtectedEditable(bool editable)
        {
            foreach (var tb in new[] { _dxcc, _cqz, _ituz, _stationCall, _operator }) tb.ReadOnly = !editable;
            foreach (var cb in _uploads.Values) cb.AutoCheck = editable;
        }

        // ── What the contact holds ───────────────────────────────────────────────────────────

        private static bool UploadSent(NexusQso q, string service)
        {
            switch (service)
            {
                case "qrz": return q.Upload?.Qrz?.IsSent == true;
                case "clublog": return q.Upload?.Clublog?.IsSent == true;
                case "lotw": return q.Upload?.Lotw?.IsSent == true;
                case "eqsl": return q.Upload?.Eqsl?.IsSent == true;
                default: return !string.IsNullOrEmpty(q.ExtraValue(NexusMigration.HrdlogUploadedTag));
            }
        }

        private static string Confirmations(NexusQso q)
        {
            var by = new List<string>();
            if (q.QslRcvd.Lotw) by.Add("LoTW");
            if (q.QslRcvd.Qrz) by.Add("QRZ");
            if (q.QslRcvd.Eqsl) by.Add("eQSL");
            if (q.QslRcvd.Card) by.Add("card");
            string text = by.Count == 0 ? "not confirmed" : "confirmed by " + string.Join(", ", by);
            if (q.QslSent.Sent) text += "; QSL sent" + (string.IsNullOrEmpty(q.QslSent.Via) ? "" : " via " + q.QslSent.Via);
            if (q.CreditGranted.Count > 0) text += "; credit granted " + string.Join(", ", q.CreditGranted);
            if (q.CreditSubmitted.Count > 0) text += "; credit submitted " + string.Join(", ", q.CreditSubmitted);
            return text;
        }

        // Everything else the contact carries, one per line. Jimmy's own bookkeeping tags are left out.
        private static string OtherFields(NexusQso q)
        {
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(q.PropMode)) lines.Add("PROP_MODE: " + q.PropMode);
            if (!string.IsNullOrEmpty(q.SatName)) lines.Add("SAT_NAME: " + q.SatName);
            if (q.FreqRxMhz.HasValue) lines.Add("FREQ_RX: " + q.FreqRxMhz.Value.ToString("0.000000", CultureInfo.InvariantCulture));
            foreach (var kv in q.Extra)
            {
                if (kv.Count != 2 || ShownTags.Contains(kv[0]) || kv[0].StartsWith("APP_JIMMY_", StringComparison.OrdinalIgnoreCase)) continue;
                lines.Add(kv[0] + ": " + kv[1]);
            }
            return string.Join(Environment.NewLine, lines);
        }

        // ── Save ─────────────────────────────────────────────────────────────────────────────

        private void Save_Click(object sender, EventArgs e)
        {
            string error = null;
            var q = NexusLogClient.FromJson<NexusQso>(NexusLogClient.ToJson(_orig));   // a copy to change

            q.Call = _call.Text.Trim().ToUpperInvariant();
            if (q.Call.Length == 0) error = "Callsign cannot be blank.";
            q.Band = _band.Text.Trim().ToLowerInvariant();
            q.Mode = _mode.Text.Trim().ToUpperInvariant();

            // Times and frequency: a box left as shown keeps the stored value exactly.
            var when = DateTimeOffset.FromUnixTimeSeconds((long)_orig.WhenUnix).UtcDateTime;
            string origDate = when.ToString("yyyyMMdd", CultureInfo.InvariantCulture), origTime = when.ToString("HHmmss", CultureInfo.InvariantCulture);
            string date = _date.Text.Trim(), timeOn = Pad6(_timeOn.Text);
            if (date != origDate || timeOn != origTime)
            {
                long w = NexusMigration.UnixOf(date, timeOn);
                if (w <= 0) error = error ?? "Date or time on is not valid (YYYYMMDD and HHMM or HHMMSS).";
                else q.WhenUnix = (ulong)w;
            }
            string off = Pad6(_timeOff.Text);
            string origOff = _orig.TimeOffUnix.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds((long)_orig.TimeOffUnix.Value).UtcDateTime.ToString("HHmmss", CultureInfo.InvariantCulture) : "";
            if (off.Length > 0 && (off != origOff || q.WhenUnix != _orig.WhenUnix))
            {
                long o = NexusMigration.UnixOf(date, off);
                if (o <= 0) error = error ?? "Time off is not valid (HHMM or HHMMSS).";
                else q.TimeOffUnix = (ulong)(o < (long)q.WhenUnix ? o + 86_400 : o);
            }
            string freqShown = _orig.FreqMhz > 0 ? _orig.FreqMhz.ToString("0.000000", CultureInfo.InvariantCulture) : "";
            if (_freq.Text.Trim() != freqShown)
            {
                if (_freq.Text.Trim().Length == 0) q.FreqMhz = 0;
                else if (double.TryParse(_freq.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double f) && f > 0) q.FreqMhz = f;
                else error = error ?? "Frequency is not a number in MHz.";
            }

            q.RstSent = Blank(_rstSent.Text);
            q.RstRcvd = Blank(_rstRcvd.Text);
            q.Name = Blank(_name.Text);
            q.Qth = Blank(_qth.Text);
            q.State = Blank(_state.Text.ToUpperInvariant());
            SetExtra(q, "CNTY", _county.Text);
            q.Country = Blank(_country.Text);
            q.Grid = Blank(_grid.Text.ToUpperInvariant());
            q.Ota = q.Ota ?? new NexusOta();
            q.Ota.Iota = Blank(_iota.Text.ToUpperInvariant());
            q.Ota.TheirProgram = Blank(_theirProgram.Text.ToUpperInvariant());
            q.Ota.TheirRef = Blank(_theirRef.Text.ToUpperInvariant());

            string powerShown = _orig.TxPower.HasValue ? _orig.TxPower.Value.ToString("0.###", CultureInfo.InvariantCulture) : "";
            if (_power.Text.Trim() != powerShown)
            {
                if (_power.Text.Trim().Length == 0) q.TxPower = null;
                else if (double.TryParse(_power.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double p) && p >= 0) q.TxPower = p;
                else error = error ?? "Power is not a number of watts.";
            }
            q.MyRig = Blank(_myRig.Text);
            q.MyGrid = Blank(_myGrid.Text.ToUpperInvariant());
            q.Ota.MyProgram = Blank(_myProgram.Text.ToUpperInvariant());
            q.Ota.MyRef = Blank(_myRef.Text.ToUpperInvariant());
            q.Comment = Blank(_comment.Text);
            q.Notes = Blank(_notes.Text);

            var uploadChanges = new List<(string Service, bool Sent)>();
            if (_allowProtected.Checked)
            {
                if (_dxcc.Text.Trim().Length == 0) q.Dxcc = null;
                else if (uint.TryParse(_dxcc.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint d)) q.Dxcc = d;
                else error = error ?? "DXCC is not an entity number.";
                SetExtra(q, "CQZ", _cqz.Text);
                SetExtra(q, "ITUZ", _ituz.Text);
                q.StationCallsign = Blank(_stationCall.Text.ToUpperInvariant());
                q.Operator = Blank(_operator.Text.ToUpperInvariant());
                foreach (var (service, _) in UploadServices)
                {
                    bool sent = _uploads[service].Checked;
                    if (sent == UploadSent(_orig, service)) continue;
                    if (service == "hrdlog")   // kept on the contact itself (Nexus has no HRDLog state)
                        SetExtra(q, NexusMigration.HrdlogUploadedTag, sent ? DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) : "");
                    else
                        uploadChanges.Add((service, sent));
                }
            }

            if (error != null) { _status.Text = error; return; }
            string failed = _onSave?.Invoke(q, uploadChanges);
            if (failed != null) { _status.Text = failed; return; }   // every field stays as typed
            Close();
        }

        private static string Pad6(string t) { t = (t ?? "").Trim(); return t.Length == 4 ? t + "00" : t; }
        private static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static void SetExtra(NexusQso q, string tag, string value)
        {
            q.Extra.RemoveAll(kv => kv.Count == 2 && string.Equals(kv[0], tag, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(value)) q.Extra.Add(new List<string> { tag, value.Trim() });
        }
    }
}
