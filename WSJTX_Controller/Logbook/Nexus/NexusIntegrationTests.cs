using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace WSJTX_Controller
{
    // Logbook migration Phase 5-6 proof, on a COPY of a real logbook in a temp folder: Jimmy's own
    // code paths (the ILogbookService Nexus implementation, AdifImporter, EqslReconciler's route,
    // RuleEngine) running against a logbook-only jimmy-engine-host that owns the migrated copy.
    // Never touches a real data path; no upload service is contacted.
    public static class NexusIntegrationTests
    {
        public class Result { public bool Passed; public string Report; }

        public static Result Run(string engineExe, string jimmyDbCopy, string rulesFolder, string workRoot, int port)
        {
            var sb = new StringBuilder();
            bool all = true;
            void Check(string name, bool ok, string detail = "")
            {
                sb.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail == "" ? "" : "  -> " + detail)}");
                all &= ok;
            }

            string folder = Path.Combine(workRoot, "NexusLog");
            Directory.CreateDirectory(folder);
            var original = NexusMigration.ReadJimmyRows(jimmyDbCopy);
            NexusMigration.WriteAdif(jimmyDbCopy, Path.Combine(folder, "log.adi"));
            string token = Guid.NewGuid().ToString("N");
            NexusLogbook.Reset();
            NexusLogbook.TestFolderOverride = folder;
            NexusLogbook.TestPortOverride = port;
            NexusLogbook.TestForceActive = true;
            try
            {
                using (var engine = LogbookOnlyEngine.Start(engineExe, folder, Path.Combine(workRoot, "appdata"), port, token))
                {
                    Check("projection built from Nexus", NexusLogbook.Refresh(force: true));
                    string proj = NexusLogbook.ProjectionPath;
                    ILogbookService svc = LogbookFactory.Open();
                    Check("the factory gives the Nexus service while active", svc is NexusLogbookService);

                    // ── Reads: identical to Jimmy's own database ───────────────────────────
                    using (var orig = new LogbookDb(jimmyDbCopy))
                    {
                        Check("total contacts", svc.TotalQsos() == orig.TotalQsos(), $"{svc.TotalQsos()} vs {orig.TotalQsos()}");
                        Check("confirmed (LoTW or QRZ)", svc.ConfirmedQsos() == orig.ConfirmedQsos(), $"{svc.ConfirmedQsos()} vs {orig.ConfirmedQsos()}");
                        Check("LoTW / QRZ / eQSL confirmed", svc.LotwConfirmedQsos() == orig.LotwConfirmedQsos() &&
                              svc.QrzConfirmedQsos() == orig.QrzConfirmedQsos() && svc.EqslConfirmedQsos() == orig.EqslConfirmedQsos());
                        foreach (var band in new[] { null, "20m", "40m" })
                            Check($"WAS / DXCC / WAZ {(band ?? "all bands")}",
                                svc.WasProgress(band) == orig.WasProgress(band) && svc.DxccProgress(band) == orig.DxccProgress(band) &&
                                svc.WazProgress(band) == orig.WazProgress(band));
                        var some = original[original.Count / 2];
                        string call = some.C("callsign"), band2 = some.C("band");
                        Check($"search and worked-before ({call})",
                            svc.SearchByCallsign(call).Count == orig.SearchByCallsign(call).Count &&
                            svc.HasWorkedBefore(call, band2) && svc.HasWorkedBefore(call) && !svc.HasWorkedBefore("ZZ0ZZZ"));
                        Check("recent contacts", string.Join(",", svc.GetRecentQsos(10).Select(q => q.Callsign)) ==
                                                 string.Join(",", orig.GetRecentQsos(10).Select(q => q.Callsign)));
                        Check("pending uploads (QRZ, LoTW)", svc.GetPendingUploads("QRZ").Count == orig.GetPendingUploads("QRZ").Count &&
                                                            svc.GetPendingUploads("LOTW").Count == orig.GetPendingUploads("LOTW").Count);
                    }
                    int awardsChecked = 0, awardsDiffer = 0;
                    foreach (var f in Directory.GetFiles(rulesFolder, "*.ini"))
                    {
                        var def = RuleLoader.ParseAndValidate(f, out _);
                        if (def == null || def.Id.StartsWith("_")) continue;
                        try
                        {
                            var a = RuleEngine.Evaluate(def, jimmyDbCopy);
                            var b = RuleEngine.Evaluate(def, proj);
                            awardsChecked++;
                            if (a.Worked != b.Worked || a.Confirmed != b.Confirmed)
                            {
                                awardsDiffer++;
                                sb.AppendLine($"      {def.Id}: worked {a.Worked}/{b.Worked}, confirmed {a.Confirmed}/{b.Confirmed}");
                            }
                        }
                        catch (Exception ex) { sb.AppendLine($"      {def.Id}: not evaluated ({ex.Message})"); }
                    }
                    Check($"every shipped award reads the same ({awardsChecked} checked)", awardsChecked > 0 && awardsDiffer == 0, $"{awardsDiffer} differ");

                    // ── The rebuilt Jimmy copy is only a disposable read cache ───────────────
                    int totalBefore = svc.TotalQsos();
                    NexusLogbook.Reset();
                    NexusLogbook.TestFolderOverride = folder;
                    foreach (var f in Directory.GetFiles(NexusLogbook.ProjectionFolder)) File.Delete(f);
                    Check("with the read cache deleted, reads are empty (never an old Jimmy file)", svc.TotalQsos() == 0);
                    Check("...and the log counts as LOADING, not as an empty log", !NexusLogbook.LogReady);
                    int readySignals = 0;
                    Action onReady = () => readySignals++;
                    NexusLogbook.LogBecameReady += onReady;
                    NexusLogbook.Refresh(force: true);
                    NexusLogbook.Refresh(force: true);
                    NexusLogbook.LogBecameReady -= onReady;
                    Check("the read cache rebuilds from Nexus, identical", svc.TotalQsos() == totalBefore);
                    Check("ready again, and 'ready' is signalled exactly once", NexusLogbook.LogReady && readySignals == 1, $"{readySignals} signals");

                    // ── Live logging (Jimmy's own RequestLog path) ───────────────────────────
                    int before = svc.TotalQsos();
                    string live = "<CALL:6>ZZ9ZZZ <BAND:3>20m <FREQ:9>14.075500 <MODE:3>FT8 <QSO_DATE:8>20260928 <TIME_ON:6>120000 " +
                                  "<TIME_OFF:6>120130 <RST_SENT:3>-10 <RST_RCVD:3>-12 <GRIDSQUARE:4>FN31 <STATION_CALLSIGN:6>KB0UZT <MY_GRIDSQUARE:4>EN34 <EOR>";
                    var r1 = AdifImporter.Import(svc, AdifParser.ParseWithOrder(live), QsoRecord.JimmyNextSource);
                    string liveKey = AdifImporter.BuildDedupKey("ZZ9ZZZ", "20m", "FT8", "20260928", "120000");
                    Check("live contact queued", r1.NewQsos == 1);
                    Check("live contact reached Nexus", NexusLogbook.WaitSent(NexusLogbookService.RequestIdFor(NexusLogbookService.LiveRequestPrefix, liveKey), 30_000));
                    AdifImporter.Import(svc, AdifParser.ParseWithOrder(live), QsoRecord.JimmyNextSource); // a retry of the same contact
                    NexusLogbook.WaitSent(NexusLogbookService.RequestIdFor(NexusLogbookService.LiveRequestPrefix, liveKey), 30_000);
                    NexusLogbook.Refresh(force: true);
                    Check("live contact logged exactly once, and worked-before sees it",
                        svc.TotalQsos() == before + 1 && svc.HasWorkedBefore("ZZ9ZZZ", "20m"), $"{svc.TotalQsos()} vs {before + 1}");

                    // ── A LoTW download confirming an existing contact ───────────────────────
                    var target = original.First(r => r.C("lotw_qsl_rcvd") != "Y" && r.C("qrz_qsl_rcvd") != "Y");
                    int lotwBefore = svc.LotwConfirmedQsos(), qrzBefore = svc.QrzConfirmedQsos();
                    string lotw = $"<CALL:{target.C("callsign").Length}>{target.C("callsign")} <BAND:{target.C("band").Length}>{target.C("band")} " +
                                  $"<MODE:{target.C("mode").Length}>{target.C("mode")} <QSO_DATE:8>{target.C("qso_date")} <TIME_ON:6>{target.C("time_on").PadRight(6, '0')} <QSL_RCVD:1>Y <EOR>";
                    var r2 = AdifImporter.Import(svc, AdifParser.ParseWithOrder(lotw), "LOTW");
                    Check($"LoTW download confirms {target.C("callsign")} as LoTW (not a card), QRZ untouched",
                        svc.LotwConfirmedQsos() == lotwBefore + 1 && svc.QrzConfirmedQsos() == qrzBefore && r2.NewlyConfirmed == 1,
                        $"lotw {svc.LotwConfirmedQsos()} vs {lotwBefore + 1}, newly {r2.NewlyConfirmed}; {r2.Errors}");
                    var cardDef = new RuleDefinition { Id = "CARDTEST", Name = "t", GroupBy = RuleGroupBy.None, Target = RuleTargetType.Count, Threshold = 1,
                                                       Confirmation = RuleConfirmation.Sources, ConfirmationSources = new List<string> { "CARD" } };
                    Check("no paper card appeared", RuleEngine.Evaluate(cardDef, NexusLogbook.ProjectionPath).Confirmed == 0);

                    // ── The full contact editor (ContactEditDlg): every field it writes is kept,
                    //    an upload can be marked not sent, and the LoTW confirmation survives ──
                    var editor = (NexusLogbookService)svc;
                    var full = editor.GetRecord((int)target.Id);
                    full.Qth = "TEST QTH"; full.Notes = "test notes"; full.MyRig = "TEST RIG"; full.Dxcc = 1;
                    full.Extra.RemoveAll(kv => kv.Count == 2 && kv[0] == "CNTY");   // one value per field, as the editor does
                    full.Extra.Add(new List<string> { "CNTY", "MO,TEST" });
                    editor.SaveRecord(full, new List<(string, bool)> { ("qrz", false) });
                    var saved = NexusLogbook.Client.Rows().Rows.First(x => x.Id == full.Id);
                    Check("full edit: QTH, notes, rig, county and DXCC kept; QRZ marked not sent; LoTW confirmation kept",
                        saved.Qth == "TEST QTH" && saved.Notes == "test notes" && saved.MyRig == "TEST RIG" && saved.Dxcc == 1 &&
                        saved.ExtraValue("CNTY") == "MO,TEST" && saved.Upload?.Qrz?.IsSent == false && saved.QslRcvd.Lotw);

                    // ── Bulk edit: one read, an edit per contact, one rebuild at the end ────
                    var bulkIds = original.Where(r => r.Id != target.Id).Take(200).Select(r => (int)r.Id).ToList();
                    var bulkClock = System.Diagnostics.Stopwatch.StartNew();
                    var bulk = editor.BulkEdit(bulkIds, q => { if (q.MyRig == "BULK RIG") return false; q.MyRig = "BULK RIG"; return true; });
                    bulkClock.Stop();
                    var afterBulk = NexusLogbook.Client.Rows().Rows;
                    Check($"bulk edit: {bulkIds.Count} contacts changed, read copy rebuilt once ({bulkClock.ElapsedMilliseconds} ms)",
                        bulk.Changed == bulkIds.Count && bulk.Failed == 0 &&
                        afterBulk.Count(x => x.MyRig == "BULK RIG") == bulkIds.Count,
                        $"changed {bulk.Changed}, failed {bulk.Failed} ({bulk.FirstError})");
                    var again = editor.BulkEdit(bulkIds, q => { if (q.MyRig == "BULK RIG") return false; q.MyRig = "BULK RIG"; return true; });
                    Check("bulk edit again: every contact already had the value, nothing saved", again.Changed == 0 && again.Same == bulkIds.Count);

                    // ── Manual add, and Nexus's own duplicate rule (D2) ─────────────────────
                    int beforeManual = svc.TotalQsos();
                    var add = svc.Upsert("ZZ8ZZZ", "40m", "FT8", "20260928", "130000", "130100", 7_075_500, "-05", "-07", "", "", 0, 0,
                        "EM12", "", "", "", "", "KB0UZT", "EN34", "", "", "", "", "MANUAL", "", AdifImporter.BuildDedupKey("ZZ8ZZZ", "40m", "FT8", "20260928", "130000"),
                        "", 0, "", "", "", "", "", "", "", "", "", "");
                    var dup = svc.Upsert("ZZ8ZZZ", "40m", "FT8", "20260928", "130200", "130300", 7_075_500, "-05", "-07", "", "", 0, 0,
                        "EM12", "", "", "", "", "KB0UZT", "EN34", "", "", "", "", "MANUAL", "", AdifImporter.BuildDedupKey("ZZ8ZZZ", "40m", "FT8", "20260928", "130200"),
                        "", 0, "", "", "", "", "", "", "", "", "", "");
                    Check("manual add saved; a duplicate two minutes later refused and held with its details",
                        add.isNew && !dup.isNew && svc.TotalQsos() == beforeManual + 1 &&
                        NexusLogbook.Outbox.Refused.Any(x => x.Qso.Call == "ZZ8ZZZ" && x.Qso.WhenUnix == (ulong)NexusMigration.UnixOf("20260928", "130200")));

                    // ── Edit and delete by stable row id ────────────────────────────────────
                    var editRow = original[3];
                    int editId = (int)editRow.Id;
                    svc.UpdateQso(editId, editRow.C("callsign"), editRow.C("band"), editRow.C("mode"), editRow.C("qso_date"), editRow.C("time_on"),
                        editRow.C("time_off"), editRow.C("state"), editRow.C("country"), editRow.C("grid"), "EDITED NAME", editRow.C("rst_sent"),
                        editRow.C("rst_rcvd"), editRow.C("comment"));
                    Check("edit reaches Nexus and shows in the view", svc.GetQso(editId)?.Name == "EDITED NAME");
                    var liveRow = svc.SearchByCallsign("ZZ9ZZZ").Single();
                    int liveId = svc.SearchQsos("ZZ9ZZZ", null, null, null).Single().Id;
                    var delRow = original[5];
                    int total = svc.TotalQsos();
                    int deleted = svc.DeleteQsos(new[] { (int)delRow.Id });
                    Check("delete reaches Nexus", deleted == 1 && svc.TotalQsos() == total - 1 && svc.GetQso((int)delRow.Id) == null);
                    Check("a contact logged after the move keeps its row id across rebuilds",
                        svc.SearchQsos("ZZ9ZZZ", null, null, null).Single().Id == liveId);

                    // ── Upload stamps in Nexus's own terms ──────────────────────────────────
                    int pendingQrz = svc.GetPendingUploads("QRZ").Count;
                    svc.MarkUploaded(liveKey, "QRZ", DateTime.UtcNow);
                    Check("QRZ upload of the live contact recorded", svc.GetPendingUploads("QRZ").Count == pendingQrz - 1);
                    var owedHrd = svc.GetPendingUploads("HRDLOG").Select(p => p.Callsign).OrderBy(c => c).ToList();
                    int migratedOwed = original.Count(r => string.IsNullOrEmpty(r.C("hrdlog_uploaded_at")));
                    Check("HRDLog is carried: the two contacts added after the move are owed to HRDLog, the migrated history keeps its uploads",
                        owedHrd.Count(c => c == "ZZ9ZZZ" || c == "ZZ8ZZZ") == 2 && owedHrd.Count == 2 + migratedOwed,
                        $"owed: {string.Join(",", owedHrd.Take(8))} ({owedHrd.Count}), migrated without an HRDLog upload: {migratedOwed}");

                    // ── A contest completion, then its session rows and exchange ────────────
                    var nexusSvc = (NexusLogbookService)svc;
                    var cq = new NexusQso { Call = "ZZ7ZZZ", Band = "20m", Mode = "FT8", WhenUnix = 1_790_100_000, TimeKnown = true, StationCallsign = "KB0UZT" };
                    nexusSvc.LogContestCompletion("NEXUS_CONTEST:test-session:1", cq, "ARRL-FD", "test-session",
                        new List<(string, string)> { ("CLASS", "2A"), ("SECTION", "MO") });
                    var sess = svc.GetContestSessionRows("test-session");
                    var ex2 = sess.Count == 1 ? svc.GetExtraFields(sess[0].Id) : new List<(string Tag, string Value)>();
                    Check("contest completion saved with its session and structured exchange",
                        sess.Count == 1 && ex2.Any(e => e.Tag == "CLASS" && e.Value == "2A") && ex2.Any(e => e.Tag == "SECTION" && e.Value == "MO"));

                    // ── Meta and import history (Jimmy's own bookkeeping) ────────────────────
                    svc.SetMeta("k", "v");
                    int logId = svc.LogImportStart("LOTW");
                    svc.LogImportFinish(logId, 1, 0, 1, 0, 0, "");
                    Check("meta and import history kept", svc.GetMeta("k") == "v" && svc.GetImportHistory(5).First().NewlyConfirmed == 1);

                    // ── A read copy rebuilt from what Nexus holds now ──────────────────────────────────
                    var rows = NexusLogbook.Client.Rows();
                    string rb = Path.Combine(workRoot, "rebuilt.db");
                    NexusMigration.Rebuild(rows.Rows, rb);
                    var back = NexusMigration.ReadJimmyRows(rb);
                    Check("the rebuilt read copy carries every change (edit, delete, live, manual, contest, confirmation)",
                        back.Count == original.Count - 1 + 3 &&
                        back.First(r => r.Id == editRow.Id).C("name") == "EDITED NAME" &&
                        back.All(r => r.Id != delRow.Id) &&
                        back.First(r => r.Id == target.Id).C("lotw_qsl_rcvd") == "Y" &&
                        back.Count(r => r.C("callsign") == "ZZ9ZZZ") == 1 && back.Any(r => r.C("callsign") == "ZZ7ZZZ" && r.C("contest_session_id") == "test-session"),
                        $"{back.Count} rows vs {original.Count - 1 + 3}");

                    sb.AppendLine("Engine: " + NexusLogbook.Client.Shutdown(token));
                }
            }
            finally
            {
                NexusLogbook.Reset();
                NexusLogbook.TestForceActive = null;
                NexusLogbook.TestFolderOverride = null;
                NexusLogbook.TestPortOverride = null;
            }
            sb.AppendLine(all ? "ALL PASS" : "SOME FAILED");
            var result = new Result { Passed = all, Report = sb.ToString() };
            File.WriteAllText(Path.Combine(workRoot, "integration-report.txt"), result.Report, new UTF8Encoding(false));
            return result;
        }
    }
}
