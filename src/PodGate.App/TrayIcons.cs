using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace PodGate.App;

/// <summary>What the tray icon is saying, which is also what the progress card is coloured by.</summary>
public enum TrayState
{
    /// <summary>Not connected to this PC: the AirPods belong to the phone.</summary>
    Blocked,

    /// <summary>Connected, microphone available.</summary>
    On,

    /// <summary>Connected for music: best sound quality, no microphone.</summary>
    Music,
}

/// <summary>
/// The tray and window icons, shipped as artwork rather than drawn in code. Each state exists twice, once
/// for a light taskbar and once for a dark one, because a single colour cannot read on both - Windows only
/// tells us which theme the taskbar uses, so the matching set is chosen at the moment the icon is built.
/// </summary>
public static class TrayIcons
{
    public static readonly Color White = Color.FromArgb(255, 245, 245, 247);
    public static readonly Color Purple = Color.FromArgb(255, 191, 90, 242);
    public static readonly Color Gray = Color.FromArgb(255, 142, 142, 147);

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>The sizes the artwork exists in; anything else is scaled from the nearest one.</summary>
    private static readonly int[] Sizes = [16, 20, 24, 32];

    /// <summary>The colour the progress card uses for this state.</summary>
    public static Color Colour(TrayState state) => state switch
    {
        TrayState.Music => Purple,
        TrayState.On => White,
        _ => Gray,
    };

    /// <summary>
    /// A tray icon for the current taskbar theme and DPI. The caller owns it and must dispose it: Windows
    /// keeps the old one alive until the notify icon is pointed at the new one.
    /// </summary>
    public static Icon Draw(TrayState state)
    {
        int wanted = SystemInformation.SmallIconSize.Width;
        int available = Sizes.MinBy(size => Math.Abs(size - wanted));

        using Bitmap artwork = Load(Name(state, TaskbarIsLight, available));
        using Bitmap sized = artwork.Width == wanted ? (Bitmap)artwork.Clone() : Scale(artwork, wanted);

        // GetHicon hands out a handle the Icon does not own, so it has to be destroyed by hand. Cloning
        // first gives an Icon that carries its own copy and survives the destruction.
        IntPtr handle = sized.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>The app icon for the window title bars, in the theme the app is currently showing.</summary>
    public static BitmapSource WindowIcon => ToBitmapSource(Ui.AppTheme.ShowingLight ? "app-light-256.png" : "app-dark-256.png");

    /// <summary>True when the taskbar is light. This is a different setting from the one for apps.</summary>
    private static bool TaskbarIsLight =>
        Registry.CurrentUser.OpenSubKey(PersonalizeKey)?.GetValue("SystemUsesLightTheme") is int value && value == 1;

    /// <summary>
    /// "tray-light-*" is the dark glyph, for a light taskbar; "tray-dark-*" is the light one. The names
    /// describe the taskbar the artwork is meant for, not the colour of the ink.
    /// </summary>
    private static string Name(TrayState state, bool lightTaskbar, int size) =>
        $"tray-{(lightTaskbar ? "light" : "dark")}-{state.ToString().ToLowerInvariant()}-{size}.png";

    private static Bitmap Load(string name)
    {
        Assembly assembly = typeof(TrayIcons).Assembly;
        using Stream stream = assembly.GetManifestResourceStream($"PodGate.App.Assets.{name}")
            ?? throw new InvalidOperationException($"the icon {name} is missing from the build");
        return new Bitmap(stream);
    }

    private static Bitmap Scale(Bitmap source, int size)
    {
        var scaled = new Bitmap(size, size);
        using Graphics g = Graphics.FromImage(scaled);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        g.DrawImage(source, new Rectangle(0, 0, size, size));
        return scaled;
    }

    private static BitmapSource ToBitmapSource(string name)
    {
        Assembly assembly = typeof(TrayIcons).Assembly;
        using Stream stream = assembly.GetManifestResourceStream($"PodGate.App.Assets.{name}")
            ?? throw new InvalidOperationException($"the icon {name} is missing from the build");

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
