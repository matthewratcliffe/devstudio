using DevStudio.Application.Queues;

namespace DevStudio.Tests;

public sealed class UsageLimitBackoffTests
{
    [Fact]
    public void Uses_one_minute_after_a_reported_reset_time()
    {
        var now = new DateTimeOffset(2026, 8, 16, 8, 0, 0, TimeSpan.FromHours(10));

        Assert.True(UsageLimitBackoff.TryGetUntil(
            "You've reached your usage limit; resets at 6:00 PM.", now, out var until));

        Assert.Equal(new DateTimeOffset(2026, 8, 16, 18, 1, 0, TimeSpan.FromHours(10)), until);
    }

    [Fact]
    public void Uses_two_hours_when_no_reset_time_is_present()
    {
        var now = new DateTimeOffset(2026, 8, 16, 8, 0, 0, TimeSpan.Zero);

        Assert.True(UsageLimitBackoff.TryGetUntil("Rate limit exceeded.", now, out var until));

        Assert.Equal(now.AddHours(2), until);
    }

    [Fact]
    public void Ignores_unrelated_errors()
    {
        Assert.False(UsageLimitBackoff.TryGetUntil("The repository is not clean.", DateTimeOffset.UtcNow, out _));
    }
}
