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

/// <summary>
/// The scheduled-install notice says what the message box before it said, word for word.
/// </summary>
public class ScheduledInstallNoticeTextTests
{
    private static readonly DateTime Today = new(2026, 11, 27);
    private static readonly DateTime Tonight = new(2026, 11, 27, 20, 0, 0);
    private static readonly DateTime Monday = new(2026, 11, 30, 17, 0, 0);

    /// <summary>The title and body exactly as the code built them for the message box.</summary>
    private static (string Title, string Body) MessageBoxText(DateTime when, DateTime today, bool norwegian, bool autoReboot)
    {
        string title = norwegian ? "Oppdateringer planlagt i kveld" : "Updates scheduled for this evening";
        if (when.Date != today.Date)
        {
            return (title, norwegian
                ? $"Nye Windows-oppdateringer er lastet ned og klare.\nInstallasjon er satt til {when:ddd dd.MM} kl. {when:HH:mm}.\n\nTips: Lagre åpne dokumenter før PC-en installerer og eventuelt restarter."
                : $"New Windows updates are downloaded and ready.\nInstallation is set for {when:ddd dd MMM} at {when:HH:mm}.\n\nTip: Save open files before the PC installs and possibly restarts.");
        }

        string clock = when.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (norwegian)
        {
            string reboot = autoReboot
                ? "Hvis Windows krever omstart, skjer det automatisk etter en nedtelling (du kan utsette)."
                : "Hvis Windows krever omstart, får du spørsmål før PC-en restarter.";
            return (title,
                $"Nye Windows-oppdateringer er lastet ned og klare.\n" +
                $"Installasjon er satt til i dag kl. {clock}. {reboot}\n\n" +
                "Tips: Lagre åpne dokumenter og lukk viktige programmer før du går fra PC-en, så du ikke mister ulagret arbeid.");
        }

        string rebootEn = autoReboot
            ? "If Windows requires a restart, it will happen automatically after a countdown (you can postpone)."
            : "If Windows requires a restart, you will be asked before the PC restarts.";
        return (title,
            $"New Windows updates are downloaded and ready.\n" +
            $"Installation is set for today at {clock}. {rebootEn}\n\n" +
            "Tip: Save open files and close important apps before you leave the PC, so you don’t lose unsaved work.");
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Title_and_body_are_the_message_box_text(bool today, bool norwegian, bool autoReboot)
    {
        DateTime when = today ? Tonight : Monday;
        foreach (string culture in new[] { "nb-NO", "en-US", "fi-FI" })
        {
            WithCulture(culture, () =>
            {
                var (title, body) = MessageBoxText(when, Today, norwegian, autoReboot);
                var notice = MaintenanceSetup.ScheduledNotice(when, Today, norwegian, autoReboot);
                Assert.Equal(title, notice.Title);
                Assert.Equal(body, notice.Body);
            });
        }
    }

    [Fact]
    public void Another_day_in_English_reads_as_in_the_reported_message_box()
    {
        WithCulture("en-US", () =>
        {
            var notice = MaintenanceSetup.ScheduledNotice(Monday, Today, norwegian: false, autoReboot: true);
            Assert.Equal("Updates scheduled for this evening", notice.Title);
            Assert.Equal(
                "New Windows updates are downloaded and ready.\nInstallation is set for Mon 30 Nov at 17:00.\n\n" +
                "Tip: Save open files before the PC installs and possibly restarts.",
                notice.Body);
            Assert.Equal("New Windows updates are downloaded and ready.", notice.Ready);
            Assert.Equal("Installation is set for Mon 30 Nov at 17:00.", notice.When);
            Assert.Null(notice.Restart);
            Assert.Equal("Tip: Save open files before the PC installs and possibly restarts.", notice.Tip);
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
        var corner = ScheduledInstallNoticeWindow.CentredIn(new Rect(0, 0, 1280, 752), new Size(560, 286));
        Assert.Equal(360, corner.X);
        Assert.Equal(233, corner.Y);

        // Larger than the area: kept at the area's corner, never pushed off it.
        var clamped = ScheduledInstallNoticeWindow.CentredIn(new Rect(10, 20, 400, 200), new Size(560, 286));
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

/// <summary>
/// The notice window: the restart notice's look, and the message box's behaviour. Windows are
/// shown off-screen and never activated, so a test run does not disturb the desktop.
/// </summary>
public class ScheduledInstallNoticeWindowTests
{
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(20);

    private static readonly ScheduledInstallNotice OtherDay = new(
        "Updates scheduled for this evening",
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
            Assert.Equal(restart.MinWidth, notice.MinWidth);
            Assert.Equal(restart.SizeToContent, notice.SizeToContent);
            Assert.Equal(restart.ResizeMode, notice.ResizeMode);
            Assert.Equal(restart.FontFamily.Source, notice.FontFamily.Source);
            Assert.Equal(Rgb(restart.Background), Rgb(notice.Background));
            Assert.Same(restart.Icon, notice.Icon);
            Assert.NotNull(notice.Icon);

            var restartCard = (Border)restart.Content;
            var noticeCard = (Border)notice.Content;
            Assert.Equal(restartCard.Margin, noticeCard.Margin);
            Assert.Equal(restartCard.Padding, noticeCard.Padding);
            Assert.Equal(restartCard.CornerRadius, noticeCard.CornerRadius);
            Assert.Equal(restartCard.BorderThickness, noticeCard.BorderThickness);
            Assert.Equal(Rgb(restartCard.BorderBrush), Rgb(noticeCard.BorderBrush));
            Assert.Equal(Rgb(restartCard.Background), Rgb(noticeCard.Background));

            // Heading, bold line, large blue line, grey text: the same type in the same order.
            var restartTexts = Descendants<TextBlock>(restart).ToList();
            var noticeTexts = Descendants<TextBlock>(notice).ToList();
            Assert.Equal(4, restartTexts.Count);
            Assert.Equal(5, noticeTexts.Count);
            for (int i = 0; i < 3; i++) AssertSameType(restartTexts[i], noticeTexts[i]);
            AssertSameType(restartTexts[3], noticeTexts[3]);
            AssertSameType(restartTexts[3], noticeTexts[4]);
            Assert.Equal(restartTexts[3].Margin, noticeTexts[4].Margin);

            var restartButtons = Descendants<Button>(restart).ToList();
            var ok = Assert.Single(Descendants<Button>(notice));
            var primary = restartButtons.Single(b => Rgb(b.Background) == Color.FromRgb(0x00, 0x67, 0xC0));
            Assert.Equal(primary.Height, ok.Height);
            Assert.Equal(primary.MinWidth, ok.MinWidth);
            Assert.Equal(primary.Padding, ok.Padding);
            Assert.Equal(primary.Margin, ok.Margin);
            Assert.Equal(primary.FontSize, ok.FontSize);
            Assert.Equal(primary.FontWeight, ok.FontWeight);
            Assert.Equal(Rgb(primary.Background), Rgb(ok.Background));
            Assert.Equal(Rgb(primary.Foreground), Rgb(ok.Foreground));
            Assert.Equal(Rgb(primary.BorderBrush), Rgb(ok.BorderBrush));

            var restartRow = (StackPanel)restartButtons[0].Parent;
            var noticeRow = (StackPanel)ok.Parent;
            Assert.Equal(restartRow.HorizontalAlignment, noticeRow.HorizontalAlignment);
            Assert.Equal(restartRow.Height, noticeRow.Height);

            // Behaviour stays each window's own.
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
            bool closedWhenAsked = true;
            bool? inTaskbar = null;
            int sounds = 0;
            bool dispatcherRanMeanwhile = false;

            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => dispatcherRanMeanwhile = true);
            ShowOffScreen(OtherDay, IntPtr.Zero, () => failsafeFired = true, shown =>
            {
                closedWhenAsked = !shown.IsVisible;
                inTaskbar = shown.ShowInTaskbar;
                Assert.Null(shown.Owner);
                Assert.Equal(IntPtr.Zero, new WindowInteropHelper(shown).Owner);
                PressOk(shown);
            }, () => sounds++);

            Assert.False(failsafeFired, "OK did not close the notice.");
            Assert.False(closedWhenAsked);
            Assert.True(dispatcherRanMeanwhile);
            Assert.True(inTaskbar);
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
                bool? ownerEnabledWhileShown = null, otherEnabledWhileShown = null, inTaskbar = null;
                IntPtr ownedBy = IntPtr.Zero;

                ShowOffScreen(OtherDay, ownerHandle, () => failsafeFired = true, shown =>
                {
                    ownerEnabledWhileShown = IsWindowEnabled(ownerHandle);
                    otherEnabledWhileShown = IsWindowEnabled(otherHandle);
                    inTaskbar = shown.ShowInTaskbar;
                    ownedBy = GetWindow(new WindowInteropHelper(shown).Handle, 4 /* GW_OWNER */);
                    PressOk(shown);
                });

                Assert.False(failsafeFired, "OK did not close the notice.");
                Assert.Equal(ownerHandle, ownedBy);
                Assert.False(ownerEnabledWhileShown);
                Assert.True(otherEnabledWhileShown);
                Assert.False(inTaskbar);
                Assert.True(IsWindowEnabled(ownerHandle));
                Assert.True(IsWindowEnabled(otherHandle));
            }
            finally
            {
                other.Close();
                owner.Close();
            }
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
                Assert.False(failsafeFired, "OK did not close the notice.");
                Assert.False(IsWindowEnabled(ownerHandle));
            }
            finally
            {
                EnableWindow(ownerHandle, true);
                owner.Close();
            }
        });
    }

    [Fact]
    public void A_notice_that_cannot_be_shown_gives_the_owner_back()
    {
        RunSta(() =>
        {
            var owner = OffScreenWindow("owner");
            owner.Show();
            IntPtr ownerHandle = new WindowInteropHelper(owner).Handle;
            try
            {
                Assert.Throws<InvalidOperationException>(() =>
                    ScheduledInstallNoticeWindow.ShowAndWait(
                        () =>
                        {
                            var window = new ScheduledInstallNoticeWindow(OtherDay);
                            window.SourceInitialized += (_, _) => throw new InvalidOperationException("window could not be shown");
                            return window;
                        },
                        ownerHandle,
                        () => { }));
                Assert.True(IsWindowEnabled(ownerHandle));
            }
            finally
            {
                owner.Close();
            }
        });
    }

    private static void ShowOffScreen(
        ScheduledInstallNotice notice, IntPtr owner, Action onFailsafe, Action<ScheduledInstallNoticeWindow> whenShown, Action? sound = null)
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
                failsafe.Tick += (_, _) =>
                {
                    failsafe.Stop();
                    onFailsafe();
                    window.Close();
                };
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

    private static void PressOk(Window window)
    {
        var ok = Descendants<Button>(window).Single();
        ((IInvokeProvider)new ButtonAutomationPeer(ok).GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static void AssertSameType(TextBlock expected, TextBlock actual)
    {
        Assert.Equal(expected.FontSize, actual.FontSize);
        Assert.Equal(expected.FontWeight, actual.FontWeight);
        Assert.Equal(Rgb(expected.Foreground), Rgb(actual.Foreground));
        Assert.Equal(expected.LineHeight, actual.LineHeight);
    }

    private static Color Rgb(Brush brush) => ((SolidColorBrush)brush).Color;

    /// <summary>Elements in reading order, from the logical tree so that no window has to be shown.</summary>
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

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr hWnd, bool enable);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
}
