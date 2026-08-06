namespace WSJTX_Controller
{
    // Phase 4g of the self-sufficiency plan: settings for DecodeEngineMode.JimmyNative, modeled
    // on RadioSettings.cs's LoadFromIni/SaveToIni pattern. Nothing reads these fields unless
    // EngineModeCutover.Mode == JimmyNative (an INI-only, undocumented flag itself -- see
    // BackendMode.cs), so behavior is unchanged for everyone until that's explicitly set.
    //
    // MyCall/MyGrid exist here, not in JimmySettings, because they're operationally load-bearing
    // for the native engine specifically: the engine host needs them BEFORE it can send its own
    // first Status message (unlike WsjtxExternal mode, where Controller.MyCall() reads
    // wsjtxClient.myCall -- populated FROM an inbound Status message an external WSJT-X-family
    // process sends; the native engine IS that process, so it needs the answer already, not a
    // circular wait for itself to report it).
    public class NativeEngineSettings
    {
        public string MyCall { get; set; } = "";
        public string MyGrid { get; set; } = "";

        // Empty = system default input device (cpal's own default-device pick). Matches the
        // device-name strings tempo_audio::device::available_devices() returns (see
        // EngineHost/examples/list_devices.rs) -- Options > Radio's audio-device picker (not
        // yet built) will source its choices from the same enumeration.
        public string AudioInputDevice { get; set; } = "";

        public void LoadFromIni(IniFile ini)
        {
            if (ini.KeyExists("nativeEngineMyCall")) MyCall = ini.Read("nativeEngineMyCall");
            if (ini.KeyExists("nativeEngineMyGrid")) MyGrid = ini.Read("nativeEngineMyGrid");
            if (ini.KeyExists("nativeEngineAudioDevice")) AudioInputDevice = ini.Read("nativeEngineAudioDevice");
        }

        public void SaveToIni(IniFile ini)
        {
            ini.Write("nativeEngineMyCall", MyCall);
            ini.Write("nativeEngineMyGrid", MyGrid);
            ini.Write("nativeEngineAudioDevice", AudioInputDevice);
        }
    }
}
