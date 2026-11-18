using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace UpdateExpress;

public partial class ReminderDialog : Window
{
    public string Decision { get; private set; } = "dismissed";

    public ReminderDialog(bool isTest, DateTime occurrence, bool norwegian = false)
    {
        InitializeComponent();
        AppIcon.Apply(this);
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        // After long idle / locked session, DWM sometimes leaves this dialog blank.
        // Software composition for this small modal avoids that empty white frame.
        Loaded += (_, _) =>
        {
            ForceRepaint();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ForceRepaint);
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, ForceRepaint);
        };
        Activated += (_, _) => ForceRepaint();
        ContentRendered += (_, _) => ForceRepaint();

        var time = Services.ReminderSchedule.Load().Format(occurrence);
        WhenText.Text = isTest
            ? (norwegian ? "Testvarsel – ingen handling utført" : "Notification test – no action performed")
            : (norwegian ? $"Planlagt tidspunkt: {time}" : $"Scheduled time: {time}");
        if (norwegian)
        {
            Title = "Update Express – Påminnelse";
            DialogHeading.Text = "Planlagt Windows-vedlikehold";
            SaveWorkText.Text = "Lagre arbeidet og lukk programmer før du går. Oppdateringer eller omstart kan ellers føre til tap av ulagrede data.";
            PowerText.Text = "La PC-en stå på, koblet til strøm og internett, og unngå hvilemodus. Oppdateringer kan kreve omstart.";
            PilotText.Text = "PILOT: Dette varselet starter ikke oppdateringer eller omstart av Windows.";
            AcknowledgeButton.Content = "Bekreft";
            SnoozeButton.Content = "Påminn meg om 30 minutter";
            CloseButton.Content = "Lukk";
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
