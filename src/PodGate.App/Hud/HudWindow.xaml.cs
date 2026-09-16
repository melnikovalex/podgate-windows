using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PodGate.App.Hud;

/// <summary>
/// The card that appears at the bottom of the screen while connecting or releasing. In-process now:
/// the old version lived in a separate runspace and read a status file, because the work happened in
/// another session entirely. It no longer does.
/// </summary>
public partial class HudWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private DateTime _deadline = DateTime.UtcNow.AddSeconds(30);
    private DateTime? _closeAt;
    private double _shown;
    private double _target;
    private bool _closing;

    public HudWindow()
    {
        InitializeComponent();

        // Hiding only: the connect or release carries on in the background, because stopping it half
        // way would leave the device somewhere nobody asked for.
        CloseButton.MouseLeftButtonUp += (_, _) => BeginClose();
        CloseButton.MouseEnter += (_, _) => CloseButton.Background = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
        CloseButton.MouseLeave += (_, _) => CloseButton.Background = System.Windows.Media.Brushes.Transparent;

        Loaded += (_, _) =>
        {
            Rect area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Bottom - Height - 48;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        };

        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>What is happening, in the action's colour: white connect, purple music, grey disconnect.</summary>
    public void SetTitle(string title, Color color)
    {
        TitleText.Text = title;
        TitleText.Foreground = new SolidColorBrush(color);
    }

    public void Update(string status, int percent)
    {
        StatusText.Text = status;
        _target = Math.Clamp(percent, 0, 100);
        _deadline = DateTime.UtcNow.AddSeconds(30);
    }

    /// <summary>Show the final message briefly, then fade out by itself.</summary>
    public void Finish(string status, TimeSpan after)
    {
        Update(status, 100);
        _closeAt = DateTime.UtcNow + after;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Ease towards the target so the bar glides instead of jumping.
        _shown += (_target - _shown) * 0.25;
        if (Math.Abs(_target - _shown) < 0.5) _shown = _target;
        Fill.Width = Track.ActualWidth * (_shown / 100);

        // Close on request, or by itself: a card left on screen by a crashed caller is a bug the user sees.
        if (_closing) return;
        if (DateTime.UtcNow > _deadline || (_closeAt is not null && DateTime.UtcNow > _closeAt)) BeginClose();
    }

    private void BeginClose()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
