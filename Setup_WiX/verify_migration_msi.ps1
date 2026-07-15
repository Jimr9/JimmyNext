<#
.SYNOPSIS
    Verifies the migration-built Jimmy MSI without installing it: product
    identity, key required files, UpgradeCode continuity with the stable
    production release, and MajorUpgrade configuration.

.DESCRIPTION
    Reads the MSI database directly via the Windows Installer COM API
    (WindowsInstaller.Installer). No install/uninstall/repair is performed.
    Adapted from the stable Jimmy workspace's verify_msi.ps1 pattern for the
    Stage B7 self-contained WiX 5 harvesting rewrite.
#>
param(
    [string]$MsiPath        = "C:\claude\jimmy_wsjtx31\Jimmy_migration_test.msi",
    [string]$PublishOutDir  = "C:\claude\jimmy_wsjtx31\Setup_WiX\PublishOutput"
)

$ErrorActionPreference = "Stop"
$failures = @()
$warnings = @()

function Write-Check($ok, $label, $detail) {
    $mark = if ($ok) { "PASS" } else { "FAIL" }
    Write-Output ("  [{0}] {1}{2}" -f $mark, $label, $(if ($detail) { " -- $detail" } else { "" }))
}

function Query($db, $sql) {
    $view = $db.OpenView($sql)
    [void]$view.Execute()
    $rows = New-Object System.Collections.ArrayList
    while ($true) {
        $rec = $view.Fetch()
        if ($null -eq $rec) { break }
        $cols = New-Object System.Collections.ArrayList
        for ($i = 1; $i -le 8; $i++) {
            [void]$cols.Add([string]$rec.StringData($i))
        }
        [void]$rows.Add($cols.ToArray())
    }
    [void]$view.Close()
    return ,$rows.ToArray()
}

function GetProperty($db, $name) {
    $rows = Query $db ("SELECT Value FROM Property WHERE Property = '{0}'" -f $name)
    if ($rows.Count -eq 0) { return $null }
    return $rows[0][0]
}

function LongFileName($fileNameField) {
    $parts = $fileNameField -split '\|'
    return $parts[$parts.Count - 1]
}

Write-Output "=== Jimmy Migration MSI Verification (Stage B7, not installed) ==="
Write-Output "MSI: $MsiPath"
Write-Output ""

if (-not (Test-Path $MsiPath)) {
    Write-Output "  [FAIL] MSI file not found."
    exit 1
}
$msiSize = (Get-Item $MsiPath).Length
Write-Output ("MSI size: {0:N0} bytes" -f $msiSize)
Write-Output ""

$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase($MsiPath, 0)

Write-Output "--- Product identity ---"
$productName    = GetProperty $db "ProductName"
$productCode    = GetProperty $db "ProductCode"
$upgradeCode    = GetProperty $db "UpgradeCode"
$manufacturer   = GetProperty $db "Manufacturer"

Write-Check ($productName -eq "Jimmy") "ProductName" $productName
Write-Check ($null -ne $productCode) "ProductCode" $productCode
Write-Check ($upgradeCode -eq "{D5415907-DD93-4188-85A8-F15A73F949C2}") "UpgradeCode matches production" $upgradeCode
if ($upgradeCode -ne "{D5415907-DD93-4188-85A8-F15A73F949C2}") { $failures += "UpgradeCode does not match production ($upgradeCode)" }
Write-Check ($manufacturer -eq "KB0UZT") "Manufacturer" $manufacturer
Write-Output ""

Write-Output "--- Required files present (by name) ---"
$allFiles = Query $db "SELECT File, FileName FROM File"
$allLongNames = $allFiles | ForEach-Object { LongFileName $_[1] }

$required = @(
    "Jimmy.exe", "Jimmy.dll", "Jimmy.deps.json", "Jimmy.runtimeconfig.json",
    "Jimmy.dll.config", "SQLite.Interop.dll", "System.Data.SQLite.dll",
    "clublog_key.txt", "MQTTnet.dll", "MQTTnet.Extensions.ManagedClient.dll"
)
foreach ($name in $required) {
    $found = $allLongNames | Where-Object { $_ -ieq $name }
    $ok = ($found -ne $null) -and ($found.Count -gt 0)
    Write-Check $ok $name $(if ($ok) { "$($found.Count) copy/copies" } else { "MISSING" })
    if (-not $ok) { $failures += "$name not found in MSI" }
}
Write-Output ""

Write-Output "--- Rule Definitions and Resources ---"
$iniFiles  = $allLongNames | Where-Object { $_ -like "*.ini" }
$wavFiles  = $allLongNames | Where-Object { $_ -like "*.wav" }
Write-Check ($iniFiles.Count -gt 0) "Rule Definition .ini files packaged" "$($iniFiles.Count) file(s)"
Write-Check ($wavFiles.Count -gt 0) "Sound .wav files packaged" "$($wavFiles.Count) file(s)"
if ($iniFiles.Count -eq 0) { $failures += "No Rule Definition .ini files found in MSI" }
if ($wavFiles.Count -eq 0) { $failures += "No .wav sound files found in MSI" }
Write-Output ""

Write-Output "--- File count cross-check against publish output ---"
if (Test-Path $PublishOutDir) {
    $diskCount = (Get-ChildItem $PublishOutDir -Recurse -File).Count
    Write-Check ($allFiles.Count -ge $diskCount) "MSI file count >= disk publish output" "MSI=$($allFiles.Count) disk=$diskCount (MSI also includes shortcuts)"
    if ($allFiles.Count -lt $diskCount) { $failures += "MSI file count ($($allFiles.Count)) is less than publish output on disk ($diskCount) -- harvesting may have missed files" }
} else {
    Write-Output "  (skipped -- $PublishOutDir not found)"
}
Write-Output ""

Write-Output "--- Duplicate directory entries check (known WiX 5 harvesting gotcha) ---"
$dirRows = Query $db "SELECT Directory, Directory_Parent, DefaultDir FROM Directory"
$dirKeys = $dirRows | ForEach-Object { "$($_[1])|$($_[2])" }
$dupGroups = $dirKeys | Group-Object | Where-Object { $_.Count -gt 1 }
Write-Check ($dupGroups.Count -eq 0) "No duplicate (parent, name) directory entries" "$($dupGroups.Count) duplicate group(s)"
if ($dupGroups.Count -gt 0) { $failures += "Duplicate directory entries found: $($dupGroups.Name -join ', ')" }
Write-Output ""

Write-Output "--- MajorUpgrade / Upgrade table ---"
$upgradeRows = Query $db "SELECT UpgradeCode, VersionMin, VersionMax, Attributes, ActionProperty FROM Upgrade"
Write-Check ($upgradeRows.Count -ge 2) "Upgrade table has upgrade+downgrade rows" "$($upgradeRows.Count) row(s)"
foreach ($row in $upgradeRows) {
    $sameCode = ($row[0] -eq $upgradeCode)
    Write-Check $sameCode "Upgrade row UpgradeCode matches Property" ("min={0} max={1} action={2}" -f $row[1], $row[2], $row[4])
    if (-not $sameCode) { $failures += "Upgrade table row UpgradeCode ($($row[0])) doesn't match Property UpgradeCode ($upgradeCode)" }
}
if ($upgradeRows.Count -lt 2) { $failures += "Upgrade table missing expected rows -- MajorUpgrade may not be configured correctly" }
Write-Output ""

$db = $null
[System.GC]::Collect()
[System.GC]::WaitForPendingFinalizers()

Write-Output "=== Summary ==="
Write-Output "ProductCode: $productCode"
Write-Output "Total files in MSI: $($allFiles.Count)"
if ($warnings.Count -gt 0) {
    Write-Output ""
    Write-Output "Warnings:"
    foreach ($w in $warnings) { Write-Output "  - $w" }
}
if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "FAILURES:"
    foreach ($f in $failures) { Write-Output "  - $f" }
    Write-Output ""
    Write-Output "RESULT: FAIL"
    exit 1
}
else {
    Write-Output ""
    Write-Output "RESULT: PASS"
    exit 0
}
