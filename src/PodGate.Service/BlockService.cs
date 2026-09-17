using PodGate.Core;
using PodGate.Core.Ipc;

namespace PodGate.Service;

/// <summary>
/// Owns the block state. Reconciles to Blocked when it starts, and gives the device up at shutdown,
/// which is the thing a user-session task structurally could not do (test T8).
/// </summary>
public sealed class BlockService(ILogger<BlockService> logger) : BackgroundService
{
    private readonly BlockController _controller = new(message => logger.LogInformation("{Message}", message));
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Set by the app while it is deliberately using the device, so reconcile leaves it alone.</summary>
    public bool ConnectRequested { get; private set; }

    public async Task<PodGateResponse> HandleAsync(PodGateVerb verb, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            switch (verb)
            {
                case PodGateVerb.GetVersion:
                    return new PodGateResponse
                    {
                        Ok = true,
                        Version = typeof(BlockService).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                    };

                case PodGateVerb.GetStatus:
                    return Describe(_controller.GetStatus());

                case PodGateVerb.Block:
                {
                    ConnectRequested = false;
                    TimeSpan elapsed = _controller.Block();
                    PodGateResponse response = Describe(_controller.GetStatus());
                    response.Seconds = elapsed.TotalSeconds;
                    return response;
                }

                case PodGateVerb.Unblock:
                {
                    ConnectRequested = true;
                    TimeSpan elapsed = _controller.Unblock();
                    PodGateResponse response = Describe(_controller.GetStatus());
                    response.Seconds = elapsed.TotalSeconds;
                    return response;
                }

                case PodGateVerb.HandsFreeOn:
                case PodGateVerb.HandsFreeOff:
                {
                    TimeSpan elapsed = _controller.SetHandsFree(verb == PodGateVerb.HandsFreeOn);
                    PodGateResponse response = Describe(_controller.GetStatus());
                    response.Seconds = elapsed.TotalSeconds;
                    return response;
                }

                case PodGateVerb.Restore:
                    ConnectRequested = true;
                    _controller.RestoreStock();
                    return Describe(_controller.GetStatus());

                default:
                    return PodGateResponse.Failure($"Unknown verb: {verb}");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "verb {Verb} failed", verb);
            return PodGateResponse.Failure(ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static PodGateResponse Describe(DeviceStatus status) => new()
    {
        Ok = true,
        Address = status.Address,
        DeviceName = status.Name,
        Blocked = status.Blocked,
        Connected = status.Connected,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (PodGateConfig.Load().ConnectOnStartup)
        {
            // The user asked for stock behaviour at boot: hand the AirPods to Windows, which connects them.
            logger.LogInformation("connect on startup is on; unblocking");
            await HandleAsync(PodGateVerb.Unblock, stoppingToken);
        }
        else
        {
            await ReconcileAsync("service start", stoppingToken);
        }

        // A Windows or driver update can re-enable the node behind our back (test T16), so check
        // periodically rather than trusting that nothing else touches it.
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ReconcileAsync("periodic check", stoppingToken);
        }
    }

    private async Task ReconcileAsync(string reason, CancellationToken cancellationToken)
    {
        if (ConnectRequested) return;
        try
        {
            DeviceStatus status = _controller.GetStatus();
            if (status.Blocked) return;
            logger.LogWarning("device is not blocked ({Reason}); re-asserting", reason);
            await HandleAsync(PodGateVerb.Block, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "reconcile failed ({Reason})", reason);
        }
    }

    /// <summary>
    /// Shutdown and suspend: give the device up so the next boot does not grab it. Node disable only,
    /// measured in milliseconds - there is no time, and no session, for audio work here.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            DeviceStatus status = _controller.GetStatus();
            if (PodGateConfig.Load().ConnectOnStartup)
            {
                logger.LogInformation("stopping: connect on startup is on, leaving the device as it is");
            }
            else if (!status.Blocked)
            {
                logger.LogInformation("stopping: blocking the device first");
                _controller.Block();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "could not block during shutdown");
        }

        await base.StopAsync(cancellationToken);
    }
}
