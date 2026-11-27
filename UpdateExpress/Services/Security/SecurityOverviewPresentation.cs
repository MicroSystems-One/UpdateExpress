using System;
using System.Collections.Generic;
using System.Linq;

namespace UpdateExpress.Services.Security;

/// <summary>The texts of one check as the window shows them. No WPF types, so it can be tested as text.</summary>
internal sealed record SecurityCheckRowText
{
    public string Id { get; init; } = "";
    public SecurityCheckCategory Category { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>What the status pill shows; "Checking…" while a check with no earlier answer runs.</summary>
    public SecurityAssessmentStatus Status { get; init; }
    public string StatusText { get; init; } = "";
    public string Detected { get; init; } = "";
    public string Recommended { get; init; } = "";
    public string Policy { get; init; } = "";
    public string Checked { get; init; } = "";
    public string Remediation { get; init; } = "";
    public string Restart { get; init; } = "";
    /// <summary>Why the check has this status, in the user's language. Empty when the setting was simply judged.</summary>
    public string Finding { get; init; } = "";
    /// <summary>The provider's own detail, in English. Empty when there is none.</summary>
    public string TechnicalDetail { get; init; } = "";
    public string Rationale { get; init; } = "";
    public string PolicyNote { get; init; } = "";
    public string Severity { get; init; } = "";
    public string ReferenceUrl { get; init; } = "";
    /// <summary>One line for a screen reader: the name and the status.</summary>
    public string AccessibleName { get; init; } = "";
}

/// <summary>
/// Turns definitions, results and the overall status into what the Security Overview window
/// and the header shield say. A check without an answer reads "Unknown" or "Checking…"; it is
/// never worded as off, disabled or failed.
/// </summary>
internal static class SecurityOverviewPresentation
{
    internal const string Grey = "#9099A6";
    internal const string Green = "#168A57";
    internal const string Orange = "#EA941B";
    internal const string Red = "#D13C36";
    private const string Blue = "#0067C0";
    private const string Dash = "—";

    internal static string LevelColor(SecurityOverallLevel level) => level switch
    {
        SecurityOverallLevel.Green => Green,
        SecurityOverallLevel.Orange => Orange,
        SecurityOverallLevel.Red => Red,
        _ => Grey
    };

    /// <summary>
    /// The colour of a status pill. Grey for everything that is not a judgement of the
    /// setting: unknown, not supported, not applicable and a check that could not run.
    /// </summary>
    internal static string StatusColor(SecurityAssessmentStatus status) => status switch
    {
        SecurityAssessmentStatus.Protected => Green,
        SecurityAssessmentStatus.NotProtected => Red,
        SecurityAssessmentStatus.NeedsAttention or SecurityAssessmentStatus.RestartRequired => Orange,
        SecurityAssessmentStatus.ManagedByPolicy => Blue,
        _ => Grey
    };

    internal static string BucketColor(SecuritySummaryBucket bucket) => bucket switch
    {
        SecuritySummaryBucket.Protected => Green,
        SecuritySummaryBucket.NeedsAttention or SecuritySummaryBucket.RestartRequired => Orange,
        SecuritySummaryBucket.ManualReview => Blue,
        _ => Grey
    };

    /// <param name="result">The answer to show, or null when the check has none.</param>
    /// <param name="checking">True while an assessment that has not answered this check yet runs.</param>
    internal static SecurityCheckRowText Row(
        SecurityCheckDefinition definition,
        SecurityCheckResult? result,
        bool checking,
        bool norwegian,
        Func<DateTime, string> formatTime)
    {
        string T(string english) => SecurityOverviewStrings.T(english, norwegian);

        var status = result?.Status ?? (checking ? SecurityAssessmentStatus.Checking : SecurityAssessmentStatus.Unknown);
        string statusText = SecurityOverviewStrings.Status(status, norwegian);
        if (result is not null && checking) statusText += " · " + SecurityOverviewStrings.Status(SecurityAssessmentStatus.Checking, norwegian);
        string name = T(definition.Name);

        string finding = result is null ? "" : SecurityOverviewStrings.Reason(result.ReasonCode, norwegian);
        if (result is { ManualReview: true })
            finding = (finding.Length > 0 ? finding + " " : "") + T(SecurityOverviewStrings.Ui.ManualReviewNote);

        return new SecurityCheckRowText
        {
            Id = definition.Id,
            Category = definition.Category,
            Name = name,
            Description = T(definition.Description),
            Status = status,
            StatusText = statusText,
            Detected = result is null
                ? (checking ? SecurityOverviewStrings.Status(SecurityAssessmentStatus.Checking, norwegian) : T("Unknown"))
                : SecurityOverviewStrings.State(result.DetectedCode, result.DetectedValue, norwegian),
            Recommended = SecurityOverviewStrings.State(definition.RecommendedCode, definition.RecommendedValue, norwegian),
            Policy = result is null ? Dash : SecurityOverviewStrings.Policy(result.Policy, norwegian),
            Checked = result is null || result.CheckedAt == default
                ? T(SecurityOverviewStrings.Ui.NotCheckedYet)
                : formatTime(result.CheckedAt.LocalDateTime),
            Remediation = T(RemediationEnglish(definition)),
            Restart = result is null
                ? Dash
                : T(result.RestartRequired || result.Status == SecurityAssessmentStatus.RestartRequired
                    ? SecurityOverviewStrings.Ui.RestartNeeded
                    : SecurityOverviewStrings.Ui.RestartNotNeeded),
            Finding = finding,
            TechnicalDetail = result?.Detail ?? "",
            Rationale = T(definition.Rationale),
            PolicyNote = T(definition.PolicyRestrictions),
            Severity = SecurityOverviewStrings.Severity(definition.Severity, norwegian),
            ReferenceUrl = IsWebLink(definition.ReferenceUrl) ? definition.ReferenceUrl : "",
            AccessibleName = name + ": " + statusText
        };
    }

    private static string RemediationEnglish(SecurityCheckDefinition definition) =>
        definition.RemediationLevel == SecurityRemediationLevel.AssessmentOnly
            ? SecurityOverviewStrings.Ui.RemediationAssessmentOnly
            : definition.RemediationAvailable
                ? SecurityOverviewStrings.Ui.RemediationAvailable
                : SecurityOverviewStrings.Ui.RemediationNotInThisVersion;

    /// <summary>Only https links leave the app; the window runs elevated.</summary>
    internal static bool IsWebLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>The line under the title: what the last finished assessment says overall, and when it was.</summary>
    internal static string OverallLine(SecurityOverall overall, bool norwegian, Func<DateTime, string> formatTime)
    {
        string text = SecurityOverviewStrings.Overall(overall, norwegian);
        if (overall.AssessedAt is not DateTimeOffset at) return text;
        string when = SecurityOverviewStrings.Tf(SecurityOverviewStrings.Ui.LastAssessed, norwegian, formatTime(at.LocalDateTime));
        // An old assessment says so by itself only when age is what decided the colour.
        if (overall.IsStale && overall.ReasonCode != SecurityOverallStatus.Stale)
            when += " " + StaleText(norwegian);
        return text + " " + when;
    }

    private static string StaleText(bool norwegian) =>
        SecurityOverviewStrings.Overall(
            new SecurityOverall(SecurityOverallLevel.Orange, SecurityOverallStatus.Stale, SecuritySummary.Empty, null, true), norwegian);

    /// <summary>The counts of the cards as one line, for the shield's tooltip.</summary>
    internal static string SummaryLine(SecuritySummary summary, bool norwegian)
    {
        var parts = new List<string>
        {
            Count(SecuritySummaryBucket.Protected, summary.Protected, norwegian),
            Count(SecuritySummaryBucket.NeedsAttention, summary.NeedsAttention, norwegian),
            Count(SecuritySummaryBucket.ManualReview, summary.ManualReview, norwegian),
            Count(SecuritySummaryBucket.UnknownOrUnsupported, summary.UnknownOrUnsupported, norwegian)
        };
        if (summary.RestartRequired > 0)
            parts.Add(Count(SecuritySummaryBucket.RestartRequired, summary.RestartRequired, norwegian));
        return string.Join(" · ", parts);
    }

    private static string Count(SecuritySummaryBucket bucket, int count, bool norwegian) =>
        SecurityOverviewStrings.Bucket(bucket, norwegian) + ": " + count;

    /// <summary>
    /// The tooltip of the shield in the main window's header: what it is, the overall status,
    /// when it was assessed, the counts, that it is no certification, and what a click does.
    /// </summary>
    internal static string ShieldToolTip(SecurityOverall overall, bool norwegian, Func<DateTime, string> formatTime)
    {
        var lines = new List<string>
        {
            SecurityOverviewStrings.T(SecurityOverviewStrings.Title, norwegian),
            OverallLine(overall, norwegian, formatTime)
        };
        if (overall.AssessedAt is not null) lines.Add(SummaryLine(overall.Summary, norwegian));
        lines.Add(SecurityOverviewStrings.T(SecurityOverviewStrings.HonestyLine, norwegian));
        lines.Add(SecurityOverviewStrings.T(SecurityOverviewStrings.Ui.ShieldOpen, norwegian));
        return string.Join(Environment.NewLine, lines.Where(line => line.Length > 0));
    }

    /// <summary>What a screen reader says for the shield. The colour alone carries nothing.</summary>
    internal static string ShieldAccessibleName(SecurityOverall overall, bool norwegian) =>
        SecurityOverviewStrings.T(SecurityOverviewStrings.Ui.ShieldStatus, norwegian) + ": " +
        SecurityOverviewStrings.Overall(overall, norwegian);
}
