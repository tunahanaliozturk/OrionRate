namespace Moongazing.OrionRate.AspNetCore;

using System.Globalization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Applies an OrionRate policy to Minimal API endpoints or route groups.</summary>
public static class OrionRateEndpointExtensions
{
    /// <summary>
    /// Rate-limit an endpoint or route group using a trusted, application-defined identity.
    /// Register the policy and limiter with <c>AddOrionRate</c> before mapping endpoints.
    /// The in-memory limiter's budget is per process, not shared between replicas.
    /// </summary>
    /// <param name="builder">The endpoint or route group.</param>
    /// <param name="policy">A policy registered with <c>AddOrionRate</c>.</param>
    /// <param name="keySelector">Selects a nonempty key from a trusted, normalized identity.</param>
    /// <param name="permits">The positive number of permits consumed by each request.</param>
    /// <returns>The same builder for chaining.</returns>
    public static TBuilder RequireOrionRateLimit<TBuilder>(
        this TBuilder builder,
        string policy,
        Func<HttpContext, string> keySelector,
        int permits = 1)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(policy);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permits);

        builder.AddEndpointFilter((context, next) => InvokeAsync(context, next, policy, keySelector, permits));
        return builder;
    }

    internal static async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next,
        string policy,
        Func<HttpContext, string> keySelector,
        int permits)
    {
        var http = context.HttpContext;
        var key = keySelector(http);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("The OrionRate key selector returned an empty identity.");
        }

        var limiter = http.RequestServices.GetRequiredService<IRateLimiter>();
        var decision = await limiter.AcquireAsync(policy, key, permits, http.RequestAborted).ConfigureAwait(false);

        http.Response.Headers["RateLimit-Limit"] = decision.Limit.ToString(CultureInfo.InvariantCulture);
        http.Response.Headers["RateLimit-Remaining"] = decision.Remaining.ToString(CultureInfo.InvariantCulture);

        if (decision.Allowed)
        {
            return await next(context).ConfigureAwait(false);
        }

        var ticks = decision.RetryAfter.Ticks;
        var seconds = ticks / TimeSpan.TicksPerSecond;
        if (ticks % TimeSpan.TicksPerSecond != 0)
        {
            seconds++;
        }

        http.Response.Headers.RetryAfter = Math.Max(1, seconds).ToString(CultureInfo.InvariantCulture);
        return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too Many Requests");
    }
}
