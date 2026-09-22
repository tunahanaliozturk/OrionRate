namespace Moongazing.OrionRate;

using System;

using Moongazing.Orion.Abstractions.Time;

/// <summary>
/// A token-bucket policy: the bucket holds up to <c>permit + burst</c> tokens and refills at
/// <c>permit / per</c> tokens per second, all measured on the injected clock. A request consumes
/// tokens; when the bucket is dry the request is throttled and <see cref="RateResult.RetryAfter"/>
/// reports exactly how long until enough tokens have accrued. Allows short bursts up to the bucket's
/// capacity while holding the long-run rate to <c>permit per period</c>.
/// </summary>
public sealed class TokenBucketPolicy : RateLimitPolicy
{
    private readonly double capacity;
    private readonly double refillPerSecond;

    /// <summary>Create a token-bucket policy.</summary>
    /// <param name="name">The policy name.</param>
    /// <param name="permit">The sustained number of permits per <paramref name="per"/> period. Must be positive.</param>
    /// <param name="per">The refill period the <paramref name="permit"/> count applies over. Must be positive.</param>
    /// <param name="burst">Extra bucket headroom above <paramref name="permit"/> for momentary spikes. Non-negative; defaults to 0.</param>
    public TokenBucketPolicy(string name, long permit, TimeSpan per, long burst = 0)
        : base(name, permit)
    {
        if (permit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(permit), permit, "permit must be positive.");
        }
        if (per <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(per), per, "per must be positive.");
        }
        if (burst < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(burst), burst, "burst cannot be negative.");
        }
        capacity = permit + burst;
        refillPerSecond = permit / per.TotalSeconds;
    }

    /// <inheritdoc />
    public override RateResult Evaluate(ref object? state, IOrionClock clock, int permits)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (permits <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(permits), permits, "permits must be positive.");
        }
        if (permits > capacity)
        {
            // A cost above capacity can never be admitted, however long the caller waits. Throttling
            // it would hand back a RetryAfter that is a lie the caller can retry against forever.
            throw new ArgumentOutOfRangeException(
                nameof(permits),
                permits,
                $"permits exceeds the bucket capacity ({capacity}); the request could never be admitted.");
        }

        if (state is not TokenBucketState s)
        {
            // First request for this key: a full bucket, clock started now.
            s = new TokenBucketState { Tokens = capacity, LastTimestamp = clock.GetTimestamp() };
            state = s;
        }

        // The token count is a pure function of the anchor - the tokens held at LastTimestamp - and
        // the time elapsed since it, so recompute it from the anchor instead of crediting the bucket
        // on every call. Re-anchoring on a call that consumed nothing rounded the credit away (a rate
        // below one permit per poll then accrued nothing at all, forever), and re-anchoring onto a
        // clock that had stepped backwards turned the correction back to real time into a full refill.
        var elapsed = clock.GetElapsedTime(s.LastTimestamp);
        var available = elapsed > TimeSpan.Zero
            ? Math.Min(capacity, s.Tokens + (elapsed.TotalSeconds * refillPerSecond))
            : s.Tokens;

        if (available >= permits)
        {
            s.Tokens = available - permits;
            if (elapsed > TimeSpan.Zero)
            {
                s.LastTimestamp = clock.GetTimestamp(); // the anchor only ever moves forward
            }
            return RateResult.Allow(PermitLimit, (long)s.Tokens);
        }

        // Not enough tokens: time to accrue the shortfall at the refill rate, rounded UP to the next
        // tick. Rounding down (what TimeSpan.FromSeconds does) lands the caller a tick before the
        // token exists, so honouring the advertised RetryAfter earns a second rejection - and once the
        // shortfall is under half a tick the advertised wait truncates to zero, which is a busy-spin.
        var deficit = permits - available;
        return RateResult.Throttle(PermitLimit, (long)available, CeilingSeconds(deficit / refillPerSecond));
    }

    // TimeSpan.FromSeconds truncates toward zero; a retry-after must never land early.
    private static TimeSpan CeilingSeconds(double seconds)
    {
        var ticks = Math.Ceiling(seconds * TimeSpan.TicksPerSecond);
        return ticks >= long.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)ticks);
    }

    private sealed class TokenBucketState
    {
        public double Tokens { get; set; }

        public long LastTimestamp { get; set; }
    }
}
