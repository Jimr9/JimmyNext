using System;
using System.IO;
using System.Linq;
using System.Text;

namespace WSJTX_Controller
{
    // Logbook migration Phase 6 proof of the operator's two commands (NexusLogbookMigration) --
    // run from JimmyTests only, where test mode puts Jimmy's data folder in an isolated temp folder
    // (TestModeGuard / LookupManager.DataRoot), so the REAL commands run on a copy:
    //   migrate -> a session on Nexus (a live contact, an edit) -> roll back -> check.
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
                // A leftover queue from an earlier Nexus session still holding a contact: the move
                // must refuse, change nothing, and not even make a backup.
                Directory.CreateDirectory(folder);
                var leftover = new NexusLogOutbox(Path.Combine(folder, "outbox.json"));
                leftover.Add("left-behind", new NexusQso { Call = "ZZ5ZZZ", Band = "20m", Mode = "FT8", WhenUnix = 1_790_000_000, TimeKnown = true });
                var (okPending, repPending) = NexusLogbookMigration.Migrate();
                sb.AppendLine("  with a contact pending: " + repPending.Replace("\n", " | "));
                Check("the move refuses while a contact is still pending, and changes nothing",
                    !okPending && !File.Exists(NexusLogbook.ActiveMarker) &&
                    Directory.GetFiles(Path.GetDirectoryName(jimmyDb), "logbook.before-nexus-*.db").Length == 0 &&
                    new NexusLogOutbox(Path.Combine(folder, "outbox.json")).Count == 1);
                Directory.Delete(folder, true);

                string hashBefore = Hash(jimmyDb);
                var (ok1, rep1) = NexusLogbookMigration.Migrate();
                sb.AppendLine("  migrate: " + rep1.Replace("\n", " | "));
                Check("migrate succeeded and switched", ok1 && File.Exists(NexusLogbook.ActiveMarker));
                Check("a backup of the Jimmy logbook was made",
                    Directory.GetFiles(Path.GetDirectoryName(jimmyDb), "logbook.before-nexus-*.db").Length == 1);
                Check("the Jimmy logbook file itself was not changed", NexusMigration.ReadJimmyRows(jimmyDb).Count == original.Count);
                Check("a read projection is ready for the first start", Directory.GetFiles(NexusLogbook.ProjectionFolder, "p-*.db").Length == 1);

                // A session while Nexus owns the log.
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
                    new NexusLogClient(port).Shutdown(token);
                }
                NexusLogbook.Reset();
                NexusLogbook.TestPortOverride = null;
                Check("Jimmy's own logbook file is untouched while Nexus keeps the log", Hash(jimmyDb) == hashBefore);
                var backupFile = Directory.GetFiles(Path.GetDirectoryName(jimmyDb), "logbook.before-nexus-*.db").Single();
                Check("the backup holds the logbook as it was at the move", NexusMigration.ReadJimmyRows(backupFile).Count == original.Count);

                var (ok2, rep2) = NexusLogbookMigration.Rollback();
                sb.AppendLine("  rollback: " + rep2.Replace("\n", " | "));
                NexusLogbook.TestForceActive = null;
                var back = NexusMigration.ReadJimmyRows(jimmyDb);
                Check("rollback succeeded and switched back", ok2 && !File.Exists(Path.Combine(folder, "ACTIVE")));
                Check("the Jimmy logbook now holds the session's changes",
                    back.Count == original.Count + 1 && back.Any(x => x.C("callsign") == "ZZ9ZZZ") &&
                    back.First(x => x.Id == original[10].Id).C("name") == "CHANGED IN NEXUS", $"{back.Count} rows");
                Check("everything else is as it was",
                    NexusMigrationDryRun.CompareJimmy(original.Where(x => x.Id != original[10].Id).ToList(),
                        back.Where(x => x.C("callsign") != "ZZ9ZZZ" && x.Id != original[10].Id).ToList()).Count == 0);
                Check("the replaced Jimmy file and the Nexus folder were kept",
                    Directory.GetFiles(Path.GetDirectoryName(jimmyDb), "logbook.pre-rollback-*.db").Length == 1 &&
                    Directory.GetDirectories(workRoot, "NexusLog.rolled-back-*").Length == 1);
            }
            finally
            {
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
