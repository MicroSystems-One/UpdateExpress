using UpdateExpress.Services.Security;
using Xunit;
using Xunit.Abstractions;

namespace UpdateExpress.Tests;

/// <summary>
/// A test that reads the machine it runs on. It is skipped unless the environment variable
/// UPDATEEXPRESS_LIVE_READ is 1, because what it finds differs from PC to PC.
/// </summary>
public sealed class LiveReadFactAttribute : FactAttribute
{
    internal const string Variable = "UPDATEEXPRESS_LIVE_READ";

    public LiveReadFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
            Skip = "Reads this machine. Set " + Variable + "=1 to run it.";
    }
}

/// <summary>
/// Runs the registered security checks against this machine and prints what each one found.
/// It only reads, stores nothing and writes no log. Meant for a look at a real PC without
/// opening the app:
/// <c>set UPDATEEXPRESS_LIVE_READ=1</c>, then
/// <c>dotnet test --filter FullyQualifiedName~SecurityLiveReadTests --logger "console;verbosity=detailed"</c>.
/// </summary>
public class SecurityLiveReadTests
{
    private readonly ITestOutputHelper _output;

    public SecurityLiveReadTests(ITestOutputHelper output) => _output = output;

    [LiveReadFact]
    public async Task The_registered_checks_answer_on_this_machine()
    {
        var baseline = SecurityBaselineV1.Create();
        var machine = SecurityMachineInfo.ReadCurrent();
        var providers = SecurityCheckProviders.Create();
        var log = new List<string>();
        var options = new SecurityAssessmentOptions { Log = line => { lock (log) log.Add(line); } };

        var snapshot = await SecurityAssessmentEngine.RunAsync(baseline, providers, machine, options);

        _output.WriteLine($"Machine: {machine.OsName} {machine.OsVersion}, build {machine.OsBuild}, server={machine.IsServerOs}, elevated={machine.IsElevated}");
        _output.WriteLine($"Assessment: completed={snapshot.Completed}, {snapshot.DurationMs} ms, {snapshot.Results.Count} of {baseline.Definitions.Count} checks answered");
        foreach (var result in snapshot.Results)
        {
            var row = SecurityOverviewPresentation.Row(baseline.Find(result.Id)!, result, false, false, time => time.ToString("yyyy-MM-dd HH:mm:ss"));
            _output.WriteLine($"{row.Id} | {row.Name} | {row.StatusText} | state: {row.Detected} | recommended: {row.Recommended} | policy: {row.Policy} | {result.DurationMs} ms");
            if (row.Finding.Length > 0) _output.WriteLine("    finding: " + row.Finding);
            if (row.TechnicalDetail.Length > 0) _output.WriteLine("    detail: " + row.TechnicalDetail);
        }
        var overall = SecurityOverallStatus.Evaluate(snapshot, DateTimeOffset.Now);
        _output.WriteLine($"Overall: {overall.Level} ({SecurityOverviewStrings.Overall(overall, false)})");
        foreach (string line in log) _output.WriteLine("log: " + line);

        // What is true on any machine: every check a provider claims gets an answer, and none is left "checking".
        var claimed = providers.SelectMany(p => p.CheckIds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(claimed.Length, snapshot.Results.Count);
        Assert.DoesNotContain(snapshot.Results, r => r.Status == SecurityAssessmentStatus.Checking);
    }
}
