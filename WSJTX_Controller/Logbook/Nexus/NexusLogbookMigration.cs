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
    // Logbook migration Phase 6: the operator's two commands, run with Jimmy Next closed:
    //   "Jimmy Next.exe" --nexus-logbook-migrate   Jimmy's logbook -> Nexus (Nexus becomes owner)
    //   "Jimmy Next.exe" --nexus-logbook-rollback  Nexus's CURRENT log -> a fresh Jimmy logbook
    // Both keep every old file (nothing is deleted), write a report, and show the result.
    public static class NexusLogbookMigration
    {
        private const int Port = 58293; // a private port: never the operating engine's

        public static void RunInteractive(bool migrate)
        {
            string report;
            bool ok;
            try { (ok, report) = migrate ? Migrate() : Rollback(); }
            catch (Exception ex) { ok = false; report = "Stopped: " + ex.Message; }
            MessageBox.Show(report, migrate ? "Jimmy Next - Move Logbook to Nexus" : "Jimmy Next - Move Logbook Back",
                MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
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
            if (NexusLogbook.Active) return (false, "The logbook is already kept by Nexus.");
            string pre = Preconditions();
            if (pre != null) return (false, pre);
            string jimmyDb = LogbookDb.JimmyDbPath;
            if (!File.Exists(jimmyDb)) return (false, "No Jimmy Next logbook was found at " + jimmyDb);

            // Nothing may be pending: contacts queued (or duplicates held) by an earlier Nexus
            // session that were never brought back would be left behind by a fresh move.
            string folder = NexusLogbook.Folder;
            string oldOutbox = Path.Combine(folder, "outbox.json");
            if (File.Exists(oldOutbox))
            {
                var old = new NexusLogOutbox(oldOutbox);
                if (old.Count > 0 || old.Refused.Count > 0)
                    return (false, $"The move was NOT made: an earlier Nexus logbook folder still holds {old.Count} queued contact(s) " +
                                   $"and {old.Refused.Count} held duplicate(s) that are not in your Jimmy logbook.\n\n{folder}\n\n" +
                                   "Nothing was changed. Tell Claude before going further.");
            }

            // A CURRENT backup, taken now through SQLite itself (includes anything still in the
            // write-ahead file), then checked: whole, and exactly the same contacts.
            string stamp = Stamp;
            string backup = Path.Combine(Path.GetDirectoryName(jimmyDb), $"logbook.before-nexus-{stamp}.db");
            BackupDatabase(jimmyDb, backup);
            string backupProblem = CheckBackup(jimmyDb, backup);
            if (backupProblem != null)
                return (false, "The move was NOT made: " + backupProblem + " Nothing was changed.");

            if (Directory.Exists(folder)) Directory.Move(folder, folder + ".old-" + stamp);
            Directory.CreateDirectory(folder);
            var sb = new StringBuilder();
            sb.AppendLine($"Jimmy Next logbook -> Nexus, {DateTime.Now:u}");
            sb.AppendLine("Backup of your logbook: " + backup);

            var original = NexusMigration.ReadJimmyRows(backup);
            var written = NexusMigration.WriteAdif(backup, Path.Combine(folder, "log.adi"));
            string token = Guid.NewGuid().ToString("N");
            bool passed;
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
                passed = fwd.Clean && back.Count == 0 && rows.Freshness == "current";
                sb.AppendLine();
                sb.AppendLine(NexusMigration.Report(fwd, written));
                sb.AppendLine(back.Count == 0 ? "Rollback check: no differences." : $"Rollback check: {back.Count} difference(s).");
                foreach (var d in back.Take(200)) sb.AppendLine("  " + d);
                if (passed)
                {
                    Directory.CreateDirectory(NexusLogbook.ProjectionFolder);
                    NexusMigration.Rebuild(rows.Rows, Path.Combine(NexusLogbook.ProjectionFolder, $"p-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{rows.Revision}.db"));
                }
                sb.AppendLine("Engine: " + (client.Shutdown(token) ?? "(no reply)"));
            }
            File.WriteAllText(Path.Combine(folder, "migration-report.txt"), sb.ToString(), new UTF8Encoding(false));

            if (!passed)
            {
                Directory.Move(folder, folder + ".failed-" + stamp);
                return (false, $"The move was NOT made: the check found differences. Your logbook is unchanged.\n\nReport: {folder}.failed-{stamp}\\migration-report.txt");
            }
            File.WriteAllText(NexusLogbook.ActiveMarker, $"migrated {DateTime.Now:u}\nbackup {backup}\ncontacts {original.Count}\n");
            return (true, $"Done. Nexus now keeps your logbook ({original.Count:N0} contacts, checked field by field and back again).\n\n" +
                          $"Backup of your old logbook: {backup}\nReport: {folder}\\migration-report.txt\n\nStart Jimmy Next normally.");
        }

        public static (bool ok, string report) Rollback()
        {
            if (!NexusLogbook.Active) return (false, "The logbook is not kept by Nexus; nothing to move back.");
            string pre = Preconditions();
            if (pre != null) return (false, pre);
            string stamp = Stamp;
            string folder = NexusLogbook.Folder;
            string jimmyDb = LogbookDb.JimmyDbPath;
            string rebuilt = Path.Combine(Path.GetDirectoryName(jimmyDb), $"logbook.from-nexus-{stamp}.db");
            var sb = new StringBuilder();
            sb.AppendLine($"Nexus logbook -> Jimmy Next, {DateTime.Now:u}");
            string token = Guid.NewGuid().ToString("N");
            int count;
            List<NexusLogOutbox.RefusedEntry> refused;
            using (var engine = LogbookOnlyEngine.Start(EngineExe, folder, RealLocalAppData, Port, token))
            {
                var client = new NexusLogClient(Port);
                // Anything still queued goes in first, so the rollback carries it.
                var outbox = new NexusLogOutbox(NexusLogbook.OutboxPath);
                var replay = outbox.Replay(client);
                sb.AppendLine($"Queued contacts sent first: saved {replay.Saved}, already {replay.Already}, refused as duplicates {replay.Refused}" +
                              (replay.Stopped ? $", STOPPED ({replay.StopReason})" : ""));
                if (replay.Stopped || outbox.Count > 0)
                {
                    client.Shutdown(token);
                    return (false, "Moving back was stopped: queued contacts could not be sent to Nexus first. Nothing was changed.");
                }
                refused = outbox.Refused;
                var rows = client.Rows();
                if (rows.Error != null || rows.Freshness != "current")
                {
                    client.Shutdown(token);
                    return (false, "Moving back was stopped: the Nexus logbook could not be read completely. Nothing was changed.");
                }
                NexusMigration.Rebuild(rows.Rows, rebuilt);
                count = rows.Rows.Count;
                sb.AppendLine("Engine: " + (client.Shutdown(token) ?? "(no reply)"));
            }
            // Swap in: the old Jimmy file is kept beside it, never deleted.
            string kept = Path.Combine(Path.GetDirectoryName(jimmyDb), $"logbook.pre-rollback-{stamp}.db");
            foreach (var ext in new[] { "", "-wal", "-shm" })
                if (File.Exists(jimmyDb + ext)) File.Move(jimmyDb + ext, kept + ext);
            File.Copy(rebuilt, jimmyDb);
            File.Delete(NexusLogbook.ActiveMarker);
            File.WriteAllText(Path.Combine(folder, "rollback-report.txt"), sb.ToString(), new UTF8Encoding(false));
            Directory.Move(folder, folder + ".rolled-back-" + stamp);
            string held = refused.Count == 0 ? "" :
                $"\n\n{refused.Count} contact(s) Nexus refused as duplicates are NOT in the log; their details are in {folder}.rolled-back-{stamp}\\outbox.json.";
            return (true, $"Done. Jimmy Next keeps your logbook again ({count:N0} contacts, including every edit, deletion, confirmation and upload made while Nexus kept it).\n\n" +
                          $"The previous Jimmy logbook file was kept as {kept}.\nThe Nexus logbook folder was kept as {folder}.rolled-back-{stamp}.{held}");
        }
    }
}
