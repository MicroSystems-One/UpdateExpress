using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UpdateExpress.Services.Security.Providers;

namespace UpdateExpress.Services.Security;

/// <summary>The checks this version can assess. A check of the baseline without a provider stays unanswered.</summary>
internal static class SecurityCheckProviders
{
    internal static IReadOnlyList<ISecurityCheckProvider> Create() => new ISecurityCheckProvider[]
    {
        new FirewallProfileCheckProvider()
    };
}

/// <summary>
/// One assessment from start to stored result: reads what the machine is, runs the engine off
/// the caller's thread, keeps a finished assessment in the cache and writes the log lines.
/// A cancelled assessment is returned but never stored, so the last finished one stays.
/// </summary>
internal static class SecurityAssessmentRunner
{
    internal const string ActivityAction = "Security assessment";

    /// <summary>Where the log lines go. Replaceable for tests and isolated runs.</summary>
    internal static Action<string> Log { get; set; } = AgentLog.Write;

    /// <summary>Where the activity record goes: action, status, detail.</summary>
    internal static Action<string, string, object?> Activity { get; set; } =
        (action, status, detail) => ActivityLog.TryWrite(action, status, detail);

    internal static Func<SecurityMachineInfo> ReadMachine { get; set; } = SecurityMachineInfo.ReadCurrent;

    internal static Func<IReadOnlyList<ISecurityCheckProvider>> Providers { get; set; } = SecurityCheckProviders.Create;

    /// <summary>
    /// Whether an assessment takes the place of the one before it, in the cache and on screen.
    /// A cancelled one does not, and neither does one in which no check answered: that is not
    /// a picture worth keeping over an older one.
    /// </summary>
    internal static bool ReplacesEarlier(SecurityAssessmentSnapshot? snapshot) =>
        snapshot is { Completed: true } && snapshot.Results.Count > 0;

    internal static Task<SecurityAssessmentSnapshot> RunAsync(
        SecurityBaseline baseline,
        IProgress<SecurityCheckResult>? progress,
        CancellationToken cancellationToken) =>
        // Reading the machine and starting the providers is work too; none of it on the window's thread.
        Task.Run(async () =>
        {
            var options = new SecurityAssessmentOptions { Log = WriteLog };
            var snapshot = await SecurityAssessmentEngine
                .RunAsync(baseline, Providers(), ReadMachine(), options, progress, cancellationToken)
                .ConfigureAwait(false);

            bool worthKeeping = ReplacesEarlier(snapshot);
            bool saved = worthKeeping && SecurityResultCache.TrySave(snapshot);
            if (worthKeeping && !saved)
                WriteLog("Security assessment result could not be saved; the previous stored result stays.");

            var summary = SecurityOverallStatus.Summarize(snapshot.Results);
            // Counts and timing only: no detected value, no computer or user name beyond what
            // the activity log adds to every record by itself.
            WriteActivity(snapshot.Completed ? "Completed" : "Cancelled", new
            {
                Baseline = snapshot.BaselineId,
                snapshot.DurationMs,
                Checks = snapshot.Results.Count,
                summary.Protected,
                summary.NeedsAttention,
                summary.ManualReview,
                summary.UnknownOrUnsupported,
                summary.RestartRequired,
                summary.NotApplicable,
                Saved = saved
            });
            return snapshot;
        }, CancellationToken.None);

    private static void WriteLog(string line)
    {
        try { Log(line); }
        catch { /* Logging must never break an assessment. */ }
    }

    private static void WriteActivity(string status, object detail)
    {
        try { Activity(ActivityAction, status, detail); }
        catch { /* Logging must never break an assessment. */ }
    }
}
