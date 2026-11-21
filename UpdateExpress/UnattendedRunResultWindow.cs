using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UpdateExpress.Services;

namespace UpdateExpress;

/// <summary>
/// Next-day summary of an unattended «update until clear» run:
/// Successful vs Failed (skipped-for-failure this run).
/// </summary>
internal sealed class UnattendedRunResultWindow : Window
{
    public UnattendedRunResultWindow(UnattendedUntilClearState state, bool norwegian)
    {
        Title = norwegian
            ? "Update Express – Resultat av nattlig oppdatering"
            : "Update Express – Overnight update results";
        AppIcon.Apply(this);
        Width = 560;
        MinWidth = 480;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF6, 0xFB));
        FontFamily = new FontFamily("Segoe UI");

        int ok = state.Successful.Count;
        int failed = state.Failed.Count;
        string when = state.CompletedAt?.ToString("dd.MM.yyyy HH:mm")
                      ?? state.ArmedAt?.ToString("dd.MM.yyyy HH:mm")
                      ?? "";

        var heading = new TextBlock
        {
            Text = norwegian ? "Nattlig oppdatering er ferdig" : "Overnight updates finished",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x21, 0x37)),
            Margin = new Thickness(0, 0, 0, 6)
        };

        var subtitle = new TextBlock
        {
            Text = norwegian
                ? $"Fullført {when}. Vellykket: {ok} · Mislykket (ikke forsøkt på nytt): {failed}."
                : $"Completed {when}. Successful: {ok} · Failed (not retried): {failed}.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x52, 0x61, 0x7A)),
            Margin = new Thickness(0, 0, 0, 14),
            LineHeight = 20
        };

        var okBlock = BuildSection(
            norwegian ? "Vellykket" : "Successful",
            state.Successful.Select(FormatItem).ToList(),
            norwegian ? "Ingen." : "None.",
            Color.FromRgb(0x0F, 0x7B, 0x3A));

        var failBlock = BuildSection(
            norwegian ? "Mislykket / hoppet over" : "Failed / skipped",
            state.Failed.Select(FormatItem).ToList(),
            norwegian ? "Ingen." : "None.",
            Color.FromRgb(0xB4, 0x23, 0x18));

        var close = AppDialog.CreateChromeButton(norwegian ? "Lukk" : "Close", primary: true, isDefault: true);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 16, 0, 0);
        close.Click += (_, _) => Close();

        Content = new Border
        {
            Margin = new Thickness(16),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xDF, 0xE5, 0xEC)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
            Child = new StackPanel
            {
                Children = { heading, subtitle, okBlock, failBlock, close }
            }
        };
    }

    private static string FormatItem(UnattendedRunItem item)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(item.Source))
            sb.Append('[').Append(item.Source).Append("] ");
        sb.Append(string.IsNullOrWhiteSpace(item.Title) ? item.Id : item.Title);
        if (!string.IsNullOrWhiteSpace(item.Detail) &&
            !string.Equals(item.Detail, "Failed", StringComparison.OrdinalIgnoreCase))
            sb.Append(" — ").Append(item.Detail);
        return sb.ToString();
    }

    private static UIElement BuildSection(string title, System.Collections.Generic.List<string> lines, string empty, Color accent)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(accent),
            Margin = new Thickness(0, 0, 0, 4)
        });

        if (lines.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = empty,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x52, 0x61, 0x7A))
            });
            return panel;
        }

        var box = new TextBox
        {
            Text = string.Join("\n", lines),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = 12,
            MaxHeight = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x21, 0x37))
        };
        panel.Children.Add(box);
        return panel;
    }
}
