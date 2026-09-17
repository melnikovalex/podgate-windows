using PodGate.Core.Ble;
using Xunit;

namespace PodGate.Core.Tests;

/// <summary>
/// The signal strengths here are the ones measured in a flat with several pairs of AirPods in it: the
/// owner's pair on the desk at -26 dBm, a stranger's pair of the very same model two rooms away at -80.
/// The old code had a strongest-wins guard that could never fire, so the neighbour's battery was shown as
/// the user's own. These tests exist so that cannot come back unnoticed.
/// </summary>
public class AdvertiserPickerTests
{
    private const ulong Mine = 0x0000_1111_2222;
    private const ulong MyOtherPod = 0x0000_1111_3333;
    private const ulong Neighbour = 0x0000_9999_8888;

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReadsTheNearPairAndIgnoresTheNeighbour()
    {
        var picker = new AdvertiserPicker();

        Assert.True(picker.Accepts(Mine, -26, Start));
        Assert.False(picker.Accepts(Neighbour, -80, Start.AddSeconds(1)));
        Assert.True(picker.Accepts(Mine, -30, Start.AddSeconds(2)));
    }

    [Fact]
    public void FollowsThePairsOtherPod()
    {
        // Both pods advertise, from different addresses and at different strengths, and either one carries
        // the whole reading. Handing over between them has to be free.
        var picker = new AdvertiserPicker();

        Assert.True(picker.Accepts(Mine, -26, Start));
        Assert.True(picker.Accepts(MyOtherPod, -34, Start.AddSeconds(1)));
        Assert.Equal(MyOtherPod, picker.Chosen);
        Assert.True(picker.Accepts(Mine, -28, Start.AddSeconds(2)));
    }

    [Fact]
    public void FollowsTheAddressRotation()
    {
        // The advertising address changes every few minutes. The new one is the same pair, at the same
        // strength, so it must be picked up without the battery ever going unknown.
        const ulong rotated = 0x0000_4444_5555;
        var picker = new AdvertiserPicker();

        Assert.True(picker.Accepts(Mine, -26, Start));
        Assert.True(picker.Accepts(rotated, -27, Start.AddMinutes(16)));
        Assert.Equal(rotated, picker.Chosen);
    }

    [Fact]
    public void DoesNotHandTheBatteryToADistantPairWhenOurPairGoesQuiet()
    {
        // A closed case stops advertising within seconds. The neighbour then becomes the only pair in
        // range, and must not inherit the battery line: unknown is right, someone else's 40% is not.
        var picker = new AdvertiserPicker();

        Assert.True(picker.Accepts(Mine, -26, Start));
        Assert.False(picker.Accepts(Neighbour, -80, Start.AddMinutes(1)));
        Assert.False(picker.Accepts(Neighbour, -80, Start.AddMinutes(4)));
    }

    [Fact]
    public void AdoptsAWeakerPairAfterALongSilence()
    {
        // Otherwise AirPods carried to another room, or a quieter second pair, could never be read again.
        var picker = new AdvertiserPicker();

        Assert.True(picker.Accepts(Mine, -26, Start));
        Assert.True(picker.Accepts(Neighbour, -80, Start.AddMinutes(6)));
    }

    [Fact]
    public void CountsTheTwoPodsOfOnePairAsOnePair()
    {
        // Both pods broadcast the same battery values, from two addresses. Counting advertisers would say
        // "two pairs are here" for a single pair on the desk, and the offer to connect would never appear.
        var picker = new AdvertiserPicker();

        picker.Accepts(Mine, -26, Start, "100/90/90");
        picker.Accepts(MyOtherPod, -28, Start, "100/90/90");

        Assert.Equal(1, picker.PairsNearby(-55, Start));
    }

    [Fact]
    public void CountsTwoPairsWhenTheRoomIsCrowded()
    {
        var picker = new AdvertiserPicker();

        picker.Accepts(Mine, -26, Start, "100/90/90");
        picker.Accepts(Neighbour, -30, Start, "40/40/-");

        Assert.Equal(2, picker.PairsNearby(-55, Start));
    }

    [Fact]
    public void IgnoresPairsThatAreFarAwayOrLongGone()
    {
        var picker = new AdvertiserPicker();

        picker.Accepts(Mine, -26, Start, "100/90/90");
        picker.Accepts(Neighbour, -80, Start, "40/40/-");          // in the room, not within reach

        Assert.Equal(1, picker.PairsNearby(-55, Start));
        Assert.Equal(0, picker.PairsNearby(-55, Start.AddMinutes(2)));   // nothing heard since
    }

    [Fact]
    public void StartsOverWhenTheManagedDeviceChanges()
    {
        var picker = new AdvertiserPicker();

        Assert.True(picker.Accepts(Mine, -26, Start));
        picker.Reset();

        Assert.Equal(0UL, picker.Chosen);
        Assert.True(picker.Accepts(Neighbour, -80, Start.AddSeconds(1)));
    }
}
