using System;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Update offer (operator request, 2026-09-29): the startup update check used to show a bare
    // Yes/No message box with only the version. This shows the release notes too, in a
    // read-only box the screen reader can be tabbed into and read line by line, then
    // "Yes" / "No". Focus starts in the notes; Escape = No, Enter on a button presses it.
    internal sealed class UpdateAvailableDlg : Form
    {
        private readonly TextBox _notes;

        internal UpdateAvailableDlg(string version, string summary, string releaseNotes)
        {
            var font = new Font("Microsoft Sans Serif", 9F);
            Text = $"Update to version {version}";
            Font = font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 400);

            Controls.Add(new Label { Text = "Update details:", Location = new Point(12, 12), AutoSize = true, TabStop = false });
            _notes = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = true,
                Location = new Point(12, 32),
                Size = new Size(536, 312),
                TabIndex = 0,
                // The version line leads the box, and there is no separate label for it: a label
                // is skipped by a screen reader tabbing through (operator report, 2026-09-29), and
                // shown beside the box it only repeated the box's first line for sighted users.
                AccessibleName = "Update details",
                Text = summary + "\r\n\r\nWhat's new:\r\n" + NotesAsPlainText(releaseNotes),
            };
            Controls.Add(_notes);

            var yes = new Button
            {
                Text = "&Yes",
                AccessibleName = "Yes",
                DialogResult = DialogResult.Yes,
                Location = new Point(352, 358),
                Size = new Size(100, 28),
                TabIndex = 1,
            };
            var no = new Button
            {
                Text = "&No",
                AccessibleName = "No",
                DialogResult = DialogResult.No,
                Location = new Point(460, 358),
                Size = new Size(88, 28),
                TabIndex = 2,
            };
            Controls.Add(yes);
            Controls.Add(no);
            CancelButton = no;   // Escape = No. No AcceptButton: Enter in the notes must not install.
        }

        // The first line of the offer (one wording for the real offer and the preview switch).
        internal static string Summary(string product, UpdateInfo info, string currentVersion)
        {
            string released = info.Published.HasValue ? $" (released {info.Published.Value.ToLocalTime():MMMM d, yyyy})" : "";
            return $"{product} {info.Version} is available{released}. You have {currentVersion}. " +
                   $"{product} will close to complete the install.";
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _notes.Focus();
            _notes.Select(0, 0);
        }

        // GitHub release notes are Markdown; a screen reader should hear the words, not
        // "number sign number sign" or "star star". Headings, emphasis, inline code marks and
        // link syntax are removed; "- " / "* " list items read as "- ". Line breaks become CRLF
        // for the TextBox. Empty -> a plain sentence rather than an empty box.
        internal static string NotesAsPlainText(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return "No release notes were published for this version.";
            string s = markdown.Replace("\r\n", "\n");
            s = Regex.Replace(s, @"^\s{0,3}#{1,6}\s*", "", RegexOptions.Multiline);          // headings
            s = Regex.Replace(s, @"\[([^\]]+)\]\((?:[^)]+)\)", "$1");                         // [text](url) -> text
            s = Regex.Replace(s, @"^(\s*)[*+]\s+", "$1- ", RegexOptions.Multiline);          // * item -> - item
            s = Regex.Replace(s, @"(\*\*|__|\*|`)", "");                                      // bold / italic / code marks
            s = Regex.Replace(s, @"\n{3,}", "\n\n");
            return s.Trim().Replace("\n", "\r\n");
        }
    }
}
