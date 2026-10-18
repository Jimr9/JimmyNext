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
    //   - Opens READ ONLY (operator, 2026-10-01): every field can be read and tabbed through,
    //     nothing can change until "Allow editing" is checked -- off every time this opens.
    //     Saving asks "Save these changes?" naming the fields that changed.
    //   - Editable: the contact, the station worked, my station, comment and notes.
    //   - Protected (DXCC, CQ/ITU zones, station callsign, operator, upload status, the TQSL
    //     station location recorded with the contact, and every other field the contact
    //     carries): shown read only until "Allow editing protected fields" is checked -- off
    //     every time this opens, because a wrong value there quietly changes awards or uploads.
    //     Since 2026-10-12 that is ALL of them (operator, 2026-10-09): nothing a contact holds
    //     is stranded read only except what a service owns.
    //   - The TQSL location has a "Choose TQSL Location" action, so a contact logged while the
    //     location could not be recorded is repaired by picking the location it was made from
    //     -- its details are shown first and filled in from TQSL, never typed by hand. Picking
    //     one changes nothing until Save and its question; it never marks the contact uploaded.
    //   - Confirmations (LoTW, QRZ, eQSL, card), credit, what a service reported, and the
    //     record's own identity: ALWAYS read only. The services own them, and Nexus keeps them
    //     through any edit (a sync would put back what they know).
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
        private CheckBox _allowProtected, _allowEdit;
        private readonly List<Control> _normalFields = new List<Control>();
        private readonly Dictionary<Control, string> _shown = new Dictionary<Control, string>();
        private readonly HashSet<ComboBox> _lockedCombos = new HashSet<ComboBox>();
        private TextBox _dxcc, _cqz, _ituz, _stationCall, _operator;
        private TextBox _tqslLocation, _tqslDetails, _tqslStatus, _otherFields;
        private Button _chooseTqsl;
        // Tests hand in TQSL's locations; null = read TQSL's own file.
        internal static Func<Dictionary<string, TqslStationData.Location>> TestLocations;
        private static Dictionary<string, TqslStationData.Location> Locations(out string error)
        {
            error = null;
            return TestLocations != null ? TestLocations() : TqslStationData.Load(out error);
        }
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
            Text = "Contact " + q.Call;
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

            _allowEdit = new CheckBox
            {
                Text = "Allow editing", AccessibleName = "Allow editing", AutoSize = true, Checked = false,
                TabIndex = _groupTab++, Margin = new Padding(3, 3, 3, 6),
            };
            _allowEdit.CheckedChanged += (s, e) => ApplyEditable();
            _flow.Controls.Add(_allowEdit);

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
            // The country by the contact's DXCC number (Nexus 1.19), as the lists show it and an
            // export or upload writes it: a logged spelling variant reads as the standard name (and
            // a save stores it). A logged text that carries location detail -- a WAE area, another
            // entity, "NON-DXCC" -- stays as logged, with the number's name read-only beside it;
            // changing it is the operator's call (or the country check's, once approved).
            bool standard = q.CountrySpellingOnly && !string.IsNullOrEmpty(q.CountryByDxcc);
            _country = Box(t, "Country", standard ? q.CountryByDxcc : q.Country);
            if (!standard && !string.IsNullOrEmpty(q.CountryByDxcc)
                && !string.Equals((q.Country ?? "").Trim(), q.CountryByDxcc, StringComparison.Ordinal))
                Box(t, "Country by DXCC", q.CountryByDxcc, readOnly: true);
            _grid = Box(t, "Grid", q.Grid, upper: true);
            _iota = Box(t, "IOTA", q.Ota?.Iota, upper: true);
            _theirProgram = Combo(t, "Their park program", q.Ota?.TheirProgram, Programs);
            _theirRef = Box(t, "Their park reference", q.Ota?.TheirRef, upper: true);

            t = Group("My station");
            _power = Box(t, "Power watts", q.TxPower.HasValue ? q.TxPower.Value.ToString("0.###", CultureInfo.InvariantCulture) : "");
            _myRig = Box(t, "My rig", q.MyRig);
            _myGrid = Box(t, "My grid", q.MyGrid, upper: true);
            _myProgram = Combo(t, "My park program", q.Ota?.MyProgram, Programs);
            _myRef = Box(t, "My park reference", q.Ota?.MyRef, upper: true);

            t = Group("Remarks");
            // Nexus sends COMMENT with every upload ("shared on the QSL"); NOTES never leave this computer.
            _comment = Box(t, "Comment, shared", q.Comment, width: 420, span: 3);
            _notes = Box(t, "Notes, private", q.Notes, width: 420, span: 3, lines: 3);

            t = Group("Protected");
            _allowProtected = new CheckBox { Text = "Allow editing protected fields", AutoSize = true, Checked = false };
            _allowProtected.CheckedChanged += (s, e) => ApplyEditable();
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

            // The TQSL station location recorded when the contact was logged, and whether TQSL
            // still has it as it was -- the same judgement the LoTW upload makes.
            _tqslStatus = Box(t, "TQSL location status", "", width: 420, span: 3, readOnly: true, lines: 2);
            _tqslLocation = Box(t, "TQSL location", q.ExtraValue(TqslLocationTag));
            _chooseTqsl = new Button { Text = "Choose TQSL Location...", AccessibleName = "Choose TQSL location", AutoSize = true };
            _chooseTqsl.Click += (s, e) => ChooseTqslLocation();
            Add(t, _chooseTqsl, 2);
            _tqslDetails = Box(t, "TQSL location details", q.ExtraValue(TqslDetailsTag), width: 420, span: 3);
            _tqslLocation.TextChanged += (s, e) => ShowTqslStatus();
            _tqslDetails.TextChanged += (s, e) => ShowTqslStatus();
            ShowTqslStatus();
            string other = OtherFieldsText(q);
            _otherFields = Box(t, "Other fields, one per line as NAME=value", other, width: 420, span: 3,
                lines: Math.Min(8, Math.Max(2, other.Count(c => c == '\n') + 2)));
            _otherFields.AcceptsReturn = true;

            t = Group("Read only");
            Box(t, "Confirmations", Confirmations(q), width: 420, span: 3, readOnly: true);
            string serviceText = ServiceFieldsText(q);
            Box(t, "Service and record fields", serviceText.Length == 0 ? "none" : serviceText, width: 420, span: 3, readOnly: true,
                lines: Math.Min(8, Math.Max(1, serviceText.Count(c => c == '\n') + 1)));

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

            _normalFields.AddRange(new Control[]
            {
                _call, _band, _mode, _freq, _date, _timeOn, _timeOff, _rstSent, _rstRcvd,
                _name, _qth, _state, _county, _country, _grid, _iota, _theirProgram, _theirRef,
                _power, _myRig, _myGrid, _myProgram, _myRef, _comment, _notes,
            });
            foreach (var cb in new[] { _mode, _theirProgram, _myProgram }) LockWhenReadOnly(cb);
            foreach (var c in _normalFields.Concat(ProtectedBoxes())) _shown[c] = c.Text;
            ApplyEditable();
            ClientSize = new Size(Math.Min(_flow.PreferredSize.Width + 30, 760),
                                  Math.Min(_flow.PreferredSize.Height + 12, Screen.FromControl(this).WorkingArea.Height - 80));
            AppLook.Attach(this);   // the shared look: theme, text size, list colours
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
        // Normal fields follow "Allow editing"; protected ones need "Allow editing protected
        // fields" as well, which itself only works once editing is allowed.
        private void ApplyEditable()
        {
            bool edit = _allowEdit.Checked;
            if (!edit && _allowProtected.Checked) _allowProtected.Checked = false;
            _allowProtected.AutoCheck = edit;
            bool prot = edit && _allowProtected.Checked;
            foreach (var c in _normalFields)
            {
                if (c is TextBox tb) tb.ReadOnly = !edit;
                else if (c is ComboBox cb) { if (edit) _lockedCombos.Remove(cb); else _lockedCombos.Add(cb); }
            }
            foreach (var tb in ProtectedBoxes()) tb.ReadOnly = !prot;
            foreach (var cb in _uploads.Values) cb.AutoCheck = prot;
            // Never disabled (a focused button that switches off drops the keyboard focus): it
            // says why when protected editing is off.
        }

        private TextBox[] ProtectedBoxes() => new[] { _dxcc, _cqz, _ituz, _stationCall, _operator, _tqslLocation, _tqslDetails, _otherFields };

        // ── The TQSL station location recorded with the contact ─────────────────────────────

        internal const string TqslLocationTag = "APP_JIMMY_TQSL_LOCATION", TqslDetailsTag = "APP_JIMMY_TQSL_LOC_FP";

        private void ShowTqslStatus()
        {
            var all = Locations(out _);
            var judged = TqslStationData.Judge(_tqslLocation.Text, _tqslDetails.Text, _stationCall?.Text ?? _orig.StationCallsign, all);
            string text = TqslStationData.StatusSentence(judged, _tqslLocation.Text);
            if (_tqslStatus.Text != text) _tqslStatus.Text = text;
        }

        // What choosing `loc` for this contact would mean, and what does not fit: the location's
        // details, then any difference from the contact worth a second look. `blocked` = it
        // cannot be chosen at all (another station's location).
        internal static string TqslChoiceReview(TqslStationData.Location loc, string stationCall, string myGrid, string myState, out bool blocked)
        {
            blocked = false;
            var lines = new List<string> { $"Location '{loc.Name}': {TqslStationData.Describe(loc)}." };
            string call = (stationCall ?? "").Trim();
            if (loc.Call.Length > 0 && !string.Equals(loc.Call, call, StringComparison.OrdinalIgnoreCase))
            {
                blocked = true;
                lines.Add($"It cannot be used: it is for {loc.Call}, and this contact's station callsign is {(call.Length == 0 ? "not set" : call)}.");
                return string.Join(Environment.NewLine, lines);
            }
            string grid = (myGrid ?? "").Trim();
            int n = Math.Min(grid.Length, loc.Grid.Length);
            if (n >= 4 && !string.Equals(grid.Substring(0, n), loc.Grid.Substring(0, n), StringComparison.OrdinalIgnoreCase))
                lines.Add($"Check: this contact's my grid is {grid}, the location's is {loc.Grid}.");
            string state = (myState ?? "").Trim();
            if (state.Length > 0 && loc.State.Length > 0 && !string.Equals(state, loc.State, StringComparison.OrdinalIgnoreCase))
                lines.Add($"Check: this contact's my state is {state}, the location's is {loc.State}.");
            lines.Add("Choosing it says this contact was made from this location. Nothing is saved until you press Save.");
            return string.Join(Environment.NewLine, lines);
        }

        private void ChooseTqslLocation()
        {
            if (!(_allowEdit.Checked && _allowProtected.Checked))
            {
                _status.Text = "Check Allow editing and Allow editing protected fields first.";
                return;
            }
            var all = Locations(out string error);
            if (all == null || all.Count == 0)
            {
                MessageBox.Show(this, all == null ? error : "TQSL has no station locations on this computer. Create one in TQSL first.",
                    "Choose TQSL Location", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var names = all.Values.Select(l => l.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            using (var dlg = new Form
            {
                Text = "Choose TQSL Location", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, ClientSize = new Size(500, 230), Font = Font,
            })
            {
                var list = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(10, 30), Width = 480, AccessibleName = "TQSL location", TabIndex = 0 };
                foreach (string name in names) list.Items.Add(name);
                var review = new TextBox
                {
                    ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Location = new Point(10, 62), Size = new Size(480, 120),
                    AccessibleName = "About this location", TabIndex = 1,
                };
                var ok = new Button { Text = "Use This Location", AccessibleName = "Use this location", Location = new Point(250, 194), Width = 150, TabIndex = 2 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(410, 194), Width = 80, TabIndex = 3 };
                bool blocked = true;
                void Show()
                {
                    var loc = all[names[Math.Max(0, list.SelectedIndex)]];
                    review.Text = TqslChoiceReview(loc, _stationCall.Text, _myGrid.Text, OwnStateOf(_orig), out blocked);
                }
                list.SelectedIndexChanged += (s, e) => Show();
                int now = names.FindIndex(x => string.Equals(x, _tqslLocation.Text.Trim(), StringComparison.OrdinalIgnoreCase));
                list.SelectedIndex = now >= 0 ? now : 0;
                Show();
                ok.Click += (s, e) =>
                {
                    if (blocked) { MessageBox.Show(dlg, "This location is for another callsign and cannot be used for this contact.", dlg.Text, MessageBoxButtons.OK, MessageBoxIcon.Information); list.Focus(); return; }
                    dlg.DialogResult = DialogResult.OK;
                };
                dlg.Controls.Add(new Label { Text = "The TQSL station location this contact was made from:", Location = new Point(10, 10), AutoSize = true });
                dlg.Controls.AddRange(new Control[] { list, review, ok, cancel });
                dlg.CancelButton = cancel;
                AppLook.Attach(dlg);   // the shared look
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var chosen = all[names[Math.Max(0, list.SelectedIndex)]];
                _tqslLocation.Text = chosen.Name;
                _tqslDetails.Text = chosen.Fingerprint;
                _status.Text = $"TQSL location '{chosen.Name}' chosen. Press Save to keep it.";
            }
        }

        private static string OwnStateOf(NexusQso q) => q.ExtraValue("MY_STATE") ?? "";

        // The TQSL boxes as they must be to save: null, or what is wrong. A named location must
        // be one TQSL has, for this contact's station callsign, with ITS details -- so the
        // details are never left not matching what is named.
        internal static string TqslProblem(string location, string details, string stationCall, Dictionary<string, TqslStationData.Location> all, string loadError)
        {
            string rec = (location ?? "").Trim(), fp = (details ?? "").Trim();
            if (rec.Length == 0) return fp.Length == 0 ? null : "TQSL location details are set, but no TQSL location is named. Use Choose TQSL Location, or clear the details.";
            if (rec == Controller.TqslNoLocation) return null;
            if (all == null) return "TQSL's station locations cannot be read, so the location cannot be checked: " + (loadError ?? "unknown reason");
            if (!all.TryGetValue(rec, out var loc)) return $"TQSL has no station location named '{rec}'. Use Choose TQSL Location.";
            if (loc.Call.Length > 0 && !string.Equals(loc.Call, (stationCall ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                return $"TQSL location '{rec}' is for {loc.Call}, not this contact's station callsign.";
            if (!string.Equals(loc.Fingerprint, fp, StringComparison.Ordinal))
                return $"The TQSL location details are not those of location '{rec}'. Use Choose TQSL Location to fill them in.";
            return null;
        }

        // A ComboBox has no ReadOnly: while locked it keeps focus and its value is read, but keys,
        // the wheel and the drop-down cannot change it (Tab, arrows within the text, Home/End still move).
        private void LockWhenReadOnly(ComboBox cb)
        {
            cb.KeyDown += (s, e) =>
            {
                if (!_lockedCombos.Contains(cb)) return;
                if (e.KeyCode == Keys.Tab || e.KeyCode == Keys.Left || e.KeyCode == Keys.Right
                    || e.KeyCode == Keys.Home || e.KeyCode == Keys.End || e.KeyCode == Keys.Escape || e.KeyCode == Keys.Enter) return;
                e.SuppressKeyPress = true;
                e.Handled = true;
            };
            cb.KeyPress += (s, e) => { if (_lockedCombos.Contains(cb)) e.Handled = true; };
            cb.DropDown += (s, e) => { if (_lockedCombos.Contains(cb)) BeginInvoke(new Action(() => cb.DroppedDown = false)); };
            cb.MouseWheel += (s, e) => { if (_lockedCombos.Contains(cb) && e is HandledMouseEventArgs h) h.Handled = true; };
        }

        // The fields whose shown value was changed, by name.
        private List<string> ChangedFieldNames()
        {
            var names = _shown.Where(kv => kv.Key.Text != kv.Value).Select(kv => kv.Key.AccessibleName).ToList();
            foreach (var (service, label) in UploadServices)
                if (_uploads[service].Checked != UploadSent(_orig, service)) names.Add(label);
            return names;
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

        // What a service or the record itself owns: confirmations and their dates, what LoTW,
        // QRZ and eQSL reported, upload stamps (changed with the Uploaded check boxes, never as
        // raw text), and the record's identity. Always read only.
        internal static bool IsServiceOrRecordTag(string tag)
        {
            string t = (tag ?? "").Trim().ToUpperInvariant();
            if (t == "APP_NEXUS_ID" || t == "APP_JIMMY_REQ_ID" || t == "APP_JIMMY_SOURCE_QSO_ID" || t == "APP_JIMMY_IMPORTED_AT"
                || t == "APP_JIMMY_MODIFIED_AT" || t == "APP_JIMMY_CONTEST_SESSION") return true;
            if (t.StartsWith("APP_JIMMY_LOTW_") || t.StartsWith("APP_JIMMY_QRZ_") || t.StartsWith("APP_JIMMY_EQSL_")) return true;
            if (t.StartsWith("QSL_RCVD") || t == "QSLRDATE" || t.StartsWith("LOTW_QSL") || t.StartsWith("EQSL_QSL") || t.StartsWith("QRZCOM_QSO_")
                || t.StartsWith("CLUBLOG_QSO_UPLOAD") || t.StartsWith("HRDLOG_QSO_UPLOAD") || t.StartsWith("CREDIT_") || t == "QSL_SENT" || t == "QSLSDATE"
                || t == "QSL_SENT_VIA") return true;
            return false;
        }

        // Shown in a box of their own, so never in "Other fields".
        private static bool HasOwnBox(string tag) =>
            ShownTags.Contains(tag) || string.Equals(tag, TqslLocationTag, StringComparison.OrdinalIgnoreCase) || string.Equals(tag, TqslDetailsTag, StringComparison.OrdinalIgnoreCase);

        // Every other field the contact carries, one per line as NAME=value -- the ADIF name, so
        // a line can be changed, added or removed and read back exactly. Protected.
        internal static string OtherFieldsText(NexusQso q)
        {
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(q.PropMode)) lines.Add("PROP_MODE=" + q.PropMode);
            if (!string.IsNullOrEmpty(q.SatName)) lines.Add("SAT_NAME=" + q.SatName);
            if (q.FreqRxMhz.HasValue) lines.Add("FREQ_RX=" + q.FreqRxMhz.Value.ToString("0.000000", CultureInfo.InvariantCulture));
            foreach (var kv in q.Extra)
            {
                if (kv.Count != 2 || HasOwnBox(kv[0]) || IsServiceOrRecordTag(kv[0])) continue;
                lines.Add(kv[0].ToUpperInvariant() + "=" + (kv[1] ?? "").Replace("\r", " ").Replace("\n", " "));
            }
            return string.Join(Environment.NewLine, lines);
        }

        internal static string ServiceFieldsText(NexusQso q)
        {
            var lines = new List<string>();
            foreach (var kv in q.Extra)
                if (kv.Count == 2 && !HasOwnBox(kv[0]) && IsServiceOrRecordTag(kv[0])) lines.Add(FieldName(kv[0]) + ": " + kv[1]);
            return string.Join(Environment.NewLine, lines);
        }

        // "Other fields" as typed, put on the contact: every line NAME=value, a name once, never
        // one that has its own box or that a service owns. Returns null, or what is wrong (the
        // contact is then not changed).
        internal static string ApplyOtherFields(NexusQso q, string text)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) return $"Other fields: '{line}' is not NAME=value.";
                string tag = line.Substring(0, eq).Trim().ToUpperInvariant(), value = line.Substring(eq + 1).Trim();
                if (tag.Length > 64 || !tag.All(c => c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_')) return $"Other fields: '{tag}' is not a field name (letters, digits and underscore).";
                if (HasOwnBox(tag)) return $"Other fields: {tag} has its own box above; change it there.";
                if (IsServiceOrRecordTag(tag)) return $"Other fields: {tag} belongs to a service or the record and cannot be changed here.";
                if (!seen.Add(tag)) return $"Other fields: {tag} is there twice.";
                if (value.Length == 0) continue;   // an empty value removes the field
                pairs.Add(new KeyValuePair<string, string>(tag, value));
            }
            double? freqRx = null;
            var rx = pairs.FirstOrDefault(kv => kv.Key == "FREQ_RX");
            if (rx.Key != null)
            {
                if (!double.TryParse(rx.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double f) || f <= 0) return "Other fields: FREQ_RX is not a number in MHz.";
                freqRx = f;
            }
            q.PropMode = pairs.Where(kv => kv.Key == "PROP_MODE").Select(kv => kv.Value).FirstOrDefault();
            q.SatName = pairs.Where(kv => kv.Key == "SAT_NAME").Select(kv => kv.Value).FirstOrDefault();
            q.FreqRxMhz = freqRx;
            q.Extra.RemoveAll(kv => kv.Count == 2 && !HasOwnBox(kv[0]) && !IsServiceOrRecordTag(kv[0]));
            foreach (var kv in pairs)
                if (kv.Key != "PROP_MODE" && kv.Key != "SAT_NAME" && kv.Key != "FREQ_RX") q.Extra.Add(new List<string> { kv.Key, kv.Value });
            return null;
        }

        // Plain names for the standard ADIF fields; anything else keeps its ADIF tag.
        private static readonly Dictionary<string, string> FieldNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CONT"] = "Continent", ["PFX"] = "Prefix", ["PROP_MODE"] = "Propagation mode", ["SAT_NAME"] = "Satellite",
            ["SAT_MODE"] = "Satellite mode", ["FREQ_RX"] = "Receive frequency", ["BAND_RX"] = "Receive band",
            ["SUBMODE"] = "Submode", ["SIG"] = "Special activity", ["SIG_INFO"] = "Special activity reference",
            ["MY_SIG"] = "My special activity", ["MY_SIG_INFO"] = "My special activity reference",
            ["WWFF_REF"] = "WWFF reference", ["MY_WWFF_REF"] = "My WWFF reference", ["SOTA_REF"] = "SOTA reference",
            ["MY_SOTA_REF"] = "My SOTA reference", ["POTA_REF"] = "POTA reference", ["MY_POTA_REF"] = "My POTA reference",
            ["DARC_DOK"] = "DOK", ["STX_STRING"] = "Exchange sent", ["SRX_STRING"] = "Exchange received",
            ["STX"] = "Serial sent", ["SRX"] = "Serial received", ["CONTEST_ID"] = "Contest", ["CLASS"] = "Class",
            ["ARRL_SECT"] = "ARRL section", ["AGE"] = "Age", ["DISTANCE"] = "Distance", ["RX_PWR"] = "Their power",
            ["ANT_AZ"] = "Antenna azimuth", ["ANT_EL"] = "Antenna elevation", ["MY_ANTENNA"] = "My antenna",
            ["MY_CITY"] = "My city", ["MY_COUNTY"] = "My county", ["MY_STATE"] = "My state", ["MY_CNTY"] = "My county",
            ["MY_COUNTRY"] = "My country", ["MY_DXCC"] = "My DXCC", ["MY_CQ_ZONE"] = "My CQ zone", ["MY_ITU_ZONE"] = "My ITU zone",
            ["MY_NAME"] = "My name", ["EMAIL"] = "Email", ["WEB"] = "Web", ["ADDRESS"] = "Address",
            ["QSL_VIA"] = "QSL via", ["QSL_SENT"] = "QSL sent", ["QSL_RCVD"] = "QSL received",
            ["QSL_SENT_VIA"] = "QSL sent via", ["QSLSDATE"] = "QSL sent date", ["QSLRDATE"] = "QSL received date",
            ["LOTW_QSL_SENT"] = "LoTW sent", ["LOTW_QSLSDATE"] = "LoTW sent date", ["LOTW_QSLRDATE"] = "LoTW received date",
            ["EQSL_QSL_SENT"] = "eQSL sent", ["EQSL_QSLSDATE"] = "eQSL sent date", ["EQSL_QSLRDATE"] = "eQSL received date",
            ["QRZCOM_QSO_UPLOAD_STATUS"] = "QRZ upload status", ["QRZCOM_QSO_UPLOAD_DATE"] = "QRZ upload date",
            ["CLUBLOG_QSO_UPLOAD_STATUS"] = "Club Log upload status", ["CLUBLOG_QSO_UPLOAD_DATE"] = "Club Log upload date",
            ["HRDLOG_QSO_UPLOAD_STATUS"] = "HRDLog upload status", ["GRIDSQUARE_EXT"] = "Grid extension",
            ["VUCC_GRIDS"] = "VUCC grids", ["MY_VUCC_GRIDS"] = "My VUCC grids", ["APP_NEXUS_ID"] = "Nexus record ID",
        };
        private static string FieldName(string tag) => FieldNames.TryGetValue(tag, out var n) ? n : tag;

        // ── Save ─────────────────────────────────────────────────────────────────────────────

        private void Save_Click(object sender, EventArgs e)
        {
            if (!_allowEdit.Checked) { _status.Text = "Check Allow editing to make changes."; return; }
            var changedNames = ChangedFieldNames();
            if (changedNames.Count == 0) { _status.Text = "No changes to save."; return; }
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
                // The TQSL location: checked against TQSL only when it was changed here, so a
                // contact that is merely held can still have its other fields saved.
                if (_tqslLocation.Text != _shown[_tqslLocation] || _tqslDetails.Text != _shown[_tqslDetails])
                {
                    var all = Locations(out string loadError);
                    error = error ?? TqslProblem(_tqslLocation.Text, _tqslDetails.Text, _stationCall.Text, all, loadError);
                    SetExtra(q, TqslLocationTag, _tqslLocation.Text);
                    SetExtra(q, TqslDetailsTag, _tqslDetails.Text);
                }
                if (_otherFields.Text != _shown[_otherFields])
                    error = error ?? ApplyOtherFields(q, _otherFields.Text);
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
            if (MessageBox.Show(this, $"Save these changes to {_orig.Call}?\n\n{string.Join(", ", changedNames)}", "Save Changes",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                _status.Text = "Not saved.";
                return;
            }
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
