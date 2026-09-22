namespace Moongazing.OrionRate.Tests;

using System;
using System.Threading.Tasks;

using Moongazing.OrionClock.Testing;
using Moongazing.OrionRate.Diagnostics;

using Xunit;

/// <summary>The token-bucket algorithm, driven entirely by a fake clock — no real waiting.</summary>
public sealed class TokenBucketTests
{
    private static RateLimiter Limiter(FakeOrionClock clock, Action<RateLimiterOptions> configure)
    {
        var options = new RateLimiterOptions();
        configure(options);
        return new RateLimiter(options.Build(), clock, new RateDiagnostics());
    }

    [Fact]
    public async Task Draining_a_100_per_minute_bucket_throttles_request_101_and_refills_after_60s()
    {
        // Wave 1 exit criterion.
        var clock = new FakeOrionClock();
        var limiter = Limiter(clock, o => o.AddPolicy("api", p => p.TokenBucket(permit: 100, per: TimeSpan.FromMinutes(1))));

        // Drain all 100 permits with no time passing (the fake clock is frozen).
        for (var i = 0; i < 100; i++)
        {
            var ok = await limiter.AcquireAsync("api", "tenant:acme");
            Assert.True(ok.Allowed, $"request {i + 1} should be allowed");
        }

        // Request 101 is throttled, with a retry-after equal to the time to accrue one token
        // (1 token / (100 per 60s) = 0.6s).
        var throttled = await limiter.AcquireAsync("api", "tenant:acme");
        Assert.False(throttled.Allowed);
        Assert.Equal(100, throttled.Limit);
        Assert.Equal(0, throttled.Remaining);
        Assert.Equal(0.6, throttled.RetryAfter.TotalSeconds, precision: 2);

        // Advancing the clock by 60s refills the whole bucket.
        clock.Advance(TimeSpan.FromSeconds(60));
        var afterRefill = await limiter.AcquireAsync("api", "tenant:acme");
        Assert.True(afterRefill.Allowed);
    }

    [Fact]
    public async Task Partial_refill_restores_permits_proportionally()
    {
        var clock = new FakeOrionClock();
        var limiter = Limiter(clock, o => o.AddPolicy("api", p => p.TokenBucket(permit: 60, per: TimeSpan.FromMinutes(1)))); // 1/sec

        for (var i = 0; i < 60; i++)
        {
            await limiter.AcquireAsync("api", "k");
        }
        Assert.False((await limiter.AcquireAsync("api", "k")).Allowed);

        clock.Advance(TimeSpan.FromSeconds(10)); // 1/sec * 10s = 10 tokens back
        for (var i = 0; i < 10; i++)
        {
            Assert.True((await limiter.AcquireAsync("api", "k")).Allowed, $"refilled permit {i + 1}");
        }
        Assert.False((await limiter.AcquireAsync("api", "k")).Allowed); // 11th exceeds the refill
    }

    [Fact]
    public async Task Burst_headroom_admits_a_spike_above_the_sustained_rate()
    {
        var clock = new FakeOrionClock();
        var limiter = Limiter(clock, o => o.AddPolicy("api", p => p.TokenBucket(permit: 10, per: TimeSpan.FromMinutes(1), burst: 5)));

        // Capacity is permit + burst = 15, so 15 requests pass in an instant.
        for (var i = 0; i < 15; i++)
        {
            Assert.True((await limiter.AcquireAsync("api", "k")).Allowed, $"burst request {i + 1}");
        }
        Assert.False((await limiter.AcquireAsync("api", "k")).Allowed);
    }

    [Fact]
    public async Task Keys_are_limited_independently()
    {
        var clock = new FakeOrionClock();
        var limiter = Limiter(clock, o => o.AddPolicy("api", p => p.TokenBucket(permit: 1, per: TimeSpan.FromMinutes(1))));

        Assert.True((await limiter.AcquireAsync("api", "tenant:a")).Allowed);
        Assert.False((await limiter.AcquireAsync("api", "tenant:a")).Allowed); // a is drained
        Assert.True((await limiter.AcquireAsync("api", "tenant:b")).Allowed);  // b is independent
    }

    [Fact]
    public async Task Waiting_exactly_the_advertised_retry_after_is_admitted()
    {
        // 3 per second: one token accrues in 1/3s, which is not a whole number of ticks. Truncating
        // that down advertises an instant at which the token does not yet exist.
        var clock = new FakeOrionClock();
        var limiter = Limiter(clock, o => o.AddPolicy("api", p => p.TokenBucket(permit: 3, per: TimeSpan.FromSeconds(1))));

        for (var i = 0; i < 3; i++)
        {
            await limiter.AcquireAsync("api", "k");
        }
        var throttled = await limiter.AcquireAsync("api", "k");
        Assert.False(throttled.Allowed);
        Assert.True(throttled.RetryAfter > TimeSpan.Zero, $"a throttle must never advertise a zero wait (got {throttled.RetryAfter})");

        clock.Advance(throttled.RetryAfter);
        var retry = await limiter.AcquireAsync("api", "k");
        Assert.True(retry.Allowed, $"waited the advertised {throttled.RetryAfter} and was rejected again (next {retry.RetryAfter})");
    }

    [Fact]
    public async Task A_cost_above_the_bucket_capacity_is_rejected_not_promised_an_impossible_retry()
    {
        var clock = new FakeOrionClock();
        var limiter = Limiter(clock, o => o.AddPolicy("api", p => p.TokenBucket(permit: 5, per: TimeSpan.FromSeconds(1))));

        // 50 permits never fit in a 5-token bucket. Throttling would advertise a RetryAfter the
        // caller can wait out forever and still be rejected.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => limiter.AcquireAsync("api", "k", permits: 50).AsTask());

        // Burst counts toward capacity, so a cost the bucket can actually hold still goes through.
        var withBurst = Limiter(clock, o => o.AddPolicy("api", p => p.TokenBucket(permit: 5, per: TimeSpan.FromSeconds(1), burst: 5)));
        Assert.True((await withBurst.AcquireAsync("api", "k", permits: 10)).Allowed);
    }

    [Fact]
    public async Task An_unknown_policy_throws()
    {
        var clock = new FakeOrionClock();
        var limiter = Limiter(clock, o => o.AddPolicy("api", p => p.TokenBucket(1, TimeSpan.FromSeconds(1))));

        await Assert.ThrowsAsync<System.Collections.Generic.KeyNotFoundException>(
            () => limiter.AcquireAsync("nope", "k").AsTask());
    }
}
