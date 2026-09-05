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
    // RaiseAutomationNotification) for Important/Critical notifications, so they can be heard
    // even while keyboard focus is elsewhere -- without moving focus, without self-voicing,
    // without calling JAWS/NVDA directly. There is NO beep here: Jimmy plays only its own
    // configured Options > Sounds WAV cues, fired at their own call sites.
    public interface INotificationDelivery
    {
        void Announce(string text, AlertCue cue);
    }

    public class StatusViewNotificationDelivery : INotificationDelivery
    {
        private readonly IJimmyStatusView _statusView;

        public StatusViewNotificationDelivery(IJimmyStatusView statusView)
        {
            _statusView = statusView;
        }

        // Routes through CoordinatedSpeak, the one screen-reader nudge seam shared with routine
        // RX/TX/QSO status -- NOT ShowMessage/ShowMsg (that stays the path for direct one-shot
        // operator feedback). The cue is not used here (the nudge is the same regardless); it is
        // the UiaAlertNotificationDelivery decorator's concern.
        public void Announce(string text, AlertCue cue) => _statusView.CoordinatedSpeak(text);
    }

    // Decorator, not a replacement -- always delivers through `inner` first, then ADDITIONALLY
    // raises the off-focus UIA notification when:
    //   * cue == Critical  -> ALWAYS attempt it (regardless of the operator option): a safety
    //                         event must reach the operator even when Jimmy is backgrounded.
    //   * cue == Important  -> only when `isEnabled()` is true -- the "Announce Important and
    //                         Critical events when focus is elsewhere" option
    //                         (Controller.announceImportantAlertsWhenFocusElsewhere).
    //   * cue == None       -> never.
    // In all cases `statusView.WouldAnnounce` is checked first: if the normal spoken path is
    // already going to say this (Jimmy focused), a second UIA notification would just double it.
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

        public void Announce(string text, AlertCue cue)
        {
            _inner.Announce(text, cue);
            if (cue == AlertCue.None) return;
            if (cue == AlertCue.Important && !_isEnabled()) return;
            if (_statusView.WouldAnnounce) return;
            _statusView.RaiseAccessibleAlert(text);
        }
    }
}
