using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;

namespace UpdateExpress;

public partial class OsSupportDialog : Window
{
    public const string ReferenceUrl =
        "https://learn.microsoft.com/windows/release-health/supported-versions-windows-client";

    private readonly string _url;
    private readonly bool _norwegian;

    public OsSupportDialog(string details, bool norwegian = false)
    {
        InitializeComponent();
        _url = ReferenceUrl;
        _norwegian = norwegian;
        DetailsText.Text = string.IsNullOrWhiteSpace(details) ? "" : details.Trim();
        ReferenceLink.NavigateUri = new Uri(_url);
        ReferenceLinkText.Text = _url;

        if (norwegian)
        {
            Title = "Windows sikkerhetsstøtte";
            ReferenceLabel.Text = "Referanse:";
            CopyButton.Content = "Kopier lenke";
            CopyButton.ToolTip = "Kopier referanse-URL til utklippstavlen";
            ReferenceLink.ToolTip = "Åpne i standard nettleser";
            OkButton.Content = "OK";
        }

        try
        {
            // SystemIcons.Information is a shared system icon — do not Dispose.
            InfoIcon.Source = Imaging.CreateBitmapSourceFromHIcon(
                SystemIcons.Information.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
        }
        catch
        {
            InfoIcon.Visibility = Visibility.Collapsed;
        }

        Loaded += (_, _) => OkButton.Focus();
    }

    private void ReferenceLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        e.Handled = true;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(_url);
            CopyButton.Content = _norwegian ? "Kopiert" : "Copied";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not copy: " + ex.Message, Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
