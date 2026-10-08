using System.IO;
using System.Linq;
using System.Text;

namespace WSJTX_Controller
{
    // Serializes a RuleDefinition back to the .ini format RuleLoader reads.
    // Used by the Rule Definition Manager/Editor -- round-trips through the
    // same [Award]/[Match]/[Confirmation]/[Target]/[Levels]/[Endorsements]
    // sections RuleLoader.ParseAndValidate expects, so a saved file loads back
    // identically. Always fully rewrites the file -- comments or formatting a
    // user hand-edited into a file are not preserved once it's saved from here.
    public static class RuleWriter
    {
        public static void Save(RuleDefinition def, string path)
        {
            var sb = new StringBuilder();

            sb.AppendLine("[Award]");
            sb.AppendLine($"Id={def.Id}");
            sb.AppendLine($"Name={def.Name}");
            if (!string.IsNullOrWhiteSpace(def.ShortName)) sb.AppendLine($"ShortName={def.ShortName.Trim()}");
            if (!string.IsNullOrWhiteSpace(def.StatusName)) sb.AppendLine($"StatusName={def.StatusName.Trim()}");
            if (!def.ShowInStatus) sb.AppendLine("ShowInStatus=N");
            sb.AppendLine($"Sponsor={def.Sponsor}");
            sb.AppendLine($"Category={def.Category}");
            sb.AppendLine($"FormatVersion={(def.FormatVersion > 0 ? def.FormatVersion : RuleLoader.SupportedFormatVersion)}");
            sb.AppendLine($"Enabled={(def.Enabled ? "Y" : "N")}");
            sb.AppendLine($"Description={def.Description}");
            if (!string.IsNullOrEmpty(def.Website)) sb.AppendLine($"Website={def.Website}");
            if (!string.IsNullOrEmpty(def.ManualChecks)) sb.AppendLine($"ManualChecks={def.ManualChecks}");
            sb.AppendLine();

            sb.AppendLine("[Match]");
            sb.AppendLine($"GroupBy={def.GroupBy}");
            if (!string.IsNullOrWhiteSpace(def.Universe)) sb.AppendLine($"Universe={def.Universe}");
            if (!string.IsNullOrWhiteSpace(def.LimitTo)) sb.AppendLine($"LimitTo={def.LimitTo}");
            if (def.Bands.Count > 0) sb.AppendLine($"Bands={string.Join(",", def.Bands)}");
            if (def.Modes.Count > 0) sb.AppendLine($"Modes={string.Join(",", def.Modes)}");
            if (!string.IsNullOrWhiteSpace(def.CallsignPattern)) sb.AppendLine($"CallsignPattern={def.CallsignPattern}");
            if (!string.IsNullOrWhiteSpace(def.Sig)) sb.AppendLine($"Sig={def.Sig}");
            if (!string.IsNullOrWhiteSpace(def.DateFrom)) sb.AppendLine($"DateFrom={def.DateFrom}");
            if (!string.IsNullOrWhiteSpace(def.DateTo)) sb.AppendLine($"DateTo={def.DateTo}");
            if (def.ExcludeBands.Count > 0) sb.AppendLine($"ExcludeBands={string.Join(",", def.ExcludeBands)}");
            if (def.DxccIn.Count > 0) sb.AppendLine($"DxccIn={string.Join(",", def.DxccIn)}");
            if (def.ExcludeCallsigns.Count > 0) sb.AppendLine($"ExcludeCallsigns={string.Join(",", def.ExcludeCallsigns)}");
            if (def.DcCountsAsMaryland) sb.AppendLine("DcCountsAsMaryland=Y");
            sb.AppendLine();

            sb.AppendLine("[Confirmation]");
            sb.AppendLine(def.Confirmation == RuleConfirmation.Sources
                ? $"Requires={string.Join(",", def.ConfirmationSources)}"
                : $"Requires={def.Confirmation}");
            sb.AppendLine();

            sb.AppendLine("[Target]");
            sb.AppendLine($"Type={def.Target}");
            // Basis defaults to WORKED; omitted then, to keep an ordinary award's file plain.
            if (def.Basis != RuleBasis.Worked)
                sb.AppendLine($"Basis={def.Basis}");
            if (def.Target == RuleTargetType.Count)
            {
                // ThresholdFrom (e.g. Honor Roll's "DXCC_CURRENT minus 9") takes over from a
                // literal Threshold=; writing both would leave a stale/unused Threshold in the
                // file, and writing neither -- as this used to, before ThresholdFrom existed --
                // would silently reset a dynamic-threshold award to Threshold=0 (instantly
                // "complete") the first time anything re-saved it, e.g. toggling Enabled in the
                // Rule Definition Manager.
                if (!string.IsNullOrWhiteSpace(def.ThresholdFrom))
                {
                    sb.AppendLine($"ThresholdFrom={def.ThresholdFrom}");
                    sb.AppendLine($"ThresholdOffset={def.ThresholdOffset}");
                }
                else
                {
                    sb.AppendLine($"Threshold={def.Threshold}");
                }
            }
            sb.AppendLine();

            if (def.Target == RuleTargetType.Levels && def.Levels.Count > 0)
            {
                sb.AppendLine("[Levels]");
                foreach (var level in def.Levels.OrderBy(l => l.Threshold))
                    sb.AppendLine($"{level.Name}={level.Threshold}");
                sb.AppendLine();
            }

            if (def.Endorsements != null && (def.Endorsements.Bands.Count > 0 || def.Endorsements.Modes.Count > 0))
            {
                sb.AppendLine("[Endorsements]");
                if (def.Endorsements.Bands.Count > 0) sb.AppendLine($"Band={string.Join(",", def.Endorsements.Bands)}");
                if (def.Endorsements.Modes.Count > 0) sb.AppendLine($"Mode={string.Join(",", def.Endorsements.Modes)}");
                sb.AppendLine();
            }

            File.WriteAllText(path, sb.ToString());
        }
    }
}
