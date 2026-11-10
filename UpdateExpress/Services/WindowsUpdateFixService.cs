using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using UpdateExpress.Models;

namespace UpdateExpress.Services;

public enum WindowsUpdateFixLevel
{
    Quick = 1,
    Medium = 2,
    Deep = 3
}

internal sealed class WindowsUpdateFixStepResult
{
    public string Name { get; init; } = "";
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string Detail { get; init; } = "";
    public string? Error { get; init; }
}

internal sealed class WindowsUpdateFixReport
{
    public string Level { get; init; } = "";
    public DateTimeOffset Started { get; init; }
    public DateTimeOffset Finished { get; set; }
    public string Machine { get; init; } = Environment.MachineName;
    public string Os { get; init; } = Environment.OSVersion.ToString();
    public string AppVersion { get; init; } = ProductVersion.Display;
    public List<WindowsUpdateFixStepResult> Steps { get; init; } = new();
    public bool HasFailures => Steps.Any(s => !s.Success);
    public bool Completed { get; set; }

    internal static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MicroSystems", "Update Express", "Logs");

    internal static string LastJsonPath => Path.Combine(DirectoryPath, "wu-fix-last.json");
    internal static string LastTextPath => Path.Combine(DirectoryPath, "wu-fix-last.txt");

    internal string BuildAiPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are helping troubleshoot Windows Update on a PC.");
        sb.AppendLine("Update Express ran a Windows Update fix. Analyze failures and suggest next safe steps.");
        sb.AppendLine("Do not invent severity that is not in the log. Prefer reversible steps.");
        sb.AppendLine();
        sb.AppendLine("=== Context ===");
        sb.AppendLine($"App: Update Express {AppVersion}");
        sb.AppendLine($"Machine: {Machine}");
        sb.AppendLine($"OS: {Os}");
        sb.AppendLine($"Fix level: {Level}");
        sb.AppendLine($"Started: {Started:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Finished: {Finished:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Completed: {Completed}");
        sb.AppendLine($"Has failures: {HasFailures}");
        sb.AppendLine();
        sb.AppendLine("=== Steps ===");
        foreach (var step in Steps)
        {
            sb.AppendLine($"- [{(step.Success ? "OK" : "FAIL")}] {step.Name} (exit {step.ExitCode})");
            if (!string.IsNullOrWhiteSpace(step.Error))
                sb.AppendLine($"  Error: {step.Error}");
            if (!string.IsNullOrWhiteSpace(step.Detail))
            {
                var clipped = step.Detail.Length > 4000 ? step.Detail[^4000..] : step.Detail;
                sb.AppendLine("  Detail:");
                foreach (var line in clipped.Replace("\r\n", "\n").Split('\n').TakeLast(40))
                    sb.AppendLine("    " + line);
            }
        }
        sb.AppendLine();
        sb.AppendLine("=== Ask ===");
        sb.AppendLine("1) What most likely caused any FAIL steps?");
        sb.AppendLine("2) What should the technician try next (ordered, safe)?");
        sb.AppendLine("3) Any reboot / DISM / CBS log checks needed?");
        return sb.ToString();
    }

    internal void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(LastJsonPath, json, new UTF8Encoding(false));
        File.WriteAllText(LastTextPath, BuildAiPrompt(), new UTF8Encoding(false));
    }

    internal static WindowsUpdateFixReport? LoadLatest()
    {
        try
        {
            if (!File.Exists(LastJsonPath)) return null;
            return JsonSerializer.Deserialize<WindowsUpdateFixReport>(File.ReadAllText(LastJsonPath));
        }
        catch { return null; }
    }

    internal static string? LoadLatestPrompt()
    {
        try
        {
            if (File.Exists(LastTextPath))
                return File.ReadAllText(LastTextPath);
            return LoadLatest()?.BuildAiPrompt();
        }
        catch { return null; }
    }
}

internal static class WindowsUpdateFixService
{
    private static readonly string[] UpdateServices = ["wuauserv", "bits", "cryptsvc", "msiserver"];

    internal static string Describe(WindowsUpdateFixLevel level) => level switch
    {
        WindowsUpdateFixLevel.Quick =>
            "Check free space on the Windows drive, run SFC /scannow, then save Windows Update history, reset the update cache, and scan for updates (can take 15–45 minutes).",
        WindowsUpdateFixLevel.Medium =>
            "Same steps as Quick: disk space check, SFC /scannow, then the Windows Update cache reset (can take 15–45 minutes).",
        WindowsUpdateFixLevel.Deep =>
            "Quick fix, then DISM /RestoreHealth. If the first SFC could not repair every file, SFC runs once more after DISM (can take 30–90+ minutes, needs network).",
        _ => ""
    };

    internal static async Task<WindowsUpdateFixReport> RunAsync(
        WindowsUpdateFixLevel level,
        IProgress<string>? progress,
        CancellationToken cancellationToken = default,
        UpdateEntry? fixTarget = null,
        bool includeOptional = false,
        bool allDrivers = false)
    {
        var report = new WindowsUpdateFixReport
        {
            Level = level.ToString(),
            Started = DateTimeOffset.Now
        };

        async Task Add(string name, Func<Task<(bool ok, int code, string detail, string? err)>> action)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(name + "…");
            try
            {
                var (ok, code, detail, err) = await action().ConfigureAwait(false);
                report.Steps.Add(new WindowsUpdateFixStepResult
                {
                    Name = name,
                    Success = ok,
                    ExitCode = code,
                    Detail = detail,
                    Error = err
                });
                progress?.Report(ok ? $"{name}: OK" : $"{name}: FAILED (exit {code})");
            }
            catch (Exception ex)
            {
                report.Steps.Add(new WindowsUpdateFixStepResult
                {
                    Name = name,
                    Success = false,
                    ExitCode = -1,
                    Detail = "",
                    Error = ex.Message
                });
                progress?.Report($"{name}: FAILED — {ex.Message}");
            }
        }

        await Add("Check disk space", async () =>
        {
            await Task.Yield();
            string drive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var info = new DriveInfo(drive);
            long freeGb = info.AvailableFreeSpace / (1024 * 1024 * 1024);
            long totalGb = info.TotalSize / (1024 * 1024 * 1024);
            string detail = $"{drive} free ≈ {freeGb} GB of {totalGb} GB. Windows Update needs about 10–20 GB free on this drive.";
            bool ok = freeGb >= 10;
            return (ok, ok ? 0 : 1, detail, ok ? null : "Low free space can block cumulative and feature updates. Free at least 10–20 GB on the Windows drive.");
        }).ConfigureAwait(false);

        // SFC before the cache reset, so broken system files are visible before Windows Update is changed.
        // A failed scan does not stop the later steps.
        int sfcCode = 0;
        string sfcOutput = "";
        await Add("SFC /scannow", async () =>
        {
            var sfc = Path.Combine(Environment.SystemDirectory, "sfc.exe");
            var (code, output) = await RunProcessAsync(sfc, "/scannow", cancellationToken, progress).ConfigureAwait(false);
            sfcCode = code;
            sfcOutput = output;
            bool ok = code == 0;
            return (ok, code, output, ok ? null : "SFC reported a non-zero exit code.");
        }).ConfigureAwait(false);

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string softDist = Path.Combine(windir, "SoftwareDistribution");
        string softDistBak = Path.Combine(windir, "SoftwareDistribution.bak-" + stamp);
        string catroot2 = Path.Combine(windir, "System32", "catroot2");
        string catroot2Bak = Path.Combine(windir, "System32", "catroot2.bak-" + stamp);

        await Add("Save Windows Update history", async () =>
        {
            List<UpdateEntry> live;
            try
            {
                live = await WindowsUpdateReader.RunStaAsync(WindowsUpdateReader.History).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (fixTarget != null)
                    HistoryArchive.Remember(Array.Empty<UpdateEntry>(), fixTarget);
                return (false, -1, "", ex.Message);
            }
            int count = HistoryArchive.Remember(live, fixTarget);
            return (true, 0, count + " history events saved in Update Express.", (string?)null);
        }).ConfigureAwait(false);

        await Add("Stop Windows Update services", async () =>
        {
            var log = new StringBuilder();
            foreach (var svc in UpdateServices)
            {
                var (code, output) = await RunCmdAsync($"net stop {svc} /y", cancellationToken).ConfigureAwait(false);
                log.AppendLine($"net stop {svc} /y → exit {code}");
                log.AppendLine(output);
                // 2 = service not started; treat as acceptable for Quick fix
            }
            return (true, 0, log.ToString(), null);
        }).ConfigureAwait(false);

        await Add("Rename SoftwareDistribution", async () =>
        {
            await Task.Yield();
            if (!Directory.Exists(softDist))
            {
                HistoryArchive.BeginFix();
                return (true, 0, "Folder not present (skipped).", null);
            }
            Directory.Move(softDist, softDistBak);
            HistoryArchive.BeginFix();
            return (true, 0, $"Renamed to {softDistBak}", null);
        }).ConfigureAwait(false);

        await Add("Rename catroot2", async () =>
        {
            await Task.Yield();
            if (!Directory.Exists(catroot2))
                return (true, 0, "Folder not present (skipped).", null);
            Directory.Move(catroot2, catroot2Bak);
            return (true, 0, $"Renamed to {catroot2Bak}", null);
        }).ConfigureAwait(false);

        await Add("Start Windows Update services", async () =>
        {
            var log = new StringBuilder();
            bool allOk = true;
            // Start order: cryptsvc, bits, wuauserv, msiserver
            foreach (var svc in new[] { "cryptsvc", "bits", "wuauserv", "msiserver" })
            {
                var (code, output) = await RunCmdAsync($"net start {svc}", cancellationToken).ConfigureAwait(false);
                log.AppendLine($"net start {svc} → exit {code}");
                log.AppendLine(output);
                if (code != 0 && code != 2) allOk = false; // 2 = already started
            }
            return (allOk, allOk ? 0 : 1, log.ToString(), allOk ? null : "One or more services failed to start.");
        }).ConfigureAwait(false);

        await Add("Trigger Windows Update detection", async () =>
        {
            var log = new StringBuilder();
            var uso = Path.Combine(Environment.SystemDirectory, "UsoClient.exe");
            if (File.Exists(uso))
            {
                var (code, output) = await RunProcessAsync(uso, "StartInteractiveScan", cancellationToken).ConfigureAwait(false);
                log.AppendLine($"UsoClient StartInteractiveScan → exit {code}");
                log.AppendLine(output);
                if (code == 0) return (true, 0, log.ToString(), null);
            }
            var (code2, output2) = await RunCmdAsync("wuauclt /detectnow", cancellationToken).ConfigureAwait(false);
            log.AppendLine($"wuauclt /detectnow → exit {code2}");
            log.AppendLine(output2);
            // Detection trigger can return non-zero on newer Windows; do not fail Quick solely on this.
            return (true, code2, log.ToString(), null);
        }).ConfigureAwait(false);

        if (level >= WindowsUpdateFixLevel.Deep)
        {
            await Add("DISM /RestoreHealth", async () =>
            {
                var dism = Path.Combine(Environment.SystemDirectory, "Dism.exe");
                var (code, output) = await RunProcessAsync(
                    dism,
                    "/Online /Cleanup-Image /RestoreHealth",
                    cancellationToken,
                    progress).ConfigureAwait(false);
                bool ok = code == 0;
                return (ok, code, output, ok ? null : "DISM reported a non-zero exit code.");
            }).ConfigureAwait(false);

            // DISM repairs the source SFC reads. Run SFC again only when the first pass could not repair everything.
            if (SfcNeedsAnotherPass(sfcCode, sfcOutput))
            {
                await Add("SFC /scannow after DISM", async () =>
                {
                    var sfc = Path.Combine(Environment.SystemDirectory, "sfc.exe");
                    var (code, output) = await RunProcessAsync(sfc, "/scannow", cancellationToken, progress).ConfigureAwait(false);
                    bool ok = code == 0;
                    return (ok, code, output, ok ? null : "SFC reported a non-zero exit code.");
                }).ConfigureAwait(false);
            }
        }

        await Add("Check for updates", async () =>
        {
            try
            {
                var pending = await WindowsUpdateReader.RunStaAsync(
                    () => WindowsUpdateReader.AdvancedPending(includeOptional, allDrivers)).ConfigureAwait(false);
                HistoryArchive.RecordScan(pending, true, null);
                return (true, 0, pending.Count + " updates offered after the fix.", (string?)null);
            }
            catch (Exception ex)
            {
                HistoryArchive.RecordScan(Array.Empty<UpdateEntry>(), false, ex.Message);
                return (false, -1, "", ex.Message);
            }
        }).ConfigureAwait(false);

        report.Finished = DateTimeOffset.Now;
        report.Completed = true;
        try { report.Save(); } catch { }
        ActivityLog.TryWrite("WindowsUpdateFix", report.HasFailures ? "CompletedWithErrors" : "Completed",
            new { report.Level, Failures = report.Steps.Count(s => !s.Success) });
        return report;
    }

    private static bool SfcNeedsAnotherPass(int code, string? output)
    {
        if (code != 0) return true;
        if (string.IsNullOrWhiteSpace(output)) return false;
        return output.Contains("unable to fix", StringComparison.OrdinalIgnoreCase)
            || output.Contains("kunne ikke", StringComparison.OrdinalIgnoreCase);
    }

    private static Task<(int ExitCode, string Output)> RunCmdAsync(string command, CancellationToken ct)
        => RunProcessAsync(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/c " + command, ct);

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory
        };
        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Append(string? line)
        {
            if (line == null) return;
            lock (output) output.AppendLine(line);
            if (!string.IsNullOrWhiteSpace(line))
                progress?.Report(line.Trim());
        }

        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);

        if (!process.Start())
            return (-1, "Failed to start process: " + fileName);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using var reg = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            tcs.TrySetCanceled(cancellationToken);
        });

        int code = await tcs.Task.ConfigureAwait(false);
        string text;
        lock (output) text = output.ToString();
        return (code, text);
    }
}
