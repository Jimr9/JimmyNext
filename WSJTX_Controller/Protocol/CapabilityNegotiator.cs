namespace WSJTX_Controller
{
    // Stage A5 (Jimmy_Master_Migration_Roadmap.md / Architecture Blueprint §8, §18, §19):
    // replaces acceptableWsjtxVersions' hardcoded version-string allowlist with a
    // runtime capability probe. Any WSJT-X build that completes the standard Heartbeat
    // exchange is accepted; whether the non-standard Compatibility Layer (Andy WM8Q's
    // fork / EnableTxMessage sub-command surface) is also present is tracked here as a
    // separate, non-blocking concern.
    //
    // Pure state-transition logic, no socket/UI dependency, so it's unit-testable in
    // isolation. Does not send or receive anything itself, and does not change how the
    // existing sub-command 7 ("Ack Req") probe works -- WsjtxClient still owns sending
    // cmd:7 and comparing StatusMessage.Check against its own cmdCheck string exactly
    // as before (reserved for Stage A7's handshake redesign). This class only tracks
    // what that existing exchange's outcome means for the connection's overall state.
    public enum WsjtxCapabilityState
    {
        Disconnected,
        Negotiating,        // standard Heartbeat exchange in progress
        CapabilityProbing,  // cmd:7 sent, awaiting echo, bounded timeout running
        Connected,           // standard protocol confirmed; Compatibility Layer absent (degraded)
        ConnectedFull         // standard protocol + Compatibility Layer both confirmed
    }

    public class CapabilityNegotiator
    {
        public WsjtxCapabilityState State { get; private set; } = WsjtxCapabilityState.Disconnected;

        // Standard Heartbeat exchange has started (any build reaching this point is
        // accepted -- no version-string check).
        public void BeginNegotiating() => State = WsjtxCapabilityState.Negotiating;

        // Schema negotiated; the existing cmd:7 Ack Req has just been sent.
        public void BeginCapabilityProbe() => State = WsjtxCapabilityState.CapabilityProbing;

        // WSJT-X echoed cmd:7's CmdCheck back -- Compatibility Layer confirmed present.
        public void ConfirmCompatibilityLayer() => State = WsjtxCapabilityState.ConnectedFull;

        // Bounded timeout elapsed with no echo -- degrade gracefully instead of
        // refusing to run. A no-op once already ConnectedFull (a late echo that arrived
        // just before this fired still wins).
        public void TimeoutToDegraded()
        {
            if (State != WsjtxCapabilityState.ConnectedFull) State = WsjtxCapabilityState.Connected;
        }

        // WSJT-X closed / heartbeat lost / re-init. Capability state is never cached
        // across a connection boundary -- the next connection re-probes from scratch.
        public void Reset() => State = WsjtxCapabilityState.Disconnected;
    }
}
