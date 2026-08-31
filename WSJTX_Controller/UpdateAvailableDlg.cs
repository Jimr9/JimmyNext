using System;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // The "an update is available" prompt shown by Controller.OfferUpdate, replacing a plain
    // Yes/No MessageBox that could only state the version numbers. Adds an accessible,
    // read-only "what's new" area (the GitHub release notes, already flattened to plain text
    // by UpdateChecker.SanitizeNotes) so a screen-reader user can actually hear what changed
    // before deciding to install.
    //
    // Hand-built (no .Designer file), mirroring SetTxFreqDlg / ManualCallDlg. The notes area
    // is a real multiline TextBox, not a Label: JAWS/NVDA let the user arrow through a
    // read-only edit control line by line, which a static Label doesn't support well, and it
    // scrolls for long notes. It is ReadOnly + never rendered as markup and never handed to a
    // browser/Process.Start -- it is display text only.
    internal sealed class UpdateAvailableDlg : Form
    {
        private readonly string _version;

        public UpdateAvailableDlg(string productName, UpdateInfo info, string currentVersion)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            _version = info.Version ?? "";

            string released = info.Published.HasValue
                ? $" (released {info.Published.Value.ToLocalTime():MMMM d, yyyy})"
                : "";

            Text = "Update Available";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            bool haveNotes = !string.IsNullOrEmpty(info.Notes);
            int width = 470;
            ClientSize = new Size(width, haveNotes ? 320 : 150);

            var heading = new Label
            {
                Text = $"{productName} {_version} is available{released}.{Environment.NewLine}You have {currentVersion}.",
                Location = new Point(12, 12),
                Size = new Size(width - 24, 40),
                AutoSize = false,
                TabStop = false,
                AccessibleRole = AccessibleRole.StaticText,
            };

            Control notesControl;
            var notesLabel = new Label
            {
                Text = haveNotes ? $"What's new in version {_version}:" : "Release notes",
                Location = new Point(12, 58),
                AutoSize = true,
                TabStop = false,
            };

            if (haveNotes)
            {
                var notes = new TextBox
                {
                    Multiline = true,
                    ReadOnly = true,
                    WordWrap = true,
                    ScrollBars = ScrollBars.Vertical,
                    TabStop = true,
                    BackColor = SystemColors.Window,
                    Location = new Point(12, 80),
                    Size = new Size(width - 24, 170),
                    Text = info.Notes,
                    AccessibleName = $"What's new in version {_version}",
                };
                notes.Select(0, 0);
                notesControl = notes;
            }
            else
            {
                notesControl = new Label
                {
                    Text = "Release notes aren't available for this version — see GitHub.",
                    Location = new Point(12, 80),
                    Size = new Size(width - 24, 20),
                    AutoSize = false,
                    TabStop = false,
                };
            }

            int buttonY = ClientSize.Height - 40;

            var installButton = new Button
            {
                Text = "&Install update",
                Location = new Point(12, buttonY),
                Size = new Size(120, 28),
                DialogResult = DialogResult.OK,
            };

            var laterButton = new Button
            {
                Text = "Remind me &later",
                Location = new Point(140, buttonY),
                Size = new Size(120, 28),
                DialogResult = DialogResult.Cancel,
            };

            var notesButton = new Button
            {
                Text = "&View release notes",
                Location = new Point(width - 24 - 150, buttonY),
                Size = new Size(150, 28),
            };
            notesButton.Click += (s, e) => OpenReleaseNotesPage();

            // Tab order: notes area first (so it can be reached), then the buttons.
            heading.TabIndex = 0;
            notesLabel.TabIndex = 1;
            notesControl.TabIndex = 2;
            installButton.TabIndex = 3;
            laterButton.TabIndex = 4;
            notesButton.TabIndex = 5;

            Controls.AddRange(new Control[] { heading, notesLabel, notesControl, installButton, laterButton, notesButton });
            AcceptButton = installButton;
            CancelButton = laterButton;
            ActiveControl = installButton;
        }

        // Opens the GitHub release's own tag page in the default browser. The version string
        // comes from an external API response, so it is validated against a strict numeric
        // shape before being placed in the URL; anything unexpected falls back to the update
        // site the F4 hotkey already uses.
        private void OpenReleaseNotesPage()
        {
            string url = Regex.IsMatch(_version ?? "", @"^\d+(\.\d+){1,3}$")
                ? $"https://github.com/Jimr9/JimmyNext/releases/tag/v{_version}"
                : "https://blindsea.com/jimmy20";
            try
            {
                // UseShellExecute=true, explicit -- see Controller.verLabel2_Click's own
                // comment: on .NET 5+ Process.Start(string) no longer implies shell execution,
                // so launching a URL fails without it.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not open the release notes page:{Environment.NewLine}{ex.Message}",
                    "Update Available", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
