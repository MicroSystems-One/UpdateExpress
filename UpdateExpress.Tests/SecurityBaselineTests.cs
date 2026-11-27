using System.Globalization;
using System.Text.RegularExpressions;
using UpdateExpress.Services.Security;
using Xunit;

namespace UpdateExpress.Tests;

public class SecurityBaselineTests
{
    private static readonly SecurityBaseline Baseline = SecurityBaselineV1.Create();

    private static readonly SecurityMachineInfo Client = new() { IsServerOs = false, OsBuild = 26200 };
    private static readonly SecurityMachineInfo Server = new() { IsServerOs = true, OsBuild = 20348 };

    private static SecurityCheckDefinition Definition(
        string id = "T-001",
        SecurityApplicability applicability = SecurityApplicability.ClientAndServer,
        int minimumBuild = 10240) => new()
    {
        Id = id,
        Category = SecurityCheckCategory.NetworkSecurity,
        Name = "Test",
        Description = "Test",
        RecommendedCode = SecurityStateCodes.On,
        Rationale = "Test",
        Applicability = applicability,
        MinimumBuild = minimumBuild
    };

    [Fact]
    public void The_baseline_is_version_1_of_the_standard_workstation_profile()
    {
        Assert.Equal("UpdateExpress.SecurityBaseline.v1", Baseline.Id);
        Assert.Equal("Standard Workstation", Baseline.Profile);
    }

    [Fact]
    public void It_holds_the_Phase_1_checks_and_no_others()
    {
        string[] expected =
        {
            "NET-001", "NET-002", "NET-003",
            "END-001", "END-002", "END-003", "END-004", "END-005", "END-006",
            "DEV-003", "DEV-004", "DEV-005",
            "ACC-007",
            "UPD-SEC-001", "UPD-SEC-002", "UPD-SEC-004", "UPD-SEC-005", "UPD-SEC-006", "UPD-SEC-008"
        };

        Assert.Equal(expected, Baseline.Definitions.Select(d => d.Id).ToArray());
    }

    [Fact]
    public void Every_id_is_a_stable_identifier_in_its_own_category()
    {
        var prefixes = new Dictionary<SecurityCheckCategory, string>
        {
            [SecurityCheckCategory.NetworkSecurity] = "NET-",
            [SecurityCheckCategory.EndpointProtection] = "END-",
            [SecurityCheckCategory.DeviceSecurity] = "DEV-",
            [SecurityCheckCategory.IdentityAndAccess] = "ACC-",
            [SecurityCheckCategory.UpdatesAndMaintenance] = "UPD-SEC-"
        };

        foreach (var definition in Baseline.Definitions)
        {
            Assert.Matches(new Regex(@"^[A-Z]+(-[A-Z]+)?-\d{3}$"), definition.Id);
            Assert.StartsWith(prefixes[definition.Category], definition.Id);
        }
    }

    [Fact]
    public void Every_recommendation_names_its_reason_and_a_source_that_is_Microsofts_or_none()
    {
        foreach (var definition in Baseline.Definitions)
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.Name), definition.Id);
            Assert.False(string.IsNullOrWhiteSpace(definition.Description), definition.Id);
            Assert.False(string.IsNullOrWhiteSpace(definition.Rationale), definition.Id);
            Assert.True(
                definition.ReferenceUrl.Length == 0 || definition.ReferenceUrl.StartsWith("https://learn.microsoft.com/", StringComparison.Ordinal),
                definition.Id + ": " + definition.ReferenceUrl);
        }
    }

    [Fact]
    public void Every_recommended_state_has_a_text()
    {
        foreach (var definition in Baseline.Definitions)
        {
            string? english = SecurityOverviewStrings.StateEnglish(definition.RecommendedCode);
            Assert.False(string.IsNullOrWhiteSpace(english), definition.Id);
            // A text with a placeholder needs its value, and the value must be a number here.
            if (english!.Contains("{0}"))
                Assert.True(double.TryParse(definition.RecommendedValue, NumberStyles.Float, CultureInfo.InvariantCulture, out _), definition.Id);
            Assert.NotEqual("Unknown", SecurityOverviewStrings.State(definition.RecommendedCode, definition.RecommendedValue, norwegian: false));
        }
    }

    [Fact]
    public void Phase_1_offers_no_remediation_for_any_check()
    {
        Assert.All(Baseline.Definitions, d => Assert.False(d.RemediationAvailable, d.Id));
    }

    [Fact]
    public void Secure_Boot_TPM_and_automatic_logon_are_assessment_only()
    {
        foreach (string id in new[] { "DEV-003", "DEV-004", "DEV-005", "ACC-007" })
            Assert.Equal(SecurityRemediationLevel.AssessmentOnly, Baseline.Find(id)!.RemediationLevel);
        foreach (string id in new[] { "NET-001", "NET-002", "NET-003" })
            Assert.Equal(SecurityRemediationLevel.LowRisk, Baseline.Find(id)!.RemediationLevel);
    }

    [Fact]
    public void The_age_limits_are_in_the_baseline_not_in_the_detection()
    {
        var signatures = Baseline.Find("UPD-SEC-008")!;
        var lastUpdate = Baseline.Find("UPD-SEC-001")!;

        Assert.Equal(2, signatures.Thresholds[SecurityBaselineV1.WarnAfterDays]);
        Assert.Equal(7, signatures.Thresholds[SecurityBaselineV1.CriticalAfterDays]);
        Assert.Equal("2", signatures.RecommendedValue);
        Assert.Equal(45, lastUpdate.Thresholds[SecurityBaselineV1.WarnAfterDays]);
        Assert.Equal("45", lastUpdate.RecommendedValue);
    }

    [Fact]
    public void Find_ignores_case_and_knows_no_other_ids()
    {
        Assert.Same(Baseline.Find("NET-001"), Baseline.Find("net-001"));
        Assert.Null(Baseline.Find("NET-999"));
        Assert.Null(Baseline.Find(""));
        Assert.Null(Baseline.Find(null!));
    }

    [Fact]
    public void Two_checks_cannot_share_an_id()
    {
        Assert.Throws<ArgumentException>(() =>
            new SecurityBaseline("X", "Test", new[] { Definition("T-001"), Definition("t-001") }));
    }

    [Fact]
    public void A_client_only_check_is_not_applicable_on_Windows_Server_and_is_not_a_failure()
    {
        var registered = Baseline.Find("END-001")!;

        var onServer = SecurityBaseline.NotApplicableResult(registered, Server);

        Assert.NotNull(onServer);
        Assert.Equal(SecurityAssessmentStatus.NotApplicable, onServer!.Status);
        Assert.Equal(SecurityReasonCodes.ServerOs, onServer.ReasonCode);
        Assert.Equal("END-001", onServer.Id);
        Assert.Null(SecurityBaseline.NotApplicableResult(registered, Client));
    }

    [Fact]
    public void Firewall_antivirus_updates_restart_and_lifecycle_are_assessed_on_Windows_Server()
    {
        foreach (string id in new[] { "NET-001", "NET-002", "NET-003", "END-002", "END-003", "UPD-SEC-001", "UPD-SEC-002", "UPD-SEC-004", "UPD-SEC-005", "UPD-SEC-006", "UPD-SEC-008" })
            Assert.Null(SecurityBaseline.NotApplicableResult(Baseline.Find(id)!, Server));
    }

    [Fact]
    public void A_server_only_check_is_not_applicable_on_a_client()
    {
        var definition = Definition(applicability: SecurityApplicability.ServerOnly);

        var onClient = SecurityBaseline.NotApplicableResult(definition, Client);

        Assert.Equal(SecurityAssessmentStatus.NotApplicable, onClient!.Status);
        Assert.Equal(SecurityReasonCodes.ClientOs, onClient.ReasonCode);
        Assert.Null(SecurityBaseline.NotApplicableResult(definition, Server));
    }

    [Fact]
    public void A_machine_whose_kind_or_build_was_not_read_is_assessed_not_skipped()
    {
        var unread = new SecurityMachineInfo();

        Assert.Null(SecurityBaseline.NotApplicableResult(Definition(applicability: SecurityApplicability.ClientOnly), unread));
        Assert.Null(SecurityBaseline.NotApplicableResult(Definition(applicability: SecurityApplicability.ServerOnly), unread));
        Assert.Null(SecurityBaseline.NotApplicableResult(Definition(minimumBuild: 22000), unread));
    }

    [Fact]
    public void A_Windows_older_than_the_check_supports_is_not_supported_and_is_not_a_failure()
    {
        var definition = Definition(minimumBuild: 22000);

        var onWindows10 = SecurityBaseline.NotApplicableResult(definition, new SecurityMachineInfo { IsServerOs = false, OsBuild = 19045 });

        Assert.Equal(SecurityAssessmentStatus.NotSupported, onWindows10!.Status);
        Assert.Equal(SecurityReasonCodes.OsTooOld, onWindows10.ReasonCode);
        Assert.Null(SecurityBaseline.NotApplicableResult(definition, Client));
    }
}
