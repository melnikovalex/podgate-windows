using System.Diagnostics;
using System.Security.Principal;
using PodGate.Core;
using PodGate.Core.Audio;
using PodGate.Core.Bluetooth;
using PodGate.Core.Ipc;

// Hardware smoke tests: run on a real PC with paired AirPods, never in CI. They check that
//   1. the device-node disable produces a persistent block (ConfigFlags bit 0),
//   2. the KS one-shot works unelevated,
//   3. IPolicyConfig::SetDefaultEndpoint works unelevated,
// and offer verbs for the service, the connect flow and live device-node state.
// Read-only by default. --disable / --enable / --block / --unblock / --live-enable change state.

bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
string action = args.FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal)) ?? "--report";

Console.WriteLine($"PodGate smoke tests   elevated={elevated}   action={action}");
Console.WriteLine(new string('-', 78));

// --- pipe self-test: both ends in this process, to tell a protocol bug from a service bug --------
if (action == "--pipe-selftest")
{
    string name = "PodGate.selftest." + Guid.NewGuid().ToString("N");
    bool acl = args.Contains("--acl");
    Console.WriteLine($"  server created {(acl ? "with the service's ACL" : "plainly")}");
    using System.IO.Pipes.NamedPipeServerStream server = acl
        ? System.IO.Pipes.NamedPipeServerStreamAcl.Create(name, System.IO.Pipes.PipeDirection.InOut, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous, 4096, 4096,
            PodGate.Service.PipeSecurityPolicy.Create())
        : new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous, 4096, 4096);

    Task serverSide = Task.Run(async () =>
    {
        await server.WaitForConnectionAsync();
        Console.WriteLine("  server: connected");
        string? line = await PipeMessage.ReadAsync(server, CancellationToken.None);
        Console.WriteLine($"  server: read '{line}'");
        await PipeMessage.WriteAsync(server, "{\"ok\":true}", CancellationToken.None);
        server.WaitForPipeDrain();
        Console.WriteLine("  server: replied");
    });

    using var client = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut,
        System.IO.Pipes.PipeOptions.Asynchronous);
    await client.ConnectAsync(3000);
    Console.WriteLine("  client: connected");
    await PipeMessage.WriteAsync(client, "{\"verb\":\"GetVersion\"}", CancellationToken.None);
    Console.WriteLine("  client: wrote request");

    using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    try
    {
        string? reply = await PipeMessage.ReadAsync(client, readTimeout.Token);
        Console.WriteLine($"  client: read '{reply}'");
        Console.WriteLine("PASS  the protocol itself works; a failure against the service is the service's");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("FAIL  the protocol itself hangs, independent of the service");
        return 1;
    }
    await serverSide;
    return 0;
}

// --- turn the Hands-Free side of the AirPods off or on while they stay connected ------------------
if (action == "--hands-free")
{
    string? wanted = args.SkipWhile(a => a != "--hands-free").Skip(1).FirstOrDefault();
    if (wanted is not "on" and not "off") { Console.WriteLine("usage: --hands-free on|off"); return 2; }

    string hfAddress = PodGateConfig.ResolveAddress();
    var handsFree = new Guid("0000111e-0000-1000-8000-00805f9b34fb");
    var watch = Stopwatch.StartNew();
    uint result = BtNative.SetServiceState(hfAddress, handsFree, enable: wanted == "on");
    Console.WriteLine($"  hands-free {wanted}: Win32 {result} in {watch.ElapsedMilliseconds} ms");

    Guid? hfContainer = DeviceNodes.GetContainerId(hfAddress);
    for (int i = 0; i < 6; i++)
    {
        await Task.Delay(1000);
        string states = hfContainer is null ? "no container"
            : string.Join("  ", AudioEndpoints.ForContainer(hfContainer.Value).Select(e => $"{e.Flow}/{e.Transport}={e.State}"));
        Console.WriteLine($"  +{i + 1}s connected={BtNative.FindPairedDevice(hfAddress)?.Connected}  {states}");
    }
    return result == 0 ? 0 : 1;
}

// --- raw Apple advertisement bytes, to check the battery layout against real hardware --------------
if (action == "--ble-raw")
{
    var payloads = new Dictionary<string, int>();
    var raw = new Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher
    {
        ScanningMode = Windows.Devices.Bluetooth.Advertisement.BluetoothLEScanningMode.Passive,
    };
    raw.Received += (_, e) =>
    {
        foreach (var section in e.Advertisement.ManufacturerData)
        {
            if (section.CompanyId != 0x004C) continue;
            var bytes = new byte[section.Data.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(section.Data)) reader.ReadBytes(bytes);
            string hex = Convert.ToHexString(bytes);
            lock (payloads)
            {
                if (payloads.TryGetValue(hex, out int count)) { payloads[hex] = count + 1; return; }
                payloads[hex] = 1;
            }
            Console.WriteLine($"  {DateTime.Now:HH:mm:ss} rssi={e.RawSignalStrengthInDBm,4} len={bytes.Length,2} {hex}");
        }
    };
    raw.Start();
    Console.WriteLine("  dumping distinct Apple payloads for 30 s");
    await Task.Delay(TimeSpan.FromSeconds(30));
    raw.Stop();
    Console.WriteLine($"  {payloads.Count} distinct payloads");
    return 0;
}

// --- battery layout probe: one line per state change of our own pair, with the bytes spelled out ----
if (action == "--ble-probe")
{
    int probeSeconds = 300;
    string? probeArg = args.SkipWhile(a => a != "--ble-probe").Skip(1).FirstOrDefault();
    if (probeArg is not null && int.TryParse(probeArg, out int probeParsed)) probeSeconds = probeParsed;

    // "all" keeps every model, which is how a second pair is compared against the configured one.
    bool everyModel = args.Contains("all");
    int ours = -1;
    if (!everyModel)
    {
        int? paired = PodGateConfig.ProductId(PodGateConfig.ResolveAddress());
        if (paired is null) { Console.WriteLine("  no product id in the pairing record; cannot tell our pair apart"); return 2; }
        ours = paired.Value & 0xFF;
    }
    Console.WriteLine(everyModel
        ? $"  watching every AirPods model for {probeSeconds} s; one line whenever bytes 3-7 change"
        : $"  watching model 0x{ours:X2} for {probeSeconds} s; one line whenever bytes 3-7 change");
    Console.WriteLine("  time      rssi  b3 b4 b5 b6 b7   status bits  pods    L     R     case   charge");

    string last = "";
    var probe = new Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher
    {
        ScanningMode = Windows.Devices.Bluetooth.Advertisement.BluetoothLEScanningMode.Passive,
    };
    probe.Received += (_, e) =>
    {
        foreach (var section in e.Advertisement.ManufacturerData)
        {
            if (section.CompanyId != 0x004C) continue;
            var bytes = new byte[section.Data.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(section.Data)) reader.ReadBytes(bytes);
            if (bytes.Length != 27 || bytes[0] != 0x07 || bytes[4] != 0x20) continue;
            if (!everyModel && bytes[3] != ours) continue;

            string key = $"{bytes[3]:X2}{bytes[4]:X2}{bytes[5]:X2}{bytes[6]:X2}{bytes[7]:X2}";
            lock (probe) { if (key == last) return; last = key; }

            var parsed = PodGate.Core.Ble.AppleAdvert.Parse(bytes, e.RawSignalStrengthInDBm)!;
            Console.WriteLine(
                $"  {DateTime.Now:HH:mm:ss}  {e.RawSignalStrengthInDBm,4}  " +
                $"{bytes[3]:X2} {bytes[4]:X2} {bytes[5]:X2} {bytes[6]:X2} {bytes[7]:X2}   " +
                $"{(everyModel ? parsed.ModelName.PadRight(38) : "")}" +
                $"{Convert.ToString(bytes[5], 2).PadLeft(8, '0')}     " +
                $"{bytes[6] >> 4,2}/{bytes[6] & 0x0F,-2}  " +
                $"L={Cell(parsed.Left)}{(parsed.LeftCharging ? "+" : " ")} R={Cell(parsed.Right)}{(parsed.RightCharging ? "+" : " ")} " +
                $"case={Cell(parsed.Case)}{(parsed.CaseCharging ? "+" : " ")} " +
                $"chargeNibble={bytes[7] >> 4:X1} inEar={(parsed.InEar ? "yes" : "no ")}");
        }
    };
    probe.Start();
    await Task.Delay(TimeSpan.FromSeconds(probeSeconds));
    probe.Stop();
    return 0;

    static string Cell(int? value) => value is null ? "  ?" : $"{value,3}";
}

// --- watch the Apple battery advertisement (manufacturer 0x004C) -----------------------------------
if (action == "--ble-watch")
{
    int seconds = 30;
    string? secondsArg = args.SkipWhile(a => a != "--ble-watch").Skip(1).FirstOrDefault();
    if (secondsArg is not null && int.TryParse(secondsArg, out int parsed)) seconds = parsed;

    // Exactly what the tray does: the configured pair's model byte, and the same -70 dBm threshold. The
    // point of this verb is to see what the app would show, neighbours included or excluded for real.
    int? expected = PodGateConfig.ProductId(PodGateConfig.ResolveAddress()) & 0xFF;
    Console.WriteLine($"  reading model 0x{expected:X2} at the app's own threshold; anything else is ignored");
    using var watcher = new PodGate.Core.Ble.PodWatcher(log: Console.WriteLine, model: expected);
    watcher.Updated += status => Console.WriteLine(
        $"  {status.Seen:HH:mm:ss} rssi={status.Rssi,4} model=0x{status.Model:X2} {status.ModelName,-38} " +
        $"L={Show(status.Left)}{(status.LeftCharging ? "+" : " ")} R={Show(status.Right)}{(status.RightCharging ? "+" : " ")} " +
        $"case={Show(status.Case)}{(status.CaseCharging ? "+" : " ")} inEar={(status.InEar ? "yes" : "no ")} " +
        $"");
    watcher.Start();
    Console.WriteLine($"  listening for {seconds} s; open the case, put a pod in, take it out...");
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    watcher.Stop();
    Console.WriteLine(watcher.Current is null ? "FAIL  no Apple advertisement heard" : "PASS  advertisements received");
    return watcher.Current is null ? 1 : 0;

    static string Show(int? value) => value is null ? " ? " : $"{value,3}";
}

// --- live enable: CM_Enable_DevNode on the root, reporting live problem code before and after -----
if (action == "--live-enable")
{
    string liveId = DeviceNodes.FindRootInstanceId(PodGateConfig.ResolveAddress())!;
    Console.WriteLine($"  before: problem={CfgMgr.GetProblem(liveId)} configFlags=0x{DeviceNodes.ReadConfigFlags(liveId):X}");
    var liveWatch = System.Diagnostics.Stopwatch.StartNew();
    CfgMgr.SetDevNodeEnabled(CfgMgr.LocateDevNode(liveId)!.Value, enable: true);
    Console.WriteLine($"  after:  problem={CfgMgr.GetProblem(liveId)} configFlags=0x{DeviceNodes.ReadConfigFlags(liveId):X} in {liveWatch.ElapsedMilliseconds} ms");
    return 0;
}

// --- KS reconnect to every render filter, whatever the registry says the endpoint state is ------
if (action == "--ks-reconnect")
{
    string ksAddress = PodGateConfig.ResolveAddress();
    Guid? ksContainer = DeviceNodes.GetContainerId(ksAddress);
    if (ksContainer is null) { Console.WriteLine("FAIL  no container"); return 1; }
    foreach (AudioEndpoint endpoint in AudioEndpoints.ForContainer(ksContainer.Value).Where(e => e.Flow == AudioFlow.Render && e.FilterPath is not null))
    {
        try
        {
            KsBtAudio.OneShot(endpoint.FilterPath!, reconnect: true);
            Console.WriteLine($"  sent    {endpoint.Transport,-9} (registry says {endpoint.State})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  failed  {endpoint.Transport,-9} (registry says {endpoint.State}): {ex.Message}");
        }
    }
    return 0;
}

// --- the full connect/release orchestration, without the GUI ------------------------------------
if (action is "--flow-connect" or "--flow-release")
{
    var flowConfig = PodGateConfig.Load();
    var flow = new ConnectFlow(flowConfig, message => Console.WriteLine($"  {message}"));
    var report = new Progress<ConnectProgress>(p => Console.WriteLine($"  [{p.Percent,3}%] {p.Phase}"));

    FlowResult outcome = action == "--flow-connect"
        ? await flow.ConnectAsync(report)
        : await flow.ReleaseAsync(report);

    Console.WriteLine($"{(outcome.Ok ? "PASS" : "FAIL")}  {outcome.Message} in {outcome.Elapsed.TotalSeconds:N1}s");
    QuickState after = QuickState.Read(flowConfig.Address);
    Console.WriteLine($"      state now: blocked={after.Blocked} connected={after.Connected}");
    return outcome.Ok ? 0 : 1;
}

// --- pipe client verbs: talk to the service instead of touching the device directly ---------------
if (action is "--status" or "--block" or "--unblock" or "--version")
{
    PodGateVerb verb = action switch
    {
        "--block" => PodGateVerb.Block,
        "--unblock" => PodGateVerb.Unblock,
        "--version" => PodGateVerb.GetVersion,
        _ => PodGateVerb.GetStatus,
    };
    var started = Stopwatch.StartNew();
    PodGateResponse reply = PipeClient.Send(verb);
    started.Stop();
    Console.WriteLine($"verb {verb} -> ok={reply.Ok} in {started.ElapsedMilliseconds} ms (elevated caller={elevated})");
    if (reply.Error is not null) Console.WriteLine($"  error:   {reply.Error}");
    if (reply.Version is not null) Console.WriteLine($"  version: {reply.Version}");
    if (reply.Address is not null) Console.WriteLine($"  device:  {reply.DeviceName} [{reply.Address}] blocked={reply.Blocked} connected={reply.Connected}");
    if (reply.Seconds > 0) Console.WriteLine($"  service took {reply.Seconds * 1000:N0} ms");
    return reply.Ok ? 0 : 1;
}

string address;
try
{
    address = PodGateConfig.ResolveAddress();
}
catch (Exception ex)
{
    Console.WriteLine($"FAIL  resolve address: {ex.Message}");
    return 1;
}

BtDevice? device = BtNative.FindPairedDevice(address);
Console.WriteLine($"device        {device?.Name ?? "(not paired)"} [{address}]");
string lastSeen = device?.LastSeen is { } seen
    ? $"{(DateTime.UtcNow - seen).TotalMinutes:N1} min ago ({seen.ToLocalTime():HH:mm:ss})"
    : "never";
Console.WriteLine($"              connected={device?.Connected}  services={device?.InstalledServices.Count}  lastSeen={lastSeen}");

string? rootInstanceId = DeviceNodes.FindRootInstanceId(address);
Console.WriteLine($"root node     {rootInstanceId ?? "(none)"}");
Console.WriteLine($"blocked       {DeviceNodes.IsBlocked(address)}   configFlags={FormatFlags(rootInstanceId)}");

Guid? container = DeviceNodes.GetContainerId(address);
Console.WriteLine($"container     {container?.ToString() ?? "(not readable)"}");

IReadOnlyList<AudioEndpoint> endpoints = container is null ? [] : AudioEndpoints.ForContainer(container.Value);
Console.WriteLine($"endpoints     {endpoints.Count}");
foreach (AudioEndpoint endpoint in endpoints)
{
    Console.WriteLine($"              {endpoint.Flow,-7} {endpoint.Transport,-9} {endpoint.State,-10} {endpoint.FriendlyName}");
}

// --- test 3: default endpoint switching from a plain user process ---------------------------------
string? currentRender = AudioPolicy.GetDefault(AudioFlow.Render, AudioRole.Console);
Console.WriteLine();
Console.WriteLine($"TEST 3  IPolicyConfig unelevated={!elevated}");
Console.WriteLine($"        current default render: {currentRender}");
if (currentRender is not null)
{
    try
    {
        AudioPolicy.SetDefaultForRole(currentRender, AudioRole.Console);   // no-op: sets it to itself
        bool unchanged = AudioPolicy.GetDefault(AudioFlow.Render, AudioRole.Console) == currentRender;
        Console.WriteLine($"        PASS  round-trip accepted, default unchanged={unchanged}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"        FAIL  {ex.Message}");
    }
}

// --- test 2: KS filter access from a plain user process -------------------------------------------
Console.WriteLine();
Console.WriteLine($"TEST 2  KS filter open unelevated={!elevated}");
// The kernel-streaming filter only exists while the endpoint does. After a boot with the device
// blocked every endpoint is NotPresent, so there is nothing to open and that is not a failure.
AudioEndpoint? withFilter = endpoints.FirstOrDefault(e => e.FilterPath is not null && e.State != EndpointState.NotPresent);
if (withFilter?.FilterPath is null)
{
    Console.WriteLine($"        SKIP  no endpoint is present right now (states: {string.Join(", ", endpoints.Select(e => e.State))})");
    Console.WriteLine("              connect once, or unblock, to make the filter testable");
}
else
{
    Console.WriteLine($"        path  {withFilter.FilterPath}");
    try
    {
        KsBtAudio.Probe(withFilter.FilterPath);
        Console.WriteLine("        PASS  handle opened (nothing sent)");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"        FAIL  {ex.Message}");
    }
}

// --- test 1: does the node change stick, byte for byte -------------------------------------------
if (action is "--disable" or "--enable")
{
    bool enable = action == "--enable";
    Console.WriteLine();
    Console.WriteLine($"TEST 1  device node {(enable ? "enable" : "disable")}");
    if (!elevated)
    {
        Console.WriteLine("        SKIP  needs an elevated prompt");
        return 0;
    }
    if (rootInstanceId is null)
    {
        Console.WriteLine("        FAIL  no root device node");
        return 1;
    }

    var stopwatch = Stopwatch.StartNew();
    try
    {
        string how = DeviceNodes.SetEnabled(rootInstanceId, enable);
        stopwatch.Stop();
        Console.WriteLine($"        PASS  via {how} in {stopwatch.Elapsed.TotalMilliseconds:N0} ms");
        Console.WriteLine($"        configFlags now {FormatFlags(rootInstanceId)}, blocked={DeviceNodes.IsBlocked(address)}");
        Console.WriteLine($"        expected: blocked=True after --disable, False after --enable, and the flag survives a reboot");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"        FAIL  {ex.Message}");
        return 1;
    }
}

return 0;

static string FormatFlags(string? instanceId)
{
    if (instanceId is null) return "(none)";
    int? flags = DeviceNodes.ReadConfigFlags(instanceId);
    return flags is null ? "(unset)" : $"0x{flags:X}";
}
