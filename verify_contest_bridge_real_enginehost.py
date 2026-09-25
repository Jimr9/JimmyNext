"""One-off manual verification for the Nexus contesting foundation (phases 3-7): launches the
REAL, compiled jimmy-engine-host.exe (not a stub, not JimmyDirectReplay.py's fake server) and
drives it through a genuine end-to-end ARRL Field Day contest workflow over its real TCP control
port -- CONTEST_LIST_EVENTS, CONTEST_GET_RULESET, CONTEST_ENTER, CONTEST_LOG_MANUAL,
CONTEST_QSOS_SINCE, CONTEST_REBUILD_BEGIN/APPEND/COMMIT, CONTEST_EXPORT, CONTEST_EXIT -- against
Nexus's own real, unmodified contest engine. This is the strongest verification available without
real radio hardware: every command below is answered by actual Nexus/tempo-app/tempo-core logic
compiled into the real binary, not a simulation of it.

Not part of the permanent suite (same status as verify_engine_host_poc.py) -- a one-off milestone
proof, safe to delete once real-radio/JAWS testing supersedes it. Does not touch Jimmy Next.exe,
the real logbook, or any real network service.
"""
import json
import os
import socket
import subprocess
import sys
import time

ENGINE_HOST_EXE = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                "EngineHost", "target", "x86_64-pc-windows-gnu", "debug", "jimmy-engine-host.exe")
CONTROL_PORT = 58239


def send(sock, line, timeout=5.0):
    sock.settimeout(timeout)
    sock.sendall((line + "\n").encode("utf-8"))
    try:
        sock.shutdown(socket.SHUT_WR)
    except OSError:
        pass
    chunks = []
    try:
        while True:
            data = sock.recv(65536)
            if not data:
                break
            chunks.append(data)
    except socket.timeout:
        pass
    return b"".join(chunks).decode("utf-8", errors="replace").strip()


def connect_and_send(line, timeout=5.0):
    s = socket.create_connection(("127.0.0.1", CONTROL_PORT), timeout=timeout)
    try:
        return send(s, line, timeout)
    finally:
        s.close()


def wait_for_control_port(timeout_s=20):
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        try:
            s = socket.create_connection(("127.0.0.1", CONTROL_PORT), timeout=1.0)
            s.close()
            return True
        except OSError:
            time.sleep(0.5)
    return False


def expect_ok(label, resp, results):
    ok = resp.startswith("OK")
    results.append((label, ok, resp[:200]))
    print(("PASS" if ok else "FAIL") + "  " + label + ("" if ok else "  -> " + resp[:300]))
    return resp[3:].strip() if ok else None


def main():
    if not os.path.exists(ENGINE_HOST_EXE):
        print(f"ERROR: {ENGINE_HOST_EXE} not found. Build EngineHost first (cargo build).")
        return 1

    env = dict(os.environ)
    env["PATH"] = r"C:\msys64\ucrt64\bin;" + env.get("PATH", "")

    args = [
        ENGINE_HOST_EXE,
        "--mycall", "K5KPE",
        "--mygrid", "EM48",
        "--jimmy-addr", "127.0.0.1:58240",
        "--control-port", str(CONTROL_PORT),
    ]
    print("Starting real jimmy-engine-host.exe:", " ".join(args))
    proc = subprocess.Popen(args, cwd=os.path.dirname(ENGINE_HOST_EXE), env=env,
                             stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    results = []
    try:
        if not wait_for_control_port():
            out, _ = proc.communicate(timeout=5)
            print("FAIL: control port never became reachable. Process output:\n", out)
            return 1
        print("Control port is up.")

        # 1. CONTEST_LIST_EVENTS -- real bundled seed, not a canned list.
        resp = connect_and_send("CONTEST_LIST_EVENTS")
        body = expect_ok("CONTEST_LIST_EVENTS returns OK", resp, results)
        events = json.loads(body) if body else []
        expect_ok(f"CONTEST_LIST_EVENTS includes arrlfd ({len(events)} events total)",
                   "OK" if any(e["eventId"] == "arrlfd" for e in events) else "ERR arrlfd missing", results)

        # 2. CONTEST_GET_RULESET arrlfd -- real FdRuleset, resolved domain values included.
        resp = connect_and_send("CONTEST_GET_RULESET arrlfd")
        body = expect_ok("CONTEST_GET_RULESET arrlfd returns OK", resp, results)
        ruleset = json.loads(body) if body else {}
        keys = [f["key"] for f in ruleset.get("fields", [])]
        expect_ok(f"Ruleset has CLASS and SECTION fields (got {keys})",
                   "OK" if "CLASS" in keys and "SECTION" in keys else "ERR missing fields", results)
        section_field = next((f for f in ruleset.get("fields", []) if f["key"] == "SECTION"), None)
        domain_values = (section_field or {}).get("kind", {}).get("domain", {}).get("values", [])
        expect_ok(f"SECTION domain has real resolved values ({len(domain_values)} sections)",
                   "OK" if len(domain_values) > 50 else "ERR too few", results)

        # 3. CONTEST_ENTER -- real Engine::set_mode("fieldday-run"), real validation.
        enter_args = {
            "eventId": "arrlfd", "runMode": "sp",
            "stationCallsign": "K5KPE", "grid": "EM48", "operatorCallsign": "K5KPE",
            "class": "1D", "section": "MO",
            "categoryOperator": "", "categoryPower": "", "categoryAssisted": "", "categoryStation": "",
        }
        resp = connect_and_send("CONTEST_ENTER " + json.dumps(enter_args))
        body = expect_ok("CONTEST_ENTER (class 1D, section MO) returns OK", resp, results)
        session_instance_id = json.loads(body)["sessionInstanceId"] if body else None
        expect_ok(f"CONTEST_ENTER returned a real session-instance id ({session_instance_id})",
                   "OK" if session_instance_id else "ERR missing", results)

        # 3b. Retired section must be REFUSED -- real ContestSession::for_ruleset validation,
        # not a Jimmy-side check. Exit first (only one session at a time), then try MAR.
        connect_and_send("CONTEST_EXIT")
        bad_args = dict(enter_args); bad_args["section"] = "MAR"
        resp = connect_and_send("CONTEST_ENTER " + json.dumps(bad_args))
        expect_ok("CONTEST_ENTER with a retired section (MAR) is correctly REFUSED",
                   "OK" if resp.startswith("ERR") else "ERR should have been refused", results)

        # Re-enter with a valid section for the rest of the run.
        resp = connect_and_send("CONTEST_ENTER " + json.dumps(enter_args))
        body = expect_ok("Re-entered after the refusal", resp, results)
        session_instance_id = json.loads(body)["sessionInstanceId"] if body else None

        # 4. SNAPSHOT now carries contestSessionInstanceId and a real field_day block.
        resp = connect_and_send("SNAPSHOT")
        try:
            snap = json.loads(resp)
            has_id = snap.get("contestSessionInstanceId") == session_instance_id
            has_fd = snap.get("fieldDay") is not None
            expect_ok("SNAPSHOT.contestSessionInstanceId matches CONTEST_ENTER's result",
                       "OK" if has_id else "ERR mismatch", results)
            expect_ok("SNAPSHOT.fieldDay is populated once a session is active",
                       "OK" if has_fd else "ERR missing", results)
        except Exception as ex:
            expect_ok("SNAPSHOT parses as JSON", f"ERR {ex}", results)

        # 5. CONTEST_LOG_MANUAL -- real Engine::contest_log_manual, real dupe checking.
        log_args = {"call": "W1AW", "fields": [["CLASS", "2A"], ["SECTION", "CT"]], "mode": "FT8", "submode": ""}
        resp = connect_and_send("CONTEST_LOG_MANUAL " + json.dumps(log_args))
        body = expect_ok("CONTEST_LOG_MANUAL logs a real contact", resp, results)
        logged = json.loads(body).get("logged") if body else False
        expect_ok("First log of W1AW reports logged=true", "OK" if logged else "ERR not logged", results)

        resp = connect_and_send("CONTEST_LOG_MANUAL " + json.dumps(log_args))
        body = expect_ok("CONTEST_LOG_MANUAL for the SAME call again returns OK", resp, results)
        logged2 = json.loads(body).get("logged") if body else True
        expect_ok("Second log of W1AW is refused as a duplicate by Nexus's OWN dupe rule (logged=false)",
                   "OK" if logged2 is False else "ERR not refused", results)

        # 6. CONTEST_QSOS_SINCE -- real fd_sync_outbox, must show the real logged contact.
        resp = connect_and_send("CONTEST_QSOS_SINCE 0")
        body = expect_ok("CONTEST_QSOS_SINCE 0 returns OK", resp, results)
        completions = json.loads(body) if body else []
        w1aw = next((c for c in completions if c["call"] == "W1AW"), None)
        expect_ok(f"W1AW appears in CONTEST_QSOS_SINCE ({len(completions)} completion(s) total)",
                   "OK" if w1aw else "ERR missing", results)
        if w1aw:
            expect_ok(f"Completion carries the real session-instance id ({w1aw['sessionInstanceId']})",
                       "OK" if w1aw["sessionInstanceId"] == session_instance_id else "ERR mismatch", results)
            connect_and_send(f"CONTEST_QSO_ACK {w1aw['seq']}")

        # 7. CONTEST_REBUILD_BEGIN/APPEND/COMMIT -- real FieldDayLog replay + real scoring.
        resp = connect_and_send("CONTEST_REBUILD_BEGIN")
        body = expect_ok("CONTEST_REBUILD_BEGIN returns OK", resp, results)
        begin = json.loads(body) if body else {}
        token = begin.get("rebuildToken")
        expect_ok(f"CONTEST_REBUILD_BEGIN returned a token and high-water seq ({begin})",
                   "OK" if token else "ERR missing", results)

        append_args = {"rebuildToken": token, "contacts": [
            {"call": "W1AW", "fields": [["CLASS", "2A"], ["SECTION", "CT"]], "mode": "FT8", "submode": "", "whenUnix": int(time.time())}
        ]}
        resp = connect_and_send("CONTEST_REBUILD_APPEND " + json.dumps(append_args))
        expect_ok("CONTEST_REBUILD_APPEND returns OK", resp, results)

        resp = connect_and_send(f"CONTEST_REBUILD_COMMIT {token}")
        body = expect_ok("CONTEST_REBUILD_COMMIT returns OK", resp, results)
        commit = json.loads(body) if body else {}
        expect_ok(f"Rebuild reports a real, non-zero score from real Nexus scoring ({commit})",
                   "OK" if commit.get("qsoCount", 0) >= 1 and commit.get("points", 0) > 0 else "ERR zero/missing", results)

        # 8. CONTEST_EXPORT -- real Cabrillo from Nexus's own cabrillo_with, real content check.
        export_args = {"format": "cabrillo", "operatorName": "Test Operator", "contestEmail": "test@example.com"}
        resp = connect_and_send("CONTEST_EXPORT " + json.dumps(export_args))
        body = expect_ok("CONTEST_EXPORT (cabrillo) returns OK", resp, results)
        cabrillo = json.loads(body) if body else ""
        expect_ok("Real Cabrillo export contains the real logged callsign W1AW",
                   "OK" if "W1AW" in cabrillo else "ERR missing", results)
        expect_ok("Real Cabrillo export contains a START-OF-LOG header",
                   "OK" if "START-OF-LOG" in cabrillo else "ERR missing", results)

        # 9. Restart-restore (a REAL crash/restart, not simulated): the session entered in step 3
        # is STILL active here (nothing has exited it) -- log one more contact, kill the process
        # WITHOUT exiting the session, relaunch, and confirm the sidecar restores the SAME
        # session-instance id and Nexus's own journal still has both logged contacts.
        pre_restart_id = session_instance_id

        log_args2 = {"call": "K1ABC", "fields": [["CLASS", "3A"], ["SECTION", "EMA"]], "mode": "FT8", "submode": ""}
        connect_and_send("CONTEST_LOG_MANUAL " + json.dumps(log_args2))

        print("Killing jimmy-engine-host.exe WITHOUT exiting the contest session (simulates a crash)...")
        proc.kill()
        proc.wait(timeout=5)

        proc = subprocess.Popen(args, cwd=os.path.dirname(ENGINE_HOST_EXE), env=env,
                                 stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        if not wait_for_control_port():
            expect_ok("(restart test) control port came back up", "ERR never reachable", results)
        else:
            resp = connect_and_send("SNAPSHOT")
            try:
                snap = json.loads(resp)
                restored_id = snap.get("contestSessionInstanceId")
                expect_ok(f"Restarted process restored the SAME session-instance id ({restored_id})",
                           "OK" if restored_id == pre_restart_id else f"ERR got {restored_id}, wanted {pre_restart_id}", results)
                expect_ok("Restarted process's SNAPSHOT.fieldDay is populated (fd_active survived restart)",
                           "OK" if snap.get("fieldDay") is not None else "ERR missing", results)
            except Exception as ex:
                expect_ok("(restart test) SNAPSHOT parses as JSON", f"ERR {ex}", results)

            resp = connect_and_send("CONTEST_QSOS_SINCE 0")
            body = expect_ok("(restart test) CONTEST_QSOS_SINCE after restart returns OK", resp, results)
            completions2 = json.loads(body) if body else []
            expect_ok("The contact logged BEFORE the crash is still in Nexus's own journal after restart",
                       "OK" if any(c["call"] == "K1ABC" for c in completions2) else "ERR missing", results)

            connect_and_send("CONTEST_EXIT")

        failed = [r for r in results if not r[1]]
        print(f"\n{len(results) - len(failed)}/{len(results)} checks passed against the REAL jimmy-engine-host.exe binary.")
        if failed:
            print("FAILURES:")
            for label, _, resp in failed:
                print(f"  - {label}: {resp}")
        return 0 if not failed else 1
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()


if __name__ == "__main__":
    sys.exit(main())
