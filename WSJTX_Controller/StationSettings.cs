namespace WSJTX_Controller
{
    // Nexus contesting foundation, phase 1: the operator's persistent station/operator/location
    // profile. Modeled on NativeEngineSettings.cs's LoadFromIni/SaveToIni pattern.
    //
    // Station Callsign and Grid Locator deliberately stay in NativeEngineSettings (as MyCall/
    // MyGrid) rather than moving here -- they are operationally load-bearing for the native
    // engine (baked into jimmy-engine-host's own launch args), while every field below is pure
    // Jimmy-side data with no EngineHost launch-arg dependency today. This is the single
    // authoritative store for all of it: no other class persists these values, and nothing here
    // is mirrored into a second settings object.
    public class StationSettings
    {
        // Defaults to Station Callsign (NativeEngineSettings.MyCall) wherever consumed if left
        // blank -- see WsjtxClient.RequestLog's own fallback. Stored blank here, never defaulted
        // at save time, so a blank value stays a real "not set" rather than a frozen copy of
        // whatever Station Callsign happened to be at last save.
        public string OperatorCallsign { get; set; } = "";
        public string OperatorName { get; set; } = "";
        public string ContestEmail { get; set; } = "";
        public string QthState { get; set; } = "";
        public string County { get; set; } = "";
        public string ArrlSection { get; set; } = "";
        public string CqZone { get; set; } = "";
        public string ItuZone { get; set; } = "";

        public void LoadFromIni(IniFile ini)
        {
            if (ini.KeyExists("stationOperatorCall")) OperatorCallsign = ini.Read("stationOperatorCall");
            if (ini.KeyExists("stationOperatorName")) OperatorName = ini.Read("stationOperatorName");
            if (ini.KeyExists("stationContestEmail")) ContestEmail = ini.Read("stationContestEmail");
            if (ini.KeyExists("stationQthState")) QthState = ini.Read("stationQthState");
            if (ini.KeyExists("stationCounty")) County = ini.Read("stationCounty");
            if (ini.KeyExists("stationArrlSection")) ArrlSection = ini.Read("stationArrlSection");
            if (ini.KeyExists("stationCqZone")) CqZone = ini.Read("stationCqZone");
            if (ini.KeyExists("stationItuZone")) ItuZone = ini.Read("stationItuZone");
        }

        public void SaveToIni(IniFile ini)
        {
            ini.Write("stationOperatorCall", OperatorCallsign);
            ini.Write("stationOperatorName", OperatorName);
            ini.Write("stationContestEmail", ContestEmail);
            ini.Write("stationQthState", QthState);
            ini.Write("stationCounty", County);
            ini.Write("stationArrlSection", ArrlSection);
            ini.Write("stationCqZone", CqZone);
            ini.Write("stationItuZone", ItuZone);
        }
    }
}
