using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Control = System.Windows.Controls.Control;
using Point = System.Windows.Point;

namespace PodGate.App.Ui;

/// <summary>A 24x24 stroke icon from Theme.xaml's geometries.</summary>
public sealed class Glyph : Control
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(Glyph));
    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(nameof(Stroke), typeof(System.Windows.Media.Brush), typeof(Glyph));
    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(Glyph), new PropertyMetadata(1.8));

    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public System.Windows.Media.Brush? Stroke { get => (System.Windows.Media.Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    static Glyph() => FocusableProperty.OverrideMetadata(typeof(Glyph), new FrameworkPropertyMetadata(false));
}

/// <summary>The PodGate AirPod on a black disc: the same drawing as the tray icon.</summary>
public sealed class AirPodGlyph : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(System.Windows.Media.Brush), typeof(AirPodGlyph),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public System.Windows.Media.Brush Foreground
    {
        get => (System.Windows.Media.Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Min(ActualWidth, ActualHeight) / 32;
        if (s <= 0) return;
        dc.DrawEllipse(Brushes.Black, null, new Point(16 * s, 16 * s), 16 * s, 16 * s);
        dc.DrawEllipse(Foreground, null, new Point(15 * s, 10.5 * s), 7 * s, 6.5 * s);
        dc.DrawRoundedRectangle(Foreground, null, new Rect(16 * s, 10 * s, 6 * s, 16 * s), 3 * s, 3 * s);
    }
}

/// <summary>
/// Borderless dark window with its own title bar (Theme.xaml). Drag by the title bar, close with its
/// button, and ask Windows 11 for rounded corners, which a borderless window does not get by default.
/// </summary>
public class DarkWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    public DarkWindow()
    {
        Style = (Style)System.Windows.Application.Current.FindResource(typeof(DarkWindow));
        Icon = TrayIcons.WindowIcon;
        // WindowStartupLocation is a plain CLR property, so it cannot come from the style's setters.
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SourceInitialized += (_, _) =>
        {
            int preference = DwmwcpRound;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        };
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_TitleBar") is FrameworkElement bar)
        {
            bar.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed) DragMove();
            };
        }
        if (GetTemplateChild("PART_Close") is System.Windows.Controls.Button close) close.Click += (_, _) => Close();
    }
}
