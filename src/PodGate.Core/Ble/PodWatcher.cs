using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace PodGate.Core.Ble;

/// <summary>
/// Listens for the AirPods' battery advertisement and publishes the readings of one pair. Which pair that
/// is cannot be settled by address - see <see cref="AdvertiserPicker"/> - so the model byte filters out
/// other kinds of AirPods and the picker keeps a neighbour's identical pair from taking the reading over.
/// </summary>
public sealed class PodWatcher : IDisposable
{
    private readonly BluetoothLEAdvertisementWatcher _watcher = new() { ScanningMode = BluetoothLEScanningMode.Passive };
    private readonly AdvertiserPicker _picker = new();
    private readonly Lock _gate = new();
    private readonly int _minimumRssi;
    private readonly Action<string>? _log;
    private int? _model;

    private PodStatus? _best;

    /// <param name="minimumRssi">Ignore advertisements weaker than this, in dBm. -70 is roughly one room.</param>
    /// <param name="model">Only read this model byte, which keeps a neighbour's different AirPods out.</param>
    public PodWatcher(int minimumRssi = -70, Action<string>? log = null, int? model = null)
    {
        _model = model;
        _minimumRssi = minimumRssi;
        _log = log;
        _watcher.Received += OnReceived;
    }

    /// <summary>A fresh reading of the strongest pair in range.</summary>
    public event Action<PodStatus>? Updated;

    /// <summary>Nothing heard for a while: the AirPods are off, away, or shut in their case.</summary>
    public event Action? Lost;

    public PodStatus? Current => _best;

    /// <summary>The model to listen for; set again when the managed device changes.</summary>
    public void Expect(int? model)
    {
        if (_model == model) return;
        _model = model;
        lock (_gate)
        {
            _picker.Reset();
            _best = null;
        }
        Lost?.Invoke();
    }

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
        lock (_gate)
        {
            if (_best is null || DateTimeOffset.Now - _best.Seen <= age) return;
            _best = null;
        }
        Lost?.Invoke();
    }

    /// <summary>How many distinct pairs are within <paramref name="minimumRssi"/> right now.</summary>
    public int PairsNearby(int minimumRssi)
    {
        lock (_gate) return _picker.PairsNearby(minimumRssi, DateTimeOffset.Now);
    }

    /// <summary>The two pods of one pair report the same levels, which is what makes this identify a pair.</summary>
    private static string Fingerprint(PodStatus status) => $"{status.Left}/{status.Right}/{status.Case}";

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
            if (_model is not null && status.Model != _model) continue;   // a different model entirely

            bool mine;
            lock (_gate)
            {
                mine = _picker.Accepts(args.BluetoothAddress, status.Rssi, DateTimeOffset.Now, Fingerprint(status));
                if (mine) _best = status;
            }
            if (mine) Updated?.Invoke(status);
        }
    }

    public void Dispose()
    {
        _watcher.Received -= OnReceived;
        Stop();
    }
}
