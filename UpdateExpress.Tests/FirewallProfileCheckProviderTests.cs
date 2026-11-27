using System.Diagnostics;
using UpdateExpress.Services.Security;
using UpdateExpress.Services.Security.Providers;
using Xunit;
using Status = UpdateExpress.Services.Security.SecurityAssessmentStatus;

namespace UpdateExpress.Tests;

public class FirewallReaderTests
{
    /// <summary>What a Windows 11 Pro PC without firewall policy answered (27 Nov 2026).</summary>
    private const string AllOn =
        "{\"Profiles\":[{\"Name\":\"Domain\",\"Enabled\":\"True\"},{\"Name\":\"Private\",\"Enabled\":\"True\"},{\"Name\":\"Public\",\"Enabled\":\"True\"}],\"Current\":\"Public\",\"Products\":[]}";

    private static PowerShellAnswer Answered(string output, int exitCode = 0, string error = "") => new(true, exitCode, output, error);

    [Fact]
    public void The_answer_of_a_PC_with_every_profile_on_is_read()
    {
        var reading = FirewallReader.Parse(Answered(AllOn + "\r\n"));

        Assert.True(reading.Succeeded);
        Assert.Equal(3, reading.Effective.Count);
        Assert.True(reading.Effective["Domain"]);
        Assert.True(reading.Effective["Private"]);
        Assert.True(reading.Effective["Public"]);
        Assert.Equal(new[] { "Public" }, reading.InUse);
        Assert.NotNull(reading.OtherProducts);
        Assert.Empty(reading.OtherProducts!);
    }

    [Fact]
    public void A_profile_that_is_off_and_two_profiles_in_use_are_read()
    {
        var reading = FirewallReader.Parse(Answered(
            "\uFEFF{\"Profiles\":[{\"Name\":\"Domain\",\"Enabled\":\"True\"},{\"Name\":\"Private\",\"Enabled\":\"False\"},{\"Name\":\"Public\",\"Enabled\":\"True\"}],\"Current\":\"Domain, Private\",\"Products\":[\"Bitdefender Firewall\",\"Bitdefender Firewall\"]}"));

        Assert.True(reading.Succeeded);
        Assert.False(reading.Effective["Private"]);
        Assert.Contains("Domain", reading.InUse!);
        Assert.Contains("private", reading.InUse!);
        Assert.DoesNotContain("Public", reading.InUse!);
        Assert.Equal(new[] { "Bitdefender Firewall" }, reading.OtherProducts);
    }

    [Theory]
    [InlineData("\"NotConfigured\"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    [InlineData("2")]
    [InlineData("{}")]
    public void A_state_that_is_neither_on_nor_off_is_not_read_as_off(string enabled)
    {
        var reading = FirewallReader.Parse(Answered(
            "{\"Profiles\":[{\"Name\":\"Domain\",\"Enabled\":" + enabled + "}],\"Current\":null,\"Products\":null}"));

        Assert.True(reading.Succeeded);
        Assert.Null(reading.Effective["Domain"]);
        Assert.Null(reading.InUse);
        Assert.Null(reading.OtherProducts);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("\"true\"", true)]
    public void A_state_given_as_a_flag_or_number_is_read(string enabled, bool expected)
    {
        // One profile comes back as an object, not as a list of one.
        var reading = FirewallReader.Parse(Answered("{\"Profiles\":{\"Name\":\"Public\",\"Enabled\":" + enabled + "}}"));

        Assert.True(reading.Succeeded);
        Assert.Equal(expected, reading.Effective["public"]);
    }

    [Fact]
    public void An_error_from_Windows_is_a_failed_read_with_its_first_line()
    {
        var reading = FirewallReader.Parse(Answered("{\"Error\":\"The service has not been started.\\r\\nMore text.\"}", exitCode: 1));

        Assert.False(reading.Succeeded);
        Assert.Equal("The service has not been started.", reading.Failure);
        Assert.Empty(reading.Effective);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"Profiles\":[]}")]
    [InlineData("{\"Profiles\":[{\"Enabled\":\"True\"}]}")]
    [InlineData("{\"Current\":\"Public\"}")]
    public void An_answer_that_names_no_profile_is_a_failed_read(string output)
    {
        var reading = FirewallReader.Parse(Answered(output));

        Assert.False(reading.Succeeded);
        Assert.NotEqual("", reading.Failure);
    }

    [Fact]
    public void A_PowerShell_that_ends_with_an_error_code_is_a_failed_read_even_with_profiles_in_its_output()
    {
        var reading = FirewallReader.Parse(Answered(AllOn, exitCode: 5, error: "Access is denied."));

        Assert.False(reading.Succeeded);
        Assert.Contains("code 5", reading.Failure);
        Assert.Contains("Access is denied.", reading.Failure);
    }

    [Fact]
    public void A_PowerShell_that_did_not_start_is_a_failed_read()
    {
        var reading = FirewallReader.Parse(new PowerShellAnswer(false, -1, "", "Win32Exception: not found"));

        Assert.False(reading.Succeeded);
        Assert.Contains("could not be started", reading.Failure);
    }

    [Fact]
    public void The_script_only_asks()
    {
        string script = FirewallReader.Script;

        Assert.Contains("Get-NetFirewallProfile -PolicyStore ActiveStore", script);
        foreach (string verb in new[] { "Set-", "New-Net", "Remove-", "Enable-", "Disable-", "netsh", "Restart-", "Stop-", "Start-" })
            Assert.DoesNotContain(verb, script, StringComparison.OrdinalIgnoreCase);
    }

    private static Func<string, string, int?> Registry(params (string Key, int Value)[] values) => (key, name) =>
    {
        Assert.Equal("EnableFirewall", name);
        foreach (var (k, v) in values)
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return v;
        return null;
    };

    [Fact]
    public void Without_policy_values_no_profile_is_managed()
    {
        var policy = FirewallReader.ReadPolicy(Registry());

        Assert.True(policy.Readable);
        Assert.Empty(policy.Values);
        Assert.Empty(policy.Unclear);
    }

    [Fact]
    public void Group_Policy_values_are_found_per_profile()
    {
        var policy = FirewallReader.ReadPolicy(Registry(
            (FirewallReader.GroupPolicyKey + @"\DomainProfile", 1),
            (FirewallReader.GroupPolicyKey + @"\PublicProfile", 0)));

        Assert.True(policy.Values["Domain"].Enabled);
        Assert.Equal("Group Policy", policy.Values["Domain"].Source);
        Assert.Equal(@"HKLM\SOFTWARE\Policies\Microsoft\WindowsFirewall\DomainProfile", policy.Values["Domain"].Location);
        Assert.False(policy.Values["Public"].Enabled);
        Assert.False(policy.Values.ContainsKey("Private"));
    }

    [Fact]
    public void An_MDM_value_is_found_and_Group_Policy_is_named_first_when_both_exist()
    {
        var policy = FirewallReader.ReadPolicy(Registry(
            (FirewallReader.MdmKey + @"\PrivateProfile", 0),
            (FirewallReader.MdmKey + @"\DomainProfile", 0),
            (FirewallReader.GroupPolicyKey + @"\DomainProfile", 1)));

        Assert.Equal("MDM", policy.Values["Private"].Source);
        Assert.False(policy.Values["Private"].Enabled);
        Assert.Equal("Group Policy", policy.Values["Domain"].Source);
        Assert.True(policy.Values["Domain"].Enabled);
    }

    [Fact]
    public void The_older_standard_profile_value_counts_for_private_and_leaves_public_open()
    {
        var policy = FirewallReader.ReadPolicy(Registry((FirewallReader.GroupPolicyKey + @"\StandardProfile", 0)));

        Assert.False(policy.Values["Private"].Enabled);
        Assert.False(policy.Values.ContainsKey("Public"));
        Assert.Contains("Public", policy.Unclear);
        Assert.DoesNotContain("Domain", policy.Unclear);
    }

    [Fact]
    public void A_value_of_its_own_for_public_settles_what_the_standard_profile_left_open()
    {
        var policy = FirewallReader.ReadPolicy(Registry(
            (FirewallReader.GroupPolicyKey + @"\StandardProfile", 0),
            (FirewallReader.GroupPolicyKey + @"\PrivateProfile", 1),
            (FirewallReader.GroupPolicyKey + @"\PublicProfile", 1)));

        Assert.True(policy.Values["Private"].Enabled);
        Assert.True(policy.Values["Public"].Enabled);
        Assert.Empty(policy.Unclear);
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(System.Security.SecurityException))]
    [InlineData(typeof(System.IO.IOException))]
    public void Policy_keys_that_may_not_be_read_leave_policy_unknown(Type failure)
    {
        var policy = FirewallReader.ReadPolicy((_, _) => throw (Exception)Activator.CreateInstance(failure)!);

        Assert.False(policy.Readable);
        Assert.Empty(policy.Values);
    }
}

public class FirewallProfileCheckProviderTests
{
    private static FirewallReading Reading(
        bool? domain = true, bool? @private = true, bool? @public = true,
        string[]? inUse = null, string[]? products = null, FirewallPolicyReading? policy = null) => new()
    {
        Succeeded = true,
        Effective = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Domain"] = domain, ["Private"] = @private, ["Public"] = @public
        },
        InUse = inUse ?? new[] { "Public" },
        OtherProducts = products ?? Array.Empty<string>(),
        Policy = policy ?? new FirewallPolicyReading { Readable = true }
    };

    private static FirewallPolicyReading Policy(params (string Profile, bool Enabled)[] values) => new()
    {
        Readable = true,
        Values = values.ToDictionary(
            v => v.Profile,
            v => new FirewallPolicyValue(v.Enabled, "Group Policy", @"HKLM\SOFTWARE\Policies\Microsoft\WindowsFirewall\" + v.Profile + "Profile"),
            StringComparer.OrdinalIgnoreCase)
    };

    private static SecurityCheckResult Public(FirewallReading reading) =>
        FirewallProfileCheckProvider.Judge("NET-003", "Public", reading);

    [Fact]
    public void A_profile_that_is_on_is_protected()
    {
        var result = Public(Reading());

        Assert.Equal(Status.Protected, result.Status);
        Assert.Equal(SecurityStateCodes.On, result.DetectedCode);
        Assert.Equal(SecurityPolicyState.NotManaged, result.Policy);
        Assert.Equal("", result.ReasonCode);
        Assert.False(result.ManualReview);
        Assert.Contains("A network uses this profile now.", result.Detail);
        Assert.Contains("No policy value for this profile was found.", result.Detail);
    }

    [Fact]
    public void A_profile_that_is_off_is_not_protected()
    {
        var result = Public(Reading(@public: false, inUse: new[] { "Private" }));

        Assert.Equal(Status.NotProtected, result.Status);
        Assert.Equal(SecurityStateCodes.Off, result.DetectedCode);
        Assert.Equal(SecurityPolicyState.NotManaged, result.Policy);
        Assert.Contains("No network uses this profile now.", result.Detail);
    }

    [Fact]
    public void A_profile_that_policy_holds_on_is_protected_and_shown_as_managed()
    {
        var result = Public(Reading(policy: Policy(("Public", true))));

        Assert.Equal(Status.Protected, result.Status);
        Assert.Equal(SecurityPolicyState.Managed, result.Policy);
        Assert.Contains("Group Policy sets this profile on", result.Detail);
    }

    [Fact]
    public void A_profile_that_policy_holds_off_is_managed_by_policy_and_still_shown_as_off()
    {
        var result = Public(Reading(@public: false, policy: Policy(("Public", false))));

        Assert.Equal(Status.ManagedByPolicy, result.Status);
        Assert.Equal(SecurityStateCodes.Off, result.DetectedCode);
        Assert.Equal(SecurityPolicyState.Managed, result.Policy);
        Assert.Equal(SecurityReasonCodes.FirewallOffByPolicy, result.ReasonCode);
        Assert.Contains(@"HKLM\SOFTWARE\Policies\Microsoft\WindowsFirewall\PublicProfile", result.Detail);
    }

    [Fact]
    public void A_profile_that_is_off_although_policy_says_on_is_not_protected()
    {
        var result = Public(Reading(@public: false, policy: Policy(("Public", true))));

        Assert.Equal(Status.NotProtected, result.Status);
        Assert.Equal(SecurityPolicyState.Managed, result.Policy);
    }

    [Fact]
    public void A_policy_for_another_profile_does_not_excuse_this_one()
    {
        var result = Public(Reading(@public: false, policy: Policy(("Domain", false))));

        Assert.Equal(Status.NotProtected, result.Status);
        Assert.Equal(SecurityPolicyState.NotManaged, result.Policy);
    }

    [Fact]
    public void Off_with_another_firewall_product_registered_is_for_a_person_to_judge()
    {
        var result = Public(Reading(@public: false, products: new[] { "Contoso Firewall" }));

        Assert.Equal(Status.Unknown, result.Status);
        Assert.True(result.ManualReview);
        Assert.Equal(SecurityStateCodes.Off, result.DetectedCode);
        Assert.Equal(SecurityReasonCodes.FirewallOffOtherProduct, result.ReasonCode);
        Assert.Contains("Contoso Firewall", result.Detail);
        Assert.Equal(SecuritySummaryBucket.ManualReview, SecurityOverallStatus.BucketOf(result));
    }

    [Fact]
    public void On_with_another_firewall_product_registered_is_simply_protected()
    {
        var result = Public(Reading(products: new[] { "Contoso Firewall" }));

        Assert.Equal(Status.Protected, result.Status);
        Assert.False(result.ManualReview);
    }

    [Fact]
    public void A_profile_Windows_gave_no_state_for_is_unknown_not_off()
    {
        var result = Public(Reading(@public: null));

        Assert.Equal(Status.Unknown, result.Status);
        Assert.Equal(SecurityStateCodes.Unknown, result.DetectedCode);
        Assert.Equal(SecurityReasonCodes.FirewallProfileNotReported, result.ReasonCode);
        Assert.False(result.ManualReview);
    }

    [Fact]
    public void A_profile_missing_from_the_answer_is_unknown_not_off()
    {
        var reading = Reading() with
        {
            Effective = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase) { ["Domain"] = true }
        };

        Assert.Equal(Status.Unknown, Public(reading).Status);
        Assert.Equal(Status.Protected, FirewallProfileCheckProvider.Judge("NET-001", "Domain", reading).Status);
    }

    [Fact]
    public void A_failed_read_is_a_check_that_could_not_run_for_every_profile()
    {
        var result = Public(FirewallReading.Failed("The service has not been started."));

        Assert.Equal(Status.Error, result.Status);
        Assert.Equal(SecurityStateCodes.Unknown, result.DetectedCode);
        Assert.Equal(SecurityReasonCodes.FirewallReadFailed, result.ReasonCode);
        Assert.Equal("The service has not been started.", result.Detail);
        Assert.Equal(SecurityPolicyState.NotAssessed, result.Policy);
    }

    [Fact]
    public void Policy_that_could_not_be_read_is_unknown_and_never_excuses_a_profile_that_is_off()
    {
        var unreadable = new FirewallPolicyReading { Readable = false };

        var on = Public(Reading(policy: unreadable));
        var off = Public(Reading(@public: false, policy: unreadable));

        Assert.Equal(Status.Protected, on.Status);
        Assert.Equal(SecurityPolicyState.Unknown, on.Policy);
        Assert.Equal(Status.NotProtected, off.Status);
        Assert.Equal(SecurityPolicyState.Unknown, off.Policy);
        Assert.Contains("The policy values could not be read.", off.Detail);
    }

    [Fact]
    public void An_older_standard_profile_value_leaves_the_public_profiles_policy_unknown()
    {
        var policy = new FirewallPolicyReading { Readable = true, Unclear = new[] { "Public" } };

        var result = Public(Reading(@public: false, policy: policy));

        Assert.Equal(Status.NotProtected, result.Status);
        Assert.Equal(SecurityPolicyState.Unknown, result.Policy);
    }

    [Fact]
    public void Profiles_in_use_that_could_not_be_read_are_said_to_be_unknown()
    {
        var result = Public(Reading() with { InUse = null });

        Assert.Equal(Status.Protected, result.Status);
        Assert.Contains("could not be read", result.Detail);
    }

    private static Task<SecurityAssessmentSnapshot> Assess(FirewallProfileCheckProvider provider, bool? server = false, CancellationToken cancel = default) =>
        SecurityAssessmentEngine.RunAsync(
            SecurityBaselineV1.Create(),
            new ISecurityCheckProvider[] { provider },
            new SecurityMachineInfo { IsServerOs = server, OsBuild = 26200 },
            cancellationToken: cancel);

    [Fact]
    public async Task One_read_answers_the_three_profiles()
    {
        int reads = 0;
        var provider = new FirewallProfileCheckProvider(_ =>
        {
            Interlocked.Increment(ref reads);
            return Task.FromResult(Reading(domain: true, @private: false, @public: null));
        });

        var snapshot = await Assess(provider);

        Assert.Equal(1, reads);
        Assert.True(snapshot.Completed);
        Assert.Equal(new[] { "NET-001", "NET-002", "NET-003" }, snapshot.Results.Select(r => r.Id));
        Assert.Equal(new[] { Status.Protected, Status.NotProtected, Status.Unknown }, snapshot.Results.Select(r => r.Status));
        Assert.All(snapshot.Results, r => Assert.NotEqual(default, r.CheckedAt));
    }

    [Fact]
    public async Task The_profiles_are_assessed_on_Windows_Server_too()
    {
        var provider = new FirewallProfileCheckProvider(_ => Task.FromResult(Reading() with { OtherProducts = null }));

        var snapshot = await Assess(provider, server: true);

        Assert.Equal(3, snapshot.Results.Count);
        Assert.All(snapshot.Results, r => Assert.Equal(Status.Protected, r.Status));
    }

    [Fact]
    public async Task A_read_that_throws_is_a_failed_check_not_a_profile_that_is_off()
    {
        var provider = new FirewallProfileCheckProvider(_ => throw new InvalidOperationException("no CIM"));

        var snapshot = await Assess(provider);

        Assert.All(snapshot.Results, r =>
        {
            Assert.Equal(Status.Error, r.Status);
            Assert.Equal(SecurityStateCodes.Unknown, r.DetectedCode);
        });
    }

    [Fact]
    public async Task A_cancelled_read_leaves_the_profiles_unknown()
    {
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FirewallProfileCheckProvider(async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Reading();
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
    public void The_provider_is_registered_and_answers_the_three_firewall_checks()
    {
        var provider = Assert.Single(SecurityCheckProviders.Create().OfType<FirewallProfileCheckProvider>());

        Assert.Equal(new[] { "NET-001", "NET-002", "NET-003" }, provider.CheckIds);
        Assert.True(provider.Timeout <= SecurityAssessmentEngine.MaxProviderTimeout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_findings_read_in_both_languages(bool norwegian)
    {
        var definition = SecurityBaselineV1.Create().Find("NET-003")!;
        var byPolicy = SecurityOverviewPresentation.Row(
            definition, Public(Reading(@public: false, policy: Policy(("Public", false)))), false, norwegian, t => t.ToString("s"));
        var otherProduct = SecurityOverviewPresentation.Row(
            definition, Public(Reading(@public: false, products: new[] { "Contoso Firewall" })), false, norwegian, t => t.ToString("s"));

        Assert.Equal(norwegian ? "Av" : "Off", byPolicy.Detected);
        Assert.Equal(norwegian ? "Styrt av policy" : "Managed by policy", byPolicy.StatusText);
        Assert.StartsWith(norwegian ? "En policy holder Windows-brannmuren av" : "A policy holds Windows Firewall off", byPolicy.Finding);
        Assert.Equal(norwegian ? "Ukjent" : "Unknown", otherProduct.StatusText);
        Assert.Contains(norwegian ? "kan ikke bekreftes her" : "cannot be verified here", otherProduct.Finding);
        Assert.EndsWith(norwegian ? "Dette funnet må vurderes av en person." : "This finding needs a person to judge it.", otherProduct.Finding);
    }
}

public class SecurityPowerShellTests
{
    [Fact]
    public async Task A_script_runs_and_its_output_comes_back_as_text()
    {
        var answer = await SecurityPowerShell.RunAsync(
            SecurityPowerShell.Utf8Output + "[pscustomobject]@{ Name='Brannmur æøå'; Number=3 } | ConvertTo-Json -Compress",
            CancellationToken.None);

        Assert.True(answer.Ran, answer.Error);
        Assert.Equal(0, answer.ExitCode);
        Assert.Equal("{\"Name\":\"Brannmur æøå\",\"Number\":3}", answer.Output.Trim().TrimStart('\uFEFF'));
    }

    [Fact]
    public async Task A_script_that_ends_with_an_error_code_says_so()
    {
        var answer = await SecurityPowerShell.RunAsync("exit 7", CancellationToken.None);

        Assert.True(answer.Ran);
        Assert.Equal(7, answer.ExitCode);
    }

    [Fact]
    public async Task Cancelling_stops_a_script_that_hangs()
    {
        using var cancel = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        var run = SecurityPowerShell.RunAsync("Start-Sleep -Seconds 120", cancel.Token);
        cancel.CancelAfter(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), "Cancelling took " + watch.Elapsed);
    }

    [Fact]
    public async Task A_token_that_is_already_cancelled_starts_nothing()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SecurityPowerShell.RunAsync("exit 0", cancel.Token));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("\r\n  first \r\nsecond", "first")]
    public void The_first_line_of_an_error_is_kept(string? text, string expected) =>
        Assert.Equal(expected, SecurityPowerShell.FirstLine(text));

    [Fact]
    public void PowerShell_is_started_by_its_full_path() =>
        Assert.True(System.IO.Path.IsPathRooted(SecurityPowerShell.ExecutablePath), SecurityPowerShell.ExecutablePath);
}
