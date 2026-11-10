using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using UpdateExpress.Models;

namespace UpdateExpress.Services;

/// <summary>
/// Feature updates such as Windows 11 26H2 are offered in Settings, not by the
/// Windows Update Agent search Update Express uses. The compatibility assessment
/// under TargetVersionUpgradeExperienceIndicators is the supported signal for the
/// next version that has no safeguard hold.
/// </summary>
internal static class FeatureUpdateOfferReader
{
    internal const string Source = "Windows Update-innstillinger";

    private const string CurrentVersionPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string IndicatorsPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\TargetVersionUpgradeExperienceIndicators";
    private static readonly Regex ReleaseName = new(@"(?<version>\d{2}H[12])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static UpdateEntry? Read()
    {
        var chosen = Choose(out _, out _, out _);
        return chosen is null ? null : ToEntry(chosen);
    }

    internal static IReadOnlyList<string> DiagnosticLines()
    {
        var lines = new List<string>();
        var chosen = Choose(out int currentBuild, out string displayVersion, out var considered);
        lines.Add($"Installed: {displayVersion} · build {currentBuild}.");
        if (considered.Count == 0)
            lines.Add("No feature-update assessment keys were found.");
        foreach (var item in considered)
            lines.Add($"  {item.Version}: dest build {item.DestBuild}, UpgEx={item.UpgEx}, eligible={item.Eligible}. {item.Reason}");
        if (chosen is null)
            lines.Add("No newer feature update without a safeguard hold. This does not query Windows Settings.");
        else
            lines.Add($"Shown in Windows Updates: {chosen.Version} (build {chosen.DestBuild}). Not a WUA package. Install from Windows Update in Settings.");
        lines.Add("WUA COM does not return this Settings offer. Include optional only adds BrowseOnly updates.");
        return lines;
    }

    internal static void OpenWindowsUpdate()
    {
        Process.Start(new ProcessStartInfo("ms-settings:windowsupdate") { UseShellExecute = true });
    }

    private static Assessment? Choose(out int currentBuild, out string displayVersion, out List<Assessment> considered)
    {
        considered = new List<Assessment>();
        currentBuild = 0;
        displayVersion = "Unknown";
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var current = hive.OpenSubKey(CurrentVersionPath);
            displayVersion = current?.GetValue("DisplayVersion")?.ToString()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(displayVersion))
                displayVersion = current?.GetValue("ReleaseId")?.ToString()?.Trim() ?? "Unknown";
            currentBuild = ReadInt(current?.GetValue("CurrentBuild"));
            if (currentBuild <= 0) currentBuild = Environment.OSVersion.Version.Build;

            using var indicators = hive.OpenSubKey(IndicatorsPath);
            if (indicators is null) return null;
            foreach (var name in indicators.GetSubKeyNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                if (name.Equals("UNV", StringComparison.OrdinalIgnoreCase)) continue;
                using var key = indicators.OpenSubKey(name);
                if (key is null) continue;
                var versionMatch = ReleaseName.Match(name);
                if (!versionMatch.Success) continue;
                string version = versionMatch.Groups["version"].Value.ToUpperInvariant();
                int destBuild = ReadInt(key.GetValue("DestBuildNum"));
                string upgEx = key.GetValue("UpgEx")?.ToString()?.Trim() ?? "";
                int gStatus = ReadInt(key.GetValue("GStatus"));
                bool blocked = !IsNone(key.GetValue("RedReason")) || !IsNone(key.GetValue("FailedPrereqs")) || !IsNone(key.GetValue("GatedBlockId"));
                bool expired = IsExpired(key.GetValue("DataExpDateEpoch"));
                string reason;
                bool eligible;
                if (destBuild <= currentBuild)
                {
                    eligible = false;
                    reason = "Not newer than the installed build.";
                }
                else if (expired)
                {
                    eligible = false;
                    reason = "Assessment data has expired.";
                }
                else if (!upgEx.Equals("Green", StringComparison.OrdinalIgnoreCase) || gStatus != 2 || blocked)
                {
                    eligible = false;
                    reason = "Not a clear offer. A safeguard hold, failed prerequisite, or non-green result is not shown as installable.";
                }
                else
                {
                    eligible = true;
                    reason = "No safeguard hold.";
                }
                considered.Add(new Assessment(version, destBuild, upgEx, eligible, reason, displayVersion, currentBuild));
            }
        }
        catch (Exception ex)
        {
            considered.Add(new Assessment("—", 0, "", false, ex.Message, displayVersion, currentBuild));
            return null;
        }

        return considered.Where(item => item.Eligible).OrderBy(item => item.DestBuild).ThenBy(item => item.Version).FirstOrDefault();
    }

    private static UpdateEntry ToEntry(Assessment offer)
    {
        string product = offer.CurrentBuild >= 22000 ? "Windows 11" : "Windows 10";
        return new UpdateEntry
        {
            Title = $"{product}, {offer.Version}",
            Category = "Versjonsoppgradering",
            Status = "Open Windows Update to install",
            OpensWindowsUpdate = true,
            Source = Source,
            Priority = "Unrated",
            PriorityExplanation =
                $"Installed version is {offer.DisplayVersion} (build {offer.CurrentBuild}). " +
                $"{product}, version {offer.Version} (build {offer.DestBuild}) has no safeguard hold. " +
                "Windows Update Agent does not return this offer, so Update Express cannot download or install it. Open Windows Update in Settings.",
            StatusNote =
                "Open Windows Update to install this feature update. " +
                "Update Express cannot download or install it. Include optional does not apply.",
            SafeDefault = false,
            IsSelected = false
        };
    }

    private static int ReadInt(object? value)
    {
        if (value is int number) return number;
        if (value is long wide) return wide > int.MaxValue ? int.MaxValue : (int)wide;
        return int.TryParse(Convert.ToString(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static bool IsExpired(object? epochValue)
    {
        long epoch = epochValue is long wide ? wide : ReadInt(epochValue);
        if (epoch <= 0) return false;
        try { return DateTimeOffset.FromUnixTimeSeconds(epoch) < DateTimeOffset.Now; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private static bool IsNone(object? value)
    {
        if (value is null) return true;
        if (value is string[] items)
            return items.All(item => IsNoneText(item));
        if (value is Array array)
        {
            foreach (var item in array)
                if (!IsNoneText(Convert.ToString(item))) return false;
            return true;
        }
        return IsNoneText(Convert.ToString(value));
    }

    private static bool IsNoneText(string? text) =>
        string.IsNullOrWhiteSpace(text) ||
        text.Equals("None", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("{None}", StringComparison.OrdinalIgnoreCase);

    private sealed record Assessment(string Version, int DestBuild, string UpgEx, bool Eligible, string Reason, string DisplayVersion, int CurrentBuild);
}
