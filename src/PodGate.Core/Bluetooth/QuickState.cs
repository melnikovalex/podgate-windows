namespace PodGate.Core.Bluetooth;

/// <summary>
/// The cheap read the tray polls: registry for the disabled flag, one native call for the link. The
/// full device report is far too heavy to run every few seconds (0.09 s against roughly 2 s).
/// </summary>
public sealed record QuickState(string Address, string? Name, bool Blocked, bool Connected)
{
    public static QuickState Read(string? preferredAddress = null)
    {
        string address = PodGateConfig.ResolveAddress(preferredAddress);
        BtDevice? device = BtNative.FindPairedDevice(address);
        return new QuickState(
            address,
            device?.Name,
            DeviceNodes.IsBlocked(address) ?? false,
            device?.Connected ?? false);
    }
}
