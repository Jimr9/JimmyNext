using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Accessible, keyboard-navigable Nexus-facts browser: POTA/SOTA spots, DX-cluster/RBN
    // spots, a plain-language band-conditions nowcast, and space weather -- each its own tab,
    // independently refreshable. Built entirely in code, no Designer.cs -- same convention as
    // LookupInfoDlg.cs. Every list is a plain single-column ListBox with each row pre-formatted
    // as one full text line (see MakeListBox's own comment: a multi-column ListView read fine in
    // JAWS but not NVDA -- a live-tested, still-open WinForms accessibility gap, live-NVDA
    // finding 2026-08-24) -- natively keyboard-navigable (arrow keys move between rows, Tab/
    // Shift+Tab between controls, full row text read by JAWS/NVDA out of the box) without any
    // custom accessibility infrastructure. TabControl itself is a standard WinForms control with
    // full built-in keyboard support (Ctrl+Tab / Ctrl+Shift+Tab between tabs, arrow keys within
    // the tab strip).
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
        // OtaSpotAnnotator only reads (HasWorkedBefore).
        private readonly ILogbookReader _logbookDb = LogbookFactory.Open();
        private readonly LookupManager _lookupManager;
        private readonly Func<System.Collections.Generic.Dictionary<string, WsjtxClient.ActiveAwardTag>> _activeAwardTags;
        private readonly Func<string> _currentBand;

        // Cache-only reads on EngineHost's side (see external_data.rs / live_feeds.rs) -- this
        // can poll faster than the underlying feeds refresh without wasting anything; matches
        // DirectPollIntervalMs's own "cheap, dedup handles the rest" reasoning.
        private const int RefreshIntervalMs = 15000;

        // Release-audit finding, 2026-08-20: guards against a second fetch for the SAME tab
        // starting while a slow one is still out (a mashed Refresh button, or a timer tick
        // landing mid-fetch) -- same reasoning as WsjtxClient.Direct.cs's _directPollInFlight.
        // See RefreshPotaSota's own comment for the full "why this exists at all" writeup.
        private bool _potaInFlight, _condInFlight, _dxInFlight, _wxInFlight;

        // ── POTA/SOTA tab ────────────────────────────────────────────────────────
        private ListBox _potaList;
        private Label _potaStatusLabel;
        private readonly List<OtaSpot> _potaSpots = new List<OtaSpot>();   // the spot behind each row

        // ── Band Conditions tab ─────────────────────────────────────────────────
        private TextBox _condHeadlineBox;
        private ListBox _condBandsList;
        private Label _condStatusLabel;

        // ── DX Spots tab ─────────────────────────────────────────────────────────
        private ListBox _dxList;
        private Label _dxStatusLabel;

        // ── Who Hears Me tab (2026-10-04) ────────────────────────────────────────
        private TextBox _heardSummaryBox;
        private ComboBox _heardWindowCb;
        private ListBox _heardList;
        private Label _heardStatusLabel;
        private bool _heardInFlight;

        // ── Contests tab (2026-10-05) ──────────────────────────────────────────
        private ListBox _contestList;
        private TextBox _contestStatusBox;
        private readonly List<ContestCalendarEvent> _contestRows = new List<ContestCalendarEvent>();
        private bool _contestInFlight;
        private string _contestRulesText = "";

        // ── Space Weather tab ────────────────────────────────────────────────────
        private TextBox _wxSfiValue, _wxSsnValue, _wxKpValue, _wxAValue, _wxXrayValue;
        private TextBox _wxMufValue, _wxGScaleValue, _wxSScaleValue;
        private TextBox _wxBzValue, _wxWindValue, _wxWindAgeValue;   // solar wind (2026-10-04)
        private ListBox _wxHistoryList;                               // daily solar history
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
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); if (e.KeyCode == Keys.F5) RefreshActiveTab(); };

            // No custom AccessibleName -- TabControl's default accessible behavior (JAWS
            // announces each TabPage's own Text as you switch) is exactly right here, same
            // convention as LogbookWindow.cs's own TabControl.
            _tabs = new TabControl
            {
                Dock = DockStyle.Fill,
            };
            _tabs.TabPages.Add(BuildPotaSotaTab());
            _tabs.TabPages.Add(BuildContestsTab());
            _tabs.TabPages.Add(BuildBandConditionsTab());
            _tabs.TabPages.Add(BuildDxSpotsTab());
            _tabs.TabPages.Add(BuildSpaceWeatherTab());
            _tabs.TabPages.Add(BuildWhoHearsMeTab());
            // Refresh only the tab the operator just switched to (see RefreshActiveTab's own
            // comment for why -- root cause of a live JAWS pass hearing DX Spots status text
            // while sitting on the Space Weather tab).
            _tabs.SelectedIndexChanged += (s, e) => RefreshActiveTab();
            Controls.Add(_tabs);

            _refreshTimer = new System.Windows.Forms.Timer { Interval = RefreshIntervalMs };
            _refreshTimer.Tick += (s, e) => RefreshActiveTab();

            Load += (s, e) => { RefreshActiveTab(); _refreshTimer.Start(); };
            FormClosed += (s, e) => { _refreshTimer.Stop(); _logbookDb?.Dispose(); };
        }

        // Root cause of a live JAWS pass hearing "500 spots -- last spot 0s ago -- add a DX
        // cluster server..." (DX Spots' own status text) while sitting on the Space Weather
        // tab: the periodic refresh used to call RefreshAll(), which updates EVERY tab's
        // Label.Text on every tick regardless of which TabPage is actually selected/visible.
        // Each control is correctly parented to its own TabPage only (verified -- no shared/
        // reused controls, no cross-tab Controls.Add, nothing overlapping) and WinForms
        // TabControl does set Visible=false on every non-selected page, so this was never a
        // parenting or focus bug. But assigning Label.Text still fires that Label's own
        // accessibility name-change notification whether or not its page is currently visible,
        // and JAWS can surface that notification regardless of visibility -- a background timer
        // silently narrating a hidden tab's data. The fix is the normal WinForms one: only ever
        // touch the controls on the currently selected page. Each individual tab still gets a
        // fresh read every RefreshIntervalMs (15s) while the operator is actually looking at
        // it, and immediately on switching to it -- EngineHost's own background feed threads
        // (PSK Reporter MQTT, RBN) keep running and keep their cache warm regardless, so nothing
        // goes stale server-side just because the UI stopped polling a tab nobody's looking at.
        private void RefreshActiveTab()
        {
            switch (_tabs.SelectedIndex)
            {
                case 0: RefreshPotaSota(); break;
                case 1: RefreshContests(false); break;
                case 2: RefreshBandConditions(); break;
                case 3: RefreshDxSpots(); break;
                case 4: RefreshSpaceWeather(); break;
                case 5: RefreshWhoHearsMe(); break;
            }
        }

        // Release-audit finding, 2026-08-20: marshals `action` onto this window's UI thread from
        // a RefreshXxx background fetch's continuation, silently doing nothing if the window has
        // already closed in the meantime (BeginInvoke throws on a disposed control's handle) --
        // shared so every RefreshXxx's background-fetch continuation below has the same safe
        // shape rather than four separate ad hoc try/catches.
        private void SafeBeginInvoke(Action action)
        {
            if (IsDisposed) return;
            try { BeginInvoke(action); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
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

        // Live JAWS-vs-NVDA finding, 2026-08-24: these three lists were originally a multi-
        // column ListView (View.Details) -- visually tidy, but WinForms ListView subitem/column
        // text has long-standing, still-unresolved gaps in its UI Automation accessibility
        // support (see https://github.com/dotnet/winforms/issues/3223). JAWS has its own legacy
        // SysListView32 fallback that reads every column regardless, which is why a live JAWS
        // pass read all 72 POTA/SOTA rows fine; NVDA (UIA-first) only ever read the first
        // column's text moving row to row ("17m", "12m", "20m", ..." for Band Conditions) --
        // a real, reported live-NVDA regression, not a one-off. A plain single-column ListBox
        // sidesteps the whole problem: each row's FULL text (every field, pre-formatted into one
        // line by the Refresh methods below) IS the item's only accessible text, so both screen
        // readers read the complete row the same way -- exactly the pattern Controller.cs's own
        // callListBox (the main station list, RowFormatter-built rows) already proves works.
        // Sighted users lose the aligned column grid; HorizontalScrollbar covers a row too wide
        // to fit rather than truncating it.
        // accessibleName is deliberately short ("Spots list"/"Bands list") -- the tab already
        // named the subject when it was switched to (e.g. "POTA / SOTA"), so repeating it here
        // is the redundancy a live JAWS pass flagged.
        private static ListBox MakeListBox(string accessibleName)
        {
            return new ListBox
            {
                Dock = DockStyle.Fill,
                HorizontalScrollbar = true,
                TabIndex = 0,
                AccessibleName = accessibleName,
            };
        }

        // Companion to MakeListBox's own comment: a ListBox's "current item" for keyboard/
        // screen-reader purposes is just SelectedIndex, but Items.Clear()+Add() (every Refresh
        // below) always resets it to -1, so the FIRST population after a tab/window opens would
        // otherwise leave nothing selected for a screen reader to land on. Selecting item 0 only
        // when nothing was already selected (captured via hadSelectionBeforeClear, taken before
        // Items.Clear() since Clear() wipes SelectedIndex) fixes that opening silence without
        // yanking an operator who already arrowed deeper into the list back to the top on a
        // routine 15s refresh. None of these lists have a click/Enter action, so selecting an
        // item has no side effect beyond the visual/accessible highlight.
        // internal (not private): JimmyTests exercises this directly (InternalsVisibleTo, see
        // AssemblyInfo.Testing.cs), same convention as FormatStatus/FormatNoaaScale above -- no
        // live EngineHost/network fetch needed to lock in the pure ListBox-state behavior.
        internal static void SelectFirstItemIfNoneSelectedYet(ListBox lb, bool hadSelectionBeforeClear)
        {
            if (!hadSelectionBeforeClear && lb.Items.Count > 0)
                lb.SelectedIndex = 0;
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
            _potaList = MakeListBox("Spots list");

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 30 };
            _potaStatusLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "Status", Text = "Loading..." };
            var refreshBtn = MakeRefreshButton((s, e) => RefreshPotaSota(), "Refresh POTA and SOTA spots now");
            refreshBtn.Dock = DockStyle.Right;
            // Optional (2026-10-04): the park the selected activator's NEXT logged contact is
            // credited to (SIG_INFO). Nothing is chosen for you, and logging never waits for it.
            var chooseBtn = new Button { Text = "Choose Park", AccessibleName = "Choose this park for the station", Size = new Size(100, 24), Dock = DockStyle.Right };
            chooseBtn.Click += (s, e) => ChooseSelectedPark();
            bottom.Controls.Add(_potaStatusLabel);
            bottom.Controls.Add(chooseBtn);
            bottom.Controls.Add(refreshBtn);

            page.Controls.Add(_potaList);
            page.Controls.Add(bottom);
            return page;
        }

        private void ChooseSelectedPark()
        {
            int i = _potaList.SelectedIndex;
            if (i < 0 || i >= _potaSpots.Count) { _potaStatusLabel.Text = "Select a spot first."; return; }
            var spot = _potaSpots[i];
            if (!string.Equals(spot.Program, "POTA", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(spot.Reference))
            {
                _potaStatusLabel.Text = "Only a POTA park can be chosen.";
                return;
            }
            ParkChoices.Choose(spot.Activator, spot.Reference, spot.Location);
            _potaStatusLabel.Text = $"Park {spot.Reference.Trim().ToUpperInvariant()} chosen for {spot.Activator}; it goes on that station's next logged contact.";
        }

        // Release-audit finding, 2026-08-20: this used to call _client.GetOtaSpots directly, ON
        // THE UI THREAD, justified as "cache-only local read on EngineHost's side, bounded ~3s
        // timeout, no internet round-trip on this thread". That's still true of what happens on
        // EngineHost's OWN side, but a bounded ~3s wait is still a real, user-visible UI freeze
        // here if EngineHost is ever slow to answer (or genuinely unresponsive, up to that 3s
        // bound) -- and this doesn't run just on a manual click, it also fires automatically
        // every RefreshIntervalMs (15s) via _refreshTimer, and on every switch to this tab. For a
        // JAWS/NVDA user that's a silent freeze with no feedback anything is happening. Fixed the
        // same way WsjtxClient.Direct.cs's DirectPollTick already was for the identical class of
        // problem: the network call runs on a background Task; only the (already-computed) UI
        // update is marshaled back, via SafeBeginInvoke.
        private void RefreshPotaSota()
        {
            if (_potaInFlight) return;
            _potaInFlight = true;
            string band = _currentBand?.Invoke();
            var tags = _activeAwardTags?.Invoke();
            System.Threading.Tasks.Task.Run(() =>
            {
                var result = _client.GetOtaSpots(out string error);
                SafeBeginInvoke(() =>
                {
                    _potaInFlight = false;
                    if (IsDisposed) return;

                    bool hadSelection = _potaList.SelectedIndex >= 0;
                    _potaList.BeginUpdate();
                    try
                    {
                        _potaList.Items.Clear();
                        _potaSpots.Clear();
                        if (result?.Spots != null)
                        {
                            foreach (var spot in result.Spots)
                            {
                                var annotation = OtaSpotAnnotator.Annotate(spot.Activator, band, _logbookDb, _lookupManager, tags);
                                _potaList.Items.Add(FormatPotaSotaRow(spot, annotation));
                                _potaSpots.Add(spot);
                            }
                        }
                    }
                    finally
                    {
                        _potaList.EndUpdate();
                    }
                    SelectFirstItemIfNoneSelectedYet(_potaList, hadSelection);

                    if (error != null)
                        _potaStatusLabel.Text = $"{_potaList.Items.Count} spots (stale) -- {error}";
                    else if (result?.LastError != null)
                        _potaStatusLabel.Text = $"{_potaList.Items.Count} spots -- feed warning: {result.LastError}";
                    else
                        _potaStatusLabel.Text = $"{_potaList.Items.Count} spots" + (result?.AgeSecs != null ? $" -- as of {result.AgeSecs}s ago" : "");
                });
            });
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
            // Logbook migration: while the Nexus logbook is loading, worked-before is unknown.
            if (!NexusLogbook.LogReady) return "log loading";
            if (a.NeededForAwardCount > 0)
                return $"needed for {a.NeededForAwardCount} award{(a.NeededForAwardCount == 1 ? "" : "s")}";
            return a.WorkedBefore ? "worked" : "not worked";
        }

        // One line per spot, every field labeled -- the exact wording a live JAWS pass on this
        // window already read out loud correctly (see MakeListBox's own comment for why this
        // replaced a multi-column ListView). internal (not private): JimmyTests exercises this
        // directly, same convention as FormatStatus above.
        internal static string FormatPotaSotaRow(OtaSpot spot, OtaSpotAnnotation annotation)
        {
            return $"{spot.Program}, Reference: {spot.Reference}, Activator: {spot.Activator}, " +
                $"Freq/Mode: {spot.FreqKhz / 1000.0:0.000} {spot.Mode}, Age: {FormatAge(spot.SpotTimeUnix)}, " +
                $"Status: {FormatStatus(annotation)}";
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

            _condBandsList = MakeListBox("Bands list");

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

        // One line per band, every field labeled -- see MakeListBox's own comment for why this
        // replaced a multi-column ListView. Folds in the "modeled" detail that used to be
        // ListView-tooltip-only (mouse-hover, never reachable by keyboard/screen reader at all)
        // since it's already computed and free to include now that the whole row is one string.
        // internal (not private): JimmyTests exercises this directly.
        internal static string FormatBandConditionsRow(BandReport b)
        {
            string hearMe = $"{b.NHearMe} / {b.NIHear}";
            string bestRegion = b.BestRegion != null
                ? $"{b.BestRegion.Region} ({b.BestRegion.Octant}, {b.BestRegion.Stations} stn{(b.BestRegion.Stations == 1 ? "" : "s")})"
                : "--";
            return $"{b.Band}: {b.Tier}, {b.Confidence} confidence, Hear Me/I Hear: {hearMe}, " +
                $"Best Region: {bestRegion}, Reason: {b.Reason} (modeled: {b.Modeled} -- {b.ModeledReason})";
        }

        // Release-audit finding, 2026-08-20: moved off the UI thread -- see RefreshPotaSota's
        // own comment for the full reasoning (identical shape/fix here).
        private void RefreshBandConditions()
        {
            if (_condInFlight) return;
            _condInFlight = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                var result = _client.GetBandConditions(out string error);
                SafeBeginInvoke(() =>
                {
                    _condInFlight = false;
                    if (IsDisposed) return;

                    bool hadSelection = _condBandsList.SelectedIndex >= 0;
                    _condBandsList.BeginUpdate();
                    try
                    {
                        _condBandsList.Items.Clear();
                        if (result?.Bands != null)
                        {
                            foreach (var b in result.Bands)
                                _condBandsList.Items.Add(FormatBandConditionsRow(b));
                        }
                    }
                    finally
                    {
                        _condBandsList.EndUpdate();
                    }
                    SelectFirstItemIfNoneSelectedYet(_condBandsList, hadSelection);

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
                        // Bands are always populated now (EngineHost's PropAdvisor falls back to
                        // a physics-only model when there are no PSK Reporter reception reports
                        // yet -- see live_feeds.rs's band_conditions_json), so 0 reports no
                        // longer means an empty tab. Call that out explicitly rather than leaving
                        // the operator to guess whether the ladder below is observed or modeled.
                        string modeledOnly = (result?.SpotCount ?? 0) == 0
                            ? " -- modeled only, no reception reports yet"
                            : "";
                        _condStatusLabel.Text = $"{result?.SpotCount ?? 0} reception reports, {connected}" +
                            (result?.LastEventAgeSecs != null ? $", last report {FormatAgeSecs(result.LastEventAgeSecs)}" : "") +
                            modeledOnly + banners;
                    }
                });
            });
        }

        // ── Who Hears Me tab ─────────────────────────────────────────────────────
        // Every station PSK Reporter says decoded you, newest report per station, furthest first
        // (operator, 2026-10-04) -- the same feed the band advice counts, listed one by one.

        // ── Contests tab: the WA7BNM calendar, read by Nexus's own adapter in EngineHost ──
        // A calendar listing is not support: a row says what Jimmy can do for it only when Jimmy
        // has that contest (ContestSupportLevels); every other row is a listing to read.

        private TabPage BuildContestsTab()
        {
            var page = MakeTabPage("Contests");

            _contestStatusBox = new TextBox
            {
                Dock = DockStyle.Top,
                Height = 64,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Control,
                TabStop = true,
                TabIndex = 1,
                AccessibleName = "Calendar status",
                Text = "Loading...",
            };

            _contestList = MakeListBox("Contests list");
            _contestList.TabIndex = 0;
            _contestList.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { OpenContestDetails(); e.Handled = true; e.SuppressKeyPress = true; } };

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            var detailsBtn = new Button { Text = "Open &Details", AccessibleName = "Open details", AutoSize = true, TabIndex = 2 };
            detailsBtn.Click += (s, e) => OpenContestDetails();
            var refreshBtn = new Button { Text = "&Refresh Calendar", AccessibleName = "Refresh calendar", AutoSize = true, TabIndex = 3 };
            refreshBtn.Click += (s, e) => RefreshContests(true);
            var rulesBtn = new Button { Text = "Check for Rules &Updates", AccessibleName = "Check for rules updates", AutoSize = true, TabIndex = 4 };
            rulesBtn.Click += (s, e) => CheckContestRules();
            bottom.Controls.Add(detailsBtn);
            bottom.Controls.Add(refreshBtn);
            bottom.Controls.Add(rulesBtn);

            page.Controls.Add(_contestList);
            page.Controls.Add(_contestStatusBox);
            page.Controls.Add(bottom);
            return page;
        }

        // The calendar's names for the contests Jimmy has a ruleset for, by Nexus event id.
        private static readonly (string Match, string EventId)[] CalendarEventIds =
        {
            ("ARRL Field Day", "arrlfd"), ("Winter Field Day", "wfd"),
            ("ARRL January VHF", "arrlvhf_jan"), ("ARRL June VHF", "arrlvhf_jun"), ("ARRL September VHF", "arrlvhf_sep"),
            ("Tennessee QSO Party", "tnqp"), ("Ohio QSO Party", "ohqp"), ("California QSO Party", "cqp"),
            ("Texas QSO Party", "txqp"), ("Illinois QSO Party", "ilqp"), ("New York QSO Party", "nyqp"),
            ("ARRL Sweepstakes, CW", "arrlss_cw"), ("ARRL Sweepstakes, SSB", "arrlss_ssb"),
            ("CQ Worldwide DX Contest, CW", "cqww_cw"), ("CQ Worldwide DX Contest, SSB", "cqww_ssb"), ("CQ Worldwide DX Contest, RTTY", "cqww_rtty"),
            ("CQ WW WPX Contest, CW", "cqwpx_cw"), ("CQ WW WPX Contest, SSB", "cqwpx_ssb"),
        };

        // What Jimmy can do for a calendar listing, or null when it is only a listing.
        internal static string JimmySupportFor(string calendarName)
        {
            string n = calendarName ?? "";
            if (n.IndexOf("International Digital", StringComparison.OrdinalIgnoreCase) >= 0) return "not supported yet";
            foreach (var (match, id) in CalendarEventIds)
                if (n.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0)
                    return ContestSupportLevels.Label(ContestSupportLevels.Get(id, eventIdKnownToNexus: true)).ToLowerInvariant();
            return null;
        }

        // One row: "On now: ARRL September VHF Contest, Sat 2026-09-12 13:00 to Sun 2026-09-13 21:59 CDT,
        // Jimmy: available in nexus, not yet verified in jimmy". internal: JimmyTests exercises it.
        internal static string FormatContestRow(ContestCalendarEvent ev, DateTime nowUtc, TimeZoneInfo zone)
        {
            var start = DateTimeOffset.FromUnixTimeSeconds(ev.StartUnix).UtcDateTime;
            var end = DateTimeOffset.FromUnixTimeSeconds(ev.EndUnix).UtcDateTime;
            string onNow = start <= nowUtc && nowUtc < end ? "On now: " : "";
            string support = JimmySupportFor(ev.Name);
            return $"{onNow}{ev.Name}, {DisplayTime.RangeText(start, end, zone)}{(support != null ? ", Jimmy: " + support : "")}";
        }

        // The calendar's age: older than this is said to be possibly out of date.
        internal static readonly TimeSpan CalendarStaleAfter = TimeSpan.FromHours(2);

        internal static string CalendarStatusText(ContestCalendarResult r, string transportError, DateTime nowUtc, TimeZoneInfo zone)
        {
            string When(long unix) { var t = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime; return $"{DisplayTime.DateTimeText(t, zone)} {DisplayTime.Abbreviation(t, zone)}"; }
            string problem = transportError ?? r?.Error;
            if (r?.FetchedUnix == null)
                return problem != null ? $"Contest calendar unavailable: {problem}" : "Contest calendar not read yet.";
            var age = nowUtc - DateTimeOffset.FromUnixTimeSeconds(r.FetchedUnix.Value).UtcDateTime;
            string read = $"Calendar read {When(r.FetchedUnix.Value)}";
            if (problem != null) read = $"Could not refresh the calendar ({problem}). Showing the calendar read {When(r.FetchedUnix.Value)}";
            if (age > CalendarStaleAfter) read += ", which may be out of date";
            return $"{read}. {DisplayTime.ZoneSentence(nowUtc, zone)} Rows without a Jimmy note are calendar listings only.";
        }

        internal static string RulesStatusText(ContestRulesStatus s, DateTime nowUtc, TimeZoneInfo zone)
        {
            if (s == null) return "";
            string Day(string generated) => string.IsNullOrEmpty(generated) ? "unknown" : generated.Length >= 10 ? generated.Substring(0, 10) : generated;
            bool bundled = string.IsNullOrEmpty(s.DownloadedGenerated) || s.ActiveGenerated == s.BundledGenerated;
            string text = $"Contest rules in use: rules year {s.RulesYear}, version {Day(s.ActiveGenerated)}{(bundled ? " (bundled)" : " (downloaded)")}.";
            if (s.CheckedUnix > 0)
            {
                var t = DateTimeOffset.FromUnixTimeSeconds(s.CheckedUnix).UtcDateTime;
                text += $" Last checked {DisplayTime.DateTimeText(t, zone)} {DisplayTime.Abbreviation(t, zone)}.";
            }
            if (s.WaitingForRestart)
                text += $" Version {Day(s.DownloadedGenerated)} is downloaded and applies the next time Jimmy Next starts with no contest running.";
            return text;
        }

        private void RefreshContests(bool refreshNow)
        {
            if (_contestInFlight) return;
            _contestInFlight = true;
            if (refreshNow) _contestStatusBox.Text = "Refreshing the contest calendar...";
            System.Threading.Tasks.Task.Run(() =>
            {
                var result = _client.GetContestCalendar(refreshNow, out string error);
                var rules = _client.GetContestRulesStatus(out _);
                SafeBeginInvoke(() =>
                {
                    _contestInFlight = false;
                    if (IsDisposed) return;
                    var now = DateTime.UtcNow;
                    var zone = DisplayTime.Zone;
                    if (rules != null) _contestRulesText = RulesStatusText(rules, now, zone);
                    var upcoming = (result?.Events ?? new List<ContestCalendarEvent>())
                        .Where(e => DateTimeOffset.FromUnixTimeSeconds(e.EndUnix).UtcDateTime > now)
                        .OrderBy(e => e.StartUnix).ToList();
                    var rows = upcoming.Select(e => FormatContestRow(e, now, zone)).ToList();
                    // Rebuilt only when something changed, so a screen reader is not interrupted.
                    if (!rows.SequenceEqual(_contestList.Items.Cast<string>()))
                    {
                        int keep = _contestList.SelectedIndex;
                        _contestList.BeginUpdate();
                        try
                        {
                            _contestList.Items.Clear();
                            _contestRows.Clear();
                            foreach (var (row, ev) in rows.Zip(upcoming, (r, e) => (r, e))) { _contestList.Items.Add(row); _contestRows.Add(ev); }
                        }
                        finally { _contestList.EndUpdate(); }
                        if (_contestList.Items.Count > 0) _contestList.SelectedIndex = Math.Max(0, Math.Min(keep, _contestList.Items.Count - 1));
                    }
                    string status = CalendarStatusText(result, error, now, zone) + (_contestRulesText.Length > 0 ? Environment.NewLine + _contestRulesText : "");
                    if (_contestStatusBox.Text != status) _contestStatusBox.Text = status;
                });
            });
        }

        private void OpenContestDetails()
        {
            int i = _contestList.SelectedIndex;
            if (i < 0 || i >= _contestRows.Count) { _contestStatusBox.Text = "Select a contest first."; return; }
            string url = _contestRows[i].Url;
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                _contestStatusBox.Text = "This contest has no details link.";
                return;
            }
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { _contestStatusBox.Text = $"Could not open the details page: {ex.Message}"; }
        }

        private void CheckContestRules()
        {
            _contestStatusBox.Text = "Checking for contest rules updates...";
            System.Threading.Tasks.Task.Run(() =>
            {
                var before = _client.GetContestRulesStatus(out _);
                var after = _client.CheckContestRules(out string error);
                SafeBeginInvoke(() =>
                {
                    if (IsDisposed) return;
                    var now = DateTime.UtcNow;
                    string msg;
                    if (error != null) msg = $"Rules check failed: {error}";
                    else if (after.WaitingForRestart && after.DownloadedGenerated != before?.DownloadedGenerated)
                        msg = "Newer contest rules downloaded. They apply the next time Jimmy Next starts with no contest running.";
                    else if (after.WaitingForRestart) msg = "Newer contest rules are already downloaded and waiting for the next start.";
                    else msg = "Contest rules are up to date.";
                    if (after != null) _contestRulesText = RulesStatusText(after, now, DisplayTime.Zone);
                    _contestStatusBox.Text = msg + (_contestRulesText.Length > 0 ? Environment.NewLine + _contestRulesText : "");
                    // The answer to the operator's own button press: said once, focus comes back to the button.
                    MessageBox.Show(this, msg, "Contest rules", MessageBoxButtons.OK, error != null ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                });
            });
        }

        private TabPage BuildWhoHearsMeTab()
        {
            var page = MakeTabPage("Who Hears Me");

            var top = new Panel { Dock = DockStyle.Top, Height = 30 };
            _heardWindowCb = new ComboBox
            {
                Dock = DockStyle.Right,
                Width = 140,
                DropDownStyle = ComboBoxStyle.DropDownList,
                AccessibleName = "Time window",
                TabIndex = 1,
            };
            _heardWindowCb.Items.AddRange(new object[] { "Last 15 minutes", "Last 30 minutes" });
            _heardWindowCb.SelectedIndex = 0;
            _heardWindowCb.SelectedIndexChanged += (s, e) => RefreshWhoHearsMe();
            _heardSummaryBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Control,
                TabStop = true,
                TabIndex = 0,
                AccessibleName = "Summary",
                Text = "Loading...",
            };
            top.Controls.Add(_heardSummaryBox);
            top.Controls.Add(_heardWindowCb);

            _heardList = MakeListBox("Stations list");
            _heardList.TabIndex = 2;

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 30 };
            _heardStatusLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "Status", Text = "Loading..." };
            var refreshBtn = MakeRefreshButton((s, e) => RefreshWhoHearsMe(), "Refresh who hears me now");
            refreshBtn.Dock = DockStyle.Right;
            bottom.Controls.Add(_heardStatusLabel);
            bottom.Controls.Add(refreshBtn);

            page.Controls.Add(_heardList);
            page.Controls.Add(top);
            page.Controls.Add(bottom);
            return page;
        }

        // One line per receiving station, every field labeled where it isn't obvious. An SNR the
        // report didn't carry, and a distance without the station's grid, are said as unknown --
        // never shown as 0. internal: JimmyTests exercises it.
        internal static string FormatHeardMeRow(HeardMe h)
        {
            string grid = string.IsNullOrEmpty(h.Grid) ? "" : $" {h.Grid}";
            string snr = h.Snr.HasValue ? $"{h.Snr.Value:+0;-0;0} dB" : "SNR not reported";
            string where = string.IsNullOrEmpty(h.Grid) ? "distance unknown" : $"{h.Km:N0} km {h.Octant}";
            return $"{h.Call}{grid}, {h.Band}, {snr}, {where}, {FormatAgeSecs(h.AgeSecs)}";
        }

        internal static string FormatHeardMeSummary(GettingOutResult r)
        {
            string window = $"in the last {r.WindowMinutes} minutes";
            if (r.Count == 0) return $"No reports of your signal {window}.";
            string who = r.Count == 1 ? "1 station heard you" : $"{r.Count} stations heard you";
            return r.MaxKm > 0 ? $"{who} {window}, furthest {r.MaxKm:N0} km." : $"{who} {window}.";
        }

        private void RefreshWhoHearsMe()
        {
            if (_heardInFlight) return;
            _heardInFlight = true;
            int minutes = _heardWindowCb.SelectedIndex == 1 ? 30 : 15;
            System.Threading.Tasks.Task.Run(() =>
            {
                var result = _client.GetGettingOut(minutes, out string error);
                SafeBeginInvoke(() =>
                {
                    _heardInFlight = false;
                    if (IsDisposed) return;

                    bool hadSelection = _heardList.SelectedIndex >= 0;
                    _heardList.BeginUpdate();
                    try
                    {
                        _heardList.Items.Clear();
                        if (error == null && result?.Reports != null)
                            foreach (var h in result.Reports) _heardList.Items.Add(FormatHeardMeRow(h));
                    }
                    finally
                    {
                        _heardList.EndUpdate();
                    }
                    SelectFirstItemIfNoneSelectedYet(_heardList, hadSelection);

                    if (error != null)
                    {
                        _heardSummaryBox.Text = "";
                        _heardStatusLabel.Text = $"(stale) -- {error}";
                    }
                    else if (result?.Error != null)
                    {
                        _heardSummaryBox.Text = "";
                        _heardStatusLabel.Text = result.Error;
                    }
                    else
                    {
                        _heardSummaryBox.Text = FormatHeardMeSummary(result);
                        string connected = result.Connected ? "PSK Reporter connected" : "PSK Reporter not connected";
                        string last = result.LastEventAgeSecs != null ? $", last report {FormatAgeSecs(result.LastEventAgeSecs)}" : ", no reports yet";
                        string covered = result.CoveredMinutes != null ? $" -- reports kept cover only the last {result.CoveredMinutes} minutes" : "";
                        _heardStatusLabel.Text = connected + last + covered;
                    }
                });
            });
        }

        // ── DX Spots tab ─────────────────────────────────────────────────────────

        private TabPage BuildDxSpotsTab()
        {
            var page = MakeTabPage("DX Spots");
            _dxList = MakeListBox("Spots list");

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

        // One line per spot, every field labeled -- see MakeListBox's own comment for why this
        // replaced a multi-column ListView. internal (not private): JimmyTests exercises this
        // directly.
        internal static string FormatDxSpotRow(DxSpot s)
        {
            string mode = s.Rbn ? (s.SkimmerMode ?? "RBN") : "";
            return $"DX Call: {s.DxCall}, Frequency: {s.FreqKhz / 1000.0:0.000} MHz, Mode: {mode}, " +
                $"Spotter: {s.Spotter}, Age: {FormatAgeSecs(s.AgeSecs)}, Comment: {s.Comment}";
        }

        // Release-audit finding, 2026-08-20: moved off the UI thread -- see RefreshPotaSota's
        // own comment for the full reasoning (identical shape/fix here).
        private void RefreshDxSpots()
        {
            if (_dxInFlight) return;
            _dxInFlight = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                var result = _client.GetDxSpots(out string error);
                SafeBeginInvoke(() =>
                {
                    _dxInFlight = false;
                    if (IsDisposed) return;

                    bool hadSelection = _dxList.SelectedIndex >= 0;
                    _dxList.BeginUpdate();
                    try
                    {
                        _dxList.Items.Clear();
                        if (result?.Spots != null)
                        {
                            foreach (var s in result.Spots)
                                _dxList.Items.Add(FormatDxSpotRow(s));
                        }
                    }
                    finally
                    {
                        _dxList.EndUpdate();
                    }
                    SelectFirstItemIfNoneSelectedYet(_dxList, hadSelection);

                    if (error != null)
                    {
                        _dxStatusLabel.Text = $"{_dxList.Items.Count} spots (stale) -- {error}";
                    }
                    // The reverse beacon network (RBN) digital skimmer feed is always on and
                    // needs no configuration -- this used to read "No DX cluster server
                    // configured", which was both wrong (spots show up with nothing configured)
                    // and left the operator staring at an empty list with no idea it would ever
                    // fill in. "Connected" not yet being true here just means the RBN session
                    // hasn't come up yet.
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
                        // DxSpotsResult's own comment) -- surfaced as a one-line opt-in tip, not
                        // an error, since adding a human cluster node only ADDS coverage
                        // (SSB/phone) it doesn't unlock something otherwise broken.
                        string tip = (result != null && !result.Configured)
                            ? " -- add a DX cluster server in Options > Decode Engine for SSB/phone spots too"
                            : "";
                        _dxStatusLabel.Text = $"{_dxList.Items.Count} spots" +
                            (result?.LastEventAgeSecs != null ? $" -- last spot {FormatAgeSecs(result.LastEventAgeSecs)}" : "") +
                            tip;
                    }
                });
            });
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
            // Three additions Nexus already computes/fetches but Jimmy Next wasn't surfacing
            // (investigated 2026-08-17 -- see RefreshSpaceWeather's own comment for exactly what
            // each one is and isn't): a representative long-haul MUF, and NOAA's own G
            // (geomagnetic storm) and S (solar radiation storm) scales. NOAA's R (radio
            // blackout) scale is deliberately not duplicated -- X-ray flux above already carries
            // it (same NOAA definition, same raw reading).
            _wxMufValue = AddWxRow(panel, "Representative MUF (best long-haul):", ref y, lx, vx, fw, rh, ref tabIndex);
            _wxGScaleValue = AddWxRow(panel, "Geomagnetic storm (G-scale):", ref y, lx, vx, fw, rh, ref tabIndex);
            _wxSScaleValue = AddWxRow(panel, "Solar radiation storm (S-scale):", ref y, lx, vx, fw, rh, ref tabIndex);
            // Solar wind from NOAA's DSCOVR feed, through Nexus's own fetcher (2026-10-04). The
            // reading's own age is shown: a fetch that just worked can still carry an old reading.
            _wxBzValue = AddWxRow(panel, "Solar wind Bz:", ref y, lx, vx, fw + 160, rh, ref tabIndex);
            _wxWindValue = AddWxRow(panel, "Solar wind speed and density:", ref y, lx, vx, fw + 160, rh, ref tabIndex);
            _wxWindAgeValue = AddWxRow(panel, "Solar wind reading:", ref y, lx, vx, fw + 160, rh, ref tabIndex);
            // NOAA's daily solar indices, newest day first, one line per day.
            panel.AutoScroll = true;
            panel.Controls.Add(new Label { Text = "Daily solar history (newest first):", Location = new Point(lx, y + 6), AutoSize = true, TabStop = false });
            y += rh;
            _wxHistoryList = new ListBox
            {
                Location = new Point(lx, y + 2),
                Size = new Size(vx + fw + 160 - lx, 120),
                HorizontalScrollbar = true,
                IntegralHeight = false,
                TabIndex = tabIndex++,
                AccessibleName = "Daily solar history",
            };
            panel.Controls.Add(_wxHistoryList);

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
        // Release-audit finding, 2026-08-20: moved off the UI thread -- see RefreshPotaSota's
        // own comment for the full reasoning (identical shape/fix here).
        private void RefreshSpaceWeather()
        {
            if (_wxInFlight) return;
            _wxInFlight = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                var result = _client.GetSpaceWx(out string error);
                var wind = _client.GetSolarWind(out string windError);
                var history = _client.GetSolarHistory(out string historyError);
                SafeBeginInvoke(() =>
                {
                    _wxInFlight = false;
                    if (IsDisposed) return;
                    ShowSolarWind(wind, windError);
                    ShowSolarHistory(history, historyError);

                    if (error != null || result?.Value == null)
                    {
                        _wxSfiValue.Text = _wxSsnValue.Text = _wxKpValue.Text = _wxAValue.Text = _wxXrayValue.Text = "Unavailable";
                        _wxMufValue.Text = _wxGScaleValue.Text = _wxSScaleValue.Text = "Unavailable";
                        _wxStatusLabel.Text = error ?? result?.LastError ?? "No data yet.";
                        return;
                    }

                    var wx = result.Value;
                    _wxSfiValue.Text = wx.Sfi.ToString("0.0");
                    // Ssn is genuinely optional in Nexus's own model (no R12 feed currently
                    // wired -- "consumers derive it from SFI" per SpaceWx's own doc comment) --
                    // "Unavailable" is accurate here, not a zero standing in for a missing
                    // reading.
                    _wxSsnValue.Text = wx.Ssn.HasValue ? wx.Ssn.Value.ToString("0.0") : "Unavailable";
                    _wxKpValue.Text = wx.Kp.ToString("0.0");
                    _wxAValue.Text = wx.AIndex.ToString("0.0");
                    // NOAA flare-class letter + R-scale (radio-blackout risk, 0-5) are Nexus's
                    // own existing classifications of this same raw reading (SpaceWx::
                    // xray_class()/propagation::model::r_scale()), not a Jimmy Next
                    // interpretation -- surfaced alongside the raw value since a bare
                    // "1.0e-7 W/m²" means little to most operators on its own.
                    string flareClass = string.IsNullOrEmpty(wx.XrayClass) ? "" : $" ({wx.XrayClass}-class, R{wx.RScale})";
                    _wxXrayValue.Text = wx.XrayLong.ToString("0.0e+0") + " W/m²" + flareClass;

                    // Nexus's own representative MUF: the ring-max controlling MUF over 8
                    // evenly-spaced long-haul (~9000 km) directions from the operator's own grid
                    // -- NOT a specific DX path, and NOT an observed reading; it's a classical
                    // foF2 x obliquity model driven by the same SFI above (propagation::
                    // predict::representative_muf, investigated 2026-08-17). The row LABEL
                    // ("best long-haul") carries that caveat, so the value itself stays a plain
                    // number -- kept concise for JAWS rather than repeating the caveat on every
                    // read. Null (not zero) when the operator's grid isn't set/valid.
                    _wxMufValue.Text = result.MufNow.HasValue
                        ? $"{result.MufNow.Value:0.0} MHz"
                        : "Unavailable (My Grid not set)";

                    // NOAA's own G/S scales (a separate SWPC product, fetched independently --
                    // see NoaaScales's own comment for why R isn't duplicated here).
                    // Scales==null on a fetch that hasn't succeeded yet is reported plainly, not
                    // as a numeric 0 that would read as a real "all quiet" measurement.
                    if (result.Scales != null)
                    {
                        _wxGScaleValue.Text = FormatNoaaScale('G', result.Scales.GScale) +
                            (result.Scales.GScaleTomorrow != result.Scales.GScale
                                ? $" -- tomorrow {FormatNoaaScale('G', result.Scales.GScaleTomorrow)}"
                                : "");
                        _wxSScaleValue.Text = FormatNoaaScale('S', result.Scales.SScale);
                    }
                    else
                    {
                        _wxGScaleValue.Text = _wxSScaleValue.Text = result.ScalesLastError != null
                            ? "Unavailable"
                            : "Loading...";
                    }

                    _wxStatusLabel.Text = result.LastError != null
                        ? $"Feed warning: {result.LastError}"
                        : (result.AgeSecs != null ? $"As of {FormatAgeSecs(result.AgeSecs)}" : "");
                });
            });
        }

        private void ShowSolarWind(SolarWindResult w, string error)
        {
            var (bz, wind, age) = FormatSolarWind(w, error);
            _wxBzValue.Text = bz;
            _wxWindValue.Text = wind;
            _wxWindAgeValue.Text = age;
        }

        // Bz with its direction (southward -- negative -- is the one that disturbs the field);
        // speed/density only when NOAA gave them; the reading's own age, flagged when Nexus calls
        // it stale. internal: JimmyTests exercises it.
        internal static (string bz, string wind, string age) FormatSolarWind(SolarWindResult w, string error)
        {
            if (error != null || w == null || w.BzNt == null)
                return ("Unavailable", "Unavailable", error ?? w?.Error ?? "No reading yet");
            float bz = w.BzNt.Value;
            string dir = bz < 0 ? "southward" : bz > 0 ? "northward" : "neutral";
            string bt = w.BtNt.HasValue ? $", total field {w.BtNt.Value:0.0} nT" : "";
            string speed = w.SpeedKms.HasValue ? $"{w.SpeedKms.Value:0} km/s" : "speed not known";
            string density = w.Density.HasValue ? $", {w.Density.Value:0.0} protons per cm³" : ", density not known";
            string age = w.MeasuredAgeSecs.HasValue ? $"measured {FormatAgeSecs(w.MeasuredAgeSecs)}" : "measurement time not known";
            if (w.Stale) age += " -- an old reading, not current conditions";
            return ($"{bz:+0.0;-0.0;0.0} nT ({dir}){bt}", speed + density, age);
        }

        private void ShowSolarHistory(SolarHistoryResult h, string error)
        {
            bool hadSelection = _wxHistoryList.SelectedIndex >= 0;
            var rows = FormatSolarHistory(h, error);
            bool same = rows.Count == _wxHistoryList.Items.Count;
            for (int i = 0; same && i < rows.Count; i++) same = (string)_wxHistoryList.Items[i] == rows[i];
            if (same) return;   // unchanged: leave the list (and the reader's place in it) alone
            _wxHistoryList.BeginUpdate();
            try
            {
                _wxHistoryList.Items.Clear();
                foreach (var r in rows) _wxHistoryList.Items.Add(r);
            }
            finally { _wxHistoryList.EndUpdate(); }
            SelectFirstItemIfNoneSelectedYet(_wxHistoryList, hadSelection);
        }

        internal static System.Collections.Generic.List<string> FormatSolarHistory(SolarHistoryResult h, string error)
        {
            var rows = new System.Collections.Generic.List<string>();
            if (error != null || h == null) { rows.Add("Unavailable: " + (error ?? "no data")); return rows; }
            if (h.Days == null || h.Days.Length == 0) { rows.Add(h.Error != null ? "Unavailable: " + h.Error : "No data yet"); return rows; }
            for (int i = h.Days.Length - 1; i >= 0; i--)
            {
                var d = h.Days[i];
                string date = DateTimeOffset.FromUnixTimeSeconds(d.DayUnix).UtcDateTime.ToString("yyyy-MM-dd");
                string sfi = d.Sfi.HasValue ? $"solar flux {d.Sfi.Value:0}" : "solar flux not reported";
                string ssn = d.Ssn.HasValue ? $"sunspots {d.Ssn.Value:0}" : "sunspots not reported";
                rows.Add($"{date}: {sfi}, {ssn}");
            }
            return rows;
        }

        // NOAA's own standard descriptor words for its 0-5 R/S/G scales (public, standard across
        // all three -- e.g. https://www.swpc.noaa.gov/noaa-scales-explanation), reused as-is
        // rather than invented: level 0 has no official NOAA word (it's simply "none of the
        // above"), shown here as "Quiet"/"None" per scale for a concise, honest label.
        // internal (not private): JimmyTests exercises this directly, same convention as
        // FormatStatus above (InternalsVisibleTo, see AssemblyInfo.Testing.cs).
        internal static string FormatNoaaScale(char scale, int level)
        {
            string word;
            switch (level)
            {
                case 1: word = "Minor"; break;
                case 2: word = "Moderate"; break;
                case 3: word = "Strong"; break;
                case 4: word = "Severe"; break;
                case 5: word = "Extreme"; break;
                default: word = scale == 'G' ? "Quiet" : "None"; break;
            }
            return $"{scale}{level} - {word}";
        }
    }
}
