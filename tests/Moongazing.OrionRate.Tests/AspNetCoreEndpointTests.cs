namespace Moongazing.OrionRate.Tests;

using System.Globalization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Moongazing.OrionRate.AspNetCore;
using Moongazing.OrionRate.DependencyInjection;

using Xunit;

public sealed class AspNetCoreEndpointTests
{
    [Fact]
    public async Task Real_endpoint_returns_429_after_the_first_permit()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddOrionRate(options => options.AddPolicy("login", policy =>
            policy.SlidingWindow(1, TimeSpan.FromMinutes(1))));
        await using var app = builder.Build();
        app.MapGet("/login", () => "admitted")
            .RequireOrionRateLimit("login", _ => Key.Tenant.Of("acme"));
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var allowed = await client.GetAsync("/login");
        using var denied = await client.GetAsync("/login");

        Assert.Equal(System.Net.HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal("admitted", await allowed.Content.ReadAsStringAsync());
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, denied.StatusCode);
        Assert.Equal("1", denied.Headers.GetValues("RateLimit-Limit").Single());
        Assert.Equal("0", denied.Headers.GetValues("RateLimit-Remaining").Single());
        Assert.True(long.Parse(denied.Headers.GetValues("Retry-After").Single(), CultureInfo.InvariantCulture) >= 1);
    }

    [Fact]
    public async Task Allowed_request_reaches_handler_and_emits_budget_headers()
    {
        var limiter = new RecordingLimiter(RateResult.Allow(100, 99));
        using var services = new ServiceCollection().AddLogging().AddSingleton<IRateLimiter>(limiter).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Items["tenant"] = "acme";
        using var abort = new CancellationTokenSource();
        http.RequestAborted = abort.Token;
        var reached = false;

        var result = await OrionRateEndpointExtensions.InvokeAsync(
            EndpointFilterInvocationContext.Create(http),
            _ => { reached = true; return ValueTask.FromResult<object?>("ok"); },
            "api", ctx => Key.Tenant.Of((string)ctx.Items["tenant"]!), 2);

        Assert.True(reached);
        Assert.Equal("ok", result);
        Assert.Equal("api", limiter.Policy);
        Assert.Equal(Key.Tenant.Of("acme"), limiter.Key);
        Assert.Equal(2, limiter.Permits);
        Assert.Equal(abort.Token, limiter.CancellationToken);
        Assert.Equal("100", http.Response.Headers["RateLimit-Limit"]);
        Assert.Equal("99", http.Response.Headers["RateLimit-Remaining"]);
        Assert.False(http.Response.Headers.ContainsKey("Retry-After"));
    }

    [Fact]
    public async Task Throttled_request_skips_handler_and_returns_problem_with_rounded_retry()
    {
        var limiter = new RecordingLimiter(RateResult.Throttle(5, 0, TimeSpan.FromMilliseconds(1500)));
        using var services = new ServiceCollection().AddLogging().AddSingleton<IRateLimiter>(limiter).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Response.Body = new MemoryStream();
        var reached = false;

        var result = await OrionRateEndpointExtensions.InvokeAsync(
            EndpointFilterInvocationContext.Create(http),
            _ => { reached = true; return ValueTask.FromResult<object?>("unreachable"); },
            "login", _ => Key.Tenant.Of("acme"), 1);

        Assert.False(reached);
        Assert.Equal("2", http.Response.Headers.RetryAfter);
        Assert.Equal("5", http.Response.Headers["RateLimit-Limit"]);
        Assert.Equal("0", http.Response.Headers["RateLimit-Remaining"]);
        var problem = Assert.IsAssignableFrom<IResult>(result);
        await problem.ExecuteAsync(http);
        Assert.Equal(StatusCodes.Status429TooManyRequests, http.Response.StatusCode);
        http.Response.Body.Position = 0;
        using var reader = new StreamReader(http.Response.Body);
        var body = await reader.ReadToEndAsync();
        Assert.Contains("Too Many Requests", body, StringComparison.Ordinal);
        Assert.DoesNotContain("acme", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_key_is_rejected_before_any_budget_is_spent()
    {
        var limiter = new RecordingLimiter(RateResult.Allow(1, 0));
        using var services = new ServiceCollection().AddSingleton<IRateLimiter>(limiter).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await OrionRateEndpointExtensions.InvokeAsync(
                EndpointFilterInvocationContext.Create(http),
                _ => ValueTask.FromResult<object?>("unreachable"),
                "api", _ => " ", 1));

        Assert.Equal(0, limiter.CallCount);
    }

    [Fact]
    public void Route_and_group_extensions_validate_configuration_at_registration()
    {
        using var app = WebApplication.Create();
        var endpoint = app.MapGet("/a", () => "ok");
        var group = app.MapGroup("/group");
        Assert.Same(endpoint, endpoint.RequireOrionRateLimit("api", _ => "tenant:acme"));
        Assert.Same(group, group.RequireOrionRateLimit("api", _ => "tenant:acme"));
        Assert.Throws<ArgumentException>(() => endpoint.RequireOrionRateLimit(" ", _ => "x"));
        Assert.Throws<ArgumentNullException>(() => endpoint.RequireOrionRateLimit("api", null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => endpoint.RequireOrionRateLimit("api", _ => "x", 0));
    }

    [Fact]
    public async Task Retry_after_is_invariant_and_never_zero_when_throttled()
    {
        var limiter = new RecordingLimiter(RateResult.Throttle(1, 0, TimeSpan.Zero));
        using var services = new ServiceCollection().AddSingleton<IRateLimiter>(limiter).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            await OrionRateEndpointExtensions.InvokeAsync(
                EndpointFilterInvocationContext.Create(http),
                _ => ValueTask.FromResult<object?>("unreachable"),
                "api", _ => "tenant:acme", 1);
            Assert.Equal("1", http.Response.Headers.RetryAfter);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private sealed class RecordingLimiter(RateResult result) : IRateLimiter
    {
        public int CallCount { get; private set; }

        public string? Policy { get; private set; }

        public string? Key { get; private set; }

        public int Permits { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public ValueTask<RateResult> AcquireAsync(string policy, string key, int permits = 1, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Policy = policy;
            Key = key;
            Permits = permits;
            CancellationToken = cancellationToken;
            return ValueTask.FromResult(result);
        }
    }
}
