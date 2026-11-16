using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using UpdateExpress.Services;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using RadioButton = System.Windows.Controls.RadioButton;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;

namespace UpdateExpress;

/// <summary>Display preferences opened from the tray. Reminder schedule and automation stay in the main window.</summary>
public sealed class SettingsWindow : Window
{
    private readonly ReminderSchedule _schedule = ReminderSchedule.Load();
    private readonly RadioButton _english = new() { Content = "English", Margin = new Thickness(0, 0, 16, 0) };
    private readonly RadioButton _norwegian = new() { Content = "Norsk", Margin = new Thickness(0, 0, 16, 0) };
    private readonly RadioButton _clock12 = new() { Content = "12h", Margin = new Thickness(0, 0, 16, 0) };
    private readonly RadioButton _clock24 = new() { Content = "24h", Margin = new Thickness(0, 0, 16, 0) };
    private readonly ComboBox _dateFormat = new() { Width = 280, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _preview = new() { Margin = new Thickness(0, 14, 0, 0), Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x52, 0x61, 0x7A)), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _languageCaption = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock _clockCaption = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) };
    private readonly TextBlock _dateCaption = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) };
    private readonly TextBlock _diagCaption = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6) };
    private readonly CheckBox _verboseInstall = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock _diagPath = new()
    {
        Margin = new Thickness(0, 0, 0, 8),
        FontSize = 11,
        Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x52, 0x61, 0x7A)),
        TextWrapping = TextWrapping.Wrap
    };
    private readonly Button _openLogs = new() { Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4) };
    private readonly Button _openLastLog = new() { Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4) };
    private readonly Button _exportLog = new() { Padding = new Thickness(10, 4, 10, 4) };
    private readonly TextBlock _note = new() { Margin = new Thickness(0, 16, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x52, 0x61, 0x7A)) };
    private readonly Button _close = new() { HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
    private bool _applying;

    public SettingsWindow()
    {
        Width = 520;
        Height = 580;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF3, 0xF6, 0xFB));
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        _english.GroupName = "SettingsLanguage";
        _norwegian.GroupName = "SettingsLanguage";
        _clock12.GroupName = "SettingsClock";
        _clock24.GroupName = "SettingsClock";
        _english.IsChecked = _schedule.Language != "no";
        _norwegian.IsChecked = _schedule.Language == "no";
        _clock24.IsChecked = _schedule.Use24HourClock;
        _clock12.IsChecked = !_schedule.Use24HourClock;
        _verboseInstall.IsChecked = _schedule.VerboseInstallLogging;
        _dateFormat.Items.Add(new ComboBoxItem { Content = "", Tag = "text" });
        _dateFormat.Items.Add(new ComboBoxItem { Content = "", Tag = "dmy" });
        _dateFormat.Items.Add(new ComboBoxItem { Content = "", Tag = "iso" });
        _dateFormat.Items.Add(new ComboBoxItem { Content = "", Tag = "mdy" });
        string current = string.IsNullOrWhiteSpace(_schedule.DateFormat) ? "text" : _schedule.DateFormat;
        foreach (ComboBoxItem item in _dateFormat.Items)
            if (string.Equals(item.Tag as string, current, StringComparison.OrdinalIgnoreCase))
                _dateFormat.SelectedItem = item;
        if (_dateFormat.SelectedIndex < 0) _dateFormat.SelectedIndex = 0;
        _english.Checked += (_, _) => Apply(save: true);
        _norwegian.Checked += (_, _) => Apply(save: true);
        _clock12.Checked += (_, _) => Apply(save: true);
        _clock24.Checked += (_, _) => Apply(save: true);
        _dateFormat.SelectionChanged += (_, _) => Apply(save: true);
        _verboseInstall.Checked += (_, _) => Apply(save: true);
        _verboseInstall.Unchecked += (_, _) => Apply(save: true);
        _openLogs.Click += (_, _) =>
        {
            try { AgentLog.OpenLogsFolder(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Update Express", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        _openLastLog.Click += (_, _) =>
        {
            try { AgentLog.OpenLastInstallLog(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Update Express", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        _exportLog.Click += (_, _) => ExportLastLog();
        _close.Click += (_, _) => Close();
        Content = new Border
        {
            Margin = new Thickness(16),
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDF, 0xE5, 0xEC)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(22),
            Child = new StackPanel
            {
                Children =
                {
                    _languageCaption,
                    new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Children = { _english, _norwegian } },
                    _clockCaption,
                    new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Children = { _clock12, _clock24 } },
                    _dateCaption,
                    _dateFormat,
                    _preview,
                    _diagCaption,
                    _verboseInstall,
                    _diagPath,
                    new StackPanel
                    {
                        Orientation = System.Windows.Controls.Orientation.Horizontal,
                        Children = { _openLogs, _openLastLog, _exportLog }
                    },
                    _note,
                    _close
                }
            }
        };
        Apply(save: false);
    }

    private void ExportLastLog()
    {
        try
        {
            var dlg = new SaveFileDialog
            {
                Title = _norwegian.IsChecked == true ? "Eksporter installasjonslogg" : "Export install log",
                Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                FileName = "UpdateExpress-last-install.log",
                OverwritePrompt = true
            };
            if (dlg.ShowDialog(this) != true) return;
            string? path = AgentLog.ExportLastInstallLog(dlg.FileName);
            if (path is null)
            {
                MessageBox.Show(this,
                    _norwegian.IsChecked == true
                        ? "Ingen installasjonslogg ennå. Kjør Installer / Oppdater først."
                        : "No install log yet. Run Install / Update once first.",
                    "Update Express", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            MessageBox.Show(this,
                (_norwegian.IsChecked == true ? "Logg eksportert til:\n" : "Log exported to:\n") + path,
                "Update Express", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Update Express", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Apply(bool save)
    {
        if (_applying) return;
        _applying = true;
        try
        {
        bool norwegian = _norwegian.IsChecked == true;
        _schedule.Language = norwegian ? "no" : "en";
        _schedule.Use24HourClock = _clock24.IsChecked == true;
        _schedule.DateFormat = (_dateFormat.SelectedItem as ComboBoxItem)?.Tag as string ?? "text";
        _schedule.VerboseInstallLogging = _verboseInstall.IsChecked == true;
        Title = norwegian ? "Update Express – Innstillinger" : "Update Express – Settings";
        AppIcon.Apply(this);
        _languageCaption.Text = norwegian ? "Språk" : "Language";
        _clockCaption.Text = norwegian ? "Klokke" : "Clock";
        _dateCaption.Text = norwegian ? "Datoformat" : "Date format";
        _diagCaption.Text = norwegian ? "Installasjons-/oppdateringslogg" : "Install / update diagnostics";
        _verboseInstall.Content = norwegian
            ? "Utdypende logging (også verbose.log)"
            : "Verbose logging (also verbose.log)";
        _diagPath.Text = norwegian
            ? "Installasjonslogger skrives alltid til:\n" + AgentLog.LogsDir
              + "\n(install-update.log / last-install.log). Last opp last-install.log hvis ikoner/EXE forsvinner etter oppdatering."
            : "Install logs always go to:\n" + AgentLog.LogsDir
              + "\n(install-update.log / last-install.log). Upload last-install.log if icons/EXE disappear after update.";
        _openLogs.Content = norwegian ? "Åpne loggmappe" : "Open log folder";
        _openLastLog.Content = norwegian ? "Åpne siste logg" : "Open last log";
        _exportLog.Content = norwegian ? "Eksporter logg…" : "Export log…";
        _close.Content = norwegian ? "Lukk" : "Close";
        _note.Text = norwegian
            ? "Påminnelser, Windows-automatisering og proxy endres andre steder. Proxy ligger i tray-menyen. Påminnelser og automatisering ligger i hovedvinduet."
            : "Reminders, Windows automation, and proxy stay elsewhere. Proxy is in the tray menu. Reminders and automation are in the main window.";
        var sample = DateTime.Now;
        var savedLanguage = _schedule.Language;
        var savedClock = _schedule.Use24HourClock;
        foreach (ComboBoxItem item in _dateFormat.Items)
        {
            _schedule.DateFormat = item.Tag as string ?? "text";
            item.Content = _schedule.Format(sample);
        }
        _schedule.Language = savedLanguage;
        _schedule.Use24HourClock = savedClock;
        _schedule.DateFormat = (_dateFormat.SelectedItem as ComboBoxItem)?.Tag as string ?? "text";
        _preview.Text = (norwegian ? "Eksempel: " : "Example: ") + _schedule.Format(sample);
        if (save) _schedule.Save();
        }
        finally { _applying = false; }
    }
}
