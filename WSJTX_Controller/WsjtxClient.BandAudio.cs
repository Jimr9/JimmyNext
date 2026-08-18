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

        // Options > Frequencies per-entry hotkey: jump straight to one specific frequency row
        // (not necessarily a band's primary/first entry the way SelectBand's bandToFreq lookup
        // always resolves to) -- switches mode first if the entry's mode differs from the
        // current one, matching Alt+M's own SetOperatingMode path.
        public bool SelectFrequency(int targetIdx, string entryMode, int freqKHz)
        {
            if (targetIdx < 0 || targetIdx >= bands.Count) return false;
            if (freqKHz <= 0) return false;

            if (entryMode != mode) SetOperatingMode(entryMode);

            ClearAudioOffsets();
            AutoFreqChanged(false, true);
            Pause(true, false);
            CancelQso();

            if (RetuneBand(targetIdx, (uint)(freqKHz * 1000), "SelectFrequency"))
                ShowBandChangePending(targetIdx, $"{freqKHz} kHz");
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
            if (entry.Mode == mode) return SelectFrequency(targetIdx, entry.Mode, entry.FreqKHz);
            return SelectBand(targetIdx);
        }

        // Self-sufficiency plan, Phase 5: band changes retune the radio directly over rigctld --
        // the engine's own poll loop picks up the new dial on its next tick (see
        // RigctldClient.SetFrequency's own comment) exactly like a knob-QSY would. Under
        // RadioControlMode.WsjtxCat there is no separate CAT connection at all (radio state
        // comes read-only from the engine's own StatusMessage broadcasts), so there is nothing
        // to retune -- returns false so callers skip the "Changing to..." status announcement
        // instead of claiming a band change that never happens.
        private bool RetuneBand(int targetIdx, string caller) =>
            RetuneBand(targetIdx, (uint)(bandToFreq(targetIdx) * 1000), caller);

        // SelectFrequency's own entry point: an explicit target frequency, not necessarily the
        // band's primary/first entry bandToFreq(targetIdx) would resolve to.
        private bool RetuneBand(int targetIdx, uint freqHz, string caller)
        {
            _pendingBandIdx = targetIdx;
            DebugOutput($"{Time()} [BAND-AUDIT] {caller}: currentBandIdx:{bandIdx} targetIdx:{targetIdx} newFreq:{freqHz} txFirst:{txFirst}");
            if (ctrl.Radio.Mode != RadioControlMode.HamlibRigctld || ctrl.rigctldClient == null)
            {
                StatusView.ShowMessage("Band change needs Hamlib rigctld -- not available under WSJT-X CAT radio mode.", true);
                return false;
            }
            ctrl.rigctldClient.SetFrequency(freqHz);
            return true;
        }

        private void ShowBandChangePending(int targetIdx, string detail = null)
        {
            ctrl.statusText.ForeColor = Color.Black;
            ctrl.statusText.BackColor = Color.Yellow;
            ctrl.statusText.Text = $"Changing to {detail ?? $"{bands[targetIdx]} meter band"}...";
            ctrl.statusText.SelectionStart = 0;
        }

        // Self-sufficiency plan, Phase 1: one fresh, synchronous PollOnce() -- deliberately not
        // reusing ctrl.lastRadioStatus's background-timer value, since this is an explicit
        // on-demand "check now" action (Alt+Q), not a passive display. Needs Hamlib rigctld --
        // under RadioControlMode.WsjtxCat there is no separate CAT connection to poll at all.
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
            if (transmitting || tuning)
            {
                if (ctrl.Radio.Mode == RadioControlMode.HamlibRigctld && ctrl.rigctldClient != null)
                {
                    var status = ctrl.rigctldClient.PollOnce();
                    ctrl.lastRadioStatus = status;
                    StatusView.ShowMessage(FormatRigctldPowerSwr(status), false);
                    return true;
                }

                StatusView.ShowMessage("Power/SWR needs Hamlib rigctld -- not available under WSJT-X CAT radio mode.", false);
                return true;
            }

            // Not transmitting/tuning: the soundcard's own RX audio-in level, straight from the
            // native engine (same fresh, on-demand SNAPSHOT pattern as AudioLevel() above --
            // deliberately not a cached/periodic value, matching this hotkey's own "check now"
            // purpose per this method's original comment).
            string snapJson = DirectSendCommand("SNAPSHOT");
            if (snapJson != null && snapJson.Length > 0 && !snapJson.StartsWith("ERR"))
            {
                try
                {
                    var snap = System.Text.Json.JsonSerializer.Deserialize<DirectSnapshot>(snapJson, DirectJsonOptions);
                    if (snap?.Radio != null)
                    {
                        StatusView.ShowMessage($"Audio in: {RxLevelToDb(snap.Radio.RxLevel):0} dB", false);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    DebugOutput($"{Time()} ReportPowerSwr: engine SNAPSHOT parse failed, falling back to Hamlib rigctld: {ex.Message}");
                }
            }

            if (ctrl.Radio.Mode == RadioControlMode.HamlibRigctld && ctrl.rigctldClient != null)
            {
                var status = ctrl.rigctldClient.PollOnce();
                ctrl.lastRadioStatus = status;
                StatusView.ShowMessage(FormatRigctldPowerSwr(status), false);
                return true;
            }

            StatusView.ShowMessage("Power/SWR needs Hamlib rigctld -- not available under WSJT-X CAT radio mode.", false);
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

        private static string FormatRigctldPowerSwr(RadioStatus status)
        {
            if (!status.Ok) return $"Radio: {status.LastError ?? "no response"}";
            var parts = new List<string>();
            if (status.SMeterDb.HasValue) parts.Add($"S-meter {status.SMeterDb.Value} dB");
            if (status.PowerRaw.HasValue) parts.Add($"power {status.PowerRaw.Value:0.00}");
            if (status.Swr.HasValue) parts.Add($"SWR {status.Swr.Value:0.0}");
            return parts.Count > 0 ? string.Join(", ", parts) : "Radio: no meter data available from rigctld.";
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
        public bool ToggleTuningProcess()
        {
            bool newState = !tuning;
            // Found live, 2026-08-10, auditing against production: the original classic-UDP
            // ToggleTuning() (deleted this same session, replacing Andy WM8Q's proprietary
            // sub-command 19 with this method) always did "if (txEnabled) HaltTx();" before
            // starting a tune carrier, so a normal Tx cycle could never race against Tune. This
            // replacement dropped that -- restored here, same condition, HaltTx() itself is
            // already mode-aware (routes to DirectSendHaltTx() under Direct mode).
            if (newState && txEnabled) HaltTx();
            if (!DirectSetTuning(newState))
            {
                // "Talk to engine directly" used to be a real Options toggle this message could
                // point the operator at -- removed (found stale while auditing UDP-transport
                // code, 2026-08-17): the native engine is always running and always the only
                // transport in production now (see NativeEngineSettings.cs's own comment), so a
                // failure here means the engine process itself isn't reachable, not a mode
                // choice.
                StatusView.ShowMessage("Tune needs the native engine, which isn't currently reachable.", true);
                return true;
            }
            tuning = newState;
            if (!tuning) StartStatusTimer2(false);
            StatusView.ShowMessage(tuning ? "Tune started" : "Tune stopped", false);
            return true;
        }

        // Self-sufficiency plan, Phase 1: the one and only F11/F12 redirect point (confirmed by
        // direct tracing -- HotkeyConfig.cs -> Controller.ProcessCmdKey -> here -> either path
        // below).
        //
        // Self-sufficiency plan Phase 6 addendum, 2026-08-09: F11/F12 originally (Andy WM8Q's
        // fork) sent a proprietary UDP sub-command (NewTxMsgIdx=20, see the very first commit in
        // this repo's history) that told WSJT-X itself to adjust its own generated Tx tone
        // level -- not a CAT command to the radio, and not a Windows output-device volume
        // either. That only ever worked against Andy's actual fork; standard WSJT-X, WSJT-X
        // Improved, and the native engine all speak the standard protocol and never understood
        // it, which is why Phase 1 replaced it with the Hamlib-rigctld CAT path below (adjusting
        // the radio's own receive AF gain -- a different thing, but the closest available
        // replacement at the time, and still the only option when Radio.Mode == WsjtxCat, where
        // nothing here has any CAT connection to the radio at all).
        //
        // The native engine's own Engine::set_tx_level/tx_level (tempo-app/src/engine.rs) is the
        // real modern match to the original intent -- it's WSJT-X's own "Pwr" slider equivalent,
        // the sound-card/software side of the signal chain (scales the generated tone before it
        // ever reaches the sound card), already wired up on the Rust side and applied live every
        // slot, just not exposed to Jimmy before now (control port's new SET_TX_LEVEL command,
        // EngineHost/src/main.rs). Tried first below since it works regardless of whether
        // Jimmy's own transport to the engine is UDP or Direct -- both talk to the same engine
        // process over this same control port. Falls through to the legacy Hamlib-rigctld CAT
        // path (the radio's own receive AF gain -- a different point in the signal chain, but
        // the closest available replacement) if the engine isn't reachable at all (e.g. talking
        // to real WSJT-X, no engine process exists).
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

            if (!tuning) StartStatusTimer2(false);

            // Operator-configurable (Options > Radio tab), added 2026-08-09 -- previously a
            // hardcoded 0.05 (5%) on both this path and RigctldClient.AdjustAudioLevel's own copy
            // below. Clamped defensively even though the NumericUpDown's own range should already
            // keep it sane.
            double step = Math.Max(1, Math.Min(25, ctrl.Radio.AudioStepPercent)) / 100.0;

            // sound:false on every announcement below, deliberately -- this whole method only
            // ever runs while transmitting is already true (guard above), so EVERY announcement
            // here fires during a live over. StatusView.ShowMessage's sound:true plays a Windows
            // SystemSounds.Beep through whatever the OS DEFAULT playback device is -- on a
            // typical ham setup where the radio's own sound-card interface IS that default
            // device, that beep would mix straight into the live transmitted audio. Root-caused
            // live, 2026-08-09, from a report of hearing a Windows beep on every F11/F12 press
            // while transmitting. The screen-reader announcement itself is unaffected (driven by
            // ShowMsg's own SendKeys nudge, independent of sound) -- only the audible beep goes.
            string snapJson = DirectSendCommand("SNAPSHOT");
            if (snapJson != null && snapJson.Length > 0 && !snapJson.StartsWith("ERR"))
            {
                try
                {
                    var snap = System.Text.Json.JsonSerializer.Deserialize<DirectSnapshot>(snapJson, DirectJsonOptions);
                    // TxLevel is never null (Nexus always defaults it to 0.9, no CAT connection
                    // needed -- unlike MicGain's Option<f32>), so only the Radio object itself
                    // needs to exist for this path to apply.
                    if (snap?.Radio != null)
                    {
                        double current = snap.Radio.TxLevel;
                        double next = Math.Max(0.0, Math.Min(1.0, current + (up ? step : -step)));
                        DirectSendCommand("SET_TX_LEVEL " + next.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        // Options > Radio "Remember F11/F12 audio level per band" -- see
                        // RadioSettings.TxLevelByBand's own comment. Restored on the next band
                        // change by WsjtxClient.Direct.cs's own band-change detection.
                        if (ctrl.Radio.RememberTxLevelPerBand && bandIdx != null)
                            ctrl.Radio.TxLevelByBand[(int)bandIdx] = next;
                        StatusView.ShowMessage($"Audio level {next * 100:0}%", false);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    DebugOutput($"{Time()} AudioLevel: engine SNAPSHOT parse failed, falling back to Hamlib rigctld: {ex.Message}");
                }
            }

            if (ctrl.Radio.Mode == RadioControlMode.HamlibRigctld && ctrl.rigctldClient != null)
            {
                if (ctrl.rigctldClient.AdjustAudioLevel(up, step, out double newLevel))
                    StatusView.ShowMessage($"Audio level {newLevel * 100:0}%", false);
                else
                    StatusView.ShowMessage($"Audio level: {ctrl.rigctldClient.LastError}", false);
            }
            else
                StatusView.ShowMessage("Audio level needs the native engine or Hamlib rigctld -- neither is currently available.", false);
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
            DirectSendCommand("SET_TX_LEVEL " + savedLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
            DebugOutput($"{Time()} restored tx level {savedLevel:0.00} for band index {bandIdx}");
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
