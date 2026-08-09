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
            // Wave 2 (not yet wired to a call site).
            [NotificationEventType.QsoStarted] = new NotificationPolicy
            {
                Enabled = true,
                Priority = NotificationPriority.Normal,
                RepeatSeconds = 5,
                ThrottleMilliseconds = 0,
                Template = "Working {Callsign}",
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
                Template = "{Summary}",
            },

            // Wave 2 (not yet wired to a call site). RepeatSeconds/ThrottleMilliseconds
            // deliberately independent of AwardTagger's own 30s sound cooldown
            // (WsjtxClient.AwardAlertCooldownSecs) -- see Awards/AwardTagger.cs's
            // CheckAwardAlert call site comment once wired.
            [NotificationEventType.AwardsNeeded] = new NotificationPolicy
            {
                Enabled = true,
                Priority = NotificationPriority.Normal,
                RepeatSeconds = 60,
                ThrottleMilliseconds = 3000,
                Template = "{Callsign}, {AwardSummary}",
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
        };
    }
}
