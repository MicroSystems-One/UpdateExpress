using System;
using System.Collections.Generic;

namespace UpdateExpress.Services;

internal sealed class WindowsUpdateErrorInfo
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Guidance { get; init; } = "";
    public WindowsUpdateFixLevel? RecommendedFix { get; init; }
    public string? LearnMoreUrl { get; init; }
    public bool IsKnown { get; init; }
}

/// <summary>
/// Curated catalog of common Windows Update HRESULTs mapped to Update Express fix levels.
/// Unknown codes get generic guidance — never invent a specific root cause.
/// </summary>
internal static class WindowsUpdateErrorCatalog
{
    private const string RefCommon =
        "https://learn.microsoft.com/en-us/troubleshoot/windows-client/installing-updates-features-roles/common-windows-update-errors";
    private const string RefCodes =
        "https://learn.microsoft.com/en-us/windows/deployment/update/windows-update-error-reference";
    private const string RefSupport =
        "https://support.microsoft.com/windows/troubleshoot-problems-updating-windows";

    private static readonly Dictionary<string, WindowsUpdateErrorInfo> Known =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["0x80240034"] = new()
            {
                Code = "0x80240034",
                Name = "WU_E_DOWNLOAD_FAILED",
                Summary = "The update failed to download (incomplete download or connection issue).",
                Guidance = "Reset the Windows Update download cache, then try again.",
                RecommendedFix = WindowsUpdateFixLevel.Quick,
                LearnMoreUrl = RefSupport,
                IsKnown = true
            },
            ["0x8024001F"] = new()
            {
                Code = "0x8024001F",
                Name = "WU_E_NO_CONNECTION",
                Summary = "Windows Update could not finish because the network connection was unavailable.",
                Guidance = "Verify internet/proxy connectivity, then run a Quick fix if the network is OK.",
                RecommendedFix = WindowsUpdateFixLevel.Quick,
                LearnMoreUrl = RefCodes,
                IsKnown = true
            },
            ["0x8024402C"] = new()
            {
                Code = "0x8024402C",
                Name = "WU_E_PT_WINHTTP_NAME_NOT_RESOLVED",
                Summary = "The proxy or update server name could not be resolved (DNS/proxy/WSUS).",
                Guidance = "Check DNS, proxy, and WSUS reachability. Then reset Windows Update components.",
                RecommendedFix = WindowsUpdateFixLevel.Quick,
                LearnMoreUrl = "https://learn.microsoft.com/en-us/troubleshoot/windows-client/installing-updates-features-roles/error-0x8024402c-installing-update-feature-on-demand",
                IsKnown = true
            },
            ["0x80072EE2"] = new()
            {
                Code = "0x80072EE2",
                Name = "WININET_TIMEOUT",
                Summary = "A timeout occurred while contacting the Windows Update server.",
                Guidance = "Check firewall/proxy and network stability, then reset Windows Update components.",
                RecommendedFix = WindowsUpdateFixLevel.Quick,
                LearnMoreUrl = RefSupport,
                IsKnown = true
            },
            ["0x80240016"] = new()
            {
                Code = "0x80240016",
                Name = "WU_E_INSTALL_NOT_ALLOWED",
                Summary = "Install was blocked because another install is running or a reboot is pending.",
                Guidance = "Finish any pending installs, restart Windows, then check for updates again. No cache reset required first.",
                RecommendedFix = null,
                LearnMoreUrl = RefCodes,
                IsKnown = true
            },
            ["0x80240020"] = new()
            {
                Code = "0x80240020",
                Name = "WU_E_NO_INTERACTIVE_USER",
                Summary = "The operation needs a signed-in interactive user.",
                Guidance = "Sign in locally, keep the session unlocked, and retry the update.",
                RecommendedFix = null,
                LearnMoreUrl = RefCommon,
                IsKnown = true
            },
            ["0x80070005"] = new()
            {
                Code = "0x80070005",
                Name = "E_ACCESSDENIED",
                Summary = "Access was denied to a file or registry key during servicing.",
                Guidance = "Run Update Express elevated. If it persists, review CBS.log permissions and run Medium/Deep repair.",
                RecommendedFix = WindowsUpdateFixLevel.Medium,
                LearnMoreUrl = RefCommon,
                IsKnown = true
            },
            ["0x80070002"] = new()
            {
                Code = "0x80070002",
                Name = "ERROR_FILE_NOT_FOUND",
                Summary = "A required update file could not be found.",
                Guidance = "Reset the update cache (Quick). If it continues, repair the component store (Deep).",
                RecommendedFix = WindowsUpdateFixLevel.Quick,
                LearnMoreUrl = RefSupport,
                IsKnown = true
            },
            ["0x8007000D"] = new()
            {
                Code = "0x8007000D",
                Name = "ERROR_INVALID_DATA",
                Summary = "Invalid or corrupted update cache/data.",
                Guidance = "Reset SoftwareDistribution/catroot2 (Quick), then retry.",
                RecommendedFix = WindowsUpdateFixLevel.Quick,
                LearnMoreUrl = RefSupport,
                IsKnown = true
            },
            ["0x80248014"] = new()
            {
                Code = "0x80248014",
                Name = "WU datastore / service issue",
                Summary = "Windows Update database or related service is not healthy.",
                Guidance = "Restart update services and reset the update cache (Quick).",
                RecommendedFix = WindowsUpdateFixLevel.Quick,
                LearnMoreUrl = RefSupport,
                IsKnown = true
            },
            ["0x80070422"] = new()
            {
                Code = "0x80070422",
                Name = "ERROR_SERVICE_DISABLED",
                Summary = "A required service (often Windows Update) is disabled or cannot start.",
                Guidance = "Enable and start wuauserv/BITS/cryptsvc, then run Quick fix.",
                RecommendedFix = WindowsUpdateFixLevel.Quick,
                LearnMoreUrl = RefSupport,
                IsKnown = true
            },
            ["0x80073712"] = new()
            {
                Code = "0x80073712",
                Name = "ERROR_SXS_COMPONENT_STORE_CORRUPT",
                Summary = "The Windows component store is corrupted.",
                Guidance = "Run DISM RestoreHealth and SFC (Deep fix).",
                RecommendedFix = WindowsUpdateFixLevel.Deep,
                LearnMoreUrl = "https://learn.microsoft.com/en-us/troubleshoot/windows-server/installing-updates-features-roles/fix-windows-update-errors",
                IsKnown = true
            },
            ["0x800F0831"] = new()
            {
                Code = "0x800F0831",
                Name = "CBS_E_STORE_CORRUPTION",
                Summary = "The CBS component store is corrupted.",
                Guidance = "Run DISM RestoreHealth and SFC (Deep fix).",
                RecommendedFix = WindowsUpdateFixLevel.Deep,
                LearnMoreUrl = "https://learn.microsoft.com/en-us/troubleshoot/windows-server/installing-updates-features-roles/fix-windows-update-errors",
                IsKnown = true
            },
            ["0x800F0922"] = new()
            {
                Code = "0x800F0922",
                Name = "CBS install failure",
                Summary = "Often linked to disk space, VPN, or servicing stack issues during install.",
                Guidance = "Free disk space, disconnect VPN if used, then run Medium/Deep repair.",
                RecommendedFix = WindowsUpdateFixLevel.Medium,
                LearnMoreUrl = RefSupport,
                IsKnown = true
            },
            ["0x80070020"] = new()
            {
                Code = "0x80070020",
                Name = "ERROR_SHARING_VIOLATION",
                Summary = "A file needed by the installer is locked (antivirus/backup/other process).",
                Guidance = "Temporarily pause real-time antivirus/backup scans, close locking apps, retry. Then Quick if still failing.",
                RecommendedFix = WindowsUpdateFixLevel.Quick,
                LearnMoreUrl = RefCodes,
                IsKnown = true
            },
            ["0x80070057"] = new()
            {
                Code = "0x80070057",
                Name = "ERROR_INVALID_PARAMETER",
                Summary = "Invalid parameter — sometimes proxy settings or corrupted update metadata.",
                Guidance = "Verify proxy configuration, then run Quick fix.",
                RecommendedFix = WindowsUpdateFixLevel.Quick,
                LearnMoreUrl = RefSupport,
                IsKnown = true
            },
            ["0x80240022"] = new()
            {
                Code = "0x80240022",
                Name = "WU_E_ALL_UPDATES_FAILED",
                Summary = "All selected updates failed (often cache, permissions, or AV interference).",
                Guidance = "Run Quick fix; escalate to Medium/Deep if failures continue.",
                RecommendedFix = WindowsUpdateFixLevel.Quick,
                LearnMoreUrl = RefCodes,
                IsKnown = true
            },
            ["0x80240044"] = new()
            {
                Code = "0x80240044",
                Name = "WU_E_AU_NOSERVICE / access",
                Summary = "Windows Update could not complete — often missing admin rights for download/install.",
                Guidance = "Restart Update Express as administrator and retry.",
                RecommendedFix = null,
                LearnMoreUrl = RefCodes,
                IsKnown = true
            },
            ["0x800705B4"] = new()
            {
                Code = "0x800705B4",
                Name = "ERROR_TIMEOUT",
                Summary = "The update took too long or was interrupted.",
                Guidance = "Keep the PC awake, retry once, then Quick fix if it repeats.",
                RecommendedFix = WindowsUpdateFixLevel.Quick,
                LearnMoreUrl = RefSupport,
                IsKnown = true
            },
            ["0xC1900101"] = new()
            {
                Code = "0xC1900101",
                Name = "Feature update driver rollback",
                Summary = "Feature update failed — commonly an incompatible driver.",
                Guidance = "Update or remove problematic drivers, free disk space, then retry. Deep repair may help component store issues but will not fix a bad driver by itself.",
                RecommendedFix = WindowsUpdateFixLevel.Medium,
                LearnMoreUrl = RefSupport,
                IsKnown = true
            },
            ["0x80131509"] = new()
            {
                Code = "0x80131509",
                Name = "COR_E_INVALIDOPERATION (managed guard)",
                Summary = "Update Express blocked or aborted the operation before Windows Update returned a result (for example missing hardware ID, interactive driver blocked, or identity recheck failed).",
                Guidance = "This is usually not a Windows Update download/install code. Open %ProgramData%\\UpdateExpress\\driver-recheck.log, or retry after a fresh scan. If a WUA HRESULT appears in the log line, treat that code instead.",
                RecommendedFix = null,
                LearnMoreUrl = RefCodes,
                IsKnown = true
            },
        };

    internal static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string s = raw.Trim();
        // Accept "0x80240034" or "80240034"
        if (!s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            System.Text.RegularExpressions.Regex.IsMatch(s, @"^[0-9A-Fa-f]{8}$"))
            s = "0x" + s.ToUpperInvariant();
        else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && s.Length > 2)
            s = "0x" + s[2..].ToUpperInvariant();
        return s;
    }

    internal static WindowsUpdateErrorInfo Lookup(string? rawCode)
    {
        string code = Normalize(rawCode);
        if (string.IsNullOrEmpty(code))
        {
            return new WindowsUpdateErrorInfo
            {
                Code = "",
                Name = "No error code",
                Summary = "No Windows Update HRESULT was recorded for this entry.",
                Guidance = "Open Windows Update fix and choose Quick / Medium / Deep based on symptoms.",
                RecommendedFix = WindowsUpdateFixLevel.Quick,
                LearnMoreUrl = RefSupport,
                IsKnown = false
            };
        }

        if (Known.TryGetValue(code, out var info))
            return info;

        return new WindowsUpdateErrorInfo
        {
            Code = code,
            Name = "Unknown / uncommon code",
            Summary = "This HRESULT is not in the Update Express curated catalog. Meaning may still be valid in Microsoft documentation.",
            Guidance = "Try Quick fix first. If the component store may be corrupt, use Medium or Deep. Check Microsoft Learn for this exact code.",
            RecommendedFix = WindowsUpdateFixLevel.Quick,
            LearnMoreUrl = RefCodes,
            IsKnown = false
        };
    }
}
