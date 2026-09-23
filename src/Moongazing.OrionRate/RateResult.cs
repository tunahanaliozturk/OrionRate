namespace Moongazing.OrionRate;

using System;

/// <summary>
/// The outcome of a rate-limit decision: whether the request was admitted, how much of the limit
/// remains, and — when throttled — how long to wait before retrying. Maps directly onto an HTTP
/// <c>429</c> with <c>Retry-After</c> and <c>RateLimit-*</c> headers via the optional
/// <c>OrionRate.AspNetCore</c> package.
/// </summary>
public readonly struct RateResult : IEquatable<RateResult>
{
    /// <summary>Create a result.</summary>
    /// <param name="allowed">Whether the request was admitted.</param>
    /// <param name="limit">The policy's nominal permit limit.</param>
    /// <param name="remaining">Permits still available after this decision (never negative).</param>
    /// <param name="retryAfter">When throttled, how long until enough permits are available; otherwise <see cref="TimeSpan.Zero"/>.</param>
    public RateResult(bool allowed, long limit, long remaining, TimeSpan retryAfter)
    {
        Allowed = allowed;
        Limit = limit;
        Remaining = remaining < 0 ? 0 : remaining;
        RetryAfter = retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter;
    }

    /// <summary>Whether the request was admitted.</summary>
    public bool Allowed { get; }

    /// <summary>The policy's nominal permit limit (for the <c>RateLimit-Limit</c> header).</summary>
    public long Limit { get; }

    /// <summary>Permits still available after this decision. Never negative.</summary>
    public long Remaining { get; }

    /// <summary>When throttled, how long until enough permits are available; <see cref="TimeSpan.Zero"/> when allowed.</summary>
    public TimeSpan RetryAfter { get; }

    /// <summary>An admitted result.</summary>
    public static RateResult Allow(long limit, long remaining) => new(true, limit, remaining, TimeSpan.Zero);

    /// <summary>A throttled result.</summary>
    public static RateResult Throttle(long limit, long remaining, TimeSpan retryAfter) => new(false, limit, remaining, retryAfter);

    /// <inheritdoc />
    public bool Equals(RateResult other) =>
        Allowed == other.Allowed && Limit == other.Limit && Remaining == other.Remaining && RetryAfter == other.RetryAfter;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RateResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Allowed, Limit, Remaining, RetryAfter);

    /// <summary>Value equality.</summary>
    public static bool operator ==(RateResult left, RateResult right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(RateResult left, RateResult right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        Allowed
            ? $"Allowed (limit {Limit}, remaining {Remaining})"
            : $"Throttled (limit {Limit}, retry after {RetryAfter.TotalMilliseconds:0}ms)";
}
