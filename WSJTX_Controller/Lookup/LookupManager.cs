using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WSJTX_Controller
{
    public enum QrzLookupPolicy
    {
        Disabled         = 0,  // Default — never make automatic QRZ requests
        FocusedOnly      = 1,  // QRZ used only when user explicitly opens Lookup dialog
        UnidentifiedQueue = 2, // QRZ supplements offline data for queued stations it cannot identify
    }

    public class LookupManager
    {
        private bool            _useLookupData;
        private QrzLookupPolicy _policy  = QrzLookupPolicy.Disabled;
        private int             _qrzMinIntervalSeconds = 10;

        public QrzProvider     Qrz     { get; }
        public LoTWProvider    LoTW    { get; }
        public ClubLogProvider ClubLog { get; }
        public FccUlsProvider  FccUls  { get; }

        // Every ILookupProvider that can contribute to a Build(call), in
        // priority order (earlier providers' fields win -- matches the
        // precedence GetInfoForDialog has always used: QRZ > Club Log > LoTW).
        // Providers not owned by LookupManager itself (e.g. Controller's
        // DxSpotWatcher) are added via RegisterProvider once constructed.
        private readonly List<ILookupProvider> _providers = new List<ILookupProvider>();

        // Adds a provider not owned by LookupManager (e.g. DxSpotWatcher, whose
        // lifecycle Controller manages) to the Build(call) merge. Appended after
        // the built-in providers, so it only fills fields they left blank.
        public void RegisterProvider(ILookupProvider provider)
        {
            if (provider != null) _providers.Add(provider);
        }

        // Stage A6 test infrastructure: inserts at the FRONT of the provider chain, so
        // its fields win over every other provider's -- including any real local cache
        // a provider might coincidentally already have on a given machine (e.g. Club
        // Log's downloaded country-prefix table, which resolves purely from the
        // callsign prefix and needs no per-call caching). Used only to register
        // TestFixtureLookupProvider, so replay-test parity is fully deterministic and
        // never depends on what happens to already be cached on whichever machine is
        // running the tests.
        public void RegisterProviderFirst(ILookupProvider provider)
        {
            if (provider != null) _providers.Insert(0, provider);
        }

        public bool             Enabled => _useLookupData &&
                                          (Qrz.IsEnabled || LoTW.IsEnabled || ClubLog.IsEnabled);
        public QrzLookupPolicy  Policy  => _policy;

        public static string DataRoot =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Jimmy", "Data");

        // Callback invoked on background thread when an auto-lookup completes.
        // WsjtxClient should BeginInvoke to marshal back to the UI thread.
        public Action OnLookupCompleted;

        private readonly ConcurrentQueue<string> _autoQueue  = new ConcurrentQueue<string>();
        private readonly HashSet<string>          _queued     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object                   _queuedLock = new object();
        private readonly SemaphoreSlim            _autoSem    = new SemaphoreSlim(1, 1);
        private System.Timers.Timer               _autoTimer;

        public LookupManager()
        {
            var root = DataRoot;
            Directory.CreateDirectory(Path.Combine(root, "Temp"));
            Qrz     = new QrzProvider(root);
            LoTW    = new LoTWProvider(root);
            ClubLog = new ClubLogProvider(root);
            FccUls  = new FccUlsProvider(root);

            // FccUls goes first: its State (when it has one) is the authoritative
            // FCC-registered value and should win over QRZ's, per the same
            // QRZ-vs-grid.dat priority reasoning already applied elsewhere -- QRZ's
            // own US records ultimately derive from this same FCC data anyway.
            _providers.Add(FccUls);
            _providers.Add(Qrz);
            _providers.Add(ClubLog);
            _providers.Add(LoTW);
        }

        public void Initialize(
            bool            useLookupData,
            bool            qrzEnabled,      string qrzUser,   string qrzPass, int qrzCacheDays,
            bool            lotwEnabled,     int    lotwDays,
            // clubLogAppKey is Jimmy's registered Club Log application key (see
            // ClubLogAppKey.Resolve()), not a per-user credential.
            string          clubLogAppKey,   int    clubLogDays,
            bool            fccUlsEnabled    = false,
            QrzLookupPolicy policy           = QrzLookupPolicy.Disabled,
            int             qrzMinIntervalSeconds = 10)
        {
            _useLookupData         = useLookupData;
            _policy                = policy;
            _qrzMinIntervalSeconds = Math.Max(5, qrzMinIntervalSeconds);

            Qrz.Configure(qrzEnabled,     qrzUser,     qrzPass,     qrzCacheDays);
            LoTW.Configure(lotwEnabled);
            FccUls.Configure(fccUlsEnabled);

            // Club Log country data is Jimmy infrastructure, not a user-facing
            // lookup feature -- it always loads/refreshes regardless of the
            // "Use Lookup Data" master switch, since Rule Definition universes
            // depend on it and have nothing to do with QRZ/LoTW live lookups.
            ClubLog.Configure(true, clubLogAppKey);
            ClubLog.Load();

            if (!useLookupData) { StopAutoTimer(); return; }

            if (lotwEnabled)   LoTW.Load();
            if (qrzEnabled)    Qrz.PurgeOldEntries();
            if (fccUlsEnabled) FccUls.Load();

            StartAutoTimer();
        }

        public void StartBackgroundRefreshIfNeeded(int lotwDays, int clubLogDays, int fccUlsDays = 7)
        {
            if (ClubLog.NeedsRefresh(clubLogDays))
                _ = ClubLog.RefreshAsync();

            if (!_useLookupData) return;
            if (LoTW.IsEnabled && LoTW.NeedsRefresh(lotwDays))
                _ = LoTW.RefreshAsync();
            if (FccUls.IsEnabled && FccUls.NeedsRefresh(fccUlsDays))
                _ = FccUls.RefreshAsync();
        }

        // ── Synchronous lookups (no network) ────────────────────────────────────

        public bool IsLoTWUser(string call) =>
            _useLookupData && LoTW.IsUser(call);

        public DateTime? LoTWLastActivity(string call) =>
            _useLookupData ? LoTW.LastActivity(call) : null;

        // ── Provider-agnostic lookup ─────────────────────────────────────────────

        // Merges every enabled provider's cached/offline knowledge of a callsign
        // into one LookupRecord. Synchronous and network-free (each provider's
        // Contribute() only reads its own local cache) -- safe to call from the
        // per-decode hot path as well as the Lookup dialog. This is the single
        // merge point for "what do we know about this station" -- no other code
        // should call a specific provider directly.
        public LookupRecord Build(string call)
        {
            var record = new LookupRecord { Callsign = string.IsNullOrEmpty(call) ? call : call.ToUpperInvariant() };
            if (string.IsNullOrEmpty(call)) return record;

            foreach (var provider in _providers)
                if (provider.IsEnabled)
                    provider.Contribute(record, call);

            return record;
        }

        // Build a LookupRecord for the dialog by merging all sources (no
        // network). Caller should await LookupQrzAsync first if a live lookup
        // is desired. Unlike Build(call), this deliberately ignores the "Use
        // Lookup Data" master switch: the dialog is an explicit, user-initiated
        // request to see whatever's cached, not an automatic feature.
        public LookupRecord GetInfoForDialog(string call) => Build(call);

        // ── QRZ async ────────────────────────────────────────────────────────────

        public Task<LookupRecord> LookupQrzAsync(string call) =>
            _useLookupData && Qrz.IsEnabled
                ? Qrz.LookupAsync(call)
                : Task.FromResult<LookupRecord>(null);

        public bool QrzNeedsLookup(string call) =>
            _useLookupData && Qrz.NeedsLookup(call);

        public Task<bool> TestQrzAsync() => Qrz.TestAsync();

        // ── Automatic QRZ queue (UnidentifiedQueue policy) ───────────────────────

        public bool CanAutoQueue(string call) =>
            _useLookupData &&
            _policy == QrzLookupPolicy.UnidentifiedQueue &&
            Qrz.IsEnabled &&
            Qrz.NeedsLookup(call);

        public void QueueAutoLookup(string call)
        {
            if (string.IsNullOrEmpty(call)) return;
            lock (_queuedLock)
            {
                if (_queued.Contains(call)) return;
                _queued.Add(call);
            }
            _autoQueue.Enqueue(call);
        }

        private void StartAutoTimer()
        {
            StopAutoTimer();
            if (_policy != QrzLookupPolicy.UnidentifiedQueue || !Qrz.IsEnabled) return;
            _autoTimer = new System.Timers.Timer(_qrzMinIntervalSeconds * 1000.0);
            _autoTimer.Elapsed += AutoTimerElapsed;
            _autoTimer.AutoReset = true;
            _autoTimer.Start();
        }

        private void StopAutoTimer()
        {
            try { _autoTimer?.Stop(); _autoTimer?.Dispose(); } catch { }
            _autoTimer = null;
        }

        private async void AutoTimerElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (!await _autoSem.WaitAsync(0)) return;
            try
            {
                string call;
                if (!_autoQueue.TryDequeue(out call)) return;
                lock (_queuedLock) _queued.Remove(call);
                if (!CanAutoQueue(call)) return;
                var result = await Qrz.LookupAsync(call).ConfigureAwait(false);
                if (result != null) OnLookupCompleted?.Invoke();
            }
            catch { }
            finally { _autoSem.Release(); }
        }

        public void Dispose()
        {
            StopAutoTimer();
        }
    }
}
