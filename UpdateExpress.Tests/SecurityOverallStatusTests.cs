using UpdateExpress.Services.Security;
using Xunit;
using Status = UpdateExpress.Services.Security.SecurityAssessmentStatus;

namespace UpdateExpress.Tests;

public class SecurityOverallStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.FromHours(1));

    private static SecurityCheckResult Check(Status status, string id = "", bool manualReview = false) =>
        new() { Id = id.Length > 0 ? id : "T-" + status, Status = status, ManualReview = manualReview };

    private static SecurityAssessmentSnapshot Snapshot(params Status[] statuses) => Snapshot(Now, statuses);

    private static SecurityAssessmentSnapshot Snapshot(DateTimeOffset completedAt, params Status[] statuses) => new()
    {
        BaselineId = SecurityBaselineV1.BaselineId,
        Completed = true,
        StartedAt = completedAt.AddSeconds(-3),
        CompletedAt = completedAt,
        Results = statuses.Select((s, i) => Check(s, "T-" + i)).ToArray()
    };

    [Fact]
    public void No_assessment_is_grey()
    {
        var overall = SecurityOverallStatus.Evaluate(null, Now);

        Assert.Equal(SecurityOverallLevel.Unknown, overall.Level);
        Assert.Equal(SecurityOverallStatus.NotAssessed, overall.ReasonCode);
        Assert.Null(overall.AssessedAt);
        Assert.Equal(SecuritySummary.Empty, overall.Summary);
    }

    [Fact]
    public void A_cancelled_assessment_is_grey_whatever_it_holds()
    {
        var cancelled = Snapshot(Status.Protected, Status.NotProtected) with { Completed = false };

        Assert.Equal(SecurityOverallLevel.Unknown, SecurityOverallStatus.Evaluate(cancelled, Now).Level);
    }

    [Fact]
    public void An_assessment_without_results_is_grey()
    {
        Assert.Equal(SecurityOverallLevel.Unknown, SecurityOverallStatus.Evaluate(Snapshot(), Now).Level);
    }

    [Fact]
    public void All_protected_is_green()
    {
        var overall = SecurityOverallStatus.Evaluate(Snapshot(Status.Protected, Status.Protected), Now);

        Assert.Equal(SecurityOverallLevel.Green, overall.Level);
        Assert.Equal(SecurityOverallStatus.AllClear, overall.ReasonCode);
        Assert.Equal(Now, overall.AssessedAt);
        Assert.False(overall.IsStale);
    }

    [Theory]
    [InlineData(Status.Unknown)]
    [InlineData(Status.NotSupported)]
    [InlineData(Status.NotApplicable)]
    [InlineData(Status.ManagedByPolicy)]
    public void Unknown_unsupported_not_applicable_and_policy_managed_do_not_count_against_the_PC(Status status)
    {
        var overall = SecurityOverallStatus.Evaluate(Snapshot(Status.Protected, status), Now);

        Assert.Equal(SecurityOverallLevel.Green, overall.Level);
    }

    [Fact]
    public void One_not_protected_check_is_red_whatever_else_was_found()
    {
        var overall = SecurityOverallStatus.Evaluate(
            Snapshot(Status.Protected, Status.NeedsAttention, Status.RestartRequired, Status.Error, Status.Unknown, Status.NotProtected), Now);

        Assert.Equal(SecurityOverallLevel.Red, overall.Level);
        Assert.Equal(SecurityOverallStatus.NotProtected, overall.ReasonCode);
    }

    [Theory]
    [InlineData(Status.NeedsAttention, SecurityOverallStatus.NeedsAttention)]
    [InlineData(Status.RestartRequired, SecurityOverallStatus.RestartRequired)]
    [InlineData(Status.Error, SecurityOverallStatus.CheckError)]
    public void Attention_restart_and_a_failed_check_are_orange(Status status, string reason)
    {
        var overall = SecurityOverallStatus.Evaluate(Snapshot(Status.Protected, status), Now);

        Assert.Equal(SecurityOverallLevel.Orange, overall.Level);
        Assert.Equal(reason, overall.ReasonCode);
    }

    [Fact]
    public void A_check_that_failed_to_run_is_never_red()
    {
        var overall = SecurityOverallStatus.Evaluate(Snapshot(Status.Error, Status.Error, Status.Unknown), Now);

        Assert.Equal(SecurityOverallLevel.Orange, overall.Level);
    }

    [Fact]
    public void Nothing_but_unknowns_is_orange_not_green_and_not_red()
    {
        var overall = SecurityOverallStatus.Evaluate(Snapshot(Status.Unknown, Status.Unknown, Status.NotApplicable), Now);

        Assert.Equal(SecurityOverallLevel.Orange, overall.Level);
        Assert.Equal(SecurityOverallStatus.OnlyUnknown, overall.ReasonCode);
    }

    [Fact]
    public void Nothing_that_could_be_judged_is_grey()
    {
        var overall = SecurityOverallStatus.Evaluate(Snapshot(Status.NotApplicable, Status.NotSupported, Status.ManagedByPolicy), Now);

        Assert.Equal(SecurityOverallLevel.Unknown, overall.Level);
        Assert.Equal(SecurityOverallStatus.NothingAssessable, overall.ReasonCode);
    }

    [Fact]
    public void A_result_nobody_filled_in_is_unknown_and_never_a_pass()
    {
        var blank = new SecurityCheckResult { Id = "NET-001" };
        var snapshot = Snapshot() with { Results = new[] { blank } };

        Assert.Equal(Status.Unknown, blank.Status);
        Assert.Equal(Status.Unknown, default(Status));
        Assert.NotEqual(SecurityOverallLevel.Green, SecurityOverallStatus.Evaluate(snapshot, Now).Level);
    }

    [Fact]
    public void A_status_value_outside_the_enum_is_not_a_pass()
    {
        var odd = new SecurityCheckResult { Id = "X", Status = (Status)99 };
        var snapshot = Snapshot() with { Results = new[] { odd } };

        Assert.Equal(SecuritySummaryBucket.UnknownOrUnsupported, SecurityOverallStatus.BucketOf(odd));
        Assert.NotEqual(SecurityOverallLevel.Green, SecurityOverallStatus.Evaluate(snapshot, Now).Level);
    }

    [Fact]
    public void A_green_assessment_turns_orange_after_seven_days()
    {
        var sixDays = SecurityOverallStatus.Evaluate(Snapshot(Now.AddDays(-6), Status.Protected), Now);
        var eightDays = SecurityOverallStatus.Evaluate(Snapshot(Now.AddDays(-8), Status.Protected), Now);

        Assert.Equal(SecurityOverallLevel.Green, sixDays.Level);
        Assert.False(sixDays.IsStale);
        Assert.Equal(SecurityOverallLevel.Orange, eightDays.Level);
        Assert.Equal(SecurityOverallStatus.Stale, eightDays.ReasonCode);
        Assert.True(eightDays.IsStale);
    }

    [Fact]
    public void An_old_red_assessment_stays_red_and_is_marked_stale()
    {
        var overall = SecurityOverallStatus.Evaluate(Snapshot(Now.AddDays(-30), Status.NotProtected), Now);

        Assert.Equal(SecurityOverallLevel.Red, overall.Level);
        Assert.True(overall.IsStale);
    }

    [Fact]
    public void An_assessment_dated_in_the_future_is_not_trusted_as_fresh()
    {
        var overall = SecurityOverallStatus.Evaluate(Snapshot(Now.AddDays(3), Status.Protected), Now);

        Assert.True(overall.IsStale);
        Assert.Equal(SecurityOverallLevel.Orange, overall.Level);
    }

    [Theory]
    [InlineData(Status.Protected, SecuritySummaryBucket.Protected)]
    [InlineData(Status.NotProtected, SecuritySummaryBucket.NeedsAttention)]
    [InlineData(Status.NeedsAttention, SecuritySummaryBucket.NeedsAttention)]
    [InlineData(Status.ManagedByPolicy, SecuritySummaryBucket.ManualReview)]
    [InlineData(Status.RestartRequired, SecuritySummaryBucket.RestartRequired)]
    [InlineData(Status.NotSupported, SecuritySummaryBucket.UnknownOrUnsupported)]
    [InlineData(Status.Unknown, SecuritySummaryBucket.UnknownOrUnsupported)]
    [InlineData(Status.Error, SecuritySummaryBucket.UnknownOrUnsupported)]
    [InlineData(Status.NotApplicable, SecuritySummaryBucket.NotApplicable)]
    public void Each_status_is_counted_on_one_card(Status status, SecuritySummaryBucket bucket)
    {
        Assert.Equal(bucket, SecurityOverallStatus.BucketOf(Check(status)));
    }

    [Fact]
    public void An_unreadable_unsupported_or_failed_check_is_never_counted_as_needing_attention()
    {
        foreach (var status in new[] { Status.Unknown, Status.NotSupported, Status.NotApplicable, Status.Error })
            Assert.NotEqual(SecuritySummaryBucket.NeedsAttention, SecurityOverallStatus.BucketOf(Check(status)));
    }

    [Fact]
    public void An_unknown_that_needs_a_person_is_counted_as_manual_review()
    {
        Assert.Equal(SecuritySummaryBucket.ManualReview, SecurityOverallStatus.BucketOf(Check(Status.Unknown, manualReview: true)));
        // The flag does not move a judged check off its card.
        Assert.Equal(SecuritySummaryBucket.Protected, SecurityOverallStatus.BucketOf(Check(Status.Protected, manualReview: true)));
    }

    [Fact]
    public void A_row_without_an_answer_is_unknown_in_the_list_and_not_in_other_groups()
    {
        Assert.Equal(SecuritySummaryBucket.UnknownOrUnsupported, SecurityOverallStatus.ListBucket(null, checking: false));
        Assert.Null(SecurityOverallStatus.ListBucket(null, checking: true));
        Assert.False(SecurityOverallStatus.MatchesListBucket(SecuritySummaryBucket.Protected, null, checking: false));
        Assert.False(SecurityOverallStatus.MatchesListBucket(SecuritySummaryBucket.Protected, null, checking: true));
        Assert.True(SecurityOverallStatus.MatchesListBucket(SecuritySummaryBucket.UnknownOrUnsupported, null, checking: false));
    }

    [Fact]
    public void An_information_check_is_not_in_any_summary_card_group()
    {
        var info = Check(Status.NotApplicable) with { ReasonCode = SecurityReasonCodes.Informational };
        Assert.Equal(SecuritySummaryBucket.NotApplicable, SecurityOverallStatus.ListBucket(info, checking: false));
        Assert.False(SecurityOverallStatus.MatchesListBucket(SecuritySummaryBucket.Protected, info, checking: false));
        Assert.False(SecurityOverallStatus.MatchesListBucket(SecuritySummaryBucket.UnknownOrUnsupported, info, checking: false));
    }

    [Fact]
    public void A_check_that_is_still_running_is_on_no_card()
    {
        Assert.Null(SecurityOverallStatus.BucketOf(Check(Status.Checking)));
        Assert.Equal(SecuritySummary.Empty, SecurityOverallStatus.Summarize(new[] { Check(Status.Checking) }));
    }

    [Fact]
    public void The_summary_counts_every_card()
    {
        var summary = SecurityOverallStatus.Summarize(new[]
        {
            Check(Status.Protected), Check(Status.Protected), Check(Status.Protected),
            Check(Status.NotProtected), Check(Status.NeedsAttention),
            Check(Status.ManagedByPolicy),
            Check(Status.Unknown), Check(Status.NotSupported), Check(Status.Error),
            Check(Status.RestartRequired),
            Check(Status.NotApplicable), Check(Status.NotApplicable)
        });

        Assert.Equal(new SecuritySummary(3, 2, 1, 3, 1, 2), summary);
        Assert.Equal(SecuritySummary.Empty, SecurityOverallStatus.Summarize(null));
    }

    [Fact]
    public void The_overall_result_carries_the_summary()
    {
        var overall = SecurityOverallStatus.Evaluate(Snapshot(Status.Protected, Status.Unknown, Status.NotApplicable), Now);

        Assert.Equal(new SecuritySummary(1, 0, 0, 1, 0, 1), overall.Summary);
    }
}
