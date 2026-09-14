using System;

namespace WSJTX_Controller
{
    // Single source of truth for "is a test harness driving this Jimmy instance right now."
    // Reuses the exact same JIMMY_TEST_DB_PATH signal LogbookDb already uses to isolate the
    // local database (see LogbookDb.DbPath) -- one environment variable now protects both.
    //
    // Added 2026-07-12 after a real incident: JIMMY_TEST_DB_PATH alone only isolates which
    // *database file* gets written to. It does nothing to stop the real QRZ/Club Log/LoTW
    // credentials -- which still come from the user's real Jimmy.ini regardless of which
    // database is active -- from being used to make genuine HTTP calls. A full session of
    // replay testing with real-time upload enabled in the real settings genuinely uploaded
    // ~100 fake QSOs to the user's live QRZ Logbook and Club Log accounts. Every method in
    // this codebase that makes an outbound network call to a third-party ham radio service
    // (QRZ, Club Log, LoTW, FCC ULS) must check this guard first and no-op instead.
    internal static class TestModeGuard
    {
        // Stage 14 (2026-09-14): OR'd with the JimmyTests entry-assembly check
        // (WsjtxClient.Direct.cs's own long-standing _isJimmyTestsHost pattern, consolidated
        // here) so a JimmyTests unit test that forgets to set JIMMY_TEST_DB_PATH is STILL
        // recognised as test mode -- broadening only ever makes MORE callers test-safe by
        // default, never fewer. The real "Jimmy Next.exe" binary (production, and the replay
        // harness driving a real build) has entry assembly "Jimmy Next", never "JimmyTests",
        // so replay's own reliance on the env var alone is unaffected.
        public static bool IsTestMode =>
            TestForceIsTestMode ?? (
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JIMMY_TEST_DB_PATH"))
                || string.Equals(System.Reflection.Assembly.GetEntryAssembly()?.GetName()?.Name,
                                  "JimmyTests", StringComparison.Ordinal));

        // Test-only (JimmyTests, InternalsVisibleTo): forces IsTestMode's result for one test
        // that needs to simulate real "live" (non-test) behavior from inside the JimmyTests
        // process itself -- where the entry-assembly check above would otherwise always make
        // IsTestMode true. Proving Stage 14's no-fallback contract (SemanticExtensions.
        // EffectiveSemantic) actually rejects a live decode needs exactly this. Null (default)
        // = normal detection; a test sets it, then MUST restore it to null in a finally block.
        internal static bool? TestForceIsTestMode = null;
    }
}
