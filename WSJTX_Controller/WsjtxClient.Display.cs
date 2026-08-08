using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using WsjtxUdpLib.Messages;
using WsjtxUdpLib.Messages.Out;

namespace WSJTX_Controller
{
    public partial class WsjtxClient
    {
        internal bool PlayCategorySound(EnqueueDecodeMessage msg)
        {
            string call = msg.DeCall();
            switch (msg.Category)
            {
                case CallCategory.TO_MYCALL:
                    return Sounds.PlaySoundEvent(ctrl.mycallCheckBox.Checked, ctrl.soundFile_CallingMe, call, "CALLING_ME");
                case CallCategory.NEW_COUNTRY:
                    return Sounds.PlaySoundEvent(ctrl.soundEnabled_NewDxcc, ctrl.soundFile_NewDxcc, call, "NEW_COUNTRY");
                case CallCategory.NEW_COUNTRY_ON_BAND:
                    return Sounds.PlaySoundEvent(ctrl.soundEnabled_NewDxccOnBand, ctrl.soundFile_NewDxccOnBand, call, "NEW_COUNTRY_ON_BAND");
                case CallCategory.ALWAYS_WANTED:
                    return Sounds.PlaySoundEvent(ctrl.soundEnabled_AlwaysWanted, ctrl.soundFile_AlwaysWanted, call, "ALWAYS_WANTED");
                case CallCategory.WANTED_CQ:
                    if (IsPotaCall(msg) && ctrl.soundEnabled_Pota && !string.IsNullOrEmpty(ctrl.soundFile_Pota))
                        return Sounds.PlaySoundEvent(ctrl.soundEnabled_Pota, ctrl.soundFile_Pota, call, "POTA");
                    if (_awardTagger.IsSotaCall(msg) && ctrl.soundEnabled_Sota && !string.IsNullOrEmpty(ctrl.soundFile_Sota))
                        return Sounds.PlaySoundEvent(ctrl.soundEnabled_Sota, ctrl.soundFile_Sota, call, "SOTA");
                    return Sounds.PlaySoundEvent(ctrl.soundEnabled_DirectedCq, ctrl.soundFile_DirectedCq, call, "DIRECTED_CQ");
                case CallCategory.POTA:
                    return Sounds.PlaySoundEvent(ctrl.soundEnabled_Pota, ctrl.soundFile_Pota, call, "POTA");
                case CallCategory.SOTA:
                    return Sounds.PlaySoundEvent(ctrl.soundEnabled_Sota, ctrl.soundFile_Sota, call, "SOTA");
                case CallCategory.STILL_NEEDED:
                    // The award-match sound is handled uniformly by CheckAwardAlert, which
                    // runs independently of Category/admission for every decode -- returning
                    // true here just prevents the generic "Call added" fallback from also
                    // playing for the same, already-alerted station.
                    return true;
                default:
                    return false;
            }
        }

        internal bool IsAlertCooledDown(Dictionary<string, DateTime> dict, string call, int cooldownSecs)
        {
            DateTime last;
            if (!dict.TryGetValue(call, out last)) return true;
            return (DateTime.UtcNow - last).TotalSeconds >= cooldownSecs;
        }

        internal void ShowQueue()
        {
            int q = callQueue.Count;
            bool callInProgInQueue = callInProg != null && callQueue.Contains(callInProg);
            int displayQ = callInProgInQueue ? q - 1 : q;

            // Build the new row list completely in memory before touching the UI.
            // callInProg is excluded from the display rows; _callListBoxQueueIndices maps
            // each remaining display row back to its true queue position so that
            // Enter/double-click/right-click still address the correct queue entry.
            var newItems = new List<string>();
            var newKeys = new List<string>();
            var newCategories = new List<CallCategory>();
            var newQueueIndices = new List<int>();
            SelectionMode newMode;

            if (displayQ == 0)
            {
                newMode = SelectionMode.None;
                newItems.Add(callInProg == null
                    ? "[No stations calling or in progress]"
                    : "[No stations calling]");
                newKeys.Add(null);      // keep keys parallel to items even for the placeholder row
                newCategories.Add(CallCategory.DEFAULT);
            }
            else
            {
                newMode = SelectionMode.One;
                int queuePos = 0;
                foreach (string call in callQueue)
                {
                    if (callInProgInQueue && StringComparer.OrdinalIgnoreCase.Equals(call, callInProg))
                    { queuePos++; continue; }
                    EnqueueDecodeMessage d;
                    if (callDict.TryGetValue(call, out d))
                    {
                        newItems.Add(BuildCallWaitingRow(call, d));
                        newKeys.Add(call);
                        newCategories.Add(d.Category);
                        newQueueIndices.Add(queuePos);
                    }
                    queuePos++;
                }
            }
            _callListBoxQueueIndices = newQueueIndices;

            // Advanced TX1/TX2 lists are driven by retained snapshots updated only by
            // AddCall (and global clears). ShowQueue never touches them so that
            // RemoveCall and TrimCallQueue cannot erase the opposite side's display.

            QueueView.RenderCallQueue($"Stations calling: {displayQ}", newItems, newKeys, newCategories, newMode);
        }

        public void RefreshCallWaitingRows()
        {
            ShowQueue();
            if (ctrl.advancedCallLayout) ShowAdvancedQueue(null);
        }

        public void RefreshAdvancedLists()
        {
            if (!ctrl.advancedCallLayout) return;
            ShowAdvancedQueue();
            if (ctrl.advShowRaw) ShowRawDecodes();
        }

        internal void ShowAdvancedQueue(bool? evenSide = null)
        {
            // evenSide==true  → only TX1 (even) snapshot is rebuilt (AddCall for TX1).
            // evenSide==false → only TX2 (odd)  snapshot is rebuilt (AddCall for TX2).
            // evenSide==null  → both snapshots rebuilt (ClearCalls, sort, debug, startup).
            //
            // RemoveCall and TrimCallQueue never call this method, so the snapshot for
            // each side is frozen between its own AddCall events — the opposite side's
            // retained display is never touched.
            bool rebuildTx1 = evenSide == null || evenSide == true;
            bool rebuildTx2 = evenSide == null || evenSide == false;

            // While a side is our active Tx slot and the user has "keep transmit list
            // during Tx" unchecked, keep that side's snapshot forcibly empty here instead
            // of repopulating it -- otherwise any decode/queue change that happens mid-
            // transmission (very common) silently refills it before the Tx cycle even
            // ends, undoing ProcessTxStart()'s clear. Resumes populating normally the
            // moment transmitting goes false (Tx end) for that side.
            bool suppressTx1 = !ctrl.keepTransmitListDuringTx && transmitting && txFirst;
            bool suppressTx2 = !ctrl.keepTransmitListDuringTx && transmitting && !txFirst;

            if (rebuildTx1)
            {
                _tx1SnapshotRows  = new List<string>();
                _tx1SnapshotCalls = new List<string>();
                _tx1SnapshotCategories = new List<CallCategory>();
                if (!suppressTx1)
                {
                    foreach (string call in callQueue)
                    {
                        if (StringComparer.OrdinalIgnoreCase.Equals(call, callInProg)) continue;
                        EnqueueDecodeMessage d;
                        if (!callDict.TryGetValue(call, out d)) continue;
                        if (!IsEvenCall(d)) continue;
                        _tx1SnapshotCalls.Add(call);
                        _tx1SnapshotRows.Add(BuildCallWaitingRow(call, d));
                        _tx1SnapshotCategories.Add(d.Category);
                    }
                }
            }

            if (rebuildTx2)
            {
                _tx2SnapshotRows  = new List<string>();
                _tx2SnapshotCalls = new List<string>();
                _tx2SnapshotCategories = new List<CallCategory>();
                if (!suppressTx2)
                {
                    foreach (string call in callQueue)
                    {
                        if (StringComparer.OrdinalIgnoreCase.Equals(call, callInProg)) continue;
                        EnqueueDecodeMessage d;
                        if (!callDict.TryGetValue(call, out d)) continue;
                        if (IsEvenCall(d)) continue;
                        _tx2SnapshotCalls.Add(call);
                        _tx2SnapshotRows.Add(BuildCallWaitingRow(call, d));
                        _tx2SnapshotCategories.Add(d.Category);
                    }
                }
            }

            if (ctrl.advShowTx1 && rebuildTx1)
            {
                bool tx1HasItems = _tx1SnapshotRows.Count > 0;
                string tx1Prefix = txFirst ? "TX1" : "RX1";
                string tx1Name = $"{tx1Prefix} available stations, {_tx1SnapshotRows.Count} calls";
                var display = tx1HasItems
                    ? _tx1SnapshotRows
                    : new List<string> { "No available stations" };
                var keys = tx1HasItems
                    ? _tx1SnapshotCalls
                    : new List<string> { null };
                var categories = tx1HasItems
                    ? _tx1SnapshotCategories
                    : new List<CallCategory> { CallCategory.DEFAULT };
                QueueView.RenderAdvancedList(true, tx1Name, display, keys, categories);
            }

            if (ctrl.advShowTx2 && rebuildTx2)
            {
                bool tx2HasItems = _tx2SnapshotRows.Count > 0;
                string tx2Prefix = txFirst ? "RX2" : "TX2";
                string tx2Name = $"{tx2Prefix} available stations, {_tx2SnapshotRows.Count} calls";
                var display = tx2HasItems
                    ? _tx2SnapshotRows
                    : new List<string> { "No available stations" };
                var keys = tx2HasItems
                    ? _tx2SnapshotCalls
                    : new List<string> { null };
                var categories = tx2HasItems
                    ? _tx2SnapshotCategories
                    : new List<CallCategory> { CallCategory.DEFAULT };
                QueueView.RenderAdvancedList(false, tx2Name, display, keys, categories);
            }
        }

        private string BuildCallWaitingRow(string call, EnqueueDecodeMessage d)
        {
            // Stage A6: classification-derived fields below all read from
            // EffectiveClassification() instead of directly off the wire.
            ClassifiedCall classification = d.EffectiveClassification();

            string snr = $", {d.Snr.ToString("+#;-#;0")}";
            string countryName = classification.Country;
            if (countryName.Length == 0 && lookupManager != null && lookupManager.Enabled)
            {
                var rec = lookupManager.Build(call);
                if (!string.IsNullOrEmpty(rec.Country)) countryName = rec.Country;
            }
            string country = countryName.Length > 0 ? $", {countryName}" : "";

            string g = WsjtxMessage.Grid(d.Message);
            string grid = g == null ? "" : $", {SpacifyPayload(g)}";

            if (ctrl.showUsStateCheckBox.Checked &&
                classification.Country == "USA" &&
                d.Priority != (int)CallPriority.NEW_COUNTRY_ON_BAND &&
                d.Priority != (int)CallPriority.NEW_COUNTRY)
            {
                string qrzState = null;
                if (lookupManager != null && lookupManager.Enabled)
                {
                    var rec = lookupManager.Build(call);
                    qrzState = rec.State;
                }
                string state = ResolveUsState(qrzState, GridToUsState(g));
                if (state != null) country = $", {state}";
            }

            int dist = metricUnits || classification.Distance < 0 ? classification.Distance : (int)((0.6213 * classification.Distance) + 0.5);
            string unitsStr = metricUnits ? "km" : "mi";
            string distAz = (classification.Distance >= 0 && classification.Azimuth >= 0) ? $", {dist}{unitsStr}, {classification.Azimuth}°" : "";

            string oe = debug ? $", {d.SinceMidnight.Minutes.ToString().PadLeft(2, '0')}:{d.SinceMidnight.Seconds.ToString().PadLeft(2, '0')}" : "";

            string to = WsjtxMessage.DirectedTo(d.Message);
            string dirTo = (to == null ? "" : $" {to}");
            string callp = $"{Spacify(call)}";
            string pri = (d.Priority == (int)CallPriority.TO_MYCALL) ? " replying" : (d.Priority == (int)CallPriority.WANTED_CQ ? dirTo : "");

            string rankStr = debug ? $", {d.Rank}" : "";
            string descr = debug ? $", {Reason(d)}" : "";
            string tagRaw = _awardTagger.CategoryTag(d);
            string tagStr = tagRaw.Length > 0 ? $", {tagRaw}" : "";

            string fallback = $"{callp}{pri}{tagStr}{grid}{snr}{country}{distAz}{oe}{descr}{rankStr}";
            var fieldMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "callp", callp }, { "pri", pri }, { "tag", tagStr }, { "grid", grid }, { "snr", snr },
                { "country", country }, { "distAz", distAz }, { "oe", oe },
                { "descr", descr }, { "rankStr", rankStr }
            };
            return RowFormatter.BuildOrderedRow(fieldMap, callWaitingRowOrderFields, fallback);
        }

        private void UpdateListIfChanged(ListBox lb, List<string> newItems)
        {
            bool changed = lb.Items.Count != newItems.Count;
            if (!changed)
            {
                for (int i = 0; i < newItems.Count; i++)
                {
                    if ((string)lb.Items[i] != newItems[i]) { changed = true; break; }
                }
            }
            if (!changed) return;

            lb.BeginUpdate();
            try
            {
                lb.Items.Clear();
                lb.Items.AddRange(newItems.ToArray());
            }
            finally { lb.EndUpdate(); }
        }

        private static readonly Dictionary<CallCategory, string> RawTagLabels =
            new Dictionary<CallCategory, string>
        {
            { CallCategory.NEW_COUNTRY,         "New DXCC" },
            { CallCategory.NEW_COUNTRY_ON_BAND, "New DXCC band" },
            { CallCategory.ALWAYS_WANTED,       "Wanted" },
            { CallCategory.TO_MYCALL,           "Calling me" },
            { CallCategory.MANUAL_SEL,          "Manual" },
            { CallCategory.WANTED_CQ,           "Dir CQ" },
            { CallCategory.POTA,                "POTA" },
            { CallCategory.SOTA,                "SOTA" },
            { CallCategory.WAS_NEEDED,          "WAS Needed" },
            { CallCategory.WAS_UNCONFIRMED,     "WAS Unconf" },
            { CallCategory.DXCC_UNCONFIRMED,    "DXCC Unconf" },
            { CallCategory.ZONE_NEEDED,         "Zone Needed" },
        };

        private void ShowRawDecodes()
        {
            var items = new List<string>();
            // Parallel to items; a decode's callsign alone isn't a unique-enough identity here
            // (the same station can appear in several rows -- CQ, reply, report, ...), so the
            // key includes enough of the decode to disambiguate the specific row.
            var keys = new List<string>();
            var categories = new List<CallCategory>();
            foreach (var d in _rawDecodeHistory)
            {
                if (!PassesRawDecodeFilter(d)) continue;

                // Stage A6: classification-derived fields below all read from
                // EffectiveClassification() instead of directly off the wire.
                ClassifiedCall classification = d.EffectiveClassification();

                string side = IsEvenCall(d) ? "TX1" : "TX2";

                string tag = "";
                if (rawPriorityTags && d.Category != CallCategory.DEFAULT)
                {
                    string catTag;
                    if (d.Category == CallCategory.WANTED_CQ)
                        catTag = WsjtxMessage.DirectedTo(d.Message) ?? "Dir CQ";
                    else if (d.Category == CallCategory.STILL_NEEDED)
                        catTag = _awardTagger.AwardDisplayName(d) + " Needed";
                    else
                        RawTagLabels.TryGetValue(d.Category, out catTag);
                    if (!string.IsNullOrEmpty(catTag)) tag = catTag;
                }
                if (WsjtxMessage.IsFoxHound(d.Message))
                    tag = tag.Length > 0 ? $"{tag}, Possible F/H" : "Possible F/H";
                tag = tag.Length > 0 ? $", {tag}" : "";

                string callsign = d.DeCall();
                callsign = string.IsNullOrEmpty(callsign) ? "" : $", {Spacify(callsign)}";

                string message = $", {d.Message}";

                string snr = ctrl.rawShowSnr ? $", {d.Snr.ToString("+#;-#;0")}dB" : "";

                string g = WsjtxMessage.Grid(d.Message);
                string grid = ctrl.rawShowGrid && g != null ? $", {g}" : "";

                string country = ctrl.rawShowCountry && classification.Country.Length > 0 ? $", {classification.Country}" : "";
                if (ctrl.showUsStateCheckBox.Checked && classification.Country == "USA" && g != null)
                {
                    string qrzState = null;
                    if (lookupManager != null && lookupManager.Enabled)
                    {
                        var rec = lookupManager.Build(d.DeCall());
                        qrzState = rec.State;
                    }
                    string state = ResolveUsState(qrzState, GridToUsState(g));
                    if (state != null) country = $", {state}";
                }

                string distAz = "";
                if (ctrl.rawShowDistAz && classification.Distance >= 0 && classification.Azimuth >= 0)
                {
                    int dist = metricUnits || classification.Distance < 0 ? classification.Distance : (int)((0.6213 * classification.Distance) + 0.5);
                    string unitsStr = metricUnits ? "km" : "mi";
                    distAz = $", {dist}{unitsStr} {classification.Azimuth}°";
                }

                // Fallback (only reached if rawDecodeRowOrderFields is somehow null) matches
                // the default order itself, so there is one obvious answer for "what does
                // this look like with nothing configured" rather than a second hand-rolled
                // format to keep in sync.
                string fallback = $"{tag}{$", {side}"}{message}{snr}{grid}{country}{distAz}".TrimStart(',', ' ');
                var fieldMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "tag", tag }, { "side", $", {side}" }, { "callsign", callsign }, { "message", message },
                    { "snr", snr }, { "grid", grid }, { "country", country }, { "distAz", distAz },
                };
                items.Add(RowFormatter.BuildOrderedRow(fieldMap, rawDecodeRowOrderFields, fallback));
                keys.Add($"{d.DeCall()}|{d.Message}|{d.SinceMidnight.Ticks}");
                categories.Add(d.Category);
            }
            if (ctrl.rawNewestFirst) { items.Reverse(); keys.Reverse(); categories.Reverse(); }
            if (items.Count == 0) { items.Add("[No decodes this period]"); keys.Add(null); categories.Add(CallCategory.DEFAULT); }

            QueueView.RenderRawDecodes(items, keys, categories);
        }

        private bool PassesRawDecodeFilter(EnqueueDecodeMessage d)
        {
            // Stage A6: classification-derived fields below all read from
            // EffectiveClassification() instead of directly off the wire.
            ClassifiedCall classification = d.EffectiveClassification();

            // Advanced filter: only decodes with a callsign
            if (ctrl.rawOnlyCallsigns && string.IsNullOrEmpty(d.DeCall())) return false;

            // rawOnlyUnworked: station must be new on the current band (not in WSJT-X log)
            if (ctrl.rawOnlyUnworked)
            {
                if (string.IsNullOrEmpty(d.DeCall())) return false;
                if (!classification.IsNewCallOnBand) return false;
            }

            // rawOnlyRanked: station must pass Tilly's basic call-wanted criteria,
            // mirroring the gates in AddSelectedCall (new-on-band, origin, band scope,
            // OR new-country-on-band with checkbox, OR directed alert with checkbox).
            if (ctrl.rawOnlyRanked)
            {
                if (string.IsNullOrEmpty(d.DeCall())) return false;

                bool isNewCtyOnBand    = classification.IsNewCountryOnBand;
                bool isDirAlert        = d.IsCQ() && IsDirectedAlert(WsjtxMessage.DirectedTo(d.Message), classification.IsDx);
                bool isWantedDirected  = ctrl.replyDirCqCheckBox.Checked && isDirAlert;

                if (!isNewCtyOnBand && !isWantedDirected)
                {
                    // Primary gate: must be new on current band
                    if (!classification.IsNewCallOnBand) return false;

                    // Origin filter: DX and/or local
                    bool wantedOrigin = (ctrl.replyDxCheckBox.Checked && classification.IsDx)
                                     || (ctrl.replyLocalCheckBox.Checked && !classification.IsDx);
                    if (!wantedOrigin) return false;

                    // Band scope: when set to "Any band", station must also be new on any band
                    if (ctrl.bandComboBox.SelectedIndex == (int)NewCallBands.ANY && !classification.IsNewCallAnyBand)
                        return false;
                }
            }

            // Classify message type
            bool isPota   = d.Message.Contains("POTA");
            bool isSota   = d.Message.Contains("SOTA");
            bool isDxCq   = d.IsCQ() && d.Message.Contains(" DX ");
            bool isCq     = d.IsCQ() && !isPota && !isSota && !isDxCq;
            bool isRR73   = d.IsRR73();
            bool is73     = d.Is73();

            // For non-CQ, non-terminal messages determine report vs directed.
            // WsjtxMessage.DirectedTo() returns null for non-CQ messages, so use
            // the specific message-type predicates instead.
            bool isReport   = false;
            bool isDirected = false;
            if (!isCq && !isDxCq && !isPota && !isSota && !isRR73 && !is73)
            {
                isReport   = WsjtxMessage.IsReport(d.Message) || WsjtxMessage.IsRogerReport(d.Message);
                isDirected = !isReport;
            }

            // Apply message type filters
            if (isPota     && !ctrl.rawShowPota)      return false;
            if (isSota     && !ctrl.rawShowSota)      return false;
            if (isDxCq     && !ctrl.rawShowDx)        return false;
            if (isCq       && !ctrl.rawShowCq)        return false;
            if (isRR73     && !ctrl.rawShowRR73)      return false;
            if (is73       && !ctrl.rawShow73)        return false;
            if (isReport   && !ctrl.rawShowReports)   return false;
            if (isDirected && !ctrl.rawShowDirected)  return false;

            return true;
        }

        // ===== Advanced list index helpers =====

        private string GetFilteredCall(bool evenSide, int listIdx, out int queueIdx)
        {
            queueIdx = -1;
            var arr = callQueue.ToArray();
            int count = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                EnqueueDecodeMessage d;
                if (!callDict.TryGetValue(arr[i], out d)) continue;
                if (IsEvenCall(d) == evenSide)
                {
                    if (count == listIdx) { queueIdx = i; return arr[i]; }
                    count++;
                }
            }
            return null;
        }

        // Return call sign from the retained TX1 display snapshot at the given list index.
        // The call may or may not still be in the live callQueue (snapshot persists across removes).
        public string GetCallAtTx1Index(int listIdx)
        {
            if (listIdx < 0 || listIdx >= _tx1SnapshotCalls.Count) return null;
            return _tx1SnapshotCalls[listIdx];
        }

        public string GetCallAtTx2Index(int listIdx)
        {
            if (listIdx < 0 || listIdx >= _tx2SnapshotCalls.Count) return null;
            return _tx2SnapshotCalls[listIdx];
        }

        // Return the current callQueue array index for the call shown at listIdx in the
        // TX1 snapshot.  Returns -1 when the call is no longer in the live queue.
        public int GetQueueIndexForTx1(int listIdx)
        {
            string call = GetCallAtTx1Index(listIdx);
            return call != null ? FindCallIndexInQueue(call) : -1;
        }

        public int GetQueueIndexForTx2(int listIdx)
        {
            string call = GetCallAtTx2Index(listIdx);
            return call != null ? FindCallIndexInQueue(call) : -1;
        }

        // Find the call's position in the current callQueue array; -1 if absent.
        private int FindCallIndexInQueue(string call)
        {
            var arr = callQueue.ToArray();
            for (int i = 0; i < arr.Length; i++)
                if (string.Equals(arr[i], call, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        public void NextCallFromTx1(int listIdx)
        {
            string call = GetCallAtTx1Index(listIdx);
            if (call == null) return;
            int qi = FindCallIndexInQueue(call);
            if (qi >= 0) NextCall(false, qi, operatorSelected: true, expectedCall: call);
        }

        public void NextCallFromTx2(int listIdx)
        {
            string call = GetCallAtTx2Index(listIdx);
            if (call == null) return;
            int qi = FindCallIndexInQueue(call);
            if (qi >= 0) NextCall(false, qi, operatorSelected: true, expectedCall: call);
        }

        // Maps a filtered display index (advRawListBox.SelectedIndex) to the
        // corresponding entry in _rawDecodeHistory, skipping items that do not
        // pass the current filter.  Returns null when out of range.
        private EnqueueDecodeMessage GetFilteredRawDecode(int listIdx)
        {
            int count = 0;
            foreach (var d in _rawDecodeHistory)
            {
                if (!PassesRawDecodeFilter(d)) continue;
                if (count == listIdx) return d;
                count++;
            }
            return null;
        }

        public void NextCallFromRawDecode(int listIdx)
        {
            // Use the filter-aware index so the correct decode is retrieved even
            // when some message types are hidden.
            var d = GetFilteredRawDecode(listIdx);
            if (d == null) return;
            string deCall = d.DeCall();
            if (string.IsNullOrEmpty(deCall)) return;
            if (!ConnectedToWsjtx()) return;

            // If the call is already in the queue use the standard NextCall path,
            // which handles listen-mode period checks, discard tracking, etc.
            var arr = callQueue.ToArray();
            for (int i = 0; i < arr.Length; i++)
            {
                if (string.Equals(arr[i], deCall, StringComparison.OrdinalIgnoreCase))
                {
                    NextCall(false, i, operatorSelected: true, expectedCall: deCall);
                    return;
                }
            }

            // Not in queue — do not transmit.  The call was deliberately excluded
            // by queue filters (already logged, blocked, origin filter, wrong period,
            // etc.).  Bypassing those filters via ReplyTo would be unsafe.
            StatusView.ShowMessage($"{deCall} not in call queue", false);
        }

        // Like GetRawDecodeCallOrText, but returns null (rather than falling back to
        // the raw message text) when the line has no discernible callsign -- callers
        // that need an actual callsign (e.g. station lookup) should use this instead.
        public string GetCallAtRawIndex(int listIdx)
        {
            var d = GetFilteredRawDecode(listIdx);
            return d?.DeCall();
        }

        public string GetRawDecodeCallOrText(int listIdx)
        {
            // Use filter-aware lookup so Ctrl+C copies the call the user actually sees.
            var d = GetFilteredRawDecode(listIdx);
            if (d == null) return null;
            string deCall = d.DeCall();
            return string.IsNullOrEmpty(deCall) ? d.Message : deCall;
        }

        private void ShowStatus()
        {
            string status = "";
            Color foreColor = Color.Black;
            Color backColor = Color.Yellow;     //caution
            // True only when this call is a purely routine "available stations" summary
            // with nothing else worth saying right now -- set below, at the top of the
            // ACTIVE case, from the SAME one-off flags that case's own reset block clears
            // at the end (finalSignoffCall, uploadResult, newBand, etc.), read here before
            // any of them are touched. Defaults false (don't defer) for every other opMode
            // and for the special-case branches within ACTIVE (tuning, replyFromInProg,
            // etc.) -- only the plain, nothing-special case is ever eligible to wait.
            bool deferEligible = false;

            string k = cmdPrompts ? $", use Alt, K, for command key list" : "";

            try
            {
                if (WsjtxMessage.NegoState == WsjtxMessage.NegoStates.WAIT)
                {
                    status = $"{pgmName} {pgmVer}. Waiting for WSJT-X{k}.";
                    foreColor = Color.Black;
                    backColor = Color.Orange;
                    return;
                }

                if (WsjtxMessage.NegoState == WsjtxMessage.NegoStates.FAIL)
                {
                    status = failReason;
                    backColor = Color.Red;
                    return;
                }

                if (WsjtxMessage.NegoState == WsjtxMessage.NegoStates.INITIAL)
                {
                    status = $"{pgmName} {pgmVer}. Connecting to WSJT-X{k}.";
                    foreColor = Color.Black;
                    backColor = Color.Orange;
                }
                else  //includes NegoState = SENT or RECD
                {
                    switch ((int)opMode)
                    {
                        case (int)OpModes.START:
                            string newSel = "";
                            if (newMode)
                            {
                                newSel = $"{mode} mode selected.";
                            }

                            if (newBand)
                            {
                                string b = bandIdx != null ? $"{bands[(int)bandIdx]} meter" : "Unknown";
                                newSel = $"{b} band selected.";
                            }

                            if (ctrl.freqCheckBox.Checked)
                            {
                                status = $"{newSel} Analyzing audio, calls not queued yet{k}.";
                            }
                            else
                            {
                                status = $"{newSel}Connecting to WSJT-X, wait until ready{k}.";
                            }
                            foreColor = Color.Black;
                            backColor = Color.Orange;
                            newBand = false;
                            return;
                        case (int)OpModes.IDLE:
                            status = modeSupported ? $"Connecting to WSJT-X, wait until ready{k}." : "WSJT-X operating mode not supported";
                            foreColor = Color.Black;
                            backColor = Color.Orange;
                            return;
                        case (int)OpModes.ACTIVE:
                            // Must be read here, before any of these get consumed/reset (see
                            // the reset block near the end of this case) -- true only when
                            // NOTHING special is being reported this round, i.e. this really
                            // would just be the routine "N available stations" summary, safe
                            // to batch with the rest of the period. A one-off event (a final
                            // 73, a band/mode change, an upload result, etc.) always announces
                            // immediately regardless of decode-batch timing.
                            //
                            // Deliberately NOT excluding cqPaused here (first attempt did, and
                            // it was wrong): confirmed live, 2026-08-07, cqPaused reads True
                            // continuously through ordinary Listen-mode monitoring -- it's a
                            // persistent mode flag, not a one-off event -- so excluding it
                            // silently disabled deferral for exactly the scenario this whole
                            // fix is for. The cqPaused branch below still only ever assembles
                            // the same callsWaiting-driven routine text (or tuneResult, or
                            // whatever finalSignoffCall/uploadResult/etc. already prepended to
                            // curTxMode) -- those genuinely special cases are already covered
                            // by the other checks here independent of cqPaused.
                            deferEligible = finalSignoffCall == null && uploadResult == null && !deletedAllCalls
                                && !newBand && !newMode && !newPskReporter && !newTxFirst && !promptsChanged
                                && tuneResult == null && !replyFromInProg && !tuning
                                && consecNoDecodes < maxNoDecodes && Math.Abs(timeOffset) <= maxTimeOffset
                                && autoFreqPauseMode == autoFreqPauseModes.DISABLED;
                            int qcw = callQueue.Count;
                            if ((cqPaused && txMode == TxModes.CALL_CQ) || (!transmitting && txMode == TxModes.LISTEN && qcw > 0)) modePrompt = true;
                            DateTime dt = DateTime.Now.ToUniversalTime();
                            TimeSpan sinceMidnight = dt - new DateTime(dt.Year, dt.Month, dt.Day, 0, 0, 0);
                            DebugOutput($"{nl}{Time()} ShowStatus, txEnabled:{txEnabled} cqPaused:{cqPaused} txTimeout:{txTimeout}");
                            DebugOutput($"{spacer}loggedCall:'{loggedCall}' timedOutCall:'{timedOutCall}' replyFromInProg:{replyFromInProg}");
                            DebugOutput($"{spacer}callInProg:'{callInProg}' txMode:{txMode} qcw:{qcw} transmitting:{transmitting} qsoState:{qsoState}");
                            DebugOutput($"{spacer}curTxMsg:{curTxMsg} curTxPayload:'{curTxPayload}' autoFreqPauseMode:{autoFreqPauseMode}");
                            DebugOutput($"{spacer}newSelection:{newSelection} uploadResult:'{uploadResult}' newBand:{newBand} newTxFirst:{newTxFirst} holdCheckBox:{ctrl.holdCheckBox.Checked}");
                            DebugOutput($"{spacer}modePrompt:{modePrompt} txEnableChanged:{txEnableChanged} tuneResult:{tuneResult} toCallStatus:'{toCallStatus}'");

                            string prevRxStr = "";
                            string curRxStr = "";
                            string txStr = "";
                            string curTxMode = "";
                            string prevRxPayload;
                            string curRxPayload;
                            string hold = ctrl.holdCheckBox.Checked ? ", timeout extended" : "";
                            string tMode = txMode == TxModes.LISTEN ? "Listen" : "CQ";
                            string tmStr = mode == "FT8" ? "" : $", {mode}";
                            string desc = $", {tMode} mode{tmStr}";

                            // TX1/RX1/RX2/TX2 naming matches ShowAdvancedQueue's own list headers:
                            // whichever slot is Jimmy's own Tx turn is the "TX" side, the other is
                            // the "RX" side (txFirst decides which is which).
                            string tx1Prefix = txFirst ? "TX1" : "RX1";
                            string tx2Prefix = txFirst ? "RX2" : "TX2";
                            int tx1Count = ctrl.advShowTx1 ? _tx1SnapshotRows.Count : 0;
                            int tx2Count = ctrl.advShowTx2 ? _tx2SnapshotRows.Count : 0;
                            // currentSideIsTx1: is the period that just completed the even one
                            // (tx1's bucket -- see IsEvenCall)? Deliberately NOT based on
                            // `transmitting`: in Listen mode transmitting is false for the entire
                            // session unless the operator actually keys a QSO, which pinned this to
                            // one slot forever and silently hid the other slot's count (confirmed
                            // live, 2026-08-07 -- only ever heard RX1, never TX2, despite TX2's list
                            // genuinely growing the whole time).
                            //
                            // Prefer lastDecodeEvenPeriod (the actual decode's own SinceMidnight)
                            // over the current wall clock -- confirmed live, 2026-08-07: a decode
                            // for the period that just ended can be processed a moment after the
                            // next period's clock window has already begun (WSJT-X's own
                            // decode-compute latency), and re-deriving parity from "now" at that
                            // point mislabels it as the new period's data before that period has
                            // decoded anything, producing an announcement that reads as happening
                            // partway into the next period. Only fall back to the clock before the
                            // first decode of the session has arrived at all.
                            bool currentSideIsTx1 = lastDecodeEvenPeriod ?? IsEvenPeriod((int)sinceMidnight.TotalSeconds);

                            int displayedCount = ctrl.advancedCallLayout
                                ? (currentSideIsTx1 ? tx1Count : tx2Count)
                                : (callInProg != null && callQueue.Contains(callInProg) ? qcw - 1 : qcw);
                            string callsStr = displayedCount == 1 ? "available station" : "available stations";
                            string count;
                            if (ctrl.advancedCallLayout)
                            {
                                // Naming BOTH slots' counts on the same line used to read as "it's
                                // adding them together" (fixed 2026-08-07 by adding the TX1/TX2
                                // breakdown below) -- but confirmed live, 2026-08-07, that even the
                                // breakdown still reads as hearing rx and tx info glued together on
                                // one line, since it always spoke both labels regardless of which one
                                // was actually current. Now only the slot matching what Jimmy is
                                // doing right now is spoken; the DXCC/wanted/award counts just below
                                // (via visibleCalls) are scoped to the same slot, so nothing from the
                                // other direction rides along on this announcement either.
                                count = currentSideIsTx1 ? $"{tx1Prefix} {displayedCount}" : $"{tx2Prefix} {displayedCount}";
                            }
                            else
                            {
                                count = displayedCount == 0 ? "no" : $"{displayedCount}";
                            }

                            HashSet<string> visibleCalls = null;
                            if (ctrl.advancedCallLayout)
                            {
                                visibleCalls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                var sideCalls = currentSideIsTx1 ? _tx1SnapshotCalls : _tx2SnapshotCalls;
                                bool sideEnabled = currentSideIsTx1 ? ctrl.advShowTx1 : ctrl.advShowTx2;
                                if (sideEnabled) foreach (var vc in sideCalls) visibleCalls.Add(vc);
                            }

                            int n = SnapshotPriorityCount(CallPriority.TO_MYCALL, visibleCalls);
                            EnqueueDecodeMessage dmsg = new EnqueueDecodeMessage();
                            string c = PeekVisibleCall(out dmsg, visibleCalls);
                            string pc = (c != null && (callInProg == null || timedOutCall != null || loggedCall != null)) ? $", {Spacify(c)} first" : "";
                            string pri = n > 0 ? $", {n} to you{pc}" : "";

                            n = SnapshotPriorityCount(CallPriority.NEW_COUNTRY, visibleCalls) + SnapshotPriorityCount(CallPriority.NEW_COUNTRY_ON_BAND, visibleCalls);
                            string cty = n > 0 ? $", {n} new DXCC" : "";

                            n = SnapshotPriorityCount(CallPriority.WANTED_CQ, visibleCalls);
                            string want = n > 0 ? $", {n} wanted" : "";

                            string needed = string.Concat(SnapshotNeededAwardCounts(visibleCalls)
                                .Select(kv => $", {kv.Value} {kv.Key}"));

                            // Once actively engaged with a specific station (callInProg set), the
                            // operator wants to hear the call status and RX activity, not the
                            // band-wide "N available stations, TX1/RX2 counts" summary -- that's
                            // useful while choosing who to call, not mid-exchange. Confirmed live,
                            // 2026-08-07: hearing queue/DXCC/wanted counts glued onto the same
                            // sentence as "receiving/transmitting to <call>" reads as noise once a
                            // specific QSO attempt is underway.
                            string callsWaiting = (!transmitting || qsoState == WsjtxMessage.QsoStates.CALLING) && callInProg == null
                                ? $", {count} {callsStr}{pri}{cty}{want}{needed}"
                                : "";
                            string prompt = (cmdPrompts && modePrompt) ? ((txMode == TxModes.CALL_CQ) ? $", Alt E to enable transmit" : (!transmitting && qcw > 0 ? $", Control W for list or Alt N for next" : "")) : "";

                            string curCall = callInProg;
                            //string txToCall = WsjtxMessage.ToCall(curTxMsg);
                            //if (transmitting && curTxMsg != null) curCall = curTxToCall;
 
                            string sel = newSelection ? " selected" : "";
                            string inProg = curCall != null ? $", {Spacify(curCall)}{sel}" : "";
                            curTxMode = transmitting ? "Transmitting" : "Receiving";
                            string cond = (!transmitting && txMode == TxModes.CALL_CQ) ? (!cqPaused ? ((uploadResult != null || txEnableChanged) ? ", transmit enabled" : "") : ", transmit disabled") : "";

                            if (newTxFirst) curTxMode = (txFirst ? "TX1 selected, " : "TX2 selected, ") + curTxMode;

                            if (newPskReporter)
                            {
                                string u = usePskReporter ? "Enabled" : "Disabled";
                                curTxMode = $"{u} PSKReporter spots, " + curTxMode;
                            }

                            if (newMode)
                            {
                                curTxMode = $"{mode} mode, " + curTxMode;
                            }

                            if (newBand)
                            {
                                string b = bandIdx != null ? $"{bands[(int)bandIdx]} meter" : "Unknown";
                                curTxMode = $"{b} band selected, " + curTxMode;
                            }

                            if (uploadResult != null)
                            {
                                curTxMode = $"{uploadResult}, " + curTxMode;
                            }

                            if (deletedAllCalls)
                            {
                                curTxMode = $"Deleted all waiting calls, " + curTxMode;
                            }

                            // "{call} logged" used to be woven into this sentence here too --
                            // removed 2026-08-07: RequestLog's own Notify.Publish(QsoCompletedEvent)
                            // (WSJTX_Controller/Notify/) already announces this on its own the
                            // moment it happens, and having both meant the second one could cut
                            // the first off mid-utterance (confirmed live). loggedCall itself is
                            // untouched -- it still gates the "first"/"no need to say it twice"
                            // logic just below and gets cleared at the bottom of this method
                            // exactly as before; only the redundant announcement text is gone.

                            if (finalSignoffCall != null)
                            {
                                curTxMode = $"{Spacify(finalSignoffCall)} final 73, " + curTxMode;
                            }

                            if (consecNoDecodes >= maxNoDecodes)
                            {
                                curTxMode += $", no decodes, check time, frequency, audio in";
                                consecNoDecodes = 0;
                            }

                            if (Math.Abs(timeOffset) > maxTimeOffset)
                            {
                                curTxMode += $", time offset {timeOffset:F1} seconds, check clock time ";
                            }

                            if (promptsChanged)
                            {
                                string p = cmdPrompts ? "enabled" : "disabled";
                                curTxMode = $"Command prompts {p}, " + curTxMode;
                                if (!cmdPrompts) prompt = "";
                            }

                            if (tuneResult != null)     //for 'tune stopped'
                            {
                                curTxMode = $"{tuneResult}, " + curTxMode;
                            }

                            //marker1
                            if (cqPaused)
                            {
                                if (tuning)
                                {
                                    status = tuneResult;
                                }
                                else
                                {
                                    status = $"{curTxMode}{cond}{inProg}{callsWaiting}{desc}{hold}{prompt}.";
                                    foreColor = Color.White;
                                    backColor = Color.Green;
                                }
                            }
                            else    //not paused
                            {
                                if (!transmitting)
                                {
                                    foreColor = Color.White;
                                    backColor = Color.Green;
                                }

                                if (curTxMsg != null && transmitting)
                                {
                                    if (curTxPayload == null) curTxPayload = WsjtxMessage.Payload(curTxMsg);
                                    string p = SpacifyPayload(curTxPayload);
                                    txStr = p != null ? $", sending {p}" : "";
                                }

                                prevRxPayload = null;
                                curRxPayload = null;
                                if (curCall != null)
                                {
                                    //get latest msg from deCall to myCall
                                    List<EnqueueDecodeMessage> msgList;
                                    if (allCallDict.TryGetValue(curCall, out msgList))
                                    {
                                        EnqueueDecodeMessage rmsg = msgList[msgList.Count - 1];
                                        if (!rmsg.IsCQ())
                                        {
                                            var sec = (sinceMidnight - rmsg.SinceMidnight).TotalSeconds;
                                            //DebugOutput($"{spacer}rmsg:'{rmsg.Message}' rmsg.SinceMidnight:{rmsg.SinceMidnight} TotalSeconds:{sec}");
                                            if (sec < 3.5 * (trPeriod / 1000))  //Rx period that just ended
                                            {
                                                curRxPayload = SpacifyPayload(WsjtxMessage.Payload(rmsg.Message));
                                                //DebugOutput($"{spacer}found current:{curRxPayload}");
                                                if (!rmsg.Is73orRR73() && msgList.Count >= 2)
                                                {   //Rx period previous to the one that just ended
                                                    rmsg = msgList[msgList.Count - 2];
                                                    if (!rmsg.IsCQ())
                                                    {
                                                        prevRxPayload = SpacifyPayload(WsjtxMessage.Payload(rmsg.Message));
                                                        //DebugOutput($"{spacer}found prev:{prevRxPayload}");
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                //Rx period previous to the one that just ended
                                                prevRxPayload = SpacifyPayload(WsjtxMessage.Payload(rmsg.Message));
                                                //DebugOutput($"{spacer}no current, found prev:{prevRxPayload}");
                                            }
                                            if (prevRxPayload != null && prevRxPayload == curRxPayload) prevRxPayload = null;  //no need to repeat the same results
                                        }
                                    }

                                    curRxStr = (curRxPayload == null && callInProg != null && curCall == callInProg && sentCallList.Contains(curCall)) ? (callInProgLastActivity != null ? $" {callInProgLastActivity}" : " no response") : "";
                                    if (curRxPayload != null) curRxStr = $", received {curRxPayload}";     //otherwise, no response
                                    prevRxStr = prevRxPayload != null ? $", previous {prevRxPayload}" : "";
                                    if (transmitting && (curTxPayload == "73" || curTxPayload == "RR73")) prevRxStr = "";    //don't neeed that detail any more
                                }

                                if (expiredCall != null && ((txMode == TxModes.LISTEN && !txEnabled) || txMode == TxModes.CALL_CQ))
                                {
                                    inProg = $", {Spacify(expiredCall)}";
                                    cond = " expired";
                                    curRxStr = "";
                                    prevRxStr = "";
                                    expiredCall = null;
                                }
                                else if (timedOutCall != null && ((txMode == TxModes.CALL_CQ && transmitting) || (txMode == TxModes.LISTEN && !txEnabled)))
                                {
                                    inProg = $", {Spacify(timedOutCall)}";
                                    cond = " timed out,";
                                    timedOutCall = null;
                                    if (cmdPrompts && txMode == TxModes.LISTEN) prompt = $", use Alt E to resume QSO";
                                }
                                else if (modePrompt && callInProg != null && txMode == TxModes.LISTEN && !txEnabled)
                                {
                                    if (cmdPrompts)
                                    {
                                        prompt = $", use Alt E to resume QSO";
                                    }
                                    /*else
                                    {
                                        cond = ", transmit disabled";
                                    }*/
                                }

                                if (loggedCall != null && callInProg == loggedCall) inProg = "";  //no need to say it twice

                                if (transmitting || (curRxPayload != null && curRxPayload != "")) desc = "";

                                if (tuning)
                                {
                                    status = tuneResult;
                                    foreColor = Color.Black;
                                    backColor = Color.Yellow;     //caution
                                }
                                else if (autoFreqPauseMode > autoFreqPauseModes.DISABLED)
                                {
                                    status = "Updating best transmit frequency.";
                                }
                                else if (replyFromInProg)
                                {
                                    status = $"Replying to {Spacify(callInProg)}.";          //must be short
                                }
                                else  //not a special case
                                {
                                    status = $"{curTxMode}{inProg}{cond}{curRxStr}{prevRxStr}{txStr}{callsWaiting}{desc}{hold}{prompt}.";
                                }
                            }
                            DebugOutput($"{spacer}curCall:'{curCall}' sinceMidnight:{sinceMidnight}");
                            DebugOutput($"{spacer}curTxMode:'{curTxMode}' desc:'{desc}' inProg:'{inProg}'");
                            DebugOutput($"{spacer}cond:'{cond}' curRxStr:'{curRxStr}' prevRxStr:'{prevRxStr}'");
                            DebugOutput($"{spacer}txStr:'{txStr}' callsWaiting:'{callsWaiting}' prompt:'{prompt}'");
                            DebugOutput($"{spacer}status:'{status}'");

                            loggedCall = null;
                            finalSignoffCall = null;
                            modePrompt = false;
                            newTxFirst = false;
                            newBand = false;
                            newMode = false;
                            uploadResult = null;
                            newSelection = false;
                            replyFromInProg = false;
                            deletedAllCalls = false;
                            txEnableChanged = false;
                            promptsChanged = false;
                            tuneResult = null;
                            toCallStatus = null;
                            callInProgLastActivity = null;
                            newPskReporter = false;

                            break;
                    }
                }
            }
            finally
            {
                string bandMode = (bandIdx != null && !string.IsNullOrEmpty(mode))
                    ? $"{bands[(int)bandIdx]}m {mode}" : "Status:";

                // Batch the "N available stations" announcement instead of speaking it on
                // every individual decode -- confirmed live, 2026-08-07: a couple of fast/
                // early decodes for a brand-new period could otherwise trigger a premature,
                // partial-count announcement (e.g. "3 available stations") seconds before
                // that period's real, full decode batch (e.g. 19) had actually arrived.
                //
                // Two earlier attempts both leaned on WSJT-X's own "decode pass ended"
                // signals (postDecodeTimer.Enabled, then decodesProcessed) and both failed
                // live for the same underlying reason: this real WSJT-X build reports
                // Decoding:True once at startup and never flips back to False again, so
                // neither of those ever fires a second time. ScheduleStatusAnnounce()
                // (WsjtxClient.cs) instead computes the next period boundary purely from
                // trPeriod and the wall clock -- nothing WSJT-X reports about its own decode
                // state can make this go stale. Only defer while no call is in progress -- an
                // active exchange's real-time info must never be delayed. deferEligible (set
                // at the top of the ACTIVE case) additionally requires that nothing one-off is
                // being reported this round -- a final 73, a band change, an upload result,
                // etc. must never wait on the decode batch just because callInProg happens to
                // be null when they occur.
                if (callInProg == null && deferEligible && trPeriod != null)
                {
                    _pendingStatusHeading = bandMode;
                    _pendingStatusText = status;
                    _pendingStatusForeColor = foreColor;
                    _pendingStatusBackColor = backColor;
                    ScheduleStatusAnnounce();
                }
                else
                {
                    // A fresher, immediate render always wins over a still-pending batched
                    // one -- e.g. callInProg just became non-null (operator engaged a call)
                    // after an earlier deferred summary was queued; that stale summary must
                    // never overwrite this newer, more relevant text a moment later.
                    statusAnnounceTimer.Stop();
                    _pendingStatusText = null;
                    StatusView.RenderStatus(bandMode, status, foreColor, backColor);
                }
            }
        }

        private void ShowLogged()
        {
            var logItems = new List<string>();
            var logKeys = new List<string>();
            if (logList.Count == 0)
            {
                logItems.Add("[No calls auto-logged]");
                logKeys.Add(null);
            }
            else
            {
                var rList = logList.GetRange(0, logList.Count);
                rList.Reverse();
                foreach (string call in rList)
                {
                    logItems.Add($"{Spacify(call)}, {Country(call)}");
                    logKeys.Add(call);
                }
            }

            LogView.RenderLoggedList($"Auto-logged calls: {logList.Count}", logItems, logKeys);
        }

        public void UpdateDebug()
        {
            if (!debug) return;
            string s;
            bool chg = false;

            try
            {
                ctrl.label5.ForeColor = wsjtxTxEnableButton ? Color.White : Color.Black;
                ctrl.label5.BackColor = wsjtxTxEnableButton ? Color.Red : Color.LightGray;
                ctrl.label5.Text = $"En but: {wsjtxTxEnableButton.ToString().Substring(0, 1)}";

                ctrl.label6.Text = $"dec: {period.ToString().Substring(0, 1)}";
                ctrl.label32.Text = $"pdt: {postDecodeTimer.Enabled.ToString().Substring(0, 1)}";

                ctrl.label7.ForeColor = txEnabled ? Color.White : Color.Black;
                ctrl.label7.BackColor = txEnabled ? Color.Red : Color.LightGray;
                ctrl.label7.Text = $"txEn: {txEnabled.ToString().Substring(0, 1)}";

                ctrl.label23.Text = $"t/c/p/e: {maxTxRepeat}/{maxPrevTo}/{maxPrevPotaTo}/{maxAutoGenEnqueue}";

                if (replyCmd != lastReplyCmdDebug)
                {
                    ctrl.label8.ForeColor = Color.Red;
                    ctrl.label21.ForeColor = Color.Red;
                    chg = true;
                }
                ctrl.label8.Text = $"cmd from: {WsjtxMessage.DeCall(replyCmd)}";
                lastReplyCmdDebug = replyCmd;

                ctrl.label9.Text = $"opMode: {opMode}-{WsjtxMessage.NegoState}";

                ctrl.label34.Text = $"decPr: {decodesProcessed.ToString().Substring(0, 1)}";

                string txTo = (curTxMsg == null ? "" : WsjtxMessage.ToCall(curTxMsg));
                s = (txTo == "CQ" ? null : txTo);
                ctrl.label12.Text = $"tx to: {s}";

                if (callInProg != lastCallInProgDebug)
                {
                    ctrl.label13.ForeColor = Color.Red;
                    chg = true;
                }
                ctrl.label13.Text = $"in-prog: {CallPriorityString(callInProg)}";
                lastCallInProgDebug = callInProg;

                if (qsoState != lastQsoStateDebug)
                {
                    ctrl.label14.ForeColor = Color.Red;
                    chg = true;
                }
                ctrl.label14.Text = $"qso: {qsoState}";
                lastQsoStateDebug = qsoState;

                if (evenOffset != lastEvenOffsetDebug)
                {
                    ctrl.label15.ForeColor = Color.Red;
                    chg = true;
                }
                ctrl.label15.Text = $"evn: {evenOffset}";
                lastEvenOffsetDebug = evenOffset;

                if (oddOffset != lastOddOffsetDebug)
                {
                    ctrl.label16.ForeColor = Color.Red;
                    chg = true;
                }
                ctrl.label16.Text = $"odd: {oddOffset}";
                lastOddOffsetDebug = oddOffset;

                if (txTimeout != lastTxTimeoutDebug)
                {
                    ctrl.label10.ForeColor = Color.Red;
                    chg = true;
                }
                ctrl.label10.Text = $"t/o: {txTimeout.ToString().Substring(0, 1)}";
                lastTxTimeoutDebug = txTimeout;

                if (txFirst != lastTxFirstDebug)
                {
                    ctrl.label11.ForeColor = Color.Red;
                    chg = true;
                }
                ctrl.label11.Text = $"txFirst: {txFirst.ToString().Substring(0, 1)}";
                lastTxFirstDebug = txFirst;

                if (restartQueue != lastRestartQueueDebug)
                {
                    ctrl.label24.ForeColor = Color.Red;
                    chg = true;
                }
                ctrl.label24.Text = $"rstQ: {restartQueue.ToString().Substring(0, 1)}";
                lastRestartQueueDebug = restartQueue;

                if (transmitting != lastTransmittingDebug)
                {
                    ctrl.label25.ForeColor = Color.Red;
                    chg = true;
                }
                ctrl.label25.Text = $"tx: {transmitting.ToString().Substring(0, 1)}";
                lastTransmittingDebug = transmitting;

                if (curTxMsg != lastTxMsgDebug)
                {
                    ctrl.label19.ForeColor = Color.Red;
                    chg = true;
                }
                ctrl.label19.Text = $"tx:  {curTxMsg}";
                lastTxMsgDebug = curTxMsg;

                if (lastTxMsg != lastLastTxMsgDebug)
                {
                    ctrl.label18.ForeColor = Color.Red;
                    chg = true;
                }
                ctrl.label18.Text = $"last: {lastTxMsg}";
                lastLastTxMsgDebug = lastTxMsg;

                if (lastDxCallDebug != dxCall)
                {
                    ctrl.label4.ForeColor = Color.Red;
                    chg = true;
                }
                ctrl.label4.Text = $"dxCall: {dxCall}";
                lastDxCallDebug = dxCall;

                ctrl.label21.Text = $"replyCmd: {replyCmd}";

                if (autoFreqPauseMode != lastAutoFreqPauseModeDebug)
                {
                    ctrl.label17.ForeColor = Color.Red;
                    chg = true;
                }
                ctrl.label17.Text = $"aFP: {autoFreqPauseMode}";
                lastAutoFreqPauseModeDebug = autoFreqPauseMode;

                if (consecCqCount != lastConsecCqCountDebug)
                {
                    ctrl.label26.ForeColor = Color.Red;
                    chg = true;
                }
                ctrl.label26.Text = $"cCQ: {consecCqCount}/{maxConsecCqCount}";
                lastConsecCqCountDebug = consecCqCount;

                if (consecTimeoutCount != lastConsecTimeoutCount)
                {
                    ctrl.label27.ForeColor = Color.Red;
                    chg = true;
                }
                ctrl.label27.Text = $"cTo: {consecTimeoutCount}/{maxConsecTimeoutCount}";
                lastConsecTimeoutCount = consecTimeoutCount;

                ctrl.label20.Text = $"xmitCyc : {xmitCycleCount}";

                if (consecTxCount != lastConsecTxCountDebug)
                {
                    ctrl.label1.ForeColor = Color.Red;
                    chg = true;
                }
                ctrl.label1.Text = $"cTx: {consecTxCount}/{maxConsecTxCount}";
                lastConsecTxCountDebug = consecTxCount;

                if (cqPaused != lastPausedDebug)
                {
                    ctrl.label2.ForeColor = Color.Red;
                    chg = true;
                }
                ctrl.label2.Text = $"cqPaused: {cqPaused.ToString().Substring(0, 1)}";
                lastPausedDebug = cqPaused;

                if (txMode != lastTxModeDebug)
                {
                    ctrl.label28.ForeColor = Color.Red;
                    chg = true;
                }
                string m = txMode == TxModes.LISTEN ? "Lis" : "CQ";
                ctrl.label28.Text = $"TxMode: {m}";
                lastTxModeDebug = txMode;

                ctrl.label22.Text = $"disCall: '{discardCall}'/{discardCallCycleCount}";
                ctrl.label29.Text = $"shTx: {shortTx.ToString().Substring(0, 1)}";
                ctrl.label30.Text = $"t/o call: {timedOutCall}";

                if (replyDecode == null)
                {
                    ctrl.label31.Text = $"replyDec: ---          ";
                }
                else
                {
                    ctrl.label31.Text = $"replyDec: {replyDecode.DeCall()}: {replyDecode.Priority}";
                }

                ctrl.label33.Text = (decoding ? $"decCyc: {decodeCycle}" : "decCyc:");

                if (chg)
                {
                    ctrl.debugHighlightTimer.Stop();
                    ctrl.debugHighlightTimer.Interval = 1000;
                    ctrl.debugHighlightTimer.Start();
                }
            }
            catch (Exception err)
            {
                DebugOutput($"ERROR: UpdateDebug: err:{err}");
            }
        }
    }
}
