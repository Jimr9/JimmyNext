using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace WSJTX_Controller
{
    // Logbook migration Phases 5-6: the switch and the shared state for "Nexus owns the logbook".
    //
    // ACTIVE only when a migration has run: the file NexusLog\ACTIVE exists in Jimmy Next's data
    // folder (written by the migration command, removed by the rollback command). Never in test
    // mode unless a test turns it on explicitly. When not active, nothing here runs and Jimmy's
    // own LogbookDb is the logbook exactly as before.
    //
    // When active:
    //   - Nexus (inside jimmy-engine-host, launched with --log-dir NexusLog) owns storage and saves.
    //   - Writes go through LOG_* commands (NexusLogbookService). New contacts go through Jimmy's
    //     durable outbox, so a contact survives an engine that is restarting or down.
    //   - Reads come from a READ-ONLY PROJECTION: a Jimmy-format database rebuilt from Nexus's
    //     rows (NexusMigration.Rebuild -- the rollback rebuild, proven exact) whenever Nexus's log
    //     revision moves. Every existing screen, award query and worked-before lookup reads it
    //     unchanged. It is never written to and can always be rebuilt; Nexus is the only authority.
    public static class NexusLogbook
    {
        // Test-only overrides (JimmyTests / NexusIntegrationTests); null in production.
        internal static bool? TestForceActive;
        internal static string TestFolderOverride;
        internal static int? TestPortOverride;

        public static string Folder => TestFolderOverride ?? Path.Combine(LookupManager.DataRoot, "NexusLog");
        public static string ActiveMarker => Path.Combine(Folder, "ACTIVE");
        public static bool Active => TestForceActive ?? (!TestModeGuard.IsTestMode && File.Exists(ActiveMarker));
        public static int Port => TestPortOverride ?? NativeEngineClient.ControlPort;
        public static string ProjectionFolder => Path.Combine(Folder, "projection");
        public static string OutboxPath => Path.Combine(Folder, "outbox.json");
        public static string MetaPath => Path.Combine(Folder, "jimmy-meta.json");

        private static readonly object _lock = new object();
        private static NexusLogOutbox _outbox;
        private static string _outboxPath;
        private static string _projectionPath;
        private static ulong _projectionRevision;
        private static LogbookDb _reader;
        private static string _readerPath;
        private static Timer _worker;

        public static NexusLogClient Client => new NexusLogClient(Port);

        public static NexusLogOutbox Outbox
        {
            get
            {
                lock (_lock)
                {
                    if (_outbox == null || _outboxPath != OutboxPath)
                    {
                        _outbox = new NexusLogOutbox(OutboxPath);
                        _outboxPath = OutboxPath;
                    }
                    return _outbox;
                }
            }
        }

        // The projection readers use: the newest one built, or the newest left on disk by a
        // previous session (so reads work at startup and with the engine down).
        public static string ProjectionPath
        {
            get
            {
                lock (_lock)
                {
                    if (_projectionPath != null && File.Exists(_projectionPath)) return _projectionPath;
                    var newest = Directory.Exists(ProjectionFolder)
                        ? Directory.GetFiles(ProjectionFolder, "p-*.db").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                        : null;
                    _projectionPath = newest;
                    return newest;
                }
            }
        }

        // Rebuilds the projection when Nexus's revision moved (or always, with force). Returns
        // false when Nexus could not be read -- readers keep the last projection.
        public static bool Refresh(bool force = false)
        {
            var client = Client;
            var rows = client.Rows();
            if (rows.Error != null || rows.Rows == null) return false;
            lock (_lock)
            {
                if (!force && rows.Revision == _projectionRevision && ProjectionPath != null) return true;
            }
            Directory.CreateDirectory(ProjectionFolder);
            string path = Path.Combine(ProjectionFolder, $"p-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{rows.Revision}.db");
            NexusMigration.Rebuild(rows.Rows, path, StableRowId(rows.Rows));
            lock (_lock)
            {
                string old = _projectionPath;
                _projectionPath = path;
                _projectionRevision = rows.Revision;
                // The shared reader moves to the new file; the old files go when nothing holds them.
                _reader?.Dispose();
                _reader = null;
                _readerPath = null;
                foreach (var f in Directory.GetFiles(ProjectionFolder, "p-*.db"))
                    if (f != path) TryDelete(f);
                if (old != null && old != path) TryDelete(old);
            }
            return true;
        }

        public static string RowIdMapPath => Path.Combine(Folder, "row-ids.json");

        private class RowIdMap
        {
            public long Next { get; set; }
            public Dictionary<string, long> Ids { get; set; } = new Dictionary<string, long>();
        }

        // Jimmy row ids for contacts that came to exist in Nexus (they carry no Jimmy id): assigned
        // once, kept in a file, never reused -- starting above every migrated Jimmy id.
        private static Func<NexusQso, long> StableRowId(List<NexusQso> rows)
        {
            RowIdMap map;
            try { map = File.Exists(RowIdMapPath) ? NexusLogClient.FromJson<RowIdMap>(File.ReadAllText(RowIdMapPath)) : null; }
            catch { map = null; }
            map = map ?? new RowIdMap();
            long maxJimmy = rows.Select(q => long.TryParse(q.ExtraValue(NexusMigration.RowIdTag), out var r) ? r : 0).DefaultIfEmpty(0).Max();
            if (map.Next <= maxJimmy) map.Next = maxJimmy + 1_000_000; // well clear of Jimmy's own ids
            bool changed = false;
            foreach (var q in rows)
            {
                if (q.ExtraValue(NexusMigration.RowIdTag) != null || string.IsNullOrEmpty(q.Id) || map.Ids.ContainsKey(q.Id)) continue;
                map.Ids[q.Id] = map.Next++;
                changed = true;
            }
            if (changed)
            {
                Directory.CreateDirectory(Folder);
                string tmp = RowIdMapPath + ".tmp";
                File.WriteAllText(tmp, NexusLogClient.ToJson(map));
                File.Move(tmp, RowIdMapPath, true);
            }
            return q => map.Ids.TryGetValue(q.Id ?? "", out var id) ? id : map.Next++;
        }

        // A read against the current projection, through one shared LogbookDb (reads only).
        public static T Read<T>(Func<LogbookDb, T> read, T whenUnavailable)
        {
            lock (_lock)
            {
                string path = ProjectionPath;
                if (path == null) return whenUnavailable;
                if (_reader == null || _readerPath != path)
                {
                    _reader?.Dispose();
                    _reader = new LogbookDb(path);
                    _readerPath = path;
                }
                return read(_reader);
            }
        }

        // Background upkeep while active: send anything still queued, then refresh the projection
        // when Nexus's log changed (a download, a resend that landed, another writer).
        public static void StartWorker(Action<string> debug)
        {
            if (!Active || _worker != null) return;
            _worker = new Timer(_ =>
            {
                try
                {
                    if (Outbox.Count > 0)
                    {
                        var r = Outbox.Replay(Client);
                        if (r.Saved + r.Already + r.Refused > 0)
                            debug?.Invoke($"[NexusLog] outbox replay: saved {r.Saved}, already {r.Already}, refused {r.Refused}");
                    }
                    Refresh();
                }
                catch (Exception ex) { debug?.Invoke("[NexusLog] upkeep: " + ex.Message); }
            }, null, 5_000, 10_000);
        }

        public static void StopWorker()
        {
            _worker?.Dispose();
            _worker = null;
        }

        // Waits until a queued request has reached Nexus (it left the outbox), up to timeout.
        public static bool WaitSent(string reqId, int timeoutMs)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                if (!Outbox.Snapshot().Any(e => e.ReqId == reqId)) return true;
                Thread.Sleep(250);
            }
            return false;
        }

        // Jimmy's small key/value store while Nexus owns the log (meta that is Jimmy's, not a
        // contact's: contest watermarks, the state-backfill marker, import history).
        private static readonly object _metaLock = new object();
        public static string GetMeta(string key)
        {
            lock (_metaLock)
            {
                var d = LoadMeta();
                return d.TryGetValue(key, out var v) ? v : null;
            }
        }
        public static void SetMeta(string key, string value)
        {
            lock (_metaLock)
            {
                var d = LoadMeta();
                d[key] = value ?? "";
                Directory.CreateDirectory(Folder);
                string tmp = MetaPath + ".tmp";
                File.WriteAllText(tmp, NexusLogClient.ToJson(d));
                File.Move(tmp, MetaPath, true);
            }
        }
        private static Dictionary<string, string> LoadMeta()
        {
            try
            {
                return File.Exists(MetaPath)
                    ? NexusLogClient.FromJson<Dictionary<string, string>>(File.ReadAllText(MetaPath)) ?? new Dictionary<string, string>()
                    : new Dictionary<string, string>();
            }
            catch { return new Dictionary<string, string>(); }
        }

        // Clears the in-process state (tests, and after a rollback).
        internal static void Reset()
        {
            lock (_lock)
            {
                _reader?.Dispose();
                _reader = null;
                _readerPath = null;
                _projectionPath = null;
                _projectionRevision = 0;
                _outbox = null;
                _outboxPath = null;
            }
            StopWorker();
        }

        private static void TryDelete(string f)
        {
            try { File.Delete(f); } catch { }
            try { File.Delete(f + "-wal"); } catch { }
            try { File.Delete(f + "-shm"); } catch { }
        }

        // The RecordId jimmy-engine-host derives from a request id (logbook_host.rs
        // record_id_for_request): FNV-1a-64 of the request id, position id "JMNX", sequence 1.
        public static string RecordIdForRequest(string reqId)
        {
            ulong h = 0xcbf29ce484222325UL;
            foreach (byte b in System.Text.Encoding.UTF8.GetBytes(reqId))
            {
                h ^= b;
                h = unchecked(h * 0x100000001b3UL);
            }
            return $"{0x4A4D4E58u:x8}:{h:x16}:1";
        }
    }
}
