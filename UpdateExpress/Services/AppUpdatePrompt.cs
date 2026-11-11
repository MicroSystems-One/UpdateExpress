using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UpdateExpress.Services;

/// <summary>
/// Shared Update Express self-update prompt. Automatic checks stay quiet when up to date.
/// Dialog text and buttons follow ReminderSchedule.Language (en/no), not the OS MessageBox locale.
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

        bool norwegian = IsNorwegian();
        if (!automatic)
            balloon?.Invoke(norwegian ? "Ser etter programoppdateringer…" : "Checking for application updates…", ToolTipIcon.Info);

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

    private static bool IsNorwegian() =>
        string.Equals(ReminderSchedule.Load().Language, "no", StringComparison.OrdinalIgnoreCase);

    private static bool Present(
        AppUpdateCheckResult result,
        bool automatic,
        Action<AppUpdateCheckResult>? onUpdateInstallRequested,
        Action<string, ToolTipIcon>? balloon)
    {
        using var promptGate = new Mutex(false, @"Local\MicroSystems.UpdateExpress.AppUpdatePrompt");
        bool owned = false;
        bool norwegian = IsNorwegian();
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
                        norwegian
                            ? $"Du er oppdatert (v{result.InstalledVersion})."
                            : $"You are up to date (v{result.InstalledVersion}).",
                        ToolTipIcon.Info);
                    ShowAppLanguageDialog(
                        norwegian
                            ? $"Update Express er oppdatert.\n\nInstallert versjon: {result.InstalledVersion}"
                            : $"Update Express is up to date.\n\nInstalled version: {result.InstalledVersion}",
                        "Update Express",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information,
                        norwegian);
                    return false;

                case AppUpdateStatus.UpdateAvailable:
                {
                    string notes = string.IsNullOrWhiteSpace(result.ReleaseNotes)
                        ? (norwegian ? "Ingen versjonsmerknader." : "No release notes provided.")
                        : result.ReleaseNotes!;
                    balloon?.Invoke(
                        norwegian
                            ? $"Versjon {result.AvailableVersion} er tilgjengelig."
                            : $"Version {result.AvailableVersion} is available.",
                        ToolTipIcon.Info);
                    bool canInstall = !string.IsNullOrWhiteSpace(result.DownloadUrl) && !string.IsNullOrWhiteSpace(result.Sha256);
                    bool hasDownload = !string.IsNullOrWhiteSpace(result.DownloadUrl);
                    string body;
                    MessageBoxButtons buttons;
                    if (canInstall)
                    {
                        body = norwegian
                            ? $"En nyere versjon av Update Express er tilgjengelig.\n\n" +
                              $"Installert: {result.InstalledVersion}\n" +
                              $"Tilgjengelig: {result.AvailableVersion}\n\n" +
                              $"{notes}\n\n" +
                              "Ja = Installer nå (lukker åpen Update Express, deretter last ned + verifiser SHA-256 + erstatt)\n" +
                              "Nei = Åpne nedlastingssiden\n" +
                              "Avbryt = Senere"
                            : $"A newer Update Express version is available.\n\n" +
                              $"Installed: {result.InstalledVersion}\n" +
                              $"Available: {result.AvailableVersion}\n\n" +
                              $"{notes}\n\n" +
                              "Yes = Install now (closes open Update Express, then download + verify SHA-256 + replace)\n" +
                              "No = Open download page\n" +
                              "Cancel = Later";
                        buttons = MessageBoxButtons.YesNoCancel;
                    }
                    else if (hasDownload)
                    {
                        body = norwegian
                            ? $"En nyere versjon av Update Express er tilgjengelig.\n\n" +
                              $"Installert: {result.InstalledVersion}\n" +
                              $"Tilgjengelig: {result.AvailableVersion}\n\n" +
                              $"{notes}\n\n" +
                              "SHA-256 mangler i oppdateringskanalen, så Installer er utilgjengelig.\n" +
                              "Ja = Åpne nedlastingssiden\n" +
                              "Nei = Senere"
                            : $"A newer Update Express version is available.\n\n" +
                              $"Installed: {result.InstalledVersion}\n" +
                              $"Available: {result.AvailableVersion}\n\n" +
                              $"{notes}\n\n" +
                              "SHA-256 is missing from the update channel, so Install is unavailable.\n" +
                              "Yes = Open download page\n" +
                              "No = Later";
                        buttons = MessageBoxButtons.YesNo;
                    }
                    else
                    {
                        body = norwegian
                            ? $"En nyere versjon av Update Express er tilgjengelig.\n\n" +
                              $"Installert: {result.InstalledVersion}\n" +
                              $"Tilgjengelig: {result.AvailableVersion}\n\n" +
                              $"{notes}\n\n" +
                              "Ingen HTTPS-nedlastingsadresse er publisert ennå."
                            : $"A newer Update Express version is available.\n\n" +
                              $"Installed: {result.InstalledVersion}\n" +
                              $"Available: {result.AvailableVersion}\n\n" +
                              $"{notes}\n\n" +
                              "No HTTPS download URL is published yet.";
                        buttons = MessageBoxButtons.OK;
                    }

                    var choice = ShowAppLanguageDialog(
                        body,
                        norwegian ? "Update Express – oppdatering tilgjengelig" : "Update Express update available",
                        buttons,
                        MessageBoxIcon.Information,
                        norwegian);

                    if (canInstall && choice == DialogResult.Yes)
                    {
                        if (!UpgradeShutdown.EnsureUiClosedForUpgrade())
                        {
                            balloon?.Invoke(
                                norwegian
                                    ? "Oppgradering avbrutt — lukk Update Express og prøv igjen."
                                    : "Upgrade cancelled — close Update Express and try again.",
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
                                norwegian
                                    ? "Kunne ikke åpne nedlastingssiden: " + error
                                    : "Could not open download page: " + error,
                                "Update Express",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning,
                                norwegian);
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
                    balloon?.Invoke(
                        norwegian ? "Sjekk av programoppdatering feilet." : "Application update check failed.",
                        ToolTipIcon.Warning);
                    ShowAppLanguageDialog(
                        norwegian
                            ? "Kunne ikke sjekke etter Update Express-oppdateringer.\n\n" + (result.Error ?? "Ukjent feil")
                            : "Could not check for Update Express updates.\n\n" + (result.Error ?? "Unknown error"),
                        "Update Express",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning,
                        norwegian);
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
    /// with ReminderSchedule.Language. This dialog paints button captions from the app language.
    /// </summary>
    private static DialogResult ShowAppLanguageDialog(
        string text,
        string caption,
        MessageBoxButtons buttons,
        MessageBoxIcon icon,
        bool norwegian)
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
            var button = new Button
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
                AddButton(norwegian ? "Avbryt" : "Cancel", DialogResult.Cancel, isCancel: true);
                AddButton(norwegian ? "Nei" : "No", DialogResult.No);
                AddButton(norwegian ? "Ja" : "Yes", DialogResult.Yes, isAccept: true);
                break;
            case MessageBoxButtons.YesNo:
                AddButton(norwegian ? "Nei" : "No", DialogResult.No, isCancel: true);
                AddButton(norwegian ? "Ja" : "Yes", DialogResult.Yes, isAccept: true);
                break;
            default:
                AddButton("OK", DialogResult.OK, isAccept: true, isCancel: true);
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
