namespace Moongazing.OrionRate;

using System;
using System.Collections.Generic;

using Moongazing.Orion.Abstractions.Time;

/// <summary>
/// A sliding-window policy: at most <c>permit</c> requests are admitted in any trailing
/// <c>window</c>, measured precisely on the injected clock (a request timestamp log, not a fixed
/// calendar window, so there is no burst doubling at a window boundary). When the window is full,
/// <see cref="RateResult.RetryAfter"/> reports when the oldest request will age out and free a slot.
/// </summary>
public sealed class SlidingWindowPolicy : RateLimitPolicy
{
    private readonly long permit;
    private readonly TimeSpan window;

    /// <summary>Create a sliding-window policy.</summary>
    /// <param name="name">The policy name.</param>
    /// <param name="permit">The maximum admitted requests per trailing window. Must be positive.</param>
    /// <param name="window">The trailing window length. Must be positive.</param>
    public SlidingWindowPolicy(string name, long permit, TimeSpan window)
        : base(name, permit)
    {
        if (permit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(permit), permit, "permit must be positive.");
        }
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), window, "window must be positive.");
        }
        this.permit = permit;
        this.window = window;
    }

    /// <inheritdoc />
    public override RateResult Evaluate(ref object? state, IOrionClock clock, int permits)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (permits <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(permits), permits, "permits must be positive.");
        }
        if (permits > permit)
        {
            // A cost above the window limit can never be admitted, however long the caller waits.
            // Throttling it would hand back a RetryAfter that is a lie the caller can retry against
            // forever.
            throw new ArgumentOutOfRangeException(
                nameof(permits),
                permits,
                $"permits exceeds the window limit ({permit}); the request could never be admitted.");
        }

        if (state is not SlidingWindowState s)
        {
            s = new SlidingWindowState();
            state = s;
        }

        DropAgedOut(s, clock);

        var count = s.UsedPermits;
        if (permits <= permit - count)
        {
            var now = clock.GetTimestamp();
            s.Timestamps.Enqueue(new TimestampBatch(now, permits));
            s.UsedPermits += permits;
            return RateResult.Allow(permit, permit - s.UsedPermits);
        }

        // Full window. The request needs `needed` slots to come free, so it can only succeed once the
        // needed-th oldest timestamp ages out - reporting the oldest one only frees a single slot and
        // sends a multi-permit caller back into a second rejection. `permits <= permit` is enforced
        // above, so `needed` is always between 1 and `count`.
        var needed = permits - (permit - count);
        var retryAfter = window - clock.GetElapsedTime(NthOldest(s.Timestamps, needed));
        return RateResult.Throttle(permit, permit - count, retryAfter);
    }

    /// <inheritdoc />
    public override bool IsIdle(object? state, IOrionClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (state is not SlidingWindowState s)
        {
            return true;
        }

        // An empty log behaves exactly like a key that was never seen.
        DropAgedOut(s, clock);
        return s.UsedPermits == 0;
    }

    // Drop timestamps that have aged out of the trailing window (they no longer count).
    private void DropAgedOut(SlidingWindowState s, IOrionClock clock)
    {
        while (s.Timestamps.Count > 0 && clock.GetElapsedTime(s.Timestamps.Peek().Timestamp) >= window)
        {
            s.UsedPermits -= s.Timestamps.Dequeue().Permits;
        }
    }

    private static long NthOldest(Queue<TimestampBatch> timestamps, long n)
    {
        long seen = 0;
        foreach (var batch in timestamps)
        {
            seen += batch.Permits;
            if (seen >= n)
            {
                return batch.Timestamp;
            }
        }

        throw new System.Diagnostics.UnreachableException($"the window holds {seen} permits but slot {n} was asked for.");
    }

    private sealed class SlidingWindowState
    {
        public Queue<TimestampBatch> Timestamps { get; } = new();

        public long UsedPermits { get; set; }
    }

    private readonly record struct TimestampBatch(long Timestamp, int Permits);
}
