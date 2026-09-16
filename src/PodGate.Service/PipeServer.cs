using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using PodGate.Core.Ipc;

namespace PodGate.Service;

/// <summary>
/// The only way in. One connection at a time, five verbs, no parameters, and the device always comes
/// from admin-written config, so an unelevated caller cannot redirect an elevated action.
/// </summary>
/// <summary>Who may talk to the service. Shared with the smoke tests so both exercise the same rules.</summary>
public static class PipeSecurityPolicy
{
    public static PipeSecurity Create()
    {
        var security = new PipeSecurity();
        // SYSTEM and Administrators: everything. Interactive users: enough to call the verbs.
        // Deliberately no NETWORK: this is a local tool.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return security;
    }
}

public sealed class PipeServer(BlockService blocks, ILogger<PipeServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("listening on \\\\.\\pipe\\{Pipe}", PipeProtocol.PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using NamedPipeServerStream pipe = NamedPipeServerStreamAcl.Create(
                    PipeProtocol.PipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 4096,
                    outBufferSize: 4096,
                    PipeSecurityPolicy.Create());

                await pipe.WaitForConnectionAsync(stoppingToken);
                logger.LogDebug("client connected");
                await ServeAsync(pipe, stoppingToken);
                logger.LogDebug("client served");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "pipe loop failed; retrying");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        PodGateResponse response;
        try
        {
            logger.LogDebug("reading request");
            string? line = await PipeMessage.ReadAsync(pipe, stoppingToken);
            logger.LogDebug("read {Length} chars", line?.Length ?? -1);
            PodGateRequest? request = line is null ? null : JsonSerializer.Deserialize<PodGateRequest>(line, PipeProtocol.Json);

            if (request is null)
            {
                response = PodGateResponse.Failure("empty request");
            }
            else if (!Enum.IsDefined(request.Verb))
            {
                logger.LogWarning("rejected unknown verb {Verb}", (int)request.Verb);
                response = PodGateResponse.Failure("unknown verb");
            }
            else
            {
                logger.LogInformation("verb {Verb}", request.Verb);
                response = await blocks.HandleAsync(request.Verb, stoppingToken);
            }
        }
        catch (JsonException ex)
        {
            response = PodGateResponse.Failure($"malformed request: {ex.Message}");
        }

        await PipeMessage.WriteAsync(pipe, JsonSerializer.Serialize(response, PipeProtocol.Json), stoppingToken);
        // Let the client read it: disconnecting a named pipe throws away anything still in flight.
        pipe.WaitForPipeDrain();
        if (pipe.IsConnected) pipe.Disconnect();
    }
}
