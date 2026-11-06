using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace WSJTX_Controller
{
    // The Logbook's clean-up tools as Nexus makes them (2026-10-31, Nexus 1.21; EngineHost
    // log_tools.rs, LOG_TOOL): bulk edit, find and merge duplicates, check the log, each contact's
    // history with Undo, Recently deleted with Restore, and the import preview with its no-loss
    // report. Jimmy Next decides none of it -- it asks, shows the answer, and passes the operator's
    // choice back. Every change is one run of Nexus's history, undone whole by UndoRun.
    //
    // Ids: Nexus's own contact ids. The Logbook's list holds the read copy's row ids, mapped here
    // both ways through the row's APP_NEXUS_ID.
    internal static class NexusLogTools
    {
        // ── the answer ───────────────────────────────────────────────────────────────────────

        internal sealed class Answer : IDisposable
        {
            public readonly JsonDocument Doc;
            public JsonElement Root => Doc.RootElement;
            public Answer(string json) { Doc = JsonDocument.Parse(json); }
            public string State => Str(Root, "state");
            public string Why => Str(Root, "why");
            public bool Ok => State == "ok" || State == "saved";
            public void Dispose() => Doc.Dispose();
        }

        internal static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        internal static long Num(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        internal static bool Flag(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        internal static IEnumerable<JsonElement> List(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

        private static Answer Ask(object args)
        {
            try { return new Answer(NexusLogbook.Client.Tool(args)); }
            catch (Exception ex) { return new Answer(JsonSerializer.Serialize(new { state = "error", why = ex.Message })); }
        }

        // A change's answer: refreshes the read copy when anything changed, and records the run
        // so "Undo a change" can name it.
        private static Answer Change(object args, string what)
        {
            var a = Ask(args);
            if (a.Ok && Num(a.Root, "changed") + Num(a.Root, "added") + Num(a.Root, "updated") > 0)
            {
                NexusLogbook.Refresh();
                string run = Str(a.Root, "run");
                if (!string.IsNullOrEmpty(run)) RecordRun(run, what, (int)(Num(a.Root, "changed") + Num(a.Root, "added") + Num(a.Root, "updated")));
            }
            return a;
        }

        private static string WriteTemp(string ext, string text)
        {
            string path = Path.Combine(Path.GetTempPath(), $"jimmy-logtool-{Guid.NewGuid():N}.{ext}");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }

        private static T WithFile<T>(string ext, string text, Func<string, T> use)
        {
            string path = WriteTemp(ext, text);
            try { return use(path); }
            finally { try { File.Delete(path); } catch { } }
        }

        // ── ids ──────────────────────────────────────────────────────────────────────────────

        internal static Dictionary<string, long> RowIdsByNexusId() =>
            NexusLogbook.Read(db => db.IdsByExtraValue("APP_NEXUS_ID"), new Dictionary<string, long>());

        internal static List<string> NexusIdsOf(IEnumerable<int> rowIds)
        {
            var byRow = RowIdsByNexusId().ToDictionary(kv => kv.Value, kv => kv.Key);
            return rowIds.Select(id => byRow.TryGetValue(id, out var n) ? n : null).Where(n => n != null).ToList();
        }

        // ── bulk edit ────────────────────────────────────────────────────────────────────────

        // Nexus's bulk-edit fields (tempo_core cleanup::BULK_FIELDS) by ADIF name, as the change's
        // field. A value of null clears the field.
        internal static Answer BulkPreview(List<string> nexusIds, List<(string Field, string Value)> changes) =>
            WithFile("txt", string.Join("\n", nexusIds), p => Ask(new
            {
                op = "bulkPreview", idsPath = p,
                changes = changes.Select(c => new { field = c.Field, value = c.Value }).ToList(),
            }));

        internal static Answer BulkEdit(List<string> nexusIds, List<(string Field, string Value)> changes, int expect, int moves, string moveTo, string what) =>
            WithFile("txt", string.Join("\n", nexusIds), p => Change(new
            {
                op = "bulkEdit", idsPath = p, expect, moves, moveTo,
                changes = changes.Select(c => new { field = c.Field, value = c.Value }).ToList(),
            }, what));

        // ── duplicates ───────────────────────────────────────────────────────────────────────

        internal static Answer FindDuplicates(bool possible) => Ask(new { op = "dupes", possible });

        // One merge: the copy to keep, every copy with the revision it was shown with, and the copy
        // whose value each conflicting field takes.
        internal sealed class MergeAsk
        {
            public string Keep;
            public List<(string Id, string Revision)> Copies = new List<(string, string)>();
            public Dictionary<string, string> Choices = new Dictionary<string, string>();
        }

        internal static Answer Merge(List<MergeAsk> asks)
        {
            string json = JsonSerializer.Serialize(asks.Select(a => new
            {
                keep = a.Keep,
                copies = a.Copies.Select(c => new[] { c.Id, c.Revision }).ToList(),
                choices = a.Choices,
            }).ToList());
            return WithFile("json", json, p => Change(new { op = "merge", asksPath = p }, $"Merge of {asks.Count} duplicate{(asks.Count == 1 ? "" : "s")}"));
        }

        // ── the check ────────────────────────────────────────────────────────────────────────

        internal static Answer Check() => Ask(new { op = "check" });

        internal sealed class Fix { public string Id, Field, Expect, Value; }

        internal static Answer ApplyFixes(List<Fix> fixes)
        {
            string json = JsonSerializer.Serialize(fixes.Select(f => new { id = f.Id, field = f.Field, expect = f.Expect, value = f.Value }).ToList());
            return WithFile("json", json, p => Change(new { op = "fix", fixesPath = p }, $"{fixes.Count} log check fix{(fixes.Count == 1 ? "" : "es")}"));
        }

        // ── history ──────────────────────────────────────────────────────────────────────────

        internal static Answer History(string nexusId) => Ask(new { op = "history", id = nexusId });
        internal static Answer RecentlyDeleted(int limit = 200) => Ask(new { op = "deleted", limit });
        internal static Answer UndoableRuns(int limit = 50) => Ask(new { op = "runs", limit });
        internal static Answer UndoEntry(long seq, string what) => Change(new { op = "undoEntry", seq }, what);
        internal static Answer UndoRun(string run, string what) => Change(new { op = "undoRun", run }, what);
        internal static Answer Restore(string nexusId, bool anyway, string what) => Change(new { op = "restore", id = nexusId, anyway }, what);

        // ── update from callbook ─────────────────────────────────────────────────────────────
        // The calls are looked up by Jimmy Next's own LookupManager; Nexus decides what goes on
        // each contact (empty fields only; where the station is, only on a contact from the last
        // 12 months; one run).

        internal static Answer CallbookPreview(List<string> nexusIds, Dictionary<string, object> answers) =>
            WithFile("txt", string.Join("\n", nexusIds), ids => answers == null || answers.Count == 0
                ? Ask(new { op = "callbookPreview", idsPath = ids })
                : WithFile("json", JsonSerializer.Serialize(answers), a => Ask(new { op = "callbookPreview", idsPath = ids, answersPath = a })));

        internal static Answer CallbookFill(List<string> nexusIds, Dictionary<string, object> answers, int expect) =>
            WithFile("txt", string.Join("\n", nexusIds), ids =>
                WithFile("json", JsonSerializer.Serialize(answers), a =>
                    Change(new { op = "callbookFill", idsPath = ids, answersPath = a, expect }, $"Update from callbook, {nexusIds.Count} contacts")));

        // A Jimmy Next lookup as Nexus's callbook answer. `key`: the call as Nexus keys it. A
        // stroked call (a portable, a station at a park) carries no location at all: the lookup may
        // have answered with the home call's entry, which says where the home station is. Null
        // when the lookup knows nothing.
        internal static object AnswerOf(string key, LookupRecord r)
        {
            if (r == null) return null;
            string Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            bool place = !key.Contains("/");
            string county = place ? Blank(r.County) : null;
            if (county != null && county.Contains(",")) county = Blank(county.Substring(county.IndexOf(',') + 1));
            var a = new
            {
                call = key,
                name = Blank(r.Name),
                grid = place ? Blank(r.Grid)?.ToUpperInvariant() : null,
                state = place ? Blank(r.State)?.ToUpperInvariant() : null,
                county,
                iota = place ? Blank(r.Iota)?.ToUpperInvariant() : null,
                cqZone = place && r.CqZone > 0 ? (uint?)r.CqZone : null,
                ituZone = place && r.ItuZone > 0 ? (uint?)r.ItuZone : null,
                email = Blank(r.Email),
                qslVia = Blank(r.QslManager),
                lotw = r.IsLoTWUser ? (bool?)true : null,
            };
            bool any = a.name != null || a.grid != null || a.state != null || a.county != null || a.iota != null ||
                       a.cqZone != null || a.ituZone != null || a.email != null || a.qslVia != null || a.lotw != null;
            return any ? a : null;
        }

        // ── import ───────────────────────────────────────────────────────────────────────────

        internal static Answer ImportPreview(string path) => Ask(new { op = "importPreview", path });
        internal static Answer Import(string path, bool newOnly, string what) => Change(new { op = "import", path, newOnly }, what);
        // An ADX or Cabrillo file as ADIF, written to outPath (Nexus's own reading of it).
        internal static Answer ToAdif(string path, string outPath) => Ask(new { op = "toAdif", path, outPath });

        // ── the runs Jimmy Next made, for "Undo a change" ────────────────────────────────────
        // Nexus lists only imports and purges as whole runs to undo (history_undoable_runs); a bulk
        // edit's, a merge's or a fix's run is named only in its own answer. Kept here, newest first.

        internal sealed class RunNote
        {
            public string Run { get; set; }
            public string What { get; set; }
            public int Contacts { get; set; }
            public DateTime AtUtc { get; set; }
        }

        private const string RunsMetaKey = "logtool_runs";

        internal static List<RunNote> RecordedRuns()
        {
            try { return NexusLogClient.FromJson<List<RunNote>>(NexusLogbook.GetMeta(RunsMetaKey) ?? "") ?? new List<RunNote>(); }
            catch { return new List<RunNote>(); }
        }

        private static void RecordRun(string run, string what, int contacts)
        {
            var runs = RecordedRuns();
            runs.RemoveAll(r => r.Run == run);
            runs.Insert(0, new RunNote { Run = run, What = what, Contacts = contacts, AtUtc = DateTime.UtcNow });
            try { NexusLogbook.SetMeta(RunsMetaKey, NexusLogClient.ToJson(runs.Take(30).ToList())); } catch { }
        }

        // ── words ────────────────────────────────────────────────────────────────────────────

        // "UTC 2026-10-21 18:33" from Unix seconds.
        internal static string When(long unix) =>
            unix <= 0 ? "" : DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        // "dupe", "changedSince" ... as words a person reads.
        internal static string Why(string word)
        {
            switch (word)
            {
                case "gone": return "no longer in the log";
                case "changed": return "changed since it was shown";
                case "needsChoice": return "a field needs your choice";
                case "distinct": return "they are two different contacts";
                case "alreadyThere": return "already in the log";
                case "changedSince": return "changed again since";
                case "duplicate": return "the same contact was logged again since";
                case "unknownKind": return "made by a newer version";
                default: return word ?? "";
            }
        }

        // What a change's answer says, for the status line: "Changed 12 contacts." / its refusals.
        internal static string Said(Answer a, string done)
        {
            if (!a.Ok) return "Nothing was changed: " + (a.Why ?? a.State ?? "no answer");
            long n = Num(a.Root, "changed");
            var refused = List(a.Root, "refused").Select(r => r.EnumerateArray().Skip(1).FirstOrDefault().GetString()).ToList();
            string text = $"{done} {n} contact{(n == 1 ? "" : "s")}.";
            if (refused.Count > 0)
                text += $" {refused.Count} left as {(refused.Count == 1 ? "it was" : "they were")}: " +
                        string.Join(", ", refused.GroupBy(w => w).Select(g => $"{g.Count()} {Why(g.Key)}")) + ".";
            if (a.State == "unconfirmed") text += " Not yet confirmed on disk: " + a.Why;
            return text;
        }
    }
}
