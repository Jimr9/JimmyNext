<#
.SYNOPSIS
    Copies jimmy-engine-host.exe's required MSYS2 UCRT64 runtime DLLs next to the built
    binary, so it runs with no PATH setup at all -- not even for a normal, un-doctored launch
    of Jimmy.exe itself.

.DESCRIPTION
    jimmy-engine-host.exe dynamically links against gfortran/fftw3f/stdc++/quadmath/
    winpthread/gcc_s_seh (tempo-fast-sys's own build.rs links these `dylib`, matching
    Nexus's own native-build convention -- see EngineHost/nexus-compat/README.md).
    Every session of THIS development conversation up to this point ran the exe from a
    shell that had manually added C:\msys64\ucrt64\bin to PATH, which silently masked a
    real bug: a normal launch of Jimmy.exe (no such PATH) has Jimmy's own NativeEngineClient
    spawn jimmy-engine-host.exe with nothing but the system's ordinary PATH, and Windows
    fails to load it with "The code execution cannot proceed because libgfortran-5.dll was
    not found." -- confirmed live, 2026-08-06.

    Windows' DLL search order checks the executable's OWN directory before PATH, so staging
    these DLLs directly into EngineHost\target\x86_64-pc-windows-gnu\release\ (next to
    jimmy-engine-host.exe) fixes this with no PATH changes needed anywhere, for a dev build.

    Phase 5: jimmy-engine-host.exe now also launches its OWN bundled rigctld directly (Nexus's
    tempo_audio::service::run_radio owns this, not Jimmy's C# side anymore). Nexus's own
    rigctld_proc::resolve_rigctld() looks for it at <exe-dir>\hamlib\rigctld.exe (or
    resources\hamlib\rigctld.exe, or PATH) -- NOT at WSJTX_Controller\Resources\hamlib\, which
    is where Jimmy's own bundled copy actually lives. Without this, CAT/PTT silently falls back
    to Rig::vox() with no error surfaced anywhere but Engine's own internal cat_status --
    confirmed live, 2026-08-06 (a bench test's Reply datagram flowed fine, PTT never asserted,
    and nothing in the process's own stdout said why). Stages the whole bundled hamlib folder
    (rigctld.exe + its own DLL dependencies: libhamlib-4, libusb-1.0, plus its own copies of
    libgcc_s_seh-1/libwinpthread-1 which happen to match the MSYS2 ones below) into
    target\x86_64-pc-windows-gnu\release\hamlib\ to match resolve_rigctld()'s first search
    candidate exactly.

    A real release build additionally needs the same DLLs AND the hamlib folder staged into
    WSJTX_Controller\Resources\EngineHost\ (the bundled-release location
    NativeEngineClient.LocateExe() already checks first) -- that's a packaging-time step for
    whenever DecodeEngineMode.JimmyNative graduates past its current INI-only/field-testing
    status; this script only covers the dev build location.

    Idempotent: just re-copies each time. Source is the local MSYS2 installation used to
    build the project (not fetched externally, unlike fetch-hamlib.ps1's Hamlib download) --
    these DLLs are LGPL (gfortran/gcc runtime) and MinGW-w64 runtime license (winpthread),
    both compatible with and NOT requiring relicensing of Jimmy's own GPL-3.0 code; they are
    not committed to source control (build output, like the exe itself). The hamlib folder is
    copied from WSJTX_Controller\Resources\hamlib\, itself fetched by fetch-hamlib.ps1 --
    already source-controlled/licensed there, not re-justified here.

.EXAMPLE
    .\stage-runtime-dlls.ps1
#>

$ErrorActionPreference = "Stop"

$Msys2Bin = "C:\msys64\ucrt64\bin"
# .cargo\config.toml pins the build target to x86_64-pc-windows-gnu explicitly, so cargo always
# builds under target\<triple>\release\, never plain target\release\.
$Dest = Join-Path $PSScriptRoot "target\x86_64-pc-windows-gnu\release"

$Dlls = @(
    "libgfortran-5.dll",
    "libstdc++-6.dll",
    "libfftw3f-3.dll",
    "libquadmath-0.dll",
    "libgcc_s_seh-1.dll",
    "libwinpthread-1.dll"
)

if (-not (Test-Path $Dest)) {
    Write-Error "Build EngineHost first (cargo build --release) -- $Dest does not exist yet."
}

foreach ($dll in $Dlls) {
    $src = Join-Path $Msys2Bin $dll
    if (-not (Test-Path $src)) {
        Write-Error "Expected runtime DLL not found at $src -- is MSYS2 UCRT64 installed at $Msys2Bin ?"
    }
    Copy-Item $src (Join-Path $Dest $dll) -Force
    Write-Host "Staged $dll"
}

$HamlibSrc = Join-Path $PSScriptRoot "..\WSJTX_Controller\Resources\hamlib"
$HamlibDest = Join-Path $Dest "hamlib"
if (-not (Test-Path $HamlibSrc)) {
    Write-Error "Bundled hamlib folder not found at $HamlibSrc -- run WSJTX_Controller\Resources\fetch-hamlib.ps1 first."
}
New-Item -ItemType Directory -Force -Path $HamlibDest | Out-Null
Copy-Item (Join-Path $HamlibSrc "*") $HamlibDest -Force -Recurse
Write-Host "Staged hamlib\ (rigctld.exe + its own dependencies) for run_radio's own resolve_rigctld()"

Write-Host "`nDone. jimmy-engine-host.exe now runs with only the system PATH -- verify with:"
Write-Host "  `$env:PATH = [System.Environment]::GetEnvironmentVariable('PATH','Machine')"
Write-Host "  & `"$Dest\jimmy-engine-host.exe`" --list-devices"
