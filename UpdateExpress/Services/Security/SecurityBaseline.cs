using System;
using System.Collections.Generic;
using System.Linq;

namespace UpdateExpress.Services.Security;

/// <summary>
/// What a baseline says about one check: what is recommended and why, where the check applies,
/// and how it may be remediated later. Nothing here reads Windows; detection lives in the
/// providers.
/// </summary>
internal sealed record SecurityCheckDefinition
{
    public required string Id { get; init; }
    public required SecurityCheckCategory Category { get; init; }
    /// <summary>English text; also the key of the Norwegian text.</summary>
    public required string Name { get; init; }
    /// <summary>English text; also the key of the Norwegian text.</summary>
    public required string Description { get; init; }
    /// <summary>The recommended state as a <see cref="SecurityStateCodes"/> code.</summary>
    public required string RecommendedCode { get; init; }
    public string RecommendedValue { get; init; } = "";
    public SecuritySeverity Severity { get; init; }
    public SecurityApplicability Applicability { get; init; }
    /// <summary>Oldest Windows build the check is meant for. 10240 is the first Windows 10.</summary>
    public int MinimumBuild { get; init; } = 10240;
    /// <summary>Rights the assessment needs to read the setting.</summary>
    public SecurityPrivilege AssessmentPrivilege { get; init; }
    public SecurityRemediationLevel RemediationLevel { get; init; }
    /// <summary>False in every Phase 1 entry: Phase 1 changes nothing on the PC.</summary>
    public bool RemediationAvailable { get; init; }
    public SecurityPrivilege RemediationPrivilege { get; init; } = SecurityPrivilege.Administrator;
    public bool RemediationNeedsRestart { get; init; }
    /// <summary>Policies that can hold the setting, in English. Empty when none is known.</summary>
    public string PolicyRestrictions { get; init; } = "";
    /// <summary>Where the recommendation comes from. Empty when it is Update Express's own rule.</summary>
    public string ReferenceUrl { get; init; } = "";
    /// <summary>Why this value is recommended, in English.</summary>
    public required string Rationale { get; init; }
    /// <summary>Numbers the judgement uses, such as an age limit in days.</summary>
    public IReadOnlyDictionary<string, double> Thresholds { get; init; } = new Dictionary<string, double>();
}

/// <summary>A versioned set of recommendations. Customer profiles can be further instances.</summary>
internal sealed class SecurityBaseline
{
    private readonly Dictionary<string, SecurityCheckDefinition> _byId;

    internal SecurityBaseline(string id, string profile, IEnumerable<SecurityCheckDefinition> definitions)
    {
        Id = id;
        Profile = profile;
        Definitions = definitions.ToArray();
        _byId = new Dictionary<string, SecurityCheckDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in Definitions)
        {
            if (!_byId.TryAdd(definition.Id, definition))
                throw new ArgumentException("Duplicate security check id: " + definition.Id, nameof(definitions));
        }
    }

    internal string Id { get; }
    internal string Profile { get; }
    internal IReadOnlyList<SecurityCheckDefinition> Definitions { get; }

    internal SecurityCheckDefinition? Find(string id) =>
        _byId.TryGetValue(id ?? "", out var definition) ? definition : null;

    /// <summary>
    /// The answer for a check that is not meant for this machine, or null when the check
    /// applies and must be assessed. A machine whose kind or build could not be read is
    /// assessed: guessing "not applicable" would hide a check.
    /// </summary>
    internal static SecurityCheckResult? NotApplicableResult(SecurityCheckDefinition definition, SecurityMachineInfo machine)
    {
        if (definition.Applicability == SecurityApplicability.ClientOnly && machine.IsServerOs == true)
            return SecurityCheckResult.NotApplicable(definition.Id, SecurityReasonCodes.ServerOs, "Client-only check; this is Windows Server.");
        if (definition.Applicability == SecurityApplicability.ServerOnly && machine.IsServerOs == false)
            return SecurityCheckResult.NotApplicable(definition.Id, SecurityReasonCodes.ClientOs, "Server-only check; this is a Windows client.");
        if (machine.OsBuild is int build && build < definition.MinimumBuild)
            return new SecurityCheckResult
            {
                Id = definition.Id,
                Status = SecurityAssessmentStatus.NotSupported,
                ReasonCode = SecurityReasonCodes.OsTooOld,
                Detail = $"Needs Windows build {definition.MinimumBuild} or later; this is build {build}."
            };
        return null;
    }
}
