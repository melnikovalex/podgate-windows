<#
.SYNOPSIS
  Compare the current Bluetooth state with a backup and, with -Apply, switch back on what was on.

.DESCRIPTION
  Standalone: works even if PodGate itself is broken. Without -Apply it only prints differences.

  With -Apply (run elevated):
    1. Enables every AirPods Bluetooth service that is disabled now (all services known from the backup
       and from BTHPORT), because companion apps can leave them disabled after a crash.
    2. Enables Bluetooth device nodes that are disabled now (problem code 22) but weren't in the backup.
    3. Restores HiberbootEnabled (Fast Startup) if it differs from the backup.
  Never unpairs devices and never deletes BTHPORT keys. Registry files are imported only with -ImportRegistry.

.PARAMETER Backup
  Backup folder (name under backups\ or a full path). Default: the newest one.

.PARAMETER Apply
  Make the changes. Without it this is a dry run.

.PARAMETER ImportRegistry
  Last resort: also merge bthport-devices.reg from the backup (reg import; it adds and overwrites values, deletes nothing).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restore-bt.ps1
  (elevated) powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restore-bt.ps1 -Backup 20260101-120000-before-driver-update -Apply
#>
[CmdletBinding()]
param(
    [string]$Backup,
    [switch]$Apply,
    [switch]$ImportRegistry,
    [string]$Address
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\PodGate.Common.ps1')

$backupsRoot = Join-Path $script:RepoRoot 'backups'
if (-not $Backup) {
    $latest = Get-ChildItem $backupsRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
    if (-not $latest) { throw "No backups under $backupsRoot. Run scripts\backup-bt.ps1 first." }
    $Backup = $latest.FullName
}
elseif (Test-Path (Join-Path $backupsRoot $Backup)) { $Backup = Join-Path $backupsRoot $Backup }
elseif (-not [IO.Path]::IsPathRooted($Backup)) { $Backup = Join-Path $script:RepoRoot $Backup }
if (-not (Test-Path $Backup)) { throw "Backup folder not found: $Backup" }

if ($Apply -and -not (Test-PodGateElevated)) {
    throw '-Apply needs an elevated PowerShell (right-click > Run as administrator).'
}

function Read-BackupJson {
    param([string]$Name)
    $p = Join-Path $Backup $Name
    if (Test-Path $p) { return (Get-Content $p -Raw | ConvertFrom-Json) }
    Write-Warning "$Name is missing from the backup; skipping that part."
    $null
}

$differences = 0
$failures = 0
$mode = 'DRY RUN (add -Apply to act)'
if ($Apply) { $mode = 'APPLY' }
Write-Host "Restore from $Backup  [$mode]"

# 1. AirPods services -----------------------------------------------------------------------------
$oldReport = Read-BackupJson 'device-report.json'
if ($Address) { $addr = ConvertTo-BtAddress $Address }
elseif ($oldReport) { $addr = $oldReport.Address }
else { $addr = Resolve-PodGateAddress }

$now = Get-PodGateDeviceReport -Address $addr
$known = @(@($now.Services | ForEach-Object { $_.Guid }) + @(if ($oldReport) { $oldReport.Services | ForEach-Object { $_.Guid } }) | Sort-Object -Unique)

Write-Host "`nAirPods services ($addr)"
foreach ($g in $known) {
    $label = "{0,-8} {1}" -f (Get-BtServiceShortName $g), (Get-BtServiceName $g)
    $cur = @($now.Services | Where-Object { $_.Guid -eq $g })
    if ($cur.Count -gt 0 -and $cur[0].Installed) { Write-Host "  ok        $label"; continue }
    $differences++
    if (-not $Apply) { Write-Host "  DISABLED  $label -> would enable"; continue }
    try {
        $rc = [PodGate.BtNative]::SetServiceState($addr, [Guid]$g, $true)
        if ($rc -eq 0) { Write-Host "  enabled   $label" }
        else { $failures++; Write-Warning "enable $label failed, Win32 error $rc" }
    }
    catch { $failures++; Write-Warning "enable $label failed: $($_.Exception.Message)" }
}

# 2. Device nodes --------------------------------------------------------------------------------
Write-Host "`nBluetooth device nodes"
$oldNodes = Read-BackupJson 'bt-devnodes.json'
if ($oldNodes) {
    $current = @{}
    Get-PnpDevice -ErrorAction SilentlyContinue | ForEach-Object { $current[$_.InstanceId] = $_ }
    $disabled = 0
    foreach ($o in @($oldNodes)) {
        $c = $current[$o.InstanceId]
        if (-not $c) { continue }  # nodes vanish when their service is disabled; step 1 brings them back
        if ($c.ConfigManagerErrorCode -ne 22 -or $o.ProblemCode -eq 22) { continue }
        $disabled++
        $differences++
        if (-not $Apply) { Write-Host "  DISABLED  $($o.InstanceId) -> would enable"; continue }
        try { Enable-PnpDevice -InstanceId $o.InstanceId -Confirm:$false; Write-Host "  enabled   $($o.InstanceId)" }
        catch { $failures++; Write-Warning "enable $($o.InstanceId) failed: $($_.Exception.Message)" }
    }
    if ($disabled -eq 0) { Write-Host '  ok        no device node disabled since the backup' }
}

# 3. Fast Startup --------------------------------------------------------------------------------
Write-Host "`nFast Startup"
$oldPower = Read-BackupJson 'power.json'
$powerKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power'
$curHiber = (Get-ItemProperty $powerKey -Name HiberbootEnabled -ErrorAction SilentlyContinue).HiberbootEnabled
if ($oldPower -and $null -ne $oldPower.HiberbootEnabled -and $curHiber -ne $oldPower.HiberbootEnabled) {
    $differences++
    if (-not $Apply) { Write-Host "  CHANGED   HiberbootEnabled $curHiber -> would set $($oldPower.HiberbootEnabled)" }
    else {
        try { Set-ItemProperty $powerKey -Name HiberbootEnabled -Value ([int]$oldPower.HiberbootEnabled) -Type DWord; Write-Host "  restored  HiberbootEnabled = $($oldPower.HiberbootEnabled)" }
        catch { $failures++; Write-Warning "HiberbootEnabled restore failed: $($_.Exception.Message)" }
    }
}
else { Write-Host "  ok        HiberbootEnabled = $curHiber" }

# 4. Optional registry merge ---------------------------------------------------------------------
if ($ImportRegistry) {
    $reg = Join-Path $Backup 'bthport-devices.reg'
    Write-Host "`nRegistry merge"
    if (-not (Test-Path $reg)) { $failures++; Write-Warning "$reg not found" }
    elseif (-not $Apply) { Write-Host "  would run: reg import `"$reg`"" }
    else {
        $ErrorActionPreference = 'Continue'
        $out = & reg.exe import $reg 2>&1
        if ($LASTEXITCODE -eq 0) { Write-Host "  imported  $reg" } else { $failures++; Write-Warning "reg import failed: $out" }
        $ErrorActionPreference = 'Stop'
    }
}

Write-Host ''
if ($Apply) {
    Write-Host "Done: $differences difference(s) handled, $failures failure(s)."
    Write-Host 'Reboot recommended.'
    if ($failures -gt 0) { exit 1 }
}
else {
    Write-Host "Dry run: $differences difference(s) from the backup."
    if ($differences -gt 0) { exit 2 }
}
