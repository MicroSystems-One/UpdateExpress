using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UpdateExpress.Services;

/// <summary>
/// Session-0 Windows service host. Runs Defender / schedule polling with system rights.
/// Prompt UI stays with the per-user --agent when a user is signed in.
/// </summary>
internal static class WindowsServiceHost
{
    /// <summary>SCM key (stable). Shown under M if DisplayName is missing.</summary>
    internal const string ServiceName = "MicroSystemsUpdateExpress";
    /// <summary>Friendly name in services.msc — sorts under U as “Update Express”.</summary>
    internal const string DisplayName = "Update Express";

    internal static void Run()
    {
        Host.CreateDefaultBuilder()
            .UseWindowsService(options =>
            {
                options.ServiceName = ServiceName;
            })
            .ConfigureLogging(logging =>
            {
                // Avoid custom Event Log sources (registration needs elevation quirks).
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .ConfigureServices(services =>
            {
                services.AddHostedService<UpdateExpressServiceWorker>();
            })
            .Build()
            .Run();
    }
}

internal sealed class UpdateExpressServiceWorker : BackgroundService
{
    private readonly ILogger<UpdateExpressServiceWorker> _log;

    public UpdateExpressServiceWorker(ILogger<UpdateExpressServiceWorker> log) => _log = log;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AgentLog.WriteInstall("Windows service worker started (LocalSystem/NetworkService host).");
        _log.LogInformation("Update Express service worker started.");
        // Right away, not after the stagger: state an older version left beside
        // fleet-policy.json moves to its own subfolder before the user's agent looks for it.
        AutomationStatePaths.MaintainSharedState(force: true);
        // Stagger first tick so boot is not blocked by WUA.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var policy = UpdatePolicy.Load();
                policy.Normalize();
                if (!policy.SetupCompleted)
                {
                    AgentLog.WriteInstall("Service tick skipped: SetupCompleted=false (consent gate).");
                }
                else
                {
                    // Before this tick writes state as SYSTEM: keep the shared folder writable
                    // for the signed-in user's agent, or it stops using that folder.
                    AutomationStatePaths.EnsureSharedDirectoryAccess();
                    await DefenderAutomation.RunDueJobAsync().ConfigureAwait(false);
                    // Session 0 cannot show the restart notice. The signed-in user's agent shows
                    // it; this only completes a committed restart that nobody picked up.
                    try { RebootCoordinator.RunDueJob(allowUiPrompt: false); }
                    catch (Exception rex)
                    {
                        AgentLog.WriteInstall("Service reminder reboot poll failed: " + rex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                AgentLog.WriteInstall("Service automation tick failed: " + ex.Message);
                _log.LogWarning(ex, "Service automation tick failed");
            }

            // Poll sooner when a committed restart is armed.
            TimeSpan delay = TimeSpan.FromMinutes(5);
            try
            {
                var pending = RebootCoordinator.LoadPending();
                if (pending is { Cancelled: false, EarliestRebootAt: DateTime due }
                    && RebootCoordinator.IsCommitted(pending))
                {
                    var until = due - DateTime.Now;
                    if (until <= TimeSpan.Zero) delay = TimeSpan.FromSeconds(15);
                    else if (until < delay) delay = until + TimeSpan.FromSeconds(2);
                }
            }
            catch { /* keep default */ }

            try { await Task.Delay(delay, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        AgentLog.WriteInstall("Windows service worker stopping.");
    }
}
