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
#if DEBUG
                    Console.WriteLine(e);
#endif
                }
            }

#if DEBUG
            if (debug)
            {
                Console.WriteLine(s);
            }
#endif
        }

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
                    }
                    catch (Exception err)
                    {
                        err.ToString();
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
                    IpAddress       = ipAddress,
                    Port            = port,
                    Multicast       = multicast,
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
        public System.Net.IPAddress IpAddress;
        public int Port;
        public bool Multicast;
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
