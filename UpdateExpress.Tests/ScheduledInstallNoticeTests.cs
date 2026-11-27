using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using UpdateExpress.Services;
using Xunit;

namespace UpdateExpress.Tests;

public class ScheduledInstallNoticeTextTests
{
    private static readonly DateTime Today = new(2026, 11, 27);
    private static readonly DateTime Tonight = new(2026, 11, 27, 20, 0, 0);
    private static readonly DateTime Monday = new(2026, 11, 30, 17, 0, 0);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Title_depends_on_whether_installation_is_today(bool today, bool norwegian, bool autoReboot)
    {
        DateTime when = today ? Tonight : Monday;
        var notice = MaintenanceSetup.ScheduledNotice(when, Today, norwegian, autoReboot);
        if (today)
            Assert.Equal(MaintenanceSetup.EveningNoticeTitle(norwegian), notice.Title);
        else
            Assert.Equal(norwegian ? "Oppdateringer planlagt" : "Updates scheduled", notice.Title);
    }

    [Fact]
    public void Another_day_in_English_uses_the_app_language_date()
    {
        WithCulture("nb-NO", () =>
        {
            var notice = MaintenanceSetup.ScheduledNotice(Monday, Today, norwegian: false, autoReboot: true);
            Assert.Equal("Updates scheduled", notice.Title);
            Assert.Equal("Installation is set for Mon 30 Nov at 17:00.", notice.When);
            Assert.Equal(
                "New Windows updates are downloaded and ready.\n" +
                "Installation is set for Mon 30 Nov at 17:00.\n\n" +
                "Tip: Save open files before the PC installs and possibly restarts.",
                notice.Body);
        });
    }

    [Fact]
    public void Another_day_in_Norwegian_uses_the_app_language_date()
    {
        WithCulture("en-US", () =>
        {
            var notice = MaintenanceSetup.ScheduledNotice(Monday, Today, norwegian: true, autoReboot: false);
            Assert.Equal("Oppdateringer planlagt", notice.Title);
            Assert.StartsWith("Installasjon er satt til man.", notice.When, StringComparison.Ordinal);
            Assert.Contains("kl. 17:00", notice.When, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Today_keeps_the_restart_sentence_apart_from_the_time()
    {
        var notice = MaintenanceSetup.ScheduledNotice(Tonight, Today, norwegian: true, autoReboot: false);
        Assert.Equal("Oppdateringer planlagt i kveld", notice.Title);
        Assert.Equal("Installasjon er satt til i dag kl. 20:00.", notice.When);
        Assert.Equal("Hvis Windows krever omstart, får du spørsmål før PC-en restarter.", notice.Restart);
    }

    [Fact]
    public void Ctrl_C_text_is_laid_out_as_a_message_box_copies_itself()
    {
        var notice = new ScheduledInstallNotice("Title", "Ready.", "When.", null, "Tip.");
        Assert.Equal(
            "---------------------------\r\nTitle\r\n---------------------------\r\n" +
            "Ready.\r\nWhen.\r\n\r\nTip.\r\n" +
            "---------------------------\r\nOK   \r\n---------------------------\r\n",
            ScheduledInstallNoticeWindow.ClipboardText(notice, "OK"));
    }

    [Fact]
    public void An_ownerless_notice_is_centred_in_the_given_area()
    {
        var corner = OkNoticeShellWindow.CentredIn(new Rect(0, 0, 1280, 752), new Size(560, 286));
        Assert.Equal(360, corner.X);
        Assert.Equal(233, corner.Y);

        var clamped = OkNoticeShellWindow.CentredIn(new Rect(10, 20, 400, 200), new Size(560, 286));
        Assert.Equal(10, clamped.X);
        Assert.Equal(20, clamped.Y);
    }

    private static void WithCulture(string name, Action body)
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        try { body(); }
        finally { CultureInfo.CurrentCulture = before; }
    }
}

public class DeferredRestartNoticeTests
{
    private static readonly DateTime When = new(2026, 11, 30, 17, 0, 0);

    [Fact]
    public void English_matches_the_message_box_words_with_the_app_language_date()
    {
        var reboot = new AutoRebootPolicy { ActiveHoursStart = 7, ActiveHoursEnd = 18, CountdownSeconds = 300 };
        var notice = RebootCoordinator.DeferredRestartNotice(When, reboot, norwegian: false);
        Assert.Equal("Update Express – restart scheduled", notice.Title);
        Assert.Equal("A restart is required.", notice.Lead);
        Assert.Equal(
            "Auto-reboot is set to outside active hours (07:00–18:00).\n" +
            "Update Express will offer/restart around Mon 30 Nov at 17:00 (countdown 300s).",
            notice.Detail);
    }

    [Fact]
    public void Norwegian_uses_the_app_language_date()
    {
        var reboot = new AutoRebootPolicy { ActiveHoursStart = 7, ActiveHoursEnd = 18, CountdownSeconds = 300 };
        var notice = RebootCoordinator.DeferredRestartNotice(When, reboot, norwegian: true);
        Assert.Equal("Update Express – omstart planlagt", notice.Title);
        Assert.Contains("man.", notice.Detail, StringComparison.Ordinal);
        Assert.Contains("kl. 17:00", notice.Detail, StringComparison.Ordinal);
    }
}

public class ScheduledInstallNoticeWindowTests
{
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(20);

    private static readonly ScheduledInstallNotice OtherDay = new(
        "Updates scheduled",
        "New Windows updates are downloaded and ready.",
        "Installation is set for Mon 30 Nov at 17:00.",
        null,
        "Tip: Save open files before the PC installs and possibly restarts.");

    private static readonly ScheduledInstallNotice TodayNotice = new(
        "Oppdateringer planlagt i kveld",
        "Nye Windows-oppdateringer er lastet ned og klare.",
        "Installasjon er satt til i dag kl. 20:00.",
        "Hvis Windows krever omstart, får du spørsmål før PC-en restarter.",
        "Tips: Lagre åpne dokumenter og lukk viktige programmer før du går fra PC-en, så du ikke mister ulagret arbeid.");

    private static readonly InformationNotice RestartScheduled = new(
        "Update Express – restart scheduled",
        "A restart is required.",
        "Auto-reboot is set to outside active hours (07:00–18:00).\n" +
        "Update Express will offer/restart around Mon 30 Nov at 17:00 (countdown 300s).");

    [Fact]
    public void Shows_every_part_of_the_text_once_under_the_same_title()
    {
        RunSta(() =>
        {
            foreach (var notice in new[] { OtherDay, TodayNotice })
            {
                var window = new ScheduledInstallNoticeWindow(notice);
                Assert.Equal(notice.Title, window.Title);
                var texts = Descendants<TextBlock>(window).Select(t => t.Text).ToList();
                var expected = new List<string> { notice.Title, notice.Ready, notice.When };
                if (notice.Restart is not null) expected.Add(notice.Restart);
                expected.Add(notice.Tip);
                Assert.Equal(expected, texts);
                window.Close();
            }
        });
    }

    [Fact]
    public void Information_notice_uses_the_same_look_and_OK()
    {
        RunSta(() =>
        {
            var notice = new InformationNoticeWindow(RestartScheduled);
            Assert.Equal(RestartScheduled.Title, notice.Title);
            Assert.False(notice.Topmost);
            var ok = Assert.Single(Descendants<Button>(notice));
            Assert.Equal("OK", ok.Content);
            Assert.True(ok.IsDefault);
            Assert.True(ok.IsCancel);
            Assert.Equal(Color.FromRgb(0x00, 0x67, 0xC0), ((SolidColorBrush)ok.Background).Color);
            notice.Close();
        });
    }

    [Fact]
    public void Has_OK_as_its_only_button_blue_default_and_cancel()
    {
        RunSta(() =>
        {
            var window = new ScheduledInstallNoticeWindow(OtherDay);
            var ok = Assert.Single(Descendants<Button>(window));
            Assert.Equal("OK", ok.Content);
            Assert.True(ok.IsDefault);
            Assert.True(ok.IsCancel);
            Assert.Equal(Color.FromRgb(0x00, 0x67, 0xC0), ((SolidColorBrush)ok.Background).Color);
            Assert.Equal(Colors.White, ((SolidColorBrush)ok.Foreground).Color);
            window.Close();
        });
    }

    [Fact]
    public void Is_not_topmost_and_can_be_closed_from_the_title_bar()
    {
        RunSta(() =>
        {
            var window = new ScheduledInstallNoticeWindow(OtherDay);
            Assert.False(window.Topmost);
            Assert.True(window.ShowActivated);
            Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
            bool failsafeFired = false;
            ShowOffScreen(OtherDay, IntPtr.Zero, () => failsafeFired = true, shown => shown.Close());
            Assert.False(failsafeFired, "Closing the window did not end the wait.");
        });
    }

    [Fact]
    public void Looks_like_the_restart_notice()
    {
        RunSta(() =>
        {
            var restart = (Window)typeof(RebootCountdownWindow)
                .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, new[] { typeof(int), typeof(EventWaitHandle) })!
                .Invoke(new object?[] { 600, null });
            var notice = new ScheduledInstallNoticeWindow(TodayNotice);
            Assert.Equal(restart.Width, notice.Width);
            Assert.Same(restart.Icon, notice.Icon);
            Assert.True(restart.Topmost);
            Assert.False(notice.Topmost);
            notice.Close();
            typeof(RebootCountdownWindow).GetField("_decided", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(restart, true);
            restart.Close();
        });
    }

    [Fact]
    public void The_caller_waits_until_OK_and_the_sound_plays_once()
    {
        RunSta(() =>
        {
            bool failsafeFired = false;
            int sounds = 0;
            ShowOffScreen(OtherDay, IntPtr.Zero, () => failsafeFired = true, PressOk, () => sounds++);
            Assert.False(failsafeFired);
            Assert.Equal(1, sounds);
        });
    }

    [Fact]
    public void Only_the_owner_waits_for_the_notice()
    {
        RunSta(() =>
        {
            var owner = OffScreenWindow("owner");
            var other = OffScreenWindow("other");
            owner.Show();
            other.Show();
            IntPtr ownerHandle = new WindowInteropHelper(owner).Handle;
            IntPtr otherHandle = new WindowInteropHelper(other).Handle;
            try
            {
                bool failsafeFired = false;
                bool? ownerEnabledWhileShown = null, otherEnabledWhileShown = null;
                ShowOffScreen(OtherDay, ownerHandle, () => failsafeFired = true, window =>
                {
                    ownerEnabledWhileShown = IsWindowEnabled(ownerHandle);
                    otherEnabledWhileShown = IsWindowEnabled(otherHandle);
                    PressOk(window);
                });
                Assert.False(failsafeFired);
                Assert.False(ownerEnabledWhileShown);
                Assert.True(otherEnabledWhileShown);
                Assert.True(IsWindowEnabled(ownerHandle));
            }
            finally
            {
                other.Close();
                owner.Close();
            }
        });
    }

    [Fact]
    public void An_owner_that_goes_away_takes_the_notice_with_it_and_ends_the_wait()
    {
        RunSta(() =>
        {
            var owner = OffScreenWindow("owner");
            owner.Show();
            IntPtr ownerHandle = new WindowInteropHelper(owner).Handle;
            bool failsafeFired = false;
            ScheduledInstallNoticeWindow? shown = null;
            ShowOffScreen(OtherDay, ownerHandle, () => failsafeFired = true, w => { shown = w; owner.Close(); });
            Assert.False(failsafeFired);
            Assert.NotNull(shown);
            Assert.False(shown!.IsVisible);
        });
    }

    [Fact]
    public void An_owner_that_was_already_waiting_is_left_as_it_was()
    {
        RunSta(() =>
        {
            var owner = OffScreenWindow("owner");
            owner.Show();
            IntPtr ownerHandle = new WindowInteropHelper(owner).Handle;
            EnableWindow(ownerHandle, false);
            try
            {
                bool failsafeFired = false;
                ShowOffScreen(OtherDay, ownerHandle, () => failsafeFired = true, PressOk);
                Assert.False(failsafeFired);
                Assert.False(IsWindowEnabled(ownerHandle));
            }
            finally
            {
                EnableWindow(ownerHandle, true);
                owner.Close();
            }
        });
    }

    private static void ShowOffScreen(
        ScheduledInstallNotice notice, IntPtr owner, Action onFailsafe, Action<ScheduledInstallNoticeWindow> whenShown,
        Action? sound = null)
    {
        ScheduledInstallNoticeWindow.ShowAndWait(
            () =>
            {
                var window = new ScheduledInstallNoticeWindow(notice)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                    ShowActivated = false
                };
                var failsafe = new DispatcherTimer { Interval = Failsafe };
                failsafe.Tick += (_, _) => { failsafe.Stop(); onFailsafe(); window.Close(); };
                window.Loaded += (_, _) =>
                {
                    failsafe.Start();
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => whenShown(window));
                };
                window.Closed += (_, _) => failsafe.Stop();
                return window;
            },
            owner,
            sound ?? (() => { }));
    }

    private static Window OffScreenWindow(string title) => new()
    {
        Title = title,
        Width = 200,
        Height = 100,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -32000,
        Top = -32000,
        ShowInTaskbar = false,
        ShowActivated = false
    };

    private static void PressOk(Window window) =>
        ((IInvokeProvider)new ButtonAutomationPeer(Descendants<Button>(window).Single()).GetPattern(PatternInterface.Invoke)).Invoke();

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject element) continue;
            if (element is T match) yield return match;
            foreach (T deeper in Descendants<T>(element)) yield return deeper;
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

    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr hWnd, bool enable);
}