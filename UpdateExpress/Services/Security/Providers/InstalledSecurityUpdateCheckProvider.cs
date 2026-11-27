using System.Globalization;
using System.Text.Json;

namespace UpdateExpress.Services.Security.Providers;

/// <summary>One line of Windows' list of installed updates.</summary>
/// <param name="Kind">What Windows calls it: "Security Update", "Update", "Hotfix".</param>
/// <param name="InstalledOn">The day it was installed; Windows keeps no time of day. Null when it gave none.</param>
internal sealed record InstalledFix(string Id, string Kind, DateTime? InstalledOn);

internal sealed record InstalledFixesReading
{
    public bool Succeeded { get; init; }
    public string Failure { get; init; } = "";
    public IReadOnlyList<InstalledFix> Fixes { get; init; } = Array.Empty<InstalledFix>();

    internal static InstalledFixesReading Failed(string why) => new() { Succeeded = false, Failure = why };
}

/// <summary>
/// Reads Windows' own list of installed updates (Get-HotFix, the Win32_QuickFixEngineering
/// class). It says for each update whether it is a security update and the day it was
/// installed. The description is "Security Update" on a Norwegian Windows too; should a
/// Windows translate it, the check finds no security update and answers unknown, never a
/// failure. Windows Update's history is not used: its titles are translated, most of its
/// rows carry no classification, and asking for it can start the Windows Update service.
/// </summary>
internal static class InstalledFixesReader
{
    internal const string Script =
        SecurityPowerShell.Utf8Output +
        "$ErrorActionPreference='Stop'; " +
        "$fixes=@(Get-HotFix | ForEach-Object { " +
        "$day=$null; try { if ($_.InstalledOn) { $day=([datetime]$_.InstalledOn).ToString('yyyy-MM-dd') } } catch { }; " +
        "[pscustomobject]@{ Id=[string]$_.HotFixID; Kind=[string]$_.Description; On=$day } }); " +
        "[pscustomobject]@{ Fixes=$fixes } | ConvertTo-Json -Compress -Depth 3";

    internal static async Task<InstalledFixesReading> ReadAsync(CancellationToken cancellationToken) =>
        Parse(await SecurityPowerShell.RunAsync(Script, cancellationToken).ConfigureAwait(false));

    internal static InstalledFixesReading Parse(PowerShellAnswer answer)
    {
        if (!answer.Ran) return InstalledFixesReading.Failed("PowerShell could not be started. " + answer.Error);

        string json = (answer.Output ?? "").Trim().TrimStart('\uFEFF').Trim();
        JsonDocument? document = null;
        try
        {
            if (json.Length > 0) document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // Handled below as an answer that could not be understood.
        }

        using (document)
        {
            var root = document?.RootElement;
            // A list cut short by an error would make the latest security update look older than it is.
            if (answer.ExitCode != 0 || root is not { ValueKind: JsonValueKind.Object })
            {
                return InstalledFixesReading.Failed(answer.ExitCode != 0
                    ? $"PowerShell ended with code {answer.ExitCode}. {answer.Error}".Trim()
                    : "The answer from Windows could not be understood.");
            }

            var fixes = new List<InstalledFix>();
            if (root.Value.TryGetProperty("Fixes", out var listed) && listed.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            {
                foreach (var item in listed.ValueKind == JsonValueKind.Array ? listed.EnumerateArray().ToArray() : new[] { listed })
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    string id = Text(item, "Id");
                    string kind = Text(item, "Kind");
                    if (id.Length == 0 && kind.Length == 0) continue;
                    DateTime? day = DateTime.TryParseExact(Text(item, "On"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                        ? parsed
                        : null;
                    fixes.Add(new InstalledFix(id, kind, day));
                }
            }
            return new InstalledFixesReading { Succeeded = true, Fixes = fixes };
        }
    }

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "").Trim() : "";
}

/// <summary>
/// UPD-SEC-001: when a Windows security update was last installed. Judged against the
/// baseline's limit in days. A list without a security update is unknown, not a failure:
/// Windows starts the list anew when it is installed or upgraded.
/// </summary>
internal sealed class InstalledSecurityUpdateCheckProvider : ISecurityCheckProvider
{
    internal const string Id = "UPD-SEC-001";
    internal const string SecurityKind = "Security Update";
    private const double DefaultWarnAfterDays = 45;

    private readonly Func<CancellationToken, Task<InstalledFixesReading>> _read;

    internal InstalledSecurityUpdateCheckProvider(Func<CancellationToken, Task<InstalledFixesReading>>? read = null) =>
        _read = read ?? InstalledFixesReader.ReadAsync;

    public string Name => "installed-updates";

    public IReadOnlyList<string> CheckIds { get; } = new[] { Id };

    /// <summary>Windows can take its time over the list of installed updates on a PC with many of them.</summary>
    public TimeSpan? Timeout => TimeSpan.FromSeconds(45);

    public async Task<IReadOnlyList<SecurityCheckResult>> AssessAsync(SecurityCheckContext context, CancellationToken cancellationToken)
    {
        if (!context.RequestedIds.Contains(Id, StringComparer.OrdinalIgnoreCase)) return Array.Empty<SecurityCheckResult>();
        var reading = await _read(cancellationToken).ConfigureAwait(false);
        var now = context.Now == default ? DateTimeOffset.Now : context.Now;
        return new[] { Judge(reading, now, context.Baseline.Find(Id)?.Thresholds) };
    }

    internal static SecurityCheckResult Judge(InstalledFixesReading reading, DateTimeOffset now, IReadOnlyDictionary<string, double>? limits = null)
    {
        if (!reading.Succeeded)
            return With(SecurityAssessmentStatus.Error, reason: SecurityReasonCodes.InstalledUpdatesReadFailed, detail: reading.Failure);

        var security = reading.Fixes
            .Where(fix => fix.Kind.Equals(SecurityKind, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        string counts = $"Windows lists {reading.Fixes.Count} installed updates, {security.Length} of them as security updates. " +
                        "The list gives the day of an installation, not the time.";
        if (security.Length == 0)
            return With(SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.NoSecurityUpdateListed, detail: counts);

        DateTime today = now.LocalDateTime.Date;
        // A day ahead of this PC's clock is a clock or a time zone that moved, not an installation.
        var dated = security.Where(fix => fix.InstalledOn is DateTime day && day.Date <= today.AddDays(1)).ToArray();
        if (dated.Length == 0)
            return With(SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.SecurityUpdateDateUnclear, detail: counts);

        DateTime latest = dated.Max(fix => fix.InstalledOn!.Value.Date);
        int days = Math.Max(0, (today - latest).Days);
        string[] ids = dated
            .Where(fix => fix.InstalledOn!.Value.Date == latest && fix.Id.Length > 0)
            .Select(fix => fix.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        string value = latest.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + (ids.Length > 0 ? " (" + string.Join(", ", ids) + ")" : "");

        double warn = Limit(limits, SecurityBaselineV1.WarnAfterDays, DefaultWarnAfterDays);
        double critical = Limit(limits, SecurityBaselineV1.CriticalAfterDays, double.PositiveInfinity);
        string ago = days == 0 ? "today" : days == 1 ? "1 day ago" : $"{days} days ago";
        string detail = $"The latest security update was installed {ago}. The limit is {warn.ToString("0.#", CultureInfo.InvariantCulture)} days. " + counts;

        if (days <= warn)
            return With(SecurityAssessmentStatus.Protected, SecurityStateCodes.InstalledOn, value, detail: detail);
        return With(
            days > critical ? SecurityAssessmentStatus.NotProtected : SecurityAssessmentStatus.NeedsAttention,
            SecurityStateCodes.InstalledOn, value, SecurityReasonCodes.SecurityUpdateOverdue, detail);
    }

    private static double Limit(IReadOnlyDictionary<string, double>? limits, string name, double fallback) =>
        limits is not null && limits.TryGetValue(name, out double value) && value > 0 ? value : fallback;

    private static SecurityCheckResult With(
        SecurityAssessmentStatus status, string code = SecurityStateCodes.Unknown, string value = "", string reason = "", string detail = "") =>
        new() { Id = Id, Status = status, DetectedCode = code, DetectedValue = value, ReasonCode = reason, Detail = detail };
}
