using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WSJTX_Controller
{
    // POTA/SOTA activator spot, normalized across both programs (mirrors Nexus's own
    // propagation::pota::OtaSpot -- see EngineHost/src/external_data.rs).
    public class OtaSpot
    {
        public string Program { get; set; }      // "POTA" | "SOTA"
        public string Reference { get; set; }     // e.g. "K-1234" / "W7A/MN-001"
        public string Name { get; set; }
        public string Activator { get; set; }
        public double FreqKhz { get; set; }
        public string Mode { get; set; }
        public string Spotter { get; set; }
        public string Comment { get; set; }
        public string Grid { get; set; }
        public long? SpotTimeUnix { get; set; }
    }

    public class OtaSpotsResult
    {
        public OtaSpot[] Spots { get; set; } = Array.Empty<OtaSpot>();
        public long? AgeSecs { get; set; }
        public string LastError { get; set; }
    }

    public class SpaceWx
    {
        public float Sfi { get; set; }
        public float? Ssn { get; set; }
        public float Kp { get; set; }
        public float AIndex { get; set; }
        public float XrayLong { get; set; }
    }

    public class SpaceWxResult
    {
        public SpaceWx Value { get; set; }
        public long? AgeSecs { get; set; }
        public string LastError { get; set; }
    }

    public class HamQthLookupResult
    {
        public string Call { get; set; }
        public string Name { get; set; }
        public string Qth { get; set; }
        public string Grid { get; set; }
        public string State { get; set; }
        public string Country { get; set; }
    }

    // Standalone client for the Nexus-backed facts EngineHost exposes beyond the FT8/FT4 engine
    // itself (release-candidate pass: "use Nexus for what it already does well" -- POTA/SOTA
    // spots, space weather, eQSL, HamQTH). Deliberately independent of WsjtxClient/Controller --
    // no ctrl reference, no WinForms -- so it's testable on its own and doesn't grow
    // WsjtxClient.Direct.cs's already-large surface for a feature that isn't part of the core
    // engine contract. Talks to the SAME control port DirectEngineClient already uses
    // (NativeEngineClient.ControlPort) -- one control server, one port, shared rather than
    // duplicated, same convention as every other Direct command.
    //
    // Not thread-safe by design, same convention as RigctldClient/DirectSendCommand: each call
    // opens and closes its own short-lived TCP connection, so concurrent calls from different
    // threads are fine as long as callers don't share a single instance's internal state (there
    // isn't any -- this class carries no fields at all).
    public class ExternalDataClient
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        // POTA/SOTA + space weather are cache-only reads on EngineHost's side (see
        // external_data.rs) -- always fast, same short timeout as SNAPSHOT.
        private const int FastTimeoutMs = 3000;
        // eQSL specifically can take up to ~60s (it builds the file server-side, per Nexus's own
        // transport comment) -- HamQTH's two-step login+lookup is much faster but shares the same
        // generous ceiling for simplicity. Real network calls, not a per-second poll, so a long
        // timeout here costs nothing during normal operation.
        private const int SlowTimeoutMs = 65_000;

        public OtaSpotsResult GetOtaSpots(out string error)
        {
            error = null;
            string json = SendCommand("OTA_SPOTS", FastTimeoutMs);
            if (json == null) { error = "No response from engine host."; return null; }
            try
            {
                return JsonSerializer.Deserialize<OtaSpotsResult>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                error = $"Could not parse OTA_SPOTS response: {ex.Message}";
                return null;
            }
        }

        public SpaceWxResult GetSpaceWx(out string error)
        {
            error = null;
            string json = SendCommand("SPACE_WX", FastTimeoutMs);
            if (json == null) { error = "No response from engine host."; return null; }
            try
            {
                return JsonSerializer.Deserialize<SpaceWxResult>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                error = $"Could not parse SPACE_WX response: {ex.Message}";
                return null;
            }
        }

        // Returns eQSL's wire-tagged outcome ("pending"/"accepted"/"duplicate"/"rejected"/
        // "auth_fail") on success. Jimmy's own upload-tracking (LogbookDb.MarkUploaded) decides
        // what each outcome means for local bookkeeping -- this client only relays the fact.
        public string UploadEqsl(string username, string password, string recordAdif, out string error)
        {
            error = null;
            var args = new { username, password, recordAdif };
            string json = JsonSerializer.Serialize(args, JsonOptions);
            string resp = SendCommand("EQSL_UPLOAD " + json, SlowTimeoutMs);
            return ParseOkOrError(resp, out error);
        }

        // Returns the raw ADIF InBox body (Jimmy's own AdifImporter reconciles it against the
        // local logbook, same dedup-by-dedup_key path every other ADIF import already uses --
        // EngineHost never touches Jimmy's database). sinceUnix is Jimmy's own last-synced
        // watermark for this service, same idea as its existing LoTW/Club Log refresh tracking.
        public string DownloadEqsl(string username, string password, long? sinceUnix, out string error)
        {
            error = null;
            var args = new { username, password, sinceUnix };
            string json = JsonSerializer.Serialize(args, JsonOptions);
            string resp = SendCommand("EQSL_DOWNLOAD " + json, SlowTimeoutMs);
            return ParseOkOrError(resp, out error);
        }

        public HamQthLookupResult LookupHamQth(string username, string password, string callsign, out string error)
        {
            error = null;
            var args = new { username, password, callsign };
            string json = JsonSerializer.Serialize(args, JsonOptions);
            string resultJson = ParseOkOrError(SendCommand("HAMQTH_LOOKUP " + json, SlowTimeoutMs), out error);
            if (resultJson == null) return null;
            try
            {
                return JsonSerializer.Deserialize<HamQthLookupResult>(resultJson, JsonOptions);
            }
            catch (Exception ex)
            {
                error = $"Could not parse HamQTH lookup result: {ex.Message}";
                return null;
            }
        }

        // "OK <payload>" -> payload (trimmed leading space); "ERR <message>" -> error set,
        // returns null; anything else (no response, garbled) -> generic error, returns null.
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

        // Same one-connection-per-command shape as DirectSendCommand (WsjtxClient.Direct.cs) --
        // deliberately not shared code with it (that method is private to WsjtxClient and this
        // class is intentionally standalone), but the same bounded-timeout, never-throws
        // discipline, since a hung/slow engine host must never hang the caller either.
        private static string SendCommand(string command, int timeoutMs)
        {
            using (var client = new TcpClient())
            {
                var connectTask = client.ConnectAsync(System.Net.IPAddress.Loopback, NativeEngineClient.ControlPort);
                if (!connectTask.Wait(Math.Min(timeoutMs, 3000)) || !client.Connected) return null;

                using (var stream = client.GetStream())
                {
                    stream.WriteTimeout = 3000;
                    stream.ReadTimeout = timeoutMs;
                    byte[] cmd = Encoding.UTF8.GetBytes(command + "\n");
                    try
                    {
                        stream.Write(cmd, 0, cmd.Length);
                        client.Client.Shutdown(SocketShutdown.Send);
                    }
                    catch (IOException)
                    {
                        return null;
                    }

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
                            // Read timeout or connection reset -- best-effort, matches
                            // DirectSendCommand's own tolerance.
                        }
                        return Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\r', '\n');
                    }
                }
            }
        }
    }
}
