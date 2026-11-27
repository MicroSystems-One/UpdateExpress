using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfVerticalAlignment = System.Windows.VerticalAlignment;

namespace UpdateExpress;

/// <summary>
/// The look the restart notice and the scheduled-install notice share: app icon in the title
/// bar, grey page, white rounded card, heading, bold line, large blue line, grey text and one
/// centred row of rounded buttons. Look only; each window decides how it behaves.
/// </summary>
internal static class NoticeLook
{
    internal const double ButtonHeight = 27;

    private static SolidColorBrush Rgb(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    internal static void ApplyTo(Window window)
    {
        AppIcon.Apply(window);
        window.Width = 560;
        window.MinWidth = 460;
        window.SizeToContent = SizeToContent.Height;
        window.ResizeMode = ResizeMode.NoResize;
        window.Background = Rgb(0xF3, 0xF3, 0xF3);
        window.FontFamily = new WpfFontFamily("Segoe UI");
    }

    internal static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 20,
        FontWeight = FontWeights.SemiBold,
        Foreground = Rgb(0x1C, 0x1C, 0x1C),
        Margin = new Thickness(0, 0, 0, 6)
    };

    internal static TextBlock Strong(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = FontWeights.Bold,
        Foreground = Rgb(0x1C, 0x1C, 0x1C),
        TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(0, 0, 0, 8)
    };

    internal static TextBlock Highlight() => new()
    {
        FontSize = 28,
        FontWeight = FontWeights.SemiBold,
        Foreground = Rgb(0x00, 0x67, 0xC0),
        Margin = new Thickness(0, 0, 0, 10)
    };

    internal static TextBlock Body(string text) => new()
    {
        Text = text,
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Rgb(0x52, 0x61, 0x7A),
        LineHeight = 20,
        Margin = new Thickness(0, 0, 0, 16)
    };

    internal static Button ChromeButton(string content, bool primary)
    {
        var b = AppDialog.CreateChromeButton(content, primary);
        b.Height = ButtonHeight;
        b.MinHeight = ButtonHeight;
        b.MaxHeight = ButtonHeight;
        b.MinWidth = 132;
        b.Padding = new Thickness(12, 0, 12, 0);
        b.Margin = new Thickness(4, 0, 4, 0);
        b.VerticalAlignment = WpfVerticalAlignment.Center;
        b.FontSize = 12;
        return b;
    }

    internal static StackPanel ButtonRow(params Button[] buttons)
    {
        var row = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            VerticalAlignment = WpfVerticalAlignment.Center,
            Height = ButtonHeight
        };
        foreach (var button in buttons) row.Children.Add(button);
        return row;
    }

    internal static Border Card(params UIElement[] rows)
    {
        var stack = new StackPanel();
        foreach (var row in rows) stack.Children.Add(row);
        return new Border
        {
            Margin = new Thickness(18),
            Background = Brushes.White,
            BorderBrush = Rgb(0xDF, 0xE5, 0xEC),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(22),
            Child = stack
        };
    }
}
