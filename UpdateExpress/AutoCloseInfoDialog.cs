using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UpdateExpress.Services;

namespace UpdateExpress;

/// <summary>
/// Information dialog that closes itself after a timeout (default 10 seconds).
/// OK still closes immediately.
/// </summary>
internal sealed class AutoCloseInfoDialog : Window
{
    private readonly DispatcherTimer _timer;
    private readonly TextBlock _countdown;

    public AutoCloseInfoDialog(string message, string title, int autoCloseSeconds = 10)
    {
        Title = title;
        AppIcon.Apply(this);
        Width = 460;
        SizeToContent = SizeToContent.Height;
        MinHeight = 160;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF6, 0xFB));
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        Topmost = true;

        var ok = AppDialog.CreateChromeButton(AppLanguage.Ok, primary: true, isDefault: true);
        ok.Margin = new Thickness(0, 14, 0, 0);
        ok.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        ok.Click += (_, _) => Close();

        _countdown = new TextBlock
        {
            Text = $"Closing in {autoCloseSeconds} s…",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x52, 0x61, 0x7A)),
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new Thickness(0, 14, 12, 0)
        };

        var footer = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(ok, Dock.Right);
        footer.Children.Add(ok);
        footer.Children.Add(_countdown);

        Content = new Border
        {
            Margin = new Thickness(16),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xDF, 0xE5, 0xEC)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 13,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x21, 0x37)),
                        LineHeight = 20
                    },
                    footer
                }
            }
        };

        int remaining = Math.Max(1, autoCloseSeconds);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            remaining--;
            if (remaining <= 0)
            {
                _timer.Stop();
                Close();
                return;
            }
            _countdown.Text = $"Closing in {remaining} s…";
        };
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    public static void Show(string message, string title, int autoCloseSeconds = 10)
    {
        new AutoCloseInfoDialog(message, title, autoCloseSeconds).ShowDialog();
    }
}
