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
        // isCurrent alongside each Adif: some <call> values have only ONE historical
        // <prefixes>/<exceptions> record, already expired, with no separate current-
        // day record at all (e.g. "K4" -> PUERTO RICO, <end>1946-12-31 -- a pre-war
        // assignment, long superseded by the modern US call-area system, with no
        // newer "K4" record to prefer it over). Found via live A6 field testing
        // 2026-07-16: K4YT and other real K4-prefixed US stations were being
        // misclassified as Puerto Rico because FindByCallsign's longest-prefix search
        // matched this expired record and returned immediately, never falling
        // through to the correct shorter/current "K" -> USA match. isCurrent lets
        // FindByCallsign skip an expired record and keep searching shorter prefixes,
        // rather than only comparing currency among same-key duplicates as before.
        private Dictionary<string, (int Adif, bool IsCurrent)> _prefixToAdif =
            new Dictionary<string, (int, bool)>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, (int Adif, bool IsCurrent)> _exceptionToAdif =
            new Dictionary<string, (int, bool)>(StringComparer.OrdinalIgnoreCase);
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

        // ── Big CTY alias/exception data ─────────────────────────────────────────
        // clublog_cty.xml above stores exactly one representative prefix per DXCC
        // entity (e.g. USA = "K"), so FindByCallsign historically could only ever
        // match a callsign whose prefix happened to be that one chosen prefix --
        // every other USA prefix (N, W, ...) silently resolved to nothing, and
        // ambiguous prefixes like "KG4" (shared between Guantanamo Bay and ordinary
        // FCC-sequential USA calls) matched the wrong entity outright. This second,
        // independent file is AD1C's real "Big CTY" country file -- the same one
        // WSJT-X's own User Guide directs users to for updating its bundled cty.dat
        // -- which carries the full alias/exception list per entity. It supplies
        // ONLY the callsign->main-prefix resolution; Name/Adif/Continent/Deleted
        // still come from clublog_cty.xml via FindByPrefix, unchanged.
        private readonly string _bigCtyFile;
        private readonly string _bigCtyMetaFile;
        private const string BigCtyUrl = "https://www.country-files.com/bigcty/cty.dat";
        private Dictionary<string, (string MainPrefix, int? Zone)> _bigCtyPrefixes   =
            new Dictionary<string, (string MainPrefix, int? Zone)>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, (string MainPrefix, int? Zone)> _bigCtyExceptions =
            new Dictionary<string, (string MainPrefix, int? Zone)>(StringComparer.OrdinalIgnoreCase);

        public DateTime BigCtyLastUpdate  { get; private set; }
        public int      BigCtyAliasCount  => _bigCtyPrefixes.Count + _bigCtyExceptions.Count;

        public ClubLogProvider(string dataRoot)
        {
            _dir      = Path.Combine(dataRoot, "ClubLog");
            _dataFile = Path.Combine(_dir, "clublog_cty.xml");
            _metaFile = Path.Combine(_dir, "metadata.txt");
            _bigCtyFile     = Path.Combine(_dir, "bigcty.dat");
            _bigCtyMetaFile = Path.Combine(_dir, "bigcty_metadata.txt");
            Directory.CreateDirectory(_dir);
        }

        public void Configure(bool enabled, string apiKey)
        {
            IsEnabled = enabled;
            _apiKey   = apiKey ?? "";
        }

        public void Load()
        {
            LastUpdate = ReadMeta(_metaFile);
            if (File.Exists(_dataFile)) ParseFile(_dataFile);
            BigCtyLastUpdate = ReadMeta(_bigCtyMetaFile);
            if (File.Exists(_bigCtyFile)) LoadBigCty();
        }

        public bool NeedsRefresh(int days) =>
            !File.Exists(_dataFile) || (DateTime.UtcNow - LastUpdate).TotalDays >= days;

        public bool NeedsBigCtyRefresh(int days) =>
            !File.Exists(_bigCtyFile) || (DateTime.UtcNow - BigCtyLastUpdate).TotalDays >= days;

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
                WriteMeta(_metaFile, LastUpdate);
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

        // Downloads AD1C's real Big CTY country file (plain text, no auth, no API
        // key/rate-limit concerns unlike Club Log's own API above) and parses it
        // into the alias/exception dictionaries FindByCallsign uses. Independent
        // of RefreshAsync()/clublog_cty.xml -- a failure here never touches the
        // existing entity list, and vice versa.
        public async Task<bool> RefreshBigCtyAsync()
        {
            LastError = null;
            if (TestModeGuard.IsTestMode)
            {
                LastError = "Blocked: JIMMY_TEST_DB_PATH is set (test mode) -- no real Big CTY traffic allowed.";
                return false;
            }
            var tmp = Path.Combine(_dir, "bigcty.tmp");
            try
            {
                var text = await _http.GetStringAsync(BigCtyUrl).ConfigureAwait(false);
                if (text.Length < 200)
                {
                    LastError = "Big CTY returned unexpected (too short) response.";
                    return false;
                }
                File.WriteAllText(tmp, text, System.Text.Encoding.UTF8);
                File.Copy(tmp, _bigCtyFile, overwrite: true);
                try { File.Delete(tmp); } catch { }
                BigCtyLastUpdate = DateTime.UtcNow;
                WriteMeta(_bigCtyMetaFile, BigCtyLastUpdate);
                LoadBigCty();
                return BigCtyAliasCount > 0;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                try { File.Delete(tmp); } catch { }
                return false;
            }
        }

        private void LoadBigCty()
        {
            try
            {
                ParseBigCtyText(File.ReadAllText(_bigCtyFile, System.Text.Encoding.UTF8));
            }
            catch (Exception ex)
            {
                LastError = "Big CTY parse error: " + ex.Message;
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

        // Resolves a callsign to its entity. Preferred source is AD1C's Big CTY
        // alias/exception data (real per-callarea prefixes + individually-catalogued
        // exceptions, see ResolveMainPrefix); when that hasn't been downloaded yet
        // (fresh install, still refreshing in the background) or doesn't resolve
        // anything for this call, falls back to Club Log's own exceptions/prefixes
        // tables:
        // 1) Exact full-callsign override (Club Log's <exceptions> table -- e.g. a
        //    special-event or portable operation whose true DXCC entity doesn't match
        //    what its prefix would normally imply). Only a CURRENT exception counts
        //    here -- an expired one falls through to prefix matching like any other
        //    stale record (see the isCurrent comment on _prefixToAdif above).
        // 2) A single combined longest-to-shortest pass over both Club Log's full
        //    <prefixes> table (comprehensive: every valid prefix for every entity,
        //    not just each entity's single <entities><entity><prefix> default) and
        //    the entities-only default-prefix list, at each length -- so a CURRENT
        //    match at a shorter length (e.g. "K" -> USA) is found and returned before
        //    an EXPIRED match at a longer length (e.g. "K4" -> PUERTO RICO, expired
        //    1946) ever gets a chance to win by virtue of being checked first.
        // 3) Only if no current match was found at any length: fall back to the
        //    longest expired <prefixes> match, then the exact expired <exceptions>
        //    match -- a stale historical guess is still better than no classification
        //    at all when nothing current applies.
        public ClubLogEntity FindByCallsign(string call)
        {
            if (!IsEnabled || string.IsNullOrEmpty(call)) return null;
            call = call.ToUpperInvariant();

            if (BigCtyAliasCount > 0)
            {
                var resolved = ResolveMainPrefix(call);
                if (resolved.MainPrefix != null)
                {
                    var byPrefix = FindByPrefix(resolved.MainPrefix);
                    if (byPrefix != null)
                    {
                        if (!resolved.Zone.HasValue || resolved.Zone.Value == byPrefix.CqZone)
                            return byPrefix;

                        // Per-callarea zone override (e.g. USA's K0-K9 call areas span
                        // CQ zones 3/4/5) -- return a shallow copy so the shared cached
                        // entity instance is never mutated for other callers/lookups.
                        return new ClubLogEntity
                        {
                            Name      = byPrefix.Name,
                            Prefix    = byPrefix.Prefix,
                            Continent = byPrefix.Continent,
                            CqZone    = resolved.Zone.Value,
                            Adif      = byPrefix.Adif,
                            Deleted   = byPrefix.Deleted,
                        };
                    }
                }
            }

            if (_exceptionToAdif.TryGetValue(call, out var exceptionEntry) &&
                exceptionEntry.IsCurrent &&
                _entitiesByAdif.TryGetValue(exceptionEntry.Adif, out ClubLogEntity exceptionEntity) &&
                !exceptionEntity.Deleted)
                return exceptionEntity;

            ClubLogEntity expiredPrefixFallback = null;
            for (int len = call.Length; len >= 1; len--)
            {
                string candidate = call.Substring(0, len);

                if (_prefixToAdif.TryGetValue(candidate, out var prefixEntry) &&
                    _entitiesByAdif.TryGetValue(prefixEntry.Adif, out ClubLogEntity prefixEntity) &&
                    !prefixEntity.Deleted)
                {
                    if (prefixEntry.IsCurrent) return prefixEntity;
                    if (expiredPrefixFallback == null) expiredPrefixFallback = prefixEntity;
                }

                foreach (var e in _entities)
                {
                    if (!e.Deleted &&
                        string.Equals(e.Prefix, candidate, StringComparison.OrdinalIgnoreCase))
                        return e;
                }
            }

            if (expiredPrefixFallback != null) return expiredPrefixFallback;

            if (_exceptionToAdif.TryGetValue(call, out var expiredException) &&
                _entitiesByAdif.TryGetValue(expiredException.Adif, out ClubLogEntity expiredExceptionEntity) &&
                !expiredExceptionEntity.Deleted)
                return expiredExceptionEntity;

            return null;
        }

        // Resolves a callsign to the "main prefix" string clublog_cty.xml's own
        // per-entity Prefix field uses (e.g. "K" for USA, "KG4" for Guantanamo Bay),
        // using the Big CTY alias/exception data. Order matters:
        //   1. An exact-callsign exception always wins (real, maintained data --
        //      e.g. a specific historic Guantanamo Bay operator's callsign).
        //   2. The KG4 format rule (see ResolveKg4Prefix) -- only reached once step 1
        //      has already ruled out a specific catalogued exception for this call.
        //   3. Longest-prefix match against the general per-callarea alias table.
        private (string MainPrefix, int? Zone) ResolveMainPrefix(string call)
        {
            (string MainPrefix, int? Zone) exc;
            if (_bigCtyExceptions.TryGetValue(call, out exc)) return exc;

            int slash = call.IndexOf('/');
            string noSuffix = slash >= 0 ? call.Substring(0, slash) : call;
            string kg4 = ResolveKg4Prefix(noSuffix);
            if (kg4 != null) return (kg4, null);

            for (int len = call.Length; len >= 1; len--)
            {
                (string MainPrefix, int? Zone) pfx;
                if (_bigCtyPrefixes.TryGetValue(call.Substring(0, len), out pfx)) return pfx;
            }
            return (null, null);
        }

        // KG4 is the one DXCC prefix genuinely split by callsign *format* rather
        // than by which specific calls it's been issued to: Guantanamo Bay (ADIF
        // 105) only ever uses KG4 + exactly two letters (e.g. KG4AB); every other
        // KG4 format (KG4W, KG4JOK, KG4ABCD, ...) is an ordinary FCC-sequential-
        // block USA call. Real cty.dat only models this via a hand-maintained list
        // of individual =callsign exceptions (see ResolveMainPrefix step 1), which
        // can never be complete for a callsign not yet catalogued -- this is the
        // real-world case that motivated this fix (KG4JOK, a live USA station, had
        // no such exception entry yet and was misrouted to Guantanamo Bay). This is
        // a callsign-format boundary permanently fixed by ADIF/DXCC, the same rule
        // every mature DXCC/contest logger (N1MM, DXLab, etc.) hardcodes for this
        // one well-known edge case -- not a special case for any specific station.
        // callNoSuffix must already have any "/..." portable suffix stripped.
        private static string ResolveKg4Prefix(string callNoSuffix)
        {
            if (!callNoSuffix.StartsWith("KG4", StringComparison.OrdinalIgnoreCase)) return null;
            string suffix = callNoSuffix.Substring(3);
            bool isTwoLetterSuffix = suffix.Length == 2 &&
                                      IsAsciiLetter(suffix[0]) && IsAsciiLetter(suffix[1]);
            return isTwoLetterSuffix ? "KG4" : "K";
        }

        private static bool IsAsciiLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

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
                    // Real Club Log XML uses <cqz>, not <cq> -- this previously never
                    // matched, so CqZone silently parsed as 0 for every entity (found
                    // while adding the Big CTY per-callarea zone-override test below).
                    int.TryParse(Child(n, "CQZ")  ?? Child(n, "cqz"),  out v); e.CqZone = v;
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

        // Parses Club Log's <prefixes>/<exceptions> tables into a call/prefix -> (Adif,
        // IsCurrent) lookup. Both sections can carry more than one historical record
        // for the same <call> (e.g. "DL" belonged to a pre-1973 "GERMANY" entity
        // before today's "FEDERAL REPUBLIC OF GERMANY" assignment) -- among duplicates,
        // a record with no <end> date, or an <end> date that hasn't passed yet, is
        // preferred. IsCurrent is also returned for the winning record (even when
        // there's only a single, expired record for a <call>, e.g. "K4" -> PUERTO
        // RICO, expired 1946) so FindByCallsign can tell a genuinely-current match
        // apart from a stale one that only wins by default -- see its own comment.
        private static Dictionary<string, (int Adif, bool IsCurrent)> ParseCallToAdifTable(XmlDocument doc, string containerTag, string recordTag)
        {
            var result = new Dictionary<string, (int Adif, bool IsCurrent)>(StringComparer.OrdinalIgnoreCase);

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

                if (!result.TryGetValue(call, out var existing) || (isCurrent && !existing.IsCurrent))
                    result[call] = (adif, isCurrent);
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

        // Parses AD1C's real Big CTY country file (https://www.country-files.com/bigcty/cty.dat,
        // the same file WSJT-X's own User Guide directs users to download). Real format,
        // confirmed by direct inspection of a live download -- e.g.:
        //   United States:            05:  08:  NA:   37.60:    91.87:     5.0:  K:
        //       AA,AB,AC,...,K,N,W,=NH7RO/M,...,
        //       AA0(4)[7],AB0(4)[7],...,K0(4)[7],...,=AH2BY(4)[7],...;
        // Header line (not indented): Name, then 7 colon-delimited fields (cqz, ituz,
        // cont, lat, long, tz, main prefix) -- only the main prefix is needed here;
        // Adif/Continent/CqZone/Deleted still come from clublog_cty.xml via
        // FindByPrefix(mainPrefix), so this only needs to resolve a callsign to that
        // string. Continuation lines (indented, comma-separated, terminated by ';' on
        // the last line of the block): each token is either a bare alias/prefix
        // ("KG4", "AA0") or an exact-callsign exception ("=KG4NEX", "=W1AW/KG4"),
        // optionally suffixed with "(n)" = CQ zone override and/or "[n]" = ITU zone
        // override. No dash-range compression appears in the current file (verified),
        // so every alias is fully enumerated -- no range-expansion logic is needed.
        private void ParseBigCtyText(string text)
        {
            var prefixMap = new Dictionary<string, (string MainPrefix, int? Zone)>(StringComparer.OrdinalIgnoreCase);
            var exceptionMap = new Dictionary<string, (string MainPrefix, int? Zone)>(StringComparer.OrdinalIgnoreCase);
            string currentMainPrefix = null;

            foreach (var raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (!char.IsWhiteSpace(line[0]))
                {
                    // Header line -- extract just the main prefix (last colon-delimited
                    // field before the trailing colon).
                    currentMainPrefix = null;
                    int colon = line.IndexOf(':');
                    if (colon < 1) continue;
                    var headerFields = line.Substring(colon + 1).Split(':');
                    if (headerFields.Length < 7) continue;
                    string mainPrefix = headerFields[6].Trim().TrimStart('*');
                    if (mainPrefix.Length > 0) currentMainPrefix = mainPrefix;
                    continue;
                }

                if (currentMainPrefix == null) continue;  // continuation line for a header we couldn't parse

                string content = line.Trim().TrimEnd(';', ',');
                if (content.Length == 0) continue;

                foreach (var rawToken in content.Split(','))
                {
                    string token = rawToken.Trim();
                    if (token.Length == 0) continue;

                    bool isException = token[0] == '=';
                    if (isException) token = token.Substring(1);

                    int? zone = null;
                    int parenIdx = token.IndexOf('(');
                    if (parenIdx >= 0)
                    {
                        int closeIdx = token.IndexOf(')', parenIdx);
                        if (closeIdx > parenIdx)
                        {
                            int z;
                            if (int.TryParse(token.Substring(parenIdx + 1, closeIdx - parenIdx - 1), out z))
                                zone = z;
                        }
                        token = token.Substring(0, parenIdx);
                    }
                    else
                    {
                        int bracketIdx = token.IndexOf('[');
                        if (bracketIdx >= 0) token = token.Substring(0, bracketIdx);
                    }

                    token = token.Trim();
                    if (token.Length == 0) continue;

                    if (isException)
                        exceptionMap[token] = (currentMainPrefix, zone);
                    else
                        prefixMap[token] = (currentMainPrefix, zone);
                }
            }

            if (prefixMap.Count > 0)
            {
                _bigCtyPrefixes = prefixMap;
                _bigCtyExceptions = exceptionMap;
            }
            else
            {
                LastError = "Big CTY file parsed but no prefixes found; format may have changed.";
            }
        }

        private static string Child(XmlNode parent, string tag)
        {
            var n = parent.SelectSingleNode($"*[local-name()='{tag}']");
            var t = n?.InnerText?.Trim();
            return string.IsNullOrEmpty(t) ? null : t;
        }

        private static DateTime ReadMeta(string metaFile)
        {
            try
            {
                if (!File.Exists(metaFile)) return DateTime.MinValue;
                DateTime dt;
                return DateTime.TryParse(File.ReadAllText(metaFile).Trim(), out dt)
                    ? dt : DateTime.MinValue;
            }
            catch { return DateTime.MinValue; }
        }

        private static void WriteMeta(string metaFile, DateTime dt)
        {
            try { File.WriteAllText(metaFile, dt.ToString("o")); } catch { }
        }
    }
}
