using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Media.Imaging;

namespace PodGate.App;

/// <summary>
/// Drawn rather than shipped: one AirPod on a dark disc, so it reads on light and dark taskbars. White when
/// connected, purple when connected in music mode, grey otherwise. The windows use the white one.
/// </summary>
public static class TrayIcons
{
    public static readonly Color White = Color.FromArgb(255, 245, 245, 247);
    public static readonly Color Purple = Color.FromArgb(255, 191, 90, 242);
    public static readonly Color Gray = Color.FromArgb(255, 142, 142, 147);

    private static BitmapSource? _windowIcon;

    public static Icon Draw(Color pod, int size = 32)
    {
        using Bitmap bitmap = Render(pod, size);
        IntPtr handle = bitmap.GetHicon();
        using var temp = Icon.FromHandle(handle);
        return (Icon)temp.Clone();
    }

    public static BitmapSource WindowIcon => _windowIcon ??= ToBitmapSource(White);

    private static Bitmap Render(Color pod, int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        float s = size / 32f;
        using (var disc = new SolidBrush(Color.FromArgb(255, 28, 28, 30))) g.FillEllipse(disc, 0, 0, 32 * s, 32 * s);

        using var brush = new SolidBrush(pod);
        g.FillEllipse(brush, 8 * s, 4 * s, 14 * s, 13 * s);   // the bud
        using var stem = new GraphicsPath();
        stem.AddArc(16 * s, 10 * s, 6 * s, 6 * s, 180, 180);
        stem.AddArc(16 * s, 20 * s, 6 * s, 6 * s, 0, 180);
        stem.CloseFigure();
        g.FillPath(brush, stem);                                // the stem, rounded at both ends
        return bitmap;
    }

    private static BitmapSource ToBitmapSource(Color pod)
    {
        using Bitmap bitmap = Render(pod, 64);
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
