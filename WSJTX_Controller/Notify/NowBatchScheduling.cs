using System;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Notification-joining support (2026-09-11 speech-batching fix). Monotonic elapsed-time
    // source for SpeechCoordinator's debounce/ceiling/gap timing -- deliberately NOT
    // DateTime.UtcNow. A wall-clock read can jump (NTP step, DST, an operator manually changing
    // the clock -- this app already has its own ClockOutOfSync notification for exactly that
    // scenario) and would corrupt an elapsed-time comparison; Environment.TickCount64 is
    // monotonic (milliseconds since system boot) and immune to wall-clock adjustments. Injected
    // so tests can advance time deterministically with no real Sleep -- see FakeMonotonicClock in
    // JimmyTests.cs.
    public interface IMonotonicClock
    {
        long ElapsedMilliseconds { get; }
    }

    public sealed class SystemMonotonicClock : IMonotonicClock
    {
        public long ElapsedMilliseconds => Environment.TickCount64;
    }

    // Schedules a one-shot callback after a delay, with real cancellation -- a bare
    // Action<int,Action> delegate (no way to cancel) is unsafe: if SpeechCoordinator clears its
    // pending state (a lifecycle boundary reconciled it, or a Critical event discarded it) while a
    // callback is already scheduled, that stale callback firing later would act on whatever NEW
    // state has accumulated since, which is exactly the "old batch's callback prematurely flushes
    // a new batch" race this interface exists to close. Every Schedule() call is independent (its
    // own token); Cancel() on a token that already fired or was already cancelled is a safe no-op.
    public interface INowBatchScheduler
    {
        object Schedule(int delayMs, Action callback);
        void Cancel(object token);
    }

    // Production implementation. Each Schedule() call gets its OWN short-lived
    // System.Windows.Forms.Timer -- Timer.Tick always fires on the UI thread (the same guarantee
    // WsjtxClient.Direct.cs's own _directPollTimer already relies on), so SpeechCoordinator never
    // needs to marshal back to the UI thread itself. Using one Timer per Schedule() call (rather
    // than a single shared Timer/dictionary) keeps cancellation trivial and lets more than one
    // schedule be genuinely concurrent (SpeechCoordinator needs this: an "open" batch's own
    // debounce/ceiling timer and a separately "frozen" batch's post-urgent gap-wait timer can be
    // pending at the same time). The volume of concurrent timers is always tiny (at most a
    // handful, for a few hundred milliseconds each) -- this is not a hot path.
    public sealed class WinFormsNowBatchScheduler : INowBatchScheduler, IDisposable
    {
        private sealed class Entry
        {
            public readonly Timer Timer = new Timer();
            public bool Fired;
        }

        // Pending schedules, so Dispose can cancel them: a profile switch closes the main window
        // but not the program, and a pending batch must not speak through the closed window.
        private readonly System.Collections.Generic.HashSet<Entry> _live = new System.Collections.Generic.HashSet<Entry>();
        private bool _disposed;

        public object Schedule(int delayMs, Action callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            var entry = new Entry();
            if (_disposed) { entry.Fired = true; return entry; }
            entry.Timer.Interval = Math.Max(1, delayMs);
            entry.Timer.Tick += (s, e) =>
            {
                if (entry.Fired) return;
                entry.Fired = true;
                entry.Timer.Stop();
                entry.Timer.Dispose();
                _live.Remove(entry);
                callback();
            };
            _live.Add(entry);
            entry.Timer.Start();
            return entry;
        }

        public void Cancel(object token)
        {
            if (!(token is Entry entry) || entry.Fired) return;
            entry.Fired = true;
            entry.Timer.Stop();
            entry.Timer.Dispose();
            _live.Remove(entry);
        }

        // Cancels every pending schedule; later ones do nothing.
        public void Dispose()
        {
            _disposed = true;
            foreach (var entry in new System.Collections.Generic.List<Entry>(_live)) Cancel(entry);
        }
    }
}
