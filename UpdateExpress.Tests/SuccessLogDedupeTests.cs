using UpdateExpress.Models;
using UpdateExpress.Services;
using Xunit;

namespace UpdateExpress.Tests;

/// <summary>
/// Rows are built the way the app builds them: Windows Update history rows as
/// WindowsUpdateReader.History() fills them (no Category, Norwegian status, UpdateId), and
/// Installation Manager rows through the app's own queue-item converters.
/// </summary>
public class SuccessLogDedupeTests
{
    private const string IntelTitle = "INTEL - System - 10/3/2016 12:00:00 AM - 10.1.1.38";
    private const string DotNetTitle =
        "2025-10 Kumulativ oppdatering for .NET Framework 3.5, 4.8 og 4.8.1 for Windows 10 Version 22H2 for x64 (KB5066747)";
    private const string IdA = "11111111-aaaa-4bbb-8ccc-000000000001";
    private const string IdB = "22222222-aaaa-4bbb-8ccc-000000000002";

    private static DateTime? ParseDate(string? value) => ReminderSchedule.TryParseDate(value);

    private static UpdateEntry WuaHistory(string title, string updateId, string date, string status = "Vellykket", string error = "") => new()
    {
        Title = title,
        KB = string.Join(", ", System.Text.RegularExpressions.Regex.Matches(title, @"KB\d{5,9}").Select(m => m.Value)),
        UpdateId = updateId,
        Status = status,
        Date = date,
        Source = "WUA-historikk",
        ErrorCode = error
    };

    private static InstallationQueueItem Queued(string source, string title, string updateId, string state = "Completed", string? tip = null) =>
        new(source, title) { UpdateId = updateId, State = state, Tip = tip };

    /// <summary>The in-memory outcome row the Success / Failures lists get right after a run.</summary>
    private static UpdateEntry Recent(string source, string title, string updateId, string state = "Completed", string? tip = null) =>
        MainWindow.ToInstallOutcomeEntry(Queued(source, title, updateId, state, tip));

    /// <summary>The row the same run writes to the History archive.</summary>
    private static UpdateEntry Archived(string source, string title, string updateId, string date, string state = "Completed") =>
        HistoryArchive.FromInstallationQueueItem(Queued(source, title, updateId, state), DateTime.Parse(date))!;

    [Fact]
    public void Success_list_shows_the_reported_Intel_driver_once()
    {
        var history = new[]
        {
            WuaHistory(IntelTitle, IdA, "2026-11-25 14:13:28"),
            WuaHistory(DotNetTitle, IdB, "2026-11-25 14:11:02")
        };
        var recent = new[]
        {
            Recent("Windows Update", DotNetTitle, IdB),
            Recent("Driver", IntelTitle, IdA)
        };

        var merged = OutcomeIdentity.MergeRecentWithHistory(history, recent, ParseDate);

        Assert.Equal(2, merged.Count);
        Assert.Single(merged, e => e.Title == IntelTitle);
        Assert.Single(merged, e => e.Title == DotNetTitle);
        // The History row is the one shown: it has the real install time and is what the summary counts.
        Assert.All(merged, e => Assert.Equal("WUA-historikk", e.Source));
    }

    [Fact]
    public void Rows_archived_without_an_UpdateId_still_merge_by_title()
    {
        // Builds up to 0.7.2.59 wrote Installation Manager rows without the UpdateId.
        var history = new[]
        {
            WuaHistory(IntelTitle, IdA, "2026-11-25 14:13:28"),
            Archived("Driver", IntelTitle, "", "2026-11-25 14:14:05"),
            WuaHistory(DotNetTitle, IdB, "2026-11-25 14:11:02"),
            Archived("Windows Update", DotNetTitle, "", "2026-11-25 14:14:04")
        };

        var collapsed = OutcomeIdentity.Collapse(history, ParseDate);

        Assert.Equal(2, collapsed.Count);

        var merged = OutcomeIdentity.MergeRecentWithHistory(
            collapsed,
            new[] { Recent("Driver", IntelTitle, ""), Recent("Windows Update", DotNetTitle, "") },
            ParseDate);
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Same_title_and_version_with_different_UpdateIds_stay_separate()
    {
        // Two Intel System drivers for different hardware: same title and version, own UpdateId.
        var history = new[]
        {
            WuaHistory(IntelTitle, IdA, "2026-11-25 14:13:28"),
            WuaHistory(IntelTitle, IdB, "2026-11-25 14:13:41"),
            Archived("Driver", IntelTitle, IdA, "2026-11-25 14:14:04"),
            Archived("Driver", IntelTitle, IdB, "2026-11-25 14:14:05")
        };
        var recent = new[] { Recent("Driver", IntelTitle, IdA), Recent("Driver", IntelTitle, IdB) };

        var collapsed = OutcomeIdentity.Collapse(history, ParseDate);
        var merged = OutcomeIdentity.MergeRecentWithHistory(collapsed, recent, ParseDate);

        Assert.Equal(2, collapsed.Count);
        Assert.Equal(2, merged.Count);
        Assert.Equal(new[] { IdA, IdB }, merged.Select(e => e.UpdateId).OrderBy(id => id).ToArray());
    }

    [Fact]
    public void Row_without_an_id_is_not_added_when_several_ids_share_its_title()
    {
        var history = new[]
        {
            WuaHistory(IntelTitle, IdA, "2026-11-25 14:13:28"),
            WuaHistory(IntelTitle, IdB, "2026-11-25 14:13:41"),
            Archived("Driver", IntelTitle, "", "2026-11-25 14:14:04")
        };

        var collapsed = OutcomeIdentity.Collapse(history, ParseDate);

        Assert.Equal(new[] { IdA, IdB }, collapsed.Select(e => e.UpdateId).OrderBy(id => id).ToArray());
    }

    [Fact]
    public void Title_without_a_version_still_collapses_across_UpdateIds()
    {
        // Store apps reuse one title for every release; History has always shown the newest only.
        const string title = "9NBLGGH4NNS1-Microsoft.DesktopAppInstaller";
        var history = new[]
        {
            WuaHistory(title, IdA, "2026-10-02 08:00:00"),
            WuaHistory(title, IdB, "2026-11-20 08:00:00")
        };

        var collapsed = OutcomeIdentity.Collapse(history, ParseDate);

        Assert.Equal("2026-11-20 08:00:00", Assert.Single(collapsed).Date);
    }

    [Fact]
    public void Same_UpdateId_merges_even_when_the_queue_title_was_decorated()
    {
        // The category install helper queues "KB — title"; only the UpdateId ties it to history.
        var history = new[] { WuaHistory(DotNetTitle, IdB, "2026-11-25 14:11:02") };
        var archived = Archived("Windows Update", "KB5066747 — " + DotNetTitle, IdB, "2026-11-25 14:14:04");

        var collapsed = OutcomeIdentity.Collapse(history.Append(archived), ParseDate);

        Assert.Single(collapsed);
    }

    [Fact]
    public void Driver_title_without_date_noise_merges_across_sources()
    {
        const string title = "Intel Corporation - Display - 31.0.101.5186";
        var history = new[] { WuaHistory(title, "", "2026-11-25 14:13:28") };

        var merged = OutcomeIdentity.MergeRecentWithHistory(history, new[] { Recent("Driver", title, "") }, ParseDate);

        Assert.Single(merged);
    }

    [Fact]
    public void Different_updates_success_and_failure_and_different_errors_stay_separate()
    {
        var rows = new[]
        {
            WuaHistory(IntelTitle, "", "2026-11-25 14:13:28"),
            WuaHistory("Realtek Audio - 10/1/2024 12:00:00 AM - 6.0.9159.1", "", "2026-11-25 14:13:30"),
            WuaHistory("INTEL - System - 10/3/2016 12:00:00 AM - 10.1.1.44", "", "2026-11-25 14:13:32"),
            WuaHistory(IntelTitle, "", "2026-11-24 09:00:00", "Mislykket", "0x80070103"),
            WuaHistory(IntelTitle, "", "2026-11-23 09:00:00", "Mislykket", "0x800F0247")
        };

        Assert.Equal(5, OutcomeIdentity.Collapse(rows, ParseDate).Count);
    }

    [Fact]
    public void Failures_list_shows_one_row_and_keeps_the_History_row()
    {
        const string tip = "0x80070643 — ERROR_INSTALL_FAILURE.";
        var history = new[] { WuaHistory(DotNetTitle, IdB, "2026-11-25 14:11:02", "Mislykket", "0x80070643") };
        var recent = new[] { Recent("Windows Update", DotNetTitle, IdB, "Failed 0x80070643", tip) };

        var merged = OutcomeIdentity.MergeRecentWithHistory(history, recent, ParseDate);

        // Unresolved and Fix it act on the History row, so it must be the one that is kept.
        Assert.Same(history[0], Assert.Single(merged));
    }

    [Fact]
    public void Outcome_without_a_History_row_is_still_listed()
    {
        var history = new[] { WuaHistory(DotNetTitle, IdB, "2026-11-25 14:11:02") };
        var recent = new[] { Recent("WinGet", "Mozilla Firefox", ""), Recent("Windows Update", DotNetTitle, IdB) };

        var merged = OutcomeIdentity.MergeRecentWithHistory(history, recent, ParseDate);

        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, e => e.Title == "Mozilla Firefox");
    }

    [Fact]
    public void Installation_Manager_rows_carry_KB_and_UpdateId()
    {
        var recent = Recent("Windows Update", DotNetTitle, IdB);
        var archived = Archived("Windows Update", DotNetTitle, IdB, "2026-11-25 14:14:04");

        Assert.Equal("KB5066747", recent.KB);
        Assert.Equal(IdB, recent.UpdateId);
        Assert.Equal(IdB, archived.UpdateId);
    }

    [Fact]
    public void Driver_title_reads_the_same_with_and_without_a_Category()
    {
        var history = WuaHistory(IntelTitle, IdA, "2026-11-25 14:13:28");
        var recent = Recent("Driver", IntelTitle, IdA);

        Assert.Equal("INTEL - System  ·  10.1.1.38", history.DisplayTitle);
        Assert.Equal(history.DisplayTitle, recent.DisplayTitle);
    }

    [Fact]
    public void Software_title_is_not_reshaped()
    {
        var entry = WuaHistory(DotNetTitle, IdB, "2026-11-25 14:11:02");

        Assert.Equal(DotNetTitle, entry.DisplayTitle);
    }

    [Theory]
    [InlineData("Vellykket", false, "Successful")]
    [InlineData("Successful", false, "Successful")]
    [InlineData("Vellykket", true, "Vellykket")]
    [InlineData("Successful", true, "Vellykket")]
    [InlineData("Mislykket", false, "Failed")]
    [InlineData("Failed", true, "Mislykket")]
    [InlineData("Avbrutt", false, "Cancelled")]
    [InlineData("Vellykket med feil", false, "Partial")]
    [InlineData("Fixed", true, "Fikset")]
    [InlineData("Ukjent", false, "Unknown")]
    [InlineData("Tilgjengelig", false, "Tilgjengelig")]
    public void Status_is_worded_in_the_UI_language(string stored, bool norwegian, string expected)
    {
        Assert.Equal(expected, HistoryStatusDisplay.Format(stored, norwegian));
    }

    [Fact]
    public void Status_cell_follows_the_UI_language_for_both_sources()
    {
        var history = WuaHistory(IntelTitle, IdA, "2026-11-25 14:13:28");
        var recent = Recent("Driver", IntelTitle, IdA);
        bool before = HistoryStatusDisplay.NorwegianUi;
        try
        {
            HistoryStatusDisplay.NorwegianUi = false;
            Assert.Equal("Successful", history.DisplayStatus);
            Assert.Equal("Successful", recent.DisplayStatus);

            HistoryStatusDisplay.NorwegianUi = true;
            Assert.Equal("Vellykket", history.DisplayStatus);
            Assert.Equal("Vellykket", recent.DisplayStatus);
        }
        finally
        {
            HistoryStatusDisplay.NorwegianUi = before;
        }
    }

    [Fact]
    public void Status_cell_keeps_error_code_and_pending_driver_status()
    {
        var failed = WuaHistory(DotNetTitle, IdB, "2026-11-25 14:11:02", "Mislykket", "0x80070643");
        var pendingDriver = new UpdateEntry
        {
            Title = IntelTitle,
            Category = "Driver / firmware",
            Status = "Tilgjengelig",
            DriverStatus = "Recommended"
        };

        Assert.Equal("0x80070643", failed.DisplayStatus);
        Assert.Equal("Recommended", pendingDriver.DisplayStatus);
    }

    [Fact]
    public void Identity_key_ignores_the_language_the_status_was_stored_in()
    {
        var norwegian = WuaHistory(DotNetTitle, "", "2026-11-25 14:11:02");
        var english = WuaHistory(DotNetTitle, "", "2026-11-25 14:14:04", "Successful");

        Assert.Equal(HistoryArchive.DisplayIdentityKey(norwegian), HistoryArchive.DisplayIdentityKey(english));
    }

    [Fact]
    public void Legacy_identity_key_matches_what_0_7_2_59_saved_for_hidden_rows()
    {
        var driver = WuaHistory(IntelTitle, IdA, "2026-11-20 10:00:00", "Mislykket", "0x80070103");
        var software = WuaHistory(DotNetTitle, IdB, "2026-11-20 10:00:00", "Mislykket", "0x80070643");

        Assert.Equal(
            "INTEL - SYSTEM - 10/3/2016 12:00:00 AM - 10.1.1.38||10.1.1.38|MISLYKKET|0X80070103",
            HistoryArchive.LegacyDisplayIdentityKey(driver));
        Assert.Equal(
            DotNetTitle.ToUpperInvariant() + "|KB5066747||MISLYKKET|0X80070643",
            HistoryArchive.LegacyDisplayIdentityKey(software));
        // Rows that were never reshaped keep the key they were hidden under.
        Assert.Equal(HistoryArchive.LegacyDisplayIdentityKey(software), HistoryArchive.DisplayIdentityKey(software));
    }

    private static readonly string[] NoEvents = Array.Empty<string>();
    private static readonly HistoryArchive.HiddenDisplayIdentity[] NoRules = Array.Empty<HistoryArchive.HiddenDisplayIdentity>();

    [Fact]
    public void Hiding_a_row_also_hides_its_twin_recorded_under_another_title()
    {
        // The category install helper queues "KB — title", so the archived row and the
        // Windows Update row of one failed install share the UpdateId but not the title.
        var live = WuaHistory(DotNetTitle, IdB, "2026-11-25 14:11:02", "Mislykket", "0x80070643");
        var archived = Archived("Windows Update", "KB5066747 — " + DotNetTitle, IdB, "2026-11-25 14:14:04", "Failed 0x80070643");
        var rows = new[] { live, archived };

        var shown = Assert.Single(HistoryArchive.CollapseVisible(rows, NoEvents, NoRules));
        Assert.Same(archived, shown);

        // The archive holds only its own row; the Windows Update row is read live.
        var (rule, eventKeys) = HistoryArchive.BuildHide(shown, new[] { archived });

        Assert.Empty(HistoryArchive.CollapseVisible(rows, eventKeys, new[] { rule }));
    }

    [Fact]
    public void Hiding_the_Windows_Update_row_also_hides_the_archived_twin()
    {
        var live = WuaHistory(DotNetTitle, IdB, "2026-11-25 14:11:02", "Mislykket", "0x80070643");
        var archived = Archived("Windows Update", "KB5066747 — " + DotNetTitle, IdB, "2026-11-25 14:10:40", "Failed 0x80070643");

        var (rule, eventKeys) = HistoryArchive.BuildHide(live, new[] { archived });

        Assert.Contains(HistoryArchive.EventKey(archived), eventKeys);
        Assert.Empty(HistoryArchive.CollapseVisible(new[] { live, archived }, NoEvents, new[] { rule }));
    }

    [Fact]
    public void A_later_attempt_of_a_hidden_update_is_shown_again()
    {
        var live = WuaHistory(DotNetTitle, IdB, "2026-11-25 14:11:02", "Mislykket", "0x80070643");
        var archived = Archived("Windows Update", "KB5066747 — " + DotNetTitle, IdB, "2026-11-25 14:14:04", "Failed 0x80070643");
        var (rule, eventKeys) = HistoryArchive.BuildHide(archived, new[] { archived });
        var retry = WuaHistory(DotNetTitle, IdB, "2026-11-27 09:00:00", "Mislykket", "0x80070643");

        var visible = HistoryArchive.CollapseVisible(new[] { live, archived, retry }, eventKeys, new[] { rule });

        Assert.Same(retry, Assert.Single(visible));
    }

    [Fact]
    public void Hiding_one_driver_leaves_the_other_with_the_same_title_and_version()
    {
        var older = WuaHistory(IntelTitle, IdB, "2026-11-25 14:13:20", "Mislykket", "0x80070103");
        var hiddenRow = WuaHistory(IntelTitle, IdA, "2026-11-25 14:13:28", "Mislykket", "0x80070103");
        var rows = new[] { older, hiddenRow };

        var (rule, eventKeys) = HistoryArchive.BuildHide(hiddenRow, Array.Empty<UpdateEntry>());
        var visible = HistoryArchive.CollapseVisible(rows, eventKeys, new[] { rule });

        Assert.Same(older, Assert.Single(visible));
        Assert.False(OutcomeIdentity.SameUpdate(older, hiddenRow));
    }

    [Fact]
    public void Rows_hidden_by_0_7_2_59_stay_hidden()
    {
        var driver = WuaHistory(IntelTitle, IdA, "2026-11-20 10:00:00", "Mislykket", "0x80070103");
        var saved = new HistoryArchive.HiddenDisplayIdentity
        {
            Identity = "INTEL - SYSTEM - 10/3/2016 12:00:00 AM - 10.1.1.38||10.1.1.38|MISLYKKET|0X80070103",
            MaxDate = "2026-11-20 10:00:00"
        };

        Assert.Empty(HistoryArchive.CollapseVisible(new[] { driver }, NoEvents, new[] { saved }));
    }
}
