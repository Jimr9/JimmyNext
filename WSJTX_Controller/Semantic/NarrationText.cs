using System;
using WsjtxUdpLib.Messages.Out;

namespace WSJTX_Controller
{
    // Nexus modernization Stage 7b (2026-09-14): replaces the free-text WsjtxMessage.Payload()
    // extraction for every narration/display consumer (Station Watch phrases, notification
    // template tokens, the TX/RX status line) with formatting driven off a decode's own
    // SemanticDecode -- structured facts already resolved by EffectiveSemantic, not a fresh
    // re-split of the raw message text.
    internal static class NarrationText
    {
        // Structured, Nexus-semantic formatting of a decode's "payload" string for narration --
        // reproduces WSJT-X's own wire convention exactly: a report is sign + 2-digit magnitude
        // ("-15"/"+03"), a roger-report is "R" + that same shape ("R-15"), and RRR/RR73/73/CQ are
        // their own literal tokens. Returns null for "reply" only when no grid is present, and
        // for "fieldDay"/"other" -- kinds with no single structured string to return -- in which
        // case the caller falls back to ResidualDisplayText below.
        public static string StructuredPayload(SemanticDecode sem)
        {
            if (sem == null) return null;
            switch (sem.Kind)
            {
                case "cq":
                    return "CQ";
                case "directedCq":
                    return sem.CqTarget != null ? $"CQ {sem.CqTarget}" : "CQ";
                case "rrr":
                    return "RRR";
                case "rr73":
                    return "RR73";
                case "73":
                    return "73";
                case "reply":
                    return sem.Grid;
                case "report":
                    return FormatReportValue(sem.ReportDb, rogerPrefix: false);
                case "rReport":
                    return FormatReportValue(sem.ReportDb, rogerPrefix: true);
                default:
                    return null;
            }
        }

        // WSJT-X's own report convention: always signed, always 2-digit magnitude (e.g. "+00",
        // never a bare "00" or "-0"). Matches WsjtxMessage.IsReport/IsRogerReport's own exact-shape
        // validation (3 chars "+NN"/"-NN", or "R" + that shape) -- this is the inverse of that
        // parse, not a new convention.
        private static string FormatReportValue(int? reportDb, bool rogerPrefix)
        {
            if (reportDb == null) return null;
            string sign = reportDb.Value >= 0 ? "+" : "-";
            string magnitude = Math.Abs(reportDb.Value).ToString("D2");
            return (rogerPrefix ? "R" : "") + sign + magnitude;
        }

        // ── Presentation-only residual text -- NOT Nexus semantic identity ────────────────────
        //
        // BOUNDARY (read this before touching anything here during Phase G):
        //
        // This exists ONLY to produce a human-readable label for a decode whose Kind is
        // "fieldDay"/"other" -- an arbitrary contest-exchange fragment or otherwise unrecognized
        // shape that Nexus's structured semantics do not, and are not meant to, model as a typed
        // fact. It is pure presentation formatting, the same role a ToString() or a display
        // column plays -- nothing about it is, or may become, operational:
        //
        //   * It must NEVER be used to decide message kind, admission, queue/QSO identity,
        //     sequencing, or any other operational fact. SemanticDecode.Kind (from
        //     EffectiveSemantic) is the only authority for all of that, full stop -- structured
        //     or not.
        //   * It is a thin wrapper over the legacy free-text splitter (WsjtxMessage.Payload)
        //     purely because that splitter's "last token of a well-formed 3-4-word message"
        //     heuristic is already the best available label for free text with no structured
        //     shape. It is NOT a reintroduction of text-based parsing for anything semantic, and
        //     must not be mistaken for one.
        //
        // When Phase G removes WsjtxMessage's parsing methods, THIS is the one place a narration
        // consumer still needs something -- every other call site by then goes through
        // StructuredPayload above and never reaches here. The Phase G replacement can be as
        // simple as returning "" for the rare fieldDay/other case; it does not need to preserve
        // WsjtxMessage's exact heuristic, only that this stays presentation-only.
        public static string ResidualDisplayText(string rawMessage) => WsjtxMessage.Payload(rawMessage);
    }
}
