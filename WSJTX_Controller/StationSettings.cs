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
        // Our POTA park when activating (2026-10-04): MY_SIG=POTA / MY_SIG_INFO on every contact
        // logged while it is set. Blank = not activating. Two parks for a two-fer: "US-1234,US-5678".
        public string MyPark { get; set; } = "";
        // The time zone for displays when Options > General "Display dates and times in my selected
        // time zone" is on (2026-10-05): a Windows time-zone id; blank = the computer's own.
        public string DisplayTimeZone { get; set; } = "";
        // The operator's licence class (2026-10-12), for the engine's transmit lockout: ""
        // (not set, no limits), "technician", "general", "extra" (United States classes, the
        // ones Nexus has rules for) or "open" (outside the United States: no limits applied).
        // Kept with the station callsign: shared by every profile unless the profile has its own
        // callsign and grid (SharedSettings "Station"), and never carried in a support package.
        public string LicenseClass { get; set; } = "";

        internal static readonly string[] LicenseClassValues = { "", "technician", "general", "extra", "open" };
        internal static readonly string[] LicenseClassNames =
        {
            "Not set, no transmit limits",
            "United States, Technician",
            "United States, General",
            "United States, Amateur Extra",
            "Outside the United States, no limits applied",
        };
        internal static string NormalizeLicenseClass(string value)
        {
            string v = (value ?? "").Trim().ToLowerInvariant();
            return System.Array.IndexOf(LicenseClassValues, v) >= 0 ? v : "";
        }
        // The word the engine is given: one of its three classes, or "" for no lockout.
        internal string EngineLicenseClass =>
            LicenseClass == "technician" || LicenseClass == "general" || LicenseClass == "extra" ? LicenseClass : "";
        internal string LicenseClassName
        {
            get { int i = System.Array.IndexOf(LicenseClassValues, LicenseClass ?? ""); return LicenseClassNames[i < 0 ? 0 : i]; }
        }

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
            if (ini.KeyExists("stationMyPark")) MyPark = ini.Read("stationMyPark");
            if (ini.KeyExists("stationDisplayTimeZone")) DisplayTimeZone = ini.Read("stationDisplayTimeZone");
            if (ini.KeyExists("stationLicenseClass")) LicenseClass = NormalizeLicenseClass(ini.Read("stationLicenseClass"));
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
            ini.Write("stationMyPark", MyPark);
            ini.Write("stationDisplayTimeZone", DisplayTimeZone);
            ini.Write("stationLicenseClass", LicenseClass);
        }
    }
}
