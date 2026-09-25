using System;
using System.Collections.Generic;
using System.Text;

namespace WSJTX_Controller
{
    // Nexus contesting foundation, phase 2: lossless, ordered, duplicate-safe representation of
    // ADIF fields Jimmy's own modeled qso columns don't capture. Pure/connection-free by design
    // (no SQLite dependency here) so ordering/duplicate/round-trip behavior is directly
    // unit-testable without a database. LogbookDb.SaveExtraFields/GetExtraFields own the actual
    // qso_extra_field storage (schema v10) and call into this class for the set-difference and
    // re-emission logic.
    public static class AdifExtraFields
    {
        // Every ADIF tag name AdifImporter.Normalize actually reads, from any of Jimmy's known
        // import sources. Kept here, not duplicated in AdifImporter, so this is the single place
        // that answers "does Jimmy model this tag". A stale entry here (Normalize starts/stops
        // reading a tag and this set isn't updated) only ever means a modeled tag is redundantly
        // ALSO stored as an extra, or an unmodeled tag is missed once -- fails toward
        // preservation, never toward silent loss, either way.
        public static readonly HashSet<string> ModeledTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CALL", "BAND", "FREQ", "SUBMODE", "MODE", "QSO_DATE", "QSO_DATE_OFF", "TIME_ON", "TIME_OFF",
            "DXCC", "CQZ", "CQ_ZONE", "COUNTRY", "CONT", "QSL_SENT", "QSL_RCVD", "LOTW_QSL_SENT",
            "LOTW_QSL_RCVD", "APP_QRZLOG_STATUS", "STATE", "GRIDSQUARE", "GRID", "ITUZ", "ITU_ZONE",
            "RST_SENT", "RST_RCVD", "NAME", "COMMENT", "NOTES", "TX_PWR", "OPERATOR", "STATION_CALLSIGN",
            "MY_CALL", "MY_GRIDSQUARE", "MY_GRID", "APP_QRZLOG_QSLDATE", "CNTY", "IOTA", "SIG", "SIG_INFO",
            "MY_SIG", "MY_SIG_INFO", "DARC_DOK", "PFX", "STX_STRING", "SRX_STRING",
        };

        // Every occurrence NOT in ModeledTags, in original file order, duplicates and empty
        // values included. Deliberately dumb about ADIF semantics -- a plain set-difference over
        // tag names -- so it can never itself misinterpret a field's meaning.
        public static List<(string Tag, string Value)> ExtractUnmodeled(IEnumerable<(string Tag, string Value)> ordered)
        {
            var result = new List<(string Tag, string Value)>();
            if (ordered == null) return result;
            foreach (var (tag, value) in ordered)
            {
                if (string.IsNullOrEmpty(tag)) continue;
                if (ModeledTags.Contains(tag)) continue;
                result.Add((tag.ToUpperInvariant(), value ?? ""));
            }
            return result;
        }

        // Re-emits as ADIF field text ("<TAG:LEN>VALUE ", space-separated, no trailing <eor>) in
        // original order. Length is the value's UTF-16 char count, matching every other
        // length-prefixed field Jimmy's own ADIF code already emits (AdifRecordBuilder.cs). Not
        // yet called by any export path -- Jimmy's Cabrillo/ADIF exporters build purely from
        // modeled columns today; this is the round-trip primitive a later phase wires in.
        public static string ToAdifFragment(IEnumerable<(string Tag, string Value)> extras)
        {
            var sb = new StringBuilder();
            if (extras == null) return "";
            foreach (var (tag, value) in extras)
            {
                string v = value ?? "";
                sb.Append('<').Append(tag).Append(':').Append(v.Length).Append('>').Append(v).Append(' ');
            }
            return sb.ToString();
        }
    }
}
