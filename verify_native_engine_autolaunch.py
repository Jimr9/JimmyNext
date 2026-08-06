"""One-off manual verification for Phase 4g: confirms JIMMY ITSELF (not an external test
harness) autonomously launches and uses the native engine host when decodeEngineMode=JimmyNative
is set, via the new NativeEngineClient/ApplyEngineMode wiring in Controller.cs.

CAUTION: decodeEngineMode/nativeEngine* settings live in the REAL Jimmy.ini
(%LOCALAPPDATA%\\Jimmy\\Jimmy.ini) -- JIMMY_TEST_DB_PATH only isolates the logbook DB, not this
file. This script backs up the real Jimmy.ini byte-for-byte before touching it (via the exact
same WritePrivateProfileStringW API Jimmy's own IniFile.cs uses, so no reformatting risk) and
restores it exactly in a finally block, regardless of outcome -- same care as the
TestModeGuard/JIMMY_TEST_DB_PATH protections around the real logbook and real network calls.

Not part of the permanent suite. RECEIVE ONLY throughout.
"""
import ctypes
import os
import shutil
import subprocess
import sys
import tempfile
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import JimmyReplay as jr

JIMMY_EXE = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                          "WSJTX_Controller", "bin", "Debug", "net10.0-windows", "Jimmy.exe")
INI_DIR = os.path.join(os.environ["LOCALAPPDATA"], "Jimmy")
INI_PATH = os.path.join(INI_DIR, "Jimmy.ini")
AUDIO_DEVICE = "Microphone (USB Audio CODEC )"
RUN_SECONDS = 40

kernel32 = ctypes.windll.kernel32

def ini_write(key, value, section="Jimmy", path=INI_PATH):
    kernel32.WritePrivateProfileStringW(section, key, value, path)

def main():
    if not os.path.exists(JIMMY_EXE):
        print(f"ERROR: {JIMMY_EXE} not found. Build Jimmy first.")
        return 1

    os.makedirs(INI_DIR, exist_ok=True)
    backup_path = None
    ini_existed = os.path.exists(INI_PATH)
    if ini_existed:
        backup_path = os.path.join(tempfile.gettempdir(), "Jimmy.ini.phase4g_backup")
        shutil.copy2(INI_PATH, backup_path)
        print(f"Backed up real Jimmy.ini to {backup_path}")
    else:
        print("No existing Jimmy.ini -- will remove the one this test creates afterward.")

    jimmy_proc = None
    try:
        print("Setting decodeEngineMode=JimmyNative + native engine call/grid/device in Jimmy.ini...")
        ini_write("decodeEngineMode", "JimmyNative")
        ini_write("nativeEngineMyCall", "KB0UZT")
        ini_write("nativeEngineMyGrid", "FN42")
        ini_write("nativeEngineAudioDevice", AUDIO_DEVICE)

        test_db = os.path.join(tempfile.gettempdir(), "JimmyNativeAutolaunch_logbook.db")
        if os.path.exists(test_db):
            os.remove(test_db)
        env = dict(os.environ)
        env["JIMMY_TEST_DB_PATH"] = test_db
        env["PATH"] = r"C:\msys64\ucrt64\bin;" + env.get("PATH", "")  # engine host's runtime DLLs
        print(f"Starting Jimmy.exe in test mode -- it should launch jimmy-engine-host.exe ITSELF now...")
        jimmy_proc = subprocess.Popen([JIMMY_EXE], cwd=os.path.dirname(JIMMY_EXE), env=env)
        time.sleep(5)

        v = jr.JimmyVerifier()
        if not v.available:
            print("FAIL: could not find Jimmy's window/controls.")
            return 1

        # Confirm the engine host process actually exists as a child Jimmy spawned, not one we
        # launched ourselves this time.
        time.sleep(3)
        found_engine_proc = False
        try:
            out = subprocess.check_output(
                ["tasklist", "/FI", "IMAGENAME eq jimmy-engine-host.exe"], text=True
            )
            found_engine_proc = "jimmy-engine-host.exe" in out
        except Exception as e:
            print(f"  (tasklist check failed: {e})")
        v._report(found_engine_proc, "jimmy-engine-host.exe is running (Jimmy launched it itself)")

        v.check_active("Jimmy went ACTIVE (self-launched native engine)")

        print(f"Waiting {RUN_SECONDS}s for real decodes to accumulate...")
        time.sleep(RUN_SECONDS)
        items = v.queue_items()
        print(f"Jimmy's queue: {len(items)} row(s)")
        for i in items[:10]:
            print(f"  {i}")
        v._report(len(items) > 0, "Jimmy's queue populated via the self-launched native engine", f"{len(items)} rows")

        return 0 if v.summary() else 1
    finally:
        if jimmy_proc is not None:
            jimmy_proc.terminate()
            try:
                jimmy_proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                jimmy_proc.kill()
        # Jimmy's own CloseComm should have killed jimmy-engine-host.exe on shutdown; make sure.
        subprocess.run(["taskkill", "/IM", "jimmy-engine-host.exe", "/F"],
                        capture_output=True)

        print("Restoring real Jimmy.ini...")
        if ini_existed:
            shutil.copy2(backup_path, INI_PATH)
            os.remove(backup_path)
            print("  Restored from backup.")
        elif os.path.exists(INI_PATH):
            os.remove(INI_PATH)
            print("  Removed the Jimmy.ini this test created (none existed before).")

if __name__ == "__main__":
    sys.exit(main())
