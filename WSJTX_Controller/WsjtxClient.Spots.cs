using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WSJTX_Controller
{
    // "Prepare this station" from a spot (operator's agreed workflow, 2026-10-08; built 2026-10-12):
    // set the radio to the spot's band, mode and frequency, remember the callsign, and leave
    // transmit OFF. Nothing more happens until the station is actually decoded; it is then in
    // the ordinary station list, where the operator selects it and presses Enter to call it by
    // the normal reply path, with every normal safety check.
    //
    //   - A spot is not a decode. Nothing is invented from it: no SNR, no grid, no message.
    //   - Preparing never transmits and never arms anything to transmit later. Smart Mode is
    //     not started; only the receive-only Station Watch is, so the operator hears when the
    //     station is first decoded.
    //   - The radio is changed by the ordinary frequency path (SelectFrequency: the same one a
    //     frequency hotkey uses), never by a second route.
    //   - While it stands and transmit is off, the prepared station is listed when heard even
    //     if a list filter would have hidden it (it was asked for by name). The operator's
    //     filters are not changed. A blocked call stays blocked.
    //   - It ends when the station is logged, the band or mode moves away, another station is
    //     prepared, or after two hours.
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
            public bool Settled;      // the radio reached the band and mode, and the watch is on
        }
        private PreparedSpot _preparedSpot;
        private static readonly TimeSpan PreparedSpotLife = TimeSpan.FromHours(2);
        private static readonly TimeSpan PreparedSpotSettle = TimeSpan.FromSeconds(25);
        internal string PreparedSpotCall => _preparedSpot?.Call;

        // What preparing would interrupt right now, for the question asked first; null = nothing.
        internal string PrepareWouldInterrupt()
        {
            if (tuning) return "The radio is sending a tune carrier, which will be stopped.";
            if (!string.IsNullOrEmpty(callInProg)) return $"You are working {SC(callInProg)}. That contact will be stopped.";
            if (transmitting) return "The radio is transmitting, which will be stopped.";
            if (txEnabled) return "Transmit is on. It will be turned off.";
            return null;
        }

        // Carries the plan out. Null when it was started; else why not. Transmit is off when
        // this returns, whatever happened.
        internal string PrepareSpot(SpotPlan plan)
        {
            if (plan == null || !plan.Ok) return plan?.Reason ?? "Nothing to prepare.";
            if (!_directConnected) return "The engine is not connected, so the radio cannot be set.";
            if (IsBlocked(plan.Call)) return $"{SC(plan.Call)} is on your blocked list, so it would not be listed. Remove it from the list first.";
            // Transmit and any tune carrier stop first, and stay stopped: nothing below turns
            // transmit on, and nothing here is left to turn it on later.
            HaltAndDisableTx();
            StopStationWatch();
            _preparedSpot = new PreparedSpot { Call = plan.Call, Band = plan.Band, Tier = plan.Tier, DialKHz = plan.DialKHz, PreparedUtc = DateTime.UtcNow };
            DebugOutput($"{Time()} [SPOT] prepare {plan.Call}: {plan.Tier} {plan.Band} dial {plan.DialKHz} kHz (spotted {plan.SpotKhz:0.0} kHz{(plan.OffUsual ? ", not a usual frequency" : "")}); transmit off");
            if (!SelectFrequency(plan.BandIdx, plan.Tier, plan.DialKHz, "USB"))
            {
                _preparedSpot = null;
                return "The radio could not be set to that frequency. Transmit is off.";
            }
            return null;
        }

        // Every status from the engine: once the radio is on the prepared band and mode, the
        // receive-only watch starts (a band or mode change stops every watch, so it cannot be
        // started before). If the radio never gets there, say so once and forget the station.
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
                    StartOrReplaceStationWatch(p.Call);
                    DebugOutput($"{Time()} [SPOT] {p.Call}: radio on {p.Band} {p.Tier}; watching, transmit off");
                }
                else if (DateTime.UtcNow - p.PreparedUtc > PreparedSpotSettle)
                {
                    _preparedSpot = null;
                    Notify?.Publish(new ErrorWarningEvent(ErrorSeverity.Warning, "Prepare station",
                        $"The radio did not confirm {p.Band.TrimEnd('m')} meters {p.Tier}, so {SC(p.Call)} is not being watched. Transmit is off."));
                }
                return;
            }
            // Settled: it ends when the radio leaves that band or mode, or after two hours.
            if (!there && _pendingBandIdx == null && !_tierChangeRequestInFlight) { DebugOutput($"{Time()} [SPOT] {p.Call}: band or mode changed; no longer prepared"); _preparedSpot = null; }
            else if (DateTime.UtcNow - p.PreparedUtc > PreparedSpotLife) _preparedSpot = null;
        }

        // The prepared station, heard now with transmit off: it is listed whatever the list's
        // filters say. Never with transmit on -- then only the operator's own filters decide
        // what Jimmy may call.
        private bool IsPreparedSpot(string call) =>
            _preparedSpot != null && _preparedSpot.Settled && !txEnabled && !string.IsNullOrEmpty(call)
            && string.Equals(_preparedSpot.Call, call, StringComparison.OrdinalIgnoreCase);

        private void PreparedSpotLogged(string call)
        {
            if (_preparedSpot != null && string.Equals(_preparedSpot.Call, call, StringComparison.OrdinalIgnoreCase)) _preparedSpot = null;
        }

        // Tests.
        internal void TestSetPreparedSpot(string call, string band, string tier, bool settled) =>
            _preparedSpot = call == null ? null : new PreparedSpot { Call = call, Band = band, Tier = tier, PreparedUtc = DateTime.UtcNow, Settled = settled };
        internal bool TestIsPreparedSpot(string call) => IsPreparedSpot(call);
    }
}
