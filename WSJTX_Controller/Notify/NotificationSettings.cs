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

        private static Dictionary<NotificationEventType, NotificationPolicy> ClonePolicies(
            Dictionary<NotificationEventType, NotificationPolicy> source)
        {
            var result = new Dictionary<NotificationEventType, NotificationPolicy>();
            foreach (var kv in source) result[kv.Key] = kv.Value.Clone();
            return result;
        }

        public void LoadFromIni(IniFile ini)
        {
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
                if (!string.IsNullOrWhiteSpace(template))
                    policy.Template = template;

                Policies[type] = policy;
            }
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
            }
        }
    }
}
