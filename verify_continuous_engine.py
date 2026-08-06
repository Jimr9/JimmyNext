"""One-off manual verification for Phase 4g: launches Jimmy in the same isolated test-mode DB
used by run_replay_tests.bat (no real logbook/network touched), launches the now-continuous
jimmy-engine-host as a long-running child process pointed at a real radio, lets it run for a few
real FT8 periods, and uses JimmyReplay.py's own JimmyVerifier (UI Automation over the live
window) to confirm Jimmy's queue keeps growing with real natively-decoded off-air stations --
proving the SERVICE shape (not just a one-shot demo) works end to end. Not part of the permanent
suite. RECEIVE ONLY throughout -- the engine host never asserts PTT or transmits.
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
ENGINE_HOST_EXE = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                "EngineHost", "target", "release", "jimmy-engine-host.exe")
AUDIO_DEVICE = "Microphone (USB Audio CODEC )"
RUN_SECONDS = 40

def main():
    if not os.path.exists(JIMMY_EXE):
        print(f"ERROR: {JIMMY_EXE} not found. Build Jimmy first.")
        return 1
    if not os.path.exists(ENGINE_HOST_EXE):
        print(f"ERROR: {ENGINE_HOST_EXE} not found. Build EngineHost first.")
        return 1

    test_db = os.path.join(tempfile.gettempdir(), "JimmyContinuousEngine_logbook.db")
    if os.path.exists(test_db):
        os.remove(test_db)
    env = dict(os.environ)
    env["JIMMY_TEST_DB_PATH"] = test_db
    print(f"Starting Jimmy.exe in test mode (JIMMY_TEST_DB_PATH={test_db})...")
    jimmy_proc = subprocess.Popen([JIMMY_EXE], cwd=os.path.dirname(JIMMY_EXE), env=env)
    engine_proc = None
    try:
        time.sleep(5)

        if not jr.ensure_jimmy_udp_ready():
            print("FAIL: Jimmy never opened its UDP port.")
            return 1

        v = jr.JimmyVerifier()
        if not v.available:
            print("FAIL: could not find Jimmy's window/controls.")
            return 1

        print(f"Launching continuous jimmy-engine-host against '{AUDIO_DEVICE}' for {RUN_SECONDS}s...")
        engine_env = dict(os.environ)
        engine_env["PATH"] = r"C:\msys64\ucrt64\bin;" + engine_env.get("PATH", "")
        engine_proc = subprocess.Popen(
            [ENGINE_HOST_EXE, "--mycall", "KB0UZT", "--mygrid", "FN42", "--device", AUDIO_DEVICE],
            env=engine_env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
        )

        v.check_active("Jimmy went ACTIVE from the continuous engine host")
        time.sleep(RUN_SECONDS)

        items = v.queue_items()
        print(f"\nJimmy's queue after {RUN_SECONDS}s of continuous real decoding: {len(items)} row(s)")
        for i in items[:10]:
            print(f"  {i}")
        v._report(len(items) > 0, "Jimmy's queue populated from the continuous engine host", f"{len(items)} rows")

        return 0 if v.summary() else 1
    finally:
        if engine_proc is not None:
            engine_proc.terminate()
            try:
                engine_proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                engine_proc.kill()
            out, err = engine_proc.communicate()
            if out:
                print("\n--- engine host stdout (tail) ---")
                print("\n".join(out.splitlines()[-15:]))
            if err:
                print("--- engine host stderr ---")
                print(err)
        jimmy_proc.terminate()
        try:
            jimmy_proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            jimmy_proc.kill()

if __name__ == "__main__":
    sys.exit(main())
