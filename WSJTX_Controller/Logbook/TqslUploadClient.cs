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
    // Self-sufficiency plan, Phase 3: Jimmy's own LoTW upload path, invoking TQSL directly --
    // native-only, the only LoTW upload mechanism left (there is no external WSJT-X to
    // delegate to anymore).
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
        // lookup. Returns null (not an exception) if TQSL isn't installed; callers must degrade
        // to a clear status message, not a crash.
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
                LastError = "TQSL was not found. Install TrustedQSL to upload to LoTW.";
                return false;
            }

            // Found live, 2026-08-25: GetPendingUploads' default limit (1000), sorted oldest
            // first, silently starves every newer QSO out of this batch once the LoTW-pending
            // backlog exceeds 1000 -- e.g. an expired-then-replaced TQSL certificate that
            // doesn't cover older QSOs (see ClassifyFinalStatus's own comment) can permanently
            // strand 1000+ old QSOs as "pending," and from that point on TQSL never even SEES
            // brand-new same-day QSOs, regardless of whether the current certificate covers
            // them fine -- confirmed live: a QSO made today was completely absent from the
            // batch TQSL received, cut off by the 1000-row cap around a QSO from 2024. A large
            // but bounded limit here (10000, comfortably beyond any realistic backlog) ensures
            // every genuinely pending QSO is at least offered to TQSL each run, not just
            // whichever 1000 happen to sort oldest.
            var pending = db.GetPendingUploads("LOTW", limit: 10000);
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
                    //
                    // Found live, 2026-08-10, auditing against production: the timeout below
                    // used to only guard WaitForExit, AFTER already unconditionally awaiting
                    // ReadToEndAsync() -- but that read only ever completes once the process
                    // closes stderr, which normally happens at exit. A genuinely hung TQSL (the
                    // exact "unexpected dialog" case this timeout exists for) never closes
                    // stderr, so the await above never returned and the 2-minute bound was never
                    // even reached. Racing the read itself against the timeout via Task.WhenAny
                    // (same pattern NativeEngineClient.ListDevices already uses for the identical
                    // class of problem) actually bounds the whole wait, not just half of it.
                    // Release-audit finding, 2026-08-20: RedirectStandardOutput was set above,
                    // but nothing ever read it -- only stderr was drained. If TQSL writes enough
                    // to stdout to fill the OS pipe buffer, the (single-threaded) TQSL process
                    // blocks on that write and can stop writing to stderr too, delaying (though
                    // still bounded by the timeout below) and misattributing the eventual "did
                    // not finish within 2 minutes" error to the wrong cause. Starting this
                    // ReadToEndAsync() call (even without awaiting its result immediately) begins
                    // draining stdout concurrently with stderr below, the same way the read
                    // itself has always started draining stderr concurrently with TQSL running.
                    Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
                    Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
                    Task bothStreamsTask = Task.WhenAll(stderrTask, stdoutTask);
                    // Bumped from 2 to 5 minutes, 2026-08-25: the batch this now signs/uploads
                    // can be far larger since the GetPendingUploads limit above was raised from
                    // 1000 to 10000 for the same reason -- a large but legitimately-pending
                    // backlog needs more real TQSL processing time than a small one, and this
                    // bound must not fire on genuinely-still-working TQSL just because the
                    // batch got bigger.
                    if (await Task.WhenAny(bothStreamsTask, Task.Delay(300_000)).ConfigureAwait(false) != bothStreamsTask)
                    {
                        try { proc.Kill(); } catch { }
                        // Killing the process closes its stdout/stderr handles, so the pending
                        // reads can finish now -- grab whatever partial output TQSL had already
                        // written before being terminated, best-effort only (never let a failure
                        // here mask the real timeout being reported).
                        try { stderrText = await stderrTask.ConfigureAwait(false); } catch { stderrText = ""; }
                        LastError = "TQSL did not finish within 5 minutes (possibly waiting on an unexpected dialog, e.g. a passphrase prompt).";
                        LogFailure("Timeout", LastError, stderrText);
                        return false;
                    }
                    stderrText = stderrTask.Result;
                    bool exited = proc.WaitForExit(5000);
                    exitCode = exited ? proc.ExitCode : -1;
                }

                var (code, description) = ParseFinalStatus(stderrText);

                // Release-audit finding, 2026-08-20 (release blocker): codes 8/9/14 used to be
                // treated identically to 0 (full success) and blanket-marked the ENTIRE `pending`
                // batch as uploaded. Verified directly against the installed TQSL 2.8's own
                // documentation (TrustedQSL\help\tqslapp\cmdline.htm, "Status Information" table)
                // -- codes 8 and 9 are BOTH explicitly documented as "...already uploaded OR OUT
                // OF DATE RANGE" (TQSL's own wording conflates two very different outcomes into
                // one aggregate code: a QSO already on LoTW is fine to mark, but one outside the
                // certificate's valid date range was NEVER uploaded at all). TQSL's batch/status
                // output gives no reliable per-record breakdown to tell which QSOs in THIS batch
                // hit which case -- see this method's own example in cmdline.htm: a single run
                // reporting code 9 skipped 414 records and signed only 1, with no per-record
                // detail available here to know which of Jimmy's own `pending` QSOs was the one
                // that actually went through. Blanket-marking on 8/9/14 could permanently mark a
                // QSO "uploaded" that was never actually sent to LoTW -- a QSO whose date falls
                // outside a renewed certificate's validity window is a real, not rare, scenario
                // -- with no UI anywhere in Jimmy that ever resets lotw_uploaded_at to retry it.
                // Only code 0 ("success: all qsos submitted were signed and saved or signed and
                // uploaded") is unambiguous. On 8/9/14, this now leaves every QSO in `pending`
                // unmarked -- TQSL still does the real signing/upload work for whichever ones
                // were genuinely new (per cmdline.htm's own -a compliant description: only
                // already-uploaded/out-of-range QSOs are skipped, everything else IS signed), so
                // no real LoTW submission is lost; those QSOs are simply reported "already
                // uploaded" (code 8, safe to skip) on the next retry instead of possibly-wrongly
                // "already handled" now. This trades a merely cosmetic "pending count doesn't
                // shrink as fast" for ruling out a silent, effectively permanent data-integrity
                // false positive.
                switch (ClassifyFinalStatus(code))
                {
                    case FinalStatusOutcome.MarkAllUploaded:
                        foreach (var q in pending) db.MarkUploaded(q.DedupKey, "LOTW", DateTime.UtcNow);
                        return true;

                    case FinalStatusOutcome.AmbiguousLeaveUnmarked:
                        LastError = $"TQSL reported: {description ?? "no description"} (code {code}) -- " +
                            "some QSOs may not have actually been uploaded (already-uploaded and out-of-date-range " +
                            "QSOs are reported the same way); none were marked uploaded this run, so they will be " +
                            "retried, or confirmed as already uploaded, next time.";
                        LogFailure("Partial/ambiguous status -- not marked uploaded", LastError, stderrText);
                        return true;

                    default:
                        LastError = code.HasValue
                            ? $"TQSL reported: {description ?? "no description"} (code {code})."
                            : $"TQSL exited (code {exitCode}) without a parseable status line.";
                        LogFailure("Upload failed", LastError, stderrText);
                        return false;
                }
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

        public enum FinalStatusOutcome
        {
            // Only code 0 ("success: all qsos submitted were signed and saved or signed and
            // uploaded", cmdline.htm) -- every QSO in the batch is safe to mark uploaded.
            MarkAllUploaded,
            // Codes 8/9/14 -- TQSL's own documentation conflates "already uploaded" (safe to
            // treat as already handled) with "out of date range" (never actually uploaded) in
            // the SAME aggregate code, with no per-record breakdown available to tell which
            // QSOs in this batch hit which case. Not a failure (TQSL ran and did real,
            // partially-successful work), but nothing here should be blanket-marked uploaded.
            AmbiguousLeaveUnmarked,
            // Anything else: a real TQSL failure (cancelled, rejected, connection error, no
            // parseable status line at all, ...).
            Failure,
        }

        // Release-audit finding, 2026-08-20 (release blocker): extracted into its own pure,
        // synchronously-testable function (matching main.rs's validate_set_frequency's own
        // reason for existing as a separate function -- the same audit pass) -- this decision
        // used to be inlined directly in UploadPendingAsync with no unit coverage at all, and
        // codes 8/9/14 used to be blanket-treated the same as 0 (see UploadPendingAsync's own
        // comment for the full TQSL-documentation-verified reasoning on why that was wrong).
        public static FinalStatusOutcome ClassifyFinalStatus(int? code)
        {
            if (code == 0) return FinalStatusOutcome.MarkAllUploaded;
            if (code == 8 || code == 9 || code == 14) return FinalStatusOutcome.AmbiguousLeaveUnmarked;
            return FinalStatusOutcome.Failure;
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
