using System;
using System.Collections.Generic;

namespace WSJTX_Controller
{
    // Reconciles a downloaded eQSL InBox ADIF (ExternalDataClient.DownloadEqsl) against the
    // log. An eQSL InBox is a set of confirmations reported by OTHER operators, so it only ever
    // confirms EXISTING contacts and never creates one: Nexus's eQSL merge, through the same
    // pairing guard as LoTW and QRZ (NexusReportPairing) -- a record with no confident match is
    // held or left alone, never guessed at.
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

        public static Result Reconcile(ILogbookService db, string adifText)
        {
            var result = new Result();
            var r = ((NexusLogbookService)db).MergeDownload(adifText, "EQSL");
            if (!string.IsNullOrEmpty(r.Errors)) throw new InvalidOperationException(r.Errors);
            result.Matched = r.NewlyConfirmed;
            result.Ambiguous = r.Held;
            result.Unmatched = r.Unmatched;
            result.AlreadyConfirmed = Math.Max(0, r.Processed - r.NewlyConfirmed - r.Unmatched);
            return result;
        }
    }
}
