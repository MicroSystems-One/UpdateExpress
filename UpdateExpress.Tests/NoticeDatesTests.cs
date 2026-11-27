using System.Globalization;
using UpdateExpress.Services;
using Xunit;

namespace UpdateExpress.Tests;

public class NoticeDatesTests
{
  private static readonly DateTime Monday = new(2026, 11, 30, 17, 0, 0);

    [Fact]
    public void English_day_and_time_ignores_current_culture()
    {
        WithCulture("nb-NO", () =>
        {
            Assert.Equal("Mon 30 Nov at 17:00.", NoticeDates.DayAndTime(Monday, norwegian: false));
            Assert.Equal("Mon 30 Nov at 17:00", NoticeDates.DayAndTimeInline(Monday, norwegian: false));
        });
    }

    [Fact]
    public void Norwegian_day_and_time_ignores_current_culture()
    {
        WithCulture("en-US", () =>
        {
            string line = NoticeDates.DayAndTime(Monday, norwegian: true);
            Assert.StartsWith("man.", line, StringComparison.Ordinal);
            Assert.Contains("30.", line, StringComparison.Ordinal);
            Assert.Contains("nov.", line, StringComparison.Ordinal);
            Assert.Contains("kl. 17:00", line, StringComparison.Ordinal);
        });
    }

    private static void WithCulture(string name, Action body)
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        try { body(); }
        finally { CultureInfo.CurrentCulture = before; }
    }
}