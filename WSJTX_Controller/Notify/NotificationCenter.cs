using System;
using System.Collections.Generic;

namespace WSJTX_Controller
{
    // The façade functional modules call: Publish(event) is the ONLY method business logic
    // ever calls on this class. Everything else (policy lookup, dedup/throttle, deferred
    // delivery, template resolution, delivery) is an internal implementation detail. No
    // business logic lives here -- this class only glues together NotificationSettings/
    // NotificationDedupThrottle/NotificationTemplateEngine/INotificationDelivery, each
    // independently testable.
    //
    // Speech coordination, 2026-09-02 (Items 1 & 2): Publish() formats an eligible event and
    // hands it to the one SpeechCoordinator (this.Speech) with the policy's SpeakWhen and an
    // effective priority. The coordinator -- shared with WsjtxClient's routine RX/TX/QSO status
    // speech -- owns every deferral, obsolescence and coalescing decision from that point, and
    // is released only by WsjtxClient's real state transitions forwarded through
    // OnPeriodBoundary() (a receive cycle completed) / OnTransmittingChanged() (physical TX
    // edge) / OnQsoActiveChanged() (callInProg cleared). Never a timer.
    public class NotificationCenter
    {
        private readonly NotificationSettings _settings;
        private readonly INotificationDelivery _delivery;
        private readonly NotificationDedupThrottle _dedupThrottle = new NotificationDedupThrottle();

        // 2026-09-02 (Items 1 & 2): the ONE speech-coordination authority. Every delivered
        // notification -- and, via WsjtxClient, every routine RX/TX/QSO status line -- goes
        // through this single coordinator, which owns SpeakWhen deferral, obsolescence/
        // coalescing, and the Critical-priority bypass. The old per-NotificationCenter
        // NextPeriodBoundary/DeferWhileTransmitting hold-queue was removed with this change; its
        // public entry points (OnPeriodBoundary / OnTransmittingChanged) now simply forward to
        // the coordinator, so there is exactly one deferral mechanism, not two.
        private readonly SpeechCoordinator _coordinator;
        public SpeechCoordinator Speech => _coordinator;

        // Notification History sink, 2026-09-03 (Item 1): a delivered notification's fact is
        // recorded HERE, immediately, before the coordinator decides whether/when to speak it --
        // so a deferred/Never notification still shows in Ctrl+Shift+H. (Routine status records
        // its own history in Controller.RenderStatusVisible.) Null in tests that don't care.
        private readonly Action<string> _recordHistory;

        // scheduler/clock (2026-09-11): default to the real, fully-working production
        // implementations when omitted -- NOT a no-op/synchronous stand-in -- so an existing or
        // future caller that does not pass them explicitly still gets real batching, never a
        // silently-disabled version of it. Production wiring (WsjtxClient.cs) passes them
        // explicitly anyway, for clarity and so it can own their lifetime.
        public NotificationCenter(NotificationSettings settings, INotificationDelivery delivery,
            Action<string> recordHistory = null, INowBatchScheduler scheduler = null, IMonotonicClock clock = null,
            Action<string> logDiagnostic = null)
        {
            _settings = settings;
            _delivery = delivery;
            _recordHistory = recordHistory;
            _coordinator = new SpeechCoordinator((text, cue) => _delivery.Announce(text, cue),
                scheduler ?? new WinFormsNowBatchScheduler(), clock ?? new SystemMonotonicClock(), logDiagnostic);
            _coordinator.UpdateJoinOrder(
                (System.Collections.Generic.IReadOnlyList<NotificationEventType>)settings?.NotificationJoinOrder
                    ?? NotificationDefaults.DefaultJoinOrder);
            if (settings != null)
                _coordinator.UpdateJoinTiming(settings.NotificationJoinQuietMs, settings.NotificationJoinMaxMs, settings.NotificationJoinGapMs);
        }

        // Options > Notifications > "Notification order..." calls this after the operator saves a
        // new order, so the change takes effect immediately without restarting Jimmy Next.
        public void UpdateJoinOrder(System.Collections.Generic.IReadOnlyList<NotificationEventType> order) =>
            _coordinator.UpdateJoinOrder(order);

        public void Publish(INotificationEvent evt)
        {
            if (evt == null) return;
            if (!_settings.Policies.TryGetValue(evt.EventType, out NotificationPolicy policy)) return;
            if (!policy.Enabled) return;
            // Time/count-based dedup and throttle run BEFORE any formatting -- an event this gate
            // rejects never even reaches the coordinator, so it can't leak out later at a flush.
            if (!_dedupThrottle.ShouldAnnounce(evt.EventType, evt.DedupKey, policy)) return;

            Deliver(evt, policy);
        }

        // Forwarded to the one coordinator. Kept as the public names WsjtxClient already calls;
        // OnPeriodBoundary now means "a receive cycle completed" (SpeakWhen.AfterRx).
        public void OnPeriodBoundary() => _coordinator.OnReceiveCycleComplete();
        // 2026-09-09: the "receive period begins" edge (SpeakWhen.RxStart), forwarded from
        // DirectApplyDecodes' new-slot detection before that period's decodes are processed.
        public void OnReceivePeriodStarted() => _coordinator.OnReceivePeriodStarted();
        public void OnTransmittingChanged(bool transmitting) => _coordinator.OnPhysicalTxChanged(transmitting);
        public void OnQsoActiveChanged(bool active) => _coordinator.OnQsoActiveChanged(active);
        // 2026-09-11 semantic-boundary correction: the confirmed end of ONE decode-processing
        // pass (WsjtxClient.Direct.cs's DirectApplyDecodes, called AFTER ServicePendingAutoStart)
        // -- see SpeechCoordinator.OnDecodePassComplete's own comment for why OnPeriodBoundary
        // above fires too early to be that signal on its own.
        public void OnDecodePassComplete() => _coordinator.OnDecodePassComplete();

        // Station Watch (2.0.63): forwarded to the coordinator's routine-suppression gate --
        // see SpeechCoordinator.SetStationWatchSuppression's own comment.
        public void SetStationWatchActive(bool active) => _coordinator.SetStationWatchSuppression(active);

        // Every notification type whose speech should bypass the Station-Watch suppression gate
        // (Station Watch/Smart Start's own observations) -- everything else is routine/ordinary
        // notification speech and is muted while a receive-only watch is active. Reused
        // (2026-09-11) as the JOINABLE category set for SpeechCoordinator's Now-batch/notification-
        // join-order feature -- this is the same, already-established "Station Watch/Smart Start
        // speech" boundary in the code, so the join-order feature does not need (and must not
        // maintain) a second copy of the same eleven-type list.
        internal static readonly HashSet<NotificationEventType> WatchEventTypes = new HashSet<NotificationEventType>
        {
            NotificationEventType.StationWatchStarted,
            NotificationEventType.StationWatchStopped,
            NotificationEventType.StationWatchActivity,
            NotificationEventType.StationWatchAmbiguous,
            NotificationEventType.SmartStartWaiting,
            NotificationEventType.SmartStartTargetAvailable,
            NotificationEventType.SmartStartCallStarting,
            NotificationEventType.SmartStartArmed,
            NotificationEventType.SmartStartTargetBusy,
            NotificationEventType.SmartStartYielded,
            NotificationEventType.SmartStartEngaged,
        };

        private void Deliver(INotificationEvent evt, NotificationPolicy policy)
        {
            // {Time} is the one variable every notification type carries for free -- identical
            // for all of them, so it's injected here once rather than duplicated into every
            // event class's own ToTokens(). Formatting (and the SuppressUnchanged comparison
            // below, which needs the formatted text) only ever happens here, for an event that
            // has ALREADY cleared Enabled/dedup/throttle/timing -- never for one that's about to
            // be suppressed, per the "don't build speech nobody will hear" performance rule.
            var tokens = new Dictionary<string, string>(evt.ToTokens())
            {
                [NotificationVariableRegistry.TimeKey] = DateTime.Now.ToString("h:mm tt"),
            };
            string text = NotificationTemplateEngine.Format(policy.Template, tokens);
            if (string.IsNullOrEmpty(text)) return;

            if (policy.SuppressUnchanged && _dedupThrottle.IsUnchanged(evt.EventType, evt.DedupKey, text))
                return;   // identical to the last thing actually said for this identity -- stay quiet, don't touch RecordFired/RecordText either

            // Record the fact NOW, before the coordinator -- a deferred, coalesced, or Never
            // notification still shows in Notification History (it was presented; whether it
            // was spoken is a separate question).
            _recordHistory?.Invoke(text);

            // A genuine ErrorSeverity.Error is escalated to Critical regardless of the configured
            // policy Priority (a Warning respects the policy) -- this is what lets ErrorWarning's
            // Warning and Error call sites share one policy row. effectivePriority is what the
            // coordinator sees; it derives the AlertCue (None / Important / Critical) from it.
            bool isError = evt is ErrorWarningEvent errorEvent && errorEvent.Severity == ErrorSeverity.Error;
            NotificationPriority effectivePriority =
                (policy.Priority == NotificationPriority.Critical || isError)
                    ? NotificationPriority.Critical
                    : policy.Priority;

            // Dedup/throttle "last announced" bookkeeping is moved to the point of ACTUAL
            // speech (the onSpoken callback) rather than run unconditionally here. A discarded
            // occurrence -- SpeakWhen.Never, or DuringQso.Suppress while a QSO is active, or a
            // deferred item that becomes QSO-suppressed by the time it would flush -- must not
            // advance RepeatSeconds / ThrottleMilliseconds / SuppressUnchanged state, or it
            // would silence a LATER occurrence that genuinely should be heard (Codex #12).
            //
            // 2026-09-09 multi-delivery: a policy can name MORE THAN ONE delivery boundary
            // (EffectiveSpeakWhenSet). Submit once per boundary, each with its OWN coordinator
            // identity ("EventType|DedupKey|<when>"), so the boundaries are independent -- one
            // timing never overwrites another's pending copy, and each occurrence is delivered
            // at most once (the coordinator removes a pending item when it flushes it). The
            // dedup/throttle "last announced" bookkeeping is advanced by the FIRST boundary
            // that actually speaks (a shared once-guard), so several boundaries for one Publish
            // do not multiply-advance the repeat window.
            // 2026-09-09 per-notification status-area delivery choice (P7):
            //   Normal          -> configured boundaries, coalesce by the per-occurrence DedupKey.
            //   Send immediately -> collapse the boundaries to Now (visible line + history are
            //                       already immediate; this makes the spoken nudge immediate too).
            //   Latest only      -> coalesce by event TYPE alone (empty DedupKey), so a newer
            //                       occurrence replaces an older still-pending one of the same
            //                       type. Only this type's own pending items are affected.
            string dedupKey = policy.StatusDelivery == NotificationStatusDelivery.LatestOnly
                ? ""
                : (evt.DedupKey ?? "");
            bool recorded = false;
            Action onSpokenOnce = () =>
            {
                if (recorded) return;
                recorded = true;
                _dedupThrottle.RecordFired(evt.EventType, evt.DedupKey);
                if (policy.SuppressUnchanged)
                    _dedupThrottle.RecordText(evt.EventType, evt.DedupKey, text);
            };
            bool isWatch = WatchEventTypes.Contains(evt.EventType);

            // Notification-joining support (2026-09-11): correlation data for SpeechCoordinator's
            // Posture/Observation supersession, present only for the seven SmartStart lifecycle
            // types (see NotificationEvents.cs's ISmartStartCorrelatedEvent). null for everything
            // else, including Station Watch's own four observation types -- those always join,
            // never supersede, so they need no correlation.
            SmartStartCorrelation correlation = evt is ISmartStartCorrelatedEvent corr
                ? new SmartStartCorrelation
                {
                    Target = corr.Target,
                    ArmGeneration = corr.ArmGeneration,
                    StateSeq = corr.StateSeq,
                    Group = corr.Group,
                }
                : null;

            // Critical bypasses delivery timing entirely (spoken the instant it is submitted),
            // so submitting it once per boundary would just speak it several times. "Send
            // immediately" likewise collapses to a single Now submission. Otherwise use the
            // full configured set.
            var boundaries =
                effectivePriority == NotificationPriority.Critical ? new[] { policy.SpeakWhen }
                : policy.StatusDelivery == NotificationStatusDelivery.SendImmediately ? new[] { SpeakWhen.Now }
                : policy.EffectiveSpeakWhenSet();
            foreach (SpeakWhen when in boundaries)
            {
                _coordinator.SubmitNotification(
                    identity: evt.EventType + "|" + dedupKey + "|" + when,
                    text: text,
                    when: when,
                    priority: effectivePriority,
                    condition: policy.Condition,
                    onSpoken: onSpokenOnce,
                    isWatchCategory: isWatch,
                    eventType: evt.EventType,
                    correlation: correlation);
            }
        }
    }
}
