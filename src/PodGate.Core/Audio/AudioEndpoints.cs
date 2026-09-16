using Microsoft.Win32;

namespace PodGate.Core.Audio;

public enum EndpointState
{
    Unknown = 0,
    Active = 1,
    Disabled = 2,
    NotPresent = 4,
    Unplugged = 8,
}

public enum EndpointTransport
{
    Other,
    A2dp,
    HandsFree,
}

public sealed class AudioEndpoint
{
    public required AudioFlow Flow { get; init; }
    public required EndpointState State { get; init; }
    public required EndpointTransport Transport { get; init; }
    public required string EndpointId { get; init; }
    public string? FilterPath { get; init; }
    public string? FriendlyName { get; init; }
}

/// <summary>
/// Endpoint discovery straight out of the MMDevices registry. It reads the registry rather than PnP
/// because unplugged endpoints have no device node at all, which is exactly the state a blocked device
/// is in.
/// </summary>
public static class AudioEndpoints
{
    private const string MMDevicesKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio";

    // PKEY_Device_ContainerId: 8-byte property header followed by the 16-byte GUID.
    private const string ContainerIdValue = "{8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c},2";
    // Path of the kernel-streaming filter behind the endpoint, stored with a "{N}." prefix.
    private const string FilterPathValue = "{233164c8-1b2c-4c7d-bc68-b671687a2567},1";
    // Device instance path: the only non-localized way to tell A2DP from Hands-Free.
    private const string InstancePathValue = "{b3f8fa53-0004-438e-9003-51a46e139bfc},2";
    private const string NameValue = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
    private const string DescriptionValue = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";

    public static IReadOnlyList<AudioEndpoint> ForContainer(Guid containerId)
    {
        var found = new List<AudioEndpoint>();
        foreach (AudioFlow flow in Enum.GetValues<AudioFlow>())
        {
            using RegistryKey? flowKey = Registry.LocalMachine.OpenSubKey($@"{MMDevicesKey}\{flow}");
            if (flowKey is null) continue;

            foreach (string endpointName in flowKey.GetSubKeyNames())
            {
                using RegistryKey? endpointKey = flowKey.OpenSubKey(endpointName);
                using RegistryKey? properties = endpointKey?.OpenSubKey("Properties");
                if (endpointKey is null || properties is null) continue;

                if (ReadContainerId(properties) != containerId) continue;

                string instancePath = properties.GetValue(InstancePathValue) as string ?? string.Empty;
                found.Add(new AudioEndpoint
                {
                    Flow = flow,
                    State = ReadState(endpointKey),
                    Transport = ClassifyTransport(instancePath),
                    EndpointId = $"{{0.0.{(int)flow}.00000000}}.{endpointName}",
                    FilterPath = ReadFilterPath(properties),
                    FriendlyName = $"{properties.GetValue(NameValue)} ({properties.GetValue(DescriptionValue)})",
                });
            }
        }
        return found;
    }

    private static Guid? ReadContainerId(RegistryKey properties)
    {
        if (properties.GetValue(ContainerIdValue) is not byte[] raw || raw.Length != 24) return null;
        return new Guid(raw.AsSpan(8, 16).ToArray());
    }

    private static EndpointState ReadState(RegistryKey endpointKey)
    {
        if (endpointKey.GetValue("DeviceState") is not int state) return EndpointState.Unknown;
        return (state & 0xF) switch
        {
            1 => EndpointState.Active,
            2 => EndpointState.Disabled,
            4 => EndpointState.NotPresent,
            8 => EndpointState.Unplugged,
            _ => EndpointState.Unknown,
        };
    }

    private static string? ReadFilterPath(RegistryKey properties)
    {
        if (properties.GetValue(FilterPathValue) is not string raw || raw.Length == 0) return null;
        int dot = raw.IndexOf(".\\\\?\\", StringComparison.Ordinal);
        return dot >= 0 ? raw[(dot + 1)..] : raw;
    }

    private static EndpointTransport ClassifyTransport(string instancePath)
    {
        if (instancePath.Contains("0000110b", StringComparison.OrdinalIgnoreCase)) return EndpointTransport.A2dp;
        if (instancePath.Contains("BTHHFENUM", StringComparison.OrdinalIgnoreCase) ||
            instancePath.Contains("0000111e", StringComparison.OrdinalIgnoreCase)) return EndpointTransport.HandsFree;
        return EndpointTransport.Other;
    }
}
