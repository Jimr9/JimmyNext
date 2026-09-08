using System;
using WsjtxUdpLib.Messages.Out;

namespace WSJTX_Controller
{
    // Nexus modernization Stage 5 (2026-09-08): the canonical set of FT8/FT4 semantic facts
    // about ONE decode, computed two independent ways so they can be diffed field by field:
    //
    //   * FromWsjtxMessage(text)          -- Jimmy's OWN parser (WsjtxMessage.*), the path
    //                                        production still uses today.
    //   * FromNexus(row, envelope, myCall) -- Nexus's parser, surfaced on the Direct snapshot
    //                                        as DecodeRow flags (Stage 3) + the decodeSemantics
    //                                        envelope (Stage 4, EngineHost/src/decode_semantics.rs,
    //                                        which itself calls tempo_core::message::Msg::parse).
    //
    // Field names/semantics deliberately mirror each other so SemanticParityLogger can compare
    // an instance from each source directly. NOTHING in production reads this yet -- Stage 6+
    // migrates consumers only after the shadow comparison proves a fact equal across the corpus.
    // Modelled on Classification/ClassifiedCall + ClassificationEngine.
    internal sealed class SemanticDecode
    {
        // Which parser produced this instance -- for the parity log only.
        public string Source { get; set; } = "";

        // Sender ("de"). Null when not identifiable.
        public string From { get; set; }
        // Recipient ("to"). Null for a CQ / free text. (WsjtxMessage.ToCall returns "CQ" for a
        // CQ -- normalized to null here so the two sources line up.)
        public string To { get; set; }

        public bool IsCq { get; set; }
        public bool IsDirectedCq { get; set; }
        // The directed-CQ token ("DX", "NA", "POTA", "TEST", "040" ...). Null unless IsDirectedCq.
        public string CqTarget { get; set; }

        // "to" resolves to my callsign.
        public bool AddressedToMe { get; set; }

        // Maidenhead grid the decode carried (CQ / reply), else null.
        public string Grid { get; set; }

        // Numeric signal report for a Report / RReport, else null.
        public int? ReportDb { get; set; }
        public bool IsReport { get; set; }      // "<to> <de> -NN"
        public bool IsRReport { get; set; }     // "<to> <de> R-NN"
        public bool IsRrr { get; set; }         // "<to> <de> RRR"
        public bool IsRr73 { get; set; }        // "<to> <de> RR73"
        public bool Is73 { get; set; }          // "<to> <de> 73"

        // Canonical single-token kind: cq | directedCq | reply | report | rReport | rrr | rr73 |
        // 73 | fieldDay | other. Lets a consumer switch on one value instead of five booleans.
        public string Kind { get; set; } = "other";

        // Sender call form: standard | compound | nonstandard | unknown. WsjtxMessage has no
        // first-class classifier for this, so the WsjtxMessage source fills it best-effort
        // (a "/" => compound, its own IsInvalidCall heuristic => nonstandard); a disagreement
        // here is an EXPECTED finding -- Nexus (is_std_call / is_compound / is_nonstandard_call)
        // is the authority and this is one of the facts Stage 5 is meant to prove.
        public string CallForm { get; set; } = "unknown";

        private static string NullIfCq(string toCall) =>
            (toCall == null || string.Equals(toCall, "CQ", StringComparison.Ordinal)) ? null : toCall;

        private static string DeriveKind(SemanticDecode d)
        {
            if (d.IsDirectedCq) return "directedCq";
            if (d.IsCq) return "cq";
            if (d.Is73) return "73";
            if (d.IsRr73) return "rr73";
            if (d.IsRrr) return "rrr";
            if (d.IsRReport) return "rReport";
            if (d.IsReport) return "report";
            if (d.Grid != null && d.From != null && d.To != null) return "reply";
            return "other";
        }

        // ── OLD path: Jimmy's own WsjtxMessage parser on the (already normalized) decode text ──
        public static SemanticDecode FromWsjtxMessage(string message, string myCall)
        {
            var d = new SemanticDecode { Source = "wsjtx" };
            if (string.IsNullOrWhiteSpace(message)) return d;

            d.From = WsjtxMessage.DeCall(message);
            d.To = NullIfCq(WsjtxMessage.ToCall(message));
            d.IsCq = WsjtxMessage.IsCQ(message);
            d.CqTarget = WsjtxMessage.DirectedTo(message);
            d.IsDirectedCq = d.CqTarget != null;
            d.AddressedToMe = d.To != null && !string.IsNullOrEmpty(myCall)
                              && string.Equals(d.To, myCall, StringComparison.OrdinalIgnoreCase);
            d.Grid = WsjtxMessage.Grid(message);

            d.IsReport = WsjtxMessage.IsReport(message);
            d.IsRReport = WsjtxMessage.IsRogerReport(message);
            d.IsRrr = WsjtxMessage.IsRogers(message);
            d.IsRr73 = WsjtxMessage.IsRR73(message);
            d.Is73 = WsjtxMessage.Is73(message);

            string rst = WsjtxMessage.RstRecd(message);
            if (!string.IsNullOrEmpty(rst) && int.TryParse(rst.Replace("+", ""), out int r))
                d.ReportDb = r;

            // Best-effort call form from the sender (WsjtxMessage has no real classifier).
            if (string.IsNullOrEmpty(d.From)) d.CallForm = "unknown";
            else if (d.From.Contains("/")) d.CallForm = "compound";
            else if (WsjtxMessage.IsInvalidCall(d.From)) d.CallForm = "nonstandard";
            else d.CallForm = "standard";

            d.Kind = DeriveKind(d);
            return d;
        }

        // ── NEW path: Nexus's parser via the Direct snapshot (Stage 3 flags + Stage 4 envelope) ──
        // `env` is the decodeSemantics entry for this row. A v1.10.3 EngineHost always emits it;
        // when it is ABSENT (an older host, or a partial test snapshot) FromNexus has ONLY the
        // Stage 3 DecodeRow flags -- not the recipient / report-kind facts -- so it fills the
        // rest from the WsjtxMessage parse of row.Message rather than returning a crippled
        // object a migrated consumer would then trust. DirectApplyDecodes additionally does not
        // ATTACH the result to the decode when env was absent (EffectiveSemantic then uses
        // FromWsjtxMessage directly), so this fallback is belt-and-braces.
        public static SemanticDecode FromNexus(DirectDecodeRow row, DirectDecodeSemantics env, string myCall)
        {
            if (row == null) return new SemanticDecode { Source = "nexus" };

            if (env == null || env.SchemaVersion <= 0)
            {
                // No Stage 4 envelope: parse the text ourselves, then let the Stage 3 row flags
                // that ARE present override where they are authoritative.
                var f = FromWsjtxMessage(row.Message, myCall);
                f.Source = "nexus-rowflags";
                if (row.IsCq) f.IsCq = true;
                if (!string.IsNullOrEmpty(row.Grid)) f.Grid = row.Grid;
                if (row.DirectedToMe) f.AddressedToMe = true;
                return f;
            }

            var d = new SemanticDecode { Source = "nexus" };

            // Stage 3 DecodeRow flags (a TX-text envelope passes a bare row -- the envelope's
            // own Kind then supplies IsCq below).
            d.IsCq = row.IsCq || env.Kind == "cq" || env.Kind == "directedCq";
            d.Grid = string.IsNullOrEmpty(row.Grid) ? null : row.Grid;
            bool rowSignoff = row.Signoff; // true for RR73 | 73 (not RRR)

            {
                d.From = string.IsNullOrEmpty(env.From) ? null : env.From;
                d.To = string.IsNullOrEmpty(env.To) ? null : env.To;
                d.CqTarget = string.IsNullOrEmpty(env.CqDirection) ? null : env.CqDirection;
                d.IsDirectedCq = string.Equals(env.Kind, "directedCq", StringComparison.Ordinal);
                d.AddressedToMe = env.AddressedToMe;
                if (d.Grid == null && !string.IsNullOrEmpty(env.Grid)) d.Grid = env.Grid;
                d.ReportDb = env.ReportDb;
                d.IsReport = string.Equals(env.Kind, "report", StringComparison.Ordinal);
                d.IsRReport = string.Equals(env.Kind, "rReport", StringComparison.Ordinal);
                d.IsRrr = string.Equals(env.Signoff, "rrr", StringComparison.Ordinal);
                d.IsRr73 = string.Equals(env.Signoff, "rr73", StringComparison.Ordinal);
                d.Is73 = string.Equals(env.Signoff, "sevenThree", StringComparison.Ordinal);
                d.CallForm = string.IsNullOrEmpty(env.CallForm) ? "unknown" : env.CallForm;

                // Prefer the envelope's own canonical kind where it maps 1:1.
                switch (env.Kind)
                {
                    case "cq": d.Kind = "cq"; break;
                    case "directedCq": d.Kind = "directedCq"; break;
                    case "reply": d.Kind = "reply"; break;
                    case "report": d.Kind = "report"; break;
                    case "rReport": d.Kind = "rReport"; break;
                    case "rrr": d.Kind = "rrr"; break;
                    case "rr73": d.Kind = "rr73"; break;
                    case "sevenThree": d.Kind = "73"; break;
                    case "fieldDay": d.Kind = "fieldDay"; break;
                    default: d.Kind = "other"; break;
                }
            }

            // rowSignoff (RR73|73, from the Stage 3 flag) is a cross-check only -- the envelope's
            // Signoff subtype above is authoritative and finer-grained. A disagreement would be
            // surfaced by the parity log via Kind / IsRr73 / Is73.
            _ = rowSignoff;

            return d;
        }
    }
}
