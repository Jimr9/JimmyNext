using System;

namespace WSJTX_Controller
{
    // Options > Decode tab, added 2026-08-09: WSJT-X's own decode-related settings, ported over
    // in Nexus but never previously exposed to Jimmy. Only DecodeDepth has a live setter on the
    // engine (Engine::set_decode_depth, safe to call mid-session) -- the other four have none
    // (Nexus's Engine.settings field is private to the tempo-app crate), so they're startup-CLI-
    // arg-only: an Options change to them takes effect on the next engine restart, same as
    // rig-model/data-modes-plain-ssb/etc. already do. See NativeEngineClient.Launch() for the
    // CLI args and WsjtxClient.BandAudio.cs (or wherever the live path lands) for SET_DECODE_DEPTH.
    public class DecodeSettings
    {
        // WSJT-X "Fast/Normal/Deep". 1=Fast, 2=Normal, 3=Deep. Matches Nexus's own
        // Settings::default() (3=Deep) so an operator who never opens this tab gets the same
        // behavior Jimmy Native has always had.
        public int DecodeDepth { get; set; } = 3;

        // WSJT-X "F Low" -- decoder passband low edge in Hz. Matches Nexus's own default (200,
        // "the modem floor" per its own comment).
        public int DecodeFLowHz { get; set; } = 200;

        // WSJT-X "F High" -- decoder passband high edge in Hz. Matches Nexus's own default
        // (2900); raise toward 4000 to decode stations calling high on a crowded band.
        public int DecodeFHighHz { get; set; } = 2900;

        // WSJT-X "Enable AP" (Decode menu) -- a-priori decoding, FT8 only. Matches Nexus's own
        // default (on).
        public bool ApDecode { get; set; } = true;

        // Restrict AP to the CQ hypothesis only (expert toggle). Matches Nexus's own default
        // (off -- all AP hypotheses).
        public bool ApCqOnly { get; set; } = false;

        // "Single decode": narrows the FT8/FT4 search to the RX offset +/-25 Hz -- a genuine
        // Nexus improvement over stock WSJT-X, whose own "Single decode" checkbox is inert for
        // FT8/FT4 (verified 3.0.2 -- only JT65/Q65/FST4 read it). Matches Nexus's own default
        // (off -- full passband).
        public bool SingleDecode { get; set; } = false;

        // Nexus 1.25 decoder choice per mode: "jtdx" (the JTDX-style decoder) or "standard"
        // (WSJT-X's, at DecodeDepth). FT8 starts on JTDX; FT4 on Standard, because JTDX's FT4
        // decoder drops a doubtful decode of a call not on its active-call list. Live via
        // SET_DECODERS (EngineHost), so no engine restart.
        public string Ft8Decoder { get; set; } = "jtdx";
        public string Ft4Decoder { get; set; } = "standard";

        // The JTDX-style decoder's preset, shared by FT8 and FT4 as in Nexus:
        // "easy", "keen", "ludicrous" or "custom".
        public string JtdxPreset { get; set; } = "easy";

        // Custom's knobs (Nexus Settings.jtdx_custom), starting from Easy's as Nexus does.
        public int JtdxCycles { get; set; } = 2;
        public int JtdxSensitivity { get; set; } = 2;
        public int JtdxRxSensitivity { get; set; } = 3;
        public int JtdxThreads { get; set; } = 1;      // 0 = automatic
        public bool JtdxWideDxSearch { get; set; } = true;
        public int JtdxCandThin { get; set; } = 100;   // percent kept
        public bool JtdxSwl { get; set; } = false;

        // The EngineHost decoder spec (--decoders / SET_DECODERS). No spaces, so it passes as one
        // command-line argument.
        public string DecoderSpec()
        {
            return "ft8=" + Ft8Decoder + ";ft4=" + Ft4Decoder + ";preset=" + JtdxPreset +
                   ";custom=" + JtdxCycles + "," + JtdxSensitivity + "," + JtdxRxSensitivity + "," +
                   JtdxThreads + "," + (JtdxWideDxSearch ? 1 : 0) + "," + JtdxCandThin + "," + (JtdxSwl ? 1 : 0);
        }

        static string OneOf(string v, string fallback, params string[] allowed)
        {
            return Array.IndexOf(allowed, v) >= 0 ? v : fallback;
        }

        static int Within(IniFile ini, string key, int fallback, int min, int max)
        {
            return int.TryParse(ini.Read(key), out int v) && v >= min && v <= max ? v : fallback;
        }

        public void LoadFromIni(IniFile ini)
        {
            if (int.TryParse(ini.Read("decodeDepth"), out int depth) && depth >= 1 && depth <= 3)
                DecodeDepth = depth;
            if (int.TryParse(ini.Read("decodeFLowHz"), out int flow) && flow >= 0)
                DecodeFLowHz = flow;
            if (int.TryParse(ini.Read("decodeFHighHz"), out int fhigh) && fhigh > 0)
                DecodeFHighHz = fhigh;
            if (ini.KeyExists("decodeApDecode")) ApDecode = ini.Read("decodeApDecode") == "True";
            if (ini.KeyExists("decodeApCqOnly")) ApCqOnly = ini.Read("decodeApCqOnly") == "True";
            if (ini.KeyExists("decodeSingleDecode")) SingleDecode = ini.Read("decodeSingleDecode") == "True";
            Ft8Decoder = OneOf(ini.Read("decodeFt8Decoder"), Ft8Decoder, "jtdx", "standard");
            Ft4Decoder = OneOf(ini.Read("decodeFt4Decoder"), Ft4Decoder, "jtdx", "standard");
            JtdxPreset = OneOf(ini.Read("decodeJtdxPreset"), JtdxPreset, "easy", "keen", "ludicrous", "custom");
            JtdxCycles = Within(ini, "decodeJtdxCycles", JtdxCycles, 1, 3);
            JtdxSensitivity = Within(ini, "decodeJtdxSensitivity", JtdxSensitivity, 1, 3);
            JtdxRxSensitivity = Within(ini, "decodeJtdxRxSensitivity", JtdxRxSensitivity, 1, 3);
            JtdxThreads = Within(ini, "decodeJtdxThreads", JtdxThreads, 0, 12);
            JtdxCandThin = Within(ini, "decodeJtdxCandThin", JtdxCandThin, 1, 100);
            if (ini.KeyExists("decodeJtdxWideDxSearch")) JtdxWideDxSearch = ini.Read("decodeJtdxWideDxSearch") == "True";
            if (ini.KeyExists("decodeJtdxSwl")) JtdxSwl = ini.Read("decodeJtdxSwl") == "True";
        }

        public void SaveToIni(IniFile ini)
        {
            ini.Write("decodeDepth", DecodeDepth.ToString());
            ini.Write("decodeFLowHz", DecodeFLowHz.ToString());
            ini.Write("decodeFHighHz", DecodeFHighHz.ToString());
            ini.Write("decodeApDecode", ApDecode.ToString());
            ini.Write("decodeApCqOnly", ApCqOnly.ToString());
            ini.Write("decodeSingleDecode", SingleDecode.ToString());
            ini.Write("decodeFt8Decoder", Ft8Decoder);
            ini.Write("decodeFt4Decoder", Ft4Decoder);
            ini.Write("decodeJtdxPreset", JtdxPreset);
            ini.Write("decodeJtdxCycles", JtdxCycles.ToString());
            ini.Write("decodeJtdxSensitivity", JtdxSensitivity.ToString());
            ini.Write("decodeJtdxRxSensitivity", JtdxRxSensitivity.ToString());
            ini.Write("decodeJtdxThreads", JtdxThreads.ToString());
            ini.Write("decodeJtdxWideDxSearch", JtdxWideDxSearch.ToString());
            ini.Write("decodeJtdxCandThin", JtdxCandThin.ToString());
            ini.Write("decodeJtdxSwl", JtdxSwl.ToString());
        }
    }
}
