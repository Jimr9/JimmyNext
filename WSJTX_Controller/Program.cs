using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    static class Program
    {
        // Independent audit finding 12, 2026-08-23 (HARDENING GAP): the pre-existing
        // Process.GetProcessesByName count check below is TOCTOU-racy -- two near-simultaneous
        // launches (e.g. a double-click registering as two clicks) can both read the process
        // list before either process is fully registered, so both can pass the check and end up
        // as two independent Jimmy instances each launching/owning their own EngineHost session
        // and independently commanding the same physical radio. A named Mutex is atomic (the OS
        // itself arbitrates "did I create this or does it already exist", not a read-then-act
        // race). Scoped by the running assembly's own name (Assembly.GetName().Name -- "Jimmy"
        // for a production build, "Jimmy Next" for this Test build, see build.bat's own comment
        // on why the two must never share state) so a Jimmy Next instance never blocks a
        // production Jimmy instance or vice versa -- matches the existing process-name check's
        // own identity scoping, just made atomic. Kept as a static field for the process's whole
        // lifetime: an unheld local would be eligible for GC (and therefore finalization/release)
        // while still needed.
        private static System.Threading.Mutex _singleInstanceMutex;

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // Crash visibility, 2026-08-26: previously nothing here caught an unhandled
            // exception at all, so a bug like the F4 Process.Start crash showed .NET's raw
            // "JIT Debugging" dialog and left zero trace in any log -- a Support Report
            // generated afterward had no record the crash even happened unless the operator
            // thought to copy-paste the dialog text themselves. These three handlers cover
            // the three places an exception can go unhandled: a WinForms event handler (UI
            // thread), a background Task.Run that never gets awaited/observed, and anything
            // else fatal. All three funnel into CrashLogger, which writes to its own
            // log_crashes.txt regardless of the Diagnostic Log setting, named to match the
            // "log_*.txt" pattern Create Support Report already scans -- so a crash now shows
            // up in the next report automatically, no operator action required. Registered
            // before anything else in Main() so even a startup-time exception is covered.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                CrashLogger.Log("UI thread", e.Exception);
                // Audit finding 2, 2026-08-27: Nexus/EngineHost transmits independently of
                // Jimmy's UI, so an active CQ or QSO would keep going on the air while this
                // dialog sits open and Jimmy's own state is unreliable. Best-effort halt first
                // (raw socket, bounded -- never touches the possibly-corrupt WsjtxClient).
                bool halted = NativeEngineClient.TryEmergencyHaltTx();
                MessageBox.Show(
                    $"Jimmy Next hit an unexpected problem and logged the details (Help > Create Support Report to send them).{Environment.NewLine}{Environment.NewLine}" +
                    (halted
                        ? $"Transmit has been halted as a precaution. It is safest to restart Jimmy Next now.{Environment.NewLine}{Environment.NewLine}"
                        : $"If the radio may still be transmitting, press Escape, and it is safest to restart Jimmy Next now.{Environment.NewLine}{Environment.NewLine}") +
                    e.Exception.Message,
                    "Jimmy Next - Unexpected Problem",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                NativeEngineClient.TryEmergencyHaltTx();
                CrashLogger.Log("background thread (fatal)", e.ExceptionObject as Exception);
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                CrashLogger.Log("unobserved task", e.Exception);
                e.SetObserved();
            };

            if (System.Diagnostics.Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(System.Reflection.Assembly.GetEntryAssembly().Location)).Count() > 1)
            {
                MessageBox.Show("An instance of this application is already running.");
                return;
            }
            if (System.Diagnostics.Process.GetProcessesByName("WSJTX_Controller").Count() > 0)
            {
                MessageBox.Show("Jimmy and Otto can't run at the same time.\n\nClose Otto before running Jimmy.");
                return;
            }

            string mutexName = "Local\\" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Name + "_SingleInstance";
            _singleInstanceMutex = new System.Threading.Mutex(initiallyOwned: true, name: mutexName, createdNew: out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show("An instance of this application is already running.");
                return;
            }
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Controller());
            }
            finally
            {
                _singleInstanceMutex.ReleaseMutex();
                _singleInstanceMutex.Dispose();
            }
        }
    }
}
