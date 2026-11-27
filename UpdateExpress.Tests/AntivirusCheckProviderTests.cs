using UpdateExpress.Services.Security;
using UpdateExpress.Services.Security.Providers;
using Xunit;
using Status = UpdateExpress.Services.Security.SecurityAssessmentStatus;

namespace UpdateExpress.Tests;

public class AntivirusReaderTests
{
    /// <summary>What a Windows 11 Pro PC with Microsoft Defender Antivirus alone answered (27 Nov 2026).</summary>
    private const string DefenderAlone =
        "{\"Defender\":{\"AntivirusEnabled\":true,\"RealTimeProtectionEnabled\":true,\"ServiceEnabled\":true,\"TamperProtected\":true,\"RunningMode\":\"Normal\",\"SignatureVersion\":\"1.463.492.0\",\"SignaturesUpdated\":\"2026-11-27T04:20:29.0000000+01:00\",\"SignatureAge\":0},\"DefenderError\":null,\"Products\":[{\"Name\":\"Windows Defender\",\"State\":397568,\"Id\":\"{D68DDC3A-831F-4fae-9E44-DA132C1ACF46}\"}]}";

    private static PowerShellAnswer Answered(string output, int exitCode = 0, string error = "") => new(true, exitCode, output, error);

    [Fact]
    public void The_answer_of_a_PC_with_Defender_alone_is_read()
    {
        var reading = AntivirusReader.Parse(Answered(DefenderAlone + "\r\n"));

        Assert.True(reading.Succeeded);
        var defender = reading.Defender!;
        Assert.True(defender.AntivirusEnabled);
        Assert.True(defender.RealTimeProtectionEnabled);
        Assert.True(defender.ServiceEnabled);
        Assert.True(defender.TamperProtected);
        Assert.Equal("Normal", defender.RunningMode);
        Assert.Equal("1.463.492.0", defender.SignatureVersion);
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 4, 20, 29, TimeSpan.FromHours(1)).UtcDateTime, defender.SignaturesUpdated!.Value.ToUniversalTime());
        Assert.Equal(0, defender.SignatureAgeDays);
        var product = Assert.Single(reading.Products!);
        Assert.Equal("Windows Defender", product.Name);
        Assert.Equal(397568, product.State);
        Assert.True(product.IsDefender);
        Assert.Equal("", reading.DefenderFailure);
    }

    [Fact]
    public void Another_product_beside_a_Defender_that_did_not_answer_is_read()
    {
        var reading = AntivirusReader.Parse(Answered(
            "\uFEFF{\"Defender\":null,\"DefenderError\":\"The service has not been started.\\r\\nMore text.\",\"Products\":[" +
            "{\"Name\":\"Windows Defender\",\"State\":393472,\"Id\":\"{D68DDC3A-831F-4FAE-9E44-DA132C1ACF46}\"}," +
            "{\"Name\":\"Contoso Antivirus\",\"State\":266240,\"Id\":\"{11111111-2222-3333-4444-555555555555}\"}," +
            "{\"Name\":\"Contoso Antivirus\",\"State\":266240,\"Id\":\"{11111111-2222-3333-4444-555555555555}\"}]}"));

        Assert.True(reading.Succeeded);
        Assert.Null(reading.Defender);
        Assert.Equal("The service has not been started.", reading.DefenderFailure);
        Assert.Equal(new[] { "Windows Defender", "Contoso Antivirus" }, reading.Products!.Select(p => p.Name));
        Assert.Equal(new[] { true, false }, reading.Products!.Select(p => p.IsDefender));
    }

    [Fact]
    public void Defender_under_a_translated_name_is_known_by_its_fixed_id()
    {
        var reading = AntivirusReader.Parse(Answered(
            "{\"Defender\":null,\"Products\":{\"Name\":\"Windows-forsvar\",\"State\":397568,\"Id\":\"{d68ddc3a-831f-4fae-9e44-da132c1acf46}\"}}"));

        Assert.True(Assert.Single(reading.Products!).IsDefender);
    }

    [Theory]
    [InlineData("Bitdefender Antivirus", "{A1B2C3D4-0000-1111-2222-333344445555}", false)]
    [InlineData("Bitdefender Antivirus", "", false)]
    [InlineData("Contoso Defender Pro", "", false)]
    [InlineData("Windows Defender", "{A1B2C3D4-0000-1111-2222-333344445555}", false)]
    [InlineData("Windows Defender", "", true)]
    [InlineData("microsoft defender antivirus", "", true)]
    [InlineData("Anything", "{D68DDC3A-831F-4fae-9E44-DA132C1ACF46}", true)]
    [InlineData("Anything", "d68ddc3a-831f-4fae-9e44-da132c1acf46", true)]
    public void Defender_is_known_by_its_id_and_never_by_a_name_that_merely_contains_the_word(string name, string id, bool expected) =>
        Assert.Equal(expected, AntivirusReader.IsDefender(name, id));

    [Fact]
    public void Bitdefender_beside_a_Defender_that_stepped_back_is_another_product()
    {
        var reading = AntivirusReader.Parse(Answered(
            "{\"Defender\":{\"AntivirusEnabled\":false,\"RealTimeProtectionEnabled\":false,\"ServiceEnabled\":true,\"RunningMode\":\"Not running\"},\"DefenderError\":null,\"Products\":[" +
            "{\"Name\":\"Windows Defender\",\"State\":393472,\"Id\":\"{D68DDC3A-831F-4fae-9E44-DA132C1ACF46}\"}," +
            "{\"Name\":\"Bitdefender Antivirus\",\"State\":266240,\"Id\":\"{A1B2C3D4-0000-1111-2222-333344445555}\"}]}")) with
        {
            Policy = new DefenderPolicyReading
            {
                Antivirus = new DefenderPolicySetting { Complete = true },
                RealTimeProtection = new DefenderPolicySetting { Complete = true }
            }
        };

        Assert.Equal(new[] { true, false }, reading.Products!.Select(p => p.IsDefender));
        var protection = AntivirusCheckProvider.Judge("END-002", reading, DateTimeOffset.Now);
        Assert.Equal(Status.Unknown, protection.Status);
        Assert.True(protection.ManualReview);
        Assert.Equal("Bitdefender Antivirus", protection.DetectedValue);
        Assert.Equal(Status.Unknown, AntivirusCheckProvider.Judge("END-003", reading, DateTimeOffset.Now).Status);
        Assert.Equal(Status.NotApplicable, AntivirusCheckProvider.Judge("END-006", reading, DateTimeOffset.Now).Status);
    }

    [Fact]
    public void A_machine_without_Security_Center_still_gives_Defenders_answer()
    {
        var reading = AntivirusReader.Parse(Answered(
            "{\"Defender\":{\"AntivirusEnabled\":true,\"RealTimeProtectionEnabled\":false,\"RunningMode\":\"Normal\"},\"DefenderError\":null,\"Products\":null}"));

        Assert.True(reading.Succeeded);
        Assert.Null(reading.Products);
        Assert.False(reading.Defender!.RealTimeProtectionEnabled);
        Assert.Null(reading.Defender.ServiceEnabled);
        Assert.Null(reading.Defender.SignaturesUpdated);
        Assert.Equal("", reading.Defender.SignatureVersion);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"maybe\"")]
    [InlineData("2")]
    [InlineData("{}")]
    public void A_value_that_is_neither_on_nor_off_is_not_read_as_off(string value)
    {
        var reading = AntivirusReader.Parse(Answered(
            "{\"Defender\":{\"AntivirusEnabled\":" + value + ",\"RealTimeProtectionEnabled\":" + value + "},\"Products\":[]}"));

        Assert.True(reading.Succeeded);
        Assert.Null(reading.Defender!.AntivirusEnabled);
        Assert.Null(reading.Defender.RealTimeProtectionEnabled);
    }

    [Theory]
    [InlineData("4294967295")]
    [InlineData("-1")]
    [InlineData("\"3\"")]
    [InlineData("null")]
    public void An_age_Defender_cannot_mean_is_no_age(string age)
    {
        var reading = AntivirusReader.Parse(Answered("{\"Defender\":{\"SignatureAge\":" + age + "},\"Products\":[]}"));

        Assert.Null(reading.Defender!.SignatureAgeDays);
    }

    [Theory]
    [InlineData("{\"Defender\":null,\"DefenderError\":\"The term 'Get-MpComputerStatus' is not recognized.\",\"Products\":null}", "The term 'Get-MpComputerStatus' is not recognized.")]
    [InlineData("{}", "")]
    public void When_neither_Defender_nor_Security_Center_answered_that_is_what_the_reading_says(string output, string why)
    {
        // What a Windows Server without Defender gives. The checks decide what it means.
        var reading = AntivirusReader.Parse(Answered(output));

        Assert.True(reading.Succeeded);
        Assert.Null(reading.Defender);
        Assert.Null(reading.Products);
        Assert.Equal(why, reading.DefenderFailure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void An_answer_that_cannot_be_understood_is_a_failed_read(string output)
    {
        var reading = AntivirusReader.Parse(Answered(output));

        Assert.False(reading.Succeeded);
        Assert.NotEqual("", reading.Failure);
    }

    [Fact]
    public void A_PowerShell_that_ends_with_an_error_and_no_answer_is_a_failed_read()
    {
        var reading = AntivirusReader.Parse(Answered("", exitCode: 5, error: "Access is denied."));

        Assert.False(reading.Succeeded);
        Assert.Contains("code 5", reading.Failure);
        Assert.Contains("Access is denied.", reading.Failure);
    }

    [Fact]
    public void A_PowerShell_that_did_not_start_is_a_failed_read()
    {
        var reading = AntivirusReader.Parse(new PowerShellAnswer(false, -1, "", "Win32Exception: not found"));

        Assert.False(reading.Succeeded);
        Assert.Contains("could not be started", reading.Failure);
    }

    [Fact]
    public void The_script_only_asks()
    {
        string script = AntivirusReader.Script;

        Assert.Contains("Get-MpComputerStatus", script);
        Assert.Contains("root/SecurityCenter2", script);
        foreach (string verb in new[] { "Set-", "Add-Mp", "Remove-", "Update-Mp", "Start-", "Stop-", "Enable-", "Disable-", "Restart-", "MpCmdRun", "Invoke-" })
            Assert.DoesNotContain(verb, script, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeRegistry : IMachineRegistry
    {
        private readonly Dictionary<string, int> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _locked = new(StringComparer.OrdinalIgnoreCase);

        internal FakeRegistry Value(string key, string name, int value) { _values[key + "|" + name] = value; return this; }
        internal FakeRegistry Locked(string key) { _locked.Add(key); return this; }

        public bool KeyExists(string key) => throw new NotSupportedException();

        public int? ReadNumber(string key, string name)
        {
            if (_locked.Contains(key)) throw new System.Security.SecurityException("Requested registry access is not allowed.");
            return _values.TryGetValue(key + "|" + name, out int value) ? value : null;
        }

        public string? ReadText(string key, string name) => throw new NotSupportedException();
        public IReadOnlyList<string>? ValueNames(string key) => throw new NotSupportedException();
        public int? CountLines(string key, string name) => throw new NotSupportedException();
    }

    [Fact]
    public void Without_policy_values_nothing_is_managed()
    {
        var policy = AntivirusReader.ReadPolicy(new FakeRegistry());

        Assert.Equal(SecurityPolicyState.NotManaged, policy.Antivirus.State);
        Assert.Equal(SecurityPolicyState.NotManaged, policy.RealTimeProtection.State);
        Assert.False(policy.Antivirus.HoldsOff);
        Assert.False(policy.RealTimeProtection.HoldsOff);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public void Group_Policy_values_say_disable_so_one_means_off(int value, bool enabled)
    {
        var policy = AntivirusReader.ReadPolicy(new FakeRegistry()
            .Value(AntivirusReader.GroupPolicyKey, "DisableAntiSpyware", value)
            .Value(AntivirusReader.GroupPolicyRealTimeKey, "DisableRealtimeMonitoring", value));

        var antivirus = Assert.Single(policy.Antivirus.Values);
        var realTime = Assert.Single(policy.RealTimeProtection.Values);
        Assert.Equal(enabled, antivirus.Enabled);
        Assert.Equal(enabled, realTime.Enabled);
        Assert.Equal("Group Policy", realTime.Source);
        Assert.Equal(@"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection\DisableRealtimeMonitoring", realTime.Location);
        Assert.Equal(!enabled, policy.RealTimeProtection.HoldsOff);
        Assert.Equal(SecurityPolicyState.Managed, policy.Antivirus.State);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void MDM_values_say_allow_so_zero_means_off(int value, bool enabled)
    {
        var policy = AntivirusReader.ReadPolicy(new FakeRegistry().Value(AntivirusReader.MdmKey, "AllowRealtimeMonitoring", value));

        var realTime = Assert.Single(policy.RealTimeProtection.Values);
        Assert.Equal("MDM", realTime.Source);
        Assert.Equal(enabled, realTime.Enabled);
        Assert.Equal(!enabled, policy.RealTimeProtection.HoldsOff);
        Assert.Empty(policy.Antivirus.Values);
    }

    [Fact]
    public void An_MDM_key_that_may_not_be_read_leaves_real_time_policy_unknown_and_the_rest_known()
    {
        // What a standard user gets on Windows 11: the Policy Manager key is for administrators.
        var policy = AntivirusReader.ReadPolicy(new FakeRegistry().Locked(AntivirusReader.MdmKey));

        Assert.Equal(SecurityPolicyState.Unknown, policy.RealTimeProtection.State);
        Assert.False(policy.RealTimeProtection.HoldsOff);
        Assert.Equal(SecurityPolicyState.NotManaged, policy.Antivirus.State);
    }

    [Fact]
    public void A_value_found_in_one_place_counts_even_when_another_place_may_not_be_read()
    {
        var policy = AntivirusReader.ReadPolicy(new FakeRegistry()
            .Value(AntivirusReader.GroupPolicyRealTimeKey, "DisableRealtimeMonitoring", 1)
            .Locked(AntivirusReader.MdmKey));

        Assert.Equal(SecurityPolicyState.Managed, policy.RealTimeProtection.State);
        Assert.True(policy.RealTimeProtection.HoldsOff);
        Assert.False(policy.RealTimeProtection.Complete);
    }
}

public class AntivirusCheckProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 13, 0, 0, TimeSpan.FromHours(1));

    private static readonly AntivirusProduct DefenderProduct = new("Windows Defender", 397568, true);
    private static readonly AntivirusProduct Contoso = new("Contoso Antivirus", 266240, false);

    private static readonly DefenderPolicyReading NoPolicy = new()
    {
        Antivirus = new DefenderPolicySetting { Complete = true },
        RealTimeProtection = new DefenderPolicySetting { Complete = true }
    };

    private static DefenderStatus Defender(
        bool? enabled = true, bool? realTime = true, bool? service = true, string mode = "Normal",
        string version = "1.463.492.0", double? hoursOld = 9, int? ageDays = 0) => new()
    {
        AntivirusEnabled = enabled,
        RealTimeProtectionEnabled = realTime,
        ServiceEnabled = service,
        TamperProtected = true,
        RunningMode = mode,
        SignatureVersion = version,
        SignaturesUpdated = hoursOld is double hours ? Now.LocalDateTime.AddHours(-hours) : null,
        SignatureAgeDays = ageDays
    };

    private static DefenderStatus Off() => Defender(enabled: false, realTime: false, mode: "Not running");
    private static DefenderStatus Passive() => Defender(enabled: true, realTime: false, mode: "Passive Mode");

    /// <param name="products">Null = only Defender is registered. Use <see cref="Unreadable"/> for no Security Center.</param>
    private static AntivirusReading Reading(DefenderStatus? defender, AntivirusProduct[]? products = null, DefenderPolicyReading? policy = null) => new()
    {
        Succeeded = true,
        Defender = defender,
        DefenderFailure = defender is null ? "The service has not been started." : "",
        Products = products ?? new[] { DefenderProduct },
        Policy = policy ?? NoPolicy
    };

    private static AntivirusReading Unreadable(AntivirusReading reading) => reading with { Products = null };

    private static AntivirusReading Healthy() => Reading(Defender());
    private static AntivirusReading WithContoso(DefenderStatus? defender) => Reading(defender, new[] { DefenderProduct, Contoso });
    private static readonly AntivirusProduct[] NoProducts = Array.Empty<AntivirusProduct>();

    private static SecurityCheckResult Judge(string id, AntivirusReading reading) =>
        AntivirusCheckProvider.Judge(id, reading, Now, SecurityBaselineV1.Create().Find("UPD-SEC-008")!.Thresholds);

    private static readonly string[] AllIds = { "END-001", "END-002", "END-003", "END-004", "END-005", "END-006", "UPD-SEC-008" };

    private static DefenderPolicyReading PolicyOff(bool antivirus = false, bool realTime = false, string source = "Group Policy") => new()
    {
        Antivirus = new DefenderPolicySetting
        {
            Complete = true,
            Values = antivirus ? new[] { new DefenderPolicyValue(false, source, @"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender\DisableAntiSpyware") } : Array.Empty<DefenderPolicyValue>()
        },
        RealTimeProtection = new DefenderPolicySetting
        {
            Complete = true,
            Values = realTime ? new[] { new DefenderPolicyValue(false, source, @"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection\DisableRealtimeMonitoring") } : Array.Empty<DefenderPolicyValue>()
        }
    };

    // ---- Defender alone and healthy ----

    [Fact]
    public void A_healthy_Defender_alone_gives_every_check_a_good_answer()
    {
        var results = AllIds.ToDictionary(id => id, id => Judge(id, Healthy()));

        Assert.Equal(Status.Protected, results["END-001"].Status);
        Assert.Equal(SecurityStateCodes.Value, results["END-001"].DetectedCode);
        Assert.Equal("Windows Defender", results["END-001"].DetectedValue);

        Assert.Equal(Status.Protected, results["END-002"].Status);
        Assert.Equal(SecurityStateCodes.Active, results["END-002"].DetectedCode);
        Assert.Equal("Microsoft Defender Antivirus", results["END-002"].DetectedValue);
        Assert.Equal(SecurityPolicyState.NotManaged, results["END-002"].Policy);

        Assert.Equal(Status.Protected, results["END-003"].Status);
        Assert.Equal(SecurityStateCodes.On, results["END-003"].DetectedCode);
        Assert.Equal(SecurityPolicyState.NotManaged, results["END-003"].Policy);

        Assert.Equal(Status.Protected, results["END-006"].Status);
        Assert.Equal("Normal", results["END-006"].DetectedValue);

        Assert.Equal(Status.Protected, results["UPD-SEC-008"].Status);
        Assert.Equal(SecurityStateCodes.AgeHours, results["UPD-SEC-008"].DetectedCode);
        Assert.Equal("9", results["UPD-SEC-008"].DetectedValue);

        Assert.All(results.Values, r => Assert.False(r.ManualReview));
        Assert.All(results.Values, r => Assert.Contains("Microsoft Defender Antivirus: mode Normal, antivirus on, real-time protection on", r.Detail));
        Assert.All(results.Values, r => Assert.Contains("Windows Defender (state 0x061100)", r.Detail));
    }

    [Fact]
    public void The_definitions_version_and_time_are_reported_and_not_judged()
    {
        var version = Judge("END-004", Healthy());
        var time = Judge("END-005", Healthy());

        Assert.Equal(Status.NotApplicable, version.Status);
        Assert.Equal(SecurityReasonCodes.Informational, version.ReasonCode);
        Assert.Equal(SecurityStateCodes.Value, version.DetectedCode);
        Assert.Equal("1.463.492.0", version.DetectedValue);
        Assert.True(SecurityOverviewPresentation.IsInformation(version));

        Assert.Equal(Status.NotApplicable, time.Status);
        Assert.Equal(Now.LocalDateTime.AddHours(-9).ToString("yyyy-MM-dd HH:mm"), time.DetectedValue);
        Assert.Equal(SecurityPolicyState.NotAssessed, time.Policy);

        // Neither counts for or against the PC.
        Assert.Equal(SecuritySummaryBucket.NotApplicable, SecurityOverallStatus.BucketOf(version));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_reported_value_reads_Information_not_Not_applicable(bool norwegian)
    {
        var baseline = SecurityBaselineV1.Create();
        var row = SecurityOverviewPresentation.Row(baseline.Find("END-004")!, Judge("END-004", Healthy()), false, norwegian, t => t.ToString("s"));
        var server = SecurityOverviewPresentation.Row(
            baseline.Find("END-001")!, SecurityCheckResult.NotApplicable("END-001", SecurityReasonCodes.ServerOs), false, norwegian, t => t.ToString("s"));

        Assert.Equal(norwegian ? "Informasjon" : "Information", row.StatusText);
        Assert.Equal("1.463.492.0", row.Detected);
        Assert.Equal(norwegian ? "Rapporteres til informasjon. Ingenting vurderes." : "Reported for information. Nothing is judged.", row.Finding);
        Assert.EndsWith(norwegian ? ": Informasjon" : ": Information", row.AccessibleName);
        Assert.Equal(SecurityOverviewPresentation.Grey, SecurityOverviewPresentation.StatusColor(row.Status));
        // A check that does not apply to the machine keeps its own wording.
        Assert.Equal(norwegian ? "Ikke relevant" : "Not applicable", server.StatusText);
    }

    // ---- Real-time protection ----

    [Fact]
    public void Real_time_protection_off_in_an_active_Defender_is_not_protected()
    {
        var result = Judge("END-003", Reading(Defender(realTime: false)));

        Assert.Equal(Status.NotProtected, result.Status);
        Assert.Equal(SecurityStateCodes.Off, result.DetectedCode);
        Assert.Equal(SecurityPolicyState.NotManaged, result.Policy);
        Assert.Contains("No policy value for this was found.", result.Detail);
    }

    [Fact]
    public void Real_time_protection_off_in_an_active_Defender_is_not_excused_by_another_registered_product()
    {
        // Defender in normal mode is the product in charge; the other registration may be a leftover.
        var result = Judge("END-003", WithContoso(Defender(realTime: false)));

        Assert.Equal(Status.NotProtected, result.Status);
    }

    [Theory]
    [InlineData("Group Policy")]
    [InlineData("MDM")]
    public void Real_time_protection_held_off_by_policy_is_managed_by_policy(string source)
    {
        var result = Judge("END-003", Reading(Defender(realTime: false), policy: PolicyOff(realTime: true, source: source)));

        Assert.Equal(Status.ManagedByPolicy, result.Status);
        Assert.Equal(SecurityStateCodes.Off, result.DetectedCode);
        Assert.Equal(SecurityPolicyState.Managed, result.Policy);
        Assert.Equal(SecurityReasonCodes.DefenderOffByPolicy, result.ReasonCode);
        Assert.Contains(source + " sets this off", result.Detail);
        Assert.Equal(SecuritySummaryBucket.ManualReview, SecurityOverallStatus.BucketOf(result));
    }

    [Fact]
    public void A_policy_that_asks_for_off_does_not_turn_a_protection_that_is_on_into_a_finding()
    {
        // Tamper protection ignores the policy value; what Defender says it does is what counts.
        var result = Judge("END-003", Reading(Defender(), policy: PolicyOff(realTime: true)));

        Assert.Equal(Status.Protected, result.Status);
        Assert.Equal(SecurityPolicyState.Managed, result.Policy);
    }

    [Fact]
    public void Policy_that_could_not_be_read_is_unknown_and_never_excuses_real_time_protection_that_is_off()
    {
        var unread = new DefenderPolicyReading { Antivirus = new DefenderPolicySetting { Complete = true }, RealTimeProtection = new DefenderPolicySetting { Complete = false } };

        var on = Judge("END-003", Reading(Defender(), policy: unread));
        var off = Judge("END-003", Reading(Defender(realTime: false), policy: unread));

        Assert.Equal(Status.Protected, on.Status);
        Assert.Equal(SecurityPolicyState.Unknown, on.Policy);
        Assert.Equal(Status.NotProtected, off.Status);
        Assert.Equal(SecurityPolicyState.Unknown, off.Policy);
        Assert.Contains("Not every place a policy value can be in could be read.", off.Detail);
    }

    [Fact]
    public void Real_time_protection_Defender_gave_no_value_for_is_unknown_not_off()
    {
        var result = Judge("END-003", Reading(Defender(realTime: null)));

        Assert.Equal(Status.Unknown, result.Status);
        Assert.Equal(SecurityStateCodes.Unknown, result.DetectedCode);
        Assert.Equal(SecurityReasonCodes.DefenderValueMissing, result.ReasonCode);
    }

    [Fact]
    public void Real_time_protection_on_in_a_Defender_that_is_off_does_not_fit_and_is_unknown()
    {
        var result = Judge("END-003", Reading(Defender(enabled: false, realTime: true, mode: "Not running")));

        Assert.Equal(Status.Unknown, result.Status);
        Assert.Equal(SecurityReasonCodes.DefenderStateUnclear, result.ReasonCode);
    }

    // ---- Defender off or passive, no other product ----

    [Fact]
    public void A_Defender_that_is_off_with_no_other_product_is_not_protected()
    {
        var reading = Reading(Off());

        var protection = Judge("END-002", reading);
        var realTime = Judge("END-003", reading);
        var mode = Judge("END-006", reading);

        Assert.Equal(Status.NotProtected, protection.Status);
        Assert.Equal(SecurityStateCodes.Off, protection.DetectedCode);
        Assert.Equal(Status.NotProtected, realTime.Status);
        Assert.Equal(Status.NeedsAttention, mode.Status);
        Assert.Equal("Not running", mode.DetectedValue);
        Assert.Equal(SecurityReasonCodes.DefenderOffNoProduct, mode.ReasonCode);
    }

    [Fact]
    public void A_stopped_Defender_service_counts_as_off()
    {
        var result = Judge("END-002", Reading(Defender(service: false)));

        Assert.Equal(Status.NotProtected, result.Status);
    }

    [Fact]
    public void A_Defender_held_off_by_policy_is_managed_by_policy()
    {
        var reading = Reading(Off(), policy: PolicyOff(antivirus: true));

        var protection = Judge("END-002", reading);
        var mode = Judge("END-006", reading);

        Assert.Equal(Status.ManagedByPolicy, protection.Status);
        Assert.Equal(SecurityReasonCodes.DefenderOffByPolicy, protection.ReasonCode);
        Assert.Equal(SecurityPolicyState.Managed, protection.Policy);
        Assert.Equal(Status.ManagedByPolicy, mode.Status);
        Assert.Contains(@"DisableAntiSpyware", protection.Detail);
    }

    [Fact]
    public void A_Defender_that_is_off_where_Security_Center_cannot_be_read_is_for_a_person_to_judge()
    {
        // Windows Server: another product may protect, and Windows has no list to show it.
        var reading = Unreadable(Reading(Off()));

        foreach (string id in new[] { "END-002", "END-003", "END-006" })
        {
            var result = Judge(id, reading);
            Assert.Equal(Status.Unknown, result.Status);
            Assert.True(result.ManualReview, id);
            Assert.Equal(SecurityReasonCodes.AntivirusProductsUnreadable, result.ReasonCode);
        }
        Assert.Contains("Windows Security Center could not be read.", Judge("END-002", reading).Detail);
    }

    [Fact]
    public void A_passive_Defender_with_no_other_product_needs_attention()
    {
        var reading = Reading(Passive());

        var protection = Judge("END-002", reading);
        var mode = Judge("END-006", reading);

        Assert.Equal(Status.NeedsAttention, protection.Status);
        Assert.Equal("Passive Mode", protection.DetectedValue);
        Assert.Equal(SecurityReasonCodes.DefenderPassiveNoProduct, protection.ReasonCode);
        Assert.Equal(Status.NeedsAttention, mode.Status);
        Assert.Equal(Status.NotProtected, Judge("END-003", reading).Status);
    }

    // ---- Another antivirus product ----

    [Theory]
    [InlineData("Passive Mode")]
    [InlineData("SxS Passive Mode")]
    [InlineData("EDR Block Mode")]
    public void A_passive_Defender_beside_another_product_is_not_a_failure(string mode)
    {
        var reading = WithContoso(Defender(realTime: false, mode: mode));

        var protection = Judge("END-002", reading);
        var defenderMode = Judge("END-006", reading);

        Assert.Equal(Status.Unknown, protection.Status);
        Assert.True(protection.ManualReview);
        Assert.Equal(SecurityReasonCodes.AntivirusOtherProduct, protection.ReasonCode);
        Assert.Equal("Contoso Antivirus", protection.DetectedValue);
        Assert.Equal(SecuritySummaryBucket.ManualReview, SecurityOverallStatus.BucketOf(protection));

        Assert.Equal(Status.Protected, defenderMode.Status);
        Assert.Equal(mode, defenderMode.DetectedValue);

        AssertNothingReadsAsAFailure(reading);
    }

    [Fact]
    public void A_Defender_that_switched_off_for_another_product_is_not_a_failure()
    {
        var reading = WithContoso(Off());

        var mode = Judge("END-006", reading);

        Assert.Equal(Status.NotApplicable, mode.Status);
        Assert.Equal(SecurityReasonCodes.DefenderOffOtherProduct, mode.ReasonCode);
        Assert.Equal("Not running", mode.DetectedValue);
        Assert.False(SecurityOverviewPresentation.IsInformation(mode));
        Assert.True(Judge("END-002", reading).ManualReview);
        AssertNothingReadsAsAFailure(reading);
    }

    [Fact]
    public void A_Defender_that_does_not_answer_beside_another_product_is_not_a_failure_and_not_a_failed_check()
    {
        var reading = WithContoso(null);

        var mode = Judge("END-006", reading);

        Assert.Equal(Status.Unknown, mode.Status);
        Assert.Equal(SecurityReasonCodes.DefenderSilentOtherProduct, mode.ReasonCode);
        Assert.Contains("did not answer: The service has not been started.", mode.Detail);
        AssertNothingReadsAsAFailure(reading);
        Assert.All(AllIds, id => Assert.NotEqual(Status.Error, Judge(id, reading).Status));
    }

    [Fact]
    public void Another_product_alone_in_the_list_is_registered_and_for_a_person_to_judge()
    {
        var reading = Reading(null, new[] { Contoso });

        Assert.Equal(Status.Protected, Judge("END-001", reading).Status);
        Assert.Equal("Contoso Antivirus", Judge("END-001", reading).DetectedValue);
        Assert.True(Judge("END-002", reading).ManualReview);
    }

    /// <summary>
    /// With another product in charge, registration is all Windows knows: nothing is a pass
    /// on that product's behalf, and nothing is a failure because Defender stepped back.
    /// </summary>
    private static void AssertNothingReadsAsAFailure(AntivirusReading reading)
    {
        foreach (string id in AllIds)
        {
            var result = Judge(id, reading);
            Assert.NotEqual(Status.NotProtected, result.Status);
            Assert.NotEqual(Status.NeedsAttention, result.Status);
            Assert.Contains("Contoso Antivirus", result.Detail);
        }
        foreach (string id in new[] { "END-003", "END-004", "END-005", "UPD-SEC-008" })
        {
            var result = Judge(id, reading);
            Assert.Equal(Status.Unknown, result.Status);
            Assert.Equal(SecurityReasonCodes.AntivirusOtherProductValue, result.ReasonCode);
            Assert.Equal(SecurityStateCodes.Unknown, result.DetectedCode);
            Assert.False(result.ManualReview);
        }
        Assert.Equal(Status.Protected, Judge("END-001", reading).Status);
        Assert.Equal("Windows Defender, Contoso Antivirus", Judge("END-001", reading).DetectedValue);
        Assert.NotEqual(Status.Protected, Judge("END-002", reading).Status);
    }

    [Fact]
    public void An_active_Defender_beside_another_registered_product_is_judged_as_Defender()
    {
        var reading = WithContoso(Defender());

        Assert.Equal(Status.Protected, Judge("END-002", reading).Status);
        Assert.Equal(Status.Protected, Judge("END-003", reading).Status);
        Assert.Equal(Status.Protected, Judge("UPD-SEC-008", reading).Status);
        Assert.Equal(Status.NotApplicable, Judge("END-004", reading).Status);
    }

    // ---- Registered products ----

    [Fact]
    public void No_registered_product_needs_attention_unless_Defender_says_it_protects()
    {
        var none = Judge("END-001", Reading(Off(), NoProducts));
        var doubtful = Judge("END-001", Reading(Defender(), NoProducts));

        Assert.Equal(Status.NeedsAttention, none.Status);
        Assert.Equal(SecurityStateCodes.NoneRegistered, none.DetectedCode);
        Assert.Equal(SecurityReasonCodes.AntivirusNoneRegistered, none.ReasonCode);
        Assert.Contains("Windows Security Center lists no antivirus product.", none.Detail);
        Assert.Equal(Status.Unknown, doubtful.Status);
    }

    [Fact]
    public void Products_that_could_not_be_read_are_unknown_and_Defender_is_still_judged()
    {
        var reading = Unreadable(Healthy());

        var products = Judge("END-001", reading);

        Assert.Equal(Status.Unknown, products.Status);
        Assert.Equal(SecurityReasonCodes.AntivirusProductsUnreadable, products.ReasonCode);
        Assert.Equal(Status.Protected, Judge("END-002", reading).Status);
        Assert.Equal(Status.Protected, Judge("END-003", reading).Status);
        Assert.Equal(Status.Protected, Judge("UPD-SEC-008", reading).Status);
    }

    // ---- Defender did not answer, values missing ----

    [Fact]
    public void A_Defender_that_does_not_answer_and_no_other_product_is_a_check_that_could_not_run()
    {
        var reading = Reading(null);

        foreach (string id in new[] { "END-002", "END-003", "END-004", "END-005", "END-006", "UPD-SEC-008" })
        {
            var result = Judge(id, reading);
            Assert.Equal(Status.Error, result.Status);
            Assert.Equal(SecurityReasonCodes.DefenderReadFailed, result.ReasonCode);
            Assert.Equal(SecurityStateCodes.Unknown, result.DetectedCode);
        }
        Assert.Equal(Status.Protected, Judge("END-001", reading).Status);
    }

    [Fact]
    public void On_a_client_where_nothing_answered_the_checks_could_not_run()
    {
        var reading = Unreadable(Reading(null));

        Assert.Equal(Status.Unknown, Judge("END-001", reading).Status);
        Assert.Equal(SecurityReasonCodes.AntivirusProductsUnreadable, Judge("END-001", reading).ReasonCode);
        foreach (string id in AllIds.Where(id => id != "END-001"))
        {
            var result = Judge(id, reading);
            Assert.Equal(Status.Error, result.Status);
            Assert.Contains("did not answer", result.Detail);
            Assert.Contains("Windows Security Center could not be read.", result.Detail);
        }
    }

    [Fact]
    public void On_a_server_a_silent_Defender_is_unknown_not_a_failed_check_and_not_a_failure()
    {
        // Defender removed, or replaced by a product Windows Server has no list to show.
        var reading = Unreadable(Reading(null));

        foreach (string id in AllIds.Where(id => id != "END-001"))
        {
            var result = AntivirusCheckProvider.Judge(id, reading, Now, server: true);
            Assert.Equal(Status.Unknown, result.Status);
            Assert.Equal(SecurityReasonCodes.DefenderSilentOnServer, result.ReasonCode);
            Assert.Equal(id == "END-002", result.ManualReview);
        }
    }

    [Fact]
    public void On_a_server_a_Defender_that_answers_is_judged_like_anywhere_else()
    {
        var off = Unreadable(Reading(Off()));

        Assert.Equal(Status.Protected, AntivirusCheckProvider.Judge("END-003", Unreadable(Healthy()), Now, server: true).Status);
        Assert.Equal(Status.Unknown, AntivirusCheckProvider.Judge("END-002", off, Now, server: true).Status);
        Assert.Equal(SecurityReasonCodes.AntivirusProductsUnreadable, AntivirusCheckProvider.Judge("END-002", off, Now, server: true).ReasonCode);
    }

    [Fact]
    public void A_Defender_that_does_not_say_whether_it_is_on_is_unknown()
    {
        var reading = Reading(Defender(enabled: null, mode: ""));

        Assert.Equal(DefenderRole.Unclear, AntivirusCheckProvider.RoleOf(reading.Defender));
        Assert.Equal(Status.Unknown, Judge("END-002", reading).Status);
        Assert.Equal(SecurityReasonCodes.DefenderValueMissing, Judge("END-002", reading).ReasonCode);
        Assert.Equal(Status.Unknown, Judge("END-006", reading).Status);
    }

    [Fact]
    public void Missing_definitions_values_are_unknown()
    {
        var reading = Reading(Defender(version: "", hoursOld: null, ageDays: null));

        foreach (string id in new[] { "END-004", "END-005", "UPD-SEC-008" })
        {
            var result = Judge(id, reading);
            Assert.Equal(Status.Unknown, result.Status);
            Assert.Equal(SecurityReasonCodes.DefenderValueMissing, result.ReasonCode);
        }
    }

    [Fact]
    public void A_failed_read_is_a_check_that_could_not_run_for_every_check()
    {
        foreach (string id in AllIds)
        {
            var result = Judge(id, AntivirusReading.Failed("PowerShell could not be started."));
            Assert.Equal(Status.Error, result.Status);
            Assert.Equal(SecurityReasonCodes.AntivirusReadFailed, result.ReasonCode);
            Assert.Equal("PowerShell could not be started.", result.Detail);
            Assert.Equal(SecurityStateCodes.Unknown, result.DetectedCode);
        }
    }

    // ---- Age of the definitions ----

    [Theory]
    [InlineData(0, Status.Protected, SecurityStateCodes.AgeHours, "0")]
    [InlineData(47.9, Status.Protected, SecurityStateCodes.AgeHours, "47")]
    [InlineData(48, Status.NeedsAttention, SecurityStateCodes.AgeDays, "2")]
    [InlineData(167.9, Status.NeedsAttention, SecurityStateCodes.AgeDays, "6")]
    [InlineData(168, Status.NotProtected, SecurityStateCodes.AgeDays, "7")]
    [InlineData(2400, Status.NotProtected, SecurityStateCodes.AgeDays, "100")]
    public void The_age_of_the_definitions_is_judged_at_the_same_limits_as_the_header(double hoursOld, Status expected, string code, string value)
    {
        var result = Judge("UPD-SEC-008", Reading(Defender(hoursOld: hoursOld)));

        Assert.Equal(expected, result.Status);
        Assert.Equal(code, result.DetectedCode);
        Assert.Equal(value, result.DetectedValue);
    }

    [Theory]
    [InlineData(1, Status.Protected)]
    [InlineData(2, Status.NeedsAttention)]
    [InlineData(7, Status.NotProtected)]
    public void Without_a_time_the_age_in_whole_days_is_used(int days, Status expected)
    {
        var result = Judge("UPD-SEC-008", Reading(Defender(hoursOld: null, ageDays: days)));

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public void Definitions_from_a_little_ahead_of_the_clock_are_fresh_and_from_far_ahead_are_unknown()
    {
        var little = Judge("UPD-SEC-008", Reading(Defender(hoursOld: -0.1)));
        var far = Judge("UPD-SEC-008", Reading(Defender(hoursOld: -30)));

        Assert.Equal(Status.Protected, little.Status);
        Assert.Equal("0", little.DetectedValue);
        Assert.Equal(Status.Unknown, far.Status);
        Assert.Equal(SecurityReasonCodes.DefenderStateUnclear, far.ReasonCode);
    }

    [Fact]
    public void The_age_limits_come_from_the_baseline()
    {
        var limits = new Dictionary<string, double> { [SecurityBaselineV1.WarnAfterDays] = 1, [SecurityBaselineV1.CriticalAfterDays] = 3 };

        Assert.Equal(Status.NeedsAttention, AntivirusCheckProvider.Judge("UPD-SEC-008", Reading(Defender(hoursOld: 30)), Now, limits).Status);
        Assert.Equal(Status.NotProtected, AntivirusCheckProvider.Judge("UPD-SEC-008", Reading(Defender(hoursOld: 80)), Now, limits).Status);
        // Without limits the header's own are used.
        Assert.Equal(Status.Protected, AntivirusCheckProvider.Judge("UPD-SEC-008", Reading(Defender(hoursOld: 30)), Now).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_age_and_the_findings_read_in_both_languages(bool norwegian)
    {
        var baseline = SecurityBaselineV1.Create();
        SecurityCheckRowText Row(string id, AntivirusReading reading) =>
            SecurityOverviewPresentation.Row(baseline.Find(id)!, Judge(id, reading), false, norwegian, t => t.ToString("s"));

        Assert.Equal(norwegian ? "Oppdatert for 9 t siden" : "Updated 9 h ago", Row("UPD-SEC-008", Healthy()).Detected);
        Assert.Equal(norwegian ? "Oppdatert for 3 dager siden" : "Updated 3 days ago", Row("UPD-SEC-008", Reading(Defender(hoursOld: 80))).Detected);
        Assert.Equal(norwegian ? "Innen de siste 2 dagene" : "Within the last 2 days", Row("UPD-SEC-008", Healthy()).Recommended);
        Assert.Equal(norwegian ? "Aktiv: Microsoft Defender Antivirus" : "Active: Microsoft Defender Antivirus", Row("END-002", Healthy()).Detected);
        Assert.Equal(norwegian ? "Ingen registrert" : "None registered", Row("END-001", Reading(Off(), NoProducts)).Detected);

        var other = Row("END-002", WithContoso(Off()));
        Assert.Equal(norwegian ? "Ukjent" : "Unknown", other.StatusText);
        Assert.Equal("Contoso Antivirus", other.Detected);
        Assert.StartsWith(norwegian ? "Et annet antivirusprodukt er registrert i Windows." : "Another antivirus product is registered with Windows.", other.Finding);
        Assert.EndsWith(norwegian ? "Dette funnet må vurderes av en person." : "This finding needs a person to judge it.", other.Finding);
    }

    // ---- Through the engine ----

    private static Task<SecurityAssessmentSnapshot> Assess(AntivirusCheckProvider provider, bool? server = false, CancellationToken cancel = default) =>
        SecurityAssessmentEngine.RunAsync(
            SecurityBaselineV1.Create(),
            new ISecurityCheckProvider[] { provider },
            new SecurityMachineInfo { IsServerOs = server, OsBuild = 26200 },
            new SecurityAssessmentOptions { Clock = () => Now },
            cancellationToken: cancel);

    [Fact]
    public async Task One_read_answers_the_seven_checks()
    {
        int reads = 0;
        var provider = new AntivirusCheckProvider(_ =>
        {
            Interlocked.Increment(ref reads);
            return Task.FromResult(Reading(Defender(hoursOld: 1)));
        });

        var snapshot = await Assess(provider);

        Assert.Equal(1, reads);
        Assert.True(snapshot.Completed);
        Assert.Equal(AllIds.OrderBy(id => id), snapshot.Results.Select(r => r.Id).OrderBy(id => id));
        Assert.Equal(5, snapshot.Results.Count(r => r.Status == Status.Protected));
        Assert.Equal(2, snapshot.Results.Count(r => r.Status == Status.NotApplicable));
        Assert.Equal(SecurityOverallLevel.Green, SecurityOverallStatus.Evaluate(snapshot, Now.AddMinutes(1)).Level);
    }

    [Fact]
    public async Task On_Windows_Server_the_product_list_is_not_applicable_and_Defender_is_still_judged()
    {
        var provider = new AntivirusCheckProvider(_ => Task.FromResult(Unreadable(Reading(Defender(hoursOld: 1)))));

        var snapshot = await Assess(provider, server: true);

        var products = snapshot.Results.Single(r => r.Id == "END-001");
        Assert.Equal(Status.NotApplicable, products.Status);
        Assert.Equal(SecurityReasonCodes.ServerOs, products.ReasonCode);
        Assert.Equal(Status.Protected, snapshot.Results.Single(r => r.Id == "END-002").Status);
        Assert.Equal(Status.Protected, snapshot.Results.Single(r => r.Id == "END-003").Status);
        Assert.Equal(7, snapshot.Results.Count);
    }

    [Fact]
    public async Task A_server_without_Defender_gets_no_failed_check_and_one_finding_for_a_person()
    {
        var provider = new AntivirusCheckProvider(_ => Task.FromResult(Unreadable(Reading(null))));

        var snapshot = await Assess(provider, server: true);
        var overall = SecurityOverallStatus.Evaluate(snapshot, Now.AddMinutes(1));

        Assert.DoesNotContain(snapshot.Results, r => r.Status is Status.Error or Status.NotProtected or Status.NeedsAttention);
        Assert.Equal(1, overall.Summary.ManualReview);
        Assert.NotEqual(SecurityOverallLevel.Red, overall.Level);
        Assert.NotEqual(SecurityOverallLevel.Green, overall.Level);
    }

    [Fact]
    public async Task Another_product_in_charge_does_not_turn_the_overall_status_red()
    {
        var provider = new AntivirusCheckProvider(_ => Task.FromResult(WithContoso(Off())));

        var snapshot = await Assess(provider);
        var overall = SecurityOverallStatus.Evaluate(snapshot, Now.AddMinutes(1));

        Assert.NotEqual(SecurityOverallLevel.Red, overall.Level);
        Assert.Equal(0, overall.Summary.NeedsAttention);
        Assert.Equal(1, overall.Summary.ManualReview);
    }

    [Fact]
    public async Task A_read_that_throws_is_a_failed_check_not_a_protection_that_is_off()
    {
        var provider = new AntivirusCheckProvider(_ => throw new InvalidOperationException("no CIM"));

        var snapshot = await Assess(provider);

        Assert.Equal(7, snapshot.Results.Count);
        Assert.All(snapshot.Results, r =>
        {
            Assert.Equal(Status.Error, r.Status);
            Assert.Equal(SecurityStateCodes.Unknown, r.DetectedCode);
        });
    }

    [Fact]
    public async Task A_cancelled_read_leaves_the_checks_unknown()
    {
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new AntivirusCheckProvider(async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Healthy();
        });

        var assessment = Assess(provider, cancel: cancel.Token);
        await started.Task;
        cancel.Cancel();
        var snapshot = await assessment;

        Assert.False(snapshot.Completed);
        Assert.All(snapshot.Results, r =>
        {
            Assert.Equal(Status.Unknown, r.Status);
            Assert.Equal(SecurityReasonCodes.Cancelled, r.ReasonCode);
        });
    }

    [Fact]
    public void The_provider_is_registered_and_answers_the_antivirus_checks()
    {
        var provider = Assert.Single(SecurityCheckProviders.Create().OfType<AntivirusCheckProvider>());

        Assert.Equal(AllIds, provider.CheckIds);
        Assert.True(provider.Timeout <= SecurityAssessmentEngine.MaxProviderTimeout);
        Assert.All(provider.CheckIds, id => Assert.NotNull(SecurityBaselineV1.Create().Find(id)));
    }

    [Fact]
    public void No_check_is_claimed_by_two_registered_providers()
    {
        var ids = SecurityCheckProviders.Create().SelectMany(p => p.CheckIds).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
