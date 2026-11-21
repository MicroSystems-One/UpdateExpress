using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfVerticalAlignment = System.Windows.VerticalAlignment;

namespace UpdateExpress;

/// <summary>
/// Brief «Kopiert!» / «Copied!» confirmation near a copy control.
/// Matches the OsSupportInfoDialog copy-feedback timing (≈1.5 s).
/// </summary>
internal static class CopiedToast
{
    public static void Show(FrameworkElement anchor, bool norwegian = true)
    {
        if (anchor is null) return;

        var label = new TextBlock
        {
            Text = norwegian ? "Kopiert!" : "Copied!",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            VerticalAlignment = WpfVerticalAlignment.Center
        };

        var chrome = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x00, 0x8B, 0x68)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 5, 10, 5),
            Child = label
        };

        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 4,
            AllowsTransparency = true,
            StaysOpen = true,
            IsHitTestVisible = false,
            Child = chrome
        };

        popup.IsOpen = true;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            popup.IsOpen = false;
            popup.Child = null;
        };
        timer.Start();
    }
}
