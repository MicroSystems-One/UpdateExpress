using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Navigation;
using UpdateExpress.Services;
using UpdateExpress.Services.Security;
using Brush = System.Windows.Media.Brush;
using ColorConverter = System.Windows.Media.ColorConverter;
using RadioButton = System.Windows.Controls.RadioButton;
using Strings = UpdateExpress.Services.Security.SecurityOverviewStrings;
using Ui = UpdateExpress.Services.Security.SecurityOverviewStrings.Ui;

namespace UpdateExpress;

/// <summary>
/// The read-only Security Overview: the last finished assessment, then a fresh one that
/// starts when the window opens. Nothing here changes a setting on the PC.
/// </summary>
public partial class SecurityOverviewWindow : Window
{
    private readonly bool _norwegian;
    private readonly Func<DateTime, string> _formatTime;
    private readonly SecurityBaseline _baseline = SecurityBaselineV1.Create();
    private readonly List<SecurityCheckRowView> _rows = new();
    private readonly Dictionary<string, SecurityCheckRowView> _rowsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<SecuritySummaryBucket, (Border Card, TextBlock Number)> _cards = new();
    private readonly ICollectionView _view;
    private readonly Button _scanButton;
    private SecurityCheckCategory? _category;
    private SecurityAssessmentSnapshot? _lastCompleted;
    private CancellationTokenSource? _scan;
    private int _answered;
    private bool _cancelledNote;
    private bool _closed;

    /// <param name="lastCompleted">The last finished assessment the caller knows of, shown until a fresh one answers.</param>
    /// <param name="formatTime">How the main window writes a date and time.</param>
    internal SecurityOverviewWindow(bool norwegian, SecurityAssessmentSnapshot? lastCompleted, Func<DateTime, string>? formatTime = null)
    {
        _norwegian = norwegian;
        _formatTime = formatTime ?? (time => time.ToString("g", CultureInfo.CurrentCulture));
        _lastCompleted = SecurityAssessmentRunner.ReplacesEarlier(lastCompleted) ? lastCompleted : null;
        InitializeComponent();
        AppIcon.Apply(this);
        // On a small or strongly scaled screen the window must still fit the work area.
        var area = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(Width, area.Width - 24));
        Height = Math.Max(MinHeight, Math.Min(Height, area.Height - 24));

        Title = T(Strings.Title);
        TitleText.Text = Title;
        HonestyText.Text = T(Strings.HonestyLine);
        BaselineText.Text = Strings.Tf(Ui.BaselineNote, _norwegian, _baseline.Id);
        EmptyCategoryText.Text = T(Ui.EmptyCategory);
        AutomationProperties.SetName(CardsHost, T(Ui.SummaryCards));
        AutomationProperties.SetName(ChipsHost, T(Ui.CategoryFilter));
        AutomationProperties.SetName(ChecksHost, T(Ui.CheckList));

        var labels = new SecurityOverviewLabels(_norwegian);
        foreach (var definition in _baseline.Definitions)
        {
            var row = new SecurityCheckRowView(definition, labels, _norwegian, _formatTime);
            _rows.Add(row);
            _rowsById[definition.Id] = row;
        }
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = item => _category is null || ((SecurityCheckRowView)item).Definition.Category == _category;
        ChecksHost.ItemsSource = _view;

        BuildCards();
        BuildChips();

        _scanButton = AppDialog.CreateChromeButton(T(Ui.ScanAgain), primary: true);
        _scanButton.Margin = new Thickness(0);
        _scanButton.Click += (_, _) =>
        {
            if (_scan is null) _ = StartScanAsync();
            else CancelScan();
        };
        var close = AppDialog.CreateChromeButton(T(Ui.Close), primary: false, isCancel: true);
        close.Click += (_, _) => Close();
        ButtonsHost.Children.Add(_scanButton);
        ButtonsHost.Children.Add(close);

        ShowSnapshot(_lastCompleted);
    }

    /// <summary>The last assessment that finished, from the caller or from this window.</summary>
    internal SecurityAssessmentSnapshot? LastCompleted => _lastCompleted;

    /// <summary>Raised on the window's thread each time an assessment finishes.</summary>
    internal event Action<SecurityAssessmentSnapshot>? AssessmentCompleted;

    /// <summary>False keeps the window from assessing by itself when it opens.</summary>
    internal bool ScanOnOpen { get; init; } = true;

    /// <summary>The assessment that is running, or the last one that ran.</summary>
    internal Task CurrentScan { get; private set; } = Task.CompletedTask;

    internal bool IsScanning => _scan is not null;

    internal IReadOnlyList<SecurityCheckRowView> Rows => _rows;

    private string T(string english) => Strings.T(english, _norwegian);

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (ScanOnOpen) _ = StartScanAsync();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        CancelScan();
    }

    internal Task StartScanAsync()
    {
        if (_scan is not null || _closed) return CurrentScan;
        CurrentScan = ScanAsync();
        return CurrentScan;
    }

    internal void CancelScan()
    {
        try { _scan?.Cancel(); }
        catch (ObjectDisposedException) { /* the assessment has just ended */ }
    }

    private async Task ScanAsync()
    {
        var cancel = new CancellationTokenSource();
        _scan = cancel;
        _answered = 0;
        _cancelledNote = false;
        foreach (var row in _rows) row.Show(row.Result, checking: true);
        _scanButton.Content = T(Ui.Cancel);
        RefreshSummary();

        // Created on the window's thread, so each answer comes back to it.
        var progress = new Progress<SecurityCheckResult>(result =>
        {
            if (!ReferenceEquals(_scan, cancel) || _closed) return;
            if (!_rowsById.TryGetValue(result.Id, out var row)) return;
            if (row.Checking) _answered++;
            row.Show(result, checking: false);
            RefreshSummary();
        });

        SecurityAssessmentSnapshot? snapshot = null;
        try
        {
            snapshot = await SecurityAssessmentRunner.RunAsync(_baseline, progress, cancel.Token);
        }
        catch (Exception ex)
        {
            // The engine turns every failure into a result; this is the unexpected rest.
            try { SecurityAssessmentRunner.Log("Security assessment failed to run: " + ex.GetType().Name); }
            catch { /* logging must not add a second failure */ }
        }
        finally
        {
            _scan = null;
            cancel.Dispose();
        }
        if (_closed) return;

        _scanButton.Content = T(Ui.ScanAgain);
        if (SecurityAssessmentRunner.ReplacesEarlier(snapshot))
        {
            _lastCompleted = snapshot;
            ShowSnapshot(snapshot);
            AssessmentCompleted?.Invoke(snapshot!);
            return;
        }

        // Cancelled, without any answer, or it could not run: back to the last whole picture,
        // the one that is stored. Without one, what did answer stays and the rest reads unknown.
        _cancelledNote = snapshot is not { Completed: true };
        ShowSnapshot(_lastCompleted ?? snapshot);
    }

    private void ShowSnapshot(SecurityAssessmentSnapshot? snapshot)
    {
        var results = new Dictionary<string, SecurityCheckResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in snapshot?.Results ?? Array.Empty<SecurityCheckResult>())
            results[result.Id] = result;
        foreach (var row in _rows)
            row.Show(results.TryGetValue(row.Definition.Id, out var result) ? result : null, checking: false);
        RefreshSummary();
    }

    /// <summary>The shield, the line under the title, the progress bar and the cards.</summary>
    private void RefreshSummary()
    {
        var overall = SecurityOverallStatus.Evaluate(_lastCompleted, DateTimeOffset.Now);
        var color = BrushOf(SecurityOverviewPresentation.LevelColor(overall.Level));
        OverallShieldOutline.Stroke = color;
        OverallShieldLines.Stroke = color;

        bool scanning = _scan is not null;
        string line;
        if (scanning)
        {
            line = Strings.Tf(Ui.Progress, _norwegian, _answered, _rows.Count);
            if (overall.AssessedAt is DateTimeOffset at)
                line += " " + Strings.Tf(Ui.ShownUntilAnswered, _norwegian, _formatTime(at.LocalDateTime));
        }
        else
        {
            line = SecurityOverviewPresentation.OverallLine(overall, _norwegian, _formatTime);
            if (_cancelledNote)
            {
                string note = T(Ui.Cancelled);
                if (overall.AssessedAt is DateTimeOffset at)
                    note += " " + Strings.Tf(Ui.ShowingAssessmentFrom, _norwegian, _formatTime(at.LocalDateTime));
                line = note + " " + line;
            }
        }
        OverallText.Text = line;
        ScanProgress.Visibility = scanning ? Visibility.Visible : Visibility.Hidden;
        ScanProgress.Maximum = Math.Max(1, _rows.Count);
        ScanProgress.Value = _answered;

        // The cards count what the list shows. A check without an answer is unknown, not a pass.
        var shown = _rows.Where(row => row.Result is not null).Select(row => row.Result!).ToList();
        var summary = SecurityOverallStatus.Summarize(shown);
        int unanswered = _rows.Count(row => row.Result is null && !row.Checking);
        SetCard(SecuritySummaryBucket.Protected, summary.Protected);
        SetCard(SecuritySummaryBucket.NeedsAttention, summary.NeedsAttention);
        SetCard(SecuritySummaryBucket.ManualReview, summary.ManualReview);
        SetCard(SecuritySummaryBucket.UnknownOrUnsupported, summary.UnknownOrUnsupported + unanswered);
        SetCard(SecuritySummaryBucket.RestartRequired, summary.RestartRequired);
        // "Restart required, when applicable": the card is there only when a restart is.
        _cards[SecuritySummaryBucket.RestartRequired].Card.Visibility =
            summary.RestartRequired > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildCards()
    {
        foreach (var bucket in new[]
                 {
                     SecuritySummaryBucket.Protected, SecuritySummaryBucket.NeedsAttention, SecuritySummaryBucket.ManualReview,
                     SecuritySummaryBucket.UnknownOrUnsupported, SecuritySummaryBucket.RestartRequired
                 })
        {
            var number = new TextBlock { FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = BrushOf("#1C1C1C"), Text = "0" };
            var label = new TextBlock
            {
                Text = Strings.Bucket(bucket, _norwegian),
                Foreground = BrushOf("#5D5D5D"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0)
            };
            var text = new StackPanel { Margin = new Thickness(12, 8, 10, 9) };
            text.Children.Add(number);
            text.Children.Add(label);
            var strip = new Border
            {
                Width = 4,
                Background = BrushOf(SecurityOverviewPresentation.BucketColor(bucket)),
                CornerRadius = new CornerRadius(6, 0, 0, 6)
            };
            var content = new DockPanel();
            DockPanel.SetDock(strip, Dock.Left);
            content.Children.Add(strip);
            content.Children.Add(text);
            var card = new Border
            {
                Background = Brushes.White,
                BorderBrush = BrushOf("#E5E5E5"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 8, 0),
                Child = content
            };
            _cards[bucket] = (card, number);
            CardsHost.Children.Add(card);
        }
    }

    private void SetCard(SecuritySummaryBucket bucket, int count)
    {
        var (card, number) = _cards[bucket];
        number.Text = count.ToString(CultureInfo.InvariantCulture);
        AutomationProperties.SetName(card, Strings.Bucket(bucket, _norwegian) + ": " + number.Text);
    }

    private void BuildChips()
    {
        var style = (Style)FindResource("CategoryChip");
        void Add(SecurityCheckCategory? category, string label, int count)
        {
            var chip = new RadioButton
            {
                Style = style,
                GroupName = "SecurityOverviewCategory",
                Content = label + " (" + count.ToString(CultureInfo.InvariantCulture) + ")",
                IsChecked = category == _category
            };
            chip.Checked += (_, _) =>
            {
                _category = category;
                _view.Refresh();
                EmptyCategoryText.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
                ChecksScroll.ScrollToTop();
            };
            ChipsHost.Children.Add(chip);
        }

        Add(null, T("All"), _rows.Count);
        foreach (var category in Enum.GetValues<SecurityCheckCategory>())
            Add(category, Strings.Category(category, _norwegian), _rows.Count(row => row.Definition.Category == category));
    }

    private void Reference_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        // The baseline's links are https pages; nothing else is handed to the shell.
        if (e.Uri is null || !SecurityOverviewPresentation.IsWebLink(e.Uri.OriginalString)) return;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal static SolidColorBrush BrushOf(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>The fixed labels of a row, in the window's language.</summary>
internal sealed class SecurityOverviewLabels
{
    internal SecurityOverviewLabels(bool norwegian)
    {
        string T(string english) => Strings.T(english, norwegian);
        Details = T(Ui.Details);
        MoreInformation = T(Ui.MoreInformation);
        CurrentState = T(Ui.CurrentState);
        Recommended = T(Ui.Recommended);
        Policy = T(Ui.Policy);
        Checked = T(Ui.Checked);
        Remediation = T(Ui.Remediation);
        Restart = T(Ui.Restart);
        Finding = T(Ui.Finding);
        TechnicalDetail = T(Ui.TechnicalDetail);
        WhyItMatters = T(Ui.WhyItMatters);
        PolicyNote = T(Ui.PolicyNote);
        CheckId = T(Ui.CheckId);
        Severity = T(Ui.Severity);
    }

    public string Details { get; }
    public string MoreInformation { get; }
    public string CurrentState { get; }
    public string Recommended { get; }
    public string Policy { get; }
    public string Checked { get; }
    public string Remediation { get; }
    public string Restart { get; }
    public string Finding { get; }
    public string TechnicalDetail { get; }
    public string WhyItMatters { get; }
    public string PolicyNote { get; }
    public string CheckId { get; }
    public string Severity { get; }
}

internal sealed record SecurityLabelValue(string Label, string Value);

/// <summary>One check in the list: its texts and colours for the answer it shows now.</summary>
internal sealed class SecurityCheckRowView : INotifyPropertyChanged
{
    private readonly bool _norwegian;
    private readonly Func<DateTime, string> _formatTime;
    private bool _expanded;

    internal SecurityCheckRowView(SecurityCheckDefinition definition, SecurityOverviewLabels labels, bool norwegian, Func<DateTime, string> formatTime)
    {
        Definition = definition;
        Labels = labels;
        _norwegian = norwegian;
        _formatTime = formatTime;
        Text = SecurityOverviewPresentation.Row(definition, null, false, norwegian, formatTime);
        DetailsName = Strings.Tf(Ui.DetailsFor, norwegian, Text.Name);
        if (Text.ReferenceUrl.Length > 0 && Uri.TryCreate(Text.ReferenceUrl, UriKind.Absolute, out var uri))
        {
            ReferenceUri = uri;
            ReferenceHost = " (" + uri.Host + ")";
        }
        Apply();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SecurityCheckDefinition Definition { get; }
    public SecurityOverviewLabels Labels { get; }
    public SecurityCheckRowText Text { get; private set; }
    public SecurityCheckResult? Result { get; private set; }
    public bool Checking { get; private set; }
    public Brush StatusBrush { get; private set; } = Brushes.Gray;
    public Brush StatusTint { get; private set; } = Brushes.Transparent;
    public IReadOnlyList<SecurityLabelValue> Facts { get; private set; } = Array.Empty<SecurityLabelValue>();
    public IReadOnlyList<SecurityLabelValue> Details { get; private set; } = Array.Empty<SecurityLabelValue>();
    public string DetailsName { get; }
    public Uri? ReferenceUri { get; }
    public string ReferenceHost { get; } = "";
    public Visibility LinkVisibility => ReferenceUri is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DetailsVisibility => _expanded ? Visibility.Visible : Visibility.Collapsed;

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailsVisibility)));
        }
    }

    /// <param name="result">The answer to show; null for none.</param>
    /// <param name="checking">True while a running assessment has not answered this check.</param>
    internal void Show(SecurityCheckResult? result, bool checking)
    {
        Result = result;
        Checking = checking;
        Text = SecurityOverviewPresentation.Row(Definition, result, checking, _norwegian, _formatTime);
        Apply();
        // Every bound property of the row follows from the answer.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private void Apply()
    {
        string color = SecurityOverviewPresentation.StatusColor(Text.Status);
        StatusBrush = SecurityOverviewWindow.BrushOf(color);
        StatusTint = SecurityOverviewWindow.BrushOf("#22" + color[1..]);
        Facts = new[]
        {
            new SecurityLabelValue(Labels.CurrentState, Text.Detected),
            new SecurityLabelValue(Labels.Recommended, Text.Recommended),
            new SecurityLabelValue(Labels.Policy, Text.Policy),
            new SecurityLabelValue(Labels.Checked, Text.Checked),
            new SecurityLabelValue(Labels.Remediation, Text.Remediation),
            new SecurityLabelValue(Labels.Restart, Text.Restart)
        };
        var details = new List<SecurityLabelValue>();
        void Add(string label, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) details.Add(new SecurityLabelValue(label, value));
        }
        Add(Labels.Finding, Text.Finding);
        Add(Labels.TechnicalDetail, Text.TechnicalDetail);
        Add(Labels.WhyItMatters, Text.Rationale);
        Add(Labels.PolicyNote, Text.PolicyNote);
        Add(Labels.CheckId, Text.Id);
        Add(Labels.Severity, Text.Severity);
        Details = details;
    }
}
