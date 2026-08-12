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

        // Matches WSJT-X's own Radio tab "Mode" choice exactly: None / USB / Data-Pkt.
        // "DataPkt" (the default) commands the rig's DATA submode (PKTUSB/PKTLSB) over CAT for
        // every FT8/FT4 transmission. "Usb" commands plain USB/LSB instead -- Nexus itself warns
        // this is "wiring-dependent, and wrong for most rigs", meant for a mic-jack-wired
        // interface where plain USB/LSB (not the DATA submode) is what actually routes TX audio
        // correctly. "None" means Jimmy never sends the rig a mode command at all, for any
        // operating mode -- the operator's own manual rig setting stands; needs jimmy-engine-host
        // built after 2026-08-11 (Settings.dont_set_mode). NOTE: a real TS-590SG transmitting mic
        // audio instead of the FT8 tone *despite* CAT read-back correctly reporting the DATA
        // submode (confirmed live, 2026-08-07) is a separate issue from Mode entirely -- see
        // PttDataSource below, WSJT-X's actual fix for that exact symptom.
        public RadioTxMode TxMode { get; set; } = RadioTxMode.DataPkt;

        // Default off, matching WSJT-X's own default. Real WSJT-X's Radio tab has a "Transmit
        // Audio Source: Mic / Data" choice (Configuration.cpp's TX_audio_source_button_group) --
        // only enabled at all when the selected rig's Hamlib backend reports the mic/data PTT
        // capability AND PTT Method is CAT (HamlibTransceiver::do_ptt's RIG_PTT_RIG_MICDATA
        // check). When on, PTT keys with RIG_PTT_ON_DATA instead of plain RIG_PTT_ON -- telling
        // a rig with separate mic/data audio inputs to transmit from the DATA input. This is
        // the real, WSJT-X-proven fix for a rig transmitting mic audio instead of the FT8 tone
        // even though CAT mode already correctly shows the DATA submode (confirmed live on a
        // TS-590SG, 2026-08-07) -- a different problem from DataModesPlainSsb above, which only
        // affects the CAT *mode* command, not PTT. Try this only if your interface is wired to
        // the rig's rear DATA/ACC port and audio still isn't routing correctly on PTT.
        public bool PttDataSource { get; set; } = false;

        // Matches WSJT-X's own Radio tab "Split Operation" choice exactly: None / Rig / Fake It.
        // "Rig" is true hardware split via CAT (RX VFO A, TX VFO B). "Fake It" is software-
        // emulated: no real rig split needed -- Nexus's own engine retunes the single VFO right
        // before each transmission (keeping the audio tone in the clean 1500-2000 Hz window) and
        // restores it after (Engine::split_reduce / apply_tx_dial_shift, already fully built,
        // matching WSJT-X's real "Split Operation" behavior, not a stub). Needs
        // jimmy-engine-host built after 2026-08-11 (Settings.split_mode was never threaded
        // through before then, so "Rig" split's own per-transmission dial math never actually
        // ran despite the setting existing).
        public RadioSplitMode SplitMode { get; set; } = RadioSplitMode.None;

        // WSJT-X Radio tab "PTT Method" > "Port" -- a separate serial port for RTS/DTR PTT when
        // it differs from the CAT serial port (an SO2R controller routing keying on its own COM
        // port). Empty (default) = key on the same port as CAT. Nexus's engine already supports
        // this (Settings.ptt_serial_port); it was just never exposed in Jimmy's own settings.
        public string PttSerialPort { get; set; } = "";

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

        // Added 2026-08-09: works around a real Hamlib bug (Hamlib/Hamlib#1595, Kenwood rigs --
        // reading OR setting RFPOWER on a freshly-spawned rigctld.exe triggers a destructive
        // calibration sweep the very first time, because the backend's "did the mode change"
        // cache is a static variable that starts unset every process). The native engine's own
        // routine PWR/SWR telemetry polling (ReadDisplayPwrSwr above) is exactly the kind of
        // first-interaction that trips it, once, at startup -- confirmed live the same day this
        // was added: 100W dialed on a real TS-590SG dropped to 5W the moment Jimmy connected,
        // reproduced with a bare two-line Hamlib rigctl test with neither Jimmy nor the engine
        // running at all. Real WSJT-X never hits this because it has no such polling to begin
        // with. Opt-in, default off: when enabled, jimmy-engine-host explicitly commands the
        // radio to StartupPowerWatts/StartupPowerMaxWatts once, right at startup -- not a
        // continuous override, so changing power by hand on the rig afterward (including
        // switching bands, which never re-triggers the underlying bug) sticks exactly like the
        // operator asked.
        public bool StartupPowerEnabled { get; set; } = false;
        public int StartupPowerWatts { get; set; } = 100;
        public int StartupPowerMaxWatts { get; set; } = 100;

        // F11/F12 (Audio Level up/down) step size, as a whole-number percentage of the engine's
        // 0.0-1.0 mic_gain range -- WsjtxClient.BandAudio.cs's AudioLevel() reads this instead of
        // a hardcoded step. Default 5 matches the step size those hotkeys always used before this
        // was configurable.
        public int AudioStepPercent { get; set; } = 5;

        // Added 2026-08-10: the last confirmed band index (WsjtxClient.bands: 160/80/60/40/30/
        // 20/17/15/12/10/6), persisted across sessions so Direct-mode startup can restore it
        // instead of leaving the operator on "Unknown band" until real CAT data eventually
        // arrives. -1 = never confirmed a band yet (first run, or WsjtxCat mode with no CAT to
        // read back from) -- startup falls back to 20m (index 5) in that case, matching the UDP
        // path's own long-standing InitialConnect default (WsjtxClient.Protocol.cs).
        public int LastBandIdx { get; set; } = -1;

        // Requested 2026-08-11: F11/F12 (AudioLevel(), WsjtxClient.BandAudio.cs) always applied
        // one carried-over level across every band. Opt-in (default off, matches current
        // behavior exactly when unchecked) -- when on, WsjtxClient.Direct.cs's own band-change
        // detection restores whichever level was last set for the NEW band, and AudioLevel()
        // records into TxLevelByBand on every F11/F12 press. Keyed by WsjtxClient.bands index
        // (same indexing as LastBandIdx above), value is the raw 0.0-1.0 engine tx_level.
        public bool RememberTxLevelPerBand { get; set; } = false;
        public System.Collections.Generic.Dictionary<int, double> TxLevelByBand { get; } = new System.Collections.Generic.Dictionary<int, double>();

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
            if (Enum.TryParse(ini.Read("radioTxMode"), out RadioTxMode txMode))
                TxMode = txMode;
            else if (ini.KeyExists("radioDataModesPlainSsb"))
                // One-time migration from the old on/off checkbox: True meant plain USB.
                TxMode = ini.Read("radioDataModesPlainSsb") == "True" ? RadioTxMode.Usb : RadioTxMode.DataPkt;
            PttDataSource = ini.Read("radioPttDataSource") == "True";
            if (Enum.TryParse(ini.Read("radioSplitMode"), out RadioSplitMode splitMode))
                SplitMode = splitMode;
            if (ini.KeyExists("radioPttSerialPort")) PttSerialPort = ini.Read("radioPttSerialPort");
            ReadDisplayPwrSwr = ini.Read("radioReadDisplayPwrSwr") == "True";
            HaltTxOnHighSwr = ini.Read("radioHaltTxOnHighSwr") == "True";
            if (double.TryParse(ini.Read("radioSwrHaltThreshold"), out double swrThreshold) && swrThreshold > 0)
                SwrHaltThreshold = swrThreshold;
            StartupPowerEnabled = ini.Read("radioStartupPowerEnabled") == "True";
            if (int.TryParse(ini.Read("radioStartupPowerWatts"), out int startupWatts) && startupWatts > 0)
                StartupPowerWatts = startupWatts;
            if (int.TryParse(ini.Read("radioStartupPowerMaxWatts"), out int startupMaxWatts) && startupMaxWatts > 0)
                StartupPowerMaxWatts = startupMaxWatts;
            if (int.TryParse(ini.Read("radioAudioStepPercent"), out int audioStep) && audioStep >= 1 && audioStep <= 25)
                AudioStepPercent = audioStep;
            if (int.TryParse(ini.Read("radioLastBandIdx"), out int lastBandIdx) && lastBandIdx >= 0)
                LastBandIdx = lastBandIdx;
            RememberTxLevelPerBand = ini.Read("radioRememberTxLevelPerBand") == "True";
            TxLevelByBand.Clear();
            string txLevelByBand = ini.Read("radioTxLevelByBand");
            if (!string.IsNullOrEmpty(txLevelByBand))
            {
                foreach (string entry in txLevelByBand.Split(';'))
                {
                    string[] parts = entry.Split('=');
                    if (parts.Length == 2
                        && int.TryParse(parts[0], out int idx)
                        && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double level)
                        && idx >= 0 && level >= 0.0 && level <= 1.0)
                        TxLevelByBand[idx] = level;
                }
            }
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
            ini.Write("radioTxMode", TxMode.ToString());
            ini.Write("radioPttDataSource", PttDataSource.ToString());
            ini.Write("radioSplitMode", SplitMode.ToString());
            ini.Write("radioPttSerialPort", PttSerialPort);
            ini.Write("radioReadDisplayPwrSwr", ReadDisplayPwrSwr.ToString());
            ini.Write("radioHaltTxOnHighSwr", HaltTxOnHighSwr.ToString());
            ini.Write("radioSwrHaltThreshold", SwrHaltThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture));
            ini.Write("radioStartupPowerEnabled", StartupPowerEnabled.ToString());
            ini.Write("radioStartupPowerWatts", StartupPowerWatts.ToString());
            ini.Write("radioStartupPowerMaxWatts", StartupPowerMaxWatts.ToString());
            ini.Write("radioAudioStepPercent", AudioStepPercent.ToString());
            if (LastBandIdx >= 0) ini.Write("radioLastBandIdx", LastBandIdx.ToString());
            ini.Write("radioRememberTxLevelPerBand", RememberTxLevelPerBand.ToString());
            ini.Write("radioTxLevelByBand", string.Join(";", System.Linq.Enumerable.Select(TxLevelByBand,
                kv => $"{kv.Key}={kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}")));
        }
    }

    public enum RadioSplitMode
    {
        None,
        Rig,
        FakeIt,
    }

    // Matches WSJT-X's own Radio tab "Mode" radio buttons exactly.
    public enum RadioTxMode
    {
        None,
        Usb,
        DataPkt,
    }
}
