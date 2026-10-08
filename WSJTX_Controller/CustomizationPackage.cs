using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;

namespace WSJTX_Controller
{
    // What a customization package can carry; the operator picks which, each time.
    [Flags]
    internal enum CustomizationParts
    {
        None = 0,
        Wording = 1,
        Notifications = 2,
        Sounds = 4,
        Hotkeys = 8,
        ListDisplay = 16,
        Operating = 32,
        BandFrequencies = 64,
        ContestCategories = 128,
    }

    // Customization package (operator request, 2026-10-01): one zip file that carries the
    // wording file, notifications, sounds and -- if chosen -- hotkeys and list display to another
    // operator. Only keys on the allowed lists below go out or come in: an allowed list, not a
    // blocked one, so a setting added later (a password, a radio port) can never travel by
    // accident. Radio, audio, station, callsign, logins, windows and the logbook never do.
    // Import re-checks every key against the same lists, so a hand-edited file can't set them
    // either. Entry names inside the zip are never used as paths (sound files keep only their
    // bare file name).
    internal sealed class CustomizationPackage
    {
        internal const int FormatVersion = 1;
        internal const string FileFilter = "Jimmy Next customizations (*.zip)|*.zip";
        internal const string DefaultFileName = "Jimmy Next customizations.zip";
        private const string ManifestEntry = "Jimmy Next customizations.txt";
        private const string SettingsEntry = "settings.ini";
        private const string WordingEntry = "Wording.txt";
        private const string ContestsEntry = "contests.ini";
        private const string SoundsPrefix = "Sounds/";
        private const string SettingsSection = "Settings";
        private const string HotkeysSection = "Hotkeys";
        internal const long MaxSoundBytes = 10L * 1024 * 1024;
        internal const long MaxTextBytes = 2L * 1024 * 1024;

        internal CustomizationParts Parts;
        // The named profile it was exported from (null: (Default), or a file from before 2026-10-07).
        // Only ever offered as a destination after the caller checks it is a usable profile name.
        internal string ProfileName;
        // Entries in the file that were not taken (not allowed, unreadable, or not a sound file).
        internal int Ignored;
        internal readonly Dictionary<string, string> Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> Hotkeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal string WordingText;
        internal readonly Dictionary<string, byte[]> SoundFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        // contest event id -> its entry choices (ContestConfigStore's companion file)
        internal readonly Dictionary<string, Dictionary<string, string>> Contests =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        // A contest's entry choices travel; its "section" (the operator's location, like the
        // station settings) never does.
        private static readonly string[] ContestKeys =
            { "class", "runMode", "categoryOperator", "categoryPower", "categoryAssisted", "categoryStation" };

        internal static bool IsContestId(string id) =>
            !string.IsNullOrEmpty(id) && id.Length <= 40 && id.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-');

        private static void TakeContests(Dictionary<string, Dictionary<string, string>> ini, Dictionary<string, Dictionary<string, string>> into)
        {
            foreach (var sec in ini)
            {
                if (!IsContestId(sec.Key)) continue;
                var keep = sec.Value.Where(kv => ContestKeys.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                if (keep.Count > 0) into[sec.Key] = keep;
            }
        }

        // ---- The allowed lists ----

        private static readonly string[] NotificationKeys =
        {
            "routineStatusSpeakWhen", "routineStatusCondition", "routineStatusDuringQso",
            "announceImportantAlertsWhenFocusElsewhere", "suppressReceiveNotificationsDuringTx",
            "notificationJoinOrder", "notificationHistoryIncludeRoutineStatus",
            "spaceCallsignsAndGrids", "cmdPrompts", "statusBatchDelayMs",
        };
        private static readonly string[] SoundKeys =
        {
            "soundsEnabled", "soundNewOncePerPeriod", "playMyCall", "playLogged", "playCallAdded", "alertRegions",
        };
        private static readonly string[] ListDisplayKeys =
        {
            "rawNewestFirst", "rawMaxRows", "callWaitingRowOrder", "rawDecodeRowOrder", "spotWatchRowOrder",
            "spotWatchSortKey", "listFontSize", "listBackColor", "listForeColor", "listAltRowColor", "showUsState",
            "editLogRowOrder", "smartWindowRowOrder", "advCallLayout", "advShowTx1", "advShowTx2", "advShowRaw", "showSpotWatch",
            "keepTransmitListDuringTx", "keepListPositionDuringRefresh", "moveFocusToStatusOnCallSelect",
        };
        // Calls and operating: who to call and how -- never radio, audio, decoder, station,
        // lookups or logins, and nothing about awards (award rules and which are on travel only
        // through the award rule manager's own Export/Import -- operator, 2026-10-01).
        private static readonly string[] OperatingKeys =
        {
            "wantedCalls", "wantedCallAnywhereEnabled", "spotWatchCalls", "useDirected", "directeds", "directedCqLockedEntry",
            "useAlertDirected", "alertDirecteds", "exceptCalls", "enableReplyDx", "enableReplyLocal", "replyOnlyDxcc",
            "autoReplyNewCq", "replyRR73", "useRR73", "skipGrid", "logEarly", "cqOnly", "cqGrid", "anyMsg", "newOnBand",
            "callCqDx", "callNonDirCq", "ignoreNonDx", "ignoreWeakSnr", "minSnr", "removeOnWeakSnr", "timeout",
            "maxQueuedCalls", "maxCallQueueAgePeriods", "optimizeTx", "skipLevelPrompt", "rankMethod", "rankOrder",
            "rankBeam", "categoryWeights", "callingPriorities", "rawPriorityTags", "otherStationRepliesBeforeYielding",
            "smartQsoStartEnabled", "smartStartSilencePeriods", "smartStartBusyQuietPeriods", "smartStartMaxStandbyRounds", "smartStartTimeLimitMinutes", "smartStartCallsBeforeSwitch", "smartModeStations",
            "txFreqMode", "freqStepHz", "offsetLoLimit", "offsetHiLimit",
            "usePskReporter",
        };

        // Which part a profile setting belongs to; None = it never travels.
        internal static CustomizationParts PartOf(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return CustomizationParts.None;
            bool Starts(string p) => key.StartsWith(p, StringComparison.OrdinalIgnoreCase);
            bool In(string[] list) => list.Contains(key, StringComparer.OrdinalIgnoreCase);
            if (Starts("notify") || In(NotificationKeys)) return CustomizationParts.Notifications;
            if (Starts("soundEnabled_") || Starts("soundFile_") || In(SoundKeys)) return CustomizationParts.Sounds;
            if (Starts("rawShow") || Starts("rawOnly") || Starts("alertFore_") || Starts("alertBack_") || In(ListDisplayKeys))
                return CustomizationParts.ListDisplay;
            if (In(OperatingKeys)) return CustomizationParts.Operating;
            if (key.Equals("freqEntries", StringComparison.OrdinalIgnoreCase)) return CustomizationParts.BandFrequencies;
            return CustomizationParts.None;
        }

        // Two actions on one key in the file's hotkeys (operator, 2026-10-02): one line per key,
        // e.g. "Alt+X: Log QSO, Next call". Empty = none. Unassigned actions never clash.
        internal List<string> HotkeyClashes() =>
            Hotkeys.Where(kv => int.TryParse(kv.Value, out int k) && k != 0)
                   .GroupBy(kv => int.Parse(kv.Value))
                   .Where(g => g.Count() > 1)
                   .Select(g => HotkeyConfig.FormatKeys((System.Windows.Forms.Keys)g.Key) + ": " +
                       string.Join(", ", g.Select(kv => Enum.TryParse(kv.Key, out HotkeyAction a) && HotkeyConfig.DisplayNames.TryGetValue(a, out var n) ? n : kv.Key)))
                   .ToList();

        internal static bool IsHotkeyEntry(string key, string value) =>
            Enum.TryParse(key, false, out HotkeyAction _) && int.TryParse(value, out _);

        // "notifications and sounds" -- the wording file is never named (it travels with the
        // notifications and is not public).
        internal static string Describe(CustomizationParts parts)
        {
            var names = new List<string>();
            if (parts.HasFlag(CustomizationParts.Notifications)) names.Add("notifications");
            if (parts.HasFlag(CustomizationParts.Sounds)) names.Add("sounds");
            if (parts.HasFlag(CustomizationParts.Hotkeys)) names.Add("hotkeys");
            if (parts.HasFlag(CustomizationParts.ListDisplay)) names.Add("list display");
            if (parts.HasFlag(CustomizationParts.Operating)) names.Add("calls and operating");
            if (parts.HasFlag(CustomizationParts.BandFrequencies)) names.Add("band frequencies");
            if (parts.HasFlag(CustomizationParts.ContestCategories)) names.Add("contest categories");
            if (names.Count == 0) return "nothing";
            return names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];
        }

        internal static string MainSection => Assembly.GetExecutingAssembly().GetName().Name;

        // ---- Export ----

        // From the active profile ini (flushed by the caller first) and the wording file. Every
        // sound file goes inside the package (operator, 2026-10-01): each one assigned to an
        // event -- shipped or the operator's own -- and everything in the operator's own sounds
        // folder (callsign and award sounds too); the settings name them by bare file name.
        internal static CustomizationPackage FromProfile(string iniPath, string wordingPath, CustomizationParts parts,
            string installSoundsFolder, string userSoundsFolder, string mainSection = null)
        {
            var pkg = new CustomizationPackage { Parts = parts };
            var ini = ReadIni(File.Exists(iniPath) ? File.ReadAllLines(iniPath) : new string[0]);
            if (ini.TryGetValue(mainSection ?? MainSection, out var main))
            {
                foreach (var kv in main)
                {
                    var part = PartOf(kv.Key);
                    if (part == CustomizationParts.None || !parts.HasFlag(part)) continue;
                    string value = kv.Value;
                    if (part == CustomizationParts.Sounds && kv.Key.StartsWith("soundFile_", StringComparison.OrdinalIgnoreCase))
                        value = PortableSound(value, installSoundsFolder, userSoundsFolder, pkg.SoundFiles);
                    pkg.Settings[kv.Key] = value;
                }
            }
            if (parts.HasFlag(CustomizationParts.Hotkeys) && ini.TryGetValue(HotkeysSection, out var hk))
                foreach (var kv in hk)
                    if (IsHotkeyEntry(kv.Key, kv.Value)) pkg.Hotkeys[kv.Key] = kv.Value;
            if (parts.HasFlag(CustomizationParts.ContestCategories))
            {
                string contestIni = ContestConfigStore.CompanionPathFor(iniPath);
                if (!string.IsNullOrEmpty(contestIni) && File.Exists(contestIni))
                    TakeContests(ReadIni(File.ReadAllLines(contestIni)), pkg.Contests);
            }
            if (parts.HasFlag(CustomizationParts.Sounds) && Directory.Exists(userSoundsFolder))
                foreach (string f in Directory.GetFiles(userSoundsFolder, "*.wav"))
                    AddSoundFile(f, pkg.SoundFiles);
            if (parts.HasFlag(CustomizationParts.Wording))
                pkg.WordingText = File.Exists(wordingPath) ? File.ReadAllText(wordingPath) : "";
            return pkg;
        }

        // A sound setting as it travels: a bare file name, the file itself added to the package --
        // found where Jimmy Next would play it from (a full path, the operator's own sounds
        // folder, then the shipped sounds).
        internal static string PortableSound(string value, string installSoundsFolder, string userSoundsFolder, Dictionary<string, byte[]> files)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string name = Path.GetFileName(value);
            foreach (string candidate in new[] { Path.IsPathRooted(value) ? value : null,
                         Path.Combine(userSoundsFolder ?? "", name), Path.Combine(installSoundsFolder ?? "", name),
                         Path.Combine(Path.GetDirectoryName(installSoundsFolder ?? "") ?? "", name) })
            {
                if (candidate != null && File.Exists(candidate)) { AddSoundFile(candidate, files); break; }
            }
            return name;
        }

        internal static void AddSoundFile(string path, Dictionary<string, byte[]> files)
        {
            try
            {
                string name = Path.GetFileName(path);
                if (!files.ContainsKey(name) && new FileInfo(path).Length <= MaxSoundBytes) files[name] = File.ReadAllBytes(path);
            }
            catch { }
        }

        internal void Save(string zipPath)
        {
            string temp = zipPath + ".tmp";
            if (File.Exists(temp)) File.Delete(temp);
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                AddText(zip, ManifestEntry,
                    "Jimmy Next customizations\r\n" +
                    $"format={FormatVersion}\r\n" +
                    $"parts={Parts}\r\n" +
                    (string.IsNullOrWhiteSpace(ProfileName) ? "" : $"profile={ProfileName.Trim()}\r\n") +
                    $"made by={Assembly.GetExecutingAssembly().GetName().Name} {System.Diagnostics.FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).FileVersion}\r\n" +
                    "Holds no radio, audio, station, login or window settings.\r\n");
                var sb = new StringBuilder();
                sb.AppendLine("[" + SettingsSection + "]");
                foreach (var kv in Settings.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) sb.AppendLine(kv.Key + "=" + kv.Value);
                if (Parts.HasFlag(CustomizationParts.Hotkeys))
                {
                    sb.AppendLine("[" + HotkeysSection + "]");
                    foreach (var kv in Hotkeys.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) sb.AppendLine(kv.Key + "=" + kv.Value);
                }
                AddText(zip, SettingsEntry, sb.ToString());
                if (Parts.HasFlag(CustomizationParts.Wording)) AddText(zip, WordingEntry, WordingText ?? "");
                if (Parts.HasFlag(CustomizationParts.ContestCategories))
                {
                    var cb = new StringBuilder();
                    foreach (var c in Contests)
                    {
                        cb.AppendLine("[" + c.Key + "]");
                        foreach (var kv in c.Value) cb.AppendLine(kv.Key + "=" + kv.Value);
                    }
                    AddText(zip, ContestsEntry, cb.ToString());
                }
                if (Parts.HasFlag(CustomizationParts.Sounds))
                    foreach (var kv in SoundFiles)
                    {
                        var e = zip.CreateEntry(SoundsPrefix + kv.Key);
                        using (var s = e.Open()) s.Write(kv.Value, 0, kv.Value.Length);
                    }
            }
            if (File.Exists(zipPath)) File.Delete(zipPath);
            File.Move(temp, zipPath);
        }

        internal static void AddText(ZipArchive zip, string name, string text)
        {
            var e = zip.CreateEntry(name);
            using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(text);
        }

        // ---- Import ----

        // Reads and checks a package; throws InvalidDataException with a plain reason when it is
        // not one, or is from a newer format. Everything not on the allowed lists is dropped here.
        internal static CustomizationPackage Load(string zipPath)
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var manifest = zip.GetEntry(ManifestEntry);
                if (manifest == null) throw new InvalidDataException("it is not a Jimmy Next customization file");
                var info = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in ReadText(manifest).Split('\n'))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) info[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                if (!info.TryGetValue("format", out string f) || !int.TryParse(f, out int format))
                    throw new InvalidDataException("it is not a Jimmy Next customization file");
                if (format > FormatVersion) throw new InvalidDataException("it was made by a newer version of Jimmy Next");
                info.TryGetValue("parts", out string partsText);
                Enum.TryParse(partsText ?? "", out CustomizationParts parts);

                info.TryGetValue("profile", out string profileName);
                var pkg = new CustomizationPackage { ProfileName = string.IsNullOrWhiteSpace(profileName) ? null : profileName };
                var settings = zip.GetEntry(SettingsEntry);
                if (settings != null)
                {
                    var ini = ReadIni(ReadText(settings).Split('\n'));
                    if (ini.TryGetValue(SettingsSection, out var main))
                        foreach (var kv in main)
                        {
                            var part = PartOf(kv.Key);
                            if (part != CustomizationParts.None && parts.HasFlag(part)) pkg.Settings[kv.Key] = kv.Value;
                            else pkg.Ignored++;
                        }
                    if (parts.HasFlag(CustomizationParts.Hotkeys) && ini.TryGetValue(HotkeysSection, out var hk))
                        foreach (var kv in hk)
                            if (IsHotkeyEntry(kv.Key, kv.Value)) pkg.Hotkeys[kv.Key] = kv.Value;
                            else pkg.Ignored++;
                }
                var contests = zip.GetEntry(ContestsEntry);
                if (parts.HasFlag(CustomizationParts.ContestCategories) && contests != null)
                    TakeContests(ReadIni(ReadText(contests).Split('\n')), pkg.Contests);
                var wording = zip.GetEntry(WordingEntry);
                if (parts.HasFlag(CustomizationParts.Wording) && wording != null) pkg.WordingText = ReadText(wording);
                if (parts.HasFlag(CustomizationParts.Sounds))
                    foreach (var e in zip.Entries)
                    {
                        if (!e.FullName.StartsWith(SoundsPrefix, StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith("/")) continue;
                        string name = e.Length > MaxSoundBytes ? null : SafeFileName(e.FullName.Substring(SoundsPrefix.Length));
                        if (name == null) { pkg.Ignored++; continue; }
                        using (var s = e.Open())
                        using (var ms = new MemoryStream()) { s.CopyTo(ms); pkg.SoundFiles[name] = ms.ToArray(); }
                    }

                // What the file actually holds.
                if (pkg.WordingText != null) pkg.Parts |= CustomizationParts.Wording;
                foreach (var key in pkg.Settings.Keys) pkg.Parts |= PartOf(key);
                if (pkg.Hotkeys.Count > 0) pkg.Parts |= CustomizationParts.Hotkeys;
                if (pkg.Contests.Count > 0) pkg.Parts |= CustomizationParts.ContestCategories;
                if (pkg.SoundFiles.Count > 0) pkg.Parts |= CustomizationParts.Sounds;
                if (pkg.Parts == CustomizationParts.None) throw new InvalidDataException("it holds nothing to import");
                return pkg;
            }
        }

        internal static string ReadText(ZipArchiveEntry e)
        {
            if (e.Length > MaxTextBytes) throw new InvalidDataException($"'{e.Name}' is too large");
            using (var r = new StreamReader(e.Open(), Encoding.UTF8)) return r.ReadToEnd();
        }

        // A bare .wav file name, or null.
        internal static string SafeFileName(string name)
        {
            name = Path.GetFileName((name ?? "").Replace('\\', '/').Split('/').Last());
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            return name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? name : null;
        }

        // Copies the profile ini, the wording file and the contest file aside; returns the backup
        // folder ("before-import-<time>", or "before-undo-<time>" for Undo an import). A short
        // note (BackupInfoFile) says what it was for and where each file came from -- Undo an
        // import reads it. Only the newest of each kind are kept (BackupRetention); prune: false
        // leaves that to the caller (Undo an import prunes after it has read the backup it uses).
        internal static string Backup(string iniPath, string wordingPath, string backupsRoot,
            string kind = "import", string parts = null, string profile = null, bool prune = true)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string dir = Path.Combine(backupsRoot, $"before-{kind}-{stamp}");
            for (int n = 2; Directory.Exists(dir); n++) dir = Path.Combine(backupsRoot, $"before-{kind}-{stamp}-{n}");
            Directory.CreateDirectory(dir);
            if (File.Exists(iniPath)) File.Copy(iniPath, Path.Combine(dir, Path.GetFileName(iniPath)), true);
            if (File.Exists(wordingPath)) File.Copy(wordingPath, Path.Combine(dir, Path.GetFileName(wordingPath)), true);
            string contestIni = ContestConfigStore.CompanionPathFor(iniPath);
            if (!string.IsNullOrEmpty(contestIni) && File.Exists(contestIni)) File.Copy(contestIni, Path.Combine(dir, Path.GetFileName(contestIni)), true);
            File.WriteAllLines(Path.Combine(dir, BackupInfoFile), new[]
            {
                "kind=" + kind,
                "when=" + DateTime.Now.ToString("o"),
                "parts=" + (parts ?? ""),
                "profile=" + (profile ?? ""),
                "ini=" + (iniPath ?? ""),
                "wording=" + (wordingPath ?? ""),
                "wordingExisted=" + File.Exists(wordingPath),   // False: an import made it, an undo removes it
                "contest=" + (contestIni ?? ""),
            }, new UTF8Encoding(false));
            if (prune) BackupRetention.Prune(backupsRoot, $"before-{kind}-*");
            return dir;
        }

        internal const string BackupInfoFile = "backup-info.txt";

        // What one import (or undo) backup holds and where each file goes back to.
        internal sealed class BackupInfo
        {
            public string Dir, Kind, Parts, Profile, IniPath, WordingPath, ContestPath;
            public DateTime When;
            // A profile's own wording file that did not exist yet (2026-10-07): the import made it,
            // so putting the backup back removes it and the profile uses the shared wording again.
            public bool WordingCreated;
        }

        // Reads a backup's note. A backup from before the note existed (2026-10-02) is worked out
        // from its files: the profile ini by its name, the wording file (Wording.txt = shared,
        // <profile>.txt = the profile's own), the contest file beside the ini.
        internal static BackupInfo ReadBackupInfo(string dir, string baseIniPath, string profilesDir, string sharedWordingPath)
        {
            string name = Path.GetFileName(dir);
            var info = new BackupInfo { Dir = dir, Kind = name.StartsWith("before-undo-", StringComparison.OrdinalIgnoreCase) ? "undo" : "import" };
            string note = Path.Combine(dir, BackupInfoFile);
            if (File.Exists(note))
            {
                foreach (string line in File.ReadAllLines(note))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                    switch (k)
                    {
                        case "kind": info.Kind = v; break;
                        case "when": DateTime.TryParse(v, null, System.Globalization.DateTimeStyles.RoundtripKind, out info.When); break;
                        case "parts": info.Parts = v; break;
                        case "profile": info.Profile = v; break;
                        case "ini": info.IniPath = v; break;
                        case "wording": info.WordingPath = v; break;
                        case "contest": info.ContestPath = v; break;
                        case "wordingExisted": info.WordingCreated = v == "False"; break;
                    }
                }
                // Only ever a profile's own wording file -- the shared one is never removed.
                if (string.IsNullOrEmpty(info.WordingPath)
                    || string.Equals(info.WordingPath, sharedWordingPath, StringComparison.OrdinalIgnoreCase))
                    info.WordingCreated = false;
            }
            else
            {
                string baseName = Path.GetFileName(baseIniPath);
                string ini = Directory.GetFiles(dir, "*.ini").Select(Path.GetFileName)
                    .FirstOrDefault(f => !f.EndsWith(".Contests.ini", StringComparison.OrdinalIgnoreCase));
                if (ini != null)
                {
                    bool isBase = string.Equals(ini, baseName, StringComparison.OrdinalIgnoreCase);
                    info.IniPath = isBase ? baseIniPath : Path.Combine(profilesDir, ini);
                    info.Profile = isBase ? "" : Path.GetFileNameWithoutExtension(ini);
                    string contest = ContestConfigStore.CompanionPathFor(info.IniPath);
                    if (contest != null && File.Exists(Path.Combine(dir, Path.GetFileName(contest)))) info.ContestPath = contest;
                    if (File.Exists(Path.Combine(dir, Path.GetFileName(sharedWordingPath)))) info.WordingPath = sharedWordingPath;
                    else if (!isBase && File.Exists(Path.Combine(dir, info.Profile + ".txt")))
                        info.WordingPath = Path.Combine(profilesDir, "Wording", info.Profile + ".txt");
                }
            }
            if (info.When == default)
            {
                var m = System.Text.RegularExpressions.Regex.Match(name, @"\d{8}-\d{6}");
                if (!(m.Success && DateTime.TryParseExact(m.Value, "yyyyMMdd-HHmmss", null, System.Globalization.DateTimeStyles.None, out info.When)))
                    info.When = Directory.GetCreationTime(dir);
            }
            return info;
        }

        // Puts a backup back: each file to where it came from, and the sound files an import
        // replaced back into the sounds folder. Every file it overwrites is first copied to
        // saveCurrentTo (the "before-undo" backup), so the undo can itself be undone.
        internal static void RestoreBackup(BackupInfo info, string soundsDir, string saveCurrentTo)
        {
            void Put(string target)
            {
                if (string.IsNullOrEmpty(target)) return;
                string src = Path.Combine(info.Dir, Path.GetFileName(target));
                if (!File.Exists(src)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(src, target, true);
            }
            Put(info.IniPath);
            Put(info.WordingPath);
            Put(info.ContestPath);
            if (info.WordingCreated && File.Exists(info.WordingPath))
            {
                if (saveCurrentTo != null && !File.Exists(Path.Combine(saveCurrentTo, Path.GetFileName(info.WordingPath))))
                    File.Copy(info.WordingPath, Path.Combine(saveCurrentTo, Path.GetFileName(info.WordingPath)));
                File.Delete(info.WordingPath);
            }
            string backupSounds = Path.Combine(info.Dir, "Sounds");
            if (Directory.Exists(backupSounds))
            {
                Directory.CreateDirectory(soundsDir);
                foreach (string f in Directory.GetFiles(backupSounds))
                {
                    string dest = Path.Combine(soundsDir, Path.GetFileName(f));
                    if (File.Exists(dest) && saveCurrentTo != null)
                    {
                        Directory.CreateDirectory(Path.Combine(saveCurrentTo, "Sounds"));
                        File.Copy(dest, Path.Combine(saveCurrentTo, "Sounds", Path.GetFileName(f)), true);
                    }
                    File.Copy(f, dest, true);
                }
            }
        }

        // Applies the chosen parts to the active profile: each chosen settings part replaces that
        // part as a whole (a setting the sender left at its default goes back to the default
        // here), never touching any other setting. Sound files land in <dataFolder>\Sounds -- except
        // one identical to a shipped sound, already here -- and a file of the operator's own that
        // one replaces is copied to backupDir first.
        internal void ApplyTo(IniFile ini, CustomizationParts chosen, string dataFolder, string installSoundsFolder = null,
            string backupDir = null, string mainSection = null, string wordingPath = null)
        {
            chosen &= Parts;
            var existing = ReadIni(File.Exists(ini.FilePath) ? File.ReadAllLines(ini.FilePath) : new string[0]);
            existing.TryGetValue(mainSection ?? MainSection, out var main);

            string soundsDir = Path.Combine(dataFolder, "Sounds");
            if (chosen.HasFlag(CustomizationParts.Sounds) && SoundFiles.Count > 0)
            {
                Directory.CreateDirectory(soundsDir);
                foreach (var kv in SoundFiles)
                {
                    if (!SoundNeedsWriting(kv.Key, kv.Value, soundsDir, installSoundsFolder)) continue;
                    string dest = Path.Combine(soundsDir, kv.Key);
                    if (File.Exists(dest) && backupDir != null)
                    {
                        Directory.CreateDirectory(Path.Combine(backupDir, "Sounds"));
                        File.Copy(dest, Path.Combine(backupDir, "Sounds", kv.Key), true);
                    }
                    File.WriteAllBytes(dest, kv.Value);
                }
            }
            if (chosen.HasFlag(CustomizationParts.Wording) && WordingText != null)
                File.WriteAllText(wordingPath ?? Path.Combine(dataFolder, Wording.FileName), WordingText, new UTF8Encoding(false));

            using (var batch = ini.BeginBatchScope())
            {
                foreach (var part in new[] { CustomizationParts.Notifications, CustomizationParts.Sounds, CustomizationParts.ListDisplay, CustomizationParts.Operating, CustomizationParts.BandFrequencies })
                {
                    // A part the file holds no settings for (sound files only) leaves mine as they are.
                    if (!chosen.HasFlag(part) || !Settings.Keys.Any(k => PartOf(k) == part)) continue;
                    if (main != null)
                        foreach (var key in main.Keys.Where(k => PartOf(k) == part).ToList()) ini.DeleteKey(key);
                    foreach (var kv in Settings.Where(s => PartOf(s.Key) == part))
                    {
                        ini.Write(kv.Key, kv.Value);   // sounds by bare name: the sounds folder first, then the shipped sounds
                    }
                }
                if (chosen.HasFlag(CustomizationParts.ContestCategories) && Contests.Count > 0)
                {
                    // Only the contests in the package; the operator's other contests, and every
                    // contest's section, are left as they are.
                    var contestIni = new IniFile(ContestConfigStore.CompanionPathFor(ini.FilePath));
                    using (var cbatch = contestIni.BeginBatchScope())
                    {
                        foreach (var c in Contests)
                            foreach (var kv in c.Value) contestIni.Write(kv.Key, kv.Value, c.Key);
                        cbatch.Commit();
                    }
                }
                if (chosen.HasFlag(CustomizationParts.Hotkeys))
                {
                    ini.DeleteSection(HotkeysSection);
                    foreach (var kv in Hotkeys) ini.Write(kv.Key, kv.Value, HotkeysSection);
                }
                batch.Commit();
            }
        }

        // A sound file is written unless it is a shipped sound already here or the same as the one
        // in the sounds folder.
        internal static bool SoundNeedsWriting(string name, byte[] bytes, string soundsDir, string installSoundsFolder)
        {
            string shipped = installSoundsFolder == null ? null : Path.Combine(installSoundsFolder, name);
            if (shipped != null && File.Exists(shipped) && File.ReadAllBytes(shipped).SequenceEqual(bytes)) return false;
            string dest = Path.Combine(soundsDir, name);
            return !File.Exists(dest) || !File.ReadAllBytes(dest).SequenceEqual(bytes);
        }

        // The operator's own sound files (every profile shares the sounds folder) that importing
        // the sounds would replace with different ones -- named before anything is applied.
        internal List<string> SoundsReplaced(string dataFolder, string installSoundsFolder)
        {
            string soundsDir = Path.Combine(dataFolder, "Sounds");
            return SoundFiles.Where(kv => File.Exists(Path.Combine(soundsDir, kv.Key))
                                          && SoundNeedsWriting(kv.Key, kv.Value, soundsDir, installSoundsFolder))
                             .Select(kv => kv.Key).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // section -> key -> value, the way Windows reads an ini: '[' starts a section, ';' a
        // comment, the first '=' splits key and value, both trimmed.
        internal static Dictionary<string, Dictionary<string, string>> ReadIni(IEnumerable<string> lines)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> current = null;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    string name = line.Substring(1, line.Length - 2).Trim();
                    if (!result.TryGetValue(name, out current))
                        result[name] = current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0 || current == null) continue;
                current[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return result;
        }
    }
}
