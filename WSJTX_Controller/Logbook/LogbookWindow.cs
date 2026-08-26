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

        // ── Database ──────────────────────────────────────────────────────────────
        private LogbookDb _db;

        // ── Navigation state ──────────────────────────────────────────────────────
        private Panel _activePage;

        // ── Layout controls ───────────────────────────────────────────────────────
        private TabControl _tabControl;
        private TextBox    _statusTb;

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
        private List<RuleDefinition> _awardsDefs = new List<RuleDefinition>();
        private bool     _suppressAwardsEvent;

        // ── Still Need controls ────────────────────────────────────────────────────
        private CheckedListBox _neededAwardsClb;
        private ComboBox _neededBandCb;
        private ListView _neededLv;
        private TextBox  _neededCountLbl;
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
            Action onQsoLogged = null)
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

            Text            = "Ham Radio Center — Logbook";
            MinimumSize     = new Size(720, 500);
            Size            = new Size(950, 660);
            StartPosition   = FormStartPosition.CenterScreen;
            ShowInTaskbar   = true;
            KeyPreview      = true;
            FormBorderStyle = FormBorderStyle.Sizable;

            try
            {
                _db = new LogbookDb();
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

            this.KeyDown    += LogbookWindow_KeyDown;
            this.FormClosed += (s, e) => { _db?.Dispose(); _db = null; };
        }

        // ── UI construction ──────────────────────────────────────────────────────

        private void BuildUi()
        {
            var font  = new Font("Microsoft Sans Serif", 8.25F);
            var hfont = new Font("Microsoft Sans Serif", 9F, FontStyle.Bold);

            // Status bar at the bottom — read-only TextBox so JAWS can focus and read it on demand.
            var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 22, BackColor = SystemColors.Control };

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
            };
            statusPanel.Controls.Add(_statusTb);

            // TabControl — JAWS announces tab name on each tab; Left/Right arrows switch sections.
            _tabControl = new TabControl
            {
                Dock           = DockStyle.Fill,
                Font           = font,
                TabIndex       = 1,
            };

            BuildMyLogPage(font, hfont);
            BuildAwardsPage(font, hfont);
            BuildStillNeedPage(font, hfont);
            BuildLookupPage(font, hfont);
            BuildEditLogPage(font, hfont);
            BuildSyncPage(font, hfont);

            string[] tabNames  = { "My Log", "Awards", "Still Need", "Lookup", "Edit Log", "Sync" };
            Panel[]  tabPanels = { _myLogPanel, _awardsPanel, _stillNeedPanel, _lookupPanel, _editLogPanel, _syncPanel };
            for (int i = 0; i < tabNames.Length; i++)
            {
                tabPanels[i].Dock    = DockStyle.Fill;
                tabPanels[i].Visible = true;
                var tp = new TabPage(tabNames[i]) { UseVisualStyleBackColor = true };
                tp.Controls.Add(tabPanels[i]);
                _tabControl.TabPages.Add(tp);
            }

            _tabControl.SelectedIndexChanged += (s, e) => NavigateToPage(_tabControl.SelectedIndex);

            Controls.Add(_tabControl);
            Controls.Add(statusPanel);

            this.Load += (s, e) =>
            {
                NavigateToPage(PAGE_MYLOG);
                if (RuleLibrary.LoadErrors.Count > 0)
                    SetStatus($"{RuleLibrary.LoadErrors.Count} Rule Definition load error(s) — see log_rules_errors.txt.");
                _tabControl.Focus();
            };
        }

        // ── Page construction ─────────────────────────────────────────────────────

        private void BuildMyLogPage(Font font, Font hfont)
        {
            _myLogPanel = MakePage();
            int y = 8;

            // Individual focusable read-only TextBoxes — JAWS can Tab to each and read the value.
            AddStatField(_myLogPanel, "Total QSOs",         font, ref y, out _statTotalTb, "Total QSOs");
            AddStatField(_myLogPanel, "LoTW confirmed",     font, ref y, out _statLotwTb,  "LoTW confirmed QSOs");
            AddStatField(_myLogPanel, "QRZ confirmed",      font, ref y, out _statQrzTb,   "QRZ confirmed QSOs");
            AddStatField(_myLogPanel, "Combined confirmed", font, ref y, out _statConfTb,  "Combined confirmed QSOs");
            y += 4;
            AddStatField(_myLogPanel, "WAS",  font, ref y, out _statWasTb,  "WAS worked and confirmed");
            AddStatField(_myLogPanel, "DXCC", font, ref y, out _statDxccTb, "DXCC entities worked and confirmed");
            AddStatField(_myLogPanel, "WAZ",  font, ref y, out _statWazTb,  "WAZ zones worked and confirmed");
            y += 8;

            AddSectionLabel(_myLogPanel, "Upload Status", hfont, ref y);
            AddStatField(_myLogPanel, "QRZ",      font, ref y, out _statUploadQrzTb,      "QRZ upload status");
            AddStatField(_myLogPanel, "Club Log", font, ref y, out _statUploadClubLogTb,  "Club Log upload status");
            AddStatField(_myLogPanel, "LoTW",     font, ref y, out _statUploadLotwTb,     "LoTW upload status");
            AddStatField(_myLogPanel, "HRDLog.net", font, ref y, out _statUploadHrdLogTb, "HRDLog.net upload status");
            y += 8;

            var recentLbl = new Label
            {
                Text     = "Recent QSOs",
                Font     = hfont,
                Location = new Point(8, y),
                AutoSize = true,
            };
            _myLogPanel.Controls.Add(recentLbl);
            y += 22;

            _dashRecentLv = MakeListView(font);
            _dashRecentLv.Location = new Point(8, y);
            _dashRecentLv.Size     = new Size(700, 200);
            _dashRecentLv.Anchor   = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
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
            _myLogPanel.Controls.Add(_dashRecentLv);
        }

        private void BuildSyncPage(Font font, Font hfont)
        {
            _syncPanel = MakePage();
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

            _syncPanel.Controls.AddRange(new Control[] { _syncImportBtn, _syncQrzBtn, _syncLotwBtn, _syncClubLogBtn });
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
            _syncPanel.Controls.Add(_syncEqslBtn);
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
            _syncPanel.Controls.Add(_syncExportBtn);
            y += 34;

            AddSectionLabel(_syncPanel, "QRZ Logbook", hfont, ref y);
            _srcQrzStatusLbl = AddInfoLabel(_syncPanel, "Status: not configured", font, ref y);
            y += 4;

            AddSectionLabel(_syncPanel, "LoTW", hfont, ref y);
            _srcLotwStatusLbl = AddInfoLabel(_syncPanel, "Status: not configured", font, ref y);
            y += 4;

            AddSectionLabel(_syncPanel, "eQSL", hfont, ref y);
            _srcEqslStatusLbl = AddInfoLabel(_syncPanel, "Status: not configured", font, ref y);
            y += 4;

            AddSectionLabel(_syncPanel, "Club Log", hfont, ref y);
            _srcClubLogStatusLbl = AddInfoLabel(_syncPanel, "Status: not configured", font, ref y);
            y += 12;

            var histLbl = new Label
            {
                Text     = "Import History (most recent first)",
                Font     = hfont,
                Location = new Point(8, y),
                AutoSize = true,
            };
            _syncPanel.Controls.Add(histLbl);
            y += 22;

            _srcHistoryLv = MakeListView(font);
            _srcHistoryLv.Location = new Point(8, y);
            _srcHistoryLv.Size     = new Size(700, 200);
            _srcHistoryLv.Anchor   = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _srcHistoryLv.Columns.Add("Date/Time",       135);
            _srcHistoryLv.Columns.Add("Source",           65);
            _srcHistoryLv.Columns.Add("New",              50);
            _srcHistoryLv.Columns.Add("Newly Confirmed",  95);
            _srcHistoryLv.Columns.Add("Corrected",        70);
            _srcHistoryLv.Columns.Add("Total",            55);
            _srcHistoryLv.Columns.Add("Errors",          170);
            _srcHistoryLv.AccessibleName = "Import history";
            _syncPanel.Controls.Add(_srcHistoryLv);
        }

        private void BuildAwardsPage(Font font, Font hfont)
        {
            _awardsPanel = MakePage();

            var viewLbl = new Label
            {
                Text     = "Award:",
                Font     = font,
                Location = new Point(8, 10),
                AutoSize = true,
            };
            _awardsPanel.Controls.Add(viewLbl);

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
            _awardsPanel.Controls.Add(_awardsViewCb);

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
            _awardsPanel.Controls.Add(_awardsProgressLbl);

            _awardsLv = MakeListView(font);
            _awardsLv.Location = new Point(8, 66);
            _awardsLv.Anchor   = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _awardsLv.Size     = new Size(700, 350);
            _awardsLv.TabIndex = 3;
            _awardsLv.AccessibleName = "Award details";
            _awardsPanel.Controls.Add(_awardsLv);

            var manageBtn = new Button
            {
                Text           = "Manage Rule Definitions...",
                Font           = font,
                Location       = new Point(8, 34),
                Size           = new Size(180, 24),
                TabIndex       = 4,
                AccessibleName = "Manage Rule Definitions",
            };
            manageBtn.Click += (s, e) => OpenRuleDefinitionManager();
            _awardsPanel.Controls.Add(manageBtn);

            var refreshBtn = new Button
            {
                Text           = "Refresh",
                Font           = font,
                Location       = new Point(196, 34),
                Size           = new Size(90, 24),
                TabIndex       = 5,
                AccessibleName = "Refresh award progress",
            };
            refreshBtn.Click += (s, e) => PopulateAwards();
            _awardsPanel.Controls.Add(refreshBtn);
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

            var typeLbl = new Label
            {
                Text     = "Awards:",
                Font     = font,
                Location = new Point(8, 10),
                AutoSize = true,
            };
            _stillNeedPanel.Controls.Add(typeLbl);

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
                CheckOnClick   = true,
                AccessibleName = "Still Needed awards",
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
            _stillNeedPanel.Controls.Add(_neededAwardsClb);

            var bandLbl = new Label
            {
                Text     = "Band:",
                Font     = font,
                Location = new Point(366, 10),
                AutoSize = true,
            };
            _stillNeedPanel.Controls.Add(bandLbl);

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
            _stillNeedPanel.Controls.Add(_neededBandCb);

            // Read-only TextBox, not a Label -- see the same fix on the Awards tab's
            // _awardsProgressLbl for why a plain Label is unreachable by Tab/screen reader.
            _neededCountLbl = new TextBox
            {
                Text           = "",
                Font           = font,
                Location       = new Point(500, 8),
                Size           = new Size(200, 20),
                ReadOnly       = true,
                BorderStyle    = BorderStyle.None,
                BackColor      = SystemColors.Control,
                TabStop        = true,
                TabIndex       = 3,
                AccessibleName = "Needed entries",
            };
            _stillNeedPanel.Controls.Add(_neededCountLbl);

            var neededRefreshBtn = new Button
            {
                Text           = "Refresh",
                Font           = font,
                Location       = new Point(600, 6),
                Size           = new Size(100, 23),
                TabIndex       = 4,
                AccessibleName = "Refresh needed list",
            };
            neededRefreshBtn.Click += (s, e) => PopulateNeeded();
            _stillNeedPanel.Controls.Add(neededRefreshBtn);

            _neededLv = MakeListView(font);
            _neededLv.Location = new Point(8, 115);
            _neededLv.Anchor   = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _neededLv.Size     = new Size(700, 301);
            _neededLv.TabIndex = 5;
            _neededLv.AccessibleName = "Needed items";
            _stillNeedPanel.Controls.Add(_neededLv);
        }

        private void BuildLookupPage(Font font, Font hfont)
        {
            _lookupPanel = MakePage();

            var searchLbl = new Label
            {
                Text     = "Callsign:",
                Font     = font,
                Location = new Point(8, 11),
                AutoSize = true,
            };
            _lookupPanel.Controls.Add(searchLbl);

            _searchTb = new TextBox
            {
                Font           = font,
                Location       = new Point(68, 8),
                Size           = new Size(140, 20),
                TabIndex       = 1,
                AccessibleName = "Callsign search",
            };
            _searchTb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoSearch(); } };
            _lookupPanel.Controls.Add(_searchTb);

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
            _lookupPanel.Controls.Add(_searchBtn);

            _searchCountLbl = new Label
            {
                Text     = "",
                Font     = font,
                Location = new Point(292, 11),
                AutoSize = true,
                AccessibleName = "Search result count",
            };
            _lookupPanel.Controls.Add(_searchCountLbl);

            _searchLv = MakeListView(font);
            _searchLv.Location = new Point(8, 36);
            _searchLv.Anchor   = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _searchLv.Size     = new Size(700, 380);
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

            _searchClearBtn = new Button
            {
                Text           = "Clear",
                AccessibleName = "Clear search results",
                Font           = font,
                Location       = new Point(8, 420),
                Size           = new Size(70, 23),
                Anchor         = AnchorStyles.Bottom | AnchorStyles.Left,
                TabIndex       = 4,
            };
            _searchClearBtn.Click += (s, e) => ClearSearch();
            _lookupPanel.Controls.Add(_searchClearBtn);
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

            var callLbl = new Label { Text = "Callsign:", Font = font, Location = new Point(8, 11), AutoSize = true };
            _editLogPanel.Controls.Add(callLbl);

            _editCallTb = new TextBox
            {
                Font           = font,
                Location       = new Point(68, 8),
                Size           = new Size(120, 20),
                TabIndex       = 1,
                AccessibleName = "Callsign filter",
            };
            _editCallTb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoEditSearch(); } };
            _editLogPanel.Controls.Add(_editCallTb);

            var sourceLbl = new Label { Text = "Source:", Font = font, Location = new Point(196, 11), AutoSize = true };
            _editLogPanel.Controls.Add(sourceLbl);

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
            _editLogPanel.Controls.Add(_editSourceCb);

            var dateFromLbl = new Label { Text = "Date from:", Font = font, Location = new Point(8, 37), AutoSize = true };
            _editLogPanel.Controls.Add(dateFromLbl);

            _editDateFromTb = new TextBox
            {
                Font           = font,
                Location       = new Point(70, 34),
                Size           = new Size(80, 20),
                TabIndex       = 3,
                AccessibleName = "Date from, format year month day, optional",
            };
            _editDateFromTb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoEditSearch(); } };
            _editLogPanel.Controls.Add(_editDateFromTb);

            var dateToLbl = new Label { Text = "to:", Font = font, Location = new Point(156, 37), AutoSize = true };
            _editLogPanel.Controls.Add(dateToLbl);

            _editDateToTb = new TextBox
            {
                Font           = font,
                Location       = new Point(176, 34),
                Size           = new Size(80, 20),
                TabIndex       = 4,
                AccessibleName = "Date to, format year month day, optional",
            };
            _editDateToTb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoEditSearch(); } };
            _editLogPanel.Controls.Add(_editDateToTb);

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
            _editLogPanel.Controls.Add(_editSearchBtn);

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
            _editLogPanel.Controls.Add(_editClearBtn);

            _editCountLbl = new Label
            {
                Text           = "",
                Font           = font,
                Location       = new Point(8, 62),
                AutoSize       = true,
                AccessibleName = "Result count",
            };
            _editLogPanel.Controls.Add(_editCountLbl);

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
            _editLogPanel.Controls.Add(_editRowOrderBtn);

            _editLv = MakeListView(font);
            _editLv.Location    = new Point(8, 86);
            _editLv.Anchor      = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _editLv.Size        = new Size(700, 330);
            _editLv.MultiSelect = true;
            _editLv.TabIndex    = 8;
            RebuildEditLogColumns();
            _editLv.AccessibleName = "Edit Log results";
            _editLv.SelectedIndexChanged += (s, e) => UpdateEditLogButtons();
            _editLogPanel.Controls.Add(_editLv);

            _editAddBtn = new Button
            {
                Text           = "Add New...",
                AccessibleName = "Add a new QSO",
                Font           = font,
                Location       = new Point(8, 420),
                Size           = new Size(90, 23),
                Anchor         = AnchorStyles.Bottom | AnchorStyles.Left,
                TabIndex       = 9,
            };
            _editAddBtn.Click += AddQsoBtn_Click;
            _editLogPanel.Controls.Add(_editAddBtn);

            _editEditBtn = new Button
            {
                Text           = "Edit...",
                AccessibleName = "Edit selected QSO",
                Font           = font,
                Location       = new Point(104, 420),
                Size           = new Size(70, 23),
                Anchor         = AnchorStyles.Bottom | AnchorStyles.Left,
                TabIndex       = 10,
                Enabled        = false,
            };
            _editEditBtn.Click += EditQsoBtn_Click;
            _editLogPanel.Controls.Add(_editEditBtn);

            _editDeleteBtn = new Button
            {
                Text           = "Delete...",
                AccessibleName = "Delete selected QSOs",
                Font           = font,
                Location       = new Point(180, 420),
                Size           = new Size(80, 23),
                Anchor         = AnchorStyles.Bottom | AnchorStyles.Left,
                TabIndex       = 11,
                Enabled        = false,
            };
            _editDeleteBtn.Click += DeleteQsosBtn_Click;
            _editLogPanel.Controls.Add(_editDeleteBtn);

            _editExportBtn = new Button
            {
                Text           = "Export Selected...",
                AccessibleName = "Export selected QSOs to ADIF",
                Font           = font,
                Location       = new Point(266, 420),
                Size           = new Size(130, 23),
                Anchor         = AnchorStyles.Bottom | AnchorStyles.Left,
                TabIndex       = 12,
                Enabled        = false,
            };
            _editExportBtn.Click += ExportSelectedBtn_Click;
            _editLogPanel.Controls.Add(_editExportBtn);
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
                    SetStatus($"Added {r.Callsign}.");
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

            // Keep TabControl in sync when called programmatically
            if (_tabControl != null && _tabControl.SelectedIndex != page)
                _tabControl.SelectedIndex = page;

            switch (page)
            {
                case PAGE_MYLOG:     PopulateMyLog();  break;
                case PAGE_AWARDS:    PopulateAwards(); break;
                case PAGE_STILLNEED: PopulateNeeded(); break;
                case PAGE_LOOKUP:
                    // Do NOT auto-focus the callsign box here — that steals focus from
                    // normal tab navigation.  GoToLookup() (Ctrl+F) focuses it explicitly.
                    break;
                case PAGE_EDITLOG:
                    // Same reasoning as PAGE_LOOKUP -- no auto-search/auto-focus on switch.
                    break;
                case PAGE_SYNC:      PopulateSync();   break;
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

            // Worked is always the basis now -- RuleEngine gates completion on Worked
            // regardless of Confirmation, so this must match or the summary line could
            // show a "Complete!" that disagrees with a smaller confirmed count. The
            // confirmed count is still shown as an informational side-note when this
            // rule tracks confirmation and it differs from Worked (e.g. some items
            // worked but not yet confirmed via LoTW/QRZ).
            string basisLabel = "worked";
            int basis = result.Worked;
            bool showConfirmedNote = def.Confirmation != RuleConfirmation.None && result.Confirmed != result.Worked;

            switch (def.Target)
            {
                case RuleTargetType.All:
                    string confirmedNote = showConfirmedNote ? $"  ({result.Confirmed} confirmed)" : "";
                    _awardsProgressLbl.Text = $"{basis} / {result.UniverseSize} {basisLabel}{confirmedNote}" +
                        (result.Completed ? "  — Complete!" : "");
                    break;

                case RuleTargetType.Count:
                    string confirmedNoteCount = showConfirmedNote ? $"  ({result.Confirmed} confirmed)" : "";
                    _awardsProgressLbl.Text = $"{basis} / {def.Threshold} {basisLabel}{confirmedNoteCount}" +
                        (result.Completed ? "  — Complete!" : "");
                    break;

                case RuleTargetType.Levels:
                    string tierText = result.CurrentTier != null ? $"Current: {result.CurrentTier}" : "No level reached yet";
                    string next = NextLevelText(def, basis);
                    string confirmedNoteLvl = showConfirmedNote ? $"  ({result.Confirmed} confirmed)" : "";
                    _awardsProgressLbl.Text = $"{basis} {basisLabel}{confirmedNoteLvl}  —  {tierText}" +
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
                await RunImport(dlg.FileName, "MANUAL");
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

        private async Task RunImport(string filePath, string source)
        {
            SetBusy(true);
            SetStatus($"Reading {Path.GetFileName(filePath)}…");
            try
            {
                string text = await Task.Run(() => File.ReadAllText(filePath)).ConfigureAwait(true);
                await RunImportFromText(text, source, null);
            }
            catch (Exception ex)
            {
                SetStatus("Import error: " + ex.Message);
            }
            finally { SetBusy(false); }
        }

        private async Task RunImportFromText(string adifText, string source, string metaKey)
        {
            SetBusy(true);
            int logId = _db.LogImportStart(source);
            ImportResult result = null;

            try
            {
                result = await Task.Run(() =>
                {
                    return AdifImporter.Import(_db, AdifParser.Parse(adifText), source,
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

                SetStatus($"{source} import complete: {result.NewQsos:N0} new, {result.NewlyConfirmed:N0} newly confirmed, {result.Corrected:N0} corrected, {result.Skipped:N0} unchanged.");

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
                SetStatus($"{source} import error: " + ex.Message);
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
            _tabControl.SelectedIndex = PAGE_LOOKUP;
            _searchTb?.Focus();
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static Panel MakePage()
        {
            return new Panel
            {
                Dock       = DockStyle.Fill,
                AutoScroll = true,
                TabIndex   = 5,
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
