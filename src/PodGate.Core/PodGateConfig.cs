using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using PodGate.Core.Bluetooth;

namespace PodGate.Core;

public static class Paths
{
    /// <summary>Machine state, admin-writable only: the service reads its target device from here.</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PodGate");

    /// <summary>
    /// Per-user state the unelevated app owns. Which device was your default output is a per-user fact
    /// anyway, and the app cannot write to the machine folder by design.
    /// </summary>
    public static string UserDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PodGate");

    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string BackupsDir => Path.Combine(DataDir, "backups");
    public static string StateFile => Path.Combine(UserDataDir, "state.json");

    public static string LogFile(string component) =>
        component == "service" ? Path.Combine(DataDir, "service.log") : Path.Combine(UserDataDir, $"{component}.log");
}

public sealed class HotkeyConfig
{
    public string Toggle { get; set; } = "Ctrl+Alt+Shift+A";
    public string Connect { get; set; } = "";
    public string Release { get; set; } = "";
    public string ConnectMusic { get; set; } = "Ctrl+Alt+Shift+S";
}

public enum ConnectMode
{
    Full,
    Music,
}

public enum MicRoles
{
    Communications,
    All,
    None,
}

/// <summary>
/// Machine configuration. The schema stays strictly additive, because the diagnostic scripts read it too:
/// they are the escape hatch when the app is broken. Lives under %ProgramData% and is admin-writable only,
/// because the elevated action takes its target device from it and must never be steered by an
/// unelevated caller.
/// </summary>
public sealed class PodGateConfig
{
    public string Version { get; set; } = "0.0.0";
    public string Address { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public MicRoles MicRoles { get; set; } = MicRoles.Communications;
    public ConnectMode Mode { get; set; } = ConnectMode.Full;
    public HotkeyConfig Hotkeys { get; set; } = new();
    public bool LowBatteryWarnings { get; set; } = true;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static PodGateConfig Load()
    {
        if (!File.Exists(Paths.ConfigFile)) return new PodGateConfig();
        try
        {
            return JsonSerializer.Deserialize<PodGateConfig>(File.ReadAllText(Paths.ConfigFile), Options) ?? new PodGateConfig();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new PodGateConfig();
        }
    }

    /// <summary>Needs elevation, by design.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Paths.DataDir);
        File.WriteAllText(Paths.ConfigFile, JsonSerializer.Serialize(this, Options));
    }

    /// <summary>
    /// Address from the argument, then config, then the only paired Apple device. A remembered address
    /// is only accepted when that device is actually paired here, so a config copied from another
    /// machine cannot point the tool at a device that does not exist. Apple devices are recognised by
    /// company id 0x004C, never by name: names are user-editable and localized.
    /// </summary>
    public static string ResolveAddress(string? preferred = null)
    {
        if (!string.IsNullOrWhiteSpace(preferred)) return BtNative.NormalizeAddress(preferred);

        var paired = BtNative.GetPairedDevices();
        string remembered = Load().Address;
        if (!string.IsNullOrWhiteSpace(remembered))
        {
            string candidate = BtNative.NormalizeAddress(remembered);
            if (paired.Any(d => d.Address == candidate)) return candidate;
        }

        var apple = paired.Where(d => IsApple(d.Address)).Select(d => d.Address).ToList();
        if (apple.Count == 1) return apple[0];

        string known = string.Join(", ", paired.Select(d => $"{d.Name} [{d.Address}]"));
        throw new InvalidOperationException(
            $"Cannot decide which device to manage. Paired: {known}. Set it in the configuration.");
    }

    private static bool IsApple(string address)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\{address.ToLowerInvariant()}");
        return key?.GetValue("VID") is int vid && vid == 0x004C;
    }
}
