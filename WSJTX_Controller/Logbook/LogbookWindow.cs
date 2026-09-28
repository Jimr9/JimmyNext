using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Non-modal Ham Radio Center / Logbook window.
    // Open via Controller.OpenLogbookWindow() — singleton (one instance at a time).
    //
    // Nexus contesting foundation: Contesting is NOT a page here. It was, briefly (a "Contesting
    // tab" inside this window) -- live JAWS testing (2026-09-25) found Logbook Center's own
    // category navigation followed by three MORE nested tabs inside that one entry confusing and
    // poorly announced. Contesting is back to being its own standalone, modeless window (see
    // ContestingWindow.cs), opened by Options -> Station & Operator's "Open Contesting" button
    // and the configurable Contesting hotkey (Controller.OpenContestingWindow) -- this window has
    // no involvement in it at all.
    public class LogbookWindow : Form
    {
        // ── Dependencies ──────────────────────────────────────────────────────────
        // Credentials are read live (via these delegates), not snapshotted once at
        // construction, so a change made in Options while this window is already open
        // takes effect immediately instead of requiring a close/reopen -- matches the
        // same live-read pattern LiveQsoUploadOrchestrator already uses for the same reason.
        private readonly IniFile    _ini;
        private readonly Func<string> _qrzApiKey;
        private readonly Func<string> _lotwUser;
        private readonly Func<string> _lotwPass;
        private readonly Func<string> _clubLogEmail;
        private readonly Func<string> _clubLogPassword;
        private readonly Func<string> _clubLogCallsign;
        private readonly Func<string> _eqslUsername;
        private readonly Func<string> _eqslPassword;
        private readonly Action   _onImportComplete;
        private readonly HashSet<string> _activeAwardRuleIds;
        private readonly Action<string, bool> _onActiveAwardRuleIdsChanged;
        // Offline-only callsign->US-state lookup, used when an imported ADIF record's own
        // STATE field is blank (see AdifImporter.Normalize). Never a live network query.
        private readonly Func<string, string> _resolveUsState;
        // Manual QSO entry (EditQsoDlg) support -- all offline/local, never a live network
        // query or a WSJT-X command. isWsjtxConnected/currentBand/currentMode let a brand-new
        // entry default to what's actually on the air right now; lookupCallsign is the same
        // offline station lookup as _resolveUsState but returning the whole record (state/
        // country/grid) for EditQsoDlg's blank-fields-only auto-fill.
        private readonly Func<bool> _isWsjtxConnected;
        private readonly Func<string> _currentBand;
        private readonly Func<string> _currentMode;
        private readonly Func<string, LookupRecord> _lookupCallsign;
        // Plays Jimmy's existing "Logged" sound/checkbox (same one used for auto-logged QSOs)
        // on each successful manual Add -- audible confirmation with no focus movement, so a
        // contest operator's focus can stay on the Callsign field between contacts.
        private readonly Action _onQsoLogged;
        // Logbook migration: the Nexus logbook outbox, when live logging goes through Nexus
        // (null until then -- nothing below runs). Duplicate refusals it holds are shown in the
        // Status field only: no main status line, no announcement, no focus change.
        private readonly NexusLogOutbox _nexusOutbox;

        // ── Database ──────────────────────────────────────────────────────────────
        // Nexus contesting foundation, boundary-completion pass: ILogbookService, not LogbookDb
        // -- every method this window calls is now on the interface; SQLite details stay inside
        // LogbookDb, the sole implementation.
        private ILogbookService _db;

        // ── Navigation state ──────────────────────────────────────────────────────
        private Panel _activePage;

        // ── Layout controls ───────────────────────────────────────────────────────
        // Nexus contesting foundation, JAWS correction pass (2026-09-25): replaces the former
        // TabControl with the same category-list-and-page arrangement Options already uses
        // (OptionsDlg._categoryListBox/_categoryDetailHost, WireCategoryList) -- proven with real
        // JAWS/NVDA testing there. Mechanical container swap only: every page's own controls,
        // AccessibleName values, Build*Page()/Populate*() methods, and behavior are unchanged;
        // only how the operator selects which one is visible changes.
        private ListBox _categoryListBox;
        private Panel   _categoryDetailHost;
        private TextBox _statusTb;

        // ── Page panels ───────────────────────────────────────────────────────────
        private Panel _myLogPanel;
        private Panel _awardsPanel;
        private Panel _stillNeedPanel;
        private Panel _lookupPanel;
        private Panel _editLogPanel;
        private Panel _syncPanel;

        // ── My Log controls ───────────────────────────────────────────────────────
        private TextBox  _statTotalTb;
        private TextBox  _statLotwTb;
        private TextBox  _statQrzTb;
        private TextBox  _statConfTb;
        private TextBox  _statWasTb;
        private TextBox  _statDxccTb;
        private TextBox  _statWazTb;
        private TextBox  _statUploadQrzTb;
        private TextBox  _statUploadClubLogTb;
        private TextBox  _statUploadLotwTb;
        private TextBox  _statUploadHrdLogTb;
        private ListView _dashRecentLv;

        // ── Awards controls ───────────────────────────────────────────────────────
        private ComboBox _awardsViewCb;
        private TextBox  _awardsProgressLbl;
        private ListView _awardsLv;
        private Button   _awardsManageBtn;
        private Button   _awardsRefreshBtn;
        private List<RuleDefinition> _awardsDefs = new List<RuleDefinition>();
        private bool     _suppressAwardsEvent;

        // ── Still Need controls ────────────────────────────────────────────────────
        private CheckedListBox _neededAwardsClb;
        private ComboBox _neededBandCb;
        private ListView _neededLv;
        private TextBox  _neededCountLbl;
        private Button   _neededRefreshBtn;
        private List<RuleDefinition> _neededDefs = new List<RuleDefinition>();
        private bool     _suppressNeededEvent;

        // ── Lookup controls ───────────────────────────────────────────────────────
        private TextBox  _searchTb;
        private Button   _searchBtn;
        private Label    _searchCountLbl;
        private ListView _searchLv;
        private Button   _searchClearBtn;

        // ── Sync controls ─────────────────────────────────────────────────────────
        private Button   _syncImportBtn;
        private Button   _syncExportBtn;
        private Button   _syncQrzBtn;
        private Button   _syncLotwBtn;
        private Button   _syncClubLogBtn;
        private Button   _syncEqslBtn;
        private Label    _srcQrzStatusLbl;
        private Label    _srcLotwStatusLbl;
        private Label    _srcClubLogStatusLbl;
        private Label    _srcEqslStatusLbl;
        private ListView _srcHistoryLv;

        // ── Edit Log controls ─────────────────────────────────────────────────────
        // Local-only data hygiene tab: search/filter, then edit or delete specific rows,
        // or export exactly what's selected before touching it. Never calls out to
        // QRZ/Club Log/LoTW -- see LogbookDb.UpdateQso/DeleteQsos/GetAdifFieldDicts.
        private TextBox  _editCallTb;
        private ComboBox _editSourceCb;
        private TextBox  _editDateFromTb;
        private TextBox  _editDateToTb;
        private Button   _editSearchBtn;
        private Button   _editClearBtn;
        private Label    _editCountLbl;
        private ListView _editLv;
        private Button   _editAddBtn;
        private Button   _editEditBtn;
        private Button   _editDeleteBtn;
        private Button   _editExportBtn;
        private Button   _editRowOrderBtn;
        private List<string> _editLogRowOrder;

        private static readonly Dictionary<string, int> EditLogFieldWidths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "date", 80 }, { "time", 55 }, { "callsign", 90 }, { "band", 55 }, { "mode", 55 },
            { "state", 50 }, { "country", 120 }, { "confirmed", 80 }, { "source", 60 },
        };

        // ── Page constants ────────────────────────────────────────────────────────
        private const int PAGE_MYLOG     = 0;
        private const int PAGE_AWARDS    = 1;
        private const int PAGE_STILLNEED = 2;
        private const int PAGE_LOOKUP    = 3;
        private const int PAGE_EDITLOG   = 4;
        private const int PAGE_SYNC      = 5;

        private static readonly string[] AllBands =
        {
            "(All Bands)", "160m","80m","60m","40m","30m","20m","17m","15m","12m","10m","6m","2m","70cm"
        };

        // ── Constructor ───────────────────────────────────────────────────────────

        public LogbookWindow(IniFile ini, Func<string> qrzApiKey, Func<string> lotwUser, Func<string> lotwPass,
            Func<string> clubLogEmail = null, Func<string> clubLogPassword = null, Func<string> clubLogCallsign = null,
            Func<string> eqslUsername = null, Func<string> eqslPassword = null,
            Action onImportComplete = null,
            HashSet<string> initialActiveAwardRuleIds = null,
            Action<string, bool> onActiveAwardRuleIdsChanged = null,
            Func<string, string> resolveUsState = null,
            Func<bool> isWsjtxConnected = null,
            Func<string> currentBand = null,
            Func<string> currentMode = null,
            Func<string, LookupRecord> lookupCallsign = null,
            Action onQsoLogged = null,
            NexusLogOutbox nexusOutbox = null)
        {
            _ini              = ini;
            _qrzApiKey        = qrzApiKey        ?? (() => "");
            _lotwUser         = lotwUser         ?? (() => "");
            _lotwPass         = lotwPass         ?? (() => "");
            _clubLogEmail     = clubLogEmail     ?? (() => "");
            _clubLogPassword  = clubLogPassword  ?? (() => "");
            _clubLogCallsign  = clubLogCallsign  ?? (() => "");
            _eqslUsername     = eqslUsername     ?? (() => "");
            _eqslPassword     = eqslPassword     ?? (() => "");
            _onImportComplete = onImportComplete;
            _activeAwardRuleIds = initialActiveAwardRuleIds ?? new HashSet<string>();
            _onActiveAwardRuleIdsChanged = onActiveAwardRuleIdsChanged;
            _resolveUsState        = resolveUsState        ?? (call => null);
            _isWsjtxConnected      = isWsjtxConnected      ?? (() => false);
            _currentBand           = currentBand           ?? (() => null);
            _currentMode           = currentMode            ?? (() => null);
            _lookupCallsign        = lookupCallsign         ?? (call => null);
            _onQsoLogged           = onQsoLogged            ?? (() => { });
            _nexusOutbox           = nexusOutbox;

            Text            = "Ham Radio Center — Logbook";
            MinimumSize     = new Size(720, 500);
            Size            = new Size(950, 660);
            StartPosition   = FormStartPosition.CenterScreen;
            ShowInTaskbar   = true;
            KeyPreview      = true;
            FormBorderStyle = FormBorderStyle.Sizable;

            try
            {
                _db = LogbookFactory.Open();
            }
            catch (Exception ex)
            {
                // Show error after window is visible
                this.Load += (s, e) =>
                    SetStatus("Database error: " + ex.Message);
            }

            _editLogRowOrder = Controller.ParseRowOrder(_ini?.Read("editLogRowOrder"), EditLogRowOrderDlg.DefaultFields)
                ?? new List<string>(EditLogRowOrderDlg.DefaultFields);

            BuildUi();

            if (NexusLogbook.Active && !NexusLogbook.LogReady)
                this.Load += (s, e) => SetStatus("Logbook loading. Press F5 when it is ready.");

            if (_nexusOutbox != null)
            {
                _nexusOutbox.DuplicateRefused += OnNexusDuplicateRefused;
                this.Load += (s, e) => ShowHeldDuplicates();
                this.FormClosed += (s, e) => _nexusOutbox.DuplicateRefused -= OnNexusDuplicateRefused;
                _statusTb.KeyDown += StatusTb_KeyDown;
            }

            this.KeyDown    += LogbookWindow_KeyDown;
            this.FormClosed += (s, e) => { _db?.Dispose(); _db = null; };
        }

        // ── UI construction ──────────────────────────────────────────────────────

        private void BuildUi()
        {
            var font  = new Font("Microsoft Sans Serif", 8.25F);
            var hfont = new Font("Microsoft Sans Serif", 9F, FontStyle.Bold);

            // Status bar at the bottom — read-only TextBox so JAWS can focus and read it on demand.
            // AccessibleName=""/AccessibleRole=None -- see MakePage()'s comment: this is the
            // container whose own missing name used to resolve (via WinForms/JAWS's structural
            // "infer a name for this container" fallback) to the Sync tab's unrelated "Import
            // History (most recent first)" label, on every tab, not just Sync.
            var statusPanel = new Panel
            {
                Dock           = DockStyle.Bottom,
                Height         = 22,
                BackColor      = SystemColors.Control,
                AccessibleName = "",
                AccessibleRole = AccessibleRole.None,
            };

            // Close button — a single shared control (not per-tab) so it lands last in tab
            // order on every tab, satisfying "Close appears consistently on all tabs" without
            // duplicating a button inside each TabPage.
            var closeBtn = new Button
            {
                Text           = "Close",
                Dock           = DockStyle.Right,
                Width          = 70,
                Font           = font,
                TabIndex       = 2,
                AccessibleName = "Close",
            };
            closeBtn.Click += (s, e) => this.Close();
            statusPanel.Controls.Add(closeBtn);

            _statusTb = new TextBox
            {
                Dock           = DockStyle.Fill,
                ReadOnly       = true,
                BorderStyle    = BorderStyle.None,
                BackColor      = SystemColors.Control,
                Text           = "Ready",
                Font           = font,
                TabStop        = true,
                TabIndex       = 0,
                AccessibleName = "Status",
                AccessibleDescription = "",
            };
            statusPanel.Controls.Add(_statusTb);

            // Category list -- same role/behavior as Options' _categoryListBox: JAWS announces
            // "Logbook Center categories, My Log, 1 of 6" on entry; Up/Down selects a category.
            _categoryListBox = new ListBox
            {
                Dock           = DockStyle.Left,
                Width          = 150,
                Font           = font,
                IntegralHeight = false,
                AccessibleName = "Logbook Center categories",
                TabIndex       = 1,
            };

            // Pure layout wrapper -- the one category panel actually parented inside it carries
            // its own real AccessibleName (set on each page panel below), same reasoning as
            // Options' _categoryDetailHost: left unnamed so WinForms/JAWS's own "infer a name for
            // this container" fallback never attaches to the host itself.
            _categoryDetailHost = new Panel
            {
                Dock           = DockStyle.Fill,
                AccessibleName = "",
                AccessibleRole = AccessibleRole.None,
                TabIndex       = 2,
            };

            BuildMyLogPage(font, hfont);
            BuildAwardsPage(font, hfont);
            BuildStillNeedPage(font, hfont);
            BuildLookupPage(font, hfont);
            BuildEditLogPage(font, hfont);
            BuildSyncPage(font, hfont);

            string[] pageNames  = { "My Log", "Awards", "Still Need", "Lookup", "Edit Log", "Sync" };
            Panel[]  pagePanels = { _myLogPanel, _awardsPanel, _stillNeedPanel, _lookupPanel, _editLogPanel, _syncPanel };
            for (int i = 0; i < pageNames.Length; i++)
            {
                pagePanels[i].Dock = DockStyle.Fill;
                // Each page panel now carries its own real AccessibleName -- safe (unlike the old
                // TabControl model) because WireCategoryList below Adds/Removes only the ONE
                // currently-selected panel into _categoryDetailHost; there is never a hidden
                // sibling still parented in the tree for JAWS's own "infer a name" fallback to
                // wander into and bleed stale content from (see MakePage()'s own comment for the
                // real, live-confirmed bug this used to cause under the TabControl model).
                pagePanels[i].AccessibleName = pageNames[i];
                pagePanels[i].AccessibleRole = AccessibleRole.Grouping;
                _categoryListBox.Items.Add(pageNames[i]);
            }
            CategoryListNav.Wire(_categoryListBox, _categoryDetailHost, pagePanels.Cast<Control>().ToList());

            _categoryListBox.SelectedIndexChanged += (s, e) => NavigateToPage(_categoryListBox.SelectedIndex);

            Controls.Add(_categoryDetailHost);
            Controls.Add(_categoryListBox);
            Controls.Add(statusPanel);

            this.Load += (s, e) =>
            {
                NavigateToPage(PAGE_MYLOG);
                if (RuleLibrary.LoadErrors.Count > 0)
                    SetStatus($"{RuleLibrary.LoadErrors.Count} Rule Definition load error(s) — see log_rules_errors.txt.");
            };
            // Initial focus goes to the category list itself, set in Shown (not Load, which fires
            // before the window is actually visible/activated) -- same fix and same reasoning as
            // OptionsDlg.OptionsDlg_Load's own Shown-based _categoryListBox.Focus() comment.
            this.Shown += (s, e) => _categoryListBox.Focus();
        }

        // Shows only the page matching _categoryListBox's current selection, hiding the rest --
        // same mechanism CategoryListNav.Wire also gives Options and Contesting.

        // ── Page construction ─────────────────────────────────────────────────────

        private void BuildMyLogPage(Font font, Font hfont)
        {
            _myLogPanel = MakePage();
            // Header controls are built into their own Dock=Top panel, sized to exactly the
            // content height (y), instead of being anchored directly against _myLogPanel while
            // it's still at its tiny unparented default size (see the Dock=Fill fix on
            // _dashRecentLv below for why that combination clips the list).
            // AccessibleName=""/AccessibleRole=None -- pure layout container, see MakePage()'s
            // comment; keeps it out of the accessibility tree as a distinct named region.
            var header = new Panel { Dock = DockStyle.Top, AccessibleName = "", AccessibleRole = AccessibleRole.None };
            int y = 8;

            // Individual focusable read-only TextBoxes — JAWS can Tab to each and read the value.
            AddStatField(header, "Total QSOs",         font, ref y, out _statTotalTb, "Total QSOs");
            AddStatField(header, "LoTW confirmed",     font, ref y, out _statLotwTb,  "LoTW confirmed QSOs");
            AddStatField(header, "QRZ confirmed",      font, ref y, out _statQrzTb,   "QRZ confirmed QSOs");
            AddStatField(header, "Combined confirmed", font, ref y, out _statConfTb,  "Combined confirmed QSOs");
            y += 4;
            AddStatField(header, "WAS",  font, ref y, out _statWasTb,  "WAS worked and confirmed");
            AddStatField(header, "DXCC", font, ref y, out _statDxccTb, "DXCC entities worked and confirmed");
            AddStatField(header, "WAZ",  font, ref y, out _statWazTb,  "WAZ zones worked and confirmed");
            y += 8;

            AddSectionLabel(header, "Upload Status", hfont, ref y);
            AddStatField(header, "QRZ",      font, ref y, out _statUploadQrzTb,      "QRZ upload status");
            AddStatField(header, "Club Log", font, ref y, out _statUploadClubLogTb,  "Club Log upload status");
            AddStatField(header, "LoTW",     font, ref y, out _statUploadLotwTb,     "LoTW upload status");
            AddStatField(header, "HRDLog.net", font, ref y, out _statUploadHrdLogTb, "HRDLog.net upload status");
            y += 8;

            var recentLbl = new Label
            {
                Text     = "Recent QSOs",
                Font     = hfont,
                Location = new Point(8, y),
                AutoSize = true,
            };
            header.Controls.Add(recentLbl);
            y += 22;
            header.Height = y;

            _dashRecentLv = MakeListView(font);
            _dashRecentLv.Dock = DockStyle.Fill;
            _dashRecentLv.Columns.Add("Date",      80);
            _dashRecentLv.Columns.Add("UTC",       60);
            _dashRecentLv.Columns.Add("Callsign",  90);
            _dashRecentLv.Columns.Add("Band",      60);
            _dashRecentLv.Columns.Add("Mode",      60);
            _dashRecentLv.Columns.Add("Country",  130);
            _dashRecentLv.Columns.Add("Confirmed", 80);
            _dashRecentLv.AccessibleName = "Recent QSOs";
            // MakeListView()'s shared default TabIndex (10) collides with this page's own
            // auto-numbered stat fields -- every other page using MakeListView() overrides it,
            // this one didn't, so Tab order landed the list between WAS and DXCC instead of
            // after every stat field (found 2026-07-09, confirmed by tracing real Tab-key
            // focus order). 30 is safely past the last auto-numbered control on this page.
            _dashRecentLv.TabIndex = 30;
            // Fill added before Top so Dock=Top can carve the header's space out of it.
            _myLogPanel.Controls.Add(_dashRecentLv);
            _myLogPanel.Controls.Add(header);
        }

        private void BuildSyncPage(Font font, Font hfont)
        {
            _syncPanel = MakePage();
            var header = new Panel { Dock = DockStyle.Top, AccessibleName = "", AccessibleRole = AccessibleRole.None };
            int y = 8;

            _syncImportBtn = new Button
            {
                Text           = "Import ADIF...",
                AccessibleName = "Import ADIF file",
                Size           = new Size(120, 26),
                Location       = new Point(8, y),
                Font           = font,
                TabIndex       = 1,
            };
            _syncImportBtn.Click += ImportBtn_Click;

            _syncQrzBtn = new Button
            {
                Text           = "Download from QRZ",
                AccessibleName = "Download from QRZ Logbook",
                Size           = new Size(140, 26),
                Location       = new Point(134, y),
                Font           = font,
                TabIndex       = 2,
                Enabled        = !string.IsNullOrWhiteSpace(_qrzApiKey()),
            };
            _syncQrzBtn.Click += QrzRefreshBtn_Click;

            _syncLotwBtn = new Button
            {
                Text           = "Download from LoTW",
                AccessibleName = "Download from LoTW",
                Size           = new Size(142, 26),
                Location       = new Point(280, y),
                Font           = font,
                TabIndex       = 3,
                Enabled        = !string.IsNullOrWhiteSpace(_lotwUser()) && !string.IsNullOrWhiteSpace(_lotwPass()),
            };
            _syncLotwBtn.Click += LoTWRefreshBtn_Click;

            _syncClubLogBtn = new Button
            {
                Text           = "Download from Club Log",
                AccessibleName = "Download from Club Log",
                Size           = new Size(160, 26),
                Location       = new Point(428, y),
                Font           = font,
                TabIndex       = 4,
                Enabled        = !string.IsNullOrWhiteSpace(_clubLogEmail()) &&
                                  !string.IsNullOrWhiteSpace(_clubLogPassword()) &&
                                  !string.IsNullOrWhiteSpace(_clubLogCallsign()),
            };
            _syncClubLogBtn.Click += ClubLogRefreshBtn_Click;

            header.Controls.AddRange(new Control[] { _syncImportBtn, _syncQrzBtn, _syncLotwBtn, _syncClubLogBtn });
            y += 34;

            // Own row: the first row (Import/QRZ/LoTW/Club Log) is already close to this
            // window's MinimumSize width (720) -- a 5th button on the same row would clip
            // when resized down. Only a match-only reconciliation against Jimmy's local
            // logbook (EqslReconciler), not a full import -- see EqslRefreshBtn_Click.
            _syncEqslBtn = new Button
            {
                Text           = "Download from eQSL",
                AccessibleName = "Download and reconcile eQSL confirmations",
                Size           = new Size(150, 26),
                Location       = new Point(8, y),
                Font           = font,
                TabIndex       = 5,
                Enabled        = !string.IsNullOrWhiteSpace(_eqslUsername()) && !string.IsNullOrWhiteSpace(_eqslPassword()),
            };
            _syncEqslBtn.Click += EqslRefreshBtn_Click;
            header.Controls.Add(_syncEqslBtn);
            y += 34;

            _syncExportBtn = new Button
            {
                Text           = "Export ADIF...",
                AccessibleName = "Export all QSOs to ADIF file",
                Size           = new Size(120, 26),
                Location       = new Point(8, y),
                Font           = font,
                TabIndex       = 6,
            };
            _syncExportBtn.Click += (s, e) => ExportAdif(null);
            header.Controls.Add(_syncExportBtn);
            y += 34;

            AddSectionLabel(header, "QRZ Logbook", hfont, ref y);
            _srcQrzStatusLbl = AddInfoLabel(header, "Status: not configured", font, ref y);
            y += 4;

            AddSectionLabel(header, "LoTW", hfont, ref y);
            _srcLotwStatusLbl = AddInfoLabel(header, "Status: not configured", font, ref y);
            y += 4;

            AddSectionLabel(header, "eQSL", hfont, ref y);
            _srcEqslStatusLbl = AddInfoLabel(header, "Status: not configured", font, ref y);
            y += 4;

            AddSectionLabel(header, "Club Log", hfont, ref y);
            _srcClubLogStatusLbl = AddInfoLabel(header, "Status: not configured", font, ref y);
            y += 12;

            var histLbl = new Label
            {
                Text     = "Import History (most recent first)",
                Font     = hfont,
                Location = new Point(8, y),
                AutoSize = true,
            };
            header.Controls.Add(histLbl);
            y += 22;
            header.Height = y;

            _srcHistoryLv = MakeListView(font);
            _srcHistoryLv.Dock = DockStyle.Fill;
            _srcHistoryLv.Columns.Add("Date/Time",       135);
            _srcHistoryLv.Columns.Add("Source",           65);
            _srcHistoryLv.Columns.Add("New",              50);
            _srcHistoryLv.Columns.Add("Newly Confirmed",  95);
            _srcHistoryLv.Columns.Add("Corrected",        70);
            _srcHistoryLv.Columns.Add("Total",            55);
            _srcHistoryLv.Columns.Add("Errors",          170);
            _srcHistoryLv.AccessibleName = "Import history";
            _syncPanel.Controls.Add(_srcHistoryLv);
            _syncPanel.Controls.Add(header);
        }

        private void BuildAwardsPage(Font font, Font hfont)
        {
            _awardsPanel = MakePage();

            // Two stacked Dock=Top panels, not one: manageBtn/refreshBtn sit visually above
            // the list (old Y=34 vs list's old Y=66) but were already deliberately given a
            // TabIndex (4,5) placing them AFTER the list (TabIndex=3) in tab order. A single
            // merged header panel would force them back before the list, changing existing
            // keyboard-nav behavior -- splitting into topRow (TabIndex 0, before the list) and
            // buttonRow (TabIndex 6, after the list) reproduces the original order exactly.
            // AccessibleName=""/AccessibleRole=None -- pure layout container, see MakePage().
            var topRow = new Panel { Dock = DockStyle.Top, Height = 34, AccessibleName = "", AccessibleRole = AccessibleRole.None };

            var viewLbl = new Label
            {
                Text     = "Award:",
                Font     = font,
                Location = new Point(8, 10),
                AutoSize = true,
            };
            topRow.Controls.Add(viewLbl);

            _awardsViewCb = new ComboBox
            {
                DropDownStyle  = ComboBoxStyle.DropDownList,
                Font           = font,
                Location       = new Point(56, 7),
                Size           = new Size(300, 21),
                TabIndex       = 1,
                AccessibleName = "Award selector",
            };
            // Items are populated from RuleLibrary.Definitions in PopulateAwardsCombo() —
            // dropping a new .ini file into RuleDefinitions adds it here with no code change.
            _awardsViewCb.SelectedIndexChanged += (s, e) => { if (!_suppressAwardsEvent) PopulateAwards(); };
            topRow.Controls.Add(_awardsViewCb);

            // Read-only TextBox, not a Label -- a plain Label is never reachable by Tab,
            // so JAWS/NVDA users tabbing through this page would never hear the progress
            // summary at all (found 2026-07-12: a blind JAWS user's screen-reader
            // transcript jumped straight from the combo box to the list, confirming this
            // was genuinely unreachable, not just easy to miss). Matches the same
            // focusable-readonly-TextBox pattern the My Log tab's stat fields already use.
            _awardsProgressLbl = new TextBox
            {
                Text           = "",
                Font           = font,
                Location       = new Point(366, 8),
                Size           = new Size(340, 20),
                ReadOnly       = true,
                BorderStyle    = BorderStyle.None,
                BackColor      = SystemColors.Control,
                TabStop        = true,
                TabIndex       = 2,
                AccessibleName = "Award progress summary",
            };
            topRow.Controls.Add(_awardsProgressLbl);

            // AccessibleName=""/AccessibleRole=None -- pure layout container, see MakePage().
            var buttonRow = new Panel { Dock = DockStyle.Top, Height = 32, TabIndex = 6, AccessibleName = "", AccessibleRole = AccessibleRole.None };

            _awardsManageBtn = new Button
            {
                Text           = "Manage Rule Definitions...",
                Font           = font,
                Location       = new Point(8, 2),
                Size           = new Size(180, 24),
                TabIndex       = 4,
                AccessibleName = "Manage Rule Definitions",
            };
            _awardsManageBtn.Click += (s, e) => OpenRuleDefinitionManager();
            buttonRow.Controls.Add(_awardsManageBtn);

            _awardsRefreshBtn = new Button
            {
                Text           = "Refresh",
                Font           = font,
                Location       = new Point(196, 2),
                Size           = new Size(90, 24),
                TabIndex       = 5,
                AccessibleName = "Refresh award progress",
            };
            _awardsRefreshBtn.Click += (s, e) => PopulateAwards();
            buttonRow.Controls.Add(_awardsRefreshBtn);

            _awardsLv = MakeListView(font);
            _awardsLv.Dock = DockStyle.Fill;
            _awardsLv.TabIndex = 3;
            _awardsLv.AccessibleName = "Award details";

            // topRow added before buttonRow so it stacks above it (Y0-34 then Y34-66).
            _awardsPanel.Controls.Add(_awardsLv);
            _awardsPanel.Controls.Add(topRow);
            _awardsPanel.Controls.Add(buttonRow);
        }

        // Opens the Rule Definition Manager and, if anything changed, refreshes
        // every view that reads RuleLibrary.Definitions: this window's Awards
        // and Still Need combos, plus (via _onImportComplete) the Controller's
        // HRC cache and Still Need live-tagging cache.
        private void OpenRuleDefinitionManager()
        {
            using (var mgr = new RuleDefinitionManagerDlg())
            {
                mgr.ShowDialog(this);
                if (mgr.RulesChanged)
                {
                    PopulateAwardsCombo();
                    PopulateNeededAwardsList();
                    _onImportComplete?.Invoke();
                }
            }
        }

        private void BuildStillNeedPage(Font font, Font hfont)
        {
            _stillNeedPanel = MakePage();
            // Height grown from 115 -- see _neededCountLbl's own comment below for why.
            var header = new Panel { Dock = DockStyle.Top, Height = 150, AccessibleName = "", AccessibleRole = AccessibleRole.None };

            var typeLbl = new Label
            {
                Text     = "Awards:",
                Font     = font,
                Location = new Point(8, 10),
                AutoSize = true,
            };
            header.Controls.Add(typeLbl);

            // One list serves two purposes: moving through it (arrow keys) picks which
            // award's checklist is shown below, and checking/unchecking an item (Space)
            // toggles that award's active live-tracking independently of which one is
            // currently being browsed -- any number can be checked at once. Replaces the
            // former award combo box + separate "Actively track" checkbox pair so both
            // actions live in one control instead of needing a Tab stop each.
            _neededAwardsClb = new CheckedListBox
            {
                Font           = font,
                Location       = new Point(56, 7),
                Size           = new Size(300, 100),
                TabIndex       = 1,
                CheckOnClick   = false,
                AccessibleName = "Still Needed awards",
            };
            // CheckOnClick=false + manual toggle on MouseUp, not the built-in CheckOnClick=true --
            // found live, 2026-09-15: WinForms' own CheckOnClick only toggles the box when the
            // click does NOT also change the selection. Clicking a row that isn't already selected
            // (the common case -- arrow-keying/clicking through the list to find an award, then
            // clicking its box) selects it but silently eats the check-toggle, so the very
            // interaction this control exists for could look like it worked (row highights) while
            // never actually calling _onActiveAwardRuleIdsChanged or reaching activeAwardRuleIds/
            // the ini at all. Toggling explicitly here fires on every left-click regardless of
            // whether that same click also changed the selection. Keyboard (Space) already toggles
            // correctly without CheckOnClick -- untouched by this change, so JAWS/NVDA users were
            // never affected.
            _neededAwardsClb.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                int index = _neededAwardsClb.IndexFromPoint(e.Location);
                if (index < 0 || index >= _neededAwardsClb.Items.Count) return;
                _neededAwardsClb.SetItemChecked(index, !_neededAwardsClb.GetItemChecked(index));
            };
            // Items are populated from RuleLibrary.Definitions in PopulateNeededAwardsList() --
            // dropping a new .ini file into RuleDefinitions adds it here with no code change.
            _neededAwardsClb.SelectedIndexChanged += (s, e) => { if (!_suppressNeededEvent) PopulateNeeded(); };
            _neededAwardsClb.ItemCheck += (s, e) =>
            {
                if (_suppressNeededEvent) return;
                if (e.Index < 0 || e.Index >= _neededDefs.Count) return;
                var def = _neededDefs[e.Index];
                if (e.NewValue == CheckState.Checked && !RuleEngine.SupportsLiveTag(def))
                {
                    e.NewValue = CheckState.Unchecked;
                    SetStatus($"{def.Name} can't be actively tracked -- no fixed checklist is available live during decoding.");
                    return;
                }
                _onActiveAwardRuleIdsChanged?.Invoke(def.Id, e.NewValue == CheckState.Checked);
            };
            header.Controls.Add(_neededAwardsClb);

            var bandLbl = new Label
            {
                Text     = "Band:",
                Font     = font,
                Location = new Point(366, 10),
                AutoSize = true,
            };
            header.Controls.Add(bandLbl);

            _neededBandCb = new ComboBox
            {
                DropDownStyle  = ComboBoxStyle.DropDownList,
                Font           = font,
                Location       = new Point(402, 7),
                Size           = new Size(90, 21),
                TabIndex       = 2,
                AccessibleName = "Band filter",
            };
            _neededBandCb.Items.AddRange(AllBands);
            _neededBandCb.SelectedIndex = 0;
            _neededBandCb.SelectedIndexChanged += (s, e) => { if (!_suppressNeededEvent) PopulateNeeded(); };
            header.Controls.Add(_neededBandCb);

            _neededRefreshBtn = new Button
            {
                Text           = "Refresh",
                Font           = font,
                Location       = new Point(502, 6),
                Size           = new Size(100, 23),
                TabIndex       = 4,
                AccessibleName = "Refresh needed list",
            };
            _neededRefreshBtn.Click += (s, e) => PopulateNeeded();
            header.Controls.Add(_neededRefreshBtn);

            // Was Location(500,8) Size(200,20), single-line -- neededRefreshBtn starts at
            // x=600, so this box's own declared width (ending at x=700) already overlapped it
            // by 100px before any text-length problem, and its status text (e.g. "This rule
            // does not have a fixed still-needed checklist. (Live decode tagging is
            // unavailable for this award.)") is far longer than 200px besides. Moved to its
            // own full-width row below the awards checklist/band/refresh row (header's Height
            // grew to fit it), Multiline/WordWrap so the complete message is always readable,
            // Anchor=Right so it keeps using the page's full width if the window is widened.
            // Still a read-only TextBox, not a Label -- see the same fix on the Awards tab's
            // _awardsProgressLbl for why a plain Label is unreachable by Tab/screen reader.
            _neededCountLbl = new TextBox
            {
                Text           = "",
                Font           = font,
                Location       = new Point(8, 111),
                Size           = new Size(680, 34),
                Anchor         = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Multiline      = true,
                WordWrap       = true,
                ReadOnly       = true,
                BorderStyle    = BorderStyle.None,
                BackColor      = SystemColors.Control,
                TabStop        = true,
                TabIndex       = 3,
                AccessibleName = "Needed entries",
            };
            header.Controls.Add(_neededCountLbl);

            _neededLv = MakeListView(font);
            _neededLv.Dock = DockStyle.Fill;
            _neededLv.TabIndex = 5;
            _neededLv.AccessibleName = "Needed items";
            _stillNeedPanel.Controls.Add(_neededLv);
            _stillNeedPanel.Controls.Add(header);
        }

        private void BuildLookupPage(Font font, Font hfont)
        {
            _lookupPanel = MakePage();
            var header = new Panel { Dock = DockStyle.Top, Height = 36, AccessibleName = "", AccessibleRole = AccessibleRole.None };

            var searchLbl = new Label
            {
                Text     = "Callsign:",
                Font     = font,
                Location = new Point(8, 11),
                AutoSize = true,
            };
            header.Controls.Add(searchLbl);

            _searchTb = new TextBox
            {
                Font           = font,
                Location       = new Point(68, 8),
                Size           = new Size(140, 20),
                TabIndex       = 1,
                AccessibleName = "Callsign search",
            };
            _searchTb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoSearch(); } };
            header.Controls.Add(_searchTb);

            _searchBtn = new Button
            {
                Text           = "Search",
                AccessibleName = "Search for callsign",
                Font           = font,
                Location       = new Point(214, 7),
                Size           = new Size(70, 23),
                TabIndex       = 2,
            };
            _searchBtn.Click += (s, e) => DoSearch();
            header.Controls.Add(_searchBtn);

            _searchCountLbl = new Label
            {
                Text     = "",
                Font     = font,
                Location = new Point(292, 11),
                AutoSize = true,
                AccessibleName = "Search result count",
            };
            header.Controls.Add(_searchCountLbl);

            // Bottom-docked footer, TabIndex above the list, so Clear stays after the list
            // in tab order exactly as it was when it was merely Bottom-anchored.
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 31, TabIndex = 5, AccessibleName = "", AccessibleRole = AccessibleRole.None };
            _searchClearBtn = new Button
            {
                Text           = "Clear",
                AccessibleName = "Clear search results",
                Font           = font,
                Location       = new Point(8, 4),
                Size           = new Size(70, 23),
                TabIndex       = 4,
            };
            _searchClearBtn.Click += (s, e) => ClearSearch();
            footer.Controls.Add(_searchClearBtn);

            _searchLv = MakeListView(font);
            _searchLv.Dock = DockStyle.Fill;
            _searchLv.TabIndex = 3;
            _searchLv.Columns.Add("Date",      80);
            _searchLv.Columns.Add("UTC",       55);
            _searchLv.Columns.Add("Callsign",  90);
            _searchLv.Columns.Add("Band",      55);
            _searchLv.Columns.Add("Mode",      55);
            _searchLv.Columns.Add("State",     50);
            _searchLv.Columns.Add("Country",  120);
            _searchLv.Columns.Add("Confirmed", 80);
            _searchLv.Columns.Add("Source",    60);
            _searchLv.AccessibleName = "Search results";

            _lookupPanel.Controls.Add(_searchLv);
            _lookupPanel.Controls.Add(header);
            _lookupPanel.Controls.Add(footer);
        }

        private void ClearSearch()
        {
            _searchTb.Text = "";
            _searchLv.Items.Clear();
            _searchCountLbl.Text = "";
            _searchTb.Focus();
        }

        private void BuildEditLogPage(Font font, Font hfont)
        {
            _editLogPanel = MakePage();
            var header = new Panel { Dock = DockStyle.Top, Height = 86, AccessibleName = "", AccessibleRole = AccessibleRole.None };

            var callLbl = new Label { Text = "Callsign:", Font = font, Location = new Point(8, 11), AutoSize = true };
            header.Controls.Add(callLbl);

            _editCallTb = new TextBox
            {
                Font           = font,
                Location       = new Point(68, 8),
                Size           = new Size(120, 20),
                TabIndex       = 1,
                AccessibleName = "Callsign filter",
            };
            _editCallTb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoEditSearch(); } };
            header.Controls.Add(_editCallTb);

            var sourceLbl = new Label { Text = "Source:", Font = font, Location = new Point(196, 11), AutoSize = true };
            header.Controls.Add(sourceLbl);

            _editSourceCb = new ComboBox
            {
                Font           = font,
                Location       = new Point(244, 8),
                Size           = new Size(110, 21),
                DropDownStyle  = ComboBoxStyle.DropDownList,
                TabIndex       = 2,
                AccessibleName = "Source filter",
            };
            _editSourceCb.Items.Add("(Any)");
            _editSourceCb.Items.AddRange(QsoRecord.KnownSources);
            _editSourceCb.SelectedIndex = 0;
            header.Controls.Add(_editSourceCb);

            var dateFromLbl = new Label { Text = "Date from:", Font = font, Location = new Point(8, 37), AutoSize = true };
            header.Controls.Add(dateFromLbl);

            _editDateFromTb = new TextBox
            {
                Font           = font,
                Location       = new Point(70, 34),
                Size           = new Size(80, 20),
                TabIndex       = 3,
                AccessibleName = "Date from, format year month day, optional",
            };
            _editDateFromTb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoEditSearch(); } };
            header.Controls.Add(_editDateFromTb);

            var dateToLbl = new Label { Text = "to:", Font = font, Location = new Point(156, 37), AutoSize = true };
            header.Controls.Add(dateToLbl);

            _editDateToTb = new TextBox
            {
                Font           = font,
                Location       = new Point(176, 34),
                Size           = new Size(80, 20),
                TabIndex       = 4,
                AccessibleName = "Date to, format year month day, optional",
            };
            _editDateToTb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoEditSearch(); } };
            header.Controls.Add(_editDateToTb);

            _editSearchBtn = new Button
            {
                Text           = "Search",
                AccessibleName = "Search",
                Font           = font,
                Location       = new Point(264, 33),
                Size           = new Size(70, 23),
                TabIndex       = 5,
            };
            _editSearchBtn.Click += (s, e) => DoEditSearch();
            header.Controls.Add(_editSearchBtn);

            _editClearBtn = new Button
            {
                Text           = "Clear",
                AccessibleName = "Clear filters",
                Font           = font,
                Location       = new Point(340, 33),
                Size           = new Size(70, 23),
                TabIndex       = 6,
            };
            _editClearBtn.Click += (s, e) => ClearEditLog();
            header.Controls.Add(_editClearBtn);

            _editCountLbl = new Label
            {
                Text           = "",
                Font           = font,
                Location       = new Point(8, 62),
                AutoSize       = true,
                AccessibleName = "Result count",
            };
            header.Controls.Add(_editCountLbl);

            _editRowOrderBtn = new Button
            {
                Text           = "Row Order...",
                AccessibleName = "Choose Edit Log column order",
                Font           = font,
                Location       = new Point(416, 33),
                Size           = new Size(90, 23),
                TabIndex       = 7,
            };
            _editRowOrderBtn.Click += RowOrderBtn_Click;
            header.Controls.Add(_editRowOrderBtn);

            // Bottom-docked footer, TabIndex above the list, so the four action buttons stay
            // after the list in tab order exactly as they were when merely Bottom-anchored.
            // AccessibleName=""/AccessibleRole=None -- pure layout container, see MakePage().
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 31, TabIndex = 13, AccessibleName = "", AccessibleRole = AccessibleRole.None };

            _editAddBtn = new Button
            {
                Text           = "Add New...",
                AccessibleName = "Add a new QSO",
                Font           = font,
                Location       = new Point(8, 4),
                Size           = new Size(90, 23),
                TabIndex       = 9,
            };
            _editAddBtn.Click += AddQsoBtn_Click;
            footer.Controls.Add(_editAddBtn);

            _editEditBtn = new Button
            {
                Text           = "Edit...",
                AccessibleName = "Edit selected QSO",
                Font           = font,
                Location       = new Point(104, 4),
                Size           = new Size(70, 23),
                TabIndex       = 10,
                Enabled        = false,
            };
            _editEditBtn.Click += EditQsoBtn_Click;
            footer.Controls.Add(_editEditBtn);

            _editDeleteBtn = new Button
            {
                Text           = "Delete...",
                AccessibleName = "Delete selected QSOs",
                Font           = font,
                Location       = new Point(180, 4),
                Size           = new Size(80, 23),
                TabIndex       = 11,
                Enabled        = false,
            };
            _editDeleteBtn.Click += DeleteQsosBtn_Click;
            footer.Controls.Add(_editDeleteBtn);

            _editExportBtn = new Button
            {
                Text           = "Export Selected...",
                AccessibleName = "Export selected QSOs to ADIF",
                Font           = font,
                Location       = new Point(266, 4),
                Size           = new Size(130, 23),
                TabIndex       = 12,
                Enabled        = false,
            };
            _editExportBtn.Click += ExportSelectedBtn_Click;
            footer.Controls.Add(_editExportBtn);

            _editLv = MakeListView(font);
            _editLv.Dock        = DockStyle.Fill;
            _editLv.MultiSelect = true;
            _editLv.TabIndex    = 8;
            RebuildEditLogColumns();
            _editLv.AccessibleName = "Edit Log results";
            _editLv.SelectedIndexChanged += (s, e) => UpdateEditLogButtons();

            _editLogPanel.Controls.Add(_editLv);
            _editLogPanel.Controls.Add(header);
            _editLogPanel.Controls.Add(footer);
        }

        private void ClearEditLog()
        {
            _editCallTb.Text = "";
            _editSourceCb.SelectedIndex = 0;
            _editDateFromTb.Text = "";
            _editDateToTb.Text = "";
            _editLv.Items.Clear();
            _editCountLbl.Text = "";
            UpdateEditLogButtons();
            _editCallTb.Focus();
        }

        private void UpdateEditLogButtons()
        {
            int n = _editLv.SelectedItems.Count;
            _editEditBtn.Enabled   = n == 1;
            _editDeleteBtn.Enabled = n >= 1;
            _editExportBtn.Enabled = n >= 1;
        }

        // Normalizes a user-typed date filter (accepts "2026-07-12" or "20260712")
        // to the qso_date column's own bare YYYYMMDD form. Blank/unparseable -> "".
        private static string NormalizeDateFilter(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            string digits = new string(s.Where(char.IsDigit).ToArray());
            return digits.Length >= 8 ? digits.Substring(0, 8) : "";
        }

        private void DoEditSearch()
        {
            if (_db == null) return;
            try
            {
                string call   = _editCallTb.Text.Trim();
                string source = _editSourceCb.SelectedIndex > 0 ? (string)_editSourceCb.SelectedItem : null;
                string dFrom  = NormalizeDateFilter(_editDateFromTb.Text);
                string dTo    = NormalizeDateFilter(_editDateToTb.Text);

                var results = _db.SearchQsos(call, source, dFrom, dTo);
                _editLv.Items.Clear();
                foreach (var q in results)
                {
                    var item = new ListViewItem(GetEditLogFieldValue(q, _editLogRowOrder[0])) { Tag = q.Id };
                    for (int i = 1; i < _editLogRowOrder.Count; i++)
                        item.SubItems.Add(GetEditLogFieldValue(q, _editLogRowOrder[i]));
                    _editLv.Items.Add(item);
                }
                _editCountLbl.Text = results.Count == 0
                    ? "No QSOs found."
                    : $"{results.Count} QSO{(results.Count == 1 ? "" : "s")} found.";
                UpdateEditLogButtons();
            }
            catch (Exception ex) { SetStatus("Edit Log search error: " + ex.Message); }
        }

        private string GetEditLogFieldValue(QsoRecord q, string field)
        {
            switch (field.ToLowerInvariant())
            {
                case "date":      return FormatDate(q.QsoDate);
                case "time":      return FormatTime(q.TimeOn);
                case "callsign":  return q.Callsign;
                case "band":      return q.Band;
                case "mode":      return q.Mode;
                case "state":     return q.State;
                case "country":   return q.Country;
                case "confirmed": return ConfirmedText(q.LotwQslRcvd, q.QrzQslRcvd);
                case "source":    return q.Source;
                default:          return "";
            }
        }

        private void RebuildEditLogColumns()
        {
            _editLv.Columns.Clear();
            foreach (var field in _editLogRowOrder)
            {
                string label = EditLogRowOrderDlg.FieldLabels.TryGetValue(field, out var l) ? l : field;
                int width = EditLogFieldWidths.TryGetValue(field, out var w) ? w : 80;
                _editLv.Columns.Add(label, width);
            }
        }

        private void RowOrderBtn_Click(object sender, EventArgs e)
        {
            using (var dlg = new EditLogRowOrderDlg(_editLogRowOrder) { Owner = this })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.SelectedFields == null) return;

                _editLogRowOrder = dlg.SelectedFields;
                _ini?.Write("editLogRowOrder", string.Join(",", _editLogRowOrder));
                RebuildEditLogColumns();
                if (_editLv.Items.Count > 0) DoEditSearch();
            }
        }

        private List<int> SelectedEditIds() =>
            _editLv.SelectedItems.Cast<ListViewItem>().Select(i => (int)i.Tag).ToList();

        // Invoked by Controller's "Add Manual QSO" hotkey -- jumps straight to the Edit
        // Log tab and opens the same Add New QSO dialog as its "Add New..." button.
        public void OpenAddQsoDialog()
        {
            NavigateToPage(PAGE_EDITLOG);
            AddQsoBtn_Click(this, EventArgs.Empty);
        }

        // Manually hand-logs a QSO Jimmy never heard over WSJT-X -- e.g. CW or Phone,
        // worked on a separate rig/program. Local-only, same as Edit/Delete (see
        // LogbookDb.Upsert / project memory "Logbook Edit Log tab" for why). State/
        // country/grid aren't looked up here directly -- EditQsoDlg does that itself
        // (offline only) once a callsign is typed, filling only whatever's still blank.
        // Band/Mode default to whatever WSJT-X currently has on the air, if connected --
        // just a starting suggestion; the Mode field can be freely overtyped (e.g. "SSB")
        // for a QSO made outside WSJT-X entirely.
        private void AddQsoBtn_Click(object sender, EventArgs e)
        {
            if (_db == null) return;
            bool live = _isWsjtxConnected();
            var blank = new QsoRecord
            {
                QsoDate = DateTime.UtcNow.ToString("yyyyMMdd"),
                TimeOn  = DateTime.UtcNow.ToString("HHmm"),
                Band    = live ? (_currentBand() ?? "") : "",
                Mode    = live ? (_currentMode() ?? "") : "",
            };

            // Writes one QSO per call -- EditQsoDlg calls this on every Submit, not just once
            // at close, so a contest/pileup operator can keep the dialog open and log contact
            // after contact. Returns null on success, or an error string (e.g. duplicate) that
            // the dialog shows inline without clearing the operator's in-progress entry.
            string SubmitNewQso(QsoRecord r)
            {
                try
                {
                    string dedupKey = AdifImporter.BuildDedupKey(r.Callsign, r.Band, r.Mode, r.QsoDate, r.TimeOn);
                    _db.Upsert(r.Callsign, r.Band, r.Mode, r.QsoDate, r.TimeOn, r.TimeOff,
                        0, r.RstSent, r.RstRcvd, r.State, r.Country, 0, 0,
                        r.Grid, r.Name, r.Comment, "",
                        "", "", "",
                        "", "", "", "",
                        "MANUAL", "", dedupKey,
                        "", 0, "", "", "", "", "", "", "", "",
                        "", "");
                    // While Nexus keeps the logbook, say exactly what happened: a duplicate Nexus
                    // refused is shown by the Status field's own held-duplicate message (D2), and
                    // a contact still on its way is said to be queued, never "added".
                    string state = (_db as NexusLogbookService)?.LastLogState ?? "saved";
                    if (state == "saved" || state == "already") SetStatus($"Added {r.Callsign}.");
                    else if (state == "queued") SetStatus($"{r.Callsign} queued; it will be saved when the logbook is available.");
                    DoEditSearch();
                    return null;
                }
                catch (Exception ex)
                {
                    return "Add failed: " + ex.Message +
                        " (a QSO with this callsign/band/mode/date/time may already exist)";
                }
            }

            using (var dlg = new EditQsoDlg(blank, "Add New QSO", _lookupCallsign, isNewEntry: true,
                onSubmit: SubmitNewQso, onLogged: _onQsoLogged) { Owner = this })
            {
                dlg.ShowDialog(this);
            }
        }

        private void EditQsoBtn_Click(object sender, EventArgs e)
        {
            if (_db == null || _editLv.SelectedItems.Count != 1) return;
            int id = (int)_editLv.SelectedItems[0].Tag;
            var q = _db.GetQso(id);
            if (q == null) { SetStatus("That QSO no longer exists — refreshing."); DoEditSearch(); return; }

            string SubmitEdit(QsoRecord r)
            {
                try
                {
                    bool ok = _db.UpdateQso(id, r.Callsign, r.Band, r.Mode, r.QsoDate, r.TimeOn, r.TimeOff,
                        r.State, r.Country, r.Grid, r.Name, r.RstSent, r.RstRcvd, r.Comment);
                    SetStatus(ok ? $"Updated {r.Callsign}." : "No changes were saved.");
                    DoEditSearch();
                    return null;
                }
                catch (Exception ex)
                {
                    return "Edit failed: " + ex.Message +
                        " (a QSO with this callsign/band/mode/date/time may already exist)";
                }
            }

            using (var dlg = new EditQsoDlg(q, lookupCallsign: _lookupCallsign, onSubmit: SubmitEdit) { Owner = this })
            {
                dlg.ShowDialog(this);
            }
        }

        private void DeleteQsosBtn_Click(object sender, EventArgs e)
        {
            if (_db == null || _editLv.SelectedItems.Count == 0) return;
            var ids = SelectedEditIds();

            var sample = _editLv.SelectedItems.Cast<ListViewItem>().Take(5)
                .Select(i => $"{i.SubItems[0].Text}  {i.SubItems[2].Text}  {i.SubItems[3].Text}/{i.SubItems[4].Text}");
            string sampleText = string.Join("\n", sample);
            if (ids.Count > 5) sampleText += $"\n… and {ids.Count - 5} more";

            using (var confDlg = new ConfirmDlg
            {
                Owner = this,
                text  = $"Delete {ids.Count} QSO(s) from Jimmy's local logbook?\n\n{sampleText}\n\n" +
                        "This only removes them locally -- it does not contact QRZ, Club Log, or LoTW, " +
                        "and cannot be undone.",
            })
            {
                confDlg.ShowDialog(this);
                if (confDlg.DialogResult != DialogResult.Yes) return;
            }

            try
            {
                int n = _db.DeleteQsos(ids);
                SetStatus($"Deleted {n} QSO(s) from the local logbook.");
                DoEditSearch();
            }
            catch (Exception ex) { SetStatus("Delete failed: " + ex.Message); }
        }

        private void ExportSelectedBtn_Click(object sender, EventArgs e)
        {
            if (_db == null || _editLv.SelectedItems.Count == 0) return;
            ExportAdif(SelectedEditIds());
        }

        // ids == null exports every QSO in the database (used by the Sync tab's
        // "Export ADIF..." button); a non-null list exports just those rows.
        private void ExportAdif(List<int> ids)
        {
            if (_db == null) return;

            List<string> sources;
            using (var sourceDlg = new ExportSourceFilterDlg())
            {
                if (sourceDlg.ShowDialog(this) != DialogResult.OK) return;
                sources = sourceDlg.SelectedSources;
            }

            using (var dlg = new SaveFileDialog
            {
                Title    = "Export ADIF File",
                Filter   = "ADIF files (*.adi)|*.adi|All files (*.*)|*.*",
                FileName = $"jimmy_export_{DateTime.Now:yyyyMMdd_HHmmss}.adi",
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var fields = _db.GetAdifFieldDicts(ids, sources);
                    File.WriteAllText(dlg.FileName, AdifExporter.BuildFile(fields));
                    SetStatus($"Exported {fields.Count:N0} QSO(s) to {dlg.FileName}.");
                }
                catch (Exception ex) { SetStatus("Export error: " + ex.Message); }
            }
        }

        // ── Navigation ────────────────────────────────────────────────────────────

        private void NavigateToPage(int page)
        {
            Panel[] pages = { _myLogPanel, _awardsPanel, _stillNeedPanel, _lookupPanel, _editLogPanel, _syncPanel };
            if (page >= 0 && page < pages.Length)
                _activePage = pages[page];

            // Keep the category list in sync when called programmatically
            if (_categoryListBox != null && _categoryListBox.SelectedIndex != page)
                _categoryListBox.SelectedIndex = page;

            switch (page)
            {
                case PAGE_MYLOG:     PopulateMyLog();  break;
                case PAGE_AWARDS:    PopulateAwards(); break;
                case PAGE_STILLNEED: PopulateNeeded(); break;
                case PAGE_LOOKUP:    break;
                case PAGE_EDITLOG:   break;
                case PAGE_SYNC:      PopulateSync();   break;
            }
        }

        // Root cause (found live, 2026-09-18, via a real Form.SelectNextControl walk -- not
        // guessed): each page's main ListView is Dock=Fill and MUST be added to its panel
        // before its header (Dock=Top) for the header's own carve-out to size correctly --
        // reversing that add order was verified live to make Dock=Fill ignore the header
        // entirely and overlap it from y=0 (a real, separate layout bug, not merely
        // untested). Control.SetChildIndex does not help either (confirmed live: it changes
        // neither the layout nor the traversal below).
        //
        // Given that, TWO separate points in WinForms' real, structural Tab traversal turn
        // out to use the list/header's Z-order (Controls-collection) position rather than
        // TabIndex, both confirmed live against the actual control tree, not assumed:
        //   1. Entering the page's panel for the first time from outside it (e.g. Tab pressed
        //      while sitting on the category list) lands on Controls[0] of that panel -- the
        //      list, since it must be added first for the layout reason above -- regardless of
        //      any TabIndex value.
        //   2. Leaving the header's own LAST child, moving forward, ascends back out to the
        //      header's own next sibling by the header's Z-order position among ITS parent's
        //      children -- since the list sits BEFORE the header in that collection (add-order,
        //      same layout reason), forward traversal never finds it there either; it only
        //      turns up on a full wrap-around, after Close. The same asymmetry breaks
        //      Shift+Tab backward from Status: descending into the page again lands on the
        //      header's own last child, not the list.
        // No amount of TabIndex tuning can fix either of these -- they are keyed off Controls-
        // collection position, which the Dock=Fill layout requirement fixes in place. The
        // correct, supported fix for this well-known Dock-vs-tab-order conflict is to
        // intercept Tab-key transitions at the Form level, where ProcessTabKey is a real,
        // overridable hook (Panel is not a ContainerControl and cannot override it itself),
        // and drive the page's own content from one explicit, verified-correct order (see
        // PageOrder) instead of trusting the structural walk for it -- including BOTH
        // boundary hops (entering from the category list, and leaving to Status). Confirmed
        // live that leaving the exit hop to fall through to base.ProcessTabKey looked plausible
        // (Status is a genuine, simply-structured Form-level sibling of the category list) but
        // was NOT reliable in practice: asking WinForms "what comes after this page's last
        // control" can re-enter the page's own header container instead of ascending past it,
        // producing a real infinite loop between the header's content and the list rather than
        // ever reaching Status. Handling both directions explicitly avoids trusting that
        // ascension at all. Only genuinely simple, unambiguous transitions -- Status -> Close,
        // Close -> wrap to the category list, arrow-key category switching, disabled/hidden
        // controls between OTHER Form-level controls -- are left to real, untouched WinForms
        // behavior via base.ProcessTabKey. Disabled/hidden controls WITHIN a page's own order
        // (e.g. the Award selector when no Rule Definitions are loaded, or Edit/Delete/Export
        // before any row is selected) are skipped explicitly here, same as real Tab handling
        // would. Unchanged by the TabControl -> category-list swap: this override is driven by
        // _categoryListBox.SelectedIndex/ActiveControl checks exactly like it used to be driven
        // by _tabControl's, because the underlying page panels (and their own internal add-order
        // pitfall) are completely untouched by which outer container currently hosts them.
        protected override bool ProcessTabKey(bool forward)
        {
            if (_categoryListBox == null) return base.ProcessTabKey(forward);
            Control[] order = PageOrder(_categoryListBox.SelectedIndex);

            if (forward && ActiveControl == _categoryListBox && order != null && order.Length > 0)
            {
                Control first = FirstSelectable(order, 0, +1);
                if (first != null) return first.Focus();
            }

            // Shift+Tab from the shared Status field must re-enter the CURRENTLY selected
            // page at its own last control, not whatever the structural walk would find.
            if (!forward && ActiveControl == _statusTb && order != null && order.Length > 0)
            {
                Control last = FirstSelectable(order, order.Length - 1, -1);
                if (last != null) return last.Focus();
            }

            if (order != null)
            {
                int idx = Array.IndexOf(order, ActiveControl);
                if (idx >= 0)
                {
                    int step = forward ? 1 : -1;
                    Control target = FirstSelectable(order, idx + step, step);
                    if (target != null) return target.Focus();
                    if (!forward) return _categoryListBox.Focus();   // nothing selectable before the first item -> the list
                    if (_statusTb != null) return _statusTb.Focus();   // nothing selectable after the last item -> Status
                }
            }
            return base.ProcessTabKey(forward);
        }

        // Scans `order` from `start`, stepping by `step` (+1 or -1), for the first control that
        // can actually take focus -- mirrors real Tab-key handling silently skipping disabled/
        // hidden controls instead of getting stuck on one.
        private static Control FirstSelectable(Control[] order, int start, int step)
        {
            for (int i = start; i >= 0 && i < order.Length; i += step)
                if (order[i] != null && order[i].CanSelect) return order[i];
            return null;
        }

        // The verified-correct (bb1a7a0-matching) content order for each page, driving
        // ProcessTabKey above. Kept as one explicit array per page rather than inferred from
        // the Controls tree, since that tree is exactly what can't be trusted here.
        private Control[] PageOrder(int page)
        {
            switch (page)
            {
                case PAGE_MYLOG: return new Control[] {
                    _statTotalTb, _statLotwTb, _statQrzTb, _statConfTb, _statWasTb, _statDxccTb, _statWazTb,
                    _statUploadQrzTb, _statUploadClubLogTb, _statUploadLotwTb, _statUploadHrdLogTb, _dashRecentLv,
                };
                case PAGE_AWARDS: return new Control[] {
                    _awardsViewCb, _awardsProgressLbl, _awardsLv, _awardsManageBtn, _awardsRefreshBtn,
                };
                case PAGE_STILLNEED: return new Control[] {
                    _neededAwardsClb, _neededBandCb, _neededCountLbl, _neededRefreshBtn, _neededLv,
                };
                case PAGE_LOOKUP: return new Control[] {
                    _searchTb, _searchBtn, _searchLv, _searchClearBtn,
                };
                case PAGE_EDITLOG: return new Control[] {
                    _editCallTb, _editSourceCb, _editDateFromTb, _editDateToTb, _editSearchBtn, _editClearBtn,
                    _editRowOrderBtn, _editLv, _editAddBtn, _editEditBtn, _editDeleteBtn, _editExportBtn,
                };
                case PAGE_SYNC: return new Control[] {
                    _syncImportBtn, _syncQrzBtn, _syncLotwBtn, _syncClubLogBtn, _syncEqslBtn, _syncExportBtn,
                    _srcHistoryLv,
                };
                default: return null;
            }
        }

        // ── Population methods ────────────────────────────────────────────────────

        private void PopulateMyLog()
        {
            if (_db == null)
            {
                _statTotalTb.Text = "Database not available.";
                return;
            }
            try
            {
                int total     = _db.TotalQsos();
                int confirmed = _db.ConfirmedQsos();
                int lotwConf  = _db.LotwConfirmedQsos();
                int qrzConf   = _db.QrzConfirmedQsos();
                var (wasW, wasC)   = _db.WasProgress();
                var (dxccW, dxccC) = _db.DxccProgress();
                var (wazW, wazC)   = _db.WazProgress();

                _statTotalTb.Text = total.ToString("N0");
                _statLotwTb.Text  = lotwConf.ToString("N0");
                _statQrzTb.Text   = qrzConf.ToString("N0");
                _statConfTb.Text  = confirmed.ToString("N0") +
                    (total > 0 ? $"  ({100.0 * confirmed / total:0.0}%)" : "");
                _statWasTb.Text   = $"{wasW} / 50 worked,  {wasC} / 50 confirmed";
                _statDxccTb.Text  = $"{dxccW} worked,  {dxccC} confirmed";
                _statWazTb.Text   = $"{wazW} / 40 worked,  {wazC} / 40 confirmed";

                _statUploadQrzTb.Text     = FormatUploadStatus(_db.GetUploadSyncStatus("QRZ"));
                _statUploadClubLogTb.Text = FormatUploadStatus(_db.GetUploadSyncStatus("CLUBLOG"));
                _statUploadLotwTb.Text    = FormatUploadStatus(_db.GetUploadSyncStatus("LOTW"));
                _statUploadHrdLogTb.Text  = FormatUploadStatus(_db.GetUploadSyncStatus("HRDLOG"));

                var recent = _db.GetRecentQsos(10);
                _dashRecentLv.Items.Clear();
                foreach (var q in recent)
                {
                    var item = new ListViewItem(FormatDate(q.QsoDate));
                    item.SubItems.Add(FormatTime(q.TimeOn));
                    item.SubItems.Add(q.Callsign);
                    item.SubItems.Add(q.Band);
                    item.SubItems.Add(q.Mode);
                    item.SubItems.Add(q.Country);
                    item.SubItems.Add(ConfirmedText(q.LotwQslRcvd, q.QrzQslRcvd));
                    _dashRecentLv.Items.Add(item);
                }
            }
            catch (Exception ex) { _statTotalTb.Text = "Error: " + ex.Message; }
        }

        // "Synced" here means "known to already be present at this service" -- true whether
        // Jimmy actually pushed the QSO there, or the QSO was downloaded FROM that service in
        // the first place (in which case it obviously doesn't need uploading). Calling this
        // "uploaded" was misleading: a download can grow this count with QSOs Jimmy never sent
        // anywhere, which read as a phantom/unauthorized upload the first time someone noticed
        // the count and timestamp move after only clicking Download.
        private static string FormatUploadStatus(LogbookDb.UploadSyncStatus s)
        {
            string last = s.LastUploadUtc.HasValue
                ? s.LastUploadUtc.Value.ToLocalTime().ToString("g")
                : "never";
            string synced = $"{s.UploadedCount.ToString("N0")} synced";
            return s.PendingCount == 0
                ? $"Up to date, {synced}  (last sync: {last})"
                : $"{s.PendingCount} pending, {synced}  (last sync: {last})";
        }

        private void PopulateSync()
        {
            if (_db == null) return;
            try
            {
                // QRZ status
                if (string.IsNullOrWhiteSpace(_qrzApiKey()))
                    _srcQrzStatusLbl.Text = "QRZ Logbook API key not configured.  (Options > Logbook)";
                else
                {
                    string dt = ReadableDate(_ini?.Read("LogbookLastQrzRefresh"));
                    int cnt   = _db.TotalQsos("QRZ");
                    _srcQrzStatusLbl.Text = $"API key configured.  Last refresh: {dt}.  QSOs: {cnt:N0}.";
                }

                // LoTW status
                if (string.IsNullOrWhiteSpace(_lotwUser()))
                    _srcLotwStatusLbl.Text = "LoTW credentials not configured.  (Options > Logbook)";
                else
                {
                    string dt = ReadableDate(_ini?.Read("LogbookLastLoTWRefresh"));
                    int cnt   = _db.TotalQsos("LOTW");
                    _srcLotwStatusLbl.Text = $"Username: {_lotwUser()}.  Last refresh: {dt}.  QSOs: {cnt:N0}.";
                }

                // eQSL status -- confirmed count is informational only (not an award-eligible
                // confirmation source, see LogbookDb.EqslConfirmedQsos's own comment).
                if (string.IsNullOrWhiteSpace(_eqslUsername()) || string.IsNullOrWhiteSpace(_eqslPassword()))
                    _srcEqslStatusLbl.Text = "eQSL credentials not configured.  (Options > Logbook)";
                else
                {
                    string dt = ReadableDate(_ini?.Read("LogbookLastEqslRefresh"));
                    int cnt   = _db.EqslConfirmedQsos();
                    _srcEqslStatusLbl.Text = $"Username: {_eqslUsername()}.  Last refresh: {dt}.  Confirmed (informational): {cnt:N0}.";
                }

                // Club Log status
                if (string.IsNullOrWhiteSpace(_clubLogEmail()) || string.IsNullOrWhiteSpace(_clubLogPassword()) || string.IsNullOrWhiteSpace(_clubLogCallsign()))
                    _srcClubLogStatusLbl.Text = "Club Log upload credentials not configured.  (Options > Logbook)";
                else
                {
                    string dt = ReadableDate(_ini?.Read("LogbookLastClubLogRefresh"));
                    int cnt   = _db.TotalQsos("CLUBLOG");
                    _srcClubLogStatusLbl.Text = $"Callsign: {_clubLogCallsign()}.  Last refresh: {dt}.  QSOs: {cnt:N0}.";
                }

                // History
                var hist = _db.GetImportHistory(25);
                _srcHistoryLv.Items.Clear();
                foreach (var h in hist)
                {
                    var item = new ListViewItem(h.StartedAt == DateTime.MinValue ? "?" : h.StartedAt.ToLocalTime().ToString("g"));
                    item.SubItems.Add(h.Source);
                    item.SubItems.Add(h.NewQso.ToString("N0"));
                    item.SubItems.Add(h.NewlyConfirmed.ToString("N0"));
                    item.SubItems.Add(h.Corrected.ToString("N0"));
                    item.SubItems.Add(h.TotalQso.ToString("N0"));
                    // T11 fix, 2026-08-23 (CONFIRMED bug): used to show a blank cell for success
                    // (less clear than an explicit "0") and silently truncated a multi-error
                    // import to its first line with no indication more existed. A truthful count
                    // (and the first line as detail, when there are any) is now always shown.
                    string[] errorLines = h.ErrorText?.Length > 0
                        ? h.ErrorText.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        : Array.Empty<string>();
                    item.SubItems.Add(errorLines.Length == 0 ? "0" : $"{errorLines.Length}: {errorLines[0]}");
                    _srcHistoryLv.Items.Add(item);
                }
            }
            catch (Exception ex) { SetStatus("Sync error: " + ex.Message); }
        }

        // Rebuilds the Award selector from RuleLibrary.Definitions (enabled only),
        // preserving the current selection by Id across rebuilds. Called on every
        // PopulateAwards() so a future "Reload Rules" action is reflected without
        // reopening the window.
        private void PopulateAwardsCombo()
        {
            var defs = RuleLibrary.Definitions.Where(d => d.Enabled)
                .OrderBy(d => d.Category ?? "").ThenBy(d => d.Name).ToList();

            string prevId = (_awardsViewCb.SelectedIndex >= 0 && _awardsViewCb.SelectedIndex < _awardsDefs.Count)
                ? _awardsDefs[_awardsViewCb.SelectedIndex].Id : null;

            _awardsDefs = defs;

            _suppressAwardsEvent = true;
            _awardsViewCb.Items.Clear();
            if (_awardsDefs.Count == 0)
            {
                _awardsViewCb.Items.Add("(No Rule Definitions available)");
                _awardsViewCb.Enabled = false;
                _awardsViewCb.SelectedIndex = 0;
            }
            else
            {
                _awardsViewCb.Enabled = true;
                foreach (var d in _awardsDefs) _awardsViewCb.Items.Add(d.Name);
                int idx = prevId != null ? _awardsDefs.FindIndex(d => d.Id == prevId) : -1;
                _awardsViewCb.SelectedIndex = idx >= 0 ? idx : 0;
            }
            _suppressAwardsEvent = false;
        }

        private void PopulateAwards()
        {
            if (_db == null || _awardsViewCb == null) return;

            PopulateAwardsCombo();
            _awardsLv.Items.Clear();
            _awardsLv.Columns.Clear();

            if (_awardsDefs.Count == 0)
            {
                _awardsProgressLbl.Text = RuleLibrary.LoadErrors.Count > 0
                    ? $"No enabled Rule Definitions ({RuleLibrary.LoadErrors.Count} load error(s) — see log_rules_errors.txt)."
                    : "No Rule Definitions found.";
                return;
            }

            int idx = _awardsViewCb.SelectedIndex;
            if (idx < 0 || idx >= _awardsDefs.Count) return;
            var def = _awardsDefs[idx];

            try
            {
                var result = RuleEngine.Evaluate(def);
                RenderAwardResult(def, result);
            }
            catch (Exception ex) { SetStatus("Awards error: " + ex.Message); }
        }

        // Renders one RuleResult generically, driven entirely by the definition's
        // Target/GroupBy/Confirmation -- no per-award-name branching, so a new
        // Rule Definition file just works without a UI code change.
        private void RenderAwardResult(RuleDefinition def, RuleResult result)
        {
            if (result.EvaluationError != null)
            {
                _awardsProgressLbl.Text = "Error: " + result.EvaluationError;
                return;
            }

            // Target=All is ALWAYS Worked-based, never overridable (RuleEngine.FinishGrouped's
            // own comment) -- RuleEngine gates its completion on Worked regardless of
            // Confirmation, so this must match or the summary line could show a "Complete!"
            // that disagrees with a smaller confirmed count. Count/Levels honor the
            // definition's own Basis (defaults to Worked; opt into Confirmed via Basis=
            // CONFIRMED, e.g. DXCC Honor Roll) -- whichever ISN'T the basis is still shown as
            // an informational side-note when it differs (e.g. some items worked but not yet
            // confirmed via LoTW/QRZ, or vice versa for a Confirmed-basis award).
            bool basisIsConfirmed = def.Target != RuleTargetType.All && def.Basis == RuleBasis.Confirmed;
            string basisLabel = basisIsConfirmed ? "confirmed" : "worked";
            int basis      = basisIsConfirmed ? result.Confirmed : result.Worked;
            int sideValue  = basisIsConfirmed ? result.Worked    : result.Confirmed;
            string sideLabel = basisIsConfirmed ? "worked" : "confirmed";
            bool showSideNote = basisIsConfirmed
                ? result.Worked != result.Confirmed
                : def.Confirmation != RuleConfirmation.None && result.Confirmed != result.Worked;
            string sideNote = showSideNote ? $"  ({sideValue} {sideLabel})" : "";

            switch (def.Target)
            {
                case RuleTargetType.All:
                    _awardsProgressLbl.Text = $"{basis} / {result.UniverseSize} {basisLabel}{sideNote}" +
                        (result.Completed ? "  — Complete!" : "");
                    break;

                case RuleTargetType.Count:
                    // result.EffectiveThreshold, not def.Threshold -- a dynamic ThresholdFrom
                    // award (e.g. Honor Roll) has no meaningful literal Threshold of its own.
                    _awardsProgressLbl.Text = $"{basis} / {result.EffectiveThreshold} {basisLabel}{sideNote}" +
                        (result.Completed ? "  — Complete!" : "");
                    break;

                case RuleTargetType.Levels:
                    string tierText = result.CurrentTier != null ? $"Current: {result.CurrentTier}" : "No level reached yet";
                    string next = NextLevelText(def, basis);
                    _awardsProgressLbl.Text = $"{basis} {basisLabel}{sideNote}  —  {tierText}" +
                        (next != null ? $"  (next: {next})" : "");
                    break;
            }

            BuildAwardColumns(def);
            BuildAwardRows(def, result);
        }

        private static string NextLevelText(RuleDefinition def, int basis)
        {
            foreach (var lvl in def.Levels)
                if (basis < lvl.Threshold) return $"{lvl.Name} at {lvl.Threshold}";
            return null;
        }

        private void BuildAwardColumns(RuleDefinition def)
        {
            bool showWorkedCol = def.Target == RuleTargetType.All && def.GroupBy != RuleGroupBy.None;
            bool showBandsCol  = def.GroupBy != RuleGroupBy.None;
            string itemHeader  = def.GroupBy == RuleGroupBy.None ? "Endorsement" : GroupByHeader(def.GroupBy);

            _awardsLv.Columns.Add(itemHeader, 150);
            if (def.GroupBy == RuleGroupBy.Dxcc)
                _awardsLv.Columns.Add("Country", 170);
            if (showBandsCol)
                _awardsLv.Columns.Add("Band(s) worked", 150);
            if (showWorkedCol)
                _awardsLv.Columns.Add("Worked", 70);
            _awardsLv.Columns.Add(def.Confirmation == RuleConfirmation.None ? "Logged" : "Confirmed", 90);
        }

        // Renders the per-item checklist (states/entities/zones/etc.) and, when the
        // definition has an [Endorsements] section, a divider row followed by
        // per-band/per-mode sub-results below it (see the divider comment below for
        // why this is a plain row rather than a native ListView group).
        private void BuildAwardRows(RuleDefinition def, RuleResult result)
        {
            bool showWorkedCol   = def.Target == RuleTargetType.All && def.GroupBy != RuleGroupBy.None;
            bool showBandsCol    = def.GroupBy != RuleGroupBy.None;
            bool hasEndorsements = result.Endorsements != null && result.Endorsements.Count > 0;

            if (def.GroupBy != RuleGroupBy.None)
            {
                var worked    = new HashSet<string>(result.WorkedItems    ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                var confirmed = new HashSet<string>(result.ConfirmedItems ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                var lotwConfirmed = new HashSet<string>(result.LotwConfirmedItems ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                var qrzConfirmed  = new HashSet<string>(result.QrzConfirmedItems  ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                Dictionary<int, string> dxccNames =
                    def.GroupBy == RuleGroupBy.Dxcc ? _db.GetDxccCountryNames() : null;

                var items = (def.Target == RuleTargetType.All && result.UniverseItems != null)
                    ? result.UniverseItems
                    : (result.WorkedItems ?? new List<string>());

                foreach (var value in items)
                {
                    var row = new ListViewItem(value);

                    if (dxccNames != null)
                    {
                        string name = null;
                        int dxccNum;
                        if (int.TryParse(value, out dxccNum)) dxccNames.TryGetValue(dxccNum, out name);
                        row.SubItems.Add(name ?? "");
                    }

                    bool isWorked    = worked.Contains(value);
                    bool isConfirmed = confirmed.Contains(value);
                    if (showBandsCol)
                    {
                        List<string> bands;
                        row.SubItems.Add(result.WorkedBands != null && result.WorkedBands.TryGetValue(value, out bands)
                            ? string.Join(", ", bands) : "—");
                    }
                    if (showWorkedCol)
                        row.SubItems.Add(isWorked ? "Yes" : "—");
                    // Falls back to a plain "Confirmed" (rather than misreporting "—") if this
                    // rule's Confirmation ever matches a service beyond LoTW/QRZ that isn't
                    // broken out here yet (see Next Build TODO item 1 -- Club Log/eQSL/eQTH).
                    bool viaLotwOrQrz = lotwConfirmed.Contains(value) || qrzConfirmed.Contains(value);
                    string confirmedText = !isConfirmed ? (isWorked ? "Not confirmed" : "—")
                        : viaLotwOrQrz ? ConfirmedText(lotwConfirmed.Contains(value), qrzConfirmed.Contains(value))
                        : "Confirmed";
                    row.SubItems.Add(confirmedText);

                    _awardsLv.Items.Add(row);
                }
            }

            if (hasEndorsements)
            {
                // Plain divider row instead of a native ListView group -- a real ListView
                // group (ShowGroups=true) exposes a distinct accessibility-tree node that
                // some JAWS versions mis-announce when focus crosses back into the first
                // group's first row (full window/tab/list structure re-announced instead
                // of just the row). A flat list with a text divider avoids that node
                // entirely while still visually separating the two sections.
                _awardsLv.Items.Add(new ListViewItem("— Endorsements —"));

                foreach (var end in result.Endorsements)
                {
                    var row = new ListViewItem($"{end.Kind}: {end.Value}");

                    if (def.GroupBy == RuleGroupBy.Dxcc) row.SubItems.Add("");
                    if (showBandsCol) row.SubItems.Add("");
                    if (showWorkedCol) row.SubItems.Add(end.Worked.ToString());

                    string status = def.Target == RuleTargetType.Levels
                        ? (end.Tier ?? "—")
                        : (end.Completed ? "Yes" : "No");
                    row.SubItems.Add(status);

                    _awardsLv.Items.Add(row);
                }
            }
        }

        private static string GroupByHeader(RuleGroupBy g)
        {
            switch (g)
            {
                case RuleGroupBy.Dxcc:      return "DXCC#";
                case RuleGroupBy.Country:   return "Country";
                case RuleGroupBy.State:     return "State/Province";
                case RuleGroupBy.CqZone:    return "CQ Zone";
                case RuleGroupBy.ItuZone:   return "ITU Zone";
                case RuleGroupBy.Continent: return "Continent";
                case RuleGroupBy.County:    return "County";
                case RuleGroupBy.Grid:      return "Grid";
                case RuleGroupBy.Grid4:     return "Grid Square";
                case RuleGroupBy.Iota:      return "IOTA Ref";
                case RuleGroupBy.Prefix:    return "Prefix";
                case RuleGroupBy.Callsign:  return "Callsign";
                case RuleGroupBy.SigInfo:   return "Reference";
                case RuleGroupBy.DarcDok:   return "DOK";
                default:                    return "Item";
            }
        }

        // Rebuilds the Still Need awards list from RuleLibrary.Definitions (enabled
        // only), preserving the current browsing selection by Id across rebuilds --
        // mirrors PopulateAwardsCombo() so both tabs offer the same award list. Each
        // item's checked state is independent of the selection: it reflects whether
        // that award's Id is in Controller.activeAwardRuleIds, restricted to awards
        // RuleEngine.SupportsLiveTag actually allows to be tracked live at all.
        private void PopulateNeededAwardsList()
        {
            var defs = RuleLibrary.Definitions.Where(d => d.Enabled)
                .OrderBy(d => d.Category ?? "").ThenBy(d => d.Name).ToList();

            // Skip the rebuild when the enabled-rule set hasn't actually changed. PopulateNeeded()
            // calls this on every plain selection/band change and tab switch, not just when a
            // Rule Definition was added/removed -- and _neededAwardsClb's own SelectedIndexChanged
            // fires as part of the SAME click that checks/unchecks a not-yet-selected row's box
            // (selection changes before the click's check-toggle completes). Clearing and
            // re-adding Items mid-click could silently swallow that pending checkbox toggle
            // (found 2026-09-15: checking Route 66 On The Air, closing the Logbook window, and
            // reopening it showed the box unchecked again -- activeAwardRuleIds in the profile
            // ini never gained ROUTE66OTA). Comparing by Id leaves the list and its live checked
            // states alone unless something genuinely added, removed, or reordered an award.
            if (_neededDefs.Count == defs.Count && _neededDefs.Select(d => d.Id).SequenceEqual(defs.Select(d => d.Id)))
            {
                _neededDefs = defs;
                return;
            }

            string prevId = (_neededAwardsClb.SelectedIndex >= 0 && _neededAwardsClb.SelectedIndex < _neededDefs.Count)
                ? _neededDefs[_neededAwardsClb.SelectedIndex].Id : _activeAwardRuleIds.FirstOrDefault();

            _neededDefs = defs;

            _suppressNeededEvent = true;
            _neededAwardsClb.Items.Clear();
            if (_neededDefs.Count == 0)
            {
                _neededAwardsClb.Items.Add("(No Rule Definitions available)");
                _neededAwardsClb.Enabled = false;
                _neededAwardsClb.SelectedIndex = 0;
            }
            else
            {
                _neededAwardsClb.Enabled = true;
                foreach (var d in _neededDefs)
                {
                    bool tracked = RuleEngine.SupportsLiveTag(d) && _activeAwardRuleIds.Contains(d.Id);
                    _neededAwardsClb.Items.Add(d.Name, tracked);
                }
                int idx = prevId != null ? _neededDefs.FindIndex(d => d.Id == prevId) : -1;
                _neededAwardsClb.SelectedIndex = idx >= 0 ? idx : 0;
            }
            _suppressNeededEvent = false;
        }

        private void PopulateNeeded()
        {
            if (_db == null || _neededAwardsClb == null) return;

            PopulateNeededAwardsList();
            _neededLv.Items.Clear();
            _neededLv.Columns.Clear();

            if (_neededDefs.Count == 0)
            {
                _neededCountLbl.Text = RuleLibrary.LoadErrors.Count > 0
                    ? $"No enabled Rule Definitions ({RuleLibrary.LoadErrors.Count} load error(s) — see log_rules_errors.txt)."
                    : "No Rule Definitions found.";
                return;
            }

            int idx = _neededAwardsClb.SelectedIndex;
            if (idx < 0 || idx >= _neededDefs.Count) return;
            var def = _neededDefs[idx];

            // Restrict the Band dropdown to bands that are actually meaningful for this
            // award -- a band-restricted award (e.g. a per-band WAS variant, or a single-band
            // special event) can never be meaningfully evaluated "as" some other band (see
            // RuleEngine.ResolveBandsForEvaluation), so don't offer that choice at all. Only
            // rebuild when the choice set actually differs, so switching bands (not awards)
            // never disturbs this dropdown.
            var bandChoices = RuleEngine.BandChoicesFor(def.Bands, AllBands);
            if (!_neededBandCb.Items.Cast<string>().SequenceEqual(bandChoices))
            {
                string prevBand = _neededBandCb.SelectedIndex > 0 ? (string)_neededBandCb.SelectedItem : null;
                _suppressNeededEvent = true;
                _neededBandCb.Items.Clear();
                _neededBandCb.Items.AddRange(bandChoices);
                int newIdx = prevBand != null ? Array.IndexOf(bandChoices, prevBand) : -1;
                _neededBandCb.SelectedIndex = newIdx >= 0 ? newIdx : 0;
                _suppressNeededEvent = false;
            }

            try
            {
                string band = _neededBandCb.SelectedIndex == 0 ? null : (string)_neededBandCb.SelectedItem;
                var result = RuleEngine.EvaluateBand(def, band);
                RenderNeededResult(def, result, band);
            }
            catch (Exception ex) { SetStatus("Needed error: " + ex.Message); }
        }

        // Renders one RuleResult's StillNeeded list generically, driven by the
        // definition's GroupBy -- no per-award-name branching. Definitions whose
        // Target isn't ALL (or whose universe can't be resolved) have no fixed
        // checklist, so RuleResult.StillNeeded is null; that's shown plainly
        // rather than treated as an error.
        private void RenderNeededResult(RuleDefinition def, RuleResult result, string band)
        {
            if (result.EvaluationError != null)
            {
                _neededCountLbl.Text = "Error: " + result.EvaluationError;
                return;
            }

            if (result.StillNeeded == null)
            {
                _neededCountLbl.Text = "This rule does not have a fixed still-needed checklist. " +
                    "(Live decode tagging is unavailable for this award.)";
                return;
            }

            string itemHeader = GroupByHeader(def.GroupBy);
            _neededLv.Columns.Add(itemHeader, 150);
            if (def.GroupBy == RuleGroupBy.Dxcc)
                _neededLv.Columns.Add("Country", 200);
            _neededLv.Columns.Add("Status", 120);

            Dictionary<int, string> dxccNames =
                def.GroupBy == RuleGroupBy.Dxcc ? _db.GetDxccCountryNames() : null;

            foreach (var value in result.StillNeeded)
            {
                var item = new ListViewItem(value);
                if (dxccNames != null)
                {
                    string name = null;
                    int dxccNum;
                    if (int.TryParse(value, out dxccNum)) dxccNames.TryGetValue(dxccNum, out name);
                    item.SubItems.Add(name ?? "");
                }
                item.SubItems.Add("Not yet worked");
                _neededLv.Items.Add(item);
            }

            string bandNote = band != null ? $" on {band}" : "";
            string liveTagNote = RuleEngine.SupportsLiveTag(def)
                ? "  Live decode tagging: on."
                : "  Live decode tagging: unavailable for this award.";
            _neededCountLbl.Text = $"{result.StillNeeded.Count} {itemHeader.ToLowerInvariant()} needed{bandNote}.{liveTagNote}";
        }

        private void DoSearch()
        {
            if (_db == null) return;
            string pat = (_searchTb.Text ?? "").Trim();
            if (pat.Length == 0) { _searchCountLbl.Text = "Enter a callsign."; return; }

            try
            {
                var results = _db.SearchByCallsign(pat);
                _searchLv.Items.Clear();
                foreach (var q in results)
                {
                    var item = new ListViewItem(FormatDate(q.QsoDate));
                    item.SubItems.Add(FormatTime(q.TimeOn));
                    item.SubItems.Add(q.Callsign);
                    item.SubItems.Add(q.Band);
                    item.SubItems.Add(q.Mode);
                    item.SubItems.Add(q.State);
                    item.SubItems.Add(q.Country);
                    item.SubItems.Add(ConfirmedText(q.LotwQslRcvd, q.QrzQslRcvd));
                    item.SubItems.Add(q.Source);
                    _searchLv.Items.Add(item);
                }
                _searchCountLbl.Text = results.Count == 0
                    ? "No QSOs found."
                    : $"{results.Count} QSO{(results.Count == 1 ? "" : "s")} found.";
            }
            catch (Exception ex) { SetStatus("Search error: " + ex.Message); }
        }

        // ── Import handlers ───────────────────────────────────────────────────────

        private async void ImportBtn_Click(object sender, EventArgs e)
        {
            if (_db == null) { SetStatus("Database not available."); return; }

            using (var dlg = new OpenFileDialog
            {
                Title       = "Import ADIF File",
                Filter      = "ADIF files (*.adi;*.adif)|*.adi;*.adif|All files (*.*)|*.*",
                Multiselect = false,
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                await RunImport(dlg.FileName);
            }
        }

        private async void QrzRefreshBtn_Click(object sender, EventArgs e)
        {
            if (_db == null) { SetStatus("Database not available."); return; }
            if (string.IsNullOrWhiteSpace(_qrzApiKey())) { SetStatus("QRZ API key not configured."); return; }

            SetStatus("Fetching QRZ Logbook…");
            SetBusy(true);
            try
            {
                var client = new QrzLogbookClient();
                // Always fetch the complete log, not just records modified since the last
                // refresh -- an incremental MODSINCE filter can never re-discover a QSO that
                // was missed on some earlier sync (its own last-modified date on QRZ's side
                // predates every checkpoint since), permanently hiding it. Found 2026-07-09:
                // 3 confirmed QRZ QSOs stuck exactly this way. A full ADIF pull is a few MB
                // and imports in under a second (dedup-key upsert is idempotent), so there's
                // no real cost to always doing the complete, authoritative pull.
                string adif = await client.FetchAdifAsync(_qrzApiKey(), since: null).ConfigureAwait(true);
                if (adif == null)
                {
                    string msg = "QRZ error: " + (client.LastError ?? "Unknown error") + " (see debug log for details)";
                    LogSyncFailure("QRZ", msg);
                    SetStatus(msg);
                    return;
                }
                if (adif.Length == 0)
                {
                    string msg = "QRZ: 0 QSOs returned. Verify this is your QRZ Logbook API key (qrz.com → Logbook → Settings), not the XML callsign key.";
                    LogSyncFailure("QRZ", msg);
                    SetStatus(msg);
                    return;
                }
                await RunImportFromText(adif, "QRZ", "LogbookLastQrzRefresh");
            }
            catch (Exception ex)
            {
                LogSyncFailure("QRZ", "QRZ refresh error: " + ex.Message);
                SetStatus("QRZ refresh error: " + ex.Message);
            }
            finally { SetBusy(false); }
        }

        private async void LoTWRefreshBtn_Click(object sender, EventArgs e)
        {
            if (_db == null) { SetStatus("Database not available."); return; }
            if (string.IsNullOrWhiteSpace(_lotwUser())) { SetStatus("LoTW credentials not configured."); return; }

            SetBusy(true);
            try
            {
                var client = new LoTWQsoClient();
                // Always fetch the complete history (since: null -> LoTWQsoClient uses
                // 1900-01-01), not just records changed since the last refresh -- same
                // reasoning as the QRZ sync above: an incremental filter can permanently hide
                // a QSO confirmed before the last checkpoint if it was ever missed on an
                // earlier sync. LoTW's own log is small enough that this costs nothing.

                // LoTW splits confirmed and unconfirmed QSOs into separate API responses.
                // Fetch both and concatenate; AdifParser handles multiple <EOH> tags.
                SetStatus("Fetching LoTW confirmed QSOs…");
                string adif1 = await client.FetchReportAsync(_lotwUser(), _lotwPass(), since: null, confirmedOnly: true).ConfigureAwait(true);
                if (adif1 == null)
                {
                    string msg = "LoTW error: " + (client.LastError ?? "Unknown error");
                    LogSyncFailure("LOTW", msg);
                    SetStatus(msg);
                    return;
                }

                SetStatus("Fetching LoTW unconfirmed QSOs…");
                string adif2 = await client.FetchReportAsync(_lotwUser(), _lotwPass(), since: null, confirmedOnly: false).ConfigureAwait(true);
                // Independent audit finding 2, 2026-08-23 (CONFIRMED bug, HIGH PRIORITY): this
                // used to silently replace a null adif2 with "" and continue as if the complete
                // two-part download had succeeded -- LoTWQsoClient.LastError was discarded, and
                // the subsequent import still advanced LogbookLastLoTWRefresh, so a genuinely
                // failed unconfirmed-QSO fetch was reported and checkpointed as a full success.
                // Missing unconfirmed QSOs matter for worked-but-unconfirmed award state and
                // duplicate/worked classification -- treated the same as adif1==null above:
                // abort before import, log the real error, and leave the previous refresh
                // timestamp unchanged so the next scheduled/manual run retries the whole sync
                // rather than silently missing this half forever.
                if (adif2 == null)
                {
                    string msg = "LoTW error (unconfirmed QSOs): " + (client.LastError ?? "Unknown error");
                    LogSyncFailure("LOTW", msg);
                    SetStatus(msg);
                    return;
                }

                await RunImportFromText(adif1 + "\r\n" + adif2, "LOTW", "LogbookLastLoTWRefresh").ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                LogSyncFailure("LOTW", "LoTW refresh error: " + ex.Message);
                SetStatus("LoTW refresh error: " + ex.Message);
            }
            finally { SetBusy(false); }
        }

        private async void ClubLogRefreshBtn_Click(object sender, EventArgs e)
        {
            if (_db == null) { SetStatus("Database not available."); return; }
            if (string.IsNullOrWhiteSpace(_clubLogEmail()) || string.IsNullOrWhiteSpace(_clubLogPassword()) || string.IsNullOrWhiteSpace(_clubLogCallsign()))
            {
                SetStatus("Club Log upload credentials not configured.");
                return;
            }

            SetStatus("Fetching Club Log…");
            SetBusy(true);
            try
            {
                var client = new ClubLogUploadClient();
                // Always fetch the complete history (sinceYear: null omits Club Log's
                // startyear filter entirely) -- same reasoning as the QRZ/LoTW syncs above:
                // a year-level incremental filter can permanently hide a QSO confirmed in an
                // already-passed year if it was ever missed on an earlier sync.
                string adif = await client.FetchAdifAsync(_clubLogEmail(), _clubLogPassword(), _clubLogCallsign(), sinceYear: null).ConfigureAwait(true);
                if (adif == null)
                {
                    string msg = "Club Log error: " + (client.LastError ?? "Unknown error");
                    LogSyncFailure("CLUBLOG", msg);
                    SetStatus(msg);
                    return;
                }
                if (adif.Trim().Length == 0)
                {
                    SetStatus("Club Log: no records returned.");
                    return;
                }
                await RunImportFromText(adif, "CLUBLOG", "LogbookLastClubLogRefresh");
            }
            catch (Exception ex)
            {
                LogSyncFailure("CLUBLOG", "Club Log refresh error: " + ex.Message);
                SetStatus("Club Log refresh error: " + ex.Message);
            }
            finally { SetBusy(false); }
        }

        // Downloads and reconciles eQSL InBox confirmations -- deliberately NOT
        // RunImportFromText/AdifImporter.Import (see EqslReconciler's own comment: an eQSL
        // InBox record is someone else's confirmation report, not Jimmy Next's own logbook,
        // so it must never create a new local QSO row). since_unix is Jimmy's own last-synced
        // watermark, same "always incremental after the first pull" idea LoTW/QRZ/Club Log
        // already use -- but unlike them, a fresh install still does a full pull (since_unix
        // null) since there's no prior watermark yet.
        private async void EqslRefreshBtn_Click(object sender, EventArgs e)
        {
            if (_db == null) { SetStatus("Database not available."); return; }
            if (string.IsNullOrWhiteSpace(_eqslUsername()) || string.IsNullOrWhiteSpace(_eqslPassword()))
            {
                SetStatus("eQSL credentials not configured.");
                return;
            }

            SetStatus("Fetching eQSL InBox…");
            SetBusy(true);
            int logId = _db.LogImportStart("EQSL");
            try
            {
                string lastRefresh = _ini?.Read("LogbookLastEqslRefresh");
                long? sinceUnix = DateTime.TryParse(lastRefresh,
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var last)
                    ? (long?)new DateTimeOffset(last.ToUniversalTime()).ToUnixTimeSeconds()
                    : null;

                var client = new ExternalDataClient();
                string adif = null;
                string error = null;
                await Task.Run(() => { adif = client.DownloadEqsl(_eqslUsername(), _eqslPassword(), sinceUnix, out error); }).ConfigureAwait(true);

                if (adif == null)
                {
                    string msg = "eQSL error: " + (error ?? "Unknown error");
                    _db.LogImportFinish(logId, 0, 0, 0, 0, 0, msg);
                    SetStatus(msg);
                    return;
                }

                var result = await Task.Run(() => EqslReconciler.Reconcile(_db, adif)).ConfigureAwait(true);
                int processed = result.Matched + result.AlreadyConfirmed + result.Ambiguous + result.Unmatched + result.Skipped;
                int skippedTotal = result.AlreadyConfirmed + result.Ambiguous + result.Unmatched + result.Skipped;
                string note = result.Ambiguous > 0
                    ? $"{result.Ambiguous} ambiguous match(es) skipped (never guessed)."
                    : "";
                _db.LogImportFinish(logId, processed, 0, result.Matched, 0, skippedTotal, note);
                _ini?.Write("LogbookLastEqslRefresh", DateTime.UtcNow.ToString("o"));

                SetStatus($"eQSL reconcile complete: {result}");

                if (_activePage == _syncPanel) PopulateSync();
            }
            catch (Exception ex)
            {
                _db.LogImportFinish(logId, 0, 0, 0, 0, 0, ex.Message);
                SetStatus("eQSL reconcile error: " + ex.Message);
            }
            finally { SetBusy(false); }
        }

        // Records a history row for a sync attempt that failed before any ADIF data
        // reached RunImportFromText (which logs its own row on success/parse-level errors).
        private void LogSyncFailure(string source, string message)
        {
            if (_db == null) return;
            int logId = _db.LogImportStart(source);
            _db.LogImportFinish(logId, 0, 0, 0, 0, 0, message);
        }

        // Used only by the manual "Import ADIF File" button -- the file's source (QRZ/LOTW/
        // CLUBLOG/WSJTX/MANUAL) isn't known up front the way it is for the QRZ/LoTW/Club Log
        // refresh buttons below, which call RunImportFromText directly with their own source
        // already in hand. AdifImporter.DetectSource infers it from the file itself so a
        // service's data imported by hand still gets tagged accurately instead of everything
        // collapsing into "MANUAL".
        private async Task RunImport(string filePath)
        {
            SetBusy(true);
            SetStatus($"Reading {Path.GetFileName(filePath)}…");
            try
            {
                string text = await Task.Run(() => File.ReadAllText(filePath)).ConfigureAwait(true);
                string source = await Task.Run(() => AdifImporter.DetectSource(text, AdifParser.Parse(text))).ConfigureAwait(true);
                await RunImportFromText(text, source, null, detected: true);
            }
            catch (Exception ex)
            {
                SetStatus("Import error: " + ex.Message);
            }
            finally { SetBusy(false); }
        }

        private async Task RunImportFromText(string adifText, string source, string metaKey, bool detected = false)
        {
            SetBusy(true);
            int logId = _db.LogImportStart(source);
            ImportResult result = null;

            try
            {
                result = await Task.Run(() =>
                {
                    return AdifImporter.Import(_db, AdifParser.ParseWithOrder(adifText), source,
                        count => BeginInvoke(new Action(() =>
                            SetStatus($"Importing {source}: {count:N0} processed…"))),
                        _resolveUsState);
                }).ConfigureAwait(true);

                _db.LogImportFinish(logId, result.Processed, result.NewQsos, result.NewlyConfirmed, result.Corrected, result.Skipped, result.Errors);

                // Independent audit finding 3, 2026-08-23 (CONFIRMED bug, HIGH/MEDIUM PRIORITY):
                // the checkpoint used to be written unconditionally, before result.Errors was
                // even checked below -- a malformed or newly unsupported source record could be
                // skipped and then not retried until the full refresh interval expired, even
                // though the persisted "last success" timestamp claimed a complete refresh. Valid
                // records still import (the DB transaction above is unaffected); only the
                // checkpoint write itself is now gated on a genuinely clean import, so a source
                // with any errors gets retried in full next time rather than being silently
                // marked "done".
                if (metaKey != null && string.IsNullOrWhiteSpace(result.Errors))
                    _ini?.Write(metaKey, DateTime.UtcNow.ToString("o"));

                string sourceLabel = detected ? $"Detected source: {source}." : $"{source} import complete:";
                SetStatus($"{sourceLabel} {result.NewQsos:N0} new, {result.NewlyConfirmed:N0} newly confirmed, {result.Corrected:N0} corrected, {result.Skipped:N0} unchanged.");

                if (!string.IsNullOrWhiteSpace(result.Errors))
                {
                    string summary = result.Errors.Split(new[]{'\n'}, StringSplitOptions.RemoveEmptyEntries).Length + " errors encountered -- will retry this source in full next time.";
                    SetStatus(SetStatus_Text + "  " + summary);
                }

                // Refresh the active page to show new data; do not move focus.
                if (_activePage == _myLogPanel)   PopulateMyLog();
                else if (_activePage == _syncPanel) PopulateSync();

                // Notify Jimmy so it can refresh its HRC filter caches.
                _onImportComplete?.Invoke();
            }
            catch (Exception ex)
            {
                _db.LogImportFinish(logId, 0, 0, 0, 0, 0, ex.Message);
                SetStatus($"{(detected ? $"Detected source: {source}." : source)} import error: " + ex.Message);
            }
            finally { SetBusy(false); }
        }

        // ── Keyboard shortcuts ────────────────────────────────────────────────────

        private void LogbookWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5)
            {
                e.Handled = true;
                RefreshCurrentPage();
                return;
            }
            if (e.Control && e.KeyCode == Keys.F)
            {
                e.Handled = true;
                GoToLookup();
                return;
            }
            if (e.KeyCode == Keys.Escape)
            {
                // T10 fix, 2026-08-23 (CONFIRMED bug): used to only move focus to _tabControl --
                // Escape must close the window like every other Jimmy dialog, not leave the
                // operator needing the Close button or Alt+F4. Same Close() path the Close
                // button already uses (closeBtn.Click above) -- no separate busy/closing policy
                // exists to preserve; there was none before this fix either.
                e.Handled = true;
                Close();
                return;
            }
        }

        // Called both by the F5 shortcut below and externally by Controller when a
        // QSO is logged live, so an open Awards/Still Need page reflects it immediately.
        public void RefreshCurrentPage()
        {
            if      (_activePage == _myLogPanel)     PopulateMyLog();
            else if (_activePage == _awardsPanel)    PopulateAwards();
            else if (_activePage == _stillNeedPanel) PopulateNeeded();
            else if (_activePage == _lookupPanel)    DoSearch();
            else if (_activePage == _editLogPanel)   { if (_editLv.Items.Count > 0) DoEditSearch(); }
            else if (_activePage == _syncPanel)      PopulateSync();
        }

        private void GoToLookup()
        {
            _categoryListBox.SelectedIndex = PAGE_LOOKUP;
            _searchTb?.Focus();
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        // AccessibleRole.None + empty AccessibleName mark this as a pure layout container:
        // it and every other purely-decorative Panel below (header/footer/topRow/buttonRow)
        // used to expose no explicit name at all, which let WinForms/JAWS's own "infer a
        // name for this unnamed container" fallback kick in. That fallback walks the whole
        // window's control tree structurally (by add order), not by which TabPage is
        // actually visible -- so it could -- and did, confirmed against a real JAWS speech
        // transcript 2026-09-17 -- latch onto an unrelated Label from a completely different,
        // hidden tab (BuildSyncPage's "Import History (most recent first)" heading bled into
        // the shared Status field's announcement on every tab, not just Sync). Marking these
        // containers None/empty removes them from the accessibility tree as distinct named
        // regions entirely, so JAWS/NVDA pass straight through to their real, individually-
        // named children instead of trying to announce a name for the container itself.
        //
        // Category-list navigation pass (2026-09-25): every panel this method returns (the six
        // top-level pages, each built via BuildUi's own MakePage() call) no longer keeps this
        // default ""/None -- BuildUi explicitly overwrites AccessibleName/AccessibleRole right
        // after construction, once per page. That's safe now (and gives JAWS the page's own name
        // on entry, matching Options' category panels) specifically because WireCategoryList
        // Adds/Removes only the ONE currently-selected page into the host; the exact hazard this
        // comment describes (a hidden sibling bleeding stale content) requires a sibling to still
        // be parented in the tree, which can no longer happen. This default is kept here (rather
        // than set at each call site) purely so a future new page still starts safe-by-default.
        private static Panel MakePage()
        {
            return new Panel
            {
                Dock             = DockStyle.Fill,
                AutoScroll       = true,
                TabIndex         = 5,
                AccessibleName   = "",
                AccessibleRole   = AccessibleRole.None,
            };
        }

        private static ListView MakeListView(Font font)
        {
            return new ListView
            {
                View          = View.Details,
                FullRowSelect = true,
                MultiSelect   = false,
                Font          = font,
                GridLines     = true,
                HideSelection = false,
                TabIndex      = 10,
            };
        }

        private void AddSectionLabel(Panel p, string text, Font f, ref int y)
        {
            var lbl = new Label { Text = text, Font = f, Location = new Point(8, y), AutoSize = true };
            p.Controls.Add(lbl);
            y += 22;
        }

        private Label AddInfoLabel(Panel p, string text, Font f, ref int y)
        {
            var lbl = new Label
            {
                Text     = text,
                Font     = f,
                Location = new Point(12, y),
                AutoSize = false,
                Size     = new Size(680, 18),
            };
            p.Controls.Add(lbl);
            y += 22;
            return lbl;
        }

        // Creates a label + read-only TextBox pair for a single stat on the My Log page.
        private void AddStatField(Panel p, string label, Font f, ref int y,
            out TextBox tb, string accessibleName)
        {
            var lbl = new Label
            {
                Text     = label + ":",
                Font     = f,
                Location = new Point(8, y + 2),
                Size     = new Size(130, 16),
                AutoSize = false,
            };
            p.Controls.Add(lbl);

            tb = new TextBox
            {
                ReadOnly       = true,
                BorderStyle    = BorderStyle.None,
                BackColor      = SystemColors.Control,
                Font           = f,
                Location       = new Point(142, y),
                Size           = new Size(400, 18),
                TabStop        = true,
                AccessibleName = accessibleName,
                Text           = "…",
            };
            p.Controls.Add(tb);
            y += 22;
        }

        private void SetBusy(bool busy)
        {
            _syncImportBtn.Enabled  = !busy;
            _syncQrzBtn.Enabled     = !busy && !string.IsNullOrWhiteSpace(_qrzApiKey());
            _syncLotwBtn.Enabled    = !busy && !string.IsNullOrWhiteSpace(_lotwUser()) && !string.IsNullOrWhiteSpace(_lotwPass());
            _syncClubLogBtn.Enabled = !busy && !string.IsNullOrWhiteSpace(_clubLogEmail()) &&
                                       !string.IsNullOrWhiteSpace(_clubLogPassword()) && !string.IsNullOrWhiteSpace(_clubLogCallsign());
            _syncEqslBtn.Enabled    = !busy && !string.IsNullOrWhiteSpace(_eqslUsername()) && !string.IsNullOrWhiteSpace(_eqslPassword());
        }

        private string SetStatus_Text;
        // Public so Controller can mirror QRZ/Club Log upload progress here too
        // (see Controller.ShowUploadStatus) -- lets someone watch the same
        // status while working in this window instead of only the main form.
        // ── Duplicate contacts Nexus refused (logbook migration, D2) ────────────────────────

        // Raised on whatever thread sent the contact; the Status field is updated on the UI
        // thread. Status text only -- never spoken, never focused.
        private void OnNexusDuplicateRefused(NexusLogOutbox.RefusedEntry entry)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)ShowHeldDuplicates); } catch (InvalidOperationException) { }
        }

        private void ShowHeldDuplicates()
        {
            var held = _nexusOutbox?.Refused;
            if (held == null || held.Count == 0) return;
            SetStatus(DuplicateStatusText(held));
        }

        internal static string DuplicateStatusText(List<NexusLogOutbox.RefusedEntry> held)
        {
            var latest = held.OrderBy(r => r.RefusedUtc).Last();
            var q = latest.Qso;
            string when = DateTimeOffset.FromUnixTimeSeconds((long)q.WhenUnix).UtcDateTime.ToString("HH:mm");
            string matched = latest.ExistingWhenUnix.HasValue
                ? $", matches the contact logged at {DateTimeOffset.FromUnixTimeSeconds((long)latest.ExistingWhenUnix.Value).UtcDateTime:HH:mm} UTC"
                : "";
            return $"Duplicate not logged: {q.Call} {q.Band} {q.Mode} {when} UTC{matched}. " +
                   $"{held.Count} held. Press Enter here to review.";
        }

        // Enter on the Status field opens the review, only while something is held.
        private void StatusTb_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter || e.Modifiers != Keys.None) return;
            if (_nexusOutbox == null || _nexusOutbox.Refused.Count == 0) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            using (var dlg = new NexusRefusedReviewDlg(_nexusOutbox))
                dlg.ShowDialog(this);
            if (_nexusOutbox.Refused.Count > 0) ShowHeldDuplicates();
            else SetStatus("No duplicate contacts held.");
        }

        public void SetStatus(string msg)
        {
            SetStatus_Text = msg ?? "";
            if (_statusTb != null) _statusTb.Text = SetStatus_Text;
        }

        private static string FormatDate(string d)
        {
            if (d == null || d.Length < 8) return d ?? "";
            return $"{d.Substring(0,4)}-{d.Substring(4,2)}-{d.Substring(6,2)}";
        }

        private static string FormatTime(string t)
        {
            if (t == null || t.Length < 4) return t ?? "";
            return $"{t.Substring(0,2)}:{t.Substring(2,2)}";
        }

        private static string ConfirmedText(string lotw, string qrz) =>
            ConfirmedText(lotw == "Y", qrz == "Y");

        // Shared core: which service(s) confirmed, given a plain yes/no per service.
        // Used both per-QSO (My Log rows, via the string overload above) and per grouped
        // item (Awards tab checklist, via RuleResult.LotwConfirmedItems/QrzConfirmedItems).
        private static string ConfirmedText(bool lotw, bool qrz)
        {
            if (lotw && qrz) return "LoTW + QRZ";
            if (lotw)        return "LoTW";
            if (qrz)         return "QRZ";
            return "—";
        }

        private static string ReadableDate(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return "never";
            DateTime dt;
            return DateTime.TryParse(iso, out dt) ? dt.ToLocalTime().ToString("g") : "never";
        }

        private static int SrcCount(Dictionary<string, int> d, string k)
        {
            int v; return d.TryGetValue(k, out v) ? v : 0;
        }
    }
}
