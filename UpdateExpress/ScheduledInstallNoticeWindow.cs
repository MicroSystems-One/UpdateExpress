using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using UpdateExpress.Services;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace UpdateExpress;

/// <summary>
/// The "updates scheduled" notice in the look of the restart notice, with OK as its only
/// button. It behaves as the Windows message box it replaces: shown once and left alone, not
/// topmost, never forced to the foreground, closed by OK, Enter, Esc or the title-bar X, and
/// the caller waits until it is closed.
/// </summary>
internal sealed class ScheduledInstallNoticeWindow : Window
{
    private readonly ScheduledInstallNotice _notice;
    private readonly string _okCaption;
    private IntPtr _disabledOwner;
    private bool _centreOnPrimaryScreen;
    private bool _closed;

    internal ScheduledInstallNoticeWindow(ScheduledInstallNotice notice)
    {
        _notice = notice;
        _okCaption = AppLanguage.Ok;

        Title = notice.Title;
        NoticeLook.ApplyTo(this);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowActivated = true;

        // The large line never wraps: a long date makes it smaller instead.
        var when = NoticeLook.Highlight();
        when.Text = notice.When;
        Thickness whenMargin = when.Margin;
        when.Margin = default;
        var whenLine = new Viewbox
        {
            Child = when,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = WpfHorizontalAlignment.Left,
            Margin = whenMargin
        };

        var ok = NoticeLook.ChromeButton(_okCaption, primary: true);
        ok.IsDefault = true;
        ok.IsCancel = true;
        ok.Click += (_, _) => Close();

        var ready = NoticeLook.Strong(notice.Ready);
        ready.TextTrimming = TextTrimming.None;
        ready.TextWrapping = TextWrapping.Wrap;

        var tip = NoticeLook.Body(notice.Tip);
        var buttons = NoticeLook.ButtonRow(ok);
        if (notice.Restart is null)
        {
            Content = NoticeLook.Card(NoticeLook.Heading(notice.Title), ready, whenLine, tip, buttons);
        }
        else
        {
            var restart = NoticeLook.Body(notice.Restart);
            restart.Margin = new Thickness(0, 0, 0, 8);
            Content = NoticeLook.Card(NoticeLook.Heading(notice.Title), ready, whenLine, restart, tip, buttons);
        }

        Loaded += (_, _) => ok.Focus();
    }

    /// <summary>
    /// Show the notice and return when it is closed. Call on a UI thread. Like the message box
    /// before it, the notice is owned by this thread's active window, if there is one, and only
    /// that window waits for it; every other window stays usable.
    /// </summary>
    internal static void ShowAndWait(ScheduledInstallNotice notice) =>
        ShowAndWait(
            () => new ScheduledInstallNoticeWindow(notice),
            GetActiveWindow(),
            () => System.Media.SystemSounds.Asterisk.Play());

    internal static void ShowAndWait(Func<ScheduledInstallNoticeWindow> create, IntPtr owner, Action playSound)
    {
        var app = Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess()) app = null;
        Window? mainWindowBefore = app?.MainWindow;
        var window = create();
        // A message box never became the application's main window; neither does the notice,
        // so dialogs that open over the main window keep opening where they did.
        if (app is not null && mainWindowBefore is null && ReferenceEquals(app.MainWindow, window))
            app.MainWindow = null;

        var frame = new DispatcherFrame();
        window.Closed += (_, _) => frame.Continue = false;
        try
        {
            window.Present(owner, playSound);
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            window.ReleaseOwner();
            // Still open only when the dispatcher is shutting down.
            if (!window._closed)
            {
                try { window.Close(); } catch { /* the thread's windows are going away */ }
            }
        }
    }

    private void Present(IntPtr owner, Action playSound)
    {
        if (owner != IntPtr.Zero)
        {
            new WindowInteropHelper(this).Owner = owner;
            ShowInTaskbar = false;
            if (IsWindowEnabled(owner))
            {
                EnableWindow(owner, false);
                _disabledOwner = owner;
            }
        }
        else
        {
            ShowInTaskbar = true;
            // Windows centres an ownerless message box on the primary screen; WPF would pick
            // the screen the mouse is on.
            if (WindowStartupLocation == WindowStartupLocation.CenterScreen)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                _centreOnPrimaryScreen = true;
            }
        }

        try { playSound(); } catch { /* no sound device */ }
        Show();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (!_centreOnPrimaryScreen) return;
        WpfPoint corner = CentredIn(SystemParameters.WorkArea, new WpfSize(ActualWidth, ActualHeight));
        Left = corner.X;
        Top = corner.Y;
    }

    internal static WpfPoint CentredIn(Rect area, WpfSize size) =>
        new(area.Left + Math.Max(0, (area.Width - size.Width) / 2),
            area.Top + Math.Max(0, (area.Height - size.Height) / 2));

    protected override void OnKeyDown(WpfKeyEventArgs e)
    {
        // Ctrl+C copies a message box's text; keep that.
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            try { System.Windows.Clipboard.SetText(ClipboardText(_notice, _okCaption)); }
            catch { /* clipboard held by another program */ }
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    internal static string ClipboardText(ScheduledInstallNotice notice, string okCaption)
    {
        const string rule = "---------------------------";
        return string.Join("\r\n",
            rule, notice.Title, rule, notice.Body.Replace("\n", "\r\n"), rule, okCaption + "   ", rule, "");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;

        // As a message box does when it ends: the owner takes input again and, when the notice
        // was this thread's active window, becomes the active window in its place.
        IntPtr owner = _disabledOwner;
        IntPtr self = new WindowInteropHelper(this).Handle;
        bool wasActive = self != IntPtr.Zero && GetActiveWindow() == self;
        ReleaseOwner();
        if (wasActive && owner != IntPtr.Zero && IsWindow(owner)) SetActiveWindow(owner);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        ReleaseOwner();
        base.OnClosed(e);
    }

    private void ReleaseOwner()
    {
        IntPtr owner = _disabledOwner;
        if (owner == IntPtr.Zero) return;
        _disabledOwner = IntPtr.Zero;
        if (IsWindow(owner)) EnableWindow(owner, true);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr hWnd, bool enable);
}
