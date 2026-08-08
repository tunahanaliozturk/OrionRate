namespace Moongazing.OrionRate;

using System;
using System.Collections.Generic;

/// <summary>
/// Configures the named rate-limit policies the limiter serves. Define policies once at startup with
/// <see cref="AddPolicy"/>; callers then reference them by name in
/// <see cref="IRateLimiter.AcquireAsync"/>.
/// </summary>
public sealed class RateLimiterOptions
{
    private readonly Dictionary<string, RateLimitPolicy> policies = new(StringComparer.Ordinal);

    /// <summary>Add a named policy, configuring its algorithm through <paramref name="configure"/>.</summary>
    /// <param name="name">The policy name (must be unique).</param>
    /// <param name="configure">Chooses the algorithm, e.g. <c>p =&gt; p.TokenBucket(100, TimeSpan.FromMinutes(1))</c>.</param>
    /// <returns>This options instance, for chaining.</returns>
    public RateLimiterOptions AddPolicy(string name, Action<RatePolicyBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new RatePolicyBuilder(name);
        configure(builder);
        var policy = builder.Build();

        if (!policies.TryAdd(name, policy))
        {
            throw new ArgumentException($"A rate-limit policy named '{name}' is already registered.", nameof(name));
        }
        return this;
    }

    /// <summary>Snapshot the configured policies for the limiter.</summary>
    internal IReadOnlyDictionary<string, RateLimitPolicy> Build() =>
        new Dictionary<string, RateLimitPolicy>(policies, StringComparer.Ordinal);
}

/// <summary>
/// Builds a single <see cref="RateLimitPolicy"/> by choosing its algorithm. Exactly one algorithm
/// must be selected. (Key resolution — <c>KeyBy</c> — arrives with the Wave 3 ASP.NET middleware;
/// in Wave 1 keys are supplied directly to <see cref="IRateLimiter.AcquireAsync"/>.)
/// </summary>
public sealed class RatePolicyBuilder
{
    private readonly string name;
    private RateLimitPolicy? policy;

    internal RatePolicyBuilder(string name) => this.name = name;

    /// <summary>Use a token-bucket algorithm (sustained <paramref name="permit"/> per <paramref name="per"/>, optional <paramref name="burst"/>).</summary>
    /// <param name="permit">Sustained permits per period.</param>
    /// <param name="per">The refill period.</param>
    /// <param name="burst">Extra headroom above the sustained rate for spikes. Defaults to 0.</param>
    /// <returns>This builder, for chaining.</returns>
    public RatePolicyBuilder TokenBucket(long permit, TimeSpan per, long burst = 0)
    {
        policy = new TokenBucketPolicy(name, permit, per, burst);
        return this;
    }

    /// <summary>Use a sliding-window algorithm (at most <paramref name="permit"/> per trailing <paramref name="window"/>).</summary>
    /// <param name="permit">Maximum admitted requests per window.</param>
    /// <param name="window">The trailing window length.</param>
    /// <returns>This builder, for chaining.</returns>
    public RatePolicyBuilder SlidingWindow(long permit, TimeSpan window)
    {
        policy = new SlidingWindowPolicy(name, permit, window);
        return this;
    }

    internal RateLimitPolicy Build() =>
        policy ?? throw new InvalidOperationException(
            $"Rate-limit policy '{name}' must choose an algorithm (call TokenBucket or SlidingWindow).");
}
