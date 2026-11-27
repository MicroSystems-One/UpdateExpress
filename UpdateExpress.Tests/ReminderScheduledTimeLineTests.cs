using System.Globalization;
using UpdateExpress.Services;
using Xunit;

namespace UpdateExpress.Tests;

public class ReminderScheduledTimeLineTests
{
    [Fact]
    public void Today_in_English_replaces_the_weekday_with_Today()
    {
        var today = DateTime.Today.AddHours(19).AddMinutes(30);
        var schedule = new ReminderSchedule { Language = "en", Use24HourClock = true, DateFormat = "text" };
        string without = schedule.FormatWithoutWeekday(today);
        Assert.DoesNotContain("Mon", without);
        Assert.DoesNotContain("Tue", without);
        Assert.Contains("19:30", without, StringComparison.Ordinal);
        string line = "Today " + without;
        Assert.StartsWith("Today ", line, StringComparison.Ordinal);
        Assert.Equal("Scheduled time: " + line, ScheduledTimeLine(false, today, schedule));
    }

    [Fact]
    public void Today_in_Norwegian_uses_I_dag()
    {
        var today = DateTime.Today.AddHours(19).AddMinutes(30);
        var schedule = new ReminderSchedule { Language = "no", Use24HourClock = true, DateFormat = "text" };
        string line = "I dag " + schedule.FormatWithoutWeekday(today);
        Assert.StartsWith("I dag ", line, StringComparison.Ordinal);
        Assert.Equal("Planlagt tidspunkt: " + line, ScheduledTimeLine(true, today, schedule));
    }

    [Fact]
    public void Another_day_keeps_the_full_Format()
    {
        var tomorrow = DateTime.Today.AddDays(1).AddHours(17);
        var schedule = new ReminderSchedule { Language = "en", Use24HourClock = true, DateFormat = "text" };
        Assert.Equal("Scheduled time: " + schedule.Format(tomorrow), ScheduledTimeLine(false, tomorrow, schedule));
    }

    private static string ScheduledTimeLine(bool norwegian, DateTime occurrence, ReminderSchedule schedule)
    {
        string time = schedule.Format(occurrence);
        if (occurrence.Date == DateTime.Today)
            time = (norwegian ? "I dag " : "Today ") + schedule.FormatWithoutWeekday(occurrence);
        return norwegian ? $"Planlagt tidspunkt: {time}" : $"Scheduled time: {time}";
    }
}