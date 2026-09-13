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
                    else
                    {
                        // Any actively-checked award (WAS/DXCC/WAZ included -- they're auto-
                        // checked for every install, see Controller's activeAwardRuleIds
                        // migration, so this covers what the old hardcoded HRC-database checks
                        // used to) is tried Needed-first, then Unconfirmed -- a station that's
                        // still genuinely needed is reported as needed, never as merely
                        // unconfirmed, even if it happens to also be worked-unconfirmed for a
                        // different checked award.
                        string neededRuleId = MatchedAwardRuleId(d);
                        if (neededRuleId != null) { cat = WsjtxClient.CallCategory.STILL_NEEDED; d.MatchedAwardRuleId = neededRuleId; }
                        else
                        {
                            string unconfirmedRuleId = MatchedUnconfirmedAwardRuleId(d);
                            if (unconfirmedRuleId != null) { cat = WsjtxClient.CallCategory.STILL_UNCONFIRMED; d.MatchedAwardRuleId = unconfirmedRuleId; }
                            else cat = WsjtxClient.CallCategory.DEFAULT;
                        }
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

            // Added 2026-08-10: never interrupt an active exchange with an award-needed alert
            // for band activity in general -- root-caused live from a real QSO where "KR7H, 1
            // award needed"/"AA5QJ, 1 award needed"/etc. kept firing mid-contact, competing with
            // the actual QSO status announcements ("Transmitting, W1XI, received +02.") for the
            // screen reader's attention. The station itself is still tagged/queued normally
            // (this only suppresses the spoken/beeped interruption); nothing is lost, it's just
            // not spoken RIGHT NOW while the operator is busy with someone else.
            if (_wc.callInProg != null) return;

            if (_wc.ctrl.ignoreWeakSnrCheckBox.Checked && d.Snr <= (int)_wc.ctrl.minSnrNumUpDown.Value && call != _wc.callInProg)
                return;

            string matchedRuleId = MatchedAwardRuleId(d);
            if (matchedRuleId == null) return;
            if (d.MatchedAwardRuleId == null) d.MatchedAwardRuleId = matchedRuleId;

            if (!_wc.IsAlertCooledDown(_wc._awardAlertTimes, call, WsjtxClient.AwardAlertCooldownSecs)) return;
            _wc._awardAlertTimes[call] = DateTime.UtcNow;
            _wc.Sounds.PlaySoundEvent(_wc.ctrl.soundEnabled_AwardNeeded, _wc.ctrl.soundFile_AwardNeeded, call, matchedRuleId);

            // Removed 2026-08-10: this used to also Notify.Publish(AwardsNeededEvent(...)) here,
            // a standalone spoken "{call}, 1 award needed" announcement independent of the
            // routine status line. WsjtxClient.Display.cs's ShowStatus() already reports this,
            // grouped by award type (SnapshotNeededAwardCounts -> the "needed" clause folded
            // into callsWaiting), every routine Receiving-only summary -- that mechanism already
            // covers everything this standalone announcement did, without ever competing with
            // real-time QSO status the way this one did (root-caused live, 2026-08-10, from a
            // real QSO with W1XI: "KR7H, 1 award needed" kept firing mid-contact). The sound
            // cue above is unaffected -- still a real-time "something matched" cue, independent
            // of when the routine status line next gets around to saying so.
        }

        // Returns true if dmsg is associated with a "CQ SOTA" transmission.
        public bool IsSotaCall(EnqueueDecodeMessage emsg)
        {
            if (emsg.IsSota()) return true;
            EnqueueDecodeMessage dmsg = _wc.CqMsg(emsg.DeCall());
            if (dmsg == null) return false;
            return dmsg.IsSota();
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

        // Same as MatchedAwardRuleId, against each active award's UnconfirmedSet instead of its
        // Set -- see AwardMatcher.MatchUnconfirmed's own comment.
        public string MatchedUnconfirmedAwardRuleId(EnqueueDecodeMessage d)
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

            return AwardMatcher.MatchUnconfirmed(
                _wc.activeAwardTags, call, state, d.EffectiveClassification().Continent,
                cqZoneLookup: () => { var rec = _wc.lookupManager.Enabled ? _wc.lookupManager.Build(call) : null; return rec?.CqZone ?? 0; },
                dxccLookup:   () => { var rec = _wc.lookupManager.Enabled ? _wc.lookupManager.Build(call) : null; return rec?.Dxcc   ?? 0; });
        }

        // Category tag shown in the call-waiting row (e.g. "New DXCC", "WAS Needed"). WAS_NEEDED/
        // WAS_UNCONFIRMED/DXCC_UNCONFIRMED/ZONE_NEEDED are never assigned by DeriveCategory any
        // more (see its own comment) -- their cases stay only as a harmless fallback, matching
        // the enum's own "never remove" comment. STILL_NEEDED/STILL_UNCONFIRMED are the real
        // path for every checked award now, WAS/DXCC/WAZ included; the three keep their original
        // short wording (WAS Needed/WAS Unconf/DXCC Unconf/Zone Needed) since operators already
        // know those, while any other award (including new ones like 5-Band DXCC) gets its own
        // Rule Definition name.
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
                case WsjtxClient.CallCategory.STILL_NEEDED:
                    if (d.MatchedAwardRuleId == "WAS") return "WAS Needed";
                    if (d.MatchedAwardRuleId == "WAZ") return "Zone Needed";
                    return AwardDisplayName(d) + " Needed";
                case WsjtxClient.CallCategory.STILL_UNCONFIRMED:
                    if (d.MatchedAwardRuleId == "WAS")  return "WAS Unconf";
                    if (d.MatchedAwardRuleId == "DXCC") return "DXCC Unconf";
                    return AwardDisplayName(d) + " Unconf";
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
