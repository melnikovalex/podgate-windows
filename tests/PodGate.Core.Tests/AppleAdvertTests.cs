using PodGate.Core.Ble;
using Xunit;

namespace PodGate.Core.Tests;

public class AppleAdvertTests
{
    /// <summary>
    /// A proximity-pairing advertisement built by hand, so the expected values are known exactly:
    /// AirPods Pro 2 USB-C, first-reported pod 80 %, second 60 %, case 40 % and charging.
    /// </summary>
    private static byte[] Advert(byte status = 0x00, byte pods = 0x86, byte caseAndCharge = 0x44, byte model = 0x24)
    {
        var payload = new byte[27];
        payload[0] = 0x07;              // proximity pairing
        payload[1] = 0x19;              // 25 bytes follow
        payload[2] = 0x01;
        payload[3] = model;
        payload[4] = 0x20;
        payload[5] = status;
        payload[6] = pods;
        payload[7] = caseAndCharge;
        return payload;
    }

    [Fact]
    public void ReadsBatteryAndModel()
    {
        PodStatus? status = AppleAdvert.Parse(Advert());

        Assert.NotNull(status);
        Assert.Equal(0x24, status.Model);
        Assert.Equal("AirPods Pro (2nd generation, USB-C)", status.ModelName);
        Assert.Equal(80, status.Left);
        Assert.Equal(60, status.Right);
        Assert.Equal(40, status.Case);
        Assert.True(status.CaseCharging);
        Assert.Equal(60, status.Lowest);
    }

    [Fact]
    public void SwapsLeftAndRightWithTheFlipBit()
    {
        // The bit is in the high nibble of the status byte: 0x20, not 0x02.
        PodStatus? flipped = AppleAdvert.Parse(Advert(status: 0x00));
        PodStatus? straight = AppleAdvert.Parse(Advert(status: 0x20));

        Assert.Equal(flipped!.Left, straight!.Right);
        Assert.Equal(flipped.Right, straight.Left);
    }

    [Fact]
    public void ReportsUnknownBatteryAsNull()
    {
        PodStatus? status = AppleAdvert.Parse(Advert(pods: 0xF9, caseAndCharge: 0x8F));

        Assert.Null(status!.Left);
        Assert.Equal(90, status.Right);
        Assert.Null(status.Case);
        Assert.Equal(90, status.Lowest);
        Assert.True(status.HasBattery);
    }

    [Fact]
    public void TreatsAnAllZeroReadingAsNothingKnown()
    {
        // What a pair sends when it is away or not reporting: believing it warns the user about 0 %.
        PodStatus? status = AppleAdvert.Parse(Advert(pods: 0x00, caseAndCharge: 0x00));

        Assert.False(status!.HasBattery);
        Assert.True(AppleAdvert.Parse(Advert(pods: 0x08, caseAndCharge: 0x00))!.HasBattery);
    }

    [Fact]
    public void ReadsChargingPerPod()
    {
        // Charging nibble: bit 0 is the pod in the low nibble of the battery byte, bit 2 is the case.
        PodStatus? status = AppleAdvert.Parse(Advert(status: 0x20, caseAndCharge: 0x14));

        Assert.True(status!.LeftCharging);
        Assert.False(status.RightCharging);
        Assert.False(status.CaseCharging);
    }

    [Fact]
    public void TheTwoPodsAgreeOnWhichOneIsCharging()
    {
        // Both pods advertise at the same time, one with the flip bit set and one without, and the
        // charging bits move with the flip just like the battery nibbles do. This pair of payloads was
        // captured with the left pod charging in the case and the right one out of it; reading either
        // advertisement has to give the same answer, which is what makes the bit order certain.
        PodStatus? asSeenFromOnePod = AppleAdvert.Parse(Advert(status: 0x71, pods: 0x75, caseAndCharge: 0x99));
        PodStatus? asSeenFromTheOther = AppleAdvert.Parse(Advert(status: 0x11, pods: 0x57, caseAndCharge: 0xA9));

        foreach (PodStatus? reading in new[] { asSeenFromOnePod, asSeenFromTheOther })
        {
            Assert.Equal(50, reading!.Left);
            Assert.Equal(70, reading.Right);
            Assert.Equal(90, reading.Case);
            Assert.True(reading.LeftCharging);
            Assert.False(reading.RightCharging);
            Assert.False(reading.CaseCharging);
        }
    }

    [Fact]
    public void ForgetsTheCaseWhenNoPodIsInIt()
    {
        // The case only reports a level while it holds a pod; with both pods out the nibble is 0xF.
        PodStatus? status = AppleAdvert.Parse(Advert(status: 0x03, pods: 0x67, caseAndCharge: 0x8F));

        Assert.Null(status!.Case);
        Assert.False(status.CaseCharging);
        Assert.False(status.LeftCharging);
        Assert.False(status.RightCharging);
        Assert.Equal(60, status.Left);
        Assert.Equal(70, status.Right);
    }

    [Fact]
    public void ReadsWearBits()
    {
        PodStatus? both = AppleAdvert.Parse(Advert(status: 0x0C));
        PodStatus? none = AppleAdvert.Parse(Advert(status: 0x00));

        Assert.True(both!.LeftInEar);
        Assert.True(both.RightInEar);
        Assert.True(both.AnyInEar);
        Assert.False(none!.AnyInEar);
    }

    [Theory]
    [InlineData(0x0E, "AirPods Pro")]
    [InlineData(0x13, "AirPods (3rd generation)")]
    [InlineData(0x0A, "AirPods Max")]
    [InlineData(0x77, "AirPods")]
    public void NamesKnownModels(byte model, string expected)
    {
        Assert.Equal(expected, AppleAdvert.Parse(Advert(model: model))!.ModelName);
    }

    [Theory]
    // pods nibbles, case nibble, what the tray shows
    [InlineData(0x86, 0x04, "L 60% · R 80% · Case 40%")]
    [InlineData(0x99, 0x0F, "90%")]
    [InlineData(0xF9, 0x04, "90% · Case 40%")]
    [InlineData(0x00, 0x00, "Battery unknown")]
    public void DescribesTheReadingForTheTray(byte pods, byte caseNibble, string expected)
    {
        Assert.Equal(expected, AppleAdvert.Parse(Advert(status: 0x20, pods: pods, caseAndCharge: caseNibble))!.Describe());
    }

    [Theory]
    // Real states, as the tray writes them: a bolt marks whatever is charging right now.
    [InlineData(0x67, 0xB9, "L 60%⚡ · R 70%⚡ · Case 90%")]   // both pods in the case
    [InlineData(0x67, 0xF9, "L 60%⚡ · R 70%⚡ · Case 90%⚡")] // ... and the case on a charger
    [InlineData(0x57, 0xA9, "L 50%⚡ · R 70% · Case 90%")]         // left charging, right in hand
    [InlineData(0x67, 0x8F, "L 60% · R 70%")]                                 // both out, no case reading
    public void MarksWhatIsCharging(byte pods, byte caseAndCharge, string expected)
    {
        Assert.Equal(expected, AppleAdvert.Parse(Advert(status: 0x11, pods: pods, caseAndCharge: caseAndCharge))!.Describe());
    }

    [Fact]
    public void IgnoresOtherAppleAdvertisements()
    {
        // Nearby Apple devices send type 0x07 too; without the 0x20 model suffix their bytes would decode
        // into believable nonsense (measured: a neighbour's device read as "AirPods, left 0 %").
        byte[] other = Advert();
        other[4] = 0x55;
        Assert.Null(AppleAdvert.Parse(other));

        Assert.Null(AppleAdvert.Parse(new byte[] { 0x10, 0x02, 0x01, 0x00 }));   // nearby-info, four bytes
        Assert.Null(AppleAdvert.Parse(Advert().AsSpan(0, 20)));                  // truncated
    }
}
