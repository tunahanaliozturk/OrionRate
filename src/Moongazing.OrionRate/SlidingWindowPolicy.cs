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

        // Drop timestamps that have aged out of the trailing window (they no longer count).
        while (s.Timestamps.Count > 0 && clock.GetElapsedTime(s.Timestamps.Peek()) >= window)
        {
            s.Timestamps.Dequeue();
        }

        var count = s.Timestamps.Count;
        if (count + permits <= permit)
        {
            var now = clock.GetTimestamp();
            for (var i = 0; i < permits; i++)
            {
                s.Timestamps.Enqueue(now);
            }
            return RateResult.Allow(permit, permit - (count + permits));
        }

        // Full window: the oldest in-window request frees a slot when it ages out.
        var retryAfter = s.Timestamps.Count > 0
            ? window - clock.GetElapsedTime(s.Timestamps.Peek())
            : window;
        return RateResult.Throttle(permit, Math.Max(0, permit - count), retryAfter);
    }

    private sealed class SlidingWindowState
    {
        public Queue<long> Timestamps { get; } = new();
    }
}
