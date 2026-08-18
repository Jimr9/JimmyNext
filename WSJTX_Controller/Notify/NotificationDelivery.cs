namespace WSJTX_Controller
{
    // The seam between "decided to announce" and "how it actually reaches the operator".
    // StatusViewNotificationDelivery below is a thin wrapper over the existing, already-verified
    // IJimmyStatusView.ShowMessage (Controller.ShowMsg), which never moves keyboard focus (it
    // only reads statusText.Focused to decide whether to send the re-announce keystroke, it
    // never sets focus).
    //
    // UiaAlertNotificationDelivery (2026-08-19) is the second implementation this interface was
    // always meant to make possible -- it wraps StatusViewNotificationDelivery (composition, not
    // replacement: the existing status-field behavior stays byte-identical) and additionally
    // raises a UI Automation Notification event (System.Windows.Forms.AccessibleObject.
    // RaiseAutomationNotification) for Important-priority notifications, so they can be heard
    // even while keyboard focus is elsewhere -- without moving focus, without self-voicing,
    // without calling JAWS/NVDA directly. Gated behind a General-tab option, off by default:
    // live JAWS and NVDA confirmation is still required before this should ever default to on
    // (see Controller.RaiseAccessibleAlert's own comment).
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

    // Decorator, not a replacement -- always delivers through `inner` first (preserving
    // StatusViewNotificationDelivery's existing behavior exactly, unconditionally), then
    // ADDITIONALLY raises a UIA notification when all three hold:
    //   1. `important` is true -- this is NotificationCenter.Deliver's own already-resolved
    //      policy.Priority (or the forced-Important ErrorSeverity.Error escalation), not a
    //      second, separately-maintained list of "which events count" -- see NotificationCenter.
    //      Deliver's own comment. Adding a new Important-by-default event type (or an operator
    //      raising an existing type's Priority via the notifyPriority_{Type} INI key) is
    //      automatically covered with no change here.
    //   2. `isEnabled()` is true -- the General-tab "Announce important notifications when focus
    //      is elsewhere" checkbox (Controller.announceImportantAlertsWhenFocusElsewhere), off by
    //      default.
    //   3. `statusView.WouldAnnounce` is false -- if the normal ShowMessage path is ALREADY going
    //      to announce this (statusText is focused and this is the active form), raising a
    //      second UIA notification for the same text would be a duplicate announcement, not a
    //      genuinely off-focus one. This is the ONE and only duplicate-avoidance check -- no
    //      separate suppression/cooldown state of its own.
    public class UiaAlertNotificationDelivery : INotificationDelivery
    {
        private readonly INotificationDelivery _inner;
        private readonly IJimmyStatusView _statusView;
        private readonly System.Func<bool> _isEnabled;

        public UiaAlertNotificationDelivery(INotificationDelivery inner, IJimmyStatusView statusView, System.Func<bool> isEnabled)
        {
            _inner = inner;
            _statusView = statusView;
            _isEnabled = isEnabled;
        }

        public void Announce(string text, bool important)
        {
            _inner.Announce(text, important);
            if (!important) return;
            if (!_isEnabled()) return;
            if (_statusView.WouldAnnounce) return;
            _statusView.RaiseAccessibleAlert(text);
        }
    }
}
