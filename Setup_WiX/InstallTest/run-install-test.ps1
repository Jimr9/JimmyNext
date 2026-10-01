# Jimmy Next install/upgrade test -- runs INSIDE Windows Sandbox (JimmyNextInstallTest.wsb), never
# on the host: it installs, uninstalls and deletes the Jimmy Next data folder freely.
# Scenarios: fresh install of 2.0.79; upgrade 2.0.77 (last public) -> 2.0.79; upgrade 2.0.78
# (tester build) -> 2.0.79. Each: one product left, at the right version, exe version matches, the
# app starts and keeps running (no radio, no network), shared settings finished, and uninstall
# removes it. Report: results\report.txt beside this script.

$root = 'C:\InstallTest'
$msi = Join-Path $root 'msi'
$out = Join-Path $root 'results'
New-Item -ItemType Directory -Force $out | Out-Null
$report = Join-Path $out 'report.txt'
Set-Content $report "Jimmy Next install test $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$exe = 'C:\Program Files\KB0UZT\Jimmy Next\Jimmy Next.exe'
$data = Join-Path $env:LOCALAPPDATA 'Jimmy Next'
$script:fails = 0

function Say($t) { Add-Content $report $t; Write-Host $t }
function Result($tag, $ok, $detail) {
    if (-not $ok) { $script:fails++ }
    Say ("  [{0}] {1} -- {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $tag, $detail)
}
function Installed {
    Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
                     'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -eq 'Jimmy Next' }
}
function Install($file, $tag) {
    $p = Start-Process msiexec -ArgumentList "/i `"$(Join-Path $msi $file)`" /qn /l*v `"$out\$tag.log`"" -Wait -PassThru
    Result "$tag installer" ($p.ExitCode -eq 0) "msiexec exit $($p.ExitCode)"
}
function CheckVersion($tag, $want) {
    $e = @(Installed)
    $fv = if (Test-Path $exe) { (Get-Item $exe).VersionInfo.FileVersion } else { 'missing' }
    Result "$tag version" ($e.Count -eq 1 -and "$($e[0].DisplayVersion)" -like "$want*" -and "$fv" -like "$want*") `
        "installed products: $($e.Count), version $($e.DisplayVersion -join ','), exe $fv"
}
function StartsCleanly($tag) {
    $p = Start-Process $exe -PassThru
    Start-Sleep -Seconds 30
    if ($p.HasExited) { Result "$tag starts" $false "exited after start, code $($p.ExitCode)" }
    else { Result "$tag starts" $true "running after 30 s" }
    Get-Process -Name 'Jimmy Next', 'jimmy-engine-host', 'rigctld' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3
}
function CheckShared($tag) {
    $shared = Join-Path $data 'Shared.ini'
    $line = if (Test-Path $shared) { Select-String -Path $shared -Pattern '^migratedGroups=' | Select-Object -First 1 } else { $null }
    Result "$tag shared settings" ($null -ne $line -and "$line" -match 'TqslLocation') "$(if ($line) { $line.Line } else { 'Shared.ini missing or unfinished' })"
}
function UninstallAll($tag) {
    foreach ($e in @(Installed)) { Start-Process msiexec -ArgumentList "/x $($e.PSChildName) /qn" -Wait }
    Result "$tag uninstall" ((@(Installed).Count -eq 0) -and -not (Test-Path $exe)) "products left: $(@(Installed).Count), exe present: $(Test-Path $exe)"
}
function FreshData { Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue }

Say "`nScenario 1: fresh install of 2.0.79"
FreshData
Install 'JimmyNext-2.0.79.msi' 'fresh-2.0.79'
CheckVersion 'fresh' '2.0.79'
StartsCleanly 'fresh'
CheckShared 'fresh'
UninstallAll 'fresh'

Say "`nScenario 2: upgrade 2.0.77 (last public) -> 2.0.79"
FreshData
Install 'JimmyNext-2.0.77.msi' 'up77-2.0.77'
CheckVersion 'before upgrade' '2.0.77'
StartsCleanly '2.0.77 first run'
Install 'JimmyNext-2.0.79.msi' 'up77-2.0.79'
CheckVersion 'after upgrade' '2.0.79'
StartsCleanly 'upgraded 2.0.79'
CheckShared 'upgraded from 2.0.77'
Result 'logbook moved to Nexus' (Test-Path (Join-Path $data 'Data\NexusLog\ACTIVE')) 'Data\NexusLog\ACTIVE present'
UninstallAll 'up77'

Say "`nScenario 3: upgrade 2.0.78 (tester build) -> 2.0.79"
FreshData
Install 'JimmyNext-2.0.78.msi' 'up78-2.0.78'
CheckVersion 'before upgrade' '2.0.78'
StartsCleanly '2.0.78 first run'
Install 'JimmyNext-2.0.79.msi' 'up78-2.0.79'
CheckVersion 'after upgrade' '2.0.79'
StartsCleanly 'upgraded 2.0.79'
CheckShared 'upgraded from 2.0.78'
UninstallAll 'up78'

Say "`nRESULT: $(if ($script:fails -eq 0) { 'PASS' } else { "FAIL ($script:fails)" })"
