using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml;

namespace WSJTX_Controller
{
    public class ClubLogEntity
    {
        public string Name      { get; set; }
        public string Prefix    { get; set; }
        public string Continent { get; set; }
        public int    CqZone    { get; set; }
        public int    Adif      { get; set; }
        public bool   Deleted   { get; set; }
    }

    public class ClubLogProvider : ILookupProvider
    {
        private readonly string _dir;
        private readonly string _dataFile;
        private readonly string _metaFile;
        // AllEntities/EntityCount (RuleUniverse.cs's award-rule DXCC universe lists,
        // Controller.cs's own entity enumeration) stay backed by this flat list --
        // one row per entity, each carrying only its single default prefix. This is
        // NOT sufficient for per-callsign classification (see _prefixToAdif below).
        private List<ClubLogEntity> _entities = new List<ClubLogEntity>();
        // Found via live A6 field testing 2026-07-16: <entities><entity><prefix> is
        // only ONE default prefix per entity -- e.g. UNITED STATES OF AMERICA's own
        // entity record lists just "K", missing N/W/AA-AL and every US territory's
        // own prefixes (Puerto Rico's NP4/KP4, etc.), so real callsigns like "NP4TX"
        // never matched anything via the old entities-only FindByCallsign. Club Log's
        // actual published cty.xml also has <prefixes> (the comprehensive prefix-to-
        // entity table, confirmed via a real cached download: 4122+ records, e.g.
        // NP4 -> PUERTO RICO) and <exceptions> (exact full-callsign overrides) --
        // FindByCallsign now consults these first, keyed by Adif into
        // _entitiesByAdif so Deleted/Name/Continent/CqZone still come from the
        // canonical entity record.
        private Dictionary<int, ClubLogEntity> _entitiesByAdif = new Dictionary<int, ClubLogEntity>();
        private Dictionary<string, int> _prefixToAdif = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _exceptionToAdif = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly HttpClient _http =
            new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        // Must be the cdn subdomain -- clublog.org/cty.php now rejects direct
        // requests ("Unsupported address... requests for this file are made to
        // https://cdn.clublog.org/").
        private const string BaseUrl = "https://cdn.clublog.org/cty.php";

        public string   SourceName  => "Club Log";
        public bool     IsEnabled   { get; private set; }
        public string   LastError   { get; private set; }
        public DateTime LastUpdate  { get; private set; }
        public int      EntityCount => _entities.Count;
        public IReadOnlyList<ClubLogEntity> AllEntities => _entities;
        private string _apiKey = "";

        public ClubLogProvider(string dataRoot)
        {
            _dir      = Path.Combine(dataRoot, "ClubLog");
            _dataFile = Path.Combine(_dir, "clublog_cty.xml");
            _metaFile = Path.Combine(_dir, "metadata.txt");
            Directory.CreateDirectory(_dir);
        }

        public void Configure(bool enabled, string apiKey)
        {
            IsEnabled = enabled;
            _apiKey   = apiKey ?? "";
        }

        public void Load()
        {
            LastUpdate = ReadMeta();
            if (File.Exists(_dataFile)) ParseFile(_dataFile);
        }

        public bool NeedsRefresh(int days) =>
            !File.Exists(_dataFile) || (DateTime.UtcNow - LastUpdate).TotalDays >= days;

        public async Task<bool> RefreshAsync()
        {
            LastError = null;
            if (TestModeGuard.IsTestMode)
            {
                LastError = "Blocked: JIMMY_TEST_DB_PATH is set (test mode) -- no real Club Log traffic allowed.";
                return false;
            }
            var url = string.IsNullOrWhiteSpace(_apiKey)
                ? BaseUrl
                : $"{BaseUrl}?api={Uri.EscapeDataString(_apiKey)}";
            var tmp = Path.Combine(_dir, "clublog_cty.tmp");
            try
            {
                var bytes = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
                var data = DecodeResponse(bytes);
                if (data.TrimStart().StartsWith("Error") || data.Length < 200)
                {
                    LastError = Redact(data.Length < 500 ? data.Trim() : "Club Log returned unexpected response.");
                    return false;
                }
                File.WriteAllText(tmp, data, System.Text.Encoding.UTF8);
                File.Copy(tmp, _dataFile, overwrite: true);
                try { File.Delete(tmp); } catch { }
                LastUpdate = DateTime.UtcNow;
                WriteMeta(LastUpdate);
                ParseFile(_dataFile);
                return _entities.Count > 0;
            }
            catch (Exception ex)
            {
                LastError = Redact(ex.Message);
                try { File.Delete(tmp); } catch { }
                return false;
            }
        }

        // The API key is a Jimmy application secret, not a user credential -- it
        // must never reach the UI or a log file, including inside an error message
        // (e.g. an HttpRequestException that happens to echo the request URI).
        private string Redact(string text) =>
            string.IsNullOrEmpty(text) || string.IsNullOrEmpty(_apiKey)
                ? text
                : text.Replace(_apiKey, "[REDACTED]");

        // cty.php serves the file gzip-compressed (magic bytes 1F 8B) with the
        // original filename ("cty.xml") embedded in the gzip header -- decompress
        // before treating it as text. Falls back to plain UTF-8 decoding in case
        // Club Log ever serves it uncompressed.
        private static string DecodeResponse(byte[] bytes)
        {
            if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
            {
                using (var compressed = new MemoryStream(bytes))
                using (var gzip = new GZipStream(compressed, CompressionMode.Decompress))
                using (var reader = new StreamReader(gzip, System.Text.Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        public ClubLogEntity FindByPrefix(string prefix)
        {
            if (!IsEnabled || string.IsNullOrEmpty(prefix)) return null;
            foreach (var e in _entities)
            {
                if (!e.Deleted &&
                    string.Equals(e.Prefix, prefix, StringComparison.OrdinalIgnoreCase))
                    return e;
            }
            return null;
        }

        // 1) Exact full-callsign override (Club Log's <exceptions> table -- e.g. a
        //    special-event or portable operation whose true DXCC entity doesn't match
        //    what its prefix would normally imply).
        // 2) Longest-prefix match against Club Log's full <prefixes> table (tries the
        //    full callsign, then progressively shorter prefixes, e.g.
        //    "NP4TX" -> "NP4T" -> "NP4" -> "NP" -> "N", until a match is found) --
        //    comprehensive: every valid prefix for every entity, not just each
        //    entity's single <entities><entity><prefix> default.
        // 3) Last-resort fallback: the original entities-only longest-prefix match,
        //    kept in case some entity genuinely isn't represented in <prefixes> for
        //    some reason (also the only path available for the legacy plain-text CTY
        //    format, which ParseCtyText doesn't populate _prefixToAdif/_exceptionToAdif
        //    for).
        public ClubLogEntity FindByCallsign(string call)
        {
            if (!IsEnabled || string.IsNullOrEmpty(call)) return null;
            call = call.ToUpperInvariant();

            if (_exceptionToAdif.TryGetValue(call, out int exceptionAdif) &&
                _entitiesByAdif.TryGetValue(exceptionAdif, out ClubLogEntity exceptionEntity) &&
                !exceptionEntity.Deleted)
                return exceptionEntity;

            for (int len = call.Length; len >= 1; len--)
            {
                string candidate = call.Substring(0, len);
                if (_prefixToAdif.TryGetValue(candidate, out int prefixAdif) &&
                    _entitiesByAdif.TryGetValue(prefixAdif, out ClubLogEntity prefixEntity) &&
                    !prefixEntity.Deleted)
                    return prefixEntity;
            }

            if (_entities.Count == 0) return null;
            for (int len = call.Length; len >= 1; len--)
            {
                string candidate = call.Substring(0, len);
                foreach (var e in _entities)
                {
                    if (!e.Deleted &&
                        string.Equals(e.Prefix, candidate, StringComparison.OrdinalIgnoreCase))
                        return e;
                }
            }
            return null;
        }

        // Synchronous, offline (FindByCallsign only reads already-downloaded
        // cty.xml data) -- safe for the per-decode hot path.
        public void Contribute(LookupRecord record, string call)
        {
            var entity = FindByCallsign(call);
            if (entity == null) return;

            if (string.IsNullOrEmpty(record.Country))   record.Country   = entity.Name;
            if (record.Dxcc == 0)                       record.Dxcc      = entity.Adif;
            if (string.IsNullOrEmpty(record.Continent)) record.Continent = entity.Continent;
            if (record.CqZone == 0)                     record.CqZone    = entity.CqZone;
            if (string.IsNullOrEmpty(record.Prefix))    record.Prefix    = entity.Prefix;
            // FindByCallsign only ever matches non-deleted entities (see its own
            // filter), so this is always false via this path today -- kept
            // faithful to the data rather than hardcoded, in case that changes.
            record.IsDeletedEntity = entity.Deleted;
            record.Sources.Add(SourceName);
        }

        private void ParseFile(string path)
        {
            try
            {
                var content = File.ReadAllText(path, System.Text.Encoding.UTF8).TrimStart();
                if (content.StartsWith("<"))
                    ParseXml(content);
                else
                    ParseCtyText(content);
            }
            catch (Exception ex)
            {
                LastError = "Parse error: " + ex.Message;
                _entities = new List<ClubLogEntity>();
            }
        }

        private void ParseXml(string xml)
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var result = new List<ClubLogEntity>();

            // SelectNodes returns an empty (non-null) list when nothing matches,
            // never null -- so a plain "?? " fallback here would never trigger.
            // Real Club Log data uses lower-case <entity> tags exclusively.
            var entityNodes = doc.SelectNodes("//*[local-name()='ENTITY']");
            if (entityNodes == null || entityNodes.Count == 0)
                entityNodes = doc.SelectNodes("//*[local-name()='entity']");
            if (entityNodes != null)
            {
                foreach (XmlNode n in entityNodes)
                {
                    var name = Child(n, "NAME") ?? Child(n, "name");
                    if (string.IsNullOrEmpty(name)) continue;
                    var e = new ClubLogEntity
                    {
                        Name      = name,
                        Prefix    = Child(n, "PREFIX")  ?? Child(n, "prefix"),
                        Continent = Child(n, "CONT")    ?? Child(n, "cont"),
                        Deleted   = string.Equals(Child(n, "DELETED") ?? Child(n, "deleted"),
                                                  "TRUE", StringComparison.OrdinalIgnoreCase),
                    };
                    int v;
                    int.TryParse(Child(n, "ADIF") ?? Child(n, "adif"), out v); e.Adif   = v;
                    int.TryParse(Child(n, "CQ")   ?? Child(n, "cq"),   out v); e.CqZone = v;
                    result.Add(e);
                }
            }

            if (result.Count > 0)
            {
                _entities = result;

                var byAdif = new Dictionary<int, ClubLogEntity>();
                foreach (var e in result)
                    if (e.Adif > 0 && !byAdif.ContainsKey(e.Adif))
                        byAdif[e.Adif] = e;
                _entitiesByAdif = byAdif;

                _prefixToAdif = ParseCallToAdifTable(doc, "prefixes", "prefix");
                _exceptionToAdif = ParseCallToAdifTable(doc, "exceptions", "exception");
            }
            else
                LastError = "Club Log XML parsed but no entities found; format may have changed.";
        }

        // Parses Club Log's <prefixes>/<exceptions> tables into a call/prefix -> Adif
        // lookup. Both sections can carry more than one historical record for the same
        // <call> (e.g. "DL" belonged to a pre-1973 "GERMANY" entity before today's
        // "FEDERAL REPUBLIC OF GERMANY" assignment) -- a record with no <end> date, or
        // an <end> date that hasn't passed yet, is preferred over one that's expired,
        // so classification reflects the currently-valid assignment rather than
        // whichever happened to parse last.
        private static Dictionary<string, int> ParseCallToAdifTable(XmlDocument doc, string containerTag, string recordTag)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var resultIsCurrent = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            var container = doc.SelectSingleNode($"//*[local-name()='{containerTag}']");
            var records = container?.SelectNodes($"*[local-name()='{recordTag}']");
            if (records == null) return result;

            DateTime now = DateTime.UtcNow;
            foreach (XmlNode rec in records)
            {
                string call = Child(rec, "call");
                if (string.IsNullOrEmpty(call)) continue;
                if (!int.TryParse(Child(rec, "adif"), out int adif) || adif <= 0) continue;

                string endText = Child(rec, "end");
                bool isCurrent = string.IsNullOrEmpty(endText)
                    || !DateTime.TryParse(endText, out DateTime end)
                    || end >= now;

                if (!result.ContainsKey(call) || (isCurrent && !resultIsCurrent[call]))
                {
                    result[call] = adif;
                    resultIsCurrent[call] = isCurrent;
                }
            }
            return result;
        }

        private void ParseCtyText(string text)
        {
            // Standard Big CTY format
            var result = new List<ClubLogEntity>();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r').Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith(" ")) continue;
                // Entity header: "Name:  cqz, ituz, cont, cap, prefix, adif, *flag:"
                var colon = line.IndexOf(':');
                if (colon < 1) continue;
                var name   = line.Substring(0, colon).Trim();
                var fields = line.Substring(colon + 1).TrimEnd(':').Split(',');
                var e = new ClubLogEntity { Name = name };
                if (fields.Length >= 3) e.Continent = fields[2].Trim();
                if (fields.Length >= 5) e.Prefix    = fields[4].Trim().TrimStart('*');
                int v;
                if (fields.Length >= 1 && int.TryParse(fields[0].Trim(), out v)) e.CqZone = v;
                if (fields.Length >= 6 && int.TryParse(fields[5].Trim().TrimStart('*'), out v)) e.Adif = v;
                if (!string.IsNullOrEmpty(name)) result.Add(e);
            }

            if (result.Count > 0)
                _entities = result;
            else
                LastError = "Club Log CTY text parsed but no entities found.";
        }

        private static string Child(XmlNode parent, string tag)
        {
            var n = parent.SelectSingleNode($"*[local-name()='{tag}']");
            var t = n?.InnerText?.Trim();
            return string.IsNullOrEmpty(t) ? null : t;
        }

        private DateTime ReadMeta()
        {
            try
            {
                if (!File.Exists(_metaFile)) return DateTime.MinValue;
                DateTime dt;
                return DateTime.TryParse(File.ReadAllText(_metaFile).Trim(), out dt)
                    ? dt : DateTime.MinValue;
            }
            catch { return DateTime.MinValue; }
        }

        private void WriteMeta(DateTime dt)
        {
            try { File.WriteAllText(_metaFile, dt.ToString("o")); } catch { }
        }
    }
}
