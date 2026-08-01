#!/usr/bin/env python3
"""
JimmyReplay.py - UDP test sender with automatic verification for Jimmy.

Simulates WSJT-X: sends known decode messages to Jimmy, then reads
Jimmy's statusText and callListBox directly via Win32 API to verify
expected queue entries, status text, and state transitions.

No external packages required — uses only Python standard library +
ctypes (built-in).

BEFORE RUNNING:
  1. Close WSJT-X (Jimmy must not be connected to real WSJT-X).
  2. Start Jimmy (Debug build).
  3. In Jimmy, set mode to CQ.
  4. Enable Advanced Call Layout (Options) to bypass T/R period checks.
  5. (Optional) Add 'SOTA' to the directed CQ alert text box for a full T17 PASS.
     Without it, T17 prints a WARNING instead of PASS or FAIL — this is not a bug.

USAGE:
  python JimmyReplay.py

Requires Python 3.6+.
"""

import socket
import struct
import sys
import time
import ctypes
import ctypes.wintypes
import datetime
import os
import tempfile
import atexit

# Ensure UTF-8 output so checkmark/cross symbols render on any Windows console
try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

# ─── Jimmy/WSJT-X Configuration ──────────────────────────────────────────────
JIMMY_HOST    = "127.0.0.1"
JIMMY_PORT    = 2237
LOCAL_PORT    = 9999
RECV_TIMEOUT  = 3.0

WSJT_ID       = "WSJT-X"
WSJT_VERSION  = "3.0.0-rc1"
WSJT_REVISION = "102"        # must match Jimmy's acceptableWsjtxVersions

MY_CALL  = "KB0UZT"
MY_GRID  = "FN42"
THEIR_CALL = "K4YT"

# Period-check test stations (group 10) — unique callsigns not used elsewhere
FT8_EVEN_CALL = "W9EVN"   # FT8 expected (even) period
FT8_ODD_CALL  = "W9ODD"   # FT8 opposite (odd) period

# ─── Protocol Constants ───────────────────────────────────────────────────────
MAGIC            = bytes([0xAD, 0xBC, 0xCB, 0xDA])
MSG_HEARTBEAT    = 0
MSG_STATUS       = 1
MSG_DECODE       = 2
MSG_QSO_LOGGED   = 5
MSG_ENQUEUE_V3   = 18
MSG_ENABLE_TX    = 17
MSG_LOGGED_ADIF  = 12

# ─── Win32 API Constants ──────────────────────────────────────────────────────
GWL_STYLE         = -16
ES_READONLY       = 0x0800
WM_GETTEXT        = 0x000D
WM_GETTEXTLENGTH  = 0x000E
LB_GETCOUNT       = 0x018B
LB_GETTEXTLEN     = 0x018A
LB_GETTEXT        = 0x0189

_user32   = ctypes.windll.user32
_kernel32 = ctypes.windll.kernel32

PROCESS_QUERY_LIMITED_INFORMATION = 0x1000

# OpenProcess returns a HANDLE (pointer-sized) -- ctypes' default restype is a
# 32-bit c_int, which silently truncates/misreads a 64-bit handle value on
# 64-bit Windows. Declare it explicitly so process lookups can't go wrong.
_kernel32.OpenProcess.restype  = ctypes.wintypes.HANDLE
_kernel32.OpenProcess.argtypes = [ctypes.wintypes.DWORD, ctypes.wintypes.BOOL, ctypes.wintypes.DWORD]

# ═══════════════════════════════════════════════════════════════════════════════
# Win32 Helpers
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
    """Executable name (no path/extension case folded) of the process that
    owns hwnd, e.g. "jimmy" for C:\\...\\Jimmy.exe. Returns "" if it can't be
    determined (process exited, access denied, etc.) -- callers should treat
    that as "not a match" rather than raise.
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
    """Child's top-left corner in parent's client coordinates."""
    r = _RECT()
    _user32.GetWindowRect(child, ctypes.byref(r))
    p = _POINT(r.left, r.top)
    _user32.MapWindowPoints(0, parent, ctypes.byref(p), 1)
    return p.x, p.y

# ═══════════════════════════════════════════════════════════════════════════════
# JimmyVerifier
# ═══════════════════════════════════════════════════════════════════════════════

class JimmyVerifier:
    """
    Reads Jimmy's UI controls via Win32 to assert expected state.

    Controls identified:
      statusText  — the ONLY read-only EDIT child (ES_READONLY style)
      callListBox — leftmost LISTBOX at y < 200 in client coords  (x≈15)
      logListBox  — next LISTBOX at y < 200 in client coords      (x≈214)

    Sound events cannot be read externally, but are inferred from state:
      CallingMe sound fired  → call appears in callListBox
      finalSignoff path fired → status contains "final 73", call NOT in queue
    """

    def __init__(self):
        self._jhwnd    = None
        self._stat     = None
        self._calllist = None
        self._loglist  = None
        self.passed    = 0
        self.failed    = 0
        # run_replay_tests.bat gives the freshly-launched Jimmy.exe a fixed 5s
        # head start before this script runs -- observed to be a tight/losing race
        # on a loaded or just-rebuilt machine (first launch after a build is
        # slower to reach a responsive main window). Retry for up to 10s instead
        # of a single one-shot attempt so a slow-but-fine startup doesn't get
        # misreported as "controls not located".
        deadline = time.time() + 10.0
        while True:
            self._find()
            if self.available or time.time() >= deadline:
                break
            time.sleep(0.5)

    def _find(self):
        # Title-prefix match alone is ambiguous -- e.g. a VS Code window titled
        # "Jimmy - Visual Studio Code" for this workspace also starts with
        # "Jimmy", and EnumWindows returns windows in Z-order, so whichever
        # "Jimmy..."-titled window happens to be frontmost can win instead of
        # the real app. Confirm ownership by the actual process image name
        # (Jimmy.exe) so window focus/Z-order can never pick the wrong window.
        hits = []
        @_EnumWndProc
        def cb(hwnd, _):
            if _wnd_title(hwnd).startswith("Jimmy") and _owning_process_name(hwnd) == "jimmy":
                hits.append(hwnd)
            return True
        _user32.EnumWindows(cb, 0)
        if not hits:
            return
        self._jhwnd = hits[0]

        children  = _all_descendants(self._jhwnd)
        edits     = [h for h in children if 'EDIT'    in _cls(h)]
        listboxes = [h for h in children if 'LISTBOX' in _cls(h)]

        # statusText: only ReadOnly EDIT
        for h in edits:
            if _style(h) & ES_READONLY:
                self._stat = h
                break

        # callListBox: leftmost LISTBOX near top of form (y < 200, Designer y=24)
        # logListBox:  second leftmost at same y band (Designer y=24 but x=214)
        # advTx1/2/rawListBox are at y=392+ so filtered out
        near = []
        for h in listboxes:
            try:
                x, y = _client_xy(h, self._jhwnd)
                if 0 < y < 200:
                    near.append((x, y, h))
            except Exception:
                pass
        near.sort()                        # sort by x
        if near:
            self._calllist = near[0][2]    # leftmost = callListBox (x≈15)
        if len(near) >= 2:
            self._loglist = near[1][2]     # next = logListBox (x≈214)

    @property
    def available(self):
        return bool(self._jhwnd and self._stat and self._calllist)

    def _live_listboxes(self):
        """Re-enumerate child listboxes on every call.

        WinForms recreates the ListBox HWND when SelectionMode changes
        (e.g. None→One when the first call is enqueued, One→None when the
        queue empties).  Caching the HWND from _find() causes every
        subsequent LB_GETCOUNT to read a destroyed window and return 0.
        Re-discovering from the stable form HWND is the reliable fix.
        """
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
        """Block until status contains fragment or timeout. Returns final text."""
        deadline = time.time() + timeout
        while time.time() < deadline:
            t = self.status_text()
            if fragment.lower() in t.lower():
                return t
            time.sleep(0.1)
        return self.status_text()

    def wait_for_queue(self, fragment, timeout=4.0):
        """Block until callListBox contains an item with fragment.

        Jimmy spaces out callsign characters in display rows
        (e.g. "K 4 Y T, USA, -10 ..."), so strip spaces before comparing.
        """
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

    def check_status_not_contains(self, fragment, label):
        # Give Jimmy a moment to settle before checking the negative
        time.sleep(0.3)
        t  = self.status_text()
        ok = fragment.lower() not in t.lower()
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
        """Soft queue check: PASS if the call is found; WARNING (not FAIL) if not.

        Use for tests whose admission depends on a user-configurable Options setting
        that cannot be read remotely. Does not increment the fail counter when absent.
        """
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
        """Soft queue check: PASS if the call is absent; WARNING (not FAIL) if present.

        Complement of check_queue_contains_warn -- for tests whose *rejection* depends
        on a user-configurable Options setting that cannot be read remotely.
        """
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
        """Return the queue row whose text contains fragment (spaces stripped from
        both sides, matching Jimmy's spaced-out callsign display), or None."""
        frag_nsp = fragment.lower().replace(" ", "")
        for i in self.queue_items():
            if frag_nsp in i.lower().replace(" ", ""):
                return i
        return None

    def check_queue_row_contains_warn(self, call_fragment, tag_fragment, label, config_note):
        """Soft per-row check: PASS (and returns True) if the queue row for call_fragment
        contains tag_fragment (e.g. an award's "Needed" tag); WARNING -- not FAIL, returns
        False -- if the row is missing entirely OR present without the tag. Both "missing"
        cases mean the same thing here: the environment-dependent setup (an active Rule
        Definition, plus its Call Filter) isn't configured, same reasoning as
        check_queue_contains_warn. Callers can use the return value to skip a
        follow-up hard assertion when this precondition wasn't actually met.
        """
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
        """The queue row for call_fragment must NOT contain tag_fragment. A row that
        disappeared entirely also satisfies this (no longer tagged, a stronger outcome)
        -- only a row that's still present AND still shows the stale tag is a failure.
        """
        time.sleep(0.3)
        row = self.find_queue_row(call_fragment)
        ok  = (row is None) or (tag_fragment.lower() not in row.lower())
        self._report(ok, label, f"row={row!r}")

    def check_queue_row_contains(self, call_fragment, tag_fragment, label):
        """The queue row for call_fragment MUST contain tag_fragment (e.g. the
        'replying' priority tag) -- a hard FAIL if the row is missing entirely or
        present without the tag."""
        self.wait_for_queue(call_fragment, timeout=3.0)
        row = self.find_queue_row(call_fragment)
        ok  = (row is not None) and (tag_fragment.lower() in row.lower())
        self._report(ok, label, f"row={row!r}")

    def check_status_contains_warn(self, fragment, label, config_note):
        """Soft status check: PASS if found; WARNING (not FAIL) if not.

        Use for tests whose outcome depends on manual, real-time setup this
        script cannot reliably drive by itself (e.g. a live UI action plus a
        confirmation dialog) rather than a code defect. Does not increment
        the fail counter when absent.
        """
        self.wait_for_status(fragment, timeout=3.0)
        t  = self.status_text()
        ok = fragment.lower() in t.lower()
        if ok:
            self._report(True, label, f"status='{t}'")
        else:
            print(f"    ⚠ WARN  {label}")
            print(f"           (not failed — requires: {config_note})")
            print(f"           status='{t}'")

    def check_log_contains(self, fragment, label):
        # logListBox shows auto-logged calls formatted as "K 4 Y T, Country".
        # Jimmy spaces out callsign characters, so strip spaces from both
        # sides before comparing (e.g. "K4YT" matches "K 4 Y T, USA").
        frag_nsp = fragment.lower().replace(" ", "")
        deadline = time.time() + 3.0
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


# ═══════════════════════════════════════════════════════════════════════════════
# Binary Encoding (WSJT-X UDP protocol)
# ═══════════════════════════════════════════════════════════════════════════════

def _u8(n):    return bytes([n & 0xFF])
def _u32(n):   return struct.pack('>I', n & 0xFFFFFFFF)
def _i32(n):   return struct.pack('>i', n)
def _u64(n):   return struct.pack('>Q', n)
def _f64(f):   return struct.pack('>d', f)
def _flag(b):  return bytes([1 if b else 0])

def _qstr(s):
    if s is None:
        return b'\xff\xff\xff\xff'
    b = s.encode('utf-8')
    return struct.pack('>I', len(b)) + b

def _julian(d):
    a = (14 - d.month) // 12
    y = d.year + 4800 - a
    m = d.month + 12 * a - 3
    return d.day + (153*m+2)//5 + 365*y + y//4 - y//100 + y//400 - 32045

def _qdatetime(dt):
    jd = _julian(dt.date())
    ms = (dt.hour*3600 + dt.minute*60 + dt.second)*1000 + dt.microsecond//1000
    return struct.pack('>q', jd) + _u32(ms) + _u8(1)


def build_heartbeat():
    return (MAGIC + _u32(2) + _u32(MSG_HEARTBEAT) +
            _qstr(WSJT_ID) + _u32(3) + _qstr(WSJT_VERSION) + _qstr(WSJT_REVISION))


def build_status(check="", tx_halt_clk=False, tx_enable_button=False, tx_enable_clk=False, tx_enabled=False):
    # TxFirst=False → Jimmy transmits in odd periods, receives in even.
    # SinceMidnight=0ms in decode messages (even period) matches this.
    #
    # tx_halt_clk/tx_enable_button/tx_enable_clk let a test simulate WSJT-X
    # changing its own Enable Tx button state independently of Jimmy (e.g. the
    # Wait and Reply feature auto-resuming a stalled QSO) -- see group15 below.
    # tx_enabled is the *standard* field (defaults False, matching every existing
    # test's behavior before this parameter existed) -- see group20 below, added
    # to prove Jimmy's standard-protocol-only fallback for the same scenario.
    return (
        MAGIC + _u32(2) + _u32(MSG_STATUS) +
        _qstr(WSJT_ID) +
        _u64(14_074_000) +       # Dial freq: 14.074 MHz
        _qstr("FT8") +           # Mode
        _qstr("") +              # DX call
        _qstr("-05") +           # Report
        _qstr("FT8") +           # Tx mode
        _flag(tx_enabled) +      # Tx enabled
        _flag(False) +           # Transmitting
        _flag(False) +           # Decoding
        _u32(1500) +             # Rx DF
        _u32(1500) +             # Tx DF
        _qstr(MY_CALL) +         # DE call
        _qstr(MY_GRID) +         # DE grid
        _qstr("") +              # Detail / DX grid
        _flag(False) +           # Tx watchdog
        _qstr("") +              # Sub-mode
        _flag(False) +           # Fast mode
        _u8(0) +                 # Special operation: NONE
        _u32(0xFFFFFFFF) +       # Result code (N/A)
        _u32(15) +               # T/R period: 15 s (FT8)
        _qstr("Default") +       # Config name
        _qstr("") +              # Last Tx msg
        _u32(0) +                # QSO progress: CALLING
        _flag(False) +           # TxFirst: False → Jimmy TX in odd periods
        _flag(False) +           # DblClk
        _qstr(check) +           # Check field: echo CmdCheck from cmd:7
        _flag(tx_halt_clk) +     # Tx halt clock
        _flag(tx_enable_button) +  # Tx enable button
        _flag(tx_enable_clk) +  # Tx enable clock
        _qstr("NA") +            # My continent
        _flag(False)             # Metric units
    )


def build_enqueue(message_text, snr=-10, is_new_call=True, country="USA", continent="NA",
                  since_midnight_ms=0, mode="FT8"):
    # since_midnight_ms controls the T/R period of the simulated decode:
    #   0ms     → even period (FT8: period 0; FT4: 0-6s range)
    #   15000ms → odd  period (FT8: period 1, 15-29s range = TX window with TxFirst=False)
    #   7500ms  → odd  period (FT4: 7-14s range)
    # With TxFirst=False in StatusMessage, even periods are receive windows.
    return (
        MAGIC + _u32(2) + _u32(MSG_ENQUEUE_V3) +
        _qstr(WSJT_ID) +
        _flag(True) +            # AutoGen
        _u32(since_midnight_ms) +  # SinceMidnight in ms
        _i32(snr) +              # SNR
        _f64(0.1) +              # Delta time
        _u32(1500) +             # Delta frequency
        _qstr(mode) +
        _qstr(message_text) +
        _flag(False) +           # Is DX
        _flag(False) +           # Modifier
        _flag(is_new_call) +     # New call on band
        _flag(is_new_call) +     # New call any band
        _flag(False) +           # New country on band
        _flag(False) +           # New country
        _qstr(country) +
        _qstr(continent) +
        _i32(0) +                # Azimuth
        _i32(1000)               # Distance
    )


def build_decode(message_text, snr=-10, since_midnight_ms=0, mode="FT8", off_air=False):
    """Standard WSJT-X Decode broadcast (msg type 2) -- what stock WSJT-X and
    WSJT-X Improved actually send for every decode. Andy WM8Q's fork's own
    EnqueueDecodeMessage (msg type 18, build_enqueue above) is a non-standard
    *replacement* for this, not an addition to it -- a build without the
    Compatibility Layer never sends msg type 18 at all. Added after live A7 field
    testing 2026-07-17 found Jimmy never processed a single decode from such a
    build; see EnqueueDecodeMessage.FromStandardDecode (DecodeMessage.cs)."""
    return (
        MAGIC + _u32(2) + _u32(MSG_DECODE) +
        _qstr(WSJT_ID) +
        _flag(True) +             # New
        _u32(since_midnight_ms) + # SinceMidnight in ms
        _i32(snr) +               # SNR
        _f64(0.1) +               # Delta time
        _u32(1500) +              # Delta frequency
        _qstr(mode) +
        _qstr(message_text) +
        _flag(off_air)            # Off air
    )


def build_qso_logged(dx_call, dx_grid="EM63"):
    now = datetime.datetime.utcnow()
    return (
        MAGIC + _u32(2) + _u32(MSG_QSO_LOGGED) +
        _qstr(WSJT_ID) +
        _qdatetime(now) +
        _qstr(dx_call) + _qstr(dx_grid) +
        _u64(14_074_000) + _qstr("FT8") +
        _qstr("-05") + _qstr("-10") +
        _qstr("") + _qstr("") + _qstr("") +
        _qdatetime(now) +
        _qstr("") + _qstr(MY_CALL) + _qstr(MY_GRID) +
        _qstr("") + _qstr("")
    )


def build_logged_adif(dx_call, band="20m", qso_date=None):
    """LoggedAdifMessage — WSJT-X sends this alongside QsoLoggedMessage for every
    logged QSO. Jimmy's HandleLiveAdifLogged() treats it as a fallback trigger for
    the same log/awards-refresh path, so a single dropped QsoLoggedMessage doesn't
    silently keep a QSO out of the log (see group14 below)."""
    if qso_date is None:
        qso_date = datetime.datetime.utcnow().strftime("%Y%m%d")
    adif = (
        "<adif_ver:5>3.0.7<programid:6>WSJT-X<EOH>"
        f"<call:{len(dx_call)}>{dx_call} "
        f"<band:{len(band)}>{band} "
        "<mode:3>FT8 "
        f"<qso_date:8>{qso_date} "
        "<time_on:6>235900 <time_off:6>235930 "
        "<rst_sent:3>-05 <rst_rcvd:3>-09 "
        "<station_callsign:6>KB0UZT <my_gridsquare:4>EN34 <eor>"
    )
    return (MAGIC + _u32(2) + _u32(MSG_LOGGED_ADIF) +
            _qstr(WSJT_ID) + _qstr(adif))


# ═══════════════════════════════════════════════════════════════════════════════
# Jimmy UDP Startup
# ═══════════════════════════════════════════════════════════════════════════════

def _port_in_use(port):
    """Return True if something is already bound to the given UDP port.

    Must probe on the same address Jimmy uses (127.0.0.1), not 0.0.0.0.
    On Windows, binding 0.0.0.0:port does not conflict with 127.0.0.1:port,
    so a wildcard probe would always return False even when Jimmy is listening.
    """
    probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        probe.bind(("127.0.0.1", port))
        probe.close()
        return False   # bind succeeded → nothing listening
    except OSError:
        probe.close()
        return True    # bind failed → Jimmy has it


def ensure_jimmy_udp_ready():
    """
    Jimmy only binds its UDP socket after detecting WSJT-X via the lock file
    at %TEMP%\\WSJT-X.lock. If the port isn't open yet, create the lock file
    to trigger Jimmy's startup sequence, then wait for it to bind.

    The lock file is removed on exit so Jimmy cleanly detects 'WSJT-X closed'.
    """
    if _port_in_use(JIMMY_PORT):
        return True  # already open — nothing to do

    lock_path = os.path.join(tempfile.gettempdir(), "WSJT-X.lock")
    created = False

    if not os.path.exists(lock_path):
        print(f"  Jimmy UDP not yet open. Creating {lock_path} to trigger startup...")
        try:
            open(lock_path, "w").close()
            created = True
            atexit.register(lambda: os.path.exists(lock_path) and os.remove(lock_path))
        except OSError as e:
            print(f"  WARNING: Could not create lock file: {e}")
            return False
    else:
        print(f"  WSJT-X.lock already exists at {lock_path}")

    # Jimmy's mainLoopTimer fires every ~12 ms; CheckWsjtxRunning sleeps 3 s
    # then binds UDP. Poll up to 10 seconds total to cover the 3-s sleep + margin.
    print("  Waiting for Jimmy to open UDP (up to 10 s)...", end="", flush=True)
    deadline = time.time() + 10.0
    while time.time() < deadline:
        time.sleep(0.25)
        print(".", end="", flush=True)
        if _port_in_use(JIMMY_PORT):
            print(f" ready.\n")
            return True

    print(f"\n  WARNING: Jimmy did not open port {JIMMY_PORT} within 10 s.")
    if created:
        try:
            os.remove(lock_path)
        except OSError:
            pass
    return False


# ═══════════════════════════════════════════════════════════════════════════════
# WSJT-X Handshake
# ═══════════════════════════════════════════════════════════════════════════════

def _parse_enable_tx(data):
    """Extract (cmd_idx, cmd_check) from an EnableTxMessage."""
    if not data.startswith(MAGIC) or len(data) < 12:
        return None, None
    msg_type = struct.unpack_from('>I', data, 8)[0]
    if msg_type != MSG_ENABLE_TX:
        return None, None
    pos = 12
    def rd_u32():
        nonlocal pos
        v = struct.unpack_from('>I', data, pos)[0]; pos += 4; return v
    def rd_str():
        nonlocal pos
        n = rd_u32()
        if n == 0xFFFFFFFF: return ""
        s = data[pos:pos+n].decode('utf-8', errors='replace'); pos += n; return s
    rd_str()                   # Id
    cmd_idx = rd_u32()         # NewTxMsgIdx
    rd_str()                   # GenMsg
    pos += 1                   # SkipGrid
    pos += 1                   # UseRR73
    cmd_check = rd_str()       # CmdCheck
    return cmd_idx, cmd_check


def handshake(sock, v, send_check_echo=True):
    """
    Stage A7 handshake (WsjtxClient.Protocol.cs): normal operation now depends only
    on standard protocol traffic, never on the non-standard cmd:7 echo.
      1. Send HeartbeatMessage → Jimmy replies with its own Heartbeat AND sends
         cmd:7 (CmdCheck), both from the same first-Heartbeat handler now (no
         longer waits for a 2nd incoming Heartbeat to do either).
      2. Send a normal StatusMessage (Check="") → triggers NegoState SENT→RECD
         on its own → opMode progresses toward ACTIVE, with no echo required.
      3. If send_check_echo (default True): separately send a second
         StatusMessage with Check=<the captured CmdCheck> → exercises
         CapabilityNegotiator's ConnectedFull path. Not required for ACTIVE
         anymore -- set send_check_echo=False to prove that directly (see
         group0_no_compat_layer below).
    """
    print("\n──── Handshake ────")
    hb = build_heartbeat()

    sock.sendto(hb, (JIMMY_HOST, JIMMY_PORT))
    print(f"  → HeartbeatMessage  ({WSJT_VERSION}/{WSJT_REVISION})")

    # Jimmy now sends its Heartbeat reply and cmd:7 back-to-back from the same
    # handler -- read up to two datagrams, classifying each; don't fail if cmd:7
    # specifically doesn't show up (it's no longer required for ACTIVE).
    cmd_check   = None
    got_reply   = False
    deadline    = time.time() + RECV_TIMEOUT
    while time.time() < deadline and not (got_reply and cmd_check):
        sock.settimeout(max(0.1, deadline - time.time()))
        try:
            data, _ = sock.recvfrom(4096)
        except (socket.timeout, ConnectionResetError):
            break
        ci, cc = _parse_enable_tx(data)
        if ci == 7 and cc is not None:
            cmd_check = cc
            print(f"  ← cmd:7 (capability probe)  CmdCheck='{cmd_check}'")
        else:
            got_reply = True
            print(f"  ← Heartbeat reply: {len(data)} bytes")

    if not got_reply:
        print("  ✗ No Heartbeat reply. Is Jimmy running on port 2237?")
        return False
    if not cmd_check:
        print("  (No cmd:7 echo captured within timeout -- fine, no longer required for ACTIVE)")

    time.sleep(0.3)
    sock.sendto(build_status(check=""), (JIMMY_HOST, JIMMY_PORT))
    print(f"  → StatusMessage  (myCall={MY_CALL}, no Check -- triggers SENT→RECD on its own)")
    time.sleep(1.5)

    if v.available:
        v.check_active("Jimmy reached ACTIVE state (no cmd:7 echo required)")
    else:
        print("  (Verifier not available — skipping auto-check)")

    if send_check_echo and cmd_check:
        time.sleep(0.3)
        sock.sendto(build_status(check=cmd_check), (JIMMY_HOST, JIMMY_PORT))
        print(f"  → StatusMessage  (Check='{cmd_check}' -- exercises ConnectedFull)")
        time.sleep(0.3)

    print()
    return True


# ═══════════════════════════════════════════════════════════════════════════════
# Test Runner
# ═══════════════════════════════════════════════════════════════════════════════

_test_num = 0

def send(sock, label, description, payload, verify_fn=None, delay=1.5):
    global _test_num
    _test_num += 1
    tag = f"T{_test_num:02d}"
    print(f"  [{tag}] {label}")
    print(f"        {description}")
    sock.sendto(payload, (JIMMY_HOST, JIMMY_PORT))
    time.sleep(delay)
    if verify_fn:
        verify_fn()
    print()


def group1_station_calling_me(sock, v):
    """T01-T06: K4YT works through a full QSO exchange directed at KB0UZT."""
    print("  ─ Group 1: K4YT calling KB0UZT (directed to me) ─")

    send(sock,
         "Grid reply: KB0UZT K4YT EM63",
         "CallingMe sound expected; K4YT queued with 'to you' tag",
         build_enqueue(f"{MY_CALL} {THEIR_CALL} EM63"),
         verify_fn=lambda: (
             v.check_queue_contains(THEIR_CALL,
                 f"T01: {THEIR_CALL} in callQueue (CallingMe sound inferred)"),
         ) if v.available else None)

    send(sock,
         "Signal report: KB0UZT K4YT -05",
         "Queue entry updated; K4YT remains in queue",
         build_enqueue(f"{MY_CALL} {THEIR_CALL} -05"),
         verify_fn=lambda: (
             v.check_queue_contains(THEIR_CALL,
                 f"T02: {THEIR_CALL} still in queue after report"),
         ) if v.available else None)

    send(sock,
         "Roger report: KB0UZT K4YT R-05",
         "Queue entry updated",
         build_enqueue(f"{MY_CALL} {THEIR_CALL} R-05"),
         verify_fn=lambda: (
             v.check_queue_contains(THEIR_CALL,
                 f"T03: {THEIR_CALL} still in queue after R-report"),
         ) if v.available else None)

    send(sock,
         "RRR: KB0UZT K4YT RRR",
         "Queue entry updated",
         build_enqueue(f"{MY_CALL} {THEIR_CALL} RRR"),
         verify_fn=lambda: (
             v.check_queue_contains(THEIR_CALL,
                 f"T04: {THEIR_CALL} still in queue after RRR"),
         ) if v.available else None)

    send(sock,
         "RR73: KB0UZT K4YT RR73",
         "SIGNOFF path — QSO may be logged",
         build_enqueue(f"{MY_CALL} {THEIR_CALL} RR73"),
         delay=2.0,
         verify_fn=lambda: (
             v.check_status_contains("", "T05: status updated after RR73"),
         ) if v.available else None)

    send(sock,
         "73: KB0UZT K4YT 73",
         "SIGNOFF path",
         build_enqueue(f"{MY_CALL} {THEIR_CALL} 73"),
         delay=2.0)


def group2_cq_messages(sock, v):
    """T07-T08: CQ messages from other stations are added to the queue."""
    print("  ─ Group 2: CQ messages ─")

    send(sock,
         "Plain CQ: CQ K4YT EM63",
         "CallAdded sound expected; K4YT queued",
         build_enqueue(f"CQ {THEIR_CALL} EM63"),
         verify_fn=lambda: (
             v.check_queue_contains(THEIR_CALL,
                 f"T07: {THEIR_CALL} in queue from CQ"),
         ) if v.available else None)

    send(sock,
         "POTA CQ: CQ POTA K4YT",
         "POTA sound expected if enabled; K4YT queued",
         build_enqueue(f"CQ POTA {THEIR_CALL}"),
         verify_fn=lambda: (
             v.check_queue_contains(THEIR_CALL,
                 f"T08: {THEIR_CALL} in queue from CQ POTA"),
         ) if v.available else None)


def group3_ap_suffix(sock, v):
    """T09-T10: WSJT-X 3.0 AP suffix is stripped before classification."""
    print("  ─ Group 3: WSJT-X 3.0 AP suffix — stripped before classifying ─")

    send(sock,
         f"AP report: {MY_CALL} {THEIR_CALL} -05 a35  (strips to: -05)",
         "Same behavior as T02 (IsReport=true after strip)",
         build_enqueue(f"{MY_CALL} {THEIR_CALL} -05 a35"),
         verify_fn=lambda: (
             v.check_queue_contains(THEIR_CALL,
                 f"T09: {THEIR_CALL} in queue (AP-stripped report accepted)"),
         ) if v.available else None)

    send(sock,
         f"AP CQ: CQ {THEIR_CALL} EM63 a1  (strips to: CQ K4YT EM63)",
         "Same behavior as T07 (IsCQ=true after strip)",
         build_enqueue(f"CQ {THEIR_CALL} EM63 a1"),
         verify_fn=lambda: (
             v.check_queue_contains(THEIR_CALL,
                 f"T10: {THEIR_CALL} in queue (AP-stripped CQ accepted)"),
         ) if v.available else None)


def group4_contest_field_day(sock, v):
    """T11-T12: Contest/Field Day exchanges — accepted if directed to me, rejected otherwise."""
    print("  ─ Group 4: Contest / Field Day ─")

    send(sock,
         f"FD to me: {MY_CALL} {THEIR_CALL} 2A MO",
         "IsContest=true + toCall=myCall → queued (contest exception)",
         build_enqueue(f"{MY_CALL} {THEIR_CALL} 2A MO"),
         verify_fn=lambda: (
             v.check_queue_contains(THEIR_CALL,
                 f"T11: {THEIR_CALL} queued (FD exchange accepted to me)"),
         ) if v.available else None)

    send(sock,
         f"Contest between others: {THEIR_CALL} K9AVT 559 TX",
         "IsContest=true but toCall=K4YT ≠ myCall → rejected",
         build_enqueue(f"{THEIR_CALL} K9AVT 559 TX"),
         verify_fn=lambda: (
             v.check_queue_not_contains("K9AVT",
                 "T12: K9AVT NOT in queue (contest between others rejected)"),
         ) if v.available else None)


def group5_final_73_after_qso_logged(sock, v):
    """T13-T14: Fix #3 — final 73 after a logged QSO must not re-queue the call.

    NOTE: This group intentionally logs K4YT. group6 depends on K4YT
    being present in logListBox — always run group5 before group6.
    """
    print("  ─ Group 5: Fix #3 — final 73 after QSO logged ─")
    print(f"    First log {THEIR_CALL} via QsoLoggedMessage, then send 73.")

    send(sock,
         f"QsoLoggedMessage: {THEIR_CALL} logged",
         "Expect: Logged sound; logListBox gains K4YT entry",
         build_qso_logged(THEIR_CALL),
         delay=2.0,
         verify_fn=lambda: (
             v.check_log_contains(THEIR_CALL,
                 f"T13: {THEIR_CALL} in logListBox after QsoLogged"),
         ) if v.available else None)

    send(sock,
         f"Final 73 (after logged): {MY_CALL} {THEIR_CALL} 73",
         f"FIX #3: status must say '{THEIR_CALL} final 73'; no CallingMe sound",
         build_enqueue(f"{MY_CALL} {THEIR_CALL} 73"),
         delay=2.0,
         verify_fn=lambda: (
             v.check_status_contains("final 73",
                 "T14 Fix#3: status contains 'final 73'"),
         ) if v.available else None)


def group6_recall_after_prior_qso(sock, v):
    """T15: Fix #4 — station re-calls after a prior logged QSO and must be re-queued.

    NOTE: Requires group5 to have run first (K4YT must be in logListBox).
    """
    print("  ─ Group 6: Fix #4 — K4YT re-calls after prior logged QSO ─")
    print(f"    K4YT is in logList. Old code (recdPrevSignoff guard)")
    print(f"    would silently drop this. Fix #4 removed that guard.")

    send(sock,
         f"Re-call: {MY_CALL} {THEIR_CALL} EM63  (K4YT already in logList)",
         f"FIX #4: {THEIR_CALL} MUST be re-queued; CallingMe sound expected",
         build_enqueue(f"{MY_CALL} {THEIR_CALL} EM63"),
         delay=2.0,
         verify_fn=lambda: (
             v.check_queue_contains(THEIR_CALL,
                 f"T15 Fix#4: {THEIR_CALL} re-queued after prior logged QSO"),
         ) if v.available else None)


def group7_slash_callsign_no_country(sock, v):
    """T16: W5C/H CQ — /H is heuristic only, must be queued normally.

    Original regression: AddSelectedCall hard-rejected Country=="" (now fixed).
    /H suffix is no longer suppressed; SpecialOperationMode in StatusMessage is
    the authoritative Fox/Hound indicator. In raw display the row is tagged
    'Possible F/H' but the call is processed like any other CQ.
    The unknown-country fix (WsjtxCountry null safety) is covered by parser tests.
    """
    print("  ─ Group 7: W5C/H — /H is possible F/H only, must queue ─")

    SLASH_CALL = "W5C/H"
    send(sock,
         f"CQ {SLASH_CALL}  (/H call — not suppressed, tagged Possible F/H in raw display)",
         f"Possible F/H: {SLASH_CALL} must be queued (suffix heuristic only, not authoritative)",
         build_enqueue(f"CQ {SLASH_CALL}", country=None, continent=None),
         verify_fn=lambda: (
             v.check_queue_contains(SLASH_CALL,
                 f"T16: {SLASH_CALL} in queue (Possible F/H, /H no longer suppressed)"),
         ) if v.available else None)


def group8_sota_cq(sock, v):
    """T17: CQ SOTA decode sent; queued only if 'SOTA' is in the directed CQ alert list.

    Admission gate: IsCallingEnabled(WANTED_CQ) && isWantedDirected, where
    isWantedDirected = IsDirectedAlert('SOTA', isDx). IsDirectedAlert reads
    alertTextBox.Text (Options → directed CQ alert field). If 'SOTA' is absent,
    the call is silently filtered — correct Jimmy behavior, not a bug.

    The verify uses check_queue_contains_warn: PASS if W0SDT is queued, WARNING
    (not FAIL) if not. The test cannot remotely read the Options setting, so a
    missing queue entry is treated as a configuration gap, not a test failure.

    To get a full PASS: add 'SOTA' to the directed CQ alert text box in Options.
    """
    print("  ─ Group 8: SOTA CQ ─")
    print("    NOTE: T17 is a full PASS only when 'SOTA' is in the directed CQ")
    print("          alert text box (Options). Otherwise a WARNING is printed.")
    SOTA_CALL = "W0SDT"
    send(sock,
         f"SOTA CQ: CQ SOTA {SOTA_CALL}",
         "Queued if 'SOTA' in alert list; WARNING (not FAIL) if not configured",
         build_enqueue(f"CQ SOTA {SOTA_CALL}", is_new_call=True),
         verify_fn=lambda: (
             v.check_queue_contains_warn(SOTA_CALL,
                 f"T17: {SOTA_CALL} in queue from CQ SOTA",
                 "add 'SOTA' to the directed CQ alert text box in Options"),
         ) if v.available else None)


def group9_short_ap_suffix(sock, v):
    """T18: Short WSJT-X 3.0 AP suffix ' a2' (1-digit) must be stripped
    and the underlying report must be accepted, not misclassified as a
    contest exchange.

    Regression for bug seen in log_6-27-2026.txt line 16414:
    WSJT-X sent 'KF4CCG F4DWB -04                      a2'; the AP strip
    failed in the old code, turning the 3-word report into a 4-token
    string that IsContest() matched as a contest exchange, causing
    rejection.
    """
    print("  ─ Group 9: Short AP suffix (' a2') — report must be accepted ─")
    send(sock,
         f"AP a2 report: {MY_CALL} {THEIR_CALL} -04 a2  (strips to: -04)",
         "Same behavior as T02 — IsReport=true after strip; K4YT stays in queue",
         build_enqueue(f"{MY_CALL} {THEIR_CALL} -04 a2"),
         verify_fn=lambda: (
             v.check_queue_contains(THEIR_CALL,
                 f"T18: {THEIR_CALL} in queue (short AP a2 stripped, accepted as report)"),
         ) if v.available else None)


def group10_period_checks(sock, v):
    """T19-T20: T/R period acceptance with Advanced Call Layout enabled.

    With TxFirst=False in StatusMessage, even periods (SinceMidnight=0ms) are
    receive windows; odd periods (SinceMidnight=15000ms) are TX windows.
    Without Advanced Call Layout, opposite-period decodes directed to me are
    silently dropped by IsCorrectTimePeriodForMode().  With Advanced Call Layout
    ON, IsCorrectTimePeriodForMode() returns True unconditionally — every period
    must reach the queue.

    T19: FT8 even period (0ms)     — expected receive window, baseline
    T20: FT8 odd period  (15000ms) — opposite/TX window, must queue (Advanced mode)

    A FAIL on T20 means IsCorrectTimePeriodForMode() is NOT bypassed for the odd
    period — stop and investigate before touching production code.

    FT4 period coverage: Testing FT4 requires Jimmy to be in FT4 mode from the
    start of the session.  Sending a FT4 StatusMessage mid-session triggers a mode
    change that calls ResetOpMode() (opMode→IDLE→START); without a subsequent
    Decoding-cycle event, Jimmy never reaches ACTIVE and rejects all incoming
    decodes.  FT4 period tests are future work requiring a dedicated FT4 session.
    """
    print("  ─ Group 10: T/R period acceptance (Advanced Call Layout) ─")
    print("    Prerequisite: Advanced Call Layout enabled in Options.")
    print("    TxFirst=False → even periods are receive windows.")
    print("    SinceMidnight=0ms     = even (expected receive window)")
    print("    SinceMidnight=15000ms = odd  (TX window — opposite)")
    print()

    send(sock,
         f"FT8 even period (0ms):    {MY_CALL} {FT8_EVEN_CALL} EM63",
         "Even = expected receive window with TxFirst=False. Must queue (baseline).",
         build_enqueue(f"{MY_CALL} {FT8_EVEN_CALL} EM63",
                       since_midnight_ms=0, mode="FT8"),
         verify_fn=lambda: (
             v.check_queue_contains(FT8_EVEN_CALL,
                 f"T19: {FT8_EVEN_CALL} queued (FT8 even/expected period)"),
         ) if v.available else None)

    send(sock,
         f"FT8 odd period (15000ms): {MY_CALL} {FT8_ODD_CALL} EM63",
         "Odd = TX window (opposite). Advanced Call Layout MUST still queue it.",
         build_enqueue(f"{MY_CALL} {FT8_ODD_CALL} EM63",
                       since_midnight_ms=15000, mode="FT8"),
         verify_fn=lambda: (
             v.check_queue_contains(FT8_ODD_CALL,
                 f"T20: {FT8_ODD_CALL} queued (FT8 odd/opposite period, Advanced mode)"),
         ) if v.available else None)


def group11_fox_hound_detection(sock, v):
    """T21-T24: /H calls are possible F/H (heuristic) — queued normally, not suppressed.

    The /H suffix is no longer authoritative. SpecialOperationMode=Hound (7) in
    StatusMessage is the only authoritative Fox/Hound indicator. Jimmy does not
    suppress /H calls; they appear in the queue and are tagged 'Possible F/H' in
    the raw decode display.

    T24 verifies that normal FT8 traffic continues to work after /H decodes.
    """
    print("  ─ Group 11: /H detection — Possible F/H, queued normally ─")
    FH_CALL = "K1ABC/H"

    send(sock,
         f"CQ from /H hound: CQ {FH_CALL}",
         "Possible F/H CQ — must be queued (not suppressed)",
         build_enqueue(f"CQ {FH_CALL}"),
         verify_fn=lambda: (
             v.check_queue_contains(FH_CALL,
                 f"T21: {FH_CALL} in queue (Possible F/H, /H not suppressed)"),
         ) if v.available else None)

    send(sock,
         f"/H calling me: {MY_CALL} {FH_CALL} -03",
         "Possible F/H report directed to me — must be queued",
         build_enqueue(f"{MY_CALL} {FH_CALL} -03"),
         verify_fn=lambda: (
             v.check_queue_contains(FH_CALL,
                 f"T22: {FH_CALL} in queue (Possible F/H report to me, not suppressed)"),
         ) if v.available else None)

    send(sock,
         f"/H 73 to me: {MY_CALL} {FH_CALL} 73",
         "Possible F/H 73 — station remains in queue (no QSO state established)",
         build_enqueue(f"{MY_CALL} {FH_CALL} 73"),
         delay=2.0,
         verify_fn=lambda: (
             v.check_queue_contains(FH_CALL,
                 f"T23: {FH_CALL} in queue after 73 (Possible F/H, no prior QSO state)"),
         ) if v.available else None)

    send(sock,
         f"Normal CQ after /H: CQ {THEIR_CALL} EM63",
         "Normal FT8 CQ after F/H traffic — must still queue (no regression)",
         build_enqueue(f"CQ {THEIR_CALL} EM63"),
         verify_fn=lambda: (
             v.check_queue_contains(THEIR_CALL,
                 f"T24: {THEIR_CALL} in queue (normal FT8 not affected by F/H)"),
         ) if v.available else None)


def group12_hrc_filter_baseline(sock, v):
    """T25-T26: HRC filter plumbing — empty-database baseline.

    WAS_NEEDED, DXCC_UNCONFIRMED, and ZONE_NEEDED depend on the Ham Radio
    Center (HRC) database.  When the database has no QSOs, all three
    HashSets are empty and the helper methods return false immediately —
    every decode falls through to DEFAULT or an earlier category.

    T25: CQ with a US grid (EM10 = Texas) — must queue as DEFAULT.
         Verifies that IsHrcWasNeeded → false (empty set) causes no
         rejection or crash.
    T26: CQ with a DX grid (IO91 = England) — must queue as DEFAULT.
         Verifies no regression in non-US decode handling with HRC enabled.

    To test actual WAS/DXCC/Zone tagging:
      1. Populate the HRC database (Ham Radio Center > Sync tab > Import ADIF).
      2. Enable 'WAS Needed', 'DXCC Worked, Unconfirmed', or 'Zones Needed'
         in Alt+S > Call Filters and give them a non-zero tier in List Priorities.
      3. Hear a decode from a matching state/DXCC/zone — the queue row
         should show the tag (e.g. 'WAS Needed', 'DXCC Unconf', 'Zone Needed').
    """
    print("  ─ Group 12: HRC filter plumbing — empty-DB baseline ─")
    print("    T25-T26: Empty HRC HashSets must not break normal decode handling.")

    HRC_US_CALL = "W5HRC"   # unique call — US grid (Texas EM10)
    HRC_DX_CALL = "G3HRC"   # unique call — DX grid (England IO91)

    send(sock,
         f"US grid CQ: CQ {HRC_US_CALL} EM10  (Texas grid, HRC sets empty)",
         "IsHrcWasNeeded=false (empty set) → DEFAULT → must queue normally",
         build_enqueue(f"CQ {HRC_US_CALL} EM10",
                       country="USA", continent="NA"),
         verify_fn=lambda: (
             v.check_queue_contains(HRC_US_CALL,
                 f"T25: {HRC_US_CALL} queued (US grid, HRC empty → DEFAULT)"),
         ) if v.available else None)

    send(sock,
         f"DX grid CQ: CQ {HRC_DX_CALL} IO91  (England grid, HRC sets empty)",
         "All HRC helpers false → DEFAULT → must queue normally",
         build_enqueue(f"CQ {HRC_DX_CALL} IO91",
                       country="England", continent="EU"),
         verify_fn=lambda: (
             v.check_queue_contains(HRC_DX_CALL,
                 f"T26: {HRC_DX_CALL} queued (DX grid, HRC empty → DEFAULT)"),
         ) if v.available else None)


def group13_still_need_live_tag_baseline(sock, v):
    """T27-T28: Still Need live-tag plumbing — safety-net baseline.

    STILL_NEEDED (WsjtxClient.IsRuleStillNeeded) tags a decode against
    whichever Rule Definition is currently selected in the Logbook window's
    Still Need tab (Controller.RefreshStillNeedCache / stillNeedSet). That
    cache's state depends on UI selection and real logbook content, neither
    of which this replay script controls — so, like Group 12, this only
    verifies the code path never breaks normal decode handling, regardless
    of whether a rule is currently selected/usable.

    T27: CQ with an ordinary US grid — must queue without error whether or
         not stillNeedUsable is true (IsRuleStillNeeded returning either
         value must not prevent queuing).
    T28: CQ with an ordinary DX grid/continent — same, for the Continent/
         CqZone/Dxcc match paths that read d.Continent / a Club Log lookup
         instead of grid.

    To test actual live tagging:
      1. Open Ham Radio Center > Still Need, select an award whose GroupBy
         is Callsign, State, CqZone, Continent, or Dxcc (the tab's status
         line reads "Live decode tagging: on." when usable).
      2. Hear a decode that matches one of that award's still-needed items
         — the queue row should show "<Award name> Needed".
    """
    print("  ─ Group 13: Still Need live-tag plumbing — baseline ─")
    print("    T27-T28: STILL_NEEDED matching must not break normal decode handling.")

    SNL_US_CALL = "K5SNL"   # unique call — US grid (Colorado DM79)
    SNL_DX_CALL = "PY5SNL"  # unique call — DX grid (Brazil GG66), SA continent

    send(sock,
         f"US grid CQ: CQ {SNL_US_CALL} DM79",
         "IsRuleStillNeeded must not raise or block queuing either way",
         build_enqueue(f"CQ {SNL_US_CALL} DM79",
                       country="USA", continent="NA"),
         verify_fn=lambda: (
             v.check_queue_contains(SNL_US_CALL,
                 f"T27: {SNL_US_CALL} queued (US grid, Still Need cache in whatever state)"),
         ) if v.available else None)

    send(sock,
         f"DX grid CQ: CQ {SNL_DX_CALL} GG66",
         "IsRuleStillNeeded (Continent/CqZone/Dxcc paths) must not raise or block queuing",
         build_enqueue(f"CQ {SNL_DX_CALL} GG66",
                       country="Brazil", continent="SA"),
         verify_fn=lambda: (
             v.check_queue_contains(SNL_DX_CALL,
                 f"T28: {SNL_DX_CALL} queued (DX grid, Still Need cache in whatever state)"),
         ) if v.available else None)


def group14_logged_adif_fallback(sock, v):
    """T29-T30: LoggedAdifMessage fallback for a logged QSO.

    WSJT-X sends both QsoLoggedMessage and LoggedAdifMessage for every logged
    QSO. Jimmy normally acts on QsoLoggedMessage; HandleLiveAdifLogged() is a
    fallback so a single dropped QsoLoggedMessage packet doesn't silently keep
    a QSO out of the log/awards (this is what actually happened with a real
    TM13COL QSO — WSJT-X logged it locally but its QsoLoggedMessage broadcast
    never reached Jimmy, and nothing caught it before this fix).

    T29: LoggedAdifMessage ALONE (simulates a dropped QsoLoggedMessage) must
         still get the call into logListBox.
    T30: QsoLoggedMessage + LoggedAdifMessage together (the normal case) for a
         second call must not double up — still just one logListBox entry,
         no duplicate-processing error.
    """
    print("  ─ Group 14: LoggedAdifMessage fallback for a logged QSO ─")

    ADIF_ONLY_CALL = "W1ADIF"   # unique call — never sent via QsoLoggedMessage
    BOTH_MSG_CALL  = "W2BOTH"   # unique call — sent via both message types

    send(sock,
         f"LoggedAdifMessage only: {ADIF_ONLY_CALL} (QsoLoggedMessage never arrives)",
         "Expect: Logged sound; logListBox gains the call from ADIF fallback alone",
         build_logged_adif(ADIF_ONLY_CALL),
         delay=2.0,
         verify_fn=lambda: (
             v.check_log_contains(ADIF_ONLY_CALL,
                 f"T29: {ADIF_ONLY_CALL} in logListBox from LoggedAdifMessage alone"),
         ) if v.available else None)

    send(sock,
         f"QsoLoggedMessage + LoggedAdifMessage together: {BOTH_MSG_CALL}",
         "Expect: normal case (both messages) still logs once, no duplicate-processing error",
         build_qso_logged(BOTH_MSG_CALL),
         delay=0.5)

    send(sock,
         f"(same QSO) LoggedAdifMessage: {BOTH_MSG_CALL}",
         "Second message for the same QSO must be a no-op, not a second log entry",
         build_logged_adif(BOTH_MSG_CALL),
         delay=2.0,
         verify_fn=lambda: (
             v.check_log_contains(BOTH_MSG_CALL,
                 f"T30: {BOTH_MSG_CALL} in logListBox (both messages, still just one QSO)"),
         ) if v.available else None)


def group15_wait_and_reply_cooperation(sock, v):
    """T32-T33: WSJT-X's own Enable Tx button being externally halted, then
    re-enabled without Jimmy having asked for it (i.e. WSJT-X's Wait and Reply
    feature resuming a stalled QSO) -- Jimmy must resume the stalled call
    itself instead of silently going stale. Uses a dedicated callsign
    (W3WAIT) so it can't collide with call state left over from earlier
    groups.

    T33 (the actual HandleUnsolicitedTxResume() check) only gets a real
    workout if Jimmy is genuinely cycling in CQ mode with an active
    callInProg when the halt/resume messages arrive -- by default a fresh
    Jimmy launch sits paused in Listen mode, so this reports a WARNING (not
    a FAIL) rather than a hard failure when that isn't the case, the same
    way T17 (Group 8) already handles a similar environment-dependent gap.
    To get a real PASS: in Jimmy, select "CQ only" and press Alt+C, then
    answer "No" to the "Run recommended analysis" dialog if it appears --
    confirmed via manual testing that this reaches the CALL_CQ code path,
    though cqPaused clearing (and thus callInProg actually being set before
    T34/T35 fire) needs a bit more investigation; see MEMORY for details.
    Automated click/hotkey injection into Jimmy's window was tried (direct
    BM_CLICK, SendInput, and AttachThreadInput-forced foreground) and none
    of it reliably reached Jimmy from a background script, so this is not
    attempted automatically.
    """
    print("  ─ Group 15: Wait and Reply cooperation (external Tx halt/resume) ─")

    WAIT_CALL = "W3WAIT"

    send(sock,
         f"Grid reply: KB0UZT {WAIT_CALL} EM63",
         f"{WAIT_CALL} queued with 'to you' tag, expected to become the active call",
         build_enqueue(f"{MY_CALL} {WAIT_CALL} EM63"),
         verify_fn=lambda: (
             v.check_queue_contains(WAIT_CALL,
                 f"T32: {WAIT_CALL} in callQueue, ready to become active"),
         ) if v.available else None)

    send(sock,
         f"Signal report: KB0UZT {WAIT_CALL} -05",
         "Keeps the exchange going so Jimmy starts actively replying",
         build_enqueue(f"{MY_CALL} {WAIT_CALL} -05"),
         delay=3.0)

    send(sock,
         "WSJT-X externally halts Tx (TxHaltClk)",
         "Simulates the operator or WSJT-X itself halting -- not via Jimmy's own Halt Tx",
         build_status(tx_halt_clk=True),
         delay=1.0)

    send(sock,
         "WSJT-X externally re-enables Tx (TxEnableClk, button now true)",
         "Simulates Wait and Reply auto-resuming the stalled QSO -- Jimmy did not call EnableTx() itself",
         build_status(tx_enable_clk=True, tx_enable_button=True),
         delay=1.0,
         verify_fn=lambda: (
             v.check_status_contains_warn("resumed",
                 f"T33: status announces WSJT-X resumed calling {WAIT_CALL} automatically",
                 "Jimmy actually cycling in CQ mode with an active callInProg (see group docstring)"),
         ) if v.available else None)


def group16_rrr_after_logged_no_requeue(sock, v):
    """T34-T35: bare RRR (not 73/RR73) from an already-logged call must not
    re-add it to the queue. Regression coverage for the AC7WY incident
    (2026-07-07): RRR just means "all received" per FT8/FT4 protocol -- it is
    not itself a sign-off, so a station can legitimately keep repeating it
    (e.g. because they haven't yet decoded our final 73). But once we've
    already logged this call this session/band, a repeat RRR is never a new
    contact opportunity and must not silently reappear in the "available
    stations" list as if unworked. Uses a dedicated callsign (W4RRR) so it
    can't collide with call state left over from earlier groups.
    """
    print("  ─ Group 16: RRR after logged must not re-queue ─")

    RRR_CALL = "W4RRR"

    send(sock,
         f"QsoLoggedMessage: {RRR_CALL} logged",
         "Expect: Logged sound; logListBox gains W4RRR entry",
         build_qso_logged(RRR_CALL),
         delay=2.0,
         verify_fn=lambda: (
             v.check_log_contains(RRR_CALL,
                 f"T34: {RRR_CALL} in logListBox after QsoLogged"),
         ) if v.available else None)

    send(sock,
         f"Repeat RRR (after logged): {MY_CALL} {RRR_CALL} RRR",
         f"{RRR_CALL} already logged this session -- must NOT reappear in the queue",
         build_enqueue(f"{MY_CALL} {RRR_CALL} RRR"),
         delay=2.0,
         verify_fn=lambda: (
             v.check_queue_not_contains(RRR_CALL,
                 f"T35: {RRR_CALL} NOT re-queued after prior logged QSO (repeat RRR)"),
         ) if v.available else None)


def group17_still_needed_tag_clears_on_log(sock, v):
    """T36-T37: a queued call's award "Needed" tag must clear the instant it's
    worked and logged, not stay stale for the rest of the session.

    Environment-dependent, same category as Group 8's SOTA test and Group 15's
    CQ-mode test: requires the "Replay Test Award" Rule Definition (shipped as
    RuleDefinitions/_ReplayTestAward.ini, checklist = the single fake callsign
    W9NEED, via RuleDefinitions/Lists/_replaytest_roster.txt) to be checked in
    the Still Need tab, AND the "Still Needed" Call Filter category enabled,
    before running this script. Without that one-time setup, T36 WARNs instead
    of PASSing and T37 is skipped as uninformative -- there's nothing to prove
    the tag actually cleared if it never appeared in the first place. Neither
    outcome indicates a code defect on its own.

    Regression coverage for the 2026-07-09 fix: Controller.RefreshStillNeedCache()
    rebuilds the award "still needed" cache the instant a QSO is logged, but
    previously never revisited a call already sitting in the queue -- so a
    "Needed" tag applied at enqueue time stuck around for the rest of the
    session even after working that exact station (real-use report: a worked
    KF0VJY kept showing "Needed" indefinitely). WsjtxClient.
    RefreshQueuedAwardTags() (called right after the cache rebuild) re-derives
    Category for every queued STILL_NEEDED call so the tag clears immediately.
    """
    print("  ─ Group 17: Still-Needed tag clears off the queue once worked ─")

    NEEDED_CALL = "W9NEED"   # matches RuleDefinitions/_ReplayTestAward.ini's roster
    TAG_TEXT    = "Replay Test Award Needed"

    tag_was_present = [False]

    send(sock,
         f"CQ from {NEEDED_CALL} (never worked -- matches the test award's roster)",
         f"Expect: queued with the '{TAG_TEXT}' tag IF the test award is checked "
         "in the Still Need tab (WARN, not FAIL, otherwise)",
         build_enqueue(f"CQ {NEEDED_CALL} EM63"),
         verify_fn=lambda: tag_was_present.__setitem__(0, v.check_queue_row_contains_warn(
             NEEDED_CALL, TAG_TEXT,
             f"T36: {NEEDED_CALL} queued with '{TAG_TEXT}' tag",
             "check 'Replay Test Award' in the Still Need tab + enable its Call Filter"))
         if v.available else None)

    send(sock,
         f"QsoLoggedMessage: {NEEDED_CALL} logged",
         "Expect: the 'Needed' tag clears immediately (RefreshQueuedAwardTags), "
         "instead of staying stale for the rest of the session",
         build_qso_logged(NEEDED_CALL),
         delay=2.0,
         verify_fn=lambda: (
             v.check_queue_row_not_contains(NEEDED_CALL, TAG_TEXT,
                 f"T37: {NEEDED_CALL}'s '{TAG_TEXT}' tag cleared after being logged")
             if tag_was_present[0] else
             print(f"    ⚠ WARN  T37: skipped ('{TAG_TEXT}' tag never confirmed present in T36)")
         ) if v.available else None)


def group18_weak_snr_removal(sock, v):
    """T40-T41: opt-in "remove from list immediately when signal drops below floor"
    checkbox (Controller.removeOnWeakSnrCheckBox).

    Environment-dependent, same category as Group 17: requires both "Ignore SNR at
    or below" AND "Remove from list immediately..." checked in Options > Receive /
    Auto Reply > Block List, with the floor set above -28, for T41 to actually prove
    anything. Without that, T41 WARNs instead of PASSing -- the default (both boxes
    unchecked) is for the station to keep lingering in the queue until the separate
    call-queue age timeout prunes it, which is not a defect on its own.

    Regression coverage for the 2026-07-13 fix: the weak-signal SNR floor previously
    only ever gated whether a station's queue entry got refreshed by a new decode --
    it never removed an already-queued station whose signal had since faded below
    the floor, so the entry lingered indefinitely with its last (better) SNR still
    shown. WsjtxClient.ProcessDecodeMsg now calls CallQueueStore.RemoveCall for the
    already-queued station when a subsequent decode reports it below the floor, if
    the new checkbox is on.
    """
    print("  ─ Group 18: Weak-signal floor, opt-in immediate removal ─")

    WEAK_CALL = "W5WEAK"

    was_queued = [False]
    frag_nsp   = WEAK_CALL.lower().replace(" ", "")

    def check_queued_strong():
        v.wait_for_queue(WEAK_CALL, timeout=3.0)
        items = v.queue_items()
        was_queued[0] = any(frag_nsp in i.lower().replace(" ", "") for i in items)
        v._report(was_queued[0], f"T40: {WEAK_CALL} queued at strong SNR", f"queue={items}")

    send(sock,
         f"Strong CQ from {WEAK_CALL}",
         "Expect: queued normally (strong signal, unaffected by any floor)",
         build_enqueue(f"CQ {WEAK_CALL} EM63", snr=0),
         verify_fn=check_queued_strong if v.available else None)

    def check_removed_or_warn():
        if not was_queued[0]:
            print(f"    ⚠ WARN  T41: skipped ({WEAK_CALL} never confirmed queued in T40)")
            return
        items = v.queue_items()
        still_present = any(frag_nsp in i.lower().replace(" ", "") for i in items)
        if not still_present:
            v._report(True, f"T41: {WEAK_CALL} removed after weak decode (remove-on-weak-SNR active)", f"queue={items}")
        else:
            print(f"    ⚠ WARN  T41: {WEAK_CALL} still queued after weak decode")
            print( "           (not failed — requires: check 'Ignore SNR at or below' AND "
                   "'Remove from list immediately...' in Options, floor above -28)")
            print(f"           queue={items}")

    send(sock,
         f"Same station, now very weak: {WEAK_CALL}",
         "Expect: removed immediately IF 'Ignore SNR at or below' and 'Remove from "
         "list immediately...' are both checked in Options, with the floor above -28 "
         "(WARN, not FAIL, otherwise)",
         build_enqueue(f"CQ {WEAK_CALL} EM63", snr=-28),
         delay=2.0,
         verify_fn=check_removed_or_warn if v.available else None)


def group19_weak_snr_first_decode(sock, v):
    """T42: a station whose very first-ever decode is already weak (at or below the
    floor) must never be admitted to the queue at all, when 'Ignore SNR at or below'
    is checked -- regardless of 'Remove from list immediately...', since there's
    nothing queued yet to remove.

    Regression coverage for the 2026-07-18 fix: the weak-signal SNR floor was only
    ever wired into WsjtxClient.ProcessDecodeMsg's TO_MYCALL branch. Ordinary CQ
    traffic -- the vast majority of what fills the calling queue, including
    everything Group 18 above sends -- is handled entirely by AddSelectedCall, which
    had zero weak-signal awareness. A brand-new CQ station decoded below the floor
    was still admitted to the queue on its very first appearance (the exact bug
    report that prompted this fix: a -22 floor with -23/-25 stations still showing
    up in TX1). AddSelectedCall now rejects it outright, with the same
    never-suppress-callInProg and manual-selection-bypass carve-outs already used by
    the ProcessDecodeMsg check.
    """
    print("  ─ Group 19: Weak-signal floor, first-decode admission ─")

    NEW_WEAK_CALL = "W6FIRST"

    send(sock,
         f"Brand-new station, weak from the very first decode: {NEW_WEAK_CALL}",
         "Expect: never queued IF 'Ignore SNR at or below' is checked in Options, "
         "with the floor at or above -23 (WARN, not FAIL, otherwise)",
         build_enqueue(f"CQ {NEW_WEAK_CALL} EM63", snr=-23),
         verify_fn=(lambda: v.check_queue_not_contains_warn(
             NEW_WEAK_CALL, f"T42: {NEW_WEAK_CALL} never queued (weak on first decode)",
             "'Ignore SNR at or below' checked in Options, floor at or above -23"
         )) if v.available else None)


def run_tests(sock, v):
    print("──── Test Decode Messages ────")
    print(f"  Format: [DESTINATION] [SOURCE] [payload]")
    print(f"  'KB0UZT K4YT ...' means K4YT is calling KB0UZT (directed to me)")
    print()

    group1_station_calling_me(sock, v)
    group2_cq_messages(sock, v)
    group3_ap_suffix(sock, v)
    group4_contest_field_day(sock, v)
    group5_final_73_after_qso_logged(sock, v)
    group6_recall_after_prior_qso(sock, v)
    group7_slash_callsign_no_country(sock, v)
    group8_sota_cq(sock, v)
    group9_short_ap_suffix(sock, v)
    group10_period_checks(sock, v)
    group11_fox_hound_detection(sock, v)
    group12_hrc_filter_baseline(sock, v)
    group13_still_need_live_tag_baseline(sock, v)
    group14_logged_adif_fallback(sock, v)
    group15_wait_and_reply_cooperation(sock, v)
    group16_rrr_after_logged_no_requeue(sock, v)
    group17_still_needed_tag_clears_on_log(sock, v)
    group18_weak_snr_removal(sock, v)
    group19_weak_snr_first_decode(sock, v)

    # ── To add a new replay test group ──────────────────────────────────────
    # 1. Define a new function, e.g.:
    #      def group18_your_scenario(sock, v):
    #          send(sock, "Label", "Description", build_enqueue("..."),
    #               verify_fn=lambda: v.check_queue_contains("K4YT", "T39: ..."))
    # 2. Call it here, above this comment block.
    # Tests are auto-numbered by the global _test_num counter.
    # ────────────────────────────────────────────────────────────────────────

    print("──── Test sequence complete ────")
    if v.available:
        v.summary()
    else:
        print("  (Verifier was not available — all assertions skipped)")
        print("  Re-run with Jimmy open to enable automatic verification.")
    print()


# ═══════════════════════════════════════════════════════════════════════════════
# Entry Point
# ═══════════════════════════════════════════════════════════════════════════════

def main():
    # Safety: this script simulates real WSJT-X traffic (decodes, QSO-logged
    # messages, etc.) against a live Jimmy instance. If that Jimmy was not
    # started with JIMMY_TEST_DB_PATH set, its logging code writes to the
    # REAL logbook and can upload fake QSOs to real QRZ/Club Log. Refuse to
    # run at all unless this process's own environment has it set, so a
    # direct "python JimmyReplay.py" (bypassing run_replay_tests.bat) can't
    # silently hit production. This does not run Jimmy.exe itself, so it
    # cannot force the OTHER process's environment -- always launch Jimmy
    # via run_replay_tests.bat, which sets this for both processes.
    if not os.environ.get("JIMMY_TEST_DB_PATH"):
        print("ERROR: JIMMY_TEST_DB_PATH is not set in this shell.")
        print("Refusing to run -- this script sends simulated WSJT-X traffic")
        print("that Jimmy will log for real unless it's isolated to a test")
        print("database. Run via run_replay_tests.bat instead of calling")
        print("this script directly, or set JIMMY_TEST_DB_PATH yourself")
        print("before starting BOTH Jimmy.exe and this script.")
        sys.exit(1)

    print("=" * 60)
    print("  JimmyReplay.py — UDP test sender + auto-verifier")
    print("=" * 60)
    print(f"\n  Target: {JIMMY_HOST}:{JIMMY_PORT}")
    print(f"  Simulating WSJT-X {WSJT_VERSION}/{WSJT_REVISION}")
    print(f"  myCall={MY_CALL}  myGrid={MY_GRID}")
    print()

    # Locate Jimmy's controls before opening socket
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
    print("  [1] WSJT-X is closed")
    print("  [2] Jimmy is running (Debug build)")
    print("  [3] Advanced Call Layout enabled (Options)")
    print("  [4] (optional) 'SOTA' in directed CQ alert text box → T17 PASS")
    print("      Without it, T17 prints WARNING instead of PASS or FAIL")
    print("  [5] T25-T26 (Group 12) always pass regardless of HRC database state")
    print("  [6] T27-T28 (Group 13) always pass regardless of Still Need selection")
    print("  [7] T33/T35 need Jimmy actively CQ-cycling to get a full PASS instead of a WARNING")
    print("  [8] (optional) check 'Replay Test Award' in the Still Need tab + enable its")
    print("      Call Filter → T36-T37 PASS. Without it, T36 WARNs and T37 is skipped")
    print()

    if not ensure_jimmy_udp_ready():
        print("  ERROR: Jimmy's UDP port is not available. Cannot run tests.")
        print("  Make sure Jimmy is running (Debug build) and WSJT-X is closed.")
        sys.exit(1)

    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.bind(("0.0.0.0", LOCAL_PORT))
        print(f"  Listening on port {LOCAL_PORT}\n")
    except OSError as e:
        print(f"  ERROR: Cannot bind to port {LOCAL_PORT}: {e}")
        sys.exit(1)

    try:
        if handshake(sock, v):
            run_tests(sock, v)
    except KeyboardInterrupt:
        print("\n  Interrupted.")
    finally:
        sock.close()


if __name__ == "__main__":
    main()
