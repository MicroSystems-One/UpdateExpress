using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using UpdateExpress.Models;
namespace UpdateExpress.Services;
public sealed class AppUpdate(string name, string id, string installed, string available)
{
 public string Name { get; } = name; public string Id { get; } = id; public string Installed { get; } = installed; public string Available { get; } = available; public bool IsSelected { get; set; }
 public string KB => "";
 public string VersionSummary => $"{Installed} → {Available}";
 public string VersionDetails => $"Installed: {Installed} | Available: {Available}";
 public string Status => "Available";
 public string Date => "";
 public string ErrorCode => "";
 public string DisplayName => Id.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) && !Name.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase) ? "Microsoft " + Name : Name;
 // WinGet upgrade does not provide a reliable security severity level → display as Unrated.
 public string Priority => "Unrated";
 public string PriorityDisplay
 {
  get { var g = PriorityGrade.ForApplication(); return g.Display; }
 }
 public string PriorityColorHex
 {
  get { var g = PriorityGrade.ForApplication(); return g.ColorHex; }
 }
 public string PriorityExplanation =>
  "WinGet reports an available application version but does not provide verified security severity. Shown as Unrated, not a recommendation to skip the update.";
 public bool AutoUpdate { get; set; }
}
public sealed record WingetScanResult(List<AppUpdate> Updates, string Raw, string Message);
public static class WingetReader
{
    // Winget is run only in read mode. The table format varies with winget version and locale;
    // the original output is always preserved for auditing.
    public static async Task<WingetScanResult> ScanAsync()
    {
        var start = new ProcessStartInfo("winget", "upgrade --accept-source-agreements --disable-interactivity")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("WinGet could not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch { } throw new TimeoutException("WinGet timed out after 90 seconds."); }
        var output = await outputTask;
        var error = await errorTask;
        var raw = output + Environment.NewLine + error;
        var updates = ParseTable(output);
        var message = process.ExitCode == 0
            ? $"WinGet found {updates.Count} available updates."
            : $"WinGet exited with code {process.ExitCode}; results may be incomplete.";
        return new(updates, raw, message);
    }
    public static async Task<(int ExitCode, string Output)> UpgradeAsync(string packageId, CancellationToken cancellationToken)
    {
        // Never pass a package ID through a shell. Execute only the exact ID returned by the scan.
        if (string.IsNullOrWhiteSpace(packageId) || packageId.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '+')))
            throw new ArgumentException("Invalid package ID.");
        var psi = new ProcessStartInfo("winget") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { "upgrade", "--id", packageId, "--exact", "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity" }) psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("WinGet could not start.");
        var outputTask = proc.StandardOutput.ReadToEndAsync(); var errorTask = proc.StandardError.ReadToEndAsync();
        try { await proc.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { try { proc.Kill(entireProcessTree: true); } catch { } throw; }
        return (proc.ExitCode, (await outputTask) + Environment.NewLine + (await errorTask));
    }

    /// <summary>winget APPINSTALLER_CLI_ERROR_UPDATE_INSTALL_TECHNOLOGY_MISMATCH (0x8A15008E).</summary>
    public const int ExitInstallTechnologyMismatch = unchecked((int)0x8A15008E); // -1978335090

    public static bool IsInstallTechnologyMismatch(int exitCode) => exitCode == ExitInstallTechnologyMismatch;

    /// <summary>
    /// Status label + tooltip/notice for a failed winget upgrade.
    /// Known codes get a clear EN/NO hint; others keep the raw exit code.
    /// </summary>
    public static (string State, string? Tip, string? Notice) DescribeUpgradeFailure(int exitCode, string packageLabel, bool norwegian)
    {
        if (IsInstallTechnologyMismatch(exitCode))
        {
            string state = norwegian ? "Feilet (teknologi-mismatch)" : "Failed (tech mismatch)";
            string tip = norwegian
                ? "WinGet exit -1978335090 (0x8A15008E): pakken er installert via en annen kanal (f.eks. Microsoft Store eller MSI) enn den WinGet vil oppgradere. Avinstaller og installer på nytt via winget, eller oppgrader i Store."
                : "WinGet exit -1978335090 (0x8A15008E): package was installed via another channel (e.g. Microsoft Store or MSI) than the one WinGet uses to upgrade. Reinstall via winget, or upgrade in the Store.";
            string notice = norwegian
                ? $"{packageLabel}: installert via annen kanal — reinstaller via winget eller oppgrader i Store."
                : $"{packageLabel}: installed via another channel — reinstall via winget or upgrade in the Store.";
            return (state, tip, notice);
        }

        return ($"Failed (exit {exitCode})", null, null);
    }
    private static List<AppUpdate> ParseTable(string output)
    {
        var result = new List<AppUpdate>();
        var lines = output.Replace("\r", "").Split('\n');
        var headerIndex = Array.FindIndex(lines, line => Regex.IsMatch(line, @"(?i)\b(Id|ID)\s+\b(Version|Versjon)\b") && Regex.IsMatch(line, @"(?i)\b(Available|Tilgjengelig)\b"));
        if (headerIndex < 0 || headerIndex + 1 >= lines.Length) return result;
        var header = lines[headerIndex];
        int id = header.IndexOf("Id", StringComparison.OrdinalIgnoreCase);
        int version = header.IndexOf("Version", StringComparison.OrdinalIgnoreCase);
        if (version < 0) version = header.IndexOf("Versjon", StringComparison.OrdinalIgnoreCase);
        int available = header.IndexOf("Available", StringComparison.OrdinalIgnoreCase);
        if (available < 0) available = header.IndexOf("Tilgjengelig", StringComparison.OrdinalIgnoreCase);
        int source = header.IndexOf("Source", StringComparison.OrdinalIgnoreCase);
        if (source < 0) source = header.IndexOf("Kilde", StringComparison.OrdinalIgnoreCase);
        if (id <= 0 || version <= id || available <= version) return result;
        foreach (var line in lines.Skip(headerIndex + 1))
        {
            if (string.IsNullOrWhiteSpace(line) || line.Trim().All(c => c == '-' || c == '─')) continue;
            if (line.Length < available + 1) continue;
            var name = line[..Math.Min(id, line.Length)].Trim();
            var package = line[id..Math.Min(version, line.Length)].Trim();
            var installed = line[version..Math.Min(available, line.Length)].Trim();
            var latest = (source > available && line.Length > source ? line[available..source] : line[available..]).Trim();
            if (name.Length == 0 || package.Length == 0 || installed.Length == 0 || latest.Length == 0) continue;
            result.Add(new(name, package, installed, latest));
        }
        return result;
    }
}
