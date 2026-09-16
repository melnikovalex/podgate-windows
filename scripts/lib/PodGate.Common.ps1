# PodGate.Common.ps1 - shared helpers for PodGate scripts. Dot-source it.
# Windows PowerShell 5.1 compatible. Keep this file ASCII-only (5.1 reads BOM-less files as ANSI).
# Everything here is read-only except Set-BtServiceState, which only restore-bt.ps1 calls.
# These scripts are the escape hatch that must keep working when the app itself is broken.

$script:RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:BthportDevicesKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices'
$script:MMDevicesKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio'
$script:SigBaseSuffix = '-0000-1000-8000-00805f9b34fb'

if (-not ('PodGate.BtNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PodGate
{
    public class BtDevice
    {
        public string Address;               // 12 upper-case hex digits
        public string Name;
        public bool Connected;
        public bool Remembered;
        public bool Authenticated;
        public uint ClassOfDevice;
        public string[] InstalledServices;   // lower-case GUIDs; installed == enabled
        public uint EnumerateServicesResult; // Win32 error code, 0 = OK
    }

    public static class BtNative
    {
        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEMTIME { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct BLUETOOTH_DEVICE_INFO
        {
            public uint dwSize;
            public ulong Address;
            public uint ulClassofDevice;
            [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
            [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
            [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
            public SYSTEMTIME stLastSeen;
            public SYSTEMTIME stLastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            public uint dwSize;
            [MarshalAs(UnmanagedType.Bool)] public bool fReturnAuthenticated;
            [MarshalAs(UnmanagedType.Bool)] public bool fReturnRemembered;
            [MarshalAs(UnmanagedType.Bool)] public bool fReturnUnknown;
            [MarshalAs(UnmanagedType.Bool)] public bool fReturnConnected;
            [MarshalAs(UnmanagedType.Bool)] public bool fIssueInquiry;
            public byte cTimeoutMultiplier;
            public IntPtr hRadio;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BLUETOOTH_FIND_RADIO_PARAMS { public uint dwSize; }

        const int ERROR_NO_MORE_ITEMS = 259;

        [DllImport("bthprops.cpl", SetLastError = true)]
        static extern IntPtr BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS p, ref BLUETOOTH_DEVICE_INFO info);
        [DllImport("bthprops.cpl", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO info);
        [DllImport("bthprops.cpl", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool BluetoothFindDeviceClose(IntPtr hFind);
        [DllImport("bthprops.cpl", SetLastError = true)]
        static extern uint BluetoothEnumerateInstalledServices(IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO info, ref uint count, [Out] Guid[] services);
        [DllImport("bthprops.cpl", SetLastError = true)]
        static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS p, out IntPtr hRadio);
        [DllImport("bthprops.cpl", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool BluetoothFindRadioClose(IntPtr hFind);
        [DllImport("bthprops.cpl", SetLastError = true)]
        static extern uint BluetoothSetServiceState(IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO info, ref Guid service, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr handle);

        static BLUETOOTH_DEVICE_INFO NewInfo()
        {
            var info = new BLUETOOTH_DEVICE_INFO();
            info.dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_INFO));
            return info;
        }

        static List<BLUETOOTH_DEVICE_INFO> FindPaired()
        {
            var result = new List<BLUETOOTH_DEVICE_INFO>();
            var p = new BLUETOOTH_DEVICE_SEARCH_PARAMS();
            p.dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_SEARCH_PARAMS));
            p.fReturnAuthenticated = true;
            p.fReturnRemembered = true;
            p.fReturnConnected = true;
            var info = NewInfo();
            IntPtr hFind = BluetoothFindFirstDevice(ref p, ref info);
            if (hFind == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_NO_MORE_ITEMS) return result;
                throw new Win32Exception(err);
            }
            try
            {
                do
                {
                    result.Add(info);
                    info = NewInfo();
                } while (BluetoothFindNextDevice(hFind, ref info));
            }
            finally { BluetoothFindDeviceClose(hFind); }
            return result;
        }

        static string Hex(ulong address) { return address.ToString("X12"); }

        public static List<BtDevice> GetPairedDevices()
        {
            var devices = new List<BtDevice>();
            foreach (var raw in FindPaired())
            {
                var info = raw;
                uint count = 64;
                var guids = new Guid[64];
                uint rc = BluetoothEnumerateInstalledServices(IntPtr.Zero, ref info, ref count, guids);
                var services = new List<string>();
                if (rc == 0)
                    for (int i = 0; i < count; i++) services.Add(guids[i].ToString().ToLowerInvariant());
                var d = new BtDevice();
                d.Address = Hex(info.Address);
                d.Name = info.szName;
                d.Connected = info.fConnected;
                d.Remembered = info.fRemembered;
                d.Authenticated = info.fAuthenticated;
                d.ClassOfDevice = info.ulClassofDevice;
                d.InstalledServices = services.ToArray();
                d.EnumerateServicesResult = rc;
                devices.Add(d);
            }
            return devices;
        }

        // Changes system state. Returns the Win32 result code (0 = success).
        public static uint SetServiceState(string address, Guid service, bool enable)
        {
            BLUETOOTH_DEVICE_INFO info = NewInfo();
            bool found = false;
            foreach (var raw in FindPaired())
                if (Hex(raw.Address) == address.ToUpperInvariant()) { info = raw; found = true; break; }
            if (!found) throw new ArgumentException("Paired device not found: " + address);

            var rp = new BLUETOOTH_FIND_RADIO_PARAMS();
            rp.dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_FIND_RADIO_PARAMS));
            IntPtr hRadio;
            IntPtr hFind = BluetoothFindFirstRadio(ref rp, out hRadio);
            if (hFind == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { return BluetoothSetServiceState(hRadio, ref info, ref service, enable ? 1u : 0u); }
            finally { CloseHandle(hRadio); BluetoothFindRadioClose(hFind); }
        }
    }
}
'@
}

$script:BtServiceNames = @{
    '00001000' = 'Service Discovery Server'
    '0000110a' = 'A2DP Audio Source'
    '0000110b' = 'A2DP Audio Sink'
    '0000110c' = 'AVRCP Target'
    '0000110e' = 'AVRCP'
    '00001108' = 'Headset'
    '00001112' = 'Headset Audio Gateway'
    '0000111e' = 'Hands-Free'
    '0000111f' = 'Hands-Free Audio Gateway'
    '00001800' = 'GAP'
    '00001801' = 'GATT'
}

function ConvertTo-BtAddress {
    param([Parameter(Mandatory = $true)][string]$Value)
    $hex = ($Value -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    if ($hex.Length -ne 12) { throw "Not a Bluetooth address: '$Value'" }
    $hex
}

function Get-BtServiceShortName {
    param([Parameter(Mandatory = $true)][string]$Guid)
    $g = $Guid.Trim('{}').ToLowerInvariant()
    if ($g.EndsWith($script:SigBaseSuffix) -and $g.StartsWith('0000')) { return $g.Substring(4, 4) }
    $g.Substring(0, 8)
}

function Get-BtServiceName {
    param([Parameter(Mandatory = $true)][string]$Guid)
    $g = $Guid.Trim('{}').ToLowerInvariant()
    $key = $g.Substring(0, 8)
    if ($g.EndsWith($script:SigBaseSuffix) -and $script:BtServiceNames.ContainsKey($key)) { return $script:BtServiceNames[$key] }
    if ($g.EndsWith($script:SigBaseSuffix)) { return 'SIG service' }
    'vendor-specific'
}

function Test-PodGateElevated {
    $p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-PodGateTimestamp { (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz') }

# Address from -Address, then %ProgramData%\PodGate\config.json, then the only paired Apple (company ID 0x004C) device.
function Resolve-PodGateAddress {
    param([string]$Address)
    if ($Address) { return ConvertTo-BtAddress $Address }

    # A remembered address is only worth using if that device is actually paired here: a config copied
    # from another PC must not point at a device that does not exist.
    $remembered = @()
    if (Test-Path $script:PodGateConfigPath) {
        try {
            $stored = Get-Content $script:PodGateConfigPath -Raw | ConvertFrom-Json
            if ($stored.address) { $remembered += ConvertTo-BtAddress $stored.address }
        }
        catch { }
    }
    if ($remembered.Count -gt 0) {
        $paired = @(Get-BtPairedDevice | ForEach-Object { $_.Address })
        foreach ($candidate in $remembered) {
            if ($paired -contains $candidate) { return $candidate }
        }
    }
    $apple = @(Get-ChildItem $script:BthportDevicesKey | Where-Object { $_.GetValue('VID') -eq 76 } |
        ForEach-Object { $_.PSChildName.ToUpperInvariant() })
    if ($apple.Count -eq 1) { return $apple[0] }
    throw "Can't pick the AirPods automatically (Apple devices: $($apple -join ', ')). Pass -Address."
}

function Get-BtPairedDevice {
    param([string]$Address)
    $all = @([PodGate.BtNative]::GetPairedDevices())
    if (-not $Address) { return $all }
    $a = ConvertTo-BtAddress $Address
    @($all | Where-Object { $_.Address -eq $a })
}

# Per-service Enabled flags persisted under BTHPORT. Disabled services stay listed here
# even though BluetoothEnumerateInstalledServices no longer returns them.
function Get-BtRegistryServiceState {
    param([Parameter(Mandatory = $true)][string]$Address)
    $key = Join-Path $script:BthportDevicesKey (ConvertTo-BtAddress $Address).ToLowerInvariant()
    if (-not (Test-Path $key)) { return }
    foreach ($radio in @(Get-ChildItem $key | Where-Object { $_.PSChildName -like 'ServicesFor*' })) {
        foreach ($svc in @(Get-ChildItem $radio.PSPath)) {
            foreach ($inst in @(Get-ChildItem $svc.PSPath)) {
                [pscustomobject]@{
                    Guid     = $svc.PSChildName.Trim('{}').ToLowerInvariant()
                    Instance = $inst.PSChildName
                    Enabled  = $inst.GetValue('Enabled')
                    RadioKey = $radio.PSChildName
                }
            }
        }
    }
}

function Get-PnpContainerMatch {
    param([object[]]$Devices, [Parameter(Mandatory = $true)][string]$ContainerId)
    if (-not $Devices) { return }
    $ids = @(Get-PnpDeviceProperty -InstanceId @($Devices | ForEach-Object { $_.InstanceId }) -KeyName DEVPKEY_Device_ContainerId -ErrorAction SilentlyContinue |
        Where-Object { $_.PSObject.Properties['Data'] -and "$($_.Data)" -eq $ContainerId } | ForEach-Object { $_.InstanceId })
    @($Devices | Where-Object { $ids -contains $_.InstanceId })
}

function ConvertTo-EndpointStateName {
    param($State)
    if ($null -eq $State) { return $null }
    switch ([int]$State -band 0xF) { 1 { 'ACTIVE' } 2 { 'DISABLED' } 4 { 'NOTPRESENT' } 8 { 'UNPLUGGED' } default { "0x{0:X}" -f [int]$State } }
}

# Reads MMDevices directly: unplugged endpoints have no PnP device node, so Get-PnpDevice misses them.
function Get-BtAudioEndpoint {
    param([Parameter(Mandatory = $true)][string]$ContainerId)
    $want = [Guid]$ContainerId
    foreach ($flow in 'Render', 'Capture') {
        foreach ($k in @(Get-ChildItem (Join-Path $script:MMDevicesKey $flow) -ErrorAction SilentlyContinue)) {
            $props = Get-Item (Join-Path $k.PSPath 'Properties') -ErrorAction SilentlyContinue
            if (-not $props) { continue }
            # PKEY_Device_ContainerId, stored as an 8-byte header + 16-byte GUID
            $raw = $props.GetValue('{8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c},2')
            if (-not ($raw -is [byte[]]) -or $raw.Length -ne 24) { continue }
            $guidBytes = New-Object byte[] 16
            [Array]::Copy($raw, 8, $guidBytes, 0, 16)
            if ((New-Object Guid (, $guidBytes)) -ne $want) { continue }
            $state = $k.GetValue('DeviceState')
            # Path of the kernel-streaming filter behind this endpoint, stored with a "{2}." prefix.
            $filter = "$($props.GetValue('{233164c8-1b2c-4c7d-bc68-b671687a2567},1'))" -replace '^\{\d+\}\.', ''
            # Which profile the endpoint belongs to, taken from the device instance path, never from the
            # (localized) friendly name: A2DP endpoints sit under BTHENUM\{0000110b...}, Hands-Free under BTHHFENUM.
            $path = "$($props.GetValue('{b3f8fa53-0004-438e-9003-51a46e139bfc},2'))"
            $transport = 'other'
            if ($path -match '0000110b') { $transport = 'A2DP' }
            elseif ($path -match 'BTHHFENUM|0000111e') { $transport = 'HandsFree' }
            [pscustomobject]@{
                Flow         = $flow
                State        = ConvertTo-EndpointStateName $state
                Transport    = $transport
                FilterPath   = $filter
                FriendlyName = "$($props.GetValue('{a45c254e-df1c-4efd-8020-67d146a850e0},2')) ($($props.GetValue('{b3f8fa53-0004-438e-9003-51a46e139bfc},6')))"
                EndpointId   = "{0.0.$([int]($flow -eq 'Capture')).00000000}.$($k.PSChildName)"
            }
        }
    }
}

$script:PodGateDataDir = Join-Path $env:ProgramData 'PodGate'
$script:PodGateConfigPath = Join-Path $script:PodGateDataDir 'config.json'

function Get-PodGateBootInfo {
    $os = Get-CimInstance Win32_OperatingSystem
    $hiber = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' -Name HiberbootEnabled -ErrorAction SilentlyContinue).HiberbootEnabled
    $boot = Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Kernel-Boot'; Id = 27 } -MaxEvents 1 -ErrorAction SilentlyContinue
    $bootType = $null
    if ($boot) { $bootType = [int]$boot.Properties[0].Value }
    $wake = Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Kernel-Power'; Id = 507 } -MaxEvents 1 -ErrorAction SilentlyContinue
    [pscustomobject]@{
        LastBootUpTime       = $os.LastBootUpTime.ToString('s')
        BootType             = $bootType
        BootTypeName         = $(switch ($bootType) { 0 { 'cold' } 1 { 'fast-startup' } 2 { 'hibernate-resume' } default { $null } })
        LastStandbyExit      = $(if ($wake) { $wake.TimeCreated.ToString('s') } else { $null })
        FastStartupEnabled   = $hiber
    }
}

function Get-PodGateDeviceReport {
    param([string]$Address)
    $a = Resolve-PodGateAddress -Address $Address
    $dev = @(Get-BtPairedDevice -Address $a) | Select-Object -First 1
    $reg = @(Get-BtRegistryServiceState -Address $a)
    $installed = @()
    if ($dev) { $installed = @($dev.InstalledServices) }
    $guids = @(@($installed) + @($reg | ForEach-Object { $_.Guid }) | Sort-Object -Unique)

    $services = @(foreach ($g in $guids) {
        $r = @($reg | Where-Object { $_.Guid -eq $g })
        [pscustomobject]@{
            Short           = Get-BtServiceShortName $g
            Name            = Get-BtServiceName $g
            Installed       = ($installed -contains $g)
            RegistryEnabled = (@($r | ForEach-Object { $_.Enabled }) -join ',')
            Guid            = $g
        }
    })

    $allPnp = @(Get-PnpDevice -ErrorAction SilentlyContinue)
    $nodes = @($allPnp | Where-Object { $_.InstanceId -match $a })
    $root = $nodes | Where-Object { $_.InstanceId -like "BTHENUM\DEV_$a\*" } | Select-Object -First 1
    $containerId = $null
    $devnodeConnected = $null
    if ($root) {
        $containerId = "$((Get-PnpDeviceProperty -InstanceId $root.InstanceId -KeyName DEVPKEY_Device_ContainerId).Data)"
        $devnodeConnected = (Get-PnpDeviceProperty -InstanceId $root.InstanceId -KeyName '{83DA6326-97A6-4088-9453-A1923F573B29} 15' -ErrorAction SilentlyContinue).Data
    }
    $endpoints = @()
    if ($containerId) {
        # Hands-Free audio nodes (BTHHFENUM) don't carry the address in their instance ID.
        $nodes += @(Get-PnpContainerMatch -Devices @($allPnp | Where-Object { $_.Class -eq 'MEDIA' -and $_.InstanceId -notmatch $a }) -ContainerId $containerId)
        $endpoints = @(Get-BtAudioEndpoint -ContainerId $containerId)
    }

    $sink = @($services | Where-Object { $_.Short -eq '110b' })
    $hf = @($services | Where-Object { $_.Short -eq '111e' })
    [pscustomobject]@{
        Timestamp        = Get-PodGateTimestamp
        Address          = $a
        Name             = $(if ($dev) { $dev.Name } else { $null })
        Paired           = [bool]$dev
        Connected        = $(if ($dev) { $dev.Connected } else { $null })
        DevnodeConnected = $devnodeConnected
        ContainerId      = $containerId
        AudioSinkEnabled = $(if ($sink) { $sink[0].Installed } else { $false })
        HandsFreeEnabled = $(if ($hf) { $hf[0].Installed } else { $false })
        ActiveEndpoints  = @($endpoints | Where-Object { $_.State -eq 'ACTIVE' }).Count
        Services         = $services
        RootNodeId       = $(if ($root) { $root.InstanceId } else { $null })
        Blocked          = $(if ($root) { $root.ConfigManagerErrorCode -eq 22 } else { $null })
        DeviceNodes      = @($nodes | ForEach-Object {
            [pscustomobject]@{ Class = $_.Class; Status = "$($_.Status)"; Present = $_.Present; ProblemCode = $_.ConfigManagerErrorCode; FriendlyName = $_.FriendlyName; InstanceId = $_.InstanceId }
        })
        AudioEndpoints   = $endpoints
        Boot             = Get-PodGateBootInfo
    }
}
