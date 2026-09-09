using System;
using System.Collections.Generic;

namespace WSJTX_Controller
{
    // The single authority for WHEN something Jimmy has to say is actually spoken (nudged to the
    // screen reader). Added 2026-09-02 (Items 1 & 2). Both speech sources feed through here:
    //
    //   * typed NotificationCenter events  -> SubmitNotification(...)  (NotificationCenter.Deliver)
    //   * routine RX / TX / QSO status     -> SubmitRoutineStatus(...) (WsjtxClient.ShowStatus,
    //                                          after Controller.RenderStatusVisible sets the line)
    //
    // What this does NOT touch: the VISIBLE status text/colours and the Notification History
    // entry. Those are updated immediately and unconditionally by their own call sites BEFORE
    // anything reaches here -- a deferred or suppressed *utterance* never hides or delays the
    // on-screen fact. This class only decides whether/when to fire the screen-reader nudge.
    //
    // Two orthogonal controls decide an utterance's fate:
    //
    //   SpeakCondition (ELIGIBILITY -- may this be spoken at all, given the QSO state):
    //     Always         -> eligible whether or not a QSO is active.
    //     DuringQsoOnly  -> eligible only while a QSO is active at the delivery boundary.
    //     OutsideQsoOnly -> eligible only while no QSO is active at the delivery boundary.
    //     Never          -> never spoken (recorded/shown upstream only).
    //   Eligibility is checked against the REAL QSO state at the delivery boundary, not just at
    //   submit time -- so an OutsideQsoOnly event that fires mid-QSO and is deferred (AfterQso)
    //   is HELD, then spoken once the QSO ends, rather than discarded.
    //
    //   SpeakWhen (DELIVERY TIMING -- at which operating boundary a still-eligible item goes):
    //     Now      -> at submit time.
    //     TxStart  -> on the real radio.Transmitting false -> true edge (OnPhysicalTxChanged(true)).
    //     AfterRx  -> on OnReceiveCycleComplete() (receive period finished AND its decodes
    //                 processed -- the end of the Direct new-slot decode pass, not a timer;
    //                 FT8 and FT4 alike).
    //     AfterTx  -> on OnPhysicalTxChanged(false) (the real radio.Transmitting true -> false edge).
    //     AfterQso -> when the QSO ends AND no transmission is physically in progress.
    //                 OnQsoActiveChanged(false) releases it when TX is already idle; if the QSO
    //                 ends mid-over the release is deferred to the TX falling edge, so nothing --
    //                 routine or notification -- is ever spoken over a live transmission, and
    //                 nothing is left stuck.
    //     Never    -> record only; do not speak (legacy sentinel; the UI expresses this via
    //                 SpeakCondition.Never).
    //   If timing lands and the item is NOT eligible for the QSO state at that moment, it is
    //   DISCARDED (never spoken, OnSpoken never fires) -- not left pending forever.
    //
    // Priority is orthogonal. NotificationPriority.Critical (CAT lost, high-SWR halt, engine
    // failure, serious logging failure) is spoken the instant it is submitted, bypassing the
    // delivery timing AND the DuringQsoOnly/OutsideQsoOnly eligibility gate, is never held
    // behind routine speech, and is never dropped by coalescing. It does NOT override an
    // explicit SpeakCondition.Never. Normal / Important honour condition and timing in full --
    // being "Important" alone never grants a bypass.
    //
    // Obsolescence / coalescing while an item is held:
    //   * Only the NEWEST held routine status is ever kept (a fresher render replaces the held
    //     one -- "latest relevant snapshot wins for speech"). History still has every distinct
    //     fact; speech does not replay superseded routine snapshots.
    //   * A held routine status that was rendered WHILE transmitting is dropped (not spoken)
    //     once TX ends -- a stale "Transmitting..." must not be uttered after TX is already over.
    //   * A held AfterRx routine status is dropped once physical TX starts -- do not speak stale
    //     receive status after transmission has begun.
    //   * A held notification is coalesced by (EventType, DedupKey): re-submitting the same
    //     identity replaces the held one with the newer text.
    //   * Critical items are never held and never dropped.
    //
    // Routine-status COMPOSITION (2026-09-04): the routine RX/TX/QSO status line is submitted as
    // an ordered list of RoutineFragments (SubmitRoutineComposite), one per logical clause --
    // the structural "_base" part plus the individually-configurable "Receive cycle summary",
    // "QSO started", "QSO logged", "Transmit message", "Received reply" and "No-decode warning"
    // clauses. Each fragment carries its OWN SpeakWhen + SpeakCondition. Fragments whose
    // boundary is already satisfied and whose condition is eligible are composed together, in
    // Order, into ONE utterance and spoken now; the rest are held per boundary and, when that
    // boundary fires, the still-eligible held fragments for it are again composed into ONE
    // utterance. So clauses that share a boundary are never spoken as competing announcements,
    // while clauses the operator has deliberately given different boundaries are delivered
    // separately. With the shipped defaults every clause shares a boundary, so the line speaks
    // exactly as one utterance, unchanged.
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

    public sealed class SpeechCoordinator
    {
        // The actual delivery seam -- (text, cue) exactly as INotificationDelivery.Announce
        // expects. The cue tells the accessible-alert layer whether this is an Important or
        // Critical notification (routine RX/TX/QSO status is always AlertCue.None).
        private readonly Action<string, AlertCue> _speak;

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
                                          // (describes the CURRENT state, so a copy rendered
                                          // mid-TX goes stale once TX ends). A configurable
                                          // clause ("sending 73") is not stale that way.

            // Notifications only. True for a Station Watch/Smart Start event -- bypasses the
            // Station-Watch routine-suppression gate the same way Critical bypasses the QSO gate.
            public bool IsWatchCategory;

            // Notifications only. Invoked the moment this notification is ACTUALLY spoken
            // (immediate or via a flush) -- never when it is discarded (Never / QSO-suppressed).
            // NotificationCenter uses it to move its dedup/throttle "last announced" bookkeeping
            // to the point of real delivery, so a suppressed occurrence can't poison the repeat
            // window for a later one that should be heard (Codex #12).
            public Action OnSpoken;
        }

        public SpeechCoordinator(Action<string, AlertCue> speak)
        {
            _speak = speak ?? throw new ArgumentNullException(nameof(speak));
        }

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

        // ── Submission ────────────────────────────────────────────────────────────────────────

        // Returns true when the notification was spoken now or queued for a later boundary;
        // false when it was discarded outright (Never, or not eligible for the current QSO state
        // and not deferrable into one). `onSpoken`, when supplied, fires exactly once at the
        // moment of real speech -- immediately here, or later from a flush -- and never fires for
        // a discarded occurrence.
        public bool SubmitNotification(string identity, string text,
            SpeakWhen when, NotificationPriority priority,
            SpeakCondition condition = SpeakCondition.Always, Action onSpoken = null,
            bool isWatchCategory = false)
        {
            if (string.IsNullOrEmpty(text)) return false;

            AlertCue cue = CueFor(priority);

            // (1) An explicit "never spoken" wins over everything, Critical included.
            if (when == SpeakWhen.Never || condition == SpeakCondition.Never) return false;

            // (2) Critical / Error cuts through the delivery timing AND the during/outside-a-QSO
            //     eligibility gate -- the operator needs a prompt safety warning.
            if (priority == NotificationPriority.Critical)
            {
                _speak(text, cue);
                onSpoken?.Invoke();
                return true;
            }

            // (2b) Station Watch routine-suppression (2.0.63): while a receive-only watch is
            //      active, every submission that is neither Watch-category speech nor Critical
            //      (already handled above) is discarded outright -- not held for a later
            //      boundary, since a routine fact spoken late, after the watch already ended,
            //      would be a stale announcement. Watch-category speech (Station Watch/Smart
            //      Start's own observations) is exactly what SHOULD be heard here, so it falls
            //      straight through to the normal timing/eligibility handling below.
            if (_stationWatchSuppressingRoutine && !isWatchCategory) return false;

            // (3) Delivery timing already satisfied -> deliver now IF eligible for the current
            //     QSO state; otherwise discard (an OutsideQsoOnly notice fired mid-QSO with
            //     SpeakWhen.Now is dropped -- "now" is inside a QSO).
            if (when == SpeakWhen.Now || IsTimingAlreadySatisfied(when))
            {
                if (!IsConditionEligible(condition)) return false;
                _speak(text, cue);
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
            };
            return true;
        }

        // Station Watch (2.0.63): turning suppression ON drops (does not hold) every currently
        // pending routine fragment and every pending notification not tagged Watch-category --
        // "do not allow old held routine speech to spill out after entering Watch". Turning it
        // OFF (normal QSO handoff actually succeeded, or the watch was stopped) does not need to
        // release anything: nothing was held while suppressed (submissions were discarded, not
        // queued), so lifting the gate only affects what is submitted AFTER this call.
        public void SetStationWatchSuppression(bool active)
        {
            _stationWatchSuppressingRoutine = active;
            if (!active) return;

            _pendingRoutine.Clear();

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
            if (fragments == null) fragments = System.Array.Empty<RoutineFragment>();

            // Station Watch routine-suppression (2.0.63): routine RX/TX/QSO status is never
            // Watch-category speech, so while a receive-only watch is active it is not spoken and
            // not held for later -- the visible status line/history are unaffected (those are
            // updated by the caller before this is ever reached).
            if (_stationWatchSuppressingRoutine) return;

            // allowSpeech:false is a "suppress the nudge this render" marker (CAT link down while
            // idle) -- it is NOT a real status snapshot, so it neither speaks nor disturbs any
            // fragment already held from an earlier real render.
            if (!allowSpeech) return;

            // Drop any held "current state" fragment this render no longer produces -- a stale
            // snapshot must never be spoken. One-shot (Sticky) fragments survive an absent render.
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
                    _pendingRoutine.Remove(key);          // an explicit-silence clause replaces any held copy
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
                    _pendingRoutine.Remove(key);          // a fresher "now" copy supersedes any held one
                    if (IsConditionEligible(f.Condition)) nowBatch.Add(p);
                }
                else
                {
                    _pendingRoutine[key] = p;             // coalesce by clause key
                }
            }

            if (speakNow)
            {
                string composed = Compose(nowBatch);
                if (composed.Length > 0) _speak(composed, AlertCue.None);
            }
        }

        // Join routine fragments into ONE natural spoken sentence: sort by Order, then append
        // each fragment's clean text, inserting ", " only where two clean phrases would
        // otherwise run together and collapsing any doubled separator left where a fragment was
        // filtered out. The structural "_base.*" fragments carry the real sentence punctuation;
        // the configurable clauses are separator-free semantic phrases ("K4YT logged",
        // "sending 73") so they also read well when a clause is delivered on its own boundary.
        // This never touches the operator's stored template -- only the spoken composition.
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
            // A clause left out at its own boundary (retimed / conditioned away) can leave a
            // ", ." seam where the surrounding "_base" chunks rejoin -- fold it back to a plain
            // sentence end.
            while (s.Contains(", .")) s = s.Replace(", .", ".");
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            s = s.TrimEnd(' ', ',', ';');
            // Belt-and-suspenders: if every contributing fragment was empty / separator-only, what
            // is left ("." / "," / ", ." / stray spaces) is not speech. Return "" so the caller
            // never nudges the screen reader with a lone punctuation mark.
            return HasSpeakableContent(s) ? s : "";
        }

        // True when a string carries at least one letter or digit -- real words, not just
        // separators / sentence punctuation left over from a fully-disabled routine line.
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
                // Stale receive status must not be spoken once TX has begun -- neither an
                // "after receive" nor a held "receive begins" routine line. A pending RxStart
                // NOTIFICATION is kept: the next receive period's start edge will deliver it.
                DropRoutineIf(p => p.When == SpeakWhen.AfterRx || p.When == SpeakWhen.RxStart);

                // TxStart delivery: release anything held for the physical transmit-start edge.
                FlushRoutineBucket(p => p.When == SpeakWhen.TxStart);
                FlushNotificationsIf(p => p.When == SpeakWhen.TxStart);
            }
            else if (falling)
            {
                // A held STRUCTURAL (_base.*) fragment rendered while transmitting describes a TX
                // that is now over -- drop it rather than utter a stale "Transmitting..." late.
                // A configurable clause ("sending 73", "K4YT logged") is not stale that way, and
                // one the operator deliberately timed to "After transmit ends" MUST be delivered
                // here, not dropped. AfterQso fragments are always kept for their own edge.
                DropRoutineIf(p => p.RenderedWhileTx && p.IsBase && p.When != SpeakWhen.AfterQso);

                // AfterTx always releases here. AfterQso releases here too when the QSO has
                // ALREADY ended during this over (Codex #11). RxStart also releases here: the
                // transmit falling edge IS the start of a receive period, so a "receive begins"
                // item held through the over is delivered now (the new-slot tick would also
                // deliver it; whichever fires first wins, the other finds nothing).
                FlushRoutineBucket(p => p.When == SpeakWhen.AfterTx || p.When == SpeakWhen.RxStart
                    || (p.When == SpeakWhen.AfterQso && !_qsoActive));
                FlushNotificationsIf(p => p.When == SpeakWhen.AfterTx || p.When == SpeakWhen.RxStart
                    || (p.When == SpeakWhen.AfterQso && !_qsoActive));
            }
        }

        // A receive period has BEGUN -- the authoritative new-slot edge, fired from
        // DirectApplyDecodes' new-slot detection BEFORE that period's decodes are processed
        // (WsjtxClient.Direct.cs). Releases SpeakWhen.RxStart. Never spoken over a live over:
        // while physically transmitting a held RxStart routine line is dropped and the flush is
        // skipped -- the transmit falling edge then owns the release.
        public void OnReceivePeriodStarted()
        {
            if (_physicallyTransmitting)
            {
                DropRoutineIf(p => p.When == SpeakWhen.RxStart);
                return;
            }
            FlushRoutineBucket(p => p.When == SpeakWhen.RxStart);
            FlushNotificationsIf(p => p.When == SpeakWhen.RxStart);
        }

        // The receive period finished AND its decodes were processed (end of the Direct
        // new-slot decode pass -- WsjtxClient.Direct.cs).
        public void OnReceiveCycleComplete()
        {
            // Don't speak stale RX status -- or an AfterRx notification -- over a live over.
            if (_physicallyTransmitting)
            {
                DropRoutineIf(p => p.When == SpeakWhen.AfterRx);
                return;
            }
            FlushRoutineBucket(p => p.When == SpeakWhen.AfterRx);
            FlushNotificationsIf(p => p.When == SpeakWhen.AfterRx);
        }

        // callInProg became null / non-null.
        public void OnQsoActiveChanged(bool active)
        {
            bool ending = !active && _qsoActive;
            _qsoActive = active;
            if (!ending) return;

            // If a transmission is still physically active as the QSO ends, do NOT release
            // AfterQso speech here -- routine OR notification. Speaking now would talk over the
            // live over (Codex #10 -- the notification flush used to run unconditionally). The
            // TX falling edge (OnPhysicalTxChanged(false)) owns that release and now flushes
            // both queues for AfterQso-when-QSO-already-ended.
            if (_physicallyTransmitting) return;

            FlushRoutineBucket(p => p.When == SpeakWhen.AfterQso);
            FlushNotificationsIf(p => p.When == SpeakWhen.AfterQso);
        }

        // ── Flush helpers ────────────────────────────────────────────────────────────────────

        // A held item whose SpeakCondition is not satisfied by the QSO state at the moment its
        // delivery boundary fires is DISCARDED here -- not spoken, OnSpoken not fired. E.g. a
        // DuringQsoOnly item still pending when the QSO has already ended, or an OutsideQsoOnly
        // item flushed while a QSO is (still/again) active. (When OnQsoActiveChanged(false)
        // drives the AfterQso flush, _qsoActive is already false, so an OutsideQsoOnly AfterQso
        // item correctly speaks then.)

        // Discard held routine fragments matching `pred` without speaking them (stale snapshot).
        private void DropRoutineIf(Func<Pending, bool> pred)
        {
            List<string> keys = null;
            foreach (var kv in _pendingRoutine)
                if (pred(kv.Value)) (keys ?? (keys = new List<string>())).Add(kv.Key);
            if (keys != null) foreach (var k in keys) _pendingRoutine.Remove(k);
        }

        // Deliver the held routine fragments whose boundary has arrived (`ready`): remove them
        // all, then compose the still-condition-eligible ones into ONE utterance and speak once.
        private void FlushRoutineBucket(Func<Pending, bool> ready)
        {
            List<string> keys = null;
            foreach (var kv in _pendingRoutine)
                if (ready(kv.Value)) (keys ?? (keys = new List<string>())).Add(kv.Key);
            if (keys == null) return;
            var batch = new List<Pending>();
            foreach (var k in keys)
            {
                var p = _pendingRoutine[k];
                _pendingRoutine.Remove(k);
                if (IsConditionEligible(p.Condition)) batch.Add(p);
            }
            string composed = Compose(batch);
            if (composed.Length > 0) _speak(composed, AlertCue.None);
        }

        private void FlushNotificationsIf(Func<Pending, bool> ready)
        {
            List<string> keys = null;
            foreach (var kv in _pendingNotifications)
            {
                if (!ready(kv.Value)) continue;
                (keys ?? (keys = new List<string>())).Add(kv.Key);
            }
            if (keys == null) return;
            foreach (var k in keys)
            {
                var p = _pendingNotifications[k];
                _pendingNotifications.Remove(k);
                if (!IsConditionEligible(p.Condition)) continue;
                _speak(p.Text, p.Cue);
                p.OnSpoken?.Invoke();
            }
        }
    }
}
