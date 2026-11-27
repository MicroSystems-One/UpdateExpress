using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UpdateExpress.Services;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace UpdateExpress;

/// <summary>
/// The "updates scheduled" notice in the look of the restart notice, with OK as its only
/// button. It behaves as the Windows message box it replaces: shown once and left alone, not
/// topmost, never forced to the foreground, closed by OK, Enter, Esc or the title-bar X, and
/// the caller waits until it is closed.
/// </summary>
internal sealed class ScheduledInstallNoticeWindow : OkNoticeShellWindow
{
    private readonly ScheduledInstallNotice _notice;
    private readonly string _okCaption;

    internal ScheduledInstallNoticeWindow(ScheduledInstallNotice notice)
    {
        _notice = notice;
        _okCaption = AppLanguage.Ok;
        InitNoticeShell(notice.Title);

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

    internal static void ShowAndWait(ScheduledInstallNotice notice) =>
        ShowAndWait(
            () => new ScheduledInstallNoticeWindow(notice),
            GetActiveWindow(),
            () => System.Media.SystemSounds.Asterisk.Play());

    internal static void ShowAndWait(Func<ScheduledInstallNoticeWindow> create, IntPtr owner, Action playSound) =>
        OkNoticeShellWindow.ShowAndWait(() => create(), owner, playSound);

    internal static string ClipboardText(ScheduledInstallNotice notice, string okCaption) =>
        OkNoticeShellWindow.ClipboardText(notice.Title, notice.Body, okCaption);

    protected override void OnKeyDown(WpfKeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            try { System.Windows.Clipboard.SetText(ClipboardText(_notice, _okCaption)); }
            catch { /* clipboard held by another program */ }
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}