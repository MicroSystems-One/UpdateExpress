using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UpdateExpress.Services;

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
    private readonly TextBlock _notice;
    private readonly TextBlock _counter;
    private readonly System.Windows.Controls.ProgressBar _bar;
    private readonly Button _start;
    private readonly Button _cancel;
    private readonly DispatcherTimer _clock;
    private readonly TextBlock _elapsed;
    private DateTime _started;
    private bool _running;
    private readonly System.Windows.Controls.ListView _list;
    public InstallationManagerWindow(List<InstallationQueueItem> items, string? subtitle = null)
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
        top.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(subtitle) ? "Review the combined Windows Update / driver / WinGet queue. No automatic restart." : subtitle, FontSize = 12, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
        _notice = new TextBlock
        {
            Text = WuaProgressText.PleaseWait(WuaProgressText.Norwegian),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(82, 97, 122)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        top.Children.Add(_notice);
        _stage = new TextBlock { Text = "Ready to install", FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
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
        // Status ~30% wider than previous 140; centered Installing/Pending/Completed text.
        var statusColumn = new GridViewColumn { Header = "Status", Width = 182 };
        var statusFactory = new FrameworkElementFactory(typeof(TextBlock));
        statusFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("State"));
        statusFactory.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        statusFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Stretch);
        statusFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        statusColumn.CellTemplate = new DataTemplate { VisualTree = statusFactory };
        view.Columns.Add(statusColumn);
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
    public void SetNotice(string text) => _notice.Text = text;
    public void SetItemState(int index, string state)
    {
        if (index < 0 || index >= _items.Count) return;
        _items[index].State = state;
        _list.ScrollIntoView(_items[index]);
    }

    public void ApplyActivity(WuaActivity activity, bool norwegian)
    {
        int row = FindRow(activity.Kb);
        int number = row >= 0 ? row + 1 : activity.Number;
        int total = _items.Count > 0 ? _items.Count : Math.Max(activity.Total, 1);
        if (row < 0 && _items.Count > 0 && _items.Count == activity.Total)
            row = Math.Clamp(activity.Number - 1, 0, _items.Count - 1);
        if (activity.Phase == "downloaded")
        {
            if (row >= 0) SetItemState(row, norwegian ? "Lastet ned" : "Downloaded");
            return;
        }
        if (row >= 0 && activity.Phase is "download" or "install")
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (i == row) continue;
                if (IsActiveDownload(_items[i].State))
                    _items[i].State = norwegian ? "Lastet ned" : "Downloaded";
            }
            string state = activity.Phase == "download"
                ? (norwegian ? "Laster ned" : "Downloading")
                : (norwegian ? "Installerer" : "Installing");
            if (activity.Percent >= 0) state += $" {activity.Percent}%";
            else state += "…";
            SetItemState(row, state);
        }
        string label = string.IsNullOrWhiteSpace(activity.Kb) ? "…" : activity.Kb;
        _stage.Text = WuaProgressText.Stage(norwegian, activity.Phase, label, number, total, activity.Percent);
        _counter.Text = activity.Phase switch
        {
            "download" => norwegian ? $"Laster ned {number} av {total}" : $"Downloading {number} of {total}",
            "install" => norwegian ? $"Installerer {number} av {total}" : $"Installing {number} of {total}",
            _ => $"{number} of {total}"
        };
        double slot = total <= 0 ? 0 : (Math.Max(number, 1) - 1 + (activity.Percent < 0 ? 0.08 : activity.Percent / 100.0)) / total;
        double fraction = activity.Phase == "install" ? 0.5 + slot * 0.5 : slot * 0.5;
        _bar.IsIndeterminate = false;
        _bar.Value = Math.Clamp(fraction, 0, 1) * _bar.Maximum;
    }

    private int FindRow(string kb)
    {
        if (string.IsNullOrWhiteSpace(kb)) return -1;
        for (int i = 0; i < _items.Count; i++)
            if (_items[i].Name.Contains(kb, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static bool IsActiveDownload(string state) =>
        state.StartsWith("Downloading", StringComparison.Ordinal) || state.StartsWith("Laster ned", StringComparison.Ordinal);

    private static bool IsStillRunning(string state) =>
        state is "Pending" or "Installing..." or "Verifying" or "Downloaded" or "Lastet ned"
        || state.StartsWith("Downloading", StringComparison.Ordinal)
        || state.StartsWith("Installing", StringComparison.Ordinal)
        || state.StartsWith("Laster ned", StringComparison.Ordinal)
        || state.StartsWith("Installerer", StringComparison.Ordinal);

    public void CompleteItem(int index, string state, bool failed)
    {
        if (index >= 0 && index < _items.Count) _items[index].State = state;
        _bar.IsIndeterminate = false;
        _bar.Value = _items.Count(x => !IsStillRunning(x.State));
        _counter.Text = $"{_bar.Value:0} of {_items.Count} processed";
    }
    public void Finish()
    {
        _running = false;
        _clock.Stop();
        _elapsed.Text = "Elapsed: " + (DateTime.Now - _started).ToString(@"hh\:mm\:ss");
        for (int i = 0; i < _items.Count; i++)
            if (IsStillRunning(_items[i].State))
                _items[i].State = "Not verified";
        _bar.IsIndeterminate = false;
        _bar.Value = _items.Count;
        _counter.Text = $"{_items.Count} of {_items.Count} processed";
        int failed = _items.Count(x => x.State.StartsWith("Failed", StringComparison.OrdinalIgnoreCase));
        int unverified = _items.Count(x => x.State is "Not verified" or "Check results");
        int completed = _items.Count(x => x.State == "Completed");
        _stage.Text = $"Finished: {completed} completed, {failed} failed, {unverified} unverified";
        _cancel.Content = "Close"; _cancel.IsEnabled = true;
    }
}
