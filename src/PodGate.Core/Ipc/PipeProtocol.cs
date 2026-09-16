using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PodGate.Core.Ipc;

/// <summary>
/// The complete set of things the unelevated app may ask the service to do. Frozen at five, and none of
/// them takes a device argument: the target always comes from admin-written config.json, so an
/// unelevated caller can never redirect an elevated action.
/// </summary>
public enum PodGateVerb
{
    GetVersion,
    GetStatus,
    Block,
    Unblock,
    Restore,
}

public sealed class PodGateRequest
{
    public PodGateVerb Verb { get; set; }
}

public sealed class PodGateResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Version { get; set; }
    public string? DeviceName { get; set; }
    public string? Address { get; set; }
    public bool Blocked { get; set; }
    public bool Connected { get; set; }
    public double Seconds { get; set; }

    public static PodGateResponse Failure(string error) => new() { Ok = false, Error = error };
}

public static class PipeProtocol
{
    public const string PipeName = "PodGate.v1";

    /// <summary>No byte-order mark: the other end parses the line as plain JSON.</summary>
    public static readonly System.Text.Encoding Utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>
/// Client side of the pipe. Messages are one line of JSON each way: a duplex byte pipe has no
/// end-of-message marker, so plain SerializeAsync/DeserializeAsync leaves both ends blocked until they
/// time out, and a server that disconnects straight after writing throws the reply away.
/// </summary>
public static class PipeClient
{
    public static async Task<PodGateResponse> SendAsync(PodGateVerb verb, int timeoutMs = 30000, CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(3000, cancellationToken);
        }
        catch (TimeoutException)
        {
            return PodGateResponse.Failure("The PodGate service is not running.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);

        try
        {
            await PipeMessage.WriteAsync(pipe, JsonSerializer.Serialize(new PodGateRequest { Verb = verb }, PipeProtocol.Json), timeout.Token);
            string? line = await PipeMessage.ReadAsync(pipe, timeout.Token);
            if (line is null) return PodGateResponse.Failure("The service closed the connection without answering.");
            return JsonSerializer.Deserialize<PodGateResponse>(line, PipeProtocol.Json)
                   ?? PodGateResponse.Failure("The service sent an empty answer.");
        }
        catch (OperationCanceledException)
        {
            return PodGateResponse.Failure($"The service did not answer within {timeoutMs} ms.");
        }
        catch (IOException ex)
        {
            return PodGateResponse.Failure($"Lost the connection to the service: {ex.Message}");
        }
    }

    public static PodGateResponse Send(PodGateVerb verb, int timeoutMs = 30000) =>
        SendAsync(verb, timeoutMs).GetAwaiter().GetResult();
}
