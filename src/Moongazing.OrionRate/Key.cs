namespace Moongazing.OrionRate;

using System;

/// <summary>
/// A helper for building consistent, low-collision rate-limit key strings from well-known dimensions
/// (API key, tenant, IP, route) or a custom name. Keeps every call site formatting keys the same way
/// — <c>tenant:acme</c>, <c>ip:203.0.113.4</c> — and composing them the same way.
/// <para>
/// Wave 1 is the library path: you resolve the dimension values and pass the built key to
/// <see cref="IRateLimiter.AcquireAsync"/>. Request-driven resolution (a <c>KeyBy</c> that reads the
/// tenant/API key/route off an <c>HttpContext</c>) arrives with the Wave 3 ASP.NET middleware.
/// </para>
/// </summary>
public readonly struct Key : IEquatable<Key>
{
    // ':' separates a dimension from its value and '|' separates segments of a composite key. Letting
    // either through means two different identities can spell the same key string and share a budget,
    // so they are rejected wherever a caller supplies the text.
    private static readonly char[] NameSeparators = [':', '|'];
    private static readonly char[] SegmentSeparator = ['|'];

    private Key(string name) => Name = name;

    /// <summary>The dimension prefix, e.g. <c>tenant</c>.</summary>
    public string Name { get; }

    /// <summary>The API-key dimension (pairs with an <c>OrionLedger</c>-issued key).</summary>
    public static Key ApiKey => new("apikey");

    /// <summary>The tenant dimension.</summary>
    public static Key Tenant => new("tenant");

    /// <summary>The client-IP dimension.</summary>
    public static Key Ip => new("ip");

    /// <summary>The route/endpoint dimension.</summary>
    public static Key Route => new("route");

    /// <summary>A custom dimension with the given <paramref name="name"/>.</summary>
    /// <param name="name">The dimension prefix. Must not contain <c>:</c> or <c>|</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> contains a key separator.</exception>
    public static Key Custom(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.IndexOfAny(NameSeparators) >= 0)
        {
            throw new ArgumentException(
                $"A dimension name cannot contain ':' or '|'; '{name}' would collide with another key.",
                nameof(name));
        }
        return new Key(name);
    }

    /// <summary>Build a key segment for this dimension and <paramref name="value"/>, e.g. <c>tenant:acme</c>.</summary>
    /// <param name="value">The dimension value. Must not contain <c>|</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> contains the segment separator.</exception>
    public string Of(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.IndexOfAny(SegmentSeparator) >= 0)
        {
            // Otherwise Key.Tenant.Of("acme|route:/v1/charges") is byte-for-byte the composite key for
            // tenant acme on that route, and a caller-supplied tenant id picks its own partition.
            throw new ArgumentException(
                $"A key value cannot contain '|'; '{value}' would forge a composite key.",
                nameof(value));
        }
        return string.Concat(Name, ":", value);
    }

    /// <summary>Combine several segments (from <see cref="Of"/>) into one composite key, e.g. <c>tenant:acme|route:/v1/charges</c>.</summary>
    /// <param name="segments">The segments to join.</param>
    /// <exception cref="ArgumentException">A segment contains the segment separator.</exception>
    public static string Combine(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        foreach (var segment in segments)
        {
            ArgumentNullException.ThrowIfNull(segment);
            if (segment.IndexOfAny(SegmentSeparator) >= 0)
            {
                throw new ArgumentException(
                    $"A key segment cannot contain '|'; '{segment}' would forge a composite key.",
                    nameof(segments));
            }
        }
        return string.Join("|", segments);
    }

    /// <inheritdoc />
    public bool Equals(Key other) => string.Equals(Name, other.Name, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Key other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Name is null ? 0 : StringComparer.Ordinal.GetHashCode(Name);

    /// <summary>Value equality by dimension name.</summary>
    public static bool operator ==(Key left, Key right) => left.Equals(right);

    /// <summary>Value inequality by dimension name.</summary>
    public static bool operator !=(Key left, Key right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => Name ?? string.Empty;
}
