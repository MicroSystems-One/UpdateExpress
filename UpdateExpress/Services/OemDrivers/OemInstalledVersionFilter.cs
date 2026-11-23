using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using UpdateExpress.Models;

namespace UpdateExpress.Services.OemDrivers;

/// <summary>
/// Drop OEM catalog offers that are older than or equal to what is already installed,
/// and drop older siblings when a newer package for the same component is also listed.
/// </summary>
internal static class OemInstalledVersionFilter
{
    private sealed record InstalledDriver(string Name, string Version);
    private sealed record Snapshot(string BiosVersion, IReadOnlyList<InstalledDriver> Drivers);

    internal static IReadOnlyList<UpdateEntry> Filter(
        IReadOnlyList<UpdateEntry> entries,
        Action<string>? report = null)
    {
        if (entries.Count == 0) return entries;

        Snapshot snapshot;
        try { snapshot = ReadSnapshot(); }
        catch (Exception ex)
        {
            report?.Invoke("OEM version filter: installed inventory unavailable (" + OemCatalogHelpers.Short(ex.Message) + ") — sibling prune only.");
            snapshot = new Snapshot("", Array.Empty<InstalledDriver>());
        }

        var kept = new List<UpdateEntry>(entries.Count);
        int droppedInstalled = 0;
        foreach (var entry in entries)
        {
            string offered = entry.UnifiedVersion;
            string? installed = ResolveInstalled(entry, snapshot);
            if (!string.IsNullOrWhiteSpace(installed))
                entry.InstalledDriverVersion = installed;

            if (!OemVersionComparer.IsBlank(installed) &&
                !OemVersionComparer.IsBlank(offered) &&
                OemVersionComparer.TryCompare(offered, installed) is <= 0)
            {
                droppedInstalled++;
                continue;
            }

            if (!OemVersionComparer.IsBlank(installed) && !OemVersionComparer.IsBlank(offered))
            {
                int? cmp = OemVersionComparer.TryCompare(offered, installed);
                if (cmp > 0)
                    entry.DriverReason = string.IsNullOrWhiteSpace(entry.DriverReason)
                        ? $"Offered version is newer than installed {installed}."
                        : entry.DriverReason;
            }

            kept.Add(entry);
        }

        int beforeSiblings = kept.Count;
        kept = DropOlderSiblings(kept);
        int droppedSiblings = beforeSiblings - kept.Count;

        if (droppedInstalled > 0 || droppedSiblings > 0)
        {
            report?.Invoke(
                $"OEM version filter: kept {kept.Count} (dropped {droppedInstalled} ≤ installed, {droppedSiblings} older sibling(s))." +
                (string.IsNullOrWhiteSpace(snapshot.BiosVersion) ? "" : $" BIOS installed={snapshot.BiosVersion}."));
        }

        return kept;
    }

    private static List<UpdateEntry> DropOlderSiblings(List<UpdateEntry> entries)
    {
        var groups = entries
            .Select((entry, index) => (entry, index))
            .GroupBy(x => ComponentKey(x.entry));

        var drop = new HashSet<int>();
        foreach (var group in groups)
        {
            var members = group.ToList();
            if (members.Count < 2) continue;
            for (int i = 0; i < members.Count; i++)
            {
                for (int j = 0; j < members.Count; j++)
                {
                    if (i == j) continue;
                    string left = members[i].entry.UnifiedVersion;
                    string right = members[j].entry.UnifiedVersion;
                    int? cmp = OemVersionComparer.TryCompare(left, right);
                    // Drop the older sibling when versions are comparable.
                    if (cmp < 0)
                        drop.Add(members[i].index);
                }
            }
        }

        if (drop.Count == 0) return entries;
        return entries.Where((_, index) => !drop.Contains(index)).ToList();
    }

    private static string ComponentKey(UpdateEntry entry)
    {
        string brand = (entry.DriverManufacturer ?? "").Trim().ToUpperInvariant();
        // All BIOS/UEFI flash packages for a brand target the same system firmware slot.
        if (IsBios(entry))
            return "bios|" + brand;

        string category = (entry.DriverClass ?? "").Trim().ToUpperInvariant();
        string title = OemVersionComparer.NormalizeComponentTitle(entry.Title);
        return "drv|" + brand + "|" + category + "|" + title;
    }

    private static bool IsBios(UpdateEntry entry) =>
        (entry.DriverClass ?? "").Contains("BIOS", StringComparison.OrdinalIgnoreCase) ||
        (entry.DriverClass ?? "").Contains("UEFI", StringComparison.OrdinalIgnoreCase) ||
        (entry.Title ?? "").Contains("BIOS", StringComparison.OrdinalIgnoreCase) ||
        (entry.Title ?? "").Contains("UEFI", StringComparison.OrdinalIgnoreCase) ||
        (entry.Title ?? "").Contains("Flash BIOS", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveInstalled(UpdateEntry entry, Snapshot snapshot)
    {
        // Only system BIOS offers compare against Win32_BIOS / registry BIOSVersion.
        if (IsBios(entry))
            return string.IsNullOrWhiteSpace(snapshot.BiosVersion) ? null : snapshot.BiosVersion;

        string offered = entry.UnifiedVersion;
        if (OemVersionComparer.IsBlank(offered) || snapshot.Drivers.Count == 0)
            return null;

        var titleTokens = SignificantTokens(entry.Title);
        if (titleTokens.Count == 0) return null;

        var matches = snapshot.Drivers
            .Where(d => titleTokens.Count(t =>
                d.Name.Contains(t, StringComparison.OrdinalIgnoreCase)) >= Math.Min(2, titleTokens.Count))
            .ToList();

        // Fail closed when the device match is ambiguous.
        if (matches.Count == 0) return null;
        if (matches.Count > 1)
        {
            // Prefer a unique version among matches; otherwise do not guess.
            var distinct = matches.Select(m => m.Version).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count != 1) return null;
            return distinct[0];
        }

        return matches[0].Version;
    }

    private static List<string> SignificantTokens(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return new();
        string cleaned = OemVersionComparer.NormalizeComponentTitle(title);
        return cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3)
            .Where(t => t is not ("THE" or "AND" or "FOR" or "UPDATE" or "DRIVER" or "FIRMWARE" or "FLASH"))
            .Take(6)
            .ToList();
    }

    private static Snapshot ReadSnapshot()
    {
        string bios = ReadBiosVersion();
        var drivers = ReadPnPDrivers();
        return new Snapshot(bios, drivers);
    }

    private static string ReadBiosVersion()
    {
        string registry = "";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            if (key is not null)
            {
                registry = Prefer(
                    key.GetValue("BIOSVersion") as string,
                    key.GetValue("SystemBiosVersion") as string);
                if (key.GetValue("SystemBiosVersion") is string[] multi)
                    registry = Prefer(registry, string.Join(" ", multi.Where(s => !string.IsNullOrWhiteSpace(s))));
            }
        }
        catch { /* fall through to CIM */ }

        string cim = "";
        try
        {
            const string script = """
                $ErrorActionPreference='Stop'
                $bios = Get-CimInstance -ClassName Win32_BIOS
                [pscustomobject]@{
                  Smbios = [string]$bios.SMBIOSBIOSVersion
                  Name = [string]$bios.Name
                  Version = [string]$bios.Version
                } | ConvertTo-Json -Compress
                """;
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
            }
            else
            {
                string output = outputTask.GetAwaiter().GetResult();
                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    using var json = JsonDocument.Parse(output);
                    var root = json.RootElement;
                    cim = Prefer(
                        JsonText(root, "Smbios"),
                        JsonText(root, "Name"),
                        JsonText(root, "Version"));
                }
            }
        }
        catch { /* ignore */ }

        return Prefer(cim, registry);
    }

    private static List<InstalledDriver> ReadPnPDrivers()
    {
        // Reuse the same CIM class as InstalledDriverReader (name + version only for OEM title match).
        const string script =
            "$ErrorActionPreference='Stop'; @(Get-CimInstance Win32_PnPSignedDriver | Where-Object { $_.DeviceName -and $_.DriverVersion } | ForEach-Object { [pscustomobject]@{ Name=[string]$_.DeviceName; Version=[string]$_.DriverVersion } }) | ConvertTo-Json -Compress -Depth 3";
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
                RedirectStandardError = false
            }
        };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(20000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return new();
        }
        string output = outputTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return new();

        using var json = JsonDocument.Parse(output);
        var result = new List<InstalledDriver>();
        var elements = json.RootElement.ValueKind == JsonValueKind.Array
            ? json.RootElement.EnumerateArray().ToArray()
            : new[] { json.RootElement };
        foreach (var item in elements)
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string name = item.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
            string version = item.TryGetProperty("Version", out var v) ? v.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version)) continue;
            result.Add(new InstalledDriver(name.Trim(), version.Trim()));
        }
        return result;
    }

    private static string Prefer(params string?[] values)
    {
        foreach (var value in values)
        {
            if (OemVersionComparer.IsBlank(value)) continue;
            return value!.Trim();
        }
        return "";
    }

    private static string JsonText(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
