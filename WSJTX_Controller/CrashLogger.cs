using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace WSJTX_Controller
{
    // Independent of WsjtxClient/diagLog -- must work even before Controller/WsjtxClient
    // exist (a startup crash) and regardless of whether the operator has Diagnostic Log
    // enabled; a crash matters regardless of that toggle. Appends to one persistent file
    // (not date-bucketed like the regular log_M-D-YYYY.txt files) so a crash's full record
    // survives across restarts, and names it to match the "log_*.txt" glob
    // SupportReportBuilder.CollectLogFiles() already scans -- zero changes needed there
    // for a crash to show up in the next Support Report.
    internal static class CrashLogger
    {
        private static readonly object _lock = new object();

        internal static void Log(string source, Exception ex)
        {
            if (ex == null) return;
            try
            {
                lock (_lock)
                {
                    string path = LogPath();
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    var sb = new StringBuilder();
                    sb.AppendLine();
                    sb.AppendLine("================================================================================");
                    sb.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} UNHANDLED EXCEPTION ({source})");
                    sb.AppendLine("================================================================================");
                    AppendException(sb, ex);

                    File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
                }
            }
            catch
            {
                // Logging a crash must never itself throw -- that would mask the original
                // exception, or crash a process that's already inside an unhandled-exception
                // handler.
            }
        }

        private static void AppendException(StringBuilder sb, Exception ex)
        {
            int depth = 0;
            while (ex != null)
            {
                string prefix = depth == 0 ? "" : $"[Inner exception {depth}] ";
                sb.AppendLine($"{prefix}{ex.GetType().FullName}: {ex.Message}");
                sb.AppendLine(ex.StackTrace ?? "  (no stack trace)");
                ex = ex.InnerException;
                depth++;
            }
        }

        private static string LogPath()
        {
            string name = Assembly.GetExecutingAssembly().GetName().Name;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                name, "log_crashes.txt");
        }
    }
}
