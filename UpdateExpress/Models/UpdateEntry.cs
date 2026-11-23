namespace UpdateExpress.Models;
public sealed class UpdateEntry
{
    public bool IsSelected { get; set; } = false;
    public string UpdateId { get; set; } = "";
    public string Category { get; set; } = "";
    public bool SafeDefault { get; set; } = false;
    public bool DriverAssigned { get; set; } = false;
    public string DriverClassification { get; set; } = "";
    public string KB { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>Raw severity/classification (Critical, Important, Moderate, Low, Unrated, Unknown, …).</summary>
    public string Priority { get; set; } = "Unknown";
    public string PriorityExplanation { get; set; } = "No severity information was recorded for this entry.";
    /// <summary>Colored Priority column: Critical / Important / Moderate / Optional / Unrated / Unknown.</summary>
    public string PriorityDisplay
    {
        get { var g = PriorityGrade.ForUpdate(this); return g.Display; }
    }
    public string PriorityColorHex
    {
        get { var g = PriorityGrade.ForUpdate(this); return g.ColorHex; }
    }
    // WUA IWindowsDriverUpdate metadata is preferred; title parsing is only a fallback.
    public string DriverVersionFromWua { get; set; } = "";
    public string DriverHardwareId { get; set; } = "";
    public string DriverModel { get; set; } = "";
    public string DriverManufacturer { get; set; } = "";
    public string DriverClass { get; set; } = "";
    public string DriverReason { get; set; } = "";
    public string DriverStatus { get; set; } = "";
    public string InstalledDriverVersion { get; set; } = "";
    /// <summary>Trailing driver-style version in titles: "… - 6.0.9159.1" or "(6.0.9159.1)".</summary>
    private static readonly System.Text.RegularExpressions.Regex TrailingVersion = new(
        @"(?:\s+-\s+|\s*\()(?<version>\d+(?:\.\d+){1,7})\)?\s*$",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    public string DriverVersion
    {
        get
        {
            if (Category != "Driver / firmware") return "";
            if (!string.IsNullOrWhiteSpace(DriverVersionFromWua)) return DriverVersionFromWua;
            var match = TrailingVersion.Match(Title);
            return match.Success ? match.Groups["version"].Value : "—";
        }
    }
    // Only extract an explicitly labelled software version; never infer from a KB or build number.
    private static readonly System.Text.RegularExpressions.Regex SoftwareVersionLabel = new(
        @"\s*\(\s*(?:versjon|version)\s+(?<version>\d+(?:\.\d+){1,7})\s*\)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    public string SoftwareVersion
    {
        get
        {
            if (Category == "Driver / firmware") return "";
            var match = SoftwareVersionLabel.Match(Title);
            return match.Success ? match.Groups["version"].Value : "";
        }
    }
    /// <summary>Optional Version column override (e.g. WinGet installed → available).</summary>
    public string VersionDisplay { get; set; } = "";
    /// <summary>
    /// Version column value. History rows often lack Category / WUA driver metadata;
    /// when Version is empty, fill from a clear trailing <c>x.y.z.w</c> in the title.
    /// </summary>
    public string UnifiedVersion
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(VersionDisplay)) return VersionDisplay;
            if (Category == "Driver / firmware")
            {
                string driver = DriverVersion;
                return driver == "—" ? "" : driver;
            }
            if (!string.IsNullOrWhiteSpace(SoftwareVersion)) return SoftwareVersion;
            var trailing = TrailingVersion.Match(Title ?? "");
            return trailing.Success ? trailing.Groups["version"].Value : "";
        }
    }
    public string DisplayTitle
    {
        get
        {
            if (Category == "Application") return Title;
            if (Category != "Driver / firmware")
                return string.IsNullOrEmpty(SoftwareVersion) ? Title : SoftwareVersionLabel.Replace(Title, "").Trim();

            // Strip trailing WUA date noise, keep a clean base name, then append version so
            // near-identical driver titles stay distinguishable in the Update column.
            string baseTitle = System.Text.RegularExpressions.Regex.Replace(
                TrailingVersion.Replace(Title, ""),
                @"\s+-\s+\d{1,2}/\d{1,2}/\d{4}\s+\d{1,2}:\d{2}:\d{2}\s+[AP]M\s*$", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            string version = UnifiedVersion;
            if (string.IsNullOrWhiteSpace(version) || version == "—")
                return baseTitle;
            if (baseTitle.Contains(version, StringComparison.OrdinalIgnoreCase))
                return baseTitle;
            return $"{baseTitle}  ·  {version}";
        }
    }
    public bool IsHistoryFailed => Status is "Mislykket" or "Avbrutt" or "Failed" or "Cancelled" or "Feilet / avbrutt";
    public bool IsHistorySuccessful => Status is "Vellykket" or "Successful" or "Installert";
    public bool IsHistoryPartial => Status is "Vellykket med feil" or "Delvis fullført" or "Partial";
    /// <summary>Historical failure kept for display after a later success or a completed fix scan.</summary>
    public bool IsHistoryFixed => Status == "Fixed";
    /// <summary>Why a history row was marked Fixed. Empty for ordinary rows.</summary>
    public string StatusNote { get; set; } = "";
    /// <summary>Failed entry with an HRESULT — Status cell pulses ErrorCode / Fix it.</summary>
    public bool HasFixableErrorCode => IsHistoryFailed && !string.IsNullOrWhiteSpace(ErrorCode);
    // Keep the original HRESULT in ErrorCode for logging and export. In the grid,
    // display the HRESULT instead of a failure label, never beside a success label.
    public string DisplayStatus => IsHistoryFixed
        ? "Fixed"
        : HasFixableErrorCode
        ? ErrorCode
        // Pending driver rows (WUA + OEM catalog) prefer DriverStatus. History rows have a
        // Date and use Status. OEM release times live in DateDisplay only so they stay pending.
        : Category == "Driver / firmware" && !string.IsNullOrWhiteSpace(DriverStatus) &&
          (string.IsNullOrWhiteSpace(Date) || OpensExternalUrl)
            ? DriverStatus : Status;
    public string StatusDetails
    {
        get
        {
            string core = string.IsNullOrWhiteSpace(ErrorCode) ? Status : $"{Status}\nError code: {ErrorCode}";
            return string.IsNullOrWhiteSpace(StatusNote) ? core : StatusNote + "\n" + core;
        }
    }
    public string Status { get; set; } = "";
    /// <summary>Canonical timestamp (yyyy-MM-dd HH:mm:ss) for sort, EventKey, archive, and reconcile.</summary>
    public string Date { get; set; } = "";
    /// <summary>Settings-formatted Date/Time for History (and similar) grids. Empty falls back to <see cref="Date"/>.</summary>
    public string DateDisplay { get; set; } = "";
    /// <summary>Value shown in Date/Time cells; prefers Settings formatting when set.</summary>
    public string DateShown => string.IsNullOrWhiteSpace(DateDisplay) ? Date : DateDisplay;
    public string Source { get; set; } = "";
    public string ErrorCode { get; set; } = "";
    /// <summary>Settings feature update. The row opens Windows Update instead of installing through WUA.</summary>
    public bool OpensWindowsUpdate { get; set; }
    /// <summary>OEM package download URL (exe/zip/…). Double-click / Open OEM download opens this.</summary>
    public string ExternalUrl { get; set; } = "";
    public bool OpensExternalUrl => !string.IsNullOrWhiteSpace(ExternalUrl);
    /// <summary>Optional OEM TXT / README companion (Lenovo). Separate from the installer package.</summary>
    public string ReadmeUrl { get; set; } = "";
    public bool OpensExternalReadme => !string.IsNullOrWhiteSpace(ReadmeUrl);

    internal UpdateEntry Clone() => new()
    {
        IsSelected = IsSelected,
        UpdateId = UpdateId,
        Category = Category,
        SafeDefault = SafeDefault,
        DriverAssigned = DriverAssigned,
        DriverClassification = DriverClassification,
        KB = KB,
        Title = Title,
        Priority = Priority,
        PriorityExplanation = PriorityExplanation,
        DriverVersionFromWua = DriverVersionFromWua,
        DriverHardwareId = DriverHardwareId,
        DriverModel = DriverModel,
        DriverManufacturer = DriverManufacturer,
        DriverClass = DriverClass,
        DriverReason = DriverReason,
        DriverStatus = DriverStatus,
        InstalledDriverVersion = InstalledDriverVersion,
        Status = Status,
        StatusNote = StatusNote,
        Date = Date,
        DateDisplay = DateDisplay,
        VersionDisplay = VersionDisplay,
        Source = Source,
        ErrorCode = ErrorCode,
        OpensWindowsUpdate = OpensWindowsUpdate,
        ExternalUrl = ExternalUrl,
        ReadmeUrl = ReadmeUrl
    };
}
