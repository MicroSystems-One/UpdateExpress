using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UpdateExpress.Services;

/// <summary>
/// Shared Update Express self-update prompt. Automatic checks stay quiet when up to date.
/// </summary>
internal static class AppUpdatePrompt
{
    private static int _checkRunning;

    /// <summary>
    /// Starts a background channel check. When <paramref name="automatic"/> is true,
    /// UpToDate / CheckFailed only log (no MessageBox). UpdateAvailable always prompts.
    /// </summary>
    internal static void StartCheck(
        bool automatic,
        Action<AppUpdateCheckResult>? onUpdateInstallRequested,
        Action<string, ToolTipIcon>? balloon = null)
    {
        if (Interlocked.CompareExchange(ref _checkRunning, 1, 0) != 0)
        {
            AgentLog.Write("App update check skipped (already running in this process).");
            return;
        }

        if (!automatic)
            balloon?.Invoke("Checking for application updates…", ToolTipIcon.Info);

        _ = Task.Run(async () =>
        {
            AppUpdateCheckResult result;
            try
            {
                result = await AppUpdateChecker.CheckAsync().ConfigureAwait(false);
                AgentLog.Write(
                    $"App update check ({(automatic ? "automatic" : "manual")}): status={result.Status}; " +
                    $"installed={result.InstalledVersion}; available={result.AvailableVersion}; error={result.Error}");
            }
            catch (Exception ex)
            {
                AgentLog.Write("App update check failed: " + ex);
                result = new AppUpdateCheckResult
                {
                    Status = AppUpdateStatus.CheckFailed,
                    InstalledVersion = ProductVersion.Display,
                    Error = ex.Message
                };
            }

            bool installStarted = false;
            try
            {
                var app = System.Windows.Application.Current;
                if (app?.Dispatcher is { } dispatcher)
                {
                    installStarted = await dispatcher.InvokeAsync(() =>
                        Present(result, automatic, onUpdateInstallRequested, balloon));
                }
                else
                {
                    installStarted = Present(result, automatic, onUpdateInstallRequested, balloon);
                }
            }
            catch (Exception ex)
            {
                AgentLog.Write("Cannot present app update result: " + ex.Message);
            }
            finally
            {
                if (!installStarted)
                    Interlocked.Exchange(ref _checkRunning, 0);
            }
        });
    }

    internal static void ReleaseCheckLock() => Interlocked.Exchange(ref _checkRunning, 0);

    private static bool Present(
        AppUpdateCheckResult result,
        bool automatic,
        Action<AppUpdateCheckResult>? onUpdateInstallRequested,
        Action<string, ToolTipIcon>? balloon)
    {
        using var promptGate = new Mutex(false, @"Local\MicroSystems.UpdateExpress.AppUpdatePrompt");
        bool owned = false;
        try
        {
            try { owned = promptGate.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }

            if (!owned && result.Status == AppUpdateStatus.UpdateAvailable)
            {
                AgentLog.Write("App update prompt skipped (another prompt is already open).");
                return false;
            }

            switch (result.Status)
            {
                case AppUpdateStatus.UpToDate:
                    if (automatic)
                    {
                        AgentLog.Write($"App update automatic check: up to date (v{result.InstalledVersion}).");
                        return false;
                    }
                    balloon?.Invoke($"You are up to date (v{result.InstalledVersion}).", ToolTipIcon.Info);
                    System.Windows.Forms.MessageBox.Show(
                        $"Update Express is up to date.\n\nInstalled version: {result.InstalledVersion}",
                        "Update Express",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return false;

                case AppUpdateStatus.UpdateAvailable:
                {
                    string notes = string.IsNullOrWhiteSpace(result.ReleaseNotes) ? "No release notes provided." : result.ReleaseNotes!;
                    balloon?.Invoke($"Version {result.AvailableVersion} is available.", ToolTipIcon.Info);
                    bool canInstall = !string.IsNullOrWhiteSpace(result.DownloadUrl) && !string.IsNullOrWhiteSpace(result.Sha256);
                    bool hasDownload = !string.IsNullOrWhiteSpace(result.DownloadUrl);
                    var choice = System.Windows.Forms.MessageBox.Show(
                        $"A newer Update Express version is available.\n\n" +
                        $"Installed: {result.InstalledVersion}\n" +
                        $"Available: {result.AvailableVersion}\n\n" +
                        $"{notes}\n\n" +
                        (canInstall
                            ? "Yes = Install now (closes open Update Express, then download + verify SHA-256 + replace)\nNo = Open download page\nCancel = Later"
                            : hasDownload
                                ? "SHA-256 is missing from the update channel, so Install is unavailable.\nYes = Open download page\nNo = Later"
                                : "No HTTPS download URL is published yet."),
                        "Update Express update available",
                        canInstall ? MessageBoxButtons.YesNoCancel : hasDownload ? MessageBoxButtons.YesNo : MessageBoxButtons.OK,
                        MessageBoxIcon.Information);

                    if (canInstall && choice == DialogResult.Yes)
                    {
                        if (!UpgradeShutdown.EnsureUiClosedForUpgrade())
                        {
                            balloon?.Invoke("Upgrade cancelled — close Update Express and try again.", ToolTipIcon.Warning);
                            return false;
                        }
                        onUpdateInstallRequested?.Invoke(result);
                        return true;
                    }
                    if ((canInstall && choice == DialogResult.No) || (!canInstall && hasDownload && choice == DialogResult.Yes))
                    {
                        if (!AppUpdateChecker.TryOpenDownload(result.DownloadUrl, out string? error))
                            System.Windows.Forms.MessageBox.Show("Could not open download page: " + error, "Update Express", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    return false;
                }

                default:
                    if (automatic)
                    {
                        AgentLog.Write("App update automatic check failed: " + (result.Error ?? "Unknown error"));
                        return false;
                    }
                    balloon?.Invoke("Application update check failed.", ToolTipIcon.Warning);
                    System.Windows.Forms.MessageBox.Show(
                        "Could not check for Update Express updates.\n\n" + (result.Error ?? "Unknown error"),
                        "Update Express",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return false;
            }
        }
        finally
        {
            if (owned)
            {
                try { promptGate.ReleaseMutex(); } catch { }
            }
        }
    }
}
