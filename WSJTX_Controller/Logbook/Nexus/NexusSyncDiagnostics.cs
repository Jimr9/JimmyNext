using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WSJTX_Controller
{
    // Logbook sync diagnostics while Nexus keeps the log (read-only: never changes the log).
    //
    // Keeps the downloads exactly as received (LoTW reports, QRZ FETCH replies -- neither carries a
    // password or API key) and the list of confirmations a merge could not match to a logged
    // contact, in NexusLog\diagnostics, so a sync can be replayed on copies and compared. A few of
    // each kind are kept; older ones are removed.
    public static class NexusSyncDiagnostics
    {
        private const int KeepPerKind = 6;

        public static string Folder => Path.Combine(NexusLogbook.Folder, "diagnostics");

        // kind: lotw-qsl-yes | lotw-qsl-no | lotw-nexus-qsl-yes | qrz-fetch. Best-effort: a failure
        // here must never fail the sync itself. Returns the file written, or null.
        public static string Retain(string kind, string text, bool force = false)
        {
            if (text == null || (!force && !NexusLogbook.Active)) return null;
            try
            {
                Directory.CreateDirectory(Folder);
                string path = Path.Combine(Folder, $"{kind}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.txt");
                File.WriteAllText(path, text, new UTF8Encoding(false));
                foreach (var old in Directory.GetFiles(Folder, kind + "-2*.txt").OrderByDescending(f => f).Skip(KeepPerKind))
                    try { File.Delete(old); } catch { }
                return path;
            }
            catch { return null; }
        }

        // The newest kept file of a kind, or null.
        public static string Latest(string kind) =>
            Directory.Exists(Folder) ? Directory.GetFiles(Folder, kind + "-2*.txt").OrderByDescending(f => f).FirstOrDefault() : null;

        // One line per unmatched confirmation, as Nexus reported it. Returns the file written, or null.
        public static string WriteUnmatched(string source, IReadOnlyList<string> lines) =>
            WriteList("unmatched-" + source.ToLowerInvariant(), $"{source} confirmations that matched no logged contact", lines);

        public static string WriteList(string kind, string title, IReadOnlyList<string> lines)
        {
            if (lines == null || lines.Count == 0) return null;
            var sb = new StringBuilder();
            sb.AppendLine($"{title} ({lines.Count}), {DateTime.UtcNow:u}");
            foreach (var l in lines) sb.AppendLine(l);
            return Retain(kind, sb.ToString(), force: true);
        }
    }
}
