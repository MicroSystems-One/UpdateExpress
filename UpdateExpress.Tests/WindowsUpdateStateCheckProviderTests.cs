using UpdateExpress.Models;
using UpdateExpress.Services;
using UpdateExpress.Services.Security;
using UpdateExpress.Services.Security.Providers;
using Xunit;
using Status = UpdateExpress.Services.Security.SecurityAssessmentStatus;

namespace UpdateExpress.Tests;

public class InstalledFixesReaderTests
{
    /// <summary>The shape a Norwegian Windows 11 Pro 25H2 answered with (27 Nov 2026), shortened.</summary>
    private const string Listed =
        "{\"Fixes\":[" +
        "{\"Id\":\"KB5132061\",\"Kind\":\"Security Update\",\"On\":\"2026-11-26\"}," +
        "{\"Id\":\"KB5132688\",\"Kind\":\"Security Update\",\"On\":\"2026-11-26\"}," +
        "{\"Id\":\"KB5127647\",\"Kind\":\"Security Update\",\"On\":\"2026-11-02\"}," +
        "{\"Id\":\"KB5127690\",\"Kind\":\"Update\",\"On\":\"2026-11-02\"}," +
        "{\"Id\":\"KB5050575\",\"Kind\":\"Update\",\"On\":null}]}";

    private static PowerShellAnswer Answered(string output, int exitCode = 0, string error = "") => new(true, exitCode, output, error);

    [Fact]
    public void The_list_of_a_PC_is_read()
    {
        var reading = InstalledFixesReader.Parse(Answered("\uFEFF" + Listed + "\r\n"));

        Assert.True(reading.Succeeded);
        Assert.Equal(5, reading.Fixes.Count);
        Assert.Equal(new InstalledFix("KB5132061", "Security Update", new DateTime(2026, 11, 26)), reading.Fixes[0]);
        Assert.Null(reading.Fixes[4].InstalledOn);
    }

    [Theory]
    [InlineData("{\"Fixes\":{\"Id\":\"KB1\",\"Kind\":\"Security Update\",\"On\":\"2026-11-26\"}}", 1)]
    [InlineData("{\"Fixes\":[]}", 0)]
    [InlineData("{\"Fixes\":null}", 0)]
    [InlineData("{}", 0)]
    [InlineData("{\"Fixes\":[{\"Id\":\"\",\"Kind\":\"\"},7,{\"Id\":\"KB2\",\"Kind\":\"Update\",\"On\":\"26.11.2026\"}]}", 1)]
    public void One_update_an_empty_list_and_lines_without_content_are_read(string output, int count)
    {
        var reading = InstalledFixesReader.Parse(Answered(output));

        Assert.True(reading.Succeeded);
        Assert.Equal(count, reading.Fixes.Count);
        // A day in another form is no day; it is not guessed.
        Assert.All(reading.Fixes.Where(fix => fix.Id == "KB2"), fix => Assert.Null(fix.InstalledOn));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void An_answer_that_is_not_a_list_is_a_failed_read(string output)
    {
        var reading = InstalledFixesReader.Parse(Answered(output));

        Assert.False(reading.Succeeded);
        Assert.NotEqual("", reading.Failure);
    }

    [Fact]
    public void A_PowerShell_that_ends_with_an_error_code_is_a_failed_read_even_with_a_list_in_its_output()
    {
        var reading = InstalledFixesReader.Parse(Answered(Listed, exitCode: 1, error: "Access is denied."));

        Assert.False(reading.Succeeded);
        Assert.Contains("code 1", reading.Failure);
        Assert.Contains("Access is denied.", reading.Failure);
    }

    [Fact]
    public void A_PowerShell_that_did_not_start_is_a_failed_read()
    {
        var reading = InstalledFixesReader.Parse(new PowerShellAnswer(false, -1, "", "Win32Exception: not found"));

        Assert.False(reading.Succeeded);
        Assert.Contains("could not be started", reading.Failure);
    }

    [Fact]
    public void The_script_only_asks_and_does_not_touch_Windows_Update()
    {
        string script = InstalledFixesReader.Script;

        Assert.Contains("Get-HotFix", script);
        foreach (string word in new[] { "Set-", "New-Item", "Remove-", "Install-", "Uninstall", "wusa", "Start-", "Stop-", "Restart-", "Microsoft.Update", "UsoClient" })
            Assert.DoesNotContain(word, script, StringComparison.OrdinalIgnoreCase);
    }
}

public class InstalledSecurityUpdateCheckProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 13, 0, 0, TimeSpan.FromHours(1));
    private static readonly IReadOnlyDictionary<string, double> BaselineLimits = SecurityBaselineV1.Create().Find("UPD-SEC-001")!.Thresholds;

    private static DateTime DaysAgo(int days) => Now.LocalDateTime.Date.AddDays(-days);

    private static InstalledFixesReading Reading(params InstalledFix[] fixes) => new() { Succeeded = true, Fixes = fixes };

    private static InstalledFix Security(string id, int daysAgo) => new(id, "Security Update", DaysAgo(daysAgo));

    private static SecurityCheckResult Judge(InstalledFixesReading reading, IReadOnlyDictionary<string, double>? limits = null) =>
        InstalledSecurityUpdateCheckProvider.Judge(reading, Now, limits ?? BaselineLimits);

    [Fact]
    public void A_security_update_installed_yesterday_is_within_the_limit()
    {
        var result = Judge(Reading(
            Security("KB5132061", 1), Security("KB5132688", 1), Security("KB5127647", 25),
            new InstalledFix("KB5127690", "Update", DaysAgo(0))));

        Assert.Equal("UPD-SEC-001", result.Id);
        Assert.Equal(Status.Protected, result.Status);
        Assert.Equal(SecurityStateCodes.InstalledOn, result.DetectedCode);
        Assert.Equal(DaysAgo(1).ToString("yyyy-MM-dd") + " (KB5132061, KB5132688)", result.DetectedValue);
        Assert.Equal("", result.ReasonCode);
        Assert.Contains("installed 1 days ago", result.Detail);
        Assert.Contains("4 installed updates, 3 of them as security updates", result.Detail);
    }

    [Theory]
    [InlineData(0, Status.Protected)]
    [InlineData(45, Status.Protected)]
    [InlineData(46, Status.NeedsAttention)]
    [InlineData(400, Status.NeedsAttention)]
    public void The_baseline_limit_is_45_days_and_it_has_no_red_limit(int daysAgo, Status expected)
    {
        var result = Judge(Reading(Security("KB1", daysAgo)));

        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == Status.Protected ? "" : SecurityReasonCodes.SecurityUpdateOverdue, result.ReasonCode);
        Assert.Equal(SecurityStateCodes.InstalledOn, result.DetectedCode);
    }

    [Fact]
    public void A_baseline_with_a_second_limit_makes_a_very_old_update_not_protected()
    {
        var limits = new Dictionary<string, double> { [SecurityBaselineV1.WarnAfterDays] = 30, [SecurityBaselineV1.CriticalAfterDays] = 90 };

        Assert.Equal(Status.Protected, Judge(Reading(Security("KB1", 30)), limits).Status);
        Assert.Equal(Status.NeedsAttention, Judge(Reading(Security("KB1", 90)), limits).Status);
        Assert.Equal(Status.NotProtected, Judge(Reading(Security("KB1", 91)), limits).Status);
    }

    [Fact]
    public void Only_what_Windows_calls_a_security_update_counts()
    {
        var result = Judge(Reading(
            new InstalledFix("KB1", "Update", DaysAgo(1)),
            new InstalledFix("KB2", "Hotfix", DaysAgo(2)),
            new InstalledFix("KB3", "security update", DaysAgo(60))));

        Assert.Equal(Status.NeedsAttention, result.Status);
        Assert.StartsWith(DaysAgo(60).ToString("yyyy-MM-dd"), result.DetectedValue);
    }

    [Fact]
    public void A_list_without_a_security_update_is_unknown_not_a_failure()
    {
        var none = Judge(Reading());
        var others = Judge(Reading(new InstalledFix("KB1", "Update", DaysAgo(1))));

        foreach (var result in new[] { none, others })
        {
            Assert.Equal(Status.Unknown, result.Status);
            Assert.Equal(SecurityReasonCodes.NoSecurityUpdateListed, result.ReasonCode);
            Assert.Equal(SecurityStateCodes.Unknown, result.DetectedCode);
        }
    }

    [Fact]
    public void A_security_update_without_a_usable_day_is_unknown()
    {
        var undated = Judge(Reading(new InstalledFix("KB1", "Security Update", null)));
        var ahead = Judge(Reading(new InstalledFix("KB1", "Security Update", DaysAgo(-3))));

        foreach (var result in new[] { undated, ahead })
        {
            Assert.Equal(Status.Unknown, result.Status);
            Assert.Equal(SecurityReasonCodes.SecurityUpdateDateUnclear, result.ReasonCode);
        }
    }

    [Fact]
    public void A_day_one_ahead_of_the_clock_counts_as_today_and_an_undated_update_does_not_hide_a_dated_one()
    {
        var result = Judge(Reading(
            new InstalledFix("KB1", "Security Update", DaysAgo(-1)),
            new InstalledFix("KB2", "Security Update", null)));

        Assert.Equal(Status.Protected, result.Status);
        Assert.Contains("installed 0 days ago", result.Detail);
    }

    [Fact]
    public void A_failed_read_is_a_failed_check()
    {
        var result = Judge(InstalledFixesReading.Failed("PowerShell ended with code 1."));

        Assert.Equal(Status.Error, result.Status);
        Assert.Equal(SecurityReasonCodes.InstalledUpdatesReadFailed, result.ReasonCode);
        Assert.Equal("PowerShell ended with code 1.", result.Detail);
    }

    [Fact]
    public async Task Through_the_engine_the_baseline_limit_and_the_engine_clock_are_used()
    {
        var provider = new InstalledSecurityUpdateCheckProvider(_ => Task.FromResult(Reading(Security("KB1", 46))));

        var snapshot = await SecurityAssessmentEngine.RunAsync(
            SecurityBaselineV1.Create(), new ISecurityCheckProvider[] { provider },
            new SecurityMachineInfo { IsServerOs = true, OsBuild = 20348 },
            new SecurityAssessmentOptions { Clock = () => Now });

        var result = Assert.Single(snapshot.Results);
        Assert.Equal("UPD-SEC-001", result.Id);
        Assert.Equal(Status.NeedsAttention, result.Status);
        Assert.True(provider.Timeout <= SecurityAssessmentEngine.MaxProviderTimeout);
    }
}

public class WindowsUpdateSearchMemoryTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 11, 27, 9, 40, 0, TimeSpan.FromHours(1));

    public WindowsUpdateSearchMemoryTests() => WindowsUpdateSearchMemory.Forget();

    public void Dispose() => WindowsUpdateSearchMemory.Forget();

    private static UpdateEntry Entry(string kb, string title, bool security, string source = "WUA / Software", string? id = null) =>
        new() { KB = kb, Title = title, IsSecurityUpdate = security, Source = source, Category = "Programvare / sikkerhet", UpdateId = id ?? "" };

    [Fact]
    public void A_new_session_knows_of_no_search()
    {
        Assert.Null(WindowsUpdateSearchMemory.Last);

        WindowsUpdateSearchMemory.Remove(new[] { Entry("KB1", "Something", true) });

        // Rows without a search behind them are not a search result.
        Assert.Null(WindowsUpdateSearchMemory.Last);
    }

    [Fact]
    public void A_search_is_kept_with_its_time_and_only_Windows_Update_rows_are_counted()
    {
        WindowsUpdateSearchMemory.Record(new[]
        {
            Entry("KB5132061", "2026-11 Security Update (KB5132061)", true),
            Entry("KB890830", "Malicious Software Removal Tool", false),
            Entry("", "Audio driver", false, "WUA / Driver"),
            Entry("", "Lenovo BIOS update", true, "OEM / Lenovo"),
            Entry("", "Some app", true, "WinGet")
        }, At);

        var last = WindowsUpdateSearchMemory.Last!;
        Assert.Equal(At, last.SearchedAt);
        Assert.Equal(3, last.Offered);
        Assert.Equal(new[] { new WaitingUpdate("KB5132061", "2026-11 Security Update (KB5132061)") }, last.SecurityUpdates);
    }

    [Fact]
    public void A_search_that_found_nothing_is_a_search_too()
    {
        WindowsUpdateSearchMemory.Record(Array.Empty<UpdateEntry>(), At);

        var last = WindowsUpdateSearchMemory.Last!;
        Assert.Equal(0, last.Offered);
        Assert.Empty(last.SecurityUpdates);
    }

    [Fact]
    public void An_installed_update_leaves_the_count_and_the_time_of_the_search_stays()
    {
        var security = Entry("KB5132061", "2026-11 Security Update", true);
        var other = Entry("KB890830", "Malicious Software Removal Tool", false);
        WindowsUpdateSearchMemory.Record(new[] { security, other }, At);

        WindowsUpdateSearchMemory.Remove(new[] { security });

        var last = WindowsUpdateSearchMemory.Last!;
        Assert.Equal(At, last.SearchedAt);
        Assert.Equal(1, last.Offered);
        Assert.Empty(last.SecurityUpdates);
    }

    [Fact]
    public void Rows_are_told_apart_by_the_id_Windows_Update_gives_them()
    {
        WindowsUpdateSearchMemory.Record(new[]
        {
            Entry("KB1", "Cumulative Update", true, id: "aaaa"),
            Entry("KB1", "Cumulative Update", true, id: "bbbb")
        }, At);

        WindowsUpdateSearchMemory.Remove(new[] { Entry("", "Another title for the same update", true, id: "AAAA") });

        Assert.Single(WindowsUpdateSearchMemory.Last!.SecurityUpdates);
    }

    [Fact]
    public void A_row_that_is_not_from_Windows_Update_removes_nothing()
    {
        WindowsUpdateSearchMemory.Record(new[] { Entry("", "Firmware", true) }, At);

        WindowsUpdateSearchMemory.Remove(new[] { Entry("", "Firmware", true, "OEM / Lenovo") });

        Assert.Single(WindowsUpdateSearchMemory.Last!.SecurityUpdates);
    }

    [Fact]
    public void A_verifying_search_adds_what_is_new_and_takes_the_time_of_that_search()
    {
        var later = At.AddMinutes(20);
        WindowsUpdateSearchMemory.Record(new[] { Entry("KB1", "Servicing stack", true, id: "a") }, At);
        WindowsUpdateSearchMemory.Remove(new[] { Entry("KB1", "Servicing stack", true, id: "a") });

        WindowsUpdateSearchMemory.Learn(new[] { Entry("KB2", "Cumulative Update", true, id: "b") }, later);

        var last = WindowsUpdateSearchMemory.Last!;
        Assert.Equal(later, last.SearchedAt);
        Assert.Equal(new[] { new WaitingUpdate("KB2", "Cumulative Update") }, last.SecurityUpdates);
    }

    [Fact]
    public void A_verifying_search_does_not_bring_back_an_update_installed_since_the_search()
    {
        // Windows Update goes on offering an installed update until the PC has restarted.
        var installed = Entry("KB1", "Cumulative Update", true, id: "a");
        var untouched = Entry("KB2", ".NET Update", true, id: "b");
        WindowsUpdateSearchMemory.Record(new[] { installed, untouched }, At);
        WindowsUpdateSearchMemory.Remove(new[] { installed });

        WindowsUpdateSearchMemory.Learn(new[] { installed, untouched }, At.AddMinutes(20));

        Assert.Equal(new[] { new WaitingUpdate("KB2", ".NET Update") }, WindowsUpdateSearchMemory.Last!.SecurityUpdates);
    }

    [Fact]
    public void A_verifying_search_drops_what_Windows_Update_no_longer_offers()
    {
        WindowsUpdateSearchMemory.Record(new[] { Entry("KB1", "Old", true, id: "a"), Entry("KB2", "Kept", true, id: "b") }, At);

        WindowsUpdateSearchMemory.Learn(new[] { Entry("KB2", "Kept", true, id: "b") }, At.AddMinutes(20));

        Assert.Equal(new[] { new WaitingUpdate("KB2", "Kept") }, WindowsUpdateSearchMemory.Last!.SecurityUpdates);
    }

    [Fact]
    public void A_verifying_search_without_an_earlier_search_is_the_first_search()
    {
        WindowsUpdateSearchMemory.Learn(new[] { Entry("KB1", "Cumulative Update", true, id: "a") }, At);

        var last = WindowsUpdateSearchMemory.Last!;
        Assert.Equal(At, last.SearchedAt);
        Assert.Single(last.SecurityUpdates);
    }

    [Fact]
    public void A_new_search_brings_back_an_update_that_is_offered_again()
    {
        var update = Entry("KB1", "Cumulative Update", true, id: "a");
        WindowsUpdateSearchMemory.Record(new[] { update }, At);
        WindowsUpdateSearchMemory.Remove(new[] { update });

        WindowsUpdateSearchMemory.Record(new[] { update }, At.AddHours(1));

        Assert.Single(WindowsUpdateSearchMemory.Last!.SecurityUpdates);
    }

    [Fact]
    public void A_forgotten_search_is_gone()
    {
        WindowsUpdateSearchMemory.Record(new[] { Entry("KB1", "x", true) }, At);

        WindowsUpdateSearchMemory.Forget();

        Assert.Null(WindowsUpdateSearchMemory.Last);
    }

    [Fact]
    public void A_copied_row_keeps_what_Windows_Update_called_it()
    {
        Assert.True(Entry("KB1", "x", true).Clone().IsSecurityUpdate);
        Assert.False(Entry("KB1", "x", false).Clone().IsSecurityUpdate);
        Assert.False(new UpdateEntry().IsSecurityUpdate);
    }

    [Theory]
    [InlineData("0FA1201D-4330-4FA8-8AE9-B877473B6441", true)]
    [InlineData("0fa1201d-4330-4fa8-8ae9-b877473b6441", true)]
    [InlineData(" {0fa1201d-4330-4fa8-8ae9-b877473b6441} ", true)]
    // Critical Updates, Definition Updates and Update Rollups are not security updates.
    [InlineData("E6CF1350-C01B-414D-A61F-263D14D133B4", false)]
    [InlineData("E0789628-CE08-4437-BE74-2495B842F43B", false)]
    [InlineData("28BC880E-0592-4CBF-8F95-C79B17911D5F", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_security_update_is_known_by_the_id_of_its_classification(string? id, bool expected) =>
        Assert.Equal(expected, WindowsUpdateReader.IsSecurityClassificationId(id));
}

public class WindowsServicingCheckProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 13, 0, 0, TimeSpan.FromHours(1));

    private static WindowsUpdateSearchResult Search(double hoursAgo, int offered, params WaitingUpdate[] security) =>
        new(Now.AddHours(-hoursAgo), offered, security);

    // ---- UPD-SEC-002 ----

    [Fact]
    public void Without_a_search_the_waiting_security_updates_are_unknown()
    {
        var result = WindowsServicingCheckProvider.WaitingUpdates(null, Now);

        Assert.Equal("UPD-SEC-002", result.Id);
        Assert.Equal(Status.Unknown, result.Status);
        Assert.Equal(SecurityReasonCodes.NoUpdateSearchYet, result.ReasonCode);
        Assert.Equal(SecurityStateCodes.Unknown, result.DetectedCode);
    }

    [Fact]
    public void A_search_that_found_no_security_update_is_a_pass_and_says_when_it_searched()
    {
        var result = WindowsServicingCheckProvider.WaitingUpdates(Search(3, offered: 2), Now);

        Assert.Equal(Status.Protected, result.Status);
        Assert.Equal(SecurityStateCodes.NoSecurityUpdatesWaiting, result.DetectedCode);
        Assert.Contains(Now.AddHours(-3).LocalDateTime.ToString("yyyy-MM-dd HH:mm"), result.Detail);
        Assert.Contains("offered 2 updates", result.Detail);
        Assert.Contains("0 of them as security updates", result.Detail);
    }

    [Fact]
    public void Waiting_security_updates_need_attention_and_are_named()
    {
        var result = WindowsServicingCheckProvider.WaitingUpdates(Search(1, 3,
            new WaitingUpdate("KB5132061", "2026-11 Security Update (KB5132061) (26200.9974)"),
            new WaitingUpdate("KB5127690", "2026-10 .NET Framework Sikkerhetsoppdatering")), Now);

        Assert.Equal(Status.NeedsAttention, result.Status);
        Assert.Equal(SecurityStateCodes.SecurityUpdatesWaiting, result.DetectedCode);
        Assert.Equal("2", result.DetectedValue);
        Assert.Equal(SecurityReasonCodes.SecurityUpdatesNotInstalled, result.ReasonCode);
        Assert.Contains("2026-11 Security Update (KB5132061) (26200.9974); KB5127690 2026-10 .NET Framework Sikkerhetsoppdatering.", result.Detail);
    }

    [Theory]
    [InlineData(24 * 7 - 1, Status.Protected, "")]
    [InlineData(24 * 7 + 1, Status.Unknown, SecurityReasonCodes.UpdateSearchTooOld)]
    // A search from the future: the clock was set back.
    [InlineData(-25, Status.Unknown, SecurityReasonCodes.UpdateSearchTooOld)]
    [InlineData(-1, Status.Protected, "")]
    public void A_search_is_relied_on_for_seven_days(double hoursAgo, Status expected, string reason)
    {
        var result = WindowsServicingCheckProvider.WaitingUpdates(Search(hoursAgo, 0), Now);

        Assert.Equal(expected, result.Status);
        Assert.Equal(reason, result.ReasonCode);
    }

    // ---- UPD-SEC-004 ----

    private static RestartReading Signs(bool? windowsUpdate = false, bool? servicing = false, bool? files = false) =>
        new() { WindowsUpdate = windowsUpdate, Servicing = servicing, FileOperations = files };

    [Fact]
    public void No_sign_of_a_waiting_restart_is_a_pass()
    {
        var result = WindowsServicingCheckProvider.Restart(Signs());

        Assert.Equal("UPD-SEC-004", result.Id);
        Assert.Equal(Status.Protected, result.Status);
        Assert.Equal(SecurityStateCodes.NoRestartPending, result.DetectedCode);
        Assert.False(result.RestartRequired);
    }

    [Theory]
    [InlineData(true, false, false, "")]
    [InlineData(false, true, false, "")]
    [InlineData(true, true, true, "")]
    [InlineData(false, false, true, SecurityReasonCodes.RestartForFileOperations)]
    // One sign is enough, whatever could not be read beside it.
    [InlineData(true, null, null, "")]
    [InlineData(null, null, true, SecurityReasonCodes.RestartForFileOperations)]
    public void One_sign_is_enough_for_restart_required(bool? windowsUpdate, bool? servicing, bool? files, string reason)
    {
        var result = WindowsServicingCheckProvider.Restart(Signs(windowsUpdate, servicing, files));

        Assert.Equal(Status.RestartRequired, result.Status);
        Assert.Equal(SecurityStateCodes.RestartPending, result.DetectedCode);
        Assert.True(result.RestartRequired);
        Assert.Equal(reason, result.ReasonCode);
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData(false, null, false)]
    [InlineData(false, false, null)]
    [InlineData(null, null, null)]
    public void A_sign_that_could_not_be_read_keeps_the_answer_from_being_a_pass(bool? windowsUpdate, bool? servicing, bool? files)
    {
        var result = WindowsServicingCheckProvider.Restart(Signs(windowsUpdate, servicing, files));

        Assert.Equal(Status.Unknown, result.Status);
        Assert.Equal(SecurityReasonCodes.RestartIndicatorsUnreadable, result.ReasonCode);
        Assert.Contains("could not be read", result.Detail);
    }

    [Fact]
    public void The_signs_are_read_from_the_three_places_Windows_uses()
    {
        var none = WindowsServicingCheckProvider.ReadRestart(new SecurityTestRegistry());
        var all = WindowsServicingCheckProvider.ReadRestart(new SecurityTestRegistry()
            .Key(WindowsServicingCheckProvider.WindowsUpdateRestartKey)
            .Key(WindowsServicingCheckProvider.ServicingRestartKey)
            .Value(WindowsServicingCheckProvider.SessionManagerKey, WindowsServicingCheckProvider.FileOperationsValue, new[] { @"\??\C:\old.dll", "" }));
        var emptyList = WindowsServicingCheckProvider.ReadRestart(new SecurityTestRegistry()
            .Value(WindowsServicingCheckProvider.SessionManagerKey, WindowsServicingCheckProvider.FileOperationsValue, new[] { "", " " }));
        var locked = WindowsServicingCheckProvider.ReadRestart(new SecurityTestRegistry()
            .Locked(WindowsServicingCheckProvider.ServicingRestartKey));

        Assert.Equal(Signs(), none);
        Assert.Equal(Signs(true, true, true), all);
        Assert.Equal(Signs(), emptyList);
        Assert.Equal(Signs(false, null, false), locked);
    }

    // ---- UPD-SEC-005 and UPD-SEC-006 ----

    private static WindowsSupportReader.Result Described(int build, int? productType, string edition, string version, string productName = "", string today = "2026-11-27") =>
        WindowsSupportReader.Describe(
            new WindowsSupportReader.OsFacts(build, productType, edition, version, "", productName),
            DateTime.Parse(today, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void The_Windows_version_is_reported_and_not_judged()
    {
        var result = WindowsServicingCheckProvider.Version(Described(26200, 1, "Professional", "25H2"), 26200, 9974);

        Assert.Equal("UPD-SEC-005", result.Id);
        Assert.Equal(Status.NotApplicable, result.Status);
        Assert.Equal(SecurityReasonCodes.Informational, result.ReasonCode);
        Assert.Equal(SecurityStateCodes.Value, result.DetectedCode);
        Assert.Equal("Windows 11 Pro 25H2 (build 26200.9974)", result.DetectedValue);
        Assert.True(SecurityOverviewPresentation.IsInformation(result));
        Assert.Equal(SecuritySummaryBucket.NotApplicable, SecurityOverallStatus.BucketOf(result));
    }

    [Theory]
    [InlineData(26200, null, "Windows 11 Pro 25H2 (build 26200)")]
    [InlineData(null, 9974, "Windows 11 Pro 25H2")]
    public void A_build_or_revision_that_was_not_read_is_left_out(int? build, int? revision, string expected) =>
        Assert.Equal(expected, WindowsServicingCheckProvider.Version(Described(26200, 1, "Professional", "25H2"), build, revision).DetectedValue);

    [Fact]
    public void A_Windows_that_did_not_say_what_it_is_gives_unknown()
    {
        var unread = new WindowsSupportReader.Result("Windows (unknown)", "", "unknown", "Could not determine Windows support status: denied");

        var version = WindowsServicingCheckProvider.Version(unread, null, null);
        var support = WindowsServicingCheckProvider.Support(unread);

        Assert.Equal(Status.Unknown, version.Status);
        Assert.Equal(SecurityReasonCodes.WindowsVersionUnreadable, version.ReasonCode);
        Assert.Equal(Status.Unknown, support.Status);
        Assert.Equal(SecurityReasonCodes.WindowsSupportNotVerified, support.ReasonCode);
    }

    [Fact]
    public void A_release_Microsoft_still_services_is_supported()
    {
        var result = WindowsServicingCheckProvider.Support(Described(26200, 1, "Professional", "25H2"));

        Assert.Equal("UPD-SEC-006", result.Id);
        Assert.Equal(Status.Protected, result.Status);
        Assert.Equal(SecurityStateCodes.Supported, result.DetectedCode);
        Assert.Contains(WindowsSupportReader.ClientReferenceUrl, result.Detail);
        Assert.DoesNotContain("\n", result.Detail);
    }

    [Theory]
    // Windows 10 after 14 October 2025, and a Windows 11 release past its end of servicing.
    [InlineData(19045, "Professional", "22H2")]
    [InlineData(22631, "Professional", "23H2")]
    public void A_release_past_its_end_of_servicing_is_not_protected(int build, string edition, string version)
    {
        var result = WindowsServicingCheckProvider.Support(Described(build, 1, edition, version));

        Assert.Equal(Status.NotProtected, result.Status);
        Assert.Equal(SecurityStateCodes.SupportEnded, result.DetectedCode);
        Assert.Equal(SecurityReasonCodes.WindowsSupportEnded, result.ReasonCode);
    }

    [Theory]
    // LTSC, a release the table does not know, and a server release outside the table.
    [InlineData(26100, 1, "EnterpriseS", "24H2", "")]
    [InlineData(29000, 1, "Professional", "27H2", "")]
    [InlineData(25398, 3, "ServerDatacenter", "23H2", "Windows Server Datacenter")]
    public void A_release_that_cannot_be_placed_is_unknown_for_a_person_to_look_up_never_supported(
        int build, int productType, string edition, string version, string productName)
    {
        var result = WindowsServicingCheckProvider.Support(Described(build, productType, edition, version, productName));

        Assert.Equal(Status.Unknown, result.Status);
        Assert.True(result.ManualReview);
        Assert.Equal(SecurityReasonCodes.WindowsSupportNotVerified, result.ReasonCode);
        Assert.Equal(SecurityStateCodes.Unknown, result.DetectedCode);
    }

    [Fact]
    public void Windows_Server_is_judged_by_its_own_lifecycle()
    {
        var supported = WindowsServicingCheckProvider.Support(Described(20348, 3, "ServerStandard", "21H2", "Windows Server 2022 Standard"));
        var ended = WindowsServicingCheckProvider.Support(Described(14393, 3, "ServerStandard", "1607", "Windows Server 2016 Standard", today: "2027-02-01"));
        var version = WindowsServicingCheckProvider.Version(Described(20348, 3, "ServerStandard", "21H2", "Windows Server 2022 Standard"), 20348, 4297);

        Assert.Equal(Status.Protected, supported.Status);
        Assert.Contains(WindowsSupportReader.ServerReferenceUrl, supported.Detail);
        Assert.Equal(Status.NotProtected, ended.Status);
        Assert.Equal("Windows Server 2022 Standard (build 20348.4297)", version.DetectedValue);
    }

    // ---- Through the engine ----

    private static WindowsServicingCheckProvider Provider(
        WindowsUpdateSearchResult? search = null, SecurityTestRegistry? registry = null, WindowsSupportReader.Result? support = null) =>
        new(() => search, registry ?? new SecurityTestRegistry(), () => support ?? Described(26200, 1, "Professional", "25H2"));

    private static Task<SecurityAssessmentSnapshot> Assess(ISecurityCheckProvider provider, bool? server = false) =>
        SecurityAssessmentEngine.RunAsync(
            SecurityBaselineV1.Create(), new[] { provider },
            new SecurityMachineInfo { IsServerOs = server, OsBuild = 26200 },
            new SecurityAssessmentOptions { Clock = () => Now });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_four_checks_answer_on_client_and_server_alike(bool server)
    {
        int supportReads = 0;
        var registry = new SecurityTestRegistry().Value(WindowsServicingCheckProvider.CurrentVersionKey, "UBR", 9974);
        var provider = new WindowsServicingCheckProvider(
            () => Search(2, 1), registry, () => { supportReads++; return Described(26200, 1, "Professional", "25H2"); });

        var snapshot = await Assess(provider, server);
        var byId = snapshot.Results.ToDictionary(r => r.Id);

        Assert.Equal(new[] { "UPD-SEC-002", "UPD-SEC-004", "UPD-SEC-005", "UPD-SEC-006" }, snapshot.Results.Select(r => r.Id));
        Assert.Equal(Status.Protected, byId["UPD-SEC-002"].Status);
        Assert.Equal(Status.Protected, byId["UPD-SEC-004"].Status);
        Assert.Equal("Windows 11 Pro 25H2 (build 26200.9974)", byId["UPD-SEC-005"].DetectedValue);
        Assert.Equal(Status.Protected, byId["UPD-SEC-006"].Status);
        Assert.Equal(1, supportReads);
        Assert.Equal(SecurityOverallLevel.Green, SecurityOverallStatus.Evaluate(snapshot, Now).Level);
    }

    [Fact]
    public async Task A_waiting_restart_makes_the_overall_status_orange_not_red()
    {
        var registry = new SecurityTestRegistry().Key(WindowsServicingCheckProvider.WindowsUpdateRestartKey);

        var snapshot = await Assess(Provider(Search(2, 0), registry));
        var overall = SecurityOverallStatus.Evaluate(snapshot, Now);

        Assert.Equal(SecurityOverallLevel.Orange, overall.Level);
        Assert.Equal(1, overall.Summary.RestartRequired);
    }

    [Fact]
    public async Task No_search_yet_leaves_one_unknown_and_no_failure()
    {
        var snapshot = await Assess(Provider());
        var overall = SecurityOverallStatus.Evaluate(snapshot, Now);

        Assert.Equal(Status.Unknown, snapshot.Results.Single(r => r.Id == "UPD-SEC-002").Status);
        Assert.Equal(0, overall.Summary.NeedsAttention);
        Assert.Equal(SecurityOverallLevel.Green, overall.Level);
    }
}

public class SecurityCheckRegistrationTests
{
    [Fact]
    public void Every_check_in_the_baseline_is_answered_by_exactly_one_registered_provider()
    {
        var claimed = SecurityCheckProviders.Create().SelectMany(p => p.CheckIds).ToArray();
        var baseline = SecurityBaselineV1.Create().Definitions.Select(d => d.Id).ToArray();

        Assert.Equal(claimed.Length, claimed.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(baseline.OrderBy(id => id), claimed.OrderBy(id => id));
    }

    [Fact]
    public void Every_registered_provider_keeps_within_the_longest_time_the_engine_allows()
    {
        foreach (var provider in SecurityCheckProviders.Create())
            Assert.True((provider.Timeout ?? TimeSpan.Zero) <= SecurityAssessmentEngine.MaxProviderTimeout, provider.Name);
    }

    [Fact]
    public void Only_END_001_is_left_out_on_Windows_Server()
    {
        var server = new SecurityMachineInfo { IsServerOs = true, OsBuild = 20348 };

        var leftOut = SecurityBaselineV1.Create().Definitions
            .Where(definition => SecurityBaseline.NotApplicableResult(definition, server) is not null)
            .Select(definition => definition.Id);

        Assert.Equal(new[] { "END-001" }, leftOut);
    }
}
