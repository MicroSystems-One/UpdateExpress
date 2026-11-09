using System;
using System.IO;
using System.Windows;
using UpdateExpress.Services;

namespace UpdateExpress;

public partial class WindowsUpdateFixDialog : Window
{
    private bool _running;
    private WindowsUpdateFixReport? _lastReport;

    public WindowsUpdateFixDialog(WindowsUpdateFixLevel? preferredLevel = null)
    {
        InitializeComponent();
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
            var choice = MessageBox.Show(this,
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
                MessageBox.Show(this, "Administrator approval cancelled.", "Update Express",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Unable to restart as administrator: " + ex.Message,
                    "Update Express", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }

        var level = SelectedLevel;
        string warn = level switch
        {
            WindowsUpdateFixLevel.Deep =>
                "Deep fix will run Quick + SFC + DISM RestoreHealth.\n\nThis can take over an hour. Keep the PC online and plugged in.\n\nContinue?",
            WindowsUpdateFixLevel.Medium =>
                "Medium fix will run Quick + SFC /scannow.\n\nThis can take 15–45 minutes.\n\nContinue?",
            _ =>
                "Quick fix will stop Windows Update services and rename SoftwareDistribution + catroot2 (backup names).\n\nContinue?"
        };
        if (MessageBox.Show(this, warn, "Confirm Windows Update fix", MessageBoxButton.YesNo, MessageBoxImage.Warning)
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
            _lastReport = await WindowsUpdateFixService.RunAsync(level, progress).ConfigureAwait(true);
            AppendLine("");
            AppendLine(_lastReport.HasFailures
                ? "Finished with one or more failures. Open Error report for an AI troubleshooting prompt."
                : "Finished successfully. You can still open Error report for a full log.");
            RefreshErrorReportEnabled();
            MessageBox.Show(this,
                _lastReport.HasFailures
                    ? "Windows Update fix finished with errors.\n\nUse Error report to copy a prompt for further troubleshooting."
                    : "Windows Update fix finished.\n\nTry Check for Updates next.",
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
