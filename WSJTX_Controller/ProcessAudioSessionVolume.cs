using System;
using NAudio.CoreAudioApi;

namespace WSJTX_Controller
{
    // Wraps Windows' Core Audio per-application session volume (ISimpleAudioVolume, via NAudio)
    // so Jimmy can read/set jimmy-engine-host.exe's own OS-level output and input session volume
    // directly, without the operator needing to find it themselves in the Windows Volume Mixer --
    // confirmed live, 2026-08-09: the process shows up there under its own raw, unfriendly
    // filename ("jimmy-engine-host.exe"), not "Jimmy"/"Jimmy Test", since it has no embedded
    // Windows version resource. This is a SEPARATE, real, functional control from
    // Engine::set_mic_gain (F11/F12): mic_gain scales the waveform digitally before the engine
    // ever hands samples to Windows; this scales AGAIN, afterward, as part of Windows' own
    // render/capture session mixing. Both genuinely affect what reaches the radio, just at
    // different points in the chain. Per Microsoft's own docs, ISimpleAudioVolume applies to any
    // shared-mode session, capture or render -- the classic Volume Mixer UI only ever surfacing a
    // render slider is a UI limitation, not an API one.
    public static class ProcessAudioSessionVolume
    {
        // 0.0-1.0, or null if the process isn't running, hasn't opened a session on that device
        // yet, or the device can't be resolved. isRender: true = output/speakers (DataFlow.Render),
        // false = input/microphone (DataFlow.Capture). deviceName matches the same FriendlyName
        // string NativeEngineClient's own device-name CLI args use; empty/null = system default.
        public static float? GetVolume(int processId, string deviceName, bool isRender)
        {
            using (var session = FindSession(processId, deviceName, isRender))
                return session?.SimpleAudioVolume.Volume;
        }

        public static bool SetVolume(int processId, string deviceName, bool isRender, float volume)
        {
            using (var session = FindSession(processId, deviceName, isRender))
            {
                if (session == null) return false;
                session.SimpleAudioVolume.Volume = Math.Max(0.0f, Math.Min(1.0f, volume));
                return true;
            }
        }

        private static AudioSessionControl FindSession(int processId, string deviceName, bool isRender)
        {
            if (processId <= 0) return null;
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    DataFlow flow = isRender ? DataFlow.Render : DataFlow.Capture;
                    MMDevice device = null;

                    if (!string.IsNullOrWhiteSpace(deviceName))
                    {
                        foreach (var d in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
                        {
                            if (d.FriendlyName == deviceName) { device = d; break; }
                            d.Dispose();
                        }
                    }
                    if (device == null)
                        device = enumerator.GetDefaultAudioEndpoint(flow, Role.Console);
                    if (device == null) return null;

                    using (device)
                    {
                        var sessions = device.AudioSessionManager.Sessions;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            var s = sessions[i];
                            if (s.GetProcessID == (uint)processId) return s;
                            s.Dispose();
                        }
                    }
                    return null;
                }
            }
            catch
            {
                // Best-effort: a device disappearing mid-call, no session yet, or any other Core
                // Audio COM hiccup should read back as "not available", never throw into the UI.
                return null;
            }
        }
    }
}
