using System.Reflection;
using UpdateExpress.Services.Security;
using Xunit;

namespace UpdateExpress.Tests;

public class SecurityOverviewStringsTests
{
    /// <summary>Texts that read the same in both languages.</summary>
    private static readonly HashSet<string> SameInBothLanguages = new(StringComparer.Ordinal) { "{0}", "TPM 2.0" };

    private static void AssertHasNorwegian(string english, string what)
    {
        Assert.False(string.IsNullOrWhiteSpace(english), what);
        if (SameInBothLanguages.Contains(english)) return;
        Assert.True(SecurityOverviewStrings.HasNorwegian(english), $"No Norwegian text for {what}: \"{english}\"");
        Assert.NotEqual(english, SecurityOverviewStrings.T(english, norwegian: true));
        Assert.Equal(english, SecurityOverviewStrings.T(english, norwegian: false));
    }

    private static IEnumerable<string> Constants(Type type) =>
        type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void Every_status_category_card_and_policy_state_has_both_languages()
    {
        foreach (var status in Enum.GetValues<SecurityAssessmentStatus>())
            AssertHasNorwegian(SecurityOverviewStrings.StatusEnglish(status), "status " + status);
        foreach (var category in Enum.GetValues<SecurityCheckCategory>())
            AssertHasNorwegian(SecurityOverviewStrings.CategoryEnglish(category), "category " + category);
        foreach (var bucket in Enum.GetValues<SecuritySummaryBucket>())
            AssertHasNorwegian(SecurityOverviewStrings.BucketEnglish(bucket), "card " + bucket);
        foreach (var policy in Enum.GetValues<SecurityPolicyState>())
            AssertHasNorwegian(SecurityOverviewStrings.PolicyEnglish(policy), "policy " + policy);
        AssertHasNorwegian("All", "the category filter for everything");
        AssertHasNorwegian(SecurityOverviewStrings.Title, "the title");
        AssertHasNorwegian(SecurityOverviewStrings.HonestyLine, "the honesty line");
    }

    [Fact]
    public void Statuses_have_distinct_names_in_each_language()
    {
        foreach (bool norwegian in new[] { false, true })
        {
            var names = Enum.GetValues<SecurityAssessmentStatus>().Select(s => SecurityOverviewStrings.Status(s, norwegian)).ToArray();
            Assert.Equal(names.Length, names.Distinct().Count());
        }
    }

    [Fact]
    public void Every_state_code_has_both_languages()
    {
        var codes = Constants(typeof(SecurityStateCodes)).ToArray();

        Assert.NotEmpty(codes);
        foreach (string code in codes)
        {
            string? english = SecurityOverviewStrings.StateEnglish(code);
            Assert.False(english is null, "No text for state code " + code);
            AssertHasNorwegian(english!, "state code " + code);
        }
    }

    [Fact]
    public void Every_reason_code_has_both_languages()
    {
        var codes = Constants(typeof(SecurityReasonCodes)).ToArray();

        Assert.NotEmpty(codes);
        foreach (string code in codes)
        {
            string? english = SecurityOverviewStrings.ReasonEnglish(code);
            Assert.False(english is null, "No text for reason code " + code);
            AssertHasNorwegian(english!, "reason code " + code);
        }
    }

    [Fact]
    public void Every_overall_reason_has_both_languages()
    {
        string[] reasons =
        {
            SecurityOverallStatus.NotAssessed, SecurityOverallStatus.NothingAssessable, SecurityOverallStatus.NotProtected,
            SecurityOverallStatus.NeedsAttention, SecurityOverallStatus.RestartRequired, SecurityOverallStatus.CheckError,
            SecurityOverallStatus.OnlyUnknown, SecurityOverallStatus.Stale, SecurityOverallStatus.AllClear
        };

        var texts = reasons.Select(SecurityOverviewStrings.OverallEnglish).ToArray();

        Assert.Equal(texts.Length, texts.Distinct().Count());
        for (int i = 0; i < reasons.Length; i++) AssertHasNorwegian(texts[i], "overall reason " + reasons[i]);
    }

    [Fact]
    public void Every_check_in_the_baseline_has_a_Norwegian_name_and_description()
    {
        foreach (var definition in SecurityBaselineV1.Create().Definitions)
        {
            AssertHasNorwegian(definition.Name, definition.Id + " name");
            AssertHasNorwegian(definition.Description, definition.Id + " description");
        }
    }

    [Fact]
    public void A_state_is_shown_in_the_chosen_language_with_its_value()
    {
        Assert.Equal("On", SecurityOverviewStrings.State(SecurityStateCodes.On, "", norwegian: false));
        Assert.Equal("På", SecurityOverviewStrings.State(SecurityStateCodes.On, null, norwegian: true));
        Assert.Equal("Within the last 2 days", SecurityOverviewStrings.State(SecurityStateCodes.WithinDays, "2", norwegian: false));
        Assert.Equal("Innen de siste 45 dagene", SecurityOverviewStrings.State(SecurityStateCodes.WithinDays, "45", norwegian: true));
        Assert.Equal("1.463.492.0", SecurityOverviewStrings.State(SecurityStateCodes.Value, "1.463.492.0", norwegian: true));
        Assert.Equal("Aktiv: Windows Defender", SecurityOverviewStrings.State(SecurityStateCodes.Active, "Windows Defender", norwegian: true));
    }

    [Fact]
    public void A_state_without_a_text_or_without_its_value_reads_unknown_never_as_some_other_state()
    {
        Assert.Equal("Unknown", SecurityOverviewStrings.State("something_new", "", norwegian: false));
        Assert.Equal("Ukjent", SecurityOverviewStrings.State(null, null, norwegian: true));
        Assert.Equal("Ukjent", SecurityOverviewStrings.State("", " ", norwegian: true));
        Assert.Equal("Unknown", SecurityOverviewStrings.State(SecurityStateCodes.WithinDays, "", norwegian: false));
        Assert.Equal("Unknown", SecurityOverviewStrings.State(SecurityStateCodes.Value, "", norwegian: false));
        // A code from a newer version still shows what was found.
        Assert.Equal("Some product", SecurityOverviewStrings.State("something_new", "Some product", norwegian: false));
    }

    [Fact]
    public void A_reason_is_empty_translated_or_the_code_itself()
    {
        Assert.Equal("", SecurityOverviewStrings.Reason("", norwegian: true));
        Assert.Equal("", SecurityOverviewStrings.Reason(null, norwegian: false));
        Assert.Equal("Sjekken svarte ikke i tide.", SecurityOverviewStrings.Reason(SecurityReasonCodes.Timeout, norwegian: true));
        Assert.Equal("Not relevant on Windows Server.", SecurityOverviewStrings.Reason(SecurityReasonCodes.ServerOs, norwegian: false));
        Assert.Equal("provider_specific", SecurityOverviewStrings.Reason("provider_specific", norwegian: true));
    }

    [Fact]
    public void The_overall_text_names_the_number_of_days_for_an_old_assessment()
    {
        var stale = new SecurityOverall(SecurityOverallLevel.Orange, SecurityOverallStatus.Stale, SecuritySummary.Empty, null, true);
        var clear = new SecurityOverall(SecurityOverallLevel.Green, SecurityOverallStatus.AllClear, SecuritySummary.Empty, null, false);

        Assert.Equal("The last assessment is more than 7 days old.", SecurityOverviewStrings.Overall(stale, norwegian: false));
        Assert.Equal("Siste vurdering er mer enn 7 dager gammel.", SecurityOverviewStrings.Overall(stale, norwegian: true));
        Assert.Equal("Ingen sjekker fant et problem.", SecurityOverviewStrings.Overall(clear, norwegian: true));
    }

    [Fact]
    public void Green_is_never_described_as_secure_safe_or_compliant()
    {
        var clear = new SecurityOverall(SecurityOverallLevel.Green, SecurityOverallStatus.AllClear, SecuritySummary.Empty, null, false);

        foreach (bool norwegian in new[] { false, true })
        {
            string text = SecurityOverviewStrings.Overall(clear, norwegian).ToLowerInvariant();
            foreach (string claim in new[] { "secure", "safe", "compliant", "sikker", "trygg", "samsvar" })
                Assert.DoesNotContain(claim, text);
        }
        Assert.Contains("not a security certification", SecurityOverviewStrings.HonestyLine);
    }
}
