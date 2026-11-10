using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace UpdateExpress.Services;

/// <summary>
/// SMBIOS identity for the PC running Update Express.
/// Shown in the window only. Do not write these values into logs or diagnostics.
/// </summary>
internal static class MachineIdentityReader
{
    internal sealed record Identity(string Manufacturer, string Model, string Product, string Serial, string DeviceName);

    internal static Identity ReadFast()
    {
        var registry = ReadRegistry();
        return Present(registry.Manufacturer, registry.Model, registry.Product, registry.Serial);
    }

    internal static Identity Read()
    {
        var registry = ReadRegistry();
        var firmware = ReadFirmware();
        return Present(
            First(firmware.Manufacturer, registry.Manufacturer),
            First(firmware.Model, registry.Model),
            First(firmware.Product, registry.Product),
            First(firmware.Serial, registry.Serial));
    }

    private static Identity Present(string manufacturer, string model, string product, string serial) =>
        new(Show(manufacturer), Show(model), Show(product), Show(serial), Show(Environment.MachineName));

    private static string Show(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private readonly record struct Raw(string Manufacturer, string Model, string Product, string Serial);

    private static Raw ReadRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            if (key is null) return default;
            string manufacturer = Clean(key.GetValue("SystemManufacturer") as string);
            string model = Clean(key.GetValue("SystemProductName") as string);
            string product = Clean(key.GetValue("SystemSKU") as string);
            return new(manufacturer, model, product, "");
        }
        catch { return default; }
    }

    private static Raw ReadFirmware()
    {
        const string script = """
            $ErrorActionPreference='Stop'
            $cs = Get-CimInstance -ClassName Win32_ComputerSystem
            $bios = Get-CimInstance -ClassName Win32_BIOS
            $prod = Get-CimInstance -ClassName Win32_ComputerSystemProduct
            [pscustomobject]@{
              Manufacturer = [string]$cs.Manufacturer
              Model = [string]$cs.Model
              Sku = [string]$prod.SKUNumber
              ProductName = [string]$prod.Name
              ProductVersion = [string]$prod.Version
              Serial = [string]$bios.SerialNumber
              IdentifyingNumber = [string]$prod.IdentifyingNumber
            } | ConvertTo-Json -Compress
            """;
        try
        {
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -EncodedCommand " + encoded,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                }
            };
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(12000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return default;
            }
            string output = outputTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return default;
            using var json = JsonDocument.Parse(output);
            var root = json.RootElement;
            string manufacturer = Clean(JsonText(root, "Manufacturer"));
            string model = Clean(JsonText(root, "Model"));
            string sku = Clean(JsonText(root, "Sku"));
            string productName = Clean(JsonText(root, "ProductName"));
            string productVersion = Clean(JsonText(root, "ProductVersion"));
            string serial = Clean(JsonText(root, "Serial"));
            string identifying = Clean(JsonText(root, "IdentifyingNumber"));
            if (IsGuid(productName)) productName = "";
            if (IsGuid(identifying)) identifying = "";
            // SKU is the product number HP and Dell support pages ask for.
            // Lenovo often leaves SKU empty and puts the machine type in Name.
            string product = First(sku, Different(productName, model), Code(productVersion, model));
            return new(manufacturer, model, product, First(serial, identifying));
        }
        catch { return default; }
    }

    private static string JsonText(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string First(params string[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        return "";
    }

    private static string Different(string value, string other) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), other.Trim(), StringComparison.OrdinalIgnoreCase)
            ? ""
            : value;

    private static string Code(string value, string model)
    {
        value = Different(value, model);
        return value.Any(char.IsDigit) ? value : "";
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = value.Trim();
        if (text.Equals("None", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Default string", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("System Serial Number", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("System Manufacturer", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("System Product Name", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Not Specified", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Not Available", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("O.E.M.", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Chassis Serial Number", StringComparison.OrdinalIgnoreCase) ||
            text.All(c => c == '0'))
            return "";
        return text;
    }

    private static bool IsGuid(string value) => Guid.TryParse(value, out _);
}
