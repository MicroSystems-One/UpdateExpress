using System.Globalization;
using UpdateExpress.Services.Security;
using Xunit;
using Status = UpdateExpress.Services.Security.SecurityAssessmentStatus;
using Ui = UpdateExpress.Services.Security.SecurityOverviewStrings.Ui;

namespace UpdateExpress.Tests;

public class SecurityOverviewPresentationTests
{
    private static readonly SecurityBaseline Baseline = SecurityBaselineV1.Create();
    private static readonly DateTimeOffset At = new(2026, 11, 27, 12, 40, 0, TimeSpan.FromHours(1));

    private static string Time(DateTime time) => time.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The time of the test assessment as this machine's clock shows it.</summary>
    private static readonly string Stamp = Time(At.LocalDateTime);

    private static SecurityCheckRowText Row(string id, SecurityCheckResult? result, bool checking = false, bool norwegian = false) =>
        SecurityOverviewPresentation.Row(Baseline.Find(id)!, result, checking, norwegian, Time);

    /// <summary>Words that would say a setting is bad. A check without a judgement must use none of them.</summary>
    private static readonly string[] BadWords =
    {
        "Off", "Not protected", "Disabled", "Needs attention", "Av", "Ikke beskyttet", "Deaktivert", "Krever oppmerksomhet"
    };

    private static void AssertSaysNothingBad(SecurityCheckRowText row)
    {
        foreach (string text in new[] { row.StatusText, row.Detected, row.Policy, row.Restart, row.Finding, row.AccessibleName })
            foreach (string word in BadWords)
                Assert.False(string.Equals(text, word, StringComparison.OrdinalIgnoreCase) || text.Contains(word + ":", StringComparison.OrdinalIgnoreCase)
                             || text.EndsWith(": " + word, StringComparison.OrdinalIgnoreCase),
                    $"\"{text}\" reads as a bad state");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_check_without_an_answer_reads_unknown_and_not_checked(bool norwegian)
    {
        var row = Row("NET-001", null, norwegian: norwegian);

        Assert.Equal(Status.Unknown, row.Status);
        Assert.Equal(norwegian ? "Ukjent" : "Unknown", row.StatusText);
        Assert.Equal(norwegian ? "Ukjent" : "Unknown", row.Detected);
        Assert.Equal(norwegian ? "På" : "On", row.Recommended);
        Assert.Equal("—", row.Policy);
        Assert.Equal("—", row.Restart);
        Assert.Equal(norwegian ? "Ikke sjekket ennå" : "Not checked yet", row.Checked);
        Assert.Equal("", row.Finding);
        AssertSaysNothingBad(row);
    }

    [Fact]
    public void A_check_that_is_running_for_the_first_time_reads_checking()
    {
        var row = Row("END-003", null, checking: true);

        Assert.Equal(Status.Checking, row.Status);
        Assert.Equal("Checking…", row.StatusText);
        Assert.Equal("Checking…", row.Detected);
        AssertSaysNothingBad(row);
    }

    [Fact]
    public void A_check_that_is_running_again_keeps_its_earlier_answer_and_says_that_it_is_checking()
    {
        var earlier = new SecurityCheckResult { Id = "NET-002", Status = Status.Protected, DetectedCode = SecurityStateCodes.On, CheckedAt = At };

        var row = Row("NET-002", earlier, checking: true, norwegian: true);

        Assert.Equal(Status.Protected, row.Status);
        Assert.Equal("Beskyttet · Sjekker…", row.StatusText);
        Assert.Equal("På", row.Detected);
        Assert.Equal(Stamp, row.Checked);
    }

    [Theory]
    [InlineData(Status.Unknown, SecurityReasonCodes.Timeout)]
    [InlineData(Status.Unknown, SecurityReasonCodes.Cancelled)]
    [InlineData(Status.Unknown, SecurityReasonCodes.NoResult)]
    [InlineData(Status.Error, SecurityReasonCodes.ProviderError)]
    [InlineData(Status.NotSupported, SecurityReasonCodes.OsTooOld)]
    [InlineData(Status.NotApplicable, SecurityReasonCodes.ServerOs)]
    public void A_check_that_could_not_be_judged_is_never_worded_or_coloured_as_a_bad_state(Status status, string reason)
    {
        var result = new SecurityCheckResult { Id = "NET-001", Status = status, ReasonCode = reason, CheckedAt = At };

        foreach (bool norwegian in new[] { false, true })
        {
            var row = Row("NET-001", result, norwegian: norwegian);

            AssertSaysNothingBad(row);
            Assert.Equal(norwegian ? "Ukjent" : "Unknown", row.Detected);
            Assert.NotEqual("", row.Finding);
            Assert.NotEqual(reason, row.Finding);
        }
        Assert.Equal(SecurityOverviewPresentation.Grey, SecurityOverviewPresentation.StatusColor(status));
    }

    [Fact]
    public void Only_a_judged_setting_gets_green_orange_or_red()
    {
        Assert.Equal(SecurityOverviewPresentation.Green, SecurityOverviewPresentation.StatusColor(Status.Protected));
        Assert.Equal(SecurityOverviewPresentation.Red, SecurityOverviewPresentation.StatusColor(Status.NotProtected));
        Assert.Equal(SecurityOverviewPresentation.Orange, SecurityOverviewPresentation.StatusColor(Status.NeedsAttention));
        Assert.Equal(SecurityOverviewPresentation.Orange, SecurityOverviewPresentation.StatusColor(Status.RestartRequired));
        foreach (var status in new[] { Status.Unknown, Status.Checking, Status.Error, Status.NotSupported, Status.NotApplicable })
            Assert.Equal(SecurityOverviewPresentation.Grey, SecurityOverviewPresentation.StatusColor(status));
        // Managed by policy is information, in the accent colour; it is neither a pass nor a fault.
        Assert.DoesNotContain(SecurityOverviewPresentation.StatusColor(Status.ManagedByPolicy),
            new[] { SecurityOverviewPresentation.Green, SecurityOverviewPresentation.Red, SecurityOverviewPresentation.Orange });
    }

    [Fact]
    public void The_overall_colours_are_the_ones_the_main_window_already_uses()
    {
        Assert.Equal("#9099A6", SecurityOverviewPresentation.LevelColor(SecurityOverallLevel.Unknown));
        Assert.Equal("#168A57", SecurityOverviewPresentation.LevelColor(SecurityOverallLevel.Green));
        Assert.Equal("#EA941B", SecurityOverviewPresentation.LevelColor(SecurityOverallLevel.Orange));
        Assert.Equal("#D13C36", SecurityOverviewPresentation.LevelColor(SecurityOverallLevel.Red));
    }

    [Fact]
    public void A_judged_check_shows_every_field_of_the_specification()
    {
        var result = new SecurityCheckResult
        {
            Id = "NET-003", Status = Status.NotProtected, DetectedCode = SecurityStateCodes.Off,
            Policy = SecurityPolicyState.NotManaged, CheckedAt = At, Detail = "Enabled=False in the active store"
        };

        var row = Row("NET-003", result);

        Assert.Equal("Windows Firewall – Public profile", row.Name);
        Assert.Equal("Windows Firewall for networks marked as public, such as a guest or café network.", row.Description);
        Assert.Equal("Off", row.Detected);
        Assert.Equal("On", row.Recommended);
        Assert.Equal("Not protected", row.StatusText);
        Assert.Equal("Not managed by policy", row.Policy);
        Assert.Equal(Stamp, row.Checked);
        Assert.Equal("Not available in this version", row.Remediation);
        Assert.Equal("Not required", row.Restart);
        Assert.Equal("Enabled=False in the active store", row.TechnicalDetail);
        Assert.Equal("High", row.Severity);
        Assert.StartsWith("https://learn.microsoft.com/", row.ReferenceUrl);
        Assert.Equal("Windows Firewall – Public profile: Not protected", row.AccessibleName);
        Assert.NotEqual("", row.Rationale);
        Assert.NotEqual("", row.PolicyNote);
    }

    [Fact]
    public void The_same_check_reads_in_Norwegian()
    {
        var result = new SecurityCheckResult
        {
            Id = "NET-003", Status = Status.Protected, DetectedCode = SecurityStateCodes.On,
            Policy = SecurityPolicyState.Managed, CheckedAt = At, RestartRequired = true
        };

        var row = Row("NET-003", result, norwegian: true);

        Assert.Equal("Windows-brannmur – offentlig profil", row.Name);
        Assert.Equal("På", row.Detected);
        Assert.Equal("Beskyttet", row.StatusText);
        Assert.Equal("Styrt av policy", row.Policy);
        Assert.Equal("Ikke tilgjengelig i denne versjonen", row.Remediation);
        Assert.Equal("Kreves", row.Restart);
        Assert.Equal("Høy", row.Severity);
        Assert.StartsWith("Microsoft anbefaler", row.Rationale);
        Assert.StartsWith("Gruppepolicy", row.PolicyNote);
    }

    [Fact]
    public void A_check_that_Update_Express_will_never_change_says_assessment_only()
    {
        Assert.Equal("Assessment only", Row("ACC-007", null).Remediation);
        Assert.Equal("Kun vurdering", Row("DEV-003", null, norwegian: true).Remediation);
    }

    [Fact]
    public void No_check_of_phase_1_offers_a_remediation()
    {
        foreach (var definition in Baseline.Definitions)
        {
            Assert.False(definition.RemediationAvailable, definition.Id);
            Assert.NotEqual("Available", SecurityOverviewPresentation.Row(definition, null, false, false, Time).Remediation);
        }
    }

    [Fact]
    public void A_finding_that_needs_a_person_says_so()
    {
        var result = new SecurityCheckResult
        {
            Id = "END-002", Status = Status.Unknown, ManualReview = true, DetectedCode = SecurityStateCodes.Value,
            DetectedValue = "Some Antivirus", CheckedAt = At
        };

        Assert.Equal("This finding needs a person to judge it.", Row("END-002", result).Finding);
        Assert.Equal("Dette funnet må vurderes av en person.", Row("END-002", result, norwegian: true).Finding);
        Assert.Equal("Some Antivirus", Row("END-002", result).Detected);
    }

    [Theory]
    [InlineData("https://learn.microsoft.com/windows/", true)]
    [InlineData("http://learn.microsoft.com/windows/", false)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData("C:\\Windows\\System32\\cmd.exe", false)]
    [InlineData("ms-settings:windowsupdate", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_an_https_page_counts_as_a_link(string? url, bool expected)
    {
        Assert.Equal(expected, SecurityOverviewPresentation.IsWebLink(url));
    }

    [Fact]
    public void Every_link_of_the_baseline_is_an_https_page_or_absent()
    {
        foreach (var definition in Baseline.Definitions)
            Assert.True(definition.ReferenceUrl.Length == 0 || SecurityOverviewPresentation.IsWebLink(definition.ReferenceUrl), definition.Id);
    }

    private static SecurityAssessmentSnapshot Snapshot(DateTimeOffset completedAt, params SecurityCheckResult[] results) => new()
    {
        BaselineId = SecurityBaselineV1.BaselineId, Completed = true, StartedAt = completedAt, CompletedAt = completedAt, Results = results
    };

    private static SecurityCheckResult Result(string id, Status status) => new() { Id = id, Status = status, CheckedAt = At };

    [Fact]
    public void The_shield_without_an_assessment_says_not_assessed_and_what_a_click_does()
    {
        var overall = SecurityOverallStatus.Evaluate(null, At);

        string tip = SecurityOverviewPresentation.ShieldToolTip(overall, norwegian: false, Time);

        Assert.Equal(
            new[]
            {
                "Security Overview",
                "Not assessed yet.",
                "Assessment snapshot – not a security certification.",
                "Click to open the Security Overview."
            },
            tip.Split(Environment.NewLine));
        Assert.Equal("Security Overview status: Not assessed yet.", SecurityOverviewPresentation.ShieldAccessibleName(overall, norwegian: false));
        Assert.Equal("Status for sikkerhetsoversikt: Ikke vurdert ennå.", SecurityOverviewPresentation.ShieldAccessibleName(overall, norwegian: true));
    }

    [Fact]
    public void The_shield_with_an_assessment_says_the_status_the_time_the_counts_and_that_it_is_no_certification()
    {
        var snapshot = Snapshot(At, Result("NET-001", Status.Protected), Result("NET-002", Status.Protected), Result("NET-003", Status.NotProtected), Result("END-002", Status.Unknown));
        var overall = SecurityOverallStatus.Evaluate(snapshot, At.AddMinutes(5));

        string[] tip = SecurityOverviewPresentation.ShieldToolTip(overall, norwegian: true, Time).Split(Environment.NewLine);

        Assert.Equal("Sikkerhetsoversikt", tip[0]);
        Assert.Equal("Minst én sjekk er ikke beskyttet. Sist vurdert: " + Stamp, tip[1]);
        Assert.Equal("Beskyttet: 2 · Krever oppmerksomhet: 1 · Manuell vurdering: 0 · Ukjent eller ikke støttet: 1", tip[2]);
        Assert.Equal("Øyeblikksbilde av en vurdering – ikke en sikkerhetssertifisering.", tip[3]);
        Assert.Equal("Klikk for å åpne sikkerhetsoversikten.", tip[4]);
    }

    [Fact]
    public void The_shield_is_not_named_like_the_antivirus_shield_in_the_health_check_row()
    {
        // MainWindow.xaml: the Defender shield's tooltip.
        const string defenderShield = "Antivirus protection status";
        foreach (var level in new SecurityAssessmentSnapshot?[] { null, Snapshot(At, Result("NET-001", Status.Protected)) })
        {
            var overall = SecurityOverallStatus.Evaluate(level, At);
            Assert.DoesNotContain(defenderShield, SecurityOverviewPresentation.ShieldAccessibleName(overall, false));
            Assert.DoesNotContain(defenderShield, SecurityOverviewPresentation.ShieldToolTip(overall, false, Time));
            Assert.StartsWith("Security Overview", SecurityOverviewPresentation.ShieldAccessibleName(overall, false));
        }
    }

    [Fact]
    public void An_old_assessment_says_so_once()
    {
        var green = Snapshot(At, Result("NET-001", Status.Protected));
        var red = Snapshot(At, Result("NET-001", Status.NotProtected));
        var later = At.AddDays(8);

        string greenLine = SecurityOverviewPresentation.OverallLine(SecurityOverallStatus.Evaluate(green, later), false, Time);
        string redLine = SecurityOverviewPresentation.OverallLine(SecurityOverallStatus.Evaluate(red, later), false, Time);

        Assert.Equal("The last assessment is more than 7 days old. Last assessed: " + Stamp, greenLine);
        Assert.Equal("At least one check is not protected. Last assessed: " + Stamp + " The last assessment is more than 7 days old.", redLine);
    }

    [Fact]
    public void The_restart_count_is_in_the_summary_only_when_there_is_one()
    {
        Assert.DoesNotContain("Restart", SecurityOverviewPresentation.SummaryLine(new SecuritySummary(1, 0, 0, 0, 0, 3), false));
        Assert.EndsWith("Restart required: 2", SecurityOverviewPresentation.SummaryLine(new SecuritySummary(1, 0, 0, 0, 2, 0), false));
    }

    [Fact]
    public void Green_wording_makes_no_promise_in_the_tooltip_either()
    {
        var overall = SecurityOverallStatus.Evaluate(Snapshot(At, Result("NET-001", Status.Protected)), At);
        Assert.Equal(SecurityOverallLevel.Green, overall.Level);

        foreach (bool norwegian in new[] { false, true })
        {
            string tip = SecurityOverviewPresentation.ShieldToolTip(overall, norwegian, Time).ToLowerInvariant();
            // "sikkerhetssertifisering" and "security certification" are the denial itself.
            tip = tip.Replace("ikke en sikkerhetssertifisering", "").Replace("not a security certification", "")
                .Replace("sikkerhetsoversikt", "").Replace("security overview", "");
            foreach (string claim in new[] { "secure", "safe", "compliant", "sikker", "trygg", "samsvar" })
                Assert.DoesNotContain(claim, tip);
        }
    }

    [Fact]
    public void Every_text_of_the_window_has_both_languages()
    {
        foreach (var field in typeof(Ui).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
        {
            string english = (string)field.GetRawConstantValue()!;
            if (english == Ui.Policy) continue; // "Policy" is the Norwegian word too.
            Assert.True(SecurityOverviewStrings.HasNorwegian(english), "No Norwegian text for " + field.Name);
            Assert.NotEqual(english, SecurityOverviewStrings.T(english, norwegian: true));
        }
    }

    [Fact]
    public void The_Norwegian_label_of_the_technical_detail_says_that_the_text_is_English()
    {
        // The detail comes from the check itself and is not translated; the label must not promise otherwise.
        Assert.Contains("engelsk", SecurityOverviewStrings.T(Ui.TechnicalDetail, norwegian: true));
        Assert.Equal("Technical detail", SecurityOverviewStrings.T(Ui.TechnicalDetail, norwegian: false));
    }

    [Fact]
    public void Every_reason_and_policy_note_of_the_baseline_has_a_Norwegian_text()
    {
        foreach (var definition in Baseline.Definitions)
        {
            Assert.True(SecurityOverviewStrings.HasNorwegian(definition.Rationale), definition.Id + " rationale");
            if (definition.PolicyRestrictions.Length > 0)
                Assert.True(SecurityOverviewStrings.HasNorwegian(definition.PolicyRestrictions), definition.Id + " policy note");
        }
    }
}
