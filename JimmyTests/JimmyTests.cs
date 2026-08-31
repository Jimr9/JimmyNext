using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WsjtxUdpLib.Messages.Out;
using WSJTX_Controller;

// Unit tests for Jimmy's WSJT-X message parser.
//
// Tests WsjtxMessage static classifier methods and the AP-suffix strip logic
// that runs inside DecodeMessage.Parse() / EnqueueDecodeMessage.Parse().
// No UDP, no network, no WSJT-X or Jimmy process needed.
//
// Run via test.bat (builds Jimmy.exe first, then builds and runs this).
// In all examples, MY_CALL = "KB0UZT".
// FT8 message format: [DESTINATION] [SOURCE] [payload]
//   e.g. "KB0UZT K4YT EM63" means K4YT is calling KB0UZT.

static class JimmyTests
{
    const string MY_CALL = "KB0UZT";
    const string THEIR_CALL = "K4YT";

    static int passed;
    static int failed;
    static int skipped;

    // Release-audit finding, 2026-08-20: StartStubEngineHost's three callers used to just
    // Console.WriteLine("  SKIP  ...") and `return;` directly, with no counter -- test.bat's
    // final "ALL TESTS PASSED" banner was indistinguishable from a run where these tests'
    // real assertions silently never executed at all (e.g. because a real Jimmy/engine-host
    // session already owned the control port on the developer's machine). A skip is still not
    // a FAIL (the code isn't known broken -- this is an environment collision, not a defect),
    // but it must never blend into an unqualified "all clean" signal either. See Main()'s own
    // final summary for how this is surfaced.
    static void Skip(string label, string reason)
    {
        Console.WriteLine($"  SKIP  {label}: {reason}");
        skipped++;
    }

    // Release-audit finding, 2026-08-21: RetuneBand/ToggleTuningProcess/SetOperatingMode's
    // DirectSendCommand call moved off the UI thread onto a background Task, with only the
    // resulting state update marshaled back via ctrl.BeginInvoke -- tests that call a real
    // production entry point (BandUp/BandDown/SelectBand/SelectFrequency) now need to pump this
    // process's own WinForms message queue for that BeginInvoke continuation to actually run,
    // the same way it would inside Jimmy's real Application.Run() loop. Bounded by timeoutMs so
    // a genuine bug (the awaited state never actually lands) fails the assertion below instead
    // of hanging the test run forever.
    static void PumpUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            System.Windows.Forms.Application.DoEvents();
            System.Threading.Thread.Sleep(5);
        }
    }

    static void Check(string label, bool actual, bool expected)
    {
        if (actual == expected)
        {
            Console.WriteLine($"  PASS  {label}");
            passed++;
        }
        else
        {
            Console.WriteLine($"  FAIL  {label}: expected {expected}, got {actual}");
            failed++;
        }
    }

    static void CheckStr(string label, string actual, string expected)
    {
        bool ok = (actual == expected);
        if (ok)
        {
            Console.WriteLine($"  PASS  {label}");
            passed++;
        }
        else
        {
            string a = actual == null ? "<null>" : $"'{actual}'";
            string e = expected == null ? "<null>" : $"'{expected}'";
            Console.WriteLine($"  FAIL  {label}: expected {e}, got {a}");
            failed++;
        }
    }

    // Replicates the AP-suffix stripping from DecodeMessage.Parse() and
    // EnqueueDecodeMessage.Parse() — tested here independently so failures
    // are caught before checking downstream classifiers.
    static string StripApSuffix(string msg)
    {
        if (msg == null) return null;
        // Old WSJT-X 2.x AP format: " ?"
        int idx = msg.IndexOf(" ?");
        if (idx != -1)
            msg = msg.Substring(0, idx).TrimEnd();
        // WSJT-X 3.0 AP format: trailing " a<digits>" e.g. " a35"
        int i = msg.Length - 1;
        while (i >= 0 && char.IsDigit(msg[i])) i--;
        if (i < msg.Length - 1 && i >= 1 && msg[i] == 'a' && msg[i - 1] == ' ')
            msg = msg.Substring(0, i - 1).TrimEnd();
        return msg;
    }

    [STAThread]
    static void Main(string[] args)
    {
        // Found chasing intermittent PumpUntil timeouts in the Direct-dispatcher tests, 2026-08-21:
        // ~1000 tests in one process, many constructing their own WsjtxClient, means a great many
        // short-lived background Tasks (each Direct dispatcher's own worker, WaitAsync-based since
        // the earlier thread-pinning fix -- see WsjtxClient.Direct.cs's own comment) get scheduled
        // in bursts. The CLR thread pool only grows by about one thread per ~500ms-1s when demand
        // outpaces supply, so a late test's very first EnqueueDirectCommand can occasionally wait
        // behind that growth-rate limit rather than any real slowness in the code being tested --
        // confirmed by the failure being purely intermittent and disappearing whenever the suite
        // isn't under this much simultaneous load. SetMinThreads pre-warms the pool once, up front,
        // so it never needs to grow mid-run for a burst like this again. A generous but bounded
        // floor (test-process-only; production Jimmy never runs anywhere near this many
        // concurrent WsjtxClient instances) -- this fixes the actual root cause, not just a wider
        // per-test timeout.
        System.Threading.ThreadPool.SetMinThreads(64, 64);

        if (args.Length > 0 && args[0] == "--verify-clublog")
        {
            VerifyClubLogEquivalence();
            return;
        }
        if (args.Length > 0 && args[0] == "--dxcc-shadow-dump")
        {
            DxccShadowDump();
            return;
        }
        if (args.Length > 0 && args[0] == "--echo-argv")
        {
            // Test-only escape hatch for EscapeCommandLineArgRoundTripsThroughRealWindowsArgvTests
            // -- proves NativeEngineClient.EscapeCommandLineArg's output survives a REAL round
            // trip through Process/CommandLineToArgvW argument splitting, not just a
            // hand-verified expected-string check of the escaping algorithm in isolation.
            for (int i = 1; i < args.Length; i++) Console.WriteLine(args[i]);
            return;
        }
        // 2026-08-23: dev-only convenience for iterating on ONE test method without paying the
        // full ~1125-test suite's runtime on every change (CLAUDE.md/session guidance: use
        // focused tests during implementation, run the broad suite once at a sensible final
        // checkpoint) -- reflection-invokes a single named static test method by exact name
        // instead of the whole Main() list below. Not used by test.bat/run_parser_tests.bat
        // (both invoke JimmyTests.exe with no args), so normal full-suite runs are unaffected.
        if (args.Length > 1 && args[0] == "--only")
        {
            var m = typeof(JimmyTests).GetMethod(args[1],
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (m == null) { Console.WriteLine($"No such test method: {args[1]}"); Environment.Exit(2); return; }
            m.Invoke(null, null);
            Console.WriteLine($"\n{passed} passed, {failed} failed, {skipped} skipped.");
            Environment.Exit(failed > 0 ? 1 : 0);
            return;
        }

        Console.WriteLine("=== Jimmy Parser Unit Tests ===");
        Console.WriteLine($"  WsjtxMessage static classifiers + AP strip logic");
        Console.WriteLine($"  myCall in examples = {MY_CALL}");
        Console.WriteLine();

        ApStripTests();
        ReportTests();
        FinalAckTests();
        CqTests();
        ContestTests();
        ToFromCallTests();
        ReplyTests();
        InvalidTypeTests();
        ApChainTests();
        SlashCallNoCountryTests();
        FoxHoundTests();
        HrcEnumTests();
        HrcCacheTests();
        RuleUniverseBuiltInTests();
        RuleUniverseClubLogTests();
        ClubLogBigCtyResolutionTests();
        RuleEngineCoreTests();
        RuleEngineDateRangeTests();
        RuleEngineBandIndependenceTests();
        RuleEngineWorkedBandsTests();
        RuleEngineCountTargetStillNeededTests();
        AdifRecordBuilderTests();
        AdifExporterTests();
        LogbookDbEditLogTests();
        LogbookDbAuthoritativeSourceOverrideTests();
        LogbookDbNewlyConfirmedVsCorrectedTests();
        LogbookDbDownloadMarksUploadedTests();
        Colonies13RosterRegressionTest();
        CallQueueRankerCategoryTierTests();
        CallQueueRankerSortMethodTests();
        CallQueueRankerTieBreakTests();
        CallQueueRankerCategoryWeightValidationTests();
        CallQueueRankerCallingPrioritiesTests();
        CallQueueRankerBeamRankTests();
        JimmySettingsRoundTripTests();
        JimmySettingsDefaultsTests();
        EngineRestartPolicyTests();
        NativeEngineClientGridValidationTests();
        UpdateCheckerDownloadHostValidationTests();
        UpdateCheckerNotesTests();
        NativeEngineClientDescribeConfigProblemTests();
        NativeEngineClientTxWatchdogFormulaTests();
        OtaSpotAnnotatorTests();
        EqslReconcileTests();
        LookupManagerPrimaryProviderTests();
        LookupManagerDisposeQuiescenceTests();
        LookupManagerOfflineClassificationTests();
        FindPreservedSelectionIndexTests();
        ResolveDispatchIndexTests();
        SpotWatchCallsRoundTripTests();
        BandAppliesToLiveTagTests();
        RuleEngineFixedBandRestrictionTests();
        AwardMatcherMatchTests();
        AwardMatcherAlreadyWorkedGateTests();
        RuleEngineResolveBandsForEvaluationTests();
        RuleEngineBandChoicesForTests();
        RuleEngineBandOverrideIntersectEndToEndTests();
        RowFormatterBuildOrderedRowTests();
        ParseRowOrderTests();
        HotkeyConfigNewActionConflictTests();
        LogbookDbUploadSyncStatusTests();
        QrzIsDuplicateReasonTests();
        HrdLogClassifyResponseTests();
        RigctldClientListRigModelsTests();
        RigctldClientBoundedReadTests();
        OptionsDlgSystemDefaultDeviceLabelTests();
        OptionsDlgExtractRigModelIdTests();
        TqslParseFinalStatusTests();
        TqslClassifyFinalStatusTests();
        ResolveUsStateTests();
        StateSetContainsTests();
        AdifImporterLiveLoggedStateFallbackTests();
        AdifImporterBackfillsMissingDxccTests();
        AdifImportMixedValidErrorRetainsValidRowsTests();
        DxSpotWatcherIsEvenPeriodTests();
        FccUlsProviderParseLineTests();
        FccUlsProviderShouldPreferNameTests();
        FccUlsProviderLooksIncompleteTests();
        ClassificationEngineTests();
        GeoMathTests();
        GeoMathEllipsoidCrossValidationTests();
        A6ClassificationParityTests();
        DirectModePlumbingParityTests();
        DirectRunawayRr73HaltsEngineTests();
        DirectLogRetryAndEarlyRrrTests();
        DirectRr73BeforeRogerDecodeHoldsCallInProgTests();
        DirectDecodeNormalizationTests();
        DirectFailedWriteRetryIsBoundedTests();
        StartupStatusMessageTests();
        OptionsDlgConstructionTests();
        AudioTuningHotkeyTests();
        MeterReadingHintTests();
        SetOperatingModeFailureDoesNotChangeLocalModeTests();
        TxLevelPerBandRestoreTests();
        DirectPathTxLevelBandTrackingTests();
        DirectPathTxEnabledReconciliationTests();
        DirectPathPendingBandIdxClearedOnConfirmationTests();
        RetuneBandFailureDoesNotLeakPendingBandIdxTests();
        SelectFrequencyHotkeyModeStaysPutTests();
        FrequencyEntrySidebandTests();
        SelectFrequencyHotkeySendsConfiguredSidebandTests();
        RigModeMismatchClassificationTests();
        RigModeMismatchGraceWindowAndReconciliationTests();
        TimeoutSettingClampedOnLoadTests();
        DiagnosticLogRetentionTests();
        DebugOutputLogWriteFailureTests();
        OtaSpotsWindowFormatStatusTests();
        OtaSpotsWindowRowFormattingTests();
        SpaceWxJsonDeserializationTests();
        FormatNoaaScaleTests();
        SpaceWxMufAndScalesJsonDeserializationTests();
        ClubLogPrefixTableTests();
        EnqueueDecodeMessageFromStandardDecodeTests();
        DecodeMessageIsCallToTests();
        DefaultTrPeriodMsTests();
        NotificationTemplateEngineTests();
        NotificationSettingsDefaultsTests();
        NotificationSettingsRoundTripTests();
        NotificationDedupThrottleTests();
        NotificationCenterPublishTests();
        UiaAlertNotificationDeliveryTests();
        NotificationTemplateComponentParserTests();
        NotificationVariableRegistryTests();
        NotificationDefaultsAllTemplatesValidTests();
        NotificationPolicyExtendedFieldsTests();
        NotificationCenterDeferredDeliveryTests();
        NotificationParkedEventTypesGuardTests();
        ClockSyncNotificationTests();
        ClockSyncDirectPathStateHygieneTests();
        DirectTxHoldSafetyNetTests();
        DirectPollFailureNotificationTests();
        HaltPurgesQueuedTxArmCommandTests();
        HaltAbortsInFlightCommandTests();
        HaltConfirmsStoppedStateViaFollowUpSnapshotTests();
        HaltDoesNotConfirmWhenStillTransmittingTests();
        RejectedReplyPreservesQueuedStationTests();
        RxTxFrequencyModeReplyTests();
        EmergencyHaltTxConfirmationTests();
        FailedManualTxOffsetPreservesBestFreeTests();
        RapidFrequencyNudgesAccumulateTests();
        SessionTokenAuthenticationTests();
        RepeatLimitActivelyStopsTxTests();
        CompletedQsoRemovesStaleQueueStateTests();
        BandSessionLocationSurvivesGridlessMessageTests();
        ConfirmedBandChangeFlushesStaleTxStateTests();
        DirectBandChangeRebuildsAwardCacheForNewBandTests();
        DelayedReplyAfterBandChangeDoesNotResurrectStaleQsoTests();
        DelayedReplyAfterOperatorAbortDoesNotResurrectStaleQsoTests();
        FailedQsoWriteDoesNotFalselyAnnounceSuccessTests();
        DirectInitialConnectAlwaysRestoresLastExactDialTests();
        DirectInitialConnectResyncsTierAndPeriodTests();
        RepeatLimitStopsBeforeTheDisallowedAttemptKeysTests();
        ToggleTxFirstActuallyTogglesTests();
        OptimizeReducesOnlyUntilReportExchangedTests();
        RawDecodesIngestsEveryDecodeBothModesTests();
        RawDecodesSideLabelReflectsTxFirstTests();
        FinalQsoLoggedAndSendingAnnounceTogetherTests();
        ReportClockStatusTests();
        SuppressReceiveNotificationsDuringTxTests();
        ResolveActiveIniPathTests();
        ActiveIniFilePathTests();
        ListNamedProfilesTests();
        BuildWorkingFrequencyEntriesTests();
        DirectSetWorkingFrequenciesSendsCorrectCommandTests();
        EscapeCommandLineArgRoundTripsThroughRealWindowsArgvTests();
        PowerShellSingleQuoteLiteralRoundTripsThroughRealPowerShellTests();
        BeginnerModeOnlyAccessibilityTests();
        CrashLoggerTests();

        Console.WriteLine();
        Console.WriteLine($"=== {passed} passed, {failed} failed, {skipped} skipped ===");
        if (failed > 0)
        {
            Console.WriteLine("SOME TESTS FAILED");
            Environment.Exit(1);
        }
        else if (skipped > 0)
        {
            // Deliberately NOT "ALL TESTS PASSED" -- see Skip()'s own comment. Exit code stays
            // 0 (a skip is an environment collision, not a known defect), but the banner itself
            // must never read as an unqualified clean run when real assertions didn't execute.
            Console.WriteLine($"TESTS PASSED, BUT {skipped} TEST(S) WERE SKIPPED -- see SKIP lines above");
        }
        else
        {
            Console.WriteLine("ALL TESTS PASSED");
        }
    }

    static void ApStripTests()
    {
        Console.WriteLine("── AP Suffix Stripping ──");
        CheckStr("no suffix: unchanged",
            StripApSuffix($"{MY_CALL} {THEIR_CALL} -05"),
                          $"{MY_CALL} {THEIR_CALL} -05");
        CheckStr("a35 stripped from report",
            StripApSuffix($"{MY_CALL} {THEIR_CALL} -05 a35"),
                          $"{MY_CALL} {THEIR_CALL} -05");
        CheckStr("a1 stripped from CQ+grid",
            StripApSuffix($"CQ {THEIR_CALL} EM63 a1"),
                          $"CQ {THEIR_CALL} EM63");
        CheckStr("a35 stripped from grid reply",
            StripApSuffix($"{MY_CALL} {THEIR_CALL} EM63 a35"),
                          $"{MY_CALL} {THEIR_CALL} EM63");
        CheckStr("a35 stripped from FD exchange",
            StripApSuffix($"{MY_CALL} {THEIR_CALL} 2A MO a35"),
                          $"{MY_CALL} {THEIR_CALL} 2A MO");
        CheckStr("old-format ' ?' stripped",
            StripApSuffix($"{MY_CALL} {THEIR_CALL} +00  ? a3"),
                          $"{MY_CALL} {THEIR_CALL} +00");
        // Grid EM63 ends in digits but the char before the trailing digits is 'M', not 'a'
        CheckStr("grid EM63: digit-suffix guard prevents strip",
            StripApSuffix($"CQ {THEIR_CALL} EM63"),
                          $"CQ {THEIR_CALL} EM63");
        CheckStr("73 message: unchanged",
            StripApSuffix($"{MY_CALL} {THEIR_CALL} 73"),
                          $"{MY_CALL} {THEIR_CALL} 73");
        CheckStr("empty string: unchanged",
            StripApSuffix(""), "");
        // Short AP suffix " a2" (1-digit): same stripping rule
        CheckStr("a2 stripped from report",
            StripApSuffix($"{MY_CALL} {THEIR_CALL} -04 a2"),
                          $"{MY_CALL} {THEIR_CALL} -04");
        // Old-format long-space hybrid without '?': e.g. WSJT-X 3.0-rc1 early builds
        // "KB0UZT K4YT -04                      a2" — many spaces, no '?', but ' a2' at end
        CheckStr("long-space a2 (no '?') stripped",
            StripApSuffix($"{MY_CALL} {THEIR_CALL} -04                      a2"),
                          $"{MY_CALL} {THEIR_CALL} -04");
    }

    static void ReportTests()
    {
        Console.WriteLine("\n── Signal Reports ──");
        Check("IsReport: negative dB",       WsjtxMessage.IsReport($"{MY_CALL} {THEIR_CALL} -05"), true);
        Check("IsReport: positive dB",       WsjtxMessage.IsReport($"{MY_CALL} {THEIR_CALL} +05"), true);
        Check("IsReport: -12",               WsjtxMessage.IsReport($"{MY_CALL} {THEIR_CALL} -12"), true);
        Check("IsReport: R-05 is NOT",       WsjtxMessage.IsReport($"{MY_CALL} {THEIR_CALL} R-05"), false);
        Check("IsReport: grid is NOT",       WsjtxMessage.IsReport($"{MY_CALL} {THEIR_CALL} EM63"), false);
        Check("IsReport: 73 is NOT",         WsjtxMessage.IsReport($"{MY_CALL} {THEIR_CALL} 73"), false);

        Check("IsRogerReport: R-05",         WsjtxMessage.IsRogerReport($"{MY_CALL} {THEIR_CALL} R-05"), true);
        Check("IsRogerReport: R+12",         WsjtxMessage.IsRogerReport($"{MY_CALL} {THEIR_CALL} R+12"), true);
        Check("IsRogerReport: -05 is NOT",   WsjtxMessage.IsRogerReport($"{MY_CALL} {THEIR_CALL} -05"), false);
    }

    static void FinalAckTests()
    {
        Console.WriteLine("\n── 73 / RR73 / RRR ──");
        Check("Is73: 73",                       WsjtxMessage.Is73($"{MY_CALL} {THEIR_CALL} 73"), true);
        Check("Is73: RR73 is NOT Is73",         WsjtxMessage.Is73($"{MY_CALL} {THEIR_CALL} RR73"), false);
        Check("IsRR73: RR73",                   WsjtxMessage.IsRR73($"{MY_CALL} {THEIR_CALL} RR73"), true);
        Check("IsRR73: 73 is NOT IsRR73",       WsjtxMessage.IsRR73($"{MY_CALL} {THEIR_CALL} 73"), false);
        Check("Is73orRR73: 73",                 WsjtxMessage.Is73orRR73($"{MY_CALL} {THEIR_CALL} 73"), true);
        Check("Is73orRR73: RR73",               WsjtxMessage.Is73orRR73($"{MY_CALL} {THEIR_CALL} RR73"), true);
        Check("Is73orRR73: -05 is NOT",         WsjtxMessage.Is73orRR73($"{MY_CALL} {THEIR_CALL} -05"), false);
        Check("IsRogers: RRR",                  WsjtxMessage.IsRogers($"{MY_CALL} {THEIR_CALL} RRR"), true);
        Check("IsRogers: 73 is NOT",            WsjtxMessage.IsRogers($"{MY_CALL} {THEIR_CALL} 73"), false);
        Check("IsRogers: RR73 is NOT",          WsjtxMessage.IsRogers($"{MY_CALL} {THEIR_CALL} RR73"), false);
    }

    static void CqTests()
    {
        Console.WriteLine("\n── CQ Types ──");
        Check("IsCQ: plain CQ with grid",    WsjtxMessage.IsCQ($"CQ {THEIR_CALL} EM63"), true);
        Check("IsCQ: CQ no grid",            WsjtxMessage.IsCQ($"CQ {THEIR_CALL}"), true);
        Check("IsCQ: directed POTA",         WsjtxMessage.IsCQ($"CQ POTA {THEIR_CALL}"), true);
        Check("IsCQ: directed SOTA",         WsjtxMessage.IsCQ($"CQ SOTA {THEIR_CALL}"), true);
        Check("IsCQ: directed DX with grid", WsjtxMessage.IsCQ($"CQ DX {THEIR_CALL} EM63"), true);
        Check("IsCQ: directed NA",           WsjtxMessage.IsCQ($"CQ NA {THEIR_CALL}"), true);
        Check("IsCQ: non-CQ is NOT",         WsjtxMessage.IsCQ($"{MY_CALL} {THEIR_CALL} -05"), false);

        Check("IsPota: POTA CQ",                  WsjtxMessage.IsPota($"CQ POTA {THEIR_CALL}"), true);
        Check("IsPota: plain CQ is NOT POTA",     WsjtxMessage.IsPota($"CQ {THEIR_CALL} EM63"), false);
        Check("IsSota: SOTA CQ",                  WsjtxMessage.IsSota($"CQ SOTA {THEIR_CALL}"), true);
        Check("IsSota: plain CQ is NOT SOTA",     WsjtxMessage.IsSota($"CQ {THEIR_CALL} EM63"), false);

        CheckStr("DirectedTo: POTA",   WsjtxMessage.DirectedTo($"CQ POTA {THEIR_CALL}"), "POTA");
        CheckStr("DirectedTo: SOTA",   WsjtxMessage.DirectedTo($"CQ SOTA {THEIR_CALL}"), "SOTA");
        CheckStr("DirectedTo: DX",     WsjtxMessage.DirectedTo($"CQ DX {THEIR_CALL} EM63"), "DX");
        CheckStr("DirectedTo: NA",     WsjtxMessage.DirectedTo($"CQ NA {THEIR_CALL}"), "NA");
        CheckStr("DirectedTo: plain CQ → null",
                                       WsjtxMessage.DirectedTo($"CQ {THEIR_CALL} EM63"), null);
    }

    static void ContestTests()
    {
        Console.WriteLine("\n── Contest / Field Day ──");
        Check("IsContest: FD 2A MO to me",       WsjtxMessage.IsContest($"{MY_CALL} {THEIR_CALL} 2A MO"), true);
        Check("IsContest: FD R 2A MO to me",     WsjtxMessage.IsContest($"{MY_CALL} {THEIR_CALL} R 2A MO"), true);
        Check("IsContest: FD 2A MO to other",    WsjtxMessage.IsContest($"{THEIR_CALL} K9AVT 559 TX"), true);
        Check("IsContest: 559 TX",               WsjtxMessage.IsContest($"{MY_CALL} {THEIR_CALL} 559 TX"), true);
        Check("IsContest: R 559 TX",             WsjtxMessage.IsContest($"{MY_CALL} {THEIR_CALL} R 559 TX"), true);
        Check("IsContest: 559 0021",             WsjtxMessage.IsContest($"{MY_CALL} {THEIR_CALL} 559 0021"), true);
        Check("IsContest: CQ RU",                WsjtxMessage.IsContest($"CQ RU {THEIR_CALL}"), true);
        Check("IsContest: CQ TEST",              WsjtxMessage.IsContest($"CQ TEST {THEIR_CALL}"), true);
        Check("IsContest: plain report is NOT",  WsjtxMessage.IsContest($"{MY_CALL} {THEIR_CALL} -05"), false);
        Check("IsContest: 73 is NOT",            WsjtxMessage.IsContest($"{MY_CALL} {THEIR_CALL} 73"), false);
        Check("IsContest: CQ plain is NOT",      WsjtxMessage.IsContest($"CQ {THEIR_CALL} EM63"), false);
    }

    static void ToFromCallTests()
    {
        Console.WriteLine("\n── ToCall / DeCall ──");
        // FT8 format: [DESTINATION] [SOURCE] [payload]
        // "KB0UZT K4YT EM63" = K4YT calling KB0UZT
        CheckStr("ToCall: station calling me",   WsjtxMessage.ToCall($"{MY_CALL} {THEIR_CALL} EM63"), MY_CALL);
        CheckStr("DeCall: station calling me",   WsjtxMessage.DeCall($"{MY_CALL} {THEIR_CALL} EM63"), THEIR_CALL);
        CheckStr("ToCall: CQ",                   WsjtxMessage.ToCall($"CQ {THEIR_CALL} EM63"), "CQ");
        CheckStr("DeCall: CQ",                   WsjtxMessage.DeCall($"CQ {THEIR_CALL} EM63"), THEIR_CALL);
        CheckStr("ToCall: directed CQ POTA",     WsjtxMessage.ToCall($"CQ POTA {THEIR_CALL}"), "CQ");
        CheckStr("DeCall: directed CQ POTA",     WsjtxMessage.DeCall($"CQ POTA {THEIR_CALL}"), THEIR_CALL);
        CheckStr("ToCall: contest to me",        WsjtxMessage.ToCall($"{MY_CALL} {THEIR_CALL} 2A MO"), MY_CALL);
        CheckStr("ToCall: contest to other",     WsjtxMessage.ToCall($"{THEIR_CALL} K9AVT 559 TX"), THEIR_CALL);
    }

    static void ReplyTests()
    {
        Console.WriteLine("\n── IsReply / IsShortReply ──");
        Check("IsReply: grid",                   WsjtxMessage.IsReply($"{MY_CALL} {THEIR_CALL} EM63"), true);
        Check("IsReply: report is NOT",          WsjtxMessage.IsReply($"{MY_CALL} {THEIR_CALL} -05"), false);
        Check("IsReply: CQ is NOT",              WsjtxMessage.IsReply($"CQ {THEIR_CALL} EM63"), false);
        Check("IsShortReply: 2 words",           WsjtxMessage.IsShortReply($"{MY_CALL} {THEIR_CALL}"), true);
        Check("IsShortReply: 3 words is NOT",    WsjtxMessage.IsShortReply($"{MY_CALL} {THEIR_CALL} EM63"), false);
        Check("IsShortReply: CQ is NOT",         WsjtxMessage.IsShortReply($"CQ {THEIR_CALL}"), false);
    }

    static void InvalidTypeTests()
    {
        Console.WriteLine("\n── IsInvalidType ──");
        Check("IsInvalidType: report is valid",      WsjtxMessage.IsInvalidType($"{MY_CALL} {THEIR_CALL} -05"), false);
        Check("IsInvalidType: grid is valid",        WsjtxMessage.IsInvalidType($"{MY_CALL} {THEIR_CALL} EM63"), false);
        Check("IsInvalidType: 73 is valid",          WsjtxMessage.IsInvalidType($"{MY_CALL} {THEIR_CALL} 73"), false);
        Check("IsInvalidType: CQ is valid",          WsjtxMessage.IsInvalidType($"CQ {THEIR_CALL} EM63"), false);
        Check("IsInvalidType: garbage IS invalid",   WsjtxMessage.IsInvalidType($"{MY_CALL} {THEIR_CALL} GARBAGE"), true);
        // Un-stripped AP suffix makes the type unrecognizable
        Check("IsInvalidType: a35 before strip IS invalid",
              WsjtxMessage.IsInvalidType($"{MY_CALL} {THEIR_CALL} -05 a35"), true);
        // After stripping, classifier correctly recognizes it
        Check("IsInvalidType: a35 after strip is valid",
              WsjtxMessage.IsInvalidType(StripApSuffix($"{MY_CALL} {THEIR_CALL} -05 a35")), false);
    }

    // Regression: slash callsigns with no country must parse correctly.
    // Covers the bug where AddSelectedCall hard-rejected calls with Country=="".
    static void SlashCallNoCountryTests()
    {
        Console.WriteLine("\n── Slash Callsign / Unknown Country ──");
        // Parser must recognise "CQ W5C/H" as a valid CQ from callsign W5C/H
        Check("IsCQ: CQ W5C/H",            WsjtxMessage.IsCQ("CQ W5C/H"), true);
        CheckStr("DeCall: CQ W5C/H",       WsjtxMessage.DeCall("CQ W5C/H"), "W5C/H");
        Check("IsInvalidType: CQ W5C/H",   WsjtxMessage.IsInvalidType("CQ W5C/H"), false);
        // WsjtxCountry must return "" for null or empty — never throw
        CheckStr("WsjtxCountry: null → empty",   EnqueueDecodeMessage.WsjtxCountry(null), "");
        CheckStr("WsjtxCountry: empty → empty",  EnqueueDecodeMessage.WsjtxCountry(""), "");

        // Real cases pulled directly from a live A6 parity-diagnostic session
        // 2026-07-16/17: Club Log's own <name> is ALL CAPS, and now that Club Log is
        // the primary Country source (Stage A6 field testing), that's what actually
        // reaches WsjtxCountry -- must normalize to WSJT-X's own display convention
        // instead of passing the ALL-CAPS string through unchanged.
        CheckStr("WsjtxCountry: USA abbreviation",
            EnqueueDecodeMessage.WsjtxCountry("UNITED STATES OF AMERICA"), "USA");
        CheckStr("WsjtxCountry: Germany short name",
            EnqueueDecodeMessage.WsjtxCountry("FEDERAL REPUBLIC OF GERMANY"), "Germany");
        CheckStr("WsjtxCountry: European Russia abbreviation",
            EnqueueDecodeMessage.WsjtxCountry("EUROPEAN RUSSIA"), "EU Russia");
        CheckStr("WsjtxCountry: Asiatic Turkey abbreviation",
            EnqueueDecodeMessage.WsjtxCountry("ASIATIC TURKEY"), "AS Turkey");
        // General title-case fallback for everything without a specific override --
        // including the "of"/"the"/"and" minor-word cases that a naive per-word
        // capitalize-everything approach would get wrong.
        CheckStr("WsjtxCountry: simple one-word name",
            EnqueueDecodeMessage.WsjtxCountry("BELGIUM"), "Belgium");
        CheckStr("WsjtxCountry: minor word 'of' stays lowercase",
            EnqueueDecodeMessage.WsjtxCountry("ISLE OF MAN"), "Isle of Man");
        CheckStr("WsjtxCountry: two-word name, no minor words",
            EnqueueDecodeMessage.WsjtxCountry("CZECH REPUBLIC"), "Czech Republic");
        CheckStr("WsjtxCountry: minor word 'of' not at start stays lowercase",
            EnqueueDecodeMessage.WsjtxCountry("DOMINICAN REPUBLIC"), "Dominican Republic");
        // Distinct, real (non-deleted) Club Log entities like ALASKA/GUANTANAMO BAY
        // must NOT be special-cased away into USA here -- WsjtxCountry only
        // reformats casing/abbreviations, it must never change WHICH entity a name
        // refers to (that's ClassificationEngine/ClubLogProvider's job).
        CheckStr("WsjtxCountry: distinct entity name preserved, only case changes",
            EnqueueDecodeMessage.WsjtxCountry("ALASKA"), "Alaska");
        CheckStr("WsjtxCountry: distinct entity name preserved, only case changes (2)",
            EnqueueDecodeMessage.WsjtxCountry("GUANTANAMO BAY"), "Guantanamo Bay");
    }

    // IsFoxHound() is a suffix heuristic only — /H may be a Hound callsign OR a
    // legitimate portable suffix. SpecialOperationMode in StatusMessage is the
    // authoritative source. These tests verify the heuristic rule, not blocking.
    static void FoxHoundTests()
    {
        Console.WriteLine("\n── Possible F/H Detection (suffix heuristic, not authoritative) ──");
        Check("Possible F/H: CQ from /H call",          WsjtxMessage.IsFoxHound("CQ W5C/H"),                     true);
        Check("Possible F/H: CQ /H with grid",           WsjtxMessage.IsFoxHound("CQ W5C/H EM63"),                true);
        Check("Possible F/H: /H report to me",           WsjtxMessage.IsFoxHound($"{MY_CALL} W5C/H -03"),         true);
        Check("Possible F/H: /H 73 to me",               WsjtxMessage.IsFoxHound($"{MY_CALL} W5C/H 73"),          true);
        Check("Possible F/H: /H RR73 to me",             WsjtxMessage.IsFoxHound($"{MY_CALL} W5C/H RR73"),        true);
        Check("Possible F/H: to-call is /H",             WsjtxMessage.IsFoxHound($"W5C/H {MY_CALL} RR73"),        true);
        // Normal FT8 must NOT be flagged as possible F/H
        Check("Possible F/H: normal CQ is NOT",          WsjtxMessage.IsFoxHound($"CQ {THEIR_CALL} EM63"),        false);
        Check("Possible F/H: normal report is NOT",      WsjtxMessage.IsFoxHound($"{MY_CALL} {THEIR_CALL} -03"),  false);
        Check("Possible F/H: normal 73 is NOT",          WsjtxMessage.IsFoxHound($"{MY_CALL} {THEIR_CALL} 73"),   false);
        // Other slash-suffix portable calls must NOT be flagged as possible F/H
        Check("Possible F/H: /P call is NOT",            WsjtxMessage.IsFoxHound($"CQ W5C/P EM63"),               false);
        Check("Possible F/H: /M call is NOT",            WsjtxMessage.IsFoxHound($"CQ W5C/M"),                    false);
        // Safety: null and empty input
        Check("Possible F/H: null is NOT",               WsjtxMessage.IsFoxHound(null),                           false);
        Check("Possible F/H: empty is NOT",              WsjtxMessage.IsFoxHound(""),                             false);
    }

    // ── HRC filter enum values ────────────────────────────────────────────────
    // Verify the four new CallCategory values have the expected integer assignments.
    // If any of these fail, DeriveCategory / AddSelectedCall routing is broken.
    static void HrcEnumTests()
    {
        Console.WriteLine("\n── HRC CallCategory Enum Values ──");
        // Existing values must be unchanged — regression guard
        Check("DEFAULT == 0",             (int)WsjtxClient.CallCategory.DEFAULT             == 0,  true);
        Check("ALWAYS_WANTED == 8",       (int)WsjtxClient.CallCategory.ALWAYS_WANTED       == 8,  true);
        // New HRC values
        Check("WAS_NEEDED == 9",          (int)WsjtxClient.CallCategory.WAS_NEEDED          == 9,  true);
        Check("WAS_UNCONFIRMED == 10",    (int)WsjtxClient.CallCategory.WAS_UNCONFIRMED     == 10, true);
        Check("DXCC_UNCONFIRMED == 11",   (int)WsjtxClient.CallCategory.DXCC_UNCONFIRMED    == 11, true);
        Check("ZONE_NEEDED == 12",        (int)WsjtxClient.CallCategory.ZONE_NEEDED         == 12, true);
    }

    // ── HRC cache SQL logic ───────────────────────────────────────────────────
    // Creates a throwaway SQLite database, inserts known QSOs, calls LoadHrcCache(),
    // and verifies the three output HashSets are computed correctly.
    // No network access, no WSJT-X, no real HRC data path involved.
    static void HrcCacheTests()
    {
        Console.WriteLine("\n── HRC Cache (LoadHrcCache SQL logic) ──");
        string tmpDb = Path.Combine(Path.GetTempPath(),
            "JimmyTest_HRC_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = new LogbookDb(tmpDb))
            {
                // TX confirmed via LoTW → TX must NOT be in neededStates or unconfirmedStates
                InsertQso(db, "W5TX",   "TX", dxcc: 100, zone: 4, lotwRcvd: "Y");
                // CA worked but unconfirmed → CA MUST be in unconfirmedStates, NOT neededStates
                // (never-worked and worked-unconfirmed are now a WAS/DXCC-parity split, not one bucket)
                InsertQso(db, "W6CA",   "CA", dxcc: 100, zone: 3);
                // WY: never worked at all → WY MUST be in neededStates (no QSO inserted)

                // DXCC 100 has a confirmed QSO (W5TX) → 100 must NOT be in unconfirmedDxcc
                // DXCC 200 worked, never confirmed → 200 MUST be in unconfirmedDxcc
                InsertQso(db, "OE1TST", "  ", dxcc: 200, zone: 15);
                // DXCC 300 confirmed → 300 must NOT be in unconfirmedDxcc
                InsertQso(db, "VK2TST", "  ", dxcc: 300, zone: 29, lotwRcvd: "Y");

                // Zone 3 confirmed (VE3TST) → zone 3 must NOT be in neededZones
                InsertQso(db, "VE3TST", "ON", dxcc: 400, zone: 3,  lotwRcvd: "Y");
                // Zone 5 worked but unconfirmed → zone 5 MUST be in neededZones
                InsertQso(db, "W0TST",  "CO", dxcc: 100, zone: 5);
                // Zone 20: never worked → zone 20 MUST be in neededZones

                HashSet<string> neededStates;
                HashSet<string> unconfirmedStates;
                HashSet<int>    unconfirmedDxcc;
                HashSet<int>    neededZones;
                db.LoadHrcCache(out neededStates, out unconfirmedStates, out unconfirmedDxcc, out neededZones);

                // ── States ──────────────────────────────────────────────────
                // Worked states in this fixture: TX (confirmed), CA (unconfirmed),
                // and CO (unconfirmed, via the W0TST zone-5 QSO below) — all three
                // move out of neededStates now that it means "never worked".
                Check("neededStates: TX confirmed → NOT in set",     neededStates.Contains("TX"), false);
                Check("neededStates: CA worked/unconfirmed → NOT in set (moved to unconfirmedStates)",
                      neededStates.Contains("CA"), false);
                Check("neededStates: CO worked/unconfirmed → NOT in set (moved to unconfirmedStates)",
                      neededStates.Contains("CO"), false);
                Check("neededStates: WY (no QSO) → in set",          neededStates.Contains("WY"), true);
                Check("neededStates: count ≤ 50",                    neededStates.Count <= 50,    true);
                // TX, CA, and CO are all no longer "never worked", so 47 states remain needed
                Check("neededStates: count == 47",                   neededStates.Count == 47,    true);
                // DC must never appear — it is not a state
                Check("neededStates: DC never present",              neededStates.Contains("DC"), false);

                // ── States unconfirmed ────────────────────────────────────────
                Check("unconfirmedStates: TX confirmed → NOT in set", unconfirmedStates.Contains("TX"), false);
                Check("unconfirmedStates: CA worked/unconfirmed → in set", unconfirmedStates.Contains("CA"), true);
                Check("unconfirmedStates: CO worked/unconfirmed → in set", unconfirmedStates.Contains("CO"), true);
                Check("unconfirmedStates: WY never worked → NOT in set",   unconfirmedStates.Contains("WY"), false);
                Check("unconfirmedStates: count == 2",                     unconfirmedStates.Count == 2,     true);

                // ── DXCC unconfirmed ─────────────────────────────────────────
                // DXCC 100 has a confirmed QSO → NOT unconfirmed
                Check("unconfirmedDxcc: DXCC 100 has confirmed → NOT in set",
                      unconfirmedDxcc.Contains(100), false);
                // DXCC 200: worked, no confirmation → IS unconfirmed
                Check("unconfirmedDxcc: DXCC 200 worked/unconfirmed → in set",
                      unconfirmedDxcc.Contains(200), true);
                // DXCC 300: confirmed → NOT unconfirmed
                Check("unconfirmedDxcc: DXCC 300 confirmed → NOT in set",
                      unconfirmedDxcc.Contains(300), false);

                // ── Zones ────────────────────────────────────────────────────
                // Zone 3: VE3TST confirmed → NOT needed
                Check("neededZones: zone 3 confirmed (VE3TST) → NOT in set", neededZones.Contains(3),  false);
                // Zone 4: W5TX confirmed → NOT needed (W5TX has zone=4, lotwRcvd='Y')
                Check("neededZones: zone 4 confirmed (W5TX) → NOT in set",   neededZones.Contains(4),  false);
                Check("neededZones: zone 5 unconfirmed → in set",             neededZones.Contains(5),  true);
                Check("neededZones: zone 20 (no QSO) → in set",              neededZones.Contains(20), true);
                // Zone 29: VK2TST confirmed → NOT needed
                Check("neededZones: zone 29 confirmed (VK2TST) → NOT in set", neededZones.Contains(29), false);
                Check("neededZones: count ≤ 40",                               neededZones.Count <= 40,  true);
                // Zones 3, 4, and 29 confirmed → 40 - 3 = 37 zones needed
                Check("neededZones: count == 37",                              neededZones.Count == 37,  true);
                // Zone 41 must never be added — only zones 1-40 are valid
                Check("neededZones: zone 41 never present",                   neededZones.Contains(41), false);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  HrcCacheTests threw: {ex.GetType().Name}: {ex.Message}");
            failed++;
        }
        finally
        {
            try { File.Delete(tmpDb); } catch { }
        }
    }

    // ── eQSL reconciliation: LogbookDb.TryMarkEqslConfirmed + EqslReconciler ───
    // Conservative match-only reconciliation against EXISTING qso rows -- never creates a
    // row, never guesses an ambiguous match, never clears eqsl_qsl_rcvd once set. No network:
    // exercises the offline matching/parsing logic only (real eQSL transport is EngineHost's,
    // untestable here -- same reasoning as every other network provider in this suite).
    static void EqslReconcileTests()
    {
        Console.WriteLine("\n── eQSL reconciliation (TryMarkEqslConfirmed + EqslReconciler) ──");
        string tmpDb = Path.Combine(Path.GetTempPath(),
            "JimmyTest_Eqsl_" + Guid.NewGuid().ToString("N") + ".db");
        string tmpDb2 = Path.Combine(Path.GetTempPath(),
            "JimmyTest_Eqsl2_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = new LogbookDb(tmpDb))
            {
                InsertQso(db, "W1AW", "CT", dxcc: 291, zone: 5, band: "20m", qsoDate: "20241201");
                InsertQso(db, "OK7AN", "", dxcc: 503, zone: 15, band: "40m", qsoDate: "20241205");

                Check("Exact call+band+date match -> Matched",
                    db.TryMarkEqslConfirmed("W1AW", "20m", "20241201", null) == LogbookDb.EqslReconcileOutcome.Matched, true);
                Check("Re-marking the same QSO -> AlreadyConfirmed (idempotent, no error)",
                    db.TryMarkEqslConfirmed("W1AW", "20m", "20241201", null) == LogbookDb.EqslReconcileOutcome.AlreadyConfirmed, true);

                // +/-1 day tolerance: the QSO is dated 20241205, an eQSL record dated one day
                // either side must still match (midnight-boundary clock-skew tolerance).
                Check("Date one day BEFORE the QSO date still matches (+/-1 day window)",
                    db.TryMarkEqslConfirmed("OK7AN", "40m", "20241204", "FT8") == LogbookDb.EqslReconcileOutcome.Matched, true);

                Check("Unknown callsign -> Unmatched (never invents a row)",
                    db.TryMarkEqslConfirmed("ZZ1NOPE", "20m", "20241201", null) == LogbookDb.EqslReconcileOutcome.Unmatched, true);
                Check("Right callsign, wrong band -> Unmatched",
                    db.TryMarkEqslConfirmed("W1AW", "40m", "20241201", null) == LogbookDb.EqslReconcileOutcome.Unmatched, true);
                Check("Date more than 1 day away -> Unmatched",
                    db.TryMarkEqslConfirmed("W1AW", "20m", "20241210", null) == LogbookDb.EqslReconcileOutcome.Unmatched, true);

                // Ambiguity: two DISTINCT QSO rows (different time_on -> different dedup_key)
                // sharing the same callsign+band+date, with no mode given to disambiguate --
                // must be left alone, not guessed.
                InsertQso(db, "W1AW", "CT", dxcc: 291, zone: 5, band: "15m", qsoDate: "20241215", timeOn: "1200");
                InsertQso(db, "W1AW", "CT", dxcc: 291, zone: 5, band: "15m", qsoDate: "20241215", timeOn: "1800");
                Check("Two equally-plausible candidates, no mode to disambiguate -> Ambiguous",
                    db.TryMarkEqslConfirmed("W1AW", "15m", "20241215", null) == LogbookDb.EqslReconcileOutcome.Ambiguous, true);

                // Unparseable date -> Unmatched, not an exception and not a wildcard match.
                Check("Malformed QSO_DATE -> Unmatched, not an exception",
                    db.TryMarkEqslConfirmed("W1AW", "20m", "not-a-date", null) == LogbookDb.EqslReconcileOutcome.Unmatched, true);
            }

            // End-to-end via EqslReconciler.Reconcile against a small synthetic ADIF InBox --
            // proves the ADIF-record -> match-call shape works, including that a record
            // WITHOUT EQSL_QSL_RCVD=Y (e.g. a pending/unconfirmed entry) is skipped, not treated
            // as a confirmation. Own fresh database file -- the block above already left
            // confirmations on tmpDb, which would make an absolute EqslConfirmedQsos() count
            // here misleading.
            using (var db = new LogbookDb(tmpDb2))
            {
                InsertQso(db, "N0CALL", "MO", dxcc: 291, zone: 4, band: "20m", qsoDate: "20241220");

                string adif =
                    "ADIF 3 Export from eQSL.cc\n<PROGRAMID:21>eQSL.cc DownloadInBox <ADIF_Ver:5>3.1.6 <EOH>\n" +
                    "<CALL:6>N0CALL <BAND:3>20m <MODE:3>FT8 <QSO_DATE:8>20241220 <EQSL_QSL_RCVD:1>Y <EOR>\n" +
                    "<CALL:6>ZZ9NUL <BAND:3>20m <MODE:3>FT8 <QSO_DATE:8>20241220 <EQSL_QSL_RCVD:1>N <EOR>\n";

                var result = EqslReconciler.Reconcile(db, adif);
                Check("Reconcile: confirmed record matches -> Matched == 1", result.Matched == 1, true);
                Check("Reconcile: EQSL_QSL_RCVD != Y record is skipped, not counted as Unmatched",
                    result.Skipped == 1, true);
                Check("Reconcile: unconfirmed record does not touch the confirmed count",
                    result.Unmatched == 0, true);
                Check("Reconcile: eqsl_qsl_rcvd actually persisted",
                    db.EqslConfirmedQsos() == 1, true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  EqslReconcileTests threw: {ex.GetType().Name}: {ex.Message}");
            failed++;
        }
        finally
        {
            try { File.Delete(tmpDb); } catch { }
            try { File.Delete(tmpDb2); } catch { }
        }
    }

    // ── LookupManager: primary provider selection (QRZ vs HamQTH) ──────────────
    // Offline/cache-only -- no real QRZ/HamQTH network traffic. Proves the provider-selection
    // plumbing itself (PrimaryProvider resolution, CanAutoQueue/PrimaryNeedsLookup routing,
    // and Build()'s passive-contribution merge order), not the providers' own network calls.
    static void LookupManagerPrimaryProviderTests()
    {
        Console.WriteLine("\n── LookupManager: primary provider selection ──");
        var manager = new LookupManager();
        try
        {
            Check("Default PrimaryProvider is QRZ (existing installations unchanged)",
                ReferenceEquals(manager.PrimaryProvider, manager.Qrz), true);

            manager.Initialize(
                useLookupData: true,
                qrzEnabled: true, qrzUser: "testuser", qrzPass: "testpass", qrzCacheDays: 7,
                lotwEnabled: false, lotwDays: 30,
                clubLogAppKey: "", clubLogDays: 30,
                fccUlsEnabled: false,
                policy: QrzLookupPolicy.Disabled, qrzMinIntervalSeconds: 10,
                primaryProvider: CallsignLookupProvider.HamQth,
                hamQthEnabled: true, hamQthUser: "hamuser", hamQthPass: "hampass", hamQthCacheDays: 7);

            Check("After selecting HamQth, PrimaryProvider is HamQth",
                ReferenceEquals(manager.PrimaryProvider, manager.HamQth), true);
            Check("QRZ provider itself is still independently enabled (lookup choice != log-upload choice)",
                manager.Qrz.IsEnabled, true);
            Check("HamQth provider is enabled",
                manager.HamQth.IsEnabled, true);

            // Switch back to QRZ -- proves this isn't a one-way/init-only choice.
            manager.Initialize(
                useLookupData: true,
                qrzEnabled: true, qrzUser: "testuser", qrzPass: "testpass", qrzCacheDays: 7,
                lotwEnabled: false, lotwDays: 30,
                clubLogAppKey: "", clubLogDays: 30,
                fccUlsEnabled: false,
                policy: QrzLookupPolicy.Disabled, qrzMinIntervalSeconds: 10,
                primaryProvider: CallsignLookupProvider.Qrz,
                hamQthEnabled: true, hamQthUser: "hamuser", hamQthPass: "hampass", hamQthCacheDays: 7);
            Check("Switching CallsignLookupProvider back to Qrz updates PrimaryProvider",
                ReferenceEquals(manager.PrimaryProvider, manager.Qrz), true);

            // CanAutoQueue must route through whichever is primary, not always QRZ.
            manager.Initialize(
                useLookupData: true,
                qrzEnabled: false, qrzUser: "", qrzPass: "", qrzCacheDays: 7,
                lotwEnabled: false, lotwDays: 30,
                clubLogAppKey: "", clubLogDays: 30,
                fccUlsEnabled: false,
                policy: QrzLookupPolicy.UnidentifiedQueue, qrzMinIntervalSeconds: 10,
                primaryProvider: CallsignLookupProvider.HamQth,
                hamQthEnabled: true, hamQthUser: "hamuser", hamQthPass: "hampass", hamQthCacheDays: 7);
            Check("CanAutoQueue is true when HamQth is primary and enabled, even with QRZ disabled",
                manager.CanAutoQueue("W1AW"), true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  LookupManagerPrimaryProviderTests threw: {ex.GetType().Name}: {ex.Message}");
            failed++;
        }
        finally
        {
            manager.Dispose();
        }
    }

    // ── Background shutdown / quiescence (independent audit finding, 2026-08-23): LookupManager.
    // Dispose() must stop the auto-lookup timer, clear OnLookupCompleted (so nothing keeps the
    // Controller/UI closure it captured alive, and so a callback firing after Dispose is
    // impossible even if reached), and tolerate being called more than once ──
    static void LookupManagerDisposeQuiescenceTests()
    {
        Console.WriteLine("\n── Background shutdown / quiescence: LookupManager.Dispose() clears its callback and is idempotent -- THE FIX ──");
        var manager = new LookupManager();
        try
        {
            int completedCount = 0;
            manager.OnLookupCompleted = () => completedCount++;
            manager.Initialize(
                useLookupData: true,
                qrzEnabled: true, qrzUser: "testuser", qrzPass: "testpass", qrzCacheDays: 7,
                lotwEnabled: false, lotwDays: 30,
                clubLogAppKey: "", clubLogDays: 30,
                fccUlsEnabled: false,
                policy: QrzLookupPolicy.UnidentifiedQueue, qrzMinIntervalSeconds: 10,
                primaryProvider: CallsignLookupProvider.Qrz,
                hamQthEnabled: false, hamQthUser: null, hamQthPass: null, hamQthCacheDays: 7);
            Check("Setup: OnLookupCompleted is set before Dispose",
                manager.OnLookupCompleted != null, true);

            manager.Dispose();

            Check("THE FIX: Dispose() clears OnLookupCompleted -- nothing keeps its captured Controller/UI closure alive past shutdown",
                manager.OnLookupCompleted == null, true);

            bool secondDisposeThrew = false;
            try { manager.Dispose(); }
            catch { secondDisposeThrew = true; }
            Check("THE FIX: Dispose() is idempotent -- a second call (CloseComm's own defensive shape) does not throw",
                secondDisposeThrew, false);

            Check("...and OnLookupCompleted was never actually invoked by any of this (nothing left to race)",
                completedCount == 0, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  LookupManagerDisposeQuiescenceTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── LookupManager.BuildOffline / ClassificationEngine offline classification ──
    // Regression test for the fresh-install bug: with "Use Lookup Data" OFF, Country/
    // Continent/DXCC classification (New DXCC, DXCC Unconfirmed, Zone Needed, country,
    // continent) must still resolve from Jimmy's own offline Club Log/Big CTY data --
    // that setting only gates the optional account-backed providers (QRZ/LoTW/FccUls/
    // HamQth). Root cause was ClassificationEngine/AwardTagger gating their entire
    // LookupManager.Build(call) call behind LookupManager.Enabled (useLookupData &&
    // some provider IsEnabled), which meant NO lookup data at all -- not even
    // ClubLog's -- reached classification whenever useLookupData was false. Uses
    // TestFixtureLookupProvider (deterministic, no real ClubLog network/cache needed)
    // standing in for ClubLog, same technique A6ClassificationParityTests uses.
    static void LookupManagerOfflineClassificationTests()
    {
        Console.WriteLine("\n── LookupManager.BuildOffline / offline classification (useLookupData=false) ──");
        string tmpDb = Path.Combine(Path.GetTempPath(),
            "JimmyTest_OfflineClassification_" + Guid.NewGuid().ToString("N") + ".db");
        var manager = new LookupManager();
        try
        {
            manager.RegisterProviderFirst(new TestFixtureLookupProvider());
            manager.Initialize(
                useLookupData: false,
                qrzEnabled: false, qrzUser: null, qrzPass: null, qrzCacheDays: 7,
                lotwEnabled: false, lotwDays: 30,
                clubLogAppKey: null, clubLogDays: 30,
                fccUlsEnabled: false);

            Check("useLookupData=false: LookupManager.Enabled is false (master switch honored)",
                manager.Enabled, false);

            var offlineRec = manager.BuildOffline("K4YT");
            CheckStr("BuildOffline still resolves Country despite Enabled=false", offlineRec.Country, "USA");
            CheckStr("BuildOffline still resolves Continent despite Enabled=false", offlineRec.Continent, "NA");
            Check("BuildOffline still resolves Dxcc despite Enabled=false", offlineRec.Dxcc == 291, true);

            using (var db = new LogbookDb(tmpDb))
            {
                var engine = new ClassificationEngine(db, manager);

                var classified = engine.Classify("K4YT", "20m");
                CheckStr("Classify: Country resolves with useLookupData off", classified.Country, "USA");
                CheckStr("Classify: Continent resolves with useLookupData off", classified.Continent, "NA");
                Check("Classify: IsNewCountry true (never worked) with useLookupData off",
                    classified.IsNewCountry, true);
                Check("Classify: IsNewCountryOnBand true (never worked) with useLookupData off",
                    classified.IsNewCountryOnBand, true);

                InsertQso(db, "K4YT", "TN", dxcc: 291, zone: 4, band: "20m");
                var afterWorked = engine.Classify("K4YT", "20m");
                Check("Classify: IsNewCountry false once DXCC 291 worked, still useLookupData off",
                    afterWorked.IsNewCountry, false);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  LookupManagerOfflineClassificationTests threw: {ex.GetType().Name}: {ex.Message}");
            failed++;
        }
        finally
        {
            manager.Dispose();
            try { File.Delete(tmpDb); } catch { }
        }
    }

    // ── LogbookDb.GetUploadSyncStatus: pending count + last-upload time ────────
    // Backs the Sync Status section on the My Log tab -- must correctly report
    // "still pending" vs "already uploaded" per service, independently of the
    // other service's upload column.
    static void LogbookDbUploadSyncStatusTests()
    {
        Console.WriteLine("\n── LogbookDb.GetUploadSyncStatus ──");
        string tmpDb = Path.Combine(Path.GetTempPath(),
            "JimmyTest_UploadSync_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = new LogbookDb(tmpDb))
            {
                InsertQso(db, "W1AW", "CT", dxcc: 291, zone: 5);
                InsertQso(db, "W2AW", "NY", dxcc: 291, zone: 5);
                string keyW1AW = AdifImporter.BuildDedupKey("W1AW", "20m", "FT8", "20241201", "1200");

                // Neither QSO uploaded yet to either service.
                var qrzBefore = db.GetUploadSyncStatus("QRZ");
                Check("before any upload: QRZ pending count == 2",     qrzBefore.PendingCount == 2, true);
                Check("before any upload: QRZ uploaded count == 0",    qrzBefore.UploadedCount == 0, true);
                Check("before any upload: QRZ last upload time null",  qrzBefore.LastUploadUtc.HasValue, false);

                var when = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
                db.MarkUploaded(keyW1AW, "QRZ", when);

                var qrzAfter = db.GetUploadSyncStatus("QRZ");
                Check("after marking W1AW uploaded: QRZ pending count == 1", qrzAfter.PendingCount == 1, true);
                Check("after marking W1AW uploaded: QRZ uploaded count == 1", qrzAfter.UploadedCount == 1, true);
                Check("after marking W1AW uploaded: QRZ last upload time set",
                      qrzAfter.LastUploadUtc.HasValue && qrzAfter.LastUploadUtc.Value == when, true);

                // Club Log status must be unaffected by the QRZ-only mark.
                var clubLogAfter = db.GetUploadSyncStatus("CLUBLOG");
                Check("QRZ mark does not affect Club Log pending count", clubLogAfter.PendingCount == 2, true);
                Check("QRZ mark does not affect Club Log uploaded count", clubLogAfter.UploadedCount == 0, true);
                Check("QRZ mark does not affect Club Log last upload time",
                      clubLogAfter.LastUploadUtc.HasValue, false);

                // LOTW and HRDLOG: regression coverage for the 2026-08-07 fix -- both
                // GetPendingUploads("LOTW")/("HRDLOG") and MarkUploaded(..., "LOTW"/"HRDLOG", ...)
                // used to throw ArgumentException (UploadColumn had no case for either), silently
                // caught by every real caller (TqslUploadClient, LiveQsoUploadOrchestrator,
                // WsjtxClient.Uploads.cs's CatchUpHrdLog) -- so neither service's upload ever
                // actually got recorded locally, even when the real upload itself succeeded.
                var lotwBefore = db.GetUploadSyncStatus("LOTW");
                Check("before any upload: LOTW pending count == 2", lotwBefore.PendingCount == 2, true);
                var hrdLogBefore = db.GetUploadSyncStatus("HRDLOG");
                Check("before any upload: HRDLOG pending count == 2", hrdLogBefore.PendingCount == 2, true);

                db.MarkUploaded(keyW1AW, "LOTW", when);
                var lotwAfter = db.GetUploadSyncStatus("LOTW");
                Check("after marking W1AW uploaded: LOTW pending count == 1", lotwAfter.PendingCount == 1, true);
                Check("after marking W1AW uploaded: LOTW uploaded count == 1", lotwAfter.UploadedCount == 1, true);

                // LOTW mark must not affect HRDLOG, or either of QRZ/Club Log from above.
                Check("LOTW mark does not affect HRDLOG pending count",
                      db.GetUploadSyncStatus("HRDLOG").PendingCount == 2, true);
                Check("LOTW mark does not affect QRZ pending count",
                      db.GetUploadSyncStatus("QRZ").PendingCount == 1, true);
                Check("LOTW mark does not affect Club Log pending count",
                      db.GetUploadSyncStatus("CLUBLOG").PendingCount == 2, true);

                db.MarkUploaded(keyW1AW, "HRDLOG", when);
                var hrdLogAfter = db.GetUploadSyncStatus("HRDLOG");
                Check("after marking W1AW uploaded: HRDLOG pending count == 1", hrdLogAfter.PendingCount == 1, true);
                Check("after marking W1AW uploaded: HRDLOG uploaded count == 1", hrdLogAfter.UploadedCount == 1, true);

                var lotwPending = db.GetPendingUploads("LOTW");
                Check("LOTW GetPendingUploads returns the one still-pending QSO", lotwPending.Count == 1, true);
                CheckStr("LOTW GetPendingUploads pending QSO is W2AW", lotwPending[0].Callsign, "W2AW");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  LogbookDbUploadSyncStatusTests threw: {ex.GetType().Name}: {ex.Message}");
            failed++;
        }
        finally
        {
            try { File.Delete(tmpDb); } catch { }
        }
    }

    // ── ClassificationEngine (migration Stage A1) ───────────────────────────────
    // Independently-derived counterpart to EnqueueDecodeMessage's wire-supplied
    // IsNewCallOnBand/IsNewCallAnyBand fields, from Jimmy's own LogbookDb --
    // parallel-validation only, nothing downstream reads ClassificationEngine yet.
    //
    // Country/Continent/IsNewCountry/IsNewCountryOnBand need a live-enabled
    // LookupManager (real QRZ/Club Log provider data) to resolve a DXCC entity
    // for the DE station; a fresh LookupManager defaults to disabled (no network
    // calls made), so those fields aren't exercised here -- covered separately by
    // replay-capture comparisons and the existing --verify-clublog tooling. The
    // LogbookDb.HasWorkedDxcc query itself (the part ClassificationEngine would
    // call once a DXCC number is available) is still exercised directly below.
    static void ClassificationEngineTests()
    {
        Console.WriteLine("\n── ClassificationEngine (Stage A1) ──");
        string tmpDb = Path.Combine(Path.GetTempPath(),
            "JimmyTest_Classification_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = new LogbookDb(tmpDb))
            {
                var engine = new ClassificationEngine(db, lookupManager: null);

                // Nobody worked yet: everything is "new".
                var neverWorked = engine.Classify("W1AW", "20m");
                Check("never-worked call: IsNewCallOnBand", neverWorked.IsNewCallOnBand, true);
                Check("never-worked call: IsNewCallAnyBand", neverWorked.IsNewCallAnyBand, true);
                CheckStr("never-worked call: Country empty (no LookupManager)", neverWorked.Country, "");
                Check("never-worked call: IsNewCountry false (no DXCC resolvable)", neverWorked.IsNewCountry, false);

                InsertQso(db, "W1AW", "CT", dxcc: 291, zone: 5, band: "20m");

                var sameBand = engine.Classify("W1AW", "20m");
                Check("worked on 20m, asked about 20m: IsNewCallOnBand false", sameBand.IsNewCallOnBand, false);
                Check("worked on 20m, asked about 20m: IsNewCallAnyBand false", sameBand.IsNewCallAnyBand, false);

                var otherBand = engine.Classify("W1AW", "40m");
                Check("worked on 20m, asked about 40m: IsNewCallOnBand true", otherBand.IsNewCallOnBand, true);
                Check("worked on 20m, asked about 40m: IsNewCallAnyBand still false", otherBand.IsNewCallAnyBand, false);

                var differentCall = engine.Classify("W2XYZ", "20m");
                Check("different, never-worked call still new", differentCall.IsNewCallOnBand, true);

                var caseInsensitive = engine.Classify("w1aw", "20m");
                Check("call lookup is case-insensitive", caseInsensitive.IsNewCallOnBand, false);

                var unknownBand = engine.Classify("W1AW", null);
                Check("null current band: IsNewCallOnBand defaults true (conservative)", unknownBand.IsNewCallOnBand, true);
                Check("null current band: IsNewCallAnyBand unaffected", unknownBand.IsNewCallAnyBand, false);

                Check("empty call: no crash, defaults all false/empty",
                      engine.Classify("", "20m").IsNewCallOnBand, false);
                Check("null call: no crash, defaults all false/empty",
                      engine.Classify(null, "20m").IsNewCallOnBand, false);

                // Underlying DXCC worked-before query (what Classify() would call once a
                // DXCC entity is resolved) -- exercised directly since LookupManager can't
                // be driven deterministically offline.
                Check("HasWorkedDxcc: entity 291 worked (any band)", db.HasWorkedDxcc(291, null), true);
                Check("HasWorkedDxcc: entity 291 worked on 20m", db.HasWorkedDxcc(291, "20m"), true);
                Check("HasWorkedDxcc: entity 291 NOT worked on 40m", db.HasWorkedDxcc(291, "40m"), false);
                Check("HasWorkedDxcc: different entity NOT worked", db.HasWorkedDxcc(999, null), false);
                Check("HasWorkedDxcc: dxcc <= 0 always false", db.HasWorkedDxcc(0, null), false);

                // -- IsDx/Azimuth/Distance (Stage A6 addition) --
                // No LookupManager here (same constraint as the Country/Continent checks
                // above), so Continent is always unresolved -- IsDx must stay conservatively
                // false regardless of myContinent, exactly like IsNewCountry's "can't
                // classify, don't guess" behavior. The true/DX branch is exercised at the
                // consumer/replay level, where LookupManager has real provider data.
                var noGridOrMsg = engine.Classify("W1AW", "20m", myGrid: "FN42", myContinent: "NA");
                Check("IsDx false when Continent unresolved (no LookupManager)", noGridOrMsg.IsDx, false);
                Check("Azimuth -1 (unknown) when no grid available at all", noGridOrMsg.Azimuth == -1, true);
                Check("Distance -1 (unknown) when no grid available at all", noGridOrMsg.Distance == -1, true);

                // Message-embedded grid does NOT require LookupManager -- WsjtxMessage.Grid()
                // is pure string parsing. Cross-checked against GeoMath directly (oracle-style,
                // same approach as GeoMathTests below) rather than a hardcoded expected number.
                var expected = GeoMath.DistanceAndAzimuth("FN42", "EM63");
                var withMsgGrid = engine.Classify("K4YT", "20m", decodedMessage: "CQ K4YT EM63", myGrid: "FN42");
                Check("message-grid path: Distance matches GeoMath oracle",
                      expected.HasValue && withMsgGrid.Distance == (int)Math.Round(expected.Value.distanceKm), true);
                Check("message-grid path: Azimuth matches GeoMath oracle",
                      expected.HasValue && withMsgGrid.Azimuth == (int)Math.Round(expected.Value.azimuthDeg) % 360, true);

                // A report/73 message never carries a grid, and there's no LookupManager
                // fallback here -- must stay -1, not silently reuse a stale/wrong grid.
                var reportMsg = engine.Classify("K4YT", "20m", decodedMessage: "KB0UZT K4YT -05", myGrid: "FN42");
                Check("no grid in report-type message: Distance stays -1", reportMsg.Distance == -1, true);
                Check("no grid in report-type message: Azimuth stays -1", reportMsg.Azimuth == -1, true);

                // myGrid missing (unknown local grid): must not crash, stays -1 even though
                // the message itself has a grid.
                var noMyGrid = engine.Classify("K4YT", "20m", decodedMessage: "CQ K4YT EM63", myGrid: null);
                Check("no myGrid: Distance stays -1 (no crash)", noMyGrid.Distance == -1, true);
                Check("no myGrid: Azimuth stays -1 (no crash)", noMyGrid.Azimuth == -1, true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  ClassificationEngineTests threw: {ex.GetType().Name}: {ex.Message}");
            failed++;
        }
        finally
        {
            try { File.Delete(tmpDb); } catch { }
        }
    }

    // ── GeoMath (migration Stage A2) ────────────────────────────────────────────
    // Maidenhead grid -> lat/lon -> great-circle bearing/distance -- the one piece
    // of EnqueueDecodeMessage's wire-supplied fields (Azimuth, Distance) that is
    // genuinely new code for Jimmy (no prior great-circle math existed). Parallel-
    // validation only -- CallQueueRanker.cs does not read GeoMath yet.
    //
    // Distance/azimuth values below are checked against independently derivable
    // geometry (1 degree of latitude is ~111.2 km; a quarter of Earth's
    // circumference along the equator is R*(pi/2)), not against the module's own
    // arithmetic, so these are real oracle checks, not tautologies.
    static void GeoMathTests()
    {
        Console.WriteLine("\n── GeoMath ──");

        // Grid parsing: JJ00 sits at the intersection of the equator and prime
        // meridian by Maidenhead convention. A bare 4-character grid has no
        // sub-square letters, and WSJT-X's own grid2deg.f90 defaults the missing
        // pair to "mm" rather than resolving to the true geometric center of the
        // 2x1 degree square -- GridToLatLon replicates that exactly (Stage-A6-
        // geodesic-match, 2026-07-16), landing ~1.25'/2.5' off the box center.
        var jj00 = GeoMath.GridToLatLon("JJ00");
        Check("JJ00 parses (non-null)", jj00.HasValue, true);
        if (jj00.HasValue)
        {
            Check("JJ00 lat within 0.6 of equator", Math.Abs(jj00.Value.lat - 0.520833) < 0.001, true);
            Check("JJ00 lon within 0.6 of prime meridian", Math.Abs(jj00.Value.lon - 1.041667) < 0.001, true);
        }

        Check("malformed grid (1 char) returns null", GeoMath.GridToLatLon("A").HasValue, false);
        Check("malformed grid (3 chars) returns null", GeoMath.GridToLatLon("JJ0").HasValue, false);
        Check("null grid returns null", GeoMath.GridToLatLon(null).HasValue, false);
        Check("empty grid returns null", GeoMath.GridToLatLon("").HasValue, false);
        Check("out-of-range field letter returns null", GeoMath.GridToLatLon("ZZ00").HasValue, false);

        // Lowercase input must parse the same as uppercase (grids are case-insensitive
        // in practice -- WSJT-X and most logging tools accept either).
        var lower = GeoMath.GridToLatLon("jj00");
        Check("lowercase grid parses same as uppercase",
              lower.HasValue && jj00.HasValue &&
              Math.Abs(lower.Value.lat - jj00.Value.lat) < 0.0001 &&
              Math.Abs(lower.Value.lon - jj00.Value.lon) < 0.0001, true);

        // Pure formula checks (direct lat/lon, independent of grid parsing). These
        // target the Clarke 1866 ellipsoid WSJT-X itself uses (Thomas 1970 geodesic,
        // Stage-A6-geodesic-match, 2026-07-16), not a spherical approximation -- a
        // degree of meridional latitude is ~110.57 km near the equator (vs ~111.69 km
        // near the poles) on this ellipsoid, not a constant ~111.2 km as a sphere of
        // mean radius 6371 km would give. Expected values cross-validated against an
        // independent Python port of WSJT-X's own geodist.f90.
        //
        // one degree of latitude north, same longitude -- due north, ~110.57 km at
        // the equator.
        double distNorth = GeoMath.DistanceKm(0.5, 1.0, 1.5, 1.0);
        Check("1 degree latitude ~= 110.57 km at the equator", Math.Abs(distNorth - 110.5676) < 0.01, true);
        double azNorth = GeoMath.AzimuthDegrees(0.5, 1.0, 1.5, 1.0);
        Check("due north bearing == 0 degrees", Math.Abs(azNorth - 0.0) < 0.01, true);

        // Quarter of the equator's circumference (independently derivable:
        // equatorialRadiusKm * (pi/2), Clarke 1866 equatorial radius 6378.2064 km),
        // due east.
        double distQuarter = GeoMath.DistanceKm(0, 0, 0, 90);
        Check("quarter circumference along equator ~= equatorialR*(pi/2)",
              Math.Abs(distQuarter - (6378.2064 * Math.PI / 2)) < 0.5, true);
        double azEast = GeoMath.AzimuthDegrees(0, 0, 0, 90);
        Check("due east bearing == 90 degrees", Math.Abs(azEast - 90.0) < 0.01, true);

        // Same point: zero distance.
        Check("same point: distance == 0", Math.Abs(GeoMath.DistanceKm(10, 20, 10, 20)) < 0.0001, true);

        // End-to-end grid-pair convenience overload composes the same way.
        var pair = GeoMath.DistanceAndAzimuth("JJ00", "JJ01");
        Check("DistanceAndAzimuth(JJ00, JJ01): non-null", pair.HasValue, true);
        if (pair.HasValue)
        {
            // JJ00/JJ01 resolve to their "mm" sub-square centers (see the GridToLatLon
            // comment above), ~1 degree of latitude apart at the equator: ~110.57 km.
            Check("DistanceAndAzimuth(JJ00, JJ01): ~110.57 km", Math.Abs(pair.Value.distanceKm - 110.5676) < 0.01, true);
            Check("DistanceAndAzimuth(JJ00, JJ01): due north", Math.Abs(pair.Value.azimuthDeg - 0.0) < 0.01, true);
        }

        Check("DistanceAndAzimuth: bad 'from' grid returns null",
              GeoMath.DistanceAndAzimuth("bad", "JJ00").HasValue, false);
        Check("DistanceAndAzimuth: bad 'to' grid returns null",
              GeoMath.DistanceAndAzimuth("JJ00", "bad").HasValue, false);

        // 6-character (subsquare) locators parse to a finer-resolution center point
        // still inside the parent 4-character square.
        var sixChar = GeoMath.GridToLatLon("JJ00aa");
        Check("6-char locator parses (non-null)", sixChar.HasValue, true);
        if (sixChar.HasValue && jj00.HasValue)
        {
            Check("6-char locator stays within the parent 4-char square (lat)",
                  Math.Abs(sixChar.Value.lat - jj00.Value.lat) < 0.5, true);
            Check("6-char locator stays within the parent 4-char square (lon)",
                  Math.Abs(sixChar.Value.lon - jj00.Value.lon) < 1.0, true);
        }
    }

    // ── GeoMath ellipsoidal cross-validation (Stage A6 geodesic-match, 2026-07-16) ──
    // Expected values below were produced by an independent Python transcription of
    // WSJT-X's own lib/geodist.f90/grid2deg.f90 (Thomas 1970 spheroidal geodesic,
    // Clarke 1866 ellipsoid), run against real grid squares -- NOT re-derived from
    // Jimmy's own C# port. This is the permanent record of the cross-validation used
    // to confirm the C# port is a faithful, bug-free transcription (the temporary
    // "XVAL" console printout used to find/fix the GridToLatLon "mm" sub-square
    // default bug during development has been removed; this replaces it).
    static void GeoMathEllipsoidCrossValidationTests()
    {
        Console.WriteLine("\n── GeoMath ellipsoidal cross-validation (vs independent Python port of WSJT-X) ──");

        var expectedLatLon = new (string grid, double lat, double lon)[]
        {
            ("EN34", 44.520833, -92.958333),
            ("FN31", 41.520833, -72.958333),
            ("EM63", 33.520833, -86.958333),
            ("EN70", 40.520833, -84.958333),
            ("FN42", 42.520833, -70.958333),
            ("JJ00", 0.520833, 1.041667),
            ("JJ01", 1.520833, 1.041667),
        };
        foreach (var (grid, lat, lon) in expectedLatLon)
        {
            var ll = GeoMath.GridToLatLon(grid);
            Check($"{grid}: lat matches Python oracle", ll.HasValue && Math.Abs(ll.Value.lat - lat) < 0.0001, true);
            Check($"{grid}: lon matches Python oracle", ll.HasValue && Math.Abs(ll.Value.lon - lon) < 0.0001, true);
        }

        var expectedPairs = new (string from, string to, double az, double distKm)[]
        {
            ("EN34", "FN31", 94.58, 1659.57),
            ("EM63", "EN70", 12.31, 796.89),
            ("FN42", "EM63", 239.75, 1719.13),
            ("JJ00", "JJ01", 0.00, 110.57),
        };
        foreach (var (from, to, az, distKm) in expectedPairs)
        {
            var r = GeoMath.DistanceAndAzimuth(from, to);
            Check($"{from} -> {to}: azimuth matches Python oracle", r.HasValue && Math.Abs(r.Value.azimuthDeg - az) < 0.01, true);
            Check($"{from} -> {to}: distance matches Python oracle", r.HasValue && Math.Abs(r.Value.distanceKm - distKm) < 0.01, true);
        }
    }

    // ── A6 Classification Parity (deterministic fixtures) ───────────────────────
    // Proves ClassificationEngine's computed output matches hand-derived ground
    // truth (LogbookDb worked-before state + TestFixtureLookupProvider's
    // deterministic Country/Continent/Dxcc/Grid data -- zero network/disk I/O
    // beyond the throwaway LogbookDb, TestModeGuard's protections untouched), and
    // that CallQueueRanker/AwardMatcher produce identical output whether reading
    // via the wire-style path (ClassificationCutover.UseClassificationEngine=false)
    // or the computed path (=true) for the same underlying decode. Calls/Country/
    // Continent values are chosen to match JimmyReplay.py's own *_CALL fixtures --
    // see TestFixtureLookupProvider.cs for the per-call cross-reference.
    static void CheckFieldParity(ClassificationEngine engine, string label, string message, string band,
        string myGrid, string myContinent,
        bool expectNewOnBand, bool expectNewAnyBand, bool expectNewCountry, bool expectNewCountryOnBand,
        string expectCountry, string expectContinent, bool expectIsDx, string expectGridForAzDist)
    {
        string call = WsjtxMessage.DeCall(message);
        var c = engine.Classify(call, band, message, myGrid, myContinent);

        Check($"{label}: IsNewCallOnBand", c.IsNewCallOnBand == expectNewOnBand, true);
        Check($"{label}: IsNewCallAnyBand", c.IsNewCallAnyBand == expectNewAnyBand, true);
        Check($"{label}: IsNewCountry", c.IsNewCountry == expectNewCountry, true);
        Check($"{label}: IsNewCountryOnBand", c.IsNewCountryOnBand == expectNewCountryOnBand, true);
        CheckStr($"{label}: Country", c.Country, expectCountry);
        CheckStr($"{label}: Continent", c.Continent, expectContinent);
        Check($"{label}: IsDx", c.IsDx == expectIsDx, true);

        if (expectGridForAzDist == null)
        {
            Check($"{label}: Azimuth -1 (unresolvable)", c.Azimuth == -1, true);
            Check($"{label}: Distance -1 (unresolvable)", c.Distance == -1, true);
        }
        else
        {
            var expectedDa = GeoMath.DistanceAndAzimuth(myGrid, expectGridForAzDist);
            int expectedDist = (int)Math.Round(expectedDa.Value.distanceKm);
            int expectedAz = (int)Math.Round(expectedDa.Value.azimuthDeg) % 360;
            if (expectedAz < 0) expectedAz += 360;
            Check($"{label}: Distance matches GeoMath oracle for {expectGridForAzDist}", c.Distance == expectedDist, true);
            Check($"{label}: Azimuth matches GeoMath oracle for {expectGridForAzDist}", c.Azimuth == expectedAz, true);
        }
    }

    static void A6ClassificationParityTests()
    {
        Console.WriteLine("\n── A6 Classification Parity (deterministic fixtures, Stage A6) ──");
        string tmpDb = Path.Combine(Path.GetTempPath(),
            "JimmyTest_A6Parity_" + Guid.NewGuid().ToString("N") + ".db");
        bool originalFlag = ClassificationCutover.UseClassificationEngine;
        try
        {
            using (var db = new LogbookDb(tmpDb))
            {
                // TestModeGuard.IsTestMode is unaffected by any of this -- LookupManager's
                // real providers (Qrz/ClubLog/LoTW/FccUls) stay fully registered and are
                // still exercised by Build(), they just contribute nothing for these
                // fictional test calls (no real cached data). TestFixtureLookupProvider is
                // the only source that resolves them, inserted first so it always wins.
                var lookupManager = new LookupManager();
                lookupManager.RegisterProviderFirst(new TestFixtureLookupProvider());
                lookupManager.Initialize(
                    useLookupData: true,
                    qrzEnabled: false, qrzUser: null, qrzPass: null, qrzCacheDays: 1,
                    lotwEnabled: true, lotwDays: 1,
                    clubLogAppKey: null, clubLogDays: 1,
                    fccUlsEnabled: false);

                var engine = new ClassificationEngine(db, lookupManager);
                const string myGrid = "FN42";
                const string myContinent = "NA";
                const string band = "20m";

                // Case A: K4YT, never worked, message carries its own grid.
                CheckFieldParity(engine, "K4YT (never worked, msg carries grid)", "CQ K4YT EM63", band, myGrid, myContinent,
                    expectNewOnBand: true, expectNewAnyBand: true, expectNewCountry: true, expectNewCountryOnBand: true,
                    expectCountry: "USA", expectContinent: "NA", expectIsDx: false, expectGridForAzDist: "EM63");

                // Case B: K4YT worked on 20m -- this message has no grid, must fall back to
                // the fixture's grid (matching K4YT's own CQ grid, so Azimuth/Distance stay
                // consistent across a station's grid-carrying and grid-less messages).
                InsertQso(db, "K4YT", "TN", dxcc: 291, zone: 4, band: "20m");
                CheckFieldParity(engine, "K4YT (worked on 20m, report msg has no grid)", "KB0UZT K4YT -05", band, myGrid, myContinent,
                    expectNewOnBand: false, expectNewAnyBand: false, expectNewCountry: false, expectNewCountryOnBand: false,
                    expectCountry: "USA", expectContinent: "NA", expectIsDx: false, expectGridForAzDist: "EM63");

                // Case C: same call, worked on 20m, asked about 40m -- new on this band,
                // country/DXCC already worked (any-band).
                CheckFieldParity(engine, "K4YT (worked on 20m, asked about 40m)", "KB0UZT K4YT RRR", "40m", myGrid, myContinent,
                    expectNewOnBand: true, expectNewAnyBand: false, expectNewCountry: false, expectNewCountryOnBand: true,
                    expectCountry: "USA", expectContinent: "NA", expectIsDx: false, expectGridForAzDist: "EM63");

                // Case D: G3HRC -- DX (different continent than myContinent=NA), never
                // worked, DXCC never worked.
                CheckFieldParity(engine, "G3HRC (DX, never worked)", "CQ G3HRC IO91", band, myGrid, myContinent,
                    expectNewOnBand: true, expectNewAnyBand: true, expectNewCountry: true, expectNewCountryOnBand: true,
                    expectCountry: "England", expectContinent: "EU", expectIsDx: true, expectGridForAzDist: "IO91");

                // Case E: PY5SNL -- DX, DXCC (Brazil) already worked via a *different*
                // callsign on this band -- IsNewCountry/IsNewCountryOnBand must reflect the
                // DXCC entity, not the specific call, while IsNewCallOnBand/AnyBand stay true
                // for PY5SNL itself.
                InsertQso(db, "PT9ZZ", "SA", dxcc: 108, zone: 11, band: "20m");
                CheckFieldParity(engine, "PY5SNL (Brazil, DXCC already worked via another call)", "CQ PY5SNL GG66", band, myGrid, myContinent,
                    expectNewOnBand: true, expectNewAnyBand: true, expectNewCountry: false, expectNewCountryOnBand: false,
                    expectCountry: "Brazil", expectContinent: "SA", expectIsDx: true, expectGridForAzDist: "GG66");

                // Case F: W5C/H -- deliberately absent from the fixture table (matches
                // JimmyReplay.py's own country=None/continent=None test case) -- must not
                // crash, everything stays at conservative defaults. IsDx=true here (not
                // the general unresolved-continent default of false) because W5C/H itself
                // ends in /H -- confirmed against real wire behavior via live A6 field
                // testing 2026-07-16 (wire IsDx=true for this exact call).
                CheckFieldParity(engine, "W5C/H (unresolvable, no fixture data, /H suffix)", "CQ W5C/H", band, myGrid, myContinent,
                    expectNewOnBand: true, expectNewAnyBand: true, expectNewCountry: false, expectNewCountryOnBand: false,
                    expectCountry: "", expectContinent: "", expectIsDx: true, expectGridForAzDist: null);

                // Case F2: K1ABC/H -- regression test for the /H-suffix fix itself. This
                // fixture entry DOES have real data (Country/Continent/Dxcc/Grid, see
                // TestFixtureLookupProvider), proving the fix actively SKIPS using it
                // (rather than the fixture simply having no entry, like Case F above) --
                // mirrors real QRZ/Club Log data confidently resolving a /H station's base
                // callsign to its home location, which Classify() must not trust.
                CheckFieldParity(engine, "K1ABC/H (/H suffix must skip real fixture data)", "CQ K1ABC/H", band, myGrid, myContinent,
                    expectNewOnBand: true, expectNewAnyBand: true, expectNewCountry: false, expectNewCountryOnBand: false,
                    expectCountry: "", expectContinent: "", expectIsDx: true, expectGridForAzDist: null);

                // Case G: K3ZK -- regression test for a real bug found via live A6 field
                // testing 2026-07-16 (user-reported: US-state display substitution silently
                // stopped firing). The fixture's raw Country is "United States" (mirroring
                // QRZ's actual raw output), but Classify() must normalize it to "USA" --
                // several consumers (US-state display substitution, CallQueueStore's
                // auto-lookup trigger) compare Country against the literal "USA".
                // DXCC 291 (USA) was already marked worked on 20m by Case B's K4YT insert
                // above (same db, whole method) -- so IsNewCountry/IsNewCountryOnBand are
                // correctly false here too, same as K4YT's own Case B/C expectations.
                CheckFieldParity(engine, "K3ZK (raw QRZ-style country string must normalize to USA)", "CQ K3ZK FN21", band, myGrid, myContinent,
                    expectNewOnBand: true, expectNewAnyBand: true, expectNewCountry: false, expectNewCountryOnBand: false,
                    expectCountry: "USA", expectContinent: "NA", expectIsDx: false, expectGridForAzDist: "FN21");

                // ── Consumer-level parity: same underlying values, both cutover-flag
                //    states, identical output. ──
                var ranker = new CallQueueRanker();
                ranker.ApplySortOrder(new List<WsjtxClient.RankMethods> { WsjtxClient.RankMethods.DIST_DECR }, null);

                var computed = engine.Classify("G3HRC", band, "CQ G3HRC IO91", myGrid, myContinent);
                var da = GeoMath.DistanceAndAzimuth(myGrid, "IO91");
                int expDist = (int)Math.Round(da.Value.distanceKm);
                int expAz = (int)Math.Round(da.Value.azimuthDeg) % 360;
                if (expAz < 0) expAz += 360;

                var wireStyle = new EnqueueDecodeMessage
                {
                    Message = "CQ G3HRC IO91",
                    Category = WsjtxClient.CallCategory.DEFAULT,
                    IsDx = true, IsNewCallOnBand = true, IsNewCallAnyBand = true,
                    IsNewCountry = true, IsNewCountryOnBand = true,
                    Country = "England", Continent = "EU",
                    Distance = expDist, Azimuth = expAz,
                    Classified = computed,
                };

                ClassificationCutover.UseClassificationEngine = false;
                ranker.SetRank(wireStyle);
                int rankWireStyle = wireStyle.Rank;

                ClassificationCutover.UseClassificationEngine = true;
                ranker.SetRank(wireStyle);
                int rankComputedStyle = wireStyle.Rank;

                Check("SetRank: identical Rank regardless of cutover flag (same underlying grid/values)",
                      rankWireStyle == rankComputedStyle, true);

                bool rejectWire = AwardMatcher.ShouldRejectAlreadyWorked(
                    wireStyle.IsNewCallOnBand, isPota: false, isNewDxccCategory: false, isStillNeededByActiveAward: false);
                bool rejectComputed = AwardMatcher.ShouldRejectAlreadyWorked(
                    computed.IsNewCallOnBand, isPota: false, isNewDxccCategory: false, isStillNeededByActiveAward: false);
                Check("ShouldRejectAlreadyWorked: identical outcome, wire-derived vs computed IsNewCallOnBand",
                      rejectWire == rejectComputed, true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  A6ClassificationParityTests threw: {ex.GetType().Name}: {ex.Message}");
            failed++;
        }
        finally
        {
            ClassificationCutover.UseClassificationEngine = originalFlag;
            try { File.Delete(tmpDb); } catch { }
        }
    }

    // ── Direct-mode plumbing parity (Self-sufficiency plan Phase 6 hardening) ───────────
    // Feeds a hand-written engine-snapshot JSON (the same camelCase shape jimmy-engine-
    // host's SNAPSHOT command actually produces) through WsjtxClient.Direct.cs's real
    // DirectApplyStatus/DirectApplyDecodes pipeline, via the TestApplyDirectSnapshot hook,
    // and checks the outcome the exact same way A6ClassificationParityTests above checks
    // the UDP path. Added 2026-08-09 after finding, live, that direct mode had silently
    // left dialFrequency and the private "mode" field at their zero/empty defaults the
    // whole session: CurrentBandStr stayed "unknown band" forever, and Classify()'s own
    // documented "can't tell, assume new" convention made every decode -- USA included --
    // read as New DXCC / New DXCC on band regardless of real log history. That gap took
    // hours of live radio observation to notice because the shared downstream pipeline
    // (ProcessDecodeMsg, Classify, AddSelectedCall...) is correct and well-tested --
    // only the direct-mode field-mapping feeding it was wrong. This test exists so a
    // similar gap surfaces as a failing assertion instead.
    //
    // Safety: JIMMY_TEST_DB_PATH is set BEFORE constructing Controller/WsjtxClient, exactly
    // like every other test/harness in this codebase (TestModeGuard.cs is the single
    // source of truth) -- LogbookDb.DbPath, WsjtxClient's own "path" field, and every real
    // QRZ/Club Log/LoTW/FCC ULS network call site all redirect/no-op automatically from
    // that one signal. Controller is constructed but its Load event is never fired (no
    // .Show()/Application.Run() anywhere below), so the real Jimmy.ini is never read and
    // no engine process is ever spawned -- confirmed by reading Program.cs and Controller's
    // constructor before writing this test. No real file writes, no real network calls,
    // no real engine process, nothing sent to any live logging service.
    static void DirectModePlumbingParityTests()
    {
        Console.WriteLine("\n── Direct-mode plumbing parity (DirectApplyStatus/DirectApplyDecodes vs UDP path) ──");

        string tmpDb = Path.Combine(Path.GetTempPath(),
            "JimmyTest_DirectParity_" + Guid.NewGuid().ToString("N") + ".db");
        string prevTestDbPath = Environment.GetEnvironmentVariable("JIMMY_TEST_DB_PATH");
        Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", tmpDb);
        try
        {
            var ctrl = new Controller(); // never Show()/Run() -- Load event (real .ini, real engine spawn) never fires

            // callCqOptionsButton is normally built by Controller's real settings-load method
            // (the one this test deliberately never calls, to avoid touching the real
            // Jimmy.ini) -- WsjtxClient's own constructor calls UpdateModeVisible(), which
            // sets this button's Visible state unconditionally, so it must exist first.
            // Built here exactly as Controller.cs itself builds it, without pulling in that
            // whole ini-reading method.
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            // Same story -- weak-signal-floor controls (Controller.cs builds these
            // dynamically too, reparented into Options > Receive/Auto Reply > Block List).
            // AddSelectedCall reads ignoreWeakSnrCheckBox.Checked first and short-circuits
            // (default false) before ever touching minSnrNumUpDown.Value, so only existing
            // (not any particular value) matters here.
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();

            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);

            var lookupManager = new LookupManager();
            lookupManager.RegisterProviderFirst(new TestFixtureLookupProvider());
            lookupManager.Initialize(
                useLookupData: true,
                qrzEnabled: false, qrzUser: null, qrzPass: null, qrzCacheDays: 1,
                lotwEnabled: true, lotwDays: 1,
                clubLogAppKey: null, clubLogDays: 1,
                fccUlsEnabled: false);
            wc.lookupManager = lookupManager;

            // Realistic baseline filter config: a freshly-constructed Controller (never
            // loaded real Jimmy.ini) has these at their raw WinForms defaults -- all
            // unchecked -- which would reject every call regardless of the plumbing this
            // test targets. This is the exact live-observed config gap (2026-08-09,
            // "loc"/"DX stations" both unchecked) that made real CQ calls stop queueing --
            // a real bug, but a filter-configuration one, not a plumbing one, and already
            // covered by CallQueueRanker's own tests. Set permissively here so this test
            // isolates band/classification wiring, not filter-checkbox defaults.
            ctrl.anyMsgRadioButton.Checked = true;
            ctrl.replyDxCheckBox.Checked = true;
            ctrl.replyLocalCheckBox.Checked = true;

            const string myCall = "KB0UZT";
            const string myGrid = "FN42";

            // ── Scenario 1: dial frequency / band tracking ──────────────────────────────
            // 14.074 MHz is FT8's 20m calling frequency. Before the 2026-08-09 fix,
            // CurrentBandStr stayed null (unknown band) forever in direct mode regardless
            // of dialMhz.
            var snap1 = ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""",
                ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""slot"": 1000 },
                ""recentDecodes"": []
            }");
            wc.TestApplyDirectSnapshot(myCall, myGrid, snap1);
            CheckStr("Direct mode: CurrentBandStr resolves 14.074 MHz to 20m", wc.CurrentBandStr, "20m");
            // Added 2026-08-10, alongside the "unknown band at startup" live-testing fix:
            // bandIdx (private, unreachable from here) is what drives this, but LastBandIdx is
            // the observable, public side effect -- confirms DirectApplyStatus's new "persist
            // whenever a real band is confirmed" logic actually ran, not just CurrentBandStr's
            // own (pre-existing) FreqToBandStr text lookup above.
            Check("Direct mode: resolving a real band persists it to Radio.LastBandIdx (20m/index 5)",
                  ctrl.Radio.LastBandIdx == 5, true);

            // ── Scenario 2: a never-worked, ordinary CQ call reaches the queue ──────────
            // K4YT is a domestic (USA) fixture call (TestFixtureLookupProvider) -- never
            // worked in this test's own throwaway DB, so it must be admitted.
            var snap2 = ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""",
                ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""slot"": 1001 },
                ""recentDecodes"": [
                    { ""from"": ""K4YT"", ""snr"": -10, ""dtSec"": 0.1, ""freqHz"": 1500.0, ""message"": ""CQ K4YT EM63"" }
                ]
            }");
            wc.TestApplyDirectSnapshot(myCall, myGrid, snap2);
            Check("Direct mode: never-worked CQ call reaches the queue", wc.TestCallQueueString.Contains("K4YT"), true);

            // ── Scenario 3: raw-decode-history population ───────────────────────────────
            // Before the 2026-08-09 fix, DirectApplyDecodes never populated
            // _rawDecodeHistory at all -- the Raw Decodes panel was silently empty in
            // direct mode regardless of "Show raw decodes"/advancedCallLayout.
            ctrl.advancedCallLayout = true;
            ctrl.advShowRaw = false; // avoid touching the real UI list (ShowRawDecodes) -- only the data list matters here
            var snap3 = ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""",
                ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""slot"": 1002 },
                ""recentDecodes"": [
                    { ""from"": ""W9EVN"", ""snr"": -5, ""dtSec"": 0.2, ""freqHz"": 1600.0, ""message"": ""CQ W9EVN EM63"" }
                ]
            }");
            wc.TestApplyDirectSnapshot(myCall, myGrid, snap3);
            Check("Direct mode: raw decode history captures the decode",
                  wc.TestRawDecodeHistory.Any(m => m.Message != null && m.Message.Contains("W9EVN")), true);

            // ── Scenario 4: already-worked station is NOT re-admitted ───────────────────
            // Proves the classification-parity concern specifically: a station this test's
            // own throwaway DB marks as worked on 20m must be excluded, exactly like the
            // UDP path (see A6ClassificationParityTests' Case B/C above).
            using (var db = new LogbookDb(tmpDb))
            {
                InsertQso(db, "KD4LS", "AL", dxcc: 291, zone: 4, band: "20m");
            }
            var snap4 = ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""",
                ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""slot"": 1003 },
                ""recentDecodes"": [
                    { ""from"": ""KD4LS"", ""snr"": -7, ""dtSec"": 0.1, ""freqHz"": 1700.0, ""message"": ""CQ KD4LS EM74"" }
                ]
            }");
            wc.TestApplyDirectSnapshot(myCall, myGrid, snap4);
            Check("Direct mode: already-worked-on-this-band station is not re-admitted",
                  wc.TestCallQueueString.Contains("KD4LS"), false);

            // ── Scenario 5: QSO completion/logging drives off the engine's own qso.txNow ────
            // Added 2026-08-10, root-caused live from a real QSO with W1XI that never logged:
            // curTxMsg/txMsg (feeds "sending X" status) and LogQso (73/RR73 -> ADIF log entry)
            // were both permanently dead in Direct mode because the only code that ever set
            // them (ProcessTxStart/ProcessTxEnd, WsjtxClient.Protocol.cs) is UDP-only.
            // DirectApplyStatus's fix reads snap.Qso.TxNow instead -- the engine's own
            // authoritative "what am I actually sending" (Jimmy itself never knows this in
            // Direct mode; see DirectSendReply's own comment). Deliberately sets up callInProg/
            // allCallDict/sentReportList directly rather than driving a full CQ-through-73
            // decode sequence through ProcessDecodeMsg -- that admission pipeline is already
            // covered by scenarios 2-4 above; this isolates the NEW tx_now-driven logic alone.
            const string qsoCall = "N3XYZ";
            wc.callInProg = qsoCall;
            wc.allCallDict[qsoCall] = new List<EnqueueDecodeMessage>
            {
                new EnqueueDecodeMessage
                {
                    Message = $"{myCall} {qsoCall} R-15",
                    Snr = -15,
                    RxDate = DateTime.UtcNow.Date,
                    SinceMidnight = DateTime.UtcNow.TimeOfDay,
                },
            };

            // Step 1: Jimmy's own report to N3XYZ goes out ("N3XYZ KB0UZT -12") -- should be
            // recognized as a report and added to sentReportList, exactly like the UDP path's
            // own ProcessTxEnd does at the moment WSJT-X reports having sent it.
            var snap5a = ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""",
                ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": true, ""slot"": 1004 },
                ""recentDecodes"": [],
                ""qso"": { ""state"": ""awaitReport"", ""txNow"": """ + qsoCall + " " + myCall + @" -12"" }
            }");
            wc.TestApplyDirectSnapshot(myCall, myGrid, snap5a);
            Check("Direct mode: engine's own tx_now report text is recognized and tracked",
                  wc.sentReportList.Contains(qsoCall), true);
            Check("Direct mode: QSO not logged yet -- only a report has gone out, not 73/RR73",
                  wc.logList.Contains(qsoCall), false);

            // Step 2: Jimmy's own 73 goes out ("N3XYZ KB0UZT 73") -- should trigger LogQso,
            // which finds N3XYZ's own roger-report in allCallDict (set up above) and logs the
            // QSO via the exact same RequestLog/ClaimLiveLoggedQso path the UDP path always used.
            var snap5b = ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""",
                ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": true, ""slot"": 1005 },
                ""recentDecodes"": [],
                ""qso"": { ""state"": ""done"", ""txNow"": """ + qsoCall + " " + myCall + @" 73"" }
            }");
            wc.TestApplyDirectSnapshot(myCall, myGrid, snap5b);
            Check("Direct mode: engine's own tx_now 73 text logs the completed QSO",
                  wc.logList.Contains(qsoCall), true);

            // Regression guard, 2026-08-11: DirectApplyStatus logged the completed QSO above but
            // never cleared callInProg afterward -- unlike the UDP path's ProcessTxEnd, which
            // always calls SetCallInProg(null) once a 73/RR73 to callInProg goes out. Root-caused
            // live from a real session where ShowStatus() kept re-announcing one already-logged
            // QSO's stale "previous RR73" for 19+ minutes afterward instead of returning to
            // normal "N available stations" status (callsWaiting is only computed when
            // callInProg == null, WsjtxClient.Display.cs).
            Check("Direct mode: callInProg is cleared once the final 73/RR73 has gone out",
                  wc.callInProg == null, true);

            // Step 3: a real bug found live, 2026-08-10, right after the fix above shipped --
            // the engine's own tx_now keeps reporting the same sent-73 text for several more
            // poll ticks (as long as the engine stays on that QSO step), not just the one tick
            // the transmission ended on. Re-applying the SAME snapshot simulates that: without
            // an explicit "already logged" guard, this replayed LogQso() on every 1s poll --
            // ClaimLiveLoggedQso's dedup key stayed identical so the database only ever got one
            // entry, but RequestLog's sound/announcement side effects are unconditional, so the
            // operator heard three duplicate "Logged QSO with K7F" dings for one real QSO.
            // logList.Count(x => x == qsoCall) (not Contains, which can't see duplicates) is the
            // only way to catch a regression here.
            wc.TestApplyDirectSnapshot(myCall, myGrid, snap5b);
            wc.TestApplyDirectSnapshot(myCall, myGrid, snap5b);
            Check("Direct mode: repeated polls with unchanged tx_now do not re-log the same QSO",
                  wc.logList.Count(c => c == qsoCall) == 1, true);

            // Step 4: real incident, 2026-08-10 -- LogQso's actual database write happens on a
            // fire-and-forget background Task.Run (LiveQsoUploadOrchestrator.ImportLiveLoggedQso),
            // not synchronously on this thread. Before that method was fixed to capture its
            // target path up front, the write raced this test's own env-var-based isolation: by
            // the time the background task actually got scheduled, this test's `finally` block
            // (below) could already have restored JIMMY_TEST_DB_PATH to its previous value,
            // sending the write into the REAL production logbook.db instead of tmpDb. Confirmed
            // live: four synthetic "N3XYZ" QSOs landed in the operator's actual logbook. This
            // assertion is the regression guard for that fix -- it polls tmpDb (never the real
            // path) for up to 2s waiting for the background write to land. If the path-capture
            // fix in LiveQsoUploadOrchestrator/RunTqslUpload/RunUploadCatchUp is ever undone or a
            // similar new Task.Run(...new LogbookDb()...) is added elsewhere, this either times
            // out (write never reaches tmpDb) or the earlier in-memory checks above still pass
            // while the real user's logbook silently gets contaminated again -- this is the one
            // assertion that actually proves the write landed in the ISOLATED database, not just
            // that logList (in-memory only) was updated.
            bool foundInTmpDb = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 2000)
            {
                using (var verifyDb = new LogbookDb(tmpDb))
                {
                    if (verifyDb.SearchQsos(qsoCall, null, null, null).Count > 0) { foundInTmpDb = true; break; }
                }
                System.Threading.Thread.Sleep(50);
            }
            Check("Direct mode: the background QSO write actually lands in the ISOLATED test database",
                  foundInTmpDb, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  DirectModePlumbingParityTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", prevTestDbPath);
            try { File.Delete(tmpDb); } catch { }
        }
    }

    // ── Read-only audit, 2026-08-28: (1) "Log early, after RRR" went dead when the UDP
    // ProcessTxEnd() was removed -- IsLogEarly() has had no callers since -- so a QSO with both
    // reports exchanged plus the DX's bare RRR was only logged if a trailing 73/RR73 later got
    // exchanged (contest/award loss if it didn't). (2) A failed local logbook write during
    // completion was never retried in the Direct path, because SetCallInProg(null) ran
    // unconditionally right after LogQso, so the callInProg-gated completion branch could not
    // re-enter. ──
    static void DirectLogRetryAndEarlyRrrTests()
    {
        Console.WriteLine("\n── Audit: RRR early-log revived + failed local-write retry (Direct path) ──");

        string goodDb = Path.Combine(Path.GetTempPath(), "JimmyTest_LogRetry_" + Guid.NewGuid().ToString("N") + ".db");
        string badDir = Path.Combine(Path.GetTempPath(), "JimmyTest_BadDb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(badDir);   // a directory path can't be opened as a SQLite file -> write throws -> localWriteFailed
        string prev = Environment.GetEnvironmentVariable("JIMMY_TEST_DB_PATH");

        const string myCall = "KB0UZT", myGrid = "FN42", dx = "K4YT";  // K4YT: domestic fixture

        WsjtxClient MakeClient(bool logEarly)
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.anyMsgRadioButton.Checked = true;
            ctrl.replyDxCheckBox.Checked = true;
            ctrl.replyLocalCheckBox.Checked = true;
            ctrl.logEarlyCheckBox.Checked = logEarly;
            var lm = new LookupManager();
            lm.RegisterProviderFirst(new TestFixtureLookupProvider());
            lm.Initialize(useLookupData: true, qrzEnabled: false, qrzUser: null, qrzPass: null, qrzCacheDays: 1,
                lotwEnabled: false, lotwDays: 1, clubLogAppKey: null, clubLogDays: 1, fccUlsEnabled: false);
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.lookupManager = lm;
            wc.TestSetDirectConnected(true);
            wc.TestSetMode("FT8");
            return wc;
        }

        // Seed a client mid-QSO with dx: they reported to us, we reported to them.
        void SeedMidQso(WsjtxClient wc, int dxPriority)
        {
            wc.callInProg = dx;
            wc.allCallDict[dx] = new System.Collections.Generic.List<EnqueueDecodeMessage>
            {
                new EnqueueDecodeMessage
                {
                    Message = $"{myCall} {dx} R-07", Snr = -7, Priority = dxPriority,
                    RxDate = DateTime.UtcNow.Date, SinceMidnight = DateTime.UtcNow.TimeOfDay,
                },
            };
            wc.sentReportList.Add(dx);
        }

        DirectSnapshot DecodeSnap(ulong slot, string message) => ParseDirectSnapshot(@"{
            ""mycall"": """ + myCall + @""", ""mygrid"": """ + myGrid + @""",
            ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""slot"": " + slot + @" },
            ""recentDecodes"": [ { ""from"": """ + dx + @""", ""snr"": -1, ""dtSec"": 0.1, ""freqHz"": 1500.0, ""message"": """ + message + @""" } ]
        }");
        DirectSnapshot TxNowSnap(ulong slot, string txNow) => ParseDirectSnapshot(@"{
            ""mycall"": """ + myCall + @""", ""mygrid"": """ + myGrid + @""",
            ""radio"": { ""dialMhz"": 14.074, ""transmitting"": true, ""slot"": " + slot + @" },
            ""recentDecodes"": [],
            ""qso"": { ""state"": ""done"", ""txNow"": """ + txNow + @""" }
        }");

        try
        {
            // ── Part 1 (Finding 1): a decoded bare RRR from the worked station logs early ──
            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", goodDb);

            var wcOn = MakeClient(logEarly: true);
            SeedMidQso(wcOn, (int)WsjtxClient.CallPriority.DEFAULT);   // ordinary call -> IsLogEarly true
            wcOn.TestApplyDirectSnapshot(myCall, myGrid, DecodeSnap(5000, $"{myCall} {dx} RRR"));
            Check("THE FIX (Finding 1): decoded RRR + reports exchanged + 'Log early' on -> QSO logged now",
                  wcOn.logList.Contains(dx), true);
            Check("THE FIX (Finding 1): callInProg stays set -- the QSO continues until 73/RR73",
                  wcOn.callInProg == dx, true);
            // the trailing 73 later tears it down and does NOT double-log
            wcOn.TestApplyDirectSnapshot(myCall, myGrid, TxNowSnap(5002, $"{dx} {myCall} 73"));
            Check("THE FIX (Finding 1): the trailing 73 clears callInProg and does not double-log",
                  wcOn.callInProg == null && wcOn.logList.FindAll(c => c == dx).Count == 1, true);

            var wcOff = MakeClient(logEarly: false);
            SeedMidQso(wcOff, (int)WsjtxClient.CallPriority.DEFAULT);
            wcOff.TestApplyDirectSnapshot(myCall, myGrid, DecodeSnap(5010, $"{myCall} {dx} RRR"));
            Check("control: 'Log early' OFF -> a decoded RRR does NOT log",
                  wcOff.logList.Contains(dx), false);

            var wcNewDx = MakeClient(logEarly: true);
            SeedMidQso(wcNewDx, (int)WsjtxClient.CallPriority.NEW_COUNTRY);   // new DXCC -> IsLogEarly false
            wcNewDx.TestApplyDirectSnapshot(myCall, myGrid, DecodeSnap(5020, $"{myCall} {dx} RRR"));
            Check("THE FIX (Finding 1): the new-DXCC exception is preserved -- RRR does NOT early-log a new country",
                  wcNewDx.logList.Contains(dx), false);
            wcNewDx.TestApplyDirectSnapshot(myCall, myGrid, DecodeSnap(5022, $"{myCall} {dx} RR73"));
            Check("...but its real RR73 still logs it",
                  wcNewDx.logList.Contains(dx), true);

            // ── Part 2 (Finding 2): a failed local write is held for retry, not lost ──
            // Construct + seed while the path is still valid (WsjtxClient's ctor opens its own
            // LogbookDb); only the COMPLETION write below hits the bad path.
            var wcFail = MakeClient(logEarly: false);
            SeedMidQso(wcFail, (int)WsjtxClient.CallPriority.DEFAULT);
            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", badDir);   // RequestLog's write now throws
            wcFail.TestApplyDirectSnapshot(myCall, myGrid, TxNowSnap(6000, $"{dx} {myCall} RR73"));
            Check("THE FIX (Finding 2): a failed local logbook write leaves the QSO unlogged",
                  wcFail.logList.Contains(dx), false);
            Check("THE FIX (Finding 2): callInProg is KEPT so the completion branch can retry",
                  wcFail.callInProg == dx, true);
            Check("THE FIX (Finding 2): the failed call is recorded for retry",
                  wcFail.TestLiveLogWriteFailedCall == dx, true);

            // disk clears; next poll (curTxMsg still the RR73) re-enters the branch and retries
            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", goodDb);
            wcFail.TestApplyDirectSnapshot(myCall, myGrid, TxNowSnap(6002, $"{dx} {myCall} RR73"));
            Check("THE FIX (Finding 2): the next poll retries the write and it succeeds",
                  wcFail.logList.Contains(dx), true);
            Check("THE FIX (Finding 2): only then is callInProg cleared",
                  wcFail.callInProg == null, true);
            Check("THE FIX (Finding 2): the retry flag is cleared",
                  wcFail.TestLiveLogWriteFailedCall == null, true);
            using (var db = new LogbookDb(goodDb))
                Check("THE FIX (Finding 2): exactly one logbook row for the retried QSO (no duplicate)",
                      db.SearchQsos(dx, null, null, null).Count == 1, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  DirectLogRetryAndEarlyRrrTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", prev);
            try { File.Delete(goodDb); } catch { }
            try { Directory.Delete(badDir, true); } catch { }
        }
    }

    // ── KF4CCG race, 2026-08-29 (CONFIRMED live): the engine's Qso.TxNow flips to the final
    // RR73 in the SAME ~1s poll that first decodes the DX's roger-report, and DirectApplyStatus
    // runs before DirectApplyDecodes ingests that decode -- so LogQso() in the Is73orRR73
    // completion branch finds no report from the DX on record yet and silently no-ops. Before
    // the fix callInProg was still torn down: the contact then went unlogged until CheckLateLog
    // rescued it ~60s later on the DX's literal 73, the engine re-sent an orphaned RR73 in
    // between (runaway backstop had to halt it), and the roger-report was never announced. The
    // fix holds callInProg for up to MaxDirectRr73LogRetries extra polls while we HAVE sent a
    // report and it still isn't on record -- the roger normally lands later in that same tick,
    // so the next poll logs it. ──
    static void DirectRr73BeforeRogerDecodeHoldsCallInProgTests()
    {
        Console.WriteLine("\n── KF4CCG race: RR73 seen before the roger decode is ingested -- hold callInProg, log on the next poll -- THE FIX ──");

        string goodDb = Path.Combine(Path.GetTempPath(), "JimmyTest_Rr73Race_" + Guid.NewGuid().ToString("N") + ".db");
        string prev = Environment.GetEnvironmentVariable("JIMMY_TEST_DB_PATH");
        const string myCall = "KB0UZT", myGrid = "FN42", dx = "K4YT";

        WsjtxClient MakeClient()
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var lm = new LookupManager();
            lm.RegisterProviderFirst(new TestFixtureLookupProvider());
            lm.Initialize(useLookupData: true, qrzEnabled: false, qrzUser: null, qrzPass: null, qrzCacheDays: 1,
                lotwEnabled: false, lotwDays: 1, clubLogAppKey: null, clubLogDays: 1, fccUlsEnabled: false);
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.lookupManager = lm;
            wc.TestSetDirectConnected(true);
            wc.TestSetMode("FT8");
            return wc;
        }

        // Mid-QSO but the DX's roger is NOT yet in allCallDict (only their grid reply) -- the
        // exact state DirectApplyStatus sees when the poll races ahead of decode ingestion.
        void SeedReportSentRogerNotYetDecoded(WsjtxClient wc)
        {
            wc.callInProg = dx;
            wc.allCallDict[dx] = new System.Collections.Generic.List<EnqueueDecodeMessage>
            {
                new EnqueueDecodeMessage
                {
                    Message = $"{dx} {myCall} EM87", Snr = -7, Priority = (int)WsjtxClient.CallPriority.DEFAULT,
                    RxDate = DateTime.UtcNow.Date, SinceMidnight = DateTime.UtcNow.TimeOfDay,
                },
            };
            wc.sentReportList.Add(dx);   // we have sent our report -> a real, completable QSO
        }

        void IngestRoger(WsjtxClient wc) =>
            wc.allCallDict[dx].Add(new EnqueueDecodeMessage
            {
                Message = $"{myCall} {dx} R+02", Snr = 2, Priority = (int)WsjtxClient.CallPriority.DEFAULT,
                RxDate = DateTime.UtcNow.Date, SinceMidnight = DateTime.UtcNow.TimeOfDay,
            });

        DirectSnapshot Rr73Snap(ulong slot) => ParseDirectSnapshot(@"{
            ""mycall"": """ + myCall + @""", ""mygrid"": """ + myGrid + @""",
            ""radio"": { ""dialMhz"": 14.074, ""transmitting"": true, ""slot"": " + slot + @" },
            ""recentDecodes"": [],
            ""qso"": { ""state"": ""done"", ""txNow"": """ + dx + " " + myCall + @" RR73"" }
        }");

        try
        {
            // ── Case 1: roger arrives on the next poll -> QSO logs then, callInProg held until it does ──
            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", goodDb);

            var wc = MakeClient();
            SeedReportSentRogerNotYetDecoded(wc);
            wc.TestApplyDirectSnapshot(myCall, myGrid, Rr73Snap(7000));
            Check("THE FIX: engine RR73 but the DX's roger isn't on record yet -> QSO NOT torn down",
                  wc.callInProg == dx, true);
            Check("THE FIX: ...and NOT logged yet (nothing to log)",
                  wc.logList.Contains(dx), false);

            IngestRoger(wc);   // DirectApplyDecodes would add this later in the same tick
            wc.TestApplyDirectSnapshot(myCall, myGrid, Rr73Snap(7001));
            Check("THE FIX: next poll, roger now on record -> QSO logs",
                  wc.logList.Contains(dx), true);
            Check("THE FIX: ...and only then is callInProg cleared",
                  wc.callInProg == null, true);
            using (var db = new LogbookDb(goodDb))
                Check("THE FIX: exactly one logbook row (CheckLateLog's later 73 can't double-log)",
                      db.SearchQsos(dx, null, null, null).Count == 1, true);

            // ── Case 2: the roger genuinely never comes -> the hold is bounded, QSO tears down ──
            var wc2 = MakeClient();
            SeedReportSentRogerNotYetDecoded(wc2);
            bool heldThrough4 = true;
            for (ulong i = 0; i < 4; i++)
            {
                wc2.TestApplyDirectSnapshot(myCall, myGrid, Rr73Snap(7100 + i));
                if (wc2.callInProg != dx) heldThrough4 = false;
            }
            Check("bounded hold: callInProg survives the first MaxDirectRr73LogRetries (4) polls",
                  heldThrough4, true);
            wc2.TestApplyDirectSnapshot(myCall, myGrid, Rr73Snap(7104));
            Check("bounded hold: the 5th poll with still no roger tears the QSO down (CheckLateLog remains the net)",
                  wc2.callInProg == null, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  DirectRr73BeforeRogerDecodeHoldsCallInProgTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", prev);
            try { File.Delete(goodDb); } catch { }
        }
    }

    // ── Direct decode / TxNow normalization (approved fix 2026-08-30). The engine reports a
    // hashed compound / portable / special-event call in angle-bracket form -- "<W1AW/2> KB0UZT
    // 73", "KB0UZT <VA3LG/W2> R-05". Every WsjtxMessage parser (ToCall/DeCall/Is73orRR73/
    // IsReport/IsRogerReport/IsRogers/Payload) bails on IsInvalid(), true for ANY '<'/'>', so
    // before this fix ProcessDecodeMsg dropped every bracketed incoming decode at its deCall/
    // toCall == null gate, and DirectApplyStatus's completion block never matched a bracketed
    // TxNow against a bracket-free callInProg -- CONFIRMED live (W1AW/2 stalled ~3 min,
    // unlogged). The fix runs the SAME cleaning the mature UDP byte parsers always did
    // (WsjtxMessage.NormalizeDecodedMessage: " ? aN" / " aN" AP markers + hashed-call unwrap)
    // on each Direct decode row and on TxNow. An unresolved "<...>" normalizes to "..." and
    // stays rejected. ──
    static void DirectDecodeNormalizationTests()
    {
        Console.WriteLine("\n── Direct decode/TxNow normalization: hashed <compound> calls + AP suffixes reach the shared QSO paths (mature UDP parity) -- THE FIX ──");

        string goodDb = Path.Combine(Path.GetTempPath(), "JimmyTest_Norm_" + Guid.NewGuid().ToString("N") + ".db");
        string prev = Environment.GetEnvironmentVariable("JIMMY_TEST_DB_PATH");
        const string myCall = "KB0UZT", myGrid = "FN42";

        WsjtxClient MakeClient(bool logEarly = false)
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.anyMsgRadioButton.Checked = true;
            ctrl.replyDxCheckBox.Checked = true;
            ctrl.replyLocalCheckBox.Checked = true;
            ctrl.logEarlyCheckBox.Checked = logEarly;
            var lm = new LookupManager();
            lm.RegisterProviderFirst(new TestFixtureLookupProvider());
            lm.Initialize(useLookupData: true, qrzEnabled: false, qrzUser: null, qrzPass: null, qrzCacheDays: 1,
                lotwEnabled: false, lotwDays: 1, clubLogAppKey: null, clubLogDays: 1, fccUlsEnabled: false);
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.lookupManager = lm;
            wc.TestSetDirectConnected(true);
            wc.TestSetMode("FT8");
            return wc;
        }

        string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        DirectSnapshot DecodesSnap(ulong slot, params string[] messages)
        {
            var rows = string.Join(",", System.Linq.Enumerable.Select(messages, m =>
                @"{ ""from"": ""X"", ""snr"": -5, ""dtSec"": 0.2, ""freqHz"": 1500.0, ""message"": """ + Esc(m) + @""" }"));
            return ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""", ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""slot"": " + slot + @" },
                ""recentDecodes"": [ " + rows + @" ]
            }");
        }
        DirectSnapshot TxNowSnap(ulong slot, string txNow) => ParseDirectSnapshot(@"{
            ""mycall"": """ + myCall + @""", ""mygrid"": """ + myGrid + @""",
            ""radio"": { ""dialMhz"": 14.074, ""transmitting"": true, ""slot"": " + slot + @" },
            ""recentDecodes"": [],
            ""qso"": { ""state"": ""done"", ""txNow"": """ + Esc(txNow) + @""" }
        }");

        void SeedMidQso(WsjtxClient wc, string dx)
        {
            wc.callInProg = dx;
            wc.allCallDict[dx] = new System.Collections.Generic.List<EnqueueDecodeMessage>
            {
                new EnqueueDecodeMessage
                {
                    Message = $"{myCall} {dx} R-07", Snr = -7, Priority = (int)WsjtxClient.CallPriority.DEFAULT,
                    RxDate = DateTime.UtcNow.Date, SinceMidnight = DateTime.UtcNow.TimeOfDay,
                },
            };
            wc.sentReportList.Add(dx);
        }

        try
        {
            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", goodDb);

            // ── 1. Complete W1AW/2 exchange: our hashed outgoing report -> incoming hashed
            //       roger-report -> our hashed final 73 -> exactly one logged QSO, callInProg
            //       cleared, queue entry removed. Nothing is hand-seeded into allCallDict --
            //       the incoming roger has to arrive through the normalized decode path. ──
            {
                var wc = MakeClient();
                const string dx = "W1AW/2";
                wc.callInProg = dx;

                wc.TestApplyDirectSnapshot(myCall, myGrid, TxNowSnap(100, "<" + dx + "> " + myCall + " -07"));
                Check("1: our hashed outgoing report is recognized -> sentReportList has W1AW/2",
                      wc.sentReportList.Contains(dx), true);

                wc.TestApplyDirectSnapshot(myCall, myGrid, DecodesSnap(101, myCall + " <" + dx + "> R-05"));
                Check("1: incoming hashed roger-report reaches allCallDict for W1AW/2 (not dropped)",
                      wc.allCallDict.ContainsKey(dx), true);

                wc.TestApplyDirectSnapshot(myCall, myGrid, TxNowSnap(102, "<" + dx + "> " + myCall + " 73"));
                Check("1: THE FIX: the hashed final 73 completes the QSO -> logged",
                      wc.logList.Contains(dx), true);
                Check("1: THE FIX: callInProg is cleared (no wedge)",
                      wc.callInProg == null, true);
                using (var db = new LogbookDb(goodDb))
                    Check("1: THE FIX: exactly one logbook row (no double-log)",
                          db.SearchQsos(dx, null, null, null).Count == 1, true);
            }

            // ── 2. Incoming bracketed RRR / RR73 / 73 reach the shared sign-off path
            //       (CheckLateLog -> RequestLog). Seed a completable mid-QSO, then feed the
            //       hashed sign-off as a decode. ──
            foreach (var tail in new[] { "RRR", "RR73", "73" })
            {
                var wc = MakeClient(logEarly: true); // RRR needs "Log early" opted in
                const string dx = "K7ABC/2";
                SeedMidQso(wc, dx);
                wc.TestApplyDirectSnapshot(myCall, myGrid, DecodesSnap(210, myCall + " <" + dx + "> " + tail));
                Check($"2: incoming hashed {tail} from a compound station reaches CheckLateLog -> QSO logged",
                      wc.logList.Contains(dx), true);
            }

            // ── 3. Incoming resolved bracketed /P and /M calls are treated as the real call. ──
            foreach (var dx in new[] { "VE3ABC/P", "K5XYZ/M" })
            {
                var wc = MakeClient();
                wc.TestApplyDirectSnapshot(myCall, myGrid, DecodesSnap(300, myCall + " <" + dx + "> R-03"));
                Check($"3: incoming hashed {dx} is unwrapped and recorded under '{dx}'",
                      wc.allCallDict.ContainsKey(dx), true);
            }

            // ── 4. Unresolved <...> and a partially-hashed line stay rejected -- nothing is
            //       recorded, callInProg is untouched, no throw. ──
            {
                var wc = MakeClient();
                wc.TestApplyDirectSnapshot(myCall, myGrid,
                    DecodesSnap(400, "<...> " + myCall + " R-05", "K7HSR <...> -16", "<...> W1AW/2 RR73"));
                Check("4: unresolved <...> decodes are still rejected -- allCallDict stays empty",
                      wc.allCallDict.Count == 0, true);
                Check("4: ...and callInProg is untouched",
                      wc.callInProg == null, true);
            }

            // ── 5. AP suffix / " ? aN" normalization: the stored message has the marker gone. ──
            {
                var wc = MakeClient();
                wc.TestApplyDirectSnapshot(myCall, myGrid, DecodesSnap(500, myCall + " K7ABC -15 a35"));
                Check("5: ' a35' AP suffix is stripped before shared processing",
                      wc.allCallDict.ContainsKey("K7ABC") &&
                      wc.allCallDict["K7ABC"][wc.allCallDict["K7ABC"].Count - 1].Message == myCall + " K7ABC -15", true);

                var wc2 = MakeClient();
                wc2.TestApplyDirectSnapshot(myCall, myGrid, DecodesSnap(501, myCall + " K7DEF -12  ? a2"));
                Check("5: old ' ? aN' AP format is stripped too",
                      wc2.allCallDict.ContainsKey("K7DEF") &&
                      wc2.allCallDict["K7DEF"][wc2.allCallDict["K7DEF"].Count - 1].Message == myCall + " K7DEF -12", true);
            }

            // ── 6. An ordinary unbracketed decode with no AP marker is unchanged. ──
            {
                var wc = MakeClient();
                wc.TestApplyDirectSnapshot(myCall, myGrid, DecodesSnap(600, myCall + " K1ABC R-09"));
                Check("6: an ordinary unbracketed message is stored verbatim",
                      wc.allCallDict.ContainsKey("K1ABC") &&
                      wc.allCallDict["K1ABC"][wc.allCallDict["K1ABC"].Count - 1].Message == myCall + " K1ABC R-09", true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  DirectDecodeNormalizationTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", prev);
            try { File.Delete(goodDb); } catch { }
        }
    }

    // ── Audit finding, 2026-08-30: the Is73orRR73 hold for a FAILED local logbook write
    // (Finding 2, 2.0.45) had no bound -- a locked/full SQLite file held callInProg forever,
    // which neutralizes both fast Tx backstops (_directOrphanTxOvers resets while callInProg
    // is set; DiscardCall can't fire) and re-published the "NOT saved" error every ~1s poll.
    // Fix: bound the hold (MaxDirectWriteFailRetries, ~10 polls / ~10s), then give up -- tear
    // the QSO down (backstop re-arms), one final "still not saved" notice. And warn only on the
    // FIRST failure, not per poll. ──
    static void DirectFailedWriteRetryIsBoundedTests()
    {
        Console.WriteLine("\n── Failed local write: the callInProg hold is bounded, warns once, then gives up -- THE FIX ──");

        string goodDb = Path.Combine(Path.GetTempPath(), "JimmyTest_WrFailBound_" + Guid.NewGuid().ToString("N") + ".db");
        string badDir = Path.Combine(Path.GetTempPath(), "JimmyTest_WrFailBad_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(badDir);   // a directory path can't be opened as a SQLite file -> write throws
        string prev = Environment.GetEnvironmentVariable("JIMMY_TEST_DB_PATH");
        const string myCall = "KB0UZT", myGrid = "FN42", dx = "K4YT";

        (WsjtxClient wc, FakeNotificationDelivery notify) MakeClient()
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var lm = new LookupManager();
            lm.RegisterProviderFirst(new TestFixtureLookupProvider());
            lm.Initialize(useLookupData: true, qrzEnabled: false, qrzUser: null, qrzPass: null, qrzCacheDays: 1,
                lotwEnabled: false, lotwDays: 1, clubLogAppKey: null, clubLogDays: 1, fccUlsEnabled: false);
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.lookupManager = lm;
            var notify = new FakeNotificationDelivery();
            wc.Notify = new NotificationCenter(new NotificationSettings(), notify);
            wc.TestSetDirectConnected(true);
            wc.TestSetMode("FT8");
            return (wc, notify);
        }

        void SeedMidQso(WsjtxClient wc)
        {
            wc.callInProg = dx;
            wc.allCallDict[dx] = new System.Collections.Generic.List<EnqueueDecodeMessage>
            {
                new EnqueueDecodeMessage
                {
                    Message = $"{myCall} {dx} R-07", Snr = -7, Priority = (int)WsjtxClient.CallPriority.DEFAULT,
                    RxDate = DateTime.UtcNow.Date, SinceMidnight = DateTime.UtcNow.TimeOfDay,
                },
            };
            wc.sentReportList.Add(dx);
        }

        DirectSnapshot Rr73Snap(ulong slot) => ParseDirectSnapshot(@"{
            ""mycall"": """ + myCall + @""", ""mygrid"": """ + myGrid + @""",
            ""radio"": { ""dialMhz"": 14.074, ""transmitting"": true, ""slot"": " + slot + @" },
            ""recentDecodes"": [],
            ""qso"": { ""state"": ""done"", ""txNow"": """ + dx + " " + myCall + @" RR73"" }
        }");

        try
        {
            // ── Case 1: write keeps failing -> bounded hold, one warning, then give up ──
            // Construct + seed while the DB path is valid (the WsjtxClient ctor opens its own
            // LogbookDb), THEN point at badDir so only the completion write fails.
            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", goodDb);
            var (wc, notify) = MakeClient();
            SeedMidQso(wc);
            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", badDir);

            int cap = wc.TestMaxDirectWriteFailRetries;
            wc.TestApplyDirectSnapshot(myCall, myGrid, Rr73Snap(9000));
            Check("first failed write: QSO held for retry (callInProg kept)",
                  wc.callInProg == dx, true);
            Check("first failed write: the failed call is recorded",
                  wc.TestLiveLogWriteFailedCall == dx, true);
            int warnsAfterFirst = notify.AnnounceCount;
            Check("first failed write: the operator IS warned",
                  warnsAfterFirst >= 1, true);

            // Poll through the rest of the retry budget -- still held, and NOT re-warned each poll.
            for (ulong i = 1; i < (ulong)cap; i++)
                wc.TestApplyDirectSnapshot(myCall, myGrid, Rr73Snap(9000 + i));
            Check("through the whole retry budget: callInProg is still held",
                  wc.callInProg == dx, true);
            Check("through the whole retry budget: NOT re-warned every poll (audit finding)",
                  notify.AnnounceCount == warnsAfterFirst, true);

            // One more poll: budget spent -> give up.
            wc.TestApplyDirectSnapshot(myCall, myGrid, Rr73Snap(9000 + (ulong)cap));
            Check("THE FIX: past the retry budget, the QSO is torn down (not held forever)",
                  wc.callInProg == null, true);
            Check("THE FIX: never logged (write never landed)",
                  wc.logList.Contains(dx), false);
            Check("THE FIX: exactly one more notice -- \"still not saved\" -- on giving up",
                  notify.AnnounceCount == warnsAfterFirst + 1, true);

            // With callInProg clear, the orphan-Tx backstop is live again: two orphaned overs halt.
            wc.TestApplyDirectSnapshot(myCall, myGrid, ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""", ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""slot"": " + (9002 + (ulong)cap) + @" }, ""recentDecodes"": [] }"));
            Check("THE FIX: with callInProg cleared, the orphan-Tx backstop counts again (was pinned at 0 while held)",
                  wc.TestOrphanTxOvers >= 1, true);

            // ── Case 2: write recovers within the budget -> logs, no data lost ──
            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", goodDb);
            var (wc2, notify2) = MakeClient();
            SeedMidQso(wc2);
            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", badDir);
            wc2.TestApplyDirectSnapshot(myCall, myGrid, Rr73Snap(9500));
            Check("recovery: held after the first failure",
                  wc2.callInProg == dx && !wc2.logList.Contains(dx), true);
            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", goodDb);
            wc2.TestApplyDirectSnapshot(myCall, myGrid, Rr73Snap(9501));
            Check("recovery: the disk clears within the budget -> QSO logs on the next poll",
                  wc2.logList.Contains(dx), true);
            Check("recovery: callInProg cleared, retry flag cleared",
                  wc2.callInProg == null && wc2.TestLiveLogWriteFailedCall == null, true);
            using (var db = new LogbookDb(goodDb))
                Check("recovery: exactly one logbook row",
                      db.SearchQsos(dx, null, null, null).Count == 1, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  DirectFailedWriteRetryIsBoundedTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", prev);
            try { File.Delete(goodDb); } catch { }
            try { Directory.Delete(badDir, true); } catch { }
        }
    }

    // ── Direct-mode runaway-RR73 backstop (CONFIRMED live, 2026-08-28 -- HB9TIH then NE5L),
    // revised 2026-08-31. Original bug: after a logged contact the engine's QSO sequencer kept
    // re-sending "<call> KB0UZT RR73" every slot for ~2 minutes with nothing telling it to stand
    // down. First fix halted on the 2nd such over -- too aggressive: Nexus (State::Confirming)
    // OWNS the legitimate closing exchange -- it re-sends RR73 while the worked station repeats
    // its R-report, stops on that station's own 73/RR73/RRR, and bounds a silent tail with its
    // own wall-clock Tx watchdog. Revised: Jimmy enters a "Finishing" state on the log and just
    // does NOT count the engine's RR73/73 closing overs to that station as orphans. It ends the
    // moment that station's own 73/RR73/RRR is decoded. An unrelated Tx with no contact --
    // different call, not 73/RR73, an engine restart re-keying on its own -- is still an ORPHAN:
    // tolerate the 1st, halt on the 2nd.
    static void DirectRunawayRr73HaltsEngineTests()
    {
        Console.WriteLine("\n── Direct-mode runaway RR73: engine owns the Finishing closing exchange, Jimmy's orphan halt only fires for UNRELATED Tx ──");

        var seen = new System.Collections.Generic.List<string>();
        var seenLock = new object();
        var listener = StartStubEngineHostWithResponses(line => { lock (seenLock) seen.Add(line); return "OK"; });
        if (listener == null)
        {
            Skip("DirectRunawayRr73HaltsEngineTests", "control port 58239 already in use by another Jimmy/engine-host session");
            return;
        }

        string tmpDb = Path.Combine(Path.GetTempPath(), "JimmyTest_Runaway_" + Guid.NewGuid().ToString("N") + ".db");
        string prevTestDbPath = Environment.GetEnvironmentVariable("JIMMY_TEST_DB_PATH");
        Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", tmpDb);
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var _ = ctrl.Handle; // Direct command completions marshal via ctrl.BeginInvoke
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.TestSetDirectConnected(true);
            wc.TestSetMode("FT8");

            const string myCall = "KB0UZT", myGrid = "FN42", qsoCall = "HB9TIH";
            System.Collections.Generic.List<string> Seen() { lock (seenLock) return new System.Collections.Generic.List<string>(seen); }
            bool SeenCmd(string prefix) => Seen().Exists(c => c.StartsWith(prefix));

            // txNow = the engine's Qso.TxNow; decode = an optional recentDecodes row "from|message".
            DirectSnapshot Snap(bool transmitting, ulong slot, string txNow, string decodeFrom = null, string decodeMsg = null)
            {
                string decodes = decodeFrom == null ? "" :
                    @"{ ""from"": """ + decodeFrom + @""", ""snr"": -18, ""dtSec"": 0.1, ""freqHz"": 1500.0, ""message"": """ + decodeMsg + @""" }";
                return ParseDirectSnapshot(@"{
                    ""mycall"": """ + myCall + @""", ""mygrid"": """ + myGrid + @""",
                    ""radio"": { ""dialMhz"": 14.074, ""transmitting"": " + (transmitting ? "true" : "false") + @", ""slot"": " + slot + @" },
                    ""recentDecodes"": [" + decodes + "]" + (txNow == null ? "" : @",
                    ""qso"": { ""state"": ""done"", ""txNow"": """ + txNow + @""" }") + @"
                }");
            }

            // Drive a real completion: our roger-report goes out (adds qsoCall to sentReportList),
            // then the engine's RR73 -> LogQso -> callInProg cleared -> Finishing entered.
            void CompleteQso(ulong baseSlot)
            {
                wc.callInProg = qsoCall;
                wc.allCallDict[qsoCall] = new System.Collections.Generic.List<EnqueueDecodeMessage>
                {
                    new EnqueueDecodeMessage { Message = $"{myCall} {qsoCall} R-05", Snr = -5, RxDate = DateTime.UtcNow.Date, SinceMidnight = DateTime.UtcNow.TimeOfDay },
                };
                wc.TestApplyDirectSnapshot(myCall, myGrid, Snap(true, baseSlot, $"{qsoCall} {myCall} R-05"));
                wc.TestApplyDirectSnapshot(myCall, myGrid, Snap(true, baseSlot + 1, $"{qsoCall} {myCall} RR73"));
            }
            // One completed finishing over: engine transmits RR73 to qsoCall, then that over ends.
            void FinishingOver(ulong slot)
            {
                wc.TestApplyDirectSnapshot(myCall, myGrid, Snap(true, slot, $"{qsoCall} {myCall} RR73"));
                wc.TestApplyDirectSnapshot(myCall, myGrid, Snap(false, slot + 1, null));
            }

            // ══ 1. Completion enters Finishing ══
            lock (seenLock) seen.Clear();
            CompleteQso(2000);
            Check("Setup: the completed QSO is logged", wc.logList.Contains(qsoCall), true);
            Check("Setup: callInProg is cleared once the RR73 goes out", wc.callInProg == null, true);
            Check("Finishing entered: _finishingCall is the just-worked station", wc.TestFinishingCall == qsoCall, true);

            // ══ 2. The engine's RR73 closing tail to the worked station is NEVER halted by Jimmy
            //       (Nexus owns it; its own wall-clock watchdog bounds a silent run) ══
            lock (seenLock) seen.Clear();
            for (ulong s = 2010; s < 2030; s += 2) FinishingOver(s);   // 10 closing overs
            Check("10 RR73 closing overs to the worked station do NOT halt and never count as orphans",
                  !SeenCmd("HALT_TX") && wc.TestOrphanTxOvers == 0 && wc.TestFinishingCall == qsoCall, true);

            // ══ 3. A repeated R-report from the worked station is handled entirely by the engine
            //       -- Jimmy neither halts nor changes state ══
            wc.TestApplyDirectSnapshot(myCall, myGrid, Snap(false, 2040, null, qsoCall, $"{myCall} {qsoCall} R-08"));
            for (ulong s = 2042; s < 2050; s += 2) FinishingOver(s);
            Check("a repeated R-report + more RR73 closing overs still do not halt",
                  !SeenCmd("HALT_TX") && wc.TestFinishingCall == qsoCall, true);

            // ══ 4. The worked station's own closing over (73, RR73, or bare RRR) ends Finishing ══
            wc.TestApplyDirectSnapshot(myCall, myGrid, Snap(false, 2060, null, qsoCall, $"{myCall} {qsoCall} 73"));
            Check("the worked station's own 73 clears Finishing -- clean close, nothing halted",
                  wc.TestFinishingCall == null && !SeenCmd("HALT_TX"), true);
            // ...and a bare RRR does it too (fresh contact -> its RRR).
            CompleteQso(2100);
            Check("Setup: Finishing re-entered for the next QSO", wc.TestFinishingCall == qsoCall, true);
            wc.TestApplyDirectSnapshot(myCall, myGrid, Snap(false, 2110, null, qsoCall, $"{myCall} {qsoCall} RRR"));
            Check("a bare RRR from the worked station also clears Finishing", wc.TestFinishingCall == null, true);

            // ══ 5. With Finishing cleared, an UNRELATED orphaned Tx still halts fast (at the 2nd) ══
            lock (seenLock) seen.Clear();
            wc.TestApplyDirectSnapshot(myCall, myGrid, Snap(true, 2220, $"W9XYZ {myCall} RR73"));
            wc.TestApplyDirectSnapshot(myCall, myGrid, Snap(false, 2221, null));
            Check("orphan over #1 (unrelated call, Finishing not active) is tolerated",
                  wc.TestOrphanTxOvers == 1 && !SeenCmd("HALT_TX"), true);
            wc.TestApplyDirectSnapshot(myCall, myGrid, Snap(true, 2222, $"W9XYZ {myCall} RR73"));
            wc.TestApplyDirectSnapshot(myCall, myGrid, Snap(false, 2223, null));
            PumpUntil(() => SeenCmd("HALT_TX") && SeenCmd("SET_TX_ENABLED 0"));
            Check("orphan over #2 trips HALT_TX + SET_TX_ENABLED 0", SeenCmd("HALT_TX") && SeenCmd("SET_TX_ENABLED 0"), true);
            Check("orphan counter resets after firing", wc.TestOrphanTxOvers == 0, true);

            // ══ 6. A completion re-entering Finishing does NOT resurrect a spent orphan halt for
            //       a genuinely unrelated over afterward ══
            lock (seenLock) seen.Clear();
            CompleteQso(2250);
            wc.TestApplyDirectSnapshot(myCall, myGrid, Snap(true, 2260, $"W9XYZ {myCall} RR73"));   // unrelated
            wc.TestApplyDirectSnapshot(myCall, myGrid, Snap(false, 2261, null));
            wc.TestApplyDirectSnapshot(myCall, myGrid, Snap(true, 2262, $"W9XYZ {myCall} RR73"));
            wc.TestApplyDirectSnapshot(myCall, myGrid, Snap(false, 2263, null));
            PumpUntil(() => SeenCmd("HALT_TX"));
            Check("an UNRELATED over during Finishing still halts on the 2nd (Finishing only covers the worked call)",
                  SeenCmd("HALT_TX"), true);

            // ══ 7. A normal idle listen (no contact, never transmitting) trips neither ══
            lock (seenLock) seen.Clear();
            for (ulong s = 2300; s < 2306; s++)
                wc.TestApplyDirectSnapshot(myCall, myGrid, Snap(false, s, null));
            Check("a normal idle LISTEN session does not trip the backstop",
                  wc.TestOrphanTxOvers == 0 && !SeenCmd("HALT_TX"), true);

            wc.ShutdownDirectCommandQueue();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  DirectRunawayRr73HaltsEngineTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            listener.Stop();
            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", prevTestDbPath);
            try { File.Delete(tmpDb); } catch { }
        }
    }

    static DirectSnapshot ParseDirectSnapshot(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<DirectSnapshot>(json, WsjtxClient.DirectJsonOptions);

    // Minimal stub engine host: binds the REAL fixed control port (NativeEngineClient.
    // ControlPort) -- unlike RigctldClient, DirectSendCommand's target host/port is not
    // injectable, so exercising DirectSetFrequency's SUCCESS path needs a listener on the
    // literal port jimmy-engine-host.exe would use. Accepts connections in a loop on a
    // background thread (one connection per command, matching jimmy-engine-host's own
    // run_control_server -- DirectSendCommand opens a fresh TcpClient per call, unlike
    // RigctldClient's one persistent connection) and replies "OK" to whatever line it reads.
    // Returns null instead of throwing if the port is already bound (e.g. a real engine host
    // already running on this machine) -- callers should skip their success-path assertions
    // rather than fail the whole suite over a collision with the developer's own live session.
    // Caller must Stop() a non-null returned listener when done.
    //
    // onCommandReceived (release-audit finding, 2026-08-20, "real validation/coverage for the
    // SET_FREQUENCY Direct contract"): every caller used to only prove "a wire round-trip
    // happened and got OK back", never that the actual JSON payload DirectSetFrequency sent was
    // correct -- a field-name/unit mismatch between WsjtxClient.Direct.cs's DirectSetFrequencyArgs
    // and EngineHost/src/main.rs's SetFrequencyArgs would have passed every one of these tests
    // while silently mistuning the radio in the field. Optional so existing callers that only
    // care about the success path (not the payload) don't need to change.
    static System.Net.Sockets.TcpListener StartStubEngineHost(Action<string> onCommandReceived = null)
    {
        System.Net.Sockets.TcpListener listener;
        try
        {
            listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, NativeEngineClient.ControlPort);
            listener.Start();
        }
        catch (System.Net.Sockets.SocketException)
        {
            return null;
        }
        var t = new System.Threading.Thread(() =>
        {
            try
            {
                while (true)
                {
                    using (var client = listener.AcceptTcpClient())
                    using (var stream = client.GetStream())
                    using (var reader = new System.IO.StreamReader(stream))
                    using (var writer = new System.IO.StreamWriter(stream) { AutoFlush = true, NewLine = "\n" })
                    {
                        string line = reader.ReadLine();
                        if (line != null)
                        {
                            onCommandReceived?.Invoke(line);
                            writer.WriteLine("OK");
                        }
                    }
                }
            }
            catch { /* listener.Stop() during teardown breaks AcceptTcpClient -- harmless */ }
        });
        t.IsBackground = true;
        t.Start();
        return listener;
    }

    // T7/T8 regression coverage: like StartStubEngineHost above, but each connection's response
    // is computed per command line via `respond` (e.g. to make one specific command return ERR
    // while everything else returns OK) and connections are handled concurrently, one thread
    // each -- matching the real EngineHost's own per-connection-thread accept loop (main.rs's
    // run_control_server) rather than the serial accept loop above. `respond` returning null
    // holds that one connection open without ever answering or closing it, simulating a
    // stuck/hung command -- the caller must eventually Stop() the listener to release it.
    static System.Net.Sockets.TcpListener StartStubEngineHostWithResponses(Func<string, string> respond)
    {
        System.Net.Sockets.TcpListener listener;
        try
        {
            listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, NativeEngineClient.ControlPort);
            listener.Start();
        }
        catch (System.Net.Sockets.SocketException)
        {
            return null;
        }
        var acceptThread = new System.Threading.Thread(() =>
        {
            try
            {
                while (true)
                {
                    var client = listener.AcceptTcpClient();
                    var connThread = new System.Threading.Thread(() =>
                    {
                        try
                        {
                            using (client)
                            using (var stream = client.GetStream())
                            using (var reader = new System.IO.StreamReader(stream))
                            {
                                string line = reader.ReadLine();
                                string response = line != null ? respond(line) : null;
                                if (response != null)
                                {
                                    using (var writer = new System.IO.StreamWriter(stream) { AutoFlush = true, NewLine = "\n" })
                                        writer.WriteLine(response);
                                }
                                else
                                {
                                    // Hold open long enough for the test to exercise whatever
                                    // "stuck command" behavior it needs (well past any bounded
                                    // wait a test itself uses), but bounded -- not
                                    // Timeout.Infinite -- so this thread and its socket always
                                    // clean up on their own shortly after, rather than leaking
                                    // for the rest of the whole ~1000-test process's lifetime
                                    // and adding ambient thread/socket load to unrelated later
                                    // tests. listener.Stop() during the test's own teardown
                                    // still breaks this early via exception, caught below.
                                    System.Threading.Thread.Sleep(6000);
                                }
                            }
                        }
                        catch { /* connection aborted by AbortInFlightDirectCommand or teardown -- expected */ }
                    });
                    connThread.IsBackground = true;
                    connThread.Start();
                }
            }
            catch { /* listener.Stop() during teardown breaks AcceptTcpClient -- harmless */ }
        });
        acceptThread.IsBackground = true;
        acceptThread.Start();
        return listener;
    }

    // ── Alt+Q / Tune / F11-F12 fix, 2026-08-10 ──────────────────────────────────────────────
    // Covers the parts of that fix that are deterministic and testable without a live
    // jimmy-engine-host.exe process: DirectApplyStatus wiring the engine's own `tuning` flag
    // through, AudioLevel()'s new tuning-OR-transmitting guard, RxLevelToDb's dB conversion, and
    // -- found live, right after the fix first shipped -- ToggleTuningProcess incorrectly
    // claiming "Tune started" (and letting F11/F12 proceed) even when the engine host wasn't
    // reachable at all, because it flipped its own `tuning` field unconditionally instead of on
    // confirmed success. No test here starts a real engine host, so DirectSetTuning/SNAPSHOT
    // calls always fail to connect -- exactly the classic WSJT-X/UDP-mode scenario the live bug
    // was found in.
    static void AudioTuningHotkeyTests()
    {
        Console.WriteLine("\n── Alt+Q / Tune / F11-F12: tuning guard + dB conversion ──");
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);

            // DirectApplyStatus must set the class-level `tuning` field from radio.tuning, the
            // same way it already did for `transmitting` -- without this, AudioLevel()'s new
            // guard below would always read tuning=false regardless of what the engine reports.
            var snapTuning = ParseDirectSnapshot(@"{
                ""mycall"": ""KB0UZT"", ""mygrid"": ""FN42"",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""tuning"": true, ""slot"": 2000 },
                ""recentDecodes"": []
            }");
            wc.TestApplyDirectSnapshot("KB0UZT", "FN42", snapTuning);
            Check("DirectApplyStatus: radio.tuning=true is applied to the class-level tuning field",
                  wc.tuning, true);

            // AudioLevel()'s new guard: `!transmitting && !tuning` -- with tuning now true (and
            // transmitting still false), it must proceed past the early guard (return true, not
            // the blocked-early false) even though no real engine host is reachable to actually
            // apply anything -- the guard change is what's under test, not the SNAPSHOT round-trip.
            Check("AudioLevel: proceeds (does not early-return false) while tuning, even though not transmitting",
                  wc.AudioLevel(true), true);

            var snapIdle = ParseDirectSnapshot(@"{
                ""mycall"": ""KB0UZT"", ""mygrid"": ""FN42"",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""tuning"": false, ""slot"": 2001 },
                ""recentDecodes"": []
            }");
            wc.TestApplyDirectSnapshot("KB0UZT", "FN42", snapIdle);
            Check("AudioLevel: still blocked (returns false) when neither transmitting nor tuning -- baseline unchanged",
                  wc.AudioLevel(true), false);

            // The exact live bug: ToggleTuningProcess must NOT claim "Tune started" (by flipping
            // its own `tuning` field) when DirectSetTuning couldn't even connect -- no engine
            // host process exists in this test, so this is guaranteed to fail to connect, just
            // like classic WSJT-X/UDP mode (nativeEngineUseDirectEngine=False) does live.
            wc.tuning = false;
            wc.ToggleTuningProcess();
            Check("ToggleTuningProcess: does NOT optimistically claim tuning=true when the engine host is unreachable",
                  wc.tuning, false);

            // RxLevelToDb: pure conversion, mirrors Nexus's own canonical formula
            // (ui/src/components/LevelMeter.tsx's rxLevelDb) exactly -- 20*log10(rms) + 90.3,
            // clamped [0, 90].
            Check("RxLevelToDb: silence (0.0 RMS) reads 0 dB, not -infinity",
                  WsjtxClient.RxLevelToDb(0.0) == 0.0, true);
            double fullScale = WsjtxClient.RxLevelToDb(1.0);
            Check("RxLevelToDb: full-scale (1.0 RMS) reads the clamped max, 90 dB",
                  Math.Abs(fullScale - 90.0) < 0.001, true);
            // A healthy FT8 input (per the formula's own doc comment, "~30 dB") is roughly
            // rms=0.03 -- 20*log10(0.03)+90.3 = 60.86, which is inside the formula's own
            // documented "decodes fine ~15-60" window, confirming the constant (90.3) actually
            // produces numbers in the range WSJT-X operators expect, not just "doesn't crash".
            double healthy = WsjtxClient.RxLevelToDb(0.03);
            Check("RxLevelToDb: a typical healthy RX level lands within the documented 15-60 dB decode-fine window",
                  healthy >= 15.0 && healthy <= 60.0, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  AudioTuningHotkeyTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── "Explain meter readings" (Options ▸ Radio, RadioSettings.ExplainMeterReadings), 2026-08-28 ──
    // Alt+Q's optional verbose form: WsjtxClient.BandAudio.cs's SwrHint / AlcHint / AudioInHint /
    // SmeterToSUnits are pure functions, tested directly here the same way RxLevelToDb is (they
    // are the deterministic half of ReportPowerSwr, whose transmit/receive branching needs a live
    // engine host to exercise). Also documents the read-only-audit fix: the S-meter is a
    // receive-only reading and no longer belongs in the transmit branch at all.
    static void MeterReadingHintTests()
    {
        Console.WriteLine("\n── Explain meter readings: SWR / ALC / audio-in / S-meter hint wording ──");
        try
        {
            // SWR: 1.0 perfect, folding back / antenna trouble by ~3.
            Check("SwrHint: 1.0 is good", WsjtxClient.SwrHint(1.0) == "good", true);
            Check("SwrHint: 1.5 is good", WsjtxClient.SwrHint(1.5) == "good", true);
            Check("SwrHint: 1.8 is acceptable", WsjtxClient.SwrHint(1.8) == "acceptable", true);
            Check("SwrHint: 2.7 is high", WsjtxClient.SwrHint(2.7) == "high", true);
            Check("SwrHint: 4.0 is very high, check antenna", WsjtxClient.SwrHint(4.0) == "very high, check antenna", true);

            // ALC: near zero is the target for FT8/FT4.
            Check("AlcHint: 0.0 is clean", WsjtxClient.AlcHint(0.0) == "clean", true);
            Check("AlcHint: 0.05 is clean", WsjtxClient.AlcHint(0.05) == "clean", true);
            Check("AlcHint: 0.15 is a little high, reduce audio", WsjtxClient.AlcHint(0.15) == "a little high, reduce audio", true);
            Check("AlcHint: 0.40 is high, reduce audio", WsjtxClient.AlcHint(0.40) == "high, reduce audio", true);

            // Audio-in: RxLevelToDb's own 0-90 scale; ~15-60 decodes well.
            Check("AudioInHint: 8 dB is low", WsjtxClient.AudioInHint(8) == "low", true);
            Check("AudioInHint: 31 dB is good", WsjtxClient.AudioInHint(31) == "good", true);
            Check("AudioInHint: 65 dB is hot", WsjtxClient.AudioInHint(65) == "hot", true);
            Check("AudioInHint: 80 dB is too hot, clipping", WsjtxClient.AudioInHint(80) == "too hot, clipping", true);

            // S-meter: Hamlib reports dB relative to S9 (S9 = 0 dB, ~6 dB per S-unit). Spoken as
            // S-units -- the "-12 dB" bare number is exactly what confused the operators.
            Check("SmeterToSUnits: 0 dB is S9", WsjtxClient.SmeterToSUnits(0) == "S9", true);
            Check("SmeterToSUnits: -12 dB rel S9 is S7", WsjtxClient.SmeterToSUnits(-12) == "S7", true);
            Check("SmeterToSUnits: -48 dB rel S9 is about S1", WsjtxClient.SmeterToSUnits(-48) == "S1", true);
            Check("SmeterToSUnits: an off-scale weak reading clamps to S1, never S0 or negative",
                  WsjtxClient.SmeterToSUnits(-90) == "S1", true);
            Check("SmeterToSUnits: +20 dB over S9 is spoken as S9 plus 20 dB", WsjtxClient.SmeterToSUnits(20) == "S9 plus 20 dB", true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  MeterReadingHintTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── SetOperatingMode: a failed tier switch must not change local mode, 2026-08-19 ───────
    // Codex release audit: SetOperatingMode used to call DirectSetTier, discard whether the
    // engine confirmed it, and set `mode` unconditionally right after. An unreachable engine, a
    // dropped connection, a timed-out read, or an explicit ERR reply all left Jimmy believing it
    // was on the new mode while the engine -- and the real FT8/FT4 decode/TX cycle on the air --
    // silently stayed on the old one. Same bug class, same no-real-engine test strategy, as
    // AudioTuningHotkeyTests' own ToggleTuningProcess coverage above: no engine host exists in
    // this test, so DirectSetTier's SET_TIER command is guaranteed to fail to connect -- exactly
    // what a failed Alt+M hits live.
    static void SetOperatingModeFailureDoesNotChangeLocalModeTests()
    {
        Console.WriteLine("\n── SetOperatingMode: a failed tier switch does not change local mode ──");
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.TestSetMode("FT8");
            wc.TestSetDirectConnected(true);

            bool ok = wc.SetOperatingMode("FT4");
            Check("SetOperatingMode still returns true (hotkey stays 'handled') even though the engine never confirmed",
                ok, true);
            Check("...but local mode was NOT changed to FT4 -- THE FIX (used to flip unconditionally)",
                wc.CurrentMode == "FT8", true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  SetOperatingModeFailureDoesNotChangeLocalModeTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── "Remember F11/F12 audio level per band" restore decision, 2026-08-17 ───────────────
    // Found live: the restore-on-band-change call only ever existed on WsjtxClient.Direct.cs's
    // poll path -- the classic WSJT-X/UDP StatusMessage band-change handler
    // (WsjtxClient.Protocol.cs) had no restore call at all, so an operator not on pure
    // Direct-mode-with-Jimmy-Native got a feature that silently SAVED (AudioLevel() reaches the
    // engine's own control port directly, independent of transport) but never restored. Fixed by
    // sharing one decision function (ShouldRestoreTxLevel) from both call sites. This tests the
    // pure decision only -- the actual SET_TX_LEVEL send needs a live engine host and isn't
    // covered here, same as every other DirectSendCommand-driven behavior in this suite.
    static void TxLevelPerBandRestoreTests()
    {
        Console.WriteLine("\n── Remember TX level per band: restore decision ──");
        try
        {
            var levels = new Dictionary<int, double> { [5] = 0.65, [9] = 0.40 };  // 20m, 10m

            Check("Feature disabled -> never restores, even with a saved entry",
                WsjtxClient.ShouldRestoreTxLevel(false, 5, levels, out _), false);

            Check("No band known (null) -> never restores",
                WsjtxClient.ShouldRestoreTxLevel(true, null, levels, out _), false);

            Check("Band with no saved entry -> does not restore",
                WsjtxClient.ShouldRestoreTxLevel(true, 7, levels, out _), false);

            bool restored20m = WsjtxClient.ShouldRestoreTxLevel(true, 5, levels, out double level20m);
            Check("Enabled + known band + saved entry -> restores", restored20m, true);
            Check("...with the exact saved value for that band", Math.Abs(level20m - 0.65) < 0.0001, true);

            bool restored10m = WsjtxClient.ShouldRestoreTxLevel(true, 9, levels, out double level10m);
            Check("A different band's own saved value is used, not the first band checked",
                restored10m && Math.Abs(level10m - 0.40) < 0.0001, true);

            Check("Null dictionary -> does not restore, does not throw",
                WsjtxClient.ShouldRestoreTxLevel(true, 5, null, out _), false);

            // Save-side companion (confirmation-gated remember, 2026-08-30): a just-CONFIRMED
            // SET_TX_LEVEL is written into the per-band map under the same gate the old inline
            // AudioLevel() code used -- feature on AND a real band index. DirectSetEngineTxLevel
            // only calls this on the engine's OK, so nothing is remembered before confirmation.
            Check("Remember decision: feature off -> not remembered",
                WsjtxClient.ShouldRememberTxLevelForBand(false, 5, out _), false);
            Check("Remember decision: no band known -> not remembered",
                WsjtxClient.ShouldRememberTxLevelForBand(true, null, out _), false);
            bool remembered = WsjtxClient.ShouldRememberTxLevelForBand(true, 5, out int rememberKey);
            Check("Remember decision: feature on + known band -> remembered under that band's key",
                remembered && rememberKey == 5, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  TxLevelPerBandRestoreTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── TX-level-per-band: real band tracking through the production Direct pipeline ───────
    // Companion to TxLevelPerBandRestoreTests above: that one covers the pure decision function
    // in isolation; this one proves the band-index tracking that decision depends on is correct
    // going through DirectApplyStatus itself (via TestApplyDirectSnapshot -- the exact same
    // pipeline the real ~1s SNAPSHOT poll drives in production, which is Jimmy Next's ONLY
    // production transport: ApplyEngineMode always uses Direct outside of
    // TestModeGuard.IsTestMode, so this -- not the classic WSJT-X/UDP StatusMessage handler in
    // WsjtxClient.Protocol.cs, which only ever runs in replay tests -- is the real path an
    // operator's F11/F12 press and band change actually go through). Feeds a real switch away
    // from and back to a band, then confirms ShouldRestoreTxLevel would pick the right saved
    // level at each stop -- exactly "switch bands, come back, get the level I had there."
    static void DirectPathTxLevelBandTrackingTests()
    {
        Console.WriteLine("\n── TX level per band: real band tracking (Direct pipeline) ──");
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);

            ctrl.Radio.RememberTxLevelPerBand = true;
            ctrl.Radio.TxLevelByBand[5] = 0.70;   // 20m, saved earlier
            ctrl.Radio.TxLevelByBand[3] = 0.30;   // 40m, saved earlier

            var snap20m = ParseDirectSnapshot(@"{
                ""mycall"": ""KB0UZT"", ""mygrid"": ""FN42"",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""tuning"": false, ""slot"": 3000 },
                ""recentDecodes"": []
            }");
            wc.TestApplyDirectSnapshot("KB0UZT", "FN42", snap20m);
            Check("Initial snapshot on 20m -> bandIdx resolves to 20m (index 5)",
                wc.TestBandIdx == 5, true);
            bool restore20a = WsjtxClient.ShouldRestoreTxLevel(ctrl.Radio.RememberTxLevelPerBand, wc.TestBandIdx, ctrl.Radio.TxLevelByBand, out double v20a);
            Check("...and the real tracked band correctly picks 20m's own saved level",
                restore20a && Math.Abs(v20a - 0.70) < 0.0001, true);

            var snap40m = ParseDirectSnapshot(@"{
                ""mycall"": ""KB0UZT"", ""mygrid"": ""FN42"",
                ""radio"": { ""dialMhz"": 7.074, ""transmitting"": false, ""tuning"": false, ""slot"": 3001 },
                ""recentDecodes"": []
            }");
            wc.TestApplyDirectSnapshot("KB0UZT", "FN42", snap40m);
            Check("Switching to 40m -> bandIdx follows the real dial frequency (index 3)",
                wc.TestBandIdx == 3, true);
            bool restore40 = WsjtxClient.ShouldRestoreTxLevel(ctrl.Radio.RememberTxLevelPerBand, wc.TestBandIdx, ctrl.Radio.TxLevelByBand, out double v40);
            Check("...and picks 40m's own saved level, not 20m's",
                restore40 && Math.Abs(v40 - 0.30) < 0.0001, true);

            wc.TestApplyDirectSnapshot("KB0UZT", "FN42", snap20m);
            Check("Returning to 20m -> bandIdx correctly comes back to index 5, not stuck on 40m",
                wc.TestBandIdx == 5, true);
            bool restore20b = WsjtxClient.ShouldRestoreTxLevel(ctrl.Radio.RememberTxLevelPerBand, wc.TestBandIdx, ctrl.Radio.TxLevelByBand, out double v20b);
            Check("...and restores the ORIGINAL 20m level again -- the exact 'switch away and back' case",
                restore20b && Math.Abs(v20b - 0.70) < 0.0001, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  DirectPathTxLevelBandTrackingTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── Direct-path: txEnabled reconciled from the engine's own snapshot, 2026-08-19 ────────
    // Release-blocker follow-up, root-caused live: the engine can disable its own tx_enabled
    // independently (its own QSO-sequencer/retry logic -- confirmed live via a real SNAPSHOT
    // query showing the engine's real txEnabled already false while Jimmy's own field still
    // read true, with no HALT_TX ever sent). Before this fix, Jimmy's own `txEnabled` was ONLY
    // ever written locally by EnableTx()/DisableTx() at the moment JIMMY commands a change --
    // Direct mode had no way to learn the engine changed its mind on its own, which is exactly
    // what left a real operator's callInProg stuck forever: DiscardCall()'s own "give up" check
    // (WsjtxClient.cs) requires `(txMode==LISTEN && !txEnabled) || txMode==CALL_CQ` to actually
    // take effect, and EnableMode() (Alt+E)'s own resume logic requires `!txEnabled` too -- both
    // silently no-op while Jimmy's stale belief says Tx is still enabled.
    static void DirectPathTxEnabledReconciliationTests()
    {
        Console.WriteLine("\n── Direct-path: txEnabled reconciled from the engine's own snapshot ──");
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);

            var snapEnabled = ParseDirectSnapshot(@"{
                ""mycall"": ""KB0UZT"", ""mygrid"": ""FN42"",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""tuning"": false, ""slot"": 5000, ""txEnabled"": true },
                ""recentDecodes"": []
            }");
            wc.TestApplyDirectSnapshot("KB0UZT", "FN42", snapEnabled);
            Check("A snapshot reporting the engine's txEnabled=true updates Jimmy's own belief",
                wc.TestTxEnabled, true);

            // THE FIX: a later snapshot reporting the engine turned itself off, with nothing on
            // Jimmy's own side having called DisableTx() -- must still be reflected.
            var snapDisabled = ParseDirectSnapshot(@"{
                ""mycall"": ""KB0UZT"", ""mygrid"": ""FN42"",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""tuning"": false, ""slot"": 5001, ""txEnabled"": false },
                ""recentDecodes"": []
            }");
            wc.TestApplyDirectSnapshot("KB0UZT", "FN42", snapDisabled);
            Check("A later snapshot reporting the engine disabled txEnabled on its own updates Jimmy's belief -- THE FIX",
                wc.TestTxEnabled, false);

            // And it tracks back the other way too -- not a one-way latch.
            wc.TestApplyDirectSnapshot("KB0UZT", "FN42", snapEnabled);
            Check("A snapshot reporting txEnabled=true again updates it back -- reconciliation is bidirectional",
                wc.TestTxEnabled, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  DirectPathTxEnabledReconciliationTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── _pendingBandIdx: cleared on Direct-path confirmation, 2026-08-17 ────────────────────
    // Root-caused from a "radio clicks twice on a band-change hotkey" report: WsjtxClient.
    // Protocol.cs's classic UDP StatusMessage handler always clears _pendingBandIdx the moment
    // a real confirmed bandIdx arrives ("drop any optimistic guess"), but that mirroring was
    // never carried over when Direct.cs's own bandIdx assignment was added (2026-08-10) -- so on
    // the ONLY production transport, _pendingBandIdx stuck at whatever Jimmy last REQUESTED,
    // forever, regardless of what the radio actually confirmed afterward. BandUp/BandDown prefer
    // _pendingBandIdx over the real bandIdx by design (so repeated presses before a CAT
    // round-trip lands keep advancing), so a stale value computes the WRONG next band once it
    // has drifted from reality -- demonstrated below via a manual/external band change (a real
    // confirmed snapshot for a band Jimmy never requested), exactly the shape of drift a long
    // session could accumulate.
    static void DirectPathPendingBandIdxClearedOnConfirmationTests()
    {
        Console.WriteLine("\n── _pendingBandIdx: real confirmation clears the stale optimistic guess (Direct pipeline) ──");
        // Band retunes now go through DirectSetFrequency (SET_FREQUENCY on the engine's own
        // control port), not RigctldClient.SetFrequency -- since the RetuneBand failure-handling
        // fix (Codex release audit, 2026-08-19), _pendingBandIdx is only ever set once that
        // command is actually attempted, so the BandDown() calls below need a real accept + wire
        // round-trip against a stub engine host to set it at all. RetuneBand's gate is now just
        // RadioControlMode.HamlibRigctld (2026-08-20: ctrl.rigctldClient itself is retired --
        // it had no remaining functional consumer once AudioLevel()'s AF-gain fallback and the
        // old rigctld-based polling were both gone), so no dummy client is needed here anymore.
        // Captures the raw SET_FREQUENCY line the stub actually receives, so the assertions
        // below can check the real JSON payload's hz/band/mode fields, not just that a wire
        // round-trip happened and came back OK.
        var lastCommand = new string[1];
        var engineListener = StartStubEngineHost(line => lastCommand[0] = line);
        if (engineListener == null)
        {
            Skip("DirectPathPendingBandIdxClearedOnConfirmationTests", "engine control port already in use on this machine");
            return;
        }
        try
        {
            var ctrl = new Controller();
            var _ = ctrl.Handle; // force handle creation -- RetuneBand's ctrl.BeginInvoke below needs it
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.Radio.Mode = RadioControlMode.HamlibRigctld;
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);

            var snap20m = ParseDirectSnapshot(@"{
                ""mycall"": ""KB0UZT"", ""mygrid"": ""FN42"",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""tuning"": false, ""slot"": 3000 },
                ""recentDecodes"": []
            }");
            wc.TestApplyDirectSnapshot("KB0UZT", "FN42", snap20m);
            Check("Initial confirmed snapshot on 20m -> bandIdx resolves to index 5",
                wc.TestBandIdx == 5, true);
            Check("...and _pendingBandIdx starts clear (nothing requested yet)",
                wc.TestPendingBandIdx == null, true);

            bool downOk = wc.BandDown();
            Check("BandDown() from 20m succeeds", downOk, true);
            Check("...and requests 30m (index 4) as the optimistic pending target",
                wc.TestPendingBandIdx == 4, true);

            // Release-audit finding, 2026-08-20: prove the actual SET_FREQUENCY wire payload is
            // correct, not just that a round-trip happened and came back OK -- a field-name/unit
            // mismatch between DirectSetFrequencyArgs (WsjtxClient.Direct.cs) and SetFrequencyArgs
            // (EngineHost/src/main.rs) would otherwise pass every existing test in this file while
            // silently mistuning the radio.
            //
            // Release-audit finding, 2026-08-21: RetuneBand's actual DirectSetFrequency call now
            // runs on a background Task (see its own comment) -- wait for the stub to actually
            // receive it rather than checking immediately after BandDown() returns.
            PumpUntil(() => lastCommand[0] != null);
            const string prefix = "SET_FREQUENCY ";
            string cmd = lastCommand[0];
            Check("BandDown() actually sent a SET_FREQUENCY command",
                cmd != null && cmd.StartsWith(prefix), true);
            if (cmd != null && cmd.StartsWith(prefix))
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(cmd.Substring(prefix.Length)))
                {
                    var root = doc.RootElement;
                    double hz = root.GetProperty("hz").GetDouble();
                    string band = root.GetProperty("band").GetString();
                    string sentMode = root.GetProperty("mode").GetString();
                    CheckStr("...requesting band '30m'", band, "30m");
                    CheckStr("...requesting mode 'USB' (FT8/FT4 default)", sentMode, "USB");
                    // 30m's own primary/CQ frequency is 10.136 MHz (matches snap30m below) --
                    // loose tolerance, this is checking the right BAND was requested, not
                    // re-deriving bands[4]'s exact table value here.
                    Check("...requesting a frequency actually within 30m (10.100-10.150 MHz)",
                        hz >= 10_100_000 && hz <= 10_150_000, true);
                }
            }

            var snap30m = ParseDirectSnapshot(@"{
                ""mycall"": ""KB0UZT"", ""mygrid"": ""FN42"",
                ""radio"": { ""dialMhz"": 10.136, ""transmitting"": false, ""tuning"": false, ""slot"": 3001 },
                ""recentDecodes"": []
            }");
            wc.TestApplyDirectSnapshot("KB0UZT", "FN42", snap30m);
            Check("Real confirmation arrives matching the request -> bandIdx follows (30m, index 4)",
                wc.TestBandIdx == 4, true);
            // THE FIX: without it, _pendingBandIdx stayed at 4 forever from here on, even
            // though it had already served its purpose (the real bandIdx now agrees).
            Check("...and _pendingBandIdx is cleared back to null -- THE FIX (was permanently stuck under Direct before this)",
                wc.TestPendingBandIdx == null, true);

            // Now simulate a band change Jimmy itself never requested -- the operator manually
            // spins the VFO to 60m (index 2), or any other external change. A real confirmed
            // snapshot for this must still update bandIdx AND clear any (here, already-clear)
            // pending guess, so the NEXT hotkey press computes from reality, not a stale target.
            var snap60mManual = ParseDirectSnapshot(@"{
                ""mycall"": ""KB0UZT"", ""mygrid"": ""FN42"",
                ""radio"": { ""dialMhz"": 5.357, ""transmitting"": false, ""tuning"": false, ""slot"": 3002 },
                ""recentDecodes"": []
            }");
            wc.TestApplyDirectSnapshot("KB0UZT", "FN42", snap60mManual);
            Check("External/manual confirmation to 60m (index 2, never requested via BandUp/BandDown) -> bandIdx follows reality",
                wc.TestBandIdx == 2, true);
            Check("...and _pendingBandIdx has nothing stale left to clear (stays null)",
                wc.TestPendingBandIdx == null, true);

            // The actual bug this fix prevents: BandDown() from the REAL current band (60m) must
            // target 80m (index 1) -- the operator's actual next-lower band. Before the fix,
            // _pendingBandIdx would still have read 4 (30m, the last thing Jimmy itself
            // requested, several steps back) instead of null, so this would have silently
            // computed 40m (index 3) instead -- retuning the radio to a band the operator never
            // asked for and never stood on.
            bool downFromManual = wc.BandDown();
            Check("BandDown() from the real (manually-confirmed) 60m succeeds", downFromManual, true);
            Check("...and correctly targets 80m (index 1) -- NOT 40m (index 3), the stale-pending-index bug's wrong answer",
                wc.TestPendingBandIdx == 1, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  DirectPathPendingBandIdxClearedOnConfirmationTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            engineListener.Stop();
        }
    }

    // ── DirectInitialConnect: startup ALWAYS restores the last exact confirmed FT8/FT4 dial ──
    // Release-audit finding, 2026-08-21, operator directive ("always force it, no setting"):
    // the original 2026-08-20 startup fallback only corrected a totally UNRECOGNIZED band --
    // a radio already reporting a recognized-but-different band (even a non-digital CW/phone
    // frequency, or simply a different ham band than last session) was left alone. This test
    // proves the broadened behavior: even though the very first snapshot reports a perfectly
    // valid, recognized band (15m), Jimmy must still retune back to the operator's own last
    // CONFIRMED exact dial from a prior session (20m/14.074) rather than accepting 15m as
    // "good enough".
    static void DirectInitialConnectAlwaysRestoresLastExactDialTests()
    {
        Console.WriteLine("\n── DirectInitialConnect: always restore last exact confirmed dial, even over a recognized band -- THE FIX ──");
        var lastCommand = new string[1];
        var engineListener = StartStubEngineHost(line => lastCommand[0] = line);
        if (engineListener == null)
        {
            Skip("DirectInitialConnectAlwaysRestoresLastExactDialTests", "engine control port already in use on this machine");
            return;
        }
        try
        {
            var ctrl = new Controller();
            var _ = ctrl.Handle; // force handle creation -- RetuneBand's ctrl.BeginInvoke below needs it
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.Radio.Mode = RadioControlMode.HamlibRigctld;
            // Prior session: confirmed on 20m/14.074 MHz, FT8 -- persisted, exactly as
            // DirectApplyStatus's own "persist whenever a real band is confirmed" write would
            // have left it.
            ctrl.Radio.LastDialFrequencyHz = 14074000;
            ctrl.Radio.LastTier = "FT8";
            ctrl.Radio.LastBandIdx = 5; // 20m
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);

            // First snapshot this session reports 21.074 MHz -- 15m, a perfectly valid,
            // RECOGNIZED band, just not the one the operator was actually on last time.
            var snap15m = ParseDirectSnapshot(@"{
                ""mycall"": ""KB0UZT"", ""mygrid"": ""FN42"",
                ""radio"": { ""dialMhz"": 21.074, ""transmitting"": false, ""tuning"": false, ""slot"": 3000 },
                ""recentDecodes"": []
            }");
            wc.TestApplyDirectSnapshot("KB0UZT", "FN42", snap15m);
            Check("First snapshot resolves to a real, recognized band (15m, index 7) -- not 'unknown'",
                wc.TestBandIdx == 7, true);

            // THE FIX: despite 15m being perfectly recognized, Jimmy must still send a real
            // SET_FREQUENCY restoring 20m/14.074 -- the last thing the operator actually
            // confirmed, not wherever the radio happened to power up.
            //
            // 8000ms, not PumpUntil's own 3000ms default -- found intermittently timing out at
            // 3000ms during a full-suite run (2026-08-21): this is the single test in the whole
            // suite that most depends on the ordered Direct-command dispatcher's own background
            // Task actually getting scheduled promptly, and a full ~1000-test run in one process
            // leaves a lot of real sockets/threads/timers active by the time this one runs, which
            // can occasionally add a second or two of pure OS/CLR scheduling jitter before the
            // dispatcher's Task.Run even starts -- not a logic bug (the command IS always sent
            // correctly; every one of many other DirectInitialConnect/dispatcher tests confirms
            // that), just an occasionally-too-tight margin for a real network round-trip inside a
            // large concurrent test process.
            PumpUntil(() => lastCommand[0] != null, timeoutMs: 8000);
            const string prefix = "SET_FREQUENCY ";
            string cmd = lastCommand[0];
            Check("Startup sent a SET_FREQUENCY command despite the current band already being recognized -- THE FIX",
                cmd != null && cmd.StartsWith(prefix), true);
            if (cmd != null && cmd.StartsWith(prefix))
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(cmd.Substring(prefix.Length)))
                {
                    var root = doc.RootElement;
                    double hz = root.GetProperty("hz").GetDouble();
                    string band = root.GetProperty("band").GetString();
                    CheckStr("...restoring band '20m' (the last CONFIRMED band, not the current 15m)", band, "20m");
                    Check("...restoring the exact confirmed dial, 14.074 MHz (within 1 Hz)",
                        Math.Abs(hz - 14074000) < 1.0, true);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  DirectInitialConnectAlwaysRestoresLastExactDialTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            engineListener.Stop();
        }
    }

    // ── Startup/restart mode-sync fix, 2026-08-24 (independent audit finding, CONFIRMED live):
    // a startup tier restore (FT8 -> FT4) must also reset trPeriod, not just `mode`/newMode ──
    // jimmy-engine-host always starts hardcoded to FT8 (main.rs's own startup set_tier call), so
    // DirectApplyStatus's own lazy fallback ("if (string.IsNullOrEmpty(this.mode)) this.mode =
    // 'FT8'") always wins the FIRST poll, before DirectInitialConnect's own tier-restore lands --
    // and UpdateTrPeriod (WsjtxClient.Protocol.cs) computes trPeriod from THAT stale mode and
    // then never re-derives it (guarded on "trPeriod == null"), so a real FT4 session was left
    // with trPeriod permanently stuck at FT8's 15000ms for the rest of the session -- not
    // cosmetic: trPeriod directly drives even/odd period-parity math (WsjtxClient.cs's own
    // IsEvenPeriod) and call-queue age expiry (CallQueueStore.cs). Confirmed via a real
    // diagnostic log: LastTier="FT4" restored correctly and fast (~5ms), but nothing before this
    // fix re-derived trPeriod or told the UI about the correction (SetOperatingMode, the
    // OPERATOR-driven Alt+M equivalent, already resets both `newMode`/`trPeriod` on every live
    // switch -- this closes the same gap for a STARTUP restore). Drives the real production
    // startup path end to end (a real stub engine host, DirectSetTier's own real network
    // round trip) rather than asserting on isolated fields.
    static void DirectInitialConnectResyncsTierAndPeriodTests()
    {
        Console.WriteLine("\n── Startup/restart mode-sync fix: tier restore also re-derives trPeriod and announces the corrected mode -- THE FIX ──");
        var engineListener = StartStubEngineHostWithResponses(line => "OK");
        if (engineListener == null)
        {
            Skip("DirectInitialConnectResyncsTierAndPeriodTests", "engine control port already in use on this machine");
            return;
        }
        try
        {
            var ctrl = new Controller();
            var _ = ctrl.Handle; // force handle creation -- DirectSetTier's completion runs via ctrl.BeginInvoke
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.Radio.Mode = RadioControlMode.HamlibRigctld;
            // Prior session: confirmed on 30m/10.140 MHz, FT4 -- exactly the reported real-launch
            // shape (left on 30m FT4, restarted).
            ctrl.Radio.LastDialFrequencyHz = 10140000;
            ctrl.Radio.LastTier = "FT4";
            ctrl.Radio.LastBandIdx = 4; // 30m
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            // Retest finding, 2026-08-24: newMode=true alone only primed the NEXT unrelated
            // ShowStatus() render -- on the real retest that render could be many seconds away
            // (or never come before the operator acted on the stale text), so the restore must
            // announce itself immediately. Capture RenderStatus calls directly to prove this,
            // rather than inferring it indirectly through trPeriod.
            var fakeStatusView = new FakeStatusView();
            wc.StatusView = fakeStatusView;
            // TestApplyDirectSnapshot bypasses ConnectDirectEngine/DirectPollTick's own response
            // handler entirely (that's the point -- it drives DirectApplyStatus directly), but in
            // real operation that handler always sets NegoState->RECD on the first successful
            // poll BEFORE DirectApplyStatus ever runs (WsjtxClient.Direct.cs, ~line 668). Left at
            // its real default (WAIT) here, ShowStatus() takes its own WAIT-only early-return
            // branch and renders nothing but the generic "connecting" placeholder -- masking
            // exactly the render this test exists to check. Match production's real ordering.
            WsjtxMessage.NegoState = WsjtxMessage.NegoStates.RECD;

            // First snapshot this session -- the engine's own hardcoded FT8 startup default,
            // reporting a real dial/band so band resolution succeeds. this.mode is still empty at
            // this point (a fresh WsjtxClient), so DirectApplyStatus's own lazy fallback sets it
            // to "FT8" while processing THIS exact snapshot -- the stale value the tier restore
            // (triggered by this same snapshot) must correct.
            var snap = ParseDirectSnapshot(@"{
                ""mycall"": ""KB0UZT"", ""mygrid"": ""FN42"",
                ""radio"": { ""dialMhz"": 10.140, ""transmitting"": false, ""tuning"": false, ""slot"": 3000 },
                ""recentDecodes"": []
            }");
            wc.TestApplyDirectSnapshot("KB0UZT", "FN42", snap);
            // Proves the race is real, not hypothetical: the tier restore this snapshot just
            // triggered is asynchronous (a real dispatcher round trip -- see DirectSetTier's own
            // comment), so immediately after this synchronous call returns, mode must still read
            // the stale optimistic default -- if this assertion ever fails, the race window
            // closed on its own and everything below would be testing the wrong thing.
            CheckStr("Setup: the first poll's own lazy fallback used the stale 'FT8' default -- the restore hasn't landed yet",
                wc.CurrentMode, "FT8");

            // THE FIX proves out over the real dispatcher -- bounded wait for DirectSetTier's own
            // real network round trip (matches DirectInitialConnectAlwaysRestoresLastExactDialTests'
            // own documented worst-case margin).
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (wc.CurrentMode != "FT4" && sw.ElapsedMilliseconds < 8000)
            {
                System.Windows.Forms.Application.DoEvents();
                System.Threading.Thread.Sleep(5);
            }
            CheckStr("THE FIX: mode is corrected to the restored tier 'FT4', not left on the engine's stale FT8 default",
                wc.CurrentMode, "FT4");
            Check("THE FIX: trPeriod is reset (null) so it gets re-derived from the NOW-correct mode, instead of staying stuck at FT8's 15000ms for the rest of the session",
                wc.trPeriod == null, true);
            // THE FIX (the operator-facing half): the restore announces itself right away
            // instead of waiting for some later, unrelated render -- proven here BEFORE snap2
            // below drives any further render, so this can only be the tier-restore callback's
            // own new ShowStatus() call landing.
            Check("THE FIX: the tier restore renders status immediately (not left waiting for the next unrelated render)",
                fakeStatusView.RenderStatusCount >= 1, true);
            Check("THE FIX: the immediately-rendered status already reflects the corrected 'FT4' mode, not the stale FT8 text the operator would otherwise have acted on",
                fakeStatusView.LastStatusText != null && fakeStatusView.LastStatusText.IndexOf("FT4", StringComparison.Ordinal) >= 0, true);

            // Drive one more real status render (the same path UpdateTrPeriod/ShowStatus both run
            // through) to prove trPeriod actually RE-DERIVES to FT4's real period, not just that
            // it was reset to null and then never revisited.
            var snap2 = ParseDirectSnapshot(@"{
                ""mycall"": ""KB0UZT"", ""mygrid"": ""FN42"",
                ""radio"": { ""dialMhz"": 10.140, ""transmitting"": false, ""tuning"": false, ""slot"": 3001 },
                ""recentDecodes"": []
            }");
            wc.TestApplyDirectSnapshot("KB0UZT", "FN42", snap2);
            Check("THE FIX: trPeriod re-derives to FT4's real 7500ms period, not FT8's 15000ms",
                wc.trPeriod == 7500, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  DirectInitialConnectResyncsTierAndPeriodTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            engineListener.Stop();
        }
    }

    // ── WsjtxClient.BuildWorkingFrequencyEntries: Nexus working-frequency override hand-off,
    // 2026-08-24 -- THE FIX (frequency-override authority split) ───────────────────────────────
    // Only the PRIMARY (first-sorted, lowest-frequency) entry per (band,mode) is sent -- not
    // every direct-jump hotkey extra -- and a band the operator has never customized is left out
    // entirely rather than backfilled from Jimmy's own defaults, matching Engine::band_plan's
    // own "Empty overrides = stock" semantics. See WsjtxClient.Direct.cs's own comment.
    static void BuildWorkingFrequencyEntriesTests()
    {
        Console.WriteLine("\n── WsjtxClient.BuildWorkingFrequencyEntries: Nexus working-frequency override hand-off -- THE FIX ──");
        try
        {
            var freq = new FrequencySettings();
            // 30m (index 4): FT8 customized with an extra hotkey-only row on top of the primary
            // -- sorted ascending by FreqKHz per FrequencySettings.cs's own contract, so 10.136
            // (added first, and lower) is the primary/canonical one, not 10.138.
            freq.Bands[4].Add(new FrequencyEntry { Mode = "FT8", FreqKHz = 10136, Hotkey = System.Windows.Forms.Keys.None });
            freq.Bands[4].Add(new FrequencyEntry { Mode = "FT8", FreqKHz = 10138, Hotkey = System.Windows.Forms.Keys.F1 });
            freq.Bands[4].Add(new FrequencyEntry { Mode = "FT4", FreqKHz = 10141, Hotkey = System.Windows.Forms.Keys.None });
            // 20m (index 5): never customized at all.

            var entries = WsjtxClient.BuildWorkingFrequencyEntries(freq);

            Check("Only the two customized (band,mode) rows are present -- the untouched 20m band contributes nothing",
                entries.Count == 2, true);
            var ft8 = entries.Find(e => e.Band == "30m" && e.Mode == "FT8");
            var ft4 = entries.Find(e => e.Band == "30m" && e.Mode == "FT4");
            Check("30m FT8 entry present", ft8 != null, true);
            if (ft8 != null)
                Check("...using the PRIMARY (lowest, first-sorted) entry, 10.136 MHz, not the 10.138 hotkey extra",
                    Math.Abs(ft8.Mhz - 10.136) < 1e-9, true);
            Check("30m FT4 entry present", ft4 != null, true);
            if (ft4 != null)
                Check("...at the operator's customized 10.141 MHz",
                    Math.Abs(ft4.Mhz - 10.141) < 1e-9, true);
            Check("No entry at all for 20m (never customized) -- Nexus's own stock table is left in charge there",
                entries.Find(e => e.Band == "20m") == null, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  BuildWorkingFrequencyEntriesTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── DirectSetWorkingFrequencies: sends the real SET_WORKING_FREQUENCIES wire command,
    // 2026-08-24 -- THE FIX ─────────────────────────────────────────────────────────────────────
    static void DirectSetWorkingFrequenciesSendsCorrectCommandTests()
    {
        Console.WriteLine("\n── DirectSetWorkingFrequencies: sends the real SET_WORKING_FREQUENCIES wire command -- THE FIX ──");
        string capturedLine = null;
        var engineListener = StartStubEngineHostWithResponses(line =>
        {
            if (line.StartsWith("SET_WORKING_FREQUENCIES ")) capturedLine = line;
            return "OK";
        });
        if (engineListener == null)
        {
            Skip("DirectSetWorkingFrequenciesSendsCorrectCommandTests", "engine control port already in use on this machine");
            return;
        }
        try
        {
            var ctrl = new Controller();
            var _ = ctrl.Handle; // force handle creation -- EnqueueDirectCommand's completion runs via ctrl.BeginInvoke
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.ConnectDirectEngine("KB0UZT", "FN42");
            wc.TestStopPollTimer(); // the 1s SNAPSHOT poll would otherwise race this test's own connection

            var entries = new List<WorkingFreqArg>
            {
                new WorkingFreqArg { Band = "30m", Mode = "FT4", Mhz = 10.141 },
            };
            bool? result = null;
            var done = new System.Threading.ManualResetEventSlim(false);
            wc.DirectSetWorkingFrequencies(entries, ok => { result = ok; done.Set(); });
            // onComplete is marshaled via ctrl.BeginInvoke (EnqueueDirectCommand's default) --
            // needs the message loop actually pumped, same as DirectInitialConnectResyncsTier
            // AndPeriodTests' own wait loop, not a bare cross-thread Wait().
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!done.IsSet && sw.ElapsedMilliseconds < 8000)
            {
                System.Windows.Forms.Application.DoEvents();
                System.Threading.Thread.Sleep(5);
            }
            bool completed = done.IsSet;

            Check("The command completed within a bounded wait", completed, true);
            Check("...and the engine's OK response was reported back as success", result == true, true);
            Check("The stub engine host actually received a SET_WORKING_FREQUENCIES command",
                capturedLine != null, true);
            if (capturedLine != null)
            {
                Check("...carrying the band", capturedLine.Contains("\"band\":\"30m\""), true);
                Check("...carrying the mode", capturedLine.Contains("\"mode\":\"FT4\""), true);
                Check("...carrying the frequency", capturedLine.Contains("\"mhz\":10.141"), true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  DirectSetWorkingFrequenciesSendsCorrectCommandTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            engineListener.Stop();
        }
    }

    // ── NativeEngineClient.EscapeCommandLineArg: real Windows argv round trip, 2026-08-24 --
    // THE FIX (--working-frequencies is the first EngineHost launch arg whose value is JSON,
    // full of literal '"' characters that the naive `\"..\"` quoting every other arg here uses
    // would truncate at the first one) ──────────────────────────────────────────────────────────
    static void EscapeCommandLineArgRoundTripsThroughRealWindowsArgvTests()
    {
        Console.WriteLine("\n── NativeEngineClient.EscapeCommandLineArg: real Windows argv round trip -- THE FIX ──");
        try
        {
            string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            // GetExecutingAssembly().Location is the .dll under net10.0-windows; the real
            // entry point test.bat/run_parser_tests.bat actually invoke is the generated .exe
            // apphost sitting right next to it.
            if (exePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                exePath = exePath.Substring(0, exePath.Length - 4) + ".exe";

            string[] payloads =
            {
                "[{\"band\":\"30m\",\"mode\":\"FT4\",\"mhz\":10.14}]", // the real shape this exists for
                "trailing backslash\\",
                "quote at the very end\"",
                "back\\\"slash-then-quote",
                "",
            };
            foreach (string payload in payloads)
            {
                string escaped = NativeEngineClient.EscapeCommandLineArg(payload);
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--echo-argv " + escaped,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    string got = output.TrimEnd('\r', '\n');
                    CheckStr($"Round-trips through a real Windows process for payload '{payload}'", got, payload);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  EscapeCommandLineArgRoundTripsThroughRealWindowsArgvTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── Controller.PowerShellSingleQuoteLiteral: real PowerShell round trip, 2026-08-24 --
    // THE FIX (RestartApplication's relaunch used a fixed "timeout /t 2" delay that raced
    // Program.cs's single-instance Mutex/process check when real shutdown took longer than 2s,
    // leaving Jimmy Next not running at all -- replaced with a detached PowerShell helper that
    // Wait-Process'es on the exiting PID before Start-Process'ing the new instance; this proves
    // the exePath literal embedded in that script survives real PowerShell parsing even when
    // the path contains a space or an embedded single quote) ──────────────────────────────────
    static void PowerShellSingleQuoteLiteralRoundTripsThroughRealPowerShellTests()
    {
        Console.WriteLine("\n── Controller.PowerShellSingleQuoteLiteral: real PowerShell round trip -- THE FIX ──");
        try
        {
            string[] payloads =
            {
                @"C:\Program Files\Jimmy Next\Jimmy Next.exe", // the real shape: AssemblyName has a space
                "O'Brien's Path\\Jimmy.exe", // embedded single quotes
                "plain.exe",
                "",
            };
            foreach (string payload in payloads)
            {
                string literal = Controller.PowerShellSingleQuoteLiteral(payload);
                string script = $"Write-Output {literal}\n";
                string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10000);
                    string got = output.TrimEnd('\r', '\n');
                    CheckStr($"Round-trips through real PowerShell for payload '{payload}'", got, payload);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  PowerShellSingleQuoteLiteralRoundTripsThroughRealPowerShellTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── RetuneBand: a failed SetFrequency is not reported as success, 2026-08-19 ────────────
    // Codex release audit: RetuneBand used to set _pendingBandIdx, call RigctldClient.
    // SetFrequency, discard its bool result, and return true unconditionally -- so a rejected
    // command, a dropped connection, or a timeout (SetFrequency returns false for all three)
    // was reported as a successful band change, and _pendingBandIdx was left pointing at a band
    // the radio was never actually retuned to. Since BandUp/BandDown prefer _pendingBandIdx over
    // the last CONFIRMED bandIdx, a stale pending value from a failed retune would make the NEXT
    // BandUp/BandDown compute from a band that only ever existed as an unconfirmed request --
    // the failure-path twin of the confirmation-path bug DirectPathPendingBandIdxCleared...
    // above already covers. RetuneBand now calls DirectSetFrequency (SET_FREQUENCY on the
    // engine's own control port) instead of RigctldClient.SetFrequency, so this exercises
    // DirectSendCommand's real failure mode (connection refused -- nothing listens on the
    // engine control port at that point) and real success mode (StartStubEngineHost) rather
    // than mocking anything, matching this suite's existing convention.
    static void RetuneBandFailureDoesNotLeakPendingBandIdxTests()
    {
        Console.WriteLine("\n── RetuneBand: a failed SetFrequency does not claim success or leak _pendingBandIdx ──");
        try
        {
            var ctrl = new Controller();
            var _ = ctrl.Handle; // force handle creation -- RetuneBand's ctrl.BeginInvoke below needs it
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.Radio.Mode = RadioControlMode.HamlibRigctld;
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);

            var snap20m = ParseDirectSnapshot(@"{
                ""mycall"": ""KB0UZT"", ""mygrid"": ""FN42"",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""tuning"": false, ""slot"": 3000 },
                ""recentDecodes"": []
            }");
            wc.TestApplyDirectSnapshot("KB0UZT", "FN42", snap20m);
            Check("Setup: confirmed on 20m (index 5)", wc.TestBandIdx == 5, true);

            // FAILURE case: nothing listens on the engine control port yet -- DirectSetFrequency
            // genuinely fails to connect (real connection-refused, not a mock). RetuneBand's gate
            // is just RadioControlMode.HamlibRigctld (already set above); no client instance is
            // needed to satisfy it, since ctrl.rigctldClient itself is retired.
            bool downFailed = wc.BandDown();
            Check("BandDown() still returns true (hotkey stays 'handled') even though the retune failed",
                downFailed, true);
            // Release-audit finding, 2026-08-21: RetuneBand's actual DirectSetFrequency call (and
            // therefore its failure-path _pendingBandIdx clear) now runs on a background Task --
            // wait for it to actually land rather than checking immediately after BandDown()
            // returns (which only proves the SYNCHRONOUS optimistic-set half, not the fix itself).
            PumpUntil(() => wc.TestPendingBandIdx == null);
            Check("...but _pendingBandIdx was NOT left pointing at the unconfirmed target -- THE FIX",
                wc.TestPendingBandIdx == null, true);

            // SUCCESS case, right after a failure: a real accept + wire round-trip against a stub
            // engine host must still work normally -- "successful behavior remains unchanged".
            var engineListener = StartStubEngineHost();
            if (engineListener == null)
            {
                Skip("RetuneBandFailureDoesNotLeakPendingBandIdxTests success case", "engine control port already in use on this machine");
                return;
            }
            try
            {
                bool downOk = wc.BandDown();
                Check("BandDown() succeeds against a real (stub) engine host", downOk, true);
                Check("...and _pendingBandIdx correctly targets 30m (index 4) -- successful behavior unchanged",
                    wc.TestPendingBandIdx == 4, true);
            }
            finally
            {
                engineListener.Stop();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  RetuneBandFailureDoesNotLeakPendingBandIdxTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── SelectFrequencyHotkey: a band hotkey never switches mode, 2026-08-18 ────────────────
    // Root-caused live: Options > Frequencies auto-creates one FT8 and one FT4 row per band,
    // sorted ascending by frequency. 40m is the ONLY band where FT4's built-in calling
    // frequency (7047) is lower than FT8's (7074), so it's the one band where the FT4 row
    // lists first -- every other band correctly lists FT8 first. An operator assigning "one
    // hotkey per band" down the list landed their 40m hotkey on the FT4 row by exactly this
    // quirk, and pressing it while on FT8 silently switched tier to FT4 (SetOperatingMode ->
    // DirectSetTier -> the engine's own tier-switch retune) AND separately sent Jimmy's own
    // explicit frequency command for the same target -- two genuine CAT frequency writes from
    // two different connections, for one keypress (confirmed via a live Hamlib -vvvv trace).
    // SelectFrequencyHotkey now redirects a MISMATCHED-mode hotkey through SelectBand (already
    // correct, previously unreferenced by any hotkey) instead of ever calling
    // SetOperatingMode -- so the SAME hotkey works correctly in either mode, and never
    // silently changes which mode the operator is in.
    static void SelectFrequencyHotkeyModeStaysPutTests()
    {
        Console.WriteLine("\n── SelectFrequencyHotkey: a band hotkey never switches mode ──");
        // Real (stub) engine-host setup, not left at the default WsjtxCat mode -- since the
        // RetuneBand failure-handling fix (Codex release audit, 2026-08-19), _pendingBandIdx is
        // only ever set once RetuneBand's DirectSetFrequency call is actually attempted, so this
        // test needs a real accept + wire round-trip for its TestPendingBandIdx assertions below
        // to mean anything. RetuneBand's gate is just RadioControlMode.HamlibRigctld (set below);
        // ctrl.rigctldClient itself is retired, so no client instance is needed to satisfy it.
        var engineListener = StartStubEngineHost();
        if (engineListener == null)
        {
            Skip("SelectFrequencyHotkeyModeStaysPutTests", "engine control port already in use on this machine");
            return;
        }
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.Radio.Mode = RadioControlMode.HamlibRigctld;
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.TestSetMode("FT8");

            // Start confirmed on 20m (index 5) so the 40m jump below is a real cross-band move,
            // not masked by SelectBand's own "already on this band" no-op guard.
            var snap20m = ParseDirectSnapshot(@"{
                ""mycall"": ""KB0UZT"", ""mygrid"": ""FN42"",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""tuning"": false, ""slot"": 3000 },
                ""recentDecodes"": []
            }");
            wc.TestApplyDirectSnapshot("KB0UZT", "FN42", snap20m);
            wc.TestSetMode("FT8"); // TestApplyDirectSnapshot doesn't touch mode; belt and suspenders
            Check("Setup: confirmed on 20m (index 5), FT8", wc.TestBandIdx == 5, true);

            // THE BUG SCENARIO: on FT8, but the 40m hotkey is bound to the auto-created FT4 row
            // (exactly the 40m-only sort-order trap).
            var mismatchedEntry = new FrequencyEntry { Mode = "FT4", FreqKHz = 7047, Hotkey = System.Windows.Forms.Keys.Alt | System.Windows.Forms.Keys.D7 };
            bool ok = wc.SelectFrequencyHotkey(3, mismatchedEntry);
            Check("SelectFrequencyHotkey succeeds (a real cross-band move happened)", ok, true);
            Check("Mode-mismatched hotkey -> band 40m (index 3) is targeted",
                wc.TestPendingBandIdx == 3, true);
            Check("...but mode stays FT8 -- THE FIX (used to silently switch to FT4)",
                wc.CurrentMode == "FT8", true);

            // A hotkey whose OWN entry already matches the current mode still behaves exactly
            // like the original targeted jump -- multiple same-mode entries per band (e.g. an
            // alternate spot frequency) remain reachable by their own hotkey. The stub engine
            // host accepts one connection per command (DirectSendCommand dials fresh each time),
            // so this second real retune succeeds too.
            var matchedEntry = new FrequencyEntry { Mode = "FT8", FreqKHz = 14074, Hotkey = System.Windows.Forms.Keys.Alt | System.Windows.Forms.Keys.D5 };
            bool ok2 = wc.SelectFrequencyHotkey(5, matchedEntry);
            Check("Mode-matched hotkey succeeds", ok2, true);
            Check("...targets its own band (20m, index 5)", wc.TestPendingBandIdx == 5, true);
            Check("...and mode is still FT8 (never needed switching)", wc.CurrentMode == "FT8", true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  SelectFrequencyHotkeyModeStaysPutTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            engineListener.Stop();
        }
    }

    // ── T18 fix, 2026-08-23: FrequencyEntry.Sideband round-trips through the INI, legacy
    // 4-part rows migrate silently to "USB", and RetuneBand sends the entry's REAL configured
    // sideband over SET_FREQUENCY instead of a hardcoded "USB" literal ──
    static void FrequencyEntrySidebandTests()
    {
        Console.WriteLine("\n── T18 fix: FrequencyEntry.Sideband persistence + wire wiring -- THE FIX ──");
        try
        {
            // -- INI round-trip, including legacy migration --
            string tmpIniPath = Path.Combine(Path.GetTempPath(), "JimmyTest_FreqSideband_" + Guid.NewGuid().ToString("N") + ".ini");
            try
            {
                var ini = new IniFile(tmpIniPath);
                // A hand-written legacy 4-part row (pre-T18 format, no Sideband field) alongside
                // a real LSB entry in modern 5-part format, on two different bands.
                ini.Write("freqEntries", "3:FT8:7074:0;6:FT8:18100:0:LSB");
                var settings = new FrequencySettings();
                settings.LoadFromIni(ini);
                Check("Legacy 4-part row (band 3, no Sideband field) migrates to the default USB",
                    settings.Bands[3].Count == 1 && settings.Bands[3][0].Sideband == "USB", true);
                Check("Modern 5-part row (band 6) loads its real LSB value",
                    settings.Bands[6].Count == 1 && settings.Bands[6][0].Sideband == "LSB", true);

                settings.SaveToIni(ini);
                var reloaded = new FrequencySettings();
                reloaded.LoadFromIni(ini);
                Check("Round-trip: USB entry still USB after save/reload", reloaded.Bands[3][0].Sideband == "USB", true);
                Check("Round-trip: LSB entry still LSB after save/reload", reloaded.Bands[6][0].Sideband == "LSB", true);
            }
            finally
            {
                try { File.Delete(tmpIniPath); } catch { }
            }

            // -- Clone preserves Sideband --
            var original = new FrequencyEntry { Mode = "FT4", FreqKHz = 7047, Sideband = "LSB" };
            Check("FrequencyEntry.Clone() preserves Sideband", original.Clone().Sideband == "LSB", true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  FrequencyEntrySidebandTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── T18 fix, 2026-08-23: RetuneBand sends the SELECTED entry's real configured sideband
    // over the wire, not a hardcoded "USB" literal -- proves the actual SET_FREQUENCY JSON
    // payload, not just that a request was sent (matches this file's own "real validation for
    // the SET_FREQUENCY Direct contract" precedent, StartStubEngineHost's own onCommandReceived
    // comment) ──
    static void SelectFrequencyHotkeySendsConfiguredSidebandTests()
    {
        Console.WriteLine("\n── T18 fix: SelectFrequencyHotkey sends the entry's configured sideband -- THE FIX ──");
        var lastCommand = new string[1];
        var engineListener = StartStubEngineHost(line => lastCommand[0] = line);
        if (engineListener == null)
        {
            Skip("SelectFrequencyHotkeySendsConfiguredSidebandTests", "engine control port already in use on this machine");
            return;
        }
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.Radio.Mode = RadioControlMode.HamlibRigctld;
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.TestSetMode("FT8");

            var lsbEntry = new FrequencyEntry { Mode = "FT8", FreqKHz = 18100, Sideband = "LSB" };
            bool ok = wc.SelectFrequencyHotkey(6, lsbEntry);
            Check("SelectFrequencyHotkey succeeds", ok, true);

            PumpUntil(() => lastCommand[0] != null, timeoutMs: 5000);
            const string prefix = "SET_FREQUENCY ";
            string cmd = lastCommand[0];
            Check("A real SET_FREQUENCY command was sent", cmd != null && cmd.StartsWith(prefix), true);
            if (cmd != null && cmd.StartsWith(prefix))
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(cmd.Substring(prefix.Length)))
                {
                    Check("THE FIX: the wire payload's mode is 'LSB' (the entry's own configured value), not a hardcoded 'USB'",
                        doc.RootElement.GetProperty("mode").GetString() == "LSB", true);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  SelectFrequencyHotkeySendsConfiguredSidebandTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            engineListener.Stop();
        }
    }

    // ── T17 fix, 2026-08-23: CAT-mode readback reconciliation -- pure classification tests for
    // RigModeMismatchesCommandedSideband, the exact logic DirectApplyStatus now uses to warn on
    // a real rig/CAT sideband mismatch instead of never checking at all ──
    static void RigModeMismatchClassificationTests()
    {
        Console.WriteLine("\n── T17 fix: CAT-mode readback mismatch classification -- THE FIX ──");
        try
        {
            Check("Commanded USB, rig reports USB -- no mismatch",
                WsjtxClient.RigModeMismatchesCommandedSideband("USB", "USB"), false);
            Check("Commanded USB, rig reports rig-specific 'PKTUSB' -- tolerated, no mismatch",
                WsjtxClient.RigModeMismatchesCommandedSideband("USB", "PKTUSB"), false);
            Check("THE FIX: commanded USB, rig reports LSB -- real mismatch",
                WsjtxClient.RigModeMismatchesCommandedSideband("USB", "LSB"), true);
            Check("THE FIX: commanded USB, rig reports 'PKTLSB' -- real mismatch",
                WsjtxClient.RigModeMismatchesCommandedSideband("USB", "PKTLSB"), true);
            Check("Commanded LSB, rig reports LSB -- no mismatch",
                WsjtxClient.RigModeMismatchesCommandedSideband("LSB", "LSB"), false);
            Check("THE FIX: commanded LSB, rig reports USB -- real mismatch",
                WsjtxClient.RigModeMismatchesCommandedSideband("LSB", "USB"), true);
            Check("THE FIX: rig reports neither USB nor LSB (e.g. FM/CW) -- treated as a mismatch",
                WsjtxClient.RigModeMismatchesCommandedSideband("USB", "FM"), true);
            Check("Nothing commanded yet -- never a mismatch (nothing to compare)",
                WsjtxClient.RigModeMismatchesCommandedSideband(null, "LSB"), false);
            Check("No readback yet (VOX-only/no-CAT, or before the first report) -- never a mismatch",
                WsjtxClient.RigModeMismatchesCommandedSideband("USB", null), false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  RigModeMismatchClassificationTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── CAT mode command/readback correlation (independent audit finding, 2026-08-23): the
    // ACTUAL DirectApplyStatus pipeline, not just the pure RigModeMismatchesCommandedSideband
    // classification helper above ──
    // RigModeMismatchClassificationTests above only proves the pure comparison function -- it
    // never exercises the transition-latency grace window or the reconcile-after-sustained-
    // mismatch behavior DirectApplyStatus itself now adds around that function. Drives real
    // snapshots through TestApplyDirectSnapshot (the same production DirectApplyStatus every
    // other Direct-mode test in this file uses) and observes real NotificationCenter delivery,
    // proving: (1) a mismatch inside the transition grace window is silent, (2) the SAME
    // mismatch past the grace window is announced exactly once, (3) sustained agreement across
    // enough consecutive polls reconciles _lastCommandedSideband to the rig's own reported mode
    // and a distinct "reconciled" notice fires, (4) an ambiguous readback (neither USB nor LSB)
    // is never auto-reconciled, no matter how long it persists.
    static void RigModeMismatchGraceWindowAndReconciliationTests()
    {
        Console.WriteLine("\n── CAT mode command/readback correlation: transition grace window + bounded reconciliation -- THE FIX ──");
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);

            var settings = new NotificationSettings();
            settings.Policies[NotificationEventType.ErrorWarning].RepeatSeconds = 0;
            var delivery = new FakeNotificationDelivery();
            wc.Notify = new NotificationCenter(settings, delivery);

            const string myCall = "KB0UZT";
            const string myGrid = "FN42";

            // A confirmed retune to USB, timestamped "now" -- same shape RetuneBand's own
            // completion callback produces on a real confirmed SET_FREQUENCY (WsjtxClient.
            // BandAudio.cs). _lastCommandedSideband is already internal; only the timestamp
            // needs a test hook (see its own comment).
            wc._lastCommandedSideband = "USB";
            wc.TestSetLastCommandedSidebandChangedUtc(DateTime.UtcNow);

            // (1) A snapshot landing well inside the transition grace window, even with a real
            // mismatched readback (rig still reports LSB -- hasn't caught up yet), must be silent.
            wc.TestApplyDirectSnapshot(myCall, myGrid, ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""", ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""slot"": 1, ""rigMode"": ""LSB"" }
            }"));
            Check("THE FIX: a mismatch inside the transition grace window is NOT announced (normal rig latency, not a real problem yet)",
                delivery.AnnounceCount == 0, true);
            Check("...and does not count toward the reconcile streak either",
                wc.TestSidebandMismatchStreak == 0, true);

            // Backdate the commanded timestamp past the grace window (no real sleep needed) and
            // re-apply the SAME still-mismatched snapshot.
            wc.TestSetLastCommandedSidebandChangedUtc(DateTime.UtcNow - TimeSpan.FromSeconds(10));
            wc.TestApplyDirectSnapshot(myCall, myGrid, ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""", ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""slot"": 2, ""rigMode"": ""LSB"" }
            }"));
            Check("(2) THE FIX: the SAME mismatch, once past the grace window, IS announced",
                delivery.AnnounceCount == 1, true);
            Check("...as the mismatch notice specifically",
                delivery.LastText != null && delivery.LastText.Contains("mismatch"), true);

            // Repeated polls (still LSB, still mismatched) -- edge-triggered: must not re-announce
            // the same mismatch on every tick while it persists.
            for (int i = 0; i < SidebandReconcileAfterConsecutiveMismatchesForTest - 2; i++)
            {
                wc.TestApplyDirectSnapshot(myCall, myGrid, ParseDirectSnapshot(@"{
                    ""mycall"": """ + myCall + @""", ""mygrid"": """ + myGrid + @""",
                    ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""slot"": " + (3 + i) + @", ""rigMode"": ""LSB"" }
                }"));
            }
            Check("Repeated mismatched polls do not re-announce while still the same open episode",
                delivery.AnnounceCount == 1, true);

            // (3) THE FIX: one more consistently-mismatched poll reaches the reconcile threshold
            // -- Jimmy adopts the rig's own reported LSB as the new commanded baseline and stops
            // treating this as an open mismatch.
            wc.TestApplyDirectSnapshot(myCall, myGrid, ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""", ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""slot"": 99, ""rigMode"": ""LSB"" }
            }"));
            Check("THE FIX: sustained mismatch reconciles _lastCommandedSideband to the rig's own reported mode",
                wc._lastCommandedSideband == "LSB", true);
            Check("THE FIX: a distinct reconciliation notice fired",
                delivery.AnnounceCount == 2 && delivery.LastText != null && delivery.LastText.Contains("reconciled"), true);
            Check("...and the streak resets once reconciled",
                wc.TestSidebandMismatchStreak == 0, true);

            // Now that _lastCommandedSideband == LSB and the readback still reports LSB, a
            // further identical poll must be a clean agreement -- no further announcement.
            wc.TestApplyDirectSnapshot(myCall, myGrid, ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""", ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""slot"": 100, ""rigMode"": ""LSB"" }
            }"));
            Check("After reconciling, the readback now agrees -- no further announcement",
                delivery.AnnounceCount == 2, true);

            // (4) An ambiguous readback (neither USB nor LSB) must NEVER be auto-reconciled, no
            // matter how long it persists -- "do not guess about rig-specific USB/Data/PKTUSB
            // behavior."
            wc._lastCommandedSideband = "USB";
            wc.TestSetLastCommandedSidebandChangedUtc(DateTime.UtcNow - TimeSpan.FromSeconds(10));
            for (int i = 0; i < SidebandReconcileAfterConsecutiveMismatchesForTest + 3; i++)
            {
                wc.TestApplyDirectSnapshot(myCall, myGrid, ParseDirectSnapshot(@"{
                    ""mycall"": """ + myCall + @""", ""mygrid"": """ + myGrid + @""",
                    ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""slot"": " + (200 + i) + @", ""rigMode"": ""FM"" }
                }"));
            }
            Check("THE FIX: an ambiguous (neither USB nor LSB) readback is never auto-reconciled, however long it persists",
                wc._lastCommandedSideband == "USB", true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  RigModeMismatchGraceWindowAndReconciliationTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // Mirrors WsjtxClient.Direct.cs's own SidebandReconcileAfterConsecutiveMismatches -- kept as
    // a separate test-side constant (not a reflection read of the production one) so this test
    // fails loudly if the two ever drift apart instead of silently adapting to whatever the
    // production value happens to be.
    private const int SidebandReconcileAfterConsecutiveMismatchesForTest = 5;

    // ── Persisted settings validation (independent audit finding, 2026-08-23): timeoutNumUpDown
    // must never crash startup on a malformed/corrupted/out-of-range persisted "timeout" value ──
    // timeoutNumUpDown (Controller.Designer.cs) has no explicit Minimum/Maximum, so it uses
    // WinForms' own NumericUpDown defaults (0..100); the SEMANTIC valid range Controller.cs's own
    // timeoutNumUpDown_ValueChanged already enforces live is narrower (minSkipCount..
    // maxSkipCount, 1..20). Before this fix, Form_Load assigned an ini-parsed/legacy-Properties
    // "timeout" value straight to timeoutNumUpDown.Value with no clamping at all -- anything
    // outside [0,100] threw ArgumentOutOfRangeException, and anything in (20,100] silently landed
    // outside the semantic range with no live-clamp handler having run yet (formLoaded is only
    // set true near the END of Form_Load). Exercises a real IniFile fixture (malformed/legacy/
    // current/idempotent values) through the SAME clamp formula Controller.cs's own two fixed
    // call sites now use, assigned to a REAL NumericUpDown built with timeoutNumUpDown's actual
    // declared shape (no explicit Minimum/Maximum -- see its own Designer.cs entry) -- not a
    // Form_Load integration test (Form_Load's own side effects -- window positioning, real OS
    // foreground activation, a background update-check Task -- are far outside this fix's actual
    // scope and would make the test fragile without proving anything more about this specific
    // defect).
    static void TimeoutSettingClampedOnLoadTests()
    {
        Console.WriteLine("\n── Persisted settings validation: timeoutNumUpDown never crashes startup on a bad saved value -- THE FIX ──");
        string tmpIniPath = Path.Combine(Path.GetTempPath(), "JimmyTest_TimeoutClamp_" + Guid.NewGuid().ToString("N") + ".ini");
        try
        {
            const int minSkipCount = 1;   // mirrors Controller.cs's own private field of the same name/value
            const int maxSkipCount = 20;  // mirrors Controller.cs's own maxSkipCount constant

            void CheckOneValue(string label, string rawIniValue, int expectedClamped)
            {
                var ini = new IniFile(tmpIniPath);
                ini.Write("timeout", rawIniValue);
                int.TryParse(ini.Read("timeout"), out int parsed);
                int clamped = Math.Max(minSkipCount, Math.Min(maxSkipCount, parsed));
                // Same shape as the Designer -- no explicit Minimum/Maximum, so this is the
                // real control-level range (0..100) the unclamped assignment used to be able to
                // violate for a negative or >100 saved value.
                var numUpDown = new System.Windows.Forms.NumericUpDown();
                bool threw = false;
                try { numUpDown.Value = clamped; }
                catch (ArgumentOutOfRangeException) { threw = true; }
                Check($"{label}: does not throw assigning timeoutNumUpDown.Value", threw, false);
                Check($"{label}: clamps to the expected semantic value ({expectedClamped})",
                    (int)numUpDown.Value == expectedClamped, true);
            }

            // Malformed (non-numeric) -- int.TryParse fails, parsed stays its default 0, clamps
            // up to the minimum.
            CheckOneValue("Malformed (\"abc\")", "abc", minSkipCount);
            // Out of range LOW (a value the raw control-level Minimum of 0 would have accepted
            // without throwing, but which is semantically invalid -- must still clamp, not just
            // avoid crashing).
            CheckOneValue("Out of range low (0)", "0", minSkipCount);
            // Out of range NEGATIVE -- below even the control's own raw Minimum (0); this is the
            // exact case that used to throw ArgumentOutOfRangeException and crash startup.
            CheckOneValue("Out of range negative (-5)", "-5", minSkipCount);
            // Out of range HIGH but still within the control's own raw default Maximum (100) --
            // would NOT have thrown before this fix, but would have silently landed outside the
            // real semantic range with nothing left to correct it this early in startup.
            CheckOneValue("Out of range high, within raw control range (55)", "55", maxSkipCount);
            // Out of range HIGH and ALSO past the control's own raw default Maximum (100) -- the
            // other exact case that used to throw ArgumentOutOfRangeException and crash startup.
            CheckOneValue("Out of range high, past raw control range (999)", "999", maxSkipCount);
            // Legacy/current valid values, including both boundary edges -- must pass through
            // completely unchanged.
            CheckOneValue("Legacy/current valid boundary (1)", "1", 1);
            CheckOneValue("Legacy/current valid mid-range (10)", "10", 10);
            CheckOneValue("Legacy/current valid boundary (20)", "20", 20);

            // Idempotence: writing the ALREADY-clamped value back out and reloading it must
            // produce the exact same result again, not drift.
            var ini2 = new IniFile(tmpIniPath);
            ini2.Write("timeout", "999");
            int.TryParse(ini2.Read("timeout"), out int firstParsed);
            int firstClamped = Math.Max(minSkipCount, Math.Min(maxSkipCount, firstParsed));
            ini2.Write("timeout", firstClamped.ToString());
            int.TryParse(ini2.Read("timeout"), out int secondParsed);
            int secondClamped = Math.Max(minSkipCount, Math.Min(maxSkipCount, secondParsed));
            Check("Idempotent: reloading an already-clamped value produces the identical result",
                secondClamped == firstClamped, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  TimeoutSettingClampedOnLoadTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            try { File.Delete(tmpIniPath); } catch { }
        }
    }

    // ── DebugOutput: log-write-failure circuit breaker, 2026-08-17 ─────────────────────────
    // Found while investigating an unused-variable warning in Release: DebugOutput's own catch
    // block silently swallowed a logSw.WriteLine failure in Release builds (only ever visible
    // via a DEBUG-only Console.WriteLine) -- diagLog stayed true and logSw stayed open, so
    // EVERY subsequent call (there are hundreds of DebugOutput call sites throughout the app)
    // kept retrying the same broken stream and re-swallowing the same exception, forever, with
    // zero operator visibility in a real Release build. This is the exact same class of bug
    // SetLogFileState's own "couldn't open the log file" catch was already fixed for (2026-08-08,
    // a real "log is blank" tester report) -- this is its "couldn't WRITE to an already-open
    // log" counterpart, which had been missed. Proves the fix's circuit breaker actually trips
    // and stays tripped, using a genuinely-broken StreamWriter (a disposed one), not just that
    // the code compiles.
    // ── Independent audit finding 10, 2026-08-23: diagnostic log retention -- old log_*.txt
    // files past the retention window are removed, recent ones and non-matching files are left
    // alone -- THE FIX ── Uses an isolated temp directory (wc.path overridden), never the real
    // operator log folder.
    static void DiagnosticLogRetentionTests()
    {
        Console.WriteLine("\n── Finding 10 fix: diagnostic log retention removes only old log_*.txt files -- THE FIX ──");
        string tmpLogDir = Path.Combine(Path.GetTempPath(), "JimmyTest_LogRetention_" + Guid.NewGuid().ToString("N"));
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.path = tmpLogDir;
            Directory.CreateDirectory(tmpLogDir);

            DateTime old = DateTime.Now.Date.AddDays(-45);
            DateTime recent = DateTime.Now.Date.AddDays(-2);
            string oldFile      = Path.Combine(tmpLogDir, $"log_{old.Month}-{old.Day}-{old.Year}.txt");
            string recentFile   = Path.Combine(tmpLogDir, $"log_{recent.Month}-{recent.Day}-{recent.Year}.txt");
            string unrelatedFile = Path.Combine(tmpLogDir, "not_a_log_file.txt");
            File.WriteAllText(oldFile, "old");
            File.WriteAllText(recentFile, "recent");
            File.WriteAllText(unrelatedFile, "unrelated");

            wc.TestCleanUpOldLogs();

            Check("THE FIX: a log file older than the retention window is removed",
                !File.Exists(oldFile), true);
            Check("A recent log file is left alone", File.Exists(recentFile), true);
            Check("A non-matching file in the same directory is never touched",
                File.Exists(unrelatedFile), true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  DiagnosticLogRetentionTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            try { Directory.Delete(tmpLogDir, true); } catch { }
        }
    }

    static void DebugOutputLogWriteFailureTests()
    {
        Console.WriteLine("\n── DebugOutput: log write failure stops retrying, doesn't throw ──");
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);

            // A StreamWriter over an already-disposed MemoryStream throws ObjectDisposedException
            // on the next WriteLine -- but only if it actually touches the underlying stream:
            // StreamWriter buffers internally by default, so a plain WriteLine with no flush
            // silently no-ops here without AutoFlush (caught live writing this test -- the first
            // draft asserted nothing because of exactly this). AutoFlush=true matches
            // SetLogFileState's own real logSw setup, so this is the same configuration
            // production actually uses, not a synthetic-only difference.
            var ms = new System.IO.MemoryStream();
            var brokenWriter = new StreamWriter(ms) { AutoFlush = true };
            ms.Dispose();
            wc.TestSetLogWriter(brokenWriter);

            wc.diagLog = true;
            wc.DebugOutput("first message -- write fails here");

            Check("Log write failure disables diagLog (the Options checkbox will show 'off' next open)",
                wc.diagLog, false);
            Check("...and nulls out the broken writer",
                wc.TestLogWriterIsNull, true);

            // Second call must be a harmless no-op -- proves the circuit breaker actually stops
            // future attempts instead of retrying the same broken stream every time.
            bool threw = false;
            try { wc.DebugOutput("second message -- must not retry or throw"); }
            catch { threw = true; }
            Check("A second DebugOutput call after the failure does not throw (no retry storm)",
                threw, false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  DebugOutputLogWriteFailureTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── OtaSpotsWindow.FormatStatus: concise POTA/SOTA row status text ─────────────
    // A live JAWS pass (2026-08-17) found every row saying "not worked before, not currently
    // needed" -- verbose, and the "needed" half was noise on almost every row. FormatStatus now
    // collapses to one clause: needed-for-N replaces (not appends to) the worked/not-worked
    // clause, and "not currently needed" never prints.
    static void OtaSpotsWindowFormatStatusTests()
    {
        Console.WriteLine("\n── OtaSpotsWindow.FormatStatus: concise, silences 'not currently needed' ──");
        try
        {
            Check("null annotation -> empty string, not a crash",
                OtaSpotsWindow.FormatStatus(null) == "", true);

            var notWorkedNotNeeded = new OtaSpotAnnotation { WorkedBefore = false, NeededForAwardCount = 0 };
            Check("Not worked, not needed -> just 'not worked' (no 'not currently needed' noise)",
                OtaSpotsWindow.FormatStatus(notWorkedNotNeeded) == "not worked", true);

            var workedNotNeeded = new OtaSpotAnnotation { WorkedBefore = true, NeededForAwardCount = 0 };
            Check("Worked before, not needed -> just 'worked'",
                OtaSpotsWindow.FormatStatus(workedNotNeeded) == "worked", true);

            var neededOne = new OtaSpotAnnotation { WorkedBefore = false, NeededForAwardCount = 1 };
            Check("Needed for 1 award -> singular, replaces the worked clause entirely",
                OtaSpotsWindow.FormatStatus(neededOne) == "needed for 1 award", true);

            var neededTwo = new OtaSpotAnnotation { WorkedBefore = false, NeededForAwardCount = 2 };
            Check("Needed for 2 awards -> plural",
                OtaSpotsWindow.FormatStatus(neededTwo) == "needed for 2 awards", true);

            // Needed always wins over worked-before, even if (unusually) both are true -- the
            // "worth attention" fact should stand out, not get buried behind "worked".
            var workedButStillNeeded = new OtaSpotAnnotation { WorkedBefore = true, NeededForAwardCount = 3 };
            Check("Worked before AND needed -> needed wins (the actionable fact)",
                OtaSpotsWindow.FormatStatus(workedButStillNeeded) == "needed for 3 awards", true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  OtaSpotsWindowFormatStatusTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── OtaSpotsWindow: ListView -> ListBox for NVDA, 2026-08-24 -- THE FIX ─────────────────
    // Live NVDA finding: a multi-column ListView (View.Details) reads fine in JAWS but NVDA only
    // ever announced the FIRST column moving row to row ("17m", "12m", "20m", ...) -- a real,
    // reported live-NVDA regression traced to WinForms ListView's own long-standing, still-open
    // UI Automation subitem gap (dotnet/winforms#3223). Replaced with single-column ListBoxes
    // whose only accessible text per row IS the full formatted line, so there's no subitem
    // channel for either screen reader to miss. These tests lock in each row's exact text (the
    // wording a live JAWS pass on this window already read out loud correctly) and the
    // first-population selection behavior that replaced the earlier (also live-NVDA-tested,
    // also insufficient on its own) ListView-focused-item attempt.
    static void OtaSpotsWindowRowFormattingTests()
    {
        Console.WriteLine("\n── OtaSpotsWindow: ListBox row formatting -- THE FIX ──");
        try
        {
            var spot = new OtaSpot
            {
                Program = "POTA", Reference = "US-12405", Activator = "KQ4TAX",
                FreqKhz = 3910.0, Mode = "SSB", SpotTimeUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 58,
            };
            var annotation = new OtaSpotAnnotation { WorkedBefore = false, NeededForAwardCount = 0 };
            CheckStr("POTA/SOTA row: exact wording a live JAWS pass already read correctly",
                OtaSpotsWindow.FormatPotaSotaRow(spot, annotation),
                "POTA, Reference: US-12405, Activator: KQ4TAX, Freq/Mode: 3.910 SSB, Age: 58s ago, Status: not worked");

            var band = new BandReport
            {
                Band = "20m", Tier = "Active", Confidence = "Strong", NHearMe = 5, NIHear = 8,
                Reason = "reciprocal spots both directions",
                Modeled = "false", ModeledReason = "n/a",
                BestRegion = new RegionReport { Region = "EU", Octant = "NE", Stations = 3 },
            };
            CheckStr("Band Conditions row: every column present as one labeled line",
                OtaSpotsWindow.FormatBandConditionsRow(band),
                "20m: Active, Strong confidence, Hear Me/I Hear: 5 / 8, Best Region: EU (NE, 3 stns), " +
                "Reason: reciprocal spots both directions (modeled: false -- n/a)");

            var bandNoRegion = new BandReport { Band = "10m", Tier = "Closed", Confidence = "Marginal", Reason = "no reports", Modeled = "true", ModeledReason = "physics only" };
            Check("Band Conditions row: no best region -> '--' placeholder, not a null-ref",
                OtaSpotsWindow.FormatBandConditionsRow(bandNoRegion).Contains("Best Region: --"), true);

            var dx = new DxSpot { DxCall = "K1ABC", FreqKhz = 14074.0, Spotter = "W2XYZ", Comment = "FT8 CQ", Rbn = true, SkimmerMode = "FT8", AgeSecs = 42 };
            CheckStr("DX spot row: every column present as one labeled line",
                OtaSpotsWindow.FormatDxSpotRow(dx),
                "DX Call: K1ABC, Frequency: 14.074 MHz, Mode: FT8, Spotter: W2XYZ, Age: 42s ago, Comment: FT8 CQ");

            var dxNotRbn = new DxSpot { DxCall = "K1ABC", FreqKhz = 14074.0, Spotter = "W2XYZ", Comment = "", Rbn = false, AgeSecs = 5 };
            Check("DX spot row: non-RBN (human cluster) spot -> blank mode, not 'RBN'",
                OtaSpotsWindow.FormatDxSpotRow(dxNotRbn).Contains("Mode: ,"), true);

            // SelectFirstItemIfNoneSelectedYet: root cause of the original bug report -- the
            // very first population after a tab/window opens must leave something selected for
            // a screen reader to land on, but a routine periodic refresh must NOT yank an
            // operator who already arrowed deeper into the list back to the top.
            using (var lb = new System.Windows.Forms.ListBox())
            {
                lb.Items.AddRange(new object[] { "row 1", "row 2", "row 3" });
                OtaSpotsWindow.SelectFirstItemIfNoneSelectedYet(lb, hadSelectionBeforeClear: false);
                Check("First population (nothing was selected before) -> item 0 selected",
                    lb.SelectedIndex == 0, true);
            }
            using (var lb = new System.Windows.Forms.ListBox())
            {
                lb.Items.AddRange(new object[] { "row 1", "row 2", "row 3" });
                OtaSpotsWindow.SelectFirstItemIfNoneSelectedYet(lb, hadSelectionBeforeClear: true);
                Check("Routine refresh (operator already had a selection before Clear()) -> left alone, not reset to 0",
                    lb.SelectedIndex == -1, true);
            }
            using (var lb = new System.Windows.Forms.ListBox())
            {
                OtaSpotsWindow.SelectFirstItemIfNoneSelectedYet(lb, hadSelectionBeforeClear: false);
                Check("Empty list -> no crash, stays unselected",
                    lb.SelectedIndex == -1, true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  OtaSpotsWindowRowFormattingTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── SpaceWx JSON deserialization: aIndex/xrayLong must not silently read as 0.0 ────
    // Root cause of a live JAWS pass finding A-index/X-ray always "0.0"/"0.0e+0": EngineHost's
    // SPACE_WX response used to serialize Nexus's own SpaceWx type verbatim, whose Rust field
    // names ("a_index"/"xray_long") don't match what ExternalDataClient's
    // JsonNamingPolicy.CamelCase looks for ("aIndex"/"xrayLong") -- System.Text.Json doesn't
    // throw on an unmatched property, it just leaves the C# property at its default value.
    // Fixed on EngineHost's own side (external_data.rs's new SpaceWxWire DTO, camelCase-renamed
    // + xrayClass/rScale added). This test locks in the C# side of the contract: feed it the
    // EXACT shape EngineHost now emits and confirm every field actually populates.
    static void SpaceWxJsonDeserializationTests()
    {
        Console.WriteLine("\n── SpaceWx JSON: aIndex/xrayLong deserialize from EngineHost's camelCase wire shape ──");
        try
        {
            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
            };

            // Exactly the shape external_data.rs's SpaceWxWire now serializes.
            string json = "{\"sfi\":130.5,\"ssn\":45.0,\"kp\":2.0,\"aIndex\":8.0,\"xrayLong\":1e-6,\"xrayClass\":\"C\",\"rScale\":0}";
            var wx = System.Text.Json.JsonSerializer.Deserialize<SpaceWx>(json, options);

            Check("Sfi deserializes (already worked before the fix -- no underscore in the name)",
                Math.Abs(wx.Sfi - 130.5f) < 0.01f, true);
            Check("AIndex deserializes to the real value, not the float default (the actual bug)",
                Math.Abs(wx.AIndex - 8.0f) < 0.01f, true);
            Check("XrayLong deserializes to the real value, not the float default (the actual bug)",
                Math.Abs(wx.XrayLong - 1e-6f) < 1e-9f, true);
            Check("XrayClass (Nexus's own flare-class letter) comes through",
                wx.XrayClass == "C", true);
            Check("RScale (Nexus's own NOAA R-scale) comes through",
                wx.RScale == 0, true);

            // The exact snake_case shape the bug used to produce -- must NOT populate anymore
            // (regression guard: if someone reverts the Rust rename, this test catches it).
            string brokenJson = "{\"sfi\":130.5,\"ssn\":45.0,\"kp\":2.0,\"a_index\":8.0,\"xray_long\":1e-6}";
            var broken = System.Text.Json.JsonSerializer.Deserialize<SpaceWx>(brokenJson, options);
            Check("Snake_case a_index does NOT match -> stays at the float default (proves the bug is real)",
                broken.AIndex == 0.0f, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  SpaceWxJsonDeserializationTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── OtaSpotsWindow.FormatNoaaScale: standard NOAA R/S/G descriptor words ──────
    static void FormatNoaaScaleTests()
    {
        Console.WriteLine("\n── OtaSpotsWindow.FormatNoaaScale: standard NOAA scale words ──");
        try
        {
            Check("G0 -> Quiet (G has no official NOAA word at 0; Jimmy Next's own concise label)",
                OtaSpotsWindow.FormatNoaaScale('G', 0) == "G0 - Quiet", true);
            Check("S0 -> None (S's own concise label at 0, distinct wording from G0)",
                OtaSpotsWindow.FormatNoaaScale('S', 0) == "S0 - None", true);
            Check("G1 -> Minor (NOAA's own standard word)",
                OtaSpotsWindow.FormatNoaaScale('G', 1) == "G1 - Minor", true);
            Check("G3 -> Strong",
                OtaSpotsWindow.FormatNoaaScale('G', 3) == "G3 - Strong", true);
            Check("S5 -> Extreme",
                OtaSpotsWindow.FormatNoaaScale('S', 5) == "S5 - Extreme", true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  FormatNoaaScaleTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── SpaceWxResult JSON: MufNow/Scales deserialize from EngineHost's wire shape ──
    // Investigated 2026-08-17: Nexus already computes a representative MUF (predict::
    // representative_muf) and NOAA's own G/S storm scales (live::swpc_scales::fetch_noaa_scales)
    // but EngineHost wasn't surfacing either. Locks in the C# side of the new SPACE_WX shape
    // (external_data.rs's SpaceWxPayload: mufNow, scales{gScale,gScaleTomorrow,sScale},
    // scalesAgeSecs, scalesLastError).
    static void SpaceWxMufAndScalesJsonDeserializationTests()
    {
        Console.WriteLine("\n── SpaceWxResult JSON: mufNow/scales deserialize from EngineHost's wire shape ──");
        try
        {
            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
            };

            string json = "{\"value\":{\"sfi\":130.5,\"ssn\":null,\"kp\":2.0,\"aIndex\":8.0,\"xrayLong\":1e-7," +
                "\"xrayClass\":\"B\",\"rScale\":0},\"ageSecs\":12,\"lastError\":null," +
                "\"mufNow\":18.4,\"scales\":{\"gScale\":1,\"gScaleTomorrow\":2,\"sScale\":0}," +
                "\"scalesAgeSecs\":30,\"scalesLastError\":null}";
            var result = System.Text.Json.JsonSerializer.Deserialize<SpaceWxResult>(json, options);

            Check("MufNow deserializes to the real value",
                result.MufNow.HasValue && Math.Abs(result.MufNow.Value - 18.4f) < 0.01f, true);
            Check("Scales.GScale deserializes",
                result.Scales != null && result.Scales.GScale == 1, true);
            Check("Scales.GScaleTomorrow deserializes",
                result.Scales != null && result.Scales.GScaleTomorrow == 2, true);
            Check("Scales.SScale deserializes",
                result.Scales != null && result.Scales.SScale == 0, true);

            // A response with no scales fetched yet (server startup) must not crash or fabricate
            // a Scales object -- Scales stays null, distinguishable from "fetched and all zero".
            string noScalesYet = "{\"value\":null,\"ageSecs\":null,\"lastError\":\"no data yet\"," +
                "\"mufNow\":null,\"scales\":null,\"scalesAgeSecs\":null,\"scalesLastError\":\"not fetched yet\"}";
            var noScales = System.Text.Json.JsonSerializer.Deserialize<SpaceWxResult>(noScalesYet, options);
            Check("Scales stays null (not a fabricated all-zero object) when never fetched",
                noScales.Scales == null, true);
            Check("MufNow stays null (not a fabricated 0.0) when the grid isn't resolvable",
                noScales.MufNow.HasValue, false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  SpaceWxMufAndScalesJsonDeserializationTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── OptionsDlg: constructs without throwing ─────────────────────────────────
    // Added 2026-08-10 after a real live crash: the Options dialog's TabControl -> ListBox +
    // detail-panel accessibility reorg reparented every category's real content panel directly
    // into a plain Panel host -- except basicPanel, which was left as a bare TabPage (TabPage
    // technically inherits from Panel, so this looked safe at the type level and compiled
    // clean). It crashed at runtime the moment Options was opened: WinForms' TabPage overrides
    // its own parent-assignment logic to throw ArgumentException unless the new parent is a
    // real TabControl -- a runtime-only invariant a clean compile can never catch. Fixed by
    // converting basicPanel to a plain Panel like its 15 siblings. This test is the permanent
    // guard: OptionsDlg's entire InitializeComponent() (where the crash happened, inside the
    // constructor, before the dialog is ever shown) runs every time this test runs, so any
    // future control that's added/reparented incorrectly the same way fails a fast, cheap,
    // always-on test instead of only surfacing live when a real operator next opens Options --
    // exactly what happened here. wsjtxClient/ctrl can both be null: InitializeComponent() runs
    // first, before either is ever touched (OptionsDlg.cs's own constructor order) -- no real
    // Controller/WsjtxClient/ini/engine needed for this specific check.
    // ── Startup status message: version + Prompt Mode (Alt+P), no obsolete UDP wording ──
    // Corrected 2026-08-12 (live JAWS feedback): "Waiting for WSJT-X" is startup wording from
    // before Direct engine mode existed. This is the very first status render of EVERY
    // session -- NegoState always starts at WAIT (ResetNego(), called unconditionally from the
    // WsjtxClient constructor, regardless of transport) -- so it fired under Direct mode too,
    // not just classic UDP. Fixed to reuse the EXISTING Prompt Mode setting (cmdPrompts,
    // toggled by Alt+P via TogglePrompts()) rather than a new preference, and the existing
    // pgmVer field (populated from the compiled assembly's own FileVersion -- see the
    // constructor) rather than a hardcoded version.
    static void StartupStatusMessageTests()
    {
        Console.WriteLine("\n── Startup status message (Prompt Mode / version, no obsolete wording) ──");

        var ctrl = new Controller();
        ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
        ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
        ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
        ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
        // Live-testing finding, 2026-08-21: Command Prompts is now beginner-mode-only
        // (TogglePrompts refuses while ctrl.advancedCallLayout is true -- see its own comment).
        // JimmySettings.AdvancedCallLayout's own raw field default is true (a pre-existing,
        // deliberate migration default for upgrades with no saved value yet -- LoadFromIni is
        // what actually resolves a fresh/no-ini install to false); this test never loads a real
        // ini, so it must set beginner mode explicitly to exercise Alt+P at all.
        ctrl.advancedCallLayout = false;
        var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);

        // cmdPrompts defaults to true, so the constructor's own first render already exercises
        // the Prompt-Mode-ON case.
        Check("Prompt Mode defaults to on", wc.cmdPrompts, true);
        CheckStr("Prompt Mode ON: startup text is version + the real Alt+K prompt, nothing else",
            ctrl.statusText.Text, $"{wc.pgmName} {wc.pgmVer}, use Alt, K, for command key list.");
        Check("Startup text never mentions the obsolete UDP-era \"Waiting for WSJT-X\" wording",
            ctrl.statusText.Text.Contains("WSJT-X") == false, true);

        // Alt+P's own real dispatch target (TogglePrompts), not a synthetic field flip -- both
        // flips cmdPrompts and re-renders, same as a live Alt+P press (Controller.cs's
        // ProcessCmdKey: hotkeyConfig[HotkeyAction.Prompts] -> TogglePrompts()) would.
        wc.TogglePrompts();
        Check("Alt+P (TogglePrompts) turns Prompt Mode off", wc.cmdPrompts, false);
        CheckStr("Prompt Mode OFF: startup text is version identification only",
            ctrl.statusText.Text, $"{wc.pgmName} {wc.pgmVer}.");

        wc.TogglePrompts();
        Check("Alt+P toggles Prompt Mode back on", wc.cmdPrompts, true);

        // The announced shortcut must be real, not a stale reference -- HotkeyAction.Help is
        // what Alt+K actually dispatches to (Controller.cs's ProcessCmdKey), still bound to
        // Alt+K by default.
        Check("The announced Alt+K shortcut is genuinely HotkeyAction.Help's current default binding",
            HotkeyConfig.Defaults[HotkeyAction.Help] == (System.Windows.Forms.Keys.Alt | System.Windows.Forms.Keys.K), true);
    }

    // ── Live-testing findings, 2026-08-21: beginner-mode-only accessibility fixes ──────────
    // Command Prompts (Alt+P), the hotkey-suffixed button names it now gates
    // (Controller.RefreshHotkeyAccessibleNames), and callListBox's own RX1/RX2 labeling
    // (WsjtxClient.UpdateCallListAccessibleName) are all beginner-mode (Advanced Call Layout
    // OFF) concepts -- none of them should have any effect while Advanced Call Layout is on.
    static void BeginnerModeOnlyAccessibilityTests()
    {
        Console.WriteLine("\n── Beginner-mode-only accessibility fixes (Command Prompts / hotkey names / list labels) ──");
        var ctrl = new Controller();
        ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
        ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
        ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
        ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
        ctrl.advancedCallLayout = true;
        var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
        // RefreshHotkeyAccessibleNames reads ctrl.wsjtxClient (Controller's own field) to decide
        // whether to show hotkey suffixes, and returns immediately if ctrl.hotkeyConfig is null
        // (its own "have hotkeys loaded yet" gate) -- real Form_Load assigns both at startup;
        // this test's bare `new Controller()` needs the same explicit setup other tests don't
        // need (they never touch RefreshHotkeyAccessibleNames).
        ctrl.wsjtxClient = wc;
        ctrl.hotkeyConfig = new HotkeyConfig();

        // T2 fix, 2026-08-23: TogglePrompts (Alt+P) now works in BOTH layouts -- it used to
        // refuse outright in Advanced Call Layout. In Advanced UI it controls the button
        // hotkey-label display only (RefreshHotkeyAccessibleNames, already layout-agnostic);
        // the Beginner-only canned status prompts it also used to gate are now independently
        // blocked directly in ShowStatus() regardless of cmdPrompts' value -- see
        // ShowStatusNeverEmitsBeginnerPromptInAdvancedLayoutTests below for that half.
        bool wasOn = wc.cmdPrompts;
        wc.TogglePrompts();
        Check("THE FIX: TogglePrompts actually toggles cmdPrompts while Advanced Call Layout is on",
            wc.cmdPrompts != wasOn, true);

        // Toggling back in Advanced mode still works (not a one-shot escape hatch).
        wc.TogglePrompts();
        Check("...and toggles back", wc.cmdPrompts == wasOn, true);

        // Switch to beginner mode -- TogglePrompts still works normally.
        ctrl.advancedCallLayout = false;
        wc.TogglePrompts();
        Check("TogglePrompts works normally in Beginner mode too",
            wc.cmdPrompts != wasOn, true);

        // RefreshHotkeyAccessibleNames: hotkey suffix only appears when cmdPrompts is on, and
        // never for an action with no hotkey assigned (OpenLogbook defaults to Keys.None).
        wc.cmdPrompts = true;
        ctrl.RefreshHotkeyAccessibleNames();
        Check("Prompts ON: Options button's name includes its real hotkey",
            ctrl.optionsButton.AccessibleName.Contains(HotkeyConfig.FormatKeysForHelp(HotkeyConfig.Defaults[HotkeyAction.Options])), true);

        wc.cmdPrompts = false;
        ctrl.RefreshHotkeyAccessibleNames();
        CheckStr("Prompts OFF: Options button's name reverts to plain (no hotkey suffix)",
            ctrl.optionsButton.AccessibleName, "Options");

        // UpdateCallListAccessibleName: callListBox never gets RX1/RX2 labeling outside
        // Advanced Call Layout -- that side-labeling concept has no counterpart in beginner mode.
        ctrl.advancedCallLayout = false;
        wc.UpdateCallListAccessibleName(force: true);
        CheckStr("Beginner mode: callListBox's name is plain, no RX1/RX2 side label",
            ctrl.callListBox.AccessibleName, "Stations Available");

        ctrl.advancedCallLayout = true;
        wc.UpdateCallListAccessibleName(force: true);
        Check("Advanced mode: callListBox's name DOES carry the RX1/RX2 side label",
            ctrl.callListBox.AccessibleName.StartsWith("RX"), true);
    }

    static void OptionsDlgConstructionTests()
    {
        Console.WriteLine("\n── OptionsDlg: constructs without throwing ──");
        try
        {
            using (var dlg = new OptionsDlg(null, null))
            {
                Check("OptionsDlg constructs (InitializeComponent) without throwing", true, true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  OptionsDlg construction threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }

        // Root-caused live, 2026-08-12: InitializeComponent alone never reaches
        // BuildFrequenciesTab (only OptionsDlg_Load does, which the construction-only check
        // above never triggers), so the NullReferenceException that actually crashed Options
        // the instant Alt+O was pressed went uncaught. Calling BuildFrequenciesTab directly --
        // rather than the full OptionsDlg_Load, which drags in every OTHER panel's own
        // unrelated scaffolding needs (BuildHotkeysTab needs a real Controller.hotkeyConfig,
        // BuildRadioTab touches System.IO.Ports, etc. -- not needed to cover this specific bug)
        // -- targets exactly the method that crashed, with no window ever appearing.
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);

            using (var dlg = new OptionsDlg(wc, ctrl))
            {
                dlg.BuildFrequenciesTab();
                Check("BuildFrequenciesTab runs without throwing (the actual live-reported crash)", true, true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  BuildFrequenciesTab threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── ClubLogProvider: prefixes/exceptions tables ─────────────────────────────
    // Found via live A6 field testing 2026-07-16: <entities><entity><prefix> is only
    // ONE default prefix per entity (confirmed against a real cached Club Log
    // download: UNITED STATES OF AMERICA's own entity record lists just "K"), so a
    // real callsign like "NP4TX" (Puerto Rico, prefix "NP4") never matched anything
    // via the old entities-only FindByCallsign. Club Log's actual schema also
    // publishes <prefixes> (the comprehensive prefix-to-entity table -- confirmed
    // "NP4" -> PUERTO RICO in the real data) and <exceptions> (exact full-callsign
    // overrides), neither previously parsed at all. This fixture is a small,
    // hand-built XML sample matching the real confirmed schema exactly (same tag
    // names/structure/casing), not the full multi-hundred-KB real file.
    const string ClubLogTestXml = @"<?xml version='1.0'?>
<clublog date='2026-07-12T20:30:07+00:00'>
<entities>
	<entity>
		<adif>291</adif>
		<name>UNITED STATES OF AMERICA</name>
		<prefix>K</prefix>
		<deleted>false</deleted>
		<cqz>5</cqz>
		<cont>NA</cont>
	</entity>
	<entity>
		<adif>202</adif>
		<name>PUERTO RICO</name>
		<prefix>KP4</prefix>
		<deleted>false</deleted>
		<cqz>8</cqz>
		<cont>NA</cont>
	</entity>
	<entity>
		<adif>230</adif>
		<name>FEDERAL REPUBLIC OF GERMANY</name>
		<prefix>DL</prefix>
		<deleted>false</deleted>
		<cqz>14</cqz>
		<cont>EU</cont>
	</entity>
	<entity>
		<adif>81</adif>
		<name>GERMANY</name>
		<prefix>Y2</prefix>
		<deleted>true</deleted>
		<cqz>14</cqz>
		<cont>EU</cont>
	</entity>
</entities>
<exceptions>
	<exception record='1'>
		<call>W1AW/KP4</call>
		<entity>PUERTO RICO</entity>
		<adif>202</adif>
		<cqz>8</cqz>
		<cont>NA</cont>
	</exception>
</exceptions>
<prefixes>
	<prefix record='1'>
		<call>K</call>
		<entity>UNITED STATES OF AMERICA</entity>
		<adif>291</adif>
		<cqz>5</cqz>
		<cont>NA</cont>
	</prefix>
	<prefix record='2'>
		<call>N</call>
		<entity>UNITED STATES OF AMERICA</entity>
		<adif>291</adif>
		<cqz>5</cqz>
		<cont>NA</cont>
	</prefix>
	<prefix record='3'>
		<call>KP4</call>
		<entity>PUERTO RICO</entity>
		<adif>202</adif>
		<cqz>8</cqz>
		<cont>NA</cont>
	</prefix>
	<prefix record='4'>
		<call>NP4</call>
		<entity>PUERTO RICO</entity>
		<adif>202</adif>
		<cqz>8</cqz>
		<cont>NA</cont>
		<start>1978-03-24T00:00:00+00:00</start>
	</prefix>
	<prefix record='5'>
		<call>DL</call>
		<entity>GERMANY</entity>
		<adif>81</adif>
		<cqz>14</cqz>
		<cont>EU</cont>
		<end>1973-09-16T23:59:59+00:00</end>
	</prefix>
	<prefix record='6'>
		<call>DL</call>
		<entity>FEDERAL REPUBLIC OF GERMANY</entity>
		<adif>230</adif>
		<cqz>14</cqz>
		<cont>EU</cont>
	</prefix>
	<prefix record='7'>
		<call>K4</call>
		<entity>PUERTO RICO</entity>
		<adif>202</adif>
		<cqz>8</cqz>
		<cont>NA</cont>
		<end>1946-12-31T23:59:59+00:00</end>
	</prefix>
</prefixes>
</clublog>";

    static void ClubLogPrefixTableTests()
    {
        Console.WriteLine("\n── ClubLogProvider: prefixes/exceptions tables ──");
        string tmpRoot = Path.Combine(Path.GetTempPath(), "JimmyTest_ClubLog_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(tmpRoot, "ClubLog"));
            File.WriteAllText(Path.Combine(tmpRoot, "ClubLog", "clublog_cty.xml"), ClubLogTestXml);

            var provider = new ClubLogProvider(tmpRoot);
            provider.Configure(true, "");
            provider.Load();

            Check("Entity list still loads (RuleUniverse's AllEntities/EntityCount unaffected)",
                  provider.EntityCount == 4, true);

            // Case 1: NP4TX -- the actual real-world call that failed before this fix.
            // Its prefix "NP4" only exists in <prefixes>, never in PUERTO RICO's own
            // <entities><entity><prefix> ("KP4").
            var np4tx = provider.FindByCallsign("NP4TX");
            Check("NP4TX resolves via <prefixes> table (was previously unresolvable)", np4tx != null, true);
            if (np4tx != null)
                CheckStr("NP4TX -> PUERTO RICO", np4tx.Name, "PUERTO RICO");

            // Case 2: KP4TX -- same entity, via its <entities> default prefix (already
            // worked before this fix) -- must still work identically.
            var kp4tx = provider.FindByCallsign("KP4TX");
            Check("KP4TX still resolves (entity's own default prefix, pre-existing path)", kp4tx != null, true);
            if (kp4tx != null)
                CheckStr("KP4TX -> PUERTO RICO", kp4tx.Name, "PUERTO RICO");

            // Case 3: plain "K" call -- USA, via <prefixes>.
            var kCall = provider.FindByCallsign("K1ABC");
            Check("K1ABC -> USA via <prefixes>", kCall != null && kCall.Name == "UNITED STATES OF AMERICA", true);

            // Case 4: exact <exceptions> override wins even though its own prefix ("W")
            // would otherwise resolve to USA.
            var exception = provider.FindByCallsign("W1AW/KP4");
            Check("W1AW/KP4 exception override -> PUERTO RICO (not USA, despite W prefix)",
                  exception != null && exception.Name == "PUERTO RICO", true);

            // Case 5: "DL" has two <prefixes> records -- one expired in 1973 (GERMANY,
            // deleted entity), one with no <end> (FEDERAL REPUBLIC OF GERMANY, current)
            // -- must resolve to the currently-valid one, not whichever parsed last.
            var dl = provider.FindByCallsign("DL1ABC");
            Check("DL1ABC resolves to the currently-valid entity, not the expired 1973 one",
                  dl != null && dl.Name == "FEDERAL REPUBLIC OF GERMANY", true);

            // Case 6: unresolvable call (no matching prefix at any length) -- must not
            // crash, just return null.
            Check("Completely unrelated call returns null, no crash",
                  provider.FindByCallsign("1A1A") == null, true);

            // Case 7: real-world bug found via live A6 field testing 2026-07-16 --
            // "K4" has only ONE <prefixes> record, PUERTO RICO, expired 1946 (a
            // pre-war assignment long superseded by the modern US call-area system,
            // with no separate current-day "K4" record to prefer over it). A real
            // K4-prefixed US station like K4YT must still resolve to USA via the
            // shorter, current "K" match -- not the longer but expired "K4" one.
            var k4call = provider.FindByCallsign("K4YT");
            Check("K4YT -> USA, not the expired 1946 K4/Puerto Rico record",
                  k4call != null && k4call.Name == "UNITED STATES OF AMERICA", true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  ClubLogPrefixTableTests threw: {ex.GetType().Name}: {ex.Message}");
            failed++;
        }
        finally
        {
            try { Directory.Delete(tmpRoot, recursive: true); } catch { }
        }
    }

    // ── EnqueueDecodeMessage.FromStandardDecode ─────────────────────────────────
    // Found via live A7 field testing 2026-07-17: Jimmy's decode-processing code only
    // ever reacted to the non-standard EnqueueDecodeMessage (Andy WM8Q's fork only) --
    // stock WSJT-X and WSJT-X Improved send the standard base-class DecodeMessage
    // instead, which was never wired to anything. FromStandardDecode adapts one into
    // the other so it can flow through the exact same ProcessDecodeMsg/
    // ClassificationEngine pipeline. These tests cover the adapter itself; end-to-end
    // queue admission (including the AutoGen="replying to me" path) is covered by
    // JimmyReplay.py's group19_standard_decode_message instead, since ProcessDecodeMsg
    // needs a live socket-driven WsjtxClient.
    static void EnqueueDecodeMessageFromStandardDecodeTests()
    {
        Console.WriteLine("\n── EnqueueDecodeMessage.FromStandardDecode ──");

        var now = DateTime.UtcNow.Date;
        var since = TimeSpan.FromMinutes(5);
        var src = new DecodeMessage
        {
            SchemaVersion = 3,
            Id = "WSJT-X",
            New = true,
            SinceMidnight = since,
            RxDate = now,
            Snr = -12,
            DeltaTime = 0.3,
            DeltaFrequency = 1500,
            Mode = "FT8",
            Message = "CQ W6NEW EM63",
            Priority = 0,
            UseStdReply = false,
            OffAir = false,
        };

        var result = EnqueueDecodeMessage.FromStandardDecode(src);

        Check("SchemaVersion copied", result.SchemaVersion == 3, true);
        CheckStr("Id copied", result.Id, "WSJT-X");
        Check("New copied", result.New, true);
        Check("SinceMidnight copied", result.SinceMidnight == since, true);
        Check("RxDate copied", result.RxDate == now, true);
        Check("Snr copied", result.Snr == -12, true);
        Check("DeltaTime copied", Math.Abs(result.DeltaTime - 0.3) < 0.0001, true);
        Check("DeltaFrequency copied", result.DeltaFrequency == 1500, true);
        CheckStr("Mode copied", result.Mode, "FT8");
        CheckStr("Message copied", result.Message, "CQ W6NEW EM63");
        Check("UseStdReply copied", result.UseStdReply, false);
        Check("OffAir copied", result.OffAir, false);

        // The critical semantic mapping: a standard Decode broadcast is always
        // automatic -- there's no "manually enqueued" concept in the standard
        // protocol. AutoGen=false here would silently break the "someone is
        // replying to me" handling in ProcessDecodeMsg.
        Check("AutoGen is always true (standard Decode has no manual-enqueue concept)",
              result.AutoGen, true);

        // Quality must be computed from Message content (never wire-supplied, even
        // for a real EnqueueDecodeMessage) -- matching SetMsgQuality()'s own logic
        // exactly, not left at the Qualities.NONE default.
        Check("Quality: CQ message -> HIGH",
              EnqueueDecodeMessage.FromStandardDecode(new DecodeMessage { Message = "CQ W6NEW EM63" }).Quality
                  == (int)EnqueueDecodeMessage.Qualities.HIGH, true);
        Check("Quality: 73 message -> MEDIUM",
              EnqueueDecodeMessage.FromStandardDecode(new DecodeMessage { Message = "W6NEW KB0UZT 73" }).Quality
                  == (int)EnqueueDecodeMessage.Qualities.MEDIUM, true);
        Check("Quality: RRR message -> MARGINAL",
              EnqueueDecodeMessage.FromStandardDecode(new DecodeMessage { Message = "W6NEW KB0UZT RRR" }).Quality
                  == (int)EnqueueDecodeMessage.Qualities.MARGINAL, true);
        Check("Quality: plain report message -> LOW",
              EnqueueDecodeMessage.FromStandardDecode(new DecodeMessage { Message = "W6NEW KB0UZT -05" }).Quality
                  == (int)EnqueueDecodeMessage.Qualities.LOW, true);

        // Fields ProcessDecodeMsg/downstream always overwrite before any read must be
        // safe at their plain default -- not asserting specific values, just that
        // constructing the adapter doesn't throw and leaves them at sane defaults.
        Check("Rank defaults to 0 (always overwritten by SetRank before read)", result.Rank == 0, true);
        Check("Category defaults (always overwritten by DeriveCategory before read)",
              result.Category == default, true);
        CheckStr("MatchedAwardRuleId defaults to null (always overwritten before read)",
              result.MatchedAwardRuleId, null);
    }

    // ── DecodeMessage.IsCallTo ───────────────────────────────────────────────────
    // Direct unit coverage for the 2026-08-07 fix: IsCallTo used a case-sensitive ==
    // comparison (myCall == ToCall(Message)). Under Jimmy Native, myCall keeps
    // whatever case the operator actually typed in Options (lower case is a normal,
    // expected thing to type); wire text is always upper case. The case-sensitive
    // compare then never matched, so every reply from every QSO partner was silently
    // classified as "not directed at me" -- see DecodeMessage.cs's own comment on the
    // fix for the full incident. This is a live-protocol-free, single-process check
    // of the fixed comparison itself; JimmyReplay.py's end-to-end groups exercise the
    // surrounding queue/logging behavior for a matched-case myCall (Group 1) without
    // needing a mid-session myCall change, which a real operator would never do
    // without reconnecting anyway.
    static void DecodeMessageIsCallToTests()
    {
        Console.WriteLine("\n── DecodeMessage.IsCallTo ──");

        Check("Lower-case myCall matches upper-case wire text",
              new DecodeMessage { Message = "KB0UZT K4YT EM63" }.IsCallTo("kb0uzt"), true);
        Check("Mixed-case myCall matches upper-case wire text",
              new DecodeMessage { Message = "KB0UZT K4YT EM63" }.IsCallTo("Kb0Uzt"), true);
        Check("Upper-case myCall matches upper-case wire text (unchanged baseline)",
              new DecodeMessage { Message = "KB0UZT K4YT EM63" }.IsCallTo("KB0UZT"), true);
        Check("Different callsign does not match regardless of case",
              new DecodeMessage { Message = "KB0UZT K4YT EM63" }.IsCallTo("w6new"), false);
        Check("Null myCall never matches (no throw)",
              new DecodeMessage { Message = "KB0UZT K4YT EM63" }.IsCallTo(null), false);
    }

    // ── WsjtxClient.DefaultTrPeriodMs ───────────────────────────────────────────
    // Found via live field testing 2026-07-17: WSJT-X Improved 3.1's StatusMessage
    // never reports a real TRPeriod at all -- confirmed directly via a real session's
    // debug log (every single StatusMessage from this build carried the N/A
    // sentinel). Without a fallback, Jimmy's trPeriod field stayed permanently null
    // for the whole connection, which silently broke the even/odd period-parity math
    // raw-decode TX1/TX2 display depends on (IsEvenPeriod's final comparison
    // collapses to "null == 0" under C#'s lifted nullable-comparison semantics, which
    // is always false) -- decodes only ever displayed in TX2, never TX1, regardless
    // of real signal on both. FT8/FT4's T/R periods are fixed protocol constants;
    // this pure function is the core of the fix (WsjtxClient.Protocol.cs's
    // UpdateTrPeriod calls it, but that method needs a live StatusMessage/WsjtxClient
    // instance to exercise, so it's tested here in isolation instead).
    static void DefaultTrPeriodMsTests()
    {
        Console.WriteLine("\n── WsjtxClient.DefaultTrPeriodMs ──");
        Check("FT8 defaults to 15000ms", WsjtxClient.DefaultTrPeriodMs("FT8") == 15000, true);
        Check("FT4 defaults to 7500ms", WsjtxClient.DefaultTrPeriodMs("FT4") == 7500, true);
        Check("Unknown/null mode falls back to the FT8 default (most common case)",
              WsjtxClient.DefaultTrPeriodMs(null) == 15000, true);
    }

    // ── QrzLogbookClient.IsDuplicateReason ──────────────────────────────────────
    // QRZ reports "already have this QSO" as RESULT=FAIL with a REASON mentioning
    // "duplicate" rather than a distinct result code -- this must be recognized
    // so a duplicate is marked handled instead of retried forever on every Alt+U.
    static void QrzIsDuplicateReasonTests()
    {
        Console.WriteLine("\n── QrzLogbookClient.IsDuplicateReason ──");
        Check("exact QRZ duplicate message recognized",
              QrzLogbookClient.IsDuplicateReason("Unable to add QSO to database: duplicate"), true);
        Check("case-insensitive match",
              QrzLogbookClient.IsDuplicateReason("DUPLICATE QSO"), true);
        Check("unrelated failure reason is not treated as duplicate",
              QrzLogbookClient.IsDuplicateReason("Invalid API Key"), false);
        Check("null reason is not a duplicate", QrzLogbookClient.IsDuplicateReason(null), false);
        Check("empty reason is not a duplicate", QrzLogbookClient.IsDuplicateReason(""), false);
        Check("whitespace-only reason is not a duplicate", QrzLogbookClient.IsDuplicateReason("   "), false);
    }

    // ── HrdLogUploadClient.ClassifyResponse ──────────────────────────────────────
    // HRDLog.net's NewEntry.aspx reply format and these exact fixture bodies are ported
    // directly from the open-source Nexus project's crates/tempo-core/src/hrdlog.rs unit
    // tests, since Jimmy's own codebase has no other documentation of HRDLog's real XML shape.
    static void HrdLogClassifyResponseTests()
    {
        Console.WriteLine("\n── HrdLogUploadClient.ClassifyResponse ──");

        string ok = "<?xml version=\"1.0\" ?><HrdLog xmlns=\"http://xml.hrdlog.com\">" +
                    "<NewEntry><insert>1</insert></NewEntry></HrdLog>";
        Check("insert=1 is Ok", HrdLogUploadClient.ClassifyResponse(ok).Result == HrdLogUploadClient.HrdLogResult.Ok, true);

        string dup = "<HrdLog><NewEntry><insert>0</insert></NewEntry></HrdLog>";
        Check("insert=0 is Duplicate", HrdLogUploadClient.ClassifyResponse(dup).Result == HrdLogUploadClient.HrdLogResult.Duplicate, true);

        string unknownUser = "<HrdLog><NewEntry><error>Unknown user</error></NewEntry></HrdLog>";
        var unknownUserResult = HrdLogUploadClient.ClassifyResponse(unknownUser);
        Check("'Unknown user' error is AuthFail", unknownUserResult.Result == HrdLogUploadClient.HrdLogResult.AuthFail, true);
        Check("'Unknown user' error message preserved", unknownUserResult.Message == "Unknown user", true);

        string invalidToken = "<HrdLog><NewEntry><error>Invalid token</error></NewEntry></HrdLog>";
        Check("'Invalid token' error is AuthFail",
              HrdLogUploadClient.ClassifyResponse(invalidToken).Result == HrdLogUploadClient.HrdLogResult.AuthFail, true);

        string badAdif = "<HrdLog><NewEntry><error>A key should contain at least: Call, QSO_Date, " +
                          "Time_On</error></NewEntry></HrdLog>";
        var badAdifResult = HrdLogUploadClient.ClassifyResponse(badAdif);
        Check("other error text is Rejected, not AuthFail", badAdifResult.Result == HrdLogUploadClient.HrdLogResult.Rejected, true);
        Check("Rejected keeps the error message",
              badAdifResult.Message != null && badAdifResult.Message.Contains("Call, QSO_Date"), true);

        Check("unrecognized HTML body is Unknown (transient, not a bounce)",
              HrdLogUploadClient.ClassifyResponse("<html>500 Internal Server Error</html>").Result == HrdLogUploadClient.HrdLogResult.Unknown, true);
        Check("empty body is Unknown", HrdLogUploadClient.ClassifyResponse("").Result == HrdLogUploadClient.HrdLogResult.Unknown, true);
    }

    // ── RigctldClient.ListRigModels ──────────────────────────────────────────────
    // Runs the actual bundled rigctl.exe --list and parses its fixed-column output --
    // regression coverage for the 2026-08-07 rig-model dropdown: a whitespace split would
    // break on multi-word manufacturer names ("N2ADR James Ahlstrom", "Vertex Standard"),
    // which is exactly why this parses fixed columns instead. Skips (warns) rather than
    // fails if the bundled exe isn't present next to JimmyTests.exe -- it ships next to
    // Jimmy.exe, not this test binary.
    static void RigctldClientListRigModelsTests()
    {
        Console.WriteLine("\n── RigctldClient.ListRigModels ──");

        var models = RigctldClient.ListRigModels();
        if (models.Count == 0)
        {
            Console.WriteLine("  WARN  ListRigModels returned 0 entries -- rigctl.exe not found next to JimmyTests.exe (expected; it ships next to Jimmy.exe). Skipping.");
            return;
        }

        Check("returns a large real list (Hamlib supports hundreds of rigs)", models.Count > 100, true);

        var kenwood590sg = models.Find(m => m.Id == 2037);
        Check("Kenwood TS-590SG (id 2037) is present", kenwood590sg != null, true);
        if (kenwood590sg != null)
        {
            CheckStr("id 2037 manufacturer is Kenwood", kenwood590sg.Mfg, "Kenwood");
            CheckStr("id 2037 model is TS-590SG", kenwood590sg.Model, "TS-590SG");
            CheckStr("Display combines mfg/model/id", kenwood590sg.Display, "Kenwood TS-590SG (2037)");
        }

        // The multi-word-manufacturer edge case that motivated fixed-column parsing over a
        // whitespace split -- id 10 is Hamlib's own "N2ADR James Ahlstrom" / Quisk entry.
        var quisk = models.Find(m => m.Id == 10);
        Check("multi-word manufacturer (id 10, Quisk) is present", quisk != null, true);
        if (quisk != null)
        {
            CheckStr("multi-word manufacturer parsed whole, not split", quisk.Mfg, "N2ADR James Ahlstrom");
            CheckStr("model column unaffected by the long manufacturer name", quisk.Model, "Quisk");
        }

        Check("no duplicate IDs in the parsed list",
              models.Count == new System.Collections.Generic.HashSet<int>(models.ConvertAll(m => m.Id)).Count, true);
    }

    // ── RigctldClient.ReadStdoutBounded ─────────────────────────────────────────────
    // Independent audit finding, 2026-08-30: ListRigModels used proc.StandardOutput.ReadToEnd()
    // BEFORE proc.WaitForExit(10_000), so the timeout bounded nothing -- a rigctl.exe that hung
    // or whose stderr pipe filled froze the Options Radio tab until Jimmy was killed. The read
    // is now raced against the timeout with kill-on-overrun (ReadStdoutBounded). rigctl.exe
    // itself can't be made to hang on demand, so this drives the helper with cmd.exe stand-ins.
    static void RigctldClientBoundedReadTests()
    {
        Console.WriteLine("\n── RigctldClient.ReadStdoutBounded: the read is actually bounded -- THE FIX ──");

        // ping.exe: a standalone process (no cmd.exe wrapper, so ReadStdoutBounded's plain
        // proc.Kill() reaps it directly with no orphaned child), it writes to stdout, and
        // '-n <count>' gives a predictable runtime.
        string pingExe = System.IO.Path.Combine(Environment.SystemDirectory, "ping.exe");
        if (!System.IO.File.Exists(pingExe))
        {
            Console.WriteLine("  WARN  ping.exe not found -- skipping (non-Windows or unusual environment).");
            return;
        }

        System.Diagnostics.ProcessStartInfo Psi(string args) => new System.Diagnostics.ProcessStartInfo
        {
            FileName = pingExe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // Fast, well-behaved child: exits on its own well inside the budget, output intact.
        using (var p = System.Diagnostics.Process.Start(Psi("-n 1 127.0.0.1")))
        {
            string outp = RigctldClient.ReadStdoutBounded(p, 10_000);
            Check("a fast child's stdout is returned intact", outp.Contains("127.0.0.1"), true);
        }

        // Child that stays alive (holding its stdout handle open) far past the budget:
        // 'ping -n 20' runs ~19s. A 1s budget must not wait for it -- ReadStdoutBounded returns
        // whatever partial output was buffered (same as NativeEngineClient.ListDevices), which
        // ListRigModels then parses harmlessly, rather than freezing the Options tab.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (var p = System.Diagnostics.Process.Start(Psi("-n 20 127.0.0.1")))
        {
            string outp = RigctldClient.ReadStdoutBounded(p, 1_000);
            sw.Stop();
            Check("THE FIX: a hanging child does NOT block past the budget (returned well under the child's ~19s runtime)",
                  sw.ElapsedMilliseconds < 5_000, true);
            Check("THE FIX: the overrun killed the child process",
                  p.HasExited, true);
            Check("THE FIX: the read was cut short at the budget, not run to completion (far fewer than 20 ping replies captured)",
                  outp.Split('\n').Length < 20, true);
        }
    }

    // ── OptionsDlg.ExtractRigModelId ─────────────────────────────────────────────
    // ── T13 fix, 2026-08-23: audio device combo "System default" label round-trips to/from
    // the stored empty string exactly -- the display change must never alter what's actually
    // saved to NativeEngine.AudioInputDevice/AudioOutputDevice ──
    static void OptionsDlgSystemDefaultDeviceLabelTests()
    {
        Console.WriteLine("\n── T13 fix: 'System default' audio device label round-trip -- THE FIX ──");
        CheckStr("THE FIX: an empty stored value displays as 'System default', not a blank item",
            OptionsDlg.ToDisplayDeviceName(""), OptionsDlg.SystemDefaultDeviceLabel);
        CheckStr("A null stored value also displays as 'System default'",
            OptionsDlg.ToDisplayDeviceName(null), OptionsDlg.SystemDefaultDeviceLabel);
        CheckStr("A real device name displays unchanged",
            OptionsDlg.ToDisplayDeviceName("USB Audio CODEC"), "USB Audio CODEC");
        CheckStr("THE FIX: selecting 'System default' saves back to the empty string (no storage-format change)",
            OptionsDlg.ToStoredDeviceName(OptionsDlg.SystemDefaultDeviceLabel), "");
        CheckStr("A real device name saves back unchanged",
            OptionsDlg.ToStoredDeviceName("USB Audio CODEC"), "USB Audio CODEC");
    }

    static void OptionsDlgExtractRigModelIdTests()
    {
        Console.WriteLine("\n── OptionsDlg.ExtractRigModelId ──");

        CheckStr("extracts id from a normal catalog entry",
              OptionsDlg.ExtractRigModelId("Kenwood TS-590SG (2037)"), "2037");
        CheckStr("extracts id from a multi-word manufacturer entry",
              OptionsDlg.ExtractRigModelId("Vertex Standard VX-1700 (1033)"), "1033");
        CheckStr("extracts the raw value from the unlisted-fallback entry",
              OptionsDlg.ExtractRigModelId("(currently configured: 9999)"), "9999");
        CheckStr("a bare number (old-style stored value) passes through unchanged",
              OptionsDlg.ExtractRigModelId("2037"), "2037");
        CheckStr("unrecognized text passes through unchanged rather than disappearing",
              OptionsDlg.ExtractRigModelId("garbage"), "garbage");
        CheckStr("null passes through as null (no throw)",
              OptionsDlg.ExtractRigModelId(null), null);
        CheckStr("empty string passes through as empty",
              OptionsDlg.ExtractRigModelId(""), "");
    }

    // ── TqslUploadClient.ParseFinalStatus ────────────────────────────────────────
    // These three stderr examples are copied verbatim from TQSL 2.8's own installed
    // documentation (TrustedQSL\help\tqslapp\cmdline.htm's "Status Examples" section), not
    // fabricated -- confirms the "Final Status: Description (Code)" parser matches TQSL's
    // real batch-mode (-x) output shape, including the cancelled/partial/success cases.
    static void TqslParseFinalStatusTests()
    {
        Console.WriteLine("\n── TqslUploadClient.ParseFinalStatus ──");

        string cancelled =
            "05:57:39 PM: Warning: Signing cancelled\n" +
            "05:57:39 PM: No records output\n" +
            "05:57:39 PM: Final Status: cancelled by user (1)\n";
        var c = TqslUploadClient.ParseFinalStatus(cancelled);
        Check("cancelled-by-user code parsed", c.Code == 1, true);
        Check("cancelled-by-user description parsed", c.Description == "cancelled by user", true);

        string partial =
            "06:05:56 PM: /home/rmurphy/k1mu.adi: 414 QSO records were already uploaded\n" +
            "06:05:56 PM: /home/rmurphy/k1mu.adi: wrote 1 records to /home/rmurphy/k1mu.tq8\n" +
            "06:05:56 PM: /home/rmurphy/k1mu.tq8 is ready to be emailed or uploaded.\n" +
            "Note: TQSL assumes that this file will be uploaded to LoTW.\n" +
            "Resubmitting these QSOs will cause them to be reported as already uploaded.\n" +
            "06:05:56 PM: Final Status: Some QSOs were already uploaded or out of date range (9)\n";
        var p = TqslUploadClient.ParseFinalStatus(partial);
        // Release-audit finding, 2026-08-20: UploadPendingAsync no longer marks QSOs uploaded
        // on this code (8/9/14 all conflate "already uploaded" with "out of date range" in
        // TQSL's own status text -- see that method's own comment) -- this label used to say
        // "treated as success, not a failure", which described the OLD caller behavior
        // (blanket-marked the whole batch). This test only covers the parser itself, unchanged.
        Check("partial-upload code parsed (caller now treats this as 'ambiguous, not marked, will retry')", p.Code == 9, true);
        Check("partial-upload description parsed", p.Description == "Some QSOs were already uploaded or out of date range", true);

        string success =
            "17:21:32 PM: /Signing using Callsign W4TV, DXCC Entity UNITED STATES OF AMERICA\n" +
            "17:21:32 PM: /Attempting to upload 2 QSOs\n" +
            "17:21:33 PM: /Log uploaded successfully with result \"File queued for processing\"!\n" +
            "17:21:33 PM: /Final Status: Success (0)\n";
        var s = TqslUploadClient.ParseFinalStatus(success);
        Check("success code parsed", s.Code == 0, true);
        Check("success description parsed", s.Description == "Success", true);

        var none = TqslUploadClient.ParseFinalStatus("no status line here at all");
        Check("missing status line yields null code (must be treated as failure, not assumed success)", none.Code == null, true);
        Check("empty stderr yields null code", TqslUploadClient.ParseFinalStatus("").Code == null, true);
    }

    // ── TqslUploadClient.ClassifyFinalStatus ────────────────────────────────────
    // Release-audit finding, 2026-08-20 (release blocker): codes 8/9/14 used to be
    // blanket-treated the same as 0 (mark the whole pending batch uploaded) -- verified
    // directly against TQSL 2.8's own installed documentation that 8/9/14 all conflate
    // "already uploaded" (safe) with "out of date range" (never actually uploaded, so NOT
    // safe to mark) in one aggregate code, with no per-record breakdown available.
    static void TqslClassifyFinalStatusTests()
    {
        Console.WriteLine("\n── TqslUploadClient.ClassifyFinalStatus (TQSL exit-code -> mark decision) ──");
        Check("code 0 (full success) -> mark all uploaded",
              TqslUploadClient.ClassifyFinalStatus(0) == TqslUploadClient.FinalStatusOutcome.MarkAllUploaded, true);
        // THE FIX: these three used to also return MarkAllUploaded.
        Check("code 8 (no QSOs processed, already-uploaded-or-out-of-range) -> ambiguous, leave unmarked -- THE FIX",
              TqslUploadClient.ClassifyFinalStatus(8) == TqslUploadClient.FinalStatusOutcome.AmbiguousLeaveUnmarked, true);
        Check("code 9 (some processed, some ignored as already-uploaded-or-out-of-range) -> ambiguous, leave unmarked -- THE FIX",
              TqslUploadClient.ClassifyFinalStatus(9) == TqslUploadClient.FinalStatusOutcome.AmbiguousLeaveUnmarked, true);
        Check("code 14 (some QSOs already uploaded) -> ambiguous, leave unmarked -- THE FIX",
              TqslUploadClient.ClassifyFinalStatus(14) == TqslUploadClient.FinalStatusOutcome.AmbiguousLeaveUnmarked, true);
        Check("code 1 (cancelled by user) -> real failure",
              TqslUploadClient.ClassifyFinalStatus(1) == TqslUploadClient.FinalStatusOutcome.Failure, true);
        Check("code 2 (rejected by LoTW) -> real failure",
              TqslUploadClient.ClassifyFinalStatus(2) == TqslUploadClient.FinalStatusOutcome.Failure, true);
        Check("code 11 (LoTW connection error) -> real failure",
              TqslUploadClient.ClassifyFinalStatus(11) == TqslUploadClient.FinalStatusOutcome.Failure, true);
        Check("null code (no parseable status line) -> real failure, never assumed success",
              TqslUploadClient.ClassifyFinalStatus(null) == TqslUploadClient.FinalStatusOutcome.Failure, true);
    }

    // ── WsjtxClient.ResolveUsState ───────────────────────────────────────────────
    // Shared priority rule for every US-state lookup site: QRZ's cached real state
    // wins whenever present; grid.dat's guess is only a last-resort fallback.
    static void ResolveUsStateTests()
    {
        Console.WriteLine("\n── WsjtxClient.ResolveUsState ──");
        Check("QRZ state wins when both present",
              WsjtxClient.ResolveUsState("CT", "MN-WI") == "CT", true);
        Check("grid fallback used when QRZ has nothing",
              WsjtxClient.ResolveUsState(null, "CT") == "CT", true);
        Check("grid fallback used when QRZ state is empty string",
              WsjtxClient.ResolveUsState("", "CT") == "CT", true);
        Check("both null -> null", WsjtxClient.ResolveUsState(null, null) == null, true);
        Check("QRZ present, grid null -> QRZ wins",
              WsjtxClient.ResolveUsState("CT", null) == "CT", true);
    }

    // ── UsGridStateMap.StateSetContains ─────────────────────────────────────────
    // Release-audit finding, 2026-08-20: award-matching set-membership checks (AwardTagger.
    // IsHrcWasNeeded/IsHrcWasUnconfirmed, AwardMatcher's RuleGroupBy.State branch) used to do a
    // plain exact-string HashSet.Contains(state) against ResolveUsState's own output, which can
    // be a compound border-straddling grid.dat value like "MN-WI" -- silently never matching a
    // set containing "MN" or "WI" individually, hiding a still-needed station from the queue.
    static void StateSetContainsTests()
    {
        Console.WriteLine("\n── UsGridStateMap.StateSetContains (compound border-state grid matching) ──");
        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MN", "OH", "TX" };

        Check("plain single-state exact match",
              UsGridStateMap.StateSetContains("MN", needed), true);
        Check("plain single-state non-match",
              UsGridStateMap.StateSetContains("CA", needed), false);
        // THE FIX: a compound value must match if EITHER component state is in the set --
        // a naive Contains("MN-WI") against this same set would have returned false.
        Check("compound 'MN-WI' matches because MN is needed -- THE FIX",
              UsGridStateMap.StateSetContains("MN-WI", needed), true);
        Check("compound 'WI-MN' (component order reversed) also matches",
              UsGridStateMap.StateSetContains("WI-MN", needed), true);
        Check("compound value matches when neither component is needed -> false",
              UsGridStateMap.StateSetContains("WI-IA", needed), false);
        Check("null state -> false", UsGridStateMap.StateSetContains(null, needed), false);
        Check("empty state -> false", UsGridStateMap.StateSetContains("", needed), false);
        Check("null set -> false", UsGridStateMap.StateSetContains("MN", null), false);
        Check("empty set -> false", UsGridStateMap.StateSetContains("MN", new HashSet<string>()), false);
    }

    // ── AdifImporter.Import: live-logged QSO state resolution ──────────────────
    // Regression guard for a live-logged QSO (LiveQsoUploadOrchestrator.ImportLiveLoggedQso,
    // fed by WsjtxClient.RequestLog) never getting a usable US state when the QSO's own
    // fields have no STATE key -- exactly the shape RequestLog builds (GRIDSQUARE only,
    // no STATE). Previously that path passed resolveUsState=null, so a QSO worked with no
    // grid square heard (e.g. a bare "CQ CALL" with no grid) left state permanently blank,
    // and that station could never satisfy a State-grouped award (the WAS family) no matter
    // how many times it was worked. The fix wires the same lookupManager-backed callback
    // every other US-state lookup in the app already uses into that one call site.
    static void AdifImporterLiveLoggedStateFallbackTests()
    {
        Console.WriteLine("\n── AdifImporter.Import: live-logged QSO state fallback ──");

        var def = new RuleDefinition
        {
            Id = "TEST_STATE_FALLBACK", Name = "Test", FormatVersion = 1, Enabled = true,
            GroupBy = RuleGroupBy.State, Target = RuleTargetType.Count, Threshold = 1,
            Confirmation = RuleConfirmation.None,
        };

        // Fields shaped exactly like WsjtxClient.RequestLog's liveFields: no STATE key,
        // GRIDSQUARE blank -- the real-world case (a station worked with no grid heard).
        Dictionary<string, string> LiveFieldsNoGrid(string call) => new Dictionary<string, string>
        {
            ["CALL"] = call, ["BAND"] = "80m", ["FREQ"] = "3.573", ["MODE"] = "FT8",
            ["QSO_DATE"] = "20260710", ["TIME_ON"] = "104200", ["TIME_OFF"] = "104300",
            ["RST_SENT"] = "-10", ["RST_RCVD"] = "-14", ["GRIDSQUARE"] = "",
            ["STATION_CALLSIGN"] = "KB0UZT", ["MY_GRIDSQUARE"] = "EN34",
        };

        string tmpDbFixed = Path.Combine(Path.GetTempPath(),
            "JimmyTest_LiveStateFallback_Fixed_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = new LogbookDb(tmpDbFixed))
            {
                AdifImporter.Import(db, new[] { LiveFieldsNoGrid("K5KPE") }, "WSJTX", null,
                    resolveUsState: call => call == "K5KPE" ? "AR" : null);
            }
            var r = RuleEngine.Evaluate(def, tmpDbFixed, null);
            Check("resolveUsState callback wired in: no-grid QSO still gets a real state",
                  r.WorkedItems != null && r.WorkedItems.Contains("AR"), true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  AdifImporterLiveLoggedStateFallbackTests (fixed) threw: {ex.GetType().Name}: {ex.Message}");
            failed++;
        }
        finally
        {
            try { File.Delete(tmpDbFixed); } catch { }
        }

        // Documents the pre-fix behavior for contrast: with no resolveUsState callback and
        // no grid, the QSO is logged but with no usable state at all (AddGroupByFilter
        // excludes blank-state rows from grouping entirely), so it can never satisfy a
        // State-grouped award regardless of how many times the station is worked.
        string tmpDbBroken = Path.Combine(Path.GetTempPath(),
            "JimmyTest_LiveStateFallback_Broken_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = new LogbookDb(tmpDbBroken))
            {
                AdifImporter.Import(db, new[] { LiveFieldsNoGrid("K5KPE") }, "WSJTX", null, null);
            }
            var r = RuleEngine.Evaluate(def, tmpDbBroken, null);
            Check("without the callback: no-grid QSO never resolves to any state",
                  r.WorkedItems == null || r.WorkedItems.Count == 0, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  AdifImporterLiveLoggedStateFallbackTests (broken) threw: {ex.GetType().Name}: {ex.Message}");
            failed++;
        }
        finally
        {
            try { File.Delete(tmpDbBroken); } catch { }
        }
    }

    // ── T12 fix, 2026-08-23 (PARTIALLY CONFIRMED -- LoTW-only DXCC/awards, reported
    // 2026-08-21): a raw import lacking DXCC/COUNTRY/CONT (real LoTW/Club Log exports
    // sometimes omit them) now backfills them from the canonical offline Club Log entity data,
    // instead of persisting dxcc=0 and being permanently invisible to DXCC-needed/worked-DXCC
    // award logic (LogbookDb.LoadHrcCache's worked/confirmed DXCC sets are filtered dxcc>0) ──
    static void AdifImporterBackfillsMissingDxccTests()
    {
        Console.WriteLine("\n── T12 fix: AdifImporter backfills missing DXCC/country/continent -- THE FIX ──");
        string tmpRoot = Path.Combine(Path.GetTempPath(), "JimmyTest_T12_ClubLog_" + Guid.NewGuid().ToString("N"));
        var prevClubLog = RuleLibrary.ClubLog;
        string tmpDb = Path.Combine(Path.GetTempPath(), "JimmyTest_T12_Db_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            // Same offline fixture shape as RuleUniverseClubLogTests -- one representative
            // prefix per entity, no network access.
            Directory.CreateDirectory(Path.Combine(tmpRoot, "ClubLog"));
            string xml =
                "<clublog><entities>" +
                "<ENTITY><adif>291</adif><name>UNITED STATES OF AMERICA</name><prefix>K</prefix><deleted>FALSE</deleted><cqz>5</cqz><cont>NA</cont></ENTITY>" +
                "</entities></clublog>";
            File.WriteAllText(Path.Combine(tmpRoot, "ClubLog", "clublog_cty.xml"), xml);
            var provider = new ClubLogProvider(tmpRoot);
            provider.Configure(true, "");
            provider.Load();
            RuleLibrary.ClubLog = provider;

            // Raw fields shaped like a real LoTW-only export missing DXCC/COUNTRY/CONT entirely
            // -- confirmed QSL, but no entity data of its own.
            var lotwFieldsNoDxcc = new Dictionary<string, string>
            {
                ["CALL"] = "K9ABC", ["BAND"] = "20m", ["FREQ"] = "14.074", ["MODE"] = "FT8",
                ["QSO_DATE"] = "20260710", ["TIME_ON"] = "104200",
                ["QSL_RCVD"] = "Y", // LoTW confirmation flag
                ["STATION_CALLSIGN"] = "KB0UZT", ["MY_GRIDSQUARE"] = "EN34",
            };

            using (var db = new LogbookDb(tmpDb))
            {
                var result = AdifImporter.Import(db, new[] { lotwFieldsNoDxcc }, "LOTW");
                Check("Import reports the record processed with no errors", result.Errors == "" && result.NewQsos == 1, true);

                var rows = db.SearchQsos("K9ABC", null, null, null);
                Check("THE FIX: the imported row's DXCC is backfilled (291, not left at 0)",
                    rows.Count == 1 && rows[0].Dxcc == 291, true);
                Check("THE FIX: country is backfilled",
                    rows.Count == 1 && rows[0].Country == "UNITED STATES OF AMERICA", true);
                Check("LoTW confirmation flag is preserved independently -- service-neutral, not LoTW-blocked",
                    rows.Count == 1 && rows[0].LotwQslRcvd == "Y", true);
            }

            // Continent isn't exposed via SearchQsos/QsoRecord -- verified indirectly through a
            // Continent-grouped rule (evaluated against its own separate connection, after the
            // import connection above has closed, matching AdifImporterLiveLoggedStateFallback
            // Tests' own established pattern for verifying a backfilled field this way).
            var continentRule = new RuleDefinition
            {
                Id = "TEST_T12_CONTINENT", Name = "Test", FormatVersion = 1, Enabled = true,
                GroupBy = RuleGroupBy.Continent, Target = RuleTargetType.Count, Threshold = 1,
                Confirmation = RuleConfirmation.None,
            };
            var continentResult = RuleEngine.Evaluate(continentRule, tmpDb, null);
            Check("THE FIX: continent is backfilled (NA)",
                continentResult.WorkedItems != null && continentResult.WorkedItems.Contains("NA"), true);

            // A record that already carries real DXCC/country/continent data must not be
            // overridden by the offline resolver -- backfill only fills genuinely missing fields.
            var withRealDxcc = new Dictionary<string, string>
            {
                ["CALL"] = "K9XYZ", ["BAND"] = "20m", ["FREQ"] = "14.074", ["MODE"] = "FT8",
                ["QSO_DATE"] = "20260711", ["TIME_ON"] = "104200",
                ["DXCC"] = "6", ["COUNTRY"] = "ALASKA", ["CONT"] = "NA",
                ["STATION_CALLSIGN"] = "KB0UZT", ["MY_GRIDSQUARE"] = "EN34",
            };
            string tmpDb2 = Path.Combine(Path.GetTempPath(), "JimmyTest_T12_Db2_" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using (var db2 = new LogbookDb(tmpDb2))
                {
                    AdifImporter.Import(db2, new[] { withRealDxcc }, "LOTW");
                    var rows2 = db2.SearchQsos("K9XYZ", null, null, null);
                    Check("A real source-supplied DXCC (6, Alaska) is never overridden by the K->291 fallback",
                        rows2.Count == 1 && rows2[0].Dxcc == 6, true);
                }
            }
            finally { try { File.Delete(tmpDb2); } catch { } }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  AdifImporterBackfillsMissingDxccTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            RuleLibrary.ClubLog = prevClubLog;
            try { Directory.Delete(tmpRoot, true); } catch { }
            try { File.Delete(tmpDb); } catch { }
        }
    }

    // ── Independent audit finding 3, 2026-08-23 (CONFIRMED bug): AdifImporter's ImportResult
    // contract that BOTH LogbookWindow.RunImportFromText and LogbookAutoSync.ImportAndReport's
    // checkpoint-write gating depend on -- valid records are retained even when ANOTHER record
    // in the same batch genuinely errors, and Errors is populated exactly when a real per-record
    // failure occurred (the condition each call site's own `if (... && string.IsNullOrWhiteSpace
    // (result.Errors))` checkpoint guard now uses), not for an ordinary benign skip (a record
    // Normalize() itself declines, e.g. missing QSO_DATE -- counted in Skipped, never Errors).
    // Forces a real per-record exception via a throwing resolveUsState callback for one specific
    // call (Normalize's own try/catch scope in AdifImporter.Import wraps that call) -- a clean,
    // self-contained way to exercise the catch block without reaching into SQLite internals.
    static void AdifImportMixedValidErrorRetainsValidRowsTests()
    {
        Console.WriteLine("\n── Finding 3: mixed valid/error import retains valid rows, reports Errors truthfully -- THE FIX ──");
        string tmpDb = Path.Combine(Path.GetTempPath(), "JimmyTest_MixedImport_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            Dictionary<string, string> Rec(string call) => new Dictionary<string, string>
            {
                ["CALL"] = call, ["BAND"] = "20m", ["FREQ"] = "14.074", ["MODE"] = "FT8",
                ["QSO_DATE"] = "20260710", ["TIME_ON"] = "104200",
                ["STATION_CALLSIGN"] = "KB0UZT", ["MY_GRIDSQUARE"] = "EN34",
            };
            var benignSkip = new Dictionary<string, string> { ["CALL"] = "K9SKIP" }; // no QSO_DATE -- Normalize() returns null

            using (var db = new LogbookDb(tmpDb))
            {
                var records = new[] { Rec("K9VALID1"), Rec("K9BOOM"), Rec("K9VALID2"), benignSkip };
                var result = AdifImporter.Import(db, records, "QRZ", null,
                    resolveUsState: call => call == "K9BOOM" ? throw new InvalidOperationException("simulated per-record failure") : null);

                Check("THE FIX: a genuine per-record error is reported in Errors (checkpoint-gating condition would NOT advance)",
                    !string.IsNullOrWhiteSpace(result.Errors), true);
                Check("Both OTHER valid records still committed despite the one error", result.NewQsos == 2, true);
                // Skipped counts BOTH the benign skip (Normalize() declining a record with no
                // QSO_DATE) AND the genuine per-record error (Import's own catch block also
                // increments Skipped alongside Errors) -- 2 total, not a bug, just Skipped's own
                // established "did not land in the DB" meaning rather than a pure benign-only tally.
                Check("Skipped totals both the benign skip and the erroring record",
                    result.Skipped == 2, true);
                Check("...total Processed accounts for all 4 records", result.Processed == 4, true);

                Check("K9VALID1 actually landed in the DB", db.SearchQsos("K9VALID1", null, null, null).Count == 1, true);
                Check("K9VALID2 actually landed in the DB", db.SearchQsos("K9VALID2", null, null, null).Count == 1, true);
                Check("K9BOOM (the erroring record) did NOT land in the DB", db.SearchQsos("K9BOOM", null, null, null).Count == 0, true);
            }

            // Contrast: an all-valid batch must report Errors == "" (checkpoint-gating condition
            // WOULD advance) -- the positive control for the assertions above.
            string tmpDb2 = Path.Combine(Path.GetTempPath(), "JimmyTest_MixedImport2_" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using (var db2 = new LogbookDb(tmpDb2))
                {
                    var cleanResult = AdifImporter.Import(db2, new[] { Rec("K9CLEAN") }, "QRZ");
                    Check("All-valid batch: Errors is empty", string.IsNullOrWhiteSpace(cleanResult.Errors), true);
                }
            }
            finally { try { File.Delete(tmpDb2); } catch { } }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  AdifImportMixedValidErrorRetainsValidRowsTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            try { File.Delete(tmpDb); } catch { }
        }
    }

    // ── DxSpotWatcher.IsEvenPeriod ───────────────────────────────────────────────
    static void DxSpotWatcherIsEvenPeriodTests()
    {
        Console.WriteLine("\n── DxSpotWatcher.IsEvenPeriod ──");
        var baseDay = new DateTime(2026, 7, 8, 0, 0, 0, DateTimeKind.Utc);
        Check("FT8 :00 is even", DxSpotWatcher.IsEvenPeriod(baseDay.AddSeconds(0), "FT8"), true);
        Check("FT8 :15 is odd",  DxSpotWatcher.IsEvenPeriod(baseDay.AddSeconds(15), "FT8"), false);
        Check("FT8 :30 is even", DxSpotWatcher.IsEvenPeriod(baseDay.AddSeconds(30), "FT8"), true);
        Check("FT8 :45 is odd",  DxSpotWatcher.IsEvenPeriod(baseDay.AddSeconds(45), "FT8"), false);
        Check("FT8 mode is case-insensitive", DxSpotWatcher.IsEvenPeriod(baseDay.AddSeconds(0), "ft8"), true);
        Check("FT4 :03 (within first even window) is even",
              DxSpotWatcher.IsEvenPeriod(baseDay.AddSeconds(3), "FT4"), true);
        Check("FT4 :10 (odd window) is odd",
              DxSpotWatcher.IsEvenPeriod(baseDay.AddSeconds(10), "FT4"), false);
        Check("FT4 :37 (odd window) is odd",
              DxSpotWatcher.IsEvenPeriod(baseDay.AddSeconds(37), "FT4"), false);
    }

    // ── FccUlsProvider.ParseLine ─────────────────────────────────────────────────
    // Uses REAL sample rows copied verbatim from an actual downloaded EN.dat
    // (2026-07-08), not synthetic data -- confirms the empirically-verified field
    // positions (callsign index 4, state index 17, unique_system_identifier index
    // 1, first/mi/last name indices 8/9/10) still parse correctly, and that dedup
    // keeps the highest-uid row per callsign (the current holder) rather than an
    // old/reissued one.
    static void FccUlsProviderParseLineTests()
    {
        Console.WriteLine("\n── FccUlsProvider.ParseLine ──");

        // W1AW = ARRL HQ, Newington, CT -- a stable, well-known real-world answer.
        // Club licenses leave first/mi/last name all blank -- Name must be null,
        // not an empty string pieced together from blanks.
        const string w1aw = "EN|780866|||W1AW|L|L00306106|ARRL HQ OPERATORS CLUB||||||||225 MAIN ST|NEWINGTON|CT|06111|| David A Minster|000|0004511143|B||||||";
        var d1 = new Dictionary<string, (long Uid, string State, string Name)>(StringComparer.OrdinalIgnoreCase);
        FccUlsProvider.ParseLine(w1aw, d1);
        Check("W1AW parses to CT", d1.TryGetValue("W1AW", out var w1awEntry) && w1awEntry.State == "CT", true);
        Check("W1AW (club license) has no personal name", w1awEntry.Name == null, true);

        // AA0A: two real rows for the same callsign -- an older license (McCarthy,
        // MO, lower uid, with a middle initial) and the current one (Rosebrook, SD,
        // higher uid, no middle initial). Confirmed duplicate pair copied verbatim
        // from a real downloaded file.
        const string aa0aOld = "EN|215000|||AA0A|L|L00209566|MC CARTHY, DENNIS J|DENNIS|J|MC CARTHY|||||6438 Bishops Pl|SAINT LOUIS|MO|631093371|||000|0002274249|I||||||";
        const string aa0aNew = "EN|4280373|||AA0A|L|L02306961|Rosebrook, John|John||Rosebrook|||||3916 N. Potsdam Ave. #4555|Sioux Falls|SD|57104|||000|0028942159|I||||||";

        // The old row alone (not shadowed by the higher-uid new row) exercises the
        // middle-initial-present combining path.
        var dOldAlone = new Dictionary<string, (long Uid, string State, string Name)>(StringComparer.OrdinalIgnoreCase);
        FccUlsProvider.ParseLine(aa0aOld, dOldAlone);
        Check("AA0A old row: name combines first + MI + last",
              dOldAlone.TryGetValue("AA0A", out var aa0aOldEntry) && aa0aOldEntry.Name == "DENNIS J MC CARTHY", true);

        // Order 1: old row first, then new -- higher uid must win.
        var d2 = new Dictionary<string, (long Uid, string State, string Name)>(StringComparer.OrdinalIgnoreCase);
        FccUlsProvider.ParseLine(aa0aOld, d2);
        FccUlsProvider.ParseLine(aa0aNew, d2);
        Check("AA0A (old-then-new order): higher uid (SD) wins",
              d2.TryGetValue("AA0A", out var aa0aEntry1) && aa0aEntry1.State == "SD", true);
        Check("AA0A (old-then-new order): current holder's name (no MI)",
              aa0aEntry1.Name == "John Rosebrook", true);

        // Order 2: new row first, then old -- must NOT regress back to the old one.
        var d3 = new Dictionary<string, (long Uid, string State, string Name)>(StringComparer.OrdinalIgnoreCase);
        FccUlsProvider.ParseLine(aa0aNew, d3);
        FccUlsProvider.ParseLine(aa0aOld, d3);
        Check("AA0A (new-then-old order): higher uid (SD) still wins",
              d3.TryGetValue("AA0A", out var aa0aEntry2) && aa0aEntry2.State == "SD", true);
        Check("AA0A (new-then-old order): current holder's name (no MI)",
              aa0aEntry2.Name == "John Rosebrook", true);

        // Malformed/irrelevant input must be skipped, not throw or add junk.
        var d4 = new Dictionary<string, (long Uid, string State, string Name)>(StringComparer.OrdinalIgnoreCase);
        FccUlsProvider.ParseLine("HD|780866|||W1AW|A|||||", d4);
        Check("non-EN record type is skipped", d4.Count == 0, true);
        FccUlsProvider.ParseLine("EN|123|||W9ZZZ|L|", d4);
        Check("too-few-fields line is skipped", d4.Count == 0, true);
        FccUlsProvider.ParseLine("", d4);
        Check("empty line is skipped, does not throw", d4.Count == 0, true);
    }

    // ── FccUlsProvider.ShouldPreferName ──────────────────────────────────────────
    // Real-world case that motivated this: QRZ's "fname" field sometimes already
    // jams a first name + middle initial together ("RICHARD L") with the separate
    // last-name field left blank, so QRZ's own combined name has only 2 words even
    // though a full name is available -- FCC's fuller 3-word record must still win.
    static void FccUlsProviderShouldPreferNameTests()
    {
        Console.WriteLine("\n── FccUlsProvider.ShouldPreferName ──");
        Check("no existing name -> FCC's name is preferred",
              FccUlsProvider.ShouldPreferName("John Rosebrook", null), true);
        Check("FCC name more complete (3 words) than existing (2 words) -> preferred",
              FccUlsProvider.ShouldPreferName("RICHARD L DILLON", "RICHARD L"), true);
        Check("FCC name same word count as existing -> not preferred (existing kept)",
              FccUlsProvider.ShouldPreferName("John Rosebrook", "Johnny Rosebrook"), false);
        Check("FCC name fewer words than existing -> not preferred",
              FccUlsProvider.ShouldPreferName("John", "John Q Rosebrook"), false);
        Check("no FCC name -> never preferred, regardless of existing",
              FccUlsProvider.ShouldPreferName(null, "RICHARD L"), false);
        Check("no FCC name and no existing name -> not preferred",
              FccUlsProvider.ShouldPreferName(null, null), false);
    }

    // ── FccUlsProvider.LooksIncomplete ───────────────────────────────────────────
    // Guards against a technically-valid-but-truncated download (e.g. FCC's
    // server caught mid-regeneration of the weekly file) silently replacing good
    // data with a partial file.
    static void FccUlsProviderLooksIncompleteTests()
    {
        Console.WriteLine("\n── FccUlsProvider.LooksIncomplete ──");
        Check("first-ever download, plausible count -> accepted",
              FccUlsProvider.LooksIncomplete(1_580_000, 0), false);
        Check("first-ever download, implausibly low count -> rejected",
              FccUlsProvider.LooksIncomplete(1000, 0), true);
        Check("first-ever download, right at the floor -> accepted",
              FccUlsProvider.LooksIncomplete(FccUlsProvider.MinPlausibleRecordCount, 0), false);
        Check("subsequent refresh, similar count -> accepted",
              FccUlsProvider.LooksIncomplete(1_580_000, 1_575_000), false);
        Check("subsequent refresh, slightly lower (normal churn) -> accepted",
              FccUlsProvider.LooksIncomplete(1_570_000, 1_580_000), false);
        Check("subsequent refresh, sharply lower (truncated download) -> rejected",
              FccUlsProvider.LooksIncomplete(400_000, 1_580_000), true);
    }

    // Insert a minimal QSO record into a test LogbookDb.
    // Each callsign produces a unique dedup key — no counter needed.
    // band/qsoDate/continent are optional so existing calls (fixed 20m,
    // 2024-12-01, no continent) keep working unchanged.
    static void InsertQso(LogbookDb db, string call, string state,
        int dxcc, int zone, string lotwRcvd = "", string qrzRcvd = "",
        string band = "20m", string qsoDate = "20241201", string continent = "",
        string timeOn = "1200")
    {
        string key = AdifImporter.BuildDedupKey(call, band, "FT8", qsoDate, timeOn);
        // Parameter order: ..., lotwQslSent, lotwQslRcvd, qrzQslSent, qrzQslRcvd, ...
        db.Upsert(call, band, "FT8", qsoDate, timeOn, "1215",
            14_074_000, "-10", "-05", state, "Test", dxcc, zone,
            "", "", "", "", "", "", "",
            "", lotwRcvd, "", qrzRcvd,
            "MANUAL", "", key,
            continent, 0, "", "", "", "", "", "", "", "", "", "");
    }

    // Verify that each AP-suffixed message, once stripped, classifies correctly.
    //
    // IsInvalidType does NOT cover contest/FD exchanges — they are handled by a
    // separate isContest branch in ProcessDecodeMsg before IsInvalidType is
    // checked.  So IsInvalidType("KB0UZT K4YT 2A MO") == true is intentional.
    static void ApChainTests()
    {
        Console.WriteLine("\n── AP Suffix: strip then classify ──");

        // Cases 0-2: non-contest messages — IsInvalidType=false after strip
        string[] nonContest = {
            $"{MY_CALL} {THEIR_CALL} -05 a35",   // signal report
            $"CQ {THEIR_CALL} EM63 a1",           // plain CQ
            $"{MY_CALL} {THEIR_CALL} EM63 a35",  // grid reply
        };
        bool[] expectReport = { true,  false, false };
        bool[] expectCq     = { false, true,  false };
        bool[] expectReply  = { false, false, true  };

        for (int n = 0; n < nonContest.Length; n++)
        {
            string s = StripApSuffix(nonContest[n]);
            Check($"case {n}: IsInvalidType=false after strip",  WsjtxMessage.IsInvalidType(s), false);
            Check($"case {n}: IsContest=false after strip",      WsjtxMessage.IsContest(s),     false);
            Check($"case {n}: IsReport",   WsjtxMessage.IsReport(s), expectReport[n]);
            Check($"case {n}: IsCQ",       WsjtxMessage.IsCQ(s),     expectCq[n]);
            Check($"case {n}: IsReply",    WsjtxMessage.IsReply(s),   expectReply[n]);
        }

        // Regression: short AP suffix " a2" on a report must not survive as contest tokens.
        // Scenario: WSJT-X sends "KB0UZT K4YT -04                      a2" (old RC format).
        // After strip → "KB0UZT K4YT -04".  IsContest must be False; IsReport must be True.
        string f4dwb = StripApSuffix($"{MY_CALL} THEIR -04                      a2");
        CheckStr("F4DWB-style: stripped correctly",   f4dwb, $"{MY_CALL} THEIR -04");
        Check("F4DWB-style: IsContest=False after strip", WsjtxMessage.IsContest(f4dwb), false);
        Check("F4DWB-style: IsReport=True after strip",   WsjtxMessage.IsReport(f4dwb),  true);

        // Case 3: FD/contest exchange — IsContest=true; IsInvalidType=true by design
        // (contest messages are routed via the isContest branch, not the normal path)
        string fd = StripApSuffix($"{MY_CALL} {THEIR_CALL} 2A MO a35");
        CheckStr("case 3: stripped FD exchange",  fd, $"{MY_CALL} {THEIR_CALL} 2A MO");
        Check("case 3: IsContest=true",           WsjtxMessage.IsContest(fd), true);
        Check("case 3: IsInvalidType=true (FD routes via contest branch, not normal path)",
                                                  WsjtxMessage.IsInvalidType(fd), true);
    }

    // ── TEMPORARY one-off verification, not part of the regular suite ─────────
    // Compares the new Club Log-backed / built-in universes against the existing
    // companion-file approach using REAL downloaded Club Log data (not a
    // fixture). Requires network access, so it's gated behind --verify-clublog
    // and is not called from Main()'s normal run. Delete after use.
    // Persistent (not per-run) cache dir so repeat runs of this verification
    // reuse the downloaded country file instead of re-downloading every time.
    static readonly string ClubLogVerifyCacheDir =
        Path.Combine(Path.GetTempPath(), "JimmyVerify_ClubLog_Cache");

    static void VerifyClubLogEquivalence()
    {
        string listsFolder = @"C:\claude\Jimmy\WSJTX_Controller\bin\Debug\RuleDefinitions\Lists";
        Directory.CreateDirectory(ClubLogVerifyCacheDir);

        // CA_PROVINCES needs no Club Log data at all.
        Console.WriteLine("=== CA_PROVINCES vs rac_provinces.txt ===");
        CompareUniverses("CA_PROVINCES", "File:rac_provinces.txt", listsFolder, null);
        Console.WriteLine();

        // Real key lives in a private file outside the repo, never an env var
        // or a build artifact -- read line 9 directly (same convention as
        // Jimmy.csproj's ClubLogKeyFile/ClubLogKeyLineNumber properties).
        string keyFilePath = @"C:\Users\Jim\Dropbox\amateur radio\Keys_private\Club Log API key for Jimmy.txt";
        string key = "";
        if (File.Exists(keyFilePath))
        {
            var lines = File.ReadAllLines(keyFilePath);
            if (lines.Length >= 9) key = (lines[8] ?? "").Trim();
        }
        if (string.IsNullOrEmpty(key))
        {
            Console.WriteLine($"Could not read Club Log key from line 9 of {keyFilePath} -- DXCC_* comparisons skipped.");
            return;
        }

        var provider = new ClubLogProvider(ClubLogVerifyCacheDir);
        provider.Configure(true, key);
        provider.Load();   // reuse cached clublog_cty.xml if one already exists
        if (provider.EntityCount == 0)
        {
            Console.WriteLine("No cached Club Log data yet -- downloading once...");
            bool ok = provider.RefreshAsync().GetAwaiter().GetResult();
            Console.WriteLine($"  RefreshAsync() returned: {ok}, EntityCount={provider.EntityCount}, LastError={provider.LastError}");
            if (!ok || provider.EntityCount == 0)
            {
                Console.WriteLine("Could not download real Club Log data -- DXCC_* comparisons skipped.");
                return;
            }
        }
        else
        {
            Console.WriteLine($"Reusing cached Club Log data: EntityCount={provider.EntityCount}, LastUpdate={provider.LastUpdate}");
        }

        Console.WriteLine();
        Console.WriteLine("=== DXCC_NORTH_AMERICA vs na_dxcc_entities.txt (as LimitTo) ===");
        CompareUniverses("DXCC_NORTH_AMERICA", "File:na_dxcc_entities.txt", listsFolder, provider);

        Console.WriteLine();
        Console.WriteLine("=== Other Club Log-backed universes (no companion-file counterpart to compare) ===");
        foreach (var token in new[] { "DXCC_CURRENT", "DXCC_DELETED", "DXCC_SOUTH_AMERICA", "DXCC_EUROPE", "DXCC_AFRICA", "DXCC_ASIA", "DXCC_OCEANIA" })
        {
            string err;
            var set = RuleUniverse.Resolve(token, listsFolder, provider, out err);
            Console.WriteLine(set == null
                ? $"  {token}: ERROR {err}"
                : $"  {token}: {set.Count} entities");
        }

        // Big CTY alias/exception data (https://www.country-files.com/bigcty/cty.dat) --
        // sanity-check the real fix for the KG4JOK bug report against the actual current
        // file, not just the synthetic fixture in ClubLogBigCtyResolutionTests.
        Console.WriteLine();
        Console.WriteLine("=== Big CTY alias/exception data (real download) ===");
        if (provider.BigCtyAliasCount == 0)
        {
            Console.WriteLine("No cached Big CTY data yet -- downloading once...");
            bool ok = provider.RefreshBigCtyAsync().GetAwaiter().GetResult();
            Console.WriteLine($"  RefreshBigCtyAsync() returned: {ok}, AliasCount={provider.BigCtyAliasCount}, LastError={provider.LastError}");
            if (!ok || provider.BigCtyAliasCount == 0)
            {
                Console.WriteLine("Could not download real Big CTY data -- KG4 resolution checks skipped.");
                return;
            }
        }
        else
        {
            Console.WriteLine($"Reusing cached Big CTY data: AliasCount={provider.BigCtyAliasCount}, LastUpdate={provider.BigCtyLastUpdate}");
        }

        // KG4JOK: the real callsign from the bug report -- an ordinary 3-letter-
        // suffix USA amateur, not yet in AD1C's exception list (confirmed by direct
        // inspection when this fix was written). Must resolve to USA (291), not
        // Guantanamo Bay (105), via the KG4 length rule.
        var kg4jok = provider.FindByCallsign("KG4JOK");
        Console.WriteLine(kg4jok == null
            ? "  KG4JOK: FAIL -- did not resolve at all"
            : $"  KG4JOK: Adif={kg4jok.Adif} ({kg4jok.Name}) -- {(kg4jok.Adif == 291 ? "PASS" : "FAIL, expected 291 (USA)")}");

        // KG44WW: a real, currently-catalogued Guantanamo Bay exception (3-letter-
        // style suffix "4WW", so the KG4 length rule alone would get this one
        // wrong -- must resolve via the exception, not the rule). Note: an earlier
        // draft of this check used "KG4NEX", which real historic Club Log data
        // marks as Guantanamo only through 1971 -- AD1C's current file has since
        // reassigned it to USA, so it no longer exercises this path; verified by
        // inspecting the live download directly when this was written.
        var kg44ww = provider.FindByCallsign("KG44WW");
        Console.WriteLine(kg44ww == null
            ? "  KG44WW: FAIL -- did not resolve at all"
            : $"  KG44WW: Adif={kg44ww.Adif} ({kg44ww.Name}) -- {(kg44ww.Adif == 105 ? "PASS" : "FAIL, expected 105 (Guantanamo Bay)")}");
    }

    // DXCC shadow comparison (development pass, see ARCHITECTURE.md): dumps Jimmy's own
    // ClubLogProvider.FindByCallsign() output, in the same pipe-delimited format as
    // EngineHost/tests/dxcc_shadow_dump.rs's dump of Nexus's propagation::dxcc::resolve(),
    // for the SAME fixed callsign list -- so the two can be diffed directly. Reuses whatever
    // is already cached under LookupManager.DataRoot\ClubLog (real data from normal Jimmy
    // Test usage on this machine) rather than requiring a fresh download/API key. Not called
    // from Main()'s normal run; invoked via --dxcc-shadow-dump.
    static void DxccShadowDump()
    {
        var provider = new ClubLogProvider(LookupManager.DataRoot);
        provider.Configure(true, "");
        provider.Load();
        if (provider.EntityCount == 0)
        {
            Console.WriteLine($"No cached Club Log data under {LookupManager.DataRoot}\\ClubLog -- run Jimmy Next normally first (Options > Awards/Lookup) so it downloads once, then re-run this.");
            return;
        }

        // Must match EngineHost/tests/dxcc_shadow_dump.rs's CALLS list exactly, same order.
        string[] calls =
        {
            "W1AW", "K9ABC", "N4XYZ", "AA1AA",
            "KG4AB", "KG4XYZ", "KG4JOK",
            "K4YT", "K4ABC",
            "NP4TX", "KP4AA",
            "KH6XX", "KL7AA",
            "G3ABC", "JA1ABC", "VK2ABC", "ZS6ABC", "PY2ABC", "9V1ABC", "4X1ABC",
            "VE3ABC", "VE7ABC",
            "W1AW/P", "W1AW/MM", "DL/W1AW",
            "3Y0J",
            "K1ABC/H", "W5HRC", "G3HRC", "K5SNL", "PY5SNL", "K3ZK",
            "ZZZZZ99",
        };

        Console.WriteLine($"ClubLogProvider: EntityCount={provider.EntityCount}, LastUpdate={provider.LastUpdate}, BigCtyAliasCount={provider.BigCtyAliasCount}, BigCtyLastUpdate={provider.BigCtyLastUpdate}");
        Console.WriteLine("CALL|ENTITY|CONT|CQ_ZONE|ADIF");
        foreach (var call in calls)
        {
            var e = provider.FindByCallsign(call);
            Console.WriteLine(e == null
                ? $"{call}|<NONE>|||"
                : $"{call}|{e.Name}|{e.Continent}|{e.CqZone}|{e.Adif}");
        }
    }

    static void CompareUniverses(string newToken, string oldToken, string listsFolder, ClubLogProvider clubLog)
    {
        string err1, err2;
        var a = RuleUniverse.Resolve(newToken, listsFolder, clubLog, out err1);
        var b = RuleUniverse.Resolve(oldToken, listsFolder, clubLog, out err2);

        if (a == null) { Console.WriteLine($"  {newToken}: ERROR {err1}"); return; }
        if (b == null) { Console.WriteLine($"  {oldToken}: ERROR {err2}"); return; }

        var onlyInNew = a.Except(b, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var onlyInOld = b.Except(a, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();

        Console.WriteLine($"  {newToken}: {a.Count} entries   {oldToken}: {b.Count} entries");
        if (onlyInNew.Count == 0 && onlyInOld.Count == 0)
        {
            Console.WriteLine("  IDENTICAL");
        }
        else
        {
            Console.WriteLine("  DIFFERENT");
            if (onlyInNew.Count > 0) Console.WriteLine($"    Only in {newToken}: {string.Join(", ", onlyInNew)}");
            if (onlyInOld.Count > 0) Console.WriteLine($"    Only in {oldToken}: {string.Join(", ", onlyInOld)}");
        }
    }

    // ── RuleUniverse built-ins ────────────────────────────────────────────────
    // CA_PROVINCES and the AN (Antarctica) continent code, added to replace the
    // rac_provinces.txt companion file and fill a gap in the ADIF continent set.
    static void RuleUniverseBuiltInTests()
    {
        Console.WriteLine("\n── RuleUniverse: Built-in Universes ──");

        string err;
        var caProvinces = RuleUniverse.Resolve("CA_PROVINCES", "", null, out err);
        CheckStr("CA_PROVINCES: no error", err, null);
        Check("CA_PROVINCES: count == 13", caProvinces != null && caProvinces.Count == 13, true);
        foreach (var p in new[] { "AB", "BC", "MB", "NB", "NL", "NS", "NT", "NU", "ON", "PE", "QC", "SK", "YT" })
            Check($"CA_PROVINCES: contains {p}", caProvinces != null && caProvinces.Contains(p), true);

        Check("Continents: includes AN (Antarctica)", RuleUniverse.Continents.Contains("AN"), true);
        Check("Continents: has 7 entries",             RuleUniverse.Continents.Length == 7,   true);
    }

    // ── RuleUniverse Club Log-backed universes ─────────────────────────────────
    // DXCC_CURRENT / DXCC_DELETED / continent-filtered DXCC universes, resolved
    // from a fixture ClubLogProvider (no network access -- Load() reads a local
    // XML file, same mechanism ClubLogProvider uses for its real cache).
    static void RuleUniverseClubLogTests()
    {
        Console.WriteLine("\n── RuleUniverse: Club Log-backed Universes ──");

        // Unavailable case: no provider at all (e.g. a caller that never wired one up).
        string unavailErr;
        var noProvider = RuleUniverse.Resolve("DXCC_CURRENT", "", null, out unavailErr);
        Check("DXCC_CURRENT: null provider -> unresolved", noProvider == null, true);
        Check("DXCC_CURRENT: null provider -> has error",  !string.IsNullOrEmpty(unavailErr), true);

        string tmpRoot = Path.Combine(Path.GetTempPath(),
            "JimmyTest_ClubLog_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(tmpRoot, "ClubLog"));
            // Entity node name must be upper-case ENTITY: ClubLogProvider.ParseXml
            // looks for "ENTITY" first and only falls back to "entity" if that
            // XPath query returns null, but SelectNodes returns an empty (non-null)
            // list rather than null when nothing matches -- so lower-case entity
            // elements would silently parse as zero entities.
            string xml =
                "<clublog><entities>" +
                "<ENTITY><adif>6</adif><name>ALASKA</name><prefix>KL</prefix><deleted>FALSE</deleted><cqz>1</cqz><cont>NA</cont></ENTITY>" +
                "<ENTITY><adif>291</adif><name>UNITED STATES OF AMERICA</name><prefix>K</prefix><deleted>FALSE</deleted><cqz>5</cqz><cont>NA</cont></ENTITY>" +
                "<ENTITY><adif>100</adif><name>ARGENTINA</name><prefix>LU</prefix><deleted>FALSE</deleted><cqz>13</cqz><cont>SA</cont></ENTITY>" +
                "<ENTITY><adif>999</adif><name>FICTIONAL DELETED ENTITY</name><prefix>ZZ</prefix><deleted>TRUE</deleted><cqz>1</cqz><cont>NA</cont></ENTITY>" +
                "</entities></clublog>";
            File.WriteAllText(Path.Combine(tmpRoot, "ClubLog", "clublog_cty.xml"), xml);

            var provider = new ClubLogProvider(tmpRoot);
            provider.Configure(true, "");
            provider.Load();
            Check("Fixture: 4 entities loaded", provider.EntityCount == 4, true);

            string err;
            var current = RuleUniverse.Resolve("DXCC_CURRENT", "", provider, out err);
            Check("DXCC_CURRENT: resolved",            current != null,                     true);
            Check("DXCC_CURRENT: count == 3",           current != null && current.Count == 3, true);
            Check("DXCC_CURRENT: includes 6 (Alaska)",   current != null && current.Contains("6"),   true);
            Check("DXCC_CURRENT: includes 291 (USA)",    current != null && current.Contains("291"), true);
            Check("DXCC_CURRENT: includes 100 (Argentina)", current != null && current.Contains("100"), true);
            Check("DXCC_CURRENT: excludes deleted 999",  current != null && !current.Contains("999"), true);

            var deleted = RuleUniverse.Resolve("DXCC_DELETED", "", provider, out err);
            Check("DXCC_DELETED: count == 1",     deleted != null && deleted.Count == 1,     true);
            Check("DXCC_DELETED: contains 999",   deleted != null && deleted.Contains("999"), true);

            var na = RuleUniverse.Resolve("DXCC_NORTH_AMERICA", "", provider, out err);
            Check("DXCC_NORTH_AMERICA: count == 2",          na != null && na.Count == 2,          true);
            Check("DXCC_NORTH_AMERICA: includes 6",          na != null && na.Contains("6"),        true);
            Check("DXCC_NORTH_AMERICA: includes 291",        na != null && na.Contains("291"),      true);
            Check("DXCC_NORTH_AMERICA: excludes deleted 999", na != null && !na.Contains("999"),     true);

            var sa = RuleUniverse.Resolve("DXCC_SOUTH_AMERICA", "", provider, out err);
            Check("DXCC_SOUTH_AMERICA: count == 1",   sa != null && sa.Count == 1,        true);
            Check("DXCC_SOUTH_AMERICA: contains 100", sa != null && sa.Contains("100"),   true);

            var eu = RuleUniverse.Resolve("DXCC_EUROPE", "", provider, out err);
            Check("DXCC_EUROPE: count == 0 (none in fixture)", eu != null && eu.Count == 0, true);
        }
        finally
        {
            try { Directory.Delete(tmpRoot, true); } catch { }
        }
    }

    // ── ClubLogProvider: Big CTY alias/exception resolution ─────────────────────
    // Regression coverage for the KG4JOK bug report (Jimmy_1.90.7_support_
    // 20260731_220224.zip): FindByCallsign historically matched only ClubLog's own
    // clublog_cty.xml, which stores exactly one representative prefix per entity
    // (USA = "K"), so a plain "W"/"N" call never resolved at all, and "KG4"-prefix
    // calls always matched Guantanamo Bay (which also claims "KG4") regardless of
    // suffix length/format. Both fixture files are written directly to disk (no
    // network -- same mechanism as RuleUniverseClubLogTests above) so
    // ClubLogProvider.Load() picks them up exactly like its real on-disk cache.
    static void ClubLogBigCtyResolutionTests()
    {
        Console.WriteLine("\n── ClubLogProvider: Big CTY Alias/Exception Resolution ──");

        string tmpRoot = Path.Combine(Path.GetTempPath(),
            "JimmyTest_BigCty_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(tmpRoot, "ClubLog"));

            string xml =
                "<clublog><entities>" +
                "<ENTITY><adif>291</adif><name>UNITED STATES OF AMERICA</name><prefix>K</prefix><deleted>FALSE</deleted><cqz>5</cqz><cont>NA</cont></ENTITY>" +
                "<ENTITY><adif>105</adif><name>GUANTANAMO BAY</name><prefix>KG4</prefix><deleted>FALSE</deleted><cqz>8</cqz><cont>NA</cont></ENTITY>" +
                "<ENTITY><adif>100</adif><name>ARGENTINA</name><prefix>LU</prefix><deleted>FALSE</deleted><cqz>13</cqz><cont>SA</cont></ENTITY>" +
                "</entities></clublog>";
            File.WriteAllText(Path.Combine(tmpRoot, "ClubLog", "clublog_cty.xml"), xml);

            // Real Big CTY format (header: Name: cqz: ituz: cont: lat: long: tz: mainPrefix:).
            // "K0(4)" gives the USA entry a per-callarea zone override to exercise the
            // shallow-copy path; "=KG4NEX" is a 3-letter-suffix exception deliberately
            // routed to Guantanamo Bay to prove exceptions outrank the KG4 length rule.
            string bigCty =
                "United States:             05:  08:  NA:   37.60:    91.87:     5.0:  K:\n" +
                "    K,N,W,\n" +
                "    K0(4)[7],K1(5)[8],=AH6XX(4)[7];\n" +
                "\n" +
                "Guantanamo Bay:            08:  11:  NA:   20.00:    75.00:     5.0:  KG4:\n" +
                "    KG4,=KG4NEX,=KG44WW;\n" +
                "\n" +
                "Argentina:                 13:  14:  SA:  -34.00:    64.00:     3.0:  LU:\n" +
                "    LU;\n";
            File.WriteAllText(Path.Combine(tmpRoot, "ClubLog", "bigcty.dat"), bigCty);

            var provider = new ClubLogProvider(tmpRoot);
            provider.Configure(true, "");
            provider.Load();
            Check("Fixture: 3 entities loaded",      provider.EntityCount == 3,     true);
            Check("Fixture: Big CTY aliases loaded", provider.BigCtyAliasCount > 0, true);

            // Ordinary 3-letter-suffix KG4 call, no exception -- must resolve to USA,
            // not Guantanamo Bay (this is the exact real-world KG4JOK bug).
            var kg4jok = provider.FindByCallsign("KG4JOK");
            Check("KG4JOK (3-letter suffix): resolves", kg4jok != null,        true);
            Check("KG4JOK: resolves to USA (291)",      kg4jok?.Adif == 291,   true);

            // 2-letter-suffix KG4 call, no exception -- the real Guantanamo Bay format.
            var kg4ab = provider.FindByCallsign("KG4AB");
            Check("KG4AB (2-letter suffix): resolves to Guantanamo Bay (105)", kg4ab?.Adif == 105, true);

            // Explicit exception on a 3-letter-suffix call -- must win over the KG4
            // length rule (real historic Guantanamo Bay operator format).
            var kg4nex = provider.FindByCallsign("KG4NEX");
            Check("KG4NEX (exception, 3-letter suffix): still Guantanamo Bay (105)", kg4nex?.Adif == 105, true);

            // Plain "W"/"N" USA calls -- unresolvable via clublog_cty.xml alone (it
            // only lists "K"), proving the multi-prefix-country gap is fixed too.
            var w1aw = provider.FindByCallsign("W1AW");
            Check("W1AW: resolves to USA (291)", w1aw?.Adif == 291, true);
            var n5xyz = provider.FindByCallsign("N5XYZ");
            Check("N5XYZ: resolves to USA (291)", n5xyz?.Adif == 291, true);

            // Per-callarea zone override -- longest-match should prefer "K0" (zone 4)
            // over the bare "K" alias (header default zone 5), without corrupting the
            // shared cached USA entity's own CqZone for other lookups.
            var k0abc = provider.FindByCallsign("K0ABC");
            Check("K0ABC: zone override applied (4, not header default 5)", k0abc?.CqZone == 4, true);
            Check("K0ABC: Adif still USA (291) despite zone override",      k0abc?.Adif == 291,  true);
            var w1awAfter = provider.FindByCallsign("W1AW");
            Check("W1AW: unaffected by K0ABC's zone override (still header default 5)",
                  w1awAfter?.CqZone == 5, true);

            // Plain single-prefix country, unaffected by the KG4/zone-override logic.
            var lu = provider.FindByCallsign("LU1ABC");
            Check("LU1ABC: resolves to Argentina (100)", lu?.Adif == 100, true);
        }
        finally
        {
            try { Directory.Delete(tmpRoot, true); } catch { }
        }

        // Fallback case: no bigcty.dat downloaded yet (fresh install) -- must
        // degrade to the legacy single-prefix-per-entity behavior, not throw or
        // resolve nothing across the board.
        string tmpRoot2 = Path.Combine(Path.GetTempPath(),
            "JimmyTest_BigCty_NoFile_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(tmpRoot2, "ClubLog"));
            string xml =
                "<clublog><entities>" +
                "<ENTITY><adif>291</adif><name>UNITED STATES OF AMERICA</name><prefix>K</prefix><deleted>FALSE</deleted><cqz>5</cqz><cont>NA</cont></ENTITY>" +
                "<ENTITY><adif>105</adif><name>GUANTANAMO BAY</name><prefix>KG4</prefix><deleted>FALSE</deleted><cqz>8</cqz><cont>NA</cont></ENTITY>" +
                "</entities></clublog>";
            File.WriteAllText(Path.Combine(tmpRoot2, "ClubLog", "clublog_cty.xml"), xml);

            var provider = new ClubLogProvider(tmpRoot2);
            provider.Configure(true, "");
            provider.Load();
            Check("No bigcty.dat: BigCtyAliasCount == 0", provider.BigCtyAliasCount == 0, true);

            // Documents the known, accepted limitation during the download gap --
            // matches legacy single-prefix behavior exactly (still misroutes KG4 to
            // Guantanamo, still can't resolve a plain "W" call).
            var kg4jokLegacy = provider.FindByCallsign("KG4JOK");
            Check("Fallback: KG4JOK matches legacy single-prefix Guantanamo entry (105)",
                  kg4jokLegacy?.Adif == 105, true);
            var w1awLegacy = provider.FindByCallsign("W1AW");
            Check("Fallback: W1AW unresolved (legacy multi-prefix gap)", w1awLegacy == null, true);
        }
        finally
        {
            try { Directory.Delete(tmpRoot2, true); } catch { }
        }
    }

    // ── RuleEngine core evaluation ────────────────────────────────────────────
    // Exercises RuleEngine.Evaluate/EvaluateBand directly against a throwaway
    // SQLite database -- no live app, no WSJT-X. Added after two real bugs
    // shipped without anything ever testing RuleEngine itself: a Colonies13
    // DateFrom/DateTo mixup, and the HRC cache always filtering by the current
    // band. Before this, only RuleUniverse.Resolve() (checklist building) and
    // LoadHrcCache() called the correct (unrestricted) way were covered.
    static void RuleEngineCoreTests()
    {
        Console.WriteLine("\n── RuleEngine: Evaluate (GroupBy/Target/Confirmation) ──");
        string tmpDb = Path.Combine(Path.GetTempPath(),
            "JimmyTest_RuleEngine_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = new LogbookDb(tmpDb))
            {
                // Confirmation=None: a plain worked QSO (no QSL) must be enough to count.
                InsertQso(db, "W5TX", "TX", dxcc: 291, zone: 5);
                var defWorked = new RuleDefinition
                {
                    Id = "TEST_WORKED", Name = "Test", FormatVersion = 1, Enabled = true,
                    GroupBy = RuleGroupBy.State, Universe = "US_50_STATES",
                    Confirmation = RuleConfirmation.None, Target = RuleTargetType.All,
                };
                var r1 = RuleEngine.Evaluate(defWorked, tmpDb, null);
                Check("Confirmation=None: TX worked (no QSL) counts",
                      r1.WorkedItems != null && r1.WorkedItems.Contains("TX"), true);
                Check("Confirmation=None: 49 still needed",
                      r1.StillNeeded != null && r1.StillNeeded.Count == 49, true);

                // Confirmation=Lotw: worked-but-unconfirmed now counts as done -- Worked
                // always gates completion/StillNeeded, regardless of Confirmation.
                // Confirmed/ConfirmedItems still track the real LoTW/QRZ status separately
                // (see the "CA confirmed" check below and the Awards tab's per-item column).
                var defConfirmed = new RuleDefinition
                {
                    Id = "TEST_CONFIRMED", Name = "Test", FormatVersion = 1, Enabled = true,
                    GroupBy = RuleGroupBy.State, Universe = "US_50_STATES",
                    Confirmation = RuleConfirmation.Lotw, Target = RuleTargetType.All,
                };
                var r2 = RuleEngine.Evaluate(defConfirmed, tmpDb, null);
                Check("Confirmation=Lotw: TX worked but unconfirmed -> NOT still needed (worked gates completion now)",
                      r2.StillNeeded != null && !r2.StillNeeded.Contains("TX"), true);
                Check("Confirmation=Lotw: TX worked but unconfirmed -> not in ConfirmedItems (still tracked for display)",
                      r2.ConfirmedItems != null && !r2.ConfirmedItems.Contains("TX"), true);

                InsertQso(db, "W6CA", "CA", dxcc: 291, zone: 3, lotwRcvd: "Y");
                var r3 = RuleEngine.Evaluate(defConfirmed, tmpDb, null);
                Check("Confirmation=Lotw: CA confirmed -> not still needed",
                      r3.StillNeeded != null && !r3.StillNeeded.Contains("CA"), true);

                // Per-service breakdown (LotwConfirmedItems/QrzConfirmedItems): tracked
                // independently of the rule's own Confirmation setting, so the Awards tab can
                // show *which* service(s) confirmed each item, not just a yes/no.
                InsertQso(db, "W7QZ", "WA", dxcc: 291, zone: 3, qrzRcvd: "Y");
                var defAny = new RuleDefinition
                {
                    Id = "TEST_ANY", Name = "Test", FormatVersion = 1, Enabled = true,
                    GroupBy = RuleGroupBy.State, Universe = "US_50_STATES",
                    Confirmation = RuleConfirmation.Any, Target = RuleTargetType.All,
                };
                var r4 = RuleEngine.Evaluate(defAny, tmpDb, null);
                Check("Per-service: CA (LoTW-confirmed) appears in LotwConfirmedItems",
                      r4.LotwConfirmedItems != null && r4.LotwConfirmedItems.Contains("CA"), true);
                Check("Per-service: CA (LoTW-confirmed) does NOT appear in QrzConfirmedItems",
                      r4.QrzConfirmedItems != null && !r4.QrzConfirmedItems.Contains("CA"), true);
                Check("Per-service: WA (QRZ-confirmed) appears in QrzConfirmedItems",
                      r4.QrzConfirmedItems != null && r4.QrzConfirmedItems.Contains("WA"), true);
                Check("Per-service: WA (QRZ-confirmed) does NOT appear in LotwConfirmedItems",
                      r4.LotwConfirmedItems != null && !r4.LotwConfirmedItems.Contains("WA"), true);
                Check("Per-service: TX (worked, unconfirmed) in neither Lotw nor Qrz confirmed sets",
                      r4.LotwConfirmedItems != null && r4.QrzConfirmedItems != null &&
                      !r4.LotwConfirmedItems.Contains("TX") && !r4.QrzConfirmedItems.Contains("TX"), true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  RuleEngineCoreTests threw: {ex.GetType().Name}: {ex.Message}");
            failed++;
        }
        finally
        {
            try { File.Delete(tmpDb); } catch { }
        }
    }

    // ── RuleEngine date range filtering ────────────────────────────────────────
    // Mirrors the exact Colonies13 scenario: DateFrom/DateTo set to track one
    // year's event. A real QSO from an earlier year must NOT count once a date
    // range excludes it -- that's the correct, intentional behavior the feature
    // is for, but it's exactly what caused the "why does it still say I need
    // this station" confusion, so it needs a test pinning down both directions.
    static void RuleEngineDateRangeTests()
    {
        Console.WriteLine("\n── RuleEngine: DateFrom/DateTo Filtering ──");
        string tmpDb = Path.Combine(Path.GetTempPath(),
            "JimmyTest_RuleEngineDate_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = new LogbookDb(tmpDb))
            {
                // OH worked only in a prior year -- outside the 2026-07-01..2026-07-08 window.
                InsertQso(db, "W8OH", "OH", dxcc: 291, zone: 4, qsoDate: "20240115");
                // TX worked inside the window.
                InsertQso(db, "W5TX", "TX", dxcc: 291, zone: 5, qsoDate: "20260703");

                var def = new RuleDefinition
                {
                    Id = "TEST_DATERANGE", Name = "Test", FormatVersion = 1, Enabled = true,
                    GroupBy = RuleGroupBy.State, Universe = "US_50_STATES",
                    Confirmation = RuleConfirmation.None, Target = RuleTargetType.All,
                    DateFrom = "2026-07-01", DateTo = "2026-07-08",
                };
                var r = RuleEngine.Evaluate(def, tmpDb, null);
                Check("Date range: OH worked only outside window -> still needed",
                      r.StillNeeded != null && r.StillNeeded.Contains("OH"), true);
                Check("Date range: TX worked inside window -> not still needed",
                      r.StillNeeded != null && !r.StillNeeded.Contains("TX"), true);

                // Same log, no date range at all: OH must count too (all-time view).
                var defAllTime = new RuleDefinition
                {
                    Id = "TEST_ALLTIME", Name = "Test", FormatVersion = 1, Enabled = true,
                    GroupBy = RuleGroupBy.State, Universe = "US_50_STATES",
                    Confirmation = RuleConfirmation.None, Target = RuleTargetType.All,
                };
                var rAll = RuleEngine.Evaluate(defAllTime, tmpDb, null);
                Check("No date range: OH worked (any year) -> not still needed",
                      rAll.StillNeeded != null && !rAll.StillNeeded.Contains("OH"), true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  RuleEngineDateRangeTests threw: {ex.GetType().Name}: {ex.Message}");
            failed++;
        }
        finally
        {
            try { File.Delete(tmpDb); } catch { }
        }
    }

    // ── RuleEngine / LoadHrcCache band independence ────────────────────────────
    // Regression guard for two real bugs: the live Still Need cache
    // (Controller.RefreshStillNeedCache) and the HRC cache (LoadHrcCache) both
    // silently scoped "still needed" to the current band even though the award
    // itself has no [Match] Bands= restriction -- which is every shipped award.
    // EvaluateBand(def, null) / LoadHrcCache(..., band: null) is the correct call
    // for those awards; passing a real band is a genuinely different, deliberately
    // restricted view (used by the Still Need tab's manual band filter). This
    // pins down both halves of that contract: unrestricted finds cross-band work,
    // and a real band filter genuinely does restrict (so the mechanism itself is
    // proven to work, not just always empty/always full).
    static void RuleEngineBandIndependenceTests()
    {
        Console.WriteLine("\n── RuleEngine / LoadHrcCache: Band Independence ──");
        string tmpDb = Path.Combine(Path.GetTempPath(),
            "JimmyTest_RuleEngineBand_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = new LogbookDb(tmpDb))
            {
                // Worked on 20m only. No Bands= restriction on the award (empty list).
                InsertQso(db, "K2BAND", "", dxcc: 291, zone: 5, band: "20m");
                var def = new RuleDefinition
                {
                    Id = "TEST_BANDIND", Name = "Test", FormatVersion = 1, Enabled = true,
                    GroupBy = RuleGroupBy.Callsign, Target = RuleTargetType.Count, Threshold = 1,
                    Confirmation = RuleConfirmation.None,
                };

                var unrestricted = RuleEngine.EvaluateBand(def, null, tmpDb, null);
                Check("EvaluateBand(band:null): worked on 20m counts regardless of 'current' band",
                      unrestricted.WorkedItems != null && unrestricted.WorkedItems.Contains("K2BAND"), true);

                var wrongBand = RuleEngine.EvaluateBand(def, "10m", tmpDb, null);
                Check("EvaluateBand(band:'10m'): correctly restricts -- 20m QSO doesn't count for 10m",
                      wrongBand.WorkedItems == null || !wrongBand.WorkedItems.Contains("K2BAND"), true);

                var rightBand = RuleEngine.EvaluateBand(def, "20m", tmpDb, null);
                Check("EvaluateBand(band:'20m'): restricting to the actual band still finds it",
                      rightBand.WorkedItems != null && rightBand.WorkedItems.Contains("K2BAND"), true);

                // Same mechanism, older HRC cache code path: state confirmed on 20m only.
                InsertQso(db, "W5TX", "TX", dxcc: 291, zone: 5, band: "20m", lotwRcvd: "Y");

                HashSet<string> neededNoBand, neededWithBand;
                db.LoadHrcCache(out neededNoBand, out _, out _, out _, band: null);
                db.LoadHrcCache(out neededWithBand, out _, out _, out _, band: "10m");

                Check("LoadHrcCache(band:null): TX confirmed on 20m -> not needed (all-time view)",
                      !neededNoBand.Contains("TX"), true);
                Check("LoadHrcCache(band:'10m'): TX confirmed only on 20m -> needed again for 10m",
                      neededWithBand.Contains("TX"), true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  RuleEngineBandIndependenceTests threw: {ex.GetType().Name}: {ex.Message}");
            failed++;
        }
        finally
        {
            try { File.Delete(tmpDb); } catch { }
        }
    }

    // ── LogbookDb.Upsert: a download FROM a service marks it as already-uploaded
    // TO that same service ────────────────────────────────────────────────────
    // A QSO downloaded from QRZ obviously doesn't need to be uploaded back to
    // QRZ -- that's where it came from. Before this fix, qrz_uploaded_at/
    // clublog_uploaded_at were never touched by a download import at all, so
    // such a QSO stayed "pending" forever and got redundantly re-uploaded.
    static void LogbookDbDownloadMarksUploadedTests()
    {
        Console.WriteLine("\n── LogbookDb.Upsert: download marks matching service uploaded ──");
        string tmpDb = Path.Combine(Path.GetTempPath(),
            "JimmyTest_DownloadUploaded_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = new LogbookDb(tmpDb))
            {
                void DoUpsert(string call, string key, string source)
                {
                    db.Upsert(call, "20m", "FT8", "20260706", "1200", "1215",
                        14_074_000, "-10", "-05", "", "", 0, 0,
                        "", "", "", "", "", "", "",
                        "", "", "", "",
                        source, "", key,
                        "", 0, "", "", "", "", "", "", "", "", "", "");
                }

                string keyA = AdifImporter.BuildDedupKey("W1AW", "20m", "FT8", "20260706", "1200");
                DoUpsert("W1AW", keyA, "WSJTX");
                Check("before any download: QRZ pending includes the WSJTX-logged QSO",
                      db.GetUploadSyncStatus("QRZ").PendingCount == 1, true);
                Check("before any download: CLUBLOG pending also includes it",
                      db.GetUploadSyncStatus("CLUBLOG").PendingCount == 1, true);

                // Downloading it back from QRZ must mark it uploaded-to-QRZ...
                DoUpsert("W1AW", keyA, "QRZ");
                Check("QRZ download marks the QSO as no longer pending for QRZ",
                      db.GetUploadSyncStatus("QRZ").PendingCount == 0, true);
                Check("...but does NOT affect Club Log's pending status",
                      db.GetUploadSyncStatus("CLUBLOG").PendingCount == 1, true);

                // A later Club Log download for the same QSO must independently mark
                // Club Log too, without disturbing the already-set QRZ status.
                DoUpsert("W1AW", keyA, "CLUBLOG");
                Check("Club Log download marks the QSO as no longer pending for Club Log",
                      db.GetUploadSyncStatus("CLUBLOG").PendingCount == 0, true);
                Check("QRZ status remains uploaded after the Club Log download",
                      db.GetUploadSyncStatus("QRZ").PendingCount == 0, true);

                // A download from an unrelated service (LOTW) must not mark either.
                string keyB = AdifImporter.BuildDedupKey("K1XYZ", "20m", "FT8", "20260706", "1201");
                DoUpsert("K1XYZ", keyB, "WSJTX");
                DoUpsert("K1XYZ", keyB, "LOTW");
                Check("LoTW download does not mark QRZ as uploaded",
                      db.GetUploadSyncStatus("QRZ").PendingCount == 1, true);
                Check("LoTW download does not mark Club Log as uploaded",
                      db.GetUploadSyncStatus("CLUBLOG").PendingCount == 1, true);

            }

            // Separate, single-row database for this check -- GetUploadSyncStatus's
            // LastUploadUtc is a table-wide MAX(), which the multi-row db above would
            // confuse this assertion with (an unrelated row's later real timestamp
            // would win the MAX() over the specific value being checked here).
            string tmpDb2 = Path.Combine(Path.GetTempPath(),
                "JimmyTest_DownloadUploaded2_" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using (var db2 = new LogbookDb(tmpDb2))
                {
                    string keyC = AdifImporter.BuildDedupKey("K1XYZ", "20m", "FT8", "20260706", "1201");
                    db2.Upsert("K1XYZ", "20m", "FT8", "20260706", "1200", "1215",
                        14_074_000, "-10", "-05", "", "", 0, 0,
                        "", "", "", "", "", "", "",
                        "", "", "", "",
                        "WSJTX", "", keyC,
                        "", 0, "", "", "", "", "", "", "", "", "", "");

                    // A real prior upload (Jimmy's own successful Alt+U) must never be
                    // downgraded/overwritten by a later download's import timestamp.
                    var realUploadTime = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc);
                    db2.MarkUploaded(keyC, "QRZ", realUploadTime);
                    db2.Upsert("K1XYZ", "20m", "FT8", "20260706", "1200", "1215",
                        14_074_000, "-10", "-05", "", "", 0, 0,
                        "", "", "", "", "", "", "",
                        "", "", "", "",
                        "QRZ", "", keyC,
                        "", 0, "", "", "", "", "", "", "", "", "", "");
                    Check("a real prior upload timestamp is preserved, not overwritten by a later download",
                          db2.GetUploadSyncStatus("QRZ").LastUploadUtc == realUploadTime, true);
                }
            }
            finally
            {
                try { File.Delete(tmpDb2); } catch { }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  LogbookDbDownloadMarksUploadedTests threw: {ex.GetType().Name}: {ex.Message}");
            failed++;
        }
        finally
        {
            try { File.Delete(tmpDb); } catch { }
        }
    }

    // ── RuleEngine "Band(s) worked" column ─────────────────────────────────────
    // The Awards tab's "Band(s) worked" column (RuleResult.WorkedBands) must
    // list every band a station was worked on, low-to-high, regardless of the
    // order the QSOs were logged in.
    static void RuleEngineWorkedBandsTests()
    {
        Console.WriteLine("\n── RuleEngine: WorkedBands (\"Band(s) worked\" column) ──");
        string tmpDb = Path.Combine(Path.GetTempPath(),
            "JimmyTest_RuleEngineBands_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = new LogbookDb(tmpDb))
            {
                // Logged out of frequency order: 17m, then 40m, then 20m.
                InsertQso(db, "K2BANDS", "", dxcc: 291, zone: 5, band: "17m", qsoDate: "20260701");
                InsertQso(db, "K2BANDS", "", dxcc: 291, zone: 5, band: "40m", qsoDate: "20260702");
                InsertQso(db, "K2BANDS", "", dxcc: 291, zone: 5, band: "20m", qsoDate: "20260703");

                var def = new RuleDefinition
                {
                    Id = "TEST_BANDS", Name = "Test", FormatVersion = 1, Enabled = true,
                    GroupBy = RuleGroupBy.Callsign, Target = RuleTargetType.Count, Threshold = 1,
                    Confirmation = RuleConfirmation.None,
                };
                var r = RuleEngine.Evaluate(def, tmpDb, null);

                Check("WorkedBands: entry exists for K2BANDS",
                      r.WorkedBands != null && r.WorkedBands.ContainsKey("K2BANDS"), true);
                if (r.WorkedBands != null && r.WorkedBands.TryGetValue("K2BANDS", out var bands))
                {
                    CheckStr("WorkedBands: ordered low-to-high regardless of log order",
                             string.Join(",", bands), "40m,20m,17m");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  RuleEngineWorkedBandsTests threw: {ex.GetType().Name}: {ex.Message}");
            failed++;
        }
        finally
        {
            try { File.Delete(tmpDb); } catch { }
        }
    }

    // ── RuleEngine: Count-target rules never produce a StillNeeded checklist ───
    // Regression guard for the DXCC "Still Need" live-tagging bug: WsjtxClient's HRC
    // suppression gates (IsHrcWasNeeded/IsHrcDxccUnconfirmed/IsHrcZoneNeeded) only
    // retire the old HRC tracking once the equivalent Rule Definition is actually
    // present in activeAwardTags -- and Controller.RefreshStillNeedCache() only adds
    // a rule to activeAwardTags when result.StillNeeded != null. The shipped
    // DXCC.ini is Target=COUNT, so this confirms it (and any Count/Levels-target
    // rule) can never satisfy that guard, regardless of GroupBy/SupportsLiveTag --
    // i.e. checking "DXCC" in the Still Need tab must not silently suppress the
    // older, still-working DXCC_UNCONFIRMED HRC category.
    static void RuleEngineCountTargetStillNeededTests()
    {
        Console.WriteLine("\n── RuleEngine: Count-target rules never populate StillNeeded ──");
        string tmpDb = Path.Combine(Path.GetTempPath(),
            "JimmyTest_RuleEngineCountTarget_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = new LogbookDb(tmpDb))
            {
                InsertQso(db, "K2DXCC", "", dxcc: 291, zone: 5, band: "20m", lotwRcvd: "Y");

                // Shaped exactly like the shipped DXCC.ini: GroupBy=Dxcc, Target=Count.
                var dxccLike = new RuleDefinition
                {
                    Id = "DXCC", Name = "Test DXCC", FormatVersion = 1, Enabled = true,
                    GroupBy = RuleGroupBy.Dxcc, Target = RuleTargetType.Count, Threshold = 100,
                    Confirmation = RuleConfirmation.Any,
                };
                var countResult = RuleEngine.Evaluate(dxccLike, tmpDb, null);
                Check("SupportsLiveTag(DXCC-like, GroupBy=Dxcc) is true (GroupBy alone doesn't exclude it)",
                      RuleEngine.SupportsLiveTag(dxccLike), true);
                Check("Target=Count result has StillNeeded == null (can't satisfy RefreshStillNeedCache's guard)",
                      countResult.StillNeeded == null, true);

                // Different GroupBy, but Target=All (shaped like the shipped WAS.ini) -- this is
                // the case that SHOULD be able to enter activeAwardTags and retire an HRC category.
                var allTargetLike = new RuleDefinition
                {
                    Id = "TEST_WAS_ALL", Name = "Test WAS All", FormatVersion = 1, Enabled = true,
                    GroupBy = RuleGroupBy.State, Target = RuleTargetType.All, Threshold = 0,
                    Confirmation = RuleConfirmation.Any, Universe = "US_50_STATES",
                };
                var allResult = RuleEngine.Evaluate(allTargetLike, tmpDb, null);
                Check("Target=All result (known-working universe) has a real StillNeeded list, not null",
                      allResult.StillNeeded != null, true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  RuleEngineCountTargetStillNeededTests threw: {ex.GetType().Name}: {ex.Message}");
            failed++;
        }
        finally
        {
            try { File.Delete(tmpDb); } catch { }
        }
    }

    // ── AdifRecordBuilder: shared field list used by RequestLog + HandleLiveQsoLogged ──
    // Regression guard for RequestLog (Jimmy's own self-initiated logging path) now
    // reusing this shared builder instead of a separate hand-rolled ADIF string --
    // confirms the field it uniquely needed (qso_date_off, for a QSO spanning a UTC
    // midnight boundary) survived the switch, and that omitted/empty fields
    // (name/comment/tx_pwr/operator -- not available to RequestLog) are correctly
    // left out rather than emitted blank.
    static void AdifRecordBuilderTests()
    {
        Console.WriteLine("\n── AdifRecordBuilder: field list ──");

        string full = AdifRecordBuilder.Build(
            "K4YT", "20m", 14074000, "FT8",
            "20260706", "235900", "000030",
            "-05", "-09", "EM63", "", "",
            "", "", "KB0UZT", "EN34",
            qsoDateOff: "20260707");

        Check("Build(): includes call", full.Contains("<call:4>K4YT"), true);
        Check("Build(): includes band", full.Contains("<band:3>20m"), true);
        Check("Build(): includes qso_date (on)", full.Contains("<qso_date:8>20260706"), true);
        Check("Build(): includes qso_date_off when the QSO crosses a UTC day boundary", full.Contains("<qso_date_off:8>20260707"), true);
        Check("Build(): includes time_off", full.Contains("<time_off:6>000030"), true);
        Check("Build(): includes station_callsign", full.Contains("<station_callsign:6>KB0UZT"), true);
        Check("Build(): terminates with <eor>", full.TrimEnd().EndsWith("<eor>"), true);
        Check("Build(): omits empty name/comment/tx_pwr/operator rather than emitting them blank",
              !full.Contains("<name:") && !full.Contains("<comment:") && !full.Contains("<tx_pwr:") && !full.Contains("<operator:"), true);

        string withoutDateOff = AdifRecordBuilder.Build(
            "K4YT", "20m", 14074000, "FT8",
            "20260706", "235900", "235930",
            "-05", "-09", "EM63", "", "",
            "", "", "KB0UZT", "EN34");
        Check("Build(): qso_date_off omitted entirely when not supplied (same-day QSO, existing callers unaffected)",
              !withoutDateOff.Contains("<qso_date_off:"), true);
    }

    // ── AdifExporter: export-direction record building (Edit Log / Sync "Export ADIF") ──
    static void AdifExporterTests()
    {
        Console.WriteLine("\n── AdifExporter: field dictionary -> ADIF text ──");

        var rec = new Dictionary<string, string>
        {
            ["CALL"]        = "K4YT",
            ["BAND"]        = "20m",
            ["QSO_DATE"]    = "20260706",
            ["EMPTY_FIELD"] = "",
        };
        string built = AdifExporter.BuildRecord(rec);
        Check("BuildRecord: includes call", built.Contains("<CALL:4>K4YT"), true);
        Check("BuildRecord: includes band", built.Contains("<BAND:3>20m"), true);
        Check("BuildRecord: omits blank-valued fields", !built.Contains("<EMPTY_FIELD:"), true);
        Check("BuildRecord: terminates with <eor>", built.TrimEnd().EndsWith("<eor>"), true);

        string header = AdifExporter.Header();
        Check("Header: ends with <EOH>", header.TrimEnd().EndsWith("<EOH>"), true);

        var records = new List<Dictionary<string, string>> { rec, rec };
        string file = AdifExporter.BuildFile(records);
        Check("BuildFile: starts with the header", file.StartsWith(header), true);
        Check("BuildFile: contains one <eor> per record",
              file.Split(new[] { "<eor>" }, StringSplitOptions.None).Length - 1 == 2, true);
    }

    // ── LogbookDb: Edit Log tab support (search/edit/delete/export) ─────────────
    // Local-only data hygiene tooling added after a real incident where fake
    // replay-test QSOs (K4YT, W1ADIF, W9NEED, etc.) leaked into the production
    // logbook and real QRZ/Club Log accounts -- this is what lets a user find and
    // remove them without touching QRZ/Club Log/LoTW's own APIs.
    static void LogbookDbEditLogTests()
    {
        Console.WriteLine("\n── LogbookDb: SearchQsos/GetQso/UpdateQso/DeleteQsos/GetAdifFieldDicts ──");
        string tmpDb = Path.Combine(Path.GetTempPath(),
            "JimmyTest_EditLog_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = new LogbookDb(tmpDb))
            {
                InsertQso(db, "K4YT",   "GA", dxcc: 291, zone: 4, band: "20m", qsoDate: "20260706");
                InsertQso(db, "W1ADIF", "CT", dxcc: 291, zone: 5, band: "20m", qsoDate: "20260708");
                InsertQso(db, "W9NEED", "IL", dxcc: 291, zone: 4, band: "40m", qsoDate: "20260710");

                // ── SearchQsos filters ──────────────────────────────────────
                var byCall = db.SearchQsos("K4YT", null, null, null);
                Check("SearchQsos: callsign filter matches", byCall.Count == 1 && byCall[0].Callsign == "K4YT", true);

                var byDateFrom = db.SearchQsos(null, null, "20260707", null);
                Check("SearchQsos: date-from excludes the earlier K4YT row (2 remain)", byDateFrom.Count == 2, true);

                var byDateRange = db.SearchQsos(null, null, "20260707", "20260709");
                Check("SearchQsos: date range narrows to just W1ADIF",
                      byDateRange.Count == 1 && byDateRange[0].Callsign == "W1ADIF", true);

                var bySource = db.SearchQsos(null, "MANUAL", null, null);
                Check("SearchQsos: source filter matches (InsertQso writes source=MANUAL)", bySource.Count == 3, true);

                var all = db.SearchQsos(null, null, null, null);
                Check("SearchQsos: no filters returns all 3", all.Count == 3, true);

                // ── GetQso / UpdateQso ───────────────────────────────────────
                int id = byCall[0].Id;
                Check("SearchQsos: row id is populated (non-zero)", id != 0, true);

                var fetched = db.GetQso(id);
                Check("GetQso: fetches the right row", fetched != null && fetched.Callsign == "K4YT", true);

                bool updated = db.UpdateQso(id, "K4YT", "20m", "FT8", "20260706", "1200", "1215",
                    "FL", "Test", "EM63", "Test Name", "-10", "-05", "Fixed via editor");
                Check("UpdateQso: reports a change", updated, true);

                var afterUpdate = db.GetQso(id);
                Check("UpdateQso: state updated",   afterUpdate.State   == "FL", true);
                Check("UpdateQso: comment updated", afterUpdate.Comment == "Fixed via editor", true);

                // Attempting to rename this row to collide with another row's identity
                // (same callsign/band/mode/date/time) must throw, not silently merge --
                // the caller shows this as a "duplicate" error rather than losing data.
                bool threwOnCollision = false;
                try
                {
                    db.UpdateQso(id, "W1ADIF", "20m", "FT8", "20260708", "1200", "1215",
                        "FL", "Test", "", "", "", "", "");
                }
                catch (Exception) { threwOnCollision = true; }
                Check("UpdateQso: colliding with another row's identity throws instead of silently merging",
                      threwOnCollision, true);

                // ── DeleteQsos ────────────────────────────────────────────────
                var toDelete = db.SearchQsos("W1ADIF", null, null, null);
                int deleted = db.DeleteQsos(new[] { toDelete[0].Id });
                Check("DeleteQsos: reports 1 row deleted", deleted == 1, true);
                Check("DeleteQsos: row actually gone", db.SearchQsos("W1ADIF", null, null, null).Count == 0, true);
                Check("DeleteQsos: unrelated rows untouched", db.SearchQsos(null, null, null, null).Count == 2, true);
                Check("DeleteQsos: empty id list is a safe no-op", db.DeleteQsos(new int[0]) == 0, true);

                // ── GetAdifFieldDicts ─────────────────────────────────────────
                var exportAll = db.GetAdifFieldDicts(null);
                Check("GetAdifFieldDicts: exports every remaining row", exportAll.Count == 2, true);
                Check("GetAdifFieldDicts: CALL field present",
                      exportAll.Any(f => f.ContainsKey("CALL") && f["CALL"] == "K4YT"), true);
                Check("GetAdifFieldDicts: zero-valued numeric fields omitted (no DXCC=0 noise)",
                      exportAll.All(f => !f.ContainsKey("DXCC") || f["DXCC"] != "0"), true);

                var idsLeft = db.SearchQsos(null, null, null, null).Select(q => q.Id).ToList();
                var exportOne = db.GetAdifFieldDicts(new[] { idsLeft[0] });
                Check("GetAdifFieldDicts: scoped id list exports exactly that count", exportOne.Count == 1, true);

                var exportBySource = db.GetAdifFieldDicts(null, new[] { "MANUAL" });
                Check("GetAdifFieldDicts: source filter matching all rows (source=MANUAL) exports both",
                      exportBySource.Count == 2, true);

                var exportByOtherSource = db.GetAdifFieldDicts(null, new[] { "QRZ" });
                Check("GetAdifFieldDicts: source filter matching no rows exports none",
                      exportByOtherSource.Count == 0, true);

                var exportIdsAndSource = db.GetAdifFieldDicts(new[] { idsLeft[0] }, new[] { "MANUAL" });
                Check("GetAdifFieldDicts: id list and source filter combine (AND, not OR)",
                      exportIdsAndSource.Count == 1, true);
            }
        }
        catch (Exception ex)
        {
            Check("LogbookDbEditLogTests: unexpected exception -- " + ex.Message, false, true);
        }
        finally
        {
            try { if (File.Exists(tmpDb)) File.Delete(tmpDb); } catch { }
        }
    }

    // ── LogbookDb.Upsert: authoritative source overrides Jimmy's own guess ─────
    // country/dxcc/continent/cq_zone are populated at live-logging time from
    // Jimmy's own local Club Log cache (EnrichWithClubLogGeoData) -- a guess, not
    // an authoritative fact. A later sync from QRZ/LoTW/Club Log must always be
    // able to correct that guess, even if a (possibly wrong) value is already
    // present. A second self-sourced (WSJTX) or MANUAL write must NOT clobber an
    // already-synced authoritative value -- it only fills in if still blank.
    // Uses SearchByCallsign (country/dxcc) as the read-back path since those are
    // the only two of the four affected columns already exposed publicly; all
    // four columns share the identical CASE WHEN shape, so this covers the logic.
    static void LogbookDbAuthoritativeSourceOverrideTests()
    {
        Console.WriteLine("\n── LogbookDb.Upsert: authoritative source overrides guess ──");
        string tmpDb = Path.Combine(Path.GetTempPath(),
            "JimmyTest_Upsert_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = new LogbookDb(tmpDb))
            {
                string key = AdifImporter.BuildDedupKey("W1AW", "20m", "FT8", "20260706", "1200");
                void DoUpsert(string source, string country, int dxcc)
                {
                    db.Upsert("W1AW", "20m", "FT8", "20260706", "1200", "1215",
                        14_074_000, "-10", "-05", "", country, dxcc, 0,
                        "", "", "", "", "", "", "",
                        "", "", "", "",
                        source, "", key,
                        "", 0, "", "", "", "", "", "", "", "", "", "");
                }
                (string country, int dxcc) Read()
                {
                    var rec = db.SearchByCallsign("W1AW").First();
                    return (rec.Country, rec.Dxcc);
                }

                // Jimmy's own guess, written at live-logging time (source=WSJTX)
                DoUpsert("WSJTX", "Wrong Guess", 1);
                var afterGuess = Read();
                Check("initial WSJTX guess stored", afterGuess.country == "Wrong Guess" && afterGuess.dxcc == 1, true);

                // A second self-sourced write must NOT clobber the (still-a-guess) value --
                // a different WSJTX guess must not overwrite the first (blank-only-backfill).
                DoUpsert("WSJTX", "Another Guess", 2);
                var afterSecondGuess = Read();
                Check("second WSJTX write does not overwrite existing guess (blank-only-backfill)",
                      afterSecondGuess.country == "Wrong Guess" && afterSecondGuess.dxcc == 1, true);

                // QRZ sync arrives with the real data -- must overwrite the wrong guess
                DoUpsert("QRZ", "United States", 291);
                var afterQrz = Read();
                Check("QRZ sync overwrites wrong guess: country", afterQrz.country == "United States", true);
                Check("QRZ sync overwrites wrong guess: dxcc", afterQrz.dxcc == 291, true);

                // A subsequent WSJTX/self-log re-send must NOT be able to clobber the
                // now-authoritative QRZ value back to a guess.
                DoUpsert("WSJTX", "Wrong Guess Again", 1);
                var afterReguess = Read();
                Check("WSJTX write after QRZ sync cannot overwrite authoritative value",
                      afterReguess.country == "United States" && afterReguess.dxcc == 291, true);

                // A later LoTW sync must still be able to override an existing (even if already
                // authoritative-sourced) value -- authoritative sources always win over each other.
                DoUpsert("LOTW", "United States Corrected", 291);
                var afterLotw = Read();
                Check("LOTW sync can overwrite a previously-QRZ-sourced value",
                      afterLotw.country == "United States Corrected", true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  LogbookDbAuthoritativeSourceOverrideTests threw: {ex.GetType().Name}: {ex.Message}");
            failed++;
        }
        finally
        {
            try { File.Delete(tmpDb); } catch { }
        }
    }

    // ── LogbookDb.Upsert: newly-confirmed vs corrected categorization ──────────
    // A sync's "N updated" used to be a single opaque bucket. Confirming a QSL
    // (moves award progress) and correcting a data-quality field (state/country/
    // etc.) are independent signals -- a row can be neither, either, or both.
    static void LogbookDbNewlyConfirmedVsCorrectedTests()
    {
        Console.WriteLine("\n── LogbookDb.Upsert: newly-confirmed vs corrected categorization ──");
        string tmpDb = Path.Combine(Path.GetTempPath(),
            "JimmyTest_UpsertCategorize_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = new LogbookDb(tmpDb))
            {
                string key = AdifImporter.BuildDedupKey("K1ABC", "20m", "FT8", "20260706", "1200");
                (bool isNew, bool newlyConfirmed, bool corrected) DoUpsert(
                    string state, string qrzQslRcvd, string source = "QRZ")
                {
                    return db.Upsert("K1ABC", "20m", "FT8", "20260706", "1200", "1215",
                        14_074_000, "-10", "-05", state, "", 0, 0,
                        "", "", "", "", "", "", "",
                        "", "", "", qrzQslRcvd,
                        source, "", key,
                        "", 0, "", "", "", "", "", "", "", "", "", "");
                }

                var first = DoUpsert("", "");
                Check("first insert: isNew", first.isNew, true);
                Check("first insert: not newlyConfirmed", first.newlyConfirmed, false);
                Check("first insert: not corrected", first.corrected, false);

                var noChange = DoUpsert("", "");
                Check("re-upsert identical data: not new", noChange.isNew, false);
                Check("re-upsert identical data: not newlyConfirmed", noChange.newlyConfirmed, false);
                Check("re-upsert identical data: not corrected", noChange.corrected, false);

                var confirmed = DoUpsert("", "Y");
                Check("QRZ confirms QSL: not new", confirmed.isNew, false);
                Check("QRZ confirms QSL: newlyConfirmed", confirmed.newlyConfirmed, true);
                Check("QRZ confirms QSL: not corrected", confirmed.corrected, false);

                var stateFixed = DoUpsert("CA", "Y");
                Check("state corrected (already confirmed): not newlyConfirmed again", stateFixed.newlyConfirmed, false);
                Check("state corrected (already confirmed): corrected", stateFixed.corrected, true);

                string key2 = AdifImporter.BuildDedupKey("K2DEF", "20m", "FT8", "20260706", "1300");
                db.Upsert("K2DEF", "20m", "FT8", "20260706", "1300", "1315",
                    14_074_000, "-10", "-05", "", "", 0, 0,
                    "", "", "", "", "", "", "",
                    "", "", "", "",
                    "QRZ", "", key2,
                    "", 0, "", "", "", "", "", "", "", "", "", "");
                var bothAtOnce = db.Upsert("K2DEF", "20m", "FT8", "20260706", "1300", "1315",
                    14_074_000, "-10", "-05", "TX", "", 0, 0,
                    "", "", "", "", "", "", "",
                    "", "", "", "Y",
                    "QRZ", "", key2,
                    "", 0, "", "", "", "", "", "", "", "", "", "");
                Check("confirmed + corrected in same sync: newlyConfirmed", bothAtOnce.newlyConfirmed, true);
                Check("confirmed + corrected in same sync: corrected", bothAtOnce.corrected, true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  LogbookDbNewlyConfirmedVsCorrectedTests threw: {ex.GetType().Name}: {ex.Message}");
            failed++;
        }
        finally
        {
            try { File.Delete(tmpDb); } catch { }
        }
    }

    // ── 13 Colonies bonus-station roster regression guard ──────────────────────
    // WM3PEN/GB13COL/TM13COL are bonus stations, deliberately excluded from the
    // Clean Sweep roster (they have their own separate award instead -- see
    // Colonies13Bonus.ini). Guards against someone "fixing" a future bug report
    // by merging them back into the Clean Sweep roster.
    static void Colonies13RosterRegressionTest()
    {
        Console.WriteLine("\n── Colonies13: Bonus Stations Excluded From Clean Sweep ──");
        string path = FindRepoFile(Path.Combine("WSJTX_Controller", "RuleDefinitions", "Lists", "colonies13_roster.txt"));
        if (path == null)
        {
            Console.WriteLine("  SKIP  colonies13_roster.txt not found relative to test binary");
            return;
        }

        string text = File.ReadAllText(path);
        // Only real (non-comment) roster lines count -- the file documents the
        // bonus calls in a comment, which must not be mistaken for membership.
        var realLines = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith(";") && !l.StartsWith("#"))
            .Select(l => l.Split(';')[0].Trim().ToUpperInvariant())
            .ToList();

        foreach (var bonus in new[] { "WM3PEN", "GB13COL", "TM13COL" })
            Check($"Clean Sweep roster excludes bonus station {bonus}",
                  realLines.Contains(bonus), false);
        Check("Clean Sweep roster has exactly the 13 official stations",
              realLines.Count == 13, true);
    }

    // ── CallQueueRanker: pure ranking/ordering logic extracted from WsjtxClient ──
    // Category *derivation* (HRC/lookup/award-tag matching) stays in WsjtxClient and
    // isn't covered here; these tests assume Category/Priority/Distance/Azimuth/Snr/
    // SequenceNumber are already set, matching how WsjtxClient.SortCalls() calls in.
    static EnqueueDecodeMessage MakeDecode(string call, WsjtxClient.CallCategory cat, int distance = 500,
        int azimuth = 45, int snr = -10, int sequenceNumber = 1)
    {
        return new EnqueueDecodeMessage
        {
            Message = $"{MY_CALL} {call} EM63",
            Category = cat,
            Distance = distance,
            Azimuth = azimuth,
            Snr = snr,
            SequenceNumber = sequenceNumber,
        };
    }

    static void CallQueueRankerCategoryTierTests()
    {
        Console.WriteLine("\n── CallQueueRanker: Category Tier Ordering ──");
        var ranker = new CallQueueRanker();

        var toMyCall   = MakeDecode("K1ABC", WsjtxClient.CallCategory.TO_MYCALL);
        var newCtryBand= MakeDecode("K2ABC", WsjtxClient.CallCategory.NEW_COUNTRY_ON_BAND);
        var newCtry    = MakeDecode("K3ABC", WsjtxClient.CallCategory.NEW_COUNTRY);
        var wantedCq   = MakeDecode("K4ABC", WsjtxClient.CallCategory.WANTED_CQ);
        var alwaysWtd  = MakeDecode("K5ABC", WsjtxClient.CallCategory.ALWAYS_WANTED);
        var wasNeeded  = MakeDecode("K6ABC", WsjtxClient.CallCategory.WAS_NEEDED);

        foreach (var d in new[] { toMyCall, newCtryBand, newCtry, wantedCq, alwaysWtd, wasNeeded })
            ranker.SetRank(d);

        Check("TO_MYCALL ranks above NEW_COUNTRY_ON_BAND", toMyCall.Rank > newCtryBand.Rank, true);
        Check("NEW_COUNTRY_ON_BAND ranks above NEW_COUNTRY", newCtryBand.Rank > newCtry.Rank, true);
        Check("NEW_COUNTRY ranks above WANTED_CQ", newCtry.Rank > wantedCq.Rank, true);
        Check("WANTED_CQ ranks above ALWAYS_WANTED", wantedCq.Rank > alwaysWtd.Rank, true);
        Check("ALWAYS_WANTED ranks above WAS_NEEDED (tier 0)", alwaysWtd.Rank > wasNeeded.Rank, true);
        Check("Non-DEFAULT categories always rank above the DEFAULT tier base",
              wasNeeded.Rank >= CallQueueRanker.NonDefaultTierBase, true);

        // POTA/SOTA/MANUAL_SEL are hidden from the tier UI and rank with WANTED_CQ.
        var pota = MakeDecode("K7ABC", WsjtxClient.CallCategory.POTA);
        ranker.SetRank(pota);
        Check("POTA ranks the same tier as WANTED_CQ", pota.Rank == wantedCq.Rank, true);
    }

    static void CallQueueRankerSortMethodTests()
    {
        Console.WriteLine("\n── CallQueueRanker: DEFAULT-category Sort Methods ──");
        var ranker = new CallQueueRanker();

        // CALL_ORDER: oldest (lowest SequenceNumber) first.
        var older = MakeDecode("K1OLD", WsjtxClient.CallCategory.DEFAULT, sequenceNumber: 1);
        var newer = MakeDecode("K2NEW", WsjtxClient.CallCategory.DEFAULT, sequenceNumber: 2);
        ranker.ApplySortOrder(new List<WsjtxClient.RankMethods> { WsjtxClient.RankMethods.CALL_ORDER }, null);
        ranker.SetRank(older); ranker.SetRank(newer);
        Check("CALL_ORDER: oldest (lower SequenceNumber) ranks first", older.Rank > newer.Rank, true);

        // MOST_RECENT: newest (highest SequenceNumber) first.
        ranker.ApplySortOrder(new List<WsjtxClient.RankMethods> { WsjtxClient.RankMethods.MOST_RECENT }, null);
        ranker.SetRank(older); ranker.SetRank(newer);
        Check("MOST_RECENT: newest (higher SequenceNumber) ranks first", newer.Rank > older.Rank, true);

        // DIST_DECR: farthest first (descending distance down the list).
        var near = MakeDecode("K3NEAR", WsjtxClient.CallCategory.DEFAULT, distance: 100);
        var far  = MakeDecode("K4FAR",  WsjtxClient.CallCategory.DEFAULT, distance: 5000);
        ranker.ApplySortOrder(new List<WsjtxClient.RankMethods> { WsjtxClient.RankMethods.DIST_DECR }, null);
        ranker.SetRank(near); ranker.SetRank(far);
        Check("DIST_DECR: farthest station ranks first", far.Rank > near.Rank, true);

        // DIST_INCR: nearest first (ascending distance down the list).
        ranker.ApplySortOrder(new List<WsjtxClient.RankMethods> { WsjtxClient.RankMethods.DIST_INCR }, null);
        ranker.SetRank(near); ranker.SetRank(far);
        Check("DIST_INCR: nearest station ranks first", near.Rank > far.Rank, true);

        // SNR_DECR: strongest signal first.
        var weak = MakeDecode("K5WEAK", WsjtxClient.CallCategory.DEFAULT, snr: -20);
        var strong = MakeDecode("K6STR", WsjtxClient.CallCategory.DEFAULT, snr: -3);
        ranker.ApplySortOrder(new List<WsjtxClient.RankMethods> { WsjtxClient.RankMethods.SNR_DECR }, null);
        ranker.SetRank(weak); ranker.SetRank(strong);
        Check("SNR_DECR: strongest signal ranks first", strong.Rank > weak.Rank, true);

        // SNR_INCR: weakest signal first.
        ranker.ApplySortOrder(new List<WsjtxClient.RankMethods> { WsjtxClient.RankMethods.SNR_INCR }, null);
        ranker.SetRank(weak); ranker.SetRank(strong);
        Check("SNR_INCR: weakest signal ranks first", weak.Rank > strong.Rank, true);
    }

    static void CallQueueRankerTieBreakTests()
    {
        Console.WriteLine("\n── CallQueueRanker: Tie-break Ordering (Compare/CompareRank) ──");
        var ranker = new CallQueueRanker();
        // Primary MOST_RECENT (tied), secondary DIST_INCR breaks the tie.
        ranker.ApplySortOrder(new List<WsjtxClient.RankMethods>
            { WsjtxClient.RankMethods.MOST_RECENT, WsjtxClient.RankMethods.DIST_INCR }, null);

        var closeStation = MakeDecode("K1CLOSE", WsjtxClient.CallCategory.DEFAULT, distance: 200, sequenceNumber: 5);
        var farStation   = MakeDecode("K2FAR",   WsjtxClient.CallCategory.DEFAULT, distance: 3000, sequenceNumber: 5);
        ranker.SetRank(closeStation);
        ranker.SetRank(farStation);

        Check("Tied primary sort (equal SequenceNumber) produces equal Rank",
              closeStation.Rank == farStation.Rank, true);
        int cmp = ranker.Compare(closeStation, farStation, null, false);
        Check("Compare: closer station (DIST_INCR secondary) sorts before farther one", cmp < 0, true);

        int cmpRank = ranker.CompareRank(farStation, closeStation, null, false);
        Check("CompareRank: mirrors Compare's tiebreak direction", cmpRank < 0, true);

        // Final fallback: a single-method order list (DIST_INCR only, no secondary) means two
        // same-distance entries tie on the only configured method, with no more tiebreakers left
        // to apply -- only then does the final SequenceNumber fallback actually decide the order.
        ranker.ApplySortOrder(new List<WsjtxClient.RankMethods> { WsjtxClient.RankMethods.DIST_INCR }, null);
        var first = MakeDecode("K3FIRST", WsjtxClient.CallCategory.DEFAULT, distance: 500, sequenceNumber: 1);
        var second = MakeDecode("K4SECOND", WsjtxClient.CallCategory.DEFAULT, distance: 500, sequenceNumber: 2);
        ranker.SetRank(first);
        ranker.SetRank(second);
        Check("Same-distance entries tie on the only configured sort method", first.Rank == second.Rank, true);
        int cmpFinal = ranker.Compare(first, second, null, false);
        Check("Final CALL_ORDER fallback: identical primary, older SequenceNumber sorts first", cmpFinal < 0, true);
    }

    static void CallQueueRankerCategoryWeightValidationTests()
    {
        Console.WriteLine("\n── CallQueueRanker: ApplyCategoryWeights Validation ──");
        var ranker = new CallQueueRanker();
        var originalDefault = new Dictionary<WsjtxClient.CallCategory, int>(ranker.categoryWeight);

        Check("ApplyCategoryWeights(null) is rejected", ranker.ApplyCategoryWeights(null), false);

        var badWeights = new Dictionary<WsjtxClient.CallCategory, int> { { WsjtxClient.CallCategory.DEFAULT, 1 } };
        Check("ApplyCategoryWeights with DEFAULT != 0 is rejected", ranker.ApplyCategoryWeights(badWeights), false);
        Check("Rejected weights table leaves categoryWeight unchanged",
              ranker.categoryWeight[WsjtxClient.CallCategory.TO_MYCALL] == originalDefault[WsjtxClient.CallCategory.TO_MYCALL], true);

        // Partial table (old INI with missing keys, e.g. a config saved before STILL_NEEDED existed):
        // missing entries should be merged in from the current defaults, not left absent.
        var partialWeights = new Dictionary<WsjtxClient.CallCategory, int>
        {
            { WsjtxClient.CallCategory.DEFAULT, 0 },
            { WsjtxClient.CallCategory.TO_MYCALL, 9 },
        };
        Check("ApplyCategoryWeights with a valid partial table is accepted", ranker.ApplyCategoryWeights(partialWeights), true);
        Check("Explicit override value is applied", ranker.categoryWeight[WsjtxClient.CallCategory.TO_MYCALL] == 9, true);
        Check("Missing key (STILL_NEEDED) is merged in with its prior default",
              ranker.categoryWeight.ContainsKey(WsjtxClient.CallCategory.STILL_NEEDED), true);
    }

    static void CallQueueRankerCallingPrioritiesTests()
    {
        Console.WriteLine("\n── CallQueueRanker: ApplyCallingPriorities / IsCallingEnabled ──");
        var ranker = new CallQueueRanker();

        ranker.ApplyCallingPriorities(new List<WsjtxClient.CallCategory> { WsjtxClient.CallCategory.TO_MYCALL });
        Check("IsCallingEnabled: TO_MYCALL enabled after explicit list", ranker.IsCallingEnabled(WsjtxClient.CallCategory.TO_MYCALL), true);
        Check("IsCallingEnabled: WANTED_CQ NOT enabled (excluded from explicit list)", ranker.IsCallingEnabled(WsjtxClient.CallCategory.WANTED_CQ), false);

        // POTA/SOTA/MANUAL_SEL are hidden from the Call Filters UI; they follow WANTED_CQ's admission.
        ranker.ApplyCallingPriorities(new List<WsjtxClient.CallCategory> { WsjtxClient.CallCategory.WANTED_CQ });
        Check("IsCallingEnabled: POTA follows WANTED_CQ admission", ranker.IsCallingEnabled(WsjtxClient.CallCategory.POTA), true);
        Check("IsCallingEnabled: SOTA follows WANTED_CQ admission", ranker.IsCallingEnabled(WsjtxClient.CallCategory.SOTA), true);

        // null restores the documented default list.
        ranker.ApplyCallingPriorities(null);
        Check("ApplyCallingPriorities(null): default includes TO_MYCALL", ranker.IsCallingEnabled(WsjtxClient.CallCategory.TO_MYCALL), true);
        Check("ApplyCallingPriorities(null): default includes DEFAULT", ranker.IsCallingEnabled(WsjtxClient.CallCategory.DEFAULT), true);
    }

    static void CallQueueRankerBeamRankTests()
    {
        Console.WriteLine("\n── CallQueueRanker: Beam (Azimuth) Ranking ──");
        var ranker = new CallQueueRanker();

        Check("CalcAzRank: unknown azimuth (-1) is off-beam", ranker.CalcAzRank(-1) == CallQueueRanker.OffBeamRank, true);

        // AZ_NQUAD points at heading 0; BeamWidth defaults to 90 (±45).
        ranker.ApplySortOrder(new List<WsjtxClient.RankMethods> { WsjtxClient.RankMethods.MOST_RECENT }, WsjtxClient.RankMethods.AZ_NQUAD);
        Check("CalcAzRank: azimuth exactly on heading is the best (closest to zero) in-beam rank",
              ranker.CalcAzRank(0) == 0, true);
        Check("CalcAzRank: azimuth just outside the beam window is off-beam",
              ranker.CalcAzRank(0 + CallQueueRanker.BeamWidth / 2 + 1) == CallQueueRanker.OffBeamRank, true);

        // SetRank with a beam method set on a DEFAULT-category message routes through CalcAzRank.
        var onBeam  = MakeDecode("K1BEAM", WsjtxClient.CallCategory.DEFAULT, azimuth: 0);
        var offBeam = MakeDecode("K2BEAM", WsjtxClient.CallCategory.DEFAULT, azimuth: 180);
        ranker.SetRank(onBeam);
        ranker.SetRank(offBeam);
        Check("SetRank: on-beam station ranks above an off-beam station", onBeam.Rank > offBeam.Rank, true);
    }

    // ── JimmySettings: Advanced Call Layout flags (Phase 2.1 first slice) ────────
    static void JimmySettingsRoundTripTests()
    {
        Console.WriteLine("\n── JimmySettings: Load/Save Round-trip ──");
        string tmpIni = Path.Combine(Path.GetTempPath(), "JimmyTest_Settings_" + Guid.NewGuid().ToString("N") + ".ini");
        try
        {
            var saved = new JimmySettings
            {
                AdvancedCallLayout = false,
                AdvShowTx1 = false,
                AdvShowTx2 = true,
                AdvShowRaw = false,
                ListFontSize = 14,
                ListBackColor = System.Drawing.Color.FromArgb(30, 30, 30),
                ListForeColor = System.Drawing.Color.FromArgb(220, 220, 220),
                ListAltRowColor = System.Drawing.Color.FromArgb(45, 45, 45),
            };
            var ini = new IniFile(tmpIni);
            saved.SaveToIni(ini);

            var loaded = new JimmySettings();
            loaded.LoadFromIni(ini);

            Check("Round-trip: AdvancedCallLayout", loaded.AdvancedCallLayout, saved.AdvancedCallLayout);
            Check("Round-trip: AdvShowTx1", loaded.AdvShowTx1, saved.AdvShowTx1);
            Check("Round-trip: AdvShowTx2", loaded.AdvShowTx2, saved.AdvShowTx2);
            Check("Round-trip: AdvShowRaw", loaded.AdvShowRaw, saved.AdvShowRaw);
            Check("Round-trip: ListFontSize", loaded.ListFontSize == saved.ListFontSize, true);
            Check("Round-trip: ListBackColor", loaded.ListBackColor.ToArgb() == saved.ListBackColor.ToArgb(), true);
            Check("Round-trip: ListForeColor", loaded.ListForeColor.ToArgb() == saved.ListForeColor.ToArgb(), true);
            Check("Round-trip: ListAltRowColor", loaded.ListAltRowColor.ToArgb() == saved.ListAltRowColor.ToArgb(), true);
        }
        finally
        {
            try { File.Delete(tmpIni); } catch { }
        }
    }

    static void JimmySettingsDefaultsTests()
    {
        Console.WriteLine("\n── JimmySettings: Missing-key Defaults (matches prior inline Form_Load behavior) ──");
        string tmpIni = Path.Combine(Path.GetTempPath(), "JimmyTest_SettingsDefaults_" + Guid.NewGuid().ToString("N") + ".ini");
        try
        {
            // Fresh/never-written INI file -- every key read returns "".
            var ini = new IniFile(tmpIni);
            var settings = new JimmySettings();
            settings.LoadFromIni(ini);

            // Preserves a pre-existing quirk: AdvancedCallLayout reads as `== "True"` (missing
            // key -> false), while the other three read as `!= "False"` (missing key -> true).
            // This mismatch already existed in Controller.Form_Load; not "fixed" here.
            Check("Missing advCallLayout key -> AdvancedCallLayout defaults false", settings.AdvancedCallLayout, false);
            Check("Missing advShowTx1 key -> AdvShowTx1 defaults true", settings.AdvShowTx1, true);
            Check("Missing advShowTx2 key -> AdvShowTx2 defaults true", settings.AdvShowTx2, true);
            Check("Missing advShowRaw key -> AdvShowRaw defaults true", settings.AdvShowRaw, true);
        }
        finally
        {
            try { File.Delete(tmpIni); } catch { }
        }
    }

    // ── EngineRestartPolicy (bounded native-engine auto-restart, extracted from Controller) ──

    static void EngineRestartPolicyTests()
    {
        Console.WriteLine("\n── EngineRestartPolicy: bounded rolling-window auto-restart ──");

        // Fake clock: starts at a fixed instant and only advances when the test tells it to --
        // makes the 5-minute window boundary exactly testable without a real sleep.
        DateTime clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var policy = new EngineRestartPolicy(5, TimeSpan.FromMinutes(5), () => clock);

        for (int i = 1; i <= 5; i++)
        {
            bool should = policy.RecordAttemptAndShouldRestart(out int attemptNumber);
            Check($"Attempt {i} within budget -> should restart", should, true);
            Check($"Attempt {i} reports attemptNumber {i}", attemptNumber == i, true);
            clock = clock.AddSeconds(1); // crashes seconds apart, same window
        }

        bool sixth = policy.RecordAttemptAndShouldRestart(out int sixthAttemptNumber);
        Check("6th attempt within the same 5-minute window -> gives up", sixth, false);
        Check("6th attempt still reports attemptNumber 6 (count keeps accumulating)", sixthAttemptNumber == 6, true);

        bool seventh = policy.RecordAttemptAndShouldRestart(out _);
        Check("7th attempt, still same window -> still gives up (not a one-shot deny)", seventh, false);

        // Window elapses -- a persistently-flaky-but-not-crash-looping engine gets a fresh budget,
        // matching the original inline comment's own reasoning (a rolling window, not a lifetime cap).
        clock = clock.AddMinutes(6);
        bool afterWindow = policy.RecordAttemptAndShouldRestart(out int freshAttemptNumber);
        Check("Attempt after the window elapses -> budget resets, should restart again", afterWindow, true);
        Check("First attempt in the new window reports attemptNumber 1", freshAttemptNumber == 1, true);
    }

    // ── NativeEngineClient.IsValidGridFormat (fresh-install audit, 2026-08-18: a malformed but
    //    non-empty grid used to launch the engine silently -- see NativeEngineClient.Launch's own
    //    comment) ──

    static void NativeEngineClientGridValidationTests()
    {
        Console.WriteLine("\n── NativeEngineClient.IsValidGridFormat: Maidenhead locator shape check ──");

        Check("4-char locator, standard case (field upper, square digits) -> valid", NativeEngineClient.IsValidGridFormat("FN42"), true);
        Check("6-char locator, standard case (subsquare lower) -> valid", NativeEngineClient.IsValidGridFormat("FN42ab"), true);
        Check("4-char locator, all lowercase -> valid (case-insensitive)", NativeEngineClient.IsValidGridFormat("fn42"), true);
        Check("6-char locator, all uppercase -> valid (case-insensitive)", NativeEngineClient.IsValidGridFormat("FN42AB"), true);
        Check("Field letters at the top of the real A-R range (RR99) -> valid", NativeEngineClient.IsValidGridFormat("RR99"), true);
        Check("Subsquare letters at the top of the real A-X range -> valid", NativeEngineClient.IsValidGridFormat("FN42xx"), true);

        Check("Empty string -> invalid", NativeEngineClient.IsValidGridFormat(""), false);
        Check("null -> invalid", NativeEngineClient.IsValidGridFormat(null), false);
        Check("Too short (3 chars) -> invalid", NativeEngineClient.IsValidGridFormat("FN4"), false);
        Check("Too long (5 chars) -> invalid", NativeEngineClient.IsValidGridFormat("FN42a"), false);
        Check("Field letter past 'R' (S is out of range) -> invalid", NativeEngineClient.IsValidGridFormat("SN42"), false);
        Check("Square position not digits -> invalid", NativeEngineClient.IsValidGridFormat("FNAB"), false);
        Check("Subsquare letter past 'X' (Y is out of range) -> invalid", NativeEngineClient.IsValidGridFormat("FN42yy"), false);
        Check("Subsquare position not letters -> invalid", NativeEngineClient.IsValidGridFormat("FN4212"), false);
        Check("Garbage input entirely -> invalid", NativeEngineClient.IsValidGridFormat("ABC123"), false);
    }

    // ── NativeEngineClient.DescribeConfigProblem (fresh-install release blocker, 2026-08-19:
    //    single source of truth Controller.ApplyEngineMode's pre-check and Launch()'s own safety
    //    net both use, so a genuinely unconfigured install gets a calm, plain-language, no-
    //    "native engine"-jargon message instead of the old Error-severity ErrorWarningEvent) ──

    // ── Independent audit finding 7, 2026-08-23 (HARDENING GAP): update download refuses a
    // non-HTTPS URL or an unexpected host BEFORE any download begins -- THE FIX ──
    static void UpdateCheckerDownloadHostValidationTests()
    {
        Console.WriteLine("\n── Finding 7 fix: update download rejects non-HTTPS/unexpected hosts -- THE FIX ──");
        try
        {
            // These must all fail fast on validation, before any real network I/O -- run
            // synchronously with a short overall guard so a bug that somehow let one reach the
            // network doesn't hang the suite.
            void ExpectRejected(string url, string label)
            {
                bool threw = false;
                try
                {
                    UpdateChecker.DownloadToTempAsync(url, "JimmyNext.msi").GetAwaiter().GetResult();
                }
                catch (InvalidOperationException) { threw = true; }
                catch { threw = true; } // any other exception (e.g. a real connection attempt failing) still proves it didn't succeed
                Check($"THE FIX: {label}", threw, true);
            }

            ExpectRejected("http://github.com/jimr9/Jimmy/releases/download/v1.0/Jimmy.msi", "plain HTTP is rejected, not just HTTPS preferred");
            ExpectRejected("https://evil.example.com/Jimmy.msi", "an unexpected host is rejected even over HTTPS");
            ExpectRejected(null, "a null URL is rejected");
            ExpectRejected("", "an empty URL is rejected");
            ExpectRejected("not a url", "a malformed URL is rejected");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  UpdateCheckerDownloadHostValidationTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    static void UpdateCheckerNotesTests()
    {
        Console.WriteLine("\n── UpdateChecker.ParseLatestReleaseJson: accessible \"what's new\" notes ──");
        try
        {
            const string current = "2.0.54";
            string assets = "\"assets\":[{\"name\":\"JimmyNext.msi\",\"browser_download_url\":"
                + "\"https://github.com/Jimr9/JimmyNext/releases/download/v2.0.99/JimmyNext.msi\"}]";

            string Json(string bodyClause) =>
                "{\"tag_name\":\"v2.0.99\",\"published_at\":\"2026-08-31T12:00:00Z\","
                + bodyClause + assets + "}";

            // body present: notes populated, CRLF normalized, markdown flattened
            var withBody = UpdateChecker.ParseLatestReleaseJson(
                Json("\"body\":\"## What's new\\r\\n\\r\\n- Fixed a thing\\r\\n- **Bold** item\\r\\n\\r\\nDone.\","),
                current);
            Check("newer release is detected", withBody != null && withBody.Version == "2.0.99", true);
            Check("Notes is populated when body present", !string.IsNullOrEmpty(withBody?.Notes), true);
            Check("Notes has no carriage returns (normalized to \\n)", withBody != null && !withBody.Notes.Contains("\r"), true);
            Check("Notes strips '#' heading markers", withBody != null && !withBody.Notes.Contains("#"), true);
            Check("Notes keeps heading text", withBody != null && withBody.Notes.Contains("What's new"), true);
            Check("Notes converts '- ' bullets to '• '", withBody != null && withBody.Notes.Contains("• Fixed a thing"), true);
            Check("Notes strips '**' bold markers", withBody != null && !withBody.Notes.Contains("**"), true);

            // body absent: UpdateInfo still returned, Notes null
            var noBody = UpdateChecker.ParseLatestReleaseJson(Json(""), current);
            Check("update still offered when body absent", noBody != null && noBody.Version == "2.0.99", true);
            Check("Notes is null when body absent", noBody != null && noBody.Notes == null, true);

            // body empty / whitespace-only: Notes null
            var emptyBody = UpdateChecker.ParseLatestReleaseJson(Json("\"body\":\"\","), current);
            Check("Notes is null when body is empty string", emptyBody != null && emptyBody.Notes == null, true);
            var wsBody = UpdateChecker.ParseLatestReleaseJson(Json("\"body\":\"   \\n  \\n\","), current);
            Check("Notes is null when body is whitespace only", wsBody != null && wsBody.Notes == null, true);

            // oversized body: truncated with GitHub marker, well under the raw size
            string huge = new string('x', 20000);
            var bigBody = UpdateChecker.ParseLatestReleaseJson(Json($"\"body\":\"{huge}\","), current);
            Check("oversized Notes is truncated", bigBody != null && bigBody.Notes.Length < 20000, true);
            Check("oversized Notes ends with GitHub marker",
                bigBody != null && bigBody.Notes.EndsWith("(full notes on GitHub)"), true);

            // malformed JSON: null, no throw
            bool threw = false;
            UpdateInfo bad = null;
            try { bad = UpdateChecker.ParseLatestReleaseJson("{ this is not json", current); }
            catch { threw = true; }
            Check("malformed JSON returns null without throwing", !threw && bad == null, true);

            // already up to date: null even though body is present
            var upToDate = UpdateChecker.ParseLatestReleaseJson(
                "{\"tag_name\":\"v2.0.54\",\"body\":\"notes\"," + assets + "}", current);
            Check("no update when already current", upToDate == null, true);

            // SanitizeNotes directly: null/whitespace -> null
            Check("SanitizeNotes(null) -> null", UpdateChecker.SanitizeNotes(null) == null, true);
            Check("SanitizeNotes(\"  \") -> null", UpdateChecker.SanitizeNotes("   \n\t ") == null, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  UpdateCheckerNotesTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    static void NativeEngineClientDescribeConfigProblemTests()
    {
        Console.WriteLine("\n── NativeEngineClient.DescribeConfigProblem: plain-language config gate ──");

        Check("Valid call+grid -> no problem (null)", NativeEngineClient.DescribeConfigProblem("KB0UZT", "FN42") == null, true);
        Check("Valid call+grid, 6-char grid -> no problem (null)", NativeEngineClient.DescribeConfigProblem("KB0UZT", "FN42ab") == null, true);

        Check("Empty call -> the exact required plain-language wording, naming the Decode Engine tab",
            NativeEngineClient.DescribeConfigProblem("", "FN42") == "Set your callsign and grid on the Decode Engine tab in Options to begin operating.", true);
        Check("Empty grid -> the same plain-language wording",
            NativeEngineClient.DescribeConfigProblem("KB0UZT", "") == "Set your callsign and grid on the Decode Engine tab in Options to begin operating.", true);
        Check("Both blank (fresh-install defaults) -> the same plain-language wording",
            NativeEngineClient.DescribeConfigProblem("", "") == "Set your callsign and grid on the Decode Engine tab in Options to begin operating.", true);
        Check("Whitespace-only call/grid -> treated the same as empty",
            NativeEngineClient.DescribeConfigProblem("   ", "   ") == "Set your callsign and grid on the Decode Engine tab in Options to begin operating.", true);

        Check("Malformed grid -> a plain-language message naming the bad value and the Decode Engine tab",
            NativeEngineClient.DescribeConfigProblem("KB0UZT", "ABC123") == "'ABC123' isn't a valid grid square (e.g. FN42 or FN42ab) -- fix it on the Decode Engine tab in Options to begin operating.", true);

        // No call site (Launch's LastError, Controller.ApplyEngineMode's status message) should
        // ever be able to say "native engine" for this specific, normal, expected first-run
        // condition -- the operator should never need to know that concept exists.
        Check("Missing-config message never mentions 'native engine'",
            !NativeEngineClient.DescribeConfigProblem("", "").ToLowerInvariant().Contains("native engine"), true);
        Check("Malformed-grid message never mentions 'native engine'",
            !NativeEngineClient.DescribeConfigProblem("KB0UZT", "ABC123").ToLowerInvariant().Contains("native engine"), true);

        // Project owner feedback, 2026-08-19: names the destination area, never a hotkey/input
        // method -- Options is also reachable by mouse, so "press Alt+O" would be both jargon-
        // adjacent and simply wrong for someone not using that specific hotkey.
        Check("Missing-config message names the Decode Engine tab, not a hotkey",
            NativeEngineClient.DescribeConfigProblem("", "").Contains("Decode Engine")
                && !NativeEngineClient.DescribeConfigProblem("", "").ToLowerInvariant().Contains("alt+o")
                && !NativeEngineClient.DescribeConfigProblem("", "").ToLowerInvariant().Contains("press "), true);
    }

    // ── Repeat limit / TX watchdog authority split, 2026-08-24: NativeEngineClient.
    // ComputeAutomaticTxWatchdogMinutes -- the Automatic safety-backstop formula ──
    // Pure-function coverage for the exact agreed calculation: (RepeatLimit + 2 attempt-cycles
    // margin) * 30s/attempt (FT8 basis, used unconditionally), ceiling-rounded to whole minutes,
    // clamped to [2, 30]. See the method's own comment for the full reasoning this locks in.
    static void NativeEngineClientTxWatchdogFormulaTests()
    {
        Console.WriteLine("\n── NativeEngineClient.ComputeAutomaticTxWatchdogMinutes: Automatic watchdog formula ──");

        // RepeatLimit=1: (1+2)*30=90s=1.5min -> ceil 2min -> floor doesn't change it.
        Check("RepeatLimit=1 -> 2 minutes",
            NativeEngineClient.ComputeAutomaticTxWatchdogMinutes(1) == 2, true);
        // RepeatLimit=3 (the exact real-launch reproduction value): (3+2)*30=150s=2.5min -> ceil 3min.
        Check("RepeatLimit=3 -> 3 minutes (the exact value from the real-launch reproduction)",
            NativeEngineClient.ComputeAutomaticTxWatchdogMinutes(3) == 3, true);
        // RepeatLimit=20 (Controller.cs's own maxSkipCount, the real enforced UI ceiling):
        // (20+2)*30=660s=11min exactly.
        Check("RepeatLimit=20 (the real UI ceiling) -> 11 minutes",
            NativeEngineClient.ComputeAutomaticTxWatchdogMinutes(20) == 11, true);
        // Never below the 2-minute floor, even at the smallest possible RepeatLimit.
        Check("Never below the 2-minute floor",
            NativeEngineClient.ComputeAutomaticTxWatchdogMinutes(0) >= NativeEngineClient.TxWatchdogMinMinutes, true);
        // Defensive outer cap -- confirms the clamp itself works even for a value far outside
        // Repeat Limit's own real UI range, in case that range is ever raised later.
        Check("Clamped at the 30-minute defensive ceiling for a hypothetically much larger RepeatLimit",
            NativeEngineClient.ComputeAutomaticTxWatchdogMinutes(1000) == NativeEngineClient.TxWatchdogMaxMinutes, true);
        // Never 0 -- Nexus's own code treats tx_watchdog_min=0 as "watchdog disabled entirely"
        // (engine.rs: "if limit_secs > 0"), which Automatic must never compute.
        Check("Never computes exactly 0 (which would disable the watchdog entirely)",
            NativeEngineClient.ComputeAutomaticTxWatchdogMinutes(0) != 0, true);
    }

    // ── OtaSpotAnnotator (POTA/SOTA spot -> Jimmy's own worked-before/needed-award facts) ──

    static void OtaSpotAnnotatorTests()
    {
        Console.WriteLine("\n── OtaSpotAnnotator: applies Jimmy's own award/logbook intelligence ──");
        string tmpDb = Path.Combine(Path.GetTempPath(),
            "JimmyTest_OtaAnnotate_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = new LogbookDb(tmpDb))
            {
                InsertQso(db, "W1WORKED", "TX", dxcc: 291, zone: 5, band: "20m");

                var tags = new Dictionary<string, WsjtxClient.ActiveAwardTag>
                {
                    ["r1"] = new WsjtxClient.ActiveAwardTag { RuleId = "r1", RuleName = "Award One", GroupBy = RuleGroupBy.Callsign, Set = new HashSet<string> { "K1ABC" } },
                    ["r2"] = new WsjtxClient.ActiveAwardTag { RuleId = "r2", RuleName = "Award Two", GroupBy = RuleGroupBy.Callsign, Set = new HashSet<string> { "K1ABC" } },
                    ["r3"] = new WsjtxClient.ActiveAwardTag { RuleId = "r3", RuleName = "Award Three (no match)", GroupBy = RuleGroupBy.Callsign, Set = new HashSet<string> { "SOMEONE-ELSE" } },
                    ["r4"] = new WsjtxClient.ActiveAwardTag { RuleId = "r4", RuleName = "Empty set is skipped", GroupBy = RuleGroupBy.Callsign, Set = new HashSet<string>() },
                };

                var needed = OtaSpotAnnotator.Annotate("K1ABC", "20m", db, null, tags);
                Check("K1ABC not previously worked -> WorkedBefore false", needed.WorkedBefore, false);
                Check("K1ABC matches exactly 2 active awards -> NeededForAwardCount 2", needed.NeededForAwardCount == 2, true);

                var worked = OtaSpotAnnotator.Annotate("W1WORKED", "20m", db, null, tags);
                Check("W1WORKED already logged on 20m -> WorkedBefore true", worked.WorkedBefore, true);
                Check("W1WORKED matches no active award (not in any Set) -> NeededForAwardCount 0", worked.NeededForAwardCount == 0, true);

                var noAwards = OtaSpotAnnotator.Annotate("K1ABC", "20m", db, null, null);
                Check("Null activeAwardTags -> NeededForAwardCount 0, not a crash", noAwards.NeededForAwardCount == 0, true);

                var empty = OtaSpotAnnotator.Annotate("", "20m", db, null, tags);
                Check("Empty callsign -> WorkedBefore false (default, no lookup attempted)", empty.WorkedBefore, false);
                Check("Empty callsign -> NeededForAwardCount 0 (default, no lookup attempted)", empty.NeededForAwardCount == 0, true);
            }
        }
        finally
        {
            try { File.Delete(tmpDb); } catch { }
        }
    }

    // ── Notification architecture (WSJTX_Controller/Notify/) ──────────────────────────

    static void NotificationTemplateEngineTests()
    {
        Console.WriteLine("\n── NotificationTemplateEngine: Format/Pluralize ──");

        CheckStr("Format: simple substitution",
            NotificationTemplateEngine.Format("Working {Callsign}",
                new Dictionary<string, string> { ["Callsign"] = "K4YT" }),
            "Working K4YT");

        CheckStr("Format: multiple tokens",
            NotificationTemplateEngine.Format("{Callsign}, {AwardSummary}",
                new Dictionary<string, string> { ["Callsign"] = "K4YT", ["AwardSummary"] = "1 award needed" }),
            "K4YT, 1 award needed");

        CheckStr("Format: unknown token left verbatim, never throws",
            NotificationTemplateEngine.Format("Hi {Nope}", new Dictionary<string, string>()),
            "Hi {Nope}");

        CheckStr("Format: null template -> empty string",
            NotificationTemplateEngine.Format(null, null), "");

        CheckStr("Format: empty template -> empty string",
            NotificationTemplateEngine.Format("", new Dictionary<string, string>()), "");

        CheckStr("Format: null tokens dictionary with a token present -> left verbatim, never throws",
            NotificationTemplateEngine.Format("Hi {Callsign}", null), "Hi {Callsign}");

        CheckStr("Format: unmatched opening brace treated as literal text",
            NotificationTemplateEngine.Format("Hi {Callsign",
                new Dictionary<string, string> { ["Callsign"] = "K4YT" }),
            "Hi {Callsign");

        CheckStr("Format: no tokens in template at all",
            NotificationTemplateEngine.Format("WSJT-X closed", new Dictionary<string, string>()),
            "WSJT-X closed");

        CheckStr("Pluralize: singular", NotificationTemplateEngine.Pluralize(1, "award needed"), "1 award needed");
        CheckStr("Pluralize: default plural adds 's'", NotificationTemplateEngine.Pluralize(2, "award needed"), "2 award neededs");
        CheckStr("Pluralize: explicit plural form", NotificationTemplateEngine.Pluralize(2, "award needed", "awards needed"), "2 awards needed");
        CheckStr("Pluralize: zero uses plural form", NotificationTemplateEngine.Pluralize(0, "award needed", "awards needed"), "0 awards needed");
    }

    static void NotificationSettingsDefaultsTests()
    {
        Console.WriteLine("\n── NotificationSettings: Missing/invalid-key Defaults ──");
        string tmpIni = Path.Combine(Path.GetTempPath(), "JimmyTest_NotifyDefaults_" + Guid.NewGuid().ToString("N") + ".ini");
        try
        {
            // Fresh/never-written INI file -- every key read returns "".
            var ini = new IniFile(tmpIni);
            var settings = new NotificationSettings();
            settings.LoadFromIni(ini);

            var codeDefault = NotificationDefaults.Policies[NotificationEventType.QsoCompleted];
            var loaded = settings.Policies[NotificationEventType.QsoCompleted];
            Check("Missing keys -> Enabled matches code default", loaded.Enabled, codeDefault.Enabled);
            Check("Missing keys -> Priority matches code default", loaded.Priority == codeDefault.Priority, true);
            Check("Missing keys -> RepeatSeconds matches code default", loaded.RepeatSeconds == codeDefault.RepeatSeconds, true);
            CheckStr("Missing keys -> Template matches code default", loaded.Template, codeDefault.Template);

            // Every enum member must have a code default -- a new type added later with no
            // NotificationDefaults entry would silently vanish from Policies on load, which
            // would be a real bug, not a "new type, no override yet" situation.
            bool allTypesPresent = true;
            foreach (NotificationEventType type in Enum.GetValues(typeof(NotificationEventType)))
                if (!settings.Policies.ContainsKey(type)) allTypesPresent = false;
            Check("Every NotificationEventType has a policy after load", allTypesPresent, true);

            // Invalid values must fall back to the code default, never throw.
            ini.Write("notifyRepeatSeconds_QsoCompleted", "not-a-number");
            ini.Write("notifyPriority_ErrorWarning", "Bogus");
            ini.Write("notifyThrottleMs_AwardsNeeded", "-5");
            var settings2 = new NotificationSettings();
            settings2.LoadFromIni(ini);
            Check("Invalid int value falls back to code default",
                settings2.Policies[NotificationEventType.QsoCompleted].RepeatSeconds
                    == NotificationDefaults.Policies[NotificationEventType.QsoCompleted].RepeatSeconds, true);
            Check("Invalid enum value falls back to code default",
                settings2.Policies[NotificationEventType.ErrorWarning].Priority == NotificationDefaults.Policies[NotificationEventType.ErrorWarning].Priority, true);
            Check("Negative throttle value falls back to code default",
                settings2.Policies[NotificationEventType.AwardsNeeded].ThrottleMilliseconds
                    == NotificationDefaults.Policies[NotificationEventType.AwardsNeeded].ThrottleMilliseconds, true);
        }
        finally
        {
            try { File.Delete(tmpIni); } catch { }
        }
    }

    static void NotificationSettingsRoundTripTests()
    {
        Console.WriteLine("\n── NotificationSettings: Load/Save Round-trip + valid overrides ──");
        string tmpIni = Path.Combine(Path.GetTempPath(), "JimmyTest_NotifyRoundTrip_" + Guid.NewGuid().ToString("N") + ".ini");
        try
        {
            var ini = new IniFile(tmpIni);
            var saved = new NotificationSettings();
            saved.Policies[NotificationEventType.QsoStarted].Enabled = false;
            saved.Policies[NotificationEventType.QsoStarted].Priority = NotificationPriority.Important;
            saved.Policies[NotificationEventType.QsoStarted].RepeatSeconds = 42;
            saved.Policies[NotificationEventType.QsoStarted].ThrottleMilliseconds = 1500;
            saved.Policies[NotificationEventType.QsoStarted].Template = "On {Callsign}";
            saved.SaveToIni(ini);

            var loaded = new NotificationSettings();
            loaded.LoadFromIni(ini);
            var p = loaded.Policies[NotificationEventType.QsoStarted];
            Check("Round-trip: Enabled", p.Enabled, false);
            Check("Round-trip: Priority", p.Priority == NotificationPriority.Important, true);
            Check("Round-trip: RepeatSeconds", p.RepeatSeconds == 42, true);
            Check("Round-trip: ThrottleMilliseconds", p.ThrottleMilliseconds == 1500, true);
            CheckStr("Round-trip: Template override honored", p.Template, "On {Callsign}");

            // A type not explicitly touched must still round-trip its own (code-default) values,
            // proving SaveToIni writes every type, not just ones the caller modified.
            var untouched = loaded.Policies[NotificationEventType.ConnectionClosed];
            var untouchedDefault = NotificationDefaults.Policies[NotificationEventType.ConnectionClosed];
            CheckStr("Untouched type's template still round-trips", untouched.Template, untouchedDefault.Template);
        }
        finally
        {
            try { File.Delete(tmpIni); } catch { }
        }
    }

    static void NotificationDedupThrottleTests()
    {
        Console.WriteLine("\n── NotificationDedupThrottle: dedup + throttle ──");

        var repeatPolicy = new NotificationPolicy { RepeatSeconds = 30, ThrottleMilliseconds = 0 };
        var d = new NotificationDedupThrottle();
        Check("First fire for a fresh identity is allowed",
            d.ShouldAnnounce(NotificationEventType.QsoStarted, "K4YT", repeatPolicy), true);
        d.RecordFired(NotificationEventType.QsoStarted, "K4YT");
        Check("Immediate repeat for the same identity is suppressed",
            d.ShouldAnnounce(NotificationEventType.QsoStarted, "K4YT", repeatPolicy), false);
        Check("A different identity is not suppressed by another identity's cooldown",
            d.ShouldAnnounce(NotificationEventType.QsoStarted, "W1AW", repeatPolicy), true);
        Check("A different event type for the same identity is not suppressed",
            d.ShouldAnnounce(NotificationEventType.QsoCompleted, "K4YT", repeatPolicy), true);

        var throttlePolicy = new NotificationPolicy { RepeatSeconds = 0, ThrottleMilliseconds = 60000 };
        var t = new NotificationDedupThrottle();
        Check("First fire under a throttle policy is allowed",
            t.ShouldAnnounce(NotificationEventType.AwardsNeeded, "K4YT", throttlePolicy), true);
        t.RecordFired(NotificationEventType.AwardsNeeded, "K4YT");
        Check("A DIFFERENT identity is still throttled by the same event type's global spacing",
            t.ShouldAnnounce(NotificationEventType.AwardsNeeded, "W1AW", throttlePolicy), false);

        var noLimitsPolicy = new NotificationPolicy { RepeatSeconds = 0, ThrottleMilliseconds = 0 };
        var n = new NotificationDedupThrottle();
        n.RecordFired(NotificationEventType.QsoStarted, "K4YT");
        Check("RepeatSeconds=0 and ThrottleMilliseconds=0 means never suppressed",
            n.ShouldAnnounce(NotificationEventType.QsoStarted, "K4YT", noLimitsPolicy), true);
    }

    // Test-only INotificationDelivery: records what would have been announced instead of
    // touching any real UI, so NotificationCenter.Publish's policy/dedup/format/deliver
    // pipeline can be exercised end-to-end without a running Jimmy window.
    private class FakeNotificationDelivery : INotificationDelivery
    {
        public string LastText;
        public bool? LastImportant;
        public int AnnounceCount;

        public void Announce(string text, bool important)
        {
            LastText = text;
            LastImportant = important;
            AnnounceCount++;
        }
    }

    // Test-only IJimmyStatusView: lets UiaAlertNotificationDeliveryTests drive WouldAnnounce and
    // observe RaiseAccessibleAlert without a real Form/statusText/screen reader -- this is
    // exactly the boundary "do not attempt to unit-test JAWS or NVDA speech" draws: everything
    // on Jimmy's own side of that boundary (the gating logic) is real and tested; the actual
    // UIA call and whatever an AT does with it is not (see the manual test checklist instead).
    private class FakeStatusView : IJimmyStatusView
    {
        public bool WouldAnnounceValue;
        public string LastAccessibleAlert;
        public int AccessibleAlertCount;
        public string LastStatusText;
        public int RenderStatusCount;

        public void RenderStatus(string headingText, string statusText, System.Drawing.Color foreColor, System.Drawing.Color backColor)
        {
            LastStatusText = statusText;
            RenderStatusCount++;
        }
        public string LastShowMessageText;
        public int ShowMessageCount;
        public void ShowMessage(string text, bool sound)
        {
            LastShowMessageText = text;
            ShowMessageCount++;
        }
        public bool WouldAnnounce => WouldAnnounceValue;
        public void RaiseAccessibleAlert(string text)
        {
            LastAccessibleAlert = text;
            AccessibleAlertCount++;
        }
    }

    // ── UiaAlertNotificationDelivery: off-focus accessibility alert gating, 2026-08-19 ──────
    // The decorator's own policy logic in isolation -- important+enabled+not-already-announcing
    // is the ENTIRE gate (see UiaAlertNotificationDelivery's own comment: no second, separately
    // maintained list of "which events count", no duplicate-suppression state of its own beyond
    // the WouldAnnounce check). Proves: the inner delivery (status field) always fires regardless
    // of any of this; a Normal-priority announcement never raises a UIA alert; the feature being
    // off (default) suppresses it even for an Important one; and the one duplicate-avoidance
    // check (WouldAnnounce) actually gates it.
    static void UiaAlertNotificationDeliveryTests()
    {
        Console.WriteLine("\n── UiaAlertNotificationDelivery: off-focus accessibility alert gating ──");
        try
        {
            var inner = new FakeNotificationDelivery();
            var statusView = new FakeStatusView();
            bool enabled = true;
            var decorator = new UiaAlertNotificationDelivery(inner, statusView, () => enabled);

            statusView.WouldAnnounceValue = false;
            decorator.Announce("Normal message", false);
            Check("Inner delivery always receives the announcement (status field preserved)", inner.AnnounceCount == 1, true);
            Check("Not important -> no accessible alert raised", statusView.AccessibleAlertCount == 0, true);

            decorator.Announce("Important message", true);
            Check("Important + enabled + focus elsewhere -> raises the accessible alert", statusView.AccessibleAlertCount == 1, true);
            CheckStr("...with the same text ShowMessage would have shown", statusView.LastAccessibleAlert, "Important message");

            enabled = false;
            decorator.Announce("Important message 2", true);
            Check("Important but the General-tab option is off (default) -> no accessible alert", statusView.AccessibleAlertCount == 1, true);
            enabled = true;

            statusView.WouldAnnounceValue = true;
            decorator.Announce("Important message 3", true);
            Check("Important + enabled but statusText would already announce it -> no duplicate", statusView.AccessibleAlertCount == 1, true);
            statusView.WouldAnnounceValue = false;

            Check("Inner delivery received every one of the 4 Announce calls regardless of the above",
                inner.AnnounceCount == 4, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  UiaAlertNotificationDeliveryTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    static void NotificationCenterPublishTests()
    {
        Console.WriteLine("\n── NotificationCenter: Publish end-to-end ──");

        var settings = new NotificationSettings();
        var delivery = new FakeNotificationDelivery();
        var center = new NotificationCenter(settings, delivery);

        center.Publish(new QsoCompletedEvent("K4YT", "20m", "FT8"));
        CheckStr("QsoCompleted uses its default template", delivery.LastText, "Logged QSO with K4YT");
        Check("QsoCompleted default priority is Normal (no beep)", delivery.LastImportant == false, true);

        settings.Policies[NotificationEventType.QsoStarted].Enabled = false;
        int before = delivery.AnnounceCount;
        center.Publish(new QsoStartedEvent("K4YT", "20m", "FT8"));
        Check("Disabled event type never reaches delivery", delivery.AnnounceCount == before, true);

        // ErrorSeverity.Error forces the beep regardless of the configured policy Priority
        // (default Normal) -- see NotificationCenter.Publish's own comment; this is what lets
        // both existing migrated error call sites (one historically sound:false at
        // Warning-equivalent severity, one sound:true) share one policy.
        center.Publish(new ErrorWarningEvent(ErrorSeverity.Error, "Radio", "CAT link lost"));
        Check("ErrorSeverity.Error always sets Important, regardless of policy default", delivery.LastImportant == true, true);

        center.Publish(new ErrorWarningEvent(ErrorSeverity.Warning, "Radio", "launch failed"));
        Check("ErrorSeverity.Warning respects the policy's configured Priority (default Normal)", delivery.LastImportant == false, true);

        // Dedup: QsoStarted defaults to RepeatSeconds=5 (see NotificationDefaults.cs), so an
        // immediate re-publish for the same callsign must not reach delivery a second time.
        settings.Policies[NotificationEventType.QsoStarted].Enabled = true;
        center.Publish(new QsoStartedEvent("W1AW", "20m", "FT8"));
        int afterFirst = delivery.AnnounceCount;
        center.Publish(new QsoStartedEvent("W1AW", "20m", "FT8"));
        Check("Immediate repeat of the same QsoStarted identity is suppressed by default RepeatSeconds",
            delivery.AnnounceCount == afterFirst, true);

        center.Publish(null);
        Check("Publish(null) is a safe no-op", delivery.AnnounceCount == afterFirst, true);
    }

    // ── NotificationTemplateEngine.ParseComponents/ExtractVariableNames ──
    // Configurable-notification-templates feature, 2026-08-12.
    static void NotificationTemplateComponentParserTests()
    {
        Console.WriteLine("\n── NotificationTemplateEngine: ParseComponents / ExtractVariableNames ──");

        var c = NotificationTemplateEngine.ParseComponents("Calling {Callsign} in {Country}");
        Check("ParseComponents: literal + variable + literal + variable = 4 components", c.Count == 4, true);
        Check("ParseComponents: component 0 is literal 'Calling '", !c[0].IsVariable && c[0].Text == "Calling ", true);
        Check("ParseComponents: component 1 is variable 'Callsign'", c[1].IsVariable && c[1].Text == "Callsign", true);
        Check("ParseComponents: component 2 is literal ' in '", !c[2].IsVariable && c[2].Text == " in ", true);
        Check("ParseComponents: component 3 is variable 'Country'", c[3].IsVariable && c[3].Text == "Country", true);

        var empty = NotificationTemplateEngine.ParseComponents("");
        Check("ParseComponents: empty template -> no components", empty.Count == 0, true);

        var nullTemplate = NotificationTemplateEngine.ParseComponents(null);
        Check("ParseComponents: null template -> no components, never throws", nullTemplate.Count == 0, true);

        var malformed = NotificationTemplateEngine.ParseComponents("Calling {Callsign, no closing brace");
        Check("ParseComponents: unclosed '{' falls back to a single literal component",
            malformed.Count == 1 && !malformed[0].IsVariable, true);
        CheckStr("ParseComponents: unclosed '{' text preserved verbatim", malformed[0].Text, "Calling {Callsign, no closing brace");

        var pureLiteral = NotificationTemplateEngine.ParseComponents("Listening");
        Check("ParseComponents: pure literal template -> one literal component, no variables",
            pureLiteral.Count == 1 && !pureLiteral[0].IsVariable, true);

        var dup = NotificationTemplateEngine.ExtractVariableNames("{Callsign} worked {Callsign} again");
        Check("ExtractVariableNames: duplicate variable only counted once", dup.Count == 1 && dup[0] == "Callsign", true);

        var ordered = NotificationTemplateEngine.ExtractVariableNames("{Country}, {Callsign}, {Band}");
        Check("ExtractVariableNames: preserves left-to-right template order",
            ordered.Count == 3 && ordered[0] == "Country" && ordered[1] == "Callsign" && ordered[2] == "Band", true);

        // Round-trip: reassembling ParseComponents' own output must reproduce the original
        // string exactly -- this is the exact operation MoveNotifyVar/NotifyVarCheckChanged
        // (OptionsDlg.cs) rely on when they rebuild a template from an edited component list.
        string original = "Working {Callsign}, {Distance} miles away in {Country}";
        var roundTripSb = new System.Text.StringBuilder();
        foreach (var comp in NotificationTemplateEngine.ParseComponents(original))
            roundTripSb.Append(comp.IsVariable ? "{" + comp.Text + "}" : comp.Text);
        CheckStr("ParseComponents round-trips exactly back to the original template", roundTripSb.ToString(), original);

        // Format() must still produce byte-identical output to before this parser refactor --
        // regression guard for the "reuse one shared scanner" change (NotificationTemplateEngine.cs).
        var tokens = new Dictionary<string, string> { ["Callsign"] = "K4YT" };
        CheckStr("Format still substitutes known tokens after the ParseComponents refactor",
            NotificationTemplateEngine.Format("Working {Callsign}", tokens), "Working K4YT");
        CheckStr("Format still leaves unknown tokens literal after the refactor",
            NotificationTemplateEngine.Format("{Callsign} {Countri}", tokens), "K4YT {Countri}");
    }

    // ── NotificationVariableRegistry ──
    static void NotificationVariableRegistryTests()
    {
        Console.WriteLine("\n── NotificationVariableRegistry ──");

        Check("Validate: a template using only known variables passes",
            NotificationVariableRegistry.Validate("Working {Callsign} in {Country}", NotificationEventType.QsoStarted) == null, true);

        Check("Validate: pure literal text (no variables at all) passes",
            NotificationVariableRegistry.Validate("Listening", NotificationEventType.ConnectionClosed) == null, true);

        string error = NotificationVariableRegistry.Validate("{Callsign} {Countri}", NotificationEventType.QsoStarted);
        CheckStr("Validate: unknown keyword produces the exact required error message",
            error, "Unknown template keyword: Countri");

        Check("Validate: a variable valid for ONE type is rejected for a type that doesn't offer it",
            NotificationVariableRegistry.Validate("{AwardSummary}", NotificationEventType.ConnectionLost) != null, true);

        Check("Every type offers the universal {Time} variable",
            NotificationVariableRegistry.Validate("{Time}", NotificationEventType.ConnectionClosed) == null, true);

        // Keeps the registry honest: constructs a representative instance of every event class
        // and diffs its REAL ToTokens().Keys against what the registry claims is available for
        // that type (minus the one universal {Time}, injected centrally by NotificationCenter,
        // not by any individual event's own ToTokens()). If a future edit adds/renames/removes
        // a field on one side and forgets the other, this fails immediately instead of an
        // operator discovering a "valid" template that renders with a literal {Typo}.
        var sampleEvents = new Dictionary<NotificationEventType, INotificationEvent>
        {
            [NotificationEventType.QsoStarted] = new QsoStartedEvent("K4YT", "20m", "FT8", "EM79", "USA", 500, 90),
            [NotificationEventType.QsoCompleted] = new QsoCompletedEvent("K4YT", "20m", "FT8", "EM79", "USA", 500, 90, "-05", "+02"),
            [NotificationEventType.TxMessageChanged] = new TxMessageChangedEvent("K4YT", "K4YT KB0UZT -05", "20m", "FT8"),
            [NotificationEventType.AwardsNeeded] = new AwardsNeededEvent("K4YT", 2, new[] { "WAS", "DXCC" }, "2 awards needed", "USA"),
            [NotificationEventType.ConnectionClosed] = new ConnectionClosedEvent(),
            [NotificationEventType.ConnectionLost] = new ConnectionLostEvent("heartbeat timeout"),
            [NotificationEventType.ErrorWarning] = new ErrorWarningEvent(ErrorSeverity.Warning, "Radio", "CAT link lost"),
            [NotificationEventType.RadioCatRecovered] = new RadioCatRecoveredEvent(),
        };
        foreach (var kv in sampleEvents)
        {
            var registryKeys = new HashSet<string>();
            foreach (var v in NotificationVariableRegistry.For(kv.Key))
                if (v.Key != NotificationVariableRegistry.TimeKey) registryKeys.Add(v.Key);
            var realKeys = new HashSet<string>(kv.Value.ToTokens().Keys);
            Check($"Registry variable set for {kv.Key} exactly matches its event class's real ToTokens() keys",
                registryKeys.SetEquals(realKeys), true);
        }
    }

    // ── NotificationDefaults: every shipped default template is valid ──
    // Regression guard: a typo'd default template (e.g. {Countri} instead of {Country}) would
    // otherwise ship silently -- NotificationSettings.LoadFromIni's own fallback only protects
    // against a BAD ini value, not a bad code default, since the code default IS what it falls
    // back to.
    static void NotificationDefaultsAllTemplatesValidTests()
    {
        Console.WriteLine("\n── NotificationDefaults: every default template validates ──");

        foreach (var kv in NotificationDefaults.Policies)
        {
            string error = NotificationVariableRegistry.Validate(kv.Value.Template, kv.Key);
            Check($"Default template for {kv.Key} contains only known variables", error == null, true);
        }

        Check("Every NotificationEventType has a DisplayNames entry",
            System.Enum.GetValues(typeof(NotificationEventType)).Length == NotificationDefaults.DisplayNames.Count, true);
    }

    // ── NotificationPolicy: new fields (Timing/DeferWhileTransmitting/SuppressUnchanged) ──
    static void NotificationPolicyExtendedFieldsTests()
    {
        Console.WriteLine("\n── NotificationPolicy: extended fields (Clone + persistence) ──");

        var p = new NotificationPolicy
        {
            Timing = NotificationTiming.NextPeriodBoundary,
            DeferWhileTransmitting = true,
            SuppressUnchanged = true,
        };
        var cloned = p.Clone();
        Check("Clone() copies Timing", cloned.Timing == NotificationTiming.NextPeriodBoundary, true);
        Check("Clone() copies DeferWhileTransmitting", cloned.DeferWhileTransmitting, true);
        Check("Clone() copies SuppressUnchanged", cloned.SuppressUnchanged, true);

        // Persistence round-trip through the real IniFile-backed save/load path -- same shape
        // as NotificationSettingsRoundTripTests, extended to the three new fields.
        string tmpIni = Path.Combine(Path.GetTempPath(), $"jimmy_notify_ext_{System.Guid.NewGuid():N}.ini");
        try
        {
            var settings = new NotificationSettings();
            settings.Policies[NotificationEventType.AwardsNeeded].Timing = NotificationTiming.Immediate;
            settings.Policies[NotificationEventType.AwardsNeeded].DeferWhileTransmitting = false;
            settings.Policies[NotificationEventType.AwardsNeeded].SuppressUnchanged = true;
            var ini = new IniFile(tmpIni);
            settings.SaveToIni(ini);

            var reloaded = new NotificationSettings();
            reloaded.LoadFromIni(new IniFile(tmpIni));
            Check("Round-trip: Timing overridden away from AwardsNeeded's own default survives save/load",
                reloaded.Policies[NotificationEventType.AwardsNeeded].Timing == NotificationTiming.Immediate, true);
            Check("Round-trip: DeferWhileTransmitting survives save/load",
                reloaded.Policies[NotificationEventType.AwardsNeeded].DeferWhileTransmitting == false, true);
            Check("Round-trip: SuppressUnchanged survives save/load",
                reloaded.Policies[NotificationEventType.AwardsNeeded].SuppressUnchanged, true);

            // Missing keys (an ini saved by an older Jimmy version, before this feature existed)
            // must fall back to the code default, never throw or leave a half-set policy --
            // same "fail safe" contract NotificationSettings.cs's own header comment documents.
            var emptyIni = new IniFile(Path.Combine(Path.GetTempPath(), $"jimmy_notify_empty_{System.Guid.NewGuid():N}.ini"));
            var migratedSettings = new NotificationSettings();
            migratedSettings.LoadFromIni(emptyIni);
            Check("Missing notifyTiming_ key falls back to the code default (Immediate for QsoStarted)",
                migratedSettings.Policies[NotificationEventType.QsoStarted].Timing == NotificationTiming.Immediate, true);
            Check("Missing notifyTiming_ key falls back to the code default (NextPeriodBoundary for AwardsNeeded)",
                migratedSettings.Policies[NotificationEventType.AwardsNeeded].Timing == NotificationTiming.NextPeriodBoundary, true);

            // A corrupted/invalid template in the ini (e.g. hand-edited, or a future rename)
            // must fall back to the valid code default rather than shipping a broken
            // announcement -- NotificationSettings.LoadFromIni's own Validate-before-accept.
            var badTemplateIni = new IniFile(Path.Combine(Path.GetTempPath(), $"jimmy_notify_bad_{System.Guid.NewGuid():N}.ini"));
            badTemplateIni.Write($"notifyTemplate_{NotificationEventType.QsoStarted}", "{NotARealVariable}");
            var badSettings = new NotificationSettings();
            badSettings.LoadFromIni(badTemplateIni);
            CheckStr("An invalid saved template falls back to the valid code default, not the broken one",
                badSettings.Policies[NotificationEventType.QsoStarted].Template,
                NotificationDefaults.Policies[NotificationEventType.QsoStarted].Template);
        }
        finally
        {
            try { File.Delete(tmpIni); } catch { }
        }
    }

    // ── NotificationCenter: deferred delivery (Timing + DeferWhileTransmitting) ──
    // Configurable-notification-timing feature. FT8/FT4-agnostic by design (see
    // NotificationCenter.OnPeriodBoundary's own comment) -- these methods take no period-length
    // parameter at all, so there is nothing FT8- or FT4-specific to vary in these tests; the
    // real mode-dependent duration lives entirely upstream in DefaultTrPeriodMsTests' own
    // domain (WsjtxClient's trPeriod), not here.
    static void NotificationCenterDeferredDeliveryTests()
    {
        Console.WriteLine("\n── NotificationCenter: deferred delivery (Timing / DeferWhileTransmitting) ──");

        // Timing.NextPeriodBoundary: held until a boundary, not delivered from Publish itself.
        var settings = new NotificationSettings();
        settings.Policies[NotificationEventType.AwardsNeeded].RepeatSeconds = 0;
        settings.Policies[NotificationEventType.AwardsNeeded].ThrottleMilliseconds = 0;
        settings.Policies[NotificationEventType.AwardsNeeded].Timing = NotificationTiming.NextPeriodBoundary;
        settings.Policies[NotificationEventType.AwardsNeeded].DeferWhileTransmitting = false;
        var delivery = new FakeNotificationDelivery();
        var center = new NotificationCenter(settings, delivery);

        center.Publish(new AwardsNeededEvent("K4YT", 1, new[] { "WAS" }, "1 award needed"));
        Check("NextPeriodBoundary-timed event does not deliver immediately from Publish",
            delivery.AnnounceCount == 0, true);
        center.OnPeriodBoundary();
        Check("...but does deliver once a real period boundary fires",
            delivery.AnnounceCount == 1, true);
        CheckStr("...with the correct formatted text", delivery.LastText, "K4YT, 1 award needed");

        // Latest-wins: two publishes of the SAME identity before a boundary must only ever
        // deliver the second (latest) one -- never both, never the stale first.
        center.Publish(new AwardsNeededEvent("W1AW", 1, new[] { "WAS" }, "1 award needed"));
        center.Publish(new AwardsNeededEvent("W1AW", 2, new[] { "WAS", "DXCC" }, "2 awards needed"));
        int beforeBoundary = delivery.AnnounceCount;
        center.OnPeriodBoundary();
        Check("Two publishes of the same identity before a boundary -> exactly ONE delivery",
            delivery.AnnounceCount == beforeBoundary + 1, true);
        CheckStr("...and it's the LATEST one, not the first (no stale data)",
            delivery.LastText, "W1AW, 2 awards needed");

        // A boundary that finds nothing pending is a safe no-op, and a boundary never
        // re-delivers something already flushed (no double-delivery -- the exact bug class the
        // W4MAA incident was rooted in).
        int afterFirstFlush = delivery.AnnounceCount;
        center.OnPeriodBoundary();
        Check("A period boundary with nothing pending does not deliver anything",
            delivery.AnnounceCount == afterFirstFlush, true);

        // DeferWhileTransmitting: an Immediate-timed event published mid-transmission must wait
        // for OnTransmittingChanged(false), not deliver from Publish, and not wait for a period
        // boundary either (that's NextPeriodBoundary's job, a separate axis).
        var txSettings = new NotificationSettings();
        txSettings.Policies[NotificationEventType.QsoStarted].Timing = NotificationTiming.Immediate;
        txSettings.Policies[NotificationEventType.QsoStarted].DeferWhileTransmitting = true;
        txSettings.Policies[NotificationEventType.QsoStarted].RepeatSeconds = 0;
        var txDelivery = new FakeNotificationDelivery();
        var txCenter = new NotificationCenter(txSettings, txDelivery);

        txCenter.OnTransmittingChanged(true);
        txCenter.Publish(new QsoStartedEvent("K4YT", "20m", "FT8"));
        Check("DeferWhileTransmitting: Immediate event published mid-Tx does not deliver yet",
            txDelivery.AnnounceCount == 0, true);
        txCenter.OnPeriodBoundary();
        Check("...a period boundary during Tx does not release it either (still transmitting)",
            txDelivery.AnnounceCount == 0, true);
        txCenter.OnTransmittingChanged(false);
        Check("...but it delivers the instant transmitting ends",
            txDelivery.AnnounceCount == 1, true);

        // NextPeriodBoundary + DeferWhileTransmitting together: Tx ending alone must NOT
        // release it (that would shorten a "batched" notification's cadence to whatever the
        // current over happens to last) -- only a real period boundary that lands while not
        // transmitting does.
        var bothSettings = new NotificationSettings();
        bothSettings.Policies[NotificationEventType.AwardsNeeded].Timing = NotificationTiming.NextPeriodBoundary;
        bothSettings.Policies[NotificationEventType.AwardsNeeded].DeferWhileTransmitting = true;
        bothSettings.Policies[NotificationEventType.AwardsNeeded].RepeatSeconds = 0;
        bothSettings.Policies[NotificationEventType.AwardsNeeded].ThrottleMilliseconds = 0;
        var bothDelivery = new FakeNotificationDelivery();
        var bothCenter = new NotificationCenter(bothSettings, bothDelivery);

        bothCenter.OnTransmittingChanged(true);
        bothCenter.Publish(new AwardsNeededEvent("K4YT", 1, new[] { "WAS" }, "1 award needed"));
        bothCenter.OnTransmittingChanged(false);
        Check("NextPeriodBoundary+DeferWhileTransmitting: Tx ending alone does NOT release it",
            bothDelivery.AnnounceCount == 0, true);
        bothCenter.OnPeriodBoundary();
        Check("...only a real period boundary (now that Tx has ended) releases it",
            bothDelivery.AnnounceCount == 1, true);

        // SuppressUnchanged: identical formatted text back-to-back is suppressed even with
        // RepeatSeconds=0 (a content check, independent of the time-based one).
        var suSettings = new NotificationSettings();
        // Explicit template including {Band} -- the shipped default ("Working {Callsign}")
        // deliberately has nothing that varies with Band, which would make "a genuinely
        // changed value" below vacuously true regardless of whether SuppressUnchanged actually
        // works. Setting it here makes the test's own premise (this publish's formatted text
        // really does differ) hold regardless of what the default template happens to say.
        suSettings.Policies[NotificationEventType.QsoStarted].Template = "Working {Callsign} on {Band}";
        suSettings.Policies[NotificationEventType.QsoStarted].RepeatSeconds = 0;
        suSettings.Policies[NotificationEventType.QsoStarted].SuppressUnchanged = true;
        var suDelivery = new FakeNotificationDelivery();
        var suCenter = new NotificationCenter(suSettings, suDelivery);

        suCenter.Publish(new QsoStartedEvent("K4YT", "20m", "FT8"));
        int afterFirstSu = suDelivery.AnnounceCount;
        suCenter.Publish(new QsoStartedEvent("K4YT", "20m", "FT8"));   // identical formatted text
        Check("SuppressUnchanged: identical repeat is suppressed even with RepeatSeconds=0",
            suDelivery.AnnounceCount == afterFirstSu, true);
        suCenter.Publish(new QsoStartedEvent("K4YT", "40m", "FT8"));   // different Band -> different text
        Check("SuppressUnchanged: a genuinely changed value is still announced",
            suDelivery.AnnounceCount == afterFirstSu + 1, true);
    }

    // ── Parked notification event types stay parked ──
    // Direct requirement from the configurable-notification-templates feature spec: QsoStarted/
    // QsoCompleted/TxMessageChanged/AwardsNeeded must remain fully configurable (template/
    // timing/policy) WITHOUT gaining a new live Notify.Publish(...) call site, per the W4MAA
    // double-announcement lesson (see NotificationEvents.cs's own header comment). Scans the
    // real WSJTX_Controller source tree rather than trusting a hand-maintained list, so this
    // fails the moment anyone adds a new construction site, intentionally or not.
    static void NotificationParkedEventTypesGuardTests()
    {
        Console.WriteLine("\n── Parked notification event types remain parked ──");

        string srcRoot = FindRepoFile("WSJTX_Controller");
        if (srcRoot == null || !Directory.Exists(srcRoot))
        {
            Console.WriteLine("  SKIP  Parked-event-types guard: WSJTX_Controller source tree not found from this binary's location");
            return;
        }

        var parkedCtors = new[] { "new QsoStartedEvent(", "new QsoCompletedEvent(", "new TxMessageChangedEvent(", "new AwardsNeededEvent(" };
        var offenders = new List<string>();
        foreach (string file in Directory.GetFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            foreach (string ctor in parkedCtors)
                if (text.Contains(ctor)) offenders.Add($"{Path.GetFileName(file)} ({ctor.TrimEnd('(')})");
        }
        Check("No production WSJTX_Controller source file constructs a parked event type " +
              (offenders.Count > 0 ? $"(found: {string.Join(", ", offenders)})" : ""),
              offenders.Count == 0, true);
    }

    // ── Clock-sync notification (ClockOutOfSync/ClockSynced) ──
    // Drives the REAL Direct-mode ingestion pipeline (TestApplyDirectSnapshot ->
    // DirectApplyDecodes -> ProcessDecodeMsg -> timeOffsets, then the new-slot boundary ->
    // CalcAvgTimeOffset(true), same shape as DirectModePlumbingParityTests above) rather than a
    // synthetic bypass -- this is the actual, previously-broken-in-Direct-mode path being
    // exercised end to end, root-caused live 2026-08-12 (see CalcAvgTimeOffset's own comment).
    //
    // One mechanical wrinkle every assertion below has to account for: a snapshot's own DT
    // value is added to timeOffsets during ITS OWN processing, but only EVALUATED (averaged,
    // compared against maxTimeOffset, and a transition possibly published) at the START of the
    // NEXT snapshot that carries a new slot number -- there is an inherent one-period lag
    // between "data collected" and "period finalized", exactly like the real engine. Each
    // section below publishes its target DT twice in a row to settle fully through that lag
    // before asserting.
    static void ClockSyncNotificationTests()
    {
        Console.WriteLine("\n── Clock-sync notification (ClockOutOfSync / ClockSynced) ──");

        var ctrl = new Controller();
        ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
        ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
        ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
        ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
        var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
        ctrl.anyMsgRadioButton.Checked = true;
        ctrl.replyDxCheckBox.Checked = true;
        ctrl.replyLocalCheckBox.Checked = true;

        var settings = new NotificationSettings();
        // RepeatSeconds=0 here: the default 60s is a real wall-clock flap-guard (a SEPARATE,
        // already-covered mechanism -- see NotificationDedupThrottleTests), which would
        // otherwise suppress this test's later, deliberately-rapid transitions purely because
        // they happen within 60 real seconds of an earlier one. This test isolates the
        // transition-gate logic (CalcAvgTimeOffset's own _clockWasAcceptable) specifically.
        settings.Policies[NotificationEventType.ClockOutOfSync].RepeatSeconds = 0;
        settings.Policies[NotificationEventType.ClockSynced].RepeatSeconds = 0;
        var delivery = new FakeNotificationDelivery();
        wc.Notify = new NotificationCenter(settings, delivery);

        const string myCall = "KB0UZT";
        const string myGrid = "FN42";
        ulong slot = 5000;

        void PublishDt(double dt)
        {
            var snap = ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""",
                ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""slot"": " + (slot++) + @" },
                ""recentDecodes"": [
                    { ""from"": ""W1AW"", ""snr"": -5, ""dtSec"": " + dt.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + @", ""freqHz"": 1500.0, ""message"": ""CQ W1AW FN31"" }
                ]
            }");
            wc.TestApplyDirectSnapshot(myCall, myGrid, snap);
        }

        // Priming snapshot: first slot ever seen, so its own boundary check runs against an
        // empty timeOffsets (a no-op) before this snapshot's own DT is added.
        PublishDt(0.1);

        // Acceptable from the start (0.1s, well under the 1.20s default threshold) -- starting
        // acceptable must never itself announce anything.
        PublishDt(0.1);
        PublishDt(0.1);
        Check("Acceptable clock offset from the start -> no clock notification", delivery.AnnounceCount == 0, true);

        // Clearly unacceptable (2.0s > 1.20s threshold) -- exactly one warning once it settles.
        PublishDt(2.0);
        PublishDt(2.0);
        Check("Clock offset exceeding the threshold -> exactly one ClockOutOfSync notification",
            delivery.AnnounceCount == 1, true);
        CheckStr("...with the exact required wording and the real measured offset",
            delivery.LastText, "Computer clock is out of sync, offset 2.0 seconds.");
        Check("...delivered as Important (audible cue) -- operationally significant on both FT8 and FT4",
            delivery.LastImportant == true, true);

        // Stays bad for several more periods -- transition-gated, so no repeat chatter.
        PublishDt(2.0);
        PublishDt(2.0);
        PublishDt(2.0);
        Check("Clock remains out of sync across several more periods -> still exactly one notification (no per-cycle chatter)",
            delivery.AnnounceCount == 1, true);

        // Recovers -- a distinct ClockSynced notification, not a repeat of the warning.
        PublishDt(0.1);
        PublishDt(0.1);
        Check("Clock returns to acceptable -> a ClockSynced recovery notification fires",
            delivery.AnnounceCount == 2, true);
        CheckStr("...with the exact required recovery wording", delivery.LastText, "Computer clock timing is back within range.");

        // Becomes unacceptable again later -- a genuinely NEW transition, must announce again.
        PublishDt(1.5);
        PublishDt(1.5);
        Check("Clock becomes unacceptable again after recovering -> announces again (new transition, not suppressed)",
            delivery.AnnounceCount == 3, true);

        // Disabling the type stops it from announcing at all, same as every other notification.
        settings.Policies[NotificationEventType.ClockOutOfSync].Enabled = false;
        settings.Policies[NotificationEventType.ClockSynced].Enabled = false;
        PublishDt(0.1);
        PublishDt(0.1);   // recovery transition
        PublishDt(2.0);
        PublishDt(2.0);   // out-of-sync transition again
        Check("Disabled ClockOutOfSync/ClockSynced -> no further clock notifications at all",
            delivery.AnnounceCount == 3, true);

        // ── Boundary precision, isolated from the above sequence's ongoing state ──
        var boundarySettings = new NotificationSettings();
        var boundaryDelivery = new FakeNotificationDelivery();
        var boundaryWc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
        boundaryWc.Notify = new NotificationCenter(boundarySettings, boundaryDelivery);
        ulong bSlot = 6000;
        void PublishBoundaryDt(double dt)
        {
            var snap = ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""",
                ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""slot"": " + (bSlot++) + @" },
                ""recentDecodes"": [
                    { ""from"": ""W1AW"", ""snr"": -5, ""dtSec"": " + dt.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + @", ""freqHz"": 1500.0, ""message"": ""CQ W1AW FN31"" }
                ]
            }");
            boundaryWc.TestApplyDirectSnapshot(myCall, myGrid, snap);
        }
        PublishBoundaryDt(1.20);   // priming
        PublishBoundaryDt(1.20);
        PublishBoundaryDt(1.20);
        Check("Offset exactly AT the threshold (1.20s) is still acceptable (<=, not <)",
            boundaryDelivery.AnnounceCount == 0, true);
        PublishBoundaryDt(1.21);
        PublishBoundaryDt(1.21);
        Check("Offset just OVER the threshold (1.21s) is unacceptable",
            boundaryDelivery.AnnounceCount == 1, true);

        // ── FT8 vs FT4: same shared threshold, {Mode} token reflects whichever is active ──
        // See CalcAvgTimeOffset's own comment for why this is deliberately ONE threshold for
        // both modes, not two -- Jimmy's pre-existing maxTimeOffset was already unconditional
        // on mode before this feature, with no engine-level evidence FT4 needs a different one.
        var ft4Settings = new NotificationSettings();
        ft4Settings.Policies[NotificationEventType.ClockOutOfSync].RepeatSeconds = 0;   // see the main settings object's own comment above
        ft4Settings.Policies[NotificationEventType.ClockSynced].RepeatSeconds = 0;
        var ft4Delivery = new FakeNotificationDelivery();
        var ft4Wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
        ft4Wc.Notify = new NotificationCenter(ft4Settings, ft4Delivery);
        ft4Wc.TestSetMode("FT4");
        ulong ft4Slot = 7000;
        void PublishFt4Dt(double dt)
        {
            var snap = ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""",
                ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""slot"": " + (ft4Slot++) + @" },
                ""recentDecodes"": [
                    { ""from"": ""W1AW"", ""snr"": -5, ""dtSec"": " + dt.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + @", ""freqHz"": 1500.0, ""message"": ""CQ W1AW FN31"" }
                ]
            }");
            ft4Wc.TestApplyDirectSnapshot(myCall, myGrid, snap);
        }
        PublishFt4Dt(0.1);
        PublishFt4Dt(2.0);
        PublishFt4Dt(2.0);
        Check("FT4 mode: the same shared threshold still triggers the warning", ft4Delivery.AnnounceCount == 1, true);
        // Default template doesn't reference {Mode} at all -- switch to one that does, to prove
        // the token itself carries "FT4" correctly rather than a hardcoded FT8 assumption.
        ft4Settings.Policies[NotificationEventType.ClockOutOfSync].Template = "{Mode} clock offset {ClockOffset}";
        ft4Settings.Policies[NotificationEventType.ClockOutOfSync].SuppressUnchanged = false;
        // Force a fresh transition to re-fire with the new template active.
        PublishFt4Dt(0.1);
        PublishFt4Dt(0.1);
        PublishFt4Dt(2.5);
        PublishFt4Dt(2.5);
        CheckStr("FT4 mode: {Mode} token renders as \"FT4\" when the template actually uses it",
            ft4Delivery.LastText, "FT4 clock offset 2.5");
    }

    // ── Clock-sync: Direct-path reconnect / mode-switch state hygiene ──
    // Found in the 2026-08-12 Direct-engine-path review: ConnectDirectEngine and
    // SetOperatingMode's own Direct-mode branch didn't clear timeOffsets/timeOffset (and, for
    // reconnect, _clockWasAcceptable), so stale pre-reconnect or prior-mode DT samples could
    // contaminate the very next average. Fixed at both sites (WsjtxClient.Direct.cs /
    // WsjtxClient.Protocol.cs); this proves it via OBSERABLE behavior (whether a spurious
    // announcement fires), not by reaching into private state.
    static void ClockSyncDirectPathStateHygieneTests()
    {
        Console.WriteLine("\n── Clock-sync: Direct-path reconnect / mode-switch state hygiene ──");

        var ctrl = new Controller();
        ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
        ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
        ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
        ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
        var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
        ctrl.anyMsgRadioButton.Checked = true;
        ctrl.replyDxCheckBox.Checked = true;
        ctrl.replyLocalCheckBox.Checked = true;

        var settings = new NotificationSettings();
        settings.Policies[NotificationEventType.ClockOutOfSync].RepeatSeconds = 0;
        settings.Policies[NotificationEventType.ClockSynced].RepeatSeconds = 0;
        var delivery = new FakeNotificationDelivery();
        wc.Notify = new NotificationCenter(settings, delivery);

        const string myCall = "KB0UZT";
        const string myGrid = "FN42";
        ulong slot = 9000;
        void PublishDt(double dt)
        {
            var snap = ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""",
                ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""slot"": " + (slot++) + @" },
                ""recentDecodes"": [
                    { ""from"": ""W1AW"", ""snr"": -5, ""dtSec"": " + dt.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + @", ""freqHz"": 1500.0, ""message"": ""CQ W1AW FN31"" }
                ]
            }");
            wc.TestApplyDirectSnapshot(myCall, myGrid, snap);
        }

        wc.ConnectDirectEngine(myCall, myGrid);

        // Drive it bad before the reconnect under test.
        PublishDt(0.1);
        PublishDt(2.0);
        PublishDt(2.0);
        Check("Setup: clock reads out of sync before the reconnect under test", delivery.AnnounceCount == 1, true);

        // A reconnect must reset clock tracking to "unmeasured", not "still bad" -- proven by
        // fresh acceptable data staying silent (no spurious recovery announcement), since a
        // truly fresh connection has nothing to "recover" FROM yet.
        wc.ConnectDirectEngine(myCall, myGrid);
        int afterReconnect = delivery.AnnounceCount;
        PublishDt(0.1);
        PublishDt(0.1);
        Check("Reconnect resets clock state: fresh acceptable data right after reconnecting does not fire a spurious recovery",
            delivery.AnnounceCount == afterReconnect, true);

        // ...and a genuinely bad reading after reconnecting still announces normally (the reset
        // didn't disable the feature, only cleared stale state).
        PublishDt(2.0);
        PublishDt(2.0);
        Check("...but a genuinely bad reading after reconnecting still announces normally",
            delivery.AnnounceCount == afterReconnect + 1, true);

        // Mode switch: stale samples from the prior mode must not contaminate the new mode's
        // first average. Currently mid-"bad" (from the check just above, one 2.0s sample still
        // sitting unevaluated in timeOffsets right before the switch) -- feeding a reading
        // exactly AT the acceptable boundary (1.20s) right after switching is a value chosen
        // specifically to tell contaminated from clean apart: blended with the stale 2.0s
        // sample the average would land at 1.60s (still unacceptable, no transition, test
        // would correctly catch the bug), where a properly-cleared average reads exactly
        // 1.20s -- acceptable -- and fires a real recovery transition. A genuine recovery IS
        // the expected, correct outcome here (the clock really was bad, is now genuinely
        // measured as fine); what this proves is that the reading behind it wasn't contaminated.
        int beforeSwitch = delivery.AnnounceCount;
        // TestSetMode + TestClearTimeOffsetState, not the real SetOperatingMode -- this test's
        // own point is CalcAvgTimeOffset's clearing behavior, not SetOperatingMode/DirectSetTier's
        // wire round-trip (no live engine host exists in this harness, so DirectSetTier is
        // guaranteed to fail -- see WsjtxClient.Protocol.cs's own failure-handling fix,
        // 2026-08-19 -- and would also publish its own ErrorWarningEvent through `delivery`,
        // throwing off this test's own AnnounceCount arithmetic). Same reasoning TestSetMode's
        // own comment already documents for this exact test.
        wc.TestSetMode("FT4");
        wc.TestClearTimeOffsetState();
        PublishDt(1.20);
        PublishDt(1.20);
        Check("Mode switch clears stale samples: a boundary-acceptable FT4 reading is recognized as acceptable, not dragged over threshold by the prior mode's stale bad sample",
            delivery.AnnounceCount == beforeSwitch + 1, true);
    }

    // ── Rx/Tx frequency control, 2026-08-27: Tx stays stable during an active contact ──
    // The old behavior (removed): after maxConsecTxCount consecutive transmit cycles with no
    // reply (while the retired "Extended Timeout" was on), Direct mode disabled Tx and re-ran
    // the best-free-frequency analysis, moving the operator's transmit slot mid-QSO. Both that
    // re-pick and the Extended Timeout / "Hold" checkbox itself are now gone -- automatic
    // best-frequency selection happens ONLY at the start of a CQ or a reply. This test guards
    // that: consecTxCount still counts (debug/status only) but must NEVER trip auto-freq-pause,
    // however long a station stays silent. Drives the real DirectApplyStatus pipeline.
    static void DirectTxHoldSafetyNetTests()
    {
        Console.WriteLine("\n── Rx/Tx frequency control: Tx stays put during a contact (no mid-QSO re-pick) ──");

        var ctrl = new Controller();
        ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
        ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
        ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
        ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
        var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
        ctrl.anyMsgRadioButton.Checked = true;
        ctrl.replyDxCheckBox.Checked = true;
        ctrl.replyLocalCheckBox.Checked = true;
        ctrl.freqCheckBox.Checked = true;   // "Best free frequency" mode active

        const string myCall = "KB0UZT";
        const string myGrid = "FN42";
        ulong slot = 20000;

        void ApplyTransmitting(bool transmitting)
        {
            var snap = ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""",
                ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": " + (transmitting ? "true" : "false") + @", ""slot"": " + slot + @" },
                ""recentDecodes"": []
            }");
            wc.TestApplyDirectSnapshot(myCall, myGrid, snap);
        }

        // Prime a real band resolution so DirectApplyStatus's one-time startup-band fallback
        // doesn't fire mid-test and disturb anything (same dialMhz the clock-sync tests use).
        ApplyTransmitting(false);
        Check("Setup: auto-freq-pause starts disabled", wc.TestAutoFreqPauseDisabled, true);

        // A long string of unanswered transmit cycles (well past the old 12-cycle trip point),
        // with "Best free frequency" active, must NEVER disable Tx or start a frequency re-pick.
        for (int i = 0; i < 30; i++)
        {
            ApplyTransmitting(true);
            ApplyTransmitting(false);
        }
        Check("30 consecutive unanswered Tx cycles -> auto-freq-pause still disabled (Tx stays put)",
            wc.TestAutoFreqPauseDisabled, true);
        Check("...consecTxCount still tracks the run (reads 30)", wc.TestConsecTxCount == 30, true);

        // Even more unanswered cycles -- still nothing. There is no longer any counter,
        // checkbox, or hotkey that turns a long silent run into a frequency change.
        for (int i = 0; i < 20; i++)
        {
            ApplyTransmitting(true);
            ApplyTransmitting(false);
        }
        Check("50 unanswered cycles total -> auto-freq-pause still fully disabled",
            wc.TestAutoFreqPauseDisabled, true);
    }

    // ── Codex Audit 03 release blocker #1 regression test: HALT purges a queued CALL_CQ ──
    // Checks the queue's own state directly via TestDirectNormalQueueHasTxArmCommand
    // (WsjtxClient.Direct.cs) instead of a real network round-trip through a stub engine host --
    // deterministic (no dependency on the background worker actually winning a race against a
    // real TCP connect, or on this process's shared SNAPSHOT-poll-timer/control-port state across
    // ~1000 other tests in the same run), and it proves the exact mechanism the fix changed.
    static void HaltPurgesQueuedTxArmCommandTests()
    {
        Console.WriteLine("\n── HALT_TX purges a still-queued CALL_CQ instead of letting it re-arm TX -- THE FIX ──");
        try
        {
            var ctrl = new Controller();
            var _ = ctrl.Handle;
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.ConnectDirectEngine("KB0UZT", "FN42"); // sets _directConnected = true, required by HaltTx()
            // Test-isolation fix, 2026-08-23: ConnectDirectEngine also starts a real 1s-interval
            // SNAPSHOT poll timer that this test never otherwise stops -- found live while adding
            // T6/T7/T8/T16 regression coverage nearby: this wc (and its live timer) survive for
            // the rest of the WHOLE test process (nothing here ever calls Closing()/Dispose()),
            // so a stray periodic "SNAPSHOT" command from it can land on a LATER unrelated test's
            // own fresh stub-engine-host listener and corrupt whatever that test is trying to
            // observe (confirmed: intermittently broke DirectInitialConnectAlwaysRestoresLast
            // ExactDialTests' own lastCommand capture once enough wall-clock time elapsed before
            // it ran). Stopping it here costs nothing this test itself needs (both assertions
            // above already completed before this point matters).
            wc.TestStopPollTimer();
            // 2026-08-28: suppress the ordered command worker so it can't dequeue CALL_CQ before
            // the assertion below reads the queue. That Task.Run scheduling race made this test
            // intermittently fail as unrelated tests were added and shifted overall suite timing
            // -- the purge logic under test (PurgePendingTxArmCommands_NoLock) is fully
            // synchronous under _directQueueLock and needs no worker to exercise.
            wc.TestSuppressDirectCommandWorker();

            wc.DirectSendCq(null);
            Check("CALL_CQ is sitting in the normal queue (isTxArm) right after being sent",
                wc.TestDirectNormalQueueHasTxArmCommand(), true);

            wc.HaltTx();
            Check("THE FIX: HALT_TX's own enqueue purges the still-queued CALL_CQ before it can re-arm TX",
                wc.TestDirectNormalQueueHasTxArmCommand(), false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  HaltPurgesQueuedTxArmCommandTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── T7 fix, 2026-08-23: a priority HALT_TX aborts an already-in-flight ordinary command
    // instead of waiting behind its own ~4s worst-case budget ──
    // Reproduces the exact release-critical gap: a normal (non-priority) Direct command gets
    // dequeued and is genuinely blocked mid-flight (the stub engine host below accepts the TCP
    // connection but never writes a response), then HaltTx() is called while it's still stuck.
    // Before this fix, HALT_TX could only jump ahead of commands still WAITING in the queue --
    // an already-dequeued, in-flight command was untouched, so the single ordered worker
    // couldn't even attempt to send HALT_TX until the stuck command's own connect/read timeout
    // (~4s) expired on its own. AbortInFlightDirectCommand (called automatically for every
    // priority enqueue) closes that blocked socket immediately instead, freeing the worker to
    // send HALT_TX right away. Asserts the whole round trip completes well under the old ~4s+
    // worst case, not just that it eventually completes.
    static void HaltAbortsInFlightCommandTests()
    {
        Console.WriteLine("\n── T7 fix: priority HALT_TX aborts an already-in-flight command -- THE FIX ──");
        var acceptedFirstConnection = new System.Threading.ManualResetEventSlim(false);
        // Released explicitly right after this test's own assertions below (bounded to 5s as a
        // safety net) -- NOT Timeout.Infinite/a fixed long sleep, so the connection thread this
        // simulated "stuck command" runs on cleans up itself promptly instead of lingering for
        // the rest of the whole ~1000-test process and adding ambient thread/socket load that
        // could delay an unrelated LATER test's own timing-sensitive dispatcher assertions (see
        // Main()'s own SetMinThreads comment on this exact class of cross-test interaction).
        var releaseHungConnection = new System.Threading.ManualResetEventSlim(false);
        var listener = StartStubEngineHostWithResponses(line =>
        {
            if (line.StartsWith("SET_TX_ENABLED"))
            {
                // The ordinary command under test: signal it was actually received, then hold
                // this connection open without responding -- exactly what a hung/slow engine
                // host looks like from DirectSendCommand's side. By the time this eventually
                // "responds" (if ever -- most runs release it well before the 5s bound), Jimmy's
                // own client end is long gone (closed by AbortInFlightDirectCommand), so the
                // write is simply discarded; only the WAIT itself, not the response, matters.
                acceptedFirstConnection.Set();
                releaseHungConnection.Wait(5000);
                return "OK";
            }
            return "OK"; // HALT_TX (and anything else) gets a normal confirmed response
        });
        if (listener == null)
        {
            Skip("HaltAbortsInFlightCommandTests", "control port 58239 already in use by another Jimmy/engine-host session");
            return;
        }
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.ConnectDirectEngine("KB0UZT", "FN42");
            wc.TestStopPollTimer(); // the 1s SNAPSHOT poll would otherwise race this test's own connections

            // Enqueue an ordinary (non-priority) command and wait until the stub host has
            // actually accepted the TCP connection -- proves the worker has genuinely dequeued
            // and is blocked mid-flight, not merely sitting in the queue (which
            // HaltPurgesQueuedTxArmCommandTests above already covers separately).
            wc.DirectSetTxEnabled(true);
            bool acceptedInTime = acceptedFirstConnection.Wait(3000);
            Check("Setup: the ordinary command was actually dequeued and is blocked mid-flight",
                acceptedInTime, true);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool halted = wc.HaltTxAndWaitForShutdown(TimeSpan.FromMilliseconds(WsjtxClient.DirectHaltConfirmTimeoutMs));
            sw.Stop();

            Check("THE FIX: HALT_TX still completes with a confirmed OK despite the stuck in-flight command",
                halted, true);
            // Old worst case (waiting out the in-flight command's own ~4s budget, THEN sending
            // HALT_TX and waiting out ITS ~4s budget) was on the order of 8s+; the abort fix
            // collapses this to roughly one HALT_TX round trip. 3000ms leaves generous margin
            // above a healthy loopback round trip while still failing if the abort regresses.
            Check($"THE FIX: round trip finished well under the old worst case ({sw.ElapsedMilliseconds}ms observed, < 3000ms expected)",
                sw.ElapsedMilliseconds < 3000, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  HaltAbortsInFlightCommandTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            // idempotent -- ensures the held connection's thread never outlives this test even
            // if an assertion above threw.
            releaseHungConnection.Set();
            try { listener.Stop(); } catch { }
        }
    }

    // ── HALT/restart stopped-state confirmation (independent audit finding, 2026-08-23, HIGH
    // PRIORITY): HALT_TX's own "OK" is only an acknowledgement, not proof the engine's own
    // transmit/tune state actually stopped -- HaltAndConfirmTxStopped now also requires a
    // follow-up SNAPSHOT to agree ──
    // Drives HaltAndConfirmTxStopped (WsjtxClient.Direct.cs) directly against a stub engine host
    // whose SNAPSHOT still reports transmitting:true on the FIRST follow-up poll and only
    // transmitting:false on the second -- proving this is a genuine bounded RETRY loop reading
    // real engine-reported state, not a single check or a bare pass-through of HALT_TX's own OK.
    static void HaltConfirmsStoppedStateViaFollowUpSnapshotTests()
    {
        Console.WriteLine("\n── HALT/restart stopped-state confirmation: a follow-up SNAPSHOT, not just HALT_TX's OK, proves TX/Tune actually stopped -- THE FIX ──");
        int snapshotCallCount = 0;
        var listener = StartStubEngineHostWithResponses(line =>
        {
            if (line == "HALT_TX") return "OK";
            if (line == "SNAPSHOT")
            {
                snapshotCallCount++;
                // First poll still shows the engine mid-transmission (HALT_TX's own "OK" landed,
                // but the engine hasn't actually reported stopped yet); second poll shows it
                // genuinely stopped -- the shape this budget exists to observe.
                bool stillTransmitting = snapshotCallCount == 1;
                return "{\"mycall\":\"KB0UZT\",\"mygrid\":\"FN42\",\"radio\":{\"dialMhz\":14.074,\"transmitting\":" +
                    (stillTransmitting ? "true" : "false") + ",\"tuning\":false,\"slot\":1}}";
            }
            return "OK";
        });
        if (listener == null)
        {
            Skip("HaltConfirmsStoppedStateViaFollowUpSnapshotTests", "control port 58239 already in use by another Jimmy/engine-host session");
            return;
        }
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.ConnectDirectEngine("KB0UZT", "FN42");
            wc.TestStopPollTimer(); // the 1s SNAPSHOT poll would otherwise race this test's own follow-up polls

            bool result = wc.HaltAndConfirmTxStopped();

            Check("THE FIX: HaltAndConfirmTxStopped returns true once a follow-up SNAPSHOT confirms transmitting/tuning both false",
                result, true);
            Check("THE FIX: the follow-up confirmation actually retried (proves a real poll loop, not a single check)",
                snapshotCallCount >= 2, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  HaltConfirmsStoppedStateViaFollowUpSnapshotTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }
    }

    // ── HALT/restart stopped-state confirmation, companion test: the bounded budget actually
    // gives up and falls through to forced fallback when the engine keeps reporting still-
    // transmitting, rather than blocking indefinitely ──
    static void HaltDoesNotConfirmWhenStillTransmittingTests()
    {
        Console.WriteLine("\n── HALT/restart stopped-state confirmation: bounded give-up when the engine keeps reporting still-transmitting -- THE FIX ──");
        var listener = StartStubEngineHostWithResponses(line =>
        {
            if (line == "HALT_TX") return "OK";
            if (line == "SNAPSHOT")
                return "{\"mycall\":\"KB0UZT\",\"mygrid\":\"FN42\",\"radio\":{\"dialMhz\":14.074,\"transmitting\":true,\"tuning\":false,\"slot\":1}}";
            return "OK";
        });
        if (listener == null)
        {
            Skip("HaltDoesNotConfirmWhenStillTransmittingTests", "control port 58239 already in use by another Jimmy/engine-host session");
            return;
        }
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.ConnectDirectEngine("KB0UZT", "FN42");
            wc.TestStopPollTimer();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool result = wc.HaltAndConfirmTxStopped();
            sw.Stop();

            Check("THE FIX: HaltAndConfirmTxStopped returns false when SNAPSHOT keeps reporting still-transmitting",
                result, false);
            Check($"THE FIX: gives up within the small bounded budget instead of hanging ({sw.ElapsedMilliseconds}ms observed, < 3000ms expected)",
                sw.ElapsedMilliseconds < 3000, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  HaltDoesNotConfirmWhenStillTransmittingTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }
    }

    // ── T8 fix, 2026-08-23: a rejected/timed-out Reply no longer permanently drops the
    // selected station from the queue ──
    // Previously GetCall(idx) removed the call from CallQueueStore before REPLY was even sent;
    // a rejection/timeout left it gone with no rollback (the operator had to wait for another
    // decode). Now the peek is non-destructive and the dequeue itself only happens in
    // DirectSendReply's own success callback. Drives the real public ReplyTo(int) entry point
    // (via NextCall's own dialogTimer2_Tick, not an internal shortcut) against a stub engine
    // host that returns ERR for REPLY, then confirms the station is still exactly where it was.
    static void RejectedReplyPreservesQueuedStationTests()
    {
        Console.WriteLine("\n── T8 fix: rejected Reply preserves the selected station -- THE FIX ──");
        // Only REPLY gets ERR -- everything else (e.g. a stray SET_TX_OFFSET) gets a plain OK,
        // so this only targets the exact command under test.
        var engineListener = StartStubEngineHostWithResponses(line =>
            line.StartsWith("REPLY") ? "ERR rejected by test stub" : "OK");
        if (engineListener == null)
        {
            Skip("RejectedReplyPreservesQueuedStationTests", "control port 58239 already in use by another Jimmy/engine-host session");
            return;
        }
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.ConnectDirectEngine("KB0UZT", "FN42");
            wc.TestStopPollTimer(); // the 1s SNAPSHOT poll would otherwise race this test's own connections

            const string call = "9V1SH";
            var dmsg = new EnqueueDecodeMessage
            {
                Message = $"CQ {call} OJ22",
                Snr = -10,
                AutoGen = true,
                RxDate = DateTime.UtcNow.Date,
                SinceMidnight = DateTime.UtcNow.TimeOfDay,
            };
            wc.callDict[call] = dmsg;
            wc.callQueue.Enqueue(call);

            wc.NextCall(false, 0);
            // Pump the message loop long enough for dialogTimer2 (20ms interval) to fire
            // ReplyTo, the REPLY round trip against the stub (rejecting it) to complete, and its
            // completion callback (marshaled via ctrl.BeginInvoke) to run.
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 1500)
            {
                System.Windows.Forms.Application.DoEvents();
                System.Threading.Thread.Sleep(5);
            }

            Check("THE FIX: a rejected REPLY leaves the station exactly where it was in the queue",
                wc.callQueue.Contains(call), true);
            Check("...and callInProg was never committed for a reply that was never accepted",
                wc.callInProg == call, false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  RejectedReplyPreservesQueuedStationTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            engineListener.Stop();
        }
    }

    // ── Rx/Tx frequency control, 2026-08-27: where a reply transmits and receives, per the
    // "Transmit frequency" mode, and the caller-answers-our-CQ special case ──
    // Drives the real ReplyTo path (NextCall -> dialogTimer2_Tick) against a command-capturing
    // stub engine host and asserts the exact SET_RX_OFFSET / SET_TX_OFFSET commands Jimmy
    // emits. REPLY's dxFreqHz must always be null (Jimmy owns every offset move explicitly).
    static void RxTxFrequencyModeReplyTests()
    {
        Console.WriteLine("\n── Rx/Tx frequency control: reply placement per Transmit-frequency mode ──");

        var seen = new System.Collections.Generic.List<string>();
        var seenLock = new object();
        var engineListener = StartStubEngineHostWithResponses(line =>
        {
            lock (seenLock) seen.Add(line);
            return "OK";
        });
        if (engineListener == null)
        {
            Skip("RxTxFrequencyModeReplyTests", "control port 58239 already in use by another Jimmy/engine-host session");
            return;
        }
        try
        {
            const int theirHz = 1234;

            // Runs one reply to `message` in `mode`, returns the commands the stub saw.
            System.Collections.Generic.List<string> RunReply(WsjtxClient.TxFreqMode mode, string message, string curCmd)
            {
                var ctrl = new Controller();
                ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
                ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
                ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
                ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
                ctrl.txFreqMode = mode;
                ctrl.freqCheckBox.Checked = mode == WsjtxClient.TxFreqMode.BestFree;
                var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
                wc.ConnectDirectEngine("KB0UZT", "FN42");
                wc.TestStopPollTimer();
                wc.TestSetBestOffsets(2500, 2500);   // so BestFree has a slot to send
                if (curCmd != null) wc.TestSetCurCmd(curCmd);

                const string call = "9V1SH";
                var dmsg = new EnqueueDecodeMessage
                {
                    Message = message,
                    Snr = -10,
                    DeltaFrequency = theirHz,
                    AutoGen = true,
                    RxDate = DateTime.UtcNow.Date,
                    SinceMidnight = DateTime.UtcNow.TimeOfDay,
                };
                wc.callDict[call] = dmsg;
                wc.callQueue.Enqueue(call);

                lock (seenLock) seen.Clear();
                wc.NextCall(false, 0);
                var deadline = System.Diagnostics.Stopwatch.StartNew();
                while (deadline.ElapsedMilliseconds < 1500)
                {
                    System.Windows.Forms.Application.DoEvents();
                    System.Threading.Thread.Sleep(5);
                }
                lock (seenLock) return new System.Collections.Generic.List<string>(seen);
            }

            bool Has(System.Collections.Generic.List<string> cmds, string prefix) => cmds.Exists(c => c.StartsWith(prefix));
            bool HasExact(System.Collections.Generic.List<string> cmds, string exact) => cmds.Contains(exact);

            // S&P -- answering their CQ.
            var hold = RunReply(WsjtxClient.TxFreqMode.Hold, "CQ 9V1SH OJ22", null);
            Check("Hold: RX follows the station (SET_RX_OFFSET 1234)", HasExact(hold, "SET_RX_OFFSET 1234"), true);
            Check("Hold: TX is left alone (no SET_TX_OFFSET)", Has(hold, "SET_TX_OFFSET"), false);
            Check("Hold: REPLY carries no dxFreqHz", hold.Exists(c => c.StartsWith("REPLY") && c.Contains("\"dxFreqHz\":null")), true);

            var onStation = RunReply(WsjtxClient.TxFreqMode.OnStation, "CQ 9V1SH OJ22", null);
            Check("OnStation: RX moves to the station (SET_RX_OFFSET 1234)", HasExact(onStation, "SET_RX_OFFSET 1234"), true);
            Check("OnStation: TX moves to the station (SET_TX_OFFSET 1234)", HasExact(onStation, "SET_TX_OFFSET 1234"), true);

            var best = RunReply(WsjtxClient.TxFreqMode.BestFree, "CQ 9V1SH OJ22", null);
            Check("BestFree: RX follows the station (SET_RX_OFFSET 1234)", HasExact(best, "SET_RX_OFFSET 1234"), true);
            Check("BestFree: TX goes to the analyzed free slot (SET_TX_OFFSET 2500), not the station", HasExact(best, "SET_TX_OFFSET 2500"), true);

            // Caller answered OUR CQ: hold our CQ transmit frequency, only follow them on RX --
            // in every mode.
            foreach (var mode in new[] { WsjtxClient.TxFreqMode.BestFree, WsjtxClient.TxFreqMode.Hold, WsjtxClient.TxFreqMode.OnStation })
            {
                var ans = RunReply(mode, "KB0UZT 9V1SH FN31", "CQ KB0UZT FN42");
                Check($"Caller answers our CQ ({mode}): RX follows the caller (SET_RX_OFFSET 1234)", HasExact(ans, "SET_RX_OFFSET 1234"), true);
                Check($"Caller answers our CQ ({mode}): our CQ TX frequency is preserved (no SET_TX_OFFSET)", Has(ans, "SET_TX_OFFSET"), false);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  RxTxFrequencyModeReplyTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            engineListener.Stop();
        }
    }

    // ── Read-only audit finding 1, 2026-08-27: NativeEngineClient.TryEmergencyHaltTx must
    // confirm the engine's explicit "OK" before reporting success ──
    // The unhandled-exception crash handler (Program.cs) softens its dialog wording ("Transmit
    // has been halted as a precaution") based purely on this return value, so returning true
    // just because the bytes were written -- with no proof the engine received or acted on
    // HALT_TX -- could tell an operator TX was stopped when it wasn't. Drives the real static
    // method against a stub engine host whose HALT_TX response is varied per case, including a
    // hung connection and no engine at all -- both must return false quickly, never hang.
    static void EmergencyHaltTxConfirmationTests()
    {
        Console.WriteLine("\n── Read-only audit finding 1: emergency HALT_TX confirmation ──");

        string[] resp = { "OK" };
        string gotCommand = null;
        var listener = StartStubEngineHostWithResponses(line =>
        {
            gotCommand = line;
            return resp[0] == "<hang>" ? null : resp[0];
        });
        if (listener == null)
        {
            Skip("EmergencyHaltTxConfirmationTests", "control port 58239 already in use by another Jimmy/engine-host session");
            return;
        }
        try
        {
            resp[0] = "OK";
            gotCommand = null;
            bool okResult = NativeEngineClient.TryEmergencyHaltTx();
            Check("THE FIX: returns true only when the engine answers an explicit OK", okResult, true);
            Check("...and it actually sent HALT_TX", gotCommand == "HALT_TX", true);

            resp[0] = "ERR something went wrong";
            Check("an ERR response is not a confirmation -> false", NativeEngineClient.TryEmergencyHaltTx(), false);

            resp[0] = "MAYBE";
            Check("a malformed / unexpected response is not a confirmation -> false", NativeEngineClient.TryEmergencyHaltTx(), false);

            resp[0] = "<hang>";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool hungResult = NativeEngineClient.TryEmergencyHaltTx();
            sw.Stop();
            Check("a hung engine (no response) -> false", hungResult, false);
            Check($"...and it gave up within the bounded read budget ({sw.ElapsedMilliseconds}ms, < 2000ms)", sw.ElapsedMilliseconds < 2000, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  EmergencyHaltTxConfirmationTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            listener.Stop();
        }

        // No engine listening at all -- the port is free again now that the stub is stopped.
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool noEngine = NativeEngineClient.TryEmergencyHaltTx();
            sw.Stop();
            Check("no engine reachable -> false", noEngine, false);
            Check($"...and it gave up within the bounded connect budget ({sw.ElapsedMilliseconds}ms, < 2000ms)", sw.ElapsedMilliseconds < 2000, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  EmergencyHaltTxConfirmationTests (no-engine) threw: {ex.GetType().Name}: {ex.Message}");
            failed++;
        }
    }

    // ── Read-only audit finding 2, 2026-08-27: a failed manual Tx-frequency change must not
    // leave _manualFreqThisQso set ──
    // _manualFreqThisQso suppresses automatic "Best free frequency" placement for the rest of
    // the QSO. It used to be set true eagerly, before SET_TX_OFFSET was even sent, so a change
    // the engine rejected/never answered permanently blocked Best Free. Now it is set only in
    // the confirmed-success callback and left untouched on failure.
    static void FailedManualTxOffsetPreservesBestFreeTests()
    {
        Console.WriteLine("\n── Read-only audit finding 2: failed manual Tx change preserves Best Free ──");

        string[] resp = { "OK" };
        var listener = StartStubEngineHostWithResponses(line =>
            line.StartsWith("SET_TX_OFFSET") ? resp[0] : "OK");
        if (listener == null)
        {
            Skip("FailedManualTxOffsetPreservesBestFreeTests", "control port 58239 already in use by another Jimmy/engine-host session");
            return;
        }
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.txFreqMode = WsjtxClient.TxFreqMode.BestFree;
            ctrl.freqCheckBox.Checked = true;
            ctrl.freqStepHz = 60;
            // Force the Form's native handle to exist NOW: the DirectSetTxOffset completion runs
            // via ctrl.BeginInvoke on a background Task, and without a created handle BeginInvoke
            // throws off-thread (caught nowhere) and the callback silently never runs -- same fix
            // SessionTokenAuthenticationTests / HaltPurgesQueuedTxArmCommandTests already apply.
            var _ = ctrl.Handle;
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.ConnectDirectEngine("KB0UZT", "FN42");
            wc.TestStopPollTimer();

            Check("precondition: _manualFreqThisQso starts false", wc.TestManualFreqThisQso, false);

            // A SET_TX_OFFSET the engine rejects (ERR) must NOT latch the manual override.
            resp[0] = "ERR rejected by test stub";
            wc.NudgeTxFrequency(+1);
            PumpUntil(() => wc.TestTxOffsetRequestsInFlight == 0);
            Check("THE FIX: a rejected manual Tx change leaves _manualFreqThisQso false", wc.TestManualFreqThisQso, false);
            Check("...and txOffset was not moved by an unconfirmed change", wc.TestTxOffset == 0, true);
            Check("...and pending Tx state was cleared, not left drifting", wc.TestPendingTxOffsetHz == null, true);

            // Control: a later CONFIRMED manual change still latches the flag as designed.
            resp[0] = "OK";
            wc.NudgeTxFrequency(+1);
            PumpUntil(() => wc.TestTxOffsetRequestsInFlight == 0 && wc.TestManualFreqThisQso);
            Check("a confirmed manual Tx change sets _manualFreqThisQso true", wc.TestManualFreqThisQso, true);
            Check("...and txOffset advanced to the confirmed value (1560)", wc.TestTxOffset == 1560, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  FailedManualTxOffsetPreservesBestFreeTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            listener.Stop();
        }
    }

    // ── Read-only audit finding 3, 2026-08-27: rapid Tx/Rx nudge presses must ACCUMULATE while
    // earlier SET_TX_OFFSET / SET_RX_OFFSET commands are still in flight ──
    // Three +60 presses from 1500 must target 1560, 1620, 1680 -- not 1560 three times -- and a
    // nudge whose command fails must not leave the pending offset drifted away from the engine:
    // the next nudge resumes from the confirmed value.
    static void RapidFrequencyNudgesAccumulateTests()
    {
        Console.WriteLine("\n── Read-only audit finding 3: rapid frequency nudges accumulate ──");

        var seen = new System.Collections.Generic.List<string>();
        var seenLock = new object();
        string[] resp = { "OK" };
        var listener = StartStubEngineHostWithResponses(line =>
        {
            lock (seenLock) seen.Add(line);
            if (line.StartsWith("SET_TX_OFFSET") || line.StartsWith("SET_RX_OFFSET")) return resp[0];
            return "OK";
        });
        if (listener == null)
        {
            Skip("RapidFrequencyNudgesAccumulateTests", "control port 58239 already in use by another Jimmy/engine-host session");
            return;
        }
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.freqStepHz = 60;
            // Force the Form's native handle to exist NOW -- the DirectSetTxOffset/
            // DirectSetRxOffset completions run via ctrl.BeginInvoke on a background Task; without
            // a created handle BeginInvoke throws off-thread (caught nowhere) and the callbacks
            // silently never run, so the in-flight counters would never decrement.
            var _ = ctrl.Handle;
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.ConnectDirectEngine("KB0UZT", "FN42");
            wc.TestStopPollTimer();

            System.Collections.Generic.List<string> OffsetCmds(string prefix)
            {
                lock (seenLock) return seen.FindAll(c => c.StartsWith(prefix));
            }

            // --- Tx: three rapid +60 presses with nothing pumped in between ---
            lock (seenLock) seen.Clear();
            wc.NudgeTxFrequency(+1);
            wc.NudgeTxFrequency(+1);
            wc.NudgeTxFrequency(+1);
            PumpUntil(() => wc.TestTxOffsetRequestsInFlight == 0 && OffsetCmds("SET_TX_OFFSET").Count >= 3);

            var txCmds = OffsetCmds("SET_TX_OFFSET");
            Check("THE FIX: three rapid Tx nudges emit exactly three commands", txCmds.Count == 3, true);
            Check("...targeting 1560 then 1620 then 1680, not 1560 three times",
                txCmds.Count == 3 && txCmds[0] == "SET_TX_OFFSET 1560" && txCmds[1] == "SET_TX_OFFSET 1620" && txCmds[2] == "SET_TX_OFFSET 1680", true);
            Check("...and the confirmed Tx offset ends at 1680", wc.TestTxOffset == 1680, true);
            Check("...and pending Tx state cleared once the burst settled", wc.TestPendingTxOffsetHz == null, true);

            // --- Rx: same accumulation on the receive marker ---
            lock (seenLock) seen.Clear();
            wc.NudgeRxFrequency(+1);
            wc.NudgeRxFrequency(+1);
            wc.NudgeRxFrequency(+1);
            PumpUntil(() => wc.TestRxOffsetRequestsInFlight == 0 && OffsetCmds("SET_RX_OFFSET").Count >= 3);

            var rxCmds = OffsetCmds("SET_RX_OFFSET");
            Check("three rapid Rx nudges accumulate to 1560 / 1620 / 1680",
                rxCmds.Count == 3 && rxCmds[0] == "SET_RX_OFFSET 1560" && rxCmds[1] == "SET_RX_OFFSET 1620" && rxCmds[2] == "SET_RX_OFFSET 1680", true);
            Check("...and the confirmed Rx offset ends at 1680", wc.TestRxOffset == 1680, true);

            // --- Recovery after a nudge command fails ---
            // Confirmed Tx offset is 1680. One failing nudge, then a succeeding one: the retry
            // must resume from 1680 (SET_TX_OFFSET 1740), NOT from a pending value that drifted
            // to 1740 during the failure (which would make the retry target 1800).
            lock (seenLock) seen.Clear();
            resp[0] = "ERR dropped";
            wc.NudgeTxFrequency(+1);
            PumpUntil(() => wc.TestTxOffsetRequestsInFlight == 0);
            Check("a failed nudge does not move the confirmed Tx offset", wc.TestTxOffset == 1680, true);
            Check("a failed nudge clears pending Tx state (no permanent drift)", wc.TestPendingTxOffsetHz == null, true);

            resp[0] = "OK";
            wc.NudgeTxFrequency(+1);
            PumpUntil(() => wc.TestTxOffsetRequestsInFlight == 0 && wc.TestTxOffset == 1740);
            var recoveryCmds = OffsetCmds("SET_TX_OFFSET");
            Check("THE FIX: after a failed nudge the retry resumes from the confirmed value -- SET_TX_OFFSET 1740, not 1800",
                recoveryCmds.Count == 2 && recoveryCmds[1] == "SET_TX_OFFSET 1740", true);
            Check("...and the confirmed Tx offset is exactly one step past 1680", wc.TestTxOffset == 1740, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  RapidFrequencyNudgesAccumulateTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            listener.Stop();
        }
    }

    // ── EngineHost ownership / session identity, 2026-08-23 (independent audit finding, HIGH
    // PRIORITY): Direct mode must prove a SNAPSHOT actually came from the exact child process
    // this session launched before treating the connection as authenticated/connected or
    // sending TX-arming commands ──
    // Drives a real SNAPSHOT round trip (WsjtxClient.Direct.cs's DirectPollTick, fired once on
    // demand via TestTriggerDirectPollTick -- the same production method the 1s poll timer
    // calls) against a stub engine host, proving both directions of the actual authentication
    // gate rather than exercising ConnectDirectEngine/DirectPollTick's internals directly:
    //   1. A SNAPSHOT whose sessionToken matches the token this session expects -> authenticated,
    //      NegoState reaches RECD, and a real TX-arming command (CALL_CQ) is actually sent.
    //   2. A SNAPSHOT whose sessionToken does NOT match (stale/orphan process on the fixed
    //      control port) -> never authenticated, NegoState never reaches RECD, and CALL_CQ is
    //      refused locally (onComplete(false)) without ever reaching the stub engine host at all
    //      -- proving the block happens before anything TX-capable is sent, not merely that the
    //      command was later rejected.
    static void SessionTokenAuthenticationTests()
    {
        Console.WriteLine("\n── EngineHost ownership / session identity: SNAPSHOT sessionToken gates authentication and TX-arming commands ──");
        const string expectedToken = "test-session-token-abc123";
        var savedNegoState = WsjtxMessage.NegoState;

        // ---- Part 1: matching token authenticates and allows a TX-arming command through ----
        bool snapshotSeen = false;
        bool callCqReachedStub = false;
        var matchListener = StartStubEngineHostWithResponses(line =>
        {
            if (line == "SNAPSHOT")
            {
                snapshotSeen = true;
                return $"{{\"mycall\":\"KB0UZT\",\"mygrid\":\"FN42\",\"sessionToken\":\"{expectedToken}\",\"pid\":4242}}";
            }
            if (line.StartsWith("CALL_CQ")) { callCqReachedStub = true; return "OK"; }
            return "OK";
        });
        if (matchListener == null)
        {
            Skip("SessionTokenAuthenticationTests", "control port 58239 already in use by another Jimmy/engine-host session");
            return;
        }
        try
        {
            var ctrl = new Controller();
            // Forces the Form's native window handle to exist NOW -- DirectPollTick's completion
            // runs via ctrl.BeginInvoke on a background Task; without a created handle,
            // BeginInvoke throws (caught nowhere, since it's off the calling thread) and the
            // whole continuation silently never runs, which would make this test pass for the
            // wrong reason (nothing ever updates _directAuthenticated either way). Same fix
            // HaltPurgesQueuedTxArmCommandTests already applies for the same reason.
            var _ = ctrl.Handle;
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.ConnectDirectEngine("KB0UZT", "FN42", expectedToken);
            wc.TestStopPollTimer(); // drive exactly one tick ourselves instead of racing the real 1s timer

            wc.TestTriggerDirectPollTick();
            PumpUntil(() => snapshotSeen);
            Check("Setup: the poll tick actually reached the stub engine host's SNAPSHOT handler",
                snapshotSeen, true);
            PumpUntil(() => wc.TestDirectAuthenticated);
            Check("Matching sessionToken: session becomes authenticated",
                wc.TestDirectAuthenticated, true);
            Check("Matching sessionToken: NegoState reaches RECD",
                WsjtxMessage.NegoState == WsjtxMessage.NegoStates.RECD, true);

            bool? cqResult = null;
            wc.DirectSendCq("", ok => cqResult = ok);
            PumpUntil(() => cqResult.HasValue || callCqReachedStub);
            Check("Matching sessionToken: CALL_CQ actually reaches the authenticated engine host",
                callCqReachedStub, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  SessionTokenAuthenticationTests (matching token) threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            matchListener.Stop();
        }

        // ---- Part 2: mismatched token never authenticates and blocks TX-arming commands ----
        WsjtxMessage.NegoState = WsjtxMessage.NegoStates.WAIT; // reset from Part 1 before reusing the shared static
        bool snapshotSeenMismatch = false;
        bool callCqReachedStubMismatch = false;
        var mismatchListener = StartStubEngineHostWithResponses(line =>
        {
            if (line == "SNAPSHOT")
            {
                snapshotSeenMismatch = true;
                return "{\"mycall\":\"KB0UZT\",\"mygrid\":\"FN42\",\"sessionToken\":\"some-other-stale-process-token\",\"pid\":9999}";
            }
            if (line.StartsWith("CALL_CQ")) { callCqReachedStubMismatch = true; return "OK"; }
            return "OK";
        });
        if (mismatchListener == null)
        {
            Skip("SessionTokenAuthenticationTests (mismatch part)", "control port 58239 already in use by another Jimmy/engine-host session");
            return;
        }
        try
        {
            var ctrl = new Controller();
            var _ = ctrl.Handle; // see Part 1's own comment on why this is required
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.ConnectDirectEngine("KB0UZT", "FN42", expectedToken);
            wc.TestStopPollTimer();

            wc.TestTriggerDirectPollTick();
            PumpUntil(() => snapshotSeenMismatch);
            Check("Setup: the poll tick actually reached the stub engine host's SNAPSHOT handler",
                snapshotSeenMismatch, true);
            // Bounded extra wait for the poll's BeginInvoke continuation to finish running on the
            // UI thread after the stub responded -- we expect it to leave _directAuthenticated
            // false (can't PumpUntil a negative condition), so this just gives that continuation
            // time to actually complete before asserting it left things exactly as expected.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1000)
            {
                System.Windows.Forms.Application.DoEvents();
                System.Threading.Thread.Sleep(5);
            }
            Check("THE FIX: mismatched sessionToken never authenticates",
                wc.TestDirectAuthenticated, false);
            Check("THE FIX: mismatched sessionToken -- NegoState never promoted to RECD",
                WsjtxMessage.NegoState == WsjtxMessage.NegoStates.RECD, false);

            bool? cqResult = null;
            wc.DirectSendCq("", ok => cqResult = ok);
            PumpUntil(() => cqResult.HasValue);
            Check("THE FIX: CALL_CQ's onComplete was actually invoked (refused synchronously, not left hanging)",
                cqResult.HasValue, true);
            Check("THE FIX: CALL_CQ is refused locally (onComplete(false)) while unauthenticated",
                cqResult.GetValueOrDefault(true), false);
            Check("THE FIX: the refused CALL_CQ never actually reached the (wrong) engine host",
                callCqReachedStubMismatch, false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  SessionTokenAuthenticationTests (mismatched token) threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            mismatchListener.Stop();
            WsjtxMessage.NegoState = savedNegoState;
        }

        // ---- Part 3: real-launch-failure root cause, 2026-08-24 -- an outdated EngineHost
        // binary (no sessionToken/pid fields in its SNAPSHOT JSON at all -- exactly what an
        // EngineHost built before --session-token existed reports; confirmed live: nothing else
        // was running, Jimmy's own freshly-launched child simply didn't speak the protocol yet)
        // must get a DIFFERENT, accurate message -- not the "close any stale jimmy-engine-host.exe
        // process" advice, which is actively wrong when the responding process IS the one this
        // session just launched. ----
        WsjtxMessage.NegoState = WsjtxMessage.NegoStates.WAIT;
        bool snapshotSeenOutdated = false;
        var outdatedListener = StartStubEngineHostWithResponses(line =>
        {
            if (line == "SNAPSHOT")
            {
                snapshotSeenOutdated = true;
                // No "sessionToken"/"pid" fields at all -- exactly what a pre-session-token
                // EngineHost build's SNAPSHOT response looks like (DirectSnapshot.SessionToken/
                // Pid deserialize to their type defaults, null/0, when the JSON simply omits them).
                return "{\"mycall\":\"KB0UZT\",\"mygrid\":\"FN42\"}";
            }
            return "OK";
        });
        if (outdatedListener == null)
        {
            Skip("SessionTokenAuthenticationTests (outdated-binary part)", "control port 58239 already in use by another Jimmy/engine-host session");
            return;
        }
        try
        {
            var ctrl = new Controller();
            var _ = ctrl.Handle; // see Part 1's own comment on why this is required
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);

            var settings = new NotificationSettings();
            settings.Policies[NotificationEventType.ErrorWarning].RepeatSeconds = 0;
            var delivery = new FakeNotificationDelivery();
            wc.Notify = new NotificationCenter(settings, delivery);

            wc.ConnectDirectEngine("KB0UZT", "FN42", expectedToken);
            wc.TestStopPollTimer();

            wc.TestTriggerDirectPollTick();
            PumpUntil(() => snapshotSeenOutdated);
            Check("Setup: the poll tick actually reached the stub engine host's SNAPSHOT handler",
                snapshotSeenOutdated, true);
            PumpUntil(() => delivery.AnnounceCount > 0);
            Check("THE FIX: an outdated-binary SNAPSHOT (no sessionToken/pid at all) still never authenticates",
                wc.TestDirectAuthenticated, false);
            Check("THE FIX: the announced text correctly blames an outdated EngineHost build",
                delivery.LastText != null && delivery.LastText.ToLowerInvariant().Contains("outdated"), true);
            Check("THE FIX: the announced text does NOT tell the operator to close a stale process (none exists -- this IS the launched child)",
                delivery.LastText != null && !delivery.LastText.ToLowerInvariant().Contains("close any stale"), true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  SessionTokenAuthenticationTests (outdated-binary) threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            outdatedListener.Stop();
            WsjtxMessage.NegoState = savedNegoState;
        }
    }

    // ── Repeat limit authoritative-stop fix, 2026-08-24 (independent audit finding, CONFIRMED
    // live -- KF4CCG, 2026-08-23/24): Jimmy's own Repeat Limit is now the sole attempt-count
    // stop, and actively forces TX off itself the moment its own count reaches it, instead of
    // waiting to observe that the engine already agrees ──
    // SUPERSEDES the old T14 "two-clock divergence" test below (2026-08-23): that design waited
    // for EngineHost's OWN txEnabled to go false on its own before treating the limit as
    // terminal, because EngineHost's own call-cap (directed_max_calls) was still active and
    // uncoordinated with Jimmy's count. Real-launch reproduction (Repeat Limit=3, manual call to
    // KF4CCG) proved the wait strategy doesn't work: EngineHost's own cap never actually
    // disables txEnabled at all (confirmed by reading tempo-app/src/engine.rs's own "THE CAPPED
    // STATION IS STILL AN ARMED TRANSMITTER" comment) -- only an unrelated wall-clock watchdog
    // does, on its own schedule -- so Jimmy kept "waiting" through 5 real transmissions before
    // the operator intervened by hand. Fix: EngineHost's own call-cap is now disabled outright
    // (main.rs's own Settings construction, directed_max_calls: None), and DiscardCall itself
    // actively sends SET_TX_ENABLED 0 the moment it's called, rather than waiting for anything.
    // Drives a real stub engine host (not just local-state assertions) specifically to prove
    // there is no possible 4th transmission: once the limit is reached, SET_TX_ENABLED 0 is the
    // ONLY command that reaches the engine for this call -- no CALL_CQ, no REPLY, no
    // SET_TX_ENABLED 1 ever follows it.
    static void RepeatLimitActivelyStopsTxTests()
    {
        Console.WriteLine("\n── Repeat limit authoritative-stop fix: reaching the limit actively sends SET_TX_ENABLED 0, proving no further transmission -- THE FIX ──");
        bool setTxDisabledSent = false;
        bool txArmCommandSentAfterStop = false;
        var listener = StartStubEngineHostWithResponses(line =>
        {
            if (line.StartsWith("SET_TX_ENABLED 0"))
            {
                setTxDisabledSent = true;
                return "OK";
            }
            // Anything that could actually key the radio again for this call -- a 4th
            // transmission would have to go through one of these. Flagged if it EVER arrives,
            // regardless of timing, since none of them should be sent at all once the limit is
            // reached and DiscardCall's own cleanup runs.
            if (line.StartsWith("CALL_CQ") || line.StartsWith("REPLY") || line.StartsWith("SET_TX_ENABLED 1"))
            {
                txArmCommandSentAfterStop = true;
                return "OK";
            }
            return "OK";
        });
        if (listener == null)
        {
            Skip("RepeatLimitActivelyStopsTxTests", "control port 58239 already in use by another Jimmy/engine-host session");
            return;
        }
        try
        {
            var ctrl = new Controller();
            var _ = ctrl.Handle; // see SessionTokenAuthenticationTests' own comment on why this is required
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.ConnectDirectEngine("KB0UZT", "FN42");
            wc.TestStopPollTimer(); // the 1s SNAPSHOT poll would otherwise race this test's own connection

            const string call = "KF4CCG";
            wc.callInProg = call;
            // Matches the real log exactly: the engine's own txEnabled was STILL true (it never
            // authoritatively disables on its own -- see this test's own header comment) at the
            // moment Jimmy's local counter reached the configured Repeat Limit.
            wc.TestSetTxEnabled(true);
            wc.TestStartDiscardCall(call);

            // Simulate reaching the configured Repeat Limit -- DiscardCall's caller (WsjtxClient.
            // Direct.cs's own new-slot check) only ever invokes it once its own period counter has
            // already reached the limit; this drives that exact call.
            wc.TestTriggerDiscardCall();

            PumpUntil(() => setTxDisabledSent);
            Check("THE FIX: reaching the Repeat Limit actively sends SET_TX_ENABLED 0 -- Jimmy stops TX itself, not waiting for the engine to agree",
                setTxDisabledSent, true);
            Check("THE FIX: callInProg is cleared immediately, regardless of txEnabled still reading true",
                wc.callInProg == null, true);
            Check("THE FIX: the discard tracker disarms immediately (one-shot, not waiting for another period)",
                wc.TestDiscardCall == null, true);

            // Bounded extra wait -- give any (incorrect) further command a real chance to arrive
            // before asserting it never did.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1000)
            {
                System.Windows.Forms.Application.DoEvents();
                System.Threading.Thread.Sleep(5);
            }
            Check("THE FIX: no 4th transmission -- no CALL_CQ/REPLY/SET_TX_ENABLED 1 was ever sent for this call",
                txArmCommandSentAfterStop, false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  RepeatLimitActivelyStopsTxTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            listener.Stop();
        }

        // ---- Part 2: CALL_CQ mode -- a genuinely different code path (never calls Pause(), so
        // nothing else stops TX for it -- the explicit SET_TX_ENABLED 0 send is the ONLY thing
        // that does) ----
        bool setTxDisabledSentCq = false;
        var cqListener = StartStubEngineHostWithResponses(line =>
        {
            if (line.StartsWith("SET_TX_ENABLED 0")) { setTxDisabledSentCq = true; return "OK"; }
            return "OK";
        });
        if (cqListener == null)
        {
            Skip("RepeatLimitActivelyStopsTxTests (CALL_CQ part)", "control port 58239 already in use by another Jimmy/engine-host session");
            return;
        }
        try
        {
            var ctrl = new Controller();
            var _ = ctrl.Handle;
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            // Constructed in LISTEN mode deliberately -- StartDiscardCall itself no-ops in
            // CALL_CQ mode ("if (txMode == TxModes.CALL_CQ) return;"), so the only real way the
            // tracker is ever armed AND DiscardCall() later sees txMode==CALL_CQ is the operator
            // switching modes mid-track (armed while replying in Listen mode, then switched to
            // Call CQ before the next period boundary) -- reproduced explicitly below rather than
            // constructing straight into CALL_CQ, which would never arm the tracker at all.
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.ConnectDirectEngine("KB0UZT", "FN42");
            wc.TestStopPollTimer();

            const string call = "KF4CCG";
            wc.callInProg = call;
            wc.TestSetTxEnabled(true);
            wc.TestStartDiscardCall(call);
            wc.txMode = WsjtxClient.TxModes.CALL_CQ;
            wc.TestTriggerDiscardCall();

            PumpUntil(() => setTxDisabledSentCq);
            Check("THE FIX (CALL_CQ mode): reaching the Repeat Limit actively sends SET_TX_ENABLED 0 -- nothing else in this mode's own path stops TX",
                setTxDisabledSentCq, true);
            Check("THE FIX (CALL_CQ mode): callInProg is cleared immediately",
                wc.callInProg == null, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  RepeatLimitActivelyStopsTxTests (CALL_CQ) threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            cqListener.Stop();
        }
    }

    // ── Repeat-limit timing fix, 2026-08-24 (independent audit finding, CONFIRMED live --
    // KF4TST, Repeat Limit 3: attempts 1-3 transmitted normally, but on what would be attempt 4
    // the radio ACTUALLY KEYED UP for about a second before Jimmy's own halt landed) -- THE FIX.
    // RepeatLimitActivelyStopsTxTests above proves DiscardCall()'s OWN behavior once invoked
    // (via TestTriggerDiscardCall, which calls it directly) -- this proves WHEN it gets invoked
    // from the real trigger path: on attempt 3's own transmitting-just-ended edge, not on some
    // later new-decode-slot event that (per the real log) can arrive at essentially the same
    // moment the engine has already autonomously started keying the disallowed 4th attempt.
    // Every snapshot below deliberately keeps the SAME Radio.Slot -- DirectApplyDecodes' own
    // new-slot trigger must play NO role here; if the fix ever regressed back to needing a slot
    // change, the halt would never fire at all in this test, not just fire late. ─────────────────
    static void RepeatLimitStopsBeforeTheDisallowedAttemptKeysTests()
    {
        Console.WriteLine("\n── Repeat-limit timing fix: the halt fires on attempt 3's own transmitting-ended edge, before a 4th attempt could key -- THE FIX ──");
        bool setTxDisabledSent = false;
        bool txArmCommandSent = false;
        var listener = StartStubEngineHostWithResponses(line =>
        {
            if (line.StartsWith("SET_TX_ENABLED 0")) { setTxDisabledSent = true; return "OK"; }
            // Anything that could actually key the radio again for this call.
            if (line.StartsWith("CALL_CQ") || line.StartsWith("REPLY") || line.StartsWith("SET_TX_ENABLED 1"))
            {
                txArmCommandSent = true;
                return "OK";
            }
            return "OK";
        });
        if (listener == null)
        {
            Skip("RepeatLimitStopsBeforeTheDisallowedAttemptKeysTests", "engine control port already in use on this machine");
            return;
        }
        try
        {
            var ctrl = new Controller();
            var _ = ctrl.Handle; // force handle creation -- EnqueueDirectCommand's completion runs via ctrl.BeginInvoke
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.timeoutNumUpDown.Value = 3; // Repeat Limit = 3, matching the real KF4TST reproduction
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.ConnectDirectEngine("KB0UZT", "FN42");
            wc.TestStopPollTimer(); // the 1s SNAPSHOT poll would otherwise race this test's own connections

            const string call = "KF4TST";
            wc.callInProg = call;
            wc.TestSetTxEnabled(true);
            wc.TestStartDiscardCall(call);
            wc.UpdateMaxTxRepeat(); // picks up timeoutNumUpDown=3 into maxTxRepeat, same as TxRepeatChanged does live

            const int slot = 1000; // never changes below -- see this test's own header comment
            void ApplySnapshot(bool transmitting)
            {
                // txEnabled: true -- DirectApplyStatus reconciles Jimmy's local txEnabled from
                // this field on EVERY snapshot (see its own comment); omitting it would default
                // to false and silently stomp TestSetTxEnabled(true) below on the very first
                // snapshot, before DiscardCall() ever runs, hiding the exact SET_TX_ENABLED 0
                // send this test exists to prove.
                wc.TestApplyDirectSnapshot("KB0UZT", "FN42", ParseDirectSnapshot($@"{{
                    ""mycall"": ""KB0UZT"", ""mygrid"": ""FN42"",
                    ""radio"": {{ ""dialMhz"": 7.0475, ""transmitting"": {(transmitting ? "true" : "false")}, ""tuning"": false, ""slot"": {slot}, ""txEnabled"": true }},
                    ""recentDecodes"": []
                }}"));
            }

            // Attempts 1 and 2: transmitting starts, then ends -- two completed attempts, still
            // under the Repeat Limit of 3.
            ApplySnapshot(true);
            ApplySnapshot(false);
            ApplySnapshot(true);
            ApplySnapshot(false);
            Check("Setup: still armed after 2 completed attempts, well under the Repeat Limit of 3",
                wc.callInProg == call, true);
            Check("Setup: no halt sent yet", setTxDisabledSent, false);

            // Attempt 3: transmitting starts, then ends -- this is the 3rd completed attempt,
            // exactly at the configured Repeat Limit. THE FIX means the halt must land here, off
            // this same transmitting-ended edge -- no 4th snapshot required to trigger it.
            ApplySnapshot(true);
            ApplySnapshot(false);
            PumpUntil(() => setTxDisabledSent);

            Check("THE FIX: the halt is sent immediately on attempt 3's OWN transmitting-ended edge -- no 4th (disallowed) transmitting snapshot was needed to trigger it",
                setTxDisabledSent, true);
            Check("THE FIX: callInProg is cleared at that same moment",
                wc.callInProg == null, true);
            Check("THE FIX: no CALL_CQ/REPLY/SET_TX_ENABLED 1 was ever sent for this call",
                txArmCommandSent, false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  RepeatLimitStopsBeforeTheDisallowedAttemptKeysTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            listener.Stop();
        }
    }

    // ── TX First/RX First fix, 2026-08-24 (independent audit finding, CONFIRMED live -- the
    // configured hotkey always announced "Tx first selected, halted", never "second", and the
    // Advanced UI TX1/TX2 indication never changed either) -- THE FIX. Root cause: SetBandTxFirst
    // (WsjtxClient.Protocol.cs) never actually wrote `txFirst` -- a leftover from the classic UDP
    // transport, where the field was only ever updated later by a real WSJT-X's OWN confirming
    // StatusMessage, which does not exist under Direct-engine mode. `txFirst` is not cosmetic --
    // it is the real TX-period decision every CALL_CQ xmit gate and the call queue's own
    // opposite-period filtering read, so this was a stuck-TX-period bug, not just a wrong
    // announcement. No real engine connection is made (ConnectDirectEngine is never called, so
    // _directConnected stays false) -- HaltTx()'s own network send is gated on that, so this
    // proves the fix as a pure, fast unit test with no stub engine host needed. ────────────────
    static void ToggleTxFirstActuallyTogglesTests()
    {
        Console.WriteLine("\n── TX First/RX First fix: the hotkey actually toggles txFirst, alternating announcements -- THE FIX ──");
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.advancedCallLayout = true; // exercises the Advanced-UI TX1/TX2 label path too
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);

            Check("Setup: txFirst starts at its documented default (false)", wc.txFirst, false);

            wc.ToggleTxFirst();
            Check("THE FIX: the FIRST press actually flips txFirst (was permanently stuck before this fix)",
                wc.txFirst, true);
            CheckStr("...and announces 'Tx first selected, halted'",
                ctrl.statusText.Text, "Tx first selected, halted");
            CheckStr("...and updates the Advanced UI's call-list accessible name to match (RX2, since TX1 is now the transmit side)",
                ctrl.callListBox.AccessibleName, "RX2 Stations Available");

            wc.ToggleTxFirst();
            Check("THE FIX: the SECOND press flips it back -- proving this isn't just a one-shot fluke",
                wc.txFirst, false);
            CheckStr("...and announces 'Tx second selected, halted' -- THE bug the operator actually reported (only ever heard 'first')",
                ctrl.statusText.Text, "Tx second selected, halted");
            CheckStr("...and the call-list accessible name reverts too (RX1)",
                ctrl.callListBox.AccessibleName, "RX1 Stations Available");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  ToggleTxFirstActuallyTogglesTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── "Optimize throughput" scope fix, 2026-08-28 (from Jim's fix-list discussion after the
    // KE2ET "stuck after 3 exchanges" log trace): the queue-depth retry trim must stop applying
    // once the DX has actually answered. Before this, a Call-Next (Alt+N) pick whose QSO had
    // reached signal-report exchange could still be abandoned after as few as ceil(limit/3)
    // unanswered overs in a deep pileup, even though it was one over from complete. The trim is
    // still correct while Jimmy is only trying to GET a Call-Next pick's attention (no report
    // received yet); operator-selected calls (Space/Enter -> _manualCallInProg) were already
    // exempt. Pure synchronous test -- UpdateMaxTxRepeat() is invoked directly, exactly as
    // TxRepeatChanged and the per-decode-cycle boundary do live. ─────────────────────────────────
    static void OptimizeReducesOnlyUntilReportExchangedTests()
    {
        Console.WriteLine("\n── Optimize throughput: the queue-depth retry trim stops once the DX has answered with a report -- THE FIX ──");
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.optimizeCheckBox.Checked = true;      // the feature under test is ON
            ctrl.timeoutNumUpDown.Value = 12;          // Repeat limit 12 -> 4+ waiting trims to 12/3 = 4

            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);

            // Deep pileup: 4 calls waiting -> proportional factor 1/3.
            wc.callQueue.Enqueue("AA1AA");
            wc.callQueue.Enqueue("BB2BB");
            wc.callQueue.Enqueue("CC3CC");
            wc.callQueue.Enqueue("DD4DD");

            // Phase 1: a Call-Next pick Jimmy is still only CALLING -- no report from the DX yet,
            // not operator-selected. The trim SHOULD apply.
            wc.TestSetManualCallInProg(false);
            wc.callInProg = "DX1";
            wc.UpdateMaxTxRepeat();
            Check("still only calling an Alt+N pick in a deep pileup: retry budget is trimmed (12 -> 4)",
                wc.TestMaxTxRepeat == 4, true);

            // Phase 2: same call, same deep pileup, but the DX has now sent us a signal report.
            wc.allCallDict["DX1"] = new System.Collections.Generic.List<EnqueueDecodeMessage>
            {
                new EnqueueDecodeMessage
                {
                    Message = "KB0UZT DX1 -07",
                    Snr = -7,
                    RxDate = DateTime.UtcNow.Date,
                    SinceMidnight = DateTime.UtcNow.TimeOfDay
                }
            };
            wc.UpdateMaxTxRepeat();
            Check("THE FIX: once the DX has answered with a report, the trim stops -- full Repeat limit (12) restored",
                wc.TestMaxTxRepeat == 12, true);

            // Phase 3: regression -- an operator-selected call stays exempt regardless of report state.
            wc.allCallDict.Remove("DX1");
            wc.TestSetManualCallInProg(true);
            wc.UpdateMaxTxRepeat();
            Check("regression: an operator-selected (Space/Enter) call keeps the full Repeat limit even with no report yet",
                wc.TestMaxTxRepeat == 12, true);

            // Phase 4: regression -- with Optimize throughput OFF the trim never applies at all.
            ctrl.optimizeCheckBox.Checked = false;
            wc.TestSetManualCallInProg(false);
            wc.callInProg = null;
            wc.UpdateMaxTxRepeat();
            Check("regression: with Optimize throughput OFF the retry budget is always the full Repeat limit",
                wc.TestMaxTxRepeat == 12, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  OptimizeReducesOnlyUntilReportExchangedTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── Raw Decodes item 1, 2026-08-24 (operator request -- careful FT8/FT4 verification):
    // proves every decode EngineHost supplies in a real SNAPSHOT reaches TestRawDecodeHistory
    // intact -- CQs and directed replies alike, not just ones addressed to myCall -- for BOTH
    // FT8 and FT4 independently (two entirely separate WsjtxClient instances/decode sets, not
    // one re-run under a different label). This is the ingestion half; the side-labeling half
    // (below) is a separate, genuine bug this same investigation found and fixed. ─────────────
    static void RawDecodesIngestsEveryDecodeBothModesTests()
    {
        Console.WriteLine("\n── Raw Decodes: every EngineHost decode reaches the raw decode history, FT8 and FT4 independently ──");
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.advancedCallLayout = true;
            ctrl.advShowRaw = true;
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.TestSetMode("FT8");
            var ft8Snap = ParseDirectSnapshot(@"{
                ""mycall"": ""KB0UZT"", ""mygrid"": ""FN42"",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""tuning"": false, ""slot"": 5000 },
                ""recentDecodes"": [
                    { ""from"": ""W1AW"", ""message"": ""CQ W1AW FN31"", ""snr"": -5, ""dtSec"": 0.1, ""freqHz"": 1500 },
                    { ""from"": ""K2ABC"", ""message"": ""K2ABC KB0UZT -10"", ""snr"": -10, ""dtSec"": 0.2, ""freqHz"": 1600 },
                    { ""from"": ""VE3XYZ"", ""message"": ""CQ VE3XYZ FN25"", ""snr"": -3, ""dtSec"": 0.0, ""freqHz"": 1700 }
                ]
            }");
            wc.TestApplyDirectSnapshot("KB0UZT", "FN42", ft8Snap);
            Check("FT8: all 3 decodes from this period reached the raw decode history, none silently dropped",
                wc.TestRawDecodeHistory.Count == 3, true);
            Check("FT8: a plain CQ made it through", wc.TestRawDecodeHistory.Exists(d => d.Message == "CQ W1AW FN31"), true);
            Check("FT8: a directed reply (not addressed to myCall) made it through -- not filtered to 'to me' only",
                wc.TestRawDecodeHistory.Exists(d => d.Message == "K2ABC KB0UZT -10"), true);
            Check("FT8: the second CQ made it through too", wc.TestRawDecodeHistory.Exists(d => d.Message == "CQ VE3XYZ FN25"), true);

            var ctrl2 = new Controller();
            ctrl2.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl2.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl2.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl2.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl2.advancedCallLayout = true;
            ctrl2.advShowRaw = true;
            var wc2 = new WsjtxClient(ctrl2, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc2.TestSetMode("FT4");
            var ft4Snap = ParseDirectSnapshot(@"{
                ""mycall"": ""KB0UZT"", ""mygrid"": ""FN42"",
                ""radio"": { ""dialMhz"": 7.0475, ""transmitting"": false, ""tuning"": false, ""slot"": 6000 },
                ""recentDecodes"": [
                    { ""from"": ""N5AAA"", ""message"": ""CQ N5AAA EM12"", ""snr"": -8, ""dtSec"": 0.1, ""freqHz"": 1200 },
                    { ""from"": ""DL1BBB"", ""message"": ""DL1BBB KB0UZT R-15"", ""snr"": -15, ""dtSec"": 0.3, ""freqHz"": 1300 }
                ]
            }");
            wc2.TestApplyDirectSnapshot("KB0UZT", "FN42", ft4Snap);
            Check("FT4: both decodes from this period reached the raw decode history (independent WsjtxClient/decode set from the FT8 case above)",
                wc2.TestRawDecodeHistory.Count == 2, true);
            Check("FT4: a plain CQ made it through", wc2.TestRawDecodeHistory.Exists(d => d.Message == "CQ N5AAA EM12"), true);
            Check("FT4: a roger-report reply made it through", wc2.TestRawDecodeHistory.Exists(d => d.Message == "DL1BBB KB0UZT R-15"), true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  RawDecodesIngestsEveryDecodeBothModesTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── Raw Decodes item 1, 2026-08-24 -- THE FIX: side label (TX1/RX1/RX2/TX2) now reflects
    // txFirst instead of hardcoding "TX1"=even/"TX2"=odd regardless of which side Jimmy actually
    // transmits on. Verified independently for BOTH txFirst states, using an FT4-shaped even/odd
    // boundary (mode-driven parity, IsEvenPeriod's own FT4 branch) so this also stands in for the
    // "both alternating periods/sides work correctly on FT4" requirement -- the label FORMULA
    // itself has no mode dependence once parity is known, so one thorough pass here covers both
    // modes' labeling; RawDecodesIngestsEveryDecodeBothModesTests above independently covers
    // FT8-vs-FT4 ingestion. ───────────────────────────────────────────────────────────────────
    static void RawDecodesSideLabelReflectsTxFirstTests()
    {
        Console.WriteLine("\n── Raw Decodes: side label (TX1/RX1/RX2/TX2) reflects txFirst -- THE FIX ──");
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.advancedCallLayout = true;
            ctrl.advShowRaw = true;
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.TestSetMode("FT4");
            // Real connect/snapshot once, so myCall/classification state is properly initialized
            // (EffectiveClassification() needs it) -- recentDecodes deliberately empty, the
            // entries under test are added directly below instead (see TestShowRawDecodes' own
            // comment on why: real SinceMidnight can't be controlled from a test).
            wc.TestApplyDirectSnapshot("KB0UZT", "FN42", ParseDirectSnapshot(@"{
                ""mycall"": ""KB0UZT"", ""mygrid"": ""FN42"",
                ""radio"": { ""dialMhz"": 7.0475, ""transmitting"": false, ""tuning"": false, ""slot"": 1 },
                ""recentDecodes"": []
            }"));

            // FT4's own IsEvenPeriod branch: seconds-past-minute 2 is even ([0,7)), 10 is odd
            // ([7,15)) -- see WsjtxClient.cs's own IsEvenPeriod comment.
            wc.TestRawDecodeHistory.Add(new EnqueueDecodeMessage
            {
                Message = "EVENCALL KB0UZT FN42", SinceMidnight = new TimeSpan(0, 5, 2), AutoGen = true, New = true,
            });
            wc.TestRawDecodeHistory.Add(new EnqueueDecodeMessage
            {
                Message = "ODDCALL KB0UZT FN42", SinceMidnight = new TimeSpan(0, 5, 10), AutoGen = true, New = true,
            });

            wc.txFirst = true;
            wc.TestShowRawDecodes();
            var itemsTxFirstTrue = ctrl.advRawListBox.Items.Cast<string>().ToList();
            Check("txFirst=true: exactly 2 rows rendered", itemsTxFirstTrue.Count == 2, true);
            Check("txFirst=true: the EVEN-period decode is labeled TX1 (Jimmy's own transmit side)",
                itemsTxFirstTrue.Exists(s => s.Contains("EVENCALL") && s.Contains("TX1")), true);
            Check("txFirst=true: the ODD-period decode is labeled RX2 (the receive side)",
                itemsTxFirstTrue.Exists(s => s.Contains("ODDCALL") && s.Contains("RX2")), true);

            wc.txFirst = false;
            wc.TestShowRawDecodes();
            var itemsTxFirstFalse = ctrl.advRawListBox.Items.Cast<string>().ToList();
            Check("txFirst=false: exactly 2 rows rendered", itemsTxFirstFalse.Count == 2, true);
            Check("THE FIX: txFirst=false flips the EVEN-period decode's label to RX1 (it used to always say TX1 regardless of txFirst)",
                itemsTxFirstFalse.Exists(s => s.Contains("EVENCALL") && s.Contains("RX1")), true);
            Check("THE FIX: txFirst=false flips the ODD-period decode's label to TX2 (Jimmy's actual transmit side in this configuration)",
                itemsTxFirstFalse.Exists(s => s.Contains("ODDCALL") && s.Contains("TX2")), true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  RawDecodesSideLabelReflectsTxFirstTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── T16 fix, 2026-08-23 (CONFIRMED bug, CRITICAL -- W5PF, 2026-08-21): a completed QSO no
    // longer leaves stale queue/current-message state, and a same-tick trailing decode from the
    // just-completed call does not re-admit it ──
    // Previously the completion branch in DirectApplyStatus only dequeued the just-worked call
    // "if (txMode == TxModes.CALL_CQ)" -- Listen/Reply-mode completions (this exact shape) left
    // the call sitting in CallQueueStore where ShowStatus's "first"/"to you" wording kept
    // treating it as still-waiting, and curCmd kept pointing at the finished exchange. Unlike
    // DirectModePlumbingParityTests' own Scenario 5 (which deliberately isolates the tx_now ->
    // LogQso wiring alone, with the call present only in allCallDict), this test genuinely
    // queues the call first -- T16's defect is specifically about the QUEUE surviving
    // completion -- and includes a same-tick trailing RR73 decode from that same call, the
    // exact "late same-slot decode" shape both the log evidence and the master requirements
    // call out by name.
    static void CompletedQsoRemovesStaleQueueStateTests()
    {
        Console.WriteLine("\n── T16 fix: completed QSO removes stale queue/current-message state (W5PF) -- THE FIX ──");
        string tmpDb = Path.Combine(Path.GetTempPath(), "JimmyTest_W5PF_" + Guid.NewGuid().ToString("N") + ".db");
        string prevTestDbPath = Environment.GetEnvironmentVariable("JIMMY_TEST_DB_PATH");
        Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", tmpDb);
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.anyMsgRadioButton.Checked = true;
            ctrl.replyDxCheckBox.Checked = true;
            ctrl.replyLocalCheckBox.Checked = true;
            ctrl.advancedCallLayout = true;   // bypass T/R period gating -- matches the replay harness's own documented setup
            ctrl.replyRR73CheckBox.Checked = true;   // exercise the courtesy-RR73 re-admit branch under test

            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            var lookupManager = new LookupManager();
            lookupManager.RegisterProviderFirst(new TestFixtureLookupProvider());
            lookupManager.Initialize(useLookupData: true, qrzEnabled: false, qrzUser: null, qrzPass: null, qrzCacheDays: 1,
                lotwEnabled: true, lotwDays: 1, clubLogAppKey: null, clubLogDays: 1, fccUlsEnabled: false);
            wc.lookupManager = lookupManager;

            const string myCall = "KB0UZT";
            const string myGrid = "FN42";
            const string qsoCall = "W5PF";

            wc.callInProg = qsoCall;
            wc.allCallDict[qsoCall] = new List<EnqueueDecodeMessage>
            {
                new EnqueueDecodeMessage { Message = $"{myCall} {qsoCall} R-15", Snr = -15, RxDate = DateTime.UtcNow.Date, SinceMidnight = DateTime.UtcNow.TimeOfDay },
            };
            wc.sentReportList.Add(qsoCall);
            // Genuinely queued -- T16's own defect is specifically about the queue surviving
            // completion, so (unlike Scenario 5 above) the call must actually be in callQueue.
            wc.callDict[qsoCall] = new EnqueueDecodeMessage
            {
                Message = $"CQ {qsoCall} EM12", Snr = -10, AutoGen = true,
                RxDate = DateTime.UtcNow.Date, SinceMidnight = DateTime.UtcNow.TimeOfDay,
            };
            wc.callQueue.Enqueue(qsoCall);

            // One snapshot: the engine's own tx_now reaches the final 73 (completion) AND, in
            // the SAME tick, a trailing RR73 decode from the very call that just completed
            // arrives -- the "late same-slot decode" shape.
            var snap = ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""",
                ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": true, ""slot"": 3000 },
                ""recentDecodes"": [
                    { ""from"": """ + qsoCall + @""", ""snr"": -5, ""dtSec"": 0.1, ""freqHz"": 1500.0, ""message"": """ + myCall + " " + qsoCall + @" RR73"" }
                ],
                ""qso"": { ""state"": ""done"", ""txNow"": """ + qsoCall + " " + myCall + @" 73"" }
            }");
            wc.TestApplyDirectSnapshot(myCall, myGrid, snap);

            Check("Setup: the completed QSO is logged", wc.logList.Contains(qsoCall), true);
            Check("Setup: callInProg is cleared", wc.callInProg == null, true);
            Check("THE FIX: completed call is removed from the queue, not left as still-waiting",
                wc.callQueue.Contains(qsoCall), false);
            Check("THE FIX: completed call is removed from callDict too",
                wc.callDict.ContainsKey(qsoCall), false);
            Check("THE FIX: a same-tick trailing RR73 from the just-completed call does not re-admit it",
                wc.callQueue.Contains(qsoCall), false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  CompletedQsoRemovesStaleQueueStateTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", prevTestDbPath);
            try { File.Delete(tmpDb); } catch { }
        }
    }

    // ── Final-QSO notification ordering fix, item 5, 2026-08-24 -- TWO fixes, both needed,
    // confirmed via a real K4XN QSO (log_8-24-2026.txt) that exposed the second one after the
    // first alone shipped:
    //
    // Part 1 (curTxMode/txStr, WsjtxClient.Display.cs): the engine can report qso.TxNow as the
    // final 73/RR73 text -- LogQso's own trigger, DirectApplyStatus's Is73orRR73(curTxMsg)
    // branch -- BEFORE `transmitting` itself flips true for that period. Fixed so "logged" and
    // "sending 73" get composed into ONE string together instead of two separate ones.
    //
    // Part 2 (deferEligible, this test's own real find): composing the combined string was not
    // enough -- loggedCall was missing from deferEligible's own exclusion list, even though its
    // sibling finalSignoffCall ("a final 73", this method's own comment names it explicitly) was
    // already there. That let the combined "logged, Transmitting, sending 73" render get
    // SILENTLY BATCHED (ScheduleStatusAnnounce) instead of announced immediately -- and a
    // deferred render's one-shot flags (loggedCall included) are consumed/reset regardless of
    // whether that specific render is ever actually delivered. The real K4XN log shows exactly
    // this: the combined text was built correctly (visible in DebugOutput) but never announced;
    // 12 seconds later, once `transmitting` itself flipped true and produced a fresh IMMEDIATE
    // render, "a fresher render always wins" (this file's own render-vs-defer comment) delivered
    // a plain "Transmitting, sending 73" with no "logged" at all -- what the operator actually
    // heard, after already hearing the (synchronous, defer-independent) log sound moments
    // earlier. This test's own first run (before Part 2 existed) silently accepted the deferred/
    // pending text as good enough and passed anyway -- masking exactly this bug; it now requires
    // genuine IMMEDIATE delivery, matching the real symptom. ─────────────────────────────────
    static void FinalQsoLoggedAndSendingAnnounceTogetherTests()
    {
        Console.WriteLine("\n── Final-QSO notification ordering: 'logged' and 'sending 73' merge into one utterance -- THE FIX ──");
        string tmpDb = Path.Combine(Path.GetTempPath(), "JimmyTest_FinalQso_" + Guid.NewGuid().ToString("N") + ".db");
        string prevTestDbPath = Environment.GetEnvironmentVariable("JIMMY_TEST_DB_PATH");
        Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", tmpDb);
        try
        {
            var ctrl = new Controller();
            var _ = ctrl.Handle;
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.anyMsgRadioButton.Checked = true;
            ctrl.replyDxCheckBox.Checked = true;
            ctrl.replyLocalCheckBox.Checked = true;
            ctrl.advancedCallLayout = true;

            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            var lookupManager = new LookupManager();
            lookupManager.RegisterProviderFirst(new TestFixtureLookupProvider());
            lookupManager.Initialize(useLookupData: true, qrzEnabled: false, qrzUser: null, qrzPass: null, qrzCacheDays: 1,
                lotwEnabled: true, lotwDays: 1, clubLogAppKey: null, clubLogDays: 1, fccUlsEnabled: false);
            wc.lookupManager = lookupManager;

            var fakeStatusView = new FakeStatusView();
            wc.StatusView = fakeStatusView;
            // TestApplyDirectSnapshot bypasses ConnectDirectEngine/DirectPollTick's own response
            // handler, which in real operation always sets NegoState->RECD before the first
            // DirectApplyStatus call -- see DirectInitialConnectResyncsTierAndPeriodTests' own
            // comment for the full explanation. Left at WAIT, ShowStatus takes its early-return
            // "connecting" branch and never renders the text this test is checking at all.
            WsjtxMessage.NegoState = WsjtxMessage.NegoStates.RECD;

            const string myCall = "KB0UZT";
            const string myGrid = "FN42";
            const string qsoCall = "KF4TST";

            wc.callInProg = qsoCall;
            // cqPaused defaults true (WsjtxClient.cs's own field default) and only ever goes
            // false once the operator actually starts transmitting/replying -- true here would
            // route ShowStatus through its OWN separate cqPaused-branch template (line ~980),
            // which never references txStr at all, masking the exact thing this test exists to
            // check. Real usage: by the time an active QSO with callInProg is underway, this is
            // already false.
            wc.cqPaused = false;
            wc.allCallDict[qsoCall] = new List<EnqueueDecodeMessage>
            {
                new EnqueueDecodeMessage { Message = $"{myCall} {qsoCall} R-15", Snr = -15, RxDate = DateTime.UtcNow.Date, SinceMidnight = DateTime.UtcNow.TimeOfDay },
            };
            wc.sentReportList.Add(qsoCall);

            // The exact real-log shape: the engine reports the final 73 as tx_now on a snapshot
            // where `transmitting` is STILL false -- the race this fix closes.
            var snap = ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""",
                ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 7.0475, ""transmitting"": false, ""slot"": 3000 },
                ""recentDecodes"": [],
                ""qso"": { ""state"": ""done"", ""txNow"": """ + qsoCall + " " + myCall + @" 73"" }
            }");
            wc.TestApplyDirectSnapshot(myCall, myGrid, snap);

            Check("Setup: the QSO is logged", wc.logList.Contains(qsoCall), true);
            Check("Setup: callInProg is cleared", wc.callInProg == null, true);

            // THE REAL BUG (Part 2): this render must be delivered IMMEDIATELY, not merely
            // composed correctly and left sitting in _pendingStatusText -- a still-pending one
            // can be silently dropped for good the moment any later, unrelated immediate render
            // arrives first (this file's own "a fresher render always wins" comment), which is
            // exactly what the real K4XN QSO showed. TestPendingStatusText must be null here;
            // if it isn't, the render was deferred and this test must fail, not fall back to
            // reading the pending text as if that were good enough (that's the exact blind spot
            // that let the real bug through the first time this test was written).
            Check("THE FIX (part 2): the combined render was NOT deferred/batched -- delivered immediately",
                wc.TestPendingStatusText == null, true);
            Check("THE FIX (part 2): ...and it actually reached the status view (RenderStatus was called)",
                fakeStatusView.RenderStatusCount >= 1, true);
            string text = fakeStatusView.LastStatusText ?? "";
            Check("THE FIX (part 1): 'logged' and 'sending' both appear in the SAME rendered utterance, not split across two separate ones",
                text.IndexOf("logged", StringComparison.OrdinalIgnoreCase) >= 0 && text.IndexOf("sending", StringComparison.OrdinalIgnoreCase) >= 0,
                true);
            Check("THE FIX (part 1): the headline reads Transmitting, not Receiving -- the final 73 is what's actually about to go out",
                text.IndexOf("Transmitting", StringComparison.OrdinalIgnoreCase) >= 0, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  FinalQsoLoggedAndSendingAnnounceTogetherTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", prevTestDbPath);
            try { File.Delete(tmpDb); } catch { }
        }
    }

    // ── Item 4, 2026-08-24 (operator request): on-demand clock sync status hotkey. Reuses the
    // exact same real-pipeline PublishDt pattern ClockSyncNotificationTests already established
    // (feed real slot changes with a known DT, drive timeOffset/_clockWasAcceptable through the
    // genuine CalcAvgTimeOffset transition-gate -- no test-only field setters), so this doubles
    // as independent confirmation that the underlying automatic out-of-sync/back-in-sync
    // machinery this hotkey reads is still intact, not just that ReportClockStatus's own text
    // is right. ──────────────────────────────────────────────────────────────────────────────
    static void ReportClockStatusTests()
    {
        Console.WriteLine("\n── ReportClockStatus (Alt+Y): on-demand clock sync status -- THE FIX ──");
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            ctrl.anyMsgRadioButton.Checked = true;
            ctrl.replyDxCheckBox.Checked = true;
            ctrl.replyLocalCheckBox.Checked = true;

            var fakeStatusView = new FakeStatusView();
            wc.StatusView = fakeStatusView;

            const string myCall = "KB0UZT";
            const string myGrid = "FN42";
            ulong slot = 9000;
            void PublishDt(double dt)
            {
                var snap = ParseDirectSnapshot(@"{
                    ""mycall"": """ + myCall + @""",
                    ""mygrid"": """ + myGrid + @""",
                    ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""slot"": " + (slot++) + @" },
                    ""recentDecodes"": [
                        { ""from"": ""W1AW"", ""snr"": -5, ""dtSec"": " + dt.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + @", ""freqHz"": 1500.0, ""message"": ""CQ W1AW FN31"" }
                    ]
                }");
                wc.TestApplyDirectSnapshot(myCall, myGrid, snap);
            }

            wc.ReportClockStatus();
            CheckStr("Before any period has completed: reports 'not yet measured', not a misleading 'good'",
                fakeStatusView.LastShowMessageText, "Clock sync not yet measured");

            PublishDt(0.1);
            PublishDt(0.1);
            PublishDt(0.1);
            wc.ReportClockStatus();
            CheckStr("Acceptable offset -> reports good, with the real measured offset",
                fakeStatusView.LastShowMessageText, "Clock sync good, offset 0.1 seconds");

            PublishDt(2.0);
            PublishDt(2.0);
            wc.ReportClockStatus();
            CheckStr("Unacceptable offset -> reports out of sync, with the real measured offset",
                fakeStatusView.LastShowMessageText, "Clock out of sync, offset 2.0 seconds, check clock time");

            PublishDt(0.1);
            PublishDt(0.1);
            wc.ReportClockStatus();
            CheckStr("Recovers -> reports good again, matching the automatic ClockSynced transition",
                fakeStatusView.LastShowMessageText, "Clock sync good, offset 0.1 seconds");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  ReportClockStatusTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── Item 2, 2026-08-24 (operator request): "while transmitting, transmit-related speech can
    // take priority and receive-side notifications are suppressed until receiving resumes" --
    // proves the new opt-in setting actually gates the "N available stations" summary while
    // transmitting (qsoState defaults to CALLING, WsjtxClient.cs's own field default -- calling
    // CQ with nobody yet in progress, the one case that clause can still fire mid-Tx even before
    // this setting exists), and that it changes NOTHING when off (default), preserving today's
    // behavior for every operator who hasn't touched this new checkbox. ───────────────────────
    static void SuppressReceiveNotificationsDuringTxTests()
    {
        Console.WriteLine("\n── Item 2: suppress receive-side 'available stations' summary while transmitting -- THE FIX ──");
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.anyMsgRadioButton.Checked = true;
            ctrl.replyDxCheckBox.Checked = true;
            ctrl.replyLocalCheckBox.Checked = true;
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.CALL_CQ);
            WsjtxMessage.NegoState = WsjtxMessage.NegoStates.RECD;
            wc.cqPaused = false;

            var fakeStatusView = new FakeStatusView();
            wc.StatusView = fakeStatusView;

            const string myCall = "KB0UZT";
            const string myGrid = "FN42";

            // Calling CQ (qsoState defaults CALLING, callInProg stays null), actively
            // transmitting, with a real station queued -- the exact shape that can still
            // announce "N available stations" mid-Tx today.
            var snap = ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""",
                ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": true, ""slot"": 4000 },
                ""recentDecodes"": [
                    { ""from"": ""W1AW"", ""snr"": -5, ""dtSec"": 0.1, ""freqHz"": 1500.0, ""message"": ""CQ W1AW FN31"" }
                ]
            }");

            ctrl.suppressReceiveNotificationsDuringTx = false;
            wc.TestApplyDirectSnapshot(myCall, myGrid, snap);
            string textOff = fakeStatusView.LastStatusText ?? wc.TestPendingStatusText ?? "";
            Check("Setting OFF (default): the routine available-stations summary can still appear while transmitting/calling CQ -- unchanged existing behavior",
                textOff.IndexOf("available station", StringComparison.OrdinalIgnoreCase) >= 0, true);

            // Fresh client for the ON case -- avoids any carried-over dedup/defer state from the
            // OFF render above affecting this one.
            var ctrl2 = new Controller();
            ctrl2.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl2.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl2.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl2.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl2.anyMsgRadioButton.Checked = true;
            ctrl2.replyDxCheckBox.Checked = true;
            ctrl2.replyLocalCheckBox.Checked = true;
            ctrl2.suppressReceiveNotificationsDuringTx = true;
            var wc2 = new WsjtxClient(ctrl2, 2237, false, false, WsjtxClient.TxModes.CALL_CQ);
            WsjtxMessage.NegoState = WsjtxMessage.NegoStates.RECD;
            wc2.cqPaused = false;
            var fakeStatusView2 = new FakeStatusView();
            wc2.StatusView = fakeStatusView2;
            wc2.TestApplyDirectSnapshot(myCall, myGrid, snap);
            string textOn = fakeStatusView2.LastStatusText ?? wc2.TestPendingStatusText ?? "";
            Check("THE FIX: setting ON suppresses the available-stations summary while transmitting, even while calling CQ",
                textOn.IndexOf("available station", StringComparison.OrdinalIgnoreCase) >= 0, false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  SuppressReceiveNotificationsDuringTxTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── Profiles feature, 2026-08-24 (operator request): ResolveActiveIniPath -- the startup
    // decision "given the built-in/default file and the Profiles folder, which .ini path should
    // THIS session actually load from" -- is the single highest-stakes piece of this whole
    // feature: a bug here could affect every existing user's startup, not just operators who
    // touch Profiles. Proves the critical "no lost settings" requirement (no activeProfile key
    // at all falls straight through to today's exact behavior) alongside the three other real
    // cases (a real profile selected, a missing base file, a stale/deleted profile selected). ──
    static void ResolveActiveIniPathTests()
    {
        Console.WriteLine("\n── Profiles: ResolveActiveIniPath startup resolution -- THE FIX ──");
        string tmpDir = Path.Combine(Path.GetTempPath(), "JimmyTest_Profiles_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tmpDir);
            string baseIniPath = Path.Combine(tmpDir, "Jimmy Next.ini");
            string profilesDir = Path.Combine(tmpDir, "Profiles");

            Check("No base file at all (fresh install) -- resolves to the base path unchanged, nothing to fail on",
                Controller.ResolveActiveIniPath(baseIniPath, profilesDir) == baseIniPath, true);

            // THE critical "no lost settings" requirement: an existing install's base file with
            // NO activeProfile key at all (every install before this feature ever existed) must
            // fall straight through to today's exact behavior.
            File.WriteAllText(baseIniPath, "[Jimmy Next]\r\nmyCall=KB0UZT\r\n");
            Check("Existing user, base file present but no activeProfile key -- still resolves to the base path (no lost settings)",
                Controller.ResolveActiveIniPath(baseIniPath, profilesDir) == baseIniPath, true);

            // A real, existing named profile is selected.
            Directory.CreateDirectory(profilesDir);
            string homeBasePath = Path.Combine(profilesDir, "HomeBase.ini");
            File.WriteAllText(homeBasePath, "[Jimmy Next]\r\nmyCall=KB0UZT\r\n");
            var baseIni = new IniFile(baseIniPath);
            baseIni.Write("activeProfile", "HomeBase");
            Check("THE FIX: activeProfile names a real profile -- resolves to that profile's own path",
                Controller.ResolveActiveIniPath(baseIniPath, profilesDir) == homeBasePath, true);

            // The selected profile was deleted/moved since it was last chosen -- self-healing,
            // never a startup failure.
            File.Delete(homeBasePath);
            Check("THE FIX: activeProfile names a profile that no longer exists -- falls back to the base path, not a crash/missing-file startup",
                Controller.ResolveActiveIniPath(baseIniPath, profilesDir) == baseIniPath, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  ResolveActiveIniPathTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }

    // ── Independent audit finding, 2026-08-30: SupportReportBuilder.GetIniPath always returned
    // the base LocalAppData\<name>\<name>.ini, so a support ZIP from an operator running a named
    // profile redacted/attached the WRONG settings file. It now delegates to
    // Controller.ActiveIniFilePath, which applies the same active-profile resolution Form_Load
    // does (covered end-to-end by ResolveActiveIniPathTests above). This checks the thin wrapper:
    // the base-path composition matches what the old code produced, and the startup test-mode
    // skip is honored (so a test context, or any JIMMY_TEST_DB_PATH session, still gets the base
    // file and never a Profiles path). ──
    static void ActiveIniFilePathTests()
    {
        Console.WriteLine("\n── SupportReport active-profile INI path (Controller.ActiveIniFilePath) -- THE FIX ──");
        string prev = Environment.GetEnvironmentVariable("JIMMY_TEST_DB_PATH");
        try
        {
            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", Path.Combine(Path.GetTempPath(), "x.db")); // -> IsTestMode

            // The name Jimmy's OWN assembly reports (Controller.ProgramName / the old
            // GetIniPath both use Assembly.GetExecutingAssembly from inside that assembly) --
            // NOT the test assembly's name.
            string name = typeof(Controller).Assembly.GetName().Name;
            string expectedBase = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                name, name + ".ini");

            string actual = Controller.ActiveIniFilePath();
            CheckStr("in test mode: resolves to exactly the base file the old GetIniPath computed",
                actual, expectedBase);
            Check("in test mode: never a Profiles path (startup's test-mode skip is honored)",
                actual.IndexOf("\\Profiles\\", StringComparison.OrdinalIgnoreCase) < 0, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  ActiveIniFilePathTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", prev);
        }
    }

    // ── Profiles feature, 2026-08-24: ListNamedProfiles -- the Profiles folder's own contents
    // are the single source of truth for "what named profiles exist" (Load/Delete both read this
    // list), so it must reflect real .ini files there, by name, with no folder = no profiles
    // (not a crash) and never include the built-in/default profile (which isn't a file in this
    // folder at all). ────────────────────────────────────────────────────────────────────────
    static void ListNamedProfilesTests()
    {
        Console.WriteLine("\n── Profiles: ListNamedProfiles -- THE FIX ──");
        try
        {
            // ListNamedProfiles() reads LocalApplicationData\{Jimmy assembly name}\Profiles --
            // Controller.ProgramName() resolves Assembly.GetExecutingAssembly() from INSIDE
            // Controller.cs (the Jimmy assembly, "Jimmy Next" for this Debug build), which is
            // NOT the same assembly as this test file's own (JimmyTests) -- reconstructing the
            // path here with THIS file's assembly name would silently check a different,
            // unrelated folder. Ask ListNamedProfiles() itself whether anything's there instead
            // of guessing its path independently; only run the "empty folder" assertion when it
            // genuinely reports empty, e.g. no real profile has ever been saved on this machine.
            bool preexisted = Controller.ListNamedProfiles().Count > 0;
            if (!preexisted)
                Check("No Profiles folder yet -- returns an empty list, not an exception",
                    Controller.ListNamedProfiles().Count == 0, true);
            else
                Console.WriteLine("  SKIP  'no Profiles folder yet' case -- this machine already has real profiles saved (fine, real machine state)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  ListNamedProfilesTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── T15 fix, 2026-08-23 (LIKELY bug -- KJ5OUL, 2026-08-21): a later gridless message does
    // not downgrade a station's already-known distance/bearing to unknown ──
    // A CQ carrying a grid resolves real distance/azimuth; a later message from the same
    // station in the same band session (a report, common right after Escape/Halt's
    // RequeueAbortedCall re-enqueues the in-progress call using its own captured decode) often
    // carries no grid at all, and CallQueueStore.UpdateCall's priority-improvement branch can
    // fully REPLACE the queued decode (and its classification) with that gridless one. Before
    // this fix, the replacement classification's Distance/Azimuth/Country/Continent silently
    // reset to ClassifiedCall's own -1/empty "unknown" defaults even though the station's real
    // location was already known this band session. Drives MergeBandSessionLocation directly
    // (the exact call ProcessDecodeMsg makes for every decode, real grid or not) rather than
    // through the full admission pipeline (period gating, weak-signal floor, DX-only filters),
    // which is orthogonal plumbing already covered elsewhere and would make this test fragile
    // against unrelated admission-gate changes.
    static void BandSessionLocationSurvivesGridlessMessageTests()
    {
        Console.WriteLine("\n── T15 fix: known distance/bearing survives a later gridless message -- THE FIX ──");
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);

            const string call = "K4YT";

            // Step 1: a CQ carrying a real grid resolved real distance/azimuth/country/continent.
            var known = new ClassifiedCall { Distance = 750, Azimuth = 42, Country = "USA", Continent = "NA" };
            wc.TestMergeBandSessionLocation(call, known);

            // Step 2: a later gridless message -- Classify() could not resolve a grid, so this
            // starts out at ClassifiedCall's own "unknown" defaults, exactly as production code
            // computes it for a gridless report/73/RR73.
            var gridless = new ClassifiedCall(); // Distance=-1, Azimuth=-1, Country="", Continent=""
            wc.TestMergeBandSessionLocation(call, gridless);

            Check("THE FIX: distance is NOT downgraded to unknown by the gridless message",
                gridless.Distance == 750, true);
            Check("THE FIX: azimuth is NOT downgraded to unknown by the gridless message",
                gridless.Azimuth == 42, true);
            Check("THE FIX: country is NOT downgraded to unknown by the gridless message",
                gridless.Country == "USA", true);
            Check("THE FIX: continent is NOT downgraded to unknown by the gridless message",
                gridless.Continent == "NA", true);

            // A genuinely fresher resolved value must still win, not get stuck on the first one.
            var moved = new ClassifiedCall { Distance = 900, Azimuth = 88, Country = "USA", Continent = "NA" };
            wc.TestMergeBandSessionLocation(call, moved);
            Check("A genuinely fresh resolved distance still updates the cache (not a one-shot latch)",
                moved.Distance == 900, true);

            // T19 companion: the cache is band-session-scoped -- a confirmed band change clears
            // it, so a DIFFERENT station reusing behavior after a band change starts clean.
            wc.TestApplyDirectSnapshot("KB0UZT", "FN42", ParseDirectSnapshot(@"{
                ""mycall"": ""KB0UZT"", ""mygrid"": ""FN42"",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""slot"": 9000 },
                ""recentDecodes"": []
            }"));
            wc.TestApplyDirectSnapshot("KB0UZT", "FN42", ParseDirectSnapshot(@"{
                ""mycall"": ""KB0UZT"", ""mygrid"": ""FN42"",
                ""radio"": { ""dialMhz"": 18.100, ""transmitting"": false, ""slot"": 9001 },
                ""recentDecodes"": []
            }"));
            var afterBandChange = new ClassifiedCall();
            wc.TestMergeBandSessionLocation(call, afterBandChange);
            Check("Band-session cache is cleared on a confirmed band change, not carried forward",
                afterBandChange.Distance == -1, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  BandSessionLocationSurvivesGridlessMessageTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── T19 fix, 2026-08-23 (PARTIAL/CONFIRMED -- W5PF band-change log evidence, 2026-08-21),
    // STRENGTHENED per independent audit finding (2026-08-23): a confirmed band change flushes
    // stale curTxMsg/curCmd/replyCmd/replyDecode AND terminally clears callInProg/discard-retry
    // state, while the QSO is still genuinely ACTIVE at the moment the band changes ──
    // The original version of this test seeded a txNow of "<call> <mycall> 73" (a FINAL 73) as
    // its own setup snapshot -- DirectApplyStatus's own Is73orRR73(curTxMsg) branch treats that
    // as a real QSO completion and calls SetCallInProg(null) right there, during setup, before
    // the band-change snapshot is ever applied (independent audit finding: "the new band test
    // accidentally completes its seeded QSO before changing bands"). That made every assertion
    // below pass whether or not the band-change handler itself cleared anything -- callInProg
    // was already null from ordinary QSO-completion logic, not proven cleared BY the band
    // change. Fixed here by seeding a mid-QSO report exchange (not a 73/RR73) as the setup
    // snapshot's txNow -- Is73orRR73 never fires, so callInProg/curCmd/replyCmd/replyDecode/the
    // discard tracker all remain genuinely live right up to the moment the band-change snapshot
    // is applied, and the assertions below prove the band-change handler itself is what clears
    // them, not a QSO that already finished on its own.
    static void ConfirmedBandChangeFlushesStaleTxStateTests()
    {
        Console.WriteLine("\n── T19 fix (strengthened): confirmed band change terminally clears an ACTIVE QSO's callInProg/TX-message/discard state -- THE FIX ──");
        try
        {
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);

            const string myCall = "KB0UZT";
            const string myGrid = "FN42";
            const string oldBandCall = "W5PF";

            // First snapshot: establishes a known band (20m, 14.074 MHz) and a real prior dial
            // frequency to change FROM -- band-change detection compares against lastDialFrequency.
            var snap20m = ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""", ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""slot"": 5000 },
                ""recentDecodes"": []
            }");
            wc.TestApplyDirectSnapshot(myCall, myGrid, snap20m);
            CheckStr("Setup: resolves to 20m before the band change under test", wc.CurrentBandStr, "20m");

            // Establish a genuinely ACTIVE (not completing/completed) QSO on the old band:
            // callInProg set, a mid-exchange report (NOT 73/RR73) as the last TX text, and the
            // retry/discard tracker armed -- everything item 2 says must survive to the moment
            // of the band change and then be terminally cleared BY it.
            wc.callInProg = oldBandCall;
            wc.TestSetCurCmd($"{oldBandCall} {myCall} R-10");
            wc.TestSetReplyCmd($"{oldBandCall} {myCall} R-10");
            wc.TestSetReplyDecode(new EnqueueDecodeMessage { Message = $"{myCall} {oldBandCall} -10", AutoGen = true });
            // A real, actively-retrying QSO has TX enabled. Set directly in the snapshot's own
            // radio.txEnabled field, NOT via TestSetTxEnabled beforehand -- DirectApplyStatus
            // unconditionally reconciles txEnabled from every snapshot ("txEnabled =
            // radio.TxEnabled", see its own comment), which would silently stomp a pre-set test
            // value back to false (DirectRadioStatus.TxEnabled is a plain bool, not nullable, so
            // an omitted JSON field also deserializes to false).
            var snapMidQso = ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""", ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": true, ""slot"": 5001, ""txEnabled"": true },
                ""recentDecodes"": [],
                ""qso"": { ""state"": ""awaitReport"", ""txNow"": """ + oldBandCall + " " + myCall + @" R-10"" }
            }");
            wc.TestApplyDirectSnapshot(myCall, myGrid, snapMidQso);
            // Repeat limit authoritative-stop fix, 2026-08-24: DiscardCall is now unconditionally
            // terminal the moment it's called (see its own comment) -- arming the discard tracker
            // is deferred until AFTER this setup snapshot's own "new slot" processing runs (rather
            // than before, as this test used to), so that snapshot's discard-check never sees an
            // already-armed tracker and can't prematurely terminate the QSO before the band
            // change under test ever runs. The tracker is still proven armed by the very next
            // assertion, and terminally cleared BY the band change further down -- this only
            // changes WHEN it gets armed within setup, not what the test actually proves.
            wc.TestStartDiscardCall(oldBandCall);
            // Prove the QSO is genuinely still active at this point -- NOT already completed by
            // DirectApplyStatus's own Is73orRR73 completion branch (that's exactly the flaw being
            // fixed in this test itself; if this assertion ever fails, everything below it would
            // be testing the wrong thing again).
            Check("Setup: callInProg is still the old-band station -- QSO genuinely still active, not auto-completed",
                wc.callInProg == oldBandCall, true);
            Check("Setup: curTxMsg carries the old band's mid-QSO report text",
                wc.TestCurTxMsg == $"{oldBandCall} {myCall} R-10", true);
            Check("Setup: discard/retry tracker is armed for the old-band call",
                wc.TestDiscardCall == oldBandCall, true);

            // A real confirmed band change: 20m -> 17m (18.100 MHz), well past freqChangeThreshold
            // -- happening WHILE the QSO above is still active, per the required test shape.
            var snap17m = ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""", ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 18.100, ""transmitting"": false, ""slot"": 5002 },
                ""recentDecodes"": []
            }");
            wc.TestApplyDirectSnapshot(myCall, myGrid, snap17m);
            CheckStr("Confirmed band change actually resolved to the new band (17m)", wc.CurrentBandStr, "17m");

            Check("THE FIX: callInProg is terminally cleared by the band change, not carried into the new band",
                wc.callInProg == null, true);
            Check("THE FIX: curTxMsg no longer carries the old band's stale mid-QSO text",
                wc.TestCurTxMsg != $"{oldBandCall} {myCall} R-10", true);
            Check("THE FIX: curCmd is flushed on confirmed band change",
                wc.TestCurCmd == null, true);
            Check("THE FIX: replyCmd is flushed on confirmed band change",
                wc.TestReplyCmd == null, true);
            Check("THE FIX: replyDecode is flushed on confirmed band change",
                wc.TestReplyDecode == null, true);
            Check("THE FIX: the discard/retry tracker is disarmed, not left armed for a station on the old band",
                wc.TestDiscardCall == null, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  ConfirmedBandChangeFlushesStaleTxStateTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
    }

    // ── Direct band change rebuilds the per-band award live-tag cache against the NEW band ──
    // Bug (report 2026-08-31, "Worked All States - 160m Needed" showing on 40m/80m): the Direct
    // band-change handler (WsjtxClient.Direct.cs) called ctrl.LoadHrcCache()/RefreshStillNeedCache()
    // while dialFrequency -- and therefore wsjtxClient.CurrentBandStr, which both caches key off --
    // still pointed at the band just LEFT (dialFrequency wasn't assigned until a few lines after
    // that block). So after tuning 20m -> 40m, a band-restricted award like WAS_20M ([Match]
    // Bands=20m) stayed in activeAwardTags and every 40m decode from a still-needed state got
    // tagged "Worked All States - 20m Needed" until the next band change (which then repeated the
    // mistake one band later -- "always one band behind"). The fix moves the dialFrequency update
    // above those two cache-rebuild calls.
    static void DirectBandChangeRebuildsAwardCacheForNewBandTests()
    {
        Console.WriteLine("\n── Direct band change: per-band award live-tag cache is rebuilt against the NEW band, not the one just left -- THE FIX ──");
        string tmpDb = Path.Combine(Path.GetTempPath(),
            "JimmyTest_BandChangeAwardCache_" + Guid.NewGuid().ToString("N") + ".db");
        string prevTestDbPath = Environment.GetEnvironmentVariable("JIMMY_TEST_DB_PATH");
        var def = new RuleDefinition
        {
            Id = "TEST_WASBANDLAG_20M", Name = "Worked All States - 20m (test)",
            FormatVersion = 1, Enabled = true,
            GroupBy = RuleGroupBy.State, Universe = "US_50_STATES",
            Confirmation = RuleConfirmation.None, Target = RuleTargetType.All,
            Bands = new List<string> { "20m" },
        };
        bool defRegistered = false;
        try
        {
            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", tmpDb);
            using (var db = new LogbookDb(tmpDb))
            {
                InsertQso(db, "W6CA", "CA", dxcc: 291, zone: 3, band: "20m");

                RuleLibrary.Definitions.Add(def);
                defRegistered = true;

                var ctrl = new Controller();
                ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
                ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
                ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
                ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
                var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
                ctrl.wsjtxClient = wc;   // RefreshStillNeedCache() no-ops unless Controller.wsjtxClient is set
                ctrl.activeAwardRuleIds.Add(def.Id);

                const string myCall = "KB0UZT";
                const string myGrid = "EN34";

                // On 20m: the 20m-only award is genuinely live-tagging.
                var snap20m = ParseDirectSnapshot(@"{
                    ""mycall"": """ + myCall + @""", ""mygrid"": """ + myGrid + @""",
                    ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""slot"": 5000 },
                    ""recentDecodes"": []
                }");
                wc.TestApplyDirectSnapshot(myCall, myGrid, snap20m);
                CheckStr("Setup: resolves to 20m before the band change under test", wc.CurrentBandStr, "20m");
                ctrl.RefreshStillNeedCache();
                Check("Setup: the 20m-only award IS live-tagging while the radio is on 20m",
                      wc.activeAwardTags.ContainsKey(def.Id), true);

                // Tune 20m -> 40m. The handler rebuilds the award cache internally; with the fix
                // that rebuild sees CurrentBandStr == "40m", so the 20m-only award drops out.
                var snap40m = ParseDirectSnapshot(@"{
                    ""mycall"": """ + myCall + @""", ""mygrid"": """ + myGrid + @""",
                    ""radio"": { ""dialMhz"": 7.074, ""transmitting"": false, ""slot"": 5001 },
                    ""recentDecodes"": []
                }");
                wc.TestApplyDirectSnapshot(myCall, myGrid, snap40m);
                CheckStr("Confirmed band change actually resolved to the new band (40m)", wc.CurrentBandStr, "40m");
                Check("THE FIX: after tuning 20m -> 40m the 20m-only award is NO LONGER live-tagging "
                      + "(its cache was rebuilt against the new band, not the one just left)",
                      wc.activeAwardTags.ContainsKey(def.Id), false);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  DirectBandChangeRebuildsAwardCacheForNewBandTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            if (defRegistered) RuleLibrary.Definitions.Remove(def);
            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", prevTestDbPath);
            try { File.Delete(tmpDb); } catch { }
        }
    }

    // ── Active QSO survives band change, second half (independent audit finding, 2026-08-23):
    // "Protect against delayed old-band asynchronous results being admitted after the new
    // band/session becomes authoritative" ──
    // ConfirmedBandChangeFlushesStaleTxStateTests above proves the band-change handler itself
    // terminally clears an active QSO. This proves the OTHER half of item 2: a REPLY already
    // sent to the engine before the band change, whose confirmation arrives only AFTER the band
    // change already ran, must not resurrect callInProg/curCmd/replyCmd for the station that was
    // heard on the OLD band -- see ReplyTo's own capturedBandSessionEpoch check (WsjtxClient.cs).
    // Drives the REAL production path (NextCall -> dialogTimer2_Tick -> ReplyTo -> DirectSendReply)
    // against a stub engine host that holds the REPLY connection open (same technique
    // HaltAbortsInFlightCommandTests already uses to prove a command is genuinely in flight, not
    // merely queued), performs a real confirmed band change while it's still blocked, then
    // releases the stub's delayed "OK" and proves it lands as a no-op.
    static void DelayedReplyAfterBandChangeDoesNotResurrectStaleQsoTests()
    {
        Console.WriteLine("\n── Active QSO survives band change: a REPLY confirmed AFTER the band already changed must not resurrect the old-band QSO -- THE FIX ──");
        var acceptedReply = new System.Threading.ManualResetEventSlim(false);
        var releaseReply = new System.Threading.ManualResetEventSlim(false);
        var engineListener = StartStubEngineHostWithResponses(line =>
        {
            if (line.StartsWith("REPLY"))
            {
                acceptedReply.Set();
                releaseReply.Wait(5000);
                return "OK";
            }
            return "OK";
        });
        if (engineListener == null)
        {
            Skip("DelayedReplyAfterBandChangeDoesNotResurrectStaleQsoTests", "control port 58239 already in use by another Jimmy/engine-host session");
            return;
        }
        try
        {
            var ctrl = new Controller();
            var _ = ctrl.Handle; // see SessionTokenAuthenticationTests' own comment on why this is required
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.ConnectDirectEngine("KB0UZT", "FN42");
            wc.TestStopPollTimer(); // the 1s SNAPSHOT poll would otherwise race this test's own connections

            const string myCall = "KB0UZT";
            const string myGrid = "FN42";
            const string call = "W5PF";

            // Establish a known band (20m) so the later snapshot is a real, detectable change.
            wc.TestApplyDirectSnapshot(myCall, myGrid, ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""", ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""slot"": 6000 },
                ""recentDecodes"": []
            }"));

            var dmsg = new EnqueueDecodeMessage
            {
                Message = $"{myCall} {call} EM12",
                Snr = -10,
                AutoGen = true,
                RxDate = DateTime.UtcNow.Date,
                SinceMidnight = DateTime.UtcNow.TimeOfDay,
            };
            wc.callDict[call] = dmsg;
            wc.callQueue.Enqueue(call);

            wc.NextCall(false, 0);
            bool accepted = acceptedReply.Wait(3000);
            // dialogTimer2 (20ms) posts to the UI thread -- Wait() above blocks THIS thread while
            // that timer and the REPLY send both need the message loop pumped to actually run, so
            // pump alongside the wait instead of a bare Wait() (same reasoning PumpUntil documents).
            if (!accepted)
            {
                var sw0 = System.Diagnostics.Stopwatch.StartNew();
                while (!acceptedReply.Wait(0) && sw0.ElapsedMilliseconds < 3000)
                {
                    System.Windows.Forms.Application.DoEvents();
                    System.Threading.Thread.Sleep(5);
                }
                accepted = acceptedReply.Wait(0);
            }
            Check("Setup: REPLY was actually dequeued and is blocked mid-flight (stub is holding it)",
                accepted, true);

            // A real confirmed band change WHILE the REPLY above is still in flight, unconfirmed.
            wc.TestApplyDirectSnapshot(myCall, myGrid, ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""", ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 18.100, ""transmitting"": false, ""slot"": 6001 },
                ""recentDecodes"": []
            }"));
            CheckStr("Setup: the band change actually resolved to the new band (17m) while REPLY was still in flight",
                wc.CurrentBandStr, "17m");
            Check("Setup: callInProg is null immediately after the band change (REPLY hasn't committed anything yet)",
                wc.callInProg == null, true);

            // Now let the delayed REPLY "OK" land, and pump long enough for its BeginInvoke-
            // marshaled completion callback to actually run.
            releaseReply.Set();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1500)
            {
                System.Windows.Forms.Application.DoEvents();
                System.Threading.Thread.Sleep(5);
            }

            Check("THE FIX: the delayed REPLY confirmation does not resurrect callInProg for the old-band station",
                wc.callInProg == null, true);
            Check("THE FIX: curCmd is not stomped by the delayed, superseded REPLY completion",
                wc.TestCurCmd == null, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  DelayedReplyAfterBandChangeDoesNotResurrectStaleQsoTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            releaseReply.Set();
            try { engineListener.Stop(); } catch { }
        }
    }

    // ── Unified contact-supersede guard (2026-08-31): _contactEpoch replaces the two
    // single-purpose guards it merges (_bandSessionEpoch = band change only; _haltGeneration =
    // halt only), which between them left an operator abort at the WsjtxClient layer and a tier
    // switch guarded by neither. Same in-flight-REPLY harness as the band-change test above, but
    // the supersede trigger here is AbortContact() (Escape / Alt+H) -- which reaches _contactEpoch
    // via CancelQso -> SetCallInProg(null). A REPLY confirmed after the operator has aborted must
    // not resurrect callInProg/curCmd for the station they just walked away from.
    static void DelayedReplyAfterOperatorAbortDoesNotResurrectStaleQsoTests()
    {
        Console.WriteLine("\n── Unified contact epoch: a REPLY confirmed AFTER an operator abort must not resurrect the QSO -- THE FIX ──");
        var acceptedReply = new System.Threading.ManualResetEventSlim(false);
        var releaseReply = new System.Threading.ManualResetEventSlim(false);
        var engineListener = StartStubEngineHostWithResponses(line =>
        {
            if (line.StartsWith("REPLY"))
            {
                acceptedReply.Set();
                releaseReply.Wait(5000);
                return "OK";
            }
            return "OK";
        });
        if (engineListener == null)
        {
            Skip("DelayedReplyAfterOperatorAbortDoesNotResurrectStaleQsoTests", "control port 58239 already in use by another Jimmy/engine-host session");
            return;
        }
        try
        {
            var ctrl = new Controller();
            var _ = ctrl.Handle;
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            wc.ConnectDirectEngine("KB0UZT", "FN42");
            wc.TestStopPollTimer();

            const string myCall = "KB0UZT";
            const string myGrid = "FN42";
            const string call = "W5PF";

            wc.TestApplyDirectSnapshot(myCall, myGrid, ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""", ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": false, ""slot"": 6100 },
                ""recentDecodes"": []
            }"));

            var dmsg = new EnqueueDecodeMessage
            {
                Message = $"{myCall} {call} EM12",
                Snr = -10,
                AutoGen = true,
                RxDate = DateTime.UtcNow.Date,
                SinceMidnight = DateTime.UtcNow.TimeOfDay,
            };
            wc.callDict[call] = dmsg;
            wc.callQueue.Enqueue(call);

            wc.NextCall(false, 0);
            bool accepted = acceptedReply.Wait(3000);
            if (!accepted)
            {
                var sw0 = System.Diagnostics.Stopwatch.StartNew();
                while (!acceptedReply.Wait(0) && sw0.ElapsedMilliseconds < 3000)
                {
                    System.Windows.Forms.Application.DoEvents();
                    System.Threading.Thread.Sleep(5);
                }
                accepted = acceptedReply.Wait(0);
            }
            Check("Setup: REPLY was actually dequeued and is blocked mid-flight (stub is holding it)",
                accepted, true);

            // Operator hits Escape / Alt+H WHILE the REPLY above is still in flight, unconfirmed.
            wc.AbortContact();
            Check("Setup: callInProg is null immediately after AbortContact (REPLY hasn't committed anything yet)",
                wc.callInProg == null, true);

            releaseReply.Set();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1500)
            {
                System.Windows.Forms.Application.DoEvents();
                System.Threading.Thread.Sleep(5);
            }

            Check("THE FIX: the delayed REPLY confirmation does not resurrect callInProg after an operator abort",
                wc.callInProg == null, true);
            Check("THE FIX: curCmd is not stomped by the delayed, superseded REPLY completion",
                wc.TestCurCmd == null, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  DelayedReplyAfterOperatorAbortDoesNotResurrectStaleQsoTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            releaseReply.Set();
            try { engineListener.Stop(); } catch { }
        }
    }

    // ── Codex Audit 03 release blocker #2 regression test: a failed QSO write is never
    // presented as success, and a later retry for the SAME dedup key actually retries ──
    // Forces a REAL local-DB write failure (JIMMY_TEST_DB_PATH pointed at a directory that does
    // not exist -- not a mock), then fixes the path and re-triggers RequestLog for the identical
    // call/band/mode/date/time (same dedup key) a second time. callInProg is re-armed before
    // that second trigger: DirectApplyStatus's own Is73orRR73 branch calls SetCallInProg(null)
    // unconditionally right after the first LogQso call, win or lose (see its own comment,
    // WsjtxClient.Direct.cs) -- found writing this test, this means the identical-snapshot
    // "natural" repeated-poll re-entry the surrounding code comments describe does NOT actually
    // re-invoke RequestLog a second time on its own once callInProg is cleared; re-arming it here
    // stands in for whatever real second trigger (a later retry mechanism, or a second completion
    // signal for the same QSO) would supply the same call to ClaimLiveLoggedQso again. That
    // pre-existing UI-flow gap is a separate concern from finding 2 -- this test targets the
    // dedup-key claim/release fix in isolation, exactly as Codex's own audit asked for.
    static void FailedQsoWriteDoesNotFalselyAnnounceSuccessTests()
    {
        Console.WriteLine("\n── Failed QSO write is not falsely announced as logged, and a retry actually retries -- THE FIX ──");
        // A path UNDER AN EXISTING FILE, not a missing directory: Directory.CreateDirectory
        // auto-creates missing parent directories (found live writing this test -- a merely
        // nonexistent directory doesn't actually force a failure), but it cannot create a
        // directory where a file already exists, so LogbookDb's own Directory.CreateDirectory
        // call reliably throws here.
        string blockerFile = Path.Combine(Path.GetTempPath(), "JimmyTest_Blocker_" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blockerFile, "blocks a directory from being created at this path");
        string brokenDbPath = Path.Combine(blockerFile, "test.db");
        string workingDbPath = Path.Combine(Path.GetTempPath(),
            "JimmyTest_ClaimRelease_" + Guid.NewGuid().ToString("N") + ".db");
        string prevTestDbPath = Environment.GetEnvironmentVariable("JIMMY_TEST_DB_PATH");
        try
        {
            // WsjtxClient's own constructor opens a SEPARATE, persistent classification LogbookDb
            // (unprotected -- found live writing this test: it threw straight out of `new
            // WsjtxClient(...)` when JIMMY_TEST_DB_PATH was already broken at that point). Construct
            // on a working path first; the broken path is only switched in right before the actual
            // write attempt below, which is the one this test targets.
            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", workingDbPath);
            var ctrl = new Controller();
            ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
            ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
            ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
            var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);

            const string myCall = "KB0UZT";
            const string myGrid = "FN42";
            const string qsoCall = "N3XYZ";
            wc.callInProg = qsoCall;
            wc.allCallDict[qsoCall] = new List<EnqueueDecodeMessage>
            {
                new EnqueueDecodeMessage
                {
                    Message = $"{myCall} {qsoCall} R-15",
                    Snr = -15,
                    RxDate = DateTime.UtcNow.Date,
                    SinceMidnight = DateTime.UtcNow.TimeOfDay,
                },
            };
            // LogQso's own precondition (WsjtxClient.cs: "if (!sentReportList.Contains(call))
            // return -- never reported SNR to the DX station") -- normally set by an earlier
            // "awaitReport" snapshot (see DirectModePlumbingParityTests' scenario 5); set
            // directly here since this test only needs the final 73 step.
            wc.sentReportList.Add(qsoCall);

            var snap73 = ParseDirectSnapshot(@"{
                ""mycall"": """ + myCall + @""",
                ""mygrid"": """ + myGrid + @""",
                ""radio"": { ""dialMhz"": 14.074, ""transmitting"": true, ""slot"": 2000 },
                ""recentDecodes"": [],
                ""qso"": { ""state"": ""done"", ""txNow"": """ + qsoCall + " " + myCall + @" 73"" }
            }");

            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", brokenDbPath);
            wc.TestApplyDirectSnapshot(myCall, myGrid, snap73);
            Check("First attempt (broken DB path) does not falsely add the call to today's logged list",
                wc.logList.Contains(qsoCall), false);

            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", workingDbPath);
            // Re-arm callInProg: SetCallInProg(null) already cleared it after the first attempt
            // above (see this test's own comment) -- this simulates whatever real trigger would
            // supply the same QSO to RequestLog again for a retry.
            wc.callInProg = qsoCall;
            wc.TestApplyDirectSnapshot(myCall, myGrid, snap73);
            Check("THE FIX: once the DB path is fixed, a later retry for the same dedup key actually retries and succeeds",
                wc.logList.Contains(qsoCall), true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  FailedQsoWriteDoesNotFalselyAnnounceSuccessTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            Environment.SetEnvironmentVariable("JIMMY_TEST_DB_PATH", prevTestDbPath);
            try { File.Delete(workingDbPath); } catch { }
            try { File.Delete(blockerFile); } catch { }
        }
    }

    // ── UDP-to-Direct parity pass, 2026-08-12: connection-loss signal for a hung control port ──
    // The UDP path announces "WSJT-X disconnected" (HeartbeatNotRecd, WsjtxClient.Protocol.cs)
    // once its heartbeat watchdog times out. DirectPollTick's own failure branch used to only
    // log to DebugOutput -- a hung-but-still-running engine control port produced no
    // user-facing signal at all under Direct mode. This exercises the new
    // DirectHandlePollFailure threshold/announce-once logic directly (via its test hook, since
    // driving the real network-facing DirectPollTick would require an actual engine process).
    static void DirectPollFailureNotificationTests()
    {
        Console.WriteLine("\n── Direct-path poll-failure connection-loss notification ──");

        var ctrl = new Controller();
        ctrl.callCqOptionsButton = new System.Windows.Forms.Button { Visible = false };
        ctrl.ignoreWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
        ctrl.minSnrNumUpDown = new System.Windows.Forms.NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
        ctrl.removeOnWeakSnrCheckBox = new System.Windows.Forms.CheckBox();
        var wc = new WsjtxClient(ctrl, 2237, false, false, WsjtxClient.TxModes.LISTEN);

        var settings = new NotificationSettings();
        settings.Policies[NotificationEventType.ConnectionLost].RepeatSeconds = 0;
        var delivery = new FakeNotificationDelivery();
        wc.Notify = new NotificationCenter(settings, delivery);

        var savedNegoState = WsjtxMessage.NegoState;
        try
        {
            WsjtxMessage.NegoState = WsjtxMessage.NegoStates.RECD;
            wc.TestSetDirectConnected(true);

            wc.TestDirectHandlePollFailure();
            wc.TestDirectHandlePollFailure();
            Check("Two consecutive poll failures (below the 3-failure threshold) -> no announcement yet",
                delivery.AnnounceCount == 0, true);

            wc.TestDirectHandlePollFailure();
            Check("Third consecutive poll failure -> ConnectionLost announced",
                delivery.AnnounceCount == 1, true);
            Check("...NegoState reset to WAIT so a later reconnect is detected as fresh",
                WsjtxMessage.NegoState == WsjtxMessage.NegoStates.WAIT, true);

            // Once already announced this loss episode, further failures must not repeat it.
            wc.TestDirectHandlePollFailure();
            wc.TestDirectHandlePollFailure();
            Check("Further failures in the same loss episode do not re-announce",
                delivery.AnnounceCount == 1, true);

            // Disconnected entirely (e.g. operator switched transports) -> must never announce,
            // and the early-return guard means the failure count itself must not move either.
            int countBeforeDisconnect = wc.TestDirectConsecutivePollFailures;
            wc.TestSetDirectConnected(false);
            wc.TestDirectHandlePollFailure();
            Check("Not connected at all -> poll failure handling is a no-op",
                delivery.AnnounceCount == 1 && wc.TestDirectConsecutivePollFailures == countBeforeDisconnect, true);
        }
        finally
        {
            WsjtxMessage.NegoState = savedNegoState;
        }
    }

    // ── Controller.FindPreservedSelectionIndex: list-selection identity tracking ──
    // Regression coverage for the WM3PEN/N8BB mismatch (2026-07-06): a list refresh
    // must never silently leave the selection on an unrelated station just because
    // it landed at the same numeric position as the one the operator was actually on.
    static void FindPreservedSelectionIndexTests()
    {
        Console.WriteLine("\n── Controller.FindPreservedSelectionIndex ──");

        var oldKeys = new List<string> { "KF8CXC", "N8BB", "WM3PEN", "VK9DX" };

        // Station moved to a different position -- must follow it, not the old slot.
        var reordered = new List<string> { "N8BB", "KF8CXC", "VK9DX", "WM3PEN" };
        int idx = Controller.FindPreservedSelectionIndex(oldKeys, 2, reordered);
        Check("Selected station (WM3PEN, was index 2) found at its new index 3", idx == 3, true);

        // Station removed entirely -- must return -1 (deselect), never guess a neighbor.
        var withoutIt = new List<string> { "KF8CXC", "N8BB", "VK9DX" };
        idx = Controller.FindPreservedSelectionIndex(oldKeys, 2, withoutIt);
        Check("Selected station removed from list -> -1 (deselect, not a guess)", idx == -1, true);

        // Nothing changed -- same index.
        idx = Controller.FindPreservedSelectionIndex(oldKeys, 2, oldKeys);
        Check("Unchanged list -> same index preserved", idx == 2, true);

        // Invalid prior selection index -- no crash, no selection.
        idx = Controller.FindPreservedSelectionIndex(oldKeys, -1, reordered);
        Check("No prior selection (-1) -> -1", idx == -1, true);
        idx = Controller.FindPreservedSelectionIndex(oldKeys, 99, reordered);
        Check("Out-of-range prior index -> -1, not a crash", idx == -1, true);

        // Empty new list -- can't possibly still be selected.
        idx = Controller.FindPreservedSelectionIndex(oldKeys, 2, new List<string>());
        Check("Empty new list -> -1", idx == -1, true);

        // The exact scenario from the live bug report: WM3PEN selected at index 2 in
        // the old list; after a reorder, N8BB ends up at that same index 2 instead,
        // while WM3PEN moves to index 1. The old (buggy) code clamped the raw index
        // and would have silently selected N8BB. Confirm the fix follows WM3PEN
        // instead of landing on whatever now occupies its old slot.
        var liveOld = new List<string> { "KF8CXC", "N8BB", "WM3PEN", "VK9DX" };
        var liveNewReordered = new List<string> { "KF8CXC", "WM3PEN", "N8BB", "VK9DX" };
        idx = Controller.FindPreservedSelectionIndex(liveOld, 2, liveNewReordered);
        Check("WM3PEN/N8BB regression: follows WM3PEN to its new index 1, not N8BB's index 2", idx == 1 && liveNewReordered[idx] == "WM3PEN", true);
    }

    // Controller.ResolveUdpListenAddress and its regression coverage here were removed
    // 2026-08-18 along with the rest of the classic WSJT-X/UDP transport (WsjtxProtocolAdapter,
    // ConnectNativeEngine/UdpLoop) -- see Controller.cs's own comment at that removal site. The
    // real fresh-install crash this used to guard against (IPAddress.Parse(null) throwing
    // ArgumentNullException) is now structurally impossible rather than merely handled: nothing
    // parses an IP address for Jimmy's own transport at all anymore, in production or in test
    // mode, so there is no more call site left for this regression to protect.

    // ── WsjtxClient.ResolveDispatchIndex: Enter/Space/dbl-click dispatch-side re-lookup ──
    // Regression coverage for the dispatch-side half of the WM3PEN/N8BB class of bug
    // (2026-07-06): NextCall's dialogTimer2 dispatch is deferred ~20ms, and the queue
    // reorders on essentially every decode cycle in that window. The operator's selected
    // call must still be worked wherever it now sits -- or, if it truly left the queue,
    // nothing must be worked at all. Comparing against the stale original index (the
    // first attempt at this fix) is wrong: it treats an ordinary reorder as "gone" and
    // silently does nothing even though the call is still sitting right there.
    static void ResolveDispatchIndexTests()
    {
        Console.WriteLine("\n── WsjtxClient.ResolveDispatchIndex ──");

        var queue = new List<string> { "KF8CXC", "N8BB", "WM3PEN", "VK9DX" };
        Func<string, int> lookup = call => queue.IndexOf(call);

        Check("No identity captured (null expected) -> raw idx used as-is",
            WsjtxClient.ResolveDispatchIndex(null, 2, lookup) == 2, true);

        Check("Selected call still at its original index -> same index",
            WsjtxClient.ResolveDispatchIndex("WM3PEN", 2, lookup) == 2, true);

        // The live regression: operator selected WM3PEN at index 2; by dispatch time the
        // queue reordered and WM3PEN now sits at index 1 (N8BB took its old slot). Must
        // follow WM3PEN to its new index, not bail just because the raw idx moved.
        var reordered = new List<string> { "KF8CXC", "WM3PEN", "N8BB", "VK9DX" };
        Func<string, int> lookupReordered = call => reordered.IndexOf(call);
        Check("Selected call moved to a new index -> follows it there, not the old slot",
            WsjtxClient.ResolveDispatchIndex("WM3PEN", 2, lookupReordered) == 1, true);

        // Selected call actually left the queue (removed/timed out/logged) -- lookup
        // returns -1. Must propagate -1 (abort), never fall back to a guess.
        Func<string, int> lookupGone = call => -1;
        Check("Selected call gone from queue -> -1 (abort, not a guess)",
            WsjtxClient.ResolveDispatchIndex("WM3PEN", 2, lookupGone) == -1, true);
    }

    // ── Controller.FormatSpotWatchCalls / ParseSpotWatchCalls: DX Spot Watch list round-trip ──
    // The Spot Watch list is deliberately separate from Wanted Calls (added 2026-07-07) so it
    // never affects call-queue ranking. Same shape as the (private, untested) wantedCalls
    // helpers -- covered here since these were made public specifically for testability.
    static void SpotWatchCallsRoundTripTests()
    {
        Console.WriteLine("\n── Controller.FormatSpotWatchCalls / ParseSpotWatchCalls ──");

        var calls = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "K2A", "w1aw/13", "GB13COL" };
        string formatted = Controller.FormatSpotWatchCalls(calls);
        CheckStr("Format: sorted case-insensitively, comma-separated, original casing preserved",
            formatted, "GB13COL,K2A,w1aw/13");

        var parsed = Controller.ParseSpotWatchCalls("k2a, W1AW/13  GB13COL");
        Check("Parse: comma/space separated, uppercased, trimmed", parsed.SetEquals(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "K2A", "W1AW/13", "GB13COL" }), true);

        Check("Parse: null input -> empty set, no crash", Controller.ParseSpotWatchCalls(null).Count == 0, true);
        Check("Parse: whitespace-only input -> empty set", Controller.ParseSpotWatchCalls("   ").Count == 0, true);
        Check("Format: null input -> empty string, no crash", Controller.FormatSpotWatchCalls(null) == "", true);
        Check("Format: empty set -> empty string", Controller.FormatSpotWatchCalls(new HashSet<string>()) == "", true);

        var roundTrip = Controller.ParseSpotWatchCalls(Controller.FormatSpotWatchCalls(calls));
        Check("Round-trip: format then parse recovers the same set (case-insensitive)",
            roundTrip.SetEquals(calls), true);
    }

    // ── Controller.BandAppliesToLiveTag: per-band award live-tag gating ─────────
    // A band-restricted award (e.g. the WAS_*M per-band awards, [Match] Bands=6m)
    // must only live-tag decodes while the radio is actually on one of its bands.
    // RefreshStillNeedCache() previously substituted whatever band the radio was
    // currently on for ANY band-restricted award, which would silently tag
    // decodes on the wrong band as satisfying an unrelated single-band award.
    static void BandAppliesToLiveTagTests()
    {
        Console.WriteLine("\n── Controller.BandAppliesToLiveTag ──");

        Check("No band restriction (empty list): always applies, regardless of current band",
              Controller.BandAppliesToLiveTag(new List<string>(), "20m"), true);
        Check("No band restriction, current band unknown (null/blank): still always applies",
              Controller.BandAppliesToLiveTag(new List<string>(), ""), true);

        var sixMeterOnly = new List<string> { "6m" };
        Check("Band-restricted award: applies when current band matches (6m == 6m)",
              Controller.BandAppliesToLiveTag(sixMeterOnly, "6m"), true);
        Check("Band-restricted award: matching is case-insensitive",
              Controller.BandAppliesToLiveTag(sixMeterOnly, "6M"), true);
        Check("Band-restricted award: does NOT apply on an unrelated band (operating on 15m, award is 6m-only)",
              Controller.BandAppliesToLiveTag(sixMeterOnly, "15m"), false);
        Check("Band-restricted award: does NOT apply when current band is unknown",
              Controller.BandAppliesToLiveTag(sixMeterOnly, ""), false);

        var multiBand = new List<string> { "160m", "80m", "40m" };
        Check("Multi-band restriction: applies to any listed band",
              Controller.BandAppliesToLiveTag(multiBand, "80m"), true);
        Check("Multi-band restriction: does not apply to a band not in the list",
              Controller.BandAppliesToLiveTag(multiBand, "20m"), false);
    }

    // ── AwardMatcher.Match: pure award-matching logic (extracted from WsjtxClient's old
    // MatchedAwardRuleId so it's testable without a live LookupManager/UDP pipeline) ──
    static Dictionary<string, WsjtxClient.ActiveAwardTag> MakeTags(RuleGroupBy groupBy, params string[] setValues)
    {
        return new Dictionary<string, WsjtxClient.ActiveAwardTag>
        {
            ["TEST_RULE"] = new WsjtxClient.ActiveAwardTag
            {
                RuleId = "TEST_RULE", RuleName = "Test Rule", GroupBy = groupBy,
                Set = new HashSet<string>(setValues, StringComparer.OrdinalIgnoreCase),
            }
        };
    }

    static void AwardMatcherMatchTests()
    {
        Console.WriteLine("\n── AwardMatcher.Match ──");

        Check("Empty activeAwardTags -> no match",
              AwardMatcher.Match(new Dictionary<string, WsjtxClient.ActiveAwardTag>(), "GB13COL", null, null, () => 0, () => 0) == null, true);

        Check("Null/empty call -> no match",
              AwardMatcher.Match(MakeTags(RuleGroupBy.Callsign, "GB13COL"), "", null, null, () => 0, () => 0) == null, true);

        // Callsign GroupBy (e.g. 13 Colonies Bonus Stations)
        var callsignTags = MakeTags(RuleGroupBy.Callsign, "GB13COL", "WM3PEN");
        Check("Callsign GroupBy: matched call returns the rule Id",
              AwardMatcher.Match(callsignTags, "GB13COL", null, null, () => 0, () => 0) == "TEST_RULE", true);
        Check("Callsign GroupBy: unmatched call returns null",
              AwardMatcher.Match(callsignTags, "K1ABC", null, null, () => 0, () => 0) == null, true);

        // State GroupBy
        var stateTags = MakeTags(RuleGroupBy.State, "CA", "TX");
        Check("State GroupBy: matched state returns the rule Id",
              AwardMatcher.Match(stateTags, "K6ABC", "CA", null, () => 0, () => 0) == "TEST_RULE", true);
        Check("State GroupBy: null state (no grid decoded) returns null",
              AwardMatcher.Match(stateTags, "K6ABC", null, null, () => 0, () => 0) == null, true);
        Check("State GroupBy: unmatched state returns null",
              AwardMatcher.Match(stateTags, "K6ABC", "NY", null, () => 0, () => 0) == null, true);

        // CqZone GroupBy -- delegate only invoked when this branch is actually reached
        var cqZoneTags = MakeTags(RuleGroupBy.CqZone, "5", "14");
        Check("CqZone GroupBy: matched zone (via delegate) returns the rule Id",
              AwardMatcher.Match(cqZoneTags, "PY5SNL", null, null, () => 14, () => 0) == "TEST_RULE", true);
        Check("CqZone GroupBy: unmatched zone returns null",
              AwardMatcher.Match(cqZoneTags, "PY5SNL", null, null, () => 8, () => 0) == null, true);
        Check("CqZone GroupBy: zone 0 (unresolved) never matches",
              AwardMatcher.Match(cqZoneTags, "PY5SNL", null, null, () => 0, () => 0) == null, true);

        // Continent GroupBy
        var continentTags = MakeTags(RuleGroupBy.Continent, "EU", "AS");
        Check("Continent GroupBy: matched continent returns the rule Id",
              AwardMatcher.Match(continentTags, "G3HRC", null, "EU", () => 0, () => 0) == "TEST_RULE", true);
        Check("Continent GroupBy: unmatched continent returns null",
              AwardMatcher.Match(continentTags, "G3HRC", null, "NA", () => 0, () => 0) == null, true);

        // Dxcc GroupBy -- delegate only invoked when this branch is actually reached
        var dxccTags = MakeTags(RuleGroupBy.Dxcc, "291", "1");
        Check("Dxcc GroupBy: matched entity (via delegate) returns the rule Id",
              AwardMatcher.Match(dxccTags, "GB13COL", null, null, () => 0, () => 1) == "TEST_RULE", true);
        Check("Dxcc GroupBy: unmatched entity returns null",
              AwardMatcher.Match(dxccTags, "GB13COL", null, null, () => 0, () => 999) == null, true);

        // Multiple simultaneously-active awards -- must find the one that actually matches
        var multi = new Dictionary<string, WsjtxClient.ActiveAwardTag>
        {
            ["A"] = new WsjtxClient.ActiveAwardTag { RuleId = "A", RuleName = "A", GroupBy = RuleGroupBy.Callsign, Set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "K1ABC" } },
            ["B"] = new WsjtxClient.ActiveAwardTag { RuleId = "B", RuleName = "B", GroupBy = RuleGroupBy.Callsign, Set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GB13COL" } },
        };
        Check("Multiple active awards: matches whichever one actually contains the call",
              AwardMatcher.Match(multi, "GB13COL", null, null, () => 0, () => 0) == "B", true);

        // Defensive: a null delegate must not throw (callers should always pass one, but
        // this runs on the per-decode hot path -- a crash here must never be possible).
        Check("CqZone GroupBy: null delegate treated as zone 0, does not throw",
              AwardMatcher.Match(cqZoneTags, "PY5SNL", null, null, null, null) == null, true);
    }

    // ── AwardMatcher.ShouldRejectAlreadyWorked: the already-worked-per-band admission
    // gate's exception logic (WsjtxClient.AddSelectedCall) ──
    static void AwardMatcherAlreadyWorkedGateTests()
    {
        Console.WriteLine("\n── AwardMatcher.ShouldRejectAlreadyWorked ──");

        Check("New call on band -> never rejected regardless of other flags",
              AwardMatcher.ShouldRejectAlreadyWorked(isNewCallOnBand: true, isPota: false, isNewDxccCategory: false, isStillNeededByActiveAward: false), false);

        Check("Already worked, no exceptions apply -> rejected",
              AwardMatcher.ShouldRejectAlreadyWorked(isNewCallOnBand: false, isPota: false, isNewDxccCategory: false, isStillNeededByActiveAward: false), true);

        Check("Already worked, POTA -> allowed (POTA can repeat)",
              AwardMatcher.ShouldRejectAlreadyWorked(isNewCallOnBand: false, isPota: true, isNewDxccCategory: false, isStillNeededByActiveAward: false), false);

        Check("Already worked, new-DXCC category -> allowed",
              AwardMatcher.ShouldRejectAlreadyWorked(isNewCallOnBand: false, isPota: false, isNewDxccCategory: true, isStillNeededByActiveAward: false), false);

        Check("Already worked, still needed by an active award -> allowed (the fix)",
              AwardMatcher.ShouldRejectAlreadyWorked(isNewCallOnBand: false, isPota: false, isNewDxccCategory: false, isStillNeededByActiveAward: true), false);

        Check("Already worked, still needed AND POTA -> allowed (either alone suffices)",
              AwardMatcher.ShouldRejectAlreadyWorked(isNewCallOnBand: false, isPota: true, isNewDxccCategory: false, isStillNeededByActiveAward: true), false);
    }

    // ── RuleEngine.ResolveBandsForEvaluation: a band override must never let an award
    // evaluate "as" a band outside its own Bands= restriction ──
    static void RuleEngineResolveBandsForEvaluationTests()
    {
        Console.WriteLine("\n── RuleEngine.ResolveBandsForEvaluation ──");

        var unrestricted = new List<string>();
        var sixMOnly = new List<string> { "6m" };
        var multiBand = new List<string> { "6m", "10m" };

        Check("No override -> unrestricted award's own (empty) Bands list is returned unchanged",
              RuleEngine.ResolveBandsForEvaluation(unrestricted, null).Count == 0, true);
        Check("No override -> restricted award's own Bands list is returned unchanged",
              RuleEngine.ResolveBandsForEvaluation(sixMOnly, null).SequenceEqual(sixMOnly), true);

        var overrideResult = RuleEngine.ResolveBandsForEvaluation(unrestricted, "20m");
        Check("Override on an unrestricted award: narrows to just that band (legitimate 'browse one band' use)",
              overrideResult.Count == 1 && overrideResult[0] == "20m", true);

        var validOverride = RuleEngine.ResolveBandsForEvaluation(sixMOnly, "6m");
        Check("Override matching a restricted award's own band: honored",
              validOverride.Count == 1 && validOverride[0] == "6m", true);

        var invalidOverride = RuleEngine.ResolveBandsForEvaluation(sixMOnly, "20m");
        Check("Override NOT matching a restricted award's own band: ignored, falls back to the award's own Bands (the fix)",
              invalidOverride.SequenceEqual(sixMOnly), true);

        var multiValidOverride = RuleEngine.ResolveBandsForEvaluation(multiBand, "10m");
        Check("Multi-band award: override matching one of its own bands narrows to just that one",
              multiValidOverride.Count == 1 && multiValidOverride[0] == "10m", true);

        var multiInvalidOverride = RuleEngine.ResolveBandsForEvaluation(multiBand, "20m");
        Check("Multi-band award: override matching none of its own bands falls back to the full list",
              multiInvalidOverride.SequenceEqual(multiBand), true);

        var caseInsensitiveOverride = RuleEngine.ResolveBandsForEvaluation(sixMOnly, "6M");
        Check("Band matching is case-insensitive (override honored despite case difference)",
              caseInsensitiveOverride.Count == 1 && caseInsensitiveOverride[0].Equals("6M", StringComparison.OrdinalIgnoreCase), true);
    }

    // ── RuleEngine.BandChoicesFor: Still Need tab's Band dropdown contents per award ──
    static void RuleEngineBandChoicesForTests()
    {
        Console.WriteLine("\n── RuleEngine.BandChoicesFor ──");

        string[] allBands = { "(All Bands)", "160m", "80m", "40m", "20m", "6m" };

        var unrestrictedChoices = RuleEngine.BandChoicesFor(new List<string>(), allBands);
        Check("Unrestricted award: offers the full universal band list",
              unrestrictedChoices.SequenceEqual(allBands), true);

        var sixMChoices = RuleEngine.BandChoicesFor(new List<string> { "6m" }, allBands);
        Check("Single-band-restricted award: offers only '(All Bands)' + its own band, not the universal list",
              sixMChoices.SequenceEqual(new[] { "(All Bands)", "6m" }), true);

        var multiChoices = RuleEngine.BandChoicesFor(new List<string> { "6m", "10m" }, allBands);
        Check("Multi-band-restricted award: offers only '(All Bands)' + its own specific bands",
              multiChoices.SequenceEqual(new[] { "(All Bands)", "6m", "10m" }), true);
    }

    // ── RuleEngine.EvaluateBand: confirms the intersect fix actually changes evaluation
    // end to end, not just the pure ResolveBandsForEvaluation helper in isolation ──
    static void RuleEngineBandOverrideIntersectEndToEndTests()
    {
        Console.WriteLine("\n── RuleEngine.EvaluateBand: band-override intersect (end to end) ──");
        string tmpDb = Path.Combine(Path.GetTempPath(),
            "JimmyTest_RuleEngineBandOverride_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = new LogbookDb(tmpDb))
            {
                // TX worked+confirmed on 6m -- a 6m-only award should show it worked.
                InsertQso(db, "W5TX", "TX", dxcc: 291, zone: 5, band: "6m", lotwRcvd: "Y");
                // CA worked+confirmed on 20m only -- irrelevant to a 6m-only award.
                InsertQso(db, "W6CA", "CA", dxcc: 291, zone: 3, band: "20m", lotwRcvd: "Y");

                var was6m = new RuleDefinition
                {
                    Id = "TEST_WAS_6M", Name = "Test WAS 6m", FormatVersion = 1, Enabled = true,
                    GroupBy = RuleGroupBy.State, Universe = "US_50_STATES",
                    Bands = new List<string> { "6m" },
                    Target = RuleTargetType.All, Confirmation = RuleConfirmation.Any,
                };

                // Picking "20m" for this 6m-only award must NOT evaluate as-if Bands were 20m --
                // it must fall back to the award's own 6m restriction. (The bug: this used to
                // silently show real 20m data mislabeled as this 6m-only award's result.)
                var result = RuleEngine.EvaluateBand(was6m, "20m", tmpDb, null);
                Check("Invalid band override for a 6m-only award: TX (worked on 6m) still shows worked",
                      result.WorkedItems != null && result.WorkedItems.Contains("TX"), true);
                Check("Invalid band override for a 6m-only award: CA (only worked on 20m) must NOT show worked",
                      result.WorkedItems == null || !result.WorkedItems.Contains("CA"), true);

                // Picking "6m" (the award's own band) is a legitimate, honored override --
                // identical result to no override at all.
                var validResult = RuleEngine.EvaluateBand(was6m, "6m", tmpDb, null);
                Check("Valid band override (matches the award's own band): still shows TX worked",
                      validResult.WorkedItems != null && validResult.WorkedItems.Contains("TX"), true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  RuleEngineBandOverrideIntersectEndToEndTests threw: {ex.GetType().Name}: {ex.Message}");
            failed++;
        }
        finally
        {
            try { File.Delete(tmpDb); } catch { }
        }
    }

    // ── RowFormatter.BuildOrderedRow: shared row-building logic behind both the
    // Stations Available row and the Raw Decodes row ──────────────────────────────
    static void RowFormatterBuildOrderedRowTests()
    {
        Console.WriteLine("\n── RowFormatter.BuildOrderedRow ──");

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "callsign", ", GB13COL" }, { "side", ", TX1" }, { "tag", ", WAS Needed" }, { "empty", "" },
        };

        Check("Null order -> fallback returned unchanged",
              RowFormatter.BuildOrderedRow(fields, null, "FALLBACK") == "FALLBACK", true);

        CheckStr("Single field: leading ', ' is stripped when it's first in the row",
                 RowFormatter.BuildOrderedRow(fields, new List<string> { "callsign" }, "FALLBACK"), "GB13COL");

        CheckStr("Two fields: separator inserted between them, in the given order",
                 RowFormatter.BuildOrderedRow(fields, new List<string> { "callsign", "side" }, "FALLBACK"), "GB13COL, TX1");

        CheckStr("Order can be reversed freely -- side first, then callsign",
                 RowFormatter.BuildOrderedRow(fields, new List<string> { "side", "callsign" }, "FALLBACK"), "TX1, GB13COL");

        CheckStr("Unknown field names are skipped, not inserted as blank/garbage",
                 RowFormatter.BuildOrderedRow(fields, new List<string> { "callsign", "doesnotexist", "side" }, "FALLBACK"), "GB13COL, TX1");

        CheckStr("Duplicate field names: only the first occurrence is used",
                 RowFormatter.BuildOrderedRow(fields, new List<string> { "callsign", "callsign", "side" }, "FALLBACK"), "GB13COL, TX1");

        CheckStr("Empty-string field values are included but contribute nothing",
                 RowFormatter.BuildOrderedRow(fields, new List<string> { "empty", "callsign" }, "FALLBACK"), "GB13COL");

        Check("Order given but every field empty/unmatched -> fallback returned",
              RowFormatter.BuildOrderedRow(fields, new List<string> { "empty", "doesnotexist" }, "FALLBACK") == "FALLBACK", true);

        Check("Null fieldMap with a non-null order -> fallback, does not throw",
              RowFormatter.BuildOrderedRow(null, new List<string> { "callsign" }, "FALLBACK") == "FALLBACK", true);
    }

    // ── Controller.ParseRowOrder: INI parsing for both row-order settings ─────────
    static void ParseRowOrderTests()
    {
        Console.WriteLine("\n── Controller.ParseRowOrder ──");

        var allowed = new[] { "callsign", "side", "tag", "message" };

        Check("Null/empty INI value -> null (falls back to compiled-in default)",
              Controller.ParseRowOrder(null, allowed) == null, true);
        Check("Whitespace-only INI value -> null",
              Controller.ParseRowOrder("   ", allowed) == null, true);

        var parsed = Controller.ParseRowOrder("callsign,side,message", allowed);
        Check("Valid comma list parses in order",
              parsed != null && parsed.SequenceEqual(new[] { "callsign", "side", "message" }), true);

        var withInvalid = Controller.ParseRowOrder("callsign,bogus,side", allowed);
        Check("Unknown field name in the INI value is dropped, valid ones kept in order",
              withInvalid != null && withInvalid.SequenceEqual(new[] { "callsign", "side" }), true);

        var withDupes = Controller.ParseRowOrder("callsign,side,callsign", allowed);
        Check("Duplicate field name in the INI value: only first occurrence kept",
              withDupes != null && withDupes.SequenceEqual(new[] { "callsign", "side" }), true);

        Check("Only invalid/unknown names -> null, not an empty list",
              Controller.ParseRowOrder("bogus1,bogus2", allowed) == null, true);

        var trimmed = Controller.ParseRowOrder(" callsign , side ", allowed);
        Check("Whitespace around tokens is trimmed",
              trimmed != null && trimmed.SequenceEqual(new[] { "callsign", "side" }), true);

        // "freq" (station's audio Hz) is a selectable Row Order field for both the Stations
        // Available list and the Raw Decodes list, with a screen-reader label, but is NOT in
        // either default row (opt-in only).
        Check("'freq' is an allowed Stations Available row field",
              RowDisplayOrderDlg.CallWaitingDefaultFields.Contains("freq"), true);
        Check("'freq' is an allowed Raw Decodes row field",
              RowDisplayOrderDlg.RawDecodeDefaultFields.Contains("freq"), true);
        Check("'freq' has a Stations Available label",
              RowDisplayOrderDlg.CallWaitingFieldLabels.ContainsKey("freq"), true);
        Check("'freq' has a Raw Decodes label",
              RowDisplayOrderDlg.RawDecodeFieldLabels.ContainsKey("freq"), true);
        Check("ParseRowOrder accepts 'freq' against the real allowed set",
              (Controller.ParseRowOrder("callp,snr,freq", RowDisplayOrderDlg.CallWaitingDefaultFields) ?? new List<string>())
                  .Contains("freq"), true);
    }

    // ── HotkeyConfig: a newer action whose default key an upgrade's saved hotkeys already use
    //    is left unassigned, not silently double-bound (audit finding 3, 2026-08-27) ──
    static void HotkeyConfigNewActionConflictTests()
    {
        Console.WriteLine("\n── HotkeyConfig: new frequency-shortcut default that collides with a saved binding ──");

        string tmpIni = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "JimmyHotkeyConflictTest_" + Guid.NewGuid().ToString("N") + ".ini");
        try
        {
            // Simulate an operator upgrading from a build with no frequency shortcuts who had
            // remapped Quick Power / SWR Check onto Shift+F12 -- which is now TxFreqUp's default.
            int shiftF12 = (int)(System.Windows.Forms.Keys.Shift | System.Windows.Forms.Keys.F12);
            var seed = new IniFile(tmpIni);
            seed.Write("PowerSwr", shiftF12.ToString(), "Hotkeys");

            var cfg = new HotkeyConfig();
            cfg.LoadFromIni(new IniFile(tmpIni));

            Check("The operator's custom PowerSwr = Shift+F12 binding is kept",
                cfg[HotkeyAction.PowerSwr] == (System.Windows.Forms.Keys.Shift | System.Windows.Forms.Keys.F12), true);
            Check("TxFreqUp (whose default is now taken) is left unassigned, not double-bound",
                cfg[HotkeyAction.TxFreqUp] == System.Windows.Forms.Keys.None, true);
            Check("...and it's reported so Controller can tell the operator",
                cfg.UnassignedDueToConflict.Contains(HotkeyAction.TxFreqUp), true);
            Check("A frequency shortcut with no collision keeps its default",
                cfg[HotkeyAction.RxFreqDown] == (System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Shift | System.Windows.Forms.Keys.F11), true);

            // No conflicts at all -> nothing unassigned, empty report.
            var clean = new HotkeyConfig();
            clean.LoadFromIni(new IniFile(System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "JimmyHotkeyConflictTest_none_" + Guid.NewGuid().ToString("N") + ".ini")));
            Check("Fresh install (no saved hotkeys): no frequency shortcut is unassigned",
                clean.UnassignedDueToConflict.Count == 0, true);
            Check("Fresh install: TxFreqUp has its Shift+F12 default",
                clean[HotkeyAction.TxFreqUp] == (System.Windows.Forms.Keys.Shift | System.Windows.Forms.Keys.F12), true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  HotkeyConfigNewActionConflictTests threw: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            failed++;
        }
        finally
        {
            try { System.IO.File.Delete(tmpIni); } catch { }
        }
    }

    // ── RuleEngine: fixed single-band award restriction ([Match] Bands=) ────────
    // Mirrors the shape of the new WAS_*M per-band awards (GroupBy=State,
    // Universe=US_50_STATES, Target=All, Bands=<one band>). Confirms the award's
    // own Bands restriction is honored by a plain Evaluate() call (no band
    // override) -- the path the Awards tab and Still Need tab's static checklist
    // both use -- so a state worked only on a different band does not count.
    static void RuleEngineFixedBandRestrictionTests()
    {
        Console.WriteLine("\n── RuleEngine: fixed single-band award (Bands=) ──");
        string tmpDb = Path.Combine(Path.GetTempPath(),
            "JimmyTest_RuleEngineFixedBand_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = new LogbookDb(tmpDb))
            {
                // TX worked AND confirmed on 6m -- must count for a 6m-only WAS award.
                InsertQso(db, "W5TX", "TX", dxcc: 291, zone: 5, band: "6m", lotwRcvd: "Y");
                // CA worked only on 20m -- must NOT count for a 6m-only WAS award.
                InsertQso(db, "W6CA", "CA", dxcc: 291, zone: 3, band: "20m");

                var was6m = new RuleDefinition
                {
                    Id = "TEST_WAS_6M", Name = "Test WAS 6m", FormatVersion = 1, Enabled = true,
                    GroupBy = RuleGroupBy.State, Universe = "US_50_STATES",
                    Bands = new List<string> { "6m" },
                    Target = RuleTargetType.All, Confirmation = RuleConfirmation.Any,
                };

                var result = RuleEngine.Evaluate(was6m, tmpDb, null);
                Check("Fixed Bands=6m: state worked on 6m counts",
                      result.WorkedItems != null && result.WorkedItems.Contains("TX"), true);
                Check("Fixed Bands=6m: state worked only on 20m does NOT count",
                      result.WorkedItems == null || !result.WorkedItems.Contains("CA"), true);
                Check("Fixed Bands=6m: still-needed checklist includes CA (not confirmed on 6m)",
                      result.StillNeeded != null && result.StillNeeded.Contains("CA"), true);
                Check("Fixed Bands=6m: still-needed checklist does not include TX (confirmed on 6m)",
                      result.StillNeeded != null && !result.StillNeeded.Contains("TX"), true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  RuleEngineFixedBandRestrictionTests threw: {ex.GetType().Name}: {ex.Message}");
            failed++;
        }
        finally
        {
            try { File.Delete(tmpDb); } catch { }
        }
    }

    // CrashLogger.Log has no test-mode path override (unlike LogbookDb/JIMMY_TEST_DB_PATH) --
    // a crash log genuinely belongs at the real %LocalAppData%\Jimmy Next\log_crashes.txt
    // location regardless of build flavor, so this test exercises that real path directly
    // rather than an isolated copy. Records the file's length beforehand and only inspects
    // what Log() actually appended, so a real crash logged earlier in this same session
    // (or by a previous test run) can't make this test pass or fail for the wrong reason.
    static void CrashLoggerTests()
    {
        Console.WriteLine("\n── CrashLogger.Log: writes exception details to log_crashes.txt ──");
        // CrashLogger.Log's own internal Assembly.GetExecutingAssembly() resolves to
        // wherever CrashLogger's IL actually lives (Jimmy Next.dll) regardless of who calls
        // it -- using THIS test's own GetExecutingAssembly() here would resolve to
        // JimmyTests.dll instead and silently check the wrong folder.
        string logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            typeof(CrashLogger).Assembly.GetName().Name, "log_crashes.txt");
        long beforeLength = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;

        Exception thrown;
        try
        {
            try { throw new InvalidOperationException("CrashLoggerTests inner exception"); }
            catch (Exception inner) { throw new ApplicationException("CrashLoggerTests outer exception", inner); }
        }
        catch (Exception ex) { thrown = ex; }

        CrashLogger.Log("CrashLoggerTests", thrown);

        Check("log_crashes.txt exists after Log()", File.Exists(logPath), true);
        if (!File.Exists(logPath)) return;

        string appended;
        using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fs.Seek(beforeLength, SeekOrigin.Begin);
            using (var reader = new StreamReader(fs)) appended = reader.ReadToEnd();
        }

        Check("Logged text names the source passed to Log()", appended.Contains("UNHANDLED EXCEPTION (CrashLoggerTests)"), true);
        Check("Logged text includes the outer exception's type", appended.Contains("ApplicationException"), true);
        Check("Logged text includes the outer exception's message", appended.Contains("CrashLoggerTests outer exception"), true);
        Check("Logged text marks the inner exception", appended.Contains("[Inner exception 1]"), true);
        Check("Logged text includes the inner exception's type", appended.Contains("InvalidOperationException"), true);
        Check("Logged text includes the inner exception's message", appended.Contains("CrashLoggerTests inner exception"), true);

        // Log(null) for the exception must be a silent no-op, not a NullReferenceException --
        // a defensive-programming guarantee for a handler that itself runs inside another
        // unhandled-exception handler, where throwing again would be far worse than doing
        // nothing.
        bool threwOnNull = false;
        try { CrashLogger.Log("null-exception guard", null); } catch { threwOnNull = true; }
        Check("Log(null) does not throw", threwOnNull, false);
    }

    // Walks up from the test binary's directory looking for Jimmy.sln, then
    // resolves relativePath from there. Returns null if not found (keeps this
    // test a soft SKIP rather than a hard failure if run from an unusual layout).
    static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            string candidateSln = Path.Combine(dir.FullName, "Jimmy.sln");
            if (File.Exists(candidateSln))
            {
                string full = Path.Combine(dir.FullName, relativePath);
                return File.Exists(full) ? full : null;
            }
        }
        return null;
    }
}
