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
    private bool _hovered;

    public HudWindow()
    {
        InitializeComponent();

        // Hiding only: the connect or release carries on in the background, because stopping it half
        // way would leave the device somewhere nobody asked for.
        CloseButton.MouseLeftButtonUp += (_, _) => BeginClose();
        CloseButton.MouseEnter += (_, _) => CloseButton.Background = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
        CloseButton.MouseLeave += (_, _) => CloseButton.Background = System.Windows.Media.Brushes.Transparent;

        // The percentages are only interesting when you look at the card, and the card stays while you do:
        // it must not vanish from under the pointer halfway through reading them.
        MouseEnter += (_, _) =>
        {
            BatteryText.Opacity = 0.75;
            _hovered = true;
        };
        MouseLeave += (_, _) =>
        {
            BatteryText.Opacity = 0;
            _hovered = false;
            if (_closeAt is not null) _closeAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(600);
        };

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

    /// <summary>
    /// Battery on the card: a glyph that goes yellow at 20% and red at 5%, with the percentages hidden until
    /// the pointer is over the card. Null hides it, which is what "no advertisement heard yet" looks like.
    /// </summary>
    public void SetBattery(int? lowest, string text)
    {
        if (lowest is null)
        {
            Battery.Visibility = Visibility.Collapsed;
            return;
        }

        Color colour = lowest <= 5 ? Color.FromRgb(0xFF, 0x45, 0x3A)
            : lowest <= 20 ? Color.FromRgb(0xFF, 0xD6, 0x0A)
            : Color.FromRgb(0x8E, 0x8E, 0x93);
        var brush = new SolidColorBrush(colour);
        var shell = new SolidColorBrush(colour) { Opacity = 0.55 };

        BatteryShell.BorderBrush = shell;
        BatteryTip.Background = shell;
        BatteryLevel.Background = brush;
        BatteryLevel.Width = Math.Max(1.5, 17 * lowest.Value / 100.0);
        BatteryText.Text = text;
        Battery.Visibility = Visibility.Visible;
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
        if (_closing || _hovered) return;
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
