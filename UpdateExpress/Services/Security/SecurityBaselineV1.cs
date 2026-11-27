using System.Collections.Generic;

namespace UpdateExpress.Services.Security;

/// <summary>
/// Baseline "UpdateExpress.SecurityBaseline.v1", profile Standard Workstation: the checks of
/// Phase 1 with the recommended state, its source and the reason for it. It is Update
/// Express's own selection. It is not a Microsoft, CIS or NSM baseline and claims no
/// compliance with one.
/// </summary>
internal static class SecurityBaselineV1
{
    internal const string BaselineId = "UpdateExpress.SecurityBaseline.v1";
    internal const string ProfileName = "Standard Workstation";

    /// <summary>Threshold names used in <see cref="SecurityCheckDefinition.Thresholds"/>.</summary>
    internal const string WarnAfterDays = "warnAfterDays";
    internal const string CriticalAfterDays = "criticalAfterDays";

    private const string FirewallUrl =
        "https://learn.microsoft.com/windows/security/operating-system-security/network-security/windows-firewall/";
    private const string DefenderUrl =
        "https://learn.microsoft.com/defender-endpoint/microsoft-defender-antivirus-windows";
    private const string DefenderCompatibilityUrl =
        "https://learn.microsoft.com/defender-endpoint/microsoft-defender-antivirus-compatibility";
    private const string DefenderUpdatesUrl =
        "https://learn.microsoft.com/defender-endpoint/microsoft-defender-antivirus-updates";
    private const string SecureBootUrl =
        "https://learn.microsoft.com/windows-hardware/design/device-experiences/oem-secure-boot";
    private const string TpmUrl =
        "https://learn.microsoft.com/windows/security/hardware-security/tpm/trusted-platform-module-overview";
    private const string TpmRecommendationsUrl =
        "https://learn.microsoft.com/windows/security/hardware-security/tpm/tpm-recommendations";
    private const string AutoLogonUrl =
        "https://learn.microsoft.com/troubleshoot/windows-server/user-profiles-and-logon/turn-on-automatic-logon";
    private const string ReleaseCycleUrl =
        "https://learn.microsoft.com/windows/deployment/update/release-cycle";

    private const string FirewallPolicies =
        "Group Policy or MDM (Windows Defender Firewall profile settings) can hold the profile state.";
    private const string DefenderPolicies =
        "Group Policy, MDM or tamper protection can hold Microsoft Defender Antivirus settings; a third-party product manages its own.";

    internal static SecurityBaseline Create() => new(BaselineId, ProfileName, Definitions());

    private static IEnumerable<SecurityCheckDefinition> Definitions()
    {
        yield return Firewall("NET-001", "Windows Firewall – Domain profile",
            "Windows Firewall for networks where the PC reaches its domain.");
        yield return Firewall("NET-002", "Windows Firewall – Private profile",
            "Windows Firewall for networks marked as private, such as a home or office network.");
        yield return Firewall("NET-003", "Windows Firewall – Public profile",
            "Windows Firewall for networks marked as public, such as a guest or café network.");

        yield return new SecurityCheckDefinition
        {
            Id = "END-001",
            Category = SecurityCheckCategory.EndpointProtection,
            Name = "Registered antivirus products",
            Description = "The antivirus products that have registered with Windows Security Center.",
            RecommendedCode = SecurityStateCodes.AntivirusRegistered,
            Severity = SecuritySeverity.High,
            // Windows Security Center (root/SecurityCenter2) exists on client editions only.
            Applicability = SecurityApplicability.ClientOnly,
            PolicyRestrictions = DefenderPolicies,
            ReferenceUrl = DefenderCompatibilityUrl,
            Rationale = "A PC needs one antivirus product. Registration shows which product Windows knows of; it does not prove that the product is healthy or up to date."
        };
        yield return new SecurityCheckDefinition
        {
            Id = "END-002",
            Category = SecurityCheckCategory.EndpointProtection,
            Name = "Antivirus protection status",
            Description = "Whether an antivirus product is switched on and protecting this PC.",
            RecommendedCode = SecurityStateCodes.Active,
            Severity = SecuritySeverity.High,
            PolicyRestrictions = DefenderPolicies,
            ReferenceUrl = DefenderUrl,
            Rationale = "Antivirus protection that is switched off does not stop known malware. For a third-party product the health cannot be verified from Windows alone and is reported as unknown."
        };
        yield return new SecurityCheckDefinition
        {
            Id = "END-003",
            Category = SecurityCheckCategory.EndpointProtection,
            Name = "Real-time protection",
            Description = "Whether files and programs are scanned as they are opened or run.",
            RecommendedCode = SecurityStateCodes.On,
            Severity = SecuritySeverity.High,
            PolicyRestrictions = DefenderPolicies,
            ReferenceUrl = DefenderUrl,
            Rationale = "Without real-time protection malware is found only by a scheduled or manual scan, after it has run."
        };
        yield return new SecurityCheckDefinition
        {
            Id = "END-004",
            Category = SecurityCheckCategory.EndpointProtection,
            Name = "Antivirus signature version",
            Description = "The version of the antivirus definitions in use.",
            RecommendedCode = SecurityStateCodes.Informational,
            Severity = SecuritySeverity.Informational,
            ReferenceUrl = DefenderUpdatesUrl,
            Rationale = "Reported for reference and support. No version is recommended; how fresh the definitions are is judged by UPD-SEC-008."
        };
        yield return new SecurityCheckDefinition
        {
            Id = "END-005",
            Category = SecurityCheckCategory.EndpointProtection,
            Name = "Antivirus signatures last updated",
            Description = "When the antivirus definitions were last updated.",
            RecommendedCode = SecurityStateCodes.Informational,
            Severity = SecuritySeverity.Informational,
            ReferenceUrl = DefenderUpdatesUrl,
            Rationale = "Reported for reference and support. The age limit is in UPD-SEC-008."
        };
        yield return new SecurityCheckDefinition
        {
            Id = "END-006",
            Category = SecurityCheckCategory.EndpointProtection,
            Name = "Microsoft Defender operating mode",
            Description = "Whether Microsoft Defender Antivirus runs in normal (active) or passive mode.",
            RecommendedCode = SecurityStateCodes.DefenderNormalOrPassive,
            Severity = SecuritySeverity.Medium,
            PolicyRestrictions = DefenderPolicies,
            ReferenceUrl = DefenderCompatibilityUrl,
            Rationale = "Microsoft Defender Antivirus steps back to passive mode or switches off when another antivirus product is the active one. That is by design and is not a fault."
        };

        yield return new SecurityCheckDefinition
        {
            Id = "DEV-003",
            Category = SecurityCheckCategory.DeviceSecurity,
            Name = "Secure Boot",
            Description = "Whether the firmware starts only boot software it trusts.",
            RecommendedCode = SecurityStateCodes.On,
            Severity = SecuritySeverity.Medium,
            RemediationLevel = SecurityRemediationLevel.AssessmentOnly,
            ReferenceUrl = SecureBootUrl,
            Rationale = "Secure Boot keeps malware from loading before Windows starts. It is a firmware setting; Update Express reports it and never changes it. A PC that starts in legacy BIOS mode cannot use it and is reported as not supported."
        };
        yield return new SecurityCheckDefinition
        {
            Id = "DEV-004",
            Category = SecurityCheckCategory.DeviceSecurity,
            Name = "TPM availability",
            Description = "Whether Windows finds a Trusted Platform Module (TPM) security chip.",
            RecommendedCode = SecurityStateCodes.Present,
            Severity = SecuritySeverity.Medium,
            RemediationLevel = SecurityRemediationLevel.AssessmentOnly,
            ReferenceUrl = TpmUrl,
            Rationale = "A TPM protects encryption keys and sign-in secrets in hardware; BitLocker and Windows Hello build on it. It is hardware or a firmware setting; Update Express reports it and never changes it."
        };
        yield return new SecurityCheckDefinition
        {
            Id = "DEV-005",
            Category = SecurityCheckCategory.DeviceSecurity,
            Name = "TPM specification version",
            Description = "The version of the TPM specification the security chip follows.",
            RecommendedCode = SecurityStateCodes.Tpm20,
            Severity = SecuritySeverity.Low,
            RemediationLevel = SecurityRemediationLevel.AssessmentOnly,
            ReferenceUrl = TpmRecommendationsUrl,
            Rationale = "Microsoft recommends TPM 2.0 over TPM 1.2 for its newer algorithms, and Windows 11 requires it."
        };

        yield return new SecurityCheckDefinition
        {
            Id = "ACC-007",
            Category = SecurityCheckCategory.IdentityAndAccess,
            Name = "Automatic Windows logon",
            Description = "Whether Windows signs a user in by itself when the PC starts.",
            RecommendedCode = SecurityStateCodes.NotConfigured,
            Severity = SecuritySeverity.High,
            RemediationLevel = SecurityRemediationLevel.AssessmentOnly,
            ReferenceUrl = AutoLogonUrl,
            Rationale = "With automatic logon anyone who can switch the PC on is signed in, and the classic setup keeps the password readable in the registry. Only whether it is configured is reported; the stored password is never read."
        };

        yield return new SecurityCheckDefinition
        {
            Id = "UPD-SEC-001",
            Category = SecurityCheckCategory.UpdatesAndMaintenance,
            Name = "Last successful Windows security update",
            Description = "When a Windows security update was last installed successfully.",
            RecommendedCode = SecurityStateCodes.WithinDays,
            RecommendedValue = "45",
            Severity = SecuritySeverity.High,
            ReferenceUrl = ReleaseCycleUrl,
            Rationale = "Microsoft releases Windows security updates monthly, on the second Tuesday. Two releases are at most 35 days apart; 45 days allows ten days to install. The limit of 45 days is Update Express's own.",
            Thresholds = new Dictionary<string, double> { [WarnAfterDays] = 45 }
        };
        yield return new SecurityCheckDefinition
        {
            Id = "UPD-SEC-002",
            Category = SecurityCheckCategory.UpdatesAndMaintenance,
            Name = "Available security updates",
            Description = "Security updates that the last Windows Update search found and that are not installed yet.",
            RecommendedCode = SecurityStateCodes.NoSecurityUpdatesWaiting,
            Severity = SecuritySeverity.High,
            ReferenceUrl = ReleaseCycleUrl,
            Rationale = "A security update that is offered and not installed leaves a known weakness open. The result of the last search in Update Express is used; this check does not search again, and without a search it reports unknown."
        };
        yield return new SecurityCheckDefinition
        {
            Id = "UPD-SEC-004",
            Category = SecurityCheckCategory.UpdatesAndMaintenance,
            Name = "Pending restart",
            Description = "Whether Windows waits for a restart to finish installing updates.",
            RecommendedCode = SecurityStateCodes.NoRestartPending,
            Severity = SecuritySeverity.Medium,
            Rationale = "Updates that wait for a restart do not protect until the PC has restarted. Update Express's own rule, from the indicators Windows sets."
        };
        yield return new SecurityCheckDefinition
        {
            Id = "UPD-SEC-005",
            Category = SecurityCheckCategory.UpdatesAndMaintenance,
            Name = "Windows edition and version",
            Description = "The Windows edition, feature version and build on this PC.",
            RecommendedCode = SecurityStateCodes.Informational,
            Severity = SecuritySeverity.Informational,
            ReferenceUrl = WindowsSupportReader.ClientReferenceUrl,
            Rationale = "Reported for reference. Whether this release still gets security updates is judged by UPD-SEC-006."
        };
        yield return new SecurityCheckDefinition
        {
            Id = "UPD-SEC-006",
            Category = SecurityCheckCategory.UpdatesAndMaintenance,
            Name = "Windows support status",
            Description = "Whether Microsoft still releases security updates for this Windows release.",
            RecommendedCode = SecurityStateCodes.Supported,
            Severity = SecuritySeverity.High,
            ReferenceUrl = WindowsSupportReader.ClientReferenceUrl,
            Rationale = "A Windows release past its end of servicing gets no security fixes. LTSC editions, releases outside the built-in table and Extended Security Updates cannot be verified here and are reported as unknown, not as supported."
        };
        yield return new SecurityCheckDefinition
        {
            Id = "UPD-SEC-008",
            Category = SecurityCheckCategory.UpdatesAndMaintenance,
            Name = "Antivirus signature freshness",
            Description = "How old the antivirus definitions are.",
            RecommendedCode = SecurityStateCodes.WithinDays,
            RecommendedValue = "2",
            Severity = SecuritySeverity.High,
            PolicyRestrictions = DefenderPolicies,
            ReferenceUrl = DefenderUpdatesUrl,
            Rationale = "Microsoft publishes Defender definitions several times a day. Update Express already warns in its header at 2 days and marks 7 days as critical; the same limits are used here.",
            Thresholds = new Dictionary<string, double> { [WarnAfterDays] = 2, [CriticalAfterDays] = 7 }
        };
    }

    private static SecurityCheckDefinition Firewall(string id, string name, string description) => new()
    {
        Id = id,
        Category = SecurityCheckCategory.NetworkSecurity,
        Name = name,
        Description = description,
        RecommendedCode = SecurityStateCodes.On,
        Severity = SecuritySeverity.High,
        // Level A of the specification; the remediation itself is Phase 2.
        RemediationLevel = SecurityRemediationLevel.LowRisk,
        PolicyRestrictions = FirewallPolicies,
        ReferenceUrl = FirewallUrl,
        Rationale = "Microsoft recommends keeping Windows Firewall on for every network profile, also when another firewall is in use on the network."
    };
}
