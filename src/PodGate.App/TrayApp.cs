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
    private readonly SemaphoreSlim _busy = new(1, 1);

    private HudWindow? _hud;
    private SetupWindow? _setup;
    private SettingsWindow? _settings;
    private string _lastState = "";
    // Only this process knows how the AirPods were connected; after a restart of the app it assumes Full.
    private ConnectMode _connectedMode = ConnectMode.Full;

    public TrayApp(PodGateConfig config, UserSettings settings)
    {
        _hotkeys = new HotkeyManager(settings.EffectiveHotkeys(config));

        _icon.Icon = TrayIcons.Draw(TrayIcons.Gray);
        _icon.Text = "PodGate";
        _icon.Visible = true;
        _icon.ContextMenuStrip = BuildMenu();
        // Left click opens the same menu as right click; nothing is triggered by a click on the icon itself,
        // so a stray click can never connect or disconnect. WinForms only wires the menu to the right button.
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.Invoke(_icon, null);
            }
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

        _stateTimer.Tick += (_, _) => RefreshState();
        _stateTimer.Start();
        RefreshState();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
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
        menu.Items.Add(Item("Set up again...", null, () =>
        {
            OpenSetup(changeDevice: false);
            return Task.CompletedTask;
        }));
        menu.Items.Add(Item("Open log", null, () =>
        {
            Open(Paths.LogFile("app"));
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
            _lastState = "";
            RefreshState();
        };
        _setup.Show();
        _setup.Activate();
    }

    public void OpenSettings()
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
        _settings = new SettingsWindow(_hotkeys);
        _settings.ChooseOtherRequested += () => OpenSetup(changeDevice: true);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
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
        RunAsync(flow => flow.ConnectAsync(Progress()), ConnectMode.Full, "AirPods: connecting...", TrayIcons.White, connects: true);

    private Task ConnectMusicAsync() =>
        RunAsync(flow => flow.ConnectAsync(Progress()), ConnectMode.Music, "AirPods: connecting music...", TrayIcons.Purple, connects: true);

    private Task ReleaseAsync() =>
        RunAsync(flow => flow.ReleaseAsync(Progress()), PodGateConfig.Load().Mode, "AirPods: disconnecting...", TrayIcons.Gray, connects: false);

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

    private async Task RunAsync(Func<ConnectFlow, Task<FlowResult>> action, ConnectMode mode, string title, Color color, bool connects)
    {
        try
        {
            await RunExclusiveAsync(async () =>
            {
                PodGateConfig config = PodGateConfig.Load();
                config.Mode = mode;
                var flow = new ConnectFlow(config, AppLog.Write);

                ShowHud(title, color);
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

    private void ShowServiceProblem(string? error)
    {
        AppLog.Write($"service unavailable: {error}");
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
        _hud.Closed += (_, _) => _hud = null;
        _hud.Show();
    });

    private void FinishHud(string message) => Application.Current.Dispatcher.Invoke(() =>
        _hud?.Finish(message, TimeSpan.FromMilliseconds(900)));

    // --- state -----------------------------------------------------------------------------------

    private void RefreshState()
    {
        string label;
        Color pod = TrayIcons.Gray;
        try
        {
            var quick = QuickState.Read(PodGateConfig.Load().Address);
            if (quick.Connected) pod = _connectedMode == ConnectMode.Music ? TrayIcons.Purple : TrayIcons.White;
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
        _icon.Icon = TrayIcons.Draw(pod);
        old?.Dispose();
        _icon.Text = $"PodGate - {_statusItem.Text}";
    }

    public void Dispose()
    {
        _stateTimer.Stop();
        _stateTimer.Dispose();
        _hotkeys.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _busy.Dispose();
    }
}
