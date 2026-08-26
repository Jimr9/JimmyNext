using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace WSJTX_Controller
{
    [Serializable]
    [XmlRoot("HamQthCache")]
    public class HamQthCacheFile
    {
        [XmlElement("Entry")]
        public List<HamQthCacheEntry> Entries { get; set; } = new List<HamQthCacheEntry>();
    }

    [Serializable]
    public class HamQthCacheEntry
    {
        [XmlAttribute] public string Callsign { get; set; }
        [XmlAttribute] public string Country  { get; set; }
        [XmlAttribute] public string State    { get; set; }
        [XmlAttribute] public string Grid     { get; set; }
        [XmlAttribute] public string Name     { get; set; }
        [XmlAttribute] public string Dxcc     { get; set; }
        [XmlAttribute] public string CqZone   { get; set; }
        [XmlAttribute] public string ItuZone  { get; set; }
        [XmlAttribute] public string CachedAt { get; set; }

        [XmlIgnore]
        public DateTime CachedAtDt
        {
            get { DateTime dt; return DateTime.TryParse(CachedAt, out dt) ? dt : DateTime.MinValue; }
        }
    }

    // HamQTH callsign lookup, reached through EngineHost/Nexus (ExternalDataClient.LookupHamQth)
    // rather than a second direct HTTP client -- Nexus's tempo_core::hamqth already implements
    // the session-login + XML-parse logic well; this class only adds the caching/policy layer
    // Jimmy Next needs, the same role QrzProvider plays for QRZ. Deliberately the same on-disk
    // cache shape (own XML file under LookupManager.DataRoot\HamQTH\) and the same
    // Configure/GetCached/NeedsLookup/LookupAsync/Contribute contract as QrzProvider, so
    // LookupManager's existing automatic-lookup/policy/queue machinery (built once for QRZ) can
    // operate against either provider interchangeably via IOnlineLookupProvider -- see
    // ARCHITECTURE.md's "HamQTH as a primary lookup provider" section for why this mirrors
    // QrzProvider's shape instead of introducing a different pattern.
    //
    // No session-id caching across calls (unlike QrzProvider's _sessionKey): each lookup
    // re-logs-in through EngineHost's combined HAMQTH_LOOKUP command (see external_data.rs's
    // own comment on this choice -- simpler and more robust than threading HamQTH's ~1h session
    // lifetime through a second process for what remains an occasional operator-driven or
    // queue-supplement lookup, not a per-decode hot path).
    public class HamQthProvider : IOnlineLookupProvider
    {
        private readonly string _cacheFile;
        private Dictionary<string, HamQthCacheEntry> _cache =
            new Dictionary<string, HamQthCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly object _cacheLock = new object();
        private readonly SemaphoreSlim _rateLimiter = new SemaphoreSlim(1, 1);
        private static readonly XmlSerializer _xmlSer =
            new XmlSerializer(typeof(HamQthCacheFile));
        private bool _cacheLoaded;

        public string SourceName   => "HamQTH";
        public bool   IsEnabled    { get; private set; }
        public string Username     { get; private set; } = "";
        public string LastError    { get; private set; }
        private string _password  = "";
        private int    _cacheDays = 7;

        public HamQthProvider(string dataRoot)
        {
            var dir = Path.Combine(dataRoot, "HamQTH");
            Directory.CreateDirectory(dir);
            _cacheFile = Path.Combine(dir, "hamqth_cache.xml");
        }

        public void Configure(bool enabled, string username, string password, int cacheDays)
        {
            IsEnabled  = enabled && !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password);
            Username   = username  ?? "";
            _password  = password  ?? "";
            _cacheDays = Math.Max(1, cacheDays);
            if (!_cacheLoaded) { LoadCache(); _cacheLoaded = true; }
        }

        public LookupRecord GetCached(string call)
        {
            if (!IsEnabled || string.IsNullOrEmpty(call)) return null;
            lock (_cacheLock)
            {
                HamQthCacheEntry e;
                if (!_cache.TryGetValue(call, out e)) return null;
                if ((DateTime.UtcNow - e.CachedAtDt).TotalDays >= _cacheDays) return null;
                return ToResult(e);
            }
        }

        public DateTime? GetCachedAt(string call)
        {
            if (!IsEnabled || string.IsNullOrEmpty(call)) return null;
            lock (_cacheLock)
            {
                HamQthCacheEntry e;
                if (!_cache.TryGetValue(call, out e)) return null;
                if ((DateTime.UtcNow - e.CachedAtDt).TotalDays >= _cacheDays) return null;
                return e.CachedAtDt;
            }
        }

        // Cache-only, synchronous -- safe for the per-decode hot path, same contract as
        // QrzProvider.Contribute. Numeric dxcc/cq_zone/itu_zone only fill blanks (matches
        // every other field here); ClubLogProvider stays the authoritative source for those
        // when it has already resolved them (list order in LookupManager's constructor).
        public void Contribute(LookupRecord record, string call)
        {
            var cached = GetCached(call);
            if (cached == null) return;

            if (string.IsNullOrEmpty(record.Name))      record.Name      = cached.Name;
            if (string.IsNullOrEmpty(record.Grid))      record.Grid      = cached.Grid;
            if (string.IsNullOrEmpty(record.State))     record.State     = cached.State;
            if (string.IsNullOrEmpty(record.Country))   record.Country   = cached.Country;
            // ClubLogProvider is the authoritative Dxcc source (see LookupManager's provider
            // order comment) -- this only fills the rare case it left Dxcc unresolved.
            if (record.Dxcc == 0)                        record.Dxcc     = cached.Dxcc;
            if (record.CqZone == 0)                      record.CqZone   = cached.CqZone;
            if (record.ItuZone == 0)                      record.ItuZone  = cached.ItuZone;
            record.Sources.Add(SourceName);
        }

        public bool NeedsLookup(string call)
        {
            if (!IsEnabled || string.IsNullOrEmpty(call)) return false;
            lock (_cacheLock)
            {
                HamQthCacheEntry e;
                if (!_cache.TryGetValue(call, out e)) return true;
                return (DateTime.UtcNow - e.CachedAtDt).TotalDays >= _cacheDays;
            }
        }

        public async Task<LookupRecord> LookupAsync(string call)
        {
            if (!IsEnabled || string.IsNullOrEmpty(call)) return null;
            if (TestModeGuard.IsTestMode)
            {
                LastError = "Blocked: JIMMY_TEST_DB_PATH is set (test mode) -- no real HamQTH traffic allowed.";
                return null;
            }
            await _rateLimiter.WaitAsync().ConfigureAwait(false);
            try
            {
                var cached = GetCached(call);
                if (cached != null) return cached;

                var client = new ExternalDataClient();
                HamQthLookupResult result = null;
                string error = null;
                await Task.Run(() => { result = client.LookupHamQth(Username, _password, call, out error); }).ConfigureAwait(false);

                if (result == null)
                {
                    LastError = error;
                    return null;
                }

                var entry = new HamQthCacheEntry
                {
                    Callsign = (result.Call ?? call).ToUpperInvariant(),
                    Country  = result.Country,
                    State    = result.State,
                    Grid     = result.Grid,
                    Name     = result.Name,
                    Dxcc     = result.Dxcc?.ToString() ?? "",
                    CqZone   = result.CqZone?.ToString() ?? "",
                    ItuZone  = result.ItuZone?.ToString() ?? "",
                    CachedAt = DateTime.UtcNow.ToString("o"),
                };
                lock (_cacheLock) { _cache[entry.Callsign] = entry; }
                SaveCache();
                LastError = null;
                return ToResult(entry);
            }
            finally
            {
                await Task.Delay(150).ConfigureAwait(false);
                _rateLimiter.Release();
            }
        }

        public async Task<bool> TestAsync()
        {
            LastError = null;
            if (TestModeGuard.IsTestMode)
            {
                LastError = "Blocked: JIMMY_TEST_DB_PATH is set (test mode) -- no real HamQTH traffic allowed.";
                return false;
            }
            var client = new ExternalDataClient();
            bool ok = false;
            string error = null;
            await Task.Run(() => { ok = client.TestHamQth(Username, _password, out error); }).ConfigureAwait(false);
            LastError = error;
            return ok;
        }

        private static LookupRecord ToResult(HamQthCacheEntry e) => new LookupRecord
        {
            Callsign = e.Callsign,
            Country  = e.Country,
            State    = e.State,
            Grid     = e.Grid,
            Name     = e.Name,
            Dxcc     = ParseInt(e.Dxcc),
            CqZone   = ParseInt(e.CqZone),
            ItuZone  = ParseInt(e.ItuZone),
        };

        private static int ParseInt(string s)
        {
            int v;
            return int.TryParse(s, out v) ? v : 0;
        }

        private void LoadCache()
        {
            try
            {
                if (!File.Exists(_cacheFile)) return;
                HamQthCacheFile f;
                using (var r = new StreamReader(_cacheFile))
                    f = (HamQthCacheFile)_xmlSer.Deserialize(r);
                lock (_cacheLock)
                    _cache = f.Entries
                        .Where(e => !string.IsNullOrEmpty(e.Callsign))
                        .ToDictionary(e => e.Callsign, e => e, StringComparer.OrdinalIgnoreCase);
            }
            catch { lock (_cacheLock) _cache = new Dictionary<string, HamQthCacheEntry>(StringComparer.OrdinalIgnoreCase); }
        }

        public void SaveCache()
        {
            try
            {
                List<HamQthCacheEntry> entries;
                lock (_cacheLock) entries = _cache.Values.ToList();
                using (var w = new StreamWriter(_cacheFile, false, System.Text.Encoding.UTF8))
                    _xmlSer.Serialize(w, new HamQthCacheFile { Entries = entries });
            }
            catch { }
        }

        public void PurgeOldEntries()
        {
            if (!_cacheLoaded) return;
            var cutoff = DateTime.UtcNow.AddDays(-_cacheDays * 3);
            lock (_cacheLock)
            {
                var old = _cache.Where(kv => kv.Value.CachedAtDt < cutoff)
                                .Select(kv => kv.Key).ToList();
                foreach (var k in old) _cache.Remove(k);
            }
        }
    }
}
