using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WSJTX_Controller
{
    public enum TxMeterFeedbackMode { Off, Tone, Speech, ToneAndSpeech }

    // 2026-09-28 (operator request, "Option A"): accessible feedback from the radio's power and
    // ALC meters while the operator sets the drive level with F11/F12 -- during Tune (Alt+T) the
    // whole time, and during a normal transmission for a few seconds after a press (the drive
    // level applies live, mid-over, in both). Readings come from the engine's SNAPSHOT
    // (RadioStatus.tx_po_w / tx_alc), about once a second.
    //
    // Tone: one smooth sine whose pitch follows ALC -- 300 Hz at ALC 0, an octave higher per
    // 1.0 of ALC (capped at 2400 Hz), so one dot of a TS-590SG's ALC meter (about 0.17) is an
    // audible step. Tune = raise the drive until the tone just starts to climb, then back off.
    // History (operator tests, 2026-09-29): an ALC "buzz" (square wave) sounded like a second,
    // much higher tone and was misleading; a watts-based pitch could not climb at 5 W, where the
    // radio reports power in ~1.7 W steps (only 1.7 and 3.3 W ever came back). Watts are spoken.
    // Speech answers a request, once: each F11/F12
    // press (after its "Audio level N%" announcement) and the start of a Tune ask for a reading,
    // and the first meters that arrive at least ReadingDelaySeconds later -- so they reflect the
    // new level -- are spoken: "88 watts, ALC 1.50". Nothing is spoken unasked.
    //
    // ⚠️ The tone NEVER plays on the device that feeds the radio -- on many ham stations the
    // Windows default speaker IS the radio's sound card, and the tone would go out on the air
    // (the same hazard AudioLevel's sound:false announcements avoid). When they match, the tone
    // is refused and speech is used instead.
    internal sealed class TxMeterFeedback : IDisposable
    {
        // A requested reading uses meters taken at least this long after the request (the radio's
        // meters lag the drive change), and waits at most MissingMeterWaitSeconds more for the
        // second meter before speaking the one it has.
        internal const double ReadingDelaySeconds = 0.6;
        internal const double MissingMeterWaitSeconds = 1.0;

        private WasapiOut _out;
        private SignalGenerator _gen;
        private double? _lastWatts, _lastAlc;
        private DateTime? _readingWantedAfter;
        private double? _freshWatts, _freshAlc;
        private string _pendingLevel;
        private bool _active;

        internal static bool SpeechWanted(TxMeterFeedbackMode mode) =>
            mode == TxMeterFeedbackMode.Speech || mode == TxMeterFeedbackMode.ToneAndSpeech;

        // Ask for one spoken reading from meters taken after `now` + ReadingDelaySeconds.
        // levelText: an F11/F12 press's "Audio level 17.8%", spoken WITH the reading as one
        // announcement ("5 watts, ALC 0.33, audio level 17.8%" -- operator request, 2026-09-29)
        // instead of two back to back. It is never lost: spoken alone when no meter reading
        // comes, or when the keying ends first. A newer press replaces an unspoken one.
        internal void RequestReading(DateTime now, string levelText = null)
        {
            _readingWantedAfter = now.AddSeconds(ReadingDelaySeconds);
            _freshWatts = _freshAlc = null;
            _pendingLevel = levelText;
        }

        // ALC to tone pitch: 300 Hz at 0, doubling per 1.0 of ALC, 2400 Hz at most.
        internal static double ToneFrequency(double alc) =>
            Math.Min(2400.0, 300.0 * Math.Pow(2.0, Math.Max(0.0, alc)));

        internal static string SpeechText(double? watts, double? alc)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string w = watts.HasValue ? $"{watts.Value.ToString("0", inv)} watts" : null;
            string a = alc.HasValue ? $"ALC {alc.Value.ToString("0.00", inv)}" : null;
            return w != null && a != null ? $"{w}, {a}" : (w ?? a);
        }

        // Called once per engine poll. active: feedback wanted right now (tuning, or transmitting
        // just after an F11/F12 press). startOfTune: a Tune just began -- ask for a starting
        // reading. Returns text to speak, or null.
        internal string Update(TxMeterFeedbackMode mode, bool active, double? watts, double? alc,
                               string radioOutputDevice, DateTime now, Action<string> log, bool startOfTune = false)
        {
            if (mode == TxMeterFeedbackMode.Off || !active)
            {
                if (_active) { _active = false; StopTone(); }
                _lastWatts = _lastAlc = null;
                _readingWantedAfter = null; _freshWatts = _freshAlc = null;
                string unspoken = _pendingLevel;
                _pendingLevel = null;
                return unspoken;
            }
            if (!_active)
            {
                _active = true;
                if (startOfTune && _readingWantedAfter == null) RequestReading(now);
            }
            // Meters are not in every snapshot: keep the last reading of this keying.
            if (watts.HasValue) _lastWatts = watts;
            if (alc.HasValue) _lastAlc = alc;

            bool wantTone = mode == TxMeterFeedbackMode.Tone || mode == TxMeterFeedbackMode.ToneAndSpeech;
            bool wantSpeech = SpeechWanted(mode) || _pendingLevel != null;
            // The tone waits for an ALC reading, so it starts at the right pitch.
            if (wantTone && _lastAlc.HasValue && !UpdateTone(radioOutputDevice, log)) wantSpeech = true;   // tone refused: speak instead

            if (!wantSpeech || _readingWantedAfter == null || now < _readingWantedAfter.Value) return null;
            // Only meters that arrived after the wait count -- they reflect the new level.
            if (watts.HasValue) _freshWatts = watts;
            if (alc.HasValue) _freshAlc = alc;
            bool both = _freshWatts.HasValue && _freshAlc.HasValue;
            bool waitedEnough = now >= _readingWantedAfter.Value.AddSeconds(MissingMeterWaitSeconds);
            bool any = _freshWatts.HasValue || _freshAlc.HasValue;
            if (!both && !(waitedEnough && (any || _pendingLevel != null))) return null;
            string reading = SpeechText(_freshWatts, _freshAlc);
            string level = _pendingLevel == null ? null : char.ToLowerInvariant(_pendingLevel[0]) + _pendingLevel.Substring(1);
            string text = reading == null ? _pendingLevel : (level == null ? reading : $"{reading}, {level}");
            _readingWantedAfter = null; _freshWatts = _freshAlc = null; _pendingLevel = null;
            return text;
        }

        // Starts or retunes the tone; false when it may not play (radio device, or no device).
        private bool UpdateTone(string radioOutputDevice, Action<string> log)
        {
            try
            {
                if (_out == null)
                {
                    using (var enumerator = new MMDeviceEnumerator())
                    {
                        var speaker = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        string radioId = RadioOutputDeviceId(enumerator, radioOutputDevice);
                        if (speaker == null || radioId == null || string.Equals(speaker.ID, radioId, StringComparison.OrdinalIgnoreCase))
                        {
                            log?.Invoke($"meter tone refused: default speaker '{speaker?.FriendlyName}' feeds the radio (or none)");
                            return false;
                        }
                        var fmt = speaker.AudioClient.MixFormat;
                        _gen = new SignalGenerator(fmt.SampleRate, 1) { Gain = 0.15, Type = SignalGeneratorType.Sin, Frequency = ToneFrequency(_lastAlc ?? 0) };
                        _out = new WasapiOut(speaker, AudioClientShareMode.Shared, true, 100);
                        _out.Init(_gen);
                        _out.Play();
                    }
                }
                double freq = ToneFrequency(_lastAlc.Value);
                // Logged on every audible change, with the readings behind it.
                if (Math.Abs(freq - _gen.Frequency) >= 10 || !_toneLogged)
                    log?.Invoke($"tone {freq:0} Hz: watts {_lastWatts:0.#} alc {_lastAlc:0.00}");
                _toneLogged = true;
                _gen.Frequency = freq;
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"meter tone failed: {ex.Message}");
                StopTone();
                return false;
            }
        }

        // The engine's output device, by the same name-then-default rule AudioEndpointMasterVolume
        // uses (a blank or unplugged name means the engine plays on the Windows default).
        private static string RadioOutputDeviceId(MMDeviceEnumerator enumerator, string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
                foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    if (d.FriendlyName == name) return d.ID;
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)?.ID;
        }

        private bool _toneLogged;

        private void StopTone()
        {
            _toneLogged = false;
            try { _out?.Stop(); _out?.Dispose(); } catch { }
            _out = null;
            _gen = null;
        }

        public void Dispose() => StopTone();
    }
}
