using Microsoft.Extensions.Hosting.WindowsServices;
using PodGate.Core;
using PodGate.Service;

// PodGate.Service runs as LocalSystem and owns exactly one thing: whether the AirPods' Bluetooth
// device node is enabled. It has no UI and no audio code on purpose - it has no user session, and
// default devices and media controls belong to the logged-on user.

if (args.Contains("--restore-stock"))
{
    // Used by the uninstaller before it deletes anything, so a removal always leaves stock Windows.
    try
    {
        new BlockController(Console.WriteLine).RestoreStock();
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"restore failed: {ex.Message}");
        return 1;
    }
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "PodGate");
builder.Services.AddSingleton<BlockService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<BlockService>());
builder.Services.AddHostedService<PipeServer>();

builder.Logging.ClearProviders();
builder.Logging.AddProvider(new FileLoggerProvider(Paths.LogFile("service")));
if (!WindowsServiceHelpers.IsWindowsService()) builder.Logging.AddSimpleConsole();

await builder.Build().RunAsync();
return 0;
