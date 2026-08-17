using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    public partial class OptionsDlg : Form
    {
        private Color normalFore;
        private Color normalBack;
        private Color highlightFore;
        private Color highlightBack;
        private Color highlightBackDisabled;

        private bool cqButtonEnabled = false;
        private bool activatorEnabled = false;
        private bool hunterEnabled = false;
        private bool cqDxButtonEnabled = false;
        private bool nonDxButtonEnabled = false;
        private bool dxButtonEnabled = false;
        private bool dxccButtonEnabled = false;

        private List<CheckBox> disableList;

        private WsjtxClient wsjtxClient;
        private Controller ctrl;

        // Index into _categoryListBox (2026-08-10, Options accessibility reorg -- replaces the
        // old tabControl1.SelectedIndex jump). Must match "Hotkeys"'s position in the Items list
        // populated in OptionsDlg.Designer.cs's InitializeComponent(). The other four sibling
        // constants that used to live here (AdvUiTabIndex/WantedCallsTabIndex/SpotWatchTabIndex/
        // SoundsTabIndex) were already dead code before this change -- nothing ever read them --
        // so they're not being carried forward.
        private const int HotkeysCategoryIndex = 4;

        // Advanced UI tab — controls created dynamically in BuildAdvancedUiTab()
        private System.Windows.Forms.CheckBox advCallLayoutCheckBox;
        private System.Windows.Forms.CheckBox advShowTx1CheckBox;
        private System.Windows.Forms.CheckBox advShowTx2CheckBox;
        private System.Windows.Forms.CheckBox advShowRawCheckBox;
        private System.Windows.Forms.NumericUpDown rawMaxRowsNumeric;
        private System.Windows.Forms.NumericUpDown _maxQueuedCallsNumeric;
        private System.Windows.Forms.NumericUpDown _maxCallQueueAgeNumeric;
        private System.Windows.Forms.CheckBox rawShowCqCheckBox;
        private System.Windows.Forms.CheckBox rawShowDirectedCheckBox;
        private System.Windows.Forms.CheckBox rawShowReportsCheckBox;
        private System.Windows.Forms.CheckBox rawShowRR73CheckBox;
        private System.Windows.Forms.CheckBox rawShow73CheckBox;
        private System.Windows.Forms.CheckBox rawShowPotaCheckBox;
        private System.Windows.Forms.CheckBox rawShowSotaCheckBox;
        private System.Windows.Forms.CheckBox rawShowDxCheckBox;
        private System.Windows.Forms.CheckBox rawShowSnrCheckBox;
        private System.Windows.Forms.CheckBox rawShowGridCheckBox;
        private System.Windows.Forms.CheckBox rawShowCountryCheckBox;
        private System.Windows.Forms.CheckBox rawShowDistAzCheckBox;
        private System.Windows.Forms.CheckBox rawOnlyCallsignsCheckBox;
        private System.Windows.Forms.CheckBox rawOnlyUnworkedCheckBox;
        private System.Windows.Forms.CheckBox rawOnlyRankedCheckBox;
        private System.Windows.Forms.CheckBox rawPriorityTagsCheckBox;
        private System.Windows.Forms.CheckBox rawNewestFirstCheckBox;
        private System.Windows.Forms.CheckBox keepTransmitListDuringTxCheckBox;
        private System.Windows.Forms.CheckBox keepListPositionDuringRefreshCheckBox;
        private List<System.Windows.Forms.Control> _advUiDependentControls;

        // Sounds tab state
        private List<SoundRow> _soundRows;
        private System.Windows.Forms.CheckBox _soundsEnabledCb;

        // Lookup / Data tab state
        private System.Windows.Forms.CheckBox        _useLookupDataCb;
        private System.Windows.Forms.CheckBox        _qrzEnabledCb;
        private System.Windows.Forms.TextBox         _qrzUsernameTb;
        private System.Windows.Forms.TextBox         _qrzPasswordTb;
        private System.Windows.Forms.NumericUpDown   _qrzCacheDaysNum;
        private System.Windows.Forms.ComboBox        _qrzPolicyCb;
        private System.Windows.Forms.NumericUpDown   _qrzIntervalNum;
        private System.Windows.Forms.Button          _qrzTestBtn;
        private System.Windows.Forms.TextBox         _qrzStatusLbl;
        private System.Windows.Forms.TextBox         _qrzLogbookApiKeyTb;
        private System.Windows.Forms.CheckBox        _qrzUploadEnabledCb;
        private System.Windows.Forms.CheckBox        _qrzUploadRealtimeCb;
        private System.Windows.Forms.CheckBox        _qrzLogbookAutoSyncCb;
        private System.Windows.Forms.NumericUpDown   _qrzLogbookRefreshDaysNum;
        private System.Windows.Forms.CheckBox        _lotwEnabledCb;
        private System.Windows.Forms.CheckBox        _lotwBoostCb;
        private System.Windows.Forms.NumericUpDown   _lotwRefreshDaysNum;
        private System.Windows.Forms.Button          _lotwUpdateBtn;
        private System.Windows.Forms.TextBox         _lotwStatusLbl;
        private System.Windows.Forms.CheckBox        _lotwUploadEnabledCb;
        private System.Windows.Forms.TextBox         _lotwLogbookUserTb;
        private System.Windows.Forms.TextBox         _lotwLogbookPassTb;
        private System.Windows.Forms.CheckBox        _lotwLogbookAutoSyncCb;
        private System.Windows.Forms.NumericUpDown   _lotwLogbookRefreshDaysNum;
        private System.Windows.Forms.NumericUpDown   _clubLogRefreshDaysNum;
        private System.Windows.Forms.Button          _clubLogUpdateBtn;
        private System.Windows.Forms.TextBox         _clubLogStatusLbl;
        private System.Windows.Forms.CheckBox        _fccUlsEnabledCb;
        private System.Windows.Forms.NumericUpDown   _fccUlsRefreshDaysNum;
        private System.Windows.Forms.Button          _fccUlsUpdateBtn;
        private System.Windows.Forms.TextBox         _fccUlsStatusLbl;
        private System.Windows.Forms.CheckBox        _clubLogUploadEnabledCb;
        private System.Windows.Forms.CheckBox        _clubLogUploadRealtimeCb;
        private System.Windows.Forms.TextBox         _clubLogUploadEmailTb;
        private System.Windows.Forms.TextBox         _clubLogUploadPasswordTb;
        private System.Windows.Forms.TextBox         _clubLogUploadCallsignTb;
        private System.Windows.Forms.CheckBox        _clubLogLogbookAutoSyncCb;
        private System.Windows.Forms.NumericUpDown   _clubLogLogbookRefreshDaysNum;
        private System.Windows.Forms.CheckBox        _hrdLogUploadEnabledCb;
        private System.Windows.Forms.CheckBox        _hrdLogUploadRealtimeCb;
        private System.Windows.Forms.TextBox         _hrdLogUploadCallsignTb;
        private System.Windows.Forms.TextBox         _hrdLogUploadCodeTb;
        private System.Windows.Forms.TextBox         _tqslStationLocationTb;
        private System.Windows.Forms.CheckBox        _eqslUploadEnabledCb;
        private System.Windows.Forms.CheckBox        _eqslUploadRealtimeCb;
        private System.Windows.Forms.TextBox         _eqslUsernameTb;
        private System.Windows.Forms.TextBox         _eqslPasswordTb;
        private System.Windows.Forms.CheckBox        _hamQthEnabledCb;
        private System.Windows.Forms.TextBox         _hamQthUsernameTb;
        private System.Windows.Forms.TextBox         _hamQthPasswordTb;

        private sealed class SoundRow
        {
            public string Key;
            public System.Windows.Forms.CheckBox EnabledCb;
            public System.Windows.Forms.TextBox  FileTb;
        }

        // Hotkeys tab state
        private Dictionary<HotkeyAction, Keys> _pendingKeys;
        private List<HotkeyAction?>             _listActionMap;
        private HotkeyCaptureBox                _sharedCaptureBox;
        private int                              _lastRealActionIndex = -1;
        private ListBox                         _actionListBox;

        // Wanted Calls tab
        private System.Windows.Forms.TextBox    wantedCallsTextBox;
        private System.Windows.Forms.CheckBox   _wantedCallAnywhereCheckBox;

        // Spot Watch tab
        private System.Windows.Forms.TextBox    spotWatchCallsTextBox;
        private System.Windows.Forms.CheckBox   _showSpotWatchCheckBox;
        private System.Windows.Forms.ComboBox   _spotWatchSortCb;

        // General tab
        private System.Windows.Forms.CheckBox pskReporterCheckBox;
        private System.Windows.Forms.CheckBox moveFocusToStatusCheckBox;
        private System.Windows.Forms.CheckBox checkForUpdatesCheckBox;

        // Appearance tab
        private System.Windows.Forms.ComboBox appearanceThemeCombo;
        private System.Windows.Forms.NumericUpDown appearanceFontSizeNumeric;
        private System.Windows.Forms.Button appearanceBackColorButton;
        private System.Windows.Forms.Button appearanceForeColorButton;
        private System.Windows.Forms.Button appearanceAltRowColorButton;
        private Color _appearanceBackColor;
        private Color _appearanceForeColor;
        private Color _appearanceAltRowColor;

        // Alert category colors (Options > Appearance)
        private System.Windows.Forms.ComboBox alertCategoryCombo;
        private System.Windows.Forms.Button alertForeColorButton;
        private System.Windows.Forms.Button alertBackColorButton;
        private System.Windows.Forms.Button alertClearColorButton;
        private Dictionary<WsjtxClient.CallCategory, Color?> _alertForeColors;
        private Dictionary<WsjtxClient.CallCategory, Color?> _alertBackColors;

        private Dictionary<Control, Control> originalParents = new Dictionary<Control, Control>();
        private Dictionary<Control, Point> originalLocations = new Dictionary<Control, Point>();
        private List<Control> reparentedControls = new List<Control>();

        public OptionsDlg(WsjtxClient wsjtxClient, Controller ctrl)
        {
            InitializeComponent();

            this.wsjtxClient = wsjtxClient;
            this.ctrl = ctrl;

            normalFore = okButton.ForeColor;
            normalBack = okButton.BackColor;
            highlightFore = Color.White;
            highlightBack = Color.Gray;
            highlightBackDisabled = Color.LightGray;

            disableList = new List<CheckBox>
            {
                listenButton, callCqButton,
                cqButton, cqDxButton,
                dxButton, nonDxButton,
                potaButton, hunterButton,
                allButton, recentButton
            };
        }

        public void UpdateView()
        {
            UpdateAllButtons();
        }

        private void OptionsDlg_Load(object sender, EventArgs e)
        {
            Screen screen = Screen.FromControl(ctrl);
            Location = new Point(
                screen.Bounds.X + (screen.Bounds.Width - Width) / 2,
                screen.Bounds.Y + (screen.Bounds.Height - Height) / 2);

            udpOnTopCheckBox.Checked = ctrl.alwaysOnTop;
            udpDiagLogCheckBox.Checked = wsjtxClient.diagLog;
            BuildGeneralTab();
            BuildHotkeysTab();
            BuildAdvancedUiTab();
            BuildWantedCallsTab();
            BuildSpotWatchTab();
            BuildRadioTab();
            BuildDecodeEngineTab();
            BuildDecodeTab();
            BuildFrequenciesTab();
            BuildNotificationsTab();
            BuildSoundsTab();
            BuildLogbookSyncTab();
            BuildLookupDataTab();
            BuildAppearanceTab();
            ReparentControlsToDialog();

            // Order must match _categoryListBox.Items (OptionsDlg.Designer.cs) and
            // HotkeysCategoryIndex above -- basicPanel first, so the very first item is
            // already visible before subtitleLabel.Focus() below.
            WireCategoryList(_categoryListBox, _categoryDetailHost, new List<Control> {
                basicPanel, generalPanel, receiveReplyPanel, transmitPanel, hotkeysPanel,
                advUiPanel, wantedCallsPanel, spotWatchPanel, soundsPanel, radioPanel,
                decodeEnginePanel, decodePanel, frequenciesPanel, notificationsPanel, logbookSyncPanel, lookupPanel,
                appearancePanel
            });

            UpdateAllButtons();
            dxccButtonEnabled = false;  // Phase 3: New DXCC exclusive mode removed
            UpdateAllButtons();

            subtitleLabel.Focus();
        }

        // Generalizes WireServiceList (below, still used as-is inside Logbook Sync/Lookup Data)
        // to the whole dialog: shows only the item matching _categoryListBox's current
        // selection, hiding the rest. Control instead of GroupBox purely for generality --
        // every one of the 16 top-level items is a plain Panel (basicPanel included: originally
        // a bare TabPage, converted to a Panel here -- confirmed live that WinForms' TabPage
        // throws ArgumentException if reparented to anything other than a real TabControl,
        // despite TabPage technically inheriting from Panel).
        private static void WireCategoryList(ListBox listBox, Control host, List<Control> panels)
        {
            Control current = null;
            void UpdateVisibility()
            {
                if (current != null) host.Controls.Remove(current);
                int idx = listBox.SelectedIndex;
                current = (idx >= 0 && idx < panels.Count) ? panels[idx] : null;
                if (current != null) host.Controls.Add(current);
            }
            listBox.SelectedIndexChanged += (s, e) => UpdateVisibility();
            if (listBox.Items.Count > 0) listBox.SelectedIndex = 0;
            UpdateVisibility();
        }

        // ===== GENERAL TAB =====

        private void BuildGeneralTab()
        {
            var font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F);

            pskReporterCheckBox = new System.Windows.Forms.CheckBox
            {
                Text                  = "PSK Reporter Enabled",
                AccessibleName        = "PSK Reporter enabled",
                AccessibleDescription = "Send spots to PSK Reporter. Same as the PSK Reporter hotkey.",
                AutoSize              = true,
                Location              = new System.Drawing.Point(10, 38),
                TabIndex              = 1,
                Checked               = wsjtxClient.usePskReporter,
                Font                  = font,
            };
            generalPanel.Controls.Add(pskReporterCheckBox);

            moveFocusToStatusCheckBox = new System.Windows.Forms.CheckBox
            {
                Text                  = "Move focus to status after selecting a call",
                AccessibleName        = "Move focus to status after selecting a call",
                AccessibleDescription = "After pressing Enter or Space to select a call in any call list (simple or advanced), move keyboard focus to the status line and re-announce it. Off by default.",
                AutoSize              = true,
                Location              = new System.Drawing.Point(10, 62),
                TabIndex              = 2,
                Checked               = ctrl.moveFocusToStatusOnCallSelect,
                Font                  = font,
            };
            generalPanel.Controls.Add(moveFocusToStatusCheckBox);

            var maxCallQueueAgeLabel = new System.Windows.Forms.Label
            {
                Text     = "Max call-queue age (periods):",
                AutoSize = true,
                Location = new System.Drawing.Point(10, 90),
                Font     = font,
                TabStop  = false
            };
            generalPanel.Controls.Add(maxCallQueueAgeLabel);

            _maxCallQueueAgeNumeric = new System.Windows.Forms.NumericUpDown
            {
                AccessibleName        = "Max call-queue age in periods",
                AccessibleDescription = "How many TX/RX periods an auto-queued call may go unheard before being dropped from the waiting list. 16 periods is about 4 minutes on FT8, 2 minutes on FT4. Manually-selected calls and New DXCC entries are never dropped by this.",
                Location              = new System.Drawing.Point(210, 87),
                Size                  = new System.Drawing.Size(70, 20),
                TabIndex              = 3,
                Minimum               = 4,
                Maximum               = 200,
                Value                 = Math.Max(4, Math.Min(200, ctrl.maxCallQueueAgePeriods)),
                Font                  = font,
            };
            generalPanel.Controls.Add(_maxCallQueueAgeNumeric);

            checkForUpdatesCheckBox = new System.Windows.Forms.CheckBox
            {
                Text                  = "Check for updates on startup",
                AccessibleName        = "Check for updates on startup",
                AccessibleDescription = "On startup, check GitHub for a newer Jimmy release. If one is found, ask whether to download and install it. Off by default.",
                AutoSize              = true,
                Location              = new System.Drawing.Point(10, 115),
                TabIndex              = 4,
                Checked               = ctrl.checkForUpdatesOnStartup,
                Font                  = font,
            };
            generalPanel.Controls.Add(checkForUpdatesCheckBox);
        }

        private void ApplyGeneralSettings()
        {
            if (pskReporterCheckBox != null &&
                pskReporterCheckBox.Checked != wsjtxClient.usePskReporter)
            {
                wsjtxClient.TogglePskReporter();
            }

            ctrl.moveFocusToStatusOnCallSelect = moveFocusToStatusCheckBox?.Checked ?? false;
            ctrl.checkForUpdatesOnStartup = checkForUpdatesCheckBox?.Checked ?? false;

            int maxAge = (int)(_maxCallQueueAgeNumeric?.Value ?? 16);
            ctrl.maxCallQueueAgePeriods = Math.Max(4, Math.Min(200, maxAge));

            ctrl.alwaysOnTop = udpOnTopCheckBox.Checked;
            wsjtxClient.LogModeChanged(udpDiagLogCheckBox.Checked);
        }

        private void OptionsDlg_FormClosing(object sender, FormClosingEventArgs e)
        {
        }

        private void OptionsDlg_FormClosed(object sender, FormClosedEventArgs e)
        {
            ReparentControlsBack();
            ctrl.OptionsDlgClosed();
        }

        private void OptionsDlg_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { e.Handled = true; e.SuppressKeyPress = true; Close(); return; }
            // When the capture box has focus, let the key pass through to it.
            if (IsCaptureFieldFocused()) return;
            if (e.Control && e.KeyCode == Keys.Q) Close();
        }

        // ===== OK / CANCEL =====

        private void okButton_Click(object sender, EventArgs e)
        {
            if (!ValidateHotkeys()) return;
            ApplyGeneralSettings();
            SaveHotkeysTab();
            SaveAdvancedUiTab();
            SaveWantedCallsTab();
            SaveSpotWatchTab();
            SaveRadioTab();
            SaveDecodeTab();
            SaveFrequenciesTab();
            SaveNotificationsTab();
            SaveSoundsTab();
            SaveLookupTab();
            SaveAppearanceTab();
            // Give Club Log real-time upload another chance now that the user has
            // had an opportunity to fix credentials/settings -- see
            // LiveQsoUploadOrchestrator's circuit breaker.
            ctrl.wsjtxClient?.LiveQsoUploader?.ResetClubLogRealtimeBreaker();
            ctrl.ApplyAdvancedLayout();
            ctrl.ApplyListAppearance();
            Close();
        }

        private void cancelButton_Click(object sender, EventArgs e)
        {
            Close();
        }

        // ===== ADVANCED UI TAB =====

        private void BuildAdvancedUiTab()
        {
            advUiPanel.Controls.Clear();
            _advUiDependentControls = new List<System.Windows.Forms.Control>();

            var font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F);
            var boldFont = new System.Drawing.Font("Microsoft Sans Serif", 8.25F, System.Drawing.FontStyle.Bold);
            int y = 8;
            const int left = 5;
            const int right = 330;
            const int groupW = 315;
            const int fullW = 650;

            // ── Group: Advanced call waiting layout ──────────────────────────────
            var layoutGroup = new System.Windows.Forms.GroupBox
            {
                Text = "Advanced stations available layout",
                Location = new System.Drawing.Point(left, y),
                Size = new System.Drawing.Size(fullW, 157),
                Font = font,
                TabStop = false,
                AccessibleName = "Advanced stations available layout options"
            };

            advCallLayoutCheckBox = new System.Windows.Forms.CheckBox
            {
                Text = "Enable advanced stations available layout",
                AccessibleName = "Enable advanced stations available layout",
                AutoSize = true,
                Location = new System.Drawing.Point(8, 20),
                TabIndex = 0,
                Font = boldFont,
                Checked = ctrl.advancedCallLayout
            };
            advCallLayoutCheckBox.CheckedChanged += (s, e) => UpdateAdvUiDependentEnabled();
            layoutGroup.Controls.Add(advCallLayoutCheckBox);

            advShowTx1CheckBox = MakeCheck(layoutGroup, "Show TX1 available stations", "Show TX1 available stations", 8, 44, 1, ctrl.advShowTx1, font);
            advShowTx2CheckBox = MakeCheck(layoutGroup, "Show TX2 available stations", "Show TX2 available stations", 210, 44, 2, ctrl.advShowTx2, font);
            advShowRawCheckBox = MakeCheck(layoutGroup, "Show raw decodes", "Show raw decodes", 8, 66, 3, ctrl.advShowRaw, font);
            keepTransmitListDuringTxCheckBox = MakeCheck(layoutGroup, "Keep transmit list during transmit", "Keep transmit list during transmit", 8, 88, 4, ctrl.keepTransmitListDuringTx, font);
            var maxLabel = new System.Windows.Forms.Label
            {
                Text = "Maximum raw decode rows:",
                AutoSize = true,
                Location = new System.Drawing.Point(8, 113),
                Font = font,
                TabStop = false
            };
            layoutGroup.Controls.Add(maxLabel);

            rawMaxRowsNumeric = new System.Windows.Forms.NumericUpDown
            {
                AccessibleName = "Maximum raw decode rows",
                Location = new System.Drawing.Point(195, 110),
                Size = new System.Drawing.Size(70, 20),
                TabIndex = 5,
                Minimum = 10,
                Maximum = 5000,
                Value = Math.Max(10, Math.Min(5000, ctrl.rawMaxRows)),
                Font = font
            };
            layoutGroup.Controls.Add(rawMaxRowsNumeric);
            _advUiDependentControls.Add(maxLabel);
            _advUiDependentControls.Add(rawMaxRowsNumeric);

            var maxQueuedLabel = new System.Windows.Forms.Label
            {
                Text = "Max queued calls:",
                AutoSize = true,
                Location = new System.Drawing.Point(8, 136),
                Font = font,
                TabStop = false
            };
            layoutGroup.Controls.Add(maxQueuedLabel);

            _maxQueuedCallsNumeric = new System.Windows.Forms.NumericUpDown
            {
                AccessibleName = "Max queued calls",
                AccessibleDescription = "Maximum number of calls held in the waiting queue across TX1 and TX2 combined. Increase to see more callers in the advanced TX1/TX2 lists.",
                Location = new System.Drawing.Point(195, 133),
                Size = new System.Drawing.Size(70, 20),
                TabIndex = 6,
                Minimum = 4,
                Maximum = 100,
                Value = Math.Max(4, Math.Min(100, ctrl.maxQueuedCallsBase)),
                Font = font
            };
            layoutGroup.Controls.Add(_maxQueuedCallsNumeric);
            _advUiDependentControls.Add(maxQueuedLabel);
            _advUiDependentControls.Add(_maxQueuedCallsNumeric);

            advUiPanel.Controls.Add(layoutGroup);
            y += 165;

            keepListPositionDuringRefreshCheckBox = new System.Windows.Forms.CheckBox
            {
                Text = "Keep list position during refresh",
                AccessibleName = "Keep list position during refresh",
                AccessibleDescription = "Keeps the selected row when lists refresh. Uncheck for quieter screen-reader behavior.",
                AutoSize = true,
                Location = new System.Drawing.Point(left + 8, y),
                TabIndex = 23,
                Checked = ctrl.keepListPositionDuringRefresh,
                Font = font
            };
            advUiPanel.Controls.Add(keepListPositionDuringRefreshCheckBox);
            y += 24;

            // ── Group: Message types ──────────────────────────────────────────────
            var msgGroup = new System.Windows.Forms.GroupBox
            {
                Text = "Message types to show in raw decodes",
                Location = new System.Drawing.Point(left, y),
                Size = new System.Drawing.Size(groupW, 125),
                Font = font,
                TabStop = false,
                AccessibleName = "Message types in raw decodes"
            };
            rawShowCqCheckBox       = MakeCheck(msgGroup, "CQ messages",     "CQ messages",       8,   22,  5, ctrl.rawShowCq,       font);
            rawShowDirectedCheckBox = MakeCheck(msgGroup, "Directed calls",   "Directed calls",    8,   44,  6, ctrl.rawShowDirected,  font);
            rawShowReportsCheckBox  = MakeCheck(msgGroup, "Signal reports",   "Signal reports",    8,   66,  7, ctrl.rawShowReports,   font);
            rawShowRR73CheckBox     = MakeCheck(msgGroup, "RR73 messages",    "RR73 messages",     8,   88,  8, ctrl.rawShowRR73,      font);
            rawShow73CheckBox       = MakeCheck(msgGroup, "73 messages",      "73 messages",       165, 22,  9, ctrl.rawShow73,        font);
            rawShowPotaCheckBox     = MakeCheck(msgGroup, "POTA messages",    "POTA messages",     165, 44, 10, ctrl.rawShowPota,      font);
            rawShowSotaCheckBox     = MakeCheck(msgGroup, "SOTA messages",    "SOTA messages",     165, 66, 11, ctrl.rawShowSota,      font);
            rawShowDxCheckBox       = MakeCheck(msgGroup, "DX messages",      "DX messages",       165, 88, 12, ctrl.rawShowDx,        font);
            advUiPanel.Controls.Add(msgGroup);

            // ── Group: Display fields ─────────────────────────────────────────────
            var displayGroup = new System.Windows.Forms.GroupBox
            {
                Text = "Display fields in raw decodes",
                Location = new System.Drawing.Point(right, y),
                Size = new System.Drawing.Size(groupW, 125),
                Font = font,
                TabStop = false,
                AccessibleName = "Display fields in raw decodes"
            };
            rawShowSnrCheckBox     = MakeCheck(displayGroup, "SNR",               "Show SNR",                8,   22, 13, ctrl.rawShowSnr,     font);
            rawShowGridCheckBox    = MakeCheck(displayGroup, "Grid",              "Show Grid",               8,   44, 14, ctrl.rawShowGrid,     font);
            rawShowCountryCheckBox = MakeCheck(displayGroup, "Country",           "Show Country",            8,   66, 15, ctrl.rawShowCountry,  font);
            rawShowDistAzCheckBox  = MakeCheck(displayGroup, "Distance/Azimuth",  "Show Distance Azimuth",  165, 22, 16, ctrl.rawShowDistAz,   font);
            advUiPanel.Controls.Add(displayGroup);
            y += 132;

            // ── Group: Advanced filters ───────────────────────────────────────────
            var filtersGroup = new System.Windows.Forms.GroupBox
            {
                Text = "Advanced filters",
                Location = new System.Drawing.Point(left, y),
                Size = new System.Drawing.Size(fullW, 120),
                Font = font,
                TabStop = false,
                AccessibleName = "Advanced filters group"
            };
            rawOnlyCallsignsCheckBox = MakeCheck(filtersGroup, "Show only decodes containing callsigns",           "Only callsigns",         8,  20, 18, ctrl.rawOnlyCallsigns, font);
            rawOnlyUnworkedCheckBox  = MakeCheck(filtersGroup, "Show only stations not previously worked",          "Only unworked",           8,  44, 19, ctrl.rawOnlyUnworked,  font);
            rawOnlyRankedCheckBox    = MakeCheck(filtersGroup, "Show only stations matching current ranking filters","Only ranked",             8,  68, 20, ctrl.rawOnlyRanked,    font);
            rawPriorityTagsCheckBox  = MakeCheck(filtersGroup, "Show priority tags in Raw Decodes",                 "Show priority tags",      8,  92, 21, ctrl.rawPriorityTags,  font);
            rawNewestFirstCheckBox   = MakeCheck(filtersGroup, "Show newest decodes at top",                        "Newest at top",           8, 116, 22, ctrl.rawNewestFirst,   font);
            advUiPanel.Controls.Add(filtersGroup);

            // All groups (except the enable checkbox itself) are dependent controls
            _advUiDependentControls.Add(advShowTx1CheckBox);
            _advUiDependentControls.Add(advShowTx2CheckBox);
            _advUiDependentControls.Add(advShowRawCheckBox);
            _advUiDependentControls.Add(keepTransmitListDuringTxCheckBox);
            _advUiDependentControls.Add(msgGroup);
            _advUiDependentControls.Add(displayGroup);
            _advUiDependentControls.Add(filtersGroup);
            _advUiDependentControls.Add(rawPriorityTagsCheckBox);

            UpdateAdvUiDependentEnabled();
        }

        private System.Windows.Forms.CheckBox MakeCheck(
            System.Windows.Forms.Control parent, string text, string accessibleName,
            int x, int y, int tabIndex, bool chk, System.Drawing.Font font)
        {
            var cb = new System.Windows.Forms.CheckBox
            {
                Text = text,
                AccessibleName = accessibleName,
                AutoSize = true,
                Location = new System.Drawing.Point(x, y),
                TabIndex = tabIndex,
                Checked = chk,
                Font = font
            };
            parent.Controls.Add(cb);
            return cb;
        }

        private void UpdateAdvUiDependentEnabled()
        {
            bool en = advCallLayoutCheckBox?.Checked ?? false;
            if (_advUiDependentControls == null) return;
            foreach (var c in _advUiDependentControls)
                c.Enabled = en;
        }

        private void SaveAdvancedUiTab()
        {
            ctrl.advancedCallLayout = advCallLayoutCheckBox?.Checked ?? false;
            ctrl.advShowTx1 = advShowTx1CheckBox?.Checked ?? true;
            ctrl.advShowTx2 = advShowTx2CheckBox?.Checked ?? true;
            ctrl.advShowRaw = advShowRawCheckBox?.Checked ?? true;
            ctrl.rawShowCq        = rawShowCqCheckBox?.Checked ?? true;
            ctrl.rawShowDirected  = rawShowDirectedCheckBox?.Checked ?? true;
            ctrl.rawShowReports   = rawShowReportsCheckBox?.Checked ?? true;
            ctrl.rawShowRR73      = rawShowRR73CheckBox?.Checked ?? false;
            ctrl.rawShow73        = rawShow73CheckBox?.Checked ?? false;
            ctrl.rawShowPota      = rawShowPotaCheckBox?.Checked ?? true;
            ctrl.rawShowSota      = rawShowSotaCheckBox?.Checked ?? true;
            ctrl.rawShowDx        = rawShowDxCheckBox?.Checked ?? true;
            ctrl.rawShowSnr       = rawShowSnrCheckBox?.Checked ?? true;
            ctrl.rawShowGrid      = rawShowGridCheckBox?.Checked ?? true;
            ctrl.rawShowCountry   = rawShowCountryCheckBox?.Checked ?? true;
            ctrl.rawShowDistAz    = rawShowDistAzCheckBox?.Checked ?? false;
            ctrl.rawOnlyCallsigns  = rawOnlyCallsignsCheckBox?.Checked ?? false;
            ctrl.rawOnlyUnworked   = rawOnlyUnworkedCheckBox?.Checked ?? false;
            ctrl.rawOnlyRanked     = rawOnlyRankedCheckBox?.Checked ?? false;
            ctrl.rawPriorityTags   = rawPriorityTagsCheckBox?.Checked ?? false;
            if (ctrl.wsjtxClient != null) ctrl.wsjtxClient.rawPriorityTags = ctrl.rawPriorityTags;
            ctrl.rawNewestFirst    = rawNewestFirstCheckBox?.Checked ?? false;
            ctrl.keepTransmitListDuringTx = keepTransmitListDuringTxCheckBox?.Checked ?? false;
            ctrl.keepListPositionDuringRefresh = keepListPositionDuringRefreshCheckBox?.Checked ?? false;
            int rawMax = (int)(rawMaxRowsNumeric?.Value ?? 100);
            ctrl.rawMaxRows = Math.Max(10, Math.Min(5000, rawMax));
            int maxQueued = (int)(_maxQueuedCallsNumeric?.Value ?? 4);
            ctrl.maxQueuedCallsBase = Math.Max(4, Math.Min(100, maxQueued));
        }

        // ===== WANTED CALLS TAB =====

        private void BuildWantedCallsTab()
        {
            wantedCallsPanel.Controls.Clear();

            var font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F);
            int y = 8;
            const int left = 8;
            const int w = 640;

            // Instruction label
            var instrBox = new System.Windows.Forms.TextBox
            {
                ReadOnly       = true,
                Multiline      = true,
                BorderStyle    = System.Windows.Forms.BorderStyle.None,
                BackColor      = wantedCallsPanel.BackColor,
                ForeColor      = System.Drawing.SystemColors.ControlText,
                Location       = new System.Drawing.Point(left, y),
                Size           = new System.Drawing.Size(w, 60),
                Text           = "Enter callsigns to always elevate in priority (one per line, or comma/space separated).\r\n" +
                                 "Examples: W1AW/0, VP8SGI, 3Y0K\r\n" +
                                 "Matching calls receive the \"Always Wanted Calls\" category. Case-insensitive. Duplicates are ignored.",
                TabStop        = false,
                AccessibleName = "Wanted Calls instructions",
                Font           = font,
            };
            wantedCallsPanel.Controls.Add(instrBox);
            y += 68;

            // Edit box label
            var editLabel = new System.Windows.Forms.Label
            {
                Text           = "Wanted callsigns:",
                AccessibleName = "Wanted callsigns label",
                AutoSize       = true,
                Location       = new System.Drawing.Point(left, y),
                Font           = font,
                TabStop        = false,
            };
            wantedCallsPanel.Controls.Add(editLabel);
            y += 20;

            // Multiline text box
            wantedCallsTextBox = new System.Windows.Forms.TextBox
            {
                Multiline      = true,
                ScrollBars     = System.Windows.Forms.ScrollBars.Vertical,
                Location       = new System.Drawing.Point(left, y),
                Size           = new System.Drawing.Size(w, 220),
                TabIndex       = 0,
                AccessibleName = "Wanted callsigns",
                AccessibleDescription = "Enter callsigns to always prioritize. One per line, or comma or space separated. Case-insensitive.",
                Font           = font,
            };
            // Populate from current wanted calls (sorted for readability)
            var sorted = new List<string>(wsjtxClient.wantedCalls);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            wantedCallsTextBox.Text = string.Join(Environment.NewLine, sorted);
            wantedCallsPanel.Controls.Add(wantedCallsTextBox);
            y += 228;

            // Checkbox: alert when wanted call heard anywhere
            _wantedCallAnywhereCheckBox = new System.Windows.Forms.CheckBox
            {
                Text           = "Alert when wanted call is heard anywhere",
                Checked        = ctrl.wantedCallAnywhereEnabled,
                Location       = new System.Drawing.Point(left, y),
                AutoSize       = true,
                TabIndex       = 1,
                Font           = font,
                AccessibleName = "Alert when wanted call is heard anywhere",
                AccessibleDescription = "When checked, plays the Wanted Call Heard Anywhere sound whenever a callsign from your Wanted Calls list appears in any decode, even if they are working someone else or not eligible for your queue.",
            };
            wantedCallsPanel.Controls.Add(_wantedCallAnywhereCheckBox);
        }

        private void SaveWantedCallsTab()
        {
            if (wantedCallsTextBox == null) return;
            var raw = wantedCallsTextBox.Text ?? string.Empty;
            // Parse: accept newlines, commas, and spaces as separators
            var tokens = raw.Split(new char[] { '\r', '\n', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tok in tokens)
            {
                string call = tok.Trim().ToUpperInvariant();
                if (!string.IsNullOrEmpty(call))
                    normalized.Add(call);
            }
            ctrl.ApplyAndSaveWantedCalls(normalized);
            ctrl.wantedCallAnywhereEnabled = _wantedCallAnywhereCheckBox?.Checked ?? false;
        }

        // ===== SPOT WATCH TAB =====

        private void BuildSpotWatchTab()
        {
            spotWatchPanel.Controls.Clear();

            var font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F);
            int y = 8;
            const int left = 8;
            const int w = 640;

            // Instruction label
            var instrBox = new System.Windows.Forms.TextBox
            {
                ReadOnly       = true,
                Multiline      = true,
                BorderStyle    = System.Windows.Forms.BorderStyle.None,
                BackColor      = spotWatchPanel.BackColor,
                ForeColor      = System.Drawing.SystemColors.ControlText,
                Location       = new System.Drawing.Point(left, y),
                Size           = new System.Drawing.Size(w, 60),
                Text           = "Enter callsigns to watch for \"last spotted\" reports via PSKReporter (one per line, or comma/space separated).\r\n" +
                                 "Examples: K2A, W1AW/13\r\n" +
                                 "Separate from Wanted Calls -- adding a call here has no effect on call-queue priority.",
                TabStop        = false,
                AccessibleName = "Spot Watch instructions",
                Font           = font,
            };
            spotWatchPanel.Controls.Add(instrBox);
            y += 68;

            // Edit box label
            var editLabel = new System.Windows.Forms.Label
            {
                Text           = "Watched callsigns:",
                AccessibleName = "Watched callsigns label",
                AutoSize       = true,
                Location       = new System.Drawing.Point(left, y),
                Font           = font,
                TabStop        = false,
            };
            spotWatchPanel.Controls.Add(editLabel);
            y += 20;

            // Multiline text box
            spotWatchCallsTextBox = new System.Windows.Forms.TextBox
            {
                Multiline      = true,
                ScrollBars     = System.Windows.Forms.ScrollBars.Vertical,
                Location       = new System.Drawing.Point(left, y),
                Size           = new System.Drawing.Size(w, 220),
                TabIndex       = 0,
                AccessibleName = "Watched callsigns",
                AccessibleDescription = "Enter callsigns to watch for last-spotted reports. One per line, or comma or space separated. Case-insensitive.",
                Font           = font,
            };
            // Populate from current spot watch list (sorted for readability)
            var sorted = new List<string>(wsjtxClient.spotWatchCalls);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            spotWatchCallsTextBox.Text = string.Join(Environment.NewLine, sorted);
            spotWatchPanel.Controls.Add(spotWatchCallsTextBox);
            y += 228;

            // Checkbox: show the Spot Watch list in the main window
            _showSpotWatchCheckBox = new System.Windows.Forms.CheckBox
            {
                Text           = "Show Spot Watch list in main window",
                Checked        = ctrl.showSpotWatch,
                Location       = new System.Drawing.Point(left, y),
                AutoSize       = true,
                TabIndex       = 1,
                Font           = font,
                AccessibleName = "Show Spot Watch list in main window",
                AccessibleDescription = "When checked, adds a Spot Watch list to the main window showing last-spotted info for each watched callsign.",
            };
            spotWatchPanel.Controls.Add(_showSpotWatchCheckBox);
            y += 24;

            // Spot Watch display now requires Advanced Call Layout to be enabled.
            _advUiDependentControls?.Add(_showSpotWatchCheckBox);
            UpdateAdvUiDependentEnabled();

            // Sort order for the Spot Watch list -- separate from row field order
            // (Alt+I), which only controls which fields appear, not the row order.
            var sortLabel = new System.Windows.Forms.Label
            {
                Text           = "Sort by:",
                AccessibleName = "Spot Watch sort order label",
                AutoSize       = true,
                Location       = new System.Drawing.Point(left, y + 3),
                Font           = font,
                TabStop        = false,
            };
            spotWatchPanel.Controls.Add(sortLabel);

            _spotWatchSortCb = new System.Windows.Forms.ComboBox
            {
                DropDownStyle  = System.Windows.Forms.ComboBoxStyle.DropDownList,
                Location       = new System.Drawing.Point(left + 60, y),
                Size           = new System.Drawing.Size(160, 21),
                TabIndex       = 2,
                Font           = font,
                AccessibleName = "Spot Watch sort order",
                AccessibleDescription = "Choose how the Spot Watch list is ordered: by callsign, by even/odd transmit period, or by signal report.",
            };
            _spotWatchSortCb.Items.AddRange(new object[] { "Callsign (A-Z)", "Even/Odd Period", "SNR (Strongest First)" });
            int sortIdx = (ctrl.spotWatchSortKey ?? "callsign").ToLowerInvariant() == "evenodd" ? 1
                : (ctrl.spotWatchSortKey ?? "callsign").ToLowerInvariant() == "snr" ? 2 : 0;
            _spotWatchSortCb.SelectedIndex = sortIdx;
            spotWatchPanel.Controls.Add(_spotWatchSortCb);
        }

        private void SaveSpotWatchTab()
        {
            if (spotWatchCallsTextBox == null) return;
            var raw = spotWatchCallsTextBox.Text ?? string.Empty;
            // Parse: accept newlines, commas, and spaces as separators
            var tokens = raw.Split(new char[] { '\r', '\n', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tok in tokens)
            {
                string call = tok.Trim().ToUpperInvariant();
                if (!string.IsNullOrEmpty(call))
                    normalized.Add(call);
            }
            ctrl.ApplyAndSaveSpotWatchCalls(normalized);
            ctrl.showSpotWatch = _showSpotWatchCheckBox?.Checked ?? false;

            string[] sortKeys = { "callsign", "evenodd", "snr" };
            int idx = _spotWatchSortCb?.SelectedIndex ?? 0;
            ctrl.spotWatchSortKey = sortKeys[idx >= 0 && idx < sortKeys.Length ? idx : 0];
            ctrl.RefreshSpotWatchDisplay();
        }

        // ===== RADIO TAB (self-sufficiency plan, Phase 1) =====

        private System.Windows.Forms.RadioButton _radioWsjtxCatRb;
        private System.Windows.Forms.RadioButton _radioHamlibRb;
        private System.Windows.Forms.ComboBox _radioRigModelCombo;
        private System.Windows.Forms.ComboBox _radioComPortTextBox;
        private System.Windows.Forms.ComboBox _radioBaudRateTextBox;
        private System.Windows.Forms.ComboBox _radioPttMethodCombo;
        private System.Windows.Forms.CheckBox _radioUseExternalCheckBox;
        private System.Windows.Forms.TextBox _radioHostTextBox;
        private System.Windows.Forms.TextBox _radioPortTextBox;
        private System.Windows.Forms.CheckBox _radioPttEnabledCheckBox;
        private System.Windows.Forms.Button _radioTestButton;
        private AnnouncingLabel _radioTestResultLabel;

        private System.Windows.Forms.TextBox _engineMyCallTextBox;
        private System.Windows.Forms.TextBox _engineMyGridTextBox;
        private System.Windows.Forms.ComboBox _engineAudioDeviceCombo;
        private System.Windows.Forms.ComboBox _engineAudioOutputDeviceCombo;
        private System.Windows.Forms.NumericUpDown _engineAudioInputLevelUpDown;
        private System.Windows.Forms.NumericUpDown _engineAudioOutputLevelUpDown;
        private System.Windows.Forms.CheckBox _radioPttDataSourceCheckBox;
        private System.Windows.Forms.ComboBox _radioPttSerialPortCombo;
        private System.Windows.Forms.GroupBox _radioModeGroupBox;
        private System.Windows.Forms.RadioButton _radioModeNoneRb;
        private System.Windows.Forms.RadioButton _radioModeUsbRb;
        private System.Windows.Forms.RadioButton _radioModeDataPktRb;
        private System.Windows.Forms.GroupBox _radioSplitGroupBox;
        private System.Windows.Forms.RadioButton _radioSplitNoneRb;
        private System.Windows.Forms.RadioButton _radioSplitRigRb;
        private System.Windows.Forms.RadioButton _radioSplitFakeItRb;
        private System.Windows.Forms.NumericUpDown _radioPollIntervalUpDown;
        private System.Windows.Forms.CheckBox _radioReadDisplayPwrSwrCheckBox;
        private System.Windows.Forms.CheckBox _radioHaltTxOnHighSwrCheckBox;
        private System.Windows.Forms.NumericUpDown _radioSwrHaltThresholdUpDown;
        private System.Windows.Forms.CheckBox _radioStartupPowerEnabledCheckBox;
        private System.Windows.Forms.NumericUpDown _radioStartupPowerWattsUpDown;
        private System.Windows.Forms.NumericUpDown _radioStartupPowerMaxWattsUpDown;
        private System.Windows.Forms.NumericUpDown _radioAudioStepUpDown;
        private System.Windows.Forms.CheckBox _radioRememberTxLevelPerBandCheckBox;

        private System.Windows.Forms.ComboBox _decodeDepthCombo;
        private System.Windows.Forms.NumericUpDown _decodeFLowUpDown;
        private System.Windows.Forms.NumericUpDown _decodeFHighUpDown;
        private System.Windows.Forms.CheckBox _decodeApDecodeCheckBox;
        private System.Windows.Forms.CheckBox _decodeApCqOnlyCheckBox;
        private System.Windows.Forms.CheckBox _decodeSingleDecodeCheckBox;

        // Working copy, cloned from ctrl.Frequencies.Bands when the dialog opens, mutated freely
        // by Add/Remove/edit within this session, only committed back in SaveFrequenciesTab (OK
        // button) -- Cancel just discards this instance, same pending-until-OK shape as the
        // Hotkeys panel's own _pendingKeys.
        private List<FrequencyEntry>[] _pendingFreqBands;
        private System.Windows.Forms.ListBox _freqListBox;
        private List<FrequencyEntry> _freqListBoxEntries;      // parallel to _freqListBox.Items
        private List<int> _freqListBoxBandIdx;                 // parallel to _freqListBox.Items -- which band each row belongs to
        private System.Windows.Forms.NumericUpDown _freqValueUpDown;
        private HotkeyCaptureBox _freqHotkeyCaptureBox;
        private System.Windows.Forms.Button _freqClearHotkeyButton;
        private System.Windows.Forms.Button _freqAddButton;
        private System.Windows.Forms.Button _freqRemoveButton;
        // Re-entrancy guard: true while code (not the operator) is setting field values to
        // reflect a newly-selected list entry -- keeps those programmatic updates from being
        // mistaken for edits and written back into the entry that's only just been read FROM.
        private bool _freqUpdatingFields;

        // ===== NOTIFICATIONS TAB fields =====
        // Working copy, cloned from ctrl.Notifications.Policies when the dialog opens -- same
        // pending-until-OK shape as every other tab here (Hotkeys' _pendingKeys, Frequencies'
        // _pendingFreqBands). The template STRING is the only thing persisted per type; the
        // variable checklist and its order are always re-derived FROM that string (via
        // NotificationTemplateEngine.ParseComponents), never stored separately -- see
        // RefreshNotifyVarsList's own comment.
        private Dictionary<NotificationEventType, NotificationPolicy> _pendingNotifyPolicies;
        private List<NotificationEventType> _notifyTypeOrder;
        private System.Windows.Forms.CheckedListBox _notifyTypesListBox;
        private System.Windows.Forms.CheckedListBox _notifyVarsListBox;
        private List<NotificationVariable> _notifyVarsListEntries;   // parallel to _notifyVarsListBox.Items
        private System.Windows.Forms.Button _notifyVarMoveUpButton;
        private System.Windows.Forms.Button _notifyVarMoveDownButton;
        private System.Windows.Forms.TextBox _notifyTemplateTextBox;
        private System.Windows.Forms.RadioButton _notifyTimingImmediateRadio;
        private System.Windows.Forms.RadioButton _notifyTimingDeferredRadio;
        private System.Windows.Forms.CheckBox _notifyDeferWhileTxCheckBox;
        private System.Windows.Forms.NumericUpDown _notifyRepeatSecondsUpDown;
        private System.Windows.Forms.NumericUpDown _notifyThrottleMsUpDown;
        private System.Windows.Forms.CheckBox _notifySuppressUnchangedCheckBox;
        private System.Windows.Forms.ComboBox _notifyPriorityComboBox;
        private AnnouncingLabel _notifyValidationLabel;
        // Re-entrancy guard, same role as _freqUpdatingFields above: true while code is
        // populating fields from a newly-selected type/re-synced template, so those
        // programmatic changes never get mistaken for operator edits.
        private bool _notifyUpdatingFields;

        private void BuildRadioTab()
        {
            radioPanel.Controls.Clear();

            var font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F);
            int y = 8;
            const int left = 8;
            const int w = 640;

            var instrBox = new System.Windows.Forms.TextBox
            {
                ReadOnly       = true,
                Multiline      = true,
                BorderStyle    = System.Windows.Forms.BorderStyle.None,
                BackColor      = radioPanel.BackColor,
                ForeColor      = System.Drawing.SystemColors.ControlText,
                Location       = new System.Drawing.Point(left, y),
                Size           = new System.Drawing.Size(w, 48),
                Text           = "Choose where signal-meter, power, and SWR readings come from. Receive Only reports " +
                                 "whatever the native engine itself broadcasts, no separate radio connection. Hamlib " +
                                 "rigctld adds a real S-meter and connects to the radio directly.",
                TabStop        = false,
                AccessibleName = "Radio tab instructions",
                Font           = font,
            };
            radioPanel.Controls.Add(instrBox);
            y += 56;

            _radioWsjtxCatRb = new System.Windows.Forms.RadioButton
            {
                Text = "Receive Only (no separate CAT connection)",
                Checked = ctrl.Radio.Mode == RadioControlMode.WsjtxCat,
                Location = new System.Drawing.Point(left, y),
                AutoSize = true,
                TabIndex = 0,
                Font = font,
                AccessibleName = "Receive Only",
                AccessibleDescription = "Radio state (frequency, mode, transmitting) comes read-only from the native engine's own status reports. No S-meter/power/SWR, no PTT.",
            };
            radioPanel.Controls.Add(_radioWsjtxCatRb);
            y += 24;

            _radioHamlibRb = new System.Windows.Forms.RadioButton
            {
                Text = "Use Hamlib rigctld (S-meter, power, SWR; optional PTT)",
                Checked = ctrl.Radio.Mode == RadioControlMode.HamlibRigctld,
                Location = new System.Drawing.Point(left, y),
                AutoSize = true,
                TabIndex = 1,
                Font = font,
                AccessibleName = "Use Hamlib rigctld",
                AccessibleDescription = "Jimmy launches its own bundled rigctld against the rig model and COM port below (or connects to an external rigctld if configured) and drives CAT/PTT directly.",
            };
            radioPanel.Controls.Add(_radioHamlibRb);
            y += 32;

            var rigModelLabel = new System.Windows.Forms.Label
            {
                Text = "Rig model:",
                AccessibleName = "Rig model label",
                AutoSize = true,
                Location = new System.Drawing.Point(left, y + 3),
                Font = font,
                TabStop = false,
            };
            radioPanel.Controls.Add(rigModelLabel);
            y += 20;

            _radioRigModelCombo = new System.Windows.Forms.ComboBox
            {
                DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList,
                Location = new System.Drawing.Point(left, y),
                Size = new System.Drawing.Size(320, 21),
                TabIndex = 2,
                Font = font,
                AccessibleName = "Rig model",
                AccessibleDescription = "The radio model Jimmy's bundled rigctld talks to. Type the first letters of a manufacturer or model to jump to it, e.g. \"Kenwood\".",
            };
            // Live list from the bundled rigctl.exe itself (Resources\hamlib\rigctl.exe --list)
            // -- always matches whichever Hamlib version actually ships with Jimmy, never a
            // separately maintained list that could drift out of sync with it. Sorted by
            // manufacturer then model so typing the first few letters of either jumps close to
            // the right entry (a plain DropDownList's own built-in incremental-search behavior).
            var rigModels = RigctldClient.ListRigModels();
            rigModels.Sort((a, b) =>
            {
                int c = string.Compare(a.Mfg, b.Mfg, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare(a.Model, b.Model, StringComparison.OrdinalIgnoreCase);
            });
            bool foundCurrent = false;
            foreach (var m in rigModels)
            {
                _radioRigModelCombo.Items.Add(m.Display);
                if (m.Id.ToString() == (ctrl.Radio.RigModel ?? "").Trim()) foundCurrent = true;
            }
            // Never lose an already-configured value just because this Hamlib build's --list
            // didn't include it (or --list itself failed to run) -- keep it selectable and
            // selected rather than silently switching the operator to a different rig.
            string curRigModel = (ctrl.Radio.RigModel ?? "").Trim();
            if (!foundCurrent && curRigModel.Length > 0)
                _radioRigModelCombo.Items.Insert(0, $"(currently configured: {curRigModel})");
            if (curRigModel.Length > 0)
            {
                foreach (var item in _radioRigModelCombo.Items)
                {
                    string s = (string)item;
                    if (s.Contains($"({curRigModel})") || s == $"(currently configured: {curRigModel})")
                    {
                        _radioRigModelCombo.SelectedItem = s;
                        break;
                    }
                }
            }
            radioPanel.Controls.Add(_radioRigModelCombo);
            y += 28;

            var comPortLabel = new System.Windows.Forms.Label
            {
                Text = "COM port:",
                AccessibleName = "COM port label",
                AutoSize = true,
                Location = new System.Drawing.Point(left, y + 3),
                Font = font,
                TabStop = false,
            };
            radioPanel.Controls.Add(comPortLabel);
            y += 20;

            // DropDownList (a closed list, not free-text DropDown) -- reported live, 2026-08-06,
            // as the more reliable style with JAWS; it also makes the "typed 4 instead of COM4"
            // class of mistake structurally impossible (rigctld launched with -r 4 -- not a real
            // Windows device path -- silently could never talk to the radio, and every symptom
            // that followed, from a hung Test-connection to a stalled S-meter poll, traced back
            // to that one bad string). Real detected ports come from
            // System.IO.Ports.SerialPort.GetPortNames(); the currently-saved value is always
            // included even if not currently detected (radio powered off/unplugged at the
            // moment Options happens to be open must never silently hide or lose the setting).
            _radioComPortTextBox = new System.Windows.Forms.ComboBox
            {
                DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList,
                Location = new System.Drawing.Point(left, y),
                Size = new System.Drawing.Size(100, 21),
                TabIndex = 3,
                Font = font,
                AccessibleName = "COM port",
                AccessibleDescription = "Serial port the radio is connected to. Only used when launching Jimmy's own bundled rigctld. Lists ports currently detected on this PC.",
            };
            _radioComPortTextBox.Items.Add("");   // blank = not configured
            var detectedPorts = new System.Collections.Generic.List<string>(System.IO.Ports.SerialPort.GetPortNames());
            detectedPorts.Sort((a, b) =>
            {
                int an, bn;
                bool aNum = int.TryParse(new string(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(a, char.IsDigit))), out an);
                bool bNum = int.TryParse(new string(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(b, char.IsDigit))), out bn);
                return aNum && bNum ? an.CompareTo(bn) : string.CompareOrdinal(a, b);
            });
            foreach (var p in detectedPorts)
                _radioComPortTextBox.Items.Add(p);
            // Add the saved value verbatim (no suffix/decoration -- DropDownList's .Text IS the
            // selected item's exact text, so decorating it here would corrupt the actual saved
            // setting) if it's not already in the detected list, so a radio that's simply
            // powered off/unplugged right now never silently loses its configured port.
            if (!string.IsNullOrWhiteSpace(ctrl.Radio.ComPort) && !detectedPorts.Contains(ctrl.Radio.ComPort))
                _radioComPortTextBox.Items.Add(ctrl.Radio.ComPort);
            _radioComPortTextBox.Text = ctrl.Radio.ComPort;
            radioPanel.Controls.Add(_radioComPortTextBox);
            y += 32;

            var baudRateLabel = new System.Windows.Forms.Label
            {
                Text = "Baud rate (blank = Hamlib's default for this rig):",
                AccessibleName = "Baud rate label",
                AutoSize = true,
                Location = new System.Drawing.Point(left, y + 3),
                Font = font,
                TabStop = false,
            };
            radioPanel.Controls.Add(baudRateLabel);
            y += 20;

            // DropDownList (closed list), not free-text DropDown -- reported live, 2026-08-06, as
            // the more reliable style with JAWS. If the saved rate isn't one of the standard
            // ones it's still added to the list below (so an unusual real value is never
            // silently discarded), just not typeable ad hoc anymore.
            _radioBaudRateTextBox = new System.Windows.Forms.ComboBox
            {
                DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList,
                Location = new System.Drawing.Point(left, y),
                Size = new System.Drawing.Size(100, 21),
                TabIndex = 4,
                Font = font,
                AccessibleName = "Baud rate",
                AccessibleDescription = "Serial baud rate for CAT control -- must match the rig's own CAT baud rate menu setting, or rigctld cannot talk to it at all. Leave blank to use Hamlib's built-in default for this rig model.",
            };
            // Standard RS-232 rates, same set real WSJT-X's own Radio tab offers.
            _radioBaudRateTextBox.Items.Add("");   // blank = Hamlib's own default
            var standardBauds = new[] { "1200", "4800", "9600", "19200", "38400", "57600", "115200" };
            foreach (var rate in standardBauds)
                _radioBaudRateTextBox.Items.Add(rate);
            if (!string.IsNullOrWhiteSpace(ctrl.Radio.BaudRate) && System.Array.IndexOf(standardBauds, ctrl.Radio.BaudRate) < 0)
                _radioBaudRateTextBox.Items.Add(ctrl.Radio.BaudRate);
            _radioBaudRateTextBox.Text = ctrl.Radio.BaudRate;
            radioPanel.Controls.Add(_radioBaudRateTextBox);
            y += 32;

            _radioUseExternalCheckBox = new System.Windows.Forms.CheckBox
            {
                Text = "Use an external rigctld instead of Jimmy's own bundled copy",
                Checked = ctrl.Radio.UseExternalRigctld,
                Location = new System.Drawing.Point(left, y),
                AutoSize = true,
                TabIndex = 5,
                Font = font,
                AccessibleName = "Use external rigctld",
                AccessibleDescription = "When checked, Jimmy connects to a rigctld you are already running elsewhere, using the host and port below, instead of launching its own bundled copy.",
            };
            _radioUseExternalCheckBox.CheckedChanged += (s, e) => UpdateRadioHostPortEnabled();
            radioPanel.Controls.Add(_radioUseExternalCheckBox);
            y += 28;

            var hostLabel = new System.Windows.Forms.Label
            {
                Text = "Host:",
                AccessibleName = "rigctld host label",
                AutoSize = true,
                Location = new System.Drawing.Point(left, y + 3),
                Font = font,
                TabStop = false,
            };
            radioPanel.Controls.Add(hostLabel);

            _radioHostTextBox = new System.Windows.Forms.TextBox
            {
                Text = ctrl.Radio.RigctldHost,
                Location = new System.Drawing.Point(left + 45, y),
                Size = new System.Drawing.Size(120, 21),
                TabIndex = 6,
                Font = font,
                AccessibleName = "rigctld host",
                AccessibleDescription = "Hostname or IP address of the external rigctld instance.",
            };
            radioPanel.Controls.Add(_radioHostTextBox);

            var portLabel = new System.Windows.Forms.Label
            {
                Text = "Port:",
                AccessibleName = "rigctld port label",
                AutoSize = true,
                Location = new System.Drawing.Point(left + 175, y + 3),
                Font = font,
                TabStop = false,
            };
            radioPanel.Controls.Add(portLabel);

            _radioPortTextBox = new System.Windows.Forms.TextBox
            {
                Text = ctrl.Radio.RigctldPort.ToString(),
                Location = new System.Drawing.Point(left + 215, y),
                Size = new System.Drawing.Size(60, 21),
                TabIndex = 7,
                Font = font,
                AccessibleName = "rigctld port",
                AccessibleDescription = "TCP port of the external rigctld instance. Hamlib's default is 4532.",
            };
            radioPanel.Controls.Add(_radioPortTextBox);
            y += 32;

            _radioPttEnabledCheckBox = new System.Windows.Forms.CheckBox
            {
                Text = "Use rigctld for PTT (instead of WSJT-X's own CAT-driven PTT)",
                Checked = ctrl.Radio.PttEnabled,
                Location = new System.Drawing.Point(left, y),
                AutoSize = true,
                TabIndex = 8,
                Font = font,
                AccessibleName = "Use rigctld for PTT",
                AccessibleDescription = "Off by default. A bigger change than read-only telemetry -- only turn this on if you want Jimmy, not WSJT-X, keying the radio.",
            };
            radioPanel.Controls.Add(_radioPttEnabledCheckBox);

            // Placed on the SAME row as the checkbox above (not a new row) -- the Radio tab
            // already hit a real Tab-unreachable overflow bug once today from adding rows one at
            // a time without re-checking the total against the tab's visible height; the fix
            // that time was splitting Decode Engine into its own tab, which isn't warranted for
            // one more field here, so this stays deliberately width-wise instead of tall-wise.
            var pttMethodLabel = new System.Windows.Forms.Label
            {
                Text = "PTT method:",
                AccessibleName = "PTT method label",
                AutoSize = true,
                Location = new System.Drawing.Point(left + 400, y + 3),
                Font = font,
                TabStop = false,
            };
            radioPanel.Controls.Add(pttMethodLabel);

            _radioPttMethodCombo = new System.Windows.Forms.ComboBox
            {
                DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList,
                Location = new System.Drawing.Point(left + 480, y),
                Size = new System.Drawing.Size(110, 21),
                TabIndex = 9,
                Font = font,
                AccessibleName = "PTT method",
                AccessibleDescription = "How PTT is actually keyed once 'Use rigctld for PTT' above is checked: Cat (rigctld's own CAT command -- the common case), Vox (the radio keys itself off transmit audio -- Jimmy sends no PTT command at all), Serial RTS or Serial DTR (a serial control line on the same port as CAT). Ignored, forced to Vox, when 'Use rigctld for PTT' is unchecked.",
            };
            foreach (PttMethod m in Enum.GetValues(typeof(PttMethod)))
                _radioPttMethodCombo.Items.Add(m.ToString());
            _radioPttMethodCombo.SelectedItem = ctrl.Radio.PttMethod.ToString();
            radioPanel.Controls.Add(_radioPttMethodCombo);
            y += 32;

            // WSJT-X Radio tab "PTT Method" > "Port" -- a separate serial port for RTS/DTR PTT
            // when it differs from the CAT port (an SO2R controller). Nexus's engine already
            // supported this (Settings.ptt_serial_port); it was just never exposed here before.
            var pttSerialPortLabel = new System.Windows.Forms.Label
            {
                Text = "PTT port (blank = same as CAT):",
                AccessibleName = "PTT port label",
                AutoSize = true,
                Location = new System.Drawing.Point(left, y + 3),
                Font = font,
                TabStop = false,
            };
            radioPanel.Controls.Add(pttSerialPortLabel);
            _radioPttSerialPortCombo = new System.Windows.Forms.ComboBox
            {
                DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList,
                Location = new System.Drawing.Point(left + 260, y),
                Size = new System.Drawing.Size(100, 21),
                TabIndex = 10,
                Font = font,
                AccessibleName = "PTT port",
                AccessibleDescription = "Separate serial port for RTS/DTR PTT keying when it differs from the CAT serial port above -- an SO2R controller routing keying on its own COM port. Blank (default) keys on the same port as CAT.",
            };
            _radioPttSerialPortCombo.Items.Add("");
            foreach (var p in detectedPorts)
                _radioPttSerialPortCombo.Items.Add(p);
            if (!string.IsNullOrWhiteSpace(ctrl.Radio.PttSerialPort) && !detectedPorts.Contains(ctrl.Radio.PttSerialPort))
                _radioPttSerialPortCombo.Items.Add(ctrl.Radio.PttSerialPort);
            _radioPttSerialPortCombo.Text = ctrl.Radio.PttSerialPort;
            radioPanel.Controls.Add(_radioPttSerialPortCombo);
            y += 32;

            // Same row/placement family as real WSJT-X's own Radio tab, which puts its "Mode:
            // None/USB/Data-Pkt" choice right above "Test CAT" -- confirmed live, 2026-08-07, via
            // the operator's own JAWS navigation transcript of WSJT-X 3.0.0 rc1 mod's Radio tab.
            // Originally placed on Jimmy's Decode Engine tab instead (next to the audio device
            // pickers), which the operator flagged as the wrong location: WSJT-X keeps rig-mode
            // choices with its other CAT/PTT controls, not with audio device selection, and
            // Jimmy should match that grouping for anyone already familiar with WSJT-X's layout.
            // Real radio buttons now (all three of WSJT-X's choices, including None), not a
            // checkbox -- the operator flagged the old on/off checkbox as confusing, 2026-08-11.
            _radioModeGroupBox = new System.Windows.Forms.GroupBox
            {
                Text = "Mode",
                Location = new System.Drawing.Point(left, y),
                Size = new System.Drawing.Size(380, 45),
                TabIndex = 16,
                Font = font,
                AccessibleName = "Mode",
            };
            _radioModeNoneRb = new System.Windows.Forms.RadioButton
            {
                Text = "None", Location = new System.Drawing.Point(10, 18), AutoSize = true,
                TabIndex = 0, Font = font, Checked = ctrl.Radio.TxMode == RadioTxMode.None,
                AccessibleDescription = "Never send the radio a mode command at all, for any operating mode -- the operator's own manual rig setting stands.",
            };
            _radioModeUsbRb = new System.Windows.Forms.RadioButton
            {
                Text = "USB", Location = new System.Drawing.Point(130, 18), AutoSize = true,
                TabIndex = 1, Font = font, Checked = ctrl.Radio.TxMode == RadioTxMode.Usb,
                AccessibleDescription = "Command plain USB instead of the Data/Pkt submode. Only try this if Data/Pkt mode isn't actually routing transmit audio to the radio correctly on your setup.",
            };
            _radioModeDataPktRb = new System.Windows.Forms.RadioButton
            {
                Text = "Data/Pkt", Location = new System.Drawing.Point(240, 18), AutoSize = true,
                TabIndex = 2, Font = font, Checked = ctrl.Radio.TxMode == RadioTxMode.DataPkt,
                AccessibleDescription = "Default and recommended for most rigs, including a normal rear-panel USB or ACC digital audio interface. Commands the rig's DATA submode over CAT for every FT8/FT4 transmission.",
            };
            _radioModeGroupBox.Controls.Add(_radioModeNoneRb);
            _radioModeGroupBox.Controls.Add(_radioModeUsbRb);
            _radioModeGroupBox.Controls.Add(_radioModeDataPktRb);
            radioPanel.Controls.Add(_radioModeGroupBox);
            y += 52;

            // Matches real WSJT-X's own Radio tab "Transmit Audio Source: Mic / Data" choice
            // (Configuration.cpp's TX_audio_source_button_group) -- a SEPARATE control from
            // Mode above: that one changes the CAT *mode* command (M USB vs M PKTUSB); this one
            // changes the PTT command itself (RIG_PTT_ON vs RIG_PTT_ON_DATA), telling a rig with
            // separate mic/data audio inputs which one to key from. Confirmed live, 2026-08-07: a
            // real TS-590SG transmitted mic audio instead of the FT8 tone even though CAT mode
            // already correctly showed the DATA submode -- this is the actual fix for that, not
            // the Mode group above.
            _radioPttDataSourceCheckBox = new System.Windows.Forms.CheckBox
            {
                Text = "Transmit Audio Source: Data (not Mic)",
                Checked = ctrl.Radio.PttDataSource,
                Location = new System.Drawing.Point(left, y),
                AutoSize = true,
                TabIndex = 17,
                Font = font,
                AccessibleName = "Transmit Audio Source Data not Mic",
                AccessibleDescription = "Off by default (Mic), matching WSJT-X's own default. Matches WSJT-X's own Radio tab 'Transmit Audio Source' choice: Mic (default here, off) vs Data (checked). Only try this if your interface is wired to the rig's rear DATA/ACC port and the rig still transmits mic audio instead of the FT8 tone during a real transmission.",
            };
            radioPanel.Controls.Add(_radioPttDataSourceCheckBox);
            y += 32;

            // Rig Data section, packed two-controls-per-row like PTT Method above it -- same
            // "don't grow the tab tall enough to go Tab-unreachable" discipline. Matches WSJT-X's
            // own Radio tab "Rig Data" grouping (Split Operation, Poll Interval, Read/display
            // PWR+SWR, Halt Tx on high SWR) per the operator's own request and JAWS navigation
            // transcript of WSJT-X 3.0.0 rc1 mod, 2026-08-07 -- kept on THIS tab rather than a
            // separate one because the operator explicitly said WSJT-X has it all on one Radio
            // tab and Jimmy should match that. Real radio buttons now (all three of WSJT-X's
            // choices, including Fake It -- Engine::split_reduce/apply_tx_dial_shift already
            // fully implement it), not a checkbox.
            _radioSplitGroupBox = new System.Windows.Forms.GroupBox
            {
                Text = "Split Operation",
                Location = new System.Drawing.Point(left, y),
                Size = new System.Drawing.Size(380, 45),
                TabIndex = 18,
                Font = font,
                AccessibleName = "Split Operation",
            };
            _radioSplitNoneRb = new System.Windows.Forms.RadioButton
            {
                Text = "None", Location = new System.Drawing.Point(10, 18), AutoSize = true,
                TabIndex = 0, Font = font, Checked = ctrl.Radio.SplitMode == RadioSplitMode.None,
                AccessibleDescription = "Receive and transmit on the same frequency -- how ordinary FT8/FT4 works.",
            };
            _radioSplitRigRb = new System.Windows.Forms.RadioButton
            {
                Text = "Rig", Location = new System.Drawing.Point(130, 18), AutoSize = true,
                TabIndex = 1, Font = font, Checked = ctrl.Radio.SplitMode == RadioSplitMode.Rig,
                AccessibleDescription = "True hardware split via CAT -- receive on VFO A, transmit on VFO B.",
            };
            _radioSplitFakeItRb = new System.Windows.Forms.RadioButton
            {
                Text = "Fake It", Location = new System.Drawing.Point(240, 18), AutoSize = true,
                TabIndex = 2, Font = font, Checked = ctrl.Radio.SplitMode == RadioSplitMode.FakeIt,
                AccessibleDescription = "Software-emulated split -- no true rig split needed. The engine retunes the single VFO right before each transmission and restores it after.",
            };
            _radioSplitGroupBox.Controls.Add(_radioSplitNoneRb);
            _radioSplitGroupBox.Controls.Add(_radioSplitRigRb);
            _radioSplitGroupBox.Controls.Add(_radioSplitFakeItRb);
            radioPanel.Controls.Add(_radioSplitGroupBox);
            y += 52;

            var pollIntervalLabel = new System.Windows.Forms.Label
            {
                Text = "Poll interval (s):",
                AccessibleName = "Poll interval label",
                AutoSize = true,
                Location = new System.Drawing.Point(left + 290, y + 3),
                Font = font,
                TabStop = false,
            };
            radioPanel.Controls.Add(pollIntervalLabel);

            _radioPollIntervalUpDown = new System.Windows.Forms.NumericUpDown
            {
                Minimum = 1,
                Maximum = 30,
                Value = Math.Max(1, Math.Min(30, ctrl.Radio.PollIntervalMs / 1000)),
                Location = new System.Drawing.Point(left + 410, y),
                Size = new System.Drawing.Size(55, 21),
                TabIndex = 19,
                Font = font,
                AccessibleName = "Poll interval seconds",
                AccessibleDescription = "How often Jimmy polls the radio for S-meter/power/SWR, in seconds, while 'Read and display PWR and SWR' below is checked.",
            };
            radioPanel.Controls.Add(_radioPollIntervalUpDown);
            y += 32;

            _radioReadDisplayPwrSwrCheckBox = new System.Windows.Forms.CheckBox
            {
                Text = "Read and display PWR and SWR",
                Checked = ctrl.Radio.ReadDisplayPwrSwr,
                Location = new System.Drawing.Point(left, y),
                AutoSize = true,
                TabIndex = 20,
                Font = font,
                AccessibleName = "Read and display PWR and SWR",
                AccessibleDescription = "Off by default. Turns on the periodic S-meter/power/SWR poll (Poll interval above) that Alt+Q's on-demand check also uses. Must be checked for 'Halt Tx when SWR' below to have anything to check.",
            };
            _radioReadDisplayPwrSwrCheckBox.CheckedChanged += (s, e) => UpdateSwrHaltEnabled();
            radioPanel.Controls.Add(_radioReadDisplayPwrSwrCheckBox);

            _radioHaltTxOnHighSwrCheckBox = new System.Windows.Forms.CheckBox
            {
                Text = "Halt Tx when SWR >",
                Checked = ctrl.Radio.HaltTxOnHighSwr,
                Location = new System.Drawing.Point(left + 290, y),
                AutoSize = true,
                TabIndex = 21,
                Font = font,
                AccessibleName = "Halt Tx when SWR exceeds threshold",
                AccessibleDescription = "Off by default. Matches WSJT-X's own Radio tab safety feature: automatically halts transmission if a poll (see 'Read and display PWR and SWR' above, which this requires) reports SWR above the threshold to the right.",
            };
            radioPanel.Controls.Add(_radioHaltTxOnHighSwrCheckBox);

            _radioSwrHaltThresholdUpDown = new System.Windows.Forms.NumericUpDown
            {
                Minimum = 1.0m,
                Maximum = 10.0m,
                DecimalPlaces = 1,
                Increment = 0.1m,
                Value = (decimal)Math.Max(1.0, Math.Min(10.0, ctrl.Radio.SwrHaltThreshold)),
                Location = new System.Drawing.Point(left + 470, y),
                Size = new System.Drawing.Size(55, 21),
                TabIndex = 22,
                Font = font,
                AccessibleName = "SWR halt threshold",
                AccessibleDescription = "SWR value above which Tx is automatically halted, when 'Halt Tx when SWR' to the left is checked. WSJT-X's own default is 2.5.",
            };
            radioPanel.Controls.Add(_radioSwrHaltThresholdUpDown);
            y += 32;

            // Startup-only power workaround for the Hamlib Kenwood-backend bug (RadioSettings.
            // StartupPowerEnabled's own doc comment has the full story) -- off by default, so
            // nothing changes for anyone who hasn't hit the bug. When checked, jimmy-engine-host
            // commands Watts/Max watts (as a fraction) to the rig exactly once at startup, then
            // leaves power alone -- changing it by hand on the rig afterward, including band
            // changes, sticks.
            _radioStartupPowerEnabledCheckBox = new System.Windows.Forms.CheckBox
            {
                Text = "Set power once at startup",
                Checked = ctrl.Radio.StartupPowerEnabled,
                Location = new System.Drawing.Point(left, y),
                AutoSize = true,
                TabIndex = 23,
                Font = font,
                AccessibleName = "Set power once at startup",
                AccessibleDescription = "Off by default. Works around a Hamlib bug on some rigs (Kenwood CAT) where the radio's power can drop unexpectedly the first time Jimmy starts. When checked, Jimmy commands the Watts value below to the rig exactly once at startup, then leaves power alone -- changing it by hand on the rig afterward, including when you change bands, sticks.",
            };
            _radioStartupPowerEnabledCheckBox.CheckedChanged += (s, e) => UpdateStartupPowerEnabled();
            radioPanel.Controls.Add(_radioStartupPowerEnabledCheckBox);

            var startupPowerWattsLabel = new System.Windows.Forms.Label
            {
                Text = "Watts:",
                AccessibleName = "Startup power watts label",
                AutoSize = true,
                Location = new System.Drawing.Point(left + 290, y + 3),
                Font = font,
                TabStop = false,
            };
            radioPanel.Controls.Add(startupPowerWattsLabel);

            _radioStartupPowerWattsUpDown = new System.Windows.Forms.NumericUpDown
            {
                Minimum = 1,
                Maximum = 1000,
                Value = Math.Max(1, Math.Min(1000, ctrl.Radio.StartupPowerWatts)),
                Location = new System.Drawing.Point(left + 340, y),
                Size = new System.Drawing.Size(55, 21),
                TabIndex = 24,
                Font = font,
                AccessibleName = "Startup power watts",
                AccessibleDescription = "Power to command at startup, in watts, when 'Set power once at startup' is checked.",
            };
            radioPanel.Controls.Add(_radioStartupPowerWattsUpDown);

            var startupPowerMaxWattsLabel = new System.Windows.Forms.Label
            {
                Text = "Rig max watts:",
                AccessibleName = "Rig maximum watts label",
                AutoSize = true,
                Location = new System.Drawing.Point(left + 410, y + 3),
                Font = font,
                TabStop = false,
            };
            radioPanel.Controls.Add(startupPowerMaxWattsLabel);

            _radioStartupPowerMaxWattsUpDown = new System.Windows.Forms.NumericUpDown
            {
                Minimum = 1,
                Maximum = 1000,
                Value = Math.Max(1, Math.Min(1000, ctrl.Radio.StartupPowerMaxWatts)),
                Location = new System.Drawing.Point(left + 510, y),
                Size = new System.Drawing.Size(55, 21),
                TabIndex = 25,
                Font = font,
                AccessibleName = "Rig maximum watts",
                AccessibleDescription = "Your rig's full-power rating in watts (for example, 100). Jimmy uses this together with the Watts value to compute the fraction it commands to the rig.",
            };
            radioPanel.Controls.Add(_radioStartupPowerMaxWattsUpDown);
            y += 32;

            var audioStepLabel = new System.Windows.Forms.Label
            {
                Text = "F11/F12 audio level step (%):",
                AccessibleName = "F11 F12 audio level step percent label",
                AutoSize = true,
                Location = new System.Drawing.Point(left, y + 3),
                Font = font,
                TabStop = false,
            };
            radioPanel.Controls.Add(audioStepLabel);

            _radioAudioStepUpDown = new System.Windows.Forms.NumericUpDown
            {
                Minimum = 1,
                Maximum = 25,
                Value = Math.Max(1, Math.Min(25, ctrl.Radio.AudioStepPercent)),
                Location = new System.Drawing.Point(left + 210, y),
                Size = new System.Drawing.Size(55, 21),
                TabIndex = 26,
                Font = font,
                AccessibleName = "F11 F12 audio level step percent",
                AccessibleDescription = "How much F11 and F12 change the transmit audio level per press, as a percentage. Default 5.",
            };
            radioPanel.Controls.Add(_radioAudioStepUpDown);
            y += 32;

            _radioRememberTxLevelPerBandCheckBox = new System.Windows.Forms.CheckBox
            {
                Text = "Remember F11/F12 audio level per band",
                Checked = ctrl.Radio.RememberTxLevelPerBand,
                AutoSize = true,
                Location = new System.Drawing.Point(left, y),
                Font = font,
                TabIndex = 27,
                AccessibleName = "Remember F11 F12 audio level per band",
                AccessibleDescription = "When checked, each F11/F12 adjustment is saved for the current band and restored automatically when you return to it. When unchecked, the level carries over as-is across bands, same as before.",
            };
            radioPanel.Controls.Add(_radioRememberTxLevelPerBandCheckBox);
            y += 32;

            _radioTestButton = new System.Windows.Forms.Button
            {
                Text = "Test connection",
                Location = new System.Drawing.Point(left, y),
                Size = new System.Drawing.Size(120, 24),
                TabIndex = 28,
                Font = font,
                AccessibleName = "Test radio connection",
                AccessibleDescription = "Launches (or connects to) rigctld with the settings above and reports whether it answered.",
            };
            _radioTestButton.Click += RadioTestButton_Click;
            radioPanel.Controls.Add(_radioTestButton);

            _radioTestResultLabel = new AnnouncingLabel
            {
                Text = "",
                AccessibleName = "Radio test result",
                AutoSize = true,
                Location = new System.Drawing.Point(left + 130, y + 4),
                Font = font,
                TabStop = false,
            };
            radioPanel.Controls.Add(_radioTestResultLabel);
            y += 40;

            UpdateRadioHostPortEnabled();
            UpdateSwrHaltEnabled();
            UpdateStartupPowerEnabled();
        }

        private void UpdateSwrHaltEnabled()
        {
            bool pollOn = _radioReadDisplayPwrSwrCheckBox?.Checked ?? false;
            if (_radioHaltTxOnHighSwrCheckBox != null) _radioHaltTxOnHighSwrCheckBox.Enabled = pollOn;
            if (_radioSwrHaltThresholdUpDown != null) _radioSwrHaltThresholdUpDown.Enabled = pollOn;
        }

        private void UpdateStartupPowerEnabled()
        {
            bool on = _radioStartupPowerEnabledCheckBox?.Checked ?? false;
            if (_radioStartupPowerWattsUpDown != null) _radioStartupPowerWattsUpDown.Enabled = on;
            if (_radioStartupPowerMaxWattsUpDown != null) _radioStartupPowerMaxWattsUpDown.Enabled = on;
        }

        // Split out from BuildRadioTab: this section had grown tall enough to render below the
        // Radio tab's visible area, and AutoScroll being set on that panel did not make it
        // reachable by Tab with a real screen reader -- confirmed live with JAWS/NVDA,
        // 2026-08-06 (tabbing from "Test radio connection" went straight to OK/Cancel, skipping
        // this whole section entirely). Its own tab has plenty of room and needs no scrolling.
        private void BuildDecodeEngineTab()
        {
            decodeEnginePanel.Controls.Clear();

            var font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F);
            int y = 8;
            const int left = 8;
            const int w = 640;

            var engineInstrBox = new System.Windows.Forms.TextBox
            {
                ReadOnly = true,
                Multiline = true,
                BorderStyle = System.Windows.Forms.BorderStyle.None,
                BackColor = decodeEnginePanel.BackColor,
                ForeColor = System.Drawing.SystemColors.ControlText,
                Location = new System.Drawing.Point(left, y),
                Size = new System.Drawing.Size(w, 48),
                Text = "Jimmy decodes FT8 audio itself -- no separate WSJT-X-family program needed. Replying " +
                       "WILL transmit for real -- with real PTT and real audio -- if Radio Mode (Radio tab) is " +
                       "set to Hamlib rigctld with PTT enabled; otherwise it stays receive-only with nowhere " +
                       "to key PTT.",
                TabStop = false,
                AccessibleName = "Decode Engine instructions",
                Font = font,
            };
            decodeEnginePanel.Controls.Add(engineInstrBox);
            y += 56;

            var myCallLabel = new System.Windows.Forms.Label
            {
                Text = "My Call:",
                AccessibleName = "My Call label",
                AutoSize = true,
                Location = new System.Drawing.Point(left, y + 3),
                Font = font,
                TabStop = false,
            };
            decodeEnginePanel.Controls.Add(myCallLabel);

            _engineMyCallTextBox = new System.Windows.Forms.TextBox
            {
                Text = ctrl.NativeEngine.MyCall,
                Location = new System.Drawing.Point(left + 65, y),
                Size = new System.Drawing.Size(100, 21),
                TabIndex = 2,
                Font = font,
                AccessibleName = "My Call",
                AccessibleDescription = "Your callsign. Required for Jimmy Native -- the engine needs it before it can report its own status.",
            };
            decodeEnginePanel.Controls.Add(_engineMyCallTextBox);

            var myGridLabel = new System.Windows.Forms.Label
            {
                Text = "My Grid:",
                AccessibleName = "My Grid label",
                AutoSize = true,
                Location = new System.Drawing.Point(left + 180, y + 3),
                Font = font,
                TabStop = false,
            };
            decodeEnginePanel.Controls.Add(myGridLabel);

            _engineMyGridTextBox = new System.Windows.Forms.TextBox
            {
                Text = ctrl.NativeEngine.MyGrid,
                Location = new System.Drawing.Point(left + 245, y),
                Size = new System.Drawing.Size(80, 21),
                TabIndex = 3,
                Font = font,
                AccessibleName = "My Grid",
                AccessibleDescription = "Your Maidenhead grid square. Required for Jimmy Native.",
            };
            decodeEnginePanel.Controls.Add(_engineMyGridTextBox);
            y += 32;

            var audioDeviceLabel = new System.Windows.Forms.Label
            {
                Text = "Audio input device:",
                AccessibleName = "Audio input device label",
                AutoSize = true,
                Location = new System.Drawing.Point(left, y + 3),
                Font = font,
                TabStop = false,
            };
            decodeEnginePanel.Controls.Add(audioDeviceLabel);
            y += 20;

            _engineAudioDeviceCombo = new System.Windows.Forms.ComboBox
            {
                DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDown,
                Location = new System.Drawing.Point(left, y),
                Size = new System.Drawing.Size(320, 21),
                TabIndex = 4,
                Font = font,
                AccessibleName = "Audio input device",
                AccessibleDescription = "Sound card input Jimmy Native captures from. Leave blank for the system default. Populated from the real devices this computer sees.",
            };
            _engineAudioDeviceCombo.Items.Add("");   // blank = system default
            bool engineSessionActive = ctrl.nativeEngineClient != null && ctrl.nativeEngineClient.Running;
            foreach (var dev in NativeEngineClient.ListAudioDevices(engineSessionActive))
                _engineAudioDeviceCombo.Items.Add(dev);
            _engineAudioDeviceCombo.Text = ctrl.NativeEngine.AudioInputDevice;
            decodeEnginePanel.Controls.Add(_engineAudioDeviceCombo);
            y += 24;

            var audioInputLevelLabel = new System.Windows.Forms.Label
            {
                Text = "Input level (%):",
                AccessibleName = "Audio input level label",
                AutoSize = true,
                Location = new System.Drawing.Point(left, y + 3),
                Font = font,
                TabStop = false,
            };
            decodeEnginePanel.Controls.Add(audioInputLevelLabel);

            // Windows' own per-application session volume for the engine's capture stream on
            // the device above -- separate from mic_gain (F11/F12), which scales the TX waveform
            // digitally before it ever reaches Windows. This is the SAME control the Windows
            // Volume Mixer exposes for jimmy-engine-host.exe (confirmed live, 2026-08-09, it
            // shows there under its own raw filename since it has no embedded Windows version
            // resource); reading/writing it here just saves hunting for that unfamiliar name.
            // Live -- applies immediately on change, not gated behind OK, since it's OS session
            // state, not a Jimmy setting Jimmy itself remembers/reapplies at next startup.
            _engineAudioInputLevelUpDown = new System.Windows.Forms.NumericUpDown
            {
                Minimum = 0,
                Maximum = 100,
                Location = new System.Drawing.Point(left + 110, y),
                Size = new System.Drawing.Size(55, 21),
                TabIndex = 5,
                Font = font,
                AccessibleName = "Audio input level percent",
                AccessibleDescription = "Windows' own output volume for the engine's capture stream on the device above -- the same control the Windows Volume Mixer has for jimmy-engine-host.exe, applied immediately. Only available while the native engine is running and has opened this device.",
                Enabled = false,
            };
            decodeEnginePanel.Controls.Add(_engineAudioInputLevelUpDown);
            InitAudioSessionLevelControl(_engineAudioInputLevelUpDown, isRender: false);
            y += 32;

            var audioOutputDeviceLabel = new System.Windows.Forms.Label
            {
                Text = "Audio output device (TX):",
                AccessibleName = "Audio output device label",
                AutoSize = true,
                Location = new System.Drawing.Point(left, y + 3),
                Font = font,
                TabStop = false,
            };
            decodeEnginePanel.Controls.Add(audioOutputDeviceLabel);
            y += 20;

            _engineAudioOutputDeviceCombo = new System.Windows.Forms.ComboBox
            {
                DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDown,
                Location = new System.Drawing.Point(left, y),
                Size = new System.Drawing.Size(320, 21),
                TabIndex = 6,
                Font = font,
                AccessibleName = "Audio output device",
                AccessibleDescription = "Sound card output Jimmy Native transmits to -- normally the radio's own audio interface, NOT your PC speakers. Leave blank for the system default. Populated from the real devices this computer sees.",
            };
            _engineAudioOutputDeviceCombo.Items.Add("");   // blank = system default
            foreach (var dev in NativeEngineClient.ListOutputAudioDevices(engineSessionActive))
                _engineAudioOutputDeviceCombo.Items.Add(dev);
            _engineAudioOutputDeviceCombo.Text = ctrl.NativeEngine.AudioOutputDevice;
            decodeEnginePanel.Controls.Add(_engineAudioOutputDeviceCombo);
            y += 24;

            var audioOutputLevelLabel = new System.Windows.Forms.Label
            {
                Text = "Output level (%):",
                AccessibleName = "Audio output level label",
                AutoSize = true,
                Location = new System.Drawing.Point(left, y + 3),
                Font = font,
                TabStop = false,
            };
            decodeEnginePanel.Controls.Add(audioOutputLevelLabel);

            // Same idea as the input level control above, for the engine's RENDER stream on the
            // device above -- this is the one that actually feeds the radio, and the slider you'd
            // find in the Windows Volume Mixer under jimmy-engine-host.exe's own "adjust output
            // volume." Live -- applies immediately on change.
            _engineAudioOutputLevelUpDown = new System.Windows.Forms.NumericUpDown
            {
                Minimum = 0,
                Maximum = 100,
                Location = new System.Drawing.Point(left + 110, y),
                Size = new System.Drawing.Size(55, 21),
                TabIndex = 7,
                Font = font,
                AccessibleName = "Audio output level percent",
                AccessibleDescription = "Windows' own output volume for the engine's transmit stream on the device above -- the same control the Windows Volume Mixer has for jimmy-engine-host.exe, applied immediately. This is the level that actually reaches the radio. Only available while the native engine is running and has opened this device.",
                Enabled = false,
            };
            decodeEnginePanel.Controls.Add(_engineAudioOutputLevelUpDown);
            InitAudioSessionLevelControl(_engineAudioOutputLevelUpDown, isRender: true);
            y += 32;
        }

        // Reads the engine's current OS-level session volume for the given direction (input
        // device combo's saved device for isRender:false, output device combo's for
        // isRender:true) and shows it on upDown, enabling it only if a live session was actually
        // found. Wires ValueChanged to apply changes immediately (ProcessAudioSessionVolume.cs) --
        // this is real Windows session state, not a Jimmy setting saved to the ini, so there's
        // nothing to persist and no reason to wait for OK.
        private void InitAudioSessionLevelControl(System.Windows.Forms.NumericUpDown upDown, bool isRender)
        {
            int pid = ctrl.nativeEngineClient?.ProcessId ?? 0;
            string deviceName = isRender ? ctrl.NativeEngine.AudioOutputDevice : ctrl.NativeEngine.AudioInputDevice;
            float? current = pid > 0 ? ProcessAudioSessionVolume.GetVolume(pid, deviceName, isRender) : null;

            if (current.HasValue)
            {
                upDown.Value = (decimal)Math.Max(0, Math.Min(100, current.Value * 100));
                upDown.Enabled = true;
            }
            else
            {
                upDown.Value = 100;
                upDown.Enabled = false;
            }

            upDown.ValueChanged += (s, e) =>
            {
                int livePid = ctrl.nativeEngineClient?.ProcessId ?? 0;
                if (livePid <= 0) return;
                string liveDeviceName = isRender ? ctrl.NativeEngine.AudioOutputDevice : ctrl.NativeEngine.AudioInputDevice;
                ProcessAudioSessionVolume.SetVolume(livePid, liveDeviceName, isRender, (float)(upDown.Value / 100m));
            };
        }

        // WSJT-X's own decode-related settings, ported over in Nexus but never previously
        // exposed to Jimmy -- see DecodeSettings.cs's own comment for the full list and which
        // ones are live vs. startup-only. Only takes effect for Jimmy Native (Decode Engine tab);
        // meaningless under WSJT-X External, but shown unconditionally like the Radio tab is --
        // no real harm in the operator seeing/setting it ahead of switching decode engines.
        private void BuildDecodeTab()
        {
            decodePanel.Controls.Clear();

            var font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F);
            int y = 8;
            const int left = 8;
            const int w = 640;

            var instrBox = new System.Windows.Forms.TextBox
            {
                ReadOnly = true,
                Multiline = true,
                BorderStyle = System.Windows.Forms.BorderStyle.None,
                BackColor = decodePanel.BackColor,
                ForeColor = System.Drawing.SystemColors.ControlText,
                Location = new System.Drawing.Point(left, y),
                Size = new System.Drawing.Size(w, 48),
                Text = "WSJT-X's own decode settings, for Jimmy Native (Decode Engine tab). Decode depth " +
                       "takes effect immediately; the rest take effect the next time the engine restarts " +
                       "(changing them here, or changing decode engine/radio settings, restarts it).",
                TabStop = false,
                AccessibleName = "Decode instructions",
                Font = font,
            };
            decodePanel.Controls.Add(instrBox);
            y += 56;

            var depthLabel = new System.Windows.Forms.Label
            {
                Text = "Decode depth:",
                AccessibleName = "Decode depth label",
                AutoSize = true,
                Location = new System.Drawing.Point(left, y + 3),
                Font = font,
                TabStop = false,
            };
            decodePanel.Controls.Add(depthLabel);

            _decodeDepthCombo = new System.Windows.Forms.ComboBox
            {
                DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList,
                Location = new System.Drawing.Point(left + 100, y),
                Size = new System.Drawing.Size(100, 21),
                TabIndex = 0,
                Font = font,
                AccessibleName = "Decode depth",
                AccessibleDescription = "WSJT-X's Fast/Normal/Deep. Deep catches weaker signals but uses more CPU; Fast trades sensitivity for speed. Takes effect immediately, no engine restart needed.",
            };
            _decodeDepthCombo.Items.AddRange(new object[] { "Fast", "Normal", "Deep" });
            _decodeDepthCombo.SelectedIndex = Math.Max(0, Math.Min(2, ctrl.Decode.DecodeDepth - 1));
            decodePanel.Controls.Add(_decodeDepthCombo);
            y += 32;

            var flowLabel = new System.Windows.Forms.Label
            {
                Text = "F Low (Hz):",
                AccessibleName = "F Low label",
                AutoSize = true,
                Location = new System.Drawing.Point(left, y + 3),
                Font = font,
                TabStop = false,
            };
            decodePanel.Controls.Add(flowLabel);

            _decodeFLowUpDown = new System.Windows.Forms.NumericUpDown
            {
                Minimum = 200,
                Maximum = 3900,
                Value = Math.Max(200, Math.Min(3900, ctrl.Decode.DecodeFLowHz)),
                Location = new System.Drawing.Point(left + 100, y),
                Size = new System.Drawing.Size(70, 21),
                TabIndex = 1,
                Font = font,
                AccessibleName = "Decode F Low Hz",
                AccessibleDescription = "Decoder passband low edge in Hz -- signals below this are not searched. WSJT-X default 200.",
            };
            decodePanel.Controls.Add(_decodeFLowUpDown);

            var fhighLabel = new System.Windows.Forms.Label
            {
                Text = "F High (Hz):",
                AccessibleName = "F High label",
                AutoSize = true,
                Location = new System.Drawing.Point(left + 190, y + 3),
                Font = font,
                TabStop = false,
            };
            decodePanel.Controls.Add(fhighLabel);

            _decodeFHighUpDown = new System.Windows.Forms.NumericUpDown
            {
                Minimum = 200,
                Maximum = 3900,
                Value = Math.Max(200, Math.Min(3900, ctrl.Decode.DecodeFHighHz)),
                Location = new System.Drawing.Point(left + 290, y),
                Size = new System.Drawing.Size(70, 21),
                TabIndex = 2,
                Font = font,
                AccessibleName = "Decode F High Hz",
                AccessibleDescription = "Decoder passband high edge in Hz. WSJT-X default 2900; raise toward 4000 to catch stations calling high on a crowded band.",
            };
            decodePanel.Controls.Add(_decodeFHighUpDown);
            y += 32;

            _decodeApDecodeCheckBox = MakeCheck(decodePanel,
                "Enable AP", "Enable AP",
                left, y, 3, ctrl.Decode.ApDecode, font);
            _decodeApDecodeCheckBox.AccessibleDescription =
                "WSJT-X's Decode menu 'Enable AP' -- a-priori decoding. FT8 only. On by default; off means the decoder tries no hypothesis-assisted passes.";
            _decodeApDecodeCheckBox.CheckedChanged += (s, e) => UpdateApCqOnlyEnabled();
            y += 24;

            _decodeApCqOnlyCheckBox = MakeCheck(decodePanel,
                "AP for CQ only (expert)", "AP for CQ only, expert",
                left, y, 4, ctrl.Decode.ApCqOnly, font);
            _decodeApCqOnlyCheckBox.AccessibleDescription =
                "Restricts AP to the CQ hypothesis only, requires Enable AP above. WSJT-X flips this automatically after 5 idle minutes; here it's an explicit choice. Off by default.";
            y += 24;

            _decodeSingleDecodeCheckBox = MakeCheck(decodePanel,
                "Single decode", "Single decode",
                left, y, 5, ctrl.Decode.SingleDecode, font);
            _decodeSingleDecodeCheckBox.AccessibleDescription =
                "Narrows the FT8/FT4 search to your RX offset plus or minus 25 Hz. Note: stock WSJT-X's own Single decode checkbox does nothing for FT8/FT4 -- this one actually works. Off by default (full passband).";
            y += 32;

            UpdateApCqOnlyEnabled();
        }

        private void UpdateApCqOnlyEnabled()
        {
            if (_decodeApCqOnlyCheckBox != null)
                _decodeApCqOnlyCheckBox.Enabled = _decodeApDecodeCheckBox?.Checked ?? false;
        }

        // Options > Frequencies panel, rebuilt 2026-08-12 (and again same day per live
        // feedback -- "make it more like the Hotkeys panel, this doesn't navigate well"): ONE
        // flat list, same shape as BuildHotkeysTab/BuildActionList below -- every band's entries
        // in a single ListBox (group name folded into each band's first item, exactly like
        // "General Commands: Options"), a small fixed set of edit fields to the right that
        // always reflect whichever row is selected, no separate band/mode picker to navigate
        // through first. Replaces the old fixed one-NumericUpDown-per-band-per-mode grid (22
        // always-visible fields) and the old fixed per-band hotkeys (HotkeyAction.Band160m..
        // Band6m, removed entirely -- not worth carrying forward, all defaulted to Keys.None).
        //
        // internal (not private): JimmyTests calls this directly (see InternalsVisibleTo in
        // AssemblyInfo.Testing.cs) to exercise the exact method that crashed live, 2026-08-12,
        // without needing the rest of OptionsDlg_Load's other 15 Build*Tab() calls (several of
        // which need their own unrelated scaffolding -- e.g. BuildHotkeysTab needs a real
        // Controller.hotkeyConfig, BuildRadioTab touches System.IO.Ports -- not needed just to
        // cover this bug).
        internal void BuildFrequenciesTab()
        {
            frequenciesPanel.Controls.Clear();

            var font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F);
            int tabIdx = 0;

            var instrBox = new System.Windows.Forms.TextBox
            {
                ReadOnly = true,
                Multiline = true,
                BorderStyle = System.Windows.Forms.BorderStyle.None,
                BackColor = frequenciesPanel.BackColor,
                ForeColor = System.Drawing.SystemColors.ControlText,
                Location = new System.Drawing.Point(8, 8),
                Size = new System.Drawing.Size(640, 34),
                Text = "Choose a frequency, then tab to the field you want to change, or the shortcut field to assign a hotkey.",
                TabStop = false,
                AccessibleName = "Frequencies usage instructions",
                Font = font,
            };
            frequenciesPanel.Controls.Add(instrBox);

            // Deep-clone into a working copy -- Add/Remove/edit below only ever touch this;
            // SaveFrequenciesTab (OK button only) is what commits it back to ctrl.Frequencies.
            // Cancel just discards this OptionsDlg instance, same pending-until-OK shape the
            // Hotkeys panel's own _pendingKeys already uses. Every band gets its built-in
            // defaults materialized up front (not lazily per-band) since the whole list is
            // always on screen at once now, not revealed one band at a time.
            _pendingFreqBands = new List<FrequencyEntry>[ctrl.Frequencies.Bands.Length];
            for (int i = 0; i < _pendingFreqBands.Length; i++)
            {
                _pendingFreqBands[i] = new List<FrequencyEntry>();
                foreach (var e in ctrl.Frequencies.Bands[i]) _pendingFreqBands[i].Add(e.Clone());
                EnsureBandHasEntries(i);
                _pendingFreqBands[i].Sort((a, b) => a.FreqKHz.CompareTo(b.FreqKHz));
            }

            // Frequencies list box -- Tab stop 0, same position/size as the Hotkeys panel's own
            // action list.
            _freqListBox = new System.Windows.Forms.ListBox
            {
                Location = new System.Drawing.Point(8, 50),
                Size = new System.Drawing.Size(330, 262),
                TabIndex = tabIdx++,
                Font = font,
                AccessibleName = "Frequencies",
                AccessibleDescription = "Every band's FT8 and FT4 calling frequencies. The first entry for each mode in a band is what Band Up/Down tunes to.",
            };
            // NOT populated here: BuildFreqList() ends by calling LoadSelectedFreqEntry(),
            // which reads _freqValueUpDown/_freqHotkeyCaptureBox/_freqClearHotkeyButton/
            // _freqRemoveButton -- none of which exist yet at this point in construction.
            // Root-caused live, 2026-08-12: calling it this early threw a NullReferenceException
            // the instant Options opened (Alt+O), before any of those controls were created --
            // moved to the end of this method, after everything it touches actually exists.
            _freqListBox.SelectedIndexChanged += (s, e) => LoadSelectedFreqEntry();
            frequenciesPanel.Controls.Add(_freqListBox);

            frequenciesPanel.Controls.Add(new System.Windows.Forms.Label
            {
                Text = "Frequency (kHz):",
                Location = new System.Drawing.Point(356, 50),
                Size = new System.Drawing.Size(140, 18),
                Font = font,
                TabStop = false,
            });

            // Tab stop 1 -- edits whichever entry is selected in the list.
            _freqValueUpDown = new System.Windows.Forms.NumericUpDown
            {
                Minimum = 1800,
                Maximum = 54000,
                Location = new System.Drawing.Point(356, 70),
                Size = new System.Drawing.Size(100, 22),
                TabIndex = tabIdx++,
                Font = font,
                AccessibleName = "Selected frequency kHz",
                AccessibleDescription = "Frequency in kHz for the entry currently selected in the list.",
            };
            _freqValueUpDown.Leave += (s, e) => CommitFreqValue();
            frequenciesPanel.Controls.Add(_freqValueUpDown);

            frequenciesPanel.Controls.Add(new System.Windows.Forms.Label
            {
                Text = "Hotkey:",
                Location = new System.Drawing.Point(356, 100),
                Size = new System.Drawing.Size(140, 18),
                Font = font,
                TabStop = false,
            });

            // Tab stop 2 -- one shared capture box, same idiom as the Hotkeys panel's own
            // _sharedCaptureBox. Deliberately no ReadOnly here, matching _sharedCaptureBox --
            // HotkeyCaptureBox already suppresses OnKeyPress itself (see that class), so
            // ReadOnly adds no real protection, only an extra "read only" JAWS/NVDA announcement
            // that the Hotkeys panel's own box doesn't have. Blind operator feedback, 2026-08-12.
            _freqHotkeyCaptureBox = new HotkeyCaptureBox
            {
                Location = new System.Drawing.Point(356, 120),
                Size = new System.Drawing.Size(160, 22),
                TabIndex = tabIdx++,
                Font = font,
                AccessibleName = "Selected entry hotkey",
                AccessibleDescription = "Press a key combination to jump straight to this frequency, switching mode if needed. Empty means no hotkey.",
            };
            _freqHotkeyCaptureBox.KeyCaptured += (s, ev) => OnFreqKeyCaptured(ev.Keys);
            frequenciesPanel.Controls.Add(_freqHotkeyCaptureBox);

            _freqClearHotkeyButton = new System.Windows.Forms.Button
            {
                Text = "Clear",
                Location = new System.Drawing.Point(522, 119),
                Size = new System.Drawing.Size(55, 23),
                TabIndex = tabIdx++,
                Font = font,
                AccessibleName = "Clear selected entry's hotkey",
            };
            _freqClearHotkeyButton.Click += (s, e) => OnFreqKeyCaptured(System.Windows.Forms.Keys.None);
            frequenciesPanel.Controls.Add(_freqClearHotkeyButton);

            _freqAddButton = new System.Windows.Forms.Button
            {
                Text = "Add",
                Location = new System.Drawing.Point(356, 156),
                Size = new System.Drawing.Size(80, 24),
                TabIndex = tabIdx++,
                Font = font,
                AccessibleName = "Add frequency",
                AccessibleDescription = "Adds a copy of the selected entry to the same band -- edit the Frequency field afterward to the new value.",
            };
            _freqAddButton.Click += FreqAddButton_Click;
            frequenciesPanel.Controls.Add(_freqAddButton);

            _freqRemoveButton = new System.Windows.Forms.Button
            {
                Text = "Remove",
                Location = new System.Drawing.Point(446, 156),
                Size = new System.Drawing.Size(80, 24),
                TabIndex = tabIdx++,
                Font = font,
                AccessibleName = "Remove frequency",
                AccessibleDescription = "Removes the selected entry. A band's last FT8 or last FT4 entry cannot be removed.",
            };
            _freqRemoveButton.Click += FreqRemoveButton_Click;
            frequenciesPanel.Controls.Add(_freqRemoveButton);

            var restoreButton = new System.Windows.Forms.Button
            {
                Text = "Restore All to Defaults",
                Location = new System.Drawing.Point(356, 192),
                Size = new System.Drawing.Size(160, 27),
                TabIndex = tabIdx++,
                Font = font,
                AccessibleName = "Restore all frequencies to defaults",
                AccessibleDescription = "Removes every custom frequency and hotkey on every band, back to Jimmy's single built-in calling frequency per band.",
            };
            restoreButton.Click += (s, e) =>
            {
                for (int i = 0; i < _pendingFreqBands.Length; i++)
                {
                    _pendingFreqBands[i].Clear();
                    EnsureBandHasEntries(i);
                }
                BuildFreqList();
            };
            frequenciesPanel.Controls.Add(restoreButton);

            // Populates the list AND (via its own trailing LoadSelectedFreqEntry() call) syncs
            // the edit fields to the initial selection -- safe here, now that every control it
            // touches has actually been created above.
            BuildFreqList();
        }

        // Every band+mode always has at least one real entry once this runs -- an operator who
        // never customized a band sees its built-in default here rather than the panel needing
        // separate "no entries yet" display logic anywhere else.
        private void EnsureBandHasEntries(int bandIdx)
        {
            if (_pendingFreqBands[bandIdx].Count > 0) return;
            var defaults = wsjtxClient.FreqsDictDefaults;
            _pendingFreqBands[bandIdx].Add(new FrequencyEntry { Mode = "FT8", FreqKHz = defaults["FT8"][bandIdx] });
            _pendingFreqBands[bandIdx].Add(new FrequencyEntry { Mode = "FT4", FreqKHz = defaults["FT4"][bandIdx] });
        }

        // Rebuilds the flat list from every band's _pendingFreqBands entries -- each band's
        // first entry gets the band name folded into its own row text ("160 Meter Band: FT8 —
        // 1,840 kHz"), every other entry in that band just indents, exactly the same grouping
        // idiom BuildActionList uses for "General Commands:" / "Accessibility Navigation:".
        private void BuildFreqList()
        {
            var bandsMeters = wsjtxClient.BandsMeters;
            _freqListBox.Items.Clear();
            _freqListBoxEntries = new List<FrequencyEntry>();
            _freqListBoxBandIdx = new List<int>();
            for (int b = 0; b < _pendingFreqBands.Length; b++)
            {
                var entries = _pendingFreqBands[b];
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    _freqListBoxEntries.Add(entry);
                    _freqListBoxBandIdx.Add(b);
                    string prefix = i == 0 ? $"{bandsMeters[b]} Meter Band: " : "  ";
                    _freqListBox.Items.Add(prefix + FreqRowText(entry));
                }
            }
            _freqListBox.SelectedIndex = _freqListBoxEntries.Count > 0 ? 0 : -1;
            // Setting SelectedIndex above only fires SelectedIndexChanged on an actual change --
            // harmless to also call this directly so the edit fields are never left stale.
            LoadSelectedFreqEntry();
        }

        // Matches the Hotkeys panel's own BuildActionList: the row is just the one thing it's
        // primarily about (mode + frequency here, action name there) -- the hotkey, if any, is
        // shown only in the separate capture box below once selected, not folded into the row
        // text. Blind operator feedback, 2026-08-12: embedding it here made every row read as
        // a run-on "Mode — Freq kHz [Hotkey]" phrase instead of one clean piece of information.
        private static string FreqRowText(FrequencyEntry entry)
        {
            return $"{entry.Mode} — {entry.FreqKHz:N0} kHz";
        }

        private void LoadSelectedFreqEntry()
        {
            bool hasSelection = _freqListBoxEntries != null
                && _freqListBox.SelectedIndex >= 0 && _freqListBox.SelectedIndex < _freqListBoxEntries.Count;
            _freqValueUpDown.Enabled = hasSelection;
            _freqHotkeyCaptureBox.Enabled = hasSelection;
            _freqClearHotkeyButton.Enabled = hasSelection;
            _freqRemoveButton.Enabled = hasSelection;
            if (!hasSelection) return;

            var entry = _freqListBoxEntries[_freqListBox.SelectedIndex];
            _freqUpdatingFields = true;
            try
            {
                _freqValueUpDown.Value = Math.Max(_freqValueUpDown.Minimum, Math.Min(_freqValueUpDown.Maximum, entry.FreqKHz));
                _freqHotkeyCaptureBox.SetValue(entry.Hotkey);
            }
            finally
            {
                _freqUpdatingFields = false;
            }
        }

        private void CommitFreqValue()
        {
            if (_freqUpdatingFields || _freqListBox.SelectedIndex < 0) return;
            var entry = _freqListBoxEntries[_freqListBox.SelectedIndex];
            int newVal = (int)_freqValueUpDown.Value;
            if (entry.FreqKHz == newVal) return;
            int bandIdx = _freqListBoxBandIdx[_freqListBox.SelectedIndex];
            entry.FreqKHz = newVal;
            _pendingFreqBands[bandIdx].Sort((a, b) => a.FreqKHz.CompareTo(b.FreqKHz));
            BuildFreqList();
            int idx = _freqListBoxEntries.IndexOf(entry);
            if (idx >= 0) _freqListBox.SelectedIndex = idx;
        }

        private void FreqAddButton_Click(object sender, EventArgs e)
        {
            if (_freqListBox.SelectedIndex < 0) return;
            int bandIdx = _freqListBoxBandIdx[_freqListBox.SelectedIndex];
            var selected = _freqListBoxEntries[_freqListBox.SelectedIndex];
            var newEntry = new FrequencyEntry { Mode = selected.Mode, FreqKHz = selected.FreqKHz, Hotkey = Keys.None };
            _pendingFreqBands[bandIdx].Add(newEntry);
            _pendingFreqBands[bandIdx].Sort((a, b) => a.FreqKHz.CompareTo(b.FreqKHz));
            BuildFreqList();
            int idx = _freqListBoxEntries.IndexOf(newEntry);
            if (idx >= 0) _freqListBox.SelectedIndex = idx;
            // Blind operator feedback, 2026-08-12: without this, focus stayed on the Add button
            // after the click -- JAWS only announces a selection change on a control that has
            // focus, so the new entry (and the fact anything happened at all) went completely
            // unannounced.
            _freqListBox.Focus();
        }

        private void FreqRemoveButton_Click(object sender, EventArgs e)
        {
            if (_freqListBox.SelectedIndex < 0) return;
            int bandIdx = _freqListBoxBandIdx[_freqListBox.SelectedIndex];
            var entry = _freqListBoxEntries[_freqListBox.SelectedIndex];
            int othersOfMode = 0;
            foreach (var other in _pendingFreqBands[bandIdx]) if (other != entry && other.Mode == entry.Mode) othersOfMode++;
            if (othersOfMode == 0)
            {
                MessageBox.Show($"Can't remove the last {entry.Mode} entry on this band.",
                    ctrl.friendlyName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            int removedIdx = _freqListBox.SelectedIndex;
            _pendingFreqBands[bandIdx].Remove(entry);
            BuildFreqList();
            if (_freqListBoxEntries.Count > 0)
                _freqListBox.SelectedIndex = Math.Min(removedIdx, _freqListBoxEntries.Count - 1);
            // Same focus fix as FreqAddButton_Click -- without it, the removal (and whatever
            // entry ends up selected afterward) went completely unannounced.
            _freqListBox.Focus();
        }

        // Mirrors OnKeyCaptured (Hotkeys panel) -- same reserved/valid checks, plus
        // conflict-checking against both the pending Hotkeys-panel assignments and every other
        // frequency entry across every band (a hotkey has to be globally unique regardless of
        // which panel assigned it, not just unique within the current band).
        private void OnFreqKeyCaptured(Keys keys)
        {
            if (_freqListBox.SelectedIndex < 0) return;
            var entry = _freqListBoxEntries[_freqListBox.SelectedIndex];

            if (keys != Keys.None)
            {
                string keyStr = HotkeyConfig.FormatKeys(keys);
                if (HotkeyConfig.IsReserved(keys))
                {
                    MessageBox.Show($"{keyStr} is a reserved system shortcut and cannot be assigned.",
                        ctrl.friendlyName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _freqHotkeyCaptureBox.SetValue(entry.Hotkey);
                    return;
                }
                if (!HotkeyConfig.IsValid(keys))
                {
                    MessageBox.Show(
                        $"{keyStr} is not a valid shortcut.\r\n\r\nUse a combination with Alt or Ctrl, or a function key (F1-F24).",
                        ctrl.friendlyName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _freqHotkeyCaptureBox.SetValue(entry.Hotkey);
                    return;
                }
                if (_pendingKeys != null)
                {
                    foreach (var kv in _pendingKeys)
                    {
                        if (kv.Value != keys) continue;
                        MessageBox.Show($"{keyStr} is already assigned to {HotkeyConfig.DisplayNames[kv.Key]}.",
                            ctrl.friendlyName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        _freqHotkeyCaptureBox.SetValue(entry.Hotkey);
                        return;
                    }
                }
                foreach (var bandList in _pendingFreqBands)
                {
                    foreach (var other in bandList)
                    {
                        if (other == entry || other.Hotkey != keys) continue;
                        MessageBox.Show($"{keyStr} is already assigned to another frequency entry.",
                            ctrl.friendlyName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        _freqHotkeyCaptureBox.SetValue(entry.Hotkey);
                        return;
                    }
                }
            }

            entry.Hotkey = keys;
            BuildFreqList();
            int idx = _freqListBoxEntries.IndexOf(entry);
            if (idx >= 0) _freqListBox.SelectedIndex = idx;
        }

        private void SaveFrequenciesTab()
        {
            if (_pendingFreqBands == null) return;
            for (int i = 0; i < _pendingFreqBands.Length; i++)
            {
                ctrl.Frequencies.Bands[i].Clear();
                ctrl.Frequencies.Bands[i].AddRange(_pendingFreqBands[i]);
            }
        }

        // ===== NOTIFICATIONS TAB =====
        //
        // Same overall shape as the Hotkeys/Frequencies panels: one flat list you arrow
        // through (here, every configurable notification type, checked = enabled), a small
        // fixed set of controls to the right/below that always reflect whichever type is
        // currently selected. Two things are specific to this panel:
        //
        // 1. A SECOND list -- the variables applicable to the selected type, checked = present
        //    in its template, with Move Up/Down to reorder. Checking/unchecking/reordering here
        //    edits the Template text box live; typing directly into the Template box re-syncs
        //    this list's checked state and order on the way out. The template STRING is always
        //    the single source of truth (see NotificationVariableRegistry.Validate) -- this
        //    list is a view onto it, never a second place the same information is stored.
        //
        // 2. An Advanced section (Timing/Defer-while-transmitting/Repeat/Throttle/Suppress-
        //    unchanged/Priority) -- reachable by Tab for anyone who wants it, but a simple
        //    operator only ever needs the first list's checkboxes and can leave everything
        //    else at its default.
        private void BuildNotificationsTab()
        {
            notificationsPanel.Controls.Clear();

            var font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F);
            int tabIdx = 0;

            var instrBox = new System.Windows.Forms.TextBox
            {
                ReadOnly = true,
                Multiline = true,
                BorderStyle = System.Windows.Forms.BorderStyle.None,
                BackColor = notificationsPanel.BackColor,
                ForeColor = System.Drawing.SystemColors.ControlText,
                Location = new System.Drawing.Point(8, 8),
                Size = new System.Drawing.Size(660, 34),
                Text = "Check a notification to enable it. Choose one to edit what it says and, further down, when and how it announces.",
                TabStop = false,
                AccessibleName = "Notifications usage instructions",
                Font = font,
            };
            notificationsPanel.Controls.Add(instrBox);

            // Deep-clone into a working copy -- every control below only ever touches this;
            // SaveNotificationsTab (OK button only) commits it back to ctrl.Notifications.
            // Cancel just discards this OptionsDlg instance.
            _pendingNotifyPolicies = new Dictionary<NotificationEventType, NotificationPolicy>();
            foreach (var kv in ctrl.Notifications.Policies) _pendingNotifyPolicies[kv.Key] = kv.Value.Clone();
            // Stable, deliberate order (not Enum.GetValues' declaration order) -- groups the
            // three currently-live types together, first, since they're the ones an operator is
            // most likely to actually hear today; the four parked types follow. Both groups are
            // fully configurable either way (see NotificationEvents.cs's own comment on why
            // configurability and "is anything publishing this yet" are separate questions).
            _notifyTypeOrder = new List<NotificationEventType>
            {
                NotificationEventType.ConnectionLost,
                NotificationEventType.ConnectionClosed,
                NotificationEventType.ErrorWarning,
                NotificationEventType.ClockOutOfSync,
                NotificationEventType.ClockSynced,
                NotificationEventType.QsoStarted,
                NotificationEventType.QsoCompleted,
                NotificationEventType.TxMessageChanged,
                NotificationEventType.AwardsNeeded,
            };

            _notifyTypesListBox = new System.Windows.Forms.CheckedListBox
            {
                Location = new System.Drawing.Point(8, 50),
                Size = new System.Drawing.Size(230, 150),
                TabIndex = tabIdx++,
                Font = font,
                CheckOnClick = true,
                AccessibleName = "Notifications",
                AccessibleDescription = "Every configurable notification. Check to enable.",
            };
            foreach (var type in _notifyTypeOrder)
                _notifyTypesListBox.Items.Add(NotificationDefaults.DisplayNames[type], _pendingNotifyPolicies[type].Enabled);
            _notifyTypesListBox.SelectedIndexChanged += (s, e) => LoadSelectedNotifyType();
            _notifyTypesListBox.ItemCheck += (s, e) =>
            {
                // ItemCheck fires BEFORE the box's own state updates -- e.NewValue is
                // authoritative for what it's about to become.
                var type = _notifyTypeOrder[e.Index];
                _pendingNotifyPolicies[type].Enabled = e.NewValue == System.Windows.Forms.CheckState.Checked;
            };
            notificationsPanel.Controls.Add(_notifyTypesListBox);

            notificationsPanel.Controls.Add(new System.Windows.Forms.Label
            {
                Text = "Variables for this notification (check to include):",
                Location = new System.Drawing.Point(246, 50),
                Size = new System.Drawing.Size(260, 18),
                Font = font,
                TabStop = false,
            });

            _notifyVarsListBox = new System.Windows.Forms.CheckedListBox
            {
                Location = new System.Drawing.Point(246, 68),
                Size = new System.Drawing.Size(260, 132),
                TabIndex = tabIdx++,
                Font = font,
                CheckOnClick = true,
                AccessibleName = "Template variables",
                AccessibleDescription = "Variables this notification can include. Check to add to the template, uncheck to remove. Order matches the template's own left-to-right order.",
            };
            _notifyVarsListBox.ItemCheck += (s, e) => NotifyVarCheckChanged(e);
            notificationsPanel.Controls.Add(_notifyVarsListBox);

            _notifyVarMoveUpButton = new System.Windows.Forms.Button
            {
                Text = "Move Up",
                Location = new System.Drawing.Point(514, 68),
                Size = new System.Drawing.Size(90, 24),
                TabIndex = tabIdx++,
                Font = font,
                AccessibleName = "Move selected variable earlier in the template",
            };
            _notifyVarMoveUpButton.Click += (s, e) => MoveNotifyVar(-1);
            notificationsPanel.Controls.Add(_notifyVarMoveUpButton);

            _notifyVarMoveDownButton = new System.Windows.Forms.Button
            {
                Text = "Move Down",
                Location = new System.Drawing.Point(514, 96),
                Size = new System.Drawing.Size(90, 24),
                TabIndex = tabIdx++,
                Font = font,
                AccessibleName = "Move selected variable later in the template",
            };
            _notifyVarMoveDownButton.Click += (s, e) => MoveNotifyVar(1);
            notificationsPanel.Controls.Add(_notifyVarMoveDownButton);

            notificationsPanel.Controls.Add(new System.Windows.Forms.Label
            {
                Text = "Template:",
                Location = new System.Drawing.Point(8, 210),
                Size = new System.Drawing.Size(120, 18),
                Font = font,
                TabStop = false,
            });

            _notifyTemplateTextBox = new System.Windows.Forms.TextBox
            {
                Location = new System.Drawing.Point(8, 228),
                Size = new System.Drawing.Size(596, 22),
                TabIndex = tabIdx++,
                Font = font,
                AccessibleName = "Template text",
                AccessibleDescription = "The exact wording spoken, including {Variable} placeholders and any literal text you type.",
            };
            _notifyTemplateTextBox.Leave += (s, e) => CommitNotifyTemplateText();
            notificationsPanel.Controls.Add(_notifyTemplateTextBox);

            _notifyValidationLabel = new AnnouncingLabel
            {
                Text = "",
                Location = new System.Drawing.Point(8, 252),
                Size = new System.Drawing.Size(660, 18),
                Font = font,
                ForeColor = System.Drawing.Color.Firebrick,
                AccessibleName = "Template validation result",
                TabStop = false,
            };
            notificationsPanel.Controls.Add(_notifyValidationLabel);

            var advancedGroup = new System.Windows.Forms.GroupBox
            {
                Text = "Advanced (timing and repeat behavior)",
                Location = new System.Drawing.Point(8, 278),
                Size = new System.Drawing.Size(596, 168),
                Font = font,
            };
            notificationsPanel.Controls.Add(advancedGroup);

            var whenLabel = new System.Windows.Forms.Label
            {
                Text = "When to announce it:",
                Location = new System.Drawing.Point(10, 22),
                Size = new System.Drawing.Size(160, 18),
                Font = font,
                TabStop = false,
            };
            advancedGroup.Controls.Add(whenLabel);

            _notifyTimingImmediateRadio = new System.Windows.Forms.RadioButton
            {
                Text = "Immediately, the moment it happens",
                Location = new System.Drawing.Point(10, 42),
                Size = new System.Drawing.Size(280, 20),
                TabIndex = tabIdx++,
                Font = font,
                AccessibleName = "Announce immediately",
            };
            advancedGroup.Controls.Add(_notifyTimingImmediateRadio);

            _notifyTimingDeferredRadio = new System.Windows.Forms.RadioButton
            {
                Text = "Wait for the next receive cycle (batches repeats)",
                Location = new System.Drawing.Point(10, 64),
                Size = new System.Drawing.Size(320, 20),
                TabIndex = tabIdx++,
                Font = font,
                AccessibleName = "Announce at the next receive cycle",
            };
            advancedGroup.Controls.Add(_notifyTimingDeferredRadio);
            _notifyTimingImmediateRadio.CheckedChanged += (s, e) => CommitNotifyTiming();
            _notifyTimingDeferredRadio.CheckedChanged += (s, e) => CommitNotifyTiming();

            _notifyDeferWhileTxCheckBox = new System.Windows.Forms.CheckBox
            {
                Text = "Wait until transmitting stops",
                Location = new System.Drawing.Point(10, 88),
                Size = new System.Drawing.Size(280, 20),
                TabIndex = tabIdx++,
                Font = font,
                AccessibleName = "Defer while transmitting",
                AccessibleDescription = "When checked, this notification never interrupts an active transmission -- it waits until the current one ends.",
            };
            _notifyDeferWhileTxCheckBox.CheckedChanged += (s, e) => CommitNotifyCheckboxes();
            advancedGroup.Controls.Add(_notifyDeferWhileTxCheckBox);

            _notifySuppressUnchangedCheckBox = new System.Windows.Forms.CheckBox
            {
                Text = "Don't repeat if nothing changed",
                Location = new System.Drawing.Point(10, 110),
                Size = new System.Drawing.Size(280, 20),
                TabIndex = tabIdx++,
                Font = font,
                AccessibleName = "Suppress unchanged repeats",
                AccessibleDescription = "When checked, this notification stays silent if the exact same wording was already just announced.",
            };
            _notifySuppressUnchangedCheckBox.CheckedChanged += (s, e) => CommitNotifyCheckboxes();
            advancedGroup.Controls.Add(_notifySuppressUnchangedCheckBox);

            var repeatLabel = new System.Windows.Forms.Label
            {
                Text = "Don't repeat within (seconds, 0 = no limit):",
                Location = new System.Drawing.Point(300, 42),
                Size = new System.Drawing.Size(240, 18),
                Font = font,
                TabStop = false,
            };
            advancedGroup.Controls.Add(repeatLabel);

            _notifyRepeatSecondsUpDown = new System.Windows.Forms.NumericUpDown
            {
                Minimum = 0,
                Maximum = 600,
                Location = new System.Drawing.Point(300, 60),
                Size = new System.Drawing.Size(70, 22),
                TabIndex = tabIdx++,
                Font = font,
                AccessibleName = "Minimum seconds between repeats",
            };
            _notifyRepeatSecondsUpDown.ValueChanged += (s, e) => CommitNotifyNumeric();
            advancedGroup.Controls.Add(_notifyRepeatSecondsUpDown);

            var throttleLabel = new System.Windows.Forms.Label
            {
                Text = "Minimum gap between any two (ms, 0 = none):",
                Location = new System.Drawing.Point(300, 88),
                Size = new System.Drawing.Size(250, 18),
                Font = font,
                TabStop = false,
            };
            advancedGroup.Controls.Add(throttleLabel);

            _notifyThrottleMsUpDown = new System.Windows.Forms.NumericUpDown
            {
                Minimum = 0,
                Maximum = 60000,
                Increment = 100,
                Location = new System.Drawing.Point(300, 106),
                Size = new System.Drawing.Size(80, 22),
                TabIndex = tabIdx++,
                Font = font,
                AccessibleName = "Minimum milliseconds between any two announcements of this type",
            };
            _notifyThrottleMsUpDown.ValueChanged += (s, e) => CommitNotifyNumeric();
            advancedGroup.Controls.Add(_notifyThrottleMsUpDown);

            var priorityLabel = new System.Windows.Forms.Label
            {
                Text = "Priority:",
                Location = new System.Drawing.Point(300, 134),
                Size = new System.Drawing.Size(60, 18),
                Font = font,
                TabStop = false,
            };
            advancedGroup.Controls.Add(priorityLabel);

            _notifyPriorityComboBox = new System.Windows.Forms.ComboBox
            {
                DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList,
                Location = new System.Drawing.Point(360, 130),
                Size = new System.Drawing.Size(110, 21),
                TabIndex = tabIdx++,
                Font = font,
                AccessibleName = "Priority",
                AccessibleDescription = "Important notifications also play the audible alert tone.",
            };
            _notifyPriorityComboBox.Items.Add("Normal");
            _notifyPriorityComboBox.Items.Add("Important");
            _notifyPriorityComboBox.SelectedIndexChanged += (s, e) => CommitNotifyCheckboxes();
            advancedGroup.Controls.Add(_notifyPriorityComboBox);

            var resetThisButton = new System.Windows.Forms.Button
            {
                Text = "Reset This Notification to Default",
                Location = new System.Drawing.Point(8, 454),
                Size = new System.Drawing.Size(220, 27),
                TabIndex = tabIdx++,
                Font = font,
                AccessibleName = "Reset this notification to default",
                AccessibleDescription = "Restores the selected notification's template, timing, and repeat settings to Jimmy's built-in defaults.",
            };
            resetThisButton.Click += ResetThisNotification_Click;
            notificationsPanel.Controls.Add(resetThisButton);

            var resetAllButton = new System.Windows.Forms.Button
            {
                Text = "Reset All Notification Settings to Defaults",
                Location = new System.Drawing.Point(236, 454),
                Size = new System.Drawing.Size(260, 27),
                TabIndex = tabIdx++,
                Font = font,
                AccessibleName = "Reset all notification settings to defaults",
                AccessibleDescription = "Restores every notification's template, timing, and repeat settings to Jimmy's built-in defaults. Enabled/disabled state is reset too.",
            };
            resetAllButton.Click += ResetAllNotifications_Click;
            notificationsPanel.Controls.Add(resetAllButton);

            _notifyTypesListBox.SelectedIndex = 0;
        }

        private NotificationPolicy CurrentNotifyPolicy()
        {
            if (_notifyTypesListBox.SelectedIndex < 0) return null;
            return _pendingNotifyPolicies[_notifyTypeOrder[_notifyTypesListBox.SelectedIndex]];
        }

        private void LoadSelectedNotifyType()
        {
            var policy = CurrentNotifyPolicy();
            bool has = policy != null;
            _notifyVarsListBox.Enabled = has;
            _notifyVarMoveUpButton.Enabled = has;
            _notifyVarMoveDownButton.Enabled = has;
            _notifyTemplateTextBox.Enabled = has;
            _notifyTimingImmediateRadio.Enabled = has;
            _notifyTimingDeferredRadio.Enabled = has;
            _notifyDeferWhileTxCheckBox.Enabled = has;
            _notifySuppressUnchangedCheckBox.Enabled = has;
            _notifyRepeatSecondsUpDown.Enabled = has;
            _notifyThrottleMsUpDown.Enabled = has;
            _notifyPriorityComboBox.Enabled = has;
            if (!has) return;

            _notifyUpdatingFields = true;
            try
            {
                _notifyTemplateTextBox.Text = policy.Template;
                _notifyValidationLabel.Text = "";
                _notifyTimingImmediateRadio.Checked = policy.Timing == NotificationTiming.Immediate;
                _notifyTimingDeferredRadio.Checked = policy.Timing == NotificationTiming.NextPeriodBoundary;
                _notifyDeferWhileTxCheckBox.Checked = policy.DeferWhileTransmitting;
                _notifySuppressUnchangedCheckBox.Checked = policy.SuppressUnchanged;
                _notifyRepeatSecondsUpDown.Value = System.Math.Max(_notifyRepeatSecondsUpDown.Minimum, System.Math.Min(_notifyRepeatSecondsUpDown.Maximum, policy.RepeatSeconds));
                _notifyThrottleMsUpDown.Value = System.Math.Max(_notifyThrottleMsUpDown.Minimum, System.Math.Min(_notifyThrottleMsUpDown.Maximum, policy.ThrottleMilliseconds));
                _notifyPriorityComboBox.SelectedIndex = policy.Priority == NotificationPriority.Important ? 1 : 0;
                RefreshNotifyVarsList();
            }
            finally
            {
                _notifyUpdatingFields = false;
            }
        }

        // Rebuilds the variables checklist from the CURRENT template text -- the template
        // string is the single source of truth (see this method's own header comment on the
        // class), so this is the one and only place that reads it back into checked/order form.
        // Called after every edit path (checkbox, move, or direct typing), never the reverse.
        private void RefreshNotifyVarsList()
        {
            var type = _notifyTypeOrder[_notifyTypesListBox.SelectedIndex];
            var applicable = NotificationVariableRegistry.For(type);
            var present = NotificationTemplateEngine.ExtractVariableNames(_notifyTemplateTextBox.Text);

            // Present-and-known variables first, in their real template order (so the list's
            // top-to-bottom order matches Move Up/Down's own effect); any applicable variable
            // not yet used follows, alphabetical-by-declaration-order from the registry.
            _notifyVarsListEntries = new List<NotificationVariable>();
            foreach (string key in present)
            {
                NotificationVariable found = null;
                foreach (var x in applicable) { if (x.Key == key) { found = x; break; } }
                if (found != null) _notifyVarsListEntries.Add(found);
            }
            foreach (var v in applicable)
                if (!_notifyVarsListEntries.Contains(v)) _notifyVarsListEntries.Add(v);

            _notifyUpdatingFields = true;
            _notifyVarsListBox.BeginUpdate();
            try
            {
                _notifyVarsListBox.Items.Clear();
                var presentSet = new HashSet<string>(present);
                foreach (var v in _notifyVarsListEntries)
                    _notifyVarsListBox.Items.Add($"{v.Key} — {v.Description}", presentSet.Contains(v.Key));
            }
            finally
            {
                _notifyVarsListBox.EndUpdate();
                _notifyUpdatingFields = false;
            }
        }

        private void NotifyVarCheckChanged(System.Windows.Forms.ItemCheckEventArgs e)
        {
            if (_notifyUpdatingFields) return;
            var policy = CurrentNotifyPolicy();
            if (policy == null) return;
            var variable = _notifyVarsListEntries[e.Index];
            bool nowChecked = e.NewValue == System.Windows.Forms.CheckState.Checked;

            var components = NotificationTemplateEngine.ParseComponents(policy.Template);
            if (nowChecked)
            {
                // Append at the end, space-separated from whatever's already there -- literal
                // text already in the template is never touched.
                string sep = components.Count > 0 && !string.IsNullOrEmpty(policy.Template) && !policy.Template.EndsWith(" ") ? ", " : "";
                policy.Template = policy.Template + sep + "{" + variable.Key + "}";
            }
            else
            {
                // Remove every occurrence of just this variable's own token -- literal text
                // (including any the operator typed around it) is left exactly where it is.
                var sb = new System.Text.StringBuilder();
                foreach (var c in components)
                {
                    if (c.IsVariable && c.Text == variable.Key) continue;
                    sb.Append(c.IsVariable ? "{" + c.Text + "}" : c.Text);
                }
                policy.Template = sb.ToString();
            }

            _notifyUpdatingFields = true;
            _notifyTemplateTextBox.Text = policy.Template;
            _notifyValidationLabel.Text = "";
            _notifyUpdatingFields = false;

            // Deliberately do NOT touch _notifyVarsListBox.Items here. WinForms itself already
            // applies this item's own checked bit (CheckOnClick) once this handler returns --
            // nothing left for us to do there -- and _notifyVarsListEntries' existing order is
            // still valid since nothing's position has changed. Re-sorting the checklist into
            // true template order (checked items first, in template order) is deferred to the
            // next real structural change -- switching notification type, Move Up/Down, or
            // committing a hand-typed template edit -- all of which already call
            // RefreshNotifyVarsList() at a moment when this box isn't mid-toggle. Rebuilding
            // Items on every single Space-press used to tear down and recreate every item's
            // accessibility identity right as the screen reader was announcing the toggle it
            // just fired, which is what caused the live-reported silent/stale-announce bug.
        }

        private void MoveNotifyVar(int direction)
        {
            var policy = CurrentNotifyPolicy();
            if (policy == null || _notifyVarsListBox.SelectedIndex < 0) return;
            var variable = _notifyVarsListEntries[_notifyVarsListBox.SelectedIndex];

            var components = NotificationTemplateEngine.ParseComponents(policy.Template);
            var varIndexes = new List<int>();
            for (int i = 0; i < components.Count; i++) if (components[i].IsVariable) varIndexes.Add(i);
            int thisPos = varIndexes.FindIndex(i => components[i].Text == variable.Key);
            int swapWith = thisPos + direction;
            if (thisPos < 0 || swapWith < 0 || swapWith >= varIndexes.Count) return;   // already checked not-present/not-movable by the button's Enabled state, but stay safe

            // Swap just the two variable TOKENS' text in place -- every literal-text component
            // stays exactly where it is, so nothing an operator typed around them ever moves.
            int a = varIndexes[thisPos], b = varIndexes[swapWith];
            var tmp = components[a];
            components[a] = components[b];
            components[b] = tmp;

            var sb = new System.Text.StringBuilder();
            foreach (var c in components) sb.Append(c.IsVariable ? "{" + c.Text + "}" : c.Text);
            policy.Template = sb.ToString();

            ApplyNotifyTemplateChange(policy, resyncVars: true);
            // Re-select the moved variable so repeated Move Up/Down presses keep tracking it.
            int newIdx = _notifyVarsListEntries.FindIndex(v => v.Key == variable.Key);
            if (newIdx >= 0) _notifyVarsListBox.SelectedIndex = newIdx;
        }

        private void CommitNotifyTemplateText()
        {
            if (_notifyUpdatingFields) return;
            var policy = CurrentNotifyPolicy();
            if (policy == null) return;
            if (policy.Template == _notifyTemplateTextBox.Text) return;

            var type = _notifyTypeOrder[_notifyTypesListBox.SelectedIndex];
            string error = NotificationVariableRegistry.Validate(_notifyTemplateTextBox.Text, type);
            if (error != null)
            {
                // Do NOT touch policy.Template or the text box's own text -- the operator's
                // edit stays exactly as typed so they can fix it, per the feature's own
                // "never destroy the user's edit on validation failure" requirement.
                _notifyValidationLabel.Text = error;
                return;
            }

            _notifyValidationLabel.Text = "";
            policy.Template = _notifyTemplateTextBox.Text;
            RefreshNotifyVarsList();
        }

        // Shared tail for both the checklist-driven and Move-Up/Down-driven template edits:
        // both already know the new template text is well-formed (built from real components,
        // never hand-typed), so there's nothing to validate -- just push it into the text box
        // and, when the edit could have changed presence/order (a move; a checklist add/remove
        // already IS the list, so it skips this), resync the checklist from it.
        private void ApplyNotifyTemplateChange(NotificationPolicy policy, bool resyncVars)
        {
            _notifyUpdatingFields = true;
            try
            {
                _notifyTemplateTextBox.Text = policy.Template;
                _notifyValidationLabel.Text = "";
            }
            finally
            {
                _notifyUpdatingFields = false;
            }
            if (resyncVars) RefreshNotifyVarsList();
        }

        private void CommitNotifyTiming()
        {
            if (_notifyUpdatingFields) return;
            var policy = CurrentNotifyPolicy();
            if (policy == null) return;
            policy.Timing = _notifyTimingDeferredRadio.Checked ? NotificationTiming.NextPeriodBoundary : NotificationTiming.Immediate;
        }

        private void CommitNotifyCheckboxes()
        {
            if (_notifyUpdatingFields) return;
            var policy = CurrentNotifyPolicy();
            if (policy == null) return;
            policy.DeferWhileTransmitting = _notifyDeferWhileTxCheckBox.Checked;
            policy.SuppressUnchanged = _notifySuppressUnchangedCheckBox.Checked;
            policy.Priority = _notifyPriorityComboBox.SelectedIndex == 1 ? NotificationPriority.Important : NotificationPriority.Normal;
        }

        private void CommitNotifyNumeric()
        {
            if (_notifyUpdatingFields) return;
            var policy = CurrentNotifyPolicy();
            if (policy == null) return;
            policy.RepeatSeconds = (int)_notifyRepeatSecondsUpDown.Value;
            policy.ThrottleMilliseconds = (int)_notifyThrottleMsUpDown.Value;
        }

        private void ResetThisNotification_Click(object sender, EventArgs e)
        {
            if (_notifyTypesListBox.SelectedIndex < 0) return;
            var type = _notifyTypeOrder[_notifyTypesListBox.SelectedIndex];
            _pendingNotifyPolicies[type] = NotificationDefaults.Policies[type].Clone();
            _notifyUpdatingFields = true;
            _notifyTypesListBox.SetItemChecked(_notifyTypesListBox.SelectedIndex, _pendingNotifyPolicies[type].Enabled);
            _notifyUpdatingFields = false;
            LoadSelectedNotifyType();
        }

        private void ResetAllNotifications_Click(object sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "Reset every notification's template, timing, and repeat settings to Jimmy's built-in defaults?",
                ctrl.friendlyName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result != DialogResult.Yes) return;

            foreach (var type in _notifyTypeOrder)
                _pendingNotifyPolicies[type] = NotificationDefaults.Policies[type].Clone();

            _notifyUpdatingFields = true;
            for (int i = 0; i < _notifyTypeOrder.Count; i++)
                _notifyTypesListBox.SetItemChecked(i, _pendingNotifyPolicies[_notifyTypeOrder[i]].Enabled);
            _notifyUpdatingFields = false;
            LoadSelectedNotifyType();
        }

        private void SaveNotificationsTab()
        {
            if (_pendingNotifyPolicies == null) return;
            // Commit the field the operator is currently sitting in, in case OK was clicked
            // without ever leaving the Template box (Leave never fired).
            CommitNotifyTemplateText();
            foreach (var kv in _pendingNotifyPolicies)
                ctrl.Notifications.Policies[kv.Key] = kv.Value;
        }

        private void UpdateRadioHostPortEnabled()
        {
            bool external = _radioUseExternalCheckBox?.Checked ?? false;
            if (_radioHostTextBox != null) _radioHostTextBox.Enabled = external;
            if (_radioPortTextBox != null) _radioPortTextBox.Enabled = external;
            if (_radioRigModelCombo != null) _radioRigModelCombo.Enabled = !external;
            if (_radioComPortTextBox != null) _radioComPortTextBox.Enabled = !external;
            if (_radioBaudRateTextBox != null) _radioBaudRateTextBox.Enabled = !external;
        }

        // Applies the fields above to a throwaway RigctldClient, launching the bundled copy
        // (or connecting to the external one) exactly as SaveRadioTab would, then polls once.
        // Never touches ctrl.rigctldClient -- a failed test must not disturb a working session.
        // Sets the test-result label's text AND fires the MSAA NameChanged notification --
        // JAWS/NVDA do not announce a plain Label.Text change on an unfocused control, so
        // without this the result was only ever visible to sighted users (confirmed live,
        // 2026-08-06: "when i do the test radio button nothing happens either"). Does NOT move
        // focus -- this is the correct, non-focus-stealing way to announce a status change in
        // response to the user's own just-taken action.
        private void SetRadioTestResult(string text)
        {
            _radioTestResultLabel.Text = text;
            _radioTestResultLabel.AnnounceTextChanged();
        }

        // AccessibilityNotifyClients is `protected` on Control -- a plain Label can't be told to
        // fire it from outside its own class, hence this thin subclass exposing it publicly. See
        // SetRadioTestResult's own comment for why this exists at all.
        private class AnnouncingLabel : System.Windows.Forms.Label
        {
            public void AnnounceTextChanged() =>
                AccessibilityNotifyClients(System.Windows.Forms.AccessibleEvents.NameChange, -1);
        }

        // A label change alone was reported live, 2026-08-06, as unreliable ("got nothing back")
        // -- AccessibilityNotifyClients announces out of context if the operator has already
        // tabbed elsewhere, and is silent if they haven't yet interacted with the label at all.
        // A modal MessageBox always steals focus and is always announced by JAWS/NVDA, so it is
        // the result path that cannot be missed; the label is kept too for sighted users glancing
        // at the dialog.
        private void RadioTestButton_Click(object sender, EventArgs e)
        {
            SetRadioTestResult("Testing...");
            bool external = _radioUseExternalCheckBox.Checked;
            string host = external ? _radioHostTextBox.Text.Trim() : "127.0.0.1";
            int.TryParse(_radioPortTextBox.Text.Trim(), out int port);
            if (port <= 0) port = 4532;

            string resultText;
            bool ok;
            using (var test = new RigctldClient(host, port))
            {
                if (!external)
                {
                    if (!test.LaunchBundled(ExtractRigModelId(_radioRigModelCombo.Text.Trim()), _radioComPortTextBox.Text.Trim(), _radioBaudRateTextBox.Text.Trim()))
                    {
                        resultText = "FAIL: " + test.LastError;
                        ok = false;
                        SetRadioTestResult(resultText);
                        System.Windows.Forms.MessageBox.Show(this, resultText, "Radio Test Result",
                            System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
                        return;
                    }
                }

                var status = test.PollOnce();
                ok = status.Ok;
                resultText = status.Ok
                    ? "PASS -- rigctld answered."
                    : "FAIL: " + (status.LastError ?? "no response");
                SetRadioTestResult(resultText);

                if (!external) test.StopBundled();
            }

            System.Windows.Forms.MessageBox.Show(this, resultText, "Radio Test Result",
                System.Windows.Forms.MessageBoxButtons.OK,
                ok ? System.Windows.Forms.MessageBoxIcon.Information : System.Windows.Forms.MessageBoxIcon.Error);
        }

        private void SaveRadioTab()
        {
            if (_radioWsjtxCatRb == null) return;

            // Snapshot everything ApplyEngineMode()/ApplyRadioSettings() actually care about,
            // from BEFORE this save's writes below -- confirmed live, 2026-08-07: both used to
            // run unconditionally on every single Options save, restarting the live native
            // engine (and its rigctld connection) even when the operator only touched an
            // unrelated tab (e.g. Logbook Sync). That restart does not durably preserve the
            // radio's actual current frequency, so it retuned the operator's real radio away
            // from what it was actually on (40m -> 20m), with no radio-related change intended
            // at all. Now: only restart/reconnect when something that actually feeds into
            // either call changed.
            string wasMyCall = ctrl.NativeEngine.MyCall;
            string wasMyGrid = ctrl.NativeEngine.MyGrid;
            string wasAudioIn = ctrl.NativeEngine.AudioInputDevice;
            string wasAudioOut = ctrl.NativeEngine.AudioOutputDevice;
            var r = ctrl.Radio;
            var wasRadioMode = r.Mode;
            string wasRigModel = r.RigModel;
            string wasComPort = r.ComPort;
            string wasBaudRate = r.BaudRate;
            bool wasUseExternal = r.UseExternalRigctld;
            string wasHost = r.RigctldHost;
            int wasPort = r.RigctldPort;
            bool wasPttEnabled = r.PttEnabled;
            PttMethod wasPttMethod = r.PttMethod;
            RadioTxMode wasTxMode = r.TxMode;
            bool wasPttDataSource = r.PttDataSource;
            RadioSplitMode wasSplitMode = r.SplitMode;
            string wasPttSerialPort = r.PttSerialPort;
            int wasPollIntervalMs = r.PollIntervalMs;
            bool wasReadDisplayPwrSwr = r.ReadDisplayPwrSwr;
            bool wasHaltTxOnHighSwr = r.HaltTxOnHighSwr;
            double wasSwrHaltThreshold = r.SwrHaltThreshold;
            bool wasStartupPowerEnabled = r.StartupPowerEnabled;
            int wasStartupPowerWatts = r.StartupPowerWatts;
            int wasStartupPowerMaxWatts = r.StartupPowerMaxWatts;

            ctrl.Radio.Mode = _radioHamlibRb.Checked ? RadioControlMode.HamlibRigctld : RadioControlMode.WsjtxCat;
            ctrl.Radio.RigModel = ExtractRigModelId(_radioRigModelCombo.Text.Trim());
            ctrl.Radio.ComPort = _radioComPortTextBox.Text.Trim();
            ctrl.Radio.BaudRate = _radioBaudRateTextBox.Text.Trim();
            ctrl.Radio.UseExternalRigctld = _radioUseExternalCheckBox.Checked;
            ctrl.Radio.RigctldHost = _radioHostTextBox.Text.Trim();
            if (int.TryParse(_radioPortTextBox.Text.Trim(), out int port) && port > 0 && port <= 65535)
                ctrl.Radio.RigctldPort = port;
            ctrl.Radio.PttEnabled = _radioPttEnabledCheckBox.Checked;
            ctrl.Radio.PttMethod = _radioPttMethodCombo != null && Enum.TryParse(_radioPttMethodCombo.SelectedItem as string, out PttMethod pttMethod)
                ? pttMethod : ctrl.Radio.PttMethod;
            if (_radioModeNoneRb != null)
            {
                ctrl.Radio.TxMode = _radioModeNoneRb.Checked ? RadioTxMode.None
                    : _radioModeUsbRb.Checked ? RadioTxMode.Usb
                    : RadioTxMode.DataPkt;
            }
            if (_radioPttDataSourceCheckBox != null) ctrl.Radio.PttDataSource = _radioPttDataSourceCheckBox.Checked;
            if (_radioSplitNoneRb != null)
            {
                ctrl.Radio.SplitMode = _radioSplitRigRb.Checked ? RadioSplitMode.Rig
                    : _radioSplitFakeItRb.Checked ? RadioSplitMode.FakeIt
                    : RadioSplitMode.None;
            }
            if (_radioPttSerialPortCombo != null) ctrl.Radio.PttSerialPort = _radioPttSerialPortCombo.Text.Trim();
            if (_radioPollIntervalUpDown != null) ctrl.Radio.PollIntervalMs = (int)_radioPollIntervalUpDown.Value * 1000;
            if (_radioReadDisplayPwrSwrCheckBox != null)
            {
                ctrl.Radio.ReadDisplayPwrSwr = _radioReadDisplayPwrSwrCheckBox.Checked;
                ctrl.Radio.PollEnabled = _radioReadDisplayPwrSwrCheckBox.Checked;
            }
            if (_radioHaltTxOnHighSwrCheckBox != null) ctrl.Radio.HaltTxOnHighSwr = _radioHaltTxOnHighSwrCheckBox.Checked;
            if (_radioSwrHaltThresholdUpDown != null) ctrl.Radio.SwrHaltThreshold = (double)_radioSwrHaltThresholdUpDown.Value;
            if (_radioStartupPowerEnabledCheckBox != null) ctrl.Radio.StartupPowerEnabled = _radioStartupPowerEnabledCheckBox.Checked;
            if (_radioStartupPowerWattsUpDown != null) ctrl.Radio.StartupPowerWatts = (int)_radioStartupPowerWattsUpDown.Value;
            if (_radioStartupPowerMaxWattsUpDown != null) ctrl.Radio.StartupPowerMaxWatts = (int)_radioStartupPowerMaxWattsUpDown.Value;
            // Not part of radioSettingsChanged below -- read live on every AudioLevel() call
            // (WsjtxClient.BandAudio.cs), never baked into the engine's own launch args, so no
            // restart is ever needed for this one to take effect.
            if (_radioAudioStepUpDown != null) ctrl.Radio.AudioStepPercent = (int)_radioAudioStepUpDown.Value;
            // Same live-read, no-restart-needed shape as AudioStepPercent just above --
            // WsjtxClient.BandAudio.cs's AudioLevel() and WsjtxClient.Direct.cs's band-change
            // restore both read this directly off ctrl.Radio.
            if (_radioRememberTxLevelPerBandCheckBox != null) ctrl.Radio.RememberTxLevelPerBand = _radioRememberTxLevelPerBandCheckBox.Checked;

            // Normalize case on entry: this call/grid flows straight into jimmy-engine-host's
            // --mycall (see DecodeMessage.IsCallTo's own comment on the 2026-08-07
            // case-sensitivity bug that came from an operator-typed lower-case callsign never
            // matching upper-case decoded wire text). Fixing the comparison to be
            // case-insensitive is the real fix; normalizing here too is defense in depth, and
            // makes the field actually look right either way.
            ctrl.NativeEngine.MyCall = _engineMyCallTextBox.Text.Trim().ToUpperInvariant();
            ctrl.NativeEngine.MyGrid = FormatGridSquare(_engineMyGridTextBox.Text.Trim());
            ctrl.NativeEngine.AudioInputDevice = _engineAudioDeviceCombo.Text.Trim();
            ctrl.NativeEngine.AudioOutputDevice = _engineAudioOutputDeviceCombo.Text.Trim();

            // Only run either call if something it actually depends on changed -- see this
            // method's own opening comment. radioSettingsChanged also covers ApplyEngineMode(),
            // not just ApplyRadioSettings(): Launch() bakes the whole Radio settings object into
            // the engine host's own CLI args, so a Radio-only change still needs a restart to
            // take effect.
            bool engineIdentityChanged =
                wasMyCall != ctrl.NativeEngine.MyCall || wasMyGrid != ctrl.NativeEngine.MyGrid ||
                wasAudioIn != ctrl.NativeEngine.AudioInputDevice || wasAudioOut != ctrl.NativeEngine.AudioOutputDevice;
            bool radioSettingsChanged =
                wasRadioMode != r.Mode || wasRigModel != r.RigModel || wasComPort != r.ComPort ||
                wasBaudRate != r.BaudRate || wasUseExternal != r.UseExternalRigctld || wasHost != r.RigctldHost ||
                wasPort != r.RigctldPort || wasPttEnabled != r.PttEnabled || wasPttMethod != r.PttMethod ||
                wasTxMode != r.TxMode || wasPttDataSource != r.PttDataSource ||
                wasSplitMode != r.SplitMode || wasPttSerialPort != r.PttSerialPort ||
                wasPollIntervalMs != r.PollIntervalMs || wasReadDisplayPwrSwr != r.ReadDisplayPwrSwr ||
                wasHaltTxOnHighSwr != r.HaltTxOnHighSwr || wasSwrHaltThreshold != r.SwrHaltThreshold ||
                wasStartupPowerEnabled != r.StartupPowerEnabled || wasStartupPowerWatts != r.StartupPowerWatts ||
                wasStartupPowerMaxWatts != r.StartupPowerMaxWatts;

            // ApplyEngineMode() first (when applicable): under HamlibRigctld it launches the
            // engine host, which owns and spawns the real rigctld; ApplyRadioSettings() then
            // only ever CONNECTS to that rigctld in this combination, never launches its own,
            // so running it after gives the real rigctld a head start instead of racing its first
            // poll tick against a daemon that doesn't exist yet (found live, 2026-08-06/07).
            if (engineIdentityChanged || radioSettingsChanged)
                ctrl.ApplyEngineMode();
            if (radioSettingsChanged)
                ctrl.ApplyRadioSettings();
        }

        // Options > Decode tab. Only DecodeDepth has a live control-port path (see
        // DirectSetDecodeDepth's own comment) -- the other four are baked into the engine host's
        // CLI args at launch, same as Radio tab settings, so changing any of THEM needs a full
        // ApplyEngineMode() restart. If a restart is already happening (because one of those
        // four changed), it picks up a simultaneous depth change for free -- no need to also
        // send the live command in that case.
        private void SaveDecodeTab()
        {
            if (_decodeDepthCombo == null) return;

            var d = ctrl.Decode;
            int wasDecodeDepth = d.DecodeDepth;
            int wasFLow = d.DecodeFLowHz;
            int wasFHigh = d.DecodeFHighHz;
            bool wasApDecode = d.ApDecode;
            bool wasApCqOnly = d.ApCqOnly;
            bool wasSingleDecode = d.SingleDecode;

            d.DecodeDepth = _decodeDepthCombo.SelectedIndex + 1;
            d.DecodeFLowHz = (int)_decodeFLowUpDown.Value;
            d.DecodeFHighHz = (int)_decodeFHighUpDown.Value;
            d.ApDecode = _decodeApDecodeCheckBox.Checked;
            d.ApCqOnly = _decodeApCqOnlyCheckBox.Checked;
            d.SingleDecode = _decodeSingleDecodeCheckBox.Checked;

            bool restartNeededChanged =
                wasFLow != d.DecodeFLowHz || wasFHigh != d.DecodeFHighHz ||
                wasApDecode != d.ApDecode || wasApCqOnly != d.ApCqOnly ||
                wasSingleDecode != d.SingleDecode;
            bool depthChanged = wasDecodeDepth != d.DecodeDepth;

            if (restartNeededChanged)
                ctrl.ApplyEngineMode();
            else if (depthChanged)
                ctrl.wsjtxClient?.DirectSetDecodeDepth(d.DecodeDepth);
        }

        // ===== SOUNDS TAB =====

        private void BuildSoundsTab()
        {
            soundsPanel.Controls.Clear();
            _soundRows = new List<SoundRow>();

            var font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F);

            // Global sounds enabled checkbox
            int tabIdx = 0;
            _soundsEnabledCb = new System.Windows.Forms.CheckBox
            {
                Text           = "Sounds enabled",
                Checked        = ctrl.soundsEnabled,
                Location       = new System.Drawing.Point(8, 6),
                AutoSize       = true,
                TabIndex       = tabIdx++,
                TabStop        = true,
                AccessibleName = "All Jimmy sounds enabled",
                Font           = font,
            };
            soundsPanel.Controls.Add(_soundsEnabledCb);

            // Instruction label
            var instrBox = new System.Windows.Forms.TextBox
            {
                ReadOnly       = true,
                Multiline      = true,
                BorderStyle    = System.Windows.Forms.BorderStyle.None,
                BackColor      = soundsPanel.BackColor,
                ForeColor      = System.Drawing.SystemColors.ControlText,
                Location       = new System.Drawing.Point(8, 28),
                Size           = new System.Drawing.Size(648, 32),
                Text           = "Enable or disable each sound event and choose a WAV file. Leave the path empty to disable a sound.",
                TabStop        = false,
                AccessibleName = "Sounds tab instructions",
                Font           = font,
            };
            soundsPanel.Controls.Add(instrBox);

            // Column headers
            var hdrEnabled = new System.Windows.Forms.Label { Text = "On",      AutoSize = true, Location = new System.Drawing.Point(8,   66), Font = font, TabStop = false };
            var hdrEvent   = new System.Windows.Forms.Label { Text = "Event",   AutoSize = true, Location = new System.Drawing.Point(32,  66), Font = font, TabStop = false };
            var hdrFile    = new System.Windows.Forms.Label { Text = "WAV file path (empty = no sound)", AutoSize = true, Location = new System.Drawing.Point(190, 66), Font = font, TabStop = false };
            soundsPanel.Controls.Add(hdrEnabled);
            soundsPanel.Controls.Add(hdrEvent);
            soundsPanel.Controls.Add(hdrFile);

            var eventDefs = new[]
            {
                new { Key = "CallAdded",      Label = "Call added",          Enabled = ctrl.callAddedCheckBox.Checked, File = ctrl.soundFile_CallAdded,   EnabledEditable = true  },
                new { Key = "CallingMe",      Label = "Calling me",          Enabled = ctrl.mycallCheckBox.Checked,    File = ctrl.soundFile_CallingMe,   EnabledEditable = true  },
                new { Key = "Logged",         Label = "Logged",              Enabled = ctrl.loggedCheckBox.Checked,    File = ctrl.soundFile_Logged,      EnabledEditable = true  },
                new { Key = "TxEnabled",      Label = "TX enabled",          Enabled = ctrl.soundEnabled_TxEnabled,    File = ctrl.soundFile_TxEnabled,   EnabledEditable = true  },
                new { Key = "Disconnected",   Label = "WSJT-X disconnected", Enabled = ctrl.soundEnabled_Disconnected, File = ctrl.soundFile_Disconnected,EnabledEditable = true  },
                new { Key = "NewDxcc",        Label = "New DXCC",            Enabled = ctrl.soundEnabled_NewDxcc,      File = ctrl.soundFile_NewDxcc,     EnabledEditable = true  },
                new { Key = "NewDxccOnBand",  Label = "New DXCC on band",    Enabled = ctrl.soundEnabled_NewDxccOnBand,File = ctrl.soundFile_NewDxccOnBand,EnabledEditable = true },
                new { Key = "AlwaysWanted",   Label = "Always Wanted",       Enabled = ctrl.soundEnabled_AlwaysWanted, File = ctrl.soundFile_AlwaysWanted, EnabledEditable = true },
                new { Key = "DirectedCq",     Label = "Directed CQ",         Enabled = ctrl.soundEnabled_DirectedCq,   File = ctrl.soundFile_DirectedCq,   EnabledEditable = true },
                new { Key = "Pota",           Label = "POTA",                Enabled = ctrl.soundEnabled_Pota,         File = ctrl.soundFile_Pota,         EnabledEditable = true },
                new { Key = "Sota",           Label = "SOTA",                            Enabled = ctrl.soundEnabled_Sota,           File = ctrl.soundFile_Sota,           EnabledEditable = true },
                new { Key = "WantedAnywhere", Label = "Wanted call heard anywhere",       Enabled = ctrl.soundEnabled_WantedAnywhere, File = ctrl.soundFile_WantedAnywhere, EnabledEditable = true },
                new { Key = "OppositePeriod", Label = "Interesting call opposite period", Enabled = ctrl.soundEnabled_OppositePeriod, File = ctrl.soundFile_OppositePeriod, EnabledEditable = true },
                new { Key = "AwardNeeded",    Label = "Award needed (Still Need tab)",    Enabled = ctrl.soundEnabled_AwardNeeded,    File = ctrl.soundFile_AwardNeeded,    EnabledEditable = true },
            };

            int y = 84;

            foreach (var ev in eventDefs)
            {
                var row = new SoundRow { Key = ev.Key };

                var enabledCb = new System.Windows.Forms.CheckBox
                {
                    Checked         = ev.Enabled,
                    Location        = new System.Drawing.Point(8, y),
                    Size            = new System.Drawing.Size(20, 17),
                    TabIndex        = tabIdx++,
                    TabStop         = ev.EnabledEditable,
                    Enabled         = ev.EnabledEditable,
                    AccessibleName  = ev.Label + " sound enabled",
                    Font            = font,
                };
                soundsPanel.Controls.Add(enabledCb);
                row.EnabledCb = enabledCb;

                var evLabel = new System.Windows.Forms.Label
                {
                    Text     = ev.Label,
                    Location = new System.Drawing.Point(32, y + 1),
                    Size     = new System.Drawing.Size(155, 17),
                    Font     = font,
                    TabStop  = false,
                };
                soundsPanel.Controls.Add(evLabel);

                var fileTb = new System.Windows.Forms.TextBox
                {
                    Text            = ev.File ?? "",
                    Location        = new System.Drawing.Point(190, y - 1),
                    Size            = new System.Drawing.Size(295, 20),
                    TabIndex        = tabIdx++,
                    AccessibleName  = ev.Label + " sound file path",
                    Font            = font,
                };
                soundsPanel.Controls.Add(fileTb);
                row.FileTb = fileTb;

                string capturedLabel = ev.Label;
                System.Windows.Forms.TextBox capturedTb = fileTb;

                var browseBtn = new System.Windows.Forms.Button
                {
                    Text            = "Browse",
                    Location        = new System.Drawing.Point(490, y - 1),
                    Size            = new System.Drawing.Size(60, 22),
                    TabIndex        = tabIdx++,
                    AccessibleName  = "Browse " + ev.Label + " sound file",
                    Font            = font,
                };
                browseBtn.Click += (s, e) => BrowseSoundFile(capturedLabel, capturedTb);
                soundsPanel.Controls.Add(browseBtn);

                var testBtn = new System.Windows.Forms.Button
                {
                    Text            = "Test",
                    Location        = new System.Drawing.Point(555, y - 1),
                    Size            = new System.Drawing.Size(48, 22),
                    TabIndex        = tabIdx++,
                    AccessibleName  = "Test " + ev.Label + " sound",
                    Font            = font,
                };
                testBtn.Click += (s, e) => TestSoundFile(capturedTb.Text);
                soundsPanel.Controls.Add(testBtn);

                _soundRows.Add(row);
                y += 26;
            }
        }

        private void BrowseSoundFile(string eventLabel, System.Windows.Forms.TextBox fileTb)
        {
            using (var dlg = new System.Windows.Forms.OpenFileDialog())
            {
                dlg.Title = "Select sound file for: " + eventLabel;
                dlg.Filter = "WAV files (*.wav)|*.wav|All files (*.*)|*.*";
                dlg.FilterIndex = 1;
                dlg.CheckFileExists = true;
                string current = fileTb.Text ?? "";
                if (!string.IsNullOrEmpty(current))
                {
                    try
                    {
                        if (System.IO.File.Exists(current))
                            dlg.InitialDirectory = System.IO.Path.GetDirectoryName(current);
                    }
                    catch { }
                }
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    fileTb.Text = dlg.FileName;
            }
        }

        private void TestSoundFile(string filePath)
        {
            if (!string.IsNullOrEmpty(filePath))
                wsjtxClient.Sounds.TestPlaySound(filePath);
        }

        private void SaveSoundsTab()
        {
            if (_soundRows == null) return;
            if (_soundsEnabledCb != null) ctrl.soundsEnabled = _soundsEnabledCb.Checked;
            foreach (var row in _soundRows)
            {
                bool enabled = row.EnabledCb?.Checked ?? false;
                string file  = row.FileTb?.Text ?? "";
                switch (row.Key)
                {
                    case "CallAdded":
                        ctrl.callAddedCheckBox.Checked = enabled;
                        ctrl.soundFile_CallAdded = file;
                        break;
                    case "CallingMe":
                        ctrl.mycallCheckBox.Checked = enabled;
                        ctrl.soundFile_CallingMe = file;
                        break;
                    case "Logged":
                        ctrl.loggedCheckBox.Checked = enabled;
                        ctrl.soundFile_Logged = file;
                        break;
                    case "TxEnabled":
                        ctrl.soundEnabled_TxEnabled = enabled;
                        ctrl.soundFile_TxEnabled = file;
                        break;
                    case "Disconnected":
                        ctrl.soundEnabled_Disconnected = enabled;
                        ctrl.soundFile_Disconnected = file;
                        break;
                    case "NewDxcc":
                        ctrl.soundEnabled_NewDxcc = enabled;
                        ctrl.soundFile_NewDxcc = file;
                        break;
                    case "NewDxccOnBand":
                        ctrl.soundEnabled_NewDxccOnBand = enabled;
                        ctrl.soundFile_NewDxccOnBand = file;
                        break;
                    case "AlwaysWanted":
                        ctrl.soundEnabled_AlwaysWanted = enabled;
                        ctrl.soundFile_AlwaysWanted = file;
                        break;
                    case "DirectedCq":
                        ctrl.soundEnabled_DirectedCq = enabled;
                        ctrl.soundFile_DirectedCq = file;
                        break;
                    case "Pota":
                        ctrl.soundEnabled_Pota = enabled;
                        ctrl.soundFile_Pota = file;
                        break;
                    case "Sota":
                        ctrl.soundEnabled_Sota = enabled;
                        ctrl.soundFile_Sota = file;
                        break;
                    case "WantedAnywhere":
                        ctrl.soundEnabled_WantedAnywhere = enabled;
                        ctrl.soundFile_WantedAnywhere = file;
                        break;
                    case "OppositePeriod":
                        ctrl.soundEnabled_OppositePeriod = enabled;
                        ctrl.soundFile_OppositePeriod = file;
                        break;
                    case "AwardNeeded":
                        ctrl.soundEnabled_AwardNeeded = enabled;
                        ctrl.soundFile_AwardNeeded = file;
                        break;
                }
            }
        }

        // ===== REPARENTING =====

        private void ReparentControlsToDialog()
        {
            // Calling / CQ Mode section (what kind of CQ, and its directed-CQ text) moved to
            // the Call CQ dialog (main screen button) -- no longer reparented into Options.
            ReparentTo(ctrl.ignoreNonDxCheckBox,  rcvCallingGroupBox, new Point(10, 18));
            ReparentTo(ctrl.IgnoreNonDxHelpLabel, rcvCallingGroupBox, new Point(350, 21));

            // Replying section (DX/Local + band/message filter) → Receive / Auto Reply tab
            ReparentTo(ctrl.replyNormCqLabel,   rcvReplyingGroupBox, new Point(8, 22));
            ReparentTo(ctrl.replyDxCheckBox,    rcvReplyingGroupBox, new Point(185, 20));
            ReparentTo(ctrl.replyLocalCheckBox, rcvReplyingGroupBox, new Point(240, 20));
            ReparentTo(ctrl.bandComboBox,        rcvReplyingGroupBox, new Point(112, 43));
            ReparentTo(ctrl.forLabel,            rcvReplyingGroupBox, new Point(190, 46));
            ReparentTo(ctrl.ExcludeHelpLabel,    rcvReplyingGroupBox, new Point(215, 46));
            ReparentTo(ctrl.includeLabel,        rcvReplyingGroupBox, new Point(8, 70));
            ReparentTo(ctrl.cqOnlyRadioButton,   rcvReplyingGroupBox, new Point(100, 68));
            ReparentTo(ctrl.cqGridRadioButton,   rcvReplyingGroupBox, new Point(162, 68));
            ReparentTo(ctrl.anyMsgRadioButton,   rcvReplyingGroupBox, new Point(232, 68));
            ReparentTo(ctrl.IncludeHelpLabel,    rcvReplyingGroupBox, new Point(282, 70));

            // Directed CQ Alert → Receive / Auto Reply tab
            ReparentTo(ctrl.replyDirCqCheckBox,     rcvDirectedCqGroupBox, new Point(10, 18));
            ReparentTo(ctrl.alertTextBox,           rcvDirectedCqGroupBox, new Point(180, 16));
            ReparentTo(ctrl.AlertDirectedHelpLabel, rcvDirectedCqGroupBox, new Point(300, 19));

            // Reply Behavior → Receive / Auto Reply tab
            ReparentTo(ctrl.replyRR73CheckBox,  rcvReplyBehaviorGroupBox, new Point(10, 18));
            ReparentTo(ctrl.ReplyRR73HelpLabel, rcvReplyBehaviorGroupBox, new Point(200, 20));

            // Block List → Receive / Auto Reply tab
            ReparentTo(ctrl.exceptLabel,   rcvBlockListGroupBox, new Point(10, 20));
            ReparentTo(ctrl.exceptTextBox, rcvBlockListGroupBox, new Point(110, 17));
            ReparentTo(ctrl.blockHelpLabel, rcvBlockListGroupBox, new Point(275, 20));

            // Weak-signal floor → same group, to the right of the block list
            ReparentTo(ctrl.ignoreWeakSnrCheckBox, rcvBlockListGroupBox, new Point(320, 19));
            ReparentTo(ctrl.minSnrNumUpDown,        rcvBlockListGroupBox, new Point(478, 17));
            ReparentTo(ctrl.minSnrLabel,            rcvBlockListGroupBox, new Point(530, 20));
            ReparentTo(ctrl.removeOnWeakSnrCheckBox, rcvBlockListGroupBox, new Point(10, 42));

            // Transmit group → Transmit tab
            ReparentTo(ctrl.freqCheckBox,       rcvTransmitGroupBox, new Point(10, 18));
            ReparentTo(ctrl.AutoFreqHelpLabel,  rcvTransmitGroupBox, new Point(150, 20));
            ReparentTo(ctrl.skipGridCheckBox,   rcvTransmitGroupBox, new Point(10, 40));
            ReparentTo(ctrl.useRR73CheckBox,    rcvTransmitGroupBox, new Point(110, 40));
            ReparentTo(ctrl.logEarlyCheckBox,   rcvTransmitGroupBox, new Point(10, 62));
            ReparentTo(ctrl.LogEarlyHelpLabel,  rcvTransmitGroupBox, new Point(140, 64));
            ReparentTo(ctrl.optimizeCheckBox,   rcvTransmitGroupBox, new Point(10, 84));
            ReparentTo(ctrl.holdCheckBox,       rcvTransmitGroupBox, new Point(90, 84));
            ReparentTo(ctrl.limitLabel,         rcvTransmitGroupBox, new Point(10, 108));
            ReparentTo(ctrl.timeoutNumUpDown,   rcvTransmitGroupBox, new Point(57, 105));
            ctrl.timeoutNumUpDown.TabStop = true;
            ReparentTo(ctrl.repeatLabel,        rcvTransmitGroupBox, new Point(95, 108));
            ReparentTo(ctrl.LimitTxHelpLabel,   rcvTransmitGroupBox, new Point(240, 108));
            ReparentTo(ctrl.periodLabel,        rcvTransmitGroupBox, new Point(10, 132));
            ReparentTo(ctrl.periodComboBox,     rcvTransmitGroupBox, new Point(67, 129));
            ReparentTo(ctrl.PeriodHelpLabel,    rcvTransmitGroupBox, new Point(127, 132));

            // General tab
            ReparentTo(ctrl.showUsStateCheckBox, generalPanel, new Point(10, 61));
        }

        private void ReparentTo(Control c, Control newParent, Point newLocation)
        {
            originalParents[c] = c.Parent;
            originalLocations[c] = c.Location;
            c.Parent?.Controls.Remove(c);
            newParent.Controls.Add(c);
            c.Location = newLocation;
            c.Visible = true;
            reparentedControls.Add(c);
        }

        private void ReparentControlsBack()
        {
            foreach (Control c in reparentedControls)
            {
                c.Parent?.Controls.Remove(c);
                if (originalParents.TryGetValue(c, out Control origParent) && origParent != null)
                {
                    origParent.Controls.Add(c);
                    if (originalLocations.TryGetValue(c, out Point origLoc))
                        c.Location = origLoc;
                    c.Visible = false;
                }
            }
            reparentedControls.Clear();
            originalParents.Clear();
            originalLocations.Clear();
        }

        // ===== BASIC TAB WIZARD LOGIC (ported from Guide.cs) =====

        private void UpdateAllButtons()
        {
            foreach (CheckBox b in disableList)
                b.Enabled = !dxccButtonEnabled;

            SetState(listenButton,  wsjtxClient.txMode == WsjtxClient.TxModes.LISTEN && ctrl.periodComboBox.SelectedIndex == (int)WsjtxClient.ListenModeTxPeriods.ANY, true);
            SetState(callCqButton,  wsjtxClient.txMode == WsjtxClient.TxModes.CALL_CQ, true);

            SetState(cqButton,    (cqButtonEnabled    = ctrl.callNonDirCqCheckBox.Checked && !ctrl.callDirCqCheckBox.Checked), true);
            SetState(cqDxButton,  (cqDxButtonEnabled  = ctrl.callCqDxCheckBox.Checked    && !ctrl.callDirCqCheckBox.Checked), true);

            SetState(dxButton,    (dxButtonEnabled    = ctrl.replyDxCheckBox.Checked), true);
            SetState(nonDxButton, (nonDxButtonEnabled = ctrl.replyLocalCheckBox.Checked), true);

            SetState(potaButton, (activatorEnabled =
                wsjtxClient.txMode == WsjtxClient.TxModes.CALL_CQ &&
                ctrl.directedTextBox.Text == "POTA" &&
                ctrl.callDirCqCheckBox.Checked &&
                !ctrl.callCqDxCheckBox.Checked &&
                !ctrl.callNonDirCqCheckBox.Checked), true);

            SetState(hunterButton, (hunterEnabled =
                wsjtxClient.txMode == WsjtxClient.TxModes.LISTEN &&
                ctrl.alertTextBox.Text.Contains("POTA") &&
                ctrl.replyDirCqCheckBox.Checked), true);

            SetState(allButton,    (wsjtxClient.Ranker.rankOrderList.Count > 0 && wsjtxClient.Ranker.rankOrderList[0] == WsjtxClient.RankMethods.CALL_ORDER), true);
            SetState(recentButton, (wsjtxClient.Ranker.rankOrderList.Count > 0 && wsjtxClient.Ranker.rankOrderList[0] == WsjtxClient.RankMethods.MOST_RECENT), true);

            if (callCqButton.Checked)
                label9.Text = "You're now ready to start. Press OK to close this Options dialog, then enable CQ mode using Ctrl, E.";
            else
                label9.Text = "You're now ready to start. Press OK to close this Options dialog, and Listen mode is enabled.";
        }

        private void callCqButton_Click(object sender, EventArgs e)
        {
            ctrl.GuideCqMode();
            UpdateAllButtons();
        }

        private void listenButton_Click(object sender, EventArgs e)
        {
            ctrl.GuideListenMode();
            if (wsjtxClient.txMode == WsjtxClient.TxModes.LISTEN)
                ctrl.periodComboBox.SelectedIndex = (int)WsjtxClient.ListenModeTxPeriods.ANY;
            UpdateAllButtons();
        }

        private void cqButton_Click(object sender, EventArgs e)
        {
            UpdateAllButtons();
            if (cqButtonEnabled)
                ctrl.callNonDirCqCheckBox.Checked = false;
            else
            {
                ctrl.callNonDirCqCheckBox.Checked = true;
                ctrl.callDirCqCheckBox.Checked = false;
            }
            UpdateAllButtons();
        }

        private void cqDxButton_Click(object sender, EventArgs e)
        {
            UpdateAllButtons();
            if (cqDxButtonEnabled)
                ctrl.callCqDxCheckBox.Checked = false;
            else
            {
                ctrl.callCqDxCheckBox.Checked = true;
                ctrl.callDirCqCheckBox.Checked = false;
                ctrl.periodComboBox.SelectedIndex = (int)WsjtxClient.ListenModeTxPeriods.ANY;
            }
            UpdateAllButtons();
        }

        private void dxButton_Click(object sender, EventArgs e)
        {
            UpdateAllButtons();
            ctrl.ToggleDx();
            UpdateAllButtons();
        }

        private void nonDxButton_Click(object sender, EventArgs e)
        {
            UpdateAllButtons();
            ctrl.ToggleLocal();
            UpdateAllButtons();
        }

        private void potaButton_Click(object sender, EventArgs e)
        {
            UpdateAllButtons();
            if (!activatorEnabled && hunterEnabled) ctrl.ToggleHunter();
            ctrl.ToggleActivator();
            ctrl.cqModeButton_Click(null, null);
            UpdateAllButtons();
        }

        private void hunterButton_Click(object sender, EventArgs e)
        {
            UpdateAllButtons();
            if (!hunterEnabled && activatorEnabled) ctrl.ToggleActivator();
            ctrl.ToggleHunter();
            ctrl.listenModeButton_Click(null, null);
            ctrl.periodComboBox.SelectedIndex = (int)WsjtxClient.ListenModeTxPeriods.ANY;
            UpdateAllButtons();
        }

        private void allButton_Click(object sender, EventArgs e)
        {
            UpdateAllButtons();
            if (!(wsjtxClient.Ranker.rankOrderList.Count > 0 && wsjtxClient.Ranker.rankOrderList[0] == WsjtxClient.RankMethods.CALL_ORDER) || ctrl.timeoutNumUpDown.Value != 3)
                wsjtxClient.ApplySortOrder(new List<WsjtxClient.RankMethods> { WsjtxClient.RankMethods.CALL_ORDER }, null);
            UpdateAllButtons();
        }

        private void recentButton_Click(object sender, EventArgs e)
        {
            UpdateAllButtons();
            if (!(wsjtxClient.Ranker.rankOrderList.Count > 0 && wsjtxClient.Ranker.rankOrderList[0] == WsjtxClient.RankMethods.MOST_RECENT) || ctrl.timeoutNumUpDown.Value != 1)
                wsjtxClient.ApplySortOrder(new List<WsjtxClient.RankMethods> { WsjtxClient.RankMethods.MOST_RECENT }, null);
            UpdateAllButtons();
        }

        private void SetState(CheckBox button, bool selected, bool enabled)
        {
            if (selected) HighLight(button, enabled);
            else Normal(button, enabled);
        }

        private void HighLight(CheckBox button, bool enabled)
        {
            button.ForeColor = highlightFore;
            button.BackColor = enabled ? highlightBack : highlightBackDisabled;
            button.Checked = true;
        }

        private void Normal(CheckBox button, bool enabled)
        {
            button.ForeColor = normalFore;
            button.BackColor = normalBack;
            button.Checked = false;
        }

        // ===== TEXTBOX FOCUS HANDLERS (suppress cursor on read-only labels) =====

        private void subtitleLabel_Enter(object sender, EventArgs e)
        { subtitleLabel.SelectionStart = 0; subtitleLabel.SelectionLength = 0; }

        private void modeLabel_Enter(object sender, EventArgs e)
        { modeLabel.SelectionStart = 0; modeLabel.SelectionLength = 0; }

        private void label12_Enter(object sender, EventArgs e)
        { label12.SelectionStart = 0; label12.SelectionLength = 0; }

        private void label2_Enter(object sender, EventArgs e)
        { label2.SelectionStart = 0; label2.SelectionLength = 0; }

        private void label4_Enter(object sender, EventArgs e)
        { label4.SelectionStart = 0; label4.SelectionLength = 0; }

        private void label5_Enter(object sender, EventArgs e)
        { label5.SelectionStart = 0; label5.SelectionLength = 0; }

        private void label9_Enter(object sender, EventArgs e)
        { label9.SelectionStart = 0; label9.SelectionLength = 0; }

        // ===== HOTKEYS TAB =====

        private bool IsCaptureFieldFocused()
            => _sharedCaptureBox != null && _sharedCaptureBox.Focused;

        private void BuildHotkeysTab()
        {
            hotkeysPanel.Controls.Clear();
            _listActionMap = new List<HotkeyAction?>();
            _pendingKeys   = new Dictionary<HotkeyAction, Keys>();

            // Initialise pending keys from the live config
            foreach (HotkeyAction action in Enum.GetValues(typeof(HotkeyAction)))
                _pendingKeys[action] = ctrl.hotkeyConfig[action];

            // Instruction text (no tab stop)
            var instrBox = new TextBox
            {
                ReadOnly       = true,
                Multiline      = true,
                BorderStyle    = BorderStyle.None,
                BackColor      = hotkeysPanel.BackColor,
                ForeColor      = SystemColors.ControlText,
                Location       = new Point(8, 8),
                Size           = new Size(640, 34),
                Text           = "Choose an action, then tab to the shortcut field and press the new shortcut.",
                TabStop        = false,
                AccessibleName = "Hotkeys usage instructions",
            };
            hotkeysPanel.Controls.Add(instrBox);

            // Actions list box — Tab stop 0
            _actionListBox = new ListBox
            {
                Location       = new Point(8, 50),
                Size           = new Size(330, 262),
                TabIndex       = 0,
                AccessibleName = "Hotkey actions",
                Name           = "hkActionListBox",
            };
            BuildActionList();
            _actionListBox.SelectedIndexChanged += ActionListBox_SelectedIndexChanged;
            _actionListBox.KeyPress += ActionListBox_KeyPress;
            hotkeysPanel.Controls.Add(_actionListBox);

            // "Current shortcut:" static label (no tab stop)
            hotkeysPanel.Controls.Add(new Label
            {
                Text     = "Current shortcut:",
                Location = new Point(356, 50),
                Size     = new Size(140, 18),
                TabStop  = false,
            });

            // Shared capture box — Tab stop 1
            _sharedCaptureBox = new HotkeyCaptureBox
            {
                Location       = new Point(356, 70),
                Size           = new Size(240, 22),
                TabIndex       = 1,
                AccessibleName = "Shortcut key",
                Name           = "hkSharedCaptureBox",
            };
            _sharedCaptureBox.KeyCaptured += (s, ev) =>
                OnKeyCaptured(GetSelectedAction(), (HotkeyCaptureBox)s, ev.Keys);
            hotkeysPanel.Controls.Add(_sharedCaptureBox);

            // Reset All to Defaults button — Tab stop 2
            var resetBtn = new Button
            {
                Text     = "Reset All to Defaults",
                Location = new Point(356, 104),
                Size     = new Size(160, 27),
                TabIndex = 2,
                Name     = "hkResetButton",
            };
            resetBtn.Click += ResetHotkeys_Click;
            hotkeysPanel.Controls.Add(resetBtn);

            // Every list item is now a real, selectable action -- no header row to skip.
            _actionListBox.SelectedIndex = 0;
        }

        private void BuildActionList()
        {
            var generalActions = new HotkeyAction[]
            {
                HotkeyAction.Options,
                HotkeyAction.Help,
                HotkeyAction.UpdateCheck,
                HotkeyAction.CallCqMode,
                HotkeyAction.ListenMode,
                HotkeyAction.EnableTx,
                HotkeyAction.HaltTx,
                HotkeyAction.NextCall,
                HotkeyAction.ManualCall,
                HotkeyAction.DeleteAllCalls,
                HotkeyAction.TxPeriod,
                HotkeyAction.HoldTimeout,
                HotkeyAction.TuneMode,
                HotkeyAction.AudioUp,
                HotkeyAction.AudioDown,
                HotkeyAction.PowerSwr,
                HotkeyAction.BandUp,
                HotkeyAction.BandDown,
                HotkeyAction.ToggleMode,
                HotkeyAction.PSKReporter,
                HotkeyAction.Prompts,
                HotkeyAction.UploadLotw,
                HotkeyAction.SortOrder,
                HotkeyAction.RowOrder,
                HotkeyAction.AnalyzeSlot,
                HotkeyAction.LookupStation,
                HotkeyAction.OpenLogbook,
                HotkeyAction.AddManualQso,
                HotkeyAction.OpenOtaSpots,
                HotkeyAction.ResetWindowSize,
            };

            var navActions = new HotkeyAction[]
            {
                HotkeyAction.NavStatus,
                HotkeyAction.NavCallList,
                HotkeyAction.NavPendingCount,
                HotkeyAction.NavLoggedList,
                HotkeyAction.NavLoggedCount,
                HotkeyAction.NavAdvTx1,
                HotkeyAction.NavAdvTx2,
                HotkeyAction.NavAdvRaw,
                HotkeyAction.NavSpotWatch,
            };

            // Group header folded into the first real item's own text (e.g. "General
            // Commands: Options") instead of a separate list row -- a standalone header
            // row occupied a real ListBox item slot, which pushed every action's
            // 1-based JAWS/NVDA "N of 52" position one higher than its actual rank
            // among selectable actions (reported live, 2026-08-09: "Options" read as
            // "2 of 52" instead of "1 of ..."). Folding it in gives screen-reader users
            // the same grouping context, spoken as part of the item itself, with an
            // accurate position count -- and removes the need to skip a
            // never-selectable header row during arrow navigation.
            for (int i = 0; i < generalActions.Length; i++)
            {
                var a = generalActions[i];
                string text = i == 0
                    ? "General Commands: " + HotkeyConfig.DisplayNames[a]
                    : "  " + HotkeyConfig.DisplayNames[a];
                _actionListBox.Items.Add(text);
                _listActionMap.Add(a);
            }

            for (int i = 0; i < navActions.Length; i++)
            {
                var a = navActions[i];
                string text = i == 0
                    ? "Accessibility Navigation: " + HotkeyConfig.DisplayNames[a]
                    : "  " + HotkeyConfig.DisplayNames[a];
                _actionListBox.Items.Add(text);
                _listActionMap.Add(a);
            }

        }

        private void ActionListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            int idx = _actionListBox?.SelectedIndex ?? -1;
            if (idx < 0 || idx >= _listActionMap.Count) return;

            // Group header selected — skip to the next real action in the direction
            // the selection came from, so Up-arrow at the top of a group moves up
            // into the previous group instead of bouncing back to where it started.
            if (_listActionMap[idx] == null)
            {
                bool movingUp = idx < _lastRealActionIndex;
                if (movingUp)
                {
                    for (int prev = idx - 1; prev >= 0; prev--)
                    {
                        if (_listActionMap[prev] != null)
                        {
                            _actionListBox.SelectedIndex = prev;
                            return;
                        }
                    }
                }

                for (int next = idx + 1; next < _listActionMap.Count; next++)
                {
                    if (_listActionMap[next] != null)
                    {
                        _actionListBox.SelectedIndex = next;
                        return;
                    }
                }
                return;
            }

            _lastRealActionIndex = idx;
            HotkeyAction action  = _listActionMap[idx].Value;
            string       name    = HotkeyConfig.DisplayNames[action];

            if (_sharedCaptureBox != null)
            {
                _sharedCaptureBox.AccessibleName = name + " shortcut key";
                _sharedCaptureBox.SetValue(_pendingKeys[action]);
            }
        }

        // Windows' native listbox jump-to-letter matches on the item's literal first
        // character, which is always a space here (used for visual indent) — so it
        // never matches. Do the prefix match ourselves against the display name instead.
        private void ActionListBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            char ch = char.ToUpperInvariant(e.KeyChar);
            if (!char.IsLetterOrDigit(ch)) return;
            e.Handled = true;

            int count = _listActionMap.Count;
            if (count == 0) return;
            int start = _actionListBox.SelectedIndex;

            for (int step = 1; step <= count; step++)
            {
                int idx = (start + step) % count;
                var action = _listActionMap[idx];
                if (action == null) continue;
                string name = HotkeyConfig.DisplayNames[action.Value];
                if (name.Length > 0 && char.ToUpperInvariant(name[0]) == ch)
                {
                    _actionListBox.SelectedIndex = idx;
                    return;
                }
            }
        }

        private HotkeyAction? GetSelectedAction()
        {
            int idx = _actionListBox?.SelectedIndex ?? -1;
            if (idx < 0 || idx >= _listActionMap.Count) return null;
            return _listActionMap[idx];
        }

        private void OnKeyCaptured(HotkeyAction? action, HotkeyCaptureBox box, Keys keys)
        {
            if (action == null) return;

            string keyStr = HotkeyConfig.FormatKeys(keys);

            if (HotkeyConfig.IsReserved(keys))
            {
                MessageBox.Show(
                    $"{keyStr} is a reserved system shortcut and cannot be assigned.",
                    ctrl.friendlyName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!HotkeyConfig.IsValid(keys))
            {
                MessageBox.Show(
                    $"{keyStr} is not a valid shortcut.\r\n\r\n" +
                    "Use a combination with Alt or Ctrl, or a function key (F1-F24).\r\n" +
                    "Bare letters, numbers, and navigation keys are not allowed.",
                    ctrl.friendlyName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Check for conflicts among all pending assignments
            foreach (var kv in _pendingKeys)
            {
                if (kv.Key == action.Value) continue;
                if (kv.Value == keys)
                {
                    string conflictName = HotkeyConfig.DisplayNames[kv.Key];
                    MessageBox.Show(
                        $"{keyStr} is already assigned to {conflictName}.",
                        ctrl.friendlyName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            _pendingKeys[action.Value] = keys;
            box.SetValue(keys);
        }

        private bool ValidateHotkeys()
        {
            if (_pendingKeys == null) return true;

            var seen = new HashSet<Keys>();
            foreach (var kv in _pendingKeys)
            {
                Keys k = kv.Value;
                if (k == Keys.None)
                {
                    if (HotkeyConfig.OptionalActions.Contains(kv.Key)) continue;
                    string name = HotkeyConfig.DisplayNames[kv.Key];
                    MessageBox.Show(
                        $"Shortcut for '{name}' is not set.",
                        ctrl.friendlyName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _categoryListBox.SelectedIndex = HotkeysCategoryIndex;
                    return false;
                }
                if (seen.Contains(k))
                {
                    MessageBox.Show(
                        "Duplicate shortcut detected. Please correct the Hotkeys settings.",
                        ctrl.friendlyName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _categoryListBox.SelectedIndex = HotkeysCategoryIndex;
                    return false;
                }
                seen.Add(k);
            }
            return true;
        }

        private void SaveHotkeysTab()
        {
            if (_pendingKeys == null || ctrl.hotkeyConfig == null) return;
            foreach (var kv in _pendingKeys)
                ctrl.hotkeyConfig.Apply(kv.Key, kv.Value);
            ctrl.SaveHotkeyConfig();
        }

        private void ResetHotkeys_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show(
                "Reset all shortcuts to their default values?",
                ctrl.friendlyName,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            foreach (HotkeyAction action in Enum.GetValues(typeof(HotkeyAction)))
            {
                if (HotkeyConfig.Defaults.TryGetValue(action, out Keys def))
                    _pendingKeys[action] = def;
            }

            // Refresh the capture box for the currently selected action
            ActionListBox_SelectedIndexChanged(null, null);
        }

        // ===== LOOKUP / DATA TAB =====

        // Two tabs share one "pick a service from a list, its settings show in a detail
        // panel beside it" mechanism -- see WireServiceList. Logbook Sync (this method)
        // covers the three services' own-account credentials and QSO upload/download;
        // Lookup Data (below) covers reference/enrichment data most of which needs no
        // personal account at all. Split decided with the user 2026-07-13: by *purpose*
        // (my logbook vs. helping Jimmy operate), not by brand -- QRZ legitimately has
        // an entry in both, since it genuinely does both jobs.
        private void BuildLogbookSyncTab()
        {
            logbookSyncPanel.Controls.Clear();
            var font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F);
            int pw = 630;   // detail panel usable width -- matches the original single-column
                             // width exactly, so no description label needed re-wrapping.

            string uploadLotwKeyText = ctrl.hotkeyConfig != null
                ? HotkeyConfig.FormatKeys(ctrl.hotkeyConfig[HotkeyAction.UploadLotw])
                : "";
            if (string.IsNullOrEmpty(uploadLotwKeyText)) uploadLotwKeyText = "(unassigned hotkey)";

            var serviceList = new System.Windows.Forms.ListBox
            {
                Location       = new System.Drawing.Point(5, 5),
                Size           = new System.Drawing.Size(160, 340),
                Font           = font,
                TabIndex       = 0,
                AccessibleName = "Logbook service list",
            };
            logbookSyncPanel.Controls.Add(serviceList);

            var panels = new List<System.Windows.Forms.GroupBox>();
            int tabIdx;

            // ── QRZ Logbook Download / Upload ────────────────────────────────────
            tabIdx = 1;
            var qrzLogbookBox = MakeGroupBox("QRZ Logbook Download / Upload", 175, 5, pw, 182, font);
            panels.Add(qrzLogbookBox);
            serviceList.Items.Add("QRZ");

            qrzLogbookBox.Controls.Add(MakeLabel("API key:", 10, 23, font));
            _qrzLogbookApiKeyTb = new System.Windows.Forms.TextBox
            {
                Text           = ctrl.qrzLogbookApiKey ?? "",
                Location       = new System.Drawing.Point(68, 20),
                Size           = new System.Drawing.Size(300, 20),
                PasswordChar   = '●',
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "QRZ Logbook API key",
            };
            qrzLogbookBox.Controls.Add(_qrzLogbookApiKeyTb);

            qrzLogbookBox.Controls.Add(MakeLabel(
                "Downloads QSOs you've already logged to your QRZ online logbook (Logbook > Sync tab). From",
                10, 48, font));
            qrzLogbookBox.Controls.Add(MakeLabel(
                "qrz.com → Logbook → Settings → API Access. Requires a paid QRZ XML Data subscription -- this",
                10, 64, font));
            qrzLogbookBox.Controls.Add(MakeLabel(
                "key only reaches your own logbook, but also unlocks full Callsign Lookup data (Lookup Data tab).",
                10, 80, font));

            _qrzUploadEnabledCb = new System.Windows.Forms.CheckBox
            {
                Text           = "Enable QRZ Logbook upload (uses the same API key above)",
                Checked        = ctrl.qrzUploadEnabled,
                Location       = new System.Drawing.Point(10, 104),
                AutoSize       = true,
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Enable QRZ Logbook upload",
            };
            qrzLogbookBox.Controls.Add(_qrzUploadEnabledCb);

            _qrzUploadRealtimeCb = new System.Windows.Forms.CheckBox
            {
                Text           = $"Upload automatically as each QSO completes (otherwise, use {uploadLotwKeyText})",
                Checked        = ctrl.qrzUploadRealtime,
                Location       = new System.Drawing.Point(28, 126),
                AutoSize       = true,
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Upload to QRZ automatically in real time",
            };
            qrzLogbookBox.Controls.Add(_qrzUploadRealtimeCb);

            // Automatic download: opt-in (default off), reuses the exact same fetch/import
            // path the Logbook window's manual "Download from QRZ" button already uses --
            // see LogbookAutoSync.cs. Minimum=1 day is a hard floor (can't be set lower),
            // so this can never be configured into hammering QRZ's API.
            _qrzLogbookAutoSyncCb = new System.Windows.Forms.CheckBox
            {
                Text           = "Automatically download and sync every",
                Checked        = ctrl.qrzLogbookAutoSyncEnabled,
                Location       = new System.Drawing.Point(10, 150),
                AutoSize       = true,
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Automatically download and sync the QRZ Logbook",
            };
            qrzLogbookBox.Controls.Add(_qrzLogbookAutoSyncCb);

            _qrzLogbookRefreshDaysNum = new System.Windows.Forms.NumericUpDown
            {
                Minimum        = 1,
                Maximum        = 365,
                Value          = Math.Max(1, Math.Min(365, ctrl.qrzLogbookRefreshDays)),
                Location       = new System.Drawing.Point(216, 148),
                Size           = new System.Drawing.Size(50, 20),
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "QRZ Logbook automatic sync interval in days",
            };
            qrzLogbookBox.Controls.Add(_qrzLogbookRefreshDaysNum);
            qrzLogbookBox.Controls.Add(MakeLabel("days", 270, 150, font));

            // ── LoTW Logbook Download ────────────────────────────────────────────
            tabIdx = 1;
            var lotwLogbookBox = MakeGroupBox("LoTW Logbook Download", 175, 5, pw, 210, font);
            panels.Add(lotwLogbookBox);
            serviceList.Items.Add("LoTW");

            lotwLogbookBox.Controls.Add(MakeLabel("Username:", 10, 23, font));
            _lotwLogbookUserTb = new System.Windows.Forms.TextBox
            {
                Text           = ctrl.lotwLogbookUser ?? "",
                Location       = new System.Drawing.Point(90, 20),
                Size           = new System.Drawing.Size(160, 20),
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "LoTW username for logbook download",
            };
            lotwLogbookBox.Controls.Add(_lotwLogbookUserTb);

            lotwLogbookBox.Controls.Add(MakeLabel("Password:", 10, 47, font));
            _lotwLogbookPassTb = new System.Windows.Forms.TextBox
            {
                Text           = ctrl.lotwLogbookPass ?? "",
                Location       = new System.Drawing.Point(90, 44),
                Size           = new System.Drawing.Size(160, 20),
                PasswordChar   = '●',
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "LoTW password for logbook download",
            };
            lotwLogbookBox.Controls.Add(_lotwLogbookPassTb);

            lotwLogbookBox.Controls.Add(MakeLabel(
                "Downloads your confirmed QSOs from LoTW (Logbook > Sync tab). Separate feature from LoTW User",
                10, 71, font));
            lotwLogbookBox.Controls.Add(MakeLabel(
                "Activity (Lookup Data tab) -- this is your standard LoTW.org login; no TQSL certificate here.",
                10, 87, font));

            // Automatic download: opt-in (default off), same shape as QRZ's above -- see
            // LogbookAutoSync.cs. Minimum=1 day hard floor.
            _lotwLogbookAutoSyncCb = new System.Windows.Forms.CheckBox
            {
                Text           = "Automatically download and sync every",
                Checked        = ctrl.lotwLogbookAutoSyncEnabled,
                Location       = new System.Drawing.Point(10, 111),
                AutoSize       = true,
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Automatically download and sync the LoTW Logbook",
            };
            lotwLogbookBox.Controls.Add(_lotwLogbookAutoSyncCb);

            _lotwLogbookRefreshDaysNum = new System.Windows.Forms.NumericUpDown
            {
                Minimum        = 1,
                Maximum        = 365,
                Value          = Math.Max(1, Math.Min(365, ctrl.lotwLogbookRefreshDays)),
                Location       = new System.Drawing.Point(216, 109),
                Size           = new System.Drawing.Size(50, 20),
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "LoTW Logbook automatic sync interval in days",
            };
            lotwLogbookBox.Controls.Add(_lotwLogbookRefreshDaysNum);
            lotwLogbookBox.Controls.Add(MakeLabel("days", 270, 111, font));

            // ── LoTW upload (self-sufficiency plan, Phase 3) ────────────────
            // Independent of the download settings above -- this only affects what Alt+U's
            // LoTW leg does. Native-only: Jimmy always invokes TQSL itself (TqslUploadClient) --
            // there is no external WSJT-X to delegate to anymore -- needing only the Station
            // Location name below; TQSL's own certificate/passphrase setup stays exactly as
            // configured inside TQSL.
            lotwLogbookBox.Controls.Add(new System.Windows.Forms.Label
            {
                Text = "――――――――――――――――――――――――――――――――――――――――――――――――――――――――――――――――――――――――",
                Location = new System.Drawing.Point(10, 137), AutoSize = true, Font = font, TabStop = false,
            });

            lotwLogbookBox.Controls.Add(MakeLabel("TQSL Station Location:", 10, 148, font));
            _tqslStationLocationTb = new System.Windows.Forms.TextBox
            {
                Text           = ctrl.tqslStationLocation ?? "",
                Location       = new System.Drawing.Point(160, 145),
                Size           = new System.Drawing.Size(150, 20),
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "TQSL Station Location name",
                AccessibleDescription = $"The Station Location name as configured inside TQSL -- not a Jimmy credential. Required for {uploadLotwKeyText} to sign and upload via TQSL.",
            };
            lotwLogbookBox.Controls.Add(_tqslStationLocationTb);
            lotwLogbookBox.Controls.Add(MakeLabel(
                "A passphrase-protected certificate isn't supported here -- use a certificate TQSL doesn't need to unlock.",
                10, 168, font));

            // ── Club Log Logbook Upload ──────────────────────────────────────────
            // A per-user credential (Application Password), entirely separate from
            // the app-wide Club Log key used for country data (Lookup Data tab) -- see
            // ClubLogUploadClient.cs for why these cannot be the same credential.
            tabIdx = 1;
            var clUploadBox = MakeGroupBox("Club Log Logbook Upload", 175, 5, pw, 222, font);
            panels.Add(clUploadBox);
            serviceList.Items.Add("Club Log");

            _clubLogUploadEnabledCb = new System.Windows.Forms.CheckBox
            {
                Text           = "Enable Club Log Logbook upload",
                Checked        = ctrl.clubLogUploadEnabled,
                Location       = new System.Drawing.Point(10, 20),
                AutoSize       = true,
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Enable Club Log Logbook upload",
            };
            clUploadBox.Controls.Add(_clubLogUploadEnabledCb);

            _clubLogUploadRealtimeCb = new System.Windows.Forms.CheckBox
            {
                Text           = $"Upload automatically as each QSO completes (otherwise, use {uploadLotwKeyText})",
                Checked        = ctrl.clubLogUploadRealtime,
                Location       = new System.Drawing.Point(28, 42),
                AutoSize       = true,
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Upload to Club Log automatically in real time",
            };
            clUploadBox.Controls.Add(_clubLogUploadRealtimeCb);

            clUploadBox.Controls.Add(MakeLabel("Email:", 10, 68, font));
            _clubLogUploadEmailTb = new System.Windows.Forms.TextBox
            {
                Text           = ctrl.clubLogUploadEmail ?? "",
                Location       = new System.Drawing.Point(90, 65),
                Size           = new System.Drawing.Size(220, 20),
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Club Log account email for upload",
            };
            clUploadBox.Controls.Add(_clubLogUploadEmailTb);

            clUploadBox.Controls.Add(MakeLabel("App Password:", 10, 92, font));
            _clubLogUploadPasswordTb = new System.Windows.Forms.TextBox
            {
                Text           = ctrl.clubLogUploadPassword ?? "",
                Location       = new System.Drawing.Point(90, 89),
                Size           = new System.Drawing.Size(220, 20),
                PasswordChar   = '●',
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Club Log Application Password for upload",
            };
            clUploadBox.Controls.Add(_clubLogUploadPasswordTb);

            clUploadBox.Controls.Add(MakeLabel("Callsign:", 10, 116, font));
            _clubLogUploadCallsignTb = new System.Windows.Forms.TextBox
            {
                Text           = ctrl.clubLogUploadCallsign ?? "",
                Location       = new System.Drawing.Point(90, 113),
                Size           = new System.Drawing.Size(120, 20),
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Callsign for Club Log upload",
            };
            clUploadBox.Controls.Add(_clubLogUploadCallsignTb);

            // Automatic download: opt-in (default off), same shape as QRZ/LoTW above --
            // see LogbookAutoSync.cs. Minimum=1 day hard floor -- worth being especially
            // conservative here given Club Log's own documented anti-abuse rules (a past
            // real incident: repeated failed requests can get an IP firewalled).
            _clubLogLogbookAutoSyncCb = new System.Windows.Forms.CheckBox
            {
                Text           = "Automatically download and sync every",
                Checked        = ctrl.clubLogLogbookAutoSyncEnabled,
                Location       = new System.Drawing.Point(10, 142),
                AutoSize       = true,
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Automatically download and sync the Club Log Logbook",
            };
            clUploadBox.Controls.Add(_clubLogLogbookAutoSyncCb);

            _clubLogLogbookRefreshDaysNum = new System.Windows.Forms.NumericUpDown
            {
                Minimum        = 1,
                Maximum        = 365,
                Value          = Math.Max(1, Math.Min(365, ctrl.clubLogLogbookRefreshDays)),
                Location       = new System.Drawing.Point(216, 140),
                Size           = new System.Drawing.Size(50, 20),
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Club Log Logbook automatic sync interval in days",
            };
            clUploadBox.Controls.Add(_clubLogLogbookRefreshDaysNum);
            clUploadBox.Controls.Add(MakeLabel("days", 270, 142, font));

            clUploadBox.Controls.Add(MakeLabel(
                "Uploads QSOs to your Club Log online logbook (Logbook > Sync tab, or automatically as you log",
                10, 168, font));
            clUploadBox.Controls.Add(MakeLabel(
                "each contact). Requires a Club Log Application Password (clublog.org → Settings → App",
                10, 184, font));
            clUploadBox.Controls.Add(MakeLabel(
                "Passwords) -- NOT your normal Club Log website login. Separate from the country-data key (Lookup Data tab).",
                10, 200, font));

            // ── HRDLog.net Upload (self-sufficiency plan, Phase 2) ───────────────
            // HRDLog.net is the online logging/awards site at hrdlog.net -- NOT the Ham Radio
            // Deluxe *Logbook* desktop app, and NOT an ARRL confirmation source (an upload here
            // never earns DXCC/WAS credit, unlike LoTW). No auto-download here: HRDLog exposes
            // no bulk-fetch API, unlike QRZ/LoTW/Club Log above.
            tabIdx = 1;
            var hrdLogBox = MakeGroupBox("HRDLog.net Upload", 175, 5, pw, 160, font);
            panels.Add(hrdLogBox);
            serviceList.Items.Add("HRDLog");

            _hrdLogUploadEnabledCb = new System.Windows.Forms.CheckBox
            {
                Text           = "Enable HRDLog.net upload",
                Checked        = ctrl.hrdLogUploadEnabled,
                Location       = new System.Drawing.Point(10, 20),
                AutoSize       = true,
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Enable HRDLog.net upload",
            };
            hrdLogBox.Controls.Add(_hrdLogUploadEnabledCb);

            _hrdLogUploadRealtimeCb = new System.Windows.Forms.CheckBox
            {
                Text           = $"Upload automatically as each QSO completes (otherwise, use {uploadLotwKeyText})",
                Checked        = ctrl.hrdLogUploadRealtime,
                Location       = new System.Drawing.Point(28, 42),
                AutoSize       = true,
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Upload to HRDLog.net automatically in real time",
            };
            hrdLogBox.Controls.Add(_hrdLogUploadRealtimeCb);

            hrdLogBox.Controls.Add(MakeLabel("Callsign:", 10, 68, font));
            _hrdLogUploadCallsignTb = new System.Windows.Forms.TextBox
            {
                Text           = ctrl.hrdLogUploadCallsign ?? "",
                Location       = new System.Drawing.Point(90, 65),
                Size           = new System.Drawing.Size(120, 20),
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Callsign for HRDLog.net upload",
            };
            hrdLogBox.Controls.Add(_hrdLogUploadCallsignTb);

            hrdLogBox.Controls.Add(MakeLabel("Upload code:", 10, 92, font));
            _hrdLogUploadCodeTb = new System.Windows.Forms.TextBox
            {
                Text           = ctrl.hrdLogUploadCode ?? "",
                Location       = new System.Drawing.Point(90, 89),
                Size           = new System.Drawing.Size(220, 20),
                PasswordChar   = '●',
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "HRDLog.net upload code",
            };
            hrdLogBox.Controls.Add(_hrdLogUploadCodeTb);

            hrdLogBox.Controls.Add(MakeLabel(
                "Uploads QSOs to your HRDLog.net account (Options -> your account -> Upload Code). This is",
                10, 116, font));
            hrdLogBox.Controls.Add(MakeLabel(
                "the online HRDLog.net live-logging/awards site, not Ham Radio Deluxe software, and does",
                10, 132, font));
            hrdLogBox.Controls.Add(MakeLabel(
                "not earn ARRL award credit -- LoTW above still handles DXCC/WAS confirmation.",
                10, 148, font));

            // ── eQSL.cc Upload ────────────────────────────────────────────────────
            // Uploaded via EngineHost/Nexus's own eQSL transport (propagation::live::eqsl) --
            // Jimmy Test supplies the operator's own eQSL.cc credentials and the completed
            // ADIF record; Nexus already implements the upload plumbing well, so it isn't
            // duplicated here (see ARCHITECTURE.md's logbook/logging comparison). No auto-
            // download here yet -- see ARCHITECTURE.md for the deferred download/reconciliation
            // contract.
            tabIdx = 1;
            var eqslBox = MakeGroupBox("eQSL.cc Upload", 175, 5, pw, 145, font);
            panels.Add(eqslBox);
            serviceList.Items.Add("eQSL");

            _eqslUploadEnabledCb = new System.Windows.Forms.CheckBox
            {
                Text           = "Enable eQSL.cc upload",
                Checked        = ctrl.eqslUploadEnabled,
                Location       = new System.Drawing.Point(10, 20),
                AutoSize       = true,
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Enable eQSL.cc upload",
            };
            eqslBox.Controls.Add(_eqslUploadEnabledCb);

            _eqslUploadRealtimeCb = new System.Windows.Forms.CheckBox
            {
                Text           = "Upload automatically as each QSO completes",
                Checked        = ctrl.eqslUploadRealtime,
                Location       = new System.Drawing.Point(28, 42),
                AutoSize       = true,
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Upload to eQSL.cc automatically in real time",
            };
            eqslBox.Controls.Add(_eqslUploadRealtimeCb);

            eqslBox.Controls.Add(MakeLabel("Username:", 10, 68, font));
            _eqslUsernameTb = new System.Windows.Forms.TextBox
            {
                Text           = ctrl.eqslUsername ?? "",
                Location       = new System.Drawing.Point(90, 65),
                Size           = new System.Drawing.Size(120, 20),
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "eQSL.cc username",
            };
            eqslBox.Controls.Add(_eqslUsernameTb);

            eqslBox.Controls.Add(MakeLabel("Password:", 10, 92, font));
            _eqslPasswordTb = new System.Windows.Forms.TextBox
            {
                Text           = ctrl.eqslPassword ?? "",
                Location       = new System.Drawing.Point(90, 89),
                Size           = new System.Drawing.Size(160, 20),
                PasswordChar   = '●',
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "eQSL.cc password",
            };
            eqslBox.Controls.Add(_eqslPasswordTb);

            eqslBox.Controls.Add(MakeLabel(
                "Uploads QSOs to your eQSL.cc account using your normal eQSL.cc login and password.",
                10, 116, font));

            WireServiceList(serviceList, logbookSyncPanel, panels);
        }

        private void BuildLookupDataTab()
        {
            lookupPanel.Controls.Clear();
            var font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F);
            int pw = 630;

            // ── General ──────────────────────────────────────────────────────────
            var genBox = MakeGroupBox("General", 5, 5, pw, 48, font);
            lookupPanel.Controls.Add(genBox);

            _useLookupDataCb = new System.Windows.Forms.CheckBox
            {
                Text           = "Use lookup data (master enable — uncheck to disable all lookups without losing settings)",
                Checked        = ctrl.useLookupData,
                Location       = new System.Drawing.Point(10, 18),
                AutoSize       = true,
                TabIndex       = 0,
                Font           = font,
                AccessibleName = "Use lookup data master enable",
            };
            genBox.Controls.Add(_useLookupDataCb);

            var serviceList = new System.Windows.Forms.ListBox
            {
                Location       = new System.Drawing.Point(5, 58),
                Size           = new System.Drawing.Size(160, 305),
                Font           = font,
                TabIndex       = 1,
                AccessibleName = "Lookup data service list",
            };
            lookupPanel.Controls.Add(serviceList);

            var panels = new List<System.Windows.Forms.GroupBox>();
            int tabIdx;

            // ── QRZ Callsign Lookup ──────────────────────────────────────────────
            tabIdx = 2;
            var qrzBox = MakeGroupBox("QRZ Callsign Lookup", 175, 58, pw, 230, font);
            panels.Add(qrzBox);
            serviceList.Items.Add("QRZ Callsign Lookup");

            _qrzEnabledCb = new System.Windows.Forms.CheckBox
            {
                Text           = "Enable QRZ callsign lookup",
                Checked        = ctrl.qrzEnabled,
                Location       = new System.Drawing.Point(10, 20),
                AutoSize       = true,
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Enable QRZ lookup",
            };
            qrzBox.Controls.Add(_qrzEnabledCb);

            qrzBox.Controls.Add(MakeLabel("Username:", 10, 46, font));
            _qrzUsernameTb = new System.Windows.Forms.TextBox
            {
                Text           = ctrl.qrzUsername ?? "",
                Location       = new System.Drawing.Point(90, 43),
                Size           = new System.Drawing.Size(160, 20),
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "QRZ username",
            };
            qrzBox.Controls.Add(_qrzUsernameTb);

            qrzBox.Controls.Add(MakeLabel("Password:", 10, 70, font));
            _qrzPasswordTb = new System.Windows.Forms.TextBox
            {
                Text           = ctrl.qrzPassword ?? "",
                Location       = new System.Drawing.Point(90, 67),
                Size           = new System.Drawing.Size(160, 20),
                PasswordChar   = '●',
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "QRZ password",
            };
            qrzBox.Controls.Add(_qrzPasswordTb);

            qrzBox.Controls.Add(MakeLabel("Cache (days):", 10, 94, font));
            _qrzCacheDaysNum = new System.Windows.Forms.NumericUpDown
            {
                Minimum        = 1,
                Maximum        = 365,
                Value          = Math.Max(1, Math.Min(365, ctrl.qrzCacheDays)),
                Location       = new System.Drawing.Point(100, 91),
                Size           = new System.Drawing.Size(60, 20),
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "QRZ cache lifetime in days",
            };
            qrzBox.Controls.Add(_qrzCacheDaysNum);

            qrzBox.Controls.Add(MakeLabel("Automatic lookup:", 10, 118, font));
            _qrzPolicyCb = new System.Windows.Forms.ComboBox
            {
                DropDownStyle  = System.Windows.Forms.ComboBoxStyle.DropDownList,
                Location       = new System.Drawing.Point(126, 115),
                Size           = new System.Drawing.Size(320, 21),
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "QRZ automatic lookup policy",
            };
            _qrzPolicyCb.Items.AddRange(new object[]
            {
                "Disabled (default) — no automatic QRZ requests",
                "Manual only — lookup dialog for focused call only",
                "Supplement offline — queue entries offline data cannot identify",
            });
            _qrzPolicyCb.SelectedIndex = (int)ctrl.qrzLookupPolicy;
            qrzBox.Controls.Add(_qrzPolicyCb);

            qrzBox.Controls.Add(MakeLabel("Min interval (sec):", 10, 142, font));
            _qrzIntervalNum = new System.Windows.Forms.NumericUpDown
            {
                Minimum        = 5,
                Maximum        = 300,
                Value          = Math.Max(5, Math.Min(300, ctrl.qrzMinIntervalSeconds)),
                Location       = new System.Drawing.Point(138, 139),
                Size           = new System.Drawing.Size(60, 20),
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Minimum seconds between automatic QRZ requests",
            };
            qrzBox.Controls.Add(_qrzIntervalNum);
            qrzBox.Controls.Add(MakeLabel("(default 10 s — recommended for QRZ server courtesy)", 205, 142, font));

            _qrzTestBtn = new System.Windows.Forms.Button
            {
                Text           = "Test Login",
                Location       = new System.Drawing.Point(10, 167),
                Size           = new System.Drawing.Size(90, 24),
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Test QRZ login credentials",
            };
            _qrzTestBtn.Click += QrzTestBtn_Click;
            qrzBox.Controls.Add(_qrzTestBtn);

            _qrzStatusLbl = new System.Windows.Forms.TextBox
            {
                Text           = QrzStatusText(),
                Location       = new System.Drawing.Point(110, 171),
                Size           = new System.Drawing.Size(500, 18),
                Font           = font,
                ReadOnly       = true,
                BorderStyle    = System.Windows.Forms.BorderStyle.None,
                BackColor      = System.Drawing.SystemColors.Control,
                TabStop        = true,
                TabIndex       = tabIdx++,
                AccessibleName = "QRZ login status",
            };
            qrzBox.Controls.Add(_qrzStatusLbl);

            qrzBox.Controls.Add(MakeLabel(
                "Real-time station lookup by callsign (name, address, grid). Uses your normal QRZ.com login --",
                10, 194, font));
            qrzBox.Controls.Add(MakeLabel(
                "any QRZ account works, but without a paid XML Data subscription QRZ returns fewer data fields.",
                10, 210, font));
            qrzBox.Controls.Add(MakeLabel(
                "The same subscription key (Logbook Sync's QRZ panel) unlocks full lookup data and log sync.",
                10, 226, font));

            // ── LoTW User Activity ───────────────────────────────────────────────
            string uploadLotwKeyText = ctrl.hotkeyConfig != null
                ? HotkeyConfig.FormatKeys(ctrl.hotkeyConfig[HotkeyAction.UploadLotw])
                : "";
            if (string.IsNullOrEmpty(uploadLotwKeyText)) uploadLotwKeyText = "(unassigned hotkey)";

            tabIdx = 2;
            var lotwBox = MakeGroupBox("LoTW User Activity  (public download — no account required)", 175, 58, pw, 160, font);
            panels.Add(lotwBox);
            serviceList.Items.Add("LoTW User Activity");

            _lotwEnabledCb = new System.Windows.Forms.CheckBox
            {
                Text           = "Enable LoTW user activity lookup",
                Checked        = ctrl.lotwEnabled,
                Location       = new System.Drawing.Point(10, 20),
                AutoSize       = true,
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Enable LoTW user lookup",
            };
            lotwBox.Controls.Add(_lotwEnabledCb);

            _lotwBoostCb = new System.Windows.Forms.CheckBox
            {
                Text           = "Boost LoTW users (tiebreaker preference for DEFAULT-tier calls)",
                Checked        = ctrl.lotwBoostEnabled,
                Location       = new System.Drawing.Point(10, 42),
                AutoSize       = true,
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Boost LoTW users in call queue ordering",
            };
            lotwBox.Controls.Add(_lotwBoostCb);

            lotwBox.Controls.Add(MakeLabel("Refresh (days):", 10, 66, font));
            _lotwRefreshDaysNum = new System.Windows.Forms.NumericUpDown
            {
                Minimum        = 1,
                Maximum        = 365,
                Value          = Math.Max(1, Math.Min(365, ctrl.lotwRefreshDays)),
                Location       = new System.Drawing.Point(108, 63),
                Size           = new System.Drawing.Size(60, 20),
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "LoTW refresh interval in days",
            };
            lotwBox.Controls.Add(_lotwRefreshDaysNum);

            _lotwUpdateBtn = new System.Windows.Forms.Button
            {
                Text           = "Update Now",
                Location       = new System.Drawing.Point(10, 87),
                Size           = new System.Drawing.Size(90, 24),
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Download LoTW user activity now",
            };
            _lotwUpdateBtn.Click += LoTWUpdateBtn_Click;
            lotwBox.Controls.Add(_lotwUpdateBtn);

            _lotwStatusLbl = new System.Windows.Forms.TextBox
            {
                Text      = LoTWStatusText(),
                Location  = new System.Drawing.Point(110, 91),
                Size      = new System.Drawing.Size(500, 18),
                Font      = font,
                ReadOnly    = true,
                BorderStyle = System.Windows.Forms.BorderStyle.None,
                BackColor   = System.Drawing.SystemColors.Control,
                TabStop   = true,
                TabIndex  = tabIdx++,
                AccessibleName = "LoTW download status",
            };
            lotwBox.Controls.Add(_lotwStatusLbl);

            // LoTW upload itself stays a manual, WSJT-X-driven action (its own TQSL
            // signing/upload is batch-oriented, not a per-QSO API call like QRZ/Club Log,
            // so there is no real-time-upload option to offer here) -- this checkbox only
            // gates whether the upload hotkey below tells WSJT-X to do it at all, for
            // operators who don't use LoTW and would otherwise see WSJT-X report an error.
            _lotwUploadEnabledCb = new System.Windows.Forms.CheckBox
            {
                Text           = $"Have WSJT-X upload to LoTW when pressing {uploadLotwKeyText} (uncheck if you don't use LoTW)",
                Checked        = ctrl.lotwUploadEnabled,
                Location       = new System.Drawing.Point(10, 116),
                AutoSize       = true,
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Upload to LoTW when pressing the upload hotkey",
            };
            lotwBox.Controls.Add(_lotwUploadEnabledCb);

            // ── Club Log Country Data + Big CTY aliases ──────────────────────────
            // Automatic Jimmy infrastructure, not a user-facing toggle -- country
            // data downloads unconditionally using Jimmy's own application key
            // (see ClubLogAppKey.cs), so Rule Definition awards (DXCC etc.) work
            // out of the box with no configuration. "Update Now" and the refresh
            // interval below cover both files: Club Log's clublog_cty.xml (Name/
            // Adif/Continent/Deleted per entity) and AD1C's real Big CTY cty.dat
            // (the full per-callarea prefix/exception data Club Log's own file
            // doesn't carry -- see ClubLogProvider.cs), which is what actually
            // resolves a decoded callsign to the right entity.
            tabIdx = 2;
            var clBox = MakeGroupBox("Country & Prefix Data (automatic — no account needed)", 175, 58, pw, 76, font);
            panels.Add(clBox);
            serviceList.Items.Add("Country & Prefix Data");

            clBox.Controls.Add(MakeLabel("Refresh (days):", 10, 23, font));
            _clubLogRefreshDaysNum = new System.Windows.Forms.NumericUpDown
            {
                Minimum        = 1,
                Maximum        = 365,
                Value          = Math.Max(1, Math.Min(365, ctrl.clubLogRefreshDays)),
                Location       = new System.Drawing.Point(108, 20),
                Size           = new System.Drawing.Size(60, 20),
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Club Log refresh interval in days",
            };
            clBox.Controls.Add(_clubLogRefreshDaysNum);

            _clubLogUpdateBtn = new System.Windows.Forms.Button
            {
                Text           = "Update Now",
                Location       = new System.Drawing.Point(10, 43),
                Size           = new System.Drawing.Size(90, 24),
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Download Club Log data now",
            };
            _clubLogUpdateBtn.Click += ClubLogUpdateBtn_Click;
            clBox.Controls.Add(_clubLogUpdateBtn);

            _clubLogStatusLbl = new System.Windows.Forms.TextBox
            {
                Text      = ClubLogStatusText(),
                Location  = new System.Drawing.Point(110, 47),
                Size      = new System.Drawing.Size(500, 18),
                Font      = font,
                ReadOnly    = true,
                BorderStyle = System.Windows.Forms.BorderStyle.None,
                BackColor   = System.Drawing.SystemColors.Control,
                TabStop   = true,
                TabIndex  = tabIdx++,
                AccessibleName = "Club Log download status",
            };
            clBox.Controls.Add(_clubLogStatusLbl);

            // ── FCC ULS US State Lookup ──────────────────────────────────────────
            // Opt-in (default off) since the full download is ~170MB, unlike Club
            // Log's small country file above. When enabled, its state answer takes
            // priority over QRZ's (see LookupManager's provider order) since it's
            // the FCC's own authoritative registration data.
            tabIdx = 2;
            var fccBox = MakeGroupBox("FCC ULS US State Lookup (optional -- ~170MB download, no account needed)", 175, 58, pw, 130, font);
            panels.Add(fccBox);
            serviceList.Items.Add("FCC ULS");

            _fccUlsEnabledCb = new System.Windows.Forms.CheckBox
            {
                Text           = "Enable FCC ULS lookup",
                Checked        = ctrl.fccUlsEnabled,
                Location       = new System.Drawing.Point(10, 20),
                AutoSize       = true,
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Enable FCC ULS US state lookup",
            };
            fccBox.Controls.Add(_fccUlsEnabledCb);

            fccBox.Controls.Add(MakeLabel("Refresh (days):", 10, 47, font));
            _fccUlsRefreshDaysNum = new System.Windows.Forms.NumericUpDown
            {
                Minimum        = 1,
                Maximum        = 365,
                Value          = Math.Max(1, Math.Min(365, ctrl.fccUlsRefreshDays)),
                Location       = new System.Drawing.Point(108, 44),
                Size           = new System.Drawing.Size(60, 20),
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "FCC ULS refresh interval in days",
            };
            fccBox.Controls.Add(_fccUlsRefreshDaysNum);

            _fccUlsUpdateBtn = new System.Windows.Forms.Button
            {
                Text           = "Update Now",
                Location       = new System.Drawing.Point(10, 70),
                Size           = new System.Drawing.Size(90, 24),
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Download FCC ULS data now",
            };
            _fccUlsUpdateBtn.Click += FccUlsUpdateBtn_Click;
            fccBox.Controls.Add(_fccUlsUpdateBtn);

            _fccUlsStatusLbl = new System.Windows.Forms.TextBox
            {
                Text        = FccUlsStatusText(),
                Location    = new System.Drawing.Point(110, 74),
                Size        = new System.Drawing.Size(500, 18),
                Font        = font,
                ReadOnly    = true,
                BorderStyle = System.Windows.Forms.BorderStyle.None,
                BackColor   = System.Drawing.SystemColors.Control,
                TabStop     = true,
                TabIndex    = tabIdx++,
                AccessibleName = "FCC ULS download status",
            };
            fccBox.Controls.Add(_fccUlsStatusLbl);

            fccBox.Controls.Add(MakeLabel(
                "The FCC's own free public amateur-license database -- gives the actual registered US state",
                10, 100, font));
            fccBox.Controls.Add(MakeLabel(
                "for a callsign, offline and without needing QRZ. Weekly full refresh only (no daily deltas).",
                10, 116, font));

            // ── HamQTH Callsign Lookup ───────────────────────────────────────────
            // Uploaded via EngineHost/Nexus's own HamQTH transport (propagation::live::hamqth) --
            // login+lookup combined per call, no session caching (see ExternalDataClient.
            // LookupHamQth). Not yet wired into LookupManager's own provider chain (that would
            // change lookup precedence/behavior for every existing operator and needs its own
            // deliberate design pass) -- see ARCHITECTURE.md. For now this is a standalone,
            // on-demand credential panel; a future pass can register it as an additional
            // ILookupProvider once the precedence question is settled.
            tabIdx = 2;
            var hamQthBox = MakeGroupBox("HamQTH Callsign Lookup", 175, 58, pw, 110, font);
            panels.Add(hamQthBox);
            serviceList.Items.Add("HamQTH");

            _hamQthEnabledCb = new System.Windows.Forms.CheckBox
            {
                Text           = "Enable HamQTH callsign lookup",
                Checked        = ctrl.hamQthEnabled,
                Location       = new System.Drawing.Point(10, 20),
                AutoSize       = true,
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "Enable HamQTH lookup",
            };
            hamQthBox.Controls.Add(_hamQthEnabledCb);

            hamQthBox.Controls.Add(MakeLabel("Username:", 10, 46, font));
            _hamQthUsernameTb = new System.Windows.Forms.TextBox
            {
                Text           = ctrl.hamQthUsername ?? "",
                Location       = new System.Drawing.Point(90, 43),
                Size           = new System.Drawing.Size(160, 20),
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "HamQTH username",
            };
            hamQthBox.Controls.Add(_hamQthUsernameTb);

            hamQthBox.Controls.Add(MakeLabel("Password:", 10, 70, font));
            _hamQthPasswordTb = new System.Windows.Forms.TextBox
            {
                Text           = ctrl.hamQthPassword ?? "",
                Location       = new System.Drawing.Point(90, 67),
                Size           = new System.Drawing.Size(160, 20),
                PasswordChar   = '●',
                TabIndex       = tabIdx++,
                Font           = font,
                AccessibleName = "HamQTH password",
            };
            hamQthBox.Controls.Add(_hamQthPasswordTb);

            hamQthBox.Controls.Add(MakeLabel(
                "Uses your normal HamQTH.com login. Currently used only from the Lookup Selected Station",
                10, 94, font));

            WireServiceList(serviceList, lookupPanel, panels);
        }

        // Shows only the panel matching the current list selection, hiding the rest --
        // same idea as the Logbook window's Awards-tab combo-driven detail view, just
        // backed by a persistent ListBox instead of a dropdown.
        //
        // Added 2026-08-10: only the CURRENTLY selected panel is ever actually present in
        // host.Controls -- the others are fully removed, not just Visible=false. Confirmed live
        // (real JAWS testing) that the old Visible-toggle-only approach left stale content from
        // a PREVIOUSLY-shown panel getting announced interleaved with the newly-selected one's
        // real controls (e.g. Lookup Data's own FCC ULS description text bleeding into the QRZ
        // panel's announcement after switching services) -- JAWS's own accessibility-tree/
        // virtual-buffer cache does not reliably invalidate for a sibling control that's still
        // physically parented in the same window, merely hidden. Actually removing the control
        // from the tree, not just hiding it, is what a native TabControl's own tab-switch does
        // structurally and is the fix that's actually needed here too.
        private static void WireServiceList(System.Windows.Forms.ListBox listBox, System.Windows.Forms.Control host, List<System.Windows.Forms.GroupBox> panels)
        {
            System.Windows.Forms.GroupBox current = null;
            void UpdateVisibility()
            {
                if (current != null) host.Controls.Remove(current);
                int idx = listBox.SelectedIndex;
                current = (idx >= 0 && idx < panels.Count) ? panels[idx] : null;
                if (current != null) host.Controls.Add(current);
            }
            listBox.SelectedIndexChanged += (s, e) => UpdateVisibility();
            if (listBox.Items.Count > 0) listBox.SelectedIndex = 0;
            UpdateVisibility();
        }

        private static System.Windows.Forms.GroupBox MakeGroupBox(string text, int x, int y, int w, int h, System.Drawing.Font font)
        {
            return new System.Windows.Forms.GroupBox
            {
                Text     = text,
                Location = new System.Drawing.Point(x, y),
                Size     = new System.Drawing.Size(w, h),
                TabStop  = false,
                Font     = font,
            };
        }

        private static System.Windows.Forms.Label MakeLabel(string text, int x, int y, System.Drawing.Font font)
        {
            return new System.Windows.Forms.Label
            {
                Text     = text,
                Location = new System.Drawing.Point(x, y),
                AutoSize = true,
                TabStop  = false,
                Font     = font,
            };
        }

        private string QrzStatusText()
        {
            var m = ctrl.lookupManager;
            if (m == null || !m.Qrz.IsEnabled) return "QRZ lookup disabled.";
            if (!string.IsNullOrEmpty(m.Qrz.LastError)) return $"Error: {m.Qrz.LastError}";
            string auth = !string.IsNullOrEmpty(m.Qrz.AuthCallsign) ? $" ({m.Qrz.AuthCallsign})" : "";
            string lastLookup = m.Qrz.LastSuccessfulLookup.HasValue
                ? $", last lookup cached {m.Qrz.LastSuccessfulLookup.Value.ToLocalTime():g}"
                : ", no lookups cached yet";
            return $"Configured: {m.Qrz.Username}{auth}{lastLookup}";
        }

        private string LoTWStatusText()
        {
            var m = ctrl.lookupManager;
            if (m == null || !m.LoTW.IsEnabled) return "LoTW lookup disabled.";
            if (m.LoTW.UserCount == 0) return "Not downloaded yet. Click Update Now.";
            var age = m.LoTW.LastUpdate == DateTime.MinValue ? "never" : m.LoTW.LastUpdate.ToLocalTime().ToString("g");
            return $"{m.LoTW.UserCount:N0} users, last updated {age}";
        }

        private string ClubLogStatusText()
        {
            var m = ctrl.lookupManager;
            if (m == null) return "Not available yet.";
            if (m.ClubLog.EntityCount == 0)
            {
                return string.IsNullOrEmpty(ClubLogAppKey.Resolve())
                    ? "No application key available in this build — Club Log data unavailable."
                    : "Not downloaded yet. Click Update Now.";
            }
            var age = m.ClubLog.LastUpdate == DateTime.MinValue ? "never" : m.ClubLog.LastUpdate.ToLocalTime().ToString("g");
            string bigCty = m.ClubLog.BigCtyAliasCount == 0
                ? "Big CTY aliases not downloaded yet"
                : $"{m.ClubLog.BigCtyAliasCount:N0} Big CTY aliases, updated " +
                  (m.ClubLog.BigCtyLastUpdate == DateTime.MinValue ? "never" : m.ClubLog.BigCtyLastUpdate.ToLocalTime().ToString("g"));
            return $"{m.ClubLog.EntityCount} entities, last updated {age}; {bigCty}";
        }

        private void SaveLookupTab()
        {
            if (_useLookupDataCb == null) return;
            ctrl.useLookupData           = _useLookupDataCb.Checked;
            ctrl.qrzEnabled              = _qrzEnabledCb?.Checked              ?? false;
            ctrl.qrzUsername             = _qrzUsernameTb?.Text                ?? "";
            ctrl.qrzPassword             = _qrzPasswordTb?.Text                ?? "";
            ctrl.qrzCacheDays            = (int)(_qrzCacheDaysNum?.Value        ?? 7);
            ctrl.qrzLookupPolicy         = (QrzLookupPolicy)(_qrzPolicyCb?.SelectedIndex ?? 0);
            ctrl.qrzMinIntervalSeconds   = (int)(_qrzIntervalNum?.Value         ?? 10);
            ctrl.qrzLogbookApiKey        = _qrzLogbookApiKeyTb?.Text.Trim()     ?? "";
            ctrl.qrzUploadEnabled        = _qrzUploadEnabledCb?.Checked         ?? false;
            ctrl.qrzUploadRealtime       = _qrzUploadRealtimeCb?.Checked        ?? false;
            ctrl.qrzLogbookAutoSyncEnabled = _qrzLogbookAutoSyncCb?.Checked      ?? false;
            ctrl.qrzLogbookRefreshDays   = (int)(_qrzLogbookRefreshDaysNum?.Value ?? 7);
            ctrl.lotwEnabled             = _lotwEnabledCb?.Checked              ?? false;
            ctrl.lotwBoostEnabled        = _lotwBoostCb?.Checked                ?? false;
            ctrl.lotwUploadEnabled       = _lotwUploadEnabledCb?.Checked        ?? true;
            ctrl.lotwRefreshDays         = (int)(_lotwRefreshDaysNum?.Value      ?? 30);
            ctrl.lotwLogbookUser         = _lotwLogbookUserTb?.Text.Trim()      ?? "";
            ctrl.lotwLogbookPass         = _lotwLogbookPassTb?.Text            ?? "";
            ctrl.lotwLogbookAutoSyncEnabled = _lotwLogbookAutoSyncCb?.Checked    ?? false;
            ctrl.lotwLogbookRefreshDays  = (int)(_lotwLogbookRefreshDaysNum?.Value ?? 7);
            ctrl.clubLogRefreshDays      = (int)(_clubLogRefreshDaysNum?.Value   ?? 30);
            ctrl.clubLogUploadEnabled    = _clubLogUploadEnabledCb?.Checked     ?? false;
            ctrl.clubLogUploadRealtime   = _clubLogUploadRealtimeCb?.Checked    ?? false;
            ctrl.clubLogUploadEmail      = _clubLogUploadEmailTb?.Text.Trim()   ?? "";
            ctrl.clubLogUploadPassword   = _clubLogUploadPasswordTb?.Text      ?? "";
            ctrl.clubLogUploadCallsign   = _clubLogUploadCallsignTb?.Text.Trim().ToUpperInvariant() ?? "";
            ctrl.clubLogLogbookAutoSyncEnabled = _clubLogLogbookAutoSyncCb?.Checked ?? false;
            ctrl.clubLogLogbookRefreshDays = (int)(_clubLogLogbookRefreshDaysNum?.Value ?? 7);
            ctrl.hrdLogUploadEnabled     = _hrdLogUploadEnabledCb?.Checked          ?? false;
            ctrl.hrdLogUploadRealtime    = _hrdLogUploadRealtimeCb?.Checked         ?? false;
            ctrl.hrdLogUploadCallsign    = _hrdLogUploadCallsignTb?.Text.Trim().ToUpperInvariant() ?? "";
            ctrl.hrdLogUploadCode        = _hrdLogUploadCodeTb?.Text              ?? "";
            ctrl.tqslStationLocation     = _tqslStationLocationTb?.Text.Trim()    ?? "";
            ctrl.fccUlsEnabled           = _fccUlsEnabledCb?.Checked           ?? false;
            ctrl.fccUlsRefreshDays       = (int)(_fccUlsRefreshDaysNum?.Value   ?? 7);
            ctrl.eqslUploadEnabled       = _eqslUploadEnabledCb?.Checked       ?? false;
            ctrl.eqslUploadRealtime      = _eqslUploadRealtimeCb?.Checked      ?? false;
            ctrl.eqslUsername            = _eqslUsernameTb?.Text.Trim()        ?? "";
            ctrl.eqslPassword            = _eqslPasswordTb?.Text              ?? "";
            ctrl.hamQthEnabled           = _hamQthEnabledCb?.Checked           ?? false;
            ctrl.hamQthUsername          = _hamQthUsernameTb?.Text.Trim()      ?? "";
            ctrl.hamQthPassword          = _hamQthPasswordTb?.Text            ?? "";
        }

        // ===== APPEARANCE TAB =====

        private void BuildAppearanceTab()
        {
            appearancePanel.Controls.Clear();

            var font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F);
            int y = 8;
            const int left = 10;

            _appearanceBackColor = ctrl.Settings.ListBackColor;
            _appearanceForeColor = ctrl.Settings.ListForeColor;
            _appearanceAltRowColor = ctrl.Settings.ListAltRowColor;
            _alertForeColors = new Dictionary<WsjtxClient.CallCategory, Color?>(ctrl.Settings.AlertForeColors);
            _alertBackColors = new Dictionary<WsjtxClient.CallCategory, Color?>(ctrl.Settings.AlertBackColors);

            var themeLabel = new System.Windows.Forms.Label
            {
                Text = "Theme:",
                AutoSize = true,
                Location = new System.Drawing.Point(left, y + 3),
                Font = font,
                TabStop = false,
            };
            appearancePanel.Controls.Add(themeLabel);

            appearanceThemeCombo = new System.Windows.Forms.ComboBox
            {
                DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList,
                Location = new System.Drawing.Point(left + 60, y),
                Size = new System.Drawing.Size(160, 22),
                TabIndex = 0,
                Font = font,
                AccessibleName = "Appearance theme",
                AccessibleDescription = "Choose a preset color theme for the station lists, or pick individual colors below.",
            };
            appearanceThemeCombo.Items.AddRange(new object[] { "Default", "Dark", "High Contrast" });
            appearanceThemeCombo.SelectedIndex = AppearanceThemeIndexForColors(_appearanceBackColor, _appearanceForeColor, _appearanceAltRowColor);
            appearanceThemeCombo.SelectedIndexChanged += AppearanceThemeCombo_SelectedIndexChanged;
            appearancePanel.Controls.Add(appearanceThemeCombo);
            y += 34;

            appearanceBackColorButton = new System.Windows.Forms.Button
            {
                Text = "List background color...",
                Location = new System.Drawing.Point(left, y),
                Size = new System.Drawing.Size(200, 24),
                TabIndex = 1,
                Font = font,
                AccessibleName = "List background color",
                AccessibleDescription = "Choose the background color for the station lists.",
            };
            appearanceBackColorButton.Click += AppearanceBackColorButton_Click;
            appearancePanel.Controls.Add(appearanceBackColorButton);
            y += 30;

            appearanceForeColorButton = new System.Windows.Forms.Button
            {
                Text = "List text color...",
                Location = new System.Drawing.Point(left, y),
                Size = new System.Drawing.Size(200, 24),
                TabIndex = 2,
                Font = font,
                AccessibleName = "List text color",
                AccessibleDescription = "Choose the text color for the station lists.",
            };
            appearanceForeColorButton.Click += AppearanceForeColorButton_Click;
            appearancePanel.Controls.Add(appearanceForeColorButton);
            y += 30;

            appearanceAltRowColorButton = new System.Windows.Forms.Button
            {
                Text = "Alternating row color...",
                Location = new System.Drawing.Point(left, y),
                Size = new System.Drawing.Size(200, 24),
                TabIndex = 3,
                Font = font,
                AccessibleName = "Alternating row color",
                AccessibleDescription = "Choose the color used for every other row in the station lists.",
            };
            appearanceAltRowColorButton.Click += AppearanceAltRowColorButton_Click;
            appearancePanel.Controls.Add(appearanceAltRowColorButton);
            y += 38;

            var fontSizeLabel = new System.Windows.Forms.Label
            {
                Text = "List font size:",
                AutoSize = true,
                Location = new System.Drawing.Point(left, y + 3),
                Font = font,
                TabStop = false,
            };
            appearancePanel.Controls.Add(fontSizeLabel);

            appearanceFontSizeNumeric = new System.Windows.Forms.NumericUpDown
            {
                Location = new System.Drawing.Point(left + 90, y),
                Size = new System.Drawing.Size(60, 22),
                TabIndex = 4,
                Minimum = 8,
                Maximum = 18,
                Value = Math.Max(8, Math.Min(18, ctrl.Settings.ListFontSize)),
                Font = font,
                AccessibleName = "List font size",
                AccessibleDescription = "Font size used in the station lists, from 8 to 18 points.",
            };
            appearancePanel.Controls.Add(appearanceFontSizeNumeric);
            y += 36;

            var restoreDefaultsButton = new System.Windows.Forms.Button
            {
                Text = "Restore Defaults",
                Location = new System.Drawing.Point(left, y),
                Size = new System.Drawing.Size(140, 24),
                TabIndex = 5,
                Font = font,
                AccessibleName = "Restore appearance defaults",
                AccessibleDescription = "Resets list colors and font size back to the original Jimmy defaults.",
            };
            restoreDefaultsButton.Click += AppearanceRestoreDefaultsButton_Click;
            appearancePanel.Controls.Add(restoreDefaultsButton);
            y += 38;

            var alertSectionLabel = new System.Windows.Forms.Label
            {
                Text = "Alert category colors:",
                AutoSize = true,
                Location = new System.Drawing.Point(left, y + 3),
                Font = font,
                TabStop = false,
            };
            appearancePanel.Controls.Add(alertSectionLabel);
            y += 26;

            alertCategoryCombo = new System.Windows.Forms.ComboBox
            {
                DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList,
                Location = new System.Drawing.Point(left, y),
                Size = new System.Drawing.Size(220, 22),
                TabIndex = 6,
                Font = font,
                AccessibleName = "Alert category",
                AccessibleDescription = "Choose which alert category's colors to edit below.",
            };
            foreach (var cat in JimmySettings.AlertCategories)
                alertCategoryCombo.Items.Add(JimmySettings.AlertCategoryLabels[cat]);
            alertCategoryCombo.SelectedIndex = 0;
            alertCategoryCombo.SelectedIndexChanged += AlertCategoryCombo_SelectedIndexChanged;
            appearancePanel.Controls.Add(alertCategoryCombo);
            y += 34;

            alertForeColorButton = new System.Windows.Forms.Button
            {
                Text = "Alert text color...",
                Location = new System.Drawing.Point(left, y),
                Size = new System.Drawing.Size(220, 24),
                TabIndex = 7,
                Font = font,
                AccessibleDescription = "Choose the text color used for the selected alert category.",
            };
            alertForeColorButton.Click += AlertForeColorButton_Click;
            appearancePanel.Controls.Add(alertForeColorButton);
            y += 30;

            alertBackColorButton = new System.Windows.Forms.Button
            {
                Text = "Alert background color...",
                Location = new System.Drawing.Point(left, y),
                Size = new System.Drawing.Size(220, 24),
                TabIndex = 8,
                Font = font,
                AccessibleDescription = "Choose the background color used for the selected alert category.",
            };
            alertBackColorButton.Click += AlertBackColorButton_Click;
            appearancePanel.Controls.Add(alertBackColorButton);
            y += 30;

            alertClearColorButton = new System.Windows.Forms.Button
            {
                Text = "Clear This Category's Colors",
                Location = new System.Drawing.Point(left, y),
                Size = new System.Drawing.Size(220, 24),
                TabIndex = 9,
                Font = font,
                AccessibleDescription = "Removes the custom colors for the selected alert category, so it uses the normal list colors again.",
            };
            alertClearColorButton.Click += AlertClearColorButton_Click;
            appearancePanel.Controls.Add(alertClearColorButton);

            UpdateAlertColorAccessibleNames();
        }

        private void AppearanceThemeCombo_SelectedIndexChanged(object sender, EventArgs e)
        {
            switch (appearanceThemeCombo.SelectedIndex)
            {
                case 0: // Default
                    _appearanceBackColor = SystemColors.Window;
                    _appearanceForeColor = SystemColors.WindowText;
                    _appearanceAltRowColor = Color.FromArgb(233, 233, 233);
                    break;
                case 1: // Dark
                    _appearanceBackColor = Color.FromArgb(30, 30, 30);
                    _appearanceForeColor = Color.FromArgb(220, 220, 220);
                    _appearanceAltRowColor = Color.FromArgb(45, 45, 45);
                    break;
                case 2: // High Contrast
                    _appearanceBackColor = Color.Black;
                    _appearanceForeColor = Color.Yellow;
                    _appearanceAltRowColor = Color.FromArgb(40, 40, 0);
                    break;
            }
        }

        private void AppearanceBackColorButton_Click(object sender, EventArgs e)
        {
            using (var dlg = new System.Windows.Forms.ColorDialog { Color = _appearanceBackColor, FullOpen = true })
                if (dlg.ShowDialog(this) == System.Windows.Forms.DialogResult.OK)
                    _appearanceBackColor = dlg.Color;
        }

        private void AppearanceForeColorButton_Click(object sender, EventArgs e)
        {
            using (var dlg = new System.Windows.Forms.ColorDialog { Color = _appearanceForeColor, FullOpen = true })
                if (dlg.ShowDialog(this) == System.Windows.Forms.DialogResult.OK)
                    _appearanceForeColor = dlg.Color;
        }

        private void AppearanceAltRowColorButton_Click(object sender, EventArgs e)
        {
            using (var dlg = new System.Windows.Forms.ColorDialog { Color = _appearanceAltRowColor, FullOpen = true })
                if (dlg.ShowDialog(this) == System.Windows.Forms.DialogResult.OK)
                    _appearanceAltRowColor = dlg.Color;
        }

        private void AppearanceRestoreDefaultsButton_Click(object sender, EventArgs e)
        {
            _appearanceBackColor = SystemColors.Window;
            _appearanceForeColor = SystemColors.WindowText;
            _appearanceAltRowColor = Color.FromArgb(233, 233, 233);
            appearanceThemeCombo.SelectedIndex = 0;
            appearanceFontSizeNumeric.Value = 10;
        }

        // Matches the working colors back to a preset index for the combo's initial
        // selection. No exact match (i.e. a manually-picked custom color) falls back
        // to "Default" in the combo -- the combo is just a shortcut, not the source
        // of truth, so this doesn't lose or alter the actual custom colors.
        private static int AppearanceThemeIndexForColors(Color back, Color fore, Color alt)
        {
            if (ColorsEqual(back, SystemColors.Window) && ColorsEqual(fore, SystemColors.WindowText) && ColorsEqual(alt, Color.FromArgb(233, 233, 233)))
                return 0;
            if (ColorsEqual(back, Color.FromArgb(30, 30, 30)) && ColorsEqual(fore, Color.FromArgb(220, 220, 220)) && ColorsEqual(alt, Color.FromArgb(45, 45, 45)))
                return 1;
            if (ColorsEqual(back, Color.Black) && ColorsEqual(fore, Color.Yellow) && ColorsEqual(alt, Color.FromArgb(40, 40, 0)))
                return 2;
            return 0;
        }

        private static bool ColorsEqual(Color a, Color b) => a.ToArgb() == b.ToArgb();

        private WsjtxClient.CallCategory SelectedAlertCategory()
        {
            int idx = alertCategoryCombo.SelectedIndex;
            if (idx < 0 || idx >= JimmySettings.AlertCategories.Length) idx = 0;
            return JimmySettings.AlertCategories[idx];
        }

        private void AlertCategoryCombo_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateAlertColorAccessibleNames();
        }

        private void AlertForeColorButton_Click(object sender, EventArgs e)
        {
            var cat = SelectedAlertCategory();
            Color current = _alertForeColors.TryGetValue(cat, out var c) && c.HasValue ? c.Value : _appearanceForeColor;
            using (var dlg = new System.Windows.Forms.ColorDialog { Color = current, FullOpen = true })
                if (dlg.ShowDialog(this) == System.Windows.Forms.DialogResult.OK)
                {
                    _alertForeColors[cat] = dlg.Color;
                    UpdateAlertColorAccessibleNames();
                }
        }

        private void AlertBackColorButton_Click(object sender, EventArgs e)
        {
            var cat = SelectedAlertCategory();
            Color current = _alertBackColors.TryGetValue(cat, out var c) && c.HasValue ? c.Value : _appearanceBackColor;
            using (var dlg = new System.Windows.Forms.ColorDialog { Color = current, FullOpen = true })
                if (dlg.ShowDialog(this) == System.Windows.Forms.DialogResult.OK)
                {
                    _alertBackColors[cat] = dlg.Color;
                    UpdateAlertColorAccessibleNames();
                }
        }

        private void AlertClearColorButton_Click(object sender, EventArgs e)
        {
            var cat = SelectedAlertCategory();
            _alertForeColors[cat] = null;
            _alertBackColors[cat] = null;
            UpdateAlertColorAccessibleNames();
        }

        // Keeps each alert color button's AccessibleName current so JAWS/NVDA announce the
        // selected category and its color on focus -- no visual swatch needed to convey state.
        private void UpdateAlertColorAccessibleNames()
        {
            if (alertCategoryCombo == null) return;
            var cat = SelectedAlertCategory();
            string label = JimmySettings.AlertCategoryLabels[cat];
            _alertForeColors.TryGetValue(cat, out var fc);
            _alertBackColors.TryGetValue(cat, out var bc);
            alertForeColorButton.AccessibleName = $"Alert text color for {label}, currently {ColorDisplayName(fc)}";
            alertBackColorButton.AccessibleName = $"Alert background color for {label}, currently {ColorDisplayName(bc)}";
            alertClearColorButton.AccessibleName = $"Clear alert color for {label}";
        }

        private static string ColorDisplayName(Color? c)
        {
            if (!c.HasValue) return "default";
            return c.Value.IsKnownColor ? c.Value.Name : $"RGB {c.Value.R}, {c.Value.G}, {c.Value.B}";
        }

        // Standard Maidenhead locator casing: field (first 2 chars) uppercase letters,
        // square (next 2) digits, subsquare (last 2, if present) lowercase letters -- so a
        // grid typed in any case (or all upper/lower) displays and stores in the
        // conventional form, matching what real over-the-air FT8 grid exchanges expect.
        // Malformed input (wrong length/non-alpha where a letter belongs) is returned
        // unchanged, uppercased only -- not this dialog's job to validate grid syntax.
        private static string FormatGridSquare(string grid)
        {
            if (string.IsNullOrEmpty(grid)) return grid;
            if (grid.Length != 4 && grid.Length != 6) return grid.ToUpperInvariant();

            char[] c = grid.ToCharArray();
            for (int i = 0; i < 2; i++)
            {
                if (!char.IsLetter(c[i])) return grid.ToUpperInvariant();
                c[i] = char.ToUpperInvariant(c[i]);
            }
            for (int i = 2; i < 4; i++)
            {
                if (!char.IsDigit(c[i])) return grid.ToUpperInvariant();
            }
            if (grid.Length == 6)
            {
                for (int i = 4; i < 6; i++)
                {
                    if (!char.IsLetter(c[i])) return grid.ToUpperInvariant();
                    c[i] = char.ToLowerInvariant(c[i]);
                }
            }
            return new string(c);
        }

        // Turns the rig model combo's selected display text ("Kenwood TS-590SG (2037)", or the
        // "(currently configured: X)" fallback for an unlisted value) back into the raw Hamlib
        // model number ctrl.Radio.RigModel/LaunchBundled expect. Falls back to the text itself
        // for anything that doesn't match either pattern, so nothing ever silently disappears --
        // matches the plain-TextBox behavior this replaced.
        // Public (not private) for the same reason HrdLogUploadClient.ClassifyResponse is
        // public: JimmyTests has no InternalsVisibleTo, so only public static members are
        // reachable from tests.
        public static string ExtractRigModelId(string display)
        {
            if (string.IsNullOrEmpty(display)) return display;
            var m = System.Text.RegularExpressions.Regex.Match(display, @"\((\d+)\)\s*$");
            if (m.Success) return m.Groups[1].Value;
            m = System.Text.RegularExpressions.Regex.Match(display, @"^\(currently configured:\s*(.+)\)$");
            return m.Success ? m.Groups[1].Value : display;
        }

        private void SaveAppearanceTab()
        {
            if (appearanceFontSizeNumeric == null) return;
            ctrl.Settings.ListBackColor = _appearanceBackColor;
            ctrl.Settings.ListForeColor = _appearanceForeColor;
            ctrl.Settings.ListAltRowColor = _appearanceAltRowColor;
            ctrl.Settings.ListFontSize = (int)appearanceFontSizeNumeric.Value;

            foreach (var cat in JimmySettings.AlertCategories)
            {
                ctrl.Settings.AlertForeColors[cat] = _alertForeColors.TryGetValue(cat, out var fc) ? fc : null;
                ctrl.Settings.AlertBackColors[cat] = _alertBackColors.TryGetValue(cat, out var bc) ? bc : null;
            }
        }

        // Tests QRZ.com login (username/password) as before, and -- if a Logbook API key
        // is entered -- also validates that key (via a side-effect-free STATUS call), so a
        // bad key is caught here instead of only surfacing later as an upload/download
        // error. Skips the key check entirely when the field is blank.
        private async void QrzTestBtn_Click(object sender, EventArgs e)
        {
            if (ctrl.lookupManager == null) return;
            ctrl.lookupManager.Qrz.Configure(
                true,
                _qrzUsernameTb?.Text ?? "",
                _qrzPasswordTb?.Text ?? "",
                (int)(_qrzCacheDaysNum?.Value ?? 7));
            _qrzTestBtn.Enabled  = false;
            _qrzStatusLbl.Text   = "Testing login…";

            bool loginOk = await ctrl.lookupManager.TestQrzAsync();

            string loginResult;
            if (loginOk)
            {
                string callsign = ctrl.lookupManager.Qrz.AuthCallsign;
                loginResult = string.IsNullOrEmpty(callsign)
                    ? "Login successful!"
                    : $"Login successful — authenticated as {callsign}";
            }
            else
            {
                loginResult = $"Login error: {ctrl.lookupManager.Qrz.LastError}";
            }

            string apiKey = _qrzLogbookApiKeyTb?.Text.Trim() ?? "";
            string keyResult = null;
            if (!string.IsNullOrEmpty(apiKey))
            {
                if (!IsDisposed) _qrzStatusLbl.Text = "Testing login… Testing Logbook API key…";
                var qrzLogbookClient = new QrzLogbookClient();
                bool keyOk = await qrzLogbookClient.TestApiKeyAsync(apiKey);
                keyResult = keyOk
                    ? "Logbook API key valid."
                    : $"Logbook API key error: {qrzLogbookClient.LastError}";
            }

            if (!IsDisposed)
            {
                _qrzStatusLbl.Text = keyResult == null ? loginResult : $"{loginResult}  {keyResult}";
                _qrzTestBtn.Enabled = true;
                _qrzStatusLbl.Focus();
            }
        }

        private async void LoTWUpdateBtn_Click(object sender, EventArgs e)
        {
            if (ctrl.lookupManager == null) return;
            _lotwUpdateBtn.Enabled = false;
            _lotwStatusLbl.Text   = "Downloading…";
            bool ok = await ctrl.lookupManager.LoTW.RefreshAsync();
            if (!IsDisposed)
            {
                _lotwStatusLbl.Text   = ok ? LoTWStatusText() : $"Error: {ctrl.lookupManager.LoTW.LastError}";
                _lotwUpdateBtn.Enabled = true;
                _lotwStatusLbl.Focus();
            }
        }

        private async void ClubLogUpdateBtn_Click(object sender, EventArgs e)
        {
            if (ctrl.lookupManager == null) return;
            ctrl.lookupManager.ClubLog.Configure(true, ClubLogAppKey.Resolve());
            _clubLogUpdateBtn.Enabled = false;
            _clubLogStatusLbl.Text   = "Downloading…";
            // Refresh both files -- clublog_cty.xml (Name/Adif/Continent/Deleted) and
            // the Big CTY alias/exception data (actual callsign->entity resolution).
            // LastError is shared by both calls on the same provider instance, so
            // capture each one immediately -- the second call's success would
            // otherwise clear the first call's failure message before it's read.
            bool okClubLog = await ctrl.lookupManager.ClubLog.RefreshAsync();
            string clubLogError = ctrl.lookupManager.ClubLog.LastError;
            bool okBigCty = await ctrl.lookupManager.ClubLog.RefreshBigCtyAsync();
            string bigCtyError = ctrl.lookupManager.ClubLog.LastError;
            if (!IsDisposed)
            {
                _clubLogStatusLbl.Text = (okClubLog && okBigCty)
                    ? ClubLogStatusText()
                    : $"Error: {(!okClubLog ? clubLogError : bigCtyError)}";
                _clubLogUpdateBtn.Enabled = true;
                _clubLogStatusLbl.Focus();
            }
        }

        private string FccUlsStatusText()
        {
            var m = ctrl.lookupManager;
            if (m == null || !m.FccUls.IsEnabled) return "FCC ULS lookup disabled.";
            if (!string.IsNullOrEmpty(m.FccUls.LastError)) return $"Error: {m.FccUls.LastError}";
            if (m.FccUls.RecordCount == 0) return "Not downloaded yet. Click Update Now.";
            var age = m.FccUls.LastUpdate == DateTime.MinValue ? "never" : m.FccUls.LastUpdate.ToLocalTime().ToString("g");
            return $"{m.FccUls.RecordCount:N0} callsigns, last updated {age}";
        }

        private async void FccUlsUpdateBtn_Click(object sender, EventArgs e)
        {
            if (ctrl.lookupManager == null) return;
            ctrl.lookupManager.FccUls.Configure(true);
            _fccUlsUpdateBtn.Enabled = false;
            _fccUlsStatusLbl.Text    = "Downloading (~170MB, may take a minute)…";
            bool ok = await ctrl.lookupManager.FccUls.RefreshAsync();
            if (!IsDisposed)
            {
                _fccUlsStatusLbl.Text    = ok ? FccUlsStatusText() : $"Error: {ctrl.lookupManager.FccUls.LastError}";
                _fccUlsUpdateBtn.Enabled = true;
                _fccUlsStatusLbl.Focus();
            }
        }
    }
}
