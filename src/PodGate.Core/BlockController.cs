using System.Diagnostics;
using PodGate.Core.Audio;
using PodGate.Core.Bluetooth;

namespace PodGate.Core;

/// <summary>What PodGate believes about the device right now.</summary>
public sealed class DeviceStatus
{
    public required string Address { get; init; }
    public string? Name { get; init; }
    public bool Blocked { get; init; }
    public bool Connected { get; init; }
    public IReadOnlyList<AudioEndpoint> Endpoints { get; init; } = [];
}

/// <summary>
/// The privileged half of PodGate: block and unblock, nothing else. This runs in the service, which has
/// no user session, so it must never touch default audio devices or media controls: those belong to the
/// logged-on user. Sending a KS one-shot is fine here: it is a device handle, not session state.
/// </summary>
public sealed class BlockController(Action<string>? log = null)
{
    private readonly Action<string> _log = log ?? (_ => { });

    public DeviceStatus GetStatus(string? address = null)
    {
        string resolved = PodGateConfig.ResolveAddress(address);
        BtDevice? device = BtNative.FindPairedDevice(resolved);
        Guid? container = DeviceNodes.GetContainerId(resolved);

        return new DeviceStatus
        {
            Address = resolved,
            Name = device?.Name,
            Blocked = DeviceNodes.IsBlocked(resolved) ?? false,
            Connected = device?.Connected ?? false,
            Endpoints = container is null ? [] : AudioEndpoints.ForContainer(container.Value),
        };
    }

    /// <summary>
    /// Give the device up: ask the driver to drop the audio link, then disable the device node so
    /// Windows cannot take it back at boot. The one-shot comes first because disabling a device node is
    /// deferred while an audio stream is open, so a release during playback would otherwise do nothing.
    /// </summary>
    public TimeSpan Block(string? address = null)
    {
        var stopwatch = Stopwatch.StartNew();
        DeviceStatus status = GetStatus(address);

        foreach (AudioEndpoint endpoint in status.Endpoints.Where(e => e.State == EndpointState.Active && e.FilterPath is not null))
        {
            try
            {
                KsBtAudio.OneShot(endpoint.FilterPath!, reconnect: false);
                _log($"one-shot disconnect sent: {endpoint.Flow}/{endpoint.Transport}");
            }
            catch (Exception ex)
            {
                _log($"one-shot disconnect failed ({endpoint.Flow}/{endpoint.Transport}): {ex.Message}");
            }
        }

        string? instanceId = DeviceNodes.FindRootInstanceId(status.Address)
            ?? throw new InvalidOperationException($"No device node for {status.Address}");
        string how = DeviceNodes.SetEnabled(instanceId, enable: false);

        stopwatch.Stop();
        _log($"blocked via {how} in {stopwatch.Elapsed.TotalMilliseconds:N0} ms");
        return stopwatch.Elapsed;
    }

    /// <summary>
    /// Hand the device back to Windows. Making it the default output and starting audio is the tray's
    /// job, because both are per-user-session.
    /// </summary>
    public TimeSpan Unblock(string? address = null)
    {
        var stopwatch = Stopwatch.StartNew();
        string resolved = PodGateConfig.ResolveAddress(address);
        string? instanceId = DeviceNodes.FindRootInstanceId(resolved)
            ?? throw new InvalidOperationException($"No device node for {resolved}");
        string how = DeviceNodes.SetEnabled(instanceId, enable: true);

        StartIfStillDisabled(instanceId);

        stopwatch.Stop();
        _log($"unblocked via {how} in {stopwatch.Elapsed.TotalMilliseconds:N0} ms");
        return stopwatch.Elapsed;
    }

    /// <summary>
    /// After a boot with the device blocked, clearing ConfigFlags leaves the live node disabled and the
    /// audio endpoints NotPresent; the node has to be started explicitly. pnputil /enable-device is the
    /// one path measured to work, so it is the fallback if the Configuration Manager does not.
    /// </summary>
    private void StartIfStillDisabled(string instanceId)
    {
        if (!CfgMgr.StartIfStillDisabled(instanceId)) return;
        if (CfgMgr.GetProblem(instanceId) == CfgMgr.ProblemDisabled)
        {
            _log("node still disabled after CM_Setup_DevNode, using pnputil");
            RunPnpUtil("/enable-device", instanceId);
        }
        _log($"cold start: node started, live problem now {CfgMgr.GetProblem(instanceId)}");
    }

    private void RunPnpUtil(string verb, string instanceId)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"))
        {
            ArgumentList = { verb, instanceId },
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        using Process? process = Process.Start(start);
        if (process is not null && !process.WaitForExit(15_000)) _log("pnputil did not finish in 15 s");
    }

    /// <summary>
    /// Turns the AirPods' Hands-Free side off or on while they stay connected: music quality without a
    /// microphone, or both. Measured: about 3 s each way, the A2DP stream keeps playing, and the Hands-Free
    /// endpoints come back with new identifiers.
    /// </summary>
    public TimeSpan SetHandsFree(bool enable, string? address = null)
    {
        var stopwatch = Stopwatch.StartNew();
        string resolved = PodGateConfig.ResolveAddress(address);
        uint result = BtNative.SetServiceState(resolved, HandsFreeService, enable);
        stopwatch.Stop();

        // 87 is what an already-enabled service answers; anything else is a real failure.
        if (result is not 0 and not 87) throw new InvalidOperationException($"Hands-Free could not be turned {(enable ? "on" : "off")}: Win32 {result}");
        _log($"hands-free {(enable ? "on" : "off")} in {stopwatch.Elapsed.TotalMilliseconds:N0} ms");
        return stopwatch.Elapsed;
    }

    private static readonly Guid HandsFreeService = new("0000111e-0000-1000-8000-00805f9b34fb");

    /// <summary>
    /// Stock Windows behaviour: the device node enabled and every Bluetooth service back on. Used by the
    /// uninstaller and by the Restore verb, and deliberately the same code path for both.
    /// </summary>
    public void RestoreStock(string? address = null)
    {
        string resolved = PodGateConfig.ResolveAddress(address);
        string? instanceId = DeviceNodes.FindRootInstanceId(resolved);
        if (instanceId is not null && (DeviceNodes.IsBlocked(resolved) ?? false))
        {
            DeviceNodes.SetEnabled(instanceId, enable: true);
            _log("device node re-enabled");
        }
        if (instanceId is not null) StartIfStillDisabled(instanceId);

        BtDevice? device = BtNative.FindPairedDevice(resolved);
        if (device is null) return;

        // Re-enable every service the device is known to have. Companion apps can leave these disabled
        // after a crash, and older blocking approaches disabled them deliberately.
        foreach (string service in KnownServices(device))
        {
            uint result = BtNative.SetServiceState(resolved, new Guid(service), enable: true);
            // 87 (invalid parameter) is what an already-enabled service returns: measured on all eight.
            if (result is not 0 and not 87) _log($"service {service} could not be enabled: Win32 {result}");
        }
    }

    private static IEnumerable<string> KnownServices(BtDevice device) => device.InstalledServices;
}
