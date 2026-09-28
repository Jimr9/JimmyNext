using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WSJTX_Controller
{
    // Logbook migration (plan 6.1), Phase 3: Jimmy's own guarantee that a contact is never lost
    // between "Jimmy decided to log it" and "Nexus has it on disk". A killed EngineHost keeps
    // nothing Nexus had not committed, so the request itself must survive on Jimmy's side:
    //
    //   1. Add():    the request (Jimmy request id + the contact) is written to this file FIRST,
    //                durably (temp file, flushed to disk, then atomically swapped in).
    //   2. Send:     LOG_QSO with that request id.
    //   3. Remove(): only when EngineHost answers "saved" (on disk) or "already" (a re-send of a
    //                request it had saved -- found by the id derived from the request id).
    //   Anything else -- no reply, cut-off reply, "unconfirmed", "closed" -- leaves it here, and
    //   Replay() sends it again after a restart. The re-send can never create a second contact.
    //
    // A "duplicate" refusal -- Nexus's own live rule, adopted as is (plan decision D2: no Jimmy
    // duplicate logic, no "log anyway") -- is NOT dropped: the request moves, with every detail of
    // the contact and the contact Nexus matched it to, into the refused list in the same file, and
    // stays there until it is handled (Dismiss). DuplicateRefused lets Jimmy say so once.
    //
    // Not used by live logging yet: Phase 3 proves it with crash tests on isolated data.
    public class NexusLogOutbox
    {
        public class Entry
        {
            public string ReqId { get; set; }
            public NexusQso Qso { get; set; }
            public DateTime QueuedUtc { get; set; }
        }

        public class RefusedEntry
        {
            public string ReqId { get; set; }
            public NexusQso Qso { get; set; }
            public DateTime QueuedUtc { get; set; }
            public DateTime RefusedUtc { get; set; }
            public string ExistingId { get; set; }
            public ulong? ExistingWhenUnix { get; set; }
        }

        private class OutboxFile
        {
            public List<Entry> Queued { get; set; } = new List<Entry>();
            public List<RefusedEntry> Refused { get; set; } = new List<RefusedEntry>();
        }

        // Raised once per refusal, as it is recorded. Not wired to any screen or speech yet.
        public event Action<RefusedEntry> DuplicateRefused;

        public class ReplayResult
        {
            public int Saved, Already, Refused;
            public List<string> RefusedReqIds = new List<string>();
            // True when replay stopped early because EngineHost gave no usable answer; what is
            // left stays queued.
            public bool Stopped;
            public string StopReason;
        }

        private readonly string _path;
        private readonly object _lock = new object();
        private List<Entry> _entries;
        private List<RefusedEntry> _refused;

        public NexusLogOutbox(string path)
        {
            _path = path;
            var f = Load(path);
            _entries = f.Queued;
            _refused = f.Refused;
        }

        public List<RefusedEntry> Refused { get { lock (_lock) return _refused.ToList(); } }

        // The operator (or a later phase's UI) has dealt with a refused contact.
        public void Dismiss(string reqId)
        {
            lock (_lock)
            {
                if (_refused.RemoveAll(r => r.ReqId == reqId) > 0) Save();
            }
        }

        public int Count { get { lock (_lock) return _entries.Count; } }
        public List<Entry> Snapshot() { lock (_lock) return _entries.ToList(); }

        public void Add(string reqId, NexusQso qso)
        {
            lock (_lock)
            {
                if (_entries.Any(e => e.ReqId == reqId)) return;
                _entries.Add(new Entry { ReqId = reqId, Qso = qso, QueuedUtc = DateTime.UtcNow });
                Save();
            }
        }

        public void Remove(string reqId)
        {
            lock (_lock)
            {
                if (_entries.RemoveAll(e => e.ReqId == reqId) > 0) Save();
            }
        }

        // One request: send, then keep or remove by the answer. Returns the answer.
        public NexusLogQsoReply Send(NexusLogClient client, Entry e)
        {
            var reply = client.LogQso(e.ReqId, e.Qso);
            if (reply.State == "saved" || reply.State == "already")
                Remove(e.ReqId);
            else if (reply.State == "duplicate")
            {
                RefusedEntry refused;
                lock (_lock)
                {
                    // One save moves it: never out of both lists, never in both.
                    _entries.RemoveAll(x => x.ReqId == e.ReqId);
                    refused = new RefusedEntry
                    {
                        ReqId = e.ReqId, Qso = e.Qso, QueuedUtc = e.QueuedUtc, RefusedUtc = DateTime.UtcNow,
                        ExistingId = reply.ExistingId, ExistingWhenUnix = reply.ExistingWhenUnix,
                    };
                    if (!_refused.Any(r => r.ReqId == e.ReqId)) _refused.Add(refused);
                    Save();
                }
                DuplicateRefused?.Invoke(refused);
            }
            return reply;
        }

        // Every queued request, oldest first. Stops at the first request without a usable answer
        // (the engine is down or not answering) so order is kept and nothing is skipped.
        public ReplayResult Replay(NexusLogClient client)
        {
            var result = new ReplayResult();
            foreach (var e in Snapshot())
            {
                var reply = Send(client, e);
                switch (reply.State)
                {
                    case "saved": result.Saved++; break;
                    case "already": result.Already++; break;
                    case "duplicate": result.Refused++; result.RefusedReqIds.Add(e.ReqId); break;
                    default:
                        result.Stopped = true;
                        result.StopReason = $"{reply.State}: {reply.Why}";
                        return result;
                }
            }
            return result;
        }

        private void Save()
        {
            string dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = _path + ".tmp";
            byte[] bytes = new UTF8Encoding(false).GetBytes(NexusLogClient.ToJson(new OutboxFile { Queued = _entries, Refused = _refused }));
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true); // to the disk, not just the OS cache
            }
            File.Move(tmp, _path, true);
        }

        private static OutboxFile Load(string path)
        {
            // A leftover .tmp is a write that never finished: the real file is still the last whole
            // one, so the .tmp is ignored (and replaced on the next save).
            if (!File.Exists(path)) return new OutboxFile();
            string text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return new OutboxFile();
            var f = NexusLogClient.FromJson<OutboxFile>(text) ?? new OutboxFile();
            f.Queued ??= new List<Entry>();
            f.Refused ??= new List<RefusedEntry>();
            return f;
        }
    }
}
