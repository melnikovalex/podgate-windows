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
        PodStatus? flipped = AppleAdvert.Parse(Advert(status: 0x00));
        PodStatus? straight = AppleAdvert.Parse(Advert(status: 0x02));

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
    public void ReadsChargingPerPod()
    {
        // Charging nibble: bit 0 is the pod that reported first, bit 2 is the case.
        PodStatus? status = AppleAdvert.Parse(Advert(status: 0x02, caseAndCharge: 0x14));

        Assert.False(status!.LeftCharging);
        Assert.True(status.RightCharging);
        Assert.False(status.CaseCharging);
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
