using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UpdateExpress.Services.Security;

/// <summary>
/// Reads one source of Windows state and answers the checks that source decides. One provider
/// may answer several checks, so that the three firewall profiles or the Defender values cost
/// one read, not one each.
/// A provider only reads. It never changes a setting, and it never reads a password, a
/// recovery key or another secret.
/// </summary>
internal interface ISecurityCheckProvider
{
    /// <summary>Short name for the log.</summary>
    string Name { get; }

    /// <summary>The checks this provider answers, by their stable ids.</summary>
    IReadOnlyList<string> CheckIds { get; }

    /// <summary>Longest time one assessment may take, or null for the engine's default.</summary>
    TimeSpan? Timeout => null;

    /// <summary>
    /// One result for each id in <see cref="SecurityCheckContext.RequestedIds"/>. A value that
    /// could not be read is <see cref="SecurityAssessmentStatus.Unknown"/>, never a failed check.
    /// </summary>
    Task<IReadOnlyList<SecurityCheckResult>> AssessAsync(SecurityCheckContext context, CancellationToken cancellationToken);
}
