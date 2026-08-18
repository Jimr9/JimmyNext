JIMMY TESTING GUIDE
===================
Plain text. Screen-reader compatible.
Last updated: 2026-08-18


CONTENTS
--------
1.  Overview
2.  Prerequisites
3.  How to test safely (no transmissions, no real QSOs affected)
4.  Suite 1: Parser Unit Tests
5.  Suite 2: Replay Integration Tests
6.  What PASS and FAIL mean
7.  How to add a new parser unit test
8.  How to add a new replay test
9.  Best practices for regression testing


1. OVERVIEW
-----------
Jimmy has two test suites:

Suite 1: Parser Unit Tests (JimmyTests)
  Fast, offline, no process needed.
  Tests the message classification logic that Jimmy uses to decide
  what to do with every decode received from WSJT-X.

Suite 2: Replay Integration Tests (JimmyDirectReplay)
  Requires Jimmy to be running (WSJT-X is irrelevant -- Jimmy talks
  to a fake engine host, not to WSJT-X, in either mode).
  Simulates jimmy-engine-host.exe over Jimmy's own Direct control
  port and verifies Jimmy's queue and status text respond correctly
  to known decode/status sequences, exactly the way production
  Jimmy talks to the real native engine.
  No radio. No transmissions. No real QSOs are affected.
  (Renamed 2026-08-18: JimmyReplay.py simulated a standard WSJT-X
  UDP peer, the classic protocol Jimmy's own production code no
  longer uses at all. JimmyDirectReplay.py replaces it, speaking
  the same Direct control-port protocol production Jimmy speaks to
  the real engine host, so replay tests exercise the real transport
  instead of a retired one.)


2. PREREQUISITES
----------------
Both suites:
  - Visual Studio Community (any recent edition) or Build Tools for
    Visual Studio, with MSBuild and the C# compiler installed.
  - Python 3.6 or later (for Suite 2 only).

Suite 2 only:
  - Jimmy must be running (Debug build).
  - WSJT-X must be closed.
  - In Jimmy: set mode to CQ.
  - In Jimmy: enable Advanced Call Layout (Options dialog).
    This bypasses T/R period gating so the test messages arrive
    in both even and odd periods without being filtered out.


3. HOW TO TEST SAFELY
---------------------
Suite 1 (parser tests) is entirely offline. It links against the
Jimmy assembly and calls static classifier methods directly.
No network traffic. No radio. Nothing external is involved.

Suite 2 (replay tests) runs a fake control-port server on localhost
(127.0.0.1 port 58239, the same port the real jimmy-engine-host.exe
would listen on) that Jimmy connects to instead of a real engine.
Jimmy never spawns the real jimmy-engine-host.exe in test mode
(TestModeGuard.IsTestMode), so no real audio device, COM port, or
radio is ever touched. No CAT commands are sent. No PTT is keyed.
No frequencies change. The test traffic is synthetic and
self-contained on your computer.

To be completely safe:
  - Close WSJT-X before running Suite 2.
  - Do not connect a radio while running Suite 2.
  - Run the Debug build of Jimmy, not the installed release.


4. SUITE 1: PARSER UNIT TESTS
------------------------------
File: JimmyTests\JimmyTests.cs
Runner script: run_parser_tests.bat

What it tests:
  - AP suffix stripping (WSJT-X 3.0 "a35" and old-format "?").
  - WsjtxMessage static classifiers: IsReport, IsRogerReport,
    Is73, IsRR73, Is73orRR73, IsRogers, IsCQ, IsPota, IsSota,
    DirectedTo, IsContest, IsReply, IsShortReply, IsInvalidType.
  - ToCall and DeCall extraction.
  - End-to-end: AP-suffixed messages are stripped then classified
    correctly.
  - Contest messages route via the contest branch (IsInvalidType
    is intentionally true for FD exchanges in the normal path).

How to run:
  Double-click run_parser_tests.bat
  or type at a command prompt:
    run_parser_tests

The script builds Jimmy, builds JimmyTests, then runs the tests.
All output appears in the console window.


5. SUITE 2: REPLAY INTEGRATION TESTS
--------------------------------------
File: JimmyDirectReplay.py
Runner script: run_replay_tests.bat

What it tests (current groups -- see JimmyDirectReplay.py itself for
the full, authoritative list; each group function has its own
docstring with the exact regression it guards):

  Group 1: Full QSO exchange directed at me.
    K4YT sends grid, report, roger-report, RRR, RR73.
    Verifies K4YT is queued at each step.

  Group 2: CQ messages (plain and POTA-directed).

  Group 3: WSJT-X 3.0 AP suffix stripping (long and short forms).

  Group 4: Contest and Field Day exchanges (directed vs. between
    other stations).

  Group 5-6: A REAL logged QSO, driven end to end -- the replay
    script double-clicks a queued call's row (posted Win32 mouse
    messages, no real mouse/keyboard input) to trigger Jimmy's own
    real double-click-to-reply path, then feeds the fake engine's
    own simulated report/RR73 exchange back through Jimmy's real
    SNAPSHOT poll loop, exercising the actual production logging
    code path (not a shortcut). Verifies "final 73" status wording
    and that a station re-calling after being logged is re-queued.

  Group 7-13: /H (Fox/Hound-suffix) handling, SOTA-directed CQ
    admission, short AP suffixes, T/R period gating under Advanced
    Call Layout, Fox/Hound detection, and HRC/Still-Need filter
    plumbing baselines.

  Group 15-19: Grid-reply queuing, RRR-after-logged non-requeue
    (using the same real double-click-driven logging as Group 5-6),
    Still-Needed award tag clearing on a real logged QSO, and the
    weak-signal-floor admission/removal checks.

  (Group 14, the old UDP-only LoggedAdifMessage-fallback test, was
  retired 2026-08-18 -- Direct mode has no ADIF-broadcast wire
  message and no lossy-UDP-packet failure mode to guard against, so
  its premise does not translate; see JimmyDirectReplay.py's own
  group14_retired_note() for the full reasoning.)

How to run:
  1. Start Jimmy (Debug build). (WSJT-X does not need to be closed
     for Direct mode itself, but keep it closed anyway -- habit
     worth keeping, and some setups still check for it.)
  2. Set Jimmy mode to CQ.
  3. Enable Advanced Call Layout in Options.
  4. Double-click run_replay_tests.bat
     or type: run_replay_tests

The replay script starts a fake control-port server BEFORE checking
for Jimmy, so Jimmy's very first poll (about 1 second after Direct
mode connects) typically succeeds immediately.

If Jimmy is not running, the script still executes but skips all
UI assertions and prints "(Verifier was not available)".


6. WHAT PASS AND FAIL MEAN
---------------------------
Parser tests (Suite 1):
  PASS  - The classifier returned the expected value.
  FAIL  - The classifier returned the wrong value. The label and
          both expected and actual values are printed.
  Final line shows "ALL TESTS PASSED" or "SOME TESTS FAILED".
  A failing parser test means a regression in message classification.

Replay tests (Suite 2):
  checkmark PASS - Jimmy's UI showed the expected state within the
                   timeout window (usually 3-4 seconds).
  cross FAIL     - Jimmy's UI did not reach the expected state.
                   The actual queue contents or status text are
                   printed so you can diagnose the difference.
  Final summary: "N/M assertions passed".
  A failing replay test means a regression in Jimmy's behavior.


7. HOW TO ADD A NEW PARSER UNIT TEST
--------------------------------------
Parser tests live in JimmyTests\JimmyTests.cs.

Step 1: Find or create a test group function.
  Each group is a static void method, for example ApStripTests(),
  ReportTests(), FinalAckTests(). If your test fits an existing
  group, add it there. If it covers a new category, add a new
  method at the bottom of the class.

Step 2: Write the test using Check() or CheckStr().
  Check(label, actual_bool, expected_bool)
  CheckStr(label, actual_string, expected_string)

  Example:
    Check("IsReport: zero dB",
          WsjtxMessage.IsReport($"{MY_CALL} {THEIR_CALL} +00"),
          true);

Step 3: If you created a new group method, call it from Main().
  Add the call after the last existing group call:
    YourNewGroupTests();

Step 4: Run run_parser_tests.bat to confirm PASS.

Best practice: Every new classifier method in WsjtxMessage.cs
should have at least one true case and one false case in the
parser tests.


8. HOW TO ADD A NEW REPLAY TEST
---------------------------------
Replay tests live in JimmyDirectReplay.py.
Tests are organized as group functions (group1, group2, ...).
The test number tag (D01, D02, ...) is assigned automatically
by a global counter, so tests always number sequentially.

Step 1: Define a new group function at the bottom of the group
  functions section (just before run_tests()).

  Example:
    def group20_my_new_scenario(engine, v):
        print("  - Group 20: My new scenario -")

        step("Label shown in output",
             "What this message tests",
             lambda: engine.send_decode(f"{MY_CALL} {THEIR_CALL} -05",
                                         from_call=THEIR_CALL),
             verify_fn=lambda: v.check_queue_contains(THEIR_CALL,
                 f"D: {THEIR_CALL} in queue after new message"))

Step 2: Call your group from run_tests(), just before the closing
  print() calls:
    group20_my_new_scenario(engine, v)

Step 3: Run run_replay_tests.bat with Jimmy running to confirm PASS.

Available step() parameters:
  label       - short label printed in the test header line
  description - longer description printed below the label
  action      - a zero-argument callable that drives the fake engine
                (engine.send_decode, engine.set_transmitting, etc.)
  verify_fn   - optional lambda that calls v.check_* methods
  settle      - seconds to wait after action() before verifying
                (default 1.2)

FakeEngine methods (drive the simulated engine host):
  engine.send_decode(message, from_call=..., snr=..., dt_sec=..., freq_hz=...)
    Injects one decode into the next SNAPSHOT response and advances
    the simulated slot counter (matches AppSnapshot.recentDecodes'
    real "replaced each slot" semantics).
  engine.send_decodes(list_of_row_dicts)
    Same, for more than one decode in the same slot.
  engine.set_transmitting(bool) / engine.set_qso_txnow(text)
    Directly control the next SNAPSHOT's radio.transmitting /
    qso.txNow fields.
  engine.complete_qso_now(dxcall, final="RR73")
    Simulates the engine reporting a completed exchange with dxcall
    in one step.
  engine.wait_for_reply(timeout=5.0)
    Blocks until Jimmy sends a real REPLY command (e.g. from a
    double-click), returning the parsed {dxcall, dxgrid, replyMsg,
    replySnr, dxFreqHz} dict, or None on timeout.

Available verify assertions (same names/shapes as the old harness):
  v.check_queue_contains(fragment, label)
  v.check_queue_not_contains(fragment, label)
  v.check_status_contains(fragment, label)
  v.check_log_contains(fragment, label)
  v.check_active(label)
  v.check_queue_contains_warn / check_queue_not_contains_warn /
  v.check_queue_row_contains_warn / check_status_contains_warn
    (soft checks: WARN not FAIL when a config-dependent precondition
    isn't met -- see any existing group using them for the pattern)

If your new test needs a REAL logged QSO (not just a queued call),
use the shared _real_logged_qso(engine, v, call, tag_num) helper --
it drives a real double-click-to-reply, then a real report/RR73
exchange through the fake engine, exercising Jimmy's actual
DirectApplyStatus/LogQso code path end to end. It returns True/False
once a real REPLY arrived (a hard assertion), or None if the
double-click automation itself could not be reliably targeted this
run (an environment/timing gap -- report as WARN via the
_report_real_logged_qso helper, not a hard FAIL).

Note on group dependencies:
  Groups 5 and 6 are intentionally sequential (Group 5 logs a call;
  Group 6 tests what happens when that call calls again). New groups
  should be independent when possible and use their own unique test
  callsigns (never reuse THEIR_CALL/K4YT or another group's call) so
  a group can be re-run or reordered without picking up stale state
  from an earlier group. Document any real dependency in the group
  function's docstring.


9. BEST PRACTICES FOR REGRESSION TESTING
------------------------------------------
Every bug fix should get a test before the fix and a passing test
after.

For parser bugs (wrong classification of a message):
  1. Reproduce the bad input as a string in JimmyTests.cs.
  2. Add a Check() call that fails with the old code.
  3. Fix WsjtxMessage.cs.
  4. Confirm the test now passes.
  5. Leave the test in place permanently.

For behavior bugs (Jimmy queues/drops/sounds wrong):
  1. Identify the decode/status sequence that triggered the bug.
  2. Add a new replay group in JimmyDirectReplay.py that drives the
     fake engine through that sequence and asserts the correct
     outcome.
  3. Fix Jimmy.
  4. Run the replay test with Jimmy running and confirm PASS.
  5. Leave the group in place as a regression guard.

Keep tests small and focused:
  One assertion per observable behavior.
  If a test needs setup state (e.g., a prior QSO logged), document
  the dependency in the group function's docstring.

Naming conventions:
  Parser groups:  FooBarTests() in JimmyTests.cs
  Replay groups:  group20_short_description(engine, v) in
                   JimmyDirectReplay.py
  Test labels:    Start with the tag "D: " (or a fixed "D##:" once
                   you know the assigned number) for traceability.


END OF TESTING GUIDE
