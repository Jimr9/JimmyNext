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
            // Wave 2 (not yet wired to a call site). DeferWhileTransmitting=true: this fires
            // right as an over starts, so without deferring it would announce mid-Tx every
            // single time -- see NotificationPolicy.DeferWhileTransmitting's own comment.
            [NotificationEventType.QsoStarted] = new NotificationPolicy
            {
                Enabled = true,
                Priority = NotificationPriority.Normal,
                RepeatSeconds = 5,
                ThrottleMilliseconds = 0,
                Template = "Working {Callsign}",
                Timing = NotificationTiming.Immediate,
                DeferWhileTransmitting = true,
            },

            // Wave 1. Matches WsjtxClient.cs's RequestLog: ShowMessage($"Logged QSO with
            // {call}", false) exactly.
            [NotificationEventType.QsoCompleted] = new NotificationPolicy
            {
                Enabled = true,
                Priority = NotificationPriority.Normal,
                RepeatSeconds = 0,
                ThrottleMilliseconds = 0,
                Template = "Logged QSO with {Callsign}",
            },

            // Wave 2 (not yet wired to a call site).
            [NotificationEventType.TxMessageChanged] = new NotificationPolicy
            {
                Enabled = true,
                Priority = NotificationPriority.Normal,
                RepeatSeconds = 0,
                ThrottleMilliseconds = 0,
                Template = "Sending {Message}",
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
                Timing = NotificationTiming.NextPeriodBoundary,
                DeferWhileTransmitting = true,
            },

            // Wave 1. Matches WsjtxClient.Protocol.cs:54: ShowMessage("WSJT-X closed", true).
            [NotificationEventType.ConnectionClosed] = new NotificationPolicy
            {
                Enabled = true,
                Priority = NotificationPriority.Important,
                RepeatSeconds = 0,
                ThrottleMilliseconds = 0,
                Template = "WSJT-X closed",
            },

            // Wave 1. Matches WsjtxClient.Protocol.cs's HeartbeatNotRecd:
            // ShowMessage("WSJT-X disconnected", false) -- the accompanying
            // Sounds.PlaySoundEvent(soundEnabled_Disconnected, ...) stays untouched, called
            // independently at the same site.
            [NotificationEventType.ConnectionLost] = new NotificationPolicy
            {
                Enabled = true,
                Priority = NotificationPriority.Normal,
                RepeatSeconds = 0,
                ThrottleMilliseconds = 0,
                Template = "WSJT-X disconnected",
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
        };

        // Human-readable name shown in the configurable-notifications UI's first list --
        // mirrors HotkeyConfig.DisplayNames' exact role/shape for the Hotkeys panel.
        public static readonly Dictionary<NotificationEventType, string> DisplayNames =
            new Dictionary<NotificationEventType, string>
        {
            [NotificationEventType.QsoStarted] = "Starting a QSO",
            [NotificationEventType.QsoCompleted] = "QSO logged",
            [NotificationEventType.TxMessageChanged] = "Transmit message changed",
            [NotificationEventType.AwardsNeeded] = "Award needed",
            [NotificationEventType.ConnectionClosed] = "WSJT-X closed",
            [NotificationEventType.ConnectionLost] = "WSJT-X disconnected",
            [NotificationEventType.ErrorWarning] = "Error or warning",
            [NotificationEventType.ClockOutOfSync] = "Computer clock out of sync",
            [NotificationEventType.ClockSynced] = "Computer clock back in sync",
        };
    }
}
