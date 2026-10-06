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
    // custom accessibility infrastructure. Pages are chosen from a category list, as in Logbook
    // Center and Options (2026-10-06; it was a TabControl, Ctrl+Tab between tabs).
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
        // Same arrangement as Logbook Center and Options (operator, 2026-10-06): a category list,
        // one page shown at a time, each page's Tab order given explicitly (_pageOrder, used by
        // ProcessTabKey), and a Close button last.
        private readonly ListBox _categoryListBox;
        private readonly Panel _host;
        private readonly Button _closeBtn;
        private readonly Panel[] _pages;
        private readonly Dictionary<Panel, Control[]> _pageOrder = new Dictionary<Panel, Control[]>();
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
        private TextBox _potaStatusLabel;
        private readonly List<OtaSpot> _potaSpots = new List<OtaSpot>();   // the spot behind each row

        // ── Band Conditions tab ─────────────────────────────────────────────────
        private TextBox _condHeadlineBox;
        private ListBox _condBandsList;
        private TextBox _condStatusLabel;

        // ── DX Spots tab ─────────────────────────────────────────────────────────
        private ListBox _dxList;
        private TextBox _dxStatusLabel;

        // ── Who Hears Me tab (2026-10-04) ────────────────────────────────────────
        private TextBox _heardSummaryBox;
        private ComboBox _heardWindowCb;
        private ListBox _heardList;
        private TextBox _heardStatusLabel;
        private bool _heardInFlight;

        // ── Contests tab (2026-10-05) ──────────────────────────────────────────
        private ListBox _contestList;
        private TextBox _contestStatusBox;
        private readonly List<ContestCalendarEvent> _contestRows = new List<ContestCalendarEvent>();
        private bool _contestInFlight;
        private string _contestRulesText = "";

        // ── Space Weather tab ────────────────────────────────────────────────────
        private ListBox _wxReadingsList;   // one plain-language line per reading (2026-10-06)
        private ListBox _wxHistoryList;    // daily solar history
        private TextBox _wxStatusLabel;

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
            Text = "Spots and Conditions";   // "and", not "&", which screen readers skip (2026-10-06)
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            MinimumSize = new Size(600, 340);
            Size = new Size(820, 460);
            Font = new Font("Microsoft Sans Serif", 9F);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); if (e.KeyCode == Keys.F5) RefreshActiveTab(); };

            // The window's own Close button, last in every page's Tab order.
            var closePanel = new Panel { Dock = DockStyle.Bottom, Height = 30, AccessibleName = "", AccessibleRole = AccessibleRole.None };
            _closeBtn = new Button { Text = "Close", Dock = DockStyle.Right, Width = 70, AccessibleName = "Close" };
            _closeBtn.Click += (s, e) => Close();
            closePanel.Controls.Add(_closeBtn);

            // Category list -- same role as Logbook Center's: "Spots and Conditions categories,
            // POTA / SOTA, 1 of 6" on entry; Up/Down switches the page.
            _categoryListBox = new ListBox
            {
                Dock = DockStyle.Left,
                Width = 150,
                IntegralHeight = false,
                AccessibleName = "Categories",   // the window title already names it (2026-10-06)
            };
            // Pure layout host for the one page shown (see LogbookWindow's _categoryDetailHost).
            _host = new Panel { Dock = DockStyle.Fill, AccessibleName = "", AccessibleRole = AccessibleRole.None };

            string[] names = { "POTA / SOTA", "Contests", "Band Conditions", "DX Spots", "Space Weather", "Who Hears Me" };
            _pages = new[] { BuildPotaSotaTab(), BuildContestsTab(), BuildBandConditionsTab(), BuildDxSpotsTab(), BuildSpaceWeatherTab(), BuildWhoHearsMeTab() };
            for (int i = 0; i < _pages.Length; i++)
            {
                _pages[i].Dock = DockStyle.Fill;
                // Unnamed (2026-10-06): the category list has just said the page's name; a named
                // page said it again on the way in.
                _pages[i].AccessibleName = "";
                _pages[i].AccessibleRole = AccessibleRole.None;
                _categoryListBox.Items.Add(names[i]);
            }
            Controls.Add(_host);
            Controls.Add(_categoryListBox);
            Controls.Add(closePanel);
            CategoryListNav.Wire(_categoryListBox, _host, _pages.Cast<Control>().ToList());
            // Refresh only the page the operator just switched to (see RefreshActiveTab's own
            // comment for why -- root cause of a live JAWS pass hearing DX Spots status text
            // while sitting on the Space Weather tab).
            _categoryListBox.SelectedIndexChanged += (s, e) => RefreshActiveTab();
            // Focus is in the category list from the moment the window appears (2026-10-06): moved
            // there in Shown, the window was announced once on opening and again on the move.
            ActiveControl = _categoryListBox;

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
            switch (_categoryListBox.SelectedIndex)
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

        // One page; its name is set by the constructor, read once when the page is chosen.
        private static Panel MakeTabPage(string title) => new Panel();

        // A page's status line: a read-only text box, so Tab reaches it and a screen reader reads
        // it (a Label is never a Tab stop) -- last on its page, as in Logbook Center.
        private static TextBox MakeStatusBox() => new TextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control,
            AccessibleName = "Status",
            Text = "Loading...",
        };

        // -- Tab order --
        // Each page's controls in the order Tab visits them; the category list before, Close
        // after -- the same Form-level override Logbook Center uses (see its ProcessTabKey for
        // why: a Dock=Fill list must be added before its header, and WinForms' own walk then
        // visits controls in add order, not the order meant).
        protected override bool ProcessTabKey(bool forward)
        {
            int page = _categoryListBox?.SelectedIndex ?? -1;
            if (page < 0 || page >= _pages.Length || !_pageOrder.TryGetValue(_pages[page], out var order)) return base.ProcessTabKey(forward);
            Control active = ActiveControl;
            if (active == _categoryListBox)
                return forward ? (FirstSelectable(order, 0, 1) ?? _closeBtn).Focus() : _closeBtn.Focus();
            if (active == _closeBtn)
                return forward ? _categoryListBox.Focus() : (FirstSelectable(order, order.Length - 1, -1) ?? _categoryListBox).Focus();
            int idx = Array.IndexOf(order, active);
            if (idx >= 0)
            {
                int step = forward ? 1 : -1;
                Control next = FirstSelectable(order, idx + step, step);
                if (next != null) return next.Focus();
                return forward ? _closeBtn.Focus() : _categoryListBox.Focus();
            }
            return base.ProcessTabKey(forward);
        }

        private static Control FirstSelectable(Control[] order, int start, int step)
        {
            for (int i = start; i >= 0 && i < order.Length; i += step)
                if (order[i] != null && order[i].CanSelect) return order[i];
            return null;
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
            var lb = new ListBox
            {
                Dock = DockStyle.Fill,
                HorizontalScrollbar = true,
                TabIndex = 0,
                AccessibleName = accessibleName,
            };
            // The first line is selected when the list is entered, never while it is filled out of
            // view: a selection change is announced even in a list that does not have the focus.
            lb.GotFocus += (s, e) => SelectFirstItemIfNoneSelectedYet(lb, lb.SelectedIndex >= 0);
            return lb;
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

        private Panel BuildPotaSotaTab()
        {
            var page = MakeTabPage("POTA / SOTA");
            _potaList = MakeListBox("Spots list");

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 30 };
            _potaStatusLabel = MakeStatusBox();
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
            _pageOrder[page] = new Control[] { _potaList, chooseBtn, refreshBtn, _potaStatusLabel };
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

                    var potaRows = new List<string>();
                    _potaSpots.Clear();
                    if (result?.Spots != null)
                    {
                        foreach (var spot in result.Spots)
                        {
                            var annotation = OtaSpotAnnotator.Annotate(spot.Activator, band, _logbookDb, _lookupManager, tags);
                            potaRows.Add(FormatPotaSotaRow(spot, annotation));
                            _potaSpots.Add(spot);
                        }
                    }
                    SetRowsIfChanged(_potaList, potaRows);

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

        private Panel BuildBandConditionsTab()
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
            _condStatusLabel = MakeStatusBox();
            var refreshBtn = MakeRefreshButton((s, e) => RefreshBandConditions(), "Refresh band conditions now");
            refreshBtn.Dock = DockStyle.Right;
            bottom.Controls.Add(_condStatusLabel);
            bottom.Controls.Add(refreshBtn);

            page.Controls.Add(_condBandsList);
            page.Controls.Add(_condHeadlineBox);
            page.Controls.Add(bottom);
            _pageOrder[page] = new Control[] { _condHeadlineBox, _condBandsList, refreshBtn, _condStatusLabel };
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

                    SetRowsIfChanged(_condBandsList, result?.Bands != null
                        ? result.Bands.Select(FormatBandConditionsRow).ToList() : new List<string>());

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

        private Panel BuildContestsTab()
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
            _pageOrder[page] = new Control[] { _contestList, detailsBtn, refreshBtn, rulesBtn, _contestStatusBox };
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
                    _contestRows.Clear();
                    _contestRows.AddRange(upcoming);
                    SetRowsIfChanged(_contestList, rows);
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

        private Panel BuildWhoHearsMeTab()
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
            _heardStatusLabel = MakeStatusBox();
            var refreshBtn = MakeRefreshButton((s, e) => RefreshWhoHearsMe(), "Refresh who hears me now");
            refreshBtn.Dock = DockStyle.Right;
            bottom.Controls.Add(_heardStatusLabel);
            bottom.Controls.Add(refreshBtn);

            page.Controls.Add(_heardList);
            page.Controls.Add(top);
            page.Controls.Add(bottom);
            _pageOrder[page] = new Control[] { _heardSummaryBox, _heardWindowCb, _heardList, refreshBtn, _heardStatusLabel };
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

                    SetRowsIfChanged(_heardList, error == null && result?.Reports != null
                        ? result.Reports.Select(FormatHeardMeRow).ToList() : new List<string>());

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

        private Panel BuildDxSpotsTab()
        {
            var page = MakeTabPage("DX Spots");
            _dxList = MakeListBox("Spots list");

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 30 };
            _dxStatusLabel = MakeStatusBox();
            var refreshBtn = MakeRefreshButton((s, e) => RefreshDxSpots(), "Refresh DX spots now");
            refreshBtn.Dock = DockStyle.Right;
            bottom.Controls.Add(_dxStatusLabel);
            bottom.Controls.Add(refreshBtn);

            page.Controls.Add(_dxList);
            page.Controls.Add(bottom);
            _pageOrder[page] = new Control[] { _dxList, refreshBtn, _dxStatusLabel };
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

                    SetRowsIfChanged(_dxList, result?.Spots != null
                        ? result.Spots.Select(FormatDxSpotRow).ToList() : new List<string>());

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

        private Panel BuildSpaceWeatherTab()
        {
            var page = MakeTabPage("Space Weather");

            // Every reading is one line of a list, in plain words (operator, 2026-10-06: eleven
            // separate read-only boxes, each named after a label with brackets in it, and values
            // like "1.2e-6 W/m²", read oddly) -- arrowed through like every other list here.
            _wxReadingsList = MakeListBox("Readings");

            var historyPanel = new Panel { Dock = DockStyle.Bottom, Height = 150, AccessibleName = "", AccessibleRole = AccessibleRole.None };
            _wxHistoryList = MakeListBox("Daily solar history, newest first");
            historyPanel.Controls.Add(_wxHistoryList);
            historyPanel.Controls.Add(new Label { Text = "Daily solar history (newest first):", Dock = DockStyle.Top, Height = 20, TabStop = false });

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 30 };
            _wxStatusLabel = MakeStatusBox();
            var refreshBtn = MakeRefreshButton((s, e) => RefreshSpaceWeather(), "Refresh space weather now");
            refreshBtn.Dock = DockStyle.Right;
            bottom.Controls.Add(_wxStatusLabel);
            bottom.Controls.Add(refreshBtn);

            page.Controls.Add(_wxReadingsList);
            page.Controls.Add(historyPanel);
            page.Controls.Add(bottom);
            _pageOrder[page] = new Control[] { _wxReadingsList, _wxHistoryList, refreshBtn, _wxStatusLabel };
            return page;
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
                    SetRowsIfChanged(_wxReadingsList, FormatSpaceWeatherRows(result, error, wind, windError));
                    ShowSolarHistory(history, historyError);
                    _wxStatusLabel.Text = error != null || result?.Value == null
                        ? error ?? result?.LastError ?? "No data yet."
                        : result.LastError != null
                            ? $"Feed warning: {result.LastError}"
                            : (result.AgeSecs != null ? $"As of {AgeWords(result.AgeSecs.Value)}" : "");
                });
            });
        }

        // A list's rows replaced only when they changed. In the list being read the place is kept;
        // a list out of focus is left with nothing selected, so a refresh says nothing (a selection
        // change is announced even out of focus -- 2026-10-06).
        private static void SetRowsIfChanged(ListBox list, List<string> rows)
        {
            if (rows.SequenceEqual(list.Items.Cast<string>())) return;
            bool focused = list.Focused;
            int keep = list.SelectedIndex;
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (var r in rows) list.Items.Add(r);
            }
            finally { list.EndUpdate(); }
            if (focused && list.Items.Count > 0) list.SelectedIndex = keep >= 0 ? Math.Min(keep, list.Items.Count - 1) : 0;
        }

        // "45 seconds ago", "5 minutes ago", "2 hours ago" -- words, not "5m ago" (read "5 meters").
        internal static string AgeWords(long secs)
        {
            if (secs < 60) return secs == 1 ? "1 second ago" : $"{Math.Max(0, secs)} seconds ago";
            long m = secs / 60;
            if (m < 60) return m == 1 ? "1 minute ago" : $"{m} minutes ago";
            long h = m / 60;
            return h == 1 ? "1 hour ago" : $"{h} hours ago";
        }

        // The Space Weather readings, one plain line each (2026-10-06). Numbers without needless
        // decimals; units said in words; NOAA's scale words; nothing in brackets. The facts are
        // Nexus's own (SFI, Kp, A, X-ray class and R scale, its representative long-haul MUF from
        // the operator's grid, NOAA's G and S scales, DSCOVR solar wind) -- only the wording is here.
        // internal: JimmyTests exercises it.
        internal static List<string> FormatSpaceWeatherRows(SpaceWxResult result, string error, SolarWindResult wind, string windError)
        {
            var rows = new List<string>();
            if (error != null || result?.Value == null)
                rows.Add("Space weather not available: " + (error ?? result?.LastError ?? "no data yet"));
            else
            {
                var wx = result.Value;
                rows.Add($"Solar flux {wx.Sfi:0}");
                rows.Add(wx.Ssn.HasValue ? $"Sunspot number {wx.Ssn.Value:0}" : "Sunspot number not available");
                rows.Add($"K index {wx.Kp:0.#}");
                rows.Add($"A index {wx.AIndex:0}");
                rows.Add(string.IsNullOrEmpty(wx.XrayClass)
                    ? "X-ray level not available"
                    : $"X-ray {wx.XrayClass} class, radio blackout {FormatNoaaScale('R', wx.RScale)}");
                rows.Add(result.MufNow.HasValue
                    ? $"Long-haul MUF {result.MufNow.Value:0.#} megahertz"
                    : "Long-haul MUF not available, set My Grid in Options");
                if (result.Scales != null)
                {
                    string tomorrow = result.Scales.GScaleTomorrow != result.Scales.GScale
                        ? $", tomorrow {FormatNoaaScale('G', result.Scales.GScaleTomorrow)}" : "";
                    rows.Add($"Geomagnetic storm {FormatNoaaScale('G', result.Scales.GScale)}{tomorrow}");
                    rows.Add($"Radiation storm {FormatNoaaScale('S', result.Scales.SScale)}");
                }
                else
                {
                    string why = result.ScalesLastError != null ? "not available" : "loading";
                    rows.Add("Geomagnetic storm " + why);
                    rows.Add("Radiation storm " + why);
                }
            }
            var (bz, speed, age) = FormatSolarWind(wind, windError);
            rows.Add("Solar wind Bz " + bz);
            rows.Add("Solar wind " + speed);
            rows.Add("Solar wind " + age);
            return rows;
        }

        // Bz with its direction (southward -- negative -- is the one that disturbs the field);
        // speed/density only when NOAA gave them; the reading's own age, flagged when Nexus calls
        // it stale. internal: JimmyTests exercises it.
        internal static (string bz, string wind, string age) FormatSolarWind(SolarWindResult w, string error)
        {
            if (error != null || w == null || w.BzNt == null)
                return ("not available", "speed not available", "reading: " + (error ?? w?.Error ?? "none yet"));
            float bz = w.BzNt.Value;
            string dir = bz < 0 ? "southward" : bz > 0 ? "northward" : "neutral";
            string bt = w.BtNt.HasValue ? $", total field {w.BtNt.Value:0.#} nanotesla" : "";
            string speed = w.SpeedKms.HasValue ? $"{w.SpeedKms.Value:0} kilometers per second" : "speed not known";
            string density = w.Density.HasValue ? $", {w.Density.Value:0.#} protons per cubic centimeter" : ", density not known";
            string age = w.MeasuredAgeSecs.HasValue ? $"measured {AgeWords(w.MeasuredAgeSecs.Value)}" : "measurement time not known";
            if (w.Stale) age += ", an old reading, not current conditions";
            return ($"{bz:0.#} nanotesla, {dir}{bt}", speed + density, age);
        }

        private void ShowSolarHistory(SolarHistoryResult h, string error) =>
            SetRowsIfChanged(_wxHistoryList, FormatSolarHistory(h, error));

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
            return $"{scale}{level}, {word.ToLowerInvariant()}";
        }
    }
}
