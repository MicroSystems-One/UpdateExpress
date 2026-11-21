using System;
using System.IO;
using System.Text.Json;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using System.Windows.Media;

namespace UpdateExpress;

public sealed class ProxySettingsWindow : Window
{
    private readonly RadioButton _current = new() { Content = "Use current config", GroupName = "ProxyMode", Margin = new Thickness(0, 8, 0, 5) };
    private readonly RadioButton _manual = new() { Content = "Manually configure", GroupName = "ProxyMode", Margin = new Thickness(0, 0, 0, 14) };
    private readonly TextBox _winHttp = new() { Margin = new Thickness(0, 4, 0, 12) };
    private readonly CheckBox _pacEnabled = new() { Content = "Enable PAC script", Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBox _pac = new() { Margin = new Thickness(0, 4, 0, 12) };
    private readonly TextBox _report = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 170, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
    private static readonly string ConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdateExpress", "proxy-profile.json");
    private sealed record ProxyProfile(bool Manual, string ProxyServer, bool PacEnabled, string PacUrl);

    public ProxySettingsWindow()
    {
        Title = "Update Express – Proxy Settings";
        AppIcon.Apply(this);
        Width = 650; Height = 680; MinWidth = 520; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(18) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "Proxy configuration mode", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(_current); panel.Children.Add(_manual);
        panel.Children.Add(new TextBlock { Text = "Proxy server (host:port)", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(_winHttp);
        panel.Children.Add(_pacEnabled);
        panel.Children.Add(new TextBlock { Text = "PAC script URL (HTTP/HTTPS)", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(_pac);
        panel.Children.Add(new TextBlock {
            Text = "Manual HTTP proxy is used by the connection test only. Windows Update, WinGet and Windows system settings remain unchanged. Manual PAC is saved but PAC execution is not implemented; use current Windows configuration for PAC routing.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 12)
        });
        // Fixed-height grid keeps all three actions on the same baseline.
        var actions = new System.Windows.Controls.Grid {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 16), Height = 28
        };
        actions.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(125) });
        actions.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(8) });
        actions.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(135) });
        var test = new Button { Content = "Test connection", Height = 28, Padding = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var save = new Button { Content = "Save configuration", Height = 28, Padding = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        test.Click += async (_, _) => await TestConnectionAsync(test);
        save.Click += (_, _) => Save();
        System.Windows.Controls.Grid.SetColumn(test, 0);
        System.Windows.Controls.Grid.SetColumn(save, 2);
        actions.Children.Add(test); actions.Children.Add(save);
        panel.Children.Add(actions);
        panel.Children.Add(new TextBlock { Text = "Current Windows configuration (read only)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) });
        panel.Children.Add(_report);
        _current.Checked += (_, _) => UpdateEnabled();
        _manual.Checked += (_, _) => UpdateEnabled();
        _pacEnabled.Checked += (_, _) => UpdateEnabled();
        _pacEnabled.Unchecked += (_, _) => UpdateEnabled();
        Load();
        _report.Text = Services.ProxyDiagnostics.ReadOnlyReport();
    }
    private void UpdateEnabled()
    {
        bool manual = _manual.IsChecked == true;
        _winHttp.IsEnabled = manual;
        _pacEnabled.IsEnabled = manual;
        _pac.IsEnabled = manual && _pacEnabled.IsChecked == true;
    }
    private void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var saved = JsonSerializer.Deserialize<ProxyProfile>(File.ReadAllText(ConfigPath));
                if (saved != null)
                {
                    _winHttp.Text = saved.ProxyServer;
                    _pacEnabled.IsChecked = saved.PacEnabled;
                    _pac.Text = saved.PacUrl;
                    if (saved.Manual) _manual.IsChecked = true;
                    else _current.IsChecked = true;
                }
            }
        }
        catch (Exception ex) { MessageBox.Show(this, "Unable to read saved profile: " + ex.Message, "Proxy Settings", MessageBoxButton.OK, MessageBoxImage.Warning); }
        if (_current.IsChecked != true && _manual.IsChecked != true) _current.IsChecked = true;
        UpdateEnabled();
    }
    private async Task TestConnectionAsync(Button button)
    {
        button.IsEnabled = false;
        var previous = button.Content;
        button.Content = "Please wait...";
        try
        {
            bool manual = _manual.IsChecked == true;
            string server = _winHttp.Text.Trim();
            if (manual && _pacEnabled.IsChecked == true)
            {
                MessageBox.Show(this, "Manual PAC execution is not supported yet. Choose 'Use current config' to test using the Windows user's configured proxy/PAC, or disable PAC and enter a manual host:port.", "PAC test unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            using var handler = new HttpClientHandler();
            if (manual)
            {
                if (server.Length == 0) handler.UseProxy = false;
                else
                {
                    if (!Uri.TryCreate("http://" + server, UriKind.Absolute, out var proxyUri) || proxyUri.Port < 1 || proxyUri.Port > 65535 || proxyUri.Host.Length == 0 || server.Contains('/'))
                        throw new ArgumentException("Enter a valid proxy as host:port.");
                    handler.UseProxy = true;
                    handler.Proxy = new WebProxy(proxyUri);
                }
            }
            else handler.UseProxy = true; // Default Windows per-user proxy resolver.
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            using var response = await client.GetAsync("https://www.microsoft.com/", HttpCompletionOption.ResponseHeadersRead);
            string route = manual ? (server.Length == 0 ? "Direct (manual)" : "Manual proxy " + server) : "Windows current user proxy (system resolver)";
            MessageBox.Show(this, $"Route: {route}\nHTTP status: {(int)response.StatusCode} {response.ReasonPhrase}\nTarget: https://www.microsoft.com/\n\nThis tests Update Express HTTP connectivity only, not WUA or WinGet.", "Proxy connection test", MessageBoxButton.OK, response.IsSuccessStatusCode ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) { MessageBox.Show(this, "Connection test failed: " + ex.Message, "Proxy connection test", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { button.Content = previous; button.IsEnabled = true; }
    }

    private void Save()
    {
        bool manual = _manual.IsChecked == true;
        string server = _winHttp.Text.Trim();
        string pac = _pac.Text.Trim();
        if (manual && server.Length > 0)
        {
            var parts = server.Split(':');
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || parts[0].Contains(' ') || parts[0].Contains('/') || !int.TryParse(parts[1], out int port) || port < 1 || port > 65535)
            { MessageBox.Show(this, "Proxy must be host:port, for example kunde.proxy.nhn.no:8080.", "Invalid proxy", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        }
        if (manual && _pacEnabled.IsChecked == true && (!Uri.TryCreate(pac, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        { MessageBox.Show(this, "Enter a valid HTTP or HTTPS PAC URL.", "Invalid PAC URL", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            var profile = new ProxyProfile(manual, server, _pacEnabled.IsChecked == true, pac);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
            MessageBox.Show(this, "Proxy profile saved. Windows system settings were not changed. Manual proxy is available for the connection test only. Windows Update and WinGet are unchanged.", "Proxy Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Unable to save proxy profile", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
