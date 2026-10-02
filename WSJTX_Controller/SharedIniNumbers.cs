using System;
using System.IO;

namespace WSJTX_Controller
{
    // Numbers the operator sets by hand in Shared.ini (operator, 2026-10-02) -- no Options control:
    // written there with their default at start when missing, so they are there to find and edit,
    // and read when used, so an edit counts without a restart. Test mode always gets the default.
    internal static class SharedIniNumbers
    {
        private static string SharedIni => Controller.SharedIniFilePath();

        internal static int Read(string key, int defaultValue, int min, int max)
        {
            if (TestModeGuard.IsTestMode) return defaultValue;
            try
            {
                string v = File.Exists(SharedIni) ? new IniFile(SharedIni).Read(key) : "";
                return int.TryParse(v, out int n) ? Math.Max(min, Math.Min(max, n)) : defaultValue;
            }
            catch { return defaultValue; }
        }

        internal static void Ensure(string key, int defaultValue)
        {
            if (TestModeGuard.IsTestMode) return;
            try
            {
                if (!File.Exists(SharedIni)) return;
                var ini = new IniFile(SharedIni);
                if (!ini.KeyExists(key)) ini.Write(key, defaultValue.ToString());
            }
            catch { }
        }
    }
}
