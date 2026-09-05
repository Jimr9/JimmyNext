using System;
using System.Collections.Generic;

namespace WSJTX_Controller
{
    // One recorded operator-facing message, rendered as its own stack of physical lines in the
    // Notification History window (newest entry first):
    //
    //   <time>
    //   <hotkey>      -- only when the message was produced inside a configured hotkey handler
    //   <message>
    //
    // The window separates consecutive entries with one blank line. Plain Windows newlines, so
    // the whole thing lives in one standard read-only multiline edit control that JAWS/NVDA
    // navigate as ordinary text.
    public sealed class NotificationHistoryEntry
    {
        public DateTime TimeLocal { get; }
        public string Origin { get; }        // e.g. "Alt+Z"; null when not hotkey-triggered
        public string Text { get; }
        public bool IsRoutineStatus { get; } // a routine ShowStatus render, not a NotificationCenter/direct message

        public NotificationHistoryEntry(DateTime timeLocal, string origin, string text, bool isRoutineStatus)
        {
            TimeLocal = timeLocal;
            Origin = string.IsNullOrWhiteSpace(origin) ? null : origin.Trim();
            Text = text ?? "";
            IsRoutineStatus = isRoutineStatus;
        }

        public string TimeText => TimeLocal.ToString("h:mm:ss tt");

        // The entry as its own block of physical lines (no trailing blank -- the window inserts
        // the blank line between blocks). Hotkey-triggered entries carry the configured hotkey
        // on its own line between the time and the message.
        public string Block => Origin == null
            ? TimeText + "\r\n" + Text
            : TimeText + "\r\n" + Origin + "\r\n" + Text;

        public override string ToString() => Block;
    }

    // Session-only, bounded, in-memory record of what Jimmy actually said to the operator this
    // session -- NOT persisted to disk. Fed from the one final delivery seam (Controller.ShowMsg,
    // which every delivered NotificationCenter notification and every direct ShowMessage/
    // ShowUploadStatus call already funnels through) plus, optionally, the routine status render
    // path. A policy-suppressed NotificationCenter event never reaches ShowMsg, so it is
    // correctly absent here -- this is a record of what was presented, not what was published.
    //
    // Thread-affinity: every caller today is on the UI thread (ShowMsg, RenderStatus,
    // NotificationCenter.Deliver all run there), but the collection is locked anyway so a future
    // off-thread caller can't corrupt it. Changed is raised inside the lock's release; the
    // window subscriber marshals to the UI thread itself.
    public sealed class NotificationHistory
    {
        public const int DefaultCapacity = 300;

        private readonly int _capacity;
        private readonly LinkedList<NotificationHistoryEntry> _entries = new LinkedList<NotificationHistoryEntry>();
        private readonly object _lock = new object();
        private string _lastRoutineText;

        // Raised after any entry is added. No args -- subscribers pull a fresh Snapshot().
        public event Action Changed;

        public NotificationHistory(int capacity = DefaultCapacity)
        {
            _capacity = capacity > 0 ? capacity : DefaultCapacity;
        }

        // A NotificationCenter notification or a direct operator message that was actually
        // delivered. origin is the triggering hotkey label when the message was produced
        // synchronously inside a hotkey handler, else null.
        public void Record(string text, string origin = null)
        {
            AddEntry(new NotificationHistoryEntry(DateTime.Now, origin, text, isRoutineStatus: false));
        }

        // A routine status render. Only recorded when the caller opts in (the "Include routine
        // status messages" setting) AND the meaningful text actually changed since the last one
        // -- the status render path fires repeatedly with identical text every poll tick, and
        // the history must not fill with hundreds of duplicate lines.
        public void RecordRoutineStatus(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_lock)
            {
                if (text == _lastRoutineText) return;
                _lastRoutineText = text;
            }
            AddEntry(new NotificationHistoryEntry(DateTime.Now, origin: null, text: text, isRoutineStatus: true));
        }

        private void AddEntry(NotificationHistoryEntry entry)
        {
            lock (_lock)
            {
                _entries.AddLast(entry);
                while (_entries.Count > _capacity) _entries.RemoveFirst();
            }
            Changed?.Invoke();
        }

        // Newest first.
        public IReadOnlyList<NotificationHistoryEntry> Snapshot()
        {
            lock (_lock)
            {
                var list = new List<NotificationHistoryEntry>(_entries.Count);
                for (var node = _entries.Last; node != null; node = node.Previous) list.Add(node.Value);
                return list;
            }
        }

        public int Count { get { lock (_lock) return _entries.Count; } }
    }
}
