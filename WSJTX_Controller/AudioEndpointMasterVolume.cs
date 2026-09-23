using System;
using NAudio.CoreAudioApi;

namespace WSJTX_Controller
{
    // Wraps Windows' Core Audio ENDPOINT (device) master volume (IAudioEndpointVolume, via
    // NAudio's MMDevice.AudioEndpointVolume) -- the SAME master level shown in Windows Sound
    // settings for the device itself, affecting every application that uses it. Distinct from
    // ProcessAudioSessionVolume.cs's per-application Volume Mixer session control, which scales
    // only jimmy-engine-host.exe's own session. See OptionsDlg.cs's "Radio input/output master
    // level percent" controls (Options > Decode Engine) and Controller.
    // ApplyRadioMasterAudioLevels for the only callers.
    public static class AudioEndpointMasterVolume
    {
        // 0.0-1.0, or null if the device can't be resolved. isRender: true = output/speakers
        // (DataFlow.Render), false = input/microphone (DataFlow.Capture). deviceName matches the
        // same FriendlyName string NativeEngineClient's own device-name CLI args use; empty/null
        // = system default.
        public static float? GetVolume(string deviceName, bool isRender)
        {
            using (var device = FindDevice(deviceName, isRender))
                return device?.AudioEndpointVolume?.MasterVolumeLevelScalar;
        }

        public static bool SetVolume(string deviceName, bool isRender, float volume)
        {
            using (var device = FindDevice(deviceName, isRender))
            {
                if (device?.AudioEndpointVolume == null) return false;
                device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Max(0.0f, Math.Min(1.0f, volume));
                return true;
            }
        }

        // Same name-match + default-device-fallback convention as ProcessAudioSessionVolume's
        // own FindSession.
        private static MMDevice FindDevice(string deviceName, bool isRender)
        {
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
                    return device;
                }
            }
            catch
            {
                // Best-effort: a device disappearing mid-call, or any other Core Audio COM
                // hiccup, should read back as "not available", never throw into the UI.
                return null;
            }
        }
    }
}
