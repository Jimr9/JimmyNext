namespace WSJTX_Controller
{
    // The one place a new notification type gets added. NotificationDefaults must gain a
    // matching entry before a type is usable -- until then NotificationSettings.LoadFromIni
    // simply never iterates it, which is what lets a future Jimmy version add a new type with
    // zero required INI migration (see NotificationSettings.cs's own header comment).
    public enum NotificationEventType
    {
        // ── Routine-status wording rows ──────────────────────────────────────────────────────
        // These do NOT publish through NotificationCenter. Their Template (and Enabled flag)
        // control the wording of a CLAUSE that WsjtxClient.ShowStatus composes into the ONE
        // routine RX/TX/QSO status utterance -- so "QSO started" and the receive-cycle counts
        // are never spoken as competing announcements (the W4MAA double-announcement lesson).
        // Their delivery timing / speech condition follow the global "Routine status speech"
        // controls, not a per-row setting.
        ReceiveCycleSummary,
        // 2026-09-05: the idle receive line's two structural phrases -- the state verb
        // ("Receiving" / "Transmitting") and the operating-mode descriptor ("Listen mode" /
        // "CQ mode", with the FT4 suffix) -- split out of the hard-coded "_base" skeleton into
        // their OWN independently toggleable/retimeable clauses. Before this, unchecking
        // "Receive cycle summary" dropped only the counts; "Receiving, Listen mode." kept being
        // spoken from _base with no way to turn it off. Default templates + Enabled reproduce
        // today's exact combined wording; all three compose into the one routine utterance.
        ReceiveStateSummary,
        OperatingModeSummary,
        // 2026-09-05: the advanced-call-layout side name ("RX1" / "TX2") of the idle receive
        // line, split out of the counts clause so it can be worded, and independently role-scoped
        // (ReceiveSideScope), on its own. NOT published; ShowStatus formats it from this type's
        // "{Side}" Template and composes it into the one routine utterance next to the count.
        ReceiveSideId,
        QsoStarted,
        QsoCompleted,
        TxMessageChanged,
        // 2026-09-04: the "received R minus 12, previous R R 7 3" QSO-progress detail, and the
        // "no decodes, check time, frequency, audio in" nudge. The state detection stays in
        // ShowStatus; only the final wording is a configurable clause routed through the
        // coordinator (default templates reproduce today's exact text).
        ReceivedReply,
        NoDecodeWarning,
        // Retained as the internal test vehicle for the deferred-delivery mechanism; NOT shown
        // as an operator row and never published -- award-needed info is now a field
        // ({Awards} / {AwardCount}) of ReceiveCycleSummary's template.
        AwardsNeeded,
        // Removed 2026-09-04: ConnectionClosed had no production publisher after the 2026-08-18
        // Direct-only cutover and ConnectionLost (three failed SNAPSHOT polls) fully represents
        // "the native engine is gone". A visible notification row must control real behaviour --
        // an inert one was removed rather than left in the list.
        ConnectionLost,
        ErrorWarning,
        ClockOutOfSync,
        ClockSynced,
        RadioCatRecovered,
        // Added 2026-09-02: the rig's CAT link going down, as its OWN typed event rather than a
        // generic ErrorWarningEvent carrying Nexus/Hamlib's raw cat_detail ("RPRT -20", escaped
        // newlines, rigctld backend wording) straight into "{Source}: {Detail}" speech. This
        // type's default template is concise, generic operator wording built from the CONFIGURED
        // connection (COM port / baud); the raw cat_detail is preserved for diagnostics/logging
        // and is still available as {Detail} for anyone who wants it in a custom template.
        RadioCatLost,

        // Added 2026-09-04: Jimmy's transmit was resumed automatically (the native engine's
        // own "wait and reply" cooperation resuming a stalled QSO after the other station
        // finally replied). A blind operator needs to know their radio started transmitting
        // again on its own -- previously a bare ShowMessage that still said "WSJT-X". Now a
        // real, configurable typed event with a {Callsign} field.
        AutoTxResume,

        // ── Station Watch / Smart QSO Start (2.0.63) ─────────────────────────────────────────
        // These carry TargetMonitor's own observations (StationWatch/TargetMonitor.cs) through
        // the SAME NotificationCenter/SpeechCoordinator path every other notification uses --
        // "Watch" is a scoped speech CATEGORY (see SpeechCoordinator's Station-Watch suppression
        // gate), not a different delivery mechanism. One generic "activity" type carries every
        // CQ/addressing/report/RRR/RR73/73/peer-observed fact (TargetMonitor.TargetObservation's
        // own Kind/Target/Peer/Value already distinguish them; a template author can still
        // reference {Kind} directly) rather than one enum member per fact, per the "avoid event
        // explosion" guidance -- ambiguous decodes and the waiting-progress nudge get their OWN
        // types only because those two need a DIFFERENT default policy (history-only / speech
        // off by default) than the rest of the family.
        StationWatchStarted,
        StationWatchStopped,
        StationWatchActivity,
        StationWatchAmbiguous,
        SmartStartWaiting,
        SmartStartTargetAvailable,
        SmartStartCallStarting,

        // Added 2026-09-07 (Smart Start narration pass). Smart Start is a decision-support
        // feature, not a full observational watch: these carry the small operational subset a
        // blind operator needs to follow what Jimmy is deciding while it waits on / calls a
        // captured target -- "request taken", "target is working someone else", "standing by",
        // "target engaged us / normal QSO sequencing has it". Every one is a normal configurable
        // NotificationCenter type (Options > Notifications: enable, template, when, condition);
        // the fuller CQ/report/RRR play-by-play stays StationWatchActivity's job. When a Station
        // Watch is ALSO active on the same call, the glue (WsjtxClient.StationWatch.cs) drops
        // Smart Start's bare "target busy" fact so the richer Station Watch line is not doubled.
        SmartStartArmed,
        SmartStartTargetBusy,
        SmartStartYielded,
        SmartStartEngaged,
    }
}
