using COMS_MVC.Helpers;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Regression tests for the shared write-throttle helper used by support
/// messages and ticket creation (abuse prevention).
/// </summary>
public sealed class RateThrottleTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public void Dispose() => _cache.Dispose();

    /// <summary>Production message-throttle parameters (see CustomerServiceController).</summary>
    private static (TimeSpan Cooldown, int Max, TimeSpan Window) MessageConfig =>
        (TimeSpan.FromSeconds(5), 30, TimeSpan.FromHours(1));

    /// <summary>Production ticket-creation parameters.</summary>
    private static (TimeSpan Cooldown, int Max, TimeSpan Window) TicketConfig =>
        (TimeSpan.FromSeconds(30), 10, TimeSpan.FromHours(1));

    [Fact]
    public void First_Check_Passes()
    {
        var (cooldown, max, window) = TicketConfig;
        var ok = RateThrottle.Check(_cache, "t1", cooldown, max, window,
            "wait", "limit", out var error);
        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void Second_Check_WithinCooldown_IsRejected()
    {
        var (cooldown, max, window) = MessageConfig;
        Assert.True(RateThrottle.Check(_cache, "t2", cooldown, max, window, "cool", "limit", out _));
        Assert.False(RateThrottle.Check(_cache, "t2", cooldown, max, window, "cool", "limit", out var error));
        Assert.Equal("cool", error);
    }

    [Fact]
    public void Rejected_Throws_DoNot_ConsumeBudget()
    {
        // Within-cooldown rejections must not increment the counter, or the
        // hourly budget would drain on failed attempts.
        var (cooldown, max, window) = MessageConfig;
        RateThrottle.Check(_cache, "t3", cooldown, max, window, "cool", "limit", out _);
        RateThrottle.Check(_cache, "t3", cooldown, max, window, "cool", "limit", out _);
        RateThrottle.Check(_cache, "t3", cooldown, max, window, "cool", "limit", out _);
        Assert.Equal(1, _cache.Get<int>("t3:count"));
    }

    [Fact]
    public void Budget_Exhaustion_IsRejected_WithLimitMessage()
    {
        // Zero cooldown makes each accepted action count toward the budget.
        Assert.True(CheckWith("t4", TimeSpan.Zero, maxActionsPerWindow: 2));
        Assert.True(CheckWith("t4", TimeSpan.Zero, maxActionsPerWindow: 2));
        var blocked = RateThrottle.Check(_cache, "t4", TimeSpan.Zero, 2,
            TimeSpan.FromHours(1), "cool", "limit", out var error);
        Assert.False(blocked);
        Assert.Equal("limit", error);
    }

    [Fact]
    public void Ticket_Parameters_AreStricter_ThanMessageParameters()
    {
        Assert.True(TicketConfig.Cooldown > MessageConfig.Cooldown);
        Assert.True(TicketConfig.Max < MessageConfig.Max);
    }

    private bool CheckWith(string prefix, TimeSpan cooldown, int maxActionsPerWindow)
    {
        return RateThrottle.Check(_cache, prefix, cooldown, maxActionsPerWindow,
            TimeSpan.FromHours(1), "cool", "limit", out _);
    }

    [Fact]
    public void Accepted_Actions_IncrementCounter()
    {
        RateThrottle.Check(_cache, "t6", TimeSpan.Zero, 5, TimeSpan.FromHours(1), "cool", "limit", out _);
        RateThrottle.Check(_cache, "t6", TimeSpan.Zero, 5, TimeSpan.FromHours(1), "cool", "limit", out _);
        Assert.Equal(2, _cache.Get<int>("t6:count"));
    }
}
