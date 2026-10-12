using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Options > Other Programs (2026-10-12): the WSJT-X-format output a logging or mapping
    // program can listen to, and the ADIF copy of the log it can read (OtherPrograms has the
    // rules; the engine host's relay makes it receive-only). Read top to bottom: what it is,
    // on or off, where to, the log file, and what the running engine reports.
    public partial class OptionsDlg
    {
        private Panel _otherProgramsPanel;
        private CheckBox _otherProgramsEnable;
        private TextBox _otherProgramsTargets, _otherProgramsStatus;

        private void AddOtherProgramsCategory(List<Control> panels)
        {
            if (InSetupMode) return;
            var p = _otherProgramsPanel = new Panel { AccessibleName = "Other Programs", AutoScroll = true, Dock = DockStyle.Fill, Name = "otherProgramsPanel" };
            var font = SupportFont;
            int tab = 0, y = 8;
            int port = ctrl.wsjtxClient?.port > 0 ? ctrl.wsjtxClient.port : 2237;

            p.Controls.Add(new TextBox
            {
                ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control, TabStop = true, TabIndex = tab++,
                AccessibleName = "About other programs", Font = font, Location = new Point(8, y), Size = new Size(560, 74),
                Text = "Jimmy Next can send what WSJT-X sends, so a logging or mapping program can run beside it without a second WSJT-X: " +
                       "each decode, the radio's status, and each contact as it is logged. Set the other program to listen for WSJT-X on the same address and port. " +
                       "Other programs can only listen. They cannot transmit, call a station or change the radio through Jimmy Next. " +
                       "Not every program has been tried with Jimmy Next; tell us what works.",
            });
            y += 82;

            _otherProgramsEnable = new CheckBox
            {
                Text = "Send decodes, status and logged contacts to other programs", AccessibleName = "Send to other programs",
                AutoSize = true, Location = new Point(8, y), TabIndex = tab++, Font = font,
                Checked = ctrl.SettingsIni?.Read(OtherPrograms.EnabledKey) != "False",   // on unless turned off, as it always was
            };
            p.Controls.Add(_otherProgramsEnable);
            y += 28;

            p.Controls.Add(new Label { Text = "Send to (address:port; several separated by commas):", Location = new Point(8, y + 3), AutoSize = true, Font = font });
            y += 22;
            string saved = ctrl.SettingsIni?.Read(OtherPrograms.TargetsKey);
            _otherProgramsTargets = new TextBox
            {
                Location = new Point(8, y), Width = 400, TabIndex = tab++, Font = font,
                AccessibleName = "Send to, address and port",
                AccessibleDescription = "For a program on this computer use 127.0.0.1 and its WSJT-X port, usually 2237. Several are separated by commas.",
                Text = string.IsNullOrWhiteSpace(saved) ? OtherPrograms.DefaultTarget(port) : saved,
            };
            p.Controls.Add(_otherProgramsTargets);
            y += 32;

            string logFile = Path.Combine(NexusLogbook.Folder, "log.adi");
            p.Controls.Add(new Label { Text = "Your log as an ADIF file, for a program that reads one:", Location = new Point(8, y + 3), AutoSize = true, Font = font });
            y += 22;
            p.Controls.Add(new TextBox
            {
                ReadOnly = true, Location = new Point(8, y), Width = 560, TabIndex = tab++, Font = font, Text = logFile,
                AccessibleName = "Log file for other programs, read only",
                AccessibleDescription = "The engine keeps this file up to date. Another program may read it. Never let another program write to it, and do not move it.",
            });
            y += 24;
            p.Controls.Add(new TextBox
            {
                ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control, TabStop = false, Font = font,
                Location = new Point(8, y), Size = new Size(560, 32),
                Text = "Another program may read this file but must not write to it or move it. The logbook itself is kept by Jimmy Next's engine.",
            });
            y += 38;

            var refresh = SupportButton("Check Now", "Check what is being sent", 8, y, 120, ref tab);
            p.Controls.Add(refresh);
            y += 32;
            _otherProgramsStatus = new TextBox
            {
                ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, TabIndex = tab++, Font = font,
                Location = new Point(8, y), Size = new Size(560, 96), AccessibleName = "What is being sent now",
                Text = "Press Check Now to ask the running engine.",
            };
            p.Controls.Add(_otherProgramsStatus);
            refresh.Click += async (s, e) =>
            {
                refresh.Enabled = false;
                string json = await System.Threading.Tasks.Task.Run(() => ExternalDataClient.WsjtxOutStatus());
                if (IsDisposed) return;
                _otherProgramsStatus.Text = OtherPrograms.StatusText(json);
                refresh.Enabled = true;
                refresh.Focus();
            };

            panels.Add(p);
            _categoryListBox.Items.Add("Other Programs");
        }

        // False: something is wrong, said, and Options stays open on that field.
        private bool ValidateOtherPrograms()
        {
            if (_otherProgramsTargets == null || !_otherProgramsEnable.Checked) return true;
            var check = OtherPrograms.Validate(_otherProgramsTargets.Text, new[] { NativeEngineClient.ControlPort, ctrl.Radio?.RigctldPort ?? 0 });
            if (check.Error == null && check.Warning != null && check.Normalized != (ctrl.SettingsIni?.Read(OtherPrograms.TargetsKey) ?? ""))
            {
                if (MessageBox.Show(this, check.Warning + "\n\nSend there?", "Other Programs", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) == DialogResult.Yes) return true;
                check.Error = "";   // declined: stay on the field, nothing more to say
            }
            if (check.Error == null) return true;
            int idx = _categoryPanels.IndexOf(_otherProgramsPanel);
            if (idx >= 0) _categoryListBox.SelectedIndex = idx;
            if (check.Error.Length > 0) MessageBox.Show(this, check.Error, "Other Programs", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _otherProgramsTargets.Focus();
            return false;
        }

        private void SaveOtherPrograms()
        {
            if (_otherProgramsTargets == null) return;
            var ini = ctrl.SettingsIni;
            if (ini == null) return;
            int port = ctrl.wsjtxClient?.port > 0 ? ctrl.wsjtxClient.port : 2237;
            string before = OtherPrograms.EngineArg(ini.Read(OtherPrograms.EnabledKey) != "False", ini.Read(OtherPrograms.TargetsKey), port);
            var check = OtherPrograms.Validate(_otherProgramsTargets.Text, null);
            ini.Write(OtherPrograms.EnabledKey, _otherProgramsEnable.Checked.ToString());
            if (check.Error == null) ini.Write(OtherPrograms.TargetsKey, check.Normalized);
            string after = OtherPrograms.EngineArg(_otherProgramsEnable.Checked, ini.Read(OtherPrograms.TargetsKey), port);
            // The destinations are part of how the engine is started: a change restarts it
            // (transmit stops, as with any engine restart). No change, no restart.
            if (after != before) ctrl.ApplyEngineMode();
        }
    }
}
