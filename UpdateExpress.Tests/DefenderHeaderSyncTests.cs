using UpdateExpress.Models;
using UpdateExpress.Services;
using Xunit;

namespace UpdateExpress.Tests;

public class DefenderHeaderSyncTests
{
    private static readonly DateTime MpTime = new(2026, 11, 26, 9, 14, 0);

    /// <summary>A state as DefenderStatusReader builds it for a healthy PC.</summary>
    private static DefenderState FromMp(DateTime? updated = null) => new(
        "green",
        "Updated",
        $"Signature: 1.441.212.0\nLast updated: {(updated ?? MpTime):dd.MM.yyyy HH:mm}\nReal-time protection: On\nChecked: 26.11.2026 09:20",
        new DateTime(2026, 11, 26, 9, 20, 0),
        updated ?? MpTime,
        "1.441.212.0");

    [Theory]
    [InlineData("Installert", true)]
    [InlineData("Installed", true)]
    [InlineData(" installert ", true)]
    [InlineData("Already up to date", false)]
    [InlineData("Verification unavailable", false)]
    [InlineData("Failed", false)]
    [InlineData("Check only", false)]
    [InlineData("Disabled", false)]
    [InlineData("Never run", false)]
    [InlineData("Install failed", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_run_that_installed_signatures_moves_the_header(string? result, bool expected) =>
        Assert.Equal(expected, DefenderHeaderSync.InstalledNewSignatures(result));

    [Fact]
    public void A_failed_background_read_keeps_the_status_on_screen()
    {
        var shown = FromMp();
        var failed = new DefenderState("unknown", "Unknown", "Defender check timed out.", DateTime.Now, ReadFailed: true);

        Assert.True(DefenderHeaderSync.KeepShownStatus(shown, failed, quiet: true));
    }

    [Fact]
    public void A_failed_read_is_shown_when_the_user_asked_or_nothing_is_on_screen()
    {
        var failed = new DefenderState("unknown", "Unknown", "Defender check timed out.", DateTime.Now, ReadFailed: true);

        Assert.False(DefenderHeaderSync.KeepShownStatus(FromMp(), failed, quiet: false));
        Assert.False(DefenderHeaderSync.KeepShownStatus(null, failed, quiet: true));
    }

    [Fact]
    public void A_status_Defender_reported_replaces_the_shown_one_even_when_it_is_unknown_or_red()
    {
        var passive = new DefenderState("unknown", "Defender passive", "Third-party antivirus detected.", DateTime.Now);
        var disabled = FromMp() with { Level = "red", Label = "Protection disabled" };

        Assert.False(DefenderHeaderSync.KeepShownStatus(FromMp(), passive, quiet: true));
        Assert.False(DefenderHeaderSync.KeepShownStatus(FromMp(), disabled, quiet: true));
    }

    [Fact]
    public void A_newer_time_replaces_the_shown_time_and_names_its_source()
    {
        var when = MpTime.AddHours(2);

        var updated = DefenderHeaderSync.WithNewerSignatureTime(FromMp(), when, "automation");

        Assert.Equal(when, updated.SignatureLastUpdated);
        Assert.Equal(
            "Signature: 1.441.212.0\nLast updated: 26.11.2026 11:14 (automation)\nReal-time protection: On\nChecked: 26.11.2026 09:20",
            updated.Details);
    }

    [Fact]
    public void Level_label_and_signature_version_are_kept()
    {
        var status = FromMp() with { Level = "red", Label = "Protection disabled" };

        var updated = DefenderHeaderSync.WithNewerSignatureTime(status, MpTime.AddMinutes(5), "Historikk");

        Assert.Equal("red", updated.Level);
        Assert.Equal("Protection disabled", updated.Label);
        Assert.Equal("1.441.212.0", updated.SignatureVersion);
        Assert.Equal(status.CheckedAt, updated.CheckedAt);
    }

    [Fact]
    public void An_older_or_equal_time_changes_nothing()
    {
        var status = FromMp();

        Assert.Same(status, DefenderHeaderSync.WithNewerSignatureTime(status, MpTime, "Historikk"));
        Assert.Same(status, DefenderHeaderSync.WithNewerSignatureTime(status, MpTime.AddMinutes(-1), "automation"));
    }

    [Fact]
    public void A_state_without_a_signature_time_takes_the_given_time()
    {
        var status = new DefenderState("unknown", "Unknown", "Defender check timed out.", new DateTime(2026, 11, 26, 9, 20, 0));

        var updated = DefenderHeaderSync.WithNewerSignatureTime(status, MpTime, "Historikk");

        Assert.Equal(MpTime, updated.SignatureLastUpdated);
        Assert.Equal("Defender check timed out.\nLast updated: 26.11.2026 09:14 (Historikk)", updated.Details);
    }

    [Fact]
    public void Historikk_then_automation_keeps_one_last_updated_line()
    {
        var afterHistory = DefenderHeaderSync.WithNewerSignatureTime(FromMp(), MpTime.AddMinutes(10), "Historikk");

        var afterAutomation = DefenderHeaderSync.WithNewerSignatureTime(afterHistory, MpTime.AddMinutes(30), "automation");

        Assert.Single(afterAutomation.Details.Split('\n'), line => line.StartsWith("Last updated:"));
        Assert.Contains("Last updated: 26.11.2026 09:44 (automation)", afterAutomation.Details);
    }

    [Fact]
    public void The_newest_successful_Defender_row_in_Historikk_gives_the_time()
    {
        var history = new[]
        {
            DefenderRow("2026-11-26 08:02:11", "Vellykket"),
            DefenderRow("2026-11-26 10:31:05", "Vellykket"),
            DefenderRow("2026-11-26 11:45:00", "Mislykket"),
            new UpdateEntry
            {
                KB = "KB5066747", Title = "2025-10 Kumulativ oppdatering (KB5066747)", Status = "Vellykket",
                Date = "2026-11-26 12:00:00", Source = "WUA-historikk"
            }
        };

        var latest = PendingHistoryReconcile.LatestDefenderSuccessAt(history);
        var updated = DefenderHeaderSync.WithNewerSignatureTime(FromMp(), latest!.Value, "Historikk");

        Assert.Equal(new DateTime(2026, 11, 26, 10, 31, 5), updated.SignatureLastUpdated);
    }

    private static UpdateEntry DefenderRow(string date, string status) => new()
    {
        KB = "KB2267602",
        Title = "Sikkerhetsanalyseoppdatering for Microsoft Defender Antivirus - KB2267602 (versjon 1.441.212.0)",
        Status = status,
        Date = date,
        Source = "WUA-historikk"
    };
}
