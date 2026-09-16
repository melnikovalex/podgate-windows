using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Windows.UI.ViewManagement;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace PodGate.App.Ui;

/// <summary>
/// Follows Windows: light or dark, and the accent colour the user picked. Theme.xaml holds the dark
/// palette; this replaces those brushes at runtime, which works because the styles reference them
/// dynamically. The tray icon stays on its black disc in both themes, so it reads on any taskbar.
/// </summary>
public static class AppTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly UISettings Settings = new();

    public static bool IsLight => Registry.CurrentUser.OpenSubKey(PersonalizeKey)?.GetValue("AppsUseLightTheme") is int value && value == 1;

    /// <summary>The Windows accent colour, used for the primary button, checkmarks and the step bar.</summary>
    public static Color Accent
    {
        get
        {
            Windows.UI.Color accent = Settings.GetColorValue(UIColorType.Accent);
            return Color.FromRgb(accent.R, accent.G, accent.B);
        }
    }

    /// <summary>Applies the current theme, and keeps following it while the app runs.</summary>
    public static void Follow()
    {
        Apply();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Apply());
            }
        };
    }

    public static void Apply() => Apply(IsLight, Accent);

    public static void Apply(bool light, Color accent)
    {
        ResourceDictionary resources = System.Windows.Application.Current.Resources;
        Color onAccent = Luminance(accent) > 0.6 ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Colors.White;

        // Accent first: it is the one colour that comes from the user, not from us.
        Set(resources, "Accent", accent);
        Set(resources, "OnAccent", onAccent);
        Set(resources, "StepDone", accent);

        if (light)
        {
            Set(resources, "Background", Color.FromRgb(0xF9, 0xF9, 0xF9));
            Set(resources, "Group", Colors.White);
            Set(resources, "Separator", Color.FromRgb(0xE6, 0xE6, 0xE6));
            Set(resources, "Fill3", Color.FromRgb(0xEE, 0xEE, 0xEE));
            Set(resources, "Track", Color.FromRgb(0xD6, 0xD6, 0xD6));
            Set(resources, "Label", Color.FromRgb(0x1A, 0x1A, 0x1C));
            Set(resources, "Label2", Color.FromArgb(0xA8, 0x00, 0x00, 0x00));
            Set(resources, "Label3", Color.FromArgb(0x66, 0x00, 0x00, 0x00));
            Set(resources, "TestFill", Color.FromRgb(0xA0, 0xA0, 0xA4));
            Set(resources, "Green", Color.FromRgb(0x1D, 0x8B, 0x3E));
            Set(resources, "GreenTint", Color.FromArgb(0x22, 0x1D, 0x8B, 0x3E));
            Set(resources, "Red", Color.FromRgb(0xC4, 0x2B, 0x1C));
            Set(resources, "RedTint", Color.FromArgb(0x1A, 0xC4, 0x2B, 0x1C));
            Set(resources, "Purple", Color.FromRgb(0x8A, 0x2B, 0xE2));
            Set(resources, "Dark", Colors.White);            // text on the accent-filled button
            Set(resources, "WindowBorder", Color.FromArgb(0x24, 0x00, 0x00, 0x00));
            Set(resources, "CloseGlyph", Color.FromRgb(0x1A, 0x1A, 0x1C));
            Set(resources, "Hover", Color.FromArgb(0x14, 0x00, 0x00, 0x00));
        }
        else
        {
            Set(resources, "Background", Color.FromRgb(0x1C, 0x1C, 0x1E));
            Set(resources, "Group", Color.FromRgb(0x2C, 0x2C, 0x2E));
            Set(resources, "Separator", Color.FromRgb(0x38, 0x38, 0x3A));
            Set(resources, "Fill3", Color.FromRgb(0x3A, 0x3A, 0x3C));
            Set(resources, "Track", Color.FromRgb(0x48, 0x48, 0x4A));
            Set(resources, "Label", Color.FromRgb(0xF5, 0xF5, 0xF7));
            Set(resources, "Label2", Color.FromArgb(0x99, 0xEB, 0xEB, 0xF5));
            Set(resources, "Label3", Color.FromArgb(0x4D, 0xEB, 0xEB, 0xF5));
            Set(resources, "TestFill", Color.FromRgb(0x63, 0x63, 0x66));
            Set(resources, "Green", Color.FromRgb(0x30, 0xD1, 0x58));
            Set(resources, "GreenTint", Color.FromArgb(0x29, 0x30, 0xD1, 0x58));
            Set(resources, "Red", Color.FromRgb(0xFF, 0x45, 0x3A));
            Set(resources, "RedTint", Color.FromArgb(0x1F, 0xFF, 0x45, 0x3A));
            Set(resources, "Purple", Color.FromRgb(0xBF, 0x5A, 0xF2));
            Set(resources, "Dark", Color.FromRgb(0x1C, 0x1C, 0x1E));
            Set(resources, "WindowBorder", Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            Set(resources, "CloseGlyph", Color.FromRgb(0xEB, 0xEB, 0xF5));
            Set(resources, "Hover", Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        }
    }

    private static void Set(ResourceDictionary resources, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }

    /// <summary>Rough perceived brightness, to decide whether text on the accent should be dark or light.</summary>
    private static double Luminance(Color color) =>
        (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;

    public static Brush Brush(string key) => (Brush)System.Windows.Application.Current.Resources[key];
}
