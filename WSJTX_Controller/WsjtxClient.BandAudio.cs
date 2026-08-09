using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using WsjtxUdpLib.Messages.Out;

namespace WSJTX_Controller
{
    public partial class WsjtxClient
    {
        private void ClearAudioOffsets()
        {
            oddOffset = 0;
            evenOffset = 0;
            cachedOddOffset = 0;
            cachedEvenOffset = 0;
            period = Periods.UNK;
            DisableAutoFreqPause();
            skipFirstDecodeSeries = true;
            timeOffset = 0;
            analysisCompleted = false;
            pendingCqAfterAnalysis = false;
            _manualAnalysisRequested = false;
            DebugOutput($"{Time()} [BAND-AUDIT] ClearAudioOffsets: bandIdx:{bandIdx} skipFirstDecodeSeries:{skipFirstDecodeSeries} mode:'{mode}'");
        }

        private int? FreqToBandIdx(double? freq)            //null if unknown
        {
            if (freq == null) return null;
            if (freq >= 1.8 && freq <= 2.0) return 0;
            if (freq >= 3.5 && freq <= 4.0) return 1;
            if (freq >= 5.35 && freq <= 5.37) return 2;
            if (freq >= 7.0 && freq <= 7.3) return 3;
            if (freq >= 10.1 && freq <= 10.15) return 4;
            if (freq >= 14.0 && freq <= 14.35) return 5;
            if (freq >= 18.068 && freq <= 18.168) return 6;
            if (freq >= 21.0 && freq <= 21.45) return 7;
            if (freq >= 24.89 && freq <= 24.99) return 8;
            if (freq >= 28.0 && freq <= 29.7) return 9;
            if (freq >= 50.0 && freq <= 54.0) return 10;
            return null;
        }

        private string FreqToBandStr(double? freq)           //null if unknown
        {
            if (freq == null) return null;
            int? idx = FreqToBandIdx(freq);
            if (idx == null || (int)idx < 0 || !freqsDict.Keys.Contains(mode) || (int)idx >= freqsDict[mode].Count) return null;
            return $"{bands[(int)idx]}m";
        }

        private int? bandToFreq(int? idx)
        {
            if (idx == null || (int)idx < 0 || !freqsDict.Keys.Contains(mode) || (int)idx >= freqsDict[mode].Count) return null;
            return freqsDict[mode][(int)idx];
        }

        public bool BandUp()
        {
            if (!freqsDict.Keys.Contains(mode)) return false;
            // Prefer the last REQUESTED band over the last CONFIRMED one, so repeated presses
            // before a real CAT round-trip lands keep advancing instead of re-requesting the
            // same band each time -- see _pendingBandIdx's own comment.
            int? effectiveIdx = _pendingBandIdx ?? bandIdx;
            if (effectiveIdx == null || (int)effectiveIdx >= freqsDict[mode].Count - 1) return false;
            int targetIdx = (int)effectiveIdx + 1;
            if (bandToFreq(targetIdx) == null) return false;

            ClearAudioOffsets();
            // Deliberately not arming _requireOffsetForActive here -- 2026-07-12, found that
            // pausing to re-search the best Tx slot on every band change caused the radio to
            // double-switch. Best-slot search still happens silently in the background via
            // CalcBestOffset once decodes resume; only the foreground pause-and-wait is skipped.
            AutoFreqChanged(false, true);
            Pause(true, false);
            CancelQso();

            if (RetuneBand(targetIdx, "BandUp")) ShowBandChangePending(targetIdx);
            return true;
        }

        public bool BandDown()
        {
            if (!freqsDict.Keys.Contains(mode)) return false;
            int? effectiveIdx = _pendingBandIdx ?? bandIdx;
            if (effectiveIdx == null || (int)effectiveIdx <= 0) return false;
            int targetIdx = (int)effectiveIdx - 1;
            if (bandToFreq(targetIdx) == null) return false;

            ClearAudioOffsets();
            // See BandUp() -- not arming _requireOffsetForActive on band change (2026-07-12).
            AutoFreqChanged(false, true);
            Pause(true, false);
            CancelQso();

            if (RetuneBand(targetIdx, "BandDown")) ShowBandChangePending(targetIdx);
            return true;
        }

        public bool SelectBand(int targetIdx)
        {
            if (!freqsDict.Keys.Contains(mode)) return false;
            if (targetIdx < 0 || targetIdx >= freqsDict[mode].Count) return false;
            if (bandToFreq(targetIdx) == null) return false;
            if (bandIdx != null && (int)bandIdx == targetIdx) return false;

            ClearAudioOffsets();
            // See BandUp() -- not arming _requireOffsetForActive on band change (2026-07-12).
            AutoFreqChanged(false, true);
            Pause(true, false);
            CancelQso();

            if (RetuneBand(targetIdx, "SelectBand")) ShowBandChangePending(targetIdx);
            return true;
        }

        // Self-sufficiency plan, Phase 5: band changes retune the radio directly over rigctld --
        // the engine's own poll loop picks up the new dial on its next tick (see
        // RigctldClient.SetFrequency's own comment) exactly like a knob-QSY would. Under
        // RadioControlMode.WsjtxCat there is no separate CAT connection at all (radio state
        // comes read-only from the engine's own StatusMessage broadcasts), so there is nothing
        // to retune -- returns false so callers skip the "Changing to..." status announcement
        // instead of claiming a band change that never happens.
        private bool RetuneBand(int targetIdx, string caller)
        {
            _pendingBandIdx = targetIdx;
            uint freqHz = (uint)(bandToFreq(targetIdx) * 1000);
            DebugOutput($"{Time()} [BAND-AUDIT] {caller}: currentBandIdx:{bandIdx} targetIdx:{targetIdx} newFreq:{freqHz} txFirst:{txFirst}");
            if (ctrl.Radio.Mode != RadioControlMode.HamlibRigctld || ctrl.rigctldClient == null)
            {
                StatusView.ShowMessage("Band change needs Hamlib rigctld -- not available under WSJT-X CAT radio mode.", true);
                return false;
            }
            ctrl.rigctldClient.SetFrequency(freqHz);
            return true;
        }

        private void ShowBandChangePending(int targetIdx)
        {
            ctrl.statusText.ForeColor = Color.Black;
            ctrl.statusText.BackColor = Color.Yellow;
            ctrl.statusText.Text = $"Changing to {bands[targetIdx]} meter band...";
            ctrl.statusText.SelectionStart = 0;
        }

        // Self-sufficiency plan, Phase 1: one fresh, synchronous PollOnce() -- deliberately not
        // reusing ctrl.lastRadioStatus's background-timer value, since this is an explicit
        // on-demand "check now" action (Alt+Q), not a passive display. Needs Hamlib rigctld --
        // under RadioControlMode.WsjtxCat there is no separate CAT connection to poll at all.
        public bool ReportPowerSwr()
        {
            if (ctrl.Radio.Mode == RadioControlMode.HamlibRigctld && ctrl.rigctldClient != null)
            {
                var status = ctrl.rigctldClient.PollOnce();
                ctrl.lastRadioStatus = status;
                StatusView.ShowMessage(FormatRigctldPowerSwr(status), true);
                return true;
            }

            StatusView.ShowMessage("Power/SWR needs Hamlib rigctld -- not available under WSJT-X CAT radio mode.", true);
            return true;
        }

        private static string FormatRigctldPowerSwr(RadioStatus status)
        {
            if (!status.Ok) return $"Radio: {status.LastError ?? "no response"}";
            var parts = new List<string>();
            if (status.SMeterDb.HasValue) parts.Add($"S-meter {status.SMeterDb.Value} dB");
            if (status.PowerRaw.HasValue) parts.Add($"power {status.PowerRaw.Value:0.00}");
            if (status.Swr.HasValue) parts.Add($"SWR {status.Swr.Value:0.0}");
            return parts.Count > 0 ? string.Join(", ", parts) : "Radio: no meter data available from rigctld.";
        }

        // No native trigger exists for this yet -- jimmy-engine-host.exe has no CLI/UDP
        // "start tuning" request of its own (tuning here otherwise only ever reflects the
        // engine's own real txMsg=="TUNE" status reports, see ProcessTxStart/ProcessTxEnd).
        public bool ToggleTuningProcess()
        {
            StatusView.ShowMessage("Tune isn't available in Jimmy Native yet.", true);
            return true;
        }

        // Self-sufficiency plan, Phase 1: the one and only F11/F12 redirect point (confirmed by
        // direct tracing -- HotkeyConfig.cs -> Controller.ProcessCmdKey -> here -> either path
        // below). Needs Hamlib rigctld to adjust the radio's own AF gain -- under
        // RadioControlMode.WsjtxCat there is no separate CAT connection to adjust at all.
        public bool AudioLevel(bool up)
        {
            if (!transmitting) return false;

            if (!tuning) StartStatusTimer2(false);

            if (ctrl.Radio.Mode == RadioControlMode.HamlibRigctld && ctrl.rigctldClient != null)
            {
                if (ctrl.rigctldClient.AdjustAudioLevel(up, out double newLevel))
                    StatusView.ShowMessage($"Audio level {newLevel * 100:0}%", true);
                else
                    StatusView.ShowMessage($"Audio level: {ctrl.rigctldClient.LastError}", true);
            }
            else
                StatusView.ShowMessage("Audio level needs Hamlib rigctld -- not available under WSJT-X CAT radio mode.", true);
            return true;
        }

        private bool CalcBestOffset(List<int> offsetList, Periods decodePeriod, bool clearList)
        {
            DebugOutput($"{Time()} CalcBestOffset, decodePeriod:{decodePeriod} clearList:{clearList} offsetList.Count:{offsetList.Count()} skipFirstDecodeSeries:{skipFirstDecodeSeries}");

            // "Use best Tx frequency" governs the UNPROMPTED background version of this
            // feature -- nothing actually enforced that until now, so this ran on every
            // session/band regardless of the checkbox. But an explicit on-demand request
            // (Analyze Transmit Slot hotkey, or the "run recommended analysis now?" prompt
            // before calling CQ) must still work even with the checkbox off -- that's a
            // one-time lookup, not the background auto-apply-to-everything mode. See
            // StartSlotAnalysis/_manualAnalysisRequested. AudioOffsetFromMsg/
            // AudioOffsetFromTxPeriod still gate their own *use* of oddOffset/evenOffset on
            // the checkbox regardless, so a manually-requested result is informational only
            // unless the checkbox is also on.
            if (!ctrl.freqCheckBox.Checked && !_manualAnalysisRequested) return false;

            if (period == Periods.UNK)
            {
                oddOffset = 0;
                evenOffset = 0;
                offsetList.Clear();
                timeOffset = 0;
                return false;
            }

            int bestOffset = 0;
            int maxInterval = 0;

            //set limits
            offsetList.Add(offsetLoLimit);
            offsetList.Add(offsetHiLimit);

            offsetList.Sort();
            int[] offsets = offsetList.ToArray();

            for (int i = 0; i < offsets.Length - 1; i++)
            {
                if (offsets[i + 1] - offsets[i] > maxInterval)
                {
                    maxInterval = offsets[i + 1] - offsets[i];
                    bestOffset = (offsets[i + 1] + offsets[i]) / 2;
                }
            }

            if (decodePeriod == Periods.EVEN)
            {
                evenOffset = bestOffset;
                if (bestOffset > 0) cachedEvenOffset = bestOffset;
            }
            else
            {
                oddOffset = bestOffset;
                if (bestOffset > 0) cachedOddOffset = bestOffset;
            }

            if (clearList) offsetList.Clear();

            DebugOutput($"{spacer}evenOffset:{evenOffset} oddOffset:{oddOffset}");

            bool bothKnown = oddOffset > 0 && evenOffset > 0;
            // Announce here, not at any individual call site -- CalcBestOffset is called from
            // three places (the pre-negotiation decode-end path, the normal post-negotiation
            // decode-end path, and DecodesCompleted's own end-of-cycle path), and the decode-end
            // paths run BEFORE DecodesCompleted on every cycle. Found 2026-07-11: with the
            // announcement only in DecodesCompleted, one of the decode-end call sites always won
            // the race to flip analysisCompleted first (silently, no message), so by the time
            // DecodesCompleted checked "was this already completed", it always had been --
            // the announcement was permanently unreachable in practice, not just occasionally.
            if (bothKnown && !analysisCompleted)
            {
                analysisCompleted = true;
                _manualAnalysisRequested = false;
                _slotAnalysisWatchdog?.Stop();
                StatusView.ShowMessage(
                    $"Transmit slot analysis complete. Even period: {evenOffset} Hz, odd period: {oddOffset} Hz.",
                    true);
                if (pendingCqAfterAnalysis)
                {
                    pendingCqAfterAnalysis = false;
                    ctrl.cqModeButton_Click(null, null);
                }
            }
            return bothKnown;
        }

        private UInt32 AudioOffsetFromMsg(EnqueueDecodeMessage msg)        //msg is a reply msg, so tx msg will be opposite time period
        {
            if (msg == null || !ctrl.freqCheckBox.Checked) return 0;

            if (IsEvenCall(msg))
            {
                return (UInt32)oddOffset;
            }
            else
            {
                return (UInt32)evenOffset;
            }
        }

        private UInt32 AudioOffsetFromTxPeriod()
        {
            if ((period == Periods.UNK || !ctrl.freqCheckBox.Checked))
                return 0;

            if (txFirst)
            {
                return (UInt32)evenOffset;
            }
            else
            {
                return (UInt32)oddOffset;
            }
        }

        private int CalcTimerAdj()
        {
            return (mode == "FT8" ? 150 /*300*/ : (mode == "FT4" ? 150 /*300*/ : (mode == "FST4" ? 750 : 300)));      //msec
        }

        private void UpdateBandComboBox()
        {
            int idx = ctrl.bandComboBox.SelectedIndex;
            ctrl.bandComboBox.Items.Clear();
            if (opMode == OpModes.ACTIVE)
            {
                string b = FreqToBandStr(dialFrequency / 1e6);
                if (b == null) b = "this band";
                ctrl.bandComboBox.Items.AddRange(new string[] { "for 1 band", $"for {b}" });
            }
            else
            {
                ctrl.bandComboBox.Items.AddRange(new string[] { "for 1 band", "this band" });
            }
            ctrl.bandComboBox.SelectedIndex = idx;
        }

        private void CalcAvgTimeOffset(bool clear)
        {
            timeOffset = 0;

            if (timeOffsets.Count == 0) return;

            foreach (double offset in timeOffsets)
            {
                timeOffset += offset;
            }
            timeOffset /= timeOffsets.Count;

            DebugOutput($"{Time()} CalcAvgTimeOffset, timeOffset:{timeOffset:F2} clear:{clear}");
            if (clear) timeOffsets.Clear();
        }
    }
}
