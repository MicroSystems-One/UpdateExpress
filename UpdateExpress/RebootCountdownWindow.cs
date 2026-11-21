using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UpdateExpress.Services;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfVerticalAlignment = System.Windows.VerticalAlignment;

namespace UpdateExpress;

/// <summary>
/// Centered modal while a Windows restart countdown is active.
/// Buttons (same height/baseline): Avbryt | Omstart nå | Utsett 30 minutter.
/// Any interactive user in the tray session can choose.
/// </summary>
internal sealed class RebootCountdownWindow : Window
{
    public enum Choice
    {
        LetCountdownFinish,
        Cancel,
        RestartNow,
        Postpone30
    }

    public Choice Decision { get; private set; } = Choice.LetCountdownFinish;

    private readonly DispatcherTimer _timer;
    private readonly TextBlock _countdownText;
    private readonly TextBlock _subText;
    private int _remaining;
    private bool _closing;

    public RebootCountdownWindow(int countdownSeconds, string source, string comment, bool norwegian)
    {
        _remaining = Math.Max(1, countdownSeconds);
        Title = norwegian ? "Update Express – Omstart" : "Update Express – Restart";
        AppIcon.Apply(this);
        Width = 520;
        MinWidth = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));
        FontFamily = new WpfFontFamily("Segoe UI");

        var heading = new TextBlock
        {
            Text = norwegian ? "Omstart om kort tid" : "Restart coming soon",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)),
            Margin = new Thickness(0, 0, 0, 8)
        };

        _countdownText = new TextBlock
        {
            Text = FormatCountdown(_remaining, norwegian),
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x67, 0xC0)),
            Margin = new Thickness(0, 0, 0, 10)
        };

        _subText = new TextBlock
        {
            Text = BuildSubtitle(source, comment, norwegian),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x52, 0x61, 0x7A)),
            LineHeight = 20,
            Margin = new Thickness(0, 0, 0, 16)
        };

        var cancel = MakeButton(norwegian ? "Avbryt" : "Cancel", primary: false);
        cancel.Click += (_, _) => Finish(Choice.Cancel);

        var now = MakeButton(norwegian ? "Omstart nå" : "Restart now", primary: true);
        now.Click += (_, _) => Finish(Choice.RestartNow);

        var postpone = MakeButton(norwegian ? "Utsett 30 minutter" : "Postpone 30 minutes", primary: false);
        postpone.Click += (_, _) => Finish(Choice.Postpone30);

        // Left → right: Avbryt | Omstart nå | Utsett — identical height/baseline.
        var row = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            VerticalAlignment = WpfVerticalAlignment.Center
        };
        row.Children.Add(cancel);
        row.Children.Add(now);
        row.Children.Add(postpone);

        Content = new Border
        {
            Margin = new Thickness(18),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xDF, 0xE5, 0xEC)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(22),
            Child = new StackPanel
            {
                Children = { heading, _countdownText, _subText, row }
            }
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _remaining--;
            if (_remaining <= 0)
            {
                _timer.Stop();
                if (!_closing)
                {
                    Decision = Choice.LetCountdownFinish;
                    _closing = true;
                    Close();
                }
                return;
            }
            _countdownText.Text = FormatCountdown(_remaining, norwegian);
        };
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void Finish(Choice choice)
    {
        if (_closing) return;
        _closing = true;
        Decision = choice;
        _timer.Stop();
        Close();
    }

    private static Button MakeButton(string content, bool primary)
    {
        var b = AppDialog.CreateChromeButton(content, primary, isDefault: primary);
        // Identical geometry for baseline alignment (ReminderDialog pattern).
        b.Height = 30;
        b.MinHeight = 30;
        b.MaxHeight = 30;
        b.MinWidth = 128;
        b.Padding = new Thickness(12, 0, 12, 0);
        b.Margin = new Thickness(0, 0, 8, 0);
        b.VerticalAlignment = VerticalAlignment.Center;
        b.FontSize = 12;
        return b;
    }

    private static string FormatCountdown(int seconds, bool norwegian)
    {
        int m = seconds / 60;
        int s = seconds % 60;
        return norwegian
            ? $"Omstart om {m}:{s:00}"
            : $"Restart in {m}:{s:00}";
    }

    private static string BuildSubtitle(string source, string comment, bool norwegian)
    {
        string src = string.IsNullOrWhiteSpace(source) ? "Update Express" : source;
        string tip = string.IsNullOrWhiteSpace(comment)
            ? (norwegian ? "Lagre arbeidet før omstart." : "Save your work before restart.")
            : comment;
        return norwegian
            ? $"Kilde: {src}\n{tip}\n\nAlle innloggede brukere kan velge Avbryt, Omstart nå eller Utsett."
            : $"Source: {src}\n{tip}\n\nAny signed-in user can Cancel, Restart now, or Postpone.";
    }

    /// <summary>
    /// Start OS countdown and show the modal. Applies Cancel / Restart now / Postpone.
    /// Returns false when the user cancels (reboot not armed); true otherwise.
    /// </summary>
    internal static bool RunWithUi(string source, int countdownSeconds, string comment, Window? owner = null)
    {
        bool norwegian = AppLanguage.IsNorwegian;
        int seconds = Math.Clamp(countdownSeconds, 60, 3600);
        RebootCoordinator.StartCountdownRebootCore(source, seconds, comment);

        var dlg = new RebootCountdownWindow(seconds, source, comment, norwegian);
        if (owner is not null)
        {
            try { dlg.Owner = owner; } catch { /* ignore */ }
        }

        dlg.ShowDialog();
        switch (dlg.Decision)
        {
            case Choice.Cancel:
                RebootCoordinator.CancelCountdownReboot("user cancelled from countdown UI");
                RebootCoordinator.ClearPending();
                return false;
            case Choice.RestartNow:
                RebootCoordinator.CancelCountdownReboot("user chose restart now");
                RebootCoordinator.StartCountdownRebootCore(source, 5,
                    norwegian ? "Update Express: omstart nå." : "Update Express: restart now.");
                return true;
            case Choice.Postpone30:
                RebootCoordinator.CancelCountdownReboot("user postponed 30 minutes");
                // UserAuthorized so Update-until-current / unattended postpones survive AutoReboot=off.
                RebootCoordinator.SchedulePending(
                    source,
                    DateTime.Now.AddMinutes(30),
                    norwegian ? "Utsett 30 minutter fra omstarts-dialog." : "Postponed 30 minutes from countdown dialog.",
                    userAuthorized: true);
                return true;
            default:
                // Countdown finished / window closed without choice — OS reboot proceeds.
                return true;
        }
    }
}
