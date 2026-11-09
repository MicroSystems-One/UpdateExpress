using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using UpdateExpress.Services;

namespace UpdateExpress;

public partial class CriticalUpdatesDialog : Window
{
    public enum PromptDecision
    {
        Later,
        InstallNow,
        InstallTonight,
        RemindOneHour,
        RemindTomorrow
    }

    public PromptDecision Decision { get; private set; } = PromptDecision.Later;

    public CriticalUpdatesDialog(IReadOnlyList<CriticalPromptUpdate> updates, string preferredTime, bool downloaded)
    {
        InitializeComponent();
        TonightButton.Content = $"Install at {preferredTime}";
        int count = updates.Count;
        SummaryText.Text = downloaded
            ? $"{count} critical/important update(s) downloaded. Choose when to install:"
            : $"{count} critical/important update(s) found. Download may finish during install. Choose when to install:";
        UpdatesList.ItemsSource = updates
            .Select(u => $"{u.Priority}: {(string.IsNullOrWhiteSpace(u.KB) ? u.UpdateId : u.KB)} — {u.Title}")
            .ToList();
    }

    private void InstallNow_Click(object sender, RoutedEventArgs e)
    {
        Decision = PromptDecision.InstallNow;
        DialogResult = true;
        Close();
    }

    private void Tonight_Click(object sender, RoutedEventArgs e)
    {
        Decision = PromptDecision.InstallTonight;
        DialogResult = true;
        Close();
    }

    private void RemindHour_Click(object sender, RoutedEventArgs e)
    {
        Decision = PromptDecision.RemindOneHour;
        DialogResult = true;
        Close();
    }

    private void RemindDay_Click(object sender, RoutedEventArgs e)
    {
        Decision = PromptDecision.RemindTomorrow;
        DialogResult = true;
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        Decision = PromptDecision.Later;
        DialogResult = false;
        Close();
    }
}
