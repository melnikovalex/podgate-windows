using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using PodGate.Core;
using PodGate.Core.Bluetooth;

namespace PodGate.App;

/// <summary>
/// config.json is administrator-only: the service takes its target device from it. The unelevated app
/// writes it by starting itself again with the runas verb, which shows Windows' permission prompt, and
/// the elevated copy checks what it is asked to save before saving it. Nothing crosses the service pipe.
/// </summary>
public static class ElevatedConfig
{
    public const string Verb = "--save-config";

    /// <summary>False when the prompt was declined or saving failed.</summary>
    public static async Task<bool> SaveAsync(string? address = null, bool? connectOnStartup = null)
    {
        var arguments = new List<string> { Verb };
        if (address is not null) arguments.Add($"--address {BtNative.NormalizeAddress(address)}");
        if (connectOnStartup is not null) arguments.Add($"--connect-on-startup {(connectOnStartup.Value ? "true" : "false")}");

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                Arguments = string.Join(" ", arguments),
                UseShellExecute = true,
                Verb = "runas",
            });
            if (process is null) return false;
            await process.WaitForExitAsync();
            AppLog.Write($"config saved elevated: exit {process.ExitCode}");
            return process.ExitCode == 0;
        }
        catch (Win32Exception ex)
        {
            AppLog.Write($"elevated save not done: {ex.Message}");   // 1223: the user said no
            return false;
        }
    }

    /// <summary>The elevated side. Exit code 0 on success.</summary>
    public static int Apply(string[] args)
    {
        try
        {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 2;

            PodGateConfig config = PodGateConfig.Load();
            string? address = Value(args, "--address");
            if (address is not null)
            {
                string wanted = BtNative.NormalizeAddress(address);
                BtDevice device = BtNative.FindPairedDevice(wanted) ?? throw new InvalidOperationException($"{wanted} is not paired");

                // Switching pairs: the old one goes back to stock Windows, or it would stay blocked forever
                // with nothing left that manages it.
                string previous = config.Address;
                if (!string.IsNullOrWhiteSpace(previous) && BtNative.NormalizeAddress(previous) != wanted &&
                    BtNative.FindPairedDevice(previous) is not null)
                {
                    new BlockController(AppLog.Write).RestoreStock(previous);
                }

                config.Address = wanted;
                config.DeviceName = device.Name;
            }

            string? startup = Value(args, "--connect-on-startup");
            if (startup is not null) config.ConnectOnStartup = bool.Parse(startup);

            config.Version = typeof(ElevatedConfig).Assembly.GetName().Version?.ToString(3) ?? config.Version;
            config.Save();
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Write($"elevated save failed: {ex.Message}");
            return 1;
        }
    }

    private static string? Value(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1].ToString(CultureInfo.InvariantCulture) : null;
    }
}
