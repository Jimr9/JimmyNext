using System;


namespace WSJTX_Controller
{
    // Nexus 1.25's audio-error line (tempo-app dto.rs RadioStatus.audio_error / audio_error_kind),
    // said by ear (2026-11-22, operator):
    //  - No receive audio: a sound card that opens but delivers nothing, or one that failed and is
    //    being reopened. Nexus reopens it by itself; Jimmy says it once when it starts and "back"
    //    when it ends, so a blind operator is never left wondering why nothing decodes.
    //  - PTT not accepted / not confirmed: Nexus's sentence says what to check first and what the
    //    radio answered. A refused FT over is already said ("Radio did not transmit"); the two are
    //    merged so one failure is said once, with Nexus's advice in it.
    // Nexus's own words are used as the detail. Nothing here takes focus.
    public partial class WsjtxClient
    {
        internal const string KindNoReceiveAudio = "noReceiveAudio";
        internal const string KindSoundCard = "soundCard";
        internal const string KindPtt = "ptt";
        internal const string KindPttUnconfirmed = "pttUnconfirmed";
        // Nexus's line while a reopened card has not delivered yet: on the way back, not news.
        internal const string ReopenedWaitingPrefix = "Audio device reopened";
        // How close a PTT line and a refused over must be to be the same failure.
        internal static readonly TimeSpan PttMergeWindow = TimeSpan.FromSeconds(5);
        // How long a PTT line waits for its refused over before it is said on its own.
        internal static readonly TimeSpan PttStandaloneDelay = TimeSpan.FromSeconds(2);

        private bool _rxAudioProblemSaid;
        private string _pttLineSeen;          // the PTT line of the current episode
        private bool _pttLineSaid;
        private DateTime _pttLineSeenUtc;
        private DateTime _keyRefusalSaidUtc = DateTime.MinValue;

        internal static bool IsReceiveAudioKind(string kind) =>
            kind == KindNoReceiveAudio || kind == KindSoundCard;
        internal static bool IsPttKind(string kind) =>
            kind == KindPtt || kind == KindPttUnconfirmed;

        // The receive-audio half, once per poll. Returns what was said (tests), or null.
        internal string ApplyReceiveAudioLine(string kind, string line)
        {
            if (IsReceiveAudioKind(kind))
            {
                if (_rxAudioProblemSaid) return null;
                // A card just reopened and waiting for its first samples is on its way back; a
                // real problem says something else first, or next.
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith(ReopenedWaitingPrefix, StringComparison.Ordinal)) return null;
                _rxAudioProblemSaid = true;
                string title = kind == KindSoundCard ? "Sound card" : "No receive audio";
                DebugOutput($"{Time()} [AUDIO] {kind}: {line}");
                Notify?.Publish(new ErrorWarningEvent(ErrorSeverity.Error, title, line.Trim()));
                return title + ": " + line.Trim();
            }
            if (_rxAudioProblemSaid)
            {
                _rxAudioProblemSaid = false;
                DebugOutput($"{Time()} [AUDIO] receive audio back");
                Notify?.Publish(new ErrorWarningEvent(ErrorSeverity.Warning, "Receive audio", "back."));
                return "Receive audio: back.";
            }
            return null;
        }

        // The PTT half, once per poll, after the refused-over announcement. Returns what was said
        // on its own (tests), or null.
        internal string ApplyPttLine(string kind, string line, DateTime nowUtc)
        {
            if (!IsPttKind(kind) || string.IsNullOrWhiteSpace(line))
            {
                _pttLineSeen = null;
                _pttLineSaid = false;
                return null;
            }
            if (!string.Equals(line, _pttLineSeen, StringComparison.Ordinal))
            {
                _pttLineSeen = line;
                _pttLineSeenUtc = nowUtc;
                // Already said inside a refused over just now.
                _pttLineSaid = nowUtc - _keyRefusalSaidUtc <= PttMergeWindow;
            }
            if (_pttLineSaid || nowUtc - _pttLineSeenUtc < PttStandaloneDelay) return null;
            _pttLineSaid = true;
            string title = kind == KindPttUnconfirmed ? "PTT not confirmed" : "PTT not accepted";
            DebugOutput($"{Time()} [RADIO] {kind}: {line}");
            Notify?.Publish(new ErrorWarningEvent(ErrorSeverity.Error, title, line.Trim()));
            return title + ": " + line.Trim();
        }

        // A refused FT over is being said: Nexus's PTT line, when there is one, is its detail, and
        // that line is then not said again on its own.
        internal string PttLineForKeyRefusal(string kind, string line, DateTime nowUtc)
        {
            _keyRefusalSaidUtc = nowUtc;
            if (!IsPttKind(kind) || string.IsNullOrWhiteSpace(line)) return null;
            _pttLineSeen = line;
            _pttLineSeenUtc = nowUtc;
            _pttLineSaid = true;
            return line.Trim();
        }
    }
}
