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
        private bool _potaInFlight, _condInFlight, _dxInFlight, _wxInFlight, _dxpedInFlight;

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
        private ListBox _wxIonoList;       // KC2G ionosondes, measured MUF (2026-10-08)
        private ListBox _wxHistoryList;    // daily solar history
        private TextBox _wxStatusLabel;

        // What the main window gives the spot lists (2026-10-12). Every member may be absent
        // (tests, an older caller): the lists then show what the feed says and nothing more.
        public sealed class Host
        {
            // The checked awards as they stand for a band -- the SPOT's band, not the radio's.
            public Func<string, IEnumerable<WsjtxClient.ActiveAwardTag>> AwardTagsForBand;
            public Func<string, bool> IsWanted;
            // Row order of a list ("pota", "dx", "heard"): read, and saved.
            public Func<string, List<string>> RowOrder;
            public Action<string, List<string>> SaveRowOrder;
            // Prepare this station: the plan for a spot (nothing is touched), what carrying it
            // out would interrupt (null = nothing), and carrying it out (null = started).
            public Func<string, string, double, WsjtxClient.SpotPlan> PlanSpot;
            public Func<string> PrepareWouldInterrupt;
            public Func<WsjtxClient.SpotPlan, string> PrepareSpot;
        }
        private readonly Host _host2;

        public OtaSpotsWindow(LookupManager lookupManager,
            Func<System.Collections.Generic.Dictionary<string, WsjtxClient.ActiveAwardTag>> activeAwardTags,
            Func<string> currentBand, Host host = null)
        {
            _lookupManager = lookupManager;
            _activeAwardTags = activeAwardTags;
            _currentBand = currentBand;
            _host2 = host ?? new Host();
            _potaFields = _host2.RowOrder?.Invoke("pota") ?? new List<string>(SpotRows.Pota.Defaults);
            _dxFields = _host2.RowOrder?.Invoke("dx") ?? new List<string>(SpotRows.Dx.Defaults);
            _heardFields = _host2.RowOrder?.Invoke("heard") ?? new List<string>(SpotRows.Heard.Defaults);

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

            string[] names = { "POTA / SOTA", "Contests", "Band Conditions", "DX Spots", "Space Weather", "Who Hears Me", "DXpeditions" };
            _pages = new[] { BuildPotaSotaTab(), BuildContestsTab(), BuildBandConditionsTab(), BuildDxSpotsTab(), BuildSpaceWeatherTab(), BuildWhoHearsMeTab(), BuildDxpeditionsTab() };
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
            // Disposed as well as stopped (2026-10-12): this timer has no components container to
            // dispose it. A fetch still in flight comes back through SafeBeginInvoke, which does
            // nothing once the window is disposed, and nothing restarts the timer.
            FormClosed += (s, e) => { _refreshTimer.Stop(); _refreshTimer.Dispose(); _logbookDb?.Dispose(); };
            AppLook.Attach(this);   // the shared look: theme, text size, list colours
            AppLook.RememberBounds(this, "Spots");   // opens where it was last closed
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
                case 6: RefreshDxpeditions(); break;
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
            lb.GotFocus += (s, e) =>
            {
                // Rows that waited while the operator was elsewhere (SetRowsIfChanged) go in now,
                // in place, as the list is entered.
                if (_waitingRows.TryGetValue(lb, out var waiting)) { _waitingRows.Remove(lb); SetRowsIfChanged(lb, waiting.Rows, waiting.Records, waiting.Identity); }
                SelectFirstItemIfNoneSelectedYet(lb, lb.SelectedIndex >= 0);
            };
            return lb;
        }

        // A list's new rows held back while it is out of focus with a line still selected.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ListBox, Waiting> _waitingRows =
            new System.Runtime.CompilerServices.ConditionalWeakTable<ListBox, Waiting>();

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
            var orderBtn = MakeRowOrderButton("pota", SpotRows.Pota, "POTA and SOTA Row Order", () => _potaFields, f => _potaFields = f, RefreshPotaSota);
            var prepareBtn = MakePrepareButton(() =>
            {
                var spot = ShownRecord(_potaList, _potaList.SelectedIndex) as OtaSpot;
                return spot == null ? ((string, string, double, string)?)null : (spot.Activator, spot.Mode, spot.FreqKhz, FormatAge(spot.SpotTimeUnix));
            }, _potaStatusLabel);
            bottom.Controls.Add(_potaStatusLabel);
            bottom.Controls.Add(orderBtn);
            bottom.Controls.Add(prepareBtn);
            bottom.Controls.Add(chooseBtn);
            bottom.Controls.Add(refreshBtn);

            page.Controls.Add(_potaList);
            page.Controls.Add(bottom);
            _pageOrder[page] = new Control[] { _potaList, orderBtn, prepareBtn, chooseBtn, refreshBtn, _potaStatusLabel };
            return page;
        }

        // ── Prepare this station (2026-10-12) ───────────────────────────────────
        // A button, pressed on purpose: selecting a spot, a refresh or an alert never moves the
        // radio. It says what it will do and asks first; then the radio goes to the spot's band,
        // mode and frequency with transmit OFF, and the callsign is remembered. The station is
        // called only by the operator, from the station list, once it is actually decoded
        // (WsjtxClient.Spots.cs). Enter on a spot does nothing here, as before.
        private Button MakePrepareButton(Func<(string Call, string Mode, double FreqKhz, string Age)?> selected, TextBox status)
        {
            var btn = new Button { Text = "Prepare Station", AccessibleName = "Prepare this station", Size = new Size(110, 24), Dock = DockStyle.Right };
            btn.AccessibleDescription = "Sets the radio to the spot's band, mode and frequency and leaves transmit off. The station is listed when it is heard; select it there and press Enter to call.";
            btn.Click += (s, e) =>
            {
                var spot = selected();
                if (spot == null) { status.Text = "Select a spot first."; return; }
                if (_host2.PlanSpot == null || _host2.PrepareSpot == null) { status.Text = "The radio is not available."; return; }
                var plan = _host2.PlanSpot(spot.Value.Call, spot.Value.Mode, spot.Value.FreqKhz);
                if (plan == null || !plan.Ok) { status.Text = "Not prepared. " + (plan?.Reason ?? "The spot cannot be used."); return; }
                string interrupts = _host2.PrepareWouldInterrupt?.Invoke();
                string question = "Prepare the radio for " + plan.Sentence() + $" Spotted {spot.Value.Age}.\n\n" +
                    (interrupts != null ? interrupts + "\n\n" : "") +
                    "Transmit will be off. Nothing is sent. When the station is heard it is in the station list; select it there and press Enter to call it.";
                if (MessageBox.Show(this, question, "Prepare Station", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                        interrupts != null ? MessageBoxDefaultButton.Button2 : MessageBoxDefaultButton.Button1) != DialogResult.Yes)
                {
                    status.Text = "Not prepared. Nothing was changed.";
                    return;
                }
                string problem = _host2.PrepareSpot(plan);
                status.Text = problem != null ? "Not prepared. " + problem
                    : $"Setting the radio for {plan.Call}: {plan.Tier}, {plan.Band.TrimEnd('m')} meters, {plan.DialKHz / 1000.0:0.000}. Transmit is off. It will be listed when it is heard.";
            };
            return btn;
        }

        // ── Row Order (2026-10-12) ───────────────────────────────────────────────
        // Which fields a row has, and in what order -- the same checked list with Move Up,
        // Move Down and Restore Default the station lists use (EditLogRowOrderDlg). It changes
        // how a row reads, never which stations are listed or how they are sorted. Each of the
        // three lists keeps its own. Cancel changes nothing; OK rebuilds the rows from what is
        // already here (no new download) with the same station still selected.
        private List<string> _potaFields, _dxFields, _heardFields;

        private Button MakeRowOrderButton(string list, SpotRows.FieldSet set, string title, Func<List<string>> get, Action<List<string>> put, Action rebuild)
        {
            var btn = new Button { Text = "Row Order...", AccessibleName = "Row order", Size = new Size(100, 24), Dock = DockStyle.Right };
            btn.Click += (s, e) =>
            {
                using (var dlg = new EditLogRowOrderDlg(get(), set.All, set.Labels, title, "Choose what each row says, and in what order:", "Row parts"))
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    put(dlg.SelectedFields);
                    _host2.SaveRowOrder?.Invoke(list, dlg.SelectedFields);
                }
                rebuild();
            };
            return btn;
        }

        private void ChooseSelectedPark()
        {
            // The spot behind the row as it is on screen (rows wait while the list is out of
            // focus with a line selected, so this is never a newer list's row at that position).
            var spot = ShownRecord(_potaList, _potaList.SelectedIndex) as OtaSpot;
            if (spot == null) { _potaStatusLabel.Text = "Select a spot first."; return; }
            if (!string.Equals(spot.Program, "POTA", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(spot.Reference))
            {
                _potaStatusLabel.Text = "Only a POTA park can be chosen.";
                return;
            }
            // A park in several states (2026-10-12): which one the activator is in can be said
            // here, or left unsaid -- the contact then logs no state from the park, as before.
            var states = SpotRows.Places(spot).Where(SpotRows.IsUsState).Select(SpotRows.PlaceCode).ToList();
            string chosenState = null;
            if (states.Count > 1)
            {
                var answer = AskParkState(spot, states);
                if (answer == null) { _potaStatusLabel.Text = "No park was chosen."; return; }
                chosenState = answer.Length == 0 ? null : answer;
            }
            string location = !string.IsNullOrWhiteSpace(spot.Location) ? spot.Location : string.Join(",", SpotRows.Places(spot));
            ParkChoices.Choose(spot.Activator, spot.Reference, location, null, chosenState);
            _potaStatusLabel.Text = $"Park {spot.Reference.Trim().ToUpperInvariant()} chosen for {spot.Activator}" +
                (chosenState != null ? $", in {SpotRows.PlaceName("US-" + chosenState)}" : states.Count > 1 ? ", state not specified" : "") +
                "; it goes on that station's next logged contact.";
            RefreshPotaSota();
        }

        // Null = cancelled; "" = not specified; else the state's code.
        private string AskParkState(OtaSpot spot, List<string> states)
        {
            using (var dlg = new Form
            {
                Text = "Which State", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, ClientSize = new Size(420, 130), Font = Font,
            })
            {
                var about = new Label
                {
                    Text = $"Park {spot.Reference.Trim().ToUpperInvariant()} is in {string.Join(" and ", states.Select(c => SpotRows.PlaceName("US-" + c)))}. Which state is {spot.Activator} in?",
                    Location = new Point(10, 10), Size = new Size(400, 34),
                };
                var list = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(10, 50), Width = 400, AccessibleName = "State of the activation", TabIndex = 0 };
                list.Items.Add("Not specified");
                foreach (string c in states) list.Items.Add(SpotRows.PlaceName("US-" + c));
                list.SelectedIndex = 0;
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(250, 92), Width = 75, TabIndex = 1 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(335, 92), Width = 75, TabIndex = 2 };
                dlg.Controls.AddRange(new Control[] { about, list, ok, cancel });
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;
                AppLook.Attach(dlg);   // the shared look
                if (dlg.ShowDialog(this) != DialogResult.OK) return null;
                return list.SelectedIndex <= 0 ? "" : states[list.SelectedIndex - 1];
            }
        }

        // What Jimmy adds to a spot (SpotRows): worked before on the spot's band, where an
        // activation is, and the award and wanted labels for that band. The log and callsign
        // look-ups are remembered for a minute, so a list of hundreds of spots refreshed every
        // 15 seconds asks each question once, not every time.
        private readonly Dictionary<string, (bool Worked, LookupRecord Rec)> _spotMemo = new Dictionary<string, (bool, LookupRecord)>(StringComparer.OrdinalIgnoreCase);
        private DateTime _spotMemoFrom = DateTime.MinValue;

        private SpotRows.SpotNotes NotesFor(string call, double freqKhz, bool activation, IList<string> places, string chosenUsState)
        {
            var notes = new SpotRows.SpotNotes { LogReady = NexusLogbook.LogReady };
            if (string.IsNullOrEmpty(call)) return notes;
            if ((DateTime.UtcNow - _spotMemoFrom).TotalSeconds > 60 || _spotMemo.Count > 4000) { _spotMemo.Clear(); _spotMemoFrom = DateTime.UtcNow; }
            string band = SpotRows.BandOfKhz(freqKhz);
            string key = call + "|" + (band ?? "");
            if (!_spotMemo.TryGetValue(key, out var memo))
            {
                memo = (band != null && _logbookDb != null && _logbookDb.HasWorkedBefore(call, band),
                        _lookupManager != null && _lookupManager.Enabled ? _lookupManager.Build(call) : null);
                _spotMemo[key] = memo;
            }
            notes.WorkedBefore = memo.Worked;
            notes.Location = activation ? SpotRows.LocationText(places, chosenUsState) : "";
            if (!notes.LogReady) return notes;   // no log to judge against: no labels, and the status says "log loading"
            var facts = new SpotRows.Facts
            {
                Call = call, Band = band, Activation = activation, Places = places, ChosenUsState = chosenUsState,
                HomeState = memo.Rec?.State, Continent = memo.Rec?.Continent, CqZone = memo.Rec?.CqZone ?? 0, Dxcc = memo.Rec?.Dxcc ?? 0,
                Wanted = _host2.IsWanted?.Invoke(call) ?? false,
            };
            // An unknown band (a spot off the ham bands): no band, so no award is judged for it.
            if (band != null) notes.Labels = SpotRows.AwardLabels(facts, _host2.AwardTagsForBand?.Invoke(band));
            else if (facts.Wanted) notes.Labels = SpotRows.AwardLabels(facts, null);
            return notes;
        }

        private SpotRows.SpotNotes NotesFor(OtaSpot spot)
        {
            var places = SpotRows.Places(spot);
            // The operator's own choice for this park, while it stands.
            var choice = ParkChoices.Peek(spot.Activator);
            string chosen = choice != null && string.Equals(choice.Reference, (spot.Reference ?? "").Trim(), StringComparison.OrdinalIgnoreCase) ? choice.State : null;
            return NotesFor(spot.Activator, spot.FreqKhz, activation: true, places, places.Count > 1 ? chosen : null);
        }

        // A spot's identity across refreshes: the same station on the same park and band stays
        // selected when the list is rebuilt, whatever row it lands on.
        private static string SpotId(object o) =>
            o is OtaSpot s ? $"{s.Program}|{s.Reference}|{s.Activator}|{SpotRows.BandOfKhz(s.FreqKhz)}"
            : o is DxSpot d ? $"{d.DxCall}|{SpotRows.BandOfKhz(d.FreqKhz)}|{d.Spotter}"
            : o is HeardMe h ? $"{h.Call}|{h.Band}" : null;

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
                    var spots = new List<OtaSpot>();
                    if (result?.Spots != null)
                    {
                        foreach (var spot in result.Spots)
                        {
                            // Judged on the SPOT's band and the activation's own place (SpotRows),
                            // never the radio's band or the activator's home state.
                            potaRows.Add(SpotRows.PotaRow(spot, NotesFor(spot), _potaFields, FormatAge(spot.SpotTimeUnix)));
                            spots.Add(spot);
                        }
                    }
                    // The spots behind the rows change only when the rows on screen do, and the
                    // same spot stays selected (SetRowsIfChanged).
                    if (SetRowsIfChanged(_potaList, potaRows, spots.Cast<object>().ToList(), SpotId))
                    {
                        _potaSpots.Clear();
                        _potaSpots.AddRange(spots);
                    }

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
        // 2026-10-12: a count ("needed for 2 awards") said nothing about WHICH awards; the row now
        // has the awards' own labels beside this (SpotRows), and this says only worked or not.
        internal static string FormatStatus(OtaSpotAnnotation a)
        {
            if (a == null) return "";
            // Logbook migration: while the Nexus logbook is loading, worked-before is unknown.
            if (!NexusLogbook.LogReady) return "log loading";
            return a.WorkedBefore ? "worked" : "not worked";
        }

        // One line per spot, every field labeled -- the exact wording a live JAWS pass on this
        // window already read out loud correctly (see MakeListBox's own comment for why this
        // replaced a multi-column ListView). internal (not private): JimmyTests exercises this
        // directly, same convention as FormatStatus above.
        internal static string FormatPotaSotaRow(OtaSpot spot, OtaSpotAnnotation annotation)
        {
            // The default row (SpotRows.Pota.Defaults), with what the annotation says.
            var notes = annotation == null ? null : new SpotRows.SpotNotes
            {
                LogReady = NexusLogbook.LogReady, WorkedBefore = annotation.WorkedBefore,
                Location = SpotRows.LocationText(SpotRows.Places(spot), null), Labels = annotation.Labels ?? new List<string>(),
            };
            return SpotRows.PotaRow(spot, notes, null, FormatAge(spot.SpotTimeUnix));
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
            var orderBtn = MakeRowOrderButton("heard", SpotRows.Heard, "Who Hears Me Row Order", () => _heardFields, f => _heardFields = f, RefreshWhoHearsMe);
            bottom.Controls.Add(_heardStatusLabel);
            bottom.Controls.Add(orderBtn);
            bottom.Controls.Add(refreshBtn);

            page.Controls.Add(_heardList);
            page.Controls.Add(top);
            page.Controls.Add(bottom);
            _pageOrder[page] = new Control[] { _heardSummaryBox, _heardWindowCb, _heardList, orderBtn, refreshBtn, _heardStatusLabel };
            return page;
        }

        // One line per receiving station, every field labeled where it isn't obvious. An SNR the
        // report didn't carry, and a distance without the station's grid, are said as unknown --
        // never shown as 0. internal: JimmyTests exercises it.
        internal static string FormatHeardMeRow(HeardMe h) => SpotRows.HeardRow(h, null, FormatAgeSecs(h.AgeSecs));

        internal static string FormatHeardMeSummary(GettingOutResult r)
        {
            string window = $"in the last {r.WindowMinutes} minutes";
            if (r.Count == 0) return $"No reports of your signal {window}.";
            string who = r.Count == 1 ? "1 station heard you" : $"{r.Count} stations heard you";
            return r.MaxKm > 0 ? $"{who} {window}, furthest {r.MaxKm:N0} km." : $"{who} {window}.";
        }

        // ── DXpeditions (2026-10-08) ─────────────────────────────────────────────
        // The announced DXpeditions (NG3K, with Club Log's on-air list) from Nexus's own fetch,
        // with Jimmy's own worked-entity rules for what is needed. Calendar only: no frequency,
        // and nothing here ever tunes the radio. "Active now" = inside the announced dates (or on
        // the air per Club Log, said as such) -- not proof the operation is heard.

        private ListBox _dxpedList;
        private ComboBox _dxpedShowCb;
        private CheckBox _dxpedNeededCb;
        private TextBox _dxpedStatusBox;
        private readonly List<DxpeditionPlan> _dxpedRows = new List<DxpeditionPlan>();

        private Panel BuildDxpeditionsTab()
        {
            var page = MakeTabPage("DXpeditions");
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            top.Controls.Add(new Label { Text = "Show:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
            _dxpedShowCb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130, AccessibleName = "Show" };
            _dxpedShowCb.Items.AddRange(new object[] { "Active now", "Upcoming", "All" });
            _dxpedShowCb.SelectedIndex = 0;
            _dxpedShowCb.SelectedIndexChanged += (s, e) => RefreshDxpeditions();
            _dxpedNeededCb = new CheckBox { Text = "Needed only", AccessibleName = "Needed only", AutoSize = true, Margin = new Padding(10, 6, 3, 3) };
            _dxpedNeededCb.CheckedChanged += (s, e) => RefreshDxpeditions();
            top.Controls.Add(_dxpedShowCb);
            top.Controls.Add(_dxpedNeededCb);

            _dxpedList = MakeListBox("DXpeditions list");
            _dxpedList.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { OpenDxpeditionWebsite(); e.Handled = true; e.SuppressKeyPress = true; } };

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            var websiteBtn = new Button { Text = "Open &Website", AccessibleName = "Open website", AutoSize = true };
            websiteBtn.Click += (s, e) => OpenDxpeditionWebsite();
            var refreshBtn = new Button { Text = "&Refresh", AccessibleName = "Refresh DXpeditions", AutoSize = true };
            refreshBtn.Click += (s, e) => RefreshDxpeditions();
            bottom.Controls.Add(websiteBtn);
            bottom.Controls.Add(refreshBtn);
            _dxpedStatusBox = MakeStatusBox();
            _dxpedStatusBox.Dock = DockStyle.Bottom;

            page.Controls.Add(_dxpedList);
            page.Controls.Add(top);
            page.Controls.Add(bottom);
            page.Controls.Add(_dxpedStatusBox);   // added last: docks lowest, under the buttons, as Tab reaches it
            _pageOrder[page] = new Control[] { _dxpedShowCb, _dxpedNeededCb, _dxpedList, websiteBtn, refreshBtn, _dxpedStatusBox };
            return page;
        }

        // What Jimmy's log says about one operation: Known = false when its entity can't be told
        // from its callsign or the log is still loading -- never guessed needed or not.
        internal sealed class DxpeditionNeed
        {
            public bool Known;
            public bool NewDxcc;
            public List<string> NeededBands = new List<string>();
            public bool Needed => Known && (NewDxcc || NeededBands.Count > 0);
        }

        // The same worked-entity rule the call list uses (ClassificationEngine: HasWorkedDxcc by
        // entity, overall and per band), the entity from the offline lookup of the callsign.
        internal static DxpeditionNeed AssessDxpedition(DxpeditionPlan p, Func<string, int> dxccOf, Func<int, string, bool> workedDxcc, bool logReady)
        {
            var need = new DxpeditionNeed();
            int dxcc = 0;
            try { dxcc = dxccOf(p?.Call ?? ""); } catch { }
            if (!logReady || dxcc <= 0) return need;
            need.Known = true;
            need.NewDxcc = !workedDxcc(dxcc, null);
            foreach (string band in p.Bands ?? Array.Empty<string>())
                if (!workedDxcc(dxcc, band)) need.NeededBands.Add(band);
            return need;
        }

        internal static bool DxpeditionActive(DxpeditionPlan p, long nowUnix) =>
            p.OnAirPerClubLog || (p.StartUnix.HasValue && p.EndUnix.HasValue && p.StartUnix.Value <= nowUnix && nowUnix <= p.EndUnix.Value);

        internal static bool DxpeditionUpcoming(DxpeditionPlan p, long nowUnix) =>
            !p.OnAirPerClubLog && p.StartUnix.HasValue && p.StartUnix.Value > nowUnix;

        // FT8/FT4 announcements first, then those that announced no mode, then the rest; within
        // each, by start date.
        internal static int DxpeditionModeRank(DxpeditionPlan p)
        {
            var modes = p.Modes ?? Array.Empty<string>();
            if (modes.Any(m => m.Equals("FT8", StringComparison.OrdinalIgnoreCase) || m.Equals("FT4", StringComparison.OrdinalIgnoreCase))) return 0;
            return modes.Length == 0 ? 1 : 2;
        }

        // One row: "H49A, Solomon Is, active now, October 7 to October 21 UTC, 20m 6m, FT8 CW SSB, new DXCC".
        internal static string FormatDxpeditionRow(DxpeditionPlan p, DxpeditionNeed need, long nowUnix)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string Day(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("MMMM d", inv);
            string when;
            if (p.OnAirPerClubLog)
                when = "on the air per Club Log" + (p.EndUnix.HasValue ? $", announced to {Day(p.EndUnix.Value)} UTC" : "");
            else if (p.StartUnix.HasValue && p.EndUnix.HasValue)
                when = (DxpeditionActive(p, nowUnix) ? "active now, " : p.EndUnix.Value < nowUnix ? "ended, " : "")
                       + $"{Day(p.StartUnix.Value)} to {Day(p.EndUnix.Value)} UTC";
            else when = "dates not known";
            string bands = p.Bands?.Length > 0 ? string.Join(" ", p.Bands) : "bands not announced";
            string modes = p.Modes?.Length > 0 ? string.Join(" ", p.Modes) : "mode not announced";
            string fox = string.IsNullOrEmpty(p.Ft8Mode) ? "" : p.Ft8Mode == "FoxHound" ? ", Fox and Hound"
                : p.Ft8Mode == "SuperFox" ? ", SuperFox" : ", " + p.Ft8Mode;
            string needed = !need.Known ? ", needs not known"
                : need.NewDxcc ? ", new DXCC"
                : need.NeededBands.Count > 0 ? ", needed on " + string.Join(" ", need.NeededBands)
                : ", worked";
            string entity = string.IsNullOrWhiteSpace(p.Entity) ? "" : ", " + p.Entity.Trim();
            return $"{p.Call}{entity}, {when}, {bands}, {modes}{fox}{needed}";
        }

        // The rows for a Show choice (0 Active now, 1 Upcoming, 2 All) and Needed only. Returns
        // the plans shown and how many were left out only because their needs are not known.
        internal static (List<DxpeditionPlan> Plans, List<DxpeditionNeed> Needs, int UnknownHidden) SelectDxpeditions(
            IEnumerable<(DxpeditionPlan Plan, DxpeditionNeed Need)> all, int show, bool neededOnly, long nowUnix)
        {
            var chosen = all.Where(x => show == 2 || (show == 0 ? DxpeditionActive(x.Plan, nowUnix) : DxpeditionUpcoming(x.Plan, nowUnix))).ToList();
            int unknownHidden = neededOnly ? chosen.Count(x => !x.Need.Known) : 0;
            if (neededOnly) chosen = chosen.Where(x => x.Need.Needed).ToList();
            chosen = chosen.OrderBy(x => DxpeditionModeRank(x.Plan)).ThenBy(x => x.Plan.StartUnix ?? long.MaxValue).ThenBy(x => x.Plan.Call).ToList();
            return (chosen.Select(x => x.Plan).ToList(), chosen.Select(x => x.Need).ToList(), unknownHidden);
        }

        private void RefreshDxpeditions()
        {
            if (_dxpedInFlight) return;
            _dxpedInFlight = true;
            int show = _dxpedShowCb.SelectedIndex;
            bool neededOnly = _dxpedNeededCb.Checked;
            System.Threading.Tasks.Task.Run(() =>
            {
                var result = _client.GetDxpeditions(out string error);
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                bool logReady = NexusLogbook.LogReady;
                var assessed = (result?.Plans ?? Array.Empty<DxpeditionPlan>())
                    .Where(pl => !string.IsNullOrWhiteSpace(pl?.Call))
                    .Select(pl => (pl, AssessDxpedition(pl, c => _lookupManager?.BuildOffline(c)?.Dxcc ?? 0,
                        (dxcc, band) => _logbookDb != null && _logbookDb.HasWorkedDxcc(dxcc, band), logReady && _logbookDb != null)))
                    .ToList();
                var picked = SelectDxpeditions(assessed, show, neededOnly, now);
                SafeBeginInvoke(() =>
                {
                    _dxpedInFlight = false;
                    if (IsDisposed) return;
                    var rows = picked.Plans.Select((pl, i) => FormatDxpeditionRow(pl, picked.Needs[i], now)).ToList();
                    if (rows.Count == 0)
                        rows.Add(result?.Plans == null ? "DXpeditions " + (error ?? result?.Error ?? "loading")
                            : show == 0 ? "No DXpeditions active now" : show == 1 ? "No upcoming DXpeditions" : "No DXpeditions announced");
                    _dxpedRows.Clear();
                    _dxpedRows.AddRange(picked.Plans);
                    SetRowsIfChanged(_dxpedList, rows);
                    string status = error != null ? "Could not read the DXpeditions: " + error
                        : result?.CheckedAgeSecs == null ? (result?.Error != null ? "DXpedition calendars unavailable: " + result.Error : "DXpedition calendars not read yet.")
                        : $"Calendars from NG3K and Club Log, checked {AgeWords(result.CheckedAgeSecs.Value)}" + (result.Error != null ? $"; the last check failed: {result.Error}" : "") + ".";
                    if (picked.UnknownHidden > 0) status += $" {picked.UnknownHidden} not shown because their needs are not known.";
                    if (!logReady) status += " The logbook is still loading; needs are not known yet.";
                    if (_dxpedStatusBox.Text != status) _dxpedStatusBox.Text = status;
                });
            });
        }

        private void OpenDxpeditionWebsite()
        {
            int i = _dxpedList.SelectedIndex;
            if (i < 0 || i >= _dxpedRows.Count) { _dxpedStatusBox.Text = "Select a DXpedition first."; return; }
            string url = _dxpedRows[i].Website;
            if (string.IsNullOrWhiteSpace(url) || !(url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
            {
                _dxpedStatusBox.Text = $"{_dxpedRows[i].Call} has no website in the calendar.";
                return;
            }
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { _dxpedStatusBox.Text = $"Could not open the website: {ex.Message}"; }
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

                    var heard = error == null && result?.Reports != null ? result.Reports.ToList() : new List<HeardMe>();
                    SetRowsIfChanged(_heardList, heard.Select(h => SpotRows.HeardRow(h, _heardFields, FormatAgeSecs(h.AgeSecs))).ToList(),
                        heard.Cast<object>().ToList(), SpotId);

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
            var orderBtn = MakeRowOrderButton("dx", SpotRows.Dx, "DX Spots Row Order", () => _dxFields, f => _dxFields = f, RefreshDxSpots);
            var prepareBtn = MakePrepareButton(() =>
            {
                // A reverse-beacon spot says its mode; a hand-typed cluster spot does not, and
                // a mode is never guessed for it.
                var spot = ShownRecord(_dxList, _dxList.SelectedIndex) as DxSpot;
                return spot == null ? ((string, string, double, string)?)null : (spot.DxCall, spot.Rbn ? spot.SkimmerMode : "", spot.FreqKhz, FormatAgeSecs(spot.AgeSecs));
            }, _dxStatusLabel);
            bottom.Controls.Add(_dxStatusLabel);
            bottom.Controls.Add(orderBtn);
            bottom.Controls.Add(prepareBtn);
            bottom.Controls.Add(refreshBtn);

            page.Controls.Add(_dxList);
            page.Controls.Add(bottom);
            _pageOrder[page] = new Control[] { _dxList, orderBtn, prepareBtn, refreshBtn, _dxStatusLabel };
            return page;
        }
        private readonly List<DxSpot> _dxSpots = new List<DxSpot>();   // the spot behind each row

        // One line per spot, every field labeled -- see MakeListBox's own comment for why this
        // replaced a multi-column ListView. internal (not private): JimmyTests exercises this
        // directly.
        internal static string FormatDxSpotRow(DxSpot s) => SpotRows.DxRow(s, null, null, FormatAgeSecs(s.AgeSecs));

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

                    // Worked before and the award labels are for the spot's own band; the state
                    // of a DX spot is the station's home state (it is not an activation).
                    var dxSpots = result?.Spots != null ? result.Spots.ToList() : new List<DxSpot>();
                    var dxRows = dxSpots.Select(d => SpotRows.DxRow(d, NotesFor(d.DxCall, d.FreqKhz, false, null, null), _dxFields, FormatAgeSecs(d.AgeSecs))).ToList();
                    if (SetRowsIfChanged(_dxList, dxRows, dxSpots.Cast<object>().ToList(), SpotId))
                    {
                        _dxSpots.Clear();
                        _dxSpots.AddRange(dxSpots);
                    }

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

            // Measured MUF from KC2G's ionosondes (2026-10-08), under the readings' modeled one.
            var ionoPanel = new Panel { Dock = DockStyle.Bottom, Height = 150, AccessibleName = "", AccessibleRole = AccessibleRole.None };
            _wxIonoList = MakeListBox("Ionosondes, measured MUF");
            ionoPanel.Controls.Add(_wxIonoList);
            ionoPanel.Controls.Add(new Label { Text = "Ionosondes, measured MUF (recent first, nearest first):", Dock = DockStyle.Top, Height = 20, TabStop = false });

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
            page.Controls.Add(ionoPanel);      // docks above the history
            page.Controls.Add(historyPanel);
            page.Controls.Add(bottom);
            _pageOrder[page] = new Control[] { _wxReadingsList, _wxIonoList, _wxHistoryList, refreshBtn, _wxStatusLabel };
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
                    SetRowsIfChanged(_wxReadingsList, FormatSpaceWeatherRows(result, error, wind, windError, history));
                    SetRowsIfChanged(_wxIonoList, FormatIonosondeRows(result, error));
                    ShowSolarHistory(history, historyError);
                    string status = error != null || result?.Value == null
                        ? error ?? result?.LastError ?? "No data yet."
                        : result.LastError != null
                            ? $"Feed warning: {result.LastError}"
                            : (result.AgeSecs != null ? $"As of {AgeWords(result.AgeSecs.Value)}" : "");
                    string xray = error == null ? FormatXrayStatus(result) : "";
                    _wxStatusLabel.Text = xray.Length == 0 ? status : status.Length == 0 ? xray : status + "; " + xray;
                });
            });
        }

        // A list's rows replaced only when they changed. In the list being read the place is kept;
        // a list out of focus is left with nothing selected, so a refresh says nothing (a selection
        // change is announced even out of focus -- 2026-10-06).
        // 2026-10-08: a list the operator was on and left still has a line selected, and rewriting
        // it was read out while they were in another list (the Space Weather readings, heard from
        // the history list). Its new rows wait, silently, until the list is entered again.
        // internal: JimmyTests exercises it.
        // records/identity (2026-10-12): the record behind each row, and what makes a record the
        // same one after a refresh. The line that was selected stays selected by WHAT it is, not
        // by its position -- a refresh that puts a new spot above it no longer moves the
        // selection to a different station. Returns true when the rows on screen are now `rows`
        // (so the caller's own list of records is in step with them); false when they are
        // waiting for the list to be entered, and the screen still shows the old ones.
        internal static bool SetRowsIfChanged(ListBox list, List<string> rows, List<object> records = null, Func<object, string> identity = null)
        {
            _shownRecords.TryGetValue(list, out var shown);
            if (rows.SequenceEqual(list.Items.Cast<string>()))
            {
                _waitingRows.Remove(list);
                if (records != null) _shownRecords.AddOrUpdate(list, records);
                return true;
            }
            bool focused = list.Focused;
            if (!focused && list.SelectedIndex >= 0)
            {
                _waitingRows.AddOrUpdate(list, new Waiting { Rows = rows, Records = records, Identity = identity });
                return false;
            }
            _waitingRows.Remove(list);
            int keep = list.SelectedIndex;
            string keepId = identity != null && shown != null && keep >= 0 && keep < shown.Count ? identity(shown[keep]) : null;
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (var r in rows) list.Items.Add(r);
            }
            finally { list.EndUpdate(); }
            if (records != null) _shownRecords.AddOrUpdate(list, records); else _shownRecords.Remove(list);
            if (focused && list.Items.Count > 0)
            {
                int same = keepId != null && records != null ? records.FindIndex(r => identity(r) == keepId) : -1;
                list.SelectedIndex = same >= 0 ? same : keep >= 0 ? Math.Min(keep, list.Items.Count - 1) : 0;
            }
            return true;
        }

        private sealed class Waiting { public List<string> Rows; public List<object> Records; public Func<object, string> Identity; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ListBox, List<object>> _shownRecords =
            new System.Runtime.CompilerServices.ConditionalWeakTable<ListBox, List<object>>();
        // The record behind a list's row as it is ON SCREEN now, or null.
        internal static object ShownRecord(ListBox list, int index) =>
            _shownRecords.TryGetValue(list, out var shown) && index >= 0 && index < shown.Count ? shown[index] : null;

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
        internal static List<string> FormatSpaceWeatherRows(SpaceWxResult result, string error, SolarWindResult wind, string windError,
            SolarHistoryResult history = null)
        {
            var rows = new List<string>();
            if (error != null || result?.Value == null)
                rows.Add("Space weather not available: " + (error ?? result?.LastError ?? "no data yet"));
            else
            {
                var wx = result.Value;
                rows.Add($"Solar flux {wx.Sfi:0}");
                rows.Add(FormatSunspotRow(wx.Ssn, history));
                rows.Add($"K index {wx.Kp:0.#}");
                rows.Add($"A index {wx.AIndex:0}");
                rows.Add(FormatXrayRow(result));
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

        // Nexus leaves the current reading's sunspot number empty (its desktop fills it from a
        // separate predicted-cycle feed EngineHost does not read), so the row said "not available"
        // every time while the daily history below had NOAA's real count (2026-10-08). The newest
        // daily count, with its date, when there is no current one.
        internal static string FormatSunspotRow(float? current, SolarHistoryResult history)
        {
            if (current.HasValue) return $"Sunspot number {current.Value:0}";
            var day = history?.Days?.Where(d => d.Ssn.HasValue).OrderByDescending(d => d.DayUnix).FirstOrDefault();
            if (day == null) return "Sunspot number not available";
            var date = DateTimeOffset.FromUnixTimeSeconds(day.DayUnix).UtcDateTime;
            return $"Sunspot number {day.Ssn.Value:0}, daily count for {date.ToString("MMMM d", System.Globalization.CultureInfo.InvariantCulture)}";
        }

        // One line per ionosonde (2026-10-08): where it is (KC2G's name, else its position -- no
        // name is ever made up), its measured MUF and foF2 (or "not reported"), how far, and how
        // old the measurement is ("measurement time not known" when KC2G gave none). Readings
        // from the last day first, each group nearest first (EngineHost's order); a station whose
        // last reading is months old stays listed, saying so.
        internal const long IonosondeRecentSecs = 24 * 3600;

        internal static List<string> FormatIonosondeRows(SpaceWxResult result, string error)
        {
            var list = result?.Ionosondes;
            if (error != null || list == null)
                return new List<string> { "Ionosonde readings " + (error != null || result?.IonosondesLastError != null
                    ? "not available: " + (error ?? result.IonosondesLastError) : "loading") };
            if (list.Length == 0) return new List<string> { "No ionosonde readings" };
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string Mhz(double? v, string what) => v.HasValue ? $"{what} {v.Value.ToString("0.#", inv)} megahertz" : $"{what} not reported";
            string Where(Ionosonde s) => !string.IsNullOrWhiteSpace(s.Name) ? s.Name.Trim()
                : $"Station near {Math.Abs(s.Lat).ToString("0.#", inv)} {(s.Lat >= 0 ? "north" : "south")}, " +
                  $"{Math.Abs(s.Lon).ToString("0.#", inv)} {(s.Lon >= 0 ? "east" : "west")}";
            bool Recent(Ionosonde s) => s.MeasuredAgeSecs.HasValue && s.MeasuredAgeSecs.Value <= IonosondeRecentSecs;
            return list.Select((s, i) => (s, i)).OrderBy(x => Recent(x.s) ? 0 : 1).ThenBy(x => x.i).Select(x =>
            {
                var s = x.s;
                string dist = s.DistanceKm.HasValue ? $", {s.DistanceKm.Value.ToString("#,0", inv)} kilometers" : "";
                string age = s.MeasuredAgeSecs.HasValue ? "measured " + LongAgeWords(s.MeasuredAgeSecs.Value) : "measurement time not known";
                string code = string.IsNullOrWhiteSpace(s.Code) ? "" : ", " + s.Code.Trim();
                return $"{Where(s)}: {Mhz(s.MufMhz, "MUF")}, {Mhz(s.Fof2Mhz, "foF2")}{dist}, {age}{code}";
            }).ToList();
        }

        // AgeWords, and days past two days ("202 days ago", not "4848 hours ago").
        internal static string LongAgeWords(long secs) =>
            secs >= 48 * 3600 ? $"{secs / 86400} days ago" : AgeWords(secs);

        // The fast-lane X-ray reading is over this old: said in its row.
        internal const long XrayOldSecs = 5 * 60;

        // The X-ray row (2026-10-08): the minute-by-minute reading when EngineHost has one, else
        // the one that came with the other readings. No age in the row itself -- the page re-reads
        // every 15 seconds, and a row that changes is read out again while the operator is on it;
        // the age goes in the status line, and the row says only when the reading has gone old.
        internal static string FormatXrayRow(SpaceWxResult result)
        {
            var fast = result?.Xray;
            string cls = fast != null ? fast.XrayClass : result?.Value?.XrayClass;
            int r = fast != null ? fast.RScale : result?.Value?.RScale ?? 0;
            if (string.IsNullOrEmpty(cls)) return "X-ray level not available";
            bool old = fast != null && (result.XrayAgeSecs ?? long.MaxValue) > XrayOldSecs;
            return $"X-ray {cls} class, radio blackout {FormatNoaaScale('R', r)}" + (old ? ", an old reading, not updating" : "");
        }

        // The status line's X-ray part: its own age, and why it is not updating when it isn't.
        internal static string FormatXrayStatus(SpaceWxResult result)
        {
            if (result?.Xray == null) return result?.XrayLastError != null ? "X-ray not updating: " + result.XrayLastError : "";
            string age = result.XrayAgeSecs != null ? "X-ray " + AgeWords(result.XrayAgeSecs.Value) : "";
            return result.XrayLastError != null && (result.XrayAgeSecs ?? long.MaxValue) > XrayOldSecs
                ? age + ", not updating: " + result.XrayLastError
                : age;
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
