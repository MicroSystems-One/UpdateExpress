using System;
using System.Collections.Generic;
using System.Globalization;

namespace UpdateExpress.Services.Security;

/// <summary>
/// English and Norwegian text for the Security Overview. Same pattern as
/// <see cref="AppLanguage"/>: the English text is the key of the Norwegian text. Results and
/// the cache hold codes only, so a change of language also changes what an older assessment
/// reads.
/// </summary>
internal static class SecurityOverviewStrings
{
    internal const string Title = "Security Overview";
    internal const string HonestyLine = "Assessment snapshot – not a security certification.";

    /// <summary>Texts of the window and of the status shield in the main window's header.</summary>
    internal static class Ui
    {
        internal const string LastAssessed = "Last assessed: {0}";
        internal const string Progress = "Checking… {0} of {1}";
        internal const string ShownUntilAnswered = "Results from {0} are shown until each check has answered.";
        internal const string Cancelled = "The assessment was cancelled.";
        internal const string ShowingAssessmentFrom = "Showing the assessment from {0}.";
        internal const string EmptyCategory = "No checks in this category in this version.";
        internal const string ScanAgain = "Scan again";
        internal const string Cancel = "Cancel";
        internal const string Close = "Close";
        internal const string Details = "Details";
        internal const string DetailsFor = "Details for {0}";
        internal const string CurrentState = "Current state";
        internal const string Recommended = "Recommended";
        internal const string Policy = "Policy";
        internal const string Checked = "Checked";
        internal const string NotCheckedYet = "Not checked yet";
        internal const string Remediation = "Remediation";
        internal const string RemediationAssessmentOnly = "Assessment only";
        internal const string RemediationNotInThisVersion = "Not available in this version";
        internal const string RemediationAvailable = "Available";
        internal const string Restart = "Restart";
        internal const string RestartNeeded = "Required";
        internal const string RestartNotNeeded = "Not required";
        internal const string WhyItMatters = "Why it matters";
        internal const string PolicyNote = "About policy";
        internal const string Finding = "Finding";
        internal const string TechnicalDetail = "Technical detail";
        internal const string CheckId = "Check ID";
        internal const string Severity = "Severity";
        internal const string SeverityInformational = "Informational";
        internal const string SeverityLow = "Low";
        internal const string SeverityMedium = "Medium";
        internal const string SeverityHigh = "High";
        internal const string MoreInformation = "More information";
        internal const string ManualReviewNote = "This finding needs a person to judge it.";
        /// <summary>The status of a check that reports a value and judges nothing.</summary>
        internal const string StatusInformation = "Information";
        internal const string BaselineNote = "The checks and recommendations are Update Express's own selection ({0}). The assessment only reads; it changes nothing on this PC.";
        internal const string ShieldStatus = "Security Overview status";
        internal const string ShieldOpen = "Click to open the Security Overview.";
        internal const string SummaryCards = "Summary";
        internal const string CategoryFilter = "Category";
        internal const string CheckList = "Security checks";
    }

    internal static string T(string english) => T(english, AppLanguage.IsNorwegian);

    /// <summary>A text with "{0}" places filled in, in the chosen language.</summary>
    internal static string Tf(string english, bool norwegian, params object[] values) =>
        string.Format(CultureInfo.InvariantCulture, T(english, norwegian), values);

    internal static string SeverityEnglish(SecuritySeverity severity) => severity switch
    {
        SecuritySeverity.High => Ui.SeverityHigh,
        SecuritySeverity.Medium => Ui.SeverityMedium,
        SecuritySeverity.Low => Ui.SeverityLow,
        _ => Ui.SeverityInformational
    };

    internal static string Severity(SecuritySeverity severity, bool norwegian) => T(SeverityEnglish(severity), norwegian);

    internal static string T(string english, bool norwegian) =>
        norwegian && Norwegian.TryGetValue(english, out var text) ? text : english;

    internal static bool HasNorwegian(string english) => Norwegian.ContainsKey(english);

    internal static string StatusEnglish(SecurityAssessmentStatus status) => status switch
    {
        SecurityAssessmentStatus.Protected => "Protected",
        SecurityAssessmentStatus.NotProtected => "Not protected",
        SecurityAssessmentStatus.NeedsAttention => "Needs attention",
        SecurityAssessmentStatus.ManagedByPolicy => "Managed by policy",
        SecurityAssessmentStatus.RestartRequired => "Restart required",
        SecurityAssessmentStatus.NotSupported => "Not supported",
        SecurityAssessmentStatus.NotApplicable => "Not applicable",
        SecurityAssessmentStatus.Checking => "Checking…",
        SecurityAssessmentStatus.Error => "Check failed",
        _ => "Unknown"
    };

    internal static string CategoryEnglish(SecurityCheckCategory category) => category switch
    {
        SecurityCheckCategory.NetworkSecurity => "Network Security",
        SecurityCheckCategory.EndpointProtection => "Endpoint Protection",
        SecurityCheckCategory.DeviceSecurity => "Device Security",
        SecurityCheckCategory.IdentityAndAccess => "Identity and Access",
        SecurityCheckCategory.UpdatesAndMaintenance => "Updates and Maintenance",
        _ => "Logging and Application Security"
    };

    internal static string BucketEnglish(SecuritySummaryBucket bucket) => bucket switch
    {
        SecuritySummaryBucket.Protected => "Protected",
        SecuritySummaryBucket.NeedsAttention => "Needs attention",
        SecuritySummaryBucket.ManualReview => "Manual review",
        SecuritySummaryBucket.RestartRequired => "Restart required",
        SecuritySummaryBucket.NotApplicable => "Not applicable",
        _ => "Unknown or unsupported"
    };

    internal static string PolicyEnglish(SecurityPolicyState policy) => policy switch
    {
        SecurityPolicyState.NotManaged => "Not managed by policy",
        SecurityPolicyState.Managed => "Managed by policy",
        SecurityPolicyState.Unknown => "Unknown",
        _ => "Not assessed"
    };

    /// <summary>English text for a state code, or null for a code this table does not know.</summary>
    internal static string? StateEnglish(string code) => code switch
    {
        SecurityStateCodes.Unknown => "Unknown",
        SecurityStateCodes.Value => "{0}",
        SecurityStateCodes.On => "On",
        SecurityStateCodes.Off => "Off",
        SecurityStateCodes.Active => "Active",
        SecurityStateCodes.Present => "Present",
        SecurityStateCodes.NotConfigured => "Not configured",
        SecurityStateCodes.Supported => "Supported by Microsoft",
        SecurityStateCodes.Informational => "No recommended value (information only)",
        SecurityStateCodes.AntivirusRegistered => "At least one antivirus product registered",
        SecurityStateCodes.DefenderNormalOrPassive => "Normal, or passive when another antivirus product protects the PC",
        SecurityStateCodes.Tpm20 => "TPM 2.0",
        SecurityStateCodes.NoSecurityUpdatesWaiting => "No security updates waiting",
        SecurityStateCodes.NoRestartPending => "No restart pending",
        SecurityStateCodes.WithinDays => "Within the last {0} days",
        SecurityStateCodes.NoneRegistered => "None registered",
        SecurityStateCodes.AgeHours => "Updated {0} h ago",
        SecurityStateCodes.AgeDays => "Updated {0} days ago",
        SecurityStateCodes.Configured => "Configured",
        SecurityStateCodes.NotFound => "Not found",
        SecurityStateCodes.Tpm12 => "TPM 1.2",
        SecurityStateCodes.InstalledOn => "Installed {0}",
        SecurityStateCodes.SecurityUpdatesWaiting => "Waiting: {0}",
        SecurityStateCodes.RestartPending => "Restart pending",
        SecurityStateCodes.SupportEnded => "Support has ended",
        _ => null
    };

    /// <summary>English text for a reason code, or null for a code this table does not know.</summary>
    internal static string? ReasonEnglish(string code) => code switch
    {
        SecurityReasonCodes.Timeout => "The check did not answer in time.",
        SecurityReasonCodes.Cancelled => "The assessment was cancelled before this check finished.",
        SecurityReasonCodes.ProviderError => "The check could not run.",
        SecurityReasonCodes.NoResult => "The check gave no answer.",
        SecurityReasonCodes.ServerOs => "Not relevant on Windows Server.",
        SecurityReasonCodes.ClientOs => "Only relevant on Windows Server.",
        SecurityReasonCodes.OsTooOld => "Not supported on this Windows version.",
        SecurityReasonCodes.FirewallReadFailed => "Windows did not answer the question about the firewall.",
        SecurityReasonCodes.FirewallProfileNotReported => "Windows gave no state for this firewall profile.",
        SecurityReasonCodes.FirewallOffByPolicy =>
            "A policy holds Windows Firewall off for this profile. Update Express does not override policy.",
        SecurityReasonCodes.FirewallOffOtherProduct =>
            "Windows Firewall is off for this profile, and another firewall product is registered with Windows. Whether that product protects this profile cannot be verified here.",
        SecurityReasonCodes.Informational => "Reported for information. Nothing is judged.",
        SecurityReasonCodes.AntivirusReadFailed => "Windows did not answer the question about antivirus protection.",
        SecurityReasonCodes.AntivirusProductsUnreadable =>
            "Windows Security Center could not be read, so it is not known which antivirus products are registered.",
        SecurityReasonCodes.AntivirusNoneRegistered => "Windows Security Center lists no antivirus product.",
        SecurityReasonCodes.AntivirusOtherProduct =>
            "Another antivirus product is registered with Windows. Windows cannot show whether it works and is up to date; check that in the product itself.",
        SecurityReasonCodes.AntivirusOtherProductValue =>
            "Another antivirus product is registered with Windows, and this cannot be read for that product.",
        SecurityReasonCodes.DefenderReadFailed => "Microsoft Defender Antivirus did not answer.",
        SecurityReasonCodes.DefenderValueMissing => "Microsoft Defender Antivirus gave no value for this.",
        SecurityReasonCodes.DefenderStateUnclear => "Microsoft Defender Antivirus gave values that do not fit together.",
        SecurityReasonCodes.DefenderPassiveNoProduct =>
            "Microsoft Defender Antivirus is in passive mode, and Windows Security Center lists no other antivirus product.",
        SecurityReasonCodes.DefenderOffOtherProduct =>
            "Microsoft Defender Antivirus is switched off, and another antivirus product is registered. Windows does that by design.",
        SecurityReasonCodes.DefenderOffNoProduct =>
            "Microsoft Defender Antivirus is switched off, and Windows Security Center lists no other antivirus product.",
        SecurityReasonCodes.DefenderSilentOtherProduct =>
            "Microsoft Defender Antivirus did not answer, and another antivirus product is registered. Defender normally switches off then.",
        SecurityReasonCodes.DefenderSilentOnServer =>
            "Microsoft Defender Antivirus did not answer. On Windows Server it can be removed or replaced by another product, and Windows Server keeps no list of antivirus products.",
        SecurityReasonCodes.DefenderOffByPolicy =>
            "A policy holds this Microsoft Defender Antivirus setting off. Update Express does not override policy.",
        SecurityReasonCodes.SecureBootLegacyBios => "This PC starts in legacy BIOS mode, which cannot use Secure Boot.",
        SecurityReasonCodes.SecureBootNotReported =>
            "Windows holds no Secure Boot state for this PC. Its firmware may not offer Secure Boot.",
        SecurityReasonCodes.SecureBootUnreadable => "The Secure Boot state could not be read.",
        SecurityReasonCodes.SecureBootOff =>
            "Secure Boot is switched off. It is switched on in the firmware setup of the PC, not in Windows.",
        SecurityReasonCodes.TpmNotRead => "Windows did not answer the question about the TPM.",
        SecurityReasonCodes.TpmNotFound => "Windows finds no TPM. The PC has none, or it is switched off in the firmware setup.",
        SecurityReasonCodes.TpmOlderVersion => "The TPM follows version 1.2 of the specification. Windows 11 requires version 2.0.",
        SecurityReasonCodes.TpmVersionNotKnown => "The TPM reports a specification version that Update Express does not know.",
        SecurityReasonCodes.AutoLogonUnreadable => "The settings for automatic logon could not be read.",
        SecurityReasonCodes.AutoLogonValueUnclear => "Windows holds a value for automatic logon that is neither on nor off.",
        SecurityReasonCodes.AutoLogonOn =>
            "Automatic logon is on: anyone who can switch this PC on is signed in. That can be intended, for instance on a kiosk or a display PC.",
        SecurityReasonCodes.AutoLogonPasswordInRegistry =>
            "Automatic logon is on, and its password is stored in the registry, where it can be read. Update Express has not read it.",
        SecurityReasonCodes.AutoLogonPasswordLeftBehind =>
            "Automatic logon is off, but a stored password is left in the registry, where it can be read. Update Express has not read it.",
        SecurityReasonCodes.InstalledUpdatesReadFailed => "Windows did not answer the question about installed updates.",
        SecurityReasonCodes.NoSecurityUpdateListed =>
            "Windows lists no installed security update. That can be so right after Windows was installed or upgraded, so it is not a sign that updates are missing.",
        SecurityReasonCodes.SecurityUpdateDateUnclear => "Windows gave no usable date for its installed security updates.",
        SecurityReasonCodes.SecurityUpdateOverdue =>
            "No security update has been installed within the recommended time. Search for updates in the main window.",
        SecurityReasonCodes.NoUpdateSearchYet =>
            "Update Express has not searched for updates since it was started. Search for updates in the main window, then scan again here.",
        SecurityReasonCodes.UpdateSearchTooOld =>
            "The last search for updates in Update Express is too old to rely on. Search again in the main window, then scan again here.",
        SecurityReasonCodes.SecurityUpdatesNotInstalled =>
            "Security updates are offered and not installed yet. Install them from the main window.",
        SecurityReasonCodes.RestartIndicatorsUnreadable => "The restart indicators of Windows could not be read.",
        SecurityReasonCodes.RestartForFileOperations =>
            "File operations wait for the next restart. An installer left them; they are not always from a Windows update.",
        SecurityReasonCodes.WindowsVersionUnreadable => "Windows did not say which edition and version it is.",
        SecurityReasonCodes.WindowsSupportNotVerified =>
            "Whether this Windows release still gets security updates could not be verified here. The technical detail says why.",
        SecurityReasonCodes.WindowsSupportEnded =>
            "Microsoft's regular security updates for this Windows release have ended. A separate agreement such as Extended Security Updates cannot be verified here.",
        _ => null
    };

    internal static string OverallEnglish(string reasonCode) => reasonCode switch
    {
        SecurityOverallStatus.NothingAssessable => "No check could be assessed on this PC.",
        SecurityOverallStatus.NotProtected => "At least one check is not protected.",
        SecurityOverallStatus.NeedsAttention => "At least one check needs attention.",
        SecurityOverallStatus.RestartRequired => "A restart is required.",
        SecurityOverallStatus.CheckError => "At least one check could not run.",
        SecurityOverallStatus.OnlyUnknown => "No check could confirm protection.",
        SecurityOverallStatus.Stale => "The last assessment is more than {0} days old.",
        SecurityOverallStatus.AllClear => "No check found a problem.",
        _ => "Not assessed yet."
    };

    internal static string Status(SecurityAssessmentStatus status, bool norwegian) => T(StatusEnglish(status), norwegian);
    internal static string Category(SecurityCheckCategory category, bool norwegian) => T(CategoryEnglish(category), norwegian);
    internal static string Bucket(SecuritySummaryBucket bucket, bool norwegian) => T(BucketEnglish(bucket), norwegian);
    internal static string Policy(SecurityPolicyState policy, bool norwegian) => T(PolicyEnglish(policy), norwegian);

    /// <summary>
    /// A detected or recommended state as text. A code without a text of its own shows the
    /// value, or "Unknown" when there is none; it never shows as a state it is not.
    /// </summary>
    internal static string State(string? code, string? value, bool norwegian)
    {
        value = (value ?? "").Trim();
        string? english = StateEnglish((code ?? "").Trim());
        if (english is null) return value.Length > 0 ? value : T("Unknown", norwegian);
        string text = T(english, norwegian);
        if (text.Contains("{0}", StringComparison.Ordinal))
            return value.Length > 0 ? string.Format(CultureInfo.InvariantCulture, text, value) : T("Unknown", norwegian);
        return value.Length > 0 ? text + ": " + value : text;
    }

    /// <summary>Text for a reason code; empty for no reason, the code itself for one without a text.</summary>
    internal static string Reason(string? code, bool norwegian)
    {
        code = (code ?? "").Trim();
        if (code.Length == 0) return "";
        string? english = ReasonEnglish(code);
        return english is null ? code : T(english, norwegian);
    }

    internal static string Overall(SecurityOverall overall, bool norwegian)
    {
        string text = T(OverallEnglish(overall.ReasonCode), norwegian);
        return text.Contains("{0}", StringComparison.Ordinal)
            ? string.Format(CultureInfo.InvariantCulture, text, (int)SecurityOverallStatus.StaleAfter.TotalDays)
            : text;
    }

    private static readonly Dictionary<string, string> Norwegian = new(StringComparer.Ordinal)
    {
        [Title] = "Sikkerhetsoversikt",
        [HonestyLine] = "Øyeblikksbilde av en vurdering – ikke en sikkerhetssertifisering.",

        // Assessment status
        ["Protected"] = "Beskyttet",
        ["Not protected"] = "Ikke beskyttet",
        ["Needs attention"] = "Krever oppmerksomhet",
        ["Managed by policy"] = "Styrt av policy",
        ["Restart required"] = "Omstart kreves",
        ["Not supported"] = "Støttes ikke",
        ["Not applicable"] = "Ikke relevant",
        ["Unknown"] = "Ukjent",
        ["Checking…"] = "Sjekker…",
        ["Check failed"] = "Sjekken feilet",

        // Summary cards
        ["Manual review"] = "Manuell vurdering",
        ["Unknown or unsupported"] = "Ukjent eller ikke støttet",

        // Policy management
        ["Not managed by policy"] = "Ikke styrt av policy",
        ["Not assessed"] = "Ikke vurdert",

        // Categories
        ["All"] = "Alle",
        ["Network Security"] = "Nettverkssikkerhet",
        ["Endpoint Protection"] = "Endepunktsbeskyttelse",
        ["Device Security"] = "Enhetssikkerhet",
        ["Identity and Access"] = "Identitet og tilgang",
        ["Updates and Maintenance"] = "Oppdateringer og vedlikehold",
        ["Logging and Application Security"] = "Logging og programsikkerhet",

        // Detected and recommended states
        ["On"] = "På",
        ["Off"] = "Av",
        ["Active"] = "Aktiv",
        ["Present"] = "Til stede",
        ["Not configured"] = "Ikke konfigurert",
        ["Supported by Microsoft"] = "Støttet av Microsoft",
        ["No recommended value (information only)"] = "Ingen anbefalt verdi (kun informasjon)",
        ["At least one antivirus product registered"] = "Minst ett antivirusprodukt registrert",
        ["Normal, or passive when another antivirus product protects the PC"] =
            "Normal, eller passiv når et annet antivirusprodukt beskytter PC-en",
        ["No security updates waiting"] = "Ingen sikkerhetsoppdateringer venter",
        ["No restart pending"] = "Ingen omstart venter",
        ["Within the last {0} days"] = "Innen de siste {0} dagene",
        ["None registered"] = "Ingen registrert",
        ["Updated {0} h ago"] = "Oppdatert for {0} t siden",
        ["Updated {0} days ago"] = "Oppdatert for {0} dager siden",
        ["Configured"] = "Konfigurert",
        ["Not found"] = "Ikke funnet",
        ["Installed {0}"] = "Installert {0}",
        ["Waiting: {0}"] = "Venter: {0}",
        ["Restart pending"] = "Omstart venter",
        ["Support has ended"] = "Støtten er avsluttet",

        // Reasons
        ["The check did not answer in time."] = "Sjekken svarte ikke i tide.",
        ["The assessment was cancelled before this check finished."] = "Vurderingen ble avbrutt før denne sjekken var ferdig.",
        ["The check could not run."] = "Sjekken kunne ikke kjøres.",
        ["The check gave no answer."] = "Sjekken ga ikke noe svar.",
        ["Not relevant on Windows Server."] = "Ikke relevant på Windows Server.",
        ["Only relevant on Windows Server."] = "Bare relevant på Windows Server.",
        ["Not supported on this Windows version."] = "Støttes ikke på denne Windows-versjonen.",
        ["Windows did not answer the question about the firewall."] = "Windows svarte ikke på spørsmålet om brannmuren.",
        ["Windows gave no state for this firewall profile."] = "Windows oppga ingen tilstand for denne brannmurprofilen.",
        ["A policy holds Windows Firewall off for this profile. Update Express does not override policy."] =
            "En policy holder Windows-brannmuren av for denne profilen. Update Express overstyrer ikke policy.",
        ["Windows Firewall is off for this profile, and another firewall product is registered with Windows. Whether that product protects this profile cannot be verified here."] =
            "Windows-brannmuren er av for denne profilen, og et annet brannmurprodukt er registrert i Windows. Om det produktet beskytter denne profilen, kan ikke bekreftes her.",
        ["Reported for information. Nothing is judged."] = "Rapporteres til informasjon. Ingenting vurderes.",
        ["Windows did not answer the question about antivirus protection."] = "Windows svarte ikke på spørsmålet om antivirusbeskyttelse.",
        ["Windows Security Center could not be read, so it is not known which antivirus products are registered."] =
            "Windows Sikkerhetssenter kunne ikke leses, så det er ikke kjent hvilke antivirusprodukter som er registrert.",
        ["Windows Security Center lists no antivirus product."] = "Windows Sikkerhetssenter viser ingen antivirusprodukter.",
        ["Another antivirus product is registered with Windows. Windows cannot show whether it works and is up to date; check that in the product itself."] =
            "Et annet antivirusprodukt er registrert i Windows. Windows kan ikke vise om det virker og er oppdatert; kontroller det i produktet selv.",
        ["Another antivirus product is registered with Windows, and this cannot be read for that product."] =
            "Et annet antivirusprodukt er registrert i Windows, og dette kan ikke leses for det produktet.",
        ["Microsoft Defender Antivirus did not answer."] = "Microsoft Defender Antivirus svarte ikke.",
        ["Microsoft Defender Antivirus gave no value for this."] = "Microsoft Defender Antivirus oppga ingen verdi for dette.",
        ["Microsoft Defender Antivirus gave values that do not fit together."] =
            "Microsoft Defender Antivirus oppga verdier som ikke stemmer overens.",
        ["Microsoft Defender Antivirus is in passive mode, and Windows Security Center lists no other antivirus product."] =
            "Microsoft Defender Antivirus er i passiv modus, og Windows Sikkerhetssenter viser ingen andre antivirusprodukter.",
        ["Microsoft Defender Antivirus is switched off, and another antivirus product is registered. Windows does that by design."] =
            "Microsoft Defender Antivirus er slått av, og et annet antivirusprodukt er registrert. Slik er Windows ment å virke.",
        ["Microsoft Defender Antivirus is switched off, and Windows Security Center lists no other antivirus product."] =
            "Microsoft Defender Antivirus er slått av, og Windows Sikkerhetssenter viser ingen andre antivirusprodukter.",
        ["Microsoft Defender Antivirus did not answer, and another antivirus product is registered. Defender normally switches off then."] =
            "Microsoft Defender Antivirus svarte ikke, og et annet antivirusprodukt er registrert. Da slår Defender seg vanligvis av.",
        ["Microsoft Defender Antivirus did not answer. On Windows Server it can be removed or replaced by another product, and Windows Server keeps no list of antivirus products."] =
            "Microsoft Defender Antivirus svarte ikke. På Windows Server kan det være fjernet eller erstattet av et annet produkt, og Windows Server har ingen liste over antivirusprodukter.",
        ["A policy holds this Microsoft Defender Antivirus setting off. Update Express does not override policy."] =
            "En policy holder denne innstillingen i Microsoft Defender Antivirus av. Update Express overstyrer ikke policy.",
        ["This PC starts in legacy BIOS mode, which cannot use Secure Boot."] =
            "Denne PC-en starter i eldre BIOS-modus, som ikke kan bruke sikker oppstart.",
        ["Windows holds no Secure Boot state for this PC. Its firmware may not offer Secure Boot."] =
            "Windows har ingen tilstand for sikker oppstart for denne PC-en. Fastvaren tilbyr kanskje ikke sikker oppstart.",
        ["The Secure Boot state could not be read."] = "Tilstanden for sikker oppstart kunne ikke leses.",
        ["Secure Boot is switched off. It is switched on in the firmware setup of the PC, not in Windows."] =
            "Sikker oppstart er slått av. Den slås på i PC-ens fastvareoppsett, ikke i Windows.",
        ["Windows did not answer the question about the TPM."] = "Windows svarte ikke på spørsmålet om TPM.",
        ["Windows finds no TPM. The PC has none, or it is switched off in the firmware setup."] =
            "Windows finner ingen TPM. PC-en har ingen, eller den er slått av i fastvareoppsettet.",
        ["The TPM follows version 1.2 of the specification. Windows 11 requires version 2.0."] =
            "TPM-en følger versjon 1.2 av spesifikasjonen. Windows 11 krever versjon 2.0.",
        ["The TPM reports a specification version that Update Express does not know."] =
            "TPM-en oppgir en spesifikasjonsversjon som Update Express ikke kjenner.",
        ["The settings for automatic logon could not be read."] = "Innstillingene for automatisk pålogging kunne ikke leses.",
        ["Windows holds a value for automatic logon that is neither on nor off."] =
            "Windows har en verdi for automatisk pålogging som verken er på eller av.",
        ["Automatic logon is on: anyone who can switch this PC on is signed in. That can be intended, for instance on a kiosk or a display PC."] =
            "Automatisk pålogging er på: alle som kan slå på denne PC-en, blir logget på. Det kan være tilsiktet, for eksempel på en kiosk- eller skjerm-PC.",
        ["Automatic logon is on, and its password is stored in the registry, where it can be read. Update Express has not read it."] =
            "Automatisk pålogging er på, og passordet er lagret i registeret, der det kan leses. Update Express har ikke lest det.",
        ["Automatic logon is off, but a stored password is left in the registry, where it can be read. Update Express has not read it."] =
            "Automatisk pålogging er av, men et lagret passord ligger igjen i registeret, der det kan leses. Update Express har ikke lest det.",
        ["Windows did not answer the question about installed updates."] = "Windows svarte ikke på spørsmålet om installerte oppdateringer.",
        ["Windows lists no installed security update. That can be so right after Windows was installed or upgraded, so it is not a sign that updates are missing."] =
            "Windows viser ingen installert sikkerhetsoppdatering. Slik kan det være rett etter at Windows ble installert eller oppgradert, så det er ikke et tegn på at oppdateringer mangler.",
        ["Windows gave no usable date for its installed security updates."] =
            "Windows oppga ingen brukbar dato for de installerte sikkerhetsoppdateringene.",
        ["No security update has been installed within the recommended time. Search for updates in the main window."] =
            "Ingen sikkerhetsoppdatering er installert innen den anbefalte tiden. Søk etter oppdateringer i hovedvinduet.",
        ["Update Express has not searched for updates since it was started. Search for updates in the main window, then scan again here."] =
            "Update Express har ikke søkt etter oppdateringer siden programmet ble startet. Søk etter oppdateringer i hovedvinduet, og skann deretter på nytt her.",
        ["The last search for updates in Update Express is too old to rely on. Search again in the main window, then scan again here."] =
            "Det siste søket etter oppdateringer i Update Express er for gammelt til å stole på. Søk på nytt i hovedvinduet, og skann deretter på nytt her.",
        ["Security updates are offered and not installed yet. Install them from the main window."] =
            "Sikkerhetsoppdateringer tilbys og er ikke installert ennå. Installer dem fra hovedvinduet.",
        ["The restart indicators of Windows could not be read."] = "Omstartsindikatorene i Windows kunne ikke leses.",
        ["File operations wait for the next restart. An installer left them; they are not always from a Windows update."] =
            "Filoperasjoner venter på neste omstart. Et installasjonsprogram har lagt dem igjen; de kommer ikke alltid fra en Windows-oppdatering.",
        ["Windows did not say which edition and version it is."] = "Windows oppga ikke hvilken utgave og versjon det er.",
        ["Whether this Windows release still gets security updates could not be verified here. The technical detail says why."] =
            "Om denne Windows-versjonen fortsatt får sikkerhetsoppdateringer, kunne ikke bekreftes her. Den tekniske detaljen sier hvorfor.",
        ["Microsoft's regular security updates for this Windows release have ended. A separate agreement such as Extended Security Updates cannot be verified here."] =
            "Microsofts vanlige sikkerhetsoppdateringer for denne Windows-versjonen er avsluttet. En egen avtale som Extended Security Updates kan ikke bekreftes her.",

        // Overall status
        ["Not assessed yet."] = "Ikke vurdert ennå.",
        ["No check could be assessed on this PC."] = "Ingen sjekker kunne vurderes på denne PC-en.",
        ["At least one check is not protected."] = "Minst én sjekk er ikke beskyttet.",
        ["At least one check needs attention."] = "Minst én sjekk krever oppmerksomhet.",
        ["A restart is required."] = "En omstart kreves.",
        ["At least one check could not run."] = "Minst én sjekk kunne ikke kjøres.",
        ["No check could confirm protection."] = "Ingen sjekker kunne bekrefte beskyttelse.",
        ["The last assessment is more than {0} days old."] = "Siste vurdering er mer enn {0} dager gammel.",
        ["No check found a problem."] = "Ingen sjekker fant et problem.",

        // Window and header shield
        [Ui.LastAssessed] = "Sist vurdert: {0}",
        [Ui.Progress] = "Sjekker… {0} av {1}",
        [Ui.ShownUntilAnswered] = "Resultatene fra {0} vises til hver sjekk har svart.",
        [Ui.Cancelled] = "Vurderingen ble avbrutt.",
        [Ui.ShowingAssessmentFrom] = "Viser vurderingen fra {0}.",
        [Ui.EmptyCategory] = "Ingen sjekker i denne kategorien i denne versjonen.",
        [Ui.ScanAgain] = "Skann på nytt",
        [Ui.Cancel] = "Avbryt",
        [Ui.Close] = "Lukk",
        [Ui.Details] = "Detaljer",
        [Ui.DetailsFor] = "Detaljer for {0}",
        [Ui.CurrentState] = "Nåværende tilstand",
        [Ui.Recommended] = "Anbefalt",
        [Ui.Checked] = "Sjekket",
        [Ui.NotCheckedYet] = "Ikke sjekket ennå",
        [Ui.Remediation] = "Utbedring",
        [Ui.RemediationAssessmentOnly] = "Kun vurdering",
        [Ui.RemediationNotInThisVersion] = "Ikke tilgjengelig i denne versjonen",
        [Ui.RemediationAvailable] = "Tilgjengelig",
        [Ui.Restart] = "Omstart",
        [Ui.RestartNeeded] = "Kreves",
        [Ui.RestartNotNeeded] = "Kreves ikke",
        [Ui.WhyItMatters] = "Hvorfor det betyr noe",
        [Ui.PolicyNote] = "Om policy",
        [Ui.Finding] = "Funn",
        [Ui.TechnicalDetail] = "Teknisk detalj",
        [Ui.CheckId] = "Sjekk-ID",
        [Ui.Severity] = "Alvorlighetsgrad",
        [Ui.SeverityInformational] = "Informasjon",
        [Ui.SeverityLow] = "Lav",
        [Ui.SeverityMedium] = "Middels",
        [Ui.SeverityHigh] = "Høy",
        [Ui.MoreInformation] = "Mer informasjon",
        [Ui.ManualReviewNote] = "Dette funnet må vurderes av en person.",
        [Ui.StatusInformation] = "Informasjon",
        [Ui.BaselineNote] = "Sjekkene og anbefalingene er Update Express' eget utvalg ({0}). Vurderingen leser bare; den endrer ingenting på denne PC-en.",
        [Ui.ShieldStatus] = "Status for sikkerhetsoversikt",
        [Ui.ShieldOpen] = "Klikk for å åpne sikkerhetsoversikten.",
        [Ui.SummaryCards] = "Sammendrag",
        [Ui.CategoryFilter] = "Kategori",
        [Ui.CheckList] = "Sikkerhetssjekker",

        // Why a value is recommended, and what policy can hold (baseline v1)
        ["Group Policy or MDM (Windows Defender Firewall profile settings) can hold the profile state."] =
            "Gruppepolicy eller MDM (profilinnstillinger for Windows Defender-brannmur) kan styre profilens tilstand.",
        ["Group Policy, MDM or tamper protection can hold Microsoft Defender Antivirus settings; a third-party product manages its own."] =
            "Gruppepolicy, MDM eller manipuleringsbeskyttelse kan styre innstillingene for Microsoft Defender Antivirus; et tredjepartsprodukt styrer sine egne.",
        ["Microsoft recommends keeping Windows Firewall on for every network profile, also when another firewall is in use on the network."] =
            "Microsoft anbefaler å ha Windows-brannmuren på for alle nettverksprofiler, også når en annen brannmur er i bruk i nettverket.",
        ["A PC needs one antivirus product. Registration shows which product Windows knows of; it does not prove that the product is healthy or up to date."] =
            "En PC trenger ett antivirusprodukt. Registreringen viser hvilket produkt Windows kjenner til; den beviser ikke at produktet fungerer eller er oppdatert.",
        ["Antivirus protection that is switched off does not stop known malware. For a third-party product the health cannot be verified from Windows alone and is reported as unknown."] =
            "Antivirusbeskyttelse som er slått av, stopper ikke kjent skadevare. For et tredjepartsprodukt kan tilstanden ikke bekreftes fra Windows alene, og den rapporteres som ukjent.",
        ["Without real-time protection malware is found only by a scheduled or manual scan, after it has run."] =
            "Uten sanntidsbeskyttelse blir skadevare bare funnet av en planlagt eller manuell skanning, etter at den har kjørt.",
        ["Reported for reference and support. No version is recommended; how fresh the definitions are is judged by UPD-SEC-008."] =
            "Rapporteres som referanse og for support. Ingen versjon er anbefalt; hvor ferske definisjonene er, vurderes av UPD-SEC-008.",
        ["Reported for reference and support. The age limit is in UPD-SEC-008."] =
            "Rapporteres som referanse og for support. Aldersgrensen ligger i UPD-SEC-008.",
        ["Microsoft Defender Antivirus steps back to passive mode or switches off when another antivirus product is the active one. That is by design and is not a fault."] =
            "Microsoft Defender Antivirus går over i passiv modus eller slår seg av når et annet antivirusprodukt er det aktive. Det er tilsiktet og er ikke en feil.",
        ["Secure Boot keeps malware from loading before Windows starts. It is a firmware setting; Update Express reports it and never changes it. A PC that starts in legacy BIOS mode cannot use it and is reported as not supported."] =
            "Sikker oppstart hindrer skadevare i å lastes før Windows starter. Det er en fastvareinnstilling; Update Express rapporterer den og endrer den aldri. En PC som starter i eldre BIOS-modus kan ikke bruke den, og rapporteres som ikke støttet.",
        ["A TPM protects encryption keys and sign-in secrets in hardware; BitLocker and Windows Hello build on it. It is hardware or a firmware setting; Update Express reports it and never changes it."] =
            "En TPM beskytter krypteringsnøkler og påloggingshemmeligheter i maskinvaren; BitLocker og Windows Hello bygger på den. Den er maskinvare eller en fastvareinnstilling; Update Express rapporterer den og endrer den aldri.",
        ["Microsoft recommends TPM 2.0 over TPM 1.2 for its newer algorithms, and Windows 11 requires it."] =
            "Microsoft anbefaler TPM 2.0 fremfor TPM 1.2 på grunn av nyere algoritmer, og Windows 11 krever det.",
        ["With automatic logon anyone who can switch the PC on is signed in, and the classic setup keeps the password readable in the registry. Only whether it is configured is reported; the stored password is never read."] =
            "Med automatisk pålogging blir alle som kan slå på PC-en logget på, og det klassiske oppsettet lagrer passordet lesbart i registeret. Bare om det er konfigurert rapporteres; det lagrede passordet leses aldri.",
        ["Microsoft releases Windows security updates monthly, on the second Tuesday. Two releases are at most 35 days apart; 45 days allows ten days to install. The limit of 45 days is Update Express's own."] =
            "Microsoft gir ut sikkerhetsoppdateringer for Windows hver måned, den andre tirsdagen. Det er høyst 35 dager mellom to utgivelser; 45 dager gir ti dager til å installere. Grensen på 45 dager er Update Express' egen.",
        ["A security update that is offered and not installed leaves a known weakness open. The result of the last search in Update Express is used; this check does not search again, and without a search it reports unknown."] =
            "En sikkerhetsoppdatering som tilbys og ikke er installert, lar en kjent svakhet stå åpen. Resultatet av det siste søket i Update Express brukes; denne sjekken søker ikke på nytt, og uten et søk rapporterer den ukjent.",
        ["Updates that wait for a restart do not protect until the PC has restarted. Update Express's own rule, from the indicators Windows sets."] =
            "Oppdateringer som venter på omstart, beskytter ikke før PC-en er startet på nytt. Update Express' egen regel, ut fra indikatorene Windows setter.",
        ["Reported for reference. Whether this release still gets security updates is judged by UPD-SEC-006."] =
            "Rapporteres som referanse. Om denne versjonen fortsatt får sikkerhetsoppdateringer, vurderes av UPD-SEC-006.",
        ["A Windows release past its end of servicing gets no security fixes. LTSC editions, releases outside the built-in table and Extended Security Updates cannot be verified here and are reported as unknown, not as supported."] =
            "En Windows-versjon som har passert slutten på støtteperioden, får ingen sikkerhetsrettinger. LTSC-utgaver, versjoner utenfor den innebygde tabellen og Extended Security Updates kan ikke bekreftes her, og rapporteres som ukjent, ikke som støttet.",
        ["Microsoft publishes Defender definitions several times a day. Update Express already warns in its header at 2 days and marks 7 days as critical; the same limits are used here."] =
            "Microsoft publiserer Defender-definisjoner flere ganger om dagen. Update Express varsler allerede i toppfeltet ved 2 dager og markerer 7 dager som kritisk; de samme grensene brukes her.",

        // Check names and descriptions (baseline v1)
        ["Windows Firewall – Domain profile"] = "Windows-brannmur – domeneprofil",
        ["Windows Firewall for networks where the PC reaches its domain."] =
            "Windows-brannmur for nettverk der PC-en når domenet sitt.",
        ["Windows Firewall – Private profile"] = "Windows-brannmur – privat profil",
        ["Windows Firewall for networks marked as private, such as a home or office network."] =
            "Windows-brannmur for nettverk merket som private, for eksempel hjemme- eller kontornettverk.",
        ["Windows Firewall – Public profile"] = "Windows-brannmur – offentlig profil",
        ["Windows Firewall for networks marked as public, such as a guest or café network."] =
            "Windows-brannmur for nettverk merket som offentlige, for eksempel gjeste- eller kafénettverk.",
        ["Registered antivirus products"] = "Registrerte antivirusprodukter",
        ["The antivirus products that have registered with Windows Security Center."] =
            "Antivirusproduktene som har registrert seg i Windows Sikkerhetssenter.",
        ["Antivirus protection status"] = "Status for antivirusbeskyttelse",
        ["Whether an antivirus product is switched on and protecting this PC."] =
            "Om et antivirusprodukt er slått på og beskytter denne PC-en.",
        ["Real-time protection"] = "Sanntidsbeskyttelse",
        ["Whether files and programs are scanned as they are opened or run."] =
            "Om filer og programmer skannes når de åpnes eller kjøres.",
        ["Antivirus signature version"] = "Versjon av antivirussignaturer",
        ["The version of the antivirus definitions in use."] = "Versjonen av antivirusdefinisjonene som er i bruk.",
        ["Antivirus signatures last updated"] = "Antivirussignaturer sist oppdatert",
        ["When the antivirus definitions were last updated."] = "Når antivirusdefinisjonene sist ble oppdatert.",
        ["Microsoft Defender operating mode"] = "Driftsmodus for Microsoft Defender",
        ["Whether Microsoft Defender Antivirus runs in normal (active) or passive mode."] =
            "Om Microsoft Defender Antivirus kjører i normal (aktiv) eller passiv modus.",
        ["Secure Boot"] = "Sikker oppstart",
        ["Whether the firmware starts only boot software it trusts."] =
            "Om fastvaren bare starter oppstartsprogramvare den stoler på.",
        ["TPM availability"] = "TPM tilgjengelig",
        ["Whether Windows finds a Trusted Platform Module (TPM) security chip."] =
            "Om Windows finner en TPM-sikkerhetsbrikke (Trusted Platform Module).",
        ["TPM specification version"] = "TPM-spesifikasjonsversjon",
        ["The version of the TPM specification the security chip follows."] =
            "Versjonen av TPM-spesifikasjonen som sikkerhetsbrikken følger.",
        ["Automatic Windows logon"] = "Automatisk Windows-pålogging",
        ["Whether Windows signs a user in by itself when the PC starts."] =
            "Om Windows logger på en bruker av seg selv når PC-en starter.",
        ["Last successful Windows security update"] = "Siste vellykkede sikkerhetsoppdatering for Windows",
        ["When a Windows security update was last installed successfully."] =
            "Når en sikkerhetsoppdatering for Windows sist ble installert uten feil.",
        ["Available security updates"] = "Tilgjengelige sikkerhetsoppdateringer",
        ["Security updates that the last Windows Update search found and that are not installed yet."] =
            "Sikkerhetsoppdateringer som det siste Windows Update-søket fant, og som ikke er installert ennå.",
        ["Pending restart"] = "Venter på omstart",
        ["Whether Windows waits for a restart to finish installing updates."] =
            "Om Windows venter på en omstart for å fullføre installasjonen av oppdateringer.",
        ["Windows edition and version"] = "Windows-utgave og -versjon",
        ["The Windows edition, feature version and build on this PC."] =
            "Windows-utgaven, funksjonsversjonen og byggnummeret på denne PC-en.",
        ["Windows support status"] = "Støttestatus for Windows",
        ["Whether Microsoft still releases security updates for this Windows release."] =
            "Om Microsoft fortsatt gir ut sikkerhetsoppdateringer for denne Windows-versjonen.",
        ["Antivirus signature freshness"] = "Alder på antivirussignaturer",
        ["How old the antivirus definitions are."] = "Hvor gamle antivirusdefinisjonene er.",
    };
}
