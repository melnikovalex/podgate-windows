using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using PodGate.App.Hud;
using PodGate.App.Ui;
using PodGate.Core;
using PodGate.Core.Bluetooth;
using PodGate.Core.Ipc;
using Application = System.Windows.Application;
using Timer = System.Windows.Forms.Timer;

namespace PodGate.App;

/// <summary>
/// The tray icon and everything the user touches. Unelevated: it asks the service for the privileged
/// part and does the audio itself, because audio belongs to this session (ADR-002).
/// </summary>
public sealed class TrayApp : IDisposable
{
    private readonly NotifyIcon _icon = new();
    private readonly HotkeyManager _hotkeys;
    private readonly Timer _stateTimer = new() { Interval = 3000 };
    private readonly ToolStripMenuItem _statusItem = new("PodGate") { Enabled = false };
    private readonly ToolStripMenuItem _batteryItem = new("Battery unknown") { Enabled = false };
    private readonly BatteryMonitor _battery;
    private readonly SemaphoreSlim _busy = new(1, 1);

    /// <summary>True while the balloon on screen is the offer to connect nearby AirPods.</summary>
    private bool _balloonConnects;

    /// <summary>
    /// When the second half of a double click landed. Windows sends a button-up for both clicks, so
    /// WinForms raises MouseClick twice and the second one would start the menu timer again straight after
    /// MouseDoubleClick had cancelled it - which is why the menu still appeared on a double click.
    /// </summary>
    private DateTime _doubleClicked = DateTime.MinValue;

    /// <summary>
    /// Holds a left click back long enough to see whether a second one follows. Without it a double click
    /// never reaches us: the first click would open the menu, the menu would take the mouse, and the second
    /// click would only dismiss it. The wait is Windows' own double-click time, so it matches every other
    /// tray icon on the machine.
    /// </summary>
    private readonly Timer _clickTimer = new() { Interval = Math.Max(SystemInformation.DoubleClickTime, 200) };

    private HudWindow? _hud;
    private SetupWindow? _setup;
    private SettingsWindow? _settings;
    private string _lastState = "";
    // Only this process knows how the AirPods were connected; after a restart of the app it assumes Full.
    private ConnectMode _connectedMode = ConnectMode.Full;

    public TrayApp(PodGateConfig config, UserSettings settings)
    {
        _hotkeys = new HotkeyManager(settings.EffectiveHotkeys(config));

        _icon.Icon = TrayIcons.Draw(TrayState.Blocked);
        _icon.Text = "PodGate";
        _icon.Visible = true;
        _icon.ContextMenuStrip = BuildMenu();
        // Left click opens the same menu as right click; nothing is triggered by a click on the icon itself,
        // so a stray click can never connect or disconnect. WinForms only wires the menu to the right button.
        // Double click opens Settings, which is why the menu waits to see if a second click is coming.
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer.Stop();
            ShowMenu();
        };

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (DateTime.UtcNow - _doubleClicked < TimeSpan.FromMilliseconds(400)) return;   // the tail of a double click
            _clickTimer.Stop();
            _clickTimer.Start();
        };

        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _doubleClicked = DateTime.UtcNow;
            _clickTimer.Stop();
            OpenSettings();
        };

        IReadOnlyList<string> failed = _hotkeys.RegisterAll();
        if (failed.Count > 0)
        {
            AppLog.Write($"hotkeys already taken: {string.Join(", ", failed)}");
            _icon.ShowBalloonTip(5000, "PodGate", $"Shortcut already taken by another program: {string.Join(", ", failed)}", ToolTipIcon.Warning);
        }
        _hotkeys.Pressed += action => _ = action switch
        {
            HotkeyAction.Toggle => ToggleAsync(),
            HotkeyAction.Connect => ConnectAsync(),
            HotkeyAction.Release => ReleaseAsync(),
            HotkeyAction.ConnectMusic => ConnectMusicAsync(),
            _ => Task.CompletedTask,
        };
        _hotkeys.Changed += () =>
        {
            ContextMenuStrip old = _icon.ContextMenuStrip;
            _icon.ContextMenuStrip = BuildMenu();
            old.Dispose();
        };

        _battery = new BatteryMonitor(ListeningOnPods, IsConnected, (title, text) =>
        {
            _balloonConnects = false;
            _icon.ShowBalloonTip(6000, title, text, ToolTipIcon.Info);
        });

        // Only the "they are nearby" balloon acts on a click; every other one is just information, so the
        // flag is cleared whenever a different balloon is raised or this one goes away unclicked.
        _battery.ArrivedNearby += () => Application.Current.Dispatcher.Invoke(() =>
        {
            _balloonConnects = true;
            _icon.ShowBalloonTip(8000, $"{DeviceLabel()} are nearby", "Click here to connect them to this PC.", ToolTipIcon.Info);
        });

        _icon.BalloonTipClicked += (_, _) =>
        {
            if (!_balloonConnects) return;
            _balloonConnects = false;
            _ = ConnectAsync();
        };

        _icon.BalloonTipClosed += (_, _) => _balloonConnects = false;
        _battery.Changed += status => Application.Current.Dispatcher.Invoke(() =>
        {
            string text = BatteryMonitor.MenuText(status);
            if (text != _batteryItem.Text) AppLog.Write($"battery: {text} ({status?.ModelName ?? "no advertisement"})");
            _batteryItem.Text = text;
            _settings?.Reload();
            _hud?.SetBattery(status?.Lowest, text);
        });
        _battery.Start();

        _stateTimer.Tick += (_, _) => RefreshState();
        _stateTimer.Start();
        RefreshState();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(_batteryItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Connect / Disconnect", _hotkeys.Get(HotkeyAction.Toggle), ToggleAsync));
        menu.Items.Add(Item("Connect", _hotkeys.Get(HotkeyAction.Connect), ConnectAsync));
        menu.Items.Add(Item("Connect music", _hotkeys.Get(HotkeyAction.ConnectMusic), ConnectMusicAsync));
        menu.Items.Add(Item("Disconnect", _hotkeys.Get(HotkeyAction.Release), ReleaseAsync));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Settings...", null, () =>
        {
            OpenSettings();
            return Task.CompletedTask;
        }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Exit", null, () =>
        {
            Application.Current.Shutdown();
            return Task.CompletedTask;
        }));
        return menu;
    }

    private static ToolStripMenuItem Item(string text, string? shortcut, Func<Task> action)
    {
        var item = new ToolStripMenuItem(text);
        if (!string.IsNullOrWhiteSpace(shortcut)) item.ShortcutKeyDisplayString = shortcut;
        item.Click += (_, _) => _ = action();
        return item;
    }

    /// <summary>
    /// What the popup calls the device. PodGate can manage any paired Bluetooth audio device, and calling a
    /// Jabra headset "AirPods" would be plain wrong, so anything that is not an Apple device is just PodGate.
    /// </summary>
    private static string DeviceLabel()
    {
        try
        {
            return PodGateConfig.IsAppleDevice(PodGateConfig.ResolveAddress()) ? "AirPods" : "PodGate";
        }
        catch (Exception)
        {
            return "PodGate";
        }
    }

    /// <summary>True when sound is going to the AirPods right now: what ear detection acts on.</summary>
    private static bool ListeningOnPods()
    {
        try
        {
            Guid? container = PodGate.Core.Bluetooth.DeviceNodes.GetContainerId(PodGateConfig.ResolveAddress());
            if (container is null) return false;
            string? current = PodGate.Core.Audio.AudioPolicy.GetDefault(PodGate.Core.Audio.AudioFlow.Render, PodGate.Core.Audio.AudioRole.Console);
            return current is not null && PodGate.Core.Audio.AudioEndpoints.ForContainer(container.Value)
                .Any(endpoint => endpoint.EndpointId == current);
        }
        catch (Exception ex)
        {
            AppLog.Write($"could not tell where sound is going: {ex.Message}");
            return false;
        }
    }

    private static void Open(string path)
    {
        if (File.Exists(path)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    // --- windows ---------------------------------------------------------------------------------

    public void OpenSetup(bool changeDevice)
    {
        _settings?.Close();
        if (_setup is not null)
        {
            _setup.Activate();
            return;
        }
        _setup = new SetupWindow(_hotkeys, RunExclusiveAsync, changeDevice);
        _setup.Closed += (_, _) =>
        {
            _setup = null;
            _battery.Retarget();   // a different pair means a different model to listen for
            _lastState = "";
            RefreshState();
        };
        _setup.Show();
        _setup.Activate();
    }

    /// <param name="alreadyRunning">Opened because PodGate was started a second time: say where it lives.</param>
    private static void ShowMenuOn(NotifyIcon icon) =>
        typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.Invoke(icon, null);

    private void ShowMenu() => ShowMenuOn(_icon);

    public void OpenSettings(bool alreadyRunning = false)
    {
        if (_setup is not null)
        {
            _setup.Activate();
            return;
        }
        if (_settings is not null)
        {
            _settings.Activate();
            return;
        }
        _settings = new SettingsWindow(_hotkeys, () => BatteryMonitor.MenuText(_battery.Current));
        _settings.ChooseOtherRequested += () => OpenSetup(changeDevice: true);
        _settings.SetupRequested += () => OpenSetup(changeDevice: false);
        _settings.LogRequested += () => Open(Paths.LogFile("app"));
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
        if (alreadyRunning) _settings.ShowAlreadyRunningHint();
    }

    // --- actions ---------------------------------------------------------------------------------

    private async Task ToggleAsync()
    {
        PodGateResponse status = await PipeClient.SendAsync(PodGateVerb.GetStatus);
        if (!status.Ok)
        {
            ShowServiceProblem(status.Error);
            return;
        }
        if (status.Blocked) await ConnectAsync();
        else await ReleaseAsync();
    }

    private Task ConnectAsync() =>
        RunAsync(flow => flow.ConnectAsync(Progress()), ConnectMode.Full, $"{DeviceLabel()}: connecting...", TrayState.On, connects: true);

    private Task ConnectMusicAsync() =>
        RunAsync(flow => flow.ConnectAsync(Progress()), ConnectMode.Music, $"{DeviceLabel()}: connecting music...", TrayState.Music, connects: true);

    private Task ReleaseAsync() =>
        RunAsync(flow => flow.ReleaseAsync(Progress()), PodGateConfig.Load().Mode, $"{DeviceLabel()}: disconnecting...", TrayState.Blocked, connects: false);

    /// <summary>
    /// One action at a time: overlapping connect and release runs can leave the AirPods in Hands-Free-only
    /// mode. Setup's test runs through this too. False when something else is already running.
    /// </summary>
    private async Task<bool> RunExclusiveAsync(Func<Task> body)
    {
        if (!await _busy.WaitAsync(0))
        {
            AppLog.Write("ignored: another action is already running");
            return false;
        }
        try
        {
            await body();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write($"ERROR: {ex.Message}");
            throw;
        }
        finally
        {
            _busy.Release();
            _lastState = "";
            RefreshState();
        }
    }

    private async Task RunAsync(Func<ConnectFlow, Task<FlowResult>> action, ConnectMode mode, string title, TrayState state, bool connects)
    {
        try
        {
            await RunExclusiveAsync(async () =>
            {
                PodGateConfig config = PodGateConfig.Load();
                config.Mode = mode;
                var flow = new ConnectFlow(config, AppLog.Write);

                ShowHud(title, TrayIcons.Colour(state));
                FlowResult result = await action(flow);
                if (connects && result.Ok) _connectedMode = mode;
                AppLog.Write($"{result.Message} in {result.Elapsed.TotalSeconds:N1}s");
                FinishHud(result.Message);
            });
        }
        catch (Exception)
        {
            FinishHud("Something went wrong");
        }
    }

    /// <summary>Whether the AirPods are on this PC right now; unknown counts as not connected.</summary>
    private static bool IsConnected()
    {
        try
        {
            return QuickState.Read(PodGateConfig.Load().Address).Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ShowServiceProblem(string? error)
    {
        AppLog.Write($"service unavailable: {error}");
        _balloonConnects = false;
        _icon.ShowBalloonTip(5000, "PodGate", error ?? "The PodGate service is not running.", ToolTipIcon.Warning);
    }

    // --- HUD -------------------------------------------------------------------------------------

    private IProgress<ConnectProgress> Progress() => new Progress<ConnectProgress>(p =>
        Application.Current.Dispatcher.Invoke(() => _hud?.Update(p.Phase, p.Percent)));

    private void ShowHud(string title, Color color) => Application.Current.Dispatcher.Invoke(() =>
    {
        _hud?.Close();
        _hud = new HudWindow();
        _hud.SetTitle(title, System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B));
        _hud.SetBattery(_battery.Current?.Lowest, BatteryMonitor.MenuText(_battery.Current));
        _hud.Closed += (_, _) => _hud = null;
        _hud.Show();
    });

    private void FinishHud(string message) => Application.Current.Dispatcher.Invoke(() =>
        _hud?.Finish(message, TimeSpan.FromMilliseconds(900)));

    // --- state -----------------------------------------------------------------------------------

    private void RefreshState()
    {
        string label;
        TrayState state = TrayState.Blocked;
        try
        {
            var quick = QuickState.Read(PodGateConfig.Load().Address);
            if (quick.Connected) state = _connectedMode == ConnectMode.Music ? TrayState.Music : TrayState.On;
            label = quick.Connected ? (_connectedMode == ConnectMode.Music ? "Connected (music)" : "Connected")
                                    : "Disconnected";
            _statusItem.Text = $"{quick.Name ?? "AirPods"} : {label}";
        }
        catch (Exception ex)
        {
            label = "No AirPods chosen";
            _statusItem.Text = label;
            if (_lastState != label) AppLog.Write($"state read failed: {ex.Message}");
        }

        if (label == _lastState) return;
        _lastState = label;

        Icon? old = _icon.Icon;
        _icon.Icon = TrayIcons.Draw(state);
        old?.Dispose();
        _icon.Text = $"PodGate - {_statusItem.Text}";
    }

    public void Dispose()
    {
        _clickTimer.Dispose();
        _battery.Dispose();
        _stateTimer.Stop();
        _stateTimer.Dispose();
        _hotkeys.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _busy.Dispose();
    }
}
