namespace WSJTX_Controller
{
    // The seam between "decided to announce" and "how it actually reaches the operator".
    // StatusViewNotificationDelivery below is the only implementation used by this migration --
    // it's a thin wrapper over the existing, already-verified IJimmyStatusView.ShowMessage
    // (Controller.ShowMsg), which never moves keyboard focus (it only reads statusText.Focused
    // to decide whether to send the re-announce keystroke, it never sets focus).
    //
    // A second implementation using System.Windows.Forms.AccessibleObject.
    // RaiseAutomationNotification (.NET 5+, announces via UI Automation without requiring the
    // announcing control to have focus at all) could be added here later as a genuine
    // improvement -- it would close the real gap where ShowMessage's announcement is silent if
    // the operator isn't currently focused on statusText. It is deliberately NOT built or wired
    // in this pass: it has zero verification history in this app, and the project's own
    // migration docs already flag SendKeys/live-region behavior as needing real JAWS+NVDA
    // confirmation from the operator before being relied on (citing real upstream WinForms
    // regressions). This interface exists so that verification can happen later without any
    // change to NotificationCenter or any migrated call site.
    public interface INotificationDelivery
    {
        void Announce(string text, bool important);
    }

    public class StatusViewNotificationDelivery : INotificationDelivery
    {
        private readonly IJimmyStatusView _statusView;

        public StatusViewNotificationDelivery(IJimmyStatusView statusView)
        {
            _statusView = statusView;
        }

        public void Announce(string text, bool important) => _statusView.ShowMessage(text, important);
    }
}
