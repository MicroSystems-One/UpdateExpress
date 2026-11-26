using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using UpdateExpress.Services;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfVerticalAlignment = System.Windows.VerticalAlignment;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfClipboard = System.Windows.Clipboard;

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
        LocalNetworkReader.Info network,
        Func<PcHardwareReader.Snapshot>? hardwareReader = null)
    {
        _norwegian = norwegian;
        Title = norwegian ? "Denne PCen" : "This PC";
        AppIcon.Apply(this);
        Width = 560;
        // Tall enough that default content + Lukk fit without a scrollbar (match desired-size screenshot),
        // but never taller than the screen; the content scrolls when it does not fit.
        MinWidth = 480;
        MaxWidth = 640;
        MinHeight = 520;
        MaxHeight = 1040;
        Height = Math.Max(MinHeight, Math.Min(920, SystemParameters.WorkArea.Height - 24));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = false;
        Background = Brush("#F3F3F3");
        FontFamily = new WpfFontFamily("Segoe UI Variable Text, Segoe UI");
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
        body.Children.Add(ValueRow(windowsVersion));
        body.Children.Add(SectionLabel(norwegian ? "Build / nummer" : "Build / number", top: 10));
        body.Children.Add(ValueRow(windowsBuild));

        body.Children.Add(Divider());

        // Device above Network (UPD-150).
        body.Children.Add(SectionLabel(norwegian ? "Enhet" : "Device"));
        string manufacturerModel = CombineManufacturerModel(identity.Manufacturer, identity.Model);
        body.Children.Add(Field(
            norwegian ? "Produsent/modell nr." : "Manufacturer/model no.",
            manufacturerModel,
            copy: true));
        body.Children.Add(Field(norwegian ? "Produktnr." : "Product no.", identity.Product, copy: true));
        body.Children.Add(Field(norwegian ? "Serienr." : "Serial no.", identity.Serial, copy: true));
        body.Children.Add(Field(norwegian ? "Enhetsnavn" : "Device name", identity.DeviceName, copy: true));
        body.Children.Add(Field(
            norwegian ? "Oppetid" : "Uptime",
            uptimeText,
            copy: false,
            valueTooltip: uptimeTooltip));

        body.Children.Add(Divider());

        // Hardware above Network. The rows start as "…" and are filled when the read, which
        // runs off the UI thread, is done.
        body.Children.Add(SectionLabel(norwegian ? "Maskinvare" : "Hardware"));
        var processor = AddHardwareRow(body, norwegian ? "Prosessor" : "Processor", copy: true);
        var memory = AddHardwareRow(body, norwegian ? "Minne" : "Memory", copy: true);
        var storage = AddHardwareRow(body, norwegian ? "Lokal lagring" : "Local storage", copy: true);
        var cpuTemperature = AddHardwareRow(body, norwegian ? "CPU-temperatur" : "CPU temperature", copy: false);
        var gpuTemperature = AddHardwareRow(body, norwegian ? "GPU-temperatur" : "GPU temperature", copy: false);
        Loaded += async (_, _) =>
        {
            PcHardwareReader.Display display;
            try
            {
                var snapshot = await System.Threading.Tasks.Task.Run(hardwareReader ?? PcHardwareReader.Read);
                display = PcHardwareReader.Describe(snapshot, norwegian);
            }
            catch
            {
                display = PcHardwareReader.Describe(new PcHardwareReader.Snapshot(), norwegian);
            }
            processor.Show(display.Processor);
            memory.Show(display.Memory);
            storage.Show(display.Storage);
            cpuTemperature.Show(display.CpuTemperature);
            gpuTemperature.Show(display.GpuTemperature);
            HardwareLoaded = true;
        };

        body.Children.Add(Divider());

        body.Children.Add(SectionLabel(norwegian ? "Nettverk" : "Network"));
        body.Children.Add(Field(norwegian ? "Lokal IP" : "Local IP", network.LocalIp, copy: true));
        body.Children.Add(Field(norwegian ? "Subnettmaske" : "Subnet mask", network.SubnetMask, copy: false));
        body.Children.Add(Field("Gateway", network.Gateway, copy: false));
        body.Children.Add(Field(norwegian ? "Primær DNS" : "Primary DNS", network.PrimaryDns, copy: false));
        body.Children.Add(Field(norwegian ? "Sekundær DNS" : "Secondary DNS", network.SecondaryDns, copy: false));

        var close = AppDialog.CreateChromeButton(norwegian ? "Lukk" : "Close", primary: true, isDefault: true);
        close.HorizontalAlignment = WpfHorizontalAlignment.Right;
        close.Margin = new Thickness(0, 16, 0, 0);
        close.Click += (_, _) => Close();
        body.Children.Add(close);

        scroll.Content = body;
        root.Child = scroll;
        Content = root;
    }

    private static string CombineManufacturerModel(string manufacturer, string model)
    {
        bool hasManufacturer = !string.IsNullOrWhiteSpace(manufacturer);
        bool hasModel = !string.IsNullOrWhiteSpace(model);
        if (hasManufacturer && hasModel)
            return $"{manufacturer.Trim()} {model.Trim()}";
        if (hasManufacturer)
            return manufacturer.Trim();
        if (hasModel)
            return model.Trim();
        return "";
    }

    /// <summary>True once the hardware rows show what was read (or "unavailable").</summary>
    internal bool HardwareLoaded { get; private set; }

    private sealed class HardwareRow(TextBlock value, Button? copy)
    {
        public void Show(PcHardwareReader.Row row)
        {
            value.Text = row.Value;
            value.ToolTip = row.Tooltip;
            if (copy is null) return;
            // Nothing to copy from a row that has no reading.
            copy.Tag = row.Available ? row.Value : "";
            copy.Visibility = row.Available ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private HardwareRow AddHardwareRow(StackPanel body, string label, bool copy)
    {
        var field = Field(label, "…", copy);
        var row = (StackPanel)field.Child;
        body.Children.Add(field);
        return new HardwareRow((TextBlock)row.Children[1], copy ? (Button)row.Children[2] : null);
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
            Width = 150,
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
            MaxWidth = 260,
            ToolTip = string.IsNullOrWhiteSpace(valueTooltip) ? value : valueTooltip
        });
        if (copy)
            row.Children.Add(CopyButton(copyText ?? value));

        return new Border { Child = row };
    }

    private UIElement ValueRow(string value)
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
                WpfClipboard.SetDataObject(text, true);
                btn.ToolTip = _norwegian ? "Kopiert!" : "Copied!";
                CopiedToast.Show(btn, _norwegian);
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
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
