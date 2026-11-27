using System.Globalization;

namespace UpdateExpress.Services.Security.Providers;

/// <summary>
/// What Windows holds about automatic logon. Only the switch is read as a value; of
/// everything else only whether it is there. The password is never read.
/// </summary>
internal sealed record AutoLogonReading
{
    public bool Readable { get; init; }
    /// <summary>The text of AutoAdminLogon, or null when Windows holds no such value.</summary>
    public string? Switch { get; init; }
    /// <summary>A DefaultPassword value exists. Its data was not read.</summary>
    public bool PasswordValuePresent { get; init; }
    /// <summary>An AutoLogonCount value exists: Windows signs in by itself a set number of times only.</summary>
    public bool Counted { get; init; }
}

/// <summary>
/// ACC-007: whether Windows signs a user in by itself. The answer comes from the switch and
/// from the names of the values beside it. The stored password is a secret: this check sees
/// that a value of that name exists and never opens it.
/// </summary>
internal sealed class AutoLogonCheckProvider : ISecurityCheckProvider
{
    internal const string Id = "ACC-007";
    internal const string WinlogonKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    internal const string SwitchValue = "AutoAdminLogon";
    internal const string PasswordValue = "DefaultPassword";
    internal const string CountValue = "AutoLogonCount";
    private const string NotText = "(not text)";

    private readonly Func<AutoLogonReading> _read;

    internal AutoLogonCheckProvider(Func<AutoLogonReading>? read = null) =>
        _read = read ?? (() => Read(new MachineRegistry()));

    public string Name => "auto-logon";

    public IReadOnlyList<string> CheckIds { get; } = new[] { Id };

    public Task<IReadOnlyList<SecurityCheckResult>> AssessAsync(SecurityCheckContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<SecurityCheckResult> results = context.RequestedIds
            .Where(id => string.Equals(id, Id, StringComparison.OrdinalIgnoreCase))
            .Select(_ => Judge(_read()))
            .ToArray();
        return Task.FromResult(results);
    }

    internal static AutoLogonReading Read(IMachineRegistry registry)
    {
        try
        {
            var names = registry.ValueNames(WinlogonKey);
            if (names is null) return new AutoLogonReading();
            bool Has(string name) => names.Contains(name, StringComparer.OrdinalIgnoreCase);
            return new AutoLogonReading
            {
                Readable = true,
                // A value that is there but is neither text nor a number says neither on nor off.
                Switch = Has(SwitchValue) ? registry.ReadText(WinlogonKey, SwitchValue) ?? NotText : null,
                PasswordValuePresent = Has(PasswordValue),
                Counted = Has(CountValue)
            };
        }
        catch (Exception ex) when (MachineRegistry.IsReadFailure(ex))
        {
            return new AutoLogonReading();
        }
    }

    /// <summary>On, off, or null for a value that says neither. Windows reads the switch as a number.</summary>
    internal static bool? IsOn(string? text)
    {
        if (text is null) return false;
        text = text.Trim();
        if (text.Length == 0) return false;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number != 0 : null;
    }

    internal static SecurityCheckResult Judge(AutoLogonReading reading)
    {
        if (!reading.Readable)
        {
            return With(SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.AutoLogonUnreadable,
                detail: "The Winlogon key could not be read.");
        }

        string detail = Detail(reading);
        return IsOn(reading.Switch) switch
        {
            null => With(SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.AutoLogonValueUnclear, detail: detail),
            // A password in the registry can be read by every user of the PC; that is the worse of the two.
            true => reading.PasswordValuePresent
                ? With(SecurityAssessmentStatus.NotProtected, SecurityStateCodes.Configured, SecurityReasonCodes.AutoLogonPasswordInRegistry, detail)
                : With(SecurityAssessmentStatus.NeedsAttention, SecurityStateCodes.Configured, SecurityReasonCodes.AutoLogonOn, detail),
            false => reading.PasswordValuePresent
                ? With(SecurityAssessmentStatus.NeedsAttention, SecurityStateCodes.NotConfigured, SecurityReasonCodes.AutoLogonPasswordLeftBehind, detail)
                : With(SecurityAssessmentStatus.Protected, SecurityStateCodes.NotConfigured, detail: detail)
        };
    }

    private static string Detail(AutoLogonReading reading)
    {
        var parts = new List<string>
        {
            reading.Switch is null
                ? $"Windows holds no {SwitchValue} value."
                // The switch is a number; anything else is not repeated here.
                : IsOn(reading.Switch) is bool on ? $"{SwitchValue} is {(on ? "on" : "off")}." : $"{SwitchValue} holds a value that is not a number.",
            reading.PasswordValuePresent
                ? $"A {PasswordValue} value exists; it was not read."
                : $"No {PasswordValue} value exists."
        };
        if (reading.Counted) parts.Add($"An {CountValue} value exists: Windows signs in by itself a set number of times only.");
        if (IsOn(reading.Switch) == true && !reading.PasswordValuePresent)
            parts.Add("Without that value the password, if the account has one, is kept by Windows as a protected secret.");
        return string.Join(" ", parts);
    }

    private static SecurityCheckResult With(
        SecurityAssessmentStatus status, string code = SecurityStateCodes.Unknown, string reason = "", string detail = "") =>
        new() { Id = Id, Status = status, DetectedCode = code, ReasonCode = reason, Detail = detail };
}
