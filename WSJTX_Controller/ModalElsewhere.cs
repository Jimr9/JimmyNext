using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // A dialog opened from another Jimmy window -- Logbook Center (Rule Definitions, Edit contact,
    // Review Downloaded Changes, a message box...), Alt+K Help -- is modal to THAT window, never to
    // Jimmy's main window (operator, 2026-10-17: with Rule Definitions open the main screen could
    // not be used). Windows Forms disables every window of the program while any dialog is shown
    // modally; the main window re-enables itself, and the other independent windows, as soon as it
    // sees the dialog belongs to another window. The window the dialog was opened from stays
    // disabled, as a modal dialog's owner should. A dialog the main window opens itself is left
    // exactly as before. Nothing takes the keyboard focus: a window is only made usable again.
    //
    // While such a dialog is open, the main window must not close the window it belongs to or
    // open another dialog on it: the code behind the open dialog would carry on against a closed
    // window. Closing the main window (Alt+F4) first closes those dialogs as Cancel would
    // (CloseDialogsThen); a profile switch, Open Logbook and Add QSO bring the open dialog to the
    // front instead (BringOpenDialogForward).
    internal static class ModalElsewhere
    {
        private const int WM_ENABLE = 0x000A;
        private const uint GW_OWNER = 4;

        [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr hWnd, bool enable);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumProc proc, IntPtr lParam);

        // Called from the main window's WndProc: true when the message disabled it.
        internal static bool IsDisable(ref Message m) => m.Msg == WM_ENABLE && m.WParam == IntPtr.Zero;

        // Whether `ancestor` is in h's owner chain. (Not just the chain's top: a window kept off the
        // taskbar is owned by a hidden window of Windows Forms' own, above the form that opened it.)
        internal static bool OwnedBy(IntPtr h, IntPtr ancestor)
        {
            for (int guard = 0; guard < 32 && h != IntPtr.Zero; guard++)
            {
                h = GetWindow(h, GW_OWNER);
                if (h == ancestor) return true;
            }
            return false;
        }

        // The dialog now open on this thread: a visible, enabled, OWNED window (a modal dialog
        // always has an owner, and is the one window left enabled). Zero when there is none.
        private static IntPtr OpenDialog()
        {
            IntPtr found = IntPtr.Zero;
            EnumThreadWindows(GetCurrentThreadId(), (h, _) =>
            {
                if (IsWindowVisible(h) && IsWindowEnabled(h) && GetWindow(h, GW_OWNER) != IntPtr.Zero) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        // The open dialog when it belongs to a window other than `main`; zero otherwise. Such a
        // window is the one independent window still disabled while the main window is usable;
        // its dialog is the visible, enabled window it owns (or the window itself, to bring it
        // forward, if the dialog cannot be told).
        // only: look at that window alone (null: every independent window).
        internal static IntPtr OpenDialogOutsideMain(Form main, Form only = null)
        {
            if (main == null || !main.IsHandleCreated || !IsWindowEnabled(main.Handle)) return IntPtr.Zero;
            foreach (Form f in Application.OpenForms)
            {
                if (only != null && f != only) continue;
                if (f == main || f.IsDisposed || !f.IsHandleCreated || f.Owner != null || !f.TopLevel || !f.Visible) continue;
                if (IsWindowEnabled(f.Handle)) continue;
                IntPtr from = f.Handle, dlg = IntPtr.Zero;
                EnumThreadWindows(GetCurrentThreadId(), (h, _) =>
                {
                    if (IsWindowVisible(h) && IsWindowEnabled(h) && OwnedBy(h, from)) { dlg = h; return false; }
                    return true;
                }, IntPtr.Zero);
                return dlg != IntPtr.Zero ? dlg : from;
            }
            return IntPtr.Zero;
        }

        // After the main window was disabled: if the dialog behind it belongs to another window,
        // make the main window and every other independent window usable again. Tries again on the
        // next message when the dialog is not shown yet (it is created right after the disable).
        internal static void KeepUsable(Form main, int tries = 3)
        {
            if (main == null || main.IsDisposed || !main.IsHandleCreated || IsWindowEnabled(main.Handle)) return;
            IntPtr dlg = OpenDialog();
            if (dlg == IntPtr.Zero)
            {
                if (tries > 1) main.BeginInvoke(new Action(() => KeepUsable(main, tries - 1)));
                return;
            }
            if (OwnedBy(dlg, main.Handle)) return;   // the main window's own dialog: modal to it, as before
            var windows = new List<IntPtr>();
            foreach (Form f in Application.OpenForms)
                if (!f.IsDisposed && f.IsHandleCreated && f.Owner == null && f.TopLevel && f.Handle != dlg && !OwnedBy(dlg, f.Handle)) windows.Add(f.Handle);
            foreach (var h in windows) EnableWindow(h, true);
        }

        // Brings the open dialog of another window to the front (the operator asked the main window
        // for something that must wait until it is closed). Returns true when there was one.
        internal static bool BringOpenDialogForward(Form main, Form only = null)
        {
            if (only != null && only.IsDisposed) return false;
            IntPtr dlg = OpenDialogOutsideMain(main, only);
            if (dlg == IntPtr.Zero) return false;
            SetForegroundWindow(dlg);
            return true;
        }

        private const int WM_CLOSE = 0x0010;
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // Closing the main window (Alt+F4) while dialogs of other windows are open (operator,
        // 2026-10-17: Alt+F4 in the main window closes all of Jimmy Next): each dialog open at that
        // moment is closed as Cancel would close it -- nothing in it applied -- innermost first,
        // and then `then` runs (the main window's close, again). A dialog that answers the close
        // with a question of its own ("save changes?") is not answered for the operator: the
        // closing stops there and that question is brought to the front. Returns false when no
        // such dialog was open (the caller closes as usual).
        internal static bool CloseDialogsThen(Form main, Action then)
        {
            if (OpenDialogOutsideMain(main) == IntPtr.Zero) return false;
            var atStart = new HashSet<IntPtr>();
            foreach (Form f in Application.OpenForms)
            {
                if (f == main || f.IsDisposed || !f.IsHandleCreated || f.Owner != null || !f.TopLevel) continue;
                IntPtr from = f.Handle;
                EnumThreadWindows(GetCurrentThreadId(), (h, _) =>
                {
                    if (IsWindowVisible(h) && OwnedBy(h, from)) atStart.Add(h);
                    return true;
                }, IntPtr.Zero);
            }
            var timer = new Timer { Interval = 100 };
            int ticks = 0;
            IntPtr lastClosed = IntPtr.Zero;
            void Step()
            {
                IntPtr dlg = OpenDialogOutsideMain(main);
                if (dlg == IntPtr.Zero) { timer.Stop(); timer.Dispose(); then(); return; }
                bool stuck = ++ticks > 50 || (dlg == lastClosed && ticks % 10 == 0);
                if (!atStart.Contains(dlg) || stuck)
                {
                    // A question the close raised, or a dialog that would not close: the operator decides.
                    timer.Stop(); timer.Dispose();
                    SetForegroundWindow(dlg);
                    return;
                }
                if (dlg != lastClosed)
                {
                    lastClosed = dlg;
                    if (Control.FromHandle(dlg) is Form form) form.Close();   // DialogResult Cancel
                    else PostMessage(dlg, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);  // a message box, a file dialog
                }
            }
            timer.Tick += (s, e) => Step();
            timer.Start();
            Step();
            return true;
        }
    }
}
