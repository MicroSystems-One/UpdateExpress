using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using UpdateExpress.Models;
using UpdateExpress.Services;

namespace UpdateExpress;

public partial class ErrorCodeFixDialog : Window
{
    private readonly UpdateEntry _entry;
    private readonly bool _includeOptional;
    private readonly bool _allDrivers;
    private readonly WindowsUpdateErrorInfo _info;
    private readonly WindowsUpdateFixLevel? _preferred;

    public ErrorCodeFixDialog(UpdateEntry entry, bool includeOptional = false, bool allDrivers = false)
    {
        InitializeComponent();
        _entry = entry;
        _includeOptional = includeOptional;
        _allDrivers = allDrivers;
        _info = WindowsUpdateErrorCatalog.Lookup(entry.ErrorCode);
        _preferred = _info.RecommendedFix;

        CodeTitle.Text = string.IsNullOrWhiteSpace(_info.Code) ? "Windows Update error" : _info.Code;
        NameText.Text = _info.Name;
        KnownBadge.Text = _info.IsKnown
            ? "Known code in Update Express catalog"
            : "Not in curated catalog — guidance is generic";
        SummaryText.Text = _info.Summary;
        GuidanceText.Text = _info.Guidance;
        FixLevelText.Text = _preferred is null
            ? "Recommended automated fix: none (follow the guidance above first)."
            : $"Recommended automated fix: {_preferred} — {WindowsUpdateFixService.Describe(_preferred.Value)}";
        ContextText.Text =
            $"Update: {entry.Title}\nKB: {(string.IsNullOrWhiteSpace(entry.KB) ? "—" : entry.KB)}\nStatus: {entry.Status}";

        if (!string.IsNullOrWhiteSpace(_info.LearnMoreUrl) &&
            Uri.TryCreate(_info.LearnMoreUrl, UriKind.Absolute, out var uri))
            LearnMoreLink.NavigateUri = uri;
        else
            LearnMoreLink.NavigateUri = new Uri("https://learn.microsoft.com/en-us/windows/deployment/update/windows-update-error-reference");

        RunFixButton.IsEnabled = _preferred is not null;
        if (_preferred is null)
            RunFixButton.ToolTip = "This code usually needs a manual step first (restart, sign-in, permissions).";
    }

    private void LearnMore_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Update Express", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        e.Handled = true;
    }

    private void RunFix_Click(object sender, RoutedEventArgs e)
    {
        if (_preferred is null) return;
        ShowFix(_preferred);
    }

    private void ChooseFix_Click(object sender, RoutedEventArgs e) => ShowFix(_preferred);

    private void ShowFix(WindowsUpdateFixLevel? preferred)
    {
        var owner = Owner;
        var dlg = new WindowsUpdateFixDialog(preferred, _entry, _includeOptional, _allDrivers) { Owner = owner ?? this };
        Close();
        dlg.ShowDialog();
        if (dlg.FixCompleted && owner is MainWindow main)
            _ = main.ReloadAfterWindowsUpdateFixAsync(dlg.ReplacedUpdateCache, dlg.ScanCompleted);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
