using System.Text.Json;

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
    /// <summary>
    /// By profile name: the values that apply to the profile, Group Policy before MDM.
    /// A profile without an entry has no policy value.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<FirewallPolicyValue>> Values { get; init; } =
        new Dictionary<string, IReadOnlyList<FirewallPolicyValue>>(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyList<FirewallPolicyValue> For(string profile) =>
        Values.TryGetValue(profile, out var values) ? values : Array.Empty<FirewallPolicyValue>();
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
        return reading with { Policy = ReadPolicy(new MachineRegistry()) };
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
    /// Looks for the policy values that decide each profile. Group Policy keeps its values
    /// under SOFTWARE\Policies, an MDM service under the firewall service's Mdm key.
    /// The older policy template has one "standard" profile for every network that is not the
    /// domain. Windows applies it to the private and the public profile, but only when
    /// neither of those two has a policy key of its own; otherwise it is ignored ([MS-GPFAS],
    /// Per-Profile Policy Configuration Options).
    /// </summary>
    internal static FirewallPolicyReading ReadPolicy(IMachineRegistry store)
    {
        var values = new Dictionary<string, IReadOnlyList<FirewallPolicyValue>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            bool standardApplies = !store.KeyExists(GroupPolicyKey + @"\PrivateProfile") &&
                                   !store.KeyExists(GroupPolicyKey + @"\PublicProfile");
            foreach (string profile in ProfileNames)
            {
                var found = new List<FirewallPolicyValue>();
                string groupPolicy = profile != Domain && standardApplies ? "StandardProfile" : profile + "Profile";
                Add(found, store, GroupPolicyKey + @"\" + groupPolicy, "Group Policy");
                Add(found, store, MdmKey + @"\" + profile + "Profile", "MDM");
                if (found.Count > 0) values[profile] = found;
            }
        }
        catch (Exception ex) when (MachineRegistry.IsReadFailure(ex))
        {
            return new FirewallPolicyReading { Readable = false };
        }
        return new FirewallPolicyReading { Readable = true, Values = values };
    }

    private static void Add(List<FirewallPolicyValue> found, IMachineRegistry store, string key, string source)
    {
        if (store.ReadNumber(key, EnableFirewall) is int value)
            found.Add(new FirewallPolicyValue(value != 0, source, @"HKLM\" + key));
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
