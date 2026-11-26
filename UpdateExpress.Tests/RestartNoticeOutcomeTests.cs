using Xunit;

namespace UpdateExpress.Tests;

/// <summary>
/// What the restart notice reports back to the code that asked for the restart. Only Stop
/// means "no restart is coming", which lets a parked application queue resume without one.
/// </summary>
public class RestartNoticeOutcomeTests
{
    private static bool KeepsArmed(string decision) =>
        RebootCountdownWindow.KeepsRestartArmed(Enum.Parse<RebootCountdownWindow.Choice>(decision));

    [Fact]
    public void Stop_reports_that_no_restart_is_armed()
    {
        Assert.False(KeepsArmed(nameof(RebootCountdownWindow.Choice.Stop)));
    }

    [Theory]
    [InlineData(nameof(RebootCountdownWindow.Choice.None))]
    [InlineData(nameof(RebootCountdownWindow.Choice.Elapsed))]
    [InlineData(nameof(RebootCountdownWindow.Choice.RestartNow))]
    [InlineData(nameof(RebootCountdownWindow.Choice.Postpone))]
    [InlineData(nameof(RebootCountdownWindow.Choice.CancelledElsewhere))]
    public void Every_other_outcome_keeps_the_restart_armed(string decision)
    {
        Assert.True(KeepsArmed(decision));
    }
}
