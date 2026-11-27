using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using UpdateExpress.Services.Security;
using Xunit;
using Status = UpdateExpress.Services.Security.SecurityAssessmentStatus;

namespace UpdateExpress.Tests;

public class SecurityAssessmentEngineTests
{
    private static readonly SecurityBaseline Baseline = SecurityBaselineV1.Create();
    private static readonly SecurityMachineInfo Client = new()
    {
        ComputerName = "TEST-PC", OsName = "Windows 11 Pro 25H2", OsVersion = "25H2", OsBuild = 26200,
        IsServerOs = false, IsElevated = true, AppVersion = "0.7.2.63"
    };
    private static readonly SecurityMachineInfo Server = Client with { OsName = "Windows Server 2022 Standard", OsBuild = 20348, IsServerOs = true };

    /// <summary>Long enough that a test never reaches it, short enough that a hung test ends.</summary>
    private static readonly TimeSpan Never = TimeSpan.FromSeconds(30);

    private sealed class FakeProvider : ISecurityCheckProvider
    {
        private readonly Func<SecurityCheckContext, CancellationToken, Task<IReadOnlyList<SecurityCheckResult>>> _assess;

        public FakeProvider(string[] ids, Func<SecurityCheckContext, CancellationToken, Task<IReadOnlyList<SecurityCheckResult>>> assess)
        {
            CheckIds = ids;
            _assess = assess;
        }

        public string Name { get; init; } = "Fake";
        public IReadOnlyList<string> CheckIds { get; }
        public TimeSpan? Timeout { get; init; }
        public int Calls;
        public SecurityCheckContext? LastContext;

        public Task<IReadOnlyList<SecurityCheckResult>> AssessAsync(SecurityCheckContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            LastContext = context;
            return _assess(context, cancellationToken);
        }
    }

    private static FakeProvider Answers(Status status, params string[] ids) =>
        new(ids, (context, _) => Task.FromResult<IReadOnlyList<SecurityCheckResult>>(
            context.RequestedIds.Select(id => new SecurityCheckResult { Id = id, Status = status, DetectedCode = SecurityStateCodes.On }).ToArray()));

    private static FakeProvider Throws(Exception exception, params string[] ids) =>
        new(ids, (_, _) => throw exception);

    /// <summary>Never answers and does not listen to cancellation, like a hung Windows call.</summary>
    private static FakeProvider Hangs(params string[] ids) => Hangs(null, ids);

    private static FakeProvider Hangs(TimeSpan? timeout, params string[] ids) =>
        new(ids, async (context, _) =>
        {
            await Task.Delay(Never, CancellationToken.None);
            return Array.Empty<SecurityCheckResult>();
        })
        { Timeout = timeout };

    private static Task<SecurityAssessmentSnapshot> Run(
        IReadOnlyList<ISecurityCheckProvider> providers,
        SecurityMachineInfo? machine = null,
        SecurityAssessmentOptions? options = null,
        IProgress<SecurityCheckResult>? progress = null,
        CancellationToken cancellationToken = default) =>
        SecurityAssessmentEngine.RunAsync(Baseline, providers, machine ?? Client, options, progress, cancellationToken);

    [Fact]
    public async Task Results_come_back_in_baseline_order_with_the_machine_and_baseline_named()
    {
        var at = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.FromHours(1));
        var options = new SecurityAssessmentOptions { Clock = () => at };

        var snapshot = await Run(new[] { Answers(Status.Protected, "UPD-SEC-004", "ACC-007"), Answers(Status.NeedsAttention, "NET-002", "NET-001") }, options: options);

        Assert.Equal(new[] { "NET-001", "NET-002", "ACC-007", "UPD-SEC-004" }, snapshot.Results.Select(r => r.Id).ToArray());
        Assert.True(snapshot.Completed);
        Assert.Equal(SecurityBaselineV1.BaselineId, snapshot.BaselineId);
        Assert.Equal(SecurityAssessmentEngine.ModuleVersion, snapshot.ModuleVersion);
        Assert.Equal(SecurityAssessmentSnapshot.CurrentSchemaVersion, snapshot.SchemaVersion);
        Assert.Equal("TEST-PC", snapshot.ComputerName);
        Assert.Equal("Windows 11 Pro 25H2", snapshot.OsName);
        Assert.Equal(26200, snapshot.OsBuild);
        Assert.Equal("0.7.2.63", snapshot.AppVersion);
        Assert.True(snapshot.Elevated);
        Assert.Equal(at, snapshot.StartedAt);
        Assert.Equal(at, snapshot.CompletedAt);
        Assert.All(snapshot.Results, r => Assert.Equal(at, r.CheckedAt));
        Assert.Equal(Status.NeedsAttention, snapshot.Results[0].Status);
        Assert.Equal(Status.Protected, snapshot.Results[2].Status);
    }

    [Fact]
    public async Task A_provider_that_throws_gives_a_failed_check_not_a_failed_setting_and_the_others_still_answer()
    {
        var snapshot = await Run(new ISecurityCheckProvider[]
        {
            Throws(new InvalidOperationException("WMI is broken"), "NET-001", "NET-002"),
            Answers(Status.Protected, "ACC-007")
        });

        Assert.True(snapshot.Completed);
        foreach (string id in new[] { "NET-001", "NET-002" })
        {
            var failed = snapshot.Results.Single(r => r.Id == id);
            Assert.Equal(Status.Error, failed.Status);
            Assert.Equal(SecurityReasonCodes.ProviderError, failed.ReasonCode);
            Assert.Equal(SecurityStateCodes.Unknown, failed.DetectedCode);
            Assert.Contains("InvalidOperationException", failed.Detail);
        }
        Assert.Equal(Status.Protected, snapshot.Results.Single(r => r.Id == "ACC-007").Status);
        Assert.DoesNotContain(snapshot.Results, r => r.Status == Status.NotProtected);
    }

    [Fact]
    public async Task A_provider_that_fails_asynchronously_is_a_failed_check_too()
    {
        var provider = new FakeProvider(new[] { "NET-001" }, async (_, _) =>
        {
            await Task.Yield();
            throw new UnauthorizedAccessException("Access is denied");
        });

        var snapshot = await Run(new[] { provider });

        Assert.Equal(Status.Error, snapshot.Results.Single().Status);
    }

    [Fact]
    public async Task A_provider_that_hangs_is_unknown_after_its_time_limit_and_does_not_hold_the_assessment()
    {
        // Only the hanging provider gets the short limit, so a busy test machine cannot make
        // the quick one late.
        var options = new SecurityAssessmentOptions { ProviderTimeout = Never };
        var stopwatch = Stopwatch.StartNew();

        var snapshot = await Run(new ISecurityCheckProvider[] { Hangs(TimeSpan.FromMilliseconds(150), "NET-001", "NET-002"), Answers(Status.Protected, "ACC-007") }, options: options);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), "took " + stopwatch.Elapsed);
        Assert.True(snapshot.Completed);
        foreach (string id in new[] { "NET-001", "NET-002" })
        {
            var timedOut = snapshot.Results.Single(r => r.Id == id);
            Assert.Equal(Status.Unknown, timedOut.Status);
            Assert.Equal(SecurityReasonCodes.Timeout, timedOut.ReasonCode);
        }
        Assert.Equal(Status.Protected, snapshot.Results.Single(r => r.Id == "ACC-007").Status);
    }

    [Fact]
    public async Task A_provider_may_name_its_own_time_limit()
    {
        var slow = new FakeProvider(new[] { "NET-001" }, async (_, _) =>
        {
            await Task.Delay(Never, CancellationToken.None);
            return Array.Empty<SecurityCheckResult>();
        })
        { Timeout = TimeSpan.FromMilliseconds(100) };

        var snapshot = await Run(new[] { slow }, options: new SecurityAssessmentOptions { ProviderTimeout = Never });

        Assert.Equal(SecurityReasonCodes.Timeout, snapshot.Results.Single().ReasonCode);
    }

    [Fact]
    public async Task A_provider_is_told_to_stop_when_its_time_is_up()
    {
        var told = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider(new[] { "NET-001" }, async (_, token) =>
        {
            token.Register(() => told.TrySetResult());
            await Task.Delay(Never, CancellationToken.None);
            return Array.Empty<SecurityCheckResult>();
        });

        await Run(new[] { provider }, options: new SecurityAssessmentOptions { ProviderTimeout = TimeSpan.FromMilliseconds(100) });

        await told.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_provider_that_gives_up_by_itself_is_unknown_and_the_assessment_is_still_whole()
    {
        var snapshot = await Run(new[] { Throws(new OperationCanceledException(), "NET-001") });

        Assert.True(snapshot.Completed);
        Assert.Equal(Status.Unknown, snapshot.Results.Single().Status);
        Assert.Equal(SecurityReasonCodes.Timeout, snapshot.Results.Single().ReasonCode);
    }

    [Fact]
    public async Task Cancelling_returns_at_once_with_the_unanswered_checks_unknown_and_the_assessment_marked_incomplete()
    {
        using var cancel = new CancellationTokenSource();
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new Collector(result => { if (result.Id == "ACC-007") answered.TrySetResult(); });
        var stopwatch = Stopwatch.StartNew();

        var running = Run(
            new ISecurityCheckProvider[] { Answers(Status.Protected, "ACC-007"), Hangs("NET-001") },
            options: new SecurityAssessmentOptions { ProviderTimeout = Never },
            progress: progress,
            cancellationToken: cancel.Token);
        await answered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        var snapshot = await running;

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), "took " + stopwatch.Elapsed);
        Assert.False(snapshot.Completed);
        Assert.Equal(Status.Protected, snapshot.Results.Single(r => r.Id == "ACC-007").Status);
        var cancelled = snapshot.Results.Single(r => r.Id == "NET-001");
        Assert.Equal(Status.Unknown, cancelled.Status);
        Assert.Equal(SecurityReasonCodes.Cancelled, cancelled.ReasonCode);
    }

    [Fact]
    public async Task An_assessment_cancelled_before_it_starts_asks_no_provider()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var provider = Answers(Status.Protected, "NET-001", "NET-002");

        var snapshot = await Run(new[] { provider }, cancellationToken: cancel.Token);

        Assert.Equal(0, provider.Calls);
        Assert.False(snapshot.Completed);
        Assert.All(snapshot.Results, r => Assert.Equal(SecurityReasonCodes.Cancelled, r.ReasonCode));
        Assert.All(snapshot.Results, r => Assert.Equal(Status.Unknown, r.Status));
    }

    [Fact]
    public async Task A_cancel_after_every_check_has_answered_changes_nothing()
    {
        using var cancel = new CancellationTokenSource();
        var progress = new Collector(_ => cancel.Cancel());

        var snapshot = await Run(new[] { Answers(Status.Protected, "NET-001") }, progress: progress, cancellationToken: cancel.Token);

        Assert.True(snapshot.Completed);
        Assert.Equal(Status.Protected, snapshot.Results.Single().Status);
    }

    [Fact]
    public async Task A_check_the_provider_did_not_answer_is_unknown()
    {
        var provider = new FakeProvider(new[] { "NET-001", "NET-002", "NET-003" }, (_, _) => Task.FromResult<IReadOnlyList<SecurityCheckResult>>(new[]
        {
            new SecurityCheckResult { Id = "NET-001", Status = Status.Protected },
            new SecurityCheckResult { Id = "NET-003", Status = Status.Checking }
        }));

        var snapshot = await Run(new[] { provider });

        Assert.Equal(Status.Protected, snapshot.Results.Single(r => r.Id == "NET-001").Status);
        foreach (string id in new[] { "NET-002", "NET-003" })
        {
            var unanswered = snapshot.Results.Single(r => r.Id == id);
            Assert.Equal(Status.Unknown, unanswered.Status);
            Assert.Equal(SecurityReasonCodes.NoResult, unanswered.ReasonCode);
        }
    }

    [Fact]
    public async Task A_provider_that_returns_nothing_at_all_leaves_its_checks_unknown()
    {
        var provider = new FakeProvider(new[] { "NET-001" }, (_, _) => Task.FromResult<IReadOnlyList<SecurityCheckResult>>(null!));

        var snapshot = await Run(new[] { provider });

        Assert.Equal(SecurityReasonCodes.NoResult, snapshot.Results.Single().ReasonCode);
    }

    [Fact]
    public async Task Answers_nobody_asked_for_and_ids_outside_the_baseline_are_dropped()
    {
        var log = new ConcurrentQueue<string>();
        var provider = new FakeProvider(new[] { "NET-001", "XYZ-123" }, (_, _) => Task.FromResult<IReadOnlyList<SecurityCheckResult>>(new[]
        {
            new SecurityCheckResult { Id = "net-001", Status = Status.Protected },
            new SecurityCheckResult { Id = "XYZ-123", Status = Status.Protected },
            new SecurityCheckResult { Id = "ACC-007", Status = Status.NotProtected }
        }));

        var snapshot = await Run(new[] { provider }, options: new SecurityAssessmentOptions { Log = log.Enqueue });

        var only = Assert.Single(snapshot.Results);
        Assert.Equal("NET-001", only.Id);
        Assert.Equal(Status.Protected, only.Status);
        Assert.Equal(new[] { "NET-001" }, provider.LastContext!.RequestedIds);
        Assert.Contains(log, line => line.Contains("XYZ-123") && line.Contains("not in the baseline"));
    }

    [Fact]
    public async Task A_check_belongs_to_the_first_provider_that_names_it()
    {
        var first = Answers(Status.Protected, "NET-001");
        var second = Answers(Status.NotProtected, "NET-001", "NET-002");

        var snapshot = await Run(new[] { first, second });

        Assert.Equal(Status.Protected, snapshot.Results.Single(r => r.Id == "NET-001").Status);
        Assert.Equal(new[] { "NET-002" }, second.LastContext!.RequestedIds);
    }

    [Fact]
    public async Task On_Windows_Server_a_client_only_check_is_not_applicable_and_Windows_is_not_asked_for_it()
    {
        var clientOnly = Answers(Status.NotProtected, "END-001");
        var mixed = Answers(Status.Protected, "END-001", "END-002");
        // END-001 goes to the first provider; the second one is left with END-002.
        var snapshot = await Run(new[] { clientOnly, mixed }, machine: Server);

        Assert.Equal(0, clientOnly.Calls);
        var registered = snapshot.Results.Single(r => r.Id == "END-001");
        Assert.Equal(Status.NotApplicable, registered.Status);
        Assert.Equal(SecurityReasonCodes.ServerOs, registered.ReasonCode);
        Assert.Equal(Status.Protected, snapshot.Results.Single(r => r.Id == "END-002").Status);
        Assert.True(snapshot.IsServerOs);
        Assert.True(snapshot.Completed);
    }

    [Fact]
    public async Task The_same_check_is_assessed_on_a_client()
    {
        var provider = Answers(Status.Protected, "END-001");

        var snapshot = await Run(new[] { provider }, machine: Client);

        Assert.Equal(1, provider.Calls);
        Assert.Equal(Status.Protected, snapshot.Results.Single().Status);
    }

    [Fact]
    public async Task No_more_providers_read_at_once_than_the_limit_allows()
    {
        int running = 0, peak = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new SemaphoreSlim(0);
        string[] ids = { "NET-001", "NET-002", "NET-003", "ACC-007", "UPD-SEC-004", "DEV-003" };
        var providers = ids.Select(id => new FakeProvider(new[] { id }, async (context, _) =>
        {
            int now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            started.Release();
            await release.Task;
            Interlocked.Decrement(ref running);
            return new[] { new SecurityCheckResult { Id = context.RequestedIds[0], Status = Status.Protected } };
        })).ToArray();

        var assessment = Run(providers, options: new SecurityAssessmentOptions { MaxConcurrency = 2, ProviderTimeout = Never });
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(10)));
        // The limit is reached; a third provider must not start while two are reading.
        Assert.False(await started.WaitAsync(TimeSpan.FromMilliseconds(300)));
        release.SetResult();
        var snapshot = await assessment;

        Assert.Equal(2, peak);
        Assert.Equal(6, snapshot.Results.Count);
        Assert.All(snapshot.Results, r => Assert.Equal(Status.Protected, r.Status));
    }

    [Fact]
    public async Task A_provider_that_blocks_its_thread_does_not_block_the_caller()
    {
        using var unblock = new ManualResetEventSlim(false);
        var provider = new FakeProvider(new[] { "NET-001" }, (context, _) =>
        {
            unblock.Wait(Never);
            return Task.FromResult<IReadOnlyList<SecurityCheckResult>>(new[] { new SecurityCheckResult { Id = "NET-001", Status = Status.Protected } });
        });

        // The call itself returns while the provider is still blocked: this is what keeps a
        // window responsive when the engine is started from its thread.
        var assessment = Run(new[] { provider }, options: new SecurityAssessmentOptions { ProviderTimeout = Never });
        Assert.False(assessment.IsCompleted);
        unblock.Set();
        var snapshot = await assessment;

        Assert.Equal(Status.Protected, snapshot.Results.Single().Status);
    }

    [Fact]
    public async Task Each_check_is_reported_once_as_it_answers()
    {
        var progress = new Collector();

        var snapshot = await Run(new[] { Answers(Status.Protected, "NET-001", "NET-002"), Throws(new Exception("x"), "ACC-007") }, progress: progress);

        Assert.Equal(new[] { "ACC-007", "NET-001", "NET-002" }, progress.Seen.Select(r => r.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.Equal(snapshot.Results.Count, progress.Seen.Count);
    }

    [Fact]
    public async Task A_listener_or_a_log_that_fails_does_not_lose_the_assessment()
    {
        var progress = new Collector(_ => throw new InvalidOperationException("listener"));
        var options = new SecurityAssessmentOptions { Log = _ => throw new IOException("disk full") };

        var snapshot = await Run(new[] { Answers(Status.Protected, "NET-001") }, options: options, progress: progress);

        Assert.True(snapshot.Completed);
        Assert.Equal(Status.Protected, snapshot.Results.Single().Status);
    }

    [Fact]
    public async Task The_log_tells_start_end_duration_and_failures_and_never_a_detected_value()
    {
        var log = new ConcurrentQueue<string>();
        var provider = new FakeProvider(new[] { "END-004" }, (_, _) => Task.FromResult<IReadOnlyList<SecurityCheckResult>>(new[]
        {
            new SecurityCheckResult { Id = "END-004", Status = Status.Protected, DetectedCode = SecurityStateCodes.Value, DetectedValue = "DETECTED-VALUE-1", Detail = "DETAIL-TEXT-2" }
        }));

        await Run(new ISecurityCheckProvider[] { provider, Throws(new InvalidOperationException("EXCEPTION-TEXT-3"), "NET-001") },
            options: new SecurityAssessmentOptions { Log = log.Enqueue });

        string all = string.Join("\n", log);
        Assert.Contains("Security assessment started: baseline=UpdateExpress.SecurityBaseline.v1", all);
        Assert.Contains("Security assessment completed: durationMs=", all);
        Assert.Contains("protected=1", all);
        Assert.Contains("unknownOrUnsupported=1", all);
        Assert.Contains("Security check failed: provider=Fake; ids=NET-001; reason=provider_error; error=InvalidOperationException", all);
        Assert.DoesNotContain("DETECTED-VALUE-1", all);
        Assert.DoesNotContain("DETAIL-TEXT-2", all);
        Assert.DoesNotContain("EXCEPTION-TEXT-3", all);
        Assert.DoesNotContain("TEST-PC", all);
    }

    [Fact]
    public async Task A_cancelled_assessment_is_logged_as_cancelled()
    {
        var log = new ConcurrentQueue<string>();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Run(new[] { Answers(Status.Protected, "NET-001") }, options: new SecurityAssessmentOptions { Log = log.Enqueue }, cancellationToken: cancel.Token);

        Assert.Contains(log, line => line.StartsWith("Security assessment cancelled:"));
    }

    [Fact]
    public async Task Free_text_from_a_provider_is_cleaned_before_it_is_kept()
    {
        var provider = new FakeProvider(new[] { "ACC-007" }, (_, _) => Task.FromResult<IReadOnlyList<SecurityCheckResult>>(new[]
        {
            new SecurityCheckResult
            {
                Id = "ACC-007", Status = Status.NeedsAttention, DetectedCode = " ",
                DetectedValue = "DefaultPassword=hunter2",
                Detail = "Recovery password 111111-222222-333333-444444-555555-666666-777777-888888\u0007"
            }
        }));

        var result = (await Run(new[] { provider })).Results.Single();

        Assert.DoesNotContain("hunter2", result.DetectedValue);
        Assert.DoesNotContain("111111-222222", result.Detail);
        Assert.DoesNotContain('\u0007', result.Detail);
        Assert.Equal(SecurityStateCodes.Unknown, result.DetectedCode);
        Assert.Equal(Status.NeedsAttention, result.Status);
    }

    [Fact]
    public async Task The_provider_is_told_the_machine_the_baseline_and_the_time()
    {
        var at = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.FromHours(1));
        var provider = Answers(Status.Protected, "NET-001");

        await Run(new[] { provider }, machine: Server, options: new SecurityAssessmentOptions { Clock = () => at });

        Assert.Same(Baseline, provider.LastContext!.Baseline);
        Assert.Same(Server, provider.LastContext.Machine);
        Assert.Equal(at, provider.LastContext.Now);
    }

    [Fact]
    public async Task No_providers_gives_an_empty_assessment_that_reads_as_not_assessed()
    {
        var snapshot = await Run(Array.Empty<ISecurityCheckProvider>());

        Assert.Empty(snapshot.Results);
        Assert.Equal(SecurityOverallLevel.Unknown, SecurityOverallStatus.Evaluate(snapshot, snapshot.CompletedAt).Level);
    }

    [Fact]
    public async Task A_timed_out_assessment_is_whole_and_shows_orange_or_green_never_red()
    {
        var snapshot = await Run(
            new ISecurityCheckProvider[] { Hangs(TimeSpan.FromMilliseconds(100), "NET-001"), Throws(new Exception("x"), "NET-002") },
            options: new SecurityAssessmentOptions { ProviderTimeout = Never });

        var overall = SecurityOverallStatus.Evaluate(snapshot, snapshot.CompletedAt);

        Assert.NotEqual(SecurityOverallLevel.Red, overall.Level);
        Assert.Equal(0, overall.Summary.NeedsAttention);
        Assert.Equal(2, overall.Summary.UnknownOrUnsupported);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        do { seen = Volatile.Read(ref target); }
        while (value > seen && Interlocked.CompareExchange(ref target, value, seen) != seen);
    }

    /// <summary>Reports on the calling thread, unlike Progress&lt;T&gt;, so a test sees every report.</summary>
    private sealed class Collector : IProgress<SecurityCheckResult>
    {
        private readonly Action<SecurityCheckResult>? _then;
        public Collector(Action<SecurityCheckResult>? then = null) => _then = then;
        public ConcurrentQueue<SecurityCheckResult> Seen { get; } = new();
        public void Report(SecurityCheckResult value)
        {
            Seen.Enqueue(value);
            _then?.Invoke(value);
        }
    }
}
