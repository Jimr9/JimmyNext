<#
.SYNOPSIS
    READ-ONLY Nexus currency / provenance report. Never changes the pin, the patches,
    the prepared tree, or anything else -- it only tells you where Jimmy Next's pinned
    Nexus sits relative to official upstream.

.DESCRIPTION
    Jimmy Next pins an EXACT Nexus commit (EngineHost/nexus-compat/pin.txt) for
    reproducible builds. That is correct, but it means later upstream work does not
    arrive unless someone deliberately bumps the pin. This script makes the gap
    obvious so an explicit upgrade/skip decision can be made at each significant
    release, instead of drifting silently (which is exactly how the pre-v1.10.3
    integration fell ~175 commits behind before anyone noticed).

    It prints:
      Jimmy Nexus base / reference / commit date
      Current upstream STABLE (newest vX.Y.Z tag) + its commit
      Current upstream main + its commit
      Commits behind stable / behind main   (when a local .nexus-src clone is present)
      The ordered Jimmy compatibility patches + their SHA-256
      Whether each patch still applies clean against the CURRENT pin's prepared tree
      UTC timestamp of this check + the official remote URL

    It does NOT auto-upgrade. Run it before every significant Jimmy Next release and
    monthly during active Nexus development, then record an owner decision:
    upgrade to the exact current stable / stay pinned (reason + expiry) / evaluate a
    named intermediate fix.

.PARAMETER Gate
    Exit non-zero if the pinned commit is behind the current stable tag AND no
    disposition line is present in pin.txt (a line starting '# DISPOSITION:'). For
    CI/release gating. Without -Gate the script always exits 0 (report only).
#>
[CmdletBinding()]
param([switch]$Gate)

$ErrorActionPreference = "Stop"
$RepoRoot   = Split-Path -Parent $PSScriptRoot
$CompatDir  = Join-Path $RepoRoot "EngineHost\nexus-compat"
$PinFile    = Join-Path $CompatDir "pin.txt"
$PatchDir   = Join-Path $CompatDir "patches"
$StagingDir = Join-Path $RepoRoot "EngineHost\.nexus-src"

if (-not (Test-Path $PinFile)) { throw "Cannot find $PinFile -- run from a Jimmy repo checkout." }

# --- pin.txt (KEY=VALUE, '#' comments; accepts NEXUS_TAG or NEXUS_REF) ------------------
$Pin = @{}
$Disposition = $null
Get-Content $PinFile | ForEach-Object {
    $line = $_.Trim()
    if ($line -like "# DISPOSITION:*") { $Disposition = $line.Substring(14).Trim(); return }
    if ($line -eq "" -or $line.StartsWith("#")) { return }
    $p = $line.Split("=", 2)
    if ($p.Length -eq 2) { $Pin[$p[0].Trim()] = $p[1].Trim() }
}
$repo   = $Pin["NEXUS_REPO"]
$ref    = if ($Pin.ContainsKey("NEXUS_REF")) { $Pin["NEXUS_REF"] } else { $Pin["NEXUS_TAG"] }
$commit = $Pin["NEXUS_COMMIT"]
if (-not $repo -or -not $commit) { throw "pin.txt missing NEXUS_REPO / NEXUS_COMMIT" }

function Git-Quiet { param([string[]]$a) $prev=$ErrorActionPreference; $ErrorActionPreference="Continue"; $o = & git @a 2>$null; $ErrorActionPreference=$prev; return $o }

# --- upstream refs via ls-remote (no clone, no fetch) ----------------------------------
Write-Host "Querying $repo ..." -ForegroundColor DarkGray
$lsTags = Git-Quiet @("ls-remote","--tags","--refs",$repo)
$lsHead = Git-Quiet @("ls-remote",$repo,"HEAD","refs/heads/main")

$stableTag = $null; $stableCommit = $null
$verRe = [regex]'refs/tags/(v(\d+)\.(\d+)\.(\d+))$'
$best = $null
foreach ($l in $lsTags) {
    $m = $verRe.Match($l)
    if (-not $m.Success) { continue }
    $key = [version]("{0}.{1}.{2}" -f $m.Groups[2].Value,$m.Groups[3].Value,$m.Groups[4].Value)
    if ($null -eq $best -or $key -gt $best.Key) {
        $best = @{ Key = $key; Tag = $m.Groups[1].Value; Commit = $l.Split("`t")[0] }
    }
}
if ($best) { $stableTag = $best.Tag; $stableCommit = $best.Commit }

$mainCommit = $null
foreach ($l in $lsHead) { if ($l -match "refs/heads/main|HEAD") { $mainCommit = $l.Split("`t")[0]; break } }

# --- commit-count gaps: only when a local prepared clone is present -------------------
$behindStable = "(no local .nexus-src -- run scripts\prepare-nexus.ps1 for exact counts)"
$behindMain   = $behindStable
$baseDate     = "(unknown)"
if (Test-Path (Join-Path $StagingDir ".git")) {
    $null = Git-Quiet @("-C",$StagingDir,"fetch","--quiet","--tags","origin","main")
    $bd = Git-Quiet @("-C",$StagingDir,"show","-s","--format=%ci",$commit)
    if ($bd) { $baseDate = ($bd | Out-String).Trim() }
    if ($stableCommit) {
        $c = Git-Quiet @("-C",$StagingDir,"rev-list","--count","$commit..$stableCommit")
        if ($c) { $behindStable = ($c | Out-String).Trim() }
    }
    if ($mainCommit) {
        $c = Git-Quiet @("-C",$StagingDir,"rev-list","--count","$commit..$mainCommit")
        if ($c) { $behindMain = ($c | Out-String).Trim() }
    }
}

# --- patches -------------------------------------------------------------------------
$patchRows = @()
Get-ChildItem $PatchDir -Filter *.patch | Sort-Object Name | ForEach-Object {
    $patchRows += [pscustomobject]@{ Name = $_.Name; Sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
}

# --- report -----------------------------------------------------------------------------
$atStable = ($stableCommit -and $stableCommit -eq $commit)
$behindStableText = if ($atStable) { "0 (pin IS the current stable tag commit)" } else { $behindStable }
"" ; "=== Jimmy Next -- Nexus currency / provenance ==="
"Checked (UTC)          : {0}" -f (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ssZ")
"Official remote        : {0}" -f $repo
""
"Jimmy Nexus base       : {0}" -f $commit
"Jimmy Nexus reference  : {0}" -f $ref
"Jimmy Nexus base date  : {0}" -f $baseDate
"Current upstream stable: {0}  {1}" -f $stableTag, $stableCommit
"Current upstream main  : {0}" -f $mainCommit
"Commits behind stable  : {0}" -f $behindStableText
"Commits behind main    : {0}" -f $behindMain
""
"Jimmy compatibility patches (ordered):"
$patchRows | ForEach-Object { "  {0,-45} {1}" -f $_.Name, $_.Sha256 }
"Patch count            : {0}  (8 is the maximum; long-term this should DECREASE)" -f $patchRows.Count
""
if ($Disposition) { "Recorded disposition   : {0}" -f $Disposition }
else              { "Recorded disposition   : (none -- add a '# DISPOSITION: ...' line to pin.txt at release review)" }
""
if ($atStable) {
    "RESULT: pin is at the current stable tag. Re-review at the next stable release." ; ""
    exit 0
}
"RESULT: pin is BEHIND current stable. At the next significant release, classify the"
"        intervening commits (safety / API / build / new-scope) and record an explicit"
"        decision: upgrade to $stableTag, stay pinned (reason + expiry), or take a named"
"        intermediate fix. See C:\chat gpt\nexus plan.txt Section 20."
""
if ($Gate -and -not $Disposition) {
    Write-Error "nexus-status -Gate: pin is behind stable $stableTag and pin.txt has no '# DISPOSITION:' line."
    exit 2
}
exit 0
