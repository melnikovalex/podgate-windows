<#
.SYNOPSIS
  Snapshot Bluetooth, audio endpoint, power and companion-app settings before any change.

.DESCRIPTION
  Changes nothing on the system; it only writes files under backups\<timestamp>[-label]\.
  Run it before changing Bluetooth devices, services, audio or power settings, elevated when possible.

  Always (unelevated is fine):
    device-report.json     AirPods services, device nodes, audio endpoints, boot info
    bt-paired.json         every paired classic device with its installed (enabled) services
    bt-devnodes.json       every Bluetooth-related device node with status and service
    bthport-devices.reg    HKLM\...\BTHPORT\Parameters\Devices (no link keys)
    mmdevices-audio.reg    HKLM\...\MMDevices\Audio (endpoint states and properties)
    power.json             Fast Startup flag, sleep states, lid/button settings (raw powercfg text)
    services.json          Bluetooth and audio Windows services (status, start type)
    magicpods\             copy of MagicPods' settings.dat, if installed
  Elevated only:
    bthport-parameters.secret.reg   full BTHPORT\Parameters including pairing link keys (git-ignored)
  Plus manifest.json listing files, errors, Windows build and elevation.

.PARAMETER Label
  Short reason, for example "before-driver-update". Appended to the folder name.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\backup-bt.ps1 -Label before-driver-update
#>
[CmdletBinding()]
param(
    [string]$Label,
    [string]$Address
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\PodGate.Common.ps1')

$folder = Get-Date -Format 'yyyyMMdd-HHmmss'
if ($Label) { $folder += '-' + ($Label -replace '[^A-Za-z0-9_-]', '_') }
$dir = Join-Path $script:RepoRoot "backups\$folder"
New-Item -ItemType Directory -Path $dir -Force | Out-Null

$elevated = Test-PodGateElevated
$files = New-Object System.Collections.Generic.List[string]
$errors = New-Object System.Collections.Generic.List[string]

function Save-Item {
    param([string]$File, [scriptblock]$Action)
    $path = Join-Path $dir $File
    try {
        & $Action $path
        $files.Add($File)
        Write-Host "  ok    $File"
    }
    catch {
        $errors.Add("${File}: $($_.Exception.Message)")
        Write-Warning "$File failed: $($_.Exception.Message)"
    }
}

function Save-Json {
    param([string]$Path, $Object)
    $Object | ConvertTo-Json -Depth 6 | Set-Content -Path $Path -Encoding UTF8
}

function Export-RegKey {
    param([string]$Key, [string]$Path)
    # reg.exe writes errors to stderr; with 'Stop' Windows PowerShell 5.1 would turn that into a terminating error.
    $ErrorActionPreference = 'Continue'
    $out = & reg.exe export $Key $Path /y 2>&1
    if ($LASTEXITCODE -ne 0) { throw "reg export $Key failed (exit $LASTEXITCODE): $out" }
}

Write-Host "Backup -> $dir  (elevated: $elevated)"

Save-Item 'device-report.json' { param($p) Save-Json $p (Get-PodGateDeviceReport -Address $Address) }
Save-Item 'bt-paired.json' { param($p) Save-Json $p @(Get-BtPairedDevice) }

Save-Item 'bt-devnodes.json' {
    param($p)
    $nodes = @(Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
        $_.Class -eq 'Bluetooth' -or $_.InstanceId -match '^(BTH|BTHENUM|BTHLE|BTHLEDEVICE|BTHHFENUM)\\'
    })
    $svc = @{}
    if ($nodes) {
        Get-PnpDeviceProperty -InstanceId @($nodes | ForEach-Object { $_.InstanceId }) -KeyName DEVPKEY_Device_Service -ErrorAction SilentlyContinue |
            Where-Object { $_.PSObject.Properties['Data'] } | ForEach-Object { $svc[$_.InstanceId] = "$($_.Data)" }
    }
    Save-Json $p @($nodes | ForEach-Object {
        [pscustomobject]@{
            InstanceId   = $_.InstanceId
            Class        = $_.Class
            Status       = "$($_.Status)"
            Present      = $_.Present
            Service      = $svc[$_.InstanceId]
            ProblemCode  = $_.ConfigManagerErrorCode
            FriendlyName = $_.FriendlyName
        }
    })
}

Save-Item 'bthport-devices.reg' { param($p) Export-RegKey 'HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices' $p }
Save-Item 'mmdevices-audio.reg' { param($p) Export-RegKey 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio' $p }

if ($elevated) {
    Save-Item 'bthport-parameters.secret.reg' { param($p) Export-RegKey 'HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters' $p }
}
else {
    Write-Host "  skip  bthport-parameters.secret.reg (needs elevation)"
}

Save-Item 'power.json' {
    param($p)
    $ErrorActionPreference = 'Continue'
    Save-Json $p ([pscustomobject]@{
        HiberbootEnabled = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' -Name HiberbootEnabled -ErrorAction SilentlyContinue).HiberbootEnabled
        ActiveScheme     = (& powercfg.exe /getactivescheme | Out-String).Trim()
        SleepStates      = (& powercfg.exe /a | Out-String).Trim()
        ButtonsAndLid    = (& powercfg.exe /q SCHEME_CURRENT SUB_BUTTONS | Out-String).Trim()
    })
}

Save-Item 'services.json' {
    param($p)
    Save-Json $p @(Get-Service -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^(bthserv|BTAGService|BthAvctpSvc|BluetoothUserService|DeviceAssociationService|Audiosrv|AudioEndpointBuilder)' } |
        ForEach-Object { [pscustomobject]@{ Name = $_.Name; Status = "$($_.Status)"; StartType = "$($_.StartType)" } })
}

$mp = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter '*MagicPods*' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($mp) {
    Save-Item 'magicpods' {
        param($p)
        New-Item -ItemType Directory -Path $p -Force | Out-Null
        Copy-Item (Join-Path $mp.FullName 'Settings\*') $p -Force
        $pkg = Get-AppxPackage -Name '*MagicPods*' -ErrorAction SilentlyContinue | Select-Object -First 1
        Save-Json (Join-Path $p 'package.json') ([pscustomobject]@{ Name = $pkg.Name; Version = "$($pkg.Version)"; SettingsFrom = $mp.FullName })
    }
}

$cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
Save-Json (Join-Path $dir 'manifest.json') ([pscustomobject]@{
    Timestamp = Get-PodGateTimestamp
    Label     = $Label
    Computer  = $env:COMPUTERNAME
    Windows   = "$($cv.DisplayVersion) build $($cv.CurrentBuild).$($cv.UBR)"
    Elevated  = $elevated
    Files     = $files
    Errors    = $errors
})

Write-Host ""
Write-Host "Done: $($files.Count) item(s), $($errors.Count) error(s)."
Write-Host "Backup written to backups\$folder"
if ($errors.Count -gt 0) { exit 1 }
