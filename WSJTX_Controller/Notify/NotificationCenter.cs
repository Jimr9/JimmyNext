namespace WSJTX_Controller
{
    // The façade functional modules call: Publish(event) is the ONLY method business logic
    // ever calls on this class. Everything else (policy lookup, dedup/throttle, template
    // resolution, delivery) is an internal implementation detail. No business logic lives
    // here -- this class only glues together NotificationSettings/NotificationDedupThrottle/
    // NotificationTemplateEngine/INotificationDelivery, each already independently testable.
    public class NotificationCenter
    {
        private readonly NotificationSettings _settings;
        private readonly INotificationDelivery _delivery;
        private readonly NotificationDedupThrottle _dedupThrottle = new NotificationDedupThrottle();

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
            if (!_dedupThrottle.ShouldAnnounce(evt.EventType, evt.DedupKey, policy)) return;

            string text = NotificationTemplateEngine.Format(policy.Template, evt.ToTokens());
            if (string.IsNullOrEmpty(text)) return;

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
        }
    }
}
