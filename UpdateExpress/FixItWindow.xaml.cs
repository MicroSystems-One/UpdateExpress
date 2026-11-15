using System.Windows;

namespace UpdateExpress;

public partial class FixItWindow : Window
{
    private readonly MainWindow _owner;
    private bool _detachOnClose = true;

    public FixItWindow(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        Owner = owner;
    }

    private void WindowsUpdateFix_Click(object sender, RoutedEventArgs e) => _owner.FixItWindowsUpdateFix();
    private void WuaDeepCheck_Click(object sender, RoutedEventArgs e) => _owner.FixItWuaDeepCheck();
    private void History_Click(object sender, RoutedEventArgs e) => _owner.FixItHistory();
    private void Export_Click(object sender, RoutedEventArgs e) => _owner.FixItExportCsv();
    private void ErrorReport_Click(object sender, RoutedEventArgs e) => _owner.FixItErrorReport();
    private void WuaStatusReport_Click(object sender, RoutedEventArgs e) => _owner.FixItWuaStatusReport();
    private void Diagnostics_Click(object sender, RoutedEventArgs e) => _owner.FixItDiagnostics();
    private void Health_Click(object sender, RoutedEventArgs e) => _owner.FixItHealth();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closed(object sender, EventArgs e)
    {
        if (_detachOnClose)
            _owner.DetachFixItWindow(this);
    }

    internal void SuppressDetach() => _detachOnClose = false;

    internal void ApplyLanguage(bool norwegian)
    {
        Title = norwegian ? "FIX IT! – Windows Update-reparasjon" : "FIX IT! – Windows Update repair";
        TitleText.Text = Title;
        SubtitleText.Text = norwegian
            ? "Reparer og diagnostiser problemer med Windows Update. Historikk og status oppdateres i hovedvinduet."
            : "Repair and diagnose stuck Windows Update problems. History and status updates appear in the main window.";

        void SetButton(System.Windows.Controls.Button button, string english, string norwegianText)
        {
            if (button.Tag is not string)
                button.Tag = english;
            string original = button.Tag as string ?? english;
            // Preserve emoji/prefix before the translated label.
            int split = original.LastIndexOf("  ", StringComparison.Ordinal);
            if (split >= 0)
            {
                string prefix = original[..(split + 2)];
                button.Content = norwegian ? prefix + norwegianText : original;
            }
            else
                button.Content = norwegian ? norwegianText : english;
        }

        SetButton(WindowsUpdateFixButton, "⚒  Windows Update fix", "Windows Update-fiks");
        SetButton(WuaDeepCheckButton, "🔍  WUA deep check", "WUA dybdesjekk");
        SetButton(HistoryButton, "☷  Installation History", "Installasjonshistorikk");
        SetButton(ExportCsvButton, "↓  Export CSV", "Eksporter CSV");
        SetButton(ErrorReportButton, "📋  Error report", "Feilrapport");
        SetButton(WuaStatusReportButton, "📄  WUA status report", "WUA statusrapport");
        if (DiagnosticsButton.IsEnabled)
            SetButton(DiagnosticsButton, "⚙  Diagnostics", "Diagnostikk");
        SetButton(HealthButton, "✓  Check Status", "Kontroller status");
        CloseButton.Content = norwegian ? "Lukk" : "Close";

        void SetTip(System.Windows.Controls.Button button, string english, string norwegianText) =>
            button.ToolTip = norwegian ? norwegianText : english;

        SetTip(WindowsUpdateFixButton,
            "Quick / Medium / Deep repair for stuck or empty Windows Update",
            "Hurtig / Middels / Dyp reparasjon for fastlåst eller tom Windows Update");
        SetTip(WuaDeepCheckButton,
            "Read-only deep analysis when Windows Update fix did not help (network, services, WUA). ~2–4 min.",
            "Skrivebeskyttet dybdeanalyse når Windows Update-fiks ikke hjalp (nettverk, tjenester, WUA). ~2–4 min.");
        SetTip(HistoryButton,
            "Show Windows Update installation history in the main window",
            "Vis Windows Update-installasjonshistorikk i hovedvinduet");
        SetTip(ExportCsvButton,
            "Export the current history list as a CSV file",
            "Eksporter gjeldende historikkliste som CSV-fil");
        SetTip(ErrorReportButton,
            "AI troubleshooting prompt from the last Windows Update fix",
            "AI-feilsøkingsprompt fra siste Windows Update-fiks");
        SetTip(WuaStatusReportButton,
            "Open the latest or a previous WUA deep check report",
            "Åpne siste eller en tidligere WUA dybdesjekk-rapport");
        SetTip(DiagnosticsButton,
            "Collect WUA diagnostics and open the report in Notepad",
            "Samle WUA-diagnostikk og åpne rapporten i Notepad");
        SetTip(HealthButton,
            "Refresh history and pending updates (health check)",
            "Oppdater historikk og ventende oppdateringer (helsesjekk)");
    }
}
