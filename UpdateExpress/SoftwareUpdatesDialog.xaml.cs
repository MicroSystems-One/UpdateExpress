using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using UpdateExpress.Services;

namespace UpdateExpress;

public partial class SoftwareUpdatesDialog : Window
{
    /// <summary>AFK soft-dismiss: same as Later — does not start install.</summary>
    public static readonly TimeSpan AfkAutoClose = TimeSpan.FromMinutes(30);

    public static SoftwareUpdatesDialog? Current { get; private set; }
    public static bool IsShowing => Current is { IsLoaded: true };

    public enum PromptDecision
    {
        Later,
        InstallNow,
        InstallAtPreferred,
        RemindOneHour,
        RemindTomorrow
    }

    public PromptDecision Decision { get; private set; } = PromptDecision.Later;

    private readonly DispatcherTimer _afkTimer;

    public SoftwareUpdatesDialog(
        string displayName,
        IReadOnlyList<PromptUpdateItem> updates,
        string preferredTime,
        bool downloaded)
    {
        InitializeComponent();
        AppIcon.Apply(this);
        Current = this;

        Title = $"Update Express – {displayName} updates";
        bool windowsCategory = displayName is "Critical" or "General";
        HeadingText.Text = windowsCategory
            ? $"{displayName} Windows updates ready"
            : $"{displayName} updates ready";
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

        _afkTimer = new DispatcherTimer { Interval = AfkAutoClose };
        _afkTimer.Tick += (_, _) =>
        {
            _afkTimer.Stop();
            // Soft dismiss — equivalent to Later (no install).
            Decision = PromptDecision.Later;
            try { DialogResult = false; } catch { /* already closing */ }
            Close();
        };
        Loaded += (_, _) => _afkTimer.Start();
        Closed += (_, _) =>
        {
            _afkTimer.Stop();
            if (ReferenceEquals(Current, this))
                Current = null;
        };
    }

    /// <summary>
    /// Show at most one updates-ready dialog. If one is already open, returns false
    /// and leaves <paramref name="decision"/> as <see cref="PromptDecision.Later"/>
    /// without stacking a second window (caller should leave prompt state unchanged
    /// so it can surface after the current dialog closes).
    /// </summary>
    public static bool TryShowModal(
        string displayName,
        IReadOnlyList<PromptUpdateItem> updates,
        string preferredTime,
        bool downloaded,
        out PromptDecision decision)
    {
        decision = PromptDecision.Later;
        if (IsShowing)
            return false;

        var dialog = new SoftwareUpdatesDialog(displayName, updates, preferredTime, downloaded);
        dialog.ShowDialog();
        decision = dialog.Decision;
        return true;
    }

    private void InstallNow_Click(object sender, RoutedEventArgs e)
    {
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressLabel.Text = "Starting installation — administrator approval may be required…";
        ActionsPanel.IsEnabled = false;
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
