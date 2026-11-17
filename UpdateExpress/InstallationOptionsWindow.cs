using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UpdateExpress.Services;

namespace UpdateExpress;

public sealed class InstallationOptionsWindow : Window
{
    private readonly bool _norwegian;
    private readonly System.Windows.Controls.RadioButton _current = new() { Margin = new Thickness(0, 6, 0, 4) };
    private readonly System.Windows.Controls.RadioButton _all = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly System.Windows.Controls.RadioButton _profilePrivate = new() { GroupName = "InstallProfile", Margin = new Thickness(0, 4, 0, 2) };
    private readonly System.Windows.Controls.RadioButton _profileBusiness = new() { GroupName = "InstallProfile", Margin = new Thickness(0, 4, 0, 2) };
    private readonly System.Windows.Controls.RadioButton _profileBoth = new() { GroupName = "InstallProfile", Margin = new Thickness(0, 4, 0, 8) };
    private readonly System.Windows.Controls.TextBox _activeStart = new() { Width = 48, Height = 28, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 0, 12, 0) };
    private readonly System.Windows.Controls.TextBox _activeEnd = new() { Width = 48, Height = 28, Padding = new Thickness(6, 3, 6, 3) };
    private readonly System.Windows.Controls.CheckBox[] _dayBoxes = new System.Windows.Controls.CheckBox[7];
    private readonly System.Windows.Controls.TextBox _installTime = new() { Width = 72, Height = 28, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(8, 0, 0, 0) };
    private readonly System.Windows.Controls.RadioButton _rebootConfirm = new() { GroupName = "InstallReboot", Margin = new Thickness(0, 4, 0, 2) };
    private readonly System.Windows.Controls.RadioButton _rebootOutside = new() { GroupName = "InstallReboot", Margin = new Thickness(0, 4, 0, 2) };
    private readonly System.Windows.Controls.RadioButton _rebootOff = new() { GroupName = "InstallReboot", Margin = new Thickness(0, 4, 0, 8) };
    private readonly System.Windows.Controls.CheckBox _desktop = new() { IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
    private readonly System.Windows.Controls.CheckBox _menu = new() { IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
    private readonly System.Windows.Controls.CheckBox _taskbar = new() { IsChecked = false, Margin = new Thickness(0, 4, 0, 4) };
    private readonly System.Windows.Controls.CheckBox _agent = new() { IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
    private readonly System.Windows.Controls.CheckBox _service = new() { IsChecked = false, Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBlock _serviceHint = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Foreground = Brushes.DimGray,
        Margin = new Thickness(22, 0, 0, 8)
    };

    private string _profile = "private";
    private string _rebootChoice = "confirm";
    private int _daysMask = MaintenanceSetup.AllDaysMask;

    public bool AllUsers => _all.IsChecked == true;
    public bool DesktopShortcut => _desktop.IsChecked == true;
    public bool StartMenuShortcut => _menu.IsChecked == true;
    public bool TaskbarPin => _taskbar.IsChecked == true;
    public bool BackgroundAgent => _agent.IsChecked == true;
    public bool InstallWindowsService => _service.IsChecked == true;
    public string SetupProfile => _profile;
    public int ActiveHoursStart { get; private set; } = 8;
    public int ActiveHoursEnd { get; private set; } = 16;
    public int InstallDaysMask => _daysMask;
    public string InstallTime { get; private set; } = "18:00";
    public string RebootChoice => _rebootChoice;

    public InstallationOptionsWindow(bool repair)
    {
        _norwegian = MaintenanceSetup.IsNorwegian();
        Title = repair
            ? (_norwegian ? "Update Express – Reparasjon" : "Update Express – Repair options")
            : (_norwegian ? "Update Express – Installasjonsvalg" : "Update Express – Installation options");
        AppIcon.Apply(this);
        Width = 560;
        Height = 720;
        MinWidth = 520;
        MinHeight = 560;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White;

        ApplyLocalizedLabels(repair);

        var root = new Grid { Margin = new Thickness(24, 20, 24, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(27) });
        Content = root;

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(scroll, 0);
        root.Children.Add(scroll);

        var content = new StackPanel();
        scroll.Content = content;
        content.Children.Add(new TextBlock
        {
            Text = repair
                ? (_norwegian ? "Reparer Update Express" : "Repair Update Express")
                : (_norwegian ? "Installer Update Express" : "Install Update Express"),
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold
        });

        content.Children.Add(Section("Installation scope", "Installasjonsområde"));
        _all.Checked += (_, _) => RefreshScopeUi();
        _current.Checked += (_, _) => RefreshScopeUi();
        content.Children.Add(_current);
        content.Children.Add(_all);

        content.Children.Add(Section("Profile", "Profil"));
        content.Children.Add(ProfileTip(_profilePrivate, "private",
            _norwegian ? "Privat" : "Private",
            _norwegian ? "Hjemmebruk – mykere kveldsvindu, spør før omstart." : "Home use — softer evening window, ask before restart."));
        content.Children.Add(ProfileTip(_profileBusiness, "business",
            _norwegian ? "Bedrift" : "Business",
            _norwegian ? "Arbeids-PC – man–fre arbeidstid, omstart utenom arbeidstid." : "Work PC — weekday hours, restart outside active hours."));
        content.Children.Add(ProfileTip(_profileBoth, "both",
            _norwegian ? "Begge deler" : "Both",
            _norwegian ? "Blanding – arbeidstid man–fre, spør før omstart (tryggere)." : "Mixed — weekday hours, ask before restart (safer)."));

        content.Children.Add(Section("Active hours", "Arbeidstid"));
        content.Children.Add(new TextBlock
        {
            Text = _norwegian
                ? "I denne perioden unngår vi omstart når det er mulig."
                : "We avoid restarts in this period when possible.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 6)
        });
        var hours = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        hours.Children.Add(new TextBlock
        {
            Text = _norwegian ? "Fra" : "From",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        hours.Children.Add(_activeStart);
        hours.Children.Add(new TextBlock
        {
            Text = _norwegian ? "til" : "to",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        hours.Children.Add(_activeEnd);
        hours.Children.Add(new TextBlock
        {
            Text = _norwegian ? " (time 0–23)" : " (hour 0–23)",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.Gray,
            Margin = new Thickness(8, 0, 0, 0)
        });
        content.Children.Add(hours);

        content.Children.Add(Section("Install window", "Installasjonsvindu"));
        content.Children.Add(new TextBlock
        {
            Text = _norwegian
                ? "Velg dager og ett klokkeslett. Defender kjører uansett kontinuerlig."
                : "Pick days and one clock time. Defender still runs continuously.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 6)
        });
        var days = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var labels = _norwegian ? MaintenanceSetup.DayLabelsNo : MaintenanceSetup.DayLabelsEn;
        for (int i = 0; i < 7; i++)
        {
            var box = new System.Windows.Controls.CheckBox
            {
                Content = labels[i],
                Margin = new Thickness(0, 0, 12, 6),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _dayBoxes[i] = box;
            days.Children.Add(box);
        }
        content.Children.Add(days);
        var timeRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        timeRow.Children.Add(new TextBlock
        {
            Text = _norwegian ? "Klokkeslett" : "Time",
            VerticalAlignment = VerticalAlignment.Center
        });
        timeRow.Children.Add(_installTime);
        content.Children.Add(timeRow);

        content.Children.Add(Section("If Windows requires a restart", "Hvis Windows krever omstart"));
        _rebootConfirm.Checked += (_, _) => _rebootChoice = "confirm";
        _rebootOutside.Checked += (_, _) => _rebootChoice = "outsideActiveHours";
        _rebootOff.Checked += (_, _) => _rebootChoice = "off";
        content.Children.Add(_rebootConfirm);
        content.Children.Add(_rebootOutside);
        content.Children.Add(_rebootOff);

        content.Children.Add(Section("Shortcuts and monitoring", "Snarveier og overvåking"));
        content.Children.Add(_desktop);
        content.Children.Add(_menu);
        content.Children.Add(_taskbar);
        content.Children.Add(_agent);
        content.Children.Add(_service);
        content.Children.Add(_serviceHint);
        content.Children.Add(new TextBlock
        {
            Text = _norwegian
                ? "Eksisterende Update Express-snarveier erstattes (ikke dupliseres) slik at ikon og sti alltid peker på gjeldende installasjonsmappe."
                : "Existing Update Express shortcuts are replaced (not duplicated) so the icon and path always point at the current install folder.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 8)
        });
        content.Children.Add(new TextBlock
        {
            Text = _norwegian
                ? "Alle-brukere installerer for alle; bakgrunnsagent for spørsmål konfigureres for innlogget bruker. Windows-tjeneste krever administrator."
                : "All-users installation installs the app for everyone; the prompt agent is configured for the installing user. Windows service requires administrator.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Brushes.DimGray
        });

        _service.Checked += (_, _) =>
        {
            _all.IsChecked = true;
            RefreshScopeUi();
        };
        _service.Unchecked += (_, _) => RefreshScopeUi();

        SeedFromPolicy();
        RefreshScopeUi();

        var buttons = new Grid
        {
            Height = 27,
            MinHeight = 27,
            MaxHeight = 27,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        Grid.SetRow(buttons, 1);
        var ok = new Button
        {
            Content = repair ? (_norwegian ? "Reparer" : "Repair") : (_norwegian ? "Installer" : "Install"),
            Width = 90,
            Height = 27,
            MinHeight = 27,
            MaxHeight = 27,
            Padding = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            IsDefault = true
        };
        ok.Click += (_, _) =>
        {
            if (!TryCommit()) return;
            DialogResult = true;
        };
        var cancel = new Button
        {
            Content = _norwegian ? "Avbryt" : "Cancel",
            Width = 90,
            Height = 27,
            MinHeight = 27,
            MaxHeight = 27,
            Padding = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            IsCancel = true
        };
        cancel.Click += (_, _) => { DialogResult = false; };
        Grid.SetColumn(ok, 0);
        Grid.SetColumn(cancel, 2);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
    }

    private void ApplyLocalizedLabels(bool repair)
    {
        _current.Content = _norwegian ? "Kun gjeldende bruker" : "Current user only";
        _all.Content = _norwegian ? "Alle brukere (krever administrator)" : "All users (administrator required)";
        _desktop.Content = _norwegian ? "Opprett snarvei på skrivebordet" : "Create desktop shortcut";
        _menu.Content = _norwegian ? "Opprett Start-meny-snarvei" : "Create Start Menu shortcut";
        _taskbar.Content = _norwegian ? "Fest til oppgavelinjen (erstatt eksisterende)" : "Pin to taskbar (replace existing pin if present)";
        _agent.Content = _norwegian
            ? "Aktiver bakgrunnsovervåking ved innlogging (gjeldende bruker)"
            : "Enable background monitoring at sign-in (current user)";
        _service.Content = _norwegian
            ? "Installer som Windows-tjeneste (maskin / LocalSystem)"
            : "Install as Windows service (machine / LocalSystem)";
        _serviceHint.Text = _norwegian
            ? "Tjenesten kjører bakgrunnsautomatisering med systemrettigheter. UI og spørsmål forblir i brukersesjonen via bakgrunnsagenten. Eksplisitt samtykke kreves."
            : "The service runs background automation with system rights. UI and prompts stay in the user session via the tray agent. Explicit consent required.";
        _rebootConfirm.Content = _norwegian ? "Spør meg om bekreftelse" : "Ask me for confirmation";
        _rebootOutside.Content = _norwegian ? "Bare utenom arbeidstid" : "Only outside active hours";
        _rebootOff.Content = _norwegian ? "Jeg restarter selv (ingen auto-omstart)" : "I’ll restart myself (no auto-reboot)";
        _ = repair;
    }

    private UIElement Section(string en, string no) => new TextBlock
    {
        Text = _norwegian ? no : en,
        Margin = new Thickness(0, 14, 0, 4),
        FontSize = 14,
        FontWeight = FontWeights.SemiBold
    };

    private UIElement ProfileTip(System.Windows.Controls.RadioButton radio, string value, string label, string tip)
    {
        radio.Content = label;
        radio.FontWeight = FontWeights.SemiBold;
        radio.Checked += (_, _) =>
        {
            _profile = value;
            ApplyProfileDefaultsToFields(value);
        };
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        panel.Children.Add(radio);
        panel.Children.Add(new TextBlock
        {
            Text = tip,
            FontSize = 12,
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(22, 0, 0, 0)
        });
        return panel;
    }

    private void SeedFromPolicy()
    {
        var policy = UpdatePolicy.Load();
        policy.Normalize();
        if (!string.IsNullOrWhiteSpace(policy.SetupProfile))
            _profile = policy.SetupProfile.Trim().ToLowerInvariant();
        _activeStart.Text = policy.WindowsAutomation.AutoReboot.ActiveHoursStart.ToString(CultureInfo.InvariantCulture);
        _activeEnd.Text = policy.WindowsAutomation.AutoReboot.ActiveHoursEnd.ToString(CultureInfo.InvariantCulture);
        _daysMask = policy.InstallDaysMask == 0 ? MaintenanceSetup.AllDaysMask : policy.InstallDaysMask;
        _installTime.Text = MaintenanceSetup.NormalizeTime(policy.InstallTime, "18:00");
        if (!policy.WindowsAutomation.AutoReboot.Enabled) _rebootChoice = "off";
        else if (string.Equals(policy.WindowsAutomation.AutoReboot.Mode, "outsideActiveHours", StringComparison.OrdinalIgnoreCase))
            _rebootChoice = "outsideActiveHours";
        else _rebootChoice = "confirm";

        _profilePrivate.IsChecked = _profile == "private";
        _profileBusiness.IsChecked = _profile == "business";
        _profileBoth.IsChecked = _profile == "both";
        if (_profilePrivate.IsChecked != true && _profileBusiness.IsChecked != true && _profileBoth.IsChecked != true)
            _profilePrivate.IsChecked = true;

        for (int i = 0; i < 7; i++)
            _dayBoxes[i].IsChecked = MaintenanceSetup.HasDay(_daysMask, i);

        _rebootConfirm.IsChecked = _rebootChoice == "confirm";
        _rebootOutside.IsChecked = _rebootChoice == "outsideActiveHours";
        _rebootOff.IsChecked = _rebootChoice == "off";
    }

    private void ApplyProfileDefaultsToFields(string profile)
    {
        var draft = new UpdatePolicy();
        draft.Normalize();
        MaintenanceSetup.ApplyProfileDefaults(draft, profile);
        _activeStart.Text = draft.WindowsAutomation.AutoReboot.ActiveHoursStart.ToString(CultureInfo.InvariantCulture);
        _activeEnd.Text = draft.WindowsAutomation.AutoReboot.ActiveHoursEnd.ToString(CultureInfo.InvariantCulture);
        _daysMask = draft.InstallDaysMask;
        _installTime.Text = draft.InstallTime;
        for (int i = 0; i < 7; i++)
            _dayBoxes[i].IsChecked = MaintenanceSetup.HasDay(_daysMask, i);
        if (!draft.WindowsAutomation.AutoReboot.Enabled) _rebootChoice = "off";
        else if (string.Equals(draft.WindowsAutomation.AutoReboot.Mode, "outsideActiveHours", StringComparison.OrdinalIgnoreCase))
            _rebootChoice = "outsideActiveHours";
        else _rebootChoice = "confirm";
        _rebootConfirm.IsChecked = _rebootChoice == "confirm";
        _rebootOutside.IsChecked = _rebootChoice == "outsideActiveHours";
        _rebootOff.IsChecked = _rebootChoice == "off";
    }

    private void RefreshScopeUi()
    {
        bool machine = _all.IsChecked == true;
        bool service = _service.IsChecked == true;
        if (service && !machine)
            _all.IsChecked = true;

        // Per-user agent remains available for prompts even with machine service install.
        _agent.IsEnabled = true;
        if (!machine && !service && _agent.IsChecked != true)
            _agent.IsEnabled = true;

        _service.IsEnabled = true;
        if (!machine && _service.IsChecked == true)
            _serviceHint.Foreground = Brushes.DarkOrange;
        else
            _serviceHint.Foreground = Brushes.DimGray;
    }

    private bool TryCommit()
    {
        if (!int.TryParse(_activeStart.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int start) ||
            start is < 0 or > 23 ||
            !int.TryParse(_activeEnd.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int end) ||
            end is < 0 or > 23)
        {
            MessageBox.Show(this,
                _norwegian ? "Arbeidstid må være timer 0–23." : "Active hours must be integers 0–23.",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        int mask = 0;
        for (int i = 0; i < 7; i++)
            if (_dayBoxes[i].IsChecked == true) mask |= 1 << i;
        if (mask == 0)
        {
            MessageBox.Show(this,
                _norwegian ? "Velg minst én dag." : "Select at least one day.",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        string time = _installTime.Text.Trim();
        if (!TimeSpan.TryParseExact(time, @"hh\:mm", CultureInfo.InvariantCulture, out _))
        {
            MessageBox.Show(this,
                _norwegian ? "Klokkeslett må være TT:mm, f.eks. 18:00." : "Time must be HH:mm, e.g. 18:00.",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (_service.IsChecked == true && _all.IsChecked != true)
            _all.IsChecked = true;

        if (_service.IsChecked == true)
        {
            var consent = MessageBox.Show(this,
                _norwegian
                    ? "Du har valgt å installere Update Express som Windows-tjeneste (LocalSystem).\n\n" +
                      "Tjenesten kan kjøre bakgrunnsautomatisering uten innlogget bruker. UI og spørsmål forblir i brukersesjonen.\n\n" +
                      "Fortsette?"
                    : "You chose to install Update Express as a Windows service (LocalSystem).\n\n" +
                      "The service can run background automation without a signed-in user. UI and prompts stay in the user session.\n\n" +
                      "Continue?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (consent != MessageBoxResult.Yes)
                return false;
        }

        ActiveHoursStart = start;
        ActiveHoursEnd = end;
        _daysMask = mask;
        InstallTime = time;
        return true;
    }
}
