using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WSJTX_Controller
{
    internal sealed class SupportReportResult
    {
        public bool   Success;
        public string ZipPath;
        public string Error;
    }

    internal static class SupportReportBuilder
    {
        private static readonly string[] SensitiveKeywords =
        {
            "password", "pwd", "pass", "apikey", "api_key",
            "token", "secret", "credential", "qrz", "lotw", "eqsl", "hamqth",
        };

        private const string Line80 = "================================================================================";
        private const string Line40 = "----------------------------------------";

        // -----------------------------------------------------------------------
        // Public entry point
        // -----------------------------------------------------------------------

        internal static SupportReportResult Build(
            Controller ctrl,
            string callsign, string name, string email,
            string problemType, string description, string steps)
        {
            var result = new SupportReportResult();
            try
            {
                string infoVer   = GetInfoVersion();
                string safeVer   = infoVer.Replace("/", "-").Replace("\\", "-").Replace(":", "").Replace(" ", "_");
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string zipName   = $"Jimmy_{safeVer}_support_{timestamp}.zip";
                string outputDir = GetOutputDir();
                string zipPath   = Path.Combine(outputDir, zipName);

                // Capture diagnostic snapshot on the UI thread before any I/O
                WsjtxDiagData diag = null;
                try { diag = ctrl?.wsjtxClient?.GetDiagnosticData(); } catch { }

                // Read log files before building the report so the report can
                // accurately reflect which files were actually included in the ZIP.
                var logResults = CollectLogFiles();

                using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false, entryNameEncoding: Encoding.UTF8))
                {
                    string report = BuildReportText(ctrl, diag, callsign, name, email,
                                                    problemType, description, steps, logResults);
                    AddTextEntry(zip, "jimmy_support_report.txt", report);

                    string iniPath = GetIniPath();
                    if (File.Exists(iniPath))
                    {
                        string redacted = RedactIni(iniPath);
                        AddTextEntry(zip, "jimmy_settings_redacted.ini", redacted);
                    }

                    // Only create ZIP entries for files that were successfully read.
                    // This prevents 0-byte ghost entries when a read fails.
                    foreach (var lr in logResults)
                    {
                        if (lr.Data != null && lr.Data.Length > 0)
                        {
                            try
                            {
                                var entry = zip.CreateEntry(Path.GetFileName(lr.Path), CompressionLevel.Optimal);
                                using (var dest = entry.Open())
                                    dest.Write(lr.Data, 0, lr.Data.Length);
                            }
                            catch { /* skip on ZIP write failure */ }
                        }
                    }
                }

                result.Success = true;
                result.ZipPath = zipPath;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error   = ex.Message;
            }
            return result;
        }

        // -----------------------------------------------------------------------
        // Report text assembly
        // -----------------------------------------------------------------------

        private static string BuildReportText(
            Controller ctrl, WsjtxDiagData diag,
            string callsign, string name, string email,
            string problemType, string description, string steps,
            IReadOnlyList<LogFileResult> logResults)
        {
            var sb = new StringBuilder();

            AppendSection(sb, "DIAGNOSTIC SUMMARY", () =>
                BuildSummarySection(sb, ctrl, diag, callsign, name, email, problemType, description));

            AppendSection(sb, "USER REPORT", () =>
                BuildUserSection(sb, callsign, name, email, problemType, description, steps));

            AppendSection(sb, "JIMMY VERSION", () =>
                BuildVersionSection(sb));

            AppendSection(sb, "WINDOWS AND RUNTIME", () =>
                BuildWindowsSection(sb));

            AppendSection(sb, "SETTINGS", () =>
                BuildSettingsSection(sb));

            AppendSection(sb, "WSJT-X CONNECTION", () =>
                BuildConnectionSection(sb, ctrl, diag));

            AppendSection(sb, "OPERATING STATE", () =>
                BuildOperatingSection(sb, ctrl, diag));

            AppendSection(sb, "UI CONFIGURATION", () =>
                BuildUiSection(sb, ctrl, diag));

            AppendSection(sb, "NON-DEFAULT HOTKEYS", () =>
                BuildHotkeySection(sb, ctrl));

            AppendSection(sb, "SOUND CONFIGURATION", () =>
                BuildSoundSection(sb, ctrl));

            AppendSection(sb, "CALL QUEUE", () =>
                BuildQueueSection(sb, diag));

            AppendSection(sb, "RECENT DECODE HISTORY", () =>
                BuildDecodeHistorySection(sb, diag));

            AppendSection(sb, "LOG FILES", () =>
                BuildLogFileSection(sb, logResults));

            AppendSection(sb, "PRIVACY NOTE", () =>
            {
                sb.AppendLine("The following were intentionally excluded or redacted:");
                sb.AppendLine("  - QRZ, LoTW, eQSL, and HamQTH credentials");
                sb.AppendLine("  - Settings keys matching: password / token / API key patterns");
                sb.AppendLine("  - Passwords and API keys in any section");
                sb.AppendLine();
                sb.AppendLine("File paths in this report contain the Windows username.");
                sb.AppendLine("The redacted settings file (jimmy_settings_redacted.ini) is included");
                sb.AppendLine("in the ZIP as a separate attachment.");
            });

            return sb.ToString();
        }

        // -----------------------------------------------------------------------
        // Sections
        // -----------------------------------------------------------------------

        private static void BuildSummarySection(
            StringBuilder sb, Controller ctrl, WsjtxDiagData diag,
            string callsign, string name, string email,
            string problemType, string description)
        {
            string infoVer      = Safe(() => GetInfoVersion());
            string wsjtxVer     = Safe(() => diag?.PgmVer ?? "Unknown");
            string winVer       = Safe(() => GetWindowsVersion());
            string connection   = Safe(() => diag == null ? "Unknown" : diag.Connected ? "Connected" : diag.Connecting ? "Connecting" : "Disconnected");
            string txModeStr    = Safe(() => diag == null ? "Unknown" : FormatTxMode(diag.TxMode));
            string modeStr      = Safe(() => string.IsNullOrEmpty(diag?.Mode) ? "Unknown" : diag.Mode);
            string subsystem    = Safe(() => InferSubsystem(problemType, description, ctrl));

            sb.AppendLine($"Report created:    {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Jimmy version:     {infoVer}");
            sb.AppendLine($"WSJT-X version:   {wsjtxVer}");
            sb.AppendLine($"Windows:           {winVer}");
            sb.AppendLine($"Connection:        {connection}");
            sb.AppendLine($"Mode:              {modeStr} / {txModeStr}");
            sb.AppendLine($"Problem type:      {problemType}");
            sb.AppendLine($"Likely subsystem:  {subsystem}");
            sb.AppendLine();
            sb.AppendLine("User description:");
            string truncated = description.Length > 300
                ? description.Substring(0, 297) + "..."
                : description;
            foreach (var line in truncated.Split('\n'))
                sb.AppendLine("  " + line.TrimEnd('\r'));
            sb.AppendLine();
            sb.AppendLine("Developer notes:");
            sb.AppendLine("  (to be completed by reviewer before forwarding)");
        }

        private static void BuildUserSection(
            StringBuilder sb,
            string callsign, string name, string email,
            string problemType, string description, string steps)
        {
            if (!string.IsNullOrEmpty(callsign)) sb.AppendLine($"Callsign:     {callsign}");
            if (!string.IsNullOrEmpty(name))     sb.AppendLine($"Name:         {name}");
            if (!string.IsNullOrEmpty(email))    sb.AppendLine($"Email:        {email}");
            sb.AppendLine($"Problem type: {problemType}");
            sb.AppendLine();
            sb.AppendLine("Problem description:");
            sb.AppendLine(Line40);
            foreach (var line in description.Split('\n'))
                sb.AppendLine(line.TrimEnd('\r'));
            sb.AppendLine(Line40);

            if (!string.IsNullOrWhiteSpace(steps))
            {
                sb.AppendLine();
                sb.AppendLine("Steps to reproduce:");
                sb.AppendLine(Line40);
                foreach (var line in steps.Split('\n'))
                    sb.AppendLine(line.TrimEnd('\r'));
                sb.AppendLine(Line40);
            }
        }

        private static void BuildVersionSection(StringBuilder sb)
        {
            var asm = Assembly.GetExecutingAssembly();

            string infoVer = Safe(() =>
                asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion ?? "Unknown");

            string fileVer = Safe(() =>
                FileVersionInfo.GetVersionInfo(asm.Location).FileVersion ?? "Unknown");

            string exePath = Safe(() => asm.Location);
            string buildTs = Safe(() => File.GetLastWriteTime(asm.Location).ToString("yyyy-MM-dd HH:mm:ss"));

            sb.AppendLine($"Informational version: {infoVer}");
            sb.AppendLine($"File version:          {fileVer}");
            sb.AppendLine($"Executable path:       {exePath}");
            sb.AppendLine($"Build timestamp:       {buildTs}");
        }

        private static void BuildWindowsSection(StringBuilder sb)
        {
            sb.AppendLine($"Windows version:   {Safe(GetWindowsVersion)}");
            sb.AppendLine($"Windows build:     {Safe(() => Environment.OSVersion.Version.Build.ToString())}");
            sb.AppendLine($".NET runtime:      {Safe(() => Environment.Version.ToString())}");
            sb.AppendLine($"Processor count:   {Safe(() => Environment.ProcessorCount.ToString())}");
            sb.AppendLine($"Screen DPI:        {Safe(() => { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return $"{g.DpiX:F0} x {g.DpiY:F0}"; })}");
            sb.AppendLine($"Monitor count:     {Safe(() => Screen.AllScreens.Length.ToString())}");
        }

        private static void BuildSettingsSection(StringBuilder sb)
        {
            string iniPath = GetIniPath();
            sb.AppendLine($"Settings file: {iniPath}");
            if (File.Exists(iniPath))
            {
                sb.AppendLine($"Settings file exists:  Yes");
                sb.AppendLine($"Settings last modified: {Safe(() => File.GetLastWriteTime(iniPath).ToString("yyyy-MM-dd HH:mm:ss"))}");
                sb.AppendLine($"Settings file size:    {Safe(() => new FileInfo(iniPath).Length + " bytes")}");
                sb.AppendLine("(Redacted copy included as jimmy_settings_redacted.ini in the ZIP)");
            }
            else
            {
                sb.AppendLine("Settings file exists:  No");
            }
        }

        private static void BuildConnectionSection(StringBuilder sb, Controller ctrl, WsjtxDiagData diag)
        {
            if (diag == null)
            {
                sb.AppendLine("Connection data unavailable.");
                return;
            }

            string connState = diag.Connected ? "Connected" : diag.Connecting ? "Connecting" : "Disconnected";
            sb.AppendLine($"Connection state:     {connState}");
            sb.AppendLine($"WSJT-X program name:  {diag.PgmName ?? "Unknown"}");
            sb.AppendLine($"WSJT-X version:       {diag.PgmVer ?? "Unknown"}");
            sb.AppendLine($"WSJT-X revision:      {WsjtxClient.wsjtxRevision}");
            // UDP IP address / UDP multicast lines removed 2026-08-18: both were the classic
            // WSJT-X/UDP transport's own listen-address configuration, which no longer exists
            // (WsjtxProtocolAdapter and the rest of that transport are deleted). Port is kept --
            // it's still genuinely used, now to build the real engine host's own --jimmy-addr
            // argument, not a UDP listen port.
            sb.AppendLine($"Engine announce port: {diag.Port}");
        }

        private static void BuildOperatingSection(StringBuilder sb, Controller ctrl, WsjtxDiagData diag)
        {
            if (diag == null)
            {
                sb.AppendLine("Operating state unavailable.");
                return;
            }

            string band = Safe(() =>
            {
                if (diag.BandIdx == null || diag.Bands == null || diag.BandIdx.Value >= diag.Bands.Length)
                    return "Unknown";
                return diag.Bands[diag.BandIdx.Value] + "m";
            });

            string freq = Safe(() =>
                diag.DialFrequency == 0
                    ? "Unknown"
                    : $"{diag.DialFrequency / 1_000_000.0:F6} MHz ({diag.DialFrequency} Hz)");

            sb.AppendLine($"myCall:              {diag.MyCall ?? "(not set)"}");
            sb.AppendLine($"myGrid:              {diag.MyGrid ?? "(not set)"}");
            sb.AppendLine($"WSJT-X mode:         {(string.IsNullOrEmpty(diag.Mode) ? "Unknown" : diag.Mode)}");
            sb.AppendLine($"Jimmy TX mode:       {FormatTxMode(diag.TxMode)}");
            sb.AppendLine($"TX first:            {diag.TxFirst}");
            sb.AppendLine($"Current band:        {band}");
            sb.AppendLine($"Dial frequency:      {freq}");
            sb.AppendLine($"Call in progress:    {diag.CallInProg ?? "(none)"}");
            sb.AppendLine($"PSKReporter:         {(diag.UsePskReporter ? "Enabled" : "Disabled")}");
            sb.AppendLine($"Diagnostic log:      {(diag.DiagLog ? "Enabled" : "Disabled")}");
            sb.AppendLine($"Call queue count:    {diag.CallQueueCount}");
            sb.AppendLine($"Logged this session: {diag.LoggedCount}");
        }

        private static void BuildUiSection(StringBuilder sb, Controller ctrl, WsjtxDiagData diag)
        {
            if (ctrl == null)
            {
                sb.AppendLine("UI state unavailable.");
                return;
            }

            sb.AppendLine($"Advanced UI enabled:       {ctrl.advancedCallLayout}");
            if (ctrl.advancedCallLayout)
            {
                string label1 = (diag?.TxFirst == true) ? "TX1" : "RX1";
                string label2 = (diag?.TxFirst == true) ? "RX2" : "TX2";
                sb.AppendLine($"  {label1} list visible:      {ctrl.advShowTx1} (count: {diag?.Tx1Count ?? 0})");
                sb.AppendLine($"  {label2} list visible:      {ctrl.advShowTx2} (count: {diag?.Tx2Count ?? 0})");
                sb.AppendLine($"  Raw decode list visible: {ctrl.advShowRaw} (count: {diag?.RawDecodeCount ?? 0})");
            }

            sb.AppendLine($"Max queued calls (base):   {ctrl.maxQueuedCallsBase}");
            sb.AppendLine($"Max raw rows:              {ctrl.rawMaxRows}");
            sb.AppendLine($"Keep TX list during TX:    {ctrl.keepTransmitListDuringTx}");
            sb.AppendLine($"Keep list position:        {ctrl.keepListPositionDuringRefresh}");
            sb.AppendLine($"Always on top:             {ctrl.alwaysOnTop}");
            sb.AppendLine();

            var sortList = diag?.CallQueueDetails != null && ctrl?.wsjtxClient != null
                ? ctrl.wsjtxClient.Ranker.rankOrderList
                : null;
            sb.AppendLine("Sort order: " + Safe(() =>
                sortList != null && sortList.Count > 0
                    ? string.Join(", ", sortList)
                    : "Default"));

            sb.AppendLine("Row field order: " + Safe(() =>
                ctrl.wsjtxClient?.callWaitingRowOrderFields != null
                    ? string.Join(", ", ctrl.wsjtxClient.callWaitingRowOrderFields)
                    : "Unknown"));

            sb.AppendLine();
            sb.AppendLine("Raw decode filters:");
            sb.AppendLine($"  Show CQ:         {ctrl.rawShowCq}");
            sb.AppendLine($"  Show Directed:   {ctrl.rawShowDirected}");
            sb.AppendLine($"  Show Reports:    {ctrl.rawShowReports}");
            sb.AppendLine($"  Show RR73:       {ctrl.rawShowRR73}");
            sb.AppendLine($"  Show 73:         {ctrl.rawShow73}");
            sb.AppendLine($"  Show POTA:       {ctrl.rawShowPota}");
            sb.AppendLine($"  Show SOTA:       {ctrl.rawShowSota}");
            sb.AppendLine($"  Show DX:         {ctrl.rawShowDx}");
            sb.AppendLine($"  Show SNR:        {ctrl.rawShowSnr}");
            sb.AppendLine($"  Show Grid:       {ctrl.rawShowGrid}");
            sb.AppendLine($"  Show Country:    {ctrl.rawShowCountry}");
            sb.AppendLine($"  Show U.S. state: {ctrl.showUsStateCheckBox.Checked}");
            sb.AppendLine($"  Show Dist/Az:    {ctrl.rawShowDistAz}");
            sb.AppendLine($"  Only callsigns:  {ctrl.rawOnlyCallsigns}");
            sb.AppendLine($"  Only unworked:   {ctrl.rawOnlyUnworked}");
            sb.AppendLine($"  Only ranked:     {ctrl.rawOnlyRanked}");
            sb.AppendLine($"  Priority tags:   {ctrl.rawPriorityTags}");
            sb.AppendLine($"  Newest first:    {ctrl.rawNewestFirst}");
        }

        private static void BuildHotkeySection(StringBuilder sb, Controller ctrl)
        {
            if (ctrl?.hotkeyConfig == null)
            {
                sb.AppendLine("Hotkey data unavailable.");
                return;
            }

            bool anyNonDefault = false;
            foreach (HotkeyAction action in Enum.GetValues(typeof(HotkeyAction)))
            {
                if (!HotkeyConfig.Defaults.ContainsKey(action)) continue;
                Keys current = ctrl.hotkeyConfig[action];
                Keys def     = HotkeyConfig.Defaults[action];
                if (current == def) continue;
                if (!anyNonDefault)
                {
                    anyNonDefault = true;
                    sb.AppendLine("Action                              Current              Default");
                    sb.AppendLine(Line40);
                }
                string actionName = HotkeyConfig.DisplayNames.TryGetValue(action, out var n) ? n : action.ToString();
                string curStr  = current == Keys.None ? "(none)" : HotkeyConfig.FormatKeys(current);
                string defStr  = def     == Keys.None ? "(none)" : HotkeyConfig.FormatKeys(def);
                sb.AppendLine($"  {actionName,-34} {curStr,-20} {defStr}");
            }
            if (!anyNonDefault)
                sb.AppendLine("All hotkeys are at their default values.");
        }

        private static void BuildSoundSection(StringBuilder sb, Controller ctrl)
        {
            if (ctrl == null)
            {
                sb.AppendLine("Sound configuration unavailable.");
                return;
            }

            sb.AppendLine($"Sounds enabled (master): {ctrl.soundsEnabled}");
            sb.AppendLine();
            sb.AppendLine("Event                  Enabled  File");
            sb.AppendLine(Line40);
            sb.AppendLine($"  New call added       (checkbox)  {ctrl.soundFile_CallAdded}");
            sb.AppendLine($"  Calling me           (checkbox)  {ctrl.soundFile_CallingMe}");
            sb.AppendLine($"  Logged               (checkbox)  {ctrl.soundFile_Logged}");
            sb.AppendLine($"  TX enabled           {ctrl.soundEnabled_TxEnabled,-8} {ctrl.soundFile_TxEnabled}");
            sb.AppendLine($"  Disconnected         {ctrl.soundEnabled_Disconnected,-8} {ctrl.soundFile_Disconnected}");
            sb.AppendLine($"  New DXCC             {ctrl.soundEnabled_NewDxcc,-8} {ctrl.soundFile_NewDxcc}");
            sb.AppendLine($"  New DXCC on band     {ctrl.soundEnabled_NewDxccOnBand,-8} {ctrl.soundFile_NewDxccOnBand}");
            sb.AppendLine($"  Always wanted        {ctrl.soundEnabled_AlwaysWanted,-8} {ctrl.soundFile_AlwaysWanted}");
            sb.AppendLine($"  Directed CQ          {ctrl.soundEnabled_DirectedCq,-8} {ctrl.soundFile_DirectedCq}");
            sb.AppendLine($"  POTA                 {ctrl.soundEnabled_Pota,-8} {ctrl.soundFile_Pota}");
            sb.AppendLine($"  SOTA                 {ctrl.soundEnabled_Sota,-8} {ctrl.soundFile_Sota}");
            sb.AppendLine($"  Wanted anywhere      {ctrl.soundEnabled_WantedAnywhere,-8} {ctrl.soundFile_WantedAnywhere}");
        }

        private static void BuildQueueSection(StringBuilder sb, WsjtxDiagData diag)
        {
            if (diag == null)
            {
                sb.AppendLine("Queue data unavailable.");
                return;
            }

            sb.AppendLine($"Call queue count:    {diag.CallQueueCount}");
            sb.AppendLine($"Logged this session: {diag.LoggedCount}");
            sb.AppendLine($"TX1 list count:      {diag.Tx1Count}");
            sb.AppendLine($"TX2 list count:      {diag.Tx2Count}");
            sb.AppendLine($"Raw decode count:    {diag.RawDecodeCount}");

            if (diag.CallQueueDetails.Count == 0)
            {
                sb.AppendLine();
                sb.AppendLine("Queue is empty.");
                return;
            }

            sb.AppendLine();
            sb.AppendLine("Pos  Callsign   Country          Category              New/Band  SNR   Dist  Az   Message");
            sb.AppendLine(Line80);
            foreach (var e in diag.CallQueueDetails)
            {
                string flags = (e.IsNewCountry ? "NC " : "   ") + (e.IsNewCountryOnBand ? "NCB" : "   ");
                sb.AppendLine(
                    $"{e.QueuePosition,3}  {e.Callsign,-10} {e.Country,-16} {e.Category,-21} {flags}  {e.Snr,3}  {e.Distance,5} {e.Azimuth,3}  {e.Message}");
            }
        }

        private static void BuildDecodeHistorySection(StringBuilder sb, WsjtxDiagData diag)
        {
            if (diag == null || diag.DecodeHistory.Count == 0)
            {
                sb.AppendLine("No recent decode history was available.");
                sb.AppendLine("(Decode history accumulates after Jimmy connects to WSJT-X and decodes are received.)");
                return;
            }

            sb.AppendLine($"Recent decode history: {diag.DecodeHistory.Count} record(s) (newest at bottom)");
            sb.AppendLine($"Note: Action column not tracked in this version.");
            sb.AppendLine();
            sb.AppendLine("UTC       SNR   dF    dT     Mode Country              Message");
            sb.AppendLine(Line80);

            // Show last 100 entries
            var entries = diag.DecodeHistory.Count > 100
                ? diag.DecodeHistory.Skip(diag.DecodeHistory.Count - 100).ToList()
                : diag.DecodeHistory;

            foreach (var e in entries)
            {
                string flags = (e.IsNewCountry ? "NC " : "   ") + (e.IsDx ? "DX" : "  ");
                sb.AppendLine(
                    $"{e.TimeUtc,-9} {e.Snr,3}  {e.DeltaFrequency,4}  {e.DeltaTime,5:F1}  {e.Mode,-4}  {e.Country,-20} {e.Message}  [{flags}]");
            }
        }

        private static void BuildLogFileSection(StringBuilder sb, IReadOnlyList<LogFileResult> logResults)
        {
            string logDir = GetLogDir();
            sb.AppendLine($"Log directory: {logDir}");

            if (logResults.Count == 0)
            {
                sb.AppendLine("No diagnostic log files were available.");
                sb.AppendLine("(Enable 'Diagnostic Log' in Jimmy Setup to capture future sessions.)");
                return;
            }

            int included = 0;
            foreach (var lr in logResults) if (lr.Included) included++;
            sb.AppendLine($"Found {logResults.Count} log file(s) (up to 5 most recent attempted). {included} included in ZIP:");
            sb.AppendLine();

            foreach (var lr in logResults)
            {
                string fn = Path.GetFileName(lr.Path);
                long   sz = 0;
                string mod = "";
                try { sz  = new FileInfo(lr.Path).Length; }       catch { }
                try { mod = File.GetLastWriteTime(lr.Path).ToString("yyyy-MM-dd HH:mm:ss"); } catch { }

                if (lr.Included)
                {
                    sb.AppendLine($"  [included]      {fn}  {sz / 1024.0:F1} KB  modified {mod}");
                }
                else
                {
                    string reason = lr.Error ?? (lr.Data != null ? "file was empty" : "read failed");
                    sb.AppendLine($"  [not included]  {fn}  {sz / 1024.0:F1} KB  modified {mod}  -- {reason}");
                }
            }
        }

        // -----------------------------------------------------------------------
        // INI redaction
        // -----------------------------------------------------------------------

        private static string RedactIni(string path)
        {
            try
            {
                string[] lines = File.ReadAllLines(path);
                var sb = new StringBuilder();
                int redactCount = 0;

                sb.AppendLine("; Jimmy settings — sensitive values redacted for privacy");
                sb.AppendLine($"; Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine();

                foreach (string line in lines)
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith(";") || trimmed.StartsWith("[") || !trimmed.Contains("="))
                    {
                        sb.AppendLine(line);
                        continue;
                    }

                    int eq  = line.IndexOf('=');
                    string rawKey = line.Substring(0, eq).Trim().ToLowerInvariant();
                    bool sensitive = SensitiveKeywords.Any(k => rawKey.Contains(k));

                    if (sensitive)
                    {
                        sb.AppendLine(line.Substring(0, eq + 1) + "[REDACTED]");
                        redactCount++;
                    }
                    else
                    {
                        sb.AppendLine(line);
                    }
                }

                if (redactCount > 0)
                    sb.AppendLine($"; [{redactCount} sensitive value(s) were redacted]");

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"; Could not read settings file: {ex.Message}";
            }
        }

        // -----------------------------------------------------------------------
        // ZIP helpers
        // -----------------------------------------------------------------------

        private static void AddTextEntry(ZipArchive zip, string entryName, string text)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using (var stream = entry.Open())
            {
                byte[] data = Encoding.UTF8.GetBytes(text);
                stream.Write(data, 0, data.Length);
            }
        }

        private static List<LogFileResult> CollectLogFiles()
        {
            var results = new List<LogFileResult>();
            string logDir = GetLogDir();
            if (!Directory.Exists(logDir)) return results;

            var logs = Directory.GetFiles(logDir, "log_*.txt")
                                .OrderByDescending(File.GetLastWriteTime)
                                .Take(5);
            foreach (var lf in logs)
            {
                var lr = new LogFileResult { Path = lf };
                try   { lr.Data  = ReadFileSafe(lf); }
                catch (Exception ex) { lr.Error = ex.Message; }
                results.Add(lr);
            }
            return results;
        }

        // FileShare.ReadWrite allows reading the active log even while WsjtxClient
        // has it open for appending (FileAccess.Write, FileShare.Read). Without
        // ReadWrite here Windows rejects the open because our handle's share mode
        // would not permit the existing writer, producing a sharing violation and
        // a 0-byte ZIP entry.
        private static byte[] ReadFileSafe(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                return ms.ToArray();
            }
        }

        // -----------------------------------------------------------------------
        // Path helpers
        // -----------------------------------------------------------------------

        private static string GetOutputDir()
        {
            string downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return Directory.Exists(downloads)
                ? downloads
                : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        // Independent audit finding, 2026-08-30: this used to hard-return LocalAppData\<name>\
        // <name>.ini -- the base file -- so a support ZIP from an operator running a named
        // profile redacted and attached settings that were NOT the ones governing the session
        // that reproduced their problem. Now resolves the same active-profile path Form_Load
        // does at startup (Controller.ActiveIniFilePath). Redaction downstream is unchanged.
        private static string GetIniPath() => Controller.ActiveIniFilePath();

        private static string GetLogDir()
        {
            string name = Assembly.GetExecutingAssembly().GetName().Name;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                name);
        }

        // -----------------------------------------------------------------------
        // Diagnostic helpers
        // -----------------------------------------------------------------------

        private static string GetInfoVersion()
        {
            return Assembly.GetExecutingAssembly()
                           .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                           ?.InformationalVersion ?? "Unknown";
        }

        private static string GetWindowsVersion()
        {
            string os = Environment.OSVersion.VersionString;
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    string display = key?.GetValue("DisplayVersion")?.ToString();
                    if (!string.IsNullOrEmpty(display))
                        return $"{os} ({display})";
                }
            }
            catch { }
            return os;
        }

        private static string FormatTxMode(WsjtxClient.TxModes mode)
        {
            switch (mode)
            {
                case WsjtxClient.TxModes.LISTEN:   return "Listen";
                case WsjtxClient.TxModes.CALL_CQ:  return "Call CQ";
                default:                            return mode.ToString();
            }
        }

        private static string InferSubsystem(string problemType, string description, Controller ctrl)
        {
            if (problemType == "Feature Request") return "Feature Request";
            string combined = (problemType + " " + description).ToLowerInvariant();
            if (problemType == "Accessibility" || combined.Contains("screen reader") ||
                combined.Contains("jaws") || combined.Contains("nvda") ||
                combined.Contains("accessibility") || combined.Contains("tab order"))
                return "Accessibility / Screen Reader";
            if (combined.Contains("queue") || combined.Contains("sort") ||
                combined.Contains("rank") || combined.Contains("priority") ||
                combined.Contains("order"))
                return "Queue / Call Ranking";
            if (combined.Contains("connect") || combined.Contains("udp") ||
                combined.Contains("wsjt-x") || combined.Contains("wsjtx") ||
                combined.Contains("heartbeat"))
                return "WSJT-X Connection";
            if (combined.Contains("audio") || combined.Contains("sound") ||
                combined.Contains("volume") || combined.Contains("beep"))
                return "Audio / Sounds";
            if (combined.Contains("pota") || combined.Contains("sota"))
                return "POTA / SOTA";
            if (combined.Contains("pskr") || combined.Contains("pskreporter"))
                return "PSKReporter";
            if (combined.Contains("log") || combined.Contains("lotw") || combined.Contains("adif"))
                return "Logging";
            return "General";
        }

        // -----------------------------------------------------------------------
        // Section wrapper + safe executor
        // -----------------------------------------------------------------------

        private static void AppendSection(StringBuilder sb, string title, Action body)
        {
            sb.AppendLine();
            sb.AppendLine(Line80);
            sb.AppendLine(title);
            sb.AppendLine(Line80);
            try { body(); }
            catch (Exception ex) { sb.AppendLine($"[Section failed: {ex.Message}]"); }
        }

        private static string Safe(Func<string> fn)
        {
            try { return fn() ?? ""; }
            catch { return "Unavailable"; }
        }

        private sealed class LogFileResult
        {
            public string Path;
            public byte[] Data;   // null if read failed
            public string Error;  // exception message if read failed
            public bool Included => Data != null && Data.Length > 0;
        }
    }
}
