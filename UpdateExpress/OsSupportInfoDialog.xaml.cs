using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace UpdateExpress;

public partial class OsSupportInfoDialog : Window
{
    public const string ReferenceUrl =
        "https://learn.microsoft.com/windows/release-health/supported-versions-windows-client";

    private readonly bool _norwegian;
    private DispatcherTimer? _copyFeedbackTimer;

    public OsSupportInfoDialog(string details, bool norwegian = true)
    {
        InitializeComponent();
        _norwegian = norwegian;
        DetailsText.Text = details?.Trim() ?? "";
        ReferenceLabel.Text = norwegian ? "Referanse:" : "Reference:";
        CopyButton.Content = norwegian ? "Kopier" : "Copy";
        CopyButton.ToolTip = norwegian ? "Kopier lenken" : "Copy the link";
        System.Windows.Automation.AutomationProperties.SetName(
            CopyButton, norwegian ? "Kopier" : "Copy");
        ReferenceLink.NavigateUri = new Uri(ReferenceUrl);
        ReferenceLinkText.Text = ReferenceUrl;
    }

    private void ReferenceLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        e.Handled = true;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetDataObject(ReferenceUrl, true);
            CopyFeedback.Text = _norwegian ? "Kopiert!" : "Copied!";
            CopyFeedback.Visibility = Visibility.Visible;
            _copyFeedbackTimer?.Stop();
            _copyFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _copyFeedbackTimer.Tick += (_, _) =>
            {
                _copyFeedbackTimer.Stop();
                CopyFeedback.Visibility = Visibility.Collapsed;
            };
            _copyFeedbackTimer.Start();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this,
                (_norwegian ? "Kunne ikke kopiere: " : "Could not copy: ") + ex.Message,
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
