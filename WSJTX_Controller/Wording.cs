using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WSJTX_Controller
{
    // The wording file (operator request, 2026-09-30): the words of the spoken notification pieces
    // -- the receive side names, shared with the list titles, and the station tags in the lists --
    // live in Wording.txt in the
    // settings folder, read once at startup, so the operator changes a word without a code change.
    // A missing entry or unreadable line means the built-in wording. Start writes the file (or
    // fills an empty one) with every entry commented out ("# key = words"); remove the '#' to
    // change one. Not public (operator, 2026-10-01): never in the release notes, website or help. {Name} placeholders are filled in by
    // Jimmy. Never read in test mode.
    internal static class Wording
    {
        internal const string FileName = "Wording.txt";

        // key, built-in words, what it is
        internal static readonly (string Key, string Default, string Note)[] Known =
        {
            ("Summary.Stations.One",  "available station",         "receive summary: after the count, one station"),
            ("Summary.Stations.Many", "available stations",        "receive summary: after the count, several stations"),
            ("Summary.None",          "no",                        "receive summary: the count when there are none (simple layout)"),
            ("Summary.ToYou",         "{Count} to you",            "stations calling you"),
            ("Summary.FirstInLine",   "{Call} first",              "the station that will be worked first"),
            ("Summary.NewDxcc",       "{Count} new DXCC",          "stations from a country never worked"),
            ("Summary.NewDxccOnBand", "{Count} new DXCC on band",  "stations from a country not yet worked on this band"),
            ("Summary.Wanted",        "{Count} wanted",            "wanted stations"),
            ("Summary.Award",         "{Count} {Award}",           "stations an award still needs; {Award} is the award's own name"),
            ("Side.TX1",              "TX1",                       "advanced layout: the first-period list when you transmit first"),
            ("Side.RX1",              "RX1",                       "advanced layout: the first-period list when you receive first"),
            ("Side.TX2",              "TX2",                       "advanced layout: the second-period list when you transmit second"),
            ("Side.RX2",              "RX2",                       "advanced layout: the second-period list when you receive second"),
            ("List.Title",            "{Side} available stations", "advanced layout: the label shown above each list"),
            ("List.TitleSpoken",      "{Side} available stations, {Count} calls", "advanced layout: what the screen reader says for each list; remove {Count} not to hear the count"),
            ("Tag.NewDxcc",           "New DXCC",                  "station tag: a country never worked"),
            ("Tag.NewDxccOnBand",     "New DXCC on band",          "station tag: a country not yet worked on this band"),
            ("Tag.Wanted",            "Wanted",                    "station tag: a call on your wanted list"),
            ("Tag.Pota",              "POTA",                      "station tag: a POTA activator"),
            ("Tag.Sota",              "SOTA",                      "station tag: a SOTA activator"),
            ("Tag.WasNeeded",         "WAS Needed",                "station tag: a state Worked All States still needs"),
            ("Tag.WasUnconf",         "WAS Unconf",                "station tag: a state worked but not confirmed"),
            ("Tag.DxccUnconf",        "DXCC Unconf",               "station tag: a country worked but not confirmed"),
            ("Tag.ZoneNeeded",        "Zone Needed",               "station tag: a CQ zone still needed"),
            ("Tag.AwardNeeded",       "{Award} Needed",            "station tag: any other award still needed; {Award} is its name"),
            ("Tag.AwardUnconf",       "{Award} Unconf",            "station tag: any other award worked but not confirmed"),
            ("Tag.CallingMe",         "Calling me",                "raw decodes tag: a station calling you"),
            ("Tag.Manual",            "Manual",                    "raw decodes tag: a station you picked"),
            ("Tag.DirCq",             "Dir CQ",                    "raw decodes tag: a directed CQ you want"),
            ("Tag.FoxHound",          "Possible F/H",              "raw decodes tag: possibly a Fox/Hound station"),
            ("Msg.TxHalted", "Tx halted", "Escape / Alt+H stopped transmitting"),
            ("Msg.SmartModeStopped", "Smart Mode stopped", "Escape / Alt+H stopped Smart Mode"),
            ("Msg.SmartModeStoppedFor", "Smart Mode stopped, {Call}", "Escape / Alt+H stopped Smart Mode for a station"),
            ("Msg.SmartModeOff", "Smart Mode is off.", "Smart Mode status hotkey: the option is off"),
            ("Msg.SmartModeNoTarget", "Smart Mode is on, no target.", "Smart Mode status hotkey: nothing armed"),
            ("Msg.SmartModeStatusUnavailable", "Smart Mode status unavailable.", "Smart Mode status hotkey: no answer"),
            ("Msg.SmartModeNotHeard", "{Call} not heard after {Count} calls; Smart Mode stopped", "Smart Mode gave up: the station was not heard"),
            ("Msg.SmartModeTimeLimit", "Smart Mode time limit reached after {Minutes} {MinuteWord} calling {Call}, no contact completed", "Smart Mode gave up: its time limit"),
            ("Msg.MinuteOne", "minute", "the word for one minute"),
            ("Msg.MinuteMany", "minutes", "the word for several minutes"),
            ("Msg.RepeatLimit", "Repeat limit reached after {Count} calls to {Call}, no contact completed", "the repeat limit stopped calling"),
            ("Msg.PartnerWorkingOther", "{Call} is working {Other}; stopped calling", "Smart Mode: the station is working someone else"),
            ("Msg.Replying", "Replying to {Call}", "Jimmy is answering a station"),
            ("Msg.ReplyingNext", "Replying next to {Call}", "Jimmy will answer a station next"),
            ("Msg.NoLongerAvailable", "{Call} no longer available", "the chosen station is gone"),
            ("Msg.NoCallSelected", "No call selected", "nothing chosen to answer"),
            ("Msg.NotInQueue", "{Call} not in call queue", "the station is not in the list"),
            ("Msg.SelectCallsManually", "Select calls manually (alt/dbl-click)", "automatic calling is off"),
            ("Msg.ManualCallStarted", "Manual call started for {Call}", "a manual call began"),
            ("Msg.ManualCallFailed", "Manual call to {Call} could not be started -- no connection to the radio engine.", "a manual call could not begin"),
            ("Msg.Blocked", "{Call} is now blocked", "a station was blocked"),
            ("Msg.AlreadyBlocked", "{Call} already blocked", "the station was already blocked"),
            ("Msg.BlockingTemporarily", "Blocking {Call} temporarily...", "a station is blocked for a while"),
            ("Msg.IsBlocked", "{Call} is blocked", "the station is blocked"),
            ("Msg.IgnoredNotDx", "{Call} ignored (not DX)", "a station was skipped: not DX"),
            ("Msg.Frequencies", "Receive {Rx} hertz, transmit {Tx} hertz, {Mode}", "the receive/transmit offsets report"),
            ("Msg.BandChanged", "Band changed to {Band}", "the band changed"),
            ("Msg.TuneStarted", "Tune started", "Alt+T tune carrier on"),
            ("Msg.TuneStopped", "Tune stopped", "Alt+T tune carrier off"),
            ("Msg.TuneNeedsEngine", "Tune needs the native engine, which isn't currently reachable.", "Alt+T without the engine"),
            ("Msg.TunerStarted", "Tuner started", "Alt+Shift+T: the radio's tuner started"),
            ("Msg.TunerNotStarted", "Tuner not started", "Alt+Shift+T cancelled before it reached the radio"),
            ("Msg.TunerNeedsEngine", "Antenna tuner needs the native engine, which isn't currently reachable.", "Alt+Shift+T without the engine"),
            ("Msg.TunerStillWorking", "Tuner still working", "Alt+Shift+T pressed during a tune-up"),
            ("Msg.TunerStopTuneFirst", "Stop Tune before starting the antenna tuner", "Alt+Shift+T while Alt+T tune is on"),
            ("Msg.TunerFinished", "Tuner finished", "the tune-up ended, radio receiving"),
            ("Msg.TunerFinishedTransmitting", "Tuner finished, radio still transmitting", "the tune-up ended, radio still transmitting"),
            ("Msg.TunerDidNotStart", "The radio's tuner did not start", "the radio never switched its tuner in"),
            ("Msg.TunerNoResult", "No tuner result after {Seconds} seconds, check the radio", "the tune-up took too long"),
            ("Msg.RadioBackToReceive", "Radio back to receive", "the radio returned to receive after a tune-up"),
            ("Msg.RadioNotBackToReceive", "Could not return the radio to receive, check the radio", "the radio could not be returned to receive"),
            ("Msg.AudioIn", "Audio in: {Level} dB", "receive audio level report"),
            ("Msg.AudioLevelNoEngine", "Audio level: engine not available.", "audio level without the engine"),
            ("Msg.AudioLevelNotConfirmed", "Audio level change not confirmed -- engine not responding.", "audio level change not confirmed"),
            ("Msg.MeterEngineUnreachable", "Power/SWR: engine host unreachable.", "power/SWR report without the engine"),
            ("Msg.MeterCatDown", "Radio: CAT link is down, no meter data.", "power/SWR report, CAT down"),
            ("Msg.MeterNoData", "Radio: CAT connected, but this rig or backend reported no transmit meter data.", "power/SWR report, no data"),
            ("Msg.HoundNoEngine", "Hound: engine not connected.", "Hound without the engine"),
            ("Msg.HoundFt8Only", "Hound is FT8 only.", "Hound in FT4"),
            ("Msg.HoundNotChanged", "Hound not changed: {Reason}", "Hound change refused"),
            ("Msg.HoundOn", "Hound on", "Hound turned on"),
            ("Msg.HoundOff", "Hound off", "Hound turned off"),
            ("Msg.AnalyzingSlot", "Analyzing transmit slot...", "slot analysis started"),
            ("Msg.StillAnalyzingSlot", "Still analyzing transmit slot... ({Seconds}s)", "slot analysis still running"),
            ("Msg.SlotStartingAnyway", "{Result} Starting CQ anyway.", "slot analysis done, CQ starts"),
            ("Msg.SlotAnalysisComplete", "Transmit slot analysis complete. Even period: {Even} Hz, odd period: {Odd} Hz.", "slot analysis result"),
            ("Msg.SlotAnalysisNone", "No transmit-slot analysis has been done yet. Use Analyze Transmit Slot to run one.", "slot analysis report, none yet"),
            ("Msg.SlotAnalysisSkipped", "Transmit slot analysis skipped.", "slot analysis skipped"),
            ("Msg.WatchNoStation", "No station selected to watch", "Station Watch with nothing chosen"),
            ("Msg.WatchNotActive", "Station Watch is not active", "Station Watch status: off"),
            ("Msg.WatchWaiting", "Waiting for another decode from {Call}", "Station Watch waiting"),
            ("Msg.LogbookReady", "Logbook ready", "the logbook finished loading"),
            ("Msg.LogbookLoading", "Logbook loading; new-station alerts paused", "the logbook is loading"),
            ("Msg.LogbookSyncError", "Logbook auto-sync error: {Error}", "logbook auto-sync failed"),
            ("Msg.ProfileSaved", "Profile '{Profile}' saved.", "a profile was saved"),
            ("Msg.ProfileDeleted", "Profile '{Profile}' deleted.", "a profile was deleted"),
            ("Msg.CustomizationsExported", "Exported {Parts}.", "customizations were exported"),
            ("Msg.SharedSettingsNotSaved", "Shared settings could not be saved, see the crash log", "the shared settings move-over failed"),
            ("Msg.EngineRestarting", "Native engine host stopped unexpectedly -- restarting ({Attempt}/{Max})...", "the engine stopped and is restarting"),
            ("Msg.HotkeysUnassigned", "New shortcut(s) left unassigned because your custom keys already use them: {Names}. Set them in Options, Hotkeys.", "new hotkeys clashed with yours"),
            ("Status.Receiving", "Receiving", "status line: receiving"),
            ("Status.Transmitting", "Transmitting", "status line: transmitting"),
            ("Status.TxEnabled", "transmit enabled", "status line: transmit just enabled"),
            ("Status.TxDisabled", "transmit disabled", "status line: transmit disabled"),
            ("Status.Selected", "selected", "after the station just picked"),
            ("Status.Expired", "expired", "after a station that expired"),
            ("Status.TimedOut", "timed out", "after a station that timed out"),
            ("Status.FinalSignoff", "{Call} final 73", "the station's final 73"),
            ("Status.Received", "received {Message}", "what the station just sent"),
            ("Status.Previous", "previous {Message}", "what the station sent before"),
            ("Status.NoResponse", "no response", "the station did not answer"),
            ("Status.TxSideSelected", "{Side} selected", "the transmit period just chosen ({Side} from the Side entries)"),
            ("Status.PskReporterOn", "Enabled PSKReporter spots", "PSKReporter spotting turned on"),
            ("Status.PskReporterOff", "Disabled PSKReporter spots", "PSKReporter spotting turned off"),
            ("Status.ModeName", "{Mode} mode", "the mode just changed to"),
            ("Status.ModeSelected", "{Mode} mode selected.", "starting up: the mode chosen"),
            ("Status.BandSelected", "{Band} meter band selected", "the band just chosen"),
            ("Status.BandUnknown", "Unknown band selected", "the band just chosen, not known"),
            ("Status.DeletedCalls", "Deleted all waiting calls", "the waiting list was cleared"),
            ("Status.CommandPromptsOn", "Command prompts enabled", "command prompts turned on"),
            ("Status.CommandPromptsOff", "Command prompts disabled", "command prompts turned off"),
            ("Status.Connecting", "Connecting, wait until ready", "starting up, connecting to the engine"),
            ("Status.AnalyzingAudio", "Analyzing audio, calls not queued yet", "starting up, best-frequency analysis"),
            ("Status.ModeNotSupported", "operating mode not supported", "the radio's mode cannot be used"),
            ("Status.UpdatingTxFreq", "Updating best transmit frequency.", "best transmit frequency being worked out"),
            ("Status.CatLost", "Radio CAT link lost, {Mode}.", "the radio control link was lost"),
            ("Status.CqMode", "CQ mode", "operating mode word: calling CQ"),
            ("Status.CqTxDisabled", "CQ, transmit disabled", "operating mode words: CQ with transmit off"),
            ("Status.ListenMode", "Listen mode", "operating mode word: listening"),
            ("Status.HelpHint", "use {Key}, for command key list", "command key list hint; {Key} is your Help hotkey (Options, Hotkeys)"),
            ("Status.EnableTxHint", "{Key} to enable transmit", "enable transmit hint; {Key} is your Enable Transmit hotkey"),
            ("Status.ResumeHint", "use {Key} to resume QSO", "resume hint; {Key} is your Enable Transmit hotkey"),
            ("Status.ListHint", "{Key} for list", "list hint; {Key} is your Focus Available Stations List hotkey"),
            ("Status.NextHint", "{Key} for next", "next hint; {Key} is your Skip to Next Call hotkey"),
            ("Status.ListOrNext", "{List} or {Next}", "both hints together"),
            ("Status.SettingUp", "Setting up Jimmy Next.", "starting up while setup is open"),
            ("Status.SetupNeeded", "To begin operating, set in Options: {List}.", "setup not finished; {List} is the parts below still missing"),
            ("Status.SetupCallGrid", "your callsign and grid on the Station & Operator page", "missing part: callsign and grid"),
            ("Status.SetupRadio", "your radio on the Radio page", "missing part: radio"),
            ("Status.SetupAudio", "your radio's audio devices on the Decode Engine page", "missing part: audio devices"),
            ("List.EmptyCalling", "[No stations calling]", "standard layout: nobody waiting, a QSO in progress"),
            ("List.EmptyCallingOrInProgress", "[No stations calling or in progress]", "standard layout: nobody waiting, no QSO"),
            ("List.EmptyAvailable", "No available stations", "advanced layout: a TX/RX list with nobody in it"),
            ("List.EmptyAutoLogged", "[No calls auto-logged]", "the auto-logged list is empty"),
            ("Msg.ClockFast", "{Seconds} seconds fast", "clock report: computer clock ahead"),
            ("Msg.ClockSlow", "{Seconds} seconds slow", "clock report: computer clock behind"),
            ("Msg.ClockTooFar", "Clock {Offset}, too far to correct, set the computer clock", "clock report: too far off for the time server"),
            ("Msg.ClockCorrected", "Clock {Offset}, corrected by time server, {When}", "clock report: time server correcting"),
            ("Msg.ClockCheckedNow", "checked just now", "clock report: when it was checked"),
            ("Msg.ClockCheckedMinute", "checked 1 minute ago", "clock report: when it was checked"),
            ("Msg.ClockCheckedMinutes", "checked {Minutes} minutes ago", "clock report: when it was checked"),
            ("Msg.ClockNotMeasured", "Clock not yet measured", "clock report: nothing to go on yet"),
            ("Msg.ClockSignalsBad", "Clock {Seconds} seconds by signals, out of sync, no time server, check clock time", "clock report: judged from signals, off"),
            ("Msg.ClockSignalsGood", "Clock {Seconds} seconds by signals, good, no time server", "clock report: judged from signals, good"),
            ("Msg.MeterPower", "power {Watts} W", "power/SWR report: output power"),
            ("Msg.MeterSwr", "SWR {Swr}", "power/SWR report: SWR"),
            ("Msg.MeterAlc", "ALC {Alc}", "power/SWR report: ALC"),
            ("Msg.AudioInExplained", "Audio in {Level} dB, {Hint}", "receive audio level, Explain meter readings on"),
            ("Msg.SMeter", "S-meter {Reading}", "receive S-meter, Explain meter readings on"),
            ("Msg.SMeterPlus", "S9 plus {Db} dB", "S-meter above S9"),
            ("Msg.SwrGood", "good", "SWR hint"),
            ("Msg.SwrAcceptable", "acceptable", "SWR hint"),
            ("Msg.SwrHigh", "high", "SWR hint"),
            ("Msg.SwrVeryHigh", "very high, check antenna", "SWR hint"),
            ("Msg.AlcClean", "clean", "ALC hint"),
            ("Msg.AlcLittleHigh", "a little high, reduce audio", "ALC hint"),
            ("Msg.AlcHigh", "high, reduce audio", "ALC hint"),
            ("Msg.AudioInLow", "low", "audio-in hint"),
            ("Msg.AudioInGood", "good", "audio-in hint"),
            ("Msg.AudioInHot", "hot", "audio-in hint"),
            ("Msg.AudioInClipping", "too hot, clipping", "audio-in hint"),
            ("Msg.SlotFor", "for {BandMode}", "slot analysis report: the band and mode it was for"),
            ("Msg.SlotResult", "Transmit slot analysis{For}: even period {Even} Hz, odd period {Odd} Hz.", "slot analysis report, both periods"),
            ("Msg.SlotEvenOnly", "Transmit slot analysis{For} incomplete: only the even period had usable data, {Even} Hz.", "slot analysis report, even period only"),
            ("Msg.SlotOddOnly", "Transmit slot analysis{For} incomplete: only the odd period had usable data, {Odd} Hz.", "slot analysis report, odd period only"),
            ("Msg.SlotNotEnough", "Transmit slot analysis{For} incomplete: not enough decodes to analyze the transmit slot.", "slot analysis report, not enough decodes"),
            ("List.RawTitle", "Raw decodes", "the Raw Decodes list: its label and spoken name"),
            ("List.SpotWatchTitle", "Spot Watch", "the Spot Watch list: its label and spoken name"),
        };

        // The file's sections, by key prefix: grouping only -- a key means the same in any section.
        internal static string SectionOf(string key) =>
            key.StartsWith("Msg.") || key.StartsWith("Summary.") ? "NOTIFICATIONS"
            : key.StartsWith("Status.") ? "STATUS"
            : key.StartsWith("Tag.") ? "TAGS"
            : "LISTS";

        private static Dictionary<string, string> _overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static string Get(string key)
        {
            if (_overrides.TryGetValue(key, out string v)) return v;
            foreach (var k in Known) if (k.Key == key) return k.Default;
            return key;
        }

        internal static string Fill(string key, params (string Name, string Value)[] values)
        {
            string s = Get(key);
            foreach (var (name, value) in values) s = s.Replace("{" + name + "}", value ?? "");
            return s;
        }

        // Startup: read the file, or write it (all commented out) when there is none or it is
        // empty. Returns a line for the debug log, or null.
        internal static string Load(string folder)
        {
            if (TestModeGuard.IsTestMode) return null;
            string path = Path.Combine(folder, FileName);
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                {
                    File.WriteAllText(path, Template(), new UTF8Encoding(false));
                    _overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    return null;
                }
                string[] lines = File.ReadAllLines(path);
                _overrides = Parse(lines);
                // Entries added in a later version: appended, commented out, so the file always
                // lists everything that can be reworded. The operator's own lines are untouched.
                var listed = new HashSet<string>(lines.Select(l => l.TrimStart('#', ' ', '\t'))
                    .Where(l => l.Contains("=")).Select(l => l.Substring(0, l.IndexOf('=')).Trim()), StringComparer.OrdinalIgnoreCase);
                var missing = Known.Where(k => !listed.Contains(k.Key)).ToList();
                if (missing.Count > 0)
                {
                    var sb = new StringBuilder();
                    foreach (var group in missing.GroupBy(m => SectionOf(m.Key)))
                    {
                        sb.AppendLine();
                        sb.AppendLine("[" + group.Key + "]");
                        foreach (var (key, def, note) in group)
                        {
                            sb.AppendLine();
                            sb.AppendLine("# " + note);
                            sb.AppendLine("# " + key + " = " + def);
                        }
                    }
                    File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
                }
                return _overrides.Count > 0 ? $"wording: {_overrides.Count} entr{(_overrides.Count == 1 ? "y" : "ies")} from {FileName}" : null;
            }
            catch (Exception ex)
            {
                _overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return $"wording: {FileName} not read ({ex.Message}); built-in wording used";
            }
        }

        // "key = words" lines; '#' starts a comment line; unknown keys and blank words are ignored.
        internal static Dictionary<string, string> Parse(IEnumerable<string> lines)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in lines ?? Enumerable.Empty<string>())
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("[")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string words = line.Substring(eq + 1).Trim();
                if (words.Length > 0 && Known.Any(k => k.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) d[key] = words;
            }
            return d;
        }

        // True when the file changes at least one entry (the Export prompt offers wording only then).
        internal static bool HasOverrides => _overrides.Count > 0;

        internal static void SetForTest(Dictionary<string, string> overrides) =>
            _overrides = overrides ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static string Template()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Jimmy Next wording. Remove the '#' in front of a line and change the words after '='.");
            sb.AppendLine("# Read when Jimmy Next starts. A line left with '#' keeps the built-in wording.");
            sb.AppendLine("# Words in {braces} are filled in by Jimmy Next; keep them.");
            sb.AppendLine("# [SECTIONS] only group the lines; an entry means the same in any section.");
            foreach (var group in Known.GroupBy(k => SectionOf(k.Key)))
            {
                sb.AppendLine();
                sb.AppendLine("[" + group.Key + "]");
                foreach (var (key, def, note) in group)
                {
                    sb.AppendLine();
                    sb.AppendLine("# " + note);
                    sb.AppendLine("# " + key + " = " + def);
                }
            }
            return sb.ToString();
        }
    }
}
