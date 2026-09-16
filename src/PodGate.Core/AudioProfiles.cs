namespace PodGate.Core;

/// <summary>Which device an action should switch to, or that it should leave that one alone.</summary>
public sealed class AudioTarget
{
    public const string Unchanged = "unchanged";
    public const string AirPods = "airpods";
    public const string Previous = "previous";
    public const string Device = "device";

    /// <summary>One of <see cref="Unchanged"/>, <see cref="AirPods"/>, <see cref="Previous"/>, <see cref="Device"/>.</summary>
    public string Kind { get; set; } = Unchanged;

    /// <summary>Set for <see cref="Device"/>: the Windows endpoint id.</summary>
    public string? EndpointId { get; set; }

    /// <summary>Set for <see cref="Device"/>: what to show when that device is not plugged in right now.</summary>
    public string? Name { get; set; }

    public static AudioTarget Of(string kind) => new() { Kind = kind };

    public static AudioTarget Of(string endpointId, string name) =>
        new() { Kind = Device, EndpointId = endpointId, Name = name };

    public string Describe() => Kind switch
    {
        AirPods => "AirPods",
        Previous => "Previous device",
        Device => Name ?? "A device that is not here",
        _ => "Leave unchanged",
    };
}

/// <summary>What one mode switches when it runs: output, microphone, and the microphone calls use.</summary>
public sealed class ModeAudio
{
    public AudioTarget Output { get; set; } = new();
    public AudioTarget Microphone { get; set; } = new();
    public AudioTarget CallMicrophone { get; set; } = new();

    public static ModeAudio Connect() => new()
    {
        Output = AudioTarget.Of(AudioTarget.AirPods),
        CallMicrophone = AudioTarget.Of(AudioTarget.AirPods),
    };

    /// <summary>Music keeps the AirPods out of call quality: nothing points at their microphone.</summary>
    public static ModeAudio Music() => new() { Output = AudioTarget.Of(AudioTarget.AirPods) };

    public static ModeAudio Disconnect() => new()
    {
        Output = AudioTarget.Of(AudioTarget.Previous),
        CallMicrophone = AudioTarget.Of(AudioTarget.Previous),
    };
}
