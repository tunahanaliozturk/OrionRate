# OrionRate.AspNetCore

Minimal API endpoint and route-group rate limiting backed by OrionRate. `RequireOrionRateLimit` asks the limiter before the endpoint runs and answers `429 Too Many Requests` with `Retry-After` when the budget is spent.

![RequireOrionRateLimit: the key selector picks an identity, a blank key throws, the limiter decides, RateLimit headers are set on both outcomes, an allowed request runs the endpoint and a throttled one gets 429 with Retry-After](https://raw.githubusercontent.com/tunahanaliozturk/OrionRate/master/docs/diagrams/endpoint-filter.png)

## Install

    dotnet add package OrionRate.AspNetCore

Plugs into `OrionRate` (installed with it): register the policies with `AddOrionRate`. Targets `net8.0`, `net9.0` and `net10.0`.

## Quick start

```csharp
using Moongazing.OrionRate;
using Moongazing.OrionRate.AspNetCore;
using Moongazing.OrionRate.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOrionRate(options =>
    options.AddPolicy("api", p => p.SlidingWindow(100, TimeSpan.FromMinutes(1))));

var app = builder.Build();
app.MapGet("/orders", () => Results.Ok())
    .RequireAuthorization() // configure authorization to require a validated tenant_id claim
    .RequireOrionRateLimit("api", context => Key.Tenant.Of(
        context.User.FindFirst("tenant_id")?.Value
            ?? throw new InvalidOperationException("Authenticated tenant_id claim required")));
```

The filter works the same on a route group: `app.MapGroup("/v1").RequireOrionRateLimit(...)`. The optional `permits` argument (default 1) is what each request costs.

## Responses

| Outcome | What happens |
|---------|--------------|
| Allowed | The endpoint runs. `RateLimit-Limit` and `RateLimit-Remaining` headers are set. |
| Throttled | Problem-details `429` with title "Too Many Requests"; `Retry-After` is the limiter's `RetryAfter` rounded up to whole seconds, at least 1; the `RateLimit-*` headers are set. The endpoint does not run. |
| Blank key | The filter throws `InvalidOperationException` (fails closed) instead of merging callers into one empty-key budget. The endpoint does not run. |

`IRateLimiter` is resolved from the request services on every request, and `HttpContext.RequestAborted` is passed to `AcquireAsync`. A policy name that was never registered surfaces as `KeyNotFoundException` when a request arrives.

## Keys and deployment

- Build keys from authenticated, normalized identities (a validated tenant or API-key id). Do not trust a client-supplied identity header.
- The in-memory limiter keeps one budget per process: three replicas admit up to three times the limit.
- Tested on all three target frameworks; this package makes no NativeAOT claim yet (the `OrionRate` core does).

## Related packages

- `OrionRate` - the limiter, policies, `RateResult` and the `Key` helper this filter uses.
- `OrionClock.Testing` - `FakeOrionClock`, to fast-forward limits in integration tests.

## Links

- Documentation and full README: https://github.com/tunahanaliozturk/OrionRate
- Changelog: https://github.com/tunahanaliozturk/OrionRate/blob/master/CHANGELOG.md
- License: MIT
