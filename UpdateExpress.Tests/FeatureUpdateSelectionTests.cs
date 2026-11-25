using UpdateExpress.Models;
using UpdateExpress.Services;
using Xunit;

namespace UpdateExpress.Tests;

public class FeatureUpdateSelectionTests
{
    private const string CandidateSource = "Windows Update-innstillinger";

    [Fact]
    public void KeepNewest_On23H2_With24H2CandidateAnd25H2Wua_KeepsOnly25H2()
    {
        var entries = new List<UpdateEntry>
        {
            Candidate("Windows 11, 24H2", "24H2"),
            WuaFeature("Windows 11, version 25H2", "KB5129195", "uid-25h2"),
            Cumulative("Windows-verktøy for fjerning av skadelig programvare x64 - v5.147 (KB890830)", "KB890830")
        };

        var result = FeatureUpdateSelection.KeepNewest(entries, "23H2", 22631);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, e => e.Title.Contains("25H2", StringComparison.OrdinalIgnoreCase) && e.KB == "KB5129195");
        Assert.DoesNotContain(result, e => e.Title.Contains("24H2", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result, e => e.KB == "KB890830");
    }

    [Fact]
    public void KeepNewest_SingleFeatureOffer_Unchanged()
    {
        var entries = new List<UpdateEntry>
        {
            WuaFeature("Feature update to Windows 11, version 24H2", "KB5033375", "uid-24h2"),
            Cumulative("2024-11 Cumulative Update for Windows 11 Version 23H2 for x64-based Systems (KB5046617)", "KB5046617")
        };

        var result = FeatureUpdateSelection.KeepNewest(entries, "23H2", 22631);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, e => e.UpdateId == "uid-24h2");
        Assert.Contains(result, e => e.KB == "KB5046617");
    }

    [Fact]
    public void KeepNewest_NewestIsUnverifiedCandidate_LowerVerifiedWuaKept_CandidateDropped()
    {
        // Newest row is only a registry candidate; a lower verified WUA offer must stay visible.
        var entries = new List<UpdateEntry>
        {
            Candidate("Windows 11, 25H2", "25H2"),
            WuaFeature("Feature update to Windows 11, version 24H2", "KB5033375", "uid-24h2")
        };

        var result = FeatureUpdateSelection.KeepNewest(entries, "23H2", 22631);

        Assert.Single(result);
        Assert.Equal("uid-24h2", result[0].UpdateId);
        Assert.DoesNotContain(result, e => FeatureUpdateSelection.IsRegistryCandidate(e));
    }

    [Fact]
    public void KeepNewest_Windows10ToWindows11_OfferNotCollapsedWithWin10Feature()
    {
        var entries = new List<UpdateEntry>
        {
            WuaFeature("Feature update to Windows 11, version 24H2", "KB5033375", "uid-win11"),
            WuaFeature("Feature update to Windows 10, version 22H2", "KB5017380", "uid-win10"),
            Cumulative("2024-11 Cumulative Update for Windows 10 Version 22H2 for x64-based Systems (KB5046613)", "KB5046613")
        };

        // Installed Windows 10 21H2 — Win10→22H2 and Win10→Win11 are different product lanes.
        var result = FeatureUpdateSelection.KeepNewest(entries, "21H2", 19044);

        Assert.Equal(3, result.Count);
        Assert.Contains(result, e => e.UpdateId == "uid-win11");
        Assert.Contains(result, e => e.UpdateId == "uid-win10");
        Assert.Contains(result, e => e.KB == "KB5046613");
    }

    [Fact]
    public void KeepNewest_OnlyCandidates_KeepsHighest()
    {
        var entries = new List<UpdateEntry>
        {
            Candidate("Windows 11, 24H2", "24H2"),
            Candidate("Windows 11, 25H2", "25H2")
        };

        var result = FeatureUpdateSelection.KeepNewest(entries, "23H2", 22631);

        Assert.Single(result);
        Assert.Contains("25H2", result[0].Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KeepNewest_SameVersionCumulative_NotTreatedAsFeature()
    {
        var entries = new List<UpdateEntry>
        {
            Cumulative("2024-11 Cumulative Update for Windows 11 Version 23H2 for x64-based Systems (KB5046617)", "KB5046617"),
            WuaFeature("Windows 11, version 25H2", "KB5129195", "uid-25h2")
        };

        var result = FeatureUpdateSelection.KeepNewest(entries, "23H2", 22631);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, e => e.KB == "KB5046617" && e.Category == "Programvare / sikkerhet");
    }

    [Fact]
    public void RankRelease_ParsesH1AndH2()
    {
        Assert.Equal(231, FeatureUpdateSelection.RankRelease("23H1"));
        Assert.Equal(232, FeatureUpdateSelection.RankRelease("Windows 11 Pro 23H2"));
        Assert.Equal(252, FeatureUpdateSelection.RankRelease("Windows 11, version 25H2"));
        Assert.True(FeatureUpdateSelection.RankRelease("25H2") > FeatureUpdateSelection.RankRelease("24H2"));
    }

    private static UpdateEntry Candidate(string title, string version) => new()
    {
        Title = title,
        Category = "Versjonsoppgradering",
        Status = "Open Windows Update to install",
        OpensWindowsUpdate = true,
        Source = CandidateSource,
        SafeDefault = false,
        IsSelected = false
    };

    private static UpdateEntry WuaFeature(string title, string kb, string updateId) => new()
    {
        Title = title,
        Category = "Versjonsoppgradering",
        KB = kb,
        UpdateId = updateId,
        Source = "WUA / Software",
        Status = "Tilgjengelig",
        SafeDefault = false,
        IsSelected = false
    };

    private static UpdateEntry Cumulative(string title, string kb) => new()
    {
        Title = title,
        Category = "Programvare / sikkerhet",
        KB = kb,
        UpdateId = "uid-" + kb,
        Source = "WUA / Software",
        Status = "Tilgjengelig",
        SafeDefault = true,
        IsSelected = false
    };
}
