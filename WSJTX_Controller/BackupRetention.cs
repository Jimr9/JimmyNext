using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace WSJTX_Controller
{
    // Backups never pile up (operator, 2026-10-02): after each new backup, only the newest few of
    // that kind are kept. How many is "backupsToKeep" in Shared.ini (written there as 5 at start
    // when missing, so it can be changed by hand); 1 is the least. One-time move backups (the
    // logbook's move to Nexus, the move to shared settings) are never touched.
    internal static class BackupRetention
    {
        internal const string KeepKey = "backupsToKeep";
        internal const int DefaultKeep = 5;
        private static readonly Regex Stamp = new Regex(@"\d{8}-\d{6}");

        internal static int Keep => SharedIniNumbers.Read(KeepKey, DefaultKeep, 1, int.MaxValue);

        // At start: put the setting in Shared.ini, so it is there to edit.
        internal static void EnsureSetting() => SharedIniNumbers.Ensure(KeepKey, DefaultKeep);

        // Deletes all but the newest `keep` files or folders in `folder` matching `pattern`
        // (newest by the date-time stamp in the name, else by last write). Returns how many went.
        internal static int Prune(string folder, string pattern, int? keep = null)
        {
            int n = keep ?? Keep, removed = 0;
            try
            {
                if (!Directory.Exists(folder)) return 0;
                var all = Directory.GetFileSystemEntries(folder, pattern)
                    .Select(p => (Path: p, Key: StampOf(p)))
                    .OrderByDescending(x => x.Key, StringComparer.Ordinal)
                    .ToList();
                foreach (var old in all.Skip(n))
                {
                    try
                    {
                        if (Directory.Exists(old.Path)) Directory.Delete(old.Path, true);
                        else File.Delete(old.Path);
                        removed++;
                    }
                    catch { }
                }
            }
            catch { }
            return removed;
        }

        private static string StampOf(string path)
        {
            var m = Stamp.Match(Path.GetFileName(path));
            if (m.Success) return m.Value;
            try { return File.GetLastWriteTime(path).ToString("yyyyMMdd-HHmmss"); } catch { return ""; }
        }
    }
}
