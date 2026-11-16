using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UpdateExpress;

internal static class AppIcon
{
    private static ImageSource? _cached;

    internal static ImageSource Source
    {
        get
        {
            if (_cached is not null) return _cached;
            _cached = BitmapFrame.Create(
                new Uri("pack://application:,,,/Assets/UpdateExpress-U.ico", UriKind.Absolute),
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);
            return _cached;
        }
    }

    internal static void Apply(Window window)
    {
        try { window.Icon = Source; }
        catch { /* pack URI unavailable in some early hosts */ }
    }
}
