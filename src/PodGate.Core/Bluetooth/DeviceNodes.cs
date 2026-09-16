using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PodGate.Core.Bluetooth;

/// <summary>
/// The block mechanism (ADR-001): disable the AirPods' Bluetooth device node. The disabled flag lands in
/// the device's ConfigFlags and survives reboots, which is what stops Windows grabbing them at boot.
///
/// This uses the SetupDi property-change path (DIF_PROPERTYCHANGE with DICS_FLAG_GLOBAL), the same route
/// devcon and Disable-PnpDevice take.
/// </summary>
public static class DeviceNodes
{
    private const string EnumKey = @"SYSTEM\CurrentControlSet\Enum";
    private const uint DifPropertyChange = 0x12;
    private const uint DicsEnable = 1;
    private const uint DicsDisable = 2;
    // GLOBAL is the one that writes CONFIGFLAG_DISABLED into the device's ConfigFlags, which is what
    // survives a reboot. CONFIGSPECIFIC writes to the hardware-profile config instead and, with no
    // extra hardware profile, silently does nothing at all (it returns success and ConfigFlags stays
    // unchanged).
    private const uint DicsFlagGlobal = 1;
    private const int ConfigFlagDisabled = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_CLASSINSTALL_HEADER
    {
        public uint cbSize;
        public uint InstallFunction;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_PROPCHANGE_PARAMS
    {
        public SP_CLASSINSTALL_HEADER ClassInstallHeader;
        public uint StateChange;
        public uint Scope;
        public uint HwProfile;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiOpenDeviceInfoW(IntPtr deviceInfoSet, string deviceInstanceId, IntPtr hwndParent, uint flags, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiSetClassInstallParams(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref SP_PROPCHANGE_PARAMS classInstallParams, int size);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    /// <summary>Root device node instance id for a paired classic device, or null if it is not present.</summary>
    public static string? FindRootInstanceId(string address)
    {
        string wanted = BtNative.NormalizeAddress(address);
        using RegistryKey? deviceKey = Registry.LocalMachine.OpenSubKey($@"{EnumKey}\BTHENUM\DEV_{wanted}");
        string? instance = deviceKey?.GetSubKeyNames().FirstOrDefault();
        return instance is null ? null : $@"BTHENUM\DEV_{wanted}\{instance}";
    }

    /// <summary>True when the device node carries CONFIGFLAG_DISABLED, i.e. PodGate is blocking it.</summary>
    public static bool? IsBlocked(string address)
    {
        string wanted = BtNative.NormalizeAddress(address);
        using RegistryKey? deviceKey = Registry.LocalMachine.OpenSubKey($@"{EnumKey}\BTHENUM\DEV_{wanted}");
        if (deviceKey is null) return null;

        bool? blocked = null;
        foreach (string instance in deviceKey.GetSubKeyNames())
        {
            using RegistryKey? instanceKey = deviceKey.OpenSubKey(instance);
            if (instanceKey?.GetValue("ConfigFlags") is int flags) blocked = (flags & ConfigFlagDisabled) == ConfigFlagDisabled;
        }
        return blocked;
    }

    /// <summary>
    /// DEVPKEY_Device_ContainerId for the device node, read from the Enum key. Everything audio-side is
    /// matched on this: the Hands-Free nodes do not carry the Bluetooth address at all, so the container
    /// is the only honest link between a paired device and its endpoints.
    /// </summary>
    public static Guid? GetContainerId(string address)
    {
        string? instanceId = FindRootInstanceId(address);
        return instanceId is null ? null : CfgMgr.GetContainerId(instanceId);
    }

    public static int? ReadConfigFlags(string instanceId)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"{EnumKey}\{instanceId}");
        return key?.GetValue("ConfigFlags") as int?;
    }

    /// <summary>
    /// Changes system state; needs elevation. Verifies the result rather than trusting the API: a
    /// property change can return success and change nothing. Falls back to the Configuration Manager
    /// if SetupDi does not stick, and throws if neither works.
    /// </summary>
    public static string SetEnabled(string instanceId, bool enable)
    {
        SetEnabledViaSetupDi(instanceId, enable);
        if (IsInDesiredState(instanceId, enable)) return "SetupDi";

        SetEnabledViaCfgMgr(instanceId, enable);
        if (IsInDesiredState(instanceId, enable)) return "CfgMgr";

        throw new InvalidOperationException(
            $"Neither SetupDi nor the Configuration Manager changed {instanceId}: ConfigFlags is still 0x{ReadConfigFlags(instanceId):X}");
    }

    private static bool IsInDesiredState(string instanceId, bool enable)
    {
        int flags = ReadConfigFlags(instanceId) ?? 0;
        bool disabled = (flags & ConfigFlagDisabled) == ConfigFlagDisabled;
        return disabled != enable;
    }

    private static void SetEnabledViaCfgMgr(string instanceId, bool enable)
    {
        uint? devInst = CfgMgr.LocateDevNode(instanceId);
        if (devInst is null) throw new InvalidOperationException($"Device not found: {instanceId}");
        CfgMgr.SetDevNodeEnabled(devInst.Value, enable);
    }

    private static void SetEnabledViaSetupDi(string instanceId, bool enable) =>
        CallPropertyChange(instanceId, enable ? DicsEnable : DicsDisable);

    private static void CallPropertyChange(string instanceId, uint stateChange)
    {
        IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Device not found: {instanceId}");
            }

            var change = new SP_PROPCHANGE_PARAMS
            {
                ClassInstallHeader = new SP_CLASSINSTALL_HEADER
                {
                    cbSize = (uint)Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(),
                    InstallFunction = DifPropertyChange,
                },
                StateChange = stateChange,
                Scope = DicsFlagGlobal,
                HwProfile = 0,
            };

            if (!SetupDiSetClassInstallParams(set, ref info, ref change, Marshal.SizeOf<SP_PROPCHANGE_PARAMS>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (!SetupDiCallClassInstaller(DifPropertyChange, set, ref info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }
}
