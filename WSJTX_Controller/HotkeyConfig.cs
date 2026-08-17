using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    public enum HotkeyAction
    {
        // General Commands
        Options,
        Help,
        UpdateCheck,
        CallCqMode,
        ListenMode,
        EnableTx,
        HaltTx,
        NextCall,
        ManualCall,
        DeleteAllCalls,
        TxPeriod,
        HoldTimeout,
        TuneMode,
        AudioUp,
        AudioDown,
        PowerSwr,
        BandUp,
        BandDown,
        ToggleMode,
        PSKReporter,
        Prompts,
        UploadLotw,
        SortOrder,
        RowOrder,
        AnalyzeSlot,
        LookupStation,
        OpenLogbook,
        AddManualQso,
        OpenOtaSpots,
        ResetWindowSize,
        // Accessibility Navigation
        NavStatus,
        NavCallList,
        NavPendingCount,
        NavLoggedList,
        NavLoggedCount,
        NavAdvTx1,
        NavAdvTx2,
        NavAdvRaw,
        NavSpotWatch,
    }

    public class HotkeyConfig
    {
        private const string IniSection = "Hotkeys";

        private readonly Dictionary<HotkeyAction, Keys> _keys = new Dictionary<HotkeyAction, Keys>();

        public static readonly Dictionary<HotkeyAction, Keys> Defaults = new Dictionary<HotkeyAction, Keys>
        {
            [HotkeyAction.Options]         = Keys.Alt | Keys.O,
            [HotkeyAction.Help]            = Keys.Alt | Keys.K,
            [HotkeyAction.UpdateCheck]     = Keys.F4,
            [HotkeyAction.CallCqMode]      = Keys.Alt | Keys.C,
            [HotkeyAction.ListenMode]      = Keys.Alt | Keys.L,
            [HotkeyAction.EnableTx]        = Keys.Alt | Keys.E,
            [HotkeyAction.HaltTx]          = Keys.Alt | Keys.H,
            [HotkeyAction.NextCall]        = Keys.Alt | Keys.N,
            [HotkeyAction.ManualCall]      = Keys.Alt | Keys.J,
            [HotkeyAction.DeleteAllCalls]  = Keys.Alt | Keys.D,
            [HotkeyAction.TxPeriod]        = Keys.Alt | Keys.F,
            [HotkeyAction.HoldTimeout]     = Keys.Alt | Keys.X,
            [HotkeyAction.TuneMode]        = Keys.Alt | Keys.T,
            [HotkeyAction.AudioUp]         = Keys.F12,
            [HotkeyAction.AudioDown]       = Keys.F11,
            [HotkeyAction.PowerSwr]        = Keys.Alt | Keys.Q,
            [HotkeyAction.BandUp]          = Keys.Alt | Keys.PageUp,
            [HotkeyAction.BandDown]        = Keys.Alt | Keys.PageDown,
            [HotkeyAction.ToggleMode]      = Keys.Alt | Keys.M,
            [HotkeyAction.PSKReporter]     = Keys.Alt | Keys.R,
            [HotkeyAction.Prompts]         = Keys.Alt | Keys.P,
            [HotkeyAction.UploadLotw]      = Keys.Alt | Keys.U,
            [HotkeyAction.SortOrder]       = Keys.Alt | Keys.S,
            [HotkeyAction.RowOrder]        = Keys.Alt | Keys.I,
            [HotkeyAction.AnalyzeSlot]     = Keys.Alt | Keys.Z,
            [HotkeyAction.LookupStation]   = Keys.None,
            [HotkeyAction.OpenLogbook]     = Keys.None,
            [HotkeyAction.AddManualQso]    = Keys.None,
            [HotkeyAction.OpenOtaSpots]    = Keys.Alt | Keys.G,
            [HotkeyAction.ResetWindowSize] = Keys.Control | Keys.Shift | Keys.R,
            [HotkeyAction.NavStatus]       = Keys.Control | Keys.S,
            [HotkeyAction.NavCallList]     = Keys.Control | Keys.W,
            [HotkeyAction.NavPendingCount] = Keys.Control | Keys.P,
            [HotkeyAction.NavLoggedList]   = Keys.Control | Keys.A,
            [HotkeyAction.NavLoggedCount]  = Keys.Control | Keys.T,
            // Ctrl+1/2/3 are free of conflicts with all existing defaults
            [HotkeyAction.NavAdvTx1]       = Keys.Control | Keys.D1,
            [HotkeyAction.NavAdvTx2]       = Keys.Control | Keys.D2,
            [HotkeyAction.NavAdvRaw]       = Keys.Control | Keys.D3,
            [HotkeyAction.NavSpotWatch]    = Keys.Control | Keys.D4,
        };

        public static readonly Dictionary<HotkeyAction, string> DisplayNames = new Dictionary<HotkeyAction, string>
        {
            [HotkeyAction.Options]         = "Options",
            [HotkeyAction.Help]            = "Help (show shortcuts)",
            [HotkeyAction.UpdateCheck]     = "Check for Update",
            [HotkeyAction.CallCqMode]      = "Start selected CQ mode",
            [HotkeyAction.ListenMode]      = "Listen for Calls Mode",
            [HotkeyAction.EnableTx]        = "Enable Transmit",
            [HotkeyAction.HaltTx]          = "Halt Transmit",
            [HotkeyAction.NextCall]        = "Skip to Next Call",
            [HotkeyAction.ManualCall]      = "Call Callsign Manually",
            [HotkeyAction.DeleteAllCalls]  = "Delete All Available Stations",
            [HotkeyAction.TxPeriod]        = "Toggle Transmit Period",
            [HotkeyAction.HoldTimeout]     = "Toggle Extended Timeout",
            [HotkeyAction.TuneMode]        = "Toggle Tune Mode",
            [HotkeyAction.AudioUp]         = "Audio Level Up",
            [HotkeyAction.AudioDown]       = "Audio Level Down",
            [HotkeyAction.PowerSwr]        = "Quick Power / SWR Check",
            [HotkeyAction.BandUp]          = "Band Up",
            [HotkeyAction.BandDown]        = "Band Down",
            [HotkeyAction.ToggleMode]      = "Toggle Mode (FT8 / FT4)",
            [HotkeyAction.PSKReporter]     = "Toggle PSKReporter",
            [HotkeyAction.Prompts]         = "Toggle Command Prompts",
            [HotkeyAction.UploadLotw]      = "Upload to Logbook of the World",
            [HotkeyAction.SortOrder]       = "Sort Order Editor",
            [HotkeyAction.RowOrder]        = "Row Order Editor",
            [HotkeyAction.AnalyzeSlot]     = "Analyze Transmit Slot",
            [HotkeyAction.LookupStation]   = "Lookup Selected Station",
            [HotkeyAction.OpenLogbook]     = "Open Ham Radio Center Logbook",
            [HotkeyAction.AddManualQso]    = "Add Manual QSO to Logbook",
            [HotkeyAction.OpenOtaSpots]    = "Open POTA / SOTA / DX Spots",
            [HotkeyAction.ResetWindowSize] = "Reset Window Size to Default",
            [HotkeyAction.NavStatus]       = "Focus Status Area",
            [HotkeyAction.NavCallList]     = "Focus Available Stations List",
            [HotkeyAction.NavPendingCount] = "Focus Pending Count",
            [HotkeyAction.NavLoggedList]   = "Focus Auto Logged List",
            [HotkeyAction.NavLoggedCount]  = "Focus Auto Logged Count",
            [HotkeyAction.NavAdvTx1]       = "Focus TX1 Available Stations",
            [HotkeyAction.NavAdvTx2]       = "Focus TX2 Available Stations",
            [HotkeyAction.NavAdvRaw]       = "Focus Raw Decodes",
            [HotkeyAction.NavSpotWatch]    = "Focus Spot Watch List",
        };

        // Actions that may be left unassigned (Keys.None) without triggering a validation error.
        public static readonly HashSet<HotkeyAction> OptionalActions = new HashSet<HotkeyAction>
        {
            HotkeyAction.AnalyzeSlot,
            HotkeyAction.LookupStation,
            HotkeyAction.OpenLogbook,
            HotkeyAction.AddManualQso,
            HotkeyAction.OpenOtaSpots,
            HotkeyAction.NavAdvTx1,
            HotkeyAction.NavAdvTx2,
            HotkeyAction.NavAdvRaw,
            HotkeyAction.NavSpotWatch,
        };

        private static readonly HashSet<Keys> ReservedKeys = new HashSet<Keys>
        {
            Keys.Alt  | Keys.F4,
            Keys.Alt  | Keys.Tab,
            Keys.Alt  | Keys.Space,
            Keys.Control | Keys.Alt | Keys.Delete,
            Keys.Control | Keys.Q,
            Keys.Escape,
        };

        public Keys this[HotkeyAction action] => _keys[action];

        public HotkeyConfig()
        {
            foreach (var kv in Defaults)
                _keys[kv.Key] = kv.Value;
        }

        public void LoadFromIni(IniFile ini)
        {
            foreach (HotkeyAction action in Enum.GetValues(typeof(HotkeyAction)))
            {
                string stored = ini.Read(action.ToString(), IniSection);
                if (string.IsNullOrEmpty(stored)) continue;
                if (!int.TryParse(stored, out int val)) continue;
                Keys k = (Keys)val;
                if (IsValid(k) && !IsReserved(k))
                    _keys[action] = k;
            }
        }

        public void SaveToIni(IniFile ini)
        {
            foreach (var kv in _keys)
                ini.Write(kv.Key.ToString(), ((int)kv.Value).ToString(), IniSection);
        }

        public void ResetToDefaults()
        {
            foreach (var kv in Defaults)
                _keys[kv.Key] = kv.Value;
        }

        public void Apply(HotkeyAction action, Keys keys)
        {
            _keys[action] = keys;
        }

        public HotkeyAction? FindConflict(Keys keys, HotkeyAction excludeAction)
        {
            foreach (var kv in _keys)
            {
                if (kv.Key == excludeAction) continue;
                if (kv.Value == keys) return kv.Key;
            }
            return null;
        }

        public static bool IsReserved(Keys keys) => ReservedKeys.Contains(keys);

        public static bool IsValid(Keys keys)
        {
            if (keys == Keys.None) return false;
            Keys keyCode  = keys & Keys.KeyCode;
            Keys modifiers = keys & Keys.Modifiers;

            // Function keys F1-F24 are valid without a modifier
            if (keyCode >= Keys.F1 && keyCode <= Keys.F24)
                return true;

            // Everything else requires at least one non-Shift modifier
            if (modifiers == Keys.None)  return false;
            if (modifiers == Keys.Shift) return false;

            return true;
        }

        // "Alt+PageUp" format — used in capture boxes and conflict messages
        public static string FormatKeys(Keys keys)
        {
            if (keys == Keys.None) return "";
            var parts = new List<string>();
            if ((keys & Keys.Control) != 0) parts.Add("Ctrl");
            if ((keys & Keys.Alt)     != 0) parts.Add("Alt");
            if ((keys & Keys.Shift)   != 0) parts.Add("Shift");
            parts.Add(GetKeyName(keys & Keys.KeyCode));
            return string.Join("+", parts);
        }

        // "Alt, Page Up" format — screen-reader-friendly, used in the help dialog
        public static string FormatKeysForHelp(Keys keys)
        {
            if (keys == Keys.None) return "";
            var parts = new List<string>();
            if ((keys & Keys.Control) != 0) parts.Add("Ctrl");
            if ((keys & Keys.Alt)     != 0) parts.Add("Alt");
            if ((keys & Keys.Shift)   != 0) parts.Add("Shift");
            string name = GetKeyNameForHelp(keys & Keys.KeyCode);
            parts.Add(name);
            return string.Join(", ", parts);
        }

        private static string GetKeyName(Keys keyCode)
        {
            switch ((int)keyCode)
            {
                case 33: return "PageUp";
                case 34: return "PageDown";
                case (int)Keys.Return:  return "Enter";
                case (int)Keys.Back:    return "Backspace";
                case (int)Keys.Escape:  return "Escape";
                case (int)Keys.Space:   return "Space";
                case (int)Keys.Delete:  return "Delete";
                case (int)Keys.Insert:  return "Insert";
                case (int)Keys.Left:    return "Left";
                case (int)Keys.Right:   return "Right";
                case (int)Keys.Up:      return "Up";
                case (int)Keys.Down:    return "Down";
                case (int)Keys.Home:    return "Home";
                case (int)Keys.End:     return "End";
                case (int)Keys.Tab:     return "Tab";
                default:                return keyCode.ToString();
            }
        }

        private static string GetKeyNameForHelp(Keys keyCode)
        {
            switch ((int)keyCode)
            {
                case 33: return "Page Up";
                case 34: return "Page Down";
                case (int)Keys.Return:  return "Enter";
                case (int)Keys.Back:    return "Backspace";
                case (int)Keys.Escape:  return "Escape";
                case (int)Keys.Space:   return "Space";
                case (int)Keys.Delete:  return "Delete";
                case (int)Keys.Insert:  return "Insert";
                case (int)Keys.Left:    return "Left";
                case (int)Keys.Right:   return "Right";
                case (int)Keys.Up:      return "Up";
                case (int)Keys.Down:    return "Down";
                case (int)Keys.Home:    return "Home";
                case (int)Keys.End:     return "End";
                case (int)Keys.Tab:     return "Tab";
                default:
                    string s = keyCode.ToString();
                    // "F12" → "F 12" for screen readers
                    if (s.Length > 1 && s[0] == 'F' && char.IsDigit(s[1]))
                        return "F " + s.Substring(1);
                    return s;
            }
        }
    }
}
