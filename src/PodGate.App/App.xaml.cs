using System.IO;
using System.Windows;
using PodGate.Core;
using Application = System.Windows.Application;

namespace PodGate.App;

public partial class App : Application
{
    private const string OpenSettingsSignal = @"Local\PodGate.OpenSettings";

    private static Mutex? _singleInstance;
    private EventWaitHandle? _openSettings;
    private TrayApp? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The elevated copy started by Settings or setup: save config.json and leave, no tray, no window.
        if (e.Args.Contains(ElevatedConfig.Verb))
        {
            Shutdown(ElevatedConfig.Apply(e.Args));
            return;
        }

        UserSettings settings = UserSettings.Load();
        bool autostart = e.Args.Contains("--autostart");
        if (autostart && !settings.StartWithWindows)
        {
            Shutdown();
            return;
        }

        // One tray icon per session. Starting PodGate again (Start menu) opens Settings in the running copy.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\PodGate.App", out bool first);
        if (!first)
        {
            if (!autostart && EventWaitHandle.TryOpenExisting(OpenSettingsSignal, out EventWaitHandle? signal))
            {
                signal.Set();
                signal.Dispose();
            }
            Shutdown();
            return;
        }

        AppLog.Write("app started");
        _tray = new TrayApp(PodGateConfig.Load(), settings);
        ListenForSecondStart();

        if (!settings.SetupCompleted) _tray.OpenSetup(changeDevice: false);
    }

    private void ListenForSecondStart()
    {
        _openSettings = new EventWaitHandle(false, EventResetMode.AutoReset, OpenSettingsSignal);
        var thread = new Thread(() =>
        {
            while (_openSettings.WaitOne())
            {
                Dispatcher.BeginInvoke(() => _tray?.OpenSettings());
            }
        })
        {
            IsBackground = true,
            Name = "PodGate second start",
        };
        thread.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstance?.Dispose();
        if (_tray is not null) AppLog.Write("app exited");
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
