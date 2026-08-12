using DevStudio.Application.Scheduling;

namespace DevStudio.Tests;

public class CronExpressionTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Theory]
    [InlineData("* * * * *")]
    [InlineData("0 9 * * 1-5")]
    [InlineData("*/15 * * * *")]
    [InlineData("0 0 1 1 *")]
    [InlineData("30 6,18 * * SUN")]
    public void Parses_valid_expressions(string expression) =>
        Assert.True(CronExpression.TryParse(expression, out _, out _));

    [Theory]
    [InlineData("")]
    [InlineData("* * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("a * * * *")]
    public void Rejects_invalid_expressions(string expression) =>
        Assert.False(CronExpression.TryParse(expression, out _, out _));

    [Fact]
    public void Every_minute_fires_on_the_next_minute()
    {
        var cron = CronExpression.Parse("* * * * *");
        var from = new DateTimeOffset(2026, 3, 14, 10, 30, 25, TimeSpan.Zero);

        var next = cron.GetNextOccurrence(from, Utc);

        Assert.Equal(new DateTimeOffset(2026, 3, 14, 10, 31, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Weekday_schedule_skips_the_weekend()
    {
        var cron = CronExpression.Parse("0 9 * * 1-5");
        // A Saturday.
        var from = new DateTimeOffset(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);

        var next = cron.GetNextOccurrence(from, Utc);

        Assert.Equal(new DateTimeOffset(2026, 3, 16, 9, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Step_values_land_on_each_interval()
    {
        var cron = CronExpression.Parse("*/15 * * * *");
        var from = new DateTimeOffset(2026, 3, 14, 10, 3, 0, TimeSpan.Zero);

        var next = cron.GetNextOccurrence(from, Utc);

        Assert.Equal(15, next!.Value.Minute);
    }

    [Fact]
    public void Day_of_month_and_day_of_week_are_combined_with_or()
    {
        // The 1st of the month, or any Monday.
        var cron = CronExpression.Parse("0 0 1 * 1");
        var from = new DateTimeOffset(2026, 3, 3, 0, 0, 0, TimeSpan.Zero); // Tuesday

        var next = cron.GetNextOccurrence(from, Utc);

        Assert.Equal(DayOfWeek.Monday, next!.Value.DayOfWeek);
        Assert.Equal(9, next.Value.Day);
    }

    [Fact]
    public void Occurrences_are_returned_in_the_requested_zone_but_expressed_in_utc()
    {
        var cron = CronExpression.Parse("0 9 * * *");
        var melbourne = FindZone("Australia/Melbourne", "AUS Eastern Standard Time");
        var from = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var next = cron.GetNextOccurrence(from, melbourne);

        // 09:00 in Melbourne during winter (UTC+10) is 23:00 UTC the day before.
        Assert.Equal(23, next!.Value.UtcDateTime.Hour);
    }

    private static TimeZoneInfo FindZone(params string[] ids)
    {
        foreach (var id in ids)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // Try the next id — Windows and Linux disagree on zone naming.
            }
        }

        throw new InvalidOperationException("No usable time zone found for this test.");
    }
}
