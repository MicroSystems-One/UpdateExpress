using System;
using System.Collections.Generic;

namespace UpdateExpress.Services.Security;

/// <summary>
/// Language-neutral names for a detected or recommended state. The window turns them into
/// English or Norwegian; the cache and the log keep the code.
/// </summary>
internal static class SecurityStateCodes
{
    internal const string Unknown = "unknown";
    /// <summary>The detected value says it all: a version number, a date, a name.</summary>
    internal const string Value = "value";
    internal const string On = "on";
    internal const string Off = "off";
    internal const string Active = "active";
    internal const string Present = "present";
    internal const string NotConfigured = "not_configured";
    internal const string Supported = "supported";
    /// <summary>The check reports a value and recommends none.</summary>
    internal const string Informational = "informational";
    internal const string AntivirusRegistered = "antivirus_registered";
    internal const string DefenderNormalOrPassive = "defender_normal_or_passive";
    internal const string Tpm20 = "tpm_2_0";
    internal const string NoSecurityUpdatesWaiting = "no_security_updates_waiting";
    internal const string NoRestartPending = "no_restart_pending";
    /// <summary>Value: the number of days.</summary>
    internal const string WithinDays = "within_days";
    internal const string NoneRegistered = "none_registered";
    /// <summary>Value: whole hours since the last update.</summary>
    internal const string AgeHours = "age_hours";
    /// <summary>Value: whole days since the last update.</summary>
    internal const string AgeDays = "age_days";
}

/// <summary>Why a check ended without a judgement of the setting itself.</summary>
internal static class SecurityReasonCodes
{
    internal const string Timeout = "timeout";
    internal const string Cancelled = "cancelled";
    internal const string ProviderError = "provider_error";
    internal const string NoResult = "no_result";
    internal const string ServerOs = "server_os";
    internal const string ClientOs = "client_os";
    internal const string OsTooOld = "os_too_old";

    internal const string FirewallReadFailed = "firewall_read_failed";
    internal const string FirewallProfileNotReported = "firewall_profile_not_reported";
    internal const string FirewallOffByPolicy = "firewall_off_by_policy";
    internal const string FirewallOffOtherProduct = "firewall_off_other_product";

    /// <summary>The check reports a value and judges nothing. Goes with the status "not applicable".</summary>
    internal const string Informational = "informational";

    internal const string AntivirusReadFailed = "antivirus_read_failed";
    internal const string AntivirusProductsUnreadable = "antivirus_products_unreadable";
    internal const string AntivirusNoneRegistered = "antivirus_none_registered";
    internal const string AntivirusOtherProduct = "antivirus_other_product";
    internal const string AntivirusOtherProductValue = "antivirus_other_product_value";
    internal const string DefenderReadFailed = "defender_read_failed";
    internal const string DefenderValueMissing = "defender_value_missing";
    internal const string DefenderStateUnclear = "defender_state_unclear";
    internal const string DefenderPassiveNoProduct = "defender_passive_no_product";
    internal const string DefenderOffOtherProduct = "defender_off_other_product";
    internal const string DefenderOffNoProduct = "defender_off_no_product";
    internal const string DefenderSilentOtherProduct = "defender_silent_other_product";
    internal const string DefenderSilentOnServer = "defender_silent_on_server";
    internal const string DefenderOffByPolicy = "defender_off_by_policy";
}

/// <summary>
/// The outcome of one check. It names the check by its stable id; name, description and
/// recommended state come from the baseline, so a result holds nothing that depends on the
/// display language.
/// </summary>
internal sealed record SecurityCheckResult
{
    public string Id { get; init; } = "";
    public SecurityAssessmentStatus Status { get; init; }
    /// <summary>What was found, as a <see cref="SecurityStateCodes"/> code.</summary>
    public string DetectedCode { get; init; } = SecurityStateCodes.Unknown;
    /// <summary>A language-neutral value that goes with the code: a version, a date, a product name.</summary>
    public string DetectedValue { get; init; } = "";
    public SecurityPolicyState Policy { get; init; }
    public bool RestartRequired { get; init; }
    /// <summary>The finding needs a person to judge it; the check cannot say pass or fail.</summary>
    public bool ManualReview { get; init; }
    /// <summary>A <see cref="SecurityReasonCodes"/> code or a provider's own, empty when the setting was judged.</summary>
    public string ReasonCode { get; init; } = "";
    /// <summary>Technical detail in English for the details view and the log. Never a secret.</summary>
    public string Detail { get; init; } = "";
    public DateTimeOffset CheckedAt { get; init; }
    public long DurationMs { get; init; }

    internal static SecurityCheckResult Unknown(string id, string reasonCode, string detail = "") =>
        new() { Id = id, Status = SecurityAssessmentStatus.Unknown, ReasonCode = reasonCode, Detail = detail };

    internal static SecurityCheckResult NotApplicable(string id, string reasonCode, string detail = "") =>
        new() { Id = id, Status = SecurityAssessmentStatus.NotApplicable, ReasonCode = reasonCode, Detail = detail };
}

/// <summary>What the engine knows about the machine before any check runs. Null = not read.</summary>
internal sealed record SecurityMachineInfo
{
    public string ComputerName { get; init; } = "";
    public string OsName { get; init; } = "";
    public string OsVersion { get; init; } = "";
    public int? OsBuild { get; init; }
    public bool? IsServerOs { get; init; }
    public bool IsElevated { get; init; }
    public string AppVersion { get; init; } = "";

    /// <summary>Registry and API reads only; nothing here starts a process or asks the network.</summary>
    internal static SecurityMachineInfo ReadCurrent()
    {
        var support = WindowsSupportReader.Read();
        int? productType = Try(WindowsProductType.Read);
        return new SecurityMachineInfo
        {
            ComputerName = Try(() => Environment.MachineName) ?? "",
            OsName = support.Name,
            OsVersion = support.Version,
            OsBuild = Try(() => (int?)Environment.OSVersion.Version.Build),
            IsServerOs = productType is null ? null : WindowsProductType.IsServer(productType),
            IsElevated = Try(() => (bool?)ElevationHelper.IsElevated) ?? false,
            AppVersion = Try(() => ProductVersion.Display) ?? ""
        };
    }

    private static T? Try<T>(Func<T?> read)
    {
        try { return read(); }
        catch { return default; }
    }
}

/// <summary>What a provider is told when it is asked to assess.</summary>
internal sealed record SecurityCheckContext
{
    public required SecurityBaseline Baseline { get; init; }
    public required SecurityMachineInfo Machine { get; init; }
    /// <summary>The provider's checks that apply to this machine. The others are already answered.</summary>
    public required IReadOnlyList<string> RequestedIds { get; init; }
    public DateTimeOffset Now { get; init; }
}

/// <summary>One finished or cancelled assessment: what the window shows and the cache keeps.</summary>
internal sealed record SecurityAssessmentSnapshot
{
    internal const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string BaselineId { get; init; } = "";
    public string ModuleVersion { get; init; } = "";
    public string AppVersion { get; init; } = "";
    public string ComputerName { get; init; } = "";
    public string OsName { get; init; } = "";
    public string OsVersion { get; init; } = "";
    public int? OsBuild { get; init; }
    public bool? IsServerOs { get; init; }
    public bool Elevated { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public long DurationMs { get; init; }
    /// <summary>False when the assessment was cancelled before every check had answered.</summary>
    public bool Completed { get; init; }
    public IReadOnlyList<SecurityCheckResult> Results { get; init; } = Array.Empty<SecurityCheckResult>();
}
