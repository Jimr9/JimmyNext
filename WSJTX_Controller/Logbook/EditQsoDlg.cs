using System;
using System.Drawing;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Local-only edit dialog for a single QSO row on the Logbook window's Edit Log tab.
    // Never contacts QRZ/Club Log/LoTW -- only ever writes to Jimmy's own logbook.db
    // (see LogbookDb.UpdateQso). Field set is deliberately smaller than every column
    // the database stores: this exposes the fields a user would realistically want to
    // review or correct (callsign, band/mode/date/time, state/country/grid, name,
    // RST, a free-form comment), not the award-engine bookkeeping fields (SIG, DARC_DOK,
    // WPX prefix, etc.), which are left untouched by an edit.
    internal class EditQsoDlg : Form
    {
        private readonly TextBox _callTb, _bandTb, _dateTb, _timeOnTb, _timeOffTb;
        private readonly TextBox _stateTb, _countryTb, _gridTb, _nameTb, _rstSentTb, _rstRcvdTb;
        private readonly TextBox _commentTb;
        private readonly TextBox _statusTb;
        private readonly ComboBox _modeCb;

        // Common modes offered in the Mode dropdown -- editable, so anything not listed
        // here (a less common digital mode, a contest-specific label, etc.) can still be
        // typed in directly.
        private static readonly string[] CommonModes =
        {
            "FT8", "FT4", "SSB", "CW", "RTTY", "PSK31", "FM", "AM", "WSPR", "JT65", "JT9", "MSK144", "FST4",
        };
        private readonly Button  _submitButton, _closeButton;
        private readonly Func<string, LookupRecord> _lookupCallsign;
        private readonly Func<QsoRecord, string> _onSubmit;
        private readonly Action _onLogged;
        private readonly bool _isNewEntry;

        // Also used for "Add New QSO" (LogbookWindow's AddQsoBtn_Click) with a mostly-blank
        // QsoRecord and title -- same fields either way, so one dialog covers both.
        //
        // lookupCallsign is Jimmy's offline-only station lookup (LookupManager.Build) -- safe
        // to call synchronously, never throws, never returns null; fields it can't answer are
        // just left blank, so a not-found callsign silently does nothing (see CallTb_Leave).
        //
        // onSubmit performs the actual database write (Upsert for a new entry, UpdateQso for
        // an edit) and returns null on success or a user-facing error string on failure --
        // the dialog never touches the database itself.
        //
        // isNewEntry switches Submit's behavior: for a brand-new contact it saves, plays
        // onLogged, clears the per-QSO fields, refreshes Date/Time on to "now", and returns
        // focus to Callsign so a contest/pileup operator can just keep typing -- Band/Mode
        // are deliberately left alone since the radio setup doesn't change contact-to-contact.
        // For an existing record (isNewEntry=false) Submit just saves and closes, same as the
        // dialog's old OK behavior. Either way, a failed submit leaves every field exactly as
        // typed and does not move focus, so a rejected entry (e.g. a duplicate) is never
        // silently lost.
        public EditQsoDlg(QsoRecord q, string title = "Edit QSO",
            Func<string, LookupRecord> lookupCallsign = null, bool isNewEntry = false,
            Func<QsoRecord, string> onSubmit = null, Action onLogged = null)
        {
            _lookupCallsign = lookupCallsign;
            _isNewEntry     = isNewEntry;
            _onSubmit       = onSubmit;
            _onLogged       = onLogged;
            Text            = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            ShowInTaskbar   = false;
            StartPosition   = FormStartPosition.CenterParent;
            ClientSize      = new Size(460, 300);
            KeyPreview      = true;

            int yA = 12, yB = 12, tab = 1;
            _callTb    = AddField(this, "Callsign:",    12, ref yA, 90, 110, out _, ref tab, q.Callsign, upper: true);
            _callTb.Leave += CallTb_Leave;
            _bandTb    = AddField(this, "Band:",        12, ref yA, 90, 110, out _, ref tab, q.Band);
            _modeCb    = AddModeField(this, 12, ref yA, 90, 110, ref tab, q.Mode);
            _dateTb    = AddField(this, "Date (YYYYMMDD):", 12, ref yA, 90, 110, out _, ref tab, q.QsoDate);
            _timeOnTb  = AddField(this, "Time on (HHMM):",  12, ref yA, 90, 110, out _, ref tab, q.TimeOn);
            _timeOffTb = AddField(this, "Time off (HHMM):", 12, ref yA, 90, 110, out _, ref tab, q.TimeOff);

            _stateTb   = AddField(this, "State:",       242, ref yB, 70, 130, out _, ref tab, q.State, upper: true);
            _countryTb = AddField(this, "Country:",     242, ref yB, 70, 130, out _, ref tab, q.Country);
            _gridTb    = AddField(this, "Grid:",        242, ref yB, 70, 130, out _, ref tab, q.Grid, upper: true);
            _nameTb    = AddField(this, "Name:",        242, ref yB, 70, 130, out _, ref tab, q.Name);
            _rstSentTb = AddField(this, "RST sent:",    242, ref yB, 70, 130, out _, ref tab, q.RstSent);
            _rstRcvdTb = AddField(this, "RST rcvd:",    242, ref yB, 70, 130, out _, ref tab, q.RstRcvd);

            int yC = Math.Max(yA, yB) + 6;
            var commentLbl = new Label { Text = "Comment:", Location = new Point(12, yC + 2), AutoSize = true };
            Controls.Add(commentLbl);
            _commentTb = new TextBox
            {
                Location       = new Point(90, yC),
                Size           = new Size(358, 20),
                TabIndex       = tab++,
                AccessibleName = "Comment",
                Text           = q.Comment ?? "",
            };
            Controls.Add(_commentTb);
            yC += 30;

            _submitButton = new Button
            {
                Text     = "Submit",
                Location = new Point(282, yC),
                Size     = new Size(80, 26),
                TabIndex = tab++,
            };
            _submitButton.Click += SubmitButton_Click;

            _closeButton = new Button
            {
                Text     = "Close",
                Location = new Point(368, yC),
                Size     = new Size(80, 26),
                TabIndex = tab++,
            };
            _closeButton.Click += (s, e) => Close();

            Controls.Add(_submitButton);
            Controls.Add(_closeButton);
            AcceptButton = _submitButton;
            CancelButton = _closeButton;
            yC += 34;

            // Read-only status line -- Tab-focusable/NVDA-readable on demand, same convention
            // as LogbookWindow's own status bar. Deliberately not forced into focus on every
            // submit (see the onSubmit success/failure feedback in SubmitButton_Click): a
            // contest operator's focus must stay on Callsign, and the Logged sound / exclamation
            // beep already give an audible cue without a focus jump.
            _statusTb = new TextBox
            {
                Location       = new Point(12, yC),
                Size           = new Size(436, 20),
                ReadOnly       = true,
                BorderStyle    = BorderStyle.None,
                BackColor      = SystemColors.Control,
                TabStop        = true,
                TabIndex       = tab++,
                AccessibleName = "Status",
            };
            Controls.Add(_statusTb);
            yC += 26;

            ClientSize = new Size(460, yC);
        }

        private static TextBox AddField(Form form, string label, int x, ref int y, int labelWidth, int boxWidth,
            out Label lbl, ref int tab, string value, bool upper = false)
        {
            lbl = new Label
            {
                Text     = label,
                Location = new Point(x, y + 2),
                Size     = new Size(labelWidth, 16),
                AutoSize = false,
            };
            form.Controls.Add(lbl);

            var tb = new TextBox
            {
                Location       = new Point(x + labelWidth, y),
                Size           = new Size(boxWidth, 20),
                TabIndex       = tab++,
                AccessibleName = label.TrimEnd(':', ' '),
                Text           = value ?? "",
                CharacterCasing = upper ? CharacterCasing.Upper : CharacterCasing.Normal,
            };
            form.Controls.Add(tb);
            y += 26;
            return tb;
        }

        // Editable combo box: DropDown (not DropDownList) so a common mode can be picked
        // from the list, or anything else typed in freely -- ComboBox has no CharacterCasing
        // property (unlike TextBox), so uppercasing a typed value happens at submit time
        // instead (see SubmitButton_Click), not live as the user types.
        private static ComboBox AddModeField(Form form, int x, ref int y, int labelWidth, int boxWidth, ref int tab, string value)
        {
            var lbl = new Label
            {
                Text     = "Mode:",
                Location = new Point(x, y + 2),
                Size     = new Size(labelWidth, 16),
                AutoSize = false,
            };
            form.Controls.Add(lbl);

            var cb = new ComboBox
            {
                Location       = new Point(x + labelWidth, y),
                Size           = new Size(boxWidth, 20),
                TabIndex       = tab++,
                AccessibleName = "Mode",
                DropDownStyle  = ComboBoxStyle.DropDown,
                Text           = value ?? "",
            };
            cb.Items.AddRange(CommonModes);
            form.Controls.Add(cb);
            y += 26;
            return cb;
        }

        // Offline-only convenience: fills State/Country/Grid/Name from Jimmy's cached lookup
        // data when the callsign field loses focus, but only into fields still blank -- never
        // overwrites anything the user already typed. Does nothing if lookupCallsign wasn't
        // supplied, the callsign is empty, or nothing is known about it.
        private void CallTb_Leave(object sender, EventArgs e)
        {
            if (_lookupCallsign == null) return;
            string call = _callTb.Text.Trim();
            if (call.Length == 0) return;

            var rec = _lookupCallsign(call);
            if (rec == null) return;

            if (_stateTb.Text.Trim().Length == 0 && !string.IsNullOrEmpty(rec.State))
                _stateTb.Text = rec.State;
            if (_countryTb.Text.Trim().Length == 0 && !string.IsNullOrEmpty(rec.Country))
                _countryTb.Text = rec.Country;
            if (_gridTb.Text.Trim().Length == 0 && !string.IsNullOrEmpty(rec.Grid))
                _gridTb.Text = rec.Grid;
            if (_nameTb.Text.Trim().Length == 0 && !string.IsNullOrEmpty(rec.Name))
                _nameTb.Text = rec.Name;
        }

        private void SubmitButton_Click(object sender, EventArgs e)
        {
            string callsign = _callTb.Text.Trim();
            if (callsign.Length == 0)
            {
                _statusTb.Text = "Callsign cannot be blank.";
                _callTb.Focus();
                return;
            }

            // New entries only: a QSO actively being logged just ended, so "now" is a
            // reasonable Time off default if left blank. Never applied when editing an
            // existing (possibly historical) QSO.
            string timeOff = _timeOffTb.Text.Trim();
            if (_isNewEntry && timeOff.Length == 0)
                timeOff = DateTime.UtcNow.ToString("HHmm");

            var record = new QsoRecord
            {
                Callsign = callsign,
                Band     = _bandTb.Text.Trim(),
                Mode     = _modeCb.Text.Trim().ToUpperInvariant(),
                QsoDate  = _dateTb.Text.Trim(),
                TimeOn   = _timeOnTb.Text.Trim(),
                TimeOff  = timeOff,
                State    = _stateTb.Text.Trim(),
                Country  = _countryTb.Text.Trim(),
                Grid     = _gridTb.Text.Trim(),
                Name     = _nameTb.Text.Trim(),
                RstSent  = _rstSentTb.Text.Trim(),
                RstRcvd  = _rstRcvdTb.Text.Trim(),
                Comment  = _commentTb.Text.Trim(),
            };

            string error = _onSubmit?.Invoke(record);
            if (error != null)
            {
                // Leave every field exactly as typed and don't move focus -- a rejected
                // entry (e.g. a duplicate) must stay visible and fixable, never silently lost.
                _statusTb.Text = error;
                return;
            }

            _statusTb.Text = (_isNewEntry ? "Added " : "Saved ") + callsign + ".";
            _onLogged?.Invoke();

            if (_isNewEntry)
            {
                ResetForNextEntry();
                _callTb.Focus();
            }
            else
            {
                Close();
            }
        }

        // New-entry loop: clears everything callsign/QSO-specific, keeps Band/Mode (the
        // operator's radio setup doesn't change contact-to-contact), and refreshes Date/
        // Time on to "now" for the next contact -- Time off stays blank, filled again at
        // the next Submit.
        private void ResetForNextEntry()
        {
            _callTb.Text    = "";
            _dateTb.Text    = DateTime.UtcNow.ToString("yyyyMMdd");
            _timeOnTb.Text  = DateTime.UtcNow.ToString("HHmm");
            _timeOffTb.Text = "";
            _stateTb.Text   = "";
            _countryTb.Text = "";
            _gridTb.Text    = "";
            _nameTb.Text    = "";
            _rstSentTb.Text = "";
            _rstRcvdTb.Text = "";
            _commentTb.Text = "";
        }
    }
}
