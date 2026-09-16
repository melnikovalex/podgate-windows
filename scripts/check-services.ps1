<#
.SYNOPSIS
  Read-only report: the AirPods' Bluetooth services, device nodes, audio endpoints and connection state.

.DESCRIPTION
  Changes nothing; safe to run at any time, unelevated.
  "Installed" comes from BluetoothEnumerateInstalledServices (installed == enabled).
  "RegEnabled" is the per-service Enabled flag under BTHPORT, which also lists disabled services.
  Audio endpoints are matched by container ID, never by name.

.PARAMETER Address
  Bluetooth address (AABBCCDDEEFF or AA:BB:CC:DD:EE:FF). Default: the configured or only paired Apple device.

.PARAMETER AsJson
  Emit one JSON object instead of tables.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check-services.ps1
#>
[CmdletBinding()]
param(
    [string]$Address,
    [switch]$AsJson
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\PodGate.Common.ps1')

$r = Get-PodGateDeviceReport -Address $Address

if ($AsJson) {
    $r | ConvertTo-Json -Depth 6
    return
}

Write-Host ("{0}  {1} [{2}]" -f $r.Timestamp, $r.Name, $r.Address)
Write-Host ("Paired: {0}   Connected (API): {1}   Connected (devnode): {2}   Container: {3}" -f $r.Paired, $r.Connected, $r.DevnodeConnected, $r.ContainerId)
Write-Host ("Audio Sink enabled: {0}   Hands-Free enabled: {1}   Active audio endpoints: {2}" -f $r.AudioSinkEnabled, $r.HandsFreeEnabled, $r.ActiveEndpoints)
Write-Host ("Boot: {0} ({1})   Last standby exit: {2}   Fast Startup: {3}" -f $r.Boot.LastBootUpTime, $r.Boot.BootTypeName, $r.Boot.LastStandbyExit, $r.Boot.FastStartupEnabled)

Write-Host "`nServices"
$r.Services | Format-Table -AutoSize Short, Name, Installed, @{ n = 'RegEnabled'; e = { $_.RegistryEnabled } }, Guid | Out-String -Width 200 | Write-Host

Write-Host "Device nodes"
$r.DeviceNodes | Sort-Object Class, InstanceId | Format-Table -AutoSize Class, Status, Present, FriendlyName | Out-String -Width 200 | Write-Host

Write-Host "Audio endpoints"
if ($r.AudioEndpoints.Count -eq 0) { Write-Host "  (none with this container ID)`n" }
else { $r.AudioEndpoints | Format-Table -AutoSize Flow, State, FriendlyName, EndpointId | Out-String -Width 200 | Write-Host }
