namespace PodGate.Core.Ble;

/// <summary>Battery and wear state of one pair, as far as the advertisement tells us.</summary>
public sealed record PodStatus
{
    /// <summary>Apple's model byte from the advertisement (0x24 = AirPods Pro 2 USB-C, 0x0E = AirPods Pro).</summary>
    public required int Model { get; init; }
    public required string ModelName { get; init; }

    /// <summary>Battery in ten-percent steps, or null when that pod did not report one.</summary>
    public int? Left { get; init; }
    public int? Right { get; init; }
    public int? Case { get; init; }

    public bool LeftCharging { get; init; }
    public bool RightCharging { get; init; }
    public bool CaseCharging { get; init; }

    /// <summary>Best effort: the advertisement's wear bits are undocumented (see the class remarks).</summary>
    public bool LeftInEar { get; init; }
    public bool RightInEar { get; init; }

    public int Rssi { get; init; }
    public DateTimeOffset Seen { get; init; } = DateTimeOffset.Now;

    /// <summary>What the tray colours and warns on: the emptier pod, ignoring the case.</summary>
    public int? Lowest => Left is null ? Right : Right is null ? Left : Math.Min(Left.Value, Right.Value);

    public bool AnyInEar => LeftInEar || RightInEar;

    /// <summary>
    /// How the tray shows it. A pair in its case usually reports one value for both pods, so "L 90% · R —"
    /// would read like a missing pod: one known value is shown as one number, and the device it belongs to
    /// is already named in the row above.
    /// </summary>
    public string Describe()
    {
        if (!HasBattery) return "Battery unknown";

        string pods = (Left, Right) switch
        {
            ({ } left, { } right) when left != right => $"L {Level(left, LeftCharging)} · R {Level(right, RightCharging)}",
            ({ } left, _) => Level(left, LeftCharging || RightCharging),
            (_, { } right) => Level(right, LeftCharging || RightCharging),
            _ => "",
        };
        string caseText = Case is null ? "" : $"Case {Level(Case.Value, CaseCharging)}";
        return string.Join(" · ", new[] { pods, caseText }.Where(part => part.Length > 0));
    }

    /// <summary>
    /// A percentage, with a bolt when that part is charging. The case only reports a level while it holds
    /// a pod, so "Case 90%" disappearing simply means both pods are out, not that the reading was lost.
    /// </summary>
    private static string Level(int percent, bool charging) => charging ? $"{percent}%⚡" : $"{percent}%";

    /// <summary>
    /// True only for a reading worth showing. A pair that is not reporting sends zero nibbles, so an
    /// all-zero reading means "nothing known", not "empty": warning about 0 % when the AirPods are simply
    /// away is the bug this prevents.
    /// </summary>
    public bool HasBattery => Left > 0 || Right > 0 || Case > 0;
}

/// <summary>
/// Apple's "proximity pairing" advertisement (manufacturer 0x004C, type 0x07, 27 bytes). It is the only way
/// to read AirPods battery on Windows, it is unencrypted in the part we need, and Apple documents none of
/// it, so this follows the layout every open-source reader agrees on and reports anything unexpected as
/// unknown rather than guessing:
///
///   byte 0   0x07   type: proximity pairing
///   byte 1   0x19   length, 25 bytes follow
///   byte 3   model  0x0E AirPods Pro, 0x24 AirPods Pro 2 (USB-C), ...
///   byte 5   status flags; the high nibble says which pod reported first, so left and right swap with it
///   byte 6   battery nibbles of the two pods, 0-10 in ten-percent steps, 0xF = not reported
///   byte 7   high nibble charging flags, low nibble case battery
///
/// The wear (in-ear) bits in byte 5 are the least certain part: they are inferred, and a pair that never
/// reports them simply never pauses anything.
/// </summary>
public static class AppleAdvert
{
    public const ushort AppleCompanyId = 0x004C;
    private const byte ProximityPairing = 0x07;
    private const int PayloadLength = 27;
    private const byte ModelSuffix = 0x20;

    public static PodStatus? Parse(ReadOnlySpan<byte> payload, int rssi = 0)
    {
        // Byte 4 is 0x20 on every pair that reports battery this way; other Apple products send type 0x07
        // advertisements too, and without this check their bytes decode into believable nonsense.
        if (payload.Length != PayloadLength || payload[0] != ProximityPairing || payload[4] != ModelSuffix) return null;

        byte status = payload[5];
        byte pods = payload[6];
        byte caseAndCharge = payload[7];

        // "Flipped" means the pod that reported first is the left one. The bit sits in the HIGH nibble of
        // the status byte, which is the nibble every reference implementation indexes.
        bool flipped = ((status >> 4) & 0x02) == 0;
        int? first = Battery(pods >> 4);
        int? second = Battery(pods & 0x0F);
        int charging = caseAndCharge >> 4;
        bool firstInEar = (status & 0x08) != 0;
        bool secondInEar = (status & 0x04) != 0;

        return new PodStatus
        {
            Model = payload[3],
            ModelName = Name(payload[3]),
            Left = flipped ? first : second,
            Right = flipped ? second : first,
            Case = Battery(caseAndCharge & 0x0F),
            // Measured, not assumed: the charging bits follow the nibble position in byte 6, not the pod.
            // Bit 0 belongs to the pod in the low nibble, bit 1 to the one in the high nibble, so they
            // swap with the flip bit exactly like the battery values do. Both of the pair's simultaneous
            // advertisements then agree on which pod is charging, which is how this was pinned down.
            LeftCharging = (charging & (flipped ? 0b0000_0010 : 0b0000_0001)) != 0,
            RightCharging = (charging & (flipped ? 0b0000_0001 : 0b0000_0010)) != 0,
            CaseCharging = (charging & 0b0000_0100) != 0,
            LeftInEar = flipped ? firstInEar : secondInEar,
            RightInEar = flipped ? secondInEar : firstInEar,
            Rssi = rssi,
        };
    }

    private static int? Battery(int nibble) => nibble is >= 0 and <= 10 ? nibble * 10 : null;

    /// <summary>
    /// Known model bytes. An unknown one still reports battery: the name is only for the tray, and every
    /// pair that sends this advertisement uses the same layout.
    /// </summary>
    public static string Name(int model) => model switch
    {
        0x02 => "AirPods",
        0x0F => "AirPods (2nd generation)",
        0x13 => "AirPods (3rd generation)",
        0x19 or 0x1B => "AirPods (4th generation)",
        0x0E => "AirPods Pro",
        0x14 => "AirPods Pro (2nd generation)",
        0x24 => "AirPods Pro (2nd generation, USB-C)",
        0x0A => "AirPods Max",
        0x1F => "AirPods Max (USB-C)",
        _ => "AirPods",
    };
}
