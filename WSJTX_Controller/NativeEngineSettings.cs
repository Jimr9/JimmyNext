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

        // Self-sufficiency plan Phase 6: talk to the engine host directly over its local TCP
        // control port (SNAPSHOT/REPLY/HALT_TX/SET_TX_ENABLED) instead of the standard WSJT-X
        // UDP protocol's heartbeat/negotiation handshake -- root-caused live, 2026-08-08, that
        // handshake (not raw UDP delivery) was the real source of the engine crashes/freezes
        // chased through most of that day, and is specifically timing-sensitive in a way that
        // matters more on a slower machine (a slow CAT/audio open racing the one-shot
        // heartbeat). Now defaults to true, 2026-08-10: this is the intended primary way Jimmy
        // talks to its own bundled engine, not a fallback -- UDP mode only exists because Jimmy
        // originally spoke to an external, real WSJT-X, and has no purpose-built way to tell
        // jimmy-engine-host.exe to actually enable transmit (EnableTx() there only ever sets a
        // local flag, unlike DirectSetTxEnabled's explicit SET_TX_ENABLED command) -- found live
        // this same night chasing a manual-call-never-transmits report that traced back to
        // exactly that gap. See WsjtxClient.Direct.cs.
        public bool UseDirectEngine { get; set; } = true;

        public void LoadFromIni(IniFile ini)
        {
            if (ini.KeyExists("nativeEngineMyCall")) MyCall = ini.Read("nativeEngineMyCall");
            if (ini.KeyExists("nativeEngineMyGrid")) MyGrid = ini.Read("nativeEngineMyGrid");
            if (ini.KeyExists("nativeEngineAudioDevice")) AudioInputDevice = ini.Read("nativeEngineAudioDevice");
            if (ini.KeyExists("nativeEngineAudioOutputDevice")) AudioOutputDevice = ini.Read("nativeEngineAudioOutputDevice");
            if (ini.KeyExists("nativeEngineUseDirectEngine")) UseDirectEngine = ini.Read("nativeEngineUseDirectEngine") == "True";
        }

        public void SaveToIni(IniFile ini)
        {
            ini.Write("nativeEngineMyCall", MyCall);
            ini.Write("nativeEngineMyGrid", MyGrid);
            ini.Write("nativeEngineAudioDevice", AudioInputDevice);
            ini.Write("nativeEngineAudioOutputDevice", AudioOutputDevice);
            ini.Write("nativeEngineUseDirectEngine", UseDirectEngine.ToString());
        }
    }
}
