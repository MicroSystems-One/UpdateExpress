using UpdateExpress.Services;
using Xunit;

namespace UpdateExpress.Tests;

public class CategoryPromptWaitingTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 20, 10, 0);

    private static PromptUpdateItem Item(string id, string title = "Update") =>
        new() { UpdateId = id, Title = title };

    private static CategoryPromptState Prompt(string state, DateTime? scheduledAt = null, params PromptUpdateItem[] updates) =>
        new() { State = state, ScheduledInstallAt = scheduledAt, Updates = updates.ToList() };

    [Fact]
    public void AwaitingPromptWithUpdates_IsWaiting()
    {
        Assert.True(Prompt("awaitingPrompt", null, Item("a")).IsWaitingForUser(Now));
    }

    [Fact]
    public void AwaitingPromptWithoutUpdates_IsNotWaiting()
    {
        Assert.False(Prompt("awaitingPrompt").IsWaitingForUser(Now));
    }

    [Fact]
    public void ScheduledInstallWhoseTimeHasPassed_IsWaiting()
    {
        Assert.True(Prompt("scheduled", Now.AddMinutes(-1), Item("a")).IsWaitingForUser(Now));
    }

    [Fact]
    public void ScheduledInstallStillAhead_IsNotWaiting()
    {
        Assert.False(Prompt("scheduled", Now.AddMinutes(1), Item("a")).IsWaitingForUser(Now));
    }

    [Theory]
    [InlineData("snoozed")]
    [InlineData("installing")]
    [InlineData("cleared")]
    public void OtherStates_AreNotWaiting(string state)
    {
        Assert.False(Prompt(state, Now.AddMinutes(-1), Item("a")).IsWaitingForUser(Now));
    }

    [Fact]
    public void UpdatesWaitingForUser_TakesOnlyTheWaitingCategories()
    {
        var waiting = CategoryPromptState.UpdatesWaitingForUser(
            new[]
            {
                Prompt("awaitingPrompt", null, Item("critical-1"), Item("critical-2")),
                Prompt("snoozed", null, Item("general-1")),
                Prompt("scheduled", Now.AddHours(-1), Item("driver-1")),
                Prompt("cleared"),
            },
            Now);

        Assert.Equal(new[] { "critical-1", "critical-2", "driver-1" }, waiting.Select(i => i.UpdateId));
    }

    [Fact]
    public void NotAlreadyListed_DropsUpdatesTheCallerListsAndRepeats()
    {
        var rest = CategoryPromptState.NotAlreadyListed(
            new[] { Item("a"), Item("B"), Item("c"), Item("c"), Item("", "No id 1"), Item("", "No id 2") },
            new[] { "b", "", "x" });

        Assert.Equal(new[] { "a", "c", "", "" }, rest.Select(i => i.UpdateId));
        Assert.Equal(new[] { "No id 1", "No id 2" }, rest.Where(i => i.UpdateId == "").Select(i => i.Title));
    }

    [Fact]
    public void RemoveUpdates_DropsTheMatchingUpdatesAndReportsTheChange()
    {
        var prompt = Prompt("awaitingPrompt", null, Item("a"), Item("b"), Item("c"));

        Assert.True(prompt.RemoveUpdates(item => item.UpdateId is "a" or "c"));

        Assert.Equal(new[] { "b" }, prompt.Updates.Select(i => i.UpdateId));
        Assert.True(prompt.IsWaitingForUser(Now));
    }

    [Fact]
    public void RemoveUpdates_WhenEveryUpdateIsGone_NothingIsWaiting()
    {
        var prompt = Prompt("awaitingPrompt", null, Item("a"), Item("b"));

        Assert.True(prompt.RemoveUpdates(_ => true));

        Assert.Empty(prompt.Updates);
        Assert.False(prompt.IsWaitingForUser(Now));
    }

    [Fact]
    public void RemoveUpdates_WithNoMatch_ReportsNoChange()
    {
        var prompt = Prompt("awaitingPrompt", null, Item("a"));

        Assert.False(prompt.RemoveUpdates(item => item.UpdateId == "x"));

        Assert.Single(prompt.Updates);
    }

    [Fact]
    public void RemoveUpdates_KeepsAnUpdateWithoutId()
    {
        var prompt = Prompt("awaitingPrompt", null, Item("", "No id"), Item("a"));

        Assert.True(prompt.RemoveUpdates(_ => true));

        Assert.Equal(new[] { "No id" }, prompt.Updates.Select(i => i.Title));
    }

    [Fact]
    public void RemoveUpdates_LeavesAQueueThatIsInstalling()
    {
        var prompt = Prompt("installing", null, Item("a"));

        Assert.False(prompt.RemoveUpdates(_ => true));

        Assert.Single(prompt.Updates);
    }
}
