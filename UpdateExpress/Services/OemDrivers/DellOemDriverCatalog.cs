using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace UpdateExpress.Services.OemDrivers;

/// <summary>
/// Dell driver catalog.
/// Prefer support IPS JSON by service tag / product code when reachable;
/// fall back to public CatalogPC.cab on downloads.dell.com (matched by SystemID / Model).
/// </summary>
internal sealed class DellOemDriverCatalog : IOemDriverCatalog
{
    private const string IpsByTag =
        "https://www.dell.com/support/driver/en-us/ips/api/driverlist/fetchdriversbytag";
    private const string IpsByProduct =
        "https://www.dell.com/support/driver/en-us/ips/api/driverlist/fetchdriversbyproduct";
    private const string SupportTagPage =
        "https://www.dell.com/support/home/en-us/product-support/servicetag/";
    private const string CatalogCabUrl =
        "https://downloads.dell.com/catalog/CatalogPC.cab";
    private const string Referer = "https://www.dell.com/support/";

    private static readonly HttpClient Http = OemCatalogHelpers.CreateClient(Referer, "Dell");
    private static readonly object CatalogGate = new();

    private static readonly HashSet<string> Win11OsCodes = new(StringComparer.OrdinalIgnoreCase)
        { "W21P4", "W21H4", "W21S4", "W2021" };
    private static readonly HashSet<string> Win10OsCodes = new(StringComparer.OrdinalIgnoreCase)
        { "W10P4", "W10H4", "WT64A", "W10" };

    public string Brand => "Dell";

    public bool Matches(string manufacturer) =>
        !string.IsNullOrWhiteSpace(manufacturer) &&
        manufacturer.Contains("dell", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<OemDriverOffer>> SearchAsync(
        MachineIdentityReader.Identity identity,
        Action<string>? report = null,
        CancellationToken cancellationToken = default)
    {
        string serviceTag = OemCatalogHelpers.Prefer(identity.Serial);
        string model = OemCatalogHelpers.Prefer(identity.Model);
        string product = OemCatalogHelpers.Prefer(identity.Product);

        try
        {
            var ips = await TryIpsAsync(serviceTag, model, product, report, cancellationToken)
                .ConfigureAwait(false);
            if (ips.Count > 0)
            {
                report?.Invoke($"OEM / Dell: {ips.Count} driver offer(s) from support catalog.");
                return ips;
            }
        }
        catch (Exception ex)
        {
            report?.Invoke("OEM / Dell: support API unavailable (" + OemCatalogHelpers.Short(ex.Message) + ").");
        }

        report?.Invoke("OEM / Dell: falling back to CatalogPC…");
        try
        {
            var catalog = await Task.Run(
                () => SearchCatalogPc(serviceTag, model, product, report),
                cancellationToken).ConfigureAwait(false);
            report?.Invoke($"OEM / Dell: {catalog.Count} driver offer(s) from CatalogPC.");
            return catalog;
        }
        catch (Exception ex)
        {
            report?.Invoke("OEM / Dell: CatalogPC failed (" + OemCatalogHelpers.Short(ex.Message) + ").");
            return Array.Empty<OemDriverOffer>();
        }
    }

    private static async Task<IReadOnlyList<OemDriverOffer>> TryIpsAsync(
        string serviceTag,
        string model,
        string product,
        Action<string>? report,
        CancellationToken cancellationToken)
    {
        string oscode = OemCatalogHelpers.CurrentWindowsFamily() == "Windows 11" ? "W2021" : "WT64A";

        if (!string.IsNullOrEmpty(serviceTag))
        {
            report?.Invoke($"OEM / Dell: resolving service tag {serviceTag}…");
            var fromTag = await FetchByServiceTagAsync(serviceTag, oscode, cancellationToken)
                .ConfigureAwait(false);
            if (fromTag.Count > 0) return fromTag;
        }

        string productCode = GuessProductCode(model, product);
        if (string.IsNullOrEmpty(productCode))
            return Array.Empty<OemDriverOffer>();

        report?.Invoke($"OEM / Dell: querying product code {productCode}…");
        string url = IpsByProduct +
                     "?productcode=" + Uri.EscapeDataString(productCode) +
                     "&oscode=" + Uri.EscapeDataString(oscode) +
                     "&initialload=true";
        using var doc = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        return ParseIpsDrivers(doc.RootElement, serviceTag);
    }

    private static async Task<IReadOnlyList<OemDriverOffer>> FetchByServiceTagAsync(
        string serviceTag,
        string oscode,
        CancellationToken cancellationToken)
    {
        using var pageRequest = new HttpRequestMessage(HttpMethod.Get, SupportTagPage + Uri.EscapeDataString(serviceTag));
        pageRequest.Headers.TryAddWithoutValidation("Accept", "text/html");
        using var pageResponse = await Http.SendAsync(pageRequest, cancellationToken).ConfigureAwait(false);
        if (!pageResponse.IsSuccessStatusCode)
            return Array.Empty<OemDriverOffer>();

        string html = await pageResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string productCode = MatchGroup(html, @"Dell\.Metrics\.sc\.supportsystem\s*=\s*""([^""]+)""")
            ?? MatchGroup(html, @"supportsystem\s*=\s*""([^""]+)""")
            ?? "";
        string encrypted = MatchGroup(html, @"serviceEncryptedkey\s*=\s*'([^']+)'")
            ?? MatchGroup(html, @"serviceEncryptedKey\s*=\s*""([^""]+)""")
            ?? "";

        if (string.IsNullOrEmpty(productCode))
            return Array.Empty<OemDriverOffer>();

        string url = IpsByTag +
                     "?productcode=" + Uri.EscapeDataString(productCode) +
                     (string.IsNullOrEmpty(encrypted)
                         ? ""
                         : "&servicetag=" + Uri.EscapeDataString(encrypted)) +
                     "&oscode=" + Uri.EscapeDataString(oscode);
        using var doc = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        return ParseIpsDrivers(doc.RootElement, serviceTag);
    }

    private static List<OemDriverOffer> ParseIpsDrivers(JsonElement root, string serviceTag)
    {
        if (!root.TryGetProperty("DriverListData", out var list) || list.ValueKind != JsonValueKind.Array)
            return new List<OemDriverOffer>();

        var offers = new List<OemDriverOffer>();
        foreach (var item in list.EnumerateArray())
        {
            string type = Text(item, "Type");
            if (type is not ("TYPE_DRVR" or "TYPE_BIOS" or "TYPE_FRMW" or "DRVR" or "BIOS" or "FRMW") &&
                !string.IsNullOrEmpty(type) &&
                !type.Contains("DRVR", StringComparison.OrdinalIgnoreCase) &&
                !type.Contains("BIOS", StringComparison.OrdinalIgnoreCase) &&
                !type.Contains("FRMW", StringComparison.OrdinalIgnoreCase))
            {
                // Keep when Type missing but category looks like a driver.
                string catProbe = Text(item, "CatName");
                if (!LooksLikeDriverCategory(catProbe)) continue;
            }

            string title = Text(item, "DriverName");
            if (string.IsNullOrWhiteSpace(title)) continue;
            if (title.Contains("Dell Update", StringComparison.OrdinalIgnoreCase) &&
                title.Contains("Application", StringComparison.OrdinalIgnoreCase))
                continue;

            string version = OemCatalogHelpers.Prefer(Text(item, "DellVer"), Text(item, "DriverVersion"), "—");
            string category = OemCatalogHelpers.Prefer(Text(item, "CatName"), Text(item, "Type"), "Driver");
            string imp = Text(item, "Imp");
            string id = OemCatalogHelpers.Prefer(Text(item, "DriverId"), title);
            string download = "";
            if (item.TryGetProperty("FileFrmtInfo", out var file) && file.ValueKind == JsonValueKind.Object)
                download = Text(file, "HttpFileLocation");
            string details = $"https://www.dell.com/support/home/en-us/drivers/driversdetails?driverId={Uri.EscapeDataString(id)}";
            if (string.IsNullOrWhiteSpace(download))
                download = details;

            DateTimeOffset? released = null;
            if (DateTimeOffset.TryParse(Text(item, "ReleaseDate"), out var dto))
                released = dto;

            string status = MapIpsStatus(imp, category, type);
            offers.Add(new OemDriverOffer(
                Brand: "Dell",
                Id: "dell:" + id,
                Title: title.Trim(),
                Category: category,
                Version: version,
                Priority: MapIpsPriority(imp),
                Status: status,
                DownloadUrl: download,
                DetailsUrl: details,
                ReleasedAt: released,
                Reason: string.IsNullOrEmpty(serviceTag)
                    ? "Dell support catalog. Open link to download; not installed through WUA."
                    : $"Dell support catalog (service tag {serviceTag}). Open link to download; not installed through WUA."));
        }

        offers.Sort((a, b) =>
        {
            int byCat = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
            if (byCat != 0) return byCat;
            return string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
        });
        return offers;
    }

    private static bool LooksLikeDriverCategory(string category) =>
        !string.IsNullOrWhiteSpace(category) &&
        (category.Contains("BIOS", StringComparison.OrdinalIgnoreCase) ||
         category.Contains("Driver", StringComparison.OrdinalIgnoreCase) ||
         category.Contains("Firmware", StringComparison.OrdinalIgnoreCase) ||
         category.Contains("Chipset", StringComparison.OrdinalIgnoreCase) ||
         category.Contains("Network", StringComparison.OrdinalIgnoreCase) ||
         category.Contains("Audio", StringComparison.OrdinalIgnoreCase) ||
         category.Contains("Storage", StringComparison.OrdinalIgnoreCase) ||
         category.Contains("Video", StringComparison.OrdinalIgnoreCase) ||
         category.Contains("Graphics", StringComparison.OrdinalIgnoreCase));

    private static string MapIpsStatus(string imp, string category, string type)
    {
        if (category.Contains("BIOS", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("BIOS", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("FRMW", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("Firmware", StringComparison.OrdinalIgnoreCase))
            return "Firmware";
        if (imp.Equals("Critical", StringComparison.OrdinalIgnoreCase) ||
            imp.Equals("Urgent", StringComparison.OrdinalIgnoreCase) ||
            imp.Equals("Recommended", StringComparison.OrdinalIgnoreCase))
            return "Recommended";
        return "Available";
    }

    private static string MapIpsPriority(string imp)
    {
        if (imp.Equals("Critical", StringComparison.OrdinalIgnoreCase) ||
            imp.Equals("Urgent", StringComparison.OrdinalIgnoreCase))
            return "Critical";
        if (imp.Equals("Recommended", StringComparison.OrdinalIgnoreCase))
            return "Important";
        if (imp.Equals("Optional", StringComparison.OrdinalIgnoreCase))
            return "Optional";
        return "Unrated";
    }

    private static string GuessProductCode(string model, string product)
    {
        string raw = OemCatalogHelpers.Prefer(model, product);
        if (string.IsNullOrEmpty(raw)) return "";
        if (OemCatalogHelpers.LooksLikeDellSystemId(raw)) return "";

        string key = OemCatalogHelpers.NormalizeModelKey(raw).ToLowerInvariant();
        if (string.IsNullOrEmpty(key)) return "";
        if (!key.Contains("laptop", StringComparison.Ordinal) &&
            !key.Contains("desktop", StringComparison.Ordinal) &&
            !key.Contains("precision", StringComparison.Ordinal) &&
            !key.Contains("optiplex", StringComparison.Ordinal) &&
            !key.Contains("latitude", StringComparison.Ordinal) &&
            !key.Contains("inspiron", StringComparison.Ordinal) &&
            !key.Contains("xps", StringComparison.Ordinal) &&
            !key.Contains("vostro", StringComparison.Ordinal) &&
            !key.Contains("alienware", StringComparison.Ordinal))
        {
            // Common client models → append -laptop when it looks like a notebook family.
            if (key.Contains("latitude", StringComparison.Ordinal) ||
                key.Contains("inspiron", StringComparison.Ordinal) ||
                key.Contains("xps", StringComparison.Ordinal))
                key += "-laptop";
        }
        return key;
    }

    private static IReadOnlyList<OemDriverOffer> SearchCatalogPc(
        string serviceTag,
        string model,
        string product,
        Action<string>? report)
    {
        string xmlPath = EnsureCatalogPcXml(report);
        string modelKey = OemCatalogHelpers.NormalizeModelKey(model);
        string productKey = OemCatalogHelpers.NormalizeModelKey(product);
        bool productIsSystemId = OemCatalogHelpers.LooksLikeDellSystemId(product);
        string osFamily = OemCatalogHelpers.CurrentWindowsFamily();
        var wantedOs = osFamily == "Windows 11" ? Win11OsCodes : Win10OsCodes;

        var offers = new List<OemDriverOffer>();
        var settings = new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = true,
            DtdProcessing = DtdProcessing.Prohibit
        };

        using var reader = XmlReader.Create(xmlPath, settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "SoftwareComponent")
                continue;

            using var subtree = reader.ReadSubtree();
            var doc = new XmlDocument();
            doc.Load(subtree);
            var root = doc.DocumentElement;
            if (root is null) continue;

            string componentType = root.SelectSingleNode("ComponentType")?.Attributes?["value"]?.Value ?? "";
            if (componentType is not ("DRVR" or "BIOS" or "FRMW"))
                continue;

            if (!ComponentMatchesIdentity(root, modelKey, productKey, productIsSystemId ? product.Trim() : ""))
                continue;

            if (!OsCompatible(root, wantedOs, osFamily))
                continue;

            string title = root.SelectSingleNode("Name/Display[@lang='en']")?.InnerText?.Trim()
                ?? root.SelectSingleNode("Name/Display")?.InnerText?.Trim()
                ?? "";
            if (string.IsNullOrWhiteSpace(title)) continue;

            string path = root.GetAttribute("path");
            if (string.IsNullOrWhiteSpace(path)) continue;
            string download = "https://downloads.dell.com/" + path.TrimStart('/');
            string version = OemCatalogHelpers.Prefer(root.GetAttribute("vendorVersion"), root.GetAttribute("dellVersion"), "—");
            string id = OemCatalogHelpers.Prefer(root.GetAttribute("packageID"), root.GetAttribute("releaseID"), title);
            string details = root.SelectSingleNode("ImportantInfo")?.Attributes?["URL"]?.Value ?? download;
            string category = componentType switch
            {
                "BIOS" => "BIOS",
                "FRMW" => "Firmware",
                _ => root.SelectSingleNode("Category")?.Attributes?["value"]?.Value ?? "Driver"
            };
            string criticality = root.SelectSingleNode("Criticality")?.Attributes?["value"]?.Value ?? "1";
            DateTimeOffset? released = null;
            if (DateTimeOffset.TryParse(root.GetAttribute("releaseDate"), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dto))
                released = dto;

            string status = componentType is "BIOS" or "FRMW"
                ? "Firmware"
                : criticality is "3" or "4" or "2" ? "Recommended" : "Available";
            string priority = criticality switch
            {
                "3" or "4" => "Critical",
                "2" => "Important",
                _ => "Optional"
            };

            offers.Add(new OemDriverOffer(
                Brand: "Dell",
                Id: "dell-catalog:" + id,
                Title: title,
                Category: category,
                Version: version,
                Priority: priority,
                Status: status,
                DownloadUrl: download,
                DetailsUrl: details,
                ReleasedAt: released,
                Reason: string.IsNullOrEmpty(serviceTag)
                    ? $"Dell CatalogPC ({OemCatalogHelpers.Prefer(model, product)}). Open link to download; not installed through WUA."
                    : $"Dell CatalogPC (service tag {serviceTag}). Open link to download; not installed through WUA."));
        }

        // Dedupe by title+version (catalog repeats packages across brands).
        var deduped = offers
            .GroupBy(o => o.Title + "|" + o.Version, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(o => o.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return deduped;
    }

    private static bool ComponentMatchesIdentity(
        XmlElement root,
        string modelKey,
        string productKey,
        string systemId)
    {
        var models = root.SelectNodes(".//SupportedSystems//Model");
        if (models is null || models.Count == 0) return false;

        foreach (XmlNode node in models)
        {
            if (node is not XmlElement model) continue;
            string sid = model.GetAttribute("systemID");
            string display = model.SelectSingleNode("Display[@lang='en']")?.InnerText?.Trim()
                ?? model.SelectSingleNode("Display")?.InnerText?.Trim()
                ?? "";
            string displayKey = OemCatalogHelpers.NormalizeModelKey(display);

            if (!string.IsNullOrEmpty(systemId) &&
                sid.Equals(systemId, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrEmpty(productKey) &&
                OemCatalogHelpers.LooksLikeDellSystemId(productKey) &&
                sid.Equals(productKey, StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.IsNullOrEmpty(displayKey)) continue;

            if (!string.IsNullOrEmpty(modelKey) && ModelKeysOverlap(modelKey, displayKey))
                return true;
            if (!string.IsNullOrEmpty(productKey) &&
                !OemCatalogHelpers.LooksLikeDellSystemId(productKey) &&
                ModelKeysOverlap(productKey, displayKey))
                return true;
        }
        return false;
    }

    private static bool ModelKeysOverlap(string a, string b)
    {
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
        // Require a meaningful token length so bare "5420" does not match every 5420 variant
        // unless the identity model also carries a family name.
        if (a.Length >= 8 && (b.Contains(a, StringComparison.OrdinalIgnoreCase) || a.Contains(b, StringComparison.OrdinalIgnoreCase)))
            return true;
        var tokensA = a.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var tokensB = new HashSet<string>(b.Split('-', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        // Need family + model token (e.g. LATITUDE + 5420)
        int hits = tokensA.Count(t => t.Length >= 3 && tokensB.Contains(t));
        return hits >= 2 || (hits == 1 && tokensA.Any(t => t.Length >= 6 && tokensB.Contains(t)));
    }

    private static bool OsCompatible(XmlElement root, HashSet<string> wanted, string osFamily)
    {
        var nodes = root.SelectNodes(".//SupportedOperatingSystems/OperatingSystem");
        if (nodes is null || nodes.Count == 0) return true;
        foreach (XmlNode node in nodes)
        {
            string code = node.Attributes?["osCode"]?.Value ?? "";
            if (wanted.Contains(code)) return true;
            if (osFamily == "Windows 11" && code.StartsWith("W21", StringComparison.OrdinalIgnoreCase))
                return true;
            if (osFamily == "Windows 10" && code.StartsWith("W10", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string EnsureCatalogPcXml(Action<string>? report)
    {
        lock (CatalogGate)
        {
            string cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UpdateExpress", "OemCatalogs", "Dell");
            Directory.CreateDirectory(cacheDir);
            string cabPath = Path.Combine(cacheDir, "CatalogPC.cab");
            string xmlPath = Path.Combine(cacheDir, "CatalogPC.xml");
            string stampPath = Path.Combine(cacheDir, "CatalogPC.stamp");

            bool fresh = File.Exists(xmlPath) &&
                         File.Exists(stampPath) &&
                         DateTime.UtcNow - File.GetLastWriteTimeUtc(stampPath) < TimeSpan.FromDays(7) &&
                         new FileInfo(xmlPath).Length > 1_000_000;
            if (fresh) return xmlPath;

            report?.Invoke("OEM / Dell: downloading CatalogPC.cab…");
            using (var response = Http.GetAsync(CatalogCabUrl).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                using var fs = File.Create(cabPath);
                response.Content.CopyToAsync(fs).GetAwaiter().GetResult();
            }

            if (File.Exists(xmlPath)) File.Delete(xmlPath);
            ExtractCab(cabPath, cacheDir, report);
            if (!File.Exists(xmlPath))
                throw new InvalidOperationException("CatalogPC.xml missing after CAB extract.");
            File.WriteAllText(stampPath, DateTime.UtcNow.ToString("o"));
            return xmlPath;
        }
    }

    private static void ExtractCab(string cabPath, string destDir, Action<string>? report)
    {
        // Windows expand.exe ships with the OS. Non-Windows (dev/test) can use cabextract.
        string expand = Path.Combine(Environment.SystemDirectory, "expand.exe");
        if (File.Exists(expand))
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = expand,
                Arguments = $"\"{cabPath}\" -F:CatalogPC.xml \"{destDir}\\\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) throw new InvalidOperationException("Could not start expand.exe.");
            process.WaitForExit(120_000);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("expand.exe failed: " + process.StandardError.ReadToEnd());
            return;
        }

        string? cabextract = FindOnPath("cabextract");
        if (cabextract is null)
            throw new InvalidOperationException("No CAB extractor available (expand.exe / cabextract).");

        report?.Invoke("OEM / Dell: extracting CatalogPC with cabextract…");
        using var alt = Process.Start(new ProcessStartInfo
        {
            FileName = cabextract,
            Arguments = $"-d \"{destDir}\" -q \"{cabPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        });
        if (alt is null) throw new InvalidOperationException("Could not start cabextract.");
        alt.WaitForExit(120_000);
        if (alt.ExitCode != 0)
            throw new InvalidOperationException("cabextract failed: " + alt.StandardError.ReadToEnd());
    }

    private static string? FindOnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
                if (File.Exists(candidate + ".exe")) return candidate + ".exe";
            }
            catch { /* ignore */ }
        }
        return null;
    }

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("x-requested-with", "XMLHttpRequest");
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

    private static string? MatchGroup(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}
