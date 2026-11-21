using UpdateExpress.Models;

namespace UpdateExpress.Services.OemDrivers;

/// <summary>
/// Routes OEM driver search to the catalog matching Manufacturer.
/// Lenovo is registered in v1; add Dell/HP/ASUS catalogs here later.
/// </summary>
internal static class OemDriverSearch
{
    private static readonly IOemDriverCatalog[] Catalogs =
    {
        new LenovoOemDriverCatalog()
        // new DellOemDriverCatalog(),
        // new HpOemDriverCatalog(),
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
            report?.Invoke($"OEM drivers: no catalog for «{identity.Manufacturer}» yet (Lenovo in v1).");
            return Array.Empty<UpdateEntry>();
        }

        var offers = await catalog.SearchAsync(identity, report, cancellationToken).ConfigureAwait(false);
        return offers.Select(ToEntry).ToList();
    }

    internal static UpdateEntry ToEntry(OemDriverOffer offer)
    {
        bool firmware = offer.Status.Equals("Firmware", StringComparison.OrdinalIgnoreCase) ||
                        offer.Category.Contains("BIOS", StringComparison.OrdinalIgnoreCase);
        string openUrl = First(offer.DetailsUrl, offer.DownloadUrl);
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
            // Leave Date empty so DisplayStatus keeps DriverStatus (Recommended/Firmware).
            // Catalog release time goes in DateDisplay for the Date/Time column only.
            Date = "",
            DateDisplay = offer.ReleasedAt?.ToLocalTime().ToString("g") ?? ""
        };
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

    private static string First(params string[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        return "";
    }
}
