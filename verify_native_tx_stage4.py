"""Phase 4 TX Stage 4 bench test: confirms jimmy-engine-host.exe (run standalone, NOT through
Jimmy.exe -- matches main.rs's own documented bench-testing convention) actually keys PTT and
plays real synthesized FT8 audio when a QSO owes a transmission, releases PTT automatically once
that audio's known duration elapses, and cuts it short immediately on a hard (auto_only=False)
HaltTx. Uses Hamlib's dummy rig (model 1) for PTT -- no CAT connection to a real radio at all.

Audio device: "Microphone (USB Audio CODEC )" -- the operator confirmed the radio that interface
normally serves is powered off right now, so opening it for real playback carries no RF/VOX risk.
Deliberately NOT "Microphone (2- Realtek(R) Audio)": that's the screen reader's device, and must
not be touched by this or any other test.

This script talks to jimmy-engine-host.exe directly (not via Jimmy.exe/NativeTxPttListener --
that C#-side wiring was already bench-tested in verify_native_ptt_listener.py) by hand-building
the same WSJT-X Reply/HaltTx datagrams Jimmy would send, using the exact wire format JimmyReplay.py
already uses elsewhere in this project. A small local UDP listener stands in for Jimmy's own
NativeTxPttListener, forwarding PTT_ON/PTT_OFF to rigctld exactly as that C# class does (see its
own header comment) -- so this proves the ENGINE HOST's new Stage 4 logic against a real rigctld
daemon, without needing Jimmy.exe running at all.
"""
import os
import re
import socket
import struct
import subprocess
import sys
import threading
import time

ENGINE_HOST_EXE = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                "EngineHost", "target", "release", "jimmy-engine-host.exe")
RIGCTLD_EXE = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                            "WSJTX_Controller", "Resources", "hamlib", "rigctld.exe")
AUDIO_DEVICE = "Microphone (USB Audio CODEC )"

MAGIC = struct.pack('>I', 0xADBCCBDA)


def _u8(n):   return bytes([n & 0xFF])
def _u32(n):  return struct.pack('>I', n & 0xFFFFFFFF)
def _i32(n):  return struct.pack('>i', n)
def _f64(f):  return struct.pack('>d', f)
def _flag(b): return bytes([1 if b else 0])


def _qstr(s):
    b = s.encode('utf-8')
    return struct.pack('>I', len(b)) + b


def build_reply(dxcall_cq_text, delta_freq=1500, snr=-10):
    return (MAGIC + _u32(3) + _u32(4) + _qstr("JimmyTest") +
            _u32(0) + _i32(snr) + _f64(0.0) + _u32(delta_freq) +
            _qstr("FT8") + _qstr(dxcall_cq_text) + _flag(False) + _u8(0))


def build_halt_tx(auto_only=False):
    return MAGIC + _u32(3) + _u32(8) + _qstr("JimmyTest") + _flag(auto_only)


def query_ptt_via_rigctld(port):
    with socket.create_connection(("127.0.0.1", port), timeout=3) as s:
        s.sendall(b"t\n")
        return s.recv(64).decode().strip()


def set_ptt_via_rigctld(port, on):
    with socket.create_connection(("127.0.0.1", port), timeout=3) as s:
        s.sendall((b"T 1\n" if on else b"T 0\n"))
        return s.recv(64).decode().strip()


def fake_native_tx_ptt_listener(rigctld_port, stop_event):
    """Stands in for Jimmy's NativeTxPttListener.cs: receives PTT_ON/PTT_OFF and asserts/releases
    PTT on the real rigctld daemon exactly as that C# class does."""
    udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp.bind(("127.0.0.1", 0))
    udp.settimeout(0.5)
    port = udp.getsockname()[1]

    def loop():
        while not stop_event.is_set():
            try:
                data, _ = udp.recvfrom(64)
            except socket.timeout:
                continue
            msg = data.decode(errors="replace").strip()
            if msg == "PTT_ON":
                set_ptt_via_rigctld(rigctld_port, True)
            elif msg == "PTT_OFF":
                set_ptt_via_rigctld(rigctld_port, False)

    t = threading.Thread(target=loop, daemon=True)
    t.start()
    return port, udp


def main():
    if not os.path.exists(ENGINE_HOST_EXE):
        print(f"ERROR: {ENGINE_HOST_EXE} not found. Build EngineHost first (cargo build --release).")
        return 1
    if not os.path.exists(RIGCTLD_EXE):
        print(f"ERROR: {RIGCTLD_EXE} not found.")
        return 1

    passed = 0
    failed = 0
    rig_proc = None
    engine_proc = None
    stop_event = threading.Event()
    log_lines = []

    def check(label, cond):
        nonlocal passed, failed
        print(f"  [{'PASS' if cond else 'FAIL'}] {label}")
        if cond:
            passed += 1
        else:
            failed += 1

    try:
        print("Starting bundled rigctld (Hamlib dummy rig, model 1)...")
        rig_proc = subprocess.Popen(
            [RIGCTLD_EXE, "-m", "1", "-t", "4534"],
            cwd=os.path.dirname(RIGCTLD_EXE),
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        time.sleep(1.5)

        print("Starting fake NativeTxPttListener (real rigctld TCP client, PTT_ON/PTT_OFF UDP)...")
        ptt_port, ptt_sock = fake_native_tx_ptt_listener(4534, stop_event)
        print(f"  listening on 127.0.0.1:{ptt_port}")

        print(f"Starting jimmy-engine-host.exe standalone (device='{AUDIO_DEVICE}')...")
        engine_proc = subprocess.Popen(
            [ENGINE_HOST_EXE,
             "--mycall", "KB0UZT", "--mygrid", "FN42",
             "--device", AUDIO_DEVICE,
             "--jimmy-addr", "127.0.0.1:19999",  # nobody real listens here -- fine, sends are fire-and-forget
             "--jimmy-control-addr", f"127.0.0.1:{ptt_port}"],
            cwd=os.path.dirname(ENGINE_HOST_EXE),
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )

        control_addr_re = re.compile(r"listening on (127\.0\.0\.1:\d+)")

        def read_stdout():
            for line in engine_proc.stdout:
                log_lines.append(line.rstrip("\n"))

        reader = threading.Thread(target=read_stdout, daemon=True)
        reader.start()

        engine_control_addr = None
        deadline = time.time() + 10
        while time.time() < deadline and engine_control_addr is None:
            for line in log_lines:
                m = control_addr_re.search(line)
                if m:
                    engine_control_addr = m.group(1)
                    break
            time.sleep(0.2)

        check("engine host reported its own control address", engine_control_addr is not None)
        if engine_control_addr is None:
            print("--- engine host log so far ---")
            print("\n".join(log_lines))
            return 1
        print(f"  engine host control address: {engine_control_addr}")
        host, port_s = engine_control_addr.split(":")
        engine_port = int(port_s)

        reply_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

        # ---- Scenario 1: natural completion -- PTT released automatically after the real
        # transmission duration elapses, with no HaltTx sent at all. ----
        # tx_parity is set from a FALLBACK decoded_period (this bench test never fed the engine
        # a real decode, so there's no recent-decode history to match the Reply against) -- that
        # fallback is deliberately the CURRENT period, which makes tx_parity the OPPOSITE parity
        # of right now (see tx_schedule.rs's own parity convention), so the very first owed
        # period is the NEXT one, up to ~15s away. Poll for asserted-then-released across a
        # window generous enough to cover that wait plus the ~12.64s transmission itself.
        print("\n-- Scenario 1: Reply -> real PTT_ON + real audio play -> automatic PTT_OFF --")
        before = len(log_lines)
        reply_sock.sendto(build_reply("CQ W9XYZ EN37"), (host, engine_port))

        deadline = time.time() + 20
        asserted_at = None
        while time.time() < deadline:
            if query_ptt_via_rigctld(4534) == "1":
                asserted_at = time.time()
                break
            time.sleep(0.5)
        check(f"PTT asserted within ~20s of Reply (parity wait + queueing)", asserted_at is not None)

        released_naturally = False
        if asserted_at is not None:
            deadline = asserted_at + 16
            while time.time() < deadline:
                if query_ptt_via_rigctld(4534) == "0":
                    released_naturally = True
                    break
                time.sleep(0.5)
        check("PTT released automatically after the transmission (no HaltTx sent)", released_naturally)

        new_lines = log_lines[before:]
        check("engine logged a real [TX_SCHEDULE] transmit line",
              any("[TX_SCHEDULE]" in l and "transmitting" in l for l in new_lines))
        check("engine logged automatic PTT release",
              any("transmission complete -- PTT released" in l for l in new_lines))

        # ---- Scenario 2: hard HaltTx (auto_only=False) mid-transmission cuts PTT and flushes
        # queued audio immediately, well before the ~12.64s transmission would finish on its own.
        print("\n-- Scenario 2: Reply -> HaltTx(auto_only=False) mid-transmission -> immediate cutoff --")
        before = len(log_lines)
        reply_sock.sendto(build_reply("CQ W9XYZ EN37"), (host, engine_port))

        deadline = time.time() + 20
        asserted_at = None
        while time.time() < deadline:
            if query_ptt_via_rigctld(4534) == "1":
                asserted_at = time.time()
                break
            time.sleep(0.5)
        check("PTT asserted for scenario 2's transmission", asserted_at is not None)

        reply_sock.sendto(build_halt_tx(auto_only=False), (host, engine_port))
        time.sleep(1.0)
        cutoff_elapsed = time.time() - asserted_at if asserted_at else None
        state = query_ptt_via_rigctld(4534)
        check("PTT released immediately after hard HaltTx", state == "0")
        check(f"cutoff happened well before natural ~12.64s completion "
              f"(took {cutoff_elapsed:.1f}s since assertion)" if cutoff_elapsed is not None
              else "cutoff timing unavailable (PTT never asserted)",
              cutoff_elapsed is not None and cutoff_elapsed < 8.0)

        new_lines = log_lines[before:]
        check("engine logged an immediate HaltTx flush with queued samples discarded",
              any("HaltTx (immediate)" in l and "flushed" in l and "flushed 0" not in l
                  for l in new_lines))

        print(f"\nVerification summary: {passed}/{passed + failed} assertions passed")
        return 0 if failed == 0 else 1
    finally:
        stop_event.set()
        if engine_proc is not None:
            engine_proc.terminate()
            try:
                engine_proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                engine_proc.kill()
        if rig_proc is not None:
            rig_proc.terminate()
            try:
                rig_proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                rig_proc.kill()
        subprocess.run(["taskkill", "/IM", "jimmy-engine-host.exe", "/F"], capture_output=True)
        subprocess.run(["taskkill", "/IM", "rigctld.exe", "/F"], capture_output=True)
        print("\n--- full engine host log ---")
        print("\n".join(log_lines))


if __name__ == "__main__":
    sys.exit(main())
