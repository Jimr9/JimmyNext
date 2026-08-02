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

        private bool startOnUdpTab;

        private const int HotkeysTabIndex      = 4;
        private const int AdvUiTabIndex        = 5;
        private const int WantedCallsTabIndex  = 6;
        private const int SpotWatchTabIndex    = 7;
        private const int SoundsTabIndex       = 8;
        private const int UdpTabIndex          = 9;
        private const int LookupTabIndex       = 10;

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

        public OptionsDlg(WsjtxClient wsjtxClient, Controller ctrl, bool startOnUdpTab = false)
        {
            InitializeComponent();

            this.wsjtxClient = wsjtxClient;
            this.ctrl = ctrl;
            this.startOnUdpTab = startOnUdpTab;

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

        public void SelectUdpTab()
        {
            tabControl1.SelectedIndex = UdpTabIndex;
        }

        private void OptionsDlg_Load(object sender, EventArgs e)
        {
            Screen screen = Screen.FromControl(ctrl);
            Location = new Point(
                screen.Bounds.X + (screen.Bounds.Width - Width) / 2,
                screen.Bounds.Y + (screen.Bounds.Height - Height) / 2);

            LoadUdpTab();
            BuildGeneralTab();
            BuildHotkeysTab();
            BuildAdvancedUiTab();
            BuildWantedCallsTab();
            BuildSpotWatchTab();
            BuildSoundsTab();
            BuildLogbookSyncTab();
            BuildLookupDataTab();
            BuildAppearanceTab();
            ReparentControlsToDialog();

            UpdateAllButtons();
            dxccButtonEnabled = false;  // Phase 3: New DXCC exclusive mode removed
            UpdateAllButtons();

            if (startOnUdpTab)
                tabControl1.SelectedIndex = UdpTabIndex;
            else
                subtitleLabel.Focus();
        }

        private void LoadUdpTab()
        {
            udpOverrideCheckBox.Checked = wsjtxClient.overrideUdpDetect;
            if (wsjtxClient.ipAddress != null) udpAddrTextBox.Text = wsjtxClient.ipAddress.ToString();
            if (wsjtxClient.port != 0) udpPortTextBox.Text = wsjtxClient.port.ToString();
            udpMulticastCheckBox.Checked = wsjtxClient.multicast;
            udpMulticastCheckBox_CheckedChanged(null, null);
            udpOnTopCheckBox.Checked = ctrl.alwaysOnTop;
            udpDiagLogCheckBox.Checked = wsjtxClient.diagLog;
            udpOverrideCheckBox_CheckedChanged(null, null);
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
            if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); return; }
            // When the capture box has focus, let the key pass through to it.
            if (IsCaptureFieldFocused()) return;
            if (e.Control && e.KeyCode == Keys.Q) Close();
        }

        // ===== OK / CANCEL =====

        private void okButton_Click(object sender, EventArgs e)
        {
            if (!ApplyUdpSettings()) return;
            if (!ValidateHotkeys()) return;
            ApplyGeneralSettings();
            SaveHotkeysTab();
            SaveAdvancedUiTab();
            SaveWantedCallsTab();
            SaveSpotWatchTab();
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

        private bool ApplyUdpSettings()
        {
            bool multicast = udpMulticastCheckBox.Checked;
            bool overrideUdp = udpOverrideCheckBox.Checked;
            UInt16 port;
            IPAddress ipAddress;

            if (!UInt16.TryParse(udpPortTextBox.Text, out port))
            {
                MessageBox.Show("A port number must be between 0 and 65535.\n\nExample: 2237",
                    wsjtxClient.pgmName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                tabControl1.SelectedIndex = UdpTabIndex;
                udpPortTextBox.Focus();
                return false;
            }

            var a = udpAddrTextBox.Text.Split('.');
            if (a.Length != 4)
            {
                string ex = multicast ? "239.255.0.0" : "127.0.0.1";
                MessageBox.Show($"An IP address must be 4 numbers between 0 and 255, each separated by a period.\n\nExample: {ex}",
                    wsjtxClient.pgmName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                tabControl1.SelectedIndex = UdpTabIndex;
                udpAddrTextBox.Focus();
                return false;
            }

            if (overrideUdp && multicast && (a[0] != "239" || a[1] != "255"))
            {
                MessageBox.Show("Multicast addresses must start with '239.255'.\n\nExample: 239.255.0.0",
                    wsjtxClient.pgmName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                tabControl1.SelectedIndex = UdpTabIndex;
                udpAddrTextBox.Focus();
                return false;
            }

            try
            {
                ipAddress = IPAddress.Parse(udpAddrTextBox.Text);
            }
            catch
            {
                string ex = multicast ? "239.255.0.0" : "127.0.0.1";
                MessageBox.Show($"An IP address must be 4 numbers between 0 and 255, each separated by a period.\n\nExample: {ex}",
                    wsjtxClient.pgmName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                tabControl1.SelectedIndex = UdpTabIndex;
                udpAddrTextBox.Focus();
                return false;
            }

            ctrl.alwaysOnTop = udpOnTopCheckBox.Checked;
            wsjtxClient.LogModeChanged(udpDiagLogCheckBox.Checked);

            if (wsjtxClient.ipAddress.ToString() == ipAddress.ToString() &&
                wsjtxClient.port == port &&
                wsjtxClient.multicast == multicast &&
                wsjtxClient.overrideUdpDetect == overrideUdp)
            {
                return true;
            }

            wsjtxClient.UpdateAddrPortMulti(ipAddress, port, multicast, overrideUdp);
            return true;
        }

        // ===== UDP TAB EVENT HANDLERS =====

        private void udpMulticastCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            udpAddrLabel.Text = udpMulticastCheckBox.Checked
                ? "(Standard: 239.255.0.0)"
                : "(Standard: 127.0.0.1)";
        }

        private void udpOverrideCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            udpAddrTextBox.Enabled = udpPortTextBox.Enabled = udpMulticastCheckBox.Enabled =
                udpOverrideCheckBox.Checked;
        }

        private void udpHelpButton_Click(object sender, EventArgs e)
        {
            new Thread(new ThreadStart(delegate
            {
                MessageBox.Show(
                    $"This information allows communication with WSJT-X.{Environment.NewLine}{Environment.NewLine}" +
                    $"- In the WSJT-X program, select File | Settings | Reporting.{Environment.NewLine}{Environment.NewLine}" +
                    $"- Enter the UDP server address (xxx.xxx.xxx.xxx) and port number shown there.{Environment.NewLine}{Environment.NewLine}" +
                    $"- Make sure 'Accept UDP requests' is enabled in WSJT-X.{Environment.NewLine}{Environment.NewLine}" +
                    $"Note: Select 'Multicast' here (entering the standard UDP address and port number) if other WSJT-X helper programs (loggers, maps, etc.) will be used.",
                    wsjtxClient.pgmName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            })).Start();
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

            // Select the first real action (index 0 is the "General Commands" header)
            _actionListBox.SelectedIndex = 1;
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

            // Group header: General Commands
            _actionListBox.Items.Add("General Commands");
            _listActionMap.Add(null);

            foreach (var a in generalActions)
            {
                _actionListBox.Items.Add("  " + HotkeyConfig.DisplayNames[a]);
                _listActionMap.Add(a);
            }

            // Group header: Accessibility Navigation
            _actionListBox.Items.Add("Accessibility Navigation");
            _listActionMap.Add(null);

            foreach (var a in navActions)
            {
                _actionListBox.Items.Add("  " + HotkeyConfig.DisplayNames[a]);
                _listActionMap.Add(a);
            }

            var bandActions = new HotkeyAction[]
            {
                HotkeyAction.Band160m,
                HotkeyAction.Band80m,
                HotkeyAction.Band60m,
                HotkeyAction.Band40m,
                HotkeyAction.Band30m,
                HotkeyAction.Band20m,
                HotkeyAction.Band17m,
                HotkeyAction.Band15m,
                HotkeyAction.Band12m,
                HotkeyAction.Band10m,
                HotkeyAction.Band6m,
            };

            // Group header: Direct Band Selection
            _actionListBox.Items.Add("Direct Band Selection");
            _listActionMap.Add(null);

            foreach (var a in bandActions)
            {
                _actionListBox.Items.Add("  " + HotkeyConfig.DisplayNames[a]);
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
                    tabControl1.SelectedIndex = HotkeysTabIndex;
                    return false;
                }
                if (seen.Contains(k))
                {
                    MessageBox.Show(
                        "Duplicate shortcut detected. Please correct the Hotkeys settings.",
                        ctrl.friendlyName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    tabControl1.SelectedIndex = HotkeysTabIndex;
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
            logbookSyncPanel.Controls.Add(qrzLogbookBox);
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
            var lotwLogbookBox = MakeGroupBox("LoTW Logbook Download", 175, 5, pw, 142, font);
            logbookSyncPanel.Controls.Add(lotwLogbookBox);
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

            // ── Club Log Logbook Upload ──────────────────────────────────────────
            // A per-user credential (Application Password), entirely separate from
            // the app-wide Club Log key used for country data (Lookup Data tab) -- see
            // ClubLogUploadClient.cs for why these cannot be the same credential.
            tabIdx = 1;
            var clUploadBox = MakeGroupBox("Club Log Logbook Upload", 175, 5, pw, 222, font);
            logbookSyncPanel.Controls.Add(clUploadBox);
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

            WireServiceList(serviceList, panels);
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
            lookupPanel.Controls.Add(qrzBox);
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
            lookupPanel.Controls.Add(lotwBox);
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
            lookupPanel.Controls.Add(clBox);
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
            lookupPanel.Controls.Add(fccBox);
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

            WireServiceList(serviceList, panels);
        }

        // Shows only the panel matching the current list selection, hiding the rest --
        // same idea as the Logbook window's Awards-tab combo-driven detail view, just
        // backed by a persistent ListBox instead of a dropdown.
        private static void WireServiceList(System.Windows.Forms.ListBox listBox, List<System.Windows.Forms.GroupBox> panels)
        {
            void UpdateVisibility()
            {
                for (int i = 0; i < panels.Count; i++)
                    panels[i].Visible = (i == listBox.SelectedIndex);
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
            ctrl.fccUlsEnabled           = _fccUlsEnabledCb?.Checked           ?? false;
            ctrl.fccUlsRefreshDays       = (int)(_fccUlsRefreshDaysNum?.Value   ?? 7);
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
