using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using UpdateExpress.Services.Security;
using Xunit;
using Status = UpdateExpress.Services.Security.SecurityAssessmentStatus;

namespace UpdateExpress.Tests;

/// <summary>
/// The runner and the window read a few process-wide settings (where the result is kept,
/// where log lines go, which checks exist). Tests that replace them run one at a time.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SecurityOverviewRuntimeCollection
{
    public const string Name = "Security Overview runtime";
}

/// <summary>
/// Points the Security Overview at a temp folder, fake checks and in-memory logs, and puts
/// everything back afterwards. Nothing of the signed-in user's own data is read or written.
/// </summary>
internal sealed class SecurityOverviewSandbox : IDisposable
{
    private readonly string? _path = SecurityResultCache.PathOverride;
    private readonly Action<string> _log = SecurityAssessmentRunner.Log;
    private readonly Action<string, string, object?> _activity = SecurityAssessmentRunner.Activity;
    private readonly Func<SecurityMachineInfo> _machine = SecurityAssessmentRunner.ReadMachine;
    private readonly Func<IReadOnlyList<ISecurityCheckProvider>> _providers = SecurityAssessmentRunner.Providers;

    internal SecurityOverviewSandbox(params ISecurityCheckProvider[] providers)
    {
        Folder = Path.Combine(Path.GetTempPath(), "ue-secov-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        CachePath = Path.Combine(Folder, SecurityResultCache.FileName);
        SecurityResultCache.PathOverride = CachePath;
        SecurityAssessmentRunner.Log = line => { lock (LogLines) LogLines.Add(line); };
        SecurityAssessmentRunner.Activity = (action, status, detail) =>
        {
            lock (Activities) Activities.Add((action, status, JsonSerializer.Serialize(detail)));
        };
        SecurityAssessmentRunner.ReadMachine = () => new SecurityMachineInfo
        {
            ComputerName = "TEST-PC", OsName = "Windows 11 Pro 25H2", OsVersion = "25H2", OsBuild = 26200,
            IsServerOs = false, IsElevated = true, AppVersion = "0.7.2.63"
        };
        SecurityAssessmentRunner.Providers = () => providers;
    }

    internal string Folder { get; }
    internal string CachePath { get; }
    internal List<string> LogLines { get; } = new();
    internal List<(string Action, string Status, string Detail)> Activities { get; } = new();

    public void Dispose()
    {
        SecurityResultCache.PathOverride = _path;
        SecurityAssessmentRunner.Log = _log;
        SecurityAssessmentRunner.Activity = _activity;
        SecurityAssessmentRunner.ReadMachine = _machine;
        SecurityAssessmentRunner.Providers = _providers;
        try { Directory.Delete(Folder, recursive: true); } catch { /* a temp folder left behind harms nothing */ }
    }
}

internal sealed class FakeSecurityProvider : ISecurityCheckProvider
{
    private readonly Func<SecurityCheckContext, CancellationToken, Task<IReadOnlyList<SecurityCheckResult>>> _assess;

    internal FakeSecurityProvider(string[] ids, Func<SecurityCheckContext, CancellationToken, Task<IReadOnlyList<SecurityCheckResult>>> assess)
    {
        CheckIds = ids;
        _assess = assess;
    }

    public string Name => "Fake";
    public IReadOnlyList<string> CheckIds { get; }
    public int Calls;

    public Task<IReadOnlyList<SecurityCheckResult>> AssessAsync(SecurityCheckContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        return _assess(context, cancellationToken);
    }

    internal static FakeSecurityProvider Answers(Status status, params string[] ids) =>
        new(ids, (context, _) => Task.FromResult<IReadOnlyList<SecurityCheckResult>>(context.RequestedIds
            .Select(id => new SecurityCheckResult
            {
                Id = id, Status = status,
                DetectedCode = status == Status.Protected ? SecurityStateCodes.On : status == Status.NotProtected ? SecurityStateCodes.Off : SecurityStateCodes.Unknown,
                Detail = "DETAIL-OF-" + id
            }).ToArray()));

    /// <summary>Answers "protected" once the test lets it.</summary>
    internal static FakeSecurityProvider Waits(Task gate, params string[] ids) =>
        new(ids, async (context, _) =>
        {
            await gate.ConfigureAwait(false);
            return context.RequestedIds.Select(id => new SecurityCheckResult { Id = id, Status = Status.Protected, DetectedCode = SecurityStateCodes.On }).ToArray();
        });

    internal static FakeSecurityProvider Throws(Exception exception, params string[] ids) => new(ids, (_, _) => throw exception);
}

[Collection(SecurityOverviewRuntimeCollection.Name)]
public class SecurityAssessmentRunnerTests
{
    private static readonly SecurityBaseline Baseline = SecurityBaselineV1.Create();

    [Fact]
    public async Task A_finished_assessment_is_stored_and_logged_with_counts_only()
    {
        using var sandbox = new SecurityOverviewSandbox(
            FakeSecurityProvider.Answers(Status.Protected, "NET-001", "NET-002"),
            FakeSecurityProvider.Answers(Status.NotProtected, "NET-003"));

        var snapshot = await SecurityAssessmentRunner.RunAsync(Baseline, null, CancellationToken.None);

        Assert.True(snapshot.Completed);
        var stored = SecurityResultCache.TryLoad(sandbox.CachePath);
        Assert.NotNull(stored);
        Assert.Equal(new[] { "NET-001", "NET-002", "NET-003" }, stored!.Results.Select(r => r.Id).ToArray());

        var activity = Assert.Single(sandbox.Activities);
        Assert.Equal(SecurityAssessmentRunner.ActivityAction, activity.Action);
        Assert.Equal("Completed", activity.Status);
        using var detail = JsonDocument.Parse(activity.Detail);
        Assert.Equal(SecurityBaselineV1.BaselineId, detail.RootElement.GetProperty("Baseline").GetString());
        Assert.Equal(3, detail.RootElement.GetProperty("Checks").GetInt32());
        Assert.Equal(2, detail.RootElement.GetProperty("Protected").GetInt32());
        Assert.Equal(1, detail.RootElement.GetProperty("NeedsAttention").GetInt32());
        Assert.True(detail.RootElement.GetProperty("Saved").GetBoolean());

        Assert.Contains(sandbox.LogLines, line => line.StartsWith("Security assessment started", StringComparison.Ordinal));
        Assert.Contains(sandbox.LogLines, line => line.StartsWith("Security assessment completed", StringComparison.Ordinal));
        // What a check found stays in the result; the logs carry counts.
        foreach (string text in sandbox.LogLines.Append(activity.Detail))
        {
            Assert.DoesNotContain("DETAIL-OF-", text);
            Assert.DoesNotContain("TEST-PC", text);
        }
    }

    [Fact]
    public async Task A_cancelled_assessment_is_returned_but_never_replaces_the_stored_one()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sandbox = new SecurityOverviewSandbox(FakeSecurityProvider.Waits(gate.Task, "NET-001"));
        var earlier = new SecurityAssessmentSnapshot
        {
            BaselineId = SecurityBaselineV1.BaselineId, Completed = true,
            StartedAt = DateTimeOffset.Now.AddHours(-1), CompletedAt = DateTimeOffset.Now.AddHours(-1),
            Results = new[] { new SecurityCheckResult { Id = "NET-001", Status = Status.Protected, CheckedAt = DateTimeOffset.Now.AddHours(-1) } }
        };
        Assert.True(SecurityResultCache.TrySave(earlier, sandbox.CachePath));
        using var cancel = new CancellationTokenSource();

        var running = SecurityAssessmentRunner.RunAsync(Baseline, null, cancel.Token);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(100));
        var snapshot = await running.WaitAsync(TimeSpan.FromSeconds(20));
        gate.SetResult();

        Assert.False(snapshot.Completed);
        Assert.Equal(Status.Protected, SecurityResultCache.TryLoad(sandbox.CachePath)!.Results.Single().Status);
        var activity = Assert.Single(sandbox.Activities);
        Assert.Equal("Cancelled", activity.Status);
        using var detail = JsonDocument.Parse(activity.Detail);
        Assert.False(detail.RootElement.GetProperty("Saved").GetBoolean());
    }

    [Fact]
    public async Task An_assessment_in_which_no_check_answers_does_not_replace_the_stored_one()
    {
        using var sandbox = new SecurityOverviewSandbox();
        var earlier = new SecurityAssessmentSnapshot
        {
            BaselineId = SecurityBaselineV1.BaselineId, Completed = true, StartedAt = DateTimeOffset.Now, CompletedAt = DateTimeOffset.Now,
            Results = new[] { new SecurityCheckResult { Id = "NET-001", Status = Status.Protected, CheckedAt = DateTimeOffset.Now } }
        };
        Assert.True(SecurityResultCache.TrySave(earlier, sandbox.CachePath));

        var snapshot = await SecurityAssessmentRunner.RunAsync(Baseline, null, CancellationToken.None);

        Assert.Empty(snapshot.Results);
        Assert.Single(SecurityResultCache.TryLoad(sandbox.CachePath)!.Results);
    }

    [Fact]
    public async Task The_stored_assessment_of_this_PC_and_this_baseline_is_shown_again()
    {
        using var sandbox = new SecurityOverviewSandbox(FakeSecurityProvider.Answers(Status.Protected, "NET-001"));
        await SecurityAssessmentRunner.RunAsync(Baseline, null, CancellationToken.None);

        var stored = SecurityAssessmentRunner.LoadStored(Baseline, sandbox.CachePath, "test-pc ");

        Assert.NotNull(stored);
        Assert.Equal(Status.Protected, stored!.Results.Single().Status);
    }

    [Theory]
    [InlineData("OTHER-PC")]
    [InlineData("TEST-PC2")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_assessment_stored_on_another_PC_is_not_shown_as_this_one(string thisPc)
    {
        using var sandbox = new SecurityOverviewSandbox(FakeSecurityProvider.Answers(Status.Protected, "NET-001"));
        await SecurityAssessmentRunner.RunAsync(Baseline, null, CancellationToken.None);
        Assert.NotNull(SecurityResultCache.TryLoad(sandbox.CachePath));

        Assert.Null(SecurityAssessmentRunner.LoadStored(Baseline, sandbox.CachePath, thisPc));
    }

    [Fact]
    public void An_assessment_stored_without_a_computer_name_or_for_another_baseline_is_not_shown()
    {
        using var sandbox = new SecurityOverviewSandbox();
        var result = new[] { new SecurityCheckResult { Id = "NET-001", Status = Status.Protected, CheckedAt = DateTimeOffset.Now } };
        var unnamed = new SecurityAssessmentSnapshot
        {
            BaselineId = SecurityBaselineV1.BaselineId, Completed = true, StartedAt = DateTimeOffset.Now, CompletedAt = DateTimeOffset.Now,
            Results = result
        };
        var otherBaseline = unnamed with { BaselineId = "UpdateExpress.SecurityBaseline.v0", ComputerName = "TEST-PC" };

        Assert.True(SecurityResultCache.TrySave(unnamed, sandbox.CachePath));
        Assert.Null(SecurityAssessmentRunner.LoadStored(Baseline, sandbox.CachePath, "TEST-PC"));
        Assert.Null(SecurityAssessmentRunner.LoadStored(Baseline, sandbox.CachePath, ""));

        Assert.True(SecurityResultCache.TrySave(otherBaseline, sandbox.CachePath));
        Assert.Null(SecurityAssessmentRunner.LoadStored(Baseline, sandbox.CachePath, "TEST-PC"));

        Assert.True(SecurityResultCache.TrySave(otherBaseline with { BaselineId = SecurityBaselineV1.BaselineId }, sandbox.CachePath));
        Assert.NotNull(SecurityAssessmentRunner.LoadStored(Baseline, sandbox.CachePath, "TEST-PC"));
    }

    [Fact]
    public void No_stored_file_is_no_stored_assessment()
    {
        using var sandbox = new SecurityOverviewSandbox();

        Assert.Null(SecurityAssessmentRunner.LoadStored(Baseline, sandbox.CachePath, "TEST-PC"));
        Assert.Null(SecurityAssessmentRunner.LoadStored(Baseline));
    }

    [Fact]
    public async Task A_result_that_cannot_be_stored_is_still_returned_and_the_failure_is_logged()
    {
        using var sandbox = new SecurityOverviewSandbox(FakeSecurityProvider.Answers(Status.Protected, "NET-001"));
        // A folder where the file should be: the save fails.
        Directory.CreateDirectory(sandbox.CachePath);

        var snapshot = await SecurityAssessmentRunner.RunAsync(Baseline, null, CancellationToken.None);

        Assert.True(snapshot.Completed);
        Assert.Equal(Status.Protected, snapshot.Results.Single().Status);
        Assert.Contains(sandbox.LogLines, line => line.Contains("could not be saved", StringComparison.Ordinal));
        using var detail = JsonDocument.Parse(Assert.Single(sandbox.Activities).Detail);
        Assert.False(detail.RootElement.GetProperty("Saved").GetBoolean());
    }

    [Fact]
    public async Task A_log_that_fails_does_not_fail_the_assessment()
    {
        using var sandbox = new SecurityOverviewSandbox(FakeSecurityProvider.Answers(Status.Protected, "NET-001"));
        SecurityAssessmentRunner.Log = _ => throw new IOException("disk full");
        SecurityAssessmentRunner.Activity = (_, _, _) => throw new IOException("disk full");

        var snapshot = await SecurityAssessmentRunner.RunAsync(Baseline, null, CancellationToken.None);

        Assert.True(snapshot.Completed);
    }

    [Fact]
    public void This_version_registers_only_checks_that_the_baseline_knows()
    {
        var known = Baseline.Definitions.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in SecurityCheckProviders.Create())
            foreach (string id in provider.CheckIds)
            {
                Assert.Contains(id, known);
                Assert.True(claimed.Add(id), id + " is answered by two providers");
            }
    }
}

[Collection(SecurityOverviewRuntimeCollection.Name)]
public class SecurityOverviewWindowTests
{
    private static readonly DateTimeOffset Earlier = DateTimeOffset.Now.AddHours(-3);

    private static string Time(DateTime time) => time.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    private static SecurityAssessmentSnapshot EarlierSnapshot(params (string Id, Status Status)[] results) => new()
    {
        BaselineId = SecurityBaselineV1.BaselineId, Completed = true, StartedAt = Earlier, CompletedAt = Earlier,
        Results = results.Select(r => new SecurityCheckResult
        {
            Id = r.Id, Status = r.Status, CheckedAt = Earlier,
            DetectedCode = r.Status == Status.NotProtected ? SecurityStateCodes.Off : SecurityStateCodes.On
        }).ToArray()
    };

    /// <summary>The window is built and driven, but never shown: no window appears on the desktop.</summary>
    private static SecurityOverviewWindow Window(SecurityAssessmentSnapshot? earlier = null, bool norwegian = false) =>
        new(norwegian, earlier, Time) { ScanOnOpen = false };

    private static SecurityCheckRowView Row(SecurityOverviewWindow window, string id) => window.Rows.Single(r => r.Definition.Id == id);

    private static string Card(SecurityOverviewWindow window, int index) =>
        AutomationProperties.GetName(((System.Windows.Controls.Primitives.UniformGrid)window.FindName("CardsHost")).Children[index]);

    private static string OverallText(SecurityOverviewWindow window) => ((TextBlock)window.FindName("OverallText")).Text;

    private static string ScanButton(SecurityOverviewWindow window) =>
        (string)((System.Windows.Controls.Button)((StackPanel)window.FindName("ButtonsHost")).Children[0]).Content;

    /// <summary>Lets the answers that are on their way reach the window.</summary>
    private static async Task Settle()
    {
        for (int i = 0; i < 3; i++)
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        for (int i = 0; i < 400 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), "Timed out waiting for: " + what);
    }

    [Fact]
    public void The_window_opens_on_the_stored_assessment_without_assessing()
    {
        var provider = FakeSecurityProvider.Answers(Status.Protected, "NET-001");
        using var sandbox = new SecurityOverviewSandbox(provider);

        RunSta(async () =>
        {
            var window = Window(EarlierSnapshot(("NET-001", Status.Protected), ("NET-003", Status.NotProtected)));
            await Settle();

            Assert.Equal("Security Overview", window.Title);
            Assert.False(window.IsScanning);
            Assert.Equal(0, provider.Calls);
            Assert.Equal(Status.Protected, Row(window, "NET-001").Text.Status);
            Assert.Equal(Status.NotProtected, Row(window, "NET-003").Text.Status);
            // A check the stored assessment does not hold is unknown, and counted as unknown.
            Assert.Equal(Status.Unknown, Row(window, "END-002").Text.Status);
            Assert.Equal("Protected: 1", Card(window, 0));
            Assert.Equal("Needs attention: 1", Card(window, 1));
            Assert.Equal("Manual review: 0", Card(window, 2));
            Assert.Equal("Unknown or unsupported: " + (window.Rows.Count - 2), Card(window, 3));
            Assert.Equal("At least one check is not protected. Last assessed: " + Time(Earlier.LocalDateTime), OverallText(window));
            Assert.Equal("Scan again", ScanButton(window));
            window.Close();
        });
    }

    [Fact]
    public void Without_a_stored_assessment_nothing_is_shown_as_good_or_bad()
    {
        using var sandbox = new SecurityOverviewSandbox();

        RunSta(async () =>
        {
            var window = Window();
            await Settle();

            Assert.All(window.Rows, row => Assert.Equal(Status.Unknown, row.Text.Status));
            Assert.Equal("Protected: 0", Card(window, 0));
            Assert.Equal("Needs attention: 0", Card(window, 1));
            Assert.Equal("Unknown or unsupported: " + window.Rows.Count, Card(window, 3));
            Assert.Equal("Not assessed yet.", OverallText(window));
            window.Close();
        });
    }

    [Fact]
    public void A_fresh_assessment_replaces_the_stored_answers_is_stored_and_reported_once()
    {
        using var sandbox = new SecurityOverviewSandbox(FakeSecurityProvider.Answers(Status.Protected, "NET-001", "NET-002", "NET-003"));

        RunSta(async () =>
        {
            var window = Window(EarlierSnapshot(("NET-003", Status.NotProtected)));
            var reported = new List<SecurityAssessmentSnapshot>();
            window.AssessmentCompleted += reported.Add;

            await window.StartScanAsync();
            await Settle();

            Assert.False(window.IsScanning);
            Assert.Equal(Status.Protected, Row(window, "NET-003").Text.Status);
            Assert.False(Row(window, "NET-003").Checking);
            Assert.Equal("Protected: 3", Card(window, 0));
            Assert.Equal("Needs attention: 0", Card(window, 1));
            Assert.StartsWith("No check found a problem. Last assessed: ", OverallText(window));
            Assert.Equal("Scan again", ScanButton(window));
            Assert.Same(window.LastCompleted, Assert.Single(reported));
            Assert.Equal(3, SecurityResultCache.TryLoad(sandbox.CachePath)!.Results.Count);
            window.Close();
        });
    }

    [Fact]
    public void An_assessment_without_any_answer_keeps_the_last_whole_one_on_screen_and_reports_nothing()
    {
        // No provider at all: the engine finishes with nothing to say.
        using var sandbox = new SecurityOverviewSandbox();

        RunSta(async () =>
        {
            var earlier = EarlierSnapshot(("NET-001", Status.Protected), ("NET-003", Status.NotProtected));
            var window = Window(earlier);
            bool reported = false;
            window.AssessmentCompleted += _ => reported = true;

            await window.StartScanAsync();
            await Settle();

            Assert.False(window.IsScanning);
            Assert.False(reported);
            Assert.Same(earlier, window.LastCompleted);
            Assert.Equal(Status.Protected, Row(window, "NET-001").Text.Status);
            Assert.Equal(Status.NotProtected, Row(window, "NET-003").Text.Status);
            Assert.False(Row(window, "NET-003").Checking);
            Assert.Equal("Needs attention: 1", Card(window, 1));
            // Nobody cancelled, so the line says what it said before.
            Assert.Equal("At least one check is not protected. Last assessed: " + Time(Earlier.LocalDateTime), OverallText(window));
            Assert.Equal("Scan again", ScanButton(window));
            Assert.False(File.Exists(sandbox.CachePath));
            window.Close();
        });
    }

    [Fact]
    public void A_first_assessment_without_any_answer_stays_not_assessed()
    {
        using var sandbox = new SecurityOverviewSandbox();

        RunSta(async () =>
        {
            var window = Window();
            bool reported = false;
            window.AssessmentCompleted += _ => reported = true;

            await window.StartScanAsync();
            await Settle();

            Assert.False(reported);
            Assert.Null(window.LastCompleted);
            Assert.All(window.Rows, row => Assert.Equal(Status.Unknown, row.Text.Status));
            Assert.Equal("Not assessed yet.", OverallText(window));
            window.Close();
        });
    }

    [Fact]
    public void While_an_assessment_runs_earlier_answers_stay_and_the_rest_reads_checking()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sandbox = new SecurityOverviewSandbox(
            FakeSecurityProvider.Answers(Status.Protected, "NET-001"),
            FakeSecurityProvider.Waits(gate.Task, "NET-002", "NET-003"));

        RunSta(async () =>
        {
            var window = Window(EarlierSnapshot(("NET-003", Status.NotProtected)));

            var scan = window.StartScanAsync();
            await Until(() => !Row(window, "NET-001").Checking, "the quick check's answer");

            Assert.True(window.IsScanning);
            Assert.Equal("Cancel", ScanButton(window));
            Assert.Equal(Status.Protected, Row(window, "NET-001").Text.Status);
            Assert.Equal(Status.Checking, Row(window, "NET-002").Text.Status);
            // The earlier answer is still what the row says, marked as being checked again.
            Assert.Equal(Status.NotProtected, Row(window, "NET-003").Text.Status);
            Assert.Equal("Not protected · Checking…", Row(window, "NET-003").Text.StatusText);
            Assert.StartsWith("Checking… 1 of " + window.Rows.Count, OverallText(window));
            Assert.Contains("Results from " + Time(Earlier.LocalDateTime) + " are shown until each check has answered.", OverallText(window));

            gate.SetResult();
            await scan;
            await Settle();
            Assert.Equal(Status.Protected, Row(window, "NET-003").Text.Status);
            window.Close();
        });
    }

    [Fact]
    public void A_cancelled_assessment_goes_back_to_the_last_whole_one_and_stores_nothing()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sandbox = new SecurityOverviewSandbox(
            FakeSecurityProvider.Answers(Status.Protected, "NET-001"),
            FakeSecurityProvider.Waits(gate.Task, "NET-003"));

        RunSta(async () =>
        {
            var earlier = EarlierSnapshot(("NET-001", Status.NotProtected), ("NET-003", Status.NotProtected));
            var window = Window(earlier);
            bool reported = false;
            window.AssessmentCompleted += _ => reported = true;

            var scan = window.StartScanAsync();
            await Until(() => !Row(window, "NET-001").Checking, "the quick check's answer");
            window.CancelScan();
            await scan;
            await Settle();
            gate.SetResult();

            Assert.False(window.IsScanning);
            Assert.False(reported);
            Assert.Same(earlier, window.LastCompleted);
            // Also the check that did answer: the picture shown is one whole assessment.
            Assert.Equal(Status.NotProtected, Row(window, "NET-001").Text.Status);
            Assert.Equal(Status.NotProtected, Row(window, "NET-003").Text.Status);
            Assert.StartsWith("The assessment was cancelled. Showing the assessment from " + Time(Earlier.LocalDateTime) + ".", OverallText(window));
            Assert.Equal("Scan again", ScanButton(window));
            Assert.False(File.Exists(sandbox.CachePath));
            window.Close();
        });
    }

    [Fact]
    public void A_cancelled_first_assessment_leaves_the_unanswered_checks_unknown()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sandbox = new SecurityOverviewSandbox(FakeSecurityProvider.Waits(gate.Task, "NET-001", "NET-002"));

        RunSta(async () =>
        {
            var window = Window();

            var scan = window.StartScanAsync();
            await Until(() => window.IsScanning, "the assessment to start");
            window.CancelScan();
            await scan;
            await Settle();
            gate.SetResult();

            Assert.Null(window.LastCompleted);
            Assert.All(window.Rows, row => Assert.Equal(Status.Unknown, row.Text.Status));
            Assert.Equal("The assessment was cancelled before this check finished.", Row(window, "NET-001").Text.Finding);
            Assert.Equal("Needs attention: 0", Card(window, 1));
            Assert.Equal("Unknown or unsupported: " + window.Rows.Count, Card(window, 3));
            Assert.StartsWith("The assessment was cancelled. Not assessed yet.", OverallText(window));
            window.Close();
        });
    }

    [Fact]
    public void A_check_that_fails_to_run_is_shown_as_a_failed_check_not_as_a_bad_setting()
    {
        using var sandbox = new SecurityOverviewSandbox(
            FakeSecurityProvider.Throws(new UnauthorizedAccessException("Access is denied"), "NET-001"),
            FakeSecurityProvider.Answers(Status.Protected, "NET-002"));

        RunSta(async () =>
        {
            var window = Window();

            await window.StartScanAsync();
            await Settle();

            var failed = Row(window, "NET-001");
            Assert.Equal(Status.Error, failed.Text.Status);
            Assert.Equal("Check failed", failed.Text.StatusText);
            Assert.Equal("Unknown", failed.Text.Detected);
            Assert.Equal("The check could not run.", failed.Text.Finding);
            Assert.Equal("Needs attention: 0", Card(window, 1));
            Assert.Equal("Unknown or unsupported: " + (window.Rows.Count - 1), Card(window, 3));
            window.Close();
        });
    }

    [Fact]
    public void Closing_the_window_stops_a_running_assessment_and_reports_nothing()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sandbox = new SecurityOverviewSandbox(FakeSecurityProvider.Waits(gate.Task, "NET-001"));

        RunSta(async () =>
        {
            var window = Window();
            bool reported = false;
            window.AssessmentCompleted += _ => reported = true;

            var scan = window.StartScanAsync();
            await Until(() => window.IsScanning, "the assessment to start");
            window.Close();
            await scan.WaitAsync(TimeSpan.FromSeconds(20));
            gate.SetResult();
            await Settle();

            Assert.False(reported);
            Assert.False(File.Exists(sandbox.CachePath));
            Assert.Equal("Cancelled", Assert.Single(sandbox.Activities).Status);
        });
    }

    [Fact]
    public void A_second_start_while_one_runs_does_not_start_another()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = FakeSecurityProvider.Waits(gate.Task, "NET-001");
        using var sandbox = new SecurityOverviewSandbox(provider);

        RunSta(async () =>
        {
            var window = Window();

            var first = window.StartScanAsync();
            var second = window.StartScanAsync();
            gate.SetResult();
            await first;
            await Settle();

            Assert.Same(first, second);
            Assert.Equal(1, provider.Calls);
            window.Close();
        });
    }

    [Fact]
    public void The_category_filter_shows_the_checks_of_one_category()
    {
        using var sandbox = new SecurityOverviewSandbox();

        RunSta(async () =>
        {
            var window = Window();
            var chips = ((WrapPanel)window.FindName("ChipsHost")).Children.OfType<System.Windows.Controls.RadioButton>().ToList();
            var list = (ItemsControl)window.FindName("ChecksHost");
            var empty = (TextBlock)window.FindName("EmptyCategoryText");
            await Settle();

            // All, then the six categories of the specification.
            Assert.Equal(7, chips.Count);
            Assert.Equal("All (" + window.Rows.Count + ")", chips[0].Content);
            Assert.True(chips[0].IsChecked);
            Assert.Equal(window.Rows.Count, list.Items.Count);

            chips[1].IsChecked = true;
            Assert.Equal("Network Security (3)", chips[1].Content);
            Assert.Equal(new[] { "NET-001", "NET-002", "NET-003" }, list.Items.Cast<SecurityCheckRowView>().Select(r => r.Definition.Id).ToArray());
            Assert.Equal(System.Windows.Visibility.Collapsed, empty.Visibility);

            // Phase 1 has no check in the last category; the list says so instead of standing empty.
            chips[6].IsChecked = true;
            Assert.Equal("Logging and Application Security (0)", chips[6].Content);
            Assert.True(list.Items.IsEmpty);
            Assert.Equal(System.Windows.Visibility.Visible, empty.Visibility);

            chips[0].IsChecked = true;
            Assert.Equal(window.Rows.Count, list.Items.Count);
            window.Close();
        });
    }

    [Fact]
    public void The_window_reads_in_Norwegian()
    {
        using var sandbox = new SecurityOverviewSandbox();

        RunSta(async () =>
        {
            var window = Window(EarlierSnapshot(("NET-001", Status.Protected)), norwegian: true);
            await Settle();

            Assert.Equal("Sikkerhetsoversikt", window.Title);
            Assert.Equal("Øyeblikksbilde av en vurdering – ikke en sikkerhetssertifisering.", ((TextBlock)window.FindName("HonestyText")).Text);
            Assert.Equal("Beskyttet: 1", Card(window, 0));
            Assert.Equal("Skann på nytt", ScanButton(window));
            var row = Row(window, "NET-001");
            Assert.Equal("Windows-brannmur – domeneprofil", row.Text.Name);
            Assert.Equal("Detaljer for Windows-brannmur – domeneprofil", row.DetailsName);
            Assert.Equal(new[] { "Nåværende tilstand", "Anbefalt", "Policy", "Sjekket", "Utbedring", "Omstart" }, row.Facts.Select(f => f.Label).ToArray());
            Assert.Contains(row.Details, d => d.Label == "Hvorfor det betyr noe" && d.Value.StartsWith("Microsoft anbefaler", StringComparison.Ordinal));
            Assert.Contains(row.Details, d => d.Label == "Sjekk-ID" && d.Value == "NET-001");
            window.Close();
        });
    }

    [Fact]
    public void Details_open_and_close_per_check()
    {
        using var sandbox = new SecurityOverviewSandbox();

        RunSta(async () =>
        {
            var window = Window();
            await Settle();
            var row = Row(window, "ACC-007");

            Assert.Equal(System.Windows.Visibility.Collapsed, row.DetailsVisibility);
            row.IsExpanded = true;
            Assert.Equal(System.Windows.Visibility.Visible, row.DetailsVisibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, Row(window, "NET-001").DetailsVisibility);
            Assert.Equal("https", row.ReferenceUri!.Scheme);
            Assert.Equal(System.Windows.Visibility.Visible, row.LinkVisibility);
            // UPD-SEC-004 is Update Express's own rule and has no page to point to.
            Assert.Equal(System.Windows.Visibility.Collapsed, Row(window, "UPD-SEC-004").LinkVisibility);
            window.Close();
        });
    }

    /// <summary>Runs the test on a thread of its own with a dispatcher, as the app's window thread has.</summary>
    private static void RunSta(Func<Task> body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            async void Start()
            {
                try { await body(); }
                catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
                finally { frame.Continue = false; }
            }
            dispatcher.BeginInvoke(new Action(Start));
            Dispatcher.PushFrame(frame);
            dispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(90)))
            throw new TimeoutException("The window test did not finish in 90 seconds.");
        failure?.Throw();
    }
}
