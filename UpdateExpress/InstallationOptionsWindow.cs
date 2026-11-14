using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace UpdateExpress;
public sealed class InstallationOptionsWindow : Window
{
    private readonly System.Windows.Controls.RadioButton _current = new() { Content = "Current user only", IsChecked = true, Margin = new Thickness(0, 6, 0, 4) };
    private readonly System.Windows.Controls.RadioButton _all = new() { Content = "All users (administrator required)", Margin = new Thickness(0, 0, 0, 12) };
    private readonly System.Windows.Controls.CheckBox _desktop = new() { Content = "Create desktop shortcut", IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
    private readonly System.Windows.Controls.CheckBox _menu = new() { Content = "Create Start Menu shortcut", IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
    private readonly System.Windows.Controls.CheckBox _agent = new() { Content = "Enable background monitoring at sign-in (current user)", IsChecked = true, Margin = new Thickness(0, 4, 0, 12) };
    public bool AllUsers => _all.IsChecked == true;
    public bool DesktopShortcut => _desktop.IsChecked == true;
    public bool StartMenuShortcut => _menu.IsChecked == true;
    public bool BackgroundAgent => _agent.IsChecked == true;
    public InstallationOptionsWindow(bool repair)
    {
        Title = repair ? "Update Express – Repair options" : "Update Express – Installation options";
        Width = 490; Height = 430; MinHeight = 430; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White;
        // Keep the action row anchored at the bottom, independent of text wrapping or DPI.
        var root = new Grid { Margin = new Thickness(24, 24, 24, 30) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var content = new StackPanel();
        Grid.SetRow(content, 0);
        root.Children.Add(content);
        content.Children.Add(new TextBlock { Text = repair ? "Repair Update Express" : "Install Update Express", FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 20, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = "Installation scope", Margin = new Thickness(0, 14, 0, 0), FontSize = 14, FontWeight = FontWeights.SemiBold });
        _all.Checked += (_, _) => { _agent.IsChecked = false; _agent.IsEnabled = false; };
        _current.Checked += (_, _) => { _agent.IsEnabled = true; };
        content.Children.Add(_current); content.Children.Add(_all);
        content.Children.Add(new TextBlock { Text = "Shortcuts and monitoring", FontSize = 14, FontWeight = FontWeights.SemiBold });
        content.Children.Add(_desktop); content.Children.Add(_menu); content.Children.Add(_agent);
        content.Children.Add(new TextBlock { Text = "All-users installation installs the app for everyone; tray monitoring is configured for the installing user only.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.DimGray });

        var buttons = new Grid { HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 4) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        Grid.SetRow(buttons, 2);
        var ok = new Button { Content = repair ? "Repair" : "Install", Width = 90, Height = 36, MinHeight = 36, Padding = new Thickness(0), Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch, IsDefault = true };
        ok.Click += (_, _) => { DialogResult = true; };
        var cancel = new Button { Content = "Cancel", Width = 90, Height = 36, MinHeight = 36, Padding = new Thickness(0), Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch, IsCancel = true };
        cancel.Click += (_, _) => { DialogResult = false; };
        // Primary action left, Cancel right (Install | Cancel / Repair | Cancel).
        Grid.SetColumn(ok, 0);
        Grid.SetColumn(cancel, 2);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
    }
}
