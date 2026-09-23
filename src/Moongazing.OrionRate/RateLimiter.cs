namespace Moongazing.OrionRate;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Moongazing.Orion.Abstractions.Time;
using Moongazing.OrionRate.Diagnostics;

/// <summary>
/// The in-memory rate limiter: one shared instance holds the per-key counter state for every policy.
/// Each key's state is guarded by its own lock, so acquisitions for different keys never contend and
/// acquisitions for the same key are serialized (the check-and-consume is atomic). All window / refill
/// math runs on the injected <see cref="IOrionClock"/>, so a fake clock fast-forwards limits in tests.
/// <para>The Wave 2 Redis store makes limits correct across replicas; this store is per-process.</para>
/// </summary>
public sealed class RateLimiter : IRateLimiter
{
    private readonly IReadOnlyDictionary<string, RateLimitPolicy> policies;
    private readonly IOrionClock clock;
    private readonly RateDiagnostics diagnostics;
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    // Keys come from API keys, tenants and client IPs, so the key space belongs to the caller and a
    // dictionary that only ever grows is a memory leak an attacker can drive. Idle partitions are
    // swept off the acquire path: no timer, so the limiter needs no lifetime of its own.
    // ponytail: one linear sweep per interval; if the partition count ever makes that too slow,
    // shard the dictionary and sweep one shard per pass.
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);
    private const int SweepFloor = 256;
    private long lastSweepTimestamp;
    private int sweeping;

    /// <summary>Create the limiter over a fixed set of <paramref name="policies"/>.</summary>
    /// <param name="policies">The registered policies, keyed by name.</param>
    /// <param name="clock">The clock all window / refill math runs on.</param>
    /// <param name="diagnostics">The instrumentation each decision is recorded to.</param>
    public RateLimiter(IReadOnlyDictionary<string, RateLimitPolicy> policies, IOrionClock clock, RateDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);
        this.policies = policies;
        this.clock = clock;
        this.diagnostics = diagnostics;
        lastSweepTimestamp = clock.GetTimestamp();
    }

    /// <summary>
    /// Create a limiter from configured <paramref name="options"/> without a DI container — the
    /// library path. Prefer <c>AddOrionRate</c> in a hosted app; use this for a console tool, a test,
    /// or any place you hold the clock yourself.
    /// </summary>
    /// <param name="options">The policies to serve (configured via <see cref="RateLimiterOptions.AddPolicy"/>).</param>
    /// <param name="clock">The clock all window / refill math runs on.</param>
    /// <param name="diagnostics">Instrumentation; defaults to the process-wide <see cref="RateDiagnostics.Shared"/>.</param>
    /// <returns>A ready limiter.</returns>
    public static RateLimiter Create(RateLimiterOptions options, IOrionClock clock, RateDiagnostics? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new RateLimiter(options.Build(), clock, diagnostics ?? RateDiagnostics.Shared);
    }

    /// <summary>The number of per-(policy, key) partitions currently held. For tests.</summary>
    internal int PartitionCount => entries.Count;

    /// <summary>Test seam for scheduling an acquisition between lookup and its entry lock.</summary>
    internal Action<string>? AfterEntryLookupForTests { get; set; }

    /// <inheritdoc />
    public ValueTask<RateResult> AcquireAsync(string policy, string key, int permits = 1, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(policy);
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (permits <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(permits), permits, "permits must be positive.");
        }
        if (!policies.TryGetValue(policy, out var p))
        {
            throw new KeyNotFoundException($"No rate-limit policy named '{policy}' is registered.");
        }

        // A NUL separator realistically appears in neither a policy name nor a key, so the
        // (policy, key) composite never collides across policies.
        var composite = string.Concat(policy, "\0", key);
        RateResult result;
        while (true)
        {
            var entry = entries.GetOrAdd(composite, static (_, policy) => new Entry(policy), p);
            AfterEntryLookupForTests?.Invoke(key);

            lock (entry.Gate)
            {
                // A sweep may have removed this entry after GetOrAdd but before we acquired its
                // gate. Spending its state would be lost when the next caller creates a fresh one.
                if (entry.Retired)
                {
                    continue;
                }

                result = p.Evaluate(ref entry.State, clock, permits);
                break;
            }
        }

        diagnostics.Record(policy, result);
        SweepIdlePartitions();
        return new ValueTask<RateResult>(result);
    }

    /// <summary>
    /// Drop partitions whose state has decayed back to what a brand-new key would start from - a
    /// refilled bucket, an empty window - so they are indistinguishable from never having existed.
    /// Runs at most once per <see cref="SweepInterval"/>, and only once the map is big enough to be
    /// worth walking.
    /// </summary>
    private void SweepIdlePartitions()
    {
        // ConcurrentDictionary.Count takes every one of the dictionary's locks, so it must not be on
        // the acquire path. The elapsed-time check is a single interlocked read and gates the rest.
        if (clock.GetElapsedTime(Interlocked.Read(ref lastSweepTimestamp)) < SweepInterval ||
            Interlocked.Exchange(ref sweeping, 1) == 1)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref lastSweepTimestamp, clock.GetTimestamp());
            if (entries.Count < SweepFloor)
            {
                return;
            }

            foreach (var pair in entries)
            {
                lock (pair.Value.Gate)
                {
                    if (pair.Value.Policy.IsIdle(pair.Value.State, clock))
                    {
                        // Readers that selected this entry before removal must retry against its
                        // replacement rather than spend permits on an orphaned state.
                        if (entries.TryRemove(pair))
                        {
                            pair.Value.Retired = true;
                        }
                    }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref sweeping, 0);
        }
    }

    // One lock + state slot per (policy,key). The policy owns the shape of State.
    private sealed class Entry(RateLimitPolicy policy)
    {
        public readonly object Gate = new();

        public readonly RateLimitPolicy Policy = policy;

        public object? State;

        // Protected by Gate. An acquisition that selected this entry before a sweep must retry.
        public bool Retired;
    }
}
