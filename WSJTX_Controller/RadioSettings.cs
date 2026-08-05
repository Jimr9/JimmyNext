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

        // Opt-in, default off: use rigctld for PTT instead of WSJT-X's own CAT-driven PTT.
        // A bigger behavioral change than read-only telemetry, so it gets its own separate
        // default-off flag rather than following Mode automatically.
        public bool PttEnabled { get; set; } = false;

        public bool PollEnabled { get; set; } = false;
        public int PollIntervalMs { get; set; } = 1000;

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
            PttEnabled = ini.Read("radioPttEnabled") == "True";
            PollEnabled = ini.Read("radioPollEnabled") == "True";
            if (int.TryParse(ini.Read("radioPollIntervalMs"), out int interval) && interval >= 200)
                PollIntervalMs = interval;
        }

        public void SaveToIni(IniFile ini)
        {
            ini.Write("radioControlMode", Mode.ToString());
            ini.Write("radioUseExternalRigctld", UseExternalRigctld.ToString());
            ini.Write("radioRigctldHost", RigctldHost);
            ini.Write("radioRigctldPort", RigctldPort.ToString());
            ini.Write("radioRigModel", RigModel);
            ini.Write("radioComPort", ComPort);
            ini.Write("radioPttEnabled", PttEnabled.ToString());
            ini.Write("radioPollEnabled", PollEnabled.ToString());
            ini.Write("radioPollIntervalMs", PollIntervalMs.ToString());
        }
    }
}
