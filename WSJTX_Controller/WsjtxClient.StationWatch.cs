using System;
using System.Linq;
using WsjtxUdpLib.Messages.Out;

namespace WSJTX_Controller
{
    // Station Watch / Smart QSO Start (2.0.63) -- the glue between TargetMonitor
    // (StationWatch/TargetMonitor.cs, which owns zero UI/notification/transport dependency) and
    // the rest of WsjtxClient: feeding it decodes and receive-period boundaries, turning its
    // Observed facts into real NotificationCenter publishes, and turning a Smart Start "ready"
    // decision or an explicit Work Watched Station Now into the EXISTING ReplyTo hand-off. See
    // TargetMonitor's own class comment for the ownership split this file is careful not to blur.
    public partial class WsjtxClient
    {
        private readonly TargetMonitor _stationWatch = new TargetMonitor(TargetPurpose.StationWatch);
        private readonly TargetMonitor _smartStart = new TargetMonitor(TargetPurpose.SmartStart);

        // Race-safety gate ("Work Now versus Smart Start becoming ready at the same time: one
        // atomic start-request gate; only one wins"). WinForms' single UI thread means the only
        // real hazard is two observations landing in the SAME synchronous pass (e.g. a target's
        // CQ and a silence threshold both resolving out of the same final decode batch) each
        // trying to fire a start; this flag, held for the duration of one synchronous dispatch,
        // is exactly what that needs. ReplyTo's own success-only commit + _contactEpoch
        // (Codex Audit 04) independently guards the actual async REPLY commit, so this is a
        // belt-and-suspenders pairing, not the only safety net.
        private bool _targetMonitorStartDispatching;

        public bool StationWatchActive => _stationWatch.IsActive;
        public string StationWatchTarget => _stationWatch.TargetCall;

        // Called once from the constructor.
        private void InitTargetMonitors()
        {
            _stationWatch.Observed += HandleTargetObservation;
            _smartStart.Observed += HandleTargetObservation;
        }

        // ── Station Watch: start / stop / replace ───────────────────────────────────────────────

        public void StartOrReplaceStationWatch(string call)
        {
            if (string.IsNullOrEmpty(call)) return;
            _stationWatch.Start(call, CurrentBandStr, mode, _directExpectedSessionToken);
            Notify?.SetStationWatchActive(true);
        }

        public void ToggleStationWatch(string focusedOrSelectedCall)
        {
            if (_stationWatch.IsActive)
            {
                StopStationWatch();
                return;
            }
            if (string.IsNullOrEmpty(focusedOrSelectedCall))
            {
                StatusView.ShowMessage("No station selected to watch", false);
                return;
            }
            StartOrReplaceStationWatch(focusedOrSelectedCall);
        }

        public void StopStationWatch()
        {
            if (!_stationWatch.IsActive) return;
            _stationWatch.Stop();
            Notify?.SetStationWatchActive(false);
        }

        // Work Watched Station Now (default Ctrl+Shift+Enter). Uses the STORED watched target and
        // its most recent usable decode -- never current list focus, never a synthesized message.
        public void WorkWatchedStationNow()
        {
            if (!_stationWatch.IsActive)
            {
                StatusView.ShowMessage("Station Watch is not active", false);
                return;
            }
            if (_stationWatch.LastUsableDecode == null)
            {
                Notify?.Publish(new SmartStartWaitingEvent(_stationWatch.TargetCall, "no decode yet"));
                StatusView.ShowMessage($"Waiting for another decode from {_stationWatch.TargetCall}", false);
                return;
            }
            RequestTargetMonitorStart(_stationWatch);
        }

        // ── Smart QSO Start: capture from Enter ─────────────────────────────────────────────────

        // Called from the existing Enter-on-a-station dispatch (dialogTimer2_Tick) when Smart QSO
        // Start is enabled and the press was a real operator selection -- captures the call and
        // its source decode instead of replying immediately. Returns true if the press was
        // captured this way (caller must not also call ReplyTo for it).
        private bool TryCaptureSmartStart(string call, EnqueueDecodeMessage dmsg)
        {
            if (!ctrl.smartQsoStartEnabled) return false;
            if (string.IsNullOrEmpty(call) || dmsg == null) return false;

            _smartStart.SilenceThreshold = ctrl.smartStartSilencePeriods;
            _smartStart.Start(call, CurrentBandStr, mode, _directExpectedSessionToken);
            // Seed the freshly-started monitor with the exact decode the operator selected --
            // ObserveDecode would otherwise only learn about the target from FUTURE decodes.
            _smartStart.ObserveDecode(dmsg, IsEvenSlotNow(), myCall);
            if (_smartStart.ConsumeReadyToStart())
                RequestTargetMonitorStart(_smartStart);
            else
                StatusView.ShowMessage($"Will work {call} when appropriate", false);
            return true;
        }

        private bool IsEvenSlotNow() => (_directLastSlotSeen % 2UL) == 0UL;

        // ── Shared feed points, called from the Direct decode loop ──────────────────────────────

        // Called for every processed decode (WsjtxClient.Direct.cs), regardless of whether it
        // ends up in the reply queue -- Station Watch and Smart Start both need the full raw feed,
        // not just queued candidates.
        private void FeedTargetMonitors(EnqueueDecodeMessage enq, bool evenSlot)
        {
            if (_stationWatch.IsActive) _stationWatch.ObserveDecode(enq, evenSlot, myCall);
            if (_smartStart.IsActive)
            {
                _smartStart.ObserveDecode(enq, evenSlot, myCall);
                if (_smartStart.ConsumeReadyToStart()) RequestTargetMonitorStart(_smartStart);
            }
        }

        // Called once per real, completed receive-period boundary (the exact same signal
        // Notify.OnPeriodBoundary() already uses -- WsjtxClient.Direct.cs's own slot-advance
        // detection). `weTransmittedThisSlot` is Jimmy's own `transmitting` flag at that moment.
        private void FeedTargetMonitorsPeriodComplete(ulong slot, bool evenSlot, bool weTransmittedThisSlot)
        {
            if (!_smartStart.IsActive) return;
            _smartStart.OnReceivePeriodComplete(slot, evenSlot, CurrentBandStr, mode, _directExpectedSessionToken, weTransmittedThisSlot);
            if (_smartStart.ConsumeReadyToStart()) RequestTargetMonitorStart(_smartStart);
        }

        // Band or mode change, or a full session reset (ResetBandSession -- see its own comment):
        // stop Station Watch rather than leave a hidden dormant watch, and clear Smart Start's
        // monitoring/silence state. Both TargetMonitor instances independently no-op if not active.
        private void StopTargetMonitorsForContextChange()
        {
            if (_stationWatch.IsActive)
            {
                _stationWatch.Stop();
                Notify?.SetStationWatchActive(false);
            }
            _smartStart.Stop(announce: false);
        }

        // Escape / Alt+H: cancel any pending automatic start, but do NOT stop a receive-only
        // Station Watch (operator decision) -- only the explicit toggle hotkey stops that. Smart
        // Start has no persistent UI of its own, so its whole capture is dropped rather than risk
        // a later surprise transmission from stale readiness ("be careful not to allow Halt to
        // leave an actionable pending automatic start behind").
        public void CancelStationWatchPendingStart()
        {
            _stationWatch.CancelPendingStart();
            _smartStart.Stop(announce: false);
        }

        // CAT loss / TX disabled: Smart Start becomes non-actionable and its silence count resets;
        // a receive-only Station Watch is untouched (it may continue decoding regardless).
        private void NotifyTargetMonitorsNonActionable()
        {
            _smartStart.OnSmartStartNonActionable();
        }

        // ── The one atomic start-request gate ───────────────────────────────────────────────────

        private void RequestTargetMonitorStart(TargetMonitor monitor)
        {
            if (monitor?.LastUsableDecode == null) return;
            if (callInProg != null) return;                 // already mid-QSO -- never step on it
            if (_targetMonitorStartDispatching) return;      // one atomic gate; only one wins
            _targetMonitorStartDispatching = true;
            try
            {
                Notify?.Publish(new SmartStartCallStartingEvent(monitor.TargetCall));
                ReplyTo(monitor.LastUsableDecode);
            }
            finally
            {
                _targetMonitorStartDispatching = false;
            }
        }

        // ── TargetObservation -> NotificationCenter ─────────────────────────────────────────────

        private void HandleTargetObservation(TargetObservation obs)
        {
            switch (obs.Kind)
            {
                case TargetObservationKind.WatchStarted:
                    Notify?.Publish(new StationWatchLifecycleEvent(NotificationEventType.StationWatchStarted, obs.Target));
                    return;
                case TargetObservationKind.WatchStopped:
                    Notify?.Publish(new StationWatchLifecycleEvent(NotificationEventType.StationWatchStopped, obs.Target));
                    return;
                case TargetObservationKind.SmartStartWaiting:
                    Notify?.Publish(new SmartStartWaitingEvent(obs.Target, obs.Value));
                    return;
                case TargetObservationKind.SmartStartTargetAvailable:
                    Notify?.Publish(new SmartStartTargetAvailableEvent(obs.Target));
                    return;
            }

            // The CQ/addressing/report/RRR/RR73/73/peer-observed narration family is Station
            // Watch's own feature (Smart Start is meant to stay a quiet background auto-pilot --
            // it only ever speaks Waiting/Available/CallStarting above).
            if (obs.Purpose != TargetPurpose.StationWatch) return;

            if (obs.Kind == TargetObservationKind.TargetAmbiguous)
            {
                Notify?.Publish(new StationWatchAmbiguousEvent(obs.Target));
                return;
            }

            string phrase = BuildActivityPhrase(obs);
            if (string.IsNullOrEmpty(phrase)) return;
            Notify?.Publish(new StationWatchActivityEvent(phrase, obs.Target, obs.Peer, obs.Value, obs.Kind.ToString()));
        }

        // Natural spoken phrasing -- no S/R shorthand, no "report" filler word (spec). Only what
        // TargetMonitor actually classified; nothing here invents content beyond what Observed
        // already carries.
        private static string BuildActivityPhrase(TargetObservation obs)
        {
            switch (obs.Kind)
            {
                case TargetObservationKind.TargetCq:
                    return $"{obs.Target} CQ.";
                case TargetObservationKind.TargetAddressingUs:
                    return $"{obs.Target} calling you.";
                case TargetObservationKind.TargetAddressingOther:
                    return $"{obs.Target} working {obs.Peer}.";
                case TargetObservationKind.TargetReport:
                case TargetObservationKind.TargetRReport:
                    return $"{obs.Target} working {obs.Peer}, {SpokenReport(obs.Value)}.";
                case TargetObservationKind.TargetRrr:
                    return $"{obs.Target} RRR.";
                case TargetObservationKind.TargetRr73:
                    return $"{obs.Target} RR73.";
                case TargetObservationKind.Target73:
                    return $"{obs.Target} 73.";
                case TargetObservationKind.OtherPartyObserved:
                    return $"{obs.Peer} {SpokenReport(obs.Value)}.";
                default:
                    return null;
            }
        }

        // "-08" -> "minus 8", "+12" -> "plus 12", "R-05" -> "R minus 5". Anything that isn't a
        // report/R-report shape (RRR/RR73/73/a grid/free text) is returned unchanged -- it already
        // reads naturally as-is.
        private static string SpokenReport(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return payload ?? "";
            bool rogered = payload.Length > 2 && payload[0] == 'R' && (payload[1] == '+' || payload[1] == '-');
            string core = rogered ? payload.Substring(1) : payload;
            if (core.Length < 2 || (core[0] != '+' && core[0] != '-')) return payload;
            string digits = core.Substring(1).TrimStart('0');
            if (digits.Length == 0) digits = "0";
            if (!digits.All(char.IsDigit)) return payload;
            string word = core[0] == '-' ? "minus" : "plus";
            return rogered ? $"R {word} {digits}" : $"{word} {digits}";
        }
    }
}
