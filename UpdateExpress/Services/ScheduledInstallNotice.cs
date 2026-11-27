namespace UpdateExpress.Services;

/// <summary>
/// The scheduled-install notice in the parts its window lays out: the title, the line saying
/// updates are ready, the line saying when they install, an optional sentence about the
/// restart, and the tip.
/// </summary>
internal sealed record ScheduledInstallNotice(string Title, string Ready, string When, string? Restart, string Tip)
{
    /// <summary>The whole text in one piece, as the message box before the window showed it.</summary>
    internal string Body => Ready + "\n" + When + (Restart is null ? "" : " " + Restart) + "\n\n" + Tip;
}
