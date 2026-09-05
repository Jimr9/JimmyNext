using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Modeless, keyboard-accessible view of the session NotificationHistory. Newest entry first.
    // The whole history is one standard read-only multiline TextBox -- no owner drawing, no
    // custom AccessibleObject, no self-voicing -- so JAWS/NVDA treat it as ordinary editable
    // text: arrow-key navigation, Ctrl+Home/End, Shift+arrow selection, Ctrl+A, Ctrl+C all work
    // natively. Escape closes; on close, focus returns to wherever it was before the window
    // opened (Controller wires that via FormClosed).
    //
    // New entries only ever arrive at the FRONT of the list, and old ones fall off the back once
    // the 300-entry cap is reached. When that happens while the operator is reading, the text is
    // rebuilt but their place is kept: the caret / selection is shifted right by exactly the
    // number of characters prepended, and the view is scrolled back down by the matching number
    // of display lines. Nothing here calls Focus(), so a reader working in another control is
    // never pulled over.
    internal sealed class NotificationHistoryWindow : Form
    {
        private const string EntrySeparator = "\r\n\r\n";

        private const int EM_LINESCROLL = 0x00B6;
        private const int EM_LINEINDEX = 0x00BB;
        private const int EM_LINEFROMCHAR = 0x00C9;
        private const int EM_GETFIRSTVISIBLELINE = 0x00CE;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private readonly NotificationHistory _history;
        private readonly Action<bool> _setIncludeRoutine;

        private readonly TextBox _text;
        private readonly CheckBox _includeRoutine;
        private bool _refreshQueued;

        public NotificationHistoryWindow(NotificationHistory history,
            Func<bool> getIncludeRoutine, Action<bool> setIncludeRoutine)
        {
            _history = history;
            _setIncludeRoutine = setIncludeRoutine;

            Text = "Notification History";
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            ClientSize = new Size(560, 420);
            MinimumSize = new Size(360, 240);
            KeyPreview = true;

            _includeRoutine = new CheckBox
            {
                Text = "&Include routine status messages",
                AccessibleName = "Include routine status messages",
                Location = new Point(12, 10),
                AutoSize = true,
                Checked = getIncludeRoutine != null && getIncludeRoutine(),
            };
            _includeRoutine.CheckedChanged += (s, e) => _setIncludeRoutine?.Invoke(_includeRoutine.Checked);

            _text = new TextBox
            {
                Location = new Point(12, 38),
                Size = new Size(ClientSize.Width - 24, ClientSize.Height - 50),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Multiline = true,
                ReadOnly = true,
                WordWrap = true,
                ScrollBars = ScrollBars.Vertical,
                MaxLength = 4_000_000,
                AccessibleName = "Notification history, newest first",
                TabStop = true,
                Font = new Font(FontFamily.GenericSansSerif, 10f),
            };

            Controls.Add(_includeRoutine);
            Controls.Add(_text);

            Load += (s, e) =>
            {
                _text.Text = BuildAll(_history.Snapshot());
                _text.Select(0, 0);
                _history.Changed += OnHistoryChanged;
                _text.Focus();
            };
            FormClosed += (s, e) => _history.Changed -= OnHistoryChanged;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                Close();
                return;
            }
            base.OnKeyDown(e);
        }

        // internal for a focused format test -- joins entry blocks newest-first with exactly one
        // blank line between them.
        internal static string BuildAll(IReadOnlyList<NotificationHistoryEntry> snapshot)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (i > 0) sb.Append(EntrySeparator);
                sb.Append(snapshot[i].Block);
            }
            return sb.ToString();
        }

        private void OnHistoryChanged()
        {
            // Marshal to the UI thread; coalesce bursts into one refresh.
            if (_refreshQueued || !IsHandleCreated || IsDisposed) return;
            _refreshQueued = true;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    _refreshQueued = false;
                    ApplyUpdate();
                }));
            }
            catch (InvalidOperationException) { _refreshQueued = false; }
        }

        private void ApplyUpdate()
        {
            if (IsDisposed || !IsHandleCreated) return;

            string oldText = _text.Text;
            string newText = BuildAll(_history.Snapshot());
            if (newText == oldText) return;

            // Not being read right now: just show the rebuilt history from the top (newest).
            if (!_text.Focused)
            {
                _text.Text = newText;
                _text.Select(0, 0);
                return;
            }

            int selStart = _text.SelectionStart;
            int selLen = _text.SelectionLength;

            int firstVisLine = (int)SendMessage(_text.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
            int firstVisChar = (int)SendMessage(_text.Handle, EM_LINEINDEX, (IntPtr)firstVisLine, IntPtr.Zero);
            if (firstVisChar < 0) firstVisChar = 0;

            // Characters prepended ahead of where the old text resumes. The old text's tail may
            // also have been trimmed by 300-entry eviction; that only matters if it swallowed
            // the caret, which the clamps below handle safely.
            int prependChars = PrependLength(oldText, newText);

            _text.Text = newText;

            int newStart = selStart + prependChars;
            if (newStart > newText.Length) newStart = newText.Length;
            if (newStart < 0) newStart = 0;
            int maxLen = newText.Length - newStart;
            if (selLen > maxLen) selLen = maxLen;
            if (selLen < 0) selLen = 0;
            _text.Select(newStart, selLen);

            // Restore the scroll position: translate the old top-visible character offset into
            // its new display line (wrap-accurate) and scroll there from the reset top.
            int targetChar = firstVisChar + prependChars;
            if (targetChar > newText.Length) targetChar = newText.Length;
            int targetLine = (int)SendMessage(_text.Handle, EM_LINEFROMCHAR, (IntPtr)targetChar, IntPtr.Zero);
            int curFirst = (int)SendMessage(_text.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
            int delta = targetLine - curFirst;
            if (delta != 0)
                SendMessage(_text.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)delta);
        }

        // Length of the run of characters at the start of newText that precedes the point where
        // oldText resumes verbatim. New entries are always prepended, so newText is
        // "<new blocks><separator><oldText>" -- with, at steady state, oldText's own tail
        // trimmed by eviction. Anchor on oldText's first entry block to stay correct in that
        // case; fall back to the length delta (caller clamps) only if nothing lines up.
        // internal for a focused test of the caret-shift math.
        internal static int PrependLength(string oldText, string newText)
        {
            if (string.IsNullOrEmpty(oldText)) return newText.Length;

            int exact = newText.IndexOf(oldText, StringComparison.Ordinal);
            if (exact >= 0) return exact;

            int firstBreak = oldText.IndexOf(EntrySeparator, StringComparison.Ordinal);
            string anchor = firstBreak > 0 ? oldText.Substring(0, firstBreak) : oldText;
            int a = newText.IndexOf(anchor, StringComparison.Ordinal);
            if (a >= 0) return a;

            return Math.Max(0, newText.Length - oldText.Length);
        }
    }
}
