using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Data.SQLite;

namespace WSJTX_Controller
{
    public class QsoRecord
    {
        public int    Id          { get; set; }
        public string Callsign    { get; set; }
        public string Band        { get; set; }
        public string Mode        { get; set; }
        public string QsoDate     { get; set; }
        public string TimeOn      { get; set; }
        public string TimeOff     { get; set; }
        public string Country     { get; set; }
        public string State       { get; set; }
        public int    Dxcc        { get; set; }
        public int    CqZone      { get; set; }
        public string LotwQslRcvd { get; set; }
        public string QrzQslRcvd  { get; set; }
        public string Source      { get; set; }
        public string Grid        { get; set; }
        public string Name        { get; set; }
        public string RstSent     { get; set; }
        public string RstRcvd     { get; set; }
        public string Comment     { get; set; }
        public bool   IsConfirmed => LotwQslRcvd == "Y" || QrzQslRcvd == "Y";

        // Every value the "source" column is ever written with. Single source of truth
        // for the Edit Log source filter and the export source-filter dialog -- add a
        // new logging service here and both pick it up automatically.
        public static readonly string[] KnownSources = { "WSJTX", "QRZ", "LOTW", "CLUBLOG", "MANUAL" };
    }

    public class ImportLogEntry
    {
        public int      Id             { get; set; }
        public string   Source         { get; set; }
        public DateTime StartedAt      { get; set; }
        public int      TotalQso       { get; set; }
        public int      NewQso         { get; set; }
        public int      NewlyConfirmed { get; set; }
        public int      Corrected      { get; set; }
        public int      SkippedQso     { get; set; }
        public string   ErrorText      { get; set; }
    }

    public class BandStat
    {
        public string Label     { get; set; }
        public int    Total     { get; set; }
        public int    Confirmed { get; set; }
        public string Pct => Total > 0 ? $"{100.0 * Confirmed / Total:0.0}%" : "—";
    }

    public class LogbookDb : IDisposable
    {
        private SQLiteConnection _conn;
        private readonly object  _lock = new object();

        // WAS = 50 states only (DC is not a state and does not count).
        private const string WasInList =
            "'AK','AL','AR','AZ','CA','CO','CT','DE','FL','GA','HI','IA','ID','IL','IN'," +
            "'KS','KY','LA','MA','MD','ME','MI','MN','MO','MS','MT','NC','ND','NE','NH','NJ'," +
            "'NM','NV','NY','OH','OK','OR','PA','RI','SC','SD','TN','TX','UT','VA','VT','WA'," +
            "'WI','WV','WY'";

        // JIMMY_TEST_DB_PATH lets the replay test suite point a real, separately-running
        // Jimmy.exe at a throwaway database instead of the user's actual logbook -- unset
        // in normal operation, so behavior is unchanged.
        public static string DbPath =>
            Environment.GetEnvironmentVariable("JIMMY_TEST_DB_PATH") ??
            Path.Combine(LookupManager.DataRoot, "Logbook", "logbook.db");

        public LogbookDb()
        {
            var dir = Path.GetDirectoryName(DbPath);
            Directory.CreateDirectory(dir);
            _conn = new SQLiteConnection($"Data Source={DbPath};");
            _conn.Open();
            InitSchema();
        }

        // Opens (or creates) a database at an explicit path.
        // Used by automated tests to avoid touching the real data directory.
        public LogbookDb(string dbPath)
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _conn = new SQLiteConnection($"Data Source={dbPath};");
            _conn.Open();
            InitSchema();
        }

        // ── Schema ───────────────────────────────────────────────────────────────

        private void InitSchema()
        {
            Exec("PRAGMA journal_mode=WAL;");
            Exec("PRAGMA foreign_keys=ON;");
            Exec(@"CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT
            );");

            int ver;
            int.TryParse(GetMeta("db_version") ?? "0", out ver);

            if (ver < 1)
            {
                Exec(@"CREATE TABLE IF NOT EXISTS qso (
                    id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    callsign       TEXT NOT NULL,
                    band           TEXT    DEFAULT '',
                    mode           TEXT    DEFAULT '',
                    qso_date       TEXT    DEFAULT '',
                    time_on        TEXT    DEFAULT '',
                    time_off       TEXT    DEFAULT '',
                    freq_hz        INTEGER DEFAULT 0,
                    rst_sent       TEXT    DEFAULT '',
                    rst_rcvd       TEXT    DEFAULT '',
                    state          TEXT    DEFAULT '',
                    country        TEXT    DEFAULT '',
                    dxcc           INTEGER DEFAULT 0,
                    cq_zone        INTEGER DEFAULT 0,
                    grid           TEXT    DEFAULT '',
                    name           TEXT    DEFAULT '',
                    comment        TEXT    DEFAULT '',
                    tx_pwr         TEXT    DEFAULT '',
                    operator_call  TEXT    DEFAULT '',
                    station_call   TEXT    DEFAULT '',
                    my_grid        TEXT    DEFAULT '',
                    lotw_qsl_sent  TEXT    DEFAULT '',
                    lotw_qsl_rcvd  TEXT    DEFAULT '',
                    qrz_qsl_sent   TEXT    DEFAULT '',
                    qrz_qsl_rcvd   TEXT    DEFAULT '',
                    source         TEXT    NOT NULL DEFAULT 'MANUAL',
                    source_qso_id  TEXT    DEFAULT '',
                    imported_at    TEXT    NOT NULL,
                    dedup_key      TEXT    NOT NULL UNIQUE
                );");

                Exec("CREATE INDEX IF NOT EXISTS ix_call      ON qso(callsign);");
                Exec("CREATE INDEX IF NOT EXISTS ix_date      ON qso(qso_date);");
                Exec("CREATE INDEX IF NOT EXISTS ix_dxcc      ON qso(dxcc);");
                Exec("CREATE INDEX IF NOT EXISTS ix_band_mode ON qso(band, mode);");
                Exec("CREATE INDEX IF NOT EXISTS ix_state     ON qso(state);");
                Exec("CREATE INDEX IF NOT EXISTS ix_cq_zone   ON qso(cq_zone);");

                Exec(@"CREATE TABLE IF NOT EXISTS import_log (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    source      TEXT NOT NULL,
                    started_at  TEXT NOT NULL,
                    finished_at TEXT,
                    total_qso   INTEGER DEFAULT 0,
                    new_qso     INTEGER DEFAULT 0,
                    updated_qso INTEGER DEFAULT 0,
                    skipped_qso INTEGER DEFAULT 0,
                    error_text  TEXT    DEFAULT ''
                );");

                SetMeta("db_version", "1");
                ver = 1;
            }

            // v2: fields needed by the Rule Definitions (awards) engine. Standard ADIF
            // fields that were previously received (when present in the source data)
            // but discarded on import.
            if (ver < 2)
            {
                Exec("ALTER TABLE qso ADD COLUMN continent    TEXT    DEFAULT '';");
                Exec("ALTER TABLE qso ADD COLUMN itu_zone     INTEGER DEFAULT 0;");
                Exec("ALTER TABLE qso ADD COLUMN county       TEXT    DEFAULT '';");
                Exec("ALTER TABLE qso ADD COLUMN iota         TEXT    DEFAULT '';");
                Exec("ALTER TABLE qso ADD COLUMN sig          TEXT    DEFAULT '';");
                Exec("ALTER TABLE qso ADD COLUMN sig_info     TEXT    DEFAULT '';");
                Exec("ALTER TABLE qso ADD COLUMN my_sig       TEXT    DEFAULT '';");
                Exec("ALTER TABLE qso ADD COLUMN my_sig_info  TEXT    DEFAULT '';");
                Exec("ALTER TABLE qso ADD COLUMN darc_dok     TEXT    DEFAULT '';");
                Exec("ALTER TABLE qso ADD COLUMN wpx_prefix   TEXT    DEFAULT '';");

                Exec("CREATE INDEX IF NOT EXISTS ix_continent ON qso(continent);");
                Exec("CREATE INDEX IF NOT EXISTS ix_itu_zone  ON qso(itu_zone);");
                Exec("CREATE INDEX IF NOT EXISTS ix_county    ON qso(county);");
                Exec("CREATE INDEX IF NOT EXISTS ix_iota      ON qso(iota);");
                Exec("CREATE INDEX IF NOT EXISTS ix_sig_info  ON qso(sig_info);");

                SetMeta("db_version", "2");
            }

            // v3: per-QSO outbound upload tracking for QRZ/Club Log logbook upload.
            // Empty string = not yet uploaded to that service (matches this table's
            // existing convention of '' rather than NULL for "unset" TEXT columns).
            if (ver < 3)
            {
                Exec("ALTER TABLE qso ADD COLUMN qrz_uploaded_at     TEXT DEFAULT '';");
                Exec("ALTER TABLE qso ADD COLUMN clublog_uploaded_at TEXT DEFAULT '';");

                SetMeta("db_version", "3");
            }

            // v4: contest exchange fields (ADIF STX_STRING/SRX_STRING), e.g. Field
            // Day "2A MO" -- carried through to QRZ/Club Log upload so contest QSOs
            // are not missing this data there.
            if (ver < 4)
            {
                Exec("ALTER TABLE qso ADD COLUMN exchange_sent TEXT DEFAULT '';");
                Exec("ALTER TABLE qso ADD COLUMN exchange_rcvd TEXT DEFAULT '';");

                SetMeta("db_version", "4");
            }

            // v5: split import_log's single "updated_qso" count into what actually changed --
            // a QSL newly received (moves award progress) vs. other details corrected
            // (state/country/grid/etc.) -- so the sync status can say which happened instead
            // of just "N updated". Old rows keep updated_qso=0 in both new columns (that
            // history predates the distinction and can't be reconstructed).
            if (ver < 5)
            {
                Exec("ALTER TABLE import_log ADD COLUMN newly_confirmed_qso INTEGER DEFAULT 0;");
                Exec("ALTER TABLE import_log ADD COLUMN corrected_qso       INTEGER DEFAULT 0;");

                SetMeta("db_version", "5");
            }

            // v6: same per-QSO outbound upload tracking as v3's qrz_uploaded_at/
            // clublog_uploaded_at, extended to LoTW (TqslUploadClient) and HRDLog.net
            // (HrdLogUploadClient) -- both already called GetPendingUploads/MarkUploaded
            // with "LOTW"/"HRDLOG" before this column existed, which UploadColumn()
            // rejected with an exception every time (silently caught and logged, never
            // surfaced) -- confirmed live, 2026-08-07: real-time HRDLog uploads could
            // succeed on HRDLog.net's own end while Jimmy never recorded it locally, and
            // both services' Alt+U catch-up silently did nothing.
            if (ver < 6)
            {
                Exec("ALTER TABLE qso ADD COLUMN lotw_uploaded_at   TEXT DEFAULT '';");
                Exec("ALTER TABLE qso ADD COLUMN hrdlog_uploaded_at TEXT DEFAULT '';");

                SetMeta("db_version", "6");
            }

            // v7: same per-QSO outbound upload tracking, extended to eQSL.cc (uploaded via
            // EngineHost/Nexus's own transport -- see ExternalDataClient.UploadEqsl).
            if (ver < 7)
            {
                Exec("ALTER TABLE qso ADD COLUMN eqsl_uploaded_at TEXT DEFAULT '';");

                SetMeta("db_version", "7");
            }

            // v8: eQSL INBOUND confirmation (EQSL_QSL_RCVD from a downloaded InBox record),
            // separate from eqsl_uploaded_at (v7, our own outbound push). Deliberately its own
            // column, not folded into lotw_qsl_rcvd/qrz_qsl_rcvd or RuleConfirmation's award-
            // counting SQL (Awards/RuleEngine.cs) -- eQSL is not an ARRL-recognized DXCC/WAS
            // confirmation source (matches Nexus's own documented "confirmed=true but
            // award_confirmed=false" distinction for eQSL, docs/manual/Logbook-and-Awards.md).
            // Informational only: shown to the operator, never advances Still-Need counts.
            if (ver < 8)
            {
                Exec("ALTER TABLE qso ADD COLUMN eqsl_qsl_rcvd TEXT DEFAULT '';");

                SetMeta("db_version", "8");
            }

            // v9: one-time repair for a real matching bug (found live, 2026-08-26): LoTW's own
            // ADIF export reports FT4 QSOs under the ADIF umbrella MODE "MFSK" (see
            // AdifImporter's own SUBMODE comment), which never matched the "FT4" every other
            // source here (QRZ, Club Log, Jimmy's own live logging) already used for the exact
            // same real QSOs -- so every LoTW download before that fix inserted a SEPARATE
            // mode='MFSK' duplicate row instead of recognizing the QSO it already had, and
            // left the real FT4 row's LoTW-upload status permanently unmatched/pending. That
            // fix only stops new duplicates; this repairs data the bug already wrote. For each
            // mode='MFSK' row: if a real mode='FT4' row exists for the same
            // callsign/band/date/time, backfill whatever LoTW fields the FT4 row is still
            // missing from the MFSK row (never overwrites a value the FT4 row already has),
            // then delete the MFSK duplicate. If no FT4 counterpart exists -- this MFSK row is
            // the ONLY record of that contact -- relabel it to FT4 in place instead of
            // deleting it; deleting an unmatched row would be a real, unrecoverable QSO loss,
            // worse than leaving one merge undone (see this project's own "never knowingly
            // lose a valid QSO" rule).
            if (ver < 9)
            {
                var mfskRows = new List<(long id, string callsign, string band, string qsoDate, string timeOn,
                                          string lotwUploadedAt, string lotwQslSent, string lotwQslRcvd)>();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT id, callsign, band, qso_date, time_on, lotw_uploaded_at, lotw_qsl_sent, lotw_qsl_rcvd " +
                        "FROM qso WHERE mode='MFSK';";
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                            mfskRows.Add((
                                r.GetInt64(0),
                                r.IsDBNull(1) ? "" : r.GetString(1),
                                r.IsDBNull(2) ? "" : r.GetString(2),
                                r.IsDBNull(3) ? "" : r.GetString(3),
                                r.IsDBNull(4) ? "" : r.GetString(4),
                                r.IsDBNull(5) ? "" : r.GetString(5),
                                r.IsDBNull(6) ? "" : r.GetString(6),
                                r.IsDBNull(7) ? "" : r.GetString(7)));
                    }
                }

                foreach (var row in mfskRows)
                {
                    long? counterpartId = null;
                    using (var cmd = _conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "SELECT id FROM qso " +
                            "WHERE callsign=@c AND band=@b AND qso_date=@d AND time_on=@t AND mode='FT4' LIMIT 1;";
                        cmd.Parameters.AddWithValue("@c", row.callsign);
                        cmd.Parameters.AddWithValue("@b", row.band);
                        cmd.Parameters.AddWithValue("@d", row.qsoDate);
                        cmd.Parameters.AddWithValue("@t", row.timeOn);
                        var result = cmd.ExecuteScalar();
                        if (result != null && result != DBNull.Value) counterpartId = Convert.ToInt64(result);
                    }

                    if (counterpartId.HasValue)
                    {
                        using (var upd = _conn.CreateCommand())
                        {
                            upd.CommandText =
                                "UPDATE qso SET " +
                                "lotw_uploaded_at = CASE WHEN lotw_uploaded_at != '' THEN lotw_uploaded_at ELSE @lu END, " +
                                "lotw_qsl_sent    = CASE WHEN lotw_qsl_sent    != '' THEN lotw_qsl_sent    ELSE @ls END, " +
                                "lotw_qsl_rcvd    = CASE WHEN lotw_qsl_rcvd    != '' THEN lotw_qsl_rcvd    ELSE @lr END " +
                                "WHERE id=@id;";
                            upd.Parameters.AddWithValue("@lu", row.lotwUploadedAt);
                            upd.Parameters.AddWithValue("@ls", row.lotwQslSent);
                            upd.Parameters.AddWithValue("@lr", row.lotwQslRcvd);
                            upd.Parameters.AddWithValue("@id", counterpartId.Value);
                            upd.ExecuteNonQuery();
                        }
                        using (var del = _conn.CreateCommand())
                        {
                            del.CommandText = "DELETE FROM qso WHERE id=@id;";
                            del.Parameters.AddWithValue("@id", row.id);
                            del.ExecuteNonQuery();
                        }
                    }
                    else
                    {
                        string newDedupKey = AdifImporter.BuildDedupKey(row.callsign, row.band, "FT4", row.qsoDate, row.timeOn);
                        using (var upd = _conn.CreateCommand())
                        {
                            upd.CommandText = "UPDATE qso SET mode='FT4', dedup_key=@k WHERE id=@id;";
                            upd.Parameters.AddWithValue("@k", newDedupKey);
                            upd.Parameters.AddWithValue("@id", row.id);
                            upd.ExecuteNonQuery();
                        }
                    }
                }

                SetMeta("db_version", "9");
            }
        }

        // ── Meta ─────────────────────────────────────────────────────────────────

        public string GetMeta(string key)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT value FROM meta WHERE key=@k;";
                    cmd.Parameters.AddWithValue("@k", key);
                    var r = cmd.ExecuteScalar();
                    return r == null || r == DBNull.Value ? null : r.ToString();
                }
            }
        }

        public void SetMeta(string key, string value)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText =
                        "INSERT INTO meta(key,value) VALUES(@k,@v) " +
                        "ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
                    cmd.Parameters.AddWithValue("@k", key);
                    cmd.Parameters.AddWithValue("@v", value ?? "");
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // One-time repair for QSOs already sitting in the database with a blank state
        // despite the callsign being derivable -- found 2026-07-08: QRZ's own ADIF
        // export sometimes omits STATE for a contact even though QRZ's own site already
        // credits the state (several confirmed Alaska/Hawaii contacts had this gap).
        // Only ever fills a currently-blank state; never touches a row that already has
        // one. resolveState is expected to be offline/cache-backed (e.g. FCC ULS or an
        // already-cached QRZ lookup) -- never a live network query, since this can touch
        // many rows in one pass. Returns the number of rows updated.
        public int BackfillMissingStates(Func<string, string> resolveState)
        {
            if (resolveState == null) return 0;

            var candidates = new List<(long id, string callsign, string grid)>();
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT id, callsign, grid FROM qso WHERE (state='' OR state IS NULL) AND callsign != '';";
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                            candidates.Add((r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2)));
                    }
                }
            }

            int updated = 0;
            lock (_lock)
            {
                using (var tx = _conn.BeginTransaction())
                {
                    foreach (var (id, callsign, grid) in candidates)
                    {
                        string state = resolveState(callsign);
                        if (string.IsNullOrEmpty(state) && !string.IsNullOrEmpty(grid))
                            state = WsjtxClient.GridToUsState(grid);
                        if (string.IsNullOrEmpty(state)) continue;

                        using (var upd = _conn.CreateCommand())
                        {
                            upd.Transaction = tx;
                            upd.CommandText = "UPDATE qso SET state=@s WHERE id=@id;";
                            upd.Parameters.AddWithValue("@s", state);
                            upd.Parameters.AddWithValue("@id", id);
                            upd.ExecuteNonQuery();
                        }
                        updated++;
                    }
                    tx.Commit();
                }
            }
            return updated;
        }

        // ── Upsert ───────────────────────────────────────────────────────────────

        // Returns (isNew, isUpdated).
        // Column indices within mutableCols (below) that represent a QSL actually being
        // received -- the one kind of "updated" a ham actually cares about (it moves award
        // progress). Everything else mutableCols tracks (state/country/grid/DXCC/etc.) is
        // grouped as "corrected" instead -- a data-quality fix, not new award credit.
        private static readonly int[] ConfirmColumnIndices = { 1, 3 }; // lotw_qsl_rcvd, qrz_qsl_rcvd

        public (bool isNew, bool newlyConfirmed, bool corrected) Upsert(
            string callsign, string band, string mode,
            string qsoDate, string timeOn, string timeOff,
            long freqHz, string rstSent, string rstRcvd,
            string state, string country, int dxcc, int cqZone,
            string grid, string name, string comment, string txPwr,
            string operatorCall, string stationCall, string myGrid,
            string lotwQslSent, string lotwQslRcvd,
            string qrzQslSent, string qrzQslRcvd,
            string source, string sourceQsoId, string dedupKey,
            string continent, int ituZone, string county, string iota,
            string sig, string sigInfo, string mySig, string mySigInfo,
            string darcDok, string wpxPrefix,
            string exchangeSent, string exchangeRcvd)
        {
            // Columns the ON CONFLICT clause below can modify. Compared before/after so
            // "updated" only counts rows whose data actually changed, instead of every
            // already-known QSO the source re-sends (which is nearly all of them).
            const string mutableCols =
                "lotw_qsl_sent, lotw_qsl_rcvd, qrz_qsl_sent, qrz_qsl_rcvd, country, state, name, grid, " +
                "dxcc, cq_zone, continent, itu_zone, county, iota, sig, sig_info, my_sig, my_sig_info, " +
                "darc_dok, wpx_prefix, exchange_sent, exchange_rcvd, qrz_uploaded_at, clublog_uploaded_at, " +
                "lotw_uploaded_at, hrdlog_uploaded_at";

            lock (_lock)
            {
                bool existed;
                object[] before = null;
                using (var check = _conn.CreateCommand())
                {
                    check.CommandText = $"SELECT {mutableCols} FROM qso WHERE dedup_key=@k;";
                    check.Parameters.AddWithValue("@k", dedupKey);
                    using (var r = check.ExecuteReader())
                    {
                        existed = r.Read();
                        if (existed)
                        {
                            before = new object[r.FieldCount];
                            r.GetValues(before);
                        }
                    }
                }

                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT INTO qso (
    callsign, band, mode, qso_date, time_on, time_off, freq_hz,
    rst_sent, rst_rcvd, state, country, dxcc, cq_zone, grid,
    name, comment, tx_pwr, operator_call, station_call, my_grid,
    lotw_qsl_sent, lotw_qsl_rcvd, qrz_qsl_sent, qrz_qsl_rcvd,
    source, source_qso_id, imported_at, dedup_key,
    continent, itu_zone, county, iota, sig, sig_info, my_sig, my_sig_info,
    darc_dok, wpx_prefix, exchange_sent, exchange_rcvd,
    qrz_uploaded_at, clublog_uploaded_at, lotw_uploaded_at, hrdlog_uploaded_at
) VALUES (
    @callsign, @band, @mode, @qso_date, @time_on, @time_off, @freq_hz,
    @rst_sent, @rst_rcvd, @state, @country, @dxcc, @cq_zone, @grid,
    @name, @comment, @tx_pwr, @operator_call, @station_call, @my_grid,
    @lotw_qsl_sent, @lotw_qsl_rcvd, @qrz_qsl_sent, @qrz_qsl_rcvd,
    @source, @source_qso_id, @imported_at, @dedup_key,
    @continent, @itu_zone, @county, @iota, @sig, @sig_info, @my_sig, @my_sig_info,
    @darc_dok, @wpx_prefix, @exchange_sent, @exchange_rcvd,
    CASE WHEN @source='QRZ'     THEN @imported_at ELSE '' END,
    CASE WHEN @source='CLUBLOG' THEN @imported_at ELSE '' END,
    CASE WHEN @source='LOTW'    THEN @imported_at ELSE '' END,
    CASE WHEN @source='HRDLOG'  THEN @imported_at ELSE '' END
)
ON CONFLICT(dedup_key) DO UPDATE SET
    lotw_qsl_sent = CASE WHEN excluded.source='LOTW' AND excluded.lotw_qsl_sent!='' THEN excluded.lotw_qsl_sent ELSE qso.lotw_qsl_sent END,
    lotw_qsl_rcvd = CASE WHEN qso.lotw_qsl_rcvd='Y' THEN 'Y' WHEN excluded.source='LOTW' AND excluded.lotw_qsl_rcvd!='' THEN excluded.lotw_qsl_rcvd ELSE qso.lotw_qsl_rcvd END,
    qrz_qsl_sent  = CASE WHEN excluded.source='QRZ'  AND excluded.qrz_qsl_sent !='' THEN excluded.qrz_qsl_sent  ELSE qso.qrz_qsl_sent  END,
    qrz_qsl_rcvd  = CASE WHEN qso.qrz_qsl_rcvd='Y'  THEN 'Y' WHEN excluded.source='QRZ'  AND excluded.qrz_qsl_rcvd !='' THEN excluded.qrz_qsl_rcvd  ELSE qso.qrz_qsl_rcvd  END,
    -- A download FROM a service proves that service already has the QSO, so it
    -- must never be queued to be uploaded back to it (GetPendingUploads checks
    -- exactly this column). Only ever fills in a blank -- an already-recorded
    -- real upload time (from Jimmy's own successful upload) is never overwritten
    -- or downgraded by a later download. A download from a DIFFERENT service
    -- (e.g. LOTW) leaves both of these alone, since it says nothing about
    -- whether QRZ/Club Log has this QSO.
    qrz_uploaded_at      = CASE WHEN qso.qrz_uploaded_at      != '' THEN qso.qrz_uploaded_at      ELSE excluded.qrz_uploaded_at      END,
    clublog_uploaded_at  = CASE WHEN qso.clublog_uploaded_at  != '' THEN qso.clublog_uploaded_at  ELSE excluded.clublog_uploaded_at  END,
    lotw_uploaded_at     = CASE WHEN qso.lotw_uploaded_at     != '' THEN qso.lotw_uploaded_at     ELSE excluded.lotw_uploaded_at     END,
    hrdlog_uploaded_at   = CASE WHEN qso.hrdlog_uploaded_at   != '' THEN qso.hrdlog_uploaded_at   ELSE excluded.hrdlog_uploaded_at   END,
    -- country/dxcc/continent/cq_zone are the fields Jimmy itself guesses from its own
    -- cached Club Log country data at live-logging time (EnrichWithClubLogGeoData), purely
    -- so Awards/Still-Need tracking has something to show immediately -- Jimmy is never the
    -- authoritative source for this metadata (see project logging philosophy). So unlike the
    -- other blank-only-backfill columns below: a real download from QRZ/LoTW/Club Log always
    -- overwrites Jimmy's own guess here, even if a (possibly wrong) guess is already present --
    -- otherwise a wrong guess could never be corrected once written. A guess from Jimmy's own
    -- live logging (source='WSJTX') or a manual import still only fills in if currently blank,
    -- same as before.
    country  = CASE WHEN excluded.source IN ('QRZ','LOTW','CLUBLOG') AND excluded.country !='' THEN excluded.country
                    WHEN (qso.country ='' OR qso.country  IS NULL) AND excluded.country !='' THEN excluded.country  ELSE qso.country  END,
    state    = CASE WHEN (qso.state   ='' OR qso.state    IS NULL) AND excluded.state   !='' THEN excluded.state    ELSE qso.state    END,
    name     = CASE WHEN (qso.name    ='' OR qso.name     IS NULL) AND excluded.name    !='' THEN excluded.name     ELSE qso.name     END,
    grid     = CASE WHEN (qso.grid    ='' OR qso.grid     IS NULL) AND excluded.grid    !='' THEN excluded.grid     ELSE qso.grid     END,
    dxcc     = CASE WHEN excluded.source IN ('QRZ','LOTW','CLUBLOG') AND excluded.dxcc >0 THEN excluded.dxcc
                    WHEN (qso.dxcc    =0  OR qso.dxcc     IS NULL) AND excluded.dxcc    >0   THEN excluded.dxcc     ELSE qso.dxcc     END,
    cq_zone  = CASE WHEN excluded.source IN ('QRZ','LOTW','CLUBLOG') AND excluded.cq_zone >0 THEN excluded.cq_zone
                    WHEN (qso.cq_zone =0  OR qso.cq_zone  IS NULL) AND excluded.cq_zone >0   THEN excluded.cq_zone  ELSE qso.cq_zone  END,
    continent    = CASE WHEN excluded.source IN ('QRZ','LOTW','CLUBLOG') AND excluded.continent !='' THEN excluded.continent
                         WHEN (qso.continent   ='' OR qso.continent    IS NULL) AND excluded.continent   !='' THEN excluded.continent   ELSE qso.continent    END,
    itu_zone     = CASE WHEN (qso.itu_zone    =0  OR qso.itu_zone     IS NULL) AND excluded.itu_zone    >0   THEN excluded.itu_zone    ELSE qso.itu_zone     END,
    county       = CASE WHEN (qso.county      ='' OR qso.county       IS NULL) AND excluded.county      !='' THEN excluded.county      ELSE qso.county       END,
    iota         = CASE WHEN (qso.iota        ='' OR qso.iota         IS NULL) AND excluded.iota        !='' THEN excluded.iota         ELSE qso.iota         END,
    sig          = CASE WHEN (qso.sig         ='' OR qso.sig          IS NULL) AND excluded.sig         !='' THEN excluded.sig          ELSE qso.sig          END,
    sig_info     = CASE WHEN (qso.sig_info    ='' OR qso.sig_info     IS NULL) AND excluded.sig_info    !='' THEN excluded.sig_info     ELSE qso.sig_info     END,
    my_sig       = CASE WHEN (qso.my_sig      ='' OR qso.my_sig       IS NULL) AND excluded.my_sig      !='' THEN excluded.my_sig       ELSE qso.my_sig       END,
    my_sig_info  = CASE WHEN (qso.my_sig_info ='' OR qso.my_sig_info  IS NULL) AND excluded.my_sig_info !='' THEN excluded.my_sig_info  ELSE qso.my_sig_info  END,
    darc_dok     = CASE WHEN (qso.darc_dok    ='' OR qso.darc_dok     IS NULL) AND excluded.darc_dok    !='' THEN excluded.darc_dok     ELSE qso.darc_dok     END,
    wpx_prefix   = CASE WHEN (qso.wpx_prefix  ='' OR qso.wpx_prefix   IS NULL) AND excluded.wpx_prefix  !='' THEN excluded.wpx_prefix   ELSE qso.wpx_prefix   END,
    exchange_sent = CASE WHEN (qso.exchange_sent ='' OR qso.exchange_sent IS NULL) AND excluded.exchange_sent !='' THEN excluded.exchange_sent ELSE qso.exchange_sent END,
    exchange_rcvd = CASE WHEN (qso.exchange_rcvd ='' OR qso.exchange_rcvd IS NULL) AND excluded.exchange_rcvd !='' THEN excluded.exchange_rcvd ELSE qso.exchange_rcvd END;
";
                    cmd.Parameters.AddWithValue("@callsign",      callsign      ?? "");
                    cmd.Parameters.AddWithValue("@band",          band          ?? "");
                    cmd.Parameters.AddWithValue("@mode",          mode          ?? "");
                    cmd.Parameters.AddWithValue("@qso_date",      qsoDate       ?? "");
                    cmd.Parameters.AddWithValue("@time_on",       timeOn        ?? "");
                    cmd.Parameters.AddWithValue("@time_off",      timeOff       ?? "");
                    cmd.Parameters.AddWithValue("@freq_hz",       freqHz);
                    cmd.Parameters.AddWithValue("@rst_sent",      rstSent       ?? "");
                    cmd.Parameters.AddWithValue("@rst_rcvd",      rstRcvd       ?? "");
                    cmd.Parameters.AddWithValue("@state",         state         ?? "");
                    cmd.Parameters.AddWithValue("@country",       country       ?? "");
                    cmd.Parameters.AddWithValue("@dxcc",          dxcc);
                    cmd.Parameters.AddWithValue("@cq_zone",       cqZone);
                    cmd.Parameters.AddWithValue("@grid",          grid          ?? "");
                    cmd.Parameters.AddWithValue("@name",          name          ?? "");
                    cmd.Parameters.AddWithValue("@comment",       comment       ?? "");
                    cmd.Parameters.AddWithValue("@tx_pwr",        txPwr         ?? "");
                    cmd.Parameters.AddWithValue("@operator_call", operatorCall  ?? "");
                    cmd.Parameters.AddWithValue("@station_call",  stationCall   ?? "");
                    cmd.Parameters.AddWithValue("@my_grid",       myGrid        ?? "");
                    cmd.Parameters.AddWithValue("@lotw_qsl_sent", lotwQslSent   ?? "");
                    cmd.Parameters.AddWithValue("@lotw_qsl_rcvd", lotwQslRcvd   ?? "");
                    cmd.Parameters.AddWithValue("@qrz_qsl_sent",  qrzQslSent    ?? "");
                    cmd.Parameters.AddWithValue("@qrz_qsl_rcvd",  qrzQslRcvd   ?? "");
                    cmd.Parameters.AddWithValue("@source",         source        ?? "MANUAL");
                    cmd.Parameters.AddWithValue("@source_qso_id",  sourceQsoId  ?? "");
                    cmd.Parameters.AddWithValue("@imported_at",    DateTime.UtcNow.ToString("o"));
                    cmd.Parameters.AddWithValue("@dedup_key",      dedupKey);
                    cmd.Parameters.AddWithValue("@continent",      continent     ?? "");
                    cmd.Parameters.AddWithValue("@itu_zone",       ituZone);
                    cmd.Parameters.AddWithValue("@county",         county        ?? "");
                    cmd.Parameters.AddWithValue("@iota",           iota          ?? "");
                    cmd.Parameters.AddWithValue("@sig",            sig           ?? "");
                    cmd.Parameters.AddWithValue("@sig_info",       sigInfo       ?? "");
                    cmd.Parameters.AddWithValue("@my_sig",         mySig         ?? "");
                    cmd.Parameters.AddWithValue("@my_sig_info",    mySigInfo     ?? "");
                    cmd.Parameters.AddWithValue("@darc_dok",       darcDok       ?? "");
                    cmd.Parameters.AddWithValue("@wpx_prefix",     wpxPrefix     ?? "");
                    cmd.Parameters.AddWithValue("@exchange_sent",  exchangeSent  ?? "");
                    cmd.Parameters.AddWithValue("@exchange_rcvd",  exchangeRcvd  ?? "");
                    cmd.ExecuteNonQuery();
                }

                bool newlyConfirmed = false;
                bool corrected = false;
                if (existed)
                {
                    using (var check2 = _conn.CreateCommand())
                    {
                        check2.CommandText = $"SELECT {mutableCols} FROM qso WHERE dedup_key=@k;";
                        check2.Parameters.AddWithValue("@k", dedupKey);
                        using (var r = check2.ExecuteReader())
                        {
                            if (r.Read())
                            {
                                var after = new object[r.FieldCount];
                                r.GetValues(after);
                                for (int i = 0; i < before.Length; i++)
                                {
                                    if (Equals(before[i], after[i])) continue;
                                    if (Array.IndexOf(ConfirmColumnIndices, i) >= 0) newlyConfirmed = true;
                                    else corrected = true;
                                }
                            }
                        }
                    }
                }

                return (!existed, newlyConfirmed, corrected);
            }
        }

        // ── Import log ───────────────────────────────────────────────────────────

        public int LogImportStart(string source)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText =
                        "INSERT INTO import_log(source, started_at) VALUES(@s,@t);" +
                        "SELECT last_insert_rowid();";
                    cmd.Parameters.AddWithValue("@s", source);
                    cmd.Parameters.AddWithValue("@t", DateTime.UtcNow.ToString("o"));
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
        }

        public void LogImportFinish(int logId, int total, int newCount, int newlyConfirmed, int corrected, int skipped, string errorText)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText =
                        "UPDATE import_log SET finished_at=@f, total_qso=@t, new_qso=@n, " +
                        "newly_confirmed_qso=@c, corrected_qso=@r, skipped_qso=@s, error_text=@e WHERE id=@id;";
                    cmd.Parameters.AddWithValue("@f",  DateTime.UtcNow.ToString("o"));
                    cmd.Parameters.AddWithValue("@t",  total);
                    cmd.Parameters.AddWithValue("@n",  newCount);
                    cmd.Parameters.AddWithValue("@c",  newlyConfirmed);
                    cmd.Parameters.AddWithValue("@r",  corrected);
                    cmd.Parameters.AddWithValue("@s",  skipped);
                    cmd.Parameters.AddWithValue("@e",  errorText ?? "");
                    cmd.Parameters.AddWithValue("@id", logId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public List<ImportLogEntry> GetImportHistory(int limit = 25)
        {
            lock (_lock)
            {
                var result = new List<ImportLogEntry>();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT id, source, started_at, total_qso, new_qso, newly_confirmed_qso, corrected_qso, skipped_qso, error_text " +
                        $"FROM import_log ORDER BY id DESC LIMIT {limit};";
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            DateTime dt;
                            DateTime.TryParse(r.IsDBNull(2) ? "" : r.GetString(2), out dt);
                            result.Add(new ImportLogEntry
                            {
                                Id             = r.GetInt32(0),
                                Source         = r.IsDBNull(1) ? "" : r.GetString(1),
                                StartedAt      = dt,
                                TotalQso       = r.IsDBNull(3) ? 0  : r.GetInt32(3),
                                NewQso         = r.IsDBNull(4) ? 0  : r.GetInt32(4),
                                NewlyConfirmed = r.IsDBNull(5) ? 0  : r.GetInt32(5),
                                Corrected      = r.IsDBNull(6) ? 0  : r.GetInt32(6),
                                SkippedQso     = r.IsDBNull(7) ? 0  : r.GetInt32(7),
                                ErrorText      = r.IsDBNull(8) ? "" : r.GetString(8),
                            });
                        }
                    }
                }
                return result;
            }
        }

        // ── Outbound upload tracking (QRZ / Club Log logbook upload) ───────────────

        public class PendingUploadQso
        {
            public string Callsign, Band, Mode, QsoDate, TimeOn, TimeOff;
            public long   FreqHz;
            public string RstSent, RstRcvd, Grid, Name, Comment, TxPwr;
            public string OperatorCall, StationCall, MyGrid, DedupKey;
            public string ExchangeSent, ExchangeRcvd;
        }

        // service is "qrz", "clublog", "lotw", or "hrdlog" (see UploadColumn). Returns QSOs never yet uploaded to that
        // service, oldest first, so a batch upload sends them in QSO order.
        public List<PendingUploadQso> GetPendingUploads(string service, int limit = 1000)
        {
            string col = UploadColumn(service);
            lock (_lock)
            {
                var result = new List<PendingUploadQso>();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText =
                        $"SELECT callsign, band, mode, qso_date, time_on, time_off, freq_hz, " +
                        $"rst_sent, rst_rcvd, grid, name, comment, tx_pwr, operator_call, station_call, my_grid, dedup_key, " +
                        $"exchange_sent, exchange_rcvd " +
                        $"FROM qso WHERE {col} = '' ORDER BY qso_date, time_on LIMIT {limit};";
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            result.Add(new PendingUploadQso
                            {
                                Callsign     = r.IsDBNull(0)  ? "" : r.GetString(0),
                                Band         = r.IsDBNull(1)  ? "" : r.GetString(1),
                                Mode         = r.IsDBNull(2)  ? "" : r.GetString(2),
                                QsoDate      = r.IsDBNull(3)  ? "" : r.GetString(3),
                                TimeOn       = r.IsDBNull(4)  ? "" : r.GetString(4),
                                TimeOff      = r.IsDBNull(5)  ? "" : r.GetString(5),
                                FreqHz       = r.IsDBNull(6)  ? 0  : r.GetInt64(6),
                                RstSent      = r.IsDBNull(7)  ? "" : r.GetString(7),
                                RstRcvd      = r.IsDBNull(8)  ? "" : r.GetString(8),
                                Grid         = r.IsDBNull(9)  ? "" : r.GetString(9),
                                Name         = r.IsDBNull(10) ? "" : r.GetString(10),
                                Comment      = r.IsDBNull(11) ? "" : r.GetString(11),
                                TxPwr        = r.IsDBNull(12) ? "" : r.GetString(12),
                                OperatorCall = r.IsDBNull(13) ? "" : r.GetString(13),
                                StationCall  = r.IsDBNull(14) ? "" : r.GetString(14),
                                MyGrid       = r.IsDBNull(15) ? "" : r.GetString(15),
                                DedupKey     = r.IsDBNull(16) ? "" : r.GetString(16),
                                ExchangeSent = r.IsDBNull(17) ? "" : r.GetString(17),
                                ExchangeRcvd = r.IsDBNull(18) ? "" : r.GetString(18),
                            });
                        }
                    }
                }
                return result;
            }
        }

        public void MarkUploaded(string dedupKey, string service, DateTime whenUtc)
        {
            string col = UploadColumn(service);
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = $"UPDATE qso SET {col} = @w WHERE dedup_key = @k;";
                    cmd.Parameters.AddWithValue("@w", whenUtc.ToString("o"));
                    cmd.Parameters.AddWithValue("@k", dedupKey);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static string UploadColumn(string service)
        {
            switch ((service ?? "").ToUpperInvariant())
            {
                case "QRZ":     return "qrz_uploaded_at";
                case "CLUBLOG": return "clublog_uploaded_at";
                case "LOTW":    return "lotw_uploaded_at";
                case "HRDLOG":  return "hrdlog_uploaded_at";
                case "EQSL":    return "eqsl_uploaded_at";
                default: throw new ArgumentException("Unknown upload service: " + service);
            }
        }

        public enum EqslReconcileOutcome { Matched, AlreadyConfirmed, Ambiguous, Unmatched }

        // Conservative, deterministic, idempotent match against EXISTING qso rows -- unlike
        // Upsert/AdifImporter.Import (which QRZ/LoTW/Club Log downloads use, and which CAN
        // create a new row for a record Jimmy Next doesn't have yet), this method never
        // creates a row and never guesses: an eQSL InBox "confirmation" is someone else's
        // report about a QSO, not a request to add one, so ambiguous or absent matches are
        // simply left alone (see EqslReconciler, the caller). Match key: callsign + band
        // exactly, qso_date within +/-1 day (mirrors Nexus's own LoTW reconcile tolerance for
        // midnight-boundary clock skew between the two operators' logs -- docs/manual/
        // Logbook-and-Awards.md). If more than one row matches that window, mode (when the
        // eQSL record carries one) narrows it; if still ambiguous, no row is touched.
        // Never clears eqsl_qsl_rcvd once set -- monotonic, same as LoTW/QRZ confirmation.
        public EqslReconcileOutcome TryMarkEqslConfirmed(string callsign, string band, string qsoDateAdif, string mode)
        {
            if (string.IsNullOrWhiteSpace(callsign) || string.IsNullOrWhiteSpace(band) || string.IsNullOrWhiteSpace(qsoDateAdif))
                return EqslReconcileOutcome.Unmatched;

            string call = callsign.Trim().ToUpperInvariant();
            string bandNorm = band.Trim().ToLowerInvariant();
            var dateWindow = DateWindow(qsoDateAdif);
            if (dateWindow.Length == 0) return EqslReconcileOutcome.Unmatched;

            lock (_lock)
            {
                var candidates = new List<(long id, string eqslRcvd)>();
                using (var cmd = _conn.CreateCommand())
                {
                    string dateParams = string.Join(",", Enumerable.Range(0, dateWindow.Length).Select(i => $"@d{i}"));
                    cmd.CommandText = $"SELECT id, eqsl_qsl_rcvd FROM qso WHERE callsign=@call AND band=@band AND qso_date IN ({dateParams});";
                    cmd.Parameters.AddWithValue("@call", call);
                    cmd.Parameters.AddWithValue("@band", bandNorm);
                    for (int i = 0; i < dateWindow.Length; i++)
                        cmd.Parameters.AddWithValue($"@d{i}", dateWindow[i]);
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            candidates.Add((r.GetInt64(0), r.IsDBNull(1) ? "" : r.GetString(1)));
                }

                if (candidates.Count == 0) return EqslReconcileOutcome.Unmatched;

                if (candidates.Count > 1 && !string.IsNullOrWhiteSpace(mode))
                {
                    var narrowed = new List<(long id, string eqslRcvd)>();
                    using (var cmd = _conn.CreateCommand())
                    {
                        string dateParams = string.Join(",", Enumerable.Range(0, dateWindow.Length).Select(i => $"@d{i}"));
                        cmd.CommandText = $"SELECT id, eqsl_qsl_rcvd FROM qso WHERE callsign=@call AND band=@band AND qso_date IN ({dateParams}) AND mode=@mode COLLATE NOCASE;";
                        cmd.Parameters.AddWithValue("@call", call);
                        cmd.Parameters.AddWithValue("@band", bandNorm);
                        cmd.Parameters.AddWithValue("@mode", mode.Trim());
                        for (int i = 0; i < dateWindow.Length; i++)
                            cmd.Parameters.AddWithValue($"@d{i}", dateWindow[i]);
                        using (var r = cmd.ExecuteReader())
                            while (r.Read())
                                narrowed.Add((r.GetInt64(0), r.IsDBNull(1) ? "" : r.GetString(1)));
                    }
                    if (narrowed.Count > 0) candidates = narrowed;
                }

                if (candidates.Count != 1) return EqslReconcileOutcome.Ambiguous;

                var (id, eqslRcvd) = candidates[0];
                if (eqslRcvd == "Y") return EqslReconcileOutcome.AlreadyConfirmed;

                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "UPDATE qso SET eqsl_qsl_rcvd='Y' WHERE id=@id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.ExecuteNonQuery();
                }
                return EqslReconcileOutcome.Matched;
            }
        }

        // qsoDateAdif is ADIF's YYYYMMDD. Returns [date-1, date, date+1] as YYYYMMDD strings
        // for the +/-1 day match window, or an empty array if the date can't be parsed (caller
        // treats that as Unmatched rather than matching everything).
        private static string[] DateWindow(string qsoDateAdif)
        {
            if (!DateTime.TryParseExact(qsoDateAdif.Trim(), "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
                return Array.Empty<string>();
            return new[]
            {
                d.AddDays(-1).ToString("yyyyMMdd"),
                d.ToString("yyyyMMdd"),
                d.AddDays(1).ToString("yyyyMMdd"),
            };
        }

        public class UploadSyncStatus
        {
            public int      PendingCount;
            public int      UploadedCount;
            public DateTime? LastUploadUtc;
        }

        // service is "qrz", "clublog", "lotw", or "hrdlog" (see UploadColumn). Cheap summary for the Sync Status
        // display -- avoids pulling full QSO rows just to show a count.
        public UploadSyncStatus GetUploadSyncStatus(string service)
        {
            string col = UploadColumn(service);
            lock (_lock)
            {
                var status = new UploadSyncStatus();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT COUNT(*) FROM qso WHERE {col} = '';";
                    var r = cmd.ExecuteScalar();
                    status.PendingCount = r == null || r == DBNull.Value ? 0 : Convert.ToInt32(r);
                }
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT COUNT(*) FROM qso WHERE {col} != '';";
                    var r = cmd.ExecuteScalar();
                    status.UploadedCount = r == null || r == DBNull.Value ? 0 : Convert.ToInt32(r);
                }
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT MAX({col}) FROM qso WHERE {col} != '';";
                    var r = cmd.ExecuteScalar();
                    if (r != null && r != DBNull.Value)
                    {
                        DateTime when;
                        if (DateTime.TryParse(Convert.ToString(r), null,
                                System.Globalization.DateTimeStyles.RoundtripKind, out when))
                            status.LastUploadUtc = when;
                    }
                }
                return status;
            }
        }

        // ── Scalar helpers ───────────────────────────────────────────────────────

        public int QueryScalar(string sql, params SQLiteParameter[] parms)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    foreach (var p in parms) cmd.Parameters.Add(p);
                    var r = cmd.ExecuteScalar();
                    return r == null || r == DBNull.Value ? 0 : Convert.ToInt32(r);
                }
            }
        }

        public int TotalQsos(string source = null)
        {
            string w = source != null ? $" WHERE source='{EscSql(source)}'" : "";
            return QueryScalar($"SELECT COUNT(*) FROM qso{w};");
        }

        public int ConfirmedQsos(string source = null)
        {
            string w  = source != null ? $" AND source='{EscSql(source)}'" : "";
            return QueryScalar($"SELECT COUNT(*) FROM qso WHERE (lotw_qsl_rcvd='Y' OR qrz_qsl_rcvd='Y'){w};");
        }

        public int LotwConfirmedQsos() =>
            QueryScalar("SELECT COUNT(*) FROM qso WHERE lotw_qsl_rcvd='Y';");

        public int QrzConfirmedQsos() =>
            QueryScalar("SELECT COUNT(*) FROM qso WHERE qrz_qsl_rcvd='Y';");

        // Informational only -- eqsl_qsl_rcvd never feeds ConfirmedQsos/RuleConfirmation's
        // award counts (see TryMarkEqslConfirmed's own comment: eQSL is not an ARRL-recognized
        // DXCC/WAS confirmation source).
        public int EqslConfirmedQsos() =>
            QueryScalar("SELECT COUNT(*) FROM qso WHERE eqsl_qsl_rcvd='Y';");

        // ── Band / mode / year stats ──────────────────────────────────────────────

        public List<BandStat> GetBandStats(string source = null)
        {
            string w = source != null ? $" AND source='{EscSql(source)}'" : "";
            return QueryBandStats(
                "SELECT band, COUNT(*) as total, " +
                "SUM(CASE WHEN lotw_qsl_rcvd='Y' OR qrz_qsl_rcvd='Y' THEN 1 ELSE 0 END) as confirmed " +
                $"FROM qso WHERE band!='' AND band IS NOT NULL{w} GROUP BY band ORDER BY band;");
        }

        public List<BandStat> GetModeStats(string source = null)
        {
            string w = source != null ? $" AND source='{EscSql(source)}'" : "";
            return QueryBandStats(
                "SELECT mode, COUNT(*) as total, " +
                "SUM(CASE WHEN lotw_qsl_rcvd='Y' OR qrz_qsl_rcvd='Y' THEN 1 ELSE 0 END) as confirmed " +
                $"FROM qso WHERE mode!='' AND mode IS NOT NULL{w} GROUP BY mode ORDER BY total DESC;");
        }

        public List<BandStat> GetYearStats(string source = null)
        {
            string w = source != null ? $" AND source='{EscSql(source)}'" : "";
            return QueryBandStats(
                "SELECT SUBSTR(qso_date,1,4) as yr, COUNT(*) as total, " +
                "SUM(CASE WHEN lotw_qsl_rcvd='Y' OR qrz_qsl_rcvd='Y' THEN 1 ELSE 0 END) as confirmed " +
                $"FROM qso WHERE LENGTH(qso_date)>=4{w} GROUP BY yr ORDER BY yr DESC;");
        }

        private List<BandStat> QueryBandStats(string sql)
        {
            lock (_lock)
            {
                var result = new List<BandStat>();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                            result.Add(new BandStat
                            {
                                Label     = r.IsDBNull(0) ? "?" : r.GetString(0),
                                Total     = r.GetInt32(1),
                                Confirmed = r.GetInt32(2),
                            });
                    }
                }
                return result;
            }
        }

        // ── Award progress ───────────────────────────────────────────────────────

        public (int worked, int confirmed) WasProgress(string band = null)
        {
            string bf = BandFilter(band);
            // COUNT(DISTINCT ...) must normalize the same way the WHERE filter does -- the raw
            // state column can hold case variants of the same state (e.g. "PA" and "Pa" both
            // resolve to Pennsylvania), which the un-normalized count previously treated as two
            // different states, inflating "worked" past the true 50-state ceiling (found
            // 2026-07-09: displayed 51/50 worked).
            return (
                QueryScalar($"SELECT COUNT(DISTINCT UPPER(TRIM(state))) FROM qso WHERE UPPER(TRIM(state)) IN ({WasInList}){bf};"),
                QueryScalar($"SELECT COUNT(DISTINCT UPPER(TRIM(state))) FROM qso WHERE UPPER(TRIM(state)) IN ({WasInList}) AND (lotw_qsl_rcvd='Y' OR qrz_qsl_rcvd='Y'){bf};")
            );
        }

        public (int worked, int confirmed) DxccProgress(string band = null)
        {
            string bf = BandFilter(band);
            return (
                QueryScalar($"SELECT COUNT(DISTINCT dxcc) FROM qso WHERE dxcc>0{bf};"),
                QueryScalar($"SELECT COUNT(DISTINCT dxcc) FROM qso WHERE dxcc>0 AND (lotw_qsl_rcvd='Y' OR qrz_qsl_rcvd='Y'){bf};")
            );
        }

        public (int worked, int confirmed) WazProgress(string band = null)
        {
            string bf = BandFilter(band);
            return (
                QueryScalar($"SELECT COUNT(DISTINCT cq_zone) FROM qso WHERE cq_zone>0 AND cq_zone<=40{bf};"),
                QueryScalar($"SELECT COUNT(DISTINCT cq_zone) FROM qso WHERE cq_zone>0 AND cq_zone<=40 AND (lotw_qsl_rcvd='Y' OR qrz_qsl_rcvd='Y'){bf};")
            );
        }

        // ── Dashboard ─────────────────────────────────────────────────────────────

        public List<QsoRecord> GetRecentQsos(int limit = 10)
        {
            lock (_lock)
            {
                var result = new List<QsoRecord>();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT qso_date, time_on, callsign, band, mode, country, lotw_qsl_rcvd, qrz_qsl_rcvd " +
                        $"FROM qso ORDER BY qso_date DESC, time_on DESC LIMIT {limit};";
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                            result.Add(new QsoRecord
                            {
                                QsoDate     = Str(r, 0),
                                TimeOn      = Str(r, 1),
                                Callsign    = Str(r, 2),
                                Band        = Str(r, 3),
                                Mode        = Str(r, 4),
                                Country     = Str(r, 5),
                                LotwQslRcvd = Str(r, 6),
                                QrzQslRcvd  = Str(r, 7),
                            });
                    }
                }
                return result;
            }
        }

        // Read-only "worked before" check, added for the Classification Engine (migration
        // Stage A1 -- independently deriving what EnqueueDecodeMessage's wire-supplied
        // IsNewCallOnBand/IsNewCallAnyBand fields currently give Jimmy). band == null
        // checks "any band"; a non-null band restricts to that exact band. Does not
        // mutate any state.
        public bool HasWorkedBefore(string callsign, string band = null)
        {
            if (string.IsNullOrEmpty(callsign)) return false;
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = band == null
                        ? "SELECT COUNT(*) FROM qso WHERE callsign = @callsign COLLATE NOCASE;"
                        : "SELECT COUNT(*) FROM qso WHERE callsign = @callsign COLLATE NOCASE AND band = @band COLLATE NOCASE;";
                    cmd.Parameters.AddWithValue("@callsign", callsign);
                    if (band != null) cmd.Parameters.AddWithValue("@band", band);
                    long count = (long)cmd.ExecuteScalar();
                    return count > 0;
                }
            }
        }

        // Read-only "worked this DXCC entity before" check, added alongside
        // HasWorkedBefore for the Classification Engine (migration Stage A1) --
        // independently deriving what EnqueueDecodeMessage's wire-supplied
        // IsNewCountry/IsNewCountryOnBand fields currently give Jimmy. dxcc <= 0
        // (unknown entity) always returns false, matching "not classifiable as new/not
        // new" rather than a false "new country". Does not mutate any state.
        public bool HasWorkedDxcc(int dxcc, string band = null)
        {
            if (dxcc <= 0) return false;
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = band == null
                        ? "SELECT COUNT(*) FROM qso WHERE dxcc = @dxcc;"
                        : "SELECT COUNT(*) FROM qso WHERE dxcc = @dxcc AND band = @band COLLATE NOCASE;";
                    cmd.Parameters.AddWithValue("@dxcc", dxcc);
                    if (band != null) cmd.Parameters.AddWithValue("@band", band);
                    long count = (long)cmd.ExecuteScalar();
                    return count > 0;
                }
            }
        }

        public Dictionary<string, int> GetSourceCounts()
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT source, COUNT(*) FROM qso GROUP BY source;";
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                            result[r.IsDBNull(0)?"?":r.GetString(0)] = r.GetInt32(1);
                    }
                }
            }
            return result;
        }

        // ── Search ────────────────────────────────────────────────────────────────

        public List<QsoRecord> SearchByCallsign(string pattern, int limit = 200)
        {
            if (!pattern.Contains("%")) pattern = pattern.ToUpperInvariant() + "%";
            lock (_lock)
            {
                var result = new List<QsoRecord>();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT qso_date, time_on, callsign, band, mode, state, country, dxcc, " +
                        "lotw_qsl_rcvd, qrz_qsl_rcvd, source " +
                        $"FROM qso WHERE callsign LIKE @p ORDER BY qso_date DESC, time_on DESC LIMIT {limit};";
                    cmd.Parameters.AddWithValue("@p", pattern);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                            result.Add(new QsoRecord
                            {
                                QsoDate     = Str(r,  0),
                                TimeOn      = Str(r,  1),
                                Callsign    = Str(r,  2),
                                Band        = Str(r,  3),
                                Mode        = Str(r,  4),
                                State       = Str(r,  5),
                                Country     = Str(r,  6),
                                Dxcc        = r.IsDBNull(7) ? 0 : r.GetInt32(7),
                                LotwQslRcvd = Str(r,  8),
                                QrzQslRcvd  = Str(r,  9),
                                Source      = Str(r, 10),
                            });
                    }
                }
                return result;
            }
        }

        // ── Edit Log (search/edit/delete/export) ────────────────────────────────────
        // Backs the Edit Log tab: unlike SearchByCallsign (a quick "have I worked this
        // call" lookup), this supports filtering by source and date range too, and
        // returns the row id so callers can Edit/Delete/Export specific records.

        private static readonly string[] EditLogSelectCols =
        {
            "id", "qso_date", "time_on", "time_off", "callsign", "band", "mode",
            "state", "country", "grid", "name", "rst_sent", "rst_rcvd", "comment",
            "dxcc", "cq_zone", "lotw_qsl_rcvd", "qrz_qsl_rcvd", "source",
        };

        private static QsoRecord ReadEditLogRow(IDataReader r) => new QsoRecord
        {
            Id          = r.GetInt32(0),
            QsoDate     = Str(r, 1),
            TimeOn      = Str(r, 2),
            TimeOff     = Str(r, 3),
            Callsign    = Str(r, 4),
            Band        = Str(r, 5),
            Mode        = Str(r, 6),
            State       = Str(r, 7),
            Country     = Str(r, 8),
            Grid        = Str(r, 9),
            Name        = Str(r, 10),
            RstSent     = Str(r, 11),
            RstRcvd     = Str(r, 12),
            Comment     = Str(r, 13),
            Dxcc        = r.IsDBNull(14) ? 0 : r.GetInt32(14),
            CqZone      = r.IsDBNull(15) ? 0 : r.GetInt32(15),
            LotwQslRcvd = Str(r, 16),
            QrzQslRcvd  = Str(r, 17),
            Source      = Str(r, 18),
        };

        // callsignPattern/source/dateFrom/dateTo are all optional (null/blank = no filter).
        // dateFrom/dateTo are inclusive, expected in qso_date's own YYYYMMDD form.
        public List<QsoRecord> SearchQsos(string callsignPattern, string source,
            string dateFrom, string dateTo, int limit = 500)
        {
            lock (_lock)
            {
                var result = new List<QsoRecord>();
                using (var cmd = _conn.CreateCommand())
                {
                    var where = new List<string>();
                    if (!string.IsNullOrWhiteSpace(callsignPattern))
                    {
                        string p = callsignPattern.Trim().ToUpperInvariant();
                        if (!p.Contains("%")) p += "%";
                        where.Add("callsign LIKE @call");
                        cmd.Parameters.AddWithValue("@call", p);
                    }
                    if (!string.IsNullOrWhiteSpace(source))
                    {
                        where.Add("source = @source");
                        cmd.Parameters.AddWithValue("@source", source);
                    }
                    if (!string.IsNullOrWhiteSpace(dateFrom))
                    {
                        where.Add("qso_date >= @dfrom");
                        cmd.Parameters.AddWithValue("@dfrom", dateFrom);
                    }
                    if (!string.IsNullOrWhiteSpace(dateTo))
                    {
                        where.Add("qso_date <= @dto");
                        cmd.Parameters.AddWithValue("@dto", dateTo);
                    }
                    string whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
                    cmd.CommandText =
                        $"SELECT {string.Join(", ", EditLogSelectCols)} FROM qso {whereClause} " +
                        $"ORDER BY qso_date DESC, time_on DESC LIMIT {limit};";
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                            result.Add(ReadEditLogRow(r));
                    }
                }
                return result;
            }
        }

        public QsoRecord GetQso(int id)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT {string.Join(", ", EditLogSelectCols)} FROM qso WHERE id=@id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    using (var r = cmd.ExecuteReader())
                        return r.Read() ? ReadEditLogRow(r) : null;
                }
            }
        }

        // Updates the user-editable fields of one QSO. Recomputes dedup_key from the
        // (possibly changed) callsign/band/mode/date/time -- if that now collides with
        // another existing row's dedup_key, the UNIQUE constraint throws and the caller
        // is expected to show that as a "duplicate" error rather than silently merge.
        public bool UpdateQso(int id, string callsign, string band, string mode,
            string qsoDate, string timeOn, string timeOff, string state, string country,
            string grid, string name, string rstSent, string rstRcvd, string comment)
        {
            callsign = (callsign ?? "").Trim().ToUpperInvariant();
            band     = (band     ?? "").Trim().ToLowerInvariant();
            mode     = (mode     ?? "").Trim().ToUpperInvariant();
            qsoDate  = (qsoDate  ?? "").Trim();
            timeOn   = (timeOn   ?? "").Trim();
            string dedupKey = AdifImporter.BuildDedupKey(callsign, band, mode, qsoDate, timeOn);

            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"
UPDATE qso SET
    callsign=@callsign, band=@band, mode=@mode, qso_date=@qso_date, time_on=@time_on,
    time_off=@time_off, state=@state, country=@country, grid=@grid, name=@name,
    rst_sent=@rst_sent, rst_rcvd=@rst_rcvd, comment=@comment, dedup_key=@dedup_key
WHERE id=@id;";
                    cmd.Parameters.AddWithValue("@callsign",  callsign);
                    cmd.Parameters.AddWithValue("@band",      band);
                    cmd.Parameters.AddWithValue("@mode",      mode);
                    cmd.Parameters.AddWithValue("@qso_date",  qsoDate);
                    cmd.Parameters.AddWithValue("@time_on",   timeOn);
                    cmd.Parameters.AddWithValue("@time_off",  (timeOff ?? "").Trim());
                    cmd.Parameters.AddWithValue("@state",     (state   ?? "").Trim().ToUpperInvariant());
                    cmd.Parameters.AddWithValue("@country",   (country ?? "").Trim());
                    cmd.Parameters.AddWithValue("@grid",      (grid    ?? "").Trim().ToUpperInvariant());
                    cmd.Parameters.AddWithValue("@name",      (name    ?? "").Trim());
                    cmd.Parameters.AddWithValue("@rst_sent",  (rstSent ?? "").Trim());
                    cmd.Parameters.AddWithValue("@rst_rcvd",  (rstRcvd ?? "").Trim());
                    cmd.Parameters.AddWithValue("@comment",   (comment ?? "").Trim());
                    cmd.Parameters.AddWithValue("@dedup_key", dedupKey);
                    cmd.Parameters.AddWithValue("@id", id);
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        // Deletes the given rows by id. Local-only -- never touches QRZ/Club Log/LoTW;
        // those remain separate services a local delete has no effect on.
        public int DeleteQsos(IEnumerable<int> ids)
        {
            var idList = ids?.Distinct().ToList() ?? new List<int>();
            if (idList.Count == 0) return 0;
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    string placeholders = string.Join(",", idList.Select((_, i) => $"@id{i}"));
                    cmd.CommandText = $"DELETE FROM qso WHERE id IN ({placeholders});";
                    for (int i = 0; i < idList.Count; i++)
                        cmd.Parameters.AddWithValue($"@id{i}", idList[i]);
                    return cmd.ExecuteNonQuery();
                }
            }
        }

        // Returns full-fidelity ADIF field dictionaries (every stored column, not just
        // the Edit Log tab's display subset) for export. ids null/empty exports every QSO.
        // sources null/empty applies no source filter; otherwise only rows whose "source"
        // column matches one of the given values are included.
        public List<Dictionary<string, string>> GetAdifFieldDicts(IEnumerable<int> ids, IEnumerable<string> sources = null)
        {
            var idList = ids?.Distinct().ToList();
            var sourceList = sources?.Distinct().ToList();
            lock (_lock)
            {
                var result = new List<Dictionary<string, string>>();
                using (var cmd = _conn.CreateCommand())
                {
                    var clauses = new List<string>();
                    if (idList != null && idList.Count > 0)
                    {
                        string placeholders = string.Join(",", idList.Select((_, i) => $"@id{i}"));
                        clauses.Add($"id IN ({placeholders})");
                        for (int i = 0; i < idList.Count; i++)
                            cmd.Parameters.AddWithValue($"@id{i}", idList[i]);
                    }
                    if (sourceList != null && sourceList.Count > 0)
                    {
                        string placeholders = string.Join(",", sourceList.Select((_, i) => $"@src{i}"));
                        clauses.Add($"source IN ({placeholders})");
                        for (int i = 0; i < sourceList.Count; i++)
                            cmd.Parameters.AddWithValue($"@src{i}", sourceList[i]);
                    }
                    string whereClause = clauses.Count > 0 ? "WHERE " + string.Join(" AND ", clauses) : "";
                    cmd.CommandText = $"SELECT * FROM qso {whereClause} ORDER BY qso_date, time_on;";
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var f = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            void AddText(string tag, string col)
                            {
                                string v = StrByName(r, col);
                                if (v.Length > 0) f[tag] = v;
                            }
                            void AddNum(string tag, string col)
                            {
                                long v;
                                long.TryParse(StrByName(r, col), out v);
                                if (v > 0) f[tag] = v.ToString();
                            }

                            AddText("CALL", "callsign");
                            AddText("BAND", "band");
                            AddText("MODE", "mode");
                            AddText("QSO_DATE", "qso_date");
                            AddText("TIME_ON", "time_on");
                            AddText("TIME_OFF", "time_off");
                            long freqHz;
                            long.TryParse(StrByName(r, "freq_hz"), out freqHz);
                            if (freqHz > 0)
                                f["FREQ"] = (freqHz / 1_000_000.0).ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
                            AddText("RST_SENT", "rst_sent");
                            AddText("RST_RCVD", "rst_rcvd");
                            AddText("STATE", "state");
                            AddText("COUNTRY", "country");
                            AddNum ("DXCC", "dxcc");
                            AddNum ("CQZ", "cq_zone");
                            AddText("GRIDSQUARE", "grid");
                            AddText("NAME", "name");
                            AddText("COMMENT", "comment");
                            AddText("TX_PWR", "tx_pwr");
                            AddText("OPERATOR", "operator_call");
                            AddText("STATION_CALLSIGN", "station_call");
                            AddText("MY_GRIDSQUARE", "my_grid");
                            AddText("LOTW_QSL_SENT", "lotw_qsl_sent");
                            AddText("LOTW_QSL_RCVD", "lotw_qsl_rcvd");
                            AddText("QSL_SENT", "qrz_qsl_sent");
                            AddText("QSL_RCVD", "qrz_qsl_rcvd");
                            // Jimmy-specific bookkeeping (which source last supplied this row) --
                            // not a standard ADIF field, kept as an APP-namespaced tag so a
                            // re-import of this export elsewhere doesn't misread it as anything else.
                            AddText("APP_JIMMY_SOURCE", "source");
                            AddText("CONT", "continent");
                            AddNum ("ITUZ", "itu_zone");
                            AddText("CNTY", "county");
                            AddText("IOTA", "iota");
                            AddText("SIG", "sig");
                            AddText("SIG_INFO", "sig_info");
                            AddText("MY_SIG", "my_sig");
                            AddText("MY_SIG_INFO", "my_sig_info");
                            AddText("DARC_DOK", "darc_dok");
                            AddText("PFX", "wpx_prefix");
                            AddText("STX_STRING", "exchange_sent");
                            AddText("SRX_STRING", "exchange_rcvd");

                            result.Add(f);
                        }
                    }
                }
                return result;
            }
        }

        // Returns best-known country name per DXCC entity, derived from QSO records.
        public Dictionary<int, string> GetDxccCountryNames()
        {
            var result = new Dictionary<int, string>();
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    // Pick the most-common non-empty country name per DXCC number
                    cmd.CommandText =
                        "SELECT dxcc, country, COUNT(*) as c FROM qso " +
                        "WHERE dxcc>0 AND country!='' AND country IS NOT NULL " +
                        "GROUP BY dxcc, country ORDER BY dxcc, c DESC;";
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            int adif = r.GetInt32(0);
                            if (!result.ContainsKey(adif))
                                result[adif] = r.IsDBNull(1) ? "" : r.GetString(1);
                        }
                    }
                }
            }
            return result;
        }

        // ── Transactions (for batch imports) ──────────────────────────────────────

        public SQLiteTransaction BeginTransaction() => _conn.BeginTransaction();

        // ── Helpers ───────────────────────────────────────────────────────────────

        // ── HRC cache (used by Jimmy's tag/filter system) ─────────────────────────

        private static readonly string[] UsStates50 =
        {
            "AK","AL","AR","AZ","CA","CO","CT","DE","FL","GA","HI","IA","ID","IL","IN",
            "KS","KY","LA","MA","MD","ME","MI","MN","MO","MS","MT","NC","ND","NE","NH","NJ",
            "NM","NV","NY","OH","OK","OR","PA","RI","SC","SD","TN","TX","UT","VA","VT","WA",
            "WI","WV","WY"
        };

        // Computes the four HRC filter sets used by Jimmy's decode processor.
        // neededStates = never worked; unconfirmedStates = worked but not confirmed
        // (mirrors unconfirmedDxcc's worked-minus-confirmed split for WAS/DXCC parity).
        // All computation is local — no network access.
        // band: ADIF-style string (e.g. "20m"); null means all-band.
        public void LoadHrcCache(
            out HashSet<string> neededStates,
            out HashSet<string> unconfirmedStates,
            out HashSet<int>    unconfirmedDxcc,
            out HashSet<int>    neededZones,
            string band = null)
        {
            string bf = BandFilter(band);
            var confirmedSt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var workedSt    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var workedDx    = new HashSet<int>();
            var confirmedDx = new HashSet<int>();
            var confirmedZn = new HashSet<int>();

            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText =
                        $"SELECT DISTINCT UPPER(TRIM(state)) FROM qso " +
                        $"WHERE UPPER(TRIM(state)) IN ({WasInList}) " +
                        $"AND (lotw_qsl_rcvd='Y' OR qrz_qsl_rcvd='Y'){bf};";
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) if (!r.IsDBNull(0)) confirmedSt.Add(r.GetString(0));
                }
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText =
                        $"SELECT DISTINCT UPPER(TRIM(state)) FROM qso " +
                        $"WHERE UPPER(TRIM(state)) IN ({WasInList}){bf};";
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) if (!r.IsDBNull(0)) workedSt.Add(r.GetString(0));
                }
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT DISTINCT dxcc FROM qso WHERE dxcc>0{bf};";
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) workedDx.Add(r.GetInt32(0));
                }
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText =
                        $"SELECT DISTINCT dxcc FROM qso WHERE dxcc>0 " +
                        $"AND (lotw_qsl_rcvd='Y' OR qrz_qsl_rcvd='Y'){bf};";
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) confirmedDx.Add(r.GetInt32(0));
                }
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText =
                        $"SELECT DISTINCT cq_zone FROM qso WHERE cq_zone>0 AND cq_zone<=40 " +
                        $"AND (lotw_qsl_rcvd='Y' OR qrz_qsl_rcvd='Y'){bf};";
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) confirmedZn.Add(r.GetInt32(0));
                }
            }

            neededStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var st in UsStates50)
                if (!workedSt.Contains(st)) neededStates.Add(st);

            unconfirmedStates = new HashSet<string>(workedSt, StringComparer.OrdinalIgnoreCase);
            unconfirmedStates.ExceptWith(confirmedSt);

            unconfirmedDxcc = new HashSet<int>(workedDx);
            unconfirmedDxcc.ExceptWith(confirmedDx);

            neededZones = new HashSet<int>();
            for (int z = 1; z <= 40; z++)
                if (!confirmedZn.Contains(z)) neededZones.Add(z);
        }

        private void Exec(string sql)
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        private static string BandFilter(string band) =>
            !string.IsNullOrEmpty(band) ? $" AND band='{EscSql(band)}'" : "";

        private static string EscSql(string s) => s?.Replace("'", "''") ?? "";

        private static string Str(IDataReader r, int i) =>
            r.IsDBNull(i) ? "" : r.GetString(i);

        private static string StrByName(IDataReader r, string col)
        {
            object v = r[col];
            return (v == null || v is DBNull) ? "" : v.ToString();
        }

        public void Dispose()
        {
            try { _conn?.Close(); } catch { }
            try { _conn?.Dispose(); } catch { }
            _conn = null;
        }
    }
}
