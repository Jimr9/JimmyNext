using System.Collections.Generic;

namespace WSJTX_Controller
{
    // Nexus contesting foundation, phase 6: Jimmy's OWN policy about what it can actually do for
    // a given contest -- entirely independent of what Nexus's own contest list reports. Nexus
    // provides rules/validation/scoring/export for many contests; that alone never implies
    // Jimmy has built automated exchange sequencing for any of them. This table is the single
    // place that answers "what can the operator expect", and every entry is a value this
    // codebase can point to a real, tested capability for -- never inferred from Nexus's list.
    public enum ContestSupportLevel
    {
        // Jimmy composes/sends/logs the exchange automatically for this contest.
        AutomatedDigital,
        // Jimmy's manual entry workflow, Nexus's validation/dupe/scoring/export, and the full
        // correction-via-rebuild path have all been tested for this specific contest.
        VerifiedManual,
        // Nexus reports a ruleset for this event_id; Jimmy has not yet verified the manual
        // workflow against it. This is the default for every event_id Nexus's own list returns
        // that isn't already one of the two states above -- never silently promoted.
        AvailableNotVerified,
        // No Nexus ruleset exists for this event_id at all.
        NotSupportedByNexus,
    }

    public static class ContestSupportLevels
    {
        // The only two entries earned so far. AutomatedDigital: ARRL Field Day's FT8/FT4 slice
        // (phase 7). Nothing else is promoted by default -- adding a real entry here always
        // requires the promotion checklist (entry fields, validation, dupe, scoring, correction-
        // via-rebuild, export all tested for that specific contest) to have actually run.
        private static readonly Dictionary<string, ContestSupportLevel> Overrides =
            new Dictionary<string, ContestSupportLevel>(System.StringComparer.OrdinalIgnoreCase)
        {
            { "arrlfd", ContestSupportLevel.AutomatedDigital },
        };

        // eventIdKnownToNexus: true when this event_id appeared in Nexus's own CONTEST_LIST_EVENTS
        // response (or CONTEST_GET_RULESET resolved it) -- callers must pass real data, never
        // assume true.
        public static ContestSupportLevel Get(string eventId, bool eventIdKnownToNexus)
        {
            if (!string.IsNullOrEmpty(eventId) && Overrides.TryGetValue(eventId, out var level))
                return level;
            return eventIdKnownToNexus ? ContestSupportLevel.AvailableNotVerified : ContestSupportLevel.NotSupportedByNexus;
        }

        // Single, short, screen-reader-friendly label -- the ONE accessible surface a support
        // level is ever announced through (see ContestingWindow's own accessibility comment).
        public static string Label(ContestSupportLevel level)
        {
            switch (level)
            {
                case ContestSupportLevel.AutomatedDigital: return "Automated digital operation";
                case ContestSupportLevel.VerifiedManual: return "Verified manual contest logging";
                case ContestSupportLevel.AvailableNotVerified: return "Available in Nexus, not yet verified in Jimmy";
                default: return "Not supported by Nexus";
            }
        }
    }
}
