using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WSJTX_Controller
{
    // Sound playback subsystem, extracted from WsjtxClient (Phase 2.5 of the modernization
    // plan). Bodies moved verbatim -- this was already almost fully self-contained; the only
    // external dependency was Controller.soundsEnabled (the master mute flag), now passed in
    // as a Func<bool> so this class has no reference to Controller/WinForms at all.
    public class NotificationSounds
    {
        private readonly Func<bool> _soundsEnabled;
        private readonly Queue<string> _soundQueue = new Queue<string>();

        [DllImport("winmm.dll", SetLastError = true)]
        static extern bool PlaySound(string pszSound, UIntPtr hmod, uint fdwSound);

        [Flags]
        private enum SoundFlags
        {
            /// <summary>play synchronously (default)</summary>
            SND_SYNC = 0x0000,
            /// <summary>play asynchronously</summary>
            SND_ASYNC = 0x0001,
            /// <summary>silence (!default) if sound not found</summary>
            SND_NODEFAULT = 0x0002,
            /// <summary>pszSound points to a memory file</summary>
            SND_MEMORY = 0x0004,
            /// <summary>loop the sound until next sndPlaySound</summary>
            SND_LOOP = 0x0008,
            /// <summary>don't stop any currently playing sound</summary>
            SND_NOSTOP = 0x0010,
            /// <summary>Stop Playing Wave</summary>
            SND_PURGE = 0x40,
            /// <summary>don't wait if the driver is busy</summary>
            SND_NOWAIT = 0x00002000,
            /// <summary>name is a registry alias</summary>
            SND_ALIAS = 0x00010000,
            /// <summary>alias is a predefined id</summary>
            SND_ALIAS_ID = 0x00110000,
            /// <summary>name is file name</summary>
            SND_FILENAME = 0x00020000,
            /// <summary>name is resource name or atom</summary>
            SND_RESOURCE = 0x00040004
        }

        // Sound files by name -> full path, refreshed at startup and whenever Options closes --
        // kept in memory so every sound lookup is a dictionary check, not a disk hit, even when
        // trying several drop-in-file candidates per alert. 2026-09-28: shipped sounds live in
        // Resources\Sounds\ (SoundsFolder); files already dropped straight into Resources\ by
        // an earlier version still resolve, Sounds\ winning on a name clash.
        private Dictionary<string, string> _soundFiles;

        public static string SoundsFolder =>
            Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Resources", "Sounds");

        // The operator's own sounds (2026-10-01): %LOCALAPPDATA%\Jimmy Next\Sounds -- one place for
        // them, no administrator rights needed. Looked in first, so a file there wins over a
        // shipped one of the same name; callsign and award sound files work from here too.
        // Imported customizations put their sound files here.
        public static string UserSoundsFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Assembly.GetExecutingAssembly().GetName().Name, "Sounds");

        public NotificationSounds(Func<bool> soundsEnabled)
        {
            _soundsEnabled = soundsEnabled;
            RefreshResourceFileCache();
            // Its own background thread, not a thread-pool Task (2026-09-30): this loop never
            // returns, so as a Task it held a pool thread for good -- one more with every client a
            // profile switch creates, and hundreds across the test suite, starving the Direct
            // command sender. Stop() ends it when the window closes.
            new Thread(ProcSoundQueue) { IsBackground = true, Name = "Jimmy sound queue" }.Start();
        }

        private volatile bool _stopped;

        // The window is closing (WsjtxClient.StopTimersForClose): end the queue loop.
        public void Stop() => _stopped = true;

        public void RefreshResourceFileCache()
        {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string dir in new[] { UserSoundsFolder, SoundsFolder, Path.GetDirectoryName(SoundsFolder) })
                    if (Directory.Exists(dir))
                        foreach (string f in Directory.GetFiles(dir))
                            if (!files.ContainsKey(Path.GetFileName(f))) files[Path.GetFileName(f)] = f;
            }
            catch { }
            _soundFiles = files;
        }

        // Tries, in order: a callsign-specific file (e.g. KG4CCG.wav), a rule/category-key
        // -specific file (e.g. WAS.wav, NEW_COUNTRY.wav), then the file configured in
        // Options -- the only behavior that existed before this. All three are looked up in the
        // sound folders above; no Options UI needed for the first two.
        private string ResolveSoundPath(string name, string callsign, string key)
        {
            var files = _soundFiles;
            if (files == null) { RefreshResourceFileCache(); files = _soundFiles; }

            if (!string.IsNullOrEmpty(callsign)
                && files.TryGetValue(SanitizeSoundFileName(callsign) + ".wav", out string byCall)) return byCall;
            if (!string.IsNullOrEmpty(key)
                && files.TryGetValue(SanitizeSoundFileName(key) + ".wav", out string byKey)) return byKey;

            if (string.IsNullOrEmpty(name)) return null;
            if (Path.IsPathRooted(name))
            {
                if (File.Exists(name)) return name;
                // A full path picked with Assign that no longer exists (e.g. a shipped sound that
                // moved into Resources\Sounds\) still finds the file by its name.
                name = Path.GetFileName(name);
            }
            return files.TryGetValue(name, out string byName) ? byName : null;
        }

        private static string SanitizeSoundFileName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s;
        }

        public void Play(string strFileName)
        {
            Play(strFileName, null, null);
        }

        public void Play(string strFileName, string callsign, string key)
        {
            string resolved = ResolveSoundPath(strFileName, callsign, key);
            if (resolved == null) return;
            LogQueued(resolved, callsign, key);
            _soundQueue.Enqueue(resolved);
        }

        // One debug-log line per sound queued (operator, 2026-10-02: "so we can tell which sound
        // you heard"). Set by the owner; null = no log.
        public Action<string> Log;

        private void LogQueued(string file, string callsign, string key)
        {
            if (Log == null) return;
            string why = string.Join(", ", new[] { key, callsign }.Where(x => !string.IsNullOrEmpty(x)));
            try { Log($"{DateTime.Now:HH:mm:ss} sound: {Path.GetFileName(file)}{(why.Length > 0 ? " (" + why + ")" : "")}"); }
            catch { }
        }

        public bool PlaySoundEvent(bool enabled, string file)
        {
            return PlaySoundEvent(enabled, file, null, null);
        }

        // callsign/key let a more specific drop-in sound file win over the configured
        // default -- see ResolveSoundPath. Returns whether a sound actually resolved and
        // was queued (not merely whether one was nominally configured), so callers that
        // fall back to a generic sound when this returns false never go silent just
        // because a configured file went missing.
        public bool PlaySoundEvent(bool enabled, string file, string callsign, string key)
        {
            if (!_soundsEnabled() || !enabled) return false;
            string resolved = ResolveSoundPath(file, callsign, key);
            if (resolved == null) return false;
            LogQueued(resolved, callsign, key);
            _soundQueue.Enqueue(resolved);
            return true;
        }

        public void TestPlaySound(string file)
        {
            if (string.IsNullOrEmpty(file)) return;
            Play(file);
        }

        private void ProcSoundQueue()
        {
            while (!_stopped)
            {
                if (_soundQueue.Count > 0)
                {
                    string waveFileName = _soundQueue.Peek();
                    _soundQueue.Dequeue();
                    if (!string.IsNullOrEmpty(waveFileName))
                    {
                        PlaySound(waveFileName, UIntPtr.Zero,
                            (uint)(SoundFlags.SND_FILENAME | SoundFlags.SND_ASYNC | SoundFlags.SND_NODEFAULT));
                        string baseName = Path.GetFileNameWithoutExtension(waveFileName).ToLower();
                        if (baseName == "beepbeep" || baseName == "blip")
                            Thread.Sleep(200);
                        else
                            Thread.Sleep(650);
                    }
                }

                Thread.Sleep(100);
            }
        }
    }
}
