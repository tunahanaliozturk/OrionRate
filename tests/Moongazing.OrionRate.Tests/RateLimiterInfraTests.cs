namespace Moongazing.OrionRate.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;

using Moongazing.Orion.Abstractions.Diagnostics;
using Moongazing.OrionClock;
using Moongazing.OrionClock.Testing;
using Moongazing.OrionRate.Diagnostics;
using Moongazing.OrionRate.DependencyInjection;

using Xunit;

/// <summary>DI wiring, telemetry, and thread-safety of the limiter.</summary>
public sealed class RateLimiterInfraTests
{
    [Fact]
    public async Task AddOrionRate_wires_a_usable_limiter()
    {
        var services = new ServiceCollection();
        services.AddOrionRate(o => o.AddPolicy("api", p => p.TokenBucket(2, TimeSpan.FromMinutes(1))));
        using var provider = services.BuildServiceProvider();

        var limiter = provider.GetRequiredService<IRateLimiter>();
        Assert.True((await limiter.AcquireAsync("api", "k")).Allowed);
        Assert.True((await limiter.AcquireAsync("api", "k")).Allowed);
        Assert.False((await limiter.AcquireAsync("api", "k")).Allowed);
    }

    [Fact]
    public void Duplicate_policy_names_and_algorithm_less_policies_are_rejected()
    {
        var options = new RateLimiterOptions();
        options.AddPolicy("api", p => p.TokenBucket(1, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => options.AddPolicy("api", p => p.TokenBucket(1, TimeSpan.FromSeconds(1))));
        Assert.Throws<InvalidOperationException>(() => new RateLimiterOptions().AddPolicy("x", _ => { }));
    }

    [Fact]
    public async Task Decisions_are_recorded_to_telemetry_tagged_by_policy()
    {
        using var diagnostics = new RateDiagnostics();
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (OrionInstrumentation.ListensTo(instrument, diagnostics))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var policy = "";
            foreach (var t in tags)
            {
                if (t.Key == RateDiagnostics.PolicyTagKey)
                {
                    policy = t.Value?.ToString() ?? "";
                }
            }
            lock (counts) { counts[$"{instrument.Name}|{policy}"] = counts.GetValueOrDefault($"{instrument.Name}|{policy}") + value; }
        });
        listener.Start();

        var clock = new FakeOrionClock();
        var options = new RateLimiterOptions();
        options.AddPolicy("api", p => p.TokenBucket(1, TimeSpan.FromMinutes(1)));
        var limiter = new RateLimiter(options.Build(), clock, diagnostics);

        await limiter.AcquireAsync("api", "k"); // allowed
        await limiter.AcquireAsync("api", "k"); // throttled

        Assert.Equal(1, counts[$"{OrionTelemetry.MetricName("rate", "allowed")}|api"]);
        Assert.Equal(1, counts[$"{OrionTelemetry.MetricName("rate", "throttled")}|api"]);
    }

    [Fact]
    public async Task Concurrent_acquires_on_one_key_never_over_admit()
    {
        // The per-key lock makes check-and-consume atomic: a bucket of 50 admits exactly 50 even
        // when 500 requests race.
        var clock = new FakeOrionClock();
        var options = new RateLimiterOptions();
        options.AddPolicy("api", p => p.TokenBucket(50, TimeSpan.FromHours(1))); // negligible refill during the test
        var limiter = new RateLimiter(options.Build(), clock, new RateDiagnostics());

        var tasks = Enumerable.Range(0, 500)
            .Select(_ => Task.Run(async () => (await limiter.AcquireAsync("api", "hot")).Allowed))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(50, results.Count(allowed => allowed));
    }

    [Fact]
    public async Task Idle_partitions_are_evicted_so_the_key_space_cannot_grow_without_bound()
    {
        // Keys come from API keys and client IPs, so the key space belongs to the caller. A partition
        // whose bucket has refilled to full is indistinguishable from one that was never created, so
        // holding it is pure leak.
        var clock = new FakeOrionClock();
        var options = new RateLimiterOptions();
        options.AddPolicy("api", p => p.TokenBucket(permit: 10, per: TimeSpan.FromSeconds(1)));
        var limiter = new RateLimiter(options.Build(), clock, new RateDiagnostics());

        for (var i = 0; i < 5000; i++)
        {
            await limiter.AcquireAsync("api", $"ip:10.0.{i / 256}.{i % 256}");
        }
        Assert.True(limiter.PartitionCount > 4000, "the one-shot keys should still be held while they are in use");

        clock.Advance(TimeSpan.FromHours(1)); // every one of those buckets is long since full
        await limiter.AcquireAsync("api", "ip:live");

        Assert.True(limiter.PartitionCount < 100, $"{limiter.PartitionCount} partitions still held an hour after a 1-second bucket last saw traffic");
    }

    [Fact]
    public async Task Eviction_does_not_forget_a_partition_that_is_still_spending()
    {
        var clock = new FakeOrionClock();
        var options = new RateLimiterOptions();
        options.AddPolicy("api", p => p.TokenBucket(permit: 10, per: TimeSpan.FromHours(24)));
        var limiter = new RateLimiter(options.Build(), clock, new RateDiagnostics());

        for (var i = 0; i < 5000; i++)
        {
            await limiter.AcquireAsync("api", $"ip:10.0.{i / 256}.{i % 256}");
        }
        for (var i = 0; i < 10; i++)
        {
            Assert.True((await limiter.AcquireAsync("api", "ip:hot")).Allowed);
        }

        // A sweep runs, but 90 minutes of a 24-hour period is 0.6 of a token: nothing is back to full.
        clock.Advance(TimeSpan.FromMinutes(90));
        Assert.False((await limiter.AcquireAsync("api", "ip:hot")).Allowed, "an in-use partition was evicted and handed back a fresh bucket");
    }

    [Fact]
    public async Task A_sweep_running_under_a_concurrent_race_still_admits_exactly_the_bucket()
    {
        // The sweep walks partitions while acquisitions are in flight. With the clock frozen for the
        // duration of the race nothing refills, so the hot key must admit exactly its capacity no
        // matter how the sweep interleaves - a sweep that dropped a partition still in use would
        // hand the next caller a fresh full bucket and push the count past 50.
        var clock = new FakeOrionClock();
        var options = new RateLimiterOptions();
        options.AddPolicy("api", p => p.TokenBucket(permit: 50, per: TimeSpan.FromHours(1)));
        var limiter = new RateLimiter(options.Build(), clock, new RateDiagnostics());

        for (var i = 0; i < 1000; i++)
        {
            await limiter.AcquireAsync("api", $"cold:{i}");
        }
        clock.Advance(TimeSpan.FromHours(2)); // every cold partition is now full, and a sweep is due

        var results = await Task.WhenAll(Enumerable.Range(0, 500)
            .Select(_ => Task.Run(async () => (await limiter.AcquireAsync("api", "hot")).Allowed)));

        Assert.Equal(50, results.Count(allowed => allowed));
    }

    [Fact]
    public async Task Policies_do_not_share_state_through_a_composite_key_collision()
    {
        // ("a b", "c") and ("a", "b c") must not land on the same partition. Any separator that can
        // occur in a policy name or a key - a space, a colon, a pipe - lets one policy spend another
        // policy's budget.
        var clock = new FakeOrionClock();
        var options = new RateLimiterOptions();
        options.AddPolicy("a b", p => p.TokenBucket(1, TimeSpan.FromHours(1)));
        options.AddPolicy("a", p => p.TokenBucket(1, TimeSpan.FromHours(1)));
        var limiter = new RateLimiter(options.Build(), clock, new RateDiagnostics());

        Assert.True((await limiter.AcquireAsync("a b", "c")).Allowed);
        Assert.True((await limiter.AcquireAsync("a", "b c")).Allowed, "a second policy was charged against the first one's bucket");
        Assert.Equal(2, limiter.PartitionCount);
    }

    [Fact]
    public void Key_segments_cannot_be_forged_into_another_identity()
    {
        // Key.Of joins a dimension to its value with ':' and Key.Combine joins segments with '|'.
        // Unchecked, Key.Tenant.Of("acme|route:/v1/charges") is byte-for-byte the composite key for
        // tenant acme on that route, so a caller-supplied tenant id chooses its own partition and can
        // spend another identity's budget.
        Assert.Throws<ArgumentException>(() => Key.Tenant.Of("acme|route:/v1/charges"));
        Assert.Throws<ArgumentException>(() => Key.Custom("a:b"));
        Assert.Throws<ArgumentException>(() => Key.Combine("tenant:a|route:/x", "route:/y"));

        // Well-formed keys are untouched.
        Assert.Equal(
            "tenant:acme|route:/v1/charges",
            Key.Combine(Key.Tenant.Of("acme"), Key.Route.Of("/v1/charges")));
    }

    [Fact]
    public void Key_helper_builds_and_composes_consistent_keys()
    {
        Assert.Equal("tenant:acme", Key.Tenant.Of("acme"));
        Assert.Equal("ip:203.0.113.4", Key.Ip.Of("203.0.113.4"));
        Assert.Equal("tenant:acme|route:/v1/charges", Key.Combine(Key.Tenant.Of("acme"), Key.Route.Of("/v1/charges")));
        Assert.Equal("region", Key.Custom("region").Name);
    }
}
