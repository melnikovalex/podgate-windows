using System.IO;
using System.Windows;
using PodGate.Core;
using Application = System.Windows.Application;

namespace PodGate.App;

public partial class App : Application
{
    private static Mutex? _singleInstance;
    private TrayApp? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One tray icon per session. A second copy would fight over the hotkeys and show two cards.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\PodGate.App", out bool first);
        if (!first)
        {
            Shutdown();
            return;
        }

        AppLog.Write("app started");
        _tray = new TrayApp(PodGateConfig.Load());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstance?.Dispose();
        AppLog.Write("app exited");
        base.OnExit(e);
    }
}

/// <summary>
/// One timestamped line per event, the same shape as the service log, so both can be read side by side.
/// </summary>
public static class AppLog
{
    private static readonly Lock Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Paths.UserDataDir);
                File.AppendAllLines(Paths.LogFile("app"), [$"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz}  {message}"]);
            }
            catch (IOException)
            {
                // Logging must never break the app.
            }
        }
    }
}
