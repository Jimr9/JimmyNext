using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Moves this install's Jimmy logbook into Nexus -- automatically at startup (AutoMove), or by
    // "Jimmy Next.exe" --nexus-logbook-migrate with Jimmy Next closed. Keeps every old file
    // (nothing is deleted), writes a report, and shows the result. Nexus keeps the log from then
    // on (Phase 7, 2026-09-29: there is no move back).
    public static class NexusLogbookMigration
    {
        private const int Port = 58293; // a private port: never the operating engine's

        public static void RunInteractive()
        {
            string report;
            bool ok;
            try { (ok, report) = Migrate(); }
            catch (Exception ex) { ok = false; report = "Stopped: " + ex.Message; }
            MessageBox.Show(report, "Jimmy Next - Move Logbook to Nexus",
                MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        // ── Automatic move at startup (operator decision 2026-09-29, from 2.0.78) ──────────────
        // The first start of Jimmy Next moves the existing Jimmy logbook with the same checked
        // Migrate() the command runs (fresh verified backup, every contact compared field by field
        // both ways). A new install with no logbook goes through the same path from an empty one,
        // so its Nexus log and read copy are built by the same tested code. Until the move has
        // happened it is tried at every start (Nexus is the only log there is); meanwhile the
        // engine does not open a Nexus log and a contact logged waits in the durable outbox, which
        // the move carries into the new log. AutoMoveRecord keeps the last attempt's report.
        internal static string AutoMoveRecord => Path.Combine(LookupManager.DataRoot, "nexus-logbook-auto-move.txt");

        // What the automatic move did, for Jimmy to say once its window is up (null: nothing).
        internal static string AutoMoveMessage;

        internal static bool AutoMoveNeeded() => !TestModeGuard.IsTestMode && !NexusLogbook.Moved;

        // Runs the move (callers show progress around it). Returns (moved, message to speak/show).
        internal static (bool ok, string message) AutoMove()
        {
            if (!File.Exists(LogbookDb.JimmyDbPath))
                using (new LogbookDb(LogbookDb.JimmyDbPath)) { }   // new install: an empty logbook to move
            bool ok;
            string report;
            try { (ok, report) = Migrate(); }
            catch (Exception ex) { ok = false; report = "Stopped: " + ex.Message; }
            File.WriteAllText(AutoMoveRecord, $"{DateTime.Now:u} {(ok ? "moved" : "not moved")}\n{report}\n", new UTF8Encoding(false));
            if (!ok)
                return (false, "Your logbook has not been moved to the new format yet; it is unchanged. Contacts you log now are kept " +
                               "safely and added when it moves. Jimmy Next tries again the next time it starts.\n\n" + report);
            var marker = File.ReadAllLines(NexusLogbook.ActiveMarker);
            string contacts = marker.FirstOrDefault(l => l.StartsWith("contacts ", StringComparison.Ordinal))?.Substring(9) ?? "0";
            string differences = marker.FirstOrDefault(l => l.StartsWith("differences ", StringComparison.Ordinal))?.Substring(12) ?? "0";
            string diffText = differences == "0" ? "" : $", {differences} differences listed in the report";
            // A new install has nothing to move: say nothing (2026-10-01 -- "New logbook ready" was
            // heard on a first start where the operator had entered nothing at all).
            return (true, int.TryParse(contacts, out int n) && n > 0
                ? $"Logbook moved to the new format, {n:N0} contacts checked{diffText}."
                : null);
        }

        private static string Stamp => DateTime.Now.ToString("yyyyMMdd-HHmmss");
        internal static string TestEngineExeOverride; // JimmyTests only
        private static string EngineExe => TestEngineExeOverride ??
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "EngineHost", "jimmy-engine-host.exe");
        private static string RealLocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        private static string Preconditions()
        {
            if (Process.GetProcessesByName("jimmy-engine-host").Length > 0)
                return "An engine host (jimmy-engine-host) is still running. Close Jimmy Next (and Nexus if it uses one), wait a few seconds, and try again.";
            if (!File.Exists(EngineExe)) return "The engine host was not found: " + EngineExe;
            return null;
        }

        // Consistent copy of a SQLite database (including anything still in its WAL).
        private static void BackupDatabase(string from, string to)
        {
            using (var src = new SQLiteConnection($"Data Source={from};Read Only=True;"))
            using (var dst = new SQLiteConnection($"Data Source={to};"))
            {
                src.Open();
                dst.Open();
                src.BackupDatabase(dst, "main", "main", -1, null, 0);
            }
        }

        // null when the backup is whole and holds exactly the logbook's contacts.
        private static string CheckBackup(string source, string backup)
        {
            long Count(string db)
            {
                using (var c = new SQLiteConnection($"Data Source={db};Read Only=True;"))
                {
                    c.Open();
                    using (var cmd = c.CreateCommand()) { cmd.CommandText = "SELECT COUNT(*) FROM qso;"; return (long)cmd.ExecuteScalar(); }
                }
            }
            using (var c = new SQLiteConnection($"Data Source={backup};Read Only=True;"))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA integrity_check;";
                    string r = Convert.ToString(cmd.ExecuteScalar());
                    if (r != "ok") return $"the backup failed its integrity check ({r}).";
                }
            }
            long a = Count(source), b = Count(backup);
            return a == b ? null : $"the backup holds {b} contacts but the logbook holds {a}.";
        }

        public static (bool ok, string report) Migrate()
        {
            if (NexusLogbook.Moved) return (false, "The logbook is already kept by Nexus.");
            string pre = Preconditions();
            if (pre != null) return (false, pre);
            string jimmyDb = LogbookDb.JimmyDbPath;
            if (!File.Exists(jimmyDb)) return (false, "No Jimmy Next logbook was found at " + jimmyDb);

            // A CURRENT backup, taken now through SQLite itself (includes anything still in the
            // write-ahead file), then checked: whole, and exactly the same contacts.
            string stamp = Stamp;
            string backup = Path.Combine(Path.GetDirectoryName(jimmyDb), $"logbook.before-nexus-{stamp}.db");
            BackupDatabase(jimmyDb, backup);
            string backupProblem = CheckBackup(jimmyDb, backup);
            if (backupProblem != null)
                return (false, "The move was NOT made: " + backupProblem + " Nothing was changed.");

            // Anything already in the folder (contacts queued before the move, Jimmy's settings for
            // the log) is set aside whole and put back if the move does not happen; the outbox and
            // the settings travel into the new log.
            string folder = NexusLogbook.Folder;
            string setAside = folder + ".old-" + stamp;
            bool hadFolder = Directory.Exists(folder);
            if (hadFolder) Directory.Move(folder, setAside);
            Directory.CreateDirectory(folder);
            if (hadFolder)
                foreach (var name in new[] { "outbox.json", "jimmy-meta.json" })
                    if (File.Exists(Path.Combine(setAside, name))) File.Copy(Path.Combine(setAside, name), Path.Combine(folder, name));
            string NotMade(string why)
            {
                string failed = folder + ".failed-" + stamp;
                Directory.Move(folder, failed);
                if (hadFolder) Directory.Move(setAside, folder);
                return $"The move was NOT made: {why} Your logbook is unchanged.\n\nReport: {failed}\\migration-report.txt";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Jimmy Next logbook -> Nexus, {DateTime.Now:u}");
            sb.AppendLine("Backup of your logbook: " + backup);
            var original = NexusMigration.ReadJimmyRows(backup);
            bool complete;
            int differences;
            try
            {
                var written = NexusMigration.WriteAdif(backup, Path.Combine(folder, "log.adi"));
                string token = Guid.NewGuid().ToString("N");
                using (var engine = LogbookOnlyEngine.Start(EngineExe, folder, RealLocalAppData, Port, token))
                {
                    var client = new NexusLogClient(Port);
                    var rows = client.Rows();
                    if (rows.Error != null) throw new InvalidOperationException("the new logbook could not be read: " + rows.Error);
                    var fwd = NexusMigration.Compare(original, rows.Rows);
                    string trip = Path.Combine(Path.GetTempPath(), $"jimmy-roundtrip-{stamp}.db");
                    NexusMigration.Rebuild(rows.Rows, trip);
                    var back = NexusMigrationDryRun.CompareJimmy(original, NexusMigration.ReadJimmyRows(trip));
                    try { File.Delete(trip); } catch { }
                    // Every contact must be in the new log, read completely. A difference in a
                    // field is reported and does not stop the move (operator decision 2026-09-29:
                    // the backup and the report are kept).
                    complete = fwd.Matched == original.Count && rows.Freshness == "current";
                    differences = fwd.Differences.Count + back.Count;
                    sb.AppendLine();
                    sb.AppendLine(NexusMigration.Report(fwd, written));
                    sb.AppendLine(back.Count == 0 ? "Round-trip check: no differences." : $"Round-trip check: {back.Count} difference(s).");
                    foreach (var d in back.Take(200)) sb.AppendLine("  " + d);
                    if (complete)
                    {
                        // Contacts queued before the move go in now; any not sent stay queued.
                        var outbox = new NexusLogOutbox(Path.Combine(folder, "outbox.json"));
                        if (outbox.Count > 0)
                        {
                            var replay = outbox.Replay(client);
                            sb.AppendLine($"Contacts queued before the move: saved {replay.Saved}, already {replay.Already}, refused as duplicates {replay.Refused}" +
                                          (replay.Stopped ? $", {outbox.Count} still queued ({replay.StopReason})" : ""));
                            if (replay.Saved > 0) rows = client.Rows();
                        }
                        if (rows.Error == null)
                        {
                            Directory.CreateDirectory(NexusLogbook.ProjectionFolder);
                            NexusMigration.Rebuild(rows.Rows, Path.Combine(NexusLogbook.ProjectionFolder, $"p-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{rows.Revision}.db"));
                        }
                    }
                    sb.AppendLine("Engine: " + (client.Shutdown(token) ?? "(no reply)"));
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("Stopped: " + ex.Message);
                File.WriteAllText(Path.Combine(folder, "migration-report.txt"), sb.ToString(), new UTF8Encoding(false));
                return (false, NotMade(ex.Message));
            }
            File.WriteAllText(Path.Combine(folder, "migration-report.txt"), sb.ToString(), new UTF8Encoding(false));

            if (!complete)
                return (false, NotMade("not every contact could be read back from the new log."));
            File.WriteAllText(NexusLogbook.ActiveMarker,
                $"migrated {DateTime.Now:u}\nbackup {backup}\ncontacts {original.Count}\ndifferences {differences}\n");
            string diffNote = differences == 0 ? "checked field by field and back again"
                : $"{differences} difference(s) found, listed in the report";
            return (true, $"Done. Nexus now keeps your logbook ({original.Count:N0} contacts, {diffNote}).\n\n" +
                          $"Backup of your old logbook: {backup}\nReport: {folder}\\migration-report.txt\n\nStart Jimmy Next normally.");
        }
    }
}
