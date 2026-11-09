using System;
using System.Globalization;

namespace UpdateExpress.Services;

internal static class AutomationSchedule
{
    internal const string DefaultPreferredTime = "12:00";

    internal static string PreferredOrDefault(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DefaultPreferredTime : value.Trim();

    internal static DateTime NextPreferredOccurrence(string preferredHhMm)
    {
        if (!TimeSpan.TryParseExact(preferredHhMm, @"hh\:mm", CultureInfo.InvariantCulture, out var preferred))
            preferred = new TimeSpan(12, 0, 0);
        DateTime candidate = DateTime.Today + preferred;
        if (candidate <= DateTime.Now.AddMinutes(2))
            candidate = candidate.AddDays(1);
        return candidate;
    }

    internal static DateTime ComputeNextDue(DateTime from, int intervalMinutes, string preferredHhMm, int minIntervalMinutes = 60)
    {
        int minutes = Math.Max(minIntervalMinutes, intervalMinutes);
        DateTime raw = from.AddMinutes(minutes);
        if (!TimeSpan.TryParseExact(preferredHhMm, @"hh\:mm", CultureInfo.InvariantCulture, out var preferred))
            return raw;

        DateTime day = raw.Date + preferred;
        if (day < raw) day = day.AddDays(1);
        return day;
    }

    /// <summary>
    /// Weekly general schedule. policyWeekday: 0=Monday … 6=Sunday, 7=every day (falls back to interval+preferred).
    /// </summary>
    internal static DateTime ComputeNextWeeklyDue(DateTime from, int intervalMinutes, string preferredHhMm, int policyWeekday)
    {
        if (policyWeekday < 0 || policyWeekday > 6)
            return ComputeNextDue(from, intervalMinutes, preferredHhMm, 1440);

        if (!TimeSpan.TryParseExact(preferredHhMm, @"hh\:mm", CultureInfo.InvariantCulture, out var preferred))
            preferred = new TimeSpan(12, 0, 0);

        DayOfWeek target = policyWeekday switch
        {
            0 => DayOfWeek.Monday,
            1 => DayOfWeek.Tuesday,
            2 => DayOfWeek.Wednesday,
            3 => DayOfWeek.Thursday,
            4 => DayOfWeek.Friday,
            5 => DayOfWeek.Saturday,
            _ => DayOfWeek.Sunday
        };

        DateTime earliest = from.AddMinutes(Math.Max(1440, intervalMinutes));
        DateTime candidate = earliest.Date + preferred;
        int guard = 0;
        while ((candidate.DayOfWeek != target || candidate < earliest) && guard++ < 14)
            candidate = candidate.AddDays(1).Date + preferred;
        return candidate;
    }
}
