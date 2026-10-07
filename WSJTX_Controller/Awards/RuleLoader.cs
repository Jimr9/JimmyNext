using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace WSJTX_Controller
{
    public class RuleLoadResult
    {
        public List<RuleDefinition> Definitions = new List<RuleDefinition>();
        public List<string>         Errors      = new List<string>();
    }

    // Scans the RuleDefinitions folder and loads every .ini file it finds. New
    // awards are added by dropping another file in the folder -- no code change.
    public static class RuleLoader
    {
        // Bump only if the [Award] section format itself changes incompatibly.
        public const int SupportedFormatVersion = 1;

        private static readonly Regex IdPattern = new Regex(@"^[A-Za-z0-9_-]+$");

        public static string RulesFolder =>
            Path.Combine(LookupManager.DataRoot, "RuleDefinitions");

        public static string ListsFolder =>
            Path.Combine(RulesFolder, "Lists");

        // The starter library shipped next to the exe -- the source of truth for
        // "Restore Default Awards" (RuleDefinitionManagerDlg) as well as first-run
        // seeding below. Never written to at runtime.
        public static string ShippedRulesFolder =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RuleDefinitions");

        public static string ShippedListsFolder =>
            Path.Combine(ShippedRulesFolder, "Lists");

        // Scans RulesFolder for *.ini, parses and validates each one. A single bad
        // file is skipped (recorded as an error) and never blocks the others or
        // Jimmy's startup.
        public static RuleLoadResult LoadAll()
        {
            var result = new RuleLoadResult();
            try
            {
                SeedIfMissing();
                if (!Directory.Exists(RulesFolder)) return result;

                var files = Directory.GetFiles(RulesFolder, "*.ini")
                                      .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
                var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var path in files)
                {
                    try
                    {
                        string error;
                        var def = ParseAndValidate(path, out error);
                        if (def == null)
                        {
                            result.Errors.Add($"{Path.GetFileName(path)}: {error}");
                            continue;
                        }
                        if (seenIds.Contains(def.Id))
                        {
                            result.Errors.Add(
                                $"{Path.GetFileName(path)}: duplicate Id '{def.Id}' (already loaded from an earlier file) -- skipped.");
                            continue;
                        }
                        seenIds.Add(def.Id);
                        result.Definitions.Add(def);
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add("RuleLoader.LoadAll failed: " + ex.Message);
            }

            if (result.Errors.Count > 0) LogErrors(result.Errors);
            return result;
        }

        // First run: seed the AppData folder from the starter library shipped
        // next to the exe (RuleDefinitions\, copied there at build time). Once the
        // AppData folder exists, it's the user's to manage -- never overwritten.
        private static void SeedIfMissing()
        {
            ReplaceStockSet(ShippedRulesFolder, RulesFolder, BackupsFolder);
            if (Directory.Exists(RulesFolder))
            {
                AddNewStockAwards();
                return;
            }

            Directory.CreateDirectory(RulesFolder);
            Directory.CreateDirectory(ListsFolder);
            if (Directory.Exists(ShippedRulesFolder)) CopyDirectory(ShippedRulesFolder, RulesFolder);
            AddNewStockAwards();
        }

        // 2026-09-28 (operator: "it is a stock award, it ships"): a stock award added in a later
        // release (e.g. WAS_30M) reaches an existing install too. Each shipped award file is
        // copied in once, if missing, and its name recorded -- so an award the operator later
        // deletes stays deleted. Missing Lists files (universe data, not a choice) are copied
        // whenever absent. Existing files are never overwritten.
        internal const string StockAwardsAddedFile = "stock-awards-added.txt";

        // ── Replacing the whole award set (2026-10-06) ──────────────────────────────────────
        // The shipped folder carries StockVersionFile. When the installed set's differs, the
        // installed folder is copied to Backups once, and the shipped set -- definitions and lists
        // -- takes its place whole: superseded awards disappear, and custom or edited ones are
        // not kept active (operator: nobody depends on them yet; the backup has them). Award
        // progress is computed from the logbook every time, so nothing else moves; checked
        // awards keep their place by Id. Interrupt-safe: the new set is built beside the old one
        // ("<folder>.new") and swapped in with two renames; the next start finishes or undoes a
        // swap that was cut short.
        internal const string StockVersionFile = "stock-awards-version.txt";

        private static string BackupsFolder => Path.Combine(Path.GetDirectoryName(LookupManager.DataRoot) ?? LookupManager.DataRoot, "Backups");

        internal static string VersionOf(string folder)
        {
            try
            {
                string f = Path.Combine(folder, StockVersionFile);
                return File.Exists(f) ? File.ReadAllText(f).Trim() : null;
            }
            catch { return null; }
        }

        // Returns the backup folder made, or null when nothing was replaced.
        internal static string ReplaceStockSet(string shippedFolder, string rulesFolder, string backupsRoot)
        {
            string newDir = rulesFolder + ".new", oldDir = rulesFolder + ".old";
            try
            {
                // Finish or undo a swap cut short.
                if (!Directory.Exists(rulesFolder) && Directory.Exists(oldDir))
                {
                    if (Directory.Exists(newDir) && VersionOf(newDir) != null) Directory.Move(newDir, rulesFolder);
                    else Directory.Move(oldDir, rulesFolder);
                }
                if (Directory.Exists(rulesFolder))
                {
                    if (Directory.Exists(newDir)) Directory.Delete(newDir, true);
                    if (Directory.Exists(oldDir)) Directory.Delete(oldDir, true);
                }

                string shippedVersion = VersionOf(shippedFolder);
                if (shippedVersion == null || !Directory.Exists(rulesFolder) || VersionOf(rulesFolder) == shippedVersion) return null;

                string backup = Path.Combine(backupsRoot, $"RuleDefinitions-before-awards-{Safe(shippedVersion)}-{DateTime.Now:yyyyMMdd-HHmmss}");
                CopyDirectory(rulesFolder, backup);

                CopyDirectory(shippedFolder, newDir);
                // Every shipped award counts as added, so AddNewStockAwards never re-adds one later.
                File.WriteAllLines(Path.Combine(newDir, StockAwardsAddedFile),
                    Directory.GetFiles(shippedFolder, "*.ini").Select(Path.GetFileName));
                Directory.Move(rulesFolder, oldDir);
                Directory.Move(newDir, rulesFolder);
                Directory.Delete(oldDir, true);
                BackupRetention.Prune(backupsRoot, "RuleDefinitions-before-awards-*");
                LogErrors(new List<string> { $"Award set replaced with version {shippedVersion}; the previous set is in {backup}." });
                return backup;
            }
            catch (Exception ex)
            {
                LogErrors(new List<string> { "Replacing the award set failed (the previous set stays in use): " + ex.Message });
                return null;
            }
        }

        private static string Safe(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_').ToArray());

        internal static void AddNewStockAwards() => AddNewStockAwards(ShippedRulesFolder, RulesFolder);

        internal static void AddNewStockAwards(string shippedFolder, string rulesFolder)
        {
            try
            {
                if (!Directory.Exists(shippedFolder)) return;
                string shippedLists = Path.Combine(shippedFolder, "Lists"), lists = Path.Combine(rulesFolder, "Lists");
                string recordPath = Path.Combine(rulesFolder, StockAwardsAddedFile);
                var added = new HashSet<string>(
                    File.Exists(recordPath) ? File.ReadAllLines(recordPath).Where(l => l.Trim().Length > 0).Select(l => l.Trim()) : Enumerable.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);
                var newlyRecorded = new List<string>();
                foreach (var shipped in Directory.GetFiles(shippedFolder, "*.ini"))
                {
                    string name = Path.GetFileName(shipped);
                    if (added.Contains(name)) continue;
                    string target = Path.Combine(rulesFolder, name);
                    if (!File.Exists(target)) File.Copy(shipped, target, overwrite: false);
                    newlyRecorded.Add(name);
                }
                if (Directory.Exists(shippedLists))
                {
                    Directory.CreateDirectory(lists);
                    foreach (var list in Directory.GetFiles(shippedLists))
                    {
                        string target = Path.Combine(lists, Path.GetFileName(list));
                        if (!File.Exists(target)) File.Copy(list, target, overwrite: false);
                    }
                }
                if (newlyRecorded.Count > 0) File.AppendAllLines(recordPath, newlyRecorded);
            }
            catch (Exception ex)
            {
                LogErrors(new List<string> { "Adding new stock awards failed: " + ex.Message });
            }
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.GetFiles(sourceDir))
                File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: false);
            foreach (var dir in Directory.GetDirectories(sourceDir))
                CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }

        // Internal (not private): reused by RuleDefinitionManagerDlg's "Restore Default
        // Awards" to parse the shipped folder's files with the exact same validation as
        // the live loader, rather than a second hand-rolled parser.
        internal static RuleDefinition ParseAndValidate(string path, out string error)
        {
            error = null;
            RuleFile file;
            try { file = RuleFile.Load(path); }
            catch (Exception ex) { error = "Could not read file: " + ex.Message; return null; }

            int formatVersion;
            if (!int.TryParse(file.Get("Award", "FormatVersion"), out formatVersion))
            {
                error = "[Award] FormatVersion is missing or not a number.";
                return null;
            }
            if (formatVersion != SupportedFormatVersion)
            {
                error = $"[Award] FormatVersion={formatVersion} is not supported by this version of Jimmy " +
                        $"(expected {SupportedFormatVersion}).";
                return null;
            }

            string id = file.Get("Award", "Id");
            if (string.IsNullOrWhiteSpace(id) || !IdPattern.IsMatch(id))
            {
                error = "[Award] Id is missing or contains characters other than letters, digits, '_', '-'.";
                return null;
            }

            string name = file.Get("Award", "Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "[Award] Name is required.";
                return null;
            }

            string enabledStr = file.Get("Award", "Enabled", "Y");
            bool enabled = !enabledStr.Equals("N", StringComparison.OrdinalIgnoreCase) &&
                           !enabledStr.Equals("No", StringComparison.OrdinalIgnoreCase) &&
                           !enabledStr.Equals("False", StringComparison.OrdinalIgnoreCase);

            string groupByStr = file.Get("Match", "GroupBy");
            RuleGroupBy groupBy;
            if (string.IsNullOrWhiteSpace(groupByStr) || !Enum.TryParse(groupByStr, true, out groupBy))
            {
                error = $"[Match] GroupBy is missing or not a recognized kind ('{groupByStr}'). " +
                        $"Supported: {string.Join(", ", Enum.GetNames(typeof(RuleGroupBy)))}.";
                return null;
            }

            string confirmStr = file.Get("Confirmation", "Requires", "ANY");
            RuleConfirmation confirmation;
            var confirmationSources = new List<string>();
            // D1 (2026-09-28): a list of channels (e.g. "LOTW,CARD"), or one of the channels the
            // five original words have no single word for (EQSL, CARD), means Sources -- any one
            // of them confirms. The five original words keep exactly their meaning.
            var tokens = SplitList(confirmStr).Select(t => t.ToUpperInvariant()).Distinct().ToList();
            bool isList = tokens.Count > 1 ||
                          (tokens.Count == 1 && (tokens[0] == RuleConfirmationSources.Eqsl || tokens[0] == RuleConfirmationSources.EqslAg
                                                 || tokens[0] == RuleConfirmationSources.Card));
            if (isList)
            {
                var unknown = tokens.Where(t => !RuleConfirmationSources.All.Contains(t)).ToList();
                if (unknown.Count > 0)
                {
                    error = $"[Confirmation] Requires='{confirmStr}': '{string.Join(",", unknown)}' is not a confirmation source. " +
                            "Supported sources: LOTW, QRZ, EQSL, EQSL_AG, CARD.";
                    return null;
                }
                confirmation = RuleConfirmation.Sources;
                confirmationSources = tokens;
            }
            else if (!Enum.TryParse(confirmStr, true, out confirmation) || confirmation == RuleConfirmation.Sources)
            {
                error = $"[Confirmation] Requires='{confirmStr}' is not recognized. Supported: ANY, LOTW, QRZ, BOTH, NONE, " +
                        "or a list of sources from LOTW, QRZ, EQSL, EQSL_AG, CARD (e.g. LOTW,CARD).";
                return null;
            }

            string targetTypeStr = file.Get("Target", "Type");
            RuleTargetType targetType;
            if (string.IsNullOrWhiteSpace(targetTypeStr) || !Enum.TryParse(targetTypeStr, true, out targetType))
            {
                error = $"[Target] Type is missing or not recognized ('{targetTypeStr}'). Supported: ALL, COUNT, LEVELS.";
                return null;
            }

            RuleBasis basis;
            string basisStr = file.Get("Target", "Basis", "WORKED");
            if (!Enum.TryParse(basisStr, true, out basis))
            {
                error = $"[Target] Basis='{basisStr}' is not recognized. Supported: WORKED, CONFIRMED.";
                return null;
            }
            if (basis == RuleBasis.Confirmed && confirmation == RuleConfirmation.None)
            {
                error = "[Target] Basis=CONFIRMED needs [Confirmation] Requires= to name the accepted sources.";
                return null;
            }
            foreach (var key in new[] { "DateFrom", "DateTo" })
            {
                string v = file.Get("Match", key);
                if (!string.IsNullOrWhiteSpace(v) && RuleEngine.CompactDateTime(v, key == "DateTo") == null)
                {
                    error = $"[Match] {key}='{v}' is not a date (yyyy-MM-dd) or a UTC date and time (yyyy-MM-dd HHmm).";
                    return null;
                }
            }

            int threshold = 0;
            string thresholdFrom = file.Get("Target", "ThresholdFrom");
            int thresholdOffset = 0;
            var levels = new List<RuleLevel>();

            if (targetType == RuleTargetType.Count)
            {
                if (!string.IsNullOrWhiteSpace(thresholdFrom))
                {
                    if (!int.TryParse(file.Get("Target", "ThresholdOffset", "0"), out thresholdOffset) || thresholdOffset < 0)
                    {
                        error = "[Target] ThresholdOffset must be a non-negative integer when ThresholdFrom is set.";
                        return null;
                    }
                    // Threshold itself is resolved dynamically at evaluation time (see
                    // RuleEngine.EvaluateCore) -- a literal [Target] Threshold= is not required.
                }
                else if (!int.TryParse(file.Get("Target", "Threshold"), out threshold) || threshold <= 0)
                {
                    error = "[Target] Threshold is required and must be a positive integer when Type=COUNT (unless ThresholdFrom is set).";
                    return null;
                }
            }
            else if (targetType == RuleTargetType.Levels)
            {
                foreach (var kv in file.GetSection("Levels"))
                {
                    int t;
                    if (!int.TryParse(kv.Value, out t) || t <= 0)
                    {
                        error = $"[Levels] {kv.Key}={kv.Value} is not a positive integer.";
                        return null;
                    }
                    levels.Add(new RuleLevel { Name = kv.Key, Threshold = t });
                }
                if (levels.Count == 0)
                {
                    error = "[Levels] section must contain at least one Name=Threshold entry when Target.Type=LEVELS.";
                    return null;
                }
                levels = levels.OrderBy(l => l.Threshold).ToList();
            }
            else if (targetType == RuleTargetType.All)
            {
                if (string.IsNullOrWhiteSpace(file.Get("Match", "Universe")))
                {
                    error = "[Match] Universe is required when Target.Type=ALL.";
                    return null;
                }
            }

            var def = new RuleDefinition
            {
                Id              = id,
                Name            = name,
                Sponsor         = file.Get("Award", "Sponsor", ""),
                Category        = file.Get("Award", "Category", ""),
                FormatVersion   = formatVersion,
                Enabled         = enabled,
                Description     = file.Get("Award", "Description", ""),
                Website         = file.Get("Award", "Website", ""),
                GroupBy         = groupBy,
                Universe        = file.Get("Match", "Universe"),
                LimitTo         = file.Get("Match", "LimitTo"),
                Bands           = SplitList(file.Get("Match", "Bands")),
                Modes           = SplitList(file.Get("Match", "Modes")),
                CallsignPattern = file.Get("Match", "CallsignPattern"),
                Sig             = file.Get("Match", "Sig"),
                DateFrom        = file.Get("Match", "DateFrom"),
                DateTo          = file.Get("Match", "DateTo"),
                ExcludeBands    = SplitList(file.Get("Match", "ExcludeBands")),
                DxccIn          = SplitList(file.Get("Match", "DxccIn")),
                ExcludeCallsigns = SplitList(file.Get("Match", "ExcludeCallsigns")),
                DcCountsAsMaryland = IsYes(file.Get("Match", "DcCountsAsMaryland", "N")),
                ManualChecks    = file.Get("Award", "ManualChecks", ""),
                Confirmation    = confirmation,
                ConfirmationSources = confirmationSources,
                Target          = targetType,
                Basis           = basis,
                Threshold       = threshold,
                ThresholdFrom   = thresholdFrom,
                ThresholdOffset = thresholdOffset,
                Levels          = levels,
                SourceFile      = path,
            };

            var endBand = SplitList(file.Get("Endorsements", "Band"));
            var endMode = SplitList(file.Get("Endorsements", "Mode"));
            if (endBand.Count > 0 || endMode.Count > 0)
                def.Endorsements = new RuleEndorsements { Bands = endBand, Modes = endMode };

            return def;
        }

        private static bool IsYes(string s) =>
            s.Equals("Y", StringComparison.OrdinalIgnoreCase) || s.Equals("Yes", StringComparison.OrdinalIgnoreCase)
            || s.Equals("True", StringComparison.OrdinalIgnoreCase);

        private static List<string> SplitList(string s) =>
            string.IsNullOrWhiteSpace(s)
                ? new List<string>()
                : s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        // Appends to a dedicated log file (never the WSJT-X diagnostic log -- that
        // file is held open with FileShare.Read while diagnostic logging is on,
        // which makes a second writer fail silently).
        private static void LogErrors(List<string> errors)
        {
            try
            {
                // Replay-test isolation, 2026-08-23: only reachable on an actual rule-definition
                // load error (not normal operation), but redirected under TestModeGuard.IsTestMode
                // for the same reason as Controller.cs's own settings-.ini redirect -- a test
                // session must never write into the real AppData folder, error path included.
                string dir = TestModeGuard.IsTestMode
                    ? Path.Combine(Path.GetTempPath(), "JimmyReplayTest_AppData")
                    : Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        Assembly.GetExecutingAssembly().GetName().Name);
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "log_rules_errors.txt");

                var sb = new System.Text.StringBuilder();
                sb.AppendLine();
                sb.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Rule Definitions load errors:");
                foreach (var e in errors) sb.AppendLine("  " + e);

                File.AppendAllText(file, sb.ToString());
            }
            catch
            {
                // Logging must never break startup.
            }
        }
    }
}
