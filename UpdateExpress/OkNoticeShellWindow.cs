using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using UpdateExpress.Services;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace UpdateExpress;

/// <summary>
/// Shared message-box-style behaviour for OK-only notice windows: owner disables, primary-screen
/// centre when ownerless, modal wait via <see cref="DispatcherFrame"/>, not topmost.
/// </summary>
internal abstract class OkNoticeShellWindow : Window
{
    private IntPtr _disabledOwner;
    private bool _centreOnPrimaryScreen;
    private bool _closed;

    protected void InitNoticeShell(string title)
    {
        Title = title;
        NoticeLook.ApplyTo(this);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowActivated = true;
    }

    protected static IntPtr OwnerFrom(Window? wpfOwner)
    {
        if (wpfOwner is not null)
        {
            if (wpfOwner is OkNoticeShellWindow) return new WindowInteropHelper(wpfOwner).Handle;
            try { return new WindowInteropHelper(wpfOwner).Handle; }
            catch { return IntPtr.Zero; }
        }
        return GetActiveWindow();
    }

    protected static void ShowAndWait(Func<OkNoticeShellWindow> create, IntPtr owner, Action playSound)
    {
        var app = Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess()) app = null;
        Window? mainWindowBefore = app?.MainWindow;
        var window = create();
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
            if (!window._closed)
            {
                try { window.Close(); } catch { /* dispatcher shutting down */ }
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

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;

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

    protected static string ClipboardText(string title, string body, string okCaption)
    {
        const string rule = "---------------------------";
        return string.Join("\r\n",
            rule, title, rule, body.Replace("\n", "\r\n"), rule, okCaption + "   ", rule, "");
    }

    [DllImport("user32.dll")]
    protected static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr hWnd, bool enable);
}