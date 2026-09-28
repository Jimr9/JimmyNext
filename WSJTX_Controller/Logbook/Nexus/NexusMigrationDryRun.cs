using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace WSJTX_Controller
{
    // Logbook migration Phase 2: the whole round trip on a COPY, in a temp work folder --
    //   Jimmy copy -> log.adi -> Nexus (logbook-only EngineHost, its own temp LOCALAPPDATA)
    //   -> compare -> rebuild a Jimmy-format db from Nexus -> compare with the copy.
    // Never uses a real data path, never uploads (EngineHost runs --no-radio and no upload runs
    // in it), and writes its report to <work>\migration-report.txt.
    public static class NexusMigrationDryRun
    {
        public class Result
        {
            public bool ForwardClean, RoundTripClean, ChangesSurvive;
            public string Report;
        }

        public static Result Run(string jimmyDbCopy, string workDir, string engineExe, int port)
        {
            Directory.CreateDirectory(workDir);
            string logDir = Path.Combine(workDir, "NexusLog");
            string adi = Path.Combine(logDir, "log.adi");
            if (Directory.Exists(logDir)) throw new IOException("work folder already holds a NexusLog: " + logDir);
            var sb = new StringBuilder();
            sb.AppendLine($"Jimmy Next -> Nexus logbook migration DRY RUN, mapping v{NexusMigration.MappingVersion}, {DateTime.UtcNow:u}");
            sb.AppendLine($"Source (copy): {jimmyDbCopy}");
            sb.AppendLine($"Work folder:   {workDir}");
            sb.AppendLine();

            var original = NexusMigration.ReadJimmyRows(jimmyDbCopy);
            var written = NexusMigration.WriteAdif(jimmyDbCopy, adi);

            string token = Guid.NewGuid().ToString("N");
            var result = new Result();
            using (var engine = LogbookOnlyEngine.Start(engineExe, logDir, Path.Combine(workDir, "appdata"), port, token))
            {
                var client = new NexusLogClient(port);
                var rows = client.Rows();
                if (rows.Error != null) throw new InvalidOperationException("LOG_ROWS failed: " + rows.Error);
                sb.AppendLine($"Nexus: {client.StatusJson()}");
                sb.AppendLine($"Read freshness: {rows.Freshness}");
                sb.AppendLine();

                sb.AppendLine("== 1. Jimmy -> Nexus, field by field ==");
                var fwd = NexusMigration.Compare(original, rows.Rows);
                result.ForwardClean = fwd.Clean;
                sb.AppendLine(NexusMigration.Report(fwd, written));

                sb.AppendLine("== 2. Rollback: Nexus -> rebuilt Jimmy db, compared with the original ==");
                string rebuiltPath = Path.Combine(workDir, "rebuilt-logbook.db");
                var rebuilt = NexusMigration.Rebuild(rows.Rows, rebuiltPath);
                foreach (var n in rebuilt.Notes) sb.AppendLine("  note: " + n);
                var back = CompareJimmy(original, NexusMigration.ReadJimmyRows(rebuiltPath));
                result.RoundTripClean = back.Count == 0;
                sb.AppendLine(back.Count == 0 ? "RESULT: no differences." : $"RESULT: {back.Count} difference(s).");
                foreach (var d in back) sb.AppendLine("  " + d);

                sb.AppendLine();
                sb.AppendLine("== 3. Changes made in Nexus survive a rollback (edit, delete, LoTW confirmation, upload stamp) ==");
                var changed = ScriptedChanges(client, rows.Rows, workDir, sb, out var expect);
                if (changed)
                {
                    var now = client.Rows();
                    string rebuilt2 = Path.Combine(workDir, "rebuilt-after-changes.db");
                    NexusMigration.Rebuild(now.Rows, rebuilt2);
                    var after = NexusMigration.ReadJimmyRows(rebuilt2);
                    var problems = CheckExpected(original, after, expect);
                    result.ChangesSurvive = problems.Count == 0;
                    sb.AppendLine(problems.Count == 0 ? "RESULT: every change carried back, nothing else moved." : $"RESULT: {problems.Count} problem(s).");
                    foreach (var p in problems) sb.AppendLine("  " + p);
                }

                string shut = client.Shutdown(token);
                sb.AppendLine();
                sb.AppendLine("Engine shutdown: " + (shut ?? "(no reply)"));
            }
            result.Report = sb.ToString();
            File.WriteAllText(Path.Combine(workDir, "migration-report.txt"), result.Report, new UTF8Encoding(false));
            return result;
        }

        // What the scripted changes should leave in a rebuilt Jimmy db, by original row id.
        public class Expectation
        {
            public long EditedRow, DeletedRow, ConfirmedRow, StampedRow;
            public string EditedName;
        }

        // Four changes through the real LOG_* commands, on rows chosen from the migrated log:
        // an operator edit, a deletion, a LoTW confirmation arriving as a LoTW report file, and an
        // eQSL upload stamp. Each must answer "saved".
        private static bool ScriptedChanges(NexusLogClient client, List<NexusQso> rows, string workDir, StringBuilder sb, out Expectation expect)
        {
            expect = new Expectation { EditedName = "ROLLBACK TEST NAME" };
            long RowId(NexusQso q) => long.Parse(q.ExtraValue(NexusMigration.RowIdTag), CultureInfo.InvariantCulture);
            var byRow = rows.Where(q => q.ExtraValue(NexusMigration.RowIdTag) != null).ToList();
            var edit = byRow[0];
            var del = byRow[1];
            var confirm = byRow.FirstOrDefault(q => !q.QslRcvd.Lotw && q.TimeKnown && q != edit && q != del);
            var stamp = byRow.FirstOrDefault(q => q.Upload?.Eqsl == null && q != edit && q != del && q != confirm);
            if (confirm == null || stamp == null) { sb.AppendLine("  skipped: no suitable rows"); return false; }
            expect.EditedRow = RowId(edit); expect.DeletedRow = RowId(del);
            expect.ConfirmedRow = RowId(confirm); expect.StampedRow = RowId(stamp);

            var e = NexusLogClient.FromJson<NexusQso>(NexusLogClient.ToJson(edit));
            e.Name = expect.EditedName;
            var r1 = client.Edit(edit.Id, edit.EditKey, e);
            var r2 = client.Delete(del.Id, del.EditKey);
            var when = DateTimeOffset.FromUnixTimeSeconds((long)confirm.WhenUnix).UtcDateTime;
            string report = Path.Combine(workDir, "lotw-report-test.adi");
            string Fld(string t, string v) => $"<{t}:{Encoding.UTF8.GetByteCount(v)}>{v} ";
            File.WriteAllText(report,
                "ARRL Logbook of the World Status Report\n<PROGRAMID:4>LoTW\n<APP_LoTW_NUMREC:1>1\n<eoh>\n" +
                Fld("CALL", confirm.Call) + Fld("BAND", confirm.Band) + Fld("MODE", confirm.Mode) +
                Fld("QSO_DATE", when.ToString("yyyyMMdd", CultureInfo.InvariantCulture)) +
                Fld("TIME_ON", when.ToString("HHmmss", CultureInfo.InvariantCulture)) + Fld("QSL_RCVD", "Y") + "<eor>\n",
                new UTF8Encoding(false));
            var r3 = client.Merge("lotw", report);
            var r4 = client.StampUpload(stamp.Id, "eqsl", "accepted", 1_790_000_000);
            sb.AppendLine($"  edit row {expect.EditedRow}: {r1.State} {r1.Why}");
            sb.AppendLine($"  delete row {expect.DeletedRow}: {r2.State} {r2.Why}");
            sb.AppendLine($"  LoTW confirmation for row {expect.ConfirmedRow}: {r3.State} {r3.Why} {r3.Detail}");
            sb.AppendLine($"  eQSL upload stamp on row {expect.StampedRow}: {r4.State} {r4.Why}");
            return r1.State == "saved" && r2.State == "saved" && r3.State == "saved" && r4.State == "saved";
        }

        private static List<string> CheckExpected(List<NexusMigration.JimmyRow> original, List<NexusMigration.JimmyRow> after, Expectation x)
        {
            var problems = new List<string>();
            var byId = after.ToDictionary(r => r.Id);
            if (byId.ContainsKey(x.DeletedRow)) problems.Add($"row {x.DeletedRow} was deleted in Nexus but came back");
            if (!byId.TryGetValue(x.EditedRow, out var ed) || ed.C("name") != x.EditedName) problems.Add($"row {x.EditedRow}: the edited name was not carried back");
            if (!byId.TryGetValue(x.ConfirmedRow, out var cf) || cf.C("lotw_qsl_rcvd") != "Y") problems.Add($"row {x.ConfirmedRow}: the LoTW confirmation was not carried back");
            else if (cf.C("qrz_qsl_rcvd") != original.First(r => r.Id == x.ConfirmedRow).C("qrz_qsl_rcvd")) problems.Add($"row {x.ConfirmedRow}: QRZ state changed by a LoTW merge");
            if (!byId.TryGetValue(x.StampedRow, out var st) || string.IsNullOrEmpty(st.C("eqsl_uploaded_at"))) problems.Add($"row {x.StampedRow}: the eQSL upload stamp was not carried back");
            // Everything else exactly as before (the per-row comparison, minus the four rows).
            var skip = new HashSet<long> { x.EditedRow, x.DeletedRow, x.ConfirmedRow, x.StampedRow };
            problems.AddRange(CompareJimmy(original.Where(r => !skip.Contains(r.Id)).ToList(), after.Where(r => !skip.Contains(r.Id)).ToList()));
            return problems;
        }

        // Original Jimmy rows vs rebuilt ones, by row id, on what a rollback must restore.
        // Normalised only where the representation differs, never the meaning: times compared at
        // the minute Jimmy stores, upload times compared as instants, extras as a set (Nexus keeps
        // them sorted).
        public static List<string> CompareJimmy(List<NexusMigration.JimmyRow> a, List<NexusMigration.JimmyRow> b)
        {
            var diffs = new List<string>();
            var byId = b.ToDictionary(r => r.Id);
            string[] plain =
            {
                "callsign","band","mode","qso_date","rst_sent","rst_rcvd","state","country","dxcc","cq_zone","grid","name",
                "comment","operator_call","station_call","my_grid","lotw_qsl_sent","lotw_qsl_rcvd","qrz_qsl_sent","qrz_qsl_rcvd",
                "eqsl_qsl_rcvd","continent","itu_zone","county","iota","sig","sig_info","my_sig","my_sig_info","darc_dok",
                "wpx_prefix","exchange_sent","exchange_rcvd","contest_id","source","source_qso_id","imported_at","modified_at",
            };
            foreach (var r in a)
            {
                if (!byId.TryGetValue(r.Id, out var x)) { diffs.Add($"row {r.Id} {r.C("callsign")}: MISSING after rollback"); continue; }
                foreach (var c in plain)
                    if (!string.Equals(Norm(c, r.C(c)), Norm(c, x.C(c)), StringComparison.Ordinal))
                        diffs.Add($"row {r.Id} {r.C("callsign")}: {c} '{r.C(c)}' -> '{x.C(c)}'");
                if (r.C("time_on").Substring(0, Math.Min(4, r.C("time_on").Length)) != x.C("time_on").Substring(0, Math.Min(4, x.C("time_on").Length)))
                    diffs.Add($"row {r.Id} {r.C("callsign")}: time_on '{r.C("time_on")}' -> '{x.C("time_on")}'");
                if (Hhmm(r.C("time_off")) != Hhmm(x.C("time_off")))
                    diffs.Add($"row {r.Id} {r.C("callsign")}: time_off '{r.C("time_off")}' -> '{x.C("time_off")}'");
                if (r.N("freq_hz") != x.N("freq_hz"))
                    diffs.Add($"row {r.Id} {r.C("callsign")}: freq_hz {r.C("freq_hz")} -> {x.C("freq_hz")}");
                if (Power(r.C("tx_pwr")) != Power(x.C("tx_pwr")))
                    diffs.Add($"row {r.Id} {r.C("callsign")}: tx_pwr '{r.C("tx_pwr")}' -> '{x.C("tx_pwr")}'");
                foreach (var c in new[] { "qrz_uploaded_at", "clublog_uploaded_at", "lotw_uploaded_at", "hrdlog_uploaded_at", "eqsl_uploaded_at" })
                    if (Instant(r.C(c)) != Instant(x.C(c)))
                        diffs.Add($"row {r.Id} {r.C("callsign")}: {c} '{r.C(c)}' -> '{x.C(c)}'");
                var ea = new HashSet<string>(r.Extras.Select(e => e.Tag.ToUpperInvariant() + "=" + e.Value));
                var eb = new HashSet<string>(x.Extras.Where(e => e.Tag != "APP_NEXUS_ID").Select(e => e.Tag.ToUpperInvariant() + "=" + e.Value));
                foreach (var e in ea.Except(eb)) diffs.Add($"row {r.Id} {r.C("callsign")}: extra lost {e}");
                foreach (var e in eb.Except(ea)) diffs.Add($"row {r.Id} {r.C("callsign")}: extra added {e}");
            }
            foreach (var x in b)
                if (!a.Any(r => r.Id == x.Id)) diffs.Add($"row {x.Id} {x.C("callsign")}: NEW after rollback (not in the original)");
            return diffs;
        }

        // band: Jimmy stores lower case. state: Nexus stores STATE upper case (the forward report
        // lists each one under "Normalised by Nexus"); Jimmy's own WAS count already compares
        // UPPER(TRIM(state)), so the meaning is the same.
        private static string Norm(string col, string v) =>
            col == "band" ? (v ?? "").ToLowerInvariant() : col == "state" ? (v ?? "").ToUpperInvariant() : (v ?? "");
        private static string Hhmm(string t) => (t ?? "").Length >= 4 ? t.Substring(0, 4) : (t ?? "");
        private static string Power(string p) =>
            double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w.ToString("0.###", CultureInfo.InvariantCulture) : (p ?? "");
        private static long Instant(string iso) =>
            DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
                ? new DateTimeOffset(t.ToUniversalTime()).ToUnixTimeSeconds() : 0;
    }

    // A logbook-only jimmy-engine-host (--no-radio) for tools and tests: no audio, CAT or PTT, its
    // LOCALAPPDATA redirected into the work folder, killed on Dispose only if SHUTDOWN did not
    // already end it.
    public sealed class LogbookOnlyEngine : IDisposable
    {
        private readonly Process _process;
        private LogbookOnlyEngine(Process p) { _process = p; }

        // crashAt: TEST ONLY -- a crash point for the recovery tests (JIMMY_TEST_CRASH_AT, see
        // logbook_host.rs). null in every other use.
        public static LogbookOnlyEngine Start(string exe, string logDir, string appDataDir, int port, string token, string crashAt = null)
        {
            Directory.CreateDirectory(appDataDir);
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var a in new[] { "--mycall", "KB0UZT", "--mygrid", "EN34", "--control-port", port.ToString(CultureInfo.InvariantCulture),
                                      "--session-token", token, "--no-radio", "--log-dir", logDir })
                psi.ArgumentList.Add(a);
            psi.Environment["LOCALAPPDATA"] = appDataDir;
            psi.Environment["JIMMY_TEST_CRASH_AT"] = crashAt ?? "";
            var p = Process.Start(psi);
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();
            var client = new NexusLogClient(port);
            var deadline = DateTime.UtcNow.AddSeconds(120); // a first open converts the whole log
            while (DateTime.UtcNow < deadline)
            {
                if (p.HasExited) throw new InvalidOperationException("engine host exited during start, code " + p.ExitCode);
                string s = client.StatusJson();
                if (s != null && s.Contains("\"enabled\":true")) return new LogbookOnlyEngine(p);
                Thread.Sleep(200);
            }
            try { p.Kill(); } catch { }
            throw new TimeoutException("engine host did not answer LOG_STATUS");
        }

        public bool WaitForExit(int ms) => _process.WaitForExit(ms);
        public int? ExitCode => _process.HasExited ? _process.ExitCode : (int?)null;

        public void Dispose()
        {
            try { if (!_process.WaitForExit(15_000)) _process.Kill(); } catch { }
            _process.Dispose();
        }
    }
}
