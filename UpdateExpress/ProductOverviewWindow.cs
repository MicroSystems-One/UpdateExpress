using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using UpdateExpress.Services;

namespace UpdateExpress;

/// <summary>
/// Short overview: Update Express vs Windows Update, and who it is for.
/// </summary>
internal sealed class ProductOverviewWindow : Window
{
    public ProductOverviewWindow(bool norwegian)
    {
        Title = norwegian ? "Om Update Express" : "About Update Express";
        AppIcon.Apply(this);
        Width = 620;
        Height = 560;
        MinWidth = 520;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = false;
        Background = Brush("#F3F3F3");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 12;

        var root = new Border
        {
            Margin = new Thickness(16),
            Background = Brushes.White,
            BorderBrush = Brush("#DFE5EC"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20)
        };

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var body = new StackPanel();
        body.Children.Add(TitleBlock(
            norwegian ? "Update Express – oversikt" : "Update Express – overview",
            18, true));
        body.Children.Add(Para(
            norwegian
                ? "Update Express er laget for privatpersoner og små bedrifter som ikke har et eget patch management-system (som N-able). Målet er en sikrere PC: bakgrunnssøk, tydelige påminnelser og kontrollert installasjon/omstart – manuelt eller mer automatisk etter dine valg."
                : "Update Express is for individuals and small businesses that do not have a dedicated patch management system (such as N-able). The goal is a more secure PC: background checks, clear reminders, and controlled install/restart — manually or more automatically based on your choices.",
            "#1C1C1C", bottom: 14));

        body.Children.Add(TitleBlock(
            norwegian ? "Tiltenkt bruk" : "Intended use",
            15, true));
        body.Children.Add(Bullet(norwegian
            ? "Hjelpe sluttbrukeren å huske og gjennomføre Windows-oppdateringer"
            : "Help the end user remember and complete Windows updates"));
        body.Children.Add(Bullet(norwegian
            ? "Automatisere det som er trygt (f.eks. Defender), og spørre før større endringer"
            : "Automate what is safe (e.g. Defender), and ask before larger changes"));
        body.Children.Add(Bullet(norwegian
            ? "Gi et vedlikeholdsvindu med påminnelse og valgfri bekreftet omstart"
            : "Provide a maintenance window with reminder and optional confirmed restart"));
        body.Children.Add(Bullet(norwegian
            ? "Hjelpe med fastlåste Windows Update-problemer (FIX IT)"
            : "Help with stuck Windows Update problems (FIX IT)", bottom: 14));

        body.Children.Add(TitleBlock(
            norwegian ? "Update Express vs Windows Update" : "Update Express vs Windows Update",
            15, true));

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var leftHead = HeaderCell(norwegian ? "Update Express" : "Update Express", "#0067C0");
        var rightHead = HeaderCell("Windows Update", "#5D5D5D");
        Grid.SetColumn(leftHead, 0);
        Grid.SetColumn(rightHead, 2);
        grid.Children.Add(leftHead);
        grid.Children.Add(rightHead);

        var left = new StackPanel();
        foreach (var line in norwegian
            ? new[]
            {
                "Én oversikt: Windows, drivere, programmer (WinGet)",
                "Vedlikeholdstid + påminnelse du kan bekrefte",
                "Bakgrunnssøk og valgfri automatisering",
                "Spør før installasjon (unntatt Defender når aktivert)",
                "Reparasjonshjelp ved typiske WU-feil",
                "Valgfri omstart utenfor arbeidstid / etter bekreftelse"
            }
            : new[]
            {
                "One overview: Windows, drivers, applications (WinGet)",
                "Maintenance time + reminder you can confirm",
                "Background checks and optional automation",
                "Ask before install (except Defender when enabled)",
                "Repair help for common Windows Update failures",
                "Optional restart outside active hours / after confirm"
            })
            left.Children.Add(Bullet(line, compact: true));

        var right = new StackPanel();
        foreach (var line in norwegian
            ? new[]
            {
                "Innebygd i Windows (Innstillinger)",
                "Microsoft styrer mye av tidspunktet",
                "Hovedsakelig Windows-oppdateringer",
                "Kan installere/omstarte mer automatisk",
                "Begrenset hjelp når WU henger",
                "Ingen egen «problem-PC»-løype"
            }
            : new[]
            {
                "Built into Windows (Settings)",
                "Microsoft largely controls timing",
                "Primarily Windows updates",
                "May install/restart more automatically",
                "Limited help when Windows Update is stuck",
                "No dedicated “problem PC” pipeline"
            })
            right.Children.Add(Bullet(line, compact: true));

        var leftBox = new Border
        {
            Background = Brush("#F7FAFD"),
            BorderBrush = Brush("#D6E4F0"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Child = left
        };
        var rightBox = new Border
        {
            Background = Brush("#F9F9F9"),
            BorderBrush = Brush("#E5E5E5"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Child = right
        };
        Grid.SetRow(leftBox, 1);
        Grid.SetColumn(leftBox, 0);
        Grid.SetRow(rightBox, 1);
        Grid.SetColumn(rightBox, 2);
        grid.Children.Add(leftBox);
        grid.Children.Add(rightBox);
        body.Children.Add(grid);

        body.Children.Add(TitleBlock(
            norwegian ? "Viktig å vite" : "Good to know",
            15, true));
        body.Children.Add(Bullet(norwegian
            ? "Update Express erstatter ikke Microsofts oppdateringskatalog – den styrer når og hvordan du følger dem opp."
            : "Update Express does not replace Microsoft’s update catalog — it controls when and how you follow up."));
        body.Children.Add(Bullet(norwegian
            ? "Funksjonsoppdateringer (store Windows-versjoner) installeres fortsatt via Windows Innstillinger."
            : "Feature updates (major Windows versions) are still installed via Windows Settings."));
        body.Children.Add(Bullet(norwegian
            ? "Har du allerede bedrifts-patching (RMM), bruk Update Express som tillegg på problem-PC-er – ikke dobbeltstyring uten å skru av den andre."
            : "If you already have business patching (RMM), use Update Express as a complement on problem PCs — avoid dual control without turning the other off.",
            bottom: 16));

        var close = AppDialog.CreateChromeButton(norwegian ? "Lukk" : "Close", primary: true, isDefault: true);
        close.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        close.Click += (_, _) => Close();
        body.Children.Add(close);

        scroll.Content = body;
        root.Child = scroll;
        Content = root;
    }

    private static TextBlock TitleBlock(string text, double size, bool bold) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = Brush("#1C1C1C"),
        Margin = new Thickness(0, 0, 0, 8),
        TextWrapping = TextWrapping.Wrap
    };

    private static TextBlock Para(string text, string color, double bottom = 8) => new()
    {
        Text = text,
        Foreground = Brush(color),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, bottom),
        LineHeight = 18
    };

    private static TextBlock Bullet(string text, bool compact = false, double bottom = 6) => new()
    {
        Text = "•  " + text,
        Foreground = Brush("#1C1C1C"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, compact ? 4 : bottom),
        LineHeight = 17
    };

    private static TextBlock HeaderCell(string text, string color) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        FontSize = 13,
        Foreground = Brush(color),
        Margin = new Thickness(0, 0, 0, 6)
    };

    private static SolidColorBrush Brush(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
