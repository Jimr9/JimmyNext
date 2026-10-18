using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace WSJTX_Controller
{
    // Separate logbooks for profiles (handoff 33, 2026-10-14). A special event station such as
    // K4B keeps its contacts in a logbook of its own, apart from the operator's personal log, and
    // that logbook can be EXPORT ONLY: nothing in it is ever uploaded or downloaded by Jimmy.
    //
    //   - The MAIN logbook (id "") is the one every install already has, in DataRoot\NexusLog.
    //     A profile with no logbook chosen uses it, so existing profiles are unchanged.
    //   - A NAMED logbook lives in DataRoot\Logbooks\<id>\NexusLog: the same Nexus storage, outbox,
    //     projection and Jimmy meta as the main one (NexusLogbook takes its Folder from here), so
    //     every screen, award, worked-before check, export and contest watermark follows the
    //     logbook in use. The engine is started with --log-dir set to it.
    //   - Each profile names its logbook (profile key "logbookId"). Several profiles may share
    //     one. The id is stable: renaming or cloning a profile keeps it; deleting a profile never
    //     deletes a logbook or its contacts.
    //   - A new logbook starts EMPTY (its ACTIVE marker is written at creation, so the startup
    //     move of the old personal Jimmy log never runs into it) and EXPORT ONLY.
    //   - Uploads: export only blocks every way an upload or a download can start -- Alt+U, the
    //     real-time upload after a contact, the automatic and manual syncs, retries. Pending
    //     contacts stay pending; nothing is marked as sent.
    //   - In any logbook, a contact is only uploaded if it was made as the station call the
    //     profile uses now (STATION_CALLSIGN). One made as another call stays pending. A contact
    //     with no station call recorded is taken as the main logbook owner's (contacts logged by
    //     older versions); in a named logbook it is held.
    //   - The main logbook owner's call (2026-10-17) is the station call most of its contacts
    //     record; with none recorded at all, the call in use (as before); with two calls equally
    //     common, unknown -- and a contact with no station call is then held, never sent or
    //     confirmed under a guess. The same identity decides uploads (MayUpload) and which LoTW
    //     confirmations land on which contacts (ConfirmationCall, NexusReportPairing), and a
    //     portable /P, /M, /MM, /AM or /QRP is the same call for a confirmation, as in Nexus.
    // The list of logbooks is DataRoot\Logbooks\logbooks.ini, shared by every profile.
    public static class LogbookBooks
    {
        public const string MainId = "";
        public const string MainName = "Main logbook";

        public sealed class Book
        {
            public string Id, Name;
            public bool ExportOnly;
        }

        // Test override of the data root; null in production.
        internal static string TestRoot;
        private static string Root => TestRoot ?? LookupManager.DataRoot;
        private static string BooksFolder => Path.Combine(Root, "Logbooks");
        private static string ListPath => Path.Combine(BooksFolder, "logbooks.ini");

        // The logbook in use: set once a profile is read, before the logbook is touched.
        public static string ActiveId { get; private set; } = MainId;

        // The station call uploads are made as (the profile's own); set by the main window.
        public static Func<string> StationCallNow = () => null;

        public static string FolderOf(string id) =>
            string.IsNullOrEmpty(id) ? Path.Combine(Root, "NexusLog") : Path.Combine(BooksFolder, id, "NexusLog");

        public static string ActiveFolder => FolderOf(ActiveId);

        // The profile's choice, as read from it. An id no longer in the list falls back to the
        // main logbook only if its folder is gone too; a folder that is there is used (a list
        // file lost or copied without it must not send contacts to the wrong log).
        public static void Use(string id)
        {
            id = Clean(id);
            if (id.Length > 0 && Find(id) == null && !Directory.Exists(FolderOf(id))) id = MainId;
            ActiveId = id;
        }

        // The station calls the main logbook's contacts record, with how many each (read from the
        // read copy by NexusLogbookService). Null: not known.
        public static Func<IEnumerable<(string Call, int Count)>> MainStationCalls = () => null;

        internal static void ResetForTests() { ActiveId = MainId; TestRoot = null; StationCallNow = () => null; MainStationCalls = () => null; }

        // The owner's call from the main logbook's recorded station calls: the most common; null
        // when none is recorded; "" when two are equally common (unknown).
        internal static string OwnerFrom(IEnumerable<(string Call, int Count)> calls)
        {
            var top = (calls ?? Enumerable.Empty<(string Call, int Count)>())
                .Where(c => !string.IsNullOrWhiteSpace(c.Call) && c.Count > 0)
                .GroupBy(c => c.Call.Trim().ToUpperInvariant())
                .Select(g => (Call: g.Key, Count: g.Sum(x => x.Count)))
                .OrderByDescending(c => c.Count).Take(2).ToList();
            if (top.Count == 0) return null;
            return top.Count > 1 && top[1].Count == top[0].Count ? "" : top[0].Call;
        }

        // Whose a contact with no station call recorded is, in the logbook in use: the main
        // logbook's owner (the call in use when no contact records one); null -- held -- in a
        // named logbook, or when the owner is unknown.
        public static string BlankCallBelongsTo()
        {
            if (ActiveId.Length != 0) return null;
            string owner = OwnerFrom(MainStationCalls?.Invoke());
            if (owner == null)
            {
                string now = (StationCallNow?.Invoke() ?? "").Trim().ToUpperInvariant();
                return now.Length > 0 ? now : null;
            }
            return owner.Length > 0 ? owner : null;
        }

        // The call a contact was made under, for its confirmations: its station call, or for one
        // with none recorded, BlankCallBelongsTo(). Null: not known.
        public static string ConfirmationCall(string contactStationCall)
        {
            string was = (contactStationCall ?? "").Trim();
            return was.Length > 0 ? was.ToUpperInvariant() : BlankCallBelongsTo();
        }

        // A call without the suffix that only says how it was operated (Nexus 1.19's
        // reconcile::without_portable_suffix): W1ABC/P is W1ABC; FG/W1ABC and W1ABC/9 stay as they are.
        public static string PortableBase(string call)
        {
            string c = (call ?? "").Trim().ToUpperInvariant();
            int slash = c.LastIndexOf('/');
            if (slash > 0)
            {
                string suffix = c.Substring(slash + 1);
                if (suffix == "P" || suffix == "M" || suffix == "MM" || suffix == "AM" || suffix == "QRP") return c.Substring(0, slash);
            }
            return c;
        }

        public static Book Active => Find(ActiveId) ?? new Book { Id = ActiveId, Name = ActiveId.Length == 0 ? MainName : ActiveId, ExportOnly = ActiveId.Length > 0 };

        public static List<Book> All()
        {
            var list = new List<Book> { new Book { Id = MainId, Name = MainName, ExportOnly = false } };
            list.AddRange(ReadList());
            return list;
        }

        public static Book Find(string id)
        {
            id = Clean(id);
            if (id.Length == 0) return new Book { Id = MainId, Name = MainName, ExportOnly = false };
            return ReadList().FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        // A new, empty, export-only logbook. Its id is made from the name and is never reused.
        public static Book Create(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new ArgumentException("A logbook needs a name.");
            if (string.Equals(name, MainName, StringComparison.OrdinalIgnoreCase) || ReadList().Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"There is already a logbook named {name}.");
            string stem = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
            if (stem.Length == 0) stem = "logbook";
            if (stem.Length > 24) stem = stem.Substring(0, 24).Trim('-');
            string id;
            do id = stem + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            while (Directory.Exists(Path.Combine(BooksFolder, id)));
            string folder = FolderOf(id);
            Directory.CreateDirectory(folder);
            // Empty from the start: the startup move of the old Jimmy logbook looks for this.
            File.WriteAllText(Path.Combine(folder, "ACTIVE"), $"new {DateTime.Now:u}\ncontacts 0\ndifferences 0\nlogbook {name}\n", new UTF8Encoding(false));
            var book = new Book { Id = id, Name = name, ExportOnly = true };
            var all = ReadList();
            all.Add(book);
            WriteList(all);
            return book;
        }

        public static void SetExportOnly(string id, bool exportOnly)
        {
            id = Clean(id);
            if (id.Length == 0) return;   // the main logbook's uploads are set in Options, as always
            var all = ReadList();
            var b = all.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (b == null) return;
            b.ExportOnly = exportOnly;
            WriteList(all);
        }

        // Null when uploads may run for the logbook in use; else why not, said to the operator.
        public static string UploadsBlockedMessage
        {
            get
            {
                var b = Active;
                return b.ExportOnly ? $"Uploads and downloads are off for the {b.Name} logbook: it is export only. Use Export ADIF in the Logbook window." : null;
            }
        }

        public static bool UploadsBlocked => UploadsBlockedMessage != null;

        // Whether a pending contact may be uploaded by this profile: made as the station call in
        // use now. Blank: the main logbook owner's (older versions did not always record it).
        public static bool MayUpload(string contactStationCall)
        {
            if (UploadsBlocked) return false;
            string now = (StationCallNow?.Invoke() ?? "").Trim();
            string was = (contactStationCall ?? "").Trim();
            if (was.Length == 0)
            {
                // The owner's (2026-10-17): sent only while the profile uses the owner's call, so a
                // profile on another call never signs the owner's contacts. No call recorded in the
                // whole logbook: the call in use, as before.
                if (ActiveId.Length != 0) return false;
                string owner = OwnerFrom(MainStationCalls?.Invoke());
                if (owner == null) return true;
                return owner.Length > 0 && now.Length > 0 && string.Equals(owner, now, StringComparison.OrdinalIgnoreCase);
            }
            return now.Length > 0 && string.Equals(was, now, StringComparison.OrdinalIgnoreCase);
        }

        // Count of contacts the last pending-upload read left out because they were made as
        // another station call (said with Alt+U's result).
        public static int LastHeldForOtherCall;

        // One short line for the operator: which logbook, and whether it uploads.
        public static string Describe(string profileName) =>
            $"Profile {profileName}, {Active.Name}{(Active.ExportOnly ? ", export only" : "")}.";

        private static string Clean(string id) => (id ?? "").Trim();

        private static List<Book> ReadList()
        {
            var list = new List<Book>();
            try
            {
                if (!File.Exists(ListPath)) return list;
                Book cur = null;
                foreach (var line in File.ReadAllLines(ListPath))
                {
                    string t = line.Trim();
                    if (t.StartsWith("[") && t.EndsWith("]"))
                    {
                        cur = new Book { Id = t.Substring(1, t.Length - 2), Name = t.Substring(1, t.Length - 2), ExportOnly = true };
                        list.Add(cur);
                        continue;
                    }
                    int eq = t.IndexOf('=');
                    if (cur == null || eq < 0) continue;
                    string k = t.Substring(0, eq).Trim(), v = t.Substring(eq + 1).Trim();
                    if (k.Equals("name", StringComparison.OrdinalIgnoreCase) && v.Length > 0) cur.Name = v;
                    else if (k.Equals("exportOnly", StringComparison.OrdinalIgnoreCase)) cur.ExportOnly = !v.Equals("False", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
            return list;
        }

        private static void WriteList(List<Book> books)
        {
            Directory.CreateDirectory(BooksFolder);
            var sb = new StringBuilder();
            sb.AppendLine("; Jimmy Next logbooks. Each [id] is a folder beside this file.");
            foreach (var b in books)
            {
                sb.AppendLine($"[{b.Id}]");
                sb.AppendLine($"name={b.Name.Replace("\r", " ").Replace("\n", " ")}");
                sb.AppendLine($"exportOnly={(b.ExportOnly ? "True" : "False")}");
            }
            string tmp = ListPath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(ListPath)) File.Replace(tmp, ListPath, null);
            else File.Move(tmp, ListPath);
        }
    }
}
