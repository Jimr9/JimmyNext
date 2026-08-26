@echo off
setlocal

rem -- Safety: never let replay testing touch the real logbook or real online
rem    services. This script is the ONLY supported way to run replay tests --
rem    it always forces an isolated test database and always checks for a
rem    real WSJT-X before doing anything else. Do not bypass it by starting
rem    Jimmy Next.exe manually and running JimmyDirectReplay.py directly.
rem
rem    Launches "Jimmy Next.exe", not "Jimmy.exe" -- 2026-08-10: JIMMY_TEST_DB_PATH below
rem    already isolated the logbook/diag-log/radio control, but the .ini file itself was
rem    NEVER covered by that (Controller.cs's pgmName-derived path has no test-mode branch
rem    at all) -- a plain "Jimmy.exe" build reads/writes the REAL production Jimmy.ini
rem    regardless of this env var. build.bat now compiles with -p:AssemblyName="Jimmy Next"
rem    for exactly this reason (mirrors Setup_WiX\JimmyNext.wxs's own publish-time override)
rem    -- everything below must reference that same renamed exe.
rem
rem    UDP-to-Direct test-harness migration, 2026-08-18: this script used to run
rem    JimmyReplay.py, which sent real WSJT-X-protocol UDP packets to Jimmy's own UDP
rem    listener (only ever opened under TestModeGuard.IsTestMode). Test mode now uses the
rem    same Direct control-port transport production does (Controller.ApplyEngineMode()),
rem    so JimmyReplay.py and its UDP wire-format sender were retired; JimmyDirectReplay.py
rem    replaces it -- a fake control-port TCP server (127.0.0.1:58239) standing in for
rem    jimmy-engine-host.exe, verifying Jimmy's real UI the same way JimmyReplay.py always
rem    did (JimmyVerifier, carried over unchanged). No change to this script's own safety
rem    checks below (real-WSJT-X guard, isolated JIMMY_TEST_DB_PATH, graceful cleanup) --
rem    the transport swap is entirely inside the Python script.

tasklist /FI "IMAGENAME eq wsjtx.exe" 2>NUL | find /I "wsjtx.exe" >NUL
if %ERRORLEVEL%==0 (
    echo ERROR: A real WSJT-X process is currently running.
    echo Replay testing must not run while real WSJT-X is up -- ask the user
    echo to close it first. This script will not close it for you.
    exit /b 1
)

set "JIMMY_TEST_DB_PATH=%TEMP%\JimmyReplayTest_logbook.db"
rem Found live, 2026-08-10: this path was set fresh every run, but the FILE itself was
rem never deleted first -- so "isolated test database" was never actually true. The same
rem file had been silently accumulating real log entries (K4YT logged dozens of times)
rem across ten straight days of replay runs (Aug 1 through today), eventually drifting
rem "already worked" state enough to make a real assertion (T08, POTA CQ admission) fail
rem for reasons that had nothing to do with the code under test. `if exist` guards the
rem delete so a genuinely first-ever run (no file yet) is a silent no-op, not an error.
if exist "%JIMMY_TEST_DB_PATH%" del "%JIMMY_TEST_DB_PATH%"
rem Replay test isolation fix, 2026-08-23 (independent audit finding): the SAME class of bug
rem the logbook-db delete just above was already fixed for, found in the settings-ini/Data/
rem diag-log isolation root instead (Controller.cs/WsjtxClient.cs/LookupManager.cs/RuleLoader.cs/
rem ClassificationParityLogger.cs -- all five key off this one shared "%TEMP%\JimmyReplayTest_
rem AppData" folder under TestModeGuard.IsTestMode). That folder's own .ini is seeded ONCE, the
rem first time a session finds none there yet, then never re-seeded -- so a run after the very
rem first one always started from whatever state the PRIOR replay run's own writes left behind,
rem not a fresh copy of the real operator .ini, and any cached Data/ lookups or log_*.txt entries
rem from prior runs carried forward too. Deleting the whole folder here, before each run, makes
rem the very next launch's own "seed once" logic see a genuinely empty root and re-copy the real
rem .ini fresh -- matching the logbook db's own already-proven-necessary treatment above. `if
rem exist` guards it the same way, so a genuinely first-ever run is a silent no-op. This does NOT
rem make genuinely PARALLEL replay runs safe (a second run's delete could still race a first
rem run's writes) -- this script's own single-instance design (the tasklist check below reuses
rem an already-running Jimmy Next.exe rather than racing a second one) was never built to support
rem that; only the FRESH/PREVIOUS-RUN-CONTAMINATION half of the finding applies here.
if exist "%TEMP%\JimmyReplayTest_AppData" rd /s /q "%TEMP%\JimmyReplayTest_AppData"
echo Test mode: JIMMY_TEST_DB_PATH=%JIMMY_TEST_DB_PATH% (freshly reset)
echo   Settings/.ini/Data/diag-log isolation root also freshly reset (JimmyReplayTest_AppData)
echo   (real logbook untouched; all real QRZ/Club Log/LoTW/FCC ULS network calls blocked)
echo   (JimmyNative also force-disabled for this session -- see Controller.cs's own
echo    TestModeGuard.IsTestMode check -- so a test run can never spawn the real engine
echo    host or touch the real radio, even if decodeEngineMode=JimmyNative in the real
echo    .ini; nothing here needs manual editing/restoring of the .ini anymore)

where python >nul 2>&1
if errorlevel 1 (
    echo ERROR: Python not found in PATH.
    echo Install Python 3.6 or later and ensure it is on your PATH.
    exit /b 1
)

set "JIMMY_EXE=%~dp0WSJTX_Controller\bin\Debug\net10.0-windows\Jimmy Next.exe"
set "STARTED_BY_SCRIPT="
set "JIMMY_PID="

tasklist /FI "IMAGENAME eq Jimmy Next.exe" 2>NUL | find /I "Jimmy Next.exe" >NUL
if not %ERRORLEVEL%==0 (
    if not exist "%JIMMY_EXE%" (
        echo ERROR: %JIMMY_EXE% not found. Build Jimmy first ^(build.bat^).
        exit /b 1
    )
    echo Starting Jimmy Next.exe in test mode...
    rem PID via a temp file, not `for /f` + backtick command substitution -- 2026-08-10:
    rem that pattern started failing once JIMMY_EXE's path gained a space ("Jimmy Next.exe"),
    rem with PowerShell exiting immediately ("Input redirection is not supported") under the
    rem nested cmd-backtick-powershell invocation, despite the identical Start-Process call
    rem working fine standalone. Writing the PID to a file and reading it back with `set /p`
    rem sidesteps that whole fragile quoting/piping chain instead of chasing it further.
    set "JIMMY_PID_FILE=%TEMP%\jimmy_replay_pid_%RANDOM%.txt"
    powershell -NoProfile -Command "(Start-Process -FilePath '%JIMMY_EXE%' -WorkingDirectory '%~dp0WSJTX_Controller\bin\Debug\net10.0-windows' -PassThru).Id | Out-File -Encoding ascii '%JIMMY_PID_FILE%'"
    if exist "%JIMMY_PID_FILE%" (
        set /p JIMMY_PID=<"%JIMMY_PID_FILE%"
        del "%JIMMY_PID_FILE%" >nul 2>&1
    )
    set "STARTED_BY_SCRIPT=1"
    rem Launching via PowerShell (needed to capture the PID for cleanup below) adds a
    rem little startup latency versus the old plain "start" -- give the window extra
    rem time to come up so JimmyVerifier's Win32 window search doesn't miss it.
    timeout /t 5 /nobreak >nul
) else (
    echo NOTE: Jimmy Next.exe is already running. This script cannot confirm that
    echo instance was started with JIMMY_TEST_DB_PATH set. If you started it
    echo manually, make sure you set JIMMY_TEST_DB_PATH first -- otherwise
    echo close it and re-run this script so it can launch a safe instance.
)

python "%~dp0JimmyDirectReplay.py"

rem -- Cleanup: only close the test-mode Jimmy instance THIS run launched
rem    (tracked by PID above) -- never touches an instance that was already
rem    running before this script started, real or otherwise.
if defined STARTED_BY_SCRIPT if defined JIMMY_PID (
    echo Closing test-mode Jimmy Next.exe ^(PID %JIMMY_PID%^)...
    taskkill /PID %JIMMY_PID% /F >nul 2>&1
)
