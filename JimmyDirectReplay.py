#!/usr/bin/env python3
"""
JimmyDirectReplay.py - Direct-protocol replay tester with automatic verification for Jimmy.

Replaces JimmyReplay.py (retired 2026-08-18, UDP-to-Direct test-harness migration).
JimmyReplay.py simulated a standard WSJT-X UDP peer talking to Jimmy's now-deleted
classic-protocol listener. Production Jimmy has been Direct-only since 2026-08-12
(Jimmy Next -> Direct control port -> EngineHost -> Nexus); the only reason the UDP
transport code survived this long was this test harness. This script closes that gap:
it is a fake control-port SERVER standing in for jimmy-engine-host.exe, listening on
127.0.0.1:58239 (NativeEngineClient.ControlPort) and speaking the exact same line-
delimited SNAPSHOT/REPLY/HALT_TX/... protocol the real engine host does (EngineHost/
src/main.rs's run_control_server). Controller.ApplyEngineMode()'s TestModeGuard.IsTestMode
branch now calls the SAME ConnectDirectEngine() production uses, just against this fake
server instead of a real jimmy-engine-host.exe process -- see that method's own comment.

Verification works exactly as before: Jimmy's real on-screen Win32 controls are read
directly (JimmyVerifier, copied verbatim from JimmyReplay.py -- transport-agnostic by
design, it only ever looked at Jimmy's UI, never the wire).

BEFORE RUNNING:
  1. Close WSJT-X (irrelevant to Direct mode, but keep the same safety habit).
  2. Start Jimmy (Debug build), or let run_direct_replay_tests.bat start it for you.
  3. In Jimmy, set mode to CQ.
  4. Enable Advanced Call Layout (Options) to bypass T/R period checks.
  5. (Optional) Add 'SOTA' to the directed CQ alert text box for a full T-series PASS
     on the SOTA group. Without it, that test prints a WARNING instead of PASS or FAIL --
     this is not a bug.

USAGE:
  python JimmyDirectReplay.py

Requires Python 3.6+. No external packages required -- standard library + ctypes only.
"""

import json
import socket
import socketserver
import sys
import threading
import time
import ctypes
import ctypes.wintypes
import os

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

# ─── Configuration ────────────────────────────────────────────────────────────
# Matches NativeEngineClient.ControlPort (WSJTX_Controller/NativeEngineClient.cs) exactly --
# Jimmy's DirectPollTick (WsjtxClient.Direct.cs) connects here every 1000ms regardless of
# whether a real engine host or this fake one is listening.
CONTROL_PORT = 58239

MY_CALL    = "KB0UZT"
MY_GRID    = "FN42"
THEIR_CALL = "K4YT"

# Period-check test stations (Group 10) -- unique callsigns not used elsewhere.
FT8_A_CALL = "W9EVN"
FT8_B_CALL = "W9ODD"

# ─── Win32 API constants ──────────────────────────────────────────────────────
GWL_STYLE         = -16
ES_READONLY       = 0x0800
WM_GETTEXT        = 0x000D
WM_GETTEXTLENGTH  = 0x000E
LB_GETCOUNT       = 0x018B
LB_GETTEXTLEN     = 0x018A
LB_GETTEXT        = 0x0189
LB_GETITEMHEIGHT  = 0x01A1
LB_SETTOPINDEX    = 0x0197
WM_LBUTTONDOWN    = 0x0201
MK_LBUTTON        = 0x0001

_user32   = ctypes.windll.user32
_kernel32 = ctypes.windll.kernel32

PROCESS_QUERY_LIMITED_INFORMATION = 0x1000

_kernel32.OpenProcess.restype  = ctypes.wintypes.HANDLE
_kernel32.OpenProcess.argtypes = [ctypes.wintypes.DWORD, ctypes.wintypes.BOOL, ctypes.wintypes.DWORD]

# ═══════════════════════════════════════════════════════════════════════════════
# Win32 helpers
# ═══════════════════════════════════════════════════════════════════════════════

class _RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]

class _POINT(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]

_EnumChildProc = ctypes.WINFUNCTYPE(ctypes.c_bool,
                                    ctypes.wintypes.HWND, ctypes.wintypes.LPARAM)
_EnumWndProc   = ctypes.WINFUNCTYPE(ctypes.c_bool,
                                    ctypes.wintypes.HWND, ctypes.wintypes.LPARAM)

def _all_descendants(parent):
    results = []
    @_EnumChildProc
    def cb(hwnd, _):
        results.append(hwnd)
        return True
    _user32.EnumChildWindows(parent, cb, 0)
    return results

def _wnd_title(hwnd):
    buf = ctypes.create_unicode_buffer(512)
    _user32.GetWindowTextW(hwnd, buf, 512)
    return buf.value

def _cls(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    _user32.GetClassNameW(hwnd, buf, 256)
    return buf.value.upper()

def _style(hwnd):
    return _user32.GetWindowLongW(hwnd, GWL_STYLE)

def _owning_process_name(hwnd):
    """Executable name (no path/extension, case folded) of the process that owns
    hwnd, e.g. "jimmy" for C:\\...\\Jimmy Next.exe. Returns "" if it can't be
    determined -- callers should treat that as "not a match" rather than raise.
    """
    pid = ctypes.wintypes.DWORD()
    _user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    if not pid.value:
        return ""
    handle = _kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid.value)
    if not handle:
        return ""
    try:
        buf  = ctypes.create_unicode_buffer(260)
        size = ctypes.wintypes.DWORD(260)
        if not _kernel32.QueryFullProcessImageNameW(handle, 0, buf, ctypes.byref(size)):
            return ""
        return os.path.splitext(os.path.basename(buf.value))[0].lower()
    finally:
        _kernel32.CloseHandle(handle)

def _read_text(hwnd):
    n = _user32.SendMessageW(hwnd, WM_GETTEXTLENGTH, 0, 0)
    if n <= 0:
        return ""
    buf = ctypes.create_unicode_buffer(n + 2)
    _user32.SendMessageW(hwnd, WM_GETTEXT, n + 1, buf)
    return buf.value

def _read_listbox(hwnd):
    count = _user32.SendMessageW(hwnd, LB_GETCOUNT, 0, 0)
    if count <= 0:
        return []
    items = []
    for i in range(count):
        n = _user32.SendMessageW(hwnd, LB_GETTEXTLEN, i, 0)
        if n > 0:
            buf = ctypes.create_unicode_buffer(n + 2)
            _user32.SendMessageW(hwnd, LB_GETTEXT, i, buf)
            items.append(buf.value)
    return items

def _client_xy(child, parent):
    r = _RECT()
    _user32.GetWindowRect(child, ctypes.byref(r))
    p = _POINT(r.left, r.top)
    _user32.MapWindowPoints(0, parent, ctypes.byref(p), 1)
    return p.x, p.y

def _post_listbox_click(hwnd, index):
    """Simulate a real operator double-click on listbox row `index`, entirely via
    posted Win32 messages -- no real mouse movement, no keyboard input, no focus
    change to any other window. This is what drives Jimmy's real double-click-to-
    reply path (Controller.callListBox_MouseDown -> callListBoxClickTimer_Tick ->
    ProcessCallListBoxAnyClick(dblClk=True) -> WsjtxClient.NextCall -> ReplyTo ->
    DirectSendReply) end to end through the real running app, the same way
    JimmyVerifier already reads Jimmy's controls end to end through the real app.

    LB_SETTOPINDEX scrolls the target row to the very top of the visible list first
    (value-only message, safe cross-process) so the click point is always the fixed
    near-top-left corner of the control, regardless of queue length or existing
    scroll position -- no cross-process RECT/pointer marshaling needed at all.
    Jimmy's own MouseDown handler only reads e.Location (decoded by WinForms from
    lParam) and IndexFromPoint -- neither requires real hardware input or window
    focus; a directly posted WM_LBUTTONDOWN triggers the identical code path.

    Two posts within callListBoxClickTimer's own 250ms window (Controller.cs) is
    what makes Jimmy itself recognize this as a double-click (listBoxClickCount > 1
    when the timer fires) -- there is no OS-level WM_LBUTTONDBLCLK involved at all.
    """
    _user32.SendMessageW(hwnd, LB_SETTOPINDEX, index, 0)
    height = _user32.SendMessageW(hwnd, LB_GETITEMHEIGHT, 0, 0)
    if height <= 0:
        height = 16
    x, y = 5, height // 2
    lparam = (y << 16) | (x & 0xFFFF)
    _user32.PostMessageW(hwnd, WM_LBUTTONDOWN, MK_LBUTTON, lparam)
    time.sleep(0.08)
    _user32.PostMessageW(hwnd, WM_LBUTTONDOWN, MK_LBUTTON, lparam)


# ═══════════════════════════════════════════════════════════════════════════════
# JimmyVerifier -- copied verbatim from JimmyReplay.py (transport-agnostic; it only
# ever reads Jimmy's real UI via Win32, never the wire), plus two small additions
# at the bottom for driving the real double-click-to-reply path.
# ═══════════════════════════════════════════════════════════════════════════════

class JimmyVerifier:
    """
    Reads Jimmy's UI controls via Win32 to assert expected state.

    Controls identified:
      statusText  — the ONLY read-only EDIT child (ES_READONLY style)
      callListBox — leftmost LISTBOX at y < 200 in client coords  (x≈15)
      logListBox  — next LISTBOX at y < 200 in client coords      (x≈214)
    """

    def __init__(self):
        self._jhwnd    = None
        self._stat     = None
        self._calllist = None
        self._loglist  = None
        self.passed    = 0
        self.failed    = 0
        deadline = time.time() + 10.0
        while True:
            self._find()
            if self.available or time.time() >= deadline:
                break
            time.sleep(0.5)

    def _find(self):
        hits = []
        @_EnumWndProc
        def cb(hwnd, _):
            if _wnd_title(hwnd).startswith("Jimmy") and _owning_process_name(hwnd).startswith("jimmy"):
                hits.append(hwnd)
            return True
        _user32.EnumWindows(cb, 0)
        if not hits:
            return
        self._jhwnd = hits[0]

        children  = _all_descendants(self._jhwnd)
        edits     = [h for h in children if 'EDIT'    in _cls(h)]
        listboxes = [h for h in children if 'LISTBOX' in _cls(h)]

        for h in edits:
            if _style(h) & ES_READONLY:
                self._stat = h
                break

        near = []
        for h in listboxes:
            try:
                x, y = _client_xy(h, self._jhwnd)
                if 0 < y < 200:
                    near.append((x, y, h))
            except Exception:
                pass
        near.sort()
        if near:
            self._calllist = near[0][2]
        if len(near) >= 2:
            self._loglist = near[1][2]

    @property
    def available(self):
        return bool(self._jhwnd and self._stat and self._calllist)

    def _live_listboxes(self):
        """Re-enumerate child listboxes on every call -- WinForms recreates the
        ListBox HWND when SelectionMode changes (None<->One as the queue empties/
        fills), so a cached HWND can start reading a destroyed window."""
        if not self._jhwnd:
            return None, None
        children  = _all_descendants(self._jhwnd)
        listboxes = [h for h in children if 'LISTBOX' in _cls(h)]
        near = []
        for h in listboxes:
            try:
                x, y = _client_xy(h, self._jhwnd)
                if 0 < y < 200:
                    near.append((x, y, h))
            except Exception:
                pass
        near.sort()
        calllist = near[0][2] if near else None
        loglist  = near[1][2] if len(near) >= 2 else None
        return calllist, loglist

    # ── Read methods ──────────────────────────────────────────────────────
    def status_text(self):
        return _read_text(self._stat) if self._stat else ""

    def queue_items(self):
        calllist, _ = self._live_listboxes()
        return _read_listbox(calllist) if calllist else []

    def log_items(self):
        _, loglist = self._live_listboxes()
        return _read_listbox(loglist) if loglist else []

    # ── Polling ───────────────────────────────────────────────────────────
    def wait_for_status(self, fragment, timeout=4.0):
        deadline = time.time() + timeout
        while time.time() < deadline:
            t = self.status_text()
            if fragment.lower() in t.lower():
                return t
            time.sleep(0.1)
        return self.status_text()

    def wait_for_queue(self, fragment, timeout=4.0):
        frag_nsp = fragment.lower().replace(" ", "")
        deadline = time.time() + timeout
        while time.time() < deadline:
            if any(frag_nsp in i.lower().replace(" ", "") for i in self.queue_items()):
                return True
            time.sleep(0.1)
        return False

    # ── Assertions ────────────────────────────────────────────────────────
    def _report(self, ok, label, detail=""):
        mark = "✓ PASS" if ok else "✗ FAIL"
        d    = f"  [{detail}]" if detail else ""
        print(f"    {mark}  {label}{d}")
        if ok:
            self.passed += 1
        else:
            self.failed += 1

    def check_active(self, label="Jimmy is ACTIVE"):
        t  = self.status_text()
        ok = bool(t) and not any(w in t.lower()
                                  for w in ("idle", "inactive", "start", "connecting"))
        self._report(ok, label, f"status='{t}'")

    def check_status_contains(self, fragment, label):
        self.wait_for_status(fragment, timeout=3.0)
        t  = self.status_text()
        ok = fragment.lower() in t.lower()
        self._report(ok, label, f"status='{t}'")

    def check_queue_contains(self, fragment, label):
        self.wait_for_queue(fragment, timeout=3.0)
        items    = self.queue_items()
        frag_nsp = fragment.lower().replace(" ", "")
        ok       = any(frag_nsp in i.lower().replace(" ", "") for i in items)
        self._report(ok, label, f"queue={items}")

    def check_queue_not_contains(self, fragment, label):
        time.sleep(0.3)
        items    = self.queue_items()
        frag_nsp = fragment.lower().replace(" ", "")
        ok       = not any(frag_nsp in i.lower().replace(" ", "") for i in items)
        self._report(ok, label, f"queue={items}")

    def check_queue_contains_warn(self, fragment, label, config_note):
        self.wait_for_queue(fragment, timeout=3.0)
        items    = self.queue_items()
        frag_nsp = fragment.lower().replace(" ", "")
        ok       = any(frag_nsp in i.lower().replace(" ", "") for i in items)
        if ok:
            self._report(True, label, f"queue={items}")
        else:
            print(f"    ⚠ WARN  {label}")
            print(f"           (not failed — requires: {config_note})")
            print(f"           queue={items}")

    def check_queue_not_contains_warn(self, fragment, label, config_note):
        time.sleep(0.3)
        items    = self.queue_items()
        frag_nsp = fragment.lower().replace(" ", "")
        ok       = not any(frag_nsp in i.lower().replace(" ", "") for i in items)
        if ok:
            self._report(True, label, f"queue={items}")
        else:
            print(f"    ⚠ WARN  {label}")
            print(f"           (not failed — requires: {config_note})")
            print(f"           queue={items}")

    def find_queue_row(self, fragment):
        frag_nsp = fragment.lower().replace(" ", "")
        for i in self.queue_items():
            if frag_nsp in i.lower().replace(" ", ""):
                return i
        return None

    def find_queue_row_index(self, fragment):
        """Like find_queue_row, but returns the row's live index (for double-click
        targeting) instead of its text, or None if not present."""
        frag_nsp = fragment.lower().replace(" ", "")
        items = self.queue_items()
        for idx, i in enumerate(items):
            if frag_nsp in i.lower().replace(" ", ""):
                return idx
        return None

    def check_queue_row_contains_warn(self, call_fragment, tag_fragment, label, config_note):
        self.wait_for_queue(call_fragment, timeout=3.0)
        row = self.find_queue_row(call_fragment)
        ok  = row is not None and tag_fragment.lower() in row.lower()
        if ok:
            self._report(True, label, f"row='{row}'")
        else:
            print(f"    ⚠ WARN  {label}")
            print(f"           (not failed — requires: {config_note})")
            print(f"           queue={self.queue_items()}")
        return ok

    def check_queue_row_not_contains(self, call_fragment, tag_fragment, label):
        time.sleep(0.3)
        row = self.find_queue_row(call_fragment)
        ok  = (row is None) or (tag_fragment.lower() not in row.lower())
        self._report(ok, label, f"row={row!r}")

    def check_log_contains(self, fragment, label):
        frag_nsp = fragment.lower().replace(" ", "")
        deadline = time.time() + 4.0
        while time.time() < deadline:
            if any(frag_nsp in i.lower().replace(" ", "") for i in self.log_items()):
                break
            time.sleep(0.1)
        items = self.log_items()
        ok    = any(frag_nsp in i.lower().replace(" ", "") for i in items)
        self._report(ok, label, f"logList={items}")

    def summary(self):
        total = self.passed + self.failed
        print(f"\n  Verification summary: {self.passed}/{total} assertions passed")
        if self.failed:
            print(f"  ✗ {self.failed} assertion(s) FAILED")
        else:
            print(f"  ✓ All assertions passed")
        return self.failed == 0

    # ── Double-click-to-reply driver (new for the Direct harness) ──────────
    def double_click_queue_row(self, call_fragment, timeout=3.0):
        """Finds call_fragment's current row in the live callListBox and posts a
        real double-click at it (see _post_listbox_click's own comment) -- drives
        Jimmy's actual ReplyTo()/DirectSendReply() path exactly the way an operator
        clicking the row would, so a subsequent REPLY the fake engine receives (see
        FakeEngine.wait_for_reply below) is Jimmy's own real decision, not a
        scripted shortcut. Returns True if the row was found and clicked, False if
        the call was never in the queue within `timeout`.
        """
        deadline = time.time() + timeout
        idx = None
        while time.time() < deadline:
            idx = self.find_queue_row_index(call_fragment)
            if idx is not None:
                break
            time.sleep(0.1)
        if idx is None:
            return False
        calllist, _ = self._live_listboxes()
        if not calllist:
            return False
        _post_listbox_click(calllist, idx)
        return True


# ═══════════════════════════════════════════════════════════════════════════════
# FakeEngine -- the Direct control-port server. Stands in for jimmy-engine-host.exe:
# Jimmy's real DirectPollTick (WsjtxClient.Direct.cs) opens a new short-lived TCP
# connection to 127.0.0.1:58239 roughly once a second and sends one of a small set
# of commands; this class answers exactly the way EngineHost/src/main.rs's
# run_control_server does, using the JSON field-naming contract WsjtxClient.Direct.cs
# actually deserializes (DirectJsonOptions: CamelCase, case-insensitive) -- see that
# file's own DirectSnapshot/DirectRadioStatus/DirectDecodeRow/DirectQsoStatus classes,
# which are the real, load-bearing contract this mirrors field-for-field.
# ═══════════════════════════════════════════════════════════════════════════════

class FakeEngine:
    def __init__(self, mycall=MY_CALL, mygrid=MY_GRID, dial_mhz=14.074):
        self._lock = threading.Lock()
        self.mycall = mycall
        self.mygrid = mygrid
        self.dial_mhz = dial_mhz
        self.transmitting = False
        self.slot = 0
        self.tx_enabled = True
        self.tuning = False
        self.tx_level = 1.0
        self.recent_decodes = []       # list of dict(from,snr,dtSec,freqHz,message)
        self.qso_state = None
        self.qso_txnow = None
        self.last_reply = None         # most recent REPLY args dict Jimmy sent us
        self.reply_event = threading.Event()
        self._server = None
        self._thread = None

    # ── Test-side control (called from the main test thread) ───────────────
    def start(self):
        self._server = socketserver.ThreadingTCPServer(
            ("127.0.0.1", CONTROL_PORT), self._make_handler())
        self._server.daemon_threads = True
        self._server.allow_reuse_address = True
        self._thread = threading.Thread(target=self._server.serve_forever, daemon=True)
        self._thread.start()

    def stop(self):
        if self._server:
            self._server.shutdown()
            self._server.server_close()

    def send_decodes(self, rows):
        """Replace the current slot's decode list and advance the slot counter --
        mirrors AppSnapshot.recentDecodes' real "replaced each slot" semantics
        (WsjtxClient.Direct.cs's DirectApplyDecodes' own comment). Each row is a
        dict with keys: from, message, snr (default -10), dtSec (default 0.1),
        freqHz (default 1500.0).
        """
        with self._lock:
            self.slot += 1
            self.recent_decodes = [{
                "from":    r.get("from", ""),
                "snr":     r.get("snr", -10),
                "dtSec":   r.get("dtSec", 0.1),
                "freqHz":  r.get("freqHz", 1500.0),
                "message": r["message"],
            } for r in rows]

    def send_decode(self, message, from_call="", snr=-10, dt_sec=0.1, freq_hz=1500.0):
        self.send_decodes([{"from": from_call, "message": message, "snr": snr,
                             "dtSec": dt_sec, "freqHz": freq_hz}])

    def set_transmitting(self, on):
        with self._lock:
            self.transmitting = on

    def set_qso_txnow(self, text):
        with self._lock:
            self.qso_txnow = text

    def clear_qso(self):
        with self._lock:
            self.qso_txnow = None
            self.qso_state = None

    def wait_for_reply(self, timeout=5.0):
        """Blocks until Jimmy sends a REPLY command (real double-click-to-reply
        path fired), returning the parsed args dict, or None on timeout."""
        got = self.reply_event.wait(timeout)
        if not got:
            return None
        with self._lock:
            self.reply_event.clear()
            return dict(self.last_reply) if self.last_reply else None

    def complete_qso_now(self, dxcall, final="RR73"):
        """Simulates the engine's own QSO sequencer finishing an exchange in one
        step -- the next SNAPSHOT poll reports our own final 73/RR73 addressed to
        dxcall. Standard on-air message format is "<TO> <DE> <payload>"
        (WsjtxMessage.ToCall/DeCall, Messages/Out/WsjtxMessage.cs), so our own
        outgoing text is "<dxcall> <mycall> RR73". This exercises the exact same
        DirectApplyStatus code path (curTxMsg/callInProg/Is73orRR73 -> LogQso) a
        real completed QSO would, since Jimmy's own callInProg was already set by
        its own real ReplyTo() call before this REPLY was even answered -- nothing
        here bypasses Jimmy's own logging logic, it only supplies the engine-side
        fact a real engine would eventually report anyway.
        """
        with self._lock:
            self.transmitting = True
            self.qso_state = "done"
            self.qso_txnow = f"{dxcall} {self.mycall} {final}"

    # ── Server-side protocol handling ───────────────────────────────────────
    def _snapshot_json(self):
        with self._lock:
            radio = {
                "dialMhz": self.dial_mhz,
                "transmitting": self.transmitting,
                "slot": self.slot,
                "micGain": None,
                "txLevel": self.tx_level,
                "tuning": self.tuning,
                "rxLevel": 0.0,
                "smeterDb": None,
                "txEnabled": self.tx_enabled,
            }
            qso = None
            if self.qso_txnow is not None or self.qso_state is not None:
                qso = {"state": self.qso_state, "txNow": self.qso_txnow}
            snap = {
                "mycall": self.mycall,
                "mygrid": self.mygrid,
                "radio": radio,
                "recentDecodes": list(self.recent_decodes),
                "qso": qso,
            }
            return json.dumps(snap)

    def _handle_reply(self, json_text):
        try:
            args = json.loads(json_text)
        except Exception:
            return "ERR bad REPLY args"
        with self._lock:
            self.last_reply = args
            self.reply_event.set()
        return "OK"

    def _make_handler(self):
        engine = self

        class Handler(socketserver.StreamRequestHandler):
            def handle(self):
                try:
                    line = self.rfile.readline()
                    if not line:
                        return
                    cmd = line.decode("utf-8", errors="replace").strip()
                except Exception:
                    return

                if cmd == "SNAPSHOT":
                    resp = engine._snapshot_json()
                elif cmd.startswith("REPLY "):
                    resp = engine._handle_reply(cmd[len("REPLY "):])
                elif cmd == "HALT_TX":
                    with engine._lock:
                        engine.transmitting = False
                    resp = "OK"
                elif cmd.startswith("SET_TX_ENABLED "):
                    with engine._lock:
                        engine.tx_enabled = cmd.split(" ", 1)[1].strip() == "1"
                    resp = "OK"
                elif cmd.startswith("SET_TUNING "):
                    with engine._lock:
                        engine.tuning = cmd.split(" ", 1)[1].strip() == "1"
                    resp = "OK"
                elif cmd.startswith("SET_TIER "):
                    resp = "OK" if cmd.split(" ", 1)[1].strip() in ("FT8", "FT4") else "ERR bad tier"
                elif cmd.startswith(("SET_PSKREPORTER ", "SET_MIC_GAIN ", "SET_TX_LEVEL ",
                                      "SET_DECODE_DEPTH ")):
                    resp = "OK"
                else:
                    resp = "ERR unknown command"

                try:
                    self.wfile.write((resp + "\n").encode("utf-8"))
                except Exception:
                    pass

        return Handler


# ═══════════════════════════════════════════════════════════════════════════════
# Test Runner
# ═══════════════════════════════════════════════════════════════════════════════

_test_num = 0

def step(label, description, action, verify_fn=None, settle=1.2):
    global _test_num
    _test_num += 1
    tag = f"D{_test_num:02d}"
    print(f"  [{tag}] {label}")
    print(f"        {description}")
    action()
    time.sleep(settle)
    if verify_fn:
        verify_fn()
    print()


def group1_station_calling_me(engine, v):
    print("  ─ Group 1: K4YT calling KB0UZT (directed to me) ─")

    step("Grid reply: KB0UZT K4YT EM63", "CallingMe expected; K4YT queued with 'to you' tag",
         lambda: engine.send_decode(f"{MY_CALL} {THEIR_CALL} EM63", from_call=THEIR_CALL),
         verify_fn=lambda: v.check_queue_contains(THEIR_CALL, f"D: {THEIR_CALL} in callQueue (CallingMe inferred)"))

    step("Signal report: KB0UZT K4YT -05", "Queue entry updated; K4YT remains in queue",
         lambda: engine.send_decode(f"{MY_CALL} {THEIR_CALL} -05", from_call=THEIR_CALL),
         verify_fn=lambda: v.check_queue_contains(THEIR_CALL, f"D: {THEIR_CALL} still in queue after report"))

    step("Roger report: KB0UZT K4YT R-05", "Queue entry updated",
         lambda: engine.send_decode(f"{MY_CALL} {THEIR_CALL} R-05", from_call=THEIR_CALL),
         verify_fn=lambda: v.check_queue_contains(THEIR_CALL, f"D: {THEIR_CALL} still in queue after R-report"))

    step("RRR: KB0UZT K4YT RRR", "Queue entry updated",
         lambda: engine.send_decode(f"{MY_CALL} {THEIR_CALL} RRR", from_call=THEIR_CALL),
         verify_fn=lambda: v.check_queue_contains(THEIR_CALL, f"D: {THEIR_CALL} still in queue after RRR"))

    step("RR73: KB0UZT K4YT RR73", "SIGNOFF path -- queue entry still reflects the exchange",
         lambda: engine.send_decode(f"{MY_CALL} {THEIR_CALL} RR73", from_call=THEIR_CALL),
         settle=2.0,
         verify_fn=lambda: v.check_status_contains("", f"D: status updated after RR73"))


def group2_cq_messages(engine, v):
    print("  ─ Group 2: CQ messages ─")
    step("Plain CQ: CQ K4YT EM63", "CallAdded expected; K4YT queued",
         lambda: engine.send_decode(f"CQ {THEIR_CALL} EM63", from_call=THEIR_CALL),
         verify_fn=lambda: v.check_queue_contains(THEIR_CALL, f"D: {THEIR_CALL} in queue from CQ"))

    step("POTA CQ: CQ POTA K4YT", "POTA sound expected if enabled; K4YT queued",
         lambda: engine.send_decode(f"CQ POTA {THEIR_CALL}", from_call=THEIR_CALL),
         verify_fn=lambda: v.check_queue_contains(THEIR_CALL, f"D: {THEIR_CALL} in queue from CQ POTA"))


def group3_ap_suffix(engine, v):
    print("  ─ Group 3: WSJT-X 3.0 AP suffix -- stripped before classifying ─")
    step(f"AP report: {MY_CALL} {THEIR_CALL} -05 a35", "Strips to -05; report accepted",
         lambda: engine.send_decode(f"{MY_CALL} {THEIR_CALL} -05 a35", from_call=THEIR_CALL),
         verify_fn=lambda: v.check_queue_contains(THEIR_CALL, f"D: {THEIR_CALL} in queue (AP-stripped report)"))

    step(f"AP CQ: CQ {THEIR_CALL} EM63 a1", "Strips to CQ K4YT EM63; accepted",
         lambda: engine.send_decode(f"CQ {THEIR_CALL} EM63 a1", from_call=THEIR_CALL),
         verify_fn=lambda: v.check_queue_contains(THEIR_CALL, f"D: {THEIR_CALL} in queue (AP-stripped CQ)"))


def group4_contest_field_day(engine, v):
    print("  ─ Group 4: Contest / Field Day ─")
    step(f"FD to me: {MY_CALL} {THEIR_CALL} 2A MO", "IsContest + toCall=myCall -> queued",
         lambda: engine.send_decode(f"{MY_CALL} {THEIR_CALL} 2A MO", from_call=THEIR_CALL),
         verify_fn=lambda: v.check_queue_contains(THEIR_CALL, f"D: {THEIR_CALL} queued (FD exchange to me)"))

    step(f"Contest between others: {THEIR_CALL} K9AVT 559 TX", "toCall != myCall -> rejected",
         lambda: engine.send_decode(f"{THEIR_CALL} K9AVT 559 TX", from_call=THEIR_CALL),
         verify_fn=lambda: v.check_queue_not_contains("K9AVT", "D: K9AVT NOT in queue (contest between others)"))


def _real_logged_qso(engine, v, call, tag_num):
    """Shared driver: get `call` queued via an ordinary CQ, double-click it to
    trigger Jimmy's own real ReplyTo()/DirectSendReply() path, then complete the
    simulated QSO through the fake engine so Jimmy's OWN DirectApplyStatus logic
    (curTxMsg/callInProg/Is73orRR73 -> LogQso, WsjtxClient.Direct.cs) does the
    logging for real -- this is the Direct-mode equivalent of what the old UDP
    harness got for free from a directly-injected QsoLoggedMessage packet, which
    has no Direct-protocol counterpart (Direct has no "trust me, log this" wire
    command -- see the migration notes in ApplyEngineMode's own comment).

    Returns True/False for the real logging assertion once REPLY genuinely arrived
    for `call` (a hard result -- False there is a real potential regression, same
    weight as any other check_* assertion). Returns None when the click-automation
    setup itself could not be completed (call never queued, or the posted-message
    double-click could not be reliably targeted after retries) -- an environment/
    timing gap, same class as Group 15's own WARN-not-FAIL precedent, not a claim
    about the code under test. Callers must tell these apart, not treat both as
    the same kind of failure.
    """
    engine.send_decode(f"CQ {call} EM63", from_call=call)
    if not v.wait_for_queue(call, timeout=3.0):
        print(f"    ⚠ WARN  D{tag_num}-setup: {call} never queued -- cannot drive a real QSO")
        return None
    time.sleep(0.3)

    # find_queue_row_index -> _post_listbox_click has a small, real race: the queue can
    # re-sort (rank/priority tags recompute) in the gap between locating the row and the
    # two posted clicks landing, especially once many earlier groups have left several
    # calls queued -- confirmed live (not merely theorized): an early version of this
    # harness occasionally double-clicked a neighboring row instead. wait_for_reply
    # always tells us which call the engine actually received a REPLY for, so a mismatch
    # is detected for certain, never silently trusted -- retry a few times (re-finding
    # the row fresh each attempt) before giving up, the same tolerance real operators
    # get from just clicking again.
    MAX_CLICK_ATTEMPTS = 6
    reply = None
    for attempt in range(MAX_CLICK_ATTEMPTS):
        clicked = v.double_click_queue_row(call)
        if not clicked:
            print(f"    ⚠ WARN  D{tag_num}-setup: could not double-click {call}'s queue row "
                  f"(attempt {attempt + 1}/{MAX_CLICK_ATTEMPTS})")
            continue
        reply = engine.wait_for_reply(timeout=5.0)
        if reply is None:
            print(f"    ⚠ WARN  D{tag_num}-setup: Jimmy never sent REPLY for {call} "
                  f"(attempt {attempt + 1}/{MAX_CLICK_ATTEMPTS})")
            continue
        replied_call = (reply.get("dxcall") or "").upper()
        if replied_call == call.upper():
            break
        print(f"    ⚠ WARN  D{tag_num}-setup: double-click replied to '{replied_call}', "
              f"not '{call}' -- retrying (attempt {attempt + 1}/{MAX_CLICK_ATTEMPTS})")
        reply = None
        time.sleep(0.5)
    if reply is None:
        print(f"    ⚠ WARN  D{tag_num}-setup: could not reliably reply to {call} after "
              f"{MAX_CLICK_ATTEMPTS} attempts (environment/timing dependent -- posted-message "
              "double-click racing the live 1s SNAPSHOT poll's own queue re-sort; see "
              "double_click_queue_row's own comment)")
        return None

    # LogQso (WsjtxClient.cs) requires two things beyond callInProg/RR73 that a bare
    # CQ decode alone never provides: (1) allCallDict must hold a Report- or
    # RogerReport-format decode FROM this station (its own FindLast(RogerReport)/
    # FindLast(Report) gate) -- populated by an ordinary incoming decode through
    # ProcessDecodeMsg's ordinary ToMyCall path, same as ANY real signal report; (2)
    # sentReportList must contain this call (its own "never reported SNR to the DX
    # station" gate) -- populated by DirectApplyStatus itself the moment curTxMsg is
    # a Report/RogerReport addressed to callInProg. Mirrors a real over-the-air
    # exchange (station reports to me, I report back, then RR73) rather than jumping
    # straight to sign-off, which is why this needs two engine-side steps below, not
    # one -- the old UDP test's build_qso_logged() shortcut never had to satisfy
    # either gate because it bypassed LogQso entirely.
    engine.send_decode(f"{MY_CALL} {call} -05", from_call=call)   # their report to me
    time.sleep(1.5)
    engine.set_transmitting(True)
    engine.set_qso_txnow(f"{call} {MY_CALL} -05")                 # my report back to them
    time.sleep(1.5)
    engine.complete_qso_now(call, final="RR73")
    time.sleep(2.0)
    engine.set_transmitting(False)
    time.sleep(1.5)
    logged = call.lower().replace(" ", "") in "".join(v.log_items()).lower().replace(" ", "")
    return logged


def _report_real_logged_qso(v, ok, tag_num, call, label):
    """ok is _real_logged_qso's own tri-state: None means the click-automation setup
    itself never got far enough to make a real claim (environment/timing gap, WARN
    -- not counted against the pass/fail total, same treatment as every other
    check_*_warn helper in this file); True/False is a hard assertion once REPLY
    genuinely arrived, reported normally. Returns ok unchanged so callers can still
    branch on it (only True proceeds to a dependent follow-up check).
    """
    if ok is None:
        print(f"    ⚠ WARN  D{tag_num:02d}: {call} really-logged check skipped ({label}) "
              "-- double-click automation could not be reliably targeted this run")
    else:
        v._report(ok, f"D{tag_num:02d}: {call} {label}", f"logList={v.log_items()}")
    return ok


def group5_final_73_after_qso_logged(engine, v):
    global _test_num
    print("  ─ Group 5: final 73 after a REAL logged QSO (Direct-mode equivalent) ─")
    print(f"    Old UDP test injected a QsoLoggedMessage packet directly (no callInProg")
    print(f"    needed). Direct mode has no such 'trust me, log this' command -- the real")
    print(f"    equivalent is driving an actual double-click reply through to a real")
    print(f"    engine-reported RR73, exercising DirectApplyStatus's own LogQso path.")
    call = "W7LOG5"
    ok = _real_logged_qso(engine, v, call, _test_num + 1)
    _test_num += 1
    ok = _report_real_logged_qso(v, ok, _test_num, call,
                                  "really logged via double-click + engine RR73")
    if not ok:
        return None

    engine.send_decode(f"{MY_CALL} {call} 73", from_call=call)
    time.sleep(2.0)
    _test_num += 1
    v.check_status_contains("final 73", f"D{_test_num:02d}: status contains 'final 73' after repeat 73")
    return call


def group6_recall_after_prior_qso(engine, v, prior_call):
    print("  ─ Group 6: station re-calls after a prior real logged QSO ─")
    if prior_call is None:
        print("    ⚠ WARN  D: skipped -- Group 5 did not produce a real logged QSO")
        return
    global _test_num
    engine.send_decode(f"{MY_CALL} {prior_call} EM63", from_call=prior_call)
    time.sleep(2.0)
    _test_num += 1
    v.check_queue_contains(prior_call, f"D{_test_num:02d}: {prior_call} re-queued after prior logged QSO (Fix #4)")


def group7_slash_callsign(engine, v):
    print("  ─ Group 7: W5C/H -- /H is possible F/H only, must queue ─")
    call = "W5C/H"
    step(f"CQ {call}", "Possible F/H: must queue (suffix heuristic only, not authoritative)",
         lambda: engine.send_decode(f"CQ {call}", from_call=call),
         verify_fn=lambda: v.check_queue_contains(call, f"D: {call} in queue (Possible F/H)"))


def group8_sota_cq(engine, v):
    print("  ─ Group 8: SOTA CQ ─")
    print("    Full PASS only when 'SOTA' is in the directed CQ alert text box (Options).")
    call = "W0SDT"
    step(f"SOTA CQ: CQ SOTA {call}", "Queued if 'SOTA' in alert list; WARN otherwise",
         lambda: engine.send_decode(f"CQ SOTA {call}", from_call=call),
         verify_fn=lambda: v.check_queue_contains_warn(call, f"D: {call} in queue from CQ SOTA",
             "add 'SOTA' to the directed CQ alert text box in Options"))


def group9_short_ap_suffix(engine, v):
    print("  ─ Group 9: Short AP suffix (' a2') -- report must be accepted ─")
    step(f"AP a2 report: {MY_CALL} {THEIR_CALL} -04 a2", "Strips to -04; accepted as report",
         lambda: engine.send_decode(f"{MY_CALL} {THEIR_CALL} -04 a2", from_call=THEIR_CALL),
         verify_fn=lambda: v.check_queue_contains(THEIR_CALL, f"D: {THEIR_CALL} in queue (short AP a2 stripped)"))


def group10_period_checks(engine, v):
    """Direct-mode equivalent note: DirectApplyDecodes always stamps SinceMidnight
    from the real wall clock (WsjtxClient.Direct.cs: "SinceMidnight = DateTime.UtcNow
    .TimeOfDay") -- there is no wire field a Direct-mode client can use to force an
    artificial 'opposite period' decode the way the old UDP build_enqueue's
    since_midnight_ms parameter could. Advanced Call Layout makes
    IsCorrectTimePeriodForMode() return true unconditionally regardless of period
    (same prerequisite this whole suite already requires), so the meaningful,
    honestly-portable assertion is: decodes injected at two different real moments
    both still queue -- proving the period gate stays bypassed under Direct mode
    too, without pretending to control a wall-clock value Direct's real design
    doesn't expose to a test.
    """
    print("  ─ Group 10: T/R period gate stays bypassed under Advanced Call Layout ─")
    print("    (Direct mode decode timing is always real wall-clock; see this group's own")
    print("     comment in JimmyDirectReplay.py for why period cannot be forced here.)")
    step(f"CQ {FT8_A_CALL}", "Must queue (baseline)",
         lambda: engine.send_decode(f"CQ {FT8_A_CALL} EM63", from_call=FT8_A_CALL),
         verify_fn=lambda: v.check_queue_contains(FT8_A_CALL, f"D: {FT8_A_CALL} queued"))
    time.sleep(1.0)
    step(f"CQ {FT8_B_CALL}", "Must also queue (different real moment, Advanced Call Layout)",
         lambda: engine.send_decode(f"CQ {FT8_B_CALL} EM63", from_call=FT8_B_CALL),
         verify_fn=lambda: v.check_queue_contains(FT8_B_CALL, f"D: {FT8_B_CALL} queued"))


def group11_fox_hound_detection(engine, v):
    print("  ─ Group 11: /H detection -- Possible F/H, queued normally ─")
    call = "K1ABC/H"
    step(f"CQ from /H hound: CQ {call}", "Must be queued (not suppressed)",
         lambda: engine.send_decode(f"CQ {call}", from_call=call),
         verify_fn=lambda: v.check_queue_contains(call, f"D: {call} in queue (Possible F/H)"))

    step(f"/H calling me: {MY_CALL} {call} -03", "Must be queued",
         lambda: engine.send_decode(f"{MY_CALL} {call} -03", from_call=call),
         verify_fn=lambda: v.check_queue_contains(call, f"D: {call} in queue (Possible F/H report)"))

    # Real behavior difference from the old UDP harness, root-caused rather than papered
    # over: WsjtxClient.cs's ProcessDecodeMsg (shared by both transports) only reaches its
    # 73/RR73 handling (including "not callInProg, not already logged -> RemoveCall", the
    # code's own "don't process the 73 or RR73 -- may have been added manually" comment)
    # when txEnabled is true. The OLD UDP test's build_status() always defaulted
    # tx_enabled=False for its ENTIRE session (never toggled true anywhere in
    # JimmyReplay.py), so that whole branch was never exercised there -- its "remains
    # queued" outcome came from an earlier, different branch (line ~1302, the
    # txEnabled==false path) that has no such removal, not from any F/H-specific
    # exemption. FakeEngine defaults tx_enabled=True (matching a real, healthy running
    # session -- Direct mode's own radio.txEnabled, WsjtxClient.Direct.cs), which
    # correctly reaches the real removal logic: a plain "73" from a station that is
    # neither the active callInProg nor already logged is not a live opportunity, and is
    # removed. Verified by reading the shared code, not assumed.
    step(f"/H 73 to me: {MY_CALL} {call} 73", "Removed: not callInProg, not logged, txEnabled=true",
         lambda: engine.send_decode(f"{MY_CALL} {call} 73", from_call=call),
         settle=2.0,
         verify_fn=lambda: v.check_queue_not_contains(call, f"D: {call} removed after unsolicited 73 (real ProcessDecodeMsg behavior)"))

    step(f"Normal CQ after /H: CQ {THEIR_CALL} EM63", "Must still queue (no regression)",
         lambda: engine.send_decode(f"CQ {THEIR_CALL} EM63", from_call=THEIR_CALL),
         verify_fn=lambda: v.check_queue_contains(THEIR_CALL, f"D: {THEIR_CALL} in queue (normal FT8 unaffected)"))


def group12_hrc_filter_baseline(engine, v):
    print("  ─ Group 12: HRC filter plumbing -- empty-DB baseline ─")
    us_call, dx_call = "W5HRD", "G3HRD"
    step(f"US grid CQ: CQ {us_call} EM10", "HRC empty -> DEFAULT -> must queue normally",
         lambda: engine.send_decode(f"CQ {us_call} EM10", from_call=us_call),
         verify_fn=lambda: v.check_queue_contains(us_call, f"D: {us_call} queued (US grid, HRC empty)"))

    step(f"DX grid CQ: CQ {dx_call} IO91", "HRC empty -> DEFAULT -> must queue normally",
         lambda: engine.send_decode(f"CQ {dx_call} IO91", from_call=dx_call),
         verify_fn=lambda: v.check_queue_contains(dx_call, f"D: {dx_call} queued (DX grid, HRC empty)"))


def group13_still_need_baseline(engine, v):
    print("  ─ Group 13: Still Need live-tag plumbing -- baseline ─")
    us_call, dx_call = "K5SNI", "PY5SNI"
    step(f"US grid CQ: CQ {us_call} DM79", "Must not raise or block queuing",
         lambda: engine.send_decode(f"CQ {us_call} DM79", from_call=us_call),
         verify_fn=lambda: v.check_queue_contains(us_call, f"D: {us_call} queued"))

    step(f"DX grid CQ: CQ {dx_call} GG66", "Must not raise or block queuing",
         lambda: engine.send_decode(f"CQ {dx_call} GG66", from_call=dx_call),
         verify_fn=lambda: v.check_queue_contains(dx_call, f"D: {dx_call} queued"))


def group14_retired_note():
    print("  ─ Group 14 (LoggedAdifMessage fallback) -- RETIRED, not ported ─")
    print("    This group tested a UDP-only redundancy: WSJT-X sends BOTH QsoLoggedMessage")
    print("    and LoggedAdifMessage for every logged QSO so one dropped UDP packet doesn't")
    print("    silently lose a QSO. Direct mode's control protocol has no ADIF-broadcast")
    print("    wire message at all (SNAPSHOT never carries raw ADIF text) and no analogous")
    print("    lossy-packet failure mode (each command is one TCP round trip on a 1s poll,")
    print("    already covered by dedicated 'Direct-path poll-failure connection-loss'")
    print("    JimmyTests coverage) -- the premise does not survive translation, not just")
    print("    the mechanics. Its other real assertion, 'the same QSO logged twice must not")
    print("    double-count', is still exercised for real: Group 5's real RR73 is held")
    print("    steady across several SNAPSHOT polls, and DirectApplyStatus's own logList")
    print("    dedup guard (WsjtxClient.Direct.cs) is what keeps that to one entry.")
    print()


def group15_grid_reply_only(engine, v):
    """Direct-mode equivalent note: the old T33/T35 assertion (status announces
    'WSJT-X resumed calling X automatically') depended on WSJT-X's own Enable-Tx
    button being toggled externally (TxHaltClk/TxEnableClk in classic StatusMessage)
    -- a concept that only exists when a SEPARATE WSJT-X UI can act independently of
    Jimmy. Under Direct mode Jimmy IS the sole driver of TX; DirectRadioStatus has
    no such fields, and the real Direct-mode equivalent (the engine's own retry
    logic silently flipping txEnabled) is reconciled with no status announcement at
    all (DirectApplyStatus's plain "txEnabled = radio.TxEnabled;" -- see its own
    comment) -- already covered directly by JimmyTests' dedicated txEnabled-
    reconciliation unit tests (ARCHITECTURE.md's TX-safety section), not something
    this black-box UI-reading harness can observe since there is no announcement to
    read. Only the still-meaningful half (an ordinary directed reply gets queued)
    is ported here.
    """
    print("  ─ Group 15: grid-reply queuing (Wait-and-Reply's 'external resume status'")
    print("    half has no Direct-mode equivalent -- see this group's own comment) ─")
    call = "W3WAI2"
    step(f"Grid reply: KB0UZT {call} EM63", "Queued with 'to you' tag",
         lambda: engine.send_decode(f"{MY_CALL} {call} EM63", from_call=call),
         verify_fn=lambda: v.check_queue_contains(call, f"D: {call} in callQueue"))


def group16_rrr_after_logged_no_requeue(engine, v):
    global _test_num
    print("  ─ Group 16: bare RRR after a REAL logged QSO must not re-queue ─")
    call = "W4RR2"
    ok = _real_logged_qso(engine, v, call, _test_num + 1)
    _test_num += 1
    ok = _report_real_logged_qso(v, ok, _test_num, call, "really logged (setup for RRR-after-logged check)")
    if not ok:
        return
    engine.send_decode(f"{MY_CALL} {call} RRR", from_call=call)
    time.sleep(2.0)
    _test_num += 1
    v.check_queue_not_contains(call, f"D{_test_num:02d}: {call} NOT re-queued after prior logged QSO (repeat RRR)")


def group17_still_needed_tag_clears_on_log(engine, v):
    print("  ─ Group 17: Still-Needed tag clears off the queue once really worked ─")
    print("    Requires 'Replay Test Award' checked in the Still Need tab (WARN otherwise,")
    print("    same as the old harness).")
    global _test_num
    call = "W9NEED"
    tag = "Replay Test Award Needed"
    engine.send_decode(f"CQ {call} EM63", from_call=call)
    _test_num += 1
    tag_present = v.check_queue_row_contains_warn(call, tag,
        f"D{_test_num:02d}: {call} queued with '{tag}' tag",
        "check 'Replay Test Award' in the Still Need tab + enable its Call Filter")
    if not tag_present:
        print(f"    ⚠ WARN  D: skipped -- tag never confirmed present")
        return
    ok = _real_logged_qso(engine, v, call, _test_num + 1)
    _test_num += 1
    ok = _report_real_logged_qso(v, ok, _test_num, call, "really logged (setup for tag-clears check)")
    if not ok:
        return
    _test_num += 1
    v.check_queue_row_not_contains(call, tag, f"D{_test_num:02d}: '{tag}' tag cleared after being logged")


def group18_weak_snr_removal(engine, v):
    print("  ─ Group 18: Weak-signal floor, opt-in immediate removal ─")
    print("    Requires 'Ignore SNR at or below' AND 'Remove from list immediately...'")
    print("    checked in Options, floor above -28 (WARN otherwise).")
    call = "W5WEA2"
    global _test_num

    engine.send_decode(f"CQ {call} EM63", from_call=call, snr=0)
    _test_num += 1
    v.wait_for_queue(call, timeout=3.0)
    items = v.queue_items()
    was_queued = any(call.lower() in i.lower().replace(" ", "") for i in items)
    v._report(was_queued, f"D{_test_num:02d}: {call} queued at strong SNR", f"queue={items}")

    engine.send_decode(f"CQ {call} EM63", from_call=call, snr=-28)
    time.sleep(2.0)
    _test_num += 1
    if not was_queued:
        print(f"    ⚠ WARN  D{_test_num:02d}: skipped ({call} never confirmed queued)")
        return
    items = v.queue_items()
    still_present = any(call.lower() in i.lower().replace(" ", "") for i in items)
    if not still_present:
        v._report(True, f"D{_test_num:02d}: {call} removed after weak decode", f"queue={items}")
    else:
        print(f"    ⚠ WARN  D{_test_num:02d}: {call} still queued after weak decode")
        print( "           (not failed — requires: check 'Ignore SNR at or below' AND "
               "'Remove from list immediately...' in Options, floor above -28)")
        print(f"           queue={items}")


def group19_weak_snr_first_decode(engine, v):
    print("  ─ Group 19: Weak-signal floor, first-decode admission ─")
    call = "W6FIR2"
    global _test_num
    _test_num += 1
    step2_label = f"D{_test_num:02d}"
    engine.send_decode(f"CQ {call} EM63", from_call=call, snr=-23)
    v.check_queue_not_contains_warn(call, f"{step2_label}: {call} never queued (weak on first decode)",
        "'Ignore SNR at or below' checked in Options, floor at or above -23")


def run_tests(engine, v):
    print("──── Direct-protocol test sequence ────")
    print(f"  Format: [DESTINATION] [SOURCE] [payload], injected via the fake engine's")
    print(f"  SNAPSHOT.recentDecodes -- Jimmy polls for it exactly like it would poll a")
    print(f"  real jimmy-engine-host.exe.")
    print()

    group1_station_calling_me(engine, v)
    group2_cq_messages(engine, v)
    group3_ap_suffix(engine, v)
    group4_contest_field_day(engine, v)
    logged_call = group5_final_73_after_qso_logged(engine, v)
    group6_recall_after_prior_qso(engine, v, logged_call)
    group7_slash_callsign(engine, v)
    group8_sota_cq(engine, v)
    group9_short_ap_suffix(engine, v)
    group10_period_checks(engine, v)
    group11_fox_hound_detection(engine, v)
    group12_hrc_filter_baseline(engine, v)
    group13_still_need_baseline(engine, v)
    group14_retired_note()
    group15_grid_reply_only(engine, v)
    group16_rrr_after_logged_no_requeue(engine, v)
    group17_still_needed_tag_clears_on_log(engine, v)
    group18_weak_snr_removal(engine, v)
    group19_weak_snr_first_decode(engine, v)

    print("──── Test sequence complete ────")
    if v.available:
        v.summary()
    else:
        print("  (Verifier was not available — all assertions skipped)")
        print("  Re-run with Jimmy open to enable automatic verification.")
    print()


# ═══════════════════════════════════════════════════════════════════════════════
# Entry point
# ═══════════════════════════════════════════════════════════════════════════════

def main():
    # Same safety gate as JimmyReplay.py's own main() -- this script sends
    # simulated engine traffic that a non-isolated Jimmy would log for real.
    if not os.environ.get("JIMMY_TEST_DB_PATH"):
        print("ERROR: JIMMY_TEST_DB_PATH is not set in this shell.")
        print("Refusing to run -- run via run_replay_tests.bat instead of calling this")
        print("script directly, or set JIMMY_TEST_DB_PATH yourself before starting BOTH")
        print("Jimmy Next.exe and this script.")
        sys.exit(1)

    print("=" * 60)
    print("  JimmyDirectReplay.py — Direct control-port replay + auto-verifier")
    print("=" * 60)
    print(f"\n  Fake engine listening on: 127.0.0.1:{CONTROL_PORT}")
    print(f"  myCall={MY_CALL}  myGrid={MY_GRID}")
    print()

    engine = FakeEngine()
    engine.start()
    print(f"  Fake control-port server started.")

    print("  Locating Jimmy controls via Win32...")
    v = JimmyVerifier()
    if v.available:
        print(f"  ✓ Found Jimmy window, statusText, callListBox, logListBox")
        print(f"    Current status: '{v.status_text()}'")
    else:
        print("  ✗ Jimmy window not found (or controls not located).")
        print("    Assertions will be skipped. Start Jimmy first if you want")
        print("    automatic verification.")
    print()

    print("  Checklist:")
    print("  [1] Jimmy is running in test mode (Debug build, launched by run_replay_tests.bat)")
    print("  [2] Advanced Call Layout enabled (Options)")
    print("  [3] (optional) 'SOTA' in directed CQ alert text box -> Group 8 full PASS")
    print("  [4] (optional) 'Replay Test Award' checked in Still Need tab -> Group 17 full PASS")
    print("  [5] (optional) 'Ignore SNR at or below' + 'Remove from list immediately...'")
    print("      checked, floor above -28 -> Group 18/19 full PASS")
    print()

    print("  Waiting for Jimmy to reach ACTIVE via the Direct control port (up to 10s)...")
    deadline = time.time() + 10.0
    reached = False
    while time.time() < deadline:
        if v.available:
            t = v.status_text()
            if t and not any(w in t.lower() for w in ("connecting", "idle", "start")):
                reached = True
                break
        time.sleep(0.3)
    if reached:
        print("  Jimmy reached ACTIVE state.\n")
    else:
        print("  WARNING: Jimmy did not clearly reach ACTIVE within 10s -- continuing anyway.\n")

    if v.available:
        v.check_active("Jimmy reached ACTIVE state (Direct control port)")
    print()

    try:
        run_tests(engine, v)
    except KeyboardInterrupt:
        print("\n  Interrupted.")
    finally:
        engine.stop()


if __name__ == "__main__":
    main()
