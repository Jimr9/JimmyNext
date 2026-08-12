namespace WSJTX_Controller
{
    // Options > Frequencies tab, added 2026-08-09: lets the operator override Jimmy's own
    // built-in per-band FT8/FT4 calling frequencies (WsjtxClient.cs's freqsDict), the same
    // frequencies Band Up/Down/the band-number hotkeys and the initial-connect band lookup all
    // read through bandToFreq(). Mirrors WSJT-X's own Settings ▸ Frequencies in spirit, but not
    // in shape: WSJT-X keeps a variable-length, multi-entry-per-band-and-mode table; Jimmy's own
    // Band Up/Down model is one canonical frequency per band (the `bands` list, 160/80/60/40/30/
    // 20/17/15/12/10/6 -- index-aligned with freqsDict's own per-mode lists), so this is a
    // straight per-band override/revert-to-default, not a free-form add/remove list. Values are
    // in kHz, matching freqsDict's own units.
    public class FrequencySettings
    {
        // 0 = no override, use freqsDict's built-in default for that band/mode. Index-aligned
        // with WsjtxClient.bands (11 entries: 160/80/60/40/30/20/17/15/12/10/6).
        public int[] Ft8OverrideKHz { get; set; } = new int[11];
        public int[] Ft4OverrideKHz { get; set; } = new int[11];

        public void LoadFromIni(IniFile ini)
        {
            LoadOne(ini, "freqFt8Override", Ft8OverrideKHz);
            LoadOne(ini, "freqFt4Override", Ft4OverrideKHz);
        }

        public void SaveToIni(IniFile ini)
        {
            SaveOne(ini, "freqFt8Override", Ft8OverrideKHz);
            SaveOne(ini, "freqFt4Override", Ft4OverrideKHz);
        }

        private static void LoadOne(IniFile ini, string keyPrefix, int[] target)
        {
            for (int i = 0; i < target.Length; i++)
            {
                if (int.TryParse(ini.Read(keyPrefix + i), out int v) && v >= 0)
                    target[i] = v;
            }
        }

        private static void SaveOne(IniFile ini, string keyPrefix, int[] source)
        {
            for (int i = 0; i < source.Length; i++)
                ini.Write(keyPrefix + i, source[i].ToString());
        }
    }
}
