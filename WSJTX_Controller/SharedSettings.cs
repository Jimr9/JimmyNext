using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSJTX_Controller
{
    // Settings every profile shares (operator request 2026-09-29): station callsign and grid,
    // and the logins for QRZ, LoTW, Club Log, HRDLog, eQSL and HamQTH. They live in one
    // Shared.ini beside the base settings file. A profile uses them unless it has its own for
    // that group ("This profile only" in Options, profileOnly_<group>=True in the profile's
    // ini, the values then kept in the profile's ini as before). Profile switches, Save Profile
    // As and every save go through IniFile, which routes these keys (IniFile.AttachShared) --
    // no setting's own load/save code knows about this.
    internal static class SharedSettings
    {
        internal const string FileName = "Shared.ini";

        internal static readonly (string Group, string[] Keys)[] Groups =
        {
            ("Station",    new[] { "nativeEngineMyCall", "nativeEngineMyGrid" }),
            ("QrzLookup",  new[] { "qrzUsername", "qrzPassword" }),
            ("QrzLogbook", new[] { "qrzLogbookApiKey" }),
            ("Lotw",       new[] { "lotwLogbookUser", "lotwLogbookPass" }),
            ("ClubLog",    new[] { "clubLogUploadEmail", "clubLogUploadPassword", "clubLogUploadCallsign" }),
            ("HrdLog",     new[] { "hrdLogUploadCallsign", "hrdLogUploadCode" }),
            ("Eqsl",       new[] { "eqslUsername", "eqslPassword" }),
            ("HamQth",     new[] { "hamQthUsername", "hamQthPassword" }),
        };

        // Stored DPAPI-protected (CredentialProtector); compared and shown as plain text.
        private static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "qrzPassword", "qrzLogbookApiKey", "lotwLogbookPass", "clubLogUploadPassword",
            "hrdLogUploadCode", "eqslPassword", "hamQthPassword",
        };

        private static readonly Dictionary<string, string> GroupByKey =
            Groups.SelectMany(g => g.Keys.Select(k => (k, g.Group)))
                  .ToDictionary(p => p.k, p => p.Group, StringComparer.OrdinalIgnoreCase);

        internal static string GroupOf(string key) =>
            key != null && GroupByKey.TryGetValue(key, out string g) ? g : null;

        internal static string[] KeysOf(string group) =>
            Groups.First(g => g.Group == group).Keys;

        internal static string ProfileOnlyKey(string group) => "profileOnly_" + group;

        internal static bool IsProfileOnly(IniFile ini, string group) =>
            ini != null && ini.ReadOwn(ProfileOnlyKey(group)) == "True";

        // The shared value of one key as plain text ("" when there is no shared file).
        internal static string SharedPlain(IniFile ini, string key)
        {
            string raw = ini?.Shared?.Read(key) ?? "";
            return Protected.Contains(key) ? CredentialProtector.Unprotect(raw) : raw;
        }

        // On: the profile starts from the shared values and keeps its own from now on.
        // Off: the profile's own values are dropped and the shared ones apply again.
        internal static void SetProfileOnly(IniFile ini, string group, bool on)
        {
            if (ini?.Shared == null || IsProfileOnly(ini, group) == on) return;
            foreach (string key in KeysOf(group))
                ini.WriteOwn(key, on ? ini.Shared.Read(key) : null);
            ini.WriteOwn(ProfileOnlyKey(group), on ? "True" : null);
        }

        // Once, when Shared.ini doesn't exist yet: the active profile's values (or, for a group it
        // has blank, the first profile with values) become the shared ones. A profile whose
        // values match them, or are blank, drops its copy and uses the shared ones; one with
        // different values keeps them as "this profile only". Nothing is lost: every settings
        // file is copied to a backup folder first. Returns a line for the debug log, or null.
        internal static string MigrateOnce(string appDataDir, string baseIniPath, string profilesDir, string activeIniPath)
        {
            string sharedPath = Path.Combine(appDataDir, FileName);
            if (File.Exists(sharedPath)) return null;

            var files = new List<string>();
            if (File.Exists(baseIniPath)) files.Add(baseIniPath);
            if (Directory.Exists(profilesDir))
                files.AddRange(Directory.GetFiles(profilesDir, "*.ini")
                    .Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith(".Contests", StringComparison.OrdinalIgnoreCase)));

            string backupDir = Path.Combine(appDataDir, "Profiles-backup-before-shared-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(backupDir);
            foreach (string f in files)
                File.Copy(f, Path.Combine(backupDir, (f == baseIniPath ? "" : "Profiles_") + Path.GetFileName(f)), overwrite: true);

            var ordered = new List<string>();
            if (files.Any(f => string.Equals(f, activeIniPath, StringComparison.OrdinalIgnoreCase))) ordered.Add(activeIniPath);
            ordered.AddRange(files.Where(f => !string.Equals(f, activeIniPath, StringComparison.OrdinalIgnoreCase)));

            var shared = new IniFile(sharedPath);
            var inis = ordered.ToDictionary(f => f, f => new IniFile(f), StringComparer.OrdinalIgnoreCase);
            int kept = 0;
            foreach (var (group, keys) in Groups)
            {
                string source = ordered.FirstOrDefault(f => !IsBlank(inis[f], keys));
                if (source == null) continue;
                foreach (string key in keys) shared.Write(key, inis[source].Read(key));
                foreach (string f in ordered)
                {
                    var ini = inis[f];
                    if (IsBlank(ini, keys) || keys.All(k => Plain(ini, k) == Plain(shared, k)))
                        foreach (string key in keys) ini.DeleteKey(key);
                    else
                    {
                        ini.Write(ProfileOnlyKey(group), "True");
                        kept++;
                    }
                }
            }
            if (!File.Exists(sharedPath)) shared.Write("created", DateTime.UtcNow.ToString("u"));
            return $"shared settings created from '{Path.GetFileNameWithoutExtension(activeIniPath)}'; {kept} profile setting group(s) kept as this profile only; backup {backupDir}";
        }

        private static string Plain(IniFile ini, string key)
        {
            string raw = ini.Read(key) ?? "";
            return Protected.Contains(key) ? CredentialProtector.Unprotect(raw) : raw;
        }

        private static bool IsBlank(IniFile ini, string[] keys) => keys.All(k => string.IsNullOrWhiteSpace(Plain(ini, k)));
    }
}
