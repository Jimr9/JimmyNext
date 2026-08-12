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
    // Deferred-delivery addition, 2026-08-12 (configurable notification timing): a policy whose
    // Timing is NextPeriodBoundary, or whose DeferWhileTransmitting is true while a transmission
    // is in progress, doesn't deliver from Publish() at all -- it's held in _pending (keyed by
    // (EventType, DedupKey), latest publish of the same identity simply overwrites the held one)
    // until WsjtxClient calls OnPeriodBoundary()/OnTransmittingChanged() from a REAL FT8/FT4
    // state transition it already tracks (never a new arbitrary timer -- see those methods' own
    // comments for exactly which transitions). This is the one and only place a notification can
    // be released, so there is no way for the same pending entry to be delivered twice.
    public class NotificationCenter
    {
        private readonly NotificationSettings _settings;
        private readonly INotificationDelivery _delivery;
        private readonly NotificationDedupThrottle _dedupThrottle = new NotificationDedupThrottle();

        private readonly Dictionary<(NotificationEventType, string), (INotificationEvent evt, NotificationPolicy policy)> _pending
            = new Dictionary<(NotificationEventType, string), (INotificationEvent, NotificationPolicy)>();
        private bool _transmitting;

        public NotificationCenter(NotificationSettings settings, INotificationDelivery delivery)
        {
            _settings = settings;
            _delivery = delivery;
        }

        public void Publish(INotificationEvent evt)
        {
            if (evt == null) return;
            if (!_settings.Policies.TryGetValue(evt.EventType, out NotificationPolicy policy)) return;
            if (!policy.Enabled) return;
            // Time/count-based dedup and throttle run BEFORE any formatting or deferral --
            // an event this gate rejects never even gets held in _pending, so it can't leak
            // out later at a period boundary either.
            if (!_dedupThrottle.ShouldAnnounce(evt.EventType, evt.DedupKey, policy)) return;

            if (policy.Timing == NotificationTiming.NextPeriodBoundary || (policy.DeferWhileTransmitting && _transmitting))
            {
                _pending[(evt.EventType, evt.DedupKey ?? "")] = (evt, policy);
                return;
            }

            Deliver(evt, policy);
        }

        // Call exactly when a receive period has just completed -- WsjtxClient.
        // NotifyPeriodBoundary() is the one place that should call this, itself driven by a
        // real state transition (the UDP path's decoding-flag flip, or Direct mode's new-slot
        // detection), not a timer, and not FT8/FT4-duration-specific -- both modes' transports
        // already resolve "a period just ended" to this same signal regardless of whether that
        // period was 15s (FT8) or 7.5s (FT4). Releases every NextPeriodBoundary-timed pending
        // entry that isn't ALSO still waiting out an in-progress transmission.
        public void OnPeriodBoundary()
        {
            FlushWhere(p => !p.DeferWhileTransmitting || !_transmitting);
        }

        // Call on every real transmitting-flag transition (ProcessTxStart/ProcessTxEnd on the
        // UDP path, DirectApplyStatus's transmittingChanged on the Direct path). Only
        // Immediate-timed entries are released here when transmitting just ended -- a
        // NextPeriodBoundary entry keeps waiting for the next real OnPeriodBoundary() call even
        // if Tx happens to end first, so a "batched" notification's cadence is always the
        // period grid, never incidentally shortened by however long that particular over ran.
        public void OnTransmittingChanged(bool transmitting)
        {
            _transmitting = transmitting;
            if (!transmitting)
                FlushWhere(p => p.Timing == NotificationTiming.Immediate);
        }

        private void FlushWhere(Func<NotificationPolicy, bool> ready)
        {
            List<(NotificationEventType, string)> toFlush = null;
            foreach (var kv in _pending)
            {
                if (!ready(kv.Value.policy)) continue;
                (toFlush ?? (toFlush = new List<(NotificationEventType, string)>())).Add(kv.Key);
            }
            if (toFlush == null) return;

            // Remove before delivering, not after -- a policy edited mid-flush (e.g. Options
            // closed with OK between two pending entries) can never see a half-updated _pending,
            // and nothing here can observe or re-flush an entry that's already been handed off.
            foreach (var key in toFlush)
            {
                var (evt, policy) = _pending[key];
                _pending.Remove(key);
                Deliver(evt, policy);
            }
        }

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

            bool important = policy.Priority == NotificationPriority.Important;
            // ErrorSeverity.Error always gets the audible cue regardless of the configured
            // policy Priority -- Warning respects the policy. This is what lets both of
            // ErrorWarning's existing migrated call sites (one historically sound:false, one
            // sound:true) share a single policy row and still reproduce their exact prior
            // behavior -- see NotificationDefaults.cs's ErrorWarning entry for the full
            // rationale.
            if (evt is ErrorWarningEvent errorEvent && errorEvent.Severity == ErrorSeverity.Error)
                important = true;

            _delivery.Announce(text, important);
            _dedupThrottle.RecordFired(evt.EventType, evt.DedupKey);
            if (policy.SuppressUnchanged)
                _dedupThrottle.RecordText(evt.EventType, evt.DedupKey, text);
        }
    }
}
