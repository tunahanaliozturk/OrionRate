// NativeAOT smoke test. Publishing this with PublishAot=true must produce zero trim/AOT warnings,
// and running it must exit 0 - that pair is OrionRate's AOT exit criterion. Assertions are runtime
// checks, not a test framework, so the point is to prove these paths survive trimming.
using Moongazing.OrionClock;
using Moongazing.OrionRate;
using Moongazing.OrionRate.Diagnostics;

var clock = new OrionClock();

using var diagnostics = new RateDiagnostics();
Check(diagnostics.Meter.Name == RateDiagnostics.MeterName, "meter name wrong");

var options = new RateLimiterOptions();
options.AddPolicy("api", p => p.TokenBucket(permit: 3, per: TimeSpan.FromMinutes(1)));
options.AddPolicy("login", p => p.SlidingWindow(permit: 2, window: TimeSpan.FromMinutes(15)));
var limiter = RateLimiter.Create(options, clock, diagnostics);

// Token bucket: capacity 3, so the fourth immediate request is throttled with a retry-after.
var key = Key.Tenant.Of("acme");
for (var i = 0; i < 3; i++)
{
    Check((await limiter.AcquireAsync("api", key)).Allowed, $"token-bucket request {i + 1} should be allowed");
}
var throttled = await limiter.AcquireAsync("api", key);
Check(!throttled.Allowed && throttled.RetryAfter > TimeSpan.Zero, "token-bucket 4th should be throttled with a retry-after");

// Sliding window: capacity 2 for a composed key.
var composed = Key.Combine(Key.Tenant.Of("acme"), Key.Route.Of("/auth/login"));
Check((await limiter.AcquireAsync("login", composed)).Allowed, "sliding-window request 1");
Check((await limiter.AcquireAsync("login", composed)).Allowed, "sliding-window request 2");
Check(!(await limiter.AcquireAsync("login", composed)).Allowed, "sliding-window request 3 should be throttled");

Console.WriteLine("OrionRate AOT smoke test passed.");
return 0;

static void Check(bool condition, string message)
{
    if (!condition)
    {
        Console.Error.WriteLine($"AOT smoke test failed: {message}");
        Environment.Exit(1);
    }
}
