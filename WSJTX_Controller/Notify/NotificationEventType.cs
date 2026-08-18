namespace WSJTX_Controller
{
    // The one place a new notification type gets added. NotificationDefaults must gain a
    // matching entry before a type is usable -- until then NotificationSettings.LoadFromIni
    // simply never iterates it, which is what lets a future Jimmy version add a new type with
    // zero required INI migration (see NotificationSettings.cs's own header comment).
    public enum NotificationEventType
    {
        QsoStarted,
        QsoCompleted,
        TxMessageChanged,
        AwardsNeeded,
        ConnectionClosed,
        ConnectionLost,
        ErrorWarning,
        ClockOutOfSync,
        ClockSynced,
        RadioCatRecovered,
    }
}
