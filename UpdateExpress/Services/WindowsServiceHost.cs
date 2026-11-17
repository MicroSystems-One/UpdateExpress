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
    internal const string ServiceName = "MicroSystemsUpdateExpress";
    internal const string DisplayName = "MicroSystems Update Express";

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
                    await DefenderAutomation.RunDueJobAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                AgentLog.WriteInstall("Service automation tick failed: " + ex.Message);
                _log.LogWarning(ex, "Service automation tick failed");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        AgentLog.WriteInstall("Windows service worker stopping.");
    }
}
