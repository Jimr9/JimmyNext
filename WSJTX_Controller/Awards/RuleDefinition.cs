using System.Collections.Generic;

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
    public enum RuleConfirmation { Any, Lotw, Qrz, Both, None }

    public enum RuleTargetType { All, Count, Levels }

    // What "progress" counts against Target/Threshold -- Worked (the default, and the only
    // option for Target=All; see RuleEngine.FinishGrouped's own comment on why Target=All's
    // StillNeeded/Completed are always Worked-based, confirmation tracked separately) or
    // Confirmed (opt-in, Count/Levels only -- e.g. ARRL Honor Roll, which by its real-world
    // definition counts CONFIRMED entities, not merely worked ones). Defaulting every existing
    // award to Worked means this is purely additive: nothing already shipped changes behavior.
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
        public string        DateFrom;   // yyyy-MM-dd
        public string        DateTo;

        public RuleConfirmation Confirmation = RuleConfirmation.Any;

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

        // Set by the loader; not read from the file.
        public string SourceFile;
    }
}
