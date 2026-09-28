using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Read-only diagnosis of a LoTW / QRZ sync while Nexus keeps the log: the same downloads are
    // merged into COPIES of the logbook as it was before the move, three ways, and every
    // confirmation that changes is checked against the downloads at that contact's exact minute.
    //
    //   A  Jimmy's sync as it is: its two LoTW downloads (qso_qsl=yes and qso_qsl=no) joined into
    //      one report, then QRZ's FETCH decoded and re-written the way Jimmy does.
    //   B  Nexus desktop's own path: its LoTW request (qso_qsl=yes, qso_owncall) as received, then
    //      QRZ's FETCH decoded the way Nexus decodes it -- both handed to Nexus unchanged.
    //   C  Jimmy's confirmation download alone (qso_qsl=yes), as received, then QRZ as in A.
    //
    // The real logbook is never opened for writing: a run needs Jimmy Next closed, reads the
    // pre-move backup and the current read copy read-only, and works in a temporary folder that is
    // deleted afterwards. The report is kept in NexusLog\diagnostics.
    public static class NexusSyncDiagnosis
    {
        internal static string TestEngineExeOverride; // JimmyTests only
        private const int Port = 58296;
        private static string EngineExe => TestEngineExeOverride ??
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "EngineHost", "jimmy-engine-host.exe");

        public class Reports
        {
            public string JimmyLotwYes, JimmyLotwNo; // Jimmy's two LoTW requests, as received
            public string NexusLotwYes;              // Nexus desktop's LoTW request, as received
            public string QrzRaw;                    // QRZ FETCH reply, as received
        }

        // ── Command line: Jimmy Next.exe --nexus-logbook-diagnose-sync [yes no nexusYes qrzRaw] ──

        public static void RunInteractive(string[] files)
        {
            string report; bool ok;
            try { (ok, report) = Run(files); }
            catch (Exception ex) { ok = false; report = "Stopped: " + ex.Message; }
            string shown = report.Length > 2500 ? report.Substring(0, 2500) + "\n..." : report;
            MessageBox.Show(shown, "Jimmy Next - Logbook sync diagnosis", MessageBoxButtons.OK,
                ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private static (bool, string) Run(string[] files)
        {
            if (!NexusLogbook.Active) return (false, "Nexus does not keep the logbook here, so there is nothing to diagnose.");
            if (Process.GetProcessesByName("jimmy-engine-host").Length > 0)
                return (false, "An engine host (jimmy-engine-host) is still running. Close Jimmy Next, wait a few seconds, and try again.");
            if (!File.Exists(EngineExe)) return (false, "The engine host was not found: " + EngineExe);
            string backup = File.ReadAllLines(NexusLogbook.ActiveMarker)
                .Where(l => l.StartsWith("backup ")).Select(l => l.Substring(7).Trim()).FirstOrDefault();
            if (backup == null || !File.Exists(backup)) return (false, "The pre-move backup named in the ACTIVE marker was not found.");

            Reports r;
            if (files != null && files.Length >= 4)
                r = new Reports { JimmyLotwYes = File.ReadAllText(files[0]), JimmyLotwNo = File.ReadAllText(files[1]),
                                  NexusLotwYes = File.ReadAllText(files[2]), QrzRaw = File.ReadAllText(files[3]) };
            else
            {
                string err;
                (r, err) = Download();
                if (r == null) return (false, "Download failed: " + err);
            }

            string work = Path.Combine(Path.GetTempPath(), $"jimmy-sync-diagnosis-{DateTime.Now:yyyyMMdd-HHmmss}");
            try
            {
                string text = Diagnose(EngineExe, backup, work, r, NexusLogbook.ProjectionPath);
                string saved = NexusSyncDiagnostics.Retain("diagnosis", text, force: true);
                return (true, $"Report saved: {saved}\n\n{text}");
            }
            finally { try { Directory.Delete(work, true); } catch { } }
        }

        // The same requests a sync makes, with the saved credentials -- downloads only, nothing merged.
        private static (Reports, string) Download()
        {
            var ini = new IniFile(Controller.ActiveIniFilePath());
            string user = ini.Read("lotwLogbookUser") ?? "";
            string pass = CredentialProtector.Unprotect(ini.Read("lotwLogbookPass") ?? "");
            string qrzKey = CredentialProtector.Unprotect(ini.Read("qrzLogbookApiKey") ?? "");
            string myCall = (ini.Read("nativeEngineMyCall") ?? "").Trim();

            var lotw = new LoTWQsoClient();
            string yes = lotw.FetchReportAsync(user, pass, null, confirmedOnly: true).GetAwaiter().GetResult();
            if (yes == null) return (null, "LoTW (confirmations): " + lotw.LastError);
            string no = lotw.FetchReportAsync(user, pass, null, confirmedOnly: false).GetAwaiter().GetResult();
            if (no == null) return (null, "LoTW (own records): " + lotw.LastError);

            // Nexus desktop's request (tempo-core lotw::build_report_url), full pull (no cursor).
            string url = "https://lotw.arrl.org/lotwuser/lotwreport.adi?login=" + Uri.EscapeDataString(user.Trim()) +
                         "&password=" + Uri.EscapeDataString(pass.Trim()) +
                         "&qso_query=1&qso_qsl=yes&qso_qsldetail=yes&qso_withown=yes" +
                         (myCall.Length > 0 ? "&qso_owncall=" + Uri.EscapeDataString(myCall) : "");
            string nexusYes;
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) })
            {
                try { nexusYes = http.GetStringAsync(url).GetAwaiter().GetResult(); }
                catch (Exception ex) { return (null, "LoTW (Nexus request): " + ex.Message); }
            }
            if (nexusYes.IndexOf("<eoh>", StringComparison.OrdinalIgnoreCase) < 0) return (null, "LoTW (Nexus request) did not return a report.");
            NexusSyncDiagnostics.Retain("lotw-nexus-qsl-yes", nexusYes, force: true);

            var qrz = new QrzLogbookClient();
            if (qrz.FetchAdifAsync(qrzKey, since: null).GetAwaiter().GetResult() == null) return (null, "QRZ: " + qrz.LastError);
            return (new Reports { JimmyLotwYes = yes, JimmyLotwNo = no, NexusLotwYes = nexusYes, QrzRaw = qrz.LastRawResponse }, null);
        }

        // ── The comparison ────────────────────────────────────────────────────────────────────

        // One contact at the minute: CALL|band|YYYYMMDD|HHMM.
        private static string Key(string call, string band, string date, string time) =>
            $"{(call ?? "").Trim().ToUpperInvariant()}|{(band ?? "").Trim().ToLowerInvariant()}|{(date ?? "").Trim()}|{((time ?? "").Trim() + "0000").Substring(0, 4)}";
        private static string Key(AdifRawRecord r) =>
            Key(F(r, "CALL"), F(r, "BAND"), F(r, "QSO_DATE"), F(r, "TIME_ON"));
        private static string F(AdifRawRecord r, string tag) => r.Fields.TryGetValue(tag, out var v) ? v ?? "" : "";
        private static bool Yes(string v) => string.Equals((v ?? "").Trim(), "Y", StringComparison.OrdinalIgnoreCase);

        // Nexus's QRZ FETCH parsing (tempo-core qrz::split_adif + html_unescape), reproduced exactly.
        public static string NexusQrzAdif(string raw)
        {
            string lower = raw.ToLowerInvariant();
            int search = 0;
            while (true)
            {
                int idx = lower.IndexOf("adif=", search, StringComparison.Ordinal);
                if (idx < 0) return "";
                if (idx == 0 || raw[idx - 1] == '&')
                    return raw.Substring(idx + 5).Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
                search = idx + 5;
            }
        }

        // Jimmy's (QrzLogbookClient.FetchAdifAsync).
        public static string JimmyQrzAdif(string raw)
        {
            int i = raw.IndexOf("ADIF=", StringComparison.OrdinalIgnoreCase);
            return i < 0 ? "" : System.Net.WebUtility.HtmlDecode(raw.Substring(i + 5));
        }

        private class Outcome
        {
            public string Name, Error;
            public List<string> Merges = new List<string>();
            public List<(string Kind, string Key, string Line)> Unmatched = new List<(string, string, string)>();
            public Dictionary<long, (string Lotw, string Qrz)> After = new Dictionary<long, (string, string)>();
        }

        private static Outcome RunScenario(string name, string engineExe, string startDb, string work, params (string Kind, string Text)[] merges)
        {
            var o = new Outcome { Name = name };
            string dir = Path.Combine(work, name), logDir = Path.Combine(dir, "NexusLog");
            Directory.CreateDirectory(logDir);
            NexusMigration.WriteAdif(startDb, Path.Combine(logDir, "log.adi"));
            string token = Guid.NewGuid().ToString("N");
            using (LogbookOnlyEngine.Start(engineExe, logDir, Path.Combine(dir, "appdata"), Port, token))
            {
                var client = new NexusLogClient(Port);
                int n = 0;
                foreach (var (kind, text) in merges)
                {
                    if (string.IsNullOrEmpty(text)) { o.Merges.Add($"{kind}: (no download)"); continue; }
                    string f = Path.Combine(dir, $"merge-{++n}-{kind}.adi");
                    File.WriteAllText(f, text, new UTF8Encoding(false));
                    var r = client.Merge(kind, f);
                    if (r.State != "saved" || r.Detail == null) { o.Error = $"{kind} merge: {r.State} {r.Why}"; break; }
                    var d = r.Detail.Value;
                    int N(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
                    o.Merges.Add($"{kind}: {N("matched")} matched, {N("added")} added, {N("newlyConfirmed")} newly LoTW/card-confirmed, " +
                                 $"{N("newlyConfirmedAny")} newly confirmed by any source, {N("orphans")} not matched");
                    if (d.TryGetProperty("unmatched", out var list) && list.ValueKind == JsonValueKind.Array)
                        foreach (var u in list.EnumerateArray())
                        {
                            string S(string k) => u.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                            var when = DateTimeOffset.FromUnixTimeSeconds(u.GetProperty("whenUnix").GetInt64()).UtcDateTime;
                            o.Unmatched.Add((kind, Key(S("call"), S("band"), when.ToString("yyyyMMdd"), when.ToString("HHmm")), S("reason")));
                        }
                }
                var rows = client.Rows();
                if (rows.Error != null) o.Error = o.Error ?? "LOG_ROWS: " + rows.Error;
                else
                {
                    string db = Path.Combine(dir, "after.db");
                    NexusMigration.Rebuild(rows.Rows, db);
                    foreach (var r in NexusMigration.ReadJimmyRows(db)) o.After[r.Id] = (r.C("lotw_qsl_rcvd"), r.C("qrz_qsl_rcvd"));
                }
                client.Shutdown(token);
            }
            return o;
        }

        public static string Diagnose(string engineExe, string startDb, string work, Reports r, string realProjection)
        {
            Directory.CreateDirectory(work);
            string start = Path.Combine(work, "start.db");
            File.Copy(startDb, start);
            var before = NexusMigration.ReadJimmyRows(start).ToDictionary(x => x.Id);
            var keyOf = before.Values.ToDictionary(x => x.Id, x => Key(x.C("callsign"), x.C("band"), x.C("qso_date"), x.C("time_on")));
            var logKeys = new HashSet<string>(keyOf.Values);

            var jYes = AdifParser.ParseWithOrder(r.JimmyLotwYes ?? "").ToList();
            var jNo = AdifParser.ParseWithOrder(r.JimmyLotwNo ?? "").ToList();
            var nYes = AdifParser.ParseWithOrder(r.NexusLotwYes ?? "").ToList();
            string qrzNexus = NexusQrzAdif(r.QrzRaw ?? ""), qrzJimmy = JimmyQrzAdif(r.QrzRaw ?? "");
            var qrz = AdifParser.ParseWithOrder(qrzNexus).ToList();

            // What the downloads say, contact by contact at the minute.
            var lotwConfirmed = new HashSet<string>(jYes.Concat(nYes).Concat(jNo).Where(x => Yes(F(x, "QSL_RCVD"))).Select(Key));
            var qrzConfirmed = new HashSet<string>(qrz.Where(x => string.Equals(F(x, "APP_QRZLOG_STATUS"), "C", StringComparison.OrdinalIgnoreCase)).Select(Key));
            var qrzSaysLotw = new HashSet<string>(qrz.Where(x => Yes(F(x, "LOTW_QSL_RCVD"))).Select(Key));
            var inBothLotw = new HashSet<string>(jYes.Select(Key).Intersect(jNo.Select(Key)));

            string lotwJoined = NexusLogbookService.ToAdifText(AdifParser.ParseWithOrder(r.JimmyLotwYes + "\r\n" + r.JimmyLotwNo), "LOTW");
            string qrzAsJimmy = NexusLogbookService.ToAdifText(AdifParser.ParseWithOrder(qrzJimmy), "QRZ");

            var outcomes = new[]
            {
                RunScenario("A-jimmy-today", engineExe, start, work, ("lotw", lotwJoined), ("qrz", qrzAsJimmy)),
                RunScenario("B-nexus-desktop", engineExe, start, work, ("lotw", r.NexusLotwYes), ("qrz", qrzNexus)),
                RunScenario("C-jimmy-confirmations-only", engineExe, start, work, ("lotw", r.JimmyLotwYes), ("qrz", qrzAsJimmy)),
            };

            var sb = new StringBuilder();
            sb.AppendLine($"Logbook sync diagnosis, {DateTime.UtcNow:u}. Copies only; the real log was not changed.");
            sb.AppendLine($"Start: the logbook as it was before the move ({before.Count} contacts).");
            sb.AppendLine();
            sb.AppendLine("Downloads:");
            sb.AppendLine($"  Jimmy LoTW qso_qsl=yes: {jYes.Count} rows ({jYes.Count(x => Yes(F(x, "QSL_RCVD")))} confirmed)");
            sb.AppendLine($"  Jimmy LoTW qso_qsl=no:  {jNo.Count} rows ({jNo.Count(x => Yes(F(x, "QSL_RCVD")))} confirmed)");
            sb.AppendLine($"  -> contacts in BOTH of Jimmy's LoTW downloads: {inBothLotw.Count}");
            sb.AppendLine($"  Nexus desktop LoTW (qso_qsl=yes, qso_owncall): {nYes.Count} rows ({nYes.Count(x => Yes(F(x, "QSL_RCVD")))} confirmed)");
            sb.AppendLine($"  QRZ FETCH: {qrz.Count} rows ({qrzConfirmed.Count} QRZ-confirmed, {qrzSaysLotw.Count} say LoTW-confirmed); " +
                          $"Jimmy's and Nexus's decoding {(qrzJimmy == qrzNexus ? "give identical text" : "DIFFER")}");
            sb.AppendLine();

            foreach (var o in outcomes)
            {
                sb.AppendLine($"== {o.Name} ==");
                foreach (var m in o.Merges) sb.AppendLine("  " + m);
                if (o.Error != null) { sb.AppendLine("  STOPPED: " + o.Error); sb.AppendLine(); continue; }

                var twice = o.Unmatched.Count(u => inBothLotw.Contains(u.Key));
                var inLog = o.Unmatched.Count(u => !inBothLotw.Contains(u.Key) && logKeys.Contains(u.Key));
                var rest = o.Unmatched.Where(u => !inBothLotw.Contains(u.Key) && !logKeys.Contains(u.Key)).ToList();
                sb.AppendLine($"  Not matched: {o.Unmatched.Count} = {twice} sent twice (in both LoTW downloads) + {inLog} other with a logged contact at that minute + {rest.Count} with no logged contact at that minute");
                foreach (var u in rest.Take(40)) sb.AppendLine($"    {u.Kind} {u.Key}: {u.Line}");

                var lotwUp = before.Keys.Where(id => o.After.ContainsKey(id) && !Yes(before[id].C("lotw_qsl_rcvd")) && Yes(o.After[id].Lotw)).ToList();
                var qrzUp = before.Keys.Where(id => o.After.ContainsKey(id) && !Yes(before[id].C("qrz_qsl_rcvd")) && Yes(o.After[id].Qrz)).ToList();
                var lotwFalse = lotwUp.Where(id => !lotwConfirmed.Contains(keyOf[id]) && !qrzSaysLotw.Contains(keyOf[id])).ToList();
                var qrzFalse = qrzUp.Where(id => !qrzConfirmed.Contains(keyOf[id])).ToList();
                var lotwMissed = before.Keys.Where(id => o.After.ContainsKey(id) && lotwConfirmed.Contains(keyOf[id]) && !Yes(o.After[id].Lotw)).ToList();
                sb.AppendLine($"  LoTW newly confirmed: {lotwUp.Count}, of which {lotwUp.Count - lotwFalse.Count} a download confirms at that contact's minute and {lotwFalse.Count} NO download confirms");
                sb.AppendLine($"  QRZ newly confirmed:  {qrzUp.Count}, of which {qrzUp.Count - qrzFalse.Count} QRZ confirms at that minute and {qrzFalse.Count} QRZ does NOT confirm");
                sb.AppendLine($"  LoTW-confirmed at their minute but left unconfirmed in the log: {lotwMissed.Count}");
                foreach (var id in lotwFalse.Concat(qrzFalse).Distinct().Take(60))
                {
                    string k = keyOf[id], pre = k.Substring(0, k.LastIndexOf('|'));
                    var twins = keyOf.Where(p => p.Key != id && p.Value.StartsWith(pre + "|")).Select(p => p.Value.Substring(p.Value.LastIndexOf('|') + 1) +
                        (lotwConfirmed.Contains(p.Value) ? " (LoTW-confirmed)" : "") + (qrzConfirmed.Contains(p.Value) ? " (QRZ-confirmed)" : ""));
                    sb.AppendLine($"    not confirmed at its minute: {k}  {(lotwFalse.Contains(id) ? "LoTW" : "")}{(qrzFalse.Contains(id) ? " QRZ" : "")}; same station/band/day: {string.Join(", ", twins)}");
                }
                sb.AppendLine();
            }

            if (realProjection != null && File.Exists(realProjection))
            {
                string copy = Path.Combine(work, "real-now.db");
                File.Copy(realProjection, copy);
                var real = NexusMigration.ReadJimmyRows(copy).ToDictionary(x => x.Id);
                var a = outcomes[0];
                int diff = real.Keys.Count(id => a.After.TryGetValue(id, out var v) &&
                    (Yes(real[id].C("lotw_qsl_rcvd")) != Yes(v.Lotw) || Yes(real[id].C("qrz_qsl_rcvd")) != Yes(v.Qrz)));
                sb.AppendLine($"Replay check: path A against your real log now: {diff} contact(s) differ in LoTW/QRZ confirmation " +
                              "(0 = this replay reproduces what your syncs did).");
            }
            return sb.ToString();
        }
    }
}
