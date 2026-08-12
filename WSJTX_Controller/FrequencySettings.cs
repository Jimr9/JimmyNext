using System.Collections.Generic;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // A single calling-frequency entry: which mode it's for, its frequency in kHz, and an
    // optional hotkey that jumps straight to it (retunes and switches mode if needed).
    public class FrequencyEntry
    {
        public string Mode { get; set; }        // "FT8" or "FT4"
        public int FreqKHz { get; set; }
        public Keys Hotkey { get; set; } = Keys.None;

        public FrequencyEntry Clone() => new FrequencyEntry { Mode = Mode, FreqKHz = FreqKHz, Hotkey = Hotkey };
    }

    // Options > Frequencies tab, rebuilt 2026-08-12 from the old fixed one-override-per-band
    // model (a single NumericUpDown per band per mode, 22 fixed fields to tab through) into a
    // real per-band list the operator arrows through, with Add/Remove and a per-entry hotkey.
    // Replaces the 11 old fixed per-band hotkeys (HotkeyAction.Band160m..Band6m, removed
    // entirely) -- every frequency row, not just one per band, can now carry its own direct-jump
    // hotkey. No migration path for the old fixed hotkeys or the old override values: all 11 old
    // hotkeys defaulted to Keys.None (operator request: not worth the code to carry forward
    // whatever few may have been manually set).
    public class FrequencySettings
    {
        // Index-aligned with WsjtxClient.bands (11: 160/80/60/40/30/20/17/15/12/10/6). Empty for
        // a band the operator has never customized -- WsjtxClient.BandAudio.cs's bandToFreq()
        // falls back to WsjtxClient.FreqsDictDefaults in that case, so this class never needs to
        // duplicate those built-in values itself. When non-empty, kept sorted ascending by
        // FreqKHz; the FIRST entry matching the current mode is that band's primary/canonical
        // frequency (what Band Up/Down/SelectBand tune to) -- every other entry is a direct-jump
        // extra, reachable only via its own assigned hotkey (WsjtxClient.SelectFrequency).
        public List<FrequencyEntry>[] Bands { get; } = InitBands();

        private static List<FrequencyEntry>[] InitBands()
        {
            var arr = new List<FrequencyEntry>[11];
            for (int i = 0; i < arr.Length; i++) arr[i] = new List<FrequencyEntry>();
            return arr;
        }

        public void LoadFromIni(IniFile ini)
        {
            for (int i = 0; i < Bands.Length; i++) Bands[i].Clear();
            string raw = ini.Read("freqEntries");
            if (string.IsNullOrEmpty(raw)) return;
            foreach (string entry in raw.Split(';'))
            {
                string[] parts = entry.Split(':');
                if (parts.Length != 4) continue;
                if (!int.TryParse(parts[0], out int bandIdx) || bandIdx < 0 || bandIdx >= Bands.Length) continue;
                string mode = parts[1];
                if (mode != "FT8" && mode != "FT4") continue;
                if (!int.TryParse(parts[2], out int freqKHz) || freqKHz <= 0) continue;
                if (!int.TryParse(parts[3], out int hotkeyVal)) continue;
                Bands[bandIdx].Add(new FrequencyEntry { Mode = mode, FreqKHz = freqKHz, Hotkey = (Keys)hotkeyVal });
            }
            for (int i = 0; i < Bands.Length; i++)
                Bands[i].Sort((a, b) => a.FreqKHz.CompareTo(b.FreqKHz));
        }

        public void SaveToIni(IniFile ini)
        {
            var parts = new List<string>();
            for (int i = 0; i < Bands.Length; i++)
                foreach (var e in Bands[i])
                    parts.Add($"{i}:{e.Mode}:{e.FreqKHz}:{(int)e.Hotkey}");
            ini.Write("freqEntries", string.Join(";", parts));
        }
    }
}
