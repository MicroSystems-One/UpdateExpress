using UpdateExpress.Models;

namespace UpdateExpress.Services.OemDrivers;

/// <summary>
/// Routes OEM driver search to the catalog matching Manufacturer.
/// Lenovo, Dell, and HP are registered; ASUS can plug in later.
/// </summary>
internal static class OemDriverSearch
{
    private static readonly IOemDriverCatalog[] Catalogs =
    {
        new LenovoOemDriverCatalog(),
        new DellOemDriverCatalog(),
        new HpOemDriverCatalog()
        // new AsusOemDriverCatalog(),
    };

    public static async Task<IReadOnlyList<UpdateEntry>> SearchAsync(
        MachineIdentityReader.Identity identity,
        Action<string>? report = null,
        CancellationToken cancellationToken = default)
    {
        if (IsMissing(identity.Manufacturer))
        {
            report?.Invoke("OEM drivers: manufacturer unknown — skipped.");
            return Array.Empty<UpdateEntry>();
        }

        var catalog = Catalogs.FirstOrDefault(c => c.Matches(identity.Manufacturer));
        if (catalog is null)
        {
            report?.Invoke($"OEM drivers: no catalog for «{identity.Manufacturer}» yet (Lenovo / Dell / HP in v1).");
            return Array.Empty<UpdateEntry>();
        }

        var offers = await catalog.SearchAsync(identity, report, cancellationToken).ConfigureAwait(false);
        var entries = offers.Select(ToEntry).Where(e => e.OpensExternalUrl).ToList();
        int afterCatalog = entries.Count;
        entries = OemHandledStore.FilterUnhandled(entries).ToList();
        int afterHandled = entries.Count;
        // Before showing results: drop ≤ installed and older siblings for the same component.
        entries = OemInstalledVersionFilter.Filter(entries, report).ToList();
        entries.Sort(CompareOemPriorityThenTitle);
        int handledDropped = afterCatalog - afterHandled;
        int versionDropped = afterHandled - entries.Count;
        report?.Invoke(
            $"OEM drivers: {entries.Count} offer(s) after version filter" +
            (handledDropped > 0 ? $" ({handledDropped} handled)" : "") +
            (versionDropped > 0 ? $" ({versionDropped} older/equal)" : "") +
            ".");
        return entries;
    }

    internal static UpdateEntry ToEntry(OemDriverOffer offer)
    {
        bool firmware = offer.Status.Equals("Firmware", StringComparison.OrdinalIgnoreCase) ||
                        offer.Category.Contains("BIOS", StringComparison.OrdinalIgnoreCase);
        // Package URL only — never fall back to an HTML details/support page.
        string openUrl = (offer.DownloadUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(openUrl) || OemPackageDownload.LooksLikeSupportPage(openUrl))
            openUrl = "";
        return new UpdateEntry
        {
            UpdateId = "", // never install through WUA
            Category = "Driver / firmware",
            Title = offer.Title,
            KB = "OEM",
            Priority = MapPriority(offer.Priority),
            PriorityExplanation = offer.Reason,
            Status = "Tilgjengelig",
            Source = "OEM / " + offer.Brand,
            DriverClassification = firmware ? "Firmware" : "OEM",
            DriverStatus = offer.Status,
            DriverVersionFromWua = offer.Version is "—" or "" ? "" : offer.Version,
            DriverManufacturer = offer.Brand,
            DriverClass = offer.Category,
            DriverReason = offer.Reason,
            DriverAssigned = false,
            SafeDefault = false,
            IsSelected = false,
            ExternalUrl = openUrl,
            ReadmeUrl = offer.ReadmeUrl?.Trim() ?? "",
            OemPackageId = offer.Id?.Trim() ?? "",
            // Leave Date empty so DisplayStatus keeps DriverStatus (Recommended/Firmware).
            // Catalog release time goes in DateDisplay for the Date/Time column only.
            Date = "",
            DateDisplay = offer.ReleasedAt?.ToLocalTime().ToString("g") ?? ""
        };
    }

    /// <summary>Critical first, then Important, then other OEM priorities; stable title within a band.</summary>
    internal static int CompareOemPriorityThenTitle(UpdateEntry a, UpdateEntry b)
    {
        int byPri = PriorityRank(a).CompareTo(PriorityRank(b));
        if (byPri != 0) return byPri;
        return string.Compare(a.DisplayTitle, b.DisplayTitle, StringComparison.OrdinalIgnoreCase);
    }

    private static int PriorityRank(UpdateEntry entry)
    {
        string raw = entry.Priority ?? "";
        if (raw.Equals("Critical", StringComparison.OrdinalIgnoreCase)) return 0;
        if (raw.Equals("Important", StringComparison.OrdinalIgnoreCase)) return 1;
        if (raw.Equals("Optional", StringComparison.OrdinalIgnoreCase)) return 3;
        return 2;
    }

    private static string MapPriority(string priority)
    {
        if (priority.Equals("Critical", StringComparison.OrdinalIgnoreCase)) return "Critical";
        if (priority.Equals("Recommended", StringComparison.OrdinalIgnoreCase)) return "Important";
        if (priority.Equals("Optional", StringComparison.OrdinalIgnoreCase)) return "Optional";
        return "Unrated";
    }

    private static bool IsMissing(string value) =>
        string.IsNullOrWhiteSpace(value) || value is "—" or "…" or "-" or "N/A" or "n/a";

}
