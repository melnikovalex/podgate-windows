using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PodGate.Core.Audio;

/// <summary>
/// IPolicyConfig is undocumented and still the only non-GUI way to set the default endpoint; every
/// audio switcher uses it. Only SetDefaultEndpoint is called, so the ten slots before it
/// exist purely to hold vtable positions and their signatures do not matter.
/// </summary>
[ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat();
    [PreserveSig] int GetDeviceFormat();
    [PreserveSig] int ResetDeviceFormat();
    [PreserveSig] int SetDeviceFormat();
    [PreserveSig] int GetProcessingPeriod();
    [PreserveSig] int SetProcessingPeriod();
    [PreserveSig] int GetShareMode();
    [PreserveSig] int SetShareMode();
    [PreserveSig] int GetPropertyValue();
    [PreserveSig] int SetPropertyValue();
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    [PreserveSig] int SetEndpointVisibility();
}

[ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
internal class CPolicyConfigClient
{
}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr iface);
    [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out int state);
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumerator
{
}

public enum AudioFlow
{
    Render = 0,
    Capture = 1,
}

public enum AudioRole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

/// <summary>Default-device switching. Per user session: this must never run in a service.</summary>
public static class AudioPolicy
{
    public static void SetDefault(string endpointId)
    {
        foreach (AudioRole role in Enum.GetValues<AudioRole>()) SetDefaultForRole(endpointId, role);
    }

    public static void SetDefaultForRole(string endpointId, AudioRole role)
    {
        var config = (IPolicyConfig)new CPolicyConfigClient();
        int hr = config.SetDefaultEndpoint(endpointId, (int)role);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
    }

    public static string? GetDefault(AudioFlow flow, AudioRole role)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        if (enumerator.GetDefaultAudioEndpoint((int)flow, (int)role, out IMMDevice? device) != 0 || device is null) return null;
        return device.GetId(out string id) == 0 ? id : null;
    }
}

/// <summary>
/// The documented way to disconnect or reconnect a Bluetooth audio endpoint, and what the Windows
/// sound settings use: KSPROPSETID_BtAudio one-shots sent to the endpoint's kernel-streaming filter
/// Unlike disabling a device node, this works while audio is streaming.
/// </summary>
public static class KsBtAudio
{
    private static readonly Guid KsPropSetIdBtAudio = new("7fa06c40-b8f6-4c7e-8556-e8c33a12e54d");
    private const uint IoctlKsProperty = 0x002F0003;
    private const uint KspropertyTypeGet = 0x00000001;
    private const uint Reconnect = 0;
    private const uint Disconnect = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct KSPROPERTY
    {
        public Guid Set;
        public uint Id;
        public uint Flags;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(IntPtr handle, uint code, ref KSPROPERTY input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private static IntPtr Open(string filterPath)
    {
        IntPtr handle = CreateFileW(filterPath, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }

    /// <summary>Opens and closes the filter without sending anything, to validate a path.</summary>
    public static void Probe(string filterPath) => CloseHandle(Open(filterPath));

    public static void OneShot(string filterPath, bool reconnect)
    {
        IntPtr handle = Open(filterPath);
        try
        {
            var property = new KSPROPERTY
            {
                Set = KsPropSetIdBtAudio,
                Id = reconnect ? Reconnect : Disconnect,
                Flags = KspropertyTypeGet,
            };
            if (!DeviceIoControl(handle, IoctlKsProperty, ref property, Marshal.SizeOf<KSPROPERTY>(), IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
