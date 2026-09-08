<#
.SYNOPSIS
    Prepares a clean, reproducible local checkout of official Nexus (pinned revision) with
    Jimmy Next's minimum-necessary compatibility patches applied, for EngineHost to build against.

.DESCRIPTION
    Jimmy Next is a CONSUMER of the third-party Nexus project (https://github.com/kd9taw/Nexus).
    We never modify a developer's own reference clone of Nexus, and we never submit changes
    upstream. This script instead builds and owns a SEPARATE staging checkout
    (EngineHost/.nexus-src, git-ignored, safe to delete and regenerate at any time) by:

      1. Reading the pinned revision from EngineHost/nexus-compat/pin.txt.
      2. Cloning official Nexus fresh at that exact revision into the staging directory. This is
         a plain, independent clone -- it never reads from or writes to a developer's own Nexus
         checkout (e.g. C:\claude\nexus), so that checkout is unaffected either way.
      3. Applying every patch file in EngineHost/nexus-compat/patches/ on top, each checked
         with a dry run first. A patch that no longer applies cleanly means official Nexus has
         drifted since the patch was written -- the script fails loudly rather than silently
         building against a mismatched tree. See EngineHost/nexus-compat/README.md.

    Safe to re-run at any time. If the staging checkout already matches the pinned revision and
    has every patch applied (tracked via .nexus-src-info.json), it is left alone.

.PARAMETER Force
    Delete and rebuild the staging checkout even if it already looks up to date.
#>
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$CompatDir = Join-Path $RepoRoot "EngineHost\nexus-compat"
$PinFile = Join-Path $CompatDir "pin.txt"
$PatchDir = Join-Path $CompatDir "patches"
$StagingDir = Join-Path $RepoRoot "EngineHost\.nexus-src"
$InfoFile = Join-Path $RepoRoot "EngineHost\.nexus-src-info.json"

if (-not (Test-Path $PinFile)) {
    throw "Cannot find $PinFile -- run this script from a Jimmy repo checkout."
}

# --- Parse pin.txt (simple KEY=VALUE, '#' comments) --------------------------------------
$Pin = @{}
Get-Content $PinFile | ForEach-Object {
    $line = $_.Trim()
    if ($line -eq "" -or $line.StartsWith("#")) { return }
    $parts = $line.Split("=", 2)
    if ($parts.Length -eq 2) { $Pin[$parts[0].Trim()] = $parts[1].Trim() }
}
foreach ($key in @("NEXUS_REPO", "NEXUS_TAG", "NEXUS_COMMIT")) {
    if (-not $Pin.ContainsKey($key)) { throw "pin.txt is missing $key" }
}
Write-Host "Pinned Nexus revision: $($Pin.NEXUS_TAG) ($($Pin.NEXUS_COMMIT))"

# --- Locate patch.exe (Git for Windows ships GNU patch under usr\bin) --------------------
# Deliberately prefers Git for Windows' own copy over whatever "patch.exe" a PATH search
# turns up FIRST -- a step earlier in CI that adds MSYS2 to PATH (for tempo-fast-sys's native
# libtempo build) can shadow Git's with MSYS2's own patch.exe, which crashed applying these
# exact patches ("Assertation failed!", no further detail) the first time this ran in CI, while
# Git's own copy has been reliable in every local test. Check the Git-relative path FIRST.
function Find-PatchExe {
    $gitCmd = Get-Command git.exe -ErrorAction SilentlyContinue
    if ($gitCmd) {
        $candidate = Join-Path (Split-Path (Split-Path $gitCmd.Source)) "usr\bin\patch.exe"
        if (Test-Path $candidate) { return $candidate }
    }
    foreach ($guess in @("C:\Program Files\Git\usr\bin\patch.exe", "C:\Program Files (x86)\Git\usr\bin\patch.exe")) {
        if (Test-Path $guess) { return $guess }
    }
    $cmd = Get-Command patch.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "Could not find patch.exe (normally bundled with Git for Windows under usr\bin). Install Git for Windows or add patch.exe to PATH."
}
$PatchExe = Find-PatchExe

# --- Compute a hash of the patch set + pin, to detect "already prepared, nothing to do" --
$patchFiles = Get-ChildItem $PatchDir -Filter *.patch | Sort-Object Name
$hashInput = ($Pin.NEXUS_COMMIT + "|" + (($patchFiles | ForEach-Object {
    $_.Name + ":" + (Get-FileHash $_.FullName -Algorithm SHA256).Hash
}) -join "|"))
$expectedHash = [System.BitConverter]::ToString(
    [System.Security.Cryptography.SHA256]::Create().ComputeHash([System.Text.Encoding]::UTF8.GetBytes($hashInput))
).Replace("-", "")

if (-not $Force -and (Test-Path $InfoFile)) {
    try {
        $existing = Get-Content $InfoFile -Raw | ConvertFrom-Json
        # The state hash covers the pin COMMIT string + every patch file's SHA-256. But a hash
        # match plus "the directory exists" is not proof the staged tree is actually AT the
        # pinned revision -- a half-finished prior run, a manual `git checkout` inside
        # .nexus-src, or a stray edit could leave it elsewhere. Independently confirm the staged
        # HEAD still equals the exact pin before trusting the fast path; any doubt -> rebuild.
        $stagedHead = $null
        if (Test-Path (Join-Path $StagingDir ".git")) {
            $prevEAP = $ErrorActionPreference
            $ErrorActionPreference = "Continue"
            $stagedHead = (& git -C $StagingDir rev-parse HEAD 2>$null | Out-String).Trim()
            $ErrorActionPreference = $prevEAP
        }
        if ($existing.stateHash -eq $expectedHash `
                -and (Test-Path $StagingDir) `
                -and $stagedHead -eq $Pin.NEXUS_COMMIT) {
            Write-Host "Staging checkout at $StagingDir already matches pin + patch set (HEAD $stagedHead). Nothing to do."
            Write-Host "(Use -Force to rebuild anyway.)"
            exit 0
        }
        if ($stagedHead -and $stagedHead -ne $Pin.NEXUS_COMMIT) {
            Write-Host "Staged Nexus HEAD is $stagedHead, expected $($Pin.NEXUS_COMMIT) -- rebuilding from scratch."
        }
    } catch {
        # Info file unreadable/stale -- fall through and rebuild.
    }
}

# --- Rebuild the staging checkout from scratch --------------------------------------------
if (Test-Path $StagingDir) {
    Write-Host "Removing stale staging checkout at $StagingDir ..."
    Remove-Item -Recurse -Force $StagingDir
}
if (Test-Path $InfoFile) { Remove-Item -Force $InfoFile }

# git writes progress + "From <url>" to stderr even on success; under $ErrorActionPreference =
# "Stop" that stderr, once it reaches the pipeline, is wrapped as a terminating NativeCommandError.
# Drop to Continue only around the native git calls (their real outcome is $LASTEXITCODE).
$prevEAP = $ErrorActionPreference
$ErrorActionPreference = "Continue"
if ($Pin.NEXUS_TAG -eq "main") {
    # This integration pins an exact COMMIT on main, not a tag. A full clone of `main` always
    # contains NEXUS_COMMIT (it is an ancestor of main's tip); detach onto it exactly. The
    # rev-parse check below is the real guarantee we are on the intended revision.
    Write-Host "Cloning official Nexus (main) into $StagingDir, then pinning to $($Pin.NEXUS_COMMIT) ..."
    & git clone --quiet --branch main --single-branch $Pin.NEXUS_REPO $StagingDir
    if ($LASTEXITCODE -ne 0) { $ErrorActionPreference = $prevEAP; throw "git clone failed (exit $LASTEXITCODE)" }
    & git -C $StagingDir checkout --quiet --detach $Pin.NEXUS_COMMIT
    if ($LASTEXITCODE -ne 0) { $ErrorActionPreference = $prevEAP; throw "git checkout $($Pin.NEXUS_COMMIT) failed (exit $LASTEXITCODE) -- commit not reachable from official Nexus main." }
} else {
    Write-Host "Cloning official Nexus ($($Pin.NEXUS_TAG)) into $StagingDir ..."
    & git clone --quiet --branch $Pin.NEXUS_TAG --single-branch $Pin.NEXUS_REPO $StagingDir
    if ($LASTEXITCODE -ne 0) { $ErrorActionPreference = $prevEAP; throw "git clone failed (exit $LASTEXITCODE)" }
}
$ErrorActionPreference = $prevEAP

$actualCommit = (& git -C $StagingDir rev-parse HEAD).Trim()
if ($actualCommit -ne $Pin.NEXUS_COMMIT) {
    throw "Pinned revision $($Pin.NEXUS_TAG) resolved to $actualCommit, expected $($Pin.NEXUS_COMMIT). " +
          "Someone moved the ref upstream, or pin.txt is wrong -- stopping rather than building against an unverified revision."
}
Write-Host "Verified: $($Pin.NEXUS_TAG) = $actualCommit"

# --- Apply each compatibility patch, dry-run first --------------------------------------
# Maps each patch file to the directory (relative to $StagingDir) it must be applied from.
$PatchTargets = @{
    "tempo-app-engine.patch"                     = "crates\tempo-app"
    "tempo-app-settings.patch"                   = "crates\tempo-app"
    "tempo-app-snapshot.patch"                   = "crates\tempo-app"
    "tempo-audio-rig.patch"                      = "crates\tempo-audio"
    "tempo-audio-service.patch"                  = "crates\tempo-audio"
    "tempo-audio-slot.patch"                     = "crates\tempo-audio"
    "tempo-audio-rigctld-test-portability.patch" = "crates\tempo-audio"
    "tempo-fast-sys-build.patch"                 = "crates\tempo-fast-sys"
}

# patch.exe writes "checking file ..." to stdout but any warning/failure detail to stderr;
# same PS5.1 native-stderr-under-Stop hazard as the git calls above.
$ErrorActionPreference = "Continue"
foreach ($patchFile in $patchFiles) {
    if (-not $PatchTargets.ContainsKey($patchFile.Name)) {
        $ErrorActionPreference = $prevEAP
        throw "$($patchFile.Name) has no entry in `$PatchTargets in this script -- add one before proceeding."
    }
    $targetDir = Join-Path $StagingDir $PatchTargets[$patchFile.Name]
    $patchPath = $patchFile.FullName
    Write-Host "Applying $($patchFile.Name) to $targetDir ..."

    & $PatchExe -p1 --dry-run "--directory=$targetDir" "--input=$patchPath" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        $ErrorActionPreference = $prevEAP
        throw @"
Compatibility patch '$($patchFile.Name)' no longer applies cleanly against Nexus $($Pin.NEXUS_TAG).
This means official Nexus has changed the surrounding code since this patch was written.
Do NOT force it. Instead:
  1. Read EngineHost\nexus-compat\README.md for what this patch does and why.
  2. Check whether Nexus now provides the same functionality natively (see that file's
     "How to check" step for this patch) -- if so, delete the patch instead of fixing it.
  3. If still needed, re-derive it by hand against the new revision's actual code.
"@
    }
    & $PatchExe -p1 "--directory=$targetDir" "--input=$patchPath"
    if ($LASTEXITCODE -ne 0) { $ErrorActionPreference = $prevEAP; throw "Patch '$($patchFile.Name)' passed --dry-run but failed for real -- investigate before continuing." }
}
$ErrorActionPreference = $prevEAP

# --- Record state + full provenance for the fast-path check and release evidence -------
# Nexus modernization Stage 12: the provenance block below is what a release review and
# scripts/nexus-status.ps1 read to see EXACTLY what was built -- repo, requested ref,
# resolved commit + its date, the upstream stable/main heads at the moment of preparation,
# every patch file with its SHA-256, and the tool versions. `stateHash` (pin commit + patch
# hashes) still drives the "already prepared, nothing to do" fast path on re-runs.
$prevEAP2 = $ErrorActionPreference; $ErrorActionPreference = "Continue"
$commitDate = (& git -C $StagingDir show -s --format=%cI $actualCommit 2>$null | Out-String).Trim()
& git -C $StagingDir fetch --quiet --tags origin main 2>$null
$upstreamMain = (& git -C $StagingDir rev-parse FETCH_HEAD 2>$null | Out-String).Trim()
if (-not $upstreamMain -or $upstreamMain -notmatch '^[0-9a-f]{40}$') {
    $upstreamMain = (& git ls-remote $Pin.NEXUS_REPO refs/heads/main 2>$null | Out-String).Split("`t")[0].Trim()
}
$upstreamStable = ""
$stableTags = & git -C $StagingDir tag --list "v*" 2>$null | Where-Object { $_ -match '^v\d+\.\d+\.\d+$' }
if ($stableTags) {
    $newest = $stableTags | Sort-Object { [version]($_.TrimStart('v')) } | Select-Object -Last 1
    $upstreamStable = "$newest " + (& git -C $StagingDir rev-list -n 1 $newest 2>$null | Out-String).Trim()
}
$patchTool = (& $PatchExe --version 2>$null | Select-Object -First 1 | Out-String).Trim()
$gitVer    = (& git --version 2>$null | Out-String).Trim()
$ErrorActionPreference = $prevEAP2

$patchManifest = @{}
foreach ($pf in $patchFiles) { $patchManifest[$pf.Name] = (Get-FileHash $pf.FullName -Algorithm SHA256).Hash }

@{
    nexusRepo             = $Pin.NEXUS_REPO
    nexusRef              = $(if ($Pin.ContainsKey("NEXUS_REF")) { $Pin.NEXUS_REF } else { $Pin.NEXUS_TAG })
    nexusTag              = $Pin.NEXUS_TAG
    nexusCommit           = $Pin.NEXUS_COMMIT
    nexusCommitDate       = $commitDate
    upstreamStableAtPrep  = $upstreamStable
    upstreamMainAtPrep    = $upstreamMain
    patchCount            = $patchFiles.Count
    patchManifest         = $patchManifest
    patchTool             = $patchTool
    gitVersion            = $gitVer
    stateHash             = $expectedHash
    preparedAt            = (Get-Date).ToString("o")
} | ConvertTo-Json -Depth 4 | Set-Content -Path $InfoFile -Encoding utf8

Write-Host ""
Write-Host "Nexus staging checkout ready: $StagingDir"
Write-Host "  Revision: $($Pin.NEXUS_TAG) ($($Pin.NEXUS_COMMIT))"
Write-Host "  Patches applied: $($patchFiles.Count)"
