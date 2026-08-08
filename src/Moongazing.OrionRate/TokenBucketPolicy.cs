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

        if (state is not TokenBucketState s)
        {
            // First request for this key: a full bucket, clock started now.
            s = new TokenBucketState { Tokens = capacity, LastTimestamp = clock.GetTimestamp() };
            state = s;
        }
        else
        {
            var elapsed = clock.GetElapsedTime(s.LastTimestamp);
            s.LastTimestamp = clock.GetTimestamp();
            if (elapsed > TimeSpan.Zero)
            {
                s.Tokens = Math.Min(capacity, s.Tokens + (elapsed.TotalSeconds * refillPerSecond));
            }
        }

        if (s.Tokens >= permits)
        {
            s.Tokens -= permits;
            return RateResult.Allow(PermitLimit, (long)s.Tokens);
        }

        // Not enough tokens: time to accrue the shortfall at the refill rate.
        var deficit = permits - s.Tokens;
        var retryAfter = TimeSpan.FromSeconds(deficit / refillPerSecond);
        return RateResult.Throttle(PermitLimit, (long)s.Tokens, retryAfter);
    }

    private sealed class TokenBucketState
    {
        public double Tokens { get; set; }

        public long LastTimestamp { get; set; }
    }
}
