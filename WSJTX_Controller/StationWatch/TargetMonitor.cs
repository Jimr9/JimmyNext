using System;
using WsjtxUdpLib.Messages.Out;

namespace WSJTX_Controller
{
    // Station Watch / Smart QSO Start (2.0.63): ONE shared, transport-agnostic tracker for "what
    // is a single watched/target callsign doing on the air right now". Two Jimmy features each
    // own one instance of this class (WsjtxClient holds a StationWatch-purpose one and a
    // SmartStart-purpose one) rather than two separately-invented state machines -- see this
    // type's own doc below for exactly what it owns and what it deliberately does not.
    //
    // Deliberately has ZERO dependency on NotificationCenter/SpeechCoordinator/WinForms/the
    // engine transport -- it is fed decodes and receive-period-boundary ticks by WsjtxClient and
    // reports back only through the Observed event (a plain fact: "the target called CQ", "the
    // target appears available") and the ReadyToStart flag. WsjtxClient's own glue turns an
    // Observed fact into a notification and turns "ready" into an actual REPLY dispatch through
    // the EXISTING ReplyTo path -- this class never sends anything to the radio, never touches
    // callInProg, and never generates a TX message itself.
    //
    // Classification reuses WsjtxMessage's existing, tested static parser (Is73/IsRR73/IsRogers/
    // IsReport/IsRogerReport/IsCQ/DeCall/ToCall/Payload/IsFoxHound) exactly as DecodeMessage/
    // EnqueueDecodeMessage/ProcessDecodeMsg already do everywhere else in Jimmy -- no new FT8/FT4
    // grammar is invented here, root-caused via a read of Nexus's own tempo_core::message::Msg
    // (EngineHost's existing dependency) which parses the identical message shapes; Jimmy's own
    // parser already gives every classification this feature needs, so nothing new was needed in
    // EngineHost/Rust at all. See the 2.0.63 release notes for the full trace.
    public enum TargetPurpose
    {
        StationWatch,
        SmartStart,
    }

    // What TargetMonitor actually reports, one fact at a time. Only what was truly decoded --
    // the unseen half of another station's exchange is never synthesized (WatchTargetAmbiguous/
    // TargetAddressingOther describe only what Jimmy itself heard from the TARGET; a reply the
    // target's peer sent is never inferred, only reported via OtherPartyObserved when Jimmy
    // separately decodes that peer's own transmission).
    public enum TargetObservationKind
    {
        WatchStarted,
        WatchStopped,
        TargetCq,
        TargetAddressingUs,
        TargetAddressingOther,
        TargetReport,
        TargetRReport,
        TargetRrr,
        TargetRr73,
        Target73,
        TargetAmbiguous,          // heard the target, but the payload didn't parse into a known form
        OtherPartyObserved,       // the target's apparent peer was itself heard, addressed to the target
        SmartStartWaiting,        // one more completed appropriate receive opportunity ticked by
        SmartStartTargetAvailable,// TargetMonitor has decided there is enough evidence to start
    }

    public sealed class TargetObservation
    {
        public TargetObservationKind Kind;
        public TargetPurpose Purpose;
        public string Target;
        public string Peer;          // null when not applicable
        public string Value;         // formatted report ("-08"/"R-05") or a waiting-progress phrase
        public string RawMessage;    // the decoded text this observation came from, if any

        public TargetObservation(TargetObservationKind kind, TargetPurpose purpose, string target,
            string peer = null, string value = null, string rawMessage = null)
        {
            Kind = kind;
            Purpose = purpose;
            Target = target;
            Peer = peer;
            Value = value;
            RawMessage = rawMessage;
        }
    }

    public sealed class TargetMonitor
    {
        public TargetPurpose Purpose { get; }

        public string TargetCall { get; private set; }
        public bool IsActive => TargetCall != null;

        // The station the target itself appears to be working right now (from the target's own
        // decoded traffic only), or null (target idle / calling CQ / unknown).
        public string ApparentPeer { get; private set; }

        // Which of the two alternating receive slots the target's own transmissions have been
        // heard on -- null until the first confident target decode. Never inferred any other way.
        public bool? TargetEvenParity { get; private set; }

        // Smart Start only. Ignored (never incremented) for a StationWatch-purpose instance --
        // Station Watch never counts toward an automatic start.
        public int SilenceCount { get; private set; }
        public int SilenceThreshold { get; set; } = 2;

        // The most recent decode from the target that carries enough information (frequency,
        // parity, exact wire text) to hand off to the real REPLY path. Work Watched Station Now
        // and Smart Start's own auto-fire both reply using THIS, never a synthesized message.
        public EnqueueDecodeMessage LastUsableDecode { get; private set; }

        // Smart Start only: true once TargetMonitor has decided there is enough evidence to
        // request a normal QSO start. Cleared the instant the caller consumes it (see
        // ConsumeReadyToStart) or the monitor is stopped/reset.
        public bool ReadyToStart { get; private set; }

        public event Action<TargetObservation> Observed;

        // One extra appropriate receive opportunity must elapse after an ORDINARY validated RR73
        // before Smart Start treats the target as available -- the operator's own decision (the
        // other station may still send a courtesy 73). Cleared by any newer, clearer event
        // (target 73 / target CQ / a fresh RR73) since those either already fire immediately or
        // restart the same one-opportunity wait.
        private bool _rr73AwaitingOneMoreOpportunity;

        // Dedup: OnReceivePeriodComplete is called once per real slot transition, but guards
        // against being asked twice for the same slot (defensive; the real per-tick caller
        // already only calls this once per boundary).
        private ulong? _lastCountedSlot;

        private string _band;
        private string _mode;
        private string _sessionToken;

        public TargetMonitor(TargetPurpose purpose)
        {
            Purpose = purpose;
        }

        // Starts the watch, or -- if one is already active -- explicitly replaces it. Either way
        // this raises exactly ONE fact, WatchStarted for the NEW call (spec: "explicitly replace
        // it and announce the new watched station" -- a single clean announcement, never a
        // "stopped watching OLD" first).
        public void Start(string call, string band, string mode, string sessionToken)
        {
            if (string.IsNullOrEmpty(call)) return;

            TargetCall = call;
            ApparentPeer = null;
            TargetEvenParity = null;
            SilenceCount = 0;
            LastUsableDecode = null;
            ReadyToStart = false;
            _rr73AwaitingOneMoreOpportunity = false;
            _lastCountedSlot = null;
            _band = band;
            _mode = mode;
            _sessionToken = sessionToken;

            Raise(TargetObservationKind.WatchStarted, call);
        }

        public void Stop(bool announce = true)
        {
            if (TargetCall == null) return;
            string call = TargetCall;
            TargetCall = null;
            ApparentPeer = null;
            TargetEvenParity = null;
            SilenceCount = 0;
            LastUsableDecode = null;
            ReadyToStart = false;
            _rr73AwaitingOneMoreOpportunity = false;
            _lastCountedSlot = null;
            if (announce) Raise(TargetObservationKind.WatchStopped, call);
        }

        // Smart Start / Work Now consume a "ready" exactly once -- calling this both reads and
        // clears ReadyToStart, so a caller that decides not to act on it (e.g. no usable decode
        // after all) doesn't leave a stale flag that fires again on the next unrelated check.
        public bool ConsumeReadyToStart()
        {
            if (!ReadyToStart) return false;
            ReadyToStart = false;
            return true;
        }

        // Cancels a not-yet-acted-on automatic start (Escape/Halt) without stopping the watch
        // itself. For a StationWatch-purpose instance this is a no-op beyond clearing state that
        // was never set (Station Watch never sets ReadyToStart) -- callers may call it
        // unconditionally on both instances.
        public void CancelPendingStart()
        {
            ReadyToStart = false;
            _rr73AwaitingOneMoreOpportunity = false;
        }

        // Feed every decode Jimmy processes (not just ones already in the reply queue) while this
        // monitor is active. `evenSlot`/`slot` come from the engine's own RadioStatus.Slot at the
        // moment this decode arrived (WsjtxClient.Direct.cs) -- the real per-period identity, not
        // a guess. `myCall` is passed in rather than cached so a callsign change mid-session is
        // never stale.
        public void ObserveDecode(EnqueueDecodeMessage d, bool evenSlot, string myCall)
        {
            if (TargetCall == null || d == null || string.IsNullOrEmpty(d.Message)) return;

            string de = d.DeCall();
            if (de == null) return;

            if (!string.Equals(de, TargetCall, StringComparison.OrdinalIgnoreCase))
            {
                // Not the target -- only interesting if it is the target's OWN apparent peer
                // replying TO the target (never invent the unseen half of the exchange; only
                // report what was actually decoded).
                string toTarget = WsjtxMessage.ToCall(d.Message);
                if (!string.IsNullOrEmpty(ApparentPeer)
                    && string.Equals(de, ApparentPeer, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(toTarget)
                    && string.Equals(toTarget, TargetCall, StringComparison.OrdinalIgnoreCase))
                {
                    // Only what was actually decoded from the PEER's own transmission -- never
                    // the unseen half of the target's own exchange. Value carries the peer's own
                    // payload (report/RRR/RR73/73/grid) so the glue can word it naturally, e.g.
                    // "W1ABC R minus 5." from a decoded "W1ABC K4YT R-05".
                    Raise(TargetObservationKind.OtherPartyObserved, TargetCall, de, WsjtxMessage.Payload(d.Message), d.Message);
                }
                return;
            }

            // Any confidently attributed decode from the target -- including an ambiguous one --
            // resets the silence count and records the freshest usable decode/parity.
            SilenceCount = 0;
            _lastCountedSlot = null;
            TargetEvenParity = evenSlot;
            LastUsableDecode = d;

            if (WsjtxMessage.IsCQ(d.Message))
            {
                ApparentPeer = null;
                _rr73AwaitingOneMoreOpportunity = false;
                Raise(TargetObservationKind.TargetCq, TargetCall, null, null, d.Message);
                if (Purpose == TargetPurpose.SmartStart) SignalReady();
                return;
            }

            string to = WsjtxMessage.ToCall(d.Message);
            if (string.IsNullOrEmpty(to))
            {
                Raise(TargetObservationKind.TargetAmbiguous, TargetCall, null, null, d.Message);
                return;
            }

            bool addressingUs = !string.IsNullOrEmpty(myCall)
                && string.Equals(to, myCall, StringComparison.OrdinalIgnoreCase);
            if (!addressingUs && !string.Equals(to, ApparentPeer, StringComparison.OrdinalIgnoreCase))
                ApparentPeer = to;
            string peer = addressingUs ? myCall : to;

            if (WsjtxMessage.IsRR73(d.Message))
            {
                Raise(TargetObservationKind.TargetRr73, TargetCall, peer, null, d.Message);
                if (Purpose == TargetPurpose.SmartStart)
                {
                    // Fox/Hound (multiplex) RR73 must NOT be treated as generic "target is now
                    // available" evidence -- preserve the existing protocol distinction (reuses
                    // the same IsFoxHound heuristic Jimmy's own "Possible F/H" tagging uses,
                    // ClassificationEngine.cs).
                    if (!d.IsFoxHound())
                        _rr73AwaitingOneMoreOpportunity = true;   // operator's one-extra-opportunity rule
                }
                return;
            }
            if (WsjtxMessage.Is73(d.Message))
            {
                _rr73AwaitingOneMoreOpportunity = false;
                Raise(TargetObservationKind.Target73, TargetCall, peer, null, d.Message);
                if (Purpose == TargetPurpose.SmartStart) SignalReady();
                return;
            }
            if (WsjtxMessage.IsRogers(d.Message))       // RRR -- distinct from, and NOT equivalent to, RR73/73
            {
                Raise(TargetObservationKind.TargetRrr, TargetCall, peer, null, d.Message);
                return;
            }
            if (WsjtxMessage.IsRogerReport(d.Message))
            {
                Raise(TargetObservationKind.TargetRReport, TargetCall, peer, WsjtxMessage.Payload(d.Message), d.Message);
                return;
            }
            if (WsjtxMessage.IsReport(d.Message))
            {
                Raise(TargetObservationKind.TargetReport, TargetCall, peer, WsjtxMessage.Payload(d.Message), d.Message);
                return;
            }
            if (addressingUs)
            {
                Raise(TargetObservationKind.TargetAddressingUs, TargetCall, peer, null, d.Message);
                return;
            }
            // Directed at someone else with a payload that isn't one of the recognized standard
            // forms (grid, free text, contest exchange, etc.) -- still meaningful "working
            // another station" evidence, just not one of the specific typed observations above.
            Raise(TargetObservationKind.TargetAddressingOther, TargetCall, peer, null, d.Message);
        }

        // Called once per real, completed receive-period boundary (the same engine-slot-advance
        // signal SpeechCoordinator.OnReceiveCycleComplete already uses) WHILE this monitor is
        // active. `weTransmittedThisSlot` excludes a period Jimmy itself transmitted during --
        // never counted toward silence. `evenSlot`/`slot`/`band`/`mode`/`sessionToken` must all
        // match this monitor's own captured context or the opportunity does not count (a stale
        // opportunity from before activation, a different band/mode, or a different engine
        // session/reconnect never contributes).
        public void OnReceivePeriodComplete(ulong slot, bool evenSlot, string band, string mode,
            string sessionToken, bool weTransmittedThisSlot)
        {
            if (TargetCall == null) return;
            if (Purpose != TargetPurpose.SmartStart) return;   // Station Watch never counts silence
            if (weTransmittedThisSlot) return;
            if (!string.Equals(band, _band, StringComparison.OrdinalIgnoreCase)) return;
            if (!string.Equals(mode, _mode, StringComparison.OrdinalIgnoreCase)) return;
            if (!string.Equals(sessionToken, _sessionToken, StringComparison.Ordinal)) return;
            if (TargetEvenParity == null) return;               // parity not yet established -- never counted
            if (evenSlot != TargetEvenParity.Value) return;     // opposite parity -- never counted
            if (_lastCountedSlot == slot) return;                // defensive dedup
            _lastCountedSlot = slot;

            if (_rr73AwaitingOneMoreOpportunity)
            {
                _rr73AwaitingOneMoreOpportunity = false;
                SignalReady();
                return;
            }

            SilenceCount++;
            if (SilenceCount >= SilenceThreshold)
            {
                SignalReady();
            }
            else
            {
                Raise(TargetObservationKind.SmartStartWaiting, TargetCall, null, $"{SilenceCount} of {SilenceThreshold}");
            }
        }

        // Engine/session/CAT/TX transitions that make Smart Start's in-progress state stale.
        // Station Watch (receive-only) is untouched -- it may continue on captured callsign
        // alone regardless of CAT/TX availability.
        public void OnSmartStartNonActionable()
        {
            if (Purpose != TargetPurpose.SmartStart) return;
            SilenceCount = 0;
            ReadyToStart = false;
            _rr73AwaitingOneMoreOpportunity = false;
            _lastCountedSlot = null;
        }

        private void SignalReady()
        {
            ReadyToStart = true;
            Raise(TargetObservationKind.SmartStartTargetAvailable, TargetCall);
        }

        private void Raise(TargetObservationKind kind, string target, string peer = null, string value = null, string raw = null)
        {
            Observed?.Invoke(new TargetObservation(kind, Purpose, target, peer, value, raw));
        }
    }
}
