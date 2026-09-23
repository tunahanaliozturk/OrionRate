namespace Moongazing.OrionRate.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Moongazing.Orion.Abstractions.Time;
using Moongazing.OrionClock.Testing;
using Moongazing.OrionRate.Diagnostics;

using Xunit;

public sealed class SweepRaceTests
{
    [Fact]
    public async Task An_acquisition_selected_before_retirement_retries_the_replacement()
    {
        var clock = new FakeOrionClock();
        using var policy = new ControlledPolicy();
        var limiter = new RateLimiter(
            new Dictionary<string, RateLimitPolicy> { ["api"] = policy }, clock, new RateDiagnostics());

        Assert.True((await limiter.AcquireAsync("api", "target")).Allowed);
        for (var i = 0; i < 260; i++)
        {
            await limiter.AcquireAsync("api", $"other:{i}");
        }

        clock.Advance(TimeSpan.FromMinutes(2));
        using var lookupReached = new ManualResetEventSlim();
        using var continueLookup = new ManualResetEventSlim();
        var paused = 0;
        limiter.AfterEntryLookupForTests = key =>
        {
            if (key == "target" && Interlocked.Exchange(ref paused, 1) == 0)
            {
                lookupReached.Set();
                Assert.True(continueLookup.Wait(TimeSpan.FromSeconds(10)), "the selected acquisition was never released");
            }
        };

        var sweeping = Task.Run(async () => await limiter.AcquireAsync("api", "trigger"));
        Assert.True(policy.SweepReached.Wait(TimeSpan.FromSeconds(10)), "the sweep did not reach the target");

        var selected = Task.Run(async () => await limiter.AcquireAsync("api", "target"));
        Assert.True(lookupReached.Wait(TimeSpan.FromSeconds(10)), "the acquisition did not select the old entry");

        policy.ContinueSweep.Set();
        await sweeping.WaitAsync(TimeSpan.FromSeconds(10));

        // The replacement spends the one permit. The older acquisition must not also spend the
        // detached entry it selected before the sweep retired it.
        Assert.True((await limiter.AcquireAsync("api", "target")).Allowed);
        continueLookup.Set();
        Assert.False((await selected.WaitAsync(TimeSpan.FromSeconds(10))).Allowed);
    }

    private sealed class ControlledPolicy : RateLimitPolicy, IDisposable
    {
        private int created;

        public ControlledPolicy() : base("api", 1) { }

        public ManualResetEventSlim SweepReached { get; } = new();

        public ManualResetEventSlim ContinueSweep { get; } = new();

        public override RateResult Evaluate(ref object? state, IOrionClock clock, int permits)
        {
            if (state is not Counter counter)
            {
                counter = new Counter(Interlocked.Increment(ref created) == 1);
                state = counter;
            }

            if (counter.Consumed)
            {
                return RateResult.Throttle(1, 0, TimeSpan.FromMinutes(1));
            }

            counter.Consumed = true;
            return RateResult.Allow(1, 0);
        }

        public override bool IsIdle(object? state, IOrionClock clock)
        {
            if (state is not Counter { IsTarget: true } target)
            {
                return false;
            }

            SweepReached.Set();
            Assert.True(ContinueSweep.Wait(TimeSpan.FromSeconds(10)), "the sweep was never released");
            target.Consumed = false; // The modeled window has elapsed; this entry is idle.
            return true;
        }

        public void Dispose()
        {
            SweepReached.Dispose();
            ContinueSweep.Dispose();
        }

        private sealed class Counter(bool isTarget)
        {
            public bool IsTarget { get; } = isTarget;

            public bool Consumed { get; set; }
        }
    }
}
