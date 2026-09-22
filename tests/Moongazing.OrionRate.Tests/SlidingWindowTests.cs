namespace Moongazing.OrionRate.Tests;

using System;
using System.Threading.Tasks;

using Moongazing.OrionClock.Testing;
using Moongazing.OrionRate.Diagnostics;

using Xunit;

/// <summary>The sliding-window algorithm, driven by a fake clock.</summary>
public sealed class SlidingWindowTests
{
    private static RateLimiter Limiter(FakeOrionClock clock, Action<RateLimiterOptions> configure)
    {
        var options = new RateLimiterOptions();
        configure(options);
        return new RateLimiter(options.Build(), clock, new RateDiagnostics());
    }

    [Fact]
    public async Task Admits_up_to_the_limit_then_throttles_within_the_window()
    {
        var clock = new FakeOrionClock();
        var limiter = Limiter(clock, o => o.AddPolicy("login", p => p.SlidingWindow(permit: 5, window: TimeSpan.FromMinutes(15))));

        for (var i = 0; i < 5; i++)
        {
            Assert.True((await limiter.AcquireAsync("login", "ip:203.0.113.4")).Allowed, $"attempt {i + 1}");
        }
        var throttled = await limiter.AcquireAsync("login", "ip:203.0.113.4");
        Assert.False(throttled.Allowed);
        Assert.True(throttled.RetryAfter > TimeSpan.Zero && throttled.RetryAfter <= TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task Requests_age_out_of_the_trailing_window()
    {
        var clock = new FakeOrionClock();
        var limiter = Limiter(clock, o => o.AddPolicy("login", p => p.SlidingWindow(permit: 2, window: TimeSpan.FromSeconds(10))));

        Assert.True((await limiter.AcquireAsync("login", "k")).Allowed);  // t=0
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.True((await limiter.AcquireAsync("login", "k")).Allowed);  // t=6
        Assert.False((await limiter.AcquireAsync("login", "k")).Allowed); // t=6, both still in window

        // Advance past t=10 so the first request (t=0) ages out, freeing one slot.
        clock.Advance(TimeSpan.FromSeconds(5)); // t=11
        Assert.True((await limiter.AcquireAsync("login", "k")).Allowed);  // first slot freed
        Assert.False((await limiter.AcquireAsync("login", "k")).Allowed); // second still occupied (t=6, in window until t=16)
    }

    [Fact]
    public async Task Retry_after_reports_when_the_oldest_request_ages_out()
    {
        var clock = new FakeOrionClock();
        var limiter = Limiter(clock, o => o.AddPolicy("login", p => p.SlidingWindow(permit: 1, window: TimeSpan.FromSeconds(10))));

        Assert.True((await limiter.AcquireAsync("login", "k")).Allowed); // t=0
        clock.Advance(TimeSpan.FromSeconds(4));                          // t=4
        var throttled = await limiter.AcquireAsync("login", "k");
        Assert.False(throttled.Allowed);
        // The t=0 request ages out at t=10, so ~6s from now.
        Assert.Equal(6, throttled.RetryAfter.TotalSeconds, precision: 1);
    }

    [Fact]
    public async Task A_cost_above_the_window_limit_is_rejected_not_promised_an_impossible_retry()
    {
        var clock = new FakeOrionClock();
        var limiter = Limiter(clock, o => o.AddPolicy("login", p => p.SlidingWindow(permit: 5, window: TimeSpan.FromSeconds(10))));

        // A 50-permit cost never fits a 5-slot window, so a throttle would advertise a RetryAfter
        // that is never true.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => limiter.AcquireAsync("login", "k", permits: 50).AsTask());

        Assert.True((await limiter.AcquireAsync("login", "k", permits: 5)).Allowed);
    }
}
