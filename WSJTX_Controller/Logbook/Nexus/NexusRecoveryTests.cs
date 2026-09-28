using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace WSJTX_Controller
{
    // Logbook migration Phase 3: crash / recovery proof on ISOLATED data. Each scenario gets its own
    // temp folder (Nexus log + LOCALAPPDATA + outbox) and a real logbook-only jimmy-engine-host,
    // crashed at an exact point by the TEST-only JIMMY_TEST_CRASH_AT hook (a hard exit: no reply,
    // no flush). After a clean restart, Jimmy's outbox is replayed. Pass = every request logged
    // exactly once (counted by its request id in what Nexus reads back), outbox empty, nothing else
    // in the log.
    public static class NexusRecoveryTests
    {
        public class Result
        {
            public bool Passed;
            public string Report;
        }

        private const int CrashExitCode = 86;

        public static Result Run(string engineExe, string workRoot, int port)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Logbook migration Phase 3 -- crash / recovery tests, {DateTime.UtcNow:u}");
            sb.AppendLine($"Work root: {workRoot}");
            bool all = true;
            void Scenario(string name, Func<string, StringBuilder, bool> body)
            {
                string dir = Path.Combine(workRoot, name);
                Directory.CreateDirectory(dir);
                var local = new StringBuilder();
                bool ok;
                try { ok = body(dir, local); }
                catch (Exception e) { ok = false; local.AppendLine("    EXCEPTION " + e.Message); }
                sb.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}");
                sb.Append(local);
                all &= ok;
            }

            // 1. Crash BEFORE the contact reached Nexus: nothing saved; the outbox brings it in once.
            Scenario("crash-before-save", (dir, log) => OneRequest(engineExe, dir, port, "before_log", log));
            // 2. Crash after it was handed to Nexus but before it was confirmed on disk: it may or
            //    may not have landed; either way the replay leaves exactly one.
            Scenario("crash-after-handoff-before-disk", (dir, log) => OneRequest(engineExe, dir, port, "after_log", log));
            // 3. Crash after it was ON DISK but before the reply: Jimmy never heard "saved"; the
            //    replay must answer "already", not log it twice.
            Scenario("crash-after-save-before-reply", (dir, log) => OneRequest(engineExe, dir, port, "after_save", log, expectAlready: true));
            // 4. Crash DURING an outbox replay of five requests, after the 3rd was saved but before
            //    its reply; a second replay finishes the rest. Five contacts, each once.
            Scenario("crash-during-replay-after-save", (dir, log) => Replay(engineExe, dir, port, "after_save:3", 5, log));
            // 5. Crash during a replay BEFORE the 2nd reached Nexus.
            Scenario("crash-during-replay-before-save", (dir, log) => Replay(engineExe, dir, port, "before_log:2", 5, log));
            // 6. Jimmy itself restarts with requests still queued (the outbox file is re-read from
            //    disk by a new instance) and a request is replayed that was already saved.
            Scenario("jimmy-restart-reloads-outbox", (dir, log) => JimmyRestart(engineExe, dir, port, log));
            // 7. Nexus's own live duplicate rule refuses a contact: it is kept, with its details and
            //    the contact it matched, across a Jimmy restart, until dismissed.
            Scenario("duplicate-refusal-kept-until-handled", (dir, log) => DuplicateKept(engineExe, dir, port, log));
            // 8. Another program holds the database: the write is NOT claimed saved, the reason is
            //    visible, and once the lock goes Nexus's own resend saves it; the replay finds it.
            Scenario("disk-locked-then-released", (dir, log) => DiskLocked(engineExe, dir, port, log, shutdownWhileLocked: false));
            // 9. Same, but Jimmy exits while the write is still refused: the shutdown reply says it
            //    is not saved; after restart the outbox brings it in exactly once.
            Scenario("disk-locked-at-shutdown", (dir, log) => DiskLocked(engineExe, dir, port, log, shutdownWhileLocked: true));
            // 10. The database cannot be opened at all: Nexus keeps the log in log.adi for the
            //     session (its own fallback), says so, and saves still survive a restart.
            Scenario("database-cannot-open", (dir, log) => StoreCannotOpen(engineExe, dir, port, log));
            // 11. Phase 4 contract: Jimmy's own APPLY_SETTINGS JSON (receive-only and Hamlib) is
            //     accepted by the real engine host, and a malformed one is refused.
            Scenario("apply-settings-contract", (dir, log) => ApplySettingsContract(engineExe, dir, port, log));

            sb.AppendLine(all ? "ALL PASS" : "SOME FAILED");
            var r = new Result { Passed = all, Report = sb.ToString() };
            File.WriteAllText(Path.Combine(workRoot, "recovery-report.txt"), r.Report, new UTF8Encoding(false));
            return r;
        }

        private static NexusQso Qso(string call, int minute) => new NexusQso
        {
            Call = call, Band = "20m", Mode = "FT8", FreqMhz = 14.075,
            RstSent = "-10", RstRcvd = "-12", Grid = "FN31",
            WhenUnix = (ulong)(1_790_000_000 + minute * 60), TimeKnown = true,
        };

        private static string Req(string dir, int i) => $"{Path.GetFileName(dir)}-req-{i}";

        private static LogbookOnlyEngine Start(string engineExe, string dir, int port, string token, string crashAt = null) =>
            LogbookOnlyEngine.Start(engineExe, Path.Combine(dir, "NexusLog"), Path.Combine(dir, "appdata"), port, token, crashAt);

        private static bool CrashedAt(LogbookOnlyEngine engine, StringBuilder log)
        {
            engine.WaitForExit(15_000);
            bool crashed = engine.ExitCode == CrashExitCode;
            log.AppendLine($"    engine exit code {engine.ExitCode?.ToString() ?? "(still running)"}{(crashed ? " = the test crash" : " -- NOT the test crash")}");
            return crashed;
        }

        // Every request id once, and nothing else.
        private static bool ExactlyOnce(NexusLogClient client, IEnumerable<string> reqIds, StringBuilder log)
        {
            var rows = client.Rows();
            if (rows.Error != null) { log.AppendLine("    LOG_ROWS failed: " + rows.Error); return false; }
            var byReq = rows.Rows.GroupBy(q => q.ExtraValue(LogbookHostReqId) ?? "(none)").ToDictionary(g => g.Key, g => g.Count());
            bool ok = rows.Freshness == "current";
            foreach (var r in reqIds)
            {
                int n = byReq.TryGetValue(r, out var c) ? c : 0;
                if (n != 1) { ok = false; log.AppendLine($"    {r}: {n} contact(s), expected 1"); }
            }
            int extra = rows.Rows.Count(q => !reqIds.Contains(q.ExtraValue(LogbookHostReqId) ?? ""));
            if (extra > 0) { ok = false; log.AppendLine($"    {extra} contact(s) not from these requests"); }
            log.AppendLine($"    Nexus holds {rows.Total} contact(s), read {rows.Freshness}");
            return ok;
        }

        private const string LogbookHostReqId = "APP_JIMMY_REQ_ID";

        private static bool OneRequest(string engineExe, string dir, int port, string crashAt, StringBuilder log, bool expectAlready = false)
        {
            var outbox = new NexusLogOutbox(Path.Combine(dir, "outbox.json"));
            var client = new NexusLogClient(port);
            string req = Req(dir, 1);
            outbox.Add(req, Qso("W1AW", 0));
            string token = Guid.NewGuid().ToString("N");
            bool crashed;
            using (var engine = Start(engineExe, dir, port, token, crashAt))
            {
                var first = outbox.Send(client, outbox.Snapshot()[0]);
                log.AppendLine($"    first send: {first.State} ({first.Why})");
                crashed = CrashedAt(engine, log);
                if (first.State == "saved" || first.State == "already") { log.AppendLine("    the crash did not stop the reply"); return false; }
            }
            bool queued = outbox.Count == 1;
            log.AppendLine($"    outbox after crash: {outbox.Count} queued");
            using (var engine = Start(engineExe, dir, port, token))
            {
                var replay = outbox.Replay(client);
                log.AppendLine($"    replay: saved {replay.Saved}, already {replay.Already}, refused {replay.Refused}{(replay.Stopped ? ", stopped: " + replay.StopReason : "")}");
                bool once = ExactlyOnce(client, new[] { req }, log);
                bool alreadyOk = !expectAlready || replay.Already == 1;
                if (expectAlready) log.AppendLine($"    re-send recognised as already saved: {replay.Already == 1}");
                client.Shutdown(token);
                return crashed && queued && !replay.Stopped && outbox.Count == 0 && once && alreadyOk;
            }
        }

        private static bool Replay(string engineExe, string dir, int port, string crashAt, int count, StringBuilder log)
        {
            var outbox = new NexusLogOutbox(Path.Combine(dir, "outbox.json"));
            var client = new NexusLogClient(port);
            var reqs = Enumerable.Range(1, count).Select(i => Req(dir, i)).ToList();
            for (int i = 0; i < count; i++) outbox.Add(reqs[i], Qso("K" + (i + 1) + "ABC", i));
            string token = Guid.NewGuid().ToString("N");
            bool crashed;
            using (var engine = Start(engineExe, dir, port, token, crashAt))
            {
                var first = outbox.Replay(client);
                log.AppendLine($"    first replay: saved {first.Saved}, already {first.Already}{(first.Stopped ? ", stopped: " + first.StopReason : "")}; {outbox.Count} still queued");
                crashed = CrashedAt(engine, log);
                if (!first.Stopped) { log.AppendLine("    the replay was not interrupted"); return false; }
            }
            using (var engine = Start(engineExe, dir, port, token))
            {
                var second = outbox.Replay(client);
                log.AppendLine($"    second replay: saved {second.Saved}, already {second.Already}{(second.Stopped ? ", stopped: " + second.StopReason : "")}");
                bool once = ExactlyOnce(client, reqs, log);
                client.Shutdown(token);
                return crashed && !second.Stopped && outbox.Count == 0 && once;
            }
        }

        // LOG_STATUS's counts: changes not yet on disk (pending), refused for a reason that can
        // pass (retryable), refused for good (refused), and the writer's reason.
        private static (int pending, int retryable, int refused, string reason, string store) Status(NexusLogClient client)
        {
            string s = client.StatusJson();
            if (s == null) return (-1, -1, -1, "no reply", null);
            using (var doc = System.Text.Json.JsonDocument.Parse(s))
            {
                var r = doc.RootElement;
                int N(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt32() : 0;
                string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
                return (N("pending"), N("retryable"), N("refused"), S("reason"), S("store"));
            }
        }

        private static bool DuplicateKept(string engineExe, string dir, int port, StringBuilder log)
        {
            string path = Path.Combine(dir, "outbox.json");
            string token = Guid.NewGuid().ToString("N");
            var client = new NexusLogClient(port);
            var reqs = new[] { Req(dir, 1), Req(dir, 2) };
            using (var engine = Start(engineExe, dir, port, token))
            {
                var outbox = new NexusLogOutbox(path);
                int raised = 0;
                outbox.DuplicateRefused += _ => raised++;
                outbox.Add(reqs[0], Qso("W1AW", 0));
                var second = Qso("W1AW", 2); // same station, band and mode two minutes later
                second.Comment = "kept verbatim";
                outbox.Add(reqs[1], second);
                var replay = outbox.Replay(client);
                log.AppendLine($"    replay: saved {replay.Saved}, refused {replay.Refused}; event raised {raised}");
                var firstId = client.Rows().Rows.Single().Id;
                var jimmy2 = new NexusLogOutbox(path); // a Jimmy restart re-reads the file
                var kept = jimmy2.Refused.SingleOrDefault();
                bool details = kept != null && kept.ReqId == reqs[1] && kept.Qso.Call == "W1AW" && kept.Qso.Comment == "kept verbatim"
                               && kept.Qso.WhenUnix == second.WhenUnix && kept.ExistingId == firstId;
                log.AppendLine($"    after a Jimmy restart: {jimmy2.Refused.Count} refused kept, details intact {details}, matched contact named {kept?.ExistingId == firstId}");
                jimmy2.Dismiss(reqs[1]);
                bool dismissed = new NexusLogOutbox(path).Refused.Count == 0 && jimmy2.Count == 0;
                log.AppendLine($"    dismissed and gone: {dismissed}");
                var rows = client.Rows();
                log.AppendLine($"    Nexus holds {rows.Total} contact(s)");
                client.Shutdown(token);
                return replay.Saved == 1 && replay.Refused == 1 && raised == 1 && details && dismissed && rows.Total == 1;
            }
        }

        // Holds an exclusive SQLite lock on Nexus's database, as another program could.
        private sealed class DatabaseLock : IDisposable
        {
            private readonly System.Data.SQLite.SQLiteConnection _conn;
            public DatabaseLock(string dbPath)
            {
                _conn = new System.Data.SQLite.SQLiteConnection($"Data Source={dbPath};");
                _conn.Open();
                using (var cmd = _conn.CreateCommand()) { cmd.CommandText = "BEGIN EXCLUSIVE;"; cmd.ExecuteNonQuery(); }
            }
            public void Dispose()
            {
                try { using (var cmd = _conn.CreateCommand()) { cmd.CommandText = "ROLLBACK;"; cmd.ExecuteNonQuery(); } } catch { }
                _conn.Dispose();
            }
        }

        private static string DatabaseFile(string dir) =>
            Directory.GetFiles(Path.Combine(dir, "NexusLog"), "*.sqlite3").Single();

        private static bool DiskLocked(string engineExe, string dir, int port, StringBuilder log, bool shutdownWhileLocked)
        {
            string path = Path.Combine(dir, "outbox.json");
            string token = Guid.NewGuid().ToString("N");
            var client = new NexusLogClient(port);
            var reqs = new[] { Req(dir, 1), Req(dir, 2) };
            var outbox = new NexusLogOutbox(path);
            bool ok = true;
            using (var engine = Start(engineExe, dir, port, token))
            {
                outbox.Add(reqs[0], Qso("K1AAA", 0));
                ok &= outbox.Replay(client).Saved == 1;
                outbox.Add(reqs[1], Qso("K2BBB", 1));
                var held = new DatabaseLock(DatabaseFile(dir));
                try
                {
                    var r = outbox.Send(client, outbox.Snapshot()[0]);
                    log.AppendLine($"    while locked: {r.State} -- {r.Why}");
                    ok &= r.State == "unconfirmed" && !string.IsNullOrEmpty(r.Why) && outbox.Count == 1;
                    var st = Status(client);
                    log.AppendLine($"    LOG_STATUS while locked: pending {st.pending}, retryable {st.retryable}, refused {st.refused}, reason: {st.reason}");
                    ok &= st.pending + st.retryable + st.refused > 0 && !string.IsNullOrEmpty(st.reason);
                    if (shutdownWhileLocked)
                    {
                        string sd = client.Shutdown(token);
                        log.AppendLine($"    SHUTDOWN while locked: {sd}");
                        ok &= sd != null && sd.Contains("\"saved\":false");
                        engine.WaitForExit(15_000);
                    }
                }
                finally { held.Dispose(); }
                if (!shutdownWhileLocked)
                {
                    // Nexus's own resend (driven by the LOG_STATUS poll, as the desktop's snapshot poll
                    // drives it) puts the refused change on disk once the lock is gone.
                    var until = DateTime.UtcNow.AddSeconds(120);
                    var st = Status(client);
                    while (DateTime.UtcNow < until && st.pending + st.retryable + st.refused != 0)
                    {
                        System.Threading.Thread.Sleep(1000);
                        st = Status(client);
                    }
                    log.AppendLine($"    after the lock went: pending {st.pending}, retryable {st.retryable}, refused {st.refused}");
                    var replay = outbox.Replay(client);
                    log.AppendLine($"    replay: saved {replay.Saved}, already {replay.Already}");
                    ok &= st.pending + st.retryable + st.refused == 0 && replay.Already == 1 && ExactlyOnce(client, reqs, log);
                    client.Shutdown(token);
                    return ok && outbox.Count == 0;
                }
            }
            using (var engine = Start(engineExe, dir, port, token))
            {
                var before = client.Rows();
                log.AppendLine($"    after restart, before replay: {before.Total} contact(s)");
                var replay = outbox.Replay(client);
                log.AppendLine($"    replay: saved {replay.Saved}, already {replay.Already}");
                ok &= !replay.Stopped && ExactlyOnce(client, reqs, log) && outbox.Count == 0;
                client.Shutdown(token);
                return ok;
            }
        }

        private static bool StoreCannotOpen(string engineExe, string dir, int port, StringBuilder log)
        {
            // A folder where the database file should be: the store cannot be created.
            Directory.CreateDirectory(Path.Combine(dir, "NexusLog", "log.sqlite3"));
            string token = Guid.NewGuid().ToString("N");
            var client = new NexusLogClient(port);
            var outbox = new NexusLogOutbox(Path.Combine(dir, "outbox.json"));
            var req = Req(dir, 1);
            bool ok;
            using (var engine = Start(engineExe, dir, port, token))
            {
                var st = Status(client);
                log.AppendLine($"    LOG_STATUS: store {st.store}; {client.StatusJson()}");
                outbox.Add(req, Qso("W9XYZ", 0));
                var r = outbox.Replay(client);
                log.AppendLine($"    save: saved {r.Saved}{(r.Stopped ? ", stopped: " + r.StopReason : "")}");
                ok = st.store == "file" && r.Saved == 1;
                string sd = client.Shutdown(token);
                log.AppendLine($"    SHUTDOWN: {sd}");
                ok &= sd != null && sd.Contains("\"saved\":true");
            }
            using (var engine = Start(engineExe, dir, port, token))
            {
                ok &= ExactlyOnce(client, new[] { req }, log);
                client.Shutdown(token);
            }
            return ok && outbox.Count == 0;
        }

        private static string SendLine(int port, string line)
        {
            using (var c = new System.Net.Sockets.TcpClient())
            {
                c.Connect(System.Net.IPAddress.Loopback, port);
                using (var st = c.GetStream())
                {
                    st.ReadTimeout = 5000;
                    var b = Encoding.UTF8.GetBytes(line + "\n");
                    st.Write(b, 0, b.Length);
                    using (var r = new StreamReader(st, Encoding.UTF8)) return r.ReadLine();
                }
            }
        }

        private static bool ApplySettingsContract(string engineExe, string dir, int port, StringBuilder log)
        {
            string token = Guid.NewGuid().ToString("N");
            using (var engine = Start(engineExe, dir, port, token))
            {
                var hamlib = new RadioSettings
                {
                    Mode = RadioControlMode.HamlibRigctld, RigModel = "3073", ComPort = "COM7", BaudRate = "38400",
                    PttEnabled = true, PttMethod = PttMethod.Cat, TxMode = RadioTxMode.None, PttDataSource = true,
                    SplitMode = RadioSplitMode.FakeIt,
                };
                string a = SendLine(port, "APPLY_SETTINGS " + NativeEngineClient.BuildApplySettingsJson(hamlib, "In", "Out"));
                string b = SendLine(port, "APPLY_SETTINGS " + NativeEngineClient.BuildApplySettingsJson(new RadioSettings(), "", ""));
                string c = SendLine(port, "APPLY_SETTINGS {\"rigModel\":1}");
                log.AppendLine($"    Hamlib: {a}; receive-only: {b}; malformed: {c}");
                new NexusLogClient(port).Shutdown(token);
                return a == "OK" && b == "OK" && c != null && c.StartsWith("ERR");
            }
        }

        private static bool JimmyRestart(string engineExe, string dir, int port, StringBuilder log)
        {
            string path = Path.Combine(dir, "outbox.json");
            var reqs = new[] { Req(dir, 1), Req(dir, 2) };
            string token = Guid.NewGuid().ToString("N");
            var client = new NexusLogClient(port);
            using (var engine = Start(engineExe, dir, port, token))
            {
                // Jimmy #1 queues two, sends the first, and "dies" before removing it from the outbox
                // (the reply arrived but was never acted on).
                var jimmy1 = new NexusLogOutbox(path);
                jimmy1.Add(reqs[0], Qso("N0AAA", 0));
                jimmy1.Add(reqs[1], Qso("N0BBB", 1));
                var sent = client.LogQso(reqs[0], jimmy1.Snapshot()[0].Qso);
                log.AppendLine($"    Jimmy #1 sent the first ({sent.State}) and stopped with {jimmy1.Count} queued");
                // Jimmy #2 starts, reads the outbox from disk, and replays both.
                var jimmy2 = new NexusLogOutbox(path);
                log.AppendLine($"    Jimmy #2 read {jimmy2.Count} queued from disk");
                var replay = jimmy2.Replay(client);
                log.AppendLine($"    replay: saved {replay.Saved}, already {replay.Already}");
                bool once = ExactlyOnce(client, reqs, log);
                client.Shutdown(token);
                return jimmy2.Count == 0 && replay.Already == 1 && replay.Saved == 1 && once;
            }
        }
    }
}
