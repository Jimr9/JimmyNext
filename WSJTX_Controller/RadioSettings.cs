using System;

namespace WSJTX_Controller
{
    // Phase 0 of the self-sufficiency plan: settings shape for direct Hamlib/rigctld radio
    // control, modeled on JimmySettings.cs's LoadFromIni/SaveToIni pattern. Nothing reads these
    // fields yet -- Mode defaults to WsjtxCat, so behavior is unchanged until Phase 1 wires a
    // RigctldClient up to them. Field set already reflects Phase 1's planned bundled-rigctld
    // design (UseExternalRigctld/RigModel/ComPort/PttEnabled) so this file doesn't need to be
    // revisited structurally when Phase 1 lands.
    public class RadioSettings
    {
        public RadioControlMode Mode { get; set; } = RadioControlMode.WsjtxCat;

        // false (default): Jimmy launches its own bundled rigctld.exe (Phase 1) against
        // RigModel/ComPort. true: connect to an already-running rigctld elsewhere instead,
        // using RigctldHost/RigctldPort.
        public bool UseExternalRigctld { get; set; } = false;

        public string RigctldHost { get; set; } = "127.0.0.1";
        public int RigctldPort { get; set; } = 4532;   // Hamlib's documented rigctld default

        // Hamlib rig-model number as a string (e.g. "2037" for the Kenwood TS-590SG).
        // Display/config only until Phase 1's RigctldClient reads it.
        public string RigModel { get; set; } = "";
        public string ComPort { get; set; } = "";

        // Empty = let Hamlib use its own built-in default baud rate for RigModel. Added
        // 2026-08-06 while diagnosing a live TX test where PTT never engaged against a real
        // Kenwood TS-590SG: rigctld's own -s/--serial-speed flag was never wired up at all, so
        // there was no way to correct a baud-rate mismatch between rigctld's assumed default and
        // the rig's own CAT baud rate menu setting -- a mismatch there is a silent, total CAT
        // communication failure. Real WSJT-X exposes this same setting on its own Radio tab.
        public string BaudRate { get; set; } = "";

        // Opt-in, default off: use rigctld for PTT instead of WSJT-X's own CAT-driven PTT.
        // A bigger behavioral change than read-only telemetry, so it gets its own separate
        // default-off flag rather than following Mode automatically.
        public bool PttEnabled { get; set; } = false;

        // How PTT is actually keyed, independent of whether CAT control is open -- mirrors
        // Nexus's own tempo_audio::rig::PttMode 1:1 (Cat/Vox/Serial{Rts,Dtr}) since that's what
        // the native engine host now builds directly (Phase 5). Only consulted when PttEnabled
        // is true; PttEnabled=false always means Vox regardless of this setting (see
        // ApplyEngineMode's own comment on why -- S-meter/frequency should stay live under CAT
        // even with PTT itself turned off, matching Rig's own control/PTT separation).
        public PttMethod PttMethod { get; set; } = PttMethod.Cat;

        public bool PollEnabled { get; set; } = false;
        public int PollIntervalMs { get; set; } = 1000;

        // Default off: Jimmy Native's Digital operating mode always commands the rig's DATA
        // submode (PKTUSB/PKTLSB) over CAT for FT8/FT4, matching what real WSJT-X's own Radio
        // tab "Data/Pkt" Mode choice does. Nexus itself warns this off-by-default flag is
        // "wiring-dependent, and wrong for most rigs" -- it exists for a mic-jack-wired
        // interface, where plain USB/LSB (not the DATA submode) is what actually routes TX
        // audio correctly. Exposed here as the accessible equivalent of WSJT-X's Radio tab
        // "Mode: USB" choice, an experiment to try if the automatic Data/Pkt path doesn't
        // route TX audio correctly on a given rig -- confirmed live, 2026-08-07, that a real
        // TS-590SG transmitted mic audio instead of the FT8 tone despite CAT read-back
        // reporting the DATA submode correctly, with no operator-facing way to try the
        // alternative before this existed.
        public bool DataModesPlainSsb { get; set; } = false;

        // Matches WSJT-X's own Radio tab "Split Operation" choice. Only two of its three
        // options are real here: "Rig" sends rigctld's own live set_split_vfo command ("S 1
        // VFOB" / "S 0 VFOA"). WSJT-X's third option, "Fake It" (WSJT-X itself retunes the VFO
        // before/after each transmission instead of using true hardware split), has no
        // equivalent without deep integration into Nexus's own per-slot TX scheduling -- not
        // built. Rarely relevant to FT8/FT4 (conventionally simplex), included for parity with
        // WSJT-X's Radio tab per the operator's own request, 2026-08-07.
        public RadioSplitMode SplitMode { get; set; } = RadioSplitMode.None;

        // Matches WSJT-X's own Radio tab "Rig Data" section. ReadDisplayPwrSwr is the master
        // toggle for polling S-meter/power/SWR at all (an alias, in spirit, for the existing
        // PollEnabled -- kept as a separate field so a future UI can label/describe it exactly
        // like WSJT-X's own checkbox without also implying anything about poll timing, which
        // PollIntervalMs already owns). HaltTxOnHighSwr + SwrHaltThreshold add a real safety
        // feature WSJT-X has that Jimmy did not: automatically halting transmission if a poll
        // reports SWR above the threshold. Both requested by the operator, 2026-08-07, matching
        // WSJT-X's Radio tab "Halt Tx when SWR > 2.5" default.
        public bool ReadDisplayPwrSwr { get; set; } = false;
        public bool HaltTxOnHighSwr { get; set; } = false;
        public double SwrHaltThreshold { get; set; } = 2.5;

        public void LoadFromIni(IniFile ini)
        {
            if (Enum.TryParse(ini.Read("radioControlMode"), out RadioControlMode mode))
                Mode = mode;
            UseExternalRigctld = ini.Read("radioUseExternalRigctld") == "True";
            if (ini.KeyExists("radioRigctldHost")) RigctldHost = ini.Read("radioRigctldHost");
            if (int.TryParse(ini.Read("radioRigctldPort"), out int port) && port > 0 && port <= 65535)
                RigctldPort = port;
            if (ini.KeyExists("radioRigModel")) RigModel = ini.Read("radioRigModel");
            if (ini.KeyExists("radioComPort")) ComPort = ini.Read("radioComPort");
            if (ini.KeyExists("radioBaudRate")) BaudRate = ini.Read("radioBaudRate");
            if (Enum.TryParse(ini.Read("radioPttMethod"), out PttMethod pttMethod))
                PttMethod = pttMethod;
            PttEnabled = ini.Read("radioPttEnabled") == "True";
            PollEnabled = ini.Read("radioPollEnabled") == "True";
            if (int.TryParse(ini.Read("radioPollIntervalMs"), out int interval) && interval >= 200)
                PollIntervalMs = interval;
            DataModesPlainSsb = ini.Read("radioDataModesPlainSsb") == "True";
            if (Enum.TryParse(ini.Read("radioSplitMode"), out RadioSplitMode splitMode))
                SplitMode = splitMode;
            ReadDisplayPwrSwr = ini.Read("radioReadDisplayPwrSwr") == "True";
            HaltTxOnHighSwr = ini.Read("radioHaltTxOnHighSwr") == "True";
            if (double.TryParse(ini.Read("radioSwrHaltThreshold"), out double swrThreshold) && swrThreshold > 0)
                SwrHaltThreshold = swrThreshold;
        }

        public void SaveToIni(IniFile ini)
        {
            ini.Write("radioControlMode", Mode.ToString());
            ini.Write("radioUseExternalRigctld", UseExternalRigctld.ToString());
            ini.Write("radioRigctldHost", RigctldHost);
            ini.Write("radioRigctldPort", RigctldPort.ToString());
            ini.Write("radioRigModel", RigModel);
            ini.Write("radioComPort", ComPort);
            ini.Write("radioBaudRate", BaudRate);
            ini.Write("radioPttMethod", PttMethod.ToString());
            ini.Write("radioPttEnabled", PttEnabled.ToString());
            ini.Write("radioPollEnabled", PollEnabled.ToString());
            ini.Write("radioPollIntervalMs", PollIntervalMs.ToString());
            ini.Write("radioDataModesPlainSsb", DataModesPlainSsb.ToString());
            ini.Write("radioSplitMode", SplitMode.ToString());
            ini.Write("radioReadDisplayPwrSwr", ReadDisplayPwrSwr.ToString());
            ini.Write("radioHaltTxOnHighSwr", HaltTxOnHighSwr.ToString());
            ini.Write("radioSwrHaltThreshold", SwrHaltThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    public enum RadioSplitMode
    {
        None,
        Rig,
    }
}
