using WsjtxUdpLib.Messages.Out;

namespace WSJTX_Controller
{
    // Nexus modernization Stage 6+ (2026-09-08): the one seam every migrated consumer reads
    // its FT8/FT4 semantic facts through, mirroring EnqueueDecodeMessage.EffectiveClassification().
    //
    //   * When SemanticCutover.UseNexusSemantics is on AND this decode carries a Nexus-derived
    //     SemanticDecode (attached by DirectApplyDecodes), return THAT -- Nexus's own parse.
    //   * Otherwise (rollback via the .ini key, or the UDP path, or a decode the snapshot did
    //     not carry semantics for) build one from WsjtxMessage on the fly -- Jimmy's own parser,
    //     exactly as before.
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
            if (SemanticCutover.UseNexusSemantics && d.Semantic != null)
                return d.Semantic;
            return SemanticDecode.FromWsjtxMessage(d.Message, myCall);
        }
    }
}
