using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
        using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using WsjtxUdpLib;
using System.Net;
using System.Configuration;
using System.Threading;
using System.Media;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using System.Text.RegularExpressions;
using System.Net.Mail;


namespace WSJTX_Controller
{
    public partial class Controller : Form, IJimmyStatusView, IJimmyQueueView, IJimmyLogView
    {
        public WsjtxClient wsjtxClient;
        public OptionsDlg optionsDlg;
        public bool alwaysOnTop = false;
        public bool skipLevelPrompt = false;
        // Advanced Call Layout display flags now live in Settings (JimmySettings.cs) so
        // they're unit-testable outside a live Form. These are thin pass-through
        // properties, kept under the original field names so the ~65 existing call
        // sites across Controller/WsjtxClient/OptionsDlg/SupportReportBuilder are unaffected.
        public JimmySettings Settings = new JimmySettings();
        // Self-sufficiency plan, Phase 0: radio-backend settings (Hamlib/rigctld). Mode defaults
        // to WsjtxCat, so nothing reads/uses this yet -- Phase 1 wires a RigctldClient to it.
        public RadioSettings Radio = new RadioSettings();
        // Options > Decode tab: WSJT-X decode-related settings (Fast/Normal/Deep, F Low/F High,
        // Enable AP, AP-CQ-only, Single decode), ported over in Nexus but never previously
        // exposed to Jimmy. See DecodeSettings.cs.
        public DecodeSettings Decode = new DecodeSettings();
        // Options > Frequencies tab: per-band FT8/FT4 calling-frequency overrides. See
        // FrequencySettings.cs.
        public FrequencySettings Frequencies = new FrequencySettings();
        // Notification/accessibility architecture: INI-backed per-event-type policy (enable/
        // priority/dedup/throttle/wording). See WSJTX_Controller/Notify/ -- NotificationCenter
        // (constructed in WsjtxClient's constructor) is the only consumer of this.
        public NotificationSettings Notifications = new NotificationSettings();
        public bool advancedCallLayout { get => Settings.AdvancedCallLayout; set => Settings.AdvancedCallLayout = value; }
        public bool advShowTx1 { get => Settings.AdvShowTx1; set => Settings.AdvShowTx1 = value; }
        public bool advShowTx2 { get => Settings.AdvShowTx2; set => Settings.AdvShowTx2 = value; }
        public bool advShowRaw { get => Settings.AdvShowRaw; set => Settings.AdvShowRaw = value; }
        public bool showSpotWatch { get => Settings.ShowSpotWatch; set => Settings.ShowSpotWatch = value; }
        public bool rawShowCq = true;
        public bool rawShowDirected = true;
        public bool rawShowReports = true;
        public bool rawShowRR73 = false;
        public bool rawShow73 = false;
        public bool rawShowPota = true;
        public bool rawShowSota = true;
        public bool rawShowDx = true;
        public bool rawShowSnr = true;
        public bool rawShowGrid = true;
        public bool rawShowCountry = true;
        public bool rawShowDistAz = false;
        public bool rawOnlyCallsigns = false;
        public bool rawOnlyUnworked = false;
        public bool rawOnlyRanked = false;
        public bool rawPriorityTags = false;
        public bool rawNewestFirst = false;
        public int rawMaxRows = 100;
        public int maxQueuedCallsBase = 5;
        public int maxCallQueueAgePeriods = 16;
        // Extra grace period (ms) ShowStatus() waits AFTER a period's decode window is
        // confirmed done (WsjtxClient.cs's decodesProcessed, set from the real FT8/FT4
        // period clock) before actually announcing the "N available stations" summary --
        // gives one more moment for a last-instant straggler decode to be folded in. This
        // is not what makes the announcement wait for the period to finish -- that part
        // always happens and isn't optional (a mid-period partial count isn't useful to
        // anyone); this is just a small additional buffer on top of it. 0 = no buffer,
        // announce the moment the period is confirmed done.
        public int statusBatchDelayMs = 500;
        public bool keepTransmitListDuringTx = false;
        public bool keepListPositionDuringRefresh = false;
        public bool moveFocusToStatusOnCallSelect = false;
        public bool checkForUpdatesOnStartup = false;
        // Added 2026-08-19: gates UiaAlertNotificationDelivery (WSJTX_Controller/Notify/
        // NotificationDelivery.cs) -- when true, an Important-priority notification may also
        // announce via UI Automation's Notification event (RaiseAccessibleAlert) even while
        // keyboard focus is elsewhere, without moving focus, self-voicing, or calling JAWS/NVDA
        // directly. Off by default: live JAWS and NVDA testing is required before this should
        // ever default to on -- see the General tab's own checkbox wiring in
        // ApplyGeneralSettings (accessibility cleanup, 2026-08-19: the explanatory
        // AccessibleDescription this comment used to point to was removed from the checkbox
        // itself per a third-party audit -- its own accessible name already says what it does).
        public bool announceImportantAlertsWhenFocusElsewhere = false;

        // Sound settings: enabled flags and file paths for each sound event
        // CallAdded/CallingMe/Logged enabled state is controlled by existing checkboxes
        public bool   soundsEnabled         = true;
        public string soundFile_CallAdded   = "blip.wav";
        public string soundFile_CallingMe   = "trumpet.wav";
        public string soundFile_Logged      = "echo.wav";
        public bool   soundEnabled_TxEnabled     = true;
        public string soundFile_TxEnabled        = "beepbeep.wav";
        public bool   soundEnabled_Disconnected  = true;
        public string soundFile_Disconnected     = "dive.wav";
        public bool   soundEnabled_NewDxcc        = false;
        public string soundFile_NewDxcc           = "";
        public bool   soundEnabled_NewDxccOnBand  = false;
        public string soundFile_NewDxccOnBand     = "";
        public bool   soundEnabled_AlwaysWanted   = false;
        public string soundFile_AlwaysWanted      = "";
        public bool   soundEnabled_DirectedCq     = false;
        public string soundFile_DirectedCq        = "";
        public bool   soundEnabled_Pota           = false;
        public string soundFile_Pota              = "";
        public bool   soundEnabled_Sota           = false;
        public string soundFile_Sota              = "";
        public bool   soundEnabled_WantedAnywhere = false;
        public string soundFile_WantedAnywhere    = "";
        public bool   soundEnabled_OppositePeriod = false;
        public string soundFile_OppositePeriod    = "";
        public bool   soundEnabled_AwardNeeded    = false;
        public string soundFile_AwardNeeded       = "";

        // Feature flags
        public bool   wantedCallAnywhereEnabled   = true;

        // Weak-signal floor (Options > Receive / Auto Reply > Block List) — created and
        // reparented the same way as the other Receive / Auto Reply controls.
        public CheckBox      ignoreWeakSnrCheckBox;
        public NumericUpDown minSnrNumUpDown;
        public Label         minSnrLabel;
        public CheckBox      removeOnWeakSnrCheckBox;

        // Logbook credentials (loaded from ini; set from Options > Lookup / Data tab)
        public string qrzLogbookApiKey = "";
        public string lotwLogbookUser  = "";
        public string lotwLogbookPass  = "";

        // Logbook upload settings (loaded from ini; set from Options > Lookup / Data tab).
        // QRZ upload reuses qrzLogbookApiKey above -- same key QRZ uses for download.
        // Club Log upload needs its own per-user credentials (Application Password, not
        // the normal Club Log website login), separate from the app-wide Club Log key
        // used for read-only country data (see ClubLogAppKey.cs).
        public bool   qrzUploadEnabled       = false;
        public bool   qrzUploadRealtime      = false;
        // Default true -- unlike QRZ/Club Log (opt-in, need credentials), LoTW upload has
        // always fired unconditionally on the upload hotkey. Defaulting true here preserves
        // that existing behavior for everyone; only someone who doesn't use LoTW needs to
        // uncheck it (Options > Lookup) to stop WSJT-X reporting an error on that keypress.
        public bool   lotwUploadEnabled      = true;
        public string tqslStationLocation    = "";
        // Which entry in directedTextBox's space-separated list (e.g. "POTA SOTA") the Call
        // CQ dialog should use every time, instead of Jimmy's default random rotation. Empty
        // means "Random" -- NextDirCq() falls back to rotating through all entries whenever
        // this is blank or no longer matches one of them (e.g. the text box was edited since).
        public string directedCqLockedEntry  = "";
        public bool   clubLogUploadEnabled   = false;
        public bool   clubLogUploadRealtime  = false;
        public string clubLogUploadEmail     = "";
        public string clubLogUploadPassword  = "";
        public string clubLogUploadCallsign  = "";
        // HRDLog.net (self-sufficiency plan, Phase 2) -- same shape as the Club Log fields
        // above. hrdLogUploadCode is the account's HRDLog upload code (a secret, DPAPI-protected
        // like clubLogUploadPassword), not the account login password.
        public bool   hrdLogUploadEnabled    = false;
        public bool   hrdLogUploadRealtime   = false;
        public string hrdLogUploadCode       = "";
        public string hrdLogUploadCallsign   = "";
        // eQSL / HamQTH (release-candidate pass: exposed through EngineHost, reusing Nexus's own
        // mature transports -- see EngineHost/src/external_data.rs). Same credential shape as
        // Club Log above (account username + DPAPI-protected password); the operator supplies
        // their own account, never a shared application credential.
        public bool   eqslUploadEnabled      = false;
        public bool   eqslUploadRealtime     = false;
        public string eqslUsername           = "";
        public string eqslPassword           = "";
        public bool   hamQthEnabled          = false;
        public string hamQthUsername         = "";
        public string hamQthPassword         = "";
        public int    hamQthCacheDays        = 7;
        // Which online service is the PRIMARY automatic callsign lookup provider -- see
        // LookupManager.CallsignLookupProvider's own comment. Default Qrz matches every
        // existing installation's behavior unchanged.
        public CallsignLookupProvider callsignLookupProvider = CallsignLookupProvider.Qrz;

        // DX Spots (Alt+G window, DX Cluster tab): "host:port" of an operator-chosen DX-
        // cluster/RBN telnet node. Empty (default) disables that tab's feed entirely -- unlike
        // PSK Reporter's single public broker, DX clusters are an independently-run federation
        // with no universal default. Startup-CLI-arg-only (NativeEngineClient.Launch), like the
        // Decode tab's non-live-settable options -- changing it needs the usual engine restart.
        public string dxClusterAddress = "";

        // Automatic logbook download/sync (opt-in, default off so existing users aren't
        // suddenly downloading full logbooks on their next update without asking). Runs
        // once per session via LogbookAutoSync, a fixed delay after reaching ACTIVE --
        // see logbookAutoSyncTimer / OnJimmyReachedActive.
        public bool qrzLogbookAutoSyncEnabled      = false;
        public int  qrzLogbookRefreshDays          = 7;
        public bool lotwLogbookAutoSyncEnabled     = false;
        public int  lotwLogbookRefreshDays         = 7;
        public bool clubLogLogbookAutoSyncEnabled  = false;
        public int  clubLogLogbookRefreshDays      = 7;
        private System.Windows.Forms.Timer logbookAutoSyncTimer;

        private LogbookWindow _logbookWindow;
        private System.Windows.Forms.Button logbookButton;
        private OtaSpotsWindow _otaSpotsWindow;
        private System.Windows.Forms.Button otaSpotsButton;
        public System.Windows.Forms.Button callCqOptionsButton;

        // Ids of the Rule Definitions checked for live FT8 tagging in the Logbook window's
        // Still Need tab, persisted so tagging survives across sessions and works even before
        // the Logbook window has been opened. Empty = none actively tracked. Several awards
        // can be tracked at once (see RefreshStillNeedCache()).
        public HashSet<string> activeAwardRuleIds = new HashSet<string>();

        // DX Spot Watch: tracks last-seen band/time/spotter for a user-curated callsign list
        // via the PSKReporter MQTT feed. See spotWatchCalls (WsjtxClient) for the watch list.
        private DxSpotWatcher dxSpotWatcher;

        // Self-sufficiency plan, Phase 4g: Jimmy's own native FT8 engine host. Null in a
        // replay-test session (TestModeGuard.IsTestMode) -- ApplyEngineMode() never spawns the
        // real process there.
        public NativeEngineClient nativeEngineClient;
        public NativeEngineSettings NativeEngine = new NativeEngineSettings();
        public List<string> spotWatchRowOrderFields;
        // "callsign" (alphabetical, default), "evenodd", or "snr".
        public string spotWatchSortKey = "callsign";

        // Lookup / Data settings
        public LookupManager    lookupManager;
        public bool             useLookupData           = false;
        public bool             qrzEnabled              = false;
        public string           qrzUsername             = "";
        public string           qrzPassword             = "";
        public int              qrzCacheDays            = 7;
        public QrzLookupPolicy  qrzLookupPolicy         = QrzLookupPolicy.Disabled;
        public int              qrzMinIntervalSeconds   = 10;
        public bool             lotwEnabled             = false;
        public bool             lotwBoostEnabled        = false;
        public int              lotwRefreshDays         = 30;
        // No clubLogEnabled/clubLogApiKey fields: Club Log country data is
        // automatic Jimmy infrastructure, not a user-facing toggle or a
        // per-user credential -- the key is Jimmy's application key
        // (ClubLogAppKey.Resolve()) and downloads happen unconditionally,
        // subject only to the refresh interval below. See RuleUniverse.cs.
        public int              clubLogRefreshDays      = 30;
        // Opt-in (default off) since the full download is ~170MB -- unlike Club
        // Log's small country file, this isn't unconditional background infrastructure.
        public bool             fccUlsEnabled           = false;
        public int              fccUlsRefreshDays       = 7;

        private bool formLoaded = false;
        private HelpDlg helpDlg = null;
        private Control _helpReturnFocus = null;
        private IniFile iniFile = null;
        public HotkeyConfig hotkeyConfig;
        private int minSkipCount = 1;
        private const int maxSkipCount = 20;
        private const string separateBySpaces = "(separate by spaces)";
        public string friendlyName = "";
        private MouseEventArgs mouseEventArgs;
        private int listBoxClickCount;
        private bool ignoreDirectedChange = false;
        private string helpSuffix = " Help";
        private bool ignoreExceptChange = false;
        private bool _suppressIntentSync = false;

        private System.Windows.Forms.Timer mainLoopTimer;

        public System.Windows.Forms.Timer statusMsgTimer;
        public System.Windows.Forms.Timer initialConnFaultTimer;
        public System.Windows.Forms.Timer debugHighlightTimer;
        public System.Windows.Forms.Timer guideTimer;
        public System.Windows.Forms.Timer callListBoxClickTimer;
        public System.Windows.Forms.Timer helpTimer;
        public System.Windows.Forms.Timer spotWatchAgeTimer;

        private string nl = Environment.NewLine;
        private static string alphaOnly = "[^A-Za-z]";         //match if any numeric
        private static string numericOnly = "[^0-9]";          //match if any alpha


        public Controller()
        {
            InitializeComponent();
            KeyPreview = true;

            //timers
            mainLoopTimer = new System.Windows.Forms.Timer();
            mainLoopTimer.Tick += new System.EventHandler(mainLoopTimer_Tick);
            statusMsgTimer = new System.Windows.Forms.Timer();
            statusMsgTimer.Interval = 5000;
            statusMsgTimer.Tick += new System.EventHandler(statusMsgTimer_Tick);
            initialConnFaultTimer = new System.Windows.Forms.Timer();
            initialConnFaultTimer.Tick += new System.EventHandler(initialConnFaultTimer_Tick);
            debugHighlightTimer = new System.Windows.Forms.Timer();
            debugHighlightTimer.Tick += new System.EventHandler(debugHighlightTimer_Tick);
            guideTimer = new System.Windows.Forms.Timer();
            guideTimer.Interval = 20;
            guideTimer.Tick += new System.EventHandler(guideTimer_Tick);
            callListBoxClickTimer = new System.Windows.Forms.Timer();
            callListBoxClickTimer.Interval = 250;
            callListBoxClickTimer.Tick += new System.EventHandler(callListBoxClickTimer_Tick);
            helpTimer = new System.Windows.Forms.Timer();
            helpTimer.Interval = 20;
            helpTimer.Tick += new System.EventHandler(helpTimer_Tick);
            spotWatchAgeTimer = new System.Windows.Forms.Timer();
            spotWatchAgeTimer.Interval = 60000;
            spotWatchAgeTimer.Tick += new System.EventHandler(spotWatchAgeTimer_Tick);
        }

#if DEBUG
        //project type must be Console application for this to work

        [DllImport("Kernel32.dll")]
        static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
#endif

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessIdUnused);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        // internal (not private): WsjtxClient.ToggleTxFirst reuses this for the same
        // SendKeys-must-not-target-the-wrong-window guard other status-announce call sites use
        // (see ShowMsg/RenderStatus's own comments, 2026-08-19 release-blocker follow-up) --
        // one P/Invoke wrapper, not a duplicate.
        internal bool IsJimmyForegrounded() => GetForegroundWindow() == this.Handle;

        // In Debug builds, AllocConsole() (below, in Form_Load) creates -- and, when hidden,
        // briefly activates -- a console window before this form is ever shown. That spends
        // Windows' one automatic foreground-activation grant for this process launch, so by
        // the time Jimmy's own window appears, a plain SetForegroundWindow() often fails
        // silently (Windows just flashes the taskbar entry) and real keyboard input keeps
        // going to whatever had focus before Jimmy launched (e.g. the Explorer window it was
        // started from) until the user manually Alt+Tabs -- reported/confirmed 2026-07-10,
        // Debug-only (Release has no console/AllocConsole, so it doesn't hit this).
        // Attaching this thread's input queue to the current foreground thread's queue is the
        // standard Win32 workaround: Windows then treats the two threads as cooperating, which
        // allows SetForegroundWindow to actually succeed.
        private void ForceForeground()
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == this.Handle) return;

            uint foregroundThreadId = GetWindowThreadProcessId(foreground, IntPtr.Zero);
            uint thisThreadId = GetCurrentThreadId();

            bool attached = foregroundThreadId != thisThreadId
                && foregroundThreadId != 0
                && AttachThreadInput(thisThreadId, foregroundThreadId, true);
            try
            {
                SetForegroundWindow(this.Handle);
            }
            finally
            {
                if (attached) AttachThreadInput(thisThreadId, foregroundThreadId, false);
            }
        }

        // ResolveUdpListenAddress (and the ipAddrStr/ipAddress/multicast settings it resolved --
        // Properties.Settings.Default.ipAddress/multicast, the "ipAddress"/"multicast" .ini
        // keys) were removed 2026-08-18: this was the classic WSJT-X/UDP transport's own listen
        // address, exclusively consumed by WsjtxClient.Protocol.cs's ConnectNativeEngine/UdpLoop
        // (removed in the prior UDP-to-Direct test-harness migration pass) and by
        // WsjtxProtocolAdapter (removed this same pass) -- nothing parses or reads an IP address
        // for Jimmy's own transport at all anymore, in production or in test mode, so the real
        // fresh-install crash this used to guard against (IPAddress.Parse(null) throwing
        // ArgumentNullException when nothing was configured yet) is now structurally impossible
        // rather than merely handled: there is no more IPAddress.Parse call anywhere on this
        // data path to crash. Its own regression test (JimmyTests.cs's
        // ResolveUdpListenAddressTests) was removed alongside it for the same reason.

        private void Form_Load(object sender, EventArgs e)
        {
            //use .ini file for settings (avoid .Net config file mess)
            string pgmName = Assembly.GetExecutingAssembly().GetName().Name.ToString();
            string path = $"{Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}\\{pgmName}";
            string pathFileNameExt = path + "\\" + pgmName + ".ini";
            // Production-safety isolation (urgent fix, 2026-08-13): Properties.Settings.Default
            // is the legacy .NET user.config-backed settings store, predating the .ini file
            // system below. Its on-disk path is derived from assembly Product/Company identity
            // and evidence, NOT from pgmName the way every other persistence path in this app
            // deliberately is -- so unlike the .ini file, the logbook/lookup DataRoot, and the
            // diagnostic log (all keyed off Assembly.GetExecutingAssembly().GetName().Name),
            // there is no guarantee this legacy store is isolated per build identity. A
            // side-by-side "Jimmy Test" build has no legitimate reason to read it at all -- it
            // should always start from clean defaults, never inherit another install's history --
            // so every read of Properties.Settings.Default below is now gated on this being the
            // real production identity specifically, not just "any .ini-less first run".
            bool isProductionIdentity = pgmName == "Jimmy";
            List<string> parsedCallWaitingRowOrder = null;
            List<string> parsedRawDecodeRowOrder = null;
            hotkeyConfig = new HotkeyConfig();
            try
            {
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                iniFile = new IniFile(pathFileNameExt);
                hotkeyConfig.LoadFromIni(iniFile);
                RefreshHotkeyAccessibleNames();
                // Parse optional row-order settings from INI (INI-only settings). Stored in
                // local variables and assigned to wsjtxClient after it's constructed.
                try
                {
                    parsedCallWaitingRowOrder = ParseRowOrder(iniFile.Read("callWaitingRowOrder"),
                        RowDisplayOrderDlg.CallWaitingDefaultFields);
                    parsedRawDecodeRowOrder = ParseRowOrder(iniFile.Read("rawDecodeRowOrder"),
                        RowDisplayOrderDlg.RawDecodeDefaultFields);
                    spotWatchRowOrderFields = ParseRowOrder(iniFile.Read("spotWatchRowOrder"),
                        RowDisplayOrderDlg.SpotWatchDefaultFields) ?? new List<string>(RowDisplayOrderDlg.SpotWatchDefaultFields);
                    if (iniFile.KeyExists("spotWatchSortKey"))
                        spotWatchSortKey = iniFile.Read("spotWatchSortKey");
                }
                catch
                {
                    // swallow parse errors and leave both parsed*RowOrder null
                }
            }
            catch
            {
                MessageBox.Show("Unable to create settings file: " + pathFileNameExt + $"{nl}Continuing with default settings...", friendlyName, MessageBoxButtons.OK);
            }

            int port = 0;
            bool debug = false;
            bool diagLog = false;
            WsjtxClient.TxModes txMode = WsjtxClient.TxModes.CALL_CQ;
            int offsetHiLimit = -1;
            int offsetLoLimit = -1;
            bool useRR73 = false;
            bool mode = true;
            string myContinent = null;
            bool newOnBand = true;
            bool cmdPrompts = true;
            bool usePskReporter = true;
            friendlyName = pgmName;

            //control defaults
            periodComboBox.SelectedIndex = 2;
            int rankMethodIdx = (int)WsjtxClient.RankMethods.MOST_RECENT;
            freqCheckBox.Checked = false;
            string rankOrderStr = null;
            string rankBeamStr = null;
            string categoryWeightsStr = null;
            string callingPrioritiesStr = null;
            string categoryDisabledStr = null;  // legacy migration only
            string wantedCallsStr = null;
            string spotWatchCallsStr = null;

            if (iniFile == null || !iniFile.KeyExists("firstRun"))     //.ini file not written yet, read properties (possibly set defaults)
            {
                if (isProductionIdentity)
                {
                    debug = Properties.Settings.Default.debug;
                    if (Properties.Settings.Default.windowPos != new Point(0, 0))
                        this.Location = Properties.Settings.Default.windowPos;
                    if (Properties.Settings.Default.windowHt != 0)
                        this.Height = Properties.Settings.Default.windowHt;
                    port = Properties.Settings.Default.port;
                    timeoutNumUpDown.Value = Properties.Settings.Default.timeout;
                    directedTextBox.Text = Properties.Settings.Default.directeds;
                    callDirCqCheckBox.Checked = Properties.Settings.Default.useDirected;
                    mycallCheckBox.Checked = Properties.Settings.Default.playMyCall;
                    loggedCheckBox.Checked = Properties.Settings.Default.playLogged;
                    alertTextBox.Text = Properties.Settings.Default.alertDirecteds;
                    replyDirCqCheckBox.Checked = Properties.Settings.Default.useAlertDirected;
                    logEarlyCheckBox.Checked = Properties.Settings.Default.logEarly;
                    alwaysOnTop = Properties.Settings.Default.alwaysOnTop;
                    useRR73 = Properties.Settings.Default.useRR73;
                    skipGridCheckBox.Checked = Properties.Settings.Default.skipGrid;
                    diagLog = Properties.Settings.Default.diagLog;
                    callAddedCheckBox.Checked = Properties.Settings.Default.playCallAdded;
                    replyLocalCheckBox.Checked = Properties.Settings.Default.enableReplyLocal;
                    replyDxCheckBox.Checked = Properties.Settings.Default.enableReplyDx;
                    freqCheckBox.Checked = Properties.Settings.Default.bestOffset;
                    replyRR73CheckBox.Checked = Properties.Settings.Default.replyRR73;
                    newOnBand = Properties.Settings.Default.newOnBand;
                    cmdPrompts = Properties.Settings.Default.cmdPrompts;
                    usePskReporter = Properties.Settings.Default.usePskReporter;
                }
                // Jimmy Test (or any other non-production identity) falls through with the
                // plain C# defaults already declared above (newOnBand=true, cmdPrompts=true,
                // usePskReporter=true, etc.) and whatever Designer.cs set each control's
                // Checked/Text to -- a genuinely clean first run, not production's history.
                bandComboBox.SelectedIndex = newOnBand ? 1 : 0;
                optimizeCheckBox.Checked = true;
                callNonDirCqCheckBox.Checked = true;
                showUsStateCheckBox.Checked = true;

            }
            else        //read settings from .ini file (avoid .Net config file mess)
            {
                debug = iniFile.Read("debug") == "True";

                int x;
                int.TryParse(iniFile.Read("windowPosX"), out x);
                int y;
                int.TryParse(iniFile.Read("windowPosY"), out y);
                //check all screens, extended screen may not be present
                var screens = System.Windows.Forms.Screen.AllScreens;
                bool found = false;
                Rectangle matchedScreenBounds = Screen.PrimaryScreen.Bounds;
                for (int scnIdx = 0; scnIdx < screens.Length; scnIdx++)
                {
                    var screenBounds = screens[scnIdx].Bounds;
                    var centerPt = new Point(x + (this.Width / 2), y + (this.Height / 2));
                    if (screenBounds.Contains(centerPt))
                    {
                        found = true;       //found screen for window posn
                        matchedScreenBounds = screenBounds;
                        break;
                    }
                }
                if (!found)     //default window posn
                {
                    x = 0;
                    y = 0;
                    matchedScreenBounds = Screen.PrimaryScreen.Bounds;
                }
                this.Location = new Point(x, y);
                int i;
                int w;
                int.TryParse(iniFile.Read("windowWd"), out w);
                int.TryParse(iniFile.Read("windowHt"), out i);
                // Clamp to the matched screen and today's safe minimum, so a saved size
                // from a different/larger monitor can't leave the window unusable.
                if (w > 0) this.Width  = Math.Max(this.MinimumSize.Width,  Math.Min(w, matchedScreenBounds.Width));
                if (i > 0) this.Height = Math.Max(this.MinimumSize.Height, Math.Min(i, matchedScreenBounds.Height));

                if (iniFile.Read("windowState") == "Maximized")
                    this.WindowState = FormWindowState.Maximized;

                // ipAddrStr/ipAddress/multicast (the classic UDP transport's own listen address)
                // reads removed 2026-08-18 -- see ResolveUdpListenAddress's own removal comment
                // above. port's own parsing/fallback behavior is otherwise unchanged.
                try
                {
                    port = int.Parse(iniFile.Read("port"));
                }
                catch (Exception)
                {
                    // Same isolation rule as the first-run migration block above: production
                    // alone may fall back to the legacy Properties.Settings.Default store.
                    // Every other identity (Jimmy Test included) falls back to WSJT-X's own
                    // standard UDP defaults instead (matching App.config's own default values),
                    // never another install's possibly-different saved settings.
                    port = isProductionIdentity ? Properties.Settings.Default.port : 2237;
                }

                int.TryParse(iniFile.Read("timeout"), out i);
                timeoutNumUpDown.Value = i;
                directedTextBox.Text = iniFile.Read("directeds");
                callDirCqCheckBox.Checked = iniFile.Read("useDirected") == "True";
                if (iniFile.KeyExists("directedCqLockedEntry")) directedCqLockedEntry = iniFile.Read("directedCqLockedEntry");
                mycallCheckBox.Checked = iniFile.Read("playMyCall") != "False";
                loggedCheckBox.Checked = iniFile.Read("playLogged") != "False";
                callAddedCheckBox.Checked = iniFile.Read("playCallAdded") != "False";
                alertTextBox.Text = iniFile.Read("alertDirecteds");
                replyDirCqCheckBox.Checked = iniFile.Read("useAlertDirected") == "True";
                logEarlyCheckBox.Checked = iniFile.Read("logEarly") == "True";
                alwaysOnTop = iniFile.Read("alwaysOnTop") == "True";
                useRR73 = iniFile.Read("useRR73") == "True";
                skipGridCheckBox.Checked = iniFile.Read("skipGrid") == "True";
                replyDxCheckBox.Checked = iniFile.Read("enableReplyDx") != "False";     //default: true
                diagLog = iniFile.Read("diagLog") == "True";
                freqCheckBox.Checked = iniFile.Read("bestOffset") == "True";
                replyRR73CheckBox.Checked = iniFile.Read("replyRR73") == "True";
                cmdPrompts = iniFile.Read("cmdPrompts") != "False";     //default: true

                //start of .ini-file-only settings (not in .Net config)
                // mode (txMode startup) always defaults to LISTEN; not persisted across sessions
                if (iniFile.KeyExists("offsetHiLimit")) int.TryParse(iniFile.Read("offsetHiLimit"), out offsetHiLimit);
                if (iniFile.KeyExists("offsetLoLimit")) int.TryParse(iniFile.Read("offsetLoLimit"), out offsetLoLimit);
                replyLocalCheckBox.Checked = iniFile.Read("enableReplyLocal") != "False";     //default
                optimizeCheckBox.Checked = iniFile.Read("optimizeTx") == "True";
                exceptTextBox.Text = iniFile.Read("exceptCalls");
                callCqDxCheckBox.Checked = iniFile.Read("callCqDx") == "True";
                ignoreNonDxCheckBox.Checked = iniFile.Read("ignoreNonDx") == "True";
                callNonDirCqCheckBox.Checked = iniFile.Read("callNonDirCq") == "True";
                skipLevelPrompt = iniFile.Read("skipLevelPrompt") == "True";
                cqOnlyRadioButton.Checked = iniFile.Read("cqOnly") != "False";              //default: true
                newOnBand = iniFile.Read("newOnBand") != "False";      //default: true
                bandComboBox.SelectedIndex = newOnBand ? 1 : 0;
                if (iniFile.KeyExists("myContinent")) myContinent = iniFile.Read("myContinent");    //required to be null if not set
                // Stage A6 emergency rollback valve (Classification/ClassificationCutover.cs) --
                // intentionally undocumented/not exposed in OptionsDlg; default true (new
                // ClassificationEngine-computed path). Set useClassificationEngine=False by
                // hand-editing the .ini file only if a real-world edge case surfaces.
                if (iniFile.KeyExists("useClassificationEngine")) ClassificationCutover.UseClassificationEngine = iniFile.Read("useClassificationEngine") != "False";
                // TEMPORARY developer diagnostic (Classification/ClassificationParityLogger.cs) --
                // intentionally undocumented/not exposed in OptionsDlg; default false (disabled).
                // Set logClassificationParityMismatches=True by hand-editing the .ini file only
                // to collect field-verification evidence for Stage A6; remove once confirmed.
                if (iniFile.KeyExists("logClassificationParityMismatches")) ClassificationParityLogger.Enabled = iniFile.Read("logClassificationParityMismatches") == "True";
                NativeEngine.LoadFromIni(iniFile);
                Radio.LoadFromIni(iniFile);
                Decode.LoadFromIni(iniFile);
                Frequencies.LoadFromIni(iniFile);
                Notifications.LoadFromIni(iniFile);
                if (iniFile.KeyExists("rankMethod")) int.TryParse(iniFile.Read("rankMethod"), out rankMethodIdx);
                if (iniFile.KeyExists("rankOrder")) rankOrderStr = iniFile.Read("rankOrder");
                if (iniFile.KeyExists("rankBeam")) rankBeamStr = iniFile.Read("rankBeam");
                if (iniFile.KeyExists("categoryWeights"))   categoryWeightsStr   = iniFile.Read("categoryWeights");
                if (iniFile.KeyExists("callingPriorities")) callingPrioritiesStr = iniFile.Read("callingPriorities");
                else if (iniFile.KeyExists("categoryDisabled")) categoryDisabledStr = iniFile.Read("categoryDisabled"); // migrate from old setting
                if (iniFile.KeyExists("wantedCalls"))       wantedCallsStr       = iniFile.Read("wantedCalls");
                if (iniFile.KeyExists("spotWatchCalls"))    spotWatchCallsStr    = iniFile.Read("spotWatchCalls");
                if (iniFile.KeyExists("wantedCallAnywhereEnabled")) wantedCallAnywhereEnabled = iniFile.Read("wantedCallAnywhereEnabled") == "True";
                rawPriorityTags = iniFile.Read("rawPriorityTags") == "True";
                cqGridRadioButton.Checked = iniFile.Read("cqGrid") == "True";
                anyMsgRadioButton.Checked = iniFile.Read("anyMsg") == "True";
                if (iniFile.KeyExists("txPeriodIdx"))
                {
                    int.TryParse(iniFile.Read("txPeriodIdx"), out i);
                    periodComboBox.SelectedIndex = i;
                }
                usePskReporter = iniFile.Read("usePskReporter") != "False";              //default: true
                showUsStateCheckBox.Checked = iniFile.Read("showUsState") == "True";
                Settings.LoadFromIni(iniFile);
                rawShowCq = iniFile.Read("rawShowCq") != "False";
                rawShowDirected = iniFile.Read("rawShowDirected") != "False";
                rawShowReports = iniFile.Read("rawShowReports") != "False";
                rawShowRR73 = iniFile.Read("rawShowRR73") == "True";
                rawShow73 = iniFile.Read("rawShow73") == "True";
                rawShowPota = iniFile.Read("rawShowPota") != "False";
                rawShowSota = iniFile.Read("rawShowSota") != "False";
                rawShowDx = iniFile.Read("rawShowDx") != "False";
                rawShowSnr = iniFile.Read("rawShowSnr") != "False";
                rawShowGrid = iniFile.Read("rawShowGrid") != "False";
                rawShowCountry = iniFile.Read("rawShowCountry") != "False";
                rawShowDistAz = iniFile.Read("rawShowDistAz") == "True";
                rawOnlyCallsigns = iniFile.Read("rawOnlyCallsigns") == "True";
                rawOnlyUnworked = iniFile.Read("rawOnlyUnworked") == "True";
                rawOnlyRanked = iniFile.Read("rawOnlyRanked") == "True";
                rawNewestFirst = iniFile.Read("rawNewestFirst") == "True";
                int rawMax;
                if (iniFile.KeyExists("rawMaxRows") && int.TryParse(iniFile.Read("rawMaxRows"), out rawMax) && rawMax >= 10 && rawMax <= 5000)
                    rawMaxRows = rawMax;
                int maxQueued;
                if (iniFile.KeyExists("maxQueuedCalls") && int.TryParse(iniFile.Read("maxQueuedCalls"), out maxQueued) && maxQueued >= 4 && maxQueued <= 100)
                    maxQueuedCallsBase = maxQueued;
                int maxAgePeriods;
                if (iniFile.KeyExists("maxCallQueueAgePeriods") && int.TryParse(iniFile.Read("maxCallQueueAgePeriods"), out maxAgePeriods) && maxAgePeriods >= 4 && maxAgePeriods <= 200)
                    maxCallQueueAgePeriods = maxAgePeriods;
                int statusBatchMs;
                if (iniFile.KeyExists("statusBatchDelayMs") && int.TryParse(iniFile.Read("statusBatchDelayMs"), out statusBatchMs) && statusBatchMs >= 0 && statusBatchMs <= 5000)
                    statusBatchDelayMs = statusBatchMs;
                keepTransmitListDuringTx = iniFile.Read("keepTransmitListDuringTx") == "True";
                keepListPositionDuringRefresh = iniFile.Read("keepListPositionDuringRefresh") == "True";
                moveFocusToStatusOnCallSelect = iniFile.Read("moveFocusToStatusOnCallSelect") == "True";
                checkForUpdatesOnStartup = iniFile.Read("checkForUpdatesOnStartup") == "True";
                announceImportantAlertsWhenFocusElsewhere = iniFile.Read("announceImportantAlertsWhenFocusElsewhere") == "True";

                // Sound settings: migrate old enabled keys for backward compat
                // Enabled state for CallAdded/CallingMe/Logged already read above from playCallAdded/playMyCall/playLogged
                if (iniFile.KeyExists("soundFile_CallAdded"))  soundFile_CallAdded  = iniFile.Read("soundFile_CallAdded");
                if (iniFile.KeyExists("soundFile_CallingMe"))  soundFile_CallingMe  = iniFile.Read("soundFile_CallingMe");
                if (iniFile.KeyExists("soundFile_Logged"))     soundFile_Logged     = iniFile.Read("soundFile_Logged");
                if (iniFile.KeyExists("soundEnabled_TxEnabled"))    soundEnabled_TxEnabled    = iniFile.Read("soundEnabled_TxEnabled") != "False";
                if (iniFile.KeyExists("soundFile_TxEnabled"))       soundFile_TxEnabled       = iniFile.Read("soundFile_TxEnabled");
                if (iniFile.KeyExists("soundEnabled_Disconnected")) soundEnabled_Disconnected = iniFile.Read("soundEnabled_Disconnected") != "False";
                if (iniFile.KeyExists("soundFile_Disconnected"))    soundFile_Disconnected    = iniFile.Read("soundFile_Disconnected");
                if (iniFile.KeyExists("soundEnabled_NewDxcc"))       soundEnabled_NewDxcc       = iniFile.Read("soundEnabled_NewDxcc") == "True";
                if (iniFile.KeyExists("soundFile_NewDxcc"))          soundFile_NewDxcc          = iniFile.Read("soundFile_NewDxcc");
                if (iniFile.KeyExists("soundEnabled_NewDxccOnBand")) soundEnabled_NewDxccOnBand = iniFile.Read("soundEnabled_NewDxccOnBand") == "True";
                if (iniFile.KeyExists("soundFile_NewDxccOnBand"))    soundFile_NewDxccOnBand    = iniFile.Read("soundFile_NewDxccOnBand");
                if (iniFile.KeyExists("soundEnabled_AlwaysWanted"))  soundEnabled_AlwaysWanted  = iniFile.Read("soundEnabled_AlwaysWanted") == "True";
                if (iniFile.KeyExists("soundFile_AlwaysWanted"))     soundFile_AlwaysWanted     = iniFile.Read("soundFile_AlwaysWanted");
                if (iniFile.KeyExists("soundEnabled_DirectedCq"))    soundEnabled_DirectedCq    = iniFile.Read("soundEnabled_DirectedCq") == "True";
                if (iniFile.KeyExists("soundFile_DirectedCq"))       soundFile_DirectedCq       = iniFile.Read("soundFile_DirectedCq");
                if (iniFile.KeyExists("soundEnabled_Pota"))          soundEnabled_Pota          = iniFile.Read("soundEnabled_Pota") == "True";
                if (iniFile.KeyExists("soundFile_Pota"))             soundFile_Pota             = iniFile.Read("soundFile_Pota");
                if (iniFile.KeyExists("soundEnabled_Sota"))           soundEnabled_Sota           = iniFile.Read("soundEnabled_Sota") == "True";
                if (iniFile.KeyExists("soundFile_Sota"))              soundFile_Sota              = iniFile.Read("soundFile_Sota");
                if (iniFile.KeyExists("soundEnabled_WantedAnywhere")) soundEnabled_WantedAnywhere = iniFile.Read("soundEnabled_WantedAnywhere") == "True";
                if (iniFile.KeyExists("soundFile_WantedAnywhere"))    soundFile_WantedAnywhere    = iniFile.Read("soundFile_WantedAnywhere");
                if (iniFile.KeyExists("soundEnabled_OppositePeriod")) soundEnabled_OppositePeriod = iniFile.Read("soundEnabled_OppositePeriod") == "True";
                if (iniFile.KeyExists("soundFile_OppositePeriod"))    soundFile_OppositePeriod    = iniFile.Read("soundFile_OppositePeriod");
                if (iniFile.KeyExists("soundEnabled_AwardNeeded"))    soundEnabled_AwardNeeded    = iniFile.Read("soundEnabled_AwardNeeded") == "True";
                if (iniFile.KeyExists("soundFile_AwardNeeded"))       soundFile_AwardNeeded       = iniFile.Read("soundFile_AwardNeeded");
                if (iniFile.KeyExists("soundsEnabled"))               soundsEnabled               = iniFile.Read("soundsEnabled") != "False";

                // Lookup / Data settings
                if (iniFile.KeyExists("useLookupData"))      useLookupData      = iniFile.Read("useLookupData") == "True";
                if (iniFile.KeyExists("qrzEnabled"))         qrzEnabled         = iniFile.Read("qrzEnabled")    == "True";
                if (iniFile.KeyExists("qrzUsername"))        qrzUsername        = iniFile.Read("qrzUsername");
                if (iniFile.KeyExists("qrzPassword"))        qrzPassword        = CredentialProtector.Unprotect(iniFile.Read("qrzPassword"));
                int qrzcd; if (iniFile.KeyExists("qrzCacheDays")    && int.TryParse(iniFile.Read("qrzCacheDays"),    out qrzcd)   && qrzcd   >= 1) qrzCacheDays    = qrzcd;
                int qrzpol; if (iniFile.KeyExists("qrzLookupPolicy") && int.TryParse(iniFile.Read("qrzLookupPolicy"), out qrzpol)) qrzLookupPolicy = (QrzLookupPolicy)qrzpol;
                int qrzint; if (iniFile.KeyExists("qrzMinIntervalSeconds") && int.TryParse(iniFile.Read("qrzMinIntervalSeconds"), out qrzint) && qrzint >= 5) qrzMinIntervalSeconds = qrzint;
                if (iniFile.KeyExists("lotwEnabled"))        lotwEnabled        = iniFile.Read("lotwEnabled")    == "True";
                if (iniFile.KeyExists("lotwBoostEnabled"))   lotwBoostEnabled   = iniFile.Read("lotwBoostEnabled") == "True";
                int lotwd; if (iniFile.KeyExists("lotwRefreshDays") && int.TryParse(iniFile.Read("lotwRefreshDays"), out lotwd)   && lotwd   >= 1) lotwRefreshDays  = lotwd;
                int clgd; if (iniFile.KeyExists("clubLogRefreshDays") && int.TryParse(iniFile.Read("clubLogRefreshDays"), out clgd) && clgd >= 1) clubLogRefreshDays = clgd;
                if (iniFile.KeyExists("fccUlsEnabled"))       fccUlsEnabled      = iniFile.Read("fccUlsEnabled")     == "True";
                int fccd; if (iniFile.KeyExists("fccUlsRefreshDays") && int.TryParse(iniFile.Read("fccUlsRefreshDays"), out fccd) && fccd >= 1) fccUlsRefreshDays = fccd;
                if (iniFile.KeyExists("qrzLogbookApiKey")) qrzLogbookApiKey = CredentialProtector.Unprotect(iniFile.Read("qrzLogbookApiKey"));
                if (iniFile.KeyExists("lotwLogbookUser"))  lotwLogbookUser  = iniFile.Read("lotwLogbookUser")  ?? "";
                if (iniFile.KeyExists("lotwLogbookPass"))  lotwLogbookPass  = CredentialProtector.Unprotect(iniFile.Read("lotwLogbookPass"));
                if (iniFile.KeyExists("qrzUploadEnabled"))      qrzUploadEnabled      = iniFile.Read("qrzUploadEnabled")      == "True";
                if (iniFile.KeyExists("qrzUploadRealtime"))     qrzUploadRealtime     = iniFile.Read("qrzUploadRealtime")     == "True";
                if (iniFile.KeyExists("lotwUploadEnabled"))     lotwUploadEnabled     = iniFile.Read("lotwUploadEnabled")     == "True";
                if (iniFile.KeyExists("clubLogUploadEnabled"))  clubLogUploadEnabled  = iniFile.Read("clubLogUploadEnabled")  == "True";
                if (iniFile.KeyExists("clubLogUploadRealtime")) clubLogUploadRealtime = iniFile.Read("clubLogUploadRealtime") == "True";
                if (iniFile.KeyExists("clubLogUploadEmail"))    clubLogUploadEmail    = iniFile.Read("clubLogUploadEmail")    ?? "";
                if (iniFile.KeyExists("clubLogUploadPassword")) clubLogUploadPassword = CredentialProtector.Unprotect(iniFile.Read("clubLogUploadPassword"));
                if (iniFile.KeyExists("clubLogUploadCallsign")) clubLogUploadCallsign = iniFile.Read("clubLogUploadCallsign") ?? "";
                if (iniFile.KeyExists("hrdLogUploadEnabled"))    hrdLogUploadEnabled   = iniFile.Read("hrdLogUploadEnabled")    == "True";
                if (iniFile.KeyExists("hrdLogUploadRealtime"))   hrdLogUploadRealtime  = iniFile.Read("hrdLogUploadRealtime")   == "True";
                if (iniFile.KeyExists("hrdLogUploadCode"))       hrdLogUploadCode      = CredentialProtector.Unprotect(iniFile.Read("hrdLogUploadCode"));
                if (iniFile.KeyExists("hrdLogUploadCallsign"))   hrdLogUploadCallsign  = iniFile.Read("hrdLogUploadCallsign")   ?? "";
                if (iniFile.KeyExists("eqslUploadEnabled"))  eqslUploadEnabled  = iniFile.Read("eqslUploadEnabled")  == "True";
                if (iniFile.KeyExists("eqslUploadRealtime")) eqslUploadRealtime = iniFile.Read("eqslUploadRealtime") == "True";
                if (iniFile.KeyExists("eqslUsername"))       eqslUsername       = iniFile.Read("eqslUsername") ?? "";
                if (iniFile.KeyExists("eqslPassword"))       eqslPassword       = CredentialProtector.Unprotect(iniFile.Read("eqslPassword"));
                if (iniFile.KeyExists("hamQthEnabled"))      hamQthEnabled      = iniFile.Read("hamQthEnabled")  == "True";
                if (iniFile.KeyExists("hamQthUsername"))     hamQthUsername     = iniFile.Read("hamQthUsername") ?? "";
                if (iniFile.KeyExists("hamQthPassword"))     hamQthPassword     = CredentialProtector.Unprotect(iniFile.Read("hamQthPassword"));
                if (iniFile.KeyExists("hamQthCacheDays"))    int.TryParse(iniFile.Read("hamQthCacheDays"), out hamQthCacheDays);
                if (iniFile.KeyExists("callsignLookupProvider"))
                    Enum.TryParse(iniFile.Read("callsignLookupProvider"), out callsignLookupProvider);
                if (iniFile.KeyExists("dxClusterAddress")) dxClusterAddress = iniFile.Read("dxClusterAddress") ?? "";
                if (iniFile.KeyExists("tqslStationLocation"))    tqslStationLocation   = iniFile.Read("tqslStationLocation")    ?? "";
                if (iniFile.KeyExists("qrzLogbookAutoSyncEnabled"))     qrzLogbookAutoSyncEnabled     = iniFile.Read("qrzLogbookAutoSyncEnabled")     == "True";
                int qrzld; if (iniFile.KeyExists("qrzLogbookRefreshDays") && int.TryParse(iniFile.Read("qrzLogbookRefreshDays"), out qrzld) && qrzld >= 1) qrzLogbookRefreshDays = qrzld;
                if (iniFile.KeyExists("lotwLogbookAutoSyncEnabled"))    lotwLogbookAutoSyncEnabled    = iniFile.Read("lotwLogbookAutoSyncEnabled")    == "True";
                int lotwld; if (iniFile.KeyExists("lotwLogbookRefreshDays") && int.TryParse(iniFile.Read("lotwLogbookRefreshDays"), out lotwld) && lotwld >= 1) lotwLogbookRefreshDays = lotwld;
                if (iniFile.KeyExists("clubLogLogbookAutoSyncEnabled")) clubLogLogbookAutoSyncEnabled = iniFile.Read("clubLogLogbookAutoSyncEnabled") == "True";
                int clld; if (iniFile.KeyExists("clubLogLogbookRefreshDays") && int.TryParse(iniFile.Read("clubLogLogbookRefreshDays"), out clld) && clld >= 1) clubLogLogbookRefreshDays = clld;
                if (iniFile.KeyExists("activeAwardRuleIds")) activeAwardRuleIds = ParseActiveAwardRuleIds(iniFile.Read("activeAwardRuleIds"));
                else if (iniFile.KeyExists("stillNeedLiveTagRuleId"))
                {
                    // Migrate the old single-rule setting the first time this INI is loaded
                    // under the new multi-award system.
                    string oldId = iniFile.Read("stillNeedLiveTagRuleId");
                    if (!string.IsNullOrWhiteSpace(oldId)) activeAwardRuleIds = new HashSet<string> { oldId };
                }
            }

            txMode = mode ? WsjtxClient.TxModes.LISTEN : WsjtxClient.TxModes.CALL_CQ;

            if (directedTextBox.Text == "") callDirCqCheckBox.Checked = false;
            directedTextBox.Enabled = callDirCqCheckBox.Checked;
            if (!directedTextBox.Enabled && directedTextBox.Text == "")
            {
                directedTextBox.Text = separateBySpaces;
            }

            if (alertTextBox.Text == "") replyDirCqCheckBox.Checked = false;
            alertTextBox.Enabled = replyDirCqCheckBox.Checked;
            if (!alertTextBox.Enabled && alertTextBox.Text == "")
            {
                alertTextBox.Text = separateBySpaces;
            }

            if (exceptTextBox.Text == "")
            {
                exceptTextBox.Text = separateBySpaces;
                exceptTextBox.ForeColor = Color.Gray;
            }

            UpdateTxLabel();

            callCqDxCheckBox_CheckedChanged(null, null);
            callNonDirCqCheckBox_CheckedChanged(null, null);
            directedTextBox_Leave(null, null);
            if (!cqOnlyRadioButton.Checked && !cqGridRadioButton.Checked && !anyMsgRadioButton.Checked) cqOnlyRadioButton.Checked = true;
            UpdateCqNewOnBand();

#if DEBUG
            AllocConsole();

            if (!debug)
            {
                ShowWindow(GetConsoleWindow(), 0);
            }
#endif

            // Call CQ options button — replaces the old modeGroupBox 4-radio group (Listen /
            // CQ only / CQ DX only / CQ and DX), which is kept in the Designer for its radio
            // buttons' internal wiring but is never shown anymore (see WsjtxClient.UpdateModeVisible).
            // Must exist before WsjtxClient's constructor below, which calls UpdateModeVisible()
            // and sets this button's Visible state. Same on-screen footprint modeGroupBox used
            // to occupy.
            callCqOptionsButton = new System.Windows.Forms.Button
            {
                Text           = "Call CQ options...",
                AccessibleName = "Call CQ options",
                Location       = new System.Drawing.Point(15, 141),
                Size           = new System.Drawing.Size(230, 26),
                TabIndex       = 51,
                Visible        = false,
            };
            callCqOptionsButton.Click += (s2, e2) => OpenCallCqDialog();
            this.Controls.Add(callCqOptionsButton);
            callCqOptionsButton.BringToFront();

            // WsjtxClient's constructor used to also take the classic UDP transport's own
            // ipAddress/multicast settings (see ResolveUdpListenAddress's own removal comment
            // above for the fresh-install-crash history this area of code has) -- dropped
            // 2026-08-18 along with WsjtxProtocolAdapter and the rest of that transport.
            wsjtxClient = new WsjtxClient(this, port, debug, diagLog, txMode);
            if (parsedCallWaitingRowOrder != null)
            {
                wsjtxClient.callWaitingRowOrderFields = parsedCallWaitingRowOrder;
            }
            if (parsedRawDecodeRowOrder != null)
            {
                wsjtxClient.rawDecodeRowOrderFields = parsedRawDecodeRowOrder;
            }
            if (iniFile != null)
            {
                int.TryParse(iniFile.Read("txOddOffset"), out int cachedOdd);
                int.TryParse(iniFile.Read("txEvenOffset"), out int cachedEven);
                if (cachedOdd > 0) wsjtxClient.cachedOddOffset = cachedOdd;
                if (cachedEven > 0) wsjtxClient.cachedEvenOffset = cachedEven;
            }
            wsjtxClient.myContinent = myContinent;
            if (myContinent != null) replyLocalCheckBox.Text = myContinent;
            if (offsetLoLimit > 0) wsjtxClient.offsetLoLimit = offsetLoLimit;
            if (offsetHiLimit > 0) wsjtxClient.offsetHiLimit = offsetHiLimit;
            wsjtxClient.useRR73 = useRR73;
            wsjtxClient.ApplySortOrder(
                ParseRankOrder(rankOrderStr, rankMethodIdx),
                ParseRankBeam(rankBeamStr, rankMethodIdx));
            wsjtxClient.ApplyCategoryWeights(ParseCategoryWeights(categoryWeightsStr));
            wsjtxClient.ApplyCallingPriorities(ParseCallingPriorities(callingPrioritiesStr, categoryDisabledStr));
            // Migration (Phase 1): if this config pre-dates Call Filters and the operator had
            // replyDxCheckBox or replyLocalCheckBox enabled, ordinary CQ calls were being admitted.
            // Add DEFAULT to callingEnabled so that admission behaviour is preserved after upgrade.
            if (!wsjtxClient.Ranker.callingEnabled.Contains(WsjtxClient.CallCategory.DEFAULT)
                && (replyDxCheckBox.Checked || replyLocalCheckBox.Checked))
            {
                wsjtxClient.Ranker.callingEnabled.Add(WsjtxClient.CallCategory.DEFAULT);
                iniFile.Write("callingPriorities",
                    FormatCallingPriorities(wsjtxClient.Ranker.callingEnabled));
            }
            // Migration: a config saved before STILL_NEEDED existed won't have it in its
            // callingPriorities list, so Still Need live tagging would be silently disabled
            // for existing installs (ParseCallingPriorities only fills in the new default for
            // configs with no saved list at all). Add it once, same tier as WAS/DXCC/ZONE.
            if (!string.IsNullOrWhiteSpace(callingPrioritiesStr)
                && !wsjtxClient.Ranker.callingEnabled.Contains(WsjtxClient.CallCategory.STILL_NEEDED))
            {
                wsjtxClient.Ranker.callingEnabled.Add(WsjtxClient.CallCategory.STILL_NEEDED);
                iniFile.Write("callingPriorities",
                    FormatCallingPriorities(wsjtxClient.Ranker.callingEnabled));
            }
            wsjtxClient.ApplyWantedCalls(ParseWantedCalls(wantedCallsStr));
            wsjtxClient.ApplySpotWatchCalls(ParseSpotWatchCalls(spotWatchCallsStr));

            dxSpotWatcher = new DxSpotWatcher();
            dxSpotWatcher.Updated += () => BeginInvoke(new Action(RenderSpotWatchList));
            dxSpotWatcher.UpdateWatchList(wsjtxClient.spotWatchCalls);
            spotWatchAgeTimer.Start();
            ApplyEngineMode();      // Phase 4g: always launches the native engine host
            wsjtxClient.rawPriorityTags = rawPriorityTags;
            wsjtxClient.cmdPrompts = cmdPrompts;
            wsjtxClient.usePskReporter = usePskReporter;
            // The earlier call (above, right after hotkeyConfig.LoadFromIni) ran before
            // wsjtxClient existed, so it couldn't yet know a persisted cmdPrompts=true --
            // re-run now that wsjtxClient.cmdPrompts is actually set, so a session that starts
            // with command prompts already on shows hotkey-suffixed names from the first paint,
            // not only after the next hotkey save or Alt+P press.
            RefreshHotkeyAccessibleNames();

            lookupManager = new LookupManager();
            lookupManager.RegisterProvider(dxSpotWatcher);
            lookupManager.Initialize(
                useLookupData,
                qrzEnabled, qrzUsername, qrzPassword, qrzCacheDays,
                lotwEnabled, lotwRefreshDays,
                ClubLogAppKey.Resolve(), clubLogRefreshDays,
                fccUlsEnabled,
                qrzLookupPolicy, qrzMinIntervalSeconds,
                callsignLookupProvider,
                hamQthEnabled, hamQthUsername, hamQthPassword, hamQthCacheDays);
            wsjtxClient.lookupManager     = lookupManager;
            wsjtxClient.lotwBoostEnabled  = lotwBoostEnabled;
            BackfillMissingStates();
            LoadHrcCache();
            lookupManager.OnLookupCompleted = () =>
                BeginInvoke(new Action(() => wsjtxClient.RefreshQueueDisplay()));
            lookupManager.StartBackgroundRefreshIfNeeded(lotwRefreshDays, clubLogRefreshDays, fccUlsRefreshDays);

            // Loads every .ini file from the RuleDefinitions folder (awards engine).
            // A bad or missing folder must never block startup.
            RuleLibrary.ClubLog = lookupManager.ClubLog;
            try { RuleLibrary.Load(); } catch { }
            RefreshStillNeedCache();   // must run after RuleLibrary.Load() so the saved selection resolves


            mainLoopTimer.Interval = 10;           //actual is 11-12 msec (due to OS limitations)
            mainLoopTimer.Start();

            wsjtxClient.UpdateModeVisible();

            TopMost = alwaysOnTop;

            UpdateDebug();

            wsjtxClient.UpdateModeSelection();
            SyncCqIntentFromMode();     // force-sync after wsjtxClient is assigned

            // Logbook button — added below sortOrderButton at y=305
            logbookButton = new System.Windows.Forms.Button
            {
                Text           = "Logbook",
                AccessibleName = "Logbook",
                Location       = new System.Drawing.Point(10, 333),
                Size           = new System.Drawing.Size(492, 24),
                Anchor         = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right,
                TabIndex       = 50,
            };
            logbookButton.Click += (s2, e2) => OpenLogbookWindow();
            this.Controls.Add(logbookButton);
            logbookButton.BringToFront();

            // POTA/SOTA/DX Spots/Band Conditions/Space Weather window button — below
            // logbookButton at y=361, same footprint.
            otaSpotsButton = new System.Windows.Forms.Button
            {
                Text           = "POTA / SOTA / DX Spots",
                AccessibleName = "POTA, SOTA, and DX spots",
                Location       = new System.Drawing.Point(10, 361),
                Size           = new System.Drawing.Size(492, 24),
                Anchor         = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right,
                TabIndex       = 52,
            };
            otaSpotsButton.Click += (s2, e2) => OpenOtaSpotsWindow();
            this.Controls.Add(otaSpotsButton);
            otaSpotsButton.BringToFront();

            // Live JAWS-testing finding, 2026-08-21: logbookButton/otaSpotsButton set their own
            // plain, unsuffixed AccessibleName above at construction and then NEVER got it
            // refreshed with their assigned hotkey (F1/Alt+G by default) -- traced to both of
            // Form_Load's own startup-time RefreshHotkeyAccessibleNames() calls (right after
            // hotkeyConfig.LoadFromIni, and again after wsjtxClient is constructed) running
            // BEFORE this point, since these two buttons are created here, later in Form_Load.
            // callCqOptionsButton (constructed earlier, before the second of those two calls)
            // never had this problem for exactly that reason -- it happened to already exist by
            // the time a refresh ran. Every other main-screen button with a hotkey either comes
            // from InitializeComponent() (exists from before Form_Load even starts) or, like
            // callCqOptionsButton, is constructed early enough -- this is the one place in
            // Form_Load where a dynamically-created button's own construction is the LAST thing
            // that touches its AccessibleName before the form is shown, so it needs its own
            // explicit refresh call rather than relying on an earlier one it can't reach.
            RefreshHotkeyAccessibleNames();

            // Weak-signal floor controls — hidden here, reparented into
            // Options > Receive / Auto Reply > Block List while that dialog is open.
            ignoreWeakSnrCheckBox = new CheckBox
            {
                Text           = "Ignore SNR at or below",
                AccessibleName = "Ignore stations with SNR at or below the floor",
                AutoSize       = true,
                TabIndex       = 68,
                Visible        = false,
            };
            minSnrNumUpDown = new NumericUpDown
            {
                Minimum        = -30,
                Maximum        = 20,
                Value          = -24,
                Width          = 50,
                AccessibleName = "Weak signal SNR floor",
                TabIndex       = 69,
                Visible        = false,
            };
            minSnrLabel = new Label
            {
                Text     = "dB",
                AutoSize = true,
                Visible  = false,
            };
            // Off by default: the floor only gates whether a station's queue entry gets
            // refreshed by a new (weak) decode -- it does not by itself pull an
            // already-queued station back out once its signal fades. Checking this makes
            // a weak decode for an already-queued station remove it immediately instead
            // of leaving it to linger (with its last good SNR still shown) until the
            // separate call-queue age timeout eventually prunes it.
            removeOnWeakSnrCheckBox = new CheckBox
            {
                Text           = "Remove from list immediately when signal drops below floor",
                AccessibleName = "Remove already-listed stations immediately once their signal drops below the SNR floor",
                AutoSize       = true,
                TabIndex       = 70,
                Visible        = false,
            };
            if (iniFile != null)
            {
                ignoreWeakSnrCheckBox.Checked = iniFile.Read("ignoreWeakSnr") == "True";
                if (int.TryParse(iniFile.Read("minSnr"), out int savedMinSnr)) minSnrNumUpDown.Value = savedMinSnr;
                removeOnWeakSnrCheckBox.Checked = iniFile.Read("removeOnWeakSnr") == "True";
            }
            ignoreWeakSnrCheckBox.CheckedChanged += (s2, e2) => minSnrNumUpDown.Enabled = ignoreWeakSnrCheckBox.Checked;
            minSnrNumUpDown.Enabled = ignoreWeakSnrCheckBox.Checked;
            this.Controls.Add(ignoreWeakSnrCheckBox);
            this.Controls.Add(minSnrNumUpDown);
            this.Controls.Add(minSnrLabel);
            this.Controls.Add(removeOnWeakSnrCheckBox);

            formLoaded = true;
            ApplyAdvancedLayout();
            ApplyListAppearance();

            // Deferred via BeginInvoke rather than run here directly: Form_Load fires before
            // Windows has necessarily finished activating/showing the window, so SendKeys.Send
            // (which targets whatever window currently has real OS-level keyboard focus) can
            // fire too early and leave keyboard input going nowhere until the user manually
            // changes focus (e.g. Alt+Tab away and back) -- reported 2026-07-10, reproduced on
            // the unmodified 1.80.0 release build, so this is pre-existing, not a regression.
            // Posting this to run after Form_Load returns gives Windows a chance to finish
            // activating the window first.
            this.BeginInvoke(new Action(() =>
            {
                // Root cause (see ForceForeground's comment): Windows' automatic
                // foreground-activation grant for this launch may already be spent, so
                // Focus() alone (managed-only, no OS-level effect if we're not already the
                // foreground process) isn't enough. Force real OS foreground first.
                ForceForeground();

                if (!this.Focused)
                {
                    this.Focus();
                }

                if (!statusText.Focused)
                {
                    statusText.Focus();
                }
                // Hardened 2026-08-19 (release-blocker follow-up): this SendKeys.Send used to be
                // unconditional here -- it ran every single launch regardless of whether
                // ForceForeground() actually succeeded. ForceForeground can fail to make this the
                // real OS foreground window (AttachThreadInput/SetForegroundWindow have no
                // universal guarantee -- security software, a different foreground owner, or
                // other machine-specific conditions can all defeat it) -- confirmed live that it
                // DOES normally succeed on at least one real launch, but "normally" is not "always
                // guaranteed", and this is the one SendKeys call that fires on every single
                // startup, config valid or not, which is exactly why this was already suspected
                // (see this method's own comment above) as the likely explanation for reports of
                // Jimmy becoming keyboard-unreachable from a genuinely fresh launch, predating
                // this whole investigation. Only send the forced re-announce keystroke once real
                // OS-level foreground is actually confirmed -- if it isn't, skip the forced nudge
                // rather than risk SendKeys's journal-hook mechanism targeting the wrong window;
                // the status text is still set correctly and a screen reader picks it up normally
                // the next time the operator focuses/tabs into the window or a later real status
                // update fires ShowMsg again (which now carries the identical real-foreground
                // guard -- see ShowMsg's own comment).
                if (GetForegroundWindow() == this.Handle)
                    SendKeys.Send("{UP}");
            }));

            if (checkForUpdatesOnStartup)
            {
                _ = CheckForUpdateOnStartupAsync();
            }
        }

        // Fire-and-forget from Form_Load: runs entirely on a background thread until it
        // has something to show, then hops back to the UI thread via BeginInvoke. Silent
        // on any failure (network down, GitHub rate limit, etc.) -- see UpdateChecker.
        private async Task CheckForUpdateOnStartupAsync()
        {
            string currentVersion = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? string.Empty;

            UpdateInfo info = await UpdateChecker.CheckForNewerVersionAsync(currentVersion).ConfigureAwait(false);
            if (info == null) return;

            BeginInvoke(new Action(() => OfferUpdate(info, currentVersion)));
        }

        private void OfferUpdate(UpdateInfo info, string currentVersion)
        {
            string releaseNote = info.Published.HasValue
                ? $" (released {info.Published.Value.ToLocalTime():MMMM d, yyyy})"
                : "";
            var result = MessageBox.Show(this,
                $"{friendlyName} {info.Version} is available{releaseNote}. You have {currentVersion}." +
                $"{nl}{nl}Download and install it now? {friendlyName} will close to complete the install.",
                "Update Available", MessageBoxButtons.YesNo, MessageBoxIcon.Information);

            if (result != DialogResult.Yes) return;

            _ = DownloadAndInstallUpdateAsync(info);
        }

        private async Task DownloadAndInstallUpdateAsync(UpdateInfo info)
        {
            string msiPath;
            try
            {
                msiPath = await UpdateChecker.DownloadToTempAsync(info.MsiUrl, info.MsiName).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                BeginInvoke(new Action(() =>
                    MessageBox.Show(this, $"Could not download the update:{nl}{ex.Message}", "Update Failed",
                        MessageBoxButtons.OK, MessageBoxIcon.Error)));
                return;
            }

            BeginInvoke(new Action(() =>
            {
                try
                {
                    System.Diagnostics.Process.Start(msiPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Could not launch the installer:{nl}{ex.Message}", "Update Failed",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                Application.Exit();
            }));
        }

        private void Controller_FormClosing(object sender, FormClosingEventArgs e)
        {
            // Found live (release blocker, 2026-08-19): this whole block reads wsjtxClient.*
            // extensively, but wsjtxClient is only ever assigned once Form_Load reaches its own
            // WsjtxClient construction -- a startup that failed before that point (e.g. the
            // IPAddress.Parse crash this same investigation found) left it null, and the
            // original `if (iniFile != null)` guard alone didn't cover that: iniFile is created
            // much earlier in Form_Load, so it was already non-null, and every wsjtxClient.*
            // read below threw NullReferenceException the moment the operator tried to close the
            // window after a failed startup. formLoaded is the existing, already-established
            // "did Form_Load actually finish" flag (set true only at its very end, well after
            // wsjtxClient's construction -- see its own declaration comment) that many other
            // handlers in this class already guard on; reusing it here is the correct fix, not
            // just a null-check bolted onto this one symptom -- a session that never finished
            // starting has nothing real to persist anyway.
            if (iniFile != null && formLoaded)
            {
                iniFile.Write("debug", wsjtxClient.debug.ToString());
                // Save the Normal-state bounds even if currently maximized/minimized, so
                // restoring later doesn't land on maximized dimensions.
                Rectangle normalBounds = this.WindowState == FormWindowState.Normal
                    ? new Rectangle(this.Location, this.Size)
                    : this.RestoreBounds;
                iniFile.Write("windowPosX", normalBounds.X.ToString());
                iniFile.Write("windowPosY", normalBounds.Y.ToString());
                iniFile.Write("windowWd", normalBounds.Width.ToString());
                iniFile.Write("windowHt", normalBounds.Height.ToString());
                iniFile.Write("windowState", this.WindowState.ToString());
                // ipAddress/multicast (the classic UDP receive-socket's own identity) are no
                // longer written 2026-08-18 -- WsjtxClient no longer has anything to read them
                // back into (WsjtxProtocolAdapter and the whole classic UDP transport are
                // removed). port is still written: it's still genuinely read at startup to
                // build the real engine host's own --jimmy-addr argument (Controller.
                // ApplyEngineMode's jimmyPort, NativeEngineClient.Launch) -- see that call
                // site's own comment for why that specific piece was NOT touched in this pass.
                if (wsjtxClient.port != 0) iniFile.Write("port", wsjtxClient.port.ToString());
                iniFile.Write("timeout", ((int)timeoutNumUpDown.Value).ToString());
                iniFile.Write("ignoreWeakSnr", ignoreWeakSnrCheckBox.Checked.ToString());
                iniFile.Write("minSnr", ((int)minSnrNumUpDown.Value).ToString());
                iniFile.Write("removeOnWeakSnr", removeOnWeakSnrCheckBox.Checked.ToString());
                iniFile.Write("useDirected", callDirCqCheckBox.Checked.ToString());
                iniFile.Write("directedCqLockedEntry", directedCqLockedEntry ?? "");
                if (directedTextBox.Text == separateBySpaces) directedTextBox.Clear();
                iniFile.Write("directeds", directedTextBox.Text.Trim());
                iniFile.Write("playMyCall", mycallCheckBox.Checked.ToString());
                iniFile.Write("playLogged", loggedCheckBox.Checked.ToString());
                iniFile.Write("playCallAdded", callAddedCheckBox.Checked.ToString());
                iniFile.Write("useAlertDirected", replyDirCqCheckBox.Checked.ToString());
                if (alertTextBox.Text == separateBySpaces) alertTextBox.Clear();
                iniFile.Write("alertDirecteds", alertTextBox.Text.Trim());
                iniFile.Write("logEarly", logEarlyCheckBox.Checked.ToString());
                iniFile.Write("alwaysOnTop", alwaysOnTop.ToString());
                iniFile.Write("useRR73", wsjtxClient.useRR73.ToString());
                iniFile.Write("skipGrid", skipGridCheckBox.Checked.ToString());
                iniFile.Write("firstRun", "False");
                iniFile.Write("enableReplyDx", replyDxCheckBox.Checked.ToString());
                iniFile.Write("enableReplyLocal", replyLocalCheckBox.Checked.ToString());
                iniFile.Write("diagLog", wsjtxClient.diagLog.ToString());
                // txMode startup is always LISTEN; not persisted across sessions
                iniFile.Write("bestOffset", freqCheckBox.Checked.ToString());
                iniFile.Write("optimizeTx", optimizeCheckBox.Checked.ToString());
                if (exceptTextBox.Text == separateBySpaces) exceptTextBox.Clear();
                iniFile.Write("exceptCalls", exceptTextBox.Text.Trim());
                iniFile.Write("callCqDx", callCqDxCheckBox.Checked.ToString());
                iniFile.Write("ignoreNonDx", ignoreNonDxCheckBox.Checked.ToString());
                iniFile.Write("callNonDirCq", callNonDirCqCheckBox.Checked.ToString());
                iniFile.Write("skipLevelPrompt", skipLevelPrompt.ToString());
                iniFile.Write("cqOnly", cqOnlyRadioButton.Checked.ToString());
                iniFile.Write("newOnBand", (bandComboBox.SelectedIndex == 1).ToString());
                iniFile.Write("myContinent", wsjtxClient.myContinent);
                iniFile.Write("rankMethod", wsjtxClient.Ranker.rankMethodIdx.ToString());
                iniFile.Write("categoryWeights",   FormatCategoryWeights(wsjtxClient.Ranker.categoryWeight));
                iniFile.Write("callingPriorities", FormatCallingPriorities(wsjtxClient.Ranker.callingEnabled));
                iniFile.Write("wantedCalls",              FormatWantedCalls(wsjtxClient.wantedCalls));
                iniFile.Write("spotWatchCalls",            FormatSpotWatchCalls(wsjtxClient.spotWatchCalls));
                iniFile.Write("spotWatchSortKey",          spotWatchSortKey);
                iniFile.Write("wantedCallAnywhereEnabled", wantedCallAnywhereEnabled.ToString());
                iniFile.Write("rawPriorityTags",          rawPriorityTags.ToString());
                iniFile.Write("replyRR73", replyRR73CheckBox.Checked.ToString());
                iniFile.Write("cqGrid", cqGridRadioButton.Checked.ToString());
                iniFile.Write("anyMsg", anyMsgRadioButton.Checked.ToString());
                iniFile.Write("txPeriodIdx", periodComboBox.SelectedIndex.ToString());
                iniFile.Write("cmdPrompts", wsjtxClient.cmdPrompts.ToString());
                iniFile.Write("usePskReporter", wsjtxClient.usePskReporter.ToString());
                iniFile.Write("showUsState", showUsStateCheckBox.Checked.ToString());
                // Release-audit finding, 2026-08-20: extracted into SaveOptionsRelatedSettings()
                // (this method's own comment) so OptionsDlg's OK button can also call it right
                // after applying its Save*Tab() methods, instead of these settings only ever
                // reaching disk on a clean app shutdown. Still called here too, unconditionally,
                // for the same reason every other line in this block is (session-level UI state
                // that Options doesn't separately govern).
                SaveOptionsRelatedSettings();
                iniFile.Write("rawShowCq", rawShowCq.ToString());
                iniFile.Write("rawShowDirected", rawShowDirected.ToString());
                iniFile.Write("rawShowReports", rawShowReports.ToString());
                iniFile.Write("rawShowRR73", rawShowRR73.ToString());
                iniFile.Write("rawShow73", rawShow73.ToString());
                iniFile.Write("rawShowPota", rawShowPota.ToString());
                iniFile.Write("rawShowSota", rawShowSota.ToString());
                iniFile.Write("rawShowDx", rawShowDx.ToString());
                iniFile.Write("rawShowSnr", rawShowSnr.ToString());
                iniFile.Write("rawShowGrid", rawShowGrid.ToString());
                iniFile.Write("rawShowCountry", rawShowCountry.ToString());
                iniFile.Write("rawShowDistAz", rawShowDistAz.ToString());
                iniFile.Write("rawOnlyCallsigns", rawOnlyCallsigns.ToString());
                iniFile.Write("rawOnlyUnworked", rawOnlyUnworked.ToString());
                iniFile.Write("rawOnlyRanked", rawOnlyRanked.ToString());
                iniFile.Write("rawPriorityTags", rawPriorityTags.ToString());
                iniFile.Write("rawNewestFirst", rawNewestFirst.ToString());
                iniFile.Write("rawMaxRows", rawMaxRows.ToString());
                iniFile.Write("maxQueuedCalls", maxQueuedCallsBase.ToString());
                iniFile.Write("maxCallQueueAgePeriods", maxCallQueueAgePeriods.ToString());
                iniFile.Write("statusBatchDelayMs", statusBatchDelayMs.ToString());
                iniFile.Write("keepTransmitListDuringTx", keepTransmitListDuringTx.ToString());
                iniFile.Write("keepListPositionDuringRefresh", keepListPositionDuringRefresh.ToString());
                iniFile.Write("moveFocusToStatusOnCallSelect", moveFocusToStatusOnCallSelect.ToString());
                iniFile.Write("checkForUpdatesOnStartup", checkForUpdatesOnStartup.ToString());
                iniFile.Write("announceImportantAlertsWhenFocusElsewhere", announceImportantAlertsWhenFocusElsewhere.ToString());
                // Sound settings
                iniFile.Write("soundFile_CallAdded",        soundFile_CallAdded   ?? "");
                iniFile.Write("soundFile_CallingMe",        soundFile_CallingMe   ?? "");
                iniFile.Write("soundFile_Logged",           soundFile_Logged      ?? "");
                iniFile.Write("soundEnabled_TxEnabled",     soundEnabled_TxEnabled.ToString());
                iniFile.Write("soundFile_TxEnabled",        soundFile_TxEnabled   ?? "");
                iniFile.Write("soundEnabled_Disconnected",  soundEnabled_Disconnected.ToString());
                iniFile.Write("soundFile_Disconnected",     soundFile_Disconnected ?? "");
                iniFile.Write("soundEnabled_NewDxcc",       soundEnabled_NewDxcc.ToString());
                iniFile.Write("soundFile_NewDxcc",          soundFile_NewDxcc      ?? "");
                iniFile.Write("soundEnabled_NewDxccOnBand", soundEnabled_NewDxccOnBand.ToString());
                iniFile.Write("soundFile_NewDxccOnBand",    soundFile_NewDxccOnBand ?? "");
                iniFile.Write("soundEnabled_AlwaysWanted",  soundEnabled_AlwaysWanted.ToString());
                iniFile.Write("soundFile_AlwaysWanted",     soundFile_AlwaysWanted  ?? "");
                iniFile.Write("soundEnabled_DirectedCq",    soundEnabled_DirectedCq.ToString());
                iniFile.Write("soundFile_DirectedCq",       soundFile_DirectedCq    ?? "");
                iniFile.Write("soundEnabled_Pota",          soundEnabled_Pota.ToString());
                iniFile.Write("soundFile_Pota",             soundFile_Pota          ?? "");
                iniFile.Write("soundEnabled_Sota",           soundEnabled_Sota.ToString());
                iniFile.Write("soundFile_Sota",              soundFile_Sota              ?? "");
                iniFile.Write("soundEnabled_WantedAnywhere", soundEnabled_WantedAnywhere.ToString());
                iniFile.Write("soundFile_WantedAnywhere",    soundFile_WantedAnywhere    ?? "");
                iniFile.Write("soundEnabled_OppositePeriod", soundEnabled_OppositePeriod.ToString());
                iniFile.Write("soundFile_OppositePeriod",    soundFile_OppositePeriod    ?? "");
                iniFile.Write("soundEnabled_AwardNeeded",    soundEnabled_AwardNeeded.ToString());
                iniFile.Write("soundFile_AwardNeeded",       soundFile_AwardNeeded       ?? "");
                iniFile.Write("soundsEnabled",               soundsEnabled.ToString());
                iniFile.Write("txOddOffset",  wsjtxClient.cachedOddOffset.ToString());
                iniFile.Write("txEvenOffset", wsjtxClient.cachedEvenOffset.ToString());
                // Lookup / Data settings
                iniFile.Write("useLookupData",           useLookupData.ToString());
                iniFile.Write("qrzEnabled",              qrzEnabled.ToString());
                iniFile.Write("qrzUsername",             qrzUsername              ?? "");
                iniFile.Write("qrzPassword",             CredentialProtector.Protect(qrzPassword));
                iniFile.Write("qrzCacheDays",            qrzCacheDays.ToString());
                iniFile.Write("qrzLookupPolicy",         ((int)qrzLookupPolicy).ToString());
                iniFile.Write("qrzMinIntervalSeconds",   qrzMinIntervalSeconds.ToString());
                iniFile.Write("lotwEnabled",             lotwEnabled.ToString());
                iniFile.Write("lotwBoostEnabled",        lotwBoostEnabled.ToString());
                iniFile.Write("lotwRefreshDays",         lotwRefreshDays.ToString());
                iniFile.Write("clubLogRefreshDays",      clubLogRefreshDays.ToString());
                iniFile.Write("fccUlsEnabled",           fccUlsEnabled.ToString());
                iniFile.Write("fccUlsRefreshDays",       fccUlsRefreshDays.ToString());
                iniFile.Write("qrzLogbookApiKey",        CredentialProtector.Protect(qrzLogbookApiKey));
                iniFile.Write("lotwLogbookUser",         lotwLogbookUser          ?? "");
                iniFile.Write("lotwLogbookPass",         CredentialProtector.Protect(lotwLogbookPass));
                iniFile.Write("qrzUploadEnabled",        qrzUploadEnabled.ToString());
                iniFile.Write("qrzUploadRealtime",       qrzUploadRealtime.ToString());
                iniFile.Write("lotwUploadEnabled",        lotwUploadEnabled.ToString());
                iniFile.Write("clubLogUploadEnabled",    clubLogUploadEnabled.ToString());
                iniFile.Write("clubLogUploadRealtime",   clubLogUploadRealtime.ToString());
                iniFile.Write("clubLogUploadEmail",      clubLogUploadEmail       ?? "");
                iniFile.Write("clubLogUploadPassword",   CredentialProtector.Protect(clubLogUploadPassword));
                iniFile.Write("clubLogUploadCallsign",   clubLogUploadCallsign    ?? "");
                iniFile.Write("hrdLogUploadEnabled",     hrdLogUploadEnabled.ToString());
                iniFile.Write("hrdLogUploadRealtime",    hrdLogUploadRealtime.ToString());
                iniFile.Write("hrdLogUploadCode",        CredentialProtector.Protect(hrdLogUploadCode));
                iniFile.Write("hrdLogUploadCallsign",    hrdLogUploadCallsign     ?? "");
                iniFile.Write("eqslUploadEnabled",       eqslUploadEnabled.ToString());
                iniFile.Write("eqslUploadRealtime",      eqslUploadRealtime.ToString());
                iniFile.Write("eqslUsername",            eqslUsername             ?? "");
                iniFile.Write("eqslPassword",            CredentialProtector.Protect(eqslPassword));
                iniFile.Write("hamQthEnabled",           hamQthEnabled.ToString());
                iniFile.Write("hamQthUsername",          hamQthUsername           ?? "");
                iniFile.Write("hamQthPassword",          CredentialProtector.Protect(hamQthPassword));
                iniFile.Write("hamQthCacheDays",         hamQthCacheDays.ToString());
                iniFile.Write("callsignLookupProvider",  callsignLookupProvider.ToString());
                iniFile.Write("dxClusterAddress",        dxClusterAddress        ?? "");
                iniFile.Write("tqslStationLocation",     tqslStationLocation      ?? "");
                iniFile.Write("qrzLogbookAutoSyncEnabled",     qrzLogbookAutoSyncEnabled.ToString());
                iniFile.Write("qrzLogbookRefreshDays",         qrzLogbookRefreshDays.ToString());
                iniFile.Write("lotwLogbookAutoSyncEnabled",    lotwLogbookAutoSyncEnabled.ToString());
                iniFile.Write("lotwLogbookRefreshDays",        lotwLogbookRefreshDays.ToString());
                iniFile.Write("clubLogLogbookAutoSyncEnabled", clubLogLogbookAutoSyncEnabled.ToString());
                iniFile.Write("clubLogLogbookRefreshDays",     clubLogLogbookRefreshDays.ToString());
                iniFile.Write("activeAwardRuleIds",  FormatActiveAwardRuleIds(activeAwardRuleIds));
                iniFile.DeleteKey("stillNeedLiveTagRuleId");
                // Phase 4: remove stale keys left by older versions.
                iniFile.DeleteKey("autoReplyNewCq");
                iniFile.DeleteKey("replyOnlyDxcc");
                iniFile.DeleteKey("categoryDisabled");
                // Club Log's key is an app key (ClubLogAppKey), never a per-user
                // setting -- remove any value a pre-cleanup version stored here.
                iniFile.DeleteKey("clubLogApiKey");
                // Club Log is now always-on infrastructure, not a user toggle --
                // remove the old per-user enabled flag left by earlier versions.
                iniFile.DeleteKey("clubLogEnabled");
                hotkeyConfig?.SaveToIni(iniFile);
            }

            // Codex Audit 02 finding, 2026-08-21 ("improve shutdown handling for outstanding
            // optional remote-upload work"): a real-time QRZ/Club Log/HRDLog/eQSL upload from a
            // QSO logged moments ago could still be in flight right now (LiveQsoUploadOrchestrator
            // .ImportLiveLoggedQso's own Task.Run, tracked there for exactly this). Must run
            // BEFORE CloseComm() below, not after: CloseComm() disposes nativeEngineClient, which
            // kills the EngineHost process an in-flight eQSL upload is still routed through
            // (ExternalDataClient), so waiting after that point would be pointless for eQSL
            // specifically. Bounded to 3s -- long enough for a real upload that's genuinely almost
            // done, short enough that closing Jimmy never feels hung waiting on a dead network
            // call; either way CloseComm() below still runs unconditionally afterward.
            wsjtxClient?.LiveQsoUploader?.WaitForPendingUploads(TimeSpan.FromSeconds(3));

            CloseComm();
            optionsDlg?.Close();
            if (helpDlg != null) helpDlg.Close();
            _logbookWindow?.Close();
            _otaSpotsWindow?.Close();
        }

        public void SaveHotkeyConfig()
        {
            if (iniFile != null) hotkeyConfig?.SaveToIni(iniFile);
            RefreshHotkeyAccessibleNames();
        }

        // Release-audit finding, 2026-08-20 ("settings persistence should not rely only on
        // clean shutdown"): Radio/Decode/Frequencies/Notifications/NativeEngine/Settings all
        // apply live to memory the instant OptionsDlg's OK button runs their own Save*Tab()
        // methods -- the feature they control genuinely changes right then -- but the actual
        // INI DISK WRITE for every one of them used to only ever happen inside
        // Controller_FormClosing. A crash, forced kill, or Windows shutdown any time between an
        // Options change and the next CLEAN close silently reverted the operator's own change
        // for the next session, even though it had already been working correctly the whole
        // time in between. Same "commit a settings category to disk right when its own Save
        // flow completes, not only on clean shutdown" fix already established for hotkeys
        // (SaveHotkeyConfig above, called from OptionsDlg.cs's SaveHotkeysTab) -- this just
        // extends it to every other Options-governed settings object. Called from both
        // Controller_FormClosing (unconditionally, alongside the rest of that method's own
        // session-state save) and OptionsDlg's okButton_Click (right after its own Save*Tab()
        // calls) -- redundant on a normal clean-OK-then-later-clean-close session, which is
        // fine; an extra INI write of already-correct data is cheap and harmless.
        public void SaveOptionsRelatedSettings()
        {
            if (iniFile == null) return;
            Settings.SaveToIni(iniFile);
            Radio.SaveToIni(iniFile);
            Decode.SaveToIni(iniFile);
            Frequencies.SaveToIni(iniFile);
            Notifications.SaveToIni(iniFile);
            NativeEngine.SaveToIni(iniFile);
        }

        // Accessibility cleanup, 2026-08-19 (third-party audit): these used to always append the
        // assigned hotkey text to every name (e.g. "Options, Alt O") -- removed per the audit's
        // explicit examples (Options/Row Display Order/Stations Available Sort Order) and applied
        // consistently to the rest of this method's own buttons, since they're all generated by
        // this same pattern and these are custom global hotkeys, not standard WinForms mnemonics
        // JAWS/NVDA already announce on their own.
        //
        // Live-testing finding, 2026-08-21: that removal traded away real information -- a
        // keyboard/screen-reader user tabbing to these buttons had no way to learn their
        // shortcuts at all except by opening Help (Alt+K) separately. Reinstated, but tied to
        // "Show Command Prompts and Hotkeys" (HotkeyAction.Prompts, Alt+P by default) -- the
        // SAME setting that already controls whether Jimmy speaks extra command-key hints
        // elsewhere (WsjtxClient.Display.cs's ShowStatus), so this is one consistent "verbose
        // mode" rather than a second, unrelated toggle. Off (the audit's own concise default):
        // plain names, exactly as the audit intended. On: each name gains ", <hotkey>" -- but
        // ONLY for a button that actually has one assigned; an unassigned/Keys.None control
        // still shows nothing, matching the audit's own "don't say things that aren't true"
        // spirit. logbookButton/otaSpotsButton/callCqOptionsButton don't exist yet on Form_Load's
        // first two calls to this method (hotkeys load, and wsjtxClient's own construction, both
        // before any of the three are created) -- each sets its own initial, plain AccessibleName
        // at construction, matching the null-guards below. callCqOptionsButton happened to be
        // constructed before the SECOND of those two calls, so it was always picked up correctly;
        // logbookButton/otaSpotsButton are constructed later still and were NOT -- live JAWS-
        // testing finding, 2026-08-21: F1/Alt+G never appeared on those two buttons because
        // nothing ever called this method again after they were built. Fixed at the actual
        // construction site (right after otaSpotsButton.BringToFront(), Form_Load) with one more
        // explicit call, rather than here -- this method itself was never the bug, it was always
        // correct once actually called with the buttons in existence.
        public void RefreshHotkeyAccessibleNames()
        {
            if (hotkeyConfig == null) return;
            bool showHotkeys = wsjtxClient != null && wsjtxClient.cmdPrompts;
            optionsButton.AccessibleName   = WithHotkeySuffix("Options", HotkeyAction.Options, showHotkeys);
            rowOrderButton.AccessibleName  = WithHotkeySuffix("Row Display Order", HotkeyAction.RowOrder, showHotkeys);
            sortOrderButton.AccessibleName = WithHotkeySuffix("Stations Available Sort Order", HotkeyAction.SortOrder, showHotkeys);
            modeHelpLabel.AccessibleName   = WithHotkeySuffix("Help", HotkeyAction.Help, showHotkeys);
            if (logbookButton != null)
                logbookButton.AccessibleName = WithHotkeySuffix("Logbook", HotkeyAction.OpenLogbook, showHotkeys);
            if (otaSpotsButton != null)
                otaSpotsButton.AccessibleName = WithHotkeySuffix("POTA, SOTA, and DX spots", HotkeyAction.OpenOtaSpots, showHotkeys);
            if (callCqOptionsButton != null)
                callCqOptionsButton.AccessibleName = WithHotkeySuffix("Call CQ options", HotkeyAction.CallCqOptions, showHotkeys);
        }

        // Screen-reader-friendly "Alt, K" format (HotkeyConfig.FormatKeysForHelp), matching the
        // Help dialog's own hotkey wording -- see RefreshHotkeyAccessibleNames's own comment.
        private string WithHotkeySuffix(string baseName, HotkeyAction action, bool showHotkeys)
        {
            if (!showHotkeys) return baseName;
            Keys k = hotkeyConfig[action];
            if (k == Keys.None) return baseName;
            return $"{baseName}, {HotkeyConfig.FormatKeysForHelp(k)}";
        }

        public void CloseComm()
        {
            if (mainLoopTimer != null) mainLoopTimer.Stop();
            mainLoopTimer = null;
            statusMsgTimer.Stop();
            initialConnFaultTimer.Stop();
            // Codex Audit 04 finding 1, 2026-08-21: MUST run before nativeEngineClient.Dispose()
            // below, not after (the order this used to be in) -- wsjtxClient.Closing() is what
            // sends the graceful shutdown HALT_TX and waits briefly for it to actually land
            // (WsjtxClient.cs), which has nothing left to reach once the engine process is
            // already dead. CloseComm() runs unconditionally from Controller_FormClosing (outside
            // its own `iniFile != null && formLoaded` guard) -- a startup that failed before
            // wsjtxClient was ever constructed left this null too, and this was the second,
            // still-unguarded NullReferenceException risk on the same failed-startup-then-close
            // path (found alongside Controller_FormClosing's own fix, 2026-08-19).
            wsjtxClient?.Closing();
            nativeEngineClient?.Dispose();   // also stops the native engine host this session launched (and force-releases PTT, if held -- run_radio's own SHUTDOWN handling / the process exit path); last-resort backstop if the graceful halt above didn't confirm
            nativeEngineClient = null;
        }

        private void Controller_FormClosed(object sender, FormClosedEventArgs e)
        {

        }

#if DEBUG
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool AllocConsole();
#endif
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!formLoaded) return false;

            if (keyData == hotkeyConfig[HotkeyAction.Help])
            {
                modeHelpLabel_Click(null, null);
                return true;
            }

            if (keyData == hotkeyConfig[HotkeyAction.Options])
            {
                optionsButton_Click(null, null);
                return true;
            }

            if (keyData == hotkeyConfig[HotkeyAction.UpdateCheck])
            {
                verLabel2_Click(null, null);
                return true;
            }

            // Logbook is a self-contained window/database with no WSJT-X dependency, so its
            // hotkey must not sit behind the WsjtxConnecting() gate below -- otherwise it's
            // silently ignored (no error, no announcement) whenever WSJT-X hasn't launched yet.
            if (keyData == hotkeyConfig[HotkeyAction.OpenLogbook] && hotkeyConfig[HotkeyAction.OpenLogbook] != Keys.None)
            {
                OpenLogbookWindow();
                return true;
            }

            // Same reasoning as OpenLogbook above -- manual QSO entry is for logging QSOs
            // made independent of WSJT-X too (e.g. SSB on a separate rig), so it must not
            // require WSJT-X to be connected.
            if (keyData == hotkeyConfig[HotkeyAction.AddManualQso] && hotkeyConfig[HotkeyAction.AddManualQso] != Keys.None)
            {
                OpenLogbookWindow();
                _logbookWindow?.OpenAddQsoDialog();
                return true;
            }

            // Same reasoning as OpenLogbook above -- POTA/SOTA spots come from EngineHost's
            // own background cache, independent of WSJT-X's connection state.
            if (keyData == hotkeyConfig[HotkeyAction.OpenOtaSpots] && hotkeyConfig[HotkeyAction.OpenOtaSpots] != Keys.None)
            {
                OpenOtaSpotsWindow();
                return true;
            }

            if (!wsjtxClient.WsjtxConnecting()) return false;


            if (keyData == hotkeyConfig[HotkeyAction.ToggleMode])
            {
                return wsjtxClient.ToggleOperatingMode();
            }

            if (keyData == hotkeyConfig[HotkeyAction.BandUp])
            {
                return wsjtxClient.BandUp();
            }

            if (keyData == hotkeyConfig[HotkeyAction.BandDown])
            {
                return wsjtxClient.BandDown();
            }

            // Options > Frequencies: each frequency row can carry its own direct-jump hotkey
            // (replaces the old fixed HotkeyAction.Band160m..Band6m, one per band only, removed
            // entirely). Data-driven, not enum-based, so this scans instead of a fixed if-chain.
            // SelectFrequencyHotkey (not the raw SelectFrequency call this used to be), so a
            // hotkey never silently switches the operator's mode -- see its own comment
            // (WsjtxClient.BandAudio.cs) for the live-reproduced bug this fixes.
            for (int bi = 0; bi < Frequencies.Bands.Length; bi++)
            {
                foreach (var entry in Frequencies.Bands[bi])
                {
                    if (entry.Hotkey != Keys.None && keyData == entry.Hotkey)
                        return wsjtxClient.SelectFrequencyHotkey(bi, entry);
                }
            }


            if (!wsjtxClient.ConnectedToWsjtx()) return false;


            if (keyData == hotkeyConfig[HotkeyAction.PSKReporter])
            {
                return wsjtxClient.TogglePskReporter();
            }

            if (keyData == hotkeyConfig[HotkeyAction.Prompts])
            {
                return wsjtxClient.TogglePrompts();
            }

            if (keyData == hotkeyConfig[HotkeyAction.UploadLotw])
            {
                return wsjtxClient.UploadLotw();
            }

            if (keyData == hotkeyConfig[HotkeyAction.EnableTx])
            {
                return wsjtxClient.EnableMode();
            }

            if (keyData == hotkeyConfig[HotkeyAction.DeleteAllCalls])
            {
                return wsjtxClient.ClearCallQueue();
            }

            if (keyData == hotkeyConfig[HotkeyAction.HaltTx])
            {
                var focused = this.ActiveControl;
                if (wsjtxClient.ConnectedToWsjtx())
                {
                    wsjtxClient.RequeueAbortedCall();
                    wsjtxClient.CancelQso();
                    wsjtxClient.HaltAndDisableTx();
                    wsjtxClient.ResetTxToCq();
                    listenModeButton_Click(null, null);
                    ShowMsg("Tx halted", true);
                }
                BeginInvoke((Action)(() => RestoreFocus(focused)));
                return true;
            }

            if (keyData == hotkeyConfig[HotkeyAction.CallCqMode])
            {
                // modeGroupBox itself is never shown anymore (see WsjtxClient.UpdateModeVisible).
                // The previous "Listen mode selected; CQ not started" branch here (guarded by
                // cqIntentListenButton.Checked) is removed -- found 2026-07-11 to be dead-wrong,
                // not just stale: SyncCqIntentFromMode keeps that checkbox permanently mirroring
                // wsjtxClient.txMode == LISTEN (it's never set any other way once the radio group
                // is hidden), so the guard was really just "if currently in Listen mode, refuse to
                // switch to CQ mode" -- which made Alt+C, whose entire purpose is that exact
                // switch, permanently unable to start CQ from Listen mode.
                if (wsjtxClient.ConnectedToWsjtx())
                {
                    // Requested 2026-08-21: warn once per session, before the FIRST Alt+C, that
                    // the CQ options this is about to call CQ WITH (directed CQ / CQ DX only /
                    // CQ and CQ DX, etc. -- CallCqDlg) haven't been reviewed yet this session and
                    // may be carrying over whatever was last saved, possibly from a prior
                    // session. Asked before the transmit-slot-analysis check below -- deciding
                    // WHAT to call CQ with logically comes before deciding WHEN/WHERE to
                    // transmit it. "Yes" opens the dialog and stops here -- CQ does not start
                    // automatically once it's reviewed; the operator presses Alt+C again (or the
                    // dialog's own controls) when actually ready. "No" proceeds with whatever is
                    // already saved, exactly as Alt+C always has, and is remembered for the rest
                    // of this session so this never asks twice.
                    if (!_callCqOptionsReviewedThisSession)
                    {
                        var optDlg = new ConfirmDlg();
                        optDlg.text = "Call CQ options (directed CQ, CQ DX only, etc.) have not been reviewed " +
                            "this session and may not be set the way you want.\nOpen Call CQ options now?";
                        optDlg.Owner = this;
                        optDlg.ShowDialog();
                        if (optDlg.DialogResult == DialogResult.Yes)
                        {
                            OpenCallCqDialog();
                            return true;
                        }
                        _callCqOptionsReviewedThisSession = true;
                    }

                    if (wsjtxClient.txMode == WsjtxClient.TxModes.LISTEN && wsjtxClient.AnalysisNeeded)
                    {
                        var confDlg = new ConfirmDlg();
                        confDlg.text = "Transmit slot has not been analyzed.\nRun recommended analysis now?";
                        confDlg.Owner = this;
                        confDlg.ShowDialog();
                        if (confDlg.DialogResult == DialogResult.Yes)
                            wsjtxClient.StartSlotAnalysis(true);
                        else
                        {
                            ShowMsg("Transmit slot analysis skipped.", true);
                            cqModeButton_Click(null, null);
                        }
                    }
                    else
                        cqModeButton_Click(null, null);
                }
                return true;
            }

            if (hotkeyConfig[HotkeyAction.CallCqOptions] != Keys.None && keyData == hotkeyConfig[HotkeyAction.CallCqOptions])
            {
                OpenCallCqDialog();
                return true;
            }

            if (keyData == hotkeyConfig[HotkeyAction.AnalyzeSlot] && hotkeyConfig[HotkeyAction.AnalyzeSlot] != Keys.None)
            {
                wsjtxClient.StartSlotAnalysis(false);
                return true;
            }
            if (keyData == hotkeyConfig[HotkeyAction.LookupStation] && hotkeyConfig[HotkeyAction.LookupStation] != Keys.None)
            {
                LookupFocusedCall();
                return true;
            }

            if (keyData == hotkeyConfig[HotkeyAction.ListenMode])
            {
                listenModeButton_Click(null, null);
                return true;
            }

            if (keyData == hotkeyConfig[HotkeyAction.NextCall])
            {
                if (advTx1ListBox.Visible && advTx1ListBox.Focused)
                    wsjtxClient.NextBestPriorityCallFromTx1();
                else if (advTx2ListBox.Visible && advTx2ListBox.Focused)
                    wsjtxClient.NextBestPriorityCallFromTx2();
                else if (advRawListBox.Visible && advRawListBox.Focused)
                    wsjtxClient.NextBestPriorityCallFromRaw();
                else
                    wsjtxClient.NextBestPriorityCall();
                return true;
            }

            if (keyData == hotkeyConfig[HotkeyAction.ManualCall])
            {
                OpenManualCallDialog();
                return true;
            }

            if (keyData == hotkeyConfig[HotkeyAction.TxPeriod])
            {
                return wsjtxClient.ToggleTxFirst();
            }

            if (keyData == hotkeyConfig[HotkeyAction.HoldTimeout])
            {
                return wsjtxClient.ToggleHoldCheckBox();
            }

            if (keyData == hotkeyConfig[HotkeyAction.PowerSwr])
            {
                return wsjtxClient.ReportPowerSwr();
            }

            if (keyData == hotkeyConfig[HotkeyAction.TuneMode])
            {
                return wsjtxClient.ToggleTuningProcess();
            }

            if (keyData == hotkeyConfig[HotkeyAction.SortOrder])
            {
                OpenSortOrderEditor();
                return true;
            }

            if (keyData == hotkeyConfig[HotkeyAction.RowOrder])
            {
                OpenRowDisplayOrderEditor();
                return true;
            }

            if (keyData == hotkeyConfig[HotkeyAction.ResetWindowSize])
            {
                ResetWindowSize();
                return true;
            }

            if (keyData == hotkeyConfig[HotkeyAction.AudioUp])
            {
                return wsjtxClient.AudioLevel(true);
            }

            if (keyData == hotkeyConfig[HotkeyAction.AudioDown])
            {
                return wsjtxClient.AudioLevel(false);
            }

            return base.ProcessCmdKey(ref msg, keyData); // Let other keys be processed normally
        }

        // UdpLoop() (the classic WSJT-X UDP transport's own per-tick pump) was removed
        // 2026-08-18 along with ConnectNativeEngine -- see WsjtxClient.Protocol.cs's own
        // top-of-file banner comment. Direct mode (WsjtxClient.Direct.cs) polls on its own
        // System.Windows.Forms.Timer (_directPollTimer), not this one; mainLoopTimer_Tick
        // itself is left wired (Controller.Designer.cs) in case a future need for a fast
        // (10ms) UI-thread tick arises, but has nothing left to do every tick today.
        private void mainLoopTimer_Tick(object sender, EventArgs e)
        {
        }

        private void statusMsgTimer_Tick(object sender, EventArgs e)
        {
            statusMsgTimer.Stop();
            wsjtxClient.UpdateCallInProg();
        }

        private void initialConnFaultTimer_Tick(object sender, EventArgs e)
        {
            if (IsJimmyForegrounded()) BringToFront();
            wsjtxClient.ConnectionDialog();
        }

        private void debugHighlightTimer_Tick(object sender, EventArgs e)
        {
            debugHighlightTimer.Stop();
            label17.ForeColor = Color.Black;
            label24.ForeColor = Color.Black;
            label25.ForeColor = Color.Black;
            label13.ForeColor = Color.Black;
            label10.ForeColor = Color.Black;
            label20.ForeColor = Color.Black;
            label21.ForeColor = Color.Black;
            label8.ForeColor = Color.Black;
            label19.ForeColor = Color.Black;
            label18.ForeColor = Color.Black;
            label12.ForeColor = Color.Black;
            label4.ForeColor = Color.Black;
            label14.ForeColor = Color.Black;
            label15.ForeColor = Color.Black;
            label16.ForeColor = Color.Black;
            label26.ForeColor = Color.Black;
            label27.ForeColor = Color.Black;
            label1.ForeColor = Color.Black;
            label2.ForeColor = Color.Black;
            label28.ForeColor = Color.Black;
            label11.ForeColor = Color.Black;
        }

        private void timeoutNumUpDown_ValueChanged(object sender, EventArgs e)
        {
            if (!formLoaded) return;

            if (timeoutNumUpDown.Value < minSkipCount)
            {
                timeoutNumUpDown.Value = minSkipCount;
            }

            if (timeoutNumUpDown.Value > maxSkipCount)
            {
                timeoutNumUpDown.Value = maxSkipCount;
            }
            UpdateTxLabel();

            wsjtxClient.TxRepeatChanged();
            optionsDlg?.UpdateView();
        }

        private void UpdateTxLabel()
        {
            if (timeoutNumUpDown.Value == 1)
            {
                repeatLabel.Text = "Tx per msg";
            }
            else
            {
                repeatLabel.Text = "repeated Tx";
            }
        }

        private void replyDirCqCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (!formLoaded) return;

            if (replyDirCqCheckBox.Checked) ignoreNonDxCheckBox.Checked = false;

            CheckManualSelection();

            alertTextBox.Enabled = replyDirCqCheckBox.Checked;
            if (replyDirCqCheckBox.Checked && alertTextBox.Text == separateBySpaces)
            {
                alertTextBox.Clear();
                alertTextBox.ForeColor = System.Drawing.Color.Black;
            }
            if (!replyDirCqCheckBox.Checked && alertTextBox.Text == "") alertTextBox.Text = separateBySpaces;

            optionsDlg?.UpdateView();
        }

        private void loggedCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (formLoaded && loggedCheckBox.Checked) wsjtxClient.Sounds.PlaySoundEvent(true, soundFile_Logged);
        }

        private void mycallCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (formLoaded && mycallCheckBox.Checked) wsjtxClient.Sounds.PlaySoundEvent(true, soundFile_CallingMe);
        }

        private void verLabel_DoubleClick(object sender, EventArgs e)
        {
            if (!formLoaded) return;

            wsjtxClient.debug = !wsjtxClient.debug;
            UpdateDebug();
            if (formLoaded) wsjtxClient.DebugChanged();
        }

        private bool _inResize;
        private void Controller_Resize(object sender, EventArgs e)
        {
            if (!formLoaded || _inResize) return;
            _inResize = true;
            try { ApplyAdvancedLayout(); } finally { _inResize = false; }
        }

        private void ResetWindowSize()
        {
            this.WindowState = FormWindowState.Normal;
            this.Location = new Point(0, 0);
            this.Size = this.MinimumSize;   // natural size for the currently visible lists
            ApplyAdvancedLayout();

            statusText.Text = "Window size and position reset to default.";
            if (!statusText.Focused) statusText.Focus();
            // Force NVDA/JAWS to re-announce the new status text (see RenderStatus).
            BeginInvoke((Action)(() => SendKeys.Send("{UP}")));
        }

        // Natural (unstretched) bottom Y of the advanced lists block for however many of
        // TX1/TX2/Raw are shown, always derived from the fixed base sizes below -- never
        // from the lists' current (possibly window-stretched) positions/sizes. Sharing this
        // between ApplyAdvancedLayout and UpdateDebug keeps the per-configuration minimum
        // window height stable instead of ratcheting up every time the window grows.
        private int NaturalAdvancedListsBottom(bool showTx1, bool showTx2, bool showRaw, out int baseListH, out int baseRawH)
        {
            const int startY   = 376;   // first label Y (same as designer baseline)
            const int labelH   = 14;    // approx height of bold 8.25pt label
            const int labelGap = 2;     // gap between label bottom and list top
            const int groupGap = 6;     // gap between list bottom and next label

            int count = (showTx1 ? 1 : 0) + (showTx2 ? 1 : 0) + (showRaw ? 1 : 0);
            switch (count)
            {
                case 1:  baseListH = 200; baseRawH = 200; break;
                case 2:  baseListH = 120; baseRawH = 120; break;
                default: baseListH = 77;  baseRawH = 92;  break;   // 3 lists: original proportions
            }

            int bottom = startY;
            if (showTx1) bottom += labelH + labelGap + baseListH + groupGap;
            if (showTx2) bottom += labelH + labelGap + baseListH + groupGap;
            if (showRaw) bottom += labelH + labelGap + baseRawH + groupGap;
            return bottom;
        }

        private void UpdateDebug()
        {
            SuspendLayout();
            FormBorderStyle = FormBorderStyle.Sizable;
            label1.Visible = wsjtxClient.debug;
            label2.Visible = wsjtxClient.debug;
            label4.Visible = wsjtxClient.debug;
            label5.Visible = wsjtxClient.debug;
            label6.Visible = wsjtxClient.debug;
            label7.Visible = wsjtxClient.debug;
            label8.Visible = wsjtxClient.debug;
            label9.Visible = wsjtxClient.debug;
            label10.Visible = wsjtxClient.debug;
            label11.Visible = wsjtxClient.debug;
            label12.Visible = wsjtxClient.debug;
            label13.Visible = wsjtxClient.debug;
            label14.Visible = wsjtxClient.debug;
            label15.Visible = wsjtxClient.debug;
            label16.Visible = wsjtxClient.debug;
            label17.Visible = wsjtxClient.debug;
            label18.Visible = wsjtxClient.debug;
            label19.Visible = wsjtxClient.debug;
            label20.Visible = wsjtxClient.debug;
            label21.Visible = wsjtxClient.debug;
            label22.Visible = wsjtxClient.debug;
            label23.Visible = wsjtxClient.debug;
            label24.Visible = wsjtxClient.debug;
            label25.Visible = wsjtxClient.debug;
            label26.Visible = wsjtxClient.debug;
            label27.Visible = wsjtxClient.debug;
            label28.Visible = wsjtxClient.debug;
            label29.Visible = wsjtxClient.debug;
            label30.Visible = wsjtxClient.debug;
            label31.Visible = wsjtxClient.debug;
            label32.Visible = wsjtxClient.debug;
            label33.Visible = wsjtxClient.debug;
            label34.Visible = wsjtxClient.debug;
            if (wsjtxClient.debug)
            {
#if DEBUG
                AllocConsole();
                ShowWindow(GetConsoleWindow(), 5);
#endif
                WindowState = FormWindowState.Maximized;
                wsjtxClient.UpdateDebug();
            }
            else
            {
                bool anyAdvList = advancedCallLayout && (advShowTx1 || advShowTx2 || advShowRaw);
                int naturalHeight;
                if (anyAdvList)
                {
                    int bottom = NaturalAdvancedListsBottom(advShowTx1, advShowTx2, advShowRaw, out _, out _);
                    naturalHeight = bottom + 45;
                }
                else
                {
                    naturalHeight = sortOrderButton.Location.Y + sortOrderButton.Height + 45;
                }
                // Spot Watch now requires Advanced Call Layout to be enabled, so its own height
                // requirement only applies when both flags are on.
                if (advancedCallLayout && showSpotWatch)
                    naturalHeight = Math.Max(naturalHeight, spotWatchListBox.Location.Y + spotWatchListBox.Height + 45);
                // 390 is the original Designer minimum height (today's default/safe floor);
                // never let the per-configuration natural height shrink below it.
                MinimumSize = new Size(MinimumSize.Width, Math.Max(390, naturalHeight));
#if DEBUG
                ShowWindow(GetConsoleWindow(), 0);
#endif
            }
            ResumeLayout();
        }

        private void skipGridCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (!formLoaded) return;

            skipGridCheckBox.Text = "Skip grid (pending)";
            skipGridCheckBox.ForeColor = Color.DarkGreen;
            wsjtxClient.WsjtxSettingChanged();
        }

        private void useRR73CheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (!formLoaded) return;

            useRR73CheckBox.Text = "Use RR73 (pending)";
            useRR73CheckBox.ForeColor = Color.DarkGreen;
            wsjtxClient.WsjtxSettingChanged();
        }

        public void WsjtxSettingConfirmed()
        {
            skipGridCheckBox.Text = "Skip grid msg";
            skipGridCheckBox.ForeColor = Color.Black;
            useRR73CheckBox.Text = "Use RR73 msg";
            useRR73CheckBox.ForeColor = Color.Black;
        }

        // Ongoing safety-net repair (see LogbookDb.BackfillMissingStates) for QSOs logged
        // with a blank state despite the callsign being derivable. Runs every startup, not
        // just once -- the underlying query is a cheap indexed lookup (ix_state) that finds
        // nothing to fix once existing gaps are resolved, so re-checking costs almost nothing
        // but catches a fresh gap automatically if one ever reappears from a source this
        // doesn't already cover. Offline only (FCC ULS/cached QRZ data via lookupManager.Build,
        // then grid.dat) -- never a live query. Must run after lookupManager is initialized and
        // before LoadHrcCache()/RefreshStillNeedCache() so the first cache build already
        // reflects any corrected states.
        private void BackfillMissingStates()
        {
            try
            {
                using (var db = new LogbookDb())
                {
                    int fixedCount = db.BackfillMissingStates(call => lookupManager?.Build(call)?.State);
                    if (fixedCount > 0)
                        db.SetMeta("state_backfill_last_fixed", $"{DateTime.UtcNow:o} ({fixedCount} rows)");
                }
            }
            catch { /* best-effort repair -- must never block startup */ }
        }

        // Loads the HRC database filter sets into WsjtxClient's in-memory caches.
        // Checks the whole log regardless of band -- WAS/DXCC/WAZ don't require a
        // state/entity/zone to be confirmed on any particular band, so a station
        // confirmed on 20m must not show as "needed" again just because the radio
        // is now on 10m. (Previously this always filtered to the current band,
        // which meant changing bands could wrongly resurrect nearly everything as
        // "needed" -- see JimmyTests.RuleEngineBandIndependenceTests for the
        // regression guard.) Safe to call any time; silently skips if the DB is
        // unavailable or empty.
        public void LoadHrcCache()
        {
            if (wsjtxClient == null) return;
            try
            {
                using (var db = new LogbookDb())
                {
                    HashSet<string> neededStates;
                    HashSet<string> unconfirmedStates;
                    HashSet<int>    unconfirmedDxcc;
                    HashSet<int>    neededZones;
                    db.LoadHrcCache(out neededStates, out unconfirmedStates, out unconfirmedDxcc, out neededZones);
                    wsjtxClient.hrcNeededStates      = neededStates;
                    wsjtxClient.hrcUnconfirmedStates = unconfirmedStates;
                    wsjtxClient.hrcUnconfirmedDxcc   = unconfirmedDxcc;
                    wsjtxClient.hrcNeededZones       = neededZones;
                }
            }
            catch { }
        }

        // Rebuilds WsjtxClient's live-tag cache from every Rule Definition currently checked
        // in activeAwardRuleIds. Several awards can be tracked at once; each gets its own entry
        // in wsjtxClient.activeAwardTags. Only evaluates the RuleEngine here, at selection/refresh
        // time; decode-time matching is a plain HashSet lookup per active award. Safe to call any
        // time. Rules that can't be found, fail RuleEngine.SupportsLiveTag(def), or have no fixed
        // still-needed checklist (e.g. a Target=COUNT/LEVELS award) are simply left out.
        //
        // Only scoped to the current band when the award definition itself restricts to specific
        // bands ([Match] Bands=) -- mirrors LoadHrcCache()'s per-band semantics for that case. Most
        // shipped awards (Colonies13, DXCC, WAS, WAZ, ...) don't set Bands=, since they all count a
        // station worked on any band -- for those, evaluating against the current band only was a
        // bug: work a station on 20m, switch to 15m, and it would wrongly show as still needed
        // again. Matches the Still Need tab's own "All Bands" default for the same reason.
        // A handful of awards DO set Bands= to a single fixed band (e.g. the WAS_*M per-band
        // awards), so BandAppliesToLiveTag() gates the whole thing on the current band actually
        // being one of the award's own bands -- otherwise the current band would get silently
        // substituted for the award's band, tagging decodes on the wrong band as "needed" for it.
        public void RefreshStillNeedCache()
        {
            if (wsjtxClient == null) return;

            var tags = new Dictionary<string, WsjtxClient.ActiveAwardTag>();
            foreach (string ruleId in activeAwardRuleIds)
            {
                var def = RuleLibrary.Definitions.FirstOrDefault(d => d.Enabled && d.Id == ruleId);
                if (!RuleEngine.SupportsLiveTag(def)) continue;
                if (!BandAppliesToLiveTag(def.Bands, wsjtxClient.CurrentBandStr)) continue;

                try
                {
                    string band = def.Bands.Count > 0 ? wsjtxClient.CurrentBandStr : null;
                    var result = RuleEngine.EvaluateBand(def, band);
                    if (result.StillNeeded == null) continue;   // no fixed checklist to tag against

                    tags[ruleId] = new WsjtxClient.ActiveAwardTag
                    {
                        RuleId   = ruleId,
                        RuleName = def.Name,
                        GroupBy  = def.GroupBy,
                        Set      = new HashSet<string>(result.StillNeeded, StringComparer.OrdinalIgnoreCase),
                    };
                }
                catch { /* skip this rule, keep the others */ }
            }
            wsjtxClient.activeAwardTags = tags;
            wsjtxClient.RefreshQueuedAwardTags();
        }

        public void RefreshLogbookWindowIfOpen()
        {
            if (_logbookWindow != null && !_logbookWindow.IsDisposed)
                _logbookWindow.RefreshCurrentPage();
        }

        public void OpenLogbookWindow()
        {
            if (_logbookWindow != null && !_logbookWindow.IsDisposed)
            {
                _logbookWindow.Activate();
                return;
            }
            try
            {
                _logbookWindow = new LogbookWindow(iniFile,
                    () => qrzLogbookApiKey, () => lotwLogbookUser, () => lotwLogbookPass,
                    () => clubLogUploadEmail, () => clubLogUploadPassword, () => clubLogUploadCallsign,
                    () => eqslUsername, () => eqslPassword,
                    onImportComplete: () => BeginInvoke(new Action(() =>
                        { PruneStaleActiveAwardRuleIds(); LoadHrcCache(); RefreshStillNeedCache(); })),
                    initialActiveAwardRuleIds: activeAwardRuleIds,
                    onActiveAwardRuleIdsChanged: (ruleId, isTracked) =>
                    {
                        if (isTracked) activeAwardRuleIds.Add(ruleId);
                        else activeAwardRuleIds.Remove(ruleId);
                        iniFile?.Write("activeAwardRuleIds", FormatActiveAwardRuleIds(activeAwardRuleIds));
                        RefreshStillNeedCache();
                    },
                    resolveUsState: call => lookupManager?.Build(call)?.State,
                    isWsjtxConnected: () => wsjtxClient != null && wsjtxClient.ConnectedToWsjtx(),
                    currentBand: () => wsjtxClient?.CurrentBandStr,
                    currentMode: () => wsjtxClient?.CurrentMode,
                    lookupCallsign: call => lookupManager?.Build(call),
                    onQsoLogged: () => wsjtxClient?.Sounds?.PlaySoundEvent(loggedCheckBox.Checked, soundFile_Logged));
                // Deliberately no Owner assignment -- an owned window is always kept in front
                // of its owner at the Win32 level, which made it impossible to Alt+Tab back to
                // Jimmy's main window while the Logbook was open (found 2026-07-11: previously
                // added as a belt-and-suspenders way to ensure this closes with Jimmy, but
                // Controller_FormClosing already does that explicitly via _logbookWindow?.Close()
                // below, so Owner was redundant for that and only cost the Alt+Tab behavior).
                _logbookWindow.FormClosed += (s, e) => _logbookWindow = null;
                _logbookWindow.Show();
            }
            catch (Exception ex)
            {
                _logbookWindow = null;
                MessageBox.Show(
                    ex.GetType().Name + ": " + ex.Message + "\r\n\r\n" + ex.StackTrace,
                    "Logbook Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        public void OpenOtaSpotsWindow()
        {
            if (_otaSpotsWindow != null && !_otaSpotsWindow.IsDisposed)
            {
                _otaSpotsWindow.Activate();
                return;
            }
            try
            {
                _otaSpotsWindow = new OtaSpotsWindow(lookupManager,
                    () => wsjtxClient?.activeAwardTags, () => wsjtxClient?.CurrentBandStr);
                // Deliberately no Owner assignment -- see the matching comment on
                // _logbookWindow's Show() call above; Controller_FormClosing already closes
                // this explicitly via _otaSpotsWindow?.Close().
                _otaSpotsWindow.FormClosed += (s, e) => _otaSpotsWindow = null;
                _otaSpotsWindow.Show();
            }
            catch (Exception ex)
            {
                _otaSpotsWindow = null;
                MessageBox.Show(
                    ex.GetType().Name + ": " + ex.Message + "\r\n\r\n" + ex.StackTrace,
                    "POTA / SOTA / DX Spots Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        public void optionsButton_Click(object sender, EventArgs e)
        {
            if (!formLoaded) return;
            if (wsjtxClient.ConnectedToWsjtx()) wsjtxClient.HaltTuning();
            initialConnFaultTimer.Stop();

            if (optionsDlg != null)
            {
                optionsDlg.BringToFront();
                return;
            }

            guideTimer.Start();
        }

        private void guideTimer_Tick(object sender, EventArgs e)
        {
            guideTimer.Stop();
            optionsDlg = new OptionsDlg(wsjtxClient, this);
            // No Owner -- see the matching comment on _logbookWindow's Show() call; an owned
            // window always stays in front of its owner at the Win32 level, which breaks
            // Alt+Tab back to the main window. Controller_FormClosing already closes this
            // explicitly (optionsDlg?.Close()), so Owner isn't needed for that either.
            optionsDlg.Show();
        }

        public void OptionsDlgClosed()
        {
            initialConnFaultTimer.Start();
            TopMost = alwaysOnTop;
            wsjtxClient.suspendComm   = false;
            wsjtxClient.lotwBoostEnabled = lotwBoostEnabled;
            wsjtxClient.Sounds.RefreshResourceFileCache();
            optionsDlg = null;
            lookupManager?.Initialize(
                useLookupData,
                qrzEnabled, qrzUsername, qrzPassword, qrzCacheDays,
                lotwEnabled, lotwRefreshDays,
                ClubLogAppKey.Resolve(), clubLogRefreshDays,
                fccUlsEnabled,
                qrzLookupPolicy, qrzMinIntervalSeconds,
                callsignLookupProvider,
                hamQthEnabled, hamQthUsername, hamQthPassword, hamQthCacheDays);
            lookupManager?.StartBackgroundRefreshIfNeeded(lotwRefreshDays, clubLogRefreshDays, fccUlsRefreshDays);
            wsjtxClient.SortCallsPublic();  // re-rank if LoTW boost changed
        }

        public void LookupFocusedCall()
        {
            string call = null;

            // Whichever list currently has keyboard focus wins, so the lookup always
            // matches what's actually selected -- works the same in every list.
            if (callListBox.Visible && callListBox.Focused)
            {
                int idx = callListBox.SelectedIndex;
                if (idx >= 0)
                    call = wsjtxClient.GetCallAtIndex(wsjtxClient.MapNormalListIndex(idx));
            }
            else if (advTx1ListBox.Visible && advTx1ListBox.Focused)
            {
                int idx = advTx1ListBox.SelectedIndex;
                if (idx >= 0) call = wsjtxClient.GetCallAtTx1Index(idx);
            }
            else if (advTx2ListBox.Visible && advTx2ListBox.Focused)
            {
                int idx = advTx2ListBox.SelectedIndex;
                if (idx >= 0) call = wsjtxClient.GetCallAtTx2Index(idx);
            }
            else if (advRawListBox.Visible && advRawListBox.Focused)
            {
                int idx = advRawListBox.SelectedIndex;
                if (idx >= 0) call = wsjtxClient.GetCallAtRawIndex(idx);
            }
            else if (logListBox.Visible && logListBox.Focused)
            {
                int idx = logListBox.SelectedIndex;
                if (idx >= 0 && idx < _loggedKeys.Count) call = _loggedKeys[idx];
            }
            else if (callListBox.Visible)
            {
                // Nothing focused (e.g. hotkey pressed from main status) -- fall back to
                // the normal-layout "Stations calling" selection, as before.
                int idx = callListBox.SelectedIndex;
                if (idx >= 0)
                    call = wsjtxClient.GetCallAtIndex(wsjtxClient.MapNormalListIndex(idx));
            }
            else
            {
                // Advanced layout, nothing focused: try TX1 then TX2 selected index.
                int idx = advTx1ListBox.SelectedIndex;
                if (idx >= 0) call = wsjtxClient.GetCallAtTx1Index(idx);
                if (call == null)
                {
                    idx = advTx2ListBox.SelectedIndex;
                    if (idx >= 0) call = wsjtxClient.GetCallAtTx2Index(idx);
                }
            }
            if (string.IsNullOrEmpty(call)) return;
            using (var dlg = new LookupInfoDlg(call, lookupManager))
            {
                dlg.ShowDialog(this);
                if (dlg.PrimaryLookupOccurred)
                    wsjtxClient?.DebugChanged();
            }
        }
        public void HelpClosed()
        {
            initialConnFaultTimer.Start();
            helpDlg = null;
            RestoreFocus(_helpReturnFocus);
            _helpReturnFocus = null;
        }

        public void ShowMsg(string text, bool sound)
        {
            // No raw Windows system beep here, ever -- confirmed with the user, 2026-08-11:
            // only Jimmy's own configured notification sounds (Options > Sounds) should ever
            // be audible; hotkeys, Escape, and invalid-key rejection must stay silent. `sound`
            // is kept as a parameter (unused now) so every existing call site stays valid.

            statusText.Text = text;
            statusText.SelectionStart = 0;
            statusText.SelectionLength = 0;
            // Force NVDA/JAWS to announce this immediately, same guard as RenderStatus --
            // ShowStatus() will naturally overwrite this text on the next status rebuild
            // (see ToggleTxFirst for the same accepted pattern), which is fine: by then
            // the screen reader has already started speaking this message.
            // Hardened 2026-08-19 (release-blocker follow-up): announced now also requires real
            // OS-level foreground state (GetForegroundWindow() == this.Handle), not just the two
            // WinForms-internal tracking properties. Root-caused live: on a genuine first-run
            // launch, Controller.ApplyEngineMode()'s new config-check message (see its own
            // comment) fires synchronously from inside Form_Load, before the window has
            // necessarily become the REAL OS foreground window -- confirmed via live
            // instrumentation that statusText.Focused/Form.ActiveForm and the real
            // GetForegroundWindow() result can disagree at that exact point (this specific
            // session's own test happened to read the WinForms-internal pair as already false,
            // skipping SendKeys that time, but nothing guaranteed that on every machine/timing --
            // this closes the gap structurally rather than relying on a race). See Form_Load's
            // own end-of-method comment (the ORIGINAL, pre-existing "SendKeys fired too early
            // leaves keyboard input going nowhere" finding, 2026-07-10) for the exact failure
            // class this guards against: SendKeys.Send's journal-hook mechanism targets whatever
            // window Windows currently considers the real foreground, and firing it while that's
            // NOT this window can leave real keyboard input going to the wrong place for the rest
            // of the session. This is the single choke point every ShowMsg caller shares, so
            // hardening it here protects all of them, not just the one call site that surfaced it.
            bool announced = statusText.Focused && Form.ActiveForm == this && GetForegroundWindow() == this.Handle;
            // [ANNOUNCE] tag: enable the UDP diag log (Options/Setup) to get a millisecond-
            // timestamped record of every real screen-reader announcement -- added 2026-08-07
            // to pin down live reports of announcements landing partway into the next period,
            // which is very hard to time by ear alone. announced=False means the text was
            // updated but JAWS/NVDA was never actually nudged (statusText wasn't focused).
            wsjtxClient?.DebugOutput($"{wsjtxClient.Time()} [ANNOUNCE announced={announced}] '{text}'");
            if (announced)
                SendKeys.Send("{UP}");
        }

        // QRZ/Club Log upload progress (catch-up loop, real-time circuit breaker)
        // goes here instead of plain ShowMsg -- mirrors the same message into the
        // Ham Radio Center's own status bar when it's open, so someone working in
        // that window (Awards, My Log, etc.) can watch upload progress there too,
        // not just on the main form.
        public void ShowUploadStatus(string text, bool sound)
        {
            ShowMsg(text, sound);
            if (_logbookWindow != null && !_logbookWindow.IsDisposed)
                _logbookWindow.SetStatus(text);
        }

        // Logbook window's own status bar only, no main-form announcement -- used for
        // LogbookAutoSync's per-service detail messages, which would be too noisy for
        // the main status bar (up to 3 services' worth of start/result messages). If
        // the window isn't open, this is a silent no-op; the sync still runs either way.
        public void ShowLogbookWindowStatus(string text)
        {
            if (_logbookWindow != null && !_logbookWindow.IsDisposed)
                _logbookWindow.SetStatus(text);
        }

        // Fires once per session, the first time WsjtxClient.CheckActive() transitions
        // START->ACTIVE (see the call site there). Waits a fixed delay so the auto-sync
        // check never collides with the startup status announcement, then runs
        // LogbookAutoSync -- which itself no-ops entirely if nothing is due.
        public void OnJimmyReachedActive()
        {
            if (logbookAutoSyncTimer != null) return; // already scheduled/ran this session
            logbookAutoSyncTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            logbookAutoSyncTimer.Tick += async (s, e) =>
            {
                logbookAutoSyncTimer.Stop();
                var sync = new LogbookAutoSync(iniFile,
                    () => qrzLogbookApiKey, () => lotwLogbookUser, () => lotwLogbookPass,
                    () => clubLogUploadEmail, () => clubLogUploadPassword, () => clubLogUploadCallsign,
                    call => lookupManager?.Build(call)?.State,
                    text => ShowMsg(text, false),
                    ShowLogbookWindowStatus)
                {
                    QrzAutoSyncEnabled     = qrzLogbookAutoSyncEnabled,
                    QrzRefreshDays         = qrzLogbookRefreshDays,
                    LotwAutoSyncEnabled    = lotwLogbookAutoSyncEnabled,
                    LotwRefreshDays        = lotwLogbookRefreshDays,
                    ClubLogAutoSyncEnabled = clubLogLogbookAutoSyncEnabled,
                    ClubLogRefreshDays     = clubLogLogbookRefreshDays,
                };
                try { await sync.RunDueSyncsAsync(); }
                catch (Exception ex) { ShowMsg("Logbook auto-sync error: " + ex.Message, false); }
            };
            logbookAutoSyncTimer.Start();
        }

        // IJimmyStatusView / IJimmyQueueView / IJimmyLogView (Phase 2.3/2.4 first wave) --
        // these bodies are moved verbatim from WsjtxClient.ShowStatus()/ShowQueue()/ShowLogged()'s
        // former UI-touching tails; the business logic that builds headerText/items/colors stays
        // in WsjtxClient, which now calls these instead of touching controls directly.
        // Below this gap, a second RenderStatus() call with byte-identical text is treated
        // as an accidental duplicate (two code paths independently deciding to announce the
        // same thing), not a legitimate periodic re-announcement -- the shortest real T/R
        // period (FT4) is 7.5s, so any genuine "still Receiving"-style repeat is always many
        // times further apart than this.
        private static readonly TimeSpan RepeatStatusAnnounceSuppressWindow = TimeSpan.FromSeconds(3);
        private string _lastAnnouncedStatusText;
        private DateTime _lastAnnouncedStatusTime = DateTime.MinValue;

        public void RenderStatus(string headingText, string statusText, Color foreColor, Color backColor)
        {
            statusHeadingLabel.Text = headingText;
            this.statusText.AccessibleName = headingText;
            this.statusText.ForeColor = foreColor;
            this.statusText.BackColor = backColor;
            this.statusText.Text = statusText;
            this.statusText.SelectionStart = 0;
            this.statusText.SelectionLength = 0;
            // Guard: only send if Jimmy is actually the active application.
            // SendKeys.Send uses SendInput(), which delivers to the real OS foreground window;
            // without this guard a timer tick during focus-loss can send to Notepad. Hardened
            // 2026-08-19 (release-blocker follow-up, same fix as ShowMsg's identical guard --
            // see its own comment for the full root-cause writeup): the two WinForms-internal
            // properties alone are not sufficient evidence of real OS foreground state, so this
            // now also requires GetForegroundWindow() == this.Handle.
            bool announced = this.statusText.Focused && Form.ActiveForm == this && GetForegroundWindow() == this.Handle;
            // Added 2026-08-10: suppress the screen-reader nudge for a near-immediate repeat
            // of the exact same text -- root-caused live from a real QSO with W4MAA, where a
            // decode arriving milliseconds after a transmit-start event triggered a SECOND
            // ShowStatus()->RenderStatus() call with identical "Transmitting, W4MAA, sending
            // EN34." text (one from the transmit-start transition, one from the shared
            // decode-processing pipeline's own routine ShowStatus() call), 269ms apart.
            // SendKeys.Send("{UP}") below always fired regardless of whether anything
            // actually changed, so the operator heard the same status spoken twice, which
            // read as a doubled/garbled "Transmit... Transmitting...". Text/colors are still
            // applied every time above (cheap, idempotent) -- only the redundant nudge is
            // skipped, and only within this short window; a legitimate periodic repeat of
            // the same text (e.g. still "Receiving..." a full T/R period later) is far
            // outside RepeatStatusAnnounceSuppressWindow and still announces normally.
            bool isNearImmediateRepeat = announced && statusText == _lastAnnouncedStatusText
                && (DateTime.UtcNow - _lastAnnouncedStatusTime) < RepeatStatusAnnounceSuppressWindow;
            if (isNearImmediateRepeat) announced = false;
            // [ANNOUNCE] tag: see ShowMsg's identical logging for what this is for.
            wsjtxClient?.DebugOutput($"{wsjtxClient.Time()} [ANNOUNCE announced={announced}]{(isNearImmediateRepeat ? " (repeat suppressed)" : "")} '{statusText}'");
            if (announced)
            {
                SendKeys.Send("{UP}");  //triggers screen reader
                _lastAnnouncedStatusText = statusText;
                _lastAnnouncedStatusTime = DateTime.UtcNow;
            }
        }

        public void ShowMessage(string text, bool sound) => ShowMsg(text, sound);

        // See IJimmyStatusView.WouldAnnounce's own comment. Mirrors ShowMsg's internal
        // `announced` check exactly (statusText.Focused && Form.ActiveForm == this &&
        // GetForegroundWindow() == this.Handle, via IsJimmyForegrounded()) -- the two must never
        // drift apart, since this exists specifically to answer "would ShowMsg's own path already
        // say this out loud?"
        //
        // Release-audit finding, 2026-08-20 (confirmed bug, high severity): this WAS missing the
        // GetForegroundWindow() == this.Handle clause -- commit 8e44527 ("Harden every SendKeys
        // re-announce with a real OS foreground check") added that third condition to ShowMsg's
        // own `announced` (line 2230 above) but never updated this property to match, despite
        // this comment already claiming the two "must never drift apart". Effect: exactly when
        // WinForms-internal focus state says "focused/active" but the real OS foreground window
        // is something else (the scenario the foreground hardening exists for), ShowMsg's real
        // `announced` correctly comes back false (stays silent), but this used to still return
        // true -- UiaAlertNotificationDelivery (NotificationDelivery.cs) then skips raising its
        // own UIA notification, believing ShowMsg already spoke it. Net result: an Important
        // notification (connection lost, CAT link lost, high-SWR halt, clock out of sync) was
        // announced through NEITHER path -- a genuinely lost notification. Only reachable with
        // "Announce important notifications when focus is elsewhere" enabled (off by default).
        public bool WouldAnnounce => statusText.Focused && Form.ActiveForm == this && IsJimmyForegrounded();

        // See IJimmyStatusView.RaiseAccessibleAlert's own comment. AutomationNotificationKind.
        // Other / AutomationNotificationProcessing.ImportantMostRecent are a reasonable starting
        // point (interrupts any currently-queued same-source notification with this one), not a
        // value confirmed against real JAWS/NVDA behavior yet -- see the "Announce important
        // notifications when focus is elsewhere" General-tab option's own comment for why this
        // stays off by default until that live testing happens.
        public void RaiseAccessibleAlert(string text)
        {
            try
            {
                statusText.AccessibilityObject.RaiseAutomationNotification(
                    System.Windows.Forms.Automation.AutomationNotificationKind.Other,
                    System.Windows.Forms.Automation.AutomationNotificationProcessing.ImportantMostRecent,
                    text);
            }
            catch
            {
                // Best-effort only -- an AT/OS combination that doesn't support UIA notifications
                // must never crash Jimmy or fall back to focus movement/self-voicing.
            }
        }

        // Finds where the previously-selected row (identified by oldKeys[oldSelectedIndex])
        // landed in the new list, by identity rather than raw position. Returns -1 (no
        // selection) if oldSelectedIndex was invalid or that key is no longer present --
        // a safe failure mode, since guessing a nearby replacement risks silently landing
        // on an unrelated station (see the WM3PEN/N8BB mismatch this was built to fix).
        public static int FindPreservedSelectionIndex(List<string> oldKeys, int oldSelectedIndex, List<string> newKeys)
        {
            if (oldKeys == null || newKeys == null) return -1;
            if (oldSelectedIndex < 0 || oldSelectedIndex >= oldKeys.Count) return -1;
            return newKeys.IndexOf(oldKeys[oldSelectedIndex]);
        }

        // Whether a band-restricted award ([Match] Bands=) should live-tag decodes on the
        // radio's current band. An award with no band restriction always applies (band is
        // irrelevant to it). An award restricted to specific bands only applies when the
        // current band is actually one of them -- otherwise RefreshStillNeedCache() must skip
        // it rather than substitute the current band for the award's own band, which would
        // silently tag decodes on the wrong band as satisfying that award (e.g. tagging a 15m
        // station as "Needed" for a 160m-only award while operating on 15m).
        public static bool BandAppliesToLiveTag(List<string> defBands, string currentBand)
        {
            if (defBands == null || defBands.Count == 0) return true;
            if (string.IsNullOrEmpty(currentBand)) return false;
            return defBands.Any(b => b.Equals(currentBand, StringComparison.OrdinalIgnoreCase));
        }

        private List<string> _callQueueKeys = new List<string>();
        private List<WsjtxClient.CallCategory> _callQueueCategories = new List<WsjtxClient.CallCategory>();

        public void RenderCallQueue(string headerText, List<string> items, List<string> keys, List<WsjtxClient.CallCategory> categories, SelectionMode selectionMode)
        {
            replyListLabel.Text = headerText;
            _callQueueCategories = categories;

            bool changed = callListBox.SelectionMode != selectionMode || callListBox.Items.Count != items.Count;
            if (!changed)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if ((string)callListBox.Items[i] != items[i]) { changed = true; break; }
                }
            }
            if (!changed) return;

            bool focused = callListBox.Focused;
            int prevIndex = focused ? callListBox.SelectedIndex : -1;
            int newIndex = FindPreservedSelectionIndex(_callQueueKeys, prevIndex, keys);
            _callQueueKeys = keys;

            if (callListBox.SelectionMode != selectionMode)
                callListBox.SelectionMode = selectionMode;

            callListBox.BeginUpdate();
            try
            {
                callListBox.Items.Clear();
                callListBox.Items.AddRange(items.ToArray());
            }
            finally { callListBox.EndUpdate(); }

            if (focused && selectionMode != SelectionMode.None && newIndex >= 0)
                callListBox.SelectedIndex = newIndex;
        }

        private List<string> _rawDecodeKeys = new List<string>();
        private List<WsjtxClient.CallCategory> _rawDecodeCategories = new List<WsjtxClient.CallCategory>();

        public void RenderRawDecodes(List<string> items, List<string> keys, List<WsjtxClient.CallCategory> categories)
        {
            _rawDecodeCategories = categories;
            bool focused = advRawListBox.Focused;
            int prevIdx = focused ? advRawListBox.SelectedIndex : -1;
            bool changed = advRawListBox.Items.Count != items.Count;
            if (!changed)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if ((string)advRawListBox.Items[i] != items[i]) { changed = true; break; }
                }
            }
            if (!changed) return;

            int newIdx = keepListPositionDuringRefresh ? FindPreservedSelectionIndex(_rawDecodeKeys, prevIdx, keys) : -1;
            _rawDecodeKeys = keys;

            advRawListBox.BeginUpdate();
            try
            {
                advRawListBox.Items.Clear();
                advRawListBox.Items.AddRange(items.ToArray());
            }
            finally { advRawListBox.EndUpdate(); }
            if (keepListPositionDuringRefresh && focused && newIdx >= 0 && advRawListBox.Items.Count > 0)
                advRawListBox.SelectedIndex = newIdx;
        }

        private List<string> _tx1Keys = new List<string>();
        private List<string> _tx2Keys = new List<string>();
        private List<WsjtxClient.CallCategory> _tx1Categories = new List<WsjtxClient.CallCategory>();
        private List<WsjtxClient.CallCategory> _tx2Categories = new List<WsjtxClient.CallCategory>();

        // Note: unlike RenderCallQueue/RenderLoggedList/RenderRawDecodes, this does NOT return
        // early when nothing changed -- it mirrors WsjtxClient.ShowAdvancedQueue()'s original
        // structure exactly, which always attempts the selection restore after the list update
        // (a no-op in practice when nothing changed, but preserved verbatim rather than "cleaned up").
        public void RenderAdvancedList(bool isTx1Side, string accessibleName, List<string> items, List<string> keys, List<WsjtxClient.CallCategory> categories)
        {
            ListBox lb = isTx1Side ? advTx1ListBox : advTx2ListBox;
            if (lb.AccessibleName != accessibleName) lb.AccessibleName = accessibleName;
            if (isTx1Side) _tx1Categories = categories; else _tx2Categories = categories;

            bool focused = lb.Focused;
            int prevIdx = focused ? lb.SelectedIndex : -1;
            List<string> oldKeys = isTx1Side ? _tx1Keys : _tx2Keys;
            int newIdx = keepListPositionDuringRefresh ? FindPreservedSelectionIndex(oldKeys, prevIdx, keys) : -1;
            if (isTx1Side) _tx1Keys = keys; else _tx2Keys = keys;

            bool changed = lb.Items.Count != items.Count;
            if (!changed)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if ((string)lb.Items[i] != items[i]) { changed = true; break; }
                }
            }
            if (changed)
            {
                lb.BeginUpdate();
                try
                {
                    lb.Items.Clear();
                    lb.Items.AddRange(items.ToArray());
                }
                finally { lb.EndUpdate(); }
            }

            if (keepListPositionDuringRefresh && focused && newIdx >= 0 && lb.Items.Count > 0)
                lb.SelectedIndex = newIdx;
        }

        private List<string> _loggedKeys = new List<string>();

        public void RenderLoggedList(string headerText, List<string> items, List<string> keys)
        {
            loggedLabel.Text = headerText;

            bool changed = logListBox.Items.Count != items.Count;
            if (!changed)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if ((string)logListBox.Items[i] != items[i]) { changed = true; break; }
                }
            }
            if (!changed) return;

            bool focused = logListBox.Focused;
            int prevIdx = focused ? logListBox.SelectedIndex : -1;
            int newIdx = FindPreservedSelectionIndex(_loggedKeys, prevIdx, keys);
            _loggedKeys = keys;

            logListBox.BeginUpdate();
            try
            {
                logListBox.Items.Clear();
                logListBox.Items.AddRange(items.ToArray());
            }
            finally { logListBox.EndUpdate(); }
            if (focused && newIdx >= 0 && logListBox.Items.Count > 0)
                logListBox.SelectedIndex = newIdx;
        }

        private List<string> _spotWatchKeys = new List<string>();

        // Runs every 60 seconds so each row's "last spotted X min ago" age counts forward
        // smoothly, instead of only updating whenever a new MQTT spot happens to arrive for
        // some watched call (which could leave the display frozen for many minutes, then jump).
        // RenderSpotWatchList's own change-detection guard keeps this a no-op (no redraw, no
        // screen-reader chatter) unless a formatted row string actually changed.
        private void spotWatchAgeTimer_Tick(object sender, EventArgs e)
        {
            RenderSpotWatchList();
        }

        // Native-only: Jimmy always runs its own engine host -- called once at startup and
        // again whenever OptionsDlg's Radio tab is saved (COM port/audio device/rig model
        // changes take effect immediately, no restart needed, matching ApplyRadioSettings'
        // own "Done when" shape).
        //
        // ConnectDirectEngine (WsjtxClient.Direct.cs) connects straight to the engine host's
        // known local control port -- no "detect a separately-running real WSJT-X" dance of
        // any kind (that legacy path used to flat-out crash the engine host and, separately,
        // could freeze the whole window; both confirmed live, 2026-08-07/08, and removed for
        // good along with the rest of the WSJT-X-external/Andy-fork compatibility code, and
        // then the classic-UDP transport itself, ConnectNativeEngine/UdpLoop, was removed
        // entirely in the 2026-08-18 UDP-to-Direct test-harness migration once nothing --
        // production or test mode -- called it anymore).
        //
        // TestModeGuard.IsTestMode connects over the exact same Direct control-port protocol
        // production uses (JimmyDirectReplay.py's fake control-port server stands in for the
        // real jimmy-engine-host.exe, answering SNAPSHOT/REPLY/etc. on 127.0.0.1:<ControlPort>)
        // but this method still never spawns the actual jimmy-engine-host.exe process (see the
        // early return below), so a replay-test session can never open a real audio device, a
        // real COM port, or key a real radio.
        public void ApplyEngineMode()
        {
            // Still needed below for the real engine host's own --jimmy-addr argument (Launch()
            // passes it unconditionally; Nexus's run_radio sends legacy WSJT-X-protocol UDP
            // packets there regardless of transport mode -- Jimmy's own Direct-mode client simply
            // never listens for them in production, exactly as ARCHITECTURE.md documents). Not
            // related to the TestModeGuard.IsTestMode branch below at all -- kept exactly as
            // before, just no longer read by anything in the test-mode branch itself.
            int jimmyPort = wsjtxClient?.port > 0 ? wsjtxClient.port : 2237;
            // UDP-to-Direct test-harness migration, 2026-08-18: TestModeGuard.IsTestMode used to
            // force the classic WSJT-X UDP path here unconditionally (ConnectNativeEngine),
            // because JimmyReplay.py only spoke that standard protocol, not the Direct control
            // channel. That was the last piece of load-bearing UDP infrastructure in this
            // codebase -- production itself has been Direct-only since the 2026-08-12 parity
            // pass (see WsjtxClient.Direct.cs's own header comment). Closing that gap the right
            // way means test mode uses the SAME transport production does, not a parallel one
            // that only proves the (now-dead) UDP path still parses bytes correctly. Test mode
            // and production now differ in exactly one way: test mode never launches the real
            // engine host (the early "if (TestModeGuard.IsTestMode) return;" below), so
            // JimmyDirectReplay.py's fake control-port server is what Jimmy actually talks to
            // instead of jimmy-engine-host.exe -- everything downstream of ConnectDirectEngine
            // (classification, awards, call-queue ranking, notifications, band tracking) runs
            // completely unchanged, because it never knew UDP vs Direct in the first place, let
            // alone real-engine vs fake-engine-for-testing.
            wsjtxClient?.DisconnectDirectEngine();
            wsjtxClient?.ConnectDirectEngine(NativeEngine.MyCall, NativeEngine.MyGrid);

            nativeEngineClient?.Dispose();
            nativeEngineClient = null;
            if (TestModeGuard.IsTestMode) return;

            // 2026-08-19 fresh-install usability fix (release blocker): a genuine "not
            // configured yet" state, checked and handled BEFORE ever attempting Launch() --
            // not a reactive failure caught after the fact. Previously this method always
            // built a NativeEngineClient and started the background Launch() Task regardless
            // of whether My Call/My Grid were even set, which then failed via LastError and
            // surfaced through the "Native engine" ErrorWarningEvent (Error severity, forces
            // an audible cue) -- correct behavior, but framed exactly like a real malfunction
            // (exe missing, launch exception) instead of the normal, expected, first-run
            // condition it actually is, and worded around "the native engine", a concept the
            // operator should never need to know exists. ConnectDirectEngine() above still runs
            // unconditionally (test-mode's own fake control-port server needs that same Direct
            // state-tracking reset regardless of My Call/My Grid), and nativeEngineClient is
            // already null from just above, so engine-dependent functions correctly stay
            // unavailable -- everything else (Options, menus, the rest of the UI) is untouched.
            // The moment My Call/My Grid are saved as valid, OptionsDlg's own engineIdentityChanged
            // check already calls ApplyEngineMode() again (no restart needed) -- this re-evaluates
            // the same check and proceeds to Launch() normally below.
            string configProblem = NativeEngineClient.DescribeConfigProblem(NativeEngine.MyCall, NativeEngine.MyGrid);
            if (configProblem != null)
            {
                ShowMsg(configProblem, true);
                return;
            }

            // Self-sufficiency plan Phase 5: no control-channel listener to stand up anymore --
            // the native engine host builds its own Rig directly (from the CLI args
            // NativeEngineClient.Launch derives from `Radio` below) whenever Radio.Mode ==
            // HamlibRigctld, regardless of Radio.PttEnabled (control and PTT are independent --
            // S-meter/frequency tracking stays live even with PTT itself off; Launch forces the
            // PTT method to Vox when PttEnabled is false, so it never attempts to key the radio
            // in that case -- degrades to receive-only for PTT specifically, not a silent
            // failure). Under WsjtxCat, Launch passes no rig args at all -- receive-only for
            // radio entirely, exactly as before.
            var client = new NativeEngineClient();
            nativeEngineClient = client;
            string myCall = NativeEngine.MyCall, myGrid = NativeEngine.MyGrid;
            string inDevice = NativeEngine.AudioInputDevice, outDevice = NativeEngine.AudioOutputDevice;
            RadioSettings radioSnapshot = Radio;
            DecodeSettings decodeSnapshot = Decode;
            WsjtxClient wsjtx = wsjtxClient;

            // Launch() (specifically Process.Start()) runs on a background thread -- Process.Start()
            // for a new, unsigned exe is well known to be able to block synchronously on real-time
            // AV scanning or SmartScreen's Mark-of-the-Web check.
            System.Threading.Tasks.Task.Run(() =>
            {
                // Before onUnexpectedExit existed, a real crash of the engine host was completely
                // silent -- Jimmy kept showing stale decode/radio state forever with no sign the
                // process backing it was gone (found live, 2026-08-06/07, auditing everything else
                // wrong with real-time visibility into the native engine's health). Marshal to the
                // UI thread explicitly -- Process.Exited fires on a threadpool thread, and
                // ShowMessage touches statusText.
                bool ok = client.Launch(myCall, myGrid, inDevice, jimmyPort, outDevice, radioSnapshot,
                    msg => wsjtx?.DebugOutput(msg),
                    () => BeginInvoke(new Action(() => OnNativeEngineUnexpectedExit(client))),
                    decodeSnapshot, wsjtx != null && wsjtx.usePskReporter,
                    dxClusterAddress);
                if (!ok && nativeEngineClient == client)
                {
                    // Promoted from a raw ShowMessage (2026-08-19, notification-system-
                    // consistency pass) to the existing "Native engine" ErrorWarningEvent
                    // convention (same Source string already used by WsjtxClient.Protocol.cs's
                    // own engine-related errors) -- Error severity forces Important (a beep, and
                    // now also eligible for the off-focus UIA announcement), matching this
                    // event's own weight: TX/decode cannot work at all until this is resolved.
                    BeginInvoke(new Action(() =>
                        wsjtx?.Notify?.Publish(new ErrorWarningEvent(ErrorSeverity.Error, "Native engine", client.LastError))));
                }
            });
        }

        // Confirmed live, 2026-08-08: a real crash still happens occasionally (intermittently --
        // reproduced once in ~24s of a session, then not again across an hour-plus of runtime in
        // other sessions), and it's inside the native engine itself, not anything Jimmy's C# side
        // sends it (traced with cmd:7 -- the previously-confirmed crash trigger -- fully removed
        // from the codebase; the crash still happened). Root cause not yet found. Rather than
        // leave the operator stuck with a dead engine (no decoding, no TX) until they notice and
        // manually reopen Options, auto-restart once the message is shown -- "never knowingly lose
        // a valid FT8/FT4 station or QSO opportunity" applies just as much to Jimmy's own crashes
        // as to missed decodes. Capped and backed off so a persistently-crashing engine (a real
        // config problem, not a transient fault) degrades to a clear "stopped trying" message
        // instead of a tight restart loop hammering the audio device/COM port.
        private const int MaxNativeEngineAutoRestartsPerWindow = 5;
        private static readonly TimeSpan NativeEngineAutoRestartWindow = TimeSpan.FromMinutes(5);
        // Extracted (see EngineRestartPolicy.cs's own comment) -- same bounded-retry counting,
        // now independently testable without a Form/Controller instance.
        private readonly EngineRestartPolicy _nativeEngineRestartPolicy =
            new EngineRestartPolicy(MaxNativeEngineAutoRestartsPerWindow, NativeEngineAutoRestartWindow);

        private void OnNativeEngineUnexpectedExit(NativeEngineClient exitedClient)
        {
            if (nativeEngineClient != exitedClient) return;    // superseded by a newer launch already

            if (!_nativeEngineRestartPolicy.RecordAttemptAndShouldRestart(out int attemptNumber))
            {
                // Promoted (2026-08-19, notification-system-consistency pass): the terminal
                // "gave up" moment -- distinct from the per-attempt "restarting (N/M)..."
                // message just below, which stays a routine status line (an automatic recovery
                // is already in progress at that point; nothing yet needs the operator's
                // attention). This one genuinely does: auto-recovery has stopped, and the
                // operator must act. Same "Native engine" ErrorWarningEvent convention as the
                // launch-failure path above.
                wsjtxClient?.Notify?.Publish(new ErrorWarningEvent(ErrorSeverity.Error, "Native engine",
                    "stopped unexpectedly -- gave up auto-restarting after repeated crashes. Check Options > Decode Engine and try again."));
                return;
            }

            ShowMessage($"Native engine host stopped unexpectedly -- restarting ({attemptNumber}/{MaxNativeEngineAutoRestartsPerWindow})...", true);
            var restartTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            restartTimer.Tick += (s, e) =>
            {
                restartTimer.Stop();
                restartTimer.Dispose();
                if (nativeEngineClient == exitedClient) ApplyEngineMode();
            };
            restartTimer.Start();
        }

        // Called (via BeginInvoke, already marshalled to the UI thread) whenever
        // DxSpotWatcher's watch list or any watched call's last-seen data changes. One row per
        // watched call, alphabetical (stable order -- see DxSpotWatcher.Snapshot). Quiet update:
        // no sound, no forced screen-reader announcement, same change-detection + identity-based
        // selection-preservation shape as every other Render* method here.
        // Public wrapper so OptionsDlg can force a re-render after changing the
        // sort order (Spot Watch tab) -- RenderSpotWatchList itself stays private,
        // matching every other Render* method here.
        public void RefreshSpotWatchDisplay() => RenderSpotWatchList();

        private void RenderSpotWatchList()
        {
            var snapshot = dxSpotWatcher.Snapshot();

            // Snapshot() itself is always alphabetical (a stable base order); apply
            // the user's chosen display sort here, on top of it, so ties (e.g. two
            // calls both "Even") keep a predictable alphabetical secondary order --
            // OrderBy/OrderByDescending are stable sorts.
            IEnumerable<KeyValuePair<string, SpotInfo>> ordered = snapshot;
            switch ((spotWatchSortKey ?? "callsign").ToLowerInvariant())
            {
                case "evenodd":
                    ordered = snapshot.OrderBy(kv => kv.Value == null
                        ? 2 : (DxSpotWatcher.IsEvenPeriod(kv.Value.UtcTime, kv.Value.Mode) ? 0 : 1));
                    break;
                case "snr":
                    ordered = snapshot.OrderByDescending(kv => kv.Value?.Snr ?? int.MinValue);
                    break;
            }

            var items = new List<string>(snapshot.Count);
            var keys = new List<string>(snapshot.Count);
            foreach (var kv in ordered)
            {
                keys.Add(kv.Key);
                items.Add(FormatSpotWatchRow(kv.Key, kv.Value));
            }

            bool changed = spotWatchListBox.Items.Count != items.Count;
            if (!changed)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if ((string)spotWatchListBox.Items[i] != items[i]) { changed = true; break; }
                }
            }
            if (!changed) return;

            bool focused = spotWatchListBox.Focused;
            int prevIdx = focused ? spotWatchListBox.SelectedIndex : -1;
            int newIdx = FindPreservedSelectionIndex(_spotWatchKeys, prevIdx, keys);
            _spotWatchKeys = keys;

            spotWatchListBox.BeginUpdate();
            try
            {
                spotWatchListBox.Items.Clear();
                spotWatchListBox.Items.AddRange(items.ToArray());
            }
            finally { spotWatchListBox.EndUpdate(); }
            if (focused && newIdx >= 0 && spotWatchListBox.Items.Count > 0)
                spotWatchListBox.SelectedIndex = newIdx;
        }

        private string FormatSpotWatchRow(string call, SpotInfo spot)
        {
            if (spot == null) return $"{call} -- not yet spotted";

            string fallback = $"{call} -- last spotted {FormatSpotAge(spot.UtcTime)}, {spot.Band} {spot.Mode}, by {spot.SpotterCall}" +
                (string.IsNullOrEmpty(spot.SpotterGrid) ? "" : $" ({spot.SpotterGrid})");

            string country = "";
            if (wsjtxClient?.lookupManager != null && wsjtxClient.lookupManager.Enabled)
            {
                var rec = wsjtxClient.lookupManager.Build(call);
                // QRZ contributes Country as "United States"; Club Log (the fallback when
                // QRZ has no cached data for this call) contributes its own raw entity name,
                // "United States of America", plus a Dxcc entity number QRZ never sets --
                // check both, or a Club Log-sourced record silently fails this test and skips
                // the state substitution below entirely.
                bool isUsa = string.Equals(rec.Country, "United States", StringComparison.OrdinalIgnoreCase)
                             || rec.Dxcc == 291;
                if (isUsa && showUsStateCheckBox.Checked)
                {
                    // Same QRZ-first, grid.dat-fallback priority used everywhere else --
                    // show the actual state instead of just "United States".
                    string gridState = string.IsNullOrEmpty(spot.SenderGrid) ? null : WsjtxClient.GridToUsState(spot.SenderGrid);
                    string state = WsjtxClient.ResolveUsState(rec.State, gridState);
                    country = state != null ? $", {state}" : ", United States";
                }
                else if (!string.IsNullOrEmpty(rec.Country))
                {
                    country = $", {rec.Country}";
                }
            }

            // Spotter's country/state -- sourced entirely offline. Country comes from
            // PSKReporter's own DXCC entity number in the payload (free, authoritative,
            // no lookup needed). State-if-USA tries the FCC ULS database (only if the
            // user has downloaded it) then falls back to the spotter's own grid square;
            // QRZ is deliberately never queried here, since spotters are an unbounded,
            // uncontrolled set of stations worldwide (unlike the small curated watch
            // list the "country" field above resolves).
            string spotterCountry = "";
            if (spot.SpotterDxccEntity.HasValue && wsjtxClient?.lookupManager?.ClubLog != null)
            {
                var entity = wsjtxClient.lookupManager.ClubLog.AllEntities
                    .FirstOrDefault(e => e.Adif == spot.SpotterDxccEntity.Value);
                if (entity != null && !string.IsNullOrEmpty(entity.Name))
                {
                    const int UsaAdif = 291;
                    if (entity.Adif == UsaAdif && showUsStateCheckBox.Checked)
                    {
                        string fccState = wsjtxClient.lookupManager.FccUls.IsEnabled
                            ? wsjtxClient.lookupManager.FccUls.Lookup(spot.SpotterCall)
                            : null;
                        string gridState = string.IsNullOrEmpty(spot.SpotterGrid) ? null : WsjtxClient.GridToUsState(spot.SpotterGrid);
                        string state = !string.IsNullOrEmpty(fccState) ? fccState : gridState;
                        spotterCountry = state != null ? $", {state}" : $", {entity.Name}";
                    }
                    else
                    {
                        spotterCountry = $", {entity.Name}";
                    }
                }
            }

            string frequency = "";
            if (spot.Frequency.HasValue)
            {
                double kHz = spot.Frequency.Value / 1000.0;
                frequency = $", {kHz.ToString("0.0")} kHz";
            }

            var fieldMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "callsign",       call },
                { "age",            $", last spotted {FormatSpotAge(spot.UtcTime)}" },
                { "band",           string.IsNullOrEmpty(spot.Band) ? "" : $", {spot.Band}" },
                { "frequency",      frequency },
                { "mode",           string.IsNullOrEmpty(spot.Mode) ? "" : $", {spot.Mode}" },
                { "evenOdd",        string.IsNullOrEmpty(spot.Mode) ? "" : $", {(DxSpotWatcher.IsEvenPeriod(spot.UtcTime, spot.Mode) ? "Even" : "Odd")}" },
                { "snr",            spot.Snr.HasValue ? $", {spot.Snr.Value.ToString("+#;-#;0")}dB" : "" },
                { "senderGrid",     string.IsNullOrEmpty(spot.SenderGrid) ? "" : $", grid {spot.SenderGrid}" },
                { "country",        country },
                { "spottercall",    string.IsNullOrEmpty(spot.SpotterCall) ? "" : $", by {spot.SpotterCall}" },
                { "spottercountry", spotterCountry },
                { "spottergrid",    string.IsNullOrEmpty(spot.SpotterGrid) ? "" : $" ({spot.SpotterGrid})" },
            };

            return RowFormatter.BuildOrderedRow(fieldMap, spotWatchRowOrderFields, fallback);
        }

        private static string FormatSpotAge(DateTime utcTime)
        {
            var age = DateTime.UtcNow - utcTime;
            if (age.TotalSeconds < 90) return "just now";
            if (age.TotalMinutes < 90) return $"{(int)age.TotalMinutes} min ago";
            if (age.TotalHours < 36) return $"{(int)age.TotalHours} hr ago";
            return $"{(int)age.TotalDays} days ago";
        }

        private void IncludeHelpLabel_Click(object sender, EventArgs e)
        {
            ShowHelp($"The 'Reply to new calls' section allows you to choose which messages from new callers you want to add to the 'Stations calling' list." +
                $"{nl}{nl}- Select 'CQ' if you want to reply only to CQ messages." +
                $"{nl}- Select 'CQ/grid' if you want to reply only to messages with grid information, allowing you to prioritize calls based on distance or azimuth." +
                $"{nl}- Select 'any' to reply to any message." +
                $"{nl}{nl}Note: The selections here don't affect replies to 'new countries' or 'new countries on band', which are enabled when 'Reply to new DXCC' is selected.");
        }

        private void IgnoreNonDxHelpLabel_Click(object sender, EventArgs e)
        {
            ShowHelp($"When calling 'CQ DX', select 'Ignore non-DX reply' to disable replying to calls to {MyCall()} from continents other than your continent." +
                $"{nl}{nl}This also disables replies to calls not directed to {MyCall()}.");
        }

        private void UseDirectedHelpLabel_Click(object sender, EventArgs e)
        {
            ShowHelp($"To send directed CQs:{nl}" +
                $"- Enter the code(s) for the directed CQs you want to transmit (2 to 4 letters each), separated by spaces." +
                $"{nl}- Don't enter 'DX' here." +
                $"{nl}{nl}The directed CQs will be used in random order." +
                $"{nl}{nl}Example: EU SA OC");
        }

        private void AlertDirectedHelpLabel_Click(object sender, EventArgs e)
        {
            string continent = wsjtxClient.myContinent == null ? "" : $" '{wsjtxClient.myContinent}'";
            ShowHelp($"Enter targets such as POTA SOTA DX." +
                $"{nl}{nl}Matching calls such as CQ POTA are added to the waiting list as Directed CQ calls." +
                $"{nl}{nl}If you enter 'DX', there will be no reply if the caller is on your continent." +
                $"{nl}{nl}There is no need to enter 'DX' or your continent{continent} if you have selected 'DX' and 'CQ/73' at 'Reply to new calls'." +
                $"{nl}{nl}(Note: 'CQ POTA' is an exception to the 'already worked' rule, these calls will allow a reply if you haven't already logged that call in the current mode/band in the current day).");
        }

        private void LogEarlyHelpLabel_Click(object sender, EventArgs e)
        {
            ShowHelp($"To maximize the chance of completed QSOs, consider 'early logging':" +
                $"{nl}{nl}" +
                $"The defining requirement for any QSO is the exchange of call signs and signal reports." +
                $"{nl}Once either party sends an 'RRR' message (and reports have been exchanged), those requirements have been met... a '73' is not necessary for logging the QSO." +
                $"{nl}{nl}Note that the QSO will continue after early logging, completing when 'RR73' or '73' is sent, or '73' is received." +
                $"{nl}{nl}New countries are an exception to early logging. In this case, logging is only after confirmation with a '73' or 'RR73'.");
        }

        private void verLabel2_Click(object sender, EventArgs e)
        {
            string ver = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? string.Empty;
            string command = "https://blindsea.com/jimmy?v=" + Uri.EscapeDataString(ver);
            System.Diagnostics.Process.Start(command);
        }

        private void ExcludeHelpLabel_Click(object sender, EventArgs e)
        {
            if (!formLoaded) return;

            wsjtxClient.UpdateMaxAutoGenEnqueue();
            string continent = wsjtxClient.myContinent == null ? "" : $" '{wsjtxClient.myContinent}'";
            string onBand = $"{bandComboBox.Items[1]}";
            ShowHelp($"{friendlyName} will add up to {wsjtxClient.maxAutoGenEnqueue} calls to the 'Stations calling' list that meet these conditions:" +
                $"{nl}{nl}- The call has not been worked before 'for 1 band' or '{onBand}'." +
                $"{nl}- The call is 'DX' or originated in your continent{continent}." +
                $"{nl}- The received message can be" +
                $"{nl}     * CQ, 73 or RR73 (the best time to reply), or" +
                $"{nl}     * grid information (for distance calculation), or" +
                $"{nl}     * any type (for maximum number of replies)." +
                $"{nl}- The caller is on your Rx time slot (if in 'Call CQ' mode)." +
                $"{nl}- The caller hasn't been replied to more than {wsjtxClient.maxPrevTo} times during this mode / band session." +
                $"{nl}{nl}If you select 'DX', {friendlyName} will reply to calls from continents other than yours." +
                $"{nl}{nl}For example, this is useful in case you've already worked all states/entities on your continent, and only want to reply to calls you haven't worked yet from other continents." +
                $"{nl}{nl}- If you select your continent{continent}, {friendlyName} will reply only to those calls." +
                $"{nl}{nl}For example, this is useful in case you're running QRP, and expect you can't be heard on other continents, and only want to reply to calls from your continent." +
                $"{nl}{nl}Select 'for 1 band' if you want to reply to calls you haven't worked before, but only need new calls on one band. Select '{onBand}' to also reply to calls that you haven't worked before on the current band." +
                $"{nl}{nl}Note: If you have entered 'directed CQs' to reply to, those CQs will be replied to regardless of the 'DX',{continent}, 'from messages', or new 'for 1 band' or '{onBand}' settings here.");
        }

        private void modeHelpLabel_Click(object sender, EventArgs e)
        {
            if (formLoaded && wsjtxClient.ConnectedToWsjtx()) wsjtxClient.HaltTuning();
            ShowHelp(BuildHelpText());
        }

        private string BuildHelpText()
        {
            string K(HotkeyAction a) => HotkeyConfig.FormatKeysForHelp(hotkeyConfig[a]);
            string ver = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? string.Empty;

            return
                $"{friendlyName} {ver}" +
                $"{nl}{nl}{friendlyName} processes 'QSO's by selecting one of two modes:" +
                $"{nl}'Call CQ' mode, and 'Listen for calls' mode." +
                $"{nl}Stations you haven't worked yet are added to the 'Stations calling' list." +
                $"{nl}Stations calling you directly have priority on this list, and are moved to the top." +
                $"{nl}{nl}You can leave this window open, for reference, as you run {friendlyName}." +

                $"{nl}{nl}Command keys:" +
                $"{nl}{K(HotkeyAction.RowOrder)}: Open stations available row order editor." +
                $"{nl}{K(HotkeyAction.Options)}: Review or set options for processing 'QSO's." +
                $"{nl}{K(HotkeyAction.CallCqMode)}: Start selected CQ mode (CQ only / CQ DX only / CQ and CQ DX). Does nothing in Listen mode." +
                $"{nl}{K(HotkeyAction.CallCqOptions)}: Open Call CQ options (choose CQ only / CQ DX only / CQ and CQ DX, directed CQ, etc.)." +
                $"{nl}{K(HotkeyAction.ListenMode)}: Select 'Listen for calls' mode." +
                $"{nl}{K(HotkeyAction.EnableTx)}: Enable transmit, or re-enable timed out 'QSO'." +
                $"{nl}{K(HotkeyAction.HaltTx)}: Halt transmit immediately." +
                $"{nl}{K(HotkeyAction.NextCall)}: Skip to the next available station, very useful!" +
                $"{nl}{K(HotkeyAction.ManualCall)}: Enter a callsign manually to call." +

                $"{nl}{K(HotkeyAction.AnalyzeSlot)}: Analyze transmit slot (find quietest audio frequency for CQ; requires 'Use best Tx frequency' enabled)." +
                $"{nl}{K(HotkeyAction.LookupStation)}: Look up selected station (shows callsign, country, state, LoTW status, and more)." +
                $"{nl}{K(HotkeyAction.OpenLogbook)}: Open the Ham Radio Center logbook." +
                $"{nl}{K(HotkeyAction.AddManualQso)}: Add a manually-logged QSO (e.g. worked on another mode or rig)." +
                $"{nl}{K(HotkeyAction.OpenOtaSpots)}: Open POTA / SOTA spots, DX spots, band conditions, and space weather." +

                $"{nl}{nl}Radio configuration keys:" +
                $"{nl}{K(HotkeyAction.TuneMode)}: Toggle Tune mode, to determine correct audio output level to radio ({K(HotkeyAction.AudioUp)} and {K(HotkeyAction.AudioDown)} keys to adjust, {K(HotkeyAction.Prompts)} for fast or complete updates)." +
                $"{nl}{K(HotkeyAction.AudioUp)} key: Increase audio output level to radio (during tune or transmit)." +
                $"{nl}{K(HotkeyAction.AudioDown)} key: Decrease audio output level to radio (during tune or transmit)." +
                $"{nl}{K(HotkeyAction.PowerSwr)}: Quick check of output power and SWR (during transmit) or audio input (during receive)." +
                $"{nl}{K(HotkeyAction.BandUp)}: Select next higher band." +
                $"{nl}{K(HotkeyAction.BandDown)}: Select next lower band." +

                $"{nl}{nl}Optional command keys:" +
                $"{nl}{K(HotkeyAction.DeleteAllCalls)}: Delete all 'Stations calling'." +
                $"{nl}Delete key: Delete selected call in 'Stations calling'." +
                $"{nl}{K(HotkeyAction.TxPeriod)}: Toggle transmit period." +
                $"{nl}{K(HotkeyAction.HoldTimeout)}: Toggle extended timeout." +
                $"{nl}{K(HotkeyAction.UploadLotw)}: Upload to Logbook of the World." +
                $"{nl}{K(HotkeyAction.ToggleMode)}: Select operating mode (FT8 or FT4)." +
                $"{nl}{K(HotkeyAction.Prompts)}: Toggle command prompts in {friendlyName} status." +
                $"{nl}Escape key: Halt transmit, cancel current 'QSO', switch to Listen mode." +
                $"{nl}{K(HotkeyAction.UpdateCheck)}: Check for update to {friendlyName}." +
                $"{nl}{K(HotkeyAction.PSKReporter)}: Toggle sending spots to PSKReporter (leave 'Enabled' to help other hams)" +
                $"{nl}{K(HotkeyAction.SortOrder)}: Open stations available sort order editor." +
                $"{nl}{K(HotkeyAction.ResetWindowSize)}: Reset window size and position to default." +
                $"{nl}{K(HotkeyAction.Help)}: Read the list of shortcut keys." +

                $"{nl}{nl}Main navigation keys:" +
                $"{nl}{K(HotkeyAction.NavStatus)}: Read QSO and radio status (Note that {K(HotkeyAction.NavStatus)} is the 'home' location!)." +
                $"{nl}{K(HotkeyAction.NavCallList)}: Read and select from 'Stations calling' list." +

                $"{nl}{nl}Optional navigation keys:" +
                $"{nl}{K(HotkeyAction.NavLoggedList)}: Read 'Auto-logged calls' list." +
                $"{nl}{K(HotkeyAction.NavLoggedCount)}: Read total number of 'Auto-logged calls'." +
                $"{nl}{K(HotkeyAction.NavPendingCount)}: Read number of pending 'Stations calling'." +
                $"{nl}Ctrl, Y: Play the 'New call', 'Call directed to {SpacifyMyCall()}', and 'Logged' alert sounds.";
        }

        public void cqModeButton_Click(object sender, EventArgs e)
        {
            if (!formLoaded) return;

            wsjtxClient.TxModeChanged(WsjtxClient.TxModes.CALL_CQ);
            optionsDlg?.UpdateView();
        }

        public void listenModeButton_Click(object sender, EventArgs e)
        {
            if (!formLoaded) return;

            wsjtxClient.TxModeChanged(WsjtxClient.TxModes.LISTEN);
            optionsDlg?.UpdateView();
        }

        // Sets the 4-radio from the current txMode (called when mode flips between LISTEN and CALL_CQ)
        public void SyncCqIntentFromMode()
        {
            if (wsjtxClient == null) return;
            if (wsjtxClient.txMode == WsjtxClient.TxModes.LISTEN)
                cqIntentListenButton.Checked = true;
            else
                SyncCqSubtypeRadio();
        }

        // Sets the CQ subtype radio (CQ only / CQ DX only / CQ and CQ DX) from the checkboxes
        private void SyncCqSubtypeRadio()
        {
            bool callCq = callNonDirCqCheckBox.Checked;
            bool callCqDx = callCqDxCheckBox.Checked;
            if (callCq && callCqDx)
                cqIntentCqAndDxButton.Checked = true;
            else if (!callCq && callCqDx)
                cqIntentCqDxOnlyButton.Checked = true;
            else
                cqIntentCqOnlyButton.Checked = true;
        }

        // Called when CQ checkboxes change: only updates CQ subtype radio if a CQ intent is already selected
        public void SyncCqIntentFromCheckboxes()
        {
            if (_suppressIntentSync) return;
            if (!cqIntentListenButton.Checked)
                SyncCqSubtypeRadio();
        }

        // Click handlers for the 4 operating-mode intent radio buttons

        private void cqIntentListenButton_Click(object sender, EventArgs e)
        {
            if (!formLoaded) return;
            listenModeButton_Click(null, null);
        }

        private void cqIntentCqOnlyButton_Click(object sender, EventArgs e)
        {
            if (!formLoaded) return;
            _suppressIntentSync = true;
            callNonDirCqCheckBox.Checked = true;
            callCqDxCheckBox.Checked = false;
            _suppressIntentSync = false;
            optionsDlg?.UpdateView();
        }

        private void cqIntentCqDxOnlyButton_Click(object sender, EventArgs e)
        {
            if (!formLoaded) return;
            _suppressIntentSync = true;
            callCqDxCheckBox.Checked = true;
            callNonDirCqCheckBox.Checked = false;
            _suppressIntentSync = false;
            optionsDlg?.UpdateView();
        }

        private void cqIntentCqAndDxButton_Click(object sender, EventArgs e)
        {
            if (!formLoaded) return;
            _suppressIntentSync = true;
            callNonDirCqCheckBox.Checked = true;
            callCqDxCheckBox.Checked = true;
            _suppressIntentSync = false;
            optionsDlg?.UpdateView();
        }

        private void freqCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (!formLoaded) return;

            wsjtxClient.WsjtxSettingChanged();
            wsjtxClient.AutoFreqChanged(freqCheckBox.Checked, false);
            optionsDlg?.UpdateView();
        }

        private void LimitTxHelpLabel_Click(object sender, EventArgs e)
        {
            if (!formLoaded) return;

            string adv = wsjtxClient != null ? $"{nl}{nl}If 'Optimize throughput' is selected, the maximum number of replies and CQs for the current call is automatically adjusted lower than the specified limit (if possible), to help process the call queue faster." +
                $"{nl}{nl}If 'Hold' is selected, the 'Repeated Tx' limit is ignored, and replies to the current call sign are transmitted a maximum of {wsjtxClient.holdMaxTxRepeat} times." : "";
            ShowHelp($"This will limit the number of times the same message is transmitted." +
                $"{nl}{nl}For example, it will limit the number of repeated transmitted replies or CQs for the current call. If there is no response to your reply messages when the limit is reached, the next call in the queue is processed (or if the call queue is empty, CQing (or listening) will resume)." +
                $"{nl}{nl}As the repeat limit is reduced, the number of times a call can be automatically re-added to the call queue is increased, to compensate.{adv}");
        }

        private void optimizeCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (formLoaded) wsjtxClient.TxRepeatChanged();
        }

        private void holdCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (!formLoaded) return;

            wsjtxClient.HoldCheckBoxChanged();
        }

        private void directedTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.KeyChar = char.ToUpper(e.KeyChar);
            char c = e.KeyChar;
            if (c == (char)Keys.Back || c == ' ' || (c >= 'A' && c <= 'Z')) return;
            e.Handled = true;
        }

        private void alertTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.KeyChar = char.ToUpper(e.KeyChar);
            char c = e.KeyChar;
            if (c == (char)Keys.Back || c == ' ' || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return;
            e.Handled = true;
        }

        private void ReplyRR73HelpLabel_Click(object sender, EventArgs e)
        {
            ShowHelp($"Select 'Reply to RR73 msg' if you want to reply '73' to an RR73 message received at the end of a QSO." +
                $"{nl}{nl}'RR73' means:" +
                $"{nl}- 'Signal report received', and" +
                $"{nl}- 'Best regards', and" +
                $"{nl}- 'I'm confident you will see this', so" +
                $"{nl}- 'No further reply requested'." +
                $"{nl}{nl}You can safely skip replying to 'RR73' to speed up the QSO cycle, if conditions allow." +
                $"{nl}{nl}Exceptions:" +
                $"{nl}- If from a new country, RR73 is always replied to with a '73'." +
                $"{nl}- If a Fox/Hound-style (multi-stream) 'RR73' message, no '73' is expected by the caller, so it's not sent.");
        }

        private void PeriodHelpLabel_Click(object sender, EventArgs e)
        {
            ShowHelp($"'Tx period' allows you to select which period you want {friendlyName} to use for transmit when in 'Listen for calls' mode." +
                $"{nl}{nl}If you are using multiple transmitters at your station, you may want for all of them to use the same Tx period, to avoid interference." +
                $"{nl}{nl}Otherwise, the normal selection is 'any'.");
        }

        private void AutoFreqHelpLabel_Click(object sender, EventArgs e)
        {
            ShowHelp($"The Tx audio frequency is automatically set to an unused part of the audio spectrum." +
                $"{nl}{nl}After a period of no replies being received, transmitting is temporarily suspended for one Tx cycle, the received audio is re-sampled, and the best Tx frequency is re-calculated.");
        }

        private void blockHelpLabel_Click(object sender, EventArgs e)
        {
            ShowHelp($"To block replies to a specific call sign:" +
                $"{nl}{nl}If the call sign is in the 'Stations calling' list:" +
                $"{nl}- Hold the 'Ctrl' key down and click on the call sign." +
                $"{nl}{nl}Otherwise," +
                $"{nl}- Enter the call sign in the 'Block any reply' box, with each call sign separated by a space." +
                $"{nl}{nl}Note: If you manually select a blocked call, it will be unblocked to allow replies.");
        }

        private void callAddedCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (formLoaded && callAddedCheckBox.Checked) wsjtxClient.Sounds.PlaySoundEvent(true, soundFile_CallAdded);
        }

        private void exceptTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.KeyChar = char.ToUpper(e.KeyChar);
            char c = e.KeyChar;
            if (c == (char)Keys.Back || c == ' ' || c == '/' || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return;
            e.Handled = true;
        }

        private void msgTextBox_MouseUp(object sender, MouseEventArgs e)
        {
            if (!formLoaded || Control.ModifierKeys != Keys.Control) return;

            if (e.Button == MouseButtons.Left)
            {
                //available for ctrl/left-click action
            }
            else
            {
                //available for ctrl/right-click action
            }
        }

        private void callCqDxCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            ignoreNonDxCheckBox.Enabled = callCqDxCheckBox.Checked;

            if (callDirCqCheckBox.Checked || callNonDirCqCheckBox.Checked || replyDirCqCheckBox.Checked || replyDxCheckBox.Checked || replyLocalCheckBox.Checked)
            {
                if (callCqDxCheckBox.Checked) ignoreNonDxCheckBox.Checked = false;
            }

            ValidateDirCqTextBox();
            if (!callCqDxCheckBox.Checked && !callDirCqCheckBox.Checked && !callNonDirCqCheckBox.Checked)
            {
                callNonDirCqCheckBox.Checked = true;
            }

            optionsDlg?.UpdateView();
            SyncCqIntentFromCheckboxes();

            if (formLoaded) wsjtxClient.WsjtxSettingChanged();
        }

        private void directedTextBox_Leave(object sender, EventArgs e)
        {
            if (directedTextBox.Text == separateBySpaces) return;

            ValidateDirCqTextBox();

            if (directedTextBox.Text == "")
            {
                callDirCqCheckBox.Checked = false;
                return;
            }
        }

        private void ignoreNonDxCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (ignoreNonDxCheckBox.Checked)
            {
                callDirCqCheckBox.Checked = false;
                callNonDirCqCheckBox.Checked = false;
                replyDirCqCheckBox.Checked = false;
                replyLocalCheckBox.Checked = false;
                replyDxCheckBox.Checked = false;
            }
        }

        private void callDirCqCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (!formLoaded) return;

            directedTextBox.Enabled = callDirCqCheckBox.Checked;
            if (callDirCqCheckBox.Checked && directedTextBox.Text == separateBySpaces)
            {
                ignoreDirectedChange = true;
                directedTextBox.Clear();
                directedTextBox.ForeColor = System.Drawing.Color.Black;
            }
            if (!callDirCqCheckBox.Checked && directedTextBox.Text == "") directedTextBox.Text = separateBySpaces;

            if (callDirCqCheckBox.Checked)
            {
                if (callCqDxCheckBox.Checked) ignoreNonDxCheckBox.Checked = false;
            }
            else
            {
                if (!callCqDxCheckBox.Checked)
                {
                    callNonDirCqCheckBox.Checked = true;
                }
            }
            wsjtxClient.WsjtxSettingChanged();              //resets CQ to not directed

            optionsDlg?.UpdateView();
        }

        private void callNonDirCqCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (callNonDirCqCheckBox.Checked)
            {
                if (callCqDxCheckBox.Checked) ignoreNonDxCheckBox.Checked = false;
            }
            else
            {
                ValidateDirCqTextBox();
                if (!callCqDxCheckBox.Checked && !callDirCqCheckBox.Checked)
                {
                    callNonDirCqCheckBox.Checked = true;
                }
            }
            if (formLoaded) wsjtxClient.WsjtxSettingChanged();              //resets CQ to non-directed

            optionsDlg?.UpdateView();
            SyncCqIntentFromCheckboxes();
        }

        private void alertTextBox_Leave(object sender, EventArgs e)
        {
            ValidateAlertTextBox();
        }

        private void ValidateAlertTextBox()
        {
            if (alertTextBox.Text == separateBySpaces) return;

            var dirArray = alertTextBox.Text.Trim().ToUpper().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string corrText = "";
            string delim = "";
            foreach (string dir in dirArray)
            {
                if (dir.Length >= 2 && dir.Length <= 4 && (!Regex.IsMatch(dir, alphaOnly) || !Regex.IsMatch(dir, numericOnly))) corrText = corrText + delim + dir;
                delim = " ";
            }
            alertTextBox.Text = corrText;
        }

        private void ValidateDirCqTextBox()
        {
            if (directedTextBox.Text == separateBySpaces) return;

            string text = directedTextBox.Text.Replace("*", "");        //obsoleted
            var dirArray = text.Trim().ToUpper().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string corrText = "";
            string delim = "";
            foreach (string dir in dirArray)
            {
                if (dir.Length >= 2 && dir.Length <= 4)
                {
                    corrText = corrText + delim + dir;
                    delim = " ";
                }
            }
            directedTextBox.Text = corrText;

            if (corrText == "") callDirCqCheckBox.Checked = false;
        }

        private void useRR73CheckBox_Click(object sender, EventArgs e)
        {
            if (!formLoaded) return;

            wsjtxClient.useRR73 = useRR73CheckBox.Checked;
        }

        private void replyLocalCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (replyLocalCheckBox.Checked) ignoreNonDxCheckBox.Checked = false;
            UpdateCqNewOnBand();
            CheckManualSelection();
            optionsDlg?.UpdateView();
        }

        private void CheckManualSelection()
        {
            if (formLoaded && listenModeButton.Checked && !replyDxCheckBox.Checked && !replyLocalCheckBox.Checked && !replyDirCqCheckBox.Checked)
            {
                ShowMsg($"Select calls manually (alt/dbl-click)", true);
            }
        }

        private void replyDxCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (replyDxCheckBox.Checked) ignoreNonDxCheckBox.Checked = false;
            UpdateCqNewOnBand();
            CheckManualSelection();
            optionsDlg?.UpdateView();
        }

        private void Controller_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.Y)
            {
                if (wsjtxClient.ConnectedToWsjtx()) wsjtxClient.HaltTuning();
                DemoSounds();
            }

            if (e.Control && e.Shift && e.KeyCode == Keys.O)
            {
                optionsButton_Click(null, null);
            }

            if (e.Control && e.Shift && e.KeyCode == Keys.D)
            {
                verLabel_DoubleClick(null, null);
            }

            if (e.KeyData == hotkeyConfig[HotkeyAction.NavStatus])
            {
                if (!statusText.Focused)
                {
                    statusText.Focus();
                }
                // Force NVDA/JAWS to (re-)announce the status text on demand (see RenderStatus).
                BeginInvoke((Action)(() => SendKeys.Send("{UP}")));
            }

            //past this point all keys cause tuning to halt

            if (e.KeyData == hotkeyConfig[HotkeyAction.NavLoggedList])
            {
                if (formLoaded && wsjtxClient.ConnectedToWsjtx()) wsjtxClient.HaltTuning();
                logListBox.Focus();
            }

            if (e.KeyData == hotkeyConfig[HotkeyAction.NavLoggedCount])
            {
                // Live-testing finding, 2026-08-21: this used to call loggedLabel.Focus() --
                // Label controls aren't focusable by default (no TabStop), so Focus() silently
                // failed and keyboard focus fell through to whatever the next real Tab stop
                // happened to be ("the buttons"), and only ever worked on the very first press
                // before focus had already drifted away from wherever the operator actually was.
                // A count is a quick, one-shot fact the operator wants read out without being
                // relocated -- unlike NavLoggedList (a real list worth navigating INTO), there's
                // nothing here to interact with afterward. Announces via UI Automation's
                // Notification event (RaiseAccessibleAlert, same mechanism the off-focus
                // important-alert feature uses) instead of moving focus at all. loggedLabel.Text
                // already holds the live "Auto-logged: N" header RenderLoggedList last wrote.
                if (formLoaded && wsjtxClient.ConnectedToWsjtx()) wsjtxClient.HaltTuning();
                RaiseAccessibleAlert(loggedLabel.Text);
            }

            if (e.KeyData == hotkeyConfig[HotkeyAction.NavCallList])
            {
                // Live-testing finding, 2026-08-21: callListBox is the BEGINNER/simple-layout
                // list only -- Advanced Call Layout replaces it with the TX1/TX2/Raw Decodes
                // lists (their own NavAdvTx1/NavAdvTx2/NavAdvRaw hotkeys). callListBox.Visible
                // alone isn't a reliable "beginner mode" gate on its own: it can still be true
                // even with Advanced Call Layout checked, if the operator hasn't individually
                // enabled any of the TX1/TX2/Raw sub-displays -- this hotkey must not fire in
                // that case either, since Advanced Call Layout being on means this window is
                // conceptually not the operator's current one to navigate to.
                if (!advancedCallLayout && callListBox.Visible)
                {
                    if (formLoaded && wsjtxClient.ConnectedToWsjtx()) wsjtxClient.HaltTuning();
                    callListBox.Focus();
                }
            }

            if (e.KeyData == hotkeyConfig[HotkeyAction.NavPendingCount])
            {
                // Live-testing finding, 2026-08-21: same fix as NavLoggedCount just above (see
                // its own comment) -- replyListLabel.Focus() silently failed (Labels aren't
                // focusable by default) and only ever appeared to work once, before focus had
                // already drifted. Announces the live "Stations calling: N" header text without
                // relocating focus.
                if (formLoaded && wsjtxClient.ConnectedToWsjtx()) wsjtxClient.HaltTuning();
                RaiseAccessibleAlert(replyListLabel.Text);
            }

            if (hotkeyConfig[HotkeyAction.NavAdvTx1] != Keys.None && e.KeyData == hotkeyConfig[HotkeyAction.NavAdvTx1])
            {
                if (advTx1ListBox.Visible)
                {
                    if (formLoaded && wsjtxClient.ConnectedToWsjtx()) wsjtxClient.HaltTuning();
                    advTx1ListBox.Focus();
                }
            }

            if (hotkeyConfig[HotkeyAction.NavAdvTx2] != Keys.None && e.KeyData == hotkeyConfig[HotkeyAction.NavAdvTx2])
            {
                if (advTx2ListBox.Visible)
                {
                    if (formLoaded && wsjtxClient.ConnectedToWsjtx()) wsjtxClient.HaltTuning();
                    advTx2ListBox.Focus();
                }
            }

            if (hotkeyConfig[HotkeyAction.NavAdvRaw] != Keys.None && e.KeyData == hotkeyConfig[HotkeyAction.NavAdvRaw])
            {
                if (advRawListBox.Visible)
                {
                    if (formLoaded && wsjtxClient.ConnectedToWsjtx()) wsjtxClient.HaltTuning();
                    advRawListBox.Focus();
                    if (advRawListBox.Items.Count > 0 && advRawListBox.SelectionMode != SelectionMode.None && advRawListBox.SelectedIndex < 0)
                        advRawListBox.SelectedIndex = 0;
                }
            }

            if (hotkeyConfig[HotkeyAction.NavSpotWatch] != Keys.None && e.KeyData == hotkeyConfig[HotkeyAction.NavSpotWatch])
            {
                if (spotWatchListBox.Visible)
                {
                    if (formLoaded && wsjtxClient.ConnectedToWsjtx()) wsjtxClient.HaltTuning();
                    spotWatchListBox.Focus();
                    if (spotWatchListBox.Items.Count > 0 && spotWatchListBox.SelectionMode != SelectionMode.None && spotWatchListBox.SelectedIndex < 0)
                        spotWatchListBox.SelectedIndex = 0;
                }
            }

            if (!formLoaded) return;

            if (e.KeyCode == Keys.Escape)               //halt Tx, return to Listen mode
            {
                var focused = this.ActiveControl;
                if (wsjtxClient.ConnectedToWsjtx())
                {
                    wsjtxClient.RequeueAbortedCall();   // must precede CancelQso (needs callInProg/replyDecode)
                    wsjtxClient.CancelQso();
                    wsjtxClient.HaltAndDisableTx();     // unconditional: works in both CQ and Listen mode
                    wsjtxClient.ResetTxToCq();
                    listenModeButton_Click(null, null);
                    ShowMsg("Tx halted", true);
                }
                BeginInvoke((Action)(() =>
                    BeginInvoke((Action)(() => RestoreFocus(focused)))
                ));
            }
        }

        private void RestoreFocus(Control c)
        {
            if (c != null && !c.IsDisposed && c.IsHandleCreated && c.Visible && c.Enabled && c.CanFocus)
                c.Focus();
        }

        // Parses a comma-separated row-order INI value: ASCII comma, ignoring invalid
        // tokens (not in allowedFields) and duplicates after the first occurrence.
        // Returns null (not an empty list) if nothing valid was found, so callers can
        // tell "not set" from "set to nothing" and fall back to the field's own
        // compiled-in default instead of an empty row.
        public static List<string> ParseRowOrder(string orderStr, IEnumerable<string> allowedFields)
        {
            if (string.IsNullOrWhiteSpace(orderStr)) return null;

            var allowed = new HashSet<string>(allowedFields, StringComparer.OrdinalIgnoreCase);
            var parsed = new List<string>();
            foreach (var tok in orderStr.Split(new char[] { (char)44 }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = tok.Trim();
                if (f.Length == 0) continue;
                if (!allowed.Contains(f)) continue;
                if (parsed.Exists(s => string.Equals(s, f, StringComparison.OrdinalIgnoreCase))) continue;
                parsed.Add(f);
            }
            return parsed.Count > 0 ? parsed : null;
        }

        private void OpenRowDisplayOrderEditor()
        {
            if (iniFile == null || wsjtxClient == null) return;

            var currentCallWaitingOrder = wsjtxClient.callWaitingRowOrderFields;
            var currentRawDecodeOrder = wsjtxClient.rawDecodeRowOrderFields;
            var currentSpotWatchOrder = spotWatchRowOrderFields;
            using (var dlg = new RowDisplayOrderDlg(currentCallWaitingOrder, currentRawDecodeOrder, currentSpotWatchOrder, wsjtxClient.debug))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                iniFile.Write("callWaitingRowOrder", string.Join(",", dlg.SelectedCallWaitingFields));
                wsjtxClient.callWaitingRowOrderFields = new List<string>(dlg.SelectedCallWaitingFields);

                iniFile.Write("rawDecodeRowOrder", string.Join(",", dlg.SelectedRawDecodeFields));
                wsjtxClient.rawDecodeRowOrderFields = new List<string>(dlg.SelectedRawDecodeFields);

                iniFile.Write("spotWatchRowOrder", string.Join(",", dlg.SelectedSpotWatchFields));
                spotWatchRowOrderFields = new List<string>(dlg.SelectedSpotWatchFields);

                wsjtxClient.RefreshCallWaitingRows();
                wsjtxClient.RefreshAdvancedLists();
                RenderSpotWatchList();
            }
        }

        public void ShowHelp(string s)
        {
            helpTimer.Tag = s;
            helpTimer.Start();
        }

        private void helpTimer_Tick(object sender, EventArgs e)
        {
            helpTimer.Stop();
            _helpReturnFocus = this.ActiveControl;
            if (helpDlg != null) helpDlg.Close();
            helpDlg = new HelpDlg(this, $"{wsjtxClient.pgmName}{helpSuffix}", (string)helpTimer.Tag);
            // No Owner -- see the matching comment on _logbookWindow's Show() call.
            helpDlg.Show();
            helpDlg.Activate();
        }

        private void cqModeButton_CheckedChanged(object sender, EventArgs e)
        {
            SyncCqIntentFromMode();
        }

        private void listenModeButton_CheckedChanged(object sender, EventArgs e)
        {
            SyncCqIntentFromMode();
        }

        private void UpdateCqNewOnBand()
        {
            anyMsgRadioButton.Enabled = cqGridRadioButton.Enabled = cqOnlyRadioButton.Enabled = bandComboBox.Enabled = replyDxCheckBox.Checked || replyLocalCheckBox.Checked;
        }

        private void OpenSortOrderEditor()
        {
            if (iniFile == null || wsjtxClient == null) return;

            using (var dlg = new RankOrderDlg(
                wsjtxClient.Ranker.rankOrderList,
                wsjtxClient.Ranker.rankBeamMethod,
                wsjtxClient.Ranker.callingEnabled))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                wsjtxClient.ApplySortOrder(dlg.SelectedOrder, dlg.SelectedBeam);
                wsjtxClient.ApplyCategoryWeights(dlg.SelectedCategoryWeights);
                wsjtxClient.ApplyCallingPriorities(dlg.SelectedCallingPriorities);
                // Found live, 2026-08-10: ApplySortOrder/ApplyCategoryWeights/ApplyCallingPriorities
                // only update the ranking SETTINGS -- none of them re-rank the calls already sitting
                // in the queue, so a new primary sort criterion (e.g. "strongest signal") never
                // visibly reordered anything until enough new decodes trickled in on their own to
                // re-sort it incidentally. SortCallsPublic() already exists for exactly this
                // ("re-rank if LoTW boost changed" is its other, pre-existing call site) -- this
                // dialog just never called it. Confirmed present, unchanged, since before v1.90.9.
                wsjtxClient.SortCallsPublic();

                iniFile.Write("rankOrder",         string.Join(",", dlg.SelectedOrder.Select(m => MethodToRankId(m))));
                iniFile.Write("rankBeam",          dlg.SelectedBeam.HasValue ? MethodToBeamId(dlg.SelectedBeam.Value) : "none");
                iniFile.Write("rankMethod",        wsjtxClient.Ranker.rankMethodIdx.ToString());
                iniFile.Write("categoryWeights",   FormatCategoryWeights(dlg.SelectedCategoryWeights));
                iniFile.Write("callingPriorities", FormatCallingPriorities(dlg.SelectedCallingPriorities));

                optionsDlg?.UpdateView();
            }
        }

        private static List<WsjtxClient.RankMethods> ParseRankOrder(string rankOrderStr, int legacyIdx)
        {
            if (!string.IsNullOrWhiteSpace(rankOrderStr))
            {
                var result = new List<WsjtxClient.RankMethods>();
                foreach (var tok in rankOrderStr.Split(','))
                {
                    WsjtxClient.RankMethods m;
                    if (RankIdToMethod(tok.Trim(), out m) && !result.Contains(m))
                        result.Add(m);
                }
                if (result.Count > 0) return result;
            }
            if (legacyIdx >= (int)WsjtxClient.RankMethods.AZ_NQUAD)
                return new List<WsjtxClient.RankMethods> { WsjtxClient.RankMethods.MOST_RECENT };
            if (Enum.IsDefined(typeof(WsjtxClient.RankMethods), legacyIdx))
                return new List<WsjtxClient.RankMethods> { (WsjtxClient.RankMethods)legacyIdx };
            return new List<WsjtxClient.RankMethods> { WsjtxClient.RankMethods.MOST_RECENT };
        }

        private static WsjtxClient.RankMethods? ParseRankBeam(string rankBeamStr, int legacyIdx)
        {
            if (!string.IsNullOrWhiteSpace(rankBeamStr))
            {
                WsjtxClient.RankMethods? b;
                if (BeamIdToMethod(rankBeamStr.Trim(), out b)) return b;
                return null;
            }
            if (legacyIdx >= (int)WsjtxClient.RankMethods.AZ_NQUAD &&
                Enum.IsDefined(typeof(WsjtxClient.RankMethods), legacyIdx))
                return (WsjtxClient.RankMethods)legacyIdx;
            return null;
        }

        private static bool RankIdToMethod(string id, out WsjtxClient.RankMethods method)
        {
            switch (id?.ToLowerInvariant())
            {
                case "call_order":  method = WsjtxClient.RankMethods.CALL_ORDER;  return true;
                case "most_recent": method = WsjtxClient.RankMethods.MOST_RECENT; return true;
                case "dist_near":   method = WsjtxClient.RankMethods.DIST_INCR;   return true;
                case "dist_far":    method = WsjtxClient.RankMethods.DIST_DECR;   return true;
                case "snr_weak":    method = WsjtxClient.RankMethods.SNR_INCR;    return true;
                case "snr_strong":  method = WsjtxClient.RankMethods.SNR_DECR;    return true;
                default:            method = default;                              return false;
            }
        }

        private static bool BeamIdToMethod(string id, out WsjtxClient.RankMethods? method)
        {
            switch (id?.ToLowerInvariant())
            {
                case "none":  method = null;                                  return true;
                case "az_n":  method = WsjtxClient.RankMethods.AZ_NQUAD;   return true;
                case "az_ne": method = WsjtxClient.RankMethods.AZ_NEQUAD;  return true;
                case "az_e":  method = WsjtxClient.RankMethods.AZ_EQUAD;   return true;
                case "az_se": method = WsjtxClient.RankMethods.AZ_SEQUAD;  return true;
                case "az_s":  method = WsjtxClient.RankMethods.AZ_SQUAD;   return true;
                case "az_sw": method = WsjtxClient.RankMethods.AZ_SWQUAD;  return true;
                case "az_w":  method = WsjtxClient.RankMethods.AZ_WQUAD;   return true;
                case "az_nw": method = WsjtxClient.RankMethods.AZ_NWQUAD;  return true;
                default:      method = null;                                  return false;
            }
        }

        private static string MethodToRankId(WsjtxClient.RankMethods method)
        {
            switch (method)
            {
                case WsjtxClient.RankMethods.CALL_ORDER:  return "call_order";
                case WsjtxClient.RankMethods.MOST_RECENT: return "most_recent";
                case WsjtxClient.RankMethods.DIST_INCR:   return "dist_near";
                case WsjtxClient.RankMethods.DIST_DECR:   return "dist_far";
                case WsjtxClient.RankMethods.SNR_INCR:    return "snr_weak";
                case WsjtxClient.RankMethods.SNR_DECR:    return "snr_strong";
                default:                                   return "most_recent";
            }
        }

        private static string MethodToBeamId(WsjtxClient.RankMethods method)
        {
            switch (method)
            {
                case WsjtxClient.RankMethods.AZ_NQUAD:  return "az_n";
                case WsjtxClient.RankMethods.AZ_NEQUAD: return "az_ne";
                case WsjtxClient.RankMethods.AZ_EQUAD:  return "az_e";
                case WsjtxClient.RankMethods.AZ_SEQUAD: return "az_se";
                case WsjtxClient.RankMethods.AZ_SQUAD:  return "az_s";
                case WsjtxClient.RankMethods.AZ_SWQUAD: return "az_sw";
                case WsjtxClient.RankMethods.AZ_WQUAD:  return "az_w";
                case WsjtxClient.RankMethods.AZ_NWQUAD: return "az_nw";
                default:                                 return "none";
            }
        }

        // Serialize categoryWeight to a comma-separated string of "CATEGORY=tier" pairs.
        // Order follows the CallCategory enum so it is stable and human-readable.
        private static string FormatCategoryWeights(Dictionary<WsjtxClient.CallCategory, int> weights)
        {
            var parts = new System.Text.StringBuilder();
            foreach (WsjtxClient.CallCategory cat in System.Enum.GetValues(typeof(WsjtxClient.CallCategory)))
            {
                if (parts.Length > 0) parts.Append(',');
                int tier;
                weights.TryGetValue(cat, out tier);
                parts.Append($"{cat}={tier}");
            }
            return parts.ToString();
        }

        // Parse a categoryWeights INI string back into a dictionary.
        // Returns null if the string is absent or malformed; caller falls back to defaults.
        private static Dictionary<WsjtxClient.CallCategory, int> ParseCategoryWeights(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var result = new Dictionary<WsjtxClient.CallCategory, int>();
            foreach (var tok in s.Split(','))
            {
                var kv = tok.Trim().Split('=');
                if (kv.Length != 2) return null;
                WsjtxClient.CallCategory cat;
                int tier;
                if (!System.Enum.TryParse(kv[0].Trim(), out cat)) return null;
                if (!int.TryParse(kv[1].Trim(), out tier) || tier < 0) return null;
                result[cat] = tier;
            }
            return result;
        }

        // Serialize callingEnabled to a comma-separated string preserving list order.
        private static string FormatCallingPriorities(List<WsjtxClient.CallCategory> enabled)
        {
            if (enabled == null) return string.Empty;
            return string.Join(",", enabled.Select(cat => cat.ToString()));
        }

        // Parse a callingPriorities INI string into an ordered List of enabled categories.
        // INI token order is preserved — that order drives Alt+N category selection.
        // callingStr: new "callingPriorities" key (comma-separated enabled categories in order).
        // legacyDisabledStr: old "categoryDisabled" key used for migration if callingStr absent.
        // Returns default priority order on missing/malformed input.
        private static List<WsjtxClient.CallCategory> ParseCallingPriorities(
            string callingStr, string legacyDisabledStr = null)
        {
            if (!string.IsNullOrWhiteSpace(callingStr))
            {
                var result = new List<WsjtxClient.CallCategory>();
                foreach (var tok in callingStr.Split(','))
                {
                    WsjtxClient.CallCategory cat;
                    if (System.Enum.TryParse(tok.Trim(), out cat) && !result.Contains(cat))
                        result.Add(cat);   // order preserved; DEFAULT (Ordinary CQ) is permitted
                }
                if (result.Count > 0) return result;
            }

            // Migration: if old categoryDisabled exists, derive callingPriorities from it.
            // Enabled categories go in default priority order; disabled ones are excluded.
            if (!string.IsNullOrWhiteSpace(legacyDisabledStr))
            {
                var disabled = new HashSet<WsjtxClient.CallCategory>();
                foreach (var tok in legacyDisabledStr.Split(','))
                {
                    WsjtxClient.CallCategory cat;
                    if (System.Enum.TryParse(tok.Trim(), out cat) && cat != WsjtxClient.CallCategory.DEFAULT)
                        disabled.Add(cat);
                }
                var result = new List<WsjtxClient.CallCategory>();
                foreach (WsjtxClient.CallCategory cat in DefaultCallingOrder)
                {
                    if (!disabled.Contains(cat)) result.Add(cat);
                }
                return result;
            }

            // Default: all non-DEFAULT categories in default priority order.
            return new List<WsjtxClient.CallCategory>(DefaultCallingOrder);
        }

        // Canonical default Alt+N priority order (highest → lowest).
        private static readonly WsjtxClient.CallCategory[] DefaultCallingOrder =
        {
            WsjtxClient.CallCategory.TO_MYCALL,
            WsjtxClient.CallCategory.NEW_COUNTRY_ON_BAND,
            WsjtxClient.CallCategory.NEW_COUNTRY,
            WsjtxClient.CallCategory.WANTED_CQ,
            WsjtxClient.CallCategory.ALWAYS_WANTED,
            WsjtxClient.CallCategory.WAS_NEEDED,
            WsjtxClient.CallCategory.WAS_UNCONFIRMED,
            WsjtxClient.CallCategory.DXCC_UNCONFIRMED,
            WsjtxClient.CallCategory.ZONE_NEEDED,
            WsjtxClient.CallCategory.STILL_NEEDED,
            WsjtxClient.CallCategory.DEFAULT,
        };

        // Serialize wantedCalls to a comma-separated list of callsigns.
        private static string FormatWantedCalls(HashSet<string> calls)
        {
            if (calls == null || calls.Count == 0) return string.Empty;
            var sorted = new List<string>(calls);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(",", sorted);
        }

        // Parse a wantedCalls INI string into a HashSet (uppercase, trimmed, no duplicates).
        private static HashSet<string> ParseWantedCalls(string s)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(s)) return result;
            foreach (var tok in s.Split(new char[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string call = tok.Trim().ToUpperInvariant();
                if (!string.IsNullOrEmpty(call)) result.Add(call);
            }
            return result;
        }

        // A tracked Still Need award's Rule Definition file can be deleted out from under
        // it (individually via the Rule Definition Manager, or in bulk via Restore Default
        // Awards) -- RefreshStillNeedCache already no-ops safely when an Id has no matching
        // definition, but nothing previously dropped the stale Id itself, so it would sit
        // in the ini forever and silently resume tracking if that exact Id were ever reused
        // by an unrelated future award. Called after every Rule Definition Manager action
        // (see onImportComplete above), not just Restore Defaults.
        private void PruneStaleActiveAwardRuleIds()
        {
            var liveIds = new HashSet<string>(RuleLibrary.Definitions.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
            if (activeAwardRuleIds.RemoveWhere(id => !liveIds.Contains(id)) > 0)
                iniFile?.Write("activeAwardRuleIds", FormatActiveAwardRuleIds(activeAwardRuleIds));
        }

        // Serialize activeAwardRuleIds to a comma-separated list of Rule Definition Ids.
        public static string FormatActiveAwardRuleIds(HashSet<string> ids)
        {
            if (ids == null || ids.Count == 0) return string.Empty;
            var sorted = new List<string>(ids);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(",", sorted);
        }

        // Parse an activeAwardRuleIds INI string into a HashSet (trimmed, no duplicates).
        public static HashSet<string> ParseActiveAwardRuleIds(string s)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(s)) return result;
            foreach (var tok in s.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string id = tok.Trim();
                if (!string.IsNullOrEmpty(id)) result.Add(id);
            }
            return result;
        }

        // Called by OptionsDlg when the Wanted Calls tab is saved.
        public void ApplyAndSaveWantedCalls(HashSet<string> normalized)
        {
            wsjtxClient.ApplyWantedCalls(normalized);
            if (iniFile != null)
                iniFile.Write("wantedCalls", FormatWantedCalls(normalized));
        }

        // Serialize spotWatchCalls to a comma-separated list of callsigns.
        // Deliberately its own list, separate from wantedCalls, so adding a call here has no
        // effect on call-queue ranking priority -- see project decision, 2026-07-07.
        // Public (unlike FormatWantedCalls) so JimmyTests can cover the round-trip directly --
        // matches the existing FormatActiveAwardRuleIds/ParseActiveAwardRuleIds precedent below.
        public static string FormatSpotWatchCalls(HashSet<string> calls)
        {
            if (calls == null || calls.Count == 0) return string.Empty;
            var sorted = new List<string>(calls);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(",", sorted);
        }

        // Parse a spotWatchCalls INI string into a HashSet (uppercase, trimmed, no duplicates).
        public static HashSet<string> ParseSpotWatchCalls(string s)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(s)) return result;
            foreach (var tok in s.Split(new char[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string call = tok.Trim().ToUpperInvariant();
                if (!string.IsNullOrEmpty(call)) result.Add(call);
            }
            return result;
        }

        // Called by OptionsDlg when the Spot Watch tab is saved.
        public void ApplyAndSaveSpotWatchCalls(HashSet<string> normalized)
        {
            wsjtxClient.ApplySpotWatchCalls(normalized);
            if (iniFile != null)
                iniFile.Write("spotWatchCalls", FormatSpotWatchCalls(normalized));
            dxSpotWatcher?.UpdateWatchList(normalized);
        }

        private void callListBox_MouseDown(object sender, MouseEventArgs e)
        {
            mouseEventArgs = e;
            listBoxClickCount++;
            callListBoxClickTimer.Start();
        }

        private void callListBoxClickTimer_Tick(object sender, EventArgs e)
        {
            callListBoxClickTimer.Stop();
            bool dblClk = listBoxClickCount > 1;
            listBoxClickCount = 0;
            ProcessCallListBoxAnyClick(dblClk);
        }

        private void ProcessCallListBoxAnyClick(bool dblClk)
        {
            if (!formLoaded) return;

            int idx = callListBox.IndexFromPoint(mouseEventArgs.Location);

            if (mouseEventArgs.Button == MouseButtons.Right)
            {
                if (Control.ModifierKeys == Keys.Control)
                {
                    if (idx < 0 || callListBox.SelectionMode == SelectionMode.None) return;
                    //available for ctrl/right-click action
                }
                else   //right-click (no modifier)
                {
                    if (idx >= 0 && idx < callListBox.Items.Count && callListBox.SelectionMode != SelectionMode.None) callListBox.SelectedIndex = idx;
                    wsjtxClient.EditCallQueue(wsjtxClient.MapNormalListIndex(idx));
                }
            }
            else   //left-click
            {
                if (dblClk)   //left-dbl-click (no modifier)
                {
                    if (callListBox.SelectionMode == SelectionMode.None) return;
                    int mappedIdx = wsjtxClient.MapNormalListIndex(idx);
                    wsjtxClient.NextCall(false, mappedIdx, operatorSelected: true, expectedCall: wsjtxClient.GetCallAtIndex(mappedIdx));
                }
                else
                {
                    if (idx < 0) return;

                    if (Control.ModifierKeys == Keys.Control)
                    {
                        wsjtxClient.BlockCall(idx);
                    }
                }
            }
        }

        private string MyCall()
        {
            return (wsjtxClient == null || wsjtxClient.myCall == null) ? "my call" : wsjtxClient.myCall;
        }

        private void cqOnlyRadioButton_Click(object sender, EventArgs e)
        {
            anyMsgRadioButton.Checked = cqGridRadioButton.Checked = false;
        }

        private void cqGridRadioButton_Click(object sender, EventArgs e)
        {
            anyMsgRadioButton.Checked = cqOnlyRadioButton.Checked = false;
        }

        private void anyMsgRadioButton_Click(object sender, EventArgs e)
        {
            cqGridRadioButton.Checked = cqOnlyRadioButton.Checked = false;
        }

        private void periodComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (!formLoaded) return;

            wsjtxClient.TxPeriodIdxChanged(periodComboBox.SelectedIndex);
            optionsDlg?.UpdateView();
        }

        private void directedTextBox_TextChanged(object sender, EventArgs e)
        {
            if (ignoreDirectedChange)
            {
                ignoreDirectedChange = false;
                return;           //was cleared initially
            }
            if (directedTextBox.Text == "") callDirCqCheckBox.Checked = false;
            optionsDlg?.UpdateView();
            if (formLoaded) wsjtxClient.WsjtxSettingChanged();
        }

        public void GuideListenMode()
        {
            listenModeButton_Click(null, null);
            periodComboBox.SelectedIndex = (int)WsjtxClient.ListenModeTxPeriods.ANY;
        }

        public void GuideCqMode()
        {
            cqModeButton_Click(null, null);
        }
        public void ToggleDx()
        {
            replyDxCheckBox.Checked = !replyDxCheckBox.Checked;
        }

        public void ToggleLocal()
        {
            replyLocalCheckBox.Checked = !replyLocalCheckBox.Checked;
        }

        public void ToggleActivator()
        {
            ValidateDirCqTextBox();
            if (directedTextBox.Text == separateBySpaces || directedTextBox.Text == "") directedTextBox.Text = " ";
            if (directedTextBox.Text == "POTA" && callDirCqCheckBox.Checked && !callCqDxCheckBox.Checked && !callNonDirCqCheckBox.Checked)
            {
                directedTextBox.Text = directedTextBox.Text = "";
                callDirCqCheckBox.Checked = false;
            }
            else
            {
                directedTextBox.Text = "POTA";
                callDirCqCheckBox.Checked = true;
                callCqDxCheckBox.Checked = callNonDirCqCheckBox.Checked = false;
            }
            ValidateDirCqTextBox();
        }
        public void ToggleHunter()
        {
            bool origState = replyDirCqCheckBox.Checked;
            ValidateAlertTextBox();
            if (alertTextBox.Text == separateBySpaces || alertTextBox.Text == "") alertTextBox.Text = " ";
            if (alertTextBox.Text.Contains("POTA") && replyDirCqCheckBox.Checked)
            {
                alertTextBox.Text = alertTextBox.Text.Replace("POTA", "");
                if (alertTextBox.Text.Length == 0) replyDirCqCheckBox.Checked = false;
            }
            else
            {
                if (!alertTextBox.Text.Contains("POTA")) alertTextBox.Text = $"{alertTextBox.Text} POTA";
                replyDirCqCheckBox.Checked = true;
            }
            ValidateAlertTextBox();
        }

        private void alertTextBox_TextChanged(object sender, EventArgs e)
        {
            optionsDlg?.UpdateView();
        }

        private void rowOrderButton_Click(object sender, EventArgs e)
        {
            OpenRowDisplayOrderEditor();
        }

        private void sortOrderButton_Click(object sender, EventArgs e)
        {
            OpenSortOrderEditor();
        }

        public string[] CallDirCqEntries()
        {
            ValidateDirCqTextBox();
            return directedTextBox.Text.Trim().ToUpper().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        public string[] ReplyDirCqEntries()
        {
            ValidateAlertTextBox();
            return alertTextBox.Text.Trim().ToUpper().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private void replyRR73CheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (formLoaded) wsjtxClient.ReplyRR73Changed(replyRR73CheckBox.Checked);
        }

        private void exceptTextBox_TextChanged(object sender, EventArgs e)
        {
            if (!formLoaded) return;

            if (exceptTextBox.Text == separateBySpaces || exceptTextBox.Text.Trim() == "" || ignoreExceptChange) return;
            wsjtxClient.BlockedTextChanged(exceptTextBox.Text);
        }

        public bool ExceptTextBoxRemove(string call)
        {
            if (call == null || !exceptTextBox.Text.Contains(call)) return false;

            exceptTextBox_Enter(null, null);
            exceptTextBox.Text = exceptTextBox.Text.Replace(call, "");      //triggers exceptTextBox_TextChanged()
            exceptTextBox_Leave(null, null);
            return true;
        }

        public void ExceptTextBoxAdd(string call)
        {
            //call known to be non-null
            exceptTextBox_Enter(null, null);
            exceptTextBox.Text = $"{call} {exceptTextBox.Text}";      //triggers exceptTextBox_TextChanged()
            exceptTextBox_Leave(null, null);
        }

        private void exceptTextBox_Enter(object sender, EventArgs e)
        {
            if (!formLoaded) return;

            exceptTextBox.ForeColor = Color.Black;
            if (exceptTextBox.Text == separateBySpaces)
            {
                exceptTextBox.Text = "";
            }
        }

        private void exceptTextBox_Leave(object sender, EventArgs e)
        {
            if (!formLoaded) return;

            exceptTextBox.ForeColor = Color.Black;

            StringBuilder sb = new StringBuilder();
            string sep = "";
            var blockedCalls = exceptTextBox.Text.Trim().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList<string>();
            foreach (string call in blockedCalls)
            {
                sb.Append($"{sep}{call}");
                sep = " ";
            }

            ignoreExceptChange = true;
            exceptTextBox.Text = sb.ToString();
            ignoreExceptChange = false;

            if (exceptTextBox.Text == "")
            {
                exceptTextBox.Text = separateBySpaces;
                exceptTextBox.ForeColor = Color.Gray;
            }
        }

        private void callListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (!formLoaded) return;
            if (callListBox.SelectionMode == SelectionMode.None) return;
            if (e.KeyCode != Keys.Space && e.KeyCode != Keys.Enter) return;

            // SuppressKeyPress (not KeyPress's own Handled) is what actually stops the native
            // ListBox from treating Space/Enter as a type-ahead search character -- found live,
            // 2026-08-10: Space in particular always matched nothing (no item starts with a
            // literal space), so the ListBox played the default Windows "no match" beep on every
            // single selection, Handled=true on KeyPress notwithstanding (that only stops
            // WinForms' own OnKeyPress from re-firing; it does not reach back and stop the
            // KeyDown-level default processing that already decided to beep).
            e.SuppressKeyPress = true;
            int idx = callListBox.SelectedIndex;
            int mappedIdx = wsjtxClient.MapNormalListIndex(idx);
            wsjtxClient.NextCall(false, mappedIdx, operatorSelected: true, expectedCall: wsjtxClient.GetCallAtIndex(mappedIdx));
            MoveFocusToStatusIfEnabled();
        }

        // Optional accessibility behavior (Options > General): after selecting a call via
        // Enter/Space in any call list (simple-mode callListBox or the advanced TX1/TX2/Raw
        // lists), move focus to statusText and force NVDA/JAWS to announce the resulting
        // status -- same technique already used by the NavStatus hotkey. Off by default;
        // not everyone wants focus to jump after every selection.
        private void MoveFocusToStatusIfEnabled()
        {
            if (!moveFocusToStatusOnCallSelect) return;
            if (!statusText.Focused)
            {
                statusText.Focus();
            }
            BeginInvoke((Action)(() => SendKeys.Send("{UP}")));
        }

        private void statusText_TextChanged(object sender, EventArgs e)
        {

        }

        private void statusText_Enter(object sender, EventArgs e)
        {
            if (statusText.SelectionLength > 0)
            {
                statusText.SelectionStart = 0;
                statusText.SelectionLength = 0;
            }
        }

        private void Controller_Enter(object sender, EventArgs e)
        {
            //tempOnly
            //statusText_Enter(null, null);
            //statusText.Focus();
        }

        private void Controller_Activated(object sender, EventArgs e)
        {
        }

        private string SpacifyMyCall()
        {
            if (!formLoaded || !wsjtxClient.ConnectedToWsjtx()) return "me";

            return wsjtxClient.SpacifyMyCall();
        }

        private void DemoSounds()
        {
            callAddedCheckBox_CheckedChanged(null, null);
            Thread.Sleep(750);
            mycallCheckBox_CheckedChanged(null, null);
            Thread.Sleep(250);
            loggedCheckBox_CheckedChanged(null, null);
        }

        private void CallListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (!formLoaded) return;

            if (e.Control && e.KeyCode == Keys.C)
            {
                if (callListBox.SelectionMode == SelectionMode.None) return;
                int idx = callListBox.SelectedIndex;
                if (idx < 0) return;
                string call = wsjtxClient.GetCallAtIndex(wsjtxClient.MapNormalListIndex(idx));
                if (call != null)
                {
                    try { Clipboard.SetText(call); }
                    catch { MessageBox.Show("Could not copy callsign to the clipboard.", friendlyName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode != Keys.Delete) return;

            int selIdx = callListBox.SelectedIndex;
            wsjtxClient.EditCallQueue(wsjtxClient.MapNormalListIndex(selIdx));
        }

        // Pre-fills the Manual Call dialog on its next open -- overwrite to call
        // someone new, or just hit Enter/OK again to repeat the same call.
        private string _lastManualCall = "";

        private CallCqDlg _callCqDlg;

        // Requested 2026-08-21: Alt+C (HotkeyAction.CallCqMode) starts calling CQ using
        // whichever CQ variant (directed CQ / CQ DX only / CQ and CQ DX, etc.) is currently set
        // in this dialog -- but those settings persist from whatever they were last saved as,
        // possibly from a previous session, and an operator who never opens this dialog in a
        // given Jimmy session has no way to know that. False at every process start (never
        // persisted -- this is a per-SESSION reminder, not a permanent setting); set true the
        // moment the dialog is actually opened (see OpenCallCqDialog below) or once the operator
        // has explicitly been asked and chosen to proceed anyway (see the CallCqMode handler in
        // ProcessCmdKey) -- either way, asked at most once per session.
        private bool _callCqOptionsReviewedThisSession = false;

        // Non-modal (Show(), not ShowDialog()) -- found 2026-07-11: a modal dialog here
        // blocked Alt+Tab back to the main window's status bar entirely, which matters a lot
        // for this one specifically since its own "Find open slot" button kicks off up to a
        // minute of live status updates on the main window. No Owner either, same reasoning
        // as Logbook/Options/Help (see their Show() call sites) -- an owned window is always
        // kept in front of its owner at the Win32 level regardless of modality.
        private void OpenCallCqDialog()
        {
            _callCqOptionsReviewedThisSession = true;
            if (_callCqDlg != null && !_callCqDlg.IsDisposed)
            {
                _callCqDlg.Activate();
                return;
            }
            _callCqDlg = new CallCqDlg(this, wsjtxClient);
            _callCqDlg.FormClosed += (s, e) => _callCqDlg = null;
            _callCqDlg.Show();
        }

        private void OpenManualCallDialog()
        {
            if (!wsjtxClient.ConnectedToWsjtx())
            {
                MessageBox.Show("The radio engine is not connected.", friendlyName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            using (var dlg = new ManualCallDlg(_lastManualCall, wsjtxClient.lookupManager))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string callsign = dlg.Callsign;
                if (wsjtxClient.IsBlockedCall(callsign))
                {
                    MessageBox.Show($"{callsign} is blocked.", friendlyName,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                _lastManualCall = callsign;
                bool started = wsjtxClient.ManualEnqueueCall(callsign);
                if (started)
                    ShowMsg($"Manual call started for {callsign}", false);
                else
                    // Found live, 2026-08-10: this used to be a silent no-op on failure -- the
                    // operator got zero feedback that anything went wrong, indistinguishable
                    // from Jimmy simply doing nothing.
                    ShowMsg($"Manual call to {callsign} could not be started -- no connection to the radio engine.", true);
            }
        }

        public void ApplyAdvancedLayout()
        {
            bool show     = advancedCallLayout;
            bool showTx1  = show && advShowTx1;
            bool showTx2  = show && advShowTx2;
            bool showRaw  = show && advShowRaw;
            bool anyAdvList = showTx1 || showTx2 || showRaw;
            bool showSpot   = show && showSpotWatch;

            SuspendLayout();

            // Normal call list is visible only when no advanced list is replacing it
            callListBox.Visible    = !anyAdvList;
            replyListLabel.Visible = !anyAdvList;

            advTx1Label.Visible   = showTx1;
            advTx1ListBox.Visible = showTx1;
            advTx2Label.Visible   = showTx2;
            advTx2ListBox.Visible = showTx2;
            advRawLabel.Visible   = showRaw;
            advRawListBox.Visible = showRaw;

            // Spot Watch requires Advanced Call Layout to be enabled -- gated on both its own
            // flag and advancedCallLayout -- but it shares the same stacked column below the
            // main controls (x=10, full width), so its position/size is computed in the same
            // block as Tx1/Tx2/Raw rather than fighting over the same screen space with an
            // independent layout pass. It never hides callListBox.
            spotWatchLabel.Visible   = showSpot;
            spotWatchListBox.Visible = showSpot;

            // Reposition and resize visible advanced/spot-watch lists so they stack tightly
            // starting just below the last main-control row, with height scaled to count.
            if (anyAdvList || showSpot)
            {
                const int startY   = 376;   // first label Y (same as designer baseline)
                const int labelH   = 14;    // approx height of bold 8.25pt label
                const int labelGap = 2;     // gap between label bottom and list top
                const int groupGap = 6;     // gap between list bottom and next label
                const int listX    = 10;

                // Lists widen to fill the window, never below today's default 280px.
                int listW = Math.Max(280, this.ClientSize.Width - 2 * listX);

                // callListBox is hidden while any advanced list is shown, which would
                // otherwise leave a large blank rectangle where it used to sit. Give the
                // logged-calls list that reclaimed space instead of leaving it empty --
                // restored to its normal narrow, right-pinned spot when back in simple mode.
                //
                // logListX must stay strictly greater than callListBox's own X (it's never
                // moved and stays the leftmost control in this row): JimmyDirectReplay.py's
                // JimmyVerifier (carried over unchanged from the retired JimmyReplay.py)
                // identifies callListBox/logListBox by sorting same-row ListBoxes left-to-right,
                // with no visibility check, so if this ever sorted before callListBox the test
                // harness would silently swap which list it thinks is which.
                int logListX = callListBox.Location.X + 1;
                loggedLabel.Location = new Point(logListX, 6);
                logListBox.Location  = new Point(logListX, 24);
                logListBox.Size      = new Size(listW - 1, 107);
                logListBox.Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

                int count = (showTx1 ? 1 : 0) + (showTx2 ? 1 : 0) + (showRaw ? 1 : 0);

                // Spot Watch's own fixed space, reserved up front so TX1/TX2/Raw never expand
                // to consume the whole window and push it off the bottom (it used to get
                // tacked on after the extra-space distribution below, with no space guaranteed).
                const int spotWatchH = 92;
                int spotWatchReserve = showSpot ? (labelH + labelGap + spotWatchH) : 0;

                // Extra vertical room the current window height offers beyond the natural
                // (unstretched) size for however many lists are shown, split evenly between
                // them — grows TX1/TX2/Raw with the window without shrinking below the base sizes.
                int naturalBottom = NaturalAdvancedListsBottom(showTx1, showTx2, showRaw, out int baseListH, out int baseRawH);
                int extra = Math.Max(0, this.Height - (naturalBottom + spotWatchReserve + 45));
                int extraPerList = count > 0 ? extra / count : 0;

                int listH = baseListH + extraPerList;
                int rawH  = baseRawH + extraPerList;

                int y = startY;
                if (showTx1)
                {
                    advTx1Label.Location   = new Point(listX, y);
                    y += labelH + labelGap;
                    advTx1ListBox.Location = new Point(listX, y);
                    advTx1ListBox.Size     = new Size(listW, listH);
                    y += listH + groupGap;
                }
                if (showTx2)
                {
                    advTx2Label.Location   = new Point(listX, y);
                    y += labelH + labelGap;
                    advTx2ListBox.Location = new Point(listX, y);
                    advTx2ListBox.Size     = new Size(listW, listH);
                    y += listH + groupGap;
                }
                if (showRaw)
                {
                    advRawLabel.Location   = new Point(listX, y);
                    y += labelH + labelGap;
                    advRawListBox.Location = new Point(listX, y);
                    advRawListBox.Size     = new Size(listW, rawH);
                    y += rawH + groupGap;
                }
                if (showSpot)
                {
                    spotWatchLabel.Location   = new Point(listX, y);
                    y += labelH + labelGap;
                    spotWatchListBox.Location = new Point(listX, y);
                    spotWatchListBox.Size     = new Size(listW, spotWatchH);
                }
            }
            else
            {
                // Simple mode: callListBox is visible again, so restore logListBox's
                // normal narrow, right-pinned position beside it.
                loggedLabel.Location = new Point(366, 6);
                logListBox.Location  = new Point(366, 24);
                logListBox.Size      = new Size(140, 107);
                logListBox.Anchor    = AnchorStyles.Top | AnchorStyles.Right;
            }

            ResumeLayout(false);

            if (!show)
                wsjtxClient?.UpdateCallListAccessibleName(force: true);

            UpdateDebug();

            if (show && wsjtxClient != null)
                wsjtxClient.RefreshAdvancedLists();
        }

        // Shared alternating-row-color painter, used by callListBox/logListBox/advTx1ListBox/
        // advTx2ListBox/advRawListBox. Purely visual -- item text/accessible behavior is
        // unchanged, so screen readers are unaffected. Reads Font/BackColor/ForeColor live
        // from the control at paint time, so Appearance settings apply with no changes here;
        // AdvListAltRowColor is the one true constant, now settable via ApplyListAppearance().
        private Color AdvListAltRowColor = Color.FromArgb(233, 233, 233);

        // Applies the current Appearance settings (font size/colors) to all 5 main
        // lists. Called once at startup (after Settings.LoadFromIni) and again whenever
        // Options saves. ItemHeight is recalculated from the new font so larger sizes
        // don't clip -- it was previously a hardcoded 15 sized only for the default 10pt.
        public void ApplyListAppearance()
        {
            var font = new Font("Consolas", Settings.ListFontSize, FontStyle.Bold);
            int itemHeight = TextRenderer.MeasureText("Ag", font).Height + 2;

            ListBox[] lists = { callListBox, logListBox, advTx1ListBox, advTx2ListBox, advRawListBox };
            foreach (var lb in lists)
            {
                lb.Font = font;
                lb.BackColor = Settings.ListBackColor;
                lb.ForeColor = Settings.ListForeColor;
                lb.ItemHeight = itemHeight;
            }

            AdvListAltRowColor = Settings.ListAltRowColor;

            foreach (var lb in lists)
                lb.Invalidate();
        }

        // Per-category alert colors (Options > Appearance) only apply to the lists that carry
        // a CallCategory per row -- callListBox/advRawListBox/advTx1ListBox/advTx2ListBox.
        // logListBox has no category concept (logged calls aren't tagged), so it's absent here
        // and always falls through to the plain alternating-row colors below.
        private List<WsjtxClient.CallCategory> CategoriesForList(ListBox lb)
        {
            if (lb == callListBox) return _callQueueCategories;
            if (lb == advRawListBox) return _rawDecodeCategories;
            if (lb == advTx1ListBox) return _tx1Categories;
            if (lb == advTx2ListBox) return _tx2Categories;
            return null;
        }

        private void AdvListBox_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var lb = (ListBox)sender;
            string text = lb.Items[e.Index].ToString();

            var categories = CategoriesForList(lb);
            WsjtxClient.CallCategory category = (categories != null && e.Index < categories.Count)
                ? categories[e.Index] : WsjtxClient.CallCategory.DEFAULT;
            Color? alertBack = null, alertFore = null;
            if (category != WsjtxClient.CallCategory.DEFAULT)
            {
                Settings.AlertBackColors.TryGetValue(category, out alertBack);
                Settings.AlertForeColors.TryGetValue(category, out alertFore);
            }

            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color backColor = selected ? SystemColors.Highlight
                             : alertBack ?? ((e.Index % 2 == 0) ? lb.BackColor : AdvListAltRowColor);
            Color foreColor = selected ? SystemColors.HighlightText : (alertFore ?? lb.ForeColor);

            using (var backBrush = new SolidBrush(backColor))
                e.Graphics.FillRectangle(backBrush, e.Bounds);

            TextRenderer.DrawText(e.Graphics, text, lb.Font, e.Bounds, foreColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            e.DrawFocusRectangle();
        }

        private void AdvTx1ListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (!formLoaded) return;

            if (e.Control && e.KeyCode == Keys.C)
            {
                int idx = advTx1ListBox.SelectedIndex;
                if (idx < 0) return;
                string call = wsjtxClient.GetCallAtTx1Index(idx);
                if (call != null)
                {
                    try { Clipboard.SetText(call); }
                    catch { MessageBox.Show("Could not copy callsign.", friendlyName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.Delete)
            {
                int idx = advTx1ListBox.SelectedIndex;
                if (idx < 0) return;
                int queueIdx = wsjtxClient.GetQueueIndexForTx1(idx);
                if (queueIdx >= 0) wsjtxClient.EditCallQueue(queueIdx);
                return;
            }

            // SuppressKeyPress (not a KeyPress handler's own Handled) is what actually stops the
            // native ListBox from treating Space/Enter as a type-ahead search character -- found
            // live, 2026-08-10: Space in particular always matched nothing (no item starts with a
            // literal space), so the ListBox played the default Windows "no match" beep on every
            // single selection. Folded into this existing KeyDown handler (was a separate
            // KeyPress handler) since SuppressKeyPress must be set here, at KeyDown, to work.
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                int idx = advTx1ListBox.SelectedIndex;
                if (idx < 0) idx = 0;
                wsjtxClient.NextCallFromTx1(idx);
                MoveFocusToStatusIfEnabled();
            }
        }

        private void AdvTx2ListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (!formLoaded) return;

            if (e.Control && e.KeyCode == Keys.C)
            {
                int idx = advTx2ListBox.SelectedIndex;
                if (idx < 0) return;
                string call = wsjtxClient.GetCallAtTx2Index(idx);
                if (call != null)
                {
                    try { Clipboard.SetText(call); }
                    catch { MessageBox.Show("Could not copy callsign.", friendlyName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.Delete)
            {
                int idx = advTx2ListBox.SelectedIndex;
                if (idx < 0) return;
                int queueIdx = wsjtxClient.GetQueueIndexForTx2(idx);
                if (queueIdx >= 0) wsjtxClient.EditCallQueue(queueIdx);
                return;
            }

            // SuppressKeyPress (not a KeyPress handler's own Handled) is what actually stops the
            // native ListBox from treating Space/Enter as a type-ahead search character -- found
            // live, 2026-08-10: Space in particular always matched nothing (no item starts with a
            // literal space), so the ListBox played the default Windows "no match" beep on every
            // single selection. Folded into this existing KeyDown handler (was a separate
            // KeyPress handler) since SuppressKeyPress must be set here, at KeyDown, to work.
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                int idx = advTx2ListBox.SelectedIndex;
                if (idx < 0) idx = 0;
                wsjtxClient.NextCallFromTx2(idx);
                MoveFocusToStatusIfEnabled();
            }
        }

        private void AdvRawListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (!formLoaded) return;

            if (e.Control && e.KeyCode == Keys.C)
            {
                int idx = advRawListBox.SelectedIndex;
                if (idx < 0) return;
                string text = wsjtxClient.GetRawDecodeCallOrText(idx);
                if (text != null)
                {
                    try { Clipboard.SetText(text); }
                    catch { MessageBox.Show("Could not copy to clipboard.", friendlyName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            // SuppressKeyPress (not a KeyPress handler's own Handled) is what actually stops the
            // native ListBox from treating Space/Enter as a type-ahead search character -- found
            // live, 2026-08-10: Space in particular always matched nothing (no item starts with a
            // literal space), so the ListBox played the default Windows "no match" beep on every
            // single selection. Folded into this existing KeyDown handler (was a separate
            // KeyPress handler) since SuppressKeyPress must be set here, at KeyDown, to work.
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                int idx = advRawListBox.SelectedIndex;
                if (idx < 0) return;
                wsjtxClient.NextCallFromRawDecode(idx);
                MoveFocusToStatusIfEnabled();
            }
        }
    }
}

