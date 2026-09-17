using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PodGate.App;
using PodGate.App.Ui;
using PodGate.Core;

// Renders every setup page and the settings window with sample data into PNGs.
// Usage: PodGate.UiSnapshots <output folder>. Reads nothing it writes back: no settings, no config.

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string output = args.Length > 0 ? args[0] : Path.Combine(Environment.CurrentDirectory, "snapshots");
        bool light = args.Contains("--light");
        Directory.CreateDirectory(output);

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PodGate;component/Ui/Theme.xaml"),
        });

        // Light or dark with a fixed accent, so the snapshots do not change with the machine's settings.
        AppTheme.Apply(light, System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4));

        using var hotkeys = new HotkeyManager(new HotkeyConfig());
        DeviceChoice[] devices =
        [
            new() { Address = "AABBCCDDEE01", Name = "AirPods Pro", Connected = true, IsAppleAudio = true },
            new() { Address = "AABBCCDDEE02", Name = "AirPods (3rd generation)", Connected = false, IsAppleAudio = true },
            new() { Address = "AABBCCDDEE03", Name = "Headset", Connected = false, IsAppleAudio = false },
            new() { Address = "AABBCCDDEE04", Name = "Mouse", Connected = true, IsAppleAudio = false },
        ];

        var setup = new SetupWindow(hotkeys, _ => Task.FromResult(true), changeDevice: false)
        {
            SampleDevices = devices,
            SaveProgress = false,
        };
        Show(setup);
        Save(setup, output, "1-welcome");

        setup.ShowPageForSnapshot(SetupWindow.Page.Choose);
        Save(setup, output, "2-choose");

        setup.SampleDevices = devices.Where(d => !d.IsAppleAudio).ToList();
        setup.ShowPageForSnapshot(SetupWindow.Page.Choose);
        Save(setup, output, "2b-no-airpods");
        setup.SampleDevices = devices;
        setup.ShowPageForSnapshot(SetupWindow.Page.Choose);

        setup.ShowPageForSnapshot(SetupWindow.Page.Test);
        Save(setup, output, "3-test-start");
        setup.Steps[0].Done(TimeSpan.FromSeconds(0.1));
        setup.Steps[1].Done(TimeSpan.FromSeconds(3.4));
        setup.Steps[2].Done();
        setup.Steps[3].Running();
        Save(setup, output, "3-test-running");

        setup.ShowPageForSnapshot(SetupWindow.Page.Test);
        setup.Steps[0].Done(TimeSpan.FromSeconds(0.1));
        setup.Steps[1].Failed("The AirPods didn’t answer within 12 seconds", TimeSpan.FromSeconds(12));
        setup.ShowTestProblemForSnapshot("Are they in the closed case or out of range?",
            "Take them out of the case, keep them near this PC, and try again. Your phone can keep using them in the meantime.");
        Save(setup, output, "3b-test-failed");

        setup.ShowPageForSnapshot(SetupWindow.Page.Shortcuts);
        setup.SetupToggleRow.ShowSample("works");
        setup.SetupMusicRow.ShowSample("saved");
        Save(setup, output, "4-shortcuts");

        setup.SetTestPassedForSnapshot(true);
        setup.ShowPageForSnapshot(SetupWindow.Page.Done);
        setup.DoneToggleRow.ShowSample("idle");
        setup.DoneMusicRow.ShowSample("idle");
        Save(setup, output, "5-done");
        setup.Close();

        var change = new SetupWindow(hotkeys, _ => Task.FromResult(true), changeDevice: true) { SampleDevices = devices, SaveProgress = false };
        Show(change);
        Save(change, output, "6-change-airpods");
        change.Close();

        var settings = new SettingsWindow(hotkeys, () => "L 60%⚡ · R 70%⚡ · Case 90%");
        Show(settings);
        settings.ShowDeviceForSnapshot("AirPods Pro", "Connected", connected: true);
        settings.Rows[0].ShowSample("works");
        settings.Rows[1].ShowSample("saved");
        settings.Rows[2].ShowSample("idle");
        settings.Rows[3].ShowSample("idle");
        Save(settings, output, "7-settings");
        settings.ShowAlreadyRunningHint();
        Save(settings, output, "7b-settings-already-running");
        settings.Close();

        foreach (AudioWindow.Mode mode in Enum.GetValues<AudioWindow.Mode>())
        {
            var audio = new AudioWindow(mode);
            Show(audio);
            Save(audio, output, $"8-audio-{mode.ToString().ToLowerInvariant()}");
            audio.Close();
        }

        Console.WriteLine($"snapshots written to {output}");
        return 0;
    }

    private static void Show(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
    }

    private static void Save(Window window, string folder, string name)
    {
        window.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();

        var root = (Visual)VisualTreeHelper.GetChild(window, 0);
        var element = (FrameworkElement)root;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(file);
    }
}
