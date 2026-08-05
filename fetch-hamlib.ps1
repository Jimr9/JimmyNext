<#
.SYNOPSIS
    Fetches the Hamlib (rigctld) Windows runtime and stages it as a bundled
    resource at WSJTX_Controller\Resources\hamlib\, so Jimmy ships direct CAT
    rig control (self-sufficiency plan, Phase 1) with zero extra installs for
    the operator -- Jimmy launches this bundled rigctld itself.

.DESCRIPTION
    Mirrors the open-source Nexus project's own scripts/fetch-hamlib.sh
    exactly (same release version, same file list, same checksum) -- ported
    to PowerShell so it matches Jimmy's existing script convention (build.bat,
    run_replay_tests.bat, verify_msi.ps1 all live at the repo root, not in a
    scripts\ subfolder) rather than adding a bash dependency.

    Hamlib is GPL/LGPL, compatible with Jimmy's own GPL-3.0 license. The
    binaries are NOT committed to source control (see .gitignore) -- this
    script reproduces them. Idempotent: skips the download if already staged.

.EXAMPLE
    .\fetch-hamlib.ps1

.NOTES
    Requires no third-party tools: uses Invoke-WebRequest, Get-FileHash, and
    Expand-Archive, all built into PowerShell 5.1+.
#>

$ErrorActionPreference = "Stop"

$Ver = "4.7.1"
$Zip = "hamlib-w64-$Ver.zip"
$Url = "https://github.com/Hamlib/Hamlib/releases/download/$Ver/$Zip"
$Sha256 = "5B2A5D6EFC37171C24EE6AC44E6304710219859F30FA4DFC77688F71B3440402"

$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$Dest = Join-Path $RepoRoot "WSJTX_Controller\Resources\hamlib"

# What rigctld.exe needs at runtime (+ rigctl.exe for diagnostics, + licenses).
$Want = @("rigctld.exe", "rigctl.exe", "rotctld.exe", "rotctl.exe",
          "libhamlib-4.dll", "libwinpthread-1.dll", "libusb-1.0.dll", "libgcc_s_seh-1.dll")
$Lic = @("COPYING.txt", "COPYING.LIB.txt", "LICENSE.txt", "AUTHORS.txt")

if ((Test-Path (Join-Path $Dest "rigctld.exe")) -and
    (Test-Path (Join-Path $Dest "rotctld.exe")) -and
    (Test-Path (Join-Path $Dest "libhamlib-4.dll"))) {
    Write-Host "Hamlib already staged at $Dest"
    exit 0
}

$Tmp = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Path $Tmp | Out-Null
try {
    $ZipPath = Join-Path $Tmp $Zip
    Write-Host "Downloading Hamlib $Ver..."
    Invoke-WebRequest -Uri $Url -OutFile $ZipPath -UseBasicParsing

    $ActualHash = (Get-FileHash -Path $ZipPath -Algorithm SHA256).Hash
    if ($ActualHash -ne $Sha256) {
        Write-Error "Checksum mismatch -- expected $Sha256, got $ActualHash. Aborting."
        exit 1
    }

    Expand-Archive -Path $ZipPath -DestinationPath $Tmp -Force

    $SrcRoot = Join-Path $Tmp "hamlib-w64-$Ver"
    New-Item -ItemType Directory -Path $Dest -Force | Out-Null

    foreach ($f in $Want) {
        $src = Join-Path (Join-Path $SrcRoot "bin") $f
        Copy-Item -Path $src -Destination $Dest -Force
        Write-Host "  + $f"
    }
    foreach ($f in $Lic) {
        $src = Join-Path $SrcRoot $f
        if (Test-Path $src) { Copy-Item -Path $src -Destination $Dest -Force }
    }
    Write-Host "Hamlib staged -> $Dest"
}
finally {
    Remove-Item -Path $Tmp -Recurse -Force -ErrorAction SilentlyContinue
}
