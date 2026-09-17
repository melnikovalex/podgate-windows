namespace PodGate.Core.Ble;

/// <summary>
/// Decides which advertiser's battery reading to believe when several pairs of the same model are in range.
///
/// There is no better way to tell them apart. The advertisement comes from a rotating random address, and
/// Windows keeps no LE bond with AirPods - every device node they create is BTHENUM, classic Bluetooth - so
/// there is no identity resolving key to match the address against. Neighbouring AirPods are not a corner
/// case either: measured in one flat, a stranger's pair of the same model sat at -80 dBm while the owner's
/// sat at -26.
///
/// So: one advertiser is chosen and kept. Another only takes over when it comes within
/// <see cref="SwitchMargin"/> dB of it, which lets the pair's two pods and its rotating addresses hand over
/// freely while keeping the neighbour out. When the chosen pair goes quiet, the strength it had is
/// remembered, so a distant pair cannot inherit the battery line simply by being the only one left; after
/// <see cref="ForgetTheRoom"/> of silence that memory is dropped, or the AirPods could never be read again
/// after being carried to another room.
/// </summary>
public sealed class AdvertiserPicker
{
    /// <summary>How close another advertiser has to come, in dB, before it is read instead.</summary>
    private const int SwitchMargin = 8;

    /// <summary>An advertiser not heard for this long stops being a candidate; its address rotates anyway.</summary>
    private static readonly TimeSpan Forget = TimeSpan.FromSeconds(30);

    /// <summary>After this much silence any advertiser may be adopted again, however weak.</summary>
    private static readonly TimeSpan ForgetTheRoom = TimeSpan.FromMinutes(5);

    private readonly Dictionary<ulong, Smoothed> _heard = [];

    private ulong _chosen;
    private double _chosenRssi;
    private DateTimeOffset _chosenHeard;

    /// <summary>The advertiser currently being read, for logs and tests. Zero when nothing has been chosen.</summary>
    public ulong Chosen => _chosen;

    /// <summary>True when this advertisement should be published as the pair's battery.</summary>
    public bool Accepts(ulong address, int rssi, DateTimeOffset now)
    {
        if (!_heard.TryGetValue(address, out Smoothed? source)) _heard[address] = source = new Smoothed(rssi);

        source.Add(rssi);
        source.Heard = now;

        foreach (ulong gone in _heard.Where(entry => now - entry.Value.Heard > Forget).Select(entry => entry.Key).ToList())
        {
            _heard.Remove(gone);
            if (gone == _chosen) _chosen = 0;
        }

        if (address != _chosen)
        {
            bool longSilence = now - _chosenHeard > ForgetTheRoom;
            if (_chosenRssi != 0 && !longSilence && source.Rssi < _chosenRssi - SwitchMargin) return false;
            _chosen = address;
        }

        _chosenRssi = source.Rssi;
        _chosenHeard = now;
        return true;
    }

    /// <summary>Forgets everything, for when the managed device changes.</summary>
    public void Reset()
    {
        _heard.Clear();
        _chosen = 0;
        _chosenRssi = 0;
        _chosenHeard = default;
    }

    /// <summary>
    /// Signal strength of one advertiser, averaged: consecutive packets from a pod lying on the desk were
    /// measured swinging between -16 and -40 dBm, so a single reading decides nothing on its own.
    /// </summary>
    private sealed class Smoothed(int rssi)
    {
        public double Rssi { get; private set; } = rssi;
        public DateTimeOffset Heard { get; set; }

        public void Add(int reading) => Rssi = (Rssi * 2 + reading) / 3;
    }
}
