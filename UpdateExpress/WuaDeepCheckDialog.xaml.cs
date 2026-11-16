using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using UpdateExpress.Services;

namespace UpdateExpress;

public partial class WuaDeepCheckDialog : Window
{
    private bool _running;
    private WuaDeepCheckReport? _report;

    public WuaDeepCheckDialog()
    {
        InitializeComponent();
        AppIcon.Apply(this);
        string? previous = WuaDeepCheckReport.LoadLatestPrompt();
        if (!string.IsNullOrWhiteSpace(previous))
        {
            ReportBox.Text = previous;
            CopyButton.IsEnabled = true;
            ProgressLabel.Text = "Showing last saved deep-check report. Click Start check to run again.";
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;

        string estimate = $"{(int)WuaDeepCheckService.EstimatedDuration.TotalMinutes:00}:00";
        var choice = AppDialog.Show(this,
            "WUA deep check can take a while (network probes + Windows Update Agent query).\n\n" +
            $"Estimated time: about {estimate} (often 02:00–04:00).\n\n" +
            "This is read-only: it does not repair or install anything.\n\n" +
            "Continue?",
            "Confirm WUA deep check",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (choice != MessageBoxResult.OK) return;

        _running = true;
        StartButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        ProgressBar.IsIndeterminate = true;
        ProgressLabel.Text = "Starting…";
        var log = new StringBuilder();
        log.AppendLine($"=== WUA deep check started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        ReportBox.Text = log.ToString();

        var progress = new Progress<string>(msg =>
        {
            ProgressLabel.Text = msg.Length > 100 ? msg[..100] + "…" : msg;
            log.AppendLine(msg);
            ReportBox.Text = log.ToString();
            ReportBox.CaretIndex = ReportBox.Text.Length;
            ReportBox.ScrollToEnd();
        });

        try
        {
            _report = await WuaDeepCheckService.RunAsync(progress).ConfigureAwait(true);
            string prompt = _report.BuildAiPrompt();
            ReportBox.Text = prompt;
            CopyButton.IsEnabled = true;
            ProgressLabel.Text = _report.FailCount > 0
                ? $"Finished with {_report.FailCount} FAIL and {_report.WarnCount} WARN. Copy the AI prompt for further help."
                : $"Finished. WARN={_report.WarnCount}. Copy the AI prompt if you need a second opinion.";
            MessageBox.Show(this,
                _report.FailCount > 0
                    ? $"Deep check finished with {_report.FailCount} failure(s).\n\nUse Copy AI prompt for troubleshooting."
                    : "Deep check finished.\n\nReview the report or copy the AI prompt.",
                "WUA deep check",
                MessageBoxButton.OK,
                _report.FailCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            ProgressLabel.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            ProgressLabel.Text = "Failed: " + ex.Message;
            MessageBox.Show(this, ex.Message, "WUA deep check", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _running = false;
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = 100;
            StartButton.IsEnabled = true;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(ReportBox.Text ?? "");
            MessageBox.Show(this, "Copied to clipboard.", "WUA deep check",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not copy: " + ex.Message, "WUA deep check",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(WuaDeepCheckReport.DirectoryPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = WuaDeepCheckReport.DirectoryPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "WUA deep check", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            MessageBox.Show(this, "A deep check is still running. Wait until it finishes.",
                "WUA deep check", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Close();
    }
}
