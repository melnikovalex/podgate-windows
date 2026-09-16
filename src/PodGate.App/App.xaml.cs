using System.IO;
using System.Windows;
using PodGate.Core;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace PodGate.App;

public partial class App : Application
{
    private const string OpenSettingsSignal = @"Local\PodGate.OpenSettings";

    private static Mutex? _singleInstance;
    private EventWaitHandle? _openSettings;
    private TrayApp? _tray;
    private DateTime _lastReport = DateTime.MinValue;

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

        CatchEverything();
        Ui.AppTheme.Follow();   // light or dark, and the accent colour, as Windows has them
        AppLog.Write("app started");
        _tray = new TrayApp(PodGateConfig.Load(), settings);
        ListenForSecondStart();

        if (!settings.SetupCompleted) _tray.OpenSetup(changeDevice: false);
    }

    /// <summary>
    /// A bug in one window must not take the tray with it: anything unhandled is logged, shown once in a
    /// plain message box, and swallowed where the app can carry on. Without this, a mistake in a dialog
    /// closes PodGate with no trace and the AirPods keep whatever state they were left in.
    /// </summary>
    private void CatchEverything()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            Report(e.Exception, "A window ran into a problem");
            e.Handled = true;   // the tray, the hotkeys and the service connection are still fine
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error) Report(error, "PodGate has to close");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Write($"ERROR (background): {e.Exception.GetBaseException()}");
            e.SetObserved();
        };
    }

    private void Report(Exception error, string headline)
    {
        AppLog.Write($"ERROR: {error}");

        // One box at a time, and not again straight away: a repeating fault would otherwise bury the screen.
        if (DateTime.UtcNow - _lastReport < TimeSpan.FromSeconds(30)) return;
        _lastReport = DateTime.UtcNow;

        try
        {
            string message = string.Join(
                Environment.NewLine + Environment.NewLine,
                $"{headline}.",
                error.GetBaseException().Message,
                "The details are in the log: tray menu, Settings, Open log.");
            MessageBox.Show(message, "PodGate", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception)
        {
            // If even the message box fails, the log entry above is what is left.
        }
    }

    private void ListenForSecondStart()
    {
        _openSettings = new EventWaitHandle(false, EventResetMode.AutoReset, OpenSettingsSignal);
        var thread = new Thread(() =>
        {
            while (_openSettings.WaitOne())
            {
                Dispatcher.BeginInvoke(() => _tray?.OpenSettings(alreadyRunning: true));
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
