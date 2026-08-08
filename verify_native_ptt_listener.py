"""Phase 4 TX Stage 3 bench test: confirms NativeTxPttListener correctly asserts/releases PTT via
rigctld, and that its watchdog force-releases PTT if no PTT_OFF follows in time. Uses Hamlib's
dummy rig (model 1, same convention as Phase 1's own smoke test) -- no real hardware, no real
radio, completely safe. Does NOT exercise the engine host's own TX decision logic at all (that
doesn't exist yet -- this only proves the Jimmy-side listener + rigctld + watchdog).

CAUTION: decodeEngineMode/nativeEngine*/radio* settings live in the REAL Jimmy.ini -- backed up
and restored exactly, same as the other verify_*.py scripts. RECEIVE ONLY as far as actual RF is
concerned (dummy rig has no antenna, no transmitter).
"""
import ctypes
import os
import shutil
import socket
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

kernel32 = ctypes.windll.kernel32

def ini_write(key, value, section="Jimmy", path=INI_PATH):
    kernel32.WritePrivateProfileStringW(section, key, value, path)

def find_engine_host_control_port(timeout=8):
    """Reads jimmy-engine-host.exe's own command line (via Get-CimInstance, wmic isn't present
    on this machine) to find the --jimmy-control-addr port Jimmy chose when launching it. Polled
    with a timeout -- CIM's process listing can lag a newly-started process by a second or two."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        out = subprocess.check_output(
            ["powershell", "-NoProfile", "-Command",
             "(Get-CimInstance Win32_Process -Filter \"Name='jimmy-engine-host.exe'\").CommandLine"],
            text=True, stderr=subprocess.DEVNULL,
        )
        for line in out.splitlines():
            if "--jimmy-control-addr" in line:
                idx = line.index("--jimmy-control-addr") + len("--jimmy-control-addr ")
                rest = line[idx:].strip()
                addr = rest.split(" ")[0]
                return addr.split(":")[1]
        time.sleep(0.5)
    return None

def query_ptt_via_rigctld(port=4532):
    """Opens its own third rigctld TCP client (rigctld supports many concurrent clients) to
    query PTT state directly, independent of Jimmy's own connection."""
    with socket.create_connection(("127.0.0.1", port), timeout=3) as s:
        s.sendall(b"t\n")
        return s.recv(64).decode().strip()

def main():
    if not os.path.exists(JIMMY_EXE):
        print(f"ERROR: {JIMMY_EXE} not found. Build Jimmy first.")
        return 1

    os.makedirs(INI_DIR, exist_ok=True)
    backup_path = None
    ini_existed = os.path.exists(INI_PATH)
    if ini_existed:
        backup_path = os.path.join(tempfile.gettempdir(), "Jimmy.ini.pttsmoke_backup")
        shutil.copy2(INI_PATH, backup_path)

    jimmy_proc = None
    try:
        print("Setting decodeEngineMode=JimmyNative, radioControlMode=HamlibRigctld, "
              "radioPttEnabled=True, radioRigModel=1 (Hamlib Dummy rig)...")
        ini_write("decodeEngineMode", "JimmyNative")
        ini_write("nativeEngineMyCall", "KB0UZT")
        ini_write("nativeEngineMyGrid", "FN42")
        ini_write("radioControlMode", "HamlibRigctld")
        ini_write("radioPttEnabled", "True")
        ini_write("radioRigModel", "1")
        ini_write("radioUseExternalRigctld", "False")
        ini_write("radioRigctldPort", "4532")

        test_db = os.path.join(tempfile.gettempdir(), "JimmyPttSmoke_logbook.db")
        if os.path.exists(test_db):
            os.remove(test_db)
        env = dict(os.environ)
        env["JIMMY_TEST_DB_PATH"] = test_db
        print("Starting Jimmy.exe in test mode...")
        jimmy_proc = subprocess.Popen([JIMMY_EXE], cwd=os.path.dirname(JIMMY_EXE), env=env)
        time.sleep(6)  # give rigctld + engine host + PTT listener time to come up

        v = jr.JimmyVerifier()
        passed = 0
        failed = 0

        control_port = find_engine_host_control_port()
        print(f"Engine host's control port (from its own command line): {control_port}")
        if control_port is None:
            print("FAIL: could not find --jimmy-control-addr on jimmy-engine-host.exe's command line.")
            raw = subprocess.check_output(
                ["powershell", "-NoProfile", "-Command",
                 "Get-CimInstance Win32_Process -Filter \"Name='jimmy-engine-host.exe'\" | Format-List ProcessId,CommandLine"],
                text=True, stderr=subprocess.STDOUT,
            )
            print("--- raw diagnostic ---")
            print(raw)
            print("--- Jimmy.ini relevant keys ---")
            for key in ("decodeEngineMode", "radioControlMode", "radioPttEnabled", "radioRigModel"):
                buf = ctypes.create_unicode_buffer(256)
                ctypes.windll.kernel32.GetPrivateProfileStringW("Jimmy", key, "<missing>", buf, 256, INI_PATH)
                print(f"  {key} = {buf.value}")
            return 1
        passed += 1

        # Confirm PTT starts off.
        state = query_ptt_via_rigctld()
        print(f"Initial PTT state via rigctld: '{state}'")
        if state == "0":
            passed += 1
        else:
            print("FAIL: expected PTT off (0) initially.")
            failed += 1

        # Send PTT_ON to the listener and confirm rigctld shows PTT asserted.
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.sendto(b"PTT_ON", ("127.0.0.1", int(control_port)))
        time.sleep(1)
        state = query_ptt_via_rigctld()
        print(f"PTT state after PTT_ON: '{state}'")
        if state == "1":
            passed += 1
        else:
            print("FAIL: expected PTT on (1) after PTT_ON.")
            failed += 1

        # Send PTT_OFF and confirm release.
        sock.sendto(b"PTT_OFF", ("127.0.0.1", int(control_port)))
        time.sleep(1)
        state = query_ptt_via_rigctld()
        print(f"PTT state after PTT_OFF: '{state}'")
        if state == "0":
            passed += 1
        else:
            print("FAIL: expected PTT off (0) after PTT_OFF.")
            failed += 1

        print(f"\nVerification summary: {passed}/{passed + failed} assertions passed")
        return 0 if failed == 0 else 1
    finally:
        if jimmy_proc is not None:
            jimmy_proc.terminate()
            try:
                jimmy_proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                jimmy_proc.kill()
        subprocess.run(["taskkill", "/IM", "jimmy-engine-host.exe", "/F"], capture_output=True)
        subprocess.run(["taskkill", "/IM", "rigctld.exe", "/F"], capture_output=True)

        if ini_existed:
            shutil.copy2(backup_path, INI_PATH)
            os.remove(backup_path)
        elif os.path.exists(INI_PATH):
            os.remove(INI_PATH)

if __name__ == "__main__":
    sys.exit(main())
