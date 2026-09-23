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

    [Fact]
    public async Task Retry_after_covers_every_slot_a_multi_permit_request_needs()
    {
        // Five requests one second apart fill a 5-slot window. A 3-permit request needs three slots,
        // so it can only succeed once the third-oldest ages out - not the first.
        var clock = new FakeOrionClock();
        var limiter = Limiter(clock, o => o.AddPolicy("login", p => p.SlidingWindow(permit: 5, window: TimeSpan.FromSeconds(10))));

        for (var i = 0; i < 5; i++)
        {
            Assert.True((await limiter.AcquireAsync("login", "k")).Allowed);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var throttled = await limiter.AcquireAsync("login", "k", permits: 3);
        Assert.False(throttled.Allowed);

        clock.Advance(throttled.RetryAfter);
        var retry = await limiter.AcquireAsync("login", "k", permits: 3);
        Assert.True(retry.Allowed, $"waited the advertised {throttled.RetryAfter} and was rejected again (next {retry.RetryAfter})");
    }

    [Fact]
    public async Task A_request_ages_out_at_exactly_the_window_length_and_not_a_tick_sooner()
    {
        var clock = new FakeOrionClock();
        var limiter = Limiter(clock, o => o.AddPolicy("login", p => p.SlidingWindow(permit: 1, window: TimeSpan.FromSeconds(10))));

        Assert.True((await limiter.AcquireAsync("login", "k")).Allowed); // t=0

        clock.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1));
        Assert.False((await limiter.AcquireAsync("login", "k")).Allowed, "one tick short of the window still counts");

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True((await limiter.AcquireAsync("login", "k")).Allowed, "at exactly the window length the request has aged out");
    }

    [Fact]
    public async Task Retry_after_counts_permits_inside_weighted_timestamp_batches()
    {
        var clock = new FakeOrionClock();
        var limiter = Limiter(clock, o => o.AddPolicy("login", p => p.SlidingWindow(5, TimeSpan.FromSeconds(10))));

        Assert.True((await limiter.AcquireAsync("login", "k", permits: 2)).Allowed); // t=0
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True((await limiter.AcquireAsync("login", "k", permits: 3)).Allowed); // t=2
        clock.Advance(TimeSpan.FromSeconds(1)); // t=3

        var throttled = await limiter.AcquireAsync("login", "k", permits: 4);
        Assert.False(throttled.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(9), throttled.RetryAfter); // four slots free only at t=12

        clock.Advance(throttled.RetryAfter - TimeSpan.FromTicks(1));
        Assert.False((await limiter.AcquireAsync("login", "k", permits: 4)).Allowed);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True((await limiter.AcquireAsync("login", "k", permits: 4)).Allowed);
    }

    [Fact]
    public void Large_cost_keeps_allocations_bounded_by_the_number_of_acquisitions()
    {
        var clock = new FakeOrionClock();
        var policy = new SlidingWindowPolicy("bulk", 1_000_000, TimeSpan.FromMinutes(1));
        object? warmup = null;
        policy.Evaluate(ref warmup, clock, 1);

        object? state = null;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = policy.Evaluate(ref state, clock, 1_000_000);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(result.Allowed);
        Assert.Equal(0, result.Remaining);
        Assert.True(allocated < 64 * 1024, $"one acquisition allocated {allocated} bytes");
    }
}
