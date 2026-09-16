using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PodGate.Core.Bluetooth;

/// <summary>One paired classic Bluetooth device. "Installed" services are the enabled ones.</summary>
public sealed class BtDevice
{
    public required string Address { get; init; }      // 12 upper-case hex digits, no separators
    public required string Name { get; init; }
    public bool Connected { get; init; }
    public bool Remembered { get; init; }
    public bool Authenticated { get; init; }
    public uint ClassOfDevice { get; init; }
    /// <summary>When the radio last heard this device. The honest answer to "are they even in range?".</summary>
    public DateTime? LastSeen { get; init; }
    public IReadOnlyList<string> InstalledServices { get; init; } = [];
    /// <summary>Major device class "Audio/Video" in the Class of Device: headphones, speakers, headsets.</summary>
    public bool IsAudio => ((ClassOfDevice >> 8) & 0x1F) == 0x04;
    public uint EnumerateServicesResult { get; init; }  // Win32 code, 0 = OK
}

/// <summary>
/// BluetoothAPIs (bthprops.cpl): paired devices, their services, and per-service enable/disable.
/// </summary>
public static class BtNative
{
    private const int ErrorNoMoreItems = 259;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BLUETOOTH_DEVICE_INFO
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
    private struct BLUETOOTH_DEVICE_SEARCH_PARAMS
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
    private struct BLUETOOTH_FIND_RADIO_PARAMS
    {
        public uint dwSize;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS p, ref BLUETOOTH_DEVICE_INFO info);

    [DllImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO info);

    [DllImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindDeviceClose(IntPtr hFind);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern uint BluetoothEnumerateInstalledServices(IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO info, ref uint count, [Out] Guid[] services);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS p, out IntPtr hRadio);

    [DllImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindRadioClose(IntPtr hFind);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern uint BluetoothSetServiceState(IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO info, ref Guid service, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private static BLUETOOTH_DEVICE_INFO NewInfo() =>
        new() { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>() };

    private static List<BLUETOOTH_DEVICE_INFO> FindPaired()
    {
        var result = new List<BLUETOOTH_DEVICE_INFO>();
        var search = new BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
            fReturnAuthenticated = true,
            fReturnRemembered = true,
            fReturnConnected = true,
        };

        var info = NewInfo();
        IntPtr find = BluetoothFindFirstDevice(ref search, ref info);
        if (find == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorNoMoreItems) return result;
            throw new Win32Exception(error);
        }

        try
        {
            do
            {
                result.Add(info);
                info = NewInfo();
            }
            while (BluetoothFindNextDevice(find, ref info));
        }
        finally
        {
            BluetoothFindDeviceClose(find);
        }

        return result;
    }

    public static string FormatAddress(ulong address) => address.ToString("X12");

    private static DateTime? ToDateTime(SYSTEMTIME time)
    {
        if (time.Year == 0) return null;
        try
        {
            // The radio reports UTC here.
            return new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, time.Second, DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static IReadOnlyList<BtDevice> GetPairedDevices()
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
            {
                for (int i = 0; i < count; i++) services.Add(guids[i].ToString().ToLowerInvariant());
            }

            devices.Add(new BtDevice
            {
                Address = FormatAddress(info.Address),
                LastSeen = ToDateTime(info.stLastSeen),
                Name = info.szName,
                Connected = info.fConnected,
                Remembered = info.fRemembered,
                Authenticated = info.fAuthenticated,
                ClassOfDevice = info.ulClassofDevice,
                InstalledServices = services,
                EnumerateServicesResult = rc,
            });
        }
        return devices;
    }

    public static BtDevice? FindPairedDevice(string address)
    {
        string wanted = NormalizeAddress(address);
        return GetPairedDevices().FirstOrDefault(d => d.Address == wanted);
    }

    /// <summary>12 upper-case hex digits, separators of any kind removed.</summary>
    public static string NormalizeAddress(string value)
    {
        Span<char> hex = stackalloc char[12];
        int length = 0;
        foreach (char c in value)
        {
            if (!Uri.IsHexDigit(c)) continue;
            if (length == 12) throw new ArgumentException($"Not a Bluetooth address: '{value}'", nameof(value));
            hex[length++] = char.ToUpperInvariant(c);
        }
        if (length != 12) throw new ArgumentException($"Not a Bluetooth address: '{value}'", nameof(value));
        return new string(hex);
    }

    /// <summary>
    /// Changes system state; needs elevation. Kept because it is the M2 fallback and the restore path,
    /// even though ADR-001 blocks by device node instead.
    /// </summary>
    public static uint SetServiceState(string address, Guid service, bool enable)
    {
        string wanted = NormalizeAddress(address);
        BLUETOOTH_DEVICE_INFO info = default;
        bool found = false;
        foreach (var raw in FindPaired())
        {
            if (FormatAddress(raw.Address) != wanted) continue;
            info = raw;
            found = true;
            break;
        }
        if (!found) throw new ArgumentException($"Paired device not found: {address}", nameof(address));

        var radioParams = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
        IntPtr findRadio = BluetoothFindFirstRadio(ref radioParams, out IntPtr radio);
        if (findRadio == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            return BluetoothSetServiceState(radio, ref info, ref service, enable ? 1u : 0u);
        }
        finally
        {
            CloseHandle(radio);
            BluetoothFindRadioClose(findRadio);
        }
    }
}
