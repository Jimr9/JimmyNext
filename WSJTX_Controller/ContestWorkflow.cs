using System;
using System.Collections.Generic;
using System.Linq;

namespace WSJTX_Controller
{
    // Nexus contesting foundation, phase 4: reliable, at-least-once completed-contest-QSO
    // delivery from EngineHost into Jimmy's permanent logbook. Standalone, same discipline as
    // LiveQsoUploadOrchestrator/ExternalDataClient -- no WinForms/Controller reference; station
    // identity is injected via delegates so this class is unit-testable without a live engine.
    //
    // Design (see the accepted architecture): EngineHost's ContestSession/FieldDayLog is the
    // ACTIVE, in-progress state; the moment a contact is complete it is delivered here exactly
    // as any other completed QSO would be -- Jimmy performs the one authoritative logbook write.
    // Reconciliation (CONTEST_QSOS_SINCE from a persisted watermark) is ALWAYS what actually
    // drives delivery, on every poll tick while a session is active -- not conditional on having
    // received a distinct "push" -- so a lost notification, a lost ack, or an EngineHost/Jimmy
    // restart all self-heal on the very next poll with no special-case recovery code, and the
    // idempotent write (source='NEXUS_CONTEST', source_qso_id="<sessionInstanceId>:<seq>") makes
    // a redundant redelivery harmless.
    public class ContestWorkflow
    {
        private readonly ContestClient _contestClient;
        private readonly Func<string> _myCall;
        private readonly Func<string> _myGrid;
        private readonly Func<string> _operatorCall;
        private readonly Action<string> _debugLog;

        private string _activeSessionInstanceId;
        private string _activeEventId;
        private string _activeContestId;

        public ContestWorkflow(
            ContestClient contestClient,
            Func<string> myCall,
            Func<string> myGrid,
            Func<string> operatorCall,
            Action<string> debugLog = null)
        {
            _contestClient = contestClient;
            _myCall = myCall;
            _myGrid = myGrid;
            _operatorCall = operatorCall;
            _debugLog = debugLog ?? (_ => { });
        }

        public bool IsSessionActive => _activeSessionInstanceId != null;
        public string ActiveSessionInstanceId => _activeSessionInstanceId;

        // Called once CONTEST_ENTER succeeds -- restores (or starts fresh) the per-session
        // watermark so reconciliation knows where to resume from, including across a Jimmy
        // restart (the watermark itself is persisted in LogbookDb's meta table, keyed by
        // session-instance id, so it survives independently of anything in-memory here).
        public void OnSessionEntered(string sessionInstanceId, string eventId, string contestId)
        {
            _activeSessionInstanceId = sessionInstanceId;
            _activeEventId = eventId;
            _activeContestId = contestId;
        }

        public void OnSessionExited()
        {
            _activeSessionInstanceId = null;
            _activeEventId = null;
            _activeContestId = null;
        }

        private string WatermarkMetaKey(string sessionInstanceId) => "contestWatermark_" + sessionInstanceId;

        private ulong LoadWatermark(ILogbookService db, string sessionInstanceId)
        {
            string raw = db.GetMeta(WatermarkMetaKey(sessionInstanceId));
            return raw != null && ulong.TryParse(raw, out var v) ? v : 0UL;
        }

        private void SaveWatermark(ILogbookService db, string sessionInstanceId, ulong seq)
        {
            db.SetMeta(WatermarkMetaKey(sessionInstanceId), seq.ToString());
        }

        // Call on every poll tick while a session is active (recommended: alongside the existing
        // ~1s SNAPSHOT poll -- see this class's own header comment for why unconditional polling,
        // not change-detection, is the reliability mechanism). Returns the number of contacts
        // actually applied (0 is the normal, common case).
        public int PollAndReconcile()
        {
            if (_activeSessionInstanceId == null) return 0;

            using (ILogbookService db = LogbookFactory.Open())
            {
                ulong watermark = LoadWatermark(db, _activeSessionInstanceId);
                var completions = _contestClient.QsosSince(watermark, out string error);
                if (completions == null)
                {
                    _debugLog($"ContestWorkflow.PollAndReconcile: CONTEST_QSOS_SINCE failed: {error}");
                    return 0;
                }

                int applied = 0;
                foreach (var c in completions.OrderBy(c => c.Seq))
                {
                    // A stale/mismatched session id from before an unexpected EngineHost restart
                    // (a fresh session was entered with a different id) must never be applied
                    // under the wrong session's identity.
                    if (c.SessionInstanceId != _activeSessionInstanceId) continue;

                    try
                    {
                        ApplyCompletion(db, c);
                        watermark = c.Seq;
                        SaveWatermark(db, _activeSessionInstanceId, watermark);
                        _contestClient.Ack(c.Seq);
                        applied++;
                    }
                    catch (Exception ex)
                    {
                        // Root-cause visibility, never a silent drop -- and never advance the
                        // watermark/ack past a completion that failed to apply, so the next
                        // poll retries it (idempotent write makes a retry-after-partial-failure
                        // safe).
                        _debugLog($"ContestWorkflow.PollAndReconcile: failed to apply seq {c.Seq} ({c.Call}): {ex.Message}");
                        break;
                    }
                }
                return applied;
            }
        }

        private void ApplyCompletion(ILogbookService db, ContestCompletion c)
        {
            string call = (c.Call ?? "").Trim().ToUpperInvariant();
            string band = (c.Band ?? "").Trim();
            string mode = (c.Mode ?? "").Trim().ToUpperInvariant();
            string myCall = (_myCall() ?? "").Trim().ToUpperInvariant();
            string myGrid = (_myGrid() ?? "").Trim().ToUpperInvariant();
            string operatorCall = string.IsNullOrWhiteSpace(_operatorCall()) ? myCall : _operatorCall().Trim().ToUpperInvariant();

            string sourceQsoId = $"{c.SessionInstanceId}:{c.Seq}";
            // Field Day's own real-world exchange convention (e.g. "2A MO") -- space-joined
            // field VALUES in the order EngineHost sent them (the ruleset's own field order),
            // not a hardcoded CLASS/SECTION assumption -- reusable for any future contest's
            // exchange shape without a protocol change.
            string exchangeSent = string.Join(" ", (c.SentFields ?? new List<List<string>>()).Select(f => f.Count > 1 ? f[1] : ""));
            string exchangeRcvd = string.Join(" ", (c.RcvdFields ?? new List<List<string>>()).Select(f => f.Count > 1 ? f[1] : ""));

            // The contact, its contest association and its structured received exchange in ONE
            // durable write to Nexus -- it throws unless saved, so the caller never acknowledges
            // an unsaved completion.
            var q = new NexusQso
            {
                Call = call, Band = band, Mode = mode, WhenUnix = c.WhenUnix, TimeKnown = true,
                Operator = operatorCall, StationCallsign = myCall, MyGrid = myGrid,
                Extra = new List<List<string>>
                {
                    new List<string> { "STX_STRING", exchangeSent }, new List<string> { "SRX_STRING", exchangeRcvd },
                    new List<string> { NexusMigration.SourceTag, "NEXUS_CONTEST" }, new List<string> { NexusMigration.SourceQsoIdTag, sourceQsoId },
                },
            };
            var pairs = (c.RcvdFields ?? new List<List<string>>()).Where(f => f.Count > 1).Select(f => (Tag: f[0], Value: f[1])).ToList();
            ((NexusLogbookService)db).LogContestCompletion(NexusLogbookService.RequestIdFor("NEXUS_CONTEST", sourceQsoId), q,
                _activeContestId ?? _activeEventId ?? "", c.SessionInstanceId, pairs);
        }

        // Nexus contesting foundation, phase 5: Jimmy-initiated, batched rebuild of Nexus's
        // contest score/dupe-state/export from Jimmy's own authoritative records -- called
        // before official score presentation and always before export, per the accepted design.
        // Batches in chunks of 200 (matching the message-size discipline noted in
        // ContestClient.RebuildAppend's own comment). Returns the commit result, or null with
        // `error` set on any failure -- callers must treat a failed rebuild as "nothing changed,
        // try again," never assume partial progress (EngineHost's own commit is all-or-nothing;
        // see contest_bridge.rs's rebuild_commit for the atomicity argument).
        public ContestRebuildCommitResult RebuildScoreAndExport(out string error)
        {
            error = null;
            if (_activeSessionInstanceId == null)
            {
                error = "no contest session is active";
                return null;
            }

            var begin = _contestClient.RebuildBegin(out error);
            if (begin == null) return null;

            using (ILogbookService db = LogbookFactory.Open())
            {
                var rows = db.GetContestSessionRows(_activeSessionInstanceId);

                const int batchSize = 200;
                for (int i = 0; i < rows.Count; i += batchSize)
                {
                    var batch = new List<RebuildAppendContact>();
                    foreach (var row in rows.Skip(i).Take(batchSize))
                    {
                        var fields = db.GetExtraFields(row.Id).Select(f => new List<string> { f.Tag, f.Value }).ToList();
                        batch.Add(new RebuildAppendContact
                        {
                            Call = row.Callsign,
                            Fields = fields,
                            Mode = row.Mode,
                            Submode = "",
                            WhenUnix = (ulong)Math.Max(0, row.WhenUnix),
                        });
                    }
                    if (!_contestClient.RebuildAppend(begin.RebuildToken, batch, out error))
                        return null;
                }
            }

            return _contestClient.RebuildCommit(begin.RebuildToken, out error);
        }
    }
}
