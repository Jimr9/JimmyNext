using WsjtxUdpLib.Messages.Out;

namespace WSJTX_Controller
{
    // Nexus modernization Stage 6+ (2026-09-08): the one seam every migrated consumer reads
    // its FT8/FT4 semantic facts through, mirroring EnqueueDecodeMessage.EffectiveClassification().
    //
    //   * When SemanticCutover.UseNexusSemantics is on AND this decode carries a Nexus-derived
    //     SemanticDecode (attached by DirectApplyDecodes), return THAT -- Nexus's own parse.
    //   * Rollback (UseNexusSemantics=false via the .ini key, transitional -- see its own
    //     comment): build one from WsjtxMessage on the fly, exactly as before the migration.
    //   * Stage 14 (2026-09-14, the no-fallback contract): a LIVE received decode with the
    //     cutover ON and no attached envelope is no longer silently re-parsed with
    //     WsjtxMessage -- that silent degrade is exactly what let the VP5/K5UR bracket bug
    //     hide. It now resolves to an UNKNOWN identity (the same shape as a genuinely
    //     unparseable Nexus decode, e.g. Msg::Other), which existing admission gates already
    //     reject on a null From/To. WsjtxClient.Direct.cs's own S2 contract check has already
    //     told the operator EngineHost is not doing its job by the time this runs. Test mode
    //     (TestModeGuard.IsTestMode) is exempt: most of today's test corpus does not yet
    //     attach a synthetic envelope to every snapshot (tracked separately -- Phase E) and
    //     must keep working unchanged while that migration completes.
    //
    // Both sources are still computed side by side in DirectApplyDecodes for the parity log,
    // so flipping the valve never discards either. Stage 5 proved the core migrateable facts
    // (IsCq / IsDirectedCq / CqTarget / Grid / ReportDb / IsReport / IsRReport / IsRrr / IsRr73
    // / Is73) equal across the corpus; the only differences are cases where Nexus is strictly
    // more correct (see C:\chat gpt\nexus progress.txt Stage 5 findings).
    internal static class SemanticExtensions
    {
        public static SemanticDecode EffectiveSemantic(this EnqueueDecodeMessage d, string myCall)
        {
            if (d == null) return new SemanticDecode();
            if (SemanticCutover.UseNexusSemantics)
            {
                if (d.Semantic != null) return d.Semantic;
                if (!TestModeGuard.IsTestMode) return new SemanticDecode { Source = "nexus-missing" };
            }
            return SemanticDecode.FromWsjtxMessage(d.Message, myCall);
        }

        // Nexus modernization Stage 10: the same seam for the QSO's own "now sending" text.
        // `txText` is Jimmy's already-normalized curTxMsg; `env` is snap.QsoTxSemantics (the
        // EngineHost envelope for qso.txNow, null when listening). Cutover on + env present ->
        // Nexus's parse of the TX text; otherwise WsjtxMessage on txText, exactly as before.
        internal static SemanticDecode EffectiveTxSemantic(string txText, DirectDecodeSemantics env, string myCall)
        {
            if (SemanticCutover.UseNexusSemantics && env != null && env.SchemaVersion > 0)
                return SemanticDecode.FromNexus(new DirectDecodeRow { Message = env.RawMessage }, env, myCall);
            return SemanticDecode.FromWsjtxMessage(txText, myCall);
        }
    }
}
