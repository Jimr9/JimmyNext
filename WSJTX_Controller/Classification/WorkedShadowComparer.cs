using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WSJTX_Controller
{
    // Nexus's worked-before facts for one decode, as the engine put them on its DecodeRow.
    internal sealed class NexusWorkedFlags
    {
        public bool Worked, WorkedBand, NewDxcc, NewBand;
        public string Country;
    }

    // Worked-before SHADOW comparison while Nexus keeps the logbook: for each decode, Jimmy's
    // own classification (ClassificationEngine over the read copy -- what every decision still
    // uses) beside the facts Nexus already puts on the decode from the same log:
    //   NewCall        Jimmy IsNewCallAnyBand     vs  Nexus !worked
    //   NewCallOnBand  Jimmy IsNewCallOnBand      vs  Nexus !worked_band
    //   NewCountry     Jimmy IsNewCountry         vs  Nexus new_dxcc            (both resolved a country)
    //   NewCountryBand Jimmy IsNewCountryOnBand   vs  Nexus new_dxcc || new_band (both resolved a country)
    // It records only; nothing reads it to decide anything. Running totals, and each distinct
    // disagreement (call + band + fact) written once per session with both sides' country, to
    // NexusLog\diagnostics\worked-shadow-<date>.txt; a summary line every 500 decodes and on exit.
    // Whether Jimmy switches to Nexus's facts is the operator's decision on this evidence.
    internal static class WorkedShadowComparer
    {
        private static readonly object _lock = new object();
        public static int Compared;
        public static readonly Dictionary<string, (int Agree, int Disagree)> ByFact = new Dictionary<string, (int, int)>();
        private static readonly HashSet<string> _written = new HashSet<string>();
        internal static string TestPathOverride;

        private static string LogPath => TestPathOverride ??
            Path.Combine(NexusSyncDiagnostics.Folder, $"worked-shadow-{DateTime.UtcNow:yyyyMMdd}.txt");

        public static void Reset()
        {
            lock (_lock) { Compared = 0; ByFact.Clear(); _written.Clear(); }
        }

        public static void Compare(ClassifiedCall jimmy, NexusWorkedFlags nexus, string call, string band)
        {
            if (jimmy == null || nexus == null || string.IsNullOrEmpty(call)) return;
            if (!NexusLogbook.Active || !NexusLogbook.LogReady) return; // Nexus's facts come from ITS log
            bool bandKnown = !string.IsNullOrEmpty(band);
            bool countries = !string.IsNullOrEmpty(jimmy.Country) && !string.IsNullOrEmpty(nexus.Country);
            var lines = new List<string>();
            lock (_lock)
            {
                Compared++;
                void Fact(string name, bool j, bool n)
                {
                    ByFact.TryGetValue(name, out var t);
                    if (j == n) { ByFact[name] = (t.Agree + 1, t.Disagree); return; }
                    ByFact[name] = (t.Agree, t.Disagree + 1);
                    string key = $"{call.ToUpperInvariant()}|{band}|{name}";
                    if (_written.Add(key))
                        lines.Add($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z {call} {band ?? "?"} {name}: Jimmy {(j ? "new" : "not new")}, Nexus {(n ? "new" : "not new")}" +
                                  $"  (country Jimmy '{jimmy.Country}', Nexus '{nexus.Country}')");
                }
                Fact("NewCall", jimmy.IsNewCallAnyBand, !nexus.Worked);
                if (bandKnown) Fact("NewCallOnBand", jimmy.IsNewCallOnBand, !nexus.WorkedBand);
                if (countries)
                {
                    Fact("NewCountry", jimmy.IsNewCountry, nexus.NewDxcc);
                    if (bandKnown) Fact("NewCountryOnBand", jimmy.IsNewCountryOnBand, nexus.NewDxcc || nexus.NewBand);
                }
                if (Compared % 500 == 0) lines.Add(SummaryLine());
            }
            Write(lines);
        }

        public static string SummaryLine()
        {
            lock (_lock)
                return $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z SUMMARY {Compared} decodes: " +
                       string.Join(", ", ByFact.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value.Agree} agree / {k.Value.Disagree} differ"));
        }

        // On exit: the session's totals.
        public static void Close()
        {
            if (Compared > 0) Write(new List<string> { SummaryLine() });
        }

        private static void Write(List<string> lines)
        {
            if (lines.Count == 0) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllLines(LogPath, lines, new UTF8Encoding(false));
            }
            catch { /* diagnostic only: never affects operation */ }
        }
    }
}
