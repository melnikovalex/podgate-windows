using System.Diagnostics;
using System.Text.Json;
using PodGate.Core.Audio;
using PodGate.Core.Bluetooth;
using PodGate.Core.Ipc;
using PodGate.Core.Media;

namespace PodGate.Core;

public sealed record ConnectProgress(string Phase, int Percent);

public sealed record FlowResult(bool Ok, string Message, TimeSpan Elapsed);

/// <summary>Which output and microphone were default before PodGate took over, so a release can hand them back.</summary>
public sealed class PreviousDefaults
{
    public string? Render { get; set; }
    public string? Capture { get; set; }
    public DateTimeOffset SavedAt { get; set; }
}

/// <summary>
/// Connect and release, orchestrated from the user's session. The service owns the device node; this
/// owns everything that belongs to a logged-on user: endpoints, default devices, media.
///
/// Every timing here is measured, not guessed, and each one guards against a failure seen on real
/// hardware. Change them only with new evidence.
/// </summary>
public sealed class ConnectFlow(PodGateConfig config, Action<string>? log = null)
{
    private readonly Action<string> _log = log ?? (_ => { });

    // Waiting for the A2DP endpoint specifically: the Hands-Free render endpoint goes ACTIVE first, and
    // Windows refuses it as a default output with E_FAIL.
    private static readonly TimeSpan RenderWait = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    // The capture endpoint shows up a moment after the render one.
    private static readonly TimeSpan CaptureWait = TimeSpan.FromSeconds(5);

    // Let apps migrate their streams off the AirPods before the device disappears.
    private static readonly TimeSpan HandoverPause = TimeSpan.FromMilliseconds(700);

    // Turning the Hands-Free side off or on takes about 3 s; its endpoints appear or vanish after that.
    private static readonly TimeSpan HandsFreeWait = TimeSpan.FromSeconds(8);

    // A deferred disable is the failure this guards against; the baseband link can take a while to go.
    private static readonly TimeSpan ReleaseVerifyWait = TimeSpan.FromSeconds(25);

    public async Task<FlowResult> ConnectAsync(IProgress<ConnectProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        progress?.Report(new ConnectProgress("Connecting...", 8));

        string address = PodGateConfig.ResolveAddress(config.Address);
        Guid? container = DeviceNodes.GetContainerId(address);
        if (container is null) return new FlowResult(false, "The AirPods are not paired with this PC.", stopwatch.Elapsed);

        // Already connected: this is a switch between music and call quality, which takes about 3 s and
        // never drops the link, instead of a disconnect and a fresh connect.
        IReadOnlyList<AudioEndpoint> live = AudioEndpoints.ForContainer(container.Value);
        if (live.Any(e => e.Flow == AudioFlow.Render && e.State == EndpointState.Active))
        {
            return await SwitchModeAsync(container.Value, progress, stopwatch, cancellationToken);
        }

        RememberCurrentDefaults(container.Value);

        PodGateResponse unblock = await PipeClient.SendAsync(PodGateVerb.Unblock, cancellationToken: cancellationToken);
        if (!unblock.Ok) return new FlowResult(false, unblock.Error ?? "The service could not unblock the AirPods.", stopwatch.Elapsed);
        _log($"unblocked in {unblock.Seconds * 1000:N0} ms");
        progress?.Report(new ConnectProgress("Waiting for audio", 45));

        AudioEndpoint? render = await WaitForRenderAsync(container.Value, cancellationToken);
        if (render is null)
        {
            // Leave the device blocked rather than half-open: otherwise the next toggle sees "not
            // blocked", decides to release, and the user presses twice for nothing.
            await PipeClient.SendAsync(PodGateVerb.Block, cancellationToken: cancellationToken);
            _log("connect failed; blocked again");
            return new FlowResult(false, "AirPods not reachable (in the case, or out of range?)", stopwatch.Elapsed);
        }

        progress?.Report(new ConnectProgress("Switching audio over", 80));
        ModeAudio profile = UserSettings.Load().For(config.Mode);
        if (!await ApplyAsync(profile, container.Value, render, cancellationToken))
        {
            return new FlowResult(false, "Connected, but Windows would not switch the output.", stopwatch.Elapsed);
        }

        stopwatch.Stop();
        string done = config.Mode == ConnectMode.Music ? "Connected (music only)" : "Connected";
        progress?.Report(new ConnectProgress(done, 100));
        return new FlowResult(true, done, stopwatch.Elapsed);
    }

    public async Task<FlowResult> ReleaseAsync(IProgress<ConnectProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        progress?.Report(new ConnectProgress("Disconnecting...", 15));

        string address = PodGateConfig.ResolveAddress(config.Address);
        Guid? container = DeviceNodes.GetContainerId(address);
        IReadOnlyList<AudioEndpoint> endpoints = container is null ? [] : AudioEndpoints.ForContainer(container.Value);
        string[] ours = endpoints.Select(e => e.EndpointId).ToArray();

        string? currentRender = AudioPolicy.GetDefault(AudioFlow.Render, AudioRole.Console);
        bool listeningOnPods = currentRender is not null && ours.Contains(currentRender);

        if (listeningOnPods)
        {
            // Only pause when the sound is actually going to the AirPods: releasing them while you
            // listen on speakers should not stop your music.
            IReadOnlyList<string> paused = await MediaControls.PausePlayingAsync(cancellationToken);
            if (paused.Count > 0) _log($"paused: {string.Join(", ", paused)}");

            ModeAudio leaving = UserSettings.Load().DisconnectAudio;
            PreviousDefaults? previous = LoadPreviousDefaults();
            Hand(leaving.Output, AudioFlow.Render, [AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications], previous?.Render, ours);
            Hand(leaving.Microphone, AudioFlow.Capture, [AudioRole.Console, AudioRole.Multimedia], previous?.Capture, ours);
            Hand(leaving.CallMicrophone, AudioFlow.Capture, [AudioRole.Communications], previous?.Capture, ours);

            await Task.Delay(HandoverPause, cancellationToken);
        }

        progress?.Report(new ConnectProgress("Handing audio back", 55));
        PodGateResponse block = await PipeClient.SendAsync(PodGateVerb.Block, cancellationToken: cancellationToken);
        if (!block.Ok) return new FlowResult(false, block.Error ?? "The service could not disconnect the AirPods.", stopwatch.Elapsed);
        _log($"blocked in {block.Seconds * 1000:N0} ms");

        progress?.Report(new ConnectProgress("Waiting for the link to drop", 85));
        bool quiet = await WaitUntilReleasedAsync(address, container, cancellationToken);
        stopwatch.Stop();

        if (!quiet)
        {
            _log("WARNING: something is still using the AirPods");
            return new FlowResult(true, "Disconnected, but something still holds them", stopwatch.Elapsed);
        }

        progress?.Report(new ConnectProgress("Disconnected", 100));
        return new FlowResult(true, "Disconnected", stopwatch.Elapsed);
    }

    /// <summary>
    /// Switching an already connected pair between music and calls: the Hands-Free side goes off or on
    /// (about 3 s, the music keeps playing), then this mode's audio choices are applied.
    /// </summary>
    private async Task<FlowResult> SwitchModeAsync(Guid container, IProgress<ConnectProgress>? progress, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        bool wantHandsFree = config.Mode != ConnectMode.Music;
        bool hasHandsFree = AudioEndpoints.ForContainer(container)
            .Any(e => e.Transport == EndpointTransport.HandsFree && e.State == EndpointState.Active);

        if (wantHandsFree != hasHandsFree)
        {
            progress?.Report(new ConnectProgress(wantHandsFree ? "Turning the microphone on" : "Switching to music quality", 35));
            PodGateResponse answer = await PipeClient.SendAsync(
                wantHandsFree ? PodGateVerb.HandsFreeOn : PodGateVerb.HandsFreeOff, cancellationToken: cancellationToken);
            if (!answer.Ok) return new FlowResult(false, answer.Error ?? "The service could not switch the microphone.", stopwatch.Elapsed);
            _log($"hands-free {(wantHandsFree ? "on" : "off")} in {answer.Seconds * 1000:N0} ms");

            DateTime deadline = DateTime.UtcNow + HandsFreeWait;
            while (DateTime.UtcNow < deadline)
            {
                bool now = AudioEndpoints.ForContainer(container)
                    .Any(e => e.Transport == EndpointTransport.HandsFree && e.State == EndpointState.Active);
                if (now == wantHandsFree) break;
                await Task.Delay(PollInterval, cancellationToken);
            }
        }

        progress?.Report(new ConnectProgress("Switching audio over", 80));
        AudioEndpoint? render = AudioEndpoints.ForContainer(container)
            .FirstOrDefault(e => e.Flow == AudioFlow.Render && e.Transport == EndpointTransport.A2dp && e.State == EndpointState.Active);
        await ApplyAsync(UserSettings.Load().For(config.Mode), container, render, cancellationToken);

        stopwatch.Stop();
        string message = config.Mode == ConnectMode.Music ? "Music quality" : "Music and calls";
        progress?.Report(new ConnectProgress(message, 100));
        return new FlowResult(true, message, stopwatch.Elapsed);
    }

    /// <summary>Points the default devices where this mode says, leaving alone whatever it does not name.</summary>
    private async Task<bool> ApplyAsync(ModeAudio profile, Guid container, AudioEndpoint? render, CancellationToken cancellationToken)
    {
        bool needsMicrophone = profile.Microphone.Kind is AudioTarget.AirPods || profile.CallMicrophone.Kind is AudioTarget.AirPods;
        AudioEndpoint? capture = needsMicrophone ? await WaitForCaptureAsync(container, cancellationToken) : null;
        if (needsMicrophone && capture is null) _log("no active capture endpoint (Windows activates it when an app opens the microphone)");

        bool output = Point(profile.Output, AudioFlow.Render, [AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications], render?.EndpointId, "output");
        Point(profile.Microphone, AudioFlow.Capture, [AudioRole.Console, AudioRole.Multimedia], capture?.EndpointId, "microphone");
        Point(profile.CallMicrophone, AudioFlow.Capture, [AudioRole.Communications], capture?.EndpointId, "call microphone");
        return output;
    }

    private bool Point(AudioTarget target, AudioFlow flow, AudioRole[] roles, string? podsEndpoint, string what)
    {
        string? endpoint = target.Kind switch
        {
            AudioTarget.AirPods => podsEndpoint,
            AudioTarget.Device => target.EndpointId,
            _ => null,
        };
        if (endpoint is null) return target.Kind is AudioTarget.Unchanged;
        return SetDefaultWithRetry(endpoint, flow, roles, what);
    }

    /// <summary>The disconnect side of the same idea: "previous" is what the connect remembered.</summary>
    private void Hand(AudioTarget target, AudioFlow flow, AudioRole[] roles, string? previous, string[] ours)
    {
        string? endpoint = target.Kind switch
        {
            AudioTarget.Previous => previous,
            AudioTarget.Device => target.EndpointId,
            _ => null,
        };
        if (endpoint is null || ours.Contains(endpoint)) return;

        try
        {
            foreach (AudioRole role in roles) AudioPolicy.SetDefaultForRole(endpoint, role);
            _log($"{flow} handed back to {target.Describe()}");
        }
        catch (Exception ex)
        {
            _log($"handing the {flow} back failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Waiting for audio after an unblock happens in two steps, because the endpoints come back from
    /// NotPresent (no device) through Unplugged (device there, silent) to Active (audio flowing).
    /// The reconnect one-shot only means anything in the middle state, and its filter does not even
    /// exist in the first one - nudging too early is how a connect fails with the AirPods in your ears.
    /// </summary>
    private async Task<AudioEndpoint?> WaitForRenderAsync(Guid container, CancellationToken cancellationToken)
    {
        var nudged = new HashSet<string>();
        DateTime deadline = DateTime.UtcNow + RenderWait;

        while (DateTime.UtcNow < deadline)
        {
            IReadOnlyList<AudioEndpoint> endpoints = AudioEndpoints.ForContainer(container);

            AudioEndpoint? a2dp = endpoints.FirstOrDefault(e =>
                e.Flow == AudioFlow.Render && e.Transport == EndpointTransport.A2dp && e.State == EndpointState.Active);
            if (a2dp is not null) return a2dp;

            foreach (AudioEndpoint endpoint in endpoints.Where(e =>
                         e.Flow == AudioFlow.Render && e.State == EndpointState.Unplugged && e.FilterPath is not null))
            {
                if (!nudged.Add(endpoint.EndpointId)) continue;   // once per endpoint, not once per attempt
                try
                {
                    KsBtAudio.OneShot(endpoint.FilterPath!, reconnect: true);
                    _log($"one-shot reconnect sent: {endpoint.Transport}");
                }
                catch (Exception ex)
                {
                    _log($"one-shot reconnect failed ({endpoint.Transport}): {ex.Message}");
                }
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        // Nothing on A2DP: take any active render endpoint rather than leaving the user with silence.
        AudioEndpoint? fallback = AudioEndpoints.ForContainer(container)
            .FirstOrDefault(e => e.Flow == AudioFlow.Render && e.State == EndpointState.Active);
        if (fallback is not null) _log($"no A2DP endpoint appeared; falling back to {fallback.FriendlyName}");
        else _log($"no render endpoint became active; states were: {DescribeStates(container)}");
        return fallback;
    }

    private static string DescribeStates(Guid container) =>
        string.Join(", ", AudioEndpoints.ForContainer(container).Select(e => $"{e.Flow}/{e.Transport}={e.State}"));

    private static async Task<AudioEndpoint?> WaitForCaptureAsync(Guid container, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + CaptureWait;
        do
        {
            AudioEndpoint? capture = AudioEndpoints.ForContainer(container)
                .FirstOrDefault(e => e.Flow == AudioFlow.Capture && e.State == EndpointState.Active);
            if (capture is not null) return capture;
            await Task.Delay(300, cancellationToken);
        }
        while (DateTime.UtcNow < deadline);
        return null;
    }

    private async Task<bool> WaitUntilReleasedAsync(string address, Guid? container, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + ReleaseVerifyWait;
        while (DateTime.UtcNow < deadline)
        {
            bool connected = BtNative.FindPairedDevice(address)?.Connected ?? false;
            int active = container is null
                ? 0
                : AudioEndpoints.ForContainer(container.Value).Count(e => e.State == EndpointState.Active);
            if (!connected && active == 0) return true;
            await Task.Delay(300, cancellationToken);
        }
        return false;
    }

    /// <summary>
    /// Setting a default right after an endpoint goes ACTIVE can fail with E_FAIL: the registry lists it
    /// a moment before the audio service will accept it. Retry, then read it back, because a silent
    /// no-op looks exactly like success.
    /// </summary>
    private bool SetDefaultWithRetry(string endpointId, AudioFlow flow, AudioRole[] roles, string what)
    {
        string problem = "unknown";
        for (int attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                foreach (AudioRole role in roles) AudioPolicy.SetDefaultForRole(endpointId, role);
                if (AudioPolicy.GetDefault(flow, roles[0]) == endpointId)
                {
                    _log($"default {what} set{(attempt > 1 ? $" (attempt {attempt})" : "")}");
                    return true;
                }
                problem = "the call succeeded but the default did not change";
            }
            catch (Exception ex)
            {
                problem = ex.Message;
            }
            Thread.Sleep(500);
        }
        _log($"default {what} FAILED after 6 attempts: {problem}");
        return false;
    }

    private void RememberCurrentDefaults(Guid container)
    {
        string[] ours = AudioEndpoints.ForContainer(container).Select(e => e.EndpointId).ToArray();
        string? render = AudioPolicy.GetDefault(AudioFlow.Render, AudioRole.Console);
        if (render is null || ours.Contains(render)) return;   // already ours: keep what we saved before

        var previous = new PreviousDefaults
        {
            Render = render,
            Capture = AudioPolicy.GetDefault(AudioFlow.Capture, AudioRole.Communications),
            SavedAt = DateTimeOffset.Now,
        };

        try
        {
            Directory.CreateDirectory(Paths.UserDataDir);
            File.WriteAllText(Paths.StateFile, JsonSerializer.Serialize(previous));
        }
        catch (IOException ex)
        {
            _log($"could not remember the previous defaults: {ex.Message}");
        }
    }

    private static PreviousDefaults? LoadPreviousDefaults()
    {
        try
        {
            return File.Exists(Paths.StateFile)
                ? JsonSerializer.Deserialize<PreviousDefaults>(File.ReadAllText(Paths.StateFile))
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }
}
