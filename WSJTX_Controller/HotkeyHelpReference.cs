using System;
using System.Collections.Generic;

namespace WSJTX_Controller
{
    // Single ordered source of truth for the Alt+K ("Help -- show shortcuts") reference card.
    //
    // Controller.BuildHelpText renders this list instead of keeping its own hand-maintained
    // copy of the command set. Every configurable HotkeyAction must appear here exactly once
    // (or in ExcludedFromHelp with a documented reason). HotkeyHelpReferenceParityTests in
    // JimmyTests fails if that stops being true, so a future HotkeyAction cannot be silently
    // left out of Alt+K Help the way five actions (Report Clock Sync Status, Focus TX1/TX2
    // Available Stations, Focus Raw Decodes, Focus Spot Watch) were before this was added.
    //
    // Descriptions are intentionally short and operational: Alt+K is a lookup card, not a
    // manual. The operator's actual configured key is formatted at render time; a command
    // left unassigned (Keys.None) renders as "Not assigned" so it can still be discovered
    // and bound under Options > Hotkeys.
    internal static class HotkeyHelpReference
    {
        internal sealed class Item
        {
            public readonly HotkeyAction Action;
            public readonly string Description;
            public Item(HotkeyAction action, string description)
            {
                Action = action;
                Description = description;
            }
        }

        internal sealed class Section
        {
            public readonly string Title;
            public readonly IReadOnlyList<Item> Items;
            // Fixed keys that are not part of the configurable registry (Delete, Escape),
            // shown verbatim at the end of the section they belong to.
            public readonly IReadOnlyList<string> ExtraLines;
            public Section(string title, Item[] items, string[] extraLines = null)
            {
                Title = title;
                Items = items;
                ExtraLines = extraLines ?? Array.Empty<string>();
            }
        }

        // HotkeyActions deliberately omitted from the Alt+K card, each with a reason the
        // parity test prints. Keep empty unless there is a real reason to exclude one.
        internal static readonly IReadOnlyDictionary<HotkeyAction, string> ExcludedFromHelp =
            new Dictionary<HotkeyAction, string>();

        internal static readonly Section[] Sections =
        {
            new Section("Operating commands", new[]
            {
                new Item(HotkeyAction.CallCqMode,         "Start the selected CQ mode (CQ only / CQ DX only / CQ and CQ DX). Does nothing in Listen mode."),
                new Item(HotkeyAction.CallCqOptions,      "Open Call CQ options (CQ only / CQ DX only / CQ and CQ DX, directed CQ, and more)."),
                new Item(HotkeyAction.ListenMode,         "Select 'Listen for calls' mode."),
                new Item(HotkeyAction.EnableTx,           "Enable transmit, or re-enable a timed-out QSO."),
                new Item(HotkeyAction.HaltTx,             "Halt transmit immediately."),
                new Item(HotkeyAction.NextCall,           "Skip to the next available station."),
                new Item(HotkeyAction.ManualCall,         "Enter a callsign manually to call."),
                new Item(HotkeyAction.DeleteAllCalls,     "Delete all 'Stations calling'."),
                new Item(HotkeyAction.TxPeriod,           "Toggle the transmit period."),
                new Item(HotkeyAction.ToggleMode,         "Select operating mode (FT8 or FT4)."),
                new Item(HotkeyAction.AnalyzeSlot,        "Analyze the transmit slot to find the quietest CQ audio frequency (needs 'Use best Tx frequency')."),
                new Item(HotkeyAction.ReportSlotAnalysis, "Report the latest transmit slot analysis without re-running it."),
            }, new[]
            {
                "Delete key: Delete the selected call in 'Stations calling'.",
                "Escape key: Halt transmit, cancel the current QSO, and switch to Listen mode.",
            }),

            new Section("Station Watch", new[]
            {
                new Item(HotkeyAction.ToggleStationWatch,    "Start or stop watching the selected station (receive-only; never transmits)."),
                new Item(HotkeyAction.WorkWatchedStationNow, "Work the watched station now, using its most recent decode."),
            }),

            new Section("Radio and audio controls", new[]
            {
                new Item(HotkeyAction.TuneMode,  "Toggle Tune mode to set the audio output level to the radio."),
                new Item(HotkeyAction.AudioUp,   "Increase the audio output level to the radio (during tune or transmit)."),
                new Item(HotkeyAction.AudioDown, "Decrease the audio output level to the radio (during tune or transmit)."),
                new Item(HotkeyAction.PowerSwr,  "Quick check of output power and SWR (transmit) or audio input level (receive)."),
                new Item(HotkeyAction.BandUp,    "Select the next higher band."),
                new Item(HotkeyAction.BandDown,  "Select the next lower band."),
            }),

            new Section("Frequency controls", new[]
            {
                new Item(HotkeyAction.AnnounceFreq, "Announce the current receive and transmit audio frequencies and mode."),
                new Item(HotkeyAction.TxFreqUp,     "Move the transmit audio frequency up by the step set on the Transmit tab."),
                new Item(HotkeyAction.TxFreqDown,   "Move the transmit audio frequency down by the same step."),
                new Item(HotkeyAction.RxFreqUp,     "Move the receive audio frequency up by the same step."),
                new Item(HotkeyAction.RxFreqDown,   "Move the receive audio frequency down by the same step."),
                new Item(HotkeyAction.TxFromRx,     "Set the transmit audio frequency to the current receive frequency."),
                new Item(HotkeyAction.RxFromTx,     "Set the receive audio frequency to the current transmit frequency."),
                new Item(HotkeyAction.SetTxFreq,    "Set the transmit audio frequency to an exact value."),
            }),

            new Section("Windows and tools", new[]
            {
                new Item(HotkeyAction.Options,             "Review or set options for processing QSOs."),
                new Item(HotkeyAction.SortOrder,           "Open the 'Stations available' sort order editor."),
                new Item(HotkeyAction.RowOrder,            "Open the 'Stations available' row order editor."),
                new Item(HotkeyAction.Prompts,             "Toggle command prompts in the status area."),
                new Item(HotkeyAction.PSKReporter,         "Toggle sending spots to PSKReporter."),
                new Item(HotkeyAction.UploadLotw,          "Upload to Logbook of the World."),
                new Item(HotkeyAction.NotificationHistory, "Open Notification History (what has been announced this session)."),
                new Item(HotkeyAction.ClockStatus,         "Report clock sync status."),
                new Item(HotkeyAction.LookupStation,       "Look up the selected station (callsign, country, state, LoTW status, and more)."),
                new Item(HotkeyAction.OpenLogbook,         "Open the Ham Radio Center logbook."),
                new Item(HotkeyAction.AddManualQso,        "Add a manually-logged QSO (worked on another mode or rig)."),
                new Item(HotkeyAction.OpenOtaSpots,        "Open POTA / SOTA spots, DX spots, band conditions, and space weather."),
                new Item(HotkeyAction.UpdateCheck,         "Check for a program update."),
                new Item(HotkeyAction.ResetWindowSize,     "Reset the window size and position to default."),
                new Item(HotkeyAction.Help,                "Read this list of shortcut keys."),
            }),

            new Section("Main navigation", new[]
            {
                new Item(HotkeyAction.NavStatus,       "Read QSO and radio status (the 'home' location)."),
                new Item(HotkeyAction.NavCallList,     "Read and select from the 'Stations calling' list."),
                new Item(HotkeyAction.NavPendingCount, "Read the number of pending 'Stations calling'."),
                new Item(HotkeyAction.NavLoggedList,   "Read the 'Auto-logged calls' list."),
                new Item(HotkeyAction.NavLoggedCount,  "Read the total number of 'Auto-logged calls'."),
            }),

            new Section("Advanced Call Layout navigation (Advanced Call Layout only)", new[]
            {
                new Item(HotkeyAction.NavAdvTx1,    "Focus the TX1 available-stations list."),
                new Item(HotkeyAction.NavAdvTx2,    "Focus the TX2 available-stations list."),
                new Item(HotkeyAction.NavAdvRaw,    "Focus the raw-decodes list."),
                new Item(HotkeyAction.NavSpotWatch, "Focus the Spot Watch list."),
            }),
        };
    }
}
