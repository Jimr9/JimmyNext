"""Phase 4h regression check: closes the gap the self-sufficiency plan's testing notes call out
-- JimmyReplay.py's 19 existing groups prove "Jimmy reacts correctly to a given decode message,"
but every one of those messages is a hand-crafted WSJT-X UDP datagram, never something that
actually went through real encode -> real native decode (the audio->decode path didn't exist in
Jimmy before Phase 4). This script sends Jimmy the SAME message group12_hrc_filter_baseline's T26
already covers ("CQ G3HRC IO91"), but sourced from EngineHost's examples/synth_send.rs -- a real
ft8::encode() -> ft8::decode_frame() round-trip, using tempo-net's own WSJT-X UDP encoder for the
wire format -- and reuses JimmyReplay.py's own JimmyVerifier to assert the same outcome T26
checks (the call queues correctly). Deterministic, no live radio needed (see
tests/roundtrip.rs for why: a known synthesized message always decodes back to itself).

Not part of the permanent suite by itself, but demonstrates the pattern run_replay_tests.bat's
19 groups could each be extended with, per the plan's own recommendation, without needing to
rewrite all of them in one pass. RECEIVE ONLY -- synth_send never asserts PTT or transmits audio,
it only sends UDP datagrams to Jimmy's own listener, exactly like every other replay-test group.
"""
import os
import subprocess
import sys
import tempfile
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import JimmyReplay as jr

JIMMY_EXE = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                          "WSJTX_Controller", "bin", "Debug", "net10.0-windows", "Jimmy.exe")
SYNTH_SEND_EXE = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                               "EngineHost", "target", "release", "examples", "synth_send.exe")
HRC_DX_CALL = "G3HRC"  # same test fixture JimmyReplay.py's group12_hrc_filter_baseline uses

def main():
    if not os.path.exists(JIMMY_EXE):
        print(f"ERROR: {JIMMY_EXE} not found. Build Jimmy first.")
        return 1
    if not os.path.exists(SYNTH_SEND_EXE):
        print(f"ERROR: {SYNTH_SEND_EXE} not found. Build EngineHost examples first "
              f"(cargo build --release --example synth_send).")
        return 1

    test_db = os.path.join(tempfile.gettempdir(), "JimmyNativeDecodeRegression_logbook.db")
    if os.path.exists(test_db):
        os.remove(test_db)
    env = dict(os.environ)
    env["JIMMY_TEST_DB_PATH"] = test_db
    print(f"Starting Jimmy.exe in test mode (JIMMY_TEST_DB_PATH={test_db})...")
    jimmy_proc = subprocess.Popen([JIMMY_EXE], cwd=os.path.dirname(JIMMY_EXE), env=env)
    try:
        time.sleep(5)
        if not jr.ensure_jimmy_udp_ready():
            print("FAIL: Jimmy never opened its UDP port.")
            return 1

        v = jr.JimmyVerifier()
        if not v.available:
            print("FAIL: could not find Jimmy's window/controls.")
            return 1

        print(f"Running synth_send: real encode -> real native decode of 'CQ {HRC_DX_CALL} IO91'...")
        engine_env = dict(os.environ)
        engine_env["PATH"] = r"C:\msys64\ucrt64\bin;" + engine_env.get("PATH", "")
        result = subprocess.run(
            [SYNTH_SEND_EXE, f"CQ {HRC_DX_CALL} IO91"],
            capture_output=True, text=True, env=engine_env,
        )
        print(result.stdout)
        if result.returncode != 0:
            print("FAIL: synth_send itself reported failure.")
            print(result.stderr)
            return 1

        # Same assertion JimmyReplay.py's group12 T26 makes for this exact message.
        v.check_queue_contains(HRC_DX_CALL, f"T26-equivalent: {HRC_DX_CALL} queued, sourced from a real native decode")

        return 0 if v.summary() else 1
    finally:
        jimmy_proc.terminate()
        try:
            jimmy_proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            jimmy_proc.kill()

if __name__ == "__main__":
    sys.exit(main())
