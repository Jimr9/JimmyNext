using System.Collections.Generic;
using System.Linq;

namespace WSJTX_Controller
{
    // What a QSO gets grouped by when checking off units of an award (e.g. "one
    // entry per DXCC entity"). None means the award is a simple QSO count with no
    // grouping at all.
    public enum RuleGroupBy
    {
        None, Dxcc, Country, State, CqZone, ItuZone, Continent, County,
        Grid, Grid4, Iota, Prefix, Callsign, SigInfo, DarcDok
    }

    // Which QSL source(s) count as "confirmed". None means the award doesn't
    // require confirmation at all -- completion is judged on worked QSOs.
    // Any = LoTW or QRZ (unchanged since it shipped). Sources = the explicit set in
    // RuleDefinition.ConfirmationSources, any one of which confirms (D1, 2026-09-28: lets an
    // award accept eQSL or a paper card, or e.g. "LoTW or card" -- the existing five keep
    // exactly their meaning, so no shipped or saved definition's totals move).
    public enum RuleConfirmation { Any, Lotw, Qrz, Both, None, Sources }

    // The confirmation channels a Sources definition can name, each kept separate. Card = a paper
    // QSL card. EqslAg = an eQSL confirmation received from a sender eQSL marks Authenticity
    // Guaranteed (2026-10-06): BOTH conditions -- AG alone is not a confirmation, and a plain
    // EQSL confirmation is not an AG one.
    public static class RuleConfirmationSources
    {
        public const string Lotw = "LOTW", Qrz = "QRZ", Eqsl = "EQSL", EqslAg = "EQSL_AG", Card = "CARD";
        public static readonly string[] All = { Lotw, Qrz, Eqsl, EqslAg, Card };

        public static string Label(string s) =>
            s == Lotw ? "LoTW" : s == Qrz ? "QRZ" : s == Eqsl ? "eQSL" : s == EqslAg ? "eQSL (AG)" : s == Card ? "paper card" : s;

        // The channels a definition accepts, whatever form its file used -- "Any" is LoTW or QRZ.
        public static List<string> Accepted(RuleDefinition d)
        {
            switch (d.Confirmation)
            {
                case RuleConfirmation.Sources: return d.ConfirmationSources;
                case RuleConfirmation.Lotw: return new List<string> { Lotw };
                case RuleConfirmation.Qrz: return new List<string> { Qrz };
                case RuleConfirmation.Any: return new List<string> { Lotw, Qrz };
                default: return new List<string>();   // None; Both is described on its own
            }
        }

        // "LoTW or paper card" -- for display in the manager, the editor and the Awards page.
        public static string Describe(RuleDefinition d)
        {
            if (d.Confirmation == RuleConfirmation.None) return "none (contacts only)";
            if (d.Confirmation == RuleConfirmation.Both) return "LoTW and QRZ";
            return string.Join(" or ", Accepted(d).Select(Label));
        }
    }

    public enum RuleTargetType { All, Count, Levels }

    // What "progress" counts against the target -- Worked (the default; personal goals) or
    // Confirmed (sponsor awards: a contact counts once an ACCEPTED source confirms it). Since
    // 2026-10-06 a checklist (Target=All) can be Confirmed too: complete only when every item is
    // confirmed by an accepted source. StillNeeded (what is left to WORK, for hunting and live
    // tags) stays worked-based either way; worked items awaiting an accepted confirmation are
    // listed apart (RuleResult.AwaitingConfirmation).
    public enum RuleBasis { Worked, Confirmed }

    public class RuleLevel
    {
        public string Name;
        public int    Threshold;
    }

    public class RuleEndorsements
    {
        public List<string> Bands = new List<string>();
        public List<string> Modes = new List<string>();
    }

    public class RuleDefinition
    {
        public string Id;
        public string Name;
        public string Sponsor;
        public string Category;
        public int    FormatVersion;
        public bool   Enabled = true;
        public string Description;
        public string Website;

        public RuleGroupBy  GroupBy;
        public string       Universe;   // e.g. "US_50_STATES", "File:na_dxcc.txt"; raw as written
        public string       LimitTo;    // optional: restrict counted values to this universe (e.g. "only NA entities")
        public List<string> Bands = new List<string>();
        public List<string> Modes = new List<string>();
        public string        CallsignPattern;
        public string        Sig;        // optional exact filter on the SIG column (for GroupBy=SigInfo)
        public string        DateFrom;   // yyyy-MM-dd, or "yyyy-MM-dd HHmm" (UTC) for an event window
        public string        DateTo;     // inclusive; a time is the last minute counted

        // 2026-10-06 award update: what a sponsor's rules restrict, each optional.
        public List<string> ExcludeBands = new List<string>();      // e.g. 60m (ARRL)
        public List<string> DxccIn = new List<string>();            // only these entities (e.g. 291,6,110 for US states)
        public List<string> ExcludeCallsigns = new List<string>();  // wildcard patterns, e.g. VE0*, */MM
        public bool         DcCountsAsMaryland;                     // ARRL WAS, QRZ US Award: "DC counts as MD"

        public RuleConfirmation Confirmation = RuleConfirmation.Any;
        // Confirmation = Sources only: which channels confirm (RuleConfirmationSources values).
        public List<string> ConfirmationSources = new List<string>();

        public RuleTargetType  Target;
        public RuleBasis        Basis = RuleBasis.Worked;  // Target=Count/Levels only; see RuleBasis
        public int              Threshold;              // Target=Count; ignored when ThresholdFrom is set
        public List<RuleLevel>  Levels = new List<RuleLevel>();  // Target=Levels, ascending by Threshold

        // Optional Target=Count dynamic threshold: instead of a fixed Threshold=N written in
        // the file, resolve a Universe (e.g. "DXCC_CURRENT") at evaluation time and use
        // (that universe's count - ThresholdOffset) instead -- e.g. ARRL Honor Roll's
        // threshold moves as DXCC entities come in/out of existence, expressed here as
        // "current active entity count minus 9" rather than a number that goes stale.
        public string ThresholdFrom;      // e.g. "DXCC_CURRENT"; null/empty = use the fixed Threshold
        public int    ThresholdOffset;    // subtracted from the resolved universe's count

        public RuleEndorsements Endorsements;    // null if the file has no [Endorsements] section

        // What a user should know that Jimmy does not check (operating location, sponsor credit,
        // unverified rules...). Shown on the Awards page. Optional.
        public string ManualChecks;

        // The location an award counts: a LoTW-confirmed location when LoTW is one of the accepted
        // sources (the location LoTW confirmed is the evidence ARRL uses), otherwise the logged one
        // -- a QRZ award never silently takes LoTW's location (2026-10-06).
        public bool UsesLotwLocation => RuleConfirmationSources.Accepted(this).Contains(RuleConfirmationSources.Lotw)
                                        || Confirmation == RuleConfirmation.Both;

        // Set by the loader; not read from the file.
        public string SourceFile;
    }
}
