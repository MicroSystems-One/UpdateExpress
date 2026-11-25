using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using UpdateExpress.Services;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfVerticalAlignment = System.Windows.VerticalAlignment;

namespace UpdateExpress;

/// <summary>
/// Update Express restart notice: DEVICE\user, a live countdown to the restart, and
/// Stop | Utsett 15 minutter. The notice owns the countdown and restarts at 0, so Windows
/// shows no sign-out notice of its own. Topmost for its whole lifetime, ownerless (shows
/// with the main window minimised or closed), one per user session.
/// </summary>
internal sealed class RebootCountdownWindow : Window
{
    public enum Choice
    {
        /// <summary>Closed without a decision (app exiting); the pending entry stays for the next poller.</summary>
        None,
        Elapsed,
        Stop,
        Postpone,
        CancelledElsewhere
    }

    internal const int PostponeMinutes = 15;
    private const double ButtonHeight = 27;
    private const string SlotName = @"Local\MicroSystems.UpdateExpress.RebootNotice";
    private const string CancelName = @"Local\MicroSystems.UpdateExpress.RebootNoticeCancel";

    private static int _activeInProcess;

    public Choice Decision { get; private set; } = Choice.None;

    private readonly DispatcherTimer _timer;
    private readonly TextBlock _countdownText;
    private readonly EventWaitHandle? _cancelSignal;
    private readonly DateTime _deadlineUtc;
    private readonly string _countdownFormat = AppLanguage.T("Restart in {0}");
    private int _shownSeconds = -1;
    private bool _decided;

    private RebootCountdownWindow(int countdownSeconds, EventWaitHandle? cancelSignal)
    {
        _cancelSignal = cancelSignal;
        _deadlineUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, countdownSeconds));

        Title = AppLanguage.T("Update Express – Restart");
        AppIcon.Apply(this);
        Width = 520;
        MinWidth = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        ShowActivated = true;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));
        FontFamily = new WpfFontFamily("Segoe UI");

        var heading = new TextBlock
        {
            Text = AppLanguage.T("Restart coming soon"),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)),
            Margin = new Thickness(0, 0, 0, 6)
        };

        var identity = new TextBlock
        {
            Text = Environment.MachineName + "\\" + Environment.UserName,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 8)
        };

        _countdownText = new TextBlock
        {
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x67, 0xC0)),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var subText = new TextBlock
        {
            Text = AppLanguage.T("Save your work now. This PC restarts when the countdown reaches 0.")
                   + "\n" + AppLanguage.T("Stop cancels the restart. Postpone moves it 15 minutes out."),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x52, 0x61, 0x7A)),
            LineHeight = 20,
            Margin = new Thickness(0, 0, 0, 16)
        };

        // Neither button is default/cancel: the notice takes the foreground while the user may
        // be typing, and a stray Enter or Esc must not decide for them.
        var stop = MakeButton(AppLanguage.T("Stop"), primary: false);
        stop.Click += (_, _) => Finish(Choice.Stop);

        var postpone = MakeButton(AppLanguage.T("Postpone 15 minutes"), primary: true);
        postpone.Click += (_, _) => Finish(Choice.Postpone);

        var row = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            VerticalAlignment = WpfVerticalAlignment.Center,
            Height = ButtonHeight
        };
        row.Children.Add(stop);
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
                Children = { heading, identity, _countdownText, subText, row }
            }
        };

        UpdateCountdown();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => _timer.Start();
        ContentRendered += (_, _) => BringToFront(activate: true);
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        };
        Closed += (_, _) => _timer.Stop();
    }

    private void Tick()
    {
        if (_decided) return;
        if (_cancelSignal is not null && _cancelSignal.WaitOne(0))
        {
            Finish(Choice.CancelledElsewhere);
            return;
        }

        if (UpdateCountdown() <= 0)
            Finish(Choice.Elapsed);
    }

    private int UpdateCountdown()
    {
        int remaining = Math.Max(0, (int)Math.Ceiling((_deadlineUtc - DateTime.UtcNow).TotalSeconds));
        if (remaining != _shownSeconds)
        {
            _shownSeconds = remaining;
            _countdownText.Text = string.Format(_countdownFormat, $"{remaining / 60}:{remaining % 60:00}");
            // Once a second: back above any window that went topmost after this one.
            BringToFront(activate: false);
        }
        return remaining;
    }

    private void Finish(Choice choice)
    {
        if (_decided) return;
        _decided = true;
        Decision = choice;
        _timer.Stop();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The title-bar X and Alt+F4 are not a choice; the user picks Stop or Postpone.
        if (!_decided) e.Cancel = true;
        base.OnClosing(e);
    }

    private static Button MakeButton(string content, bool primary)
    {
        var b = AppDialog.CreateChromeButton(content, primary);
        b.Height = ButtonHeight;
        b.MinHeight = ButtonHeight;
        b.MaxHeight = ButtonHeight;
        b.MinWidth = 150;
        b.Padding = new Thickness(12, 0, 12, 0);
        b.Margin = new Thickness(4, 0, 4, 0);
        b.VerticalAlignment = WpfVerticalAlignment.Center;
        b.FontSize = 12;
        return b;
    }

    private void BringToFront(bool activate)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            // Disabled = another Update Express dialog is modal over this one; leave it on top.
            if (!activate && !IsWindowEnabled(hwnd)) return;

            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | (activate ? SwpShowWindow : SwpNoActivate));
            if (!activate) return;

            // Windows only lets the foreground thread hand over the foreground, so borrow its
            // input queue for the switch. Still refused when the foreground window is elevated.
            IntPtr foreground = GetForegroundWindow();
            uint foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
            uint thisThread = GetCurrentThreadId();
            bool attached = foregroundThread != 0 && foregroundThread != thisThread &&
                            AttachThreadInput(thisThread, foregroundThread, true);
            try
            {
                Activate();
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(thisThread, foregroundThread, false);
            }
        }
        catch (Exception ex)
        {
            AgentLog.Write("Restart notice could not be brought to front: " + ex.Message);
        }
    }

    /// <summary>Ask a live notice in this user session to close without restarting.</summary>
    internal static void SignalCancel()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(CancelName, out var signal))
            {
                using (signal) signal.Set();
            }
        }
        catch (Exception ex)
        {
            AgentLog.Write("Could not signal the restart notice: " + ex.Message);
        }
    }

    /// <summary>
    /// Show the notice and act on the outcome. Call on a UI thread. Returns false without
    /// showing when a notice is already live in this session or <paramref name="stillWanted"/>
    /// says the restart was taken over meanwhile.
    /// </summary>
    internal static bool RunWithUi(string source, int countdownSeconds, string comment, Func<bool>? stillWanted = null)
    {
        int seconds = Math.Clamp(countdownSeconds, 60, 3600);
        if (Interlocked.CompareExchange(ref _activeInProcess, 1, 0) != 0) return false;

        Mutex? slot = null;
        bool owned = false;
        EventWaitHandle? cancelSignal = null;
        try
        {
            try
            {
                slot = new Mutex(false, SlotName);
                try { owned = slot.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }
            }
            catch (UnauthorizedAccessException)
            {
                // Held by a notice in an elevated Update Express process.
            }

            if (!owned)
            {
                AgentLog.Write($"Restart notice for {source} skipped: one is already showing in this session.");
                return false;
            }

            if (stillWanted is not null && !stillWanted()) return false;

            try
            {
                cancelSignal = new EventWaitHandle(false, EventResetMode.ManualReset, CancelName);
                cancelSignal.Reset();
            }
            catch (Exception ex)
            {
                AgentLog.Write("Restart notice cancel signal unavailable: " + ex.Message);
            }

            // Keep the restart on disk while the notice counts down: if this process dies, the
            // tray agent or the service still completes it at the deadline.
            DateTime restartAt = DateTime.Now.AddSeconds(seconds);
            RebootCoordinator.SchedulePending(source, restartAt, "Restart notice counting down.", restartAt, log: false);
            AgentLog.Write($"Starting reboot countdown ({seconds}s) for {source} in the Update Express notice.");
            ActivityLog.TryWrite("AutoReboot", "CountdownStarted", new { source, seconds, notice = true }, mode: "Automatic", source: "RebootCoordinator");

            var dlg = new RebootCountdownWindow(seconds, cancelSignal);
            dlg.ShowDialog();
            Apply(dlg.Decision, source, seconds, comment, restartAt);
            return true;
        }
        finally
        {
            cancelSignal?.Dispose();
            if (owned)
            {
                try { slot!.ReleaseMutex(); } catch { /* not held on this thread */ }
            }
            slot?.Dispose();
            Interlocked.Exchange(ref _activeInProcess, 0);
        }
    }

    private static void Apply(Choice decision, string source, int seconds, string comment, DateTime restartAt)
    {
        try
        {
            switch (decision)
            {
                case Choice.Stop:
                    RebootCoordinator.ClearPending();
                    RebootCoordinator.AbortWindowsCountdown("user pressed Stop in the restart notice");
                    break;

                case Choice.Postpone:
                    // Restart exactly 15 minutes from now; the notice reopens for the last part.
                    DateTime postponedTo = DateTime.Now.AddMinutes(PostponeMinutes);
                    int lead = Math.Min(seconds, PostponeMinutes * 60);
                    RebootCoordinator.AbortWindowsCountdown("user postponed 15 minutes in the restart notice");
                    RebootCoordinator.SchedulePending(
                        source,
                        postponedTo.AddSeconds(-lead),
                        "Postponed 15 minutes from the restart notice.",
                        postponedTo);
                    break;

                case Choice.Elapsed:
                    if (!string.Equals(source, "Applications", StringComparison.OrdinalIgnoreCase) &&
                        ApplicationAutomation.ShouldHoldExternalReboot())
                    {
                        DateTime slip = DateTime.Now.AddMinutes(10);
                        RebootCoordinator.SchedulePending(
                            source,
                            slip,
                            "Countdown finished while application installs were running.",
                            slip.AddSeconds(Math.Min(seconds, 300)));
                        break;
                    }
                    RebootCoordinator.ClearPending();
                    RebootCoordinator.RestartNow(source, comment);
                    break;

                case Choice.CancelledElsewhere:
                    // The canceller may already have rescheduled; only drop this notice's own entry.
                    RebootCoordinator.ClearPendingIfRestartAt(restartAt);
                    AgentLog.Write($"Restart notice for {source} closed: countdown cancelled by Update Express.");
                    break;
            }
        }
        catch (Exception ex)
        {
            AgentLog.Write($"Restart notice outcome {decision} for {source} failed: {ex.Message}");
        }
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
