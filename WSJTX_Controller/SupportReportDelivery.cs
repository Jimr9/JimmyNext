using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // 2.0.81: getting a support report to KB0UZT. Upload = KB0UZT's Dropbox file request (a page
    // that only accepts files -- nobody sending can see or download anything in the folder);
    // otherwise the report stays in Downloads, as before.
    internal static class SupportReportDelivery
    {
        internal const string UploadUrl = "https://www.dropbox.com/request/p7h185fnh24sqrhhkm36";

        private const string Title = "Support Report";

        public static void Deliver(IWin32Window owner, bool upload, string zipPath)
        {
            if (!upload)
            {
                MessageBox.Show(owner, $"Support report saved:\n{zipPath}", Title,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // The page can't be filled in by a program, so the report's location goes on the
            // clipboard for the operator to paste into the page's file box. Said BEFORE the
            // browser opens, so the instructions are not hidden behind it.
            bool copied = TryCopy(zipPath);
            string how = copied
                ? "Its location is copied. On the upload page, choose Add files, press Control V to paste, press Enter, then Upload."
                : $"On the upload page, choose Add files and open:\n{zipPath}\nthen Upload.";
            MessageBox.Show(owner, "Report saved. " + how + "\n\nPress OK to open the upload page.", Title,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (!TryOpen(UploadUrl))
                MessageBox.Show(owner, $"Could not open the upload page:\n{UploadUrl}\n\nYour report is saved:\n{zipPath}", Title,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private static bool TryCopy(string text)
        {
            try { Clipboard.SetText(text); return true; } catch { return false; }
        }

        private static bool TryOpen(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return true; }
            catch { return false; }
        }
    }
}
