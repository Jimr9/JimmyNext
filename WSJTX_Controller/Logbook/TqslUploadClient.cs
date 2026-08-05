using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WSJTX_Controller
{
    // Which mechanism Alt+U's LoTW leg uses. Independent of DecodeEngineMode/RadioControlMode --
    // sub-command 16 has no coupling to decode/encode or radio control at all, so this gets its
    // own small flag rather than being folded into either of those axes.
    public enum LotwUploadPath { WsjtxDelegated, JimmyTqsl }

    // Self-sufficiency plan, Phase 3: Jimmy's own LoTW upload path, invoking TQSL directly
    // instead of asking WSJT-X to do it (the WM8Q sub-command 16 path, WsjtxCompatibilityExtension.
    // StartUploadLotw -- unchanged default, this is an alternative, not a replacement of it).
    //
    // Command-line syntax verified directly against the installed TQSL 2.8's own documentation
    // (TrustedQSL\help\tqslapp\cmdline.htm), not derived from memory or guesswork:
    //   tqsl -d -u -a compliant -x -l "<Station Location>" "<adif file>"
    // -d: suppress the QSO date-range dialog. -u: upload to LoTW instead of just saving the
    // signed file. -a compliant: skip already-uploaded/out-of-range QSOs, sign the rest (matches
    // the "duplicate is not a failure" handling QrzLogbookClient/HrdLogUploadClient already use).
    // -x: batch mode, terminate after running, status routed to stderr as a parseable
    // "Final Status: Description (Code)" last line.
    //
    // No new Jimmy-side credential: TQSL's own certificate + Station Location setup stays
    // exactly as it is today, configured once inside TQSL itself, outside Jimmy. Jimmy only
    // needs the Station Location *name* to select which one to sign with (-l) -- without it,
    // TQSL would try to show a picker dialog, which can't work in batch (-x) mode. A
    // passphrase-protected certificate is a known limitation: this class never prompts for or
    // stores a TQSL passphrase (-p), so headless operation requires a certificate TQSL doesn't
    // need to unlock, same as any other unattended TQSL invocation.
    public class TqslUploadClient
    {
        public string LastError { get; private set; }

        // Checks the standard install paths first, falling back to a registry uninstall-key
        // lookup -- same "look for it, tolerate absence" shape as
        // WsjtxProtocolAdapter.DetectUdpSettings. Returns null (not an exception) if TQSL isn't
        // installed; callers must degrade to a clear status message, not a crash.
        public static string LocateTqsl()
        {
            string[] standardPaths =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "TrustedQSL", "tqsl.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TrustedQSL", "tqsl.exe"),
            };
            foreach (string p in standardPaths)
                if (File.Exists(p)) return p;

            return LocateViaRegistry();
        }

        private static string LocateViaRegistry()
        {
            try
            {
                string[] uninstallRoots =
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                };
                foreach (string root in uninstallRoots)
                {
                    using (var uninstallKey = Registry.LocalMachine.OpenSubKey(root))
                    {
                        if (uninstallKey == null) continue;
                        foreach (string subKeyName in uninstallKey.GetSubKeyNames())
                        {
                            using (var subKey = uninstallKey.OpenSubKey(subKeyName))
                            {
                                string displayName = subKey?.GetValue("DisplayName") as string;
                                if (displayName == null ||
                                    displayName.IndexOf("TrustedQSL", StringComparison.OrdinalIgnoreCase) < 0)
                                    continue;

                                string installLocation = subKey.GetValue("InstallLocation") as string;
                                if (string.IsNullOrWhiteSpace(installLocation)) continue;
                                string candidate = Path.Combine(installLocation, "tqsl.exe");
                                if (File.Exists(candidate)) return candidate;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Registry access can fail in locked-down environments -- treat exactly like
                // "not found," not a crash.
            }
            return null;
        }

        // Signs and uploads every QSO LogbookDb has marked pending for "LOTW". Builds one ADIF
        // file (AdifExporter.Header() + AdifRecordBuilder.Build() per record -- the same
        // per-record builder CatchUpQrz/CatchUpClubLog/CatchUpHrdLog already use, so this stays
        // consistent with the rest of the upload-catch-up family rather than switching to the
        // separate ID-based export path the Logbook window's manual ADIF export uses instead).
        public async Task<bool> UploadPendingAsync(string stationLocation, LogbookDb db)
        {
            LastError = null;
            if (TestModeGuard.IsTestMode)
            {
                LastError = "Blocked: JIMMY_TEST_DB_PATH is set (test mode) -- no real TQSL invocation allowed.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(stationLocation))
            {
                LastError = "TQSL Station Location is not configured (Options > Logbook Sync).";
                return false;
            }

            string tqslPath = LocateTqsl();
            if (tqslPath == null)
            {
                LastError = "TQSL was not found. Install TrustedQSL, or use the WSJT-X-delegated LoTW upload instead.";
                return false;
            }

            var pending = db.GetPendingUploads("LOTW");
            if (pending.Count == 0) return true;   // nothing to do -- not a failure

            var sb = new StringBuilder();
            sb.Append(AdifExporter.Header());
            foreach (var q in pending)
            {
                sb.Append(AdifRecordBuilder.Build(
                    q.Callsign, q.Band, q.FreqHz, q.Mode, q.QsoDate, q.TimeOn, q.TimeOff,
                    q.RstSent, q.RstRcvd, q.Grid, q.Name, q.Comment, q.TxPwr,
                    q.OperatorCall, q.StationCall, q.MyGrid, q.ExchangeSent, q.ExchangeRcvd));
            }

            string tempFile = Path.Combine(Path.GetTempPath(), $"JimmyLotw_{Guid.NewGuid():N}.adi");
            try
            {
                File.WriteAllText(tempFile, sb.ToString());

                string args = "-d -u -a compliant -x -l " + Quote(stationLocation) + " " + Quote(tempFile);
                var psi = new ProcessStartInfo
                {
                    FileName = tqslPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };

                string stderrText;
                int exitCode;
                using (var proc = Process.Start(psi))
                {
                    // TQSL's own docs: with -x, status is routed to stderr and the process
                    // terminates on its own once done -- no stdin interaction needed. Still
                    // bound the wait so a stuck/hung TQSL (e.g. an unexpected dialog somehow
                    // appearing) can't hang Jimmy's upload catch-up indefinitely.
                    stderrText = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    bool exited = proc.WaitForExit(120_000);
                    if (!exited)
                    {
                        try { proc.Kill(); } catch { }
                        LastError = "TQSL did not finish within 2 minutes (possibly waiting on an unexpected dialog, e.g. a passphrase prompt).";
                        LogFailure("Timeout", LastError, stderrText);
                        return false;
                    }
                    exitCode = proc.ExitCode;
                }

                var (code, description) = ParseFinalStatus(stderrText);

                // Per TQSL's own status-code table: 0 = full success; 8/9/14 mean some or all
                // QSOs were already uploaded/out of range -- not a failure, same "duplicate is
                // handled, not retried forever" treatment QrzLogbookClient/HrdLogUploadClient
                // already give their own duplicate cases.
                if (code == 0 || code == 8 || code == 9 || code == 14)
                {
                    foreach (var q in pending) db.MarkUploaded(q.DedupKey, "LOTW", DateTime.UtcNow);
                    return true;
                }

                LastError = code.HasValue
                    ? $"TQSL reported: {description ?? "no description"} (code {code})."
                    : $"TQSL exited (code {exitCode}) without a parseable status line.";
                LogFailure("Upload failed", LastError, stderrText);
                return false;
            }
            catch (Exception ex)
            {
                LastError = $"Failed to run TQSL: {ex.Message}";
                LogFailure("Process error", LastError, ex.ToString());
                return false;
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                // TQSL also writes a signed .tq8 next to the source file when -u is not
                // combined with -o; with -u present (upload, not save) no .tq8 should be left
                // behind, but clean up defensively in case a partial run did anyway.
                try
                {
                    string tq8 = Path.ChangeExtension(tempFile, ".tq8");
                    if (File.Exists(tq8)) File.Delete(tq8);
                }
                catch { }
            }
        }

        private static string Quote(string s) => "\"" + (s ?? "").Replace("\"", "") + "\"";

        // Parses the last "HH:MM:SS AM/PM Final Status: Description (Code)" line TQSL writes to
        // stderr in batch mode (-x/-q), per cmdline.htm's own documented format. Returns
        // (null, null) if no such line is found -- callers must treat that as a failure, not
        // assume success.
        public static (int? Code, string Description) ParseFinalStatus(string stderrText)
        {
            if (string.IsNullOrEmpty(stderrText)) return (null, null);
            var match = Regex.Match(stderrText, @"Final Status:\s*(.*?)\s*\((\d+)\)\s*$",
                RegexOptions.Multiline | RegexOptions.RightToLeft);
            if (!match.Success) return (null, null);
            string description = match.Groups[1].Value.Trim();
            int.TryParse(match.Groups[2].Value, out int code);
            return (code, description);
        }

        private static void LogFailure(string category, string summary, string detail)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    System.Reflection.Assembly.GetExecutingAssembly().GetName().Name);
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "log_tqsl_errors.txt");

                string entry =
                    Environment.NewLine +
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} TQSL upload failed [{category}]" + Environment.NewLine +
                    $"  {summary}" + Environment.NewLine +
                    "  Full stderr/detail:" + Environment.NewLine +
                    "  " + (detail ?? "").Replace("\n", "\n  ") + Environment.NewLine;

                File.AppendAllText(file, entry);
            }
            catch
            {
                // Logging must never break the upload path.
            }
        }
    }
}
