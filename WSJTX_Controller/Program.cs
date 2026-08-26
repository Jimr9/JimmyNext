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
