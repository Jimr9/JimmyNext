using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace WSJTX_Controller
{
    public partial class WsjtxClient
    {
        public void DebugChanged()
        {
            ShowQueue();
            if (ctrl.advancedCallLayout) ShowAdvancedQueue(null);
            UpdateCallInProg();
        }

        internal void DebugOutput(string s)
        {
            if (diagLog)
            {
                try
                {
                    if (logSw != null) logSw.WriteLine(s);
                }
                catch (Exception e)
                {
                    // Previously silently swallowed (only ever visible in a DEBUG console) --
                    // diagLog stayed true and logSw stayed open, so EVERY subsequent DebugOutput
                    // call (there are hundreds of call sites) kept retrying the same broken
                    // stream and re-catching the same exception, forever, with zero visibility
                    // to the operator in a Release build. Same class of bug SetLogFileState's
                    // own "couldn't open" catch was fixed for (above, 2026-08-08, "log is blank"
                    // report despite logging being on by default) -- this is its "couldn't WRITE
                    // to an already-open log" counterpart, missed at the time. Stop retrying and
                    // tell the operator once, matching that precedent exactly: null out logSw so
                    // the guard above skips every future call, and disable diagLog so the Options
                    // checkbox correctly shows "off" next time it's opened (LogModeChanged/
                    // OptionsDlg.cs both just read diagLog fresh, no separate state to desync).
                    logSw = null;
                    diagLog = false;
                    Notify.Publish(new ErrorWarningEvent(ErrorSeverity.Error, "Debug log",
                        $"log write failed, logging stopped: {e.Message}"));
                }
            }

#if DEBUG
            if (debug)
            {
                Console.WriteLine(s);
            }
#endif
        }

        // Test-only hook (JimmyTests, see InternalsVisibleTo in AssemblyInfo.Testing.cs) --
        // lets a test put logSw into a genuinely-broken state (e.g. a disposed StreamWriter) to
        // exercise DebugOutput's own write-failure circuit breaker without real file I/O.
        internal void TestSetLogWriter(StreamWriter sw) => logSw = sw;
        internal bool TestLogWriterIsNull => logSw == null;

        private string CurrentStatus()
        {
            string repDec = (replyDecode == null ? "''" : $"{nl}           {replyDecode}");
            return $"myCall:'{myCall}' callInProg:'{CallPriorityString(callInProg)}' qsoState:{qsoState} lastQsoState:{lastQsoState} txMsg:'{txMsg}' decodeCycle:{CurrentDecodeCycleString()}{nl}           lastTxMsg:'{lastTxMsg}' curCmd:'{curCmd}' replyCmd:'{replyCmd}' opMode:{opMode} replyDecode:{repDec}{nl}           txTimeout:{txTimeout} restartQueue:{restartQueue} xmitCycleCount:{xmitCycleCount} transmitting:{transmitting} mode:'{mode}' txEnabled:{txEnabled}{nl}           txFirst:{txFirst} dxCall:'{dxCall}' trPeriod:'{trPeriod}' settingChanged:{settingChanged} wsjtxTxEnableButton:{wsjtxTxEnableButton}{nl}           newDirCq:{newDirCq} tCall:'{tCall}' decoding:{decoding} cqPaused:{cqPaused} txMode:{txMode}{nl}           autoFreqPauseMode:{autoFreqPauseMode} consecCqCount:{consecCqCount} consecTimeoutCount:{consecTimeoutCount} holdCheckBox.Checked:{ctrl.holdCheckBox.Checked}{nl}{_callQueueStore.CallQueueString()}";
        }

        private void DebugOutputStatus()
        {
            DebugOutput($"(update)   {CurrentStatus()}");
        }

        private string DatagramString(byte[] datagram)
        {
            var sb = new StringBuilder();
            string delim = "";
            for (int i = 0; i < datagram.Length; i++)
            {
                sb.Append(delim);
                sb.Append(datagram[i].ToString("X2"));
                delim = " ";
            }
            return sb.ToString();
        }

        // Independent audit finding 10, 2026-08-23 (CLEANUP / OPERATIONAL BUG, MEDIUM/LOW
        // PRIORITY): diagnostic logs had no retention or size bound at all -- one date-named
        // file per day, AutoFlush, no rollover, no age cap. Real evidence: a single day's log
        // reached ~34MB; a machine left with diagnostic logging on could accumulate gigabytes
        // with no warning. Conservative retention (newest 30 days) only, run once when a log is
        // opened (never on every write, so this never adds per-line overhead) -- deletes ONLY
        // files matching this exact "log_M-D-YYYY.txt" naming convention inside Jimmy's own log
        // directory, never touching any other file. Best-effort: a delete failure (file in use,
        // permissions) is silently skipped rather than blocking the new log from opening.
        private const int LogRetentionDays = 30;

        private void CleanUpOldLogs()
        {
            try
            {
                if (!Directory.Exists(path)) return;
                DateTime cutoff = DateTime.Now.Date.AddDays(-LogRetentionDays);
                int removed = 0;
                foreach (string file in Directory.GetFiles(path, "log_*.txt"))
                {
                    string stem = Path.GetFileNameWithoutExtension(file);
                    string datePart = stem.Length > 4 ? stem.Substring(4) : null; // strip "log_"
                    if (datePart != null && DateTime.TryParse(datePart.Replace('-', '/'), out DateTime fileDate)
                        && fileDate.Date < cutoff)
                    {
                        try { File.Delete(file); removed++; } catch { /* in use/permissions -- skip, try again next time a log opens */ }
                    }
                }
                if (removed > 0) DebugOutput($"{Time()} Diagnostic log retention: removed {removed} log file(s) older than {LogRetentionDays} days");
            }
            catch { /* best-effort housekeeping only -- must never block opening the real log */ }
        }

        // internal (not private): JimmyTests exercises the retention logic directly against an
        // isolated temp directory (wc.path overridden), never the real operator log folder.
        internal void TestCleanUpOldLogs() => CleanUpOldLogs();

        //set log file open/closed state
        //return new diagnostic log file state (true = open)
        private bool SetLogFileState(bool enable)
        {
            if (enable)         //want log file opened for write
            {
                if (logSw == null)     //log not already open
                {
                    try
                    {
                        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                        logSw = File.AppendText($"{path}\\log_{DateTime.Now.Date.ToShortDateString().Replace('/', '-')}.txt");      //local time
                        logSw.AutoFlush = true;
                        logSw.WriteLine($"{nl}{nl}{Time()} Opened log");
                        CleanUpOldLogs(); // after logSw is live so its own "removed N" notice (if any) actually lands in the log
                    }
                    catch (Exception err)
                    {
                        // Previously discarded (err.ToString() computed, never used) -- the log
                        // checkbox reflects the saved setting, not whether the file actually
                        // opened, so a failure here was completely invisible: checkbox checked,
                        // log file silently never created, no error anywhere. Confirmed live,
                        // 2026-08-08, as the likely explanation for a tester's "log is blank"
                        // report despite logging being on by default.
                        Notify.Publish(new ErrorWarningEvent(ErrorSeverity.Error, "Debug log",
                            $"couldn't open {path}: {err.Message}"));
                        logSw = null;
                        return false;       //log file state = closed
                    }
                }
                return true;       //log file state = open
            }
            else    //want log file flushed and closed
            {
                if (logSw != null)
                {
                    logSw.WriteLine($"{Time()} Closing log...");
                    logSw.Flush();
                    logSw.Close();
                    logSw = null;
                }
                return false;       //log file state = closed
            }
        }

        public WsjtxDiagData GetDiagnosticData()
        {
            try
            {
                var queueEntries = new List<CallQueueDiagEntry>();
                int pos = 0;
                foreach (string callsign in callQueue)
                {
                    pos++;
                    if (callDict.TryGetValue(callsign, out var dm))
                    {
                        // Stage A6: classification-derived fields below all read from
                        // EffectiveClassification() instead of directly off the wire.
                        ClassifiedCall dmClassification = dm.EffectiveClassification();
                        queueEntries.Add(new CallQueueDiagEntry
                        {
                            Callsign            = callsign,
                            QueuePosition       = pos,
                            Country             = dmClassification.Country ?? "",
                            Message             = dm.Message ?? "",
                            Snr                 = dm.Snr,
                            Category            = dm.Category.ToString(),
                            IsNewCountry        = dmClassification.IsNewCountry,
                            IsNewCountryOnBand  = dmClassification.IsNewCountryOnBand,
                            Distance            = dmClassification.Distance,
                            Azimuth             = dmClassification.Azimuth,
                        });
                    }
                }

                var decodeHistory = new List<DecodeHistoryDiagEntry>();
                foreach (var dm in _rawDecodeHistory)
                {
                    try
                    {
                        // Stage A6: classification-derived fields below all read from
                        // EffectiveClassification() instead of directly off the wire.
                        ClassifiedCall dmClassification = dm.EffectiveClassification();
                        decodeHistory.Add(new DecodeHistoryDiagEntry
                        {
                            TimeUtc            = (dm.RxDate + dm.SinceMidnight).ToString("HH:mm:ss"),
                            Message            = dm.Message ?? "",
                            Mode               = dm.Mode ?? "",
                            Snr                = dm.Snr,
                            DeltaTime          = dm.DeltaTime,
                            DeltaFrequency     = dm.DeltaFrequency,
                            Country            = dmClassification.Country ?? "",
                            Category           = dm.Category.ToString(),
                            IsNewCountry       = dmClassification.IsNewCountry,
                            IsNewCountryOnBand = dmClassification.IsNewCountryOnBand,
                            IsDx               = dmClassification.IsDx,
                        });
                    }
                    catch { /* skip individual entry on error */ }
                }

                return new WsjtxDiagData
                {
                    MyCall          = myCall,
                    MyGrid          = myGrid,
                    Mode            = mode,
                    TxFirst         = txFirst,
                    Connected       = ConnectedToWsjtx(),
                    Connecting      = WsjtxConnecting(),
                    PgmName         = pgmName,
                    PgmVer          = pgmVer,
                    Port            = port,
                    DiagLog         = diagLog,
                    UsePskReporter  = usePskReporter,
                    TxMode          = txMode,
                    CallInProg      = callInProg,
                    DialFrequency   = dialFrequency,
                    BandIdx         = bandIdx,
                    Bands           = bands.ToArray(),
                    CallQueueCount  = callQueue.Count,
                    LoggedCount     = logList.Count,
                    Tx1Count        = _tx1SnapshotRows.Count,
                    Tx2Count        = _tx2SnapshotRows.Count,
                    RawDecodeCount  = _rawDecodeHistory.Count,
                    CallQueueDetails = queueEntries,
                    DecodeHistory   = decodeHistory,
                };
            }
            catch
            {
                return new WsjtxDiagData();
            }
        }
    }

    public class WsjtxDiagData
    {
        public string MyCall;
        public string MyGrid;
        public string Mode   = "";
        public bool TxFirst;
        public bool Connected;
        public bool Connecting;
        public string PgmName;
        public string PgmVer;
        public int Port;
        public bool DiagLog;
        public bool UsePskReporter;
        public WsjtxClient.TxModes TxMode;
        public string CallInProg;
        public ulong DialFrequency;
        public int? BandIdx;
        public int[] Bands = new int[0];
        public int CallQueueCount;
        public int LoggedCount;
        public int Tx1Count;
        public int Tx2Count;
        public int RawDecodeCount;
        public List<CallQueueDiagEntry>     CallQueueDetails = new List<CallQueueDiagEntry>();
        public List<DecodeHistoryDiagEntry> DecodeHistory    = new List<DecodeHistoryDiagEntry>();
    }

    public class CallQueueDiagEntry
    {
        public string Callsign;
        public int    QueuePosition;
        public string Country;
        public string Message;
        public int    Snr;
        public string Category;
        public bool   IsNewCountry;
        public bool   IsNewCountryOnBand;
        public int    Distance;
        public int    Azimuth;
    }

    public class DecodeHistoryDiagEntry
    {
        public string TimeUtc;
        public string Message;
        public string Mode;
        public int    Snr;
        public double DeltaTime;
        public int    DeltaFrequency;
        public string Country;
        public string Category;
        public bool   IsNewCountry;
        public bool   IsNewCountryOnBand;
        public bool   IsDx;
    }
}
