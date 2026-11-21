using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using UpdateExpress.Services;

namespace UpdateExpress;

public partial class InstallStatusHelpWindow : Window
{
    private static readonly Regex KbRegex = new(@"KB\d{5,8}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HResultRegex = new(@"0x[0-9A-Fa-f]{8}", RegexOptions.Compiled);

    private readonly string? _catalogQuery;
    private readonly string? _storeQuery;
    private readonly string? _learnMoreUrl;

    public InstallStatusHelpWindow(InstallationQueueItem item, bool norwegian)
    {
        InitializeComponent();
        AppIcon.Apply(this);
        Title = norwegian ? "Installasjonshjelp" : "Installation help";
        HeadingText.Text = item.Name;
        StatusText.Text = norwegian ? $"Status: {item.State}" : $"Status: {item.State}";
        SourceText.Text = norwegian ? $"Kilde: {item.Source}" : $"Source: {item.Source}";

        string detail = item.Tip?.Trim() ?? "";
        string? hresult = null;
        var hrMatch = HResultRegex.Match(item.State + " " + detail);
        if (hrMatch.Success)
        {
            hresult = hrMatch.Value;
            var info = WindowsUpdateErrorCatalog.Lookup(hresult);
            if (string.IsNullOrWhiteSpace(detail))
            {
                detail = string.IsNullOrWhiteSpace(info.Summary)
                    ? (norwegian ? $"Feilkode {hresult}." : $"Error code {hresult}.")
                    : $"{info.Code} — {info.Name}\n{info.Summary}";
                if (!string.IsNullOrWhiteSpace(info.Guidance))
                    detail += "\n\n" + info.Guidance;
            }
            _learnMoreUrl = info.LearnMoreUrl;
        }

        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = norwegian
                ? "Ingen detaljert feilmelding ble lagret for denne raden. Se Activity Log for mer teknisk output."
                : "No detailed error text was stored for this row. Check the Activity Log for technical output.";
        }

        DetailText.Text = detail;
        StepsText.Text = BuildSteps(item, norwegian, hresult);
        WhatLabel.Text = norwegian ? "Hva skjedde" : "What happened";
        TryLabel.Text = norwegian ? "Hva du kan prøve" : "What you can try";
        CloseButton.Content = norwegian ? "Lukk" : "Close";
        CatalogButton.Content = norwegian ? "Åpne Update Catalog" : "Open Update Catalog";
        StoreButton.Content = norwegian ? "Åpne Microsoft Store" : "Open Microsoft Store";
        LearnMoreButton.Content = norwegian ? "Les mer" : "Learn more";

        var kb = KbRegex.Match(item.Name);
        if (kb.Success)
        {
            _catalogQuery = kb.Value;
            CatalogButton.Visibility = Visibility.Visible;
        }
        else if (item.Source.Contains("Windows", StringComparison.OrdinalIgnoreCase))
        {
            _catalogQuery = item.Name.Length > 80 ? item.Name[..80] : item.Name;
            CatalogButton.Visibility = Visibility.Visible;
        }

        if (item.Source.Contains("WinGet", StringComparison.OrdinalIgnoreCase) ||
            item.State.Contains("tech mismatch", StringComparison.OrdinalIgnoreCase) ||
            item.State.Contains("teknologi-mismatch", StringComparison.OrdinalIgnoreCase))
        {
            _storeQuery = item.Name;
            StoreButton.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrWhiteSpace(_learnMoreUrl))
            LearnMoreButton.Visibility = Visibility.Visible;
    }

    private static string BuildSteps(InstallationQueueItem item, bool norwegian, string? hresult)
    {
        bool winget = item.Source.Contains("WinGet", StringComparison.OrdinalIgnoreCase);
        bool mismatch = item.State.Contains("tech mismatch", StringComparison.OrdinalIgnoreCase)
            || item.State.Contains("teknologi-mismatch", StringComparison.OrdinalIgnoreCase)
            || (item.Tip?.Contains("0x8A15008E", StringComparison.OrdinalIgnoreCase) ?? false)
            || (item.Tip?.Contains("-1978335090", StringComparison.OrdinalIgnoreCase) ?? false);

        if (winget && mismatch)
        {
            return norwegian
                ? "1. Avinstaller den eksisterende installasjonen (Store-app eller MSI) hvis mulig.\n" +
                  "2. Installer på nytt med winget, eller oppgrader i Microsoft Store hvis den kom derfra.\n" +
                  "3. Kjør Check for Updates / Check WinGet på nytt etterpå.\n" +
                  "4. Update Express kan ikke bytte installasjonskanal automatisk for denne feilen."
                : "1. Uninstall the existing install (Store app or MSI) if possible.\n" +
                  "2. Reinstall with winget, or upgrade in Microsoft Store if that was the original channel.\n" +
                  "3. Run Check for Updates / Check WinGet again afterwards.\n" +
                  "4. Update Express cannot switch install channels automatically for this error.";
        }

        if (winget)
        {
            return norwegian
                ? "1. Prøv oppgradering på nytt som administrator.\n" +
                  "2. Avinstaller/reinstaller pakken manuelt via winget eller leverandørens installer.\n" +
                  "3. Sjekk Activity Log for winget-utdata.\n" +
                  "4. Hvis pakken også finnes i Store, prøv oppgradering der."
                : "1. Retry the upgrade as administrator.\n" +
                  "2. Uninstall/reinstall the package manually via winget or the vendor installer.\n" +
                  "3. Check the Activity Log for winget output.\n" +
                  "4. If the package is also in the Store, try upgrading there.";
        }

        if (!string.IsNullOrWhiteSpace(hresult))
        {
            return norwegian
                ? "1. Bruk Fix it / anbefalt Windows Update-fiks fra hovedvinduet hvis feilkoden er kjent.\n" +
                  "2. Last ned/installer manuelt fra Microsoft Update Catalog (KB-søk).\n" +
                  "3. Kjør Check for Updates på nytt etter fiks eller omstart.\n" +
                  "4. Omstarten er ikke automatisk fra Installation Manager."
                : "1. Use Fix it / the recommended Windows Update fix from the main window when the code is known.\n" +
                  "2. Download/install manually from the Microsoft Update Catalog (KB search).\n" +
                  "3. Run Check for Updates again after a fix or reboot.\n" +
                  "4. Restart is not automatic from Installation Manager.";
        }

        return norwegian
            ? "1. Les detaljene over og Activity Log.\n" +
              "2. Prøv installasjon på nytt som administrator.\n" +
              "3. For Windows-oppdateringer: søk KB i Update Catalog.\n" +
              "4. For programmer: prøv manuelt via winget/Store/leverandør."
            : "1. Read the details above and the Activity Log.\n" +
              "2. Retry installation as administrator.\n" +
              "3. For Windows updates: search the KB in the Update Catalog.\n" +
              "4. For applications: try manually via winget/Store/vendor.";
    }

    private void Catalog_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_catalogQuery)) return;
        OpenUrl("https://www.catalog.update.microsoft.com/Search.aspx?q=" + Uri.EscapeDataString(_catalogQuery));
    }

    private void Store_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_storeQuery)) return;
        OpenUrl("ms-windows-store://search/?query=" + Uri.EscapeDataString(_storeQuery));
    }

    private void LearnMore_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_learnMoreUrl))
            OpenUrl(_learnMoreUrl!);
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Update Express", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
