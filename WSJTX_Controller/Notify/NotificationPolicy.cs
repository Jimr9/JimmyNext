namespace WSJTX_Controller
{
    // Operator-facing labels (2026-09-04 notification pass): "Standard", "Important",
    // "Critical". The enum member names stay Normal/Important/Critical for INI back-compat
    // (notifyPriority_ keys already on disk name "Normal").
    //
    // There is NO beep / system sound tied to priority. Jimmy plays only its own configured
    // Options > Sounds WAV cues, fired independently at the relevant business-logic call site
    // (e.g. soundEnabled_Disconnected at the engine-lost publisher) -- confirmed with the user
    // 2026-08-11 that raw Windows beeps must never be produced. What priority actually controls:
    //   Standard  -> spoken per the configured condition + timing; no extra behaviour.
    //   Important -> spoken per condition + timing, and ALSO raised as an off-focus screen-reader
    //                announcement (UI Automation Notification) while Jimmy's window is not
    //                focused, IF the "Announce Important and Critical events when focus is
    //                elsewhere" option is on.
    //   Critical  -> spoken the instant it is submitted, bypassing the configured timing AND the
    //                during/outside-a-QSO gate (but NOT SpeakCondition.Never). Also ALWAYS
    //                attempts the off-focus screen-reader announcement -- regardless of that
    //                option -- so a safety event (CAT loss, engine failure, transmit-stop)
    //                reaches the operator even when Jimmy is in the background.
    public enum NotificationPriority
    {
        Normal,
        Important,
        Critical,
    }

    // What a delivered notification asks of the accessible-alert layer. Derived from
    // NotificationPriority by NotificationCenter (Critical wins; ErrorSeverity.Error is treated
    // as Critical). Routine RX/TX/QSO status always uses None.
    public enum AlertCue
    {
        None,
        Important,
        Critical,
    }

    // WHEN a notification that has passed policy/dedup is actually SPOKEN (its visible status
    // update and its Notification History entry are always immediate and independent of this).
    // Added 2026-09-02 (Item 1) as the single operator-facing timing control, replacing the
    // Timing + DeferWhileTransmitting pair below (still read for INI back-compat / migration --
    // see NotificationSettings.LoadFromIni). Deliberately an enum, not a set of bools, so it
    // can never express a contradiction, and so a future condition can be added as one new
    // value without breaking a saved notifySpeakWhen_ key that names an older one.
    public enum SpeakWhen
    {
        // Speak as soon as the notification is otherwise eligible -- today's only behaviour for
        // every currently-wired type. The default for every type, so nothing changes by default.
        Now,

        // Wait until the current receive opportunity has completed AND Jimmy has processed that
        // period's decodes -- SpeechCoordinator.OnReceiveCycleComplete(), driven from the end of
        // the Direct new-slot decode pass (WsjtxClient.Direct.cs), NOT merely "transmitting ==
        // false" and NOT a hard-coded FT8/FT4 duration. For a notice that must not talk over the
        // decode/QSO information that becomes available at that same moment.
        AfterRx,

        // Wait until PHYSICAL transmission has ended -- the authoritative radio.Transmitting
        // true -> false edge (SpeechCoordinator.OnPhysicalTxChanged(false)). Never curTxMsg /
        // TxNow / reply intent / callInProg -- those are not proof RF actually started.
        AfterTx,

        // Wait until the active QSO has ended -- callInProg cleared
        // (SpeechCoordinator.OnQsoActiveChanged(false)). Spans alternating RX and TX periods.
        AfterQso,

        // History only. Retained as a valid value for INI back-compat (a saved
        // notifySpeakWhen_==Never migrates to SpeakCondition.Never) and as a belt-and-suspenders
        // discard sentinel inside the coordinator. The new UI expresses "never spoken" through
        // SpeakCondition.Never, not here -- timing and eligibility are separate controls now.
        Never,

        // Added 2026-09-04. Deliver on the real radio.Transmitting false -> true edge
        // (SpeechCoordinator.OnPhysicalTxChanged(true)) -- an eligible/pending event is held
        // until transmission physically starts, never released on mere TxNow / reply intent.
        // Appended last on purpose: the "more-deferred-wins" Math.Max ordinal trick in
        // WsjtxClient.ShowStatus only ever compares Now/AfterRx/AfterTx/AfterQso, which keep
        // their original values; the coordinator handles TxStart explicitly, not by ordinal.
        TxStart,
    }

    // Added 2026-09-04. The operator-facing SPEECH ELIGIBILITY control: "under what operating
    // condition may this event be spoken at all". ORTHOGONAL to SpeakWhen, which only decides
    // the delivery boundary. They compose: an event fired mid-QSO with Condition=OutsideQsoOnly
    // and SpeakWhen=AfterQso is HELD (not discarded) and spoken once the QSO ends -- useful
    // deferred speech is modelled, not thrown away. Eligibility is re-checked against the real
    // QSO state at the delivery boundary, not only at submit time. Replaces the old two-value
    // DuringQso { SpeakNormally, Suppress } (migrated in NotificationSettings.LoadFromIni:
    // SpeakNormally -> Always, Suppress -> OutsideQsoOnly).
    public enum SpeakCondition
    {
        // Speak whether or not a QSO is active. The default for every type -> no change on
        // upgrade for a policy that had DuringQso.SpeakNormally.
        Always,

        // Speak only while a QSO is active (callInProg != null) at the delivery boundary. If the
        // event is/becomes eligible only outside a QSO, it is discarded, not queued. Pairing
        // this with SpeakWhen.AfterQso is contradictory (the condition can never hold at that
        // boundary) and the UI disables that combination.
        DuringQsoOnly,

        // Speak only while NO QSO is active at the delivery boundary. An occurrence that happens
        // during a QSO is NOT automatically discarded -- with SpeakWhen.AfterQso it is held and
        // spoken when the QSO ends; with SpeakWhen.Now it is dropped because "now" is inside a
        // QSO. Migration target for the old DuringQso.Suppress.
        OutsideQsoOnly,

        // Never spoken. The fact is still shown on screen and recorded in Notification History
        // as appropriate; it simply never nudges the screen reader. Critical priority does NOT
        // override this -- a truly must-hear condition should not be set to Never.
        Never,
    }

    // Which of Jimmy's two alternating FT8/FT4 slots a routine receive-side clause (the RX1/TX2
    // side name, and the "N available stations" count) is spoken for. Added 2026-09-05. Both
    // slots receive and decode; only ONE is Jimmy's current physical transmit slot. The scope is
    // resolved against the CURRENT role of the slot whose receive period just ended -- derived
    // from txFirst on every render -- so "RxSideOnly" / "TxSideOnly" automatically follow the
    // slots when the roles flip (e.g. the operator selects a station on the other slot), with no
    // settings change. "TxSideOnly" still means the current TX slot's RECEIVE/LISTEN information
    // -- it is silent only about that slot's routine receive summary, never about physical
    // transmit speech, which is a separate clause bound to the real transmit boundary.
    public enum ReceiveSideScope
    {
        Both,         // speak it for whichever slot's period just ended (today's behaviour)
        RxSideOnly,   // only when that slot currently holds the RX role
        TxSideOnly,   // only when that slot currently holds the TX role (while it is receiving)
        Neither,      // never speak this clause
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

        // Added 2026-09-02 (Item 1): the single operator-facing "when is this spoken" control.
        // Default Now = today's behaviour for every type. NotificationSettings.LoadFromIni
        // migrates a pre-existing Timing/DeferWhileTransmitting pair into this on first load.
        public SpeakWhen SpeakWhen { get; set; } = SpeakWhen.Now;

        // Added 2026-09-04: the operator-facing speech-eligibility control -- see the
        // SpeakCondition enum. Default Always = no change on upgrade for a policy that carried
        // the old DuringQso.SpeakNormally. LoadFromIni migrates a legacy notifyDuringQso_ key
        // (and a legacy notifySpeakWhen_==Never) into this.
        public SpeakCondition Condition { get; set; } = SpeakCondition.Always;

        // Added 2026-08-12. Superseded by SpeakWhen (2026-09-02) for operator configuration;
        // still present so an existing notifyTiming_ INI key migrates cleanly and so nothing
        // that referenced it breaks mid-refactor.
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
            SpeakWhen = SpeakWhen,
            Condition = Condition,
            Timing = Timing,
            DeferWhileTransmitting = DeferWhileTransmitting,
            SuppressUnchanged = SuppressUnchanged,
        };
    }
}
