using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using UpdateExpress.Services;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfVerticalAlignment = System.Windows.VerticalAlignment;

namespace UpdateExpress;

/// <summary>
/// Detailed PC identity + network window opened from the header PC icon.
/// </summary>
internal sealed class PcInfoWindow : Window
{
    private readonly bool _norwegian;

    public PcInfoWindow(
        bool norwegian,
        MachineIdentityReader.Identity identity,
        string windowsVersion,
        string windowsBuild,
        string uptimeText,
        string uptimeTooltip,
        LocalNetworkReader.Info network)
    {
        _norwegian = norwegian;
        Title = norwegian ? "Denne PCen" : "This PC";
        AppIcon.Apply(this);
        Width = 560;
        Height = 640;
        MinWidth = 480;
        MaxWidth = 640;
        MinHeight = 420;
        MaxHeight = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = false;
        Background = Brush("#F3F3F3");
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 12;

        var root = new Border
        {
            Margin = new Thickness(16),
            Background = Brushes.White,
            BorderBrush = Brush("#DFE5EC"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20, 16, 20, 16)
        };

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = norwegian ? "PC-informasjon" : "PC information",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#1C1C1C"),
            Margin = new Thickness(0, 0, 0, 14)
        });

        // Windows version at top (requested).
        body.Children.Add(SectionLabel(norwegian ? "Windows-versjon" : "Windows version"));
        body.Children.Add(ValueRow(windowsVersion, copyable: true));
        body.Children.Add(SectionLabel(norwegian ? "Build / nummer" : "Build / number", top: 10));
        body.Children.Add(ValueRow(windowsBuild, copyable: true));

        body.Children.Add(Divider());

        body.Children.Add(SectionLabel(norwegian ? "Nettverk" : "Network"));
        body.Children.Add(Field(norwegian ? "Lokal IP" : "Local IP", network.LocalIp, copy: true));
        body.Children.Add(Field(norwegian ? "Subnettmaske" : "Subnet mask", network.SubnetMask, copy: true));
        body.Children.Add(Field("Gateway", network.Gateway, copy: true));
        body.Children.Add(Field(norwegian ? "Primær DNS" : "Primary DNS", network.PrimaryDns, copy: true));
        body.Children.Add(Field(norwegian ? "Sekundær DNS" : "Secondary DNS", network.SecondaryDns, copy: true));

        body.Children.Add(Divider());

        body.Children.Add(SectionLabel(norwegian ? "Enhet" : "Device"));
        body.Children.Add(Field(norwegian ? "Produsent" : "Manufacturer", identity.Manufacturer, copy: false));
        body.Children.Add(Field(norwegian ? "Produktnr." : "Product no.", identity.Product, copy: true));
        body.Children.Add(Field(norwegian ? "Modell/nr" : "Model", identity.Model, copy: true));
        body.Children.Add(Field(norwegian ? "Serienr." : "Serial no.", identity.Serial, copy: true));
        body.Children.Add(Field(norwegian ? "Enhetsnavn" : "Device name", identity.DeviceName, copy: true));
        body.Children.Add(Field(
            norwegian ? "Oppetid" : "Uptime",
            uptimeText,
            copy: true,
            valueTooltip: uptimeTooltip,
            copyText: string.IsNullOrWhiteSpace(uptimeTooltip) ? uptimeText : $"{uptimeText} ({uptimeTooltip})"));

        var close = AppDialog.CreateChromeButton(norwegian ? "Lukk" : "Close", primary: true, isDefault: true);
        close.HorizontalAlignment = WpfHorizontalAlignment.Right;
        close.Margin = new Thickness(0, 16, 0, 0);
        close.Click += (_, _) => Close();
        body.Children.Add(close);

        scroll.Content = body;
        root.Child = scroll;
        Content = root;
    }

    private Border Field(string label, string value, bool copy, string? valueTooltip = null, string? copyText = null)
    {
        var row = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8),
            VerticalAlignment = WpfVerticalAlignment.Center
        };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Width = 130,
            FontSize = 12,
            Foreground = Brush("#5D5D5D"),
            VerticalAlignment = WpfVerticalAlignment.Center
        });
        row.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(value) ? "—" : value,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#1C1C1C"),
            VerticalAlignment = WpfVerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 280,
            ToolTip = string.IsNullOrWhiteSpace(valueTooltip) ? value : valueTooltip
        });
        if (copy)
            row.Children.Add(CopyButton(copyText ?? value));

        return new Border { Child = row };
    }

    private UIElement ValueRow(string value, bool copyable)
    {
        var row = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 4),
            VerticalAlignment = WpfVerticalAlignment.Center
        };
        row.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(value) ? "—" : value,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#1C1C1C"),
            VerticalAlignment = WpfVerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 400
        });
        if (copyable)
            row.Children.Add(CopyButton(value));
        return row;
    }

    private Button CopyButton(string value)
    {
        var btn = new Button
        {
            Width = 22,
            Height = 22,
            MinWidth = 22,
            MinHeight = 22,
            Padding = new Thickness(2),
            Margin = new Thickness(6, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = WpfVerticalAlignment.Center,
            ToolTip = _norwegian ? "Kopier verdien" : "Copy this value",
            Tag = value,
            Focusable = true
        };
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(new Path
        {
            Fill = Brush("#F3F3F3"),
            Stroke = Brush("#0067C0"),
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = Geometry.Parse("M3,4 H12 L15.2,7.2 V16.2 H3 Z")
        });
        canvas.Children.Add(new Path
        {
            Stroke = Brush("#0067C0"),
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = Geometry.Parse("M12,4 V7.2 H15.2")
        });
        canvas.Children.Add(new Path
        {
            Fill = Brush("#F3F3F3"),
            Stroke = Brush("#0067C0"),
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = Geometry.Parse("M8.2,8 H16.4 L20,11.6 V21 H8.2 Z")
        });
        canvas.Children.Add(new Path
        {
            Stroke = Brush("#0067C0"),
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = Geometry.Parse("M16.4,8 V11.6 H20")
        });
        btn.Content = new Viewbox
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            Child = canvas
        };
        btn.Click += (_, _) =>
        {
            string text = btn.Tag as string ?? "";
            if (string.IsNullOrWhiteSpace(text) || text is "…" or "—") return;
            try
            {
                Clipboard.SetDataObject(text, true);
                btn.ToolTip = _norwegian ? "Kopiert" : "Copied";
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    btn.ToolTip = _norwegian ? "Kopier verdien" : "Copy this value";
                };
                timer.Start();
            }
            catch { }
        };
        return btn;
    }

    private static TextBlock SectionLabel(string text, double top = 0) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brush("#5D5D5D"),
        Margin = new Thickness(0, top, 0, 4)
    };

    private static Border Divider() => new()
    {
        Height = 1,
        Background = Brush("#E5E5E5"),
        Margin = new Thickness(0, 10, 0, 12)
    };

    private static SolidColorBrush Brush(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
