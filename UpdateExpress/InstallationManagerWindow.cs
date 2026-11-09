using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace UpdateExpress;

public sealed class InstallationQueueItem(string source, string name) : System.ComponentModel.INotifyPropertyChanged
{
    public string Source { get; } = source;
    public string Name { get; } = name;
    private string _state = "Pending";
    public string State { get => _state; set { _state = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(State))); } }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed class InstallationManagerWindow : Window
{
    private readonly ObservableCollection<InstallationQueueItem> _items;
    private readonly TextBlock _stage;
    private readonly TextBlock _counter;
    private readonly System.Windows.Controls.ProgressBar _bar;
    private readonly Button _start;
    private readonly Button _cancel;
    private readonly DispatcherTimer _clock;
    private readonly TextBlock _elapsed;
    private DateTime _started;
    private bool _running;
    private readonly System.Windows.Controls.ListView _list;
    public InstallationManagerWindow(List<InstallationQueueItem> items)
    {
        Title = "Update Express – Installation Manager";
        Width = 760; Height = 530; MinWidth = 590; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(247, 249, 252));
        _items = new ObservableCollection<InstallationQueueItem>(items);
        var root = new DockPanel { Margin = new Thickness(20) };
        Content = root;
        // Fixed-height footer grid: both buttons share exactly one 27-DIP row.
        // A local template removes theme-specific minimum sizing and internal padding.
        var bottom = new Grid { Height = 27, MinHeight = 27, MaxHeight = 27, VerticalAlignment = System.Windows.VerticalAlignment.Bottom, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        bottom.RowDefinitions.Add(new RowDefinition { Height = new GridLength(27) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        var buttonStyle = CreateFooterButtonStyle();
        _start = new Button { Content = "Install selected updates", Style = buttonStyle, Height = 27, MinHeight = 27, MaxHeight = 27, Padding = new Thickness(8, 0, 8, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch, VerticalAlignment = System.Windows.VerticalAlignment.Stretch };
        _cancel = new Button { Content = "Close", Style = buttonStyle, Height = 27, MinHeight = 27, MaxHeight = 27, Padding = new Thickness(8, 0, 8, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch, VerticalAlignment = System.Windows.VerticalAlignment.Stretch };
        _start.Click += (_, _) => { };
        _cancel.Click += (_, _) => Close();
        Grid.SetRow(_start, 0); Grid.SetColumn(_start, 0);
        Grid.SetRow(_cancel, 0); Grid.SetColumn(_cancel, 2);
        bottom.Children.Add(_start);
        bottom.Children.Add(_cancel);
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Installation Manager", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        top.Children.Add(new TextBlock { Text = "Review the combined Windows Update / driver / WinGet queue. No automatic restart.", FontSize = 12, Margin = new Thickness(0, 0, 0, 16) });
        _stage = new TextBlock { Text = "Ready to install", FontSize = 13, FontWeight = FontWeights.SemiBold };
        top.Children.Add(_stage);
        _bar = new System.Windows.Controls.ProgressBar { Minimum = 0, Maximum = items.Count, Value = 0, Height = 9, Margin = new Thickness(0, 9, 0, 8) };
        top.Children.Add(_bar);
        var meta = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        _counter = new TextBlock { Text = $"0 of {items.Count} processed", FontSize = 12, Width = 170 };
        _elapsed = new TextBlock { Text = "Elapsed: 00:00", FontSize = 12 };
        meta.Children.Add(_counter); meta.Children.Add(_elapsed); top.Children.Add(meta);
        var list = new System.Windows.Controls.ListView { ItemsSource = _items, BorderThickness = new Thickness(0), Background = Brushes.White, Margin = new Thickness(1), HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch };
        _list = list;
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "Source", Width = 125, DisplayMemberBinding = new System.Windows.Data.Binding("Source") });
        view.Columns.Add(new GridViewColumn { Header = "Update", Width = 410, DisplayMemberBinding = new System.Windows.Data.Binding("Name") });
        view.Columns.Add(new GridViewColumn { Header = "Status", Width = 140, DisplayMemberBinding = new System.Windows.Data.Binding("State") });
        list.View = view;
        var frame = new Border { CornerRadius = new CornerRadius(10), BorderBrush = new SolidColorBrush(Color.FromRgb(211, 221, 234)), BorderThickness = new Thickness(1), Background = Brushes.White, SnapsToDevicePixels = true, Child = list };
        root.Children.Add(frame);
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => { var elapsed = DateTime.Now - _started; _elapsed.Text = "Elapsed: " + elapsed.ToString(@"hh\:mm\:ss"); };
        Closing += (_, e) => { if (_running) e.Cancel = true; };
    }
    private static Style CreateFooterButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, Brushes.White));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(35, 48, 65))));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(175, 190, 208))));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.BorderThicknessProperty, new Thickness(1)));
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "ButtonBorder";
        border.SetValue(Border.BackgroundProperty, new System.Windows.TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new System.Windows.TemplateBindingExtension(System.Windows.Controls.Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new System.Windows.TemplateBindingExtension(System.Windows.Controls.Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentProperty, new System.Windows.TemplateBindingExtension(ContentControl.ContentProperty));
        presenter.SetValue(ContentPresenter.ContentTemplateProperty, new System.Windows.TemplateBindingExtension(ContentControl.ContentTemplateProperty));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(235, 242, 250)), "ButtonBorder"));
        template.Triggers.Add(hover);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.5));
        template.Triggers.Add(disabled);
        style.Setters.Add(new Setter(System.Windows.Controls.Control.TemplateProperty, template));
        return style;
    }

    public void PrepareRunning()
    {
        _running = true; _started = DateTime.Now; _clock.Start(); _start.IsEnabled = false; _cancel.IsEnabled = false;
    }
    public void BeginItem(int index, string stage)
    {
        if (index >= 0 && index < _items.Count) _items[index].State = "Installing...";
        SetStage(stage);
    }
    public void SetStage(string stage) => _stage.Text = stage;
    public void CompleteItem(int index, string state, bool failed)
    {
        if (index >= 0 && index < _items.Count) _items[index].State = state;
        _bar.Value = _items.Count(x => x.State != "Pending" && x.State != "Installing..." && x.State != "Verifying");
        _counter.Text = $"{_bar.Value:0} of {_items.Count} processed";
    }
    public void Finish()
    {
        _running = false;
        _clock.Stop();
        _elapsed.Text = "Elapsed: " + (DateTime.Now - _started).ToString(@"hh\:mm\:ss");
        for (int i = 0; i < _items.Count; i++)
            if (_items[i].State is "Pending" or "Installing..." or "Verifying")
                _items[i].State = "Not verified";
        _bar.Value = _items.Count;
        _counter.Text = $"{_items.Count} of {_items.Count} processed";
        int failed = _items.Count(x => x.State.StartsWith("Failed", StringComparison.OrdinalIgnoreCase));
        int unverified = _items.Count(x => x.State is "Not verified" or "Check results");
        int completed = _items.Count(x => x.State == "Completed");
        _stage.Text = $"Finished: {completed} completed, {failed} failed, {unverified} unverified";
        _cancel.Content = "Close"; _cancel.IsEnabled = true;
    }
}
