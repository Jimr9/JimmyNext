using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

// Change this to match your program's normal namespace
namespace WSJTX_Controller
{
    public class IniFile   // revision 12
    {
        string Path;
        string EXE = Assembly.GetExecutingAssembly().GetName().Name;

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        static extern long WritePrivateProfileString(string Section, string Key, string Value, string FilePath);

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        static extern int GetPrivateProfileString(string Section, string Key, string Default, StringBuilder RetVal, int Size, string FilePath);

        public IniFile(string IniPath = null)
        {
            Path = new FileInfo(IniPath ?? EXE + ".ini").FullName;
        }

        // Profiles feature, 2026-08-24: "Save Current Configuration As Profile" flushes every
        // setting to this instance's own file (the normal save path, unchanged) and then needs
        // to copy that exact file to a new named-profile path -- this is the only way it learns
        // where "this exact file" actually lives.
        public string FilePath => Path;

        // ===== Batched saves (perf), 2026-09-23 =====
        //
        // Options OK, a clean shutdown, and Save Profile As each fire a long run of Write() calls
        // back-to-back (SaveAllSettingsToIniFile alone is 150+). Left in immediate mode each one
        // is its own WritePrivateProfileString call -- a separate P/Invoke plus (depending on
        // Windows' own internal write-behind cache) potentially its own disk hit. BeginBatch()
        // switches Write/DeleteKey/DeleteSection to only record the operation in memory; nothing
        // touches disk until CommitBatch() replays the whole queue against the CURRENT file
        // contents and swaps in the result with one atomic rename. Outside a batch, Write() is
        // untouched -- same single immediate WritePrivateProfileString call as before -- so every
        // "persist this one setting right now" call site (SetAndPersistMyContinent etc.) keeps
        // working exactly as it does today.
        private List<PendingOp> _pendingOps;

        private struct PendingOp
        {
            public string Section;
            public string Key;    // null => whole-section delete (mirrors DeleteSection's Write(null,null,section))
            public string Value;  // null (Key non-null) => single-key delete (mirrors DeleteKey)
        }

        public bool IsBatching => _pendingOps != null;

        // ===== Shared settings, 2026-09-29 (see SharedSettings) =====
        // A profile's ini with Shared.ini attached: the keys of every group Shared.ini has finished
        // moving over (SharedSettings.MigratedGroups) are read from and written to the shared file
        // unless this profile has its own for that group. A batch on this file batches the shared
        // file with it.
        private IniFile _shared;
        private HashSet<string> _sharedGroups = new HashSet<string>();
        private bool _sharedBatchOwned;
        internal IniFile Shared => _shared;
        internal bool SharesGroup(string group) => _shared != null && _sharedGroups.Contains(group);

        // Attaches nothing when the shared file has finished no group.
        internal void AttachShared(IniFile shared)
        {
            var groups = SharedSettings.MigratedGroups(shared);
            if (groups.Count == 0) return;
            _shared = shared;
            _sharedGroups = groups;
        }

        private IniFile Route(string key, string section)
        {
            if (_shared == null || section != null) return this;
            string group = SharedSettings.GroupOf(key);
            if (group == null || !_sharedGroups.Contains(group) || ReadOwn(SharedSettings.ProfileOnlyKey(group)) == "True") return this;
            return _shared;
        }

        public void BeginBatch()
        {
            if (_pendingOps != null)
                throw new InvalidOperationException(
                    $"IniFile.BeginBatch: a batch is already in progress for '{Path}' -- nested/overlapping batches are not supported.");
            _pendingOps = new List<PendingOp>();
            if (_shared != null && !_shared.IsBatching) { _shared.BeginBatch(); _sharedBatchOwned = true; }
        }

        // Applies every queued Write/DeleteKey/DeleteSection since BeginBatch() as ONE atomic
        // file replace, preserving every section/key this batch never touched (including ones no
        // current code even knows about -- a newer version's forward-compatible keys, or anything
        // hand-edited into the file). Always exits batch mode, even if the replay/write itself
        // throws (the pending queue is taken and cleared before any disk I/O runs), so a failed
        // commit can never leave the IniFile stuck thinking a batch is still open.
        public void CommitBatch()
        {
            if (_pendingOps == null)
                throw new InvalidOperationException($"IniFile.CommitBatch: no batch is in progress for '{Path}'.");
            var ops = _pendingOps;
            _pendingOps = null;
            try
            {
                if (ops.Count > 0) ApplyOpsAtomically(ops); // nothing queued -- don't touch disk at all
            }
            catch
            {
                if (_sharedBatchOwned) { _sharedBatchOwned = false; _shared.AbortBatch(); }
                throw;
            }
            if (_sharedBatchOwned) { _sharedBatchOwned = false; _shared.CommitBatch(); }
        }

        // Discards every queued write since BeginBatch() without touching disk. Used when the
        // caller's own work between BeginBatch/CommitBatch throws -- see BatchScope.Dispose.
        public void AbortBatch()
        {
            _pendingOps = null;
            if (_sharedBatchOwned) { _sharedBatchOwned = false; _shared.AbortBatch(); }
        }

        // Exception-safe scope: using (var batch = ini.BeginBatchScope()) { ...writes...;
        // batch.Commit(); }. If the block exits (return or exception) before Commit() runs,
        // Dispose() aborts instead of silently persisting a half-finished save.
        public BatchScope BeginBatchScope()
        {
            BeginBatch();
            return new BatchScope(this);
        }

        public sealed class BatchScope : IDisposable
        {
            private IniFile _owner;
            private bool _committed;

            internal BatchScope(IniFile owner) { _owner = owner; }

            public void Commit()
            {
                if (_owner == null) return;
                _owner.CommitBatch();
                _committed = true;
            }

            public void Dispose()
            {
                if (_owner == null) return;
                var owner = _owner;
                _owner = null;
                if (!_committed) owner.AbortBatch();
            }
        }

        public string Read(string Key, string Section = null)
        {
            var target = Route(Key, Section);
            return target == this ? ReadOwn(Key, Section) : target.Read(Key);
        }

        // This file only, never the shared one.
        internal string ReadOwn(string Key, string Section = null)
        {
            string section = Section ?? EXE;
            if (_pendingOps != null && TryGetPendingValue(section, Key, out string pending))
                return pending ?? "";
            // 2048 chars (not the old 255) so a DPAPI-encrypted, base64-encoded credential
            // never gets silently truncated -- a truncated blob fails to decrypt.
            var RetVal = new StringBuilder(2048);
            GetPrivateProfileString(section, Key, "", RetVal, 2048, Path);
            return RetVal.ToString();
        }

        // Read-your-own-writes for a still-open batch: replays the queue in order looking for the
        // last operation touching this exact (section, key), including a whole-section delete
        // wiping out an earlier key write for that same section. Returns false ("nothing queued
        // for this key") so the caller falls back to the on-disk value.
        private bool TryGetPendingValue(string section, string key, out string value)
        {
            value = null;
            bool found = false;
            for (int i = 0; i < _pendingOps.Count; i++)
            {
                var op = _pendingOps[i];
                if (!string.Equals(op.Section, section, StringComparison.OrdinalIgnoreCase)) continue;
                if (op.Key == null)
                {
                    found = true;
                    value = null; // whole section deleted -- this key is gone too
                    continue;
                }
                if (!string.Equals(op.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                found = true;
                value = op.Value; // null => this key deleted
            }
            return found;
        }

        public void Write(string Key, string Value, string Section = null)
        {
            var target = Route(Key, Section);
            if (target == this) WriteOwn(Key, Value, Section);
            else target.Write(Key, Value);
        }

        // This file only, never the shared one.
        internal void WriteOwn(string Key, string Value, string Section = null)
        {
            string section = Section ?? EXE;
            if (_pendingOps != null)
            {
                _pendingOps.Add(new PendingOp { Section = section, Key = Key, Value = Value });
                return;
            }
            WritePrivateProfileString(section, Key, Value, Path);
        }

        public void DeleteKey(string Key, string Section = null)
        {
            Write(Key, null, Section ?? EXE);
        }

        public void DeleteSection(string Section = null)
        {
            Write(null, null, Section ?? EXE);
        }

        public bool KeyExists(string Key, string Section = null)
        {
            return Read(Key, Section).Length > 0;
        }

        // ===== Batch commit: parse -> replay -> atomic replace =====

        private sealed class IniDocument
        {
            // Every line of the file, verbatim, in original order (no trailing newline stored --
            // that's tracked once, separately, as Newline). Anything a batch doesn't specifically
            // touch -- comments, blank lines, unrecognized sections/keys -- stays exactly here,
            // untouched, at its original position.
            public List<string> Lines = new List<string>();
            public Dictionary<string, Section> Sections = new Dictionary<string, Section>(StringComparer.OrdinalIgnoreCase);

            public sealed class Section
            {
                public int HeaderIndex;        // index in Lines of "[Name]"
                public int EndIndexExclusive;   // index just past this section's last line
                public Dictionary<string, int> KeyLineIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void ApplyOpsAtomically(List<PendingOp> ops)
        {
            bool fileExisted = File.Exists(Path);
            string originalText = "";
            Encoding encoding;
            string newline = "\r\n"; // matches WritePrivateProfileString's own line endings

            if (fileExisted)
            {
                byte[] bytes = File.ReadAllBytes(Path);
                encoding = DetectEncoding(bytes, out int preambleLength);
                originalText = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
                newline = DetectNewline(originalText);
                // Never write a UTF-8 byte-order mark back: Windows does not see the first
                // section heading behind one (see RepairDuplicateSections).
                if (encoding is UTF8Encoding) encoding = new UTF8Encoding(false);
            }
            else
            {
                // No BOM to preserve and nothing to round-trip -- UTF-8 without a BOM is
                // byte-identical to classic ANSI for the plain-ASCII content every current Jimmy
                // setting writes, and is the simplest safe default for a brand-new file.
                encoding = new UTF8Encoding(false);
            }

            var doc = ParseDocument(originalText);
            foreach (var op in ops)
                ApplyOp(doc, op.Section, op.Key, op.Value);

            string mergedText = string.Join(newline, doc.Lines);
            if (doc.Lines.Count > 0) mergedText += newline;

            string dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            // Temp file lives in the SAME directory as the real ini so the final swap below is a
            // same-volume rename -- genuinely atomic, not a cross-volume copy.
            string tempPath = System.IO.Path.Combine(dir ?? "", System.IO.Path.GetFileName(Path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(tempPath, mergedText, encoding);
                if (fileExisted)
                {
                    // File.Replace is the atomic swap: on failure the original is left untouched
                    // (never deleted first) and the exception propagates so the caller learns the
                    // save didn't happen -- no silent partial save.
                    File.Replace(tempPath, Path, null);
                }
                else
                {
                    // File.Replace requires an existing destination; for a brand-new file, a
                    // same-directory Move is itself an atomic rename.
                    File.Move(tempPath, Path);
                }
                tempPath = null; // consumed by Replace/Move -- nothing left to clean up
            }
            finally
            {
                if (tempPath != null)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort cleanup only */ }
                }
            }
        }

        // ===== Duplicate sections, 2026-10-02 =====
        // A tester's files held "[Jimmy Next]" twice. Windows reads only the first copy, so every
        // setting saved into the second one came back at the next start as it was before (Smart
        // Mode always off, Options changes lost). At start each settings file is checked once and
        // the copies merged into the first, a later copy's value winning -- that is where the
        // saves went. The file as it was goes to Backups first.

        // The merged text, or null when no section appears twice.
        internal static string MergeDuplicateSections(string text)
        {
            var lines = SplitLines(text);
            var firstEnd = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // section -> index past its last line
            var firstKeys = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            var output = new List<string>();
            bool merged = false;
            string section = null;
            bool inCopy = false;
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[trimmed.Length - 1] == ']')
                {
                    section = trimmed.Substring(1, trimmed.Length - 2);
                    inCopy = firstKeys.ContainsKey(section);
                    if (inCopy) { merged = true; continue; }
                    firstKeys[section] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    output.Add(line);
                    firstEnd[section] = output.Count;
                    continue;
                }
                int eq = line.IndexOf('=');
                string key = section != null && eq > 0 && trimmed.Length > 0 && trimmed[0] != ';' ? line.Substring(0, eq).Trim() : "";
                if (!inCopy)
                {
                    output.Add(line);
                    if (section == null) continue;
                    if (key.Length > 0) firstKeys[section][key] = output.Count - 1;
                    if (trimmed.Length > 0) firstEnd[section] = output.Count;
                    continue;
                }
                if (key.Length == 0) continue; // a copy's blank lines and comments go
                if (firstKeys[section].TryGetValue(key, out int at))
                {
                    output[at] = output[at].Substring(0, output[at].IndexOf('=') + 1) + line.Substring(eq + 1);
                    continue;
                }
                int insertAt = firstEnd[section];
                output.Insert(insertAt, line);
                foreach (var keys in firstKeys.Values)
                    foreach (var k in new List<string>(keys.Keys))
                        if (keys[k] >= insertAt) keys[k]++;
                foreach (var s in new List<string>(firstEnd.Keys))
                    if (firstEnd[s] >= insertAt && s != section) firstEnd[s]++;
                firstKeys[section][key] = insertAt;
                firstEnd[section] = insertAt + 1;
            }
            if (!merged) return null;
            // A copy at the end of the file leaves the blank line before it behind.
            while (output.Count > 0 && output[output.Count - 1].Trim().Length == 0) output.RemoveAt(output.Count - 1);
            return string.Join("\n", output);
        }

        // Merges duplicate sections in the settings files under dataRoot (the files there and in
        // Profiles). Returns a line for the log, or null when every file was fine.
        internal static string RepairDuplicateSections(string dataRoot)
        {
            var repaired = new List<string>();
            string backup = System.IO.Path.Combine(dataRoot, "Backups", "before-ini-repair-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            foreach (string dir in new[] { dataRoot, System.IO.Path.Combine(dataRoot, "Profiles") })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.GetFiles(dir, "*.ini"))
                {
                    byte[] bytes = File.ReadAllBytes(file);
                    var encoding = DetectEncoding(bytes, out int preambleLength);
                    string text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
                    string fixedText = MergeDuplicateSections(text);
                    // A UTF-8 byte-order mark goes too (operator, 2026-10-02): Windows reads the
                    // file byte by byte and does not see the first "[Section]" behind the mark, so
                    // that section is never read, and the first save into it makes Windows add a
                    // second copy at the end -- how "notification qso test" got [Hotkeys] twice.
                    bool utf8Mark = preambleLength == 3;
                    if (fixedText == null && !utf8Mark) continue;
                    if (fixedText == null) fixedText = string.Join("\n", SplitLines(text));
                    if (utf8Mark) encoding = new UTF8Encoding(false);
                    string rel = file.Substring(dataRoot.Length).TrimStart('\\', '/');
                    string saved = System.IO.Path.Combine(backup, rel);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(saved));
                    File.Copy(file, saved, true);
                    string newline = DetectNewline(text);
                    string tempPath = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(tempPath, fixedText.Replace("\n", newline) + newline, encoding);
                    File.Replace(tempPath, file, null);
                    repaired.Add(rel);
                }
            }
            if (repaired.Count == 0) return null;
            BackupRetention.Prune(System.IO.Path.Combine(dataRoot, "Backups"), "before-ini-repair-*");
            return $"settings: repaired {string.Join(", ", repaired)} (a section written twice, or a mark at the start that hid the first section); backup {backup}";
        }

        private static Encoding DetectEncoding(byte[] bytes, out int preambleLength)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                preambleLength = 3;
                return new UTF8Encoding(true);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                preambleLength = 2;
                return new UnicodeEncoding(false, true); // UTF-16 LE with BOM
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                preambleLength = 2;
                return new UnicodeEncoding(true, true); // UTF-16 BE with BOM
            }
            // No BOM: treat as UTF-8 without one. Byte-identical to classic ANSI for plain ASCII
            // content, which is what every current Jimmy setting is -- see the "no file yet"
            // branch above for the same reasoning.
            preambleLength = 0;
            return new UTF8Encoding(false);
        }

        private static string DetectNewline(string text)
        {
            int idx = text.IndexOf('\n');
            if (idx > 0 && text[idx - 1] == '\r') return "\r\n";
            if (idx >= 0) return "\n";
            return "\r\n";
        }

        private static List<string> SplitLines(string text)
        {
            var lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
            // A file ending in a newline splits into a trailing "" -- drop it so re-joining with
            // the detected newline below doesn't grow a blank line at EOF on every commit.
            if (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
                lines.RemoveAt(lines.Count - 1);
            return lines;
        }

        private static IniDocument ParseDocument(string text)
        {
            var doc = new IniDocument();
            doc.Lines.AddRange(SplitLines(text));

            IniDocument.Section current = null;
            for (int i = 0; i < doc.Lines.Count; i++)
            {
                string line = doc.Lines[i];
                string trimmed = line.Trim();
                if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[trimmed.Length - 1] == ']')
                {
                    string name = trimmed.Substring(1, trimmed.Length - 2);
                    if (current != null) current.EndIndexExclusive = i;
                    current = new IniDocument.Section { HeaderIndex = i, EndIndexExclusive = i + 1 };
                    // A second copy of a section is never written to: Windows reads only the first
                    // copy, so a write landing in a later one would be lost (KF4CCG, 2026-10-02).
                    if (!doc.Sections.ContainsKey(name)) doc.Sections[name] = current;
                    continue;
                }
                if (current == null) continue; // content before any section header -- left alone, never a Write target
                int eq = line.IndexOf('=');
                if (eq > 0 && trimmed.Length > 0 && trimmed[0] != ';')
                {
                    string key = line.Substring(0, eq).Trim();
                    if (key.Length > 0) current.KeyLineIndex[key] = i;
                }
            }
            if (current != null) current.EndIndexExclusive = doc.Lines.Count;
            return doc;
        }

        // Every existing index at or past `threshold` moves by `delta`. Used after every Lines
        // insert/remove so every other section/key's recorded line position stays correct for the
        // rest of this same replay.
        private static void ShiftIndicesFrom(IniDocument doc, int threshold, int delta)
        {
            foreach (var sec in doc.Sections.Values)
            {
                if (sec.HeaderIndex >= threshold) sec.HeaderIndex += delta;
                if (sec.EndIndexExclusive >= threshold) sec.EndIndexExclusive += delta;
                if (sec.KeyLineIndex.Count == 0) continue;
                var keys = new List<string>(sec.KeyLineIndex.Keys);
                foreach (var k in keys)
                {
                    int v = sec.KeyLineIndex[k];
                    if (v >= threshold) sec.KeyLineIndex[k] = v + delta;
                }
            }
        }

        // Replays exactly one queued Write() against the in-memory document, with the same
        // per-call semantics WritePrivateProfileString itself has: Key == null deletes the whole
        // section (Value is ignored, matching the real API); Key set + Value == null deletes just
        // that key; otherwise the key is updated in place or appended/created.
        private static void ApplyOp(IniDocument doc, string section, string key, string value)
        {
            if (key == null)
            {
                if (doc.Sections.TryGetValue(section, out var sec))
                {
                    int start = sec.HeaderIndex, count = sec.EndIndexExclusive - sec.HeaderIndex;
                    doc.Lines.RemoveRange(start, count);
                    doc.Sections.Remove(section);
                    ShiftIndicesFrom(doc, start + count, -count);
                }
                return;
            }

            if (!doc.Sections.TryGetValue(section, out var s))
            {
                if (value == null) return; // deleting a key from a section that isn't there: no-op
                if (doc.Lines.Count > 0 && doc.Lines[doc.Lines.Count - 1].Length != 0)
                    doc.Lines.Add("");
                int headerIdx = doc.Lines.Count;
                doc.Lines.Add("[" + section + "]");
                doc.Lines.Add(key + "=" + value);
                var newSec = new IniDocument.Section { HeaderIndex = headerIdx, EndIndexExclusive = doc.Lines.Count };
                newSec.KeyLineIndex[key] = headerIdx + 1;
                doc.Sections[section] = newSec;
                return;
            }

            if (s.KeyLineIndex.TryGetValue(key, out int lineIdx))
            {
                if (value == null)
                {
                    doc.Lines.RemoveAt(lineIdx);
                    s.KeyLineIndex.Remove(key);
                    ShiftIndicesFrom(doc, lineIdx + 1, -1);
                }
                else
                {
                    // Keep the on-disk key spelling; only the value changes (matches
                    // WritePrivateProfileString's own case-insensitive-match-in-place behavior).
                    string existingLine = doc.Lines[lineIdx];
                    int eq = existingLine.IndexOf('=');
                    string existingKeySpelling = existingLine.Substring(0, eq);
                    doc.Lines[lineIdx] = existingKeySpelling + "=" + value;
                }
            }
            else
            {
                if (value == null) return; // deleting a key that isn't there: no-op
                int insertAt = s.EndIndexExclusive;
                ShiftIndicesFrom(doc, insertAt, +1);
                doc.Lines.Insert(insertAt, key + "=" + value);
                s.KeyLineIndex[key] = insertAt;
            }
        }
    }
}
