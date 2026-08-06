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
        public bool keepTransmitListDuringTx = false;
        public bool keepListPositionDuringRefresh = false;
        public bool moveFocusToStatusOnCallSelect = false;
        public bool checkForUpdatesOnStartup = false;

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
        // Self-sufficiency plan, Phase 3: independent of DecodeEngineMode entirely -- an
        // operator can use Jimmy's own TQSL invocation for LoTW while still using WM8Q/WSJT-X
        // for decode, since WM8Q sub-command 16 has no coupling to decode/encode at all.
        // WsjtxDelegated (default) is unchanged existing behavior (Alt+U -> sub-command 16).
        public LotwUploadPath lotwUploadPath = LotwUploadPath.WsjtxDelegated;
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
        public System.Windows.Forms.Button callCqOptionsButton;

        // Ids of the Rule Definitions checked for live FT8 tagging in the Logbook window's
        // Still Need tab, persisted so tagging survives across sessions and works even before
        // the Logbook window has been opened. Empty = none actively tracked. Several awards
        // can be tracked at once (see RefreshStillNeedCache()).
        public HashSet<string> activeAwardRuleIds = new HashSet<string>();

        // DX Spot Watch: tracks last-seen band/time/spotter for a user-curated callsign list
        // via the PSKReporter MQTT feed. See spotWatchCalls (WsjtxClient) for the watch list.
        private DxSpotWatcher dxSpotWatcher;

        // Self-sufficiency plan, Phase 1: Hamlib rigctld radio-state source. Null whenever
        // Radio.Mode == WsjtxCat -- only instantiated/launched when the operator opts into
        // HamlibRigctld. Public so OptionsDlg's own throwaway test client (RadioTestButton_Click)
        // isn't the only thing that can reach it; kept but not itself made thread-safe (single
        // owner: radioPollTimer_Tick and on-demand callers like Alt+Q, never concurrently).
        public RigctldClient rigctldClient;
        public System.Windows.Forms.Timer radioPollTimer;
        public RadioStatus lastRadioStatus;

        // Self-sufficiency plan, Phase 4g: Jimmy's own native FT8 engine host. Null whenever
        // EngineModeCutover.Mode == WsjtxExternal (today's default) -- only launched when that
        // INI-only cutover flag is set to JimmyNative. See NativeEngineClient's own header
        // comment: RECEIVE ONLY, no PTT/transmit wiring yet.
        public NativeEngineClient nativeEngineClient;
        public NativeEngineSettings NativeEngine = new NativeEngineSettings();
        // True only if THIS session created %TEMP%\WSJT-X.lock to make Jimmy's existing
        // UdpLoop/CheckWsjtxRunning open its UDP socket for the native engine (which, unlike a
        // real external WSJT-X-family process, doesn't create that file itself). Never deletes
        // a lock file it didn't create -- a real external WSJT-X could legitimately own it.
        private bool _nativeEngineOwnsLockFile;
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
        private bool openOptionsOnUdpTab = false;
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
            radioPollTimer = new System.Windows.Forms.Timer();
            radioPollTimer.Interval = Math.Max(200, Radio.PollIntervalMs);
            radioPollTimer.Tick += new System.EventHandler(radioPollTimer_Tick);
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

        private bool IsJimmyForegrounded() => GetForegroundWindow() == this.Handle;

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

        private void Form_Load(object sender, EventArgs e)
        {
            //use .ini file for settings (avoid .Net config file mess)
            string pgmName = Assembly.GetExecutingAssembly().GetName().Name.ToString();
            string path = $"{Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}\\{pgmName}";
            string pathFileNameExt = path + "\\" + pgmName + ".ini";
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

            string ipAddrStr = null;
            IPAddress ipAddress = null;
            int port = 0;
            bool multicast = true;
            bool overrideUdpDetect = false;
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
                debug = Properties.Settings.Default.debug;
                if (Properties.Settings.Default.windowPos != new Point(0, 0)) 
                    this.Location = Properties.Settings.Default.windowPos;
                if (Properties.Settings.Default.windowHt != 0) 
                    this.Height = Properties.Settings.Default.windowHt;
                ipAddrStr = Properties.Settings.Default.ipAddress;
                port = Properties.Settings.Default.port;
                multicast = Properties.Settings.Default.multicast;
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
                bandComboBox.SelectedIndex = newOnBand ? 1 : 0;
                usePskReporter = Properties.Settings.Default.usePskReporter;
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

                ipAddrStr = iniFile.Read("ipAddress");
                multicast = iniFile.Read("multicast") == "True";
                try
                {
                    ipAddress = IPAddress.Parse(ipAddrStr);
                    port = int.Parse(iniFile.Read("port"));
                }
                catch (Exception)
                {
                    ipAddrStr = Properties.Settings.Default.ipAddress;
                    port = Properties.Settings.Default.port;
                    multicast = Properties.Settings.Default.multicast;
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
                overrideUdpDetect = iniFile.Read("overrideUdpDetect") == "True";
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
                // Self-sufficiency plan (Protocol/BackendMode.cs) -- same emergency-rollback-valve
                // shape as useClassificationEngine above: INI-only, undocumented, default unchanged
                // (WsjtxExternal/SeparateProcess) until Phase 4 has field validation to justify a
                // real Options control.
                if (iniFile.KeyExists("decodeEngineMode") && System.Enum.TryParse(iniFile.Read("decodeEngineMode"), out DecodeEngineMode dem)) EngineModeCutover.Mode = dem;
                if (iniFile.KeyExists("engineProcessModel") && System.Enum.TryParse(iniFile.Read("engineProcessModel"), out EngineProcessModel epm)) EngineModeCutover.ProcessModel = epm;
                NativeEngine.LoadFromIni(iniFile);
                Radio.LoadFromIni(iniFile);
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
                keepTransmitListDuringTx = iniFile.Read("keepTransmitListDuringTx") == "True";
                keepListPositionDuringRefresh = iniFile.Read("keepListPositionDuringRefresh") == "True";
                moveFocusToStatusOnCallSelect = iniFile.Read("moveFocusToStatusOnCallSelect") == "True";
                checkForUpdatesOnStartup = iniFile.Read("checkForUpdatesOnStartup") == "True";

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
                if (iniFile.KeyExists("lotwUploadPath") && System.Enum.TryParse(iniFile.Read("lotwUploadPath"), out LotwUploadPath lup)) lotwUploadPath = lup;
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

            //start the UDP message server
            wsjtxClient = new WsjtxClient(this, IPAddress.Parse(ipAddrStr), port, multicast, overrideUdpDetect, debug, diagLog, txMode);
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
            ApplyRadioSettings();   // Phase 1: launches/connects rigctld if Radio.Mode was saved as HamlibRigctld
            ApplyEngineMode();      // Phase 4g: launches the native engine host if EngineModeCutover.Mode is JimmyNative
            wsjtxClient.rawPriorityTags = rawPriorityTags;
            wsjtxClient.cmdPrompts = cmdPrompts;
            wsjtxClient.usePskReporter = usePskReporter;

            lookupManager = new LookupManager();
            lookupManager.RegisterProvider(dxSpotWatcher);
            lookupManager.Initialize(
                useLookupData,
                qrzEnabled, qrzUsername, qrzPassword, qrzCacheDays,
                lotwEnabled, lotwRefreshDays,
                ClubLogAppKey.Resolve(), clubLogRefreshDays,
                fccUlsEnabled,
                qrzLookupPolicy, qrzMinIntervalSeconds);
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
                AccessibleName = "Logbook, " + FormatKeysForAccessibleName(HotkeyAction.OpenLogbook),
                Location       = new System.Drawing.Point(10, 333),
                Size           = new System.Drawing.Size(492, 24),
                Anchor         = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right,
                TabIndex       = 50,
            };
            logbookButton.Click += (s2, e2) => OpenLogbookWindow();
            this.Controls.Add(logbookButton);
            logbookButton.BringToFront();

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
                // Focusing a textbox that already shows the same text doesn't trigger a fresh
                // screen-reader announcement; moving the caret does. See RenderStatus for the
                // same pattern used on every routine status update.
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
            if (iniFile != null)
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
                if (wsjtxClient.ipAddress != null) iniFile.Write("ipAddress", wsjtxClient.ipAddress.ToString());   //string
                if (wsjtxClient.port != 0) iniFile.Write("port", wsjtxClient.port.ToString());
                iniFile.Write("multicast", wsjtxClient.multicast.ToString());
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
                iniFile.Write("overrideUdpDetect", wsjtxClient.overrideUdpDetect.ToString());
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
                Settings.SaveToIni(iniFile);
                Radio.SaveToIni(iniFile);
                NativeEngine.SaveToIni(iniFile);
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
                iniFile.Write("keepTransmitListDuringTx", keepTransmitListDuringTx.ToString());
                iniFile.Write("keepListPositionDuringRefresh", keepListPositionDuringRefresh.ToString());
                iniFile.Write("moveFocusToStatusOnCallSelect", moveFocusToStatusOnCallSelect.ToString());
                iniFile.Write("checkForUpdatesOnStartup", checkForUpdatesOnStartup.ToString());
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
                iniFile.Write("lotwUploadPath",          lotwUploadPath.ToString());
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

            CloseComm();
            optionsDlg?.Close();
            if (helpDlg != null) helpDlg.Close();
            _logbookWindow?.Close();
        }

        public void SaveHotkeyConfig()
        {
            if (iniFile != null) hotkeyConfig?.SaveToIni(iniFile);
            RefreshHotkeyAccessibleNames();
        }

        // optionsButton, rowOrderButton, sortOrderButton, logbookButton, and modeHelpLabel
        // show their assigned shortcut in AccessibleName; keep that in sync whenever hotkeys
        // are loaded or reassigned. logbookButton doesn't exist yet on the first call
        // (Form_Load reads hotkeys before creating it) -- it sets its own initial
        // AccessibleName at construction, then this keeps it current after that.
        private void RefreshHotkeyAccessibleNames()
        {
            if (hotkeyConfig == null) return;
            optionsButton.AccessibleName   = "Options, "                       + FormatKeysForAccessibleName(HotkeyAction.Options);
            rowOrderButton.AccessibleName  = "Row Display Order, "             + FormatKeysForAccessibleName(HotkeyAction.RowOrder);
            sortOrderButton.AccessibleName = "Stations Available Sort Order, " + FormatKeysForAccessibleName(HotkeyAction.SortOrder);
            modeHelpLabel.AccessibleName   = "Help, "                         + FormatKeysForAccessibleName(HotkeyAction.Help);
            if (logbookButton != null)
                logbookButton.AccessibleName = "Logbook, " + FormatKeysForAccessibleName(HotkeyAction.OpenLogbook);
        }

        private string FormatKeysForAccessibleName(HotkeyAction action)
        {
            Keys keys = hotkeyConfig[action];
            if (keys == Keys.None) return "no shortcut assigned";
            return HotkeyConfig.FormatKeys(keys).Replace("+", " ");
        }

        public void CloseComm()
        {
            if (mainLoopTimer != null) mainLoopTimer.Stop();
            mainLoopTimer = null;
            statusMsgTimer.Stop();
            initialConnFaultTimer.Stop();
            radioPollTimer?.Stop();
            rigctldClient?.Dispose();   // also stops any bundled rigctld this session launched
            nativeEngineClient?.Dispose();   // also stops the native engine host this session launched
            nativeEngineClient = null;
            if (_nativeEngineOwnsLockFile)
            {
                try { System.IO.File.Delete(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WSJT-X.lock")); }
                catch { /* best-effort cleanup */ }
                _nativeEngineOwnsLockFile = false;
            }
            wsjtxClient.Closing();
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

            if (hotkeyConfig[HotkeyAction.Band160m] != Keys.None && keyData == hotkeyConfig[HotkeyAction.Band160m]) return wsjtxClient.SelectBand(0);
            if (hotkeyConfig[HotkeyAction.Band80m]  != Keys.None && keyData == hotkeyConfig[HotkeyAction.Band80m])  return wsjtxClient.SelectBand(1);
            if (hotkeyConfig[HotkeyAction.Band60m]  != Keys.None && keyData == hotkeyConfig[HotkeyAction.Band60m])  return wsjtxClient.SelectBand(2);
            if (hotkeyConfig[HotkeyAction.Band40m]  != Keys.None && keyData == hotkeyConfig[HotkeyAction.Band40m])  return wsjtxClient.SelectBand(3);
            if (hotkeyConfig[HotkeyAction.Band30m]  != Keys.None && keyData == hotkeyConfig[HotkeyAction.Band30m])  return wsjtxClient.SelectBand(4);
            if (hotkeyConfig[HotkeyAction.Band20m]  != Keys.None && keyData == hotkeyConfig[HotkeyAction.Band20m])  return wsjtxClient.SelectBand(5);
            if (hotkeyConfig[HotkeyAction.Band17m]  != Keys.None && keyData == hotkeyConfig[HotkeyAction.Band17m])  return wsjtxClient.SelectBand(6);
            if (hotkeyConfig[HotkeyAction.Band15m]  != Keys.None && keyData == hotkeyConfig[HotkeyAction.Band15m])  return wsjtxClient.SelectBand(7);
            if (hotkeyConfig[HotkeyAction.Band12m]  != Keys.None && keyData == hotkeyConfig[HotkeyAction.Band12m])  return wsjtxClient.SelectBand(8);
            if (hotkeyConfig[HotkeyAction.Band10m]  != Keys.None && keyData == hotkeyConfig[HotkeyAction.Band10m])  return wsjtxClient.SelectBand(9);
            if (hotkeyConfig[HotkeyAction.Band6m]   != Keys.None && keyData == hotkeyConfig[HotkeyAction.Band6m])   return wsjtxClient.SelectBand(10);


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

        private void mainLoopTimer_Tick(object sender, EventArgs e)
        {
            if (mainLoopTimer == null) return;
            wsjtxClient.UdpLoop();
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

        public void SetupDlgClosed() { }

        public void OpenUdpConfig()
        {
            initialConnFaultTimer.Stop();

            if (optionsDlg != null)
            {
                optionsDlg.SelectUdpTab();
                optionsDlg.BringToFront();
                return;
            }

            openOptionsOnUdpTab = true;
            guideTimer.Start();
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
                    lastLotwUploadTrigger: () => wsjtxClient?.lastLotwUploadTrigger,
                    uploadLotwHotkeyText: () =>
                    {
                        string s = HotkeyConfig.FormatKeys(hotkeyConfig[HotkeyAction.UploadLotw]);
                        return string.IsNullOrEmpty(s) ? "(unassigned hotkey)" : s;
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
            optionsDlg = new OptionsDlg(wsjtxClient, this, openOptionsOnUdpTab);
            // No Owner -- see the matching comment on _logbookWindow's Show() call; an owned
            // window always stays in front of its owner at the Win32 level, which breaks
            // Alt+Tab back to the main window. Controller_FormClosing already closes this
            // explicitly (optionsDlg?.Close()), so Owner isn't needed for that either.
            openOptionsOnUdpTab = false;
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
                qrzLookupPolicy, qrzMinIntervalSeconds);
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
                if (dlg.QrzLookupOccurred)
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
            if (sound) SystemSounds.Beep.Play();

            statusText.Text = text;
            statusText.SelectionStart = 0;
            statusText.SelectionLength = 0;
            // Force NVDA/JAWS to announce this immediately, same guard as RenderStatus --
            // ShowStatus() will naturally overwrite this text on the next status rebuild
            // (see ToggleTxFirst for the same accepted pattern), which is fine: by then
            // the screen reader has already started speaking this message.
            if (statusText.Focused && Form.ActiveForm == this)
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
        public void RenderStatus(string headingText, string statusText, Color foreColor, Color backColor)
        {
            statusHeadingLabel.Text = headingText;
            this.statusText.AccessibleName = headingText;
            this.statusText.ForeColor = foreColor;
            this.statusText.BackColor = backColor;
            this.statusText.Text = statusText;
            this.statusText.SelectionStart = 0;
            this.statusText.SelectionLength = 0;
            // Guard: only send if Tilly is actually the active application.
            // SendKeys.Send uses SendInput(), which delivers to the foreground window;
            // without this guard a timer tick during focus-loss can send to Notepad.
            if (this.statusText.Focused && Form.ActiveForm == this) SendKeys.Send("{UP}");  //triggers screen reader
        }

        public void ShowMessage(string text, bool sound) => ShowMsg(text, sound);

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

        // Self-sufficiency plan, Phase 1: brings the live rigctldClient/radioPollTimer state
        // into line with Radio's current settings. Called once at startup (after
        // Radio.LoadFromIni) and again every time OptionsDlg's Radio tab is saved -- switching
        // modes takes effect immediately, no restart needed, matching the plan's "Done when"
        // criterion for this phase.
        public void ApplyRadioSettings()
        {
            radioPollTimer.Stop();

            if (Radio.Mode != RadioControlMode.HamlibRigctld)
            {
                rigctldClient?.Dispose();
                rigctldClient = null;
                lastRadioStatus = null;
                return;
            }

            // Reconnect from scratch on every apply rather than trying to patch a live
            // connection's host/port/launch-args in place -- Options changes to this tab are
            // infrequent, and a clean relaunch avoids a whole class of "which half of the old
            // settings is still live" bugs for a handful of extra milliseconds.
            rigctldClient?.Dispose();
            rigctldClient = new RigctldClient(
                Radio.UseExternalRigctld ? Radio.RigctldHost : "127.0.0.1",
                Radio.RigctldPort);

            if (!Radio.UseExternalRigctld)
            {
                if (!rigctldClient.LaunchBundled(Radio.RigModel, Radio.ComPort))
                {
                    ShowMessage($"Radio: {rigctldClient.LastError}", false);
                    return;
                }
            }

            radioPollTimer.Interval = Math.Max(200, Radio.PollIntervalMs);
            if (Radio.PollEnabled) radioPollTimer.Start();
        }

        // Self-sufficiency plan, Phase 4g: brings the live nativeEngineClient state into line
        // with EngineModeCutover.Mode (an INI-only, undocumented flag -- see BackendMode.cs).
        // Called once at startup (after EngineModeCutover/NativeEngine are loaded from the
        // .ini). Unlike ApplyRadioSettings, there is no Options UI path that re-calls this yet
        // (the cutover flag is hand-edit-only), so this only ever runs once per session for now.
        //
        // Jimmy's own UdpLoop/CheckWsjtxRunning (WsjtxClient.Protocol.cs) only opens Jimmy's UDP
        // socket after detecting %TEMP%\WSJT-X.lock -- a file a real external WSJT-X-family
        // process creates on its own startup. The native engine host is that process now, but it
        // doesn't know about (or create) that file, so this method creates it, exactly the way
        // the replay-test harness's own ensure_jimmy_udp_ready() does for the same reason. Only
        // ever deleted again if THIS session created it (_nativeEngineOwnsLockFile) -- never a
        // lock file a real external WSJT-X might legitimately own.
        public void ApplyEngineMode()
        {
            if (EngineModeCutover.Mode != DecodeEngineMode.JimmyNative)
            {
                nativeEngineClient?.Dispose();
                nativeEngineClient = null;
                return;
            }

            string lockPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WSJT-X.lock");
            if (!System.IO.File.Exists(lockPath))
            {
                try
                {
                    System.IO.File.Create(lockPath).Dispose();
                    _nativeEngineOwnsLockFile = true;
                }
                catch (Exception ex)
                {
                    ShowMessage($"Native engine: couldn't create {lockPath}: {ex.Message}", false);
                    return;
                }
            }

            nativeEngineClient?.Dispose();
            nativeEngineClient = new NativeEngineClient();
            int jimmyPort = wsjtxClient?.port > 0 ? wsjtxClient.port : 2237;
            if (!nativeEngineClient.Launch(NativeEngine.MyCall, NativeEngine.MyGrid, NativeEngine.AudioInputDevice, jimmyPort))
            {
                ShowMessage($"Native engine: {nativeEngineClient.LastError}", false);
            }
        }

        // Quiet background update, same "no sound, no forced announcement" spirit as
        // spotWatchAgeTimer_Tick above -- Alt+Q (ReportPowerSwr) always does its own fresh,
        // synchronous PollOnce() rather than relying on this cached value, so this timer only
        // needs to keep lastRadioStatus reasonably current for anything that wants to peek at
        // it without forcing a round-trip.
        private void radioPollTimer_Tick(object sender, EventArgs e)
        {
            if (rigctldClient == null) return;
            lastRadioStatus = rigctldClient.PollOnce();
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
                $"{nl}Important: Minimize WSJT-X and stay on the {friendlyName} window full-time!" +

                $"{nl}{nl}Command keys:" +
                $"{nl}{K(HotkeyAction.RowOrder)}: Open stations available row order editor." +
                $"{nl}{K(HotkeyAction.Options)}: Review or set options for processing 'QSO's." +
                $"{nl}{K(HotkeyAction.CallCqMode)}: Start selected CQ mode (CQ only / CQ DX only / CQ and CQ DX). Does nothing in Listen mode." +
                $"{nl}{K(HotkeyAction.ListenMode)}: Select 'Listen for calls' mode." +
                $"{nl}{K(HotkeyAction.EnableTx)}: Enable transmit, or re-enable timed out 'QSO'." +
                $"{nl}{K(HotkeyAction.HaltTx)}: Halt transmit immediately." +
                $"{nl}{K(HotkeyAction.NextCall)}: Skip to the next available station, very useful!" +
                $"{nl}{K(HotkeyAction.ManualCall)}: Enter a callsign manually to call." +

                $"{nl}{K(HotkeyAction.AnalyzeSlot)}: Analyze transmit slot (find quietest audio frequency for CQ; requires 'Use best Tx frequency' enabled)." +
                $"{nl}{K(HotkeyAction.LookupStation)}: Look up selected station (shows callsign, country, state, LoTW status, and more)." +
                $"{nl}{K(HotkeyAction.OpenLogbook)}: Open the Ham Radio Center logbook." +
                $"{nl}{K(HotkeyAction.AddManualQso)}: Add a manually-logged QSO (e.g. worked outside WSJT-X)." +

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
            Console.Beep();
            e.Handled = true;
        }

        private void alertTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.KeyChar = char.ToUpper(e.KeyChar);
            char c = e.KeyChar;
            if (c == (char)Keys.Back || c == ' ' || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return;
            Console.Beep();
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
            ShowHelp($"'Tx period' allows you to select which period you want WSJT-X to use for transmit when in 'Listen for calls' mode." +
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
            Console.Beep();
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
                ShowMsg($"Select calls manually in WSJT-X (alt/dbl-click)", true);
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

            if (e.Control && e.Shift && e.KeyCode == Keys.C)
            {
                if (wsjtxClient.ConnectedToWsjtx()) wsjtxClient.HaltTx();
                OpenUdpConfig();
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
                if (formLoaded && wsjtxClient.ConnectedToWsjtx()) wsjtxClient.HaltTuning();
                loggedLabel.Focus();
            }

            if (e.KeyData == hotkeyConfig[HotkeyAction.NavCallList])
            {
                if (callListBox.Visible)
                {
                    if (formLoaded && wsjtxClient.ConnectedToWsjtx()) wsjtxClient.HaltTuning();
                    callListBox.Focus();
                }
            }

            if (e.KeyData == hotkeyConfig[HotkeyAction.NavPendingCount])
            {
                if (formLoaded && wsjtxClient.ConnectedToWsjtx()) wsjtxClient.HaltTuning();
                replyListLabel.Focus();
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
                else
                {
                    Console.Beep();
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

        private void callListBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (!formLoaded) return;
            if (callListBox.SelectionMode == SelectionMode.None) return;
            if (e.KeyChar != (char)Keys.Space && e.KeyChar != (char)Keys.Enter) return;

            e.Handled = true;   // prevent Win32 ListBox type-ahead after our handler
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

        // Non-modal (Show(), not ShowDialog()) -- found 2026-07-11: a modal dialog here
        // blocked Alt+Tab back to the main window's status bar entirely, which matters a lot
        // for this one specifically since its own "Find open slot" button kicks off up to a
        // minute of live status updates on the main window. No Owner either, same reasoning
        // as Logbook/Options/Help (see their Show() call sites) -- an owned window is always
        // kept in front of its owner at the Win32 level regardless of modality.
        private void OpenCallCqDialog()
        {
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
                MessageBox.Show("WSJT-X is not connected.", friendlyName,
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
                // moved and stays the leftmost control in this row): JimmyReplay.py identifies
                // callListBox/logListBox by sorting same-row ListBoxes left-to-right, with no
                // visibility check, so if this ever sorted before callListBox the test harness
                // would silently swap which list it thinks is which.
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
            }
        }

        private void AdvTx1ListBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (!formLoaded) return;
            if (e.KeyChar != (char)Keys.Space && e.KeyChar != (char)Keys.Enter) return;

            e.Handled = true;   // prevent Win32 ListBox type-ahead after our handler
            int idx = advTx1ListBox.SelectedIndex;
            if (idx < 0) idx = 0;
            wsjtxClient.NextCallFromTx1(idx);
            MoveFocusToStatusIfEnabled();
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
            }
        }

        private void AdvTx2ListBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (!formLoaded) return;
            if (e.KeyChar != (char)Keys.Space && e.KeyChar != (char)Keys.Enter) return;

            e.Handled = true;   // prevent Win32 ListBox type-ahead after our handler
            int idx = advTx2ListBox.SelectedIndex;
            if (idx < 0) idx = 0;
            wsjtxClient.NextCallFromTx2(idx);
            MoveFocusToStatusIfEnabled();
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
            }
        }

        private void AdvRawListBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (!formLoaded) return;
            if (e.KeyChar != (char)Keys.Space && e.KeyChar != (char)Keys.Enter) return;

            e.Handled = true;   // prevent Win32 ListBox type-ahead after our handler
            int idx = advRawListBox.SelectedIndex;
            if (idx < 0) return;
            wsjtxClient.NextCallFromRawDecode(idx);
            MoveFocusToStatusIfEnabled();
        }
    }
}

