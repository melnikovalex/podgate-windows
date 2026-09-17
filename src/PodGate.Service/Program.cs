using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting.WindowsServices;
using PodGate.Core;
using PodGate.Service;

// PodGate.Service runs as LocalSystem and owns exactly one thing: whether the AirPods' Bluetooth
// device node is enabled. It has no UI and no audio code on purpose - it has no user session, and
// default devices and media controls belong to the logged-on user.

if (args.Contains("--restore-stock"))
{
    // Used by the uninstaller before it deletes anything, so a removal always leaves stock Windows, and by
    // a person when the app is broken (AGENTS rule 1). This binary is a WinExe so the uninstaller does not
    // flash a console; borrowing the caller's console gives a person their output back, and gives the
    // installer nothing to show.
    AttachConsole(-1);   // ATTACH_PARENT_PROCESS
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

// Before anything reads config.json: only administrators may write the file the service takes its target
// device from. %ProgramData% lets ordinary users create files by default.
DataDirSecurity.Apply();

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

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool AttachConsole(int processId);
