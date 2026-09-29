using System.Threading.Tasks;

namespace WSJTX_Controller
{
    internal static class TaskExtensions
    {
        // For a task the caller may stop waiting on: a bounded connect (ConnectAsync + Wait(timeout))
        // that times out, after which the TcpClient is disposed. The abandoned connect then fails
        // ("the I/O operation has been aborted") with nothing observing it, and .NET reports it as an
        // unobserved task exception -- 54,000+ such entries filled log_crashes.txt (Aug-Sep 2026).
        // Observing the fault here closes that; the caller already treats the timeout as failure.
        public static T ObserveFault<T>(this T task) where T : Task
        {
            task.ContinueWith(t => { var _ = t.Exception; },
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            return task;
        }
    }
}
