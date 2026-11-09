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
    /// <summary>Colored Priority column: Critical (K1) … Unknown (K5).</summary>
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
    public string UnifiedVersion => Category == "Driver / firmware" ? DriverVersion : SoftwareVersion;
    public string DisplayTitle => Category == "Driver / firmware"
        ? System.Text.RegularExpressions.Regex.Replace(
            TrailingVersion.Replace(Title, ""),
            @"\s+-\s+\d{1,2}/\d{1,2}/\d{4}\s+\d{1,2}:\d{2}:\d{2}\s+[AP]M\s*$", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim()
        : (string.IsNullOrEmpty(SoftwareVersion) ? Title : SoftwareVersionLabel.Replace(Title, "").Trim());
    public bool IsHistoryFailed => Status is "Mislykket" or "Avbrutt" or "Failed" or "Cancelled" or "Feilet / avbrutt";
    public bool IsHistorySuccessful => Status is "Vellykket" or "Successful" or "Installert";
    public bool IsHistoryPartial => Status is "Vellykket med feil" or "Delvis fullført" or "Partial";
    /// <summary>Failed entry with an HRESULT — Status cell pulses ErrorCode / Fix it.</summary>
    public bool HasFixableErrorCode => IsHistoryFailed && !string.IsNullOrWhiteSpace(ErrorCode);
    // Keep the original HRESULT in ErrorCode for logging and export. In the grid,
    // display the HRESULT instead of a failure label, never beside a success label.
    public string DisplayStatus => HasFixableErrorCode
        ? ErrorCode
        : Category == "Driver / firmware" && !string.IsNullOrWhiteSpace(DriverStatus) && string.IsNullOrWhiteSpace(Date)
            ? DriverStatus : Status;
    public string StatusDetails => string.IsNullOrWhiteSpace(ErrorCode)
        ? Status
        : $"{Status}\nError code: {ErrorCode}";
    public string Status { get; set; } = "";
    public string Date { get; set; } = "";
    public string Source { get; set; } = "";
    public string ErrorCode { get; set; } = "";
}
