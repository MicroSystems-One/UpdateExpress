using System.Globalization;

namespace UpdateExpress.Services.Security.Providers;

/// <summary>What Microsoft Defender Antivirus is doing on this machine, as far as it said.</summary>
internal enum DefenderRole
{
    /// <summary>Defender did not answer.</summary>
    Unread,
    /// <summary>Defender is the antivirus product that protects.</summary>
    Active,
    /// <summary>Defender has stepped back for another product and scans only on request.</summary>
    Passive,
    Off,
    /// <summary>Defender answered, but not whether it is on.</summary>
    Unclear
}

/// <summary>
/// END-001 to END-006 and UPD-SEC-008: the antivirus checks, all answered from one read.
/// Microsoft Defender Antivirus can be judged from what it says about itself. Another
/// product can only be seen as registered with Windows; whether it works and is up to date
/// is not readable from Windows, so its checks are unknown and never a pass or a failure.
/// </summary>
internal sealed class AntivirusCheckProvider : ISecurityCheckProvider
{
    internal const string ProductsId = "END-001";
    internal const string ProtectionId = "END-002";
    internal const string RealTimeId = "END-003";
    internal const string SignatureVersionId = "END-004";
    internal const string SignatureTimeId = "END-005";
    internal const string DefenderModeId = "END-006";
    internal const string SignatureAgeId = "UPD-SEC-008";

    internal const string DefenderName = "Microsoft Defender Antivirus";

    /// <summary>The header's Defender shield warns at 2 days and turns red at 7; the baseline carries the same numbers.</summary>
    private const double DefaultWarnAfterDays = 2;
    private const double DefaultCriticalAfterDays = 7;

    /// <summary>A clock that is a little behind the time of the definitions is not a reason to doubt them.</summary>
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(10);

    private static readonly string[] Ids =
    {
        ProductsId, ProtectionId, RealTimeId, SignatureVersionId, SignatureTimeId, DefenderModeId, SignatureAgeId
    };

    private readonly Func<CancellationToken, Task<AntivirusReading>> _read;

    internal AntivirusCheckProvider(Func<CancellationToken, Task<AntivirusReading>>? read = null) =>
        _read = read ?? AntivirusReader.ReadAsync;

    public string Name => "antivirus";

    public IReadOnlyList<string> CheckIds => Ids;

    /// <summary>The first PowerShell start on a slow disk can take longer than the engine's default.</summary>
    public TimeSpan? Timeout => TimeSpan.FromSeconds(30);

    public async Task<IReadOnlyList<SecurityCheckResult>> AssessAsync(SecurityCheckContext context, CancellationToken cancellationToken)
    {
        var reading = await _read(cancellationToken).ConfigureAwait(false);
        var now = context.Now == default ? DateTimeOffset.Now : context.Now;
        var limits = context.Baseline.Find(SignatureAgeId)?.Thresholds;
        return context.RequestedIds
            .Where(id => Ids.Contains(id, StringComparer.OrdinalIgnoreCase))
            .Select(id => Judge(id, reading, now, limits, context.Machine.IsServerOs == true))
            .ToArray();
    }

    internal static DefenderRole RoleOf(DefenderStatus? defender)
    {
        if (defender is null) return DefenderRole.Unread;
        string mode = defender.RunningMode;
        if (mode.Contains("Passive", StringComparison.OrdinalIgnoreCase) ||
            mode.Contains("EDR Block", StringComparison.OrdinalIgnoreCase))
            return DefenderRole.Passive;
        if (defender.AntivirusEnabled == false || defender.ServiceEnabled == false ||
            mode.Contains("Not running", StringComparison.OrdinalIgnoreCase))
            return DefenderRole.Off;
        return defender.AntivirusEnabled == true ? DefenderRole.Active : DefenderRole.Unclear;
    }

    /// <summary>The answer for one check from one reading.</summary>
    /// <param name="limits">The age limits of UPD-SEC-008 in days, from the baseline.</param>
    /// <param name="server">True on Windows Server, where Defender can be absent and there is no Security Center.</param>
    internal static SecurityCheckResult Judge(
        string id, AntivirusReading reading, DateTimeOffset now, IReadOnlyDictionary<string, double>? limits = null, bool server = false)
    {
        if (!reading.Succeeded)
        {
            return new SecurityCheckResult
            {
                Id = id,
                Status = SecurityAssessmentStatus.Error,
                ReasonCode = SecurityReasonCodes.AntivirusReadFailed,
                Detail = reading.Failure
            };
        }

        var view = new View(reading);
        DefenderPolicySetting? policy = id switch
        {
            ProtectionId or DefenderModeId => reading.Policy.Antivirus,
            RealTimeId => reading.Policy.RealTimeProtection,
            _ => null
        };

        // A server whose Defender is silent may have it removed, or replaced by a product that
        // Windows Server has no list to show. That is not a check that failed, and not a pass.
        bool silentOnServer = server && view.Role == DefenderRole.Unread && !view.ProductsKnown && id != ProductsId;

        SecurityCheckResult result;
        if (silentOnServer)
        {
            result = With(SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.DefenderSilentOnServer, review: id == ProtectionId);
        }
        else
        {
            result = id switch
            {
                ProductsId => Products(view),
                ProtectionId => Protection(view),
                RealTimeId => RealTime(view),
                SignatureVersionId => Reported(view, reading.Defender?.SignatureVersion ?? ""),
                SignatureTimeId => Reported(view, reading.Defender?.SignaturesUpdated is DateTime updated
                    ? updated.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                    : ""),
                DefenderModeId => DefenderMode(view),
                SignatureAgeId => SignatureAge(view, now, limits),
                _ => SecurityCheckResult.Unknown(id, SecurityReasonCodes.NoResult)
            };
        }

        return result with
        {
            Id = id,
            Policy = policy?.State ?? SecurityPolicyState.NotAssessed,
            Detail = Detail(reading, policy)
        };
    }

    /// <summary>The reading with the questions every check asks of it.</summary>
    private sealed class View
    {
        internal View(AntivirusReading reading)
        {
            Reading = reading;
            Role = RoleOf(reading.Defender);
            Others = (reading.Products ?? Array.Empty<AntivirusProduct>())
                .Where(product => !product.IsDefender)
                .Select(product => product.Name)
                .ToArray();
        }

        internal AntivirusReading Reading { get; }
        internal DefenderStatus? Defender => Reading.Defender;
        internal DefenderRole Role { get; }
        /// <summary>The registered products that are not Microsoft Defender Antivirus.</summary>
        internal IReadOnlyList<string> Others { get; }
        internal bool ProductsKnown => Reading.Products is not null;
        internal bool AnotherProductInCharge => Role != DefenderRole.Active && Others.Count > 0;
        internal string OtherNames => string.Join(", ", Others);
    }

    private static SecurityCheckResult With(
        SecurityAssessmentStatus status, string code = SecurityStateCodes.Unknown, string value = "", string reason = "", bool review = false) =>
        new() { Status = status, DetectedCode = code, DetectedValue = value, ReasonCode = reason, ManualReview = review };

    /// <summary>END-001: which products Windows Security Center knows.</summary>
    private static SecurityCheckResult Products(View view)
    {
        if (!view.ProductsKnown)
            return With(SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.AntivirusProductsUnreadable);

        if (view.Reading.Products!.Count == 0)
        {
            // Defender says it protects while the list is empty: the list is what is in doubt.
            return With(
                view.Role == DefenderRole.Active ? SecurityAssessmentStatus.Unknown : SecurityAssessmentStatus.NeedsAttention,
                SecurityStateCodes.NoneRegistered,
                reason: SecurityReasonCodes.AntivirusNoneRegistered);
        }

        return With(SecurityAssessmentStatus.Protected, SecurityStateCodes.Value,
            string.Join(", ", view.Reading.Products.Select(product => product.Name)));
    }

    /// <summary>END-002: whether an antivirus product is on.</summary>
    private static SecurityCheckResult Protection(View view)
    {
        if (view.Role == DefenderRole.Active)
            return With(SecurityAssessmentStatus.Protected, SecurityStateCodes.Active, DefenderName);

        if (view.Others.Count > 0)
        {
            // Registered is all Windows knows of the other product. A person has to look at it.
            return With(SecurityAssessmentStatus.Unknown, SecurityStateCodes.Value, view.OtherNames,
                SecurityReasonCodes.AntivirusOtherProduct, review: true);
        }

        switch (view.Role)
        {
            case DefenderRole.Off:
                if (view.Reading.Policy.Antivirus.HoldsOff)
                    return With(SecurityAssessmentStatus.ManagedByPolicy, SecurityStateCodes.Off, reason: SecurityReasonCodes.DefenderOffByPolicy);
                return view.ProductsKnown
                    ? With(SecurityAssessmentStatus.NotProtected, SecurityStateCodes.Off)
                    : With(SecurityAssessmentStatus.Unknown, SecurityStateCodes.Off, reason: SecurityReasonCodes.AntivirusProductsUnreadable, review: true);
            case DefenderRole.Passive:
                return view.ProductsKnown
                    ? With(SecurityAssessmentStatus.NeedsAttention, SecurityStateCodes.Value, view.Defender!.RunningMode, SecurityReasonCodes.DefenderPassiveNoProduct)
                    : With(SecurityAssessmentStatus.Unknown, SecurityStateCodes.Value, view.Defender!.RunningMode, SecurityReasonCodes.AntivirusProductsUnreadable, review: true);
            case DefenderRole.Unread:
                return With(SecurityAssessmentStatus.Error, reason: SecurityReasonCodes.DefenderReadFailed);
            default:
                return With(SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.DefenderValueMissing);
        }
    }

    /// <summary>END-003: whether files are scanned as they are opened.</summary>
    private static SecurityCheckResult RealTime(View view)
    {
        bool? realTime = view.Defender?.RealTimeProtectionEnabled;
        if (view.Role == DefenderRole.Active && realTime == true)
            return With(SecurityAssessmentStatus.Protected, SecurityStateCodes.On);
        if (view.AnotherProductInCharge)
            return With(SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.AntivirusOtherProductValue);
        if (view.Role == DefenderRole.Unread)
            return With(SecurityAssessmentStatus.Error, reason: SecurityReasonCodes.DefenderReadFailed);
        if (realTime is null)
            return With(SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.DefenderValueMissing);
        if (realTime == true)
        {
            // "On" from a Defender that is off or passive: the values contradict each other.
            return With(SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.DefenderStateUnclear);
        }

        if (view.Reading.Policy.RealTimeProtection.HoldsOff)
            return With(SecurityAssessmentStatus.ManagedByPolicy, SecurityStateCodes.Off, reason: SecurityReasonCodes.DefenderOffByPolicy);
        // A Defender in normal mode is the product in charge, whatever else is registered.
        return view.Role == DefenderRole.Active || view.ProductsKnown
            ? With(SecurityAssessmentStatus.NotProtected, SecurityStateCodes.Off)
            : With(SecurityAssessmentStatus.Unknown, SecurityStateCodes.Off, reason: SecurityReasonCodes.AntivirusProductsUnreadable, review: true);
    }

    /// <summary>END-004 and END-005: a value that is reported and not judged.</summary>
    private static SecurityCheckResult Reported(View view, string value)
    {
        // Defender's own definitions say nothing about the product that protects.
        if (view.AnotherProductInCharge)
            return With(SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.AntivirusOtherProductValue);
        if (view.Role == DefenderRole.Unread)
            return With(SecurityAssessmentStatus.Error, reason: SecurityReasonCodes.DefenderReadFailed);
        return value.Length > 0
            ? With(SecurityAssessmentStatus.NotApplicable, SecurityStateCodes.Value, value, SecurityReasonCodes.Informational)
            : With(SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.DefenderValueMissing);
    }

    /// <summary>END-006: the mode Defender runs in.</summary>
    private static SecurityCheckResult DefenderMode(View view)
    {
        string mode = view.Defender?.RunningMode ?? "";
        string code = mode.Length > 0 ? SecurityStateCodes.Value : SecurityStateCodes.Unknown;

        switch (view.Role)
        {
            case DefenderRole.Active:
                return mode.Length > 0
                    ? With(SecurityAssessmentStatus.Protected, SecurityStateCodes.Value, mode)
                    : With(SecurityAssessmentStatus.Protected, SecurityStateCodes.Active);
            case DefenderRole.Passive:
                if (view.Others.Count > 0) return With(SecurityAssessmentStatus.Protected, code, mode);
                return view.ProductsKnown
                    ? With(SecurityAssessmentStatus.NeedsAttention, code, mode, SecurityReasonCodes.DefenderPassiveNoProduct)
                    : With(SecurityAssessmentStatus.Unknown, code, mode, SecurityReasonCodes.AntivirusProductsUnreadable, review: true);
            case DefenderRole.Off:
                if (mode.Length == 0) code = SecurityStateCodes.Off;
                // Defender switching off for another product is how Windows is meant to work.
                if (view.Others.Count > 0)
                    return With(SecurityAssessmentStatus.NotApplicable, code, mode, SecurityReasonCodes.DefenderOffOtherProduct);
                if (view.Reading.Policy.Antivirus.HoldsOff)
                    return With(SecurityAssessmentStatus.ManagedByPolicy, code, mode, SecurityReasonCodes.DefenderOffByPolicy);
                return view.ProductsKnown
                    ? With(SecurityAssessmentStatus.NeedsAttention, code, mode, SecurityReasonCodes.DefenderOffNoProduct)
                    : With(SecurityAssessmentStatus.Unknown, code, mode, SecurityReasonCodes.AntivirusProductsUnreadable, review: true);
            case DefenderRole.Unread:
                // With another product registered a silent Defender is the usual picture, not a fault.
                return view.Others.Count > 0
                    ? With(SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.DefenderSilentOtherProduct)
                    : With(SecurityAssessmentStatus.Error, reason: SecurityReasonCodes.DefenderReadFailed);
            default:
                return With(SecurityAssessmentStatus.Unknown, code, mode, SecurityReasonCodes.DefenderValueMissing);
        }
    }

    /// <summary>UPD-SEC-008: how old Defender's definitions are.</summary>
    private static SecurityCheckResult SignatureAge(View view, DateTimeOffset now, IReadOnlyDictionary<string, double>? limits)
    {
        if (view.AnotherProductInCharge)
            return With(SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.AntivirusOtherProductValue);
        if (view.Role == DefenderRole.Unread)
            return With(SecurityAssessmentStatus.Error, reason: SecurityReasonCodes.DefenderReadFailed);

        // Defender's own age is in whole days; the time itself is exact at the 2-day limit.
        TimeSpan? age = view.Defender!.SignaturesUpdated is DateTime updated
            ? now.UtcDateTime - updated.ToUniversalTime()
            : view.Defender.SignatureAgeDays is int days ? TimeSpan.FromDays(days) : null;
        if (age is null)
            return With(SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.DefenderValueMissing);
        if (age.Value < -ClockTolerance)
            return With(SecurityAssessmentStatus.Unknown, reason: SecurityReasonCodes.DefenderStateUnclear);
        if (age.Value < TimeSpan.Zero) age = TimeSpan.Zero;

        double warn = Limit(limits, SecurityBaselineV1.WarnAfterDays, DefaultWarnAfterDays);
        double critical = Math.Max(warn, Limit(limits, SecurityBaselineV1.CriticalAfterDays, DefaultCriticalAfterDays));

        var status = age.Value.TotalDays >= critical ? SecurityAssessmentStatus.NotProtected
            : age.Value.TotalDays >= warn ? SecurityAssessmentStatus.NeedsAttention
            : SecurityAssessmentStatus.Protected;
        return age.Value < TimeSpan.FromHours(48)
            ? With(status, SecurityStateCodes.AgeHours, ((int)Math.Floor(age.Value.TotalHours)).ToString(CultureInfo.InvariantCulture))
            : With(status, SecurityStateCodes.AgeDays, ((int)Math.Floor(age.Value.TotalDays)).ToString(CultureInfo.InvariantCulture));
    }

    private static double Limit(IReadOnlyDictionary<string, double>? limits, string name, double fallback) =>
        limits is not null && limits.TryGetValue(name, out double value) && value > 0 ? value : fallback;

    private static string Detail(AntivirusReading reading, DefenderPolicySetting? policy)
    {
        var parts = new List<string>();

        if (reading.Defender is DefenderStatus defender)
        {
            parts.Add(
                $"{DefenderName}: mode {Given(defender.RunningMode)}, antivirus {OnOff(defender.AntivirusEnabled)}, " +
                $"real-time protection {OnOff(defender.RealTimeProtectionEnabled)}, service {OnOff(defender.ServiceEnabled)}, " +
                $"tamper protection {OnOff(defender.TamperProtected)}, definitions {Given(defender.SignatureVersion)} from " +
                (defender.SignaturesUpdated is DateTime updated ? updated.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "a time not given") + ".");
        }
        else
        {
            parts.Add(DefenderName + " did not answer" + (reading.DefenderFailure.Length > 0 ? ": " + reading.DefenderFailure.TrimEnd('.') + "." : "."));
        }

        if (reading.Products is null) parts.Add("Windows Security Center could not be read.");
        else if (reading.Products.Count == 0) parts.Add("Windows Security Center lists no antivirus product.");
        else
        {
            parts.Add("Registered with Windows Security Center: " + string.Join(", ", reading.Products.Select(product =>
                product.State is long state ? $"{product.Name} (state 0x{state:X6})" : product.Name)) + ".");
        }

        if (policy is not null)
        {
            foreach (var value in policy.Values)
                parts.Add($"{value.Source} sets this {(value.Enabled ? "on" : "off")} ({value.Location}).");
            if (!policy.Complete) parts.Add("Not every place a policy value can be in could be read.");
            else if (policy.Values.Count == 0) parts.Add("No policy value for this was found.");
        }

        return string.Join(" ", parts);
    }

    private static string OnOff(bool? value) => value switch { true => "on", false => "off", _ => "not given" };

    private static string Given(string value) => value.Length > 0 ? value : "not given";
}
