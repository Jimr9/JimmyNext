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
        // udpClient/ipAddress/multicast (the classic UDP receive-socket's own identity) and
        // WsjtxProtocolAdapter itself were removed 2026-08-18 -- nothing left ever opens a UDP
        // socket, in production or in test mode. `port` stays: it is still genuinely read
        // (Controller.ApplyEngineMode's own jimmyPort) to build the real engine host's
        // --jimmy-addr argument -- see NativeEngineClient.Launch's own comment on why that
        // outbound WSJT-X-protocol announce itself was NOT touched in this pass (a separate,
        // Rust/EngineHost-side question flagged for the project owner, not a C#-side transport
        // question).
        public int port;
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
        private bool wsjtxTxEnableButton = false;
        private bool transmitting = false;
        // Tracks `transmitting` across ShowStatus() calls specifically (not the same as
        // DirectApplyStatus's own local transmittingChanged, which only covers Direct mode) --
        // lets ShowStatus() detect "this is the first render right after Tx ended" so it can
        // avoid announcing a just-unsuppressed TX1/TX2 side's count as "0 available stations"
        // before that side has had a chance to actually reflect the real queue. See
        // WsjtxClient.Display.cs's ShowStatus() for the consuming logic.
        private bool _wasTransmittingLastShowStatus = false;
        private bool decoding = false;
        private WsjtxMessage.QsoStates qsoState = WsjtxMessage.QsoStates.CALLING;
        private string mode = "";
        private bool modeSupported = true;
        internal bool txFirst = false;
        internal int? trPeriod = null;       //msec
        private ulong dialFrequency = 0;
        private int? bandIdx = null;

        // Self-sufficiency plan: BandUp/BandDown are RELATIVE (current band +/- 1), unlike the
        // Alt+1-0 direct-band hotkeys (SelectBand), which are absolute and don't care whether
        // bandIdx is current. Under Jimmy Native + a real CAT-controlled radio, bandIdx only
        // updates once a full round-trip confirms it (retune -> radio physically moves -> the
        // engine's next poll cycle reads the new frequency -> next Status message -> line ~743
        // updates bandIdx) -- typically a few seconds. Without tracking a pending target
        // separately, a second Alt+PageUp pressed before that round-trip completes recomputes
        // from the SAME stale bandIdx and just re-requests the same band again, which reads as
        // "the hotkey did nothing" (confirmed live, 2026-08-07: Alt+1-0 "fire right away" while
        // Alt+PageUp "will do nothing for a while"). Tracks the last REQUESTED index so repeated
        // presses can keep advancing optimistically; cleared the moment a real confirmed bandIdx
        // arrives (line ~743) so it never drifts from reality for long.
        private int? _pendingBandIdx = null;
        private List<int> bands = new List<int>() { 160, 80, 60, 40, 30, 20, 17, 15, 12, 10, 6 };
        // Read-only view for Options > Frequencies (OptionsDlg.cs) -- index-aligned with
        // freqsDict's own per-mode lists and FrequencySettings' override arrays.
        internal IReadOnlyList<int> BandsMeters => bands;
        private Dictionary<string, List<int>> freqsDict = new Dictionary<string, List<int>>(){
            {"FT8", new List<int>(){ 1840, 3573, 5357, 7074, 10136, 14074, 18100, 21074, 24915, 28074, 50313 }},
            {"FT4", new List<int>(){ 1840, 3575, 5357, 7047, 10140, 14080, 18104, 21140, 24919, 28180, 50318 }}};
        // Read-only view for Options > Frequencies -- the built-in default (pre-override) values
        // shown when a band has no operator override, and restored by "Restore all to defaults".
        internal IReadOnlyDictionary<string, IReadOnlyList<int>> FreqsDictDefaults =>
            freqsDict.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int>)kv.Value);
        private UInt32 txOffset = 0;
        private string replyCmd = null;     //no "reply to" cmd sent to WSJT-X yet, will not be a CQ
        private string curCmd = null;       //cmd last issed, can be CQ
        private EnqueueDecodeMessage replyDecode = null;
        public string callInProg = null;
        // The callsign callInProg is heard actually working (a report/roger-report/73/RR73
        // addressed to someone else), and a short verb describing that stage ("working" or
        // "signing with") -- lets ShowStatus() mention it so the operator knows callInProg is
        // busy with another station instead of just going quiet. Captured in ProcessDecodeMsg
        // (before AddAllCallDict's own to-me-or-CQ-only filter would otherwise discard the
        // decode entirely) and cleared whenever callInProg changes or replies to us again.
        private string otherPartyForCallInProg = null;
        private string otherPartyStage = null;
        private bool restartQueue = false;

        private WsjtxMessage.QsoStates lastQsoState = WsjtxMessage.QsoStates.INVALID;
        private string dxCall = null;
        private ulong? lastDialFrequency = null;
        private string lastTxMsg = null;
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

        private int xmitCycleCount = 0;
        private bool txTimeout = false;
        private bool newDirCq = false;
        private int specOp = 0;
        private string lCall = null;            //last call sign logged
        private string tCall = null;            //call sign being processed at timeout or completed
        private string txMsg = null;            //msg for the most-recent Tx
        internal List<string> logList = new List<string>();      //calls logged for current mode/band for this session

        // Dedup guard for a QSO logged via Direct mode's own real completion detection
        // (DirectApplyStatus's curTxMsg/callInProg/Is73orRR73 -> LogQso -> RequestLog,
        // WsjtxClient.Direct.cs/WsjtxClient.cs) -- RequestLog's own repeated-poll-tick calls
        // (Qso.TxNow can keep reporting the same completed exchange for several seconds) must
        // only actually write the database/trigger uploads once per real QSO. Keyed by the same
        // callsign/band/mode/date/time dedup key LogbookDb already uses. Until 2026-08-18 this
        // also deduped between WSJT-X's own QsoLoggedMessage/LoggedAdifMessage wire messages
        // (HandleLiveQsoLogged/HandleLiveAdifLogged, WsjtxClient.Uploads.cs) -- both removed
        // along with the rest of the classic UDP transport; this set's only remaining purpose is
        // the one above.
        private readonly HashSet<string> _liveLoggedQsoKeys = new HashSet<string>();
        private bool ClaimLiveLoggedQso(string dedupKey) =>
            string.IsNullOrEmpty(dedupKey) || _liveLoggedQsoKeys.Add(dedupKey);
        private static uint defaultAudioOffset = 1500;
        private string failReason = "Failure reason: Unknown";
        public static int wsjtxRevision;
        public static int wsjtxTestVer;
        public static int lastWsjtx270RcRevision = 185;

        public const int maxQueueLines = 8, maxQueueWidth = 19, maxLogWidth = 9;
        private WsjtxMessage msg = new UnknownMessage();
        private Random rnd = new Random();
        DateTime firstDecodeTime;
        internal const string spacer = "           *";
        private const int freqChangeThreshold = 200;
        private bool skipFirstDecodeSeries = true;
        // Batches the "N available stations" announcement while decodes are still arriving
        // for the current period -- see ShowStatus()'s own comment for why. Interval is set
        // fresh (from ctrl.statusBatchDelayMs) each time it's (re)started.
        private System.Windows.Forms.Timer statusAnnounceTimer = new System.Windows.Forms.Timer();
        private string _pendingStatusHeading = null;
        private string _pendingStatusText = null;
        private Color _pendingStatusForeColor;
        private Color _pendingStatusBackColor;
        private System.Windows.Forms.Timer processDecodeTimer2 = new System.Windows.Forms.Timer();
        private System.Windows.Forms.Timer statusTimer = new System.Windows.Forms.Timer();
        private System.Windows.Forms.Timer statusTimer2 = new System.Windows.Forms.Timer();
        public System.Windows.Forms.Timer dialogTimer2 = new System.Windows.Forms.Timer();
        public System.Windows.Forms.Timer dialogTimer3 = new System.Windows.Forms.Timer();
        private bool _requireOffsetForActive = false;   // true after a band change; keeps CheckActive from firing before WSJT-X settles
        // Test-mode isolation gap found live, 2026-08-07: this path backs both the plain-text
        // diagnostic log (WsjtxClient.Debug.cs's SetLogFileState) and pota.txt (PotaLogTracker)
        // -- neither was ever gated by TestModeGuard the way the QSO logbook DB and radio
        // control already are, so every replay-test run with diag logging enabled wrote
        // synthetic test traffic straight into the operator's real log_<date>.txt. Same
        // JIMMY_TEST_DB_PATH signal TestModeGuard already reuses from LogbookDb -- one
        // isolated folder now covers this path too.
        internal string path = TestModeGuard.IsTestMode
            ? $"{Path.GetTempPath()}JimmyReplayTest_AppData"
            : $"{Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}\\{Assembly.GetExecutingAssembly().GetName().Name.ToString()}";
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
        // Notification/accessibility architecture (see WSJTX_Controller/Notify/): the ONLY
        // entry point business logic should call is Notify.Publish(event) -- never build a
        // sentence and call StatusView.ShowMessage directly for anything migrated onto this.
        // Independent of and complementary to Sounds above (a WAV cue and a spoken
        // notification for the same real-world event are two separate, separately-configured
        // concerns, not merged).
        public NotificationCenter Notify;
        public LiveQsoUploadOrchestrator LiveQsoUploader;
        private readonly PotaLogTracker _potaLog;
        private readonly AwardTagger _awardTagger;
        private readonly CallQueueStore _callQueueStore;
        string toCallTxStart = null;
        DateTime txBeginTime = DateTime.MaxValue;
        bool shortTx = false;
        bool metricUnits = false;
        private int decodeNum = 0;
        private const int maxCheckTxRepeat = 2;
        private int decodeCycle = 0;
        private bool decodesProcessed = false;
        internal bool debugDetail = false;
        // Governs TrimAllCallDict (CallQueueStore.cs) -- how long allCallDict (the internal
        // per-callsign "everything heard from this station" history feeding LogQso/Country
        // lookups) keeps an entry with no activity. Checked 2026-08-18: this has been a plain
        // hardcoded constant since Jimmy's very first commit, never read from or written to any
        // .ini setting or Options control. Deliberately NOT the same knob as Controller.
        // maxCallQueueAgePeriods (Options > General "Max call-queue age (periods)") -- that one
        // is period-count-based, operator-configurable, and governs TrimCallQueue's pruning of
        // the visible operator-facing call queue (callQueue/callDict), an entirely different
        // data structure for an entirely different concern (queue staleness for the operator to
        // act on, vs. how long Jimmy privately remembers a station for its own logging lookups).
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
        // Which period (even/odd) the most recently processed decode actually belongs to,
        // per that decode's own SinceMidnight -- not "what time is it right now". A decode
        // for the period that just ended can genuinely get processed a moment after the
        // next period's wall-clock window has already started (WSJT-X's own decode-compute
        // latency), so ShowStatus() must not re-derive "which side to announce" from the
        // current clock -- see ShowStatus()'s currentSideIsTx1 for the live-confirmed bug
        // this caused (2026-08-07: an announcement partway into the next period, using that
        // period's clock-parity before it had actually decoded anything). Null until the
        // first decode of the session arrives.
        private bool? lastDecodeEvenPeriod = null;
        private string uploadResult = null;
        private string tuneResult = null;
        private string timedOutCall = null;
        private string curTxMsg = null;
        private bool newSelection = false;
        private int decodeCount = 0;
        private int consecNoDecodes = 0;
        private int maxNoDecodes = 4;
        private List<double> timeOffsets = new List<double>();
        private double timeOffset = 0;
        private double maxTimeOffset = 1.20;
        // Clock-sync notification, 2026-08-12: null = not yet evaluated this session (the
        // clock's actual condition is unknown, not assumed good) -- see CalcAvgTimeOffset
        // (WsjtxClient.BandAudio.cs) for the transition-detection logic this backs. Deliberately
        // ONE shared threshold (maxTimeOffset above) for every mode: that field already existed
        // as a single, unconditional-on-mode value before this feature, already governing the
        // pre-existing ShowStatus() "check clock time" text (WsjtxClient.Display.cs) for
        // whatever real operating this app has seen -- inventing a second, FT4-specific number
        // now, with no engine-level evidence FT4 actually needs a tighter one, would be exactly
        // the "blindly choose a threshold" this feature was told not to do.
        private bool? _clockWasAcceptable;
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

        public WsjtxClient(Controller c, int reqPort, bool reqDebug, bool reqLog, WsjtxClient.TxModes tMode)
        {
            ctrl = c;           //used for accessing/updating UI
            StatusView = c;
            QueueView = c;
            LogView = c;
            _potaLog = new PotaLogTracker(this);
            _awardTagger = new AwardTagger(this);
            _callQueueStore = new CallQueueStore(this);
            Sounds = new NotificationSounds(() => ctrl.soundsEnabled);
            Notify = new NotificationCenter(ctrl.Notifications,
                new UiaAlertNotificationDelivery(new StatusViewNotificationDelivery(StatusView), StatusView,
                    () => ctrl.announceImportantAlertsWhenFocusElsewhere));
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
                    EqslUploadEnabled = ctrl.eqslUploadEnabled,
                    EqslUploadRealtime = ctrl.eqslUploadRealtime,
                    EqslUsername = ctrl.eqslUsername,
                    EqslPassword = ctrl.eqslPassword,
                },
                notifyImported: () => ctrl.BeginInvoke(new Action(() =>
                {
                    ctrl.RefreshStillNeedCache();
                    ctrl.RefreshLogbookWindowIfOpen();
                })),
                debugLog: msg => DebugOutput($"{Time()} {msg}"),
                showStatus: (msg, sound) => ctrl.BeginInvoke(new Action(() => ctrl.ShowUploadStatus(msg, sound))),
                resolveUsState: call => lookupManager?.Build(call)?.State);
            port = reqPort;
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

            ctrl.verLabel.Text = $"by KB0UZT v{fileVer}";
            ctrl.verLabel2.Text = "Check for update";

            UpdateModeSelection();
            UpdateModeVisible();

            firstDecodeTime = DateTime.MinValue;

            statusAnnounceTimer.Tick += new System.EventHandler(StatusAnnounceTimerTick);

            processDecodeTimer2.Tick += new System.EventHandler(ProcessDecodeTimer2Tick);

            statusTimer.Tick += new System.EventHandler(StatusTimerTick);

            statusTimer2.Tick += new System.EventHandler(StatusTimer2Tick);       //restore previous status
            statusTimer2.Interval = 5000;

            dialogTimer2.Tick += new System.EventHandler(dialogTimer2_Tick);
            dialogTimer2.Interval = 20;

            dialogTimer3.Tick += new System.EventHandler(dialogTimer3_Tick);
            dialogTimer3.Interval = 20;

            _potaLog.Read();

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
            // Set unconditionally, before any early return below -- even a decode that gets
            // rejected still tells ShowStatus() which period is actually current right now.
            lastDecodeEvenPeriod = IsEvenCall(dmsg);

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

            // Root-caused live, 2026-08-11: AddAllCallDict only ever keeps a decode addressed to
            // myCall or a CQ, so a callInProg decode heard working someone ELSE was silently
            // discarded before ShowStatus() could ever mention it -- the operator got no
            // indication callInProg was busy with another station, just unexplained silence.
            // Captured here, unconditionally, before that filter ever runs. Cleared the moment
            // callInProg replies to us again (the "someone else" info is stale at that point) or
            // callInProg itself changes (SetCallInProg).
            if (callInProg != null && string.Equals(deCall, callInProg, StringComparison.OrdinalIgnoreCase))
            {
                if (toMyCall)
                {
                    otherPartyForCallInProg = null;
                }
                else if (!dmsg.IsCQ() && toCall != null)
                {
                    otherPartyForCallInProg = toCall;
                    otherPartyStage = dmsg.Is73orRR73() ? "signing with" : "working";
                }
            }

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

        // StartProcessDecodeTimer() and CheckMyCall(StatusMessage) were removed 2026-08-18:
        // StartProcessDecodeTimer's only caller was the dead ProcessTxStart (removed above);
        // CheckMyCall's only callers were inside the dead classic UDP dispatcher
        // (WsjtxClient.Protocol.cs) -- Direct mode learns myCall/myGrid from ConnectDirectEngine's
        // own parameters instead (WsjtxClient.Direct.cs), never from a parsed StatusMessage.

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
                        if (settingChanged)
                        {
                            ctrl.WsjtxSettingConfirmed();
                            settingChanged = false;
                        }
                    }

                    if (newDirCq)
                    {
                        string cqMsg = $"CQ{NextDirCq()} {myCall} {myGrid}";
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

        // ProcessDecodes() -- the classic UDP dispatcher's own "receive period just ended" driver
        // (queue-age expiry, discard-count tracking, CheckNextXmit dispatch, the "resume after
        // timeout" ReplyTo(idx) auto-select, and the callAddedCheckBox chime) -- was removed
        // 2026-08-18. Only ever called from ProcessDecodeTimerTick, itself only ever started from
        // the now-deleted dispatcher. Direct mode's own per-period-boundary equivalent (queue-age
        // expiry, discard tracking, the Tx-hold safety net's recovery half) lives in
        // DirectApplyDecodes' own new-slot detection instead (WsjtxClient.Direct.cs) -- already
        // there. The "resume after timeout" auto-select has no Direct-mode equivalent because
        // Direct mode's whole call-selection model is operator-driven (double-click/Enter/Alt+N,
        // by accessible-app design, not an auto-CQ-answering bot) -- see WsjtxClient.Uploads.cs's
        // own comment on OnQsoLogged's removal for the fuller version of this same finding.

        // Schedules the "N available stations" summary ShowStatus() held back while this
        // period's decode window was still open (see ShowStatus()'s own comment). Computed
        // purely from the wall clock and trPeriod -- confirmed live, 2026-08-07, that this
        // real WSJT-X build reports Decoding:True once at startup and never flips back to
        // False again for the rest of the session, so decodeCycle/processDecodeTimer/
        // decodesProcessed (the app's existing "decode pass ended" machinery) never fire a
        // second time either -- relying on any of that left every deferred announcement
        // stuck pending forever (confirmed: total silence after the very first one). This
        // depends on nothing WSJT-X reports about its own decode state, only trPeriod
        // (already known once ACTIVE) and DateTime.UtcNow, so it can't go silently stale.
        //
        // A no-op if a countdown to the next boundary is already running -- only the FIRST
        // deferred call after a flush starts a fresh one; later calls in the same window
        // just update the pending text, so the wait is anchored to the period clock and
        // never gets pushed later by more decodes arriving.
        private void ScheduleStatusAnnounce()
        {
            if (statusAnnounceTimer.Enabled) return;
            if (trPeriod == null) return;
            DateTime dtNow = DateTime.UtcNow;
            int msec = (dtNow.Second * 1000) + dtNow.Millisecond;
            int diffMsec = msec % (int)trPeriod;
            int toBoundary = Math.Max(((int)trPeriod) - diffMsec, 1);
            int interval = toBoundary + Math.Max(0, ctrl.statusBatchDelayMs);
            // Sane upper bound -- purely a defensive clamp in case of an unexpected trPeriod
            // value; the arithmetic above should never actually reach this.
            interval = Math.Min(interval, (int)trPeriod * 2);
            statusAnnounceTimer.Interval = interval;
            statusAnnounceTimer.Start();
        }

        // ProcessTxStart()/ProcessTxEnd() -- the classic UDP dispatcher's own transmitting-
        // transition handlers (Tx-hold safety net, consecutive-CQ/timeout tracking, early/normal
        // QSO logging via LogQso below, xmit-cycle counting) -- were removed 2026-08-18. Only ever
        // called from the now-deleted dispatcher; Direct mode has had its own independent,
        // already-verified equivalent for every one of these since the 2026-08-12 UDP-to-Direct
        // parity pass (DirectApplyStatus/DirectApplyDecodes, WsjtxClient.Direct.cs -- Tx-hold
        // safety net, transmitting-edge Notify.OnTransmittingChanged, and its own curTxMsg/
        // callInProg/Is73orRR73 -> LogQso detection), not something this removal needs to add.

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
            statusAnnounceTimer.Stop();
            _pendingStatusText = null;
            decodeCycle = 0;
            decodeCount = 0;
            consecNoDecodes = 0;
            DebugOutput($"{Time()} ResetOpMode, decodeCycle:{decodeCycle}");
            ClearCalls(true);
            cqPaused = true;
            if (WsjtxMessage.NegoState != WsjtxMessage.NegoStates.WAIT) HaltTx();
            opMode = OpModes.IDLE;
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
            // UDP transport cleanup, 2026-08-18: heartbeatRecdTimer.Stop() and the udpClient2/
            // CloseAllUdp teardown that used to run here are removed -- both were dead (nothing
            // left ever starts heartbeatRecdTimer or opens udpClient2) even before this pass, and
            // WsjtxProtocolAdapter (CloseAllUdp's own implementation) is deleted entirely now.

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
            // AdifRecordBuilder.cs) instead of maintaining a separate hand-rolled field list here.
            // name/comment/tx_pwr/operator/exchange are passed empty -- WSJT-X's own
            // QsoLoggedMessage carries those (typed into WSJT-X's own logging dialog); Jimmy's
            // self-initiated log here has no equivalent source for them today.
            string adifRecord = AdifRecordBuilder.Build(
                call, band, (long)(dialFrequency + txOffset), mode,
                qsoDateOn, qsoTimeOn, qsoTimeOff, rstSent, rstRecd, grid,
                name: "", comment: "", txPwr: "", operatorCall: "",
                stationCall: myCall, myGrid: myGrid, qsoDateOff: qsoDateOff);

            // Jimmy has every field needed to record this Jimmy-initiated QSO itself, so it does
            // so directly here rather than depending on any round trip back from the engine.
            // ClaimLiveLoggedQso still dedupes against a QsoLoggedMessage/LoggedAdifMessage
            // confirmation if one arrives too.
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
            // Removed 2026-08-10: this used to also Notify.Publish(QsoCompletedEvent(...))
            // here ("Logged QSO with {call}"), as a standalone announcement independent of
            // ShowStatus()'s own status line. loggedCall (set just below) is read by
            // ShowStatus() (WsjtxClient.Display.cs) to weave "{call} logged" directly into
            // the SAME sentence as the rest of the QSO status -- confirmed live, 2026-08-10,
            // that having both meant this standalone announcement could land on top of /cut
            // off the status line's own real-time "received RR73" text for the same QSO.
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
                    // else branch (lastStatusTxMsg reset) removed 2026-08-18: lastStatusTxMsg
                    // was only ever read by the now-deleted classic UDP dispatcher's own
                    // "txMsg changed" detection -- nothing reads it anymore.

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
            otherPartyForCallInProg = null;
            otherPartyStage = null;
            UpdateDblClkTip();
            UpdateCallInProg();
        }

        public void EnableTx()
        {
            try
            {
                // UDP transport cleanup, 2026-08-18: the classic UDP path's own EnableTxMessage
                // send (gated on udpClient2 being open) is removed -- Direct is the only transport
                // left, in production and in test mode alike, so _directConnected is always true
                // by the time this is reachable. The "not connected yet" guard stays (a hotkey or
                // queue-reply action could still fire in the brief window before the first
                // ConnectDirectEngine call completes, or after a disconnect) -- same shape as
                // HaltTx's own guard below.
                if (_directConnected)
                {
                    DirectSetTxEnabled(true);
                    DebugOutput($"{Time()} EnableTx (direct)");
                }
                else
                {
                    DebugOutput($"{Time()} EnableTx skipped, not connected");
                    return;
                }

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
            DebugOutput($"{Time()} DisableTx, txEnabled:{txEnabled}");
            StopDecodeTimers();

            try
            {
                // Same UDP transport cleanup as EnableTx() above.
                if (_directConnected)
                {
                    DirectSetTxEnabled(false);
                    DebugOutput($"{Time()} DisableTx (direct)");
                }
                else
                {
                    DebugOutput($"{Time()} DisableTx skipped, not connected");
                    return;
                }

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

        public void HaltTuning()
        {
            if (tuning) HaltTx();
        }

        public void HaltTx()
        {
            StopDecodeTimers();
            tuning = false;
            // UDP transport cleanup, 2026-08-18: the classic UDP path's own standard HaltTx
            // message (msg type 8, gated on udpClient2 being open) is removed -- route through
            // the control port's own HALT_TX command instead. AutoOnly is explicitly false
            // either way: an emergency stop must halt regardless of whether the pending Tx was
            // auto-generated.
            if (_directConnected)
            {
                DirectSendHaltTx();
                DebugOutput($"{Time()} >>>>>Sent HALT_TX (direct)");
                txEnabled = false;
                wsjtxTxEnableButton = false;
                UpdateDblClkTip();
            }
            else
            {
                DebugOutput($"{Time()} HaltTx skipped, not connected");
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

        private void StatusAnnounceTimerTick(object sender, EventArgs e)
        {
            statusAnnounceTimer.Stop();
            if (_pendingStatusText == null) return;
            StatusView.RenderStatus(_pendingStatusHeading, _pendingStatusText, _pendingStatusForeColor, _pendingStatusBackColor);
            _pendingStatusText = null;
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

        // DecodesCompleted() (postDecodeTimer's own Tick target) was removed 2026-08-18 along
        // with the rest of the dead UDP decode-cycle-timing machinery -- postDecodeTimer.Start()
        // was only ever called from the now-deleted classic UDP dispatcher, so this could never
        // fire (confirmed true even before this cleanup pass: production has been Direct-only
        // since 2026-08-12, and nothing in WsjtxClient.Direct.cs ever started this timer either).
        // Direct mode's own equivalent per-period-boundary work (queue-age expiry via
        // TrimCallQueue, CalcAvgTimeOffset(true), and -- restored 2026-08-18 --
        // CallQueueStore.TrimAllCallDict's allCallDict aging, previously flagged here as an
        // unreferenced-since-the-cutover gap) lives in DirectApplyDecodes' own new-slot detection
        // instead (WsjtxClient.Direct.cs).

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
            if (settingChanged)
            {
                ctrl.WsjtxSettingConfirmed();
                settingChanged = false;
            }

            string cqMsg = $"CQ{NextDirCq()} {myCall} {myGrid}";
            qsoState = WsjtxMessage.QsoStates.CALLING;      //in case enqueueing call manually right now
            replyCmd = null;        //invalidate last reply cmd since not replying
            replyDecode = null;
            curCmd = cqMsg;
            newDirCq = false;           //if set, was processed here
            DebugOutput($"{spacer}qsoState:{qsoState} (was {lastQsoState} replyCmd:'{replyCmd}') newDirCq:{newDirCq}");

            if (enableTx) EnableTx();             //sets WSJT-X "Enable Tx" button state
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
            if (settingChanged)
            {
                ctrl.WsjtxSettingConfirmed();
                settingChanged = false;
            }

            //send Reply message
            // UDP transport cleanup, 2026-08-18: the classic UDP path's own standard
            // ReplyMessage send (msg type 4, gated on udpClient2 being open) is removed --
            // route through the control port's own REPLY command (Engine::call_station_ctx
            // directly) instead. replyMsg is the exact decoded text, same as the standard-
            // protocol Message field the removed branch used; call_station_ctx resolves
            // SNR/slot context from its own decode_history by matching that text (see
            // EngineHost/src/main.rs's ReplyArgs), so passing it through verbatim is the
            // correct, minimal mapping rather than trying to duplicate that lookup here.
            // Not-connected guard added here for the first time (the removed branch used to
            // unconditionally call udpClient2.Send, a real NullReferenceException risk if this
            // was ever reached before _directConnected went true) -- same shape as EnableTx/
            // DisableTx/HaltTx's own guards.
            if (_directConnected)
            {
                DirectSendReply(nCall, null, dmsg.Message, dmsg.Snr, null);
                DebugOutput($"{Time()} >>>>>Sent Reply (direct) nCall:'{nCall}' msg:'{dmsg.Message}'");
            }
            else
            {
                DebugOutput($"{Time()} ReplyTo skipped, not connected");
                return;
            }
            replyCmd = dmsg.Message;            //save the last reply cmd to determine which call is in progress
            replyDecode = dmsg.DeepCopy();      //save the decode the reply cmd derived from
            curCmd = dmsg.Message;
            SetCallInProg(nCall);
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
            // Case-insensitive: see DecodeMessage.IsCallTo's own comment -- myCall keeps
            // whatever case the operator typed, decoded wire text is always uppercase, and a
            // plain == here always missed a genuine match under Jimmy Native, which could
            // incorrectly remove a call actually directed at us on a low-quality decode.
            if (!ctrl.cqOnlyRadioButton.Checked || dmsg.ToCall().Equals(myCall, StringComparison.OrdinalIgnoreCase)) return;

            if (!dmsg.ToCall().Equals(myCall, StringComparison.OrdinalIgnoreCase) && dmsg.Quality < (int)EnqueueDecodeMessage.Qualities.MEDIUM)
            {
                _callQueueStore.RemoveCall(call);
                if (debugDetail) DebugOutput($"{Time()} UpdateCallQueue: removed call:'{call}' msg:{dmsg.Message} quality:{dmsg.Quality}");
            }
        }

    }
}

