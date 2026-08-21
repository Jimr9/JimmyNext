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

    // Field sets below were enriched 2026-08-12 (configurable-notification-templates feature)
    // to cover what Jimmy's real data model can actually supply at each event's natural
    // publish point -- NOT a reason by itself to wire a new Publish() call site. QsoStarted/
    // QsoCompleted/TxMessageChanged/AwardsNeeded remain deliberately parked (see
    // NotificationDefaults.cs's own header comment on the W4MAA double-announcement lesson);
    // this only makes their TEMPLATES fully configurable in the new UI ahead of time, using
    // honest field names rather than inventing data Jimmy can't reliably provide (no CQ/ITU
    // zone or QRZ-sourced US state here -- those need an async network lookup that may not have
    // completed, so they're intentionally left out of the token set entirely).
    public sealed class QsoStartedEvent : INotificationEvent
    {
        public string Callsign { get; }
        public string Band { get; }
        public string Mode { get; }
        public string Grid { get; }
        public string Country { get; }
        public int Distance { get; }    // -1 = unknown
        public int Bearing { get; }     // -1 = unknown

        public QsoStartedEvent(string callsign, string band, string mode, string grid = null,
            string country = null, int distance = -1, int bearing = -1)
        {
            Callsign = callsign ?? "";
            Band = band ?? "";
            Mode = mode ?? "";
            Grid = grid ?? "";
            Country = country ?? "";
            Distance = distance;
            Bearing = bearing;
        }

        public NotificationEventType EventType => NotificationEventType.QsoStarted;
        public string DedupKey => Callsign;

        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Callsign"] = Callsign,
            ["Band"] = Band,
            ["Mode"] = Mode,
            ["Grid"] = Grid,
            ["Country"] = Country,
            ["Distance"] = Distance >= 0 ? Distance.ToString() : "",
            ["Bearing"] = Bearing >= 0 ? Bearing.ToString() : "",
        };
    }

    public sealed class QsoCompletedEvent : INotificationEvent
    {
        public string Callsign { get; }
        public string Band { get; }
        public string Mode { get; }
        public string Grid { get; }
        public string Country { get; }
        public int Distance { get; }
        public int Bearing { get; }
        public string SignalReportSent { get; }
        public string SignalReportReceived { get; }

        public QsoCompletedEvent(string callsign, string band, string mode, string grid = null,
            string country = null, int distance = -1, int bearing = -1,
            string signalReportSent = null, string signalReportReceived = null)
        {
            Callsign = callsign ?? "";
            Band = band ?? "";
            Mode = mode ?? "";
            Grid = grid ?? "";
            Country = country ?? "";
            Distance = distance;
            Bearing = bearing;
            SignalReportSent = signalReportSent ?? "";
            SignalReportReceived = signalReportReceived ?? "";
        }

        public NotificationEventType EventType => NotificationEventType.QsoCompleted;
        public string DedupKey => Callsign;

        public IReadOnlyDictionary<string, string> ToTokens() => new Dictionary<string, string>
        {
            ["Callsign"] = Callsign,
            ["Band"] = Band,
            ["Mode"] = Mode,
            ["Grid"] = Grid,
            ["Country"] = Country,
            ["Distance"] = Distance >= 0 ? Distance.ToString() : "",
            ["Bearing"] = Bearing >= 0 ? Bearing.ToString() : "",
            ["SignalReportSent"] = SignalReportSent,
            ["SignalReportReceived"] = SignalReportReceived,
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

    // Shared empty-token-dictionary singleton for events with nothing to substitute --
    // avoids allocating a fresh empty Dictionary on every ToTokens() call for these.
    internal static class EmptyTokens
    {
        public static readonly IReadOnlyDictionary<string, string> Instance =
            new Dictionary<string, string>();
    }
}
