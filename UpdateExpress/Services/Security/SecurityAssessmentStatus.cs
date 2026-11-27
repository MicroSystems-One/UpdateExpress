namespace UpdateExpress.Services.Security;

/// <summary>
/// The judgement of one security check, kept apart from the setting that was detected.
/// <see cref="Unknown"/> is the zero value on purpose: a result nobody filled in, or a status
/// missing from a cache file, never reads as a pass.
/// </summary>
public enum SecurityAssessmentStatus
{
    Unknown = 0,
    Protected,
    NotProtected,
    NeedsAttention,
    ManagedByPolicy,
    RestartRequired,
    NotSupported,
    NotApplicable,
    Checking,
    Error
}

public enum SecurityCheckCategory
{
    NetworkSecurity,
    EndpointProtection,
    DeviceSecurity,
    IdentityAndAccess,
    UpdatesAndMaintenance,
    LoggingAndApplicationSecurity
}

/// <summary>Whether Group Policy or MDM holds the setting. Not looked at is not "not managed".</summary>
public enum SecurityPolicyState
{
    NotAssessed = 0,
    NotManaged,
    Managed,
    Unknown
}

public enum SecuritySeverity
{
    Informational = 0,
    Low,
    Medium,
    High
}

/// <summary>The remediation classes of the specification, section 7.</summary>
public enum SecurityRemediationLevel
{
    /// <summary>Level C: assessment only.</summary>
    AssessmentOnly = 0,
    /// <summary>Level A: low-risk controlled remediation.</summary>
    LowRisk,
    /// <summary>Level B: compatibility-sensitive remediation.</summary>
    CompatibilitySensitive
}

public enum SecurityPrivilege
{
    StandardUser = 0,
    Administrator
}

public enum SecurityApplicability
{
    ClientAndServer = 0,
    ClientOnly,
    ServerOnly
}

/// <summary>The summary cards of the Security Overview window.</summary>
public enum SecuritySummaryBucket
{
    Protected,
    NeedsAttention,
    ManualReview,
    UnknownOrUnsupported,
    RestartRequired,
    NotApplicable
}

/// <summary>
/// One colour for the whole assessment. Whatever shows it (an icon in the header, a button, a
/// line of text) reads this value; nothing here knows which control that is.
/// </summary>
public enum SecurityOverallLevel
{
    /// <summary>Grey: nothing to go on.</summary>
    Unknown = 0,
    Green,
    Orange,
    Red
}
