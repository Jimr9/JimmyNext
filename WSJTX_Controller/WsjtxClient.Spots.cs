using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // "Prepare this station" from a spot (operator's design, 2026-10-13; it replaces the first
    // one of 2026-10-12, which used a Station Watch and so silenced Jimmy's routine speech for
    // as long as the watch ran).
    //
    //   - The radio is ALREADY on the spot's band, mode and frequency: the station goes onto
    //     Smart Mode's waiting list, exactly as if it had been picked from the station list with
    //     Smart Mode on. Smart Mode calls it when it is actually heard and free, with all its
    //     own limits and its own speech.
    //   - The radio has to move: it is moved (by the ordinary frequency path, the one a
    //     frequency hotkey uses), transmit is turned OFF, the station is remembered -- and that
    //     is all. The antenna may need tuning on the new band, and Jimmy cannot know. Nothing
    //     is called until the operator says the station is ready: the Prepared Station Ready
    //     key (default Alt+Shift+P), or Prepare Station on the spot again. Only then does the
    //     station go onto Smart Mode. No dialog is left open meanwhile, so the tune key works.
    //   - Smart Mode switched off in Options: Jimmy never calls on its own. The station is
    //     listed when it is heard, even where a list filter would hide it, and that is said once.
    //   - A spot is not a decode. Nothing is invented from it: no SNR, no grid, no message.
    //   - A remembered station is forgotten when the band or mode moves away, when it is logged,
    //     when another is prepared, or after two hours.
    public partial class WsjtxClient
    {
        // The plan for one spot: what would be done, or why it cannot be. Made without touching
        // the radio, so the operator is shown it first.
        public sealed class SpotPlan
        {
            public bool Ok;
            public string Reason;          // why not, in plain words
            public string Call, Tier, Band;
            public int BandIdx, DialKHz;
            public bool OffUsual;          // not one of the usual FT8/FT4 frequencies for the band
            public double SpotKhz;
            public string Outcome;         // what was done, once PrepareSpot has carried it out

            // "K1ABC, FT8 on 20 meters, dial 14.074 megahertz."
            public string Sentence() =>
                $"{Call}, {Tier} on {Band.TrimEnd('m')} meters, dial {DialKHz / 1000.0:0.000} megahertz" +
                (OffUsual ? ". This is not a usual " + Tier + " frequency; the radio will be set to it as it was spotted." : ".");
        }

        // bandsMeters: the bands Jimmy operates, in its own order (160, 80, ...). dialsFor: the
        // dial frequencies in kHz Jimmy knows for a band and mode -- the operator's own (Options,
        // Frequencies) first, then the built-in one.
        internal static SpotPlan PlanSpot(string call, string spotMode, double freqKhz, IList<int> bandsMeters, Func<int, string, IEnumerable<int>> dialsFor)
        {
            var plan = new SpotPlan { Call = (call ?? "").Trim().ToUpperInvariant(), SpotKhz = freqKhz };
            if (plan.Call.Length == 0) { plan.Reason = "The spot has no callsign."; return plan; }
            // FT8 or FT4, said by the spot itself. "DIGI", "DATA", SSB, CW or nothing at all is
            // never taken to mean FT8.
            string mode = (spotMode ?? "").Trim().ToUpperInvariant();
            if (mode != "FT8" && mode != "FT4")
            {
                plan.Reason = mode.Length == 0 ? "The spot does not say which mode it is. Only an FT8 or FT4 spot can be prepared."
                    : $"The spot is {mode}. Only an FT8 or FT4 spot can be prepared.";
                return plan;
            }
            plan.Tier = mode;
            if (double.IsNaN(freqKhz) || double.IsInfinity(freqKhz) || freqKhz <= 0) { plan.Reason = "The spot has no frequency."; return plan; }
            string band = SpotRows.BandOfKhz(freqKhz);
            int meters = band != null && int.TryParse(band.TrimEnd('m'), NumberStyles.None, CultureInfo.InvariantCulture, out int m) ? m : 0;
            int idx = meters > 0 && bandsMeters != null ? bandsMeters.IndexOf(meters) : -1;
            if (idx < 0)
            {
                plan.Reason = band == null ? $"{freqKhz / 1000.0:0.000} megahertz is not on an amateur band." : $"Jimmy Next does not operate on {band.TrimEnd('m')} meters.";
                return plan;
            }
            plan.Band = band;
            plan.BandIdx = idx;
            // The dial. A spot gives either the dial itself or the signal's own frequency, a
            // little above it. When it falls in the 3 kHz above a dial Jimmy knows for this band
            // and mode, that dial is the one. Otherwise it is an unusual frequency (a
            // DXpedition's, say): the radio is set to the spotted frequency itself, in whole
            // kilohertz at or below it, and the plan says so -- never silently moved to the
            // band's usual frequency, where the station is not.
            var known = (dialsFor?.Invoke(idx, mode) ?? Enumerable.Empty<int>()).Where(d => d > 0).ToList();
            int usual = known.Where(d => freqKhz >= d - 0.05 && freqKhz <= d + 3.0).OrderByDescending(d => d).FirstOrDefault();
            if (usual > 0) plan.DialKHz = usual;
            else { plan.DialKHz = (int)Math.Floor(freqKhz + 0.05); plan.OffUsual = true; }
            plan.Ok = true;
            return plan;
        }

        // The plan for a spot against this station's own bands and frequencies.
        internal SpotPlan PlanSpot(string call, string spotMode, double freqKhz) =>
            PlanSpot(call, spotMode, freqKhz, bands, (idx, tier) =>
            {
                var dials = new List<int>();
                if (idx >= 0 && idx < ctrl.Frequencies.Bands.Length)
                    dials.AddRange(ctrl.Frequencies.Bands[idx].Where(e => e.Mode == tier).Select(e => e.FreqKHz));
                if (freqsDict.TryGetValue(tier, out var builtIn) && idx >= 0 && idx < builtIn.Count) dials.Add(builtIn[idx]);
                return dials;
            });

        internal sealed class PreparedSpot
        {
            public string Call, Band, Tier;
            public int DialKHz;
            public DateTime PreparedUtc;
            public bool Settled;      // the radio is on the band and mode
            public bool ListOnly;     // Smart Mode is off: listed when heard, never called by Jimmy
            public bool HeardSaid;
        }
        private PreparedSpot _preparedSpot;
        private static readonly TimeSpan PreparedSpotLife = TimeSpan.FromHours(2);
        private static readonly TimeSpan PreparedSpotSettle = TimeSpan.FromSeconds(25);
        internal string PreparedSpotCall => _preparedSpot?.Call;

        // Whether carrying the plan out moves the radio (band, mode or dial). When it does not,
        // nothing is interrupted and there is nothing to tune.
        internal bool PrepareMovesRadio(SpotPlan plan) =>
            plan == null || !plan.Ok || mode != plan.Tier
            || !string.Equals(CurrentBandStr, plan.Band, StringComparison.OrdinalIgnoreCase)
            || (long)Math.Round(dialFrequency / 1000.0) != plan.DialKHz;

        // What moving the radio would interrupt right now, for the question asked first; null = nothing.
        internal string PrepareWouldInterrupt()
        {
            if (tuning) return "The radio is sending a tune carrier, which will be stopped.";
            if (!string.IsNullOrEmpty(callInProg)) return $"You are working {SC(callInProg)}. That contact will be stopped.";
            if (transmitting) return "The radio is transmitting, which will be stopped.";
            string waiting = SmartStillWaitingCalls();
            if (waiting.Length > 0) return $"Smart Mode is waiting for {waiting}. That will stop.";
            if (txEnabled) return "Transmit is on. It will be turned off.";
            return null;
        }

        // The key that says "the prepared station is ready", as the operator has it set.
        private string PreparedReadyKeyText()
        {
            Keys key = ctrl.hotkeyConfig != null ? ctrl.hotkeyConfig[HotkeyAction.PreparedStationReady] : Keys.None;
            return key == Keys.None ? "Prepare Station on the spot again" : HotkeyConfig.FormatKeys(key);
        }

        // Carries the plan out. Null when done (plan.Outcome says what was done); else why not.
        internal string PrepareSpot(SpotPlan plan)
        {
            if (plan == null || !plan.Ok) return plan?.Reason ?? "Nothing to prepare.";
            if (!_directConnected) return "The engine is not connected, so the radio cannot be set.";
            if (IsBlocked(plan.Call)) return $"{SC(plan.Call)} is on your blocked list, so it would not be listed. Remove it from the list first.";

            if (!PrepareMovesRadio(plan))
            {
                // On the band already: straight to Smart Mode. Nothing is halted or changed.
                _preparedSpot = new PreparedSpot { Call = plan.Call, Band = plan.Band, Tier = plan.Tier, DialKHz = plan.DialKHz, PreparedUtc = DateTime.UtcNow, Settled = true };
                DebugOutput($"{Time()} [SPOT] prepare {plan.Call}: already on {plan.Band} {plan.Tier} dial {plan.DialKHz} kHz");
                return PreparedSpotBegin(out plan.Outcome);
            }

            // The radio moves. Transmit and any tune carrier stop first, and stay stopped:
            // nothing below turns transmit on, and nothing here is left to turn it on later.
            HaltAndDisableTx();
            _preparedSpot = new PreparedSpot { Call = plan.Call, Band = plan.Band, Tier = plan.Tier, DialKHz = plan.DialKHz, PreparedUtc = DateTime.UtcNow };
            DebugOutput($"{Time()} [SPOT] prepare {plan.Call}: {plan.Tier} {plan.Band} dial {plan.DialKHz} kHz (spotted {plan.SpotKhz:0.0} kHz{(plan.OffUsual ? ", not a usual frequency" : "")}); transmit off; waits for the operator");
            if (!SelectFrequency(plan.BandIdx, plan.Tier, plan.DialKHz, "USB"))
            {
                _preparedSpot = null;
                return "The radio could not be set to that frequency. Transmit is off.";
            }
            plan.Outcome = $"Setting the radio for {plan.Call}: {plan.Tier}, {plan.Band.TrimEnd('m')} meters, {plan.DialKHz / 1000.0:0.000}. Transmit is off. " +
                           $"Tune if needed, then press {PreparedReadyKeyText()} to wait for it.";
            return null;
        }

        // The remembered station is ready (the radio is on its band): onto Smart Mode -- or, with
        // Smart Mode off, listed when heard. Null when done (said = what to tell the operator).
        private string PreparedSpotBegin(out string said)
        {
            said = null;
            var p = _preparedSpot;
            if (p == null) return "No station is prepared.";
            if (!ctrl.smartQsoStartEnabled)
            {
                p.ListOnly = true;
                said = $"Smart Mode is off, so {SC(p.Call)} will not be called for you. It will be listed when it is heard.";
                DebugOutput($"{Time()} [SPOT] {p.Call}: Smart Mode is off; listed when heard");
                return null;
            }
            string problem = SmartWaitForSpot(p.Call);
            if (problem != null) return problem;
            _preparedSpot = null;      // Smart Mode has it now, by its own rules
            said = $"Waiting to work {SC(p.Call)}. Smart Mode will call it when it is heard and free.";
            return null;
        }

        // The Prepared Station Ready key: the operator has the radio (and the antenna) ready on
        // the band Prepare Station moved to.
        public void PreparedStationReady()
        {
            var p = _preparedSpot;
            if (p == null) { StatusView.ShowMessage("No station is prepared.", false); return; }
            if (p.ListOnly) { StatusView.ShowMessage($"{SC(p.Call)} will be listed when it is heard. Smart Mode is off.", false); return; }
            if (!p.Settled) { StatusView.ShowMessage($"The radio is not on {p.Band.TrimEnd('m')} meters {p.Tier} yet.", false); return; }
            string problem = PreparedSpotBegin(out string said);
            // Smart Mode says "Waiting to work ..." itself when it takes a station.
            if (problem != null) StatusView.ShowMessage(problem, false);
            else if (p.ListOnly) StatusView.ShowMessage(said, false);
        }

        // Every status from the engine. While the radio is on its way: once it is on the
        // prepared band and mode, say so and what to do next -- or, if it never gets there, say
        // that once and forget the station. Afterwards: forgotten when the radio leaves that
        // band or mode, or after two hours.
        private void PreparedSpotTick()
        {
            var p = _preparedSpot;
            if (p == null) return;
            bool there = _pendingBandIdx == null && !_tierChangeRequestInFlight && mode == p.Tier
                         && string.Equals(CurrentBandStr, p.Band, StringComparison.OrdinalIgnoreCase);
            if (!p.Settled)
            {
                if (there)
                {
                    p.Settled = true;
                    DebugOutput($"{Time()} [SPOT] {p.Call}: radio on {p.Band} {p.Tier}; transmit off; waiting for the operator");
                    StatusView.ShowMessage($"Radio is on {p.Band.TrimEnd('m')} meters {p.Tier}, transmit off. Tune if needed, then press {PreparedReadyKeyText()} to wait for {SC(p.Call)}.", false);
                }
                else if (DateTime.UtcNow - p.PreparedUtc > PreparedSpotSettle)
                {
                    _preparedSpot = null;
                    Notify?.Publish(new ErrorWarningEvent(ErrorSeverity.Warning, "Prepare station",
                        $"The radio did not confirm {p.Band.TrimEnd('m')} meters {p.Tier}, so {SC(p.Call)} is not prepared. Transmit is off."));
                }
                return;
            }
            if (!there && _pendingBandIdx == null && !_tierChangeRequestInFlight) { DebugOutput($"{Time()} [SPOT] {p.Call}: band or mode changed; no longer prepared"); _preparedSpot = null; }
            else if (DateTime.UtcNow - p.PreparedUtc > PreparedSpotLife) _preparedSpot = null;
        }

        // Smart Mode off: the prepared station, heard now with transmit off, is listed whatever
        // the list's filters say. Never with transmit on -- then only the operator's own
        // filters decide what Jimmy may call.
        private bool IsPreparedSpot(string call) =>
            _preparedSpot != null && _preparedSpot.ListOnly && _preparedSpot.Settled && !txEnabled && !string.IsNullOrEmpty(call)
            && string.Equals(_preparedSpot.Call, call, StringComparison.OrdinalIgnoreCase);

        // ...and the first time that happens, it is said (once).
        private void PreparedSpotHeard(string call)
        {
            var p = _preparedSpot;
            if (p == null || p.HeardSaid || !string.Equals(p.Call, call, StringComparison.OrdinalIgnoreCase)) return;
            p.HeardSaid = true;
            DebugOutput($"{Time()} [SPOT] {p.Call}: heard; in the station list");
            StatusView.ShowMessage($"{SC(p.Call)} heard. It is in the station list.", false);
        }

        private void PreparedSpotLogged(string call)
        {
            if (_preparedSpot != null && string.Equals(_preparedSpot.Call, call, StringComparison.OrdinalIgnoreCase)) _preparedSpot = null;
        }

        // Tests.
        internal void TestSetPreparedSpot(string call, string band, string tier, bool settled, bool listOnly = false) =>
            _preparedSpot = call == null ? null : new PreparedSpot { Call = call, Band = band, Tier = tier, PreparedUtc = DateTime.UtcNow, Settled = settled, ListOnly = listOnly };
        internal bool TestIsPreparedSpot(string call) => IsPreparedSpot(call);
        internal string TestPreparedSpotBegin(out string said) => PreparedSpotBegin(out said);
    }
}
