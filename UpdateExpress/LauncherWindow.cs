using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using UpdateExpress.Services;
namespace UpdateExpress;

public sealed class LauncherWindow : Window
{
    public LauncherWindow()
    {
        Title = "Update Express – MicroSystems";
        Width = 460; Height = 370; MinWidth = 440; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(247, 249, 252));
        var root = new StackPanel { Margin = new Thickness(28) };
        Content = root;
        root.Children.Add(new TextBlock { Text = "Update Express", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(30, 43, 60)) });
        root.Children.Add(new TextBlock { Text = "MicroSystems · Startup and maintenance", Margin = new Thickness(0, 3, 0, 3), Foreground = Brushes.DimGray });
        root.Children.Add(new TextBlock { Text = $"Version {ProductVersion.Display}", FontSize = 12, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 18) });
        var grid = new UniformGrid { Columns = 2, Rows = 2 };
        root.Children.Add(grid);
        Add("▶  Run", "Open Update Express", () => {
            if (!ElevationHelper.IsElevated)
            {
                try { ElevationHelper.RestartElevated(); Close(); }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { /* User cancelled UAC. */ }
                catch (Exception ex) { MessageBox.Show("Could not request administrator privileges: " + ex.Message, "Update Express", MessageBoxButton.OK, MessageBoxImage.Error); }
                return;
            }
            new MainWindow().Show(); Close();
        });
        Add("↓  Install", "Install Update Express", () => InstallationManager.InstallOrRepair(false));
        Add("⚒  Repair", "Repair installation", () => InstallationManager.InstallOrRepair(true));
        Add("×  Uninstall", "Remove application", InstallationManager.Uninstall);
        root.Children.Add(new TextBlock { Text = "No updates are installed automatically.", Margin = new Thickness(0, 20, 0, 0), Foreground = Brushes.Gray, FontSize = 12 });
        void Add(string title, string subtitle, Action action)
        {
            var b = new Button { Margin = new Thickness(0, 0, 10, 10), Height = 95, Padding = new Thickness(12), Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(215, 222, 232)) };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = System.Windows.HorizontalAlignment.Center });
            stack.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = Brushes.Gray, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) });
            b.Content = stack;
            b.Click += (_, _) => { try { action(); } catch (Exception ex) { MessageBox.Show(ex.Message, "Update Express", MessageBoxButton.OK, MessageBoxImage.Error); } };
            grid.Children.Add(b);
        }
    }
}
