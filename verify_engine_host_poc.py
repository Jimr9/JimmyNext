"""One-off manual verification for Phase 4c's proof-of-concept: launches Jimmy in the same
isolated test-mode DB used by run_replay_tests.bat (no real logbook/network touched), runs the
Rust jimmy-engine-host proof-of-concept binary against it (which sends real WSJT-X-protocol
Heartbeat/Status/Decode UDP datagrams produced from a genuinely native-decoded FT8 signal, not a
canned string), and uses JimmyReplay.py's own JimmyVerifier (UI Automation over the live window)
to confirm Jimmy's existing, unmodified pipeline actually displayed it. Not part of the permanent
suite -- run_replay_tests.bat / JimmyReplay.py remain the real regression harness; this script
exists only to prove this one architectural milestone, and can be deleted once Phase 4 is done.
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

def main():
    if not os.path.exists(JIMMY_EXE):
        print(f"ERROR: {JIMMY_EXE} not found. Build Jimmy first.")
        return 1
    if not os.path.exists(ENGINE_HOST_EXE):
        print(f"ERROR: {ENGINE_HOST_EXE} not found. Build EngineHost first.")
        return 1

    test_db = os.path.join(tempfile.gettempdir(), "JimmyEngineHostPoc_logbook.db")
    if os.path.exists(test_db):
        os.remove(test_db)
    env = dict(os.environ)
    env["JIMMY_TEST_DB_PATH"] = test_db
    print(f"Starting Jimmy.exe in test mode (JIMMY_TEST_DB_PATH={test_db})...")
    proc = subprocess.Popen([JIMMY_EXE], cwd=os.path.dirname(JIMMY_EXE), env=env)
    try:
        time.sleep(5)

        if not jr.ensure_jimmy_udp_ready():
            print("FAIL: Jimmy never opened its UDP port.")
            return 1

        v = jr.JimmyVerifier()
        if not v.available:
            print("FAIL: could not find Jimmy's window/controls.")
            return 1

        print("Running jimmy-engine-host proof-of-concept binary...")
        engine_env = dict(os.environ)
        engine_env["PATH"] = r"C:\msys64\ucrt64\bin;" + engine_env.get("PATH", "")
        result = subprocess.run([ENGINE_HOST_EXE], capture_output=True, text=True, env=engine_env)
        print(result.stdout)
        if result.returncode != 0:
            print("FAIL: jimmy-engine-host itself reported failure (see output above).")
            print(result.stderr)
            return 1

        v.check_active("Jimmy went ACTIVE from engine-host Heartbeat/Status")
        v.check_queue_contains("K1ABC", "Natively-decoded 'CQ K1ABC FN20' appears in Jimmy's call queue")

        return 0 if v.summary() else 1
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()

if __name__ == "__main__":
    sys.exit(main())
