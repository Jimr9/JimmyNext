using System;
using System.IO;
using System.Linq;
using System.Text;

namespace WSJTX_Controller
{
    // Proof of the logbook move (NexusLogbookMigration) -- run from JimmyTests only, where test
    // mode puts Jimmy's data folder in an isolated temp folder (TestModeGuard /
    // LookupManager.DataRoot), so the REAL move runs on a copy:
    //   a failed move changes nothing -> the move (carrying a queued contact) -> a session on
    //   Nexus (a live contact, an edit) -> a new install.
    public static class NexusMigrationCommandTests
    {
        private static string Hash(string file)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return BitConverter.ToString(sha.ComputeHash(f));
        }

        public static (bool passed, string report) Run(string engineExe, string jimmyDbCopy, string workRoot, int port)
        {
            var sb = new StringBuilder();
            bool all = true;
            void Check(string name, bool ok, string detail = "")
            {
                sb.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail == "" ? "" : "  -> " + detail)}");
                all &= ok;
            }
            if (!TestModeGuard.IsTestMode) return (false, "refused: not in test mode (the real data folder would be used)");

            string jimmyDb = LogbookDb.JimmyDbPath; // isolated test data folder
            Directory.CreateDirectory(Path.GetDirectoryName(jimmyDb));
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(jimmyDb))) File.Delete(f);
            File.Copy(jimmyDbCopy, jimmyDb);
            var original = NexusMigration.ReadJimmyRows(jimmyDb);

            string folder = Path.Combine(workRoot, "NexusLog");
            NexusLogbook.Reset();
            NexusLogbook.TestFolderOverride = folder;
            NexusLogbookMigration.TestEngineExeOverride = engineExe;
            try
            {
                // A contact logged before the move waits in the outbox (the engine keeps no Nexus log
                // until then). A move that fails puts everything back as it was.
                Directory.CreateDirectory(folder);
                var queued = new NexusLogOutbox(Path.Combine(folder, "outbox.json"));
                queued.Add("queued-before-move", new NexusQso { Call = "ZZ5ZZZ", Band = "20m", Mode = "FT8", WhenUnix = 1_790_000_000, TimeKnown = true });
                NexusLogbookMigration.TestEngineExeOverride = Path.Combine(Environment.SystemDirectory, "where.exe");   // exits at once
                var (okFail, repFail) = NexusLogbookMigration.Migrate();
                sb.AppendLine("  failed move: " + repFail.Replace("\n", " | "));
                Check("a failed move changes nothing and keeps the queued contact",
                    !okFail && !NexusLogbook.Moved && new NexusLogOutbox(Path.Combine(folder, "outbox.json")).Count == 1 &&
                    Directory.GetDirectories(workRoot, "NexusLog.failed-*").Length == 1);
                NexusLogbookMigration.TestEngineExeOverride = engineExe;

                string hashBefore = Hash(jimmyDb);
                var (ok1, rep1) = NexusLogbookMigration.Migrate();
                sb.AppendLine("  migrate: " + rep1.Replace("\n", " | "));
                Check("migrate succeeded and switched", ok1 && NexusLogbook.Moved);
                Check("a backup of the Jimmy logbook was made",
                    Directory.GetFiles(Path.GetDirectoryName(jimmyDb), "logbook.before-nexus-*.db").Length >= 1);
                Check("the Jimmy logbook file itself was not changed", Hash(jimmyDb) == hashBefore);
                var moved = NexusMigration.ReadJimmyRows(Directory.GetFiles(NexusLogbook.ProjectionFolder, "p-*.db").Single());
                Check("the read copy holds every contact plus the one queued before the move",
                    moved.Count == original.Count + 1 && moved.Any(x => x.C("callsign") == "ZZ5ZZZ") &&
                    new NexusLogOutbox(NexusLogbook.OutboxPath).Count == 0, $"{moved.Count} rows");
                var (okAgain, _) = NexusLogbookMigration.Migrate();
                Check("a second move is refused", !okAgain);

                // A session while Nexus keeps the log.
                NexusLogbook.TestForceActive = true;
                NexusLogbook.TestPortOverride = port;
                string token = Guid.NewGuid().ToString("N");
                using (var engine = LogbookOnlyEngine.Start(engineExe, folder, Path.Combine(workRoot, "appdata"), port, token))
                {
                    var svc = LogbookFactory.Open();
                    string live = "<CALL:6>ZZ9ZZZ <BAND:3>20m <FREQ:9>14.075500 <MODE:3>FT8 <QSO_DATE:8>20260928 <TIME_ON:6>120000 <EOR>";
                    AdifImporter.Import(svc, AdifParser.ParseWithOrder(live), "WSJTX");
                    NexusLogbook.WaitSent(NexusLogbookService.RequestIdFor("WSJTX",
                        AdifImporter.BuildDedupKey("ZZ9ZZZ", "20m", "FT8", "20260928", "120000")), 30_000);
                    NexusLogbook.Refresh(force: true);
                    var r = original[10];
                    svc.UpdateQso((int)r.Id, r.C("callsign"), r.C("band"), r.C("mode"), r.C("qso_date"), r.C("time_on"), r.C("time_off"),
                        r.C("state"), r.C("country"), r.C("grid"), "CHANGED IN NEXUS", r.C("rst_sent"), r.C("rst_rcvd"), r.C("comment"));
                    NexusLogbook.Refresh(force: true);
                    var now = NexusMigration.ReadJimmyRows(NexusLogbook.ProjectionPath);
                    Check("the session's contact and edit are in the log",
                        now.Any(x => x.C("callsign") == "ZZ9ZZZ") && now.First(x => x.Id == r.Id).C("name") == "CHANGED IN NEXUS");
                    new NexusLogClient(port).Shutdown(token);
                }
                NexusLogbook.Reset();
                NexusLogbook.TestForceActive = null;
                NexusLogbook.TestPortOverride = null;
                Check("Jimmy's own logbook file is untouched while Nexus keeps the log", Hash(jimmyDb) == hashBefore);

                // A new install (no logbook at all) starts on an empty Nexus log.
                foreach (var f in Directory.GetFiles(Path.GetDirectoryName(jimmyDb), "logbook*")) File.Delete(f);
                if (File.Exists(NexusLogbookMigration.AutoMoveRecord)) File.Delete(NexusLogbookMigration.AutoMoveRecord);
                NexusLogbook.TestFolderOverride = Path.Combine(workRoot, "NexusLogNew");
                var (ok3, msg3) = NexusLogbookMigration.AutoMove();
                sb.AppendLine("  automatic move, new install: " + msg3.Replace("\n", " | "));
                Check("new install: an empty Nexus logbook with its read copy",
                    ok3 && msg3 == "New logbook ready." && NexusLogbook.Moved &&
                    Directory.GetFiles(NexusLogbook.ProjectionFolder, "p-*.db").Length == 1);
            }
            finally
            {
                try { File.Delete(NexusLogbookMigration.AutoMoveRecord); } catch { }
                // The isolated test data folder is SHARED with every other JimmyTests test and the
                // replay harness: leave its Logbook folder empty, never a copy of a real log (other
                // tests read it and would see those contacts as worked before).
                foreach (var f in Directory.GetFiles(Path.GetDirectoryName(jimmyDb), "logbook*"))
                    try { File.Delete(f); } catch { }
                NexusLogbook.Reset();
                NexusLogbook.TestForceActive = null;
                NexusLogbook.TestFolderOverride = null;
                NexusLogbook.TestPortOverride = null;
                NexusLogbookMigration.TestEngineExeOverride = null;
            }
            sb.AppendLine(all ? "ALL PASS" : "SOME FAILED");
            return (all, sb.ToString());
        }
    }
}
