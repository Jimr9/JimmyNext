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

            // Nexus modernization Stage 6: grid / directed-CQ target for this row come from
            // EffectiveSemantic (Nexus's parse when the cutover is on, WsjtxMessage otherwise).
            // Stage 5 proved Grid and CqTarget byte-identical between the two across the corpus.
            var sem = d.EffectiveSemantic(myCall);
            string g = sem.Grid;
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

            // "age" row field (opt-in via the Row Order editor; not in the default row):
            // whole operating periods since this station was last heard in a qualifying decode,
            // from the one authoritative EnqueueDecodeMessage.LastHeardUtc. "Now" = heard this
            // period (or last-heard not yet known).
            string age = ", " + AgeFieldText(PeriodsSinceLastHeard(d));

            string to = sem.CqTarget;
            string dirTo = (to == null ? "" : $" {to}");
            string callp = $"{Spacify(call)}";
            string pri = (d.Priority == (int)CallPriority.TO_MYCALL) ? " replying" : (d.Priority == (int)CallPriority.WANTED_CQ ? dirTo : "");

            string rankStr = debug ? $", {d.Rank}" : "";
            string descr = debug ? $", {Reason(d)}" : "";
            string tagRaw = _awardTagger.CategoryTag(d);
            string tagStr = tagRaw.Length > 0 ? $", {tagRaw}" : "";

            // Station's transmit audio offset (the "hertz they're on") -- opt-in via the Row
            // Order editor, not in the default row, so existing rows are unchanged.
            string freq = d.DeltaFrequency > 0 ? $", {d.DeltaFrequency} Hz" : "";

            string fallback = $"{callp}{pri}{tagStr}{grid}{snr}{country}{distAz}{oe}{descr}{rankStr}";
            var fieldMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "callp", callp }, { "pri", pri }, { "tag", tagStr }, { "grid", grid }, { "snr", snr },
                { "freq", freq }, { "country", country }, { "distAz", distAz }, { "age", age }, { "oe", oe },
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

                // Raw Decodes side-labeling fix, 2026-08-24 (item 1, independent audit finding,
                // CONFIRMED via code reading): this used to hardcode "TX1" for the even period and
                // "TX2" for the odd period regardless of txFirst -- correct only when txFirst is
                // true (Jimmy transmits on the even/TX1 side). With RX First configured
                // (txFirst=false), Jimmy transmits on the ODD side, so the even period is actually
                // the RECEIVE side -- every raw decode heard there was mislabeled "TX1" (implying
                // it was Jimmy's own transmit slot) instead of "RX1". Matches the SAME (band,mode)
                // TX1/RX1/RX2/TX2 convention already used everywhere else in this file (e.g.
                // ShowAdvancedQueue's own tx1Prefix/tx2Prefix just above, and ShowStatus's own
                // "txFirst decides which is which" comment) -- Raw Decodes was the one place that
                // convention was never applied.
                bool evenCall = IsEvenCall(d);
                string side = evenCall ? (txFirst ? "TX1" : "RX1") : (txFirst ? "RX2" : "TX2");

                string tag = "";
                if (rawPriorityTags && d.Category != CallCategory.DEFAULT)
                {
                    string catTag;
                    if (d.Category == CallCategory.WANTED_CQ)
                        catTag = d.EffectiveSemantic(myCall).CqTarget ?? "Dir CQ";   // Stage 6
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

                // Decode's audio offset -- opt-in via the Row Order editor.
                string freq = d.DeltaFrequency > 0 ? $", {d.DeltaFrequency} Hz" : "";

                string g = d.EffectiveSemantic(myCall).Grid;   // Stage 6
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
                    { "snr", snr }, { "freq", freq }, { "grid", grid }, { "country", country }, { "distAz", distAz },
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
                bool isDirAlert        = d.IsCQ() && IsDirectedAlert(d.EffectiveSemantic(myCall).CqTarget, classification.IsDx);   // Stage 6
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
                var semR = d.EffectiveSemantic(myCall);   // Stage 6: report / roger-report facts
                isReport   = semR.IsReport || semR.IsRReport;
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

        // The configurable routine-status clause texts produced by THIS ShowStatus render, by
        // type -- consumed in the finally block to split the composed status line into
        // per-clause SpeechCoordinator fragments (each with its own condition/timing). Cleared
        // at the top of every ShowStatus call.
        private readonly Dictionary<NotificationEventType, string> _clauseTextsThisRender
            = new Dictionary<NotificationEventType, string>();

        // A routine-status wording clause (ReceiveCycleSummary / QsoStarted / QsoCompleted /
        // TxMessageChanged / ReceivedReply / NoDecodeWarning): returns the policy's Template
        // formatted with `tokens` when the row is Enabled, or null when it is disabled (caller
        // supplies its own fallback). These types are never Publish()ed -- their Template
        // governs the wording of a clause ShowStatus composes into the ONE routine status
        // utterance, and their per-row condition/timing drive the coordinator.
        private string RoutineClause(NotificationEventType type, params (string Key, string Value)[] tokens)
        {
            var policies = ctrl?.Notifications?.Policies;
            if (policies == null || !policies.TryGetValue(type, out var policy) || !policy.Enabled)
                return null;
            var dict = new Dictionary<string, string>();
            foreach (var t in tokens) dict[t.Key] = t.Value ?? "";
            dict[NotificationVariableRegistry.TimeKey] = DateTime.Now.ToString("h:mm tt");
            string text = NotificationTemplateEngine.Format(policy.Template, dict);
            _clauseTextsThisRender[type] = text ?? "";
            return text;
        }

        // True when a routine-status wording row is Enabled (used to decide whether to include
        // an optional clause at all).
        private bool RoutineClauseEnabled(NotificationEventType type)
        {
            var policies = ctrl?.Notifications?.Policies;
            return policies != null && policies.TryGetValue(type, out var policy) && policy.Enabled;
        }

        // Does a receive-side role scope permit the routine clause for the slot whose receive
        // period just ended? currentSideIsRxRole is derived from txFirst every render, so
        // RxSideOnly / TxSideOnly track the slots automatically when the roles flip.
        private static bool ReceiveSideScopeAllows(ReceiveSideScope scope, bool currentSideIsRxRole)
        {
            switch (scope)
            {
                case ReceiveSideScope.Both:       return true;
                case ReceiveSideScope.RxSideOnly: return currentSideIsRxRole;
                case ReceiveSideScope.TxSideOnly: return !currentSideIsRxRole;
                default:                          return false;   // Neither
            }
        }

        // One-shot transition clauses survive a render that simply omits them (so a far delivery
        // boundary still gets them); "current state" clauses do not.
        private static bool IsStickyClause(NotificationEventType t) =>
            t == NotificationEventType.QsoStarted || t == NotificationEventType.QsoCompleted
            || t == NotificationEventType.TxMessageChanged;

        // Join the enabled clauses of the routine idle receive line into ONE sentence. Empty
        // (disabled) clauses contribute nothing; the survivors are comma-joined and the whole
        // gets its single trailing period. All-empty -> "" (the operator has turned every
        // routine receive clause off: nothing spoken, nothing on the status line).
        private static string JoinRoutineClauses(params string[] parts)
        {
            var sb = new StringBuilder();
            foreach (var p in parts)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(p);
            }
            if (sb.Length == 0) return "";
            char last = sb[sb.Length - 1];
            if (last != '.' && last != '!' && last != '?') sb.Append('.');
            return sb.ToString();
        }

        // Live JAWS finding, 2026-09-06: during an active QSO with nothing new to report this
        // render (no fresh received/previous decode, no other-party detail, no transmit-message
        // event, no CQ enable/disable note, ...), the active-QSO status assemblies below still
        // spoke/recorded the bare "name the active station" fragment alone -- "WA4VLC." -- every
        // time it changed from the prior render's text (typically right after a Tx period ends
        // and the line reverts from "WA4VLC, sending EN34." back to just the name). A lone
        // callsign is not a meaningful utterance.
        //
        // `inProg` starts as exactly that bare fragment (`standaloneInProg`). Keep it wherever it
        // has been REASSIGNED to real wording of its own (the expired/timed-out branches give it
        // a different string entirely), or wherever anything else on the same line turns it into
        // a real phrase (a received/previous decode, other-party detail, a transmit-message
        // event, a CQ-enable/disable note, a mode descriptor, a prompt). Only "still bare AND
        // nothing else present" drops to "". The raw callsign itself (callInProg/curCall) is
        // never touched by this -- QsoStarted / ReceivedReply / TxMessageChanged / QsoCompleted
        // all still compose their own real utterances from it independently of this fragment.
        internal static string DropBareCallsignFragment(string inProg, string standaloneInProg,
            params string[] otherParts)
        {
            if (inProg != standaloneInProg) return inProg;
            foreach (var p in otherParts)
                if (!string.IsNullOrEmpty(p)) return inProg;
            return "";
        }

        // Tidy a composed status line for display/speech AFTER one or more configurable clauses
        // were left out (disabled). A no-op for the default all-clauses-enabled wording, so it
        // never changes an existing status string -- it only removes the leading separator, the
        // ", ," seam, and the dangling ", ." a missing clause can leave behind.
        private static string NormalizeStatusLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            s = s.TrimStart(' ', ',', ';');
            while (s.Contains(", ,")) s = s.Replace(", ,", ",");
            while (s.Contains(", .")) s = s.Replace(", .", ".");
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            // The non-idle status assemblies below append their own trailing "." unconditionally.
            // When every routine clause that would sit in front of it is disabled or scoped out,
            // that leaves a lone "." (or ",", ", .", stray spaces) -- separators with no words.
            // That is not a status message: collapse it to empty so the visible line clears and
            // BuildRoutineFragments/SpeechCoordinator compose nothing to speak.
            if (!HasSpeakableContent(s)) return "";
            return s;
        }

        // True when a composed status / speech string carries at least one letter or digit --
        // i.e. real words, not just separators or sentence punctuation left behind when every
        // configurable routine clause on a render is turned off.
        internal static bool HasSpeakableContent(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s) if (char.IsLetterOrDigit(c)) return true;
            return false;
        }

        // Split the composed visible status line into ordered SpeechCoordinator fragments: the
        // "_base" skeleton (global routine timing/condition) plus one fragment per configurable
        // clause that appears in this render (its own timing/condition). Concatenating the
        // fragments by Order reconstructs `status` exactly. When every present clause shares the
        // base (timing, condition) -- the shipped default -- a single "_base" fragment is
        // returned and nothing about today's one-utterance behaviour changes.
        private IReadOnlyList<RoutineFragment> BuildRoutineFragments(
            string status, SpeakWhen baseWhen, SpeakCondition baseCondition)
        {
            var one = new[]
            {
                new RoutineFragment
                {
                    Key = "_base", Order = 0, Text = status ?? "",
                    When = baseWhen, Condition = baseCondition, Sticky = false,
                },
            };
            if (string.IsNullOrEmpty(status) || _clauseTextsThisRender.Count == 0) return one;

            var policies = ctrl?.Notifications?.Policies;
            var present = new List<(NotificationEventType type, string text, int pos, SpeakWhen when, SpeakCondition cond, bool sticky)>();
            foreach (var kv in _clauseTextsThisRender)
            {
                string t = kv.Value;
                if (string.IsNullOrEmpty(t)) continue;
                int pos = status.IndexOf(t, StringComparison.Ordinal);
                if (pos < 0) continue;   // clause text not literally in the line -- fold into base
                SpeakWhen w = baseWhen;
                SpeakCondition c = baseCondition;
                if (policies != null && policies.TryGetValue(kv.Key, out var p))
                {
                    // Codex #3: a clause that the operator has RE-timed / RE-conditioned uses
                    // its OWN configured boundary directly -- no numeric Math.Max of two
                    // SpeakWhen values (they are distinct real boundaries, NOT a chronological
                    // scale: TxStart is not "later" than AfterRx). A clause still at this type's
                    // code default INHERITS the "_base" skeleton's boundary/condition instead,
                    // so the shipped configuration always collapses to a single "_base"
                    // fragment and one utterance regardless of which branch composed the line
                    // (idle promotes _base to AfterRx; the active-QSO line rides the global
                    // routineStatusSpeakWhen). Only an actual customization splits a clause out.
                    NotificationPolicy d = NotificationDefaults.Policies.TryGetValue(kv.Key, out var dd) ? dd : null;
                    if (d == null || p.SpeakWhen != d.SpeakWhen) w = p.SpeakWhen;
                    if (d == null || p.Condition != d.Condition) c = p.Condition;
                }
                present.Add((kv.Key, t, pos, w, c, IsStickyClause(kv.Key)));
            }
            if (present.Count == 0) return one;

            bool anyDifferent = false;
            foreach (var x in present)
                if (x.when != baseWhen || x.cond != baseCondition) { anyDifferent = true; break; }
            if (!anyDifferent) return one;

            present.Sort((a, b) => a.pos.CompareTo(b.pos));
            var frags = new List<RoutineFragment>();
            int cursor = 0, order = 0, baseN = 0;
            foreach (var x in present)
            {
                int at = status.IndexOf(x.text, cursor, StringComparison.Ordinal);
                if (at < 0) continue;   // overlapping text already consumed -- fold into base
                if (at > cursor)
                    frags.Add(new RoutineFragment
                    {
                        Key = "_base." + baseN++, Order = order++, Text = status.Substring(cursor, at - cursor),
                        When = baseWhen, Condition = baseCondition, Sticky = false,
                    });
                frags.Add(new RoutineFragment
                {
                    Key = x.type.ToString(), Order = order++, Text = x.text,
                    When = x.when, Condition = x.cond, Sticky = x.sticky,
                });
                cursor = at + x.text.Length;
            }
            if (cursor < status.Length)
                frags.Add(new RoutineFragment
                {
                    Key = "_base." + baseN, Order = order, Text = status.Substring(cursor),
                    When = baseWhen, Condition = baseCondition, Sticky = false,
                });
            return frags;
        }

        private void ShowStatus()
        {
            _clauseTextsThisRender.Clear();
            string status = "";
            // Set true by a render whose whole point is a persistent condition that the
            // dedicated edge-triggered notifications already speak (currently: CAT link down
            // while idle). The line is still shown on screen and recorded in history -- it just
            // does not nudge the screen reader, so one failure never produces two utterances.
            bool suppressRoutineSpeechThisRender = false;
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
                    // "Waiting for WSJT-X" removed 2026-08-12: obsolete wording from before
                    // Direct engine mode existed -- this is the very first status render of
                    // every session (NegoState always starts at WAIT, set unconditionally in
                    // ResetNego() at construction, regardless of transport), well before
                    // ConnectDirectEngine's first successful poll flips it to RECD, so it fires
                    // under Direct mode too, not just classic UDP. k already carries the exact
                    // existing Prompt Mode (cmdPrompts, Alt+P) wording used everywhere else in
                    // this method -- reused as-is rather than a new hardcoded string.
                    status = $"{pgmName} {pgmVer}{k}.";
                    foreColor = Color.Black;
                    backColor = Color.Orange;
                    return;
                }

                // NegoState is now always RECD by the time control reaches here: the WAIT
                // branch above returns for the pre-connect window, and INITIAL/SENT/FAIL are
                // classic-UDP-handshake states the Direct engine transport never enters
                // (ConnectDirectEngine/DirectPollTick only ever set RECD or WAIT). The old
                // NegoState==INITIAL "Jimmy Next vX. Connecting." and NegoState==FAIL
                // (failReason) branches were removed with the UDP-vestige cleanup pass.
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
                                status = $"{newSel}Connecting, wait until ready{k}.";
                            }
                            foreColor = Color.Black;
                            backColor = Color.Orange;
                            newBand = false;
                            return;
                        case (int)OpModes.IDLE:
                            status = modeSupported ? $"Connecting, wait until ready{k}." : "operating mode not supported";
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
                            // Final-QSO notification ordering fix (part 2), 2026-08-24 --
                            // independent audit finding, CONFIRMED live (K4XN, real QSO): the
                            // log SOUND (LogQso's own PlaySoundEvent, fully independent of
                            // ShowStatus) always fires the instant LogQso runs -- but the
                            // CORRESPONDING SPOKEN "{call} logged, Transmitting, sending 73" text
                            // (the part item 5's earlier fix built) could still be silently
                            // deferred right here, because loggedCall was missing from this
                            // exclusion list even though finalSignoffCall (its sibling "a final
                            // 73" case this method's own comment calls out by name) was already
                            // here. A deferred render's one-shot flags (loggedCall included) still
                            // get consumed/reset in the block below regardless of whether that
                            // specific render is ever actually delivered -- if a LATER, unrelated
                            // immediate render (e.g. transmitting itself flipping true) arrives
                            // before the deferred one's own timer fires, "a fresher render always
                            // wins" (this method's own render-vs-defer comment) silently drops the
                            // deferred one for good. Confirmed exactly this shape in the real log:
                            // the combined "K4XN logged, Transmitting, sending 73" text WAS built
                            // correctly but never announced; 12 seconds later a plain "Transmitting,
                            // sending 73" (loggedCall already consumed) is the only thing that was.
                            deferEligible = finalSignoffCall == null && loggedCall == null && uploadResult == null && !deletedAllCalls
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
                            DebugOutput($"{spacer}callInProg:'{callInProg}' txMode:{txMode} qcw:{qcw} transmitting:{transmitting}");
                            // Label is "lastTxMsg" (the curTxMsg field is only ever overwritten
                            // with a REAL transmitted message -- see WsjtxClient.Direct.cs -- so
                            // after an interrupted contact this is the LAST message sent, not a
                            // current or pending one; reading it as "still sending this" is a
                            // diagnostic-clarity trap, 2026-08-27). Field name unchanged.
                            DebugOutput($"{spacer}lastTxMsg:{curTxMsg} curTxPayload:'{curTxPayload}' autoFreqPauseMode:{autoFreqPauseMode}");
                            DebugOutput($"{spacer}newSelection:{newSelection} uploadResult:'{uploadResult}' newBand:{newBand} newTxFirst:{newTxFirst}");
                            DebugOutput($"{spacer}modePrompt:{modePrompt} txEnableChanged:{txEnableChanged} tuneResult:{tuneResult} toCallStatus:'{toCallStatus}'");

                            string prevRxStr = "";
                            string curRxStr = "";
                            string otherStr = "";
                            string txStr = "";
                            string curTxMode = "";
                            string prevRxPayload;
                            string curRxPayload;
                            string tMode = txMode == TxModes.LISTEN ? "Listen" : "CQ";
                            string tmStr = mode == "FT8" ? "" : $", {mode}";
                            // The operating-mode descriptor is now its own configurable routine
                            // clause. modePhrase is the CLEAN phrase ("Listen mode" / "CQ mode, FT4"),
                            // "" when the row is disabled; desc keeps the historical leading ", "
                            // form every downstream status assembly already expects. Default
                            // template "{Mode} mode{SubMode}" reproduces the old wording exactly.
                            string modePhrase = RoutineClause(NotificationEventType.OperatingModeSummary,
                                ("Mode", tMode), ("SubMode", tmStr)) ?? "";
                            string desc = modePhrase == "" ? "" : $", {modePhrase}";

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

                            // Added 2026-08-10: the very first render right after Tx ends is
                            // exactly when ShowAdvancedQueue's own Tx-suppression (WsjtxClient.
                            // Display.cs: suppressTx1/suppressTx2, keyed off `transmitting`) just
                            // lifted -- root-caused live from a real QSO where the "Receiving..."
                            // announcement right after Tx ended said "TX2 0 available stations"
                            // even though the overall queue genuinely had 14 calls in it at that
                            // exact moment. Not fake data, just read at the worst possible
                            // instant, before that side's own count has settled. See its use just
                            // below, on the callsWaiting clause specifically.
                            bool justStoppedTransmitting = _wasTransmittingLastShowStatus && !transmitting;

                            int displayedCount = ctrl.advancedCallLayout
                                ? (currentSideIsTx1 ? tx1Count : tx2Count)
                                : (callInProg != null && callQueue.Contains(callInProg) ? qcw - 1 : qcw);
                            string callsStr = displayedCount == 1 ? "available station" : "available stations";
                            // Split 2026-09-05: the advanced-layout side name ("RX1"/"TX2") is its
                            // OWN routine clause (ReceiveSideId) now, no longer welded onto the
                            // count -- so it can be worded and role-scoped independently. The
                            // count text itself is just the number (or "no" in the simple layout,
                            // which has no side concept). Only the slot matching what Jimmy is
                            // doing right now is described; the DXCC/wanted/award counts just below
                            // (via visibleCalls) are scoped to the same slot, so nothing from the
                            // other direction rides along on this announcement either.
                            string sideName = "";
                            string countText;
                            if (ctrl.advancedCallLayout)
                            {
                                sideName = currentSideIsTx1 ? tx1Prefix : tx2Prefix;
                                countText = displayedCount.ToString();
                            }
                            else
                            {
                                countText = displayedCount == 0 ? "no" : $"{displayedCount}";
                            }

                            // Which CURRENT role does the slot whose period just ended hold? tx1 is
                            // the RX role when Jimmy transmits on tx2 (!txFirst), and vice-versa --
                            // re-derived from txFirst every render, so the RxSideOnly / TxSideOnly
                            // scopes below follow the slots automatically when the roles flip, with
                            // no settings change. Scopes are advanced-layout only; the simple
                            // layout has one list and always keeps its count.
                            bool currentSideIsRxRole = currentSideIsTx1 ? !txFirst : txFirst;
                            bool sideIdScopeOk = ctrl.advancedCallLayout && ReceiveSideScopeAllows(
                                ctrl.Notifications?.ReceiveSideIdScope ?? ReceiveSideScope.Both, currentSideIsRxRole);
                            bool countScopeOk = !ctrl.advancedCallLayout || ReceiveSideScopeAllows(
                                ctrl.Notifications?.ReceiveCountScope ?? ReceiveSideScope.Both, currentSideIsRxRole);

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
                            int newDxccCount = n;
                            string cty = n > 0 ? $", {n} new DXCC" : "";

                            n = SnapshotPriorityCount(CallPriority.WANTED_CQ, visibleCalls);
                            int wantedCount = n;
                            string want = n > 0 ? $", {n} wanted" : "";

                            var neededAwardCounts = SnapshotNeededAwardCounts(visibleCalls);
                            int neededAwardKindCount = neededAwardCounts.Count();
                            string needed = string.Concat(neededAwardCounts
                                .Select(kv => $", {kv.Value} {kv.Key}"));

                            // Once actively engaged with a specific station (callInProg set), the
                            // operator wants to hear the call status and RX activity, not the
                            // band-wide "N available stations, TX1/RX2 counts" summary -- that's
                            // useful while choosing who to call, not mid-exchange. Confirmed live,
                            // 2026-08-07: hearing queue/DXCC/wanted counts glued onto the same
                            // sentence as "receiving/transmitting to <call>" reads as noise once a
                            // specific QSO attempt is underway.
                            // justStoppedTransmitting && displayedCount == 0: skip the count
                            // clause entirely this one render rather than announcing a stale
                            // "0 available stations" -- the NEXT natural status update (once the
                            // just-unsuppressed side's list has had a moment to reflect the real
                            // queue) will include the real count normally.
                            // Item 2, 2026-08-24 (operator request, opt-in/default off): normally
                            // this clause CAN still appear while transmitting (e.g. calling CQ) --
                            // the one piece of ShowStatus's own text that's genuinely receive-side
                            // chatter, not TX-critical info (sending/logged/expired/timed-out text
                            // below is never gated by this). With the new setting on, transmitting
                            // alone suppresses it outright, so transmit-related speech isn't
                            // competing with a "N available stations" summary for the same utterance.
                            // The receive-side summary is now TWO independently role-scoped routine
                            // clauses -- the side name (ReceiveSideId) and the count phrase
                            // (ReceiveCycleSummary) -- built ONCE here and reused by every status
                            // assembly below, so RoutineClause() is called at most once per type
                            // per render (its text is registered for the fragment splitter). The
                            // existing whole-summary gating (transmit-time suppression,
                            // justStoppedTransmitting, callInProg) is unchanged and still wraps
                            // both. sideIdClause/countClause are "" when disabled, scoped out, or
                            // not applicable (simple layout has no side name).
                            bool receiveSummaryAllowed = (!transmitting || !ctrl.suppressReceiveNotificationsDuringTx)
                                && callInProg == null
                                && !(justStoppedTransmitting && displayedCount == 0);

                            string sideIdClause = (receiveSummaryAllowed && sideIdScopeOk && sideName != "")
                                ? (RoutineClause(NotificationEventType.ReceiveSideId, ("Side", sideName)) ?? "")
                                : "";

                            string countClause = (receiveSummaryAllowed && countScopeOk
                                    && RoutineClauseEnabled(NotificationEventType.ReceiveCycleSummary))
                                ? (RoutineClause(NotificationEventType.ReceiveCycleSummary,
                                       ("AvailableCount", countText),
                                       ("Stations", callsStr),
                                       ("ToYou", pri),
                                       ("NewDxcc", cty),
                                       ("Wanted", want),
                                       ("Awards", needed),
                                       ("NewDxccCount", newDxccCount.ToString()),
                                       ("WantedCount", wantedCount.ToString()),
                                       ("AwardCount", neededAwardKindCount.ToString()),
                                       ("Band", bandIdx != null ? $"{bands[(int)bandIdx]}m" : "")) ?? "")
                                : "";

                            // Comma-prefixed form the non-idle status assemblies (cqPaused, and
                            // the "not a special case" branch) splice in directly, matching
                            // JoinRoutineClauses' separators; NormalizeStatusLine tidies any seam a
                            // scoped-out clause leaves. The idle branch composes the two via
                            // JoinRoutineClauses instead.
                            string callsWaiting = (sideIdClause != "" ? ", " + sideIdClause : "")
                                                + (countClause != "" ? ", " + countClause : "");
                            // T2 fix, 2026-08-23 (CONFIRMED bug -- KJ5OUL log evidence, 2026-08-21):
                            // "Control W for list or Alt N for next" is Beginner-mode-only
                            // guidance -- Ctrl+W is a Beginner Available Stations shortcut not
                            // even assigned in Advanced Call Layout, and Advanced mode has its
                            // own separate TX1/TX2 list navigation entirely. This clause used to
                            // fire regardless of layout; confirmed live emitted while genuinely in
                            // Advanced mode. Alt E to enable transmit is left ungated -- that's a
                            // real TX-enable action available in both layouts, not Beginner list
                            // navigation.
                            string prompt = (cmdPrompts && modePrompt) ? ((txMode == TxModes.CALL_CQ) ? $", Alt E to enable transmit" : (!ctrl.advancedCallLayout && !transmitting && qcw > 0 ? $", Control W for list or Alt N for next" : "")) : "";

                            string curCall = callInProg;
                            //string txToCall = WsjtxMessage.ToCall(curTxMsg);
                            //if (transmitting && curTxMsg != null) curCall = curTxToCall;
 
                            string sel = newSelection ? " selected" : "";
                            // The plain "name the active station" fragment. Captured on its own so
                            // the other-party block further down -- which builds its OWN
                            // "<call> to <other>, <what>" text that already opens with this exact
                            // callsign -- can drop this one instead of ShowStatus concatenating
                            // "{inProg}...{otherStr}" and saying the call twice ("K9RRW, K9RRW to
                            // N6S, ..."). Structural de-dup at the fragment that owns the callsign,
                            // not StartsWith/trim surgery on the assembled line. A later branch
                            // (expired / timed out) legitimately REASSIGNS inProg to a different
                            // call with different wording -- the "== standaloneInProg" guard in the
                            // other-party block leaves those alone.
                            string standaloneInProg = curCall != null ? $", {Spacify(curCall)}{sel}" : "";
                            string inProg = standaloneInProg;
                            // Final-QSO notification ordering fix, 2026-08-24 (operator finding --
                            // "Logged" heard while transmitting the final 73, then "Sending 73"
                            // heard afterward, as two separate utterances): loggedCall != null here
                            // means LogQso just fired (DirectApplyStatus's Is73orRR73(curTxMsg)
                            // branch) on THIS SAME poll tick, and that branch's own trigger already
                            // guarantees curTxMsg IS the final 73/RR73 text -- but the engine can
                            // report qso.TxNow as that final text before `transmitting` itself
                            // flips true for the period, so this render can land before the
                            // "Transmitting" render does. Describing it as "Receiving" here (the
                            // literal current flag) while ALSO about to say "logged" reads as
                            // contradictory once the txStr fix just below adds "sending 73" to the
                            // same sentence -- treat this one-shot moment as the transmitting side
                            // too, matching what's actually about to go out.
                            // The state verb is now its own configurable routine clause.
                            // Default template "{State}" reproduces the bare word; "" when the
                            // row is disabled (then the one-shot prefixes below still prepend
                            // their own transition text -- those are gated by their own flags,
                            // not this row). The idle branch reuses curTxMode directly as the
                            // state clause; the other branches embed it as {curTxMode}.
                            string stateVerb = (transmitting || loggedCall != null) ? "Transmitting" : "Receiving";
                            curTxMode = RoutineClause(NotificationEventType.ReceiveStateSummary, ("State", stateVerb)) ?? "";
                            // CAT-down routine status, reworked 2026-09-02 (2.0.59): when the
                            // engine reports the rig's CAT link is KNOWN DOWN (_lastCatOk ==
                            // false -- this partial class's live latch, WsjtxClient.Direct.cs)
                            // and Jimmy is just sitting in receive/idle (not transmitting, not
                            // tuning, nothing just logged), the operator's radio is off as far
                            // as they can tell. The routine status must not imply normal
                            // reception: no "decoding continues", no available-station counts,
                            // no priority / wanted / award counts, no receive-derived current /
                            // previous-message text, no action prompts. Just a short, persistent
                            // line, assembled at the two status-composition points below. A
                            // genuine engine-reported Transmitting or Tune state is NOT masked --
                            // those paths are excluded here and keep their own wording. Status
                            // composition ONLY: CAT/PTT fail-closed, the call queue, retune,
                            // reconnect, and the dedicated edge-triggered RadioCatLost
                            // notification are all untouched.
                            bool catDownIdle = _lastCatOk == false && !transmitting && !tuning && loggedCall == null;
                            string cond = (!transmitting && txMode == TxModes.CALL_CQ) ? (!cqPaused ? ((uploadResult != null || txEnableChanged) ? ", transmit enabled" : "") : ", transmit disabled") : "";

                            // Live-testing finding, 2026-08-21: this used to fire regardless of
                            // Advanced Call Layout -- but "TX1"/"TX2" is a side-labeling concept
                            // that only exists in advanced mode's own split TX1/TX2 lists (see
                            // UpdateCallListAccessibleName's own comment, WsjtxClient.cs). A
                            // beginner-mode operator has one unified list and never sees a
                            // TX1/TX2 split anywhere else in the UI, so announcing "TX1 selected"
                            // when the Tx-first side flips (e.g. Alt+F) was meaningless to them,
                            // not just extra detail.
                            if (newTxFirst && ctrl.advancedCallLayout)
                                curTxMode = (txFirst ? "TX1 selected, " : "TX2 selected, ") + curTxMode;

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

                            // Restored 2026-08-10 (removed 2026-08-07, see the git history for
                            // that removal's own reasoning): "{call} logged" woven directly into
                            // this sentence, same prefix pattern as finalSignoffCall/uploadResult
                            // just below/above. The 2026-08-07 removal kept RequestLog's own
                            // separate Notify.Publish(QsoCompletedEvent) announcement instead,
                            // reasoning the two competed and the second cut the first off --
                            // confirmed live, 2026-08-10 (same bug class, W4MAA/K7F/WB3JSZ
                            // sessions), that keeping the STANDALONE one was the wrong half to
                            // keep: RequestLog no longer publishes QsoCompletedEvent (removed) --
                            // this woven-in text is now the ONLY place "logged" gets said, so
                            // there is nothing left for it to compete with.
                            if (loggedCall != null)
                            {
                                // "QSO logged" routine-status wording clause -- a clean phrase
                                // ("K4YT logged"); ShowStatus adds the ", " separator when
                                // weaving it into the visible line. RoutineClause returns null
                                // ONLY when the row is disabled -- then the clause is absent
                                // (no fallback text). NotificationTemplateEngine.Format never
                                // throws / never returns null for a non-null template, so a
                                // "formatting failure" fallback is not a real case here.
                                string loggedClause = RoutineClause(NotificationEventType.QsoCompleted,
                                    ("Callsign", Spacify(loggedCall)),
                                    ("Band", bandIdx != null ? $"{bands[(int)bandIdx]}m" : ""),
                                    ("Mode", mode ?? ""));
                                if (!string.IsNullOrEmpty(loggedClause)) curTxMode = loggedClause + ", " + curTxMode;
                            }

                            if (finalSignoffCall != null)
                            {
                                curTxMode = $"{Spacify(finalSignoffCall)} final 73, " + curTxMode;
                            }

                            if (consecNoDecodes >= maxNoDecodes)
                            {
                                // "No decodes warning" clause -- clean phrase; ShowStatus adds
                                // the ", " separator. Disabled -> absent.
                                string ndClause = RoutineClause(NotificationEventType.NoDecodeWarning,
                                    ("Mode", mode ?? ""));
                                if (!string.IsNullOrEmpty(ndClause)) curTxMode += ", " + ndClause;
                                consecNoDecodes = 0;
                            }

                            // Clock-drift wording removed from the routine line 2026-09-04: the
                            // ClockOutOfSync / ClockSynced notifications (transition-gated,
                            // Important) own the spoken "computer clock is out of sync" message
                            // now, so one bad clock never produces two competing utterances. The
                            // notifications are on by default and, being Important, are also
                            // eligible for the off-focus alert.

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
                                else if (catDownIdle)
                                {
                                    status = "Radio CAT link lost, CQ, transmit disabled.";
                                    foreColor = Color.White;
                                    backColor = Color.Green;
                                    // Visible + history only -- see the other catDownIdle branch.
                                    suppressRoutineSpeechThisRender = true;
                                }
                                else
                                {
                                    inProg = DropBareCallsignFragment(inProg, standaloneInProg,
                                        curTxMode, cond, callsWaiting, desc, prompt);
                                    status = $"{curTxMode}{cond}{inProg}{callsWaiting}{desc}{prompt}.";
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

                                // Final-QSO notification ordering fix, 2026-08-24 -- see the
                                // curTxMode assignment above for the full root-cause writeup.
                                // loggedCall != null merges "sending {payload}" into this SAME
                                // render instead of waiting for a later one, so the operator hears
                                // one coherent "{call} logged, Transmitting, sending 73." instead
                                // of "Logged" and "Sending 73" as two separate utterances. No
                                // change to LogQso's own trigger/timing -- this only widens when
                                // the ALREADY-known curTxMsg gets described in the status text.
                                if (curTxMsg != null && (transmitting || loggedCall != null))
                                {
                                    if (curTxPayload == null) curTxPayload = WsjtxMessage.Payload(curTxMsg);
                                    string p = SpacifyPayload(curTxPayload);
                                    // "Transmit message" clause -- clean phrase ("sending 73");
                                    // ShowStatus adds the ", " separator. Disabled -> absent.
                                    string txClause = p != null
                                        ? RoutineClause(NotificationEventType.TxMessageChanged,
                                               ("Message", p),
                                               ("Callsign", curCall ?? WsjtxMessage.ToCall(curTxMsg) ?? ""),
                                               ("Band", bandIdx != null ? $"{bands[(int)bandIdx]}m" : ""),
                                               ("Mode", mode ?? ""))
                                        : null;
                                    txStr = string.IsNullOrEmpty(txClause) ? "" : ", " + txClause;
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

                                    // "Received reply detail" clause. The state detection above
                                    // stays put; only the final wording is configurable. The
                                    // parts are built as CLEAN phrases (no leading separators)
                                    // and combined into {Received} -- the default template just
                                    // speaks {Received}. ShowStatus adds the leading ", " when it
                                    // weaves the result into the visible line. Enabled=false ->
                                    // the clause (and its visible text) is absent.
                                    string recClean = "";
                                    if (curRxPayload != null)
                                        recClean = $"received {curRxPayload}";
                                    else if (callInProg != null && curCall == callInProg && !transmitting
                                             && curTxMsg != null
                                             && string.Equals(WsjtxMessage.ToCall(curTxMsg), callInProg, StringComparison.OrdinalIgnoreCase)
                                             // Premature "no response" fix (KR4NO / K4JC live-radio
                                             // audit, 2026-09-08): not at the transmit-ended edge --
                                             // only once the following receive opportunity has
                                             // actually completed with its decodes processed and the
                                             // radio was genuinely receiving during it. See
                                             // NoResponseOpportunityComplete / _directNoResponseAwaitingCall.
                                             && NoResponseOpportunityComplete(callInProg))
                                        // In a QSO, our last over went to this station, receive
                                        // period, nothing heard back -> a plain constant "no
                                        // response". (The old sentCallList gate here has been
                                        // dead since the UDP ProcessTxEnd removal -- nothing
                                        // populates that list in Direct mode -- so an unanswered
                                        // calling phase used to render a wordless line and, since
                                        // 2.0.66, leave the stale "Sending <grid>" from the
                                        // previous TX frozen on screen.)
                                        //
                                        // 2.0.71: was `callInProgLastActivity ?? "no response"`,
                                        // but callInProgLastActivity holds a "working <other>"
                                        // decode heard BEFORE we started calling (e.g. TG9SO was
                                        // heard working YV0DX) and is not cleared as it ages, so
                                        // the receive line flipped between "no response" and a
                                        // minutes-stale "working YV0DX" every period. A fixed
                                        // "no response" is honest and stable, and still gives the
                                        // render words so the TX line no longer freezes.
                                        recClean = "no response";
                                    string prevClean = prevRxPayload != null ? $"previous {prevRxPayload}" : "";
                                    if (transmitting && (curTxPayload == "73" || curTxPayload == "RR73")) prevClean = "";    //don't need that detail any more
                                    string receivedPhrase = recClean;
                                    if (prevClean != "")
                                        receivedPhrase = receivedPhrase == "" ? prevClean : receivedPhrase + ", " + prevClean;

                                    curRxStr = "";
                                    prevRxStr = "";
                                    if (receivedPhrase != "")
                                    {
                                        string rrClause = RoutineClause(NotificationEventType.ReceivedReply,
                                            ("Received", receivedPhrase), ("Previous", prevClean),
                                            ("Callsign", curCall != null ? Spacify(curCall) : ""));
                                        if (!string.IsNullOrEmpty(rrClause)) curRxStr = ", " + rrClause;
                                    }
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

                                if (transmitting || (curRxPayload != null && curRxPayload != "")) { desc = ""; modePhrase = ""; }

                                // See ProcessDecodeMsg's own comment (WsjtxClient.cs) for why this
                                // exists: callInProg working someone else used to be silently
                                // discarded before ShowStatus ever saw it. Only said while curCall
                                // == callInProg is actually set (mid-attempt) -- once logged/reset
                                // this clears along with everything else in SetCallInProg.
                                // "HB9GWX to W2AAS, R R 7 3" -- the station being worked, who
                                // it's working, and the literal message it just sent them (see
                                // otherPartyStage's own field comment). Either half can be
                                // missing: an unresolved <...> other-call leaves only the
                                // message, a 2-word short reply leaves only the name.
                                // 5N0YEN live-radio audit (2026-09-08): weave the "callInProg to
                                // <peer>, <payload>" fragment in ONLY while that decode is still
                                // current -- within ~1.5 T/R periods. Older than that, the target
                                // has not been heard working anyone for a full listen cycle, so a
                                // stale peer fact must not keep riding every render (it was
                                // gluing "5N0YEN to R6TA, 73" onto every "no response" line for
                                // the whole 20-call effort). A fresh decode from the target
                                // re-stamps otherPartyForCallInProgUtc; a target CQ / turn-to-us
                                // clears it outright (ProcessDecodeMsg).
                                double otherFreshMs = 1.5 * (trPeriod ?? 15000);
                                bool otherPartyFresh = otherPartyForCallInProgUtc != default
                                    && (DateTime.UtcNow - otherPartyForCallInProgUtc).TotalMilliseconds <= otherFreshMs;
                                if (curCall != null && otherPartyFresh && (otherPartyForCallInProg != null || otherPartyStage != null))
                                {
                                    string otherWhat = otherPartyStage != null ? SpacifyPayload(otherPartyStage) : "";
                                    // Open with the active call AND its " selected" marker so
                                    // dropping the standalone inProg fragment below loses neither.
                                    string activeHead = $", {Spacify(curCall)}{sel}";
                                    if (otherPartyForCallInProg != null && otherWhat != "")
                                        otherStr = $"{activeHead} to {Spacify(otherPartyForCallInProg)}, {otherWhat}";
                                    else if (otherPartyForCallInProg != null)
                                        otherStr = $"{activeHead} to {Spacify(otherPartyForCallInProg)}";
                                    else
                                        otherStr = $"{activeHead}, {otherWhat}";
                                    // otherStr now names the active station (root cause of the
                                    // duplicate: "{inProg}" = ", K9RRW" AND "{otherStr}" = ", K9RRW
                                    // to N6S, -20" were both emitted). Drop the standalone fragment,
                                    // but only when it's still the plain active-call text -- an
                                    // expired/timed-out reassignment above carries its own wording.
                                    if (inProg == standaloneInProg) inProg = "";
                                }

                                // See DropBareCallsignFragment's own comment: harmless for the
                                // tuning/autoFreq/replyFromInProg/catDownIdle/idle branches just
                                // below (none of them reference `inProg` in their own status
                                // text) -- this only matters to the final "not a special case"
                                // composition, which does.
                                inProg = DropBareCallsignFragment(inProg, standaloneInProg,
                                    curTxMode, cond, curRxStr, prevRxStr, otherStr, txStr,
                                    callsWaiting, desc, prompt);

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
                                else if (replyFromInProg && RoutineClauseEnabled(NotificationEventType.QsoStarted))
                                {
                                    // "QSO started" clause -- shown once, when Jimmy decides to
                                    // reply to callInProg (folded into ONE short status so it is
                                    // never a second utterance racing the progress line -- the
                                    // 2026-08-10 W4MAA double-announcement fix). Default template
                                    // "Working {Callsign}, replying."; the operator's edited
                                    // wording is used as-is.
                                    //
                                    // DISABLED (Codex #2 fix): this branch is skipped entirely,
                                    // so there is NO hard-coded "Replying to ..." fallback --
                                    // disabling the row genuinely removes the QSO-start clause.
                                    // The render then falls through to the normal progress line
                                    // below, which still shows the station being worked.
                                    status = RoutineClause(NotificationEventType.QsoStarted,
                                                 ("Callsign", Spacify(callInProg)),
                                                 ("Band", bandIdx != null ? $"{bands[(int)bandIdx]}m" : ""),
                                                 ("Mode", mode ?? "")) ?? "";
                                }
                                else if (catDownIdle)
                                {
                                    // CAT known down, plain receive/idle: concise persistent
                                    // status only -- none of the receive-derived clauses
                                    // (curRxStr/prevRxStr/otherStr/callsWaiting/desc/prompt) or
                                    // the "decoding continues" wording. The tuning / autoFreq /
                                    // replyFromInProg branches above still win, so a genuine
                                    // operating intent is never hidden by this.
                                    string catMode = (txMode == TxModes.CALL_CQ)
                                        ? (txEnabled ? "CQ mode" : "CQ, transmit disabled")
                                        : "Listen mode";
                                    status = $"Radio CAT link lost, {catMode}.";
                                    // Shown on screen + recorded in history every render, but not
                                    // spoken: the edge-triggered RadioCatLost / RadioCatRecovered
                                    // notifications own the spoken CAT transitions, so one CAT
                                    // failure never produces two utterances.
                                    suppressRoutineSpeechThisRender = true;
                                }
                                else if (callInProg == null && deferEligible)
                                {
                                    // The routine idle receive line -- up to three independently
                                    // toggleable/retimeable clauses (state verb, "N available
                                    // stations" counts, operating-mode descriptor), composed
                                    // into ONE utterance. Each is "" when its row is disabled;
                                    // JoinRoutineClauses drops the empty ones and supplies the
                                    // single trailing period. With every row enabled (the
                                    // default) this rebuilds today's exact line
                                    // ("Receiving, no available stations, Listen mode.").
                                    // Disabling all three yields "" -> nothing spoken, nothing
                                    // on the status line. The beginner action hint is structural
                                    // and rides last, before the period, matching the old order.
                                    string stateClause = curTxMode;   // already the state clause (or "")
                                    string modeClause = modePhrase;   // already the mode clause (or "")
                                    // sideIdClause / countClause were built (scoped, enabled-
                                    // checked, and registered with the fragment splitter) above.
                                    // Compose all four in reading order; each "" one drops out.
                                    // With every row enabled and Both scopes -- the shipped
                                    // default -- this rebuilds "Receiving, RX1, N available
                                    // stations, Listen mode." (simple layout: no side clause).
                                    status = JoinRoutineClauses(stateClause, sideIdClause, countClause, modeClause);
                                    if (status.Length > 0 && prompt.Length > 0)
                                        status = status.Substring(0, status.Length - 1) + prompt + ".";
                                }
                                else  //not a special case
                                {
                                    status = $"{curTxMode}{inProg}{cond}{curRxStr}{prevRxStr}{otherStr}{txStr}{callsWaiting}{desc}{prompt}.";
                                }
                            }
                            DebugOutput($"{spacer}curCall:'{curCall}' sinceMidnight:{sinceMidnight}");
                            DebugOutput($"{spacer}curTxMode:'{curTxMode}' desc:'{desc}' inProg:'{inProg}'");
                            DebugOutput($"{spacer}cond:'{cond}' curRxStr:'{curRxStr}' prevRxStr:'{prevRxStr}' otherStr:'{otherStr}'");
                            DebugOutput($"{spacer}txStr:'{txStr}' callsWaiting:'{callsWaiting}' prompt:'{prompt}'");
                            DebugOutput($"{spacer}status:'{status}'");

                            _wasTransmittingLastShowStatus = transmitting;
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

                // Tidy any seam a disabled routine clause left behind (leading ", ", ", ,",
                // ", ."). A no-op for the default all-clauses-enabled wording, so existing
                // status strings -- and the replay-suite assertions on them -- are unchanged.
                status = NormalizeStatusLine(status);

                // VISIBLE status + Notification History: ALWAYS immediate, every call. Never
                // gated on whether/when the line is spoken (Item 2). Returns whether Jimmy is
                // really foregrounded right now.
                bool foregroundNow = StatusView.RenderStatusVisible(bandMode, status, foreColor, backColor);

                // SPEECH goes through the one SpeechCoordinator as an ordered set of clause
                // fragments (see BuildRoutineFragments). The "_base" skeleton uses the global
                // routine timing/condition; each configurable clause (Receive cycle summary, QSO
                // started, QSO logged, Transmit message, Received reply, No-decode warning) can
                // carry its own. When every present clause shares the base timing/condition -- the
                // shipped default -- exactly one fragment is emitted and the line speaks as one
                // utterance, unchanged. Where an operator has given a clause a different boundary,
                // the coordinator composes only the fragments eligible at each boundary, so
                // same-boundary clauses still coalesce into one orderly announcement.
                //
                // baseWhen (the "_base" skeleton's boundary): the operator's global
                // routineStatusSpeakWhen, EXCEPT for the pure idle "N available stations"
                // render, which always rides the receive-cycle boundary (AfterRx) -- that is
                // the long-standing cadence, and it is where the state / counts / mode clauses
                // that make up that line default to. A render carrying anything one-off is not
                // deferEligible, so it keeps the global boundary. NO Math.Max of SpeakWhen
                // values -- Codex #3: distinct real boundaries, not a chronological scale. A
                // clause the operator has RE-timed away from its default still composes at its
                // own boundary (see BuildRoutineFragments); an untouched clause inherits this.
                SpeakWhen baseWhen =
                    (callInProg == null && deferEligible && trPeriod != null)
                        ? SpeakWhen.AfterRx : ctrl.routineStatusSpeakWhen;

                var fragments = BuildRoutineFragments(status, baseWhen, ctrl.routineStatusCondition);

                // suppressRoutineSpeechThisRender: a render that only re-states a persistent
                // condition the edge-triggered notifications already speak (CAT link down while
                // idle). The visible line + history above are unaffected; only the screen-reader
                // nudge is withheld this render, so one CAT failure never yields two utterances.
                Notify?.Speech.SubmitRoutineComposite(fragments, foregroundNow,
                    allowSpeech: !suppressRoutineSpeechThisRender);
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
                    // Callsign, then country (or US state when that option is on), then the
                    // sent/received reports for this QSO so the row is a self-contained record
                    // of what was logged. The reports are a display-only snapshot captured at
                    // log time (_loggedReports); the row's key stays the bare callsign.
                    string line = $"{Spacify(call)}, {Country(call)}";
                    if (_loggedReports.TryGetValue(call, out string rpt) && !string.IsNullOrEmpty(rpt))
                        line += $", {rpt}";
                    logItems.Add(line);
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
                // label32 used to show postDecodeTimer.Enabled -- that timer was removed
                // 2026-08-18 along with the rest of the dead UDP decode-cycle machinery it
                // belonged to (see WsjtxClient.cs's own comment at DecodesCompleted's removal).

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
