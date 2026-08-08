namespace Moongazing.OrionRate.DependencyInjection;

using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Moongazing.Orion.Abstractions.Time;
using Moongazing.OrionClock;
using Moongazing.OrionRate.Diagnostics;

/// <summary>
/// DI wiring for the rate limiter.
/// </summary>
public static class OrionRateServiceCollectionExtensions
{
    /// <summary>
    /// Register the in-memory rate limiter and the policies configured in <paramref name="configure"/>.
    /// Registers the family clock and a shared <see cref="RateDiagnostics"/>, and exposes a singleton
    /// <see cref="IRateLimiter"/>. Policies are defined once here at startup.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Defines the named policies via <see cref="RateLimiterOptions.AddPolicy"/>.</param>
    /// <returns>The same <paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddOrionRate(this IServiceCollection services, Action<RateLimiterOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        // Registers OrionClock as TimeProvider AND IOrionClock via TryAdd, so a consumer override wins.
        services.AddOrionClock();

        var options = new RateLimiterOptions();
        configure(options);
        var policies = options.Build();

        services.TryAddSingleton<RateDiagnostics>();
        services.TryAddSingleton<IRateLimiter>(sp =>
            new RateLimiter(policies, sp.GetRequiredService<IOrionClock>(), sp.GetRequiredService<RateDiagnostics>()));

        return services;
    }
}
