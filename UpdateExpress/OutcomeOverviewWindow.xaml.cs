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
    public UpdateEntry? FixRequestedEntry { get; private set; }

    public OutcomeOverviewWindow(string kindKey, string heading, string subtitle, IReadOnlyList<UpdateEntry> entries, bool showFixIt, bool norwegian)
    {
        InitializeComponent();
        AppIcon.Apply(this);
        _norwegian = norwegian;
        _showFixIt = showFixIt;
        Title = heading;
        HeadingText.Text = heading;
        SubtitleText.Text = subtitle;
        EntriesGrid.ItemsSource = entries.ToList();
        FixColumn.Visibility = showFixIt ? Visibility.Visible : Visibility.Collapsed;
        FixSelectedButton.Visibility = showFixIt ? Visibility.Visible : Visibility.Collapsed;
        if (norwegian)
        {
            CloseButton.Content = "Lukk";
            FixSelectedButton.Content = "Fiks det";
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
        FixSelectedButton.IsEnabled = _showFixIt && EntriesGrid.SelectedItem is UpdateEntry entry && entry.HasFixableErrorCode;
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

    private void RequestFix(UpdateEntry entry)
    {
        FixRequestedEntry = entry;
        DialogResult = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
