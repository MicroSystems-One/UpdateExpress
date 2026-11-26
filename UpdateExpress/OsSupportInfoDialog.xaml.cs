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
    private readonly string _referenceUrl;
    private DispatcherTimer? _copyFeedbackTimer;

    /// <param name="referenceUrl">The lifecycle page to link to; the Windows client page when left out.</param>
    public OsSupportInfoDialog(string details, bool norwegian = true, string? referenceUrl = null)
    {
        InitializeComponent();
        _norwegian = norwegian;
        _referenceUrl = string.IsNullOrWhiteSpace(referenceUrl) ? ReferenceUrl : referenceUrl;
        DetailsText.Text = details?.Trim() ?? "";
        ReferenceLabel.Text = norwegian ? "Referanse:" : "Reference:";
        CopyButton.Content = norwegian ? "Kopier" : "Copy";
        CopyButton.ToolTip = norwegian ? "Kopier lenken" : "Copy the link";
        System.Windows.Automation.AutomationProperties.SetName(
            CopyButton, norwegian ? "Kopier" : "Copy");
        ReferenceLink.NavigateUri = new Uri(_referenceUrl);
        ReferenceLinkText.Text = _referenceUrl;
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
            System.Windows.Clipboard.SetDataObject(_referenceUrl, true);
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
