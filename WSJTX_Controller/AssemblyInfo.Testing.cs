using System.Runtime.CompilerServices;

// Lets JimmyTests call internal test-only hooks directly (e.g. WsjtxClient.Direct.cs's
// TestApplyDirectSnapshot) without widening anything to public. Test-only surface stays
// internal either way -- this just grants JimmyTests' own assembly the same visibility
// any other class in this assembly already has.
[assembly: InternalsVisibleTo("JimmyTests")]
