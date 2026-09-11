using System;
using System.Collections.Generic;

namespace WSJTX_Controller
{
    // INI-backed notification policy, following RadioSettings.cs/JimmySettings.cs's exact
    // LoadFromIni/SaveToIni shape. This is the ONLY class that reads notifyXxx_ INI keys --
    // WsjtxClient/AwardTagger/etc. never touch notification config directly, they only ever
    // call NotificationCenter.Publish(event); NotificationCenter is the only consumer of
    // Policies here. Flat keys, no new INI section (matches the existing radio*/adv*/
    // soundEnabled_* prefix convention, all living in the app's one default [Jimmy] section).
    //
    // Fail-safe by construction: every field starts as a Clone() of NotificationDefaults'
    // code-authoritative value, and is only overwritten when the corresponding INI key both
    // exists and parses successfully. A missing key, an empty value, or a value that fails
    // TryParse all leave the code default untouched -- never a thrown exception, never a
    // half-applied state, never a reason startup can't proceed. This is also what lets a new
    // NotificationEventType added in a future Jimmy version "just work" with no INI migration:
    // it simply isn't looked up here until NotificationDefaults.Policies gains an entry for it.
    public class NotificationSettings
    {
        public Dictionary<NotificationEventType, NotificationPolicy> Policies { get; }
            = ClonePolicies(NotificationDefaults.Policies);

        // Codex #8: a saved notifyTemplate_ that no longer validates (hand-edited INI, or a
        // Jimmy update renamed a variable) is dropped for the code default at load time --
        // previously silently. LoadFromIni records the rejected text here so the Options UI can
        // tell the operator their wording was not accepted and Jimmy is using the default.
        public Dictionary<NotificationEventType, string> RejectedTemplates { get; }
            = new Dictionary<NotificationEventType, string>();

        // 2026-09-05: which of Jimmy's two alternating slots the routine receive-side clauses are
        // spoken for -- resolved against the CURRENT role of the slot whose period just ended
        // (see ReceiveSideScope), so RxSideOnly/TxSideOnly follow a role flip with no settings
        // change. Two independent scopes: one for the side name (ReceiveSideId), one for the
        // "N available stations" count (ReceiveCycleSummary). Default Both on each = today's
        // behaviour (both slots announce), so an existing INI with neither key loads unchanged.
        // Only meaningful in the advanced call layout; the simple layout has one list and always
        // keeps its count.
        public ReceiveSideScope ReceiveSideIdScope { get; set; } = ReceiveSideScope.Both;
        public ReceiveSideScope ReceiveCountScope { get; set; } = ReceiveSideScope.Both;

        // Opt-in, default off (2026-09-10): when the idle Receive cycle summary was showing real
        // words (e.g. "1 wanted") and a later receive cycle recalculates it with nothing to say,
        // clear that stale text from the visible status area instead of leaving Jimmy's ordinary
        // "keep the last real line" behaviour show it forever. Speaks nothing, records no
        // Notification History entry either way. Never engages for a disabled row or a template
        // with no {Field} references at all (Controller.RenderStatusVisible's own guard) -- only
        // for a currently-enabled, genuinely data-driven summary whose fields happen to have
        // nothing to report THIS cycle. Default false: an existing profile's status area behaves
        // exactly as before.
        public bool ClearReceiveCycleSummaryWhenEmpty { get; set; } = false;

        private static Dictionary<NotificationEventType, NotificationPolicy> ClonePolicies(
            Dictionary<NotificationEventType, NotificationPolicy> source)
        {
            var result = new Dictionary<NotificationEventType, NotificationPolicy>();
            foreach (var kv in source) result[kv.Key] = kv.Value.Clone();
            return result;
        }

        public void LoadFromIni(IniFile ini)
        {
            RejectedTemplates.Clear();
            foreach (NotificationEventType type in Enum.GetValues(typeof(NotificationEventType)))
            {
                // Clone the code default fresh each load (not the possibly-already-overridden
                // in-memory value) so re-loading (e.g. Options cancel/reopen in a future UI)
                // never compounds a stale override on top of itself.
                NotificationPolicy policy = NotificationDefaults.Policies.TryGetValue(type, out var def)
                    ? def.Clone()
                    : new NotificationPolicy();

                if (ini.KeyExists($"notifyEnabled_{type}"))
                    policy.Enabled = ini.Read($"notifyEnabled_{type}") == "True";

                if (Enum.TryParse(ini.Read($"notifyPriority_{type}"), out NotificationPriority priority))
                    policy.Priority = priority;

                if (int.TryParse(ini.Read($"notifyRepeatSeconds_{type}"), out int repeatSeconds) && repeatSeconds >= 0)
                    policy.RepeatSeconds = repeatSeconds;

                if (int.TryParse(ini.Read($"notifyThrottleMs_{type}"), out int throttleMs) && throttleMs >= 0)
                    policy.ThrottleMilliseconds = throttleMs;

                string template = ini.Read($"notifyTemplate_{type}");

                // 2026-09-05 split migration: a saved copy of the PRE-split "Receive cycle
                // summary" default (which carried the state verb {Status} and the mode
                // descriptor {Mode} inline) is treated as "unedited" -- dropped so the new
                // three-clause default takes over. Keeping it would double "Receiving" /
                // "Listen mode" against the new ReceiveStateSummary / OperatingModeSummary
                // rows. An operator's OWN edited wording is left untouched; its now-unknown
                // {Status}/{Mode} tokens then surface through RejectedTemplates like any other
                // stale reference, so the change is visible rather than silent.
                if (type == NotificationEventType.ReceiveCycleSummary
                    && template == "{Status}, {AvailableCount} {Stations}{ToYou}{NewDxcc}{Wanted}{Awards}{Mode}{Prompt}.")
                    template = null;

                // 2026-09-08 peer-in-busy-line migration: a saved copy of the PRE-2.0.69
                // "Smart Start target busy" default named no other station. It is treated as
                // "unedited" -- dropped so the new {Phrase} default takes over, which names the
                // other station (and its report when the decode carried one) and degrades to
                // the same "another station" wording only when neither could be parsed. Without
                // this, every profile saved by 2.0.67/2.0.68 keeps the peerless string forever
                // and the 2.0.69 wording never appears. An operator's OWN edited wording is left
                // untouched; a hand-typed copy of the exact old default is the acceptable
                // false-positive (it renders identically in the peerless case anyway).
                if (type == NotificationEventType.SmartStartTargetBusy
                    && template == "{Target} is working another station.")
                    template = null;

                // 2026-09-08 waiting-phrase migration (KR4NO live-radio audit): a saved copy of
                // the PRE-2.0.67 "Smart Start waiting" default carried the raw {Progress} token.
                // {Progress} is "1 of 2" only for a true silence-progress observation and empty
                // for the legitimate non-progress revalidation-decline path -- with this template
                // that rendered "KR4NO not heard, waiting ." Treated as "unedited" -- dropped so
                // the current {Phrase} default takes over (a fully worded sentence for every
                // case, progress or not). An operator's OWN edited wording is left untouched; a
                // hand-typed copy of the exact old default is the acceptable false-positive.
                if (type == NotificationEventType.SmartStartWaiting
                    && template == "{Target} not heard, waiting {Progress}.")
                    template = null;

                if (!string.IsNullOrWhiteSpace(template))
                {
                    // A saved template that no longer validates against this type's variable
                    // registry (e.g. hand-edited ini, or a Jimmy update renamed a variable)
                    // falls back to the code default rather than shipping a broken announcement.
                    // The rejected text is recorded (Codex #8) so the UI can surface it -- the
                    // operator should not believe a bad template was accepted.
                    if (NotificationVariableRegistry.Validate(template, type) == null)
                        policy.Template = template;
                    else
                        RejectedTemplates[type] = template;
                }

                if (Enum.TryParse(ini.Read($"notifyTiming_{type}"), out NotificationTiming timing))
                    policy.Timing = timing;

                if (ini.KeyExists($"notifyDeferWhileTx_{type}"))
                    policy.DeferWhileTransmitting = ini.Read($"notifyDeferWhileTx_{type}") == "True";

                // SpeakWhen (2026-09-02, Item 1) is the delivery-timing control. An explicit
                // notifySpeakWhen_ key wins. Otherwise migrate a pre-existing user's legacy
                // Timing/DeferWhileTransmitting pair to the equivalent SpeakWhen so their
                // configured behaviour carries over rather than silently resetting to Now:
                //   NextPeriodBoundary  -> AfterRx   (batched to the receive-cycle grid)
                //   DeferWhileTransmitting (Immediate) -> AfterTx
                //   neither             -> the code default (usually Now)
                // A legacy notifySpeakWhen_==Never is NOT a timing any more (2026-09-04):
                // "never spoken" moved to SpeakCondition.Never below. Drop the timing to the code
                // default and let the Condition migration pick up the "never" intent.
                bool legacySpeakWhenNever = false;
                if (Enum.TryParse(ini.Read($"notifySpeakWhen_{type}"), out SpeakWhen speakWhen))
                {
                    if (speakWhen == SpeakWhen.Never) legacySpeakWhenNever = true;
                    else policy.SpeakWhen = speakWhen;
                }
                else if (policy.Timing == NotificationTiming.NextPeriodBoundary)
                    policy.SpeakWhen = SpeakWhen.AfterRx;
                else if (policy.DeferWhileTransmitting)
                    policy.SpeakWhen = SpeakWhen.AfterTx;

                // SpeakWhenSet (2026-09-09 multi-delivery-point work): a published notification
                // can be spoken at MORE THAN ONE boundary. notifySpeakWhenSet_ is a comma-joined
                // list of SpeakWhen names and is authoritative when present. Absent -> leave
                // SpeakWhenSet null, so NotificationPolicy.EffectiveSpeakWhenSet() falls back to
                // { policy.SpeakWhen } and every pre-multi-delivery INI keeps byte-identical
                // timing. Unparseable tokens are skipped; an all-invalid value is ignored
                // (fail-safe, like every other key here). SpeakWhen (singular) is kept in sync
                // to the first element for the back-compat readers (routine-status fragment
                // composition, a clean rollback to an older build).
                string speakWhenSetRaw = ini.Read($"notifySpeakWhenSet_{type}");
                if (!string.IsNullOrWhiteSpace(speakWhenSetRaw))
                {
                    var set = new List<SpeakWhen>();
                    foreach (var tok in speakWhenSetRaw.Split(','))
                        if (Enum.TryParse(tok.Trim(), out SpeakWhen w) && !set.Contains(w))
                            set.Add(w);
                    if (set.Count > 0)
                    {
                        policy.SpeakWhenSet = set;
                        policy.SpeakWhen = set[0];
                    }
                }

                // SpeakCondition (2026-09-04): the 4-way eligibility control. An explicit
                // notifyCondition_ key wins. Otherwise migrate:
                //   legacy notifySpeakWhen_==Never       -> SpeakCondition.Never
                //   legacy notifyDuringQso_==Suppress    -> SpeakCondition.OutsideQsoOnly
                //   legacy notifyDuringQso_==SpeakNormally-> SpeakCondition.Always
                //   nothing on disk                     -> code default (Always)
                if (Enum.TryParse(ini.Read($"notifyCondition_{type}"), out SpeakCondition condition))
                    policy.Condition = condition;
                else if (legacySpeakWhenNever)
                    policy.Condition = SpeakCondition.Never;
                else
                {
                    string legacyDuringQso = ini.Read($"notifyDuringQso_{type}");
                    if (legacyDuringQso == "Suppress")
                        policy.Condition = SpeakCondition.OutsideQsoOnly;
                    else if (legacyDuringQso == "SpeakNormally")
                        policy.Condition = SpeakCondition.Always;
                }

                if (ini.KeyExists($"notifySuppressUnchanged_{type}"))
                    policy.SuppressUnchanged = ini.Read($"notifySuppressUnchanged_{type}") == "True";

                // 2026-09-09: the per-notification status-area delivery choice. Missing /
                // unparseable -> the code default (Normal), so a pre-2026-09-09 INI is unchanged.
                if (Enum.TryParse(ini.Read($"notifyStatusDelivery_{type}"), out NotificationStatusDelivery statusDelivery))
                    policy.StatusDelivery = statusDelivery;

                Policies[type] = policy;
            }

            // Receive-side role scopes (2026-09-05). Missing / unparseable key -> the code
            // default (Both), so a pre-2026-09-05 INI keeps today's behaviour untouched.
            if (Enum.TryParse(ini.Read("notifyReceiveSideIdScope"), out ReceiveSideScope sideScope))
                ReceiveSideIdScope = sideScope;
            else
                ReceiveSideIdScope = ReceiveSideScope.Both;

            if (Enum.TryParse(ini.Read("notifyReceiveCountScope"), out ReceiveSideScope countScope))
                ReceiveCountScope = countScope;
            else
                ReceiveCountScope = ReceiveSideScope.Both;

            // Default false (missing key) -- an existing profile's status area is unchanged.
            ClearReceiveCycleSummaryWhenEmpty = ini.Read("notifyClearReceiveCycleSummaryWhenEmpty") == "True";
        }

        public void SaveToIni(IniFile ini)
        {
            foreach (var kv in Policies)
            {
                NotificationEventType type = kv.Key;
                NotificationPolicy policy = kv.Value;
                ini.Write($"notifyEnabled_{type}", policy.Enabled.ToString());
                ini.Write($"notifyPriority_{type}", policy.Priority.ToString());
                ini.Write($"notifyRepeatSeconds_{type}", policy.RepeatSeconds.ToString());
                ini.Write($"notifyThrottleMs_{type}", policy.ThrottleMilliseconds.ToString());
                ini.Write($"notifyTemplate_{type}", policy.Template);
                ini.Write($"notifySpeakWhen_{type}", policy.SpeakWhen.ToString());
                // The full multi-delivery set (2026-09-09). Always written from the effective
                // set so a re-load is a faithful round trip even for a policy still on a single
                // legacy boundary; notifySpeakWhen_ above stays the primary for older builds.
                ini.Write($"notifySpeakWhenSet_{type}", string.Join(",", policy.EffectiveSpeakWhenSet()));
                ini.Write($"notifyCondition_{type}", policy.Condition.ToString());
                // Legacy key kept in sync for a clean rollback to a pre-2026-09-04 build
                // (which only understands Suppress / SpeakNormally). DuringQsoOnly and Never
                // have no legacy equivalent and map to the nearest ("speak normally").
                ini.Write($"notifyDuringQso_{type}",
                    policy.Condition == SpeakCondition.OutsideQsoOnly ? "Suppress" : "SpeakNormally");
                ini.Write($"notifyTiming_{type}", policy.Timing.ToString());
                ini.Write($"notifyDeferWhileTx_{type}", policy.DeferWhileTransmitting.ToString());
                ini.Write($"notifySuppressUnchanged_{type}", policy.SuppressUnchanged.ToString());
                ini.Write($"notifyStatusDelivery_{type}", policy.StatusDelivery.ToString());
            }

            ini.Write("notifyReceiveSideIdScope", ReceiveSideIdScope.ToString());
            ini.Write("notifyReceiveCountScope", ReceiveCountScope.ToString());
            ini.Write("notifyClearReceiveCycleSummaryWhenEmpty", ClearReceiveCycleSummaryWhenEmpty.ToString());
        }
    }
}
