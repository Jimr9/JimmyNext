@echo off
setlocal

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: dotnet SDK not found in PATH.
    echo Install the .NET 10 SDK and re-run.
    exit /b 1
)

dotnet build "%~dp0WSJTX_Controller\Jimmy.csproj" -c Debug -v:minimal > "%~dp0build_out.txt" 2>&1
echo Exit code: %ERRORLEVEL% >> "%~dp0build_out.txt"
