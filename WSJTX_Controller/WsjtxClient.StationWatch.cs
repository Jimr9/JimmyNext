using System;
using System.Collections.Generic;
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

        // True while Smart Mode is armed on `call` (waiting or calling) -- CallQueueStore keeps
        // that station out of the list meanwhile.
        internal bool IsSmartModeWaitingOn(string call) =>
            _smartStart.IsActive && string.Equals(_smartStart.TargetCall, call, StringComparison.OrdinalIgnoreCase);

        // Smart Start ownership survives a temporary hand-off to the normal QSO sequencer.
        // Operator policy (2026-09-08): once Smart Start owns a target, it keeps owning it until
        // the QSO completes, the operator stops it, the Repeat Limit is reached, the operator
        // picks a different target, or another terminal condition ends it. When the target
        // ANSWERS us the monitor is Stopped and the normal sequencer runs the exchange -- but if
        // that exchange is then ceased because the target moved into a substantive exchange with
        // a third station (WsjtxClient.YieldActiveContactToOtherQso), Smart Start RESUMES
        // ownership of the SAME target. These two fields carry just what a resume needs across
        // the Stop(): which call originated under Smart Start, and its cumulative Repeat-Limit
        // calling-over count. _smartStartHandoffCall is null whenever the current/just-ended
        // contact did NOT originate under Smart Start (an ordinary manual QSO with Smart Start
        // merely enabled must NOT arm Smart Start on a yield). Cleared in SetCallInProg the
        // moment callInProg moves to any other value (contact ended, operator picked another
        // call, band change) -- see SetCallInProg.
        private string _smartStartHandoffCall;
        private int _smartStartHandoffCallCount;

        // Race-safety gate ("Work Now versus Smart Start becoming ready at the same time: one
        // atomic start-request gate; only one wins"). WinForms' single UI thread means the only
        // real hazard is two observations landing in the SAME synchronous pass (e.g. a target's
        // CQ and a silence threshold both resolving out of the same final decode batch) each
        // trying to fire a start; this flag, held for the duration of one synchronous dispatch,
        // is exactly what that needs. ReplyTo's own success-only commit + _contactEpoch
        // (Codex Audit 04) independently guards the actual async REPLY commit, so this is a
        // belt-and-suspenders pairing, not the only safety net.
        private bool _targetMonitorStartDispatching;

        // True for exactly the duration of the synchronous SeedSelectedDecode call in
        // TryCaptureSmartStart -- i.e. while Smart QSO Start is being ARMED from the cached
        // decode the operator just selected. That decode is what the operator is already
        // looking at; it must not narrate "<call> calling CQ" as though the target had just
        // called on the air. The seed still records the decode for the reply, parity, and
        // live-evidence/readiness -- only its CQ-opening narration is held. A genuinely new CQ
        // decoded AFTER activation arrives via ObserveDecode (the live feed), not this
        // synchronous seed, so it still sets and announces "calling CQ". A bool scoped to the
        // one synchronous call is used rather than a decode-timestamp comparison because the
        // seed and the live feed both run on the single UI thread and cannot interleave --
        // there is no race and no clock-skew edge to reason about.
        private bool _smartStartSeeding;

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

        // Operator policy (2026-09-08): a fresh CQ / RR73 / 73 from the target is a positive call
        // opportunity -- call at our next legitimate transmit opportunity, do NOT wait extra FT8
        // periods "to see which caller the DX picked". The old 2-period slot-advance hold that
        // did exactly that (post-ship 2.0.69 pileup-churn mitigation) is removed: the 3-poll
        // finality window above still catches a same-period straggler decode (Nexus spreads one
        // slot's decodes across ~3 snapshots) so RevalidateForAutoStart can still decline a
        // target that is genuinely mid-report to a peer, and rule 7 (yield the moment the target
        // sends someone else a report) is the recovery when the DX does choose another station.

        // Consecutive target-not-heard limit (reworked 2026-09-22 from the old "busy dead-end"
        // round counter -- same operator-adjustable setting/storage key, ctrl.smartStartMax
        // StandbyRounds, Options > Transmit, new meaning). After this many COMPLETED calling-over
        // transmissions IN A ROW to one armed target with NO live decode from the target at all --
        // not even one showing it busy with another station -- Smart Start disarms and tells the
        // operator: the target may be gone, or propagation may have changed. Hearing the target at
        // all, including working someone else, resets the streak to zero (TargetMonitor.
        // NoteCallOverCompletedAndCheckNotHeardLimit) -- a target that is busy but still audible is
        // not what this guards against; the Repeat Limit is what bounds a hot pileup that keeps
        // answering someone else. See TargetMonitor.TargetNotHeardStreak's own comment.

        public bool StationWatchActive => _stationWatch.IsActive;
        public string StationWatchTarget => _stationWatch.TargetCall;

        // On-demand status readout (operator request, 2026-09-12): both Smart Start and Station
        // Watch run silently -- there is no ongoing UI for either -- so the operator has no way
        // to ask "is one of these actually doing anything right now?" without waiting for the
        // next narration (which may be minutes away, or may never come if the target stays
        // quiet). Two SEPARATE hotkeys/methods (operator feedback, same day: one combined line
        // reading "Not watching" right after a Smart Start fact read like Station Watch itself
        // was off/broken, when it simply wasn't the thing being asked about). Wording is pulled
        // from the SAME Notification event classes + the operator's own configured Template in
        // Options > Notifications (via NotificationTemplateEngine.Format) rather than a second,
        // separately-invented copy of the phrase -- if the operator customizes e.g. "Watching
        // {Target}.", this on-demand report reflects that customization too, and can never drift
        // from what the live narration actually says for the same fact.
        // The live limits and counters templates can use (NotificationVariableRegistry.LiveVariables).
        internal IReadOnlyDictionary<string, string> LiveCounterValues()
        {
            bool smart = _smartStart.IsActive;
            int configuredRepeat = _configuredRepeatLimit;
            bool ordinaryCall = !smart && callInProg != null && discardCall == callInProg;
            return new Dictionary<string, string>
            {
                ["RepeatLimit"]    = (ordinaryCall ? maxTxRepeat : configuredRepeat).ToString(),
                ["RepeatCount"]    = (smart ? _smartStart.TransmittedCallCount : ordinaryCall ? discardCallCycleCount : 0).ToString(),
                ["SilencePeriods"] = ctrl.smartStartSilencePeriods.ToString(),
                ["SilenceCount"]   = (smart ? _smartStart.SilenceCount : 0).ToString(),
                ["NotHeardLimit"]  = ctrl.smartStartMaxStandbyRounds.ToString(),
                ["NotHeardCount"]  = (smart ? _smartStart.TargetNotHeardStreak : 0).ToString(),
                ["TimeLimit"]      = ctrl.smartStartTimeLimitMinutes.ToString(),
                ["TimeElapsed"]    = (smart ? (int)(DateTime.UtcNow - _smartStart.ArmedAtUtc).TotalMinutes : 0).ToString(),
                ["RepliesLimit"]   = Math.Max(1, Math.Min(6, ctrl.otherStationRepliesBeforeYielding)).ToString(),
                ["RepliesCount"]   = (callInProg != null ? _otherPartyOverStrikes : 0).ToString(),
            };
        }

        private string RenderNotificationPhrase(NotificationEventType type, IReadOnlyDictionary<string, string> tokens)
        {
            if (!ctrl.Notifications.Policies.TryGetValue(type, out NotificationPolicy policy)) return "";
            var all = new Dictionary<string, string>();
            foreach (var kv in tokens) all[kv.Key] = kv.Value;
            NotificationVariableRegistry.AddUniversal(all);
            return NotificationTemplateEngine.Format(policy.Template, all);
        }

        // Options > General "Space callsigns and grids" (2026-09-12): extended, at Jim's request,
        // to the WHOLE Notification system -- previously scoped to exactly five display surfaces
        // (station lists, Raw Decodes, Spot Watch, main status) "and nowhere else". Every
        // user-facing callsign that reaches a Station Watch/Smart Start notification token or
        // narration phrase is wrapped with this at the point it becomes PRESENTATION text --
        // never at the point it's used for internal comparisons/dedup/state (TargetCall equality,
        // _smartStartHandoffCall, ShouldAnnounceTargetActivity's tracking key, DebugOutput), which
        // must stay on the raw callsign. Reuses the EXISTING DisplayCallsign helper (WsjtxClient.
        // cs) -- no new spacing logic. SpaceEveryChar skips characters that are already spaces, so
        // this is idempotent -- applying it more than once to the same value is harmless.
        private string SC(string call) => DisplayCallsign(call, ctrl.spaceCallsignsAndGrids);

        public bool ReportSmartStartStatus()
        {
            string msg;
            if (!ctrl.smartQsoStartEnabled)
                // No Notification event models "the feature is off" -- that's a Jimmy setting,
                // not an on-air fact -- so there is nothing to source this one from.
                msg = Wording.Get("Msg.SmartModeOff");
            else if (_smartStart.AwaitingEngagement)
                msg = RenderNotificationPhrase(NotificationEventType.SmartStartCallStarting,
                    new SmartStartCallStartingEvent(SC(_smartStart.TargetCall)).ToTokens());
            else if (_smartStart.IsActive)
            {
                if (_smartStart.BusyWithOther)
                {
                    var busy = new SmartStartTargetBusyEvent(SC(_smartStart.TargetCall), SC(_smartStart.ApparentPeer ?? ""));
                    msg = RenderNotificationPhrase(NotificationEventType.SmartStartTargetBusy, busy.ToTokens());
                }
                else if (_smartStart.SilenceCount > 0)
                {
                    string progress = $"{_smartStart.SilenceCount} of {_smartStart.SilenceThreshold}";
                    string spacedTarget = SC(_smartStart.TargetCall);
                    // Plain words, not "3 of 1" (operator, 2026-10-02): how long it has been quiet.
                    var waiting = new SmartStartWaitingEvent(spacedTarget,
                        Wording.Fill(_smartStart.SilenceCount == 1 ? "Msg.SmartNotHeardForOne" : "Msg.SmartNotHeardFor",
                            ("Call", spacedTarget), ("Count", _smartStart.SilenceCount.ToString())), progress);
                    msg = RenderNotificationPhrase(NotificationEventType.SmartStartWaiting, waiting.ToTokens());
                }
                else
                    msg = RenderNotificationPhrase(NotificationEventType.SmartStartArmed,
                        new SmartStartArmedEvent(SC(_smartStart.TargetCall)).ToTokens());
            }
            else
                // Armed-but-idle (enabled, nothing captured yet) is also not an on-air fact --
                // no Notification event exists for it either.
                msg = Wording.Get("Msg.SmartModeNoTarget");

            if (string.IsNullOrEmpty(msg)) msg = Wording.Get("Msg.SmartModeStatusUnavailable");
            StatusView.ShowMessage(msg, false);
            return true;
        }

        public bool ReportStationWatchStatus()
        {
            string msg = _stationWatch.IsActive
                ? RenderNotificationPhrase(NotificationEventType.StationWatchStarted,
                    new StationWatchLifecycleEvent(NotificationEventType.StationWatchStarted, SC(_stationWatch.TargetCall)).ToTokens())
                : "Not watching any station.";
            if (string.IsNullOrEmpty(msg)) msg = "Station Watch status unavailable.";
            StatusView.ShowMessage(msg, false);
            return true;
        }

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
                StatusView.ShowMessage(Wording.Get("Msg.WatchNoStation"), false);
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
                StatusView.ShowMessage(Wording.Get("Msg.WatchNotActive"), false);
                return;
            }
            if (_stationWatch.LastUsableDecode == null)
            {
                string spacedWatched = SC(_stationWatch.TargetCall);
                Notify?.Publish(new SmartStartWaitingEvent(spacedWatched,
                    $"Waiting for another decode from {spacedWatched}.",
                    armGeneration: _stationWatch.ArmGeneration, stateSeq: _stationWatch.AdvanceStateSeq()));
                StatusView.ShowMessage(Wording.Fill("Msg.WatchWaiting", ("Call", spacedWatched)), false);
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

            // An Enter on a decode that is already addressed to OUR callsign normally means
            // "answer them now", not "wait for a good moment to start" -- fall through to the
            // normal ReplyTo path (how Enter behaved before Smart Start existed). BUT only when
            // that decode is still FRESH. A stale queued/list "to us" selection (KA1BMF live
            // radio, 2026-09-08 -- ~50 s old; the target had since started working another
            // station on a different frequency) is no longer proof the target is calling us.
            // Hand a stale "to us" selection to Smart Start management like any other stale
            // selection: SeedSelectedDecode records it as context only, Smart Start waits for
            // live evidence, and it still hands straight off to the normal QSO sequencer the
            // instant the target really does address us again (HandOffSmartStartWhileWaiting) --
            // or waits / yields while the target is demonstrably busy.
            // Nexus modernization Stage 8: "addressed to us" comes from EffectiveSemantic
            // (Nexus's parse when the cutover is on). Stage 5 proved AddressedToMe identical to
            // WsjtxMessage.ToCall(..)==myCall for every valid decode. RevalidateForAutoStart /
            // AutoStartCheck and the 3-poll finality deferral all run on TargetMonitor STATE.
            // The state machine, thresholds, yield/retry, Repeat Limit, and the 2.0.65
            // premature-TX guards are unchanged.
            if (dmsg.EffectiveSemantic(myCall).AddressedToMe
                && TargetMonitor.IsSelectionDecodeFresh(dmsg, mode, DateTime.UtcNow))
                return false;

            _smartStart.SilenceThreshold = ctrl.smartStartSilencePeriods;
            _smartStart.BusySilenceThreshold = ctrl.smartStartBusyQuietPeriods;

            // Re-selecting the call Smart Start is ALREADY armed on -- a second Enter/Space, or
            // dialogTimer2_Tick re-issuing the operator's still-queued selection (Smart Start
            // deliberately leaves a waiting target in the RX list) -- must NOT restart the
            // monitor. Start() wipes the parity, live-evidence and silence progression it has
            // accumulated, and the re-seed decode is by then usually older than the seed
            // fresh-window, so SeedSelectedDecode would leave TargetEvenParity null and
            // OnReceivePeriodComplete could not count a single opportunity until an unrelated
            // fresh live decode happened to arrive -- Smart Start silently sits with no progress
            // for minutes (TJ1GD live-radio finding, 2026-09-08). The live feed (ObserveDecode
            // via FeedTargetMonitors) already keeps the active monitor current, so the restart +
            // re-seed is simply skipped. The purely presentational re-sync of the advanced TX/RX
            // panels (2.0.71) still runs below -- the operator may have re-picked the call from
            // the other panel -- and the method still returns true so the caller does not fall
            // through to an immediate ReplyTo. A genuine change of target still replaces.
            bool alreadyArmedOnThisCall = _smartStart.IsActive
                && string.Equals(_smartStart.TargetCall, call, StringComparison.OrdinalIgnoreCase);

            if (!alreadyArmedOnThisCall)
            {
                _smartStart.Start(call, CurrentBandStr, mode, _directExpectedSessionToken);
                // Seed the freshly-started monitor with the exact decode the operator selected.
                // SeedSelectedDecode records it as the decode to eventually reply from, but only
                // treats it as genuine current evidence (parity, live-evidence, CQ/73/RR73
                // readiness) when it is still fresh -- a stale queued/list selection identifies
                // WHICH station to work and nothing more, and Smart Start then waits for a real
                // live decode before it can authorize any transmission (2.0.64: the V51WW failure
                // was a ~54 s-old RR73 being manufactured into live "target available" evidence).
                // Seeding is bracketed so a CQ classification derived from THIS cached decode
                // does not narrate "<call> calling CQ" at activation (see _smartStartSeeding).
                _smartStartSeeding = true;
                try { _smartStart.SeedSelectedDecode(dmsg, DateTime.UtcNow, myCall); }
                finally { _smartStartSeeding = false; }
                // "Waiting to work {call}." is announced by SmartStartArmedEvent, raised from
                // _smartStart.Start above via HandleTargetObservation -- no separate ShowMessage
                // (that would be a near-duplicate on both the visible line and in speech).
                if (_smartStart.ConsumeReadyToStart())
                    ArmPendingAutoStart(_smartStart);
            }

            // 2.0.71: the operator just picked this call from one of the two TX/RX panels.
            // Flip that panel to the TX side now, not (only) when Smart Start eventually
            // dispatches -- the real ReplyTo can be many receive periods away, or never come
            // if the target stays busy, and the panels sitting labelled backwards the whole
            // time is the reported bug. NextCall's own plain-Enter sync is unreachable on this
            // path (it returns true here, above that block); ReplyTo re-asserts it at dispatch.
            SyncAdvancedLayoutTxFirst(dmsg, "Smart QSO Start capture (advanced UI)");
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
                else if (_smartStart.ConsumeEngagedWhileWaiting())
                    HandOffSmartStartWhileWaiting(enq);
                else if (_smartStart.ConsumeReadyToStart())
                    ArmPendingAutoStart(_smartStart);
            }
        }

        // N4BP live-radio audit -- fix 4. The target addressed OUR callsign with a mid-exchange
        // report/reply while Smart Start was still WAITING (not yet in its own calling phase).
        // That IS engagement -- answer THIS exact decode now (identical to Enter on a to-us
        // decode), hand the contact to the normal QSO sequencer, and end Smart Start. This
        // bypasses the snapshot-finality deferral on purpose: the deferral exists to catch a
        // "target working someone else" straggler, and here the target is literally sending US a
        // report. Without this, the parked deferred auto-start could dispatch off a NEWER decode
        // (a fresh CQ from the target), replying with Tx1 grid instead of the roger -- exactly
        // what happened with N4BP's -06 on 2026-09-08.
        private void HandOffSmartStartWhileWaiting(EnqueueDecodeMessage enq)
        {
            string target = _smartStart.TargetCall;
            DebugOutput($"{Time()} [SMART] {target} addressed us while Smart Start was waiting -- answering now; normal QSO sequencing owns it");
            ClearPendingAutoStart();
            Notify?.Publish(new SmartStartEngagedEvent(SC(target), _smartStart.ArmGeneration, _smartStart.AdvanceStateSeq()));
            if (_stationWatch.IsActive && string.Equals(_stationWatch.TargetCall, target, StringComparison.OrdinalIgnoreCase))
                StopStationWatch();
            RecordSmartStartHandoffOrigin(target);
            _smartStart.Stop(announce: false);
            // Answer the target's actual to-us decode. If a contact is somehow already running
            // (an in-flight dispatch beat us here), leave it alone -- the sequencer owns it.
            if (callInProg == null && enq != null)
                ReplyTo(enq);
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
                string target = _smartStart.TargetCall;
                DebugOutput($"{Time()} [SMART] {target} answered our call -- Smart Start done; normal QSO sequencing owns it");
                Notify?.Publish(new SmartStartEngagedEvent(SC(target), _smartStart.ArmGeneration, _smartStart.AdvanceStateSeq()));
                // Decision (2026-09-07): the target has actually engaged us, so the normal QSO
                // sequencer now owns the contact. A Station Watch the operator set on this SAME
                // call stops here too, so routine QSO speech takes over cleanly -- matching the
                // manual-selection / Work-Watched-Station-Now handoff. (A watch on a DIFFERENT
                // call is untouched.)
                if (_stationWatch.IsActive && string.Equals(_stationWatch.TargetCall, target, StringComparison.OrdinalIgnoreCase))
                    StopStationWatch();
                RecordSmartStartHandoffOrigin(target);
                _smartStart.Stop(announce: false);
                return;
            }
            if (_smartStart.BusyWithOther)
                YieldSmartStartToOtherQso();
        }

        // Remember that THIS contact originated under Smart Start, just before the monitor is
        // Stopped for the hand-off to the normal QSO sequencer. Carries the cumulative
        // Repeat-Limit calling-over count so a later resume (YieldActiveContactToOtherQso) does
        // not restart the operator's limit. callInProg is normally already this target here; the
        // marker is cleared in SetCallInProg the moment callInProg moves to anything else.
        private void RecordSmartStartHandoffOrigin(string target)
        {
            _smartStartHandoffCall = target;
            _smartStartHandoffCallCount = _smartStart.TransmittedCallCount;
        }

        // Did the contact now being ceased by YieldActiveContactToOtherQso originate under Smart
        // Start (so Smart Start should RESUME ownership of the target rather than abandon it)?
        // Read BY THE CALLER before its teardown trio, because CancelQso -> SetCallInProg(null)
        // clears the _smartStartHandoffCall marker.
        private bool ShouldResumeSmartStartOnYield(string partner) =>
            ctrl.smartQsoStartEnabled
            && !string.IsNullOrEmpty(_smartStartHandoffCall)
            && string.Equals(_smartStartHandoffCall, partner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(callInProg, partner, StringComparison.OrdinalIgnoreCase)
            // The operator armed Smart Start on a DIFFERENT target in the meantime -- never
            // clobber that.
            && (!_smartStart.IsActive
                || string.Equals(_smartStart.TargetCall, partner, StringComparison.OrdinalIgnoreCase));

        // Bring Smart Start back to armed/waiting on the SAME target after its hand-off QSO was
        // ceased (the target moved into a substantive report/R-report/RRR exchange with a third
        // station before completing with us). Carries the cumulative Repeat-Limit count so the
        // operator's limit is not restarted, and narrates it exactly as the calling-phase busy
        // yield does ("Standing by."). The next real decode re-establishes parity / busy state
        // through the normal feed; the existing Smart Start policy (CQ / RR73 / 73 opening,
        // report/RRR busy, silence fallback, target-answers-us hand-off, cumulative Repeat Limit)
        // then applies unchanged.
        private void ResumeSmartStartAfterHandoffYield(string target, int carriedCallCount)
        {
            ClearPendingAutoStart();
            _smartStart.ResumeAfterHandoff(target, CurrentBandStr, mode, _directExpectedSessionToken,
                carriedCallCount, ctrl.smartStartSilencePeriods);
            _smartStart.BusySilenceThreshold = ctrl.smartStartBusyQuietPeriods;
            Notify?.Publish(new SmartStartYieldedEvent(SC(target), _smartStart.ArmGeneration, _smartStart.AdvanceStateSeq()));
            DebugOutput($"{Time()} [SMART] {target} moved to another station mid-QSO -- Smart Start resumes ownership (cumulative calls: {carriedCallCount})");
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
            // Hearing the target working another station is presence, not silence -- it does NOT
            // count against the consecutive target-not-heard limit (TargetMonitor already reset
            // that streak the moment this busy decode was ingested). Jimmy just yields and stays
            // armed, exactly as before.
            Notify?.Publish(new SmartStartYieldedEvent(SC(target), _smartStart.ArmGeneration, _smartStart.AdvanceStateSeq()));
        }

        // Consecutive target-not-heard limit reached (reworked 2026-09-22 from the old "busy
        // dead-end" stand-down -- see TargetMonitor.NoteCallOverCompletedAndCheckNotHeardLimit for
        // exactly what counts). ctrl.smartStartMaxStandbyRounds completed calling-over
        // transmissions to this target went out in a row with NO live decode from the target at
        // all -- the target may be gone, or propagation may have changed. A SEPARATE terminal
        // policy from the Repeat Limit (which counts total calls regardless of whether the target
        // was ever heard) and the time limit below (purely wall-clock): a target that stays
        // audible but busy with someone else never trips this -- the Repeat Limit is the tool for
        // a hot pileup. Mirrors SmartStartRepeatLimitReached's own teardown; distinct wording so
        // the operator can tell the two apart.
        private void SmartStartNotHeardLimitReached()
        {
            string target = _smartStart.TargetCall;
            int limit = ctrl.smartStartMaxStandbyRounds;
            DebugOutput($"{Time()} [SMART] {target} not heard across {limit} consecutive calls -- disarming Smart Start");
            ClearPendingAutoStart();
            if (string.Equals(callInProg, target, StringComparison.OrdinalIgnoreCase))
            {
                RequeueAbortedCall();                       // while callInProg / replyDecode are still valid
                if (!transmitting) expiredCall = target;    // existing "<call> expired" operator status/announcement
                CancelQso();                                // clears QSO state + bumps _contactEpoch
                HaltAndDisableTx();                         // HALT_TX + SET_TX_ENABLED 0
            }
            _smartStart.Stop(announce: false);
            StatusView.ShowMessage(Wording.Fill("Msg.SmartModeNotHeard", ("Call", SC(target)), ("Count", limit.ToString())), true);
        }

        // Operator request (2026-09-13): an absolute wall-clock backstop, independent of both the
        // Repeat Limit and the busy-churn cap above -- "so they know an hour later their radio
        // will not start trying to call the station," regardless of how many calls or busy-
        // declines happened along the way. ctrl.smartStartTimeLimitMinutes <= 0 means no limit
        // (default, and today's unchanged behavior). Mirrors SmartStartRepeatLimitReached's own
        // teardown exactly -- same halt/disarm sequence, different reason and wording so the
        // operator can tell the two apart.
        private void SmartStartTimeLimitReached()
        {
            string target = _smartStart.TargetCall;
            int limitMinutes = ctrl.smartStartTimeLimitMinutes;
            DebugOutput($"{Time()} [SMART] Time limit ({limitMinutes} min) reached for {target} -- no further calls, disarming Smart Start");
            ClearPendingAutoStart();
            if (string.Equals(callInProg, target, StringComparison.OrdinalIgnoreCase))
            {
                RequeueAbortedCall();                       // while callInProg / replyDecode are still valid
                if (!transmitting) expiredCall = target;    // existing "<call> expired" operator status/announcement
                CancelQso();                                // clears QSO state + bumps _contactEpoch
                HaltAndDisableTx();                         // HALT_TX + SET_TX_ENABLED 0
            }
            _smartStart.Stop(announce: false);
            string minuteWord = Wording.Get(limitMinutes == 1 ? "Msg.MinuteOne" : "Msg.MinuteMany");
            StatusView.ShowMessage(
                Wording.Fill("Msg.SmartModeTimeLimit", ("Minutes", limitMinutes.ToString()), ("MinuteWord", minuteWord), ("Call", SC(target))), true);
        }

        // The Smart Start Repeat Limit -- (int)ctrl.timeoutNumUpDown.Value, the operator's own
        // ordinary per-call limit -- has now been reached for the armed target across the whole
        // calling effort (initial call + repeated overs + calls after any busy yields), without
        // the target ever answering our callsign. Treat it exactly like an ordinary give-up:
        // unwind our own call to that target with the same Escape-style ordered teardown, surface
        // it through the EXISTING "<call> expired" status the ordinary Repeat Limit give-up
        // already uses (WsjtxClient.Display.cs), and disarm Smart Start completely so nothing can
        // transmit for it later. Called from DirectApplyStatus's transmitting-just-ended edge,
        // the same place DiscardCall() fires for an ordinary call.
        private void SmartStartRepeatLimitReached()
        {
            string target = _smartStart.TargetCall;
            int limit = (int)ctrl.timeoutNumUpDown.Value;
            DebugOutput($"{Time()} [SMART] Repeat limit ({limit}) reached for {target} -- no further calls, disarming Smart Start");
            ClearPendingAutoStart();
            if (string.Equals(callInProg, target, StringComparison.OrdinalIgnoreCase))
            {
                RequeueAbortedCall();                       // while callInProg / replyDecode are still valid
                if (!transmitting) expiredCall = target;    // existing "<call> expired" operator status/announcement
                CancelQso();                                // clears QSO state + bumps _contactEpoch
                HaltAndDisableTx();                         // HALT_TX + SET_TX_ENABLED 0
            }
            _smartStart.Stop(announce: false);
            // Concise terminal message so the operator knows the effort ended on the Repeat
            // Limit (counted calling overs only), distinct from the busy-churn stop above.
            StatusView.ShowMessage(Wording.Fill("Msg.RepeatLimit", ("Count", limit.ToString()), ("Call", SC(target))), true);
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
            // Operator-configurable wall-clock backstop (2026-09-13) -- checked BEFORE the
            // ordinary readiness machinery below, and regardless of AwaitingEngagement, so it is
            // an absolute limit on the whole effort, not just the waiting phase. 0 = no limit.
            if (_smartStart.ExceedsTimeLimit(DateTime.UtcNow, ctrl.smartStartTimeLimitMinutes))
            {
                SmartStartTimeLimitReached();
                return;
            }
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

        // N4BP live-radio audit -- fix 6. Escape / Alt+H (Controller.cs) capture this BEFORE
        // AbortContact() so, when nothing was transmitting (the Smart-Start-only-waiting case),
        // it can still give a short "Smart Mode stopped" confirmation -- the "Tx halted"
        // announcement is gated on HasActiveTxOrCycle, which is false while merely waiting.
        public bool SmartStartActive => _smartStart.IsActive || _pendingAutoStart != null;
        public string SmartStartTarget => _smartStart.TargetCall;

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
                string why = AutoStartDeclineText(SC(monitor.TargetCall), check);
                if (operatorOverride)
                    StatusView.ShowMessage(why, false);
                else if (check == AutoStartCheck.TargetBusy)
                {
                    // A pre-transmit decline -- no calling-over went out this round, so it is not
                    // evidence one way or the other for the consecutive target-not-heard limit
                    // (that only counts COMPLETED calling-over transmissions, WsjtxClient.Direct.cs)
                    // -- and the decode that revealed the target busy already reset that streak via
                    // IngestTargetDecode, exactly as it would for a decode heard any other way.
                    Notify?.Publish(new SmartStartYieldedEvent(SC(monitor.TargetCall), monitor.ArmGeneration, monitor.AdvanceStateSeq()));
                }
                else
                {
                    // Stale or missing live evidence IS "not heard" to the operator -- the same one
                    // fact, through the same once-per-period / changes-only gate.
                    string phrase = (check == AutoStartCheck.StaleEvidence || check == AutoStartCheck.NoLiveEvidence)
                        ? NextQuietPhrase(monitor) : why;
                    if (phrase != null)
                        Notify?.Publish(new SmartStartWaitingEvent(SC(monitor.TargetCall), phrase,
                            armGeneration: monitor.ArmGeneration, stateSeq: monitor.AdvanceStateSeq()));
                }
                // Leave the monitor armed and watching -- a later fresh decode / cleared-busy
                // state can still start it; it simply did not fire this time.
                return;
            }

            _targetMonitorStartDispatching = true;
            try
            {
                Notify?.Publish(new SmartStartCallStartingEvent(SC(monitor.TargetCall), monitor.ArmGeneration, monitor.AdvanceStateSeq()));
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
            // ── Lifecycle: purpose-aware presentation ──────────────────────────────────────────
            // Only a real Station Watch publishes the "Watching X" / "Stopped watching X"
            // lifecycle line. Arming or dropping the Smart Start monitor must never leak through
            // it -- Smart Start has its own "Waiting to work X" line (SmartStartArmed), and its
            // teardown is covered by the engagement / yield / cancel paths.
            if (obs.Kind == TargetObservationKind.WatchStarted)
            {
                if (obs.Purpose == TargetPurpose.StationWatch)
                    Notify?.Publish(new StationWatchLifecycleEvent(NotificationEventType.StationWatchStarted, SC(obs.Target)));
                else
                    Notify?.Publish(new SmartStartArmedEvent(SC(obs.Target), _smartStart.ArmGeneration, _smartStart.AdvanceStateSeq()));
                return;
            }
            if (obs.Kind == TargetObservationKind.WatchStopped)
            {
                if (obs.Purpose == TargetPurpose.StationWatch)
                    Notify?.Publish(new StationWatchLifecycleEvent(NotificationEventType.StationWatchStopped, SC(obs.Target)));
                return;
            }

            // ── Smart Start: the small decision-support narration subset ───────────────────────
            if (obs.Purpose == TargetPurpose.SmartStart)
            {
                HandleSmartStartObservation(obs);
                return;
            }

            // ── Station Watch: the fuller CQ/addressing/report/RRR/RR73/73/peer-observed family ─
            if (obs.Purpose != TargetPurpose.StationWatch) return;

            if (obs.Kind == TargetObservationKind.TargetAmbiguous)
            {
                Notify?.Publish(new StationWatchAmbiguousEvent(SC(obs.Target)));
                return;
            }

            // Shared target-activity unification (2026-09-11): every "target working ANOTHER
            // station" fact (including a bare CQ, which counts as "available") routes through
            // the SAME per-target gate Smart Start and the ordinary callInProg path use, so at
            // most one of the three ever narrates a given decode period's fact -- see
            // TargetActivityTracker.cs's own header. TargetAddressingUs is a different fact
            // entirely (the peer is US) and stays fully independent, ungated.
            bool isRepeat = false;
            if (obs.Kind != TargetObservationKind.TargetAddressingUs
                && !ShouldAnnounceTargetActivity(obs.Target, obs.Peer ?? "", obs.Kind, obs.Value ?? "", out isRepeat))
                return;

            string phrase = BuildActivityPhrase(obs);
            if (string.IsNullOrEmpty(phrase)) return;
            if (isRepeat) phrase = RepeatPhrase.Mark(phrase, SC(obs.Target), Wording.Get("Msg.Still"));
            Notify?.Publish(new StationWatchActivityEvent(phrase, SC(obs.Target), SC(obs.Peer), obs.Value, obs.Kind.ToString()));
        }

        // The ONE gate Smart Start, Station Watch, and the ordinary callInProg path (WsjtxClient.
        // cs's ProcessDecodeMsg) all submit the identical structured fact through, keyed on target
        // callsign + decode-period identity -- never on which context observed it. Returns true
        // exactly when the caller should still publish its OWN (context-selected) wording this
        // period; false means an identical fact already spoke this period (from any of the three
        // contexts), or "Repeat unchanged QSO activity each period" is off and nothing changed.
        private bool ShouldAnnounceTargetActivity(string target, string peer, TargetObservationKind kind, string rawValue) =>
            ShouldAnnounceTargetActivity(target, peer, kind, rawValue, out _);

        private bool ShouldAnnounceTargetActivity(string target, string peer, TargetObservationKind kind, string rawValue, out bool isRepeat)
        {
            isRepeat = false;
            if (string.IsNullOrEmpty(target)) return false;
            var fact = new TargetActivityFact(target, peer ?? "", kind, rawValue ?? "");
            var tracker = GetActivityTracker(target);
            bool announce = tracker.Evaluate(fact, _directLastSlotSeen,
                ctrl.Notifications.RepeatUnchangedTargetActivityEachPeriod) == TargetActivityDecision.Announce;
            isRepeat = announce && tracker.LastWasRepeat;
            return announce;
        }

        // ── "Not heard" narration (operator, 2026-10-02) ───────────────────────────────────────
        // One fact, said once per period at most: the target is not being heard. It used to be two
        // messages every period ("K2NKP not heard, 3 of 1." AND "No recent decode from K2NKP; still
        // watching"), with a confusing count. Now: "K2NKP not heard." when the quiet starts; then,
        // with "Repeat unchanged ... each period" on, "K2NKP still not heard." each period -- or,
        // with it off, nothing more until something changes (heard again, called, given up). The
        // count stays available as a field ({Progress}, {SilenceCount}) for those who want it.
        private string _quietRunTarget;
        private int _quietRunArm = -1;
        private ulong _quietRunSlot = ulong.MaxValue;
        private bool _quietRunSaid;

        // The phrase to say now for "target not heard", or null when nothing should be said.
        private string NextQuietPhrase(TargetMonitor monitor)
        {
            if (monitor?.TargetCall == null) return null;
            bool sameRun = _quietRunSaid && _quietRunArm == monitor.ArmGeneration
                && string.Equals(_quietRunTarget, monitor.TargetCall, StringComparison.OrdinalIgnoreCase);
            if (sameRun && _quietRunSlot == _directLastSlotSeen) return null;                        // once per period
            if (sameRun && !ctrl.Notifications.RepeatUnchangedTargetActivityEachPeriod) return null;  // changes only
            _quietRunTarget = monitor.TargetCall;
            _quietRunArm = monitor.ArmGeneration;
            _quietRunSlot = _directLastSlotSeen;
            _quietRunSaid = true;
            return Wording.Fill(sameRun ? "Msg.SmartStillNotHeard" : "Msg.SmartNotHeard", ("Call", SC(monitor.TargetCall)));
        }

        // The target was heard: the next quiet is news again.
        private void EndQuietRun(string target)
        {
            if (string.Equals(_quietRunTarget, target, StringComparison.OrdinalIgnoreCase)) _quietRunSaid = false;
        }

        // Smart Start's own narration -- deliberately a SUBSET of what Station Watch reports:
        // just enough for the operator to follow Jimmy's decision while it waits on / calls a
        // captured target. The CQ/73/RR73 "target is now available" facts are conveyed by the
        // SmartStartTargetAvailable + SmartStartCallStarting decision events (raised elsewhere),
        // not a bare per-message line here; engagement is handled in
        // ServiceSmartStartAwaitingEngagement. What is left for this method is the "target is
        // busy with someone else" fact -- and even that is suppressed when a Station Watch is
        // ALSO running on the same call, so the richer Station Watch line is never doubled.
        private void HandleSmartStartObservation(TargetObservation obs)
        {
            if (obs.Kind != TargetObservationKind.SmartStartWaiting) EndQuietRun(obs.Target);
            switch (obs.Kind)
            {
                case TargetObservationKind.SmartStartWaiting:
                    // N4BP live audit -- fix 3: concise, and only ever raised for a period the
                    // target was genuinely NOT heard (TargetMonitor's _targetHeardThisPeriod
                    // guard). `obs.Value` is "1 of 2" ... "2 of 2" (the operator's own silence
                    // setting -- the final period is now narrated too, operator policy rule 6).
                    {
                        string phrase = NextQuietPhrase(_smartStart);
                        if (phrase == null) return;
                        Notify?.Publish(new SmartStartWaitingEvent(
                            SC(obs.Target), phrase, obs.Value,
                            armGeneration: _smartStart.ArmGeneration, stateSeq: _smartStart.AdvanceStateSeq()));
                    }
                    return;
                case TargetObservationKind.SmartStartTargetAvailable:
                    // Operator policy (2026-09-08): no dispatch-sounding "appears available" from
                    // preliminary readiness. The factual opening narration (the target's CQ /
                    // RR73 / 73, or "not heard, N of M") plus "Calling {Target}." at the real
                    // dispatch is the truthful presentation; a separate "appears available" was
                    // premature (published before final revalidation) and could repeat while the
                    // same pending start was parked. Deliberately not published.
                    return;
            }

            bool stationWatchCoversSameTarget = _stationWatch.IsActive
                && string.Equals(_stationWatch.TargetCall, obs.Target, StringComparison.OrdinalIgnoreCase);
            if (stationWatchCoversSameTarget) return;   // the richer Station Watch line owns it -- never doubled

            // A report / reply whose peer is OUR OWN callsign is the target working US, not
            // another station. When Smart Start is WAITING, IngestTargetDecode routes that
            // straight to the engagement hand-off (HandOffSmartStartWhileWaiting) -- the
            // "answered you; normal QSO" line covers it and the normal QSO status shows the
            // report, so nothing is narrated here. During AwaitingEngagement,
            // ServiceSmartStartAwaitingEngagement owns it -- also nothing here.
            bool reportIsToUs = !string.IsNullOrEmpty(obs.Peer)
                && string.Equals(obs.Peer, myCall, StringComparison.OrdinalIgnoreCase);
            if (reportIsToUs) return;

            switch (obs.Kind)
            {
                case TargetObservationKind.TargetCq:
                    // Do NOT narrate "calling CQ" while this came from the Smart QSO Start SEED
                    // (the cached decode the operator just selected) -- it is not a fresh
                    // on-air CQ. A CQ decoded AFTER activation arrives via the live feed
                    // (ObserveDecode), where _smartStartSeeding is false, and still announces.
                    if (_smartStartSeeding) return;
                    // Operator policy rule 1: a CQ from the target is the opening. Narrate the
                    // fact ("N4BP calling CQ.") -- "Calling {Target}." follows at dispatch. Gated
                    // through the shared target-activity tracker (2026-09-11) -- see
                    // ShouldAnnounceTargetActivity's own comment -- so a target that keeps CQing
                    // without hearing us, or that Station Watch/the ordinary path already
                    // narrated this period, is not re-announced.
                    if (!ShouldAnnounceTargetActivity(obs.Target, "", TargetObservationKind.TargetCq, "", out bool cqRepeat)) return;
                    var cqEvt = SmartStartTargetBusyEvent.Cq(SC(obs.Target), _smartStart.ArmGeneration, _smartStart.AdvanceStateSeq());
                    Notify?.Publish(cqRepeat ? cqEvt.AsRepeat(Wording.Get("Msg.Still")) : cqEvt);
                    return;
                case TargetObservationKind.TargetAddressingOther:
                case TargetObservationKind.TargetReport:
                case TargetObservationKind.TargetRReport:
                case TargetObservationKind.TargetRrr:
                case TargetObservationKind.TargetRr73:
                case TargetObservationKind.Target73:
                case TargetObservationKind.OtherPartyObserved:
                    // The decoded FT8 fact about what the target is doing -- "N4BP working KZ4MW,
                    // -15." / "N4BP working KZ4MW, RR73." etc. (no translated state like
                    // "finishing"). Gated
                    // through the shared target-activity tracker so a peer/report/kind change
                    // always re-announces, an unchanged fact repeats at most once per applicable
                    // period (or never, per the operator's setting), and Station Watch / the
                    // ordinary otherStr path never double up on the identical fact.
                    if (!ShouldAnnounceTargetActivity(obs.Target, obs.Peer ?? "", obs.Kind, obs.Value ?? "", out bool busyRepeat)) return;
                    var busyEvt = new SmartStartTargetBusyEvent(
                        SC(obs.Target), SC(obs.Peer ?? ""), SpokenReport(obs.Value),
                        _smartStart.ArmGeneration, _smartStart.AdvanceStateSeq());
                    Notify?.Publish(busyRepeat ? busyEvt.AsRepeat(Wording.Get("Msg.Still")) : busyEvt);
                    return;
            }
        }

        // Natural spoken phrasing -- no S/R shorthand, no "report" filler word (spec). Only what
        // TargetMonitor actually classified; nothing here invents content beyond what Observed
        // already carries. Instance method (not static) so it can apply the "Space callsigns and
        // grids" preference via SC() -- obs.Target/obs.Peer come from TargetMonitor, which is
        // deliberately presentation-agnostic and always raw/unspaced.
        private string BuildActivityPhrase(TargetObservation obs)
        {
            string target = SC(obs.Target);
            string peer = SC(obs.Peer);
            switch (obs.Kind)
            {
                case TargetObservationKind.TargetCq:
                    return $"{target} CQ.";
                case TargetObservationKind.TargetAddressingUs:
                    return $"{target} calling you.";
                case TargetObservationKind.TargetAddressingOther:
                    return $"{target} working {peer}.";
                case TargetObservationKind.TargetReport:
                case TargetObservationKind.TargetRReport:
                    return $"{target} working {peer}, {SpokenReport(obs.Value)}.";
                case TargetObservationKind.TargetRrr:
                    return $"{target} RRR.";
                case TargetObservationKind.TargetRr73:
                    return $"{target} RR73.";
                case TargetObservationKind.Target73:
                    return $"{target} 73.";
                case TargetObservationKind.OtherPartyObserved:
                    return $"{peer} {SpokenReport(obs.Value)}.";
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
