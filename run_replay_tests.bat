@echo off
setlocal

rem -- Safety: never let replay testing touch the real logbook or real online
rem    services. This script is the ONLY supported way to run replay tests --
rem    it always forces an isolated test database and always checks for a
rem    real WSJT-X before doing anything else. Do not bypass it by starting
rem    Jimmy.exe manually and running JimmyReplay.py directly.

tasklist /FI "IMAGENAME eq wsjtx.exe" 2>NUL | find /I "wsjtx.exe" >NUL
if %ERRORLEVEL%==0 (
    echo ERROR: A real WSJT-X process is currently running.
    echo Replay testing must not run while real WSJT-X is up -- ask the user
    echo to close it first. This script will not close it for you.
    exit /b 1
)

set "JIMMY_TEST_DB_PATH=%TEMP%\JimmyReplayTest_logbook.db"
echo Test mode: JIMMY_TEST_DB_PATH=%JIMMY_TEST_DB_PATH%
echo   (real logbook untouched; all real QRZ/Club Log/LoTW/FCC ULS network calls blocked)

where python >nul 2>&1
if errorlevel 1 (
    echo ERROR: Python not found in PATH.
    echo Install Python 3.6 or later and ensure it is on your PATH.
    exit /b 1
)

set "JIMMY_EXE=%~dp0WSJTX_Controller\bin\Debug\net10.0-windows\Jimmy.exe"
set "STARTED_BY_SCRIPT="
set "JIMMY_PID="

tasklist /FI "IMAGENAME eq Jimmy.exe" 2>NUL | find /I "Jimmy.exe" >NUL
if not %ERRORLEVEL%==0 (
    if not exist "%JIMMY_EXE%" (
        echo ERROR: %JIMMY_EXE% not found. Build Jimmy first ^(build.bat^).
        exit /b 1
    )
    echo Starting Jimmy.exe in test mode...
    for /f "usebackq delims=" %%p in (`powershell -NoProfile -Command "(Start-Process -FilePath '%JIMMY_EXE%' -WorkingDirectory '%~dp0WSJTX_Controller\bin\Debug\net10.0-windows' -PassThru).Id"`) do set "JIMMY_PID=%%p"
    set "STARTED_BY_SCRIPT=1"
    rem Launching via PowerShell (needed to capture the PID for cleanup below) adds a
    rem little startup latency versus the old plain "start" -- give the window extra
    rem time to come up so JimmyVerifier's Win32 window search doesn't miss it.
    timeout /t 5 /nobreak >nul
) else (
    echo NOTE: Jimmy.exe is already running. This script cannot confirm that
    echo instance was started with JIMMY_TEST_DB_PATH set. If you started it
    echo manually, make sure you set JIMMY_TEST_DB_PATH first -- otherwise
    echo close it and re-run this script so it can launch a safe instance.
)

python "%~dp0JimmyReplay.py"

rem -- Cleanup: only close the test-mode Jimmy instance THIS run launched
rem    (tracked by PID above) -- never touches an instance that was already
rem    running before this script started, real or otherwise.
if defined STARTED_BY_SCRIPT if defined JIMMY_PID (
    echo Closing test-mode Jimmy.exe ^(PID %JIMMY_PID%^)...
    taskkill /PID %JIMMY_PID% /F >nul 2>&1
)
