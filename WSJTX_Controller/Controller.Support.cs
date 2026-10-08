using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Support settings (2026-10-07): the recipient's Get / install / restore, setup links, and the
    // profile work behind the helper's controls (OptionsDlg.Support.cs). Installing reuses the
    // import path's backup (so Undo an Import puts it back) and reopens the window the way an
    // import into the current profile does. Nothing is downloaded or installed at start.
    public partial class Controller
    {
        // The main window of the moment, for a setup link arriving from a second start.
        internal static Controller Current;

        private const string SupportTitle = "Support Settings";

        private void InitSupport()
        {
            Current = this;
            FormClosed += (s, e) => { if (Current == this) Current = null; };
            Shown += (s, e) =>
            {
                string link = Program.PendingSupportLink;
                if (link == null || TestModeGuard.IsTestMode) return;
                Program.PendingSupportLink = null;
                BeginInvoke(new Action(() => OfferSupportLink(link)));
            };
        }

        // The station's base callsign (no slash part), or null when none is set.
        internal string MyBaseCallsign()
        {
            try { return SupportService.BaseCallsign(iniFile?.Read("nativeEngineMyCall")); } catch { return null; }
        }

        // ── Setup links ─────────────────────────────────────────────────────────────────────

        // From the link listener's thread: to this window's thread, brought forward first (the
        // operator just opened the link).
        internal void ReceiveSupportLink(string link)
        {
            if (IsDisposed || !IsHandleCreated) { Program.PendingSupportLink = link; return; }
            BeginInvoke(new Action(() =>
            {
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Activate();
                OfferSupportLink(link);
            }));
        }

        private async void OfferSupportLink(string link)
        {
            var parsed = SupportLink.Parse(link);
            if (parsed == null) return;
            var (call, code) = parsed.Value;
            string mine = MyBaseCallsign();
            string other = mine != null && !string.Equals(mine, call, StringComparison.OrdinalIgnoreCase)
                ? $" Your station callsign is {mine}; it is not changed." : "";
            if (MessageBox.Show(this,
                    $"This setup link is for {call}'s support settings.{other} Remember its download code on this computer and get the settings now? " +
                    "Nothing is installed until you choose Install.",
                    SupportTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            SupportLocal.SetCode(call, code);
            SupportLocal.ShowSupport = true;
            await GetSupportSettings(call, code, null);
        }

        // ── The recipient ───────────────────────────────────────────────────────────────────

        // Options > Support's "Get my support settings": this station's own, with its saved code
        // (asked for once when there is none).
        internal async void GetSupportSettings_Click(Control busy)
        {
            if (!SupportServiceReady()) return;
            string call = MyBaseCallsign();
            if (call == null)
            {
                MessageBox.Show(this, "Set your station callsign first, in Options, Station and Operator.", SupportTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            await GetSupportSettings(call, SupportLocal.Code(call), busy);
        }

        // Options > Support's "Enter a download code": replaces the saved one, then gets the settings.
        internal async void EnterSupportCode_Click(Control busy)
        {
            if (!SupportServiceReady()) return;
            string call = MyBaseCallsign();
            if (call == null)
            {
                MessageBox.Show(this, "Set your station callsign first, in Options, Station and Operator.", SupportTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string code = AskSupportCode(call, "");
            if (code == null) return;
            await GetSupportSettings(call, code, busy);
        }

        // Said before any question, when this copy can't reach the server at all.
        private bool SupportServiceReady()
        {
            if (SupportService.Available) return true;
            MessageBox.Show(this, SupportService.NotAvailable, SupportTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        private string AskSupportCode(string call, string why)
        {
            while (true)
            {
                string typed = PromptForText(SupportTitle, why + $"Download code for {call}, from your helper:", "");
                if (typed == null) return null;
                string code = SupportService.NormalizeCode(typed);
                if (code != null) return code;
                why = "That is not a download code; it looks like 0123ABCD-4567EF01-89AB2345-CDEF6789. ";
            }
        }

        private async Task GetSupportSettings(string call, string code, Control busy)
        {
            if (!SupportService.Available)
            {
                MessageBox.Show(this, SupportService.NotAvailable, SupportTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            code = code ?? AskSupportCode(call, "");
            if (code == null) return;
            SupportService.Result r;
            while (true)
            {
                r = await RunSupportBusy(busy, ct => new SupportService().DownloadProfile(call, code, false, ct));
                if (r.Ok) break;
                if (r.Code == "download_code_required")
                {
                    code = AskSupportCode(call, "That download code was not accepted. ");
                    if (code == null) return;
                    continue;
                }
                MessageBox.Show(this, r.Status == 404 ? $"There are no support settings for {call} on the server yet."
                        : "Could not get the support settings: " + r.Error, SupportTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            SupportLocal.SetCode(call, code);   // only a code that worked is remembered

            SupportPackage pkg;
            try { pkg = SupportPackage.Load(r.Bytes); }
            catch (InvalidDataException ex)
            {
                MessageBox.Show(this, $"The support settings can't be used: {ex.Message}. Nothing was changed.", SupportTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            ReviewAndInstallSupport(pkg, r.Bytes, "Support settings", SupportPackage.RevisionOf(SupportLocal.LastInstalledPath));
        }

        // "Save current settings as my defaults".
        internal void SaveMyDefaults_Click()
        {
            if (File.Exists(SupportLocal.MyDefaultsPath) &&
                MessageBox.Show(this, "Replace your saved defaults with the settings you have now?", SupportTitle,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            try
            {
                SaveAllSettingsToIniFile();
                if (iniFile == null) throw new InvalidOperationException("no active settings file");
                string me = MyBaseCallsign();
                var pkg = SupportPackage.FromProfile(iniFile.FilePath, ActiveWordingPath(), NotificationSounds.SoundsFolder,
                    NotificationSounds.UserSoundsFolder, me, me, 1);
                SupportPackage.SaveBytes(pkg.ToZip(), SupportLocal.MyDefaultsPath);
                MessageBox.Show(this, "Your current settings are saved as your defaults.", SupportTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save your defaults: " + ex.Message, SupportTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // "Restore my defaults" / "Restore last installed support settings": from this computer,
        // no internet needed.
        internal void RestoreSupportCopy_Click(bool lastInstalled)
        {
            string path = lastInstalled ? SupportLocal.LastInstalledPath : SupportLocal.MyDefaultsPath;
            string what = lastInstalled ? "Last installed support settings" : "Your defaults";
            if (!File.Exists(path))
            {
                MessageBox.Show(this, lastInstalled ? "No support settings have been installed on this computer yet."
                        : "You have not saved your defaults yet.", SupportTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SupportPackage pkg;
            try { pkg = SupportPackage.Load(File.ReadAllBytes(path)); }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"{what} can't be used: {ex.Message}. Nothing was changed.", SupportTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            ReviewAndInstallSupport(pkg, null, what, null);
        }

        // The review (what it is, warnings, the radio ports to use here), then -- only on Install --
        // the backup, the settings, and the window reopened. downloaded: kept as the "last
        // installed" copy once installed.
        private void ReviewAndInstallSupport(SupportPackage pkg, byte[] downloaded, string what, int? installedRevision)
        {
            string dataFolder = ProfilesAppDataPath();
            var details = pkg.Details(MyBaseCallsign(), installedRevision, pkg.SoundsReplaced(dataFolder, NotificationSounds.SoundsFolder));
            string profile = ActiveProfileDisplayName();
            Dictionary<string, string> ports;
            using (var dlg = new SupportInstallDlg(what, details, pkg, iniFile, profile))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                ports = dlg.Ports;
            }
            try
            {
                SaveAllSettingsToIniFile();   // this session's settings are what the backup keeps
                if (iniFile == null) throw new InvalidOperationException("no active settings file");
                string backup = CustomizationPackage.Backup(iniFile.FilePath, ActiveWordingPath(), Path.Combine(dataFolder, "Backups"),
                    "import", what.ToLowerInvariant(), profile);
                pkg.Apply(iniFile, dataFolder, NotificationSounds.SoundsFolder, ActiveWordingPath(), backup, ports);
                if (downloaded != null) SupportPackage.SaveBytes(downloaded, SupportLocal.LastInstalledPath);
                wsjtxClient?.DebugOutput($"{DateTime.Now:HH:mm:ss} support: {what} (for {pkg.Recipient ?? "-"}, revision {pkg.Revision}) installed into '{profile}'; backup: {backup}");
            }
            catch (Exception ex)
            {
                wsjtxClient?.DebugOutput($"{DateTime.Now:HH:mm:ss} support: install into '{profile}' failed: {ex.Message}");
                MessageBox.Show(this, $"Could not install: {ex.Message}\n\nUndo an Import puts back anything already changed.", SupportTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            MessageBox.Show(this, $"{what} installed into profile '{profile}'. The window now reopens.", SupportTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            _suppressSettingsSaveOnExit = true;   // the installed settings are on disk; see ImportCustomizations_Click
            SwitchProfileInPlace();
        }

        // ── The helper ──────────────────────────────────────────────────────────────────────

        internal static string SupportEditProfileName(string call) => "Support " + call;

        // A recipient's settings, downloaded by a helper, into the helper's own working profile
        // "Support <CALL>" (a copy of the current one, so the helper's sound devices, logins and
        // station stay), which then loads. The package's radio settings come with it.
        internal void OpenSupportForEditing(string call, SupportPackage pkg)
        {
            string name = SupportEditProfileName(call);
            string path = Path.Combine(ProfilesDirectory(), name + ".ini");
            bool exists = File.Exists(path);
            var replaced = pkg.SoundsReplaced(ProfilesAppDataPath(), NotificationSounds.SoundsFolder);
            string question = $"Open {call}'s settings to edit them?\n\n" +
                (exists ? $"They replace what is in your profile '{name}'. Your other profiles are not changed."
                        : $"They open as a new profile, '{name}'. Your own profiles are not changed.") + "\n\n" +
                $"That profile has {call}'s radio settings, so your radio may not work while it is loaded. Load your own profile again when you are done." +
                (replaced.Count > 0 ? $"\n\nIt also replaces {replaced.Count} of your own sounds: {string.Join(", ", replaced)}." : "");
            if (MessageBox.Show(this, question, SupportTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    exists ? MessageBoxDefaultButton.Button2 : MessageBoxDefaultButton.Button1) != DialogResult.Yes)
                return;
            try
            {
                if (CopyCurrentSettingsToProfile(name) == null) throw new InvalidOperationException("no active settings file");
                var ini = new IniFile(path);
                pkg.Apply(ini, ProfilesAppDataPath(), NotificationSounds.SoundsFolder,
                    pkg.WordingText != null ? ProfileWordingPath(name) : null, null, null);
                ini.Write("supportPackageRecipient", call);
                ini.Write("supportPackageRevision", pkg.Revision.ToString());
                new IniFile(BaseIniFilePath()).Write("activeProfile", name);
                wsjtxClient?.DebugOutput($"{DateTime.Now:HH:mm:ss} support helper: {call} revision {pkg.Revision} opened in profile '{name}'");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not open the support settings: " + ex.Message, SupportTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            SwitchProfileInPlace();   // this session's settings were saved to the profile being left
        }

        // The profile loaded now, as support settings for `call`: the revision after the one it
        // was opened or last uploaded with for that callsign, else 1.
        internal (SupportPackage Package, byte[] Zip) CurrentAsSupportPackage(string call, string exporter)
        {
            SaveAllSettingsToIniFile();
            if (iniFile == null) throw new InvalidOperationException("no active settings file");
            int rev = string.Equals(iniFile.Read("supportPackageRecipient"), call, StringComparison.OrdinalIgnoreCase)
                      && int.TryParse(iniFile.Read("supportPackageRevision"), out int r) ? r + 1 : 1;
            var pkg = SupportPackage.FromProfile(iniFile.FilePath, ActiveWordingPath(), NotificationSounds.SoundsFolder,
                NotificationSounds.UserSoundsFolder, call, exporter, rev);
            return (pkg, pkg.ToZip());
        }

        internal void RecordSupportUpload(string call, int revision)
        {
            iniFile?.Write("supportPackageRecipient", call);
            iniFile?.Write("supportPackageRevision", revision.ToString());
        }

        // ── Shared ──────────────────────────────────────────────────────────────────────────

        // Runs one request with `busy` (the button pressed) disabled and the wait cursor on, then
        // gives the button its focus back. Never on the radio's own thread.
        internal static async Task<SupportService.Result> RunSupportBusy(Control busy, Func<CancellationToken, Task<SupportService.Result>> request)
        {
            Form form = busy?.FindForm();
            bool hadFocus = busy?.Focused ?? false;
            if (busy != null) busy.Enabled = false;
            if (form != null) form.UseWaitCursor = true;
            try { return await Task.Run(() => request(CancellationToken.None)); }
            finally
            {
                if (form != null && !form.IsDisposed) form.UseWaitCursor = false;
                if (busy != null && !busy.IsDisposed)
                {
                    busy.Enabled = true;
                    if (hadFocus) busy.Focus();
                }
            }
        }
    }

    // The review before installing support settings: what they are, any warnings, and the radio
    // ports to use on this computer (port numbers differ between computers, so the operator
    // chooses; a port the settings name that this computer does not have is never swapped for
    // another without being shown). Install is the only button that changes anything.
    internal sealed class SupportInstallDlg : Form
    {
        internal Dictionary<string, string> Ports { get; } = new Dictionary<string, string>();
        private readonly List<(string Key, ComboBox Box, List<string> Values)> _choices = new List<(string, ComboBox, List<string>)>();

        internal SupportInstallDlg(string what, List<string> details, SupportPackage pkg, IniFile current, string profile)
        {
            Text = what;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
            Font = new Font("Microsoft Sans Serif", 8.25F);

            var lines = new List<string>(details)
            {
                $"Installing replaces the settings of profile '{profile}', except your sound devices, logins and station callsign. " +
                "A backup is made first; Undo an Import puts it back."
            };
            string[] local = new string[0];
            try { local = System.IO.Ports.SerialPort.GetPortNames().OrderBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(); } catch { }

            var info = new TextBox
            {
                ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, TabStop = true,
                Text = string.Join("\r\n", lines), Location = new Point(10, 10), Size = new Size(460, 150),
                AccessibleName = "About these settings", TabIndex = 0,
            };
            Controls.Add(info);
            int y = 170, tab = 1;
            foreach (var (key, name) in SupportPackage.PortSettings)
            {
                if (!pkg.Settings.TryGetValue(key, out string theirs) || string.IsNullOrWhiteSpace(theirs)) continue;
                string mine = current?.Read(key) ?? "";
                var values = new List<string> { null };
                var box = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(150, y - 3), Width = 320,
                    AccessibleName = name, TabIndex = tab++,
                };
                box.Items.Add(string.IsNullOrEmpty(mine) ? "Keep mine (none set)" : $"Keep mine ({mine})");
                foreach (string p in local)
                {
                    values.Add(p);
                    box.Items.Add(string.Equals(p, theirs, StringComparison.OrdinalIgnoreCase) ? $"{p}, as in the settings" : p);
                }
                bool here = local.Contains(theirs, StringComparer.OrdinalIgnoreCase);
                box.SelectedIndex = here ? values.FindIndex(v => string.Equals(v, theirs, StringComparison.OrdinalIgnoreCase)) : 0;
                Controls.Add(new Label { Text = name + ":", Location = new Point(10, y), AutoSize = true });
                Controls.Add(box);
                _choices.Add((key, box, values));
                y += 28;
                if (!here)
                {
                    string note = $"The settings use {theirs}, which this computer does not have. Choose the port your radio is on.";
                    var noteBox = new TextBox
                    {
                        ReadOnly = true, BorderStyle = BorderStyle.None, Text = note, Location = new Point(10, y), Width = 460,
                        AccessibleName = name + " note", TabIndex = tab++, BackColor = SystemColors.Control,
                    };
                    Controls.Add(noteBox);
                    y += 24;
                }
            }
            var install = new Button { Text = "&Install", Location = new Point(310, y + 6), Width = 75, TabIndex = tab++, AccessibleName = "Install" };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(395, y + 6), Width = 75, TabIndex = tab++ };
            install.Click += (s, e) =>
            {
                foreach (var (key, _) in SupportPackage.PortSettings) Ports[key] = null;   // a port the settings leave out: mine stays
                foreach (var (key, box, values) in _choices) Ports[key] = values[Math.Max(0, box.SelectedIndex)];
                DialogResult = DialogResult.OK;
            };
            Controls.Add(install);
            Controls.Add(cancel);
            CancelButton = cancel;   // Enter in the text above does not install; Install is pressed on purpose
            ClientSize = new Size(480, y + 40);
            ActiveControl = info;
        }
    }
}
