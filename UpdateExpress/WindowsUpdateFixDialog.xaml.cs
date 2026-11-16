using System;
using System.IO;
using System.Windows;
using UpdateExpress.Models;
using UpdateExpress.Services;

namespace UpdateExpress;

public partial class WindowsUpdateFixDialog : Window
{
    private readonly UpdateEntry? _fixTarget;
    private readonly bool _includeOptional;
    private readonly bool _allDrivers;
    private bool _running;
    private WindowsUpdateFixReport? _lastReport;

    internal bool FixCompleted { get; private set; }
    internal bool ReplacedUpdateCache { get; private set; }
    internal bool ScanCompleted { get; private set; }

    public WindowsUpdateFixDialog(
        WindowsUpdateFixLevel? preferredLevel = null,
        UpdateEntry? fixTarget = null,
        bool includeOptional = false,
        bool allDrivers = false)
    {
        InitializeComponent();
        AppIcon.Apply(this);
        _fixTarget = fixTarget;
        _includeOptional = includeOptional;
        _allDrivers = allDrivers;
        if (preferredLevel == WindowsUpdateFixLevel.Deep) DeepOption.IsChecked = true;
        else if (preferredLevel == WindowsUpdateFixLevel.Medium) MediumOption.IsChecked = true;
        else if (preferredLevel == WindowsUpdateFixLevel.Quick) QuickOption.IsChecked = true;
        RefreshErrorReportEnabled();
        if (preferredLevel is not null)
            AppendLine("Suggested: " + preferredLevel + " — " + WindowsUpdateFixService.Describe(preferredLevel.Value));
    }

    private WindowsUpdateFixLevel SelectedLevel =>
        DeepOption.IsChecked == true ? WindowsUpdateFixLevel.Deep
        : MediumOption.IsChecked == true ? WindowsUpdateFixLevel.Medium
        : WindowsUpdateFixLevel.Quick;

    private void Level_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AppendLine("Selected: " + SelectedLevel + " — " + WindowsUpdateFixService.Describe(SelectedLevel));
    }

    private void RefreshErrorReportEnabled()
    {
        bool has = _lastReport != null || File.Exists(WindowsUpdateFixReport.LastTextPath);
        ErrorReportButton.IsEnabled = has;
    }

    private void AppendLine(string line)
    {
        if (string.IsNullOrWhiteSpace(LogText.Text) || LogText.Text == "Select a level and click Run.")
            LogText.Text = line;
        else
            LogText.Text += Environment.NewLine + line;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;

        if (!ElevationHelper.IsElevated)
        {
            var choice = AppDialog.Show(this,
                "Windows Update fix requires administrator privileges.\n\n" +
                "Restart Update Express as administrator?\n\n" +
                "After restart, open Windows Update fix again and run the selected level.",
                "Administrator privileges required",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (choice != MessageBoxResult.Yes) return;
            try
            {
                ElevationHelper.RestartElevated();
                Application.Current.Shutdown();
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                AppDialog.Show(this, "Administrator approval cancelled.", "Update Express",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppDialog.Show(this, "Unable to restart as administrator: " + ex.Message,
                    "Update Express", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }

        var level = SelectedLevel;
        const string historyNote =
            "Windows Update history is saved in Update Express first. SoftwareDistribution is renamed, not restored, so Windows' own history list is empty afterwards. Update Express still shows the saved events.";
        string warn = level switch
        {
            WindowsUpdateFixLevel.Deep =>
                "Deep fix will run Quick (disk space check and SFC /scannow) plus DISM RestoreHealth.\n\nThis can take over an hour. Keep the PC online and plugged in.\n\n" + historyNote + "\n\nContinue?",
            WindowsUpdateFixLevel.Medium =>
                "Medium fix runs the same steps as Quick, including a disk space check and SFC /scannow.\n\nThis can take 15–45 minutes.\n\n" + historyNote + "\n\nContinue?",
            _ =>
                "Quick fix will check free space on the Windows drive and run SFC /scannow before it changes Windows Update.\n\nIt then stops Windows Update services and renames SoftwareDistribution + catroot2 (backup names).\n\nThis can take 15–45 minutes.\n\n" + historyNote + "\n\nContinue?"
        };
        if (AppDialog.Show(this, warn, "Confirm Windows Update fix", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        _running = true;
        RunButton.IsEnabled = false;
        QuickOption.IsEnabled = MediumOption.IsEnabled = DeepOption.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressLabel.Text = "Starting " + level + "…";
        LogText.Text = $"=== {level} fix started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===";

        var progress = new Progress<string>(msg =>
        {
            ProgressLabel.Text = msg.Length > 90 ? msg[..90] + "…" : msg;
            AppendLine(msg);
        });

        try
        {
            _lastReport = await WindowsUpdateFixService.RunAsync(level, progress, default, _fixTarget, _includeOptional, _allDrivers).ConfigureAwait(true);
            ReplacedUpdateCache = _lastReport.Steps.Any(step => step.Name == "Rename SoftwareDistribution" && step.Success);
            ScanCompleted = _lastReport.Steps.Any(step => step.Name == "Check for updates" && step.Success);
            AppendLine("");
            AppendLine(_lastReport.HasFailures
                ? "Finished with one or more failures. Open Error report for an AI troubleshooting prompt."
                : "Finished successfully. Saved history stays in Update Express.");
            RefreshErrorReportEnabled();
            MessageBox.Show(this,
                _lastReport.HasFailures
                    ? "Windows Update fix finished with errors.\n\nSaved history stays in Update Express. Use Error report if a step failed."
                    : "Windows Update fix finished.\n\nWindows' own history starts empty. Update Express keeps the saved events. A row is marked Fixed only when a later success exists, or when the update you fixed is no longer offered.",
                "Windows Update fix",
                MessageBoxButton.OK,
                _lastReport.HasFailures ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            AppendLine("Cancelled.");
        }
        catch (Exception ex)
        {
            AppendLine("ERROR: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Windows Update fix", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _running = false;
            FixCompleted = _lastReport != null;
            ProgressPanel.Visibility = Visibility.Collapsed;
            RunButton.IsEnabled = true;
            QuickOption.IsEnabled = MediumOption.IsEnabled = DeepOption.IsEnabled = true;
            RefreshErrorReportEnabled();
        }
    }

    private void ErrorReport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ErrorReportDialog { Owner = this };
        dlg.ShowDialog();
        RefreshErrorReportEnabled();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            MessageBox.Show(this, "A fix is still running. Wait until it finishes.",
                "Windows Update fix", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Close();
    }
}
