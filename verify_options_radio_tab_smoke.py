"""Phase 4i smoke test: confirms OptionsDlg's Radio tab -- now carrying the new Decode Engine
controls (BuildRadioTab, called eagerly in OptionsDlg's constructor) -- actually opens and
renders without throwing. Backs up and restores the real Jimmy.ini exactly like the other
verify_*.py scripts, and closes the dialog via Escape/Cancel so nothing is ever saved to it.
Not part of the permanent suite.
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
INI_PATH = os.path.join(os.environ["LOCALAPPDATA"], "Jimmy", "Jimmy.ini")

user32 = ctypes.windll.user32

def main():
    backup_path = None
    ini_existed = os.path.exists(INI_PATH)
    if ini_existed:
        backup_path = os.path.join(tempfile.gettempdir(), "Jimmy.ini.optionssmoke_backup")
        shutil.copy2(INI_PATH, backup_path)

    test_db = os.path.join(tempfile.gettempdir(), "JimmyOptionsSmoke2_logbook.db")
    if os.path.exists(test_db):
        os.remove(test_db)
    env = dict(os.environ)
    env["JIMMY_TEST_DB_PATH"] = test_db
    proc = subprocess.Popen([JIMMY_EXE], cwd=os.path.dirname(JIMMY_EXE), env=env)
    try:
        time.sleep(5)
        v = jr.JimmyVerifier()
        if not v.available:
            print("FAIL: could not find Jimmy's main window.")
            return 1

        # Alt+O opens Options (HotkeyAction.Options default binding).
        user32.SetForegroundWindow(v._jhwnd)
        time.sleep(0.3)
        VK_MENU = 0x12
        VK_O = 0x4F
        user32.keybd_event(VK_MENU, 0, 0, 0)
        user32.keybd_event(VK_O, 0, 0, 0)
        time.sleep(0.1)
        user32.keybd_event(VK_O, 0, 2, 0)      # KEYEVENTF_KEYUP
        user32.keybd_event(VK_MENU, 0, 2, 0)
        time.sleep(2)

        found_options = []
        def cb(hwnd, _):
            length = user32.GetWindowTextLengthW(hwnd)
            buf = ctypes.create_unicode_buffer(length + 1)
            user32.GetWindowTextW(hwnd, buf, length + 1)
            if "Option" in buf.value:
                found_options.append((hwnd, buf.value))
            return True
        WNDENUMPROC = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
        user32.EnumWindows(WNDENUMPROC(cb), 0)

        ok_opened = len(found_options) > 0
        print(f"Options window found: {ok_opened}  {found_options}")

        still_running = proc.poll() is None
        print(f"Jimmy process still running (no crash from BuildRadioTab): {still_running}")

        # Close via Escape (Cancel) -- never Save, never touches real Jimmy.ini.
        user32.keybd_event(0x1B, 0, 0, 0)  # VK_ESCAPE
        time.sleep(0.05)
        user32.keybd_event(0x1B, 0, 2, 0)
        time.sleep(1)

        ok = ok_opened and still_running
        print("RESULT:", "PASS" if ok else "FAIL")
        return 0 if ok else 1
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
        if ini_existed:
            shutil.copy2(backup_path, INI_PATH)
            os.remove(backup_path)
        elif os.path.exists(INI_PATH):
            os.remove(INI_PATH)

if __name__ == "__main__":
    sys.exit(main())
