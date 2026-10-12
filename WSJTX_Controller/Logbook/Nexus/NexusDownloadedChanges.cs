using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace WSJTX_Controller
{
    // What a download says about a logged contact's own fields, kept for the operator to review
    // (2026-10-12). A LoTW, QRZ or eQSL download merges its CONFIRMATIONS at once, as always;
    // where its location for a contact differs from, or would fill in, what is logged, the logged
    // value stays and the difference becomes a proposal here -- one per contact, service and
    // field. Nothing is changed until the operator approves it in Logbook, Sync, Review
    // Downloaded Changes; closing that window applies nothing.
    //
    //   - One list, kept in NexusLog\downloaded-changes.json. It names contacts by Nexus's own
    //     id; it is not a second logbook and holds no contact that could be written from it.
    //   - A repeated download does not grow it: the same proposal is the same entry (its
    //     "last seen" moves), and one the operator declined stays declined unless the service
    //     now says something else.
    //   - A proposal remembers the logged value it was made against. Approved later, it is
    //     applied only if the contact still holds that value -- a contact edited meanwhile is
    //     left alone and the proposal dropped -- and through Nexus's own guarded edit, which
    //     refuses a contact that changed under it.
    //   - A proposal for a contact that is gone, or whose logged value now already equals what
    //     was proposed, is removed.
    //   - Country alone, with the same DXCC entity on both sides, is marked as a name-only
    //     difference: it changes how the country reads, never which entity the contact counts for.
    internal sealed class NexusDownloadedChanges
    {
        internal sealed class Proposal
        {
            public string Key { get; set; }            // contact id | service | field
            public string ContactId { get; set; }
            public string Service { get; set; }        // LOTW, QRZ, EQSL
            public string Field { get; set; }          // State, County, Country, DXCC, CQ zone, ITU zone, Grid
            public string Logged { get; set; }         // the logged value when proposed ("" = blank)
            public string Proposed { get; set; }
            public bool NameOnly { get; set; }         // Country differs, DXCC entity the same
            public bool Declined { get; set; }
            public string Call { get; set; }
            public string Band { get; set; }
            public string Mode { get; set; }
            public ulong WhenUnix { get; set; }
            public string FirstSeenUtc { get; set; }
            public string LastSeenUtc { get; set; }

            // "QRZ: OT9K 20m FT8 2026-10-04 15:00. Country NON-DXCC, QRZ says Belgium (name only, same DXCC entity)."
            public string Sentence()
            {
                string when = DateTimeOffset.FromUnixTimeSeconds((long)WhenUnix).UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                string service = ServiceName(Service);
                string change = string.IsNullOrEmpty(Logged) ? $"{Field} is blank, {service} says {Proposed}" : $"{Field} {Logged}, {service} says {Proposed}";
                return $"{service}: {Call} {Band} {Mode} {when}. {change}" + (NameOnly ? " (name only, same DXCC entity)." : ".");
            }
        }

        internal static string ServiceName(string service) =>
            service == "LOTW" ? "LoTW" : service == "EQSL" ? "eQSL" : service == "QRZ" ? "QRZ" : service;

        // The fields a download row can propose, and how each reads on the logged contact.
        internal static readonly (string Field, string RowTag)[] Fields =
        {
            ("State", "STATE"), ("County", "CNTY"), ("Country", "COUNTRY"), ("DXCC", "DXCC"),
            ("CQ zone", "CQZ"), ("ITU zone", "ITUZ"), ("Grid", "GRIDSQUARE"),
        };

        internal static string LoggedValue(NexusQso q, string field)
        {
            switch (field)
            {
                case "State": return (q.State ?? "").Trim();
                case "County": return (q.ExtraValue("CNTY") ?? "").Trim();
                case "Country": return (q.Country ?? "").Trim();
                case "DXCC": return q.Dxcc.HasValue && q.Dxcc.Value > 0 ? q.Dxcc.Value.ToString(CultureInfo.InvariantCulture) : "";
                case "CQ zone": return (q.ExtraValue("CQZ") ?? "").Trim();
                case "ITU zone": return (q.ExtraValue("ITUZ") ?? "").Trim();
                case "Grid": return (q.Grid ?? "").Trim();
                default: return "";
            }
        }

        // Puts `value` in the contact's field. False: not a value that field can hold.
        internal static bool SetLoggedValue(NexusQso q, string field, string value)
        {
            value = (value ?? "").Trim();
            void Extra(string tag)
            {
                q.Extra.RemoveAll(kv => kv.Count == 2 && string.Equals(kv[0], tag, StringComparison.OrdinalIgnoreCase));
                if (value.Length > 0) q.Extra.Add(new List<string> { tag, value });
            }
            switch (field)
            {
                case "State": q.State = value.Length == 0 ? null : value.ToUpperInvariant(); return true;
                case "County": Extra("CNTY"); return true;
                case "Country": q.Country = value.Length == 0 ? null : value; return true;
                case "DXCC":
                    if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint d) || d == 0) return false;
                    q.Dxcc = d; return true;
                case "CQ zone": Extra("CQZ"); return true;
                case "ITU zone": Extra("ITUZ"); return true;
                case "Grid": q.Grid = value.Length == 0 ? null : value.ToUpperInvariant(); return true;
                default: return false;
            }
        }

        // Does the download's value say something the logged one does not? The same text in
        // another case does not; a zone "04" against "4" does not; a grid that only adds or
        // drops precision (EN34 against EN34RN) does not.
        internal static bool Differs(string field, string logged, string theirs)
        {
            logged = (logged ?? "").Trim(); theirs = (theirs ?? "").Trim();
            if (theirs.Length == 0) return false;                       // the download says nothing
            if (string.Equals(logged, theirs, StringComparison.OrdinalIgnoreCase)) return false;
            if ((field == "DXCC" || field == "CQ zone" || field == "ITU zone") && logged.Length > 0
                && int.TryParse(logged, out int a) && int.TryParse(theirs, out int b) && a == b) return false;
            if (field == "Grid" && logged.Length >= 4 && theirs.Length >= 4)
            {
                int n = Math.Min(logged.Length, theirs.Length);
                if (string.Equals(logged.Substring(0, n), theirs.Substring(0, n), StringComparison.OrdinalIgnoreCase)) return false;
            }
            if (field == "County" && logged.Length > 0)
            {
                // ADIF's "MN,Goodhue" against a plain "Goodhue".
                string Bare(string c) => c.Substring(c.IndexOf(',') + 1).Trim();
                if (string.Equals(Bare(logged), Bare(theirs), StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        // ── The saved list ───────────────────────────────────────────────────────────────────

        internal static string FilePath => Path.Combine(NexusLogbook.Folder, "downloaded-changes.json");
        private static readonly object _fileLock = new object();
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

        internal readonly List<Proposal> Items = new List<Proposal>();

        internal static NexusDownloadedChanges Load(string path = null)
        {
            var list = new NexusDownloadedChanges();
            path = path ?? FilePath;
            lock (_fileLock)
            {
                try
                {
                    if (File.Exists(path))
                        list.Items.AddRange((JsonSerializer.Deserialize<List<Proposal>>(File.ReadAllText(path), Json) ?? new List<Proposal>())
                            .Where(p => p != null && !string.IsNullOrEmpty(p.Key) && !string.IsNullOrEmpty(p.ContactId)));
                }
                catch { }   // unreadable: an empty list; the next download proposes again
            }
            return list;
        }

        internal void Save(string path = null)
        {
            path = path ?? FilePath;
            lock (_fileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path + ".new", JsonSerializer.Serialize(Items, Json), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".new", path);
            }
        }

        internal int PendingCount => Items.Count(p => !p.Declined);

        // One paired download row against its logged contact. Returns how many proposals are NEW
        // to the operator (a repeat of one already listed, or of one already declined, is not).
        internal int Note(string service, NexusQso logged, IDictionary<string, string> row, DateTime utcNow)
        {
            if (logged?.Id == null || row == null) return 0;
            string now = utcNow.ToString("o", CultureInfo.InvariantCulture);
            string V(string tag) => row.TryGetValue(tag, out string v) ? (v ?? "").Trim() : "";
            int added = 0;
            foreach (var (field, tag) in Fields)
            {
                string key = logged.Id + "|" + service + "|" + field;
                string mine = LoggedValue(logged, field), theirs = V(tag);
                var have = Items.FirstOrDefault(p => p.Key == key);
                if (!Differs(field, mine, theirs))
                {
                    if (have != null && theirs.Length > 0) Items.Remove(have);   // they agree now
                    continue;
                }
                bool nameOnly = field == "Country" && LoggedValue(logged, "DXCC").Length > 0
                                && !Differs("DXCC", LoggedValue(logged, "DXCC"), V("DXCC")) && V("DXCC").Length > 0;
                if (have != null && string.Equals(have.Proposed, theirs, StringComparison.OrdinalIgnoreCase) && string.Equals(have.Logged ?? "", mine, StringComparison.OrdinalIgnoreCase))
                {
                    have.LastSeenUtc = now;
                    continue;
                }
                if (have != null) Items.Remove(have);   // the service, or the contact, says something else now: asked again
                Items.Add(new Proposal
                {
                    Key = key, ContactId = logged.Id, Service = service, Field = field, Logged = mine, Proposed = theirs, NameOnly = nameOnly,
                    Call = logged.Call, Band = logged.Band, Mode = logged.Mode, WhenUnix = logged.WhenUnix, FirstSeenUtc = now, LastSeenUtc = now,
                });
                added++;
            }
            return added;
        }

        // Against the log as it is: proposals for contacts that are gone, and proposals the log
        // already agrees with, are dropped. Returns how many were dropped.
        internal int Prune(IEnumerable<NexusQso> rows)
        {
            var byId = rows.Where(q => q.Id != null).GroupBy(q => q.Id).ToDictionary(g => g.Key, g => g.First());
            return Items.RemoveAll(p => !byId.TryGetValue(p.ContactId, out var q) || !Differs(p.Field, LoggedValue(q, p.Field), p.Proposed));
        }

        internal sealed class ApplyResult
        {
            public int Applied, Unchanged, Failed;   // contacts changed fields; left alone (edited since, or gone); refused
            public int Contacts;                      // contacts those applied fields were on
            public List<string> Notes = new List<string>();
            public string Sentence()
            {
                var parts = new List<string> { $"{Applied} change{(Applied == 1 ? "" : "s")} applied to {Contacts} contact{(Contacts == 1 ? "" : "s")}" };
                if (Unchanged > 0) parts.Add($"{Unchanged} left as they are because the contact was changed or removed since");
                if (Failed > 0) parts.Add($"{Failed} could not be saved");
                return string.Join("; ", parts) + ".";
            }
        }

        // Applies the chosen proposals. rows: the log as it is now. edit: saves one changed
        // contact and returns null, or why it was refused (Nexus's guarded edit). Each proposal
        // that is applied, or that no longer fits its contact, leaves the list.
        internal ApplyResult Apply(IEnumerable<Proposal> chosen, IEnumerable<NexusQso> rows, Func<NexusQso, string> edit)
        {
            var result = new ApplyResult();
            var byId = rows.Where(q => q.Id != null).GroupBy(q => q.Id).ToDictionary(g => g.Key, g => g.First());
            foreach (var group in chosen.Where(p => Items.Contains(p)).GroupBy(p => p.ContactId).ToList())
            {
                if (!byId.TryGetValue(group.Key, out var q))
                {
                    foreach (var p in group) { Items.Remove(p); result.Unchanged++; result.Notes.Add($"{p.Call}: the contact is no longer in the log."); }
                    continue;
                }
                var fits = new List<Proposal>();
                foreach (var p in group)
                {
                    // The value it was proposed against must still be the contact's value.
                    if (!string.Equals(LoggedValue(q, p.Field), p.Logged ?? "", StringComparison.OrdinalIgnoreCase))
                    {
                        Items.Remove(p); result.Unchanged++;
                        result.Notes.Add($"{p.Call} {p.Field}: changed since it was proposed (now {(LoggedValue(q, p.Field).Length == 0 ? "blank" : LoggedValue(q, p.Field))}); left as it is.");
                    }
                    else if (!SetLoggedValue(q, p.Field, p.Proposed))
                    {
                        result.Failed++;
                        result.Notes.Add($"{p.Call} {p.Field}: '{p.Proposed}' is not a value that field can hold.");
                    }
                    else fits.Add(p);
                }
                if (fits.Count == 0) continue;
                string refused = null;
                try { refused = edit(q); } catch (Exception ex) { refused = ex.Message; }
                if (refused == null)
                {
                    foreach (var p in fits) Items.Remove(p);
                    result.Applied += fits.Count;
                    result.Contacts++;
                }
                else
                {
                    result.Failed += fits.Count;
                    result.Notes.Add($"{fits[0].Call}: {refused}");
                }
            }
            return result;
        }
    }
}
