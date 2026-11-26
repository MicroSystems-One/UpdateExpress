using System.Net.Http;

namespace UpdateExpress.Services.OemDrivers;

internal static class OemCatalogHelpers
{
    internal static bool IsMissing(string? value) =>
        string.IsNullOrWhiteSpace(value) || value is "—" or "…" or "-" or "N/A" or "n/a";

    internal static string Prefer(params string?[] values)
    {
        foreach (var value in values)
        {
            if (IsMissing(value)) continue;
            return value!.Trim();
        }
        return "";
    }

    internal static string Short(string message)
    {
        message = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 120 ? message : message[..117] + "…";
    }

    internal static HttpClient CreateClient(string referer, string brand)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        if (!string.IsNullOrWhiteSpace(referer))
            client.DefaultRequestHeaders.Referrer = new Uri(referer);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "UpdateExpress/" + UpdateExpress.ProductVersion.Display +
            " (+https://github.com/MicroSystems-One/UpdateExpress; OEM/" + brand + ")");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/html, */*");
        return client;
    }

    internal static string NormalizeModelKey(string value)
    {
        if (IsMissing(value)) return "";
        var chars = value.Trim().ToUpperInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var text = new string(chars);
        while (text.Contains("--", StringComparison.Ordinal))
            text = text.Replace("--", "-", StringComparison.Ordinal);
        return text.Trim('-');
    }

    internal static bool LooksLikeDellSystemId(string value) =>
        !IsMissing(value) &&
        value.Trim().Length is >= 3 and <= 5 &&
        value.Trim().All(c => Uri.IsHexDigit(c));

    internal static string CurrentWindowsFamily()
    {
        try
        {
            var version = Environment.OSVersion.Version;
            if (version.Major >= 10 && version.Build >= 22000)
                return "Windows 11";
        }
        catch { /* fall through */ }
        return "Windows 10";
    }
}
