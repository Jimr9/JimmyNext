using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Options > Support and Options > Support Helper (2026-10-07). Shown only after Jimmy Next was
    // started once with --support (Support) or --support-helper (both); each has its own Hide
    // button. Hiding changes only what Options shows -- saved codes, the login and the local
    // copies stay. Every server request runs off the window's thread, with the button pressed
    // disabled until it answers.
    public partial class OptionsDlg
    {
        private Panel _supportPanel, _supportHelperPanel;

        private void AddSupportCategories(List<Control> panels)
        {
            if (InSetupMode) return;
            if (SupportLocal.ShowSupport)
            {
                _supportPanel = new Panel { AccessibleName = "Support", AutoScroll = true, Dock = DockStyle.Fill, Name = "supportPanel" };
                BuildSupportTab(_supportPanel);
                panels.Add(_supportPanel);
                _categoryListBox.Items.Add("Support");
            }
            if (SupportLocal.ShowHelper)
            {
                _supportHelperPanel = new Panel { AccessibleName = "Support Helper", AutoScroll = true, Dock = DockStyle.Fill, Name = "supportHelperPanel" };
                BuildSupportHelperTab(_supportHelperPanel);
                panels.Add(_supportHelperPanel);
                _categoryListBox.Items.Add("Support Helper");
            }
        }

        // Takes a category out of the list now (its Hide button); the list lands on Profiles.
        private void RemoveCategory(Control panel)
        {
            int idx = _categoryPanels.IndexOf(panel);
            if (idx < 0) return;
            _categoryPanels.RemoveAt(idx);
            _categoryListBox.Items.RemoveAt(idx);
            int profiles = _categoryPanels.IndexOf(profilesPanel);
            _categoryListBox.SelectedIndex = profiles >= 0 ? profiles : 0;
            _categoryListBox.Focus();
        }

        private static readonly Font SupportFont = new Font("Microsoft Sans Serif", 8.25F);

        private static TextBox InfoText(string text, int y, int height) => new TextBox
        {
            ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control,
            Text = text, Location = new Point(8, y), Size = new Size(560, height), TabStop = false, Font = SupportFont,
        };

        private static Button SupportButton(string text, string name, int x, int y, int width, ref int tab) => new Button
        {
            Text = text, AccessibleName = name, Location = new Point(x, y), Size = new Size(width, 27), TabIndex = tab++, Font = SupportFont,
        };

        // ── Support (the recipient) ─────────────────────────────────────────────────────────

        private void BuildSupportTab(Panel p)
        {
            int tab = 0;
            p.Controls.Add(InfoText(
                "Support settings are a complete setup a helper made for your station. Getting them shows what they are; " +
                "nothing changes until you choose Install. Your sound devices, logins and station callsign are always kept.", 8, 44));
            string call = ctrl.MyBaseCallsign();
            p.Controls.Add(new Label
            {
                Text = "Station callsign: " + (call ?? "not set"), AccessibleName = "Station callsign", Location = new Point(8, 56),
                Size = new Size(560, 18), Font = new Font(SupportFont, FontStyle.Bold),
            });

            var get = SupportButton("Get My Support Settings...", "Get my support settings", 8, 80, 280, ref tab);
            get.Click += (s, e) => ctrl.GetSupportSettings_Click(get);
            var code = SupportButton("Enter a Download Code...", "Enter a download code", 8, 112, 280, ref tab);
            code.Click += (s, e) => ctrl.EnterSupportCode_Click(code);
            var save = SupportButton("Save Current Settings as My Defaults", "Save current settings as my defaults", 8, 154, 280, ref tab);
            save.Click += (s, e) => ctrl.SaveMyDefaults_Click();
            var mine = SupportButton("Restore My Defaults...", "Restore my defaults", 8, 186, 280, ref tab);
            mine.Click += (s, e) => ctrl.RestoreSupportCopy_Click(false);
            var last = SupportButton("Restore Last Installed Support Settings...", "Restore last installed support settings", 8, 218, 280, ref tab);
            last.Click += (s, e) => ctrl.RestoreSupportCopy_Click(true);
            var hide = SupportButton("Hide Support Options", "Hide support options", 8, 260, 280, ref tab);
            hide.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "Hide the Support options? Your saved code and defaults stay. Starting Jimmy Next with --support shows them again.",
                        "Support", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                SupportLocal.ShowSupport = false;
                RemoveCategory(_supportPanel);
            };
            p.Controls.AddRange(new Control[] { get, code, save, mine, last, hide });
            if (!SupportService.Available)
                p.Controls.Add(InfoText(SupportService.NotAvailable + " Your own defaults still work.", 296, 30));
        }

        // ── Support Helper ──────────────────────────────────────────────────────────────────

        private TextBox _helperUserBox, _helperPasswordBox, _helperCallBox;
        private ListView _serverProfilesList, _supportRequestsList;
        private List<SupportService.ServerProfile> _serverProfiles = new List<SupportService.ServerProfile>();
        private List<SupportService.SupportRequest> _supportRequests = new List<SupportService.SupportRequest>();

        private void BuildSupportHelperTab(Panel p)
        {
            int tab = 0, y = 8;
            p.Controls.Add(InfoText(
                "Helper tools: your login, the support settings on the server, and support requests. Upload sends the profile " +
                "loaded now as last saved -- press OK on Options after changing settings, then come back here to upload.", y, 44));
            y += 48;

            p.Controls.Add(new Label { Text = "Helper username:", Location = new Point(8, y + 3), AutoSize = true, Font = SupportFont });
            _helperUserBox = new TextBox { Location = new Point(120, y), Width = 140, Text = SupportLocal.HelperUser, AccessibleName = "Helper username", TabIndex = tab++, Font = SupportFont };
            p.Controls.Add(new Label { Text = "Password:", Location = new Point(270, y + 3), AutoSize = true, Font = SupportFont });
            _helperPasswordBox = new TextBox { Location = new Point(334, y), Width = 140, UseSystemPasswordChar = true, AccessibleName = "Helper password", TabIndex = tab++, Font = SupportFont };
            _helperPasswordBox.Text = SupportLocal.HelperPassword;
            p.Controls.AddRange(new Control[] { _helperUserBox, _helperPasswordBox });
            PasswordReveal.Attach(_helperPasswordBox);
            y += 30;
            var saveLogin = SupportButton("Save Login", "Save helper login", 8, y, 120, ref tab);
            saveLogin.Click += (s, e) => SaveHelperLogin(saveLogin);
            p.Controls.Add(saveLogin);
            y += 36;

            // Support settings on the server.
            p.Controls.Add(new Label { Text = "Support settings on the server", Location = new Point(8, y), AutoSize = true, Font = new Font(SupportFont, FontStyle.Bold) });
            y += 20;
            var refresh = SupportButton("Refresh List", "Refresh server list", 8, y, 120, ref tab);
            refresh.Click += async (s, e) => await RefreshServerProfiles(refresh);
            p.Controls.Add(refresh);
            y += 32;
            _serverProfilesList = HelperList("Server support settings", new[] { ("Callsign", 110), ("Updated (UTC)", 150), ("Size", 80) }, y, ref tab);
            p.Controls.Add(_serverProfilesList);
            y += 112;
            p.Controls.Add(new Label { Text = "Callsign:", Location = new Point(8, y + 3), AutoSize = true, Font = SupportFont });
            _helperCallBox = new TextBox { Location = new Point(70, y), Width = 120, AccessibleName = "Recipient callsign", TabIndex = tab++, Font = SupportFont, CharacterCasing = CharacterCasing.Upper };
            p.Controls.Add(_helperCallBox);
            y += 30;
            var open = SupportButton("Open for Editing", "Open for editing", 8, y, 140, ref tab);
            var upload = SupportButton("Upload Current Profile...", "Upload current profile", 154, y, 170, ref tab);
            var delete = SupportButton("Delete from Server...", "Delete from server", 330, y, 150, ref tab);
            y += 32;
            var copyLink = SupportButton("Show Setup Link...", "Show setup link", 8, y, 140, ref tab);
            var copyCode = SupportButton("Show Download Code...", "Show download code", 154, y, 170, ref tab);
            var reset = SupportButton("Reset Download Code...", "Reset download code", 330, y, 150, ref tab);
            p.Controls.AddRange(new Control[] { open, upload, delete, copyLink, copyCode, reset });
            y += 42;

            _serverProfilesList.SelectedIndexChanged += (s, e) =>
            {
                int i = _serverProfilesList.SelectedIndices.Count == 1 ? _serverProfilesList.SelectedIndices[0] : -1;
                if (i >= 0 && i < _serverProfiles.Count) _helperCallBox.Text = _serverProfiles[i].Callsign;   // not a note row
            };
            EnterDoes(_serverProfilesList, () => OpenForEditing(open));
            EnterDoes(_helperCallBox, () => OpenForEditing(open));
            open.Click += (s, e) => OpenForEditing(open);
            upload.Click += (s, e) => UploadCurrent(upload);
            delete.Click += (s, e) => DeleteServerProfile(delete);
            copyLink.Click += (s, e) => CopyCodeOrLink(copyLink, link: true);
            copyCode.Click += (s, e) => CopyCodeOrLink(copyCode, link: false);
            reset.Click += (s, e) => ResetCode(reset);

            // Support requests.
            p.Controls.Add(new Label { Text = "Support requests", Location = new Point(8, y), AutoSize = true, Font = new Font(SupportFont, FontStyle.Bold) });
            y += 20;
            var refreshRequests = SupportButton("Refresh Requests", "Refresh support requests", 8, y, 140, ref tab);
            refreshRequests.Click += async (s, e) => await RefreshSupportRequests(refreshRequests);
            p.Controls.Add(refreshRequests);
            y += 32;
            _supportRequestsList = HelperList("Support requests", new[] { ("Callsign", 80), ("Name", 110), ("Email", 150), ("Sent (UTC)", 130), ("Size", 70) }, y, ref tab);
            p.Controls.Add(_supportRequestsList);
            y += 112;
            var saveReport = SupportButton("Save Report...", "Save report", 8, y, 140, ref tab);
            var deleteRequest = SupportButton("Delete Request...", "Delete request", 154, y, 140, ref tab);
            saveReport.Click += (s, e) => SaveSupportReport(saveReport);
            deleteRequest.Click += (s, e) => DeleteSupportRequest(deleteRequest);
            EnterDoes(_supportRequestsList, () => SaveSupportReport(saveReport));
            p.Controls.AddRange(new Control[] { saveReport, deleteRequest });
            y += 42;

            var hide = SupportButton("Hide Support Helper Options", "Hide support helper options", 8, y, 200, ref tab);
            hide.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "Hide the Support Helper options? Your saved login stays. Starting Jimmy Next with --support-helper shows them again.",
                        "Support Helper", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                SupportLocal.ShowHelper = false;
                RemoveCategory(_supportHelperPanel);
            };
            p.Controls.Add(hide);
            if (!SupportService.Available)
                p.Controls.Add(InfoText(SupportService.NotAvailable, y + 36, 30));

            // Both lists fill themselves the first time the page shows (2026-10-08), quietly: a
            // problem is the list's one row, never a popup.
            bool loaded = false;
            p.ParentChanged += async (s, e) =>
            {
                if (p.Parent == null || loaded) return;
                loaded = true;
                await RefreshServerProfiles(null, quiet: true);
                await RefreshSupportRequests(null, quiet: true);
            };
        }

        private static ListView HelperList(string name, (string Title, int Width)[] columns, int y, ref int tab)
        {
            var lv = new ListView
            {
                View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
                Location = new Point(8, y), Size = new Size(560, 104), AccessibleName = name, TabIndex = tab++, Font = SupportFont,
            };
            foreach (var (title, width) in columns) lv.Columns.Add(title, width);
            return lv;
        }

        // Enter on a list or box does its action, instead of pressing Options' OK.
        private static void EnterDoes(Control c, Action action)
        {
            c.PreviewKeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) e.IsInputKey = true; };
            c.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter || e.Alt || e.Control) return;
                e.Handled = e.SuppressKeyPress = true;
                action();
            };
        }

        // Said before any question, when this copy can't reach the server at all.
        private bool ServerReady()
        {
            if (SupportService.Available) return true;
            MessageBox.Show(this, SupportService.NotAvailable, "Support Helper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        private SupportService HelperService() => new SupportService
        {
            HelperUser = _helperUserBox.Text.Trim(), HelperPassword = _helperPasswordBox.Text,
        };

        // A failed helper request, said plainly; a refused login puts the cursor on the username.
        private void HelperFailed(string doing, SupportService.Result r)
        {
            MessageBox.Show(this, $"Could not {doing}: {r.Error}", "Support Helper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (r.Code == "helper_login") _helperUserBox.Focus();
        }

        private string HelperCall()
        {
            string call = SupportService.BaseCallsign(_helperCallBox.Text);
            if (call == null)
            {
                MessageBox.Show(this, "Type the recipient's callsign, or choose one in the list.", "Support Helper", MessageBoxButtons.OK, MessageBoxIcon.Information);
                _helperCallBox.Focus();
            }
            return call;
        }

        private static string When(string utc) =>
            DateTime.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                ? t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : utc;

        private static string SizeText(long bytes) => bytes >= 1048576 ? $"{bytes / 1048576.0:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";

        // Saves the login, then checks it at once (2026-10-08): a wrong password or a broken key is
        // said now, when the helper can fix it; a working login fills both lists.
        private async void SaveHelperLogin(Control busy)
        {
            SupportLocal.HelperUser = _helperUserBox.Text;
            SupportLocal.HelperPassword = _helperPasswordBox.Text;
            if (!SupportService.Available || _helperUserBox.Text.Trim().Length == 0 || _helperPasswordBox.Text.Length == 0)
            {
                MessageBox.Show(this, "Helper login saved on this computer.", "Support Helper", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var r = await Controller.RunSupportBusy(busy, ct => HelperService().ListProfiles(ct));
            if (r.Ok)
                MessageBox.Show(this, "Helper login saved and accepted by the server.", "Support Helper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else if (r.Code == "helper_login" || r.Status == 403)
                HelperFailed("check the login", r);
            else
                MessageBox.Show(this, "Helper login saved, but it could not be checked now: " + r.Error, "Support Helper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ShowServerProfiles(r);
            if (r.Ok) await RefreshSupportRequests(null, quiet: true);
        }

        // A list's one note row: why it is empty, or why it could not be read.
        private static void ListNote(ListView lv, string text)
        {
            lv.Items.Clear();
            lv.Items.Add(new ListViewItem(text));
        }

        // quiet (the page opening): a problem goes in the list only, never a popup.
        private async System.Threading.Tasks.Task RefreshServerProfiles(Control busy, bool quiet = false)
        {
            var problem = HelperListProblem(quiet);
            if (problem != null) { _serverProfiles.Clear(); ListNote(_serverProfilesList, problem); return; }
            var r = await Controller.RunSupportBusy(busy, ct => HelperService().ListProfiles(ct));
            ShowServerProfiles(r);
            if (!r.Ok && !quiet) HelperFailed("read the server list", r);
        }

        private void ShowServerProfiles(SupportService.Result r)
        {
            _serverProfiles = SupportService.Profiles(r);
            _serverProfilesList.Items.Clear();
            foreach (var sp in _serverProfiles)
                _serverProfilesList.Items.Add(new ListViewItem(new[] { sp.Callsign, When(sp.ModifiedUtc), SizeText(sp.Bytes) }));
            if (!r.Ok) ListNote(_serverProfilesList, "Could not read the list: " + r.Error);
            else if (_serverProfiles.Count == 0) ListNote(_serverProfilesList, "No support settings on the server.");
        }

        // Why a list can't be read at all, or null. Not quiet: the reason is also said.
        private string HelperListProblem(bool quiet)
        {
            string problem = !SupportService.Available ? SupportService.NotAvailable
                : _helperUserBox.Text.Trim().Length == 0 || _helperPasswordBox.Text.Length == 0 ? "Enter your helper login above, then Save Login."
                : null;
            if (problem != null && !quiet) MessageBox.Show(this, problem, "Support Helper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return problem;
        }

        private async void OpenForEditing(Control busy)
        {
            if (!ServerReady()) return;
            string call = HelperCall();
            if (call == null) return;
            var r = await Controller.RunSupportBusy(busy, ct => HelperService().DownloadProfile(call, null, true, ct));
            if (!r.Ok)
            {
                if (r.Status == 404) MessageBox.Show(this, $"There are no support settings for {call} on the server.", "Support Helper", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else HelperFailed($"download {call}'s settings", r);
                return;
            }
            SupportPackage pkg;
            try { pkg = SupportPackage.Load(r.Bytes); }
            catch (InvalidDataException ex)
            {
                MessageBox.Show(this, $"{call}'s settings can't be used: {ex.Message}. Nothing was changed.", "Support Helper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            ctrl.OpenSupportForEditing(call, pkg);
        }

        private async void UploadCurrent(Control busy)
        {
            if (!ServerReady()) return;
            string call = HelperCall();
            if (call == null) return;
            string user = _helperUserBox.Text.Trim().ToUpperInvariant();
            SupportPackage pkg;
            byte[] zip;
            try { (pkg, zip) = ctrl.CurrentAsSupportPackage(call, user); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not make the support settings: " + ex.Message, "Support Helper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show(this, $"Upload your profile '{Controller.ActiveProfileDisplayName()}' as {call}'s support settings?\n\n" +
                    "Included: all your settings, including radio, hotkeys, wording and sounds.\n\n" +
                    $"Not included: your sound devices, logins and station callsign. {call} keeps their own.",
                    "Support Helper", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            string replaceRevision = null;
            bool askedOnce = false;
            while (true)
            {
                string rev = replaceRevision;
                var r = await Controller.RunSupportBusy(busy, ct => HelperService().UploadProfile(call, zip, rev, ct));
                if (r.Ok)
                {
                    ctrl.RecordSupportUpload(call, pkg.Revision);
                    ShowCodeWindow(call, r.Get("download_code"), true,
                        $"{call}'s support settings are uploaded, revision {pkg.Revision}. Send them this setup link.");
                    await RefreshServerProfiles(busy, quiet: true);
                    return;
                }
                if (r.Code != "profile_exists") { HelperFailed($"upload {call}'s settings", r); return; }
                // On the server already: replaced only on Yes; a file changed since the question asks again.
                string question = askedOnce
                    ? $"{call}.zip changed on the server since you were asked. Replace it now?"
                    : $"Replace {call}.zip on the server?";
                if (MessageBox.Show(this, question, "Support Helper", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return;
                askedOnce = true;
                replaceRevision = r.Get("revision");
                if (string.IsNullOrEmpty(replaceRevision)) { HelperFailed($"replace {call}.zip", new SupportService.Result { Error = "the server did not say which file it holds." }); return; }
            }
        }

        private async void CopyCodeOrLink(Control busy, bool link)
        {
            if (!ServerReady()) return;
            string call = HelperCall();
            if (call == null) return;
            var r = await Controller.RunSupportBusy(busy, ct => HelperService().ProfileCode(call, ct));
            if (!r.Ok)
            {
                if (r.Status == 404) MessageBox.Show(this, $"There are no support settings for {call} on the server; upload them first.", "Support Helper", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else HelperFailed($"get {call}'s download code", r);
                return;
            }
            ShowCodeWindow(call, r.Get("download_code"), link, null);
        }

        // The setup link or download code in a read-only field to read, with Copy (2026-10-08):
        // nothing goes on the clipboard until Copy is pressed.
        private void ShowCodeWindow(string call, string code, bool link, string intro)
        {
            string text = link ? SupportLink.Make(call, code) : SupportService.NormalizeCode(code);
            if (text == null) { MessageBox.Show(this, "The server sent no usable download code.", "Support Helper", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            string what = link ? "Setup link" : "Download code";
            intro = (intro == null ? "" : intro + " ") + (link
                ? $"{what} for {call}. Paste it into an email or message to them. Anyone who has the link can get these settings."
                : $"{what} for {call}.");
            using (var dlg = new Form
            {
                Text = $"{what} for {call}", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, ClientSize = new Size(480, 150), Font = SupportFont,
            })
            {
                var about = new TextBox
                {
                    ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control, TabStop = false,
                    Text = intro, Location = new Point(10, 10), Size = new Size(460, 48),
                };
                var field = new TextBox { ReadOnly = true, Text = text, Location = new Point(10, 66), Width = 460, AccessibleName = what, TabIndex = 0 };
                var copy = new Button { Text = "&Copy", Location = new Point(310, 108), Width = 75, TabIndex = 1 };
                var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Location = new Point(395, 108), Width = 75, TabIndex = 2 };
                copy.Click += (s, e) =>
                {
                    try { Clipboard.SetText(text); MessageBox.Show(dlg, $"{what} copied.", dlg.Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }
                    catch { MessageBox.Show(dlg, "Could not copy to the clipboard. Try again.", dlg.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                };
                dlg.Controls.AddRange(new Control[] { about, field, copy, close });
                dlg.CancelButton = close;
                dlg.Shown += (s, e) => { field.Focus(); field.SelectAll(); };
                dlg.ShowDialog(this);
            }
        }

        private async void ResetCode(Control busy)
        {
            if (!ServerReady()) return;
            string call = HelperCall();
            if (call == null) return;
            if (MessageBox.Show(this, $"Reset {call}'s download code? The saved code on their computers and old setup links stop working for new downloads. " +
                    "Settings already installed are not affected.",
                    "Support Helper", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            var r = await Controller.RunSupportBusy(busy, ct => HelperService().ResetProfileCode(call, ct));
            if (!r.Ok) { HelperFailed($"reset {call}'s download code", r); return; }
            ShowCodeWindow(call, r.Get("download_code"), true,
                $"{call} has a new download code; old setup links no longer work. Send them this new setup link.");
        }

        private async void DeleteServerProfile(Control busy)
        {
            if (!ServerReady()) return;
            string call = HelperCall();
            if (call == null) return;
            var entry = _serverProfiles.FirstOrDefault(sp => string.Equals(sp.Callsign, call, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                MessageBox.Show(this, $"{call} is not in the list. Refresh the list, then choose it.", "Support Helper", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, $"Delete {call}'s support settings from the server? Their download code goes too. This can't be undone.",
                    "Support Helper", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            var r = await Controller.RunSupportBusy(busy, ct => HelperService().DeleteProfile(call, entry.Revision, ct));
            string said = r.Ok ? $"{call}'s support settings were deleted from the server."
                : r.Code == "profile_changed" ? $"{call}'s settings changed on the server since the list was read; nothing was deleted. The list is refreshed; delete again if you still want to."
                : r.Status == 404 ? $"{call}'s settings were already gone from the server."
                : null;
            if (said == null) { HelperFailed($"delete {call}'s settings", r); return; }
            MessageBox.Show(this, said, "Support Helper", MessageBoxButtons.OK, r.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            await RefreshServerProfiles(busy, quiet: true);
        }

        private async System.Threading.Tasks.Task RefreshSupportRequests(Control busy, bool quiet = false)
        {
            var problem = HelperListProblem(quiet);
            if (problem != null) { _supportRequests.Clear(); ListNote(_supportRequestsList, problem); return; }
            var r = await Controller.RunSupportBusy(busy, ct => HelperService().ListSupport(ct));
            _supportRequests = SupportService.Requests(r);
            _supportRequestsList.Items.Clear();
            foreach (var q in _supportRequests)
                _supportRequestsList.Items.Add(new ListViewItem(new[] { q.Callsign, q.Name, q.Email, When(q.SubmittedUtc), SizeText(q.Bytes) }));
            if (!r.Ok) ListNote(_supportRequestsList, "Could not read the list: " + r.Error);
            else if (_supportRequests.Count == 0) ListNote(_supportRequestsList, "No support requests.");
            if (!r.Ok && !quiet) HelperFailed("read the support requests", r);
        }

        private SupportService.SupportRequest SelectedRequest()
        {
            int i = _supportRequestsList.SelectedIndices.Count == 1 ? _supportRequestsList.SelectedIndices[0] : -1;
            if (i >= 0 && i < _supportRequests.Count) return _supportRequests[i];   // not a note row
            MessageBox.Show(this, "Choose a support request in the list first (Refresh Requests).", "Support Helper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        private async void SaveSupportReport(Control busy)
        {
            if (!ServerReady()) return;
            var q = SelectedRequest();
            if (q == null) return;
            var r = await Controller.RunSupportBusy(busy, ct => HelperService().DownloadSupport(q.Ticket, ct));
            if (!r.Ok) { HelperFailed("download the report", r); return; }
            using (var sfd = new SaveFileDialog
            {
                Title = "Save support report",
                FileName = r.FileName ?? q.FileName ?? q.Ticket + ".zip",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                OverwritePrompt = true,
            })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllBytes(sfd.FileName, r.Bytes); }
                catch (Exception ex) { MessageBox.Show(this, "Could not save the report: " + ex.Message, "Support Helper", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            }
            MessageBox.Show(this, "Report saved. It stays on the server until you delete it.", "Support Helper", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private async void DeleteSupportRequest(Control busy)
        {
            if (!ServerReady()) return;
            var q = SelectedRequest();
            if (q == null) return;
            if (MessageBox.Show(this, $"Delete the support request from {q.Callsign}{(string.IsNullOrEmpty(q.Name) ? "" : ", " + q.Name)}, with its report and sender details? This can't be undone.",
                    "Support Helper", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            var r = await Controller.RunSupportBusy(busy, ct => HelperService().DeleteSupport(q.Ticket, q.Revision, ct));
            string said = r.Ok ? "Support request deleted."
                : r.Code == "support_changed" ? "That request changed on the server since the list was read; nothing was deleted. The list is refreshed; delete again if you still want to."
                : r.Status == 404 ? "That request was already gone."
                : null;
            if (said == null) { HelperFailed("delete the request", r); return; }
            MessageBox.Show(this, said, "Support Helper", MessageBoxButtons.OK, r.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            await RefreshSupportRequests(busy, quiet: true);
        }
    }
}
