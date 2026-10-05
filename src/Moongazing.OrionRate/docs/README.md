# OrionRate

In-memory token-bucket and sliding-window rate limiting for .NET. `AcquireAsync` returns a typed `RateResult` (allowed, remaining, retry-after) instead of throwing, and every refill and window calculation runs on OrionClock, so tests fast-forward limits with a fake clock and no real waiting.

![AcquireAsync decision: the call is validated and its (policy, key) partition locked; a token bucket admits when enough tokens have refilled, a sliding window when the trailing window has room; a throttle carries RetryAfter, and an impossible request throws](https://raw.githubusercontent.com/tunahanaliozturk/OrionRate/master/docs/diagrams/acquire-decision.png)

## Install

    dotnet add package OrionRate

Targets `net8.0`, `net9.0` and `net10.0`. Depends on `Orion.Abstractions` and `OrionClock`. For Minimal API endpoints and route groups, add `OrionRate.AspNetCore`.

## Quick start

```csharp
using Moongazing.OrionRate;
using Moongazing.OrionRate.DependencyInjection;

services.AddOrionRate(o =>
{
    o.AddPolicy("api",   p => p.TokenBucket(permit: 100, per: TimeSpan.FromMinutes(1), burst: 20));
    o.AddPolicy("login", p => p.SlidingWindow(permit: 5, window: TimeSpan.FromMinutes(15)));
});

public sealed class LoginService(IRateLimiter limiter)
{
    public async Task<bool> TryLoginAsync(string tenantId, CancellationToken ct)
    {
        RateResult result = await limiter.AcquireAsync("login", Key.Tenant.Of(tenantId), cancellationToken: ct);
        if (!result.Allowed)
        {
            return false; // throttled: retry after result.RetryAfter
        }

        // ... the rate-limited work
        return true;
    }
}
```

Without a container: `RateLimiter.Create(options, clock)` with a `RateLimiterOptions` you configured through `AddPolicy`.

## Policies

Each named policy picks exactly one algorithm; picking a second throws `InvalidOperationException` at startup, and a duplicate policy name throws `ArgumentException`.

| Builder call | Behaviour | Capacity |
|--------------|-----------|----------|
| `TokenBucket(permit, per, burst = 0)` | Refills `permit` tokens per `per`. A new key starts with a full bucket, so short bursts up to the capacity pass. | `permit + burst` |
| `SlidingWindow(permit, window)` | At most `permit` permits in any trailing `window`, kept as a timestamp log, so there is no burst doubling at a window boundary. | `permit` |

## Behaviour

- A throttle is data, not an exception: `Allowed` is `false` and `RetryAfter` says when enough permits will be available; it never lands early. `Remaining` is never negative and `RetryAfter` is zero when allowed.
- Exceptions are for caller bugs only: an unknown policy throws `KeyNotFoundException`; `permits` that is not positive or above the policy's capacity throws `ArgumentOutOfRangeException`; a null or empty policy or key throws `ArgumentException`.
- Check-and-consume is atomic per (policy, key), so concurrent requests never over-admit; different keys never contend.
- Budgets are per process: three replicas admit up to three times the limit. There is no shared store yet.
- Partitions that have decayed back to a new key's state (a refilled bucket, an empty window) are swept at most once a minute, once 256 or more are held. Active keys stay in memory, so build keys from trusted, normalized identities, not an arbitrary request header.
- `Key` builds consistent key strings: `Key.Tenant.Of("acme")`, `Key.Combine(Key.Tenant.Of("acme"), Key.Route.Of("/v1/charges"))`. A value containing `|` is rejected so it cannot forge a composite key.

## Testing

```csharp
using Moongazing.OrionClock.Testing;
using Moongazing.OrionRate;

var clock = new FakeOrionClock();
var limiter = RateLimiter.Create(
    new RateLimiterOptions().AddPolicy("api", p => p.TokenBucket(100, TimeSpan.FromMinutes(1))),
    clock);

// ... drain the bucket, then:
clock.Advance(TimeSpan.FromSeconds(60)); // refilled, no real waiting
```

## Telemetry and AOT

- Meter `Moongazing.OrionRate` with counters `orion.rate.allowed` and `orion.rate.throttled` and the histogram `orion.rate.remaining`, each tagged with `orion.rate.policy`. Keys are never tagged.
- AOT- and trim-compatible (`IsAotCompatible`); CI publishes a NativeAOT smoke test.

## Related packages

- `OrionRate.AspNetCore` - `RequireOrionRateLimit` for Minimal API endpoints and route groups: `429` with `Retry-After`.
- `OrionClock` - the clock all limits are measured on; `OrionClock.Testing` has `FakeOrionClock` for tests.
- `Orion.Abstractions` - the shared contracts spine (`IOrionClock`, telemetry).

## Links

- Documentation and full README: https://github.com/tunahanaliozturk/OrionRate
- Changelog: https://github.com/tunahanaliozturk/OrionRate/blob/master/CHANGELOG.md
- License: MIT
