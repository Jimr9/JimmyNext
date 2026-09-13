using System;
using WsjtxUdpLib.Messages.Out;

namespace WSJTX_Controller
{
    // Shared "target is working peer" fact + per-period repeat gate (2026-09-11 unification of
    // otherStr / Smart Start's SmartStartTargetBusy / Station Watch's StationWatchActivity --
    // see the design writeup in conversation for the full rationale). ONE structured identity,
    // detected identically regardless of which of the three contexts observed it, submitted to
    // ONE tracker per target callsign so at most one announcement reaches the operator per real
    // decode period, in either "repeat unchanged" mode -- see TargetActivityTracker.Evaluate.
    //
    // Deliberately excludes the SmartStart lifecycle facts (Armed/Waiting/CallStarting/Engaged/
    // Yielded) and Station Watch's TargetAddressingUs/TargetAmbiguous -- those are not "target
    // working peer" activity and are untouched by this unification.
    public readonly struct TargetActivityFact : IEquatable<TargetActivityFact>
    {
        public string Target { get; }
        public string Peer { get; }          // "" for a bare CQ (no peer)
        public TargetObservationKind Kind { get; }
        public string Value { get; }         // RAW payload token ("-11", "RRR", ...), "" when none.
                                              // Presentation formatting (SpokenReport, DisplayCallsign,
                                              // SpacifyPayload) is applied only at the final announce
                                              // step, never baked into the identity.

        public TargetActivityFact(string target, string peer, TargetObservationKind kind, string value)
        {
            Target = target ?? "";
            Peer = peer ?? "";
            Kind = kind;
            Value = value ?? "";
        }

        public bool Equals(TargetActivityFact other) =>
            string.Equals(Target, other.Target, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Peer, other.Peer, StringComparison.OrdinalIgnoreCase) &&
            Kind == other.Kind &&
            string.Equals(Value, other.Value, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is TargetActivityFact f && Equals(f);

        public override int GetHashCode() =>
            (Target.ToUpperInvariant(), Peer.ToUpperInvariant(), Kind, Value).GetHashCode();
    }

    public enum TargetActivityDecision
    {
        Suppress,
        Announce,
    }

    // One instance per target callsign currently being narrated by any of the three contexts.
    // Owns exactly two pieces of state: the last fact actually announced, and the decode-period
    // identity (the engine's own monotonic slot number -- see WsjtxClient._directLastSlotSeen)
    // it was announced in. Both axes are checked independently of WHICH context calls Evaluate --
    // context never affects identity, suppression, or period tracking (see the design's own
    // "source precedence" answer: precedence only ever selects wording, at the call site, before
    // Evaluate is ever reached).
    public sealed class TargetActivityTracker
    {
        private TargetActivityFact? _lastAnnouncedFact;
        private ulong? _lastAnnouncedPeriodId;

        // periodId: the completed-period identifier the observation belongs to (the engine's own
        // slot number, NOT a wall clock -- see WsjtxClient._directLastSlotSeen). repeatUnchanged:
        // the operator's "Repeat unchanged QSO activity each period" setting, read live by the
        // caller.
        public TargetActivityDecision Evaluate(TargetActivityFact fact, ulong periodId, bool repeatUnchanged)
        {
            bool sameFactAsLastAnnounced = _lastAnnouncedFact.HasValue && _lastAnnouncedFact.Value.Equals(fact);

            if (!sameFactAsLastAnnounced)
            {
                // A genuine state change -- target, peer, kind, or value differs from whatever was
                // last announced. Always announces, in EITHER mode, and always supersedes/replaces
                // any stale "unchanged repeat" bookkeeping (the caller's own NotificationCenter
                // submission uses NotificationStatusDelivery.LatestOnly for exactly this reason --
                // see SmartStartTargetBusy/StationWatchActivity's own policy comments).
                _lastAnnouncedFact = fact;
                _lastAnnouncedPeriodId = periodId;
                return TargetActivityDecision.Announce;
            }

            // Unchanged fact. Multiple equivalent observations within the SAME period (Smart
            // Start, Station Watch, and the ordinary path all classifying the identical decode)
            // must never produce more than one announcement, in EITHER mode -- this is not a
            // "repeat", it is the identical single observation surfacing more than once.
            if (_lastAnnouncedPeriodId == periodId) return TargetActivityDecision.Suppress;

            if (!repeatUnchanged) return TargetActivityDecision.Suppress;

            // A later applicable period, same unchanged fact, option enabled -- announce once more.
            _lastAnnouncedPeriodId = periodId;
            return TargetActivityDecision.Announce;
        }
    }

    // Pure classification for the ordinary (no Smart Start / Station Watch) "callInProg is
    // working another station" case -- mirrors the SAME target-\>peer priority order
    // TargetMonitor.IngestTargetDecode / RaiseSeedClassification already use (Rr73 > 73 > Rrr >
    // RReport > Report > AddressingOther), so an identical decode classifies identically whether
    // or not a watcher happens to be active on the same call. Returns null for anything that is
    // not this fact at all: a bare CQ (no ToCall), an unresolved <...> peer, or a decode
    // addressed to US (that is the "answered you" hand-off, a different fact entirely, already
    // handled elsewhere).
    internal static class TargetActivityClassifier
    {
        public static TargetActivityFact? ClassifyPeerActivity(EnqueueDecodeMessage d, string target, string myCall)
        {
            if (d?.Message == null || string.IsNullOrEmpty(target)) return null;
            string toCall = WsjtxMessage.ToCall(d.Message);
            if (toCall == null) return null;                          // CQ / unparseable -- not this fact

            string peer = WsjtxMessage.RemoveAngleBrackets(toCall);
            if (string.IsNullOrEmpty(peer) || peer.Contains(".")) return null;   // unresolved <...> peer
            if (!string.IsNullOrEmpty(myCall) && string.Equals(peer, myCall, StringComparison.OrdinalIgnoreCase))
                return null;                                          // addressed to us -- a different fact

            var sem = d.EffectiveSemantic(myCall);
            string payload = WsjtxMessage.Payload(d.Message);

            // 2026-09-11 divergence fix (found while verifying cross-route identity): TargetMonitor.
            // IngestTargetDecode's Raise() calls pass the raw payload for EVERY one of these kinds
            // except AddressingOther (null) -- Rr73/73/Rrr included. This classifier originally
            // hardcoded "" for Rr73/73/Rrr, which would have made an identical decode produce a
            // DIFFERENT TargetActivityFact.Value depending on whether Smart Start/Station Watch or
            // this ordinary path classified it -- silently defeating cross-route dedup (a real
            // state-change false-positive) across exactly the watcher-transition scenarios this
            // unification exists to get right. Now uses `payload` for every kind TargetMonitor
            // does, so the two routes are provably identical for every shared observation kind.
            if (sem.IsRr73) return new TargetActivityFact(target, peer, TargetObservationKind.TargetRr73, payload);
            if (sem.Is73) return new TargetActivityFact(target, peer, TargetObservationKind.Target73, payload);
            if (sem.IsRrr) return new TargetActivityFact(target, peer, TargetObservationKind.TargetRrr, payload);
            if (sem.IsRReport) return new TargetActivityFact(target, peer, TargetObservationKind.TargetRReport, payload);
            if (sem.IsReport) return new TargetActivityFact(target, peer, TargetObservationKind.TargetReport, payload);
            return new TargetActivityFact(target, peer, TargetObservationKind.TargetAddressingOther, "");
        }
    }
}
