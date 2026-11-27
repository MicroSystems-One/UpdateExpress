using System;
using System.Collections.Generic;

namespace UpdateExpress.Services.Security;

/// <summary>The numbers on the summary cards. They are counts, not a score.</summary>
internal sealed record SecuritySummary(
    int Protected,
    int NeedsAttention,
    int ManualReview,
    int UnknownOrUnsupported,
    int RestartRequired,
    int NotApplicable)
{
    internal static readonly SecuritySummary Empty = new(0, 0, 0, 0, 0, 0);
}

/// <summary>The overall colour with what it rests on.</summary>
internal sealed record SecurityOverall(
    SecurityOverallLevel Level,
    string ReasonCode,
    SecuritySummary Summary,
    DateTimeOffset? AssessedAt,
    bool IsStale);

/// <summary>
/// One colour and the card counts for an assessment. It is a snapshot of what the checks
/// found; green means "no check found a problem", not "this PC is secure" and not compliance
/// with any standard.
/// </summary>
internal static class SecurityOverallStatus
{
    /// <summary>An assessment older than this is no longer shown as green.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    internal const string NotAssessed = "not_assessed";
    internal const string NothingAssessable = "nothing_assessable";
    internal const string NotProtected = "not_protected";
    internal const string NeedsAttention = "needs_attention";
    internal const string RestartRequired = "restart_required";
    internal const string CheckError = "check_error";
    internal const string OnlyUnknown = "only_unknown";
    internal const string Stale = "stale";
    internal const string AllClear = "all_clear";

    /// <summary>
    /// Grey: no finished assessment, or nothing in it that could be judged.
    /// Red: a check found a setting that does not protect.
    /// Orange: a check needs attention, a restart is pending, a check failed to run, nothing
    /// but unknowns came back, or an otherwise green assessment is older than
    /// <see cref="StaleAfter"/>.
    /// Green: at least one check is protected and none of the above.
    /// Unknown, not supported, not applicable and managed by policy never count against the PC.
    /// </summary>
    internal static SecurityOverall Evaluate(SecurityAssessmentSnapshot? snapshot, DateTimeOffset now)
    {
        if (snapshot is null || !snapshot.Completed || snapshot.Results is null || snapshot.Results.Count == 0)
            return new(SecurityOverallLevel.Unknown, NotAssessed, SecuritySummary.Empty, null, false);

        var summary = Summarize(snapshot.Results);
        var age = now - snapshot.CompletedAt;
        // A time stamp from the future (the clock was set back) is as little to trust as an old one.
        bool stale = age > StaleAfter || age < TimeSpan.FromDays(-1);

        bool notProtected = false, attention = false, restart = false, error = false, unknown = false, protectedFound = false;
        foreach (var result in snapshot.Results)
        {
            switch (result.Status)
            {
                case SecurityAssessmentStatus.NotProtected: notProtected = true; break;
                case SecurityAssessmentStatus.NeedsAttention: attention = true; break;
                case SecurityAssessmentStatus.RestartRequired: restart = true; break;
                case SecurityAssessmentStatus.Error: error = true; break;
                case SecurityAssessmentStatus.Unknown:
                case SecurityAssessmentStatus.Checking: unknown = true; break;
                case SecurityAssessmentStatus.Protected: protectedFound = true; break;
            }
        }

        SecurityOverall Result(SecurityOverallLevel level, string reason) =>
            new(level, reason, summary, snapshot.CompletedAt, stale);

        if (notProtected) return Result(SecurityOverallLevel.Red, NotProtected);
        if (attention) return Result(SecurityOverallLevel.Orange, NeedsAttention);
        if (restart) return Result(SecurityOverallLevel.Orange, RestartRequired);
        // A check that failed to run says nothing about the setting, so it is not red; it does
        // leave a hole in the picture, so it is not green either.
        if (error) return Result(SecurityOverallLevel.Orange, CheckError);
        if (protectedFound)
            return stale ? Result(SecurityOverallLevel.Orange, Stale) : Result(SecurityOverallLevel.Green, AllClear);
        if (unknown) return Result(SecurityOverallLevel.Orange, OnlyUnknown);
        return Result(SecurityOverallLevel.Unknown, NothingAssessable);
    }

    /// <summary>
    /// The card a row belongs in when the list is filtered. A check still running with no
    /// answer yet is on no card. One without an answer after the run is unknown.
    /// </summary>
    internal static SecuritySummaryBucket? ListBucket(SecurityCheckResult? result, bool checking)
    {
        if (result is null) return checking ? null : SecuritySummaryBucket.UnknownOrUnsupported;
        return BucketOf(result);
    }

    internal static bool MatchesListBucket(SecuritySummaryBucket? filter, SecurityCheckResult? result, bool checking)
    {
        if (filter is null) return true;
        var bucket = ListBucket(result, checking);
        return bucket is not null && bucket == filter;
    }

    /// <summary>The card a result is counted on, or null for a check that is still running.</summary>
    internal static SecuritySummaryBucket? BucketOf(SecurityCheckResult result) => result.Status switch
    {
        SecurityAssessmentStatus.Checking => null,
        SecurityAssessmentStatus.NotApplicable => SecuritySummaryBucket.NotApplicable,
        SecurityAssessmentStatus.RestartRequired => SecuritySummaryBucket.RestartRequired,
        SecurityAssessmentStatus.Protected => SecuritySummaryBucket.Protected,
        SecurityAssessmentStatus.NotProtected or SecurityAssessmentStatus.NeedsAttention => SecuritySummaryBucket.NeedsAttention,
        // Policy holds the setting: whoever owns the policy judges it, this app cannot change it.
        SecurityAssessmentStatus.ManagedByPolicy => SecuritySummaryBucket.ManualReview,
        SecurityAssessmentStatus.Unknown when result.ManualReview => SecuritySummaryBucket.ManualReview,
        // Unknown, NotSupported, Error: no judgement of the setting was possible.
        _ => SecuritySummaryBucket.UnknownOrUnsupported
    };

    internal static SecuritySummary Summarize(IEnumerable<SecurityCheckResult>? results)
    {
        int ok = 0, attention = 0, review = 0, unknown = 0, restart = 0, notApplicable = 0;
        foreach (var result in results ?? Array.Empty<SecurityCheckResult>())
        {
            switch (BucketOf(result))
            {
                case SecuritySummaryBucket.Protected: ok++; break;
                case SecuritySummaryBucket.NeedsAttention: attention++; break;
                case SecuritySummaryBucket.ManualReview: review++; break;
                case SecuritySummaryBucket.UnknownOrUnsupported: unknown++; break;
                case SecuritySummaryBucket.RestartRequired: restart++; break;
                case SecuritySummaryBucket.NotApplicable: notApplicable++; break;
            }
        }
        return new(ok, attention, review, unknown, restart, notApplicable);
    }
}
