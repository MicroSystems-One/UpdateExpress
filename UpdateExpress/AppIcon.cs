using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UpdateExpress;

/// <summary>
/// Forces the blue U application icon on WPF windows (title bar / Alt-Tab).
/// Prefer a real Win32 HICON from the embedded multi-size ICO — pack-URI
/// BitmapFrame alone often fails to update the title-bar icon, so Windows
/// falls back to a cached EXE icon (historically the orange M).
/// </summary>
internal static class AppIcon
{
    private const string PackUri = "pack://application:,,,/Assets/UpdateExpress-U.ico";
    private const string EmbeddedName = "UpdateExpress.Assets.UpdateExpress.ico";

    private static ImageSource? _cached;
    private static readonly object Gate = new();

    internal static ImageSource Source
    {
        get
        {
            lock (Gate)
            {
                if (_cached is not null) return _cached;
                _cached = LoadBest() ?? LoadPackFrameFallback();
                return _cached!;
            }
        }
    }

    internal static void Apply(Window window)
    {
        try { window.Icon = Source; }
        catch { /* pack URI / HICON unavailable in some early hosts */ }
    }

    /// <summary>
    /// WinForms title-bar / tray: embedded blue U ICO (cloned; caller owns dispose).
    /// </summary>
    internal static System.Drawing.Icon? TryLoadDrawingIcon()
    {
        try
        {
            using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(EmbeddedName);
            if (stream is null) return null;
            using var owned = new MemoryStream();
            stream.CopyTo(owned);
            owned.Position = 0;
            using var temp = new System.Drawing.Icon(owned);
            // Clone so the returned Icon does not depend on the MemoryStream lifetime.
            return (System.Drawing.Icon)temp.Clone();
        }
        catch { return null; }
    }

    private static ImageSource? LoadBest()
    {
        try
        {
            using var drawing = TryLoadDrawingIcon();
            if (drawing is null) return null;
            using var sized = new System.Drawing.Icon(drawing, 32, 32);
            var source = Imaging.CreateBitmapSourceFromHIcon(
                sized.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();
            return source;
        }
        catch { return null; }
    }

    private static ImageSource LoadPackFrameFallback()
    {
        var decoder = BitmapDecoder.Create(
            new Uri(PackUri, UriKind.Absolute),
            BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames
            .OrderBy(f => Math.Abs(f.PixelWidth - 32))
            .FirstOrDefault()
            ?? BitmapFrame.Create(new Uri(PackUri, UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        if (frame.CanFreeze) frame.Freeze();
        return frame;
    }
}
