using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace WSJTX_Controller
{
    // Moving Jimmy Next to another computer (operator, 2026-10-02). "Export everything" puts every
    // profile, the shared settings with logins and passwords, the wording files, sounds, contest
    // settings, award rules and the logbook (with its LoTW high-water and import history) into one
    // file; "Import everything" puts them on this computer in place of what is here. Radio and
    // decode-engine settings travel only when asked (they belong to the radio and sound card).
    //
    // Saved passwords are locked by Windows to one user on one computer (CredentialProtector), so
    // in the file they are locked instead with a password the operator chooses, and locked for this
    // computer again on import. The logbook can only be replaced while the engine is not running:
    // an import is prepared in MoveIn, Jimmy Next closes, and the next start puts it in place
    // (ApplyPending, before the engine starts), after backing up what is here.
    internal static class ComputerMove
    {
        internal const string FileFilter = "Jimmy Next move file (*.jnmove)|*.jnmove";
        internal const string DefaultFileName = "Jimmy Next everything.jnmove";
        private const string ManifestEntry = "jimmy-next-move.txt";
        private const int FormatVersion = 1;
        private const string LockPrefix = "lock:";
        private const string StagingFolder = "MoveIn";
        private const string ReadyMarker = "READY";

        // What travels, relative to the data folder. The logbook database is copied with SQLite's own
        // backup (consistent while the engine has it open); the rest as files.
        private static readonly string[] LogFiles = { "ACTIVE", "jimmy-meta.json", "row-ids.json", "outbox.json", "log.adi" };
        private const string LogFolder = "Data/NexusLog";
        private const string LogDb = "log.sqlite3";

        // Radio and decode-engine settings: the radio connection, PTT and levels, the sound devices,
        // the decoder's own settings. Station settings (callsign, grid, TQSL location) do travel.
        internal static bool IsRadioSetting(string key)
        {
            string k = (key ?? "").Trim();
            return k.StartsWith("radio", StringComparison.OrdinalIgnoreCase)
                || k.StartsWith("decode", StringComparison.OrdinalIgnoreCase)
                || k.StartsWith("nativeEngineAudio", StringComparison.OrdinalIgnoreCase)
                || k.StartsWith("engineAudio", StringComparison.OrdinalIgnoreCase)
                || k.Equals("tuneTimeoutSeconds", StringComparison.OrdinalIgnoreCase);
        }

        // ── Export ───────────────────────────────────────────────────────────────────────────

        internal static void Export(string dataRoot, string zipPath, string password, bool includeRadio)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] key = DeriveKey(password, salt);
            if (File.Exists(zipPath)) File.Delete(zipPath);
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                AddText(zip, ManifestEntry, string.Join("\n", new[]
                {
                    "format=" + FormatVersion,
                    "created=" + DateTime.UtcNow.ToString("o"),
                    "radio=" + (includeRadio ? "yes" : "no"),
                    "salt=" + Convert.ToBase64String(salt),
                    "check=" + Seal("jimmy-next", key),
                }));
                foreach (string rel in SettingsFiles(dataRoot))
                    AddText(zip, rel, LockIni(File.ReadAllText(Path.Combine(dataRoot, rel)), key, includeRadio));
                foreach (string rel in PlainFiles(dataRoot))
                    zip.CreateEntryFromFile(Path.Combine(dataRoot, rel), rel.Replace('\\', '/'), CompressionLevel.Optimal);
                string db = Path.Combine(dataRoot, LogFolder, LogDb);
                if (File.Exists(db))
                {
                    string tmp = Path.Combine(Path.GetTempPath(), $"jimmy-move-{Guid.NewGuid():N}.sqlite3");
                    try
                    {
                        using (var src = new System.Data.SQLite.SQLiteConnection($"Data Source={db};Read Only=True"))
                        using (var dst = new System.Data.SQLite.SQLiteConnection($"Data Source={tmp}"))
                        {
                            src.Open(); dst.Open();
                            src.BackupDatabase(dst, "main", "main", -1, null, 0);
                        }
                        System.Data.SQLite.SQLiteConnection.ClearAllPools();
                        zip.CreateEntryFromFile(tmp, LogFolder + "/" + LogDb, CompressionLevel.Optimal);
                    }
                    finally { try { File.Delete(tmp); } catch { } }
                }
            }
        }

        // Settings files: the main and shared settings, every profile (and its contest file), the
        // shared and per-profile wording.
        private static IEnumerable<string> SettingsFiles(string root)
        {
            foreach (string f in Directory.GetFiles(root, "*.ini")) yield return Path.GetFileName(f);
            string profiles = Path.Combine(root, "Profiles");
            if (Directory.Exists(profiles))
                foreach (string f in Directory.GetFiles(profiles, "*.ini")) yield return Path.Combine("Profiles", Path.GetFileName(f));
        }

        private static IEnumerable<string> PlainFiles(string root)
        {
            var list = new List<string>();
            void Add(string rel) { if (File.Exists(Path.Combine(root, rel))) list.Add(rel); }
            void AddDir(string rel)
            {
                string dir = Path.Combine(root, rel);
                if (!Directory.Exists(dir)) return;
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    list.Add(Path.GetRelativePath(root, f));
            }
            Add(Wording.FileName);
            AddDir(Path.Combine("Profiles", "Wording"));
            AddDir("Sounds");
            AddDir(Path.Combine("Data", "RuleDefinitions"));
            foreach (string f in LogFiles) Add(Path.Combine(LogFolder, f));
            return list;
        }

        // Passwords and keys unlocked from this computer and locked with the file's password; radio
        // settings left out unless wanted. Hotkeys and other sections pass through.
        internal static string LockIni(string text, byte[] key, bool includeRadio) =>
            MapIni(text, (section, k, v) =>
            {
                bool main = !section.Equals("Hotkeys", StringComparison.OrdinalIgnoreCase);
                if (main && !includeRadio && IsRadioSetting(k)) return null;
                if (SupportReportBuilder.IsSecretSetting(k) && v.Length > 0)
                    return LockPrefix + Seal(CredentialProtector.Unprotect(v), key);
                return v;
            });

        // ── Import ───────────────────────────────────────────────────────────────────────────

        // Reads and checks a move file: null when the password is right, else why not.
        internal static string Check(string zipPath, string password)
        {
            try
            {
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    var m = ReadManifest(zip);
                    if (m == null) return "This is not a Jimmy Next move file.";
                    if (!int.TryParse(m.GetValueOrDefault("format"), out int f) || f > FormatVersion)
                        return "This file was made by a newer version of Jimmy Next.";
                    byte[] key = DeriveKey(password, Convert.FromBase64String(m["salt"]));
                    return Open(m.GetValueOrDefault("check"), key) == "jimmy-next" ? null : "The password is not right.";
                }
            }
            catch (Exception ex) { return "The file could not be read: " + ex.Message; }
        }

        // Unpacks a checked move file into MoveIn, ready for the next start: passwords locked for
        // this computer, and -- when the file carries no radio settings -- this computer's own radio
        // settings kept in each settings file it already has.
        internal static void Prepare(string dataRoot, string zipPath, string password)
        {
            string staging = Path.Combine(dataRoot, StagingFolder);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var m = ReadManifest(zip);
                byte[] key = DeriveKey(password, Convert.FromBase64String(m["salt"]));
                bool fileHasRadio = m.GetValueOrDefault("radio") == "yes";
                foreach (var e in zip.Entries)
                {
                    if (e.FullName == ManifestEntry || e.FullName.EndsWith("/")) continue;
                    string rel = e.FullName.Replace('/', Path.DirectorySeparatorChar);
                    string dest = Path.GetFullPath(Path.Combine(staging, rel));
                    if (!dest.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        continue;   // a path leaving the folder is never written
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    if (rel.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) && !rel.StartsWith("Data", StringComparison.OrdinalIgnoreCase))
                    {
                        string text;
                        using (var r = new StreamReader(e.Open(), Encoding.UTF8)) text = r.ReadToEnd();
                        string here = Path.Combine(dataRoot, rel);
                        string mine = !fileHasRadio && File.Exists(here) ? File.ReadAllText(here) : null;
                        File.WriteAllText(dest, UnlockIni(text, key, mine), new UTF8Encoding(false));
                    }
                    else e.ExtractToFile(dest, true);
                }
            }
            File.WriteAllText(Path.Combine(staging, ReadyMarker), DateTime.UtcNow.ToString("o"));
        }

        // Locked values back to this computer's protection; this computer's radio settings added
        // back from `mine` (its current file) when the move file left them out.
        internal static string UnlockIni(string text, byte[] key, string mine)
        {
            string result = MapIni(text, (section, k, v) =>
                v.StartsWith(LockPrefix, StringComparison.Ordinal)
                    ? CredentialProtector.Protect(Open(v.Substring(LockPrefix.Length), key) ?? "")
                    : v);
            if (mine == null) return result;
            var radio = new List<string>();
            MapIni(mine, (section, k, v) =>
            {
                if (!section.Equals("Hotkeys", StringComparison.OrdinalIgnoreCase) && IsRadioSetting(k)) radio.Add(k + "=" + v);
                return v;
            });
            if (radio.Count == 0) return result;
            // Into the main (first non-Hotkeys) section, right after its header.
            var lines = result.Replace("\r\n", "\n").Split('\n').ToList();
            int at = lines.FindIndex(l => l.Trim().StartsWith("[") && !l.Trim().Equals("[Hotkeys]", StringComparison.OrdinalIgnoreCase));
            lines.InsertRange(at < 0 ? 0 : at + 1, radio);
            if (at < 0) lines.Insert(0, "[" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Name + "]");
            return string.Join("\r\n", lines);
        }

        internal static bool Pending(string dataRoot) => File.Exists(Path.Combine(dataRoot, StagingFolder, ReadyMarker));

        // At start, before the engine: puts a prepared import in place. Everything it replaces is
        // backed up first (Backups\before-move-in-<time>). Returns a line to tell the operator, or
        // null when nothing was pending.
        internal static string ApplyPending(string dataRoot)
        {
            if (!Pending(dataRoot)) return null;
            string staging = Path.Combine(dataRoot, StagingFolder);
            string db = Path.Combine(dataRoot, LogFolder, LogDb);
            // The engine from the last session can take a moment to let go of the logbook.
            for (int i = 0; i < 20 && Locked(db); i++) System.Threading.Thread.Sleep(500);
            if (Locked(db)) return "The move could not be finished yet: the logbook is still in use. Close Jimmy Next and start it again.";

            string backups = Path.Combine(dataRoot, "Backups");
            string backup = Path.Combine(backups, "before-move-in-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(backup);
            foreach (string rel in SettingsFiles(dataRoot).Concat(PlainFiles(dataRoot))
                     .Concat(new[] { "-wal", "-shm", "" }.Select(s => Path.Combine(LogFolder, LogDb + s))))
            {
                string src = Path.Combine(dataRoot, rel);
                if (!File.Exists(src)) continue;
                string dst = Path.Combine(backup, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(src, dst, true);
            }
            BackupRetention.Prune(backups, "before-move-in-*");

            // Replace: what the file holds is what this computer then has.
            foreach (string rel in SettingsFiles(dataRoot).ToList()) File.Delete(Path.Combine(dataRoot, rel));
            foreach (string dir in new[] { Path.Combine("Profiles", "Wording"), "Sounds", Path.Combine("Data", "RuleDefinitions"), Path.Combine(LogFolder, "projection") })
            {
                string full = Path.Combine(dataRoot, dir);
                if (Directory.Exists(full)) Directory.Delete(full, true);
            }
            foreach (string f in LogFiles.Concat(new[] { LogDb, LogDb + "-wal", LogDb + "-shm" }))
            {
                string full = Path.Combine(dataRoot, LogFolder, f);
                if (File.Exists(full)) File.Delete(full);
            }
            foreach (string f in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(staging, f);
                if (rel == ReadyMarker) continue;
                string dst = Path.Combine(dataRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(f, dst, true);
            }
            Directory.Delete(staging, true);
            return "Jimmy Next now has everything from the other computer. What was here before is in Backups.";
        }

        private static bool Locked(string path)
        {
            if (!File.Exists(path)) return false;
            try { using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false; }
            catch (IOException) { return true; }
        }

        // ── Pieces ───────────────────────────────────────────────────────────────────────────

        private static Dictionary<string, string> ReadManifest(ZipArchive zip)
        {
            var e = zip.GetEntry(ManifestEntry);
            if (e == null) return null;
            using (var r = new StreamReader(e.Open(), Encoding.UTF8))
                return r.ReadToEnd().Split('\n').Select(l => l.Trim()).Where(l => l.Contains('='))
                        .ToDictionary(l => l.Substring(0, l.IndexOf('=')), l => l.Substring(l.IndexOf('=') + 1));
        }

        private static void AddText(ZipArchive zip, string name, string text)
        {
            var e = zip.CreateEntry(name.Replace('\\', '/'), CompressionLevel.Optimal);
            using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(text);
        }

        // Each "key=value" line through `map` (section, key, value) -> new value, or null to drop it.
        private static string MapIni(string text, Func<string, string, string, string> map)
        {
            var sb = new StringBuilder();
            string section = "";
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string t = raw.Trim();
                if (t.StartsWith("[") && t.EndsWith("]")) { section = t.Substring(1, t.Length - 2); sb.Append(raw).Append("\r\n"); continue; }
                int eq = raw.IndexOf('=');
                if (eq <= 0 || t.StartsWith(";")) { sb.Append(raw).Append("\r\n"); continue; }
                string v = map(section, raw.Substring(0, eq).Trim(), raw.Substring(eq + 1));
                if (v != null) sb.Append(raw.Substring(0, eq)).Append('=').Append(v).Append("\r\n");
            }
            return sb.ToString().TrimEnd('\r', '\n') + "\r\n";
        }

        internal static byte[] DeriveKey(string password, byte[] salt) =>
            Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password ?? ""), salt, 200_000, HashAlgorithmName.SHA256, 32);

        // AES-GCM: nonce | tag | cipher, base64.
        internal static string Seal(string plain, byte[] key)
        {
            byte[] p = Encoding.UTF8.GetBytes(plain ?? ""), nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16], c = new byte[p.Length];
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, p, c, tag);
            return Convert.ToBase64String(nonce.Concat(tag).Concat(c).ToArray());
        }

        // null when the password (key) is wrong or the value is damaged.
        internal static string Open(string sealedText, byte[] key)
        {
            try
            {
                byte[] all = Convert.FromBase64String(sealedText ?? "");
                if (all.Length < 28) return null;
                byte[] nonce = all.Take(12).ToArray(), tag = all.Skip(12).Take(16).ToArray(), c = all.Skip(28).ToArray(), p = new byte[c.Length];
                using (var aes = new AesGcm(key, 16)) aes.Decrypt(nonce, c, tag, p);
                return Encoding.UTF8.GetString(p);
            }
            catch { return null; }
        }
    }
}
