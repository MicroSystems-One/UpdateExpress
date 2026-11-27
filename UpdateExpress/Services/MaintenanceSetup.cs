using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace UpdateExpress.Services;

/// <summary>
/// Shared maintenance window: which weekdays + one clock for Critical/General installs.
/// Maps onto existing PreferredInstallTime / Weekday / ActiveHours fields.
/// </summary>
internal static class MaintenanceSetup
{
    public const int AllDaysMask = 0b1111111; // Mon..Sun

    internal static readonly string[] DayLabelsEn = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    internal static readonly string[] DayLabelsNo = ["Man", "Tir", "Ons", "Tor", "Fre", "Lør", "Søn"];

    internal static bool IsNorwegian() =>
        string.Equals(ReminderSchedule.Load().Language, "no", StringComparison.OrdinalIgnoreCase);

    /// <summary>The fourth setup choice, beside private, business and both.</summary>
    internal const string ServerProfile = "server";

    /// <summary>
    /// Test switch: with this environment variable set to 1 the setup choice treats this PC as
    /// a Windows Server, so the Server choice and its defaults can be seen on a client. The
    /// dialogs mark it in red. Nothing else in the app reads it.
    /// </summary>
    internal const string ServerTestSwitchVariable = "UE_TEST_SERVER";

    internal static bool ServerTestSwitchOn =>
        string.Equals(Environment.GetEnvironmentVariable(ServerTestSwitchVariable)?.Trim(), "1", StringComparison.Ordinal);

    internal static bool SetupTreatsThisMachineAsServer => ServerTestSwitchOn || WindowsProductType.IsServerOs;

    /// <summary>
    /// The choice a setup dialog starts on. A saved choice always wins; only a machine with
    /// none saved starts on Server when it runs Windows Server.
    /// </summary>
    internal static string InitialProfile(string? savedProfile, bool serverOs, string clientDefault) =>
        !string.IsNullOrWhiteSpace(savedProfile) ? savedProfile
        : serverOs ? ServerProfile
        : clientDefault;

    internal static string ServerProfileTip(bool norwegian) => norwegian
        ? "Windows Server – man–fre 17:00, ingen automatisk omstart. En person starter serveren på nytt."
        : "Windows Server — weekdays at 17:00, no automatic restart. A person restarts the server.";

    internal static string ServerTestSwitchNote(bool norwegian) => norwegian
        ? $"TEST – testbryteren {ServerTestSwitchVariable}=1 er satt. Denne PC-en behandles som en Windows Server i dette valget."
        : $"TEST — the test switch {ServerTestSwitchVariable}=1 is set. This PC is treated as a Windows Server in this choice.";

    internal static int ClampMask(int mask) => mask & AllDaysMask;

    internal static bool HasDay(int mask, int mondayBasedIndex) =>
        mondayBasedIndex is >= 0 and <= 6 && (ClampMask(mask) & (1 << mondayBasedIndex)) != 0;

    internal static int ToggleDay(int mask, int mondayBasedIndex)
    {
        if (mondayBasedIndex is < 0 or > 6) return ClampMask(mask);
        return ClampMask(mask ^ (1 << mondayBasedIndex));
    }

    internal static int TodayMondayBased(DateTime now) => ((int)now.DayOfWeek + 6) % 7;

    internal static string NormalizeTime(string? value, string fallback = "18:00")
    {
        string t = (value ?? "").Trim();
        if (TimeSpan.TryParseExact(t, @"hh\:mm", CultureInfo.InvariantCulture, out _))
            return t;
        return fallback;
    }

    internal static int FirstSelectedWeekday(int mask)
    {
        mask = ClampMask(mask);
        if (mask == 0) return 2; // Wednesday fallback
        for (int i = 0; i < 7; i++)
            if ((mask & (1 << i)) != 0) return i;
        return 2;
    }

    internal static string FormatDays(int mask, bool norwegian)
    {
        mask = ClampMask(mask);
        if (mask == 0) return norwegian ? "ingen dager" : "no days";
        if (mask == AllDaysMask) return norwegian ? "hver dag" : "every day";
        var labels = norwegian ? DayLabelsNo : DayLabelsEn;
        var sb = new StringBuilder();
        for (int i = 0; i < 7; i++)
        {
            if ((mask & (1 << i)) == 0) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(labels[i]);
        }
        return sb.ToString();
    }

    internal static DateTime? NextInstallOccurrence(int daysMask, string installTime, DateTime from, int graceMinutes = 2)
    {
        daysMask = ClampMask(daysMask);
        if (daysMask == 0) return null;
        if (!TimeSpan.TryParseExact(NormalizeTime(installTime), @"hh\:mm", CultureInfo.InvariantCulture, out var at))
            at = new TimeSpan(18, 0, 0);

        DateTime start = from.AddMinutes(-graceMinutes);
        for (int offset = 0; offset < 14; offset++)
        {
            DateTime day = from.Date.AddDays(offset);
            int idx = TodayMondayBased(day);
            if ((daysMask & (1 << idx)) == 0) continue;
            DateTime candidate = day + at;
            if (candidate > start)
                return candidate;
        }
        return null;
    }

    internal static bool IsWithinInstallWindow(int daysMask, string installTime, DateTime now, int windowMinutes = 30)
    {
        var next = NextInstallOccurrence(daysMask, installTime, now, graceMinutes: 0);
        if (next is null) return false;
        // "In window" if next occurrence is today and within the past windowMinutes..now or now..+5m
        var todayAt = now.Date + TimeSpan.ParseExact(NormalizeTime(installTime), @"hh\:mm", CultureInfo.InvariantCulture);
        if (!HasDay(daysMask, TodayMondayBased(now))) return false;
        return now >= todayAt.AddMinutes(-2) && now <= todayAt.AddMinutes(windowMinutes);
    }

    internal static void ApplyToPolicy(UpdatePolicy policy, int daysMask, string installTime)
    {
        daysMask = ClampMask(daysMask);
        if (daysMask == 0) daysMask = AllDaysMask;
        string time = NormalizeTime(installTime);
        policy.InstallDaysMask = daysMask;
        policy.InstallTime = time;
        policy.WindowsAutomation.Critical.PreferredInstallTime = time;
        policy.WindowsAutomation.General.PreferredInstallTime = time;
        policy.WindowsAutomation.General.Weekday = daysMask == AllDaysMask ? 7 : FirstSelectedWeekday(daysMask);
    }

    internal static void SyncReminderFromPolicy(UpdatePolicy policy)
    {
        var schedule = ReminderSchedule.Load();
        schedule.Time = NormalizeTime(policy.InstallTime, schedule.Time);
        int mask = ClampMask(policy.InstallDaysMask);
        schedule.Weekday = mask == AllDaysMask || mask == 0 ? 7 : FirstSelectedWeekday(mask);
        schedule.Enabled = true;
        schedule.Save();
    }

    internal static void ApplyProfileDefaults(UpdatePolicy policy, string profile)
    {
        policy.SetupProfile = profile;
        policy.WindowsAutomation.Enabled = true;
        policy.WindowsAutomation.Defender.Enabled = true;
        policy.WindowsAutomation.Defender.Mode = "autoInstall";
        policy.WindowsAutomation.Critical.Enabled = true;
        policy.WindowsAutomation.General.Enabled = true;

        switch ((profile ?? "").Trim().ToLowerInvariant())
        {
            case "business":
                policy.WindowsAutomation.AutoReboot.ActiveHoursStart = 8;
                policy.WindowsAutomation.AutoReboot.ActiveHoursEnd = 16;
                policy.WindowsAutomation.AutoReboot.Enabled = true;
                policy.WindowsAutomation.AutoReboot.Mode = "outsideActiveHours";
                ApplyToPolicy(policy, (1 << 1) | (1 << 3), "18:00"); // Tue+Thu
                break;
            case "both":
                // Product default (install screenshots): weekdays 17:00, restart outside 08–16.
                policy.WindowsAutomation.AutoReboot.ActiveHoursStart = 8;
                policy.WindowsAutomation.AutoReboot.ActiveHoursEnd = 16;
                policy.WindowsAutomation.AutoReboot.Enabled = true;
                policy.WindowsAutomation.AutoReboot.Mode = "outsideActiveHours";
                ApplyToPolicy(policy, 0b0011111, "17:00"); // Mon..Fri
                break;
            case ServerProfile:
                // A server restarts only when a person restarts it. Install window as "both".
                policy.WindowsAutomation.AutoReboot.ActiveHoursStart = 8;
                policy.WindowsAutomation.AutoReboot.ActiveHoursEnd = 16;
                policy.WindowsAutomation.AutoReboot.Enabled = false;
                policy.WindowsAutomation.AutoReboot.Mode = "confirm";
                policy.WindowsAutomation.NeverAutoReboot = true;
                ApplyToPolicy(policy, 0b0011111, "17:00"); // Mon..Fri
                break;
            default: // private
                policy.WindowsAutomation.AutoReboot.ActiveHoursStart = 8;
                policy.WindowsAutomation.AutoReboot.ActiveHoursEnd = 22;
                policy.WindowsAutomation.AutoReboot.Enabled = true;
                policy.WindowsAutomation.AutoReboot.Mode = "confirm";
                ApplyToPolicy(policy, AllDaysMask, "20:00");
                break;
        }
    }

    /// <summary>
    /// Skip in the setup wizard: a policy with a saved choice keeps it and everything set under
    /// it; one without gets the defaults of the choice the wizard started on.
    /// </summary>
    internal static void ApplySkipChoice(UpdatePolicy policy, bool serverOs)
    {
        if (string.IsNullOrWhiteSpace(policy.SetupProfile))
            ApplyProfileDefaults(policy, InitialProfile(null, serverOs, "private"));
        else
            ApplyToPolicy(policy, policy.InstallDaysMask, policy.InstallTime);
        policy.SetupCompleted = true;
    }

    /// <summary>
    /// Persist Installation options (profile / hours / install window / reboot) and mark setup complete
    /// so the post-install Setup wizard is not shown again.
    /// </summary>
    internal static void ApplyInstallDialogChoices(
        string profile,
        int activeStart,
        int activeEnd,
        int daysMask,
        string installTime,
        string rebootChoice,
        bool mirrorMachine = false)
    {
        var policy = UpdatePolicy.Load();
        policy.Normalize();
        ApplyProfileDefaults(policy, profile);
        policy.WindowsAutomation.AutoReboot.ActiveHoursStart = Math.Clamp(activeStart, 0, 23);
        policy.WindowsAutomation.AutoReboot.ActiveHoursEnd = Math.Clamp(activeEnd, 0, 23);
        ApplyToPolicy(policy, daysMask, installTime);
        policy.WindowsAutomation.AutoReboot.Enabled = !string.Equals(rebootChoice, "off", StringComparison.OrdinalIgnoreCase);
        policy.WindowsAutomation.AutoReboot.Mode =
            string.Equals(rebootChoice, "outsideActiveHours", StringComparison.OrdinalIgnoreCase)
                ? "outsideActiveHours"
                : "confirm";
        policy.WindowsAutomation.NeverAutoReboot = !policy.WindowsAutomation.AutoReboot.Enabled;
        policy.SetupProfile = (profile ?? "private").Trim().ToLowerInvariant();
        policy.SetupCompleted = true;
        policy.Save();
        if (mirrorMachine)
            policy.SaveMachineMirror();
        SyncReminderFromPolicy(policy);
    }

    internal static string EveningNoticeTitle(bool norwegian) =>
        norwegian ? "Oppdateringer planlagt i kveld" : "Updates scheduled for this evening";

    internal static string ScheduledNoticeTitle(bool norwegian, bool installToday) =>
        installToday
            ? EveningNoticeTitle(norwegian)
            : (norwegian ? "Oppdateringer planlagt" : "Updates scheduled");

    /// <summary>
    /// The notice shown when downloaded updates are set to install at the next install window,
    /// worded for an installation later today or on another day.
    /// </summary>
    internal static ScheduledInstallNotice ScheduledNotice(DateTime when, DateTime today, bool norwegian, bool autoReboot)
    {
        bool installToday = when.Date == today.Date;
        string title = ScheduledNoticeTitle(norwegian, installToday);
        if (!installToday)
        {
            string whenLine = norwegian
                ? "Installasjon er satt til " + NoticeDates.DayAndTime(when, norwegian: true)
                : "Installation is set for " + NoticeDates.DayAndTime(when, norwegian: false);
            return norwegian
                ? new ScheduledInstallNotice(
                    title,
                    "Nye Windows-oppdateringer er lastet ned og klare.",
                    whenLine,
                    null,
                    "Tips: Lagre åpne dokumenter før PC-en installerer og eventuelt restarter.")
                : new ScheduledInstallNotice(
                    title,
                    "New Windows updates are downloaded and ready.",
                    whenLine,
                    null,
                    "Tip: Save open files before the PC installs and possibly restarts.");
        }

        string clock = when.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (norwegian)
        {
            return new ScheduledInstallNotice(
                title,
                "Nye Windows-oppdateringer er lastet ned og klare.",
                $"Installasjon er satt til i dag kl. {clock}.",
                autoReboot
                    ? "Hvis Windows krever omstart, skjer det automatisk etter en nedtelling (du kan utsette)."
                    : "Hvis Windows krever omstart, får du spørsmål før PC-en restarter.",
                "Tips: Lagre åpne dokumenter og lukk viktige programmer før du går fra PC-en, så du ikke mister ulagret arbeid.");
        }

        return new ScheduledInstallNotice(
            title,
            "New Windows updates are downloaded and ready.",
            $"Installation is set for today at {clock}.",
            autoReboot
                ? "If Windows requires a restart, it will happen automatically after a countdown (you can postpone)."
                : "If Windows requires a restart, you will be asked before the PC restarts.",
            "Tip: Save open files and close important apps before you leave the PC, so you don’t lose unsaved work.");
    }

    internal static string EveningBalloon(DateTime when, bool norwegian) =>
        norwegian
            ? $"Oppdateringer klare — installeres i dag kl. {when:HH:mm}. Lagre åpne filer før du går."
            : $"Updates ready — installing today at {when:HH:mm}. Save open files before you leave.";

    internal static string SummaryText(UpdatePolicy policy, bool norwegian)
    {
        string days = FormatDays(policy.InstallDaysMask, norwegian);
        string time = NormalizeTime(policy.InstallTime);
        int a0 = policy.WindowsAutomation.AutoReboot.ActiveHoursStart;
        int a1 = policy.WindowsAutomation.AutoReboot.ActiveHoursEnd;
        string reboot = !policy.WindowsAutomation.AutoReboot.Enabled
            ? (norwegian ? "manuell omstart" : "manual restart")
            : string.Equals(policy.WindowsAutomation.AutoReboot.Mode, "outsideActiveHours", StringComparison.OrdinalIgnoreCase)
                ? (norwegian ? "omstart utenom arbeidstid" : "restart outside active hours")
                : (norwegian ? "spør før omstart" : "ask before restart");
        if (norwegian)
            return $"Profil: {policy.SetupProfile}\nArbeidstid: {a0:00}:00–{a1:00}:00\nInstallasjon: {days} kl. {time}\nOmstart: {reboot}\nDefender-signaturer: kontinuerlig (uansett).";
        return $"Profile: {policy.SetupProfile}\nActive hours: {a0:00}:00–{a1:00}:00\nInstall: {days} at {time}\nRestart: {reboot}\nDefender signatures: continuous (always).";
    }
}
