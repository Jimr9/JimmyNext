using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;

namespace WSJTX_Controller
{
    [Serializable]
    [XmlRoot("QrzCache")]
    public class QrzCacheFile
    {
        [XmlElement("Entry")]
        public List<QrzCacheEntry> Entries { get; set; } = new List<QrzCacheEntry>();
    }

    [Serializable]
    public class QrzCacheEntry
    {
        [XmlAttribute] public string Callsign   { get; set; }
        [XmlAttribute] public string Country    { get; set; }
        [XmlAttribute] public string State      { get; set; }
        [XmlAttribute] public string Grid       { get; set; }
        [XmlAttribute] public string Continent  { get; set; }
        [XmlAttribute] public string Name       { get; set; }
        [XmlAttribute] public string LicenseClass { get; set; }
        [XmlAttribute] public string County     { get; set; }
        [XmlAttribute] public string CqZone      { get; set; }
        [XmlAttribute] public string ItuZone     { get; set; }
        [XmlAttribute] public string QslManager { get; set; }
        [XmlAttribute] public string Email       { get; set; }
        [XmlAttribute] public string CachedAt  { get; set; }

        [XmlIgnore]
        public DateTime CachedAtDt
        {
            get { DateTime dt; return DateTime.TryParse(CachedAt, out dt) ? dt : DateTime.MinValue; }
        }
    }

    public class QrzProvider : ILookupProvider
    {
        private readonly string _cacheFile;
        private Dictionary<string, QrzCacheEntry> _cache =
            new Dictionary<string, QrzCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly object _cacheLock = new object();
        private string _sessionKey;
        private DateTime _sessionKeyExpiry = DateTime.MinValue;
        private readonly SemaphoreSlim _authLock    = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _rateLimiter = new SemaphoreSlim(1, 1);
        private static readonly HttpClient _http =
            new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly XmlSerializer _xmlSer =
            new XmlSerializer(typeof(QrzCacheFile));
        private bool _cacheLoaded;

        public string SourceName   => "QRZ";
        public bool   IsEnabled    { get; private set; }
        public string Username     { get; private set; } = "";
        public string LastError    { get; private set; }
        public string AuthCallsign { get; private set; }  // callsign confirmed by QRZ login
        private string _password  = "";
        private int    _cacheDays = 7;

        public QrzProvider(string dataRoot)
        {
            var dir = Path.Combine(dataRoot, "QRZ");
            Directory.CreateDirectory(dir);
            _cacheFile = Path.Combine(dir, "qrz_cache.xml");
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
                QrzCacheEntry e;
                if (!_cache.TryGetValue(call, out e)) return null;
                if ((DateTime.UtcNow - e.CachedAtDt).TotalDays >= _cacheDays) return null;
                return ToResult(e);
            }
        }

        // When this specific call was last actually fetched from QRZ (not just
        // displayed) -- lets a caller show how stale a cache hit is, e.g. the
        // Lookup dialog's status line. Null if not cached (or the cache entry has
        // aged out, same freshness rule as GetCached).
        public DateTime? GetCachedAt(string call)
        {
            if (!IsEnabled || string.IsNullOrEmpty(call)) return null;
            lock (_cacheLock)
            {
                QrzCacheEntry e;
                if (!_cache.TryGetValue(call, out e)) return null;
                if ((DateTime.UtcNow - e.CachedAtDt).TotalDays >= _cacheDays) return null;
                return e.CachedAtDt;
            }
        }

        // The most recent CachedAt across every cached entry -- CachedAt is only
        // ever set when a fresh network fetch actually happens (ParseResponse), not
        // on a cache-hit reuse, so this reflects "last time we actually talked to
        // QRZ," not just "last time any callsign was displayed."
        public DateTime? LastSuccessfulLookup
        {
            get
            {
                lock (_cacheLock)
                {
                    DateTime? latest = null;
                    foreach (var e in _cache.Values)
                    {
                        var dt = e.CachedAtDt;
                        if (dt != DateTime.MinValue && (latest == null || dt > latest.Value))
                            latest = dt;
                    }
                    return latest;
                }
            }
        }

        // Cache-only, synchronous -- safe for the per-decode hot path. Fills
        // whatever the cached entry has; leaves everything else on record alone.
        public void Contribute(LookupRecord record, string call)
        {
            var cached = GetCached(call);
            if (cached == null) return;

            if (string.IsNullOrEmpty(record.Name))         record.Name         = cached.Name;
            if (string.IsNullOrEmpty(record.LicenseClass))  record.LicenseClass = cached.LicenseClass;
            if (string.IsNullOrEmpty(record.Grid))       record.Grid       = cached.Grid;
            if (string.IsNullOrEmpty(record.State))      record.State      = cached.State;
            if (string.IsNullOrEmpty(record.Country))    record.Country    = cached.Country;
            if (string.IsNullOrEmpty(record.Continent))  record.Continent  = cached.Continent;
            if (string.IsNullOrEmpty(record.County))     record.County     = cached.County;
            if (record.CqZone == 0)                      record.CqZone     = cached.CqZone;
            if (record.ItuZone == 0)                      record.ItuZone    = cached.ItuZone;
            if (string.IsNullOrEmpty(record.QslManager)) record.QslManager = cached.QslManager;
            if (string.IsNullOrEmpty(record.Email))      record.Email      = cached.Email;
            record.Sources.Add(SourceName);
        }

        public bool NeedsLookup(string call)
        {
            if (!IsEnabled || string.IsNullOrEmpty(call)) return false;
            lock (_cacheLock)
            {
                QrzCacheEntry e;
                if (!_cache.TryGetValue(call, out e)) return true;
                return (DateTime.UtcNow - e.CachedAtDt).TotalDays >= _cacheDays;
            }
        }

        public async Task<LookupRecord> LookupAsync(string call)
        {
            if (!IsEnabled || string.IsNullOrEmpty(call)) return null;
            await _rateLimiter.WaitAsync().ConfigureAwait(false);
            try
            {
                var cached = GetCached(call);
                if (cached != null) return cached;

                string key = await GetSessionKeyAsync().ConfigureAwait(false);
                if (key == null) return null;

                string xml;
                try
                {
                    var url = $"https://xmldata.qrz.com/xml/current/?s={key}&callsign={Uri.EscapeDataString(call)}";
                    xml = await _http.GetStringAsync(url).ConfigureAwait(false);
                }
                catch (Exception ex) { LastError = ex.Message; return null; }

                var entry = ParseResponse(call, xml);
                if (entry != null)
                {
                    lock (_cacheLock) { _cache[entry.Callsign ?? call] = entry; }
                    SaveCache();
                    return ToResult(entry);
                }
                return null;
            }
            finally
            {
                await Task.Delay(150).ConfigureAwait(false);
                _rateLimiter.Release();
            }
        }

        public async Task<bool> TestAsync()
        {
            LastError      = null;
            AuthCallsign   = null;
            _sessionKey    = null;
            _sessionKeyExpiry = DateTime.MinValue;
            return await GetSessionKeyAsync().ConfigureAwait(false) != null;
        }

        private async Task<string> GetSessionKeyAsync()
        {
            // Sole choke point for every real network call this provider makes (both the
            // auth POST here and LookupAsync's own GET only proceed once this returns a real
            // key) -- gating here alone covers LookupAsync, TestAsync, and any future caller.
            if (TestModeGuard.IsTestMode)
            {
                LastError = "Blocked: JIMMY_TEST_DB_PATH is set (test mode) -- no real QRZ traffic allowed.";
                return null;
            }
            await _authLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_sessionKey != null && DateTime.UtcNow < _sessionKeyExpiry)
                    return _sessionKey;

                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("username", Username),
                    new KeyValuePair<string, string>("password", _password),
                    new KeyValuePair<string, string>("agent",    "Jimmy-HAM-Controller"),
                });
                string xml;
                try
                {
                    var resp = await _http.PostAsync("https://xmldata.qrz.com/xml/current/", content)
                                         .ConfigureAwait(false);
                    xml = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
                catch (Exception ex) { LastError = ex.Message; return null; }

                XmlDocument doc;
                try { doc = new XmlDocument(); doc.LoadXml(xml); }
                catch (Exception ex) { LastError = "QRZ XML parse error: " + ex.Message; return null; }

                var keyNode = doc.SelectSingleNode("//*[local-name()='Key']");
                if (keyNode != null && !string.IsNullOrEmpty(keyNode.InnerText.Trim()))
                {
                    _sessionKey       = keyNode.InnerText.Trim();
                    _sessionKeyExpiry = DateTime.UtcNow.AddHours(23);
                    LastError         = null;
                    var callNode = doc.SelectSingleNode("//*[local-name()='Call']");
                    AuthCallsign = callNode?.InnerText?.Trim();
                    return _sessionKey;
                }

                var errNode = doc.SelectSingleNode("//*[local-name()='Error']");
                LastError = errNode?.InnerText?.Trim() ?? "Unknown QRZ authentication error";
                return null;
            }
            finally { _authLock.Release(); }
        }

        private QrzCacheEntry ParseResponse(string call, string xml)
        {
            XmlDocument doc;
            try { doc = new XmlDocument(); doc.LoadXml(xml); }
            catch { return null; }

            var errNode = doc.SelectSingleNode("//*[local-name()='Error']");
            if (errNode != null)
            {
                var msg = errNode.InnerText?.Trim() ?? "";
                if (msg.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
                    return new QrzCacheEntry { Callsign = call.ToUpperInvariant(), CachedAt = DateTime.UtcNow.ToString("o") };
                if (msg.IndexOf("Session Timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    msg.IndexOf("Invalid session", StringComparison.OrdinalIgnoreCase) >= 0)
                    _sessionKey = null;
                LastError = msg;
                return null;
            }

            var callNode = doc.SelectSingleNode("//*[local-name()='call']");
            if (callNode == null) return null;

            return new QrzCacheEntry
            {
                Callsign   = (NodeText(doc, "call") ?? call).ToUpperInvariant(),
                Country    = NodeText(doc, "country"),
                State      = NodeText(doc, "state"),
                Grid       = NodeText(doc, "grid"),
                Continent  = NodeText(doc, "cont"),
                Name       = CombineName(NodeText(doc, "fname"), NodeText(doc, "name")),
                LicenseClass = NodeText(doc, "class"),
                County     = NodeText(doc, "county"),
                CqZone     = NodeText(doc, "cqzone"),
                ItuZone    = NodeText(doc, "ituzone"),
                QslManager = NodeText(doc, "qslmgr"),
                Email      = NodeText(doc, "email"),
                CachedAt   = DateTime.UtcNow.ToString("o"),
            };
        }

        // QRZ's XML API splits the operator's name into "fname" (first name,
        // sometimes including a middle initial) and "name" (last name) --
        // these must be joined, not treated as a fallback pair, or the last
        // name is silently dropped whenever a first name is present (i.e.
        // almost always).
        private static string CombineName(string first, string last)
        {
            if (string.IsNullOrEmpty(first)) return last;
            if (string.IsNullOrEmpty(last)) return first;
            return $"{first} {last}";
        }

        private static string NodeText(XmlDocument doc, string tag)
        {
            var n = doc.SelectSingleNode($"//*[local-name()='{tag}']");
            var t = n?.InnerText?.Trim();
            return string.IsNullOrEmpty(t) ? null : t;
        }

        private static LookupRecord ToResult(QrzCacheEntry e) => new LookupRecord
        {
            Callsign   = e.Callsign,
            Country    = e.Country,
            State      = e.State,
            Grid       = e.Grid,
            Continent  = e.Continent,
            Name       = e.Name,
            LicenseClass = e.LicenseClass,
            County     = e.County,
            CqZone     = ParseZone(e.CqZone),
            ItuZone    = ParseZone(e.ItuZone),
            QslManager = e.QslManager,
            Email      = e.Email,
        };

        private static int ParseZone(string s)
        {
            int z;
            return int.TryParse(s, out z) ? z : 0;
        }

        private void LoadCache()
        {
            try
            {
                if (!File.Exists(_cacheFile)) return;
                QrzCacheFile f;
                using (var r = new StreamReader(_cacheFile))
                    f = (QrzCacheFile)_xmlSer.Deserialize(r);
                lock (_cacheLock)
                    _cache = f.Entries
                        .Where(e => !string.IsNullOrEmpty(e.Callsign))
                        .ToDictionary(e => e.Callsign, e => e, StringComparer.OrdinalIgnoreCase);
            }
            catch { lock (_cacheLock) _cache = new Dictionary<string, QrzCacheEntry>(StringComparer.OrdinalIgnoreCase); }
        }

        public void SaveCache()
        {
            try
            {
                List<QrzCacheEntry> entries;
                lock (_cacheLock) entries = _cache.Values.ToList();
                using (var w = new StreamWriter(_cacheFile, false, System.Text.Encoding.UTF8))
                    _xmlSer.Serialize(w, new QrzCacheFile { Entries = entries });
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
