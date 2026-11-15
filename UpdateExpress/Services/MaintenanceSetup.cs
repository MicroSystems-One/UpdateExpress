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
                policy.WindowsAutomation.AutoReboot.ActiveHoursStart = 8;
                policy.WindowsAutomation.AutoReboot.ActiveHoursEnd = 16;
                policy.WindowsAutomation.AutoReboot.Enabled = true;
                policy.WindowsAutomation.AutoReboot.Mode = "confirm";
                ApplyToPolicy(policy, (1 << 1) | (1 << 4), "18:00"); // Tue+Fri
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

    internal static string EveningNoticeTitle(bool norwegian) =>
        norwegian ? "Oppdateringer planlagt i kveld" : "Updates scheduled for this evening";

    internal static string EveningNoticeBody(DateTime when, bool norwegian, bool autoReboot)
    {
        string clock = when.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (norwegian)
        {
            string reboot = autoReboot
                ? "Hvis Windows krever omstart, skjer det automatisk etter en nedtelling (du kan utsette)."
                : "Hvis Windows krever omstart, får du spørsmål før PC-en restarter.";
            return
                $"Nye Windows-oppdateringer er lastet ned og klare.\n" +
                $"Installasjon er satt til i dag kl. {clock}. {reboot}\n\n" +
                "Tips: Lagre åpne dokumenter og lukk viktige programmer før du går fra PC-en, så du ikke mister ulagret arbeid.";
        }

        string rebootEn = autoReboot
            ? "If Windows requires a restart, it will happen automatically after a countdown (you can postpone)."
            : "If Windows requires a restart, you will be asked before the PC restarts.";
        return
            $"New Windows updates are downloaded and ready.\n" +
            $"Installation is set for today at {clock}. {rebootEn}\n\n" +
            "Tip: Save open files and close important apps before you leave the PC, so you don’t lose unsaved work.";
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
