namespace WSJTX_Controller
{
    // ══════════════════════════════════════════════════════════════════════════════════════
    // TRANSITIONAL -- Phase F (2026-09-14). This flag exists ONLY to cover the field-
    // validation window for the Nexus semantic-identity migration (Stages 5-14, 2026-09-08
    // through 2026-09-14). It is not a permanent feature or a durable operator setting.
    //
    // Planned removal: Phase G deletes this class, SemanticDecode.FromWsjtxMessage, and every
    // operational (non-display/debug) WsjtxMessage.DeCall/ToCall/IsCQ/IsCallTo/IsInvalidType/
    // Is73/IsRR73/IsRogers/IsReport dependency, once (a) a field session run with the flag at
    // its current default (true) shows no regression versus this session's own validation, and
    // (b) Phase E's remaining test-helper migration (see JimmyTests.cs's own
    // SyntheticSemanticEnvelope -- most of the ~129 recentDecodes-constructing tests still rely
    // on the no-envelope fallback this flag's rollback path shares) is far enough along that
    // removing the fallback doesn't strand a large fraction of the suite. Until then, DO NOT
    // treat this flag's existence as license to add new call sites that assume it will always
    // be here -- every new operational identity/kind decision should read EffectiveSemantic
    // (or the cached .Semantic/._curTxMsgSemantic/._curCmdSemantic fields it feeds), never
    // WsjtxMessage directly.
    // ══════════════════════════════════════════════════════════════════════════════════════
    //
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
        // Master valve. TRUE from Stage 6: migrated consumers (see EffectiveSemantic) read
        // Nexus's own FT8/FT4 parse. Set useNexusSemantics=False in the .ini to force EVERY
        // migrated consumer back onto WsjtxMessage in one move (full rollback) -- the
        // WsjtxMessage path stays computed alongside, so nothing is lost. Only the call sites
        // a given Stage has actually converted read through EffectiveSemantic; the rest still
        // call WsjtxMessage directly until their Stage migrates them. TRANSITIONAL -- see the
        // class-level banner above; this is not meant to be a permanent operator escape hatch.
        public static bool UseNexusSemantics = true;
    }
}
