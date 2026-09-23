<p align="center">
  <img src="docs/logo.png" alt="OrionRate" width="150" />
</p>

# OrionRate

[![CI/CD](https://github.com/tunahanaliozturk/OrionRate/actions/workflows/ci-cd.yml/badge.svg)](https://github.com/tunahanaliozturk/OrionRate/actions/workflows/ci-cd.yml)
[![NuGet](https://img.shields.io/nuget/v/OrionRate.svg)](https://www.nuget.org/packages/OrionRate/)

Rate limiting the **Orion** family way: token-bucket and sliding-window algorithms whose refill and window math run on an `OrionClock` `TimeProvider`, so a fake clock fast-forwards every limit in tests. `AcquireAsync` returns a typed `RateResult` — allowed, remaining, retry-after — with OpenTelemetry by default.

`System.Threading.RateLimiting` (the BCL primitive) is excellent and OrionRate builds on the same bucket math for the in-process case. But it limits *per process*: three replicas behind a load balancer turn a "100/min" limit into 300. Its partition-key model is low-level, so every app re-writes "resolve the key from the API key / tenant / route." And there is no notion of a *quota tied to an identity you already issued*. OrionRate is the opinionated layer that closes those gaps — starting, in this release, with a testable, observable in-memory core.

## Features

- **Token bucket & sliding window** — `TokenBucket(permit, per, burst)` for sustained-rate-with-spikes, `SlidingWindow(permit, window)` for a precise trailing-window log. Both compute a correct `Retry-After`.
- **Clock-driven, so deterministic in tests** — every refill and window runs on `OrionClock`. Under `FakeOrionClock`, draining a bucket and watching it refill takes no real time and never flakes.
- **A typed decision** — `AcquireAsync` returns a `RateResult` (`Allowed`, `Limit`, `Remaining`, `RetryAfter`); a rejection is data, not an exception. Maps cleanly onto a `429` + `RateLimit-*` headers (the web mapping ships in a later wave). Asking for more permits than the policy could ever hold is a caller bug, not a limit being hit, and throws `ArgumentOutOfRangeException` — no wait would ever satisfy it.
- **Thread-safe** — check-and-consume is atomic per key, so concurrent requests never over-admit.
- **Idle-state reclamation** — partitions whose state has decayed back to a brand-new key's (a refilled bucket, an empty window) are swept away. Active partitions remain resident; this is not a hard memory bound.
- **Consistent keys** — a small `Key` helper (`Key.Tenant.Of("acme")`, `Key.Combine(...)`) so every call site formats and composes keys the same way.
- **OpenTelemetry by default** — a `Moongazing.OrionRate` meter carrying `orion.rate.allowed`, `orion.rate.throttled`, and `orion.rate.remaining`, tagged by policy, on the family's `OrionInstrumentation` spine.
- **AOT- and trim-clean**, verified by a native-binary smoke test in CI. Multi-targets `net8.0`, `net9.0`, `net10.0`.

## Install

```bash
dotnet add package OrionRate
```

## Quick start (DI)

```csharp
using Moongazing.OrionRate;
using Moongazing.OrionRate.DependencyInjection;

services.AddOrionRate(o =>
{
    o.AddPolicy("api",   p => p.TokenBucket(permit: 100, per: TimeSpan.FromMinutes(1), burst: 20));
    o.AddPolicy("login", p => p.SlidingWindow(permit: 5, window: TimeSpan.FromMinutes(15)));
});

// ... resolve and use:
var limiter = provider.GetRequiredService<IRateLimiter>();

RateResult r = await limiter.AcquireAsync("api", Key.Tenant.Of("acme"));
if (!r.Allowed)
{
    // 429; tell the caller when to come back.
    return Results.StatusCode(429); // Retry-After: r.RetryAfter
}
```

## Quick start (no DI)

```csharp
var options = new RateLimiterOptions()
    .AddPolicy("api", p => p.TokenBucket(100, TimeSpan.FromMinutes(1)));

var limiter = RateLimiter.Create(options, new OrionClock());
RateResult r = await limiter.AcquireAsync("api", Key.Ip.Of("203.0.113.4"));
```

## Testing — limits fast-forward, no real waits

Because refill and window math run on `OrionClock`, a `FakeOrionClock` advances a whole limit window instantly and deterministically:

```csharp
var clock = new FakeOrionClock();
var limiter = RateLimiter.Create(
    new RateLimiterOptions().AddPolicy("api", p => p.TokenBucket(100, TimeSpan.FromMinutes(1))),
    clock);

for (var i = 0; i < 100; i++)
    Assert.True((await limiter.AcquireAsync("api", "tenant:acme")).Allowed);

var throttled = await limiter.AcquireAsync("api", "tenant:acme");
Assert.False(throttled.Allowed);
Assert.Equal(0.6, throttled.RetryAfter.TotalSeconds, precision: 2); // one token at 100/60 per second

clock.Advance(TimeSpan.FromSeconds(60));                            // no real waiting
Assert.True((await limiter.AcquireAsync("api", "tenant:acme")).Allowed);
```

## Key cardinality and deployment boundary

The in-memory limiter applies limits **per process**. With three independent replicas, a nominal
100-per-minute policy can admit up to 300 requests across them. It is not a distributed quota until
the planned shared store is available.

An idle sweep reclaims a partition only after its bucket refills or its window empties. A caller
that can keep generating distinct active keys can still grow the state map until those states age
out. Build keys from trusted, normalized identities (for example a validated tenant or API-key ID),
not an arbitrary request header. For endpoints exposed to high-cardinality identities such as client
IP addresses, enforce a separate admission/cardinality limit at the edge and choose windows whose
state lifetime fits the host's memory budget. The `Key` helper prevents delimiter collisions; it
does not authenticate or cap the identities it receives.

## Observability

A `Moongazing.OrionRate` meter records `orion.rate.allowed`, `orion.rate.throttled`, and `orion.rate.remaining` (a histogram of permits left at each decision), each tagged with the low-cardinality policy name. Keys are deliberately not tagged (unbounded cardinality). Multi-tenant / multi-region labels configured via `SetStaticTags` stamp every measurement.

## Roadmap

This is the **Wave 1** foundation: in-memory token-bucket / sliding-window on `OrionClock`, AOT-clean. Later waves add a Redis distributed store (atomic Lua, correct across replicas), an ASP.NET Core middleware (`RequireRateLimit("policy")` with RFC 9457 `429` + `RateLimit-*` headers and request-driven `KeyBy` resolvers), and `OrionLedger`-sourced per-API-key quotas. See [CHANGELOG.md](CHANGELOG.md).

OrionRate is app-level fairness/quota, not an API gateway or WAF; it *reads* quotas (from `OrionLedger`, in a later wave) rather than billing usage; and it is the server-side mirror of client-side backoff (which lives in [OrionResilience](https://github.com/tunahanaliozturk/OrionResilience)).

## Versioning

Follows [Semantic Versioning](https://semver.org/). Multi-targets `net8.0`, `net9.0`, and `net10.0`. Binds to `Orion.Abstractions` 1.x and `OrionClock` 0.9.x.

## Documentation

- [CHANGELOG.md](CHANGELOG.md) — release notes.

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) and the [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).

## More from the Orion family

Focused .NET libraries built to one quality bar. Each is usable on its own; several share the small [`Orion.Abstractions`](https://github.com/tunahanaliozturk/Orion.Abstractions) contracts spine, but there is no deep dependency web — pick only what you need:

- [Orion.Abstractions](https://github.com/tunahanaliozturk/Orion.Abstractions) — the shared contracts spine: telemetry, options, result, clock
- [OrionClock](https://github.com/tunahanaliozturk/OrionClock) — a `TimeProvider`-based clock with TTL / deadline vocabulary
- [OrionResilience](https://github.com/tunahanaliozturk/OrionResilience) — retry, backoff, and timeout on OrionClock (client-side mirror of this)
- [OrionLedger](https://github.com/tunahanaliozturk/OrionLedger) — API-key issuance, verification, and rotation (per-key quotas, a later wave)
- [OrionGuard](https://github.com/tunahanaliozturk/OrionGuard) — validation, guard clauses, DDD primitives, domain events
- [OrionResult](https://github.com/tunahanaliozturk/OrionResult) — Result/Option types and a shared error vocabulary
- [OrionAudit](https://github.com/tunahanaliozturk/OrionAudit) — automatic EF Core change-audit trail
- [OrionBeacon](https://github.com/tunahanaliozturk/OrionBeacon) — leader election with fencing tokens
- [OrionGrant](https://github.com/tunahanaliozturk/OrionGrant) — permission / authorization checks
- [OrionInbox](https://github.com/tunahanaliozturk/OrionInbox) — transactional inbox for exactly-once effects
- [OrionKey](https://github.com/tunahanaliozturk/OrionKey) — source-generated strongly-typed IDs
- [OrionLens](https://github.com/tunahanaliozturk/OrionLens) — ambient correlation-context propagation
- [OrionLock](https://github.com/tunahanaliozturk/OrionLock) — distributed locks with fencing tokens
- [OrionOnce](https://github.com/tunahanaliozturk/OrionOnce) — idempotency keys for exactly-once request handling
- [OrionPatch](https://github.com/tunahanaliozturk/OrionPatch) — transactional outbox for EF Core
- [OrionRelay](https://github.com/tunahanaliozturk/OrionRelay) — outbound webhook delivery (HMAC, retries, backoff)
- [OrionSaga](https://github.com/tunahanaliozturk/OrionSaga) — sagas / process managers for long-running workflows
- [OrionShade](https://github.com/tunahanaliozturk/OrionShade) — sensitive-data redaction for logs and telemetry
- [OrionStream](https://github.com/tunahanaliozturk/OrionStream) — server-sent events / streaming hub
- [OrionVault](https://github.com/tunahanaliozturk/OrionVault) — field-level encryption for EF Core

See it all working together in [OrionShowcase](https://github.com/tunahanaliozturk/OrionShowcase), a production-shaped banking sample.

## License

[MIT](LICENSE).
