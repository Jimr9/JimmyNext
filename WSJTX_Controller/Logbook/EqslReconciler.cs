using System.Collections.Generic;

namespace WSJTX_Controller
{
    // Reconciles a downloaded eQSL InBox ADIF (ExternalDataClient.DownloadEqsl) against
    // Jimmy Test's own local logbook. Deliberately NOT AdifImporter.Import: that path (used
    // for QRZ/LoTW/Club Log downloads) treats the source as a full logbook and can create a
    // new local QSO row for anything it doesn't already have -- appropriate for "my own log,
    // downloaded from a service I uploaded to," wrong for an eQSL InBox, which is a set of
    // confirmations reported by OTHER operators. This reconciler only ever matches against
    // EXISTING rows (LogbookDb.TryMarkEqslConfirmed) and never creates one -- an eQSL record
    // with no confident local match is simply left alone, never guessed at or invented.
    public static class EqslReconciler
    {
        public class Result
        {
            public int Matched;
            public int AlreadyConfirmed;
            public int Ambiguous;
            public int Unmatched;
            // Records without EQSL_QSL_RCVD=Y (not a confirmation record) or missing
            // CALL/BAND/QSO_DATE -- not an error, just nothing to reconcile.
            public int Skipped;

            public override string ToString() =>
                $"{Matched} newly confirmed, {AlreadyConfirmed} already confirmed, " +
                $"{Ambiguous} ambiguous (skipped), {Unmatched} not found locally, {Skipped} not a confirmation record";
        }

        public static Result Reconcile(LogbookDb db, string adifText)
        {
            var result = new Result();
            foreach (Dictionary<string, string> rec in AdifParser.Parse(adifText))
            {
                if (!IsConfirmed(rec)) { result.Skipped++; continue; }

                string call = rec.TryGetValue("CALL", out var c) ? c : null;
                string band = rec.TryGetValue("BAND", out var b) ? b : null;
                string date = rec.TryGetValue("QSO_DATE", out var d) ? d : null;
                string mode = rec.TryGetValue("MODE", out var m) ? m : null;

                if (string.IsNullOrWhiteSpace(call) || string.IsNullOrWhiteSpace(band) || string.IsNullOrWhiteSpace(date))
                {
                    result.Skipped++;
                    continue;
                }

                switch (db.TryMarkEqslConfirmed(call, band, date, mode))
                {
                    case LogbookDb.EqslReconcileOutcome.Matched:          result.Matched++; break;
                    case LogbookDb.EqslReconcileOutcome.AlreadyConfirmed: result.AlreadyConfirmed++; break;
                    case LogbookDb.EqslReconcileOutcome.Ambiguous:        result.Ambiguous++; break;
                    case LogbookDb.EqslReconcileOutcome.Unmatched:        result.Unmatched++; break;
                }
            }
            return result;
        }

        private static bool IsConfirmed(Dictionary<string, string> rec) =>
            rec.TryGetValue("EQSL_QSL_RCVD", out var v) && string.Equals(v?.Trim(), "Y", System.StringComparison.OrdinalIgnoreCase);
    }
}
