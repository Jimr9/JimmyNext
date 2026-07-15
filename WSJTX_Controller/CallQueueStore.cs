using System;
using System.Collections.Generic;
using System.Text;
using WsjtxUdpLib.Messages;
using WsjtxUdpLib.Messages.Out;

namespace WSJTX_Controller
{
    // Call-queue storage/bookkeeping primitives, extracted from WsjtxClient (2026-07-09,
    // technique A of the WsjtxClient.cs modularization) -- sits alongside the
    // already-extracted CallQueueRanker.cs (ranking vs. storage). Reads/writes back
    // through the owning WsjtxClient (_wc) for callQueue/callDict themselves and all
    // cross-cutting state (ShowQueue, DebugOutput, sound settings, etc.) rather than
    // owning any of it -- those fields are touched from dozens of other places in
    // WsjtxClient that aren't moving, so only the storage operations themselves move here.
    public class CallQueueStore
    {
        private readonly WsjtxClient _wc;

        public CallQueueStore(WsjtxClient wc)
        {
            _wc = wc;
        }

        //update call in call queue
        //if to myCall and has progressed in the FT8 QSO protocol, or
        //if priority increased or if grid now available;
        //return true if call added
        public bool UpdateCall(string call, EnqueueDecodeMessage msg)
        {
            _wc.DebugOutput($"{_wc.Time()} UpdateCall");
            EnqueueDecodeMessage dmsg;
            if (_wc.callDict.TryGetValue(call, out dmsg))
            {
                if (WsjtxMessage.ToCall(msg.Message) == _wc.myCall && WsjtxMessage.ToCall(dmsg.Message) == _wc.myCall && WsjtxMessage.Progress(msg.Message) > WsjtxMessage.Progress(dmsg.Message))
                {
                    _wc.DebugOutput($"{WsjtxClient.spacer}update stage/sequence '{msg.Message}' (was '{dmsg.Message}')");
                    RemoveCall(call);
                    return AddCall(call, msg);      //re-ranked
                }

                if (call != null && _wc.callDict.ContainsKey(call))
                {
                    //check for call saved as a low-priority CQ but now high-priority call to myCall
                    if ((msg.Priority < dmsg.Priority)
                        || (WsjtxMessage.Grid(dmsg.Message) == null && WsjtxMessage.Grid(msg.Message) != null))
                    {
                        if (_wc.IsCorrectTimePeriodForMode(msg))
                        {
                            _wc.DebugOutput($"{WsjtxClient.spacer}update priority/grid  '{msg.Message}' (was '{dmsg.Message}')");
                            RemoveCall(call);
                            return AddCall(call, msg);      //re-ranked
                        }
                    }
                }
            }
            return false;
        }

        //remove call from queue/dictionary;
        //call not required to be present
        //return false if failure
        public bool RemoveCall(string call, bool updateSnapshots = true)
        {
            EnqueueDecodeMessage msg;
            if (call != null && _wc.callDict.TryGetValue(call, out msg))     //dictionary contains call data for this call sign
            {
                _wc.callDict.Remove(call);

                string[] qArray = new string[_wc.callQueue.Count];
                _wc.callQueue.CopyTo(qArray, 0);
                _wc.callQueue.Clear();
                for (int i = 0; i < qArray.Length; i++)
                {
                    if (qArray[i] != call) _wc.callQueue.Enqueue(qArray[i]);
                }

                if (_wc.callDict.Count != _wc.callQueue.Count)
                {
                    _wc.DebugOutput("ERROR: queueDict and callDict out of sync");
                    _wc.UpdateDebug();
                    return false;
                }

                _wc.ShowQueue();
                if (updateSnapshots && _wc.ctrl.advancedCallLayout) _wc.ShowAdvancedQueue(null);
                if (_wc.debugDetail) _wc.DebugOutput($"{WsjtxClient.spacer}removed {call}{_wc.nl}{CallQueueString()}");
                _wc.UpdateMaxTxRepeat();
                return true;
            }
            if (_wc.debugDetail) _wc.DebugOutput($"{WsjtxClient.spacer}not removed, not in callQueue '{call}'{_wc.nl}{CallQueueString()}");
            return false;
        }

        //add call/decode to queue/dict; call rank already set;
        //place in queue according to priority then rank using current rankMethod;
        //set sequence number if not already set
        //return false if already added
        public bool AddCall(string call, EnqueueDecodeMessage msg)
        {
            _wc._lastAddCallCategoryPlayed = false;
            var callArray = _wc.callQueue.ToArray();        //make queue accessible by index

            if (_wc.debugDetail) _wc.DebugOutput($"{_wc.Time()} AddCall, call:{call} priority:{msg.Priority} cat:{msg.Category} rank:{msg.Rank}");
            if (!_wc.callDict.ContainsKey(call))     //dictionary does not contain call data for this call sign
            {
                var tmpQueue = new Queue<string>();         //will be the updated queue

                //go thru calls in reverse time order
                int i;
                for (i = 0; i < callArray.Length; i++)
                {
                    EnqueueDecodeMessage decode;
                    if (!_wc.callDict.TryGetValue(callArray[i], out decode))     //get the decode for an existing call in the queue
                    {
                        _wc.DebugOutput("ERROR: queueDict and callDict out of sync");
                        _wc.UpdateDebug();
                        return false;
                    }
                    if (_wc.CompareRank(decode, msg) <= 0)         //reached insertion point for new call
                    {
                        break;
                    }
                    else
                    {
                        tmpQueue.Enqueue(callArray[i]); //add the existing priority call
                    }
                }
                tmpQueue.Enqueue(call);         //add the new priority call (before oldest non-priority call, or at end of all-priority-call queue)

                //fill in the remaining non-priority callls
                for (int j = i; j < callArray.Length; j++)
                {
                    tmpQueue.Enqueue(callArray[j]);
                }
                _wc.callQueue = tmpQueue;

                _wc.callDict.Add(call, msg);
                _wc._lastAddCallCategoryPlayed = _wc.PlayCategorySound(msg);

                // Feature 2: opposite-period alert — fires when an interesting call is queued
                // on the period opposite to the operator's current TX/RX focus.
                if (_wc.ctrl.soundEnabled_OppositePeriod
                    && msg.Category != WsjtxClient.CallCategory.DEFAULT
                    && _wc.IsEvenCall(msg) == _wc.txFirst   // call is on our TX period, not our listen period
                    && _wc.IsAlertCooledDown(_wc._oppositePeriodAlertTimes, call, WsjtxClient.OppositePeriodAlertCooldownSecs))
                {
                    _wc.Sounds.PlaySoundEvent(_wc.ctrl.soundEnabled_OppositePeriod, _wc.ctrl.soundFile_OppositePeriod);
                    _wc._oppositePeriodAlertTimes[call] = DateTime.UtcNow;
                    _wc.DebugOutput($"{WsjtxClient.spacer}OppositePeriod alert: '{call}' cat:{msg.Category} txFirst:{_wc.txFirst}");
                }

                _wc.ShowQueue();
                if (_wc.ctrl.advancedCallLayout) _wc.ShowAdvancedQueue(_wc.IsEvenCall(msg));
                // Stage A6: Country now comes from EffectiveClassification() instead of directly off the wire.
                string msgCountry = msg.EffectiveClassification().Country;
                if (_wc.lookupManager != null && msgCountry.Length == 0 && _wc.lookupManager.CanAutoQueue(call))
                    _wc.lookupManager.QueueAutoLookup(call);
                else if (_wc.ctrl.showUsStateCheckBox.Checked &&
                         msgCountry == "USA" &&
                         _wc.lookupManager != null &&
                         _wc.lookupManager.CanAutoQueue(call) &&
                         WsjtxClient.GridToUsState(WsjtxMessage.Grid(msg.Message)) == null)
                    _wc.lookupManager.QueueAutoLookup(call);
                if (_wc.debugDetail) _wc.DebugOutput($"{WsjtxClient.spacer}enqueued {call}{_wc.nl}{CallQueueString()}");
                _wc.UpdateMaxTxRepeat();
                _wc.UpdateCallInProg();
                return true;
            }
            if (_wc.debugDetail) _wc.DebugOutput($"{WsjtxClient.spacer}already enqueued {call}{_wc.nl}{CallQueueString()}");
            return false;
        }

        //return index/msg of specified call in queue
        //queue not assumed to have any entries;
        //return -1 if failure
        public int FindCall(string call, out EnqueueDecodeMessage dmsg)
        {
            dmsg = null;
            if (call == null) return -1;
            int idx = Array.IndexOf(_wc.callQueue.ToArray(), call);
            if (idx < 0) return -1;

            if (PeekCall(idx, out dmsg) == null) return -1;
            return idx;
        }

        //return call/msg at specified index in queue;
        //queue not assume to have any entries;
        //return null if failure
        public string PeekCall(int idx, out EnqueueDecodeMessage dmsg)
        {
            dmsg = null;
            if (_wc.callQueue.Count == 0)
            {
                _wc.DebugOutput($"{WsjtxClient.spacer}no peek");
                return null;
            }

            var callArray = _wc.callQueue.ToArray();
            if (idx < 0 || idx >= callArray.Length)
            {
                _wc.DebugOutput($"{WsjtxClient.spacer}out of range, idx:{idx}");
                return null;
            }
            string call = callArray[idx];

            if (!_wc.callDict.TryGetValue(call, out dmsg))
            {
                _wc.DebugOutput("ERROR: '{call}' not found");
                _wc.UpdateDebug();
                return null;
            }

            _wc.DebugOutput($"{WsjtxClient.spacer}peek {call}: msg:'{dmsg.Message}'");
            return call;
        }

        public string RemoveCallLast()
        {
            if (_wc.callQueue.Count == 0) return null;
            var callArray = _wc.callQueue.ToArray();
            string call = callArray[callArray.Length - 1];
            RemoveCall(call);
            return call;
        }

        public string RemoveCallLastForPeriod(bool isEven)
        {
            if (_wc.callQueue.Count == 0) return null;
            var callArray = _wc.callQueue.ToArray();
            EnqueueDecodeMessage d;
            for (int i = callArray.Length - 1; i >= 0; i--)
            {
                if (_wc.callDict.TryGetValue(callArray[i], out d) && _wc.IsEvenCall(d) == isEven)
                {
                    RemoveCall(callArray[i]);
                    return callArray[i];
                }
            }
            return null;
        }

        public int PeriodCallCount(bool isEven)
        {
            EnqueueDecodeMessage d;
            int count = 0;
            foreach (var call in _wc.callQueue)
                if (_wc.callDict.TryGetValue(call, out d) && _wc.IsEvenCall(d) == isEven)
                    count++;
            return count;
        }

        public string CallQueueString()
        {
            string delim = "";
            int count = 0;
            StringBuilder sb = new StringBuilder();
            sb.Append($"{WsjtxClient.spacer}callQueue [");
            foreach (string call in _wc.callQueue)
            {
                int pri = 0;
                int rank = 0;
                int qual = 0;
                string msg = "";
                TimeSpan sm = TimeSpan.MinValue;
                EnqueueDecodeMessage d;
                if (_wc.callDict.TryGetValue(call, out d))
                {
                    pri = d.Priority;
                    rank = d.Rank;
                    qual = d.Quality;
                    sm = d.SinceMidnight;
                    msg = d.Message;
                }

                if (++count % (_wc.debugDetail ? 2 : 5) == 0)
                {
                    sb.Append($"{_wc.nl}{WsjtxClient.spacer}");
                    delim = "";
                }

                if (_wc.debugDetail)
                {
                    sb.Append($"{delim}{call}:'{msg}'/{sm.Minutes.ToString().PadLeft(2, '0')}{sm.Seconds.ToString().PadLeft(2, '0')}/{pri}/{qual}/{rank}");
                }
                else
                {
                    sb.Append($"{delim}{call}:{sm.Minutes.ToString().PadLeft(2, '0')}{sm.Seconds.ToString().PadLeft(2, '0')}/{pri}/{qual}");
                }
                delim = ", ";
            }
            sb.Append("]");
            return sb.ToString();
        }

        public string ReportListString()
        {
            string delim = "";
            int count = 0;
            StringBuilder sb = new StringBuilder();
            sb.Append($"{WsjtxClient.spacer}sentReportList [");
            foreach (string call in _wc.sentReportList)
            {
                if (++count % 12 == 0)
                {
                    sb.Append($"{_wc.nl}{WsjtxClient.spacer}");
                    delim = "";
                }

                sb.Append(delim + call);
                delim = " ";
            }
            sb.Append("]");
            return sb.ToString();
        }

        public string SentCallListString()
        {
            string delim = "";
            int count = 0;
            StringBuilder sb = new StringBuilder();
            sb.Append($"{WsjtxClient.spacer}sentCallList [");
            foreach (string call in _wc.sentCallList)
            {
                if (++count % 12 == 0)
                {
                    sb.Append($"{_wc.nl}{WsjtxClient.spacer}");
                    delim = "";
                }

                sb.Append(delim + call);
                delim = " ";
            }
            sb.Append("]");
            return sb.ToString();
        }

        public string UnwantedCqListString()
        {
            string delim = "";
            int count = 0;
            StringBuilder sb = new StringBuilder();
            sb.Append($"{WsjtxClient.spacer}unwantedCqList [");
            foreach (string call in _wc.unwantedCqList)
            {
                if (++count % 12 == 0)
                {
                    sb.Append($"{_wc.nl}{WsjtxClient.spacer}");
                    delim = "";
                }

                sb.Append(delim + call);
                delim = " ";
            }
            sb.Append("]");
            return sb.ToString();
        }

        public string LogListString()
        {
            string delim = "";
            int count = 0;
            StringBuilder sb = new StringBuilder();
            sb.Append($"{WsjtxClient.spacer}logList [");
            foreach (string call in _wc.logList)
            {
                if (++count % 12 == 0)
                {
                    sb.Append($"{_wc.nl}{WsjtxClient.spacer}");
                    delim = "";
                }
                sb.Append(delim + call);
                delim = " ";
            }
            sb.Append("]");
            return sb.ToString();
        }

        public string CallDictString()
        {
            string delim = "";
            StringBuilder sb = new StringBuilder();
            sb.Append("callDict [");
            foreach (var entry in _wc.callDict)
            {
                sb.Append(delim + entry.Key);
                delim = " ";
            }
            sb.Append("]");
            return sb.ToString();
        }

        public string TimeoutCallDictString()
        {
            int count = 0;
            string delim = "";
            StringBuilder sb = new StringBuilder();
            sb.Append($"{WsjtxClient.spacer}timeoutCallDict [");
            foreach (var entry in _wc.timeoutCallDict)
            {
                sb.Append($"{delim}{entry.Key} {entry.Value}");
                delim = ", ";
                if (++count % 8 == 0)
                {
                    sb.Append($"{_wc.nl}{WsjtxClient.spacer}");
                    delim = "";
                }
            }
            sb.Append("]");
            return sb.ToString();
        }

        public string AllCallDictString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append($"{WsjtxClient.spacer}allCallDict");
            if (_wc.allCallDict.Count == 0)
            {
                sb.Append(" []");
            }
            else
            {
                sb.Append(":");
            }

            foreach (var entry in _wc.allCallDict)
            {
                sb.Append($"{_wc.nl}{WsjtxClient.spacer}{entry.Key} ");
                string delim = "";
                sb.Append("[");
                foreach (EnqueueDecodeMessage msg in entry.Value)
                {
                    sb.Append($"{delim}{msg.Message}:{msg.Priority} @{msg.SinceMidnight}");
                    delim = ", ";
                }
                sb.Append("]");
            }

            return sb.ToString();
        }

        //remove old rec'd calls
        public bool TrimAllCallDict()
        {
            bool removed = false;
            var keys = new List<string>();
            var dtNow = DateTime.UtcNow;
            var ts = new TimeSpan(0, _wc.maxDecodeAgeMinutes, 0);

            foreach (var entry in _wc.allCallDict)
            {
                var list = entry.Value;
                if (entry.Key != _wc.callInProg && list.Count > 0)
                {
                    var decode = list[0];           //just check the oldest entry
                    if ((dtNow - (decode.RxDate + decode.SinceMidnight)) > ts)  //entry is older than wanted
                    {
                        keys.Add(entry.Key);        //collect keys to delete
                    }
                }
            }

            //delete keys to old decodes and sent reports
            foreach (string key in keys)
            {
                if (!_wc.callQueue.Contains(key))
                {
                    _wc.RemoveAllCall(key);
                    removed = true;
                }
            }

            if (removed) _wc.DebugOutput($"{WsjtxClient.spacer}TrimAllCallDict: expired calls removed from allCallDict and/or sentReportList");
            return removed;
        }

        public bool TrimCallQueue()
        {
            bool removed = false;
            var keys = new List<string>();
            var dtNow = DateTime.UtcNow;
            var ts = new TimeSpan(0, 0, ((int)_wc.trPeriod * _wc.ctrl.maxCallQueueAgePeriods) / 1000);    //total periods

            foreach (var entry in _wc.callDict)
            {   //                              old call                                                          not manually selected
                if (entry.Key != _wc.callInProg && (dtNow - (entry.Value.RxDate + entry.Value.SinceMidnight)) > ts && entry.Value.AutoGen)  //entry is older than wanted
                {
                    keys.Add(entry.Key);        //collect keys to delete
                }
            }

            //delete keys to old decodes
            foreach (string key in keys)
            {
                RemoveCall(key, updateSnapshots: false);
                removed = true;
            }

            if (removed) _wc.DebugOutput($"{WsjtxClient.spacer}TrimCallQueue: expired calls removed from callQueue and callDict");
            if (removed && _wc.ctrl.advancedCallLayout) _wc.ShowAdvancedQueue(null);
            return removed;
        }
    }
}
