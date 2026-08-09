namespace WSJTX_Controller
{
    // Which source supplies radio state (frequency/mode/PTT/S-meter/power/SWR): WsjtxCat means
    // radio state comes from the native engine's own StatusMessage broadcasts (receive-only --
    // no separate CAT link); HamlibRigctld means Jimmy/the native engine opens a real serial CAT
    // connection via Hamlib's rigctld. RadioSettings.Mode is the live, user-facing setting.
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
}
