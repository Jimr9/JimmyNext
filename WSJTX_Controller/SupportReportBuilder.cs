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
        // Only real secrets are blanked (operator, 2026-10-02): passwords (qrzPassword,
        // lotwLogbookPass, eqslPassword, hamQthPassword, clubLogUploadPassword), the QRZ Logbook API
        // key, the HRDLog upload code, and any token/secret/credential. The rest -- on/off
        // switches, refresh days, usernames, the upload hotkey -- is what support needs to see; it
        // used to be blanked whenever its name merely mentioned qrz, lotw, eqsl or hamqth.
        internal static bool IsSecretSetting(string key)
        {
            string k = (key ?? "").Trim().ToLowerInvariant();
            return k.Contains("password") || k.Contains("passwd") || k.Contains("pwd") || k.EndsWith("pass")
                || k.Contains("apikey") || k.Contains("api_key") || k.EndsWith("uploadcode")
                || k.Contains("token") || k.Contains("secret") || k.Contains("credential");
        }

        private const string Line80 = "================================================================================";
        private const string Line40 = "----------------------------------------";

        // -----------------------------------------------------------------------
        // Public entry point
        // -----------------------------------------------------------------------

        internal static SupportReportResult Build(
            Controller ctrl,
            string callsign, string name, string email,
            string problemType, string description, string steps, bool includeLogbook = true,
            string zipPathChosen = null)
        {
            var result = new SupportReportResult();
            try
            {
                string zipPath   = string.IsNullOrEmpty(zipPathChosen) ? DefaultZipPath(callsign, problemType, name, email) : zipPathChosen;

                // Capture diagnostic snapshot on the UI thread before any I/O
                WsjtxDiagData diag = null;
                try { diag = ctrl?.wsjtxClient?.GetDiagnosticData(); } catch { }
                // What goes in beside the report text (2026-10-12): the current settings, the
                // last few days of logs, the current rules and wording, and -- when the operator
                // includes the logbook -- one copy of it made by the engine itself. Decided first,
                // so the report text can say exactly which log files are in the ZIP.
                string dataRoot = GetLogDir();
                string iniPath = GetIniPath();
                var plan = PlanFocusedData(dataRoot, includeLogbook, iniPath, DateTime.Now);
                var logResults = plan.Where(f => f.Kind == "log" && f.Include).Select(f => new LogFileResult
                {
                    Path = Path.Combine(dataRoot, f.Rel.Replace('/', '\\')),
                    Data = f.Include ? new byte[1] : null,
                    Error = f.Include ? null : f.Note,
                }).ToList();

                using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false, entryNameEncoding: Encoding.UTF8))
                {
                    string report = BuildReportText(ctrl, diag, callsign, name, email,
                                                    problemType, description, steps, logResults);
                    AddTextEntry(zip, "jimmy_support_report.txt", report);

                    if (File.Exists(iniPath))
                    {
                        string redacted = RedactIni(iniPath);
                        AddTextEntry(zip, "jimmy_settings_redacted.ini", redacted);
                    }
                    // Shared settings (call, grid, logins) every profile uses unless it has its own.
                    string sharedIniPath = Controller.SharedIniFilePath();
                    if (File.Exists(sharedIniPath))
                        AddTextEntry(zip, "jimmy_settings_shared_redacted.ini", RedactIni(sharedIniPath));

                    // Nexus contesting foundation: the profile's companion contest ini (saved
                    // per-contest entry defaults -- class, section, run style) beside the main
                    // settings file. Holds no credentials, so it's attached as-is, not redacted.
                    string contestIniPath = ContestConfigStore.CompanionPathFor(iniPath);
                    if (!string.IsNullOrEmpty(contestIniPath) && File.Exists(contestIniPath))
                    {
                        AddTextEntry(zip, "jimmy_settings_contests.ini", File.ReadAllText(contestIniPath));
                    }

                    try { AddFocusedData(zip, dataRoot, plan, includeLogbook, EngineLogbookExport); }
                    catch (Exception ex) { AddTextEntry(zip, "00_START_HERE.txt", "The data folder could not be collected: " + ex.Message); }
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

            AppendSection(sb, "ENGINE CONNECTION", () =>
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
            sb.AppendLine($"Engine version:    {wsjtxVer}");
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
                string contestIniPath = ContestConfigStore.CompanionPathFor(iniPath);
                if (!string.IsNullOrEmpty(contestIniPath) && File.Exists(contestIniPath))
                    sb.AppendLine("(Companion contest settings included as jimmy_settings_contests.ini in the ZIP)");
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
            sb.AppendLine($"Engine name:          {diag.PgmName ?? "Unknown"}");
            sb.AppendLine($"Engine version:       {diag.PgmVer ?? "Unknown"}");
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
            sb.AppendLine($"Mode:                {(string.IsNullOrEmpty(diag.Mode) ? "Unknown" : diag.Mode)}");
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
            sb.AppendLine($"  New grid             {ctrl.soundEnabled_NewGrid,-8} {ctrl.soundFile_NewGrid}");
            sb.AppendLine($"  New grid on band     {ctrl.soundEnabled_NewGridOnBand,-8} {ctrl.soundFile_NewGridOnBand}");
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
                sb.AppendLine("(Decode history accumulates after the engine starts and decodes are received.)");
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
            sb.AppendLine($"{included} recent log file(s) are in the ZIP, under JimmyNextFolder (a very large one as its newest part; 00_START_HERE.txt has the details):");
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
                    bool sensitive = IsSecretSetting(line.Substring(0, eq));

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
        // Whole-folder copy (2.0.80)
        // -----------------------------------------------------------------------

        private const long MaxFolderFileBytes = 25L * 1024 * 1024;

        // Downloadable lookup data (re-downloadable, large, and other stations' details): listed only.
        private static readonly string[] LookupFolders = { "Data/FccUls", "Data/LoTW", "Data/ClubLog", "Data/QRZ", "Data/HamQTH", "Data/Temp" };

        // The operator's own contacts: only with "Include my logbook".
        internal static bool IsLogbookFile(string rel)
        {
            string r = rel.Replace('\\', '/');
            string name = Path.GetFileName(r).ToLowerInvariant();
            if (r.StartsWith("Data/Logbook/", StringComparison.OrdinalIgnoreCase)) return true;
            bool inNexusLog = r.StartsWith("Data/NexusLog", StringComparison.OrdinalIgnoreCase);
            if (!inNexusLog) return false;
            if (r.IndexOf("/backups/", StringComparison.OrdinalIgnoreCase) >= 0 || r.IndexOf("/projection/", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return name.StartsWith("log.") || name == "outbox.json" || name == "row-ids.json";
        }

        internal static bool IsSecretFile(string rel)
        {
            string name = Path.GetFileName(rel).ToLowerInvariant();
            return name.Contains("key") || name.Contains("credential") || name.Contains("token") || name.Contains("password");
        }

        // Settings files and their backups ("x.ini", "x.ini.before-fix.bak"): passwords blanked.
        internal static bool IsSettingsFile(string rel)
        {
            string name = Path.GetFileName(rel).ToLowerInvariant();
            return name.EndsWith(".ini") || name.Contains(".ini.");
        }

        // ── Focused contents (2026-10-12) ─────────────────────────────────────────────────
        // The report used to carry the whole Jimmy Next folder: a 65 MB ZIP of 501 files, most
        // of it old backups and the same log and logbook several times over. It now carries a
        // standard set that serves any kind of problem without guessing its cause:
        //   - the current settings, shared settings, wording and rules (passwords blanked);
        //   - the logs written in the last two days (three files at most), and of a very large
        //     one its newest 20 MB -- a long session writes one file for days, so "recent" goes
        //     by when a file was last written, not by the date in its name, and a report made
        //     just after midnight still has the evening before;
        //   - the small error summaries (crashes, rule errors, TQSL errors) and the logbook-move
        //     reports;
        //   - with "Include my logbook": ONE copy of the logbook, exported by the engine that
        //     owns it (Nexus's own export; the note beside it says how many very recent changes
        //     it lacks), the contacts still waiting to be saved, Jimmy's own logbook notes, the
        //     last three days of sync notes and the newest automatic backup (what the log was
        //     before the latest changes). The live database files are never copied: taken while
        //     the engine writes them, a copy lacked recent contacts.
        // Left out, and counted in the inventory: old backups, other profiles, settings and
        // wording backups, the database and its working files, crash dumps (listed by name),
        // downloaded lookup data, older logs, and the private Support folder.
        // 00_START_HERE.txt says what is in the ZIP and where to look; it lists every file put
        // in, every file that could not be read, and totals of what was left out and why.

        internal sealed class FocusedFile
        {
            public string Rel;        // path under the Jimmy Next folder, '/' separated
            public long Size;
            public DateTime Written;
            public bool Include;
            public long TailBytes;    // > 0: only the newest this-many bytes go in
            public bool Redact;       // a settings file: passwords blanked
            public string Kind;       // log, settings, rules, logbook, summary, other
            public string Note;       // why it is in, or why not
        }

        internal const long LogTailBytes = 20L * 1024 * 1024;
        internal const int RecentLogFiles = 3;
        private const long SmallFileBytes = 1024 * 1024;
        private const long LogbookFileBytes = 64L * 1024 * 1024;

        private static bool IsBackupName(string name) =>
            name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) || name.IndexOf(".before-", StringComparison.OrdinalIgnoreCase) >= 0
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase);

        // Every file in the folder and what the report does with it. Pure (no file is opened),
        // so the choice can be tested and the report text can describe it before the ZIP is made.
        internal static List<FocusedFile> PlanFocusedData(string root, bool includeLogbook, string activeIniPath, DateTime now)
        {
            var files = new List<FocusedFile>();
            if (!Directory.Exists(root)) return files;
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var f = new FocusedFile { Rel = file.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/') };
                try { var fi = new FileInfo(file); f.Size = fi.Length; f.Written = fi.LastWriteTime; } catch { }
                files.Add(f);
            }
            files = files.OrderBy(f => f.Rel, StringComparer.OrdinalIgnoreCase).ToList();

            string activeRel = null;
            try
            {
                string full = Path.GetFullPath(activeIniPath ?? "");
                string rootFull = Path.GetFullPath(root).TrimEnd('\\') + "\\";
                if (full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) activeRel = full.Substring(rootFull.Length).Replace('\\', '/');
            }
            catch { }
            string activeProfileName = activeRel == null ? null : Path.GetFileNameWithoutExtension(activeRel);

            // The recent logs: the newest-written, at most three, each written within two days.
            var recentLogs = new HashSet<string>(files
                .Where(f => f.Rel.IndexOf('/') < 0 && f.Rel.StartsWith("log_", StringComparison.OrdinalIgnoreCase)
                            && f.Rel.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && f.Rel.Length > 8 && char.IsDigit(f.Rel[4]))
                .OrderByDescending(f => f.Written).Take(RecentLogFiles)
                .Where((f, i) => i == 0 || (now - f.Written).TotalHours <= 48)
                .Select(f => f.Rel), StringComparer.OrdinalIgnoreCase);
            // The newest automatic logbook backup, and the sync notes of the last three days.
            string newestBackup = files.Where(f => f.Rel.StartsWith("Data/NexusLog/backups/", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.Written).Select(f => f.Rel).FirstOrDefault();

            foreach (var f in files)
            {
                string rel = f.Rel, name = Path.GetFileName(rel);
                bool top = rel.IndexOf('/') < 0;
                void In(string kind, string note, bool redact = false, long tail = 0) { f.Include = true; f.Kind = kind; f.Note = note; f.Redact = redact; f.TailBytes = tail; }
                void Out(string kind, string note) { f.Include = false; f.Kind = kind; f.Note = note; }

                if (rel.StartsWith(SupportLocal.FolderName + "/", StringComparison.OrdinalIgnoreCase))
                    Out("private", "support codes, login and saved support settings (private)");
                else if (LookupFolders.Any(d => rel.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase)))
                    Out("lookup", "downloadable lookup data");
                else if (rel.StartsWith("CrashDumps/", StringComparison.OrdinalIgnoreCase))
                    Out("crashdump", "crash dump (listed only; ask for it if needed)");
                else if (rel.StartsWith("Backups/", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("Profiles-backup-", StringComparison.OrdinalIgnoreCase)
                         || rel.StartsWith("Data/NexusLog-backup-", StringComparison.OrdinalIgnoreCase))
                    Out("backup", "old backup");
                else if (rel.StartsWith("Data/Logbook/", StringComparison.OrdinalIgnoreCase))
                    Out("logbook", "the old logbook from before the move to Nexus");
                else if (rel.StartsWith("Data/NexusLog/", StringComparison.OrdinalIgnoreCase))
                {
                    string sub = rel.Substring("Data/NexusLog/".Length);
                    if (sub.StartsWith("projection/", StringComparison.OrdinalIgnoreCase)) Out("logbook", "rebuildable read copy of the logbook");
                    else if (sub.StartsWith("backups/", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!includeLogbook) Out("logbook", "logbook (not included by the operator's choice)");
                        else if (rel == newestBackup && f.Size <= LogbookFileBytes) In("logbook", "the newest automatic logbook backup: the log before its latest changes");
                        else Out("backup", "older automatic logbook backup");
                    }
                    else if (sub.StartsWith("diagnostics/", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!includeLogbook) Out("logbook", "logbook sync notes (not included by the operator's choice)");
                        else if ((now - f.Written).TotalDays <= 3 && f.Size <= SmallFileBytes) In("logbook", "logbook sync note from the last three days");
                        else Out("old", "older logbook sync note");
                    }
                    else if (name.Equals("migration-report.txt", StringComparison.OrdinalIgnoreCase) || name.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase))
                        In("summary", "the logbook move's own report");
                    else if (name.Equals("outbox.json", StringComparison.OrdinalIgnoreCase) || name.Equals("jimmy-meta.json", StringComparison.OrdinalIgnoreCase)
                             || name.Equals("log.adi.scrubbed", StringComparison.OrdinalIgnoreCase))
                    {
                        if (includeLogbook && f.Size <= LogbookFileBytes) In("logbook", name.StartsWith("outbox", StringComparison.OrdinalIgnoreCase) ? "contacts still waiting to be saved" : "Jimmy Next's own logbook notes");
                        else Out("logbook", includeLogbook ? "too large" : "logbook (not included by the operator's choice)");
                    }
                    else if (IsLogbookFile(rel) || name.StartsWith("log.", StringComparison.OrdinalIgnoreCase))
                        Out("logbook", includeLogbook ? "the logbook's own files; the engine's export is included instead" : "logbook (not included by the operator's choice)");
                    else if (f.Size <= SmallFileBytes && !IsBackupName(name)) In("other", "logbook folder note");
                    else Out("old", "not needed");
                }
                else if (IsBackupName(name))
                    Out("backup", "backup of a settings or wording file");
                else if (!IsSettingsFile(rel) && IsSecretFile(rel))
                    Out("private", "may hold a key or password");
                else if (top && recentLogs.Contains(rel))
                    In("log", f.Size > LogTailBytes ? $"recent log, newest {LogTailBytes / 1048576} MB of {f.Size / 1048576.0:0.0} MB" : "recent log", tail: f.Size > LogTailBytes ? LogTailBytes : 0);
                else if (top && rel.StartsWith("log_", StringComparison.OrdinalIgnoreCase) && char.IsDigit(rel.Length > 4 ? rel[4] : 'x'))
                    Out("log", "older log");
                else if (top && rel.StartsWith("log_", StringComparison.OrdinalIgnoreCase))
                    In("summary", f.Size > SmallFileBytes ? "error summary, newest 1 MB" : "error summary", tail: f.Size > SmallFileBytes ? SmallFileBytes : 0);
                else if (rel.StartsWith("Profiles/", StringComparison.OrdinalIgnoreCase))
                {
                    // The active profile's settings are in the ZIP already (jimmy_settings_redacted.ini);
                    // its own wording file goes in, other profiles do not.
                    bool wording = rel.StartsWith("Profiles/Wording/", StringComparison.OrdinalIgnoreCase);
                    if (activeRel != null && string.Equals(rel, activeRel, StringComparison.OrdinalIgnoreCase)) Out("settings", "the active profile: in the ZIP as jimmy_settings_redacted.ini");
                    else if (wording && activeProfileName != null && Path.GetFileNameWithoutExtension(name).Equals(activeProfileName, StringComparison.OrdinalIgnoreCase)) In("settings", "the active profile's wording");
                    else if (activeProfileName != null && Path.GetFileNameWithoutExtension(name).Equals(activeProfileName, StringComparison.OrdinalIgnoreCase) && !IsSettingsFile(rel) && f.Size <= SmallFileBytes) In("settings", "the active profile's companion file");
                    else Out("profile", "another profile (not the one in use)");
                }
                else if (rel.StartsWith("Data/RuleDefinitions/", StringComparison.OrdinalIgnoreCase))
                {
                    if (f.Size <= SmallFileBytes) In("rules", "current award rule or list"); else Out("old", "larger than 1 MB");
                }
                else if (IsSettingsFile(rel))
                {
                    if (activeRel != null && string.Equals(rel, activeRel, StringComparison.OrdinalIgnoreCase)) Out("settings", "the active settings: in the ZIP as jimmy_settings_redacted.ini");
                    else if (name.Equals(SharedSettings.FileName, StringComparison.OrdinalIgnoreCase)) Out("settings", "in the ZIP as jimmy_settings_shared_redacted.ini");
                    else if (top) In("settings", "base settings, passwords blanked", redact: true);
                    else Out("profile", "settings not in use");
                }
                else if ((top || rel.StartsWith("Data/", StringComparison.OrdinalIgnoreCase) && rel.IndexOf('/', 5) < 0) && f.Size <= SmallFileBytes)
                    In("other", top && name.Equals("Wording.txt", StringComparison.OrdinalIgnoreCase) ? "current wording" : "current file");
                else
                    Out("old", "not part of the standard set");
            }
            return files;
        }

        // The engine's own export of the whole logbook to `path`. Null when it worked; else why
        // not. `lacking` = recent changes the file does not hold yet (still being saved, or held).
        internal static string EngineLogbookExport(string path, out string lacking)
        {
            lacking = null;
            try
            {
                if (!NexusLogbook.Active || !NexusLogbook.Moved) return "the logbook is not kept by the engine on this computer";
                var reply = NexusLogbook.Client.Export(path);
                if (reply.State != "saved" || !File.Exists(path))
                    return $"the engine did not export it ({reply.State}{(string.IsNullOrEmpty(reply.Why) ? "" : ": " + reply.Why)})";
                if (reply.Saving > 0 || reply.Held > 0) lacking = $"{reply.Saving} recent change(s) still being saved and {reply.Held} held by the logbook are not in it";
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        internal delegate string LogbookExporter(string path, out string lacking);

        internal static void AddFocusedData(ZipArchive zip, string root, List<FocusedFile> plan, bool includeLogbook, LogbookExporter export)
        {
            var failed = new List<string>();
            foreach (var f in plan.Where(x => x.Include))
            {
                string full = Path.Combine(root, f.Rel.Replace('/', '\\'));
                try
                {
                    string entryName = "JimmyNextFolder/" + f.Rel;
                    if (f.Redact) AddTextEntry(zip, entryName, RedactIni(full));
                    else
                    {
                        byte[] data = f.TailBytes > 0 ? ReadTailSafe(full, f.TailBytes) : ReadFileSafe(full);
                        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                        using (var dest = entry.Open()) dest.Write(data, 0, data.Length);
                    }
                }
                catch (Exception ex) { f.Include = false; f.Note = "not readable: " + ex.Message; failed.Add(f.Rel + ": " + ex.Message); }
            }

            // The logbook: one copy, made by its owner. If the engine cannot (not running, say),
            // the ADIF file it keeps beside the database, and the note says which it is.
            string logbookLine;
            if (!includeLogbook) logbookLine = "Logbook: not included (the operator's choice). Nothing from the logbook folder but the move reports is here.";
            else
            {
                string tmp = Path.Combine(Path.GetTempPath(), "JimmySupportLog_" + Guid.NewGuid().ToString("N") + ".adi");
                try
                {
                    string lacking = null;
                    string why = export == null ? "no engine" : export(tmp, out lacking);
                    if (why == null)
                    {
                        byte[] data = ReadFileSafe(tmp);
                        var entry = zip.CreateEntry("Logbook/logbook-export.adi", CompressionLevel.Optimal);
                        using (var dest = entry.Open()) dest.Write(data, 0, data.Length);
                        logbookLine = "Logbook: Logbook/logbook-export.adi -- the whole logbook as the engine exported it at " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC"
                                      + (lacking == null ? "; nothing was waiting." : "; " + lacking + ".");
                    }
                    else
                    {
                        string mirror = Path.Combine(root, "Data", "NexusLog", "log.adi");
                        if (File.Exists(mirror))
                        {
                            byte[] data = ReadFileSafe(mirror);
                            var entry = zip.CreateEntry("Logbook/log.adi", CompressionLevel.Optimal);
                            using (var dest = entry.Open()) dest.Write(data, 0, data.Length);
                            logbookLine = $"Logbook: Logbook/log.adi -- the ADIF copy the engine keeps beside its database, last written {File.GetLastWriteTimeUtc(mirror):yyyy-MM-dd HH:mm:ss} UTC. "
                                          + $"NOT a fresh export ({why}); a contact made after that time may be missing.";
                        }
                        else logbookLine = $"Logbook: could NOT be included ({why}), and there is no ADIF copy beside the database.";
                    }
                }
                catch (Exception ex) { logbookLine = "Logbook: could NOT be included: " + ex.Message; failed.Add("logbook: " + ex.Message); }
                finally { try { File.Delete(tmp); } catch { } }
            }

            var sb = new StringBuilder();
            sb.AppendLine("JIMMY NEXT SUPPORT REPORT -- START HERE");
            sb.AppendLine();
            sb.AppendLine("jimmy_support_report.txt    what the sender wrote, version, Windows, radio and operating state");
            sb.AppendLine("jimmy_settings_redacted.ini the profile in use, passwords blanked (shared and contest settings beside it)");
            sb.AppendLine("JimmyNextFolder/log_*.txt   the recent diagnostic logs; newest activity is at the end of each");
            sb.AppendLine("JimmyNextFolder/            the rest of the current files from the Jimmy Next folder, listed below");
            sb.AppendLine(logbookLine);
            sb.AppendLine();
            sb.AppendLine("Folder: " + root);
            sb.AppendLine();
            sb.AppendLine("INCLUDED");
            foreach (var f in plan.Where(x => x.Include))
                sb.AppendLine($"  {f.Rel}\t{f.Size:N0} bytes\t{f.Written:yyyy-MM-dd HH:mm}\t{f.Note}");
            if (failed.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("COULD NOT BE READ (wanted, but missing from this report)");
                foreach (string line in failed) sb.AppendLine("  " + line);
            }
            sb.AppendLine();
            sb.AppendLine("LEFT OUT (count, total size, reason)");
            foreach (var g in plan.Where(x => !x.Include && !(x.Note ?? "").StartsWith("not readable")).GroupBy(x => x.Note).OrderByDescending(g => g.Sum(x => x.Size)))
                sb.AppendLine($"  {g.Count(),5} file(s)\t{g.Sum(x => x.Size) / 1048576.0,9:0.0} MB\t{g.Key}");
            var dumps = plan.Where(x => x.Kind == "crashdump").OrderByDescending(x => x.Written).Take(10).ToList();
            if (dumps.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("CRASH DUMPS ON THIS COMPUTER (not included)");
                foreach (var f in dumps) sb.AppendLine($"  {f.Rel}\t{f.Size / 1048576.0:0.0} MB\t{f.Written:yyyy-MM-dd HH:mm}");
            }
            var otherProfiles = plan.Where(x => x.Kind == "profile").Select(x => x.Rel).ToList();
            if (otherProfiles.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("OTHER PROFILES (names only): " + string.Join(", ", otherProfiles.Select(Path.GetFileName)));
            }
            AddTextEntry(zip, "00_START_HERE.txt", sb.ToString());
        }

        // The newest `max` bytes of a file, from the start of a line.
        private static byte[] ReadTailSafe(string path, long max)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length > max) fs.Seek(-max, SeekOrigin.End);
                using (var ms = new MemoryStream())
                {
                    fs.CopyTo(ms);
                    byte[] all = ms.ToArray();
                    int start = fs.Length > max ? Array.IndexOf(all, (byte)'\n') + 1 : 0;
                    if (start <= 0 || start >= all.Length) return all;
                    byte[] tail = new byte[all.Length - start];
                    Buffer.BlockCopy(all, start, tail, 0, tail.Length);
                    return tail;
                }
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

        // 2.0.81: the default place and name the Save As box offers (not everyone uses Downloads).
        // 2026-10-12: named for who sent it, what kind it is and when (SupportNames.Report), and
        // never the name of a file that is already there.
        internal static string DefaultZipPath(string callsign, string problemType, string name, string email) =>
            SupportNames.Unique(GetOutputDir(), SupportNames.Report(callsign, problemType, name, email, DateTime.UtcNow), ".zip");

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
                return "Engine Connection";
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
