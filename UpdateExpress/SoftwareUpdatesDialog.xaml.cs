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

    // Claimed before the window is created and released in a finally, so a prompt queued by
    // another category is refused however far the first dialog has come.
    private static int _modalOpen;

    /// <summary>True from the moment a modal updates-ready dialog is claimed until it has closed.</summary>
    public static bool IsShowing => Volatile.Read(ref _modalOpen) != 0;

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
        bool downloaded,
        TimeSpan? autoClose = null)
    {
        InitializeComponent();
        AppIcon.Apply(this);

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

        _afkTimer = new DispatcherTimer { Interval = autoClose ?? AfkAutoClose };
        _afkTimer.Tick += (_, _) =>
        {
            _afkTimer.Stop();
            // Soft dismiss — equivalent to Later (no install).
            Decision = PromptDecision.Later;
            Close();
        };
        Loaded += (_, _) => _afkTimer.Start();
        Closed += (_, _) => _afkTimer.Stop();
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
        out PromptDecision decision) =>
        TryShowModal(
            () => new SoftwareUpdatesDialog(displayName, updates, preferredTime, downloaded),
            out decision);

    internal static bool TryShowModal(Func<SoftwareUpdatesDialog> create, out PromptDecision decision)
    {
        decision = PromptDecision.Later;
        if (Interlocked.CompareExchange(ref _modalOpen, 1, 0) != 0)
            return false;

        try
        {
            var dialog = create();
            dialog.ShowDialog();
            decision = dialog.Decision;
            return true;
        }
        finally
        {
            Volatile.Write(ref _modalOpen, 0);
        }
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
