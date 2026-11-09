using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using UpdateExpress.Services;

namespace UpdateExpress;

public partial class ErrorReportDialog : Window
{
    public ErrorReportDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadReport();
    }

    private void LoadReport()
    {
        string? prompt = WindowsUpdateFixReport.LoadLatestPrompt();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            ReportBox.Text =
                "No Windows Update fix report yet.\n\n" +
                "Run Windows Update fix (Quick / Medium / Deep) first.\n" +
                "If any step fails, this dialog will show a copyable AI troubleshooting prompt.";
            return;
        }
        ReportBox.Text = prompt;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(ReportBox.Text ?? "");
            MessageBox.Show(this, "Copied to clipboard.", "Error report",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not copy: " + ex.Message, "Error report",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(WindowsUpdateFixReport.DirectoryPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = WindowsUpdateFixReport.DirectoryPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Error report", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
