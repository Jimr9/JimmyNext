namespace WSJTX_Controller
{
    // Normal = today's plain ShowMessage(text, sound:false) behavior (text only, no beep).
    // Important = ShowMessage(text, sound:true) -- the existing SystemSounds.Beep.Play() cue,
    // same two-level distinction every one of the ~74 existing call sites already makes ad hoc
    // via ShowMessage's own bool parameter, just centrally policy-driven now instead of a
    // literal true/false picked at each call site.
    public enum NotificationPriority
    {
        Normal,
        Important,
    }

    // Per-event-type policy. One instance per NotificationEventType, held in
    // NotificationSettings.Policies. Mutable POCO (matches RadioSettings/JimmySettings'
    // plain-property style) -- LoadFromIni mutates a clone of the code default in place.
    public class NotificationPolicy
    {
        public bool Enabled { get; set; } = true;
        public NotificationPriority Priority { get; set; } = NotificationPriority.Normal;

        // 0 = no per-identity repeat suppression. >0 = suppress a repeat announcement for the
        // SAME DedupKey within this many seconds (e.g. the same callsign working the same QSO
        // step twice in a row).
        public int RepeatSeconds { get; set; } = 0;

        // 0 = no throttle. >0 = suppress ANY announcement of this event type within this many
        // milliseconds of the last one of the same type, regardless of identity -- the global
        // "don't flood" backstop (e.g. AwardsNeeded during a pileup).
        public int ThrottleMilliseconds { get; set; } = 0;

        // {Token}-form template; NotificationDefaults supplies the code-authoritative default
        // for every event type, this may be overridden per event type via the
        // notifyTemplate_{Type} INI key. Never null after NotificationDefaults construction.
        public string Template { get; set; } = "";

        public NotificationPolicy Clone() => new NotificationPolicy
        {
            Enabled = Enabled,
            Priority = Priority,
            RepeatSeconds = RepeatSeconds,
            ThrottleMilliseconds = ThrottleMilliseconds,
            Template = Template,
        };
    }
}
