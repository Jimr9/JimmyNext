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
        public string RawMessage { get; }
        public string Summary { get; }    // pre-built by the caller (see plan's Wave 2 note)

        public TxMessageChangedEvent(string callsign, string rawMessage, string summary)
        {
            Callsign = callsign ?? "";
            RawMessage = rawMessage ?? "";
            Summary = summary ?? "";
        }

        public NotificationEventType EventType => NotificationEventType.TxMessageChanged;
        public string DedupKey => RawMessage;

        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Callsign"] = Callsign,
            ["Summary"] = Summary,
        };
    }

    public sealed class AwardsNeededEvent : INotificationEvent
    {
        public string Callsign { get; }
        public int AwardCount { get; }
        public IReadOnlyList<string> Awards { get; }
        public string AwardSummary { get; }   // pre-pluralized, e.g. "1 award needed"

        public AwardsNeededEvent(string callsign, int awardCount, IReadOnlyList<string> awards, string awardSummary)
        {
            Callsign = callsign ?? "";
            AwardCount = awardCount;
            Awards = awards ?? System.Array.Empty<string>();
            AwardSummary = awardSummary ?? "";
        }

        public NotificationEventType EventType => NotificationEventType.AwardsNeeded;
        public string DedupKey => Callsign;

        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Callsign"] = Callsign,
            ["AwardSummary"] = AwardSummary,
            ["AwardList"] = string.Join(", ", Awards),
        };
    }

    public sealed class ConnectionClosedEvent : INotificationEvent
    {
        public NotificationEventType EventType => NotificationEventType.ConnectionClosed;
        public string DedupKey => null;
        public IReadOnlyDictionary<string, string> ToTokens() => EmptyTokens.Instance;
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

    // Shared empty-token-dictionary singleton for events with nothing to substitute --
    // avoids allocating a fresh empty Dictionary on every ToTokens() call for these.
    internal static class EmptyTokens
    {
        public static readonly IReadOnlyDictionary<string, string> Instance =
            new Dictionary<string, string>();
    }
}
