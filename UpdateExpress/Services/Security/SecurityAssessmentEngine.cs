using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UpdateExpress.Services.Security;

internal sealed class SecurityAssessmentOptions
{
    /// <summary>How many providers may read at the same time.</summary>
    public int MaxConcurrency { get; init; } = 3;
    /// <summary>Longest time one provider gets, unless it names its own.</summary>
    public TimeSpan ProviderTimeout { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>Receives log lines. They carry check ids, reasons and counts, never a detected value.</summary>
    public Action<string>? Log { get; init; }
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.Now;
}

/// <summary>
/// Runs the providers and collects one snapshot. It only asks and collects: no provider and
/// nothing here changes the PC.
/// Providers run on the thread pool, a limited number at a time, each with its own time
/// limit, so neither a slow nor a hanging read holds the caller or the other checks.
/// A read that fails, times out or is cancelled becomes Unknown or Error for its checks; it is
/// never turned into "not protected".
/// </summary>
internal static class SecurityAssessmentEngine
{
    internal const string ModuleVersion = "1.0";

    /// <summary>Upper bound for any provider, and what a nonsensical time limit falls back to.</summary>
    internal static readonly TimeSpan MaxProviderTimeout = TimeSpan.FromMinutes(2);

    internal static async Task<SecurityAssessmentSnapshot> RunAsync(
        SecurityBaseline baseline,
        IReadOnlyList<ISecurityCheckProvider> providers,
        SecurityMachineInfo machine,
        SecurityAssessmentOptions? options = null,
        IProgress<SecurityCheckResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new SecurityAssessmentOptions();
        var startedAt = options.Clock();
        var stopwatch = Stopwatch.StartNew();
        Log(options, $"Security assessment started: baseline={baseline.Id}; providers={providers.Count}; elevated={machine.IsElevated}; server={Describe(machine.IsServerOs)}");

        var results = new ConcurrentDictionary<string, SecurityCheckResult>(StringComparer.OrdinalIgnoreCase);
        var run = new Run(baseline, machine, options, progress, results, cancellationToken);

        // Each check belongs to the first provider that names it.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var work = new List<Task>();
        using var gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrency));
        foreach (var provider in providers)
        {
            var ids = new List<string>();
            foreach (string id in provider.CheckIds ?? Array.Empty<string>())
            {
                var definition = baseline.Find(id);
                if (definition is null)
                {
                    Log(options, $"Security check skipped: id={SecurityTextSanitizer.Clean(id)} is not in the baseline (provider {provider.Name})");
                    continue;
                }
                if (!claimed.Add(definition.Id))
                {
                    Log(options, $"Security check skipped: id={definition.Id} is already answered by another provider (provider {provider.Name})");
                    continue;
                }
                ids.Add(definition.Id);
            }
            if (ids.Count > 0) work.Add(RunProviderAsync(run, provider, ids, gate));
        }

        // RunProviderAsync does not throw; every outcome is a result.
        await Task.WhenAll(work).ConfigureAwait(false);

        // Cancelled only counts when it cost an answer; a cancel after the last check changes nothing.
        bool completed = results.Values.All(r => r.ReasonCode != SecurityReasonCodes.Cancelled);
        var ordered = baseline.Definitions
            .Select(d => results.TryGetValue(d.Id, out var result) ? result : null)
            .Where(r => r is not null)
            .Select(r => r!)
            .ToArray();
        stopwatch.Stop();

        var snapshot = new SecurityAssessmentSnapshot
        {
            BaselineId = baseline.Id,
            ModuleVersion = ModuleVersion,
            AppVersion = machine.AppVersion,
            ComputerName = machine.ComputerName,
            OsName = machine.OsName,
            OsVersion = machine.OsVersion,
            OsBuild = machine.OsBuild,
            IsServerOs = machine.IsServerOs,
            Elevated = machine.IsElevated,
            StartedAt = startedAt,
            CompletedAt = options.Clock(),
            DurationMs = stopwatch.ElapsedMilliseconds,
            Completed = completed,
            Results = ordered
        };

        var summary = SecurityOverallStatus.Summarize(ordered);
        Log(options,
            $"Security assessment {(completed ? "completed" : "cancelled")}: durationMs={snapshot.DurationMs}; checks={ordered.Length}; " +
            $"protected={summary.Protected}; needsAttention={summary.NeedsAttention}; manualReview={summary.ManualReview}; " +
            $"unknownOrUnsupported={summary.UnknownOrUnsupported}; restartRequired={summary.RestartRequired}; notApplicable={summary.NotApplicable}");
        return snapshot;
    }

    private sealed record Run(
        SecurityBaseline Baseline,
        SecurityMachineInfo Machine,
        SecurityAssessmentOptions Options,
        IProgress<SecurityCheckResult>? Progress,
        ConcurrentDictionary<string, SecurityCheckResult> Results,
        CancellationToken CancellationToken);

    private static async Task RunProviderAsync(Run run, ISecurityCheckProvider provider, List<string> ids, SemaphoreSlim gate)
    {
        var requested = new List<string>();
        try
        {
            // Checks that are not meant for this machine are answered without asking Windows.
            foreach (string id in ids)
            {
                var notApplicable = SecurityBaseline.NotApplicableResult(run.Baseline.Find(id)!, run.Machine);
                if (notApplicable is null) requested.Add(id);
                else Publish(run, notApplicable, 0);
            }
            if (requested.Count == 0) return;

            try { await gate.WaitAsync(run.CancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                PublishAll(run, requested, SecurityAssessmentStatus.Unknown, SecurityReasonCodes.Cancelled, "The assessment was cancelled before this check ran.", 0);
                return;
            }

            try
            {
                await AssessAsync(run, provider, requested).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex)
        {
            // Not expected; kept so that one provider can never take the whole assessment down.
            Log(run.Options, $"Security check failed: provider={provider.Name}; reason={SecurityReasonCodes.ProviderError}; error={ex.GetType().Name}");
            foreach (string id in requested.Where(id => !run.Results.ContainsKey(id)))
                Publish(run, Failure(id, SecurityAssessmentStatus.Error, SecurityReasonCodes.ProviderError, ex.GetType().Name), 0);
        }
    }

    private static async Task AssessAsync(Run run, ISecurityCheckProvider provider, List<string> requested)
    {
        var timeout = provider.Timeout ?? run.Options.ProviderTimeout;
        if (timeout <= TimeSpan.Zero || timeout > MaxProviderTimeout) timeout = MaxProviderTimeout;
        var context = new SecurityCheckContext
        {
            Baseline = run.Baseline,
            Machine = run.Machine,
            RequestedIds = requested.ToArray(),
            Now = run.Options.Clock()
        };
        var stopwatch = Stopwatch.StartNew();
        // The provider's token is not linked to the caller's: what a provider hangs on its token
        // (stopping a Windows read, say) may block, and must hold neither the caller's Cancel()
        // nor this method. Only the engine's own timer listens to the caller directly.
        var limit = new CancellationTokenSource();

        // On the thread pool: a provider that works synchronously must not hold the caller.
        Task<IReadOnlyList<SecurityCheckResult>> assessment =
            Task.Run(() => provider.AssessAsync(context, limit.Token), CancellationToken.None);
        Task first;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(run.CancellationToken))
        {
            try
            {
                first = await Task.WhenAny(assessment, Task.Delay(timeout, wait.Token)).ConfigureAwait(false);
            }
            finally
            {
                wait.Cancel();
                StopProvider(limit, assessment);
            }
        }

        if (first != assessment)
        {
            // Time is up or the user cancelled. A provider that does not listen finishes on its
            // own later and its answer is dropped.
            if (run.CancellationToken.IsCancellationRequested)
            {
                PublishAll(run, requested, SecurityAssessmentStatus.Unknown, SecurityReasonCodes.Cancelled, "The assessment was cancelled before this check answered.", stopwatch.ElapsedMilliseconds);
                return;
            }
            Log(run.Options, $"Security check failed: provider={provider.Name}; ids={string.Join(",", requested)}; reason={SecurityReasonCodes.Timeout}; afterMs={stopwatch.ElapsedMilliseconds}");
            PublishAll(run, requested, SecurityAssessmentStatus.Unknown, SecurityReasonCodes.Timeout, $"No answer within {timeout.TotalSeconds:0.#} seconds.", stopwatch.ElapsedMilliseconds);
            return;
        }

        IReadOnlyList<SecurityCheckResult>? answers;
        try
        {
            answers = await assessment.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (run.CancellationToken.IsCancellationRequested)
            {
                PublishAll(run, requested, SecurityAssessmentStatus.Unknown, SecurityReasonCodes.Cancelled, "The assessment was cancelled before this check answered.", stopwatch.ElapsedMilliseconds);
                return;
            }
            // The provider gave up on its own, for instance on a time limit of its own.
            Log(run.Options, $"Security check failed: provider={provider.Name}; ids={string.Join(",", requested)}; reason={SecurityReasonCodes.Timeout}; afterMs={stopwatch.ElapsedMilliseconds}");
            PublishAll(run, requested, SecurityAssessmentStatus.Unknown, SecurityReasonCodes.Timeout, "The check stopped before it had an answer.", stopwatch.ElapsedMilliseconds);
            return;
        }
        catch (Exception ex)
        {
            string error = SecurityTextSanitizer.Clean(ex.GetType().Name + ": " + ex.Message);
            Log(run.Options, $"Security check failed: provider={provider.Name}; ids={string.Join(",", requested)}; reason={SecurityReasonCodes.ProviderError}; error={ex.GetType().Name}");
            PublishAll(run, requested, SecurityAssessmentStatus.Error, SecurityReasonCodes.ProviderError, error, stopwatch.ElapsedMilliseconds);
            return;
        }

        long elapsed = stopwatch.ElapsedMilliseconds;
        foreach (string id in requested)
        {
            var answer = answers?.FirstOrDefault(r => r is not null && string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            // "Checking" is a state of the window, not an answer.
            if (answer is null || answer.Status == SecurityAssessmentStatus.Checking)
            {
                Log(run.Options, $"Security check failed: provider={provider.Name}; id={id}; reason={SecurityReasonCodes.NoResult}");
                Publish(run, SecurityCheckResult.Unknown(id, SecurityReasonCodes.NoResult, "The check gave no answer."), elapsed);
                continue;
            }
            if (answer.Status == SecurityAssessmentStatus.Error)
                Log(run.Options, $"Security check failed: provider={provider.Name}; id={id}; reason={SecurityTextSanitizer.Clean(answer.ReasonCode)}");
            Publish(run, answer with { Id = id }, elapsed);
        }
    }

    /// <summary>
    /// Tells a provider to stop without waiting for it: its cancellation callbacks run on the
    /// thread pool. The token source lives until both they and the provider have finished,
    /// however late that is.
    /// </summary>
    private static void StopProvider(CancellationTokenSource limit, Task assessment)
    {
        Task stop;
        try { stop = limit.CancelAsync(); }
        catch (Exception ex) { stop = Task.FromException(ex); }
        _ = Task.WhenAll(assessment, stop).ContinueWith(
            t => { _ = t.Exception; limit.Dispose(); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void PublishAll(Run run, IEnumerable<string> ids, SecurityAssessmentStatus status, string reasonCode, string detail, long durationMs)
    {
        foreach (string id in ids) Publish(run, Failure(id, status, reasonCode, detail), durationMs);
    }

    private static SecurityCheckResult Failure(string id, SecurityAssessmentStatus status, string reasonCode, string detail) =>
        new() { Id = id, Status = status, ReasonCode = reasonCode, Detail = detail };

    private static void Publish(Run run, SecurityCheckResult result, long durationMs)
    {
        var cleaned = result with
        {
            DetectedCode = string.IsNullOrWhiteSpace(result.DetectedCode) ? SecurityStateCodes.Unknown : result.DetectedCode.Trim(),
            DetectedValue = SecurityTextSanitizer.Clean(result.DetectedValue),
            ReasonCode = SecurityTextSanitizer.Clean(result.ReasonCode),
            Detail = SecurityTextSanitizer.Clean(result.Detail),
            CheckedAt = result.CheckedAt == default ? run.Options.Clock() : result.CheckedAt,
            DurationMs = result.DurationMs > 0 ? result.DurationMs : durationMs
        };
        run.Results[cleaned.Id] = cleaned;
        try { run.Progress?.Report(cleaned); }
        catch { /* A listener's fault must not lose the result. */ }
    }

    private static string Describe(bool? value) => value switch { true => "yes", false => "no", null => "unknown" };

    private static void Log(SecurityAssessmentOptions options, string line)
    {
        try { options.Log?.Invoke(line); }
        catch { /* Logging must never break an assessment. */ }
    }
}
