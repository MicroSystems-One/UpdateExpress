using System.Collections.Generic;
using System.Linq;
using System.Windows;
using UpdateExpress.Services;

namespace UpdateExpress;

public partial class SoftwareUpdatesDialog : Window
{
    public enum PromptDecision
    {
        Later,
        InstallNow,
        InstallAtPreferred,
        RemindOneHour,
        RemindTomorrow
    }

    public PromptDecision Decision { get; private set; } = PromptDecision.Later;

    public SoftwareUpdatesDialog(
        string displayName,
        IReadOnlyList<PromptUpdateItem> updates,
        string preferredTime,
        bool downloaded)
    {
        InitializeComponent();
        Title = $"Update Express – {displayName} updates";
        HeadingText.Text = $"{displayName} Windows updates ready";
        PreferredButton.Content = $"Install at {preferredTime}";
        int count = updates.Count;
        string kind = displayName.ToLowerInvariant();
        SummaryText.Text = downloaded
            ? $"{count} {kind} update(s) downloaded. Choose when to install:"
            : $"{count} {kind} update(s) found. Download may finish during install. Choose when to install:";
        UpdatesList.ItemsSource = updates
            .Select(u =>
            {
                string label = string.IsNullOrWhiteSpace(u.Priority) ? "Update" : u.Priority;
                string id = string.IsNullOrWhiteSpace(u.KB) ? u.UpdateId : u.KB;
                return $"{label}: {id} — {u.Title}";
            })
            .ToList();
    }

    private void InstallNow_Click(object sender, RoutedEventArgs e)
    {
        Decision = PromptDecision.InstallNow;
        DialogResult = true;
        Close();
    }

    private void Preferred_Click(object sender, RoutedEventArgs e)
    {
        Decision = PromptDecision.InstallAtPreferred;
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
