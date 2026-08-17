using System;

namespace WSJTX_Controller
{
    // Extracted from Controller.OnNativeEngineUnexpectedExit (release-candidate modularization
    // pass) -- the bounded-retry decision itself is pure counting/clock logic with no WinForms
    // or process dependency, following the same extraction shape LiveQsoUploadOrchestrator
    // already established (WsjtxClient.ImportLiveLoggedQso -> a standalone, independently
    // testable class with no Controller/ctrl reference).
    //
    // Behavior preserved exactly from the original inline version: a rolling window, not a
    // lifetime cap -- an engine that crashes 5 times in one bad session and then runs clean for
    // hours can crash again later without being permanently locked out, while a persistently
    // crashing engine (a real config problem, not a transient fault) still degrades to "stopped
    // trying" instead of a tight restart loop hammering the audio device/COM port.
    public class EngineRestartPolicy
    {
        private readonly int _maxAttemptsPerWindow;
        private readonly TimeSpan _window;
        private readonly Func<DateTime> _now;

        private int _attemptCount;
        private DateTime _windowStart = DateTime.MinValue;

        public EngineRestartPolicy(int maxAttemptsPerWindow, TimeSpan window, Func<DateTime> now = null)
        {
            _maxAttemptsPerWindow = maxAttemptsPerWindow;
            _window = window;
            _now = now ?? (() => DateTime.UtcNow);
        }

        // Call once per observed crash/unexpected exit. Always records the attempt (so the
        // window/count stay accurate regardless of the caller's decision), then reports whether
        // this attempt is still within the bounded budget. attemptNumber is 1-based, for
        // building the same "(n/max)" status message the original inline version showed.
        public bool RecordAttemptAndShouldRestart(out int attemptNumber)
        {
            DateTime now = _now();
            if (now - _windowStart > _window)
            {
                _windowStart = now;
                _attemptCount = 0;
            }
            _attemptCount++;
            attemptNumber = _attemptCount;
            return _attemptCount <= _maxAttemptsPerWindow;
        }
    }
}
