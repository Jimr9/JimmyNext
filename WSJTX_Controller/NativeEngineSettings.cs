namespace WSJTX_Controller
{
    // Phase 4g of the self-sufficiency plan: settings for Jimmy's native engine, modeled on
    // RadioSettings.cs's LoadFromIni/SaveToIni pattern.
    //
    // MyCall/MyGrid exist here, not in JimmySettings, because they're operationally load-bearing
    // for the native engine specifically: the engine host needs them BEFORE it can send its own
    // first Status message -- it IS the process that reports MyCall/MyGrid, so it needs the
    // answer already, not a circular wait for itself to report it.
    public class NativeEngineSettings
    {
        public string MyCall { get; set; } = "";
        public string MyGrid { get; set; } = "";

        // Empty = system default input device (cpal's own default-device pick). Matches the
        // device-name strings tempo_audio::device::available_devices() returns (see
        // EngineHost/examples/list_devices.rs) -- Options > Radio's audio-device picker
        // sources its choices from the same enumeration.
        public string AudioInputDevice { get; set; } = "";

        // Phase 4 TX Stage 4: where TX audio actually plays. Empty = system default output --
        // before this field existed, TX audio ALWAYS went to the system default regardless of
        // what (if anything) the operator expected, since there was nowhere to configure it.
        // Matters because the radio's own audio interface is very often NOT the Windows default
        // output device.
        public string AudioOutputDevice { get; set; } = "";

        // 2026-09-23 redesign: Options > Decode Engine's "Radio input/output master level
        // percent" controls now drive the Windows ENDPOINT (device) master volume for the
        // devices above (AudioEndpointMasterVolume), not the engine's own per-application
        // Volume Mixer session level. Percent 0-100; null = never set. When null, Controller.
        // ApplyRadioMasterAudioLevels reads the device's own CURRENT Windows master level
        // instead of forcing one, then adopts that reading as the new saved baseline -- so a
        // fresh profile never silently changes a level the operator never touched in Jimmy.
        // Reapplied at startup, on an Engine Host restart, and whenever the selected device
        // changes (all three funnel through Controller.ApplyEngineMode).
        public int? InputMasterLevelPercent { get; set; }
        public int? OutputMasterLevelPercent { get; set; }

        // 2026-09-23 redesign: the Decode Engine tab's Input/Output level controls used to drive
        // these two PER-APPLICATION Windows Volume Mixer session levels for jimmy-engine-host.exe
        // directly (ProcessAudioSessionVolume) -- they now drive the device MASTER level above
        // instead. Kept as hidden, ini-only settings (no UI control), always 100 unless
        // hand-edited in the ini, applied once the Engine Host's own audio session becomes
        // available -- see WsjtxClient.Direct.cs's ApplyEngineAppAudioLevelsOnceAvailable.
        public int EngineAudioInputAppLevel { get; set; } = 100;
        public int EngineAudioOutputAppLevel { get; set; } = 100;

        // UDP-to-Direct parity/cleanup pass, 2026-08-12: the "talk over classic WSJT-X UDP
        // instead of Direct" choice (UseDirectEngine) is retired as a production option -- UDP
        // mode never had a working way to tell jimmy-engine-host.exe to actually enable
        // transmit (EnableTx() there only ever set a local flag, unlike DirectSetTxEnabled's
        // explicit SET_TX_ENABLED command), so it was never a real fallback, only a leftover
        // from when Jimmy spoke to an external, real WSJT-X. ApplyEngineMode() now always uses
        // Direct outside of TestModeGuard.IsTestMode (replay tests still force classic UDP,
        // unchanged -- see that method's own comment). See WsjtxClient.Direct.cs.
        // Preservation contract (2.0.64 audit): every field here is read back ONLY when its key
        // exists, so a missing key preserves whatever is already in memory rather than resetting
        // to the "" default -- an upgrade that adds new keys never disturbs existing ones. Just as
        // important on the other side: nothing anywhere writes a *resolved* or *fallback* audio
        // device name back into these fields. AudioInputDevice/AudioOutputDevice are set only
        // here (from the INI) and in OptionsDlg.SaveRadioTab (from the operator's own combo
        // text). A saved device that Windows has since renamed / unplugged stays exactly as the
        // operator left it; the engine falls back to the system default at runtime, and the saved
        // name resolves again once the device reappears. Do not "helpfully" persist the
        // system-default fallback over a stored name.
        public void LoadFromIni(IniFile ini)
        {
            if (ini.KeyExists("nativeEngineMyCall")) MyCall = ini.Read("nativeEngineMyCall");
            if (ini.KeyExists("nativeEngineMyGrid")) MyGrid = ini.Read("nativeEngineMyGrid");
            if (ini.KeyExists("nativeEngineAudioDevice")) AudioInputDevice = ini.Read("nativeEngineAudioDevice");
            if (ini.KeyExists("nativeEngineAudioOutputDevice")) AudioOutputDevice = ini.Read("nativeEngineAudioOutputDevice");
            if (ini.KeyExists("radioInputMasterLevelPercent")
                && int.TryParse(ini.Read("radioInputMasterLevelPercent"), out int inMaster)
                && inMaster >= 0 && inMaster <= 100)
                InputMasterLevelPercent = inMaster;
            if (ini.KeyExists("radioOutputMasterLevelPercent")
                && int.TryParse(ini.Read("radioOutputMasterLevelPercent"), out int outMaster)
                && outMaster >= 0 && outMaster <= 100)
                OutputMasterLevelPercent = outMaster;
            if (int.TryParse(ini.Read("engineAudioInputAppLevel"), out int inApp) && inApp >= 0 && inApp <= 100)
                EngineAudioInputAppLevel = inApp;
            if (int.TryParse(ini.Read("engineAudioOutputAppLevel"), out int outApp) && outApp >= 0 && outApp <= 100)
                EngineAudioOutputAppLevel = outApp;
        }

        public void SaveToIni(IniFile ini)
        {
            ini.Write("nativeEngineMyCall", MyCall);
            ini.Write("nativeEngineMyGrid", MyGrid);
            ini.Write("nativeEngineAudioDevice", AudioInputDevice);
            ini.Write("nativeEngineAudioOutputDevice", AudioOutputDevice);
            if (InputMasterLevelPercent.HasValue)
                ini.Write("radioInputMasterLevelPercent", InputMasterLevelPercent.Value.ToString());
            if (OutputMasterLevelPercent.HasValue)
                ini.Write("radioOutputMasterLevelPercent", OutputMasterLevelPercent.Value.ToString());
            ini.Write("engineAudioInputAppLevel", EngineAudioInputAppLevel.ToString());
            ini.Write("engineAudioOutputAppLevel", EngineAudioOutputAppLevel.ToString());
        }
    }
}
