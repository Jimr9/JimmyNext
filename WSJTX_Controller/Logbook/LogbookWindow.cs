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

        // ── Awards controls (Awards and Still Need are one page since 2026-10-01) ──────────
        private CheckedListBox _awardsClb;
        private ComboBox _awardsBandCb;
        private ComboBox _awardsShowCb;
        private TextBox  _awardsProgressLbl;
        private TextBox  _awardsAboutTb;   // what the award counts and what to check yourself (2026-10-06)
        private ListView _awardsLv;
        private Button   _awardsManageBtn;
        private Button   _awardsRefreshBtn;
        private List<RuleDefinition> _awardsDefs = new List<RuleDefinition>();
        private bool     _suppressAwardsEvent;

        // ── Lookup controls ───────────────────────────────────────────────────────

        // ── Sync controls ─────────────────────────────────────────────────────────
        private Button   _syncImportBtn;
        private Button   _syncExportBtn;
        private Button   _syncQrzBtn;
        private Button   _syncLotwBtn;
        private Button   _syncLotwFullBtn;
        private Button   _syncClubLogBtn;
        private Button   _syncEqslBtn;
        private Button   _syncEqslFullBtn;
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
        private ComboBox _editFieldCb;    // "Search in": All fields, or one field (2026-10-02)
        private ComboBox _editUploadCb;   // "Upload status": Any, or one service and state (2026-10-06)
        private TextBox  _editTextTb;     // "Search for"
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
        // Awards and Still Need are one page since 2026-10-01 ("Awards"): the same awards and the
        // same RuleEngine; Still Need only added the live-tracking checks and a band filter.
        private const int PAGE_AWARDS    = 1;
        // Lookup and Edit Log are one page since 2026-10-01 ("Lookup and Edit"): the Edit Log
        // filters already searched by callsign exactly as Lookup did, so Lookup only duplicated it.
        private const int PAGE_EDITLOG   = 2;
        private const int PAGE_SYNC      = 3;

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
            BuildEditLogPage(font, hfont);
            BuildSyncPage(font, hfont);

            string[] pageNames  = { "My Log", "Awards", "Lookup and Edit", "Sync" };
            Panel[]  pagePanels = { _myLogPanel, _awardsPanel, _editLogPanel, _syncPanel };
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
                Text           = "Sync from LoTW",
                AccessibleName = "Sync from LoTW, new confirmations",
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
                Text           = "Sync from eQSL",
                AccessibleName = "Sync from eQSL, new confirmations",
                Size           = new Size(150, 26),
                Location       = new Point(8, y),
                Font           = font,
                TabIndex       = 5,
                Enabled        = !string.IsNullOrWhiteSpace(_eqslUsername()) && !string.IsNullOrWhiteSpace(_eqslPassword()),
            };
            _syncEqslBtn.Click += EqslRefreshBtn_Click;
            header.Controls.Add(_syncEqslBtn);

            // Download from LoTW asks only for what is new since the last one (LoTW's own
            // high-water, as the Nexus desktop does); this asks for the whole history again --
            // for peace of mind, or a log that is missing older confirmations (2026-10-02).
            _syncLotwFullBtn = new Button
            {
                Text           = "Full LoTW Download",
                AccessibleName = "Full LoTW download, all confirmations",
                Size           = new Size(150, 26),
                Location       = new Point(164, y),
                Font           = font,
                TabIndex       = 6,
                Enabled        = _syncLotwBtn.Enabled,
            };
            _syncLotwFullBtn.Click += LoTWFullBtn_Click;
            header.Controls.Add(_syncLotwFullBtn);

            // Sync from eQSL asks only for what arrived since the last one; this asks for the
            // whole eQSL inbox again (operator, 2026-10-06: a "download" that silently starts
            // where the last one stopped is misleading).
            _syncEqslFullBtn = new Button
            {
                Text           = "Full eQSL Download",
                AccessibleName = "Full eQSL download, all confirmations",
                Size           = new Size(150, 26),
                Location       = new Point(320, y),
                Font           = font,
                TabIndex       = 7,
                Enabled        = _syncEqslBtn.Enabled,
            };
            _syncEqslFullBtn.Click += EqslFullBtn_Click;
            header.Controls.Add(_syncEqslFullBtn);
            y += 34;

            _syncExportBtn = new Button
            {
                Text           = "Export ADIF...",
                AccessibleName = "Export all QSOs to ADIF file",
                Size           = new Size(120, 26),
                Location       = new Point(8, y),
                Font           = font,
                TabIndex       = 8,
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

        // Awards and Still Need are one page since 2026-10-01 (operator). Moving through the award
        // list shows that award below -- everything, or only what is still needed, on all bands or
        // one -- whether or not it is checked; checking it (Space) tracks it live while operating,
        // exactly as the Still Need page's checks did (same saved ids, same refusal for an award
        // with no fixed checklist).
        private void BuildAwardsPage(Font font, Font hfont)
        {
            _awardsPanel = MakePage();

            // header (before the list in Tab order) and buttonRow (after it) -- both Dock=Top, so
            // the buttons sit above the list on screen but are reached after it, as before.
            // AccessibleName=""/AccessibleRole=None -- pure layout containers, see MakePage().
            var header = new Panel { Dock = DockStyle.Top, Height = 214, AccessibleName = "", AccessibleRole = AccessibleRole.None };

            header.Controls.Add(new Label { Text = "Awards:", Font = font, Location = new Point(8, 10), AutoSize = true });

            _awardsClb = new CheckedListBox
            {
                Font                  = font,
                Location              = new Point(56, 7),
                Size                  = new Size(300, 100),
                TabIndex              = 1,
                CheckOnClick          = false,
                AccessibleName        = "Awards",
                AccessibleDescription = "Space tracks the award live",
            };
            // CheckOnClick=false: WinForms' own CheckOnClick eats the toggle when the same click
            // also changes the selection (found live, 2026-09-15). And now that clicking a row is
            // how a mouse user BROWSES awards, a click toggles tracking only when it lands on the
            // check box itself, never on the award's name. Keyboard: Space toggles, as always.
            _awardsClb.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                int index = _awardsClb.IndexFromPoint(e.Location);
                if (index < 0 || index >= _awardsClb.Items.Count) return;
                if (e.X > _awardsClb.GetItemRectangle(index).Left + 18) return;
                _awardsClb.SetItemChecked(index, !_awardsClb.GetItemChecked(index));
            };
            // Items come from RuleLibrary.Definitions in PopulateAwardsList() -- dropping a new .ini
            // file into RuleDefinitions adds it here with no code change.
            _awardsClb.SelectedIndexChanged += (s, e) => { if (!_suppressAwardsEvent) PopulateAwards(); };
            _awardsClb.ItemCheck += (s, e) =>
            {
                if (_suppressAwardsEvent) return;
                if (e.Index < 0 || e.Index >= _awardsDefs.Count) return;
                var def = _awardsDefs[e.Index];
                if (e.NewValue == CheckState.Checked && !RuleEngine.SupportsLiveTag(def))
                {
                    e.NewValue = CheckState.Unchecked;
                    SetStatusSpoken(_awardsClb, $"{def.Name} can't be tracked live -- it has no fixed checklist to tag stations from.");
                    return;
                }
                _onActiveAwardRuleIdsChanged?.Invoke(def.Id, e.NewValue == CheckState.Checked);
                // The summary says whether the award shown is tracked; ItemCheck fires before the
                // box changes, so it is refreshed once the change has landed.
                BeginInvoke((Action)(() => { if (!IsDisposed) PopulateAwards(); }));
            };
            header.Controls.Add(_awardsClb);

            header.Controls.Add(new Label { Text = "Band:", Font = font, Location = new Point(366, 10), AutoSize = true });
            _awardsBandCb = new ComboBox
            {
                DropDownStyle  = ComboBoxStyle.DropDownList,
                Font           = font,
                Location       = new Point(410, 7),
                Size           = new Size(110, 21),
                TabIndex       = 2,
                AccessibleName = "Band filter",
            };
            _awardsBandCb.Items.AddRange(AllBands);
            _awardsBandCb.SelectedIndex = 0;
            _awardsBandCb.SelectedIndexChanged += (s, e) => { if (!_suppressAwardsEvent) PopulateAwards(); };
            header.Controls.Add(_awardsBandCb);

            header.Controls.Add(new Label { Text = "Show:", Font = font, Location = new Point(366, 40), AutoSize = true });
            _awardsShowCb = new ComboBox
            {
                DropDownStyle  = ComboBoxStyle.DropDownList,
                Font           = font,
                Location       = new Point(410, 37),
                Size           = new Size(150, 21),
                TabIndex       = 3,
                AccessibleName = "Show",
            };
            _awardsShowCb.Items.AddRange(new object[] { "Everything", "Still needed only" });
            _awardsShowCb.SelectedIndex = 0;
            _awardsShowCb.SelectedIndexChanged += (s, e) => { if (!_suppressAwardsEvent) PopulateAwards(); };
            header.Controls.Add(_awardsShowCb);

            // Read-only TextBox, not a Label -- a plain Label is never reachable by Tab, so a
            // screen reader user would never hear the summary (found 2026-07-12). Full width and
            // word-wrapped below the list/band/show row, so a long message is always readable.
            _awardsProgressLbl = new TextBox
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
                TabIndex       = 4,
                AccessibleName = "Award progress summary",
            };
            header.Controls.Add(_awardsProgressLbl);

            // What the award counts (accepted confirmations, basis), what Jimmy does not check, and
            // the one short notice that the sponsor decides (2026-10-06). Read once, on Tab.
            _awardsAboutTb = new TextBox
            {
                Text           = "",
                Font           = font,
                Location       = new Point(8, 149),
                Size           = new Size(680, 60),
                Anchor         = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Multiline      = true,
                WordWrap       = true,
                ReadOnly       = true,
                ScrollBars     = ScrollBars.Vertical,
                BorderStyle    = BorderStyle.None,
                BackColor      = SystemColors.Control,
                TabStop        = true,
                TabIndex       = 5,
                AccessibleName = "About this award",
            };
            header.Controls.Add(_awardsAboutTb);

            var buttonRow = new Panel { Dock = DockStyle.Top, Height = 32, TabIndex = 7, AccessibleName = "", AccessibleRole = AccessibleRole.None };
            _awardsManageBtn = new Button
            {
                Text           = "Manage Rule Definitions...",
                Font           = font,
                Location       = new Point(8, 2),
                Size           = new Size(180, 24),
                TabIndex       = 0,
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
                TabIndex       = 1,
                AccessibleName = "Refresh award progress",
            };
            _awardsRefreshBtn.Click += (s, e) => PopulateAwards();
            buttonRow.Controls.Add(_awardsRefreshBtn);

            _awardsLv = MakeListView(font);
            _awardsLv.Dock = DockStyle.Fill;
            _awardsLv.TabIndex = 6;
            _awardsLv.AccessibleName = "Award details";

            // header added before buttonRow so it stacks above it.
            _awardsPanel.Controls.Add(_awardsLv);
            _awardsPanel.Controls.Add(header);
            _awardsPanel.Controls.Add(buttonRow);
        }

        // Opens the Rule Definition Manager and, if anything changed, refreshes every view that
        // reads RuleLibrary.Definitions: this window's award list, plus (via _onImportComplete)
        // the Controller's HRC cache and live-tagging cache.
        private void OpenRuleDefinitionManager()
        {
            using (var mgr = new RuleDefinitionManagerDlg())
            {
                mgr.ShowDialog(this);
                if (mgr.RulesChanged)
                {
                    PopulateAwardsList(force: true);
                    PopulateAwards();
                    _onImportComplete?.Invoke();
                }
            }
        }

        private void BuildEditLogPage(Font font, Font hfont)
        {
            _editLogPanel = MakePage();
            var header = new Panel { Dock = DockStyle.Top, Height = 112, AccessibleName = "", AccessibleRole = AccessibleRole.None };

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

            // Search any field (operator, 2026-10-02: "just my POTA contacts for the day"): Search in
            // "Program", Search for "POTA", with today's date.
            header.Controls.Add(new Label { Text = "Search in:", Font = font, Location = new Point(8, 37), AutoSize = true });
            _editFieldCb = new ComboBox
            {
                Font           = font,
                Location       = new Point(70, 34),
                Size           = new Size(170, 21),
                DropDownStyle  = ComboBoxStyle.DropDownList,
                TabIndex       = 2,
                AccessibleName = "Search in",
            };
            _editFieldCb.Items.Add(LogbookDb.AllFieldsLabel);
            foreach (var f in LogbookDb.SearchFields) _editFieldCb.Items.Add(f.Label);
            _editFieldCb.SelectedIndex = 0;
            header.Controls.Add(_editFieldCb);

            header.Controls.Add(new Label { Text = "for:", Font = font, Location = new Point(246, 37), AutoSize = true });
            _editTextTb = new TextBox
            {
                Font           = font,
                Location       = new Point(274, 34),
                Size           = new Size(140, 20),
                TabIndex       = 3,
                AccessibleName = "Search for",
            };
            _editTextTb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoEditSearch(); } };
            header.Controls.Add(_editTextTb);

            var sourceLbl = new Label { Text = "Source:", Font = font, Location = new Point(196, 11), AutoSize = true };
            header.Controls.Add(sourceLbl);

            _editSourceCb = new ComboBox
            {
                Font           = font,
                Location       = new Point(244, 8),
                Size           = new Size(110, 21),
                DropDownStyle  = ComboBoxStyle.DropDownList,
                TabIndex       = 4,
                AccessibleName = "Source filter",
            };
            _editSourceCb.Items.Add("(Any)");
            _editSourceCb.Items.AddRange(QsoRecord.KnownSources);
            _editSourceCb.SelectedIndex = 0;
            header.Controls.Add(_editSourceCb);

            // Upload status (operator, 2026-10-06): one service and state, e.g. "LoTW: sent, not
            // confirmed" -- with bulk edit's "Mark not sent to", how contacts are sent again.
            header.Controls.Add(new Label { Text = "Upload status:", Font = font, Location = new Point(362, 11), AutoSize = true });
            _editUploadCb = new ComboBox
            {
                Font           = font,
                Location       = new Point(450, 8),
                Size           = new Size(190, 21),
                DropDownStyle  = ComboBoxStyle.DropDownList,
                TabIndex       = 4,
                AccessibleName = "Upload status",
            };
            _editUploadCb.Items.Add(LogbookDb.AnyUploadLabel);
            foreach (var f in LogbookDb.UploadFilters) _editUploadCb.Items.Add(f.Label);
            _editUploadCb.SelectedIndex = 0;
            header.Controls.Add(_editUploadCb);

            var dateFromLbl = new Label { Text = "Date from:", Font = font, Location = new Point(8, 63), AutoSize = true };
            header.Controls.Add(dateFromLbl);

            _editDateFromTb = new TextBox
            {
                Font           = font,
                Location       = new Point(70, 60),
                Size           = new Size(80, 20),
                TabIndex       = 5,
                AccessibleName = "Date from, format year month day, optional",
            };
            _editDateFromTb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoEditSearch(); } };
            header.Controls.Add(_editDateFromTb);

            var dateToLbl = new Label { Text = "to:", Font = font, Location = new Point(156, 63), AutoSize = true };
            header.Controls.Add(dateToLbl);

            _editDateToTb = new TextBox
            {
                Font           = font,
                Location       = new Point(176, 60),
                Size           = new Size(80, 20),
                TabIndex       = 6,
                AccessibleName = "Date to, format year month day, optional",
            };
            _editDateToTb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoEditSearch(); } };
            header.Controls.Add(_editDateToTb);

            _editSearchBtn = new Button
            {
                Text           = "Search",
                AccessibleName = "Search",
                Font           = font,
                Location       = new Point(264, 59),
                Size           = new Size(70, 23),
                TabIndex       = 7,
            };
            _editSearchBtn.Click += (s, e) => DoEditSearch();
            header.Controls.Add(_editSearchBtn);

            _editClearBtn = new Button
            {
                Text           = "Clear",
                AccessibleName = "Clear filters",
                Font           = font,
                Location       = new Point(340, 59),
                Size           = new Size(70, 23),
                TabIndex       = 8,
            };
            _editClearBtn.Click += (s, e) => ClearEditLog();
            header.Controls.Add(_editClearBtn);

            _editCountLbl = new Label
            {
                Text           = "",
                Font           = font,
                Location       = new Point(8, 88),
                AutoSize       = true,
                AccessibleName = "Result count",
            };
            header.Controls.Add(_editCountLbl);

            _editRowOrderBtn = new Button
            {
                Text           = "Row Order...",
                AccessibleName = "Choose column order",
                Font           = font,
                Location       = new Point(416, 59),
                Size           = new Size(90, 23),
                TabIndex       = 9,
            };
            _editRowOrderBtn.Click += RowOrderBtn_Click;
            header.Controls.Add(_editRowOrderBtn);

            // Bottom-docked footer, TabIndex above the list, so the four action buttons stay
            // after the list in tab order exactly as they were when merely Bottom-anchored.
            // AccessibleName=""/AccessibleRole=None -- pure layout container, see MakePage().
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 31, TabIndex = 14, AccessibleName = "", AccessibleRole = AccessibleRole.None };

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
                Text           = "Open...",
                AccessibleName = "Open selected contact",
                Font           = font,
                Location       = new Point(104, 4),
                Size           = new Size(70, 23),
                TabIndex       = 10,
                Enabled        = false,
            };
            _editEditBtn.Click += EditQsoBtn_Click;
            footer.Controls.Add(_editEditBtn);

            // Bulk edit (BulkEditDlg): two or more contacts selected.
            _editBulkBtn = new Button
            {
                Text           = "Edit Selected...",
                AccessibleName = "Edit selected contacts together",
                Font           = font,
                Location       = new Point(180, 4),
                Size           = new Size(110, 23),
                TabIndex       = 11,
                Enabled        = false,
            };
            _editBulkBtn.Click += BulkEditBtn_Click;
            footer.Controls.Add(_editBulkBtn);

            _editDeleteBtn = new Button
            {
                Text           = "Delete...",
                AccessibleName = "Delete selected QSOs",
                Font           = font,
                Location       = new Point(296, 4),
                Size           = new Size(80, 23),
                TabIndex       = 12,
                Enabled        = false,
            };
            _editDeleteBtn.Click += DeleteQsosBtn_Click;
            footer.Controls.Add(_editDeleteBtn);

            _editExportBtn = new Button
            {
                Text           = "Export Selected...",
                AccessibleName = "Export selected QSOs to ADIF",
                Font           = font,
                Location       = new Point(382, 4),
                Size           = new Size(130, 23),
                TabIndex       = 13,
                Enabled        = false,
            };
            _editExportBtn.Click += ExportSelectedBtn_Click;
            footer.Controls.Add(_editExportBtn);

            _editLv = MakeListView(font);
            _editLv.Dock        = DockStyle.Fill;
            _editLv.MultiSelect = true;
            _editLv.TabIndex    = 8;
            RebuildEditLogColumns();
            _editLv.AccessibleName = "Contacts found";
            _editLv.SelectedIndexChanged += (s, e) => UpdateEditLogButtons();
            // Enter or Space on one contact opens it (read only until "Allow editing"), like Open.
            _editLv.KeyDown += (s, e) =>
            {
                if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && !e.Control && !e.Alt && _editLv.SelectedItems.Count == 1)
                {
                    e.SuppressKeyPress = true;
                    EditQsoBtn_Click(_editLv, EventArgs.Empty);
                }
            };
            // Double-click on a contact opens it too, for a mouse user.
            _editLv.MouseDoubleClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left && _editLv.HitTest(e.Location).Item != null && _editLv.SelectedItems.Count == 1)
                    EditQsoBtn_Click(_editLv, EventArgs.Empty);
            };

            _editLogPanel.Controls.Add(_editLv);
            _editLogPanel.Controls.Add(header);
            _editLogPanel.Controls.Add(footer);
        }

        private void ClearEditLog()
        {
            _editCallTb.Text = "";
            _editFieldCb.SelectedIndex = 0;
            _editTextTb.Text = "";
            _editSourceCb.SelectedIndex = 0;
            _editUploadCb.SelectedIndex = 0;
            _editDateFromTb.Text = "";
            _editDateToTb.Text = "";
            _editLv.Items.Clear();
            _editCountLbl.Text = "";
            UpdateEditLogButtons();
            _editCallTb.Focus();
        }

        private Button _editBulkBtn;

        private void UpdateEditLogButtons()
        {
            int n = _editLv.SelectedItems.Count;
            _editEditBtn.Enabled   = n == 1 && !_bulkRunning;
            _editBulkBtn.Enabled   = n >= 2 && !_bulkRunning;
            _editDeleteBtn.Enabled = n >= 1 && !_bulkRunning;
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

                // Every match is found (bulk edit can reach them all); the list shows the newest
                // EditListShown of them, and the count says so when there are more.
                string field  = _editFieldCb.SelectedIndex > 0 ? LogbookDb.SearchFields[_editFieldCb.SelectedIndex - 1].Column : null;
                string text   = _editTextTb.Text.Trim();
                int upload = _editUploadCb.SelectedIndex - 1;   // 0 = Any -> -1, no upload filter
                var found = _db.SearchQsos(call, source, dFrom, dTo, int.MaxValue, field, text, upload);
                _editFoundIds = found.Select(q => q.Id).ToList();
                var results = found.Take(EditListShown).ToList();
                _editLv.BeginUpdate();
                try
                {
                    _editLv.Items.Clear();
                    foreach (var q in results)
                    {
                        var item = new ListViewItem(GetEditLogFieldValue(q, _editLogRowOrder[0])) { Tag = q.Id };
                        for (int i = 1; i < _editLogRowOrder.Count; i++)
                            item.SubItems.Add(GetEditLogFieldValue(q, _editLogRowOrder[i]));
                        _editLv.Items.Add(item);
                    }
                }
                finally { _editLv.EndUpdate(); }
                _editCountLbl.Text = found.Count == 0
                    ? "No QSOs found."
                    : found.Count > results.Count
                    ? $"{found.Count:N0} QSOs found; showing the newest {results.Count:N0}."
                    : $"{found.Count} QSO{(found.Count == 1 ? "" : "s")} found.";
                UpdateEditLogButtons();
            }
            catch (Exception ex) { SetStatus("Search error: " + ex.Message); }
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
            var nexus = (NexusLogbookService)_db;
            NexusQso q;
            try { q = nexus.GetRecord(id); }
            catch (Exception ex) { SetStatus(ex.Message); DoEditSearch(); return; }

            string SaveEdit(NexusQso edited, List<(string Service, bool Sent)> uploadChanges)
            {
                try
                {
                    nexus.SaveRecord(edited, uploadChanges);
                    SetStatus($"Updated {edited.Call}.");
                    DoEditSearch();
                    return null;
                }
                catch (Exception ex) { return "Edit failed: " + ex.Message; }
            }

            // Every field the contact holds (ContactEditDlg's own comment has the rules).
            using (var dlg = new ContactEditDlg(q, SaveEdit) { Owner = this })
            {
                dlg.ShowDialog(this);
            }
        }

        // Bulk edit: the same value into chosen fields of every selected contact (BulkEditDlg has
        // the rules). Asks first, naming the fields and the count; backs the logbook up before
        // the first change; each contact goes back through the same save the contact window uses.
        private void BulkEditBtn_Click(object sender, EventArgs e)
        {
            if (_db == null || _editLv.SelectedItems.Count < 2) return;
            var nexus = (NexusLogbookService)_db;
            var ids = SelectedEditIds();
            Dictionary<string, string> changes;
            List<string> notSent;
            using (var dlg = new BulkEditDlg(ids.Count) { Owner = this })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Changes == null) return;
                changes = dlg.Changes;
                notSent = dlg.NotSent ?? new List<string>();
            }
            // "Change power watts", "Mark not sent to LoTW", or both (2026-10-06: Mark not sent to).
            var actions = new List<string>();
            if (changes.Count > 0) actions.Add("change " + BulkEditDlg.Describe(changes.Keys));
            if (notSent.Count > 0) actions.Add("mark not sent to " + BulkEditDlg.DescribeNotSent(notSent));
            string what = string.Join(" and ", actions);
            what = char.ToUpperInvariant(what[0]) + what.Substring(1);
            string after = notSent.Count > 0
                ? $"The logbook is backed up first. Your next upload to {BulkEditDlg.DescribeNotSent(notSent)} sends these contacts again; contacts it already has are ignored."
                : "The logbook is backed up first. Changes are not sent again to QRZ, Club Log or LoTW.";
            // The list shows only the newest EditListShown contacts found. With ALL of them
            // selected, the one question also asks whether every contact found is meant -- the
            // only way to reach a whole logbook. It replaces the usual confirmation, never adds one.
            bool listCut = _editFoundIds.Count > _editLv.Items.Count && ids.Count == _editLv.Items.Count;
            if (listCut)
            {
                var all = new TaskDialogButton($"All {_editFoundIds.Count:N0}");
                var shown = new TaskDialogButton($"Only these {ids.Count:N0}");
                var page = new TaskDialogPage
                {
                    Caption = "Save Changes",
                    Heading = $"{what} on all {_editFoundIds.Count:N0} contacts found?",
                    Text = $"The list shows only the newest {ids.Count:N0}. {after}",
                    Buttons = { all, shown, TaskDialogButton.Cancel },
                    DefaultButton = TaskDialogButton.Cancel,
                };
                var chosen = TaskDialog.ShowDialog(this, page);
                if (chosen == all) ids = _editFoundIds.ToList();
                else if (chosen != shown)
                {
                    SetStatus("Bulk edit cancelled; nothing changed.");
                    return;
                }
            }
            else if (MessageBox.Show(this,
                    $"{what} on {ids.Count} contacts?\n\n{after}",
                    "Save Changes", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                SetStatus("Bulk edit cancelled; nothing changed.");
                return;
            }

            string backup = Path.Combine(Path.GetDirectoryName(LookupManager.DataRoot), "Backups",
                $"logbook-before-bulk-edit-{DateTime.Now:yyyyMMdd-HHmmss}.adi");
            try { nexus.BackupTo(backup); }
            catch (Exception ex) { SetStatus("Bulk edit stopped before changing anything: " + ex.Message); return; }
            BackupRetention.Prune(Path.GetDirectoryName(backup), "logbook-before-bulk-edit-*.adi");

            // In the background (one read of the log, one rebuild at the end -- NexusLogbookService
            // .BulkEdit), so the window keeps answering; the edit buttons wait until it is done.
            _bulkRunning = true;
            UpdateEditLogButtons();
            SetStatus($"Changing {ids.Count} contacts...");
            Task.Run(() =>
            {
                string result;
                try
                {
                    var parts = new List<string>();
                    if (changes.Count > 0)
                    {
                        var (changed, same, failed, firstError) = nexus.BulkEdit(ids, q => BulkEditDlg.Apply(q, changes));
                        parts.Add($"Changed {changed} contact{(changed == 1 ? "" : "s")}" +
                                  (same > 0 ? $", {same} already had that value" : "") +
                                  (failed > 0 ? $", {failed} failed ({firstError})" : ""));
                    }
                    if (notSent.Count > 0)
                    {
                        var (marked, failed, firstError) = nexus.MarkNotSent(ids, notSent);
                        parts.Add($"Marked {marked} contact{(marked == 1 ? "" : "s")} not sent to {BulkEditDlg.DescribeNotSent(notSent)}" +
                                  (failed > 0 ? $", {failed} failed ({firstError})" : ""));
                    }
                    result = string.Join(". ", parts) + $". Backup: {Path.GetFileName(backup)} in the Backups folder.";
                }
                catch (Exception ex) { result = "Bulk edit stopped: " + ex.Message + $" Backup: {Path.GetFileName(backup)} in the Backups folder."; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        _bulkRunning = false;
                        UpdateEditLogButtons();
                        SetStatus(result);
                        DoEditSearch();
                    }));
                }
                catch (InvalidOperationException) { }   // the window was closed meanwhile
            });
        }

        private bool _bulkRunning;
        // Every match is shown (operator, 2026-10-06: the 500-row cap hid most of a 2,600-contact
        // log from Home/Shift+End and the arrow keys); this ceiling only guards a huge log, and is
        // "logbookListMax" in Shared.ini, to change by hand.
        internal const string ListMaxKey = "logbookListMax";
        internal const int DefaultListMax = 20000;
        private static int EditListShown => SharedIniNumbers.Read(ListMaxKey, DefaultListMax, 100, int.MaxValue);
        private List<int> _editFoundIds = new List<int>();   // every contact the last search found

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
                    // Nexus's own exporter writes the records.
                    var (written, note) = ((NexusLogbookService)_db).ExportAdif(ids, sources, dlg.FileName);
                    SetStatus($"Exported {written:N0} QSO(s) to {dlg.FileName}." + (note != null ? " " + note : ""));
                }
                catch (Exception ex) { SetStatus("Export error: " + ex.Message); }
            }
        }

        // ── Navigation ────────────────────────────────────────────────────────────

        private void NavigateToPage(int page)
        {
            Panel[] pages = { _myLogPanel, _awardsPanel, _editLogPanel, _syncPanel };
            if (page >= 0 && page < pages.Length)
                _activePage = pages[page];

            // Keep the category list in sync when called programmatically
            if (_categoryListBox != null && _categoryListBox.SelectedIndex != page)
                _categoryListBox.SelectedIndex = page;

            switch (page)
            {
                case PAGE_MYLOG:     PopulateMyLog();  break;
                case PAGE_AWARDS:    PopulateAwards(); break;
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
                    _awardsClb, _awardsBandCb, _awardsShowCb, _awardsProgressLbl, _awardsAboutTb, _awardsLv, _awardsManageBtn, _awardsRefreshBtn,
                };
                case PAGE_EDITLOG: return new Control[] {
                    _editCallTb, _editFieldCb, _editTextTb, _editSourceCb, _editUploadCb, _editDateFromTb, _editDateToTb, _editSearchBtn, _editClearBtn,
                    _editRowOrderBtn, _editLv, _editAddBtn, _editEditBtn, _editBulkBtn, _editDeleteBtn, _editExportBtn,
                };
                case PAGE_SYNC: return new Control[] {
                    _syncImportBtn, _syncQrzBtn, _syncLotwBtn, _syncLotwFullBtn, _syncClubLogBtn, _syncEqslBtn, _syncEqslFullBtn, _syncExportBtn,
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

        // Rebuilds the award list from RuleLibrary.Definitions (enabled only), keeping the award
        // being shown by Id. Each item's check reflects whether that award is tracked live
        // (Controller.activeAwardRuleIds), restricted to awards RuleEngine.SupportsLiveTag allows.
        // Skipped when the enabled set is unchanged: PopulateAwards runs on every selection, band
        // and view change, and the list's own SelectedIndexChanged fires during the same click
        // that toggles a not-yet-selected row's box -- rebuilding mid-click could swallow that
        // toggle (found 2026-09-15 on the Still Need page). force: after the Rule Definition
        // Manager, where a name can change under the same Id.
        private void PopulateAwardsList(bool force = false)
        {
            var defs = RuleLibrary.Definitions.Where(d => d.Enabled)
                .OrderBy(d => d.Category ?? "").ThenBy(d => d.Name).ToList();
            if (!force && _awardsDefs.Count == defs.Count && _awardsDefs.Select(d => d.Id).SequenceEqual(defs.Select(d => d.Id)))
            {
                _awardsDefs = defs;
                return;
            }
            string prevId = (_awardsClb.SelectedIndex >= 0 && _awardsClb.SelectedIndex < _awardsDefs.Count)
                ? _awardsDefs[_awardsClb.SelectedIndex].Id : _activeAwardRuleIds.FirstOrDefault();
            _awardsDefs = defs;
            _suppressAwardsEvent = true;
            _awardsClb.Items.Clear();
            if (_awardsDefs.Count == 0)
            {
                _awardsClb.Items.Add("(No Rule Definitions available)");
                _awardsClb.Enabled = false;
                _awardsClb.SelectedIndex = 0;
            }
            else
            {
                _awardsClb.Enabled = true;
                foreach (var d in _awardsDefs)
                    _awardsClb.Items.Add(d.Name, RuleEngine.SupportsLiveTag(d) && _activeAwardRuleIds.Contains(d.Id));
                int idx = prevId != null ? _awardsDefs.FindIndex(d => d.Id == prevId) : -1;
                _awardsClb.SelectedIndex = idx >= 0 ? idx : 0;
            }
            _suppressAwardsEvent = false;
        }

        private void PopulateAwards()
        {
            if (_db == null || _awardsClb == null) return;

            PopulateAwardsList();
            _awardsLv.Items.Clear();
            _awardsLv.Columns.Clear();

            if (_awardsDefs.Count == 0)
            {
                _awardsProgressLbl.Text = RuleLibrary.LoadErrors.Count > 0
                    ? $"No enabled Rule Definitions ({RuleLibrary.LoadErrors.Count} load error(s) — see log_rules_errors.txt)."
                    : "No Rule Definitions found.";
                return;
            }

            int idx = _awardsClb.SelectedIndex;
            if (idx < 0 || idx >= _awardsDefs.Count) return;
            var def = _awardsDefs[idx];
            string about = AboutText(def);
            if (_awardsAboutTb.Text != about) _awardsAboutTb.Text = about;

            // Only the bands that mean something for this award (a band-restricted award can never
            // be evaluated "as" another band -- RuleEngine.ResolveBandsForEvaluation); rebuilt only
            // when the choices differ, so changing the band never disturbs this list.
            var bandChoices = RuleEngine.BandChoicesFor(def.Bands, AllBands);
            if (!_awardsBandCb.Items.Cast<string>().SequenceEqual(bandChoices))
            {
                string prevBand = _awardsBandCb.SelectedIndex > 0 ? (string)_awardsBandCb.SelectedItem : null;
                _suppressAwardsEvent = true;
                _awardsBandCb.Items.Clear();
                _awardsBandCb.Items.AddRange(bandChoices);
                int newIdx = prevBand != null ? Array.IndexOf(bandChoices, prevBand) : -1;
                _awardsBandCb.SelectedIndex = newIdx >= 0 ? newIdx : 0;
                _suppressAwardsEvent = false;
            }

            try
            {
                string band = _awardsBandCb.SelectedIndex <= 0 ? null : (string)_awardsBandCb.SelectedItem;
                bool neededOnly = _awardsShowCb.SelectedIndex == 1;
                // All bands, everything: the full evaluation with endorsements, as the Awards page
                // always showed. A band, or still-needed-only: the band evaluation the Still Need
                // page used (no endorsements).
                var result = band == null && !neededOnly ? RuleEngine.Evaluate(def) : RuleEngine.EvaluateBand(def, band);
                if (neededOnly) RenderNeededResult(def, result, band);
                else RenderAwardResult(def, result, band);
            }
            catch (Exception ex) { SetStatus("Awards error: " + ex.Message); }
        }

        // The end of every summary: the band shown, and whether this award is tracked live.
        private string AwardSummaryTail(RuleDefinition def, string band)
        {
            string onBand = band != null ? $"  On {band}." : "";
            string live = !RuleEngine.SupportsLiveTag(def) ? "  Can't be tracked live."
                : _activeAwardRuleIds.Contains(def.Id) ? "  Tracked live." : "  Not tracked live.";
            return onBand + live;
        }

        // Renders one RuleResult generically, driven entirely by the definition's
        // Target/GroupBy/Confirmation -- no per-award-name branching, so a new
        // Rule Definition file just works without a UI code change.
        private void RenderAwardResult(RuleDefinition def, RuleResult result, string band = null)
        {
            if (result.EvaluationError != null)
            {
                _awardsProgressLbl.Text = "Error: " + result.EvaluationError;
                return;
            }

            // The award's own basis: worked (personal goals) or confirmed by an accepted source
            // (sponsor awards, checklists too since 2026-10-06). The other count is a side note
            // when it differs.
            bool basisIsConfirmed = def.Basis == RuleBasis.Confirmed;
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

            _awardsProgressLbl.Text += AwardSummaryTail(def, band);

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
            bool showBandsCol  = def.GroupBy != RuleGroupBy.None;
            string itemHeader  = def.GroupBy == RuleGroupBy.None ? "Endorsement" : GroupByHeader(def.GroupBy);

            _awardsLv.Columns.Add(itemHeader, 150);
            if (def.GroupBy == RuleGroupBy.Dxcc)
                _awardsLv.Columns.Add("Country", 170);
            if (showBandsCol)
                _awardsLv.Columns.Add("Band(s) worked", 150);
            _awardsLv.Columns.Add("Status", 260);
        }

        // "About this award": what counts, the accepted confirmations, what Jimmy does not check,
        // and -- for a sponsor's award -- that the sponsor decides. Personal goals say they are yours.
        internal static string AboutText(RuleDefinition def)
        {
            bool personal = string.Equals(def.Sponsor, "Personal", StringComparison.OrdinalIgnoreCase)
                            || (def.Category ?? "").StartsWith("Personal", StringComparison.OrdinalIgnoreCase);
            string counts = def.Basis == RuleBasis.Confirmed
                ? $"Counts contacts confirmed by {RuleConfirmationSources.Describe(def)}."
                : def.Confirmation == RuleConfirmation.None
                    ? "Counts worked contacts."
                    : $"Counts worked contacts; confirmations by {RuleConfirmationSources.Describe(def)} are shown.";
            var parts = new List<string> { counts };
            if (!string.IsNullOrWhiteSpace(def.Description)) parts.Add(def.Description.Trim());
            if (!string.IsNullOrWhiteSpace(def.ManualChecks)) parts.Add("Not checked by Jimmy: " + def.ManualChecks.Trim());
            parts.Add(personal
                ? "A personal goal, not an award."
                : "Jimmy tracks your progress from your log and may be incomplete or wrong; the sponsor decides eligibility and issues the award.");
            return string.Join(" ", parts);
        }

        // Which channels confirmed an item, by name, and which of them the award accepts.
        private static (List<string> Accepted, List<string> Others) ConfirmedBy(RuleDefinition def, RuleResult r, string value)
        {
            var accepted = RuleConfirmationSources.Accepted(def);
            if (def.Confirmation == RuleConfirmation.Both) accepted = new List<string> { RuleConfirmationSources.Lotw, RuleConfirmationSources.Qrz };
            var acc = new List<string>();
            var other = new List<string>();
            void Add(List<string> items, string source)
            {
                if (items == null || !items.Contains(value, StringComparer.OrdinalIgnoreCase)) return;
                (accepted.Contains(source) ? acc : other).Add(RuleConfirmationSources.Label(source));
            }
            Add(r.LotwConfirmedItems, RuleConfirmationSources.Lotw);
            Add(r.QrzConfirmedItems, RuleConfirmationSources.Qrz);
            Add(r.CardConfirmedItems, RuleConfirmationSources.Card);
            // An AG eQSL is named once: "eQSL (AG)" when the award takes AG eQSLs, else as plain eQSL.
            bool ag = r.EqslAgConfirmedItems != null && r.EqslAgConfirmedItems.Contains(value, StringComparer.OrdinalIgnoreCase);
            if (ag && accepted.Contains(RuleConfirmationSources.EqslAg)) acc.Add(RuleConfirmationSources.Label(RuleConfirmationSources.EqslAg));
            else Add(r.EqslConfirmedItems, RuleConfirmationSources.Eqsl);
            return (acc, other);
        }

        // One item's status: still to work; worked (and what confirmed it); or confirmed by the
        // accepted sources named.
        internal static string ItemStatus(RuleDefinition def, RuleResult r, string value, bool worked, bool confirmed)
        {
            if (!worked) return "Still to work";
            if (def.Confirmation == RuleConfirmation.None) return "Worked";
            var (acc, others) = ConfirmedBy(def, r, value);
            if (confirmed && acc.Count > 0) return "Confirmed: " + string.Join(", ", acc);
            if (confirmed) return "Confirmed";
            string notAccepted = others.Count > 0 ? $"; {string.Join(", ", others)} not accepted for this award" : "";
            return (def.Basis == RuleBasis.Confirmed ? "Worked, awaiting confirmation" : "Worked, not confirmed") + notAccepted;
        }

        // Renders the per-item checklist (states/entities/zones/etc.) and, when the
        // definition has an [Endorsements] section, a divider row followed by
        // per-band/per-mode sub-results below it (see the divider comment below for
        // why this is a plain row rather than a native ListView group).
        private void BuildAwardRows(RuleDefinition def, RuleResult result)
        {
            bool showBandsCol    = def.GroupBy != RuleGroupBy.None;
            bool hasEndorsements = result.Endorsements != null && result.Endorsements.Count > 0;

            if (def.GroupBy != RuleGroupBy.None)
            {
                var worked    = new HashSet<string>(result.WorkedItems    ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                var confirmed = new HashSet<string>(result.ConfirmedItems ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
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

                    if (showBandsCol)
                    {
                        List<string> bands;
                        row.SubItems.Add(result.WorkedBands != null && result.WorkedBands.TryGetValue(value, out bands)
                            ? string.Join(", ", bands) : "—");
                    }
                    row.SubItems.Add(ItemStatus(def, result, value, worked.Contains(value), confirmed.Contains(value)));

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

                    string status = def.Target == RuleTargetType.Levels
                        ? (end.Tier ?? "—")
                        : (end.Completed ? "Complete" : $"{end.Worked} worked, {end.Confirmed} confirmed");
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

        // "Still needed only": one RuleResult's StillNeeded list, driven by the definition's
        // GroupBy -- no per-award-name branching. A definition whose Target isn't ALL (or whose
        // universe can't be resolved) has no fixed checklist, so StillNeeded is null; that is said
        // plainly rather than treated as an error.
        private void RenderNeededResult(RuleDefinition def, RuleResult result, string band)
        {
            if (result.EvaluationError != null)
            {
                _awardsProgressLbl.Text = "Error: " + result.EvaluationError;
                return;
            }
            if (result.StillNeeded == null)
            {
                _awardsProgressLbl.Text = "This award has no fixed checklist, so there is no still-needed list. Choose Show: Everything for its progress.";
                return;
            }
            string itemHeader = GroupByHeader(def.GroupBy);
            _awardsLv.Columns.Add(itemHeader, 150);
            if (def.GroupBy == RuleGroupBy.Dxcc)
                _awardsLv.Columns.Add("Country", 200);
            _awardsLv.Columns.Add("Status", 260);

            Dictionary<int, string> dxccNames =
                def.GroupBy == RuleGroupBy.Dxcc ? _db.GetDxccCountryNames() : null;

            void AddRow(string value, string status)
            {
                var item = new ListViewItem(value);
                if (dxccNames != null)
                {
                    string name = null;
                    int dxccNum;
                    if (int.TryParse(value, out dxccNum)) dxccNames.TryGetValue(dxccNum, out name);
                    item.SubItems.Add(name ?? "");
                }
                item.SubItems.Add(status);
                _awardsLv.Items.Add(item);
            }
            foreach (var value in result.StillNeeded) AddRow(value, "Still to work");
            var awaiting = result.AwaitingConfirmation ?? new List<string>();
            foreach (var value in awaiting) AddRow(value, ItemStatus(def, result, value, true, false));

            string item_ = itemHeader.ToLowerInvariant();
            _awardsProgressLbl.Text = (awaiting.Count > 0
                ? $"{result.StillNeeded.Count} {item_} still to work, {awaiting.Count} worked and awaiting confirmation."
                : $"{result.StillNeeded.Count} {item_} still to work.") + AwardSummaryTail(def, band);
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
                // Always the complete log (fetched by Nexus's own code, 2026-10-02): an incremental
                // MODSINCE filter can never re-discover a QSO missed on an earlier sync (its own
                // last-modified date on QRZ's side predates every checkpoint since). Found
                // 2026-07-09: 3 confirmed QRZ QSOs stuck exactly this way.
                string key = _qrzApiKey();
                var (adif, error) = await Task.Run(() => NexusLogbookService.DownloadQrzLogbook(key)).ConfigureAwait(true);
                if (adif == null)
                {
                    string msg = "QRZ error: " + (error ?? "Unknown error");
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

        private void LoTWRefreshBtn_Click(object sender, EventArgs e) => RunLotwDownload(full: false);
        private void LoTWFullBtn_Click(object sender, EventArgs e) => RunLotwDownload(full: true);

        // LoTW, fetched by Nexus's own code (2026-10-02): the confirmations matched since the last
        // download's high-water, or every one with full -- see NexusLogbookService.
        // DownloadLotwConfirmations for the rules that keep it from missing one.
        private async void RunLotwDownload(bool full)
        {
            if (_db == null) { SetStatus("Database not available."); return; }
            if (string.IsNullOrWhiteSpace(_lotwUser())) { SetStatus("LoTW credentials not configured."); return; }

            SetBusy(true);
            try
            {
                SetStatus(full ? "Fetching all LoTW confirmations…" : "Fetching new LoTW confirmations…");
                string user = _lotwUser(), pass = _lotwPass();
                var (adif1, highWater, error) = await Task.Run(() =>
                    NexusLogbookService.DownloadLotwConfirmations(user, pass, full)).ConfigureAwait(true);
                if (adif1 == null)
                {
                    string msg = "LoTW error: " + (error ?? "Unknown error");
                    LogSyncFailure("LOTW", msg);
                    SetStatus(msg);
                    return;
                }

                // Only the confirmations download is merged -- see LogbookAutoSync.SyncLotwAsync.
                // The high-water moves only after a clean merge.
                if (await RunImportFromText(adif1, "LOTW", "LogbookLastLoTWRefresh",
                        label: full ? "LoTW full download complete:" : "LoTW sync complete:").ConfigureAwait(true))
                    NexusLogbookService.SaveLotwHighWater(user, highWater);
                string received = await ((NexusLogbookService)_db).LotwReceivedStepAsync(_lotwUser(), _lotwPass()).ConfigureAwait(true);
                if (received != null) SetStatus(SetStatus_Text + "  " + received);
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
                await RunImportFromText(adif, "CLUBLOG", "LogbookLastClubLogRefresh", label: "Club Log download complete:");
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
        private void EqslRefreshBtn_Click(object sender, EventArgs e) => RunEqslDownload(full: false);
        private void EqslFullBtn_Click(object sender, EventArgs e) => RunEqslDownload(full: true);

        // full: the whole eQSL inbox (no since date), as a first run does.
        private async void RunEqslDownload(bool full)
        {
            if (_db == null) { SetStatus("Database not available."); return; }
            if (string.IsNullOrWhiteSpace(_eqslUsername()) || string.IsNullOrWhiteSpace(_eqslPassword()))
            {
                SetStatus("eQSL credentials not configured.");
                return;
            }

            SetStatus(full ? "Fetching all eQSL confirmations…" : "Fetching new eQSL confirmations…");
            SetBusy(true);
            int logId = _db.LogImportStart("EQSL");
            try
            {
                string lastRefresh = full ? null : _ini?.Read("LogbookLastEqslRefresh");
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
                // Cards held for review are not errors (2026-10-06: the history said "Errors: 1" for
                // them); they are on the status line and listed in NexusLog\diagnostics.
                _db.LogImportFinish(logId, processed, 0, result.Matched, 0, skippedTotal, "");
                _ini?.Write("LogbookLastEqslRefresh", DateTime.UtcNow.ToString("o"));

                SetStatus($"{(full ? "eQSL full download complete" : "eQSL sync complete")}: {result}");

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

        // Returns true for a clean import (no errors) -- what a download's checkpoint waits for.
        // label: the status line's opening words ("LoTW sync complete:"); null = "<SOURCE> import complete:".
        private async Task<bool> RunImportFromText(string adifText, string source, string metaKey, bool detected = false, string label = null)
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

                string sourceLabel = detected ? $"Detected source: {source}." : label ?? $"{source} import complete:";
                SetStatus($"{sourceLabel} {result.NewQsos:N0} new, {result.NewlyConfirmed:N0} newly confirmed, {result.Corrected:N0} corrected, {result.Skipped:N0} unchanged{result.UnmatchedText}.");

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
                return string.IsNullOrWhiteSpace(result.Errors);
            }
            catch (Exception ex)
            {
                _db.LogImportFinish(logId, 0, 0, 0, 0, 0, ex.Message);
                SetStatus($"{(detected ? $"Detected source: {source}." : source)} import error: " + ex.Message);
                return false;
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
            else if (_activePage == _editLogPanel)   { if (_editLv.Items.Count > 0) DoEditSearch(); }
            else if (_activePage == _syncPanel)      PopulateSync();
        }

        // Ctrl+F: the Lookup and Edit page's callsign filter.
        private void GoToLookup()
        {
            _categoryListBox.SelectedIndex = PAGE_EDITLOG;
            _editCallTb?.Focus();
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
            _syncLotwFullBtn.Enabled = _syncLotwBtn.Enabled;
            _syncClubLogBtn.Enabled = !busy && !string.IsNullOrWhiteSpace(_clubLogEmail()) &&
                                       !string.IsNullOrWhiteSpace(_clubLogPassword()) && !string.IsNullOrWhiteSpace(_clubLogCallsign());
            _syncEqslBtn.Enabled    = !busy && !string.IsNullOrWhiteSpace(_eqslUsername()) && !string.IsNullOrWhiteSpace(_eqslPassword());
            _syncEqslFullBtn.Enabled = _syncEqslBtn.Enabled;
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

        // SetStatus, also spoken at once: for an action that did NOT do what was asked, so a
        // screen reader user hears why without tabbing to Status (operator, 2026-10-01). A UIA
        // notification from the control in use -- the same mechanism as the main window's
        // RaiseAccessibleAlert: it never moves focus and never self-voices; best effort only.
        private void SetStatusSpoken(Control from, string msg)
        {
            SetStatus(msg);
            try
            {
                (from ?? _statusTb)?.AccessibilityObject.RaiseAutomationNotification(
                    System.Windows.Forms.Automation.AutomationNotificationKind.Other,
                    System.Windows.Forms.Automation.AutomationNotificationProcessing.ImportantMostRecent,
                    msg);
            }
            catch { }
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
