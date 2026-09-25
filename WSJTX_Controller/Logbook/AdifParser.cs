using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WSJTX_Controller
{
    // Streaming ADIF parser.  Yields one record dictionary per QSO.
    // Field names are returned upper-cased; values are trimmed.
    public static class AdifParser
    {
        public static IEnumerable<Dictionary<string, string>> Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            using (var sr = new StringReader(text))
                foreach (var rec in Parse(sr))
                    yield return rec;
        }

        public static IEnumerable<Dictionary<string, string>> Parse(TextReader reader)
        {
            var record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            int ch;
            while ((ch = reader.Read()) != -1)
            {
                if ((char)ch != '<') continue;

                // Read tag up to the closing '>'
                var tagBuf = new StringBuilder(32);
                while ((ch = reader.Read()) != -1 && (char)ch != '>')
                    tagBuf.Append((char)ch);
                string tag = tagBuf.ToString().Trim();
                if (tag.Length == 0) continue;

                // End-of-header marker: discard whatever was accumulated so far (header
                // fields), then start the first QSO record fresh. Some sources (e.g. the
                // QRZ Logbook API's FETCH response) omit <EOH> entirely -- in that case
                // there are no header fields to discard, and the first QSO's fields
                // simply accumulate from the start.
                if (string.Equals(tag, "EOH", StringComparison.OrdinalIgnoreCase))
                {
                    record.Clear();
                    continue;
                }

                // End-of-record marker
                if (string.Equals(tag, "EOR", StringComparison.OrdinalIgnoreCase))
                {
                    if (record.Count > 0)
                    {
                        yield return record;
                        record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    }
                    continue;
                }

                // Field tag: FIELDNAME:LENGTH or FIELDNAME:LENGTH:TYPE
                var parts = tag.Split(':');
                if (parts.Length < 2) continue;

                string fieldName = parts[0].Trim().ToUpperInvariant();
                int length;
                if (!int.TryParse(parts[1].Trim(), out length) || length < 0) continue;

                // Read exactly 'length' characters
                string value = "";
                if (length > 0)
                {
                    var buf = new char[length];
                    int total = 0;
                    while (total < length)
                    {
                        int n = reader.Read(buf, total, length - total);
                        if (n == 0) break;
                        total += n;
                    }
                    value = new string(buf, 0, total).Trim();
                }

                if (fieldName.Length > 0 && value.Length > 0)
                    record[fieldName] = value;
            }

            // File ended without final <EOR>
            if (record.Count > 0)
                yield return record;
        }

        // Nexus contesting foundation, phase 2: same records as Parse(), plus every field
        // OCCURRENCE in original file order (duplicates included, empty values included) --
        // feeds the unknown-ADIF-field catch-all (AdifExtraFields.cs / LogbookDb's
        // qso_extra_field table), which needs the true file sequence, not Parse()'s
        // last-value-wins Dictionary. A separate, self-contained loop rather than a shared
        // refactor of Parse() above -- Parse() is the production entry point for every existing
        // QRZ/LoTW/Club Log/HRDLog import path and stays byte-for-byte unchanged.
        public static IEnumerable<AdifRawRecord> ParseWithOrder(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            using (var sr = new StringReader(text))
                foreach (var rec in ParseWithOrder(sr))
                    yield return rec;
        }

        public static IEnumerable<AdifRawRecord> ParseWithOrder(TextReader reader)
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<(string Tag, string Value)>();

            int ch;
            while ((ch = reader.Read()) != -1)
            {
                if ((char)ch != '<') continue;

                var tagBuf = new StringBuilder(32);
                while ((ch = reader.Read()) != -1 && (char)ch != '>')
                    tagBuf.Append((char)ch);
                string tag = tagBuf.ToString().Trim();
                if (tag.Length == 0) continue;

                if (string.Equals(tag, "EOH", StringComparison.OrdinalIgnoreCase))
                {
                    fields.Clear();
                    ordered.Clear();
                    continue;
                }

                if (string.Equals(tag, "EOR", StringComparison.OrdinalIgnoreCase))
                {
                    if (fields.Count > 0 || ordered.Count > 0)
                        yield return new AdifRawRecord(fields, ordered);
                    fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    ordered = new List<(string Tag, string Value)>();
                    continue;
                }

                var parts = tag.Split(':');
                if (parts.Length < 2) continue;

                string fieldName = parts[0].Trim().ToUpperInvariant();
                int length;
                if (!int.TryParse(parts[1].Trim(), out length) || length < 0) continue;

                string value = "";
                if (length > 0)
                {
                    var buf = new char[length];
                    int total = 0;
                    while (total < length)
                    {
                        int n = reader.Read(buf, total, length - total);
                        if (n == 0) break;
                        total += n;
                    }
                    value = new string(buf, 0, total).Trim();
                }

                if (fieldName.Length == 0) continue;

                // Ordered keeps every real occurrence (including duplicates and empty values) --
                // the lossless record ExtractUnmodeled diffs against. fields mirrors Parse()'s
                // own value.Length > 0 gate so Fields matches Parse()'s Dictionary exactly for
                // any caller that only wants that half.
                ordered.Add((fieldName, value));
                if (value.Length > 0) fields[fieldName] = value;
            }

            if (fields.Count > 0 || ordered.Count > 0)
                yield return new AdifRawRecord(fields, ordered);
        }
    }

    // Nexus contesting foundation, phase 2. Fields mirrors AdifParser.Parse()'s per-record
    // Dictionary (last-value-wins, empty values dropped) exactly; Ordered is the true file
    // sequence (duplicates and empty values included) used only for the unknown-field catch-all.
    public sealed class AdifRawRecord
    {
        public Dictionary<string, string> Fields { get; }
        public List<(string Tag, string Value)> Ordered { get; }

        public AdifRawRecord(Dictionary<string, string> fields, List<(string Tag, string Value)> ordered)
        {
            Fields = fields;
            Ordered = ordered;
        }

        // Lets a caller with only a plain field Dictionary (no ADIF text to preserve true file
        // order from -- e.g. a hand-built field set) still call AdifImporter.Import without
        // reformatting. Ordered is left empty in that case, so ExtractUnmodeled naturally
        // records nothing rather than fabricating an order that was never real.
        public static implicit operator AdifRawRecord(Dictionary<string, string> fields) =>
            new AdifRawRecord(fields ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new List<(string Tag, string Value)>());
    }
}
