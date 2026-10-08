using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Getting a support report to KB0UZT. Since 2026-10-07 it is sent from inside Jimmy Next to the
    // support service (SupportService) with the sender's callsign, name and email, and the
    // service's ticket number is shown once it has the report. The report stays saved where the
    // operator chose either way, so a failed send loses nothing. A failed send is tried again only
    // when the operator says so (a send that timed out may have arrived; trying again then makes a
    // second ticket, which is harmless).
    internal static class SupportReportDelivery
    {
        private const string Title = "Support Report";

        public static async Task Deliver(IWin32Window owner, bool upload, string zipPath, string callsign, string name, string email, Control busy)
        {
            if (!upload)
            {
                MessageBox.Show(owner, $"Support report saved:\n{zipPath}", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!SupportService.Available)
            {
                MessageBox.Show(owner, SupportService.NotAvailable + $" Your report is saved:\n{zipPath}", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            byte[] bytes;
            try { bytes = File.ReadAllBytes(zipPath); }
            catch (Exception ex)
            {
                MessageBox.Show(owner, $"Could not read the saved report to send it: {ex.Message}\n\n{zipPath}", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string call = SupportService.BaseCallsign(callsign);
            while (true)
            {
                var r = await Controller.RunSupportBusy(busy, ct => new SupportService().SubmitSupport(call, name, email, Path.GetFileName(zipPath), bytes, ct));
                if (r.Ok && !string.IsNullOrEmpty(r.Get("ticket")))
                {
                    try { SupportLocal.SenderName = name; SupportLocal.SenderEmail = email; } catch { }
                    MessageBox.Show(owner,
                        $"Support request sent. Your ticket number is {r.Get("ticket")}.\n\nThe report is also saved on this computer:\n{zipPath}\n\n" +
                        "Press Control C to copy this message.",
                        Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (MessageBox.Show(owner,
                        $"The report was not sent: {r.Error}\n\nIt is saved on this computer:\n{zipPath}\n\nTry sending it again?",
                        Title, MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Retry)
                    return;
            }
        }
    }
}
