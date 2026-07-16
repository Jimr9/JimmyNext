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

    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--verify-clublog")
        {
            VerifyClubLogEquivalence();
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
        LogbookDbUploadSyncStatusTests();
        QrzIsDuplicateReasonTests();
        ResolveUsStateTests();
        AdifImporterLiveLoggedStateFallbackTests();
        DxSpotWatcherIsEvenPeriodTests();
        FccUlsProviderParseLineTests();
        FccUlsProviderLooksIncompleteTests();
        ClassificationEngineTests();
        GeoMathTests();
        CapabilityNegotiatorTests();
        A6ClassificationParityTests();
        ClubLogPrefixTableTests();

        Console.WriteLine();
        Console.WriteLine($"=== {passed} passed, {failed} failed ===");
        if (failed > 0)
        {
            Console.WriteLine("SOME TESTS FAILED");
            Environment.Exit(1);
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
    // Verify the three new CallCategory values have the expected integer assignments.
    // If any of these fail, DeriveCategory / AddSelectedCall routing is broken.
    static void HrcEnumTests()
    {
        Console.WriteLine("\n── HRC CallCategory Enum Values ──");
        // Existing values must be unchanged — regression guard
        Check("DEFAULT == 0",             (int)WsjtxClient.CallCategory.DEFAULT             == 0,  true);
        Check("ALWAYS_WANTED == 8",       (int)WsjtxClient.CallCategory.ALWAYS_WANTED       == 8,  true);
        // New HRC values
        Check("WAS_NEEDED == 9",          (int)WsjtxClient.CallCategory.WAS_NEEDED          == 9,  true);
        Check("DXCC_UNCONFIRMED == 10",   (int)WsjtxClient.CallCategory.DXCC_UNCONFIRMED    == 10, true);
        Check("ZONE_NEEDED == 11",        (int)WsjtxClient.CallCategory.ZONE_NEEDED         == 11, true);
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
                // TX confirmed via LoTW → TX must NOT be in neededStates
                InsertQso(db, "W5TX",   "TX", dxcc: 100, zone: 4, lotwRcvd: "Y");
                // CA worked but unconfirmed → CA MUST be in neededStates
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
                HashSet<int>    unconfirmedDxcc;
                HashSet<int>    neededZones;
                db.LoadHrcCache(out neededStates, out unconfirmedDxcc, out neededZones);

                // ── States ──────────────────────────────────────────────────
                Check("neededStates: TX confirmed → NOT in set",   neededStates.Contains("TX"), false);
                Check("neededStates: CA unconfirmed → in set",     neededStates.Contains("CA"), true);
                Check("neededStates: WY (no QSO) → in set",        neededStates.Contains("WY"), true);
                Check("neededStates: count ≤ 50",                  neededStates.Count <= 50,    true);
                // Only TX was confirmed, so 49 states should be needed
                Check("neededStates: count == 49",                 neededStates.Count == 49,    true);
                // DC must never appear — it is not a state
                Check("neededStates: DC never present",            neededStates.Contains("DC"), false);

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
        // meridian by Maidenhead convention -- center of that 2x1 degree square.
        var jj00 = GeoMath.GridToLatLon("JJ00");
        Check("JJ00 parses (non-null)", jj00.HasValue, true);
        if (jj00.HasValue)
        {
            Check("JJ00 lat within 0.6 of equator", Math.Abs(jj00.Value.lat - 0.5) < 0.001, true);
            Check("JJ00 lon within 0.6 of prime meridian", Math.Abs(jj00.Value.lon - 1.0) < 0.001, true);
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

        // Pure formula checks (direct lat/lon, independent of grid parsing):
        // one degree of latitude north, same longitude -- due north, ~111.2 km.
        double distNorth = GeoMath.DistanceKm(0.5, 1.0, 1.5, 1.0);
        Check("1 degree latitude ~= 111.2 km", Math.Abs(distNorth - 111.19) < 0.5, true);
        double azNorth = GeoMath.AzimuthDegrees(0.5, 1.0, 1.5, 1.0);
        Check("due north bearing == 0 degrees", Math.Abs(azNorth - 0.0) < 0.01, true);

        // Quarter of Earth's circumference along the equator (independently
        // derivable: R * (pi/2)), due east.
        double distQuarter = GeoMath.DistanceKm(0, 0, 0, 90);
        Check("quarter circumference along equator ~= R*(pi/2)",
              Math.Abs(distQuarter - (6371.0 * Math.PI / 2)) < 0.5, true);
        double azEast = GeoMath.AzimuthDegrees(0, 0, 0, 90);
        Check("due east bearing == 90 degrees", Math.Abs(azEast - 90.0) < 0.01, true);

        // Same point: zero distance.
        Check("same point: distance == 0", Math.Abs(GeoMath.DistanceKm(10, 20, 10, 20)) < 0.0001, true);

        // End-to-end grid-pair convenience overload composes the same way.
        var pair = GeoMath.DistanceAndAzimuth("JJ00", "JJ01");
        Check("DistanceAndAzimuth(JJ00, JJ01): non-null", pair.HasValue, true);
        if (pair.HasValue)
        {
            Check("DistanceAndAzimuth(JJ00, JJ01): ~111.2 km", Math.Abs(pair.Value.distanceKm - 111.19) < 0.5, true);
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

    // ── CapabilityNegotiator (Stage A5) ─────────────────────────────────────────
    // Pure state-transition tests for the negotiation state machine that replaces
    // acceptableWsjtxVersions' hard version-string gate. No socket/UI involved --
    // WsjtxClient's actual cmd:7 send/compare mechanics are exercised end-to-end by
    // the replay suite instead (this is purely the state bookkeeping around them).
    static void CapabilityNegotiatorTests()
    {
        Console.WriteLine("\n── CapabilityNegotiator (Stage A5) ──");

        var n = new CapabilityNegotiator();
        Check("starts Disconnected", n.State == WsjtxCapabilityState.Disconnected, true);

        n.BeginNegotiating();
        Check("BeginNegotiating -> Negotiating", n.State == WsjtxCapabilityState.Negotiating, true);

        n.BeginCapabilityProbe();
        Check("BeginCapabilityProbe -> CapabilityProbing", n.State == WsjtxCapabilityState.CapabilityProbing, true);

        // Happy path: Compatibility Layer confirmed (currently supported Andy WM8Q build).
        var full = new CapabilityNegotiator();
        full.BeginNegotiating();
        full.BeginCapabilityProbe();
        full.ConfirmCompatibilityLayer();
        Check("ConfirmCompatibilityLayer -> ConnectedFull", full.State == WsjtxCapabilityState.ConnectedFull, true);
        full.TimeoutToDegraded();
        Check("TimeoutToDegraded after ConnectedFull is a no-op (late timer fire doesn't downgrade)",
              full.State == WsjtxCapabilityState.ConnectedFull, true);

        // Degraded path: no echo within the bounded timeout (stock / WSJT-X Improved
        // build with no Compatibility Layer) -- must NOT stay stuck refusing to run.
        var degraded = new CapabilityNegotiator();
        degraded.BeginNegotiating();
        degraded.BeginCapabilityProbe();
        degraded.TimeoutToDegraded();
        Check("TimeoutToDegraded (no echo) -> Connected (degraded)", degraded.State == WsjtxCapabilityState.Connected, true);

        // Reconnect: capability state must never be cached across a connection boundary.
        degraded.Reset();
        Check("Reset -> Disconnected (re-probes from scratch on reconnect)",
              degraded.State == WsjtxCapabilityState.Disconnected, true);
        full.Reset();
        Check("Reset from ConnectedFull -> Disconnected too", full.State == WsjtxCapabilityState.Disconnected, true);
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
    // 1) still parse correctly, and that dedup keeps the highest-uid row per
    // callsign (the current holder) rather than an old/reissued one.
    static void FccUlsProviderParseLineTests()
    {
        Console.WriteLine("\n── FccUlsProvider.ParseLine ──");

        // W1AW = ARRL HQ, Newington, CT -- a stable, well-known real-world answer.
        const string w1aw = "EN|780866|||W1AW|L|L00306106|ARRL HQ OPERATORS CLUB||||||||225 MAIN ST|NEWINGTON|CT|06111|| David A Minster|000|0004511143|B||||||";
        var d1 = new Dictionary<string, (long Uid, string State)>(StringComparer.OrdinalIgnoreCase);
        FccUlsProvider.ParseLine(w1aw, d1);
        Check("W1AW parses to CT", d1.TryGetValue("W1AW", out var w1awEntry) && w1awEntry.State == "CT", true);

        // AA0A: two real rows for the same callsign -- an older license (McCarthy,
        // MO, lower uid) and the current one (Rosebrook, SD, higher uid). Confirmed
        // duplicate pair copied verbatim from a real downloaded file.
        const string aa0aOld = "EN|215000|||AA0A|L|L00209566|MC CARTHY, DENNIS J|DENNIS|J|MC CARTHY|||||6438 Bishops Pl|SAINT LOUIS|MO|631093371|||000|0002274249|I||||||";
        const string aa0aNew = "EN|4280373|||AA0A|L|L02306961|Rosebrook, John|John||Rosebrook|||||3916 N. Potsdam Ave. #4555|Sioux Falls|SD|57104|||000|0028942159|I||||||";

        // Order 1: old row first, then new -- higher uid must win.
        var d2 = new Dictionary<string, (long Uid, string State)>(StringComparer.OrdinalIgnoreCase);
        FccUlsProvider.ParseLine(aa0aOld, d2);
        FccUlsProvider.ParseLine(aa0aNew, d2);
        Check("AA0A (old-then-new order): higher uid (SD) wins",
              d2.TryGetValue("AA0A", out var aa0aEntry1) && aa0aEntry1.State == "SD", true);

        // Order 2: new row first, then old -- must NOT regress back to the old one.
        var d3 = new Dictionary<string, (long Uid, string State)>(StringComparer.OrdinalIgnoreCase);
        FccUlsProvider.ParseLine(aa0aNew, d3);
        FccUlsProvider.ParseLine(aa0aOld, d3);
        Check("AA0A (new-then-old order): higher uid (SD) still wins",
              d3.TryGetValue("AA0A", out var aa0aEntry2) && aa0aEntry2.State == "SD", true);

        // Malformed/irrelevant input must be skipped, not throw or add junk.
        var d4 = new Dictionary<string, (long Uid, string State)>(StringComparer.OrdinalIgnoreCase);
        FccUlsProvider.ParseLine("HD|780866|||W1AW|A|||||", d4);
        Check("non-EN record type is skipped", d4.Count == 0, true);
        FccUlsProvider.ParseLine("EN|123|||W9ZZZ|L|", d4);
        Check("too-few-fields line is skipped", d4.Count == 0, true);
        FccUlsProvider.ParseLine("", d4);
        Check("empty line is skipped, does not throw", d4.Count == 0, true);
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
        string band = "20m", string qsoDate = "20241201", string continent = "")
    {
        string key = AdifImporter.BuildDedupKey(call, band, "FT8", qsoDate, "1200");
        // Parameter order: ..., lotwQslSent, lotwQslRcvd, qrzQslSent, qrzQslRcvd, ...
        db.Upsert(call, band, "FT8", qsoDate, "1200", "1215",
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
                db.LoadHrcCache(out neededNoBand, out _, out _, band: null);
                db.LoadHrcCache(out neededWithBand, out _, out _, band: "10m");

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
