@echo off
setlocal

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: dotnet SDK not found in PATH.
    echo Install the .NET 10 SDK and re-run.
    exit /b 1
)

rem -p:AssemblyName="Jimmy Test" -- 2026-08-10: local Debug builds used to compile as plain
rem "Jimmy", identical to the real production install's own AssemblyName. Since Controller.cs's
rem ini path, LookupManager.DataRoot, and WsjtxClient's diag-log path are ALL derived from
rem Assembly.GetExecutingAssembly().GetName().Name (deliberately, so a differently-named build
rem gets its own isolated %LocalAppData% folder automatically -- see LookupManager.DataRoot's
rem own comment), a plain "Jimmy" Debug build shared the SAME folder as production, ini and all
rem -- confirmed live: a night of hotkey/dialog testing silently flipped settings (diagLog,
rem nativeEngineUseDirectEngine) on the real production Jimmy.ini. Mirrors EXACTLY how
rem Setup_WiX\JimmyTest.wxs already builds the separate JimmyTest.msi product (same
rem -p:AssemblyName override, just at publish time instead of build time) -- not a new
rem mechanism, just applying the existing one to local dev builds too.
dotnet build "%~dp0WSJTX_Controller\Jimmy.csproj" -c Debug -p:AssemblyName="Jimmy Test" -v:minimal > "%~dp0build_out.txt" 2>&1
echo Exit code: %ERRORLEVEL% >> "%~dp0build_out.txt"
