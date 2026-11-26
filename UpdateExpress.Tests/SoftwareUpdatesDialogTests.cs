using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using UpdateExpress.Services;
using Xunit;

namespace UpdateExpress.Tests;

public class SoftwareUpdatesDialogTests
{
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(20);

    [Fact]
    public void LeftAlone_ClosesItselfAsLater()
    {
        RunSta(() =>
        {
            bool failsafeFired = false;
            bool shown = SoftwareUpdatesDialog.TryShowModal(
                () => NewDialog(TimeSpan.FromMilliseconds(300), () => failsafeFired = true),
                out var decision);

            Assert.True(shown);
            Assert.False(failsafeFired, "The dialog did not close itself.");
            Assert.Equal(SoftwareUpdatesDialog.PromptDecision.Later, decision);
            Assert.False(SoftwareUpdatesDialog.IsShowing);
        });
    }

    [Fact]
    public void SecondPromptWhileOneIsOpen_IsRefused()
    {
        RunSta(() =>
        {
            bool failsafeFired = false;
            bool? showingWhenSecondAsked = null;
            bool? secondShown = null;
            bool secondCreated = false;

            bool shown = SoftwareUpdatesDialog.TryShowModal(
                () =>
                {
                    var first = NewDialog(TimeSpan.FromMilliseconds(600), () => failsafeFired = true);
                    // Queued the way the automations queue a prompt; runs inside the first dialog's modal loop.
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
                    {
                        showingWhenSecondAsked = SoftwareUpdatesDialog.IsShowing;
                        secondShown = SoftwareUpdatesDialog.TryShowModal(
                            () =>
                            {
                                secondCreated = true;
                                return NewDialog(TimeSpan.FromMilliseconds(100), () => failsafeFired = true);
                            },
                            out _);
                    });
                    return first;
                },
                out _);

            Assert.True(shown);
            Assert.False(failsafeFired, "A dialog did not close itself.");
            Assert.True(showingWhenSecondAsked);
            Assert.False(secondShown);
            Assert.False(secondCreated);
            Assert.False(SoftwareUpdatesDialog.IsShowing);
        });
    }

    [Fact]
    public void AfterADialogClosedOrFailedToOpen_TheNextPromptIsShown()
    {
        RunSta(() =>
        {
            Assert.Throws<InvalidOperationException>(() =>
                SoftwareUpdatesDialog.TryShowModal(
                    () => throw new InvalidOperationException("window could not be created"),
                    out _));
            Assert.False(SoftwareUpdatesDialog.IsShowing);

            for (int i = 0; i < 2; i++)
            {
                bool failsafeFired = false;
                bool shown = SoftwareUpdatesDialog.TryShowModal(
                    () => NewDialog(TimeSpan.FromMilliseconds(150), () => failsafeFired = true),
                    out _);
                Assert.True(shown);
                Assert.False(failsafeFired, "The dialog did not close itself.");
            }
        });
    }

    private static SoftwareUpdatesDialog NewDialog(TimeSpan autoClose, Action onFailsafe)
    {
        var updates = new List<PromptUpdateItem>
        {
            new() { UpdateId = "uid-1", KB = "KB5000001", Title = "Test update", Priority = "Important" }
        };
        var dialog = new SoftwareUpdatesDialog("Applications", updates, "17:00", downloaded: false, autoClose)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false,
            ShowActivated = false
        };

        var failsafe = new DispatcherTimer { Interval = Failsafe };
        failsafe.Tick += (_, _) =>
        {
            failsafe.Stop();
            onFailsafe();
            dialog.Close();
        };
        dialog.Loaded += (_, _) => failsafe.Start();
        dialog.Closed += (_, _) => failsafe.Stop();
        return dialog;
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
