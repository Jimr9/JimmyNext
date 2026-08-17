using System;
using System.Drawing;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Accessible, keyboard-navigable Nexus-facts browser: POTA/SOTA spots, DX-cluster/RBN
    // spots, a plain-language band-conditions nowcast, and space weather -- each its own tab,
    // independently refreshable. Built entirely in code, no Designer.cs -- same convention as
    // LookupInfoDlg.cs. Every list is a plain ListView(View=Details): natively keyboard-
    // navigable (arrow keys move between rows, Tab/Shift+Tab between controls, column headers
    // and cell text read by JAWS/NVDA out of the box) without any custom accessibility
    // infrastructure. TabControl itself is a standard WinForms control with full built-in
    // keyboard support (Ctrl+Tab / Ctrl+Shift+Tab between tabs, arrow keys within the tab strip).
    //
    // Non-modal (Show(), not ShowDialog()) and left open across a session, same pattern as
    // LogbookWindow -- an operator chasing activity wants this visible alongside normal
    // operation, not a one-shot dialog.
    //
    // Ownership stays clean: EngineHost/Nexus supplies the facts (via ExternalDataClient), this
    // window only renders them plus Jimmy's own existing award/logbook intelligence
    // (OtaSpotAnnotator, a thin read-only wrapper over LogbookDb/AwardMatcher's existing logic)
    // -- no new business logic lives in this file.
    public class OtaSpotsWindow : Form
    {
        private readonly TabControl _tabs;
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

        // Cache-only reads on EngineHost's side (see external_data.rs / live_feeds.rs) -- this
        // can poll faster than the underlying feeds refresh without wasting anything; matches
        // DirectPollIntervalMs's own "cheap, dedup handles the rest" reasoning.
        private const int RefreshIntervalMs = 15000;

        // ── POTA/SOTA tab ────────────────────────────────────────────────────────
        private ListView _potaList;
        private Label _potaStatusLabel;

        // ── Band Conditions tab ─────────────────────────────────────────────────
        private TextBox _condHeadlineBox;
        private ListView _condBandsList;
        private Label _condStatusLabel;

        // ── DX Spots tab ─────────────────────────────────────────────────────────
        private ListView _dxList;
        private Label _dxStatusLabel;

        // ── Space Weather tab ────────────────────────────────────────────────────
        private TextBox _wxSfiValue, _wxSsnValue, _wxKpValue, _wxAValue, _wxXrayValue;
        private Label _wxStatusLabel;

        public OtaSpotsWindow(LookupManager lookupManager,
            Func<System.Collections.Generic.Dictionary<string, WsjtxClient.ActiveAwardTag>> activeAwardTags,
            Func<string> currentBand)
        {
            _lookupManager = lookupManager;
            _activeAwardTags = activeAwardTags;
            _currentBand = currentBand;

            // Shorter than the tab captions it sits above ("POTA / SOTA", "DX Spots", ...) --
            // the old title ("POTA / SOTA / DX Spots") repeated two of those captions verbatim,
            // so JAWS said the same words twice moving from window to tab strip. A live JAWS
            // pass on this window (2026-08-17) also found the TabControl/TabPage/ListView/
            // status-label AccessibleNames below all separately restating the same "POTA and
            // SOTA"/"DX cluster and RBN" phrases -- every one of those custom names was removed
            // or shortened below to stop the repetition; standard WinForms tab/list accessible
            // behavior (TabPage.Text as the tab's own name, same as LogbookWindow.cs) does the
            // rest without fighting JAWS's normal announcements.
            Text = "Spots & Conditions";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            MinimumSize = new Size(600, 340);
            Size = new Size(820, 460);
            Font = new Font("Microsoft Sans Serif", 9F);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); if (e.KeyCode == Keys.F5) RefreshAll(); };

            // No custom AccessibleName -- TabControl's default accessible behavior (JAWS
            // announces each TabPage's own Text as you switch) is exactly right here, same
            // convention as LogbookWindow.cs's own TabControl.
            _tabs = new TabControl
            {
                Dock = DockStyle.Fill,
            };
            _tabs.TabPages.Add(BuildPotaSotaTab());
            _tabs.TabPages.Add(BuildBandConditionsTab());
            _tabs.TabPages.Add(BuildDxSpotsTab());
            _tabs.TabPages.Add(BuildSpaceWeatherTab());
            Controls.Add(_tabs);

            _refreshTimer = new System.Windows.Forms.Timer { Interval = RefreshIntervalMs };
            _refreshTimer.Tick += (s, e) => RefreshAll();

            Load += (s, e) => { RefreshAll(); _refreshTimer.Start(); };
            FormClosed += (s, e) => { _refreshTimer.Stop(); _logbookDb?.Dispose(); };
        }

        private void RefreshAll()
        {
            RefreshPotaSota();
            RefreshBandConditions();
            RefreshDxSpots();
            RefreshSpaceWeather();
        }

        // ── Shared helpers ───────────────────────────────────────────────────────

        // No AccessibleName override -- TabPage's default accessible name is its own Text
        // (the tab caption already read when switching tabs), so a second, longer restatement
        // here was pure repetition (see the constructor's own comment).
        private static TabPage MakeTabPage(string title)
        {
            return new TabPage(title);
        }

        private static Button MakeRefreshButton(EventHandler onClick, string accessibleName)
        {
            var btn = new Button
            {
                Text = "Refresh",
                Size = new Size(90, 24),
                Dock = DockStyle.Bottom,
                AccessibleName = accessibleName,
            };
            btn.Click += onClick;
            return btn;
        }

        // accessibleName is deliberately short ("Spots list"/"Bands list") -- the tab already
        // named the subject when it was switched to (e.g. "POTA / SOTA"), so repeating it here
        // is the redundancy a live JAWS pass flagged. No AccessibleDescription: JAWS announces
        // it alongside the name every time focus lands on the list, and the column headers
        // already explain each field -- a longer one-time description added repetition, not
        // clarity, for a control the operator revisits constantly.
        private static ListView MakeListView(string accessibleName)
        {
            return new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                MultiSelect = false,
                Dock = DockStyle.Fill,
                TabIndex = 0,
                AccessibleName = accessibleName,
            };
        }

        // Formats a server-supplied AGE IN SECONDS (not a unix timestamp -- EngineHost's
        // live_feeds.rs/external_data.rs already compute the elapsed duration).
        private static string FormatAgeSecs(long? ageSecs)
        {
            if (ageSecs == null) return "unknown";
            long age = ageSecs.Value;
            if (age < 60) return $"{age}s ago";
            if (age < 3600) return $"{age / 60}m ago";
            return $"{age / 3600}h ago";
        }

        // Formats a unix SPOT TIMESTAMP (POTA/SOTA's own SpotTimeUnix) into an age phrase.
        private static string FormatAge(long? spotTimeUnix)
        {
            if (spotTimeUnix == null) return "unknown";
            var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - spotTimeUnix.Value;
            if (age < 0) return "just now";
            return FormatAgeSecs(age);
        }

        // ── POTA/SOTA tab ────────────────────────────────────────────────────────

        private TabPage BuildPotaSotaTab()
        {
            var page = MakeTabPage("POTA / SOTA");
            _potaList = MakeListView("Spots list");
            _potaList.Columns.Add("Program", 70);
            _potaList.Columns.Add("Reference", 90);
            _potaList.Columns.Add("Activator", 100);
            _potaList.Columns.Add("Freq/Mode", 110);
            _potaList.Columns.Add("Age", 70);
            _potaList.Columns.Add("Status", 150);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 30 };
            _potaStatusLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "Status", Text = "Loading..." };
            var refreshBtn = MakeRefreshButton((s, e) => RefreshPotaSota(), "Refresh POTA and SOTA spots now");
            refreshBtn.Dock = DockStyle.Right;
            bottom.Controls.Add(_potaStatusLabel);
            bottom.Controls.Add(refreshBtn);

            page.Controls.Add(_potaList);
            page.Controls.Add(bottom);
            return page;
        }

        // Synchronous: ExternalDataClient's OTA_SPOTS call is a cache-only local read on
        // EngineHost's side (bounded ~3s timeout, no internet round-trip on this thread) -- same
        // "fast enough for a UI-thread call" reasoning as radioPollTimer_Tick's own PollOnce().
        private void RefreshPotaSota()
        {
            var result = _client.GetOtaSpots(out string error);
            _potaList.BeginUpdate();
            try
            {
                _potaList.Items.Clear();
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
                        item.SubItems.Add($"{spot.FreqKhz / 1000.0:0.000} {spot.Mode}");
                        item.SubItems.Add(FormatAge(spot.SpotTimeUnix));
                        item.SubItems.Add(FormatStatus(annotation));
                        item.Name = spot.Activator;
                        _potaList.Items.Add(item);
                    }
                }
            }
            finally
            {
                _potaList.EndUpdate();
            }

            if (error != null)
                _potaStatusLabel.Text = $"{_potaList.Items.Count} spots (stale) -- {error}";
            else if (result?.LastError != null)
                _potaStatusLabel.Text = $"{_potaList.Items.Count} spots -- feed warning: {result.LastError}";
            else
                _potaStatusLabel.Text = $"{_potaList.Items.Count} spots" + (result?.AgeSecs != null ? $" -- as of {result.AgeSecs}s ago" : "");
        }

        // One clause, not two -- a live JAWS pass on this window found every row saying
        // "not worked before, not currently needed" (dozens of rows deep, almost always both
        // clauses negative). "Needed" only matters when it's true, so it now REPLACES the
        // worked/not-worked clause instead of appending to it; the negative "not currently
        // needed" half is silenced entirely rather than repeated on every row.
        // internal (not private): JimmyTests exercises this directly (InternalsVisibleTo, see
        // AssemblyInfo.Testing.cs) to lock in the concise wording without needing a live
        // EngineHost connection or a real LogbookDb.
        internal static string FormatStatus(OtaSpotAnnotation a)
        {
            if (a == null) return "";
            if (a.NeededForAwardCount > 0)
                return $"needed for {a.NeededForAwardCount} award{(a.NeededForAwardCount == 1 ? "" : "s")}";
            return a.WorkedBefore ? "worked" : "not worked";
        }

        // ── Band Conditions tab ──────────────────────────────────────────────────

        private TabPage BuildBandConditionsTab()
        {
            var page = MakeTabPage("Band Conditions");

            _condHeadlineBox = new TextBox
            {
                Dock = DockStyle.Top,
                Height = 40,
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Control,
                TabStop = true,
                AccessibleName = "Headline",
                Text = "Loading...",
            };

            _condBandsList = MakeListView("Bands list");
            _condBandsList.Columns.Add("Band", 60);
            _condBandsList.Columns.Add("Tier", 70);
            _condBandsList.Columns.Add("Confidence", 80);
            _condBandsList.Columns.Add("Hear Me / I Hear", 110);
            _condBandsList.Columns.Add("Best Region", 150);
            _condBandsList.Columns.Add("Reason", 260);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 30 };
            _condStatusLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "Status", Text = "Loading..." };
            var refreshBtn = MakeRefreshButton((s, e) => RefreshBandConditions(), "Refresh band conditions now");
            refreshBtn.Dock = DockStyle.Right;
            bottom.Controls.Add(_condStatusLabel);
            bottom.Controls.Add(refreshBtn);

            page.Controls.Add(_condBandsList);
            page.Controls.Add(_condHeadlineBox);
            page.Controls.Add(bottom);
            return page;
        }

        private void RefreshBandConditions()
        {
            var result = _client.GetBandConditions(out string error);
            _condBandsList.BeginUpdate();
            try
            {
                _condBandsList.Items.Clear();
                if (result?.Bands != null)
                {
                    foreach (var b in result.Bands)
                    {
                        var item = new ListViewItem(b.Band ?? "");
                        item.SubItems.Add(b.Tier ?? "");
                        item.SubItems.Add(b.Confidence ?? "");
                        item.SubItems.Add($"{b.NHearMe} / {b.NIHear}");
                        item.SubItems.Add(b.BestRegion != null
                            ? $"{b.BestRegion.Region} ({b.BestRegion.Octant}, {b.BestRegion.Stations} stn{(b.BestRegion.Stations == 1 ? "" : "s")})"
                            : "--");
                        item.SubItems.Add(b.Reason ?? "");
                        item.ToolTipText = $"{b.Band}: {b.Tier}, {b.Confidence} confidence -- {b.Reason} " +
                            $"(modeled: {b.Modeled} -- {b.ModeledReason})";
                        _condBandsList.Items.Add(item);
                    }
                }
            }
            finally
            {
                _condBandsList.EndUpdate();
            }

            if (error != null)
            {
                _condHeadlineBox.Text = "";
                _condStatusLabel.Text = $"(stale) -- {error}";
            }
            else if (result?.Error != null)
            {
                _condHeadlineBox.Text = "";
                _condStatusLabel.Text = result.Error;
            }
            else
            {
                _condHeadlineBox.Text = result?.Headline ?? "";
                string banners = (result?.Banners != null && result.Banners.Length > 0)
                    ? " -- " + string.Join("; ", result.Banners)
                    : "";
                string connected = result != null && result.Connected ? "connected" : "not connected";
                // Bands are always populated now (EngineHost's PropAdvisor falls back to a
                // physics-only model when there are no PSK Reporter reception reports yet --
                // see live_feeds.rs's band_conditions_json), so 0 reports no longer means an
                // empty tab. Call that out explicitly rather than leaving the operator to guess
                // whether the ladder below is observed or modeled.
                string modeledOnly = (result?.SpotCount ?? 0) == 0
                    ? " -- modeled only, no reception reports yet"
                    : "";
                _condStatusLabel.Text = $"{result?.SpotCount ?? 0} reception reports, {connected}" +
                    (result?.LastEventAgeSecs != null ? $", last report {FormatAgeSecs(result.LastEventAgeSecs)}" : "") +
                    modeledOnly + banners;
            }
        }

        // ── DX Spots tab ─────────────────────────────────────────────────────────

        private TabPage BuildDxSpotsTab()
        {
            var page = MakeTabPage("DX Spots");
            _dxList = MakeListView("Spots list");
            _dxList.Columns.Add("DX Call", 90);
            _dxList.Columns.Add("Frequency", 90);
            _dxList.Columns.Add("Mode", 70);
            _dxList.Columns.Add("Spotter", 90);
            _dxList.Columns.Add("Age", 70);
            _dxList.Columns.Add("Comment", 200);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 30 };
            _dxStatusLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "Status", Text = "Loading..." };
            var refreshBtn = MakeRefreshButton((s, e) => RefreshDxSpots(), "Refresh DX spots now");
            refreshBtn.Dock = DockStyle.Right;
            bottom.Controls.Add(_dxStatusLabel);
            bottom.Controls.Add(refreshBtn);

            page.Controls.Add(_dxList);
            page.Controls.Add(bottom);
            return page;
        }

        private void RefreshDxSpots()
        {
            var result = _client.GetDxSpots(out string error);
            _dxList.BeginUpdate();
            try
            {
                _dxList.Items.Clear();
                if (result?.Spots != null)
                {
                    foreach (var s in result.Spots)
                    {
                        var item = new ListViewItem(s.DxCall ?? "");
                        item.SubItems.Add($"{s.FreqKhz / 1000.0:0.000} MHz");
                        item.SubItems.Add(s.Rbn ? (s.SkimmerMode ?? "RBN") : "");
                        item.SubItems.Add(s.Spotter ?? "");
                        item.SubItems.Add(FormatAgeSecs(s.AgeSecs));
                        item.SubItems.Add(s.Comment ?? "");
                        item.ToolTipText = $"{s.DxCall} -- {s.FreqKhz / 1000.0:0.000} MHz -- spotted by {s.Spotter} " +
                            $"{FormatAgeSecs(s.AgeSecs)} -- {s.Comment}";
                        _dxList.Items.Add(item);
                    }
                }
            }
            finally
            {
                _dxList.EndUpdate();
            }

            if (error != null)
            {
                _dxStatusLabel.Text = $"{_dxList.Items.Count} spots (stale) -- {error}";
            }
            // The reverse beacon network (RBN) digital skimmer feed is always on and needs no
            // configuration -- this used to read "No DX cluster server configured", which was
            // both wrong (spots show up with nothing configured) and left the operator staring
            // at an empty list with no idea it would ever fill in. "Connected" not yet being
            // true here just means the RBN session hasn't come up yet.
            else if (result != null && !result.Connected && _dxList.Items.Count == 0)
            {
                _dxStatusLabel.Text = "Connecting to the reverse beacon network...";
            }
            else if (result != null && !result.Connected)
            {
                _dxStatusLabel.Text = $"{_dxList.Items.Count} spots -- not currently connected.";
            }
            else if (result != null && result.Stale)
            {
                _dxStatusLabel.Text = $"{_dxList.Items.Count} spots -- connected, but quiet for a while.";
            }
            else
            {
                // Configured=false is a normal, complete state now (RBN-only, see
                // DxSpotsResult's own comment) -- surfaced as a one-line opt-in tip, not an
                // error, since adding a human cluster node only ADDS coverage (SSB/phone) it
                // doesn't unlock something otherwise broken.
                string tip = (result != null && !result.Configured)
                    ? " -- add a DX cluster server in Options > Decode Engine for SSB/phone spots too"
                    : "";
                _dxStatusLabel.Text = $"{_dxList.Items.Count} spots" +
                    (result?.LastEventAgeSecs != null ? $" -- last spot {FormatAgeSecs(result.LastEventAgeSecs)}" : "") +
                    tip;
            }
        }

        // ── Space Weather tab ────────────────────────────────────────────────────

        private TabPage BuildSpaceWeatherTab()
        {
            var page = MakeTabPage("Space Weather");
            var panel = new Panel { Dock = DockStyle.Fill };

            int lx = 16, vx = 180, y = 16, rh = 26, fw = 200, tabIndex = 0;
            _wxSfiValue = AddWxRow(panel, "Solar Flux Index (SFI):", ref y, lx, vx, fw, rh, ref tabIndex);
            _wxSsnValue = AddWxRow(panel, "Sunspot Number (SSN):", ref y, lx, vx, fw, rh, ref tabIndex);
            _wxKpValue = AddWxRow(panel, "Planetary K-index (Kp):", ref y, lx, vx, fw, rh, ref tabIndex);
            _wxAValue = AddWxRow(panel, "Planetary A-index:", ref y, lx, vx, fw, rh, ref tabIndex);
            _wxXrayValue = AddWxRow(panel, "X-ray flux (long):", ref y, lx, vx, fw, rh, ref tabIndex);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 30 };
            _wxStatusLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "Status", Text = "Loading..." };
            var refreshBtn = MakeRefreshButton((s, e) => RefreshSpaceWeather(), "Refresh space weather now");
            refreshBtn.Dock = DockStyle.Right;
            bottom.Controls.Add(_wxStatusLabel);
            bottom.Controls.Add(refreshBtn);

            page.Controls.Add(panel);
            page.Controls.Add(bottom);
            return page;
        }

        private TextBox AddWxRow(Panel panel, string labelText, ref int y, int lx, int vx, int fw, int rh, ref int tabIndex)
        {
            var lbl = new Label { Text = labelText, Location = new Point(lx, y + 3), Size = new Size(vx - lx - 4, rh - 4), TabStop = false };
            var val = new TextBox
            {
                Location = new Point(vx, y + 1),
                Size = new Size(fw, rh - 4),
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Control,
                TabIndex = tabIndex++,
                AccessibleName = labelText.TrimEnd(':'),
            };
            panel.Controls.Add(lbl);
            panel.Controls.Add(val);
            y += rh;
            return val;
        }

        // Root cause of a live JAWS pass finding A-index/X-ray always reading "0.0"/"0.0e+0"
        // (looking like real-but-wrong measurements, not missing data): EngineHost's SPACE_WX
        // response used Nexus's own SpaceWx type verbatim, whose #[derive(Serialize)] emits
        // its Rust field names ("a_index"/"xray_long") rather than the camelCase
        // ("aIndex"/"xrayLong") JsonNamingPolicy.CamelCase looks for below -- System.Text.Json
        // doesn't throw on an unmatched property, it just leaves AIndex/XrayLong at C#'s
        // default float value (0.0), while Sfi/Kp/Ssn (no underscore, already camelCase-
        // equivalent) happened to match and came through correctly. Fixed on EngineHost's own
        // side (external_data.rs's new SpaceWxWire DTO) -- this method needed no change for
        // that part, but see below for what DID change: honest "Unavailable" text instead of a
        // misleading "--", and Nexus's own flare classification surfaced alongside X-ray.
        private void RefreshSpaceWeather()
        {
            var result = _client.GetSpaceWx(out string error);
            if (error != null || result?.Value == null)
            {
                _wxSfiValue.Text = _wxSsnValue.Text = _wxKpValue.Text = _wxAValue.Text = _wxXrayValue.Text = "Unavailable";
                _wxStatusLabel.Text = error ?? result?.LastError ?? "No data yet.";
                return;
            }

            var wx = result.Value;
            _wxSfiValue.Text = wx.Sfi.ToString("0.0");
            // Ssn is genuinely optional in Nexus's own model (no R12 feed currently wired --
            // "consumers derive it from SFI" per SpaceWx's own doc comment) -- "Unavailable" is
            // accurate here, not a zero standing in for a missing reading.
            _wxSsnValue.Text = wx.Ssn.HasValue ? wx.Ssn.Value.ToString("0.0") : "Unavailable";
            _wxKpValue.Text = wx.Kp.ToString("0.0");
            _wxAValue.Text = wx.AIndex.ToString("0.0");
            // NOAA flare-class letter + R-scale (radio-blackout risk, 0-5) are Nexus's own
            // existing classifications of this same raw reading (SpaceWx::xray_class()/
            // propagation::model::r_scale()), not a Jimmy Test interpretation -- surfaced
            // alongside the raw value since a bare "1.0e-7 W/m²" means little to most operators
            // on its own.
            string flareClass = string.IsNullOrEmpty(wx.XrayClass) ? "" : $" ({wx.XrayClass}-class, R{wx.RScale})";
            _wxXrayValue.Text = wx.XrayLong.ToString("0.0e+0") + " W/m²" + flareClass;

            _wxStatusLabel.Text = result.LastError != null
                ? $"Feed warning: {result.LastError}"
                : (result.AgeSecs != null ? $"As of {FormatAgeSecs(result.AgeSecs)}" : "");
        }
    }
}
