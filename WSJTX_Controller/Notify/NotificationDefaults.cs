using System.Collections.Generic;

namespace WSJTX_Controller
{
    // The single, code-only source of truth for every notification type's default policy --
    // authoritative regardless of INI state. NotificationSettings.LoadFromIni starts from a
    // Clone() of each entry here and only overlays what the INI actually contains, so a type
    // with no INI keys at all (every type, on a fresh install; any NEW type added in a future
    // Jimmy version, forever, until an operator chooses to override it) behaves exactly as
    // defined here with zero required migration.
    //
    // Wave 1 templates/priorities are copied verbatim from the exact wording/sound bool at each
    // existing call site being migrated (see the plan's "Migration slice" section) -- this wave
    // is meant to be inaudibly different from today's behavior. Wave 2 templates are new speech
    // (see WsjtxClient.cs's SetCallInProg/ProcessTxStart and Awards/AwardTagger.cs's
    // CheckAwardAlert) and are not wired to any call site yet.
    public static class NotificationDefaults
    {
        public static readonly Dictionary<NotificationEventType, NotificationPolicy> Policies =
            new Dictionary<NotificationEventType, NotificationPolicy>
        {
            // Routine-status wording rows (2026-09-04). NOT published -- WsjtxClient.ShowStatus
            // formats each of these as a CLAUSE of the one routine RX/TX/QSO status line from
            // its Template. The default templates below reproduce today's exact woven wording;
            // SpeakWhen / Condition on these rows are ignored (the line's timing follows the
            // global "Routine status speech" controls). Enabled=false drops the clause.

            // The idle "N available stations" summary line. The default template composes to
            // byte-identically what ShowStatus builds today. See ReceiveCycleSummaryEvent /
            // NotificationVariableRegistry for what each token expands to.
            [NotificationEventType.ReceiveCycleSummary] = new NotificationPolicy
            {
                Enabled = true,
                // 2026-09-05: just the counts now. The state verb and the operating-mode
                // descriptor became their own rows (ReceiveStateSummary / OperatingModeSummary)
                // so each can be turned off or retimed on its own -- ShowStatus composes all
                // three (plus any beginner prompt hint) back into ONE utterance, so the default
                // spoken line is unchanged: "Receiving, no available stations, Listen mode."
                Template = "{AvailableCount} {Stations}{ToYou}{NewDxcc}{Wanted}{Awards}",
                // Batched to the end of the receive-decode pass, coalesced to the latest count --
                // the same cadence ShowStatus has always used for this summary so it doesn't
                // announce "3 available" then "19 available" seconds apart.
                SpeakWhen = SpeakWhen.AfterRx,
                Condition = SpeakCondition.Always,
            },

            // The state verb of the idle receive line. Default template = the bare word, so it
            // composes to byte-identically today's line; Enabled=false removes "Receiving" /
            // "Transmitting" from BOTH the spoken utterance and the visible status line.
            [NotificationEventType.ReceiveStateSummary] = new NotificationPolicy
            {
                Enabled = true,
                Template = "{State}",
                SpeakWhen = SpeakWhen.AfterRx,
                Condition = SpeakCondition.Always,
            },

            // The operating-mode descriptor of the idle receive line ("Listen mode" / "CQ mode",
            // plus ", FT4" on FT4). Default template composes to today's exact phrase;
            // Enabled=false removes it from the utterance and the visible line.
            [NotificationEventType.OperatingModeSummary] = new NotificationPolicy
            {
                Enabled = true,
                Template = "{Mode} mode{SubMode}",
                SpeakWhen = SpeakWhen.AfterRx,
                Condition = SpeakCondition.Always,
            },

            // The advanced-call-layout side name ("RX1" / "TX2") of the idle receive line. Split
            // out of the counts clause 2026-09-05 so the operator can word it, and role-scope it
            // (Options > Notifications > Global speech behaviour: "Receive side name"), on its
            // own. Same AfterRx cadence as the count so the two coalesce into one utterance;
            // default "{Side}" + Both scope reproduces today's "RX1, N available stations" line.
            [NotificationEventType.ReceiveSideId] = new NotificationPolicy
            {
                Enabled = true,
                Template = "{Side}",
                SpeakWhen = SpeakWhen.AfterRx,
                Condition = SpeakCondition.Always,
            },

            // Clean semantic phrases -- NO leading/trailing structural punctuation. ShowStatus
            // adds the ", " separators when it weaves them into the visible line; the composer
            // (SpeechCoordinator.Compose) re-joins them naturally for speech, so each also reads
            // correctly when the operator retimes it to speak on its own boundary
            // ("K4YT logged.", "sending 73.").

            // Shown when Jimmy starts answering a station.
            [NotificationEventType.QsoStarted] = new NotificationPolicy
            {
                Enabled = true,
                Template = "Working {Callsign}, replying.",
            },

            // The moment a QSO is logged.
            [NotificationEventType.QsoCompleted] = new NotificationPolicy
            {
                Enabled = true,
                Template = "{Callsign} logged",
            },

            // What is going out on this transmission.
            [NotificationEventType.TxMessageChanged] = new NotificationPolicy
            {
                Enabled = true,
                Template = "sending {Message}",
            },

            // The "received R minus 12, previous R R 7 3" QSO-progress detail. {Received} is a
            // pre-built clean phrase (formatter code, like AwardsNeeded's AwardSummary) covering
            // received + previous together; the default template just speaks it. Enabled=false
            // drops it.
            [NotificationEventType.ReceivedReply] = new NotificationPolicy
            {
                Enabled = true,
                Template = "{Received}",
            },

            // Appended when several receive periods pass with nothing decoded.
            [NotificationEventType.NoDecodeWarning] = new NotificationPolicy
            {
                Enabled = true,
                Template = "no decodes, check time, frequency, audio in",
            },

            // Wave 2 (not yet wired to a call site). RepeatSeconds/ThrottleMilliseconds
            // deliberately independent of AwardTagger's own 30s sound cooldown
            // (WsjtxClient.AwardAlertCooldownSecs) -- see Awards/AwardTagger.cs's
            // CheckAwardAlert call site comment once wired. Timing=NextPeriodBoundary +
            // DeferWhileTransmitting=true: this is exactly the "batch, don't interrupt" case --
            // ShowStatus()'s own existing routine "needed" summary already proves this is the
            // right cadence for award info (see the notification-consolidation history in
            // WsjtxClient.Display.cs), so a future wiring of this event should behave the same
            // way, not compete with it.
            [NotificationEventType.AwardsNeeded] = new NotificationPolicy
            {
                Enabled = true,
                Priority = NotificationPriority.Normal,
                RepeatSeconds = 60,
                ThrottleMilliseconds = 3000,
                Template = "{Callsign}, {AwardSummary}",
                // "Batch, don't interrupt" -- SpeakWhen.AfterRx, spoken once the receive cycle's
                // decodes are in, coalescing to the latest during a pileup. (Equivalent to the
                // old NextPeriodBoundary + DeferWhileTransmitting=true.)
                SpeakWhen = SpeakWhen.AfterRx,
                Timing = NotificationTiming.NextPeriodBoundary,
                DeferWhileTransmitting = true,
            },

            // Wave 1. Originally matched WsjtxClient.Protocol.cs's classic-UDP-path
            // HeartbeatNotRecd wording (ShowMessage("WSJT-X disconnected", false)) -- that
            // handler was removed in the 2026-08-18 Direct-only cutover. The live publisher today
            // is WsjtxClient.Direct.cs's DirectHandlePollFailure (three consecutive failed
            // SNAPSHOT polls to the native engine's own control port) -- Codex Audit 02 finding,
            // 2026-08-21: "WSJT-X disconnected" was actively misleading (nothing named WSJT-X is
            // involved in a Direct-mode disconnect), corrected to match what actually happens.
            // The accompanying Sounds.PlaySoundEvent(soundEnabled_Disconnected, ...) stays
            // untouched, called independently at the same site.
            [NotificationEventType.ConnectionLost] = new NotificationPolicy
            {
                Enabled = true,
                // Item 1: three consecutive failed SNAPSHOT polls -- the engine is unreachable.
                // Critical so it is never held behind routine RX/TX speech.
                Priority = NotificationPriority.Critical,
                RepeatSeconds = 0,
                ThrottleMilliseconds = 0,
                Template = "Native engine disconnected",
            },

            // Wave 1. Matches the shape already shared by every existing error ShowMessage call
            // site (Controller.cs: "Radio: {LastError}" sound:false, "Radio CAT link lost:
            // {LastError}" sound:true, etc.) -- kept as one type with {Source}/{Detail} tokens
            // rather than one enum member per error source, since sources are open-ended
            // (today: radio CAT, native engine, audio level, logbook sync -- more later) and
            // splitting them would fight the "new types need no INI migration" goal. Priority
            // here is the policy DEFAULT for ErrorSeverity.Warning; NotificationCenter.Publish
            // additionally forces Important (a beep) for ErrorSeverity.Error regardless of this
            // policy value -- see its own comment -- which is what lets both existing call
            // sites (one sound:false, one sound:true) migrate with their exact existing
            // behavior preserved under a single shared policy.
            [NotificationEventType.ErrorWarning] = new NotificationPolicy
            {
                Enabled = true,
                Priority = NotificationPriority.Normal,
                RepeatSeconds = 0,
                ThrottleMilliseconds = 0,
                Template = "{Source}: {Detail}",
            },

            // Added 2026-08-12: reuses WsjtxClient.BandAudio.cs's existing timeOffset/
            // maxTimeOffset (already governed the pre-existing ShowStatus() "check clock time"
            // text) -- see CalcAvgTimeOffset's own comment for the transition-gate that keeps
            // this from firing every period while the clock stays bad. Immediate/Important:
            // operationally significant (a bad clock silently costs missed decodes/QSOs on
            // BOTH FT8 and FT4), matching every other Important type's own precedent of not
            // deferring for something worth interrupting for. RepeatSeconds=60 is a flap-guard
            // backstop, not the primary anti-chatter mechanism (that's the transition gate
            // itself) -- see ClockOutOfSyncEvent's own comment.
            [NotificationEventType.ClockOutOfSync] = new NotificationPolicy
            {
                Enabled = true,
                Priority = NotificationPriority.Important,
                RepeatSeconds = 60,
                ThrottleMilliseconds = 0,
                Template = "Computer clock is out of sync, offset {ClockOffset} seconds.",
                Timing = NotificationTiming.Immediate,
                DeferWhileTransmitting = false,
            },

            // Companion recovery notice -- independently enable/disable-able (the operator
            // requirement: "optionally publish"), off the SAME transition gate.
            [NotificationEventType.ClockSynced] = new NotificationPolicy
            {
                Enabled = true,
                Priority = NotificationPriority.Normal,
                RepeatSeconds = 60,
                ThrottleMilliseconds = 0,
                Template = "Computer clock timing is back within range.",
                Timing = NotificationTiming.Immediate,
                DeferWhileTransmitting = false,
            },

            // Added 2026-08-19 (notification-system-consistency pass): recovery companion to
            // the "Radio CAT link lost" ErrorWarningEvent (WsjtxClient.Direct.cs's
            // DirectApplyStatus, since 2026-08-20 -- originally Controller.cs's
            // radioPollTimer_Tick), which was already wired. Normal priority (not Important) -- a recovery is
            // reassuring news, not something that needs to interrupt/beep the way the loss
            // itself does, matching ClockSynced's own Normal-priority precedent for its
            // recovery counterpart.
            [NotificationEventType.RadioCatRecovered] = new NotificationPolicy
            {
                Enabled = true,
                Priority = NotificationPriority.Normal,
                RepeatSeconds = 0,
                ThrottleMilliseconds = 0,
                // Reworded 2026-09-02 (2.0.58 CAT-notification pass): "restored" reads as the
                // clean counterpart to "lost" below.
                Template = "Radio CAT link restored.",
            },

            // Added 2026-09-02 (2.0.58): the rig's CAT link going down, as its own typed event
            // instead of a generic ErrorWarningEvent carrying Nexus/Hamlib's raw cat_detail
            // ("RPRT -20", escaped newlines, rigctld backend wording) straight into speech.
            // Important (the audible cue + off-focus UIA alert), matching the ErrorSeverity.Error
            // this replaces at the WsjtxClient.Direct.cs DirectApplyStatus call site. The default
            // wording is concise and rig-brand-neutral, built from the CONFIGURED connection
            // (COM port / baud) via the {Connection} phrase -- the full cat_detail stays in the
            // diagnostic log and is still available as {Detail} for a hand-edited template.
            [NotificationEventType.RadioCatLost] = new NotificationPolicy
            {
                Enabled = true,
                // Item 1: the operator's radio has effectively gone dark -- Critical, spoken at
                // once regardless of any configured SpeakWhen on other notification types.
                Priority = NotificationPriority.Critical,
                RepeatSeconds = 0,
                ThrottleMilliseconds = 0,
                Template = "Radio CAT link lost. The radio{Connection} is not responding. " +
                           "Check that the radio is on and the CAT connection is available.",
            },

            // Added 2026-09-04: replaces the bare ShowMessage("WSJT-X resumed calling {call}
            // automatically") in WsjtxClient.HandleUnsolicitedTxResume. Important (audible cue /
            // off-focus alert) because a blind operator needs to know transmission restarted on
            // its own; spoken immediately, whether or not other routine speech is pending, but
            // NOT Critical -- it is not a safety fault. Only ever fires with a QSO in progress.
            [NotificationEventType.AutoTxResume] = new NotificationPolicy
            {
                Enabled = true,
                Priority = NotificationPriority.Important,
                RepeatSeconds = 5,
                ThrottleMilliseconds = 0,
                Template = "Resumed calling {Callsign} automatically.",
                SpeakWhen = SpeakWhen.Now,
                Condition = SpeakCondition.Always,
            },

            // ── Station Watch / Smart QSO Start (2.0.63) ────────────────────────────────────
            [NotificationEventType.StationWatchStarted] = new NotificationPolicy
            {
                Enabled = true,
                Priority = NotificationPriority.Normal,
                Template = "Watching {Target}.",
                SpeakWhen = SpeakWhen.Now,
                Condition = SpeakCondition.Always,
            },
            [NotificationEventType.StationWatchStopped] = new NotificationPolicy
            {
                Enabled = true,
                Priority = NotificationPriority.Normal,
                Template = "Stopped watching {Target}.",
                SpeakWhen = SpeakWhen.Now,
                Condition = SpeakCondition.Always,
            },
            // One generic activity type for the CQ/addressing/report/RRR/RR73/73/peer-observed
            // family -- {Phrase} is TargetMonitor's own already-worded natural phrase for the
            // observation ("K4YT working W1ABC, minus 8.", "K4YT RR73.", "W1ABC 73."); {Target}/
            // {Peer}/{Value}/{Kind} are also available for an operator who wants their own wording.
            [NotificationEventType.StationWatchActivity] = new NotificationPolicy
            {
                Enabled = true,
                Priority = NotificationPriority.Normal,
                Template = "{Phrase}",
                SpeakWhen = SpeakWhen.Now,
                Condition = SpeakCondition.Always,
            },
            // Heard the watched target, but the payload didn't parse into a known form --
            // history/status only by default (Enabled so it still shows in Notification History
            // and the visible status line; Condition.Never so it is not spoken unless the
            // operator opts in).
            [NotificationEventType.StationWatchAmbiguous] = new NotificationPolicy
            {
                Enabled = true,
                Priority = NotificationPriority.Normal,
                Template = "{Target}, unclear.",
                SpeakWhen = SpeakWhen.Now,
                Condition = SpeakCondition.Never,
            },
            // The repetitive "still waiting" progress nudge -- speech OFF by default (it fires
            // once per silent receive opportunity and would be chatty); still visible/recorded
            // so the operator can check progress without turning speech on for it. {Phrase} is a
            // full sentence built at the call site.
            [NotificationEventType.SmartStartWaiting] = new NotificationPolicy
            {
                Enabled = true,
                Priority = NotificationPriority.Normal,
                Template = "{Phrase}",
                SpeakWhen = SpeakWhen.Now,
                Condition = SpeakCondition.Never,
            },
            // Smart Start narration pass (2026-09-07): meaningful state changes speak by
            // default so the operator is never left wondering whether Jimmy is still working on
            // a captured target -- but only ONE concise line per real state change, and all
            // fully reconfigurable (including Condition = Never) in Options > Notifications.
            [NotificationEventType.SmartStartArmed] = new NotificationPolicy
            {
                Enabled = true,
                Priority = NotificationPriority.Normal,
                Template = "Waiting to work {Target}.",
                SpeakWhen = SpeakWhen.Now,
                Condition = SpeakCondition.Always,
            },
            // The target is mid-exchange with (or being called by) someone else. Default wording
            // (2026-09-07) names the other station and its report when known -- "{Phrase}" renders
            // "X is working Y, minus 8." / "X is working Y." / "X is working another station."
            // depending on what was decoded. DedupKey folds in the peer, so a move to a NEW
            // station re-announces immediately; RepeatSeconds still collapses several decodes for
            // the SAME peer in one exchange down to one line.
            [NotificationEventType.SmartStartTargetBusy] = new NotificationPolicy
            {
                Enabled = true,
                Priority = NotificationPriority.Normal,
                RepeatSeconds = 30,
                Template = "{Phrase}",
                SpeakWhen = SpeakWhen.Now,
                Condition = SpeakCondition.Always,
            },
            [NotificationEventType.SmartStartTargetAvailable] = new NotificationPolicy
            {
                Enabled = true,
                Priority = NotificationPriority.Normal,
                Template = "{Target} appears available.",
                SpeakWhen = SpeakWhen.Now,
                Condition = SpeakCondition.Always,
            },
            // Jimmy was calling and has ceased our call because the target turned to another
            // station first -- Smart Start stays armed and waits for a real availability signal.
            [NotificationEventType.SmartStartYielded] = new NotificationPolicy
            {
                Enabled = true,
                Priority = NotificationPriority.Normal,
                Template = "{Target} is busy; standing by.",
                SpeakWhen = SpeakWhen.Now,
                Condition = SpeakCondition.Always,
            },
            [NotificationEventType.SmartStartCallStarting] = new NotificationPolicy
            {
                Enabled = true,
                Priority = NotificationPriority.Normal,
                Template = "Calling {Target}.",
                SpeakWhen = SpeakWhen.Now,
                Condition = SpeakCondition.Always,
            },
            // The target has addressed our callsign: Smart Start's job is done and the normal
            // QSO sequencer owns the contact from here (a Station Watch on this same call also
            // stops -- see WsjtxClient.StationWatch.cs).
            [NotificationEventType.SmartStartEngaged] = new NotificationPolicy
            {
                Enabled = true,
                Priority = NotificationPriority.Normal,
                Template = "{Target} answered you; switching to normal QSO.",
                SpeakWhen = SpeakWhen.Now,
                Condition = SpeakCondition.Always,
            },
        };

        // Human-readable name shown in the configurable-notifications UI's first list --
        // mirrors HotkeyConfig.DisplayNames' exact role/shape for the Hotkeys panel.
        public static readonly Dictionary<NotificationEventType, string> DisplayNames =
            new Dictionary<NotificationEventType, string>
        {
            [NotificationEventType.ReceiveCycleSummary] = "Receive cycle summary",
            [NotificationEventType.ReceiveStateSummary] = "Receive or transmit state",
            [NotificationEventType.OperatingModeSummary] = "Operating mode announcement",
            [NotificationEventType.ReceiveSideId] = "Receive side name",
            [NotificationEventType.QsoStarted] = "QSO started",
            [NotificationEventType.QsoCompleted] = "QSO logged",
            [NotificationEventType.TxMessageChanged] = "Transmit message",
            [NotificationEventType.ReceivedReply] = "Received reply detail",
            [NotificationEventType.NoDecodeWarning] = "No decodes warning",
            [NotificationEventType.AwardsNeeded] = "Award needed on a spotted station",
            [NotificationEventType.ConnectionLost] = "Native engine disconnected",
            [NotificationEventType.ErrorWarning] = "Operating error or warning",
            [NotificationEventType.ClockOutOfSync] = "Computer clock out of sync",
            [NotificationEventType.ClockSynced] = "Computer clock back in sync",
            [NotificationEventType.RadioCatRecovered] = "Radio CAT link recovered",
            [NotificationEventType.RadioCatLost] = "Radio CAT link lost",
            [NotificationEventType.AutoTxResume] = "Automatic transmit resume",
            [NotificationEventType.StationWatchStarted] = "Station Watch started",
            [NotificationEventType.StationWatchStopped] = "Station Watch stopped",
            [NotificationEventType.StationWatchActivity] = "Station Watch target activity",
            [NotificationEventType.StationWatchAmbiguous] = "Station Watch target, unclear decode",
            [NotificationEventType.SmartStartWaiting] = "Smart Start waiting progress",
            [NotificationEventType.SmartStartTargetAvailable] = "Smart Start target appears available",
            [NotificationEventType.SmartStartCallStarting] = "Smart Start calling target",
            [NotificationEventType.SmartStartArmed] = "Smart Start armed (request taken)",
            [NotificationEventType.SmartStartTargetBusy] = "Smart Start target working another station",
            [NotificationEventType.SmartStartYielded] = "Smart Start standing by (target busy)",
            [NotificationEventType.SmartStartEngaged] = "Smart Start target engaged (QSO takeover)",
        };
    }
}
