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
            int i = (int)idx;

            // Options > Frequencies: the first (lowest) entry for the current mode in this
            // band's list is its primary/canonical frequency -- empty (operator never
            // customized this band) falls through to the built-in default below. This is the
            // single chokepoint BandUp/BandDown/SelectBand/RetuneBand/initial-connect all
            // resolve a band index to an actual frequency through, so this is sufficient
            // everywhere without touching any of those call sites.
            foreach (var entry in ctrl.Frequencies.Bands[i])
                if (entry.Mode == mode) return entry.FreqKHz;

            return freqsDict[mode][i];
        }

        // T18 fix, 2026-08-23: bandToFreq's own sideband counterpart -- same chokepoint/lookup
        // convention (first entry matching the current mode is this band's primary entry), so
        // BandUp/BandDown/SelectBand resolve the CONFIGURED sideband for whatever they retune to
        // instead of RetuneBand hardcoding "USB" for every band regardless of what Options >
        // Frequencies actually has configured. "USB" (FrequencyEntry's own default) for a band
        // never customized -- preserves prior behavior exactly.
        private string bandToSideband(int? idx)
        {
            if (idx == null || (int)idx < 0 || (int)idx >= ctrl.Frequencies.Bands.Length) return "USB";
            foreach (var entry in ctrl.Frequencies.Bands[(int)idx])
                if (entry.Mode == mode) return entry.Sideband;
            return "USB";
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

            RetuneBand(targetIdx, "BandUp");
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

            RetuneBand(targetIdx, "BandDown");
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

            RetuneBand(targetIdx, "SelectBand");
            return true;
        }

        // Options > Frequencies per-entry hotkey: jump straight to one specific frequency row
        // (not necessarily a band's primary/first entry the way SelectBand's bandToFreq lookup
        // always resolves to) -- switches mode first if the entry's mode differs from the
        // current one, matching Alt+M's own SetOperatingMode path.
        public bool SelectFrequency(int targetIdx, string entryMode, int freqKHz, string sideband = "USB")
        {
            if (targetIdx < 0 || targetIdx >= bands.Count) return false;
            if (freqKHz <= 0) return false;

            if (entryMode != mode) SetOperatingMode(entryMode);

            ClearAudioOffsets();
            AutoFreqChanged(false, true);
            Pause(true, false);
            CancelQso();

            RetuneBand(targetIdx, (uint)(freqKHz * 1000), "SelectFrequency", sideband, $"{freqKHz} kHz");
            return true;
        }

        // The actual Controller.ProcessCmdKey entry point for an Options > Frequencies
        // per-entry hotkey (2026-08-18, root-caused live): a hotkey now means "go to this
        // BAND", never "switch mode out from under the operator" -- one hotkey works for both
        // FT8 and FT4, always landing on whichever this band's entry matches the CURRENT mode
        // (SelectBand's own bandToFreq lookup, already correct, previously unreferenced by any
        // hotkey). Only when the pressed hotkey's OWN entry already matches the current mode
        // does this behave exactly like SelectFrequency's original targeted jump -- preserving
        // multiple same-mode entries per band (e.g. an alternate spot frequency) as genuine
        // direct-jump extras, still reachable by their own hotkey, still able to differ from
        // that band's primary/first entry.
        //
        // Root cause this exists for: Options > Frequencies auto-creates one FT8 and one FT4
        // row per band, sorted ascending by frequency. 40m is the ONLY band where FT4's
        // built-in calling frequency (7047) is LOWER than FT8's (7074), so it is the one band
        // where the FT4 row lists FIRST -- every other band correctly lists FT8 first. An
        // operator assigning "one hotkey per band" down the list, reasonably assuming "first
        // row = FT8" (true for the other 10 bands), landed their 40m hotkey on the FT4 row by
        // exactly this quirk. Pressing it while on FT8 then did what SelectFrequency's old
        // unconditional behavior always did: silently switched tier to FT4 (SetOperatingMode ->
        // DirectSetTier -> the ENGINE's own tier-switch retune) AND separately sent Jimmy's own
        // explicit frequency command for the same target -- two genuine CAT frequency writes,
        // from two different connections, for one keypress. Confirmed live, 2026-08-18, via a
        // Hamlib -vvvv trace capture cross-referenced with the operator's own hotkey audit.
        public bool SelectFrequencyHotkey(int targetIdx, FrequencyEntry entry)
        {
            if (entry.Mode == mode) return SelectFrequency(targetIdx, entry.Mode, entry.FreqKHz, entry.Sideband);
            return SelectBand(targetIdx);
        }

        // Band changes retune the radio through the engine's own Engine::set_frequency (the
        // SET_FREQUENCY Direct command), not a second, uncoordinated rigctld write -- see
        // DirectSetFrequency's own comment (WsjtxClient.Direct.cs). Under RadioControlMode.
        // WsjtxCat there is no separate CAT connection at all (radio state comes read-only from
        // the engine's own StatusMessage broadcasts), so there is nothing to retune.
        // T18 fix, 2026-08-23: resolves the target band's own configured sideband (bandToSideband)
        // instead of RetuneBand's core overload hardcoding "USB" for every caller -- BandUp/
        // BandDown/SelectBand (the only callers of THIS overload) go through here.
        private void RetuneBand(int targetIdx, string caller, string detail = null) =>
            RetuneBand(targetIdx, (uint)(bandToFreq(targetIdx) * 1000), caller, bandToSideband(targetIdx), detail);

        // SelectFrequency's own entry point: an explicit target frequency, not necessarily the
        // band's primary/first entry bandToFreq(targetIdx) would resolve to.
        //
        // Found live (Codex release audit, 2026-08-19): this used to set _pendingBandIdx, call
        // SetFrequency, discard its bool result, and return true unconditionally -- so a rejected
        // command, a dropped connection, or a timeout (SetFrequency returns false for all three;
        // see its own comment) was reported to the operator as a successful band change anyway,
        // and _pendingBandIdx was left pointing at a band the radio was never actually retuned
        // to. Since BandUp/BandDown prefer _pendingBandIdx over the last CONFIRMED bandIdx (by
        // design, so repeated presses before a CAT round-trip lands keep advancing), a stale
        // pending value from a failed retune would make the NEXT BandUp/BandDown compute from a
        // band that only existed as an unconfirmed request -- the same class of bug
        // _pendingBandIdx's confirmation-path fix (2026-08-17) already closed for the SUCCESS
        // side; this is that fix's failure-path twin. _pendingBandIdx is now only set once we're
        // actually attempting a CAT command (not on the earlier "wrong radio mode" rejection,
        // which never attempts one at all), and is cleared back to null on failure so the next
        // BandUp/BandDown falls back to bandIdx -- the last state the radio actually confirmed.
        //
        // Release-audit finding, 2026-08-21 (completes the 2026-08-20 partial fix): the actual
        // DirectSetFrequency call used to run synchronously here, on the UI thread -- every
        // caller (BandUp/BandDown/SelectBand/SelectFrequency, all direct hotkey/menu handlers)
        // could block keyboard/screen-reader/repaint responsiveness for DirectSendCommand's full
        // bounded connect/read wait (up to a few seconds) on every press, worst when the engine
        // host is starting, hung, or restarting -- exactly the class of bug DirectPollTick was
        // already fixed for (2026-08-19) and every fire-and-forget DirectSendXxx/DirectSetXxx
        // command was fixed for (2026-08-20), but this one couldn't get the same trivial
        // Task.Run-and-discard treatment because callers needed the real success/failure to
        // decide _pendingBandIdx and the "Changing to..." status text. Now void: the network
        // call runs on a background Task, and this method itself owns showing the pending-status
        // text or the failure notification once it completes (moved out of all four callers,
        // which no longer need to know or care whether the retune succeeded synchronously).
        // _pendingBandIdx is still set HERE, synchronously, before returning -- preserving the
        // exact "repeated presses before a CAT round-trip lands keep advancing" behavior the
        // comment above describes, since that only depends on the field being updated
        // immediately, not on the network round-trip finishing. The `_pendingBandIdx != targetIdx`
        // guard in the continuation below is this method's own reconnect-epoch equivalent: if a
        // NEWER BandUp/BandDown/SelectBand/SelectFrequency press already superseded this one
        // while it was still in flight, that newer request now owns _pendingBandIdx and the
        // pending-status text -- this now-stale completion must not clobber either.
        // T18 fix, 2026-08-23: `sideband` defaults "USB" only for the rare direct caller that
        // doesn't have a real FrequencyEntry to resolve one from (there are none left in this
        // file after this pass -- every live caller now passes a real resolved value via
        // bandToSideband/entry.Sideband) -- kept as a default rather than required so a future
        // caller can't accidentally omit it and silently regress to always-USB without at least
        // reading this comment.
        private void RetuneBand(int targetIdx, uint freqHz, string caller, string sideband = "USB", string detail = null)
        {
            DebugOutput($"{Time()} [BAND-AUDIT] {caller}: currentBandIdx:{bandIdx} targetIdx:{targetIdx} newFreq:{freqHz} txFirst:{txFirst} sideband:{sideband}");
            if (ctrl.Radio.Mode != RadioControlMode.HamlibRigctld)
            {
                StatusView.ShowMessage("Band change needs Hamlib rigctld -- not available under WSJT-X CAT radio mode.", true);
                return;
            }

            _pendingBandIdx = targetIdx;
            string bandLabel = $"{bands[targetIdx]}m";
            // Codex Audit 02 follow-up, 2026-08-21: DirectSetFrequency now routes through the
            // ordered dispatcher (WsjtxClient.Direct.cs's own class comment) instead of this method
            // opening its own independent Task.Run -- the dispatcher already marshals onComplete
            // onto the UI thread, same as ctrl.BeginInvoke did here before.
            // T18 fix, 2026-08-23: sideband, not a hardcoded "USB" literal -- see this method's
            // own signature comment and FrequencyEntry.Sideband's own comment.
            DirectSetFrequency(freqHz, bandLabel, sideband, ok =>
            {
                if (_pendingBandIdx != targetIdx) return; // superseded by a newer request

                if (ok)
                {
                    // T17 fix, 2026-08-23: record what was actually confirmed commanded, for
                    // DirectApplyStatus's own readback-mismatch check (WsjtxClient.Direct.cs) to
                    // compare against the rig's next CAT mode readback. CAT mode command/readback
                    // correlation fix, 2026-08-23: the timestamp travels with it (both set here,
                    // together, only on a non-superseded confirmed retune -- see the guard just
                    // above) -- see _lastCommandedSidebandChangedUtc's own comment for why.
                    _lastCommandedSideband = sideband;
                    _lastCommandedSidebandChangedUtc = DateTime.UtcNow;
                    _sidebandMismatchStreak = 0;
                    ShowBandChangePending(targetIdx, detail);
                    return;
                }

                // Covers a rejected/malformed SET_FREQUENCY, an unreachable engine host, or a
                // timed-out read alike -- DirectSetFrequency returns false for all three (see
                // its own comment, and DirectSendCommand's on the bounded connect/read pair it
                // shares with every other Direct command).
                _pendingBandIdx = null;
                // Routed through NotificationCenter (2026-08-19, notification-system-
                // consistency pass) instead of a raw StatusView.ShowMessage -- same
                // "headline: reason" ErrorWarningEvent shape as Controller.cs's "Radio CAT
                // link lost". Error severity forces Important (beep + eligible for the
                // off-focus UIA announcement); the inner StatusViewNotificationDelivery still
                // updates statusText exactly as before (see NotificationCenter.Deliver/
                // UiaAlertNotificationDelivery).
                Notify?.Publish(new ErrorWarningEvent(ErrorSeverity.Error, $"Band change to {bandLabel} failed",
                    "the engine host rejected or did not confirm the frequency change"));
            });
        }

        private void ShowBandChangePending(int targetIdx, string detail = null)
        {
            ctrl.statusText.ForeColor = Color.Black;
            ctrl.statusText.BackColor = Color.Yellow;
            ctrl.statusText.Text = $"Changing to {detail ?? $"{bands[targetIdx]} meter band"}...";
            ctrl.statusText.SelectionStart = 0;
        }

        // Self-sufficiency plan, Phase 1: one fresh, on-demand engine SNAPSHOT query -- this is
        // an explicit "check now" action (Alt+Q), not a passive display, so it always asks the
        // engine directly rather than reusing any cached value. Works under either RadioControl
        // Mode (the engine's own Rig, and therefore its S-meter/power/SWR readings, exist
        // independently of RadioControlMode -- Jimmy no longer runs any separate CAT session).
        // Added 2026-08-10: restored the transmit/receive split Andy WM8Q's fork's own Alt+Q
        // handler had (WSJT-X mainwindow.cpp, NewTxMsgIdx==18: `if (m_transmitting) Power/SWR
        // else "Audio in: %1 dB", arg(round(m_px))`) -- somewhere along the way this collapsed
        // into always reporting the rigctld CAT poll regardless of state, so "not transmitting"
        // silently started reporting the radio's own S-meter instead of the soundcard's own
        // audio-in level (confirmed live, 2026-08-10: "alt q used to report in db the audio
        // level when not transmitting and now it is doing something else"). m_px was WSJT-X's
        // own internal measurement of the INCOMING SOUNDCARD signal -- nothing to do with the
        // radio's S-meter. Nexus's own equivalent is RadioStatus.rx_level (tempo-app/src/
        // engine.rs's MeterFeed doc comment: "the ballistics-shaped RX input level", updated
        // every 20ms by the rx-dsp thread) -- see RxLevelToDb below for the exact conversion.
        // Codex Audit 02 finding, 2026-08-21 ("remove the remaining synchronous Direct waits from
        // UI/hotkey paths ... including Alt+Q/F11/F12"): the SNAPSHOT query below used to run
        // synchronously on the UI thread via DirectSendCommand -- same class of keyboard/screen-
        // reader/repaint freeze already fixed for DirectPollTick and every DirectSendXxx/
        // DirectSetXxx command (2026-08-19/20/21). Now routes through the ordered dispatcher
        // (WsjtxClient.Direct.cs's own class comment); the rest of this method's logic is
        // unchanged, just moved into the completion callback, which already runs on the UI thread.
        public bool ReportPowerSwr()
        {
            // sound:false, not true -- this hotkey's own documented purpose includes checking
            // "during transmit" (see its help text), so it can fire mid-over same as AudioLevel()
            // above. StatusView.ShowMessage's sound:true plays a Windows SystemSounds.Beep through
            // whatever the OS DEFAULT playback device is, which on a typical ham setup IS the
            // radio's own sound-card interface -- that beep would mix straight into the live
            // transmitted audio. Root-caused live, 2026-08-09, same bug as AudioLevel()'s own fix.
            // The screen-reader announcement itself is unaffected either way (ShowMsg's own
            // SendKeys nudge is independent of sound).
            //
            // `transmitting || tuning`, not `transmitting` alone -- Andy's fork's own
            // m_transmitting was true for a tune carrier too (a real carrier really is going out
            // the antenna), but Nexus keeps these as two independent flags (tuning never sets
            // transmitting -- confirmed via engine.rs/service.rs: set_transmitting is only ever
            // called from the normal slot-TX machinery, never from the Tune branch). Checking
            // both here is what makes Alt+Q report real Power/SWR during Tune, matching the
            // original -- and matching the actual point of Tune (trim F11/F12 while watching
            // ALC/Power/SWR settle).
            // Sourced entirely from the engine's own SNAPSHOT (2026-08-20: retired the
            // RigctldClient.PollOnce fallback this used to have here -- S-meter/power/SWR now
            // come from the SAME SNAPSHOT the engine already sends every poll tick, not a
            // second, concurrent CAT session). One fresh, on-demand query either way, same as
            // the RX-audio-in path below always was -- not a cached/periodic value, matching
            // this hotkey's own "check now" purpose.
            EnqueueDirectCommand("SNAPSHOT", snapJson =>
            {
                DirectRadioStatus radio = null;
                if (snapJson != null && snapJson.Length > 0 && !snapJson.StartsWith("ERR"))
                {
                    try
                    {
                        var snap = System.Text.Json.JsonSerializer.Deserialize<DirectSnapshot>(snapJson, DirectJsonOptions);
                        radio = snap?.Radio;
                    }
                    catch (Exception ex)
                    {
                        DebugOutput($"{Time()} ReportPowerSwr: engine SNAPSHOT parse failed: {ex.Message}");
                    }
                }
                if (radio == null)
                {
                    StatusView.ShowMessage("Power/SWR: engine host unreachable.", false);
                    return;
                }

                // "Explain meter readings" (Options > Radio, RadioSettings.ExplainMeterReadings) --
                // opt-in, default off: adds one short plain-language clause per reading and, while
                // receiving, the rig's CAT S-meter in S-units. Off = exactly the terse wording
                // this hotkey always spoke. Read live off ctrl.Radio, no restart needed.
                bool explain = ctrl.Radio.ExplainMeterReadings;

                if (transmitting || tuning)
                {
                    // S-meter is deliberately NOT reported here (read-only audit, 2026-08-28):
                    // it measures an INCOMING signal, which the rig cannot do while keyed. The
                    // engine keeps its last receive value in smeter_db through the whole over
                    // (it only clears it on a CAT drop / radio switch), so anything shown during
                    // transmit is a stale, meaningless number -- the operators who reported this
                    // were right that "you can't read an S-meter while transmitting". It belongs
                    // in the receive branch below, where it is a live reading.
                    var parts = new List<string>();
                    // Release-audit finding, 2026-08-20: this used to report radio.RfPower -- the raw
                    // 0.0-1.0 Hamlib "l RFPOWER" fraction that disable_rfpower_probe (EngineHost/src/
                    // main.rs, the proven fix for the real 5W-drop hazard, Hamlib/Hamlib#1595)
                    // deliberately keeps the engine from ever querying at all, so RfPower is always
                    // null with that gate active -- Alt+Q's power figure silently never appeared
                    // during transmit/tune even on a rig with real calibrated telemetry available.
                    // radio.TxPoW (RadioStatus.tx_po_w, calibrated output watts) is a SEPARATE
                    // reading Nexus's radio loop computes independently of the suppressed RFPOWER
                    // probe -- safe to read here, never involves the hazardous query. Do not revert
                    // this to RfPower/l RFPOWER for any reason.
                    if (radio.TxPoW.HasValue) parts.Add($"power {radio.TxPoW.Value:0.#} W");
                    if (radio.TxSwr.HasValue)
                        parts.Add(explain ? $"SWR {radio.TxSwr.Value:0.0}, {SwrHint(radio.TxSwr.Value)}"
                                          : $"SWR {radio.TxSwr.Value:0.0}");
                    if (radio.TxAlc.HasValue)
                        parts.Add(explain ? $"ALC {radio.TxAlc.Value:0.00}, {AlcHint(radio.TxAlc.Value)}"
                                          : $"ALC {radio.TxAlc.Value:0.00}");
                    StatusView.ShowMessage(parts.Count > 0 ? string.Join(", ", parts) : "Radio: no meter data available.", false);
                    return;
                }

                // Not transmitting/tuning: the soundcard's own RX audio-in level (see this method's
                // own top comment for why this differs from power/SWR above).
                if (!explain)
                {
                    StatusView.ShowMessage($"Audio in: {RxLevelToDb(radio.RxLevel):0} dB", false);
                    return;
                }
                double audioInDb = RxLevelToDb(radio.RxLevel);
                string rxReport = $"Audio in {audioInDb:0} dB, {AudioInHint(audioInDb)}";
                if (radio.SmeterDb.HasValue) rxReport += $", S-meter {SmeterToSUnits(radio.SmeterDb.Value)}";
                StatusView.ShowMessage(rxReport, false);
            });
            return true;
        }

        // Mirrors Nexus's own canonical conversion exactly (nexus/ui/src/components/
        // LevelMeter.tsx's rxLevelDb), so the number matches what Nexus's own UI -- and, by
        // design, WSJT-X's own audio-in meter -- would show for the same signal: dB =
        // 20*log10(rms) + 90.3, clamped [0, 90]. A healthy FT8 input reads ~30 dB (decodes fine
        // ~15-60, too hot above ~70).
        //
        // internal (not private): JimmyTests exercises this pure conversion directly (see
        // InternalsVisibleTo in AssemblyInfo.Testing.cs) -- it's the one part of the Alt+Q fix
        // that's fully deterministic and worth a real regression test, unlike the rest of
        // ReportPowerSwr/AudioLevel/ToggleTuningProcess, which need a live engine host to
        // meaningfully exercise.
        internal static double RxLevelToDb(double rms)
        {
            if (double.IsNaN(rms) || rms <= 0) return 0;
            return Math.Max(0, Math.Min(90, 20 * Math.Log10(rms) + 90.3));
        }

        // Plain-language hints for the "Explain meter readings" option (RadioSettings.
        // ExplainMeterReadings) -- Alt+Q's optional verbose form. Pure functions so JimmyTests
        // covers the wording and thresholds directly, same as RxLevelToDb above. The thresholds
        // are deliberately conservative and rig-agnostic (no station is special-cased); a
        // screen-reader user gets one short clause, never a sentence.

        // SWR ratio, always >= 1.0. 1.0 is a perfect match; most rigs start folding back power /
        // an antenna problem is likely by about 3.
        internal static string SwrHint(double swr)
        {
            if (swr <= 1.5) return "good";
            if (swr <= 2.0) return "acceptable";
            if (swr <= 3.0) return "high";
            return "very high, check antenna";
        }

        // ALC as a 0.0-1.0 fraction of the rig's meter scale. For FT8/FT4 you want it at or near
        // zero (a clean, linear signal) -- any real ALC action means the transmit audio is too
        // hot and should come down (F11).
        internal static string AlcHint(double alc)
        {
            if (alc <= 0.05) return "clean";
            if (alc <= 0.20) return "a little high, reduce audio";
            return "high, reduce audio";
        }

        // Soundcard receive audio-in level on RxLevelToDb's own 0-90 scale -- ~15-60 decodes
        // well, above ~70 is clipping (matches RxLevelToDb's own comment).
        internal static string AudioInHint(double db)
        {
            if (db < 15) return "low";
            if (db <= 60) return "good";
            if (db <= 70) return "hot";
            return "too hot, clipping";
        }

        // CAT S-meter, reported by Hamlib as dB relative to S9 (S9 = 0 dB, ~6 dB per S-unit).
        // Spoken the way an operator reads a front panel: "S7", "S9", "S9 plus 20 dB". This is
        // the number the operators found confusing as a bare "-12 dB".
        internal static string SmeterToSUnits(int dbRelS9)
        {
            if (dbRelS9 >= 0)
                return dbRelS9 == 0 ? "S9" : $"S9 plus {dbRelS9} dB";
            int sUnit = 9 + (int)Math.Round(dbRelS9 / 6.0, MidpointRounding.AwayFromZero);
            if (sUnit < 1) sUnit = 1;
            if (sUnit > 9) sUnit = 9;
            return $"S{sUnit}";
        }

        // Added 2026-08-10: wired to the native engine's own Engine::set_tune (control port's
        // new SET_TUNING command, EngineHost/src/main.rs) -- Engine::set_tune already existed
        // (Nexus's own Tauri UI uses it) and already plays a continuous test carrier in small
        // 40ms chunks (tempo-audio/src/service.rs, TUNE_CHUNK_MS) rather than one pre-rendered
        // slot buffer, so unlike a normal FT8/FT4 transmission, F11/F12 (SET_TX_LEVEL) DOES apply
        // live during Tune -- the correct way to trim drive level down until the radio's ALC
        // reads at/near zero, matching how Andy WM8Q's fork's own hotkeys worked. Optimistically
        // flips `tuning` immediately (same pattern as the original Tilly-era ToggleTuning) rather
        // than waiting for the next 1s Direct-mode poll to reconcile it via DirectApplyStatus --
        // but ONLY on confirmed success (found live, 2026-08-10: under classic WSJT-X/UDP mode,
        // where jimmy-engine-host.exe never runs at all, DirectSendCommand can't connect and the
        // old unconditional version still claimed "Tune started" and let F11/F12 proceed as if a
        // real carrier existed -- the native engine has no equivalent to WSJT-X's own UDP-based
        // ToggleTuning, so classic mode genuinely cannot Tune at all right now).
        // Release-audit finding, 2026-08-21 (completes the 2026-08-20 partial fix): DirectSetTuning
        // used to be called synchronously here, on the UI thread, blocking Alt+T for
        // DirectSendCommand's full bounded connect/read wait whenever the engine host is slow,
        // starting, or hung -- same class of bug already fixed for RetuneBand/the fire-and-forget
        // DirectSetXxx commands. _tuningRequestInFlight guards against a rapid double-press
        // computing `newState = !tuning` from stale state while the first press's request is
        // still out (tuning only updates once THIS continuation lands) -- Tune is a deliberate,
        // infrequent action, so a second press while one is already in flight is simply ignored
        // (handled, no-op) rather than queued or raced.
        private bool _tuningRequestInFlight;

        public bool ToggleTuningProcess()
        {
            if (_tuningRequestInFlight) return true;
            bool newState = !tuning;
            // Found live, 2026-08-10, auditing against production: the original classic-UDP
            // ToggleTuning() (deleted this same session, replacing Andy WM8Q's proprietary
            // sub-command 19 with this method) always did "if (txEnabled) HaltTx();" before
            // starting a tune carrier, so a normal Tx cycle could never race against Tune. This
            // replacement dropped that -- restored here, same condition, HaltTx() itself is
            // already mode-aware (routes to DirectSendHaltTx() under Direct mode).
            if (newState && txEnabled) HaltTx();

            _tuningRequestInFlight = true;
            // Codex Audit 02 follow-up, 2026-08-21: DirectSetTuning now routes through the ordered
            // dispatcher (WsjtxClient.Direct.cs's own class comment) instead of this method opening
            // its own independent Task.Run -- the dispatcher already marshals onComplete onto the
            // UI thread, same as ctrl.BeginInvoke did here before.
            DirectSetTuning(newState, ok =>
            {
                _tuningRequestInFlight = false;
                if (!ok)
                {
                    // "Talk to engine directly" used to be a real Options toggle this message
                    // could point the operator at -- removed (found stale while auditing
                    // UDP-transport code, 2026-08-17): the native engine is always running and
                    // always the only transport in production now (see
                    // NativeEngineSettings.cs's own comment), so a failure here means the
                    // engine process itself isn't reachable, not a mode choice.
                    StatusView.ShowMessage("Tune needs the native engine, which isn't currently reachable.", true);
                    return;
                }
                tuning = newState;
                if (!tuning) StartStatusTimer2(false);
                StatusView.ShowMessage(tuning ? "Tune started" : "Tune stopped", false);
            });
            return true;
        }

        // Self-sufficiency plan, Phase 1: the one and only F11/F12 redirect point (confirmed by
        // direct tracing -- HotkeyConfig.cs -> Controller.ProcessCmdKey -> here).
        //
        // Self-sufficiency plan Phase 6 addendum, 2026-08-09: F11/F12 originally (Andy WM8Q's
        // fork) sent a proprietary UDP sub-command (NewTxMsgIdx=20, see the very first commit in
        // this repo's history) that told WSJT-X itself to adjust its own generated Tx tone
        // level -- not a CAT command to the radio, and not a Windows output-device volume
        // either. That only ever worked against Andy's actual fork; standard WSJT-X, WSJT-X
        // Improved, and the native engine all speak the standard protocol and never understood
        // it.
        //
        // The native engine's own Engine::set_tx_level/tx_level (tempo-app/src/engine.rs) is the
        // real modern match to the original intent -- it's WSJT-X's own "Pwr" slider equivalent,
        // the sound-card/software side of the signal chain (scales the generated tone before it
        // ever reaches the sound card), already wired up on the Rust side and applied live every
        // slot (control port's SET_TX_LEVEL command, EngineHost/src/main.rs).
        //
        // 2026-08-20: retired the Hamlib-rigctld CAT fallback this used to have when the engine
        // wasn't reachable (adjusting the radio's own receive AF gain -- a different point in the
        // signal chain entirely; kept from back when Jimmy could run F11/F12 against classic
        // external WSJT-X with no native engine host at all). Jimmy is Direct-only now -- the
        // engine host always exists -- so that fallback only ever fired on a genuine engine-host
        // outage mid-transmission, and adjusting the radio's RX volume was never really the right
        // substitute for that anyway. If the engine isn't reachable, this now just reports that,
        // the same way ReportPowerSwr/Alt+Q already does.
        //
        // Added 2026-08-10: this used to read/write Engine::mic_gain via SET_MIC_GAIN instead --
        // confirmed live to be wrong by tracing every consumer of mic_gain() in Nexus's own
        // tempo-audio/src/service.rs: applied in exactly one place, explicitly commented "Only
        // the Phone section drives these (the FT8 TX path is idle there)" -- it had no effect on
        // FT8/FT4 transmit audio at all, and its snapshot value (rig_mic_gain.or(mic_gain),
        // preferring a CAT-polled read-back over what was just commanded) is why repeated F11/
        // F12 presses during one transmission kept announcing the same stale value: reported
        // live, an operator pressed F11 five times mid-transmission and heard the same "Audio
        // level 55%" every time, only seeing it change on the NEXT transmission. tx_level has no
        // such CAT-read-back field muddying it, so this fix resolves both problems at once --
        // wrong signal-chain point AND stale-reading -- with the same change.

        // 2026-08-30: this used to do its own SNAPSHOT read-modify-write here (a read to get the
        // current TxLevel, then a fire-and-forget SET_TX_LEVEL) with its own _audioLevelRequestInFlight
        // guard against two overlapping presses. Both concerns now live in DirectSetEngineTxLevel
        // (WsjtxClient.Direct.cs): the current value comes from _engineTxLevel (the engine's last
        // CONFIRMED level, refreshed every poll), and the _txLevelChangeInFlight guard there is
        // SHARED with the modeless Options > Radio "FT8/FT4 transmit tone level" spinner, so F11/F12
        // and Options cannot race each other onto a stale value either. The announcement, the cache
        // and the per-band remembered value are all updated only after the engine confirms.

        public bool AudioLevel(bool up)
        {
            // "during tune or transmit" (see this hotkey's own help text, Controller.cs) --
            // matches Andy WM8Q's fork's original gate (`newTxMsgIdx == 20 && m_transmitting`
            // covered Tune too, since WSJT-X sets m_transmitting for a tune carrier as well).
            // Added the `tuning` half 2026-08-10, alongside wiring up Tune itself
            // (ToggleTuningProcess) -- Tune is the ONLY state where this actually applies live
            // (see ToggleTuningProcess's own comment); during a real FT8/FT4 transmission this
            // still only takes effect on the next slot, same as before.
            if (!transmitting && !tuning) return false;
            if (_txLevelChangeInFlight) return true;
            if (_engineTxLevel == null)
            {
                // No engine-confirmed level to step from -- engine host not reachable, or no
                // snapshot has arrived yet this session. Report honestly rather than guess.
                StatusView.ShowMessage("Audio level: engine not available.", false);
                return true;
            }

            if (!tuning) StartStatusTimer2(false);

            // Operator-configurable (Options > Radio tab) -- 0.5% to 25% in 0.5% increments since
            // 2026-08-30 (whole-percent before). Clamped defensively even though the NumericUpDown's
            // own range should already keep it sane.
            double step = Math.Max(0.5, Math.Min(25.0, ctrl.Radio.AudioStepPercent)) / 100.0;
            double target = (double)_engineTxLevel + (up ? step : -step);

            // sound:false on every announcement below, deliberately -- this whole method only ever
            // runs while transmitting or tuning (guard above), so EVERY announcement here fires
            // during a live carrier. StatusView.ShowMessage's sound:true plays a Windows
            // SystemSounds.Beep through whatever the OS DEFAULT playback device is -- on a typical
            // ham setup where the radio's own sound-card interface IS that default device, that beep
            // would mix straight into the live transmitted audio. Root-caused live, 2026-08-09. The
            // screen-reader announcement itself is unaffected (driven by ShowMsg's own SendKeys
            // nudge, independent of sound) -- only the audible beep goes.
            DirectSetEngineTxLevel(target, (ok, applied) =>
            {
                if (!ok)
                {
                    StatusView.ShowMessage("Audio level change not confirmed -- engine not responding.", false);
                    return;
                }
                StatusView.ShowMessage($"Audio level {applied * 100:0.0}%", false);
            });
            return true;
        }

        // Save-side companion to ShouldRestoreTxLevel below: decides whether a just-CONFIRMED
        // SET_TX_LEVEL should also be written into the per-band remembered map, and under which
        // band key. Same gate the old inline AudioLevel() code used -- "Remember F11/F12 audio
        // level per band" on AND a real confirmed band index. Split out as a pure function so it
        // is unit-testable without a live engine host (JimmyTests, via InternalsVisibleTo).
        internal static bool ShouldRememberTxLevelForBand(bool rememberEnabled, int? bandIdx, out int bandKey)
        {
            bandKey = 0;
            if (!rememberEnabled || bandIdx == null) return false;
            bandKey = (int)bandIdx;
            return true;
        }

        // Pure decision for the restore half of "Remember F11/F12 audio level per band"
        // (RadioSettings.TxLevelByBand -- AudioLevel() above is the save half). Split out from
        // the actual SET_TX_LEVEL send so this is unit-testable without a live engine host --
        // same reasoning as EngineHost's own reply_wire_response extraction. internal (not
        // private): JimmyTests reaches it via InternalsVisibleTo (AssemblyInfo.Testing.cs).
        internal static bool ShouldRestoreTxLevel(bool rememberEnabled, int? bandIdx,
            IReadOnlyDictionary<int, double> txLevelByBand, out double level)
        {
            level = 0;
            if (!rememberEnabled || bandIdx == null || txLevelByBand == null) return false;
            return txLevelByBand.TryGetValue((int)bandIdx, out level);
        }

        // Called on every genuinely confirmed band change, from BOTH transports: WsjtxClient.
        // Direct.cs's own poll tick (native engine, Direct control-port mode) and
        // WsjtxClient.Protocol.cs's classic WSJT-X/UDP StatusMessage band-change handler. Found
        // live, 2026-08-17: only the Direct.cs call site existed before this -- an operator not
        // on pure Direct-mode-with-Jimmy-Native got a feature (AudioLevel() above already SAVES
        // correctly under either transport, since it reaches the engine's own control port
        // directly) that silently saved but never restored, because the restore call was never
        // mirrored into the classic UDP path. No saved entry for this band yet leaves the
        // engine's current level alone, same as when the feature is off entirely.
        private void RestoreTxLevelForBand()
        {
            if (!ShouldRestoreTxLevel(ctrl.Radio.RememberTxLevelPerBand, bandIdx, ctrl.Radio.TxLevelByBand, out double savedLevel))
                return;
            // Codex Audit 02 finding, 2026-08-21: routed through the ordered dispatcher instead of
            // a synchronous DirectSendCommand -- this runs on every confirmed band change, on the
            // UI thread (DirectApplyStatus), so a slow/hung engine host used to block band-change
            // handling for the full bounded connect/read wait.
            EnqueueDirectCommand("SET_TX_LEVEL " + savedLevel.ToString(System.Globalization.CultureInfo.InvariantCulture), null);
            DebugOutput($"{Time()} restored tx level {savedLevel:0.00} for band index {bandIdx}");
        }

        // 2026-08-18 investigation + restoration: this had NO live caller at all since the
        // 2026-08-12 Direct-only production cutover -- its only trigger, DecodesCompleted(), lived
        // exclusively inside the classic UDP dispatcher (both removed together in the later UDP
        // cleanup pass, but already unreachable well before that). Restored via DirectApplyDecodes'
        // own per-period-boundary block (WsjtxClient.Direct.cs) -- same real event, not a revived
        // timer. The APPLY half was restored too: SetupCq now sends AudioOffsetFromTxPeriod() via
        // DirectSetTxOffset before calling CQ, and ReplyTo sends AudioOffsetFromMsg(dmsg) the same
        // way before replying -- both through the new SET_TX_OFFSET control command (Engine::
        // set_tx_offset), which is NOT the REPLY command's dxFreqHz field: that one makes Jimmy's RX
        // (and TX, unless Hold Tx Freq is on) follow a specific DX station's own decoded frequency
        // (WSJT-X's classic double-click-to-work behavior); this is the unrelated "pick a quiet gap
        // in the passband for my own transmission" analysis, same as it always was.
        //
        // CalcTimerAdj (below) is NOT restored -- its only caller was StartProcessDecodeTimer's own
        // dispatcher-cycle-timing (deciding exactly when to fire a decode-completion timer relative
        // to trPeriod), a concept with no analog in Direct mode's independent snapshot-polling
        // interval. Left in place, unreferenced, same reasoning as before: a real fix would need
        // Direct's own polling cadence redesigned around it, out of scope here.
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
                // AutoFreqChanged(true, ...) parks opMode at START while the even/odd offsets
                // are unknown (see its own comment); nothing else ever resumes it to ACTIVE once
                // this analysis finishes, so ProcessDecodeMsg silently discarded every decode
                // from here on -- CheckActive() is idempotent (no-op unless opMode == START).
                CheckActive();
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

        // Item 4, 2026-08-24 (operator request): on-demand clock sync status hotkey. Deliberately
        // reads the SAME timeOffset/_clockWasAcceptable state CalcAvgTimeOffset (below) already
        // maintains for the automatic ClockOutOfSync/ClockSynced notifications, rather than
        // computing anything fresh -- guarantees this always agrees with whatever the automatic
        // notification would say, and needs no engine round trip (unlike ReportPowerSwr's own
        // on-demand SNAPSHOT query above, timeOffset is already tracked locally every period).
        public bool ReportClockStatus()
        {
            string offsetStr = timeOffset.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            string msg;
            if (_clockWasAcceptable == null)
                msg = "Clock sync not yet measured";
            else if (_clockWasAcceptable == true)
                msg = $"Clock sync good, offset {offsetStr} seconds";
            else
                msg = $"Clock out of sync, offset {offsetStr} seconds, check clock time";
            StatusView.ShowMessage(msg, false);
            return true;
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

            // Clock-sync notification, 2026-08-12: only evaluated on the authoritative
            // end-of-period average (clear:true) -- the two other call sites (WsjtxClient.
            // Protocol.cs, mid-cycle interim recalculations with clear:false) see a less
            // stable, still-accumulating average and must never trigger a transition off of
            // it. Transition-gated by design: _clockWasAcceptable only ever changes here, so a
            // clock that STAYS bad for many periods in a row publishes exactly once, not once
            // per period -- see ClockOutOfSyncEvent/ClockSyncedEvent's own dedup-key comments
            // for the second, independent backstop against exactly that kind of repeat.
            if (clear)
            {
                bool acceptable = Math.Abs(timeOffset) <= maxTimeOffset;
                if (_clockWasAcceptable == false && acceptable)
                    Notify?.Publish(new ClockSyncedEvent(mode));
                else if (_clockWasAcceptable != false && !acceptable)
                    Notify?.Publish(new ClockOutOfSyncEvent(timeOffset, mode));
                _clockWasAcceptable = acceptable;
            }
        }
    }
}
