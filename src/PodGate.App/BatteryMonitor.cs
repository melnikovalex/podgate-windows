using PodGate.Core;
using PodGate.Core.Ble;
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
    private static readonly TimeSpan ResumeWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(3);

    private readonly PodWatcher _watcher;
    private readonly Func<bool> _listeningOnPods;
    private readonly Action<string, string> _notify;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };

    private bool _warnedLow;
    private bool _warnedVeryLow;
    private bool _wasInEar;
    private IReadOnlyList<string> _paused = [];
    private DateTimeOffset _pausedAt;

    /// <param name="listeningOnPods">True when sound is actually going to the AirPods right now.</param>
    /// <param name="notify">Title and text of a tray notification.</param>
    public BatteryMonitor(Func<bool> listeningOnPods, Action<string, string> notify)
    {
        _listeningOnPods = listeningOnPods;
        _notify = notify;
        _watcher = new PodWatcher(log: AppLog.Write);
        _watcher.Updated += OnUpdated;
        _watcher.Lost += () => Changed?.Invoke(null);
        _timer.Tick += (_, _) =>
        {
            _watcher.Expire(Stale);
            if (_paused.Count > 0 && DateTimeOffset.Now - _pausedAt > ResumeWindow) _paused = [];
        };
    }

    /// <summary>A new reading, or null when the AirPods have gone quiet.</summary>
    public event Action<PodStatus?>? Changed;

    public PodStatus? Current => _watcher.Current;

    public void Start()
    {
        _watcher.Start();
        _timer.Start();
    }

    /// <summary>"L 80% · R 75% · Case 60%", collapsed to one value when the pods agree.</summary>
    public static string MenuText(PodStatus? status)
    {
        if (status is null || !status.HasBattery) return "Battery unknown";

        string pods = status.Left == status.Right
            ? Percent(status.Left)
            : $"L {Percent(status.Left)} · R {Percent(status.Right)}";
        return status.Case is null ? pods : $"{pods} · Case {Percent(status.Case)}";
    }

    private static string Percent(int? value) => value is null ? "—" : $"{value}%";

    private void OnUpdated(PodStatus status)
    {
        Changed?.Invoke(status);
        Warn(status);
        _ = EarDetectionAsync(status);
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
    /// A pod out of the ear pauses what is playing, but only while the sound is going to the AirPods: taking
    /// one out while you listen on speakers must not stop anything. Putting it back within a minute starts
    /// again exactly what PodGate paused.
    /// </summary>
    private async Task EarDetectionAsync(PodStatus status)
    {
        bool inEar = status.AnyInEar;
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
