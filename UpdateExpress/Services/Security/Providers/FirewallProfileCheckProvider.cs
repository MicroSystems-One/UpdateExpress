namespace UpdateExpress.Services.Security.Providers;

/// <summary>
/// NET-001 to NET-003: whether Windows Firewall is on for the domain, private and public
/// profile. One read answers all three. The judgement rests on the effective state; a
/// profile that could not be read is unknown or a failed check, never "off".
/// </summary>
internal sealed class FirewallProfileCheckProvider : ISecurityCheckProvider
{
    internal const string DomainId = "NET-001";
    internal const string PrivateId = "NET-002";
    internal const string PublicId = "NET-003";

    private static readonly IReadOnlyDictionary<string, string> ProfileById =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [DomainId] = FirewallReader.Domain,
            [PrivateId] = FirewallReader.Private,
            [PublicId] = FirewallReader.Public
        };

    private readonly Func<CancellationToken, Task<FirewallReading>> _read;

    internal FirewallProfileCheckProvider(Func<CancellationToken, Task<FirewallReading>>? read = null) =>
        _read = read ?? FirewallReader.ReadAsync;

    public string Name => "firewall";

    public IReadOnlyList<string> CheckIds { get; } = new[] { DomainId, PrivateId, PublicId };

    /// <summary>The first PowerShell start on a slow disk can take longer than the engine's default.</summary>
    public TimeSpan? Timeout => TimeSpan.FromSeconds(30);

    public async Task<IReadOnlyList<SecurityCheckResult>> AssessAsync(SecurityCheckContext context, CancellationToken cancellationToken)
    {
        var reading = await _read(cancellationToken).ConfigureAwait(false);
        return context.RequestedIds
            .Where(ProfileById.ContainsKey)
            .Select(id => Judge(id, ProfileById[id], reading))
            .ToArray();
    }

    /// <summary>The answer for one profile from one reading.</summary>
    internal static SecurityCheckResult Judge(string id, string profile, FirewallReading reading)
    {
        if (!reading.Succeeded)
        {
            return new SecurityCheckResult
            {
                Id = id,
                Status = SecurityAssessmentStatus.Error,
                ReasonCode = SecurityReasonCodes.FirewallReadFailed,
                Detail = reading.Failure
            };
        }

        var policy = PolicyState(profile, reading.Policy);
        reading.Policy.Values.TryGetValue(profile, out var policyValue);
        bool? enabled = reading.Effective.TryGetValue(profile, out bool? state) ? state : null;
        string detail = Detail(profile, reading, policyValue);

        if (enabled is null)
        {
            return new SecurityCheckResult
            {
                Id = id,
                Status = SecurityAssessmentStatus.Unknown,
                Policy = policy,
                ReasonCode = SecurityReasonCodes.FirewallProfileNotReported,
                Detail = detail
            };
        }

        if (enabled == true)
        {
            return new SecurityCheckResult
            {
                Id = id,
                Status = SecurityAssessmentStatus.Protected,
                DetectedCode = SecurityStateCodes.On,
                Policy = policy,
                Detail = detail
            };
        }

        // Off. Who holds it off decides what can be said about it.
        if (policyValue is { Enabled: false })
        {
            return new SecurityCheckResult
            {
                Id = id,
                Status = SecurityAssessmentStatus.ManagedByPolicy,
                DetectedCode = SecurityStateCodes.Off,
                Policy = SecurityPolicyState.Managed,
                ReasonCode = SecurityReasonCodes.FirewallOffByPolicy,
                Detail = detail
            };
        }

        if (reading.OtherProducts is { Count: > 0 })
        {
            // Another product may have taken the firewall's place. That it is registered does
            // not show that it filters this profile, so this is neither a pass nor a failure.
            return new SecurityCheckResult
            {
                Id = id,
                Status = SecurityAssessmentStatus.Unknown,
                ManualReview = true,
                DetectedCode = SecurityStateCodes.Off,
                Policy = policy,
                ReasonCode = SecurityReasonCodes.FirewallOffOtherProduct,
                Detail = detail
            };
        }

        return new SecurityCheckResult
        {
            Id = id,
            Status = SecurityAssessmentStatus.NotProtected,
            DetectedCode = SecurityStateCodes.Off,
            Policy = policy,
            Detail = detail
        };
    }

    private static SecurityPolicyState PolicyState(string profile, FirewallPolicyReading policy)
    {
        if (!policy.Readable) return SecurityPolicyState.Unknown;
        if (policy.Values.ContainsKey(profile)) return SecurityPolicyState.Managed;
        return policy.Unclear.Contains(profile) ? SecurityPolicyState.Unknown : SecurityPolicyState.NotManaged;
    }

    private static string Detail(string profile, FirewallReading reading, FirewallPolicyValue? policyValue)
    {
        var parts = new List<string> { "Effective state of the " + profile + " profile, from the active policy store." };

        if (reading.InUse is null) parts.Add("Whether a network uses this profile now could not be read.");
        else parts.Add(reading.InUse.Contains(profile) ? "A network uses this profile now." : "No network uses this profile now.");

        if (!reading.Policy.Readable) parts.Add("The policy values could not be read.");
        else if (policyValue is not null)
            parts.Add($"{policyValue.Source} sets this profile {(policyValue.Enabled ? "on" : "off")} ({policyValue.Location}).");
        else if (reading.Policy.Unclear.Contains(profile))
            parts.Add("An older policy value for the standard profile exists; whether it decides this profile is not known.");
        else parts.Add("No policy value for this profile was found.");

        if (reading.OtherProducts is { Count: > 0 })
            parts.Add("Other firewall products registered with Windows: " + string.Join(", ", reading.OtherProducts) + ".");

        return string.Join(" ", parts);
    }
}
