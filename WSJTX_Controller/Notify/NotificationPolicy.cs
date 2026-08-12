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

    // WHEN a notification that passed policy/dedup is actually delivered. Added 2026-08-12 for
    // the configurable-timing feature -- deliberately just two values, not a combinatorial mess
    // of FT8/FT4/RX/TX-specific cases: every real per-period boundary Jimmy has (UDP's
    // ProcessDecodes()/decoding-flag transition, Direct mode's new-slot detection) already
    // collapses to one transport-agnostic "a receive period just completed" signal
    // (WsjtxClient.NotifyPeriodBoundary()), regardless of whether the active mode is FT8's 15s
    // period or FT4's 7.5s one -- the trigger is a real state transition, not a duration, so
    // there is nothing FT8/FT4-specific left for this enum to represent. Whether a period-timed
    // notification should also wait out an in-progress transmission is an orthogonal, separate
    // question -- see DeferWhileTransmitting below, not folded into this enum.
    public enum NotificationTiming
    {
        // Deliver the instant Publish() passes policy/dedup -- today's exact, only-ever
        // behavior for every currently-wired event type (ConnectionClosed/ConnectionLost/
        // ErrorWarning). Left as every type's default so nothing already live changes behavior
        // by default.
        Immediate,

        // Hold the latest event of this (EventType, DedupKey) and deliver it the next time
        // WsjtxClient.NotifyPeriodBoundary() fires (a real receive-period-just-completed
        // transition, not a timer). A second publish of the same identity before that boundary
        // simply replaces the pending one -- "latest wins", not a queue -- so a station whose
        // report changes twice in one busy period is announced once, with the final value.
        NextPeriodBoundary,
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

        // Added 2026-08-12. See NotificationTiming's own comments for what each value means and
        // why there are only two.
        public NotificationTiming Timing { get; set; } = NotificationTiming.Immediate;

        // Added 2026-08-12, orthogonal to Timing: when true, a notification that would
        // otherwise deliver while WsjtxClient.transmitting is true instead waits -- an Immediate
        // one waits for the current over to end, a NextPeriodBoundary one simply isn't released
        // at a boundary that lands mid-transmission. Exists for exactly the case the operator
        // described: "event occurs during TX -> suppress or defer" -- interrupting a live
        // transmission with unrelated speech is the one thing every existing status-line
        // announcement in this app already goes out of its way to avoid (see ShowStatus's own
        // extensive comments on this), so this policy exists to let a NEW notification type
        // honor that same rule instead of operators discovering the hard way that it doesn't.
        // Default false, since none of the 3 currently-live types need it (connection/error
        // events are rare and worth interrupting for).
        public bool DeferWhileTransmitting { get; set; } = false;

        // Added 2026-08-12: content-based suppression, distinct from RepeatSeconds' time-based
        // one -- when true, a formatted announcement identical to the last one actually
        // DELIVERED for this (EventType, DedupKey) is suppressed regardless of how much time has
        // passed. Default false (preserves existing behavior for every live type) -- meant for a
        // future notification whose fact can repeat with no real change (e.g. a station's report
        // read the same on successive periods); RepeatSeconds=0 with SuppressUnchanged=true means
        // "say it again immediately if it changed, never repeat it verbatim."
        public bool SuppressUnchanged { get; set; } = false;

        public NotificationPolicy Clone() => new NotificationPolicy
        {
            Enabled = Enabled,
            Priority = Priority,
            RepeatSeconds = RepeatSeconds,
            ThrottleMilliseconds = ThrottleMilliseconds,
            Template = Template,
            Timing = Timing,
            DeferWhileTransmitting = DeferWhileTransmitting,
            SuppressUnchanged = SuppressUnchanged,
        };
    }
}
