using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace PodGate.Core.Ble;

/// <summary>
/// Listens for the AirPods' battery advertisement. The advertising address rotates every few minutes and
/// carries nothing that ties it to a paired device, so the pair is picked by model and signal strength:
/// the strongest advertiser of that model wins. Another pair of the same model in the same room can be
/// read instead, which is why the threshold and the off switch exist.
/// </summary>
public sealed class PodWatcher : IDisposable
{
    private readonly BluetoothLEAdvertisementWatcher _watcher = new() { ScanningMode = BluetoothLEScanningMode.Passive };
    private readonly int _minimumRssi;
    private readonly Action<string>? _log;

    private PodStatus? _best;

    /// <param name="minimumRssi">Ignore advertisements weaker than this, in dBm. -70 is roughly one room.</param>
    public PodWatcher(int minimumRssi = -70, Action<string>? log = null)
    {
        _minimumRssi = minimumRssi;
        _log = log;
        _watcher.Received += OnReceived;
    }

    /// <summary>A fresh reading of the strongest pair in range.</summary>
    public event Action<PodStatus>? Updated;

    /// <summary>Nothing heard for a while: the AirPods are off, away, or shut in their case.</summary>
    public event Action? Lost;

    public PodStatus? Current => _best;

    public void Start()
    {
        try
        {
            _watcher.Start();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"battery watch could not start: {ex.Message}");
        }
    }

    public void Stop()
    {
        try
        {
            _watcher.Stop();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"battery watch could not stop: {ex.Message}");
        }
    }

    /// <summary>Drops a reading that is older than <paramref name="age"/> and reports it as lost.</summary>
    public void Expire(TimeSpan age)
    {
        if (_best is null || DateTimeOffset.Now - _best.Seen <= age) return;
        _best = null;
        Lost?.Invoke();
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (args.RawSignalStrengthInDBm < _minimumRssi) return;

        foreach (BluetoothLEManufacturerData section in args.Advertisement.ManufacturerData)
        {
            if (section.CompanyId != AppleAdvert.AppleCompanyId) continue;

            var payload = new byte[section.Data.Length];
            using (DataReader reader = DataReader.FromBuffer(section.Data)) reader.ReadBytes(payload);

            PodStatus? status = AppleAdvert.Parse(payload, args.RawSignalStrengthInDBm);
            if (status is null) continue;

            // Strongest wins, but a newer reading from the same pair always replaces the old one, and a
            // pair that has gone quiet for half a minute loses its claim to a closer one.
            if (_best is not null && _best.Model != status.Model && _best.Rssi > status.Rssi &&
                DateTimeOffset.Now - _best.Seen < TimeSpan.FromSeconds(30))
            {
                continue;
            }

            _best = status;
            Updated?.Invoke(status);
        }
    }

    public void Dispose()
    {
        _watcher.Received -= OnReceived;
        Stop();
    }
}
