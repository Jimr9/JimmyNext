using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;

namespace WSJTX_Controller
{
    // Offline US-state (and fallback name) lookup from the FCC's own public amateur
    // license database -- the authoritative source QRZ's own US records ultimately
    // derive from anyway. Free, no subscription/account, no per-call network cost
    // once downloaded. State is always taken from here when available (authoritative,
    // overrides any other source). Name uses whichever contributor's name has more
    // words: QRZ's own profile name generally wins, but QRZ's "fname" field sometimes
    // already jams a first name + middle initial together while the separate
    // last-name field is left blank -- in that case FCC's fuller first+MI+last record
    // supplements it. Nothing else about a callsign (grid, QSL manager, etc.) comes
    // from this source.
    //
    // Downloads https://data.fcc.gov/download/pub/uls/complete/l_amat.zip -- the
    // FCC's full weekly amateur-license snapshot (~170MB, regenerated every Sunday).
    // Only the EN.dat member is read out of the zip (entity name/address/state);
    // the other seven files in the archive (HD/AM/HS/CO/LA/SC/SF -- license status,
    // operator class, history, comments, etc.) aren't needed for a state lookup and
    // are never extracted. Daily incremental delta files exist
    // (data.fcc.gov/download/pub/uls/daily/l_am_<day>.zip) but are true diffs that
    // would need merging against a held state -- deliberately not used; a US ham's
    // registered state changes rarely enough that a weekly full re-download (same
    // cadence FCC already publishes on) is simpler and just as current in practice.
    //
    // EN.dat field layout was NOT taken on faith from FCC's own schema PDF (which
    // repeatedly failed to fetch during research) -- verified directly against a
    // real downloaded copy: pipe-delimited, Latin-1 encoded, field index 4 (0-based)
    // = call sign, field index 17 = two-letter state. A callsign is not 1:1 with one
    // row -- confirmed empirically (1,690,095 total rows, 1,597,966 unique
    // callsigns in the file used for this verification) -- reissued/renewed
    // callsigns leave old entity rows in place alongside the current one. Resolved
    // by keeping only the row with the highest unique_system_identifier (field
    // index 1) per callsign; the FCC assigns these monotonically, so the highest
    // one for a given callsign is confirmed (spot-checked against a real duplicate,
    // "AA0A") to be the current holder, not a stale prior one. First name (index 8),
    // middle initial (index 9), and last name (index 10) verified the same way
    // against the same real "AA0A"/"W1AW" rows (see JimmyTests) -- club/club-style
    // licenses (e.g. W1AW) leave these three fields blank, individual licenses
    // populate them.
    public class FccUlsProvider : ILookupProvider
    {
        private const string DownloadUrl = "https://data.fcc.gov/download/pub/uls/complete/l_amat.zip";
        private const int CallSignFieldIndex  = 4;
        private const int FirstNameFieldIndex = 8;
        private const int MiFieldIndex        = 9;
        private const int LastNameFieldIndex  = 10;
        private const int StateFieldIndex     = 17;
        private const int UidFieldIndex       = 1;
        private const int MinFieldCount       = 18;

        // Real downloaded files (2026-07-08) parsed to ~1.58 million unique
        // callsigns -- well above this floor, which only needs to catch a badly
        // truncated/corrupt download, not track the real week-to-week count.
        public const int MinPlausibleRecordCount = 500_000;
        // A genuine weekly refresh should never lose a large fraction of its
        // callsigns from one successful refresh to the next.
        public const double MinAcceptableFraction = 0.9;

        private readonly string _dir;
        private readonly string _dbPath;
        private readonly string _metaFile;
        private readonly object _dbLock = new object();
        private static readonly HttpClient _http =
            new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        private SQLiteConnection _conn;

        public string   SourceName  => "FCC ULS";
        public bool     IsEnabled   { get; private set; }
        public string   LastError   { get; private set; }
        public DateTime LastUpdate  { get; private set; }
        public int      RecordCount { get; private set; }

        public FccUlsProvider(string dataRoot)
        {
            _dir      = Path.Combine(dataRoot, "FccUls");
            _dbPath   = Path.Combine(_dir, "fcc_uls.db");
            _metaFile = Path.Combine(_dir, "metadata.txt");
            Directory.CreateDirectory(_dir);
        }

        public void Configure(bool enabled) => IsEnabled = enabled;

        // Set false when an existing local fcc_uls.db predates the "name" column
        // (built by an older Jimmy version) -- NeedsRefresh() then forces an
        // immediate full rebuild instead of waiting out the normal refresh cadence,
        // so upgrading users get name data without a manual cache clear.
        private bool _schemaCurrent;

        public void Load()
        {
            LastUpdate = ReadMeta();
            if (!File.Exists(_dbPath)) return;
            try
            {
                lock (_dbLock)
                {
                    _conn = new SQLiteConnection($"Data Source={_dbPath};");
                    _conn.Open();
                    using (var cmd = _conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT COUNT(*) FROM fcc_amat;";
                        RecordCount = Convert.ToInt32(cmd.ExecuteScalar());
                    }
                    _schemaCurrent = HasNameColumn();
                }
            }
            catch (Exception ex)
            {
                LastError = "Load error: " + ex.Message;
            }
        }

        // PRAGMA table_info returns one row per existing column, each row's own
        // "name" field holding that column's name -- this checks whether one of
        // those rows describes a column literally called "name".
        private bool HasNameColumn()
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(fcc_amat);";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        if (string.Equals(Convert.ToString(r["name"]), "name", StringComparison.OrdinalIgnoreCase))
                            return true;
                }
            }
            return false;
        }

        public bool NeedsRefresh(int days) =>
            !File.Exists(_dbPath) || !_schemaCurrent || (DateTime.UtcNow - LastUpdate).TotalDays >= days;

        // Downloads the full weekly file, parses EN.dat directly out of the zip
        // (never extracted to disk as loose files), builds a fresh SQLite table in
        // a temp file, then atomically swaps it in for the live connection. A
        // failure at any point leaves the previous (still valid) database and
        // LastUpdate untouched -- never leaves Jimmy with a half-written table.
        public async Task<bool> RefreshAsync()
        {
            LastError = null;
            if (TestModeGuard.IsTestMode)
            {
                LastError = "Blocked: JIMMY_TEST_DB_PATH is set (test mode) -- no real FCC ULS traffic allowed.";
                return false;
            }
            int previousCount = RecordCount;
            string tmpZip = Path.Combine(_dir, "l_amat.tmp.zip");
            string tmpDb  = Path.Combine(_dir, "fcc_uls.tmp.db");
            try
            {
                byte[] bytes;
                try
                {
                    bytes = await _http.GetByteArrayAsync(DownloadUrl).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LastError = "Download failed: " + ex.Message;
                    return false;
                }
                File.WriteAllBytes(tmpZip, bytes);

                var best = new Dictionary<string, (long Uid, string State, string Name)>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    using (var zip = ZipFile.OpenRead(tmpZip))
                    {
                        var enEntry = zip.GetEntry("EN.dat");
                        if (enEntry == null)
                        {
                            LastError = "EN.dat not found in downloaded file (FCC may have changed the archive layout).";
                            return false;
                        }
                        using (var stream = enEntry.Open())
                        using (var reader = new StreamReader(stream, System.Text.Encoding.GetEncoding("ISO-8859-1")))
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                                ParseLine(line, best);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LastError = "Parse failed: " + ex.Message;
                    return false;
                }

                if (best.Count == 0)
                {
                    LastError = "No usable records parsed from EN.dat.";
                    return false;
                }

                // Guard against a technically-valid-but-truncated download -- e.g.
                // the connection dropped mid-transfer, or FCC's server was caught
                // mid-regeneration of the weekly file. A zip can open and "parse"
                // successfully while still containing only a fraction of the real
                // data, with no exception anywhere to catch. A genuine weekly file
                // should never lose a large fraction of its callsigns from one
                // refresh to the next, so reject anything that looks like a bad
                // partial file rather than silently replacing good data with worse.
                if (LooksIncomplete(best.Count, previousCount))
                {
                    LastError = previousCount > 0
                        ? $"Downloaded file looks incomplete ({best.Count:N0} callsigns vs {previousCount:N0} previously) -- keeping existing data."
                        : $"Downloaded file looks incomplete ({best.Count:N0} callsigns parsed, expected at least {MinPlausibleRecordCount:N0}).";
                    return false;
                }

                if (File.Exists(tmpDb)) File.Delete(tmpDb);
                using (var conn = new SQLiteConnection($"Data Source={tmpDb};"))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "CREATE TABLE fcc_amat (callsign TEXT PRIMARY KEY, state TEXT, name TEXT);";
                        cmd.ExecuteNonQuery();
                    }
                    using (var tx = conn.BeginTransaction())
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "INSERT INTO fcc_amat (callsign, state, name) VALUES (@c, @s, @n);";
                            foreach (var kv in best)
                            {
                                cmd.Parameters.Clear();
                                cmd.Parameters.AddWithValue("@c", kv.Key);
                                cmd.Parameters.AddWithValue("@s", kv.Value.State);
                                cmd.Parameters.AddWithValue("@n", (object)kv.Value.Name ?? DBNull.Value);
                                cmd.ExecuteNonQuery();
                            }
                        }
                        tx.Commit();
                    }
                }

                lock (_dbLock)
                {
                    _conn?.Close();
                    _conn?.Dispose();
                    _conn = null;
                    File.Copy(tmpDb, _dbPath, overwrite: true);
                    _conn = new SQLiteConnection($"Data Source={_dbPath};");
                    _conn.Open();
                    _schemaCurrent = true;
                }

                RecordCount = best.Count;
                LastUpdate = DateTime.UtcNow;
                WriteMeta(LastUpdate);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
            finally
            {
                try { File.Delete(tmpZip); } catch { }
                try { File.Delete(tmpDb); } catch { }
            }
        }

        // A newly-parsed record count is rejected if it's implausibly low outright
        // (no prior successful refresh to compare against), or if it dropped
        // sharply from the last known-good count -- either shape is what a
        // truncated/corrupt download looks like, not a normal week-to-week change.
        public static bool LooksIncomplete(int newCount, int previousCount)
        {
            if (previousCount > 0) return newCount < previousCount * MinAcceptableFraction;
            return newCount < MinPlausibleRecordCount;
        }

        // Public (and the field-index constants above it) so this can be verified
        // directly against real sample EN.dat lines in JimmyTests, instead of only
        // trusting that it compiles.
        public static void ParseLine(string line, Dictionary<string, (long Uid, string State, string Name)> best)
        {
            if (string.IsNullOrEmpty(line) || !line.StartsWith("EN|", StringComparison.Ordinal)) return;
            var f = line.Split('|');
            if (f.Length < MinFieldCount) return;

            string call = f[CallSignFieldIndex].Trim();
            string state = f[StateFieldIndex].Trim().ToUpperInvariant();
            if (call.Length == 0 || state.Length == 0) return;

            long uid;
            if (!long.TryParse(f[UidFieldIndex], out uid)) return;

            string name = CombineName(f[FirstNameFieldIndex].Trim(), f[MiFieldIndex].Trim(), f[LastNameFieldIndex].Trim());

            (long Uid, string State, string Name) existing;
            if (!best.TryGetValue(call, out existing) || uid > existing.Uid)
                best[call] = (uid, state, name);
        }

        // Club/club-style licenses (e.g. W1AW) leave first/mi/last all blank --
        // returns null rather than an empty string so downstream "is there a name"
        // checks (string.IsNullOrEmpty) behave the same as QRZ's CombineName.
        private static string CombineName(string first, string mi, string last)
        {
            string firstPart = string.IsNullOrEmpty(mi) ? first : $"{first} {mi}";
            if (string.IsNullOrEmpty(firstPart)) return string.IsNullOrEmpty(last) ? null : last;
            if (string.IsNullOrEmpty(last)) return firstPart;
            return $"{firstPart} {last}";
        }

        // Synchronous, offline (only reads the already-downloaded local table) --
        // safe for the per-decode hot path. Returns State only, for the one external
        // caller (Controller's spot-country display) that predates Name support.
        public string Lookup(string call) => LookupStateAndName(call).State;

        private (string State, string Name) LookupStateAndName(string call)
        {
            if (string.IsNullOrEmpty(call)) return (null, null);
            lock (_dbLock)
            {
                if (_conn == null) return (null, null);
                try
                {
                    using (var cmd = _conn.CreateCommand())
                    {
                        // An upgrading user's on-disk fcc_uls.db may still be the old
                        // (pre-Name) 2-column schema until the background rebuild
                        // NeedsRefresh() triggered finishes -- querying "name" against
                        // that table would throw on every single call. _schemaCurrent
                        // reflects what the currently-open connection actually has.
                        cmd.CommandText = _schemaCurrent
                            ? "SELECT state, name FROM fcc_amat WHERE callsign = @c;"
                            : "SELECT state FROM fcc_amat WHERE callsign = @c;";
                        cmd.Parameters.AddWithValue("@c", call.ToUpperInvariant());
                        using (var r = cmd.ExecuteReader())
                        {
                            if (!r.Read()) return (null, null);
                            string state = r.IsDBNull(0) ? null : r.GetString(0);
                            string name  = (_schemaCurrent && !r.IsDBNull(1)) ? r.GetString(1) : null;
                            return (state, name);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Never let a lookup-provider hiccup crash the per-decode hot path
                    // (WsjtxClient.Update -> AwardTagger -> Build -> Contribute).
                    LastError = "Lookup error: " + ex.Message;
                    return (null, null);
                }
            }
        }

        public void Contribute(LookupRecord record, string call)
        {
            var (state, name) = LookupStateAndName(call);
            bool contributed = false;

            // FCC's registered state is authoritative for a US callsign -- always
            // use it when available, regardless of what an earlier provider in the
            // merge order already set.
            if (!string.IsNullOrEmpty(state))
            {
                record.State = state;
                contributed = true;
            }

            if (ShouldPreferName(name, record.Name))
            {
                record.Name = name;
                contributed = true;
            }

            if (contributed) record.Sources.Add(SourceName);
        }

        // Prefer whichever contributor's name has more parts to it. QRZ's own
        // "fname" field sometimes already jams a first name + middle initial
        // together (e.g. "RICHARD L") while its separate last-name field is left
        // blank on many profiles -- taking QRZ's 2-word result as "already
        // complete" would permanently hide FCC's fuller "RICHARD L DILLON" record.
        // FCC's fields are always first+MI+last in a consistent order, so a higher
        // word count from FCC reliably means a more complete name, not a different
        // one. Public so this can be tested directly, same as ParseLine/LooksIncomplete.
        public static bool ShouldPreferName(string candidateName, string existingName)
        {
            if (string.IsNullOrEmpty(candidateName)) return false;
            if (string.IsNullOrEmpty(existingName)) return true;
            return WordCount(candidateName) > WordCount(existingName);
        }

        private static int WordCount(string s) =>
            s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;

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
