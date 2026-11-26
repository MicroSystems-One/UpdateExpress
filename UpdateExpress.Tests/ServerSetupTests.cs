using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using UpdateExpress.Services;
using Xunit;

namespace UpdateExpress.Tests;

public class ServerSetupTests
{
    private const int Weekdays = 0b0011111;

    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void OnlyServerAndDomainControllerProductTypesAreAServer(int? productType, bool server)
    {
        Assert.Equal(server, WindowsProductType.IsServer(productType));
    }

    [Fact]
    public void ThisMachine_ProductTypeIsReadAndDecidesServer()
    {
        int? productType = WindowsProductType.Read();

        Assert.InRange(productType ?? 0, 1, 3);
        Assert.Equal(productType != WindowsProductType.Workstation, WindowsProductType.IsServerOs);
        Assert.Equal(productType, Windows11Eligibility.ReadFacts().ProductType);
    }

    [Theory]
    [InlineData("both")]
    [InlineData("private")]
    [InlineData("business")]
    [InlineData("Business")]
    [InlineData("server")]
    [InlineData("something-else")]
    public void SavedChoiceWins_OnAServerToo(string saved)
    {
        Assert.Equal(saved, MaintenanceSetup.InitialProfile(saved, serverOs: true, clientDefault: "private"));
        Assert.Equal(saved, MaintenanceSetup.InitialProfile(saved, serverOs: false, clientDefault: "private"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NothingSaved_StartsOnServerOnlyOnAServer(string? saved)
    {
        Assert.Equal("server", MaintenanceSetup.InitialProfile(saved, serverOs: true, clientDefault: "private"));
        Assert.Equal("server", MaintenanceSetup.InitialProfile(saved, serverOs: true, clientDefault: "both"));
        Assert.Equal("private", MaintenanceSetup.InitialProfile(saved, serverOs: false, clientDefault: "private"));
        Assert.Equal("both", MaintenanceSetup.InitialProfile(saved, serverOs: false, clientDefault: "both"));
    }

    [Theory]
    [InlineData("server")]
    [InlineData("Server")]
    [InlineData(" SERVER ")]
    public void ServerDefaults_NoAutomaticRestart_DefenderAutomatic_OthersAskFirst(string profile)
    {
        var policy = new UpdatePolicy();
        // Whatever was there before: the choice turns automatic restart off.
        policy.WindowsAutomation.AutoReboot.Enabled = true;
        policy.WindowsAutomation.AutoReboot.Mode = "outsideActiveHours";
        policy.WindowsAutomation.NeverAutoReboot = false;

        MaintenanceSetup.ApplyProfileDefaults(policy, profile);
        AssertServerDefaults(policy);

        policy.Normalize();
        AssertServerDefaults(policy);
    }

    [Fact]
    public void ServerDefaults_SurviveSavingAndLoading()
    {
        var policy = new UpdatePolicy();
        MaintenanceSetup.ApplyProfileDefaults(policy, "server");
        policy.SetupCompleted = true;

        var loaded = JsonSerializer.Deserialize<UpdatePolicy>(JsonSerializer.Serialize(policy))!;
        loaded.Normalize();

        AssertServerDefaults(loaded);
        Assert.True(loaded.SetupCompleted);
    }

    private static void AssertServerDefaults(UpdatePolicy policy)
    {
        var windows = policy.WindowsAutomation;
        Assert.False(windows.AutoReboot.Enabled);
        Assert.True(windows.NeverAutoReboot);
        Assert.Equal("confirm", windows.AutoReboot.Mode);
        Assert.Equal(8, windows.AutoReboot.ActiveHoursStart);
        Assert.Equal(16, windows.AutoReboot.ActiveHoursEnd);

        Assert.True(windows.Enabled);
        Assert.True(windows.Defender.Enabled);
        Assert.Equal("autoInstall", windows.Defender.Mode);
        Assert.Equal(60, windows.Defender.IntervalMinutes);

        Assert.True(windows.Critical.Enabled);
        Assert.Equal("downloadAndPrompt", windows.Critical.Mode);
        Assert.True(windows.General.Enabled);
        Assert.Equal("downloadAndPrompt", windows.General.Mode);

        Assert.Equal(Weekdays, policy.InstallDaysMask);
        Assert.Equal("17:00", policy.InstallTime);
        Assert.False(policy.AutomaticDrivers);
        Assert.False(policy.AutomaticApplications);
        Assert.False(policy.AutomaticFirmware);
        Assert.Equal("server", policy.SetupProfile.Trim().ToLowerInvariant());
    }

    [Theory]
    [InlineData("private")]
    [InlineData("business")]
    [InlineData("both")]
    public void Server_HandlesDefenderAndOtherUpdatesExactlyAsTheClientChoices(string clientProfile)
    {
        var server = new UpdatePolicy();
        MaintenanceSetup.ApplyProfileDefaults(server, "server");
        server.Normalize();
        var client = new UpdatePolicy();
        MaintenanceSetup.ApplyProfileDefaults(client, clientProfile);
        client.Normalize();

        Assert.Equal(
            JsonSerializer.Serialize(client.WindowsAutomation.Defender),
            JsonSerializer.Serialize(server.WindowsAutomation.Defender));
        Assert.Equal(client.WindowsAutomation.Enabled, server.WindowsAutomation.Enabled);
        Assert.Equal(client.WindowsAutomation.Critical.Enabled, server.WindowsAutomation.Critical.Enabled);
        Assert.Equal(client.WindowsAutomation.Critical.Mode, server.WindowsAutomation.Critical.Mode);
        Assert.Equal(client.WindowsAutomation.Critical.IntervalMinutes, server.WindowsAutomation.Critical.IntervalMinutes);
        Assert.Equal(client.WindowsAutomation.General.Enabled, server.WindowsAutomation.General.Enabled);
        Assert.Equal(client.WindowsAutomation.General.Mode, server.WindowsAutomation.General.Mode);
        Assert.Equal(client.WindowsAutomation.General.IntervalMinutes, server.WindowsAutomation.General.IntervalMinutes);
        // The client choices do restart by themselves; that is the difference.
        Assert.True(client.WindowsAutomation.AutoReboot.Enabled);
        Assert.False(server.WindowsAutomation.AutoReboot.Enabled);
    }

    [Fact]
    public void ClientChoices_KeepTheirDefaults()
    {
        var policy = new UpdatePolicy();

        MaintenanceSetup.ApplyProfileDefaults(policy, "private");
        Assert.True(policy.WindowsAutomation.AutoReboot.Enabled);
        Assert.Equal("confirm", policy.WindowsAutomation.AutoReboot.Mode);
        Assert.Equal((8, 22), (policy.WindowsAutomation.AutoReboot.ActiveHoursStart, policy.WindowsAutomation.AutoReboot.ActiveHoursEnd));
        Assert.Equal((MaintenanceSetup.AllDaysMask, "20:00"), (policy.InstallDaysMask, policy.InstallTime));

        MaintenanceSetup.ApplyProfileDefaults(policy, "business");
        Assert.True(policy.WindowsAutomation.AutoReboot.Enabled);
        Assert.Equal("outsideActiveHours", policy.WindowsAutomation.AutoReboot.Mode);
        Assert.Equal(((1 << 1) | (1 << 3), "18:00"), (policy.InstallDaysMask, policy.InstallTime));

        MaintenanceSetup.ApplyProfileDefaults(policy, "both");
        Assert.True(policy.WindowsAutomation.AutoReboot.Enabled);
        Assert.Equal("outsideActiveHours", policy.WindowsAutomation.AutoReboot.Mode);
        Assert.Equal((Weekdays, "17:00"), (policy.InstallDaysMask, policy.InstallTime));
    }

    [Fact]
    public void ExistingInstall_OnAServer_KeepsItsSavedSettings()
    {
        // What a machine set up before this version holds: "both", restart outside active hours.
        var before = new UpdatePolicy();
        MaintenanceSetup.ApplyProfileDefaults(before, "both");
        before.WindowsAutomation.AutoReboot.ActiveHoursStart = 6;
        before.WindowsAutomation.AutoReboot.ActiveHoursEnd = 19;
        before.SetupCompleted = true;
        string saved = JsonSerializer.Serialize(before);

        var loaded = JsonSerializer.Deserialize<UpdatePolicy>(saved)!;
        loaded.Normalize();

        Assert.Equal("both", loaded.SetupProfile);
        Assert.True(loaded.WindowsAutomation.AutoReboot.Enabled);
        Assert.Equal("outsideActiveHours", loaded.WindowsAutomation.AutoReboot.Mode);
        Assert.Equal((6, 19), (loaded.WindowsAutomation.AutoReboot.ActiveHoursStart, loaded.WindowsAutomation.AutoReboot.ActiveHoursEnd));
        Assert.Equal((Weekdays, "17:00"), (loaded.InstallDaysMask, loaded.InstallTime));
        Assert.Equal("both", MaintenanceSetup.InitialProfile(loaded.SetupProfile, serverOs: true, clientDefault: "private"));

        // Skip in the wizard keeps it as well.
        MaintenanceSetup.ApplySkipChoice(loaded, serverOs: true);
        Assert.Equal("both", loaded.SetupProfile);
        Assert.True(loaded.WindowsAutomation.AutoReboot.Enabled);
        Assert.Equal("outsideActiveHours", loaded.WindowsAutomation.AutoReboot.Mode);
        Assert.Equal((6, 19), (loaded.WindowsAutomation.AutoReboot.ActiveHoursStart, loaded.WindowsAutomation.AutoReboot.ActiveHoursEnd));
        Assert.Equal((Weekdays, "17:00"), (loaded.InstallDaysMask, loaded.InstallTime));
    }

    [Fact]
    public void Skip_WithNothingSaved_TakesServerDefaultsOnAServer_AndPrivateOnAClient()
    {
        var server = new UpdatePolicy();
        MaintenanceSetup.ApplySkipChoice(server, serverOs: true);
        Assert.True(server.SetupCompleted);
        AssertServerDefaults(server);

        var client = new UpdatePolicy();
        MaintenanceSetup.ApplySkipChoice(client, serverOs: false);
        Assert.True(client.SetupCompleted);
        Assert.Equal("private", client.SetupProfile);
        Assert.True(client.WindowsAutomation.AutoReboot.Enabled);
        Assert.Equal("confirm", client.WindowsAutomation.AutoReboot.Mode);
    }

    [Fact]
    public void Summary_SaysManualRestartForServer()
    {
        var policy = new UpdatePolicy();
        MaintenanceSetup.ApplyProfileDefaults(policy, "server");

        string english = MaintenanceSetup.SummaryText(policy, norwegian: false);
        Assert.Contains("Profile: server", english);
        Assert.Contains("Restart: manual restart", english);
        Assert.Contains("Defender signatures: continuous", english);

        string norwegian = MaintenanceSetup.SummaryText(policy, norwegian: true);
        Assert.Contains("Profil: server", norwegian);
        Assert.Contains("Omstart: manuell omstart", norwegian);
    }

    [Fact]
    public void TestSwitch_IsOnOnlyWhenTheVariableIsOne()
    {
        string? before = Environment.GetEnvironmentVariable(MaintenanceSetup.ServerTestSwitchVariable);
        try
        {
            Assert.Equal("UE_TEST_SERVER", MaintenanceSetup.ServerTestSwitchVariable);

            Environment.SetEnvironmentVariable(MaintenanceSetup.ServerTestSwitchVariable, null);
            Assert.False(MaintenanceSetup.ServerTestSwitchOn);
            Assert.Equal(WindowsProductType.IsServerOs, MaintenanceSetup.SetupTreatsThisMachineAsServer);

            Environment.SetEnvironmentVariable(MaintenanceSetup.ServerTestSwitchVariable, "0");
            Assert.False(MaintenanceSetup.ServerTestSwitchOn);

            Environment.SetEnvironmentVariable(MaintenanceSetup.ServerTestSwitchVariable, "1");
            Assert.True(MaintenanceSetup.ServerTestSwitchOn);
            Assert.True(MaintenanceSetup.SetupTreatsThisMachineAsServer);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MaintenanceSetup.ServerTestSwitchVariable, before);
        }
    }

    [Theory]
    [InlineData(false, "Private", "Business", "Both", "Server")]
    [InlineData(true, "Privat", "Bedrift", "Begge deler", "Server")]
    public void SetupWizard_OffersServerAsTheFourthChoice(bool norwegian, params string[] expected)
    {
        RunSta(() =>
        {
            var wizard = new SetupWizardWindow(null, new UpdatePolicy(), serverOs: false, serverTestSwitch: false, norwegian);
            Next(wizard);

            Assert.Equal(expected, Radios(wizard, "Profile").Select(r => (string)r.Content));
            Assert.Contains(MaintenanceSetup.ServerProfileTip(norwegian), Texts(wizard));
        });
    }

    [Theory]
    [InlineData(true, "", "Server")]
    [InlineData(false, "", "Private")]
    [InlineData(true, "both", "Both")]
    [InlineData(true, "private", "Private")]
    [InlineData(false, "server", "Server")]
    public void SetupWizard_StartsOnServerOnlyOnAServerWithNothingSaved(bool serverOs, string savedProfile, string expected)
    {
        RunSta(() =>
        {
            var wizard = new SetupWizardWindow(
                null, new UpdatePolicy { SetupProfile = savedProfile }, serverOs, serverTestSwitch: false, norwegian: false);
            Next(wizard);

            Assert.Equal(expected, Radios(wizard, "Profile").Single(r => r.IsChecked == true).Content);
        });
    }

    [Fact]
    public void SetupWizard_ServerChoice_LeadsToNoAutomaticRestart_AndAnotherChoiceCanStillBePicked()
    {
        RunSta(() =>
        {
            var wizard = new SetupWizardWindow(null, new UpdatePolicy(), serverOs: true, serverTestSwitch: false, norwegian: false);
            Next(wizard); // the choice
            Next(wizard); // active hours
            Assert.Equal(new[] { "8", "16" }, Descendants<TextBox>(wizard).Select(t => t.Text));
            Next(wizard); // install days
            Assert.Equal(
                new[] { true, true, true, true, true, false, false },
                Descendants<CheckBox>(wizard).Select(c => c.IsChecked == true));
            Assert.Equal("17:00", Descendants<TextBox>(wizard).Single().Text);
            Next(wizard); // restart

            var restart = Radios(wizard, "Reboot");
            Assert.Equal(3, restart.Count);
            Assert.Equal(new[] { false, false, true }, restart.Select(r => r.IsChecked == true));
            Assert.Contains("no auto-reboot", (string)restart[2].Content);
        });

        RunSta(() =>
        {
            var wizard = new SetupWizardWindow(null, new UpdatePolicy(), serverOs: true, serverTestSwitch: false, norwegian: false);
            Next(wizard);
            Radios(wizard, "Profile").Single(r => (string)r.Content == "Business").IsChecked = true;
            Next(wizard);
            Next(wizard);
            Next(wizard);

            Assert.Equal(new[] { false, true, false }, Radios(wizard, "Reboot").Select(r => r.IsChecked == true));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SetupDialogs_MarkTheTestSwitch_OnlyWhenItIsOn(bool norwegian)
    {
        string note = MaintenanceSetup.ServerTestSwitchNote(norwegian);
        Assert.StartsWith("TEST", note);
        Assert.Contains("UE_TEST_SERVER=1", note);

        RunSta(() =>
        {
            var marked = new SetupWizardWindow(null, new UpdatePolicy(), serverOs: true, serverTestSwitch: true, norwegian);
            Next(marked);
            Assert.Contains(note, Texts(marked));

            var plain = new SetupWizardWindow(null, new UpdatePolicy(), serverOs: true, serverTestSwitch: false, norwegian);
            Next(plain);
            Assert.DoesNotContain(note, Texts(plain));

            Assert.Contains(note, Texts(new InstallationOptionsWindow(false, serverOs: true, serverTestSwitch: true, norwegian: norwegian)));
            Assert.DoesNotContain(note, Texts(new InstallationOptionsWindow(false, serverOs: true, serverTestSwitch: false, norwegian: norwegian)));
        });
    }

    [Theory]
    [InlineData(false, "Private", "Business", "Both", "Server")]
    [InlineData(true, "Privat", "Bedrift", "Begge deler", "Server")]
    public void InstallOptions_OfferServerAsTheFourthChoice(bool norwegian, params string[] expected)
    {
        RunSta(() =>
        {
            var dialog = new InstallationOptionsWindow(false, serverOs: false, serverTestSwitch: false, norwegian: norwegian);

            Assert.Equal(expected, Radios(dialog, "InstallProfile").Select(r => (string)r.Content));
            Assert.Contains(MaintenanceSetup.ServerProfileTip(norwegian), Texts(dialog));
        });
    }

    [Fact]
    public void InstallOptions_FreshInstall_StartsOnServerWithoutRestartOnAServer_AndAsBeforeOnAClient()
    {
        RunSta(() =>
        {
            var server = new InstallationOptionsWindow(false, serverOs: true, serverTestSwitch: false, norwegian: false);
            Assert.Equal("server", server.SetupProfile);
            Assert.Equal("off", server.RebootChoice);
            Assert.Equal(Weekdays, server.InstallDaysMask);
            Assert.Equal("Server", Radios(server, "InstallProfile").Single(r => r.IsChecked == true).Content);

            var client = new InstallationOptionsWindow(false, serverOs: false, serverTestSwitch: false, norwegian: false);
            Assert.Equal("both", client.SetupProfile);
            Assert.Equal("outsideActiveHours", client.RebootChoice);
            Assert.Equal(Weekdays, client.InstallDaysMask);
            Assert.Equal("Both", Radios(client, "InstallProfile").Single(r => r.IsChecked == true).Content);
        });
    }

    [Fact]
    public void InstallOptions_OnAServer_AnotherChoiceCanStillBePicked()
    {
        RunSta(() =>
        {
            var dialog = new InstallationOptionsWindow(false, serverOs: true, serverTestSwitch: false, norwegian: false);
            Radios(dialog, "InstallProfile").Single(r => (string)r.Content == "Both").IsChecked = true;

            Assert.Equal("both", dialog.SetupProfile);
            Assert.Equal("outsideActiveHours", dialog.RebootChoice);

            Radios(dialog, "InstallProfile").Single(r => (string)r.Content == "Server").IsChecked = true;

            Assert.Equal("server", dialog.SetupProfile);
            Assert.Equal("off", dialog.RebootChoice);
        });
    }

    [Fact]
    public void InstallOptions_Repair_OnAServer_KeepsTheSavedChoiceAndRestartSetting()
    {
        RunSta(() =>
        {
            var saved = new UpdatePolicy();
            MaintenanceSetup.ApplyProfileDefaults(saved, "both");
            saved.InstallDaysMask = 1 << 5;
            saved.InstallTime = "03:00";
            saved.SetupCompleted = true;

            var dialog = new InstallationOptionsWindow(true, serverOs: true, serverTestSwitch: false, saved, norwegian: false);

            Assert.Equal("both", dialog.SetupProfile);
            Assert.Equal("outsideActiveHours", dialog.RebootChoice);
            Assert.Equal(1 << 5, dialog.InstallDaysMask);
            Assert.Equal("Both", Radios(dialog, "InstallProfile").Single(r => r.IsChecked == true).Content);
        });

        RunSta(() =>
        {
            // Never set up: Repair starts like a fresh install.
            var dialog = new InstallationOptionsWindow(true, serverOs: true, serverTestSwitch: false, new UpdatePolicy(), norwegian: false);

            Assert.Equal("server", dialog.SetupProfile);
            Assert.Equal("off", dialog.RebootChoice);
        });
    }

    private static void Next(SetupWizardWindow wizard)
    {
        // Footer buttons in the order they were added: Skip, Back, Next.
        var footer = Descendants<Button>(wizard).Where(b => b.Parent is Grid).ToList();
        Assert.Equal(3, footer.Count);
        Assert.NotEqual("Finish", footer[2].Content);
        Assert.NotEqual("Fullfør", footer[2].Content);
        footer[2].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static List<RadioButton> Radios(Window window, string group) =>
        Descendants<RadioButton>(window).Where(r => r.GroupName == group).ToList();

    private static List<string> Texts(Window window) =>
        Descendants<TextBlock>(window).Select(t => t.Text).ToList();

    /// <summary>The logical tree: these windows are inspected without being shown.</summary>
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject element) continue;
            if (element is T match) yield return match;
            foreach (var deeper in Descendants<T>(element)) yield return deeper;
        }
    }

    private static void RunSta(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
