using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UpdateExpress.Services;

namespace UpdateExpress;

public sealed class InstallationQueueItem(string source, string name) : System.ComponentModel.INotifyPropertyChanged
{
    public string Source { get; } = source;
    public string Name { get; } = name;
    /// <summary>WUA UpdateID of the queued update. Empty for WinGet. Ties the outcome row to its Windows Update history event.</summary>
    public string UpdateId { get; init; } = "";
    private string _state = "Pending";
    public string State
    {
        get => _state;
        set
        {
            _state = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(State)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsClickableStatus)));
        }
    }
    private string? _tip;
    public string? Tip
    {
        get => _tip;
        set
        {
            _tip = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Tip)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsClickableStatus)));
        }
    }
    /// <summary>Non-success terminal statuses can open installation help.</summary>
    public bool IsClickableStatus => InstallationManagerWindow.IsHelpfulStatus(_state);
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
    private readonly TextBlock _autoCloseCountdown;
    private DispatcherTimer? _autoCloseTimer;
    private readonly bool _norwegian;
    private DateTime _started;
    private bool _running;
    private readonly System.Windows.Controls.ListView _list;

    /// <summary>Matches Installation Manager's existing post-finish auto-close (15 s).</summary>
    private const int AutoCloseSeconds = 15;
    public InstallationManagerWindow(List<InstallationQueueItem> items, string? subtitle = null, bool norwegian = false)
    {
        _norwegian = norwegian;
        Title = norwegian ? "Update Express – Installasjonsbehandling" : "Update Express – Installation Manager";
        AppIcon.Apply(this);
        Width = 760; Height = 530; MinWidth = 590; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(247, 249, 252));
        _items = new ObservableCollection<InstallationQueueItem>(items);
        foreach (var item in _items)
        {
            if (item.State is "Pending" or "Venter")
                item.State = norwegian ? "Venter" : "Pending";
        }
        var root = new DockPanel { Margin = new Thickness(20) };
        Content = root;
        // Fixed-height footer: countdown (when finished) + Install + Close on one 27-DIP row.
        // A local template removes theme-specific minimum sizing and internal padding.
        var bottom = new Grid { Height = 27, MinHeight = 27, MaxHeight = 27, VerticalAlignment = System.Windows.VerticalAlignment.Bottom, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch, Margin = new Thickness(0, 12, 0, 0) };
        bottom.RowDefinitions.Add(new RowDefinition { Height = new GridLength(27) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        var buttonStyle = CreateFooterButtonStyle();
        _autoCloseCountdown = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x52, 0x61, 0x7A)),
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 4, 0),
            Visibility = Visibility.Collapsed
        };
        _start = new Button
        {
            Content = norwegian ? "Installer valgte oppdateringer" : "Install selected updates",
            Style = buttonStyle, Height = 27, MinHeight = 27, MaxHeight = 27,
            Padding = new Thickness(8, 0, 8, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch
        };
        _cancel = new Button
        {
            Content = norwegian ? "Lukk" : "Close",
            Style = buttonStyle, Height = 27, MinHeight = 27, MaxHeight = 27,
            Padding = new Thickness(8, 0, 8, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch
        };
        _start.Click += (_, _) => { };
        _cancel.Click += (_, _) => Close();
        Grid.SetRow(_autoCloseCountdown, 0); Grid.SetColumn(_autoCloseCountdown, 1);
        Grid.SetRow(_start, 0); Grid.SetColumn(_start, 3);
        Grid.SetRow(_cancel, 0); Grid.SetColumn(_cancel, 5);
        bottom.Children.Add(_autoCloseCountdown);
        bottom.Children.Add(_start);
        bottom.Children.Add(_cancel);
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock
        {
            Text = norwegian ? "Installasjonsbehandling" : "Installation Manager",
            FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8)
        });
        string defaultSubtitle = norwegian
            ? "Gå gjennom den kombinerte køen for Windows Update / drivern / WinGet. Ingen automatisk omstart."
            : "Review the combined Windows Update / driver / WinGet queue. No automatic restart.";
        top.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(subtitle) ? defaultSubtitle : subtitle,
            FontSize = 12, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap
        });
        _notice = new TextBlock
        {
            Text = WuaProgressText.PleaseWait(norwegian),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(82, 97, 122)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        top.Children.Add(_notice);
        _stage = new TextBlock
        {
            Text = norwegian ? "Klar til å installere" : "Ready to install",
            FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap
        };
        top.Children.Add(_stage);
        _bar = new System.Windows.Controls.ProgressBar { Minimum = 0, Maximum = items.Count, Value = 0, Height = 9, Margin = new Thickness(0, 9, 0, 8) };
        top.Children.Add(_bar);
        var meta = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        _counter = new TextBlock { Text = FormatProcessed(0, items.Count), FontSize = 12, Width = 200 };
        _elapsed = new TextBlock { Text = FormatElapsed(TimeSpan.Zero), FontSize = 12 };
        meta.Children.Add(_counter); meta.Children.Add(_elapsed); top.Children.Add(meta);
        var list = new System.Windows.Controls.ListView { ItemsSource = _items, BorderThickness = new Thickness(0), Background = Brushes.White, Margin = new Thickness(1), HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch };
        _list = list;
        var view = new GridView();
        view.Columns.Add(new GridViewColumn
        {
            Header = norwegian ? "Kilde" : "Source",
            Width = 125,
            DisplayMemberBinding = new System.Windows.Data.Binding("Source")
        });
        view.Columns.Add(new GridViewColumn
        {
            Header = norwegian ? "Oppdatering" : "Update",
            Width = 410,
            DisplayMemberBinding = new System.Windows.Data.Binding("Name")
        });
        // Status ~30% wider than previous 140; non-success terminal states are clickable for help.
        var statusColumn = new GridViewColumn { Header = norwegian ? "Status" : "Status", Width = 182 };
        var statusFactory = new FrameworkElementFactory(typeof(TextBlock));
        statusFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(InstallationQueueItem.State)));
        statusFactory.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding(nameof(InstallationQueueItem.Tip)));
        statusFactory.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        statusFactory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        statusFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Stretch);
        statusFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        statusFactory.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(StatusCell_Click));
        statusColumn.CellTemplate = new DataTemplate { VisualTree = statusFactory };
        view.Columns.Add(statusColumn);
        list.View = view;
        list.MouseDoubleClick += List_MouseDoubleClick;
        var frame = new Border { CornerRadius = new CornerRadius(10), BorderBrush = new SolidColorBrush(Color.FromRgb(211, 221, 234)), BorderThickness = new Thickness(1), Background = Brushes.White, SnapsToDevicePixels = true, Child = list };
        root.Children.Add(frame);
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => PulseElapsed();
        Closing += (_, e) => { if (_running) e.Cancel = true; };
        Closed += (_, _) =>
        {
            _clock.Stop();
            StopAutoCloseCountdown();
        };
    }

    public static bool IsHelpfulStatus(string state)
    {
        if (string.IsNullOrWhiteSpace(state)) return false;
        if (IsCompletedState(state)) return false;
        if (IsStillRunning(state) || IsVerifyingState(state)) return false;
        if (state is "Pending" or "Downloaded" or "Lastet ned") return false;
        return state.StartsWith("Failed", StringComparison.OrdinalIgnoreCase)
            || state.StartsWith("Feilet", StringComparison.OrdinalIgnoreCase)
            || IsUnverifiedState(state)
            || state.Contains("mismatch", StringComparison.OrdinalIgnoreCase)
            || state.Contains("0x", StringComparison.OrdinalIgnoreCase);
    }

    private void StatusCell_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: InstallationQueueItem item } && item.IsClickableStatus)
        {
            e.Handled = true;
            ShowStatusHelp(item);
        }
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_list.SelectedItem is InstallationQueueItem item && item.IsClickableStatus)
            ShowStatusHelp(item);
    }

    private void ShowStatusHelp(InstallationQueueItem item)
    {
        bool norwegian = false;
        try { norwegian = string.Equals(ReminderSchedule.Load().Language, "no", StringComparison.OrdinalIgnoreCase); }
        catch { /* default EN */ }
        // Enrich empty tips from HRESULT in the status text when possible.
        if (string.IsNullOrWhiteSpace(item.Tip))
        {
            var match = System.Text.RegularExpressions.Regex.Match(item.State, @"0x[0-9A-Fa-f]{8}");
            if (match.Success)
            {
                var info = WindowsUpdateErrorCatalog.Lookup(match.Value);
                item.Tip = string.IsNullOrWhiteSpace(info.Summary)
                    ? match.Value
                    : $"{info.Code} — {info.Name}. {info.Summary} {info.Guidance}".Trim();
            }
        }
        new InstallStatusHelpWindow(item, norwegian) { Owner = this }.ShowDialog();
    }

    private void RefreshStatusCellVisuals()
    {
        bool norwegian = false;
        try { norwegian = string.Equals(ReminderSchedule.Load().Language, "no", StringComparison.OrdinalIgnoreCase); }
        catch { /* EN */ }
        foreach (var item in _items)
        {
            if (_list.ItemContainerGenerator.ContainerFromItem(item) is not System.Windows.Controls.ListViewItem container) continue;
            if (FindStatusTextBlock(container, item.State) is not TextBlock tb) continue;
            if (item.IsClickableStatus)
            {
                tb.Cursor = System.Windows.Input.Cursors.Hand;
                tb.Foreground = new SolidColorBrush(Color.FromRgb(198, 40, 40));
                tb.TextDecorations = TextDecorations.Underline;
                string clickHint = norwegian ? "Klikk for forslag." : "Click for suggestions.";
                tb.ToolTip = string.IsNullOrWhiteSpace(item.Tip) ? clickHint : item.Tip + "\n\n" + clickHint;
            }
            else
            {
                tb.Cursor = System.Windows.Input.Cursors.Arrow;
                tb.ClearValue(TextBlock.ForegroundProperty);
                tb.TextDecorations = null;
            }
        }
    }

    private static TextBlock? FindStatusTextBlock(DependencyObject root, string state)
    {
        if (root is TextBlock tb && tb.DataContext is InstallationQueueItem &&
            string.Equals(tb.Text, state, StringComparison.Ordinal))
            return tb;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var found = FindStatusTextBlock(VisualTreeHelper.GetChild(root, i), state);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>Keep the elapsed label current even when the dispatcher is busy between ticks.</summary>
    public void PulseElapsed()
    {
        if (!_running && !_clock.IsEnabled) return;
        _elapsed.Text = FormatElapsed(DateTime.Now - _started);
    }

    private string FormatElapsed(TimeSpan span) =>
        (_norwegian ? "Medgått: " : "Elapsed: ") + span.ToString(@"hh\:mm\:ss");

    private string FormatProcessed(double done, int total) =>
        _norwegian ? $"{done:0} av {total} behandlet" : $"{done:0} of {total} processed";

    public static string StatusCompleted(bool norwegian) => norwegian ? "Fullført" : "Completed";
    public static string StatusVerifying(bool norwegian) => norwegian ? "Verifiserer" : "Verifying";
    public static string StatusNotVerified(bool norwegian) => norwegian ? "Ikke verifisert" : "Not verified";
    public static string StatusVerifyTimedOut(bool norwegian) => norwegian ? "Verifisering tidsavbrutt" : "Verify timed out";
    public static string StatusInstalling(bool norwegian) => norwegian ? "Installerer…" : "Installing...";

    public static bool IsCompletedState(string state) =>
        state is "Completed" or "Fullført";

    public static bool IsVerifyingState(string state) =>
        state is "Verifying" or "Verifiserer";

    public static bool IsUnverifiedState(string state) =>
        state is "Not verified" or "Ikke verifisert" or "Check results"
            or "Verify timed out" or "Verifisering tidsavbrutt";
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

    /// <summary>
    /// Install phase reached a terminal outcome (Failed / Verifying / etc.).
    /// Unlock Close immediately so the user is not stuck during the post-install refresh.
    /// </summary>
    public void AllowClose(bool norwegian = false)
    {
        _running = false;
        _cancel.Content = norwegian ? "Lukk" : "Close";
        _cancel.IsEnabled = true;
    }
    public void BeginItem(int index, string stage)
    {
        if (!IsLoaded) return;
        if (index >= 0 && index < _items.Count) _items[index].State = StatusInstalling(_norwegian);
        SetStage(stage);
    }
    public void SetNotice(string text)
    {
        if (!IsLoaded) return;
        _notice.Text = text;
    }
    public void SetItemState(int index, string state)
    {
        if (!IsLoaded || index < 0 || index >= _items.Count) return;
        _items[index].State = state;
        _list.ScrollIntoView(_items[index]);
    }

    public void ApplyActivity(WuaActivity activity, bool norwegian)
    {
        if (!IsLoaded) return;
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
        PulseElapsed();
        string label = string.IsNullOrWhiteSpace(activity.Kb) ? "…" : activity.Kb;
        _stage.Text = WuaProgressText.Stage(norwegian, activity.Phase, label, number, total, activity.Percent);
        _counter.Text = activity.Phase switch
        {
            "download" => norwegian ? $"Laster ned {number} av {total}" : $"Downloading {number} of {total}",
            "install" => norwegian ? $"Installerer {number} av {total}" : $"Installing {number} of {total}",
            _ => norwegian ? $"{number} av {total}" : $"{number} of {total}"
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
        state is "Pending" or "Venter" or "Installing..." or "Installerer…" or "Verifying" or "Verifiserer"
            or "Downloaded" or "Lastet ned"
        || state.StartsWith("Downloading", StringComparison.Ordinal)
        || state.StartsWith("Installing", StringComparison.Ordinal)
        || state.StartsWith("Laster ned", StringComparison.Ordinal)
        || state.StartsWith("Installerer", StringComparison.Ordinal);

    public void CompleteItem(int index, string state, bool failed, string? tip = null)
    {
        if (!IsLoaded) return;
        if (index >= 0 && index < _items.Count)
        {
            _items[index].State = state;
            if (!string.IsNullOrWhiteSpace(tip))
                _items[index].Tip = tip;
            _list.ScrollIntoView(_items[index]);
            Dispatcher.BeginInvoke(RefreshStatusCellVisuals, DispatcherPriority.Loaded);
        }
        PulseElapsed();
        _bar.IsIndeterminate = false;
        _bar.Value = _items.Count(x => !IsStillRunning(x.State));
        _counter.Text = FormatProcessed(_bar.Value, _items.Count);
    }

    public void SetStage(string stage)
    {
        if (!IsLoaded) return;
        _stage.Text = stage;
        PulseElapsed();
    }

    public (int Completed, int Failed, int Unverified) Finish(bool norwegian = false, string? guidance = null, string unresolvedLabel = "Not verified")
    {
        _running = false;
        _clock.Stop();
        if (!IsLoaded)
        {
            int failedGone = _items.Count(x =>
                x.State.StartsWith("Failed", StringComparison.OrdinalIgnoreCase) ||
                x.State.StartsWith("Feilet", StringComparison.OrdinalIgnoreCase));
            int completedGone = _items.Count(x => IsCompletedState(x.State) || IsVerifyingState(x.State));
            int unverifiedGone = Math.Max(0, _items.Count - failedGone - completedGone);
            return (completedGone, failedGone, unverifiedGone);
        }
        _elapsed.Text = FormatElapsed(DateTime.Now - _started);
        // Verifying means the installer already reported success — promote to Completed.
        // Only unmatched / still-running rows become the unresolved label.
        for (int i = 0; i < _items.Count; i++)
        {
            if (!IsStillRunning(_items[i].State)) continue;
            _items[i].State = IsVerifyingState(_items[i].State)
                ? StatusCompleted(norwegian)
                : unresolvedLabel;
        }
        _bar.IsIndeterminate = false;
        _bar.Value = _items.Count;
        _counter.Text = FormatProcessed(_items.Count, _items.Count);
        int failed = _items.Count(x =>
            x.State.StartsWith("Failed", StringComparison.OrdinalIgnoreCase) ||
            x.State.StartsWith("Feilet", StringComparison.OrdinalIgnoreCase));
        int unverified = _items.Count(x => IsUnverifiedState(x.State));
        int completed = _items.Count(x => IsCompletedState(x.State));
        string summary = norwegian
            ? $"Ferdig: {completed} fullført, {failed} feilet, {unverified} ikke verifisert"
            : $"Finished: {completed} completed, {failed} failed, {unverified} unverified";
        if (!string.IsNullOrWhiteSpace(guidance) && unverified > 0)
            summary += " — " + guidance;
        _stage.Text = summary;
        // Show guidance for unverified rows and for soft notices (catalog lag after success).
        if (!string.IsNullOrWhiteSpace(guidance))
            _notice.Text = guidance;
        else if (failed > 0)
            _notice.Text = norwegian
                ? "Klikk en feilet/ikke-vellykket Status for forslag (Fix/katalog/Store)."
                : "Click a failed/non-success Status for suggestions (fix/catalog/Store).";
        _cancel.Content = norwegian ? "Lukk" : "Close";
        _cancel.IsEnabled = true;
        Dispatcher.BeginInvoke(RefreshStatusCellVisuals, DispatcherPriority.Loaded);
        ScheduleAutoClose(AutoCloseSeconds);
        return (completed, failed, unverified);
    }

    /// <summary>
    /// Close stays enabled; show remaining seconds (same pattern as <see cref="AutoCloseInfoDialog"/>)
    /// and dismiss when the countdown reaches 0 unless the user closes sooner.
    /// </summary>
    public void ScheduleAutoClose(int seconds = AutoCloseSeconds)
    {
        if (!IsLoaded) return;
        StopAutoCloseCountdown();

        int remaining = Math.Max(1, seconds);
        _autoCloseCountdown.Text = FormatAutoClose(remaining);
        _autoCloseCountdown.Visibility = Visibility.Visible;

        _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _autoCloseTimer.Tick += (_, _) =>
        {
            remaining--;
            if (remaining <= 0)
            {
                StopAutoCloseCountdown();
                if (IsLoaded) Close();
                return;
            }
            _autoCloseCountdown.Text = FormatAutoClose(remaining);
        };
        _autoCloseTimer.Start();
    }

    private void StopAutoCloseCountdown()
    {
        if (_autoCloseTimer is not null)
        {
            _autoCloseTimer.Stop();
            _autoCloseTimer = null;
        }
        _autoCloseCountdown.Visibility = Visibility.Collapsed;
        _autoCloseCountdown.Text = "";
    }

    private string FormatAutoClose(int remaining) =>
        _norwegian
            ? $"Lukker om {remaining} s…"
            : $"Closing in {remaining} s…";
}
