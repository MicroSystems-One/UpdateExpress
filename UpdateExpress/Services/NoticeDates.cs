using System.Globalization;

namespace UpdateExpress.Services;

/// <summary>
/// Day and time wording for notice windows, in the app's language (not the Windows regional format).
/// </summary>
internal static class NoticeDates
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Norwegian = CultureInfo.GetCultureInfo("nb-NO");

    /// <summary>English: "Mon 30 Nov at 17:00". Norwegian: "man. 30. nov. kl. 17:00".</summary>
    internal static string DayAndTime(DateTime when, bool norwegian)
    {
        string clock = when.ToString("HH:mm", CultureInfo.InvariantCulture);
        return norwegian
            ? $"{NorwegianDayMonth(when)} kl. {clock}."
            : $"{EnglishDayMonth(when)} at {clock}.";
    }

    /// <summary>Date and clock for the deferred-restart notice body (no leading "Installation is set for").</summary>
    internal static string DayAndTimeInline(DateTime when, bool norwegian)
    {
        string clock = when.ToString("HH:mm", CultureInfo.InvariantCulture);
        return norwegian
            ? $"{NorwegianDayMonth(when)} kl. {clock}"
            : $"{EnglishDayMonth(when)} at {clock}";
    }

    internal static string EnglishDayMonth(DateTime when)
    {
        string dow = when.ToString("ddd", English);
        string dayMonth = when.ToString("dd MMM", English);
        return $"{dow} {dayMonth}";
    }

    private static string NorwegianDayMonth(DateTime when)
    {
        string dow = when.ToString("ddd", Norwegian).ToLowerInvariant();
        if (!dow.EndsWith('.')) dow += ".";
        string day = when.ToString("dd", CultureInfo.InvariantCulture);
        string month = when.ToString("MMM", Norwegian).ToLowerInvariant();
        if (!month.EndsWith('.')) month += ".";
        return $"{dow} {day}. {month}";
    }
}