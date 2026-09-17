using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PodGate.Core;
using PodGate.Core.Audio;
using PodGate.Core.Bluetooth;
using Brush = System.Windows.Media.Brush;
using RadioButton = System.Windows.Controls.RadioButton;

namespace PodGate.App.Ui;

/// <summary>
/// First-start setup, and "Choose other AirPods" from Settings, which skips the welcome and the shortcuts.
/// Leaves the AirPods connected once the test passes: nobody finishes setup wanting them taken away.
/// </summary>
public partial class SetupWindow : DarkWindow
{
    internal enum Page
    {
        Welcome,
        Choose,
        Test,
        Shortcuts,
        Done,
    }

    private readonly Func<Func<Task>, Task<bool>> _exclusive;
    private readonly List<Page> _flow;
    private readonly HotkeyManager _hotkeys;
    private readonly List<TestStepRow> _steps = [];
    private int _index;
    private DeviceChoice? _selected;
    private bool _showOthers;
    private bool _testRunning;
    private bool _testPassed;
    private bool _connectFailed;
    private Action? _secondary;   // what the left footer button does on the current page

    /// <param name="hotkeys">Shortcut bindings, tested live on the shortcuts page.</param>
    /// <param name="exclusive">Runs the test under the tray's one-action-at-a-time lock; false when busy.</param>
    /// <param name="changeDevice">Opened from Settings: device, permission, test, done.</param>
    public SetupWindow(HotkeyManager hotkeys, Func<Func<Task>, Task<bool>> exclusive, bool changeDevice)
    {
        InitializeComponent();
        _exclusive = exclusive;
        _hotkeys = hotkeys;
        _flow = changeDevice
            ? [Page.Choose, Page.Test, Page.Done]
            : [Page.Welcome, Page.Choose, Page.Test, Page.Shortcuts, Page.Done];

        SetupToggleRow.Attach(hotkeys);
        SetupMusicRow.Attach(hotkeys);
        DoneToggleRow.Attach(hotkeys);
        DoneMusicRow.Attach(hotkeys);

        foreach (string step in new[] { "Disconnect from this PC", "Connect", "Switch sound to AirPods", "Switch call microphone", "Play a short sound" })
        {
            var row = new TestStepRow(step);
            if (_steps.Count > 0) TestSteps.Children.Add(new System.Windows.Shapes.Rectangle { Style = (Style)FindResource("RowSeparator") });
            TestSteps.Children.Add(row);
            _steps.Add(row);
        }

        BackButton.Click += (_, _) => _secondary?.Invoke();
        // Skips the test, not the rest of setup: jumping to the last page used to step over the
        // shortcuts page entirely, which is why the progress bar went from 2 straight to 4.
        SkipButton.Click += (_, _) => Go(_index + 1);
        NextButton.Click += async (_, _) => await NextAsync();
        ShowOthersLink.Click += (_, _) =>
        {
            _showOthers = !_showOthers;
            LoadDevices();
        };
        PairNewLink.Click += (_, _) => OpenBluetoothSettings();
        Activated += (_, _) =>
        {
            if (Current is Page.Choose && !_testRunning) LoadDevices();
        };
        Closing += (_, _) =>
        {
            SetupToggleRow.Cancel();
            SetupMusicRow.Cancel();
        };

        Go(0);
    }

    private Page Current => _flow[_index];

    /// <summary>Sample devices for the UI snapshot tool; null means read the paired devices.</summary>
    internal IReadOnlyList<DeviceChoice>? SampleDevices { get; set; }

    /// <summary>The snapshot tool turns this off so rendering the done page does not mark setup complete.</summary>
    internal bool SaveProgress { get; set; } = true;

    internal void SetTestPassedForSnapshot(bool passed) => _testPassed = passed;

    internal void ShowPageForSnapshot(Page page) => Go(_flow.IndexOf(page));

    internal IReadOnlyList<TestStepRow> Steps => _steps;

    internal void ShowTestProblemForSnapshot(string title, string text) => ShowTestProblem(title, text);

    // --- navigation --------------------------------------------------------------------------------

    /// <summary>The primary button's label lives in a panel, so screen readers need it set explicitly.</summary>
    private void SetNextText(string text)
    {
        NextText.Text = text;
        System.Windows.Automation.AutomationProperties.SetName(NextButton, text);
    }

    private void Go(int index)
    {
        _index = Math.Clamp(index, 0, _flow.Count - 1);
        Page page = Current;

        SkipButton.Visibility = Visibility.Collapsed;
        foreach (FrameworkElement element in new FrameworkElement[] { WelcomePage, ChoosePage, NoAirPodsPage, ShortcutsPage, TestPage, DonePage })
        {
            element.Visibility = Visibility.Collapsed;
        }

        // Step header: the welcome page has none, the rest count from the first step after it.
        int firstStep = _flow[0] == Page.Welcome ? 1 : 0;
        int total = _flow.Count - firstStep;
        StepHeader.Visibility = page == Page.Welcome ? Visibility.Collapsed : Visibility.Visible;
        WelcomeSpacer.Visibility = page == Page.Welcome ? Visibility.Visible : Visibility.Collapsed;
        int step = _index - firstStep + 1;
        Brush done = (Brush)FindResource("StepDone");
        Brush todo = (Brush)FindResource("Fill3");
        Segments.Children.Clear();
        for (int i = 0; i < total; i++)
        {
            Segments.Children.Add(new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 0, 6, 0),
                Background = i < step ? done : todo,
            });
        }
        StepText.Text = $"Step {step} of {total}";

        BackButton.Visibility = _index > 0 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.IsEnabled = true;
        BackButton.Content = "Back";
        _secondary = () => Go(_index - 1);
        FooterNote.Text = "";
        NextShield.Visibility = Visibility.Collapsed;
        NextExternal.Visibility = Visibility.Collapsed;
        NextButton.IsEnabled = true;

        switch (page)
        {
            case Page.Welcome:
                WelcomePage.Visibility = Visibility.Visible;
                FooterNote.Text = "Takes about a minute";
                SetNextText("Get started");
                break;

            case Page.Choose:
                LoadDevices();
                break;

            case Page.Shortcuts:
                ShortcutsPage.Visibility = Visibility.Visible;
                SetNextText("Next");
                break;

            case Page.Test:
                TestPage.Visibility = Visibility.Visible;
                ResetTest();
                SetNextText("Start test");
                SkipButton.Visibility = Visibility.Visible;
                break;

            case Page.Done:
                ShowDone();
                break;
        }
    }

    private async Task NextAsync()
    {
        switch (Current)
        {
            case Page.Choose when NoAirPodsPage.Visibility == Visibility.Visible:
                OpenBluetoothSettings();
                return;

            case Page.Choose when _selected is null:
                return;

            case Page.Choose:
                // Saving which pair to manage is the one administrator step, so it happens here, on the
                // page that chose it, instead of on a page of its own.
                if (NeedsElevation())
                {
                    NextButton.IsEnabled = false;
                    BackButton.IsEnabled = false;
                    bool saved = await ElevatedConfig.SaveAsync(address: _selected!.Address);
                    NextButton.IsEnabled = true;
                    BackButton.IsEnabled = true;
                    if (!saved)
                    {
                        PermissionError.Visibility = Visibility.Visible;
                        return;
                    }
                }
                break;

            case Page.Test when !_testPassed:
                await RunTestAsync();
                return;
        }

        if (Current == Page.Done)
        {
            Close();
            return;
        }
        Go(_index + 1);
    }

    // --- choose ------------------------------------------------------------------------------------

    private void LoadDevices()
    {
        IReadOnlyList<DeviceChoice> all = SampleDevices ?? DeviceChoice.ReadPaired();
        List<DeviceChoice> airPods = all.Where(d => d.IsAppleAudio).ToList();
        List<DeviceChoice> others = all.Where(d => !d.IsAppleAudio).ToList();

        if (airPods.Count == 0 && !_showOthers)
        {
            ChoosePage.Visibility = Visibility.Collapsed;
            NoAirPodsPage.Visibility = Visibility.Visible;
            BackButton.Visibility = Visibility.Collapsed;
            SetNextText("Open Bluetooth settings");
            NextExternal.Visibility = Visibility.Visible;
            NextButton.IsEnabled = true;
            BackButton.Content = "Search again";
            BackButton.Visibility = Visibility.Visible;
            _secondary = LoadDevices;
            return;
        }

        NoAirPodsPage.Visibility = Visibility.Collapsed;
        ChoosePage.Visibility = Visibility.Visible;
        BackButton.Content = "Back";
        BackButton.Visibility = _index > 0 ? Visibility.Visible : Visibility.Collapsed;
        _secondary = () => Go(_index - 1);
        SetNextText("Next");
        NextExternal.Visibility = Visibility.Collapsed;

        string configured = PodGateConfig.Load().Address;
        _selected = all.FirstOrDefault(d => d.Address == _selected?.Address)
                    ?? airPods.FirstOrDefault(d => string.Equals(d.Address, configured, StringComparison.OrdinalIgnoreCase))
                    ?? airPods.FirstOrDefault(d => d.Connected)
                    ?? airPods.FirstOrDefault();

        Fill(DeviceList, airPods);
        Fill(OtherList, _showOthers ? others : []);
        OthersLabel.Visibility = OthersBox.Visibility = _showOthers && others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowOthersLink.Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowOthersLink.Content = _showOthers
            ? "Hide other devices"
            : $"Show {others.Count} other paired device{(others.Count == 1 ? "" : "s")}";
        NextButton.IsEnabled = _selected is not null;

        // The administrator prompt only appears when the choice actually changes something.
        PermissionError.Visibility = Visibility.Collapsed;
        Visibility prompt = NeedsElevation() ? Visibility.Visible : Visibility.Collapsed;
        PermissionNote.Visibility = prompt;
        NextShield.Visibility = prompt;
    }

    private void Fill(StackPanel list, IReadOnlyList<DeviceChoice> devices)
    {
        list.Children.Clear();
        foreach (DeviceChoice device in devices)
        {
            if (list.Children.Count > 0) list.Children.Add(new System.Windows.Shapes.Rectangle { Style = (Style)FindResource("RowSeparator") });

            var text = new StackPanel { Margin = new Thickness(52, 10, 12, 10), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = device.Name, Style = (Style)FindResource("RowTitle") });
            text.Children.Add(new TextBlock { Text = device.Subtitle, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 2, 0, 0) });

            var content = new Grid();
            content.Children.Add(new AirPodGlyph
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(16, 0, 0, 0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Foreground = (Brush)FindResource(device.Connected ? "PodOn" : "PodOff"),
            });
            content.Children.Add(text);

            var radio = new RadioButton
            {
                Style = (Style)FindResource("DeviceRadio"),
                Content = content,
                GroupName = "Devices",
                IsChecked = device.Address == _selected?.Address,
            };
            radio.Checked += (_, _) =>
            {
                _selected = device;
                foreach (RadioButton other in DeviceList.Children.OfType<RadioButton>().Concat(OtherList.Children.OfType<RadioButton>()))
                {
                    if (other != radio) other.IsChecked = false;
                }
                NextButton.IsEnabled = true;
            };
            list.Children.Add(radio);
        }
    }

    private static void OpenBluetoothSettings()
    {
        // The "Add a device" dialog where Windows supports the link, Bluetooth settings otherwise.
        foreach (string uri in new[] { "ms-settings-connectabledevices:devicediscovery", "ms-settings:bluetooth" })
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                return;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                AppLog.Write($"could not open {uri}: {ex.Message}");
            }
        }
    }

    // --- permission ----------------------------------------------------------------------------------

    /// <summary>
    /// Whether saving this choice actually needs an administrator. It does not when the service would pick
    /// the same device by itself: with one pair of AirPods paired, PodGate resolves it automatically, so
    /// writing the machine configuration would change nothing and the prompt would be for show.
    /// </summary>
    private bool NeedsElevation()
    {
        if (_selected is null) return false;

        PodGateConfig config = PodGateConfig.Load();
        if (File.Exists(Paths.ConfigFile))
        {
            return !string.Equals(config.Address, _selected.Address, StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            // No configuration yet: only ask when the automatic choice differs from what the user picked.
            return !string.Equals(PodGateConfig.ResolveAddress(), _selected.Address, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return true;   // several Apple devices, or none: the choice has to be written down
        }
    }

    // --- test ----------------------------------------------------------------------------------------

    private void ResetTest()
    {
        _testPassed = false;
        _connectFailed = false;
        foreach (TestStepRow row in _steps)
        {
            row.Pending();
            row.Visibility = Visibility.Visible;
        }
        foreach (System.Windows.Shapes.Rectangle line in TestSteps.Children.OfType<System.Windows.Shapes.Rectangle>()) line.Visibility = Visibility.Visible;
        TestProblem.Visibility = Visibility.Collapsed;
        TestBarFill.Width = 0;
    }

    private async Task RunTestAsync()
    {
        ResetTest();
        _testRunning = true;
        NextButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        SkipButton.IsEnabled = false;   // skipping mid-test would leave the AirPods half connected
        SetNextText("Next");

        bool ran;
        try
        {
            ran = await _exclusive(TestBodyAsync);
        }
        catch (Exception ex)
        {
            ShowTestProblem("The test didn’t finish", ex.Message);
            ran = true;
        }
        _testRunning = false;
        BackButton.IsEnabled = true;
        NextButton.IsEnabled = true;
        SkipButton.IsEnabled = true;

        if (!ran)
        {
            ShowTestProblem("PodGate is busy", "Another connect or disconnect is still running. Try again in a moment.");
            SetNextText("Try again");
        }
        else if (_testPassed)
        {
            SetNextText("Next");
            SetBar(100);
        }
        else
        {
            SetNextText("Try again");
            SkipButton.Visibility = Visibility.Visible;
        }
    }

    private async Task TestBodyAsync()
    {
        PodGateConfig config = PodGateConfig.Load();
        config.Mode = ConnectMode.Full;
        var flow = new ConnectFlow(config, AppLog.Write);
        string address;
        try
        {
            address = PodGateConfig.ResolveAddress(config.Address);
        }
        catch (Exception ex)
        {
            _steps[0].Failed(ex.Message);
            ShowTestProblem("PodGate doesn’t know which AirPods to use", "Go back and choose your AirPods again.");
            return;
        }

        // 1. Disconnect, so the test connects the way the shortcut will.
        var watch = Stopwatch.StartNew();
        _steps[0].Running();
        SetBar(5);
        if (!QuickState.Read(address).Blocked)
        {
            FlowResult released = await flow.ReleaseAsync();
            if (!released.Ok)
            {
                _steps[0].Failed(released.Message);
                ShowTestProblem("Couldn’t disconnect the AirPods", ServiceHint(released.Message));
                return;
            }
        }
        _steps[0].Done(watch.Elapsed);
        SetBar(20);

        // 2. Connect.
        watch.Restart();
        _steps[1].Running();
        var progress = new Progress<ConnectProgress>(p => SetBar(20 + p.Percent * 0.6));
        FlowResult connected = await flow.ConnectAsync(progress);
        if (!connected.Ok)
        {
            _connectFailed = connected.Message.Contains("not reachable", StringComparison.OrdinalIgnoreCase);
            _steps[1].Failed(_connectFailed ? $"The AirPods didn’t answer within {connected.Elapsed.TotalSeconds:N0} seconds" : connected.Message, connected.Elapsed);
            if (_connectFailed)
            {
                ShowTestProblem("Are they in the closed case or out of range?",
                    "Take them out of the case, keep them near this PC, and try again. Your phone can keep using them in the meantime.");
            }
            else
            {
                ShowTestProblem("The test didn’t finish", ServiceHint(connected.Message));
            }
            return;
        }
        _steps[1].Done(watch.Elapsed);

        // 3 and 4. Did Windows actually switch?
        Guid? container = DeviceNodes.GetContainerId(address);
        HashSet<string> ours = container is null ? [] : AudioEndpoints.ForContainer(container.Value).Select(e => e.EndpointId).ToHashSet();

        _steps[2].Running();
        if (ours.Contains(AudioPolicy.GetDefault(AudioFlow.Render, AudioRole.Console) ?? "")) _steps[2].Done();
        else _steps[2].Failed("Windows kept another output. Choose the AirPods in the sound settings.");

        _steps[3].Running();
        if (ours.Contains(AudioPolicy.GetDefault(AudioFlow.Capture, AudioRole.Communications) ?? "")) _steps[3].Done();
        else _steps[3].Neutral("Switches when an app opens the microphone");
        SetBar(90);

        // 5. A sound through the new default output.
        _steps[4].Running();
        string sound = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Notify System Generic.wav");
        try
        {
            await Task.Run(() =>
            {
                using var player = new SoundPlayer(sound);
                player.PlaySync();
            });
            _steps[4].Done();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FileNotFoundException)
        {
            _steps[4].Neutral("No test sound on this PC");
        }

        _testPassed = true;
    }

    private static string ServiceHint(string message) =>
        message.Contains("service", StringComparison.OrdinalIgnoreCase)
            ? $"{message} Reinstalling PodGate starts it again."
            : message;

    private void ShowTestProblem(string title, string text)
    {
        // Steps that never ran would push the explanation off the window; the failure is the point now.
        bool hide = false;
        foreach (UIElement child in TestSteps.Children)
        {
            if (child is TestStepRow row && row.IsPending) hide = true;
            if (hide) child.Visibility = Visibility.Collapsed;
        }

        TestProblemTitle.Text = title;
        TestProblemText.Text = text;
        TestProblem.Visibility = Visibility.Visible;
        TestBarTrack.Visibility = Visibility.Collapsed;
    }

    private void SetBar(double percent)
    {
        TestBarTrack.Visibility = Visibility.Visible;
        double width = TestBarTrack.ActualWidth > 0 ? TestBarTrack.ActualWidth : 496;
        TestBarFill.Width = width * Math.Clamp(percent, 0, 100) / 100;
    }

    // --- done ----------------------------------------------------------------------------------------

    private void ShowDone()
    {
        DonePage.Visibility = Visibility.Visible;
        FooterNote.Text = "";
        SetNextText("Finish");

        UserSettings settings = UserSettings.Load();
        if (SaveProgress)
        {
            settings.SetupCompleted = true;
            settings.Save();
        }

        DoneTitle.Text = _testPassed ? "You’re all set" : "Setup is done";
        DoneBody.Text = _testPassed
            ? "Your AirPods are connected to this PC now. From here on, Windows won’t take them from your phone unless you ask."
            : "Your AirPods aren’t connected to this PC right now. Use your shortcut whenever you want them here.";
        DoneDeviceName.Text = _selected?.Name ?? PodGateConfig.Load().DeviceName;
        DoneDeviceState.Text = _testPassed ? "Connected" : "Disconnected";

        HotkeyConfig keys = settings.EffectiveHotkeys(PodGateConfig.Load());
        DoneToggleRow.Attach(_hotkeys);
        DoneMusicRow.Attach(_hotkeys);
    }

    private string CurrentBinding(HotkeyAction action, string fallback) =>
        SetupToggleRow.Manager?.Get(action) ?? fallback;
}

/// <summary>A paired device as the chooser shows it.</summary>
public sealed class DeviceChoice
{
    public required string Address { get; init; }
    public required string Name { get; init; }
    public bool Connected { get; init; }
    public bool IsAppleAudio { get; init; }

    public string DisplayAddress => string.Join(":", Enumerable.Range(0, 6).Select(i => Address.Substring(i * 2, 2)));

    public string Subtitle => $"{DisplayAddress} · {(Connected ? "Connected to this PC" : "Not connected")}";

    public static IReadOnlyList<DeviceChoice> ReadPaired()
    {
        try
        {
            return BtNative.GetPairedDevices()
                .Select(d => new DeviceChoice
                {
                    Address = d.Address,
                    Name = d.Name,
                    Connected = d.Connected,
                    IsAppleAudio = d.IsAudio && PodGateConfig.IsAppleDevice(d.Address),
                })
                .OrderByDescending(d => d.Connected)
                .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            AppLog.Write($"reading paired devices failed: {ex.Message}");
            return [];
        }
    }
}

/// <summary>One line of the test: pending, running, done with its time, failed, or a neutral note.</summary>
internal sealed class TestStepRow : Grid
{
    private readonly Grid _lead = new() { Width = 24, Height = 24, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(16, 0, 0, 0) };
    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;
    private readonly TextBlock _time;
    private readonly string _pendingText;

    public TestStepRow(string label)
    {
        _pendingText = label;
        MinHeight = 52;
        _title = new TextBlock { Style = (Style)System.Windows.Application.Current.FindResource("RowTitle") };
        _subtitle = new TextBlock { Style = (Style)System.Windows.Application.Current.FindResource("Caption"), Margin = new Thickness(0, 2, 0, 0), Visibility = Visibility.Collapsed };
        _time = new TextBlock
        {
            Style = (Style)System.Windows.Application.Current.FindResource("Caption"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
        };
        var text = new StackPanel { Margin = new Thickness(52, 10, 64, 10), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_title);
        text.Children.Add(_subtitle);
        Children.Add(_lead);
        Children.Add(text);
        Children.Add(_time);
        Pending();
    }

    private static object R(string key) => System.Windows.Application.Current.FindResource(key);

    public bool IsPending { get; private set; }

    public void Pending()
    {
        IsPending = true;
        _lead.Children.Clear();
        _lead.Children.Add(new System.Windows.Shapes.Ellipse { Width = 16, Height = 16, Stroke = (Brush)R("Label3"), StrokeThickness = 1.5 });
        Set(_pendingText, "Label2", null, "");
    }

    public void Running()
    {
        IsPending = false;
        _lead.Children.Clear();
        var arc = new System.Windows.Shapes.Path
        {
            Width = 16,
            Height = 16,
            Stroke = (Brush)R("Label"),
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = Geometry.Parse("M8,1 A7,7 0 0 1 15,8"),
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = new RotateTransform(),
        };
        var track = new System.Windows.Shapes.Ellipse { Width = 16, Height = 16, Stroke = (Brush)R("Fill3"), StrokeThickness = 2 };
        _lead.Children.Add(track);
        _lead.Children.Add(arc);
        arc.RenderTransform.BeginAnimation(RotateTransform.AngleProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        Set(_pendingText, "Label", null, "");
    }

    public void Done(TimeSpan? elapsed = null) =>
        Icon("G.Check", "Green", DoneText(), null, elapsed is null ? "" : $"{elapsed.Value.TotalSeconds:N1} s");

    public void Failed(string reason, TimeSpan? elapsed = null) =>
        Icon("G.Cross", "Red", _pendingText, reason, elapsed is null ? "" : $"{elapsed.Value.TotalSeconds:N1} s");

    public void Neutral(string note) => Icon("G.Check", "Label3", DoneText(), note, "");

    private string DoneText() => _pendingText switch
    {
        "Disconnect from this PC" => "Disconnected from this PC",
        "Connect" => "Connected",
        "Switch sound to AirPods" => "Sound switched to AirPods",
        "Switch call microphone" => "Call microphone switched",
        "Play a short sound" => "Played a short sound",
        _ => _pendingText,
    };

    private void Icon(string geometry, string brush, string title, string? subtitle, string time)
    {
        IsPending = false;
        _lead.Children.Clear();
        _lead.Children.Add(new Glyph { Data = (Geometry)R(geometry), Stroke = (Brush)R(brush), StrokeThickness = 2.4, Width = 18, Height = 18 });
        Set(title, "Label", subtitle, time);
    }

    private void Set(string title, string brush, string? subtitle, string time)
    {
        _title.Text = title;
        _title.Foreground = (Brush)R(brush);
        _subtitle.Text = subtitle ?? "";
        _subtitle.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;
        _time.Text = time;
    }
}
