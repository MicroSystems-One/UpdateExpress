namespace UpdateExpress.Services.Security.Providers;

/// <summary>What the firmware and the TPM say. Null = not read.</summary>
internal sealed record DeviceSecurityReading
{
    /// <summary>1 = legacy BIOS, 2 = UEFI, as <see cref="Windows11Eligibility"/> reads it.</summary>
    public int? FirmwareType { get; init; }
    /// <summary>The Secure Boot state Windows holds: 1 = on, 0 = off, null = no state.</summary>
    public int? SecureBoot { get; init; }
    /// <summary>False when the Secure Boot state was not allowed to be read.</summary>
    public bool SecureBootReadable { get; init; } = true;
    /// <summary>2 = TPM 2.0, 1 = TPM 1.2, 0 = no TPM found.</summary>
    public int? TpmVersion { get; init; }
}

/// <summary>
/// DEV-003 to DEV-005: Secure Boot and the TPM. The firmware type and the TPM version are
/// the ones <see cref="Windows11Eligibility"/> already reads for the Windows 11 check; that
/// reader only notes that a Secure Boot state exists, so the state itself is read here.
/// All of it is firmware or hardware: reported, never changed.
/// </summary>
internal sealed class DeviceSecurityCheckProvider : ISecurityCheckProvider
{
    internal const string SecureBootId = "DEV-003";
    internal const string TpmPresentId = "DEV-004";
    internal const string TpmVersionId = "DEV-005";

    internal const string SecureBootStateKey = @"SYSTEM\CurrentControlSet\Control\SecureBoot\State";
    internal const string SecureBootStateValue = "UEFISecureBootEnabled";

    private static readonly string[] Ids = { SecureBootId, TpmPresentId, TpmVersionId };

    private readonly Func<DeviceSecurityReading> _read;

    internal DeviceSecurityCheckProvider(Func<DeviceSecurityReading>? read = null) =>
        _read = read ?? (() => Read(new MachineRegistry(), Windows11Eligibility.ReadFacts));

    public string Name => "device";

    public IReadOnlyList<string> CheckIds => Ids;

    public Task<IReadOnlyList<SecurityCheckResult>> AssessAsync(SecurityCheckContext context, CancellationToken cancellationToken)
    {
        var reading = _read();
        IReadOnlyList<SecurityCheckResult> results = context.RequestedIds
            .Where(id => Ids.Contains(id, StringComparer.OrdinalIgnoreCase))
            .Select(id => Judge(id, reading))
            .ToArray();
        return Task.FromResult(results);
    }

    internal static DeviceSecurityReading Read(IMachineRegistry registry, Func<Windows11Eligibility.Facts> facts)
    {
        var read = facts();
        int? secureBoot = null;
        bool readable = true;
        try
        {
            secureBoot = registry.ReadNumber(SecureBootStateKey, SecureBootStateValue);
        }
        catch (Exception ex) when (MachineRegistry.IsReadFailure(ex))
        {
            readable = false;
        }

        return new DeviceSecurityReading
        {
            FirmwareType = read.FirmwareType,
            SecureBoot = secureBoot,
            SecureBootReadable = readable,
            TpmVersion = read.TpmVersion
        };
    }

    internal static SecurityCheckResult Judge(string id, DeviceSecurityReading reading) => id switch
    {
        SecureBootId => SecureBoot(reading),
        TpmPresentId => TpmPresent(reading),
        TpmVersionId => TpmVersion(reading),
        _ => SecurityCheckResult.Unknown(id, SecurityReasonCodes.NoResult)
    };

    private static SecurityCheckResult SecureBoot(DeviceSecurityReading reading)
    {
        string firmware = reading.FirmwareType switch
        {
            Windows11Eligibility.FirmwareBios => "Firmware type: legacy BIOS.",
            Windows11Eligibility.FirmwareUefi => "Firmware type: UEFI.",
            int other => $"Firmware type: {other}.",
            null => "The firmware type could not be read."
        };
        string state = !reading.SecureBootReadable ? "The Secure Boot state could not be read."
            : reading.SecureBoot is int value ? $"{SecureBootStateValue} is {value}."
            : $"Windows holds no {SecureBootStateValue} value.";
        string detail = firmware + " " + state;

        // Secure Boot that is on says by itself that the firmware is UEFI.
        if (reading.SecureBoot is int on && on != 0)
            return With(SecureBootId, SecurityAssessmentStatus.Protected, SecurityStateCodes.On, detail: detail);
        if (reading.FirmwareType == Windows11Eligibility.FirmwareBios)
            return With(SecureBootId, SecurityAssessmentStatus.NotSupported, reason: SecurityReasonCodes.SecureBootLegacyBios, detail: detail);
        if (!reading.SecureBootReadable)
            return With(SecureBootId, SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.SecureBootUnreadable, detail: detail);
        if (reading.SecureBoot is null)
            return With(SecureBootId, SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.SecureBootNotReported, detail: detail);
        return With(SecureBootId, SecurityAssessmentStatus.NeedsAttention, SecurityStateCodes.Off, SecurityReasonCodes.SecureBootOff, detail);
    }

    private static SecurityCheckResult TpmPresent(DeviceSecurityReading reading) => reading.TpmVersion switch
    {
        null => With(TpmPresentId, SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.TpmNotRead, detail: TpmDetail(reading)),
        // No TPM is hardware the PC lacks or has switched off; neither is a setting that failed.
        0 => With(TpmPresentId, SecurityAssessmentStatus.NotSupported, SecurityStateCodes.NotFound, SecurityReasonCodes.TpmNotFound, TpmDetail(reading)),
        _ => With(TpmPresentId, SecurityAssessmentStatus.Protected, SecurityStateCodes.Present, detail: TpmDetail(reading))
    };

    private static SecurityCheckResult TpmVersion(DeviceSecurityReading reading) => reading.TpmVersion switch
    {
        null => With(TpmVersionId, SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.TpmNotRead, detail: TpmDetail(reading)),
        0 => With(TpmVersionId, SecurityAssessmentStatus.NotSupported, SecurityStateCodes.NotFound, SecurityReasonCodes.TpmNotFound, TpmDetail(reading)),
        1 => With(TpmVersionId, SecurityAssessmentStatus.NeedsAttention, SecurityStateCodes.Tpm12, SecurityReasonCodes.TpmOlderVersion, TpmDetail(reading)),
        2 => With(TpmVersionId, SecurityAssessmentStatus.Protected, SecurityStateCodes.Tpm20, detail: TpmDetail(reading)),
        _ => With(TpmVersionId, SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.TpmVersionNotKnown, detail: TpmDetail(reading))
    };

    private static string TpmDetail(DeviceSecurityReading reading) => reading.TpmVersion switch
    {
        null => "The TPM Base Services did not answer.",
        0 => "The TPM Base Services report that no TPM was found.",
        1 => "The TPM Base Services report TPM 1.2.",
        2 => "The TPM Base Services report TPM 2.0.",
        int other => $"The TPM Base Services report TPM version {other}."
    };

    private static SecurityCheckResult With(
        string id, SecurityAssessmentStatus status, string code = SecurityStateCodes.Unknown, string reason = "", string detail = "") =>
        new() { Id = id, Status = status, DetectedCode = code, ReasonCode = reason, Detail = detail };
}
