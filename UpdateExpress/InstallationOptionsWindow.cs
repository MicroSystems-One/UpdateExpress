using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UpdateExpress.Services;

namespace UpdateExpress;

/// <summary>
/// Multi-step Installation / Repair options wizard.
/// Defaults match the product install screenshots (all-users, both, 8–16, Mon–Fri 17:00,
/// restart outside active hours, shortcuts + tray + Windows service).
/// </summary>
public sealed class InstallationOptionsWindow : Window
{
    private const int WeekdaysMask = 0b0011111; // Mon..Fri

    private readonly bool _norwegian;
    private readonly bool _repair;
    private int _page;

    private readonly System.Windows.Controls.RadioButton _current = new() { Margin = new Thickness(0, 6, 0, 4) };
    private readonly System.Windows.Controls.RadioButton _all = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly System.Windows.Controls.RadioButton _profilePrivate = new() { GroupName = "InstallProfile", Margin = new Thickness(0, 4, 0, 2) };
    private readonly System.Windows.Controls.RadioButton _profileBusiness = new() { GroupName = "InstallProfile", Margin = new Thickness(0, 4, 0, 2) };
    private readonly System.Windows.Controls.RadioButton _profileBoth = new() { GroupName = "InstallProfile", Margin = new Thickness(0, 4, 0, 2) };
    private readonly System.Windows.Controls.RadioButton _profileServer = new() { GroupName = "InstallProfile", Margin = new Thickness(0, 4, 0, 2) };
    private readonly System.Windows.Controls.TextBox _activeStart = new() { Width = 48, Height = 28, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 0, 12, 0) };
    private readonly System.Windows.Controls.TextBox _activeEnd = new() { Width = 48, Height = 28, Padding = new Thickness(6, 3, 6, 3) };
    private readonly System.Windows.Controls.CheckBox[] _dayBoxes = new System.Windows.Controls.CheckBox[7];
    private readonly System.Windows.Controls.TextBox _installTime = new() { Width = 72, Height = 28, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(8, 0, 0, 0) };
    private readonly System.Windows.Controls.RadioButton _rebootConfirm = new() { GroupName = "InstallReboot", Margin = new Thickness(0, 4, 0, 2) };
    private readonly System.Windows.Controls.RadioButton _rebootOutside = new() { GroupName = "InstallReboot", Margin = new Thickness(0, 4, 0, 2) };
    private readonly System.Windows.Controls.RadioButton _rebootOff = new() { GroupName = "InstallReboot", Margin = new Thickness(0, 4, 0, 8) };
    private readonly System.Windows.Controls.CheckBox _desktop = new() { IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
    private readonly System.Windows.Controls.CheckBox _menu = new() { IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
    private readonly System.Windows.Controls.CheckBox _taskbar = new() { IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
    private readonly System.Windows.Controls.CheckBox _agent = new() { IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
    private readonly System.Windows.Controls.CheckBox _service = new() { IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBlock _serviceHint = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Foreground = Brushes.DimGray,
        Margin = new Thickness(22, 0, 0, 8)
    };
    private readonly TextBlock _stepLabel = new()
    {
        FontSize = 12,
        Foreground = Brushes.DimGray,
        Margin = new Thickness(0, 0, 0, 10)
    };
    private readonly TextBlock _heading = new()
    {
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
        FontSize = 20,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 4)
    };

    private readonly StackPanel _pageHost = new();
    private readonly Button _back = new();
    private readonly Button _next = new();
    private readonly Button _cancel = new();

    private string _profile = "both";
    private string _rebootChoice = "outsideActiveHours";
    private int _daysMask = WeekdaysMask;
    private readonly bool _server;
    private readonly bool _serverTestSwitch;
    private readonly UpdatePolicy? _saved;

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
    public string InstallTime { get; private set; } = "17:00";
    public string RebootChoice => _rebootChoice;

    public InstallationOptionsWindow(bool repair)
        : this(repair, MaintenanceSetup.SetupTreatsThisMachineAsServer, MaintenanceSetup.ServerTestSwitchOn)
    {
    }

    /// <param name="saved">For Repair: the policy to start from; what is saved on this machine when left out.</param>
    internal InstallationOptionsWindow(
        bool repair, bool serverOs, bool serverTestSwitch, UpdatePolicy? saved = null, bool? norwegian = null)
    {
        _server = serverOs;
        _serverTestSwitch = serverTestSwitch;
        _saved = saved;
        _repair = repair;
        _norwegian = norwegian ?? MaintenanceSetup.IsNorwegian();
        Title = repair
            ? (_norwegian ? "Update Express – Reparasjon" : "Update Express – Repair options")
            : (_norwegian ? "Update Express – Installasjonsvalg" : "Update Express – Installation options");
        AppIcon.Apply(this);
        Width = 560;
        Height = 560;
        MinWidth = 520;
        MinHeight = 480;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White;

        ApplyLocalizedLabels();
        BuildChrome();
        BuildDayBoxes();
        WireEvents();

        if (repair)
            SeedFromPolicy();
        else
            ApplyFreshInstallDefaults();

        RefreshScopeUi();
        ShowPage(0);
    }

    private void BuildChrome()
    {
        var root = new Grid { Margin = new Thickness(24, 20, 24, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(27) });
        Content = root;

        Grid.SetRow(_heading, 0);
        root.Children.Add(_heading);
        Grid.SetRow(_stepLabel, 1);
        root.Children.Add(_stepLabel);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(scroll, 2);
        scroll.Content = _pageHost;
        root.Children.Add(scroll);

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
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        Grid.SetRow(buttons, 3);

        StyleNavButton(_back, _norwegian ? "Tilbake" : "Back");
        StyleNavButton(_next, _norwegian ? "Neste" : "Next");
        StyleNavButton(_cancel, _norwegian ? "Avbryt" : "Cancel");
        _next.IsDefault = true;
        _cancel.IsCancel = true;
        _back.Click += (_, _) => ShowPage(_page - 1);
        _next.Click += (_, _) => OnNext();
        _cancel.Click += (_, _) => { DialogResult = false; };

        Grid.SetColumn(_back, 0);
        Grid.SetColumn(_next, 2);
        Grid.SetColumn(_cancel, 4);
        buttons.Children.Add(_back);
        buttons.Children.Add(_next);
        buttons.Children.Add(_cancel);
        root.Children.Add(buttons);
    }

    private static void StyleNavButton(Button button, string content)
    {
        button.Content = content;
        button.Width = 90;
        button.Height = 27;
        button.MinHeight = 27;
        button.MaxHeight = 27;
        button.Padding = new Thickness(8, 0, 8, 0);
        button.VerticalAlignment = VerticalAlignment.Stretch;
        button.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
    }

    private void BuildDayBoxes()
    {
        var labels = _norwegian ? MaintenanceSetup.DayLabelsNo : MaintenanceSetup.DayLabelsEn;
        for (int i = 0; i < 7; i++)
        {
            _dayBoxes[i] = new System.Windows.Controls.CheckBox
            {
                Content = labels[i],
                Margin = new Thickness(0, 0, 12, 6),
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }
    }

    private void WireEvents()
    {
        _all.Checked += (_, _) => RefreshScopeUi();
        _current.Checked += (_, _) => RefreshScopeUi();
        _rebootConfirm.Checked += (_, _) => _rebootChoice = "confirm";
        _rebootOutside.Checked += (_, _) => _rebootChoice = "outsideActiveHours";
        _rebootOff.Checked += (_, _) => _rebootChoice = "off";
        _service.Checked += (_, _) =>
        {
            _all.IsChecked = true;
            RefreshScopeUi();
        };
        _service.Unchecked += (_, _) => RefreshScopeUi();
    }

    private void ApplyLocalizedLabels()
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
            ? "Tjenesten «Update Express» (internt MicroSystemsUpdateExpress) kjører bakgrunnsautomatisering med systemrettigheter. UI og spørsmål forblir i brukersesjonen via bakgrunnsagenten. Eksplisitt samtykke kreves."
            : "The “Update Express” service (internal name MicroSystemsUpdateExpress) runs background automation with system rights. UI and prompts stay in the user session via the tray agent. Explicit consent required.";
        _rebootConfirm.Content = _norwegian ? "Spør meg om bekreftelse" : "Ask me for confirmation";
        _rebootOutside.Content = _norwegian ? "Bare utenom arbeidstid" : "Only outside active hours";
        _rebootOff.Content = _norwegian ? "Jeg restarter selv (ingen auto-omstart)" : "I’ll restart myself (no auto-reboot)";
    }

    private void ShowPage(int page)
    {
        _page = Math.Clamp(page, 0, 2);
        _pageHost.Children.Clear();

        _heading.Text = _repair
            ? (_norwegian ? "Reparer Update Express" : "Repair Update Express")
            : (_norwegian ? "Installer Update Express" : "Install Update Express");

        _stepLabel.Text = _norwegian
            ? $"Steg {_page + 1} av 3"
            : $"Step {_page + 1} of 3";

        switch (_page)
        {
            case 0:
                _pageHost.Children.Add(Section("Installation scope", "Installasjonsområde"));
                _pageHost.Children.Add(_current);
                _pageHost.Children.Add(_all);
                _pageHost.Children.Add(Section("Profile", "Profil"));
                _pageHost.Children.Add(ProfileTip(_profilePrivate, "private",
                    _norwegian ? "Privat" : "Private",
                    _norwegian ? "Hjemmebruk – mykere kveldsvindu, spør før omstart." : "Home use — softer evening window, ask before restart."));
                _pageHost.Children.Add(ProfileTip(_profileBusiness, "business",
                    _norwegian ? "Bedrift" : "Business",
                    _norwegian ? "Arbeids-PC – man–fre arbeidstid, omstart utenom arbeidstid." : "Work PC — weekday hours, restart outside active hours."));
                _pageHost.Children.Add(ProfileTip(_profileBoth, "both",
                    _norwegian ? "Begge deler" : "Both",
                    _norwegian
                        ? "Blanding – man–fre 17:00, omstart utenom arbeidstid."
                        : "Mixed — weekdays at 17:00, restart outside active hours."));
                _pageHost.Children.Add(ProfileTip(_profileServer, MaintenanceSetup.ServerProfile,
                    "Server", MaintenanceSetup.ServerProfileTip(_norwegian)));
                if (_serverTestSwitch)
                    _pageHost.Children.Add(new TextBlock
                    {
                        Text = MaintenanceSetup.ServerTestSwitchNote(_norwegian),
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Color.FromRgb(198, 40, 40)),
                        Margin = new Thickness(0, 4, 0, 8)
                    });
                break;
            case 1:
                _pageHost.Children.Add(Section("Active hours", "Arbeidstid"));
                _pageHost.Children.Add(Hint(
                    _norwegian
                        ? "I denne perioden unngår vi omstart når det er mulig."
                        : "We avoid restarts in this period when possible."));
                var hours = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
                hours.Children.Add(Label(_norwegian ? "Fra" : "From"));
                hours.Children.Add(_activeStart);
                hours.Children.Add(Label(_norwegian ? "til" : "to"));
                hours.Children.Add(_activeEnd);
                hours.Children.Add(new TextBlock
                {
                    Text = _norwegian ? " (time 0–23)" : " (hour 0–23)",
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(8, 0, 0, 0)
                });
                _pageHost.Children.Add(hours);

                _pageHost.Children.Add(Section("Install window", "Installasjonsvindu"));
                _pageHost.Children.Add(Hint(
                    _norwegian
                        ? "Velg dager og ett klokkeslett. Defender kjører uansett kontinuerlig."
                        : "Pick days and one clock time. Defender still runs continuously."));
                var days = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
                foreach (var box in _dayBoxes) days.Children.Add(box);
                _pageHost.Children.Add(days);
                var timeRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
                timeRow.Children.Add(Label(_norwegian ? "Klokkeslett" : "Time"));
                timeRow.Children.Add(_installTime);
                _pageHost.Children.Add(timeRow);

                _pageHost.Children.Add(Section("If Windows requires a restart", "Hvis Windows krever omstart"));
                _pageHost.Children.Add(_rebootConfirm);
                _pageHost.Children.Add(_rebootOutside);
                _pageHost.Children.Add(_rebootOff);
                break;
            default:
                _pageHost.Children.Add(Section("Shortcuts and monitoring", "Snarveier og overvåking"));
                _pageHost.Children.Add(_desktop);
                _pageHost.Children.Add(_menu);
                _pageHost.Children.Add(_taskbar);
                _pageHost.Children.Add(_agent);
                _pageHost.Children.Add(_service);
                _pageHost.Children.Add(_serviceHint);
                _pageHost.Children.Add(Hint(
                    _norwegian
                        ? "Eksisterende Update Express-snarveier erstattes (ikke dupliseres) slik at ikon og sti alltid peker på gjeldende installasjonsmappe."
                        : "Existing Update Express shortcuts are replaced (not duplicated) so the icon and path always point at the current install folder."));
                _pageHost.Children.Add(Hint(
                    _norwegian
                        ? "Alle-brukere installerer for alle; bakgrunnsagent for spørsmål konfigureres for innlogget bruker. Windows-tjeneste krever administrator. I Tjenester heter den «Update Express»."
                        : "All-users installation installs the app for everyone; the prompt agent is configured for the installing user. Windows service requires administrator. In Services it appears as “Update Express”."));
                break;
        }

        _back.IsEnabled = _page > 0;
        _back.Visibility = _page > 0 ? Visibility.Visible : Visibility.Hidden;
        bool last = _page >= 2;
        _next.Content = last
            ? (_repair ? (_norwegian ? "Reparer" : "Repair") : (_norwegian ? "Installer" : "Install"))
            : (_norwegian ? "Neste" : "Next");
    }

    private void OnNext()
    {
        if (_page < 2)
        {
            if (_page == 1 && !TryValidateSchedule()) return;
            ShowPage(_page + 1);
            return;
        }

        if (!TryCommit()) return;
        DialogResult = true;
    }

    private UIElement Section(string en, string no) => new TextBlock
    {
        Text = _norwegian ? no : en,
        Margin = new Thickness(0, 10, 0, 4),
        FontSize = 14,
        FontWeight = FontWeights.SemiBold
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Foreground = Brushes.DimGray,
        Margin = new Thickness(0, 0, 0, 8)
    };

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 0)
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

    /// <summary>Screenshot defaults for a fresh Install.</summary>
    private void ApplyFreshInstallDefaults()
    {
        _all.IsChecked = true;
        _current.IsChecked = false;
        _profile = "both";
        _profileBoth.IsChecked = true;
        _profilePrivate.IsChecked = false;
        _profileBusiness.IsChecked = false;
        _profileServer.IsChecked = false;
        _activeStart.Text = "8";
        _activeEnd.Text = "16";
        _daysMask = WeekdaysMask;
        for (int i = 0; i < 7; i++)
            _dayBoxes[i].IsChecked = MaintenanceSetup.HasDay(_daysMask, i);
        _installTime.Text = "17:00";
        _rebootChoice = "outsideActiveHours";
        _rebootOutside.IsChecked = true;
        _rebootConfirm.IsChecked = false;
        _rebootOff.IsChecked = false;
        _desktop.IsChecked = true;
        _menu.IsChecked = true;
        _taskbar.IsChecked = true;
        _agent.IsChecked = true;
        _service.IsChecked = true;

        if (_server)
        {
            _profile = MaintenanceSetup.ServerProfile;
            _profileBoth.IsChecked = false;
            _profileServer.IsChecked = true;
            ApplyProfileDefaultsToFields(_profile);
        }
    }

    private void SeedFromPolicy()
    {
        ApplyFreshInstallDefaults();
        var policy = _saved ?? UpdatePolicy.Load();
        policy.Normalize();
        if (!policy.SetupCompleted) return;

        if (!string.IsNullOrWhiteSpace(policy.SetupProfile))
            _profile = policy.SetupProfile.Trim().ToLowerInvariant();
        _activeStart.Text = policy.WindowsAutomation.AutoReboot.ActiveHoursStart.ToString(CultureInfo.InvariantCulture);
        _activeEnd.Text = policy.WindowsAutomation.AutoReboot.ActiveHoursEnd.ToString(CultureInfo.InvariantCulture);
        _daysMask = policy.InstallDaysMask == 0 ? WeekdaysMask : policy.InstallDaysMask;
        _installTime.Text = MaintenanceSetup.NormalizeTime(policy.InstallTime, "17:00");
        if (!policy.WindowsAutomation.AutoReboot.Enabled) _rebootChoice = "off";
        else if (string.Equals(policy.WindowsAutomation.AutoReboot.Mode, "outsideActiveHours", StringComparison.OrdinalIgnoreCase))
            _rebootChoice = "outsideActiveHours";
        else _rebootChoice = "confirm";

        _profilePrivate.IsChecked = _profile == "private";
        _profileBusiness.IsChecked = _profile == "business";
        _profileBoth.IsChecked = _profile == "both";
        _profileServer.IsChecked = _profile == MaintenanceSetup.ServerProfile;
        if (_profilePrivate.IsChecked != true && _profileBusiness.IsChecked != true && _profileBoth.IsChecked != true &&
            _profileServer.IsChecked != true)
            _profileBoth.IsChecked = true;

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

        _agent.IsEnabled = true;
        _service.IsEnabled = true;
        _serviceHint.Foreground = (!machine && service) ? Brushes.DarkOrange : Brushes.DimGray;
    }

    private bool TryValidateSchedule()
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
                _norwegian ? "Klokkeslett må være TT:mm, f.eks. 17:00." : "Time must be HH:mm, e.g. 17:00.",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        ActiveHoursStart = start;
        ActiveHoursEnd = end;
        _daysMask = mask;
        InstallTime = time;
        return true;
    }

    private bool TryCommit()
    {
        if (!TryValidateSchedule()) return false;

        if (_service.IsChecked == true && _all.IsChecked != true)
            _all.IsChecked = true;

        if (_service.IsChecked == true)
        {
            var consent = MessageBox.Show(this,
                _norwegian
                    ? "Du har valgt å installere Update Express som Windows-tjeneste (LocalSystem).\n\n" +
                      "Tjenesten kan kjøre bakgrunnsautomatisering uten innlogget bruker. UI og spørsmål forblir i brukersesjonen.\n\n" +
                      "I Tjenester-listen heter den «Update Express».\n\n" +
                      "Fortsette?"
                    : "You chose to install Update Express as a Windows service (LocalSystem).\n\n" +
                      "The service can run background automation without a signed-in user. UI and prompts stay in the user session.\n\n" +
                      "In the Services list it appears as “Update Express”.\n\n" +
                      "Continue?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (consent != MessageBoxResult.Yes)
                return false;
        }

        return true;
    }
}
