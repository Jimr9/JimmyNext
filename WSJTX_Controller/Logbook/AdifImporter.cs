using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Text;
using System.Text.RegularExpressions;

namespace WSJTX_Controller
{
    public class ImportResult
    {
        public int    Processed       { get; set; }
        public int    NewQsos         { get; set; }
        // A QSO can be both newly confirmed and have other details corrected in the same
        // sync, so these are independent counts, not a partition of "Updated" -- a row that
        // changed in both ways is counted in both.
        public int    NewlyConfirmed  { get; set; }
        public int    Corrected       { get; set; }
        public int    Skipped         { get; set; }
        public string Errors          { get; set; } = "";

        public override string ToString() =>
            $"Processed {Processed}: {NewQsos} new, {NewlyConfirmed} newly confirmed, {Corrected} corrected, {Skipped} unchanged" +
            (string.IsNullOrEmpty(Errors) ? "" : $"; {Errors.Split('\n').Length} errors");
    }

    public static class AdifImporter
    {
        // Band frequency boundaries (MHz).  Used when BAND is absent but FREQ present.
        private static readonly (double lo, double hi, string band)[] FreqBands =
        {
            (0.1357, 0.1378, "2200m"),
            (0.472,  0.479,  "630m"),
            (1.8,    2.0,    "160m"),
            (3.5,    4.0,    "80m"),
            (5.06,   5.45,   "60m"),
            (7.0,    7.3,    "40m"),
            (10.1,   10.15,  "30m"),
            (14.0,   14.35,  "20m"),
            (18.068, 18.168, "17m"),
            (21.0,   21.45,  "15m"),
            (24.89,  24.99,  "12m"),
            (28.0,   29.7,   "10m"),
            (50.0,   54.0,   "6m"),
            (70.0,   70.5,   "4m"),
            (144.0,  148.0,  "2m"),
            (222.0,  225.0,  "1.25m"),
            (420.0,  450.0,  "70cm"),
            (902.0,  928.0,  "33cm"),
            (1240.0, 1300.0, "23cm"),
        };

        // source: "QRZ", "LOTW", or "MANUAL"
        // resolveUsState: optional offline callsign->state lookup (see Normalize) used only
        // when the imported record's own STATE field is blank -- e.g. QRZ's ADIF export
        // sometimes omits STATE for a contact even though QRZ's own site already credits
        // the state (found 2026-07-08, several confirmed Alaska/Hawaii contacts). Pass null
        // to disable (matches prior behavior exactly).
        public static ImportResult Import(
            LogbookDb db,
            IEnumerable<Dictionary<string, string>> records,
            string source,
            Action<int> progressCallback = null,
            Func<string, string> resolveUsState = null)
        {
            var result = new ImportResult();
            var errors = new StringBuilder();

            int batchSize = 0;
            SQLiteTransaction tx = db.BeginTransaction();
            try
            {
                foreach (var raw in records)
                {
                    try
                    {
                        var q = Normalize(raw, source, resolveUsState);
                        if (q == null) { result.Skipped++; result.Processed++; continue; }

                        var (isNew, newlyConfirmed, corrected) = db.Upsert(
                            q.callsign, q.band, q.mode, q.qsoDate, q.timeOn, q.timeOff,
                            q.freqHz, q.rstSent, q.rstRcvd, q.state, q.country,
                            q.dxcc, q.cqZone, q.grid, q.name, q.comment, q.txPwr,
                            q.operatorCall, q.stationCall, q.myGrid,
                            q.lotwQslSent, q.lotwQslRcvd, q.qrzQslSent, q.qrzQslRcvd,
                            source, q.sourceQsoId, q.dedupKey,
                            q.continent, q.ituZone, q.county, q.iota,
                            q.sig, q.sigInfo, q.mySig, q.mySigInfo,
                            q.darcDok, q.wpxPrefix, q.exchangeSent, q.exchangeRcvd);

                        if (isNew)
                        {
                            result.NewQsos++;
                        }
                        else
                        {
                            if (newlyConfirmed) result.NewlyConfirmed++;
                            if (corrected)       result.Corrected++;
                            if (!newlyConfirmed && !corrected) result.Skipped++;
                        }

                        result.Processed++;

                        batchSize++;
                        if (batchSize >= 500)
                        {
                            tx.Commit();
                            tx.Dispose();
                            tx = db.BeginTransaction();
                            batchSize = 0;
                        }

                        progressCallback?.Invoke(result.Processed);
                    }
                    catch (Exception ex)
                    {
                        result.Processed++;
                        result.Skipped++;
                        if (errors.Length < 2000)
                            errors.AppendLine(ex.Message);
                    }
                }

                tx.Commit();
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }
            finally
            {
                tx.Dispose();
            }

            result.Errors = errors.ToString().Trim();
            return result;
        }

        // ── Normalization ─────────────────────────────────────────────────────────

        private sealed class NormalizedQso
        {
            public string callsign, band, mode, qsoDate, timeOn, timeOff;
            public long   freqHz;
            public string rstSent, rstRcvd, state, country, grid, name, comment, txPwr;
            public int    dxcc, cqZone;
            public string operatorCall, stationCall, myGrid;
            public string lotwQslSent, lotwQslRcvd, qrzQslSent, qrzQslRcvd;
            public string sourceQsoId;
            public string dedupKey;
            // Fields used by the Rule Definitions (awards) engine.
            public string continent, county, iota, sig, sigInfo, mySig, mySigInfo, darcDok, wpxPrefix;
            public int    ituZone;
            public string exchangeSent, exchangeRcvd;
        }

        private static NormalizedQso Normalize(Dictionary<string, string> f, string source,
            Func<string, string> resolveUsState = null)
        {
            string call = GetField(f, "CALL");
            if (string.IsNullOrWhiteSpace(call)) return null;
            call = call.ToUpperInvariant().Trim();

            string band = NormalizeBand(GetField(f, "BAND"), GetField(f, "FREQ"));
            // Found live, 2026-08-26: LoTW's own ADIF export reports FT4 QSOs under the ADIF
            // umbrella MODE "MFSK" with SUBMODE "FT4" (per the ADIF spec, SUBMODE exists
            // specifically to narrow an umbrella MODE like this), while every other source here
            // (QRZ, Club Log, Jimmy's own live logging) reports MODE="FT4" directly. Since
            // dedup_key (BuildDedupKey below) includes mode as a literal string, "MFSK" vs
            // "FT4" for the exact same real QSO never matched -- confirmed live: a QSO both QRZ
            // and LoTW's website agree is genuinely LoTW-confirmed still showed as permanently
            // "pending" locally, because LoTW's download inserted it as a SEPARATE mode=MFSK
            // row instead of backfilling the existing mode=FT4 row's upload-confirmed flag.
            // Preferring SUBMODE when present (the ADIF-spec-correct, more specific value)
            // keeps mode consistent across every source instead of just working around MFSK
            // specifically.
            string mode = (GetField(f, "SUBMODE") ?? GetField(f, "MODE") ?? "").ToUpperInvariant().Trim();

            string qsoDate = NormalizeDate(GetField(f, "QSO_DATE") ?? GetField(f, "QSO_DATE_OFF") ?? "");
            string timeOn  = NormalizeTime(GetField(f, "TIME_ON")  ?? "");
            string timeOff = NormalizeTime(GetField(f, "TIME_OFF") ?? "");

            if (string.IsNullOrEmpty(qsoDate)) return null;

            string dedupKey = BuildDedupKey(call, band, mode, qsoDate, timeOn);

            long freqHz = 0;
            double freqMhz;
            if (double.TryParse(GetField(f, "FREQ"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out freqMhz))
                freqHz = (long)(freqMhz * 1_000_000);

            int dxcc = 0;
            int.TryParse(GetField(f, "DXCC") ?? "", out dxcc);

            int cqZone = 0;
            int.TryParse(GetField(f, "CQZ") ?? GetField(f, "CQ_ZONE") ?? "", out cqZone);

            string country = GetField(f, "COUNTRY") ?? "";
            string continent = (GetField(f, "CONT") ?? "").ToUpperInvariant();

            // T12 fix, 2026-08-23 (PARTIALLY CONFIRMED -- LoTW-only DXCC/awards, reported
            // 2026-08-21): core confirmation logic (QSL flags below, HrcCache) was already
            // service-neutral, but nothing backfilled a missing DXCC/country/continent when the
            // SOURCE ADIF simply omitted them -- some real LoTW/Club Log exports do, while QRZ's
            // own export more often already includes them, which plausibly explained "only QRZ
            // data behaves correctly" without any service actually being required. Live
            // classification and worked/confirmed DXCC sets are gated on dxcc>0 (LogbookDb.
            // LoadHrcCache), so a real confirmed QSO with dxcc left at 0 was invisible to
            // DXCC-needed/worked-DXCC award logic regardless of its QSL flags. Backfills ONLY
            // fields the source record itself left blank/zero, from the same canonical offline
            // Club Log prefix/entity data every live decode already classifies against
            // (RuleLibrary.ClubLog, populated once at startup, Controller.cs) -- never overrides
            // a real value the ADIF file actually supplied. RuleLibrary.ClubLog can be null in a
            // narrow startup/test window before it's assigned; a missing/undeleted resolution
            // simply leaves the field at its prior (possibly still zero/blank) value, same as
            // before this fix existed.
            if ((dxcc == 0 || string.IsNullOrEmpty(country) || string.IsNullOrEmpty(continent))
                && RuleLibrary.ClubLog != null)
            {
                var entity = RuleLibrary.ClubLog.FindByCallsign(call);
                if (entity != null && !entity.Deleted)
                {
                    if (dxcc == 0) dxcc = entity.Adif;
                    if (string.IsNullOrEmpty(country)) country = entity.Name;
                    if (string.IsNullOrEmpty(continent)) continent = entity.Continent;
                }
            }

            // QSL field mapping differs by source.
            // LoTW download: QSL_RCVD:Y means confirmed (LOTW_QSL_RCVD is a logging-software field, absent in LoTW's own export).
            // QRZ download:  APP_QRZLOG_STATUS:C means confirmed on QRZ logbook; QSL_RCVD is physical paper cards only.
            string lotwSent, lotwRcvd, qrzSent, qrzRcvd;
            if (source == "LOTW")
            {
                lotwSent = QslVal(GetField(f, "QSL_SENT"));
                lotwRcvd = QslVal(GetField(f, "QSL_RCVD"));
                qrzSent  = "";
                qrzRcvd  = "";
            }
            else if (source == "QRZ")
            {
                lotwSent = QslVal(GetField(f, "LOTW_QSL_SENT"));
                lotwRcvd = QslVal(GetField(f, "LOTW_QSL_RCVD"));
                string appStatus = GetField(f, "APP_QRZLOG_STATUS");
                qrzRcvd  = string.Equals(appStatus, "C", StringComparison.OrdinalIgnoreCase) ? "Y" : "";
                qrzSent  = QslVal(GetField(f, "QSL_SENT"));
            }
            else
            {
                lotwSent = QslVal(GetField(f, "LOTW_QSL_SENT"));
                string lotwRcvdDirect = QslVal(GetField(f, "LOTW_QSL_RCVD"));
                lotwRcvd = lotwRcvdDirect.Length > 0 ? lotwRcvdDirect : QslVal(GetField(f, "QSL_RCVD"));
                string appStatus = GetField(f, "APP_QRZLOG_STATUS");
                qrzRcvd  = string.Equals(appStatus, "C", StringComparison.OrdinalIgnoreCase) ? "Y" : "";
                qrzSent  = QslVal(GetField(f, "QSL_SENT"));
            }

            string state = (GetField(f, "STATE") ?? "").ToUpperInvariant().Trim();
            if (state.Length > 2) state = state.Substring(0, 2);

            string grid = (GetField(f, "GRIDSQUARE") ?? GetField(f, "GRID") ?? "").ToUpperInvariant();

            // Fallback for a blank STATE field only -- never overrides a real value from the
            // file. Offline only (resolveUsState is expected to be cache/database-backed, e.g.
            // FCC ULS or an already-cached QRZ lookup -- never a live network query, since a
            // bulk import/backfill can touch many callsigns at once). Grid-derived is a last
            // resort, same source used for live-decode display elsewhere in the app.
            if (string.IsNullOrEmpty(state))
            {
                string resolved = resolveUsState?.Invoke(call);
                if (string.IsNullOrEmpty(resolved) && !string.IsNullOrEmpty(grid))
                    resolved = WsjtxClient.GridToUsState(grid);
                if (!string.IsNullOrEmpty(resolved)) state = resolved;
            }

            int ituZone = 0;
            int.TryParse(GetField(f, "ITUZ") ?? GetField(f, "ITU_ZONE") ?? "", out ituZone);

            return new NormalizedQso
            {
                callsign     = call,
                band         = band,
                mode         = mode,
                qsoDate      = qsoDate,
                timeOn       = timeOn,
                timeOff      = timeOff,
                freqHz       = freqHz,
                rstSent      = GetField(f, "RST_SENT") ?? "",
                rstRcvd      = GetField(f, "RST_RCVD") ?? "",
                state        = state,
                country      = country,
                dxcc         = dxcc,
                cqZone       = cqZone,
                grid         = grid,
                name         = GetField(f, "NAME") ?? "",
                comment      = GetField(f, "COMMENT") ?? GetField(f, "NOTES") ?? "",
                txPwr        = GetField(f, "TX_PWR") ?? "",
                operatorCall = (GetField(f, "OPERATOR") ?? "").ToUpperInvariant(),
                stationCall  = (GetField(f, "STATION_CALLSIGN") ?? GetField(f, "MY_CALL") ?? "").ToUpperInvariant(),
                myGrid       = (GetField(f, "MY_GRIDSQUARE") ?? GetField(f, "MY_GRID") ?? "").ToUpperInvariant(),
                lotwQslSent  = lotwSent,
                lotwQslRcvd  = lotwRcvd,
                qrzQslSent   = qrzSent,
                qrzQslRcvd   = qrzRcvd,
                sourceQsoId  = GetField(f, "APP_QRZLOG_QSLDATE") ?? "",
                dedupKey     = dedupKey,
                continent    = continent,
                ituZone      = ituZone,
                county       = (GetField(f, "CNTY") ?? "").ToUpperInvariant(),
                iota         = (GetField(f, "IOTA") ?? "").ToUpperInvariant(),
                sig          = (GetField(f, "SIG") ?? "").ToUpperInvariant(),
                sigInfo      = (GetField(f, "SIG_INFO") ?? "").ToUpperInvariant(),
                mySig        = (GetField(f, "MY_SIG") ?? "").ToUpperInvariant(),
                mySigInfo    = (GetField(f, "MY_SIG_INFO") ?? "").ToUpperInvariant(),
                darcDok      = (GetField(f, "DARC_DOK") ?? "").ToUpperInvariant(),
                wpxPrefix    = (GetField(f, "PFX") ?? "").ToUpperInvariant(),
                exchangeSent = GetField(f, "STX_STRING") ?? "",
                exchangeRcvd = GetField(f, "SRX_STRING") ?? "",
            };
        }

        public static string BuildDedupKey(string call, string band, string mode, string qsoDate, string timeOn)
        {
            string t4 = timeOn != null && timeOn.Length >= 4 ? timeOn.Substring(0, 4) : timeOn ?? "";
            return $"{call.ToUpperInvariant()}|{(band ?? "").ToLowerInvariant()}|{(mode ?? "").ToUpperInvariant()}|{qsoDate}|{t4}";
        }

        // internal (not private): reused by AdifRecordBuilder callers that need the
        // same freq-to-band table for QRZ/Club Log upload records.
        internal static string NormalizeBand(string band, string freqStr)
        {
            if (!string.IsNullOrWhiteSpace(band))
                return band.ToLowerInvariant().Trim();

            if (!string.IsNullOrWhiteSpace(freqStr))
            {
                double mhz;
                if (double.TryParse(freqStr, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out mhz))
                {
                    foreach (var (lo, hi, b) in FreqBands)
                        if (mhz >= lo && mhz <= hi) return b;
                }
            }
            return "";
        }

        private static string NormalizeDate(string d)
        {
            if (string.IsNullOrWhiteSpace(d)) return "";
            d = d.Trim().Replace("-", "").Replace("/", "");
            return d.Length >= 8 ? d.Substring(0, 8) : "";
        }

        private static string NormalizeTime(string t)
        {
            if (string.IsNullOrWhiteSpace(t)) return "";
            t = t.Trim().Replace(":", "");
            return t.Length >= 4 ? t.Substring(0, 4) : t;
        }

        private static string QslVal(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return "";
            v = v.Trim().ToUpperInvariant();
            return (v == "Y" || v == "N" || v == "R" || v == "Q" || v == "I") ? v : "";
        }

        private static string GetField(Dictionary<string, string> f, string key)
        {
            string v;
            return f.TryGetValue(key, out v) ? (v ?? "").Trim() : null;
        }
    }
}
