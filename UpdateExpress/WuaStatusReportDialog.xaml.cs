using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using UpdateExpress.Services;

namespace UpdateExpress;

public partial class WuaStatusReportDialog : Window
{
    public WuaStatusReportDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadList();
    }

    private void LoadList()
    {
        var reports = WuaDeepCheckReport.ListSavedReports();
        ReportCombo.Items.Clear();
        if (reports.Count == 0)
        {
            ReportBox.Text =
                "No WUA deep check reports yet.\n\n" +
                "Run WUA deep check first. Reports are saved under:\n" +
                WuaDeepCheckReport.DirectoryPath;
            ReportCombo.IsEnabled = false;
            return;
        }

        ReportCombo.IsEnabled = true;
        ReportCombo.DisplayMemberPath = nameof(WuaDeepCheckReport.SavedReport.DisplayName);
        foreach (var r in reports)
            ReportCombo.Items.Add(r);
        ReportCombo.SelectedIndex = 0;
    }

    private void ReportCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReportCombo.SelectedItem is not WuaDeepCheckReport.SavedReport selected)
            return;
        string? text = WuaDeepCheckReport.LoadPromptFromFile(selected.Path);
        ReportBox.Text = string.IsNullOrWhiteSpace(text)
            ? "Could not read report:\n" + selected.Path
            : text;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(ReportBox.Text ?? "");
            MessageBox.Show(this, "Copied to clipboard.", "WUA status report",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not copy: " + ex.Message, "WUA status report",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
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
            MessageBox.Show(this, ex.Message, "WUA status report", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
