using System.Collections.Generic;

namespace WSJTX_Controller
{
    // "This happened" -- the semantic fact a functional module publishes. Never a preformatted
    // sentence; NotificationCenter/NotificationTemplateEngine decide the wording, separately,
    // from ToTokens()'s structured data. Any singular/plural or phrase-building a payload needs
    // (e.g. AwardsNeededEvent.AwardSummary) is done by the event's OWN constructor/formatter
    // code, not inside a template string -- see NotificationTemplateEngine.cs.
    public interface INotificationEvent
    {
        NotificationEventType EventType { get; }

        // Identity to dedup on within NotificationPolicy.RepeatSeconds -- deliberately narrow
        // (e.g. callsign only, never band/frequency) so a value that legitimately drifts
        // between two otherwise-identical occurrences of the same real-world event doesn't
        // defeat dedup. null means "only one instance of this event type is ever meaningfully
        // pending at a time" (e.g. connection state).
        string DedupKey { get; }

        IReadOnlyDictionary<string, string> ToTokens();
    }

    // Routine-status wording rows (2026-09-04). These four types are NOT published through
    // NotificationCenter -- NotificationParkedEventTypesGuardTests enforces that no production
    // file constructs them. Instead WsjtxClient.ShowStatus reads each type's policy Template
    // (and Enabled flag) via NotificationSettings and formats a CLAUSE of the one routine
    // RX/TX/QSO status line from it. The token set here is exactly what ShowStatus can supply
    // at that clause's composition point -- Callsign / Band / Mode -- NOT the fuller
    // publish-point data model (no Grid/Country/Distance -- those need a network lookup that
    // may not have completed at status-render time). The classes still exist as the honest,
    // test-checked contract for what tokens the template validator will accept.
    public sealed class QsoStartedEvent : INotificationEvent
    {
        public string Callsign { get; }
        public string Band { get; }
        public string Mode { get; }

        public QsoStartedEvent(string callsign, string band, string mode)
        {
            Callsign = callsign ?? "";
            Band = band ?? "";
            Mode = mode ?? "";
        }

        public NotificationEventType EventType => NotificationEventType.QsoStarted;
        public string DedupKey => Callsign;

        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Callsign"] = Callsign,
            ["Band"] = Band,
            ["Mode"] = Mode,
        };
    }

    public sealed class QsoCompletedEvent : INotificationEvent
    {
        public string Callsign { get; }
        public string Band { get; }
        public string Mode { get; }

        public QsoCompletedEvent(string callsign, string band, string mode)
        {
            Callsign = callsign ?? "";
            Band = band ?? "";
            Mode = mode ?? "";
        }

        public NotificationEventType EventType => NotificationEventType.QsoCompleted;
        public string DedupKey => Callsign;

        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Callsign"] = Callsign,
            ["Band"] = Band,
            ["Mode"] = Mode,
        };
    }

    public sealed class TxMessageChangedEvent : INotificationEvent
    {
        public string Callsign { get; }   // ToCall of the Tx message; may be "CQ"
        public string Message { get; }
        public string Band { get; }
        public string Mode { get; }

        public TxMessageChangedEvent(string callsign, string message, string band = null, string mode = null)
        {
            Callsign = callsign ?? "";
            Message = message ?? "";
            Band = band ?? "";
            Mode = mode ?? "";
        }

        public NotificationEventType EventType => NotificationEventType.TxMessageChanged;
        public string DedupKey => Message;

        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Callsign"] = Callsign,
            ["Message"] = Message,
            ["Band"] = Band,
            ["Mode"] = Mode,
        };
    }

    // The receive-cycle summary clause of the routine status line (2026-09-04). Like the three
    // above, NOT published -- ShowStatus formats it from this type's policy Template. Tokens are
    // the counts/classification Jimmy already computes for the "N available stations" summary,
    // offered both as self-contained phrases (empty when N/A -- what the default template uses,
    // so the shipped wording is unchanged) and as bare counts for operators who want to write
    // their own phrasing. Award-needed info folds in here ({Awards} / {AwardCount}) -- there is
    // no separate award announcement.
    public sealed class ReceiveCycleSummaryEvent : INotificationEvent
    {
        public NotificationEventType EventType => NotificationEventType.ReceiveCycleSummary;
        public string DedupKey => null;
        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["AvailableCount"] = "", ["Stations"] = "", ["ToYou"] = "",
            ["NewDxcc"] = "", ["Wanted"] = "", ["Awards"] = "",
            ["NewDxccCount"] = "", ["WantedCount"] = "", ["AwardCount"] = "", ["Band"] = "",
        };
    }

    // The state verb of the routine idle receive line (2026-09-05). NOT published --
    // ShowStatus formats it from this type's policy Template ("{State}" by default).
    public sealed class ReceiveStateSummaryEvent : INotificationEvent
    {
        public NotificationEventType EventType => NotificationEventType.ReceiveStateSummary;
        public string DedupKey => null;
        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["State"] = "",
        };
    }

    // The advanced-call-layout side name ("RX1" / "TX2") of the routine idle receive line
    // (2026-09-05). NOT published -- ShowStatus formats it from this type's "{Side}" Template.
    public sealed class ReceiveSideIdEvent : INotificationEvent
    {
        public NotificationEventType EventType => NotificationEventType.ReceiveSideId;
        public string DedupKey => null;
        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Side"] = "",
        };
    }

    // The operating-mode descriptor of the routine idle receive line (2026-09-05). NOT
    // published -- ShowStatus formats it from this type's policy Template.
    public sealed class OperatingModeSummaryEvent : INotificationEvent
    {
        public NotificationEventType EventType => NotificationEventType.OperatingModeSummary;
        public string DedupKey => null;
        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Mode"] = "", ["SubMode"] = "",
        };
    }

    public sealed class ReceivedReplyEvent : INotificationEvent
    {
        public NotificationEventType EventType => NotificationEventType.ReceivedReply;
        public string DedupKey => null;
        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Received"] = "", ["Previous"] = "", ["Callsign"] = "",
        };
    }

    public sealed class NoDecodeWarningEvent : INotificationEvent
    {
        public NotificationEventType EventType => NotificationEventType.NoDecodeWarning;
        public string DedupKey => null;
        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Mode"] = "",
        };
    }

    public sealed class AwardsNeededEvent : INotificationEvent
    {
        public string Callsign { get; }
        public int AwardCount { get; }
        public IReadOnlyList<string> Awards { get; }
        public string AwardSummary { get; }   // pre-pluralized, e.g. "1 award needed"
        public string Country { get; }

        public AwardsNeededEvent(string callsign, int awardCount, IReadOnlyList<string> awards,
            string awardSummary, string country = null)
        {
            Callsign = callsign ?? "";
            AwardCount = awardCount;
            Awards = awards ?? System.Array.Empty<string>();
            AwardSummary = awardSummary ?? "";
            Country = country ?? "";
        }

        public NotificationEventType EventType => NotificationEventType.AwardsNeeded;
        public string DedupKey => Callsign;

        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Callsign"] = Callsign,
            ["AwardSummary"] = AwardSummary,
            ["AwardList"] = string.Join(", ", Awards),
            ["Country"] = Country,
        };
    }

    // Jimmy's transmit was resumed automatically for a stalled QSO (the native engine's own
    // wait-and-reply cooperation, seen via StatusMessage.TxEnableClk). Published from
    // WsjtxClient.HandleUnsolicitedTxResume. Callsign is the station Jimmy is now back to
    // working. DedupKey = Callsign so two different stalled QSOs resuming don't dedup each
    // other, but a rapid double-fire for the same one does.
    public sealed class AutoTxResumeEvent : INotificationEvent
    {
        public string Callsign { get; }

        public AutoTxResumeEvent(string callsign)
        {
            Callsign = callsign ?? "";
        }

        public NotificationEventType EventType => NotificationEventType.AutoTxResume;
        public string DedupKey => Callsign;

        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Callsign"] = Callsign,
        };
    }

    // Station Watch WatchStarted/WatchStopped (2.0.63) -- shares one class since both are just
    // "the watched callsign", published from TargetMonitor.Observed via StationWatch/
    // TargetMonitorGlue.cs. DedupKey = Target so starting/stopping the SAME call twice in a row
    // (shouldn't normally happen -- Start()/Stop() are themselves idempotent no-ops when already
    // in that state) doesn't double-announce.
    public sealed class StationWatchLifecycleEvent : INotificationEvent
    {
        private readonly NotificationEventType _type;
        public string Target { get; }

        public StationWatchLifecycleEvent(NotificationEventType type, string target)
        {
            _type = type;
            Target = target ?? "";
        }

        public NotificationEventType EventType => _type;
        public string DedupKey => Target;

        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Target"] = Target,
        };
    }

    // The CQ/addressing/report/RRR/RR73/73/peer-observed family -- one generic type (see
    // NotificationEventType.StationWatchActivity's own comment). `Phrase` is TargetMonitor
    // glue's own pre-worded natural-language phrase (built once, not by the template engine) so
    // the DEFAULT wording never depends on template-token composition; Target/Peer/Value/Kind
    // are also exposed for an operator who wants their own template. DedupKey folds in the raw
    // message so two genuinely different observations for the same target never dedup each other,
    // while an identical repeat (SuppressUnchanged, if the operator turns it on) still can.
    public sealed class StationWatchActivityEvent : INotificationEvent
    {
        public string Phrase { get; }
        public string Target { get; }
        public string Peer { get; }
        public string Value { get; }
        public string Kind { get; }

        public StationWatchActivityEvent(string phrase, string target, string peer, string value, string kind)
        {
            Phrase = phrase ?? "";
            Target = target ?? "";
            Peer = peer ?? "";
            Value = value ?? "";
            Kind = kind ?? "";
        }

        public NotificationEventType EventType => NotificationEventType.StationWatchActivity;
        public string DedupKey => $"{Target}|{Kind}|{Peer}|{Value}";

        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Phrase"] = Phrase,
            ["Target"] = Target,
            ["Peer"] = Peer,
            ["Value"] = Value,
            ["Kind"] = Kind,
        };
    }

    public sealed class StationWatchAmbiguousEvent : INotificationEvent
    {
        public string Target { get; }

        public StationWatchAmbiguousEvent(string target) { Target = target ?? ""; }

        public NotificationEventType EventType => NotificationEventType.StationWatchAmbiguous;
        public string DedupKey => Target;

        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Target"] = Target,
        };
    }

    // The repetitive "still waiting" progress nudge -- at most one per completed appropriate
    // receive opportunity (TargetMonitor.OnReceivePeriodComplete), never per poll tick. Also
    // used for the "waiting for a current decode / no recent decode" auto-start declines, which
    // are the same "Jimmy still hasn't got what it needs" idea. Speech OFF by default.
    // `Phrase` is a fully-worded sentence built at the call site (the default template is just
    // "{Phrase}") so the wording never depends on token composition; `{Target}` and the bare
    // "{Progress}" fragment ("1 of 2", or "" when not applicable) stay available for a custom
    // template.
    public sealed class SmartStartWaitingEvent : INotificationEvent
    {
        public string Target { get; }
        public string Phrase { get; }
        public string Progress { get; }

        public SmartStartWaitingEvent(string target, string phrase, string progress = "")
        {
            Target = target ?? "";
            Phrase = phrase ?? "";
            Progress = progress ?? "";
        }

        public NotificationEventType EventType => NotificationEventType.SmartStartWaiting;
        public string DedupKey => Target;

        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Target"] = Target,
            ["Phrase"] = Phrase,
            ["Progress"] = Progress,
        };
    }

    // Smart Start narration pass (2026-09-07). All four share the "just the target callsign"
    // shape; each is its own type only so the operator can turn them on/off and re-word them
    // independently in Options > Notifications (they carry genuinely distinct operational
    // meaning -- request taken / target busy elsewhere / Jimmy standing by / target engaged us).
    public sealed class SmartStartArmedEvent : INotificationEvent
    {
        public string Target { get; }
        public SmartStartArmedEvent(string target) { Target = target ?? ""; }
        public NotificationEventType EventType => NotificationEventType.SmartStartArmed;
        public string DedupKey => Target;
        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Target"] = Target,
        };
    }

    public sealed class SmartStartTargetBusyEvent : INotificationEvent
    {
        public string Target { get; }
        public SmartStartTargetBusyEvent(string target) { Target = target ?? ""; }
        public NotificationEventType EventType => NotificationEventType.SmartStartTargetBusy;
        public string DedupKey => Target;
        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Target"] = Target,
        };
    }

    public sealed class SmartStartYieldedEvent : INotificationEvent
    {
        public string Target { get; }
        public SmartStartYieldedEvent(string target) { Target = target ?? ""; }
        public NotificationEventType EventType => NotificationEventType.SmartStartYielded;
        public string DedupKey => Target;
        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Target"] = Target,
        };
    }

    public sealed class SmartStartEngagedEvent : INotificationEvent
    {
        public string Target { get; }
        public SmartStartEngagedEvent(string target) { Target = target ?? ""; }
        public NotificationEventType EventType => NotificationEventType.SmartStartEngaged;
        public string DedupKey => Target;
        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Target"] = Target,
        };
    }

    public sealed class SmartStartTargetAvailableEvent : INotificationEvent
    {
        public string Target { get; }

        public SmartStartTargetAvailableEvent(string target) { Target = target ?? ""; }

        public NotificationEventType EventType => NotificationEventType.SmartStartTargetAvailable;
        public string DedupKey => Target;

        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Target"] = Target,
        };
    }

    public sealed class SmartStartCallStartingEvent : INotificationEvent
    {
        public string Target { get; }

        public SmartStartCallStartingEvent(string target) { Target = target ?? ""; }

        public NotificationEventType EventType => NotificationEventType.SmartStartCallStarting;
        public string DedupKey => Target;

        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Target"] = Target,
        };
    }

    public sealed class ConnectionLostEvent : INotificationEvent
    {
        public string Detail { get; }

        public ConnectionLostEvent(string detail = "")
        {
            Detail = detail ?? "";
        }

        public NotificationEventType EventType => NotificationEventType.ConnectionLost;
        public string DedupKey => null;

        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Detail"] = Detail,
        };
    }

    public enum ErrorSeverity
    {
        Warning,
        Error,
    }

    public sealed class ErrorWarningEvent : INotificationEvent
    {
        public ErrorSeverity Severity { get; }
        public string Source { get; }   // e.g. "Radio", "Native engine"
        public string Detail { get; }

        public ErrorWarningEvent(ErrorSeverity severity, string source, string detail)
        {
            Severity = severity;
            Source = source ?? "";
            Detail = detail ?? "";
        }

        public NotificationEventType EventType => NotificationEventType.ErrorWarning;
        public string DedupKey => Source + "|" + Detail;

        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Source"] = Source,
            ["Detail"] = Detail,
            ["Severity"] = Severity.ToString(),
        };
    }

    // Clock-sync notification, 2026-08-12: WsjtxClient.BandAudio.cs's CalcAvgTimeOffset is the
    // one and only publisher of either of these -- see its own comment for the transition-gate
    // logic (fires once per bad<->good transition, never every period). DedupKey is null on
    // both: there is only ever ONE meaningfully-pending clock condition at a time (matching
    // ConnectionClosed/ConnectionLost's own null-DedupKey convention), so
    // NotificationDedupThrottle's RepeatSeconds (a real config option, default 60s on both --
    // see NotificationDefaults.cs) is a pure flap-guard backstop against rapid oscillation right
    // at the threshold, not the mechanism that prevents "every period" chatter -- the transition
    // gate in CalcAvgTimeOffset already does that structurally.
    public sealed class ClockOutOfSyncEvent : INotificationEvent
    {
        public double OffsetSeconds { get; }
        public string Mode { get; }

        public ClockOutOfSyncEvent(double offsetSeconds, string mode)
        {
            OffsetSeconds = offsetSeconds;
            Mode = mode ?? "";
        }

        public NotificationEventType EventType => NotificationEventType.ClockOutOfSync;
        public string DedupKey => null;

        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["ClockOffset"] = OffsetSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
            ["Mode"] = Mode,
        };
    }

    public sealed class ClockSyncedEvent : INotificationEvent
    {
        public string Mode { get; }

        public ClockSyncedEvent(string mode)
        {
            Mode = mode ?? "";
        }

        public NotificationEventType EventType => NotificationEventType.ClockSynced;
        public string DedupKey => null;

        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Mode"] = Mode,
        };
    }

    // Recovery companion to ErrorWarningEvent's "Radio CAT link lost" (WsjtxClient.Direct.cs's
    // DirectApplyStatus -- same transition-gated check, opposite edge: only announces when
    // _lastCatOk transitions false -> true, never on the first-ever reading of a session). Its
    // own dedicated type rather than reusing ErrorWarningEvent -- this isn't
    // an error/warning at all (ErrorSeverity has no "recovered" value, and forcing good news
    // through an error-shaped "{Source}: {Detail}" template would read oddly). DedupKey null:
    // only ever one meaningfully-pending CAT-link condition at a time, matching
    // ConnectionLost/ClockSynced's own null-DedupKey convention.
    public sealed class RadioCatRecoveredEvent : INotificationEvent
    {
        public NotificationEventType EventType => NotificationEventType.RadioCatRecovered;
        public string DedupKey => null;
        public IReadOnlyDictionary<string, string> ToTokens() => EmptyTokens.Instance;
    }

    // The rig's CAT link going down. Its own type (not ErrorWarningEvent) so the default
    // announcement is concise, generic operator wording -- Nexus/Hamlib's raw cat_detail
    // ("RPRT -20", escaped newline data, rigctld/backend phrasing) never reaches speech through
    // the generic "{Source}: {Detail}" error template. The full cat_detail is still carried on
    // Detail for diagnostics/logging and is offered as the {Detail} template variable for
    // anyone who deliberately wants it. {Connection} is a pre-built phrase (formatter code, not
    // a template expression -- same rule AwardsNeededEvent.AwardSummary follows) describing the
    // CONFIGURED CAT connection: " on COM4 at 115200 baud", " on COM4", or "" when neither is
    // configured (e.g. an external rigctld). Nothing here is rig-brand-specific.
    public sealed class RadioCatLostEvent : INotificationEvent
    {
        public string RigModel { get; }   // Hamlib rig-model id as configured (may be ""/a number)
        public string ComPort { get; }
        public string BaudRate { get; }
        public string Detail { get; }     // full Nexus/Hamlib cat_detail -- diagnostics / opt-in {Detail}

        public RadioCatLostEvent(string rigModel, string comPort, string baudRate, string detail)
        {
            RigModel = rigModel ?? "";
            ComPort = comPort ?? "";
            BaudRate = baudRate ?? "";
            Detail = detail ?? "";
        }

        public NotificationEventType EventType => NotificationEventType.RadioCatLost;
        // Only ever one meaningfully-pending CAT-link condition at a time, matching
        // RadioCatRecovered/ConnectionLost's own null-DedupKey convention.
        public string DedupKey => null;

        // " on COM4 at 115200 baud" / " on COM4" / "" -- leading space so the template reads
        // naturally when it's empty ("The radio is not responding").
        public string ConnectionPhrase
        {
            get
            {
                bool hasPort = !string.IsNullOrWhiteSpace(ComPort);
                bool hasBaud = !string.IsNullOrWhiteSpace(BaudRate);
                if (hasPort && hasBaud) return $" on {ComPort.Trim()} at {BaudRate.Trim()} baud";
                if (hasPort) return $" on {ComPort.Trim()}";
                return "";
            }
        }

        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["RigModel"] = RigModel,
            ["ComPort"] = ComPort,
            ["BaudRate"] = BaudRate,
            ["Connection"] = ConnectionPhrase,
            ["Detail"] = Detail,
        };
    }

    // Shared empty-token-dictionary singleton for events with nothing to substitute --
    // avoids allocating a fresh empty Dictionary on every ToTokens() call for these.
    internal static class EmptyTokens
    {
        public static readonly IReadOnlyDictionary<string, string> Instance =
            new Dictionary<string, string>();
    }
}
