using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
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

        private void supportReportButton_Click(object sender, EventArgs e)
        {
            string prefill = null;
            try { prefill = ctrl.wsjtxClient?.myCall; } catch { }

            var dlg = new SupportReportDlg(prefill) { Owner = this };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            string confirmText =
                "Jimmy will create a support report ZIP in your Downloads folder.\n\n" +
                "The ZIP may contain diagnostic information including Jimmy version, settings, " +
                "recent log files, recent decode history, Windows information, WSJT-X connection " +
                "information, and your written description.\n\n" +
                "Nothing will be emailed automatically.\n\n" +
                "You may review the ZIP before sending it.\n\n" +
                "Create the report?";

            if (MessageBox.Show(confirmText, "Create Support Report",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            var result = SupportReportBuilder.Build(
                ctrl,
                dlg.Callsign, dlg.PersonName, dlg.Email,
                dlg.ProblemType, dlg.Description, dlg.Steps);

            if (result.Success)
            {
                MessageBox.Show(
                    $"Support report saved:\n{result.ZipPath}\n\nYou may review the ZIP before sending it to support.",
                    "Support Report Created",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
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
