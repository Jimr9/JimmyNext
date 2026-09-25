using System;
using System.IO;

namespace WSJTX_Controller
{
    // Nexus contesting foundation: per-contest SAVED CONFIGURATION (the operator's usual/last-
    // used entry values -- class, section, categories, run style) for each Nexus event_id.
    // Deliberately NOT part of Jimmy's main Options .ini: this is loaded lazily (only when the
    // Contesting tab or contest workflow actually needs it) and saved only when contest
    // configuration actually changes, never during an unrelated Options save.
    //
    // Storage: one companion .ini file, living beside whichever ini file actually governs the
    // running session -- the base file OR the active named profile's file (Controller's own
    // ActiveIniFilePath() resolution, never re-derived independently here) -- named after it
    // with a ".Contests" suffix before the extension:
    //   <AppData>\<pgmName>\<pgmName>.ini            -> <pgmName>.Contests.ini            (default profile)
    //   <AppData>\<pgmName>\Profiles\<name>.ini       -> Profiles\<name>.Contests.ini       (named profile)
    // One section per Nexus event_id ([arrlfd], [wfd], ...); unknown sections/keys (e.g. a
    // future Jimmy version's own additions, or a contest this build doesn't automate)
    // survive untouched -- IniFile's own batched-rewrite mechanism already guarantees this
    // file-wide, not just per-key (see IniFile.cs's own header comment).
    //
    // Does NOT store live score, completed contacts, or any Nexus recovery state -- those stay
    // in the logbook (LogbookDb/ILogbookService) and EngineHost/Nexus's own recovery journal
    // (contest_bridge.rs's sidecar + Nexus's own FD journal) respectively. This file only ever
    // holds what the operator would otherwise have to retype.
    public class ContestEntryDefaults
    {
        public string Class = "";
        public string Section = "";
        public string RunMode = "sp";
        public string CategoryOperator = "";
        public string CategoryPower = "";
        public string CategoryAssisted = "";
        public string CategoryStation = "";
    }

    public static class ContestConfigStore
    {
        private const string FileSuffix = ".Contests";

        // Derives the companion path from whatever ini currently governs the session -- never
        // resolved independently of Controller's own profile logic, so this can never drift
        // from which profile is actually active (base vs. a named profile), and needs no
        // separate handling when the operator switches profiles: the NEXT read/write after a
        // profile switch (which requires a restart, per Controller's own Load Profile design)
        // simply resolves against the new active ini automatically.
        public static string ActiveContestIniPath()
        {
            string activeIni = Controller.ActiveIniFilePath();
            return CompanionPathFor(activeIni);
        }

        // internal (not private): profile Save-As/Delete call this against an ARBITRARY named
        // profile's own .ini path (not necessarily the currently active one), and JimmyTests
        // exercises it directly.
        internal static string CompanionPathFor(string iniPath)
        {
            if (string.IsNullOrEmpty(iniPath)) return null;
            string dir = Path.GetDirectoryName(iniPath) ?? "";
            string stem = Path.GetFileNameWithoutExtension(iniPath);
            string ext = Path.GetExtension(iniPath);
            return Path.Combine(dir, stem + FileSuffix + ext);
        }

        // Loads the saved entry defaults for one contest, from whichever profile is currently
        // active. Returns a blank ContestEntryDefaults (never null, never throws) when the file
        // or section doesn't exist yet -- the common case for a contest never entered before.
        public static ContestEntryDefaults Load(string eventId)
        {
            var result = new ContestEntryDefaults();
            if (string.IsNullOrWhiteSpace(eventId)) return result;
            string path = ActiveContestIniPath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return result;
            try
            {
                var ini = new IniFile(path);
                if (!ini.KeyExists("class", eventId) && !ini.KeyExists("section", eventId)) return result;
                result.Class = ini.Read("class", eventId);
                result.Section = ini.Read("section", eventId);
                string runMode = ini.Read("runMode", eventId);
                if (!string.IsNullOrWhiteSpace(runMode)) result.RunMode = runMode;
                result.CategoryOperator = ini.Read("categoryOperator", eventId);
                result.CategoryPower = ini.Read("categoryPower", eventId);
                result.CategoryAssisted = ini.Read("categoryAssisted", eventId);
                result.CategoryStation = ini.Read("categoryStation", eventId);
            }
            catch
            {
                // Best-effort only -- a corrupt/unreadable companion file must never block
                // entering a contest; the operator just retypes the fields this once.
            }
            return result;
        }

        // Saves the entry defaults for one contest -- called only at the moment contest
        // configuration actually changes (a successful CONTEST_ENTER), never on a routine
        // Options save. Uses the same atomic BeginBatchScope commit every other settings save
        // in this app already uses (IniFile.cs), so a save that's interrupted mid-write can
        // never corrupt the file or lose an unrelated contest's own section.
        public static void Save(string eventId, ContestEntryDefaults defaults)
        {
            if (string.IsNullOrWhiteSpace(eventId) || defaults == null) return;
            string path = ActiveContestIniPath();
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var ini = new IniFile(path);
                using (var batch = ini.BeginBatchScope())
                {
                    ini.Write("class", defaults.Class ?? "", eventId);
                    ini.Write("section", defaults.Section ?? "", eventId);
                    ini.Write("runMode", defaults.RunMode ?? "sp", eventId);
                    ini.Write("categoryOperator", defaults.CategoryOperator ?? "", eventId);
                    ini.Write("categoryPower", defaults.CategoryPower ?? "", eventId);
                    ini.Write("categoryAssisted", defaults.CategoryAssisted ?? "", eventId);
                    ini.Write("categoryStation", defaults.CategoryStation ?? "", eventId);
                    batch.Commit();
                }
            }
            catch
            {
                // Best-effort only -- see Load's own comment. Failing to remember a default is
                // never worse than failing to log or score a contact, so this never surfaces as
                // a blocking error to the operator.
            }
        }
    }
}
