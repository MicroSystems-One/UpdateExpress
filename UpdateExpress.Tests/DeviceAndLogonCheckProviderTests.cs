using UpdateExpress.Services;
using UpdateExpress.Services.Security;
using UpdateExpress.Services.Security.Providers;
using Xunit;
using Status = UpdateExpress.Services.Security.SecurityAssessmentStatus;

namespace UpdateExpress.Tests;

public class MachineRegistryTests
{
    [Theory]
    [InlineData("DefaultPassword", true)]
    [InlineData(" defaultpassword ", true)]
    [InlineData("AutoAdminLogon", false)]
    [InlineData("DefaultUserName", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_stored_logon_password_is_the_value_that_holds_a_secret(string? name, bool secret) =>
        Assert.Equal(secret, MachineRegistry.HoldsSecret(name));

    [Fact]
    public void The_registry_reader_refuses_to_read_the_stored_logon_password()
    {
        // Refused before any key is opened, so this asks nothing of the machine it runs on.
        var registry = new MachineRegistry();

        Assert.Throws<InvalidOperationException>(() => registry.ReadText(AutoLogonCheckProvider.WinlogonKey, "DefaultPassword"));
        Assert.Throws<InvalidOperationException>(() => registry.ReadNumber(AutoLogonCheckProvider.WinlogonKey, "defaultpassword"));
        Assert.Throws<InvalidOperationException>(() => registry.CountLines(AutoLogonCheckProvider.WinlogonKey, " DefaultPassword "));
    }

    [Fact]
    public void A_refused_read_is_a_fault_in_the_code_and_not_an_unreadable_value()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => new MachineRegistry().ReadText("SOFTWARE", "DefaultPassword"));

        Assert.False(MachineRegistry.IsReadFailure(refused));
        Assert.True(MachineRegistry.IsReadFailure(new UnauthorizedAccessException()));
        Assert.True(MachineRegistry.IsReadFailure(new System.Security.SecurityException()));
        Assert.True(MachineRegistry.IsReadFailure(new System.IO.IOException()));
    }
}

public class AutoLogonCheckProviderTests
{
    private const string Key = AutoLogonCheckProvider.WinlogonKey;
    private const string Secret = "hunter2-do-not-read";

    private static SecurityTestRegistry Winlogon() => new SecurityTestRegistry()
        .Value(Key, "Shell", "explorer.exe")
        .Value(Key, "DefaultDomainName", "");

    private static void AssertNeverCarriesTheSecret(SecurityCheckResult result)
    {
        foreach (string text in new[] { result.Detail, result.DetectedValue, result.DetectedCode, result.ReasonCode })
            Assert.DoesNotContain(Secret, text);
    }

    [Fact]
    public void A_PC_without_automatic_logon_is_not_configured()
    {
        var registry = Winlogon();

        var reading = AutoLogonCheckProvider.Read(registry);
        var result = AutoLogonCheckProvider.Judge(reading);

        Assert.True(reading.Readable);
        Assert.Null(reading.Switch);
        Assert.False(reading.PasswordValuePresent);
        Assert.Equal("ACC-007", result.Id);
        Assert.Equal(Status.Protected, result.Status);
        Assert.Equal(SecurityStateCodes.NotConfigured, result.DetectedCode);
        Assert.Equal("", result.ReasonCode);
        Assert.Contains("No DefaultPassword value exists.", result.Detail);
        // Nothing was opened at all: the names of the values were enough.
        Assert.Empty(registry.DataReads);
    }

    [Fact]
    public void Automatic_logon_with_the_password_in_the_registry_is_not_protected_and_the_password_is_never_read()
    {
        var registry = Winlogon()
            .Value(Key, "AutoAdminLogon", "1")
            .Value(Key, "DefaultUserName", "kiosk")
            .Value(Key, "DefaultPassword", Secret);

        var reading = AutoLogonCheckProvider.Read(registry);
        var result = AutoLogonCheckProvider.Judge(reading);

        Assert.True(reading.PasswordValuePresent);
        Assert.Equal(Status.NotProtected, result.Status);
        Assert.Equal(SecurityStateCodes.Configured, result.DetectedCode);
        Assert.Equal(SecurityReasonCodes.AutoLogonPasswordInRegistry, result.ReasonCode);
        Assert.Contains("A DefaultPassword value exists; it was not read.", result.Detail);
        Assert.Equal(new[] { Key + "|AutoAdminLogon" }, registry.DataReads);
        AssertNeverCarriesTheSecret(result);
        // The user name is not needed for the answer and is not repeated either.
        Assert.DoesNotContain("kiosk", result.Detail);
    }

    [Fact]
    public void Automatic_logon_without_a_password_in_the_registry_needs_attention()
    {
        var result = AutoLogonCheckProvider.Judge(AutoLogonCheckProvider.Read(Winlogon().Value(Key, "AutoAdminLogon", "1")));

        Assert.Equal(Status.NeedsAttention, result.Status);
        Assert.Equal(SecurityStateCodes.Configured, result.DetectedCode);
        Assert.Equal(SecurityReasonCodes.AutoLogonOn, result.ReasonCode);
    }

    [Fact]
    public void A_password_left_behind_after_automatic_logon_was_switched_off_needs_attention()
    {
        var registry = Winlogon().Value(Key, "AutoAdminLogon", "0").Value(Key, "DefaultPassword", Secret);

        var result = AutoLogonCheckProvider.Judge(AutoLogonCheckProvider.Read(registry));

        Assert.Equal(Status.NeedsAttention, result.Status);
        Assert.Equal(SecurityStateCodes.NotConfigured, result.DetectedCode);
        Assert.Equal(SecurityReasonCodes.AutoLogonPasswordLeftBehind, result.ReasonCode);
        Assert.DoesNotContain(registry.DataReads, read => read.EndsWith("|DefaultPassword", StringComparison.OrdinalIgnoreCase));
        AssertNeverCarriesTheSecret(result);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData(" 1 ", true)]
    [InlineData("2", true)]
    [InlineData("yes", null)]
    [InlineData("(not text)", null)]
    public void The_switch_is_read_as_a_number(string? text, bool? expected) =>
        Assert.Equal(expected, AutoLogonCheckProvider.IsOn(text));

    [Fact]
    public void A_switch_stored_as_a_number_is_read_too()
    {
        var result = AutoLogonCheckProvider.Judge(AutoLogonCheckProvider.Read(Winlogon().Value(Key, "AutoAdminLogon", 1)));

        Assert.Equal(Status.NeedsAttention, result.Status);
        Assert.Equal(SecurityStateCodes.Configured, result.DetectedCode);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("on")]
    public void A_switch_that_says_neither_on_nor_off_is_unknown_and_its_text_is_not_repeated(string text)
    {
        var result = AutoLogonCheckProvider.Judge(AutoLogonCheckProvider.Read(Winlogon().Value(Key, "AutoAdminLogon", text)));

        Assert.Equal(Status.Unknown, result.Status);
        Assert.Equal(SecurityReasonCodes.AutoLogonValueUnclear, result.ReasonCode);
        Assert.Equal(SecurityStateCodes.Unknown, result.DetectedCode);
        Assert.DoesNotContain("\"" + text + "\"", result.Detail);
    }

    [Fact]
    public void A_switch_that_is_neither_text_nor_a_number_is_unknown_not_off()
    {
        var result = AutoLogonCheckProvider.Judge(AutoLogonCheckProvider.Read(Winlogon().Value(Key, "AutoAdminLogon", new byte[] { 1 })));

        Assert.Equal(Status.Unknown, result.Status);
        Assert.Equal(SecurityReasonCodes.AutoLogonValueUnclear, result.ReasonCode);
    }

    [Fact]
    public void A_key_that_may_not_be_read_is_unknown_not_a_pass()
    {
        var result = AutoLogonCheckProvider.Judge(AutoLogonCheckProvider.Read(Winlogon().Locked(Key)));

        Assert.Equal(Status.Unknown, result.Status);
        Assert.Equal(SecurityReasonCodes.AutoLogonUnreadable, result.ReasonCode);
    }

    [Fact]
    public void A_key_that_is_missing_is_unknown_not_a_pass()
    {
        var result = AutoLogonCheckProvider.Judge(AutoLogonCheckProvider.Read(new SecurityTestRegistry()));

        Assert.Equal(Status.Unknown, result.Status);
        Assert.Equal(SecurityReasonCodes.AutoLogonUnreadable, result.ReasonCode);
    }

    [Fact]
    public void A_limited_number_of_automatic_logons_is_named_in_the_detail()
    {
        var registry = Winlogon().Value(Key, "AutoAdminLogon", "1").Value(Key, "AutoLogonCount", 1);

        var result = AutoLogonCheckProvider.Judge(AutoLogonCheckProvider.Read(registry));

        Assert.Equal(Status.NeedsAttention, result.Status);
        Assert.Contains("AutoLogonCount", result.Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_check_runs_on_client_and_server_alike(bool server)
    {
        var provider = new AutoLogonCheckProvider(() => new AutoLogonReading { Readable = true });

        var snapshot = await SecurityAssessmentEngine.RunAsync(
            SecurityBaselineV1.Create(), new ISecurityCheckProvider[] { provider },
            new SecurityMachineInfo { IsServerOs = server, OsBuild = 26200 });

        var result = Assert.Single(snapshot.Results);
        Assert.Equal("ACC-007", result.Id);
        Assert.Equal(Status.Protected, result.Status);
    }

    [Fact]
    public async Task Automatic_logon_with_a_readable_password_turns_the_overall_status_red()
    {
        var provider = new AutoLogonCheckProvider(() => new AutoLogonReading { Readable = true, Switch = "1", PasswordValuePresent = true });

        var snapshot = await SecurityAssessmentEngine.RunAsync(
            SecurityBaselineV1.Create(), new ISecurityCheckProvider[] { provider }, new SecurityMachineInfo { IsServerOs = false, OsBuild = 26200 });

        Assert.Equal(SecurityOverallLevel.Red, SecurityOverallStatus.Evaluate(snapshot, DateTimeOffset.Now).Level);
    }
}

public class DeviceSecurityCheckProviderTests
{
    private const int Bios = Windows11Eligibility.FirmwareBios;
    private const int Uefi = Windows11Eligibility.FirmwareUefi;

    private static DeviceSecurityReading Reading(int? firmware = Uefi, int? secureBoot = 1, bool readable = true, int? tpm = 2) => new()
    {
        FirmwareType = firmware, SecureBoot = secureBoot, SecureBootReadable = readable, TpmVersion = tpm
    };

    private static SecurityCheckResult Judge(string id, DeviceSecurityReading reading) => DeviceSecurityCheckProvider.Judge(id, reading);

    [Fact]
    public void The_state_is_read_from_the_registry_and_the_rest_from_the_Windows_11_reader()
    {
        var registry = new SecurityTestRegistry()
            .Value(DeviceSecurityCheckProvider.SecureBootStateKey, DeviceSecurityCheckProvider.SecureBootStateValue, 1);

        var reading = DeviceSecurityCheckProvider.Read(registry, () => new Windows11Eligibility.Facts { FirmwareType = Uefi, TpmVersion = 2 });

        Assert.Equal(Uefi, reading.FirmwareType);
        Assert.Equal(1, reading.SecureBoot);
        Assert.True(reading.SecureBootReadable);
        Assert.Equal(2, reading.TpmVersion);
    }

    [Fact]
    public void A_state_that_may_not_be_read_is_kept_apart_from_a_state_that_is_not_there()
    {
        var facts = new Windows11Eligibility.Facts { FirmwareType = Uefi };

        var locked = DeviceSecurityCheckProvider.Read(new SecurityTestRegistry().Locked(DeviceSecurityCheckProvider.SecureBootStateKey), () => facts);
        var absent = DeviceSecurityCheckProvider.Read(new SecurityTestRegistry(), () => facts);

        Assert.False(locked.SecureBootReadable);
        Assert.Null(locked.SecureBoot);
        Assert.True(absent.SecureBootReadable);
        Assert.Null(absent.SecureBoot);
        Assert.Null(absent.TpmVersion);
    }

    [Theory]
    [InlineData(Uefi, 1, true, Status.Protected, SecurityStateCodes.On, "")]
    // Secure Boot that is on says by itself that the firmware is UEFI.
    [InlineData(null, 1, true, Status.Protected, SecurityStateCodes.On, "")]
    [InlineData(Uefi, 0, true, Status.NeedsAttention, SecurityStateCodes.Off, SecurityReasonCodes.SecureBootOff)]
    [InlineData(null, 0, true, Status.NeedsAttention, SecurityStateCodes.Off, SecurityReasonCodes.SecureBootOff)]
    [InlineData(Bios, null, true, Status.NotSupported, SecurityStateCodes.Unknown, SecurityReasonCodes.SecureBootLegacyBios)]
    [InlineData(Bios, 0, true, Status.NotSupported, SecurityStateCodes.Unknown, SecurityReasonCodes.SecureBootLegacyBios)]
    [InlineData(Bios, null, false, Status.NotSupported, SecurityStateCodes.Unknown, SecurityReasonCodes.SecureBootLegacyBios)]
    [InlineData(Uefi, null, true, Status.Unknown, SecurityStateCodes.Unknown, SecurityReasonCodes.SecureBootNotReported)]
    [InlineData(null, null, true, Status.Unknown, SecurityStateCodes.Unknown, SecurityReasonCodes.SecureBootNotReported)]
    [InlineData(Uefi, null, false, Status.Unknown, SecurityStateCodes.Unknown, SecurityReasonCodes.SecureBootUnreadable)]
    [InlineData(null, null, false, Status.Unknown, SecurityStateCodes.Unknown, SecurityReasonCodes.SecureBootUnreadable)]
    public void Secure_Boot_is_judged_from_the_state_and_the_firmware_type(
        int? firmware, int? state, bool readable, Status status, string code, string reason)
    {
        var result = Judge("DEV-003", Reading(firmware, state, readable));

        Assert.Equal("DEV-003", result.Id);
        Assert.Equal(status, result.Status);
        Assert.Equal(code, result.DetectedCode);
        Assert.Equal(reason, result.ReasonCode);
        Assert.NotEqual("", result.Detail);
    }

    [Theory]
    [InlineData(null, Status.Unknown, SecurityStateCodes.Unknown, SecurityReasonCodes.TpmNotRead)]
    [InlineData(0, Status.NotSupported, SecurityStateCodes.NotFound, SecurityReasonCodes.TpmNotFound)]
    [InlineData(1, Status.Protected, SecurityStateCodes.Present, "")]
    [InlineData(2, Status.Protected, SecurityStateCodes.Present, "")]
    [InlineData(3, Status.Protected, SecurityStateCodes.Present, "")]
    public void A_TPM_is_present_whatever_its_version(int? version, Status status, string code, string reason)
    {
        var result = Judge("DEV-004", Reading(tpm: version));

        Assert.Equal("DEV-004", result.Id);
        Assert.Equal(status, result.Status);
        Assert.Equal(code, result.DetectedCode);
        Assert.Equal(reason, result.ReasonCode);
    }

    [Theory]
    [InlineData(null, Status.Unknown, SecurityStateCodes.Unknown, SecurityReasonCodes.TpmNotRead)]
    [InlineData(0, Status.NotSupported, SecurityStateCodes.NotFound, SecurityReasonCodes.TpmNotFound)]
    [InlineData(1, Status.NeedsAttention, SecurityStateCodes.Tpm12, SecurityReasonCodes.TpmOlderVersion)]
    [InlineData(2, Status.Protected, SecurityStateCodes.Tpm20, "")]
    [InlineData(3, Status.Unknown, SecurityStateCodes.Unknown, SecurityReasonCodes.TpmVersionNotKnown)]
    public void The_TPM_version_is_judged_against_2_0(int? version, Status status, string code, string reason)
    {
        var result = Judge("DEV-005", Reading(tpm: version));

        Assert.Equal("DEV-005", result.Id);
        Assert.Equal(status, result.Status);
        Assert.Equal(code, result.DetectedCode);
        Assert.Equal(reason, result.ReasonCode);
    }

    private static Task<SecurityAssessmentSnapshot> Assess(DeviceSecurityCheckProvider provider, bool? server = false) =>
        SecurityAssessmentEngine.RunAsync(
            SecurityBaselineV1.Create(), new ISecurityCheckProvider[] { provider },
            new SecurityMachineInfo { IsServerOs = server, OsBuild = 26200 });

    [Fact]
    public async Task One_read_answers_the_three_checks()
    {
        int reads = 0;
        var provider = new DeviceSecurityCheckProvider(() => { reads++; return Reading(); });

        var snapshot = await Assess(provider);

        Assert.Equal(1, reads);
        Assert.Equal(new[] { "DEV-003", "DEV-004", "DEV-005" }, snapshot.Results.Select(r => r.Id));
        Assert.All(snapshot.Results, r => Assert.Equal(Status.Protected, r.Status));
        Assert.Equal(SecurityOverallLevel.Green, SecurityOverallStatus.Evaluate(snapshot, DateTimeOffset.Now).Level);
    }

    [Fact]
    public async Task An_old_PC_without_UEFI_and_TPM_is_not_supported_which_is_not_a_failure()
    {
        var provider = new DeviceSecurityCheckProvider(() => Reading(firmware: Bios, secureBoot: null, tpm: 0));

        var snapshot = await Assess(provider);
        var overall = SecurityOverallStatus.Evaluate(snapshot, DateTimeOffset.Now);

        Assert.All(snapshot.Results, r => Assert.Equal(Status.NotSupported, r.Status));
        Assert.Equal(0, overall.Summary.NeedsAttention);
        Assert.Equal(3, overall.Summary.UnknownOrUnsupported);
        Assert.NotEqual(SecurityOverallLevel.Red, overall.Level);
        Assert.NotEqual(SecurityOverallLevel.Orange, overall.Level);
    }

    [Fact]
    public async Task The_checks_run_on_Windows_Server_too()
    {
        var snapshot = await Assess(new DeviceSecurityCheckProvider(() => Reading()), server: true);

        Assert.Equal(3, snapshot.Results.Count);
        Assert.All(snapshot.Results, r => Assert.Equal(Status.Protected, r.Status));
    }

    [Fact]
    public async Task A_read_that_throws_is_a_failed_check_not_a_setting_that_is_off()
    {
        var snapshot = await Assess(new DeviceSecurityCheckProvider(() => throw new InvalidOperationException("no TBS")));

        Assert.Equal(3, snapshot.Results.Count);
        Assert.All(snapshot.Results, r => Assert.Equal(Status.Error, r.Status));
    }
}
