using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UpdateExpress.Services.OemDrivers;

/// <summary>
/// Lenovo PC Support public driver catalog (JSON).
/// Uses machine type (MT) from Produktnr/MTM — e.g. 10YM0027MX → 10YM.
/// </summary>
internal sealed class LenovoOemDriverCatalog : IOemDriverCatalog
{
    private const string DriversApi =
        "https://pcsupport.lenovo.com/us/en/api/v4/downloads/drivers?productId=";
    private const string ProductsApi =
        "https://pcsupport.lenovo.com/us/en/api/v4/mse/getproducts?productId=";
    private const string Referer = "https://pcsupport.lenovo.com/";

    private static readonly HttpClient Http = CreateClient();

    private static readonly HashSet<string> DriverCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Audio",
        "BIOS/UEFI",
        "Bluetooth and Modem",
        "Display and Video Graphics",
        "Fingerprint Reader",
        "Motherboard Devices (Backplanes, core chipset, onboard video, PCIe switches)",
        "Networking: LAN (Ethernet)",
        "Networking: Wireless LAN",
        "Storage",
        "Chipset",
        "Card Reader",
        "Camera and Imaging",
        "Input Devices",
        "Power Management"
    };

    public string Brand => "Lenovo";

    public bool Matches(string manufacturer) =>
        !string.IsNullOrWhiteSpace(manufacturer) &&
        manufacturer.Contains("lenovo", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<OemDriverOffer>> SearchAsync(
        MachineIdentityReader.Identity identity,
        Action<string>? report = null,
        CancellationToken cancellationToken = default)
    {
        // Never truncate Serial into a fake MT (PF123456 → PF12). Prefer Product/Model MTM;
        // when missing, resolve Serial (or Product) through getproducts first.
        string machineType = ResolveMachineType(identity.Product, identity.Model);
        if (string.IsNullOrEmpty(machineType))
        {
            try
            {
                var resolved = await TryResolveFromCatalogAsync(identity, cancellationToken)
                    .ConfigureAwait(false);
                machineType = resolved.MachineType;
            }
            catch (Exception ex)
            {
                report?.Invoke("OEM / Lenovo: serial/product resolve failed (" + Short(ex.Message) + ").");
            }
        }

        if (string.IsNullOrEmpty(machineType))
        {
            report?.Invoke("OEM / Lenovo: no MTM / machine type on this PC.");
            return Array.Empty<OemDriverOffer>();
        }

        string productId = machineType;
        report?.Invoke($"OEM / Lenovo: querying catalog for MT {machineType}…");

        JsonDocument? doc;
        try
        {
            doc = await GetJsonAsync(DriversApi + Uri.EscapeDataString(productId), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Retry with bare MT if a long product path failed.
            if (!string.Equals(productId, machineType, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    doc = await GetJsonAsync(DriversApi + Uri.EscapeDataString(machineType), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception retryEx)
                {
                    report?.Invoke("OEM / Lenovo: catalog request failed (" + Short(retryEx.Message) + ").");
                    return Array.Empty<OemDriverOffer>();
                }
            }
            else
            {
                report?.Invoke("OEM / Lenovo: catalog request failed (" + Short(ex.Message) + ").");
                return Array.Empty<OemDriverOffer>();
            }
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("body", out var body) ||
                !body.TryGetProperty("DownloadItems", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                report?.Invoke("OEM / Lenovo: empty or unexpected catalog response.");
                return Array.Empty<OemDriverOffer>();
            }

            string osHint = CurrentOsHint();
            var offers = new List<OemDriverOffer>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var offer = TryParseItem(item, machineType, osHint);
                if (offer is not null) offers.Add(offer);
            }

            // Newest first within category; keep list stable for the grid.
            offers.Sort((a, b) =>
            {
                int byCat = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
                if (byCat != 0) return byCat;
                int byDate = Nullable.Compare(b.ReleasedAt, a.ReleasedAt);
                if (byDate != 0) return byDate;
                return string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
            });

            report?.Invoke($"OEM / Lenovo: {offers.Count} driver offer(s) for MT {machineType}.");
            return offers;
        }
    }

    private static OemDriverOffer? TryParseItem(JsonElement item, string machineType, string osHint)
    {
        string title = Text(item, "Title");
        if (string.IsNullOrWhiteSpace(title)) return null;
        if (title.Contains("SCCM", StringComparison.OrdinalIgnoreCase)) return null;

        string category = "";
        if (item.TryGetProperty("Category", out var cat) && cat.ValueKind == JsonValueKind.Object)
            category = Text(cat, "Name");
        if (string.IsNullOrWhiteSpace(category) || !DriverCategories.Contains(category))
            return null;

        // BIOS/UEFI packages are often tagged Windows 10 only but apply on Windows 11 too.
        bool firmwareCategory = category.Contains("BIOS", StringComparison.OrdinalIgnoreCase) ||
                                category.Contains("UEFI", StringComparison.OrdinalIgnoreCase);
        if (!firmwareCategory &&
            item.TryGetProperty("OperatingSystemKeys", out var osKeys) &&
            osKeys.ValueKind == JsonValueKind.Array &&
            osKeys.GetArrayLength() > 0 &&
            !OsCompatible(osKeys, osHint))
            return null;

        string version = "";
        string priority = "Recommended";
        if (item.TryGetProperty("SummaryInfo", out var summary) && summary.ValueKind == JsonValueKind.Object)
        {
            version = Text(summary, "Version");
            priority = Prefer(Text(summary, "Priority"), priority);
        }

        string id = Text(item, "ID");
        if (string.IsNullOrWhiteSpace(id)) id = title;

        string downloadUrl = "";
        string detailsUrl = "";
        DateTimeOffset? released = null;
        if (item.TryGetProperty("Files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            JsonElement? main = null;
            foreach (var file in files.EnumerateArray())
            {
                string type = Text(file, "TypeString");
                if (type.Equals("EXE", StringComparison.OrdinalIgnoreCase) ||
                    type.Equals("zip", StringComparison.OrdinalIgnoreCase) ||
                    type.Equals("ZIP", StringComparison.OrdinalIgnoreCase))
                {
                    main = file;
                    break;
                }
            }
            main ??= files.GetArrayLength() > 0 ? files[0] : null;
            if (main is { } fileNode)
            {
                downloadUrl = Text(fileNode, "URL");
                if (string.IsNullOrWhiteSpace(version))
                    version = Text(fileNode, "Version");
                if (string.IsNullOrWhiteSpace(priority))
                    priority = Text(fileNode, "Priority");
                if (fileNode.TryGetProperty("Date", out var date) &&
                    date.ValueKind == JsonValueKind.Object &&
                    date.TryGetProperty("Unix", out var unix) &&
                    unix.TryGetInt64(out long ms))
                {
                    try { released = DateTimeOffset.FromUnixTimeMilliseconds(ms); }
                    catch { /* ignore bad timestamps */ }
                }
            }

            foreach (var file in files.EnumerateArray())
            {
                string type = Text(file, "TypeString");
                if (type.Contains("readme", StringComparison.OrdinalIgnoreCase) ||
                    type.Equals("HTML", StringComparison.OrdinalIgnoreCase))
                {
                    detailsUrl = Text(file, "URL");
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(downloadUrl) && string.IsNullOrWhiteSpace(detailsUrl))
            return null;

        string openUrl = Prefer(detailsUrl, downloadUrl);
        string status = MapStatus(priority, category);
        return new OemDriverOffer(
            Brand: "Lenovo",
            Id: "lenovo:" + id,
            Title: title.Trim(),
            Category: category.Trim(),
            Version: string.IsNullOrWhiteSpace(version) ? "—" : version.Trim(),
            Priority: string.IsNullOrWhiteSpace(priority) ? "Recommended" : priority.Trim(),
            Status: status,
            DownloadUrl: downloadUrl,
            DetailsUrl: openUrl,
            ReleasedAt: released,
            Reason: $"Lenovo PC Support catalog (MT {machineType}). Open link to download; not installed through WUA.");
    }

    private static string MapStatus(string priority, string category)
    {
        if (category.Contains("BIOS", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("UEFI", StringComparison.OrdinalIgnoreCase))
            return "Firmware";
        if (priority.Equals("Critical", StringComparison.OrdinalIgnoreCase))
            return "Recommended";
        if (priority.Equals("Recommended", StringComparison.OrdinalIgnoreCase))
            return "Recommended";
        return "Available";
    }

    private static bool OsCompatible(JsonElement osKeys, string osHint)
    {
        foreach (var key in osKeys.EnumerateArray())
        {
            string name = key.ValueKind == JsonValueKind.String ? key.GetString() ?? "" : key.ToString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (name.Contains("OS Independent", StringComparison.OrdinalIgnoreCase))
                return true;
            if (name.StartsWith(osHint, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string CurrentOsHint()
    {
        // Environment.OSVersion on modern Windows still reports 10.0; build ≥ 22000 ⇒ Windows 11.
        try
        {
            var version = Environment.OSVersion.Version;
            if (version.Major >= 10 && version.Build >= 22000)
                return "Windows 11";
        }
        catch { /* fall through */ }
        return "Windows 10";
    }

    /// <summary>
    /// Derive MT from Product/Model only — never from Serial (serials like PF123456
    /// look code-like but are not machine types).
    /// </summary>
    internal static string ResolveMachineType(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var text = candidate.Trim();
            if (IsMissing(text)) continue;

            // Bare 4-char machine type (10YM, 20X4, 81SY)
            if (LooksLikeBareMachineType(text))
                return text.ToUpperInvariant();

            // Full MTM: 10YM0027MX → 10YM (digit-led product codes, not letter-led serials)
            if (LooksLikeFullMtm(text))
            {
                var mt = Regex.Match(text, @"^[A-Za-z0-9]{4}");
                if (mt.Success) return mt.Value.ToUpperInvariant();
            }
        }
        return "";
    }

    private readonly record struct CatalogResolve(string MachineType);

    /// <summary>
    /// Last-resort resolve when Product/Model did not yield an MT: ask Lenovo getproducts
    /// with Product then Serial, and extract MT from the returned product path.
    /// </summary>
    private static async Task<CatalogResolve> TryResolveFromCatalogAsync(
        MachineIdentityReader.Identity identity,
        CancellationToken cancellationToken)
    {
        var queries = new List<string>();
        void AddQuery(string value)
        {
            if (IsMissing(value)) return;
            if (queries.Any(q => q.Equals(value, StringComparison.OrdinalIgnoreCase))) return;
            queries.Add(value.Trim());
        }

        AddQuery(identity.Product);
        AddQuery(identity.Serial);

        foreach (var query in queries)
        {
            using var doc = await GetJsonAsync(ProductsApi + Uri.EscapeDataString(query), cancellationToken)
                .ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;

            foreach (var product in doc.RootElement.EnumerateArray())
            {
                string id = Text(product, "Id");
                if (string.IsNullOrWhiteSpace(id)) continue;

                string mtFromPath = ExtractMachineTypeFromProductId(id);
                if (!string.IsNullOrEmpty(mtFromPath))
                    return new CatalogResolve(mtFromPath);
            }
        }

        return new CatalogResolve("");
    }

    /// <summary>
    /// Pull MT from Lenovo product Id paths, e.g.
    /// …/THINKCENTRE-M630E/10YM/10YM0027MX or …/20X4/20X4S6WR00/PF3ABCDE.
    /// </summary>
    internal static string ExtractMachineTypeFromProductId(string productId)
    {
        var parts = productId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (LooksLikeBareMachineType(part))
                return part.ToUpperInvariant();
        }
        foreach (var part in parts)
        {
            if (LooksLikeFullMtm(part))
            {
                var mt = Regex.Match(part, @"^[A-Za-z0-9]{4}");
                if (mt.Success) return mt.Value.ToUpperInvariant();
            }
        }
        return "";
    }

    private static bool LooksLikeBareMachineType(string text) =>
        Regex.IsMatch(text, @"^[A-Za-z0-9]{4}$") && text.Any(char.IsDigit);

    /// <summary>
    /// Lenovo product/MTM codes are typically digit-led (10YM0027MX, 20X4S6WR00).
    /// Letter-led values (PF123456) are treated as serials, not MTMs.
    /// </summary>
    private static bool LooksLikeFullMtm(string text) =>
        MachineIdentityReader.LooksLikeMachineType(text) &&
        !MachineIdentityReader.LooksLikeMarketingModel(text) &&
        text.Length >= 5 &&
        char.IsDigit(text[0]);

    private static bool IsMissing(string value) =>
        string.IsNullOrWhiteSpace(value) || value is "—" or "…" or "-" or "N/A" or "n/a";

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.Referrer = new Uri(Referer);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "UpdateExpress/" + UpdateExpress.ProductVersion.Display + " (+https://github.com/MicroSystems-One/UpdateExpress)");
        return client;
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

    private static string Prefer(params string[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        return "";
    }

    private static string Short(string message)
    {
        message = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 120 ? message : message[..117] + "…";
    }
}
