namespace WSJTX_Controller
{
    // Which FT8/FT4 decode/encode DSP source Jimmy uses. Orthogonal to CapabilityNegotiator:
    // CapabilityNegotiator answers "does the WSJT-X-family process I'm connected to also speak
    // the WM8Q Compatibility Layer?" and only matters when Mode == WsjtxExternal. This enum
    // answers a different, static-configuration question -- "which engine am I even talking
    // to?" -- and is not itself a live negotiation state.
    public enum DecodeEngineMode
    {
        WsjtxExternal,  // today's behavior: external WSJT-X-family process over UDP
        JimmyNative,    // Jimmy's own bundled/native engine (Phase 4)
    }

    // Only meaningful when DecodeEngineMode == JimmyNative. See Phase 4 plan notes: a separate
    // engine-host process is recommended for crash isolation (the vendored modem is explicitly
    // not thread-safe and keeps process-global state), with in-process P/Invoke kept available
    // as a simpler fallback.
    public enum EngineProcessModel
    {
        InProcess,
        SeparateProcess,
    }

    // Which source supplies radio state (frequency/mode/PTT/S-meter/power/SWR). Independent of
    // DecodeEngineMode -- an operator can combine either decode engine with either radio-control
    // source. WsjtxCat is charted-out in RadioSettings as the live, user-facing setting (Phase 1);
    // this enum just names the two possible values.
    public enum RadioControlMode
    {
        WsjtxCat,
        HamlibRigctld,
    }

    // How PTT is actually keyed -- mirrors Nexus's own tempo_audio::rig::PttMode 1:1
    // (Cat/Vox/Serial{Rts,Dtr}), since the native engine host (Phase 5) builds a real Rig
    // directly from this value's own --ptt-method string. Only meaningful under
    // RadioControlMode.HamlibRigctld; SerialRts/SerialDtr key the SAME serial port CAT uses
    // (the single-cable interface case -- see rigctld_proc.rs's own doc comment on this) rather
    // than a separate dedicated PTT port, matching what a typical single-USB-cable rig needs.
    public enum PttMethod
    {
        Cat,
        Vox,
        SerialRts,
        SerialDtr,
    }

    public static class PttMethodExtensions
    {
        // The exact string tempo_audio::service::RadioConfig.ptt_method / jimmy-engine-host's
        // own --ptt-method argument expects verbatim.
        public static string ToCliString(this PttMethod method) => method switch
        {
            PttMethod.Cat => "cat",
            PttMethod.Vox => "vox",
            PttMethod.SerialRts => "rts",
            PttMethod.SerialDtr => "dtr",
            _ => "vox",
        };
    }

    // Emergency rollback valve for DecodeEngineMode/EngineProcessModel, shaped identically to
    // Classification/ClassificationCutover.cs's UseClassificationEngine flag: INI-only,
    // intentionally undocumented and not exposed in OptionsDlg, default unchanged from today's
    // behavior. Only meant to be hand-edited in the .ini file if a real-world Phase 4 edge case
    // surfaces -- promote to a real Options control only once Phase 4 has enough field validation
    // to justify it. RadioControlMode is different: it's a real, user-facing Phase 1 setting and
    // lives in RadioSettings, not here.
    public static class EngineModeCutover
    {
        public static DecodeEngineMode Mode = DecodeEngineMode.WsjtxExternal;
        public static EngineProcessModel ProcessModel = EngineProcessModel.SeparateProcess;
    }
}
