using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace WSJTX_Controller
{
    // Support settings (2026-10-07): a helper sets up Jimmy Next for a recipient and sends the
    // whole configuration -- radio, CAT and PTT included -- as one zip the recipient installs.
    // Unlike a customization package (an allowed list of parts), everything in the profile
    // travels EXCEPT what belongs to one computer or one person (Travels): sound devices,
    // passwords and keys, the station and login settings every profile shares, window places,
    // and remembered state. The same zip is the recipient's "my defaults" and "last installed"
    // copies. Installing keeps the recipient's own sound devices, logins and station.
    internal sealed class SupportPackage
    {
        internal const int FormatVersion = 1;
        private const string ManifestEntry = "Jimmy Next support settings.txt";
        private const string SettingsEntry = "settings.ini";
        private const string ContestsEntry = "contests.ini";
        private const string WordingEntry = "Wording.txt";
        private const string SoundsPrefix = "Sounds/";
        private const string SettingsSection = "Settings";
        private const string HotkeysSection = "Hotkeys";
        private const long MaxTotalBytes = 40L * 1024 * 1024;

        // The port settings shown for the recipient to choose before installing (COM numbers
        // differ between computers).
        internal static readonly (string Key, string Name)[] PortSettings =
            { ("radioComPort", "Radio CAT port"), ("radioPttSerialPort", "PTT port") };

        internal string Recipient, Exporter, ExportedUtc, AppVersion;
        internal int Revision = 1;
        internal int Ignored;   // entries in the file that were not taken
        internal readonly Dictionary<string, string> Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> Hotkeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, Dictionary<string, string>> Contests =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        internal string WordingText;
        internal readonly Dictionary<string, byte[]> SoundFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        // ── What travels ────────────────────────────────────────────────────────────────────

        private static readonly HashSet<string> NeverKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "nativeEngineAudioDevice", "nativeEngineAudioOutputDevice",   // sound devices: chosen on each computer
            "activeProfile", "profileSaveFirst", "firstRun", "ResetWindowSize",
        };
        private static readonly Regex RememberedState = new Regex("Last[A-Z]", RegexOptions.Compiled);   // radioLastBandIdx, LogbookLastQrzRefresh

        internal static bool Travels(string key)
        {
            string k = (key ?? "").Trim();
            if (k.Length == 0 || k.Length > 128 || NeverKeys.Contains(k)) return false;
            if (SupportReportBuilder.IsSecretSetting(k)) return false;                       // passwords, keys, codes
            if (SharedSettings.GroupOf(k) != null || k.StartsWith("profileOnly_", StringComparison.OrdinalIgnoreCase))
                return false;                                                                // station, operator, logins
            if (k.StartsWith("window", StringComparison.OrdinalIgnoreCase)) return false;   // window places
            if (k.StartsWith("support", StringComparison.OrdinalIgnoreCase)) return false;  // this feature's own state
            return !RememberedState.IsMatch(k);
        }

        // ── Making one ──────────────────────────────────────────────────────────────────────

        // From a profile ini (flushed by the caller), its wording file and contest file, and every
        // sound it uses or the operator has added (the customization package's own sound handling).
        internal static SupportPackage FromProfile(string iniPath, string wordingPath, string installSoundsFolder,
            string userSoundsFolder, string recipient, string exporter, int revision, string mainSection = null)
        {
            var pkg = new SupportPackage
            {
                Recipient = recipient, Exporter = exporter, Revision = Math.Max(1, revision),
                ExportedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                AppVersion = AppVersionNow(),
            };
            var ini = CustomizationPackage.ReadIni(File.Exists(iniPath) ? File.ReadAllLines(iniPath) : new string[0]);
            if (ini.TryGetValue(mainSection ?? CustomizationPackage.MainSection, out var main))
                foreach (var kv in main)
                {
                    if (!Travels(kv.Key)) continue;
                    pkg.Settings[kv.Key] = kv.Key.StartsWith("soundFile_", StringComparison.OrdinalIgnoreCase)
                        ? CustomizationPackage.PortableSound(kv.Value, installSoundsFolder, userSoundsFolder, pkg.SoundFiles)
                        : kv.Value;
                }
            if (ini.TryGetValue(HotkeysSection, out var hk))
                foreach (var kv in hk)
                    if (CustomizationPackage.IsHotkeyEntry(kv.Key, kv.Value)) pkg.Hotkeys[kv.Key] = kv.Value;
            string contestIni = ContestConfigStore.CompanionPathFor(iniPath);
            if (!string.IsNullOrEmpty(contestIni) && File.Exists(contestIni))
                TakeContests(CustomizationPackage.ReadIni(File.ReadAllLines(contestIni)), pkg.Contests);
            if (Directory.Exists(userSoundsFolder))
                foreach (string f in Directory.GetFiles(userSoundsFolder, "*.wav"))
                    CustomizationPackage.AddSoundFile(f, pkg.SoundFiles);
            if (File.Exists(wordingPath)) pkg.WordingText = File.ReadAllText(wordingPath);
            return pkg;
        }

        internal static string AppVersionNow()
        {
            try { return System.Diagnostics.FileVersionInfo.GetVersionInfo(System.Reflection.Assembly.GetExecutingAssembly().Location).FileVersion; }
            catch { return ""; }
        }

        private static readonly Regex SimpleKey = new Regex(@"\A[A-Za-z0-9_.\-]{1,64}\z", RegexOptions.Compiled);

        private static void TakeContests(Dictionary<string, Dictionary<string, string>> ini, Dictionary<string, Dictionary<string, string>> into)
        {
            foreach (var sec in ini)
            {
                if (!CustomizationPackage.IsContestId(sec.Key)) continue;
                var keep = sec.Value.Where(kv => SimpleKey.IsMatch(kv.Key) && !SupportReportBuilder.IsSecretSetting(kv.Key))
                                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                if (keep.Count > 0) into[sec.Key] = keep;
            }
        }

        internal byte[] ToZip()
        {
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    CustomizationPackage.AddText(zip, ManifestEntry,
                        "Jimmy Next support settings\r\n" +
                        $"format={FormatVersion}\r\n" +
                        $"recipient={Recipient ?? ""}\r\n" +
                        $"exporter={Exporter ?? ""}\r\n" +
                        $"exported_utc={ExportedUtc ?? ""}\r\n" +
                        $"app_version={AppVersion ?? ""}\r\n" +
                        $"revision={Revision}\r\n" +
                        "Holds no passwords, logins, sound devices, station callsign or logbook.\r\n");
                    var sb = new StringBuilder();
                    sb.AppendLine("[" + SettingsSection + "]");
                    foreach (var kv in Settings.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) sb.AppendLine(kv.Key + "=" + kv.Value);
                    sb.AppendLine("[" + HotkeysSection + "]");
                    foreach (var kv in Hotkeys.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) sb.AppendLine(kv.Key + "=" + kv.Value);
                    CustomizationPackage.AddText(zip, SettingsEntry, sb.ToString());
                    if (Contests.Count > 0)
                    {
                        var cb = new StringBuilder();
                        foreach (var c in Contests)
                        {
                            cb.AppendLine("[" + c.Key + "]");
                            foreach (var kv in c.Value) cb.AppendLine(kv.Key + "=" + kv.Value);
                        }
                        CustomizationPackage.AddText(zip, ContestsEntry, cb.ToString());
                    }
                    if (WordingText != null) CustomizationPackage.AddText(zip, WordingEntry, WordingText);
                    foreach (var kv in SoundFiles)
                    {
                        var e = zip.CreateEntry(SoundsPrefix + kv.Key);
                        using (var s = e.Open()) s.Write(kv.Value, 0, kv.Value.Length);
                    }
                }
                return ms.ToArray();
            }
        }

        // Written whole or not at all, so a failed save never leaves half a file behind.
        internal static void SaveBytes(byte[] zip, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, zip);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        // ── Reading one ─────────────────────────────────────────────────────────────────────

        // Reads and checks a package; throws InvalidDataException with a plain reason when it is
        // not one, is unsafe, or is from a newer format. Settings that never travel are dropped
        // here too, so a hand-made file can't set a password, sound device or station.
        internal static SupportPackage Load(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) throw new InvalidDataException("the file is empty");
            if (bytes.LongLength > SupportService.MaxBytes) throw new InvalidDataException("the file is larger than " + SupportService.MaxText);
            ZipArchive zip;
            try { zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read); }
            catch (InvalidDataException) { throw new InvalidDataException("it is not a zip file"); }
            using (zip)
            {
                long total = 0;
                foreach (var e in zip.Entries)
                {
                    string n = e.FullName.Replace('\\', '/');
                    if (n.StartsWith("/") || n.Contains("../") || n.Contains(":") || n.Split('/').Any(p => p == ".."))
                        throw new InvalidDataException("it holds an unsafe file name");
                    total += e.Length;
                    if (total > MaxTotalBytes) throw new InvalidDataException("its contents are too large");
                }

                var manifest = zip.GetEntry(ManifestEntry) ?? throw new InvalidDataException("it is not a Jimmy Next support settings file");
                var info = Manifest(CustomizationPackage.ReadText(manifest));
                if (!info.TryGetValue("format", out string f) || !int.TryParse(f, out int format))
                    throw new InvalidDataException("it is not a Jimmy Next support settings file");
                if (format > FormatVersion) throw new InvalidDataException("it was made by a newer version of Jimmy Next");

                var pkg = new SupportPackage
                {
                    Recipient = SupportService.BaseCallsign(info.GetValueOrDefault("recipient")),
                    Exporter = Clean(info.GetValueOrDefault("exporter"), 32),
                    ExportedUtc = Clean(info.GetValueOrDefault("exported_utc"), 40),
                    AppVersion = Clean(info.GetValueOrDefault("app_version"), 40),
                    Revision = int.TryParse(info.GetValueOrDefault("revision"), out int rev) && rev > 0 ? rev : 1,
                };

                var settings = zip.GetEntry(SettingsEntry) ?? throw new InvalidDataException("it holds no settings");
                var ini = CustomizationPackage.ReadIni(CustomizationPackage.ReadText(settings).Split('\n'));
                if (ini.TryGetValue(SettingsSection, out var main))
                    foreach (var kv in main)
                        if (Travels(kv.Key) && kv.Value.Length <= 65536) pkg.Settings[kv.Key] = kv.Value;
                        else pkg.Ignored++;
                if (ini.TryGetValue(HotkeysSection, out var hk))
                    foreach (var kv in hk)
                        if (CustomizationPackage.IsHotkeyEntry(kv.Key, kv.Value)) pkg.Hotkeys[kv.Key] = kv.Value;
                        else pkg.Ignored++;
                if (pkg.Settings.Count == 0) throw new InvalidDataException("it holds no settings");

                var contests = zip.GetEntry(ContestsEntry);
                if (contests != null) TakeContests(CustomizationPackage.ReadIni(CustomizationPackage.ReadText(contests).Split('\n')), pkg.Contests);
                var wording = zip.GetEntry(WordingEntry);
                if (wording != null) pkg.WordingText = CustomizationPackage.ReadText(wording);

                foreach (var e in zip.Entries)
                {
                    string n = e.FullName.Replace('\\', '/');
                    if (n == ManifestEntry || n == SettingsEntry || n == ContestsEntry || n == WordingEntry || n.EndsWith("/")) continue;
                    string name = n.StartsWith(SoundsPrefix, StringComparison.OrdinalIgnoreCase) && e.Length <= CustomizationPackage.MaxSoundBytes
                        ? CustomizationPackage.SafeFileName(n.Substring(SoundsPrefix.Length)) : null;
                    if (name == null || name != n.Substring(SoundsPrefix.Length)) { pkg.Ignored++; continue; }
                    using (var s = e.Open())
                    using (var ms = new MemoryStream()) { s.CopyTo(ms); pkg.SoundFiles[name] = ms.ToArray(); }
                }
                return pkg;
            }
        }

        private static Dictionary<string, string> Manifest(string text)
        {
            var info = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in text.Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) info[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return info;
        }

        private static string Clean(string s, int max)
        {
            s = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
            return s.Length > max ? s.Substring(0, max) : s;
        }

        // ── Installing one ──────────────────────────────────────────────────────────────────

        // Into a profile ini: every setting that travels is replaced by the package's (one the
        // package leaves out goes back to its default), everything that doesn't travel is left as
        // it is. ports: a port setting's chosen value, or null to keep this computer's own. Sounds
        // land in <dataFolder>\Sounds (a replaced one of the operator's own copied to backupDir
        // first), the wording in wordingPath, the contests in the profile's contest file.
        internal void Apply(IniFile ini, string dataFolder, string installSoundsFolder, string wordingPath,
            string backupDir, IDictionary<string, string> ports, string mainSection = null)
        {
            var existing = CustomizationPackage.ReadIni(File.Exists(ini.FilePath) ? File.ReadAllLines(ini.FilePath) : new string[0]);
            existing.TryGetValue(mainSection ?? CustomizationPackage.MainSection, out var main);
            bool KeepMine(string key) => ports != null && ports.TryGetValue(key, out string v) && v == null;

            string soundsDir = Path.Combine(dataFolder, "Sounds");
            foreach (var kv in SoundFiles)
            {
                if (!CustomizationPackage.SoundNeedsWriting(kv.Key, kv.Value, soundsDir, installSoundsFolder)) continue;
                Directory.CreateDirectory(soundsDir);
                string dest = Path.Combine(soundsDir, kv.Key);
                if (File.Exists(dest) && backupDir != null)
                {
                    Directory.CreateDirectory(Path.Combine(backupDir, "Sounds"));
                    File.Copy(dest, Path.Combine(backupDir, "Sounds", kv.Key), true);
                }
                File.WriteAllBytes(dest, kv.Value);
            }
            if (WordingText != null && wordingPath != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(wordingPath));
                File.WriteAllText(wordingPath, WordingText, new UTF8Encoding(false));
            }

            using (var batch = ini.BeginBatchScope())
            {
                if (main != null)
                    foreach (var key in main.Keys.Where(Travels).Where(k => !KeepMine(k)).ToList()) ini.DeleteKey(key);
                foreach (var kv in Settings)
                    if (!KeepMine(kv.Key)) ini.Write(kv.Key, kv.Value);
                if (ports != null)
                    foreach (var p in ports)
                        if (p.Value != null) ini.Write(p.Key, p.Value);
                ini.DeleteSection(HotkeysSection);
                foreach (var kv in Hotkeys) ini.Write(kv.Key, kv.Value, HotkeysSection);
                if (Contests.Count > 0)
                {
                    var contestIni = new IniFile(ContestConfigStore.CompanionPathFor(ini.FilePath));
                    using (var cbatch = contestIni.BeginBatchScope())
                    {
                        foreach (var c in Contests)
                        {
                            contestIni.DeleteSection(c.Key);
                            foreach (var kv in c.Value) contestIni.Write(kv.Key, kv.Value, c.Key);
                        }
                        cbatch.Commit();
                    }
                }
                batch.Commit();
            }
        }

        // The operator's own sounds the package would replace with different ones.
        internal List<string> SoundsReplaced(string dataFolder, string installSoundsFolder)
        {
            string soundsDir = Path.Combine(dataFolder, "Sounds");
            return SoundFiles.Where(kv => File.Exists(Path.Combine(soundsDir, kv.Key))
                                          && CustomizationPackage.SoundNeedsWriting(kv.Key, kv.Value, soundsDir, installSoundsFolder))
                             .Select(kv => kv.Key).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ── What the operator is told before installing ──────────────────────────────────────

        // One line per fact, then the warnings (an older revision than the one installed, another
        // station's settings, a newer program's file, entries left out).
        internal List<string> Details(string myBaseCall, int? installedRevision, List<string> soundsReplaced)
        {
            var lines = new List<string>();
            lines.Add("For: " + (string.IsNullOrEmpty(Recipient) ? "no callsign given" : Recipient));
            if (!string.IsNullOrEmpty(Exporter)) lines.Add("Made by: " + Exporter);
            if (DateTime.TryParse(ExportedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
                lines.Add("Made: " + when.ToString("MMMM d, yyyy, HH:mm", CultureInfo.InvariantCulture) + " UTC");
            lines.Add("Revision " + Revision + (string.IsNullOrEmpty(AppVersion) ? "" : ", Jimmy Next " + AppVersion));
            if (!string.IsNullOrEmpty(Recipient) && !string.IsNullOrEmpty(myBaseCall) && !string.Equals(Recipient, myBaseCall, StringComparison.OrdinalIgnoreCase))
                lines.Add($"Warning: these are {Recipient}'s settings; your station callsign is {myBaseCall}. Your callsign is not changed.");
            if (installedRevision != null && Revision < installedRevision)
                lines.Add($"Warning: this is revision {Revision}, older than revision {installedRevision} you installed before.");
            if (Version.TryParse(AppVersion, out var made) && Version.TryParse(AppVersionNow(), out var mine) && made > mine)
                lines.Add($"Warning: made by a newer Jimmy Next ({AppVersion}); a setting this version does not know is ignored.");
            if (soundsReplaced != null && soundsReplaced.Count > 0)
                lines.Add($"Replaces {(soundsReplaced.Count == 1 ? "one of your own sounds" : $"{soundsReplaced.Count} of your own sounds")}, which every profile uses: {string.Join(", ", soundsReplaced)}.");
            if (Ignored > 0) lines.Add($"{Ignored} {(Ignored == 1 ? "entry" : "entries")} in the file can't be used and {(Ignored == 1 ? "is" : "are")} left out.");
            return lines;
        }

        // The revision number written in a saved package, or null.
        internal static int? RevisionOf(string path)
        {
            try { return File.Exists(path) ? Load(File.ReadAllBytes(path)).Revision : (int?)null; }
            catch { return null; }
        }
    }
}
