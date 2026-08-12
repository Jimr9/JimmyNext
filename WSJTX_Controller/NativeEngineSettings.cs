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

        // UDP-to-Direct parity/cleanup pass, 2026-08-12: the "talk over classic WSJT-X UDP
        // instead of Direct" choice (UseDirectEngine) is retired as a production option -- UDP
        // mode never had a working way to tell jimmy-engine-host.exe to actually enable
        // transmit (EnableTx() there only ever set a local flag, unlike DirectSetTxEnabled's
        // explicit SET_TX_ENABLED command), so it was never a real fallback, only a leftover
        // from when Jimmy spoke to an external, real WSJT-X. ApplyEngineMode() now always uses
        // Direct outside of TestModeGuard.IsTestMode (replay tests still force classic UDP,
        // unchanged -- see that method's own comment). See WsjtxClient.Direct.cs.
        public void LoadFromIni(IniFile ini)
        {
            if (ini.KeyExists("nativeEngineMyCall")) MyCall = ini.Read("nativeEngineMyCall");
            if (ini.KeyExists("nativeEngineMyGrid")) MyGrid = ini.Read("nativeEngineMyGrid");
            if (ini.KeyExists("nativeEngineAudioDevice")) AudioInputDevice = ini.Read("nativeEngineAudioDevice");
            if (ini.KeyExists("nativeEngineAudioOutputDevice")) AudioOutputDevice = ini.Read("nativeEngineAudioOutputDevice");
        }

        public void SaveToIni(IniFile ini)
        {
            ini.Write("nativeEngineMyCall", MyCall);
            ini.Write("nativeEngineMyGrid", MyGrid);
            ini.Write("nativeEngineAudioDevice", AudioInputDevice);
            ini.Write("nativeEngineAudioOutputDevice", AudioOutputDevice);
        }
    }
}
