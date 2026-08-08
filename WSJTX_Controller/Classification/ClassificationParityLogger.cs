using System;
using System.IO;
using System.Text;
using WsjtxUdpLib.Messages.Out;

namespace WSJTX_Controller
{
    // TEMPORARY developer-only diagnostic for confirming Stage A6 (queue/ranking/
    // display/award cutover to ClassificationEngine) is field-proven against real
    // WSJT-X traffic, not just the deterministic unit-test parity already proven in
    // JimmyTests.cs. Compares dmsg's own WSJT-X wire-supplied classification fields
    // (untouched by Stage A6) against dmsg.Classified (ClassificationEngine's computed
    // output for the same decode) and appends a detailed entry ONLY when they disagree
    // on at least one field -- nothing is logged for a match, so an empty log after a
    // real operating session is itself the evidence this stage needs.
    //
    // Disabled by default; enabled only via an undocumented .ini key
    // (logClassificationParityMismatches=True), never exposed in OptionsDlg or any
    // other user-facing surface -- see Controller.cs's ini-only-settings block.
    // Independent of ClassificationCutover.UseClassificationEngine (dmsg.Classified is
    // always computed regardless of that flag's state, so this works whether the
    // cutover itself is active or rolled back).
    //
    // Remove this file and its one call site (WsjtxClient.cs's ProcessDecodeMsg) once
    // A6 is confirmed field-proven and this diagnostic is no longer needed.
    public static class ClassificationParityLogger
    {
        public static bool Enabled = false;

        private static readonly object _lock = new object();
        private static StreamWriter _writer;

        // Derived from the running assembly's own name (not a literal "Jimmy") so a
        // differently-named build (e.g. a side-by-side "Jimmy Test" release) writes to its
        // own data folder, consistent with LookupManager.DataRoot.
        private static string LogPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                System.Reflection.Assembly.GetExecutingAssembly().GetName().Name, "ClassificationParityMismatches.log");

        public static void CheckAndLog(EnqueueDecodeMessage dmsg, string call, string band,
            string myGrid, string myContinent, LookupManager lookupManager)
        {
            if (!Enabled || dmsg?.Classified == null) return;

            ClassifiedCall computed = dmsg.Classified;

            bool isDiff =
                dmsg.IsNewCallOnBand != computed.IsNewCallOnBand ||
                dmsg.IsNewCallAnyBand != computed.IsNewCallAnyBand ||
                dmsg.IsNewCountry != computed.IsNewCountry ||
                dmsg.IsNewCountryOnBand != computed.IsNewCountryOnBand ||
                dmsg.Country != computed.Country ||
                dmsg.Continent != computed.Continent ||
                dmsg.IsDx != computed.IsDx ||
                dmsg.Azimuth != computed.Azimuth ||
                dmsg.Distance != computed.Distance;

            if (!isDiff) return;

            try
            {
                string msgGrid = WsjtxMessage.Grid(dmsg.Message);
                string gridSource = !string.IsNullOrEmpty(msgGrid) ? $"message-embedded ({msgGrid})" : "none in message";

                string lookupSources = "LookupManager unavailable/disabled";
                int dxcc = 0;
                string qrzCachedGrid = null;
                if (lookupManager != null && lookupManager.Enabled)
                {
                    var rec = lookupManager.Build(call);
                    if (rec != null)
                    {
                        lookupSources = rec.Sources.Count > 0 ? string.Join(", ", rec.Sources) : "none (no cached data for this call)";
                        dxcc = rec.Dxcc;
                        qrzCachedGrid = rec.Grid;
                    }
                }

                var sb = new StringBuilder();
                sb.AppendLine($"==== Parity mismatch: {call} ==== {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
                sb.AppendLine($"Message: \"{dmsg.Message}\"");
                sb.AppendLine($"Band: {band ?? "(unknown)"}   MyGrid: {myGrid ?? "(unknown)"}   MyContinent: {myContinent ?? "(unknown)"}");
                sb.AppendLine($"Grid source used for Azimuth/Distance: {gridSource}" +
                    (string.IsNullOrEmpty(msgGrid) && !string.IsNullOrEmpty(qrzCachedGrid)
                        ? $" -> fell back to QRZ-cached grid ({qrzCachedGrid})"
                        : ""));
                sb.AppendLine($"LookupManager sources: {lookupSources}");
                sb.AppendLine($"DXCC entity resolved: {(dxcc > 0 ? dxcc.ToString() : "unresolved")}");
                sb.AppendLine("  Field                Wire            Computed");
                AppendField(sb, "IsNewCallOnBand", dmsg.IsNewCallOnBand, computed.IsNewCallOnBand);
                AppendField(sb, "IsNewCallAnyBand", dmsg.IsNewCallAnyBand, computed.IsNewCallAnyBand);
                AppendField(sb, "IsNewCountry", dmsg.IsNewCountry, computed.IsNewCountry);
                AppendField(sb, "IsNewCountryOnBand", dmsg.IsNewCountryOnBand, computed.IsNewCountryOnBand);
                AppendField(sb, "Country", dmsg.Country, computed.Country);
                AppendField(sb, "Continent", dmsg.Continent, computed.Continent);
                AppendField(sb, "IsDx", dmsg.IsDx, computed.IsDx);
                AppendField(sb, "Azimuth", dmsg.Azimuth, computed.Azimuth);
                AppendField(sb, "Distance", dmsg.Distance, computed.Distance);
                sb.AppendLine();

                lock (_lock)
                {
                    if (_writer == null)
                    {
                        string dir = Path.GetDirectoryName(LogPath);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        _writer = new StreamWriter(LogPath, append: true) { AutoFlush = true };
                    }
                    _writer.Write(sb.ToString());
                }
            }
            catch
            {
                // Diagnostic-only: a logging failure must never affect real operation.
            }
        }

        private static void AppendField(StringBuilder sb, string name, object wireVal, object computedVal)
        {
            string marker = !Equals(wireVal, computedVal) ? "  <-- DIFFERS" : "";
            sb.AppendLine($"  {name,-20} {wireVal,-15} {computedVal,-15}{marker}");
        }

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
