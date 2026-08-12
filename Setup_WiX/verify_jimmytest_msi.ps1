<#
.SYNOPSIS
    Verifies the built JimmyTest.msi without installing it: version info,
    "Jimmy Test.exe" File table entry, and MajorUpgrade configuration.
    Adapted from the stable Jimmy workspace's verify_msi.ps1 pattern for the
    Jimmy Test side-by-side product (different ProductName/File id/paths).
#>
param(
    [string]$MsiPath = "C:\claude\jimmy_wsjtx31\Setup_WiX\JimmyTest.msi"
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

Write-Output "=== Jimmy Test MSI Verification ==="
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
$productVersion = GetProperty $db "ProductVersion"
$productCode    = GetProperty $db "ProductCode"
$upgradeCode    = GetProperty $db "UpgradeCode"
$manufacturer   = GetProperty $db "Manufacturer"

Write-Check ($productName -eq "Jimmy Test") "ProductName" $productName
Write-Check ($productVersion -match '^\d+\.\d+\.\d+$') "ProductVersion (well-formed)" $productVersion
Write-Check ($null -ne $productCode) "ProductCode" $productCode
Write-Check ($upgradeCode -eq "{BFC5D246-0851-4DB3-B351-EDFD8BDFEA2B}") "UpgradeCode (Jimmy Test's own, not production's)" $upgradeCode
Write-Check ($manufacturer -eq "KB0UZT") "Manufacturer" $manufacturer
Write-Output ""

Write-Output "--- Jimmy Test.exe File table entry ---"
$allFiles = Query $db "SELECT File, FileName, Version FROM File"
$exeRows = @($allFiles | Where-Object { (LongFileName $_[1]) -ieq "Jimmy Test.exe" })
if ($exeRows.Count -eq 0) {
    Write-Check $false "Jimmy Test.exe present in File table" "not found"
    $failures += "Jimmy Test.exe missing from MSI File table"
}
else {
    $exeVersion = $exeRows[0][2]
    Write-Check $true "Jimmy Test.exe present" (LongFileName $exeRows[0][1])
    $versionOk = ($exeVersion -eq $productVersion + ".0" -or $exeVersion -eq $productVersion)
    Write-Check $versionOk "Jimmy Test.exe Version matches ProductVersion" "$exeVersion vs $productVersion"
    if (-not $versionOk) { $warnings += "Jimmy Test.exe file version ($exeVersion) doesn't match ProductVersion ($productVersion)" }
}
Write-Output ""

Write-Output "--- EngineHost bundled ---"
$engineRows = $allFiles | Where-Object { (LongFileName $_[1]) -ieq "jimmy-engine-host.exe" }
Write-Check ($engineRows.Count -gt 0) "jimmy-engine-host.exe present" $(if ($engineRows.Count -gt 0) { "found" } else { "MISSING" })
if ($engineRows.Count -eq 0) { $failures += "jimmy-engine-host.exe not found in MSI" }
Write-Output ""

Write-Output "--- MajorUpgrade configuration ---"
$upgradeRows = Query $db "SELECT UpgradeCode, VersionMin, VersionMax, Attributes, ActionProperty FROM Upgrade"
Write-Check ($upgradeRows.Count -ge 1) "Upgrade table has row(s)" "$($upgradeRows.Count) row(s)"
foreach ($row in $upgradeRows) {
    $sameCode = ($row[0] -eq $upgradeCode)
    Write-Check $sameCode "Upgrade row UpgradeCode matches Property" ("min={0} max={1} action={2}" -f $row[1], $row[2], $row[4])
    if (-not $sameCode) { $failures += "Upgrade table row UpgradeCode ($($row[0])) doesn't match Property UpgradeCode ($upgradeCode)" }
}
if ($upgradeRows.Count -eq 0) { $failures += "Upgrade table missing -- MajorUpgrade may not be configured correctly" }
Write-Output ""

$db = $null
[System.GC]::Collect()
[System.GC]::WaitForPendingFinalizers()

Write-Output "=== Summary ==="
Write-Output "ProductVersion: $productVersion   ProductCode: $productCode"
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
