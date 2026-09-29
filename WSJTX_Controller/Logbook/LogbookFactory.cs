namespace WSJTX_Controller
{
    // The one place that opens the logbook. Nexus keeps the log (Phase 7, 2026-09-29): every
    // caller gets the Nexus-backed service -- writes go through Nexus, reads come from the read
    // copy (LogbookDb.DbPath). Code that only reads a specific database opens a LogbookDb on it.
    public static class LogbookFactory
    {
        public static ILogbookService Open() => new NexusLogbookService();
    }
}
