using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace UpdateExpress.Services.Security.Providers;

/// <summary>A value a policy sets for one firewall profile.</summary>
/// <param name="Source">"Group Policy" or "MDM".</param>
/// <param name="Location">The registry key the value was found in, for the technical detail.</param>
internal sealed record FirewallPolicyValue(bool Enabled, string Source, string Location);

/// <summary>What policy says about the firewall profiles, as far as it could be read.</summary>
internal sealed record FirewallPolicyReading
{
    /// <summary>False when the policy keys could not be read; then nothing is known about policy.</summary>
    public bool Readable { get; init; }
    /// <summary>By profile name. A profile without an entry has no policy value.</summary>
    public IReadOnlyDictionary<string, FirewallPolicyValue> Values { get; init; } =
        new Dictionary<string, FirewallPolicyValue>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Profiles for which a value was found that may or may not apply to them.</summary>
    public IReadOnlyCollection<string> Unclear { get; init; } = Array.Empty<string>();
}

/// <summary>One read of the Windows Firewall profiles.</summary>
internal sealed record FirewallReading
{
    /// <summary>False when Windows did not answer; then <see cref="Failure"/> says why.</summary>
    public bool Succeeded { get; init; }
    public string Failure { get; init; } = "";
    /// <summary>
    /// The effective state by profile name (Domain, Private, Public): what applies after
    /// local settings and policy are put together. Null for a state Windows did not give.
    /// </summary>
    public IReadOnlyDictionary<string, bool?> Effective { get; init; } =
        new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
    /// <summary>The profiles in use on a network right now, or null when that was not readable.</summary>
    public IReadOnlyCollection<string>? InUse { get; init; }
    /// <summary>
    /// Other firewall products registered with Windows Security Center, or null when that
    /// could not be read (Windows Server has no Security Center).
    /// </summary>
    public IReadOnlyList<string>? OtherProducts { get; init; }
    public FirewallPolicyReading Policy { get; init; } = new();

    internal static FirewallReading Failed(string why) => new() { Succeeded = false, Failure = why };
}

/// <summary>
/// Reads the Windows Firewall profiles. It asks for the active policy store, which holds the
/// effective state: a profile that Group Policy switches on or off shows as it really is, not
/// as it is set locally. It only reads; no rule, profile or setting is touched.
/// </summary>
internal static class FirewallReader
{
    internal const string Domain = "Domain";
    internal const string Private = "Private";
    internal const string Public = "Public";
    internal static readonly IReadOnlyList<string> ProfileNames = new[] { Domain, Private, Public };

    internal const string GroupPolicyKey = @"SOFTWARE\Policies\Microsoft\WindowsFirewall";
    internal const string MdmKey = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\Mdm";
    private const string EnableFirewall = "EnableFirewall";

    /// <summary>
    /// One answer as JSON: the three profiles from the active store, the profiles in use, and
    /// the firewall products Windows Security Center knows. The last two are extras; when they
    /// cannot be read the answer still stands. A failure of the profile read itself comes back
    /// as {"Error": "..."} so that the reason is one clean line.
    /// </summary>
    internal const string Script =
        SecurityPowerShell.Utf8Output +
        "$ErrorActionPreference='Stop'; " +
        "try { " +
        "$profiles=@(Get-NetFirewallProfile -PolicyStore ActiveStore | ForEach-Object { [pscustomobject]@{ Name=[string]$_.Name; Enabled=[string]$_.Enabled } }); " +
        "$current=$null; try { $current=[string](Get-NetFirewallSetting -PolicyStore ActiveStore).ActiveProfile } catch { }; " +
        "$products=$null; try { $products=@(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName FirewallProduct | ForEach-Object { [string]$_.displayName }) } catch { }; " +
        "[pscustomobject]@{ Profiles=$profiles; Current=$current; Products=$products } | ConvertTo-Json -Compress -Depth 4 " +
        "} catch { [pscustomobject]@{ Error=[string]$_.Exception.Message } | ConvertTo-Json -Compress; exit 1 }";

    internal static async Task<FirewallReading> ReadAsync(CancellationToken cancellationToken)
    {
        var answer = await SecurityPowerShell.RunAsync(Script, cancellationToken).ConfigureAwait(false);
        var reading = Parse(answer);
        if (!reading.Succeeded) return reading;
        return reading with { Policy = ReadPolicy(ReadRegistryValue) };
    }

    /// <summary>Turns PowerShell's answer into a reading. Anything it cannot make sense of is a failed read, never "off".</summary>
    internal static FirewallReading Parse(PowerShellAnswer answer)
    {
        if (!answer.Ran) return FirewallReading.Failed("PowerShell could not be started. " + answer.Error);

        string json = (answer.Output ?? "").Trim().TrimStart('\uFEFF').Trim();
        JsonDocument? document = null;
        try
        {
            if (json.Length > 0) document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // Handled below as an answer that could not be understood.
        }

        using (document)
        {
            var root = document?.RootElement;
            if (root is { ValueKind: JsonValueKind.Object } &&
                root.Value.TryGetProperty("Error", out var error) && error.ValueKind == JsonValueKind.String)
                return FirewallReading.Failed(SecurityPowerShell.FirstLine(error.GetString()));
            if (answer.ExitCode != 0)
                return FirewallReading.Failed($"PowerShell ended with code {answer.ExitCode}. {answer.Error}".Trim());
            if (root is not { ValueKind: JsonValueKind.Object })
                return FirewallReading.Failed("The answer from Windows could not be understood.");

            var effective = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
            if (root.Value.TryGetProperty("Profiles", out var profiles))
            {
                foreach (var item in Items(profiles))
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    string name = Text(item, "Name");
                    if (name.Length == 0) continue;
                    effective[name] = Flag(item, "Enabled");
                }
            }
            if (effective.Count == 0)
                return FirewallReading.Failed("Windows named no firewall profile.");

            IReadOnlyCollection<string>? inUse = null;
            string current = Text(root.Value, "Current");
            if (current.Length > 0)
            {
                // "Public", or several: "Domain, Private".
                inUse = current.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            List<string>? products = null;
            if (root.Value.TryGetProperty("Products", out var listed) && listed.ValueKind is JsonValueKind.Array or JsonValueKind.String)
            {
                products = Items(listed)
                    .Where(p => p.ValueKind == JsonValueKind.String)
                    .Select(p => (p.GetString() ?? "").Trim())
                    .Where(p => p.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return new FirewallReading { Succeeded = true, Effective = effective, InUse = inUse, OtherProducts = products };
        }
    }

    /// <summary>
    /// Looks for a policy value per profile. Group Policy keeps its values under
    /// SOFTWARE\Policies, an MDM service under the firewall service's Mdm key.
    /// </summary>
    /// <param name="readValue">Reads a DWORD under HKEY_LOCAL_MACHINE: key path, value name; null when the key or value is absent. Throws when it may not be read.</param>
    internal static FirewallPolicyReading ReadPolicy(Func<string, string, int?> readValue)
    {
        var values = new Dictionary<string, FirewallPolicyValue>(StringComparer.OrdinalIgnoreCase);
        var unclear = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string profile in ProfileNames)
            {
                var found = Find(readValue, GroupPolicyKey + @"\" + profile + "Profile", "Group Policy")
                            ?? Find(readValue, MdmKey + @"\" + profile + "Profile", "MDM");
                if (found is not null) values[profile] = found;
            }

            // The older policy template has one "standard" profile for every network that is
            // not the domain. Windows keeps the private profile under that name. Whether such a
            // value also decides the public profile is not something to assume.
            var standard = Find(readValue, GroupPolicyKey + @"\StandardProfile", "Group Policy");
            if (standard is not null)
            {
                values.TryAdd(Private, standard);
                if (!values.ContainsKey(Public)) unclear.Add(Public);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new FirewallPolicyReading { Readable = false };
        }
        return new FirewallPolicyReading { Readable = true, Values = values, Unclear = unclear };
    }

    private static FirewallPolicyValue? Find(Func<string, string, int?> readValue, string key, string source) =>
        readValue(key, EnableFirewall) is int value ? new FirewallPolicyValue(value != 0, source, @"HKLM\" + key) : null;

    private static int? ReadRegistryValue(string key, string name)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var opened = machine.OpenSubKey(key, writable: false);
        return opened?.GetValue(name) switch
        {
            int number => number,
            long number => (int)number,
            string text when int.TryParse(text, out int number) => number,
            _ => null
        };
    }

    private static IEnumerable<JsonElement> Items(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray().ToArray() : new[] { element };

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "").Trim() : "";

    /// <summary>True, false, or null for anything else: "NotConfigured", a missing value, a type nobody expected.</summary>
    private static bool? Flag(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out bool parsed) => parsed,
            JsonValueKind.Number when value.TryGetInt32(out int number) && number is 0 or 1 => number == 1,
            _ => null
        };
    }
}
