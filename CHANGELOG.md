<!-- markdownlint-disable MD024 -->

# Changelog

All notable changes to OrionRate are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.5.0] - 2026-07-29

The first release — the Orion family's Wave 1 rate-limiting foundation: token-bucket and
sliding-window algorithms on the shared clock, so limits fast-forward in tests.

### Added

- **`IRateLimiter` / `RateLimiter`** — the in-memory limiter. `AcquireAsync(policy, key, permits)`
  returns a typed `RateResult` (a rejection is data, not an exception). Check-and-consume is atomic
  per key, so concurrent requests never over-admit. `RateLimiter.Create(...)` builds one without a DI
  container for library use.
- **`TokenBucketPolicy`** — capacity `permit + burst`, refill `permit / per` per second, all on the
  clock; `RetryAfter` reports the exact time to accrue the shortfall.
- **`SlidingWindowPolicy`** — a precise trailing-window request log (no burst doubling at a window
  boundary); `RetryAfter` reports when the oldest request ages out.
- **`RateResult`** — `Allowed`, `Limit`, `Remaining` (never negative), `RetryAfter` (zero when allowed).
- **`RateLimiterOptions` / `RatePolicyBuilder`** — define named policies at startup:
  `AddPolicy("api", p => p.TokenBucket(100, TimeSpan.FromMinutes(1), burst: 20))`.
- **`Key`** — a helper for consistent key strings: `Key.ApiKey` / `Tenant` / `Ip` / `Route` /
  `Custom(name)`, `.Of(value)`, and `Key.Combine(...)`.
- **`AddOrionRate(...)`** — DI wiring: the family clock, a shared `RateDiagnostics`, and a singleton
  `IRateLimiter` over the configured policies.
- **OpenTelemetry by default** — `RateDiagnostics` on the family's `OrionInstrumentation` spine: a
  `Moongazing.OrionRate` meter with `orion.rate.allowed`, `orion.rate.throttled`, and
  `orion.rate.remaining`, tagged by policy (keys are not tagged — unbounded cardinality).
- Binds to `Orion.Abstractions` 1.2.0 and `OrionClock` 0.9.0. Multi-targets
  `net8.0`/`net9.0`/`net10.0`; `IsAotCompatible`; a NativeAOT publish smoke test in CI.

### Scope

Wave 1 is the in-memory core. Deliberately deferred to later waves: a Redis distributed store (so
limits are correct across replicas), the ASP.NET Core middleware with `RateLimit-*` headers and
request-driven `KeyBy` resolvers, and `OrionLedger`-sourced per-API-key quotas.

### Verified

- Exit criteria met: the AOT smoke publishes trim/AOT-clean under `-warnaserror` and exits 0; a test
  drains a 100/min bucket under a fake clock, asserts request 101 is throttled with a 0.6s
  `RetryAfter`, and that advancing the clock 60s refills it. 13 tests green across
  `net8.0`/`net9.0`/`net10.0`, including a 500-way concurrent race that admits exactly the bucket size.
