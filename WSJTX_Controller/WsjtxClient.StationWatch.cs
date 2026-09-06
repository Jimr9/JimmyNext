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

        // Snapshot-finality guard (2.0.64 transmit-safety fix). When a monitor decides it is
        // ready, the automatic REPLY is NOT sent from inside that decode pass -- the monitor is
        // parked here and the dispatch is deferred for a few 1 s SNAPSHOT polls. Nexus's decoder
        // can spread one receive slot's decodes across the first ~3 snapshots after the slot
        // boundary; holding the REPLY for that window lets any late "target working another
        // station" decode for the just-completed period be ingested (and set BusyWithOther /
        // change context) so RevalidateForAutoStart can abort BEFORE anything transmits.
        private TargetMonitor _pendingAutoStart;
        private int _pendingAutoStartPollsRemaining;
        private const int PendingAutoStartFinalityPolls = 3;

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
            // Explicit operator "now" -- but still revalidated (fresh, in-context, not busy)
            // inside RequestTargetMonitorStart; it will decline with a status rather than
            // transmit from a stale or wrong-context stored decode.
            RequestTargetMonitorStart(_stationWatch, operatorOverride: true);
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
            // Seed the freshly-started monitor with the exact decode the operator selected.
            // SeedSelectedDecode records it as the decode to eventually reply from, but only
            // treats it as genuine current evidence (parity, live-evidence, CQ/73/RR73
            // readiness) when it is still fresh -- a stale queued/list selection identifies
            // WHICH station to work and nothing more, and Smart Start then waits for a real
            // live decode before it can authorize any transmission (2.0.64: the V51WW failure
            // was a ~54 s-old RR73 being manufactured into live "target available" evidence).
            _smartStart.SeedSelectedDecode(dmsg, DateTime.UtcNow, myCall);
            if (_smartStart.ConsumeReadyToStart())
                ArmPendingAutoStart(_smartStart);
            else
                StatusView.ShowMessage($"Will work {call} when appropriate", false);
            return true;
        }

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
                if (_smartStart.AwaitingEngagement)
                    ServiceSmartStartAwaitingEngagement();
                else if (_smartStart.ConsumeReadyToStart())
                    ArmPendingAutoStart(_smartStart);
            }
        }

        // Smart Start has dispatched its first call and is now CALLING the target (see
        // TargetMonitor.EnterAwaitingEngagement / WsjtxClient.ReplyTo). This runs after every
        // fresh decode while that phase is active:
        //   * the target addressed our callsign -> Smart Start is finished; the normal QSO
        //     sequencer already owns callInProg, so Smart Start just gets out of the way, OR
        //   * fresh activity shows the target working (or being worked by) another station
        //     before it answered us -> yield our call and stay armed for the same target.
        private void ServiceSmartStartAwaitingEngagement()
        {
            if (_smartStart.EngagedUs)
            {
                DebugOutput($"{Time()} [SMART] {_smartStart.TargetCall} answered our call -- Smart Start done; normal QSO sequencing owns it");
                _smartStart.Stop(announce: false);
                return;
            }
            if (_smartStart.BusyWithOther)
                YieldSmartStartToOtherQso();
        }

        // The Smart Start target started/continued a QSO with someone else before answering us.
        // Cease our current call exactly the way Escape/Alt+H does (AbortContact's ordered
        // teardown, MINUS stopping any Station Watch), then drop Smart Start back to armed/
        // waiting for the SAME target. It resumes only on a genuine availability signal --
        // TargetMonitor keeps BusyWithOther set across a mere peer change, and only a target
        // CQ / 73 / RR73 / addressing-us decode clears it and re-signals readiness.
        private void YieldSmartStartToOtherQso()
        {
            string target = _smartStart.TargetCall;
            // Only unwind OUR OWN call to this exact target -- never touch an unrelated contact.
            if (!string.Equals(callInProg, target, StringComparison.OrdinalIgnoreCase))
            {
                _smartStart.ReturnToWaiting();
                return;
            }
            DebugOutput($"{Time()} [SMART] {target} is working another station before answering us -- ceasing our call, staying armed");
            RequeueAbortedCall();   // while callInProg / replyDecode are still valid
            CancelQso();            // clears QSO state + bumps _contactEpoch (in-flight REPLY can't re-commit)
            HaltAndDisableTx();     // HALT_TX + SET_TX_ENABLED 0
            ClearPendingAutoStart();
            _smartStart.ReturnToWaiting();
            Notify?.Publish(new SmartStartWaitingEvent(target, "working another station"));
        }

        // Called once per real, completed receive-period boundary (the exact same signal
        // Notify.OnPeriodBoundary() already uses -- WsjtxClient.Direct.cs's own slot-advance
        // detection). `weTransmittedThisSlot` is Jimmy's own `transmitting` flag at that moment.
        private void FeedTargetMonitorsPeriodComplete(ulong slot, bool evenSlot, bool weTransmittedThisSlot)
        {
            // Station Watch counts opportunities too (its own Work-Now freshness check reads
            // OpportunitiesSinceLiveEvidence) -- it just never signals an automatic start.
            if (_stationWatch.IsActive)
                _stationWatch.OnReceivePeriodComplete(slot, evenSlot, CurrentBandStr, mode, _directExpectedSessionToken, weTransmittedThisSlot);
            if (!_smartStart.IsActive) return;
            _smartStart.OnReceivePeriodComplete(slot, evenSlot, CurrentBandStr, mode, _directExpectedSessionToken, weTransmittedThisSlot);
            // While awaiting engagement (already calling) the readiness machinery is dormant --
            // OnReceivePeriodComplete won't SignalReady, but gate the consume too for clarity.
            if (!_smartStart.AwaitingEngagement && _smartStart.ConsumeReadyToStart())
                ArmPendingAutoStart(_smartStart);
        }

        // Band or mode change, or a full session reset (ResetBandSession -- see its own comment):
        // stop Station Watch rather than leave a hidden dormant watch, and clear Smart Start's
        // monitoring/silence state. Both TargetMonitor instances independently no-op if not active.
        private void StopTargetMonitorsForContextChange()
        {
            ClearPendingAutoStart();
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
            ClearPendingAutoStart();
            _stationWatch.CancelPendingStart();
            _smartStart.Stop(announce: false);
        }

        // CAT loss / TX disabled: Smart Start becomes non-actionable and its silence count resets;
        // a receive-only Station Watch is untouched (it may continue decoding regardless).
        private void NotifyTargetMonitorsNonActionable()
        {
            ClearPendingAutoStart();
            _smartStart.OnSmartStartNonActionable();
        }

        // ── Deferred auto-start (snapshot-finality guard) ───────────────────────────────────────

        private void ArmPendingAutoStart(TargetMonitor monitor)
        {
            if (monitor?.LastUsableDecode == null) return;
            // A repeated "ready" for the monitor already parked here is a no-op -- the finality
            // countdown keeps running rather than restarting every silent opportunity (which
            // would defer the dispatch forever). The revalidation in RequestTargetMonitorStart
            // still runs against the very latest state when the countdown finally elapses.
            if (ReferenceEquals(_pendingAutoStart, monitor)) return;
            _pendingAutoStart = monitor;
            _pendingAutoStartPollsRemaining = PendingAutoStartFinalityPolls;
        }

        private void ClearPendingAutoStart()
        {
            _pendingAutoStart = null;
            _pendingAutoStartPollsRemaining = 0;
        }

        // Called at the END of every DirectApplyDecodes pass (after that pass has ingested its
        // decodes and run the period-complete tick). Holds a parked auto-start for a few polls
        // so any late decode for the just-completed receive period is seen first, then dispatches
        // it through the one revalidating start gate -- which may still decline it.
        private void ServicePendingAutoStart()
        {
            if (_pendingAutoStart == null) return;
            if (!_pendingAutoStart.IsActive) { ClearPendingAutoStart(); return; }
            if (_pendingAutoStartPollsRemaining > 0) { _pendingAutoStartPollsRemaining--; return; }
            TargetMonitor monitor = _pendingAutoStart;
            ClearPendingAutoStart();
            RequestTargetMonitorStart(monitor, operatorOverride: false);
        }

        // ── The one atomic, revalidating start-request gate ─────────────────────────────────────

        private void RequestTargetMonitorStart(TargetMonitor monitor, bool operatorOverride)
        {
            if (monitor?.LastUsableDecode == null) return;
            if (callInProg != null) return;                 // already mid-QSO -- never step on it
            if (_targetMonitorStartDispatching) return;      // one atomic gate; only one wins

            // 2.0.64 transmit-safety: revalidate against CURRENT band/mode/session right now.
            // Stale historical evidence, a context change, a target now working someone else, or
            // a receive-finality straggler that landed during the deferral window all decline
            // here rather than transmit.
            AutoStartCheck check = monitor.RevalidateForAutoStart(
                DateTime.UtcNow, CurrentBandStr, mode, _directExpectedSessionToken, operatorOverride);
            if (check != AutoStartCheck.Ok)
            {
                string why = AutoStartDeclineText(monitor.TargetCall, check);
                if (operatorOverride)
                    StatusView.ShowMessage(why, false);
                else
                    Notify?.Publish(new SmartStartWaitingEvent(monitor.TargetCall, why));
                // Leave the monitor armed and watching -- a later fresh decode / cleared-busy
                // state can still start it; it simply did not fire this time.
                return;
            }

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

        private static string AutoStartDeclineText(string target, AutoStartCheck check)
        {
            switch (check)
            {
                case AutoStartCheck.TargetBusy:      return $"{target} is working another station";
                case AutoStartCheck.ContextChanged:  return $"Band or mode changed; not starting {target}";
                case AutoStartCheck.NoLiveEvidence:  return $"Waiting for a current decode from {target}";
                case AutoStartCheck.StaleEvidence:   return $"No recent decode from {target}; still watching";
                default:                             return $"No usable decode from {target} yet";
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
