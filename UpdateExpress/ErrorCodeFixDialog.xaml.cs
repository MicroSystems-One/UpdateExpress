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
    private readonly bool _stillOffered;
    public bool Hidden { get; private set; }
    public bool ReinstallRequested { get; private set; }

    public ErrorCodeFixDialog(UpdateEntry entry, bool includeOptional = false, bool allDrivers = false, bool stillOffered = false)
    {
        InitializeComponent();
        AppIcon.Apply(this);
        _entry = entry;
        _includeOptional = includeOptional;
        _allDrivers = allDrivers;
        _stillOffered = stillOffered;
        _info = WindowsUpdateErrorCatalog.Lookup(entry.ErrorCode);
        _preferred = _info.RecommendedFix;
        bool norwegian = string.Equals(ReminderSchedule.Load().Language, "no", StringComparison.OrdinalIgnoreCase);

        CodeTitle.Text = string.IsNullOrWhiteSpace(_info.Code) ? "Windows Update error" : _info.Code;
        NameText.Text = _info.Name;
        KnownBadge.Text = _info.IsKnown
            ? (norwegian ? "Kjent kode i Update Express-katalogen" : "Known code in Update Express catalog")
            : (norwegian ? "Ikke i katalogen — veiledningen er generell" : "Not in curated catalog — guidance is generic");
        SummaryText.Text = _info.Summary;
        GuidanceText.Text = _info.Guidance;
        FixLevelText.Text = _preferred is null
            ? (norwegian
                ? "Anbefalt automatisk fiks: ingen. Følg veiledningen over."
                : "Recommended automated fix: none (follow the guidance above first).")
            : $"Recommended automated fix: {_preferred} — {WindowsUpdateFixService.Describe(_preferred.Value)}";
        string offer = _stillOffered
            ? (norwegian
                ? "Windows tilbyr fortsatt denne oppdateringen. Installer på nytt bruker det tilbudet."
                : "Windows still offers this update. Install again uses that offer.")
            : (norwegian
                ? "Windows tilbyr ikke denne oppdateringen nå. Den er allerede installert, eller den er ikke tilgjengelig for denne PC-en."
                : "Windows is not offering this update now. It is already installed, or it is not available for this PC.");
        ContextText.Text =
            $"Update: {entry.Title}\nKB: {(string.IsNullOrWhiteSpace(entry.KB) ? "—" : entry.KB)}\nStatus: {entry.Status}\n\n{offer}";

        if (!string.IsNullOrWhiteSpace(_info.LearnMoreUrl) &&
            Uri.TryCreate(_info.LearnMoreUrl, UriKind.Absolute, out var uri))
            LearnMoreLink.NavigateUri = uri;
        else
            LearnMoreLink.NavigateUri = new Uri("https://learn.microsoft.com/en-us/windows/deployment/update/windows-update-error-reference");

        RunFixButton.IsEnabled = _preferred is not null;
        if (_preferred is null)
            RunFixButton.ToolTip = norwegian
                ? "Denne koden trenger vanligvis et manuelt steg først, for eksempel omstart."
                : "This code usually needs a manual step first (restart, sign-in, permissions).";
        ReinstallButton.Visibility = _stillOffered ? Visibility.Visible : Visibility.Collapsed;
        if (norwegian)
        {
            ReinstallButton.Content = "Installer på nytt";
            HideButton.Content = "Skjul";
            RunFixButton.Content = "Kjør anbefalt fiks";
            ChooseFixButton.Content = "Velg fiksnivå…";
            CloseButton.Content = "Lukk";
        }
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

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        HistoryArchive.HideFromDisplay(_entry);
        Hidden = true;
        Close();
    }

    private void Reinstall_Click(object sender, RoutedEventArgs e)
    {
        if (!_stillOffered) return;
        ReinstallRequested = true;
        Close();
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
