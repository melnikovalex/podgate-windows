using PodGate.Core;
using PodGate.Core.Ble;
using PodGate.Core.Bluetooth;
using PodGate.Core.Media;

namespace PodGate.App;

/// <summary>
/// Reads the AirPods' battery from their Bluetooth advertisement, warns when it runs low, and pauses what
/// is playing when a pod comes out. All of it is per user and needs no elevation: the service knows nothing
/// about any of this.
/// </summary>
public sealed class BatteryMonitor : IDisposable
{
    private const int WarnAt = 20;
    private const int WarnAgainAt = 5;

    /// <summary>
    /// How strong the advertisement has to be before the AirPods count as "here". -55 dBm is about an arm's
    /// length: weaker than that and the offer would fire from the next room, which is worse than useless.
    /// </summary>
    private const int NearbyRssi = -55;

    /// <summary>Offer once, then leave it alone: a pair on the desk advertises every couple of seconds.</summary>
    private static readonly TimeSpan AskAgainAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ResumeWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(3);

    private readonly PodWatcher _watcher;
    private readonly Func<bool> _listeningOnPods;
    private readonly Func<bool> _connected;
    private readonly Action<string, string> _notify;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };

    private bool _warnedLow;
    private bool _warnedVeryLow;
    private bool _wasNearby;
    private DateTimeOffset _asked = DateTimeOffset.MinValue;
    private bool _wasInEar;
    private IReadOnlyList<string> _paused = [];
    private DateTimeOffset _pausedAt;

    /// <param name="listeningOnPods">True when sound is actually going to the AirPods right now.</param>
    /// <param name="connected">True when the AirPods are already on this PC, so there is nothing to offer.</param>
    /// <param name="notify">Title and text of a tray notification.</param>
    public BatteryMonitor(Func<bool> listeningOnPods, Func<bool> connected, Action<string, string> notify)
    {
        _listeningOnPods = listeningOnPods;
        _connected = connected;
        _notify = notify;
        _watcher = new PodWatcher(log: AppLog.Write, model: ExpectedModel());
        _watcher.Updated += OnUpdated;
        _watcher.Lost += () =>
        {
            _wasNearby = false;   // gone and back again is a fresh arrival
            Changed?.Invoke(null);
        };
        _timer.Tick += (_, _) =>
        {
            _watcher.Expire(Stale);
            if (_paused.Count > 0 && DateTimeOffset.Now - _pausedAt > ResumeWindow) _paused = [];
        };
    }

    /// <summary>A new reading, or null when the AirPods have gone quiet.</summary>
    public event Action<PodStatus?>? Changed;

    /// <summary>The AirPods just turned up within reach and are not connected here. Offer to connect them.</summary>
    public event Action? ArrivedNearby;

    public PodStatus? Current => _watcher.Current;

    /// <summary>The managed pair's model byte, so only its advertisements are read.</summary>
    private static int? ExpectedModel()
    {
        try
        {
            int? product = PodGateConfig.ProductId(PodGateConfig.ResolveAddress());
            return product is null ? null : product & 0xFF;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Call after the managed device changes: the new pair has its own model.</summary>
    public void Retarget() => _watcher.Expect(ExpectedModel());

    public void Start()
    {
        _watcher.Start();
        _timer.Start();
    }

    /// <summary>
    /// "L 80% · R 75% · Case 60%" from the AirPods broadcast. Anything else Windows knows a battery for -
    /// another headset, for instance - has a single number, and that is all it shows.
    /// </summary>
    public static string MenuText(PodStatus? status)
    {
        if (status is not null && status.HasBattery) return status.Describe();
        int? windows = WindowsBattery();
        return windows is null ? "Battery unknown" : $"{windows}%";
    }

    /// <summary>What Windows itself reports for the configured device, for pairs that do not broadcast.</summary>
    private static int? WindowsBattery()
    {
        try
        {
            string? node = DeviceNodes.FindRootInstanceId(PodGateConfig.ResolveAddress());
            return node is null ? null : CfgMgr.GetBattery(node);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void OnUpdated(PodStatus status)
    {
        Changed?.Invoke(status);
        Warn(status);
        Arrive(status);
        _ = EarDetectionAsync(status);
    }

    /// <summary>
    /// Offers to connect when the AirPods come within reach, for people who would rather not remember the
    /// shortcut. Only on the edge from away to here: the pair advertises every couple of seconds, so acting
    /// on the reading itself would ask again and again while they simply sit on the desk.
    /// </summary>
    private void Arrive(PodStatus status)
    {
        bool nearby = status.Rssi >= NearbyRssi;
        bool wasNearby = _wasNearby;
        _wasNearby = nearby;

        if (!nearby || wasNearby) return;
        if (DateTimeOffset.Now - _asked < AskAgainAfter) return;
        if (!UserSettings.Load().AskWhenNearby) return;
        if (_connected()) return;

        _asked = DateTimeOffset.Now;
        ArrivedNearby?.Invoke();
    }

    private void Warn(PodStatus status)
    {
        // Zero means the pair is not reporting (see PodStatus.HasBattery), so there is nothing to warn about.
        int? lowest = status.Lowest;
        if (lowest is null or 0 || !status.HasBattery) return;

        // Charging resets the warnings, so a pair put in its case warns again on the next charge cycle.
        if (status.LeftCharging && status.RightCharging || lowest > WarnAt + 5)
        {
            _warnedLow = false;
            _warnedVeryLow = false;
            return;
        }

        if (!UserSettings.Load().LowBatteryWarnings) return;

        if (lowest <= WarnAgainAt && !_warnedVeryLow)
        {
            _warnedVeryLow = true;
            _warnedLow = true;
            _notify("AirPods almost empty", $"{lowest}% left. Put them in the case soon.");
        }
        else if (lowest <= WarnAt && !_warnedLow)
        {
            _warnedLow = true;
            _notify("AirPods running low", $"{lowest}% left.");
        }
    }

    /// <summary>
    /// Taking the AirPods out of your ears pauses what is playing, but only while the sound is going to them:
    /// doing it while you listen on speakers must not stop anything. Putting one back within a minute starts
    /// again exactly what PodGate paused. The advertisement only reports whether *any* pod is in an ear, so
    /// this fires when the last one comes out, not the first.
    /// </summary>
    private async Task EarDetectionAsync(PodStatus status)
    {
        bool inEar = status.InEar;
        bool wasInEar = _wasInEar;
        _wasInEar = inEar;
        if (inEar == wasInEar || !UserSettings.Load().EarDetection) return;

        if (!inEar && wasInEar)
        {
            if (!_listeningOnPods()) return;
            _paused = await MediaControls.PausePlayingAsync();
            _pausedAt = DateTimeOffset.Now;
            if (_paused.Count > 0) AppLog.Write($"pod out: paused {string.Join(", ", _paused)}");
        }
        else if (inEar && _paused.Count > 0 && DateTimeOffset.Now - _pausedAt <= ResumeWindow)
        {
            await MediaControls.ResumeAsync(_paused);
            AppLog.Write($"pod back in: resumed {string.Join(", ", _paused)}");
            _paused = [];
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _watcher.Dispose();
    }
}
