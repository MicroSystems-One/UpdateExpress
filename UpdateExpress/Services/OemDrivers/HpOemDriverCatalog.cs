using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace UpdateExpress.Services.OemDrivers;

/// <summary>
/// HP Support public wcc-services / typeahead catalog.
/// Resolve Product (SKU) / Model / Serial via typeahead, then driverDetails for the current OS.
/// </summary>
internal sealed class HpOemDriverCatalog : IOemDriverCatalog
{
    private const string TypeaheadUrl = "https://support.hp.com/typeahead";
    private const string SpecsUrl = "https://support.hp.com/wcc-services/profile/devices/warranty/specs?cache=true";
    private const string OsUrl = "https://support.hp.com/wcc-services/swd-v2/osVersionData";
    private const string DriversUrl = "https://support.hp.com/wcc-services/swd-v2/driverDetails";
    private const string Referer = "https://support.hp.com/";

    private static readonly HttpClient Http = OemCatalogHelpers.CreateClient(Referer, "HP");

    private static readonly HashSet<string> KeepAccordions = new(StringComparer.OrdinalIgnoreCase)
    {
        "BIOS-System Firmware",
        "Firmware",
        "Driver-Network",
        "Driver-Graphics",
        "Driver-Audio",
        "Driver-Storage",
        "Driver-Chipset",
        "Driver-Keyboard, Mouse and Input Devices",
        "Driver-Enabling",
        "Docks-Firmware and Driver"
    };

    public string Brand => "HP";

    public bool Matches(string manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer)) return false;
        var raw = manufacturer.Trim();
        return raw.Contains("hewlett-packard", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("hewlett packard", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("hp inc", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "HP", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "HPI", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<OemDriverOffer>> SearchAsync(
        MachineIdentityReader.Identity identity,
        Action<string>? report = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveProductAsync(identity, report, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            report?.Invoke("OEM / HP: could not resolve product from Model / Product / Serial.");
            return Array.Empty<OemDriverOffer>();
        }

        report?.Invoke($"OEM / HP: querying drivers for {resolved.ProductName}…");
        string osFamily = OemCatalogHelpers.CurrentWindowsFamily();
        string? osId = await PickOsIdAsync(resolved.SeriesOid, osFamily, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(osId))
        {
            report?.Invoke("OEM / HP: no Windows OS entry for this product.");
            return Array.Empty<OemDriverOffer>();
        }

        using var doc = await PostJsonAsync(
            DriversUrl,
            new Dictionary<string, object?>
            {
                ["productLineCode"] = resolved.ProductLineCode,
                ["lc"] = "en",
                ["cc"] = "us",
                ["osTMSId"] = osId,
                ["osName"] = "Windows",
                ["productNumberOid"] = resolved.ProductNumberOid,
                ["productSeriesOid"] = resolved.SeriesOid,
                ["platformId"] = osId
            },
            cancellationToken).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("softwareTypes", out var types) ||
            types.ValueKind != JsonValueKind.Array)
        {
            report?.Invoke("OEM / HP: empty driver response.");
            return Array.Empty<OemDriverOffer>();
        }

        var offers = new List<OemDriverOffer>();
        foreach (var type in types.EnumerateArray())
        {
            string accordion = Text(type, "accordionName");
            if (!KeepAccordions.Contains(accordion)) continue;
            if (!type.TryGetProperty("softwareDriversList", out var list) || list.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in list.EnumerateArray())
            {
                var offer = TryParseDriver(item, accordion, resolved.ProductName);
                if (offer is not null) offers.Add(offer);
            }
        }

        offers.Sort((a, b) =>
        {
            int byCat = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
            if (byCat != 0) return byCat;
            int byDate = Nullable.Compare(b.ReleasedAt, a.ReleasedAt);
            if (byDate != 0) return byDate;
            return string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
        });

        report?.Invoke($"OEM / HP: {offers.Count} driver offer(s).");
        return offers;
    }

    private static OemDriverOffer? TryParseDriver(JsonElement item, string accordion, string productName)
    {
        JsonElement latest = item;
        if (item.TryGetProperty("latestVersionDriver", out var nested) && nested.ValueKind == JsonValueKind.Object)
            latest = nested;

        string title = Text(latest, "title");
        if (string.IsNullOrWhiteSpace(title)) return null;

        string version = OemCatalogHelpers.Prefer(Text(latest, "version"), "—");
        string priorityRaw = Text(latest, "severityFlag");
        string download = Text(latest, "fileUrl");
        if (string.IsNullOrWhiteSpace(download) &&
            latest.TryGetProperty("productSoftwareFileList", out var files) &&
            files.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in files.EnumerateArray())
            {
                download = Text(file, "fileUrl");
                if (!string.IsNullOrWhiteSpace(download)) break;
                download = Text(file, "url");
                if (!string.IsNullOrWhiteSpace(download)) break;
            }
        }

        if (string.IsNullOrWhiteSpace(download)) return null;

        string id = OemCatalogHelpers.Prefer(Text(latest, "softwareItemId"), title);
        DateTimeOffset? released = ParseHpDate(Text(latest, "releaseDateString"))
            ?? ParseHpDate(Text(latest, "versionUpdatedDateString"));

        string status = MapStatus(priorityRaw, accordion);
        string priority = MapPriority(priorityRaw);
        return new OemDriverOffer(
            Brand: "HP",
            Id: "hp:" + id,
            Title: title.Trim(),
            Category: accordion,
            Version: version,
            Priority: priority,
            Status: status,
            DownloadUrl: download,
            DetailsUrl: download,
            ReleasedAt: released,
            Reason: $"HP Support catalog ({productName}). Open link to download; not installed through WUA.");
    }

    private static string MapStatus(string severity, string accordion)
    {
        if (accordion.Contains("BIOS", StringComparison.OrdinalIgnoreCase) ||
            accordion.Equals("Firmware", StringComparison.OrdinalIgnoreCase))
            return "Firmware";
        if (severity.Equals("critical", StringComparison.OrdinalIgnoreCase))
            return "Recommended";
        if (severity.Equals("recommended", StringComparison.OrdinalIgnoreCase))
            return "Recommended";
        return "Available";
    }

    private static string MapPriority(string severity)
    {
        if (severity.Equals("critical", StringComparison.OrdinalIgnoreCase)) return "Critical";
        if (severity.Equals("recommended", StringComparison.OrdinalIgnoreCase)) return "Important";
        if (severity.Equals("routine", StringComparison.OrdinalIgnoreCase) ||
            severity.Equals("optional", StringComparison.OrdinalIgnoreCase))
            return "Optional";
        return "Unrated";
    }

    private sealed record HpProduct(
        long SeriesOid,
        long ProductNumberOid,
        string ProductLineCode,
        string ProductName);

    private static async Task<HpProduct?> ResolveProductAsync(
        MachineIdentityReader.Identity identity,
        Action<string>? report,
        CancellationToken cancellationToken)
    {
        foreach (var query in new[] { identity.Product, identity.Model, identity.Serial })
        {
            if (OemCatalogHelpers.IsMissing(query)) continue;
            report?.Invoke($"OEM / HP: resolving «{query}»…");
            var match = await TypeaheadAsync(query, cancellationToken).ConfigureAwait(false);
            if (match is null) continue;

            long seriesOid = match.SeriesOid;
            long modelOid = match.ModelOid ?? match.SeriesOid;
            try
            {
                var specs = await LoadSpecsAsync(modelOid, cancellationToken).ConfigureAwait(false);
                if (specs is not null) return specs;
            }
            catch (Exception ex)
            {
                report?.Invoke("OEM / HP: specs lookup skipped (" + OemCatalogHelpers.Short(ex.Message) + ").");
            }

            // Specs failed — still try drivers with seriesOid as both series + product number.
            return new HpProduct(seriesOid, modelOid, "6U", match.Name);
        }
        return null;
    }

    private sealed record TypeaheadMatch(long SeriesOid, long? ModelOid, string Name, string PmClass);

    private static async Task<TypeaheadMatch?> TypeaheadAsync(string query, CancellationToken cancellationToken)
    {
        var url =
            TypeaheadUrl +
            "?q=" + Uri.EscapeDataString(query) +
            "&resultLimit=8&store=tmsstore&languageCode=en" +
            "&filters=" + Uri.EscapeDataString(
                "class:(pm_series_value^1.1 OR pm_name_value OR pm_number_value) AND (hiddenproduct:no OR (!_exists_:hiddenproduct))") +
            "&printFields=" + Uri.EscapeDataString(
                "tmspmseriesvalue,tmspmnamevalue,tmspmnumbervalue,class,productid,seofriendlyname,activewebsupportflag,navigationpath,childnodes,name,pmClass,pmSeriesOid,productname");

        using var doc = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("matches", out var matches) || matches.ValueKind != JsonValueKind.Array)
            return null;

        TypeaheadMatch? bestSeries = null;
        TypeaheadMatch? bestOther = null;
        foreach (var m in matches.EnumerateArray())
        {
            string pmClass = Text(m, "pmClass");
            string name = OemCatalogHelpers.Prefer(Text(m, "productname"), Text(m, "name"), query);
            long productId = Long(m, "productId");
            long seriesOid = Long(m, "pmSeriesOid");
            if (productId == 0 && seriesOid == 0) continue;

            long resolvedSeries = seriesOid != 0 ? seriesOid : productId;
            long? modelOid = null;
            if (pmClass.Equals("pm_name_value", StringComparison.OrdinalIgnoreCase) ||
                pmClass.Equals("pm_number_value", StringComparison.OrdinalIgnoreCase))
                modelOid = productId != 0 ? productId : null;

            // Prefer childnodes first model when series match has children.
            if (modelOid is null &&
                m.TryGetProperty("childnodes", out var children) &&
                children.ValueKind == JsonValueKind.String)
            {
                var first = (children.GetString() ?? "")
                    .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault();
                if (long.TryParse(first, out long childOid))
                    modelOid = childOid;
            }

            var parsed = new TypeaheadMatch(resolvedSeries, modelOid, name, pmClass);
            if (pmClass.Equals("pm_series_value", StringComparison.OrdinalIgnoreCase))
            {
                bestSeries ??= parsed;
                break;
            }
            bestOther ??= parsed;
        }

        return bestSeries ?? bestOther;
    }

    private static async Task<HpProduct?> LoadSpecsAsync(long modelOid, CancellationToken cancellationToken)
    {
        using var doc = await PostJsonAsync(
            SpecsUrl,
            new Dictionary<string, object?>
            {
                ["cc"] = "us",
                ["lc"] = "en",
                ["utcOffset"] = "M0700",
                ["devices"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["seriesOid"] = null,
                        ["modelOid"] = modelOid,
                        ["serialNumber"] = null,
                        ["displayProductNumber"] = null,
                        ["countryOfPurchase"] = "us"
                    }
                },
                ["skipSyncCall"] = false,
                ["captchaToken"] = ""
            },
            cancellationToken).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("devices", out var devices) ||
            devices.ValueKind != JsonValueKind.Array ||
            devices.GetArrayLength() == 0)
            return null;

        var device = devices[0];
        if (!device.TryGetProperty("productSpecs", out var productSpecs) ||
            !productSpecs.TryGetProperty("data", out var specs) ||
            specs.ValueKind != JsonValueKind.Object)
            return null;

        long seriesOid = Long(specs, "productSeriesOid");
        long productNumberOid = Long(specs, "productNumberOid");
        if (productNumberOid == 0) productNumberOid = Long(specs, "productNameOid");
        string line = Text(specs, "productLineCode");
        string name = OemCatalogHelpers.Prefer(Text(specs, "productName"), Text(specs, "productSeriesName"));
        if (seriesOid == 0 || productNumberOid == 0 || string.IsNullOrWhiteSpace(line))
            return null;
        return new HpProduct(seriesOid, productNumberOid, line, name);
    }

    private static async Task<string?> PickOsIdAsync(long seriesOid, string osFamily, CancellationToken cancellationToken)
    {
        string url = OsUrl + "?cc=us&lc=en&productOid=" + seriesOid;
        using var doc = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("osAvailablePlatformsAnsOS", out var platformsRoot) ||
            !platformsRoot.TryGetProperty("osPlatforms", out var platforms) ||
            platforms.ValueKind != JsonValueKind.Array)
            return null;

        string? exact = null;
        string? family = null;
        string? any = null;
        foreach (var platform in platforms.EnumerateArray())
        {
            if (!platform.TryGetProperty("osVersions", out var versions) || versions.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var version in versions.EnumerateArray())
            {
                string name = Text(version, "name");
                string id = Text(version, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                any ??= id;
                if (name.Equals(osFamily, StringComparison.OrdinalIgnoreCase))
                    exact ??= id;
                else if (name.StartsWith(osFamily, StringComparison.OrdinalIgnoreCase))
                    family ??= id;
            }
        }
        return exact ?? family ?? any;
    }

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> PostJsonAsync(
        string url,
        object body,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => ""
        };
    }

    private static long Long(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long n)) return n;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out n)) return n;
        return 0;
    }

    private static DateTimeOffset? ParseHpDate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (DateTimeOffset.TryParse(text, out var dto)) return dto;
        return null;
    }
}
