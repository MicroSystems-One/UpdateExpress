using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace UpdateExpress;

public partial class ReminderDialog : Window
{
    /// <summary>AFK soft-dismiss: same as Lukk/Close — never confirms restart.</summary>
    public static readonly TimeSpan AfkAutoClose = TimeSpan.FromMinutes(30);

    public static ReminderDialog? Current { get; private set; }
    public static bool IsShowing => Current is { IsLoaded: true };

    public string Decision { get; private set; } = "dismissed";
    /// <summary>Occurrence last applied to this dialog (updated on refresh).</summary>
    public DateTime Occurrence { get; private set; }
    /// <summary>Test vs real mode last applied (updated on refresh) — used when handling Decision.</summary>
    public bool IsTest { get; private set; }
    /// <summary>Whether the open dialog is showing the missed-previous-day notice.</summary>
    public bool ShowMissedPreviousDay { get; private set; }

    private readonly DispatcherTimer _afkTimer;
    private DateTime _afkDeadlineUtc;
    private bool _afkArmed;

    public ReminderDialog(
        bool isTest,
        DateTime occurrence,
        bool norwegian = false,
        bool showMissedPreviousDay = false,
        string reasonKey = "",
        IReadOnlyList<string>? pendingUpdateLines = null)
    {
        InitializeComponent();
        AppIcon.Apply(this);
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Current = this;

        // Absolute AFK deadline from first show — automatic content refreshes must not extend it.
        _afkDeadlineUtc = DateTime.UtcNow.Add(AfkAutoClose);
        _afkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _afkTimer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow < _afkDeadlineUtc) return;
            _afkTimer.Stop();
            // Soft dismiss (Decision stays "dismissed") — equivalent to Lukk / Later.
            Close();
        };

        // After long idle / locked session, DWM sometimes leaves this dialog blank.
        Loaded += (_, _) =>
        {
            ForceRepaint();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ForceRepaint);
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, ForceRepaint);
            if (!_afkArmed)
            {
                _afkArmed = true;
                _afkDeadlineUtc = DateTime.UtcNow.Add(AfkAutoClose);
                _afkTimer.Start();
            }
        };
        Activated += (_, _) => ForceRepaint();
        ContentRendered += (_, _) => ForceRepaint();
        Closed += (_, _) =>
        {
            _afkTimer.Stop();
            if (ReferenceEquals(Current, this))
                Current = null;
        };

        ApplyContent(isTest, occurrence, norwegian, showMissedPreviousDay, reasonKey, pendingUpdateLines);
    }

    /// <summary>
    /// Refresh an already-open Påminnelse instead of stacking a second window.
    /// Does not extend the AFK auto-close deadline.
    /// </summary>
    public void ApplyContent(
        bool isTest,
        DateTime occurrence,
        bool norwegian,
        bool showMissedPreviousDay,
        string reasonKey,
        IReadOnlyList<string>? pendingUpdateLines)
    {
        IsTest = isTest;
        Occurrence = occurrence;
        ShowMissedPreviousDay = showMissedPreviousDay && !isTest;

        var time = Services.ReminderSchedule.Load().Format(occurrence);
        WhenText.Text = isTest
            ? (norwegian ? "Testvarsel – ingen handling utført" : "Notification test – no action performed")
            : (norwegian ? $"Planlagt tidspunkt: {time}" : $"Scheduled time: {time}");

        if (ShowMissedPreviousDay)
        {
            MissedNoticeText.Visibility = Visibility.Visible;
            MissedNoticeText.Text = norwegian
                ? "Vi minner om at planlagt vedlikehold i går ikke ble gjennomført, fordi det manglet bekreftelse."
                : "Reminder: yesterday's scheduled maintenance was not completed because it was not confirmed.";
        }
        else
        {
            MissedNoticeText.Visibility = Visibility.Collapsed;
        }

        if (!isTest && !string.IsNullOrWhiteSpace(reasonKey))
        {
            ReasonText.Visibility = Visibility.Visible;
            ReasonText.Text = reasonKey switch
            {
                "pending" => norwegian
                    ? "Årsak: det finnes oppdateringer på vent."
                    : "Reason: updates are waiting.",
                "reboot" => norwegian
                    ? "Årsak: Windows venter på omstart."
                    : "Reason: Windows is waiting for a restart.",
                "uptime" => norwegian
                    ? "Årsak: oppetiden er 7 dager eller mer (anbefalt vedlikeholdsomstart)."
                    : "Reason: uptime is 7 days or more (recommended maintenance restart).",
                _ => ""
            };
            if (string.IsNullOrWhiteSpace(ReasonText.Text))
                ReasonText.Visibility = Visibility.Collapsed;
        }
        else
        {
            ReasonText.Visibility = Visibility.Collapsed;
        }

        if (!isTest
            && string.Equals(reasonKey, "pending", StringComparison.OrdinalIgnoreCase)
            && pendingUpdateLines is { Count: > 0 })
        {
            PendingListPanel.Visibility = Visibility.Visible;
            PendingUpdatesList.ItemsSource = pendingUpdateLines.ToList();
        }
        else
        {
            PendingListPanel.Visibility = Visibility.Collapsed;
            PendingUpdatesList.ItemsSource = null;
        }

        if (norwegian)
        {
            Title = "Update Express – Påminnelse";
            DialogHeading.Text = "Planlagt Windows-vedlikehold";
            SaveWorkText.Text = "Lagre arbeidet og lukk programmer før du går. Oppdateringer eller omstart kan ellers føre til tap av ulagrede data.";
            PowerText.Text = "La PC-en stå på, koblet til strøm og internett, og unngå hvilemodus. Bekreft planlegger omstart i dette tidsrommet.";
            AcknowledgeButton.Content = "Bekreft omstart";
            SnoozeButton.Content = "Påminn meg om 30 minutter";
            CloseButton.Content = "Lukk";
        }

        if (IsLoaded)
        {
            ForceRepaint();
            try { Activate(); } catch { /* ignore */ }
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource hwnd)
            hwnd.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
    }

    private void ForceRepaint()
    {
        try
        {
            InvalidateVisual();
            UpdateLayout();
            if (Content is UIElement root)
            {
                root.InvalidateVisual();
                root.UpdateLayout();
            }
        }
        catch
        {
            /* never block the reminder */
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) { Decision = "confirmed"; Close(); }
    private void Snooze_Click(object sender, RoutedEventArgs e) { Decision = "snoozed"; Close(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
