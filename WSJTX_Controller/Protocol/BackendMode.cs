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
