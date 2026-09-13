using System;
using System.Collections.Generic;
using System.Linq;

namespace WSJTX_Controller
{
    // The single authority for WHEN something Jimmy has to say is actually spoken (nudged to the
    // screen reader). Both speech sources feed through here:
    //
    //   * typed NotificationCenter events  -> SubmitNotification(...)  (NotificationCenter.Deliver)
    //   * routine RX / TX / QSO status     -> SubmitRoutineStatus(...) (WsjtxClient.ShowStatus,
    //                                          after Controller.RenderStatusVisible sets the line)
    //
    // What this does NOT touch: the VISIBLE status text/colours and the Notification History
    // entry. Those are updated immediately and unconditionally by their own call sites BEFORE
    // anything reaches here -- a deferred or suppressed *utterance* never hides or delays the
    // on-screen fact. This class only decides whether/when to fire the screen-reader nudge, and
    // (2026-09-11 speech-batching fix) how several near-simultaneous facts about the SAME
    // operating cycle get JOINED into one utterance rather than firing one nudge each.
    //
    // ── Notification joining (2026-09-11) ──────────────────────────────────────────────────────
    // Root cause this fixes: notifications produced from the same decode-processing burst (Smart
    // Start / Station Watch narration) used to speak immediately and independently the instant
    // each passed policy -- two of them 30-40ms apart produced two separate screen-reader nudges,
    // and JAWS/NVDA interrupt speech in progress when the accessible text changes again, so the
    // first was cut off. The fix: a joinable Now-priced notification (see
    // NotificationCenter.WatchEventTypes) is collected into a short-lived Now-batch instead of
    // being spoken inline; the batch is resolved (deduped, superseded, composed) and spoken as ONE
    // utterance, and -- see ReconcileAndFlush -- an ordinary lifecycle boundary (a receive cycle
    // completing, a transmission starting/ending, a QSO ending) merges that Now-batch with
    // whatever ELSE is eligible on that same boundary (the routine RX/TX/QSO status line, and any
    // AfterRx-timed notification) into that SAME one utterance, rather than either speaking twice
    // or silently dropping the Now-batch's facts.
    //
    // Two independent correlation groups (see SmartStartGroup / ISmartStartCorrelatedEvent):
    // Posture (Jimmy's own current relationship to a target: armed/waiting/calling/yielded/
    // engaged) and Observation (what was most recently decoded the TARGET doing: busy with
    // someone else / appears available). Members of the SAME group, for the SAME (Target,
    // ArmGeneration), supersede each other -- the one with the highest AdvanceStateSeq (a
    // monotonic sequence stamped at the moment each was raised, NOT a fixed tier -- TargetMonitor.
    // ReturnToWaiting() proves the state machine can legitimately cycle Yielded -> Waiting within
    // one ArmGeneration, so only recency, not a tier ladder, is correct) is kept; the rest are
    // discarded from speech (never spoken, onSpoken never invoked, exactly like any other
    // discarded occurrence). A Posture survivor and an Observation survivor for the SAME target
    // are NEVER compared against each other -- both join into the final utterance (e.g. "Waiting
    // to work EA6Y. EA6Y to KX4I, R minus 14." -- these are complementary facts, not competing
    // ones). Two different Targets never interact. Station Watch's four observation-shaped types
    // are outside both groups and always join, never superseded.
    //
    // Timing: a joinable item is added to an OPEN batch; a debounce timer (QuietPeriodMs) resets
    // on every new arrival but is capped by a hard ceiling (MaxBatchWindowMs) measured from the
    // batch's first item, so a sustained decode-processing burst can never push the first fact out
    // indefinitely. When that timer fires, the batch's membership is RESOLVED (deduped/superseded)
    // and FROZEN; a frozen batch only ever waits on MinSequentialGapMs since the last utterance of
    // ANY kind before it actually speaks (see TrySpeakFrozen) -- this is the ONLY thing in this
    // whole design that is ever deferred once its content is decided.
    //
    // Arbitration (Critical / Important / ordinary Immediate vs. a pending batch) -- see
    // SubmitNotification's Critical branch and HandleCriticalSubmission's own comment for the
    // full reasoning. Summary: a Critical event whose type INVALIDATES pending operational facts
    // (ConnectionLost, RadioCatLost, ErrorWarning-as-Critical -- default-safe: any type not
    // explicitly added to NonInvalidatingCriticalTypes) discards the pending batch (never spoken,
    // still in Notification History) and speaks alone; a Critical event that does NOT invalidate
    // anything (mechanism only -- NonInvalidatingCriticalTypes is empty today) MERGES with the
    // pending batch into one utterance, itself sorted first. Important and every ordinary
    // Immediate (direct-operator-feedback) message DEFER: they speak immediately, unmodified, and
    // never touch a pending batch at all -- the batch's own gap-check (against
    // _lastSpeechElapsedMs, which every real utterance updates) is what keeps its eventual,
    // separate flush from landing on the tail of what was just said. There is NO zero-gap
    // "flush the batch, then speak the new thing" path anywhere in this design.
    //
    // Time source: deliberately IMonotonicClock (Environment.TickCount64 in production), never
    // DateTime.UtcNow -- a wall-clock read can jump (NTP step, DST, an operator changing the
    // clock) and would corrupt every elapsed-time comparison this class makes. The scheduler
    // (INowBatchScheduler) and the clock are both injected and REQUIRED (no default) so a future
    // production caller cannot silently construct a working-looking coordinator with batching
    // quietly disabled.
    public sealed class RoutineFragment
    {
        public string Key;                 // "_base", "ReceiveCycleSummary", ...
        public int Order;                  // canonical position in the composed line
        public string Text;                // this clause's contribution (may be "")
        public SpeakWhen When;
        public SpeakCondition Condition;
        // true for one-shot transition clauses (QSO started/logged, transmit message): a held
        // fragment survives renders where the clause is simply absent, so a far delivery
        // boundary still gets it. false for "current state" clauses (base, receive summary,
        // received reply, no-decode): a held copy is dropped the moment a render omits it, so a
        // stale snapshot is never spoken.
        public bool Sticky;
    }

    // Which of two INDEPENDENT correlation groups a Smart Start lifecycle event belongs to -- see
    // this file's own header comment and NotificationEvents.cs's ISmartStartCorrelatedEvent.
    // (Declared here, not in NotificationEvents.cs, would create a needless split; kept in
    // NotificationEvents.cs alongside the interface that carries it -- see that file.)

    // Carries a joinable notification's Smart Start correlation data from NotificationCenter.
    // Deliver (which has the real ISmartStartCorrelatedEvent) into SpeechCoordinator.
    // SubmitNotification, which must not itself depend on NotificationEvents.cs's event classes.
    public sealed class SmartStartCorrelation
    {
        public string Target;
        public int ArmGeneration;
        public int StateSeq;
        public SmartStartGroup Group;
    }

    public sealed class SpeechCoordinator
    {
        // The actual delivery seam -- (text, cue) exactly as INotificationDelivery.Announce
        // expects. The cue tells the accessible-alert layer whether this is an Important or
        // Critical notification (routine RX/TX/QSO status is always AlertCue.None).
        private readonly Action<string, AlertCue> _speak;
        private readonly INowBatchScheduler _scheduler;
        private readonly IMonotonicClock _clock;

        // Notification-joining timing constants (2026-09-11). Starting points for tuning after a
        // real JAWS/NVDA listening pass, not derived or guaranteed values -- see the design
        // writeup's own repeated caveat on this. QuietPeriodMs/MaxBatchWindowMs together replace a
        // bare fixed window: a new arrival resets the short quiet timer but never the hard
        // ceiling, so a tightly-packed burst gets a chance to fully settle into one utterance
        // while a sustained burst still cannot delay the first fact indefinitely.
        // MinSequentialGapMs is the ONLY thing ever deferred, and only for a SCHEDULED batch
        // flush landing too soon after any other utterance -- it is explicitly NOT a claim that
        // two independent Immediate-lane messages will never land close together (the application
        // has no way to know when JAWS/NVDA finishes speaking; see this file's header comment).
        //
        // 2026-09-11 semantic-boundary correction (KF0VZS live-radio audit): the ORIGINAL 40/120ms
        // defaults were proven too narrow -- a real Smart Start busy/yield/not-heard cluster
        // spanned 232ms in one decode-processing pass, so each fact resolved and spoke in its own
        // separate micro-batch before the next sibling fact was even published. Two changes:
        //   (a) new defaults (75/500/150), operator-tunable via hidden, INI-only,
        //       NotificationSettings.NotificationJoin{Quiet,Max,Gap}Ms (no Options UI -- see
        //       WsjtxClient.cs's startup wiring for the effective-value diagnostic line);
        //   (b) MaxBatchWindowMs is now a pure SAFETY FALLBACK behind the new semantic
        //       OnDecodePassComplete() signal (see that method's own comment) -- it should only
        //       ever fire for content NOT produced during a decode pass (an operator-initiated
        //       Smart Start capture) or if that signal is delayed/skipped, not as the routine way
        //       one decode pass's narration gets divided into two utterances.
        // These are instance fields, not compile-time constants, so NotificationSettings can hand
        // in the operator's (validated) INI values via UpdateJoinTiming -- but they are set ONCE,
        // at startup, with no live-reload path; a changed INI value takes effect only after Jimmy
        // Next is restarted. The Default*/Min*/Max* names below are also NotificationSettings'
        // own property-initializer defaults and validation range anchors -- kept here, the single
        // source of truth for "what does this timing actually mean", not duplicated as bare
        // numbers in NotificationSettings.cs.
        internal const int DefaultQuietPeriodMs = 75;
        internal const int DefaultMaxBatchWindowMs = 500;
        internal const int DefaultMinSequentialGapMs = 150;

        private int _quietPeriodMs = DefaultQuietPeriodMs;
        private int _maxBatchWindowMs = DefaultMaxBatchWindowMs;
        private int _minSequentialGapMs = DefaultMinSequentialGapMs;

        internal int QuietPeriodMs => _quietPeriodMs;
        internal int MaxBatchWindowMs => _maxBatchWindowMs;
        internal int MinSequentialGapMs => _minSequentialGapMs;

        // Called once, at startup (WsjtxClient.cs), with NotificationSettings' already-validated
        // values. Never called again -- see this class's own restart-required comment above.
        public void UpdateJoinTiming(int quietMs, int maxMs, int gapMs)
        {
            _quietPeriodMs = quietMs;
            _maxBatchWindowMs = maxMs;
            _minSequentialGapMs = gapMs;
        }

        // A Critical event whose EventType is NOT in this set is treated as INVALIDATING pending
        // Now-batch speech by default (discard, never spoken, still in History) -- the safe
        // default, since a Critical delivery's real-world source (ErrorWarning's Source is
        // open-ended by design) cannot generically be known to be unrelated to a Smart Start/
        // Station Watch target's operational status. Empty today: no live Critical type has been
        // judged non-invalidating (ConnectionLost/RadioCatLost both genuinely do invalidate --
        // if the engine or radio is unreachable, nothing pending about calling/watching a target
        // is still operationally true). A future Critical type can opt into the MERGE behavior by
        // being added here deliberately -- never inferred.
        internal static readonly HashSet<NotificationEventType> NonInvalidatingCriticalTypes =
            new HashSet<NotificationEventType>();

        private bool _physicallyTransmitting;
        private bool _qsoActive;

        // Station Watch (2.0.63): while true, every submission NOT tagged as Watch-category
        // speech and NOT Critical priority is discarded outright (never held, never spoken) --
        // "suppress routine automatic Jimmy speech while a receive-only watch is active, but let
        // watch-specific speech and safety-critical notices through". Turning this ON also drops
        // (does not hold) anything already pending, so old routine speech can never spill out the
        // moment normal QSO handoff succeeds and suppression is lifted -- see SetStationWatchSuppression.
        private bool _stationWatchSuppressingRoutine;
        internal bool StationWatchSuppressingRoutine => _stationWatchSuppressingRoutine;

        // Held routine-status fragments, keyed by fragment Key so a re-render coalesces per
        // clause. A fragment lives here only while waiting for a future delivery boundary.
        private readonly Dictionary<string, Pending> _pendingRoutine = new Dictionary<string, Pending>();

        // Held notifications, keyed by identity so a re-publish coalesces.
        private readonly Dictionary<string, Pending> _pendingNotifications = new Dictionary<string, Pending>();

        private sealed class Pending
        {
            public string Text;
            public AlertCue Cue;
            public SpeakWhen When;
            public SpeakCondition Condition;
            public bool RenderedWhileTx;   // routine only -- the physical TX state at submit time
            public int Order;              // routine only -- composition order
            public bool Sticky;            // routine only -- survive an absent render
            public bool IsBase;            // routine only -- a "_base.*" structural skeleton span
            public NotificationEventType EventType;   // notifications only -- for reconciliation

            // Notifications only. True for a Station Watch/Smart Start event -- bypasses the
            // Station-Watch routine-suppression gate the same way Critical bypasses the QSO gate.
            public bool IsWatchCategory;

            // Notifications only. Invoked the moment this notification is ACTUALLY spoken
            // (immediate or via a flush) -- never when it is discarded (Never / QSO-suppressed).
            public Action OnSpoken;
        }

        // A joinable Now item waiting in the OPEN batch -- see AddToOpenBatch/OnOpenBatchTimerFired.
        private sealed class NowBatchItem
        {
            public NotificationEventType EventType;
            public string DedupKey;
            public string Text;
            public AlertCue Cue;
            public Action OnSpoken;
            public SmartStartCorrelation Correlation;   // null for Station Watch's 4 non-correlated types
        }

        private sealed class ResolvedItem
        {
            public NotificationEventType Category;
            public string Text;
            public readonly List<Action> OnSpokenActions = new List<Action>();
        }

        // ── Now-batch state (2026-09-11) ────────────────────────────────────────────────────────
        // OPEN: still accepting new joinable arrivals; membership is mutable until its own
        // debounce/ceiling timer fires. FROZEN: membership already resolved (deduped/superseded)
        // and fixed; only waiting on MinSequentialGapMs since the last utterance before it
        // actually speaks. The two are deliberately separate collections/tokens: a NEW open batch
        // can start accumulating while an OLDER, already-resolved frozen batch is still waiting
        // out a post-urgent gap -- collapsing them into one would either lose the "membership is
        // fixed" guarantee for the frozen one or block new arrivals from ever being collected
        // while an unrelated gap-wait is in progress.
        private readonly List<NowBatchItem> _openBatch = new List<NowBatchItem>();
        private object _openToken;
        private long _openFirstArrivalMs;

        private List<(NotificationEventType category, string text)> _frozenParts;
        private List<Action> _frozenOnSpoken;
        private object _frozenToken;
        private long _frozenBatchId;   // diagnostics only -- which batch (or merge of batches) this frozen content traces to

        // Stamped by SpeakNow on every REAL utterance (Immediate or Joinable lane alike) -- the
        // one piece of state MinSequentialGapMs checks. Never DateTime.UtcNow -- see IMonotonicClock.
        private long? _lastSpeechElapsedMs;

        // The operator-configured order joinable categories compose in -- see
        // NotificationSettings.NotificationJoinOrder / NotificationJoinOrderDlg. Empty until
        // UpdateJoinOrder is called (production wiring does this right after construction); an
        // unlisted category sorts to the end, stable on arrival order.
        private IReadOnlyList<NotificationEventType> _joinOrder = Array.Empty<NotificationEventType>();

        // Batch lifecycle diagnostics (2026-09-11) -- optional; production wiring (WsjtxClient.cs)
        // passes DebugOutput so a support log can show batch id, open time, every item added,
        // which trigger closed it (semantic decode-pass signal vs. quiet-timer vs. max-ceiling
        // fallback) and when, resolution decisions (survivors vs. dropped as duplicate/
        // superseded), the final composed text, and the nudge count. Defaults to a no-op so every
        // existing/test construction site is unaffected.
        private readonly Action<string> _logDiagnostic;
        private long _nextBatchId;
        private long _openBatchId;

        public SpeechCoordinator(Action<string, AlertCue> speak, INowBatchScheduler scheduler, IMonotonicClock clock,
            Action<string> logDiagnostic = null)
        {
            _speak = speak ?? throw new ArgumentNullException(nameof(speak));
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _logDiagnostic = logDiagnostic ?? (_ => { });
        }

        public void UpdateJoinOrder(IReadOnlyList<NotificationEventType> order) =>
            _joinOrder = order ?? Array.Empty<NotificationEventType>();

        private static AlertCue CueFor(NotificationPriority priority) =>
            priority == NotificationPriority.Critical ? AlertCue.Critical
            : priority == NotificationPriority.Important ? AlertCue.Important
            : AlertCue.None;

        // Test / diagnostic visibility.
        internal bool PhysicallyTransmitting => _physicallyTransmitting;
        internal bool QsoActive => _qsoActive;
        internal bool HasPendingRoutine => _pendingRoutine.Count > 0;
        internal int PendingRoutineCount => _pendingRoutine.Count;
        internal int PendingNotificationCount => _pendingNotifications.Count;
        internal int OpenBatchCount => _openBatch.Count;
        internal bool HasFrozenBatch => _frozenParts != null;

        // ── The one delivery primitive ───────────────────────────────────────────────────────────
        // Every real utterance -- routine, notification, joined batch, reconciled boundary,
        // Critical, Important -- goes through this ONE method. It is what lets MinSequentialGapMs
        // be checked against a single, always-current timestamp regardless of which caller most
        // recently spoke.
        private void SpeakNow(string text, AlertCue cue)
        {
            _speak(text, cue);
            _lastSpeechElapsedMs = _clock.ElapsedMilliseconds;
        }

        // ── Submission ────────────────────────────────────────────────────────────────────────

        // Returns true when the notification was spoken now, joined into a batch, or queued for a
        // later boundary; false when it was discarded outright (Never, or not eligible for the
        // current QSO state and not deferrable into one). `onSpoken`, when supplied, fires exactly
        // once at the moment of real speech -- immediately here, later from a flush, or as part of
        // a joined/reconciled utterance -- and never fires for a discarded occurrence.
        //
        // `eventType`/`correlation` are new (2026-09-11); both are optional so every existing call
        // site (including every test that predates the joining feature) keeps compiling and
        // behaving exactly as before -- they only matter when `isWatchCategory` is true.
        public bool SubmitNotification(string identity, string text,
            SpeakWhen when, NotificationPriority priority,
            SpeakCondition condition = SpeakCondition.Always, Action onSpoken = null,
            bool isWatchCategory = false,
            NotificationEventType eventType = NotificationEventType.ErrorWarning,
            SmartStartCorrelation correlation = null)
        {
            if (string.IsNullOrEmpty(text)) return false;

            AlertCue cue = CueFor(priority);

            // (1) An explicit "never spoken" wins over everything, Critical included.
            if (when == SpeakWhen.Never || condition == SpeakCondition.Never) return false;

            // (2) Critical -- see HandleCriticalSubmission's own comment for the full REPLACE/
            //     MERGE reasoning. Exactly one SpeakNow call results either way; the timing/
            //     eligibility gate below is bypassed entirely, same as before.
            if (priority == NotificationPriority.Critical)
            {
                HandleCriticalSubmission(eventType, text, onSpoken);
                return true;
            }

            // (2b) Station Watch routine-suppression (2.0.63) -- unchanged.
            if (_stationWatchSuppressingRoutine && !isWatchCategory) return false;

            // (2c) Important is NOT special-cased on timing here -- it flows through the SAME
            // (3)/(4) timing logic as Normal priority, so an operator-configured SpeakWhen for an
            // Important type (e.g. AutoTxResume held for AfterTx) is honored exactly as
            // configured, not overridden. `cue` (already Important via CueFor above) is the only
            // thing that differs. The ONE behavior Important actually needs -- DEFER, never
            // merge/discard a pending Now-batch -- falls out for free: whenever Important's
            // timing resolves to "speak now" (immediately, or because a held boundary just fired),
            // it takes the same non-watch "ordinary immediate" branch below (or, at a boundary,
            // ReconcileAndFlush's own merge, which is the correct place for an already-due fact to
            // join whatever else is due at that exact moment -- see that method's own comment).
            // Only a genuinely NEW submission arriving mid-batch must never force a synchronous
            // flush-then-speak, and nothing in this method ever does that for Important (or
            // Normal) -- see this file's header comment for the full REPLACE/MERGE/DEFER story.

            // (3) Delivery timing already satisfied -> deliver now IF eligible for the current
            //     QSO state; otherwise discard.
            if (when == SpeakWhen.Now || IsTimingAlreadySatisfied(when))
            {
                if (!IsConditionEligible(condition)) return false;
                if (isWatchCategory)
                {
                    AddToOpenBatch(eventType, identity, text, cue, onSpoken, correlation);
                    return true;
                }
                // Ordinary immediate (non-Watch) Now notification -- DEFER, same as Important/C-F:
                // speaks now, unmodified, never touches a pending batch.
                SpeakNow(text, cue);
                onSpoken?.Invoke();
                return true;
            }

            // (4) Hold for the delivery boundary (coalesce by identity). Eligibility is
            //     re-checked against the real QSO state when the flush actually fires.
            _pendingNotifications[identity ?? ""] = new Pending
            {
                Text = text,
                Cue = cue,
                When = when,
                Condition = condition,
                OnSpoken = onSpoken,
                IsWatchCategory = isWatchCategory,
                EventType = eventType,
            };
            return true;
        }

        // Critical arbitration (2026-09-11, final revision): NEVER "flush the pending batch, then
        // speak Critical" -- that reproduces the original swallowing bug at zero gap (JAWS/NVDA
        // will cut the batch's utterance off the instant Critical's own nudge lands, so the batch
        // "technically speaking" achieves nothing for the operator). Exactly one SpeakNow call
        // results from this method, always.
        //   INVALIDATING (default): the pending batch is cancelled and DISCARDED from speech --
        //     never spoken, onSpoken never invoked for its items -- but every one of those facts
        //     is still in Notification History (recorded unconditionally, upstream, at submit
        //     time), so nothing is silently erased from the record, only from what gets said.
        //     Critical speaks alone. This is the one place in the whole design that knowingly
        //     lets a pending fact go unspoken -- justified because delaying Critical to let the
        //     batch be heard first would defeat Critical's entire purpose (an operator needs a
        //     safety fact -- CAT lost, engine disconnected -- the instant it happens).
        //   NON-INVALIDATING (mechanism only -- NonInvalidatingCriticalTypes is empty today): the
        //     pending batch's survivors are resolved and composed together WITH the Critical text,
        //     Critical sorted first, as ONE utterance.
        private void HandleCriticalSubmission(NotificationEventType eventType, string text, Action onSpoken)
        {
            bool invalidating = !NonInvalidatingCriticalTypes.Contains(eventType);
            if (invalidating)
            {
                CancelOpenBatch();
                CancelFrozenBatch();
                SpeakNow(text, AlertCue.Critical);
                onSpoken?.Invoke();
                return;
            }

            var (parts, batchOnSpoken) = TakeAllPendingNowContent("critical-merge:" + eventType);
            var merged = new List<(NotificationEventType category, string text, int arrival)> { (eventType, text, -1) };
            int idx = 0;
            foreach (var p in parts) merged.Add((p.category, p.text, idx++));
            string composed = ComposeMerged(merged, forceFirst: eventType);
            SpeakNow(composed, AlertCue.Critical);
            onSpoken?.Invoke();
            foreach (var a in batchOnSpoken) a?.Invoke();
        }

        // Station Watch (2.0.63): turning suppression ON drops (does not hold) every currently
        // pending routine fragment and every pending notification not tagged Watch-category, AND
        // (2026-09-11) the Now-batch in both its open and frozen forms -- "do not allow old held
        // routine speech to spill out after entering Watch". Turning it OFF does not need to
        // release anything: nothing was held while suppressed (submissions were discarded, not
        // queued), so lifting the gate only affects what is submitted AFTER this call.
        public void SetStationWatchSuppression(bool active)
        {
            _stationWatchSuppressingRoutine = active;
            if (!active) return;

            _pendingRoutine.Clear();
            CancelOpenBatch();
            CancelFrozenBatch();

            List<string> drop = null;
            foreach (var kv in _pendingNotifications)
                if (!kv.Value.IsWatchCategory) (drop ?? (drop = new List<string>())).Add(kv.Key);
            if (drop != null) foreach (var k in drop) _pendingNotifications.Remove(k);
        }

        // AfterTx / AfterQso are "don't talk over a transmission / an active QSO" -- if neither
        // is happening at submit time the boundary is ALREADY behind us, so deliver now. AfterRx
        // and TxStart are genuine future boundaries and always hold.
        private bool IsTimingAlreadySatisfied(SpeakWhen when)
        {
            if (when == SpeakWhen.AfterTx) return !_physicallyTransmitting;
            if (when == SpeakWhen.AfterQso) return !_qsoActive && !_physicallyTransmitting;
            return false;
        }

        // Eligibility of a condition against the CURRENT QSO state (_qsoActive). Evaluated both
        // at submit time (for already-satisfied timing) and at every flush.
        private bool IsConditionEligible(SpeakCondition condition)
        {
            switch (condition)
            {
                case SpeakCondition.DuringQsoOnly:  return _qsoActive;
                case SpeakCondition.OutsideQsoOnly: return !_qsoActive;
                case SpeakCondition.Never:          return false;
                default:                            return true;   // Always
            }
        }

        // ── Now-batch: collection ────────────────────────────────────────────────────────────────

        private void AddToOpenBatch(NotificationEventType eventType, string dedupKey, string text,
            AlertCue cue, Action onSpoken, SmartStartCorrelation correlation)
        {
            var item = new NowBatchItem
            {
                EventType = eventType,
                DedupKey = dedupKey,
                Text = text,
                Cue = cue,
                OnSpoken = onSpoken,
                Correlation = correlation,
            };
            bool wasEmpty = _openBatch.Count == 0;
            _openBatch.Add(item);

            long now = _clock.ElapsedMilliseconds;
            string target = correlation?.Target ?? "-";
            string group = correlation != null ? correlation.Group.ToString() : "none";
            string seq = correlation != null ? correlation.StateSeq.ToString() : "-";
            if (wasEmpty)
            {
                _openFirstArrivalMs = now;
                _openBatchId = ++_nextBatchId;
                _logDiagnostic($"[JOIN-BATCH {_openBatchId}] opened T={now}ms first={eventType}(target={target} group={group} seq={seq})");
            }
            else
            {
                _logDiagnostic($"[JOIN-BATCH {_openBatchId}] +item T={now}ms {eventType}(target={target} group={group} seq={seq})");
            }
            if (_openToken != null) { _scheduler.Cancel(_openToken); _openToken = null; }

            long ceilingRemaining = (_openFirstArrivalMs + MaxBatchWindowMs) - now;
            int delay = (int)Math.Max(0, Math.Min(QuietPeriodMs, ceilingRemaining));
            var token = _scheduler.Schedule(delay, OnOpenBatchTimerFired);
            // Defensive against a scheduler that fires synchronously (a test double): only keep
            // the token if the batch is STILL open after Schedule() returns -- otherwise
            // OnOpenBatchTimerFired already ran and cleared it, and holding a stale/already-fired
            // token here would leave _openToken non-null with nothing really pending.
            if (_openBatch.Count > 0) _openToken = token;
            else _scheduler.Cancel(token);
        }

        private void OnOpenBatchTimerFired()
        {
            _openToken = null;
            if (_openBatch.Count == 0) return;
            long now = _clock.ElapsedMilliseconds;
            // Distinguish which of the two timers actually fired, for the diagnostic only --
            // both share one scheduled callback (delay = min(quiet, ceilingRemaining)), so this
            // is inferred from elapsed time rather than tracked as a separate flag.
            string reason = (now - _openFirstArrivalMs) >= _maxBatchWindowMs ? "max-ceiling-fallback" : "quiet-timer";
            long batchId = _openBatchId;
            var raw = new List<NowBatchItem>(_openBatch);
            _openBatch.Clear();
            var resolved = ResolveBatch(raw);
            LogBatchResolved(batchId, reason, now, raw.Count, resolved);
            if (resolved.Count == 0) return;
            FreezeAndTrySpeak(resolved);
        }

        // Notification-joining support (2026-09-11 semantic-boundary correction): the confirmed
        // end of ONE decode-processing pass (WsjtxClient.Direct.cs's DirectApplyDecodes, called
        // AFTER ServicePendingAutoStart -- see the live-radio investigation this fixes:
        // OnReceiveCycleComplete/OnPeriodBoundary fires BEFORE FeedTargetMonitorsPeriodComplete's
        // "not heard" publish and BEFORE ServicePendingAutoStart's own possible publish, so it is
        // NOT a reliable "everything this pass could say has been said" signal on its own).
        // This IS that signal for the Now-batch specifically: if anything is open or still
        // gap-waiting, resolve/merge it into ONE utterance now. This is the PRIMARY completion
        // trigger for Smart Start/Station Watch narration produced during a decode pass;
        // MaxBatchWindowMs remains only as the fallback for content NOT produced during a decode
        // pass (an operator-initiated Smart Start capture) or if this signal is ever delayed.
        public void OnDecodePassComplete()
        {
            if (_openBatch.Count == 0 && _frozenParts == null) return;
            long batchId = _openBatchId;
            long now = _clock.ElapsedMilliseconds;
            if (_openToken != null) { _scheduler.Cancel(_openToken); _openToken = null; }
            var raw = new List<NowBatchItem>(_openBatch);
            _openBatch.Clear();
            var resolved = raw.Count > 0 ? ResolveBatch(raw) : new List<ResolvedItem>();
            if (raw.Count > 0) LogBatchResolved(batchId, "decode-pass-complete", now, raw.Count, resolved);
            if (resolved.Count > 0) FreezeAndTrySpeak(resolved);
            else if (_frozenParts != null) TrySpeakFrozen();   // nothing new, but something was still gap-waiting
        }

        private void LogBatchResolved(long batchId, string reason, long resolvedAtMs, int itemCount, List<ResolvedItem> resolved)
        {
            int survivors = resolved.Count;
            int dropped = itemCount - survivors;
            _logDiagnostic($"[JOIN-BATCH {batchId}] resolve reason={reason} T={resolvedAtMs}ms items={itemCount} " +
                $"survivors={survivors} dropped(duplicate/superseded)={dropped}");
        }

        // Deduplicate exact repeats (same EventType+DedupKey+Text -- spoken once, every
        // duplicate's onSpoken still fires), then apply Posture/Observation supersession per
        // (Target, ArmGeneration, Group): only the highest-AdvanceStateSeq member of each group
        // survives; the rest are dropped entirely (not spoken, onSpoken never invoked). Items
        // with no correlation (Station Watch's 4 types) always survive.
        private static List<ResolvedItem> ResolveBatch(List<NowBatchItem> raw)
        {
            var deduped = new List<NowBatchItem>();
            var byIdentity = new Dictionary<(NotificationEventType, string, string), NowBatchItem>();
            var extraOnSpoken = new Dictionary<NowBatchItem, List<Action>>();
            foreach (var item in raw)
            {
                var key = (item.EventType, item.DedupKey ?? "", item.Text ?? "");
                if (byIdentity.TryGetValue(key, out var existing))
                {
                    if (item.OnSpoken != null)
                    {
                        if (!extraOnSpoken.TryGetValue(existing, out var l))
                            extraOnSpoken[existing] = l = new List<Action>();
                        l.Add(item.OnSpoken);
                    }
                    continue;
                }
                byIdentity[key] = item;
                deduped.Add(item);
            }

            var bestByGroup = new Dictionary<(string, int, SmartStartGroup), NowBatchItem>();
            foreach (var item in deduped)
            {
                if (item.Correlation == null) continue;
                var gkey = (item.Correlation.Target, item.Correlation.ArmGeneration, item.Correlation.Group);
                if (!bestByGroup.TryGetValue(gkey, out var current) || item.Correlation.StateSeq > current.Correlation.StateSeq)
                    bestByGroup[gkey] = item;
            }

            var result = new List<ResolvedItem>();
            foreach (var item in deduped)
            {
                if (item.Correlation != null)
                {
                    var gkey = (item.Correlation.Target, item.Correlation.ArmGeneration, item.Correlation.Group);
                    if (!ReferenceEquals(bestByGroup[gkey], item)) continue;   // superseded -- discarded
                }
                var r = new ResolvedItem { Category = item.EventType, Text = item.Text };
                if (item.OnSpoken != null) r.OnSpokenActions.Add(item.OnSpoken);
                if (extraOnSpoken.TryGetValue(item, out var extra)) r.OnSpokenActions.AddRange(extra);
                result.Add(r);
            }
            return result;
        }

        // ── Now-batch: freeze + speak (gap-respecting) ──────────────────────────────────────────

        private void FreezeAndTrySpeak(List<ResolvedItem> resolved)
        {
            var parts = resolved.Select(r => (r.Category, r.Text)).ToList();
            var onSpoken = resolved.SelectMany(r => r.OnSpokenActions).Where(a => a != null).ToList();

            // Merge with anything already frozen (still gap-waiting) -- rare, but its facts must
            // not be lost just because a NEW batch also resolved in the meantime.
            if (_frozenParts != null)
            {
                parts.AddRange(_frozenParts);
                onSpoken.AddRange(_frozenOnSpoken);
                if (_frozenToken != null) { _scheduler.Cancel(_frozenToken); _frozenToken = null; }
            }

            _frozenBatchId = _openBatchId;
            _frozenParts = parts;
            _frozenOnSpoken = onSpoken;
            TrySpeakFrozen();
        }

        private void TrySpeakFrozen()
        {
            _frozenToken = null;
            if (_frozenParts == null || _frozenParts.Count == 0) return;

            long now = _clock.ElapsedMilliseconds;
            if (_lastSpeechElapsedMs.HasValue && now - _lastSpeechElapsedMs.Value < _minSequentialGapMs)
            {
                int remaining = (int)(_minSequentialGapMs - (now - _lastSpeechElapsedMs.Value));
                _logDiagnostic($"[JOIN-BATCH {_frozenBatchId}] deferred T={now}ms gap not yet satisfied, retry in {remaining}ms");
                var token = _scheduler.Schedule(remaining, TrySpeakFrozen);
                // Same synchronous-scheduler defence as AddToOpenBatch.
                if (_frozenParts != null) _frozenToken = token;
                else _scheduler.Cancel(token);
                return;
            }

            long batchId = _frozenBatchId;
            var parts = _frozenParts.Select((p, i) => (p.category, p.text, i)).ToList();
            var onSpoken = _frozenOnSpoken;
            _frozenParts = null;
            _frozenOnSpoken = null;

            string composed = ComposeMerged(parts, forceFirst: null);
            if (composed.Length == 0)
            {
                _logDiagnostic($"[JOIN-BATCH {batchId}] speak T={now}ms text=\"\" nudges=0");
                foreach (var a in onSpoken) a?.Invoke();
                return;
            }
            SpeakNow(composed, AlertCue.None);
            _logDiagnostic($"[JOIN-BATCH {batchId}] speak T={now}ms text=\"{composed}\" nudges=1");
            foreach (var a in onSpoken) a?.Invoke();
        }

        private void CancelOpenBatch()
        {
            if (_openToken != null) { _scheduler.Cancel(_openToken); _openToken = null; }
            _openBatch.Clear();
        }

        private void CancelFrozenBatch()
        {
            if (_frozenToken != null) { _scheduler.Cancel(_frozenToken); _frozenToken = null; }
            _frozenParts = null;
            _frozenOnSpoken = null;
        }

        // Resolves BOTH the open batch (ad hoc, right now) and the frozen batch (already
        // resolved), cancels both tokens, clears both, and returns the union -- used by the
        // Critical MERGE branch and by lifecycle-boundary reconciliation. "Must not silently
        // remove valid pending speech" applies regardless of which of the two states the pending
        // content happens to be in at the moment something needs to absorb it.
        // `reason` identifies WHY this drain is happening (e.g. "reconcile:AfterRx",
        // "critical-merge:CatLost") purely for diagnostics -- it has no effect on behavior. Any
        // open batch drained here is resolved (dedup + Posture/Observation supersession) same as
        // OnOpenBatchTimerFired/OnDecodePassComplete, and that resolution is logged the same way
        // (LogBatchResolved) so it shows up alongside every other batch's resolve line even though
        // it never reached its own timer/semantic-boundary trigger. Frozen content is already
        // resolved (its own resolve line was logged when it froze) -- absorbing it here just means
        // it speaks as part of THIS utterance instead of on its own gap-timer, so that gets a
        // distinct "absorbed" line rather than a second "resolve".
        private (List<(NotificationEventType category, string text)> parts, List<Action> onSpoken) TakeAllPendingNowContent(string reason)
        {
            var parts = new List<(NotificationEventType, string)>();
            var onSpoken = new List<Action>();

            if (_openToken != null) { _scheduler.Cancel(_openToken); _openToken = null; }
            if (_openBatch.Count > 0)
            {
                long batchId = _openBatchId;
                long now = _clock.ElapsedMilliseconds;
                var raw = new List<NowBatchItem>(_openBatch);
                _openBatch.Clear();
                var resolved = ResolveBatch(raw);
                LogBatchResolved(batchId, reason, now, raw.Count, resolved);
                foreach (var r in resolved)
                {
                    parts.Add((r.Category, r.Text));
                    onSpoken.AddRange(r.OnSpokenActions.Where(a => a != null));
                }
            }

            if (_frozenToken != null) { _scheduler.Cancel(_frozenToken); _frozenToken = null; }
            if (_frozenParts != null)
            {
                _logDiagnostic($"[JOIN-BATCH {_frozenBatchId}] absorbed reason={reason} T={_clock.ElapsedMilliseconds}ms " +
                    $"items={_frozenParts.Count} (was gap-waiting, now joining this utterance instead)");
                parts.AddRange(_frozenParts);
                onSpoken.AddRange(_frozenOnSpoken);
                _frozenParts = null;
                _frozenOnSpoken = null;
            }

            return (parts, onSpoken);
        }

        // ── Composition ──────────────────────────────────────────────────────────────────────────

        // Sorts by the configured join order (an explicit arrival-index tiebreak makes this
        // deterministic regardless of List<T>.Sort's lack of a stability guarantee -- ties are
        // fully resolved by the comparator itself, not left to the sort algorithm), optionally
        // pinning one category first (Critical's MERGE branch), then joins the results with a
        // single space between each.
        //
        // Deliberately NOT the existing Compose(List<Pending>) clause-weaver: that method's
        // separator rule ("if the previous text already ends in sentence punctuation, append the
        // next fragment with NO separator at all") is correct for its own job -- weaving PARTIAL
        // clauses of one routine sentence together, where only the very last clause ends in "."
        // -- but wrong here: every category merged by THIS method contributes its own already-
        // complete sentence ("Waiting to work EA6Y." / "EA6Y to KX4I, R minus 14." / "1 new
        // DXCC."), and two complete sentences need a space between them, not nothing.
        private string ComposeMerged(List<(NotificationEventType category, string text, int arrival)> parts,
            NotificationEventType? forceFirst)
        {
            var list = parts.Where(p => !string.IsNullOrEmpty(p.text)).ToList();
            list.Sort((a, b) =>
            {
                if (forceFirst.HasValue)
                {
                    bool aFirst = a.category == forceFirst.Value;
                    bool bFirst = b.category == forceFirst.Value;
                    if (aFirst != bFirst) return aFirst ? -1 : 1;
                }
                int ia = JoinOrderIndex(a.category), ib = JoinOrderIndex(b.category);
                if (ia != ib) return ia.CompareTo(ib);
                return a.arrival.CompareTo(b.arrival);
            });
            return JoinUtterances(list.Select(p => p.text));
        }

        // Joins several independent, already-complete utterances with a single space -- see
        // ComposeMerged's own comment for why this is deliberately not Compose(List<Pending>).
        private static string JoinUtterances(IEnumerable<string> texts)
        {
            var nonEmpty = texts.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
            if (nonEmpty.Count == 0) return "";
            string joined = string.Join(" ", nonEmpty);
            while (joined.Contains("  ")) joined = joined.Replace("  ", " ");
            return HasSpeakableContent(joined) ? joined : "";
        }

        private int JoinOrderIndex(NotificationEventType type)
        {
            for (int i = 0; i < _joinOrder.Count; i++) if (_joinOrder[i] == type) return i;
            return int.MaxValue;
        }

        // Back-compat single-clause entry point: a whole routine line as one fragment.
        public void SubmitRoutineStatus(string text, bool speakNow, SpeakWhen when,
            SpeakCondition condition = SpeakCondition.Always)
        {
            SubmitRoutineComposite(new[]
            {
                new RoutineFragment { Key = "_base", Order = 0, Text = text ?? "", When = when, Condition = condition, Sticky = false },
            }, speakNow);
        }

        // The routine RX/TX/QSO status line as an ordered set of clause fragments (see
        // RoutineFragment). The VISIBLE line + history are ALREADY updated upstream, for every
        // clause, unconditionally -- this only decides the screen-reader nudge(s). `speakNow` is
        // the caller's own focus / near-duplicate decision for the immediate batch. `allowSpeech`
        // false = this render only re-states a persistent condition the notifications already
        // speak (CAT link down while idle): nothing is spoken now and nothing new is held, but
        // fragments already held from earlier renders still flush on their own boundary.
        public void SubmitRoutineComposite(IReadOnlyList<RoutineFragment> fragments, bool speakNow,
            bool allowSpeech = true)
        {
            if (fragments == null) fragments = Array.Empty<RoutineFragment>();

            if (_stationWatchSuppressingRoutine) return;
            if (!allowSpeech) return;

            var present = new HashSet<string>();
            foreach (var f in fragments) if (f != null) present.Add(f.Key ?? "");
            List<string> drop = null;
            foreach (var kv in _pendingRoutine)
                if (!kv.Value.Sticky && !present.Contains(kv.Key))
                    (drop ?? (drop = new List<string>())).Add(kv.Key);
            if (drop != null) foreach (var k in drop) _pendingRoutine.Remove(k);

            var nowBatch = new List<Pending>();
            foreach (var f in fragments)
            {
                if (f == null) continue;
                string key = f.Key ?? "";
                if (f.When == SpeakWhen.Never || f.Condition == SpeakCondition.Never)
                {
                    _pendingRoutine.Remove(key);
                    continue;
                }
                var p = new Pending
                {
                    Text = f.Text ?? "",
                    Cue = AlertCue.None,
                    When = f.When,
                    Condition = f.Condition,
                    Order = f.Order,
                    Sticky = f.Sticky,
                    IsBase = key.StartsWith("_base", StringComparison.Ordinal),
                    RenderedWhileTx = _physicallyTransmitting,
                };
                if (f.When == SpeakWhen.Now || IsTimingAlreadySatisfied(f.When))
                {
                    _pendingRoutine.Remove(key);
                    if (IsConditionEligible(f.Condition)) nowBatch.Add(p);
                }
                else
                {
                    _pendingRoutine[key] = p;
                }
            }

            if (speakNow)
            {
                string composed = Compose(nowBatch);
                if (composed.Length > 0) SpeakNow(composed, AlertCue.None);
            }
        }

        // Join routine fragments into ONE natural spoken sentence: sort by Order, then append
        // each fragment's clean text, inserting ", " only where two clean phrases would
        // otherwise run together and collapsing any doubled separator left where a fragment was
        // filtered out.
        private static string Compose(List<Pending> parts)
        {
            if (parts.Count == 0) return "";
            parts.Sort((x, y) => x.Order.CompareTo(y.Order));
            var sb = new System.Text.StringBuilder();
            foreach (var part in parts)
            {
                string t = part.Text ?? "";
                if (t.Length == 0) continue;
                if (sb.Length == 0)
                {
                    sb.Append(t.TrimStart(' ', ',', ';'));
                    continue;
                }
                char last = sb[sb.Length - 1];
                bool sbEndsSep = last == ' ' || last == ',' || last == ';' || last == '-'
                              || last == '.' || last == '!' || last == '?' || last == ':';
                if (sbEndsSep)
                {
                    sb.Append(t.TrimStart(' ', ',', ';'));
                }
                else
                {
                    char first = t[0];
                    bool startsSep = first == ' ' || first == ',' || first == ';'
                                  || first == '.' || first == '!' || first == '?' || first == ':';
                    if (!startsSep) sb.Append(", ");
                    sb.Append(t);
                }
            }
            string s = sb.ToString().Trim();
            while (s.Contains(", ,")) s = s.Replace(", ,", ",");
            while (s.Contains(", .")) s = s.Replace(", .", ".");
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            s = s.TrimEnd(' ', ',', ';');
            return HasSpeakableContent(s) ? s : "";
        }

        private static bool HasSpeakableContent(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s) if (char.IsLetterOrDigit(c)) return true;
            return false;
        }

        // ── Lifecycle hooks (driven by real WsjtxClient state transitions) ────────────────────

        // The authoritative radio.Transmitting edge (DirectApplyStatus's transmittingChanged).
        public void OnPhysicalTxChanged(bool transmitting)
        {
            bool rising = transmitting && !_physicallyTransmitting;
            bool falling = !transmitting && _physicallyTransmitting;
            _physicallyTransmitting = transmitting;

            if (rising)
            {
                DropRoutineIf(p => p.When == SpeakWhen.AfterRx || p.When == SpeakWhen.RxStart);
                ReconcileAndFlush("TxStart", p => p.When == SpeakWhen.TxStart, p => p.When == SpeakWhen.TxStart);
            }
            else if (falling)
            {
                DropRoutineIf(p => p.RenderedWhileTx && p.IsBase && p.When != SpeakWhen.AfterQso);
                ReconcileAndFlush(
                    "TxEnd",
                    p => p.When == SpeakWhen.AfterTx || p.When == SpeakWhen.RxStart
                        || (p.When == SpeakWhen.AfterQso && !_qsoActive),
                    p => p.When == SpeakWhen.AfterTx || p.When == SpeakWhen.RxStart
                        || (p.When == SpeakWhen.AfterQso && !_qsoActive));
            }
        }

        // A receive period has BEGUN.
        public void OnReceivePeriodStarted()
        {
            if (_physicallyTransmitting)
            {
                DropRoutineIf(p => p.When == SpeakWhen.RxStart);
                return;
            }
            ReconcileAndFlush("RxStart", p => p.When == SpeakWhen.RxStart, p => p.When == SpeakWhen.RxStart);
        }

        // The receive period finished AND its decodes were processed.
        public void OnReceiveCycleComplete()
        {
            if (_physicallyTransmitting)
            {
                DropRoutineIf(p => p.When == SpeakWhen.AfterRx);
                return;
            }
            ReconcileAndFlush("AfterRx", p => p.When == SpeakWhen.AfterRx, p => p.When == SpeakWhen.AfterRx);
        }

        // callInProg became null / non-null.
        public void OnQsoActiveChanged(bool active)
        {
            bool ending = !active && _qsoActive;
            _qsoActive = active;
            if (!ending) return;
            if (_physicallyTransmitting) return;   // TX falling edge owns the release instead
            ReconcileAndFlush("AfterQso", p => p.When == SpeakWhen.AfterQso, p => p.When == SpeakWhen.AfterQso);
        }

        // ── Boundary reconciliation (2026-09-11) ────────────────────────────────────────────────
        // Any ordinary lifecycle edge -- receive cycle complete, TX started/ended, QSO ended --
        // merges whatever the boundary's OWN buckets have eligible with whatever the Now-batch
        // (open + frozen) currently holds into ONE Compose()/SpeakNow call. If either side is
        // empty, the other's content simply speaks alone. If NEITHER has anything, nothing speaks.
        // This is deliberately unconditional -- "must not silently remove valid pending speech"
        // applies to every ordinary boundary, not only the receive-cycle case that surfaced it.
        // Flushing the Now-batch EARLY here (before its own debounce/ceiling would have elapsed)
        // is correct, not a compromise: the edge firing is itself proof real state has moved on.
        // Merges the routine-status-line bucket with the Now-batch into ONE utterance (this
        // feature's own new behavior -- the actually-observed "Smart Start narration + N new
        // DXCC" scenario). The held-NOTIFICATIONS bucket (_pendingNotifications -- AwardsNeeded
        // once wired, or any other AfterRx/AfterTx/AfterQso/RxStart/TxStart-held type) DELIBERATELY
        // keeps its pre-existing, separately-tested delivery semantics -- each eligible held
        // identity speaks in its own SpeakNow call, exactly as FlushNotificationsSeparately (and,
        // before this fix, the original FlushNotificationsIf) always has. That multi-identity/
        // LatestOnly/Normal coalescing behavior is a distinct, already-shipped 2026-09-09 feature
        // this fix must not silently change -- only the Now-batch/routine-line pairing is new.
        // 2026-09-11 correction: an EARLIER version of this method spoke the routine+Now-batch
        // merge in one SpeakNow call and then flushed _pendingNotifications in a SEPARATE loop of
        // its own SpeakNow calls, immediately afterward -- a zero-gap "compose one utterance, then
        // immediately speak another" sequence for any boundary where BOTH a Now-batch/routine fact
        // and a held notification were eligible at once, and a zero-gap N-in-a-row sequence
        // whenever two or more DIFFERENT held-notification identities were eligible together. Both
        // are exactly the swallowing pattern this whole feature exists to close, just relocated to
        // a combination that had no test coverage before. Fixed: every eligible source at ONE
        // boundary -- the routine-status line, the Now-batch, AND every eligible held notification
        // -- is gathered into ONE merged list and produces exactly ONE Compose()/SpeakNow call.
        // Nothing here needs to "remain distinct" from this merge: Critical never reaches
        // _pendingNotifications (it bypasses timing entirely via HandleCriticalSubmission), and an
        // Important item held for a later boundary is, by the time this fires, simply a fact that
        // is due NOW alongside everything else due now -- joining it is correct, not a collision
        // (the DEFER-vs-a-*pending*-batch concern is about a NEW submission arriving while
        // something is still open/unresolved, which this method is not).
        private void ReconcileAndFlush(string boundaryName, Func<Pending, bool> routineReady, Func<Pending, bool> notificationReady)
        {
            long now = _clock.ElapsedMilliseconds;
            string routineText = TakeRoutineBucketComposed(routineReady);
            var (notifParts, notifOnSpoken) = TakeNotificationBucketParts(notificationReady);
            var (nowParts, nowOnSpoken) = TakeAllPendingNowContent("reconcile:" + boundaryName);

            _logDiagnostic($"[RECONCILE {boundaryName}] T={now}ms gathered: routine={(string.IsNullOrEmpty(routineText) ? "none" : "\"" + routineText + "\"")} " +
                $"notifParts={notifParts.Count} nowParts={nowParts.Count}");

            var merged = new List<(NotificationEventType category, string text, int arrival)>();
            int idx = 0;
            if (!string.IsNullOrEmpty(routineText))
                merged.Add((NotificationEventType.RoutineStatusLine, routineText, idx++));
            foreach (var p in notifParts) merged.Add((p.category, p.text, idx++));
            foreach (var p in nowParts) merged.Add((p.category, p.text, idx++));

            if (merged.Count == 0)
            {
                _logDiagnostic($"[RECONCILE {boundaryName}] T={now}ms disposition=nothing-to-speak (no source had content)");
                return;
            }
            string composed = ComposeMerged(merged, forceFirst: null);
            if (composed.Length == 0)
            {
                _logDiagnostic($"[RECONCILE {boundaryName}] T={now}ms items={merged.Count} composed=\"\" disposition=nothing-to-speak (all sources blank after compose)");
                foreach (var a in notifOnSpoken) a?.Invoke();
                foreach (var a in nowOnSpoken) a?.Invoke();
                return;
            }

            SpeakNow(composed, AlertCue.None);
            _logDiagnostic($"[RECONCILE {boundaryName}] T={now}ms items={merged.Count} composed=\"{composed}\" disposition=spoken nudges=1");
            foreach (var a in notifOnSpoken) a?.Invoke();
            foreach (var a in nowOnSpoken) a?.Invoke();
        }

        // Discard held routine fragments matching `pred` without speaking them (stale snapshot).
        private void DropRoutineIf(Func<Pending, bool> pred)
        {
            List<string> keys = null;
            foreach (var kv in _pendingRoutine)
                if (pred(kv.Value)) (keys ?? (keys = new List<string>())).Add(kv.Key);
            if (keys != null) foreach (var k in keys) _pendingRoutine.Remove(k);
        }

        // Gathers + removes the routine fragments whose boundary has arrived, composes them
        // internally (their own existing Order-based weaving, unchanged), and returns the result
        // as ONE opaque string -- "" if nothing was eligible. Does NOT speak; ReconcileAndFlush
        // (or the legacy FlushRoutineBucket wrapper below, kept for internal reuse) owns that.
        private string TakeRoutineBucketComposed(Func<Pending, bool> ready)
        {
            List<string> keys = null;
            foreach (var kv in _pendingRoutine)
                if (ready(kv.Value)) (keys ?? (keys = new List<string>())).Add(kv.Key);
            if (keys == null) return "";
            var batch = new List<Pending>();
            foreach (var k in keys)
            {
                var p = _pendingRoutine[k];
                _pendingRoutine.Remove(k);
                if (IsConditionEligible(p.Condition)) batch.Add(p);
            }
            return Compose(batch);
        }

        // Gathers + removes the held notifications whose boundary has arrived and speaks each
        // ELIGIBLE one in its OWN SpeakNow call -- unchanged from the original, pre-2026-09-11
        // FlushNotificationsIf (only renamed, since it is now called from ReconcileAndFlush
        // rather than directly from the lifecycle hooks). See ReconcileAndFlush's own comment for
        // why this bucket is deliberately NOT merged into the routine/Now-batch utterance.
        // Gathers + removes the held notifications whose boundary has arrived and returns them as
        // parts to merge into the SAME utterance as the routine line / Now-batch -- does NOT speak
        // them itself. See ReconcileAndFlush's own comment for why merging (not one SpeakNow call
        // per identity) is the correct, deliberate behavior here.
        private (List<(NotificationEventType category, string text)> parts, List<Action> onSpoken) TakeNotificationBucketParts(Func<Pending, bool> ready)
        {
            var parts = new List<(NotificationEventType, string)>();
            var onSpoken = new List<Action>();
            List<string> keys = null;
            foreach (var kv in _pendingNotifications)
                if (ready(kv.Value)) (keys ?? (keys = new List<string>())).Add(kv.Key);
            if (keys == null) return (parts, onSpoken);
            foreach (var k in keys)
            {
                var p = _pendingNotifications[k];
                _pendingNotifications.Remove(k);
                if (!IsConditionEligible(p.Condition)) continue;
                if (string.IsNullOrEmpty(p.Text)) continue;
                parts.Add((p.EventType, p.Text));
                if (p.OnSpoken != null) onSpoken.Add(p.OnSpoken);
            }
            return (parts, onSpoken);
        }
    }
}
