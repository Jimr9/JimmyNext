using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace WSJTX_Controller
{
    public enum QrzLookupPolicy
    {
        Disabled         = 0,  // Default — never make automatic primary-provider requests
        FocusedOnly      = 1,  // Primary provider used only when user explicitly opens Lookup dialog
        UnidentifiedQueue = 2, // Primary provider supplements offline data for queued stations it cannot identify
    }

    // Which online service is the PRIMARY automatic callsign lookup provider -- the one
    // FocusedOnly/UnidentifiedQueue policy and the Lookup dialog's on-open auto-lookup use.
    // Only one is ever primary at a time (operator instruction: don't spend two live network
    // lookups per callsign merely to combine data). Both providers can still independently have
    // their own IsEnabled/credentials configured and still passively CONTRIBUTE already-cached
    // data to Build()'s merge either way -- see LookupManager's constructor comment -- this
    // setting controls only which one gets to make a NEW live network lookup automatically.
    public enum CallsignLookupProvider
    {
        Qrz    = 0, // default -- matches every existing installation's behavior unchanged
        HamQth = 1,
    }

    public class LookupManager
    {
        private bool            _useLookupData;
        private QrzLookupPolicy _policy  = QrzLookupPolicy.Disabled;
        private int             _qrzMinIntervalSeconds = 10;
        private CallsignLookupProvider _primaryProviderChoice = CallsignLookupProvider.Qrz;

        public QrzProvider     Qrz     { get; }
        public HamQthProvider  HamQth  { get; }
        public LoTWProvider    LoTW    { get; }
        public ClubLogProvider ClubLog { get; }
        public FccUlsProvider  FccUls  { get; }

        // The provider FocusedOnly/UnidentifiedQueue policy and the Lookup dialog's on-open
        // auto-lookup actually drive -- selected by _primaryProviderChoice. Exposed as
        // IOnlineLookupProvider so callers (LookupInfoDlg, ManualCallDlg) never need a
        // provider-specific branch.
        public IOnlineLookupProvider PrimaryProvider =>
            _primaryProviderChoice == CallsignLookupProvider.HamQth ? (IOnlineLookupProvider)HamQth : Qrz;

        // Every ILookupProvider that can contribute to a Build(call), in
        // priority order (earlier providers' fields win -- also the precedence
        // GetInfoForDialog uses, since it's a thin wrapper around Build()):
        // FCC ULS > Club Log > QRZ > LoTW. See the constructor below for why.
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

        // Derived from the running assembly's own name (not a literal "Jimmy") so a
        // differently-named build (e.g. a side-by-side "Jimmy Test" release) gets its own
        // isolated data folder automatically, rather than sharing the production install's
        // logbook database -- this is the root LogbookDb.cs's own DbPath builds from.
        public static string DataRoot =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Assembly.GetExecutingAssembly().GetName().Name, "Data");

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
            HamQth  = new HamQthProvider(root);
            LoTW    = new LoTWProvider(root);
            ClubLog = new ClubLogProvider(root);
            FccUls  = new FccUlsProvider(root);

            // Every provider below (except FccUls's State) fills a field only when
            // it's still empty, so list order is the priority order for shared
            // fields. Two independent priority findings combine here:
            //
            // ClubLog before Qrz: found via live A6 field testing 2026-07-16.
            // Club Log's Country/Continent/Dxcc/CqZone/Prefix come from its own
            // cty.php feed -- the same canonical CTY prefix database most ham radio
            // software (very likely including WSJT-X itself) is built on -- while
            // QRZ's Country field is just whatever free-text an operator typed into
            // their own profile. ClubLogProvider never populates Grid/Name/Email/
            // QslManager/State, so this has zero effect on those fields.
            //
            // Qrz before FccUls: Qrz's Name (the operator's own chosen on-air
            // display name) gets first claim on record.Name; FccUlsProvider only
            // overwrites it when its own record has strictly more name parts (see
            // FccUlsProvider.ShouldPreferName). FccUls's State is unaffected by
            // ordering -- it always overwrites State unconditionally when it has
            // one (the authoritative FCC-registered value), regardless of position.
            _providers.Add(ClubLog);
            _providers.Add(Qrz);
            // HamQth after Qrz: Nexus's own doc comment calls HamQTH "the free-account
            // fallback for QRZ" (tempo_core::hamqth) -- same idea here. This only fills
            // fields QRZ (and ClubLog, for Dxcc/CqZone/ItuZone) left blank; if HamQTH is
            // selected as PrimaryProvider instead of QRZ, this is still where its passively-
            // cached results get merged into Build() -- see PrimaryProvider's own comment for
            // why passive contribution and "the" primary provider are different concerns.
            _providers.Add(HamQth);
            _providers.Add(FccUls);
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
            int             qrzMinIntervalSeconds = 10,
            CallsignLookupProvider primaryProvider = CallsignLookupProvider.Qrz,
            bool            hamQthEnabled    = false, string hamQthUser = null, string hamQthPass = null,
            int             hamQthCacheDays  = 7)
        {
            _useLookupData         = useLookupData;
            _policy                = policy;
            _qrzMinIntervalSeconds = Math.Max(5, qrzMinIntervalSeconds);
            _primaryProviderChoice = primaryProvider;

            Qrz.Configure(qrzEnabled,     qrzUser,     qrzPass,     qrzCacheDays);
            HamQth.Configure(hamQthEnabled, hamQthUser, hamQthPass, hamQthCacheDays);
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
            if (hamQthEnabled) HamQth.PurgeOldEntries();
            if (fccUlsEnabled) FccUls.Load();

            StartAutoTimer();
        }

        public void StartBackgroundRefreshIfNeeded(int lotwDays, int clubLogDays, int fccUlsDays = 7)
        {
            if (ClubLog.NeedsRefresh(clubLogDays))
                _ = ClubLog.RefreshAsync();
            // Same "always on, no Use Lookup Data gate" treatment as the Club Log XML
            // refresh just above -- this is the alias/exception data FindByCallsign
            // needs to resolve prefixes correctly (see ClubLogProvider.cs), not a
            // user-facing lookup feature. Reuses the same refresh cadence.
            if (ClubLog.NeedsBigCtyRefresh(clubLogDays))
                _ = ClubLog.RefreshBigCtyAsync();

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

        // Offline-only merge: contributes from every registered provider EXCEPT the
        // four account-backed ones (QRZ/LoTW/FccUls/HamQth), regardless of the "Use
        // Lookup Data" master switch. In real use this means only ClubLog's bundled
        // Big CTY country data (always loaded -- see Initialize's unconditional
        // ClubLog.Configure/Load, and the "Club Log country data is Jimmy
        // infrastructure, not a user-facing lookup feature" comment there); a
        // caller-registered provider (RegisterProvider/RegisterProviderFirst, e.g.
        // tests' TestFixtureLookupProvider standing in for real cached data) also
        // still contributes. Used by ClassificationEngine/AwardTagger's built-in,
        // always-on DXCC/CqZone/Country/Continent classification (New DXCC, DXCC
        // Unconfirmed, Zone Needed, country, continent) -- NOT by the optional
        // rule-based Awards/Still Need system, which stays gated by Enabled exactly
        // as before, since QRZ/LoTW/FccUls/HamQth must never leak into classification
        // while the operator has "Use Lookup Data" unchecked.
        public LookupRecord BuildOffline(string call)
        {
            var record = new LookupRecord { Callsign = string.IsNullOrEmpty(call) ? call : call.ToUpperInvariant() };
            if (string.IsNullOrEmpty(call)) return record;

            foreach (var provider in _providers)
            {
                if (ReferenceEquals(provider, Qrz) || ReferenceEquals(provider, HamQth) ||
                    ReferenceEquals(provider, LoTW) || ReferenceEquals(provider, FccUls))
                    continue;
                if (provider.IsEnabled) provider.Contribute(record, call);
            }

            return record;
        }

        // ── Primary provider async (whichever of QRZ/HamQTH is selected) ─────────
        // Renamed from the earlier QRZ-only Lookup/Needs/CachedAt/Test methods now that
        // HamQTH is a real alternative -- see CallsignLookupProvider/PrimaryProvider above.
        // QRZ's own behavior is completely unchanged when it's the selected provider
        // (the default, matching every pre-existing installation).

        public Task<LookupRecord> LookupPrimaryAsync(string call) =>
            _useLookupData && PrimaryProvider.IsEnabled
                ? PrimaryProvider.LookupAsync(call)
                : Task.FromResult<LookupRecord>(null);

        public bool PrimaryNeedsLookup(string call) =>
            _useLookupData && PrimaryProvider.NeedsLookup(call);

        public DateTime? PrimaryCachedAt(string call) =>
            _useLookupData ? PrimaryProvider.GetCachedAt(call) : null;

        // Per-service "Test Login" buttons in Options test a SPECIFIC provider's
        // credentials, regardless of which one is currently primary -- these stay
        // provider-specific rather than routed through PrimaryProvider.
        public Task<bool> TestQrzAsync() => Qrz.TestAsync();
        public Task<bool> TestHamQthAsync() => HamQth.TestAsync();

        // ── Automatic primary-provider queue (UnidentifiedQueue policy) ──────────

        public bool CanAutoQueue(string call) =>
            _useLookupData &&
            _policy == QrzLookupPolicy.UnidentifiedQueue &&
            PrimaryProvider.IsEnabled &&
            PrimaryProvider.NeedsLookup(call);

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
            if (_policy != QrzLookupPolicy.UnidentifiedQueue || !PrimaryProvider.IsEnabled) return;
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
                var result = await PrimaryProvider.LookupAsync(call).ConfigureAwait(false);
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
