using System;
using System.Collections.Generic;
using System.Linq;

namespace WSJTX_Controller
{
    // One template variable Jimmy can offer for a given notification type. The single
    // authoritative place a {Token} name and its human-readable description live -- the
    // config UI's variable checklist, the template validator, and this file are the only three
    // places that ever need to know a variable exists, and only this file defines what one IS.
    public sealed class NotificationVariable
    {
        public string Key { get; }             // bare token name, e.g. "Callsign" (no braces)
        public string Description { get; }      // shown next to the checkbox in the config UI

        public NotificationVariable(string key, string description)
        {
            Key = key;
            Description = description;
        }
    }

    // Per-NotificationEventType variable inventory, kept honest by construction: every list
    // below is exactly the set of keys that event type's own ToTokens() (NotificationEvents.cs)
    // can produce, plus the one universal {Time} every type gets for free (injected centrally
    // by NotificationCenter.Publish, not stored on any individual event -- see its own comment).
    // NotificationVariableRegistryTests verifies this file and the real event classes never
    // drift apart by constructing a representative instance of each and diffing key sets.
    //
    // Deliberately excludes CQ zone, ITU zone, and QRZ-sourced US state: those live behind
    // LookupManager's async QRZ/Club Log lookups, which may not have completed (or may need
    // network access Jimmy doesn't have) at the moment a notification would fire -- "reliably
    // available" is the bar, not "sometimes available". WsjtxClient.GridToUsState (a local,
    // synchronous grid.dat table, no network) is the one grid-derived field that clears that
    // bar, so {State} IS included.
    public static class NotificationVariableRegistry
    {
        public const string TimeKey = "Time";
        private static readonly NotificationVariable TimeVariable =
            new NotificationVariable(TimeKey, "The current time when the notification is spoken.");

        private static readonly Dictionary<NotificationEventType, List<NotificationVariable>> ByEventType =
            new Dictionary<NotificationEventType, List<NotificationVariable>>
        {
            [NotificationEventType.QsoStarted] = new List<NotificationVariable>
            {
                new NotificationVariable("Callsign", "The station's callsign."),
                new NotificationVariable("Band", "The band you're operating on, e.g. 20m."),
                new NotificationVariable("Mode", "FT8 or FT4."),
                new NotificationVariable("Grid", "The station's grid square, if known."),
                new NotificationVariable("Country", "The station's DXCC country, if known."),
                new NotificationVariable("Distance", "Distance to the station in miles, if known."),
                new NotificationVariable("Bearing", "Compass bearing to the station in degrees, if known."),
            },
            [NotificationEventType.QsoCompleted] = new List<NotificationVariable>
            {
                new NotificationVariable("Callsign", "The station's callsign."),
                new NotificationVariable("Band", "The band the QSO was made on."),
                new NotificationVariable("Mode", "FT8 or FT4."),
                new NotificationVariable("Grid", "The station's grid square, if known."),
                new NotificationVariable("Country", "The station's DXCC country, if known."),
                new NotificationVariable("Distance", "Distance to the station in miles, if known."),
                new NotificationVariable("Bearing", "Compass bearing to the station in degrees, if known."),
                new NotificationVariable("SignalReportSent", "The signal report you sent."),
                new NotificationVariable("SignalReportReceived", "The signal report you received."),
            },
            [NotificationEventType.TxMessageChanged] = new List<NotificationVariable>
            {
                new NotificationVariable("Callsign", "Who your current transmission is addressed to (or CQ)."),
                new NotificationVariable("Message", "The full text being transmitted."),
                new NotificationVariable("Band", "The band you're operating on."),
                new NotificationVariable("Mode", "FT8 or FT4."),
            },
            [NotificationEventType.AwardsNeeded] = new List<NotificationVariable>
            {
                new NotificationVariable("Callsign", "The station's callsign."),
                new NotificationVariable("AwardSummary", "A pre-worded count, e.g. \"2 awards needed\"."),
                new NotificationVariable("AwardList", "The award names, comma-separated."),
                new NotificationVariable("Country", "The station's DXCC country, if known."),
            },
            [NotificationEventType.ConnectionClosed] = new List<NotificationVariable>
            {
                // WSJT-X closing carries no data of its own -- {Time} (added by every type
                // below) is the only variable this one has anything to offer.
            },
            [NotificationEventType.ConnectionLost] = new List<NotificationVariable>
            {
                new NotificationVariable("Detail", "Why the connection was considered lost, if known."),
            },
            [NotificationEventType.ErrorWarning] = new List<NotificationVariable>
            {
                new NotificationVariable("Source", "What reported the error, e.g. \"Radio\", \"Native engine\"."),
                new NotificationVariable("Detail", "The error text itself."),
                new NotificationVariable("Severity", "\"Warning\" or \"Error\"."),
            },
            [NotificationEventType.ClockOutOfSync] = new List<NotificationVariable>
            {
                new NotificationVariable("ClockOffset", "How far the computer's clock is estimated to be off, in seconds."),
                new NotificationVariable("Mode", "FT8 or FT4."),
            },
            [NotificationEventType.ClockSynced] = new List<NotificationVariable>
            {
                new NotificationVariable("Mode", "FT8 or FT4."),
            },
        };

        public static IReadOnlyList<NotificationVariable> For(NotificationEventType type)
        {
            var list = ByEventType.TryGetValue(type, out var v) ? new List<NotificationVariable>(v) : new List<NotificationVariable>();
            list.Add(TimeVariable);
            return list;
        }

        // Null when every {Token} in template is either a known variable for `type` or plain
        // literal text. Otherwise a short, accessible message naming the FIRST unknown token --
        // matches the Hotkeys/Frequencies panels' own one-problem-at-a-time MessageBox style
        // rather than dumping every error at once.
        public static string Validate(string template, NotificationEventType type)
        {
            var known = new HashSet<string>(For(type).Select(v => v.Key), StringComparer.Ordinal);
            foreach (string name in NotificationTemplateEngine.ExtractVariableNames(template))
            {
                if (!known.Contains(name))
                    return $"Unknown template keyword: {name}";
            }
            return null;
        }
    }
}
