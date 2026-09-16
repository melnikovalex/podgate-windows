using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace PodGate.App.Ui;

/// <summary>
/// One shortcut: its keys, Change (type a new one), Test (press it and see it arrive). The same control in
/// setup and in Settings. A new combination is only kept once its test press arrives; cancelling or closing
/// the window puts the previous one back.
/// </summary>
public partial class HotkeyRow : UserControl
{
    private enum RowState
    {
        Idle,
        Recording,
        Testing,
        Works,
        Problem,
    }

    private static HotkeyRow? _active;

    private HotkeyManager? _manager;
    private RowState _state;
    private string _problem = "";
    private string? _revertTo;   // set while testing a combination that is not saved yet

    public HotkeyRow()
    {
        InitializeComponent();
        ChangeLink.Click += (_, _) => StartRecording();
        TestLink.Click += (_, _) => StartTesting(revertTo: null);
        CancelLink.Click += (_, _) => Cancel();
        ClearLink.Click += (_, _) => ClearBinding();
        // Only give up recording when the focus really leaves this row: LostKeyboardFocus bubbles, so the
        // Change button handing focus to the row itself would otherwise cancel the recording at once.
        LostKeyboardFocus += (_, e) =>
        {
            if (_state == RowState.Recording && !IsInside(e.NewFocus as DependencyObject)) Cancel();
        };
        Unloaded += (_, _) => Cancel();

        var pulse = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(600)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        StatusDot.BeginAnimation(OpacityProperty, pulse);
        Render();
    }

    public HotkeyAction Action { get; set; }

    public string Title
    {
        get => TitleRun.Text;
        set => TitleRun.Text = value;
    }

    public string Subtitle
    {
        get => SubtitleText.Text;
        set => SubtitleText.Text = value;
    }

    public bool ShowMusicDot { get; set; }

    /// <summary>Single line: keys and links next to the title (Settings' optional shortcuts).</summary>
    public bool Compact { get; set; }

    public HotkeyManager? Manager => _manager;

    public void Attach(HotkeyManager manager)
    {
        _manager = manager;
        string combination = manager.Get(Action);
        if (combination.Length > 0 && !manager.IsRegistered(Action)) ShowProblem("Taken by another app");
        else Render();
    }

    /// <summary>Puts back an untested combination. Windows call this when they close.</summary>
    public void Cancel()
    {
        if (_manager is null) return;
        if (_state == RowState.Recording) _manager.Resume();
        if (_state == RowState.Testing)
        {
            _manager.Interceptor = null;
            if (_revertTo is not null)
            {
                if (_revertTo.Length == 0) _manager.Clear(Action);
                else _manager.TryBind(Action, _revertTo);
            }
        }
        _revertTo = null;
        if (_active == this) _active = null;
        if (_state is RowState.Recording or RowState.Testing) _state = RowState.Idle;
        Render();
    }

    private bool IsInside(DependencyObject? element)
    {
        for (DependencyObject? node = element; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, this)) return true;
        }
        return false;
    }

    private void Activate()
    {
        if (_active is not null && _active != this) _active.Cancel();
        _active = this;
    }

    private void StartRecording()
    {
        if (_manager is null) return;
        Activate();
        _manager.Suspend();
        _state = RowState.Recording;
        Render();
        Keyboard.Focus(this);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_state != RowState.Recording || _manager is null)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            Cancel();
            return;
        }

        string? combination = HotkeyText.FromKeyPress(key, Keyboard.Modifiers);
        if (combination is null) return;   // a modifier on its own: keep waiting

        _manager.Resume();
        if (!HotkeyText.HasModifier(combination))
        {
            ShowProblem("Add Ctrl, Alt, Shift or Win");
            return;
        }

        string previous = _manager.Get(Action);
        switch (_manager.TryBind(Action, combination))
        {
            case BindResult.Ok:
                StartTesting(revertTo: previous);
                break;
            case BindResult.Duplicate:
                ShowProblem("Already used by another shortcut");
                break;
            case BindResult.Taken:
                ShowProblem("Taken by another app");
                break;
            default:
                ShowProblem("This key can’t be used");
                break;
        }
    }

    private void StartTesting(string? revertTo)
    {
        if (_manager is null) return;
        Activate();
        _revertTo = revertTo;
        _state = RowState.Testing;
        _manager.Interceptor = pressed =>
        {
            if (pressed != Action) return true;   // swallow the others too: nothing should run mid-test
            Dispatcher.BeginInvoke(TestPassed);
            return true;
        };
        Render();
    }

    private void TestPassed()
    {
        if (_manager is null || _state != RowState.Testing) return;
        _manager.Interceptor = null;
        if (_revertTo is not null) _manager.Commit();
        _revertTo = null;
        if (_active == this) _active = null;
        _state = RowState.Works;
        Render();
    }

    private void ClearBinding()
    {
        if (_manager is null) return;
        Cancel();
        _manager.Clear(Action);
        _manager.Commit();
        _state = RowState.Idle;
        Render();
    }

    private void ShowProblem(string text)
    {
        if (_active == this) _active = null;
        _problem = text;
        _state = RowState.Problem;
        Render();
    }

    /// <summary>For the UI snapshot tool: show a state without a real key press.</summary>
    internal void ShowSample(string state)
    {
        switch (state)
        {
            case "idle": _state = RowState.Idle; break;
            case "works": _state = RowState.Works; break;
            case "testing": _state = RowState.Testing; break;
            case "taken": _state = RowState.Problem; _problem = "Taken by another app"; break;
        }
        Render();
    }

    private void Render()
    {
        MusicDot.Text = ShowMusicDot ? " ●" : "";

        string combination = _manager?.Get(Action) ?? "";
        bool set = combination.Length > 0;
        Keys.ItemsSource = _state == RowState.Recording ? null : HotkeyText.Caps(combination);
        NotSet.Text = _state == RowState.Recording ? "Type the new shortcut" : "Not set";
        NotSet.Foreground = (System.Windows.Media.Brush)FindResource(_state == RowState.Recording ? "Label" : "Label3");
        NotSet.Visibility = _state == RowState.Recording || !set ? Visibility.Visible : Visibility.Collapsed;

        bool busy = _state is RowState.Recording or RowState.Testing;
        ChangeLink.Content = set ? "Change" : "Set";
        ChangeLink.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        ClearLink.Visibility = Compact && set && !busy ? Visibility.Visible : Visibility.Collapsed;
        TestLink.Visibility = set && !busy ? Visibility.Visible : Visibility.Collapsed;
        TestLink.Content = _state == RowState.Works ? "Test again" : "Test";
        CancelLink.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        StatusDot.Visibility = Visibility.Collapsed;
        StatusIcon.Visibility = Visibility.Collapsed;
        StatusText.Text = "";
        StatusText.FontWeight = FontWeights.SemiBold;
        switch (_state)
        {
            case RowState.Testing:
                StatusDot.Visibility = Visibility.Visible;
                StatusText.Text = "Press it now";
                StatusText.FontWeight = FontWeights.Normal;
                StatusText.Foreground = (System.Windows.Media.Brush)FindResource("Label");
                break;
            case RowState.Works:
                StatusIcon.Visibility = Visibility.Visible;
                StatusIcon.Data = (System.Windows.Media.Geometry)FindResource("G.Check");
                StatusIcon.Stroke = (System.Windows.Media.Brush)FindResource("Green");
                StatusText.Text = "Works";
                StatusText.Foreground = StatusIcon.Stroke;
                break;
            case RowState.Problem:
                StatusIcon.Visibility = Visibility.Visible;
                StatusIcon.Data = (System.Windows.Media.Geometry)FindResource("G.Cross");
                StatusIcon.Stroke = (System.Windows.Media.Brush)FindResource("Red");
                StatusText.Text = _problem;
                StatusText.Foreground = StatusIcon.Stroke;
                break;
        }

        // Compact: one line, status and keys beside the title.
        if (Compact)
        {
            Grid.SetRow(Controls, 0);
            Grid.SetColumn(Controls, 1);
            Grid.SetColumnSpan(Controls, 1);
            Controls.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetRow(Status, 1);
            Grid.SetColumn(Status, 0);
            Status.Margin = new Thickness(0, 6, 0, 0);
            Status.Visibility = _state is RowState.Problem or RowState.Works or RowState.Testing ? Visibility.Visible : Visibility.Collapsed;
            Root.Margin = new Thickness(16, 10, 16, 10);
        }
    }
}
