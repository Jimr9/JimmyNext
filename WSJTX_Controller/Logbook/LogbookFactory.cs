using System;

namespace WSJTX_Controller
{
    // The one place that decides which logbook Jimmy talks to (logbook migration Phase 5): Jimmy's
    // own LogbookDb, or -- once a migration has made Nexus the owner (NexusLogbook.Active) -- the
    // Nexus-backed service. Callers that pass an explicit path (tests, tools) always get that
    // file as a LogbookDb.
    public static class LogbookFactory
    {
        public static ILogbookService Open() =>
            NexusLogbook.Active ? (ILogbookService)new NexusLogbookService() : new LogbookDb();

        public static ILogbookService Open(string path) =>
            NexusLogbook.Active && (path == null || path == LogbookDb.DbPath)
                ? (ILogbookService)new NexusLogbookService()
                : new LogbookDb(path);
    }
}
