using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using UpdateExpress.Services.Security;
using Xunit;

namespace UpdateExpress.Tests;

// With the tests that point the cache at a folder of their own: the default path is one static.
[Collection(SecurityOverviewRuntimeCollection.Name)]
public sealed class SecurityResultCacheTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ue-security-cache-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public SecurityResultCacheTests()
    {
        Directory.CreateDirectory(_folder);
        _path = Path.Combine(_folder, SecurityResultCache.FileName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    private static readonly DateTimeOffset At = new(2026, 11, 27, 12, 0, 0, TimeSpan.FromHours(1));

    private static SecurityAssessmentSnapshot Snapshot() => new()
    {
        BaselineId = SecurityBaselineV1.BaselineId,
        ModuleVersion = SecurityAssessmentEngine.ModuleVersion,
        AppVersion = "0.7.2.63",
        ComputerName = "TEST-PC",
        OsName = "Windows 11 Pro 25H2",
        OsVersion = "25H2",
        OsBuild = 26200,
        IsServerOs = false,
        Elevated = true,
        StartedAt = At.AddSeconds(-4),
        CompletedAt = At,
        DurationMs = 4000,
        Completed = true,
        Results = new[]
        {
            new SecurityCheckResult
            {
                Id = "NET-001", Status = SecurityAssessmentStatus.Protected, DetectedCode = SecurityStateCodes.On,
                Policy = SecurityPolicyState.NotManaged, CheckedAt = At, DurationMs = 800
            },
            new SecurityCheckResult
            {
                Id = "END-004", Status = SecurityAssessmentStatus.Unknown, DetectedCode = SecurityStateCodes.Value,
                DetectedValue = "1.463.492.0", ManualReview = true, ReasonCode = SecurityReasonCodes.Timeout,
                Detail = "No answer within 20 seconds.", CheckedAt = At
            },
            new SecurityCheckResult { Id = "UPD-SEC-004", Status = SecurityAssessmentStatus.RestartRequired, RestartRequired = true, CheckedAt = At }
        }
    };

    private string Saved(Func<string, string>? edit = null)
    {
        Assert.True(SecurityResultCache.TrySave(Snapshot(), _path));
        string json = File.ReadAllText(_path);
        if (edit is not null) File.WriteAllText(_path, json = edit(json));
        return json;
    }

    [Fact]
    public void A_saved_assessment_is_read_back_unchanged()
    {
        var saved = Snapshot();

        Assert.True(SecurityResultCache.TrySave(saved, _path));
        var loaded = SecurityResultCache.TryLoad(_path);

        Assert.NotNull(loaded);
        Assert.Equal(saved with { Results = Array.Empty<SecurityCheckResult>() }, loaded! with { Results = Array.Empty<SecurityCheckResult>() });
        Assert.Equal(saved.Results, loaded.Results);
    }

    [Fact]
    public void A_missing_file_is_no_assessment()
    {
        Assert.Null(SecurityResultCache.TryLoad(_path));
        Assert.Null(SecurityResultCache.TryLoad(Path.Combine(_folder, "no-such-folder", SecurityResultCache.FileName)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void A_damaged_or_empty_file_is_no_assessment(string content)
    {
        File.WriteAllText(_path, content);

        Assert.Null(SecurityResultCache.TryLoad(_path));
    }

    [Fact]
    public void A_file_from_another_format_version_is_no_assessment()
    {
        Saved(json => json.Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 2"));

        Assert.Null(SecurityResultCache.TryLoad(_path));
    }

    [Fact]
    public void A_cancelled_assessment_is_not_saved_and_does_not_replace_the_last_whole_one()
    {
        Saved();
        var cancelled = Snapshot() with { Completed = false, Results = Array.Empty<SecurityCheckResult>() };

        Assert.False(SecurityResultCache.TrySave(cancelled, _path));

        Assert.Equal(3, SecurityResultCache.TryLoad(_path)!.Results.Count);
    }

    [Fact]
    public void A_file_that_says_it_is_not_complete_is_no_assessment()
    {
        Saved(json => json.Replace("\"Completed\": true", "\"Completed\": false"));

        Assert.Null(SecurityResultCache.TryLoad(_path));
    }

    [Theory]
    [InlineData("\"Status\": \"Protected\"", "\"Status\": \"Excellent\"")]
    [InlineData("\"Status\": \"Protected\"", "\"Status\": 1")]
    [InlineData("\"Id\": \"NET-001\"", "\"Id\": \"\"")]
    [InlineData("\"BaselineId\": \"UpdateExpress.SecurityBaseline.v1\"", "\"BaselineId\": \"\"")]
    public void A_file_with_a_value_that_cannot_be_trusted_is_no_assessment(string from, string to)
    {
        string json = Saved(text => text.Replace(from, to));
        Assert.Contains(to, json);

        Assert.Null(SecurityResultCache.TryLoad(_path));
    }

    [Fact]
    public void A_result_without_a_status_is_unknown_not_protected()
    {
        Saved(json => json.Replace("\"Status\": \"Protected\",", ""));

        var loaded = SecurityResultCache.TryLoad(_path);

        Assert.Equal(SecurityAssessmentStatus.Unknown, loaded!.Results.Single(r => r.Id == "NET-001").Status);
    }

    [Fact]
    public void A_check_cannot_still_be_running_in_a_saved_assessment()
    {
        Saved(json => json.Replace("\"Status\": \"Protected\"", "\"Status\": \"Checking\""));

        var loaded = SecurityResultCache.TryLoad(_path);

        Assert.Equal(SecurityAssessmentStatus.Unknown, loaded!.Results.Single(r => r.Id == "NET-001").Status);
    }

    [Fact]
    public void Text_in_the_file_is_cleaned_like_a_fresh_result()
    {
        Saved(json => json.Replace("No answer within 20 seconds.", "DefaultPassword=hunter2 key 111111-222222-333333-444444-555555-666666-777777-888888"));

        var detail = SecurityResultCache.TryLoad(_path)!.Results.Single(r => r.Id == "END-004").Detail;

        Assert.DoesNotContain("hunter2", detail);
        Assert.DoesNotContain("111111-222222", detail);
    }

    [Fact]
    public void Null_text_in_the_file_becomes_empty_text()
    {
        Saved(json => json
            .Replace("\"Detail\": \"No answer within 20 seconds.\"", "\"Detail\": null")
            .Replace("\"OsName\": \"Windows 11 Pro 25H2\"", "\"OsName\": null"));

        var loaded = SecurityResultCache.TryLoad(_path)!;

        Assert.Equal("", loaded.OsName);
        Assert.Equal("", loaded.Results.Single(r => r.Id == "END-004").Detail);
    }

    [Fact]
    public void A_file_far_larger_than_any_assessment_is_not_read()
    {
        Saved(json => json + new string(' ', 3 * 1024 * 1024));

        Assert.Null(SecurityResultCache.TryLoad(_path));
    }

    [Fact]
    public void The_file_names_statuses_in_words_and_has_no_field_for_a_secret()
    {
        string json = Saved();

        Assert.Contains("\"Status\": \"RestartRequired\"", json);
        Assert.Contains("\"Policy\": \"NotManaged\"", json);
        using var document = JsonDocument.Parse(json);
        var names = new List<string>();
        Collect(document.RootElement, names);
        string[] forbidden = { "password", "passord", "secret", "recovery", "token", "credential", "user", "key" };
        Assert.DoesNotContain(names, name => forbidden.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Saving_creates_the_folder_and_replaces_the_previous_assessment()
    {
        string nested = Path.Combine(_folder, "MicroSystems", "Update Express", SecurityResultCache.FileName);

        Assert.True(SecurityResultCache.TrySave(Snapshot(), nested));
        Assert.True(SecurityResultCache.TrySave(Snapshot() with { ComputerName = "SECOND" }, nested));

        Assert.Equal("SECOND", SecurityResultCache.TryLoad(nested)!.ComputerName);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(nested)!));
    }

    [Fact]
    public void Saving_over_a_hard_link_replaces_the_link_and_leaves_the_other_file_alone()
    {
        string victim = Path.Combine(_folder, "victim.txt");
        File.WriteAllText(victim, "untouched");
        Assert.True(CreateHardLinkW(_path, victim, IntPtr.Zero), "hard link not created: " + Marshal.GetLastWin32Error());

        Assert.True(SecurityResultCache.TrySave(Snapshot(), _path));

        Assert.Equal("untouched", File.ReadAllText(victim));
        Assert.NotNull(SecurityResultCache.TryLoad(_path));
    }

    [Fact]
    public void A_folder_at_the_file_name_is_no_assessment_and_saving_reports_failure()
    {
        Directory.CreateDirectory(_path);

        Assert.Null(SecurityResultCache.TryLoad(_path));
        Assert.False(SecurityResultCache.TrySave(Snapshot(), _path));
    }

    [Fact]
    public void The_default_file_is_in_the_users_own_Update_Express_folder()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MicroSystems", "Update Express", "security-overview.json");

        Assert.Equal(expected, SecurityResultCache.DefaultPath);
    }

    private static void Collect(JsonElement element, List<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                names.Add(property.Name);
                Collect(property.Value, names);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) Collect(item, names);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
