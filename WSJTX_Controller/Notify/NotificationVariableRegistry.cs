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
            // The counts portion only, since 2026-09-05. The state verb moved to
            // ReceiveStateSummary ({State}) and the mode descriptor to OperatingModeSummary
            // ({Mode}/{SubMode}); the beginner action hint is structural and is appended by
            // ShowStatus after this clause, not a template variable here.
            [NotificationEventType.ReceiveCycleSummary] = new List<NotificationVariable>
            {
                new NotificationVariable("AvailableCount", "How many stations are available to call, e.g. \"3\" (\"no\" when none in the simple layout). The RX1/TX2 side name is its own row (\"Receive side name\") now, not part of this."),
                new NotificationVariable("Stations", "\"available stations\" / \"available station\" (pluralized). Replace with your own word, e.g. \"calls\"."),
                new NotificationVariable("ToYou", "\", N to you, CALL first\" when stations are calling you, otherwise empty."),
                new NotificationVariable("NewDxcc", "\", N new DXCC\" when any, otherwise empty."),
                new NotificationVariable("Wanted", "\", N wanted\" when any, otherwise empty."),
                new NotificationVariable("Awards", "\", N <award>\" for each award still needed on a spotted station, otherwise empty."),
                new NotificationVariable("NewDxccCount", "Bare count of new-DXCC stations (renders \"0\" when none)."),
                new NotificationVariable("WantedCount", "Bare count of wanted stations (renders \"0\" when none)."),
                new NotificationVariable("AwardCount", "Bare count of distinct awards still needed (renders \"0\" when none)."),
                new NotificationVariable("Band", "The band you're operating on, e.g. 20m."),
            },
            [NotificationEventType.ReceiveStateSummary] = new List<NotificationVariable>
            {
                new NotificationVariable("State", "\"Receiving\" or \"Transmitting\"."),
            },
            [NotificationEventType.OperatingModeSummary] = new List<NotificationVariable>
            {
                new NotificationVariable("Mode", "\"Listen\" or \"CQ\"."),
                new NotificationVariable("SubMode", "\", FT4\" on FT4, otherwise empty."),
            },
            [NotificationEventType.ReceiveSideId] = new List<NotificationVariable>
            {
                new NotificationVariable("Side", "The advanced-call-layout side name for the slot whose receive period just ended: \"RX1\", \"RX2\", \"TX1\" or \"TX2\", following the current transmit side."),
            },
            [NotificationEventType.QsoStarted] = new List<NotificationVariable>
            {
                new NotificationVariable("Callsign", "The station you're now working."),
                new NotificationVariable("Band", "The band you're operating on, e.g. 20m."),
                new NotificationVariable("Mode", "FT8 or FT4."),
            },
            [NotificationEventType.QsoCompleted] = new List<NotificationVariable>
            {
                new NotificationVariable("Callsign", "The station just logged."),
                new NotificationVariable("Band", "The band the QSO was made on."),
                new NotificationVariable("Mode", "FT8 or FT4."),
            },
            [NotificationEventType.TxMessageChanged] = new List<NotificationVariable>
            {
                new NotificationVariable("Callsign", "Who your current transmission is addressed to (or CQ)."),
                new NotificationVariable("Message", "The message text being transmitted, e.g. \"R minus 12\"."),
                new NotificationVariable("Band", "The band you're operating on."),
                new NotificationVariable("Mode", "FT8 or FT4."),
            },
            [NotificationEventType.ReceivedReply] = new List<NotificationVariable>
            {
                new NotificationVariable("Received", "\", received <message>\" (or \" no response\" / a short activity note) for the reply that just came in this receive cycle, otherwise empty."),
                new NotificationVariable("Previous", "\", previous <message>\" for the reply before that, otherwise empty."),
                new NotificationVariable("Callsign", "The station you are working."),
            },
            [NotificationEventType.NoDecodeWarning] = new List<NotificationVariable>
            {
                new NotificationVariable("Mode", "FT8 or FT4."),
            },
            [NotificationEventType.AwardsNeeded] = new List<NotificationVariable>
            {
                new NotificationVariable("Callsign", "The station's callsign."),
                new NotificationVariable("AwardSummary", "A pre-worded count, e.g. \"2 awards needed\"."),
                new NotificationVariable("AwardList", "The award names, comma-separated."),
                new NotificationVariable("Country", "The station's DXCC country, if known."),
            },
            [NotificationEventType.ConnectionLost] = new List<NotificationVariable>
            {
                new NotificationVariable("Detail", "Why the connection was considered lost, if known."),
            },
            [NotificationEventType.AutoTxResume] = new List<NotificationVariable>
            {
                new NotificationVariable("Callsign", "The station Jimmy has automatically resumed working."),
            },
            [NotificationEventType.StationWatchStarted] = new List<NotificationVariable>
            {
                new NotificationVariable("Target", "The callsign now being watched."),
            },
            [NotificationEventType.StationWatchStopped] = new List<NotificationVariable>
            {
                new NotificationVariable("Target", "The callsign that was being watched."),
            },
            [NotificationEventType.StationWatchActivity] = new List<NotificationVariable>
            {
                new NotificationVariable("Phrase", "A ready-made natural phrase for the observation, e.g. \"K4YT working W1ABC, minus 8.\""),
                new NotificationVariable("Target", "The watched callsign."),
                new NotificationVariable("Peer", "The other station involved, if any."),
                new NotificationVariable("Value", "The report value, if this observation carries one (e.g. \"-08\", \"R-05\")."),
                new NotificationVariable("Kind", "The observation kind, e.g. \"TargetCq\", \"TargetRr73\"."),
            },
            [NotificationEventType.StationWatchAmbiguous] = new List<NotificationVariable>
            {
                new NotificationVariable("Target", "The watched callsign."),
            },
            [NotificationEventType.SmartStartWaiting] = new List<NotificationVariable>
            {
                new NotificationVariable("Phrase", "A ready-made sentence describing what Jimmy is still waiting for."),
                new NotificationVariable("Target", "The captured Smart Start target callsign."),
                new NotificationVariable("Progress", "\"N of M\" -- how many appropriate receive opportunities have elapsed of the configured threshold (empty for a non-progress wait)."),
            },
            [NotificationEventType.SmartStartTargetAvailable] = new List<NotificationVariable>
            {
                new NotificationVariable("Target", "The Smart Start target callsign."),
            },
            [NotificationEventType.SmartStartCallStarting] = new List<NotificationVariable>
            {
                new NotificationVariable("Target", "The station Jimmy is now calling."),
            },
            [NotificationEventType.SmartStartArmed] = new List<NotificationVariable>
            {
                new NotificationVariable("Target", "The station Jimmy will work when appropriate."),
            },
            [NotificationEventType.SmartStartTargetBusy] = new List<NotificationVariable>
            {
                new NotificationVariable("Target", "The Smart Start target, now working another station."),
            },
            [NotificationEventType.SmartStartYielded] = new List<NotificationVariable>
            {
                new NotificationVariable("Target", "The Smart Start target Jimmy has stopped calling for now."),
            },
            [NotificationEventType.SmartStartEngaged] = new List<NotificationVariable>
            {
                new NotificationVariable("Target", "The station that has just addressed your callsign."),
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
            [NotificationEventType.RadioCatRecovered] = new List<NotificationVariable>
            {
                // Carries no data of its own -- {Time} (added by every type) is all it offers.
            },
            [NotificationEventType.RadioCatLost] = new List<NotificationVariable>
            {
                new NotificationVariable("Connection", "A pre-worded phrase for the configured CAT connection, e.g. \" on COM4 at 115200 baud\"."),
                new NotificationVariable("ComPort", "The configured CAT serial port, if any."),
                new NotificationVariable("BaudRate", "The configured CAT baud rate, if any."),
                new NotificationVariable("RigModel", "The configured Hamlib rig-model id, if any."),
                new NotificationVariable("Detail", "The raw Nexus/Hamlib diagnostic text -- verbose; omitted from the default wording."),
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
