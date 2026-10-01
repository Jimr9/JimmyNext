using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSJTX_Controller
{
    // Settings every profile shares (operator request 2026-09-29): station callsign and grid,
    // operator name and contest email, the logins for QRZ, LoTW, Club Log, HRDLog, eQSL and
    // HamQTH, and the TQSL station location. They live in one Shared.ini beside the base settings
    // file. A profile uses them unless it has its own for that group ("This profile only" in
    // Options, profileOnly_<group>=True in the profile's ini, the values then kept in the
    // profile's ini as before). Every read and save goes through IniFile, which routes these keys
    // (IniFile.AttachShared) -- no setting's own load/save code knows about this.
    //
    // Shared.ini's "migratedGroups" lists the groups whose move-over has COMPLETED; only those are
    // routed. A Shared.ini without it (partly written, or from the first 2.0.78 tester build) is
    // not a finished move-over: its values are only a fallback for the next attempt.
    internal static class SharedSettings
    {
        internal const string FileName = "Shared.ini";
        internal const string MigratedKey = "migratedGroups";

        internal static readonly (string Group, string[] Keys)[] Groups =
        {
            ("Station",      new[] { "nativeEngineMyCall", "nativeEngineMyGrid" }),
            ("Operator",     new[] { "stationOperatorName", "stationContestEmail" }),
            ("QrzLookup",    new[] { "qrzUsername", "qrzPassword" }),
            ("QrzLogbook",   new[] { "qrzLogbookApiKey" }),
            ("Lotw",         new[] { "lotwLogbookUser", "lotwLogbookPass" }),
            ("TqslLocation", new[] { "tqslStationLocation" }),
            ("ClubLog",      new[] { "clubLogUploadEmail", "clubLogUploadPassword", "clubLogUploadCallsign" }),
            ("HrdLog",       new[] { "hrdLogUploadCallsign", "hrdLogUploadCode" }),
            ("Eqsl",         new[] { "eqslUsername", "eqslPassword" }),
            ("HamQth",       new[] { "hamQthUsername", "hamQthPassword" }),
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

        // The groups a Shared.ini has finished moving over (empty: none, or not a finished file).
        internal static HashSet<string> MigratedGroups(IniFile shared) =>
            new HashSet<string>((shared?.Read(MigratedKey) ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(g => g.Trim()).Where(g => Groups.Any(x => x.Group == g)));

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
            if (ini == null || !ini.SharesGroup(group) || IsProfileOnly(ini, group) == on) return;
            foreach (string key in KeysOf(group))
                ini.WriteOwn(key, on ? ini.Shared.Read(key) : null);
            ini.WriteOwn(ProfileOnlyKey(group), on ? "True" : null);
        }

        // Moves every group not yet moved into Shared.ini; returns a line for the log, or null when
        // there was nothing to do. Throws (after changing nothing a profile relies on) when a write
        // cannot be verified. Order, so an interruption at any point loses nothing:
        //  1. every settings file is copied to a backup folder;
        //  2. the new Shared.ini is built as a temporary file and read back against its sources --
        //     per group the active profile's values, or the first profile that has any, or (for a
        //     group no profile has) an unfinished Shared.ini's own;
        //  3. a profile whose values differ (not blank, not equal) is marked "this profile only",
        //     each mark read back;
        //  4. the temporary file replaces Shared.ini, now listing the groups in migratedGroups --
        //     from here on they are shared;
        //  5. only then do matching or blank profiles drop their copies (a copy left behind by a
        //     failure there is unused, and reported).
        internal static string MigrateOnce(string appDataDir, string baseIniPath, string profilesDir, string activeIniPath)
        {
            string sharedPath = Path.Combine(appDataDir, FileName);
            var existing = File.Exists(sharedPath) ? new IniFile(sharedPath) : null;
            var done = MigratedGroups(existing);
            var pending = Groups.Where(g => !done.Contains(g.Group)).ToList();
            if (pending.Count == 0) return null;

            var files = new List<string>();
            if (File.Exists(baseIniPath)) files.Add(baseIniPath);
            if (Directory.Exists(profilesDir))
                files.AddRange(Directory.GetFiles(profilesDir, "*.ini")
                    .Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith(".Contests", StringComparison.OrdinalIgnoreCase)));
            var ordered = new List<string>();
            if (files.Any(f => string.Equals(f, activeIniPath, StringComparison.OrdinalIgnoreCase))) ordered.Add(activeIniPath);
            ordered.AddRange(files.Where(f => !string.Equals(f, activeIniPath, StringComparison.OrdinalIgnoreCase)));

            // 1. backup
            string backupDir = Path.Combine(appDataDir, "Profiles-backup-before-shared-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(backupDir);
            foreach (string f in files)
                File.Copy(f, Path.Combine(backupDir, (string.Equals(f, baseIniPath, StringComparison.OrdinalIgnoreCase) ? "" : "Profiles_") + Path.GetFileName(f)), overwrite: true);
            if (existing != null) File.Copy(sharedPath, Path.Combine(backupDir, FileName), overwrite: true);

            // 2. build and verify the new shared file
            string tempPath = sharedPath + ".new";
            if (File.Exists(tempPath)) File.Delete(tempPath);
            if (existing != null) File.Copy(sharedPath, tempPath);
            var temp = new IniFile(tempPath);
            var inis = ordered.ToDictionary(f => f, f => new IniFile(f), StringComparer.OrdinalIgnoreCase);
            var sourceOf = new Dictionary<string, IniFile>();
            foreach (var (group, keys) in pending)
            {
                string src = ordered.FirstOrDefault(f => !IsBlank(inis[f], keys));
                IniFile source = src != null ? inis[src] : (existing != null && !IsBlank(existing, keys) ? existing : null);
                sourceOf[group] = source;
                foreach (string key in keys)
                    if (source != null) temp.Write(key, source.Read(key));
                    else temp.DeleteKey(key);
            }
            var check = new IniFile(tempPath);
            foreach (var (group, keys) in pending)
                foreach (string key in keys)
                    if (Plain(check, key) != (sourceOf[group] == null ? "" : Plain(sourceOf[group], key)))
                        throw new IOException($"shared settings: {key} did not read back from {tempPath}");

            // 3. mark differing profiles
            var redundant = new List<(IniFile Ini, string[] Keys)>();
            int kept = 0;
            foreach (var (group, keys) in pending)
                foreach (string f in ordered)
                {
                    var ini = inis[f];
                    if (IsBlank(ini, keys) || keys.All(k => Plain(ini, k) == Plain(check, k)))
                    {
                        redundant.Add((ini, keys));
                        continue;
                    }
                    ini.Write(ProfileOnlyKey(group), "True");
                    if (new IniFile(f).Read(ProfileOnlyKey(group)) != "True")
                        throw new IOException($"shared settings: could not mark {Path.GetFileName(f)} as having its own {group}");
                    kept++;
                }

            // 4. the move-over is complete for these groups
            var nowDone = new HashSet<string>(done);
            foreach (var g in pending) nowDone.Add(g.Group);
            temp.Write(MigratedKey, string.Join(",", Groups.Select(g => g.Group).Where(nowDone.Contains)));
            if (MigratedGroups(new IniFile(tempPath)).Count != nowDone.Count)
                throw new IOException($"shared settings: {tempPath} did not read back");
            if (File.Exists(sharedPath)) File.Replace(tempPath, sharedPath, null);
            else File.Move(tempPath, sharedPath);

            // 5. drop redundant copies
            int notRemoved = 0;
            foreach (var (ini, keys) in redundant)
                foreach (string key in keys)
                {
                    try { ini.DeleteKey(key); if (ini.Read(key).Length > 0) notRemoved++; }
                    catch { notRemoved++; }
                }

            return $"shared settings: moved {string.Join(", ", pending.Select(g => g.Group))} from '{Path.GetFileNameWithoutExtension(activeIniPath)}'; " +
                   $"{kept} kept as this profile only; backup {backupDir}" +
                   (notRemoved > 0 ? $"; {notRemoved} unused profile copies could not be removed" : "");
        }

        private static string Plain(IniFile ini, string key)
        {
            string raw = ini.Read(key) ?? "";
            return Protected.Contains(key) ? CredentialProtector.Unprotect(raw) : raw;
        }

        private static bool IsBlank(IniFile ini, string[] keys) => keys.All(k => string.IsNullOrWhiteSpace(Plain(ini, k)));
    }
}
