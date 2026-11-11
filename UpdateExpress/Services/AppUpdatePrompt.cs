using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UpdateExpress.Services;

/// <summary>
/// Shared Update Express self-update prompt. Automatic checks stay quiet when up to date.
/// Dialog text and buttons follow ReminderSchedule.Language via AppLanguage string tables,
/// not the OS MessageBox locale and not hardcoded Yes/No/Cancel captions.
/// </summary>
internal static class AppUpdatePrompt
{
    private static int _checkRunning;

    /// <summary>
    /// Starts a background channel check. When <paramref name="automatic"/> is true,
    /// UpToDate / CheckFailed only log (no dialog). UpdateAvailable always prompts.
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
            balloon?.Invoke(AppLanguage.T("Checking for application updates…"), ToolTipIcon.Info);

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
                    balloon?.Invoke(
                        AppLanguage.Tf("You are up to date (v{0}).", result.InstalledVersion),
                        ToolTipIcon.Info);
                    ShowAppLanguageDialog(
                        AppLanguage.Tf(
                            "Update Express is up to date.\n\nInstalled version: {0}",
                            result.InstalledVersion),
                        "Update Express",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return false;

                case AppUpdateStatus.UpdateAvailable:
                {
                    string notes = string.IsNullOrWhiteSpace(result.ReleaseNotes)
                        ? AppLanguage.T("No release notes provided.")
                        : result.ReleaseNotes!;
                    balloon?.Invoke(
                        AppLanguage.Tf("Version {0} is available.", result.AvailableVersion),
                        ToolTipIcon.Info);
                    bool canInstall = !string.IsNullOrWhiteSpace(result.DownloadUrl) && !string.IsNullOrWhiteSpace(result.Sha256);
                    bool hasDownload = !string.IsNullOrWhiteSpace(result.DownloadUrl);
                    string body;
                    MessageBoxButtons buttons;
                    string header =
                        AppLanguage.T("A newer Update Express version is available.") + "\n\n" +
                        AppLanguage.Tf("Installed: {0}", result.InstalledVersion) + "\n" +
                        AppLanguage.Tf("Available: {0}", result.AvailableVersion) + "\n\n" +
                        notes;
                    if (canInstall)
                    {
                        body = header + "\n\n" +
                            AppLanguage.ButtonLegend(
                                "Yes",
                                "Install now (closes open Update Express, then download + verify SHA-256 + replace)") + "\n" +
                            AppLanguage.ButtonLegend("No", "Open download page") + "\n" +
                            AppLanguage.ButtonLegend("Cancel", "Later");
                        buttons = MessageBoxButtons.YesNoCancel;
                    }
                    else if (hasDownload)
                    {
                        body = header + "\n\n" +
                            AppLanguage.T("SHA-256 is missing from the update channel, so Install is unavailable.") + "\n" +
                            AppLanguage.ButtonLegend("Yes", "Open download page") + "\n" +
                            AppLanguage.ButtonLegend("No", "Later");
                        buttons = MessageBoxButtons.YesNo;
                    }
                    else
                    {
                        body = header + "\n\n" +
                            AppLanguage.T("No HTTPS download URL is published yet.");
                        buttons = MessageBoxButtons.OK;
                    }

                    var choice = ShowAppLanguageDialog(
                        body,
                        AppLanguage.T("Update Express update available"),
                        buttons,
                        MessageBoxIcon.Information);

                    if (canInstall && choice == DialogResult.Yes)
                    {
                        if (!UpgradeShutdown.EnsureUiClosedForUpgrade())
                        {
                            balloon?.Invoke(
                                AppLanguage.T("Upgrade cancelled — close Update Express and try again."),
                                ToolTipIcon.Warning);
                            return false;
                        }
                        onUpdateInstallRequested?.Invoke(result);
                        return true;
                    }
                    if ((canInstall && choice == DialogResult.No) || (!canInstall && hasDownload && choice == DialogResult.Yes))
                    {
                        if (!AppUpdateChecker.TryOpenDownload(result.DownloadUrl, out string? error))
                        {
                            ShowAppLanguageDialog(
                                AppLanguage.Tf("Could not open download page: {0}", error ?? ""),
                                "Update Express",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
                        }
                    }
                    return false;
                }

                default:
                    if (automatic)
                    {
                        AgentLog.Write("App update automatic check failed: " + (result.Error ?? "Unknown error"));
                        return false;
                    }
                    balloon?.Invoke(AppLanguage.T("Application update check failed."), ToolTipIcon.Warning);
                    ShowAppLanguageDialog(
                        AppLanguage.Tf(
                            "Could not check for Update Express updates.\n\n{0}",
                            result.Error ?? AppLanguage.T("Unknown error")),
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

    /// <summary>
    /// WinForms MessageBox localizes Yes/No/Cancel from the OS UI language, which can disagree
    /// with ReminderSchedule.Language. This dialog paints button captions from AppLanguage.
    /// </summary>
    private static DialogResult ShowAppLanguageDialog(
        string text,
        string caption,
        MessageBoxButtons buttons,
        MessageBoxIcon icon)
    {
        using var form = new Form
        {
            Text = caption,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterScreen,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14),
            Font = SystemFonts.MessageBoxFont,
            MinimumSize = new Size(420, 120)
        };

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 2,
            Dock = DockStyle.Fill,
            Padding = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var iconBox = new PictureBox
        {
            Size = new Size(32, 32),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Margin = new Padding(0, 0, 12, 0),
            Image = icon switch
            {
                MessageBoxIcon.Warning => SystemIcons.Warning.ToBitmap(),
                MessageBoxIcon.Error => SystemIcons.Error.ToBitmap(),
                MessageBoxIcon.Question => SystemIcons.Question.ToBitmap(),
                _ => SystemIcons.Information.ToBitmap()
            }
        };

        var message = new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Margin = new Padding(0, 2, 0, 16)
        };

        var buttonRow = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };

        DialogResult result = DialogResult.Cancel;

        void AddButton(string label, DialogResult dialogResult, bool isAccept = false, bool isCancel = false)
        {
            // Global WpfTypeAliases maps Button → WPF; this dialog is WinForms.
            var button = new System.Windows.Forms.Button
            {
                Text = label,
                AutoSize = true,
                MinimumSize = new Size(88, 28),
                Margin = new Padding(8, 0, 0, 0),
                DialogResult = dialogResult
            };
            button.Click += (_, _) =>
            {
                result = dialogResult;
                form.DialogResult = dialogResult;
                form.Close();
            };
            buttonRow.Controls.Add(button);
            if (isAccept) form.AcceptButton = button;
            if (isCancel) form.CancelButton = button;
        }

        switch (buttons)
        {
            case MessageBoxButtons.YesNoCancel:
                // RightToLeft flow: add Cancel, No, Yes so Yes ends leftmost visually like MessageBox.
                AddButton(AppLanguage.Cancel, DialogResult.Cancel, isCancel: true);
                AddButton(AppLanguage.No, DialogResult.No);
                AddButton(AppLanguage.Yes, DialogResult.Yes, isAccept: true);
                break;
            case MessageBoxButtons.YesNo:
                AddButton(AppLanguage.No, DialogResult.No, isCancel: true);
                AddButton(AppLanguage.Yes, DialogResult.Yes, isAccept: true);
                break;
            default:
                AddButton(AppLanguage.Ok, DialogResult.OK, isAccept: true, isCancel: true);
                break;
        }

        layout.Controls.Add(iconBox, 0, 0);
        layout.Controls.Add(message, 1, 0);
        layout.SetColumnSpan(buttonRow, 2);
        layout.Controls.Add(buttonRow, 0, 1);
        form.Controls.Add(layout);

        form.ShowDialog();
        return result;
    }
}
