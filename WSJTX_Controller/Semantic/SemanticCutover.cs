namespace WSJTX_Controller
{
    // Nexus modernization Stage 5+ (2026-09-08): the emergency rollback valve for the
    // FT8/FT4 semantic-fact migration, mirroring Classification/ClassificationCutover.
    //
    // INI-only (undocumented-to-users "useNexusSemantics" key, read once at startup in
    // Controller.cs; never exposed in OptionsDlg). DEFAULT FALSE for now: Stage 5 only
    // COMPUTES the Nexus-derived SemanticDecode beside the WsjtxMessage one and shadow-logs
    // disagreements -- no consumer reads it. Stage 6+ flips this true (per-family, once the
    // corpus proves that family's facts equal) so display / queue / Station Watch / Smart
    // Start read the Nexus-derived facts; false falls fully back to the WsjtxMessage path.
    //
    // Both SemanticDecode instances are always carried side by side wherever the comparison
    // runs, so a mismatch is always diagnosable regardless of this flag's state, and flipping
    // it never discards either source.
    public static class SemanticCutover
    {
        public static bool UseNexusSemantics = false;
    }
}
