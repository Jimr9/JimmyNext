using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WSJTX_Controller
{
    // Nexus modernization Stage 5 (2026-09-08): TEMPORARY developer-only diagnostic that
    // proves (or disproves) the Nexus-derived FT8/FT4 semantic facts agree with Jimmy's own
    // WsjtxMessage parser BEFORE any consumer switches to them. Modelled on
    // Classification/ClassificationParityLogger.
    //
    // For each Direct decode it is handed a SemanticDecode built the OLD way
    // (WsjtxMessage.*) and one built the NEW way (Nexus's parse via the Stage 3 DecodeRow
    // flags + the Stage 4 decodeSemantics envelope), compares them field by field, and
    // appends a detailed entry ONLY when they disagree. Nothing is logged for a match, so an
    // empty log after a real operating session (or a full replay run) is itself the evidence
    // Stage 5 needs.
    //
    // Disabled by default; enabled only via an undocumented .ini key
    // (logSemanticParityMismatches=True), never exposed in OptionsDlg -- see Controller.cs's
    // ini-only-settings block. Independent of SemanticCutover.UseNexusSemantics (both
    // SemanticDecode instances are always computed regardless).
    //
    // Remove this file and its call site (WsjtxClient.Direct.cs's DirectApplyDecodes) once
    // the migration is field-proven and the diagnostic is no longer needed (Stage 11/12).
    internal static class SemanticParityLogger
    {
        public static bool Enabled = false;

        private static readonly object _lock = new object();
        private static StreamWriter _writer;

        // In-memory tally so a test harness (or a support report) can assert "N compared,
        // M disagreed" without reading the file. Reset() clears it.
        public static int Compared;
        public static int Disagreed;
        public static readonly Dictionary<string, int> DisagreementsByField = new Dictionary<string, int>();

        private static string LogPath => TestModeGuard.IsTestMode
            ? Path.Combine(Path.GetTempPath(), "JimmyReplayTest_AppData", "SemanticParityMismatches.log")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                System.Reflection.Assembly.GetExecutingAssembly().GetName().Name, "SemanticParityMismatches.log");

        public static void Reset()
        {
            lock (_lock)
            {
                Compared = 0;
                Disagreed = 0;
                DisagreementsByField.Clear();
            }
        }

        private static void Note(string field)
        {
            DisagreementsByField.TryGetValue(field, out int n);
            DisagreementsByField[field] = n + 1;
        }

        // Compares the two views of one decode. `rawMessage` is the engine text (before
        // Jimmy's NormalizeDecodedMessage); `normMessage` is what the WsjtxMessage side was
        // actually parsed from. `band` / `myCall` are context for the log only.
        public static void CheckAndLog(SemanticDecode wsjtx, SemanticDecode nexus,
            string rawMessage, string normMessage, string band, string myCall)
        {
            if (wsjtx == null || nexus == null) return;

            var diffs = new List<(string field, object oldVal, object newVal)>();
            void Cmp(string field, object a, object b)
            {
                bool eq = (a == null && b == null) ||
                          (a != null && b != null && string.Equals(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase));
                if (!eq) diffs.Add((field, a, b));
            }

            Cmp("From", wsjtx.From, nexus.From);
            Cmp("To", wsjtx.To, nexus.To);
            Cmp("IsCq", wsjtx.IsCq, nexus.IsCq);
            Cmp("IsDirectedCq", wsjtx.IsDirectedCq, nexus.IsDirectedCq);
            Cmp("CqTarget", wsjtx.CqTarget, nexus.CqTarget);
            Cmp("AddressedToMe", wsjtx.AddressedToMe, nexus.AddressedToMe);
            Cmp("Grid", wsjtx.Grid, nexus.Grid);
            Cmp("ReportDb", wsjtx.ReportDb, nexus.ReportDb);
            Cmp("IsReport", wsjtx.IsReport, nexus.IsReport);
            Cmp("IsRReport", wsjtx.IsRReport, nexus.IsRReport);
            Cmp("IsRrr", wsjtx.IsRrr, nexus.IsRrr);
            Cmp("IsRr73", wsjtx.IsRr73, nexus.IsRr73);
            Cmp("Is73", wsjtx.Is73, nexus.Is73);
            Cmp("Kind", wsjtx.Kind, nexus.Kind);
            Cmp("CallForm", wsjtx.CallForm, nexus.CallForm);

            lock (_lock)
            {
                Compared++;
                if (diffs.Count == 0) return;
                Disagreed++;
                foreach (var d in diffs) Note(d.field);

                if (!Enabled) return;
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"==== Semantic parity mismatch ==== {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
                    sb.AppendLine($"raw:  \"{rawMessage}\"");
                    if (!string.Equals(rawMessage, normMessage, StringComparison.Ordinal))
                        sb.AppendLine($"norm: \"{normMessage}\"  (WsjtxMessage parsed this)");
                    sb.AppendLine($"band: {band ?? "(unknown)"}   myCall: {myCall ?? "(unknown)"}");
                    sb.AppendLine("  Field                WsjtxMessage         Nexus");
                    foreach (var (field, oldVal, newVal) in diffs)
                        sb.AppendLine($"  {field,-20} {Fmt(oldVal),-20} {Fmt(newVal),-20}  <-- DIFFERS");
                    sb.AppendLine();

                    if (_writer == null)
                    {
                        string dir = Path.GetDirectoryName(LogPath);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        _writer = new StreamWriter(LogPath, append: true) { AutoFlush = true };
                    }
                    _writer.Write(sb.ToString());
                }
                catch
                {
                    // Diagnostic-only: a logging failure must never affect real operation.
                }
            }
        }

        private static string Fmt(object o) => o == null ? "(null)" : o.ToString();

        public static void Close()
        {
            lock (_lock)
            {
                _writer?.Flush();
                _writer?.Close();
                _writer = null;
            }
        }
    }
}
