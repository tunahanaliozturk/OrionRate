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
    public void Key_helper_builds_and_composes_consistent_keys()
    {
        Assert.Equal("tenant:acme", Key.Tenant.Of("acme"));
        Assert.Equal("ip:203.0.113.4", Key.Ip.Of("203.0.113.4"));
        Assert.Equal("tenant:acme|route:/v1/charges", Key.Combine(Key.Tenant.Of("acme"), Key.Route.Of("/v1/charges")));
        Assert.Equal("region", Key.Custom("region").Name);
    }
}
