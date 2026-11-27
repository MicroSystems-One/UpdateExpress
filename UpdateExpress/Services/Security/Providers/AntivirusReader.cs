using System.Text.Json;

namespace UpdateExpress.Services.Security.Providers;

/// <summary>An antivirus product registered with Windows Security Center.</summary>
/// <param name="State">Security Center's own state number for the product, kept for the technical detail only.</param>
internal sealed record AntivirusProduct(string Name, long? State, bool IsDefender);

/// <summary>What Microsoft Defender Antivirus says about itself. Null = Defender gave no value.</summary>
internal sealed record DefenderStatus
{
    public bool? AntivirusEnabled { get; init; }
    public bool? RealTimeProtectionEnabled { get; init; }
    public bool? ServiceEnabled { get; init; }
    public bool? TamperProtected { get; init; }
    /// <summary>Defender's own word: "Normal", "Passive Mode", "EDR Block Mode", "SxS Passive Mode", "Not running".</summary>
    public string RunningMode { get; init; } = "";
    public string SignatureVersion { get; init; } = "";
    /// <summary>Local time.</summary>
    public DateTime? SignaturesUpdated { get; init; }
    /// <summary>Whole days, as Defender counts them. Used only when the time itself is missing.</summary>
    public int? SignatureAgeDays { get; init; }
}

/// <summary>A value a policy sets for a Defender setting.</summary>
/// <param name="Enabled">What the policy asks for: true = the protection on, false = off.</param>
/// <param name="Source">"Group Policy" or "MDM".</param>
internal sealed record DefenderPolicyValue(bool Enabled, string Source, string Location);

/// <summary>What policy says about one Defender setting, as far as it could be read.</summary>
internal sealed record DefenderPolicySetting
{
    public IReadOnlyList<DefenderPolicyValue> Values { get; init; } = Array.Empty<DefenderPolicyValue>();
    /// <summary>False when a place a policy value can be in could not be read.</summary>
    public bool Complete { get; init; }

    internal SecurityPolicyState State =>
        Values.Count > 0 ? SecurityPolicyState.Managed
        : Complete ? SecurityPolicyState.NotManaged
        : SecurityPolicyState.Unknown;

    internal bool HoldsOff => Values.Any(value => !value.Enabled);
}

internal sealed record DefenderPolicyReading
{
    public DefenderPolicySetting Antivirus { get; init; } = new();
    public DefenderPolicySetting RealTimeProtection { get; init; } = new();
}

/// <summary>One read of the antivirus state of the machine.</summary>
internal sealed record AntivirusReading
{
    /// <summary>False when PowerShell gave no answer that could be understood; then <see cref="Failure"/> says why.</summary>
    public bool Succeeded { get; init; }
    public string Failure { get; init; } = "";
    /// <summary>Null when Microsoft Defender Antivirus did not answer; <see cref="DefenderFailure"/> says why.</summary>
    public DefenderStatus? Defender { get; init; }
    public string DefenderFailure { get; init; } = "";
    /// <summary>
    /// The products registered with Windows Security Center, or null when it could not be
    /// read (Windows Server has no Security Center).
    /// </summary>
    public IReadOnlyList<AntivirusProduct>? Products { get; init; }
    public DefenderPolicyReading Policy { get; init; } = new();

    internal static AntivirusReading Failed(string why) => new() { Succeeded = false, Failure = why };
}

/// <summary>
/// Reads what Microsoft Defender Antivirus says about itself and which antivirus products
/// Windows Security Center knows. It only reads: no setting, scan or definition update is
/// started. The existing <see cref="DefenderStatusReader"/> for the header gives one verdict;
/// the checks need the values behind it and a read that can be cancelled.
/// </summary>
internal static class AntivirusReader
{
    /// <summary>The fixed id Windows Security Center lists Microsoft Defender Antivirus under.</summary>
    internal const string DefenderProductId = "D68DDC3A-831F-4FAE-9E44-DA132C1ACF46";

    internal const string GroupPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows Defender";
    internal const string GroupPolicyRealTimeKey = GroupPolicyKey + @"\Real-Time Protection";
    /// <summary>Where an MDM service such as Intune leaves its Defender settings.</summary>
    internal const string MdmKey = GroupPolicyKey + @"\Policy Manager";

    /// <summary>
    /// One answer as JSON. Defender and Security Center are asked separately, so that one
    /// that does not answer leaves the other's answer standing. The time of the definitions is
    /// printed in round-trip form; ConvertTo-Json would otherwise make it "/Date(...)/".
    /// </summary>
    internal const string Script =
        SecurityPowerShell.Utf8Output +
        "$ErrorActionPreference='Stop'; " +
        "$mp=$null; $mpError=$null; " +
        "try { $raw=Get-MpComputerStatus; $mp=[pscustomobject]@{ " +
        "AntivirusEnabled=$raw.AntivirusEnabled; RealTimeProtectionEnabled=$raw.RealTimeProtectionEnabled; " +
        "ServiceEnabled=$raw.AMServiceEnabled; TamperProtected=$raw.IsTamperProtected; " +
        "RunningMode=[string]$raw.AMRunningMode; SignatureVersion=[string]$raw.AntivirusSignatureVersion; " +
        "SignaturesUpdated= if ($raw.AntivirusSignatureLastUpdated) { ([datetime]$raw.AntivirusSignatureLastUpdated).ToString('o') } else { $null }; " +
        "SignatureAge=$raw.AntivirusSignatureAge } } catch { $mpError=[string]$_.Exception.Message }; " +
        "$products=$null; " +
        "try { $products=@(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntivirusProduct | ForEach-Object { " +
        "[pscustomobject]@{ Name=[string]$_.displayName; State=$_.productState; Id=[string]$_.instanceGuid } }) } catch { }; " +
        "[pscustomobject]@{ Defender=$mp; DefenderError=$mpError; Products=$products } | ConvertTo-Json -Compress -Depth 4";

    internal static async Task<AntivirusReading> ReadAsync(CancellationToken cancellationToken)
    {
        var answer = await SecurityPowerShell.RunAsync(Script, cancellationToken).ConfigureAwait(false);
        var reading = Parse(answer);
        if (!reading.Succeeded) return reading;
        return reading with { Policy = ReadPolicy(new MachineRegistry()) };
    }

    /// <summary>Turns PowerShell's answer into a reading. A value it cannot make sense of stays unknown; it never becomes "off".</summary>
    internal static AntivirusReading Parse(PowerShellAnswer answer)
    {
        if (!answer.Ran) return AntivirusReading.Failed("PowerShell could not be started. " + answer.Error);

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
            if (root is not { ValueKind: JsonValueKind.Object })
            {
                return AntivirusReading.Failed(answer.ExitCode != 0
                    ? $"PowerShell ended with code {answer.ExitCode}. {answer.Error}".Trim()
                    : "The answer from Windows could not be understood.");
            }

            DefenderStatus? defender = null;
            if (root.Value.TryGetProperty("Defender", out var status) && status.ValueKind == JsonValueKind.Object)
            {
                defender = new DefenderStatus
                {
                    AntivirusEnabled = Flag(status, "AntivirusEnabled"),
                    RealTimeProtectionEnabled = Flag(status, "RealTimeProtectionEnabled"),
                    ServiceEnabled = Flag(status, "ServiceEnabled"),
                    TamperProtected = Flag(status, "TamperProtected"),
                    RunningMode = Text(status, "RunningMode"),
                    SignatureVersion = Text(status, "SignatureVersion"),
                    SignaturesUpdated = DefenderStatusReader.TryParseSignatureTime(Text(status, "SignaturesUpdated")),
                    SignatureAgeDays = Days(status, "SignatureAge")
                };
            }

            List<AntivirusProduct>? products = null;
            if (root.Value.TryGetProperty("Products", out var listed) && listed.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            {
                products = new List<AntivirusProduct>();
                foreach (var item in listed.ValueKind == JsonValueKind.Array ? listed.EnumerateArray().ToArray() : new[] { listed })
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    string name = Text(item, "Name");
                    string id = Text(item, "Id").Trim('{', '}');
                    if (name.Length == 0 && id.Length == 0) continue;
                    bool isDefender = IsDefender(name, id);
                    long? state = item.TryGetProperty("State", out var number) && number.TryGetInt64(out long value) ? value : null;
                    if (products.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.IsDefender == isDefender)) continue;
                    products.Add(new AntivirusProduct(name.Length > 0 ? name : id, state, isDefender));
                }
            }

            // Neither answering is still an answer: on Windows Server without Defender that is
            // the normal picture, and the checks say what it means there.
            return new AntivirusReading
            {
                Succeeded = true,
                Defender = defender,
                DefenderFailure = defender is null ? SecurityPowerShell.FirstLine(Text(root.Value, "DefenderError")) : "",
                Products = products
            };
        }
    }

    /// <summary>
    /// Whether a registered product is Microsoft Defender Antivirus. The id decides. The name
    /// only counts when there is no id, and then only as the whole name: "Bitdefender" has
    /// "defender" in it and is another product.
    /// </summary>
    internal static bool IsDefender(string name, string id)
    {
        id = (id ?? "").Trim().Trim('{', '}');
        if (id.Length > 0) return id.Equals(DefenderProductId, StringComparison.OrdinalIgnoreCase);
        name = (name ?? "").Trim();
        return name.Equals("Windows Defender", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(AntivirusCheckProvider.DefenderName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Looks for policy values that hold Defender's settings. Group Policy keeps "Disable…"
    /// values (1 = off) under SOFTWARE\Policies; an MDM service keeps "Allow…" values
    /// (0 = off) in the Policy Manager key below it, which only an administrator may read.
    /// A place that may not be read leaves the setting's policy state unknown unless another
    /// place already holds a value.
    /// </summary>
    internal static DefenderPolicyReading ReadPolicy(IMachineRegistry registry)
    {
        var antivirus = new List<DefenderPolicyValue>();
        bool antivirusComplete = Look(registry, GroupPolicyKey, "DisableAntiSpyware", "Group Policy", disables: true, antivirus);

        var realTime = new List<DefenderPolicyValue>();
        bool realTimeComplete = Look(registry, GroupPolicyRealTimeKey, "DisableRealtimeMonitoring", "Group Policy", disables: true, realTime);
        realTimeComplete &= Look(registry, MdmKey, "AllowRealtimeMonitoring", "MDM", disables: false, realTime);

        return new DefenderPolicyReading
        {
            Antivirus = new DefenderPolicySetting { Values = antivirus, Complete = antivirusComplete },
            RealTimeProtection = new DefenderPolicySetting { Values = realTime, Complete = realTimeComplete }
        };
    }

    /// <returns>False when the place could not be read.</returns>
    private static bool Look(IMachineRegistry registry, string key, string name, string source, bool disables, List<DefenderPolicyValue> found)
    {
        try
        {
            if (registry.ReadNumber(key, name) is int value)
                found.Add(new DefenderPolicyValue(disables ? value == 0 : value != 0, source, @"HKLM\" + key + @"\" + name));
            return true;
        }
        catch (Exception ex) when (MachineRegistry.IsReadFailure(ex))
        {
            return false;
        }
    }

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "").Trim() : "";

    private static bool? Flag(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out bool parsed) => parsed,
            _ => null
        };
    }

    /// <summary>Defender gives 4294967295 for "no definitions"; that and anything else out of range is no age.</summary>
    private static int? Days(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out long days) && days is >= 0 and <= 36500 ? (int)days : null;
}
