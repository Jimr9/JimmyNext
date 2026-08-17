using System;
using System.Drawing;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Accessible, keyboard-navigable POTA/SOTA activator-spot browser (release-candidate pass:
    // "expose useful Nexus facts... in an accessible form", not a visual dashboard). Built
    // entirely in code, no Designer.cs -- same convention as LookupInfoDlg.cs. A plain
    // ListView(View=Details) is used deliberately: it is natively keyboard-navigable (arrow keys
    // move between rows, Tab/Shift+Tab between controls, column headers and cell text are read
    // by JAWS/NVDA out of the box) without any custom accessibility infrastructure.
    //
    // Non-modal (Show(), not ShowDialog()) and left open across a session, same pattern as
    // LogbookWindow -- an operator chasing POTA/SOTA activity wants this visible alongside
    // normal operation, not a one-shot dialog.
    //
    // Ownership stays clean: EngineHost/Nexus supplies the facts (via ExternalDataClient), this
    // window only renders them plus Jimmy's own existing award/logbook intelligence
    // (OtaSpotAnnotator, itself a thin read-only wrapper over LogbookDb/AwardMatcher's existing
    // logic) -- no new business logic lives in this file.
    public class OtaSpotsWindow : Form
    {
        private readonly ListView _list;
        private readonly Label _statusLabel;
        private readonly Button _refreshButton;
        private readonly System.Windows.Forms.Timer _refreshTimer;

        private readonly ExternalDataClient _client = new ExternalDataClient();
        // Own instance, same convention as LogbookWindow's own _db field -- LogbookDb's
        // default constructor resolves the one real logbook path; a second independent
        // instance pointed at the same file is the established, already-proven-safe way to
        // read it from a second window (see LogbookWindow.cs), rather than reaching into
        // WsjtxClient's private field.
        private readonly LogbookDb _logbookDb = new LogbookDb();
        private readonly LookupManager _lookupManager;
        private readonly Func<System.Collections.Generic.Dictionary<string, WsjtxClient.ActiveAwardTag>> _activeAwardTags;
        private readonly Func<string> _currentBand;

        // Cache-only reads on EngineHost's side (see external_data.rs) -- this can poll faster
        // than the underlying feeds refresh (90s) without wasting anything; matches
        // DirectPollIntervalMs's own "cheap, dedup handles the rest" reasoning.
        private const int RefreshIntervalMs = 15000;

        public OtaSpotsWindow(LookupManager lookupManager,
            Func<System.Collections.Generic.Dictionary<string, WsjtxClient.ActiveAwardTag>> activeAwardTags,
            Func<string> currentBand)
        {
            _lookupManager = lookupManager;
            _activeAwardTags = activeAwardTags;
            _currentBand = currentBand;

            Text = "POTA / SOTA Spots";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            Size = new Size(760, 420);
            Font = new Font("Microsoft Sans Serif", 9F);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); if (e.KeyCode == Keys.F5) RefreshSpots(); };

            _list = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                MultiSelect = false,
                Dock = DockStyle.Fill,
                TabIndex = 0,
                AccessibleName = "POTA and SOTA spots",
                AccessibleDescription = "Currently spotted park and summit activators. Press F5 to refresh.",
            };
            _list.Columns.Add("Program", 70);
            _list.Columns.Add("Reference", 90);
            _list.Columns.Add("Activator", 100);
            _list.Columns.Add("Freq/Mode", 110);
            _list.Columns.Add("Age", 70);
            _list.Columns.Add("Status", 220);

            var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 34 };
            _statusLabel = new Label
            {
                Location = new Point(8, 8),
                Size = new Size(560, 20),
                AccessibleName = "Spot list status",
                Text = "Loading...",
            };
            _refreshButton = new Button
            {
                Text = "Refresh",
                Size = new Size(90, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                TabIndex = 1,
                AccessibleName = "Refresh spots now",
            };
            _refreshButton.Click += (s, e) => RefreshSpots();
            bottomPanel.Controls.Add(_statusLabel);
            bottomPanel.Controls.Add(_refreshButton);
            bottomPanel.Resize += (s, e) => { _refreshButton.Location = new Point(bottomPanel.Width - _refreshButton.Width - 8, 6); };

            Controls.Add(_list);
            Controls.Add(bottomPanel);

            _refreshTimer = new System.Windows.Forms.Timer { Interval = RefreshIntervalMs };
            _refreshTimer.Tick += (s, e) => RefreshSpots();

            Load += (s, e) => { RefreshSpots(); _refreshTimer.Start(); };
            FormClosed += (s, e) => { _refreshTimer.Stop(); _logbookDb?.Dispose(); };

            _refreshButton.Location = new Point(Width - _refreshButton.Width - 24, 6);
        }

        // Synchronous: ExternalDataClient's OTA_SPOTS call is a cache-only local read on
        // EngineHost's side (bounded ~3s timeout, no internet round-trip on this thread) -- same
        // "fast enough for a UI-thread call" reasoning as radioPollTimer_Tick's own PollOnce().
        private void RefreshSpots()
        {
            var result = _client.GetOtaSpots(out string error);
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                if (result?.Spots != null)
                {
                    string band = _currentBand?.Invoke();
                    var tags = _activeAwardTags?.Invoke();
                    foreach (var spot in result.Spots)
                    {
                        var annotation = OtaSpotAnnotator.Annotate(spot.Activator, band, _logbookDb, _lookupManager, tags);
                        var item = new ListViewItem(spot.Program ?? "");
                        item.SubItems.Add(spot.Reference ?? "");
                        item.SubItems.Add(spot.Activator ?? "");
                        item.SubItems.Add($"{spot.FreqKhz / 1000.0:0.000} MHz {spot.Mode}");
                        item.SubItems.Add(FormatAge(spot.SpotTimeUnix));
                        item.SubItems.Add(FormatStatus(annotation));
                        // One accessible name per row that reads sensibly as a whole, matching
                        // the conceptual example from the release-candidate request ("K1ABC --
                        // POTA K-1234 -- 20m FT8 -- spotted 2 min ago -- needed for 2 awards").
                        item.Name = spot.Activator;
                        item.ToolTipText = $"{spot.Activator} -- {spot.Program} {spot.Reference} -- " +
                            $"{spot.FreqKhz / 1000.0:0.000} MHz {spot.Mode} -- spotted {FormatAge(spot.SpotTimeUnix)} -- {FormatStatus(annotation)}";
                        _list.Items.Add(item);
                    }
                }
            }
            finally
            {
                _list.EndUpdate();
            }

            if (error != null)
                _statusLabel.Text = $"{_list.Items.Count} spots (stale) -- {error}";
            else if (result?.LastError != null)
                _statusLabel.Text = $"{_list.Items.Count} spots -- feed warning: {result.LastError}";
            else
                _statusLabel.Text = $"{_list.Items.Count} spots" + (result?.AgeSecs != null ? $" -- as of {result.AgeSecs}s ago" : "");
        }

        private static string FormatAge(long? spotTimeUnix)
        {
            if (spotTimeUnix == null) return "unknown";
            var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - spotTimeUnix.Value;
            if (age < 0) return "just now";
            if (age < 60) return $"{age}s ago";
            if (age < 3600) return $"{age / 60}m ago";
            return $"{age / 3600}h ago";
        }

        private static string FormatStatus(OtaSpotAnnotation a)
        {
            if (a == null) return "";
            string worked = a.WorkedBefore ? "worked before" : "not worked before";
            string needed = a.NeededForAwardCount > 0
                ? $"needed for {a.NeededForAwardCount} award{(a.NeededForAwardCount == 1 ? "" : "s")}"
                : "not currently needed";
            return $"{worked}, {needed}";
        }
    }
}
