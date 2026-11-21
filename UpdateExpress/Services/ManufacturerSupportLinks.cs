namespace UpdateExpress.Services;

/// <summary>
/// Maps known PC OEMs to a support URL for the header button.
/// Prefer product/MTM/serial deep-links when the vendor supports them.
/// </summary>
internal static class ManufacturerSupportLinks
{
    internal sealed record SupportLink(string Brand, string Url);

    /// <summary>
    /// Returns true when Manufacturer matches a known OEM we show a button for.
    /// Generic/virtual makers (QEMU, VMware, …) return false so the button stays hidden.
    /// </summary>
    internal static bool TryResolve(
        string manufacturer,
        string product,
        string model,
        string serial,
        out SupportLink? link)
    {
        link = null;
        if (string.IsNullOrWhiteSpace(manufacturer) || IsPlaceholder(manufacturer))
            return false;

        string raw = manufacturer.Trim();
        if (IsGenericOrVirtual(raw))
            return false;

        string productId = Prefer(product, model);
        string serialId = Prefer(serial);

        if (ContainsAny(raw, "lenovo"))
        {
            // Produktnr / MTM (e.g. 10T7006XMX) deep-links warranty + drivers.
            string query = Prefer(product, serialId, model);
            link = new("Lenovo", LenovoUrl(query));
            return true;
        }

        if (ContainsAny(raw, "dell"))
        {
            // Dell service tag is the serial number on most PCs.
            link = new("Dell",
                !string.IsNullOrEmpty(serialId)
                    ? $"https://www.dell.com/support/home/en-us/product-support/servicetag/{Uri.EscapeDataString(serialId)}/overview"
                    : "https://www.dell.com/support/home");
            return true;
        }

        if (ContainsAny(raw, "hewlett-packard", "hewlett packard", "hp inc") ||
            string.Equals(raw, "HP", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "HPI", StringComparison.OrdinalIgnoreCase))
        {
            string query = Prefer(serialId, productId);
            link = new("HP",
                !string.IsNullOrEmpty(query)
                    ? $"https://support.hp.com/us-en/search?q={Uri.EscapeDataString(query)}"
                    : "https://support.hp.com/");
            return true;
        }

        if (ContainsAny(raw, "asus", "asustek"))
        {
            string query = Prefer(productId, serialId);
            link = new("ASUS",
                !string.IsNullOrEmpty(query)
                    ? $"https://www.asus.com/support/search/?SearchKey={Uri.EscapeDataString(query)}"
                    : "https://www.asus.com/support/");
            return true;
        }

        if (ContainsAny(raw, "samsung"))
        {
            string query = Prefer(model, product, serialId);
            link = new("Samsung",
                !string.IsNullOrEmpty(query)
                    ? $"https://www.samsung.com/us/support/search/?keyword={Uri.EscapeDataString(query)}"
                    : "https://www.samsung.com/us/support/");
            return true;
        }

        if (ContainsAny(raw, "acer"))
        {
            string query = Prefer(serialId, productId);
            link = new("Acer",
                !string.IsNullOrEmpty(query)
                    ? $"https://www.acer.com/us-en/search?q={Uri.EscapeDataString(query)}"
                    : "https://www.acer.com/us-en/support");
            return true;
        }

        return false;
    }

    private static string LenovoUrl(string query) =>
        !string.IsNullOrEmpty(query)
            ? $"https://pcsupport.lenovo.com/us/en/products?searchType=search&searchValue={Uri.EscapeDataString(query)}"
            : "https://pcsupport.lenovo.com/";

    private static bool ContainsAny(string manufacturer, params string[] tokens)
    {
        foreach (var token in tokens)
            if (manufacturer.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool IsGenericOrVirtual(string manufacturer) =>
        ContainsAny(manufacturer,
            "qemu", "innotek", "virtualbox", "vmware", "xen", "bochs", "parallels",
            "microsoft corporation", "standard pc", "to be filled", "system manufacturer",
            "o.e.m", "not specified", "not available", "default string");

    private static bool IsPlaceholder(string value) =>
        value is "—" or "…" or "-" or "N/A" or "n/a";

    private static string Prefer(params string[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var text = value.Trim();
            if (IsPlaceholder(text)) continue;
            return text;
        }
        return "";
    }
}
