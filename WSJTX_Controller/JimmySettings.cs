using System.Collections.Generic;
using System.Drawing;

namespace WSJTX_Controller
{
    // First slice of settings/INI extraction (Phase 2 of the modernization plan):
    // the Advanced Call Layout display flags, modeled on HotkeyConfig.cs's
    // LoadFromIni/SaveToIni pattern. Deliberately scoped small -- Controller.Form_Load
    // has ~270 more interleaved settings reads (plus a legacy Properties.Settings.Default
    // migration path) that stay inline for now rather than risk a single large,
    // hard-to-review diff. See the modernization plan doc for the full rationale.
    //
    // LoadFromIni intentionally preserves an existing quirk rather than "fixing" it:
    // AdvancedCallLayout reads as `== "True"` (missing/empty key -> false) while the
    // other three read as `!= "False"` (missing/empty key -> true) -- this mismatch
    // already existed in Controller.Form_Load and is preserved here byte-for-byte
    // rather than silently changed as part of a refactor.
    public class JimmySettings
    {
        public bool AdvancedCallLayout { get; set; } = true;
        public bool AdvShowTx1 { get; set; } = true;
        public bool AdvShowTx2 { get; set; } = true;
        public bool AdvShowRaw { get; set; } = true;

        // Independent of AdvancedCallLayout/AdvShowTx1/2/Raw -- this is a separate feature
        // (DX Spot Watch), not part of the Advanced Call Layout display. Opt-in, default off.
        public bool ShowSpotWatch { get; set; } = false;

        // Smart QSO Start (2.0.63): OFF preserves today's Enter behaviour exactly (reply
        // immediately). ON means "work this station when it is appropriate" instead -- see
        // TargetMonitor/WsjtxClient.StationWatch.cs. Opt-in, default off so nothing changes for
        // an operator who never visits Options for it.
        public bool SmartQsoStartEnabled { get; set; } = false;

        // "Start after target not heard for: N receive periods" -- 1-10, default 2. Counts
        // completed, appropriate-parity receive opportunities, not wall-clock seconds (see
        // TargetMonitor.OnReceivePeriodComplete's own comment for exactly what counts).
        public int SmartStartSilencePeriods { get; set; } = 2;

        // Operator request (2026-09-13), reworked 2026-09-22 from "busy dead-end" round counting
        // into a consecutive target-not-heard limit -- same setting/storage key kept for profile
        // compatibility, new meaning: how many COMPLETED Smart Start calling-over transmissions in
        // a row may go out to one target with NO live decode from it at all (a decode showing the
        // target busy with someone else still resets this -- see TargetMonitor.TargetNotHeardStreak
        // and NoteCallOverCompletedAndCheckNotHeardLimit for exactly what counts) before Smart
        // Start disarms itself: the target may be gone, or propagation may have changed.
        // Adjustable, 1-20, default 4 so nothing changes for an operator who never visits Options
        // for it.
        public int SmartStartMaxStandbyRounds { get; set; } = 4;

        // Operator request (2026-09-13): an absolute wall-clock backstop on the WHOLE Smart Start
        // effort, independent of the Repeat Limit (which only counts actual transmitted calls) and
        // the consecutive target-not-heard limit above -- "so they know an hour later their radio
        // will not start trying to call the station," regardless of how many calls/silent rounds
        // happened. Minutes; 0 = no limit (today's behavior, and the default).
        // Counts from the ORIGINAL arm time and is NOT reset by a busy-yield/resume cycle -- only
        // a fresh Start() (arming on a target) resets it. See TargetMonitor.ArmedAtUtc.
        public int SmartStartTimeLimitMinutes { get; set; } = 0;

        // VP5/K5UR live incident (2026-09-14): the KA1BMF active-partner-working-another guard
        // (WsjtxClient.cs) used to yield the contact the very first time it saw the partner send
        // a substantive over to someone else -- too eager for a big pileup-running DX station
        // genuinely juggling several simultaneous QSOs by hand. 1-6, default 2 (tolerate one
        // interleaved reply before concluding abandonment); 1 restores the original immediate-
        // yield behavior. Counts distinct other-station transmit periods, not decodes -- see
        // WsjtxClient's own _otherPartyOverStrikes comment. Applies to manual/Enter-started QSOs
        // and Smart-Start-originated ones alike (one shared guard) -- it lives in this settings
        // group for lack of a better home, not because it is Smart-Start-only. The existing
        // Repeat Limit and Smart Start time limit remain authoritative regardless of this value.
        public int OtherStationRepliesBeforeYielding { get; set; } = 2;

        // Appearance (list font size + colors) -- defaults match the app's original
        // hardcoded look exactly, so nothing changes for anyone who never opens the
        // new Appearance tab. Colors are stored as ARGB ints (unambiguous, no named-
        // color parsing needed) rather than as strings.
        public int ListFontSize { get; set; } = 10;
        public Color ListBackColor { get; set; } = SystemColors.Window;
        public Color ListForeColor { get; set; } = SystemColors.WindowText;
        public Color ListAltRowColor { get; set; } = Color.FromArgb(233, 233, 233);

        // Per-alert-category row colors (Options > Appearance). A category with no
        // entry here falls back to the normal list colors above -- so nothing changes
        // for anyone who never opens the alert color picker. DEFAULT is excluded; it's
        // not an alert, it's every ordinary row.
        public static readonly WsjtxClient.CallCategory[] AlertCategories =
        {
            WsjtxClient.CallCategory.NEW_COUNTRY,
            WsjtxClient.CallCategory.NEW_COUNTRY_ON_BAND,
            WsjtxClient.CallCategory.TO_MYCALL,
            WsjtxClient.CallCategory.MANUAL_SEL,
            WsjtxClient.CallCategory.WANTED_CQ,
            WsjtxClient.CallCategory.POTA,
            WsjtxClient.CallCategory.SOTA,
            WsjtxClient.CallCategory.ALWAYS_WANTED,
            WsjtxClient.CallCategory.WAS_NEEDED,
            WsjtxClient.CallCategory.WAS_UNCONFIRMED,
            WsjtxClient.CallCategory.DXCC_UNCONFIRMED,
            WsjtxClient.CallCategory.ZONE_NEEDED,
            WsjtxClient.CallCategory.STILL_NEEDED,
        };

        public static readonly Dictionary<WsjtxClient.CallCategory, string> AlertCategoryLabels =
            new Dictionary<WsjtxClient.CallCategory, string>
        {
            { WsjtxClient.CallCategory.NEW_COUNTRY,         "New DXCC" },
            { WsjtxClient.CallCategory.NEW_COUNTRY_ON_BAND, "New DXCC on band" },
            { WsjtxClient.CallCategory.TO_MYCALL,           "Calling me" },
            { WsjtxClient.CallCategory.MANUAL_SEL,          "Manual selection" },
            { WsjtxClient.CallCategory.WANTED_CQ,           "Directed CQ" },
            { WsjtxClient.CallCategory.POTA,                "POTA" },
            { WsjtxClient.CallCategory.SOTA,                "SOTA" },
            { WsjtxClient.CallCategory.ALWAYS_WANTED,       "Always wanted" },
            { WsjtxClient.CallCategory.WAS_NEEDED,          "WAS needed" },
            { WsjtxClient.CallCategory.WAS_UNCONFIRMED,     "WAS unconfirmed" },
            { WsjtxClient.CallCategory.DXCC_UNCONFIRMED,    "DXCC unconfirmed" },
            { WsjtxClient.CallCategory.ZONE_NEEDED,         "Zone needed" },
            { WsjtxClient.CallCategory.STILL_NEEDED,        "Award needed" },
        };

        public Dictionary<WsjtxClient.CallCategory, Color?> AlertForeColors { get; } =
            new Dictionary<WsjtxClient.CallCategory, Color?>();
        public Dictionary<WsjtxClient.CallCategory, Color?> AlertBackColors { get; } =
            new Dictionary<WsjtxClient.CallCategory, Color?>();

        public void LoadFromIni(IniFile ini)
        {
            AdvancedCallLayout = ini.Read("advCallLayout") == "True";
            AdvShowTx1 = ini.Read("advShowTx1") != "False";
            AdvShowTx2 = ini.Read("advShowTx2") != "False";
            AdvShowRaw = ini.Read("advShowRaw") != "False";
            ShowSpotWatch = ini.Read("showSpotWatch") == "True";
            SmartQsoStartEnabled = ini.Read("smartQsoStartEnabled") == "True";
            if (int.TryParse(ini.Read("smartStartSilencePeriods"), out int silencePeriods) && silencePeriods >= 1 && silencePeriods <= 10)
                SmartStartSilencePeriods = silencePeriods;
            if (int.TryParse(ini.Read("smartStartMaxStandbyRounds"), out int maxStandbyRounds) && maxStandbyRounds >= 1 && maxStandbyRounds <= 20)
                SmartStartMaxStandbyRounds = maxStandbyRounds;
            if (int.TryParse(ini.Read("smartStartTimeLimitMinutes"), out int timeLimitMinutes) && timeLimitMinutes >= 0 && timeLimitMinutes <= 999)
                SmartStartTimeLimitMinutes = timeLimitMinutes;
            if (int.TryParse(ini.Read("otherStationRepliesBeforeYielding"), out int repliesBeforeYielding) && repliesBeforeYielding >= 1 && repliesBeforeYielding <= 6)
                OtherStationRepliesBeforeYielding = repliesBeforeYielding;

            if (int.TryParse(ini.Read("listFontSize"), out int fontSize) && fontSize >= 8 && fontSize <= 18)
                ListFontSize = fontSize;
            ListBackColor = ReadColor(ini, "listBackColor", ListBackColor);
            ListForeColor = ReadColor(ini, "listForeColor", ListForeColor);
            ListAltRowColor = ReadColor(ini, "listAltRowColor", ListAltRowColor);

            foreach (var cat in AlertCategories)
            {
                AlertForeColors[cat] = ReadOptionalColor(ini, $"alertFore_{cat}");
                AlertBackColors[cat] = ReadOptionalColor(ini, $"alertBack_{cat}");
            }
        }

        public void SaveToIni(IniFile ini)
        {
            ini.Write("advCallLayout", AdvancedCallLayout.ToString());
            ini.Write("advShowTx1", AdvShowTx1.ToString());
            ini.Write("advShowTx2", AdvShowTx2.ToString());
            ini.Write("advShowRaw", AdvShowRaw.ToString());
            ini.Write("showSpotWatch", ShowSpotWatch.ToString());
            ini.Write("smartQsoStartEnabled", SmartQsoStartEnabled.ToString());
            ini.Write("smartStartSilencePeriods", SmartStartSilencePeriods.ToString());
            ini.Write("smartStartMaxStandbyRounds", SmartStartMaxStandbyRounds.ToString());
            ini.Write("smartStartTimeLimitMinutes", SmartStartTimeLimitMinutes.ToString());
            ini.Write("otherStationRepliesBeforeYielding", OtherStationRepliesBeforeYielding.ToString());

            ini.Write("listFontSize", ListFontSize.ToString());
            ini.Write("listBackColor", ListBackColor.ToArgb().ToString());
            ini.Write("listForeColor", ListForeColor.ToArgb().ToString());
            ini.Write("listAltRowColor", ListAltRowColor.ToArgb().ToString());

            foreach (var cat in AlertCategories)
            {
                WriteOptionalColor(ini, $"alertFore_{cat}", AlertForeColors.TryGetValue(cat, out var fc) ? fc : null);
                WriteOptionalColor(ini, $"alertBack_{cat}", AlertBackColors.TryGetValue(cat, out var bc) ? bc : null);
            }
        }

        private static Color ReadColor(IniFile ini, string key, Color fallback)
        {
            string raw = ini.Read(key);
            return int.TryParse(raw, out int argb) ? Color.FromArgb(argb) : fallback;
        }

        private static Color? ReadOptionalColor(IniFile ini, string key)
        {
            string raw = ini.Read(key);
            return int.TryParse(raw, out int argb) ? (Color?)Color.FromArgb(argb) : null;
        }

        private static void WriteOptionalColor(IniFile ini, string key, Color? color)
        {
            if (color.HasValue) ini.Write(key, color.Value.ToArgb().ToString());
            else ini.DeleteKey(key);
        }
    }
}
