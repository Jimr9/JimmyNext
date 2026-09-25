using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WSJTX_Controller
{
    // Nexus contesting foundation, phase 3: wire DTOs mirroring EngineHost's contest_bridge.rs
    // exactly (camelCase, matching JsonNamingPolicy.CamelCase below).

    public class ContestEventListEntry
    {
        public string EventId { get; set; }
        public string ContestId { get; set; }
        public int RulesYear { get; set; }
    }

    public class ContestDomainValue
    {
        public string Code { get; set; }
        public string Name { get; set; }
    }

    public class ContestDomain
    {
        public string Id { get; set; }
        public List<ContestDomainValue> Values { get; set; } = new List<ContestDomainValue>();
    }

    // Mirrors contest_bridge::FieldKindWire's #[serde(tag = "type")] shape -- Type says which of
    // the nine (Rst/Serial/Enum/Pattern/Number/Grid/Text/Call/OneOf) this is; only the fields
    // that kind actually carries are populated, the rest stay at their defaults.
    public class ContestFieldKind
    {
        public string Type { get; set; }
        public int? Digits { get; set; }
        public ContestDomain Domain { get; set; }
        public string Re { get; set; }
        public int? Min { get; set; }
        public int? Max { get; set; }
        public int? Chars { get; set; }
        public int? MaxLen { get; set; }
        public List<ContestFieldKind> Arms { get; set; }
    }

    public class ContestField
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public bool Required { get; set; }
        public ContestFieldKind Kind { get; set; }
    }

    public class ContestModePoints
    {
        public int Ph { get; set; }
        public int Cw { get; set; }
        public int Dig { get; set; }
    }

    public class ContestRuleset
    {
        public string EventId { get; set; }
        public string ContestId { get; set; }
        public int RulesYear { get; set; }
        public string ExchangeName { get; set; }
        public List<ContestField> Fields { get; set; } = new List<ContestField>();
        public List<string> BannedModes { get; set; } = new List<string>();
        public ContestModePoints PointsByModeClass { get; set; }
        public string Enforcement { get; set; }
    }

    public class ContestEnterResult
    {
        public string SessionInstanceId { get; set; }
    }

    // A completed contact, as delivered by CONTEST_QSOS_SINCE -- the stable identity
    // (sessionInstanceId, seq) is exactly what the Logbook Service's idempotent write keys on
    // (source='NEXUS_CONTEST', source_qso_id="<sessionInstanceId>:<seq>").
    public class ContestCompletion
    {
        public string SessionInstanceId { get; set; }
        public ulong Seq { get; set; }
        public string Call { get; set; }
        public string Band { get; set; }
        public string Mode { get; set; }
        public string Submode { get; set; }
        public ulong WhenUnix { get; set; }
        public List<List<string>> SentFields { get; set; } = new List<List<string>>();
        public List<List<string>> RcvdFields { get; set; } = new List<List<string>>();
    }

    public class ContestRebuildBeginResult
    {
        public string RebuildToken { get; set; }
        public ulong LiveHighWaterSeq { get; set; }
    }

    public class ContestRebuildCommitResult
    {
        public int QsoCount { get; set; }
        public uint Points { get; set; }
    }

    // Standalone client for the contest bridge, same discipline as ExternalDataClient.cs -- no
    // WinForms/Controller reference, one short-lived TCP connection per command, never throws.
    public class ContestClient
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        // Every contest command is a fast, in-memory operation on EngineHost's side (no network
        // I/O -- see contest_bridge.rs's own comments) -- same short timeout as SNAPSHOT, not
        // the eQSL/HamQTH-style SlowTimeoutMs.
        private const int TimeoutMs = 3000;

        // Test-only redirect, same shape/purpose as WsjtxClient.TestDirectControlPortOverride --
        // null in production, so nothing here ever changes production behavior. Lets tests point
        // this client at a StubEngineHost instance instead of the real control port.
        internal static int? TestControlPortOverride;

        public List<ContestEventListEntry> ListEvents(out string error)
        {
            string resultJson = ParseOkOrError(SendCommand("CONTEST_LIST_EVENTS", TimeoutMs), out error);
            if (resultJson == null) return null;
            try { return JsonSerializer.Deserialize<List<ContestEventListEntry>>(resultJson, JsonOptions); }
            catch (Exception ex) { error = $"Could not parse CONTEST_LIST_EVENTS response: {ex.Message}"; return null; }
        }

        public ContestRuleset GetRuleset(string eventId, out string error)
        {
            string resultJson = ParseOkOrError(SendCommand("CONTEST_GET_RULESET " + eventId, TimeoutMs), out error);
            if (resultJson == null) return null;
            try { return JsonSerializer.Deserialize<ContestRuleset>(resultJson, JsonOptions); }
            catch (Exception ex) { error = $"Could not parse CONTEST_GET_RULESET response: {ex.Message}"; return null; }
        }

        public ContestEnterResult Enter(
            string eventId, string runMode, string stationCallsign, string grid, string operatorCallsign,
            string @class, string section, string categoryOperator, string categoryPower,
            string categoryAssisted, string categoryStation, out string error)
        {
            var args = new
            {
                eventId,
                runMode,
                stationCallsign,
                grid,
                operatorCallsign,
                @class,
                section,
                categoryOperator,
                categoryPower,
                categoryAssisted,
                categoryStation,
            };
            string json = JsonSerializer.Serialize(args, JsonOptions);
            string resultJson = ParseOkOrError(SendCommand("CONTEST_ENTER " + json, TimeoutMs), out error);
            if (resultJson == null) return null;
            try { return JsonSerializer.Deserialize<ContestEnterResult>(resultJson, JsonOptions); }
            catch (Exception ex) { error = $"Could not parse CONTEST_ENTER response: {ex.Message}"; return null; }
        }

        public bool Exit(out string error)
        {
            ParseOkOrError(SendCommand("CONTEST_EXIT", TimeoutMs), out error);
            return error == null;
        }

        // Nexus contesting foundation, phase 8: the general manual contest-QSO workflow's entry
        // point into Nexus's own validation/dupe-checking (Engine::contest_log_manual). Returns
        // true if logged, false if Nexus's own DupeRule refused it as a duplicate -- both are
        // legitimate outcomes, not failures; error is set only for a real problem (no active
        // session, bad args, no response).
        public bool LogManual(string call, List<List<string>> fields, string mode, string submode, out string error)
        {
            var args = new { call, fields, mode, submode = submode ?? "" };
            string json = JsonSerializer.Serialize(args, JsonOptions);
            string resultJson = ParseOkOrError(SendCommand("CONTEST_LOG_MANUAL " + json, TimeoutMs), out error);
            if (resultJson == null) return false;
            try
            {
                using (var doc = JsonDocument.Parse(resultJson))
                    return doc.RootElement.GetProperty("logged").GetBoolean();
            }
            catch (Exception ex)
            {
                error = $"Could not parse CONTEST_LOG_MANUAL response: {ex.Message}";
                return false;
            }
        }

        public List<ContestCompletion> QsosSince(ulong afterSeq, out string error)
        {
            string resultJson = ParseOkOrError(SendCommand("CONTEST_QSOS_SINCE " + afterSeq, TimeoutMs), out error);
            if (resultJson == null) return null;
            try { return JsonSerializer.Deserialize<List<ContestCompletion>>(resultJson, JsonOptions); }
            catch (Exception ex) { error = $"Could not parse CONTEST_QSOS_SINCE response: {ex.Message}"; return null; }
        }

        // Fire-and-forget-ish: a lost ack only costs one harmless redundant redelivery next
        // reconciliation (see contest_bridge.rs's own comment) -- callers don't need to retry
        // this on failure, just proceed.
        public void Ack(ulong seq)
        {
            SendCommand("CONTEST_QSO_ACK " + seq, TimeoutMs);
        }

        public ContestRebuildBeginResult RebuildBegin(out string error)
        {
            string resultJson = ParseOkOrError(SendCommand("CONTEST_REBUILD_BEGIN", TimeoutMs), out error);
            if (resultJson == null) return null;
            try { return JsonSerializer.Deserialize<ContestRebuildBeginResult>(resultJson, JsonOptions); }
            catch (Exception ex) { error = $"Could not parse CONTEST_REBUILD_BEGIN response: {ex.Message}"; return null; }
        }

        // contacts: each a (call, fields, mode, submode, whenUnix) tuple -- see
        // RebuildAppendContact. Batched by the caller (recommended <=200 per call, matching the
        // same message-size discipline Nexus's own fd_merge_to_general bypass was built to
        // avoid) -- this method sends exactly one CONTEST_REBUILD_APPEND per call.
        public bool RebuildAppend(string rebuildToken, List<RebuildAppendContact> contacts, out string error)
        {
            var args = new { rebuildToken, contacts };
            string json = JsonSerializer.Serialize(args, JsonOptions);
            ParseOkOrError(SendCommand("CONTEST_REBUILD_APPEND " + json, TimeoutMs), out error);
            return error == null;
        }

        public ContestRebuildCommitResult RebuildCommit(string rebuildToken, out string error)
        {
            string resultJson = ParseOkOrError(SendCommand("CONTEST_REBUILD_COMMIT " + rebuildToken, TimeoutMs), out error);
            if (resultJson == null) return null;
            try { return JsonSerializer.Deserialize<ContestRebuildCommitResult>(resultJson, JsonOptions); }
            catch (Exception ex) { error = $"Could not parse CONTEST_REBUILD_COMMIT response: {ex.Message}"; return null; }
        }

        // format: "cabrillo" | "adif". Requires a successful RebuildCommit first (EngineHost
        // refuses otherwise) -- callers must rebuild before ever calling this, per the accepted
        // design ("force a successful rebuild before... every export").
        public string Export(string format, string operatorName, string contestEmail, out string error)
        {
            var args = new { format, operatorName, contestEmail };
            string json = JsonSerializer.Serialize(args, JsonOptions);
            string resultJson = ParseOkOrError(SendCommand("CONTEST_EXPORT " + json, TimeoutMs), out error);
            if (resultJson == null) return null;
            try { return JsonSerializer.Deserialize<string>(resultJson, JsonOptions); }
            catch (Exception ex) { error = $"Could not parse CONTEST_EXPORT response: {ex.Message}"; return null; }
        }

        // "OK <payload>" -> payload (trimmed leading space); "ERR <message>" -> error set,
        // returns null -- same convention as ExternalDataClient.ParseOkOrError.
        private static string ParseOkOrError(string resp, out string error)
        {
            error = null;
            if (resp == null) { error = "No response from engine host."; return null; }
            if (resp.StartsWith("OK", StringComparison.Ordinal))
                return resp.Length > 2 ? resp.Substring(3) : "";
            if (resp.StartsWith("ERR", StringComparison.Ordinal))
            {
                error = resp.Length > 4 ? resp.Substring(4) : "Unknown error.";
                return null;
            }
            error = "Unexpected response from engine host.";
            return null;
        }

        // Same one-connection-per-command, never-throw shape as ExternalDataClient.SendCommand.
        private static string SendCommand(string command, int timeoutMs)
        {
            using (var client = new TcpClient())
            {
                try
                {
                    var connectTask = client.ConnectAsync(System.Net.IPAddress.Loopback, TestControlPortOverride ?? NativeEngineClient.ControlPort);
                    if (!connectTask.Wait(Math.Min(timeoutMs, 3000)) || !client.Connected) return null;

                    using (var stream = client.GetStream())
                    {
                        stream.WriteTimeout = 3000;
                        stream.ReadTimeout = timeoutMs;
                        byte[] cmd = Encoding.UTF8.GetBytes(command + "\n");
                        stream.Write(cmd, 0, cmd.Length);
                        client.Client.Shutdown(SocketShutdown.Send);

                        using (var ms = new MemoryStream())
                        {
                            byte[] buf = new byte[8192];
                            int n;
                            try
                            {
                                while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                                    ms.Write(buf, 0, n);
                            }
                            catch (IOException)
                            {
                                // Read timeout or connection reset -- best-effort, same
                                // tolerance as ExternalDataClient/DirectSendCommand.
                            }
                            return Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\r', '\n');
                        }
                    }
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }
    }

    // One contact in a CONTEST_REBUILD_APPEND batch -- fields as generic (key, value) pairs,
    // never a specific contest's hardcoded field names, matching EngineHost's RebuildContact.
    public class RebuildAppendContact
    {
        public string Call { get; set; }
        public List<List<string>> Fields { get; set; } = new List<List<string>>();
        public string Mode { get; set; }
        public string Submode { get; set; } = "";
        public ulong WhenUnix { get; set; }
    }
}
