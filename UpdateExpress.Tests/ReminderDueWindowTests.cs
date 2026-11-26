using UpdateExpress.Services;
using Xunit;

namespace UpdateExpress.Tests;

public class ReminderDueWindowTests
{
    // Wednesday 25.11.2026; Monday-based index 2.
    private static readonly DateTime Wednesday = new(2026, 11, 25);
    private const int WednesdayOnly = 1 << 2;
    private const int ThursdayOnly = 1 << 3;

    private static ReminderSchedule Schedule(string time = "20:00", int weekday = 2, bool enabled = true) =>
        new() { Enabled = enabled, Time = time, Weekday = weekday };

    private static DateTime At(int hour, int minute, int second = 0) =>
        Wednesday.AddHours(hour).AddMinutes(minute).AddSeconds(second);

    [Fact]
    public void BeforeScheduledTime_NotDue()
    {
        Assert.False(MaintenanceReminderPromptGate.IsReminderDueWindow(Schedule(), WednesdayOnly, At(19, 59, 59)));
    }

    [Theory]
    [InlineData(20, 0)]
    [InlineData(20, 1)]
    [InlineData(21, 47)]
    [InlineData(23, 29)]
    public void FromScheduledTimeUntilLastHalfHour_Due(int hour, int minute)
    {
        Assert.True(MaintenanceReminderPromptGate.IsReminderDueWindow(Schedule(), WednesdayOnly, At(hour, minute)));
    }

    [Fact]
    public void AfterLastHalfHourSlotOfTheDay_NotDue()
    {
        Assert.False(MaintenanceReminderPromptGate.IsReminderDueWindow(Schedule(), WednesdayOnly, At(23, 30, 1)));
    }

    [Fact]
    public void RemindersDisabled_NotDue()
    {
        Assert.False(MaintenanceReminderPromptGate.IsReminderDueWindow(
            Schedule(enabled: false), WednesdayOnly, At(21, 0)));
    }

    [Fact]
    public void ConfirmedToday_NotDue()
    {
        var schedule = Schedule();
        schedule.ConfirmedOccurrence = At(20, 0);
        Assert.False(MaintenanceReminderPromptGate.IsReminderDueWindow(schedule, WednesdayOnly, At(21, 0)));
    }

    [Fact]
    public void ConfirmedOnAnEarlierDay_StillDue()
    {
        var schedule = Schedule();
        schedule.ConfirmedOccurrence = At(20, 0).AddDays(-7);
        Assert.True(MaintenanceReminderPromptGate.IsReminderDueWindow(schedule, WednesdayOnly, At(21, 0)));
    }

    [Fact]
    public void SnoozedOrAlreadyPrompted_StillDue()
    {
        var schedule = Schedule();
        schedule.LastPromptedOccurrence = At(20, 0);
        schedule.SnoozedUntil = At(21, 30);
        Assert.True(MaintenanceReminderPromptGate.IsReminderDueWindow(schedule, WednesdayOnly, At(21, 0)));
    }

    [Fact]
    public void InstallDaysMaskWithoutToday_NotDue_EvenWhenScheduleWeekdayMatches()
    {
        Assert.False(MaintenanceReminderPromptGate.IsReminderDueWindow(
            Schedule(weekday: 2), ThursdayOnly, At(21, 0)));
    }

    [Fact]
    public void InstallDaysMaskWithToday_Due_EvenWhenScheduleWeekdayDiffers()
    {
        Assert.True(MaintenanceReminderPromptGate.IsReminderDueWindow(
            Schedule(weekday: 4), WednesdayOnly, At(21, 0)));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(7, true)]
    [InlineData(3, false)]
    public void EmptyInstallDaysMask_FallsBackToScheduleWeekday(int weekday, bool expected)
    {
        Assert.Equal(expected, MaintenanceReminderPromptGate.IsReminderDueWindow(
            Schedule(weekday: weekday), 0, At(21, 0)));
    }

    [Fact]
    public void UnparsableTime_NotDue()
    {
        Assert.False(MaintenanceReminderPromptGate.IsReminderDueWindow(
            Schedule(time: "8 pm"), WednesdayOnly, At(21, 0)));
    }
}
