using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UpdateExpress.Models;

namespace UpdateExpress.Services;

/// <summary>
/// One-shot problem-PC pipeline: deep check → Medium/Deep fix when needed → WUA retry → report.
/// </summary>
internal static class ProblemPcRepair
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    internal static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MicroSystems", "Update Express", "Logs");

    internal static string LastJsonPath => Path.Combine(DirectoryPath, "problem-pc-last.json");
    internal static string LastTextPath => Path.Combine(DirectoryPath, "problem-pc-last.txt");

    internal static async Task<ProblemPcRepairReport> RunAsync(
        IProgress<string>? progress,
        bool includeOptional,
        bool allDrivers,
        CancellationToken cancellationToken = default)
    {
        var report = new ProblemPcRepairReport
        {
            Started = DateTimeOffset.Now,
            AppVersion = ProductVersion.Display,
            Machine = Environment.MachineName,
            Elevated = ElevationHelper.IsElevated
        };

        progress?.Report("1/3 Deep check…");
        var deep = await WuaDeepCheckService.RunAsync(progress, cancellationToken).ConfigureAwait(false);
        report.DeepCheckFail = deep.FailCount;
        report.DeepCheckWarn = deep.WarnCount;
        report.DeepCheckTotal = deep.Findings.Count;
        report.Steps.Add($"Deep check: FAIL={deep.FailCount}, WARN={deep.WarnCount}, total={deep.Findings.Count}");

        bool needsFix = deep.FailCount > 0 || deep.WarnCount > 0;
        if (!ElevationHelper.IsElevated && needsFix)
        {
            report.FixSkipped = true;
            report.Steps.Add("Fix skipped: administrator privileges required.");
        }
        else if (needsFix)
        {
            var level = deep.FailCount > 0 ? WindowsUpdateFixLevel.Deep : WindowsUpdateFixLevel.Medium;
            progress?.Report($"2/3 Windows Update fix ({level})…");
            var fix = await WindowsUpdateFixService.RunAsync(
                level, progress, cancellationToken, includeOptional: includeOptional, allDrivers: allDrivers)
                .ConfigureAwait(false);
            report.FixLevel = level.ToString();
            report.FixCompleted = fix.Completed;
            report.FixHasFailures = fix.HasFailures;
            report.Steps.Add(
                $"Fix {level}: completed={fix.Completed}, failures={fix.HasFailures}, steps={fix.Steps.Count}");
        }
        else
        {
            report.Steps.Add("Fix skipped: deep check found no FAIL/WARN.");
        }

        progress?.Report("3/3 Retry Windows Update search…");
        try
        {
            var pending = await WindowsUpdateReader.RunStaAsync(
                    () => WindowsUpdateReader.AdvancedPending(includeOptional, allDrivers))
                .ConfigureAwait(false);
            report.PendingAfter = pending.Count;
            report.PendingSoftware = pending.Count(UpdateUntilCurrent.IsInCycle);
            report.Steps.Add($"Retry search: {pending.Count} pending ({report.PendingSoftware} in update-until-current scope).");
        }
        catch (Exception ex)
        {
            report.RetryError = ex.Message;
            report.Steps.Add("Retry search failed: " + ex.Message);
        }

        report.Finished = DateTimeOffset.Now;
        report.Completed = true;
        Save(report);
        RmmStatusExport.WriteBestEffort(
            $"problem-pc FAIL={report.DeepCheckFail} WARN={report.DeepCheckWarn} pending={report.PendingAfter}",
            force: true);
        ActivityLog.TryWrite("ProblemPcRepair", "Completed", new
        {
            report.DeepCheckFail,
            report.DeepCheckWarn,
            report.FixLevel,
            report.PendingAfter
        }, mode: "Manual", source: "MainWindow");
        return report;
    }

    private static void Save(ProblemPcRepairReport report)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(LastJsonPath, JsonSerializer.Serialize(report, JsonOpts));
            File.WriteAllText(LastTextPath, report.BuildText(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            AgentLog.Write("Problem PC report save failed: " + ex.Message);
        }
    }
}

internal sealed class ProblemPcRepairReport
{
    public DateTimeOffset Started { get; set; }
    public DateTimeOffset Finished { get; set; }
    public string Machine { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public bool Elevated { get; set; }
    public bool Completed { get; set; }
    public int DeepCheckFail { get; set; }
    public int DeepCheckWarn { get; set; }
    public int DeepCheckTotal { get; set; }
    public string? FixLevel { get; set; }
    public bool FixCompleted { get; set; }
    public bool FixHasFailures { get; set; }
    public bool FixSkipped { get; set; }
    public int PendingAfter { get; set; }
    public int PendingSoftware { get; set; }
    public string? RetryError { get; set; }
    public List<string> Steps { get; set; } = new();

    internal string BuildText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Update Express – Problem PC pipeline");
        sb.AppendLine($"Machine: {Machine}");
        sb.AppendLine($"App: {AppVersion}");
        sb.AppendLine($"Elevated: {Elevated}");
        sb.AppendLine($"Started: {Started:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Finished: {Finished:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Deep check: FAIL={DeepCheckFail} WARN={DeepCheckWarn} total={DeepCheckTotal}");
        if (!string.IsNullOrWhiteSpace(FixLevel))
            sb.AppendLine($"Fix: {FixLevel} completed={FixCompleted} failures={FixHasFailures}");
        if (FixSkipped) sb.AppendLine("Fix: skipped (not elevated)");
        sb.AppendLine($"Pending after retry: {PendingAfter} (software cycle: {PendingSoftware})");
        if (!string.IsNullOrWhiteSpace(RetryError))
            sb.AppendLine("Retry error: " + RetryError);
        sb.AppendLine();
        sb.AppendLine("Steps:");
        foreach (var step in Steps)
            sb.AppendLine("- " + step);
        sb.AppendLine();
        sb.AppendLine("Report files:");
        sb.AppendLine(LastJsonPath);
        sb.AppendLine(LastTextPath);
        return sb.ToString();
    }

    private static string LastJsonPath => ProblemPcRepair.LastJsonPath;
    private static string LastTextPath => ProblemPcRepair.LastTextPath;
}
