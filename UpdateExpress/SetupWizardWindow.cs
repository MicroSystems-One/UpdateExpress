using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UpdateExpress.Services;

namespace UpdateExpress;

/// <summary>First-run / Change setup wizard: profile → hours → install days → reboot → extras → summary.</summary>
public sealed class SetupWizardWindow : Window
{
    private readonly bool _norwegian;
    private int _step;
    private string _profile = "private";
    private int _activeStart = 8;
    private int _activeEnd = 16;
    private int _daysMask = MaintenanceSetup.AllDaysMask;
    private string _installTime = "18:00";
    private string _rebootChoice = "confirm"; // off | confirm | outsideActiveHours
    private bool _drivers;
    private bool _apps;

    private readonly TextBlock _title;
    private readonly TextBlock _body;
    private readonly StackPanel _content;
    private readonly Button _back;
    private readonly Button _next;
    private readonly Button _skip;
    private readonly System.Windows.Controls.CheckBox[] _dayBoxes = new System.Windows.Controls.CheckBox[7];

    public bool Completed { get; private set; }

    public SetupWizardWindow(Window? owner = null)
    {
        _norwegian = MaintenanceSetup.IsNorwegian();
        Owner = owner;
        Title = _norwegian ? "Update Express – Oppsett" : "Update Express – Setup";
        Width = 560;
        Height = 520;
        MinWidth = 480;
        MinHeight = 420;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(243, 243, 243));
        ResizeMode = ResizeMode.CanResizeWithGrip;

        var root = new DockPanel { Margin = new Thickness(22) };
        Content = root;

        // Fixed 27px footer row: Skip left, Back+Next/Finish right — same line on every step.
        var footer = new Grid
        {
            Height = 27,
            MinHeight = 27,
            MaxHeight = 27,
            Margin = new Thickness(0, 16, 0, 0),
            VerticalAlignment = System.Windows.VerticalAlignment.Bottom
        };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var footerStyle = CreateFooterButtonStyle();
        _skip = new Button
        {
            Content = _norwegian ? "Hopp over" : "Skip",
            Style = footerStyle,
            Height = 27,
            MinHeight = 27,
            MaxHeight = 27,
            MinWidth = 88,
            Padding = new Thickness(10, 0, 10, 0),
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch
        };
        _skip.Click += (_, _) => Skip();
        Grid.SetColumn(_skip, 0);
        footer.Children.Add(_skip);

        _back = new Button
        {
            Content = _norwegian ? "Tilbake" : "Back",
            Style = footerStyle,
            Height = 27,
            MinHeight = 27,
            MaxHeight = 27,
            MinWidth = 88,
            Padding = new Thickness(10, 0, 10, 0),
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
            IsEnabled = false
        };
        _back.Click += (_, _) => { _step = Math.Max(0, _step - 1); Render(); };
        Grid.SetColumn(_back, 2);
        footer.Children.Add(_back);

        _next = new Button
        {
            Content = _norwegian ? "Neste" : "Next",
            Style = footerStyle,
            Height = 27,
            MinHeight = 27,
            MaxHeight = 27,
            MinWidth = 88,
            Padding = new Thickness(10, 0, 10, 0),
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
            FontWeight = FontWeights.SemiBold
        };
        _next.Click += (_, _) => GoNext();
        Grid.SetColumn(_next, 4);
        footer.Children.Add(_next);

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        header.Children.Add(new TextBlock
        {
            Text = "Update Express",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(93, 93, 93)),
            Margin = new Thickness(0, 0, 0, 4)
        });
        _title = new TextBlock
        {
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(28, 28, 28)),
            TextWrapping = TextWrapping.Wrap
        };
        header.Children.Add(_title);
        _body = new TextBlock
        {
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(93, 93, 93)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };
        header.Children.Add(_body);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(scroll);
        _content = new StackPanel();
        scroll.Content = _content;

        // Seed from existing policy if any.
        var policy = UpdatePolicy.Load();
        policy.Normalize();
        if (!string.IsNullOrWhiteSpace(policy.SetupProfile))
            _profile = policy.SetupProfile;
        _activeStart = policy.WindowsAutomation.AutoReboot.ActiveHoursStart;
        _activeEnd = policy.WindowsAutomation.AutoReboot.ActiveHoursEnd;
        _daysMask = policy.InstallDaysMask == 0 ? MaintenanceSetup.AllDaysMask : policy.InstallDaysMask;
        _installTime = MaintenanceSetup.NormalizeTime(policy.InstallTime, "18:00");
        _drivers = policy.AutomaticDrivers;
        _apps = policy.AutomaticApplications;
        if (!policy.WindowsAutomation.AutoReboot.Enabled) _rebootChoice = "off";
        else if (string.Equals(policy.WindowsAutomation.AutoReboot.Mode, "outsideActiveHours", StringComparison.OrdinalIgnoreCase))
            _rebootChoice = "outsideActiveHours";
        else _rebootChoice = "confirm";

        Render();
    }

    private void Skip()
    {
        var policy = UpdatePolicy.Load();
        policy.Normalize();
        if (string.IsNullOrWhiteSpace(policy.SetupProfile))
            MaintenanceSetup.ApplyProfileDefaults(policy, "private");
        else
            MaintenanceSetup.ApplyToPolicy(policy, policy.InstallDaysMask, policy.InstallTime);
        policy.SetupCompleted = true;
        policy.Save();
        MaintenanceSetup.SyncReminderFromPolicy(policy);
        Completed = true;
        DialogResult = true;
        Close();
    }

    private void GoNext()
    {
        if (!CommitStep()) return;
        if (_step >= 6)
        {
            Finish();
            return;
        }
        _step++;
        Render();
    }

    private void Finish()
    {
        var policy = UpdatePolicy.Load();
        policy.Normalize();
        MaintenanceSetup.ApplyProfileDefaults(policy, _profile);
        // Re-apply user overrides from later steps (profile defaults first, then edits).
        policy.WindowsAutomation.AutoReboot.ActiveHoursStart = _activeStart;
        policy.WindowsAutomation.AutoReboot.ActiveHoursEnd = _activeEnd;
        MaintenanceSetup.ApplyToPolicy(policy, _daysMask, _installTime);
        policy.WindowsAutomation.AutoReboot.Enabled = _rebootChoice != "off";
        policy.WindowsAutomation.AutoReboot.Mode = _rebootChoice == "outsideActiveHours" ? "outsideActiveHours" : "confirm";
        policy.WindowsAutomation.NeverAutoReboot = !policy.WindowsAutomation.AutoReboot.Enabled;
        policy.AutomaticDrivers = _drivers;
        policy.AutomaticApplications = _apps;
        policy.SetupCompleted = true;
        policy.SetupProfile = _profile;
        policy.Save();
        MaintenanceSetup.SyncReminderFromPolicy(policy);
        Completed = true;
        DialogResult = true;
        Close();
    }

    private bool CommitStep()
    {
        switch (_step)
        {
            case 1:
                // profile radios already bound
                MaintenanceSetup.ApplyProfileDefaults(UpdatePolicy.Load(), _profile); // only to seed defaults into local fields
                var seeded = UpdatePolicy.Load();
                MaintenanceSetup.ApplyProfileDefaults(seeded, _profile);
                _activeStart = seeded.WindowsAutomation.AutoReboot.ActiveHoursStart;
                _activeEnd = seeded.WindowsAutomation.AutoReboot.ActiveHoursEnd;
                _daysMask = seeded.InstallDaysMask;
                _installTime = seeded.InstallTime;
                if (!seeded.WindowsAutomation.AutoReboot.Enabled) _rebootChoice = "off";
                else if (string.Equals(seeded.WindowsAutomation.AutoReboot.Mode, "outsideActiveHours", StringComparison.OrdinalIgnoreCase))
                    _rebootChoice = "outsideActiveHours";
                else _rebootChoice = "confirm";
                break;
            case 2:
                if (_content.Children.OfType<WrapPanel>().FirstOrDefault() is WrapPanel hours)
                {
                    var boxes = hours.Children.OfType<System.Windows.Controls.TextBox>().ToList();
                    if (boxes.Count >= 2 &&
                        int.TryParse(boxes[0].Text.Trim(), out int s) && s is >= 0 and <= 23 &&
                        int.TryParse(boxes[1].Text.Trim(), out int e) && e is >= 0 and <= 23)
                    {
                        _activeStart = s;
                        _activeEnd = e;
                    }
                    else
                    {
                        MessageBox.Show(this,
                            _norwegian ? "Arbeidstid må være timer 0–23." : "Active hours must be integers 0–23.",
                            Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }
                }
                break;
            case 3:
                int mask = 0;
                for (int i = 0; i < 7; i++)
                    if (_dayBoxes[i]?.IsChecked == true) mask |= 1 << i;
                if (mask == 0)
                {
                    MessageBox.Show(this,
                        _norwegian ? "Velg minst én dag." : "Select at least one day.",
                        Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                _daysMask = mask;
                if (_content.Children.OfType<System.Windows.Controls.TextBox>().FirstOrDefault(t => t.Name == "InstallTimeBox") is System.Windows.Controls.TextBox timeBox)
                {
                    string t = timeBox.Text.Trim();
                    if (!TimeSpan.TryParseExact(t, @"hh\:mm", CultureInfo.InvariantCulture, out _))
                    {
                        MessageBox.Show(this,
                            _norwegian ? "Klokkeslett må være TT:mm, f.eks. 18:00." : "Time must be HH:mm, e.g. 18:00.",
                            Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }
                    _installTime = t;
                }
                break;
            case 4:
                // reboot radios
                break;
            case 5:
                break;
        }
        return true;
    }

    private void Render()
    {
        _content.Children.Clear();
        _back.IsEnabled = _step > 0;
        _next.Content = _step >= 6
            ? (_norwegian ? "Fullfør" : "Finish")
            : (_norwegian ? "Neste" : "Next");
        _skip.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;

        switch (_step)
        {
            case 0:
                _title.Text = _norwegian ? "Hold Windows oppdatert – på dine vilkår" : "Keep Windows current — on your terms";
                _body.Text = _norwegian
                    ? "Update Express hjelper deg å holde PC-en oppdatert. Defender-signaturer oppdateres kontinuerlig. Andre oppdateringer lastes i bakgrunnen og installeres bare i tidsvinduene du velger."
                    : "Update Express helps keep this PC current. Defender signatures update continuously. Other updates download in the background and install only in the windows you choose.";
                _content.Children.Add(InfoLine(_norwegian
                    ? "Denne veiviseren tar ca. ett minutt. Du kan endre valgene senere under Vedlikehold."
                    : "This wizard takes about a minute. You can change choices later under Maintenance."));
                break;

            case 1:
                _title.Text = _norwegian ? "Hvordan brukes denne PC-en?" : "How is this PC used?";
                _body.Text = _norwegian
                    ? "Vi foreslår arbeidstid, installasjonsdager og omstartsregler ut fra bruk."
                    : "We suggest active hours, install days, and restart rules based on use.";
                _content.Children.Add(ProfileRadio("private", _norwegian ? "Privat" : "Private",
                    _norwegian ? "Hjemmebruk – mykere kveldsvindu, spør før omstart." : "Home use — softer evening window, ask before restart."));
                _content.Children.Add(ProfileRadio("business", _norwegian ? "Bedrift" : "Business",
                    _norwegian ? "Arbeids-PC – man–fre arbeidstid, omstart utenom arbeidstid." : "Work PC — weekday hours, restart outside active hours."));
                _content.Children.Add(ProfileRadio("both", _norwegian ? "Begge deler" : "Both",
                    _norwegian ? "Blanding – arbeidstid man–fre, spør før omstart (tryggere)." : "Mixed — weekday hours, ask before restart (safer)."));
                break;

            case 2:
                _title.Text = _norwegian ? "Normal arbeidstid / aktiv tid" : "Normal work / active hours";
                _body.Text = _norwegian
                    ? "I denne perioden unngår vi omstart når det er mulig. Oppdateringer kan lastes ned uansett."
                    : "We avoid restarts in this period when possible. Updates may still download anytime.";
                var hours = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                hours.Children.Add(new TextBlock { Text = _norwegian ? "Fra" : "From", VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
                hours.Children.Add(new System.Windows.Controls.TextBox { Text = _activeStart.ToString(), Width = 48, Height = 28, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 0, 12, 0) });
                hours.Children.Add(new TextBlock { Text = _norwegian ? "til" : "to", VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
                hours.Children.Add(new System.Windows.Controls.TextBox { Text = _activeEnd.ToString(), Width = 48, Height = 28, Padding = new Thickness(6, 3, 6, 3) });
                hours.Children.Add(new TextBlock { Text = _norwegian ? " (time 0–23)" : " (hour 0–23)", VerticalAlignment = System.Windows.VerticalAlignment.Center, Foreground = Brushes.Gray, Margin = new Thickness(8, 0, 0, 0) });
                _content.Children.Add(hours);
                break;

            case 3:
                _title.Text = _norwegian ? "Når kan oppdateringer installeres?" : "When may updates be installed?";
                _body.Text = _norwegian
                    ? "Velg én eller flere dager og ett klokkeslett. Samme tid for alle valgte dager. Defender kjører uansett kontinuerlig."
                    : "Pick one or more days and one clock time. Same time for all selected days. Defender still runs continuously.";
                var days = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
                var labels = _norwegian ? MaintenanceSetup.DayLabelsNo : MaintenanceSetup.DayLabelsEn;
                for (int i = 0; i < 7; i++)
                {
                    int idx = i;
                    var box = new System.Windows.Controls.CheckBox
                    {
                        Content = labels[i],
                        IsChecked = MaintenanceSetup.HasDay(_daysMask, i),
                        Margin = new Thickness(0, 0, 12, 8),
                        VerticalContentAlignment = System.Windows.VerticalAlignment.Center
                    };
                    _dayBoxes[i] = box;
                    days.Children.Add(box);
                }
                _content.Children.Add(days);
                var presets = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
                presets.Children.Add(PresetButton(_norwegian ? "Hverdagskveld 18:00" : "Weeknights 18:00", () =>
                {
                    _daysMask = 0b0011111; // Mon-Fri
                    _installTime = "18:00";
                    Render();
                }));
                presets.Children.Add(PresetButton(_norwegian ? "Tir + fre 18:00" : "Tue + Fri 18:00", () =>
                {
                    _daysMask = (1 << 1) | (1 << 4);
                    _installTime = "18:00";
                    Render();
                }));
                presets.Children.Add(PresetButton(_norwegian ? "Helgemorgen 10:00" : "Weekend morning 10:00", () =>
                {
                    _daysMask = (1 << 5) | (1 << 6);
                    _installTime = "10:00";
                    Render();
                }));
                _content.Children.Add(presets);
                var timeRow = new WrapPanel();
                timeRow.Children.Add(new TextBlock
                {
                    Text = _norwegian ? "Klokkeslett" : "Time",
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                });
                timeRow.Children.Add(new System.Windows.Controls.TextBox
                {
                    Name = "InstallTimeBox",
                    Text = _installTime,
                    Width = 72,
                    Height = 28,
                    Padding = new Thickness(6, 3, 6, 3)
                });
                _content.Children.Add(timeRow);
                break;

            case 4:
                _title.Text = _norwegian ? "Hvis Windows krever omstart" : "If Windows requires a restart";
                _body.Text = _norwegian
                    ? "Firmware forblir alltid manuelt. Du kan endre dette senere."
                    : "Firmware always stays manual. You can change this later.";
                _content.Children.Add(RebootRadio("confirm", _norwegian ? "Spør meg om bekreftelse" : "Ask me for confirmation"));
                _content.Children.Add(RebootRadio("outsideActiveHours", _norwegian ? "Bare utenom arbeidstid" : "Only outside active hours"));
                _content.Children.Add(RebootRadio("off", _norwegian ? "Jeg restarter selv (ingen auto-omstart)" : "I’ll restart myself (no auto-reboot)"));
                break;

            case 5:
                _title.Text = _norwegian ? "Valgfritt: drivere og apper" : "Optional: drivers and apps";
                _body.Text = _norwegian
                    ? "Windows-oppdateringer styres av valgene over. Drivere og WinGet-apper kan følge samme «spør før install»-sti."
                    : "Windows updates follow the choices above. Drivers and WinGet apps can use the same ask-before-install path.";
                var d = new System.Windows.Controls.CheckBox
                {
                    Content = _norwegian ? "Drivere automatisk (spør før install; firmware manuelt)" : "Drivers automatic (ask before install; firmware manual)",
                    IsChecked = _drivers,
                    Margin = new Thickness(0, 8, 0, 8)
                };
                d.Checked += (_, _) => _drivers = true;
                d.Unchecked += (_, _) => _drivers = false;
                _content.Children.Add(d);
                var a = new System.Windows.Controls.CheckBox
                {
                    Content = _norwegian ? "Applikasjoner automatisk (WinGet, spør før install)" : "Applications automatic (WinGet, ask before install)",
                    IsChecked = _apps,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                a.Checked += (_, _) => _apps = true;
                a.Unchecked += (_, _) => _apps = false;
                _content.Children.Add(a);
                break;

            default:
                _title.Text = _norwegian ? "Oppsummering" : "Summary";
                _body.Text = _norwegian
                    ? "Slik blir vedlikeholdet satt. Trykk Fullfør for å lagre."
                    : "This is your maintenance setup. Press Finish to save.";
                var draft = UpdatePolicy.Load();
                draft.Normalize();
                draft.SetupProfile = _profile;
                draft.WindowsAutomation.AutoReboot.ActiveHoursStart = _activeStart;
                draft.WindowsAutomation.AutoReboot.ActiveHoursEnd = _activeEnd;
                MaintenanceSetup.ApplyToPolicy(draft, _daysMask, _installTime);
                draft.WindowsAutomation.AutoReboot.Enabled = _rebootChoice != "off";
                draft.WindowsAutomation.AutoReboot.Mode = _rebootChoice == "outsideActiveHours" ? "outsideActiveHours" : "confirm";
                draft.AutomaticDrivers = _drivers;
                draft.AutomaticApplications = _apps;
                _content.Children.Add(new TextBlock
                {
                    Text = MaintenanceSetup.SummaryText(draft, _norwegian),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0),
                    LineHeight = 22
                });
                break;
        }
    }

    private UIElement InfoLine(string text) => new TextBlock
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 12, 0, 0),
        Foreground = new SolidColorBrush(Color.FromRgb(15, 118, 110))
    };

    private UIElement ProfileRadio(string value, string label, string tip)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var radio = new System.Windows.Controls.RadioButton
        {
            Content = label,
            GroupName = "Profile",
            IsChecked = string.Equals(_profile, value, StringComparison.OrdinalIgnoreCase),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2)
        };
        radio.Checked += (_, _) => _profile = value;
        panel.Children.Add(radio);
        panel.Children.Add(new TextBlock
        {
            Text = tip,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(93, 93, 93)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(22, 0, 0, 0)
        });
        return panel;
    }

    private UIElement RebootRadio(string value, string label)
    {
        var radio = new System.Windows.Controls.RadioButton
        {
            Content = label,
            GroupName = "Reboot",
            IsChecked = _rebootChoice == value,
            Margin = new Thickness(0, 0, 0, 10)
        };
        radio.Checked += (_, _) => _rebootChoice = value;
        return radio;
    }

    private Button PresetButton(string label, Action apply)
    {
        var b = new Button
        {
            Content = label,
            Style = CreateFooterButtonStyle(),
            Height = 27,
            MinHeight = 27,
            MaxHeight = 27,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 0, 8, 6),
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        b.Click += (_, _) => apply();
        return b;
    }

    private static Style CreateFooterButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, Brushes.White));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(28, 28, 28))));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(229, 229, 229))));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 27.0));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 27.0));
        style.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 27.0));
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "ButtonBorder";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(243, 243, 243)), "ButtonBorder"));
        template.Triggers.Add(hover);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.5));
        template.Triggers.Add(disabled);
        style.Setters.Add(new Setter(System.Windows.Controls.Control.TemplateProperty, template));
        return style;
    }
}
