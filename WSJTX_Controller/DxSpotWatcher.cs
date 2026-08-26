using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;

namespace WSJTX_Controller
{
    // Last-known reception report for one watched callsign.
    public class SpotInfo
    {
        public string Band;
        public string Mode;
        public DateTime UtcTime;
        public string SpotterCall;
        public string SpotterGrid;
        // Signal report (dB) the spotter heard the watched station at ("rp" in the
        // MQTT payload) -- null if the payload didn't include one.
        public int?   Snr;
        // The watched station's own grid square ("sl", sender locator, in the MQTT
        // payload) -- independent of ever decoding them directly.
        public string SenderGrid;
        // Exact frequency in Hz ("f" in the MQTT payload) -- null if not included.
        public long?  Frequency;
        // The spotter's ADIF/DXCC entity number ("ra", receiver ADIF entity, in the
        // MQTT payload) -- PSKReporter's own authoritative country classification,
        // not a grid-square guess. Null/0 if the payload didn't include one.
        public int?   SpotterDxccEntity;
    }

    // Watches the PSKReporter live-spot MQTT feed (mqtt.pskreporter.info, no auth/registration)
    // for a small, user-curated set of callsigns and keeps each one's most recent reception
    // report. Push-based (MQTT subscribe), not polled -- watching many callsigns at once
    // carries no per-request rate-limit risk, unlike PSKReporter's HTTP query API, which is
    // exactly why this exists instead of that (see project decision, 2026-07-07: DX Spot
    // Watch investigation, started from wanting "last spotted" info for 13 Colonies chasing).
    //
    // All MQTTnet callbacks run on a background thread. Updated fires there too -- subscribers
    // must marshal back to the UI thread (e.g. Control.BeginInvoke) before touching any control.
    public class DxSpotWatcher : IDisposable, ILookupProvider
    {
        private const string Broker = "mqtt.pskreporter.info";

        public string SourceName => "DX Spot Watch";
        public bool   IsEnabled  => true;

        private readonly IManagedMqttClient _client;
        private readonly Dictionary<string, SpotInfo> _lastSpots = new Dictionary<string, SpotInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _subscribedCalls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();
        // Independent audit finding 5, 2026-08-23 (LIKELY bug, MEDIUM PRIORITY): serializes
        // UpdateWatchList reconciliations -- see that method's own comment for the race this
        // closes (two rapid Options saves computing toAdd/toRemove against the same stale
        // _subscribedCalls snapshot and finishing out of order, leaving a subscription set that
        // matches neither the old nor the latest desired list).
        private readonly SemaphoreSlim _reconcileLock = new SemaphoreSlim(1, 1);
        private HashSet<string> _latestDesired;

        // Raised whenever any watched call's last-seen data changes, or the watch list itself
        // changes. Fires on a background thread -- see class remarks above.
        public event Action Updated;

        public DxSpotWatcher()
        {
            _client = new MqttFactory().CreateManagedMqttClient();
            _client.ApplicationMessageReceivedAsync += OnMessageReceived;
        }

        // Reconciles the live MQTT subscriptions against the desired watch list: subscribes to
        // newly-added calls, unsubscribes removed ones, connects if not yet connected and the
        // list is non-empty, and fully disconnects when the list becomes empty (no connection
        // held open for nothing to watch). Safe to call repeatedly (e.g. every Options save).
        //
        // Independent audit finding 5, 2026-08-23 (LIKELY bug, MEDIUM PRIORITY, confidence 92%):
        // this used to be `async void` with no lock/generation/cancellation around the whole
        // multi-await reconciliation -- two rapid calls (e.g. two Options saves) could each
        // snapshot toAdd/toRemove against the same stale _subscribedCalls state and complete out
        // of order (example from the audit: request A adds X; before its subscribe finishes,
        // request B computes its own toAdd/toRemove against the same pre-A state and asks only
        // for Y; neither accounts for the other, leaving X+Y subscribed although the latest
        // desired set was only Y). Returns a real Task (callers that don't need to await it can
        // still fire-and-forget) and serializes every call through _reconcileLock so only one
        // reconciliation pass ever runs at a time; _latestDesired/the while loop below make a
        // queued-up caller's own pass redundant (superseded) rather than stale-and-wrong -- the
        // thread that wins the lock keeps reconciling against whatever the CURRENT latest
        // desired set is until a pass observes no newer request arrived during it, so every
        // caller's desired outcome is eventually reached by exactly one thread, not raced.
        public async Task UpdateWatchList(HashSet<string> calls)
        {
            var desired = calls ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_lock) { _latestDesired = desired; }

            await _reconcileLock.WaitAsync().ConfigureAwait(false);
            try
            {
                while (true)
                {
                    HashSet<string> toReconcile;
                    lock (_lock) { toReconcile = _latestDesired; }
                    await ReconcileAsync(toReconcile).ConfigureAwait(false);
                    lock (_lock)
                    {
                        // No newer request arrived while this pass was running -- done. If one
                        // did, _latestDesired now points at it; loop and reconcile again rather
                        // than leaving it unaddressed for a caller that already returned.
                        if (ReferenceEquals(_latestDesired, toReconcile)) break;
                    }
                }
            }
            finally
            {
                _reconcileLock.Release();
            }
        }

        private async Task ReconcileAsync(HashSet<string> desired)
        {
            try
            {
                if (desired.Count == 0)
                {
                    if (_client.IsStarted) await _client.StopAsync();
                    lock (_lock) { _subscribedCalls.Clear(); _lastSpots.Clear(); }
                    Updated?.Invoke();
                    return;
                }

                if (!_client.IsStarted)
                {
                    var options = new ManagedMqttClientOptionsBuilder()
                        .WithAutoReconnectDelay(TimeSpan.FromSeconds(5))
                        .WithClientOptions(new MqttClientOptionsBuilder()
                            .WithClientId(Guid.NewGuid().ToString())
                            .WithTcpServer(Broker)
                            .Build())
                        .Build();
                    await _client.StartAsync(options);
                }

                List<string> toAdd, toRemove;
                lock (_lock)
                {
                    toAdd    = desired.Except(_subscribedCalls, StringComparer.OrdinalIgnoreCase).ToList();
                    toRemove = _subscribedCalls.Except(desired, StringComparer.OrdinalIgnoreCase).ToList();
                }

                foreach (var call in toAdd)
                {
                    await _client.SubscribeAsync(new[] { new MqttTopicFilterBuilder().WithTopic(TopicFor(call)).Build() });
                    lock (_lock) { _subscribedCalls.Add(call); }
                }
                foreach (var call in toRemove)
                {
                    await _client.UnsubscribeAsync(TopicFor(call));
                    lock (_lock) { _subscribedCalls.Remove(call); _lastSpots.Remove(call); }
                }

                Updated?.Invoke();
            }
            catch
            {
                // Best-effort background feature -- a broker hiccup must never take down the
                // rest of Jimmy. UpdateWatchList will be retried on the next Options save, and
                // ManagedMqttClient's own auto-reconnect covers a mid-session drop.
            }
        }

        // Sender = watched call, any band/mode/receiver -- matches the documented PSKReporter
        // MQTT topic scheme (see M0LTE/pskr-mqtt-listener-example).
        private static string TopicFor(string call) => $"pskr/filter/v2/+/+/{call}/#";

        private Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs arg)
        {
            try
            {
                string json = arg.ApplicationMessage.ConvertPayloadToString();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return Task.CompletedTask;
                JsonElement root = doc.RootElement;

                string sender = GetStringOrNull(root, "sc");
                if (string.IsNullOrEmpty(sender)) return Task.CompletedTask;

                int? snr = null;
                if (TryGetNumericText(root, "rp", out var rpText) && int.TryParse(rpText, out var rpVal)) snr = rpVal;

                long? freq = null;
                if (TryGetNumericText(root, "f", out var fText) && long.TryParse(fText, out var fVal)) freq = fVal;

                int? spotterEntity = null;
                if (TryGetNumericText(root, "ra", out var raText) && int.TryParse(raText, out var raVal) && raVal > 0) spotterEntity = raVal;

                var spot = new SpotInfo
                {
                    Band              = GetStringOrNull(root, "b"),
                    Mode              = GetStringOrNull(root, "md"),
                    SpotterCall       = GetStringOrNull(root, "rc"),
                    SpotterGrid       = GetStringOrNull(root, "rl"),
                    SenderGrid        = GetStringOrNull(root, "sl"),
                    Snr               = snr,
                    Frequency         = freq,
                    SpotterDxccEntity = spotterEntity,
                    UtcTime           = root.TryGetProperty("t", out var tEl) ? UnixToUtc(tEl.GetInt64()) : DateTime.UtcNow,
                };

                bool changed;
                lock (_lock)
                {
                    // Only watched calls are ever subscribed to, but confirm before recording --
                    // a stray retained/late message for a just-unsubscribed call must not revive it.
                    if (!_subscribedCalls.Contains(sender)) return Task.CompletedTask;
                    changed = !_lastSpots.TryGetValue(sender, out var existing) || spot.UtcTime >= existing.UtcTime;
                    if (changed) _lastSpots[sender] = spot;
                }
                if (changed) Updated?.Invoke();
            }
            catch
            {
                // Malformed/unexpected payload -- skip this spot rather than crash the MQTT loop.
            }
            return Task.CompletedTask;
        }

        private static DateTime UnixToUtc(long unixSeconds) =>
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(unixSeconds);

        // JavaScriptSerializer replacement helpers (System.Text.Json, net10.0-windows port).
        // Behavior-equivalent to the old Dictionary<string, object> lookups: a missing key or a
        // key whose JSON value isn't a string yields null, same as the old `x as string` casts.
        private static string GetStringOrNull(JsonElement root, string prop) =>
            root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // Tolerates the field arriving as either a JSON number or a JSON string, same as the old
        // Convert.ToString(object) + int/long.TryParse combination did for boxed values.
        private static bool TryGetNumericText(JsonElement root, string prop, out string text)
        {
            text = null;
            if (!root.TryGetProperty(prop, out var v)) return false;
            if (v.ValueKind == JsonValueKind.Number) { text = v.GetRawText(); return true; }
            if (v.ValueKind == JsonValueKind.String) { text = v.GetString(); return true; }
            return false;
        }

        // Even/Odd transmit-period parity for a spot, derived the same way Jimmy
        // already computes it for its own live decodes (WsjtxClient.IsEvenPeriod) --
        // FT4's period is irregular (four ~7-second windows per 30s) so it's
        // special-cased identically; every other mode (FT8 being the overwhelming
        // majority of PSKReporter spots) uses the standard 15-second period
        // division. This is a display-only field with no award/logic consequences
        // (unlike the live-decode version), so modes outside FT8/FT4 (e.g. WSPR,
        // whose own 2-minute cycle isn't a simple even/odd split) just fall through
        // to the same 15-second division as a best-effort label, not a precise one.
        public static bool IsEvenPeriod(DateTime utcTime, string mode)
        {
            int secPastMinute = (int)(utcTime.TimeOfDay.TotalSeconds % 60);
            if (string.Equals(mode, "FT4", StringComparison.OrdinalIgnoreCase))
            {
                return (secPastMinute >= 0 && secPastMinute < 7) || (secPastMinute >= 15 && secPastMinute < 22) ||
                       (secPastMinute >= 30 && secPastMinute < 37) || (secPastMinute >= 45 && secPastMinute < 52);
            }
            int secPastHour = (int)(utcTime.TimeOfDay.TotalSeconds % 3600);
            return (secPastHour / 15) % 2 == 0;
        }

        // Snapshot for rendering: one entry per currently-watched call, alphabetical -- a
        // screen-reader-navigated list should have a stable order, not reshuffle every time one
        // entry updates. SpotInfo is null for calls not yet seen this session.
        public List<KeyValuePair<string, SpotInfo>> Snapshot()
        {
            lock (_lock)
            {
                var calls = new List<string>(_subscribedCalls);
                calls.Sort(StringComparer.OrdinalIgnoreCase);
                return calls
                    .Select(c => new KeyValuePair<string, SpotInfo>(c, _lastSpots.TryGetValue(c, out var s) ? s : null))
                    .ToList();
            }
        }

        // Dictionary lookup only, already lock-protected -- safe for the
        // per-decode hot path. Only ever contributes for calls actually on the
        // watch list; a no-op for everything else.
        public void Contribute(LookupRecord record, string call)
        {
            SpotInfo spot;
            lock (_lock)
            {
                if (!_lastSpots.TryGetValue(call, out spot)) return;
            }
            record.LastSpot = spot;
            record.Sources.Add(SourceName);
        }

        public void Dispose()
        {
            _client.ApplicationMessageReceivedAsync -= OnMessageReceived;
            _client.Dispose();
        }
    }
}
