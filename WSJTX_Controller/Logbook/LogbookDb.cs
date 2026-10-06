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
        // NEXUS_CONTEST added for the Nexus contesting foundation (phase 2/3/4) -- completed
        // contest QSOs delivered from EngineHost. See ix_nexus_contest_source_qso (LogbookDb
        // schema v10) for the idempotency constraint scoped to this source only.
        public static readonly string[] KnownSources = { JimmyNextSource, "WSJTX", "QRZ", "LOTW", "CLUBLOG", "MANUAL", "NEXUS_CONTEST" };

        // A contact Jimmy Next logged itself (operator, 2026-10-04: these were labelled "WSJTX").
        // Contacts already in the log keep the label they have; a genuine WSJT-X import stays "WSJTX".
        public const string JimmyNextSource = "Jimmy Next";
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

    // The READ COPY: a Jimmy-format database rebuilt from Nexus's rows (NexusMigration.Rebuild).
    // Read-only (ILogbookReader) -- Nexus keeps the log and every change goes through it
    // (NexusLogbookService). The schema and its migrations stay, because Rebuild creates the copy
    // through this class, and the old Jimmy logbook a first start moves in is opened with it.
    public class LogbookDb : IDisposable, ILogbookReader
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
            (NexusLogbook.Active ? NexusLogbook.ReadCachePath : JimmyDbPath);

        // Jimmy's own logbook file. DbPath is "the database readers read": this file, or -- while
        // Nexus owns the logbook -- Nexus's read-only projection (NexusLogbook).
        public static string JimmyDbPath => Path.Combine(LookupManager.DataRoot, "Logbook", "logbook.db");

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
                Exec("CREATE INDEX IF NOT EXISTS ix_grid4     ON qso(upper(substr(grid, 1, 4)));");
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
            // EngineHost/Nexus's own transport).
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

            // v10: Nexus contesting foundation, phase 2 -- the accepted minimum schema support
            // for the initial contest implementation and a reliable future migration. See
            // ARCHITECTURE.md's "Nexus contesting foundation" sections for the full design.
            //
            // contest_id/contest_session_id: which contest (Nexus's ADIF CONTEST_ID, e.g.
            // "ARRL-FIELD-DAY") and which operating instance a contact belongs to. Blank for
            // every non-contest QSO -- existing rows are untouched by this migration.
            //
            // modified_at: row-edit audit timestamp. imported_at (v1) already covers row
            // CREATION; this is the first column that tracks an EDIT distinct from that. Wired
            // into UpdateQso (the operator-edit path) only, not into every automated
            // upload/confirmation-mark UPDATE elsewhere in this file -- those are system status
            // writes, not operator edits, and are out of scope for this phase.
            //
            // qso_extra_field: lossless, ordered, duplicate-safe storage for ADIF fields Jimmy's
            // modeled qso columns don't capture (AdifExtraFields.cs). One row per OCCURRENCE
            // (never collapsed), ordinal preserves original file order. Not yet populated by
            // any export path -- SaveExtraFields/GetExtraFields are the round-trip primitive a
            // later phase wires into Import()/exporters.
            //
            // ix_nexus_contest_source_qso: the Nexus-contest idempotency constraint, scoped
            // ONLY to source='NEXUS_CONTEST' rows (a brand-new source with zero existing rows at
            // this migration -- no pre-check/repair pass needed, unlike v9's MFSK repair). A
            // partial index, not a plain unique constraint: source_qso_id is blank ('') on the
            // overwhelming majority of existing rows (MANUAL/live-logged entries never populated
            // it), and this index must never apply to them. Historical QRZ/LOTW/CLUBLOG/WSJTX/
            // MANUAL rows are untouched -- this establishes a new constraint for a new source
            // only, never merges/deletes/alters anything from another source.
            if (ver < 10)
            {
                Exec("ALTER TABLE qso ADD COLUMN contest_id TEXT DEFAULT '';");
                Exec("ALTER TABLE qso ADD COLUMN contest_session_id TEXT DEFAULT '';");
                Exec("ALTER TABLE qso ADD COLUMN modified_at TEXT DEFAULT '';");
                Exec("CREATE INDEX IF NOT EXISTS ix_contest_session ON qso(contest_session_id);");

                Exec(@"CREATE TABLE IF NOT EXISTS qso_extra_field (
                    qso_id     INTEGER NOT NULL,
                    tag_name   TEXT    NOT NULL,
                    tag_value  TEXT    NOT NULL DEFAULT '',
                    ordinal    INTEGER NOT NULL,
                    PRIMARY KEY (qso_id, ordinal)
                );");

                Exec(@"CREATE UNIQUE INDEX IF NOT EXISTS ix_nexus_contest_source_qso
                    ON qso(source, source_qso_id)
                    WHERE source = 'NEXUS_CONTEST' AND source_qso_id <> '';");

                SetMeta("db_version", "10");
            }

            // v11 (2026-10-04, location evidence): the location LoTW CONFIRMED, kept apart from
            // the logged location (lotw_*, from Jimmy's APP_JIMMY_LOTW_* fields); our own operating
            // location recorded at logging (my_*, and the TQSL station location in force with its
            // fingerprint); and the review note when location evidence conflicted. The read copy
            // is rebuilt from Nexus, so these fill from the contacts themselves -- nothing here
            // changes a stored contact.
            if (ver < 11)
            {
                foreach (var c in new[] { "lotw_state", "lotw_cnty", "lotw_grid", "my_state", "my_cnty",
                                          "tqsl_location", "tqsl_loc_fp", "loc_review" })
                    Exec($"ALTER TABLE qso ADD COLUMN {c} TEXT DEFAULT '';");
                foreach (var c in new[] { "lotw_dxcc", "lotw_cqz", "lotw_ituz", "my_dxcc", "dxcc_derived" })
                    Exec($"ALTER TABLE qso ADD COLUMN {c} INTEGER DEFAULT 0;");
                SetMeta("db_version", "11");
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

        private void SetMeta(string key, string value)
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

        // ── Outbound upload tracking (QRZ / Club Log logbook upload) ───────────────

        public class PendingUploadQso
        {
            public string Callsign, Band, Mode, QsoDate, TimeOn, TimeOff;
            public long   FreqHz;
            public string RstSent, RstRcvd, Grid, Name, Comment, TxPwr;
            public string OperatorCall, StationCall, MyGrid, DedupKey;
            public string ExchangeSent, ExchangeRcvd;
            // Our operating location as recorded at logging (2026-10-04): the TQSL station location
            // in force and its fingerprint, and MY_ fields. TqslLocation "" = logged before this
            // was recorded (legacy: signed with the configured location, as always).
            public string TqslLocation, TqslLocFp, MyState, MyCnty;
            public int MyDxcc;
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
                        $"exchange_sent, exchange_rcvd, tqsl_location, tqsl_loc_fp, my_state, my_cnty, my_dxcc " +
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
                                TqslLocation = r.IsDBNull(19) ? "" : r.GetString(19),
                                TqslLocFp    = r.IsDBNull(20) ? "" : r.GetString(20),
                                MyState      = r.IsDBNull(21) ? "" : r.GetString(21),
                                MyCnty       = r.IsDBNull(22) ? "" : r.GetString(22),
                                MyDxcc       = r.IsDBNull(23) ? 0  : Convert.ToInt32(r.GetValue(23)),
                            });
                        }
                    }
                }
                return result;
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
            // The state a contact counts for: LoTW's confirmed one when LoTW gave it, else the logged one.
            string st = RuleEngine.AwardText("state", "lotw_state");
            // COUNT(DISTINCT ...) must normalize the same way the WHERE filter does -- the raw
            // state column can hold case variants of the same state (e.g. "PA" and "Pa" both
            // resolve to Pennsylvania), which the un-normalized count previously treated as two
            // different states, inflating "worked" past the true 50-state ceiling (found
            // 2026-07-09: displayed 51/50 worked).
            return (
                QueryScalar($"SELECT COUNT(DISTINCT UPPER(TRIM({st}))) FROM qso WHERE UPPER(TRIM({st})) IN ({WasInList}){bf};"),
                QueryScalar($"SELECT COUNT(DISTINCT UPPER(TRIM({st}))) FROM qso WHERE UPPER(TRIM({st})) IN ({WasInList}) AND (lotw_qsl_rcvd='Y' OR qrz_qsl_rcvd='Y'){bf};")
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

        // "Worked this 4-character grid square before" (New grid sounds, 2026-09-29). Anything
        // that is not a grid (blank, "RR73") returns true -- never a false "new grid".
        public bool HasWorkedGrid(string grid, string band = null)
        {
            string g4 = Grid4(grid);
            if (g4 == null) return true;
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = band == null
                        ? "SELECT COUNT(*) FROM qso WHERE upper(substr(grid, 1, 4)) = @g;"
                        : "SELECT COUNT(*) FROM qso WHERE upper(substr(grid, 1, 4)) = @g AND band = @band COLLATE NOCASE;";
                    cmd.Parameters.AddWithValue("@g", g4);
                    if (band != null) cmd.Parameters.AddWithValue("@band", band);
                    return (long)cmd.ExecuteScalar() > 0;
                }
            }
        }

        internal static string Grid4(string grid)
        {
            string g = (grid ?? "").Trim().ToUpperInvariant();
            if (g.Length < 4 || g == "RR73") return null;
            return g[0] >= 'A' && g[0] <= 'R' && g[1] >= 'A' && g[1] <= 'R' && char.IsDigit(g[2]) && char.IsDigit(g[3])
                ? g.Substring(0, 4) : null;
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

        // "Search in" choices on Lookup and Edit (operator, 2026-10-02: "just my POTA contacts for
        // the day"): label and column. The column names only ever come from this list.
        internal static readonly (string Label, string Column)[] SearchFields =
        {
            ("Band", "band"), ("Mode", "mode"), ("Country", "country"), ("State", "state"),
            ("County", "county"), ("Grid", "grid"), ("Name", "name"), ("Comment", "comment"),
            ("Program", "sig"), ("Park or summit reference", "sig_info"),
            ("My program", "my_sig"), ("My park reference", "my_sig_info"),
            ("Continent", "continent"), ("IOTA", "iota"), ("Prefix", "wpx_prefix"), ("Power", "tx_pwr"),
            ("Report sent", "rst_sent"), ("Report received", "rst_rcvd"),
            ("Contest exchange sent", "exchange_sent"), ("Contest exchange received", "exchange_rcvd"),
        };
        internal const string AllFieldsLabel = "All fields";

        // Upload status search (operator, 2026-10-06): one service and its state. Club Log and
        // HRDLog keep no confirmations, so they have "not sent" and "sent" only. With bulk edit's
        // "Mark not sent to", it finds e.g. contacts marked sent to LoTW that LoTW never confirmed.
        internal const string AnyUploadLabel = "Any";
        internal static readonly (string Label, string Uploaded, string Confirmed, string State)[] UploadFilters =
        {
            ("LoTW: not sent", "lotw_uploaded_at", null, "notsent"),
            ("LoTW: sent, not confirmed", "lotw_uploaded_at", "lotw_qsl_rcvd", "sentunconfirmed"),
            ("LoTW: confirmed", "lotw_uploaded_at", "lotw_qsl_rcvd", "confirmed"),
            ("QRZ: not sent", "qrz_uploaded_at", null, "notsent"),
            ("QRZ: sent, not confirmed", "qrz_uploaded_at", "qrz_qsl_rcvd", "sentunconfirmed"),
            ("QRZ: confirmed", "qrz_uploaded_at", "qrz_qsl_rcvd", "confirmed"),
            ("eQSL: not sent", "eqsl_uploaded_at", null, "notsent"),
            ("eQSL: sent, not confirmed", "eqsl_uploaded_at", "eqsl_qsl_rcvd", "sentunconfirmed"),
            ("eQSL: confirmed", "eqsl_uploaded_at", "eqsl_qsl_rcvd", "confirmed"),
            ("Club Log: not sent", "clublog_uploaded_at", null, "notsent"),
            ("Club Log: sent", "clublog_uploaded_at", null, "sent"),
            ("HRDLog: not sent", "hrdlog_uploaded_at", null, "notsent"),
            ("HRDLog: sent", "hrdlog_uploaded_at", null, "sent"),
        };

        // The SQL condition for UploadFilters[index]; null for a bad index.
        internal static string UploadFilterSql(int index)
        {
            if (index < 0 || index >= UploadFilters.Length) return null;
            var (_, up, conf, state) = UploadFilters[index];
            switch (state)
            {
                case "notsent":         return $"IFNULL({up},'') = ''";
                case "sent":            return $"IFNULL({up},'') <> ''";
                case "sentunconfirmed": return $"IFNULL({up},'') <> '' AND IFNULL({conf},'') <> 'Y'";
                case "confirmed":       return $"IFNULL({conf},'') = 'Y'";
                default:                return null;
            }
        }

        // callsignPattern/source/dateFrom/dateTo are all optional (null/blank = no filter).
        // dateFrom/dateTo are inclusive, expected in qso_date's own YYYYMMDD form.
        // searchText (optional): found anywhere in searchField (a SearchFields column), or in the
        // callsign or any SearchFields column when searchField is null. Capitals do not matter.
        public List<QsoRecord> SearchQsos(string callsignPattern, string source,
            string dateFrom, string dateTo, int limit = 500, string searchField = null, string searchText = null,
            int uploadFilter = -1)
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
                    if (!string.IsNullOrWhiteSpace(searchText))
                    {
                        var cols = searchField == null
                            ? new[] { "callsign" }.Concat(SearchFields.Select(f => f.Column)).ToArray()
                            : SearchFields.Where(f => f.Column == searchField).Select(f => f.Column).ToArray();
                        if (cols.Length == 0) throw new ArgumentException("Unknown search field: " + searchField);
                        // Typed % and _ are plain characters, not wildcards.
                        where.Add("(" + string.Join(" OR ", cols.Select(c => $"IFNULL({c},'') LIKE @text ESCAPE '\\'")) + ")");
                        string t = searchText.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
                        cmd.Parameters.AddWithValue("@text", "%" + t + "%");
                    }
                    if (uploadFilter >= 0)
                    {
                        string upload = UploadFilterSql(uploadFilter);
                        if (upload == null) throw new ArgumentException("Unknown upload status filter: " + uploadFilter);
                        where.Add("(" + upload + ")");
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

        // Returns extras in original file order (ordinal ascending) -- see AdifExtraFields.
        public List<(string Tag, string Value)> GetExtraFields(long qsoId)
        {
            var result = new List<(string Tag, string Value)>();
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT tag_name, tag_value FROM qso_extra_field WHERE qso_id=@id ORDER BY ordinal;";
                    cmd.Parameters.AddWithValue("@id", qsoId);
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            result.Add((r.GetString(0), r.GetString(1)));
                }
            }
            return result;
        }

        // Looks up a row's id by its dedup_key -- used to associate freshly-upserted extras with
        // the correct row without changing Upsert's own long-established return signature.
        public long? GetIdByDedupKey(string dedupKey)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT id FROM qso WHERE dedup_key=@k;";
                    cmd.Parameters.AddWithValue("@k", dedupKey);
                    var result = cmd.ExecuteScalar();
                    return result == null || result == DBNull.Value ? (long?)null : Convert.ToInt64(result);
                }
            }
        }

        // Nexus contesting foundation, phase 5: every authoritative row for one contest session,
        // in call order (Nexus's own FieldDayLog::log_fields_at assigns dupe/scoring position by
        // replay order, so this must be deterministic, not database-default row order).
        public List<ContestSessionRow> GetContestSessionRows(string contestSessionId)
        {
            var result = new List<ContestSessionRow>();
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT id, callsign, mode, qso_date, time_on FROM qso " +
                        "WHERE contest_session_id=@sid ORDER BY qso_date, time_on, id;";
                    cmd.Parameters.AddWithValue("@sid", contestSessionId ?? "");
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            long id = r.GetInt64(0);
                            string callsign = r.IsDBNull(1) ? "" : r.GetString(1);
                            string mode = r.IsDBNull(2) ? "" : r.GetString(2);
                            string qsoDate = r.IsDBNull(3) ? "" : r.GetString(3);
                            string timeOn = r.IsDBNull(4) ? "" : r.GetString(4);
                            long whenUnix = ParseAdifDateTimeToUnix(qsoDate, timeOn);
                            result.Add(new ContestSessionRow { Id = id, Callsign = callsign, Mode = mode, WhenUnix = whenUnix });
                        }
                    }
                }
            }
            return result;
        }

        private static long ParseAdifDateTimeToUnix(string qsoDate, string timeOn)
        {
            // qsoDate: "yyyyMMdd", timeOn: "HHmmss" or "HHmm" -- same formats ContestWorkflow
            // itself writes (DateTimeOffset.FromUnixTimeSeconds(...).ToString("yyyyMMdd"/"HHmmss")).
            if (string.IsNullOrEmpty(qsoDate)) return 0;
            string t = (timeOn ?? "").PadRight(6, '0');
            string full = qsoDate + t.Substring(0, Math.Min(6, t.Length)).PadRight(6, '0');
            if (DateTime.TryParseExact(full, "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt))
            {
                return ((DateTimeOffset)dt).ToUnixTimeSeconds();
            }
            return 0;
        }

        // Returns full-fidelity ADIF field dictionaries (every stored column, not just
        // the Edit Log tab's display subset) for export. ids null/empty exports every QSO.
        // sources null/empty applies no source filter; otherwise only rows whose "source"
        // column matches one of the given values are included.
        // The rows GetAdifFieldDicts would export (same ids / sources selection, same order), as
        // each row's value of one extra-field tag, or null where the row has none -- used while
        // Nexus keeps the log to pick Nexus's own exported records by APP_NEXUS_ID.
        public List<string> GetExtraFieldForExport(string tag, IEnumerable<int> ids, IEnumerable<string> sources = null)
        {
            var idList = ids?.Distinct().ToList();
            var sourceList = sources?.Distinct().ToList();
            lock (_lock)
            {
                var result = new List<string>();
                using (var cmd = _conn.CreateCommand())
                {
                    var clauses = new List<string>();
                    if (idList != null && idList.Count > 0)
                    {
                        clauses.Add($"q.id IN ({string.Join(",", idList.Select((_, i) => $"@id{i}"))})");
                        for (int i = 0; i < idList.Count; i++) cmd.Parameters.AddWithValue($"@id{i}", idList[i]);
                    }
                    if (sourceList != null && sourceList.Count > 0)
                    {
                        clauses.Add($"q.source IN ({string.Join(",", sourceList.Select((_, i) => $"@src{i}"))})");
                        for (int i = 0; i < sourceList.Count; i++) cmd.Parameters.AddWithValue($"@src{i}", sourceList[i]);
                    }
                    cmd.Parameters.AddWithValue("@tag", tag);
                    cmd.CommandText = "SELECT e.tag_value FROM qso q LEFT JOIN qso_extra_field e ON e.qso_id = q.id AND e.tag_name = @tag " +
                        (clauses.Count > 0 ? "WHERE " + string.Join(" AND ", clauses) : "") + " ORDER BY q.qso_date, q.time_on;";
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) result.Add(r.IsDBNull(0) ? null : r.GetString(0));
                }
                return result;
            }
        }

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

        // ── Helpers ───────────────────────────────────────────────────────────────

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
