using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using PodGate.App.Hud;
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
    private readonly PodGateConfig _config;
    private readonly NotifyIcon _icon = new();
    private readonly HotkeyWindow _hotkeys = new();
    private readonly Dictionary<int, Func<Task>> _hotkeyActions = [];
    private readonly Timer _stateTimer = new() { Interval = 3000 };
    private readonly ToolStripMenuItem _statusItem = new("PodGate") { Enabled = false };
    private readonly SemaphoreSlim _busy = new(1, 1);

    // iOS dark-mode system colours: label white, systemPurple, systemGray.
    private static readonly Color White = Color.FromArgb(255, 245, 245, 247);
    private static readonly Color Purple = Color.FromArgb(255, 191, 90, 242);
    private static readonly Color Gray = Color.FromArgb(255, 142, 142, 147);

    private HudWindow? _hud;
    private string _lastState = "";
    // Only this process knows how the AirPods were connected; after a restart of the app it assumes Full.
    private ConnectMode _connectedMode = ConnectMode.Full;

    public TrayApp(PodGateConfig config)
    {
        _config = config;

        _icon.Icon = MakeIcon(TrayState.Unknown);
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

        RegisterHotkeys();

        _stateTimer.Tick += (_, _) => RefreshState();
        _stateTimer.Start();
        RefreshState();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Connect / Disconnect", _config.Hotkeys.Toggle, ToggleAsync));
        menu.Items.Add(Item("Connect", _config.Hotkeys.Connect, ConnectAsync));
        menu.Items.Add(Item("Connect music", _config.Hotkeys.ConnectMusic, ConnectMusicAsync));
        menu.Items.Add(Item("Disconnect", _config.Hotkeys.Release, ReleaseAsync));
        menu.Items.Add(new ToolStripSeparator());
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
        RunAsync(flow => flow.ConnectAsync(Progress()), ConnectMode.Full, "Connecting...", White, connects: true);

    private Task ConnectMusicAsync() =>
        RunAsync(flow => flow.ConnectAsync(Progress()), ConnectMode.Music, "Connecting music...", Purple, connects: true);

    private Task ReleaseAsync() =>
        RunAsync(flow => flow.ReleaseAsync(Progress()), _config.Mode, "Disconnecting...", Gray, connects: false);

    private async Task RunAsync(Func<ConnectFlow, Task<FlowResult>> action, ConnectMode mode, string title, Color color, bool connects)
    {
        // One at a time: overlapping connect and release runs can leave the AirPods in Hands-Free-only mode.
        if (!await _busy.WaitAsync(0))
        {
            AppLog.Write("ignored: another action is already running");
            return;
        }

        try
        {
            PodGateConfig config = PodGateConfig.Load();
            config.Mode = mode;
            var flow = new ConnectFlow(config, AppLog.Write);

            ShowHud(title, color);
            FlowResult result = await action(flow);
            if (connects) _connectedMode = mode;
            _lastState = "";   // the icon colour depends on the mode, not only on the state
            AppLog.Write($"{result.Message} in {result.Elapsed.TotalSeconds:N1}s");
            FinishHud(result.Message);
            RefreshState();
        }
        catch (Exception ex)
        {
            AppLog.Write($"ERROR: {ex.Message}");
            FinishHud("Something went wrong");
        }
        finally
        {
            _busy.Release();
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

    // --- hotkeys ---------------------------------------------------------------------------------

    private void RegisterHotkeys()
    {
        var wanted = new (string Text, Func<Task> Action)[]
        {
            (_config.Hotkeys.Toggle, ToggleAsync),
            (_config.Hotkeys.Connect, ConnectAsync),
            (_config.Hotkeys.Release, ReleaseAsync),
            (_config.Hotkeys.ConnectMusic, ConnectMusicAsync),
        };

        int id = 1;
        var failed = new List<string>();
        foreach ((string text, Func<Task> action) in wanted)
        {
            if (Hotkey.Parse(text) is not { } parsed) continue;
            if (_hotkeys.Register(id, parsed.Modifiers, parsed.Key))
            {
                _hotkeyActions[id] = action;
                id++;
            }
            else
            {
                failed.Add(text);
            }
        }

        if (failed.Count > 0)
        {
            AppLog.Write($"hotkeys already taken: {string.Join(", ", failed)}");
            _icon.ShowBalloonTip(5000, "PodGate", $"Hotkey already taken by another program: {string.Join(", ", failed)}", ToolTipIcon.Warning);
        }

        _hotkeys.HotkeyPressed += (_, e) =>
        {
            if (_hotkeyActions.TryGetValue(e.Id, out Func<Task>? action)) _ = action();
        };
    }

    // --- state -----------------------------------------------------------------------------------

    private enum TrayState
    {
        Unknown,
        Blocked,
        Connected,
    }

    private void RefreshState()
    {
        string label;
        TrayState state;
        try
        {
            var quick = QuickState.Read(_config.Address);
            state = quick.Connected ? TrayState.Connected : quick.Blocked ? TrayState.Blocked : TrayState.Unknown;
            label = quick.Connected ? (_connectedMode == ConnectMode.Music ? "Connected (music)" : "Connected")
                                    : "Disconnected";
            _statusItem.Text = $"{quick.Name ?? "AirPods"} : {label}";
        }
        catch (Exception ex)
        {
            state = TrayState.Unknown;
            label = "state unavailable";
            _statusItem.Text = label;
            AppLog.Write($"state read failed: {ex.Message}");
        }

        if (label == _lastState) return;
        _lastState = label;

        Icon? old = _icon.Icon;
        _icon.Icon = MakeIcon(state);
        old?.Dispose();
        _icon.Text = $"PodGate - {_statusItem.Text}";
    }

    /// <summary>
    /// Drawn rather than shipped: one AirPod on a dark disc, so it reads on light and dark taskbars.
    /// White when connected, purple when connected in music mode, grey otherwise.
    /// </summary>
    private Icon MakeIcon(TrayState state)
    {
        Color pod = state != TrayState.Connected ? Gray : _connectedMode == ConnectMode.Music ? Purple : White;

        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var disc = new SolidBrush(Color.FromArgb(255, 28, 28, 30))) g.FillEllipse(disc, 0, 0, 32, 32);

            using var brush = new SolidBrush(pod);
            g.FillEllipse(brush, 8, 4, 14, 13);   // the bud
            using var stem = new System.Drawing.Drawing2D.GraphicsPath();
            stem.AddArc(16, 10, 6, 6, 180, 180);
            stem.AddArc(16, 20, 6, 6, 0, 180);
            stem.CloseFigure();
            g.FillPath(brush, stem);               // the stem, rounded at both ends
        }

        IntPtr handle = bitmap.GetHicon();
        using var temp = Icon.FromHandle(handle);
        return (Icon)temp.Clone();
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
