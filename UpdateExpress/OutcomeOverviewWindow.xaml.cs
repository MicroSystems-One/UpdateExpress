using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UpdateExpress.Models;

namespace UpdateExpress;

public partial class OutcomeOverviewWindow : Window
{
    private readonly bool _norwegian;
    private readonly bool _showFixIt;
    private readonly bool _showUnresolvedActions;
    public UpdateEntry? FixRequestedEntry { get; private set; }
    public UpdateEntry? ClearUnresolvedEntry { get; private set; }
    public UpdateEntry? HideFromFutureEntry { get; private set; }

    public OutcomeOverviewWindow(
        string kindKey,
        string heading,
        string subtitle,
        IReadOnlyList<UpdateEntry> entries,
        bool showFixIt,
        bool norwegian,
        bool showUnresolvedActions = false)
    {
        InitializeComponent();
        AppIcon.Apply(this);
        _norwegian = norwegian;
        _showFixIt = showFixIt;
        _showUnresolvedActions = showUnresolvedActions;
        Title = heading;
        HeadingText.Text = heading;
        SubtitleText.Text = subtitle;
        EntriesGrid.ItemsSource = entries.ToList();
        FixColumn.Visibility = showFixIt ? Visibility.Visible : Visibility.Collapsed;
        FixSelectedButton.Visibility = showFixIt ? Visibility.Visible : Visibility.Collapsed;
        ClearUnresolvedButton.Visibility = showUnresolvedActions ? Visibility.Visible : Visibility.Collapsed;
        HideFromFutureButton.Visibility = showUnresolvedActions ? Visibility.Visible : Visibility.Collapsed;
        if (norwegian)
        {
            CloseButton.Content = "Lukk";
            FixSelectedButton.Content = "Fiks det";
            ClearUnresolvedButton.Content = "Nullstill Unresolved";
            HideFromFutureButton.Content = "Skjul fra fremtidige";
        }
        if (entries.Count == 0)
        {
            SubtitleText.Text = (subtitle + "\n\n" + (norwegian
                ? "Ingen oppføringer i denne kategorien akkurat nå."
                : "No entries in this category right now.")).Trim();
        }
    }

    private void EntriesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool selected = EntriesGrid.SelectedItem is UpdateEntry;
        FixSelectedButton.IsEnabled = _showFixIt && EntriesGrid.SelectedItem is UpdateEntry entry && entry.HasFixableErrorCode;
        ClearUnresolvedButton.IsEnabled = _showUnresolvedActions && selected;
        HideFromFutureButton.IsEnabled = _showUnresolvedActions && selected;
    }

    private void EntriesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (EntriesGrid.SelectedItem is UpdateEntry entry && entry.HasFixableErrorCode)
            RequestFix(entry);
    }

    private void FixIt_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: UpdateEntry entry })
            RequestFix(entry);
    }

    private void FixSelected_Click(object sender, RoutedEventArgs e)
    {
        if (EntriesGrid.SelectedItem is UpdateEntry entry && entry.HasFixableErrorCode)
            RequestFix(entry);
    }

    private void ClearUnresolved_Click(object sender, RoutedEventArgs e)
    {
        if (EntriesGrid.SelectedItem is not UpdateEntry entry) return;
        ClearUnresolvedEntry = entry;
        DialogResult = true;
        Close();
    }

    private void HideFromFuture_Click(object sender, RoutedEventArgs e)
    {
        if (EntriesGrid.SelectedItem is not UpdateEntry entry) return;
        HideFromFutureEntry = entry;
        DialogResult = true;
        Close();
    }

    private void RequestFix(UpdateEntry entry)
    {
        FixRequestedEntry = entry;
        DialogResult = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
