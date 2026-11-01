using System;
using Xunit;

namespace Moonfin.Households.Tests;

public class ActionRateLimiterTests
{
    private static readonly Guid Manager = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid Other = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    [Fact]
    public void Turns_away_the_excess_until_the_window_moves_on()
    {
        var now = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var limiter = new ActionRateLimiter(2, TimeSpan.FromMinutes(1), () => now);

        Assert.Equal(0, limiter.TryTake(Manager));
        now = now.AddSeconds(10);
        Assert.Equal(0, limiter.TryTake(Manager));
        Assert.Equal(50, limiter.TryTake(Manager));

        // Someone else has their own window
        Assert.Equal(0, limiter.TryTake(Other));

        now = now.AddSeconds(50);
        Assert.Equal(0, limiter.TryTake(Manager));
    }

    [Fact]
    public void A_refused_action_is_not_counted()
    {
        var now = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var limiter = new ActionRateLimiter(1, TimeSpan.FromSeconds(30), () => now);

        Assert.Equal(0, limiter.TryTake(Manager));
        for (var i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryTake(Manager) > 0);
        }

        now = now.AddSeconds(30);
        Assert.Equal(0, limiter.TryTake(Manager));
    }
}
