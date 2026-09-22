<!-- markdownlint-disable MD024 -->

# Changelog

All notable changes to OrionRate are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- **A cost above the policy's capacity is rejected instead of promised an impossible retry**
  (**breaking**) — `AcquireAsync("api", key, permits: 50)` against a 5-token bucket (or a 5-slot
  window) used to return a throttle carrying a finite `RetryAfter`; waiting it out and retrying was
  rejected again, forever, because the bucket caps at capacity. Both policies now throw
  `ArgumentOutOfRangeException` when `permits` exceeds capacity (`permit + burst` for the token
  bucket, `permit` for the sliding window), matching `System.Threading.RateLimiting`. A rejection is
  still data — this is a caller bug, not a limit being hit.
- **Token-bucket `RetryAfter` no longer lands a tick early** — the wait was computed with
  `TimeSpan.FromSeconds`, which truncates toward zero, so a caller that honoured the advertised
  instant arrived before the token existed and was rejected a second time. Once the remaining
  shortfall fell under half a tick the advertised wait truncated to `TimeSpan.Zero`, turning a
  throttle into a busy-spin. The shortfall is now rounded up to the next tick, and an over-long wait
  saturates at `TimeSpan.MaxValue` instead of overflowing.
- **Token-bucket refill is computed from a stable anchor** — the bucket re-stamped its refill
  timestamp on every call, including calls that credited or consumed nothing. Two consequences:
  a rate slower than one permit per poll (e.g. 1 per 10s, polled once a second) accumulated its
  sub-permit credits through repeated floating-point addition and never reached a whole token, so the
  bucket refilled *nothing*; and a clock stepped backwards (NTP) re-anchored onto the rewound instant,
  so the correction back to real time read as a full period of refill and handed out a whole bucket.
  Tokens are now derived from `(tokens at anchor, anchor, now)` in one step, and the anchor only ever
  moves forward.
- **Sliding-window `RetryAfter` covers every slot the request needs** — it reported when the *oldest*
  in-window request ages out, which frees exactly one slot. A caller asking for more than one permit
  was sent back too early and rejected again (a 3-permit request on a full 5-slot window was told to
  wait 5s when it needed 7s). It now reports when the last slot the request actually needs comes free.
- **Idle partitions are evicted** — the limiter kept one entry per `(policy, key)` for the lifetime
  of the process. Keys are API keys, tenants and client IPs, so the key space belongs to the caller:
  a rotating key was an unbounded memory leak an attacker could drive. Partitions whose state has
  decayed back to a brand-new key's — a bucket refilled to capacity, a window with nothing left in it
  — are now swept off the acquire path, at most once a minute and only once the map is worth walking.
  `RateLimitPolicy.IsIdle` is the opt-in; it defaults to `false`, so a custom policy keeps the old
  behaviour until it overrides it.

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
