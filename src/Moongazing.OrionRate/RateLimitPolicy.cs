namespace Moongazing.OrionRate;

using Moongazing.Orion.Abstractions.Time;

/// <summary>
/// A named rate-limit policy: the algorithm and its parameters. A policy is immutable and shared; the
/// mutable per-key counter state lives outside it (in the limiter's store) and is threaded through
/// <see cref="Evaluate"/>, so one policy instance serves every key.
/// </summary>
public abstract class RateLimitPolicy
{
    /// <summary>Base constructor.</summary>
    /// <param name="name">The policy name callers reference in <see cref="IRateLimiter.AcquireAsync"/>.</param>
    /// <param name="permitLimit">The nominal permit limit surfaced on <see cref="RateResult.Limit"/>.</param>
    protected RateLimitPolicy(string name, long permitLimit)
    {
        System.ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        PermitLimit = permitLimit;
    }

    /// <summary>The policy name.</summary>
    public string Name { get; }

    /// <summary>The nominal permit limit (for the <c>RateLimit-Limit</c> header).</summary>
    public long PermitLimit { get; }

    /// <summary>
    /// Evaluate an acquisition against the per-key <paramref name="state"/>, mutating it in place.
    /// Called under the limiter's per-key lock, so implementations need not be internally synchronized.
    /// The <paramref name="state"/> starts null for a key's first request; the policy initializes it.
    /// </summary>
    /// <param name="state">The per-key counter state; null on first use, then owned by the policy.</param>
    /// <param name="clock">The clock all window / refill math runs on.</param>
    /// <param name="permits">The number of permits requested (usually 1).</param>
    /// <returns>The decision.</returns>
    public abstract RateResult Evaluate(ref object? state, IOrionClock clock, int permits);

    /// <summary>
    /// Whether <paramref name="state"/> has decayed back to the value a brand-new key would start
    /// from, so dropping the partition is indistinguishable from keeping it. The limiter uses this to
    /// evict idle partitions instead of holding one entry per key forever. Called under the key's
    /// lock, and free to prune expired entries out of <paramref name="state"/> on the way.
    /// <para>Defaults to <see langword="false"/>: a custom policy is never evicted until it opts in.</para>
    /// </summary>
    /// <param name="state">The per-key counter state.</param>
    /// <param name="clock">The clock all window / refill math runs on.</param>
    public virtual bool IsIdle(object? state, IOrionClock clock) => false;
}
