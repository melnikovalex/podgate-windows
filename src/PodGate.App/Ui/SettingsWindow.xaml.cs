using System.Windows;
using PodGate.Core;
using PodGate.Core.Bluetooth;

namespace PodGate.App.Ui;

/// <summary>
/// AirPods, autostart, connect-on-startup and the shortcuts. Everything applies immediately: per-user
/// settings are just saved, the machine-wide one goes through Windows' permission prompt.
/// </summary>
public partial class SettingsWindow : DarkWindow
{
    private bool _loading;

    private readonly Func<string>? _battery;

    /// <param name="battery">The tray's current battery reading, shown next to the device.</param>
    public SettingsWindow(HotkeyManager hotkeys, Func<string>? battery = null)
    {
        InitializeComponent();
        _battery = battery;
        foreach (HotkeyRow row in new[] { ToggleRow, MusicRow, ConnectRow, ReleaseRow }) row.Attach(hotkeys);

        ChooseOtherButton.Click += (_, _) => ChooseOtherRequested?.Invoke();
        RunSetupButton.Click += (_, _) => SetupRequested?.Invoke();
        ConnectAudioLink.Click += (_, _) => ShowAudio(AudioWindow.Mode.Connect);
        MusicAudioLink.Click += (_, _) => ShowAudio(AudioWindow.Mode.Music);
        DisconnectAudioLink.Click += (_, _) => ShowAudio(AudioWindow.Mode.Disconnect);
        OpenLogButton.Click += (_, _) => LogRequested?.Invoke();
        DoneButton.Click += (_, _) => Close();
        StartWithWindowsBox.Click += (_, _) => Save(settings => settings.StartWithWindows = StartWithWindowsBox.IsChecked == true);
        LowBatteryBox.Click += (_, _) => Save(settings => settings.LowBatteryWarnings = LowBatteryBox.IsChecked == true);
        EarDetectionBox.Click += (_, _) => Save(settings => settings.EarDetection = EarDetectionBox.IsChecked == true);
        ConnectOnStartupBox.Click += async (_, _) =>
        {
            if (_loading) return;
            bool wanted = ConnectOnStartupBox.IsChecked == true;
            ConnectOnStartupBox.IsEnabled = false;
            GeneralError.Visibility = Visibility.Collapsed;
            bool saved = await ElevatedConfig.SaveAsync(connectOnStartup: wanted);
            ConnectOnStartupBox.IsEnabled = true;
            if (!saved)
            {
                ConnectOnStartupBox.IsChecked = !wanted;
                GeneralError.Visibility = Visibility.Visible;
            }
        };
        Closing += (_, _) =>
        {
            foreach (HotkeyRow row in new[] { ToggleRow, MusicRow, ConnectRow, ReleaseRow }) row.Cancel();
        };

        Reload();
    }

    /// <summary>"Choose other AirPods": the tray closes this and opens setup at the device step.</summary>
    public event Action? ChooseOtherRequested;

    /// <summary>"Run setup again": the whole thing, from the welcome page.</summary>
    public event Action? SetupRequested;

    /// <summary>"Open log": the app's own log file.</summary>
    public event Action? LogRequested;

    internal HotkeyRow[] Rows => [ToggleRow, MusicRow, ConnectRow, ReleaseRow];

    public void Reload()
    {
        _loading = true;
        PodGateConfig config = PodGateConfig.Load();
        UserSettings settings = UserSettings.Load();
        StartWithWindowsBox.IsChecked = settings.StartWithWindows;
        LowBatteryBox.IsChecked = settings.LowBatteryWarnings;
        EarDetectionBox.IsChecked = settings.EarDetection;
        ConnectOnStartupBox.IsChecked = config.ConnectOnStartup;

        try
        {
            QuickState state = QuickState.Read(config.Address);
            DeviceName.Text = state.Name ?? config.DeviceName;
            string address = string.Join(":", Enumerable.Range(0, 6).Select(i => state.Address.Substring(i * 2, 2)));
            DeviceState.Text = $"{address} · {(state.Connected ? "Connected" : "Disconnected")}";
            DeviceGlyph.Foreground = (System.Windows.Media.Brush)FindResource(state.Connected ? "PodOn" : "PodOff");
            DeviceBattery.Text = _battery?.Invoke() ?? "";
        }
        catch (Exception ex)
        {
            DeviceName.Text = "No AirPods chosen";
            DeviceState.Text = "Choose the pair PodGate should manage";
            AppLog.Write($"settings: {ex.Message}");
        }
        _loading = false;
    }

    private void ShowAudio(AudioWindow.Mode mode)
    {
        var window = new AudioWindow(mode) { Owner = this };
        window.ShowDialog();
    }

    private static void Save(Action<UserSettings> change)
    {
        UserSettings settings = UserSettings.Load();
        change(settings);
        settings.Save();
    }

    /// <summary>For the UI snapshot tool.</summary>
    internal void ShowDeviceForSnapshot(string name, string subtitle)
    {
        DeviceName.Text = name;
        DeviceState.Text = subtitle;
    }
}
