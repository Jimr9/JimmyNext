using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WSJTX_Controller
{
    // Logbook migration (c:\chat gpt\nexus log review.txt), Phase 2: Jimmy's side of EngineHost's
    // LOG_* commands (EngineHost/src/logbook_host.rs). Nothing in the running app uses this yet --
    // the migration/comparison tools do, against a logbook-only EngineHost in a TEMP folder.
    //
    // One connection per command, like ContestClient -- with one deliberate difference: a reply
    // that did not arrive whole is reported as Unknown, never as a success or an empty answer. For
    // a write, "unknown" means the request must stay in Jimmy's outbox and be sent again (the
    // re-send is recognised by its request id -- see logbook_host.rs).

    // Nexus's own wire shape for a contact (tempo_app::dto::LoggedQso), camelCase.
    public class NexusQso
    {
        public string Id { get; set; }
        public string Call { get; set; }
        public string Grid { get; set; }
        public string Country { get; set; }
        public string Entity { get; set; }
        public string State { get; set; }
        public string Band { get; set; }
        public double FreqMhz { get; set; }
        public double? FreqRxMhz { get; set; }
        public string Mode { get; set; }
        public string RstSent { get; set; }
        public string RstRcvd { get; set; }
        public string Name { get; set; }
        public string Qth { get; set; }
        public string Comment { get; set; }
        public string Notes { get; set; }
        public double? TxPower { get; set; }
        public ulong WhenUnix { get; set; }
        public ulong? TimeOffUnix { get; set; }
        public bool TimeKnown { get; set; } = true;
        public bool Confirmed { get; set; }
        public bool AwardConfirmed { get; set; }
        public NexusQslRcvd QslRcvd { get; set; } = new NexusQslRcvd();
        public NexusQslSent QslSent { get; set; } = new NexusQslSent();
        public List<string> CreditGranted { get; set; } = new List<string>();
        public List<string> CreditSubmitted { get; set; } = new List<string>();
        public NexusUploadState Upload { get; set; } = new NexusUploadState();
        public NexusOta Ota { get; set; } = new NexusOta();
        public uint? Dxcc { get; set; }
        public string PropMode { get; set; }
        public string SatName { get; set; }
        public string Operator { get; set; }
        public string StationCallsign { get; set; }
        public string MyGrid { get; set; }
        public string MyRig { get; set; }
        // From LOG_ROWS: the key an edit or delete must send back (Nexus refuses a stale one).
        public string EditKey { get; set; }
        // Rust (String, String) tuples serialize as two-element arrays.
        public List<List<string>> Extra { get; set; } = new List<List<string>>();

        public string ExtraValue(string tag)
        {
            foreach (var kv in Extra)
                if (kv.Count == 2 && string.Equals(kv[0], tag, StringComparison.OrdinalIgnoreCase)) return kv[1];
            return null;
        }
    }

    public class NexusQslRcvd { public bool Card { get; set; } public bool Lotw { get; set; } public bool Eqsl { get; set; } public bool Qrz { get; set; } }
    public class NexusQslSent { public bool Sent { get; set; } public string Via { get; set; } public ulong? DateUnix { get; set; } }
    public class NexusOta { public string MyProgram { get; set; } public string MyRef { get; set; } public string TheirProgram { get; set; } public string TheirRef { get; set; } public string Iota { get; set; } }

    // Nexus's upload state (tempo_core::logbook::UploadState): lotw, eqsl, qrz, clublog only.
    // outcome: pending | accepted | duplicate | rejected | authfail. "Sent" = the first three.
    public class NexusUploadStatus
    {
        public string Outcome { get; set; }
        public long WhenUnix { get; set; }
        public string Detail { get; set; }
        [JsonIgnore] public bool IsSent => Outcome == "pending" || Outcome == "accepted" || Outcome == "duplicate";
    }
    public class NexusUploadState
    {
        public NexusUploadStatus Lotw { get; set; }
        public NexusUploadStatus Eqsl { get; set; }
        public NexusUploadStatus Qrz { get; set; }
        public NexusUploadStatus Clublog { get; set; }
    }

    public class NexusLogRows
    {
        public ulong Revision { get; set; }
        public string Freshness { get; set; }
        public string Why { get; set; }
        public int Total { get; set; }
        public int Offset { get; set; }
        public List<NexusQso> Rows { get; set; } = new List<NexusQso>();
        public string Error { get; set; }
    }

    public class NexusLogQsoReply
    {
        // saved | already | unconfirmed | duplicate | closed -- or "unknown" when no whole reply came.
        public string State { get; set; }
        public string Id { get; set; }
        public string Why { get; set; }
        // On "duplicate": the contact already in the log that Nexus's rule matched, when known.
        public string ExistingId { get; set; }
        public ulong? ExistingWhenUnix { get; set; }
    }

    public class NexusUploadReply
    {
        public string State { get; set; }    // stamped | sent-not-stamped | unsent | unknown
        public string Outcome { get; set; }  // accepted | duplicate | pending | rejected | authfail
        public string Detail { get; set; }
        public string Message { get; set; }  // the service's own words, when it gave any
        public string Why { get; set; }      // why nothing was sent or recorded
    }

    public class NexusExportReply
    {
        public string State { get; set; }   // saved | error | unknown
        public string Why { get; set; }
        public int Saving { get; set; }     // recent changes the file lacks, still being saved
        public int Held { get; set; }       // recent changes the file lacks, refused by the store
    }

    public class NexusWriteReply
    {
        // saved | unconfirmed | changed | gone | busy | error | closed -- or "unknown" when no whole reply came.
        public string State { get; set; }
        public string Why { get; set; }
        public JsonElement? Detail { get; set; }
    }

    public class NexusLogClient
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        // A durable write waits up to Nexus's DURABLE_WAIT (60 s) for the disk; allow for that.
        private const int WriteTimeoutMs = 75_000;
        private const int ReadTimeoutMs = 30_000;
        private const int UploadTimeoutMs = 100_000; // eQSL alone can take a minute to answer

        private readonly int _port;
        public NexusLogClient(int port) { _port = port; }

        public string StatusJson() => Send("LOG_STATUS", ReadTimeoutMs);

        public NexusLogQsoReply LogQso(string reqId, NexusQso qso)
        {
            string arg = JsonSerializer.Serialize(new { reqId, qso }, Json);
            string resp = Send("LOG_QSO " + arg, WriteTimeoutMs);
            if (resp == null || !resp.StartsWith("{"))
                return new NexusLogQsoReply { State = "unknown", Why = resp ?? "no reply" };
            try { return JsonSerializer.Deserialize<NexusLogQsoReply>(resp, Json); }
            catch (JsonException e) { return new NexusLogQsoReply { State = "unknown", Why = e.Message }; }
        }

        public NexusLogRows Rows(int offset = 0, int limit = 0)
        {
            string resp = Send($"LOG_ROWS {{\"offset\":{offset},\"limit\":{limit}}}", ReadTimeoutMs);
            if (resp == null || !resp.StartsWith("{")) return new NexusLogRows { Error = resp ?? "no reply" };
            try { return JsonSerializer.Deserialize<NexusLogRows>(resp, Json); }
            catch (JsonException e) { return new NexusLogRows { Error = e.Message }; }
        }

        public NexusWriteReply Edit(string id, string editKey, NexusQso qso) =>
            Write("LOG_EDIT " + JsonSerializer.Serialize(new { id, editKey, qso }, Json));

        public NexusWriteReply Delete(string id, string editKey) =>
            Write("LOG_DELETE " + JsonSerializer.Serialize(new { id, editKey }, Json));

        // The ADIF text never rides the control line (8 KB cap) -- EngineHost reads the file.
        public NexusWriteReply Import(string adifPath) =>
            Write("LOG_IMPORT " + JsonSerializer.Serialize(new { path = adifPath }, Json));

        // kind: "lotw" | "qrz" | "eqsl". A LoTW file must be LoTW's download exactly as received
        // (its "ARRL Logbook of the World Status Report" header is how Nexus knows QSL_RCVD there
        // means LoTW, not a paper card).
        // One contact (Nexus id) to one service (qrz | clublog | eqsl), sent, classified and recorded
        // by Nexus (LOG_UPLOAD). The service's credentials ride with this one request only.
        public NexusUploadReply Upload(object args)
        {
            string resp = Send("LOG_UPLOAD " + JsonSerializer.Serialize(args, Json), UploadTimeoutMs);
            if (resp == null || !resp.StartsWith("{")) return new NexusUploadReply { State = "unknown", Why = resp ?? "no reply" };
            try { return JsonSerializer.Deserialize<NexusUploadReply>(resp, Json); }
            catch (JsonException e) { return new NexusUploadReply { State = "unknown", Why = e.Message }; }
        }

        // Nexus's own ADIF export of the whole log, written by EngineHost to adifPath.
        public NexusExportReply Export(string adifPath)
        {
            string resp = Send("LOG_EXPORT " + JsonSerializer.Serialize(new { path = adifPath }, Json), WriteTimeoutMs);
            if (resp == null || !resp.StartsWith("{")) return new NexusExportReply { State = "unknown", Why = resp ?? "no reply" };
            try { return JsonSerializer.Deserialize<NexusExportReply>(resp, Json); }
            catch (JsonException e) { return new NexusExportReply { State = "unknown", Why = e.Message }; }
        }

        public NexusWriteReply Merge(string kind, string adifPath) =>
            Write("LOG_MERGE " + JsonSerializer.Serialize(new { path = adifPath, kind }, Json));

        // service: lotw | eqsl | qrz | clublog. outcome: pending | accepted | duplicate | rejected | authfail.
        // Jimmy's own APP_JIMMY_ fields on one contact, set (a blank value removes the field).
        public NexusWriteReply SetExtra(string id, List<string[]> set) =>
            Write("LOG_SET_EXTRA " + JsonSerializer.Serialize(new { id, set }, Json));

        public NexusWriteReply StampUpload(string id, string service, string outcome, long whenUnix, string detail = null) =>
            Write("LOG_STAMP_UPLOAD " + JsonSerializer.Serialize(new { id, service, outcome, whenUnix, detail }, Json));

        private NexusWriteReply Write(string line)
        {
            string resp = Send(line, WriteTimeoutMs);
            if (resp == null || !resp.StartsWith("{"))
                return new NexusWriteReply { State = "unknown", Why = resp ?? "no reply" };
            try { return JsonSerializer.Deserialize<NexusWriteReply>(resp, Json); }
            catch (JsonException e) { return new NexusWriteReply { State = "unknown", Why = e.Message }; }
        }

        public string Flush() => Send("LOG_FLUSH", WriteTimeoutMs);

        public string Shutdown(string sessionToken) => Send("SHUTDOWN " + sessionToken, WriteTimeoutMs);

        // null = no whole reply (connect failed, timed out, or cut off before the newline).
        private string Send(string command, int timeoutMs)
        {
            using (var client = new TcpClient())
            {
                try
                {
                    var connect = client.ConnectAsync(System.Net.IPAddress.Loopback, _port).ObserveFault();
                    if (!connect.Wait(3000) || !client.Connected) return null;
                    using (var stream = client.GetStream())
                    {
                        stream.WriteTimeout = 3000;
                        stream.ReadTimeout = timeoutMs;
                        byte[] cmd = Encoding.UTF8.GetBytes(command + "\n");
                        stream.Write(cmd, 0, cmd.Length);
                        client.Client.Shutdown(SocketShutdown.Send);
                        using (var ms = new MemoryStream())
                        {
                            byte[] buf = new byte[65536];
                            int n;
                            while ((n = stream.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                            string text = Encoding.UTF8.GetString(ms.ToArray());
                            if (!text.EndsWith("\n")) return null; // cut off: not a whole answer
                            return text.TrimEnd('\r', '\n');
                        }
                    }
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        public static string ToJson(object o) => JsonSerializer.Serialize(o, Json);
        public static T FromJson<T>(string s) => JsonSerializer.Deserialize<T>(s, Json);
    }
}
