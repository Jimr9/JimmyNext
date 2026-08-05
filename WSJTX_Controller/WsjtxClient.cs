//to-do
//- 
//
//NOTE CAREFULLY: Several message classes require the use of a slightly modified WSJT-X program.
//Further information is in the README file.

using System;
//using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WsjtxUdpLib.Messages;
using WsjtxUdpLib.Messages.Out;

namespace WSJTX_Controller
{
    public partial class WsjtxClient : IDisposable
    {
        public Controller ctrl;
        // View seams (Phase 2.3/2.4 first wave) -- currently just point at ctrl (Controller
        // implements all three), but let ShowStatus/ShowQueue/ShowLogged stop touching ctrl's
        // controls directly. Kept alongside `ctrl` rather than replacing it outright: most of
        // WsjtxClient's ~450 other ctrl. touches are not migrated yet (see modernization plan).
        public IJimmyStatusView StatusView;
        public IJimmyQueueView QueueView;
        public IJimmyLogView LogView;
        public bool altListPaused = false;

        // Stage A3 (migration roadmap): socket state now owned by WsjtxProtocolAdapter
        // (Protocol/WsjtxProtocolAdapter.cs). These stay as delegating properties, not
        // fields, with their original names/visibility, so every existing call site
        // across WsjtxClient's other partial-class files and external classes
        // (Controller/OptionsDlg/SetupDlg read these; only WsjtxClient itself ever
        // assigns them) keeps working completely unchanged.
        private readonly WsjtxProtocolAdapter _protocolAdapter = new WsjtxProtocolAdapter();
        // Stage A4 (migration roadmap): isolates every non-standard NewTxMsgIdx
        // sub-command behind WsjtxCompatibilityExtension (Compatibility/
        // WsjtxCompatibilityExtension.cs). Field initializers run in declaration
        // order, so _protocolAdapter (declared just above) is already constructed
        // by the time this one runs.
        private readonly WsjtxCompatibilityExtension _compatExtension;
        // Stage A5 (migration roadmap / Architecture Blueprint §19): replaces
        // acceptableWsjtxVersions' hard version-string gate with a runtime capability
        // probe. See Protocol/CapabilityNegotiator.cs.
        private readonly CapabilityNegotiator _capabilityNegotiator = new CapabilityNegotiator();
        public WsjtxCapabilityState CapabilityState => _capabilityNegotiator.State;
        // Stage A6 (migration roadmap): cuts the queue/ranking/display/award consumers
        // over to ClassificationEngine's computed output. LogbookDb() (parameterless)
        // already respects JIMMY_TEST_DB_PATH (see LogbookDb.cs), so this is safe under
        // the replay test harness without any special-casing here. Held open for
        // WsjtxClient's whole lifetime -- disposed in Dispose() -- rather than opened
        // per-decode, since decodes can arrive many-at-once per FT8/FT4 cycle and
        // per-decode connection open/close would be wasteful; LogbookDb runs WAL mode
        // (LogbookDb.cs's InitSchema), which is safe for a persistent reader alongside
        // the app's existing short-lived writer connections elsewhere.
        //
        // _classificationEngine is constructed lazily (on first decode, not in this
        // constructor) because it needs `lookupManager`, which Controller doesn't
        // assign onto this instance until after WsjtxClient's own constructor returns
        // (Controller.cs, right after `new WsjtxClient(...)`) -- capturing it here
        // would freeze in a still-null reference. By the time any decode actually
        // arrives, Controller's startup sequence has long since finished.
        private readonly LogbookDb _logbookDb = new LogbookDb();
        private ClassificationEngine _classificationEngine;
        private ClassificationEngine Classifier => _classificationEngine ?? (_classificationEngine = new ClassificationEngine(_logbookDb, lookupManager));
        public UdpClient udpClient { get => _protocolAdapter.ReceiveSocket; set => _protocolAdapter.ReceiveSocket = value; }
        public int port { get => _protocolAdapter.Port; set => _protocolAdapter.Port = value; }
        public IPAddress ipAddress { get => _protocolAdapter.IpAddress; set => _protocolAdapter.IpAddress = value; }
        public bool multicast { get => _protocolAdapter.Multicast; set => _protocolAdapter.Multicast = value; }
        public bool overrideUdpDetect { get => _protocolAdapter.OverrideUdpDetect; set => _protocolAdapter.OverrideUdpDetect = value; }
        public bool debug;
        public string pgmName;
        public string pgmVer;
        public bool diagLog = false;
        public bool cqPaused = true;
        public int offsetLoLimit = 300;
        public int offsetHiLimit = 2800;
        public bool useRR73 = false;                //applies to non-FT4 modes

        internal string nl = Environment.NewLine;

        private List<string> supportedModes = new List<string>() { "FT8", "FT4" };

        public int maxPrevTo = 2;
        public int maxPrevPotaTo = 4;
        public int maxAutoGenEnqueue = 4;
        public int holdMaxTxRepeat = 50;
        public bool suspendComm = false;
        public string myCall = null, myGrid = null, myContinent = null;
        public bool cmdPrompts = true;
        public bool tuning = false;
        // new: optional INI-controlled order for call-waiting row fields.
        public List<string> callWaitingRowOrderFields = new List<string> { "tag", "pri", "country", "callp", "snr", "distAz" };
        // Optional INI-controlled order for Raw Decodes row fields. Callsign first by
        // default (not just embedded inside "message") so first-letter type-ahead jump
        // in the Raw Decodes list actually lands on a callsign instead of always hitting
        // "T" from the TX1:/TX2: side label every row used to start with.
        public List<string> rawDecodeRowOrderFields = new List<string> { "callsign", "side", "tag", "message", "snr", "grid", "country", "distAz" };

        private StreamWriter logSw = null;
        private bool settingChanged = false;
        private string cmdCheck = "";
        internal Dictionary<string, EnqueueDecodeMessage> callDict = new Dictionary<string, EnqueueDecodeMessage>();
        internal Queue<string> callQueue = new Queue<string>();
        internal List<string> sentReportList = new List<string>();
        internal List<string> sentCallList = new List<string>();
        internal Dictionary<string, List<EnqueueDecodeMessage>> allCallDict = new Dictionary<string, List<EnqueueDecodeMessage>>();            //all calls to this station plus CQs (and replies: grids) processed
        internal Dictionary<string, int> timeoutCallDict = new Dictionary<string, int>();    //calls sent to myCall immed after timeout
        private List<string> blockList = new List<string>();
        internal List<string> unwantedCqList = new List<string>();      //caller is unwanted directed CQ
        private List<EnqueueDecodeMessage> _rawDecodeHistory = new List<EnqueueDecodeMessage>();
        // Retained display snapshots for advanced TX1/TX2 lists.
        // Only rebuilt by AddCall (for that call's side) and global clears.
        // Never rebuilt by RemoveCall/TrimCallQueue so the display persists across
        // opposite-side decode periods.
        private List<string> _tx1SnapshotRows  = new List<string>();
        private List<string> _tx1SnapshotCalls = new List<string>();
        private List<CallCategory> _tx1SnapshotCategories = new List<CallCategory>();
        private List<string> _tx2SnapshotRows  = new List<string>();
        private List<string> _tx2SnapshotCalls = new List<string>();
        private List<CallCategory> _tx2SnapshotCategories = new List<CallCategory>();
        // Maps each normal-list display row index to its true callQueue position.
        // Rebuilt by ShowQueue whenever callInProg is filtered from the visible rows.
        private List<int> _callListBoxQueueIndices = new List<int>();
        internal bool _lastAddCallCategoryPlayed;
        private Dictionary<string, DateTime> _wantedAnywhereAlertTimes   = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, DateTime> _oppositePeriodAlertTimes    = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, DateTime> _awardAlertTimes             = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private const int WantedAnywhereAlertCooldownSecs  = 30;
        internal const int OppositePeriodAlertCooldownSecs  = 45;
        internal const int AwardAlertCooldownSecs           = 30;
        private bool txEnabled = false;
        private bool txEnabledConf = false;
        private bool wsjtxTxEnableButton = false;
        private bool transmitting = false;
        private bool decoding = false;
        private WsjtxMessage.QsoStates qsoState = WsjtxMessage.QsoStates.CALLING;
        private WsjtxMessage.QsoStates qsoStateConf = WsjtxMessage.QsoStates.CALLING;
        private string mode = "";
        private bool modeSupported = true;
        internal bool txFirst = false;
        internal int? trPeriod = null;       //msec
        private ulong dialFrequency = 0;
        private int? bandIdx = null;
        private List<int> bands = new List<int>() { 160, 80, 60, 40, 30, 20, 17, 15, 12, 10, 6 };
        private Dictionary<string, List<int>> freqsDict = new Dictionary<string, List<int>>(){
            {"FT8", new List<int>(){ 1840, 3573, 5357, 7074, 10136, 14074, 18100, 21074, 24915, 28074, 50313 }},
            {"FT4", new List<int>(){ 1840, 3575, 5357, 7047, 10140, 14080, 18104, 21140, 24919, 28180, 50318 }}};
        private UInt32 txOffset = 0;
        private string replyCmd = null;     //no "reply to" cmd sent to WSJT-X yet, will not be a CQ
        private string curCmd = null;       //cmd last issed, can be CQ
        private EnqueueDecodeMessage replyDecode = null;
        private string configuration = null;
        public string callInProg = null;
        private bool restartQueue = false;

        private WsjtxMessage.QsoStates lastQsoState = WsjtxMessage.QsoStates.INVALID;
        private UdpClient udpClient2 { get => _protocolAdapter.SendSocket; set => _protocolAdapter.SendSocket = value; }
        private IPEndPoint endPoint { get => _protocolAdapter.EndPoint; set => _protocolAdapter.EndPoint = value; }
        private bool? lastXmitting = null;
        private bool? lastTxWatchdog = null;
        private string dxCall = null;
        private string lastMode = null;
        private ulong? lastDialFrequency = null;
        private bool? lastTxFirst = null;
        private bool? lastDecoding = null;
        private int? lastSpecOp = null;
        private string lastTxMsg = null;
        private bool? lastTxEnabled = null;
        private string lastCallInProgDebug = null;
        private bool? lastTxTimeoutDebug = null;
        private string lastReplyCmdDebug = null;
        private WsjtxMessage.QsoStates lastQsoStateDebug = WsjtxMessage.QsoStates.INVALID;
        private string lastDxCallDebug = null;
        private string lastTxMsgDebug = null;
        private string lastLastTxMsgDebug = null;
        private bool lastTransmittingDebug = false;
        private bool lastRestartQueueDebug = false;
        private bool lastTxFirstDebug = false;

        private string lastDxCall = null;
        private int xmitCycleCount = 0;
        private bool txTimeout = false;
        private bool newDirCq = false;
        private int specOp = 0;
        private string lCall = null;            //last call sign logged
        private string tCall = null;            //call sign being processed at timeout or completed
        private string txMsg = null;            //msg for the most-recent Tx
        internal List<string> logList = new List<string>();      //calls logged for current mode/band for this session

        // WSJT-X sends both QsoLoggedMessage and LoggedAdifMessage for every logged QSO.
        // Either one alone is enough to record the QSO and refresh awards (see
        // HandleLiveQsoLogged/HandleLiveAdifLogged) -- this set makes sure that when both
        // normally arrive, the second one is a no-op instead of double-processing. Keyed by
        // the same callsign/band/mode/date/time dedup key LogbookDb already uses (NOT the
        // protocol's "Id" field -- that's WSJT-X's fixed per-instance identifier, the same
        // value on every message, not a per-QSO key).
        private readonly HashSet<string> _liveLoggedQsoKeys = new HashSet<string>();
        private bool ClaimLiveLoggedQso(string dedupKey) =>
            string.IsNullOrEmpty(dedupKey) || _liveLoggedQsoKeys.Add(dedupKey);

        private AsyncCallback asyncCallback { get => _protocolAdapter.AsyncCallback; set => _protocolAdapter.AsyncCallback = value; }
        private UdpState udpSt { get => _protocolAdapter.UdpSt; set => _protocolAdapter.UdpSt = value; }
        // messageRecd/datagram/fromEp/recvStarted were "private static" here -- a
        // historical artifact with no observable effect in this single-instance app
        // (only one WsjtxClient, hence only one WsjtxProtocolAdapter, is ever
        // constructed). Now plain instance state on the adapter, behavior-identical.
        private bool messageRecd { get => _protocolAdapter.MessageRecd; set => _protocolAdapter.MessageRecd = value; }
        private byte[] datagram { get => _protocolAdapter.Datagram; set => _protocolAdapter.Datagram = value; }
        private IPEndPoint fromEp { get => _protocolAdapter.FromEp; set => _protocolAdapter.FromEp = value; }
        private bool recvStarted { get => _protocolAdapter.RecvStarted; set => _protocolAdapter.RecvStarted = value; }
        private static uint defaultAudioOffset = 1500;
        private string failReason = "Failure reason: Unknown";
        public static int wsjtxRevision;
        public static int wsjtxTestVer;
        public static int lastWsjtx270RcRevision = 185;

        public const int maxQueueLines = 8, maxQueueWidth = 19, maxLogWidth = 9;
        private byte[] ba;
        private EnableTxMessage emsg;
        private WsjtxMessage msg = new UnknownMessage();
        private Random rnd = new Random();
        DateTime firstDecodeTime;
        internal const string spacer = "           *";
        private const int freqChangeThreshold = 200;
        private bool skipFirstDecodeSeries = true;
        private System.Windows.Forms.Timer postDecodeTimer = new System.Windows.Forms.Timer();
        private System.Windows.Forms.Timer processDecodeTimer = new System.Windows.Forms.Timer();
        private System.Windows.Forms.Timer processDecodeTimer2 = new System.Windows.Forms.Timer();
        private System.Windows.Forms.Timer statusTimer = new System.Windows.Forms.Timer();
        private System.Windows.Forms.Timer statusTimer2 = new System.Windows.Forms.Timer();
        public System.Windows.Forms.Timer cmdCheckTimer = new System.Windows.Forms.Timer();
        public System.Windows.Forms.Timer dialogTimer2 = new System.Windows.Forms.Timer();
        public System.Windows.Forms.Timer dialogTimer3 = new System.Windows.Forms.Timer();
        public System.Windows.Forms.Timer heartbeatRecdTimer = new System.Windows.Forms.Timer();
        private bool _requireOffsetForActive = false;   // true after a band change; keeps CheckActive from firing before WSJT-X settles
        internal string path = $"{Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}\\{Assembly.GetExecutingAssembly().GetName().Name.ToString()}";
        private List<int> audioOffsets = new List<int>();
        private int oddOffset = 0;
        private int lastOddOffsetDebug = 0;
        private int evenOffset = 0;
        private int lastEvenOffsetDebug = 0;
        public int cachedOddOffset = 0;
        public int cachedEvenOffset = 0;
        private bool analysisCompleted = false;
        private bool pendingCqAfterAnalysis = false;
        // Set by StartSlotAnalysis (the Analyze Transmit Slot hotkey, or the "run
        // recommended analysis now?" prompt before calling CQ) so CalcBestOffset still
        // runs an explicitly-requested one-time analysis even when "Use best Tx
        // frequency" is unchecked -- that checkbox governs the unprompted BACKGROUND
        // analysis only (see CalcBestOffset), not the on-demand hotkey.
        private bool _manualAnalysisRequested = false;
        // Transmit-slot analysis needs decodes in both the even and odd periods before it can
        // finish (see CalcBestOffset) -- on a quiet band that can take a long time, or never
        // happen at all, previously leaving the operator staring at "Analyzing transmit slot..."
        // forever with no further feedback and CQ never auto-starting. A flat wall-clock
        // timeout (not a decode-cycle count) is used because cycle length varies wildly by mode
        // (FT4 ~7.5s vs FST4 up to minutes) -- a cycle-count budget would be wildly inconsistent
        // in real wait time across modes.
        private System.Windows.Forms.Timer _slotAnalysisWatchdog;
        private int _slotAnalysisElapsedSeconds;
        private const int SlotAnalysisStatusIntervalSeconds = 20;
        private const int SlotAnalysisTimeoutSeconds = 60;
        private string cancelledCall = null;
        private int maxTxRepeat = 4;
        private bool _manualCallInProg = false;
        // holdTxRepeat removed — manual Hold now always uses holdMaxTxRepeat.
        private string curVerBld = null;
        private int consecCqCount = 0;
        private int lastConsecCqCountDebug = 0;
        private const int maxConsecCqCount = 8;
        private int consecTimeoutCount = 0;
        private int lastConsecTimeoutCount = 0;
        private const int maxConsecTimeoutCount = 10;
        private int consecTxCount = 0;
        private int lastConsecTxCountDebug = 0;
        private bool lastPausedDebug = true;
        private const int maxConsecTxCount = 12;
        private const uint tuningAudioOffset = 3200;
        private uint prevOffset = defaultAudioOffset;

        public NotificationSounds Sounds;
        public LiveQsoUploadOrchestrator LiveQsoUploader;
        private readonly PotaLogTracker _potaLog;
        private readonly AwardTagger _awardTagger;
        private readonly CallQueueStore _callQueueStore;
        bool wsjtxClosing = false;
        const int heartbeatInterval = 15;           //expected recv interval, sec
        string toCallTxStart = null;
        DateTime txBeginTime = DateTime.MaxValue;
        bool shortTx = false;
        bool txInterrupted = false;
        bool metricUnits = false;
        private int decodeNum = 0;
        private const int maxCheckTxRepeat = 2;
        private int decodeCycle = 0;
        private bool decodesProcessed = false;
        internal bool debugDetail = false;
        internal int maxDecodeAgeMinutes = 15;
        public TxModes txMode;
        public bool usePskReporter = true;
        public bool rawPriorityTags = false;
        public LookupManager lookupManager;
        public bool lotwBoostEnabled = false;
        private TxModes lastTxModeDebug;
        private string discardCall = null;
        private string expiredCall = null;
        private int discardCallCycleCount = 0;

        //for status display only
        private string curTxPayload = null;
        private string loggedCall = null;
        private string finalSignoffCall = null;
        private bool modePrompt = true;
        private bool replyFromInProg = false;
        private bool deletedAllCalls = false;
        private bool newTxFirst = false;
        private bool? lastCallListTxFirst = null;
        private bool newBand = false;
        private bool newMode = false;
        private string uploadResult = null;
        private string tuneResult = null;
        private string lastStatusTxMsg = null;
        private string timedOutCall = null;
        private string curTxMsg = null;
        private bool newSelection = false;
        private int wsjtxResultCode = 0;
        private string statusDetail = null;
        private int lastWsjtxResultCode = 0;
        private int decodeCount = 0;
        private int consecNoDecodes = 0;
        private int maxNoDecodes = 4;
        private List<double> timeOffsets = new List<double>();
        private double timeOffset = 0;
        private double maxTimeOffset = 1.20;
        private bool txEnableChanged = false;
        private bool promptsChanged = false;
        private string toCallStatus = null;
        private string callInProgLastActivity = null;
        private bool newPskReporter = false;

        public static bool IsWsjtx270Rc()
        {
            return WSJTX_Controller.WsjtxClient.wsjtxRevision == WSJTX_Controller.WsjtxClient.lastWsjtx270RcRevision;
        }

        public enum TxModes
        {
            LISTEN,
            CALL_CQ
        }

        private enum OpModes
        {
            IDLE,
            START,
            ACTIVE
        }
        private OpModes opMode;

        public enum CallPriority
        {
            RESERVED,
            NEW_COUNTRY,            //1
            NEW_COUNTRY_ON_BAND,    //2
            TO_MYCALL,              //3
            MANUAL_SEL,             //4
            WANTED_CQ,              //5
            DEFAULT                 //6
        }

        // Ranking category: what type of call this is for queue-ordering purposes.
        // Separate from CallPriority so behavioral checks (aging, hold, logging, RR73,
        // admission gates) remain tied to CallPriority and are not affected by
        // user-configurable ranking weights.
        //
        // DEFAULT = 0 is intentional: an uninitialized Category field on a new
        // EnqueueDecodeMessage is safe — it ranks as DEFAULT (lowest tier) rather
        // than accidentally ranking as NEW_COUNTRY (highest tier).
        public enum CallCategory
        {
            DEFAULT = 0,         // all other calls — safe uninitialized value
            NEW_COUNTRY,         // 1 — maps from Priority NEW_COUNTRY (1)
            NEW_COUNTRY_ON_BAND, // 2 — maps from Priority NEW_COUNTRY_ON_BAND (2)
            TO_MYCALL,           // 3 — maps from Priority TO_MYCALL (3)
            MANUAL_SEL,          // 4 — maps from Priority MANUAL_SEL (4)
            WANTED_CQ,           // 5 — maps from Priority WANTED_CQ (5)
            POTA,                // 6 — DEFAULT priority + IsPotaCall
            SOTA,                // 7 — DEFAULT priority + IsSotaCall
            ALWAYS_WANTED,       // 8 — matches user-defined wanted callsign list
            WAS_NEEDED,          // 9 — US state needed for WAS award (HRC database)
            WAS_UNCONFIRMED,     // 10 — US state worked but unconfirmed (HRC database)
            DXCC_UNCONFIRMED,    // 11 — DXCC entity worked but unconfirmed (HRC database)
            ZONE_NEEDED,         // 12 — CQ zone needed for WAZ award (HRC database)
            STILL_NEEDED,        // 13 — matches the Rule Definition selected in the Still Need tab
        }

        public enum RankMethods
        {
            //"sort order"
            CALL_ORDER,
            MOST_RECENT,
            DIST_INCR,
            DIST_DECR,
            SNR_INCR,
            SNR_DECR,
            AZ_NQUAD,   //AZ order important
            AZ_NEQUAD,
            AZ_EQUAD,
            AZ_SEQUAD,
            AZ_SQUAD,
            AZ_SWQUAD,
            AZ_WQUAD,
            AZ_NWQUAD   //AZ order important
        }
        // Ranking config (category weights, calling priorities, sort/rank-method state) now
        // lives in Ranker (CallQueueRanker.cs) so it's unit-testable outside a live Form.
        public CallQueueRanker Ranker = new CallQueueRanker();

        // User-defined always-wanted callsigns. Calls matching this set get ALWAYS_WANTED category.
        public HashSet<string> wantedCalls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // User-defined DX Spot Watch list -- deliberately separate from wantedCalls so a call
        // added here has no effect on call-queue ranking priority, only on which callsigns
        // DxSpotWatcher tracks for "last spotted" reporting via the PSKReporter MQTT feed.
        public HashSet<string> spotWatchCalls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // HRC database caches — populated from the local Ham Radio Center database at startup,
        // after each import, and after each band change.  All lookups are in-memory.
        public HashSet<string> hrcNeededStates      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> hrcUnconfirmedStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<int>    hrcUnconfirmedDxcc   = new HashSet<int>();
        public HashSet<int>    hrcNeededZones       = new HashSet<int>();

        // One entry per Rule Definition currently checked for live tagging in the
        // Logbook window's Still Need tab (see Controller.RefreshStillNeedCache()).
        // Independent of the fixed HRC sets above: this supports any enabled Rule
        // Definition, not just WAS/DXCC/WAZ, and several can be active at once.
        // Only GroupBy kinds with a fast decode-time field are usable (see
        // RuleEngine.SupportsLiveTag). A rule is simply absent from this dictionary
        // whenever it isn't checked, its GroupBy isn't one of those kinds, or it has
        // no fixed still-needed checklist (e.g. Target=COUNT/LEVELS awards never
        // produce one).
        public class ActiveAwardTag
        {
            public string          RuleId;
            public string          RuleName;
            public RuleGroupBy     GroupBy;
            public HashSet<string> Set;
        }
        public Dictionary<string, ActiveAwardTag> activeAwardTags = new Dictionary<string, ActiveAwardTag>();

        // Returns the ADIF-style band string for the current dial frequency (e.g. "20m"),
        // or null when the frequency is unknown or off-band.
        public string CurrentBandStr => FreqToBandStr(dialFrequency / 1e6);

        // WSJT-X's current reported mode (e.g. "FT8", "FT4"), straight from the last
        // StatusMessage -- just a default suggestion for manual QSO entry, not a
        // restriction; the user can freely overtype it (e.g. with "SSB") there.
        public string CurrentMode => mode;

        // Replace the entire categoryWeight table (loaded from INI or set by Phase 4 UI).
        // Validates that all entries are present and DEFAULT is 0 (delegated to Ranker).
        public void ApplyCategoryWeights(Dictionary<CallCategory, int> weights)
        {
            if (!Ranker.ApplyCategoryWeights(weights)) return;
            if (debug) DebugOutput($"{Time()} ApplyCategoryWeights: {string.Join(", ", Ranker.categoryWeight.Select(kv => $"{kv.Key}={kv.Value}"))}");
            SortCalls();
        }

        // Apply calling priorities (loaded from INI or set by dialog).
        // Order determines Alt+N category preference; membership gates admission of DEFAULT and Alt+N.
        public void ApplyCallingPriorities(List<CallCategory> enabled)
        {
            Ranker.ApplyCallingPriorities(enabled);
        }

        // POTA, SOTA, and MANUAL_SEL are hidden from the Call Filters UI; they follow the Directed CQ entry.
        private bool IsCallingEnabled(CallCategory cat) => Ranker.IsCallingEnabled(cat);

        // Apply wanted callsign list (loaded from INI or set by Phase 5 UI).
        // Re-derives categories for any existing queue entries affected.
        public void ApplyWantedCalls(HashSet<string> wanted)
        {
            wantedCalls = wanted ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Re-derive category for all queued calls so existing entries pick up changes.
            foreach (var kv in callDict)
            {
                var d = kv.Value;
                if (d.Category == CallCategory.ALWAYS_WANTED || d.Priority == (int)CallPriority.DEFAULT)
                {
                    d.Category = _awardTagger.DeriveCategory(d);
                    Ranker.SetRank(d, debug ? (Action<string>)(s => DebugOutput($"{spacer}{s}")) : null);
                }
            }
            SortCalls();
        }

        // Apply DX Spot Watch callsign list (loaded from INI or set by Options UI). Unlike
        // ApplyWantedCalls, this list never affects call-queue ranking/category, so there is
        // nothing here to re-derive -- Controller.ApplyAndSaveSpotWatchCalls is what also kicks
        // DxSpotWatcher to update its MQTT subscriptions.
        public void ApplySpotWatchCalls(HashSet<string> watched)
        {
            spotWatchCalls = watched ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private enum Periods
        {
            UNK,
            ODD,
            EVEN
        }
        private Periods period;

        private enum autoFreqPauseModes
        {
            DISABLED,
            ENABLED,
            ACTIVE
        }
        private autoFreqPauseModes autoFreqPauseMode;
        private autoFreqPauseModes lastAutoFreqPauseModeDebug = autoFreqPauseModes.DISABLED;

        public enum ListenModeTxPeriods
        {
            EVEN,
            ODD,
            ANY
        }

        public enum NewCallBands
        {
            ANY,
            CURRENT
        }

        public enum WsjtxResultCodes
        {
            NONE,
            LOTW_UPL,
            PWR_SWR_RPT,
            PWR_SWR_END,
            PWR_SWR_SINGLE_RPT
        }

        public WsjtxClient(Controller c, IPAddress reqIpAddress, int reqPort, bool reqMulticast, bool reqOverrideUdpDetect, bool reqDebug, bool reqLog, WsjtxClient.TxModes tMode)
        {
            ctrl = c;           //used for accessing/updating UI
            StatusView = c;
            QueueView = c;
            LogView = c;
            _potaLog = new PotaLogTracker(this);
            _awardTagger = new AwardTagger(this);
            _callQueueStore = new CallQueueStore(this);
            Sounds = new NotificationSounds(() => ctrl.soundsEnabled);
            LiveQsoUploader = new LiveQsoUploadOrchestrator(
                credentials: () => new LiveUploadCredentials
                {
                    QrzUploadEnabled = ctrl.qrzUploadEnabled,
                    QrzUploadRealtime = ctrl.qrzUploadRealtime,
                    QrzLogbookApiKey = ctrl.qrzLogbookApiKey,
                    ClubLogUploadEnabled = ctrl.clubLogUploadEnabled,
                    ClubLogUploadRealtime = ctrl.clubLogUploadRealtime,
                    ClubLogUploadEmail = ctrl.clubLogUploadEmail,
                    ClubLogUploadPassword = ctrl.clubLogUploadPassword,
                    ClubLogUploadCallsign = ctrl.clubLogUploadCallsign,
                    HrdLogUploadEnabled = ctrl.hrdLogUploadEnabled,
                    HrdLogUploadRealtime = ctrl.hrdLogUploadRealtime,
                    HrdLogUploadCode = ctrl.hrdLogUploadCode,
                    HrdLogUploadCallsign = ctrl.hrdLogUploadCallsign,
                },
                notifyImported: () => ctrl.BeginInvoke(new Action(() =>
                {
                    ctrl.RefreshStillNeedCache();
                    ctrl.RefreshLogbookWindowIfOpen();
                })),
                debugLog: msg => DebugOutput($"{Time()} {msg}"),
                showStatus: (msg, sound) => ctrl.BeginInvoke(new Action(() => ctrl.ShowUploadStatus(msg, sound))),
                resolveUsState: call => lookupManager?.Build(call)?.State);
            ipAddress = reqIpAddress;
            port = reqPort;
            multicast = reqMulticast;
            overrideUdpDetect = reqOverrideUdpDetect;
            txMode = tMode;
            //major.minor.build.private
            string allVer = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).FileVersion;
            Version v;
            Version.TryParse(allVer, out v);
            string fileVer = $"{v.Major}.{v.Minor}";
            pgmVer = WsjtxMessage.PgmVersion = fileVer;
            debug = reqDebug;
            opMode = OpModes.IDLE;              //wait for WSJT-X running to read its .INI
            WsjtxMessage.NegoState = WsjtxMessage.NegoStates.INITIAL;
            ctrl.Text = pgmName = Assembly.GetExecutingAssembly().GetName().Name.ToString();

            if (reqLog)            //request log file open
            {
                diagLog = SetLogFileState(true);
                if (diagLog)
                {
                    DebugOutput($"{nl}{nl}{nl}{DateTime.UtcNow.ToString("yyyy-MM-dd HHmmss")} UTC ###################### {pgmName} v{pgmVer} starting....");
                }
            }

            ClearAudioOffsets();
            if (ctrl.freqCheckBox.Checked) WsjtxSettingChanged();

            ResetNego();
            UpdateDebug();

            DebugOutput($"{Time()} NegoState:{WsjtxMessage.NegoState}");
            DebugOutput($"{Time()} opMode:{opMode}");
            DebugOutput($"{Time()} Waiting for WSJT-X to run...");

            ShowStatus();
            ShowQueue();
            if (ctrl.advancedCallLayout) ShowAdvancedQueue(null);
            ShowLogged();
            messageRecd = false;
            recvStarted = false;

            ctrl.verLabel.Text = $"by KB0UZT v{fileVer}";
            ctrl.verLabel2.Text = "Check for update";

            UpdateModeSelection();
            UpdateModeVisible();

            emsg = new EnableTxMessage();
            emsg.Id = WsjtxMessage.UniqueId;
            _compatExtension = new WsjtxCompatibilityExtension(_protocolAdapter);

            firstDecodeTime = DateTime.MinValue;

            postDecodeTimer.Interval = 4000;
            postDecodeTimer.Tick += new System.EventHandler(ProcessPostDecodeTimerTick);

            processDecodeTimer.Tick += new System.EventHandler(ProcessDecodeTimerTick);

            processDecodeTimer2.Tick += new System.EventHandler(ProcessDecodeTimer2Tick);

            statusTimer.Tick += new System.EventHandler(StatusTimerTick);

            statusTimer2.Tick += new System.EventHandler(StatusTimer2Tick);       //restore previous status
            statusTimer2.Interval = 5000;

            cmdCheckTimer.Tick += new System.EventHandler(capabilityProbeTimer_Tick);

            dialogTimer2.Tick += new System.EventHandler(dialogTimer2_Tick);
            dialogTimer2.Interval = 20;

            dialogTimer3.Tick += new System.EventHandler(dialogTimer3_Tick);
            dialogTimer3.Interval = 20;

            _potaLog.Read();

            heartbeatRecdTimer.Interval = 4 * heartbeatInterval * 1000;            //heartbeats every 15 sec
            heartbeatRecdTimer.Tick += new System.EventHandler(HeartbeatNotRecd);

            UpdateMaxTxRepeat();

            var dtNow = DateTime.UtcNow;

            UpdateBandComboBox();

            ShowLogged();

            UpdateBlockList(ctrl.exceptTextBox.Text);

            modePrompt = (txMode == TxModes.CALL_CQ);

            UpdateDebug();          //last before starting loop
        }

        public void CancelQso()
        {
            SetCallInProg(null);
            xmitCycleCount = 0;
            txTimeout = false;
            timedOutCall = null;
            ctrl.holdCheckBox.Checked = false;
        }

        // Must be called BEFORE CancelQso() so callInProg/replyDecode are still valid.
        // In advanced layout, the TX1/TX2 snapshot is not cleared by RemoveCall, so the row
        // remains visible after ReplyTo dequeues it.  Re-adding the call keeps the row backed
        // by the real queue so Enter works again.  No-op if not in advanced layout, call
        // already gone, or call is already in the queue.
        public void RequeueAbortedCall()
        {
            if (!ctrl.advancedCallLayout) return;
            if (callInProg == null || replyDecode == null) return;
            if (FindCallIndexInQueue(callInProg) >= 0) return;
            SetRank(replyDecode);
            DebugOutput($"{Time()} RequeueAbortedCall: re-enqueuing '{callInProg}'");
            _callQueueStore.AddCall(callInProg, replyDecode);
        }

        public bool EnableMode()              //cq/listen mode selected
        {
            HaltTuning();
            if (txMode == TxModes.CALL_CQ && !txEnabled)
            {
                DisableAutoFreqPause();
                consecNoDecodes = 0;

                if (callInProg != null)
                {
                    txTimeout = false;
                    xmitCycleCount = 0;
                    timedOutCall = null;
                    ClearCallTimeout(callInProg);
                    EnableTx();
                }
                else
                {
                    return false;
                }

                CancelDiscardCall();
                cqPaused = false;
                txEnableChanged = true;
                StartStatusTimer();
                Sounds.PlaySoundEvent(ctrl.soundEnabled_TxEnabled, ctrl.soundFile_TxEnabled);
                DebugOutput($"{Time()} EnableMode cqPaused:{cqPaused} txMode:{txMode} txTimeout:{txTimeout} xmitCycleCount:{xmitCycleCount}");
                UpdateDebug();
                return true;
            }

            if ((txMode == TxModes.LISTEN) && !txEnabled && callInProg != null) //listen mode, disabled or timed out
            {
                DisableAutoFreqPause();
                consecNoDecodes = 0;
                newSelection = true;
                txTimeout = false;
                xmitCycleCount = 0;
                timedOutCall = null;
                ClearCallTimeout(callInProg);
                EnableTx();
                cqPaused = false;
                modePrompt = true;
                txEnableChanged = true;
                StartStatusTimer();
                Sounds.PlaySoundEvent(ctrl.soundEnabled_TxEnabled, ctrl.soundFile_TxEnabled);
                StartDiscardCall(callInProg);
                DebugOutput($"{Time()} EnableMode txMode:{txMode} txTimeout:{txTimeout} xmitCycleCount:{xmitCycleCount}");
                UpdateDebug();
                return true;
            }

            return false;
        }

        // WSJT-X re-enabled its own "Enable Tx" button without Jimmy having asked for it --
        // most likely WSJT-X's own "Wait and Reply" feature resuming a stalled QSO after the
        // other station finally replied (see StatusMessage.TxEnableClk handling above). Mirrors
        // EnableMode()'s resume bookkeeping for the stalled callInProg, but skips re-sending
        // EnableTx() since WSJT-X already enabled itself -- and announces a distinct status
        // message so the operator can tell this was automatic, not their own action.
        private void HandleUnsolicitedTxResume()
        {
            if (callInProg == null) return;      //nothing Jimmy was waiting on to resume

            txTimeout = false;
            xmitCycleCount = 0;
            timedOutCall = null;
            ClearCallTimeout(callInProg);
            txEnabled = true;         //sync belief to match WSJT-X's actual state (no EnableTx() resend needed)
            cqPaused = false;
            UpdateMaxTxRepeat();
            StartStatusTimer();
            Sounds.PlaySoundEvent(ctrl.soundEnabled_TxEnabled, ctrl.soundFile_TxEnabled);
            StatusView.ShowMessage($"WSJT-X resumed calling {callInProg} automatically", false);
            DebugOutput($"{Time()} HandleUnsolicitedTxResume, callInProg:'{callInProg}' cqPaused:{cqPaused} txMode:{txMode}");
        }

        public void RankMethodIdxChanged(int idx)
        {
            Ranker.RankMethodIdxChanged(idx);
            DebugOutput($"{nl}{Time()} ApplySortOrder, order:{string.Join(",", Ranker.rankOrderList)} beam:{Ranker.rankBeamMethod?.ToString() ?? "none"}");
            SortCalls();
        }

        public void ApplySortOrder(List<RankMethods> orderList, RankMethods? beamMethod)
        {
            Ranker.ApplySortOrder(orderList, beamMethod);
            DebugOutput($"{nl}{Time()} ApplySortOrder, order:{string.Join(",", Ranker.rankOrderList)} beam:{Ranker.rankBeamMethod?.ToString() ?? "none"}");
            SortCalls();
        }

        private bool IsPrimarySort(RankMethods method) => Ranker.IsPrimarySort(method);

        internal int CompareRank(EnqueueDecodeMessage existing, EnqueueDecodeMessage incoming) =>
            Ranker.CompareRank(existing, incoming, lookupManager != null ? (Func<string, bool>)lookupManager.IsLoTWUser : null, lotwBoostEnabled);

        public void ReplyRR73Changed(bool state)
        {
            var calls = new List<string>() { };
            if (!state)     //'reply to RR73' unchecked
            {
                //remove any RR73 messages in call queue
                foreach (var entry in callDict)
                {
                    if (entry.Value.IsRR73()) calls.Add(entry.Key);
                }
            }

            foreach (var call in calls)
            {
                _callQueueStore.RemoveCall(call);
            }
        }

        public void TxPeriodIdxChanged(int idx)
        {
            if (callQueue.Count == 0 || (txMode == TxModes.LISTEN && idx == (int)ListenModeTxPeriods.ANY)) return;
            if (ctrl.advancedCallLayout) return;

            CheckCallQueuePeriod((ListenModeTxPeriods)idx == ListenModeTxPeriods.EVEN);
        }

        public bool ConnectedToWsjtx()
        {
            return opMode == OpModes.ACTIVE;
        }

        public bool WsjtxConnecting()
        {
            return opMode  >= OpModes.START;
        }

        public void AutoFreqChanged(bool autoFreqEnabled, bool bandOrModeChanged)
        {
            DisableAutoFreqPause();
            if (autoFreqEnabled)
            {
                if (opMode != OpModes.ACTIVE)
                {
                    ctrl.freqCheckBox.Text = "Use best freq (pending)";
                    ctrl.freqCheckBox.ForeColor = Color.DarkGreen;
                    return;
                }
                if (oddOffset > 0 && evenOffset > 0)
                {
                    ctrl.freqCheckBox.Text = "Use best Tx frequency";
                    return;
                }

                ctrl.freqCheckBox.Text = "Use best freq (pending)";
                ctrl.freqCheckBox.ForeColor = Color.DarkGreen;

                cqPaused = true;
                StopDecodeTimers();
                DisableTx(false);
                opMode = OpModes.START;
                UpdateBandComboBox();
                if (bandOrModeChanged)
                {
                    txTimeout = false;
                    tCall = null;
                    replyCmd = null;
                    curCmd = null;
                    replyDecode = null;
                    newDirCq = false;
                    dxCall = null;
                    xmitCycleCount = 0;
                    timedOutCall = null;
                    SetCallInProg(null);
                    UpdateCallInProg();
                }
                UpdateModeVisible();
                UpdateModeSelection();
                DebugOutput($"{Time()} [BAND-AUDIT] AutoFreqChanged enabled:true, bandIdx:{bandIdx} bandOrModeChanged:{bandOrModeChanged} evenOffset:{evenOffset} oddOffset:{oddOffset} opMode:{opMode} NegoState:{WsjtxMessage.NegoState} cqPaused:{cqPaused}");
            }
            else
            {
                ctrl.freqCheckBox.Text = "Use best Tx frequency";
                ctrl.freqCheckBox.ForeColor = Color.Black;
                DebugOutput($"{Time()} [BAND-AUDIT] AutoFreqChanged enabled:false, bandIdx:{bandIdx}");
                CheckActive();
            }
            UpdateDebug();
        }

        public bool ToggleHoldCheckBox()
        {
            HaltTuning();
            if (callInProg == null) return false;

            ctrl.holdCheckBox.Checked = !ctrl.holdCheckBox.Checked;
            if (ctrl.holdCheckBox.Checked) xmitCycleCount = 0;
            return HoldCheckBoxChanged();
        }

        public bool ToggleOperatingMode()
        {
            string newMode = mode != "FT8" ? "FT8" : "FT4";
            SetOperatingMode(newMode);
            return true;
        }

        public void UpdateCallInProg()
        {
            //placeholder
        }

        public void UpdateCallListAccessibleName(bool force = false)
        {
            if (!force && lastCallListTxFirst == txFirst) return;
            lastCallListTxFirst = txFirst;
            string rxLabel = txFirst ? "RX2" : "RX1";
            ctrl.callListBox.AccessibleName = $"{rxLabel} Stations Available List Box";

            // Update advanced list labels to reflect the active transmit side.
            // txFirst=true  → user transmits on TX1 (even); TX2 is the receive side → label TX2 as RX2.
            // txFirst=false → user transmits on TX2 (odd);  TX1 is the receive side → label TX1 as RX1.
            if (txFirst)
            {
                ctrl.advTx1Label.Text             = "TX1 available stations:";
                ctrl.advTx1ListBox.AccessibleName = $"TX1 available stations, {_tx1SnapshotRows.Count} calls";
                ctrl.advTx2Label.Text             = "RX2 available stations:";
                ctrl.advTx2ListBox.AccessibleName = $"RX2 available stations, {_tx2SnapshotRows.Count} calls";
            }
            else
            {
                ctrl.advTx1Label.Text             = "RX1 available stations:";
                ctrl.advTx1ListBox.AccessibleName = $"RX1 available stations, {_tx1SnapshotRows.Count} calls";
                ctrl.advTx2Label.Text             = "TX2 available stations:";
                ctrl.advTx2ListBox.AccessibleName = $"TX2 available stations, {_tx2SnapshotRows.Count} calls";
            }
        }

        public void WsjtxSettingChanged()
        {
            settingChanged = true;
            newDirCq = true;
        }

        public string SpacifyMyCall()
        {
            return Spacify(myCall);
        }

        public void Pause(bool haltTx, bool showStatus)         //go to pause mode, optionally halt Tx
        {
            consecNoDecodes = 0;
            if (haltTx || tuning) HaltTx();

            StopDecodeTimers();
            DisableAutoFreqPause();
            ctrl.holdCheckBox.Checked = false;

            if (txMode == TxModes.CALL_CQ) cqPaused = true;
            DebugOutput($"{spacer}cqPaused:{cqPaused} txEnabled:{txEnabled}");

            txEnableChanged = showStatus;
            if (showStatus && !transmitting) StartStatusTimer();
            modePrompt = true;
            UpdateMaxTxRepeat();
            UpdateDebug();
        }

        public void UpdateModeVisible()
        {
            // modeGroupBox (the 4-choice Listen/CQ-only/CQ-DX-only/CQ-and-DX radio group) is
            // superseded by the Call CQ button + dialog -- kept in the Designer only because
            // its radio buttons still back SyncCqSubtypeRadio()/cqIntentXxx_Click(), but it must
            // never actually be shown on screen anymore.
            ctrl.callCqOptionsButton.Visible = opMode == OpModes.ACTIVE;
            if (opMode == OpModes.ACTIVE)
            {
                ctrl.listenModeButton.Visible = true;
                ctrl.cqModeButton.Visible = true;
            }
            else
            {
                ctrl.listenModeButton.Visible = false;
                ctrl.cqModeButton.Visible = false;
            }
            ctrl.modeGroupBox.Visible = false;

            UpdateListenModeTxPeriod();
            DebugOutput($"{spacer}UpdateModeVisible, txMode:{txMode}");
        }

        private void ProcessDecodeMsg(EnqueueDecodeMessage dmsg, bool isSpecOp)
        {
            if (dmsg.AutoGen && (dmsg.DeltaFrequency > offsetLoLimit && dmsg.DeltaFrequency < offsetHiLimit)) audioOffsets.Add(dmsg.DeltaFrequency);
            timeOffsets.Add(dmsg.DeltaTime);

            decodeCount++;
            consecNoDecodes = 0;

            if (opMode != OpModes.ACTIVE)
            {
                DebugOutput($"{spacer}ProcessDecodeMsg: skipped opMode:{opMode} msg:'{dmsg.Message}'");
                return;
            }

            if (dmsg.Message.Contains("..."))
            {
                DebugOutput($"{nl}{Time()}");
                DebugOutput($"{dmsg}{nl}{spacer}msg:'{dmsg.Message}'");
                DebugOutput($"{nl}{spacer}ProcessDecodeMsg(4), rejected: contains ...");
                return;
            }

            string deCall = dmsg.DeCall();
            string toCall = dmsg.ToCall();
            bool isContest = false;
            bool isInvalidType = false;

            if ((isContest = dmsg.IsContest()) || (isInvalidType = dmsg.IsInvalidType()) || deCall == null || toCall == null)
            {
                // Contest/FD-format message directed to my callsign — the caller must reach the waiting list
                if (isContest && toCall == myCall && deCall != null) { }
                else
                {
                    DebugOutput($"{nl}{Time()}");
                    DebugOutput($"{dmsg}{nl}{spacer}msg:'{dmsg.Message}'");
                    DebugOutput($"{spacer}ProcessDecodeMsg(3), rejected: isContest:{isContest} isInvalidType:{isInvalidType} deCall:'{deCall}' toCall:'{toCall}'");
                    return;
                }
            }

            bool toMyCall = dmsg.IsCallTo(myCall);
            dmsg.OffAir = true;     //default: play sound

            // Stage A6 (migration roadmap): compute Jimmy's own classification for this
            // decode once, here, before anything below reads a classification-derived
            // field. dmsg's own wire-supplied fields (IsDx/IsNewCallOnBand/etc.) are left
            // untouched -- every consumer downstream reads dmsg.EffectiveClassification()
            // instead (Classification/ClassificationCutover.cs), which returns this
            // computed value or falls back to the wire fields per the rollback flag.
            dmsg.Classified = Classifier.Classify(deCall, CurrentBandStr, dmsg.Message, myGrid, myContinent);
            // TEMPORARY developer diagnostic (Stage A6 field verification) -- see
            // Classification/ClassificationParityLogger.cs. No-ops unless explicitly
            // enabled via an undocumented .ini key.
            ClassificationParityLogger.CheckAndLog(dmsg, deCall, CurrentBandStr, myGrid, myContinent, lookupManager);
            ClassifiedCall classification = dmsg.EffectiveClassification();

            //do some processing not directly related to replying immediately
            //set initial priority
            dmsg.SequenceNumber = NextMsgSeqNum();
            dmsg.Priority = (int)CallPriority.DEFAULT;
            if (toMyCall) dmsg.Priority = (int)CallPriority.TO_MYCALL;       //as opposed to a decode from anyone else
            if (classification.IsNewCountryOnBand) dmsg.Priority = (int)CallPriority.NEW_COUNTRY_ON_BAND;
            if (classification.IsNewCountry) dmsg.Priority = (int)CallPriority.NEW_COUNTRY;

            // Upgrade directed-alert CQ calls to WANTED_CQ unconditionally — whether a CQ is
            // directed (e.g. CQ POTA) is a property of the message, not of the operator's filter
            // settings.  The Call Filters decide whether the classified call is admitted; the
            // classification itself must not depend on filter state.
            if (dmsg.Priority == (int)CallPriority.DEFAULT && dmsg.IsCQ())
            {
                string directedTo = WsjtxMessage.DirectedTo(dmsg.Message);
                if (IsDirectedAlert(directedTo, classification.IsDx))
                    dmsg.Priority = (int)CallPriority.WANTED_CQ;
            }

            dmsg.Category = _awardTagger.DeriveCategory(dmsg);   //after Priority set; before SetRank
            _awardTagger.CheckAwardAlert(dmsg);   // independent of Category/Call Filters admission -- see method comment
            SetRank(dmsg);      //only after priority set

            UpdateCallQueue(deCall, dmsg);      //newest call from station replaces older in call queue, so quality is kept current for sorting

            //detect previous signoff before adding call to allCallDict
            bool recdPrevSignoff = RecdSignoff(deCall);

            //if caller has not already signed off (with 73 or RR73) or already logged:
            //current msg (not to myCall) might be replaceable by a previous msg (to myCall)
            //rec'd later in the QSO cycle than this msg;
            //this is the case when a caller stops calling myCall
            /*//and starts CQing again or is new country replying to another caller
            if (!toMyCall && dmsg.AutoGen && !logList.Contains(deCall) && !recdPrevSignoff && (dmsg.IsCQ() || dmsg.IsNewCountryOnBand))
            {
                List<EnqueueDecodeMessage> msgList;
                if (allCallDict.TryGetValue(deCall, out msgList))
                {
                    //find latest msg from deCall to myCall
                    EnqueueDecodeMessage rmsg;
                    if ((rmsg = msgList.FindLast(RogerReport)) != null || (rmsg = msgList.FindLast(Report)) != null || (rmsg = msgList.FindLast(Reply)) != null)
                    {
                        if (rmsg.DeCall() != null && rmsg.ToCall() != null)     //sanity check
                        {
                            DebugOutput($"{Time()}");
                            DebugOutput($"{dmsg}{nl}{spacer}msg:'{dmsg.Message}'");
                            DebugOutput($"{spacer}ProcessDecodeMsg(2), substitute msg found:");
                            DebugOutput($"{rmsg}{nl}{spacer}msg:'{rmsg.Message}'");
                            dmsg.Message = rmsg.Message;
                            dmsg.OffAir = false;        //flag no sound, no save
                            toMyCall = true;
                        }
                    }
                }
            }*/

            if (toMyCall && dmsg.AutoGen)
            {
                DebugOutput($"{nl}{Time()}");
                DebugOutput($"{dmsg}{nl}{spacer}msg:'{dmsg.Message}' decodeCycle:{CurrentDecodeCycleString()} decodesProcessed:{decodesProcessed} cqPaused:{cqPaused}");
                DebugOutput($"{spacer}ProcessDecodeMsg(1), deCall:'{deCall}' callInProg:'{CallPriorityString(callInProg)}' recdPrevSignoff:{recdPrevSignoff}");
                DebugOutput($"{spacer}txEnabled:{txEnabled} transmitting:{transmitting} restartQueue:{restartQueue} RecdAnyMsg:{RecdAnyMsg(deCall)}");

                consecTimeoutCount = 0;

                if (deCall == discardCall)      //reset count of periods with no call from discardCall
                {
                    discardCallCycleCount = 0;
                    DebugOutput($"{spacer}discardCall:'{discardCall}' discardCallCycleCount:{discardCallCycleCount}");
                }

                int prevTo = 0;
                bool tmpBlock = false;
                bool isPota = IsPotaCall(dmsg);
                int maxTo = MaxTimeoutsForMsg(isPota);
                timeoutCallDict.TryGetValue(deCall, out prevTo);
                DebugOutput($"{spacer}prevTo:{prevTo} maxTo:{maxTo}");
                if (!dmsg.Is73orRR73() && !dmsg.IsRogers() && prevTo >= maxTo)        //trouble finishing signal report(s)
                {
                    StatusView.ShowMessage($"Blocking {deCall} temporarily...", false);
                    DebugOutput($"{spacer}ignoring call, prevTo:{prevTo} restartQueue:{restartQueue}");
                    tmpBlock = true;
                }

                CheckLateLog(deCall, dmsg);
                UpdateDebug();

                //if call not already logged: save Report (...+03) and RogerReport (...R-02) decodes for out-of-order call processing
                //except save RR73 and 73 for later recdPrevSignoff check, also needed for status display
                //don't save if a substituted message
                if ((!logList.Contains(deCall) || dmsg.Is73orRR73()) && dmsg.OffAir)
                {
                    AddAllCallDict(deCall, dmsg);
                }

                if (IsBlocked(deCall) || tmpBlock)
                {
                    StatusView.ShowMessage($"{deCall} is blocked)", false);
                    if (debugDetail) DebugOutput($"{spacer}{deCall} ignored, blocked");
                    return;
                }

                // Weak-signal floor: never suppress the station we're actively working —
                // SNR can dip on a final RR73/73 and we must not drop a QSO in progress.
                if (ctrl.ignoreWeakSnrCheckBox.Checked && dmsg.Snr <= (int)ctrl.minSnrNumUpDown.Value && deCall != callInProg)
                {
                    if (debugDetail) DebugOutput($"{spacer}{deCall} ignored, weak signal snr:{dmsg.Snr} floor:{(int)ctrl.minSnrNumUpDown.Value}");
                    // Default: just stop refreshing this station's queue entry -- it lingers
                    // (with its last good SNR still shown) until the separate call-queue age
                    // timeout eventually prunes it. Opt-in: pull it immediately instead, since
                    // its already-queued entry no longer reflects a signal that meets the floor.
                    if (ctrl.removeOnWeakSnrCheckBox.Checked && callQueue.Contains(deCall))
                        _callQueueStore.RemoveCall(deCall);
                    return;
                }

                DebugOutput($"{spacer}deCall:{deCall} dmsg.Priority:{dmsg.Priority} callQueue.Contains:{callQueue.Contains(deCall)} SentAnyMsg:{SentAnyMsg(deCall)}");
                //if calling CQ DX and ignore non-DX replies
                if (!classification.IsDx
                    && dmsg.Priority > (int)CallPriority.NEW_COUNTRY_ON_BAND
                    && txMode == TxModes.CALL_CQ
                    && ctrl.callCqDxCheckBox.Checked
                    && ctrl.ignoreNonDxCheckBox.Checked
                    && !callQueue.Contains(deCall)
                    && !SentAnyMsg(deCall)
                    && !dmsg.Is73orRR73()
                    )
                {
                    StatusView.ShowMessage($"{deCall} ignored (not DX)", false);
                    DebugOutput($"{spacer}{deCall} ignored, DX only");
                    return;
                }

                consecTxCount = 0;          //reset Tx hold count since we're being heard
                DebugOutput($"{spacer}consecTxCount:0");

                bool isCorrectTimePeriod = IsCorrectTimePeriodForMode(dmsg);
                DebugOutput($"{spacer}isCorrectTimePeriod:{isCorrectTimePeriod}");

                // IsRogers() (bare "RRR") is included here alongside 73/RR73: per FT8/FT4
                // protocol, RRR just means "all received" -- it isn't itself a sign-off, so a
                // station can legitimately keep repeating it (e.g. auto-repeating because they
                // haven't yet decoded our final 73). But once we've already logged this call
                // this session/band, a repeat RRR is never a new contact opportunity -- it must
                // not silently re-add them to the queue as if unworked (see project notes,
                // 2026-07-07: AC7WY reappearing in the list after being logged).
                bool ignore = (dmsg.Is73() || dmsg.IsRogers() || (dmsg.IsRR73() && !ctrl.replyRR73CheckBox.Checked)) && logList.Contains(deCall);
                if (ignore)
                {
                    finalSignoffCall = deCall;
                    ShowStatus();
                }

                if (!txEnabled && deCall != null && !dmsg.Is73orRR73() && !ignore)
                {
                    if (!callQueue.Contains(deCall))
                    {
                        if (isCorrectTimePeriod)
                        {
                            DebugOutput($"{spacer}'{deCall}' not in queue");
                            _callQueueStore.AddCall(deCall, dmsg);

                            //check for call after decodes "done"
                            if (decodesProcessed && !cqPaused)
                            {
                                DebugOutput($"{spacer}late decode(1), restartQueue:{restartQueue}");
                                StartProcessDecodeTimer2();
                            }
                            if (!_lastAddCallCategoryPlayed) Sounds.PlaySoundEvent(ctrl.callAddedCheckBox.Checked, ctrl.soundFile_CallAdded);
                        }
                    }
                    else
                    {
                        DebugOutput($"{spacer}'{deCall}' already in queue");
                        _callQueueStore.UpdateCall(deCall, dmsg);
                    }
                    UpdateDebug();
                }

                //decode processing of calls to myCall requires txEnabled
                if (txEnabled && deCall != null)
                {
                    DebugOutput($"{spacer}'{deCall}' is to {myCall}");
                    if ((deCall == callInProg || (txTimeout && deCall == tCall)) && recdPrevSignoff)        //cancel call in progress
                    {
                        restartQueue = true;
                        DebugOutput($"{spacer}already rec'd signoff, restartQueue:{restartQueue} qsoState:{qsoState}");
                    }
                    else
                    {
                        if (!dmsg.Is73orRR73() && !ignore)       //not a 73 or RR73 (or an already-logged repeat signoff)
                        {
                            DebugOutput($"{spacer}not a 73 or RR73");
                            if (deCall != callInProg)
                            {
                                DebugOutput($"{spacer}{deCall} is not callInProg:{CallPriorityString(callInProg)}");
                                if (!callQueue.Contains(deCall))        //call not in queue, enqueue the call data
                                {
                                    if (isCorrectTimePeriod)
                                    {
                                        DebugOutput($"{spacer}'{deCall}' not already in queue");
                                        _callQueueStore.AddCall(deCall, dmsg);

                                        //check for high-priority call after decodes "done"
                                        DebugOutput($"{spacer}transmitting:{transmitting} qsoState:{qsoState}");
                                        if (decodesProcessed && !cqPaused)
                                        {
                                            DebugOutput($"{spacer}late decode(2), restartQueue:{restartQueue}");
                                            StartProcessDecodeTimer2();
                                        }
                                        if (!_lastAddCallCategoryPlayed) Sounds.PlaySoundEvent(ctrl.callAddedCheckBox.Checked, ctrl.soundFile_CallAdded);
                                    }

                                }
                                else       //call is already in queue, update the call data
                                {
                                    DebugOutput($"{spacer}'{deCall}' already in queue");
                                    _callQueueStore.UpdateCall(deCall, dmsg);
                                }
                            }
                            else        //call is in progress
                            {
                                DebugOutput($"{spacer}{CallPriorityString(deCall)} is callInProg, txTimeout:{txTimeout} cancelledCall:'{cancelledCall}' isSpecOp:{isSpecOp}");

                                if (isCorrectTimePeriod)
                                {
                                    _callQueueStore.AddCall(deCall, dmsg);

                                    if ((txEnabled && txMode == TxModes.LISTEN) || (!cqPaused && txMode == TxModes.CALL_CQ))
                                    {
                                        replyFromInProg = true;       //special status shown when callInProg will be replied to
                                        DebugOutput($"{spacer}replyFromInProg:{replyFromInProg}");
                                        ShowStatus();
                                    }

                                    //check for call after decodes "done"
                                    if (decodesProcessed && !cqPaused)
                                    {
                                        DebugOutput($"{spacer}late decode(3), restartQueue:{restartQueue}");
                                        StartProcessDecodeTimer2();
                                    }
                                    if (!_lastAddCallCategoryPlayed && deCall != callInProg) Sounds.PlaySoundEvent(ctrl.callAddedCheckBox.Checked, ctrl.soundFile_CallAdded);
                                }
                            }
                        }
                        else        //decode is 73 or RR73 msg
                        {
                            DebugOutput($"{spacer}decode is 73 or RR73, IsRR73:{dmsg.IsRR73()} isSpecOp:{isSpecOp} checked:{ctrl.replyRR73CheckBox.Checked} priority:{Priority(deCall)} contains:{logList.Contains(deCall)}");
                            if (deCall == callInProg)       ///check for ignore 73 or RR73
                            {
                                //                 WSJT-X will not reply automatically to RR73 for F/H msg
                                if (dmsg.Is73() || (dmsg.IsRR73() && isSpecOp) || !logList.Contains(deCall) || (!ctrl.replyRR73CheckBox.Checked && Priority(deCall) > (int)CallPriority.NEW_COUNTRY_ON_BAND))     //if new country, RR73 gets a 73 reply
                                {
                                    if (dmsg.IsRR73()) DebugOutput($"{spacer}WSJT-X not replying to RR73");
                                    restartQueue = true;
                                    DebugOutput($"{spacer}call is in progress, restartQueue:{restartQueue}");
                                    if ((decodesProcessed && !cqPaused) || transmitting)
                                    {
                                        //prevent calling CQ when not wanted
                                        DebugOutput($"{spacer}late decode (RR)73, restartQueue:{restartQueue}");
                                        //StartProcessDecodeTimer2();
                                    }
                                }
                                else        //allow WSJT-X to reply automatically to RR73
                                {
                                    DebugOutput($"{spacer}WSJT-X is replying to RR73");
                                    AddTimeoutCall(deCall);
                                }
                            }
                            else        //deCall is not call in progress
                            {
                                //check for required reply to RR73
                                if (isCorrectTimePeriod && logList.Contains(deCall) && dmsg.IsRR73() && (ctrl.replyRR73CheckBox.Checked || Priority(deCall) <= (int)CallPriority.NEW_COUNTRY_ON_BAND))        //call not in queue, enqueue the call data
                                {
                                    AddTimeoutCall(deCall);
                                    //allow RR73 to be processed
                                    if (!callQueue.Contains(deCall))
                                    {
                                        DebugOutput($"{spacer}'{deCall}' not already in queue");
                                        _callQueueStore.AddCall(deCall, dmsg);
                                        if (!_lastAddCallCategoryPlayed) Sounds.PlaySoundEvent(ctrl.callAddedCheckBox.Checked, ctrl.soundFile_CallAdded);
                                    }
                                    else
                                    {
                                        _callQueueStore.UpdateCall(deCall, dmsg);
                                    }
                                }
                                else //don't process the 73 or RR73
                                {
                                    _callQueueStore.RemoveCall(deCall);     //may have been added manually
                                }
                            }
                        }
                    }
                    UpdateDebug();
                }
            }
            else    //not toMyCall or is not auto-generated
            {
                //only resulting action is to add call to callQueue, optionally restart queue
                AddSelectedCall(dmsg);              //known to be "new" and not "replay
                UpdateDebug();
            }

            return;
        }

        private bool CheckActive()
        {
            //*****************************************
            //check for transition from START to ACTIVE
            //****************************************
            // Stage A7: commConfirmed (the non-standard cmd:7 echo) is no longer
            // required here -- see the same note on the IDLE->START gate in
            // WsjtxClient.Protocol.cs.
            if (myCall != null && supportedModes.Contains(mode) && specOp == 0 && opMode == OpModes.START && (!ctrl.freqCheckBox.Checked || !_requireOffsetForActive || (oddOffset > 0 || evenOffset > 0)))
            {
                opMode = OpModes.ACTIVE;
                if (txMode == TxModes.LISTEN)
                {
                    Pause(true, false);
                }

                UpdateModeVisible();
                UpdateBandComboBox();
                UpdateCallListAccessibleName();
                ctrl.LoadHrcCache();    //refresh HRC sets (band-independent; harmless to re-run here)
                ctrl.RefreshStillNeedCache();    //reload Still Need live-tag cache now that the current band is known
                ctrl.OnJimmyReachedActive();    //kicks off automatic logbook sync, once per session, after a short delay
                DebugOutput($"{Time()} opMode START -> ACTIVE");
                UpdateDebug();
                return true;
            }
            return false;
        }

        private void StartProcessDecodeTimer()
        {
            DateTime dtNow = DateTime.UtcNow;
            int diffMsec = ((dtNow.Second * 1000) + dtNow.Millisecond) % (int)trPeriod;
            int cycleTimerAdj = CalcTimerAdj();
            processDecodeTimer.Interval = (2 * (int)trPeriod) - diffMsec - cycleTimerAdj;
            processDecodeTimer.Start();
            DebugOutput($"{Time()} processDecodeTimer start: interval:{processDecodeTimer.Interval} msec");
        }

        private bool CheckMyCall(StatusMessage smsg)
        {
            if (smsg.DeCall == null || smsg.DeGrid == null || smsg.DeGrid.Length < 4)
            {
                heartbeatRecdTimer.Stop();
                suspendComm = true;
                ctrl.BringToFront();
                MessageBox.Show($"Call sign and Grid are not entered in WSJT-X.{nl}{nl}Enter these in WSJT-X:{nl}- Select 'File | Settings' then the 'General' tab.{nl}{nl}(Grid must be at least 4 characters){nl}{nl}{pgmName} will try again when you close this dialog.", pgmName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ResetOpMode();
                ShowStatus();
                suspendComm = false;
                return false;
            }

            if (myCall == null)
            {
                myCall = smsg.DeCall;
                myGrid = smsg.DeGrid;
                DebugOutput($"{spacer}CheckMyCall myCall:{myCall} myGrid:{myGrid}");
            }

            UpdateDebug();
            return true;
        }

        private void CheckNextXmit()
        {
            //can be called anytime, but will be called at least once per decode period shortly before the tx period begins;
            //can result in tx enabled (or disabled)
            DebugOutput($"{Time()} CheckNextXmit, txTimeout:{txTimeout} callQueue.Count:{callQueue.Count} qsoState:{qsoState}");
            DateTime dtNow = DateTime.UtcNow;      //helps with debugging to do this here

            //*******************
            //Best Tx freq update
            //*******************
            if (autoFreqPauseMode == autoFreqPauseModes.ENABLED)        //auto freq update started
            {
                DebugOutput($"{spacer}CheckNextXmit(4) start");
                autoFreqPauseMode = autoFreqPauseModes.ACTIVE;
                UpdateCallInProg();
                DebugOutput($"{spacer}auto freq update continue");
                DebugOutput($"{spacer}CheckNextXmit(4) end, autoFreqPauseMode:{autoFreqPauseMode}");
                return;
            }
            else if (autoFreqPauseMode == autoFreqPauseModes.ACTIVE)        //end auto freq update
            {
                DebugOutput($"{spacer}CheckNextXmit(5) start");
                DebugOutput($"{spacer}auto freq update end");
                DisableAutoFreqPause();
                if (txMode == TxModes.CALL_CQ || callInProg != null) EnableTx();
                DebugOutput($"{spacer}CheckNextXmit(5) end, autoFreqPauseMode:{autoFreqPauseMode}");
            }

            //********************
            //Tx state processing
            //********************
            //check for time to resume CQing mode,
            //or disable tx for listen mode
            if (txTimeout)        //important to sync qso logged to end of xmit, and manually-added call(s) to status msgs
            {
                DebugOutput($"{spacer}CheckNextXmit(1) start");
                DebugOutput($"{spacer}callQueue.Count:{callQueue.Count} ctrl.freqCheckBox.Checked:{ctrl.freqCheckBox.Checked} mode:'{mode}'");
                DebugOutput($"{_callQueueStore.CallQueueString()}");

                //start CQing (or if Listening: prepare for replying) 
                if (txMode == TxModes.LISTEN)
                {
                    Pause(true, false);
                    consecTxCount = 0;
                    DebugOutput($"{spacer}consecTxCount:0");
                }
                else        //CQ mode
                {
                    DebugOutput($"{spacer}no entries in queue, start CQing");
                    SetupCq(true);      //also sets WSJT-X "Tx Enable" button state
                }
                restartQueue = false;           //get ready for next decode phase
                txTimeout = false;              //ready for next timeout
                tCall = null;
                xmitCycleCount = 0;

                DebugOutputStatus();
                DebugOutput($"{spacer}CheckNextXmit(1) end, restartQueue:{restartQueue} txTimeout:{txTimeout}");
                UpdateDebug();      //unconditional
                return;             //don't process newDirCq
            }

            //*************************************
            //Directed CQ / new setting / best freq
            //*************************************
            if (txMode == TxModes.CALL_CQ && qsoState == WsjtxMessage.QsoStates.CALLING)
            {
                DebugOutput($"{spacer}CheckNextXmit(4) start");
                if (callInProg == null)
                {

                    DebugOutput($"{spacer}CheckNextXmit(2) start");
                    if (ctrl.freqCheckBox.Checked && oddOffset > 0 && evenOffset > 0)
                    {
                        //set/show frequency offset for period after decodes started
                        // Stage A4: build+send moved to WsjtxCompatibilityExtension.OptReq.
                        _compatExtension.OptReq(ctrl.skipGridCheckBox.Checked, ctrl.useRR73CheckBox.Checked, AudioOffsetFromTxPeriod(),
                            msg => DebugOutput($"{Time()} {msg}"));
                        if (settingChanged)
                        {
                            ctrl.WsjtxSettingConfirmed();
                            settingChanged = false;
                        }
                    }

                    if (newDirCq)
                    {
                        // Stage A4: build+send moved to WsjtxCompatibilityExtension.SetupCq.
                        string cqMsg = $"CQ{NextDirCq()} {myCall} {myGrid}";
                        _compatExtension.SetupCq(cqMsg, ctrl.skipGridCheckBox.Checked, ctrl.useRR73CheckBox.Checked,
                            msg => DebugOutput($"{Time()} {msg}"));
                        qsoState = WsjtxMessage.QsoStates.CALLING;      //in case enqueueing call manually right now
                        replyCmd = null;        //invalidate last reply cmd since not replying
                        replyDecode = null;
                        curCmd = cqMsg;
                        newDirCq = false;
                        DebugOutput($"{spacer}newDirCq:{newDirCq}");
                        if (settingChanged)
                        {
                            ctrl.WsjtxSettingConfirmed();
                            settingChanged = false;
                        }
                    }

                    UpdateWsjtxOptions();
                    DebugOutputStatus();
                    DebugOutput($"{spacer}CheckNextXmit(4) end");
                    UpdateDebug();      //unconditional
                    return;
                }
                else
                {
                    //LogBeep();
                    DebugOutput($"{spacer}CheckNextXmit(4) end, callInProg:{callInProg}");
                }
            }
        }

        private void ProcessDecodes()
        {
            //always called shortly before the tx period begins
            cancelledCall = null;
            UpdateMaxTxRepeat();
            int maxDiscardCount = maxTxRepeat + 2;     //count number of rx periods since msg from discardCall last rec'd
            DebugOutput($"{nl}{Time()} ProcessDecodes: restartQueue:{restartQueue} txTimeout:{txTimeout} txEnabled:{txEnabled}{nl}{spacer}txMode:{txMode} cqPaused:{cqPaused} txEnabled:{txEnabled}");
            DebugOutput($"{spacer}cancelledCall:{cancelledCall} autoFreqPauseMode:{autoFreqPauseMode} callInProg:'{callInProg}' discardCall:'{discardCall}' discardCallCycleCount:{discardCallCycleCount}");
            DebugOutput($"{spacer}maxDiscardCount:{maxDiscardCount} maxTxRepeat:{maxTxRepeat}");
            DebugOutputStatus();

            if (_callQueueStore.TrimCallQueue())
            {
                DebugOutput(_callQueueStore.CallQueueString());
            }

            if (debug)
            {
                DebugOutput(_callQueueStore.AllCallDictString());
                DebugOutput(_callQueueStore.SentCallListString());
                DebugOutput(_callQueueStore.LogListString());
                DebugOutput(_potaLog.DictString());
                DebugOutput(_callQueueStore.TimeoutCallDictString());
                //DebugOutput(_callQueueStore.ReportListString());
                DebugOutput(_callQueueStore.UnwantedCqListString());
            }

            if (discardCall != null && discardCall == callInProg && ++discardCallCycleCount >= maxDiscardCount) DiscardCall();

            if (restartQueue) 
            {
                txTimeout = true;       //important to only set this now, not during decode phase, since decodes can happen after Tx-starts
                SetCallInProg(null);    //not calling anyone (set this as late as possible to pick up possible reply to last Tx)
                DebugOutput($"{spacer}qsoState:{qsoState} txTimeout:{txTimeout} callInProg:'{CallPriorityString(callInProg)}'");
                UpdateDebug();
            }

            //check for call in progress with tx disabled
            if (!cqPaused && !txEnabled && callInProg != null && autoFreqPauseMode == autoFreqPauseModes.DISABLED)
            {
                DebugOutput($"{spacer}call in progress with tx disabled");
                //LogBeep();
                //EnableTx();
            }

            //check for auto freq update disabled while CQ mode previously in progress
            if (!cqPaused && !txEnabled && txMode == TxModes.CALL_CQ && autoFreqPauseMode == autoFreqPauseModes.DISABLED)
            {
                DebugOutput($"{spacer}auto freq update disabled while CQ mode previously in progress");
            }

            if ((((txTimeout || !txEnabled) && txMode == TxModes.LISTEN) || (!cqPaused && txMode == TxModes.CALL_CQ)) && callInProg != null && callQueue.Contains(callInProg))
            {
                DebugOutput($"{spacer}resume '{discardCall}' after timeout");
                DisableAutoFreqPause();
                CancelDiscardCall();            //no more retries
                timedOutCall = null;
                EnqueueDecodeMessage dummy;
                int idx = _callQueueStore.FindCall(callInProg, out dummy);
                if (idx >= 0)
                {
                    ReplyTo(idx);
                    replyFromInProg = true;
                    DebugOutput($"{spacer}resumed {callInProg}, replyFromInProg:{replyFromInProg} xmitCycleCount:{xmitCycleCount} timedOutCall:'{timedOutCall}'");
                    ShowStatus();
                }
            }
            //check for disable Tx in listen mode
            //or call timed out
            //or inhibit reply to 73/RR73
            //or process auto freq update enabled / in progress
            else if (!cqPaused && (autoFreqPauseMode != autoFreqPauseModes.DISABLED || txTimeout))
            {
                DebugOutput($"{spacer}check auto freq/disable tx");
                CheckNextXmit();        //can result in tx disabled)
            }
            else if (newDirCq)
            {
                CheckNextXmit();
            }
            else
            {
                UpdateWsjtxOptions();
            }
            var dtNow = DateTime.UtcNow;
            bool even = IsEvenPeriod((dtNow.Minute * 60) + dtNow.Second - 3);       //might be in start of transmit period when all decodes done
            EnqueueDecodeMessage dmsg;
            if (ctrl.soundsEnabled && even != txFirst && ctrl.callAddedCheckBox.Checked && callQueue.Count > 0 && _callQueueStore.PeekCall(0, out dmsg) != null)
            {
                if (dmsg.Quality >= (int)EnqueueDecodeMessage.Qualities.MEDIUM)
                {
                    Sounds.Play("chime.wav");
                }
                else
                {
                    DebugOutput($"{spacer}low quality msg:{dmsg.Message}");
                }
            }
            decodesProcessed = true;
            DebugOutput($"{Time()} ProcessDecodes done, decodesProcessed:{decodesProcessed}");
            UpdateDebug();
        }

        //check for time to log (best done at Tx-start to avoid any logging/dequeueing timing problem if done at Tx end)
        private void ProcessTxStart()
        {
            if (!ctrl.keepTransmitListDuringTx)
            {
                if (txFirst)   // TX1 transmitting → clear TX1
                {
                    _tx1SnapshotRows.Clear();
                    _tx1SnapshotCalls.Clear();
                    _tx1SnapshotCategories.Clear();
                    ctrl.advTx1ListBox.AccessibleName = "TX1 available stations, 0 calls";
                    UpdateListIfChanged(ctrl.advTx1ListBox, new List<string> { "No available stations" });
                }
                else           // TX2 transmitting → clear TX2
                {
                    _tx2SnapshotRows.Clear();
                    _tx2SnapshotCalls.Clear();
                    _tx2SnapshotCategories.Clear();
                    ctrl.advTx2ListBox.AccessibleName = "TX2 available stations, 0 calls";
                    UpdateListIfChanged(ctrl.advTx2ListBox, new List<string> { "No available stations" });
                }
            }

            string toCall = WsjtxMessage.ToCall(txMsg);
            curTxMsg = txMsg;       //the message displayed
            if (txMsg == "TUNE") tuning = true;
            lastStatusTxMsg = txMsg;     //status update for interrupted Tx not required
            string lastToCall = WsjtxMessage.ToCall(lastTxMsg);
            DebugOutput($"{nl}{Time()} WSJT-X event, Tx start: toCall:'{toCall}' lastToCall:'{lastToCall}' decodesProcessed:{decodesProcessed} processDecodeTimer interval:{processDecodeTimer.Interval} msec tuning:{tuning}");
            var dtNow = DateTime.UtcNow;
            SetTxStartInfo(dtNow, toCall);

            curTxPayload = null;

            if (toCall == null)
            {
                //WSJT-X replied to invalid message, process next msg
                txTimeout = true;
                SetCallInProg(null);       //call is expired now
                return;
            }

            DebugOutput($"{Time()} Tx start done: txMsg:'{txMsg}' lastTxMsg:'{lastTxMsg}' toCall:'{toCall}' lastToCall:'{lastToCall}'");
            UpdateCallInProg();
            _callQueueStore.RemoveCall(toCall);
            UpdateDebug();      //unconditional
        }

        //check for QSO end or timeout (and possibly logging (if txMsg changed between Tx start and Tx end)
        private void ProcessTxEnd()
        {
            string toCall = WsjtxMessage.ToCall(txMsg);
            string lastToCall = WsjtxMessage.ToCall(lastTxMsg);
            string deCall = WsjtxMessage.DeCall(replyCmd);
            string cmdToCall = WsjtxMessage.ToCall(curCmd);
            bool isCq = WsjtxMessage.IsCQ(txMsg);
            DateTime txEndTime = DateTime.UtcNow;
            shortTx = false;
            double? txTime = null;

            if (txMsg == "TUNE") tuning = false;
            DebugOutput($"{nl}{Time()} WSJT-X event, Tx end: toCall:'{toCall}' lastToCall:'{lastToCall}' deCall:'{deCall}' cmdToCall:'{cmdToCall}' tuning:{tuning}");
            DebugOutput($"{spacer}toCallTxStart:{toCallTxStart} decodesProcessed:{decodesProcessed} txEndTime:{txEndTime.ToString("HHmmss.fff")} maxTxRepeat:{maxTxRepeat}");

            if (toCall == null)
            {
                if (txMsg == "TUNE")
                {
                    DisableTx(false);
                    HaltTx();          //****this syncs txEnable state with WSJT-X****
                    return;
                }

                //WSJT-X replied to invalid message, process next msg
                txTimeout = true;
                SetCallInProg(null);
                return;
            }

            if (toCall == discardCall)
            {
                discardCallCycleCount = 0;
                DebugOutput($"{spacer}discardCall:'{discardCall}' discardCallCycleCount:{discardCallCycleCount}");
            }

            UpdateCallInProg();
            DebugOutputStatus();

            //toCallTxStart = null is a special case: intentional interruption
            //allow interruption if tx time was long enough
            txInterrupted = (toCallTxStart != null && toCall != toCallTxStart);
            if ((mode == "FT8" || mode == "FT4") && txBeginTime != DateTime.MaxValue)
            {
                //FT8: 12.64 sec, FT4: 4.48 sec tx time normally
                int shortTxMsec = (mode == "FT8" ? 11000 : 3500);       //how short tx can be and still be assumed a valid tx
                txTime = (txEndTime - txBeginTime).TotalMilliseconds;
                shortTx = (txTime < shortTxMsec);
            }

            DebugOutput($"{spacer}txTime:{txTime}");

            if (shortTx || txInterrupted)           //tx was invalid
            {
                DebugOutput($"{spacer}shortTx:{shortTx} txInterrupted:{txInterrupted} tx originally to '{toCallTxStart}'");
            }
            else
            {
                //check for max Tx count during Tx hold
                if (ctrl.freqCheckBox.Checked && autoFreqPauseMode == autoFreqPauseModes.DISABLED && (ctrl.holdCheckBox.Checked || txMode == TxModes.LISTEN))
                {
                    consecTxCount++;
                    if (consecTxCount >= maxConsecTxCount)
                    {
                        if (autoFreqPauseMode == autoFreqPauseModes.DISABLED)
                        {
                            DisableTx(true);
                            autoFreqPauseMode = autoFreqPauseModes.ENABLED;
                            UpdateCallInProg();
                            DebugOutput($"{spacer}auto freq update started (consec Tx), autoFreqPauseMode:{autoFreqPauseMode}");
                        }
                        else
                        {
                            consecTxCount = 0;
                        }
                    }
                }
                else
                {
                    consecTxCount = 0;
                }
                DebugOutput($"{spacer}autoFreqPauseMode:{autoFreqPauseMode} consecTxCount:{consecTxCount}");

                //could have clicked on "CQ" button in WSJT-X
                if (isCq)
                {
                    DebugOutput($"{spacer}possible CQ button, callInProg:'{CallPriorityString(callInProg)}'");

                    if (txMode == TxModes.LISTEN)
                    {
                        txTimeout = true;
                        if (discardCall == null) SetCallInProg(null);       //call expires now
                        DebugOutput($"{spacer}txTimeout:{txTimeout} txMode:{txMode}");
                    }

                    //check for consecutive CQs sent
                    if (ctrl.freqCheckBox.Checked && autoFreqPauseMode == autoFreqPauseModes.DISABLED && txMode == TxModes.CALL_CQ)
                    {
                        if (++consecCqCount >= maxConsecCqCount)
                        {
                            if (autoFreqPauseMode == autoFreqPauseModes.DISABLED)
                            {
                                DisableTx(true);
                                autoFreqPauseMode = autoFreqPauseModes.ENABLED;
                                UpdateCallInProg();
                                DebugOutput($"{spacer}auto freq update started (CQs)");
                            }
                            else
                            {
                                consecCqCount = 0;
                            }
                        }
                    }
                    else
                    {
                        consecCqCount = 0;
                    }
                    DebugOutput($"{spacer}toCall:{toCall} autoFreqPauseMode:{autoFreqPauseMode} consecCqCount:{consecCqCount} consecTimeoutCount:{consecTimeoutCount}");
                }
                else    //toCall not CQ
                {
                    consecCqCount = 0;
                    DebugOutput($"{spacer}consecCqCount:{consecCqCount} consecTimeoutCount:{consecTimeoutCount}");
                }

                if (debug)
                {
                    DebugOutput($"{spacer}logEarlyCheckBox:{ctrl.logEarlyCheckBox.Checked} IsRogers:{WsjtxMessage.IsRogers(txMsg)} RecdReport:{RecdReport(toCall)} RecdRogerReport:{RecdRogerReport(toCall)}{nl}{spacer}sentReportList.Contains:{sentReportList.Contains(toCall)} logList.Contains:{logList.Contains(toCall)} sentCallList.Contains:{sentCallList.Contains(toCall)}");
                }

                if (!logList.Contains(toCall))          //toCall not logged yet this mode/band for this session
                {
                    //check for time to log early; NOTE: doing this at Tx end because WSJT-X may have changed Tx msgs (between Tx start and Tx end) due to late-decode for the current call
                    //  option enabled                   just sent RRR                and prev. recd Report  or prev. recd RogerReport   and prev. sent any report
                    if (IsLogEarly(toCall) && WsjtxMessage.IsRogers(txMsg) && (RecdReport(toCall) || RecdRogerReport(toCall)) && sentReportList.Contains(toCall))
                    {
                        DebugOutput($"{spacer}early logging: toCall:'{toCall}'");
                        LogQso(toCall);
                    }
                    //check for QSO completed, trigger next call in the queue
                    if (WsjtxMessage.Is73orRR73(txMsg))
                    {
                        txTimeout = true;
                        tCall = toCall;
                        xmitCycleCount = 0;
                        SetCallInProg(null);
                        DebugOutput($"{spacer}reset(2): (is 73 or RR73) xmitCycleCount:{xmitCycleCount} txTimeout:{txTimeout}{nl}           callInProg:'{CallPriorityString(callInProg)}' tCall:'{tCall}'");

                        //NOTE: doing this at Tx end because WSJT-X may have changed Tx msgs (between Tx start and Tx end) due to late-decode for the current call
                        // prev. recd Report    or prev. recd RogerReport   and prev. sent any report
                        if ((RecdReport(toCall) || RecdRogerReport(toCall)) && sentReportList.Contains(toCall))
                        {
                            DebugOutput($"{spacer}normal logging: toCall:'{toCall}'");
                            LogQso(toCall);
                        }
                    }
                }
                else    //logList contains toCall
                {
                    if (WsjtxMessage.Is73orRR73(txMsg))
                    {
                        txTimeout = true;      //timeout to Tx the next call in the queue
                        tCall = toCall;
                        xmitCycleCount = 0;
                        SetCallInProg(null);
                        DebugOutput($"{spacer}reset(6): (is 73 or RR73) xmitCycleCount:{xmitCycleCount} txTimeout:{txTimeout}{nl}           callInProg:'{CallPriorityString(callInProg)}' tCall:'{tCall}'");
                    }
                }

                //count tx cycles: check for changed Tx call in WSJT-X
                UpdateMaxTxRepeat();
                if (maxTxRepeat > 1 && !IsSameMessage(lastTxMsg, txMsg))
                {
                    if (xmitCycleCount >= 0)
                    {
                        //check  for "to" call changed since last xmit end
                        // !restartQueue = didn't just add this call to queue during late-decode that overlapped Tx start
                        if (!restartQueue && toCall != lastToCall && callQueue.Contains(toCall))
                        {
                            _callQueueStore.RemoveCall(toCall);         //manually switched to Txing a call that was also in the queue
                        }

                        if (ctrl.holdCheckBox.Checked && toCall == lastToCall)      //overall xmit limit during hold
                        {
                            xmitCycleCount++;
                        }
                        else
                        {
                            xmitCycleCount = 0;
                        }
                        DebugOutput($"{spacer}reset(1) (different msg) xmitCycleCount:{xmitCycleCount} txMsg:'{txMsg}' lastTxMsg:'{lastTxMsg}' holdCheckBox.Checked:{ctrl.holdCheckBox.Checked}");
                    }
                    lastTxMsg = txMsg;
                }
                else        //same "to" call as last xmit or maxTxRepeat = 1, count xmit cycles
                {
                    if (!isCq)        //don't count CQ (or non-std) calls
                    {
                        xmitCycleCount++;           //count xmits to same call sign at end of xmit cycle
                        DebugOutput($"{spacer}(same msg, or maxTxRepeat = 1) xmitCycleCount:{xmitCycleCount} txMsg:'{txMsg}' lastTxMsg:'{lastTxMsg}'");
                        DebugOutput($"{spacer}holdCheckBox.Checked:{ctrl.holdCheckBox.Checked} holdMaxTxRepeat:{holdMaxTxRepeat}");

                        if ((!ctrl.holdCheckBox.Checked && xmitCycleCount >= maxTxRepeat - 1) || (ctrl.holdCheckBox.Checked && xmitCycleCount >= holdMaxTxRepeat - 1))  //n msgs = n-1 diffs
                        {
                            xmitCycleCount = 0;
                            txTimeout = true;
                            if (discardCall == null) SetCallInProg(null);       //call expires now
                            timedOutCall = toCall;
                            tCall = toCall;        //call to remove from queue, will be null if non-std msg
                            lastTxMsg = null;
                            ctrl.holdCheckBox.Checked = false;

                            //this caller might call indefinitely, so count call attempts
                            AddTimeoutCall(toCall);
                            DebugOutput($"{spacer}reset(3) (timeout) xmitCycleCount:{xmitCycleCount} txTimeout:{txTimeout} tCall:'{tCall}' callInProg:'{CallPriorityString(callInProg)}' holdCheckBox.Checked:{ctrl.holdCheckBox.Checked}");
                        }
                    }
                    else
                    {
                        //same CQ or non-std call
                        xmitCycleCount = 0;
                        DebugOutput($"{spacer}reset(4) (no action, CQ or non-std) xmitCycleCount:{xmitCycleCount}");
                    }
                }

                if (txTimeout)      //CQ or reply timed out
                {
                    DebugOutput($"{spacer}'{tCall}' timed out or completed");
                    _callQueueStore.RemoveCall(tCall);

                    if (!isCq) consecCqCount = 0;
                    //auto freq update when too many timed out replies
                    DebugOutput($"{spacer}ctrl.freqCheckBox.Checked:{ctrl.freqCheckBox.Checked} autoFreqPauseMode:{autoFreqPauseMode} txMode:{txMode} toCall:{toCall} mode:'{mode}'");
                    if (ctrl.freqCheckBox.Checked && autoFreqPauseMode == autoFreqPauseModes.DISABLED && txMode == TxModes.CALL_CQ && toCall != "CQ")
                    {
                        consecTimeoutCount += maxTxRepeat;
                        if (consecTimeoutCount >= maxConsecTimeoutCount)
                        {
                            if (autoFreqPauseMode == autoFreqPauseModes.DISABLED)
                            {
                                DisableTx(true);
                                autoFreqPauseMode = autoFreqPauseModes.ENABLED;
                                UpdateCallInProg();
                                DebugOutput($"{spacer}auto freq update started (no QSOs)");
                            }
                            else
                            {
                                consecTimeoutCount = 0;
                            }
                        }
                    }
                    else
                    {
                        consecTimeoutCount = 0;
                    }
                    DebugOutput($"{spacer}txTimeout:{txTimeout} autoFreqPauseMode:{autoFreqPauseMode} consecTimeoutCount:{consecTimeoutCount} callQueue.Count:{callQueue.Count} consecCqCount:{consecCqCount}");
                }

                //check for time to process new directed CQ
                if (txMode == TxModes.CALL_CQ && (toCall == "CQ" || qsoState == WsjtxMessage.QsoStates.CALLING) && (ctrl.callCqDxCheckBox.Checked || (ctrl.callDirCqCheckBox.Checked && ctrl.directedTextBox.Text.Trim().Length > 0)))
                {
                    xmitCycleCount = 0;
                    newDirCq = true;
                    DebugOutput($"{spacer}reset(5) (new directed CQ) xmitCycleCount:{xmitCycleCount} newDirCq:{newDirCq}");
                }
            }

            ShowStatus();           //**before** adding to sentReportList and sentCallList

            //save all call signs a report msg was (completely/partially) sent to
            if (!isCq && !sentCallList.Contains(toCall)) sentCallList.Add(toCall);
            if ((WsjtxMessage.IsReport(txMsg) || WsjtxMessage.IsRogerReport(txMsg)) && !sentReportList.Contains(toCall)) sentReportList.Add(toCall);

            txBeginTime = DateTime.MaxValue;
            DebugOutput($"{Time()} Tx end done, lastTxMsg:'{lastTxMsg}' txEnabled:{txEnabled} cqPaused:{cqPaused}");
            UpdateDebug();      //unconditional
        }

        //log a QSO (early or normal timing in QSO progress)
        private void LogQso(string call)
        {
            List<EnqueueDecodeMessage> msgList;
            if (!allCallDict.TryGetValue(call, out msgList)) return;          //no previous call(s) from DX station
            EnqueueDecodeMessage rMsg;
            if ((rMsg = msgList.FindLast(RogerReport)) == null && (rMsg = msgList.FindLast(Report)) == null) return;        //the DX station never reported a signal
            if (!sentReportList.Contains(call)) return;         //never reported SNR to the DX station
            RequestLog(call, rMsg, null);
        }

        private bool IsEvenPeriod(int secPastHour)          //or seconds since midnight
        {
            if (mode == "FT4")          //irregular
            {
                int sec = secPastHour % 60;     //seconds past the minute
                return (sec >= 0 && sec < 7) || (sec >= 15 && sec < 22) || (sec >= 30 && sec < 37) || (sec >= 45 && sec < 52);
            }

            return (secPastHour / (trPeriod / 1000)) % 2 == 0;
        }

        internal bool IsEvenCall(EnqueueDecodeMessage d)
        {
            return IsEvenPeriod((d.SinceMidnight.Minutes * 60) + d.SinceMidnight.Seconds);
        }

        private string NextDirCq()
        {
            string dirCq = "";

            List<string> dirList = new List<string>();
            if (ctrl.callNonDirCqCheckBox.Checked)
            {
                dirList.Add("");            //note zero length
            }

            if (ctrl.callCqDxCheckBox.Checked)
            {
                dirList.Add("DX");
            }

            if ((ctrl.callDirCqCheckBox.Checked && ctrl.directedTextBox.Text.Trim().Length > 0))
            {
                // A locked entry (picked in the Call CQ dialog) always wins over rotation --
                // but only while it's still one of the currently-typed entries, so editing the
                // text box to drop a locked code falls back to Random instead of silently
                // repeating a code the operator no longer wants at all.
                string locked = ctrl.directedCqLockedEntry;
                string[] entries = ctrl.CallDirCqEntries();
                if (!string.IsNullOrEmpty(locked) && entries.Contains(locked, StringComparer.OrdinalIgnoreCase))
                    dirList.Add(locked);
                else
                    dirList.AddRange(entries);
            }

            if (dirList.Count > 0)
            {
                string s = dirList[rnd.Next(dirList.Count)];
                if (s.Length <= 4 && s.Length > 0) dirCq = " " + s;          //is directed else non-directed
                DebugOutput($"{spacer}dirCq:'{dirCq}'");
            }

            return dirCq;
        }

        private void ResetOpMode()
        {
            StopDecodeTimers();
            postDecodeTimer.Stop();
            decodeCycle = 0;
            decodeCount = 0;
            consecNoDecodes = 0;
            DebugOutput($"{Time()} ResetOpMode, postDecodeTimer stop, decodeCycle:{decodeCycle}");
            ClearCalls(true);
            cqPaused = true;
            if (WsjtxMessage.NegoState != WsjtxMessage.NegoStates.WAIT) HaltTx();
            opMode = OpModes.IDLE;
            lastMode = null;
            lastSpecOp = null;
            lastDecoding = null;
            lastXmitting = null;
            bandIdx = null;
            decodesProcessed = false;
            myCall = null;
            myGrid = null;
            SetCallInProg(null);
            txTimeout = false;
            restartQueue = false;
            replyCmd = null;
            curCmd = null;
            replyDecode = null;
            tCall = null;
            newDirCq = false;
            dxCall = null;
            xmitCycleCount = 0;
            logList.Clear();        //can re-log on new mode, band, or session
            ShowLogged();
            UpdateModeVisible();
            UpdateModeSelection();
            UpdateDebug();
            UpdateBandComboBox();
            UpdateDblClkTip();
            AutoFreqChanged(ctrl.freqCheckBox.Checked, true);
            ctrl.holdCheckBox.Checked = false;
            DebugOutput($"{nl}{Time()} ResetOpMode, opMode:{opMode} NegoState:{WsjtxMessage.NegoState} cqPaused:{cqPaused}");
        }

        private void ClearCalls(bool clearBandSpecific)             //if only changing Tx period, keep info for the current band, since may return to original Tx period
        {
            callQueue.Clear();
            UpdateMaxTxRepeat();
            callDict.Clear();
            if (clearBandSpecific)
            {
                timeoutCallDict.Clear();
                allCallDict.Clear();
                sentCallList.Clear();
                sentReportList.Clear();
                unwantedCqList.Clear();
            }
            decodeNum = 0;
            ShowQueue();
            if (ctrl.advancedCallLayout) ShowAdvancedQueue(null);
            xmitCycleCount = 0;
            timedOutCall = null;
            CancelDiscardCall();
            ctrl.holdCheckBox.Checked = false;  //reset hold
            DebugOutput($"{Time()} ClearCalls, clearBandSpecific:{clearBandSpecific} decodeNum:{decodeNum} xmitCycleCount:{xmitCycleCount}");
            StopDecodeTimers();
        }

        internal string Time()
        {
            var dt = DateTime.UtcNow;
            return dt.ToString("HHmmss.fff");
        }

        public void Closing()
        {
            DebugOutput($"{nl}{nl}{DateTime.UtcNow.ToString("yyyy-MM-dd HHmmss")} UTC ###################### Program closing...");
            if (opMode > OpModes.IDLE) HaltTx();
            ResetOpMode();
            ShowStatus();
            heartbeatRecdTimer.Stop();
            cmdCheckTimer.Stop();
            DebugOutput($"{spacer}heartbeatRecdTimer stop");

            try
            {
                if (emsg != null && udpClient2 != null)
                {
                    //notify WSJT-X
                    // Stage A4: build+send moved to WsjtxCompatibilityExtension.DeInit.
                    _compatExtension.DeInit(ctrl.skipGridCheckBox.Checked, ctrl.useRR73CheckBox.Checked,
                        msg => DebugOutput($"{Time()} {msg}"));
                    Thread.Sleep(500);
                    udpClient2.Close();
                    udpClient2 = null;
                    DebugOutput($"{spacer}closed udpClient2:{udpClient2}");
                }
            }
            catch (Exception e)         //udpClient might be disposed already
            {
                DebugOutput($"{spacer}error at Closing, error:{e.ToString()}");
            }

            CloseAllUdp();

            _potaLog.Close();

            // Stage A6: close the persistent classification LogbookDb connection.
            _logbookDb?.Dispose();
            // TEMPORARY developer diagnostic -- see Classification/ClassificationParityLogger.cs.
            ClassificationParityLogger.Close();

            SetLogFileState(false);         //close log file
        }

        public void Dispose()
        {
        }

        //during decoding, check for late signoff (73 or RR73)
        //from a call sign that isn't (or won't be) the call in progress;
        //if reports have been exchanged, log the QSO;
        //logging is done directly via log file, not via WSJT-X
        private void CheckLateLog(string call, EnqueueDecodeMessage msg)
        {
            DebugOutput($"{spacer}CheckLateLog: call:'{call}' callInProg:'{CallPriorityString(callInProg)}' txTimeout:{txTimeout} msg:{msg.Message} Is73orRR73:{WsjtxMessage.Is73orRR73(msg.Message)} logList:{logList.Contains(call)} allCallDict:{allCallDict.ContainsKey(call)} sentReport:{sentReportList.Contains(call)}");
            if (call == null || !WsjtxMessage.Is73orRR73(msg.Message))
            {
                DebugOutput($"{spacer}no late log: msg is null or not 73/RR73");
                return;
            }

            if (logList.Contains(call))         //call already logged for thos mode or band for this session
            {
                DebugOutput($"{spacer}no late log: call is already logged");
                return;
            }

            List<EnqueueDecodeMessage> msgList;
            if (!allCallDict.TryGetValue(call, out msgList))
            {
                DebugOutput($"{spacer}no late log: no previous call(s) rec'd");
                return;          //no previous call(s) from DX station
            }

            EnqueueDecodeMessage rMsg;
            if ((rMsg = msgList.FindLast(RogerReport)) == null && (rMsg = msgList.FindLast(Report)) == null)
            {
                DebugOutput($"{spacer}no late log: no report rec'd from '{call}'; allCallDict has {msgList.Count} msg(s): {string.Join(", ", msgList.Select(m => $"'{m.Message}'"))}");
                return;        //the DX station never reported a signal
            }

            if (!sentReportList.Contains(call))
            {
                DebugOutput($"{spacer}no late log: no previous report(s) sent");
                return;         //never reported SNR to the DX station
            }

            RequestLog(call, rMsg, msg);              //process a "late" QSO completion
        }

        private bool Rogers(EnqueueDecodeMessage msg)
        {
            return WsjtxMessage.IsRogers(msg.Message);
        }

        private bool RogerReport(EnqueueDecodeMessage msg)
        {
            return WsjtxMessage.IsRogerReport(msg.Message);
        }

        private bool Report(EnqueueDecodeMessage msg)
        {
            return WsjtxMessage.IsReport(msg.Message);
        }

        private bool Reply(EnqueueDecodeMessage msg)
        {
            return WsjtxMessage.IsReply(msg.Message);
        }

        private bool Signoff(EnqueueDecodeMessage msg)
        {
            return WsjtxMessage.Is73orRR73(msg.Message);
        }

        private bool CQ(EnqueueDecodeMessage msg)
        {
            return WsjtxMessage.IsCQ(msg.Message);
        }

        //request WSJT-X log a QSO to the WSJT-X .ADI log file and re-broadcast to UDP listeners;
        //logging done only via WSJT-X because WSJT-X keeps track of 'logged-before' status, 
        //which is important to processing CQ notification msgs received from WSJT-X
        //recdMsg null if logging because of a sent msg
        private void RequestLog(string call, EnqueueDecodeMessage reptMsg, EnqueueDecodeMessage recdMsg)
        {
            string qsoDateOff, qsoTimeOff;

            //<call:4>W1AW  <gridsquare:4>EM77 <mode:3>FT8 <rst_sent:3>-10 <rst_rcvd:3>+01 <qso_date:8>20201226 
            //<time_on:6>042215 <qso_date_off:8>20201226 <time_off:6>042300 <band:3>40m <freq:8>7.076439 
            //<station_callsign:4>WM8Q <my_gridsquare:6>DN61OK <eor>

            string rstSent = reptMsg.Snr == 0 ? "+00" : (reptMsg.Snr > 0 ? "+" + reptMsg.Snr.ToString("D2") : reptMsg.Snr.ToString("D2"));
            string rstRecd = WsjtxMessage.RstRecd(reptMsg.Message);
            string qsoDateOn = reptMsg.RxDate.ToString("yyyyMMdd");
            string qsoTimeOn = reptMsg.SinceMidnight.ToString("hhmmss");      //one of the report decodes
            EnqueueDecodeMessage cqMsg = CqMsg(call);
            bool isPota = cqMsg != null && cqMsg.IsPota();
            var dtNow = DateTime.UtcNow;

            if (recdMsg == null)            //logging because of xmitted RRR, 73, or RR73 (not because of a rec'd msg)
            {
                qsoDateOff = dtNow.ToString("yyyyMMdd");
                qsoTimeOff = dtNow.TimeOfDay.ToString("hhmmss");
            }
            else
            {
                qsoDateOff = recdMsg.RxDate.ToString("yyyyMMdd");
                qsoTimeOff = recdMsg.SinceMidnight.ToString("hhmmss");
            }
            string qsoMode = mode;
            string grid = "";
            EnqueueDecodeMessage gridMsg = ReplyMsg(call);
            if (gridMsg == null) gridMsg = cqMsg;
            if (gridMsg != null)
            {
                string g = WsjtxMessage.Grid(gridMsg.Message);
                if (g != null) grid = g;                //CQ does have a grid
            }
            string freq = ((dialFrequency + txOffset) / 1e6).ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
            string band = FreqToBandStr(dialFrequency / 1e6);
            if (band == null) band = "Unknown";

            // Reuses the same shared builder as HandleLiveQsoLogged/HandleLiveAdifLogged (see
            // AdifRecordBuilder.cs) instead of maintaining a separate hand-rolled field list here --
            // this is the record actually sent to WSJT-X (cmd:255) and, via ImportLiveLoggedQso,
            // uploaded verbatim to QRZ/Club Log in real time, so any field the shared builder
            // supports now flows through for Jimmy-initiated logging too. name/comment/tx_pwr/
            // operator/exchange are passed empty -- WSJT-X's own QsoLoggedMessage carries those
            // (typed into WSJT-X's own logging dialog); Jimmy's self-initiated log here has no
            // equivalent source for them today.
            string adifRecord = AdifRecordBuilder.Build(
                call, band, (long)(dialFrequency + txOffset), mode,
                qsoDateOn, qsoTimeOn, qsoTimeOff, rstSent, rstRecd, grid,
                name: "", comment: "", txPwr: "", operatorCall: "",
                stationCall: myCall, myGrid: myGrid, qsoDateOff: qsoDateOff);

            //request add record to log / worked before (using explicit parameters, unlike typical WSJT-X logging)
            //send ADIF record to WSJT-X for re-broadcast to logging pgms
            // Stage A4: build+send moved to WsjtxCompatibilityExtension.BroadcastLoggedQso.
            _compatExtension.BroadcastLoggedQso(call, grid, band, mode, adifRecord,
                msg => DebugOutput($"{Time()} {msg}"));

            // Root-cause fix: the cmd:255 broadcast above asks WSJT-X to log the QSO, but
            // WSJT-X's own confirmation (QsoLoggedMessage/LoggedAdifMessage) -- the only
            // thing that normally drives ImportLiveLoggedQso (local logbook.db, Awards/Still
            // Need tracking, and QRZ/Club Log real-time upload) -- is not guaranteed to come
            // back. Observed in practice: WSJT-X wrote the QSO to its own ADIF log but never
            // sent the confirmation, so Jimmy's own database silently never learned about it.
            // Jimmy already has every field needed to record this QSO itself, so it does so
            // directly here instead of depending solely on that round trip. ClaimLiveLoggedQso
            // still dedupes against WSJT-X's confirmation if it arrives afterward.
            string liveDedupKey = AdifImporter.BuildDedupKey(call, band, mode, qsoDateOn, qsoTimeOn);
            if (ClaimLiveLoggedQso(liveDedupKey))
            {
                var liveFields = new Dictionary<string, string>
                {
                    ["CALL"]             = call,
                    ["BAND"]             = band,
                    ["FREQ"]             = freq,
                    ["MODE"]             = mode,
                    ["QSO_DATE"]         = qsoDateOn,
                    ["TIME_ON"]          = qsoTimeOn,
                    ["TIME_OFF"]         = qsoTimeOff,
                    ["RST_SENT"]         = rstSent,
                    ["RST_RCVD"]         = rstRecd,
                    ["GRIDSQUARE"]       = grid,
                    ["STATION_CALLSIGN"] = myCall,
                    ["MY_GRIDSQUARE"]    = myGrid,
                };
                EnrichWithClubLogGeoData(liveFields, call);
                ImportLiveLoggedQso(call, liveFields, adifRecord, liveDedupKey);
            }

            Sounds.PlaySoundEvent(ctrl.loggedCheckBox.Checked, ctrl.soundFile_Logged);
            StatusView.ShowMessage($"Logged QSO with {call}", false);
            if (isPota) _potaLog.Add(call, DateTime.Now, band, mode);         //local date/time
            consecCqCount = 0;
            consecTimeoutCount = 0;
            consecTxCount = 0;
            DebugOutput($"{spacer}QSO logged: call'{call}' consecCqCount:0 consecTimeoutCount:0 consecTxCount:0");
            UpdateCallInProg();
            logList.Add(call);
            ShowLogged();
            loggedCall = call;
            lCall = call;
            CancelDiscardCall();
            // Without this, an award's "still needed" tag (e.g. a state for WAS) stays stale
            // for the rest of the session on this band -- nothing else refreshes it after a
            // logged QSO, only a band change or restart. A stale "still needed" tag also lets
            // the already-worked exception (meant for repeatable special events like 13
            // Colonies) keep re-admitting this same, already-logged call into the queue.
            ctrl.RefreshStillNeedCache();
            UpdateDebug();
        }

        internal void RemoveAllCall(string call)
        {
            if (call == null) return;
            if (allCallDict.Remove(call)) DebugOutput($"{spacer}removed '{call}' from allCallDict");
            if (sentReportList.Remove(call)) DebugOutput($"{spacer}removed '{call}' from sentReportList");
            if (sentCallList.Remove(call)) DebugOutput($"{spacer}removed '{call}' from sentCallList");
        }

        public bool AnalysisNeeded => ctrl.freqCheckBox.Checked && !analysisCompleted;

        public void StartSlotAnalysis(bool pendingCq = false)
        {
            DebugOutput($"{Time()} [BAND-AUDIT] StartSlotAnalysis: bandIdx:{bandIdx} pendingCq:{pendingCq}");
            ClearAudioOffsets();
            pendingCqAfterAnalysis = pendingCq;
            _manualAnalysisRequested = true;
            StatusView.ShowMessage("Analyzing transmit slot...", false);

            if (pendingCq)
            {
                _slotAnalysisElapsedSeconds = 0;
                if (_slotAnalysisWatchdog == null)
                {
                    _slotAnalysisWatchdog = new System.Windows.Forms.Timer { Interval = SlotAnalysisStatusIntervalSeconds * 1000 };
                    _slotAnalysisWatchdog.Tick += SlotAnalysisWatchdog_Tick;
                }
                _slotAnalysisWatchdog.Start();
            }
        }

        // Fires every SlotAnalysisStatusIntervalSeconds while a CQ start is waiting on
        // analysis. Gives periodic "still working" feedback instead of silence, and gives up
        // (starting CQ anyway) once SlotAnalysisTimeoutSeconds have passed with no completion --
        // e.g. a quiet band that never produces a decode in one of the two periods.
        private void SlotAnalysisWatchdog_Tick(object sender, EventArgs e)
        {
            // Already resolved (completed normally, or superseded by a band change / new
            // analysis request) -- ClearAudioOffsets() resets pendingCqAfterAnalysis to false,
            // and DecodesCompleted() stops this timer directly on real completion, but this
            // guard covers any other path that leaves it stale.
            if (!pendingCqAfterAnalysis || analysisCompleted)
            {
                _slotAnalysisWatchdog.Stop();
                return;
            }

            _slotAnalysisElapsedSeconds += SlotAnalysisStatusIntervalSeconds;
            if (_slotAnalysisElapsedSeconds >= SlotAnalysisTimeoutSeconds)
            {
                _slotAnalysisWatchdog.Stop();
                pendingCqAfterAnalysis = false;
                StatusView.ShowMessage("Transmit slot analysis timed out; starting CQ anyway.", false);
                ctrl.cqModeButton_Click(null, null);
            }
            else
            {
                StatusView.ShowMessage($"Still analyzing transmit slot... ({_slotAnalysisElapsedSeconds}s)", false);
            }
        }

        public bool ToggleTxFirst()
        {
            HaltTuning();
            HaltTx();           // stop current TX before switching period so WSJT-X honors the new setting
            DebugOutput($"{Time()} ToggleTxFirst, newFreq:{0} newTxFirst:{!txFirst}");
            SetBandTxFirst(0, !txFirst, "ToggleTxFirst");
            // Write an immediate status before WSJT-X confirms via StatusMessage
            // (~100 ms round-trip).  ShowStatus() will overwrite this once newTxFirst
            // is set in the txFirst-change handler.
            string pendingSide = txFirst ? "second" : "first";
            ctrl.statusText.ForeColor = Color.Black;
            ctrl.statusText.BackColor = Color.Yellow;
            ctrl.statusText.Text = $"Tx {pendingSide} selected, halted";
            ctrl.statusText.SelectionStart  = 0;
            ctrl.statusText.SelectionLength = 0;
            // Force NVDA/JAWS to announce this pending status immediately, same guard and
            // reasoning as Controller.RenderStatus (only send to the foreground window).
            if (ctrl.statusText.Focused && Form.ActiveForm == ctrl)
                SendKeys.Send("{UP}");
            return true;
        }

        // Last time Alt+U told WSJT-X to upload to LoTW, for display on the Sync
        // Status view. In-memory only (not persisted) -- LoTW upload is a manual,
        // user-triggered action, not a background schedule, so "since I started
        // Jimmy" is the meaningful window, not "ever."
        public DateTime? lastLotwUploadTrigger;

        // Alt+N: Walk Call Filter order (outer), then rank order within each category (inner).
        // The first category in callingEnabled that has a queued call wins; within that category,
        // the highest-ranked call (lowest callQueue index) is selected.
        // POTA/SOTA/MANUAL_SEL are treated as WANTED_CQ for filter matching.
        // Ordinary CQ (DEFAULT) is selectable when it is checked in Call Filters.
        public void NextBestPriorityCall()
        {
            NextBestPriorityCallFiltered(_ => true);
        }

        // Alt+N when TX1 list is focused: same category-first ordering, limited to calls
        // visible in the TX1 snapshot.
        public void NextBestPriorityCallFromTx1()
        {
            var snap = new HashSet<string>(_tx1SnapshotCalls, StringComparer.OrdinalIgnoreCase);
            NextBestPriorityCallFiltered(call => snap.Contains(call));
        }

        // Alt+N when TX2 list is focused: same category-first ordering, limited to calls
        // visible in the TX2 snapshot.
        public void NextBestPriorityCallFromTx2()
        {
            var snap = new HashSet<string>(_tx2SnapshotCalls, StringComparer.OrdinalIgnoreCase);
            NextBestPriorityCallFiltered(call => snap.Contains(call));
        }

        // Alt+N when Raw Decodes list is focused: same category-first ordering, limited to calls
        // that pass the current raw-decode filter.
        public void NextBestPriorityCallFromRaw()
        {
            var rawCallSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in _rawDecodeHistory)
            {
                if (!PassesRawDecodeFilter(d)) continue;
                string deCall = d.DeCall();
                if (!string.IsNullOrEmpty(deCall)) rawCallSet.Add(deCall);
            }
            NextBestPriorityCallFiltered(call => rawCallSet.Contains(call));
        }

        // Shared implementation for all Alt+N variants.
        // Outer loop: Call Filter order (callingEnabled list).
        // Inner loop: callQueue rank order — highest-ranked call in the winning category is selected.
        // inView: returns true if the call should be considered (used to restrict to a visible list).
        private void NextBestPriorityCallFiltered(Func<string, bool> inView)
        {
            var arr = callQueue.ToArray();
            foreach (CallCategory filterCat in Ranker.callingEnabled)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    if (!inView(arr[i])) continue;
                    EnqueueDecodeMessage dmsg;
                    if (!callDict.TryGetValue(arr[i], out dmsg)) continue;
                    // Map POTA/SOTA/MANUAL_SEL to WANTED_CQ for filter-category matching.
                    CallCategory effCat = dmsg.Category;
                    if (effCat == CallCategory.POTA || effCat == CallCategory.SOTA || effCat == CallCategory.MANUAL_SEL)
                        effCat = CallCategory.WANTED_CQ;
                    if (effCat != filterCat) continue;
                    NextCall(false, i);
                    return;
                }
            }
            ctrl.statusText.Text = "No priority calls available";
        }

        // Resolves the queue index to actually dispatch to. When expectedCall was
        // captured at selection time, re-locates that call's *current* position via
        // lookupCurrentIndex rather than trusting rawIdx -- the queue reorders on
        // essentially every decode cycle, so by the time dialogTimer2_Tick fires
        // ~20ms later, the operator's selected call has very likely moved, not
        // vanished. Only a lookup failure (-1) means the call actually left the
        // queue and dispatch must be aborted; a mere reorder must still work it.
        // Without an identity (legacy callers), rawIdx is used as-is.
        public static int ResolveDispatchIndex(string expectedCall, int rawIdx, Func<string, int> lookupCurrentIndex)
        {
            return expectedCall == null ? rawIdx : lookupCurrentIndex(expectedCall);
        }

        public void NextCall(bool confirm, int idx, bool operatorSelected = false, string expectedCall = null)
        {
            HaltTuning();
            DebugOutput($"{Time()} NextCall {idx} operatorSelected:{operatorSelected} expectedCall:{expectedCall ?? "(none)"}");
            dialogTimer2.Tag = $"{confirm} {idx} {operatorSelected} {expectedCall ?? ""}";
            dialogTimer2.Start();
        }

        private void dialogTimer2_Tick(object sender, EventArgs e)
        {
            dialogTimer2.Stop();
            //if (cqPaused) return;
            var a = ((string)dialogTimer2.Tag).Split(' ');
            bool confirm = a[0] == "True";
            int idx = Convert.ToInt32(a[1]);
            bool operatorSelected = a.Length > 2 && a[2] == "True";
            string expectedCall = a.Length > 3 && a[3].Length > 0 ? a[3] : null;

            idx = ResolveDispatchIndex(expectedCall, idx, FindCallIndexInQueue);

            DebugOutput($"{Time()} dialogTimer2_Tick, idx:{idx} expectedCall:{expectedCall ?? "(none)"}");

            // Never substitute another call for the one the operator selected: bail out
            // rather than guessing (see ResolveDispatchIndex above). A quiet status-text
            // update (no sound, no forced screen-reader announcement) tells the operator
            // why nothing happened instead of leaving Enter/Space looking like a no-op.
            if (idx < 0)
            {
                DebugOutput($"{spacer}NextCall aborted: selected call no longer in queue");
                StatusView.ShowMessage(expectedCall != null ? $"{expectedCall} no longer available" : "No call selected", false);
                return;
            }

            if (callQueue.Count > 0)
            {
                if (idx >= callQueue.Count) return;
                DebugOutput($"{_callQueueStore.CallQueueString()}");
                EnqueueDecodeMessage dmsg = new EnqueueDecodeMessage();
                string call = _callQueueStore.PeekCall(idx, out dmsg);

                if (!confirm || Confirm($"Reply to {call}?") == DialogResult.Yes)
                {
                    if (!callQueue.Contains(call)) return;          //call has already been removed or processed

                    DateTime dtNow = DateTime.Now;
                    bool evenCall = IsEvenCall(dmsg);
                    bool evenPeriod = IsEvenPeriod((dtNow.Minute * 60) + dtNow.Second);       //listen mode can xmit on either period depending on current time

                    if (txMode == TxModes.LISTEN)
                    {
                        if (transmitting && evenCall == evenPeriod)     //reply is in same time period from msg
                        {
                            HaltTx();
                        }

                        DebugOutput($"{spacer}value:{ctrl.timeoutNumUpDown.Value}");
                        if (ctrl.timeoutNumUpDown.Value <= maxCheckTxRepeat)
                        {
                            DebugOutput($"{spacer}call:{call} evenCall:{evenCall}");
                            if (!ctrl.advancedCallLayout)
                                CheckCallQueuePeriod(!evenCall);        //remove queued calls from wrong time period
                            // Re-find call: period removal may have removed lower-indexed entries,
                            // shifting this call's position — the pre-removal idx is now stale.
                            idx = FindCallIndexInQueue(call);
                            if (idx < 0) return;                            //call was also removed
                        }
                    }

                    if (txMode == TxModes.CALL_CQ && !cqPaused && transmitting)
                    {
                        HaltTx();
                    }

                    xmitCycleCount = 0;
                    timedOutCall = null;
                    txTimeout = false;
                    newSelection = true;
                    ctrl.holdCheckBox.Checked = false;
                    EnqueueDecodeMessage prevReplyDecode = replyDecode;

                    if (operatorSelected) _manualCallInProg = true;
                    DebugOutput($"{spacer}reply to {call}, txTimeout:{txTimeout} holdCheckBox.Checked{ctrl.holdCheckBox.Checked} operatorSelected:{operatorSelected} _manualCallInProg:{_manualCallInProg}");
                    ReplyTo(idx);
                    StartDiscardCall(call);
                    if (!transmitting)                  //if transmtting, 
                    {
                        if (evenCall == evenPeriod)
                        {
                            //txEnableChanged = true;
                            ShowStatus();
                        }
                        else
                        {
                            StartStatusTimer();            //will actually be transmitting
                        }
                    }
                    else
                    {
                        lastStatusTxMsg = null;         //will be updated when interrupted Tx detected
                    }

                    DisableAutoFreqPause();
                    ClearCallTimeout(call);

                    UpdateDebug();
                    string n = cqPaused ? " next" : "";
                    if (!confirm) StatusView.ShowMessage($"Replying{n} to {call}", ctrl.callAddedCheckBox.Checked);
                    return;
                }
                return;
            }
        }

        public void EditCallQueue(int idx)
        {
            if (idx < 0) return;
            DebugOutput($"{Time()} EditCallQueue {idx}");
            dialogTimer3.Tag = idx;
            dialogTimer3.Start();
        }

        private void dialogTimer3_Tick(object sender, EventArgs e)
        {
            dialogTimer3.Stop();
            int idx = (int)dialogTimer3.Tag;
            if (idx > callQueue.Count - 1) return;
            var callArray = callQueue.ToArray();
            string call = callArray[idx];

            if (callQueue.Contains(call))
            {
                DebugOutput($"{Time()} dialogTimer3_Tick");
                _callQueueStore.RemoveCall(call);
                ShowStatus();
                DebugOutputStatus();
                UpdateDebug();
            }
        }

        //must be actual DX (relative to current continent) to match "DX"
        private bool IsDirectedAlert(string dirTo, bool isDx)
        {
            if (dirTo == null) return false;

            string[] a = ctrl.ReplyDirCqEntries();
            foreach (string elem in a)
            {
                if (elem == dirTo && (elem != "DX" || isDx)) return true;
            }
            return false;
        }

        //return true if received a R-XX or R+XX from the specified call
        private bool RecdRogerReport(string call)
        {
            if (call == null) return false;
            List<EnqueueDecodeMessage> msgList;
            if (!allCallDict.TryGetValue(call, out msgList)) return false;          //no previous call(s) from DX station
            //DebugOutput($"{spacer}recd previous call(s)");
            return msgList.FindLast(RogerReport) != null;        //the DX station never sent R-XX or R+XX
        }

        //return true if received a -XX or +XX from the specified call
        private bool RecdReport(string call)
        {
            if (call == null) return false;
            List<EnqueueDecodeMessage> msgList;
            if (!allCallDict.TryGetValue(call, out msgList)) return false;          //no previous call(s) from DX station
            //DebugOutput($"{spacer}recd previous call(s)");
            return msgList.FindLast(Report) != null;        //the DX station never sent -XX or +XX
        }

        //return true if received a grid from specified call
        private bool RecdReply(string call)
        {
            if (call == null) return false;
            List<EnqueueDecodeMessage> msgList;
            if (!allCallDict.TryGetValue(call, out msgList)) return false;          //no previous call(s) from DX station
            //DebugOutput($"{spacer}recd previous call(s)");
            return msgList.FindLast(Reply) != null;        //the DX station never sent grid
        }

        private bool RecdSignoff(string call)
        {
            if (call == null) return false;
            List<EnqueueDecodeMessage> msgList;
            if (!allCallDict.TryGetValue(call, out msgList)) return false;          //no previous call(s) from DX station
            //DebugOutput($"{spacer}recd previous call(s)");
            return msgList.FindLast(Signoff) != null;        //the DX station never sent 73 or RR73
        }

        private EnqueueDecodeMessage ReplyMsg(string call)
        {
            if (call == null) return null;
            List<EnqueueDecodeMessage> msgList;
            if (!allCallDict.TryGetValue(call, out msgList)) return null;          //no previous call(s) from DX station
            //DebugOutput($"{spacer}recd previous call(s)");
            return msgList.FindLast(Reply);
        }

        internal EnqueueDecodeMessage CqMsg(string call)
        {
            if (call == null) return null;
            List<EnqueueDecodeMessage> msgList;
            if (!allCallDict.TryGetValue(call, out msgList)) return null;          //no previous call(s) from DX station
            //DebugOutput($"{spacer}recd previous call(s)");
            return msgList.FindLast(CQ);
        }

        private bool RecdAnyMsg(string call)
        {
            if (call == null) return false;
            return RecdReply(call) || RecdReport(call) || RecdRogerReport(call);
        }

        private bool SentAnyMsg(string call)
        {
            if (call == null) return false;
            return sentCallList.Contains(call);
        }

        //add CQ (for grid info), 
        //or report (only those to myCall), or rogerReport (only those to myCall)
        //keep only one CQ and reply for a call, but update grid/dist/az info if necessary
        private void AddAllCallDict(string call, EnqueueDecodeMessage emsg)
        {
            if ((!emsg.IsCallTo(myCall) && !emsg.IsCQ())) return;
            //  is CQ          already added a CQ from call
            if (emsg.IsCQ() && CqMsg(call) != null) return;         //don't duplicate CQs

            List<EnqueueDecodeMessage> vlist;
            //create new List for call if nothing entered yet into the Dictionary
            if (!allCallDict.TryGetValue(call, out vlist)) allCallDict.Add(call, vlist = new List<EnqueueDecodeMessage>());
            vlist.Add(emsg);        //messages from call are in order rec'd, will be duplicate msg types
            DebugOutput($"{Time()} AddAllCallDict, call:{call} msg.Message:{emsg.Message}");
        }

        private void SetCallInProg(string call)
        {
            ctrl.holdCheckBox.Enabled = (call != null);
            DebugOutput($"{spacer}SetCallInProg: callInProg:'{CallPriorityString(call)}' (was '{CallPriorityString(callInProg)}') holdCheckBox.Enabled:{ctrl.holdCheckBox.Enabled}");

            if (call != null) lCall = null;     //last logged call is not relevant now

            if (call == null) { CancelDiscardCall(); _manualCallInProg = false; }

            callInProg = call;
            UpdateDblClkTip();
            UpdateCallInProg();
        }

        public void EnableTx()
        {
            try
            {
                if (emsg == null || udpClient2 == null)
                {
                    DebugOutput($"{Time()} EnableTx skipped, udpClient2:{udpClient2} emsg:{emsg}");
                    return;
                }

                DebugOutput($"{Time()} EnableTx, txEnabled:{txEnabled} processDecodeTimer.Enabled:{processDecodeTimer.Enabled}");
                // Stage A4: build+send moved to WsjtxCompatibilityExtension.EnableTx.
                _compatExtension.EnableTx(msg => DebugOutput($"{Time()} {msg}"));
                txEnabled = true;
                wsjtxTxEnableButton = true;
                UpdateDblClkTip();
                DebugOutput($"{spacer}txEnabled:{txEnabled}");
            }
            catch
            {
                DebugOutput($"{Time()} 'EnableTx' failed, txEnabled:{txEnabled}");        //only happens during closing
            }

            UpdateDebug();
        }

        private void DisableTx(bool buttonState)
        {
            DebugOutput($"{Time()} DisableTx, txEnabled:{txEnabled} processDecodeTimer.Enabled:{processDecodeTimer.Enabled}");
            StopDecodeTimers();

            try
            {
                if (emsg == null || udpClient2 == null)
                {
                    DebugOutput($"{Time()} DisableTx skipped, udpClient2:{udpClient2} emsg:{emsg}");
                    return;
                }

                // Stage A4: build+send moved to WsjtxCompatibilityExtension.DisableTx.
                _compatExtension.DisableTx(buttonState, msg => DebugOutput($"{Time()} {msg}"));
                txEnabled = false;
                wsjtxTxEnableButton = buttonState;
                UpdateDblClkTip();
                DebugOutput($"{spacer}txEnabled:{txEnabled}");
            }
            catch
            {
                DebugOutput($"{Time()} 'DisableTx' failed, txEnabled:{txEnabled}");        //only happens during closing
            }

            UpdateDebug();
        }

        // Stage A4: build+send moved to WsjtxCompatibilityExtension.EnableMonitoring.
        private void EnableMonitoring()
        {
            _compatExtension.EnableMonitoring(msg => DebugOutput($"{Time()} {msg}"));
        }

        // Stage A4: build+send moved to WsjtxCompatibilityExtension.SetListenMode.
        private void SetListenMode()
        {
            if (udpClient2 == null)
            {
                DebugOutput($"{Time()} SetListenMode skipped, udpClient2:{udpClient2}");
                return;
            }

            _compatExtension.SetListenMode(txMode == TxModes.LISTEN, msg => DebugOutput($"{Time()} {msg}"));
        }

        public void HaltTuning()
        {
            if (tuning) HaltTx();
        }

        public void HaltTx()
        {
            StopDecodeTimers();
            tuning = false;
            if (udpClient2 != null)
            {
                // Stage A8: also send the standard HaltTx message (msg type 8), not just
                // the non-standard sub-command 12 below -- confirmed via code reading
                // that the standard HaltTxMessage class existed but was never actually
                // instantiated/sent anywhere. A stock/Improved build (no Compatibility
                // Layer) silently ignores sub-command 12 entirely, so HaltTx() -- also
                // Jimmy's Escape-key emergency-stop path (HaltAndDisableTx, "TX halts
                // regardless of which mode Jimmy is in") -- previously had NO effect at
                // all against such a build. Sent unconditionally alongside the existing
                // sub-command 12 (kept as-is, zero behavior change for a Compatibility-
                // Layer build): pure addition, not a replacement, so nothing regresses if
                // a real-world edge case in the standard path ever surfaces. AutoOnly is
                // explicitly false -- an emergency stop must halt regardless of whether
                // the pending Tx was auto-generated or not, matching the sub-command 12
                // path's own unconditional behavior.
                var hmsg = new HaltTxMessage
                {
                    SchemaVersion = (uint)WsjtxMessage.NegotiatedSchemaVersion,
                    Id = WsjtxMessage.UniqueId,
                    AutoOnly = false,
                };
                ba = hmsg.GetBytes();
                udpClient2.Send(ba, ba.Length);
                DebugOutput($"{Time()} >>>>>Sent standard HaltTx (msg type 8)");

                // Stage A4: build+send moved to WsjtxCompatibilityExtension.HaltTx.
                _compatExtension.HaltTx(msg => DebugOutput($"{Time()} {msg}"));
                txEnabled = false;
                wsjtxTxEnableButton = false;
                UpdateDblClkTip();
            }
            else
            {
                DebugOutput($"{Time()} HaltTx skipped, udpClient2:{udpClient2}");
                return;
            }
        }

        // Stop current TX (cmd:12) and uncheck WSJT-X TX Enable button (cmd:8).
        // Use for Escape so TX halts regardless of which mode Jimmy is in.
        public void HaltAndDisableTx()
        {
            HaltTx();
            DisableTx(true);
        }

        // After abandoning a QSO, reset WSJT-X's pending TX message to CQ (cmd:10 + cmd:6).
        // This prevents a stale mid-QSO message (e.g. RRR) from firing if TX is re-enabled
        // before a fresh Reply can reset the selection.
        public void ResetTxToCq()
        {
            if (!ConnectedToWsjtx()) return;
            SetupCq(false);
        }

        public void UpdateModeSelection()
        {
            ctrl.cqModeButton.Checked = txMode == TxModes.CALL_CQ;
            ctrl.listenModeButton.Checked = txMode == TxModes.LISTEN;
        }

        private void UpdateListenModeTxPeriod()
        {
            ctrl.periodLabel.Visible = ctrl.periodComboBox.Visible = ctrl.PeriodHelpLabel.Visible = true;
            ctrl.periodLabel.Enabled = ctrl.periodComboBox.Enabled = txMode == TxModes.LISTEN;
        }

        private void UpdateRR73()
        {
            if (mode == "FT4")
            {
                ctrl.useRR73CheckBox.Checked = true;
                ctrl.useRR73CheckBox.Enabled = false;
            }
            else
            {
                ctrl.useRR73CheckBox.Checked = useRR73;
                ctrl.useRR73CheckBox.Enabled = true;

                if (mode == "") ctrl.WsjtxSettingConfirmed();
            }
        }

        private void StatusTimerTick(object sender, EventArgs e)
        {
            statusTimer.Stop();
            if (decodeCount == 0)
            {
                consecNoDecodes++;
            }
            else
            {
                consecNoDecodes = 0;
            }
            ShowStatus();
        }

        private void StatusTimer2Tick(object sender, EventArgs e)
        {
            statusTimer2.Stop();
            ShowStatus();
        }

        private void ProcessPostDecodeTimerTick(object sender, EventArgs e)
        {
            DecodesCompleted();
        }

        private void ProcessDecodeTimerTick(object sender, EventArgs e)
        {
            processDecodeTimer.Stop();
            //DebugOutput($"{nl}{Time()} processDecodeTimer stop");
            statusTimer.Interval = 2000;        //allow enough time so transmit (if needed) has started
            if (!tuning) statusTimer.Start();
            ProcessDecodes();
        }
        private void ProcessDecodeTimer2Tick(object sender, EventArgs e)
        {
            processDecodeTimer2.Stop();
            string toCall = WsjtxMessage.ToCall(curTxMsg);
            DebugOutput($"{nl}{Time()} ProcessDecodeTimer2Tick, processDecodeTimer2 stop, toCall:{toCall} curTxMsg:'{curTxMsg}' cqPaused:{cqPaused} transmitting:{transmitting} restartQueue:{restartQueue}");
            if (toCall == callInProg) _callQueueStore.RemoveCall(toCall);       //late decode caused WSJT-X to transmit a new response after the original transmit started

            //           "call CQ" mode and a late 73 may have caused WSJT-X to start calling "CQ" when it should be "CQ DX" or other directed CQ
            //TO-DO
            //bool unwantedCq = (txMode == TxModes.CALL_CQ && (qsoState == WsjtxMessage.QsoStates.CALLING || qsoState == WsjtxMessage.QsoStates.SIGNOFF));
            DebugOutput($"{spacer}txTimeout:{txTimeout} txMode:{txMode} qsoState:{qsoState}");
        }

        //the last decode pass has completed, ready to detect first decode pass
        private void DecodesCompleted()
        {
            postDecodeTimer.Stop();
            DebugOutput($"{nl}{Time()} DecodesCompleted, postDecodeTimer stop, decodeNum:{decodeNum} skipFirstDecodeSeries:{skipFirstDecodeSeries} NegoState:{WsjtxMessage.NegoState}");
            decodeNum++;
            decodeCycle = 0;
            DebugOutput($"{spacer}decodeCycle:{decodeCycle}");

            if (skipFirstDecodeSeries)
            {
                skipFirstDecodeSeries = false;
                oddOffset = 0;
                evenOffset = 0;
                audioOffsets.Clear();
                if (cachedOddOffset > 0) oddOffset = cachedOddOffset;
                if (cachedEvenOffset > 0) evenOffset = cachedEvenOffset;
            }
            else
            {
                //final calculation of best offset -- completion announcement itself now lives
                //inside CalcBestOffset (see its own comment), since this is only one of three
                //call sites and not reliably the one that first observes completion.
                if (CalcBestOffset(audioOffsets, period, true))       //calc for period when decodes started
                {
                    ctrl.freqCheckBox.Text = "Use best Tx frequency";
                    ctrl.freqCheckBox.ForeColor = Color.Black;
                }
                CalcAvgTimeOffset(true);
            }

            if (WsjtxMessage.NegoState != WsjtxMessage.NegoStates.RECD)
                return;

            if (ctrl.freqCheckBox.Checked)
            {
                if (!transmitting)
                {
                    if (!CheckActive())
                    {
                        //set/show frequency offset for Tx period
                        uint decodesCompletedOffset = AudioOffsetFromTxPeriod();
                        DebugOutput($"{Time()} [BAND-AUDIT] DecodesCompleted→cmd:10 sent: bandIdx:{bandIdx} offset:{decodesCompletedOffset}");
                        // Stage A4: build+send moved to WsjtxCompatibilityExtension.OptReq.
                        _compatExtension.OptReq(ctrl.skipGridCheckBox.Checked, ctrl.useRR73CheckBox.Checked, decodesCompletedOffset,
                            msg => DebugOutput($"{Time()} {msg}"));
                        if (settingChanged)
                        {
                            ctrl.WsjtxSettingConfirmed();
                            settingChanged = false;
                        }
                    }
                }
            }
            UpdateDebug();

            if (_callQueueStore.TrimAllCallDict())
            {
                DebugOutput(_callQueueStore.AllCallDictString());
            }
        }

        private void CheckCallQueuePeriod(bool tmpTxFirst)
        {
            bool removed = false;
            var calls = new List<string>();

            foreach (var entry in callDict)
            {
                var decode = entry.Value;
                if (IsEvenCall(decode) == tmpTxFirst)  //entry is wrong time period for new txFirst
                {
                    calls.Add(entry.Key);        //collect keys to delete
                }
            }

            //delete from callQueue
            foreach (string call in calls)
            {
                _callQueueStore.RemoveCall(call);
                removed = true;
            }

            if (removed) DebugOutput($"{spacer}CheckCallQueuePeriod: calls removed{nl}{_callQueueStore.CallQueueString()}");
        }

        private bool IsSameMessage(string tx, string lastTx)
        {
            if (tx == lastTx) return true;
            if (WsjtxMessage.ToCall(tx) != WsjtxMessage.ToCall(lastTx)) return false;
            if (WsjtxMessage.IsReport(tx) && WsjtxMessage.IsReport(lastTx)) return true;
            if (WsjtxMessage.IsRogerReport(tx) && WsjtxMessage.IsRogerReport(lastTx)) return true;
            return false;
        }

        internal void UpdateMaxTxRepeat()
        {
            int limit = (int)ctrl.timeoutNumUpDown.Value;

            if (!ctrl.optimizeCheckBox.Checked || _manualCallInProg)
            {
                maxTxRepeat = limit;
            }
            else
            {
                // Proportional reduction based on waiting-call depth.
                // Factor:  0–1 waiting → 100%,  2 → 75%,  3 → 50%,  4+ → 33%.
                // Integer truncation (floor) is used so the result is always ≤ limit
                // and never overshoots the target percentage.
                double factor;
                if      (callQueue.Count <= 1) factor = 1.0;
                else if (callQueue.Count == 2) factor = 0.75;
                else if (callQueue.Count == 3) factor = 0.5;
                else                           factor = 1.0 / 3.0;

                int reduced = Math.Max(1, (int)(limit * factor));
                maxTxRepeat = reduced;

                if (debug && reduced < limit)
                    DebugOutput($"{spacer}Optimize: maxTxRepeat {limit}→{reduced} ({callQueue.Count} calls waiting)");
            }

            UpdateMaxPrevTo();
            UpdateMaxAutoGenEnqueue();
        }

        //if low number of repeated msgs before timeout
        //allow calls to be re-queued more often     
        private void UpdateMaxPrevTo()
        {
            maxPrevTo = 2;
            if (maxTxRepeat == 3) maxPrevTo = 3;
            if (maxTxRepeat == 2) maxPrevTo = 4;
            if (maxTxRepeat == 1) maxPrevTo = 5;
            maxPrevPotaTo = Math.Min((int)(maxPrevTo * 1.5), 8);
        }

        public void UpdateMaxAutoGenEnqueue()
        {
            int baseVal = ctrl.maxQueuedCallsBase;
            maxAutoGenEnqueue = baseVal;
            if (maxTxRepeat == 3) maxAutoGenEnqueue = baseVal + 1;
            if (maxTxRepeat == 2) maxAutoGenEnqueue = baseVal + 2;
            if (maxTxRepeat == 1) maxAutoGenEnqueue = baseVal + 3;

            if (IsPrimarySort(RankMethods.CALL_ORDER) || IsPrimarySort(RankMethods.MOST_RECENT)) return;

            float cqFactor = ctrl.cqOnlyRadioButton.Enabled && ctrl.cqOnlyRadioButton.Checked ? 1.25f : 1.0f;
            float gridFactor = ctrl.cqGridRadioButton.Enabled && ctrl.cqGridRadioButton.Checked ? 1.35f : 1.0f;
            float anyFactor = ctrl.anyMsgRadioButton.Enabled && ctrl.anyMsgRadioButton.Checked ? 1.5f : 1.0f;
            float obFactor = ctrl.bandComboBox.Enabled && ctrl.bandComboBox.SelectedIndex == (int)WsjtxClient.NewCallBands.CURRENT ? 1.75f : 1.0f;
            float factor = cqFactor * gridFactor * anyFactor * obFactor;
            maxAutoGenEnqueue = (int)(maxAutoGenEnqueue * factor);
            maxAutoGenEnqueue = Math.Min(maxAutoGenEnqueue, ctrl.maxQueuedCallsBase);
        }

        private void DisableAutoFreqPause()
        {
            DebugOutput($"{Time()} DisableAutoFreqPause autoFreqPauseMode:{autoFreqPauseMode} consecCqCount:{consecCqCount} consecTimeoutCount:{consecTimeoutCount}");
            autoFreqPauseMode = autoFreqPauseModes.DISABLED;
            consecCqCount = 0;
            consecTimeoutCount = 0;
            consecTxCount = 0;
            UpdateCallInProg();
            UpdateDebug();
            DebugOutput($"{spacer}autoFreqPauseMode:{autoFreqPauseMode} consecCqCount:0 consecTimeoutCount:0 consecTxCount:0");
        }

        private string CallPriorityString(string call)
        {
            if (call == null) return "";

            return $"{call}:{Priority(call)}";
        }

        //for the specified call, return the priority, or default if not found
        //check allCallDict and replyDecode
        private int Priority(string call)
        {
            int priority = (int)CallPriority.DEFAULT;
            if (call == null || call == "CQ") return priority;

            EnqueueDecodeMessage msg = null;
            List<EnqueueDecodeMessage> msgList;
            if (allCallDict.TryGetValue(call, out msgList))
            {
                if (msgList.Count > 0)
                {
                    msg = msgList.Last();       //list is in chronological order, latest at end
                    priority = msg.Priority;
                }
            }
            else
            {
                if (replyDecode != null && callInProg != null && replyDecode.DeCall() == call) priority = replyDecode.Priority;
            }
            return priority;
        }

        //for the specified call, return the country, or empty string if not found
        //check allCallDict and replyDecode
        private string Country(string call)
        {
            string country = "";
            if (call == null || call == "CQ") return country;

            EnqueueDecodeMessage msg = null;
            List<EnqueueDecodeMessage> msgList;
            if (allCallDict.TryGetValue(call, out msgList))
            {
                if (msgList.Count > 0)
                {
                    msg = msgList.Last();       //list is in chronological order, latest at end
                    country = msg.EffectiveClassification().Country;
                }
            }
            else
            {
                if (replyDecode != null && callInProg != null && replyDecode.DeCall() == call)
                {
                    msg = replyDecode;
                    country = replyDecode.EffectiveClassification().Country;
                }
            }

            if (ctrl.showUsStateCheckBox.Checked && country == "USA" && msg != null)
            {
                // QRZ's cached state doesn't depend on this particular message carrying a
                // grid -- by the time a QSO is logged, msg is often the final 73/RR73 (no
                // grid in that message type), which previously skipped state resolution
                // entirely instead of falling back to QRZ. Same QRZ-first/grid.dat-fallback
                // priority as ResolveUsState's other call sites (BuildCallWaitingRow, Raw
                // Decodes row, IsHrcWasNeeded, MatchedAwardRuleId).
                string g = WsjtxMessage.Grid(msg.Message);
                string qrzState = null;
                if (lookupManager != null && lookupManager.Enabled)
                {
                    var rec = lookupManager.Build(call);
                    qrzState = rec.State;
                }
                string state = ResolveUsState(qrzState, g == null ? null : GridToUsState(g));
                if (state != null) country = state;
            }

            return country;
        }

        private void StartProcessDecodeTimer2()
        {
            if (processDecodeTimer2.Enabled || (mode != "FT8" && mode != "FT4")) return;
            processDecodeTimer2.Interval = (mode == "FT8" ? 1500 : 750);
            processDecodeTimer2.Start();
            DebugOutput($"{Time()} processDecodeTimer2 start");
        }

        private void StopDecodeTimers()
        {
            if (processDecodeTimer.Enabled)
            {
                processDecodeTimer.Stop();       //no xmit cycle now
                DebugOutput($"{Time()} processDecodeTimer stop");
            }
            if (processDecodeTimer2.Enabled)
            {
                processDecodeTimer2.Stop();
                DebugOutput($"{Time()} processDecodeTimer2 stop");
            }
        }

        //high priority call should log late (needs definite confirmation, not presumed/implied)
        private bool IsLogEarly(string deCall)
        {
            if (!ctrl.logEarlyCheckBox.Checked) return false;
            return Priority(deCall) > (int)CallPriority.NEW_COUNTRY_ON_BAND;
        }

        /*private bool SendUdp(byte[] ba)
        {
            if (udpClient2 == null) return false;

            try
            {
                udpClient2.Send(ba, ba.Length);
            }
            catch (Exception e)
            {
                DebugOutput($"{Time()} sendUdp error:{e.ToString()}");
                return false;
            }
            return true;
        }*/

        private DialogResult Confirm(string s)
        {
            var confDlg = new ConfirmDlg();
            confDlg.text = s;
            confDlg.Owner = ctrl;
            confDlg.ShowDialog();
            return confDlg.DialogResult;
        }

        private void SetupCq(bool enableTx)
        {
            //set/show frequency offset for period after decodes started
            // Stage A4: build+send moved to WsjtxCompatibilityExtension.OptReq.
            _compatExtension.OptReq(ctrl.skipGridCheckBox.Checked, ctrl.useRR73CheckBox.Checked, AudioOffsetFromTxPeriod(),
                msg => DebugOutput($"{Time()} {msg}"));
            if (settingChanged)
            {
                ctrl.WsjtxSettingConfirmed();
                settingChanged = false;
            }

            // Stage A4: build+send moved to WsjtxCompatibilityExtension.SetupCq.
            string cqMsg = $"CQ{NextDirCq()} {myCall} {myGrid}";
            _compatExtension.SetupCq(cqMsg, ctrl.skipGridCheckBox.Checked, ctrl.useRR73CheckBox.Checked,
                msg => DebugOutput($"{Time()} {msg}"));
            qsoState = WsjtxMessage.QsoStates.CALLING;      //in case enqueueing call manually right now
            replyCmd = null;        //invalidate last reply cmd since not replying
            replyDecode = null;
            curCmd = cqMsg;
            newDirCq = false;           //if set, was processed here
            DebugOutput($"{spacer}qsoState:{qsoState} (was {lastQsoState} replyCmd:'{replyCmd}') newDirCq:{newDirCq}");

            if (enableTx) EnableTx();             //sets WSJT-X "Enable Tx" button state
        }

        private void SetPeriodState()
        {
            DateTime dtNow = DateTime.UtcNow;
            DebugOutput($"{Time()} SetPeriodState, dtNow:{dtNow.ToString("HHmmss.fff")} trPeriod:{trPeriod}");
            period = IsEvenPeriod((dtNow.Minute * 60) + dtNow.Second) ? Periods.EVEN : Periods.ODD;       //determine this period
            DebugOutput($"{spacer}period:{period}");
        }

        private void LogBeep()
        {
            if (!debug) return;
            Console.Beep();
            DebugOutput($"{spacer}BEEP");
        }

        //get (and remove) call/msg at specified index in queue;
        //queue not assumed to have any entries;
        //return null if failure
        private string GetCall(int idx, out EnqueueDecodeMessage dmsg)
        {
            dmsg = null;
            if (callQueue.Count == 0)
            {
                DebugOutput($"{spacer}not exists idx:{idx}");
                return null;
            }

            var callArray = callQueue.ToArray();
            string call = callArray[idx];

            if (!callDict.TryGetValue(call, out dmsg))
            {
                DebugOutput("ERROR: '{call}' not found");
                UpdateDebug();
                return null;
            }

            _callQueueStore.RemoveCall(call);

            DebugOutput($"{spacer}call:{call}: msg:'{dmsg.Message}'");
            return call;
        }

        private void ReplyTo(int queueIdx)
        {
            var dmsg = new EnqueueDecodeMessage();
            string nCall = GetCall(queueIdx, out dmsg);
            DebugOutput($"{Time()} ReplyTo, queueIdx:{queueIdx} nCall:'{nCall}'");
            ReplyTo(dmsg);
        }

        private void ReplyTo(EnqueueDecodeMessage dmsg)
        {
            if (dmsg == null)
            {
                DebugOutput($"{Time()} ReplyTo, error: msg not is null");
                return;
            }

            string nCall = dmsg.DeCall();
            string toCall = WsjtxMessage.ToCall(dmsg.Message);
            DebugOutput($"{Time()} ReplyTo, nCall:'{nCall}' toCall:{toCall}");

            if (WsjtxMessage.IsCQ(dmsg.Message))                  //save the grid for logging
            {
                AddAllCallDict(nCall, dmsg);
            }

            //set call options
            // Stage A4: build+send moved to WsjtxCompatibilityExtension.OptReq.
            _compatExtension.OptReq(dmsg.UseStdReply ? false : ctrl.skipGridCheckBox.Checked, ctrl.useRR73CheckBox.Checked, AudioOffsetFromMsg(dmsg),
                msg => DebugOutput($"{Time()} {msg}"));
            if (settingChanged)
            {
                ctrl.WsjtxSettingConfirmed();
                settingChanged = false;
            }

            //send Reply message
            var rmsg = new ReplyMessage();
            rmsg.SchemaVersion = WsjtxMessage.NegotiatedSchemaVersion;
            rmsg.Id = WsjtxMessage.UniqueId;
            rmsg.SinceMidnight = dmsg.SinceMidnight;
            rmsg.Snr = dmsg.Snr;
            rmsg.DeltaTime = dmsg.DeltaTime;
            rmsg.DeltaFrequency = dmsg.DeltaFrequency;
            rmsg.Mode = dmsg.Mode;
            rmsg.Message = dmsg.Message;
            rmsg.UseStdReply = dmsg.UseStdReply;
            ba = rmsg.GetBytes();
            udpClient2.Send(ba, ba.Length);
            replyCmd = dmsg.Message;            //save the last reply cmd to determine which call is in progress
            replyDecode = dmsg.DeepCopy();      //save the decode the reply cmd derived from
            curCmd = dmsg.Message;
            SetCallInProg(nCall);
            DebugOutput($"{Time()} >>>>>Sent 'Reply To Msg' cmd:{nl}{rmsg} lastTxMsg:'{lastTxMsg}'{nl}{spacer}replyCmd:'{replyCmd}'");
            //toCallTxStart = null to flag intentional interruption
            if (transmitting) SetTxStartInfo(DateTime.UtcNow, null);  //because tx-start event already happened

            EnableTx();             //also sets WSJT-X "Tx Enable" button state

            cqPaused = false;
            restartQueue = false;           //get ready for next decode phase
            txTimeout = false;              //ready for next timeout
            tCall = null;
            xmitCycleCount = 0;
            timedOutCall = null;
            UpdateDebug();
        }

        private void SetRank(EnqueueDecodeMessage d) =>
            Ranker.SetRank(d, debug ? (Action<string>)(s => DebugOutput($"{spacer}{s}")) : null);

        private string Reason(EnqueueDecodeMessage d)
        {
            switch (d.Priority)
            {
                case (int)CallPriority.NEW_COUNTRY:
                    return "New country";

                case (int)CallPriority.NEW_COUNTRY_ON_BAND:
                    return "New country on band";

                case (int)CallPriority.TO_MYCALL:
                    return $"Call to {myCall}";

                case (int)CallPriority.MANUAL_SEL:
                    return "Manually selected";

                case (int)CallPriority.WANTED_CQ:
                    return "Directed CQ";

                default:
                    return "Auto-selected";
            }

        }
        private bool IsCorrectTimePeriod(EnqueueDecodeMessage dmsg, DateTime dtNow)
        {
            bool res = true;
            bool evenCall = IsEvenCall(dmsg);
            bool evenPeriod = txFirst;          //CQ mode can only xmit on txFirst setting
                                                //"dtNow" will be shortly before the tx period following the decode period, at least once per cycle;
                                                //add 2 seconds to dtNow to assure that decision to reply is based on the tx period's time
            if (txMode == TxModes.LISTEN) evenPeriod = IsEvenPeriod((dtNow.Minute * 60) + dtNow.Second + 2);       //listen mode can xmit on either period depending on current time
            if (!dmsg.UseStdReply) res = evenCall != evenPeriod;      //reply is in opposite time period from msg
            DebugOutput($"{spacer}IsCorrectTimePeriod:{res} evenCall:{evenCall} evenPeriod:{evenPeriod} UseStdReply:{dmsg.UseStdReply}");
            return res;
        }

        private void UpdateDblClkTip()
        {
            if (callQueue.Count == 0) ShowQueue();      //update dbl-click tip
        }

        internal bool IsCorrectTimePeriodForMode(EnqueueDecodeMessage emsg)
        {
            // Advanced layout shows TX1 and TX2 lists simultaneously, so both periods
            // must be allowed into the queue regardless of current txFirst setting.
            if (ctrl.advancedCallLayout)
            {
                DebugOutput($"{Time()} IsCorrectTimePeriodForMode, advanced mode: accepting all periods");
                return true;
            }

            bool evenCall = IsEvenCall(emsg);
            bool res = false;
            DebugOutput($"{Time()} IsCorrectTimePeriodForMode, txMode:{txMode} txFirst:{txFirst} evenCall:{evenCall}");

            if (txMode == TxModes.CALL_CQ || ctrl.periodComboBox.SelectedIndex == (int)ListenModeTxPeriods.ANY)
            {
                res = (evenCall != txFirst);
            }
            else        //listen mode
            {
                res = (evenCall != (ctrl.periodComboBox.SelectedIndex == (int)ListenModeTxPeriods.EVEN));
            }
            DebugOutput($"{spacer}IsCorrectTimePeriodForMode:{res}");
            return res;
        }

        //return non-unique random message sequence number for ranking
        //numbers for previous decode always increase for next decode
        //int range -2,147,483,648 to 2,147,483,647, allows for 8388608 decode cycles before underflow
        private int NextMsgSeqNum()
        {
            int m = decodeNum * 256;            //assume max of 256 decodes per period
            return m + rnd.Next(0, 256);
        }

        private void SetTxStartInfo(DateTime dtNow, string toCall)
        {
            txBeginTime = dtNow;
            toCallTxStart = toCall;
            DebugOutput($"{spacer}txBeginTime:{txBeginTime.ToString("HHmmss.fff")} toCallTxStart:'{toCallTxStart}'");
        }

        private string CurrentDecodeCycleString()
        {
            if (!decoding) return "";
            return $"{decodeCycle}";
        }

        private int MaxTimeoutsForMsg(bool isPota)
        {
            DebugOutput($"{spacer}isPota:{isPota} maxPrevTo:{maxPrevTo} maxPrevPotaTo:{maxPrevPotaTo}");
            return (isPota || IsPrimarySort(RankMethods.MOST_RECENT)) ? maxPrevPotaTo : maxPrevTo;
        }

        //return true if call is (or associated with a previous) "CQ POTA"
        internal bool IsPotaCall(EnqueueDecodeMessage emsg)
        {
            if (emsg.IsCQ()) return emsg.IsPota();

            EnqueueDecodeMessage dmsg = CqMsg(emsg.DeCall());
            if (dmsg == null) return false;         //never a CQ POTA from deCall
            return dmsg.IsPota();
        }

        private string Spacify(string s)
        {
            if (s == null) return "";

            var a = s.ToArray();
            var sb = new StringBuilder();

            foreach (char c in a)
            {
                if (c != ' ')
                {
                    sb.Append(c);
                    sb.Append(' ');
                }

            }
            return sb.ToString().Trim();
        }

        private string SpacifyMsg(string msg)
        {
            var sa = msg.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (sa.Length < 2) return "";

            string pl = sa.Length >= 3 ? $", {Spacify(sa[2])}" : "";
            return $"{Spacify(sa[0])}, {Spacify(sa[1])}{pl}";
        }

        private string SpacifyPayload(string s)
        {
            if (s == null) return "";
            if (s == "" || s == "CQ" || s == "RRR") return s;
            if (s.Contains("-"))        //neg roger report
            {
                return s.Replace("-", " -");
            }
            if (s.Contains("+"))        //pos roger report
            {
                return s.Replace("+", " +");
            }
            if (s.Contains("73"))
            {
                return Spacify(s);
            }
            //grid
            if (s.Length == 4)
            {
                return s.Substring(0, 1) + " " + s.Substring(1, 1) + " " + s.Substring(2, 2);
            }
            else
            {
                return Spacify(s);
            }
        }

        // Public so other USA-state display sites (e.g. Controller.FormatSpotWatchRow)
        // can apply the exact same grid.dat fallback as the per-decode hot path.
        public static string GridToUsState(string grid)
        {
            string state;
            return UsGridStateMap.TryGetState(grid, out state) ? state : null;
        }

        // Shared priority rule for every US-state lookup site: prefer QRZ's cached
        // real state (precise, single state) over grid.dat's guess (a 4-char grid
        // square can straddle a state border, which grid.dat represents as a
        // compound string like "MN-WI" rather than a single state). gridState is
        // only used when QRZ has no cached answer for this call. Extracted so all
        // call sites share one implementation and it's directly unit-testable.
        public static string ResolveUsState(string qrzState, string gridState) =>
            !string.IsNullOrEmpty(qrzState) ? qrzState : gridState;

        private int CallQueuePriorityCount(CallPriority p)
        {
            int count = 0;
            foreach (var entry in callDict)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(entry.Key, callInProg)) continue;
                if (entry.Value.Priority == (int)p) count++;
            }
            return count;
        }

        private int SnapshotPriorityCount(CallPriority p, HashSet<string> visibleCalls)
        {
            if (visibleCalls == null) return CallQueuePriorityCount(p);
            int count = 0;
            foreach (var call in visibleCalls)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(call, callInProg)) continue;
                EnqueueDecodeMessage d;
                if (callDict.TryGetValue(call, out d) && d.Priority == (int)p) count++;
            }
            return count;
        }

        // Counts visible decodes by their "Needed" tag text (WAS Needed, DXCC Unconf, Zone
        // Needed, or a specific checked Rule Definition's own name + " Needed"), grouped by
        // exact tag text since several different awards can be checked/matched at once. Feeds
        // the periodic status summary so it names which award(s) have a station waiting, not
        // just a bare count -- mirrors SnapshotPriorityCount's visible-vs-all fallback, but
        // keys on Category + CategoryTag() instead of Priority, since these five categories
        // are only ever assigned in DeriveCategory()'s default case (Priority itself doesn't
        // distinguish them -- see DeriveCategory()'s comment on Category being separate from
        // Priority). Reuses CategoryTag() rather than a separate naming scheme so the status
        // bar never disagrees with what the row itself displays.
        private Dictionary<string, int> SnapshotNeededAwardCounts(HashSet<string> visibleCalls)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            IEnumerable<string> calls = visibleCalls;
            if (calls == null) calls = callDict.Keys;

            foreach (var call in calls)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(call, callInProg)) continue;
                EnqueueDecodeMessage d;
                if (!callDict.TryGetValue(call, out d)) continue;
                if (d.Category != CallCategory.WAS_NEEDED && d.Category != CallCategory.WAS_UNCONFIRMED &&
                    d.Category != CallCategory.DXCC_UNCONFIRMED &&
                    d.Category != CallCategory.ZONE_NEEDED && d.Category != CallCategory.STILL_NEEDED) continue;

                string tag = _awardTagger.CategoryTag(d);
                if (string.IsNullOrEmpty(tag)) continue;
                int n;
                counts.TryGetValue(tag, out n);
                counts[tag] = n + 1;
            }
            return counts;
        }

        private string PeekVisibleCall(out EnqueueDecodeMessage dmsg, HashSet<string> visibleCalls)
        {
            dmsg = null;
            foreach (string call in callQueue)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(call, callInProg)) continue;
                if (visibleCalls != null && !visibleCalls.Contains(call)) continue;
                callDict.TryGetValue(call, out dmsg);
                return call;
            }
            return null;
        }

        private void StartStatusTimer()
        {
            statusTimer.Stop();
            statusTimer.Interval = 250;
            statusTimer.Start();
        }

        private void DiscardCall()
        {
            if (callInProg == discardCall && ((txMode == TxModes.LISTEN && !txEnabled) || txMode == TxModes.CALL_CQ))
            {
                DebugOutput($"{Time()} DiscardCall: in effect, discardCall:'{discardCall}' discardCallCycleCount:{discardCallCycleCount}");
                if (txMode == TxModes.LISTEN) Pause(true, false);
                if (!transmitting) expiredCall = discardCall;
                SetCallInProg(null);
                DebugOutput($"{spacer}callInProg:'{callInProg}' expiredCall:'{expiredCall}' txTimeout:{txTimeout} xmitCycleCount:{xmitCycleCount} timedOutCall:'{timedOutCall}'");
            }
            else
            {
                DebugOutput($"{Time()} DiscardCall: not in effect (was discardCall:'{discardCall}' discardCallCycleCount:{discardCallCycleCount})");
            }

            discardCall = null;
            discardCallCycleCount = 0;
            DebugOutput($"{spacer} now discardCall:'{discardCall}' discardCallCycleCount:{discardCallCycleCount}");
        }

        private void CancelDiscardCall()
        {
            DebugOutput($"{Time()} CancelDiscardCall: (was '{discardCall}' discardCallCycleCount:{discardCallCycleCount})");
            discardCall = null;
            discardCallCycleCount = 0;
            DebugOutput($"{spacer} now discardCall:'{discardCall}' discardCallCycleCount:{discardCallCycleCount}");
        }

        private void StartDiscardCall(string call)      //or reset discard call
        {
            if (txMode == TxModes.CALL_CQ) return;

            discardCall = call;
            discardCallCycleCount = 0;
            DebugOutput($"{Time()} StartDiscardCall: discardCall:'{discardCall}' discardCallCycleCount:{discardCallCycleCount}");
        }

        private void StartStatusTimer2(bool uncond)
        {
            if (uncond)
            {
                statusTimer2.Stop();
                statusTimer2.Start();       //long tx/rx period, restore previous status
            }
        }

        private void UpdateCallQueue(string call, EnqueueDecodeMessage dmsg)
        {
            if (!ctrl.cqOnlyRadioButton.Checked || dmsg.ToCall() == myCall) return;

            if (dmsg.ToCall() != myCall && dmsg.Quality < (int)EnqueueDecodeMessage.Qualities.MEDIUM)
            {
                _callQueueStore.RemoveCall(call);
                if (debugDetail) DebugOutput($"{Time()} UpdateCallQueue: removed call:'{call}' msg:{dmsg.Message} quality:{dmsg.Quality}");
            }
        }

    }
}

