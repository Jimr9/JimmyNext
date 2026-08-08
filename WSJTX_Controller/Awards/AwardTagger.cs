using System;
using System.Collections.Generic;
using WsjtxUdpLib.Messages.Out;

namespace WSJTX_Controller
{
    // Derives a decode's CallCategory and independently checks it against every
    // actively-checked award, extracted from WsjtxClient (2026-07-09, technique A of
    // the WsjtxClient.cs modularization). Reads back through the owning WsjtxClient
    // (_wc) for cross-cutting state (activeAwardTags, the HRC needed-sets, lookupManager,
    // sound settings, etc.) rather than owning any of it -- that state is shared with
    // plenty of WsjtxClient code that isn't moving, so only the derivation/matching
    // logic itself moves here.
    public class AwardTagger
    {
        private readonly WsjtxClient _wc;

        public AwardTagger(WsjtxClient wc)
        {
            _wc = wc;
        }

        // Derive the ranking category from classification fields.
        // Category is separate from Priority so behavioral checks remain tied to Priority.
        // Called after Priority is set; must be called before SetRank().
        public WsjtxClient.CallCategory DeriveCategory(EnqueueDecodeMessage d)
        {
            WsjtxClient.CallCategory cat;
            switch (d.Priority)
            {
                case (int)WsjtxClient.CallPriority.NEW_COUNTRY:         cat = WsjtxClient.CallCategory.NEW_COUNTRY; break;
                case (int)WsjtxClient.CallPriority.NEW_COUNTRY_ON_BAND: cat = WsjtxClient.CallCategory.NEW_COUNTRY_ON_BAND; break;
                case (int)WsjtxClient.CallPriority.TO_MYCALL:           cat = WsjtxClient.CallCategory.TO_MYCALL; break;
                case (int)WsjtxClient.CallPriority.MANUAL_SEL:          cat = WsjtxClient.CallCategory.MANUAL_SEL; break;
                case (int)WsjtxClient.CallPriority.WANTED_CQ:           cat = WsjtxClient.CallCategory.WANTED_CQ; break;
                default:
                    string deCall = d.DeCall();
                    if (_wc.wantedCalls.Count > 0 && !string.IsNullOrEmpty(deCall) && _wc.wantedCalls.Contains(deCall))
                        cat = WsjtxClient.CallCategory.ALWAYS_WANTED;
                    else if (_wc.IsPotaCall(d)) cat = WsjtxClient.CallCategory.POTA;
                    else if (IsSotaCall(d)) cat = WsjtxClient.CallCategory.SOTA;
                    else if (IsHrcWasNeeded(d))       cat = WsjtxClient.CallCategory.WAS_NEEDED;
                    else if (IsHrcWasUnconfirmed(d))  cat = WsjtxClient.CallCategory.WAS_UNCONFIRMED;
                    else if (IsHrcDxccUnconfirmed(d)) cat = WsjtxClient.CallCategory.DXCC_UNCONFIRMED;
                    else if (IsHrcZoneNeeded(d))      cat = WsjtxClient.CallCategory.ZONE_NEEDED;
                    else
                    {
                        string matchedRuleId = MatchedAwardRuleId(d);
                        if (matchedRuleId != null) { cat = WsjtxClient.CallCategory.STILL_NEEDED; d.MatchedAwardRuleId = matchedRuleId; }
                        else cat = WsjtxClient.CallCategory.DEFAULT;
                    }
                    break;
            }
            if (_wc.debug) _wc.DebugOutput($"{WsjtxClient.spacer}DeriveCategory: '{d.DeCall()}' pri:{d.Priority} → {cat}");
            return cat;
        }

        // Independent of Category/Call Filters admission by design: a station can be
        // classified NEW_COUNTRY (or anything else) for ranking/queueing purposes and
        // still separately match one of the actively-checked awards -- e.g. turning off
        // "New DXCC" during a DXCC contest must not also silence award alerts for the
        // same stations. This runs for every decode, admitted or not, and plays the
        // "Award Needed" sound (with its own cooldown) when a match is found. It does
        // not affect ranking, Priority, Category, or Call Filters admission in any way --
        // except the weak-signal SNR floor below, which the user wants honored as an
        // absolute gate on every alert type, award or not (2026-07-13: it was previously
        // exempt here, matching ProcessDecodeMsg's own weak-signal check/exception).
        public void CheckAwardAlert(EnqueueDecodeMessage d)
        {
            if (_wc.activeAwardTags.Count == 0) return;
            string call = d.DeCall();
            if (string.IsNullOrEmpty(call)) return;

            if (_wc.ctrl.ignoreWeakSnrCheckBox.Checked && d.Snr <= (int)_wc.ctrl.minSnrNumUpDown.Value && call != _wc.callInProg)
                return;

            string matchedRuleId = MatchedAwardRuleId(d);
            if (matchedRuleId == null) return;
            if (d.MatchedAwardRuleId == null) d.MatchedAwardRuleId = matchedRuleId;

            if (!_wc.IsAlertCooledDown(_wc._awardAlertTimes, call, WsjtxClient.AwardAlertCooldownSecs)) return;
            _wc._awardAlertTimes[call] = DateTime.UtcNow;
            _wc.Sounds.PlaySoundEvent(_wc.ctrl.soundEnabled_AwardNeeded, _wc.ctrl.soundFile_AwardNeeded, call, matchedRuleId);

            // Wave 2 of the notification architecture (WSJTX_Controller/Notify/). Deliberately
            // independent of the 30s sound cooldown right above (WsjtxClient.
            // AwardAlertCooldownSecs/_awardAlertTimes) -- NotificationDefaults gives
            // AwardsNeeded its own, separately-configurable RepeatSeconds/ThrottleMilliseconds,
            // so the beep and the spoken summary can be tuned independently. AwardCount is
            // always 1 today: MatchedAwardRuleId returns a single rule ID, first-match-wins --
            // this payload shape supports a future multi-match AwardTagger without changing
            // the notification code, but nothing here produces more than one yet.
            var awards = new[] { matchedRuleId };
            string awardSummary = NotificationTemplateEngine.Pluralize(1, "award needed");
            _wc.Notify.Publish(new AwardsNeededEvent(call, 1, awards, awardSummary));
        }

        // Returns true if dmsg is associated with a "CQ SOTA" transmission.
        public bool IsSotaCall(EnqueueDecodeMessage emsg)
        {
            if (emsg.IsSota()) return true;
            EnqueueDecodeMessage dmsg = _wc.CqMsg(emsg.DeCall());
            if (dmsg == null) return false;
            return dmsg.IsSota();
        }

        // ── HRC (Ham Radio Center) category helpers ─────────────────────────────────
        // All three read only in-memory HashSets populated at startup / after import.
        // If the HRC database is unavailable, the sets remain empty and these return false.

        // Each guarded by activeAwardTags (the realized, actually-live-tagging cache) rather
        // than activeAwardRuleIds (the raw checkbox state), so it auto-retires only once the
        // equivalent generic award (WAS/DXCC/WAZ) is BOTH checked in the new Still Need list
        // AND actually producing usable live tags -- e.g. checking "DXCC" alone does not
        // suppress this, since the shipped DXCC.ini is Target=COUNT and so never enters
        // activeAwardTags (RuleResult.StillNeeded is only ever populated for Target=All; see
        // Controller.RefreshStillNeedCache()). Previously checked activeAwardRuleIds directly,
        // which silently disabled DXCC-needed alerts the moment the box was checked even though
        // the new system was never actually going to tag anything in its place.
        public bool IsHrcWasNeeded(EnqueueDecodeMessage d)
        {
            if (_wc.hrcNeededStates.Count == 0 || _wc.activeAwardTags.ContainsKey("WAS")) return false;

            string qrzState = null;
            string call = d.DeCall();
            if (!string.IsNullOrEmpty(call) && _wc.lookupManager != null && _wc.lookupManager.Enabled)
            {
                var rec = _wc.lookupManager.Build(call);
                qrzState = rec.State;
            }
            string grid = WsjtxMessage.Grid(d.Message);
            string state = WsjtxClient.ResolveUsState(qrzState, string.IsNullOrEmpty(grid) ? null : WsjtxClient.GridToUsState(grid));
            return !string.IsNullOrEmpty(state) && _wc.hrcNeededStates.Contains(state);
        }

        public bool IsHrcWasUnconfirmed(EnqueueDecodeMessage d)
        {
            if (_wc.hrcUnconfirmedStates.Count == 0 || _wc.activeAwardTags.ContainsKey("WAS")) return false;

            string qrzState = null;
            string call = d.DeCall();
            if (!string.IsNullOrEmpty(call) && _wc.lookupManager != null && _wc.lookupManager.Enabled)
            {
                var rec = _wc.lookupManager.Build(call);
                qrzState = rec.State;
            }
            string grid = WsjtxMessage.Grid(d.Message);
            string state = WsjtxClient.ResolveUsState(qrzState, string.IsNullOrEmpty(grid) ? null : WsjtxClient.GridToUsState(grid));
            return !string.IsNullOrEmpty(state) && _wc.hrcUnconfirmedStates.Contains(state);
        }

        public bool IsHrcDxccUnconfirmed(EnqueueDecodeMessage d)
        {
            if (_wc.hrcUnconfirmedDxcc.Count == 0 || _wc.activeAwardTags.ContainsKey("DXCC")) return false;
            string call = d.DeCall();
            if (string.IsNullOrEmpty(call) || !_wc.lookupManager.Enabled) return false;
            var rec = _wc.lookupManager.Build(call);
            return rec.Dxcc > 0 && _wc.hrcUnconfirmedDxcc.Contains(rec.Dxcc);
        }

        public bool IsHrcZoneNeeded(EnqueueDecodeMessage d)
        {
            if (_wc.hrcNeededZones.Count == 0 || _wc.activeAwardTags.ContainsKey("WAZ")) return false;
            string call = d.DeCall();
            if (string.IsNullOrEmpty(call) || !_wc.lookupManager.Enabled) return false;
            var rec = _wc.lookupManager.Build(call);
            return rec.CqZone > 0 && _wc.hrcNeededZones.Contains(rec.CqZone);
        }

        // Matches a decode against every actively-checked award (activeAwardTags), built by
        // Controller.RefreshStillNeedCache() from whichever Rule Definitions are checked in
        // the Still Need tab. Only a fast in-memory lookup happens here -- the RuleEngine
        // evaluation itself already ran once per rule, at selection/refresh time, not per
        // decode. Returns the matched rule's Id, or null if none matched. The field used to
        // derive the match key depends on each rule's GroupBy; kinds not listed here are
        // never included in activeAwardTags (see RuleEngine.SupportsLiveTag).
        //
        // Thin wrapper around AwardMatcher.Match (Awards/AwardMatcher.cs) -- the actual
        // matching logic lives there, pure and unit-tested, decoupled from live app state.
        // State/Continent are resolved here (cheap); CqZone/Dxcc are handed over as lazy
        // delegates so a LookupManager.Build() call only happens if some active award's
        // GroupBy actually needs it.
        public string MatchedAwardRuleId(EnqueueDecodeMessage d)
        {
            string call = d.DeCall();
            if (string.IsNullOrEmpty(call)) return null;

            string qrzState = null;
            if (_wc.lookupManager != null && _wc.lookupManager.Enabled)
            {
                var stateRec = _wc.lookupManager.Build(call);
                qrzState = stateRec.State;
            }
            string grid = WsjtxMessage.Grid(d.Message);
            string state = WsjtxClient.ResolveUsState(qrzState, string.IsNullOrEmpty(grid) ? null : WsjtxClient.GridToUsState(grid));

            // Stage A6: Continent now comes from d.EffectiveClassification() (Jimmy's own
            // ClassificationEngine, resolved via LookupManager -- same source CqZone/Dxcc
            // below already use) instead of directly off the wire.
            return AwardMatcher.Match(
                _wc.activeAwardTags, call, state, d.EffectiveClassification().Continent,
                cqZoneLookup: () => { var rec = _wc.lookupManager.Enabled ? _wc.lookupManager.Build(call) : null; return rec?.CqZone ?? 0; },
                dxccLookup:   () => { var rec = _wc.lookupManager.Enabled ? _wc.lookupManager.Build(call) : null; return rec?.Dxcc   ?? 0; });
        }

        // Category tag shown in the call-waiting row (e.g. "New DXCC", "WAS Needed").
        public string CategoryTag(EnqueueDecodeMessage d)
        {
            switch (d.Category)
            {
                case WsjtxClient.CallCategory.NEW_COUNTRY:         return "New DXCC";
                case WsjtxClient.CallCategory.NEW_COUNTRY_ON_BAND: return "New DXCC on band";
                case WsjtxClient.CallCategory.ALWAYS_WANTED:       return "Wanted";
                case WsjtxClient.CallCategory.WANTED_CQ:
                    return "";  // pri field already shows the directed-to target
                case WsjtxClient.CallCategory.POTA:                return "POTA";
                case WsjtxClient.CallCategory.SOTA:                return "SOTA";
                case WsjtxClient.CallCategory.WAS_NEEDED:          return "WAS Needed";
                case WsjtxClient.CallCategory.WAS_UNCONFIRMED:     return "WAS Unconf";
                case WsjtxClient.CallCategory.DXCC_UNCONFIRMED:    return "DXCC Unconf";
                case WsjtxClient.CallCategory.ZONE_NEEDED:         return "Zone Needed";
                case WsjtxClient.CallCategory.STILL_NEEDED:        return AwardDisplayName(d) + " Needed";
                default:                               return "";
            }
        }

        // Looks up the display name of whichever active award this message matched
        // (stashed on the message by DeriveCategory/CheckAwardAlert), falling back to a
        // generic label if the rule can't be found (e.g. unchecked between match and display).
        public string AwardDisplayName(EnqueueDecodeMessage d)
        {
            WsjtxClient.ActiveAwardTag tag;
            if (!string.IsNullOrEmpty(d.MatchedAwardRuleId) && _wc.activeAwardTags.TryGetValue(d.MatchedAwardRuleId, out tag))
                return tag.RuleName;
            return "Still";
        }
    }
}
