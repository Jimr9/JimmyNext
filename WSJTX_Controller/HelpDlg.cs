using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    public partial class HelpDlg : Form
    {
        private Controller ctrl;
        // 2.0.64 accessibility: HelpDlg_Activated fires on every window activation (including
        // alt-tabbing back). Focusing the help text box on each one made JAWS re-read its first
        // line -- which is the product name + version -- so opening Alt+K announced the version
        // roughly three times (Show, the tick's own Activate(), Load's old Activate()). Focus it
        // exactly once, on first activation.
        private bool _initialFocusDone;
        private static string _lastSupportFolder;   // where the last support report was saved, this session

        public HelpDlg(Controller co, string c, string t)
        {
            InitializeComponent();

            ctrl = co;
            Text = c;
            helpLabel.Text = t;
            // A concise name so a screen reader announces the control by purpose, not by
            // re-reading its (version-bearing) contents. The version stays exactly once, in the
            // visible help text itself.
            helpLabel.AccessibleName = "Jimmy Next help and shortcut keys";
            // Escape closes it, like any other window (operator, 2026-10-01).
            CancelButton = closeButton;

            // The website (operator, 2026-10-02): guides, keyboard list, updates.
            websiteButton = new Button
            {
                Text = "Jimmy Next website", Size = new Size(150, 26), TabIndex = 3,
                AccessibleName = "Jimmy Next website", UseVisualStyleBackColor = true,
            };
            websiteButton.Click += (s, e) => OpenLink(WebsiteUrl);
            Controls.Add(websiteButton);

            // Donations (operator, 2026-10-02): the PayPal Donate page.
            donateButton = new Button
            {
                Text = "Donate", Size = new Size(90, 26), TabIndex = 4,
                AccessibleName = "Donate to Jimmy Next", UseVisualStyleBackColor = true,
            };
            donateButton.Click += (s, e) => OpenLink(DonateUrl);
            Controls.Add(donateButton);
        }

        internal const string DonateUrl = "https://www.paypal.com/donate/?hosted_button_id=5BKHZQ4ZU6UNG";
        private readonly Button donateButton;

        internal const string WebsiteUrl = "https://blindsea.com/jimmy20";
        private readonly Button websiteButton;

        private void OpenLink(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not open {url}\n\n{ex.Message}", "Jimmy Next", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void HelpDlg_Load(object sender, EventArgs e)
        {
            //center on current screen
            Screen screen = Screen.FromControl(ctrl);
            Location = new Point(screen.Bounds.X + ((screen.Bounds.Width - Width) / 2) - 50, screen.Bounds.Y + ((screen.Bounds.Height - Height) / 2) - 200);

            int y = helpLabel.Size.Height;

            Height = helpLabel.Location.Y + y + 85;
            closeButton.Location = new Point(closeButton.Location.X, Height - 70);
            supportReportButton.Location = new Point(15, closeButton.Location.Y);
            websiteButton.Location = new Point(supportReportButton.Right + 10, closeButton.Location.Y);
            donateButton.Location = new Point(websiteButton.Right + 10, closeButton.Location.Y);

            helpLabel.SelectionStart = 0;
            helpLabel.SelectionLength = 0;
            // No redundant this.Activate() here -- Load already runs inside Show()'s activation;
            // an extra Activate() only fires another HelpDlg_Activated (see _initialFocusDone).
        }

        private void closeButton_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void HelpDlg_FormClosing(object sender, FormClosingEventArgs e)
        {
            ctrl.HelpClosed();
        }

        private async void supportReportButton_Click(object sender, EventArgs e)
        {
            string prefill = null;
            try { prefill = ctrl.wsjtxClient?.myCall; } catch { }

            var dlg = new SupportReportDlg(prefill) { Owner = this };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            string confirmText =
                (dlg.Upload
                    ? "Jimmy Next will create a support report and send it to KB0UZT with your callsign, name and email. A copy is kept on this computer.\n\n"
                    : "Jimmy Next will create a support report ZIP. Next you choose where to save it.\n\n") +
                "It contains your description, your Jimmy Next folder (settings with passwords removed, " +
                "logs" + (dlg.IncludeLogbook ? ", your logbook" : "") + ") and Windows information.\n\n" +
                (dlg.Upload ? "Send the report?" : "Create the report?");

            if (MessageBox.Show(confirmText, "Create Support Report",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            // 2.0.81: the operator chooses where it is saved (not everyone uses Downloads);
            // Enter keeps the offered place and name. Remembered for the rest of the session.
            string offered = SupportReportBuilder.DefaultZipPath();
            string zipPath = offered;
            // Sending (2026-10-07): no Save dialog -- the copy goes in the usual place (the name
            // carries the date and time, so it never replaces another) and the ticket message
            // says where it is.
            if (!dlg.Upload)
            using (var save = new SaveFileDialog
            {
                Title            = "Save support report",
                Filter           = "ZIP file (*.zip)|*.zip",
                FileName         = Path.GetFileName(offered),
                InitialDirectory = _lastSupportFolder ?? Path.GetDirectoryName(offered),
                OverwritePrompt  = true,
            })
            {
                if (save.ShowDialog(this) != DialogResult.OK) return;
                zipPath = save.FileName;
                _lastSupportFolder = Path.GetDirectoryName(zipPath);
            }

            var result = SupportReportBuilder.Build(
                ctrl,
                dlg.Callsign, dlg.PersonName, dlg.Email,
                dlg.ProblemType, dlg.Description, dlg.Steps, dlg.IncludeLogbook, zipPath);

            if (result.Success)
            {
                await SupportReportDelivery.Deliver(this, dlg.Upload, result.ZipPath, dlg.Callsign, dlg.PersonName, dlg.Email, supportReportButton);
            }
            else
            {
                MessageBox.Show(
                    $"Could not create support report:\n{result.Error}",
                    "Support Report Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private void HelpDlg_Activated(object sender, EventArgs e)
        {
            // Put focus in the help text once, when the window first opens -- not on every
            // re-activation, which made a screen reader re-announce the first line each time.
            if (_initialFocusDone) return;
            _initialFocusDone = true;
            helpLabel.Focus();
        }
    }
}
