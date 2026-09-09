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
    // Classification (Nexus modernization Stage 7a, 2026-09-08): the message-type + recipient
    // facts -- IsCq / To / IsReport / IsRReport / IsRrr / IsRr73 / Is73 -- come from
    // EnqueueDecodeMessage.EffectiveSemantic (Nexus's own tempo_core::message parse via the
    // Direct snapshot when SemanticCutover.UseNexusSemantics is on; WsjtxMessage otherwise /
    // on the UDP path). Stage 5 proved those facts byte-identical to the WsjtxMessage
    // equivalents across the shadow corpus. The observation Value strings still come from
    // WsjtxMessage.Payload (narration formatting, migrated separately at Stage 7b) and F/H
    // detection stays d.IsFoxHound() (a heuristic). No new FT8/FT4 grammar is invented here.
    public enum TargetPurpose
    {
        StationWatch,
        SmartStart,
    }

    // Result of the atomic pre-transmit revalidation (RevalidateForAutoStart). Only Ok
    // authorizes an automatic REPLY / Work-Watched-Station-Now dispatch. 2.0.64 transmit-safety
    // fix (V51WW, 2026-09-05): a stale historical/queued decode may identify WHICH station the
    // operator wants, but it must never by itself authorize a future automatic transmission.
    public enum AutoStartCheck
    {
        Ok,
        NoUsableDecode,   // nothing to hand to ReplyTo
        ContextChanged,   // band / mode / engine session no longer matches what was armed
        NoLiveEvidence,   // never actually heard the target live since arming (seed alone doesn't count)
        StaleEvidence,    // heard live once, but too many silent opportunities / too much wall time since
        TargetBusy,       // current evidence shows the target working (or being called by) another station
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

        // Smart Start only. Cumulative count of ACTUAL transmitted calling overs to this target
        // across the WHOLE armed effort -- the initial call, ordinary repeated calling overs,
        // calls after a busy yield, and calls after later target-not-heard waiting all add to
        // this one total. A busy yield / re-arm (ReturnToWaiting) does NOT reset it; only Start()
        // (a genuinely new target/session) does. WsjtxClient feeds each completed calling over
        // through NoteCallingOverTransmitted at the same transmitting-just-ended edge the
        // ordinary per-call discard counter uses, and disarms Smart Start entirely once the
        // operator's Repeat Limit is reached -- so one armed target is one bounded calling
        // effort, never restarted per ReplyTo.
        public int TransmittedCallCount { get; private set; }

        // The most recent decode from the target that carries enough information (frequency,
        // parity, exact wire text) to hand off to the real REPLY path. Work Watched Station Now
        // and Smart Start's own auto-fire both reply using THIS, never a synthesized message.
        public EnqueueDecodeMessage LastUsableDecode { get; private set; }

        // Wall-clock UTC of whatever decode LastUsableDecode currently holds -- taken from the
        // decode's own RxDate+SinceMidnight when it carries a real timestamp (Direct-mode
        // decodes always do), else the moment it was observed. Read by RevalidateForAutoStart's
        // documented wall-clock backstop.
        public DateTime LastUsableDecodeUtc { get; private set; }

        // True once at least one confident decode FROM THE TARGET has been observed on the live
        // decode feed since this monitor was armed. An operator's selected/queued decode
        // (SeedSelectedDecode) sets this ONLY when that decode is still current (see
        // _seedFreshLimitPeriods) -- a stale seed identifies the call but is not live evidence.
        public bool HasLiveTargetEvidence { get; private set; }

        // Completed, appropriate (matching band/mode/session/parity, not our own TX) receive
        // opportunities elapsed since the last confident live target decode. 0 immediately after
        // hearing the target; grows by one per silent opportunity. For the silence path this
        // tracks SilenceCount; RevalidateForAutoStart also uses it as the primary freshness
        // measure (receive-opportunity context, not wall-clock polling).
        public int OpportunitiesSinceLiveEvidence { get; private set; }

        // Current evidence that the target is mid-exchange with, or actively being called by,
        // some other station. Set from live traffic only (target -> other with a mid-QSO
        // payload, or any station -> target). Cleared ONLY by current receive evidence that the
        // target is now available/turning to us (target CQ / 73 / RR73 / addressing us) -- never
        // by mere silence, so "target working another station" wins over a silence decision.
        public bool BusyWithOther { get; private set; }

        // Smart Start only: true once TargetMonitor has decided there is enough evidence to
        // request a normal QSO start. Cleared the instant the caller consumes it (see
        // ConsumeReadyToStart) or the monitor is stopped/reset.
        public bool ReadyToStart { get; private set; }

        // Smart Start only. Set once an automatic REPLY for this target has actually been
        // dispatched (WsjtxClient.EnterAwaitingEngagement, from ReplyTo's success callback):
        // Smart Start is now CALLING the target and watching for one of two outcomes --
        //   * the target answers OUR callsign  -> EngagedUs; Smart Start's job is done and the
        //     normal Jimmy/Nexus QSO sequencer owns it from here, OR
        //   * the target starts / continues a QSO with someone else before answering us
        //     -> BusyWithOther; the caller ceases our call and calls ReturnToWaiting().
        // While AwaitingEngagement is true the readiness machinery (silence counting, the
        // RR73 one-more-opportunity rule, SignalReady) is dormant -- we are already calling.
        public bool AwaitingEngagement { get; private set; }

        // Smart Start only: a confident LIVE decode from the target addressed to our own
        // callsign has been seen since EnterAwaitingEngagement (the target engaged us).
        public bool EngagedUs { get; private set; }

        public event Action<TargetObservation> Observed;

        // One extra appropriate receive opportunity must elapse after an ORDINARY validated RR73
        // ADDRESSED TO US before Smart Start treats the target as available -- the operator's own
        // decision (the other station may still send a courtesy 73). Cleared by any newer,
        // clearer event (target 73 / target CQ / a fresh RR73). Applies ONLY to a target -> US
        // RR73; a target -> PEER RR73 is ordinary "working another station" busy evidence and is
        // governed by the single configurable clean-silence mechanism below.
        private bool _rr73AwaitingOneMoreOpportunity;

        // N4BP live-radio audit, 2026-09-08 -- fix 1 (the confirmed silence-count bug). Set true
        // for exactly the receive period in which the TARGET'S OWN decode was ingested. A period
        // in which the target was actually heard is NOT a "not heard" opportunity, so the next
        // matching OnReceivePeriodComplete on the target's parity must not count it toward
        // SilenceCount and must not narrate "not heard N of M" for it. Cleared by that first
        // period completion after it is set (mirrors the established one-shot pattern). Any
        // over-count from a rare lagged decode is separately undone by IngestTargetDecode's
        // SilenceCount = 0.
        private bool _targetHeardThisPeriod;

        // N4BP live-radio audit, 2026-09-08 -- fix 4. The target addressed OUR callsign with a
        // mid-exchange report/reply while Smart Start was still WAITING (not yet in its calling
        // phase). That is unambiguous engagement, not merely "available": WsjtxClient answers
        // THIS exact decode immediately and hands the contact to the normal QSO sequencer,
        // rather than parking a deferred generic auto-start that can then dispatch off a newer
        // CQ from the target. One-shot: WsjtxClient consumes it via ConsumeEngagedWhileWaiting().
        private bool _engagedWhileWaiting;

        // Dedup: OnReceivePeriodComplete is called once per real slot transition, but guards
        // against being asked twice for the same slot (defensive; the real per-tick caller
        // already only calls this once per boundary).
        private ulong? _lastCountedSlot;

        // Smart Start only. How many times, for THIS armed target, a readiness turned out to be a
        // dead end -- pre-transmit revalidation declined it as busy, or a dispatched call yielded
        // before the target engaged us. A CQing DX in a pileup produces an endless "appears
        // available -> busy -> standing by" churn with no useful outcome; after MaxSmartStart
        // StandbyRounds of it WsjtxClient disarms Smart Start and tells the operator (there is no
        // lull to wait for -- a plain call + Repeat Limit is the tool for a hot pileup). Reset by
        // Start() (new target) and by a real EnterAwaitingEngagement (we actually got to call).
        private int _standbyRounds;

        private string _band;
        private string _mode;
        private string _sessionToken;

        // Fixed WSJT-X T/R period lengths, used to turn a receive-opportunity count into a
        // documented wall-clock backstop and to reason about a seed decode's age. FT8 = 15 s,
        // FT4 = 7.5 s; any other/unknown mode conservatively uses the longer FT8 period.
        private double _periodSeconds = 15.0;

        // A seed decode (operator selection) is treated as genuine current evidence only when it
        // is no older than this many T/R periods -- i.e. from the current or immediately
        // preceding receive period. Two periods is deliberately short: it is far less than the
        // time a station needs to complete a whole call -> report -> RR73 exchange with someone
        // else (3+ periods), which is exactly what made a 54 s-old RR73 invalid evidence in the
        // V51WW failure.
        private const int SeedFreshLimitPeriods = 2;

        // Work Watched Station Now is an explicit operator request, so it bypasses the Smart
        // Start silence policy -- but it still needs a reasonably current decode. One full
        // standard exchange is 3 periods (call, R+report, RR73); allow that plus one for slack.
        private const int WorkNowMaxOpportunityGap = 4;

        public TargetMonitor(TargetPurpose purpose)
        {
            Purpose = purpose;
        }

        // Fixed WSJT-X T/R period length for a mode token (FT8 15 s / FT4 7.5 s). Unknown or
        // any other value conservatively uses the longer FT8 period.
        private static double PeriodSecondsForMode(string mode) =>
            string.Equals(mode, "FT4", StringComparison.OrdinalIgnoreCase) ? 7.5 : 15.0;

        // Which alternating slot a UTC instant falls in, using the same seconds-since-midnight /
        // period alignment the engine's own monotonic slot counter uses (FT8 boundaries at
        // :00/:15/:30/:45). Only trusted for a decode already known to be recent (the seed
        // fresh-window check) -- never used to age an old decode.
        private bool ParityFromUtc(DateTime utc) =>
            ((long)(utc.TimeOfDay.TotalSeconds / _periodSeconds)) % 2 == 0;

        // The decode's own capture time when it carries a real one (Direct-mode decodes always
        // stamp RxDate+SinceMidnight at ingest), else "now" -- a live decode with no explicit
        // timestamp is by definition current.
        private static DateTime DecodeUtcOrNow(EnqueueDecodeMessage d)
        {
            if (d != null && d.RxDate > new DateTime(2000, 1, 1))
                return d.RxDate.Add(d.SinceMidnight);
            return DateTime.UtcNow;
        }

        // Shared with WsjtxClient.TryCaptureSmartStart. Is an operator-selected decode still
        // fresh enough to act on immediately -- the SAME current-or-immediately-preceding-period
        // window SeedSelectedDecode uses to decide a seed counts as genuine live evidence? A
        // selection older than this identifies WHICH station only; the target may well have moved
        // on since (KA1BMF live-radio audit, 2026-09-08: a ~50 s-old "to us" decode was selected
        // while the target had already started working another station). `mode` picks the FT8 /
        // FT4 T/R period length; `nowUtc` is the selection instant.
        public static bool IsSelectionDecodeFresh(EnqueueDecodeMessage d, string mode, DateTime nowUtc)
        {
            if (d == null) return false;
            double ageSeconds = Math.Max(0.0, (nowUtc - DecodeUtcOrNow(d)).TotalSeconds);
            return ageSeconds <= PeriodSecondsForMode(mode) * SeedFreshLimitPeriods;
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
            TransmittedCallCount = 0;
            LastUsableDecode = null;
            LastUsableDecodeUtc = default;
            HasLiveTargetEvidence = false;
            OpportunitiesSinceLiveEvidence = 0;
            BusyWithOther = false;
            ReadyToStart = false;
            AwaitingEngagement = false;
            EngagedUs = false;
            _rr73AwaitingOneMoreOpportunity = false;
            _targetHeardThisPeriod = false;
            _engagedWhileWaiting = false;
            _standbyRounds = 0;
            _lastCountedSlot = null;
            _band = band;
            _mode = mode;
            _sessionToken = sessionToken;
            _periodSeconds = PeriodSecondsForMode(mode);

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
            TransmittedCallCount = 0;
            LastUsableDecode = null;
            LastUsableDecodeUtc = default;
            HasLiveTargetEvidence = false;
            OpportunitiesSinceLiveEvidence = 0;
            BusyWithOther = false;
            ReadyToStart = false;
            AwaitingEngagement = false;
            EngagedUs = false;
            _rr73AwaitingOneMoreOpportunity = false;
            _targetHeardThisPeriod = false;
            _engagedWhileWaiting = false;
            _standbyRounds = 0;
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
            _engagedWhileWaiting = false;
        }

        // One-shot: WsjtxClient consumes this after ObserveDecode to hand a "target addressed us
        // while we were still waiting" straight to the normal QSO sequencer (see the field's
        // own comment). Both reads and clears it.
        public bool ConsumeEngagedWhileWaiting()
        {
            if (!_engagedWhileWaiting) return false;
            _engagedWhileWaiting = false;
            return true;
        }

        // Smart Start only. Called once the automatic REPLY for this target has actually been
        // dispatched (WsjtxClient.ReplyTo's success callback): Smart Start stays ARMED but its
        // own readiness machinery goes dormant -- it is now CALLING the target and just watches,
        // via ObserveDecode, for one of two outcomes: the target addresses our callsign
        // (EngagedUs -> Smart Start's job is done, the normal QSO sequencer owns it) or the
        // target starts/continues a QSO with someone else (BusyWithOther -> the caller ceases
        // our call and calls ReturnToWaiting).
        public void EnterAwaitingEngagement()
        {
            if (Purpose != TargetPurpose.SmartStart) return;
            AwaitingEngagement = true;
            EngagedUs = false;
            ReadyToStart = false;
            _rr73AwaitingOneMoreOpportunity = false;
            _engagedWhileWaiting = false;
            _standbyRounds = 0;   // we actually got to call -- the earlier dead-end rounds don't count against us
        }

        // The target started working someone else before answering us and the caller has ceased
        // our transmit attempt (the normal Escape-style halt/cancel/requeue). Drop back to the
        // armed/waiting state for the SAME target: the readiness machinery is live again, and
        // BusyWithOther is KEPT (the target IS busy). The clean-silence count restarts from zero
        // here (N4BP live-radio audit: "when that traffic stops, start the existing configurable
        // clean-silence count from zero"), so once the target has been silent for the operator's
        // configured SilenceThreshold appropriate target-parity opportunities with no fresh busy
        // evidence, BusyWithOther expires and Smart Start is ready again. TargetCall / parity /
        // HasLiveTargetEvidence / ApparentPeer / LastUsableDecode / TransmittedCallCount are all
        // retained -- this same calling effort resumes.
        public void ReturnToWaiting()
        {
            if (Purpose != TargetPurpose.SmartStart) return;
            AwaitingEngagement = false;
            EngagedUs = false;
            ReadyToStart = false;
            _rr73AwaitingOneMoreOpportunity = false;
            _engagedWhileWaiting = false;
            _targetHeardThisPeriod = false;
            SilenceCount = 0;
            _lastCountedSlot = null;
            OpportunitiesSinceLiveEvidence = 0;
        }

        // Smart Start only. The contact that a successful auto-start had handed to the normal QSO
        // sequencer (the target answered our callsign) has now been ceased because, before that
        // QSO completed, the target moved into a substantive report/R-report/RRR exchange with a
        // third station (WsjtxClient.YieldActiveContactToOtherQso). The monitor had been Stopped
        // at that hand-off, so this re-establishes it: armed and waiting on the SAME target, with
        // NO carried live evidence (the next real decode re-derives parity / busy state through
        // the normal feed), but carrying the CUMULATIVE Repeat-Limit calling-over count so the
        // operator's limit is one bounded effort across the whole capture -> call -> answer ->
        // yield -> resume cycle, never restarted. Identical to a fresh Start() except it raises
        // no WatchStarted observation (the caller narrates the yield) and keeps the call count.
        public void ResumeAfterHandoff(string call, string band, string mode, string sessionToken,
            int cumulativeCallCount, int silenceThreshold)
        {
            if (Purpose != TargetPurpose.SmartStart || string.IsNullOrEmpty(call)) return;

            TargetCall = call;
            ApparentPeer = null;
            TargetEvenParity = null;
            SilenceCount = 0;
            TransmittedCallCount = cumulativeCallCount;   // cumulative across the hand-off -- NOT reset
            LastUsableDecode = null;
            LastUsableDecodeUtc = default;
            HasLiveTargetEvidence = false;
            OpportunitiesSinceLiveEvidence = 0;
            BusyWithOther = false;
            ReadyToStart = false;
            AwaitingEngagement = false;
            EngagedUs = false;
            _rr73AwaitingOneMoreOpportunity = false;
            _targetHeardThisPeriod = false;
            _engagedWhileWaiting = false;
            _standbyRounds = 0;
            _lastCountedSlot = null;
            _band = band;
            _mode = mode;
            _sessionToken = sessionToken;
            _periodSeconds = PeriodSecondsForMode(mode);
            if (silenceThreshold > 0) SilenceThreshold = silenceThreshold;
        }

        // One ACTUAL transmitted calling over to this target just completed (fed by WsjtxClient
        // from the transmitting-just-ended edge, while this monitor is armed and AwaitingEngagement).
        // Adds to the cumulative effort total and returns true once the operator's Repeat Limit
        // is reached -- at which point WsjtxClient disarms Smart Start entirely, so no further
        // calling transmission can go out. Deliberately NOT reset by a busy yield / re-arm, so
        // the whole effort (initial call + repeats + calls after any yields) is bounded once,
        // never restarted per ReplyTo. A non-positive repeatLimit (limit disabled) never trips.
        public bool NoteCallingOverTransmitted(int repeatLimit)
        {
            if (Purpose != TargetPurpose.SmartStart) return false;
            TransmittedCallCount++;
            return repeatLimit > 0 && TransmittedCallCount >= repeatLimit;
        }

        // One readiness for this armed target turned out to be a dead end (revalidation declined
        // it as busy, or a dispatched call yielded before engagement). Returns true once that has
        // happened `maxRounds` times with no real call in between -- the caller then disarms Smart
        // Start (a CQing pileup DX has no lull to wait for). Reset by Start() / EnterAwaitingEngagement.
        public bool NoteStandbyRoundAndCheckGiveUp(int maxRounds)
        {
            if (Purpose != TargetPurpose.SmartStart) return false;
            _standbyRounds++;
            return maxRounds > 0 && _standbyRounds >= maxRounds;
        }

        // Smart Start capture (WsjtxClient.TryCaptureSmartStart): the operator selected this
        // exact decode from a list/queue. It always identifies the target and is stored as the
        // decode to eventually hand to ReplyTo -- but it only counts as genuine current evidence
        // (parity, live-evidence flag, CQ/73/RR73 readiness rules) when it is still fresh. A
        // stale selection identifies WHICH station to work and nothing more; Smart Start then
        // behaves exactly as if freshly armed with no prior evidence, waiting for a real live
        // decode before it can authorize a transmission. `nowUtc` is the capture instant.
        public void SeedSelectedDecode(EnqueueDecodeMessage d, DateTime nowUtc, string myCall)
        {
            if (TargetCall == null || d == null || string.IsNullOrEmpty(d.Message)) return;

            string de = d.DeCall();
            if (de == null || !string.Equals(de, TargetCall, StringComparison.OrdinalIgnoreCase)) return;

            DateTime decodeUtc = DecodeUtcOrNow(d);
            double ageSeconds = Math.Max(0.0, (nowUtc - decodeUtc).TotalSeconds);

            // Always: remember it as the decode to reply from, and its real age.
            LastUsableDecode = d;
            LastUsableDecodeUtc = decodeUtc;

            if (ageSeconds > _periodSeconds * SeedFreshLimitPeriods)
            {
                // Stale selection -- context only. No parity (so no opportunity can count until a
                // real live decode establishes it), no live-evidence, no readiness. Narrate what
                // was selected so the feature still reports "watching X".
                RaiseSeedClassification(d, myCall);
                return;
            }

            // Fresh selection -- genuine current evidence. Feed it through the same
            // classification/readiness path a live decode would take, with the decode's own real
            // parity rather than a guess.
            IngestTargetDecode(d, ParityFromUtc(decodeUtc), myCall, live: true, decodeUtc: decodeUtc);
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

            // Nexus modernization Stage 7a: the message-type + recipient facts this method
            // classifies on come from EffectiveSemantic (Nexus's own parse when the cutover is
            // on). Stage 5 proved To / IsCq / IsReport / IsRReport / IsRrr / IsRr73 / Is73
            // byte-identical to the WsjtxMessage equivalents across the corpus. The observation
            // Value strings (Payload) and IsFoxHound stay on WsjtxMessage / the DTO -- narration
            // formatting is migrated separately (Stage 7b), and F/H is a heuristic.
            var sem = d.EffectiveSemantic(myCall);

            if (!string.Equals(de, TargetCall, StringComparison.OrdinalIgnoreCase))
            {
                // Smart Start's decision model (operator policy, 2026-09-08) is driven ONLY by
                // the TARGET's own decoded transmissions. A third station calling or rogering
                // the target is NOT evidence the target is working anyone -- a pileup calling a
                // CQing DX is the normal "keep trying" case (rule 1). So a non-target decode is
                // ignored outright for a Smart Start monitor. Station Watch keeps its fuller
                // "being called by / the peer's own reply" reading unchanged (it never
                // auto-transmits; Work Now revalidation still reads BusyWithOther).
                if (Purpose == TargetPurpose.SmartStart) return;

                // Not the target -- interesting in two ways:
                //   1. the target's OWN apparent peer replying TO the target (narrate it, never
                //      invent the unseen half of the exchange), and
                //   2. ANY station addressing the target (2.0.64): someone is calling or working
                //      the target right now, so the target is busy. This is a transmit-safety
                //      signal only (no narration for an unknown caller) and clears again the
                //      moment the target itself is heard available.
                string toTarget = sem.To;   // Stage 7a
                bool addressedToTarget = !string.IsNullOrEmpty(toTarget)
                    && string.Equals(toTarget, TargetCall, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(de, myCall, StringComparison.OrdinalIgnoreCase);

                if (addressedToTarget)
                {
                    // Someone is calling / working the target right now -- fresh busy evidence.
                    // It restarts the clean-silence count from zero (fresh busy evidence always
                    // wins over a pending "target has gone quiet" decision).
                    BusyWithOther = true;
                    SilenceCount = 0;
                    _lastCountedSlot = null;
                }

                if (addressedToTarget
                    && !string.IsNullOrEmpty(ApparentPeer)
                    && string.Equals(de, ApparentPeer, StringComparison.OrdinalIgnoreCase))
                {
                    // Only what was actually decoded from the PEER's own transmission -- never
                    // the unseen half of the target's own exchange. Value carries the peer's own
                    // payload (report/RRR/RR73/73/grid) so the glue can word it naturally, e.g.
                    // "W1ABC R minus 5." from a decoded "W1ABC K4YT R-05".
                    Raise(TargetObservationKind.OtherPartyObserved, TargetCall, de, WsjtxMessage.Payload(d.Message), d.Message);
                }
                return;
            }

            IngestTargetDecode(d, evenSlot, myCall, live: true, decodeUtc: DecodeUtcOrNow(d));
        }

        // Shared body for a confident decode attributed to the target, whether it arrived on the
        // live feed (ObserveDecode) or as a still-fresh operator selection (SeedSelectedDecode).
        private void IngestTargetDecode(EnqueueDecodeMessage d, bool evenSlot, string myCall, bool live, DateTime decodeUtc)
        {
            // Stage 7a: classify on EffectiveSemantic (see ObserveDecode's note). Payload
            // strings + IsFoxHound stay as-is.
            var sem = d.EffectiveSemantic(myCall);

            // Any confidently attributed decode from the target -- including an ambiguous one --
            // resets the silence count and records the freshest usable decode/parity. It also
            // marks this receive period as one in which the target WAS heard, so its own
            // period-complete does not falsely count it as a "not heard" opportunity (N4BP live
            // audit -- fix 1).
            SilenceCount = 0;
            _lastCountedSlot = null;
            _targetHeardThisPeriod = true;
            TargetEvenParity = evenSlot;
            LastUsableDecode = d;
            LastUsableDecodeUtc = decodeUtc;
            if (live)
            {
                HasLiveTargetEvidence = true;
                OpportunitiesSinceLiveEvidence = 0;
            }

            if (sem.IsCq)
            {
                ApparentPeer = null;
                BusyWithOther = false;                 // calling CQ -> available
                _rr73AwaitingOneMoreOpportunity = false;
                Raise(TargetObservationKind.TargetCq, TargetCall, null, null, d.Message);
                if (Purpose == TargetPurpose.SmartStart) SignalReady();
                return;
            }

            string to = sem.To;
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
            string payload = WsjtxMessage.Payload(d.Message);   // "-15" / "R-15" / "RRR" / "RR73" / "73" -- narration only

            if (live && addressingUs)
                EngagedUs = true;                      // the target answered our callsign

            if (addressingUs)
            {
                BusyWithOther = false;                 // turning to us -> not busy with anyone else

                // Smart Start, still armed/waiting (not yet CALLING): the target is sending US a
                // mid-exchange report/reply. That is unambiguous ENGAGEMENT, not merely
                // "available" -- WsjtxClient answers THIS exact decode now and hands the contact
                // to the normal QSO sequencer (like Enter on a to-us decode), instead of parking
                // a deferred generic auto-start that can dispatch off a newer CQ from the target
                // (N4BP live, 2026-09-08). A target -> US 73 / RR73 keeps its own rules below.
                if (live && Purpose == TargetPurpose.SmartStart && !AwaitingEngagement
                    && !sem.Is73 && !sem.IsRr73)
                {
                    _engagedWhileWaiting = true;
                    Raise(TargetObservationKind.TargetAddressingUs, TargetCall, peer, payload, d.Message);
                    return;
                }
            }

            // ── Target -> US : 73 / RR73 keep their existing special handling ──────────────────
            if (addressingUs && sem.IsRr73)
            {
                // The target rogered OUR callsign -- becoming available; the deliberate
                // one-extra-opportunity rule for RR73-to-us is unchanged.
                _rr73AwaitingOneMoreOpportunity = Purpose == TargetPurpose.SmartStart && !d.IsFoxHound();
                Raise(TargetObservationKind.TargetRr73, TargetCall, peer, payload, d.Message);
                return;
            }
            if (addressingUs && sem.Is73)
            {
                _rr73AwaitingOneMoreOpportunity = false;
                Raise(TargetObservationKind.Target73, TargetCall, peer, payload, d.Message);
                if (Purpose == TargetPurpose.SmartStart) SignalReady();
                return;
            }
            if (addressingUs)
            {
                // Reaches here only during the AwaitingEngagement phase (a report/reply to us
                // while we are already calling), or for a Station Watch instance. EngagedUs is
                // already set above; ServiceSmartStartAwaitingEngagement owns the hand-off. Raise
                // the SPECIFIC decoded kind so Station Watch narration keeps the detail it always
                // had (report / R-report vs a bare directed message).
                if (sem.IsRReport) { Raise(TargetObservationKind.TargetRReport, TargetCall, peer, payload, d.Message); return; }
                if (sem.IsReport)  { Raise(TargetObservationKind.TargetReport, TargetCall, peer, payload, d.Message); return; }
                Raise(TargetObservationKind.TargetAddressingUs, TargetCall, peer, payload, d.Message);
                return;
            }

            // ── Target -> PEER ────────────────────────────────────────────────────────────────
            // Operator policy (2026-09-08): a target -> peer RR73 or 73 means the target's
            // previous QSO is CLOSING -- a positive call opportunity, not busy evidence. For
            // BOTH purposes it clears BusyWithOther, and for Smart Start it signals ready right
            // away (rules 2 & 3: call at the next legitimate transmit opportunity, with NO extra
            // silence periods). Every OTHER kind of target -> peer traffic -- report, R-report,
            // RRR, or any other directed payload -- means the target is actively working someone
            // else: for Smart Start that sets BusyWithOther and restarts the ONE configurable
            // clean-silence count from zero (rules 4 & 5). The count then becomes available
            // again after SilenceThreshold clean target-parity opportunities (rule 6), or the
            // moment the target sends a CQ / RR73 / 73 / addresses us. Station Watch / Work Now
            // keep their existing "RR73/73 to a peer = finishing" reading (they never auto-key).
            if (sem.IsRr73 || sem.Is73)
            {
                BusyWithOther = false;
                _rr73AwaitingOneMoreOpportunity = false;
                if (sem.IsRr73) Raise(TargetObservationKind.TargetRr73, TargetCall, peer, payload, d.Message);
                else            Raise(TargetObservationKind.Target73, TargetCall, peer, payload, d.Message);
                if (Purpose == TargetPurpose.SmartStart) SignalReady();
                return;
            }

            // report / R-report / RRR / any other directed payload to a peer -> working someone.
            BusyWithOther = true;
            _rr73AwaitingOneMoreOpportunity = false;
            SilenceCount = 0;
            _lastCountedSlot = null;

            if (sem.IsRrr)       { Raise(TargetObservationKind.TargetRrr, TargetCall, peer, payload, d.Message); return; }
            if (sem.IsRReport)   { Raise(TargetObservationKind.TargetRReport, TargetCall, peer, payload, d.Message); return; }
            if (sem.IsReport)    { Raise(TargetObservationKind.TargetReport, TargetCall, peer, payload, d.Message); return; }
            if (addressingUs)    { Raise(TargetObservationKind.TargetAddressingUs, TargetCall, peer, payload, d.Message); return; }
            Raise(TargetObservationKind.TargetAddressingOther, TargetCall, peer, null, d.Message);
        }

        // Narrate a stale seed selection without letting it drive any readiness/parity/evidence
        // state -- classification only, so Station Watch style callers still report "watching X".
        private void RaiseSeedClassification(EnqueueDecodeMessage d, string myCall)
        {
            var sem = d.EffectiveSemantic(myCall);   // Stage 7a (Payload strings stay as-is)
            if (sem.IsCq) { Raise(TargetObservationKind.TargetCq, TargetCall, null, null, d.Message); return; }
            string to = sem.To;
            if (string.IsNullOrEmpty(to)) { Raise(TargetObservationKind.TargetAmbiguous, TargetCall, null, null, d.Message); return; }
            bool addressingUs = !string.IsNullOrEmpty(myCall) && string.Equals(to, myCall, StringComparison.OrdinalIgnoreCase);
            string peer = addressingUs ? myCall : to;
            if (sem.IsRr73) Raise(TargetObservationKind.TargetRr73, TargetCall, peer, null, d.Message);
            else if (sem.Is73) Raise(TargetObservationKind.Target73, TargetCall, peer, null, d.Message);
            else if (sem.IsRrr) Raise(TargetObservationKind.TargetRrr, TargetCall, peer, null, d.Message);
            else if (sem.IsRReport) Raise(TargetObservationKind.TargetRReport, TargetCall, peer, WsjtxMessage.Payload(d.Message), d.Message);
            else if (sem.IsReport) Raise(TargetObservationKind.TargetReport, TargetCall, peer, WsjtxMessage.Payload(d.Message), d.Message);
            else if (addressingUs) Raise(TargetObservationKind.TargetAddressingUs, TargetCall, peer, null, d.Message);
            else Raise(TargetObservationKind.TargetAddressingOther, TargetCall, peer, null, d.Message);
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
            if (weTransmittedThisSlot) return;
            if (!string.Equals(band, _band, StringComparison.OrdinalIgnoreCase)) return;
            if (!string.Equals(mode, _mode, StringComparison.OrdinalIgnoreCase)) return;
            if (!string.Equals(sessionToken, _sessionToken, StringComparison.Ordinal)) return;
            if (TargetEvenParity == null) return;               // parity not yet established -- never counted
            if (evenSlot != TargetEvenParity.Value) return;     // opposite parity -- never counted
            if (_lastCountedSlot == slot) return;                // defensive dedup
            _lastCountedSlot = slot;

            // Both purposes track how long it has been (in real appropriate opportunities) since
            // the target was actually heard -- Work Watched Station Now's freshness check reads
            // this too, not just Smart Start's silence counter.
            OpportunitiesSinceLiveEvidence++;

            if (Purpose != TargetPurpose.SmartStart) return;   // Station Watch never counts toward an automatic start
            if (AwaitingEngagement) return;                    // already calling -- silence/RR73 readiness is dormant

            // Fix 1 (N4BP live audit): a period in which the target itself was actually heard is
            // NOT a "not heard" opportunity. Do not count it toward the clean-silence total and
            // do not narrate "not heard N of M" for it. One-shot, cleared here.
            if (_targetHeardThisPeriod)
            {
                _targetHeardThisPeriod = false;
                return;
            }

            // The deliberate one-extra-opportunity rule for a target -> US RR73 (the target
            // rogered our callsign): one clean opportunity later, it is available.
            if (_rr73AwaitingOneMoreOpportunity)
            {
                _rr73AwaitingOneMoreOpportunity = false;
                SignalReady();
                return;
            }

            // The ONE configurable clean-silence mechanism (fix 2). SilenceThreshold is the
            // operator's Smart QSO Start silence-period setting. Each genuinely clean appropriate
            // target-parity opportunity (target not heard this period) counts one, and is
            // narrated "N of M" -- including the FINAL period, so the operator hears "not heard,
            // 2 of 2." right before the call (operator policy, 2026-09-08, rule 6). When the
            // count reaches the threshold the target's earlier "working another station" traffic
            // has now been silent for the operator's full window, so clear BusyWithOther and
            // signal ready. Fresh target traffic (IngestTargetDecode) re-zeros SilenceCount.
            SilenceCount++;
            Raise(TargetObservationKind.SmartStartWaiting, TargetCall, null, $"{SilenceCount} of {SilenceThreshold}");
            if (SilenceCount >= SilenceThreshold)
            {
                BusyWithOther = false;
                SignalReady();
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
            _engagedWhileWaiting = false;
            _targetHeardThisPeriod = false;
            _lastCountedSlot = null;
        }

        // The atomic pre-transmit revalidation. Called immediately before an automatic REPLY (or
        // a Work-Watched-Station-Now dispatch) actually goes out, against the CURRENT band/mode/
        // session -- see WsjtxClient.RequestTargetMonitorStart. `operatorOverride` is true for
        // Work Now: it skips the Smart Start silence-policy bound but still requires live, in-
        // context, non-busy, reasonably current evidence.
        public AutoStartCheck RevalidateForAutoStart(DateTime nowUtc, string band, string mode,
            string sessionToken, bool operatorOverride)
        {
            if (TargetCall == null || LastUsableDecode == null) return AutoStartCheck.NoUsableDecode;

            if (!string.Equals(band, _band, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(mode, _mode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(sessionToken, _sessionToken, StringComparison.Ordinal))
                return AutoStartCheck.ContextChanged;

            if (!HasLiveTargetEvidence) return AutoStartCheck.NoLiveEvidence;
            if (BusyWithOther) return AutoStartCheck.TargetBusy;

            // Primary freshness measure: completed appropriate receive opportunities since the
            // target was last actually heard. An automatic start is legitimately reached exactly
            // SilenceThreshold clean opportunities after the last live decode (N4BP live audit --
            // fix 2), plus the period that decode was heard in (+1) and the snapshot-finality
            // deferral's slot advances in WsjtxClient (+2), plus 1 for jitter -- so the ceiling
            // is SilenceThreshold + 4. Beyond that the poll/decode feed almost certainly stalled
            // and the wall-clock backstop below is the real guard. Work Now allows one full
            // standard exchange plus slack.
            int maxGap = operatorOverride
                ? WorkNowMaxOpportunityGap
                : Math.Max(SilenceThreshold, 1) + 4;
            if (OpportunitiesSinceLiveEvidence > maxGap) return AutoStartCheck.StaleEvidence;

            // Wall-clock backstop, derived (not a magic number): each counted opportunity is one
            // T/R period of real elapsed time; a decode is delivered near the end of its own
            // period (+1) and the automatic REPLY is held one extra poll/period for snapshot
            // finality (+1). If the wall clock shows materially more time than that, the poll /
            // decode feed stalled (host sleep, CAT loss, GC pause) and we cannot claim to have
            // observed every intervening opportunity -- so the "silence" is untrusted.
            double maxAgeSeconds = (OpportunitiesSinceLiveEvidence + 2) * _periodSeconds;
            if ((nowUtc - LastUsableDecodeUtc).TotalSeconds > maxAgeSeconds) return AutoStartCheck.StaleEvidence;

            return AutoStartCheck.Ok;
        }

        private void SignalReady()
        {
            // Guardrails independent of the RR73/silence rule that got us here: never signal
            // ready without genuine live evidence, never while current evidence says the target
            // is working someone else, and never while we are already calling it.
            if (!HasLiveTargetEvidence) return;
            if (BusyWithOther) return;
            if (AwaitingEngagement) return;
            ReadyToStart = true;
            Raise(TargetObservationKind.SmartStartTargetAvailable, TargetCall);
        }

        private void Raise(TargetObservationKind kind, string target, string peer = null, string value = null, string raw = null)
        {
            Observed?.Invoke(new TargetObservation(kind, Purpose, target, peer, value, raw));
        }
    }
}
