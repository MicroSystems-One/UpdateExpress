using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UpdateExpress.Services;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace UpdateExpress;

/// <summary>
/// OK-only information notice in the restart/scheduled-install look (deferred restart during
/// active hours, and similar). Same message-box behaviour as <see cref="ScheduledInstallNoticeWindow"/>.
/// </summary>
internal sealed class InformationNoticeWindow : OkNoticeShellWindow
{
    private readonly InformationNotice _notice;
    private readonly string _okCaption;

    internal InformationNoticeWindow(InformationNotice notice)
    {
        _notice = notice;
        _okCaption = AppLanguage.Ok;
        InitNoticeShell(notice.Title);

        var ok = NoticeLook.ChromeButton(_okCaption, primary: true);
        ok.IsDefault = true;
        ok.IsCancel = true;
        ok.Click += (_, _) => Close();

        var lead = NoticeLook.Strong(notice.Lead);
        lead.TextTrimming = TextTrimming.None;
        lead.TextWrapping = TextWrapping.Wrap;

        var detail = NoticeLook.Body(notice.Detail);
        detail.Margin = string.IsNullOrEmpty(notice.Detail) ? new Thickness(0) : detail.Margin;

        var rows = string.IsNullOrEmpty(notice.Detail)
            ? new UIElement[] { NoticeLook.Heading(notice.Title), lead, NoticeLook.ButtonRow(ok) }
            : new UIElement[] { NoticeLook.Heading(notice.Title), lead, detail, NoticeLook.ButtonRow(ok) };
        Content = NoticeLook.Card(rows);
        Loaded += (_, _) => ok.Focus();
    }

    internal static void ShowAndWait(InformationNotice notice, Window? wpfOwner = null) =>
        ShowAndWait(
            () => new InformationNoticeWindow(notice),
            OwnerFrom(wpfOwner),
            () => System.Media.SystemSounds.Asterisk.Play());

    protected override void OnKeyDown(WpfKeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            try { System.Windows.Clipboard.SetText(ClipboardText(_notice.Title, _notice.Body, _okCaption)); }
            catch { /* clipboard held by another program */ }
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}