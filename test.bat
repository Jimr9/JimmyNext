@echo off
setlocal

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: dotnet SDK not found in PATH.
    echo Install the .NET 10 SDK and re-run.
    exit /b 1
)

echo Building Jimmy...
call "%~dp0build.bat"
if errorlevel 1 (
    echo Jimmy build FAILED. Check build_out.txt.
    exit /b 1
)

echo.
echo Building JimmyTests...
dotnet build "%~dp0JimmyTests\JimmyTests.csproj" -c Debug -v:minimal
if errorlevel 1 (
    echo JimmyTests build FAILED.
    exit /b 1
)

echo.
echo Running parser unit tests...
echo.
"%~dp0JimmyTests\bin\Debug\net10.0-windows\JimmyTests.exe"
