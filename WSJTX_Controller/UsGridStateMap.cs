using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace WSJTX_Controller
{
    internal static class UsGridStateMap
    {
        internal static readonly Dictionary<string, string> Map;

        static UsGridStateMap()
        {
            Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string path = FindGridDat();
            if (path != null)
                LoadGridDat(path);
        }

        internal static bool TryGetState(string grid, out string state)
        {
            state = null;
            if (grid == null || grid.Length < 4) return false;
            return Map.TryGetValue(grid.Substring(0, 4), out state);
        }

        // Release-audit finding, 2026-08-20: a 4-char grid square straddling a state border is
        // stored here as a compound value like "MN-WI" (see LoadGridDat's own comment) --
        // exactly right for DISPLAY (BuildCallWaitingRow, the Raw Decodes row), but every
        // set-membership MATCHING call site (AwardTagger.IsHrcWasNeeded/IsHrcWasUnconfirmed,
        // AwardMatcher's RuleGroupBy.State branch) used to do a plain exact-string
        // HashSet.Contains(state) -- "MN-WI" never matches a set containing "MN" or "WI"
        // individually, so a station in a genuinely still-needed state, heard on a border grid
        // square with no QRZ-cached single-state answer available, silently never got tagged as
        // needed -- and via AwardMatcher, could even be treated as already-worked/not-needed and
        // hidden from the call queue entirely: a real lost QSO opportunity, not just a missing
        // notification. Shared here so every matching call site gets the fix once, matching
        // ResolveUsState's own "one shared implementation, directly unit-testable" reasoning.
        internal static bool StateSetContains(string state, ICollection<string> set)
        {
            if (string.IsNullOrEmpty(state) || set == null || set.Count == 0) return false;
            if (set.Contains(state)) return true; // exact match -- the common, non-compound case
            if (state.IndexOf('-') < 0) return false;
            foreach (string part in state.Split('-'))
                if (set.Contains(part)) return true;
            return false;
        }

        private static string FindGridDat()
        {
            // 1. Standard Windows install locations
            string[] stdPaths = new[]
            {
                @"C:\Program Files\WSJT-X\share\wsjtx\grid.dat",
                @"C:\Program Files (x86)\WSJT-X\share\wsjtx\grid.dat",
            };
            foreach (string p in stdPaths)
            {
                if (File.Exists(p)) return p;
            }

            // 2. Registry-derived install location
            string regPath = FindGridDatViaRegistry();
            if (regPath != null) return regPath;

            // 3. Exe-adjacent portable location
            try
            {
                string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string portable = Path.Combine(exeDir, "share", "wsjtx", "grid.dat");
                if (File.Exists(portable)) return portable;
            }
            catch { }

            return null;
        }

        private static string FindGridDatViaRegistry()
        {
            string[] regKeys = new[]
            {
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            };

            foreach (string keyPath in regKeys)
            {
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(keyPath))
                    {
                        if (key == null) continue;
                        foreach (string subName in key.GetSubKeyNames())
                        {
                            if (!subName.StartsWith("wsjtx", StringComparison.OrdinalIgnoreCase))
                                continue;
                            using (RegistryKey sub = key.OpenSubKey(subName))
                            {
                                if (sub == null) continue;
                                string uninstall = sub.GetValue("UninstallString") as string;
                                if (string.IsNullOrEmpty(uninstall)) continue;
                                // UninstallString: C:\WSJT\wsjtx\Uninstall.exe
                                // Strip filename to get install root
                                string installDir = Path.GetDirectoryName(uninstall);
                                if (string.IsNullOrEmpty(installDir)) continue;
                                string candidate = Path.Combine(installDir, "share", "wsjtx", "grid.dat");
                                if (File.Exists(candidate)) return candidate;
                            }
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        private static void LoadGridDat(string path)
        {
            try
            {
                string prefix = null;
                foreach (string raw in File.ReadLines(path))
                {
                    string line = raw.TrimEnd();
                    if (line.Length == 0) continue;

                    // Group header: exactly 2 letters followed by <
                    // e.g. "EN<"
                    if (line.Length == 3 && line[2] == '<' &&
                        char.IsLetter(line[0]) && char.IsLetter(line[1]))
                    {
                        prefix = line.Substring(0, 2).ToUpperInvariant();
                        continue;
                    }

                    // Data line: starts with tab
                    // e.g. "\t34:MN-WI," or "\t34:MN-WI>"
                    if (prefix == null || line[0] != '\t') continue;

                    string content = line.TrimStart('\t');
                    int colon = content.IndexOf(':');
                    if (colon < 0) continue;

                    string suffix = content.Substring(0, colon).Trim();
                    string stateRaw = content.Substring(colon + 1);

                    // Remove trailing comma or >
                    if (stateRaw.Length > 0)
                    {
                        char last = stateRaw[stateRaw.Length - 1];
                        if (last == ',' || last == '>')
                            stateRaw = stateRaw.Substring(0, stateRaw.Length - 1);
                    }
                    stateRaw = stateRaw.Trim();

                    if (suffix.Length == 0 || stateRaw.Length == 0) continue;

                    string key = prefix + suffix;
                    if (key.Length == 4)
                        Map[key] = stateRaw;
                }
            }
            catch { }
        }
    }
}
