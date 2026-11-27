using System.Globalization;

namespace UpdateExpress.Services.Security.Providers;

/// <summary>The signs Windows leaves when it waits for a restart. Null = that sign could not be read.</summary>
internal sealed record RestartReading
{
    /// <summary>Windows Update has installed something that needs a restart.</summary>
    public bool? WindowsUpdate { get; init; }
    /// <summary>The servicing stack (updates and Windows features) waits for a restart.</summary>
    public bool? Servicing { get; init; }
    /// <summary>Files are to be replaced or removed at the next restart.</summary>
    public bool? FileOperations { get; init; }
}

/// <summary>
/// UPD-SEC-002, UPD-SEC-004, UPD-SEC-005 and UPD-SEC-006: waiting security updates, a
/// pending restart, and which Windows this is and whether Microsoft still services it.
/// Registry values and what Update Express already holds in memory; nothing is searched for
/// and no process is started.
/// </summary>
internal sealed class WindowsServicingCheckProvider : ISecurityCheckProvider
{
    internal const string WaitingUpdatesId = "UPD-SEC-002";
    internal const string RestartId = "UPD-SEC-004";
    internal const string VersionId = "UPD-SEC-005";
    internal const string SupportId = "UPD-SEC-006";

    internal const string WindowsUpdateRestartKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired";
    internal const string ServicingRestartKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending";
    internal const string SessionManagerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager";
    internal const string FileOperationsValue = "PendingFileRenameOperations";
    internal const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    /// <summary>A search older than this says too little about what waits now.</summary>
    internal static readonly TimeSpan SearchValidFor = TimeSpan.FromDays(7);

    private static readonly string[] Ids = { WaitingUpdatesId, RestartId, VersionId, SupportId };

    private readonly Func<WindowsUpdateSearchResult?> _lastSearch;
    private readonly IMachineRegistry _registry;
    private readonly Func<WindowsSupportReader.Result> _support;

    internal WindowsServicingCheckProvider(
        Func<WindowsUpdateSearchResult?>? lastSearch = null,
        IMachineRegistry? registry = null,
        Func<WindowsSupportReader.Result>? support = null)
    {
        _lastSearch = lastSearch ?? (() => WindowsUpdateSearchMemory.Last);
        _registry = registry ?? new MachineRegistry();
        _support = support ?? WindowsSupportReader.Read;
    }

    public string Name => "windows-servicing";

    public IReadOnlyList<string> CheckIds => Ids;

    public Task<IReadOnlyList<SecurityCheckResult>> AssessAsync(SecurityCheckContext context, CancellationToken cancellationToken)
    {
        var now = context.Now == default ? DateTimeOffset.Now : context.Now;
        WindowsSupportReader.Result? support = null;
        var results = new List<SecurityCheckResult>();
        foreach (string id in context.RequestedIds)
        {
            switch (id.ToUpperInvariant())
            {
                case WaitingUpdatesId:
                    results.Add(WaitingUpdates(_lastSearch(), now));
                    break;
                case RestartId:
                    results.Add(Restart(ReadRestart(_registry)));
                    break;
                case VersionId:
                    results.Add(Version(support ??= _support(), context.Machine.OsBuild, ReadRevision(_registry)));
                    break;
                case SupportId:
                    results.Add(Support(support ??= _support()));
                    break;
            }
        }
        return Task.FromResult<IReadOnlyList<SecurityCheckResult>>(results);
    }

    /// <summary>UPD-SEC-002: what the last search in Update Express left waiting. Never a search of its own.</summary>
    internal static SecurityCheckResult WaitingUpdates(WindowsUpdateSearchResult? search, DateTimeOffset now)
    {
        if (search is null)
        {
            return With(WaitingUpdatesId, SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.NoUpdateSearchYet,
                detail: "No Windows Update search has been made in this session of Update Express. This check does not search.");
        }

        string searched = "From the Windows Update search in Update Express at " +
                          search.SearchedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ".";
        var age = now - search.SearchedAt;
        if (age > SearchValidFor || age < TimeSpan.FromDays(-1))
        {
            return With(WaitingUpdatesId, SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.UpdateSearchTooOld,
                detail: searched + $" That is more than {SearchValidFor.TotalDays:0} days from now.");
        }

        string offered = $" Windows Update offered {search.Offered} updates; it classifies {search.SecurityUpdates.Count} of them as security updates.";
        if (search.SecurityUpdates.Count == 0)
            return With(WaitingUpdatesId, SecurityAssessmentStatus.Protected, SecurityStateCodes.NoSecurityUpdatesWaiting, detail: searched + offered);

        string names = string.Join("; ", search.SecurityUpdates.Take(10).Select(update =>
            update.Kb.Length > 0 && !update.Title.Contains(update.Kb, StringComparison.OrdinalIgnoreCase)
                ? update.Kb + " " + update.Title
                : update.Title.Length > 0 ? update.Title : update.Kb));
        return With(WaitingUpdatesId, SecurityAssessmentStatus.NeedsAttention, SecurityStateCodes.SecurityUpdatesWaiting,
            SecurityReasonCodes.SecurityUpdatesNotInstalled,
            searched + offered + " Waiting: " + names + (search.SecurityUpdates.Count > 10 ? "; and more." : "."),
            search.SecurityUpdates.Count.ToString(CultureInfo.InvariantCulture));
    }

    internal static RestartReading ReadRestart(IMachineRegistry registry) => new()
    {
        WindowsUpdate = Look(() => registry.KeyExists(WindowsUpdateRestartKey)),
        Servicing = Look(() => registry.KeyExists(ServicingRestartKey)),
        FileOperations = Look(() => registry.CountLines(SessionManagerKey, FileOperationsValue) is > 0)
    };

    /// <summary>
    /// UPD-SEC-004: the same three signs the main window's restart notice goes by. One sign
    /// is enough for "restart required"; "no restart pending" needs all three read.
    /// </summary>
    internal static SecurityCheckResult Restart(RestartReading reading)
    {
        var parts = new List<string>
        {
            Sign("Windows Update", reading.WindowsUpdate),
            Sign("The servicing stack", reading.Servicing),
            Sign("Pending file operations", reading.FileOperations)
        };
        string detail = string.Join(" ", parts);

        bool updates = reading.WindowsUpdate == true || reading.Servicing == true;
        if (updates || reading.FileOperations == true)
        {
            return With(RestartId, SecurityAssessmentStatus.RestartRequired, SecurityStateCodes.RestartPending,
                updates ? "" : SecurityReasonCodes.RestartForFileOperations, detail) with { RestartRequired = true };
        }
        if (reading.WindowsUpdate is null || reading.Servicing is null || reading.FileOperations is null)
            return With(RestartId, SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.RestartIndicatorsUnreadable, detail: detail);
        return With(RestartId, SecurityAssessmentStatus.Protected, SecurityStateCodes.NoRestartPending, detail: detail);
    }

    /// <summary>The update revision of the build ("9974" in 26200.9974), or null when it could not be read.</summary>
    internal static int? ReadRevision(IMachineRegistry registry)
    {
        try { return registry.ReadNumber(CurrentVersionKey, "UBR"); }
        catch (Exception ex) when (MachineRegistry.IsReadFailure(ex)) { return null; }
    }

    /// <summary>UPD-SEC-005: which Windows this is. Reported, not judged.</summary>
    internal static SecurityCheckResult Version(WindowsSupportReader.Result support, int? build, int? revision)
    {
        string name = (support.Name ?? "").Trim();
        if (name.Length == 0 || name.Contains("(unknown)", StringComparison.OrdinalIgnoreCase))
            return With(VersionId, SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.WindowsVersionUnreadable, detail: FirstLine(support.Details));

        string value = build is int number
            ? $"{name} (build {number}{(revision is int ubr ? "." + ubr.ToString(CultureInfo.InvariantCulture) : "")})"
            : name;
        return With(VersionId, SecurityAssessmentStatus.NotApplicable, SecurityStateCodes.Value, SecurityReasonCodes.Informational,
            FirstLine(support.Details), value);
    }

    /// <summary>
    /// UPD-SEC-006: what the built-in lifecycle table says. A release it cannot place is
    /// unknown and for a person to look up; it is never called supported.
    /// </summary>
    internal static SecurityCheckResult Support(WindowsSupportReader.Result support)
    {
        string detail = (support.Details ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (!string.IsNullOrWhiteSpace(support.ReferenceUrl)) detail += " Source: " + support.ReferenceUrl;

        return (support.Status ?? "").Trim().ToLowerInvariant() switch
        {
            "supported" => With(SupportId, SecurityAssessmentStatus.Protected, SecurityStateCodes.Supported, detail: detail),
            "expired" => With(SupportId, SecurityAssessmentStatus.NotProtected, SecurityStateCodes.SupportEnded, SecurityReasonCodes.WindowsSupportEnded, detail),
            _ => With(SupportId, SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.WindowsSupportNotVerified, detail: detail) with { ManualReview = true }
        };
    }

    private static bool? Look(Func<bool> read)
    {
        try { return read(); }
        catch (Exception ex) when (MachineRegistry.IsReadFailure(ex)) { return null; }
    }

    private static string Sign(string name, bool? set) => set switch
    {
        true => name + ": a restart is waiting.",
        false => name + ": nothing waiting.",
        null => name + ": could not be read."
    };

    private static string FirstLine(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";

    private static SecurityCheckResult With(
        string id, SecurityAssessmentStatus status, string code = SecurityStateCodes.Unknown, string reason = "", string detail = "", string value = "") =>
        new() { Id = id, Status = status, DetectedCode = code, DetectedValue = value, ReasonCode = reason, Detail = detail };
}
