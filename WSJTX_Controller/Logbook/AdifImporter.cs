using System;
using System.Collections.Generic;
using System.Linq;
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
        // While Nexus keeps the log: confirmations in a download that matched no logged contact
        // (Nexus's own list, one line each; also saved under NexusLog\diagnostics).
        public int    Unmatched       { get; set; }
        public List<string> UnmatchedDetails { get; } = new List<string>();
        // Rows held back because Nexus could not be sure to pair them with the right contact.
        public int    Held            { get; set; }
        public List<string> HeldDetails { get; } = new List<string>();

        // ", N not matched" / ", N held for review" for the status line, or "".
        public string UnmatchedText =>
            (Unmatched > 0 ? $", {Unmatched:N0} not matched to a logged contact" : "") +
            (Held > 0 ? $", {Held:N0} held for review" : "");

        public override string ToString() =>
            $"Processed {Processed}: {NewQsos} new, {NewlyConfirmed} newly confirmed, {Corrected} corrected, {Skipped} unchanged{UnmatchedText}" +
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
        // Overload for every existing caller/test that only has plain field Dictionaries (no
        // ADIF text to preserve true order from) -- unchanged behavior, just routed through the
        // AdifRawRecord overload below with an empty Ordered list per record, so no extras are
        // ever recorded for these (never a wrong or fabricated order).
        public static ImportResult Import(
            ILogbookService db,
            IEnumerable<Dictionary<string, string>> records,
            string source,
            Action<int> progressCallback = null,
            Func<string, string> resolveUsState = null)
        {
            return Import(db, records?.Select(r => (AdifRawRecord)r) ?? Enumerable.Empty<AdifRawRecord>(),
                source, progressCallback, resolveUsState);
        }

        // The AdifRawRecord form carries true file order/duplicates (AdifParser.ParseWithOrder);
        // unmodeled fields travel to Nexus with the record.
        public static ImportResult Import(
            ILogbookService db,
            IEnumerable<AdifRawRecord> records,
            string source,
            Action<int> progressCallback = null,
            Func<string, string> resolveUsState = null)
        {
            // Nexus keeps the log: the records go to Nexus's own import / merge as one file, and
            // Jimmy's own live-logged contact goes through the durable outbox.
            var nexus = (NexusLogbookService)db;
            {
                var list = records.ToList();
                // The T12 backfill, as Jimmy's own import applies it: DXCC / country / continent a
                // contact left blank, filled from Club Log -- for the live contact and for plain
                // imports that add contacts. (The LoTW / QRZ / eQSL downloads merge confirmations;
                // the contacts QRZ adds carry their own DXCC.)
                if (source != "LOTW" && source != "QRZ" && source != "EQSL")
                    foreach (var r in list) FillEntityGaps(r, resolveUsState);
                // Jimmy Next's own live contact -- only that: a WSJT-X file import, even of one
                // contact, is an ordinary import and keeps its own source (2026-10-04).
                if (source == QsoRecord.JimmyNextSource && list.Count == 1)
                {
                    var f = list[0].Fields;
                    string key = BuildDedupKey(f.TryGetValue("CALL", out var c) ? c : "", (f.TryGetValue("BAND", out var b) ? b : "").ToLowerInvariant(),
                        f.TryGetValue("MODE", out var m) ? m : "", f.TryGetValue("QSO_DATE", out var d) ? d : "", f.TryGetValue("TIME_ON", out var t) ? t : "");
                    NexusLogbookService.QueueLiveContact(f, key);
                    return new ImportResult { Processed = 1, NewQsos = 1 };
                }
                string text = NexusLogbookService.ToAdifText(list, source);
                return source == "LOTW" || source == "QRZ" ? nexus.MergeDownload(text, source) : nexus.ImportFile(text, source);
            }
        }

        // ── Normalization ─────────────────────────────────────────────────────────

        // The T12 backfill (see its comment in the import above), as ONE rule shared by Jimmy's own
        // import and the Nexus paths: DXCC / country / continent a record left blank or zero, from
        // Club Log's offline entity data (RuleLibrary.ClubLog); never overrides a value present.
        internal static void FillEntityGaps(string call, ref int dxcc, ref string country, ref string continent)
        {
            if ((dxcc == 0 || string.IsNullOrEmpty(country) || string.IsNullOrEmpty(continent))
                && RuleLibrary.ClubLog != null && !string.IsNullOrEmpty(call))
            {
                var entity = RuleLibrary.ClubLog.FindByCallsign(call);
                if (entity != null && !entity.Deleted)
                {
                    if (dxcc == 0) dxcc = entity.Adif;
                    if (string.IsNullOrEmpty(country)) country = entity.Name;
                    if (string.IsNullOrEmpty(continent)) continent = entity.Continent;
                }
            }
        }

        // Normalize's blank-STATE rule, shared with the Nexus paths: the offline callsign lookup
        // (FCC ULS / cached QRZ via resolveUsState), then the grid. Null when neither knows.
        // The mailing-address state (callsign lookup) checked against the grid heard on the air,
        // with Nexus's own grid table: blank rather than wrong when they disagree -- see
        // StationLocation.ResolveState. (Until 2026-09-29 this used the lookup outright and fell
        // back to WSJT-X's grid.dat, whose border squares are two-state values like "ID-WY".)
        internal static string ResolveMissingState(string call, string grid, Func<string, string> resolveUsState) =>
            StationLocation.ResolveState(resolveUsState?.Invoke(call), grid);

        // The same rules on an ADIF record handed to Nexus: fills DXCC / COUNTRY / CONT -- and,
        // 2026-09-28, STATE (live: KA1MXL logged on 80m with no state kept "WAS 80m Needed, RI")
        // -- the record left blank, in its fields and (when it keeps file order) its ordered list.
        internal static void FillEntityGaps(AdifRawRecord r, Func<string, string> resolveUsState = null)
        {
            var f = r.Fields;
            string call = f.TryGetValue("CALL", out var c) ? c : "";
            int.TryParse(f.TryGetValue("DXCC", out var d) ? d : "", out int dxcc);
            string country = f.TryGetValue("COUNTRY", out var co) ? co : "";
            string continent = f.TryGetValue("CONT", out var ct) ? ct : "";
            int dxcc0 = dxcc; string country0 = country, continent0 = continent;
            FillEntityGaps(call, ref dxcc, ref country, ref continent);
            void Set(string tag, string value)
            {
                f[tag] = value;
                if (r.Ordered != null)
                {
                    r.Ordered.RemoveAll(o => string.Equals(o.Tag, tag, StringComparison.OrdinalIgnoreCase));
                    r.Ordered.Add((tag, value));
                }
            }
            if (dxcc != dxcc0 && dxcc > 0) Set("DXCC", dxcc.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (country != country0 && !string.IsNullOrEmpty(country)) Set("COUNTRY", country);
            if (continent != continent0 && !string.IsNullOrEmpty(continent)) Set("CONT", continent.ToUpperInvariant());
            if (string.IsNullOrEmpty(f.TryGetValue("STATE", out var st) ? st : null))
            {
                string grid = f.TryGetValue("GRIDSQUARE", out var g) ? g : (f.TryGetValue("GRID", out var g2) ? g2 : "");
                string state = ResolveMissingState(call, grid, resolveUsState);
                if (state != null && state.Length <= 2) Set("STATE", state.ToUpperInvariant());
            }
        }

        public static string BuildDedupKey(string call, string band, string mode, string qsoDate, string timeOn)
        {
            string t4 = timeOn != null && timeOn.Length >= 4 ? timeOn.Substring(0, 4) : timeOn ?? "";
            return $"{call.ToUpperInvariant()}|{(band ?? "").ToLowerInvariant()}|{(mode ?? "").ToUpperInvariant()}|{qsoDate}|{t4}";
        }

        // Vendor-specific "APP_<program>_" field prefixes each service's own ADIF export
        // stamps on every record it writes -- the same APP_QRZLOG_STATUS/APP_QRZLOG_QSLDATE
        // fields Normalize() above already reads for QRZ are also the most reliable signal
        // that a file came from QRZ in the first place. Checked against every record (not
        // just the first) so one odd/incomplete leading record can't misclassify a whole file.
        private static readonly (string prefix, string source)[] VendorFieldMarkers =
        {
            ("APP_QRZLOG_",  "QRZ"),
            ("APP_LOTW_",    "LOTW"),
            ("APP_CLUBLOG_", "CLUBLOG"),
        };

        // Auto-detects which logging service produced an ADIF file, for the manual "Import
        // ADIF File" button -- the only Import() caller that doesn't already know its source
        // (the QRZ/LoTW/Club Log refresh buttons fetch directly from that service and tag it
        // themselves). Lets an operator who downloads their own confirmations by hand and
        // imports the file still get an accurate qso.source (QRZ/LOTW/CLUBLOG/WSJTX), matching
        // what the built-in refresh buttons would have tagged the same data with, instead of
        // everything collapsing into an undifferentiated "MANUAL".
        //
        // Primary signal: each service's own vendor field prefix, majority vote across every
        // record (ties broken by VendorFieldMarkers' own order). Falls back to the ADIF
        // header/comment text (e.g. WSJT-X's own export names itself there) when no record
        // carries a vendor field. Never invents a source it isn't reasonably sure of --
        // anything unrecognized stays "MANUAL", exactly today's behavior, so a hand-typed or
        // unfamiliar-program ADIF is never mislabeled.
        public static string DetectSource(string adifText, IEnumerable<Dictionary<string, string>> records)
        {
            var counts = new Dictionary<string, int>();
            foreach (var rec in records)
            {
                foreach (var key in rec.Keys)
                {
                    foreach (var (prefix, source) in VendorFieldMarkers)
                    {
                        if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            counts.TryGetValue(source, out int n);
                            counts[source] = n + 1;
                            break;
                        }
                    }
                }
            }

            if (counts.Count > 0)
            {
                string best = null;
                int bestCount = -1;
                foreach (var (_, source) in VendorFieldMarkers)
                {
                    if (counts.TryGetValue(source, out int n) && n > bestCount)
                    {
                        best = source;
                        bestCount = n;
                    }
                }
                if (best != null) return best;
            }

            string header = adifText ?? "";
            int eoh = header.IndexOf("<EOH", StringComparison.OrdinalIgnoreCase);
            header = eoh > 0 ? header.Substring(0, eoh) : (header.Length > 4000 ? header.Substring(0, 4000) : header);

            if (header.IndexOf("logbook of the world", StringComparison.OrdinalIgnoreCase) >= 0 ||
                header.IndexOf("lotw", StringComparison.OrdinalIgnoreCase) >= 0)
                return "LOTW";
            if (header.IndexOf("qrz", StringComparison.OrdinalIgnoreCase) >= 0)
                return "QRZ";
            if (header.IndexOf("club log", StringComparison.OrdinalIgnoreCase) >= 0 ||
                header.IndexOf("clublog", StringComparison.OrdinalIgnoreCase) >= 0)
                return "CLUBLOG";
            if (header.IndexOf("wsjt-x", StringComparison.OrdinalIgnoreCase) >= 0 ||
                header.IndexOf("wsjtx", StringComparison.OrdinalIgnoreCase) >= 0)
                return "WSJTX";

            return "MANUAL";
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
    }
}
