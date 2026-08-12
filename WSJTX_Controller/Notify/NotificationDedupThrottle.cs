using System;
using System.Collections.Generic;

namespace WSJTX_Controller
{
    // Generalizes the existing per-call cooldown idiom already proven at
    // WsjtxClient.Display.cs's IsAlertCooledDown + WsjtxClient.cs's _awardAlertTimes/
    // AwardAlertCooldownSecs into one reusable, per-(event type, identity) tracker, plus a
    // per-event-type global throttle. Two independent, stateless timestamp-comparison checks --
    // no Timer needed for this (WinForms has no React-style render batching to coalesce
    // against; a future batched/summarized event, if one is ever added, would layer a one-shot
    // Timer alongside this class to flush a pending count after N ms of silence, same shape as
    // WsjtxClient.cs's existing _slotAnalysisWatchdog -- not needed for this migration slice).
    public class NotificationDedupThrottle
    {
        // (EventType, DedupKey) -> last announced UTC time. Suppresses a repeat of the SAME
        // identity within NotificationPolicy.RepeatSeconds.
        private readonly Dictionary<(NotificationEventType, string), DateTime> _lastByIdentity
            = new Dictionary<(NotificationEventType, string), DateTime>();

        // EventType -> last announced UTC time, regardless of identity. Suppresses ANY
        // announcement of this type within NotificationPolicy.ThrottleMilliseconds of the
        // previous one -- the global "don't flood" backstop (e.g. AwardsNeeded during a
        // pileup of many different callsigns in quick succession).
        private readonly Dictionary<NotificationEventType, DateTime> _lastByType
            = new Dictionary<NotificationEventType, DateTime>();

        // (EventType, DedupKey) -> last actually-DELIVERED formatted text. Backs
        // NotificationPolicy.SuppressUnchanged -- a content check, deliberately independent of
        // _lastByIdentity's time-based one above (RepeatSeconds=0 with SuppressUnchanged=true
        // means "never repeat the exact same words, but say it again the instant it changes").
        // Checked separately from ShouldAnnounce/RecordFired below: the formatted text doesn't
        // exist yet at that point in NotificationCenter.Publish (dedup/throttle run BEFORE
        // NotificationTemplateEngine.Format, deliberately, so a suppressed event never pays for
        // formatting it would never speak) -- see IsUnchanged/RecordText.
        private readonly Dictionary<(NotificationEventType, string), string> _lastTextByIdentity
            = new Dictionary<(NotificationEventType, string), string>();

        public bool ShouldAnnounce(NotificationEventType type, string dedupKey, NotificationPolicy policy)
        {
            DateTime now = DateTime.UtcNow;

            if (policy.RepeatSeconds > 0)
            {
                var key = (type, dedupKey ?? "");
                if (_lastByIdentity.TryGetValue(key, out DateTime lastForIdentity) &&
                    (now - lastForIdentity).TotalSeconds < policy.RepeatSeconds)
                    return false;
            }

            if (policy.ThrottleMilliseconds > 0 &&
                _lastByType.TryGetValue(type, out DateTime lastForType) &&
                (now - lastForType).TotalMilliseconds < policy.ThrottleMilliseconds)
                return false;

            return true;
        }

        public void RecordFired(NotificationEventType type, string dedupKey)
        {
            DateTime now = DateTime.UtcNow;
            _lastByIdentity[(type, dedupKey ?? "")] = now;
            _lastByType[type] = now;
        }

        // True when formattedText is byte-identical to the last text actually delivered for
        // this identity -- only meaningful (and only ever called) when
        // NotificationPolicy.SuppressUnchanged is true. A brand-new identity (nothing recorded
        // yet) is never "unchanged".
        public bool IsUnchanged(NotificationEventType type, string dedupKey, string formattedText) =>
            _lastTextByIdentity.TryGetValue((type, dedupKey ?? ""), out string last) && last == formattedText;

        public void RecordText(NotificationEventType type, string dedupKey, string formattedText) =>
            _lastTextByIdentity[(type, dedupKey ?? "")] = formattedText;
    }
}
